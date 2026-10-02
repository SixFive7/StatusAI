using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

// The Stream Deck key. The app starts this same statusai.exe as a plugin, from the plugin's own
// folder, and the process then stays up for as long as the app does: it draws the 5h and 7d rows
// on a key, starts a Claude Code tab or window when the key is pressed, and keeps the shared usage
// fresh while Claude is working and no terminal's status line is doing so.
//
// It waits and never polls. Three things can wake it: a message from the app on its websocket, a
// change to the registry key the limit rows are cached in, and a write anywhere under
// ~/.claude/projects, where every session of every kind keeps its transcripts. It makes no fetch
// of its own: when the usage is due it starts this exe again with --refresh, which goes through
// GetUsage() like any render, so the 50 second cache, the lock and the count of failures are the
// status line's. See docs/reference/architecture.md.
static class Deck {
    // What it takes from Program.cs, whose functions are local to its top-level code.
    public sealed class Host {
        public required Func<(Cached c, string err, string fetchErr)> Look;   // the rows as they stand, without a fetch
        public required Func<long> FetchedAt;                                 // unix seconds of the last good fetch, 0 offline
        public required Func<long> TriedAt;                                   // and of the last attempt
        public required Func<RowIn, (int now, int proj, string to100, int tone)> Pace;
        public required Func<double, string> Hm;
        public required Func<int, int> BarFill;
        public required Func<int, string> ZoneColor, PctColor;
        public required Func<string, string> SevColor;
        public required string Forest;
        public required string? Offline;                                      // STATUSAI_OFFLINE: nothing live is touched
        public required string ProjectsDir;
    }

    // a key that is down and has not acted yet
    sealed class Hold { public string Ctx = ""; public long Since; }

    const int HoldMs = 500;          // a key held this long is a long press, and acts while still down
    const int SlotSeconds = 62;      // while Claude works: one refresh a minute, just behind a terminal's own 60 s
    const int FreshSeconds = 50;     // GetUsage() fetches nothing while the shared copy is younger than this
    const int IdleSeconds = 300;     // no fetch for this long means nothing has been running: a pace says nothing
    const int SettleMs = 100;        // a fetch writes its values one by one; read them once they have all landed
    const int MaxMessage = 16 * 1024 * 1024;

    // ---------------------------------------------------------------- the way in

    public static bool IsPluginStart(string[] a) =>
        Arg(a, "-port").Length > 0 && Arg(a, "-pluginUUID").Length > 0 && Arg(a, "-registerEvent").Length > 0;

    static string Arg(string[] a, string name) {
        for (int i = 0; i + 1 < a.Length; i++) if (a[i] == name) return a[i + 1];
        return "";
    }

    // 0 when the app closed the connection, 1 after anything else. Either way the process ends when
    // the socket does: with no app on the other side there is nothing to draw on, and an app that
    // is still there starts its plugin again.
    public static int Run(string[] args, Host h) {
        // This is a console program, so the app gives it a console host of its own (conhost.exe,
        // 7 MB) that nothing here writes to. The standard handles are the app's pipes: keep them out
        // of the programs a key press starts, which would hold them for as long as a terminal runs.
        FreeConsole();
        foreach (int std in new[] { -10, -11, -12 }) SetHandleInformation(GetStdHandle(std), 1, 0);

        Ws? ws = null;
        try {
            if (!int.TryParse(Arg(args, "-port"), NumberStyles.None, CultureInfo.InvariantCulture, out int port)) return 1;
            ws = Ws.Connect(port);
            ws.Send("{\"event\":" + Quote(Arg(args, "-registerEvent")) + ",\"uuid\":" + Quote(Arg(args, "-pluginUUID")) + "}");
            return Loop(ws, h);
        } catch (Exception ex) {
            // Nothing is logged to a file of its own. The app keeps a log per plugin, and one line at
            // the start and one for anything fatal is what a failure after install is read from.
            try { ws?.Send(Log("fatal: " + ex.GetType().Name + ": " + ex.Message)); } catch { }
            return 1;
        } finally { ws?.Dispose(); }
    }

    // Which build this is: its version, and when its file was written, which a copy keeps. That is
    // enough to tell the plugin's copy from the status line's, and reading the product version with
    // its commit would cost the binary 12 kB.
    static string Version() {
        string v = typeof(Deck).Assembly.GetName().Version?.ToString(3) ?? "?";
        try { return v + " of " + File.GetLastWriteTime(Environment.ProcessPath!).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture); }
        catch { return v; }
    }

    // ---------------------------------------------------------------- the loop

    static int Loop(Ws ws, Host h) {
        bool offline = h.Offline is not null;
        using var inbox = new Inbox(ws);
        CacheWatch? cache = null;     // the limit rows changed: a terminal's status line fetched, or --refresh did
        DirWatch? work = null;        // Claude is working: something under ~/.claude/projects was written
        var shown = new Dictionary<string, string>();     // each visible key, by the app's context, and the face it shows
        var held = new List<Hold>();                      // keys that are down and have not acted yet, and since when
        bool active = false; long activeSince = 0;        // a write was seen that no refresh has covered yet
        long refreshedAt = 0;                             // when this process last asked for a refresh
        long settleAt = 0;                                // the cache changed: draw it at this tick

        try {
            bool said = false;
            while (true) {
                // A watch that could not be opened is tried again whenever the loop comes round: the
                // registry key is only there once a status line has fetched, the folder once Claude Code has run.
                if (cache is null && !offline) cache = CacheWatch.Open();
                if (work is null) work = DirWatch.Open(h.ProjectsDir);
                // said once both watches have had their first chance, so a write that follows it is seen
                if (!said) { said = true; ws.Send(Log("statusai " + Version() + " started as the Stream Deck plugin" + (offline ? ", offline" : ""))); }

                long now = Now(), tick = Environment.TickCount64;
                long wait = long.MaxValue;
                foreach (Hold k in held) wait = Math.Min(wait, k.Since + HoldMs - tick);
                if (settleAt > 0) wait = Math.Min(wait, settleAt - tick);
                if (active && shown.Count > 0) wait = Math.Min(wait, (RefreshDue(h, activeSince, refreshedAt) - now) * 1000);
                // the countdowns on the key read in minutes: one look on each minute while a key shows
                if (shown.Count > 0) wait = Math.Min(wait, (60 - DateTime.Now.Second) * 1000L + 50);

                // The folder's handle stays signalled until it is armed again, so a burst of a thousand
                // writes is one wake-up, and once a refresh is due it is left out of the wait altogether.
                var waits = new WaitHandle[1 + (cache is not null && settleAt == 0 ? 1 : 0) + (work is not null && !active ? 1 : 0)];
                int iCache = -1, iWork = -1, n = 0;
                waits[n++] = inbox.Signal;
                if (cache is not null && settleAt == 0) { iCache = n; waits[n++] = cache.Changed; }
                if (work is not null && !active) { iWork = n; waits[n++] = work.Changed; }
                int got = WaitHandle.WaitAny(waits, wait == long.MaxValue ? -1 : (int)Math.Clamp(wait, 0, int.MaxValue));
                now = Now(); tick = Environment.TickCount64;
                bool redraw = got == WaitHandle.WaitTimeout;

                if (got == 0) {
                    while (inbox.Take(out string? m)) {
                        if (m is null) return inbox.End;      // the socket has closed: see Run
                        redraw |= OnMessage(ws, h, m, shown, held, tick);
                    }
                } else if (got == iCache) {
                    settleAt = tick + SettleMs;
                } else if (got == iWork) {
                    active = true; activeSince = now;
                }

                if (settleAt > 0 && tick >= settleAt) { settleAt = 0; cache!.Arm(); redraw = true; }

                for (int i = held.Count - 1; i >= 0; i--) {
                    if (tick - held[i].Since < HoldMs) continue;
                    string ctx = held[i].Ctx;
                    held.RemoveAt(i);                         // its release, when it comes, finds nothing to do
                    Press(ws, h, ctx, hold: true);
                }

                if (redraw && shown.Count > 0) Draw(ws, h, shown, now);

                // No refresh while no key is visible: there is nothing to draw it on. The write stays noted.
                if (active && shown.Count > 0 && now >= RefreshDue(h, activeSince, refreshedAt)) {
                    refreshedAt = now;
                    // Offline nothing is fetched: the refresh is reported instead.
                    if (offline) ws.Send(Log("offline: would run --refresh"));
                    // under 50 s old: a terminal's status line has just fetched, and --refresh would find nothing to do
                    else if (now - h.FetchedAt() >= FreshSeconds) Refresh(ws);
                    active = false;
                    // Listen again from here. Writes made since the last signal are reported at once, so
                    // the last write of a burst is followed by one more refresh, a minute on, then nothing.
                    work!.Arm();
                }
            }
        } finally { cache?.Dispose(); work?.Dispose(); }
    }

    // When the usage may next be brought up to date: at once after a quiet spell, and otherwise a
    // slot after the last fetch or attempt by anyone, this process included.
    static long RefreshDue(Host h, long activeSince, long refreshedAt) =>
        Math.Max(activeSince, Math.Max(refreshedAt, Math.Max(h.FetchedAt(), h.TriedAt())) + SlotSeconds);

    static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    // One message from the app. True when the key has to be drawn again.
    static bool OnMessage(Ws ws, Host h, string m, Dictionary<string, string> shown, List<Hold> held, long tick) {
        string ev, ctx, payload = "";
        try {
            using var d = JsonDocument.Parse(m);
            ev = Str(d.RootElement, "event"); ctx = Str(d.RootElement, "context");
            if (ev == "sendToPlugin" && d.RootElement.TryGetProperty("payload", out var p)) payload = p.GetRawText();
        } catch (JsonException) { return false; }             // not something the app sends; let it pass
        switch (ev) {
            case "willAppear": shown[ctx] = ""; return true;
            case "willDisappear": shown.Remove(ctx); held.RemoveAll(k => k.Ctx == ctx); return false;
            case "keyDown":
                held.RemoveAll(k => k.Ctx == ctx);
                held.Add(new Hold { Ctx = ctx, Since = tick });
                return false;
            case "keyUp":
                // released before the hold ran out: a short press, which acts on the release
                if (held.RemoveAll(k => k.Ctx == ctx) > 0) Press(ws, h, ctx, hold: false);
                return false;
            case "systemDidWakeUp": return true;
            case "sendToPlugin":
                // Offline only, for the tests: the payload comes straight back, which is how they make
                // this side send a frame of any length.
                if (h.Offline is not null) ws.Send("{\"event\":\"sendToPropertyInspector\",\"context\":" + Quote(ctx) + ",\"payload\":" + payload + "}");
                return false;
            default: return false;
        }
    }

    static string Str(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    static void Draw(Ws ws, Host h, Dictionary<string, string> shown, long now) {
        double age = 0;
        if (h.Offline is null) { long ts = h.FetchedAt(); if (ts > 0) age = Math.Max(0, now - ts); }
        string face = Face(h, age);
        bool sent = false;
        foreach (string ctx in shown.Keys.ToArray()) {
            if (shown[ctx] == face) continue;                 // the same picture: the app has it already
            ws.Send("{\"event\":\"setImage\",\"context\":" + Quote(ctx) + ",\"payload\":{\"image\":\"data:image/svg+xml,"
                    + Uri.EscapeDataString(face) + "\",\"target\":0,\"state\":0}}");
            shown[ctx] = face; sent = true;
        }
        // This process sits still for hours between a few kilobytes of work, so what a draw
        // allocated is handed back at once rather than when the collector next sees a reason to.
        if (sent) GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, true, true);
    }

    // ---------------------------------------------------------------- fetching, and a key press

    // The usage is brought up to date by this same exe, started with --refresh: it goes through
    // GetUsage() as a render does and writes the cache, which this process is told of through the
    // registry. It is not waited for, so a fetch that takes its three seconds holds nothing up.
    static void Refresh(Ws ws) {
        try {
            var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add("--refresh");
            using var p = Process.Start(psi);
        } catch (Exception ex) { ws.Send(Log("could not start --refresh: " + ex.GetType().Name + ": " + ex.Message)); }
    }

    // A short press opens a tab in the Windows Terminal window used last, a long press a new
    // window. Either gets the default profile. wt.exe is an app execution alias, named by its full
    // path so that the PATH this process was started with does not matter.
    static void Press(Ws ws, Host h, string ctx, bool hold) {
        string wt = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "wt.exe");
        string[] a = hold ? new[] { "-w", "new" } : new[] { "-w", "0", "nt" };
        // Offline nothing is started: the command is reported instead, so a test never opens a terminal.
        if (h.Offline is not null) { ws.Send(Log("offline: would run " + wt + " " + string.Join(' ', a))); return; }
        try {
            var psi = new ProcessStartInfo(wt) { UseShellExecute = false, CreateNoWindow = true };
            foreach (string s in a) psi.ArgumentList.Add(s);
            using var p = Process.Start(psi);
        } catch (Exception ex) {
            ws.Send("{\"event\":\"showAlert\",\"context\":" + Quote(ctx) + "}");
            ws.Send(Log("could not start " + wt + ": " + ex.GetType().Name + ": " + ex.Message));
        }
    }

    static string Log(string message) => "{\"event\":\"logMessage\",\"payload\":{\"message\":" + Quote(message) + "}}";

    // a JSON string
    static string Quote(string s) {
        var sb = new StringBuilder(s.Length + 2).Append('"');
        foreach (char c in s) {
            if (c == '"' || c == '\\') sb.Append('\\').Append(c);
            else if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
            else sb.Append(c);
        }
        return sb.Append('"').ToString();
    }

    // ---------------------------------------------------------------- the key face
    //
    // Two meters, 5h above 7d, each the same three lines: the label and the percentage; the bar,
    // ten cells as on the status line; and a small line with the time to the reset on the left and
    // the time to 100% on the right. Everything is placed by the numbers below, on a canvas of 144
    // by 144 that the app scales to the key (96 by 96 on an XL), so moving or resizing a part is a
    // change to one of them.
    const double RowTop = 4, RowHeight = 72;                  // the two meters: where the first starts, how far apart
    const double TitleY = 22;                                 // the baseline of the label and the percentage
    const double LabelX = 8, LabelSize = 23;                  // the label, from the left
    const double PctX = 137, PctSize = 25;                    // the percentage, ending at the right
    const double CellsY = 36, CellsX = 13, CellsStep = 13.1;  // the bar: the centre of its first cell, and the pitch
    const double CellR = 5.2, RingR = 4.4, RingStroke = 1.6;  // a lit cell, and an empty one
    const double NoteY = 59, NoteSize = 13.5;                 // the small line
    const double NoteLeft = 6, NoteText = 21, NoteRight = 139;

    // The colours the limit rows use (docs/reference/layout.md, the palette) that no function of
    // Program.cs hands over; the cells, the percentage and the label come through the Host.
    const string Ground = "#000000", Dim = "#6E738D", Plain = "#A9B1D6", Green = "#A6E3A1", LightGreen = "#C6F6C1", Red = "#F7768E";
    const string Sans = "Segoe UI", Symbols = "Segoe UI Symbol";

    // The key as SVG. age is the seconds since the fetch the rows come from; below zero, it is
    // worked out here.
    public static string Face(Host h, double age) {
        var (c, _, warn) = h.Look();
        if (age < 0) { long ts = h.Offline is null ? h.FetchedAt() : 0; age = ts > 0 ? Math.Max(0, Now() - ts) : 0; }
        var sb = new StringBuilder(2800);
        sb.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"144\" height=\"144\" viewBox=\"0 0 144 144\" font-family=\"").Append(Sans).Append("\">")
          .Append("<rect width=\"144\" height=\"144\" fill=\"").Append(Ground).Append("\"/>");
        // Two fetches in a row have failed. The rows are still the last good ones, so they are drawn
        // grey, and the small lines say what the status line's ⚠ row says: why, and how old they are.
        var (why, old) = warn.Length > 0 ? Warn(warn) : ("", "");
        Meter(sb, h, RowTop, "5h", c.Rows, age, warn.Length > 0, why, alarm: true);
        Meter(sb, h, RowTop + RowHeight, "7d", c.Rows, age, warn.Length > 0, old, alarm: false);
        return sb.Append("</svg>").ToString();
    }

    static void Meter(StringBuilder sb, Host h, double y, string label, List<RowIn> rows, double age, bool failing, string failNote, bool alarm) {
        int i = rows.FindIndex(r => r.Label == label);
        if (i < 0) {
            // no such row: nothing fetched yet, nobody signed in, or a meter the server did not send
            Text(sb, LabelX, y + TitleY, LabelSize, 600, "start", Dim, label);
            Text(sb, PctX, y + TitleY, PctSize, 700, "end", Dim, "—");
            Cells(sb, h, y + CellsY, 0, grey: true);
        } else {
            RowIn r = rows[i];
            double left = r.Hrs - age / 3600.0;
            // Its window has ended since the fetch, so what was used in it is gone, and the reset
            // time of the next one is not known until something is fetched.
            bool over = r.HasReset && left <= 0;
            int pct = over ? 0 : Math.Clamp(r.Pct, 0, 100);
            bool grey = failing || over;
            Text(sb, LabelX, y + TitleY, LabelSize, 600, "start", grey ? Dim : Hex(h.SevColor(r.Sev), Plain), label);
            Text(sb, PctX, y + TitleY, PctSize, 700, "end", grey ? Dim : Hex(h.PctColor(pct), Plain), pct.ToString(CultureInfo.InvariantCulture) + "%");
            Cells(sb, h, y + CellsY, pct, grey);
            if (!failing) {
                bool known = r.HasReset && !over;
                Text(sb, NoteLeft, y + NoteY, NoteSize, 400, "start", over ? Dim : Green, "↻", Symbols);
                Text(sb, NoteText, y + NoteY, NoteSize, 400, "start", over ? Dim : LightGreen, known ? h.Hm(left) : "—");
                if (!over) {
                    // The pace is the one of the fetch. After five minutes without one nothing has been
                    // running, and a time to 100% would be a statement about a pace that has stopped.
                    var p = h.Pace(r with { Hrs = Math.Max(0, left) });
                    bool idle = age > IdleSeconds;
                    string tone = idle || p.tone == 0 ? Dim : p.tone == 1 ? Red : Hex(h.Forest, Green);
                    Text(sb, NoteRight, y + NoteY, NoteSize, 400, "end", tone, "→" + (idle ? "idle" : p.to100));
                }
            }
        }
        if (failing && failNote.Length > 0) {
            if (alarm) {
                Text(sb, NoteLeft, y + NoteY, NoteSize, 400, "start", Red, "⚠", Symbols);
                Text(sb, NoteText, y + NoteY, NoteSize, 400, "start", Red, failNote);
            } else Text(sb, NoteLeft, y + NoteY, NoteSize, 400, "start", Dim, failNote);
        }
    }

    // The bar, by the status line's own rules: BarFill says how many cells are lit, ZoneColor what
    // colour each one is.
    static void Cells(StringBuilder sb, Host h, double y, int pct, bool grey) {
        int lit = Math.Min(10, h.BarFill(pct));
        for (int c = 1; c <= 10; c++) {
            string at = "<circle cx=\"" + F(CellsX + (c - 1) * CellsStep) + "\" cy=\"" + F(y) + "\"";
            if (c <= lit) sb.Append(at).Append(" r=\"").Append(F(CellR)).Append("\" fill=\"").Append(grey ? Dim : Hex(h.ZoneColor(c), Plain)).Append("\"/>");
            else sb.Append(at).Append(" r=\"").Append(F(RingR)).Append("\" fill=\"none\" stroke=\"").Append(Dim).Append("\" stroke-width=\"").Append(F(RingStroke)).Append("\"/>");
        }
    }

    static void Text(StringBuilder sb, double x, double y, double size, int weight, string anchor, string fill, string text, string? family = null) {
        sb.Append("<text x=\"").Append(F(x)).Append("\" y=\"").Append(F(y)).Append("\" font-size=\"").Append(F(size))
          .Append("\" font-weight=\"").Append(weight.ToString(CultureInfo.InvariantCulture)).Append("\" text-anchor=\"").Append(anchor)
          .Append("\" fill=\"").Append(fill).Append('"');
        if (family is not null) sb.Append(" font-family=\"").Append(family).Append('"');
        sb.Append('>').Append(text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")).Append("</text>");
    }

    static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    // "\x1b[1;38;2;247;118;142m" is #F7768E: the status line's colours are SGR sequences
    static string Hex(string sgr, string otherwise) {
        int i = sgr.IndexOf("38;2;", StringComparison.Ordinal);
        if (i < 0) return otherwise;
        var p = sgr.Substring(i + 5).TrimEnd('m').Split(';');
        return p.Length >= 3 && byte.TryParse(p[0], out byte r) && byte.TryParse(p[1], out byte g) && byte.TryParse(p[2], out byte b)
            ? "#" + r.ToString("X2") + g.ToString("X2") + b.ToString("X2") : otherwise;
    }

    // The status line's ⚠ row for a failing fetch, cut to what a key has room for:
    // "usage — timed out after 3 s; the limit rows are 2m old" becomes "timeout" and "2m old".
    // The wording taken apart here is FetchWhy()'s and FetchWarn()'s in Program.cs.
    static (string why, string old) Warn(string fetchErr) {
        int a = fetchErr.IndexOf("— ", StringComparison.Ordinal), b = fetchErr.IndexOf(';');
        string reason = a >= 0 && b > a ? fetchErr.Substring(a + 2, b - a - 2) : "";
        string why = reason.StartsWith("timed out", StringComparison.Ordinal) ? "timeout"
                   : reason.StartsWith("offline", StringComparison.Ordinal) ? "offline"
                   : reason.StartsWith("no OAuth token", StringComparison.Ordinal) ? "no token"
                   : reason.StartsWith("HTTP ", StringComparison.Ordinal) ? reason.Substring(0, Math.Min(reason.Length, 8)).TrimEnd()
                   : "fetch failed";
        int c = fetchErr.IndexOf(" are ", StringComparison.Ordinal);
        return (why, c >= 0 ? fetchErr.Substring(c + 5) : "no rows yet");
    }

    // ---------------------------------------------------------------- the websocket
    //
    // The app's side of the conversation is a websocket server on a loopback port. This is the
    // client the protocol asks for (RFC 6455) and nothing more: one connection, text messages, no
    // extensions. It is written out here because the framework's own client costs four times the
    // room in the binary and a thread pool in the process that stays up.
    sealed class Ws : IDisposable {
        readonly Socket s; readonly object gate = new();
        Ws(Socket socket) { s = socket; }

        public static Ws Connect(int port) {
            var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true, ReceiveTimeout = 10000, SendTimeout = 5000 };
            try {
                s.Connect(IPAddress.Loopback, port);
                string key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
                s.Send(Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: 127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture)
                    + "\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: " + key + "\r\nSec-WebSocket-Version: 13\r\n\r\n"));
                var head = new StringBuilder(); var one = new byte[1];
                while (head.Length < 4 || head.ToString(head.Length - 4, 4) != "\r\n\r\n") {
                    if (s.Receive(one) != 1 || head.Length > 8192) throw new IOException("the app did not answer the websocket handshake");
                    head.Append((char)one[0]);
                }
                string[] lines = head.ToString().Split("\r\n");
                string accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
                if (!lines[0].StartsWith("HTTP/1.1 101", StringComparison.Ordinal)
                    || !lines.Any(l => l.StartsWith("Sec-WebSocket-Accept:", StringComparison.OrdinalIgnoreCase) && l.Substring(21).Trim() == accept))
                    throw new IOException("the app refused the websocket handshake: " + lines[0]);
                s.ReceiveTimeout = 0;                         // from here it waits for as long as the app says nothing
                return new Ws(s);
            } catch { s.Dispose(); throw; }
        }

        public void Send(string text) => Frame(0x1, Encoding.UTF8.GetBytes(text));

        void Frame(int opcode, ReadOnlySpan<byte> p) {
            int n = p.Length, head = n < 126 ? 2 : n <= 0xFFFF ? 4 : 10;
            var f = new byte[head + 4 + n];
            f[0] = (byte)(0x80 | opcode);
            if (n < 126) f[1] = (byte)(0x80 | n);
            else if (n <= 0xFFFF) { f[1] = 0x80 | 126; f[2] = (byte)(n >> 8); f[3] = (byte)n; }
            else { f[1] = 0x80 | 127; for (int k = 0; k < 8; k++) f[2 + k] = (byte)((long)n >> (8 * (7 - k))); }
            RandomNumberGenerator.Fill(f.AsSpan(head, 4));    // what a client sends is masked
            for (int k = 0; k < n; k++) f[head + 4 + k] = (byte)(p[k] ^ f[head + (k & 3)]);
            lock (gate) { for (int sent = 0; sent < f.Length;) sent += s.Send(f, sent, f.Length - sent, SocketFlags.None); }
        }

        byte[]? Exact(int n) {
            var b = new byte[n];
            for (int got = 0; got < n;) { int r = s.Receive(b, got, n - got, SocketFlags.None); if (r <= 0) return null; got += r; }
            return b;
        }

        // The next whole message, put together from its fragments, with any ping answered on the
        // way. null when the conversation is over, and end says how: 0 the app closed it, which is
        // answered in kind; 1 the connection dropped, or the app sent what the protocol forbids,
        // which is answered with a close of this side's own.
        public string? Receive(out int end) {
            end = 1;
            using var msg = new MemoryStream();
            bool started = false;
            try {
                while (true) {
                    var h = Exact(2); if (h is null) return null;
                    bool fin = (h[0] & 0x80) != 0; int op = h[0] & 0x0F; long len = h[1] & 0x7F;
                    // no extension was agreed, so the reserved bits are zero; a server never masks
                    if ((h[0] & 0x70) != 0 || (h[1] & 0x80) != 0) return Refuse(1002);
                    if (len == 126) { var e = Exact(2); if (e is null) return null; len = (e[0] << 8) | e[1]; }
                    else if (len == 127) {
                        var e = Exact(8); if (e is null) return null;
                        len = 0; foreach (byte x in e) len = (len << 8) | x;
                        if (len < 0) return Refuse(1002);
                    }
                    if (op >= 0x8 && (!fin || len > 125)) return Refuse(1002);   // a control frame is whole and short
                    if (len + msg.Length > MaxMessage) return Refuse(1009);
                    var p = Exact((int)len); if (p is null) return null;
                    switch (op) {
                        case 0x8:                             // close: answer with the same status, and stop
                            try { Frame(0x8, p.AsSpan(0, Math.Min(p.Length, 2))); } catch { }
                            Finish();
                            end = 0; return null;
                        case 0x9: Frame(0xA, p); continue;    // ping: pong, with what it carried
                        case 0xA: continue;                   // a pong nobody asked for
                        case 0x0: if (!started) return Refuse(1002); break;
                        case 0x1: case 0x2: if (started) return Refuse(1002); started = true; break;
                        default: return Refuse(1002);
                    }
                    msg.Write(p);
                    if (fin) { end = 0; return Encoding.UTF8.GetString(msg.GetBuffer(), 0, (int)msg.Length); }
                }
            } catch (SocketException) { return null; } catch (ObjectDisposedException) { return null; }
        }

        string? Refuse(int status) {
            try { Frame(0x8, new[] { (byte)(status >> 8), (byte)status }); } catch { }
            Finish();
            return null;
        }

        // A close has been sent, and it is the app's turn to end the connection. Say that nothing
        // more is coming and wait for that, a second at most: a socket closed over bytes it has
        // not read is reset, and a reset can cost the other side the close it was just sent.
        void Finish() {
            try {
                s.Shutdown(SocketShutdown.Send);
                s.ReceiveTimeout = 1000;
                var rest = new byte[1024];
                while (s.Receive(rest) > 0) { }
            } catch { }
        }

        public void Dispose() => s.Dispose();
    }

    // The one extra thread. It sits in the socket's receive and hands whole messages to the loop;
    // the loop's last one is null, and End then says how the conversation ended.
    sealed class Inbox : IDisposable {
        readonly Queue<string?> q = new();
        public readonly AutoResetEvent Signal = new(false);
        public int End { get; private set; } = 1;

        public Inbox(Ws ws) {
            new Thread(() => {
                while (true) {
                    string? m = ws.Receive(out int end);
                    lock (q) { if (m is null) End = end; q.Enqueue(m); }
                    Signal.Set();
                    if (m is null) return;
                }
            }, 256 * 1024) { IsBackground = true, Name = "deck socket" }.Start();
        }

        public bool Take(out string? m) { lock (q) return q.TryDequeue(out m); }
        public void Dispose() => Signal.Dispose();
    }

    // ---------------------------------------------------------------- the two watches

    // HKCU\Software\StatusAI, where the limit rows are cached: signalled when a value is written.
    sealed class CacheWatch : IDisposable {
        readonly IntPtr key;
        public readonly AutoResetEvent Changed = new(false);
        CacheWatch(IntPtr k) { key = k; }

        public static CacheWatch? Open() {
            if (RegOpenKeyExW(new UIntPtr(0x80000001u), @"Software\StatusAI", 0, 0x0010 /* KEY_NOTIFY */, out IntPtr k) != 0) return null;
            var w = new CacheWatch(k); w.Arm(); return w;
        }
        // one signal for each arming, at the next write: REG_NOTIFY_CHANGE_LAST_SET, from any thread
        public void Arm() => RegNotifyChangeKeyValue(key, false, 0x4 | 0x10000000, Changed.SafeWaitHandle.DangerousGetHandle(), true);
        public void Dispose() { RegCloseKey(key); Changed.Dispose(); }
    }

    // ~/.claude/projects and everything under it: signalled when a file is created, renamed, grows or is written.
    sealed class DirWatch : IDisposable {
        readonly IntPtr handle;
        public readonly WaitHandle Changed;
        DirWatch(IntPtr h) { handle = h; Changed = new Borrowed(h); }

        public static DirWatch? Open(string dir) {
            IntPtr h = FindFirstChangeNotificationW(dir, true, 0x1 | 0x8 | 0x10);   // FILE_NAME, SIZE, LAST_WRITE
            return h == IntPtr.Zero || h == new IntPtr(-1) ? null : new DirWatch(h);
        }
        public void Arm() => FindNextChangeNotification(handle);
        public void Dispose() { Changed.Dispose(); FindCloseChangeNotification(handle); }

        sealed class Borrowed : WaitHandle { public Borrowed(IntPtr h) { SafeWaitHandle = new SafeWaitHandle(h, false); } }
    }

    // ---------------------------------------------------------------- Win32

    [DllImport("kernel32.dll")] static extern bool FreeConsole();
    [DllImport("kernel32.dll")] static extern IntPtr GetStdHandle(int which);
    [DllImport("kernel32.dll")] static extern bool SetHandleInformation(IntPtr handle, int mask, int flags);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern int RegOpenKeyExW(UIntPtr hKey, string subKey, int options, int sam, out IntPtr result);
    [DllImport("advapi32.dll", ExactSpelling = true)]
    static extern int RegNotifyChangeKeyValue(IntPtr hKey, bool subtree, int filter, IntPtr hEvent, bool async);
    [DllImport("advapi32.dll", ExactSpelling = true)] static extern int RegCloseKey(IntPtr hKey);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern IntPtr FindFirstChangeNotificationW(string path, bool subtree, int filter);
    [DllImport("kernel32.dll", ExactSpelling = true)] static extern bool FindNextChangeNotification(IntPtr h);
    [DllImport("kernel32.dll", ExactSpelling = true)] static extern bool FindCloseChangeNotification(IntPtr h);
}
