using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

// The em dash (U+2014) is the sentinel for "this source reported nothing", as opposed to a
// source that reported zero. Like every glyph in a metric row it is East Asian Ambiguous,
// so it takes one column there, as Vis() assumes. It is never wider than the value it
// replaces, so it can't widen a column. A const so the static local functions can use it.
const string NoData = "—";

// #28A428, forest green: the → time when the row's own reset comes before 100% would. At
// this pace the window can't run out, so it gets a calm colour instead of red.
// It is X11 forestgreen #228B22 (hue 120, saturation 61%) with the lightness raised from 34%
// to 40%. #228B22 itself is only 4,10:1 on the docs' #16161E and 3,54:1 where the 10%
// wallpaper is lightest; 40% is the first step that reaches 4,5:1 on every ground: 5,99 on
// Campbell #0C0C0C, 4,75 on the lightest wallpaper #242424, 5,51 on #16161E. It also has to
// read as a stronger green than ↻'s #A6E3A1 and the reset time's #C6F6C1, not as a third
// pastel: chroma 76 against 41, and a CIEDE2000 difference of 21,7 and 27,3 from them.
const string Forest = "\x1b[38;2;40;164;40m";

// The registry key under HKCU that holds what every session shares: the limit rows' cache, the
// count of failed fetches, the prediction history and the euro rate.
const string RegKey = @"Software\StatusAI";

string stdin;
using (var s = Console.OpenStandardInput())
using (var r = new StreamReader(s, Encoding.UTF8)) stdin = r.ReadToEnd();

string cshipOut = RunCship(stdin);

// A missing source and a real zero are the same number, so every value that could render
// as 0 has a flag saying whether it was there. Nothing is inferred from the value itself:
// cost 0 on a fresh session is real and must still render as $0,00.
double costUsd = 0; long durMs = 0; int add = 0, del = 0;
bool haveCost = false, haveDur = false, haveCtx = false, noMessagesYet = false;
string transcriptPath = "";
string stdinErr = "";
try {
    using var d = JsonDocument.Parse(stdin);
    if (d.RootElement.TryGetProperty("cost", out var c) && c.ValueKind == JsonValueKind.Object) {
        if (c.TryGetProperty("total_cost_usd", out var v1) && v1.ValueKind == JsonValueKind.Number) { costUsd = v1.GetDouble(); haveCost = true; }
        if (c.TryGetProperty("total_duration_ms", out var v2) && v2.ValueKind == JsonValueKind.Number) { durMs = v2.GetInt64(); haveDur = true; }
        if (c.TryGetProperty("total_lines_added", out var v3) && v3.ValueKind == JsonValueKind.Number) add = v3.GetInt32();
        if (c.TryGetProperty("total_lines_removed", out var v4) && v4.ValueKind == JsonValueKind.Number) del = v4.GetInt32();
    }
    // Read only to tell "the field is missing" from "the field says 0". cship draws the
    // context bar itself, from the same payload.
    //
    // null is neither. Until the first API response of a context (every new session, and
    // again after /clear) Claude Code sends used_percentage and current_usage as null, its
    // documented "no messages yet". Both come from one lookup of the last response's usage
    // (in the Claude Code 2.1.234 and 2.1.280 bundles), so they are null together or not at
    // all, and that pair means "nothing yet", like the cost of 0 above. Any other shape is
    // still a missing source: no context_window, no used_percentage, a null next to a
    // current_usage that holds a measurement, or a value that is not a number.
    if (d.RootElement.TryGetProperty("context_window", out var cw) && cw.ValueKind == JsonValueKind.Object
        && cw.TryGetProperty("used_percentage", out var up)) {
        if (up.ValueKind == JsonValueKind.Number) haveCtx = true;
        else noMessagesYet = up.ValueKind == JsonValueKind.Null
            && cw.TryGetProperty("current_usage", out var cu) && cu.ValueKind == JsonValueKind.Null;
    }
    if (d.RootElement.TryGetProperty("transcript_path", out var tp) && tp.ValueKind == JsonValueKind.String)
        transcriptPath = tp.GetString() ?? "";
} catch (Exception ex) {
    // this used to be a bare catch, and every downstream zero then looked like a real zero
    stdinErr = "payload unreadable — " + ex.GetType().Name;
}

// The columns the block may use, measured once: see Avail().
int avail = Avail();

var (usage, usageErr, fetchErr) = GetUsage();
string metaLine = BuildMeta(costUsd, durMs, add, del, EurPerUsd(), haveCost, haveDur);
var (acctLine, signedIn) = BuildAccount();
// A session a few seconds old may not have its transcript yet. That is a race, not a
// fault, so BuildTokens stays quiet about a missing file for the first 30 s. The 30 s
// matches the meta segment: BuildMeta rounds the duration to whole minutes, so ⏱ shows 0m
// for as long as this applies. A payload with no duration doesn't count as young; that is
// a failed source of its own and doesn't excuse a missing transcript.
//
// 30 s alone is not enough, though. Claude Code writes the transcript at the first prompt,
// not at session start (a transcript's creation time is its first user record, on 2.1.233,
// 2.1.234 and 2.1.275 to 2.1.280), so a session left open for over 30 s before anyone
// types has no file, and used to show red for it. While the payload says no messages yet,
// a missing file is expected at any age, with or without a duration. Once a context has
// been measured the file should exist, and after 30 s a missing one is reported again.
bool youngSession = haveDur && durMs <= 30000;
var (tokenLines, tokenErr) = BuildTokens(transcriptPath, youngSession || noMessagesYet, avail);

// A ⚠ reason for every source that failed, so a blank figure always comes with a reason.
// A source that reported a real zero adds nothing here.
var warns = new List<string>();
if (stdinErr.Length > 0) warns.Add(stdinErr);
if (usageErr.Length > 0) warns.Add(usageErr);
// A usage fetch that has failed twice in a row. With nobody signed in there is no token to fetch
// with and no limit rows to miss, and the account line already says so, so it stays quiet then.
if (fetchErr.Length > 0 && signedIn) warns.Add(fetchErr);
if (stdinErr.Length == 0) {
    if (!haveCost && !haveDur) warns.Add("cost, duration — no cost block in the payload");
    else if (!haveCost) warns.Add("cost — no cost.total_cost_usd in the payload");
    else if (!haveDur) warns.Add("duration — no cost.total_duration_ms in the payload");
    // cship draws the context bar from this field. Without it, cship draws an empty bar at
    // 0%: the same glyphs as a context that really is empty, told apart only by colour (a
    // number takes the bar's style, anything else the default foreground). The bar isn't
    // ours to change, so a warning is all we can do. "No messages yet" is a context that
    // really is empty: nothing has been measured, so 0% is correct and there is no warning.
    if (!haveCtx && !noMessagesYet) warns.Add("context — no context_window.used_percentage; the 0% bar is not real");
}
if (tokenErr.Length > 0) warns.Add(tokenErr);

// Find the host line before the ⚠ rows are laid out, so a missing one can add its own
// reason. idx < 0 means every line cship returned was blank, which is what cship does with
// a payload it can't parse, and that is when a diagnostic is worth the most.
var lines = new List<string>(cshipOut.Replace("\r\n", "\n").Split('\n'));
int idx = -1;
for (int i = lines.Count - 1; i >= 0; i--) if (lines[i].Trim().Length > 0) { idx = i; break; }
if (idx < 0) warns.Add("cship — no output; its prompt and model lines are missing");

var warnLines = WarnRows(warns, avail);
// A meter the usage API sent that this status line does not draw is a notice to review the
// code, not a failed source: a row of its own, in amber.
if (MetersRow(usage.Ig, avail) is { Length: > 0 } metersRow) warnLines.Add(metersRow);
// Being on credit is not a failed source but an alarm, so it never shares a row with one:
// its own row, last, under every other reason.
if (usage.Cr.Length > 0) warnLines.Add(AlarmRow(usage.Cr, avail));

// Every row of the block starts with a single-width glyph (the │ rule on the token rows,
// 5h/7d on the metric rows), so one indent serves them all and column 1 lines up on every
// line. The breakdown belongs to an account, so with nobody signed in it is left out. The
// limit rows are drawn here, at this session's width, from the figures the fetch cached.
var block = new List<string>(tokenLines);
block.AddRange(Compose(RenderRows(usage.Rows, 2, avail), acctLine, signedIn ? usage.Bd : "", warnLines, avail));
if (idx >= 0) {
    lines[idx] = "\x1b[0m" + lines[idx];
    if (metaLine.Length > 0) lines[idx] += "   " + metaLine;
    lines.InsertRange(idx + 1, block.Select(l => "\x1b[0m " + l));
} else {
    // No host line to insert into. The block is still worth printing on its own, because
    // almost none of it comes from stdin: the limit rows come from the registry cache and
    // the API, the account line from the account files. Only the token rows and the meta
    // figures are lost, and each of those already falls back to a sentinel with a reason.
    // This used to skip the insert and print nothing at all, which looks the same as the
    // binary being uninstalled or dead.
    //
    // The meta segment normally sits on the host line, so here it gets the first row of the
    // block, opened by NoData: the host line is a source that reported nothing. NoData is
    // single-width, so the row still starts on column 1, and indent + marker + space costs
    // 3 of the 137 columns next to the meta segment's measured worst case of 130.
    lines.Clear();
    if (metaLine.Length > 0)
        lines.Add("\x1b[0m \x1b[38;2;110;115;141m" + NoData + "\x1b[0m " + metaLine);
    lines.AddRange(block.Select(l => "\x1b[0m " + l));
}
lines.RemoveAll(l => l.Trim().Length == 0);
using (var os = Console.OpenStandardOutput())
using (var w = new StreamWriter(os, new UTF8Encoding(false))) w.Write(string.Join("\n", lines));
return;

// Two columns when the terminal has room: the account sits right of 5h and the scoped
// row right of 7d. The two left-column rows render to one visible width, so a fixed gap
// starts the right column at the same place on both lines; the right-hand row is as wide
// as its own values need and nothing lines up after it. Falls back to stacked when too
// narrow.
//
// The product breakdown takes no part in that decision: it rides after the account only in
// whatever room the layout leaves, so it can never push the rows into the stacked layout.
static List<string> Compose(string usageRows, string acct, string bd, List<string> errs, int avail) {
    const int gap = 2;
    var rows = usageRows.Length > 0 ? new List<string>(usageRows.Split('\n')) : new List<string>();
    var block = new List<string>();
    if (rows.Count >= 2) {
        int rowW = Vis(rows[0]);
        int rightW = Math.Max(rows.Count > 2 ? Vis(rows[2]) : 0, acct.Length > 0 ? Vis(acct) + 1 : 0);
        if (1 + rowW + gap + rightW <= avail) {
            string pad = new string(' ', gap);
            string right0 = acct.Length > 0 ? WithBreakdown(acct, bd, avail - (1 + rowW + gap)) : "";
            string right1 = rows.Count > 2 ? rows[2] : "";
            block.Add(rows[0] + (right0.Length > 0 ? pad + right0 : ""));
            block.Add(rows[1] + (right1.Length > 0 ? pad + right1 : ""));
            for (int i = 3; i < rows.Count; i++) block.Add(rows[i]);
            block.AddRange(errs);
            return block;
        }
    }
    block.AddRange(rows);
    if (acct.Length > 0) block.Add(WithBreakdown(acct, bd, avail - 1));
    block.AddRange(errs);
    return block;
}

// The product breakdown after the account, in whole entries or not at all: every entry if
// they all fit, else without the 0% ones, else none. Never wrapped, never cut inside an
// entry. `room` is what the line has left for the account, which is charged the way
// Compose charges it (Vis + 1), so the line passes the same test the layout was chosen by.
static string WithBreakdown(string acct, string bd, int room) {
    if (bd.Length == 0) return acct;
    const string dim = "\x1b[38;2;110;115;141m", txt = "\x1b[38;2;169;177;214m", rst = "\x1b[0m";
    var all = new List<(string label, string pct)>();
    foreach (var part in bd.Split(';', StringSplitOptions.RemoveEmptyEntries)) {
        int k = part.LastIndexOf('=');
        if (k > 0) all.Add((part.Substring(0, k), part.Substring(k + 1)));
    }
    string sep = $" {dim}·{rst} ";
    foreach (bool zeros in new[] { true, false }) {
        var shown = all.Where(e => zeros || e.pct != "0").ToList();
        if (shown.Count == 0) continue;
        string tail = sep + string.Join(sep, shown.Select(e => $"{txt}{e.label} {e.pct}%{rst}"));
        if (Vis(acct) + 1 + Vis(tail) <= room) return acct + tail;
    }
    return acct;
}

// visible width: strip SGR escapes; every glyph used in a metric row is single-width
static int Vis(string s) {
    int n = 0;
    for (int i = 0; i < s.Length; i++) {
        if (s[i] == '\x1b') { while (i < s.Length && s[i] != 'm') i++; continue; }
        n++;
    }
    return n;
}

// The terminal's width. STATUSAI_WIDTH first, a fixed width set by hand (settings.json "env");
// then COLUMNS, which Claude Code sets to its terminal's width when it runs the status line
// (since 2.1.153; the 2.1.281 bundle takes it from process.stdout); then 141, a measured
// width. Asking the OS is useless: Claude Code spawns the status line detached, and the
// console it gets reports a phantom 120x30. A value that is not a whole number over 40 is
// passed over for the next.
static int TermWidth() {
    foreach (string name in new[] { "STATUSAI_WIDTH", "COLUMNS" })
        if (int.TryParse(Environment.GetEnvironmentVariable(name), out int w) && w > 40) return w;
    return 141;
}

// The columns the block may use. Claude Code draws the status line in its prompt footer, a row
// as wide as the terminal with two columns of padding on either side, and inside that indents
// it by statusLine.padding on either side. A line longer than what is left is cut short, not
// wrapped. The fullscreen renderer draws the same footer, full width under any side panel.
// (Read in the 2.1.281 bundle.) The floor only keeps a padding set absurdly wide from
// driving the arithmetic below negative.
static int Avail() => Math.Max(16, TermWidth() - 4 - 2 * Padding());

// statusLine.padding from the user's settings.json, which is where the install guide puts the
// statusLine entry. Claude Code merges project settings over it, which are not read here, so a
// padding set only in a project goes unseen. A fraction is rounded up, to never overflow.
static int Padding() {
    try {
        using var fs = File.OpenRead(Path.Combine(Home(), ".claude", "settings.json"));
        using var d = JsonDocument.Parse(fs, new JsonDocumentOptions {
            AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        if (d.RootElement.ValueKind == JsonValueKind.Object
            && d.RootElement.TryGetProperty("statusLine", out var sl) && sl.ValueKind == JsonValueKind.Object
            && sl.TryGetProperty("padding", out var p) && p.ValueKind == JsonValueKind.Number
            && p.TryGetDouble(out double v) && v > 0)
            return (int)Math.Ceiling(Math.Min(v, 1000));
    } catch { }
    return 0;
}

// STATUSAI_OFFLINE names a directory to render from instead of the live machine, for the dev
// loop. Everything the status line normally shares with the running sessions comes from a
// file in it instead: the limit rows from its usage.json and rows.json rather than the
// registry cache and the API, the euro rate from rows.json, and it replaces %USERPROFILE%,
// so the account line reads its .claude.json, statusLine.padding comes from its
// .claude/settings.json and the token cache goes in its AppData\Local (LocalAppData).
// HKCU\Software\StatusAI is neither read nor written, the usage lock is never taken and
// nothing is fetched. So a dev build run this way can't draw a live session's cached rows,
// count a failed fetch against them, or push a sample into the shared prediction history,
// all of which a plain test run on a live machine does.
static string? Offline() {
    string? d = Environment.GetEnvironmentVariable("STATUSAI_OFFLINE");
    return string.IsNullOrEmpty(d) ? null : d;
}

static string Home() => Offline() ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

// %LOCALAPPDATA%, where the token cache lives. Offline it is the AppData\Local of the directory
// that stands in for the user profile, which is where Windows keeps it by default.
static string LocalAppData() => Offline() is string d ? Path.Combine(d, "AppData", "Local")
                                : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

static string RunCship(string input) {
    try {
        var psi = new ProcessStartInfo {
            FileName = "cship", RedirectStandardInput = true, RedirectStandardOutput = true,
            UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false), StandardInputEncoding = new UTF8Encoding(false) };
        using var p = Process.Start(psi)!;
        p.StandardInput.Write(input); p.StandardInput.Close();
        string o = p.StandardOutput.ReadToEnd(); p.WaitForExit();
        return o;
    } catch { return input; }
}

// Single-flight: the named mutex serializes the whole check-fetch-write section across
// processes. Losers draw the last cached rows instead of waiting on the network.
// The lock name is scoped per user + elevation level, so an ACL on a mutex created by a
// different security context can never deny us access. If the lock still fails, fetching
// is suspended and a loud error line is rendered. Never an unguarded parallel fetch: that
// would corrupt the prediction history.
//
// Besides the rows and a lock failure it returns the reason for a fetch that has failed
// twice in a row or more (FetchWarn), read from the shared state, so every session shows the
// same row whether or not it was the one that fetched. A fresh cache means the last fetch
// worked, so it comes with none. A failed fetch leaves the cache stale, so the next render
// to take the lock is the retry. Once the ⚠ row shows, MayRetry spaces the retries 50 s
// apart, and a render in between draws the saved rows and the warning at once, without taking
// the lock. There is no second attempt inside one render, which would double the 3 s a render
// can already spend waiting.
static (Cached c, string err, string fetchErr) GetUsage() {
    if (Offline() is string dir) return OfflineRows(dir);
    long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    if (FreshVal(now) is Cached fresh) return (fresh, "", "");
    if (!MayRetry(RegInt("fail"), RegLong("tryTs"), now)) return Saved(now);
    Mutex? mx = null; bool owned = false;
    try {
        mx = new Mutex(false, LockName());
        try { owned = mx.WaitOne(0); } catch (AbandonedMutexException) { owned = true; }
    } catch (Exception ex) {
        mx?.Dispose();
        return (AnyVal() ?? Cached.None, LockError(ex), "");
    }
    try {
        // both checked again under the lock: another session may have fetched or tried since
        if (owned) {
            now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (FreshVal(now) is Cached fresh2) return (fresh2, "", "");
            if (MayRetry(RegInt("fail"), RegLong("tryTs"), now) && FetchAndSave(now) is Cached got)
                return (got, "", "");
        }
        // another session holds the lock or has just tried, or this fetch failed
        return Saved(now);
    } finally {
        if (owned) { try { mx!.ReleaseMutex(); } catch { } }
        mx?.Dispose();
    }
}

// A render without a fetch of its own: the last good rows, and the ⚠ reason once two fetches in
// a row have failed
static (Cached c, string err, string fetchErr) Saved(long now) {
    var last = AnyVal();
    return (last ?? Cached.None, "", FetchWarn(RegInt("fail"), RegStr("why"),
                                               last is null ? 0 : RegLong("ts"), now));
}

// Whether a render may try the fetch: always until it has failed twice in a row, and from then
// on, with the ⚠ row showing, only once the last attempt (tryTs) is 50 s old, so that the open
// sessions between them retry once every 50 s instead of at every render. An attempt dated
// after now, as when the clock has been set back, holds nothing off.
static bool MayRetry(int fails, long triedAt, long now) =>
    fails < 2 || now - triedAt >= 50 || triedAt > now;

// What GetUsage returns, read from files instead of the machine. Two fixture shapes.
//
// usage.json beside rows.json: a usage response, run through the same ParseUsage as a live
// fetch at rows.json's "now", so the hours to each reset are fixed. The forecast inputs a
// live fetch takes from the history are given in rows.json by label. Which meters are drawn
// and which are flagged is the same Known() decision a live fetch makes, with "sn" as the
// scoped meter already being followed; a drawn meter the forecast leaves out is gated.
//
//   { "fx": 0.876, "now": "2026-09-23T20:14:20Z", "sn": "Fable",
//     "forecast": { "5h": { "rate": 28.8, "gated": false }, ... } }
//
// rows.json alone: the list RenderRows is given, which is the output of the fetch, the
// window checks and the slope. No breakdown, no credit state and no ignored meters:
//
//   { "fx": 0.876,
//     "rows": [ { "label": "5h", "pct": 22, "hrs": 3.38, "hasReset": true,
//                 "rate": 10.4, "gated": false, "sev": "normal" }, ... ] }
//
// Either shape can carry a "fetch", standing in for the shared state and for the fetch this
// render makes. "fails" and "why" are the failures in a row so far and the latest reason, as
// the registry's fail and why hold them. "attempt" is this render's fetch: "ok", the
// default, draws the response at "now"; "none" makes no attempt, as when another session
// holds the lock; anything else fails with that reason. "okAt" is when the last good fetch
// was made, the registry's ts, and "triedAt" when the fetch was last tried, its tryTs, which
// is 0 when not given. Where MayRetry holds the retry off, the render makes no attempt,
// whatever "attempt" says. A render without a good fetch of its own draws the rows as that
// fetch measured them, or none if the fixture has none to draw.
//
//   "fetch": { "fails": 1, "why": "timeout", "attempt": "timeout", "okAt": "2026-09-23T20:12:20Z" }
//
// Either way the forecast is an input, not computed from a history. This path is here to
// test the parsing, the drawing and the count of failed fetches: whether a render tries is
// the live code's MayRetry, the count moves by its AfterFetch and the reason is its
// FetchWarn. The rows also pass through the string the registry keeps them in, so RowsOut
// and RowsIn are under test too.
static (Cached c, string err, string fetchErr) OfflineRows(string dir) {
    try {
        using var d = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "rows.json")));
        var root = d.RootElement;
        var now = Time(root, "now") ?? DateTimeOffset.UtcNow;
        bool sim = root.TryGetProperty("fetch", out var fe) && fe.ValueKind == JsonValueKind.Object;
        int fails = sim && fe.TryGetProperty("fails", out var fl) && fl.ValueKind == JsonValueKind.Number ? fl.GetInt32() : 0;
        string why = sim ? Str(fe, "why") : "", attempt = sim ? Str(fe, "attempt") : "";
        if (attempt.Length == 0) attempt = "ok";
        var okAt = (sim ? Time(fe, "okAt") : null) ?? now;
        long triedAt = (sim ? Time(fe, "triedAt") : null)?.ToUnixTimeSeconds() ?? 0;
        if (!MayRetry(fails, triedAt, now.ToUnixTimeSeconds())) attempt = "none";
        bool ok = attempt == "ok";

        Cached? c = null;
        string usagePath = Path.Combine(dir, "usage.json");
        if (File.Exists(usagePath)) {
            using var ud = JsonDocument.Parse(File.ReadAllText(usagePath));
            var u = ParseUsage(ud.RootElement, ok ? now : okAt);
            if (u.Meters.Count == 0) return (Cached.None, "offline — usage.json has no meters", "");
            var (iS, iW, iF, ignored) = Known(u.Meters, Str(root, "sn"));
            var fc = root.TryGetProperty("forecast", out var f) && f.ValueKind == JsonValueKind.Object ? f : default;
            var rows = new List<RowIn>();
            foreach (var i in new[] { iS, iW, iF }) {
                if (i < 0) continue;
                var m = u.Meters[i];
                double rate = 0; bool gated = true;
                if (fc.ValueKind == JsonValueKind.Object && fc.TryGetProperty(m.Label, out var e)) {
                    rate = e.GetProperty("rate").GetDouble(); gated = e.GetProperty("gated").GetBoolean();
                }
                rows.Add(new RowIn(m.Label, m.Lim.Pct, m.Lim.Hours, m.Lim.HasReset, rate, gated, m.Lim.Sev));
            }
            c = new Cached(rows, u.Bd, u.Cr, string.Join("\n", ignored));
        } else if (root.TryGetProperty("rows", out var rs)) {
            var list = new List<RowIn>();
            foreach (var r in rs.EnumerateArray())
                list.Add(new RowIn(r.GetProperty("label").GetString() ?? "", r.GetProperty("pct").GetInt32(),
                                   r.GetProperty("hrs").GetDouble(), r.GetProperty("hasReset").GetBoolean(),
                                   r.GetProperty("rate").GetDouble(), r.GetProperty("gated").GetBoolean(),
                                   r.GetProperty("sev").GetString() ?? ""));
            if (list.Count == 0) return (Cached.None, "offline — rows.json lists no rows", "");
            c = new Cached(list, "", "", "");
        } else if (ok) return (Cached.None, "offline — no usage.json and no rows in rows.json", "");

        if (c is not null)
            c = c with { Rows = RowsIn(RowsOut(c.Rows)) ?? throw new FormatException("RowsIn refused what RowsOut wrote") };
        (fails, why) = AfterFetch(fails, why, attempt);
        return (c ?? Cached.None, "", FetchWarn(fails, why, c is null ? 0 : okAt.ToUnixTimeSeconds(),
                                                now.ToUnixTimeSeconds()));
    } catch (Exception ex) {
        // a broken fixture is loud, like every other failed source
        return (Cached.None, "offline — fixture unreadable: " + ex.GetType().Name + " " + Short(ex.Message), "");
    }
}

static DateTimeOffset? Time(JsonElement o, string name) =>
    o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
    && DateTimeOffset.TryParse(v.GetString(), out var t) ? t : null;

static double OfflineFx(string dir) {
    try {
        using var d = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "rows.json")));
        return d.RootElement.TryGetProperty("fx", out var f) && f.ValueKind == JsonValueKind.Number ? f.GetDouble() : 0;
    } catch { return 0; }
}

static string LockName() {
    using var id = WindowsIdentity.GetCurrent();
    string sid = id.User?.Value ?? "nosid";
    bool adm = new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    return @"Global\StatusAI.fetch." + sid + (adm ? ".adm" : ".std");
}

// Returns the bare reason; WarnRows does the ⚠ and the colour, so the lock failure shares
// one row with every other failed source rather than owning a line of its own.
static string LockError(Exception ex) {
    string why = ex switch {
        WaitHandleCannotBeOpenedException => "the lock name is occupied by a foreign kernel object that is not a mutex",
        UnauthorizedAccessException => "access to the usage lock was denied; it was created by a different security context",
        IOException => "the usage lock could not be opened due to an I/O error",
        _ => "unexpected " + ex.GetType().Name + " while opening the usage lock"
    };
    return "usage tracking suspended — " + why;
}

// A failed source must never look like a source that reported nothing, so every reason is
// stated. The reasons share one row, separated by ·, while they fit (a single reason then
// renders as the lock error always has), and get a row each once they don't: a wrapped
// status line costs the same height as a split one and reads far worse.
static List<string> WarnRows(List<string> reasons, int avail) {
    const string red = "\x1b[1;38;2;247;118;142m", rst = "\x1b[0m";
    var outp = new List<string>();
    if (reasons.Count == 0) return outp;
    string one = string.Join(" · ", reasons);
    int budget = avail - 2;   // 1 indent, 1 safety
    if (2 + one.Length <= budget) { outp.Add(red + "⚠ " + one + rst); return outp; }
    foreach (var r in reasons) outp.Add(red + "⚠ " + Short(r, budget - 2) + rst);
    return outp;
}

// The on-credit alarm: red like every ⚠ row and to the same budget, but always on a row of
// its own, never joined by ·. It is not a failed source; it is usage being billed that the
// plan should have covered.
static string AlarmRow(string reason, int avail) {
    const string red = "\x1b[1;38;2;247;118;142m", rst = "\x1b[0m";
    int budget = avail - 2;   // 1 indent, 1 safety
    return red + "⚠ " + Short(reason, budget - 2) + rst;
}

// The meters notice: a meter the usage API sent that this status line does not draw. Amber,
// because it asks for the code to be reviewed rather than reporting a failure or money; a
// row of its own to the ⚠ rows' budget. It names as many of the ignored meters as fit,
// whole, then counts the rest as "+N more"; if not even one name fits, it only counts them.
static string MetersRow(string ig, int avail) {
    const string amber = "\x1b[38;2;224;175;104m", rst = "\x1b[0m";
    var names = ig.Split('\n', StringSplitOptions.RemoveEmptyEntries);
    if (names.Length == 0) return "";
    int room = avail - 2 - 2;   // the ⚠ rows' budget, less "⚠ "
    string head = "meters — the usage API sent " + (names.Length == 1 ? "a meter" : names.Length + " meters")
                + " this status line ignores";
    const string tail = " · review StatusAI";
    for (int k = names.Length; k >= 1; k--) {
        string t = head + ": " + string.Join(", ", names.Take(k))
                 + (k < names.Length ? $" +{names.Length - k} more" : "") + tail;
        if (t.Length <= room) return amber + "⚠ " + t + rst;
    }
    return amber + "⚠ " + Short(head + tail, room) + rst;
}

// A reason has to fit on a status line. Exception text can carry a whole path (a runaway
// directory junction once produced a 33 KB one in testing, which tore the line apart), and
// a transcript_path is often over 100 characters. Head and tail are kept because a path is
// identified by its end and a message by its start. The ellipsis (U+2026) is East Asian
// Ambiguous, so it takes one column like every other glyph here.
static string Short(string s, int max = 64) {
    if (max < 16) max = 16;   // a guard for a caller with a tiny budget: Avail() floors at 16
    s = s.Replace('\n', ' ').Replace('\r', ' ');
    return s.Length <= max ? s : s.Substring(0, max - 13) + "…" + s.Substring(s.Length - 12);
}

// rows holds the figures of the rows the last good fetch produced (RowsOut), not drawn text,
// so every session draws them at its own width; bd, cr and ig are the breakdown, the credit
// alarm and the ignored meters the same fetch produced, cached beside them so that a cache
// hit draws everything a fetch would. A key without rows, as a build that cached drawn text
// in val left it, reads as no cache, so the next render fetches.
static Cached? FreshVal(long now) {
    try {
        using var rk = Registry.CurrentUser.OpenSubKey(RegKey);
        if (rk?.GetValue("ts") is string ts && long.TryParse(ts, out long t) && now - t < 50) return FromKey(rk);
    } catch { }
    return null;
}

static Cached? AnyVal() {
    try {
        using var rk = Registry.CurrentUser.OpenSubKey(RegKey);
        if (rk is not null) return FromKey(rk);
    } catch { }
    return null;
}

static Cached? FromKey(RegistryKey rk) =>
    rk.GetValue("rows") is string r && RowsIn(r) is List<RowIn> rows
        ? new Cached(rows, rk.GetValue("bd") as string ?? "", rk.GetValue("cr") as string ?? "",
                     rk.GetValue("ig") as string ?? "")
        : null;

// The rows as the registry's rows value holds them: one per line, seven fields to a line,
// separated by tabs. Labels and severities are cleaned of control characters first, so
// neither separator can turn up inside a field. The doubles are written to round-trip, so a
// cache hit draws exactly what the fetch would have drawn at the same width.
static string RowsOut(List<RowIn> rows) => string.Join("\n", rows.Select(r => string.Join("\t",
    Clean(r.Label), r.Pct.ToString(CultureInfo.InvariantCulture),
    r.Hrs.ToString("R", CultureInfo.InvariantCulture), r.HasReset ? "1" : "0",
    r.Rate.ToString("R", CultureInfo.InvariantCulture), r.Gated ? "1" : "0", Clean(r.Sev))));

// null for anything RowsOut did not write, which the callers take for no cache at all
static List<RowIn>? RowsIn(string s) {
    var rows = new List<RowIn>();
    foreach (string line in s.Split('\n', StringSplitOptions.RemoveEmptyEntries)) {
        var f = line.Split('\t');
        if (f.Length != 7
            || !int.TryParse(f[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int pct)
            || !double.TryParse(f[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double hrs)
            || !double.TryParse(f[4], NumberStyles.Float, CultureInfo.InvariantCulture, out double rate))
            return null;
        rows.Add(new RowIn(f[0], pct, hrs, f[3] == "1", rate, f[5] == "1", f[6]));
    }
    return rows;
}

static Cached? FetchAndSave(long now) {
    SaveTry(now);
    var (u, why) = Fetch();
    if (u is null) { SaveFailure(why); return null; }
    string acct = AccountInfo().uuid;
    var hist = LoadHist();
    string oldAcct = RegStr("acct"), oldName = RegStr("sn");
    long vfS = RegLong("vfS"), vfW = RegLong("vfW"), vfF = RegLong("vfF");
    long rsS = RegLong("rsS"), rsW = RegLong("rsW"), rsF = RegLong("rsF");
    var (iS, iW, iF, ignored) = Known(u.Meters, oldName);
    string scopedName = iF >= 0 ? u.Meters[iF].Label : "";

    if (acct.Length > 0 && oldAcct.Length > 0 && acct != oldAcct) {
        hist.Clear(); vfS = vfW = vfF = now;   // account switch: the whole series is foreign
    }
    // the scoped limit tracks a different model than before: its series is foreign too
    if (scopedName.Length > 0 && oldName.Length > 0 && scopedName != oldName) vfF = now;
    var last = hist.Count > 0 ? hist[^1] : (t: 0L, s: -1, w: -1, f: -1);
    if (iS >= 0) WindowCheck(u.Meters[iS].Lim, ref rsS, ref vfS, last.s, now);
    if (iW >= 0) WindowCheck(u.Meters[iW].Lim, ref rsW, ref vfW, last.w, now);
    if (iF >= 0) WindowCheck(u.Meters[iF].Lim, ref rsF, ref vfF, last.f, now);

    // a series whose meter the server did not send this time records -1, which Slope skips
    int s = iS >= 0 ? u.Meters[iS].Lim.Pct : -1, w = iW >= 0 ? u.Meters[iW].Lim.Pct : -1,
        f = iF >= 0 ? u.Meters[iF].Lim.Pct : -1;
    if (!(hist.Count > 0 && last.t == now && last.s == s && last.w == w && last.f == f))
        hist.Add((now, s, w, f));
    hist = hist.Where(h => now - h.t <= 3600 && h.t <= now).ToList();

    // the rows this status line draws, in the order it always has (5h, 7d, the scoped
    // row), each with its own history series
    var rows = new List<RowIn>();
    foreach (var (i, which) in new[] { (iS, 0), (iW, 1), (iF, 2) }) {
        if (i < 0) continue;
        var m = u.Meters[i];
        double rate = Slope(hist, which, which == 0 ? vfS : which == 1 ? vfW : vfF, out bool gated);
        rows.Add(new RowIn(m.Label, m.Lim.Pct, m.Lim.Hours, m.Lim.HasReset, rate, gated, m.Lim.Sev));
    }
    var c = new Cached(rows, u.Bd, u.Cr, string.Join("\n", ignored));
    Save(acct.Length > 0 ? acct : oldAcct, scopedName.Length > 0 ? scopedName : oldName,
         rsS, rsW, rsF, vfS, vfW, vfF, hist, c, now);
    return c;
}

// The count of failed fetches in a row after an attempt: a success clears it, a failure adds
// one and keeps its reason, and "none", no attempt, leaves both as they were. The ⚠ row waits
// for the second failure in a row (FetchWarn), so a single lost fetch stays quiet and the
// next attempt is its retry.
static (int fails, string why) AfterFetch(int fails, string why, string attempt) =>
    attempt == "ok" ? (0, "") : attempt == "none" ? (fails, why) : (Math.Min(fails, 999_999) + 1, attempt);

// The ⚠ reason once the fetch has failed twice in a row or more, and "" before that: the
// usage source, why the latest attempt failed, and how old the rows still drawn are, from
// okAt, the time of the last good fetch; 0 means there are no rows to draw.
static string FetchWarn(int fails, string why, long okAt, long now) {
    if (fails < 2) return "";
    return "usage — " + FetchWhy(why) + "; "
         + (okAt > 0 ? "the limit rows are " + Hm((now - okAt) / 3600.0) + " old" : "no limit rows yet");
}

// A failed fetch in words. The registry's why holds a short kind for the failures the row has
// words for, and a description of anything else, which is shown as it is.
static string FetchWhy(string why) => why switch {
    "timeout" => "timed out after 3 s",
    "offline" => "offline, api.anthropic.com not reached",
    "no token" => "no OAuth token in .credentials.json",
    "" => "the fetch failed",
    _ when why.StartsWith("http ", StringComparison.Ordinal) => "HTTP " + why.Substring(5) + " from api.anthropic.com",
    _ => why
};

// A failed fetch leaves the cache as it was, stale, so the next render to take the lock tries
// again. Only the count of failures in a row and the latest reason are written.
static void SaveFailure(string why) {
    try {
        var (fails, w) = AfterFetch(RegInt("fail"), RegStr("why"), why);
        using var rk = Registry.CurrentUser.CreateSubKey(RegKey);
        rk.SetValue("fail", fails.ToString(CultureInfo.InvariantCulture));
        rk.SetValue("why", w);
    } catch { }
}

// When the fetch was last tried, which MayRetry measures from. Written before the attempt, so
// one that Claude Code cuts short still counts.
static void SaveTry(long now) {
    try {
        using var rk = Registry.CurrentUser.CreateSubKey(RegKey);
        rk.SetValue("tryTs", now.ToString());
    } catch { }
}

// The three meters this status line draws (the session, weekly_all and one model-scoped
// weekly_scoped) and a description of every other meter in the response. The scoped row
// stays with the meter the history has been following (sn) for as long as the server still
// sends it, and otherwise takes the highest, as it always has. Anything else (an unknown
// kind, a surface-scoped meter, a second model-scoped one) is not drawn until the code has
// been reviewed for it; MetersRow names it instead. A missing session or weekly_all just
// means one row fewer.
static (int s, int w, int f, List<string> ignored) Known(List<Meter> m, string trackedName) {
    static bool ModelScoped(Meter x) => x.Kind == "weekly_scoped" && x.Scope == "model";
    int s = m.FindIndex(x => x.Kind == "session"), w = m.FindIndex(x => x.Kind == "weekly_all");
    int f = trackedName.Length > 0 ? m.FindIndex(x => ModelScoped(x) && x.Label == trackedName) : -1;
    if (f < 0)
        for (int i = 0; i < m.Count; i++)
            if (ModelScoped(m[i]) && (f < 0 || m[i].Lim.Pct > m[f].Lim.Pct)) f = i;
    var ignored = new List<string>();
    for (int i = 0; i < m.Count; i++)
        if (i != s && i != w && i != f) ignored.Add(Describe(m[i]));
    return (s, w, f, ignored);
}

// "Cowork (weekly_scoped, surface)": the name the meter would be drawn under, its kind and
// what its scope names: enough to find it in the response when the code is reviewed.
static string Describe(Meter m) {
    string kind = Clean(m.Kind);
    if (kind.Length == 0) kind = "no kind";
    if (m.Scope.Length > 0) return $"{m.Label} ({kind}, {m.Scope})";
    return m.Label == kind ? kind : $"{m.Label} ({kind})";
}

// A metric's history is only usable within one account+window. resets_at moving to a new
// window, or a percent drop (impossible organically inside a window), invalidates it.
static void WindowCheck(Lim lim, ref long storedRs, ref long vf, int lastPct, long now) {
    if (storedRs != 0 && lim.ResetsUnix != 0 && Math.Abs(lim.ResetsUnix - storedRs) > 60) vf = now;
    else if (lastPct >= 0 && lim.Pct < lastPct - 1) vf = now;
    if (lim.ResetsUnix != 0) storedRs = lim.ResetsUnix;
}

// read fresh every render (never cached) so an account switch shows immediately
static (string uuid, string email) AccountInfo() {
    string uuid = "", email = "";
    try {
        string p = Path.Combine(Home(), ".claude.json");
        using var fs = File.OpenRead(p);
        using var d = JsonDocument.Parse(fs);
        if (d.RootElement.TryGetProperty("oauthAccount", out var a) && a.ValueKind == JsonValueKind.Object) {
            if (a.TryGetProperty("accountUuid", out var u)) uuid = u.GetString() ?? "";
            if (a.TryGetProperty("emailAddress", out var e)) email = e.GetString() ?? "";
        }
    } catch { }
    return (uuid, email);
}

// "max" + rateLimitTier "default_claude_max_20x" -> "Max 20"
static string Plan() {
    string sub = "", tier = "";
    try {
        string p = Path.Combine(Home(), ".claude", ".credentials.json");
        using var fs = File.OpenRead(p);
        using var d = JsonDocument.Parse(fs);
        if (d.RootElement.TryGetProperty("claudeAiOauth", out var o) && o.ValueKind == JsonValueKind.Object) {
            if (o.TryGetProperty("subscriptionType", out var s)) sub = s.GetString() ?? "";
            if (o.TryGetProperty("rateLimitTier", out var t)) tier = t.GetString() ?? "";
        }
    } catch { }
    if (sub.Length == 0) return "";
    string name = char.ToUpperInvariant(sub[0]) + sub.Substring(1);
    string mult = Multiplier(tier);
    return mult.Length > 0 ? $"{name} {mult}" : name;
}

// pull the "20" out of a tier string ending in "<digits>x"
static string Multiplier(string tier) {
    for (int i = 0; i < tier.Length; i++) {
        if (!char.IsDigit(tier[i])) continue;
        int j = i;
        while (j < tier.Length && char.IsDigit(tier[j])) j++;
        if (j < tier.Length && tier[j] == 'x') return tier.Substring(i, j - i);
        i = j;
    }
    return "";
}

static (string line, bool signedIn) BuildAccount() {
    string dim = "\x1b[38;2;110;115;141m", txt = "\x1b[38;2;169;177;214m", rst = "\x1b[0m";
    string email = AccountInfo().email;
    if (email.Length == 0) return ($"{dim}👤 not signed in{rst}", false);
    var sb = new StringBuilder();
    sb.Append($"👤 {txt}{email}{rst}");
    string plan = Plan();
    if (plan.Length > 0) sb.Append($" {dim}· {plan}{rst}");
    return (sb.ToString(), true);
}

static List<(long t, int s, int w, int f)> LoadHist() {
    var list = new List<(long, int, int, int)>();
    try {
        using var rk = Registry.CurrentUser.OpenSubKey(RegKey);
        if (rk?.GetValue("hist") is string h && h.Length > 0)
            foreach (var part in h.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)) {
                var f = part.Split(':');
                if (f.Length == 4 && long.TryParse(f[0], out var t) && int.TryParse(f[1], out var s)
                    && int.TryParse(f[2], out var w) && int.TryParse(f[3], out var fa)) list.Add((t, s, w, fa));
            }
    } catch { }
    return list;
}

static string RegStr(string name) {
    try { using var rk = Registry.CurrentUser.OpenSubKey(RegKey); return rk?.GetValue(name) as string ?? ""; } catch { return ""; }
}

static long RegLong(string name) => long.TryParse(RegStr(name), out long v) ? v : 0;

static int RegInt(string name) => int.TryParse(RegStr(name), out int v) ? v : 0;

// A good fetch: the rows and what came with them, the history, and a cleared count of
// failures. val, the drawn rows builds before this one cached, is removed, so that a build
// restored from a backup fetches afresh rather than drawing rows from before the switch.
static void Save(string acct, string scopedName, long rsS, long rsW, long rsF, long vfS, long vfW, long vfF,
                 List<(long t, int s, int w, int f)> hist, Cached c, long now) {
    try {
        using var rk = Registry.CurrentUser.CreateSubKey(RegKey);
        rk.SetValue("acct", acct);
        rk.SetValue("sn", scopedName);
        rk.SetValue("rsS", rsS.ToString()); rk.SetValue("rsW", rsW.ToString()); rk.SetValue("rsF", rsF.ToString());
        rk.SetValue("vfS", vfS.ToString()); rk.SetValue("vfW", vfW.ToString()); rk.SetValue("vfF", vfF.ToString());
        rk.SetValue("hist", string.Join(";", hist.Select(h => $"{h.t}:{h.s}:{h.w}:{h.f}")));
        rk.SetValue("rows", RowsOut(c.Rows));
        rk.SetValue("bd", c.Bd);
        rk.SetValue("cr", c.Cr);
        rk.SetValue("ig", c.Ig);
        rk.SetValue("fail", "0");   // a good fetch clears the count, as AfterFetch has it
        rk.SetValue("why", "");
        rk.SetValue("ts", now.ToString());   // freshness gate: must be the last write
    } catch { }
}

// Theil-Sen: the median of the pairwise slopes, so a stray step the window invalidation
// missed barely moves it. Gated until there are 4+ samples spanning 10+ minutes.
static double Slope(List<(long t, int s, int w, int f)> hist, int which, long validFrom, out bool gated) {
    gated = true;
    var pts = new List<(double x, double y)>();
    foreach (var h in hist) {
        if (h.t < validFrom) continue;
        double y = which == 0 ? h.s : which == 1 ? h.w : h.f;
        if (y < 0) continue;
        pts.Add((h.t / 3600.0, y));
    }
    if (pts.Count < 4) return 0;
    if (pts[^1].x - pts[0].x < 10.0 / 60.0) return 0;
    var slopes = new List<double>();
    for (int i = 0; i < pts.Count; i++)
        for (int j = i + 1; j < pts.Count; j++) {
            double dx = pts[j].x - pts[i].x;
            if (dx > 1e-9) slopes.Add((pts[j].y - pts[i].y) / dx);
        }
    if (slopes.Count == 0) return 0;
    slopes.Sort();
    gated = false;
    return slopes[slopes.Count / 2];
}

// per-cell colour by zone: 0-70 cyan, 70-90 amber, 90 and up bold red (past 100 the cells
// are drawn as ✗, see Bar)
static string ZoneColor(int oneBasedBullet) {
    if (oneBasedBullet >= 11) return "\x1b[1;38;2;247;118;142m";
    if (oneBasedBullet == 10) return "\x1b[1;38;2;247;118;142m";
    if (oneBasedBullet >= 8) return "\x1b[38;2;224;175;104m";
    return "\x1b[38;2;125;207;255m";
}

// Ceiling, not rounding. With rounding a bar stood still across the one boundary that
// matters: 95% to 104% all drew ten ●, so crossing 100 changed nothing and the ✗ overflow
// cell needed 105% to appear. Ceiling lights a cell as soon as its tenth is entered, so
// 101% is eleven cells, the last one an ✗. Every bar over-reports (31% draws 4 of 10, not
// 3), which is the accepted trade: better to over-project than to hide a crossing.
//
// Bar() and the width solver in RenderRows both use this and must agree: the solver pads
// a bar to a width computed from this number, so two formulas could disagree and shift
// the second column.
static int BarFill(int pct) {
    int f = (int)Math.Ceiling(pct / 10.0);
    return f < 0 ? 0 : f;
}

// `muted` draws every cell dim (filled, ✗ and empty alike) for a bar that is shown but does
// not apply. The glyphs, and so the width, are the same as the coloured bar's.
static string Bar(int pct, int padTo = 0, int cap = 15, bool muted = false) {
    int fill = BarFill(pct);
    int len = Math.Min(cap, Math.Max(10, fill));
    var sb = new StringBuilder();
    string cur = "";
    for (int i = 1; i <= len; i++) {
        bool on = i <= fill;
        string col = on && !muted ? ZoneColor(i) : "\x1b[38;2;110;115;141m";
        if (col != cur) { sb.Append(col); cur = col; }
        sb.Append(on ? (i >= 11 ? '✗' : '●') : '○');
    }
    sb.Append("\x1b[0m");
    if (padTo > len) sb.Append(' ', padTo - len);
    return sb.ToString();
}

static string PctColor(int p) =>
    p >= 90 ? "\x1b[1;38;2;247;118;142m" : p >= 70 ? "\x1b[38;2;224;175;104m" : "\x1b[38;2;125;207;255m";

static string SevColor(string sev) =>
    sev == "critical" ? "\x1b[1;38;2;247;118;142m" : sev == "warning" ? "\x1b[38;2;224;175;104m" : "";

// leftCount = rows that stack in the left column. Column widths are the max needed across
// the rows of one column this render, so the rows that stack stay aligned while never
// padding wider than the current values require. avail is Avail(): the rows are drawn per
// render, at the width of the session drawing them, never cached drawn.
static string RenderRows(List<RowIn> rows, int leftCount, int avail) {
    var d = rows.Select(r => {
        int now = Math.Clamp(r.Pct, 0, 100);
        double rate = Math.Min(r.Rate, 40);
        bool burning = !r.Gated && rate > 0.5;
        // Project to the row's own reset, uncapped. An 8h cap made ⇢ mean "at reset" on the
        // 5h row and "in 8 hours" on the 7d row, one line apart, so a red "hits 100% in
        // 2d19h" could sit next to a calm ⇢ 20%.
        double horizon = r.Hrs;
        // Without a reset time there is no horizon, so there is no forecast to make: the
        // bar shows the current value and says nothing about where it is heading.
        int proj = burning && r.HasReset ? Math.Min((int)Math.Round(now + rate * horizon), 300) : now;
        // The → colour: 0 dim, 1 red, 2 forest.
        string to100; int tone = 0;
        if (r.Gated) to100 = "early";   // not enough same-window data for a reliable trend yet
        else if (burning && now < 100) {
            double h = (100 - now) / rate;
            // Tested against the row's own reset. Red: 100% arrives before the window resets.
            // Forest: the reset comes first, so at this pace the window never runs out; the
            // time is still shown and the colour says it is harmless. When we don't know
            // when the window resets, neither is claimed and the time stays dim.
            if (r.HasReset) tone = h < r.Hrs ? 1 : 2;
            to100 = Hm(h);
        } else to100 = now >= 100 ? "maxed" : "never";
        return (label: r.Label, now, proj, reset: r.HasReset ? Hm(r.Hrs) : NoData, to100, tone, sev: r.Sev);
    }).ToList();
    // Every width is per column ([0] left, [1] right) and never shared. The left column's
    // rows stack, 5h above 7d, so they have to agree to line up; the right column holds the
    // scoped row alone, next to 7d, with nothing to line up with. The labels were always
    // kept apart like this, so 5h/7d never take the width of a longer label like Fable. The
    // other five widths used to be shared, which aligned nothing and padded the lone
    // right-hand row to the widest value on the left: a 7d projecting 226% draws 22 cells,
    // which put Fable's 24% fourteen blank columns past the end of its own ten-cell bar.
    int[] wLbl = new int[2], wNow = new int[2], wReset = new int[2], wTo100 = new int[2], wProj = new int[2];
    for (int i = 0; i < d.Count; i++) {
        int c = i < leftCount ? 0 : 1;
        wLbl[c] = Math.Max(wLbl[c], d[i].label.Length);
        wNow[c] = Math.Max(wNow[c], d[i].now.ToString().Length);
        wReset[c] = Math.Max(wReset[c], d[i].reset.Length);
        wTo100[c] = Math.Max(wTo100[c], d[i].to100.Length);
        wProj[c] = Math.Max(wProj[c], d[i].proj.ToString().Length);
    }

    // The now-bar's own width, per column, padded below the same way as the projection bar.
    // `now` is clamped to 0..100 above, so ceil(now/10) can't exceed ten, and capNowBar
    // makes that a guarantee rather than an accident: an eleventh cell would shift the rest
    // of that one row, and Compose() puts two rows side by side assuming the left column's
    // rows have one visible width.
    const int capNowBar = 10;
    int[] wNowBar = { 10, 10 };
    for (int i = 0; i < d.Count; i++) {
        int c = i < leftCount ? 0 : 1;
        wNowBar[c] = Math.Max(wNowBar[c], Math.Min(capNowBar, BarFill(d[i].now)));
    }
    static int Wider(int[] w) => Math.Max(w[0], w[1]);

    // Spend whatever width is left on overshoot markers. A two-column line costs
    // indent + leftRow + gap + rightRow; everything but the projection bars is known here,
    // so solve for the bar length that fills the terminal. Recomputed every render, so a
    // wider reset/eta column shrinks the bars instead of wrapping.
    //
    // rowConst counts a row's glyphs and spaces outside the label, numbers and bars. The
    // true count is 14 (nine spaces, ↻ → ⇢ and two '%'); 15 leaves a column of slack per
    // row, so the line underfills by two columns and never overflows. It was 25 while it
    // still included the now-bar's ten cells, which are now a term of their own.
    //
    // The solver charges each of the five widths at the wider of the two columns, which is
    // the same max over all rows it charged when the widths were shared. So capBar is what
    // it always was, and a column's own widths can only be narrower.
    const int rowConst = 15;   // per-row glyphs/spaces outside label, numbers and bars
    // 1 = our indent, 2 = column gap; the host's padding is already out of avail
    int fixedPart = 1 + 2 + rowConst * 2 + wLbl[0] + wLbl[1]
                  + 2 * (Wider(wNow) + Wider(wReset) + Wider(wTo100) + Wider(wProj) + Wider(wNowBar));
    int capBar = Math.Clamp((avail - 2 - fixedPart) / 2, 10, 30);   // -2 = safety margin
    // max over both bars' fills, so the projection column is never padded narrower than
    // something already drawn on the line; per column, like every other width
    int[] wBar = { 10, 10 };
    for (int i = 0; i < d.Count; i++) {
        int c = i < leftCount ? 0 : 1;
        wBar[c] = Math.Max(wBar[c], Math.Min(capBar, Math.Max(BarFill(d[i].now), BarFill(d[i].proj))));
    }
    string dim = "\x1b[38;2;110;115;141m", red = "\x1b[1;38;2;247;118;142m", rst = "\x1b[0m";
    var outLines = new List<string>();
    for (int i = 0; i < d.Count; i++) {
        var x = d[i];
        int c = i < leftCount ? 0 : 1;
        var sb = new StringBuilder();
        string lbl = x.label.PadRight(wLbl[c]);
        string sevc = SevColor(x.sev);
        sb.Append(sevc.Length > 0 ? $"{sevc}{lbl}{rst}" : lbl);
        sb.Append($" {Bar(x.now, wNowBar[c], capNowBar)} {PctColor(x.now)}{x.now.ToString().PadLeft(wNow[c])}%{rst}");
        sb.Append($" \x1b[38;2;166;227;161m↻{rst} \x1b[38;2;198;246;193m{x.reset.PadRight(wReset[c])}{rst}");
        sb.Append($" {(x.tone == 1 ? red : x.tone == 2 ? Forest : dim)}→ {x.to100.PadRight(wTo100[c])}{rst}");
        // A row at 100% is blocked until its window resets, so where it is heading doesn't
        // apply for now. The ⇢ segment stays, so the row keeps its shape and the pace is
        // still readable, but all of it is dim (the glyph, every bar cell including the ✗
        // marks, and the percentage), the way a disabled control is greyed out, not removed.
        bool blocked = x.now >= 100;
        sb.Append($" {dim}⇢{rst} {Bar(x.proj, wBar[c], capBar, blocked)} {(blocked || x.proj <= 100 ? dim : red)}{x.proj.ToString().PadLeft(wProj[c])}%{rst}");
        outLines.Add(sb.ToString());
    }
    return string.Join("\n", outLines);
}

// haveCost / haveDur say whether the payload carried the field at all. A figure whose
// source is missing renders as NoData, not 0: a small number in the meta segment means a
// cheap session, and a silent 0 for "the field wasn't there" says the opposite. A 0 that
// was actually sent still renders as $0,00.
static string BuildMeta(double cost, long durMs, int add, int del, double eurRate, bool haveCost, bool haveDur) {
    // hidden only when both sources really reported nothing; an absent source has
    // something to say and says it
    if (durMs <= 0 && cost <= 0 && haveCost && haveDur) return "";
    double hours = durMs / 3600000.0;
    var sb = new StringBuilder();
    const string txt = "\x1b[38;2;169;177;214m", dim = "\x1b[38;2;110;115;141m", rst = "\x1b[0m";
    sb.Append($"\x1b[38;2;180;190;254m⏱ {(haveDur ? Hm(hours) : NoData)}{rst}");
    if (add > 0 || del > 0) sb.Append($"   📝 \x1b[38;2;166;227;161m+{add}{rst} \x1b[38;2;247;118;142m-{del}{rst}");
    if (hours > 0.02) {
        if (!haveCost) sb.Append($"   {txt}💸 {NoData}/h{rst}");
        else {
            sb.Append($"   {txt}💸 ${Nl((cost / hours).ToString("N2"))}");
            if (eurRate > 0) sb.Append($"{dim}/{txt}€{Nl((cost / hours * eurRate).ToString("N2"))}");
            sb.Append($"/h{rst}");
        }
    }
    if (!haveCost) { sb.Append($"   {txt}💰 {NoData}{rst}"); return sb.ToString(); }
    sb.Append($"   {txt}💰 ${Nl(cost.ToString("N2"))}{rst}");
    if (eurRate > 0) sb.Append($"{dim}/{txt}€{Nl((cost * eurRate).ToString("N2"))}{rst}");
    return sb.ToString();
}

// nl-NL notation: 1.234,56. InvariantGlobalization is on, so there is no culture to do this
// at runtime: every named culture silently resolves to invariant. Formatting invariant and
// swapping the two separators gets there without the ICU dependency.
static string Nl(string s) {
    var sb = new StringBuilder(s.Length);
    foreach (char c in s) sb.Append(c == ',' ? '.' : c == '.' ? ',' : c);
    return sb.ToString();
}

// The API only reports USD. The ECB publishes a free daily reference feed with no key;
// it is EUR-based, so its USD rate is dollars per euro and has to be inverted. The rate is
// cached for a day. If the refresh fails the old rate is kept and the next render tries
// again, and with no rate at all the euro half of the figure is dropped rather than
// blocking the render.
static double EurPerUsd() {
    if (Offline() is string dir) return OfflineFx(dir);
    long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    double cached = 0;
    try {
        using var rk = Registry.CurrentUser.OpenSubKey(RegKey);
        if (rk?.GetValue("fx") is string fv
            && double.TryParse(fv, NumberStyles.Float, CultureInfo.InvariantCulture, out double c)) cached = c;
        if (cached > 0 && rk?.GetValue("fxTs") is string ts
            && long.TryParse(ts, out long t) && now - t < 86400) return cached;
    } catch { }
    double fresh = FetchEurPerUsd();
    if (fresh <= 0) return cached;
    try {
        using var rk = Registry.CurrentUser.CreateSubKey(RegKey);
        rk.SetValue("fx", fresh.ToString("R", CultureInfo.InvariantCulture));
        rk.SetValue("fxTs", now.ToString());
    } catch { }
    return fresh;
}

static double FetchEurPerUsd() {
    try {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        var req = new HttpRequestMessage(HttpMethod.Get,
            "https://www.ecb.europa.eu/stats/eurofxref/eurofxref-daily.xml");
        using var resp = http.Send(req);
        if (!resp.IsSuccessStatusCode) return 0;
        string body;
        using (var rs = resp.Content.ReadAsStream())
        using (var sr = new StreamReader(rs, Encoding.UTF8)) body = sr.ReadToEnd();
        int i = body.IndexOf("'USD'", StringComparison.Ordinal);
        if (i < 0) i = body.IndexOf("\"USD\"", StringComparison.Ordinal);
        if (i < 0) return 0;
        int r = body.IndexOf("rate=", i, StringComparison.Ordinal);
        if (r < 0 || r + 5 >= body.Length) return 0;
        r += 5;
        int e = body.IndexOf(body[r], r + 1);
        if (e < 0) return 0;
        return double.TryParse(body.AsSpan(r + 1, e - r - 1), NumberStyles.Float,
                               CultureInfo.InvariantCulture, out double usdPerEur) && usdPerEur > 0
               ? 1.0 / usdPerEur : 0;
    } catch { return 0; }
}

// days once past 24h ("6d23h") so a week-long countdown needs no mental arithmetic
static string Hm(double hours) {
    if (hours < 0) hours = 0;
    int h = (int)hours; int m = (int)Math.Round((hours - h) * 60);
    if (m == 60) { h++; m = 0; }
    if (h >= 24) return $"{h / 24}d{h % 24:D2}h";
    return h > 0 ? $"{h}h{m:D2}m" : $"{m}m";
}

// One call to the usage API. On a failure the usage is null and why says what went wrong, as
// a kind FetchWhy has words for where there is one: no token, timeout, offline, http <status>.
static (Usage? u, string why) Fetch() {
    string token = "";
    try {
        using var cd = JsonDocument.Parse(File.ReadAllText(Path.Combine(Home(), ".claude", ".credentials.json")));
        token = cd.RootElement.GetProperty("claudeAiOauth").GetProperty("accessToken").GetString() ?? "";
    } catch { }
    if (token.Length == 0) return (null, "no token");
    string body;
    try {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        var req = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/api/oauth/usage");
        req.Headers.Add("Authorization", "Bearer " + token);
        req.Headers.Add("anthropic-beta", "oauth-2025-04-20");
        req.Headers.Add("User-Agent", "claude-code/2.1.90");
        using var resp = http.Send(req);
        if (!resp.IsSuccessStatusCode) return (null, "http " + (int)resp.StatusCode);
        using (var rs = resp.Content.ReadAsStream())
        using (var sr = new StreamReader(rs, Encoding.UTF8)) body = sr.ReadToEnd();
    } catch (Exception ex) { return (null, FailKind(ex)); }
    try {
        using var doc = JsonDocument.Parse(body);
        var u = ParseUsage(doc.RootElement, DateTimeOffset.UtcNow);
        return u.Meters.Count > 0 ? (u, "") : (null, "the response has no meters");
    } catch (Exception ex) { return (null, "unreadable response, " + ex.GetType().Name); }
}

// What a failed request comes down to: "timeout" for HttpClient's own 3 s limit, "offline"
// when the name did not resolve or the network is down or out of reach, and otherwise the
// exception's type and message.
static string FailKind(Exception ex) {
    for (Exception? e = ex; e is not null; e = e.InnerException) {
        if (e is TimeoutException) return "timeout";
        if (e is System.Net.Sockets.SocketException se && se.SocketErrorCode is
                System.Net.Sockets.SocketError.HostNotFound or System.Net.Sockets.SocketError.TryAgain
                or System.Net.Sockets.SocketError.NoData or System.Net.Sockets.SocketError.NetworkDown
                or System.Net.Sockets.SocketError.NetworkUnreachable or System.Net.Sockets.SocketError.HostUnreachable)
            return "offline";
    }
    if (ex is TaskCanceledException) return "timeout";
    return Clean(ex.GetType().Name + " " + Short(ex.Message, 48));
}

// Everything this binary draws from the usage response, measured against `utc`. Pure: the
// live fetch and the offline render both come through here, so a fixture exercises the
// same parse. A meter that cannot be read throws, and the fetch falls back to the cache as
// it always has; the breakdown and the credit state are read leniently instead, because a
// shape change there must not cost the meters.
static Usage ParseUsage(JsonElement root, DateTimeOffset utc) {
    var u = new Usage();
    if (root.TryGetProperty("limits", out var limits) && limits.ValueKind == JsonValueKind.Array) {
        // every meter, in the server's order. Known() then decides which are drawn and the
        // rest are flagged, so nothing the server sends goes unmentioned
        foreach (var lim in limits.EnumerateArray()) {
            string kind = lim.TryGetProperty("kind", out var k) ? k.GetString() ?? "" : "";
            int pct = lim.TryGetProperty("percent", out var pc) ? (int)pc.GetDouble() : 0;
            // resets_at is a time on every kind in the 2026-08-15 and 2026-09-23 responses,
            // weekly_scoped included, but on 2026-08-18 weekly_scoped at 0% sent null in three
            // samples in a row. So "no reset time" is a state a payload can report (the
            // legacy shape below never has one), and it is carried as a flag rather than as
            // an hrs of 0, which reads as "resets right now"
            double hrs = 0; long rsu = 0; bool hasReset = false;
            if (lim.TryGetProperty("resets_at", out var ra) && ra.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(ra.GetString(), out var rdt)) {
                hrs = Math.Max(0, (rdt - utc).TotalHours); rsu = rdt.ToUnixTimeSeconds();
                hasReset = true;
            }
            string sev = lim.TryGetProperty("severity", out var sv) ? sv.GetString() ?? "" : "";
            var (label, scope) = MeterLabel(kind, lim);
            u.Meters.Add(new Meter(kind, label, scope, new Lim(pct, hrs, hasReset, rsu, sev)));
        }
    } else {
        // the legacy shape carries utilization but no reset time, so both resets are
        // unknown here, not zero minutes away
        u.Meters.Add(new Meter("session", "5h", "",
            new Lim((int)root.GetProperty("five_hour").GetProperty("utilization").GetDouble(), 0, false, 0, "")));
        u.Meters.Add(new Meter("weekly_all", "7d", "",
            new Lim((int)root.GetProperty("seven_day").GetProperty("utilization").GetDouble(), 0, false, 0, "")));
    }
    u.Bd = Breakdown(root);
    u.Cr = Credit(root, u.Meters);
    return u;
}

// 5h and 7d for the two rows there have always been, and the scope's name for the rest
// (the model it is for, else the surface), falling back to the kind itself, so a meter this
// binary has never seen can at least be named. Also returns what the scope names: "model",
// "surface", "model+surface" or nothing. The model-scoped weekly_scoped is the only scoped
// meter that is drawn.
static (string label, string scope) MeterLabel(string kind, JsonElement lim) {
    string model = "", surface = "";
    if (lim.TryGetProperty("scope", out var sc) && sc.ValueKind == JsonValueKind.Object) {
        if (sc.TryGetProperty("model", out var mo) && mo.ValueKind == JsonValueKind.Object)
            model = Clean(Str(mo, "display_name"));
        if (sc.TryGetProperty("surface", out var su) && su.ValueKind == JsonValueKind.Object)
            surface = Clean(Str(su, "display_name"));
    }
    string scope = model.Length > 0 && surface.Length > 0 ? "model+surface"
                 : model.Length > 0 ? "model" : surface.Length > 0 ? "surface" : "";
    string label = kind == "session" ? "5h" : kind == "weekly_all" ? "7d"
                 : model.Length > 0 ? model : surface.Length > 0 ? surface
                 : kind == "weekly_scoped" ? "scoped" : kind.Length > 0 ? Clean(kind) : "limit";
    return (label, scope);
}

// seven_day_breakdown.rows[]: each product's share of this week's usage, in whole percents
// that sum to 100 (its window_started_at is exactly seven days before weekly_all's
// resets_at). Cached as "CC=99;Chat=0;Cowork=1", already cut to what may be shown: the three
// products it has always listed, even at 0%, and "Other" or anything unrecognised only while
// it is above 0.
static string Breakdown(JsonElement root) {
    try {
        if (!root.TryGetProperty("seven_day_breakdown", out var b) || b.ValueKind != JsonValueKind.Object
            || !b.TryGetProperty("rows", out var rows) || rows.ValueKind != JsonValueKind.Array) return "";
        var parts = new List<string>();
        foreach (var r in rows.EnumerateArray()) {
            if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("percent", out var p)
                || p.ValueKind != JsonValueKind.Number) continue;
            string key = Str(r, "key"), name = Str(r, "display_name");
            int pct = (int)Math.Round(p.GetDouble(), MidpointRounding.AwayFromZero);
            string label = (key, name) switch {
                ("claude_code", _) or (_, "Claude Code") => "CC",
                ("chat", _) or (_, "Chats") => "Chat",
                ("cowork", _) or (_, "Cowork") => "Cowork",
                _ => ""
            };
            if (label.Length == 0) {
                if (pct == 0) continue;
                label = Clean(name.Length > 0 ? name : key);
                if (label.Length == 0) continue;
            }
            parts.Add(label.Replace(';', ',').Replace('=', '-') + "=" + pct);
        }
        return string.Join(";", parts);
    } catch { return ""; }
}

// Usage billed beyond the plan, which the plan is there to prevent, so an alarm and not a
// figure. It triggers on money already spent this period (spend.used.amount_minor or
// extra_usage.used_credits above 0), or on a limit at 100% while credits are switched on
// (spend.enabled or extra_usage.is_enabled), the moment further usage starts to bill.
//
// The amount is spend.used where there is one (minor units, with the exponent and currency
// beside them), else extra_usage.used_credits, also read as minor units with decimal_places
// as the exponent. That last part is inferred, not confirmed: in the August response
// extra_usage.monthly_limit was 1000 where spend.limit.amount_minor was 1000 at exponent 2,
// and no non-zero used_credits has been seen yet.
static string Credit(JsonElement root, List<Meter> meters) {
    try {
        bool on = false, haveSpend = false, haveCredits = false;
        double spendMinor = 0, credits = 0; int spendExp = 2, creditExp = 2;
        string spendCur = "", creditCur = "";
        if (root.TryGetProperty("spend", out var sp) && sp.ValueKind == JsonValueKind.Object) {
            on |= sp.TryGetProperty("enabled", out var en) && en.ValueKind == JsonValueKind.True;
            if (sp.TryGetProperty("used", out var used) && used.ValueKind == JsonValueKind.Object
                && used.TryGetProperty("amount_minor", out var am) && am.ValueKind == JsonValueKind.Number) {
                spendMinor = am.GetDouble(); haveSpend = true;
                spendCur = Str(used, "currency");
                if (used.TryGetProperty("exponent", out var ex) && ex.ValueKind == JsonValueKind.Number
                    && ex.TryGetInt32(out int e)) spendExp = e;
            }
        }
        if (root.TryGetProperty("extra_usage", out var xu) && xu.ValueKind == JsonValueKind.Object) {
            on |= xu.TryGetProperty("is_enabled", out var en) && en.ValueKind == JsonValueKind.True;
            if (xu.TryGetProperty("used_credits", out var uc) && uc.ValueKind == JsonValueKind.Number) {
                credits = uc.GetDouble(); haveCredits = true;
                creditCur = Str(xu, "currency");
                if (xu.TryGetProperty("decimal_places", out var dp) && dp.ValueKind == JsonValueKind.Number
                    && dp.TryGetInt32(out int e)) creditExp = e;
            }
        }
        var maxed = meters.Where(m => m.Lim.Pct >= 100).Select(m => m.Label).ToList();
        bool billing = on && maxed.Count > 0;
        if (spendMinor <= 0 && credits <= 0 && !billing) return "";
        string amount = spendMinor > 0 || (credits <= 0 && haveSpend) ? Money(spendMinor, spendExp, spendCur)
                      : haveCredits ? Money(credits, creditExp, creditCur) : "no amount reported";
        return billing
            ? $"on credit — {string.Join(", ", maxed)} at 100% with usage credits on; {amount} spent so far this period"
            : $"on credit — {amount} spent beyond the plan this period";
    } catch { return ""; }
}

// Minor units at their own exponent, in the nl-NL notation of every other figure here. The
// currencies the meta segment already draws a sign for get it; anything else its code.
static string Money(double minor, int exp, string cur) {
    exp = Math.Clamp(exp, 0, 6);
    string n = Nl((minor / Math.Pow(10, exp)).ToString("N" + exp, CultureInfo.InvariantCulture));
    return cur switch { "USD" => "$" + n, "EUR" => "€" + n, "" => n, _ => n + " " + Clean(cur) };
}

static string Str(JsonElement o, string name) =>
    o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

// A string from the server is drawn straight into the terminal, so none of it may act on the
// terminal: control characters, ESC and the row separator among them, become spaces.
static string Clean(string s) {
    var sb = new StringBuilder(s.Length);
    foreach (char ch in s) sb.Append(char.IsControl(ch) ? ' ' : ch);
    return sb.ToString().Trim();
}

// ---------------------------------------------------------------- session token accounting
//
// Sub-agent turns are not in the main transcript: every child, at any depth, writes its
// own file under <sid>\subagents\, so a session total has to walk the tree. Two kinds of
// duplication make a plain sum wrong: one API response is written once per content block
// (about 2.2x), and child transcripts carry verbatim copies of ancestor records (measured
// up to 26x). uuid doesn't collapse the first, since every block has its own. What works
// is a single global dedup on message.id across every file.
//
// Parsing is incremental: per-file byte offsets, running totals and the dedup set are
// cached, so a render only parses the bytes appended since the last one. A fresh session
// costs next to nothing; the biggest sessions run to hundreds of MB, which is what the
// per-render byte budget below is for.

// Returns the rows and, separately, why there are none. Four of the five ways to end up
// with no rows are failures of the source (no path, an unusable path, no files on disk, an
// exception); only the fifth, a grand total of zero in TokRender, is a session that hasn't
// spent a token yet. Showing both as a missing row made them look the same, so the
// failures come back with a reason for the ⚠ row and the real zero comes back silent.
//
// `transcriptMayBeAbsent` excuses one of the four, no files on disk: a transcript that
// doesn't exist yet, on a session seconds old or on one that has had no prompt, is not a
// fault, because Claude Code only writes the file at the first prompt. It never excuses
// the other three.
static (List<string> lines, string err) BuildTokens(string transcriptPath, bool transcriptMayBeAbsent, int avail) {
    if (transcriptPath.Length == 0)
        return (new List<string>(), "tokens — no transcript_path in the payload");
    try {
        string dir = Path.GetDirectoryName(transcriptPath) ?? "";
        string sid = Path.GetFileNameWithoutExtension(transcriptPath);
        if (dir.Length == 0 || sid.Length == 0)
            return (new List<string>(), "tokens — unusable transcript_path '" + Short(transcriptPath) + "'");

        var files = new List<(string path, bool main)>();
        if (File.Exists(transcriptPath)) files.Add((transcriptPath, true));
        string subs = Path.Combine(dir, sid, "subagents");
        if (Directory.Exists(subs))
            foreach (var f in Directory.EnumerateFiles(subs, "agent-*.jsonl", SearchOption.AllDirectories)
                                       .OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                files.Add((f, false));   // nested workflow agents live deeper, so recurse
        // Nothing on disk to count: a fault, except before the first prompt. Claude Code
        // writes the transcript when the first prompt is sent, so a new session has no file
        // for as long as it sits unused, and a red row on the first frame of every session
        // would be noise, which is how warning rows stop being read. So while the caller
        // says the file may be absent (a session under 30 s old, or a payload with no
        // messages yet) this renders like a real zero: silently, with no token rows.
        // Otherwise the file should be there, and a missing one is reported.
        if (files.Count == 0)
            return (new List<string>(),
                    transcriptMayBeAbsent ? "" : "tokens — no transcript file '" + Short(transcriptPath) + "'");

        var st = TokLoad(sid);
        // A file shorter than its stored offset was rewritten rather than appended to.
        // Its message ids are already in the dedup set, so re-reading it would count
        // nothing. The only safe recovery is to rebuild the whole session from zero.
        foreach (var (path, _) in files) {
            if (!st.Off.TryGetValue(path, out long o)) continue;
            try { if (new FileInfo(path).Length < o) { st = new TokState(); break; } } catch { }
        }

        // Drop offsets for files that are no longer there. They can't affect the totals,
        // which key off message and tool ids rather than the file table, but without this
        // the table only grows: a deleted session or a renamed transcript would stay in
        // the cache forever.
        var live = new HashSet<string>(files.Select(f => f.path), StringComparer.OrdinalIgnoreCase);
        foreach (var gone in st.Off.Keys.Where(k => !live.Contains(k)).ToList()) st.Off.Remove(gone);

        bool truncated = TokScan(files, st);
        TokSave(sid, st);
        // TokRender returns nothing when the grand total is zero. That is the real zero of
        // the five, so it comes back with no reason attached.
        return (TokRender(st, truncated, avail), "");
    } catch (Exception ex) {
        // was a bare catch: the walk could fail on every file and the rows just vanished
        return (new List<string>(), "tokens — walk failed: " + ex.GetType().Name + " " + Short(ex.Message));
    }
}

// Parses at most `budget` new bytes per render and records how far it got, so a huge
// backlog is absorbed across several renders instead of stalling one.
static bool TokScan(List<(string path, bool main)> files, TokState st) {
    long budget = 64L * 1024 * 1024;
    const int chunk = 32 * 1024 * 1024;
    byte[]? buf = null;
    foreach (var (path, isMain) in files) {
        long len;
        try { len = new FileInfo(path).Length; } catch { continue; }
        while (true) {
            long have = st.Off.TryGetValue(path, out long o) ? o : 0;
            if (len <= have) break;
            if (budget <= 0) return true;
            int take = (int)Math.Min(Math.Min(len - have, budget), chunk);
            if (buf is null || buf.Length < take) buf = new byte[take];
            long used = TokScanOne(path, have, take, isMain, st, buf);
            if (used <= 0) break;   // no complete line in the window yet: a write in progress
            budget -= used;
        }
    }
    return false;
}

static long TokScanOne(string path, long from, int take, bool isMain, TokState st, byte[] buf) {
    int n = 0;
    try {
        // the live session is appending to these while we read
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                                      FileShare.ReadWrite | FileShare.Delete);
        fs.Seek(from, SeekOrigin.Begin);
        int r;
        while (n < take && (r = fs.Read(buf, n, take - n)) > 0) n += r;
    } catch { return 0; }

    int last = -1;
    for (int i = n - 1; i >= 0; i--) if (buf[i] == (byte)'\n') { last = i; break; }
    if (last < 0) return 0;   // stop on a record boundary, never mid-line

    long[] tgt = isMain ? st.Main : st.Sub;
    int start = 0;
    for (int i = 0; i <= last; i++) {
        if (buf[i] != (byte)'\n') continue;
        if (i > start) TokLine(buf, start, i - start, tgt, st.Seen, st.SeenTool);
        start = i + 1;
    }
    st.Off[path] = from + last + 1;
    return last + 1;
}

static void TokLine(byte[] buf, int start, int len, long[] tgt, HashSet<long> seen, HashSet<long> seenTool) {
    if (new ReadOnlySpan<byte>(buf, start, len).IndexOf("\"usage\""u8) < 0) return;
    try {
        using var d = JsonDocument.Parse(new ReadOnlyMemory<byte>(buf, start, len));
        var root = d.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return;
        if (!root.TryGetProperty("type", out var t) || !t.ValueEquals("assistant")) return;
        if (!root.TryGetProperty("message", out var m) || m.ValueKind != JsonValueKind.Object) return;

        // Tool calls first, on purpose: the sibling records of one API response all repeat
        // its message.id, so the dedup below returns early on every record after the first,
        // and those are the records the tool_use blocks are in. Their own toolu_ ids are
        // globally unique, so they don't need that dedup anyway.
        if (m.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            foreach (var b in content.EnumerateArray()) {
                if (b.ValueKind != JsonValueKind.Object) continue;
                if (!b.TryGetProperty("type", out var bt) || !bt.ValueEquals("tool_use")) continue;
                if (!b.TryGetProperty("id", out var bid) || bid.ValueKind != JsonValueKind.String) continue;
                if (seenTool.Add(TokHash(bid.GetString()!))) tgt[4]++;
            }

        if (!m.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String) return;
        if (!m.TryGetProperty("usage", out var u) || u.ValueKind != JsonValueKind.Object) return;
        if (!seen.Add(TokHash(id.GetString()!))) return;
        tgt[0] += TokNum(u, "input_tokens");
        tgt[1] += TokNum(u, "output_tokens");
        tgt[2] += TokNum(u, "cache_creation_input_tokens");
        tgt[3] += TokNum(u, "cache_read_input_tokens");
    } catch { }   // api-error records carry an all-zero usage; a bad line is just skipped
}

static long TokNum(JsonElement o, string name) =>
    o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
    && v.TryGetInt64(out long n) ? n : 0;

// FNV-1a: message ids are ASCII, and 64 bits keeps the dedup set small enough to persist
static long TokHash(string s) {
    ulong h = 14695981039346656037UL;
    foreach (char c in s) { h ^= (byte)c; h *= 1099511628211UL; }
    return unchecked((long)h);
}

static string TokPath(string sid) {
    string dir = Path.Combine(LocalAppData(), "StatusAI", "tokens");
    Directory.CreateDirectory(dir);
    return Path.Combine(dir, sid + ".bin");
}

static TokState TokLoad(string sid) {
    var st = new TokState();
    try {
        string p = TokPath(sid);
        if (!File.Exists(p)) return st;
        using var fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var br = new BinaryReader(fs, new UTF8Encoding(false));
        if (br.ReadInt32() != 0x324B5443) return new TokState();   // "CTK2"; CTK1 caches rebuild
        for (int i = 0; i < 5; i++) st.Main[i] = br.ReadInt64();
        for (int i = 0; i < 5; i++) st.Sub[i] = br.ReadInt64();
        int nf = br.ReadInt32();
        for (int i = 0; i < nf; i++) { string k = br.ReadString(); st.Off[k] = br.ReadInt64(); }
        int nh = br.ReadInt32();
        for (int i = 0; i < nh; i++) st.Seen.Add(br.ReadInt64());
        int nt = br.ReadInt32();
        for (int i = 0; i < nt; i++) st.SeenTool.Add(br.ReadInt64());
    } catch { return new TokState(); }   // unreadable cache just rebuilds
    return st;
}

static void TokSave(string sid, TokState st) {
    try {
        string p = TokPath(sid), tmp = p + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var bw = new BinaryWriter(fs, new UTF8Encoding(false))) {
            bw.Write(0x324B5443);
            for (int i = 0; i < 5; i++) bw.Write(st.Main[i]);
            for (int i = 0; i < 5; i++) bw.Write(st.Sub[i]);
            bw.Write(st.Off.Count);
            foreach (var kv in st.Off) { bw.Write(kv.Key); bw.Write(kv.Value); }
            bw.Write(st.Seen.Count);
            foreach (long h in st.Seen) bw.Write(h);
            bw.Write(st.SeenTool.Count);
            foreach (long h in st.SeenTool) bw.Write(h);
        }
        File.Move(tmp, p, true);   // atomic swap, so a concurrent render never sees a torn file
    } catch { }
}

// Two rows on one nine-column grid, split by direction: what left this machine on top
// (fresh prompt, prompt written to cache, tool calls issued), what came back below
// (generated output, prompt served from cache, totals). Columns 7-9 are outside that
// scheme: they are the summary, a count on top and a total below.
//
// The arrows are relative to us, not to the model: 🔺 left, 🔻 came back. 💾 and 📖 have
// no arrow, because a direction there invites reading it against the cache instead of the
// conversation. The two readings disagree, and the arrow added nothing there anyway, since
// every cache counter is an input token.
//
// Scope runs vertically: 🪵 main in columns 1/4/7, 🌿 sub in 2/5/8, 🌳 tree in 3/6/9, with
// the number colour to match, so a scope reads straight down without parsing a glyph.
//
// Every cell is [kind][scope] value. Icons align on a column's left edge, digits on its
// right. A trailing + on the grand total means the byte budget ran out.
static List<string> TokRender(TokState st, bool truncated, int avail) {
    long grand = 0;
    for (int i = 0; i < 4; i++) grand += st.Main[i] + st.Sub[i];
    if (grand == 0) return new List<string>();

    const string mn  = "\x1b[38;2;110;115;141m",    // main: dim
                 sb_ = "\x1b[38;2;180;190;254m",    // sub: lavender
                 tt  = "\x1b[1;38;2;125;207;255m",  // total: bold cyan
                 rst = "\x1b[0m";
    string[] hue = { mn, sb_, tt, mn, sb_, tt, mn, sb_, tt };

    long mAll = st.Main[0] + st.Main[1] + st.Main[2] + st.Main[3];
    long sAll = st.Sub[0]  + st.Sub[1]  + st.Sub[2]  + st.Sub[3];

    // sent above, returned below; a[] and b[] below already carry them in that order
    string[] ic1 = { "🔺🪵", "🔺🌿", "🔺🌳", "💾🪵", "💾🌿", "💾🌳", "🔧🪵", "🔧🌿", "🔧🌳" };
    string[] ic2 = { "🔻🪵", "🔻🌿", "🔻🌳", "📖🪵", "📖🌿", "📖🌳", "🪙🪵", "🪙🌿", "🪙🌳" };

    // emoji are two columns each, so icon width is declared rather than measured.
    // The +1 keeps at least one space between a cell's icons and its value.
    int[] iw = { 4, 4, 4, 4, 4, 4, 4, 4, 4 };
    var (nm1, nm2, cw) = TokNums(st, grand, truncated, TokFmt, iw);

    // A double-width emoji can't be aligned with the single-width glyphs that open every
    // other row: its ink is inset within a two-column advance, so the offset needed is
    // half a cell, and the grid only has whole cells. Leading with a single-width rule
    // makes character column and screen column the same number on every row.
    const string rule = "\x1b[38;2;110;115;141m│\x1b[0m";

    // 1 indent + 1 rule + 2 safety margin; gutters collapse to 2, at which point the
    // fixed value width above has already guaranteed the row fits
    int pad = Math.Max(2, (avail - 4 - cw.Sum()) / 8);
    return new List<string> { rule + TokRow(ic1, nm1, iw, cw, hue, pad, rst),
                              rule + TokRow(ic2, nm2, iw, cw, hue, pad, rst) };
}

static string TokRow(string[] ico, string[] num, int[] iw, int[] cw, string[] hue, int pad, string rst) {
    var sb = new StringBuilder();
    for (int i = 0; i < 9; i++) {
        if (i > 0) sb.Append(' ', pad);
        sb.Append(ico[i]).Append(' ', cw[i] - iw[i] - num[i].Length)
          .Append(hue[i]).Append(num[i]).Append(rst);
    }
    return sb.ToString();
}

// Builds both rows' value strings with one formatter, and the column widths they imply.
static (string[] a, string[] b, int[] cw) TokNums(TokState st, long grand, bool trunc,
                                                 Func<long, string> f, int[] iw) {
    long mAll = st.Main[0] + st.Main[1] + st.Main[2] + st.Main[3];
    long sAll = st.Sub[0]  + st.Sub[1]  + st.Sub[2]  + st.Sub[3];
    string[] a = { f(st.Main[0]), f(st.Sub[0]), f(st.Main[0] + st.Sub[0]),
                   f(st.Main[2]), f(st.Sub[2]), f(st.Main[2] + st.Sub[2]),
                   Nl(st.Main[4].ToString("N0")), Nl(st.Sub[4].ToString("N0")),
                   Nl((st.Main[4] + st.Sub[4]).ToString("N0")) };
    string[] b = { f(st.Main[1]), f(st.Sub[1]), f(st.Main[1] + st.Sub[1]),
                   f(st.Main[3]), f(st.Sub[3]), f(st.Main[3] + st.Sub[3]),
                   f(mAll), f(sAll), f(grand) + (trunc ? "+" : "") };
    int[] cw = new int[9];
    for (int i = 0; i < 9; i++) cw[i] = iw[i] + 1 + Math.Max(a[i].Length, b[i].Length);
    return (a, b, cw);
}

// Scaled unit, nl-NL notation, every value six characters: 0,200k  9,440k  241,0k
// 1,390M  44,60M  112,0G.
//
// A fixed number of significant figures can't give a fixed width: three of them take four
// characters at 9,44 and 41,7 but only three at 241, because once the integer part fills
// all three digits there is no comma. Padding with a space doesn't help either, since it
// is invisible and right-alignment already adds one. So the mantissa is pinned at five
// characters and the decimals float to fill it. That fixes the glyph count, and with it
// the column.
static string TokFmt(long n) {
    double v = n / 1000.0; string u = "k";
    if (v >= 1000) { v /= 1000; u = "M"; }
    if (v >= 1000) { v /= 1000; u = "G"; }
    string s = v >= 100 ? v.ToString("0.0") : v >= 10 ? v.ToString("0.00") : v.ToString("0.000");
    return Nl(s) + u;
}

sealed class TokState {
    public readonly Dictionary<string, long> Off = new(StringComparer.OrdinalIgnoreCase);
    public readonly HashSet<long> Seen = new();       // message.id, one per API response
    public readonly HashSet<long> SeenTool = new();   // toolu id, one per tool call
    public readonly long[] Main = new long[5];   // in, out, cache-write, cache-read, tool calls
    public readonly long[] Sub = new long[5];
}

// HasReset is carried explicitly rather than inferred from Hours or ResetsUnix being 0.
// The legacy five_hour/seven_day fallback shape has no reset time at all, and on 2026-08-18
// the API sent resets_at: null for weekly_scoped at 0% (on 2026-09-23, at 47%, it sent a
// time there). Both used to arrive as Hours 0, which the rows drew as "↻ 0m", a claim that
// the window resets this instant.
record struct Lim(int Pct, double Hours, bool HasReset, long ResetsUnix, string Sev);

// One meter as the server sent it: its kind, the label it is drawn or named under, what its
// scope names ("model", "surface", "model+surface" or ""), its numbers.
record struct Meter(string Kind, string Label, string Scope, Lim Lim);

// What one fetch yields: every meter in the server's order, and beside them the product
// breakdown (Bd, "CC=99;Chat=0;Cowork=1") and the on-credit alarm (Cr, or ""), each already
// reduced to what the screen shows.
sealed class Usage {
    public readonly List<Meter> Meters = new();
    public string Bd = "", Cr = "";
}

// What the registry caches from one fetch: the rows' figures, which every session draws at
// its own width, plus the breakdown, the credit alarm and the ignored meters (Ig, one
// description per line), which are laid out per render too, because where they fit depends
// on the render.
sealed record Cached(List<RowIn> Rows, string Bd, string Cr, string Ig) {
    public static readonly Cached None = new(new List<RowIn>(), "", "", "");
}

// One row's drawing inputs.
record struct RowIn(string Label, int Pct, double Hrs, bool HasReset, double Rate, bool Gated, string Sev);
