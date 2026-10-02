<sub>[StatusAI](../../README.md) / [Documentation](../README.md)</sub>

# Architecture

The parts of the status line and how they fit together: the render chain, what draws each row, how
often each source refreshes, the state kept between renders, the Stream Deck key the same exe
draws, and what would have to change to move it elsewhere.

## The render chain

```
Claude Code  --stdin JSON-->  statusai.exe  --stdin-->  cship.exe  -->  starship
  statusLine hook           (this repo, MIT)            (Apache-2.0)    (ISC, optional)
  refreshInterval: 60               |
                                    +--> transcript tree      tokens, tool calls
                                    +--> api.anthropic.com    5h / 7d / scoped limits
                                    +--> ecb.europa.eu        EUR reference rate
                                    +--> .credentials.json    OAuth token, plan tier
```

`statusai` reads the status-line payload on stdin, pipes it through `cship`, takes its stdout,
appends the meta segment to the last non-empty line, and inserts the token rows and limit rows
beneath it. If `cship` returns nothing, as it does with a payload it cannot parse, there is no line
to append to, and the block is emitted on its own with the meta segment on its first row rather than
dropped; see [layout.md](layout.md#when-there-is-no-host-line).

`cship` has to be in the same directory as `statusai`. Windows searches the calling executable's
own directory before PATH, and if `cship` isn't found, the failure path is `catch { return input; }`,
which echoes the entire raw session JSON onto the status line. Keeping the two together is a
requirement, not a convenience.

Without starship on PATH the `$starship_prompt` line silently vanishes and cship's own modules still
render. That degrades gracefully, which is why starship is optional. `cship` does honour a
pre-existing `STARSHIP_CONFIG` environment variable, though, so someone who has set one globally
will silently get their own config rather than the shipped one.

## What each row is

| row | produced by |
|---|---|
| prompt line | starship, via cship; includes RAM (`memory_usage`) and the GPU (`custom.gpu`, which shells out to `nvidia-smi` on **every render**) |
| model line | cship (`$cship.model $cship.effort $cship.context_bar`) + our meta segment (⏱ 📝 💸 💰) |
| two token rows | this repo |
| 5h / 7d / scoped rows, account line and product breakdown, the meters notice and the on-credit ⚠ row | this repo, from the OAuth usage endpoint (see [limits.md](limits.md)) |

## Update cadence

Four clocks, stacked:

| layer | interval | trigger |
|---|---|---|
| Claude Code running `statusai` | `refreshInterval: 60` | plus every new assistant message, after `/compact`, on permission-mode change; debounced 300 ms |
| OAuth limit bars, product breakdown, on-credit alarm, ignored meters | cached **50 s** | `FreshVal`: `now - t < 50`, else re-fetch behind a named mutex; a failed fetch leaves it stale, so the next render retries; from the second failure in a row, only once the last attempt is 50 s old (`MayRetry`) |
| EUR rate | cached **24 h** | ECB daily feed |
| token rows, account line | **every render** | incremental parse of appended bytes only |

A 60 s refresh against a 50 s cache means an idle render almost always finds the cache stale, so the
usage endpoint is hit roughly once a minute while a session is open. The cache only earns its keep
during bursts. Setting the cache to ~70 s, or `refreshInterval` to 45, would make it actually cache.

**The cache holds the rows' figures, not the drawn rows.** `rows` has a line per limit row: its
label, percentage, hours to its reset, whether it has one, pace, whether the pace is still gated,
and severity. Every session draws them at its own width, which comes from its own terminal, so rows
drawn once would be the wrong width in any session of another. Beside them, `bd`, `cr` and `ig`
hold the product breakdown, the on-credit alarm and the meters it did not draw from the same fetch,
already reduced to what is shown, and those are laid out per render as well. So the drawing is
always the running build's own; what a cached entry still carries from the build that fetched it is
the figures, the pace above all. On a machine with a live session that is the trap described in
[development.md](../development.md). Until 2026-09-24 the cache held the drawn rows, in `val`, and
a new build served its predecessor's drawing too. `fail` and `why` count the fetches that have
failed in a row and keep the latest reason, for the `⚠` row every session draws from them, and
`tryTs` is when the fetch was last tried, which spaces the retries 50 s apart once that row shows;
see [limits.md](limits.md#when-a-fetch-fails).

## State

| location | contents |
|---|---|
| `HKCU\Software\StatusAI` | limit-bar cache (`ts`, `rows`, `bd`, `cr`, `ig`, `fail`, `why`, `tryTs`, `hist`, `acct`, `sn`, `rsS/rsW/rsF`, `vfS/vfW/vfF`), FX rate (`fx`, `fxTs`) |
| `%LOCALAPPDATA%\StatusAI\tokens\<sid>.bin` | `CTK2` token cache: offsets, running totals, two dedup sets |
| named mutex `Global\StatusAI.fetch.<SID>.{adm\|std}` | single-flight on the usage fetch, scoped per user *and* elevation level |
| `%APPDATA%\Elgato\StreamDeck\Plugins\com.sixfive7.statusai.sdPlugin\` | the Stream Deck plugin, where it is installed: its manifest, its pictures and its own copy of `statusai.exe`. It keeps no state of its own |

Legacy, no longer written but possibly still on disk: `~/.claude/statusline-usage.json` and
`statusline-cache.json`, and `HKCU\Software\cshipUsage` and `~/.claude/statusline-tokens`, which
held the registry cache and the token cache before the rename to StatusAI. In that old key, `val`
held the drawn rows until 2026-09-24, and the first good fetch after that switch removed it.

With `STATUSAI_OFFLINE` set, a switch for development (see
[development.md](../development.md#offline-beside-live-sessions)), none of the three is touched: the
rows and the euro rate come from a file in that directory, and the token cache and the account files
move into it.

## The Stream Deck key

The same exe is also the plugin behind a key on an Elgato Stream Deck; what the key shows and
does is in [stream-deck.md](../guide/stream-deck.md). The app starts the plugin's own copy of
`statusai.exe`, from the plugin's folder, with `-port`, `-pluginUUID`, `-registerEvent` and
`-info`. `Program.cs` sees those before it reads stdin and hands over to `Deck.cs`, and the
process then stays up for as long as the app does.

```
Stream Deck  --starts-->  statusai.exe, the plugin's copy  <--websocket-->  Stream Deck
                                |
                                |  waits on    the websocket, HKCU\Software\StatusAI, ~/.claude/projects
                                +--> statusai.exe --refresh       GetUsage(): the 50 s cache, the lock, Fetch()
                                +--> statusai.exe --deck-press    a key press: wt.exe, and its window to the front
```

It waits and never polls. Three things wake it, and a timer for what is due:

| it waits on | which is signalled by | and then |
|---|---|---|
| the websocket to the app | a message: the key appears or disappears, is pressed or released, a deck came back, the system woke up | draws the key, or acts on the press |
| a change notification on `HKCU\Software\StatusAI` | a fetch by anyone: a terminal's status line, or `--refresh` | draws the key 100 ms later, once every value of the fetch has landed |
| a change notification on `~/.claude/projects` and everything under it | a write by any Claude Code session of any kind, which is where they keep their transcripts | notes that Claude is working, and refreshes when a refresh is due |
| a timer | the next minute, the half second of a held key, the next refresh, a fifth of a second while a press is opening its terminal | redraws the countdowns, makes a press a long one, refreshes, reads how the press ended |

The folder's notification stays signalled until it is armed again, so a burst of writes is one
wake-up: 65.900 appends in 30 seconds cost the process two wake-ups and no processor time that
could be measured.

**It never fetches by a path of its own.** When the usage is due it starts this exe again with
`--refresh`, which calls `GetUsage()` and exits: the same 50 second cache, the same lock, the same
`Fetch()`, the same count of failures as a render, so a terminal and the key can never both fetch.
The result reaches the key the way a terminal's fetch does, through the registry. It is a second
process rather than a call so that a fetch, which can take its three seconds, never holds up a key
press, and so that the HTTP client stays out of the process that stays up: 13,3 MB in use and
3,9 MB private as it is, against 18,3 MB and 5,2 MB with the fetch inside it.

A refresh is due 62 seconds after the last fetch or attempt by anyone, and at once after a quiet
spell. The 62 keeps it behind a terminal's own 60: once a status line has fetched, its next fetch
comes two seconds before the key's would, so the key never comes round to one for as long as that
terminal is open. A key that is ahead of a terminal sitting idle, whose renders then find the
copy fresh, loses those two seconds every minute, and the terminal's render comes first in
under half an hour. Whichever of the two fetches, it is one fetch a minute, and `--refresh` is
not even started while the shared copy is under 50 seconds old.

Arming the folder's notification again reports the writes made since it was last signalled, so
the last write of a burst is followed by one more refresh a minute later, which is the one that
sees what the last reply cost; after that nothing is fetched until Claude writes again. And
nothing is refreshed while no key is visible: the write stays noted until one is.

**A key press is a third process, `statusai --deck-press tab` or `window`.** It starts
`wt.exe -w 0 nt` or `wt.exe -w new` and brings the terminal's window to the front, which Windows
does not do for a program in the background: it keeps the foreground for the program the user is
working in, and one that asks for it from behind gets a flashing taskbar button. So the window
is brought up from that process, in the ways Windows lets through, tried in turn until
`GetForegroundWindow()` says it is there:

1. as the program that last provided input: an input event that moves nothing and presses
   nothing is sent first, then `SetForegroundWindow`;
2. as part of the program in front, with `AttachThreadInput` for the moment;
3. as after a press of Alt, which lifts the guard for everyone. Twice at most, since that one is
   seen by the program in front.

For a tab, the window it will go to is brought up before the tab is asked for: the terminal
window on top of the others on this desktop, a minimised one only when there is no other. The
window in front is the one Windows Terminal counts as used last, so the tab opens where the user
is already looking. For a new window, the process waits for one that was not there before, up to
eight seconds, and brings that up. It ends with 0 when the terminal was in front without being
asked for, 10 plus the number of times it had to be asked for, 1 when it did not get there, and
100 plus Windows' error when `wt.exe` could not be started, which the plugin shows as an alert
on the key and one line in the log.

It is a process of its own for what it would otherwise leave behind. `wt.exe` is a Store app's
alias, and starting one loads Windows' app model and the shell into the process that does it:
63 handles, 17 libraries and 3 MB that stay for good, which is what the plugin was found
holding a day after its first press.
The plugin looks at the process every fifth of a second until it has ended, a second or so.

**Nothing the app hands down is handed on.** The app starts a plugin with a dozen of its own
handles open to it, its log files and its crash reporter's lock among them, and a program
started from the plugin would be given them in turn: a terminal opened by a key press would hold
the app's log open for as long as it runs. The plugin marks every handle it has as its own
before it starts anything.

**It draws by the status line's own rules.** `Program.cs` hands `Deck.cs` its functions in a
`Deck.Host`: `Look`, the rows as they stand without a fetch, which is `Saved()`; `Pace`, the time
to 100% and its colour, which `RenderRows` uses too; `BarFill`, `ZoneColor`, `PctColor`,
`SevColor` and `Hm`. So a cell on the key is lit and coloured by the same code as a cell on the
status line. The key is an SVG, 144 by 144, that the app scales to the device; its layout is a
block of numbers at the top of the drawing code. `statusai --deck-face` prints it, which is how
the [render tests](../development.md#the-render-tests) hold it to its recorded bytes.

**The websocket client is written out** in `Deck.cs`, about a hundred lines of RFC 6455: one
connection, text messages, no extensions. The framework's own would have cost 282 kB in the exe
and a thread pool in the process; with this one the whole mode adds 91 kB, and the process runs
on three threads. [Test-Deck.ps1](../development.md#the-stream-deck-tests) takes it through the
protocol against a stand-in for the app.

**When the socket closes, the process ends.** With exit code 0 when the app closed the connection,
which is answered with a close in return, and 1 after anything else: a connection that drops, a
frame the protocol forbids (answered with a close of its own, status 1002, or 1009 for a message
over 16 MiB), a handshake that is refused or goes unanswered for ten seconds. There is nothing to
draw on without the app, and an app that is still there starts its plugin again.

It writes no log of its own. The app keeps one per plugin,
`%APPDATA%\Elgato\StreamDeck\logs\com.sixfive7.statusai0.log`, and the process sends it one line
when it starts and one for anything fatal or for a program it could not start.

Two details of running as a plugin. The exe is a console program, so the app gives it a console
host of its own, `conhost.exe`, 7,5 MB that nothing writes to: the process lets go of it with
`FreeConsole()` as it starts. And which keys show is the app's to say, with `willAppear` and
`willDisappear`: the plugin keeps no list of decks, so the order in which the app reports a
deck and its keys cannot matter to it. When a deck comes back, after a lock or a sleep or a
cable, or the system wakes up, it draws the keys that show again whether or not their picture
changed, since the deck may have lost it.

With `STATUSAI_OFFLINE` set, the mode touches nothing live, like a render: the rows come from the
fixture, the registry is not watched, the `--refresh` it starts has nothing to fetch, and a key
press starts nothing. Both are reported to the app's log, which is what the tests read. Two more
variables are for trying it: `STATUSAI_DECK_TAB`, what the tab or window of a press runs in
place of the default profile's command, with which a press is carried out even offline; and
`STATUSAI_DECK_SLOT`, the seconds between refreshes, which only an offline plugin takes.

The mode costs the status line nothing that could be measured. The exe is 91.136 bytes larger,
5.174.272 against 5.083.136; 60 offline renders of each build, taken in turn, had a median of
69,8 ms against 69,3 ms, and the same 11,9 MB at their peak.

## Portability

The token accounting is fully portable, but the code around it uses a few Windows-only APIs:

| Windows-only API | belongs to |
|---|---|
| `Registry.CurrentUser` (9 sites) | limit-bar cache, and the FX rate cache |
| `WindowsIdentity` / `WindowsPrincipal` | mutex naming |
| `Global\` mutex | single-flight on the usage fetch |
| `RegNotifyChangeKeyValue`, `FindFirstChangeNotificationW`, `FreeConsole`, `SetHandleInformation`, `SetForegroundWindow`, `SendInput` and the other window calls | the Stream Deck key, which is Windows-only as a whole: it opens Windows Terminal |
| `net10.0-windows` TFM | consequence of the above |

Everything in the walker is cross-platform: `Path`, `FileStream`, `BinaryReader`, `JsonDocument`,
`Environment.SpecialFolder.UserProfile` and `LocalApplicationData`. The transcript tree is located
from `transcript_path` in the stdin payload, so no path is hardcoded.

A Linux/macOS port means replacing the registry with a JSON file and the mutex naming with a lock
file. The accounting needs no changes.

| move it to | what breaks | fix |
|---|---|---|
| Linux / macOS | registry, mutex naming, TFM | about an hour |
| a different terminal width | nothing from Claude Code 2.1.153 on, which sets `COLUMNS` to the terminal's width; before that the width is 141 unless `STATUSAI_WIDTH` says otherwise | none |
| a terminal rendering emoji single-width | the grid: `iw[]` declares 4 columns per 2-emoji block | one array |
| a machine without a Nerd Font | cship/starship glyphs, **not** the token rows | see below |
| an API-key-only account | the limit rows and the account line | nothing; it degrades |
| an Enterprise plan | untested: whatever of the session, `weekly_all` and a model-scoped `weekly_scoped` it sends is drawn; anything else is flagged for review | none |
| a machine without an NVIDIA GPU | starship's `custom.gpu` segment | none |
| a different model | nothing: the cost comes from the payload, and there is no price table | none |

The binary itself uses no Nerd Font glyphs, only emoji and `│ ● ○ ✗ ↻ → ⇢ · — … ⚠`. All 45 patched
codepoints in the prompt line come from starship's config, and cship's model line adds two more.
Dropping starship removes ~90% of the font requirement.

## Build

.NET 10 SDK, `PublishAot`, `InvariantGlobalization`, `net10.0-windows`, x64.

```
dotnet publish src/StatusAI.csproj -c Release -r win-x64 -o <out>
```

Output is about 4,9 MiB and really self-contained: no `hostfxr`, no `coreclr` and no VC++
redistributable, because NativeAOT statically links the C++ runtime and uses only the in-box UCRT.
There is **no ARM64 build**, though cship and starship both publish one.

`InvariantGlobalization` means no named culture exists at runtime, which is why nl-NL formatting is
done by swapping separators on invariant output rather than by `CultureInfo`.
