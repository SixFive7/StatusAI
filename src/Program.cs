using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

// `—` U+2014, the one sentinel for "this source reported nothing at all", as opposed to a
// source that reported zero. East-Asian Ambiguous, like every other glyph drawn in a
// metric row, so it is one column wide there and Vis()'s assumption holds. It is also
// never wider than the value it replaces, so no column it appears in can grow.
// A compile-time constant, so the static local functions below can use it.
const string NoData = "—";

// #28A428, forest green: the → time when the row's own reset comes before 100% would, so
// this window cannot run out at the current pace — harmless, where red is not. X11
// forestgreen #228B22 is 4,10:1 on the docs' #16161E and 3,54:1 where the 10%-opacity
// wallpaper is lightest, so it keeps that hue (120°) and saturation (61%) and is lifted from
// 34% to 40% lightness, the first step at 4,5:1 on every ground this terminal can show:
// 5,99 on Campbell #0C0C0C, 4,75 on the lightest wallpaper #242424, 5,51 on #16161E. Chroma
// 76 against the 41 of ↻'s #A6E3A1, and ΔE2000 21,7 from it and 27,3 from the reset time's
// #C6F6C1, so it reads as a greener, stronger green than either rather than a third shade.
const string Forest = "\x1b[38;2;40;164;40m";

string stdin;
using (var s = Console.OpenStandardInput())
using (var r = new StreamReader(s, Encoding.UTF8)) stdin = r.ReadToEnd();

string cshipOut = RunCship(stdin);

// A missing source and a genuine zero are the same number, so presence is tracked
// alongside every value that would otherwise render as 0. Nothing is inferred from the
// value itself: cost 0 on a fresh session is real and must keep rendering as $0,00.
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
    // Read only to tell "the field is missing" from "the field says 0" — the context bar
    // itself is drawn by cship from this same payload, not here.
    //
    // null is neither. Until the first API response of a context — every new session, and
    // again after /clear — Claude Code sends used_percentage and current_usage as null: its
    // documented "no messages yet". Both come from one lookup of the last response's usage
    // (read in the 2.1.234 and 2.1.280 bundles), so they are null together or not at all,
    // and that pair is a source honestly reporting nothing yet, like the cost of 0 above.
    // Any other shape — no context_window, no used_percentage, a null beside a current_usage
    // that holds a measurement, a value that is not a number — is still a missing source.
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

var (usage, usageErr) = GetUsage();
string metaLine = BuildMeta(costUsd, durMs, add, del, EurPerUsd(), haveCost, haveDur);
var (acctLine, signedIn) = BuildAccount();
// On a session seconds old the transcript may not exist yet, which is a race and not a
// fault, so BuildTokens is told to stay quiet about a missing file that long. 30 s is
// not a new threshold: BuildMeta rounds the duration to whole minutes, so this is the
// same boundary as the ⏱ 0m being displayed while the exception applies. A duration the
// payload never carried is not youth — it is a failed source in its own right, and buys
// a missing transcript nothing.
//
// Youth alone is too short, though. Claude Code writes the transcript at the first prompt,
// not at session start — every transcript's creation time is its first user record, on
// 2.1.233/234 and 2.1.275–280 alike — so a session left open for more than 30 s before
// anyone types has no file through no fault, and went red. While the payload says no
// messages yet, a missing file is therefore expected at any age, duration or none; once a
// context has been measured the file should exist, and past 30 s its absence is loud again.
bool youngSession = haveDur && durMs <= 30000;
var (tokenLines, tokenErr) = BuildTokens(transcriptPath, youngSession || noMessagesYet);

// One ⚠ row for every source that failed, so a blank figure always has a stated reason.
// A source that reported a genuine zero contributes nothing here.
var warns = new List<string>();
if (stdinErr.Length > 0) warns.Add(stdinErr);
if (usageErr.Length > 0) warns.Add(usageErr);
if (stdinErr.Length == 0) {
    if (!haveCost && !haveDur) warns.Add("cost, duration — no cost block in the payload");
    else if (!haveCost) warns.Add("cost — no cost.total_cost_usd in the payload");
    else if (!haveDur) warns.Add("duration — no cost.total_duration_ms in the payload");
    // cship draws the context bar from this field; with the field gone it draws an empty
    // bar at 0%, the same glyphs as a genuinely empty context and told apart only by colour
    // (a number takes the bar's style, anything else the default foreground). Saying so here
    // is the only thing this repo can do about it — the bar is not ours to change. "No
    // messages yet" is that genuinely empty context: no response has been measured, so its
    // 0% is the honest reading, and it says nothing.
    if (!haveCtx && !noMessagesYet) warns.Add("context — no context_window.used_percentage; the 0% bar is not real");
}
if (tokenErr.Length > 0) warns.Add(tokenErr);

// The host line is located before the ⚠ rows are laid out, so its absence can add a
// reason of its own to them. idx < 0 means every line cship returned was blank, which is
// exactly what cship does with a payload it cannot parse — and a payload that cannot be
// parsed is the case where a diagnostic is worth the most.
var lines = new List<string>(cshipOut.Replace("\r\n", "\n").Split('\n'));
int idx = -1;
for (int i = lines.Count - 1; i >= 0; i--) if (lines[i].Trim().Length > 0) { idx = i; break; }
if (idx < 0) warns.Add("cship — no output; its prompt and model lines are missing");

var warnLines = WarnRows(warns);
// A meter the usage API sent that this status line does not draw is a notice to review the
// code, not a failed source: a row of its own, in amber.
if (MetersRow(usage.Ig) is { Length: > 0 } metersRow) warnLines.Add(metersRow);
// Being on credit is not a failed source but an alarm, so it never shares a row with one:
// its own row, last, under every other reason.
if (usage.Cr.Length > 0) warnLines.Add(AlarmRow(usage.Cr));

// Every row of the block opens with a single-width glyph — the token rows with their
// │ rule, the metric rows with 5h/7d — so one indent serves them all and column 1 is
// column 1 on every line. The breakdown belongs to an account: with nobody signed in there
// is no account line to hang it on.
var block = new List<string>(tokenLines);
block.AddRange(Compose(usage.Val, acctLine, signedIn ? usage.Bd : "", warnLines));
if (idx >= 0) {
    lines[idx] = "\x1b[0m" + lines[idx];
    if (metaLine.Length > 0) lines[idx] += "   " + metaLine;
    lines.InsertRange(idx + 1, block.Select(l => "\x1b[0m " + l));
} else {
    // No host line to insert into. Emitting the block on its own is worth it because
    // almost none of it comes from stdin: the limit rows are the registry cache and the
    // API, the account line is the credentials file, and only the token rows and the meta
    // figures are lost — each of them already degrading to a sentinel with a stated
    // reason. Skipping the insert instead, as this did, printed nothing whatsoever, which
    // is the one output indistinguishable from the binary being uninstalled or dead.
    //
    // The meta segment normally rides on the host line, so here it takes the first row of
    // the block and opens with the — sentinel: the host line is a source that reported
    // nothing at all, which is exactly what that glyph means everywhere else. It is
    // single-width, so this row's left edge lands on column 1 like every other row's, and
    // indent + marker + space costs 3 of the 137 columns against the meta segment's
    // measured worst case of 130.
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
static List<string> Compose(string usageRows, string acct, string bd, List<string> errs) {
    const int gap = 2;
    var rows = usageRows.Length > 0 ? new List<string>(usageRows.Split('\n')) : new List<string>();
    var block = new List<string>();
    int term = TermWidth();
    if (rows.Count >= 2) {
        int rowW = Vis(rows[0]);
        int rightW = Math.Max(rows.Count > 2 ? Vis(rows[2]) : 0, acct.Length > 0 ? Vis(acct) + 1 : 0);
        if (term == 0 || 1 + rowW + gap + rightW <= term - 4) {
            string pad = new string(' ', gap);
            string right0 = acct.Length > 0
                ? WithBreakdown(acct, bd, term == 0 ? int.MaxValue : term - 4 - (1 + rowW + gap)) : "";
            string right1 = rows.Count > 2 ? rows[2] : "";
            block.Add(rows[0] + (right0.Length > 0 ? pad + right0 : ""));
            block.Add(rows[1] + (right1.Length > 0 ? pad + right1 : ""));
            for (int i = 3; i < rows.Count; i++) block.Add(rows[i]);
            block.AddRange(errs);
            return block;
        }
    }
    block.AddRange(rows);
    if (acct.Length > 0) block.Add(WithBreakdown(acct, bd, term == 0 ? int.MaxValue : term - 4 - 1));
    block.AddRange(errs);
    return block;
}

// The product breakdown after the account, in whole entries or not at all: every entry
// while they all fit, then without the 0% ones, then none — never wrapped, never cut inside
// an entry. `room` is what the line has left for the account; the account is charged as
// Compose charges it, Vis + 1, so the line passes the test the layout was decided by.
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

// Claude Code spawns the status line detached, so the attached console reports a
// phantom 120x30 default rather than the real terminal — OS detection is unusable.
// Measured value; override with CSHIP_WIDTH (settings.json "env") after a resize.
static int TermWidth() =>
    int.TryParse(Environment.GetEnvironmentVariable("CSHIP_WIDTH"), out int w) && w > 40 ? w : 141;

// CSHIP_OFFLINE names a directory to render from instead of the live machine, for the dev
// loop. Everything the status line normally shares with every running session is swapped
// for a file in it: the limit rows come from its usage.json and rows.json rather than the
// registry cache and the API, the euro rate from rows.json, and it stands in for the user
// profile, so the account line reads its .claude.json and the token cache lands in its
// .claude folder.
// HKCU\Software\cshipUsage is neither read nor written, the usage lock is never taken, and
// nothing is fetched — so a dev build run this way can neither serve a live session's
// cached render nor push a sample into the prediction history every session shares, which
// is what a plain test run on a live machine does.
static string? Offline() {
    string? d = Environment.GetEnvironmentVariable("CSHIP_OFFLINE");
    return string.IsNullOrEmpty(d) ? null : d;
}

static string Home() => Offline() ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

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
// processes. Losers render the last cached value instead of waiting on the network.
// The lock name is scoped per user + elevation level, so an ACL on a mutex created by a
// different security context can never deny us access. If the lock still fails, fetching
// is suspended and a loud error line is rendered — never an unguarded parallel fetch,
// which would corrupt the prediction history.
static (Cached c, string err) GetUsage() {
    if (Offline() is string dir) return OfflineRows(dir);
    long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    if (FreshVal(now) is Cached fresh) return (fresh, "");
    Mutex? mx = null; bool owned = false;
    try {
        mx = new Mutex(false, LockName());
        try { owned = mx.WaitOne(0); } catch (AbandonedMutexException) { owned = true; }
    } catch (Exception ex) {
        mx?.Dispose();
        return (AnyVal() ?? Cached.None, LockError(ex));
    }
    try {
        if (!owned) return (AnyVal() ?? Cached.None, "");
        now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (FreshVal(now) is Cached fresh2) return (fresh2, "");
        return (FetchAndRender(now) ?? AnyVal() ?? Cached.None, "");
    } finally {
        if (owned) { try { mx!.ReleaseMutex(); } catch { } }
        mx?.Dispose();
    }
}

// What GetUsage returns, from files instead of the machine. Two fixture shapes.
//
// usage.json beside rows.json: a usage response, run through the same ParseUsage as a live
// fetch at rows.json's "now", so the hours to each reset are fixed, with the forecast inputs
// a live fetch takes from the history given in rows.json by label. Which meters are drawn
// and which are flagged is the same Known() decision a live fetch makes, with "sn" as the
// scoped meter already being followed; a drawn meter the forecast leaves out is gated.
//
//   { "fx": 0.876, "now": "2026-09-23T20:14:20Z", "sn": "Fable",
//     "forecast": { "5h": { "rate": 28.8, "gated": false }, … } }
//
// rows.json alone: the rows RenderRows is given, as it lists them — what comes out of the
// fetch, the window checks and the slope — with no breakdown, no credit state and no
// ignored meters:
//
//   { "fx": 0.876,
//     "rows": [ { "label": "5h", "pct": 22, "hrs": 3.38, "hasReset": true,
//                 "rate": 10.4, "gated": false, "sev": "normal" }, … ] }
//
// Either way the forecast is an input, not recomputed from a history — what this path
// exists to test is the parse and the drawing.
static (Cached c, string err) OfflineRows(string dir) {
    try {
        using var d = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "rows.json")));
        var root = d.RootElement;
        string usagePath = Path.Combine(dir, "usage.json");
        if (File.Exists(usagePath)) {
            var utc = root.TryGetProperty("now", out var nw) && nw.ValueKind == JsonValueKind.String
                      && DateTimeOffset.TryParse(nw.GetString(), out var at) ? at : DateTimeOffset.UtcNow;
            using var ud = JsonDocument.Parse(File.ReadAllText(usagePath));
            var u = ParseUsage(ud.RootElement, utc);
            if (u.Meters.Count == 0) return (Cached.None, "offline — usage.json has no meters");
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
            return (new Cached(RenderRows(rows, 2), u.Bd, u.Cr, string.Join("\n", ignored)), "");
        }
        var list = new List<RowIn>();
        foreach (var r in root.GetProperty("rows").EnumerateArray())
            list.Add(new RowIn(r.GetProperty("label").GetString() ?? "", r.GetProperty("pct").GetInt32(),
                               r.GetProperty("hrs").GetDouble(), r.GetProperty("hasReset").GetBoolean(),
                               r.GetProperty("rate").GetDouble(), r.GetProperty("gated").GetBoolean(),
                               r.GetProperty("sev").GetString() ?? ""));
        if (list.Count == 0) return (Cached.None, "offline — rows.json lists no rows");
        return (new Cached(RenderRows(list, 2), "", "", ""), "");
    } catch (Exception ex) {
        // a broken fixture is loud, like every other failed source
        return (Cached.None, "offline — fixture unreadable: " + ex.GetType().Name + " " + Short(ex.Message));
    }
}

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
    return @"Global\cshipUsage.fetch." + sid + (adm ? ".adm" : ".std");
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

// A failed source must never be indistinguishable from a source that honestly reported
// nothing, so every reason is stated. They share one row separated by · while they fit —
// a single reason then renders exactly as the lock error always has — and take a row each
// once they do not, because a wrapped status line costs the same height as a split one
// and reads far worse.
static List<string> WarnRows(List<string> reasons) {
    const string red = "\x1b[1;38;2;247;118;142m", rst = "\x1b[0m";
    var outp = new List<string>();
    if (reasons.Count == 0) return outp;
    string one = string.Join(" · ", reasons);
    int budget = TermWidth() - 6;   // 4 host padding, 1 indent, 1 safety
    if (2 + one.Length <= budget) { outp.Add(red + "⚠ " + one + rst); return outp; }
    foreach (var r in reasons) outp.Add(red + "⚠ " + Short(r, budget - 2) + rst);
    return outp;
}

// The on-credit alarm: red like every ⚠ row, to the same budget, but always a row of its
// own and never joined by · — it is not a source that failed, it is usage being billed
// that the plan should have covered.
static string AlarmRow(string reason) {
    const string red = "\x1b[1;38;2;247;118;142m", rst = "\x1b[0m";
    int budget = TermWidth() - 6;   // 4 host padding, 1 indent, 1 safety
    return red + "⚠ " + Short(reason, budget - 2) + rst;
}

// The meters notice: a meter the usage API sent that this status line does not draw. Amber,
// because it asks for the code to be reviewed rather than reporting a failure or money; a
// row of its own to the ⚠ rows' budget. It names as many of the ignored meters as fit,
// whole, then counts the rest as "+N more"; if not even one name fits, it only counts them.
static string MetersRow(string ig) {
    const string amber = "\x1b[38;2;224;175;104m", rst = "\x1b[0m";
    var names = ig.Split('\n', StringSplitOptions.RemoveEmptyEntries);
    if (names.Length == 0) return "";
    int room = TermWidth() - 6 - 2;   // the ⚠ rows' budget, less "⚠ "
    string head = "meters — the usage API sent " + (names.Length == 1 ? "a meter" : names.Length + " meters")
                + " this status line ignores";
    const string tail = " · review cship-usage";
    for (int k = names.Length; k >= 1; k--) {
        string t = head + ": " + string.Join(", ", names.Take(k))
                 + (k < names.Length ? $" +{names.Length - k} more" : "") + tail;
        if (t.Length <= room) return amber + "⚠ " + t + rst;
    }
    return amber + "⚠ " + Short(head + tail, room) + rst;
}

// A reason has to fit on a status line. Exception text can carry a whole path — a runaway
// directory junction produced a 33 KB one under test, which tore the line apart — and a
// transcript_path is routinely past 100 characters. Head and tail are kept because the
// identifying part of a path is its end and the identifying part of a message is its
// start. `…` U+2026 is East-Asian Ambiguous, so it is one column like every other glyph
// here.
static string Short(string s, int max = 64) {
    if (max < 16) max = 16;   // TermWidth() floors at 41, so this only guards a future caller
    s = s.Replace('\n', ' ').Replace('\r', ' ');
    return s.Length <= max ? s : s.Substring(0, max - 13) + "…" + s.Substring(s.Length - 12);
}

// val is the rendered rows; bd, cr and ig are the breakdown, the credit alarm and the ignored
// meters the same fetch produced, cached beside them so that a cache hit draws everything a
// fetch would. A value written by a build that had none of them reads as none until the
// next fetch.
static Cached? FreshVal(long now) {
    try {
        using var rk = Registry.CurrentUser.OpenSubKey(@"Software\cshipUsage");
        if (rk?.GetValue("ts") is string ts && rk.GetValue("val") is string v
            && long.TryParse(ts, out long t) && now - t < 50)
            return new Cached(v, rk.GetValue("bd") as string ?? "", rk.GetValue("cr") as string ?? "",
                              rk.GetValue("ig") as string ?? "");
    } catch { }
    return null;
}

static Cached? AnyVal() {
    try {
        using var rk = Registry.CurrentUser.OpenSubKey(@"Software\cshipUsage");
        if (rk?.GetValue("val") is string v)
            return new Cached(v, rk.GetValue("bd") as string ?? "", rk.GetValue("cr") as string ?? "",
                              rk.GetValue("ig") as string ?? "");
    } catch { }
    return null;
}

static Cached? FetchAndRender(long now) {
    if (!Fetch(out var u) || u is null) return null;
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

    // the rows this status line draws, in the order it always has — 5h, 7d, the scoped row
    // — each with its own history series
    var rows = new List<RowIn>();
    foreach (var (i, which) in new[] { (iS, 0), (iW, 1), (iF, 2) }) {
        if (i < 0) continue;
        var m = u.Meters[i];
        double rate = Slope(hist, which, which == 0 ? vfS : which == 1 ? vfW : vfF, out bool gated);
        rows.Add(new RowIn(m.Label, m.Lim.Pct, m.Lim.Hours, m.Lim.HasReset, rate, gated, m.Lim.Sev));
    }
    var c = new Cached(RenderRows(rows, 2), u.Bd, u.Cr, string.Join("\n", ignored));
    Save(acct.Length > 0 ? acct : oldAcct, scopedName.Length > 0 ? scopedName : oldName,
         rsS, rsW, rsF, vfS, vfW, vfF, hist, c, now);
    return c;
}

// The three meters this status line draws — the session, weekly_all and one model-scoped
// weekly_scoped — and a description of every other meter the response carried. The scoped
// row stays with the meter the history has been following (sn) for as long as the server
// still sends it, and otherwise takes the highest, as it always has. Anything else — a kind
// never seen, a surface-scoped meter, a second model-scoped one — is not drawn until its code
// has been reviewed; MetersRow names it instead. A missing session or weekly_all only means
// one row fewer.
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
// what its scope names — enough to find it in the response when the code is reviewed.
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
        using var rk = Registry.CurrentUser.OpenSubKey(@"Software\cshipUsage");
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
    try { using var rk = Registry.CurrentUser.OpenSubKey(@"Software\cshipUsage"); return rk?.GetValue(name) as string ?? ""; } catch { return ""; }
}

static long RegLong(string name) => long.TryParse(RegStr(name), out long v) ? v : 0;

static void Save(string acct, string scopedName, long rsS, long rsW, long rsF, long vfS, long vfW, long vfF,
                 List<(long t, int s, int w, int f)> hist, Cached c, long now) {
    try {
        using var rk = Registry.CurrentUser.CreateSubKey(@"Software\cshipUsage");
        rk.SetValue("acct", acct);
        rk.SetValue("sn", scopedName);
        rk.SetValue("rsS", rsS.ToString()); rk.SetValue("rsW", rsW.ToString()); rk.SetValue("rsF", rsF.ToString());
        rk.SetValue("vfS", vfS.ToString()); rk.SetValue("vfW", vfW.ToString()); rk.SetValue("vfF", vfF.ToString());
        rk.SetValue("hist", string.Join(";", hist.Select(h => $"{h.t}:{h.s}:{h.w}:{h.f}")));
        rk.SetValue("val", c.Val);
        rk.SetValue("bd", c.Bd);
        rk.SetValue("cr", c.Cr);
        rk.SetValue("ig", c.Ig);
        rk.SetValue("ts", now.ToString());   // freshness gate: must be the last write
    } catch { }
}

// Theil-Sen: median of pairwise slopes, robust against a stray step the window
// invalidation missed. Gated until 4+ samples spanning 10+ minutes exist.
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

// per-bullet color by zone: 0-70 cyan, 70-90 yellow, 90-100 red, >100 blinking red
static string ZoneColor(int oneBasedBullet) {
    if (oneBasedBullet >= 11) return "\x1b[1;38;2;247;118;142m";
    if (oneBasedBullet == 10) return "\x1b[1;38;2;247;118;142m";
    if (oneBasedBullet >= 8) return "\x1b[38;2;224;175;104m";
    return "\x1b[38;2;125;207;255m";
}

// Ceiling, not rounding. Rounding made a bar stand still across the only boundary that
// matters: everything from 95% to 104% drew ten identical ●, so crossing 100 changed
// nothing and the ✗ overflow glyph needed 105% before it appeared. Ceiling lights a cell
// as soon as its tenth is entered, so 101% is visibly eleven cells and the first is an ✗.
// It over-reports every bar by design — 31% draws 4 of 10 where it drew 3 — which is the
// accepted trade: over-project rather than hide a crossing.
//
// Shared by Bar() and by the width solver in RenderRows, which must agree exactly: the
// solver pads a bar to a width it computes from this number, so two formulas would let
// the pad and the bar disagree and shift the second column.
static int BarFill(int pct) {
    int f = (int)Math.Ceiling(pct / 10.0);
    return f < 0 ? 0 : f;
}

// `muted` draws every cell dim — filled, ✗ and empty alike — for a bar that is shown but does
// not apply; the glyphs, and so the width, are exactly those of the coloured bar.
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
// padding wider than the current values require.
static string RenderRows(List<RowIn> rows, int leftCount) {
    var d = rows.Select(r => {
        int now = Math.Clamp(r.Pct, 0, 100);
        double rate = Math.Min(r.Rate, 40);
        bool burning = !r.Gated && rate > 0.5;
        // Project to the row's own reset, uncapped. An 8h ceiling made ⇢ mean "at reset"
        // on the 5h row and "in 8 hours" on the 7d row — one glyph, two meanings, one line
        // apart — so a red "hits 100% in 2d19h" could sit beside a calm ⇢ 20%.
        double horizon = r.Hrs;
        // Without a reset time there is no horizon, so there is no forecast to make: the
        // bar shows the current value and asserts nothing about where it is heading.
        int proj = burning && r.HasReset ? Math.Min((int)Math.Round(now + rate * horizon), 300) : now;
        // The → colour: 0 dim, 1 red, 2 forest.
        string to100; int tone = 0;
        if (r.Gated) to100 = "early";   // not enough same-window data for an honest trend yet
        else if (burning && now < 100) {
            double h = (100 - now) / rate;
            // Tested against the row's own reset. Red: 100% arrives before the window resets.
            // Forest: the reset comes first, so at this pace the window never runs out — the
            // time is kept and the colour says it is harmless. Neither is asserted when we do
            // not know when the window resets; the time then stays dim.
            if (r.HasReset) tone = h < r.Hrs ? 1 : 2;
            to100 = Hm(h);
        } else to100 = now >= 100 ? "maxed" : "never";
        return (label: r.Label, now, proj, reset: r.HasReset ? Hm(r.Hrs) : NoData, to100, tone, sev: r.Sev);
    }).ToList();
    // Every width is per column — [0] the left, [1] the right — and never shared between
    // the two. The left column's rows stack, 5h above 7d, so they have to agree to line up.
    // The right column holds the scoped row alone, beside 7d, and a row beside another has
    // nothing to line up with. The labels were always kept apart this way, so 5h / 7d never
    // inherit the width of a longer label like Fable; the other five widths were shared,
    // which aligned nothing and padded the lone right-hand row out to the widest value on
    // the left. A 7d projecting 226% draws 22 cells, so Fable's 24% sat fourteen blank
    // columns past the end of its own ten-cell bar, at the far end of the line, aligned with
    // nothing.
    int[] wLbl = new int[2], wNow = new int[2], wReset = new int[2], wTo100 = new int[2], wProj = new int[2];
    for (int i = 0; i < d.Count; i++) {
        int c = i < leftCount ? 0 : 1;
        wLbl[c] = Math.Max(wLbl[c], d[i].label.Length);
        wNow[c] = Math.Max(wNow[c], d[i].now.ToString().Length);
        wReset[c] = Math.Max(wReset[c], d[i].reset.Length);
        wTo100[c] = Math.Max(wTo100[c], d[i].to100.Length);
        wProj[c] = Math.Max(wProj[c], d[i].proj.ToString().Length);
    }

    // The now-bar's own width, computed per column and padded to below exactly as the
    // projection bar is. `now` is clamped to 0..100 above, so ceil(now/10) can never
    // exceed ten and capNowBar makes that a guarantee rather than an accident — an
    // eleventh cell here would shift everything to its right on that row alone, and
    // Compose() lays two rows side by side assuming the left column's rows are one
    // visible width.
    const int capNowBar = 10;
    int[] wNowBar = { 10, 10 };
    for (int i = 0; i < d.Count; i++) {
        int c = i < leftCount ? 0 : 1;
        wNowBar[c] = Math.Max(wNowBar[c], Math.Min(capNowBar, BarFill(d[i].now)));
    }
    static int Wider(int[] w) => Math.Max(w[0], w[1]);

    // Spend whatever width is left on overshoot markers. A two-column line costs
    // indent + leftRow + gap + rightRow; everything but the projection bars is known
    // here, so solve for the bar length that exactly fills the terminal. Recomputed each
    // render, so a wider reset/eta column shrinks the bars instead of wrapping.
    //
    // rowConst was 25 and carried the now-bar's ten cells inside it; the now-bar is now
    // an explicit term, so the constant drops by exactly ten and the total is unchanged.
    // Measured true value is 14 — nine spaces, ↻ → ⇢, two '%' — so 15 keeps the same
    // one-per-row conservatism the 25 had. It underfills by two columns and never
    // overflows.
    //
    // The solver still charges each of the five widths at the wider of the two columns —
    // exactly the max across all rows it charged when they were shared — so capBar is what
    // it always was, and so is the guarantee: a column's own widths can only be narrower.
    const int rowConst = 15;   // per-row glyphs/spaces outside label, numbers and bars
    int term = TermWidth();
    if (term <= 0) term = 138;
    // 4 = host padding, 1 = our indent, 2 = column gap
    int fixedPart = 4 + 1 + 2 + rowConst * 2 + wLbl[0] + wLbl[1]
                  + 2 * (Wider(wNow) + Wider(wReset) + Wider(wTo100) + Wider(wProj) + Wider(wNowBar));
    int capBar = Math.Clamp((term - 2 - fixedPart) / 2, 10, 30);   // -2 = safety margin
    // max over both bars' fills, so the projection column is never padded narrower than
    // something already drawn on the line — per column, like every other width
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
        // A row at 100% is blocked until its window resets, so where it is heading does not
        // apply for now. The ⇢ segment is kept, so the row keeps its shape and the pace stays
        // readable, but drawn wholly dim — the glyph, every bar cell including the ✗ marks,
        // and the percentage — the way a disabled control is greyed rather than removed.
        bool blocked = x.now >= 100;
        sb.Append($" {dim}⇢{rst} {Bar(x.proj, wBar[c], capBar, blocked)} {(blocked || x.proj <= 100 ? dim : red)}{x.proj.ToString().PadLeft(wProj[c])}%{rst}");
        outLines.Add(sb.ToString());
    }
    return string.Join("\n", outLines);
}

// haveCost / haveDur say whether the payload carried the field at all. A figure whose
// source is missing renders as — rather than as 0: the whole point of the meta segment is
// that a small number means a cheap session, and a silent 0 for "the field wasn't there"
// says exactly the opposite of the truth. A present 0 still renders $0,00, unchanged.
static string BuildMeta(double cost, long durMs, int add, int del, double eurRate, bool haveCost, bool haveDur) {
    // suppressed only when both sources genuinely reported nothing; an absent source has
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

// nl-NL notation: 1.234,56. InvariantGlobalization is on, so no culture is available at
// runtime to do this — every named culture silently resolves to invariant. Formatting
// invariant first and swapping the two separators gets there without the ICU dependency.
static string Nl(string s) {
    var sb = new StringBuilder(s.Length);
    foreach (char c in s) sb.Append(c == ',' ? '.' : c == '.' ? ',' : c);
    return sb.ToString();
}

// The API only ever reports USD. The ECB publishes a free daily reference feed with no
// key; it is EUR-based, so its USD rate is dollars per euro and has to be inverted.
// Cached for a day, so the network call happens at most once per day — and a missing or
// stale rate drops the euro half of the figure rather than blocking the render.
static double EurPerUsd() {
    if (Offline() is string dir) return OfflineFx(dir);
    long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    double cached = 0;
    try {
        using var rk = Registry.CurrentUser.OpenSubKey(@"Software\cshipUsage");
        if (rk?.GetValue("fx") is string fv
            && double.TryParse(fv, NumberStyles.Float, CultureInfo.InvariantCulture, out double c)) cached = c;
        if (cached > 0 && rk?.GetValue("fxTs") is string ts
            && long.TryParse(ts, out long t) && now - t < 86400) return cached;
    } catch { }
    double fresh = FetchEurPerUsd();
    if (fresh <= 0) return cached;
    try {
        using var rk = Registry.CurrentUser.CreateSubKey(@"Software\cshipUsage");
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

static bool Fetch(out Usage? u) {
    u = null;
    try {
        string home = Home();
        string credJson = File.ReadAllText(Path.Combine(home, ".claude", ".credentials.json"));
        string token;
        using (var cd = JsonDocument.Parse(credJson))
            token = cd.RootElement.GetProperty("claudeAiOauth").GetProperty("accessToken").GetString() ?? "";
        if (token.Length == 0) return false;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        var req = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/api/oauth/usage");
        req.Headers.Add("Authorization", "Bearer " + token);
        req.Headers.Add("anthropic-beta", "oauth-2025-04-20");
        req.Headers.Add("User-Agent", "claude-code/2.1.90");
        using var resp = http.Send(req);
        if (!resp.IsSuccessStatusCode) return false;
        string body;
        using (var rs = resp.Content.ReadAsStream())
        using (var sr = new StreamReader(rs, Encoding.UTF8)) body = sr.ReadToEnd();
        using var doc = JsonDocument.Parse(body);
        u = ParseUsage(doc.RootElement, DateTimeOffset.UtcNow);
        return u.Meters.Count > 0;
    } catch { return false; }
}

// Everything this binary draws from the usage response, measured against `utc`. Pure: the
// live fetch and the offline render both come through here, so a fixture exercises the
// same parse. A meter that cannot be read throws, and the fetch falls back to the cache as
// it always has; the breakdown and the credit state are read leniently instead, because a
// shape change there must not cost the meters.
static Usage ParseUsage(JsonElement root, DateTimeOffset utc) {
    var u = new Usage();
    if (root.TryGetProperty("limits", out var limits) && limits.ValueKind == JsonValueKind.Array) {
        // every meter, in the server's order — Known() then decides which are drawn, and the
        // rest are flagged, so nothing the server sends goes unmentioned
        foreach (var lim in limits.EnumerateArray()) {
            string kind = lim.TryGetProperty("kind", out var k) ? k.GetString() ?? "" : "";
            int pct = lim.TryGetProperty("percent", out var pc) ? (int)pc.GetDouble() : 0;
            // resets_at is a time on every kind in the 2026-08-15 and 2026-09-23 responses,
            // weekly_scoped included, but on 2026-08-18 weekly_scoped at 0% sent null, three
            // samples running. So "no reset time" is a state a payload can report — the
            // legacy shape below never has one — and it is carried as a flag rather than as
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
        // the legacy shape carries utilization but no reset time, so both rows are
        // genuinely unknown here rather than resetting in zero minutes
        u.Meters.Add(new Meter("session", "5h", "",
            new Lim((int)root.GetProperty("five_hour").GetProperty("utilization").GetDouble(), 0, false, 0, "")));
        u.Meters.Add(new Meter("weekly_all", "7d", "",
            new Lim((int)root.GetProperty("seven_day").GetProperty("utilization").GetDouble(), 0, false, 0, "")));
    }
    u.Bd = Breakdown(root);
    u.Cr = Credit(root, u.Meters);
    return u;
}

// 5h and 7d for the two the rows have always been, and the scope's name for the rest — the
// model it is for, else the surface — falling back to the kind itself, so a meter this
// binary has never seen can at least be named. With it, what the scope names: "model",
// "surface", "model+surface" or nothing — the model-scoped weekly_scoped is the only kind of
// scoped meter that is drawn.
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
// that sum to 100 — its window_started_at is exactly seven days before weekly_all's
// resets_at. Cached as "CC=99;Chat=0;Cowork=1", already cut to what may be shown: the three
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

// Usage billed beyond the plan, which the plan exists to prevent — so an alarm, not a
// figure. Two ways in: money already spent this period (spend.used.amount_minor or
// extra_usage.used_credits above 0), or a limit at 100% while credits are switched on
// (spend.enabled or extra_usage.is_enabled), the moment further usage starts to bill.
//
// The amount is spend.used where it has one — minor units, with the exponent and currency
// beside them — and otherwise extra_usage.used_credits, read as minor units too with
// decimal_places as the exponent: in the August response extra_usage.monthly_limit was 1000
// where spend.limit.amount_minor was 1000 at exponent 2, and no non-zero used_credits has been
// seen to confirm it.
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
// terminal: control characters — ESC and the row separator among them — become spaces.
static string Clean(string s) {
    var sb = new StringBuilder(s.Length);
    foreach (char ch in s) sb.Append(char.IsControl(ch) ? ' ' : ch);
    return sb.ToString().Trim();
}

// ───────────────────────── session token accounting ─────────────────────────
//
// Sub-agent turns are not in the main transcript: every child, at any depth, writes its
// own file under <sid>\subagents\, so a session total has to walk the tree. Two
// duplication hazards make a naive sum wrong — one API response is written once per
// content block (~2.2x), and child transcripts carry verbatim copies of ancestor records
// (measured up to 26x). uuid does not collapse the first, since every block carries its
// own. The one correct rule is a single global dedup on message.id across every file.
//
// Parsing is incremental: per-file byte offsets, running totals and the dedup set are
// cached, so a render only parses bytes appended since the last one. Cold cost on a
// fresh session is nil; the tail of the corpus runs to hundreds of MB, which is what
// the per-render byte budget below is for.

// Returns the rows and, separately, why there are none. Four of the five ways this
// produces nothing are failures of the source — no path, an unusable path, no files on
// disk, an exception — and only the fifth, a grand total of zero in TokRender, is a
// session that honestly has not spent a token yet. Rendering both as an absent row made
// them indistinguishable, so the failures now come back with a reason for the ⚠ row and
// the genuine zero still comes back silent.
//
// `transcriptMayBeAbsent` buys exactly one of those four an exception, on the argument
// below: a transcript that does not exist yet — on a session seconds old, or on one that
// has had no prompt — is not a fault, because Claude Code only writes the file at the first
// prompt. The other three are not explained by that and are never suppressed by it.
static (List<string> lines, string err) BuildTokens(string transcriptPath, bool transcriptMayBeAbsent) {
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
                files.Add((f, false));   // nested workflow agents live deeper — recurse
        // Nothing on disk to count — a fault, except before the first prompt. Claude Code
        // writes the transcript when the first prompt is sent, so a brand-new session has
        // no file for as long as it sits unused, with nothing wrong, and a red row on the
        // first frame of every session is noise — noise being how a warning row stops being
        // read at all. While the caller says the file may be absent — a session under 30 s
        // old, or a payload reporting no messages yet — this therefore renders exactly as a
        // genuine zero does: silently, with an empty grid. Otherwise the file should be
        // there, and its absence is stated as loudly as it was before.
        if (files.Count == 0)
            return (new List<string>(),
                    transcriptMayBeAbsent ? "" : "tokens — no transcript file '" + Short(transcriptPath) + "'");

        var st = TokLoad(sid);
        // A file shorter than its stored offset was rewritten rather than appended to.
        // Its message ids are already in the dedup set, so re-reading it would count
        // nothing — the only sound recovery is to rebuild the whole session from zero.
        foreach (var (path, _) in files) {
            if (!st.Off.TryGetValue(path, out long o)) continue;
            try { if (new FileInfo(path).Length < o) { st = new TokState(); break; } } catch { }
        }

        // Drop offsets for files that are no longer there. They cannot affect the totals,
        // which key off message and tool ids rather than the file table, but without this
        // the table only ever grows — a deleted session or a renamed transcript would sit
        // in the cache forever.
        var live = new HashSet<string>(files.Select(f => f.path), StringComparer.OrdinalIgnoreCase);
        foreach (var gone in st.Off.Keys.Where(k => !live.Contains(k)).ToList()) st.Off.Remove(gone);

        bool truncated = TokScan(files, st);
        TokSave(sid, st);
        // TokRender returns nothing when the grand total is zero. That is the one genuine
        // zero of the five, so it comes back with no reason attached.
        return (TokRender(st, truncated), "");
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
            if (used <= 0) break;   // no complete line in the window yet — a live write
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

        // Tool calls first, and deliberately so: the sibling records of one API response
        // all repeat its message.id, so the dedup below returns early on every record
        // after the first — which is exactly where the tool_use blocks live. Their own
        // toolu_ ids are globally unique, so they need no help from that dedup anyway.
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
    string dir = Path.Combine(Home(), ".claude", "statusline-tokens");
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
        File.Move(tmp, p, true);   // atomic swap — a concurrent render never sees a torn file
    } catch { }
}

// Two rows on one nine-column grid, split by direction of travel: what left this machine
// above (fresh prompt, prompt written to cache, tool calls issued), what came back below
// (generated output, prompt served from cache, totals). Columns 7-9 sit outside that
// metaphor — they are the summary group, a count above and a total below.
//
// Arrows are relative to us, not to the model: 🔺 left, 🔻 came back. 💾 and 📖 carry no
// arrow at all, because a direction on them invites reading it against the cache rather
// than against the conversation — the two frames disagree, and the arrow was redundant
// there anyway since every cache counter is an input token.
//
// Scope holds vertically: 🪵 main in columns 1/4/7, 🌿 sub in 2/5/8, 🌳 tree in 3/6/9,
// reinforced by the number colour, so a scope reads straight down without parsing a glyph.
//
// Cell grammar is uniform — [kind][scope] value. Icons align on a column's left edge,
// digits on its right. A trailing + on the grand total means the byte budget ran out.
static List<string> TokRender(TokState st, bool truncated) {
    long grand = 0;
    for (int i = 0; i < 4; i++) grand += st.Main[i] + st.Sub[i];
    if (grand == 0) return new List<string>();

    const string mn  = "\x1b[38;2;110;115;141m",    // main  — dim
                 sb_ = "\x1b[38;2;180;190;254m",    // sub   — lavender
                 tt  = "\x1b[1;38;2;125;207;255m",  // total — bold cyan
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
    int avail = TermWidth() - 4;                     // host padding
    var (nm1, nm2, cw) = TokNums(st, grand, truncated, TokFmt, iw);

    // A double-width emoji cannot be aligned with the single-width glyphs that open every
    // other row: its ink is inset within a two-column advance, so the offset needed is
    // half a cell and the grid is integral. Leading with a single-width rule makes
    // character column and screen column the same number on every row, exactly.
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

// Scaled unit, nl-NL notation, every value exactly six characters: 0,200k  9,440k
// 241,0k  1,390M  44,60M  112,0G.
//
// A fixed *significant-figure* count cannot give a fixed width — three of them occupy
// four characters at 9,44 and 41,7 but only three at 241, because once the integer part
// fills all three digits the comma has nowhere to sit. Padding with a space doesn't help
// either: it is invisible, and right-alignment was already inserting one. So the mantissa
// is pinned at five characters instead and the decimals float to fill it, which fixes the
// glyph count rather than merely the column.
static string TokFmt(long n) {
    double v = n / 1000.0; string u = "k";
    if (v >= 1000) { v /= 1000; u = "M"; }
    if (v >= 1000) { v /= 1000; u = "G"; }
    string s = v >= 100 ? v.ToString("0.0") : v >= 10 ? v.ToString("0.00") : v.ToString("0.000");
    return Nl(s) + u;
}

sealed class TokState {
    public readonly Dictionary<string, long> Off = new(StringComparer.OrdinalIgnoreCase);
    public readonly HashSet<long> Seen = new();       // message.id — one per API response
    public readonly HashSet<long> SeenTool = new();   // toolu id — one per tool call
    public readonly long[] Main = new long[5];   // in, out, cache-write, cache-read, tool calls
    public readonly long[] Sub = new long[5];
}

// HasReset is carried explicitly rather than inferred from Hours or ResetsUnix being 0.
// The legacy five_hour/seven_day fallback shape has no reset time at all, and on 2026-08-18
// the API sent resets_at: null for weekly_scoped at 0% (it sends a time there at 47% on
// 2026-09-23) — both used to arrive as Hours 0, which the rows rendered as "↻ 0m", an
// assertion that the window resets this instant.
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

// What the registry caches from one fetch: the rendered rows, plus the breakdown, the credit
// alarm and the ignored meters (Ig, one description per line), which are laid out per render
// because where they fit depends on the render.
sealed record Cached(string Val, string Bd, string Cr, string Ig) {
    public static readonly Cached None = new("", "", "", "");
}

// One row's drawing inputs.
record struct RowIn(string Label, int Pct, double Hrs, bool HasReset, double Rate, bool Gated, string Sev);
