# Development

Building, deploying, releasing and testing a change to the status line on a live machine. Three of
the four traps below cost real time; the first one is the expensive one.

## The repository

```
src/        Program.cs and the csproj — the whole implementation
tests/      the render tests: fixtures, expected renders, Test-Renders.ps1
scripts/    Deploy.ps1, Package.ps1, and independent PowerShell implementations that verify the accounting
config/     the cship and starship configuration the status line is used with
docs/       guide/ for using it, reference/ for how it works, design/ for why, assets/ for the
            figures, and this page
.work/      scratch, gitignored: builds, test renders, anything throwaway
```

## Build

.NET 10 SDK (developed against 10.0.302; 10.0.401 builds it too), NativeAOT, `net10.0-windows`,
x64. NativeAOT links with the C++ toolchain, so it also needs Visual Studio 2022 or later with the
*Desktop development with C++* workload.

```bash
cd src && dotnet publish -c Release -r win-x64
# -> src/bin/Release/net10.0-windows/win-x64/publish/cship-usage.exe   ~4,83 MiB
```

## The render tests

`tests/` renders every fixture through a built binary, offline, and compares the output byte for
byte with the render recorded when that behaviour was decided. Run them after every build, and
against the deployed binary before replacing it:

```powershell
./tests/Test-Renders.ps1                                        # the publish output above
./tests/Test-Renders.ps1 -Exe (Get-Command cship-usage).Source  # the deployed binary
```

146 renders of 45 cases, about 15 seconds, exit code 0 when every one matches. A failure names the
case and what it checks, and prints the lines that differ with their colours stripped — or says
that only the colours differ. The render it got is left in `.work/test-renders/actual/`, and the
home it ran in under `.work/test-renders/run/`. Windows PowerShell 5.1 and PowerShell 7 both run it.

**Nothing live is touched.** Every render runs in a fresh copy of its home under
`.work/test-renders/`, with `CSHIP_OFFLINE` pointing at it — see
[offline, beside live sessions](#offline-beside-live-sessions) — so the registry cache, the usage
lock, the prediction history and the network are never touched, and nothing is written to `tests/`.
The binary is copied beside a `cship.exe` first, because cship-usage runs the cship in its own
directory: the one beside `-Exe`, else the one on PATH, else `-Cship <path>`.

**cship gets a config without starship.** `HOME` points cship at `tests/cship.toml`, which is the
shipped `config/cship.toml` without the starship line: that line draws the working directory, git,
the clock, RAM and the GPU, and is never the same twice. The model line it keeps is the real host
line the block is inserted under. The expected renders were recorded with cship 1.8.0, as
`cases.json` says; a run with another version says so first, because it may draw that line
differently.

| path | holds |
|---|---|
| `tests/cases.json` | every case: its home, its payload, its widths, and one line on what it checks |
| `tests/fixtures/homes/<home>/` | a `CSHIP_OFFLINE` directory: `rows.json`, usually a `usage.json`, the account files, and a transcript tree where the case counts tokens |
| `tests/fixtures/payloads/<payload>.json` | a status-line stdin payload, with a `transcript_path` relative to the home |
| `tests/expected/<case>.w<width>.ansi` | the exact bytes that case draws at that width |
| `tests/cship.toml` | cship's config for the renders |

The fixtures carry no credentials — `.credentials.json` holds the plan fields and nothing else —
and every address in them is an `example.com` one.

**What they cover.** The `showcase` case draws every row at once, its token grid over a main thread
and two sub-agents, one a nested workflow agent, whose files carry both
[duplication traps](reference/accounting.md#the-two-duplication-traps); the scripts under
[verifying the accounting](#verifying-the-accounting) agree with its totals. The rest: the limit
rows through a usage response and through `rows.json` alone, the meters notice, the breakdown
against the width, the on-credit alarm, the order of the `⚠` rows, and the payloads — fresh
sessions that must stay quiet and broken ones that must still warn, one of them so broken that
cship prints nothing. **What they cannot cover**: the live fetch, the registry cache and the
history. The forecast is an input to a fixture, so the window checks and the slope are not under
test.

**A deliberate change of output** is recorded with `-Update`, which rewrites `tests/expected` and
removes any render no case produces; read the diff before committing it. To add a case, add a home
or a payload, give it an entry in `cases.json`, and run `-Update -Case <name>`. The figures in
`docs/assets/` are drawn from these files, so a change of output is also a reason to regenerate
them, in the same commit: `node docs/assets/figures.gen.js` (see [its README](assets/README.md)).

**To look at a render**, `-Case showcase -Show` prints it with its colours, and `-OutDir <dir>`
writes every render it makes as `<case>.w<width>.ansi`, at any width a case lists.

## Deploying

```powershell
./scripts/Deploy.ps1 -WhatIf   # what it would do, without doing it
./scripts/Deploy.ps1           # the publish output over the cship-usage.exe on PATH
```

It refuses unless `cship.exe` sits beside the target, runs the render tests against the build with
that cship, stops if the target already has the build's hash, backs the target up as
`cship-usage.exe.bak.<unix-seconds>`, copies with retries, verifies the hash, and restores the
backup if the copy did not land — traps 2 and 3 below, as one script. `-Source` and `-Target` name
other files. Exit code 0 when deployed or already deployed, 1 when it refused, 2 when the copy
failed and the backup is back in place, 3 when even that failed, the backup intact beside it.

It was written on 2026-09-24 from the procedure that day's deploys used, and has not been run
itself yet: read its output the first time.

## Releasing

```powershell
./scripts/Package.ps1 -Version 0.1.0
```

It refuses unless `CHANGELOG.md` has a `## 0.1.0 — <date>` section, and unless the cship a friend
is told to download is the one the render tests pin and the [install guide](guide/install.md)
fetches: cship 1.8.0, by URL and SHA-256. It publishes the build with that version, runs the render
tests against it with that same cship, byte for byte the download, and writes to `.work/release/`:

| file | holds |
|---|---|
| `StatusAI-0.1.0-win-x64.zip` | `cship-usage.exe`, `cship.toml` and `starship.toml` from `config/`, and a plain-text `README.txt`, at the root of the zip |
| `StatusAI-0.1.0-win-x64.zip.sha256` | the zip's SHA-256, one line as `sha256sum` writes it |
| `notes-v0.1.0.md` | the release page's text: the version's CHANGELOG section with its links pointed at the tag, and a footer pointing at the install guide |

cship is linked, not bundled: it statically links some 43 crates whose notices a redistributor
would owe, so the install guide fetches it from cship's own release instead — see
[packaging-plan.md](design/packaging-plan.md#licensing).

**It publishes nothing.** Its last line is the `gh release create` command that would, to run from
the repository root once the release commit is pushed. The exe records the commit it was built at,
as `0.1.0+<commit>` in its product version, so package from that commit; the script says so when
the working tree has uncommitted changes. `-OutDir` writes elsewhere and `-Cship` names the cship to
test with. Exit code 0 when packaged, 1 when it refused, 2 when the build or the packaging failed;
a run that does not finish leaves no zip of that version behind. Windows PowerShell 5.1 and
PowerShell 7 both run it.

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
straight copy fails intermittently, so [Deploy.ps1](#deploying) retries it, twelve times, 700 ms
apart.

Back up the outgoing binary first. The convention on the dev machine is a sibling
`cship-usage.exe.bak.<unix-seconds>`, which makes a revert a copy rather than a rebuild; the script
writes that backup and verifies it before it copies anything.

## Trap 3 — `dotnet publish` output is not what is deployed

The status line runs the binary at its installed location, not the publish directory. Editing
`Program.cs` and rebuilding changes nothing visible until the copy step runs. Conversely, a revert
that only restores the installed binary leaves the publish tree holding the change, ready to be
redeployed by the next person who runs the copy step. **Revert the source, rebuild, and redeploy**
so all three stay consistent.

## Testing a render

The binary reads the status-line payload on stdin. `tests/fixtures/payloads/` holds a captured
mid-session one, `mid-session.json`, beside the rest the [render tests](#the-render-tests) use.

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
the meters notice are under test. `rows.json` then supplies what a live fetch would take from the
registry: the clock to measure the resets against, the scoped meter already being followed, and the
forecast inputs by label. Which meters are drawn and which are flagged is the same `Known()`
decision a live fetch makes; a drawn meter the forecast leaves out is gated.

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

See [accounting.md](reference/accounting.md) for the expected figures. The two walkers also take
`-ProjectsRoot`, so they check the render tests' showcase tree as well, where they must agree with
its token rows — 60.333.978 main, 5.081.453 sub, 101 and 129 tool calls:

```powershell
$root = (Resolve-Path tests/fixtures/homes/showcase/projects).Path
./scripts/Split-MainVsTree.ps1 -Sid 22222222-fixt-fixt-fixt-000000000002 -ProjectsRoot $root
./scripts/Verify-Tools.ps1     -Sid 22222222-fixt-fixt-fixt-000000000002 -ProjectsRoot $root
```
