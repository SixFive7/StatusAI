<sub>[StatusAI](../README.md) / [Documentation](README.md)</sub>

# Development

How to build, deploy, release and test a change to the status line on a machine where it is in use,
and how the docs are kept. Three of the five traps below have cost real time, the first one most of
all.

## The repository

```
src/         Program.cs and the csproj: the whole status line. Deck.cs: the same exe as the
             plugin behind the Stream Deck key
streamdeck/  the plugin's folder as the app wants it, less the exe: its manifest and its pictures
tests/       the render tests: fixtures, expected renders, Test-Renders.ps1; and Test-Deck.ps1,
             which runs the exe as a plugin against a stand-in for the app
scripts/     Deploy.ps1, Package.ps1, and independent PowerShell implementations that verify the accounting
config/      the cship and starship configuration the status line is used with
docs/        guide/ for using it, reference/ for how it works, design/ for why, assets/ for the
             figures, and this page
.work/       scratch, gitignored: builds, test renders, anything throwaway
```

## Build

.NET 10 SDK (developed against 10.0.302; 10.0.401 builds it too), NativeAOT, `net10.0-windows`,
x64. NativeAOT links with the C++ toolchain, so it also needs Visual Studio 2022 or later with the
*Desktop development with C++* workload.

```bash
cd src && dotnet publish -c Release -r win-x64
# -> src/bin/Release/net10.0-windows/win-x64/publish/statusai.exe   ~4,9 MiB
```

## The render tests

`tests/` renders every fixture through a built binary, offline, and compares the output byte for
byte with the render recorded when that behaviour was decided. Run them after every build, and
against the deployed binary before replacing it:

```powershell
./tests/Test-Renders.ps1                                     # the publish output above
./tests/Test-Renders.ps1 -Exe (Get-Command statusai).Source  # the deployed binary
```

Test a change this way rather than against the live status line. A plain run of the binary on a
machine with Claude Code open reads and writes `HKCU\Software\StatusAI`, which every live session
shares. A build for testing can go into `.work/` as well:

```powershell
dotnet publish src/StatusAI.csproj -c Release -r win-x64 -o .work/publish
./tests/Test-Renders.ps1 -Exe .work/publish/statusai.exe
```

180 renders of 74 cases, about 20 seconds, exit code 0 when every one matches. A failure names the
case and what it checks, and prints the lines that differ with their colours stripped, or says that
only the colours differ. The render it got is left in `.work/test-renders/actual/`, and the home it
ran in under `.work/test-renders/run/`. Windows PowerShell 5.1 and PowerShell 7 both run it.

Thirteen of the cases are the Stream Deck key. A case with a `face` gets no payload and no width:
`statusai --deck-face <face>` runs in a copy of its home and prints the key as SVG, as it is drawn
that many seconds after the fetch its rows come from, and the bytes are compared with
`tests/expected/<case>.svg`. They cover the key of the screenshot's rows, the same key a quarter of
an hour and three and a half hours on, a row at 100%, rows without a trend, without a reset time
and without a session meter, a fetch that is failing, and no rows at all.

Nothing live is touched. Every render runs in a fresh copy of its home under `.work/test-renders/`,
with `STATUSAI_OFFLINE` pointing at it (see
[offline, beside live sessions](#offline-beside-live-sessions)), so the registry cache, the usage
lock, the prediction history and the network are left alone, and nothing is written to `tests/`.
The binary is copied beside a `cship.exe` first, because statusai runs the cship in its own
directory: the one beside `-Exe`, else the one on PATH, else `-Cship <path>`.

The width reaches a render the way its case says. By default `STATUSAI_WIDTH` is the width; a case
with an `env` sets exactly the `STATUSAI_WIDTH` and `COLUMNS` it names instead, `{w}` standing for
the width. `COLUMNS` and `LINES` are cleared first, so the size of the terminal running the tests
never reaches a render.

cship gets a config without starship: `HOME` points it at `tests/cship.toml`, which is the shipped
`config/cship.toml` minus the starship line. That line draws the working directory, git, the clock,
RAM and the GPU, and is never the same twice. The model line it keeps is the real host line the
block is inserted under. The expected renders were recorded with cship 1.8.0, as `cases.json` says;
a run with another version says so first, because it may draw that line differently.

| path | holds |
|---|---|
| `tests/cases.json` | every case: its home, its payload, its widths, the `env` that gives it the width where that is under test, and one line on what it checks |
| `tests/fixtures/homes/<home>/` | a `STATUSAI_OFFLINE` directory: `rows.json`, usually a `usage.json`, the account files, and a transcript tree where the case counts tokens |
| `tests/fixtures/payloads/<payload>.json` | a status-line stdin payload, with a `transcript_path` relative to the home |
| `tests/expected/<case>.w<width>.ansi` | the exact bytes that case draws at that width |
| `tests/expected/<case>.svg` | the exact bytes of the Stream Deck key, for a case with a `face` |
| `tests/cship.toml` | cship's config for the renders |

The fixtures carry no credentials (`.credentials.json` holds the plan fields and nothing else), and
every address in them is an `example.com` one.

The `showcase` case draws every row at once, with its token grid over a main thread and two
sub-agents, one of them a nested workflow agent, whose files carry both
[duplication traps](reference/accounting.md#the-two-duplication-traps); the scripts under
[verifying the accounting](#verifying-the-accounting) agree with its totals. The other cases cover
the limit rows through a usage response and through `rows.json` alone, the meters notice, the
breakdown against the width, the on-credit alarm, the order of the `⚠` rows, where the width comes
from (`COLUMNS`, `STATUSAI_WIDTH` before it, a value that is not a width, `statusLine.padding`), a
usage fetch that fails once, twice, in another session and before any has worked, then recovers, a
retry held off while the last attempt is under 50 seconds old and made once it is 50, and the
payloads: fresh sessions that must stay quiet, and broken ones that must still warn, one of
them so broken that cship prints nothing. What the tests cannot cover is the live fetch itself, the
registry cache and the history. A failed fetch is the fixture's word, so the count and its row are
under test and the network is not. The forecast is an input to a fixture, so the window checks and
the slope are not under test.

A deliberate change of output is recorded with `-Update`, which rewrites `tests/expected` and
removes any render no case produces; read the diff before committing it. To add a case, add a home
or a payload, give it an entry in `cases.json`, and run `-Update -Case <name>`. The figures in
`docs/assets/` are drawn from these files, so a change of output means regenerating them too, in
the same commit: `node docs/assets/figures.gen.js` (see [its README](assets/README.md)).

To look at a render, `-Case showcase -Show` prints it with its colours, and `-OutDir <dir>` writes
every render it makes as `<case>.w<width>.ansi`, at any width a case lists.

## The Stream Deck tests

`tests/Test-Deck.ps1` runs the binary as a Stream Deck plugin against a stand-in for the app: a
websocket server on a loopback port the system picks, which starts the exe the way the app does
and plays one case after another.

```powershell
./tests/Test-Deck.ps1 -Exe .work/publish/statusai.exe
```

17 cases, under a minute, exit code 0 when every one passes. It needs no Stream
Deck and no app, and Windows PowerShell 5.1 and PowerShell 7 both run it. The websocket client in
`Deck.cs` is written by hand, so the cases take it through the protocol a part at a time:

| case | holds the plugin to |
|---|---|
| registers, says so, and draws the key | the headers of its handshake, its registration, the one line it logs as it starts, and a picture that is `tests/expected/key.svg` byte for byte |
| handles the app hands down are not handed on | started with a file open to it the way the app's log is, not one of its handles is left marked for a program it starts to be given |
| frames with 16 and 64 bit lengths, in and out | messages of 2.000 and 70.000 bytes received, and of 300 and 70.000 bytes sent, each masked and with the length field its size calls for |
| a message in fragments, with a ping between them | three fragments put together, and the ping between two of them answered straight away |
| a ping is answered, a pong is ignored | a pong that carries what the ping did, at 0, 3 and 125 bytes |
| a short press on release, a long press after 500 ms | nothing while the key is down and the tab when it comes up; the window 500 ms into a hold, and nothing at its release; each press a `--deck-press` that ends with 0 |
| forty presses, and as many handles as before | two presses, then thirty one after the other and ten at once, each a `--deck-press` that is started, waited for and read; the plugin's handle count at most 6 up after the first two, which is what Windows takes for the first program a process starts, and where it was after the forty |
| no Windows Terminal: an alert on the key, why in the log | with `%LOCALAPPDATA%` a folder that has no `wt.exe`, each of four holds shows the alert and logs `could not start ...\wt.exe: Windows error 2`, and the handle count stays where the first left it |
| a refresh when Claude writes, one a slot, and none while no key shows | a write under `.claude/projects` asks for one refresh, and only once a key is visible |
| the deck goes and comes back | nothing drawn or fetched for a key that does not show; the key drawn again, and the refresh that was waiting made once, whether the app names the key before the deck or the deck before the key; the picture sent again when a deck or the system comes back with nothing said about the key |
| a hundred refreshes, and as many handles as before | with a slot of no length, a hundred writes are a hundred `--refresh` processes, and the plugin's handle count after them is what it was before |
| the app closes | a close in answer, and exit code 0 |
| a frame the protocol forbids | a masked frame, a reserved bit, a ping in fragments, a continuation of nothing, a message inside another and an opcode that does not exist: each a close with status 1002 and exit code 1; a frame announced at 17 MiB: 1009 |
| the connection drops | exit code 1 after a reset, and after a connection that ends inside a frame |
| no app, or one that answers wrongly | exit code 1 with nothing listening, with a `Sec-WebSocket-Accept` that does not match, and with a 400 |
| an app that never answers the handshake | gone after its ten seconds, with exit code 1 |
| `--refresh` and `--deck-face` do not wait for stdin | both end by themselves with stdin left open, as it is for a plugin |

Every plugin runs with `STATUSAI_OFFLINE` on a fresh copy of the `shot` home. Offline, the mode
touches nothing live, and says in the app's log what it did in place of it:
`offline: would run ...\wt.exe -w 0 nt` for a press, whose `statusai --deck-press` opens nothing
and ends with 0, and `offline: ran --refresh` for a refresh, which starts the same
`statusai --refresh` as ever, with nothing to fetch. So **no test opens a terminal or fetches
anything**, and the same switch runs
the plugin by hand without either. Two things more are offline only. A `sendToPlugin` from the
app is answered with its own payload, which is how a case makes the plugin send a frame of any
length. And `STATUSAI_DECK_SLOT` is taken for the seconds between refreshes, 62 without it,
which is how a case fits a hundred of them in a few seconds.

What these tests cannot cover is the app: that it starts the exe, shows the picture and sends the
events the stand-in sends. That takes a Stream Deck; see
[the key on a live machine](#the-key-on-a-live-machine).

### The presses, on the live desktop

Whether a press ends with the terminal in front is a question about the desktop, and the cases
above cannot ask it. `-Live` adds four cases that do:

```powershell
./tests/Test-Deck.ps1 -Exe .work/publish/statusai.exe -Live -Case 'live*'
```

| case | a press, then a hold |
|---|---|
| no terminal open | each opens a window, and it is in front |
| a terminal open behind another window | the tab goes to that window and brings it up; the hold brings up a new one |
| the terminal minimised | the window is brought back with its tab; the hold brings up a new one |

They are real presses. The plugin is given `STATUSAI_DECK_TAB`, the command a tab runs in place
of the default profile's own, and with that set a press is carried out even offline: its
`statusai --deck-press` starts Windows Terminal. The tab runs `ping` under the title
"statusai test" and closes by itself after three seconds, so no Claude Code session comes of
it, and the window that was in front is put back there. A case reports how the terminal got to
the front, without being asked for or after so many times, and how many handles the plugin
held before and after its two presses.

Where the plugin under test stands matters as much as what it does. Windows lets a program put
a window in front when it was started by the program in front, and so on down a line of
programs each started by the one before; a plugin started by the script is on such a line
whenever the script was started from the window in front, from a terminal or an editor. Its
terminal then comes to the front with nothing done for it, and a case would prove nothing. So
the plugin is started through WMI, by the service Windows has for that, with the script's
environment handed over, which is where the app's plugin stands. And the first of the four
cases is the control: a terminal started the same way, with nothing done for it, has to stay
behind the window in front. Where it comes to the front by itself, the control is skipped and
says so, and the three cases after it say that their presses prove nothing about a plugin.

The cases move windows about on your desktop, so they are only run on request, and they skip
themselves where they would be in the way: on a locked session, while a full-screen program, a
game or a presentation is in front, and while a Windows Terminal window of your own is open,
since a press would put its tab there. Nothing that was open before is closed, moved or resized.

## Deploying

Deploying replaces the binary that every open session runs, so it goes through a script rather than
a plain copy:

```powershell
./scripts/Deploy.ps1 -WhatIf   # what it would do, without doing it
./scripts/Deploy.ps1           # the publish output over the statusai.exe on PATH
```

It refuses unless `cship.exe` sits beside the target, stops if the target already has the build's
hash, runs the render tests against the build with that cship, backs the target up as
`statusai.exe.bak.<unix-seconds>` and verifies the backup, copies with retries, and verifies the
hash. If no copy landed, the target is still the previous binary and it stops there; if one landed
wrong, it restores the backup the same way. That is traps 2 and 3 below, handled in one script.
`-Source` and `-Target` name other files, so it can be tried on a scratch folder holding a
`cship.exe` and an older `statusai.exe`. Exit code 0 when deployed or already deployed, 1 when it
refused, 2 when the copy failed and the previous binary is in place, never having left or restored,
3 when even the restore failed, with the backup intact beside it.

A `-Target` that does not exist yet, in a folder with a `cship.exe`, is a first deploy, as when the
binary took the name `statusai.exe`: there is nothing to compare or back up, so it tests, copies
and verifies, and a copy that lands wrong is removed rather than restored. Exit code 2 then means
nothing is in place, and 3 that a wrong file could not be removed.

It was tried that way on 2026-09-24, under PowerShell 7 and Windows PowerShell 5.1, before its
first deploy: a deploy, a rerun that does nothing, `-WhatIf`, a build the render tests refuse, no
cship beside the target, no build and no target, a target held by a running copy of itself through
every retry, a copy that lands wrong and is restored, one whose restore fails too, and a target it
cannot read. Three faults turned up and are fixed. A target locked through every retry, which the
copy never touched, was restored all the same, the restore failed on the same lock, and it said so
with exit 3. A file it could not read stopped it with PowerShell's error rather than a refusal. And
under Windows PowerShell 5.1, `-WhatIf` reached into `Get-FileHash`, which then hashed nothing, so
the script stopped on an error; it hashes in .NET now.

After the rename it was tried again on scratch folders, under both: a first deploy beside a lone
`cship.exe`, a rerun that does nothing, `-WhatIf` on a first deploy and on a replace, a replace
with its backup, a build the render tests refuse on either, no cship beside the target, no build,
no folder, no `statusai.exe` on PATH, and a first deploy into a folder that refuses the copy, which
ends with exit 2 and nothing in place. All of them did what this section says.

### The Stream Deck plugin's copy

Where the [Stream Deck key](guide/stream-deck.md) is installed, the plugin's folder under
`%APPDATA%\Elgato\StreamDeck\Plugins` holds a copy of `statusai.exe` of its own, which the app
runs for as long as it is open. `Deploy.ps1` brings that copy in step once the status line's
binary is in place, or was in place already, so the key and the status line are one build:

1. It compares the plugin's manifest, its pictures and its `statusai.exe` with the repository's
   `streamdeck/` folder and the build. If nothing differs it says so and stops.
2. It copies the manifest and the pictures, moves the plugin's `statusai.exe` aside as
   `statusai.exe.old.<unix-seconds>`, copies the build in under its name and verifies its SHA-256.
   A copy that lands wrong is removed and the previous one moved back.
3. It ends the plugin's process, and the app starts it again by itself, from the new copy, a few
   seconds later; the script waits up to 30 seconds for that process, and removes the old file
   once nothing runs it. The app's own ways of restarting a plugin, the link
   `streamdeck://plugins/restart/<uuid>` and `StreamDeck.exe --restart <uuid>`, which hands it the
   same link, are turned away unless the app is in developer mode ("Feature only enabled in
   developer mode" in its log). A copy that an earlier run moved aside and that still runs is
   taken as a plugin that was not started again, and dealt with the same way.

`./scripts/Deploy.ps1 -Deck` installs the plugin where it is not installed yet: the folder is
created and filled, and Stream Deck has to be quit and started again to find it. Without `-Deck`
and without a plugin folder, nothing of this happens. `-DeckDir` names another plugins folder,
and with one the app is asked nothing, so this too can be tried on a scratch folder. Exit code 4
means the status line's binary is in place and the plugin's copy is not in step with it.

It was tried that way on 2026-10-01, under both PowerShells: no plugin folder and no `-Deck`,
`-WhatIf`, a first install, a rerun that finds everything in step, a copy held by a running
process, which is moved aside and replaced and whose old file the next run removes, and a plugins
folder that does not exist, which ends with exit 4. Step 3 has not been tried yet: nothing has
been deployed over a plugin the app was running.

## Releasing

```powershell
./scripts/Package.ps1 -Version 1.0.0
```

It refuses unless `CHANGELOG.md` has a `## 1.0.0 - <date>` section, and unless the cship a friend
is told to download is the one the render tests pin and the [install guide](guide/install.md)
fetches: cship 1.8.0, by URL and SHA-256. It publishes the build with that version, and refuses
unless `THIRD-PARTY-NOTICES.txt` names the .NET runtime the build compiled in, which it reads from
`src/obj/project.assets.json`, so the notices cannot fall behind an SDK update unnoticed. It runs
the render tests against the build with that same cship, byte for byte the download, and writes to
`.work/release/`:

| file | holds |
|---|---|
| `StatusAI-1.0.0-win-x64.zip` | `statusai.exe`, `cship.toml` and `starship.toml` from `config/`, a plain-text `README.txt`, `LICENSE` as `LICENSE.txt`, and `THIRD-PARTY-NOTICES.txt`, at the root of the zip |
| `StatusAI-1.0.0-win-x64.zip.sha256` | the zip's SHA-256, one line as `sha256sum` writes it |
| `notes-v1.0.0.md` | the release page's text: the version's CHANGELOG section with its links pointed at the tag, and a footer pointing at the install guide |

cship is linked, not bundled: it statically links some 43 crates whose notices a redistributor
would owe, so the install guide fetches it from cship's own release instead (see
[packaging-plan.md](design/packaging-plan.md#licensing)).

The zip does not carry the [Stream Deck key](guide/stream-deck.md) yet, which is installed from a
build with `Deploy.ps1 -Deck`. Before a release ships it, `Package.ps1` has to learn to build the
plugin's installer: a `com.sixfive7.statusai.streamDeckPlugin`, which is a zip with the
`streamdeck/com.sixfive7.statusai.sdPlugin` folder at its root and the release's `statusai.exe`
in it, and the manifest's `Version` set to the release's. The app installs such a file when it is
opened.

The script publishes nothing. Its last line is the `gh release create` command that would, to be
run from the repository root once the release commit is pushed. The exe records the commit it was
built at, as `1.0.0+<commit>` in its product version, so package from that commit; the script says
so when the working tree has uncommitted changes. `-OutDir` writes elsewhere and `-Cship` names the
cship to test with. Exit code 0 when packaged, 1 when it refused, 2 when the build or the packaging
failed; a run that does not finish leaves no zip of that version behind. Windows PowerShell 5.1 and
PowerShell 7 both run it.

## Trap 1: the cache holds the *previous* binary's figures

It can make a working change to the fetch look broken, and a broken one look fine.

`GetUsage()` returns the cached rows whenever `now - ts < 50`. The cache holds their figures, not
drawn text, so the drawing is always the running binary's own, at its own width. The figures are
not: the pace and the window checks behind them were computed by whichever binary fetched last. A
value the *old* binary wrote 20 seconds ago is drawn as it stands by the *new* one, and Claude Code
re-runs the status line every 60 seconds, so on any machine with a live session there is almost
always a fresh entry.

Until 2026-09-24 the cache held the drawn rows instead, in `val`, so a new build served its
predecessor's layout as well, the nastier half of this trap: freshly fetched percentages laid out
by code no longer on disk, which read as "the fetch works but my change did nothing". A build from
before then, restored from a backup, finds no `val` and fetches afresh.

When the fetch is under test, invalidate first:

```powershell
Set-ItemProperty -Path "HKCU:\Software\StatusAI" -Name "ts" -Value "0"
```

Then run it straight away. A live Claude Code window is racing you for the same mutex, and if it
wins, you get its figures instead. While the usage `⚠` row shows, a render also leaves the fetch
alone until the last attempt is 50 seconds old, so set `tryTs` to `0` the same way.

That is a write to state every running session shares, and the test run that follows fetches and
pushes a sample into the shared prediction history. When what is under test is the drawing rather
than the fetch, skip all of it: the [offline render](#offline-beside-live-sessions) never reads the
cache, so it has no trap to invalidate.

## Trap 2: the binary is locked while the status line runs it

Windows holds the image while the process runs, and it runs every 60 seconds for ~100 ms. A
straight copy fails intermittently, so [Deploy.ps1](#deploying) retries it, twelve times, 700 ms
apart.

Back up the outgoing binary first. The convention on the dev machine is a sibling
`statusai.exe.bak.<unix-seconds>`, which makes a revert a copy rather than a rebuild; the script
writes that backup and verifies it before it copies anything.

## Trap 3: `dotnet publish` output is not what is deployed

The status line runs the binary at its installed location, not the publish directory. Editing
`Program.cs` and rebuilding changes nothing visible until the copy step runs. The other way round,
a revert that only restores the installed binary leaves the publish tree holding the change, ready
to be redeployed by the next person who runs the copy step. So revert the source, rebuild and
redeploy, and all three stay consistent.

## Testing a render

The binary reads the status-line payload on stdin. `tests/fixtures/payloads/` holds a captured
mid-session one, `mid-session.json`, beside the rest the [render tests](#the-render-tests) use.

### Offline, beside live sessions

`STATUSAI_OFFLINE=<dir>` renders from that directory instead of the machine's shared state. The
limit rows are drawn from `<dir>/usage.json` and `<dir>/rows.json` rather than the registry cache
and the API, the euro rate comes from `rows.json`, and `<dir>` stands in for the user profile: the
account line reads `<dir>/.claude.json` and `<dir>/.claude/.credentials.json`, `statusLine.padding`
comes from `<dir>/.claude/settings.json`, and the token cache is written under
`<dir>/AppData/Local/StatusAI/tokens/`, where `%LOCALAPPDATA%` would be. `HKCU\Software\StatusAI`
is neither read nor written, the usage lock is never taken, and nothing is fetched, so a dev build
can run beside live sessions without drawing their cached rows or touching their history.

Given a `usage.json` (a usage API response, verbatim or edited), the binary parses it with the same
`ParseUsage()` as a live fetch, so the meters, the product breakdown, the on-credit alarm and the
meters notice are under test. `rows.json` then supplies what a live fetch would take from the
registry: the clock to measure the resets against, the scoped meter already being followed, and the
forecast inputs by label. Which meters are drawn and which are flagged is the same `Known()`
decision a live fetch makes; a drawn meter the forecast leaves out is gated.

```json
{ "fx": 0.876, "now": "2026-09-23T20:14:20Z", "sn": "Fable",
  "forecast": { "5h":    { "rate": 28.8,   "gated": false },
                "7d":    { "rate": 6.7039, "gated": false },
                "Fable": { "rate": 0,      "gated": false } } }
```

Without one, `rows.json` holds what `RenderRows` is given (the output of the fetch, the window
checks and the slope), and there is no breakdown, no credit state and no meters notice to draw:

```json
{ "fx": 0.876,
  "rows": [ { "label": "5h",    "pct": 22, "hrs": 3.383, "hasReset": true, "rate": 10.42, "gated": false, "sev": "normal"   },
            { "label": "7d",    "pct": 89, "hrs": 57.08, "hasReset": true, "rate": 2.4,   "gated": false, "sev": "critical" },
            { "label": "Fable", "pct": 24, "hrs": 57.08, "hasReset": true, "rate": 0,     "gated": false, "sev": "normal"   } ] }
```

Either way the forecast is an input, so the window checks and the slope are not under test.

A `fetch` in either shape stands in for the count of failed fetches the shared state keeps, and for
the fetch this render makes (see [limits.md](reference/limits.md#when-a-fetch-fails)):

```json
"fetch": { "fails": 1, "why": "timeout", "attempt": "timeout", "okAt": "2026-09-23T20:12:20Z" }
```

`fails` and `why` are the failures in a row so far and the latest reason, as the registry's `fail`
and `why` hold them. `attempt` is this render's fetch: `ok`, the default, draws the response at
`now`; `none` makes no attempt, as when another session holds the lock; anything else fails with
that reason, as `timeout`, `offline`, `no token`, `http 429` or any text. `okAt` is when the last
good fetch was made, the registry's `ts`, and `triedAt` when the fetch was last tried, its `tryTs`,
none if left out. With `fails` at 2 or more and `triedAt` under 50 seconds before `now`, the render
makes no attempt, whatever `attempt` says, as a live one would not. A render without a good fetch of
its own draws the rows as that fetch measured them, or none if the fixture has none. Whether it
tries, how the count moves and how the row reads are as in a live render, through the same
`MayRetry()`, `AfterFetch()` and `FetchWarn()`.

Keep the hours exact when comparing builds. A `usage.json` and a `rows.json` that should draw the
same rows must agree on the hours to each reset down to the last bit: .NET's `TotalHours` is
`(double)ticks / TicksPerHour`, so derive `hrs` as integer ticks over 36.000.000.000, not from
floating-point seconds, or a rounding step can land on the other side of a minute.

Point the payload's `transcript_path` inside `<dir>` as well. cship keeps its own cache beside the
transcript, in `<transcript dir>/cship/`, so that keeps its writes out of `~/.claude/projects` too.

```powershell
$env:STATUSAI_OFFLINE = "<dir>"
$out = Get-Content -Raw "<payload>.json" | & "<publish>\statusai.exe"
Remove-Item Env:STATUSAI_OFFLINE
```

A payload is only a fixture if it is what Claude Code really sends. Before its first response, a
brand-new session looks like this, and the three nulls are the point (see
[layout.md](reference/layout.md#no-messages-yet-is-a-zero-not-a-gap)):

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
$out = $stdin | & "<path>\statusai.exe"
```

### Checking the output

Check alignment on the stripped text, not on the coloured output, because SGR escapes make every
visual length wrong:

```powershell
($out -replace "`e\[[0-9;]*m", "") -split "`n" |
  ForEach-Object { "{0,3} |{1}|" -f $_.Length, $_ }
```

Look for a specific glyph by its codepoint rather than by pasting the character into a script, since
encoding round trips through the shell are not reliable:

```powershell
$out -match ([char]0x2503)      # ┃
```

### The key

`statusai --deck-face` prints the Stream Deck key as SVG, which a browser shows. Offline it draws
the fixture's rows, and a number after it is the seconds since their fetch, which is how the
countdowns, `→idle` and a window that has ended are looked at without waiting for them. The render
tests write every key they draw with `-OutDir`:

```powershell
./tests/Test-Renders.ps1 -Exe .work/publish/statusai.exe -Case 'key*' -OutDir .work/keys
# -> .work/keys/key.svg, key-idle.svg, key-failing.svg, ...
```

Without `STATUSAI_OFFLINE` it draws the live cache, which it only reads. The layout is the block
of numbers above `Face()` in `Deck.cs`: where the label, the percentage, the cells and the small
line sit on a canvas of 144 by 144, and how large they are. After a change, record the faces with
`Test-Renders.ps1 -Update -Case 'key*'`, read the diff, and regenerate the figures, which also
rewrites the plugin folder's pictures from them.

The app draws the SVG with QtSvg, which follows SVG Tiny 1.2 and so knows less than a browser
does. `Face()` keeps to plain shapes and text with attributes for that reason, and a browser
showing a new face right does not settle it: `QSvgRenderer` from PySide6, in the version of the
`Qt6Svg.dll` in the app's folder, draws it with the engine the app has.

### The key on a live machine

The app is the one part no test stands in for. To look at a build on a Stream Deck without
replacing the status line's binary, give the plugin a copy of the build of its own:

```powershell
dotnet publish src/StatusAI.csproj -c Release -r win-x64 -o .work/publish
./tests/Test-Renders.ps1 -Exe .work/publish/statusai.exe
./tests/Test-Deck.ps1 -Exe .work/publish/statusai.exe
# with Stream Deck quit:
$plugin = "$env:APPDATA\Elgato\StreamDeck\Plugins\com.sixfive7.statusai.sdPlugin"
Copy-Item -Recurse streamdeck/com.sixfive7.statusai.sdPlugin $plugin
Copy-Item .work/publish/statusai.exe $plugin
```

Start the app again and put *StatusAI* > *Claude Code* on a key. The status line goes on running
the binary it had, and a plugin that fails cannot reach it: they are two files and two processes.
What they share is the registry cache, which the plugin only ever writes through `--refresh`,
that is through the `GetUsage()` of its own build. So a build that changes how the cache is kept
is the one thing not to try this way beside live sessions.

To take it out again, quit the app, delete that folder and remove the key.

## Trap 4: an account switch looks like a regression

`AccountInfo()` is read fresh from `~/.claude.json` on every render, and a changed `accountUuid`
clears `hist` outright. Immediately after a switch every row reads `→ early` and the percentages
belong to a different account, so they will not match anything observed a minute earlier. That is
`WindowCheck` and the account guard working, not a fault. Check the account line before
investigating a sudden change in the numbers.

## Trap 5: the plugin's copy is never free

The status line's binary is locked for the 100 ms of each minute it runs. The Stream Deck plugin
runs for as long as the app does, so its copy,
`%APPDATA%\Elgato\StreamDeck\Plugins\com.sixfive7.statusai.sdPlugin\statusai.exe`, is locked the
whole time: a copy over it fails on every retry, and so does a delete. Windows does let a running
program be moved aside, and a new file take its name, which is what
[Deploy.ps1](#the-stream-deck-plugins-copy) does; the process keeps running the old file until the
app restarts the plugin.

That is also why the plugin has a copy of its own and is never pointed at the status line's
binary. A process that held `statusai.exe` for good would make every deploy fail, and the
updater in [packaging-plan.md](design/packaging-plan.md#the-status-line-is-its-own-update-host)
counts on that file being free 99,8% of the time.

Two copies can be two builds. As long as both keep the cache the same way that costs nothing, and
`Deploy.ps1` keeps them one build anyway. The line the plugin logs as it starts,
`statusai 1.0.0 of 2026-10-01 19:55 started as the Stream Deck plugin` in
`%APPDATA%\Elgato\StreamDeck\logs\com.sixfive7.statusai0.log`, carries the version and the time its
file was written, which a copy keeps, so it can be told from the status line's.

## Verifying the accounting

Presentation work leaves the accounting alone, but run these after any Claude Code upgrade: the
transcript layout is undocumented, and when it changes, the counts fail silently downward.

```powershell
./scripts/Split-MainVsTree.ps1 -Sid <session-id>
./scripts/Verify-Tools.ps1     -Sid <session-id>
./scripts/Decode-TokCache.ps1  -Sid <session-id>
```

See [accounting.md](reference/accounting.md) for the expected figures. The two walkers also take
`-ProjectsRoot`, so they check the render tests' showcase tree as well, where they must agree with
its token rows: 60.333.978 main, 5.081.453 sub, and 101 and 129 tool calls.

```powershell
$root = (Resolve-Path tests/fixtures/homes/showcase/projects).Path
./scripts/Split-MainVsTree.ps1 -Sid 22222222-fixt-fixt-fixt-000000000002 -ProjectsRoot $root
./scripts/Verify-Tools.ps1     -Sid 22222222-fixt-fixt-fixt-000000000002 -ProjectsRoot $root
```

## Keeping the docs

- Text files are LF whatever `core.autocrlf` says, which `.gitattributes` takes care of.
- Every page under `docs/` opens with a way back, `<sub>[StatusAI](...) / [Documentation](...)</sub>`
  with the paths relative to the page, then its title and a sentence saying what it covers.
- The docs keep one voice: plain declarative sentences, nl-NL numbers (`4,83 MiB`, `53.949.804`),
  percentages as the status line prints them (`28%`, no space), and a width cost for any design
  that touches a row.
- The PNGs in `docs/assets/` are generated from `tests/expected` by
  `node docs/assets/figures.gen.js`, which also rewrites `docs/assets/README.md`. Don't edit an
  image by hand. After a change of output has been recorded with `-Update`, regenerate them and look
  at what changed.
- The same run writes the pictures in `streamdeck/com.sixfive7.statusai.sdPlugin/imgs`, except
  `action.svg`: the plugin's icon is the key of the `shot` fixture, and `key.svg` is the blank key
  as the binary draws it. They are generated too, so a change to the key's face reaches them.
- The two HTML pages under `docs/design/` are generated as well: edit the `.gen.js` or its data and
  regenerate. The decisions page's generator refuses to run unless its port of the drawing code
  reproduces every captured render.
- The decisions page and `docs/design/rejected-designs.md` are records. Add to them, but don't
  rewrite what was decided.
- `docs/design/house-style/weekly-wall.html` documents a feature that was reverted because its
  premise was false, and is kept only as a style reference. Read
  [its README](design/house-style/README.md) before opening it, and don't implement anything it
  describes.
