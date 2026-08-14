using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

string stdin;
using (var s = Console.OpenStandardInput())
using (var r = new StreamReader(s, Encoding.UTF8)) stdin = r.ReadToEnd();

string cshipOut = RunCship(stdin);

double costUsd = 0; long durMs = 0; int add = 0, del = 0;
string transcriptPath = "";
try {
    using var d = JsonDocument.Parse(stdin);
    if (d.RootElement.TryGetProperty("cost", out var c) && c.ValueKind == JsonValueKind.Object) {
        if (c.TryGetProperty("total_cost_usd", out var v1)) costUsd = v1.GetDouble();
        if (c.TryGetProperty("total_duration_ms", out var v2)) durMs = v2.GetInt64();
        if (c.TryGetProperty("total_lines_added", out var v3)) add = v3.GetInt32();
        if (c.TryGetProperty("total_lines_removed", out var v4)) del = v4.GetInt32();
    }
    if (d.RootElement.TryGetProperty("transcript_path", out var tp) && tp.ValueKind == JsonValueKind.String)
        transcriptPath = tp.GetString() ?? "";
} catch { }

var (usageRows, usageErr) = GetUsage();
string metaLine = BuildMeta(costUsd, durMs, add, del, EurPerUsd());
string acctLine = BuildAccount();
var tokenLines = BuildTokens(transcriptPath);

var lines = new List<string>(cshipOut.Replace("\r\n", "\n").Split('\n'));
int idx = -1;
for (int i = lines.Count - 1; i >= 0; i--) if (lines[i].Trim().Length > 0) { idx = i; break; }
if (idx >= 0) {
    lines[idx] = "\x1b[0m" + lines[idx];
    if (metaLine.Length > 0) lines[idx] += "   " + metaLine;
    // Every inserted row now opens with a single-width glyph — the token rows with their
    // │ rule, the metric rows with 5h/7d — so one indent serves them all and column 1 is
    // column 1 on every line.
    var block = new List<string>(tokenLines);
    block.AddRange(Compose(usageRows, acctLine, usageErr));
    lines.InsertRange(idx + 1, block.Select(l => "\x1b[0m " + l));
}
lines.RemoveAll(l => l.Trim().Length == 0);
using (var os = Console.OpenStandardOutput())
using (var w = new StreamWriter(os, new UTF8Encoding(false))) w.Write(string.Join("\n", lines));
return;

// Two columns when the terminal has room: the scoped row sits right of 5h and the
// account right of 7d. Every metric row renders to the same visible width, so a
// fixed gap keeps both columns aligned. Falls back to stacked when too narrow.
static List<string> Compose(string usageRows, string acct, string err) {
    const int gap = 2;
    var rows = usageRows.Length > 0 ? new List<string>(usageRows.Split('\n')) : new List<string>();
    var block = new List<string>();
    if (rows.Count >= 2) {
        int rowW = Vis(rows[0]);
        int rightW = Math.Max(rows.Count > 2 ? Vis(rows[2]) : 0, acct.Length > 0 ? Vis(acct) + 1 : 0);
        int term = TermWidth();
        if (term == 0 || 1 + rowW + gap + rightW <= term - 4) {
            string pad = new string(' ', gap);
            string right0 = acct, right1 = rows.Count > 2 ? rows[2] : "";
            block.Add(rows[0] + (right0.Length > 0 ? pad + right0 : ""));
            block.Add(rows[1] + (right1.Length > 0 ? pad + right1 : ""));
            for (int i = 3; i < rows.Count; i++) block.Add(rows[i]);
            if (err.Length > 0) block.Add(err);
            return block;
        }
    }
    block.AddRange(rows);
    if (acct.Length > 0) block.Add(acct);
    if (err.Length > 0) block.Add(err);
    return block;
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
static (string rows, string err) GetUsage() {
    long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    if (FreshVal(now) is string fresh) return (fresh, "");
    Mutex? mx = null; bool owned = false;
    try {
        mx = new Mutex(false, LockName());
        try { owned = mx.WaitOne(0); } catch (AbandonedMutexException) { owned = true; }
    } catch (Exception ex) {
        mx?.Dispose();
        return (AnyVal() ?? "", LockError(ex));
    }
    try {
        if (!owned) return (AnyVal() ?? "", "");
        now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (FreshVal(now) is string fresh2) return (fresh2, "");
        return (FetchAndRender(now) ?? AnyVal() ?? "", "");
    } finally {
        if (owned) { try { mx!.ReleaseMutex(); } catch { } }
        mx?.Dispose();
    }
}

static string LockName() {
    using var id = WindowsIdentity.GetCurrent();
    string sid = id.User?.Value ?? "nosid";
    bool adm = new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    return @"Global\cshipUsage.fetch." + sid + (adm ? ".adm" : ".std");
}

static string LockError(Exception ex) {
    string why = ex switch {
        WaitHandleCannotBeOpenedException => "the lock name is occupied by a foreign kernel object that is not a mutex",
        UnauthorizedAccessException => "access to the usage lock was denied; it was created by a different security context",
        IOException => "the usage lock could not be opened due to an I/O error",
        _ => "unexpected " + ex.GetType().Name + " while opening the usage lock"
    };
    return "\x1b[1;38;2;247;118;142m⚠ usage tracking suspended — " + why + "\x1b[0m";
}

static string? FreshVal(long now) {
    try {
        using var rk = Registry.CurrentUser.OpenSubKey(@"Software\cshipUsage");
        if (rk?.GetValue("ts") is string ts && rk.GetValue("val") is string v
            && long.TryParse(ts, out long t) && now - t < 50) return v;
    } catch { }
    return null;
}

static string? AnyVal() {
    try { using var rk = Registry.CurrentUser.OpenSubKey(@"Software\cshipUsage"); return rk?.GetValue("val") as string; } catch { }
    return null;
}

static string? FetchAndRender(long now) {
    if (!Fetch(out var sess, out var week, out var scoped, out string scopedName)
        || sess is null || week is null) return null;
    string acct = AccountInfo().uuid;
    var hist = LoadHist();
    string oldAcct = RegStr("acct"), oldName = RegStr("sn");
    long vfS = RegLong("vfS"), vfW = RegLong("vfW"), vfF = RegLong("vfF");
    long rsS = RegLong("rsS"), rsW = RegLong("rsW"), rsF = RegLong("rsF");

    if (acct.Length > 0 && oldAcct.Length > 0 && acct != oldAcct) {
        hist.Clear(); vfS = vfW = vfF = now;   // account switch: the whole series is foreign
    }
    // the scoped limit tracks a different model than before: its series is foreign too
    if (scopedName.Length > 0 && oldName.Length > 0 && scopedName != oldName) vfF = now;
    var last = hist.Count > 0 ? hist[^1] : (t: 0L, s: -1, w: -1, f: -1);
    WindowCheck(sess.Value, ref rsS, ref vfS, last.s, now);
    WindowCheck(week.Value, ref rsW, ref vfW, last.w, now);
    if (scoped is Lim slim) WindowCheck(slim, ref rsF, ref vfF, last.f, now);

    int s = sess.Value.Pct, w = week.Value.Pct, f = scoped?.Pct ?? -1;
    if (!(hist.Count > 0 && last.t == now && last.s == s && last.w == w && last.f == f))
        hist.Add((now, s, w, f));
    hist = hist.Where(h => now - h.t <= 3600 && h.t <= now).ToList();

    double rateS = Slope(hist, 0, vfS, out bool gS);
    double rateW = Slope(hist, 1, vfW, out bool gW);
    bool gF = true; double rateF = 0;
    if (scoped is not null) rateF = Slope(hist, 2, vfF, out gF);

    var rows = new List<(string label, int pct, double hrs, double rate, bool gated, string sev)> {
        ("5h", s, sess.Value.Hours, rateS, gS, sess.Value.Sev),
        ("7d", w, week.Value.Hours, rateW, gW, week.Value.Sev)
    };
    if (scoped is Lim sc)
        rows.Add((scopedName.Length > 0 ? scopedName : "scoped", sc.Pct, sc.Hours, rateF, gF, sc.Sev));
    string val = RenderRows(rows, 2);
    Save(acct.Length > 0 ? acct : oldAcct, scopedName.Length > 0 ? scopedName : oldName,
         rsS, rsW, rsF, vfS, vfW, vfF, hist, val, now);
    return val;
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
        string p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json");
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
        string p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ".credentials.json");
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

static string BuildAccount() {
    string dim = "\x1b[38;2;110;115;141m", txt = "\x1b[38;2;169;177;214m", rst = "\x1b[0m";
    string email = AccountInfo().email;
    if (email.Length == 0) return $"{dim}👤 not signed in{rst}";
    var sb = new StringBuilder();
    sb.Append($"👤 {txt}{email}{rst}");
    string plan = Plan();
    if (plan.Length > 0) sb.Append($" {dim}· {plan}{rst}");
    return sb.ToString();
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
                 List<(long t, int s, int w, int f)> hist, string val, long now) {
    try {
        using var rk = Registry.CurrentUser.CreateSubKey(@"Software\cshipUsage");
        rk.SetValue("acct", acct);
        rk.SetValue("sn", scopedName);
        rk.SetValue("rsS", rsS.ToString()); rk.SetValue("rsW", rsW.ToString()); rk.SetValue("rsF", rsF.ToString());
        rk.SetValue("vfS", vfS.ToString()); rk.SetValue("vfW", vfW.ToString()); rk.SetValue("vfF", vfF.ToString());
        rk.SetValue("hist", string.Join(";", hist.Select(h => $"{h.t}:{h.s}:{h.w}:{h.f}")));
        rk.SetValue("val", val);
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

static string Bar(int pct, int padTo = 0, int cap = 15) {
    int fill = (int)Math.Round(pct / 10.0, MidpointRounding.AwayFromZero);
    if (fill < 0) fill = 0;
    int len = Math.Min(cap, Math.Max(10, fill));
    var sb = new StringBuilder();
    string cur = "";
    for (int i = 1; i <= len; i++) {
        bool on = i <= fill;
        string col = on ? ZoneColor(i) : "\x1b[38;2;110;115;141m";
        if (col != cur) { sb.Append(col); cur = col; }
        sb.Append(on ? (i >= 11 ? '✗' : '●') : '○');
    }
    sb.Append("\x1b[0m");
    if (padTo > len) sb.Append(' ', padTo - len);
    return sb.ToString();
}

static string PctColor(int p) =>
    p >= 90 ? "\x1b[1;38;2;247;118;142m" : p >= 70 ? "\x1b[38;2;224;175;104m" : "\x1b[38;2;125;207;255m";

// column widths are the max needed across rows this render, so rows stay
// aligned while never padding wider than the current values require
static string SevColor(string sev) =>
    sev == "critical" ? "\x1b[1;38;2;247;118;142m" : sev == "warning" ? "\x1b[38;2;224;175;104m" : "";

// leftCount = rows that stack in the left column; labels only pad against the
// rows they sit above, so "5h"/"7d" don't inherit the width of a longer scoped label
static string RenderRows(List<(string label, int pct, double hrs, double rate, bool gated, string sev)> rows, int leftCount) {
    var d = rows.Select(r => {
        int now = Math.Clamp(r.pct, 0, 100);
        double rate = Math.Min(r.rate, 40);
        bool burning = !r.gated && rate > 0.5;
        double horizon = Math.Min(r.hrs, 8.0);
        int proj = burning ? Math.Min((int)Math.Round(now + rate * horizon), 300) : now;
        string to100; bool overshoot = false;
        if (r.gated) to100 = "early";   // not enough same-window data for an honest trend yet
        else if (burning && now < 100) {
            double h = (100 - now) / rate;
            overshoot = h < r.hrs;
            to100 = Hm(h);
        } else to100 = now >= 100 ? "maxed" : "never";
        return (r.label, now, proj, reset: Hm(r.hrs), to100, overshoot, r.sev);
    }).ToList();
    int wLeft = 0, wRight = 0;
    for (int i = 0; i < d.Count; i++) {
        if (i < leftCount) wLeft = Math.Max(wLeft, d[i].label.Length);
        else wRight = Math.Max(wRight, d[i].label.Length);
    }
    int wNow = d.Max(x => x.now.ToString().Length);
    int wReset = d.Max(x => x.reset.Length);
    int wTo100 = d.Max(x => x.to100.Length);
    int wProj = d.Max(x => x.proj.ToString().Length);

    // Spend whatever width is left on overshoot markers. A two-column line costs
    // indent + leftRow + gap + rightRow; everything but the two bars is known here,
    // so solve for the bar length that exactly fills the terminal. Recomputed each
    // render, so a wider reset/eta column shrinks the bars instead of wrapping.
    const int rowConst = 25;   // per-row glyphs/spaces outside label + numeric columns
    int term = TermWidth();
    if (term <= 0) term = 138;
    // 4 = host padding, 1 = our indent, 2 = column gap
    int fixedPart = 4 + 1 + 2 + rowConst * 2 + wLeft + wRight + 2 * (wNow + wReset + wTo100 + wProj);
    int capBar = Math.Clamp((term - 2 - fixedPart) / 2, 10, 30);   // -2 = safety margin
    int wBar = 10;
    foreach (var x in d) {
        int fill = (int)Math.Round(x.proj / 10.0, MidpointRounding.AwayFromZero);
        wBar = Math.Max(wBar, Math.Min(capBar, Math.Max(10, fill)));
    }
    string dim = "\x1b[38;2;110;115;141m", red = "\x1b[1;38;2;247;118;142m", rst = "\x1b[0m";
    var outLines = new List<string>();
    for (int i = 0; i < d.Count; i++) {
        var x = d[i];
        var sb = new StringBuilder();
        string lbl = x.label.PadRight(i < leftCount ? wLeft : wRight);
        string sevc = SevColor(x.sev);
        sb.Append(sevc.Length > 0 ? $"{sevc}{lbl}{rst}" : lbl);
        sb.Append($" {Bar(x.now)} {PctColor(x.now)}{x.now.ToString().PadLeft(wNow)}%{rst}");
        sb.Append($" \x1b[38;2;166;227;161m↻{rst} \x1b[38;2;198;246;193m{x.reset.PadRight(wReset)}{rst}");
        sb.Append($" {(x.overshoot ? red : dim)}→ {x.to100.PadRight(wTo100)}{rst}");
        sb.Append($" {dim}⇢{rst} {Bar(x.proj, wBar, capBar)} {(x.proj > 100 ? red : dim)}{x.proj.ToString().PadLeft(wProj)}%{rst}");
        outLines.Add(sb.ToString());
    }
    return string.Join("\n", outLines);
}

static string BuildMeta(double cost, long durMs, int add, int del, double eurRate) {
    if (durMs <= 0 && cost <= 0) return "";
    double hours = durMs / 3600000.0;
    var sb = new StringBuilder();
    const string txt = "\x1b[38;2;169;177;214m", dim = "\x1b[38;2;110;115;141m", rst = "\x1b[0m";
    sb.Append($"\x1b[38;2;180;190;254m⏱ {Hm(hours)}{rst}");
    if (add > 0 || del > 0) sb.Append($"   📝 \x1b[38;2;166;227;161m+{add}{rst} \x1b[38;2;247;118;142m-{del}{rst}");
    if (hours > 0.02) {
        sb.Append($"   {txt}💸 ${Nl((cost / hours).ToString("N2"))}");
        if (eurRate > 0) sb.Append($"{dim}/{txt}€{Nl((cost / hours * eurRate).ToString("N2"))}");
        sb.Append($"/h{rst}");
    }
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

static bool Fetch(out Lim? sess, out Lim? week, out Lim? scoped, out string scopedName) {
    sess = null; week = null; scoped = null; scopedName = "";
    try {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
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
        var root = doc.RootElement;
        var utc = DateTimeOffset.UtcNow;
        if (root.TryGetProperty("limits", out var limits) && limits.ValueKind == JsonValueKind.Array) {
            foreach (var lim in limits.EnumerateArray()) {
                string kind = lim.TryGetProperty("kind", out var k) ? k.GetString() ?? "" : "";
                int pct = lim.TryGetProperty("percent", out var pc) ? (int)pc.GetDouble() : 0;
                double hrs = 0; long rsu = 0;
                if (lim.TryGetProperty("resets_at", out var ra) && ra.ValueKind == JsonValueKind.String
                    && DateTimeOffset.TryParse(ra.GetString(), out var rdt)) {
                    hrs = Math.Max(0, (rdt - utc).TotalHours); rsu = rdt.ToUnixTimeSeconds();
                }
                string sev = lim.TryGetProperty("severity", out var sv) ? sv.GetString() ?? "" : "";
                string model = "";
                if (lim.TryGetProperty("scope", out var sc) && sc.ValueKind == JsonValueKind.Object
                    && sc.TryGetProperty("model", out var mo) && mo.ValueKind == JsonValueKind.Object
                    && mo.TryGetProperty("display_name", out var dn)) model = dn.GetString() ?? "";
                var l = new Lim(pct, hrs, rsu, sev);
                if (kind == "session") sess = l;
                else if (kind == "weekly_all") week = l;
                // one scoped row is displayed; if several ever appear, the highest wins
                else if (kind == "weekly_scoped" && (scoped is null || pct > scoped.Value.Pct)) {
                    scoped = l; scopedName = model;
                }
            }
            return sess is not null && week is not null;
        }
        sess = new Lim((int)root.GetProperty("five_hour").GetProperty("utilization").GetDouble(), 0, 0, "");
        week = new Lim((int)root.GetProperty("seven_day").GetProperty("utilization").GetDouble(), 0, 0, "");
        return true;
    } catch { return false; }
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

static List<string> BuildTokens(string transcriptPath) {
    if (transcriptPath.Length == 0) return new List<string>();
    try {
        string dir = Path.GetDirectoryName(transcriptPath) ?? "";
        string sid = Path.GetFileNameWithoutExtension(transcriptPath);
        if (dir.Length == 0 || sid.Length == 0) return new List<string>();

        var files = new List<(string path, bool main)>();
        if (File.Exists(transcriptPath)) files.Add((transcriptPath, true));
        string subs = Path.Combine(dir, sid, "subagents");
        if (Directory.Exists(subs))
            foreach (var f in Directory.EnumerateFiles(subs, "agent-*.jsonl", SearchOption.AllDirectories)
                                       .OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                files.Add((f, false));   // nested workflow agents live deeper — recurse
        if (files.Count == 0) return new List<string>();

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
        return TokRender(st, truncated);
    } catch { return new List<string>(); }
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
    string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                              ".claude", "statusline-tokens");
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

record struct Lim(int Pct, double Hours, long ResetsUnix, string Sev);
