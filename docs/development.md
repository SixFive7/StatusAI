# Development

Building, deploying and testing a change to the status line on a live machine. Three of the four
traps below cost real time; the first one is the expensive one.

## Build

.NET 10 SDK (developed against 10.0.302), NativeAOT, `net10.0-windows`, x64.

```bash
cd src && dotnet publish -c Release -r win-x64
# -> src/bin/Release/net10.0-windows/win-x64/publish/cship-usage.exe   ~4,74 MiB
```

## Trap 1 — the cache serves output from the *previous* binary

**This will make a working change look broken, and a broken change look fine.**

`GetUsage()` returns the cached render string whenever `now - ts < 50`. The cache holds a fully
rendered, ANSI-coloured string, not the underlying numbers — so a value written 20 seconds ago by
the *old* binary is returned verbatim by the *new* one. Claude Code re-runs the status line every
60 seconds, so on any machine with a live session there is almost always a fresh entry.

The failure mode is nasty because the numbers are current — only the *rendering* is stale. A test
run of a new build can show freshly fetched percentages laid out by code that is no longer on
disk, which reads as "the fetch works but my change did nothing."

Invalidate before every test:

```powershell
Set-ItemProperty -Path "HKCU:\Software\cshipUsage" -Name "ts" -Value "0"
```

Then run immediately — a live Claude Code window is racing you for the same mutex, and if it wins,
you get its render instead.

That is a write to state every running session shares, and the test run that follows fetches and
pushes a sample into the shared prediction history. When what is under test is the drawing rather
than the fetch, skip all of it: the [offline render](#offline-beside-live-sessions) never reads the
cache, so it has no trap to invalidate.

## Trap 2 — the binary is locked while the status line runs it

Windows holds the image while the process runs, and it runs every 60 seconds for ~100 ms. A
straight copy fails intermittently. Retry:

```powershell
foreach ($i in 1..12) {
  try { Copy-Item $src $dst -Force -ErrorAction Stop; break }
  catch { Start-Sleep -Milliseconds 700 }
}
```

Back up the outgoing binary first. The convention on the dev machine is a sibling
`cship-usage.exe.bak.<unix-seconds>`, which makes a revert a copy rather than a rebuild.

## Trap 3 — `dotnet publish` output is not what is deployed

The status line runs the binary at its installed location, not the publish directory. Editing
`Program.cs` and rebuilding changes nothing visible until the copy step runs. Conversely, a revert
that only restores the installed binary leaves the publish tree holding the change, ready to be
redeployed by the next person who runs the copy step. **Revert the source, rebuild, and redeploy**
so all three stay consistent.

## Testing a render

The binary reads the status-line payload on stdin. `tests/fixtures/payloads/mid-session.json` holds a captured mid-session
one.

### Offline, beside live sessions

`CSHIP_OFFLINE=<dir>` renders from that directory instead of the machine's shared state. The limit
rows are drawn from `<dir>/usage.json` and `<dir>/rows.json` rather than the registry cache and the
API, the euro rate comes from `rows.json`, and `<dir>` stands in for the user profile — the account
line reads `<dir>/.claude.json` and `<dir>/.claude/.credentials.json`, and the token cache is
written under `<dir>/.claude/statusline-tokens/`. `HKCU\Software\cshipUsage` is neither read nor
written, the usage lock is never taken, and nothing is fetched, so a dev build can run beside live
sessions without serving their cached render or touching their history.

**With a `usage.json`** — a usage API response, verbatim or edited — the binary parses it with the
same `ParseUsage()` as a live fetch, so the meters, the product breakdown, the on-credit alarm and
the meters notice are under test. `rows.json` then supplies what a live fetch would take from the registry: the clock
to measure the resets against, the scoped meter already being followed, and the forecast inputs by
label. Which meters are drawn and which are flagged is the same `Known()` decision a live fetch
makes; a drawn meter the forecast leaves out is gated.

```json
{ "fx": 0.876, "now": "2026-09-23T20:14:20Z", "sn": "Fable",
  "forecast": { "5h":    { "rate": 28.8,   "gated": false },
                "7d":    { "rate": 6.7039, "gated": false },
                "Fable": { "rate": 0,      "gated": false } } }
```

**Without one**, `rows.json` holds what `RenderRows` is given — the output of the fetch, the window
checks and the slope — and there is no breakdown, no credit state and no meters notice to draw:

```json
{ "fx": 0.876,
  "rows": [ { "label": "5h",    "pct": 22, "hrs": 3.383, "hasReset": true, "rate": 10.42, "gated": false, "sev": "normal"   },
            { "label": "7d",    "pct": 89, "hrs": 57.08, "hasReset": true, "rate": 2.4,   "gated": false, "sev": "critical" },
            { "label": "Fable", "pct": 24, "hrs": 57.08, "hasReset": true, "rate": 0,     "gated": false, "sev": "normal"   } ] }
```

Either way the forecast is an input, so the window checks and the slope are not under test.

**Keep the hours exact when comparing builds.** A `usage.json` and a `rows.json` that are meant to
draw the same rows must agree on the hours to each reset to the last bit: .NET's `TotalHours` is
`(double)ticks / TicksPerHour`, so derive `hrs` as integer ticks over 36 000 000 000, not from
floating-point seconds, or a rounding step can land on the other side of a minute.

Point the payload's `transcript_path` inside `<dir>` as well. cship keeps its own cache beside the
transcript, in `<transcript dir>/cship/`, so that keeps its writes out of `~/.claude/projects` too.

```powershell
$env:CSHIP_OFFLINE = "<dir>"
$out = Get-Content -Raw "<payload>.json" | & "<publish>\cship-usage.exe"
Remove-Item Env:CSHIP_OFFLINE
```

**A payload is only a fixture if it is what Claude Code really sends.** A brand-new session, before
its first response, looks like this — the three nulls are the point, see
[layout.md](reference/layout.md#no-messages-yet-is-a-zero-not-a-gap):

```json
{ "session_id": "<sid>", "transcript_path": "<dir>/projects/p/<sid>.jsonl", "cwd": "C:\\Users\\you",
  "model": { "id": "claude-opus-5-5", "display_name": "Opus 5.5 (1M context)" },
  "cost": { "total_cost_usd": 0, "total_duration_ms": 12000, "total_api_duration_ms": 0,
            "total_lines_added": 0, "total_lines_removed": 0 },
  "context_window": { "total_input_tokens": 0, "total_output_tokens": 0, "context_window_size": 1000000,
                      "current_usage": null, "used_percentage": null, "remaining_percentage": null },
  "exceeds_200k_tokens": false, "effort": { "level": "max" }, "thinking": { "enabled": true } }
```

The transcript that path names does not exist yet, and should not: Claude Code writes it at the first
prompt.

### Against the live binary

A minimal payload is enough when only the limit rows matter. It renders a `⚠` row naming the fields
it leaves out, which is correct for that payload:

```powershell
$stdin = '{"transcript_path":"","cost":{"total_cost_usd":0,"total_duration_ms":0,"total_lines_added":0,"total_lines_removed":0}}'
$out = $stdin | & "<path>\cship-usage.exe"
```

### Checking the output

**Check alignment on the stripped text, not the coloured output.** SGR escapes make every visual
length wrong:

```powershell
($out -replace "`e\[[0-9;]*m", "") -split "`n" |
  ForEach-Object { "{0,3} |{1}|" -f $_.Length, $_ }
```

**Check for a specific glyph by codepoint**, not by pasting the character into a script — encoding
round-trips through the shell are not reliable:

```powershell
$out -match ([char]0x2503)      # ┃
```

## Trap 4 — an account switch looks like a regression

`AccountInfo()` is read fresh from `~/.claude.json` on every render, and a changed `accountUuid`
clears `hist` outright. Immediately after a switch every row reads `→ early` and the percentages
belong to a different account, so they will not match anything observed a minute earlier. That is
`WindowCheck` and the account guard working, not a fault. Check the account line before
investigating a sudden change in the numbers.

## Verifying the accounting

Unchanged by presentation work, but run after any Claude Code upgrade — the transcript layout is
undocumented and fails silently downward:

```powershell
./scripts/Split-MainVsTree.ps1 -Sid <session-id>
./scripts/Verify-Tools.ps1     -Sid <session-id>
./scripts/Decode-TokCache.ps1  -Sid <session-id>
```

See [accounting.md](reference/accounting.md) for the expected figures.
