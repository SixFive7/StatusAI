# Changelog

What changed, newest first. Releases are headed by their version; what is not in a release yet is
under *Unreleased*, and the entries before the first release are by date.

## Unreleased

**A switch seen with Claude idle, and smaller fixes**

- The Stream Deck key sees a token that nothing has fetched with, after a switch of account or a
  renewal, at its next redraw, within a minute, Claude working or not. Once the switch is 15
  seconds old it starts one `--refresh` for it, which takes claude-swap's figure for the new
  account where it has a fresh one and fetches otherwise. Once a token, whatever comes of it, and
  not while a 429 holds the token off. The 15 seconds keep the request out of the burst a switch
  sets off. Before, the key refreshed only while something was being written, so after a switch
  with Claude idle and no terminal open it said `new account` until the next prompt. Nothing
  watches `.credentials.json`. See [when the token changes](docs/reference/limits.md#when-the-token-changes).
- With no figures at all, on a first run or with the registry emptied, the first failed fetch
  raises the `⚠` row at once, `⚠ usage — timed out after 3 s; no limit rows yet`, where it waited
  for the second; the key says `⚠ timeout` and `no rows yet`. With nobody signed in the status line
  stays quiet, as before.
- `.credentials.json`, `~/.claude.json`, `settings.json`, claude-swap's table and the token cache
  are read with the sharing that lets another program replace them meanwhile.
  `.credentials.json` was read with `File.ReadAllText`, which lets others only read, so for the
  milliseconds StatusAI had it open, Windows refused claude-swap's switch and Claude Code's renewal
  of the token, which both move a new file over the old one.
- A `~/.claude.json` that does not parse is read once more, 25 ms later, before the account counts
  as unreadable. On Windows claude-swap rewrites that file in place, so a read during a switch could
  find it cut short ([claude-swap#461](https://github.com/realiti4/claude-swap/issues/461)), and the
  account line then said `not signed in` for a render.
- A change of the model behind the scoped weekly row clears that row's reset time as well as its
  series, as a switch of account does, so its series no longer restarts a second time when the new
  model's meter first had no reset time.
- The usage request names the installed Claude Code's version, `claude-code/2.1.296` say, where it
  always said `claude-code/2.1.90`. It is the `version` the status-line payload carries, which a
  render keeps in the registry for a day, `ccv` and `ccvTs`, so that the key's `--refresh` sends it
  too; or else, as where Claude Code only runs in VS Code and no status line runs, the newest
  version named by the VS Code extension's folders or the native installer's, compared as numbers,
  looked for once a day at most and kept the same way; and `2.1.90` only where there is neither.
  Nothing is started to find it. `statusai --user-agent` prints what the next request sends. See
  [the endpoint](docs/reference/limits.md#the-endpoint).
- `Deploy.ps1` warns, in yellow, when the PC has no Bahnschrift, whenever it installs the Stream
  Deck plugin or brings it in step: the app then draws the key in Tahoma, and its two times on a
  line run into each other. The deploy goes on. The key keeps one typeface to a text, since a
  fallback list is Tahoma on every PC; see [the key, readable](docs/design/stream-deck.md#the-key-readable).
- [limits.md](docs/reference/limits.md#response-shape) said the older top-level `five_hour` and
  `seven_day` objects carry no reset time. Every response captured carries a `resets_at` in them;
  it is the fallback that reads only `utilization`.
- Nine render cases, four of the status line, one of the key and four of `--user-agent`, which a
  case with `args` runs: 219 renders of 109 cases, the 209 before unchanged. One Stream Deck case,
  19 in all. The exe is 16.384 bytes larger.

**A key that can be read**

- The Stream Deck key is drawn in Bahnschrift, the DIN typeface Windows comes with: the label and
  the percentage in its semi-condensed width, the small lines in its condensed one. On a key of 96
  pixels the capitals of the small lines grow from 6,3 to 9,2 pixels, those of the percentage from
  11,7 to 14,9 and those of the label from 10,7 to 13,5. `↻`, whose glyph read as a "u" at that
  size, is drawn as a shape, and `→` stays. It is the face chosen from six drawn on 13 October; see
  [the key, readable](docs/design/stream-deck.md#the-key-readable).
- Every text names one font family. The app's QtSvg reads a `font-family` list as one name that it
  does not have, and draws that text in Tahoma, so a fallback list would have undone Bahnschrift.
- All 19 key faces are recorded again; the status line's 190 renders are unchanged. The figure
  generator gives Edge Bahnschrift at the widths the app draws, and the key's figures and the
  plugin's pictures are drawn again. The exe is 4.608 bytes larger.

**Fewer requests after a refusal and at a switch**

- A 429 whose `Retry-After` says how long it holds now holds the fetch off until then: no render and
  no `--refresh` tries the token that drew it before that time, and the Stream Deck key starts no
  refresh. On 13 October a 429 held every caller off for 300 seconds after a switch, and StatusAI
  asked four more times inside it. The hold is an hour at most, where Claude Code caps it as well;
  a 429 without the header is retried as before. See
  [a 429 that says how long](docs/reference/limits.md#a-429-that-says-how-long).
- The `⚠` row comes at the first such 429 and names when it lifts:
  `⚠ usage — HTTP 429 from api.anthropic.com until 12:49:50; the limit rows are 5m old`, 15 columns
  more than the same row without it, 95 at the longest. The key says `⚠ 429 until 12:50`, to the
  minute and rounded up.
- At a switch to another account, claude-swap's figure for that account, if it fetched one after
  the switch and under a minute ago, is drawn in place of StatusAI's own fetch, so that it does not
  join the burst of requests a switch sets off. The next regular fetch adds what claude-swap does
  not carry: the severity, the breakdown, the on-credit alarm and the meters notice. Nothing needs
  claude-swap: without it, or with a file that does not hold, StatusAI fetches as before. See
  [claude-swap's figure at a switch](docs/reference/limits.md#claude-swaps-figure-at-a-switch).
- Nineteen render cases for it, sixteen of the status line and three of the key: 209 renders of
  100 cases. The exe is 17.408 bytes larger.

**Switching accounts**

- The shared usage is tied to the token it was fetched with. A good fetch stores the first 8 hex
  digits of the token's SHA-256 beside the account, never the token. A render or a refresh that
  finds another token in `.credentials.json` fetches at once, through the lock, instead of drawing
  a copy up to 50 seconds old, and a token not tried yet is tried at once even while the old one's
  retries are being held off.
- The previous account's figures are never drawn as the new one's. Until the new account's first
  good fetch the status line has no limit rows, and if that fetch fails its `⚠` row comes at once:
  `no limit rows for the new account yet`. The Stream Deck key greys both meters with no figures
  in them and says `new account`. A token Claude Code renews for the same account keeps the
  figures as they were while its fetch runs. See
  [when the token changes](docs/reference/limits.md#when-the-token-changes).
- A response that a switch overtakes, whose token is no longer the one in `.credentials.json`
  when it arrives, is dropped, and the next render fetches with the new token.
- An account switch now clears the reset times the window checks compare with, as well as the
  history. They were kept, so a 5h meter that had no window open at the switch restarted its
  series a second time once its window opened, five minutes after a switch on 13 October.
- Seven render cases for it, four of the status line and three of the key: 188 renders of 81
  cases. The exe is 5.120 bytes larger.

**The Stream Deck key**

- `statusai.exe` is also the plugin behind a key on an Elgato Stream Deck. The key draws the 5h
  and 7d rows with the status line's own cells and colours, their countdowns to the reset and their
  times to 100%. A short press opens a new Claude Code tab in the Windows Terminal window used
  last, and a hold of half a second a new window. See
  [the Stream Deck key](docs/guide/stream-deck.md).
- Either press ends with the terminal in front of whatever was there, which Windows does not do
  for a program in the background: the window is brought up, a minimised one is brought back, and
  the tab opens in the terminal window that was on top of the others.
- The key keeps the shared usage up to date while Claude works where no status line runs, in the
  VS Code extension for instance: once every 62 seconds while a session is writing its transcript,
  once more a minute after the last write, and not at all while Claude is idle, while the key is
  not visible, or while a terminal's status line is fetching already. It fetches through
  `statusai --refresh`, which is a render's own fetch with nothing drawn, so the 50 second cache
  and the lock are shared and the two never add up.
- When fetching fails twice in a row the key greys its meters and says why and how old they are,
  as the status line's `⚠` row does; once a limit's window has ended it reads 0% until the next
  fetch; and after five minutes without a fetch its times to 100% read `idle`.
- It costs the status line nothing that could be measured. The exe is 91.648 bytes larger and a
  render takes as long as it did. The process that stays up for the key holds 11,0 MB, 3,6 MB of
  it private, and used 0,22 s of processor time in half an hour of Claude working; a refresh is a
  second process for half a second.
- `statusai --deck-face` prints the key as SVG.

**The repository**

- `src/Deck.cs` holds the plugin, websocket client included, and `streamdeck/` the plugin's
  folder as the app wants it. `RenderRows` takes a row's forecast from `Pace()`, which the key
  draws by as well.
- A press is a process of its own, `statusai --deck-press`, as a fetch is `statusai --refresh`.
  Starting Windows Terminal by its alias loads Windows' app model into the process that does it:
  63 handles and 12 libraries that never go, which is what the plugin was found holding a day
  after its first press. The plugin waits on that process with its other waits and asks Windows
  how it ended, since .NET's own way of asking costs the process that asks 13 handles for good. A
  press leaves the plugin with the handles it had. And the plugin finds the home folder in
  `%USERPROFILE%`, where a render asks the shell, which would load five of the shell's libraries
  and 35 handles into the process that stays up.
- The plugin hands on none of the handles the Stream Deck app starts it with, so a terminal
  opened by a press does not hold the app's log files open; and it draws its key again when the
  deck comes back after a lock or a sleep.
- Thirteen render cases for the key's face: 180 renders of 74 cases.
  [Test-Deck.ps1](tests/Test-Deck.ps1) runs the exe as a plugin against a stand-in for the Stream
  Deck app, offline but for one, in 18 cases: its websocket client through the protocol (long
  frames, fragments, ping and pong, a close from either side, a dropped connection), a deck that
  goes and comes back, a hundred refreshes and forty presses that leave the handle count where it
  was, a press with no Windows Terminal to start, and a plugin that is not offline and finds its
  home folder without the shell. `-Live` adds four: a control, a terminal started
  with nothing done for it, which has to stay behind; and three that press the key for real, with
  no terminal open, one open and one minimised, and hold the terminal to being in front each
  time. Their plugin is started through WMI, where it stands as the app's plugin does.
- [Deploy.ps1](scripts/Deploy.ps1) keeps an installed plugin's copy of `statusai.exe` in step with
  the status line's, moving the running copy aside to replace it, and `-Deck` installs the plugin.
  It ends the plugin's process for the app to start the new copy: the app's own ways of
  restarting a plugin are turned away outside its developer mode.
- The figure generator draws the key's two figures from the recorded faces, and the plugin's own
  pictures with them.
- [The design record](docs/design/stream-deck.md) has what the Stream Deck app can and cannot do,
  the four ways of building the key with their measurements, how the refresh rule was chosen, how
  a press gets its terminal to the front, and what the first day on the device showed.
- The key is not in the release zip yet: `Package.ps1` does not build the plugin's installer.

## 1.0.0 - 2026-09-24

The first release: a zip with `statusai.exe` and the configuration it is used with.
[install.md](docs/guide/install.md) puts it in place and fetches
[cship](https://github.com/stephenleo/cship) 1.8.0 beside it. It draws:

- Tokens for the whole agent tree (the main conversation and every sub-agent at any depth,
  workflows included) in two rows of nine: fresh prompt, cache writes and tool calls above; output,
  cache reads and all tokens below; each for the main thread, the sub-agents and the whole tree.
- Three usage limits, the five hours, the week and the week for one model, each with where it
  stands, when it resets, when 100% arrives at the pace of the last hour, and where it will stand
  at the reset. The time to 100% is red when it comes before the reset and forest green when the
  reset comes first.
- The account, and where the week went: `👤 you@example.com · Max 20 · CC 99% · Chat 0% ·
  Cowork 1%`.
- How long the session has run, the lines it changed, its cost per hour and its cost so far, in
  dollars and euros.
- Warnings that say why. A figure that could not be read shows `—` and a red `⚠` row names its
  source. When fetching the usage fails twice in a row, a red row says why (a timeout, an HTTP
  status, no token, offline) and how old the limit rows still shown are. An amber row names a limit
  it does not draw yet, and a red alarm appears if usage is ever billed beyond the plan.
- One usage fetch at a time for every open session, and none while the shared copy is under 50
  seconds old. Once fetching has failed twice in a row, the sessions retry at most once every 50
  seconds between them, and draw the saved rows and the warning in the meantime.

It fits the terminal's width, which Claude Code 2.1.153 and later pass to it, and every open session
draws at the width of its own terminal. `STATUSAI_WIDTH` sets a fixed width instead, and with
neither the width is 141.

The program is `statusai.exe`; the builds before this release were `cship-usage.exe`. What it
keeps took the new name as well: the registry key `HKCU\Software\StatusAI`, the token cache in
`%LOCALAPPDATA%\StatusAI\tokens`, and `STATUSAI_WIDTH`, which was `CSHIP_WIDTH`.

The [README](README.md) opens with the status line as a terminal shows it, with nothing added, then
an annotated version that names every part and opens the
[reading guide](docs/guide/reading-the-status-line.md) when clicked.

StatusAI is under the [MIT License](LICENSE), which the zip carries as `LICENSE.txt`. Beside
it, [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt) holds the licences of the .NET runtime,
which is compiled into `statusai.exe`, and of cship and starship, since `cship.toml` and
`starship.toml` are based on their configuration.

It needs Windows 10 or 11 on x64, and Claude Code in a terminal, signed in with a Claude account for
the limit rows; the VS Code extension's chat panel shows no status line. The terminal has to draw
emoji two columns wide, as Windows Terminal does, and be at least 121 columns wide for the token
grid. cship needs the Microsoft Visual C++ Redistributable, which most PCs already have and the
install block checks for.

Known limits: numbers are in Dutch notation (`1.234,56`); `statusai.exe` is x64 only; it and
cship are unsigned, so Windows 11's Smart App Control blocks them where it is on; there is no
installer or auto-update yet.

## 2026-09-24

**The status line**

- A row at 100% keeps its `⇢` segment but draws it grey, like a disabled control: the row is
  blocked until its reset, so where it is heading does not apply yet.
- Only the three known limits are drawn: the session, the week, and one model-scoped weekly limit.
  Any other the usage API sends is named in an amber notice instead, pending a review of the code.
- The account line shows how this week's usage splits across products, `CC 99% · Chat 0% ·
  Cowork 1%`, in whole entries or not at all.
- A red alarm, always the last row, when usage is being billed beyond the plan.
- `→` turns forest green when the row's own reset comes before 100% would: at this pace the
  window never runs out.

**The repository**

- The [install guide](docs/guide/install.md) says where the status line shows, what cship needs,
  what an unsigned program meets on Windows, and what to do when something is missing; its block
  checks that cship starts. The README's steps link it.
- The README and the guides say only what the code and the transcripts bear out: the sub-agents'
  28%, 78% and 98% are sourced in [accounting.md](docs/reference/accounting.md), and the one failure
  that raises no row, a usage fetch, is named.
- Every docs page opens with a way back to the README and the docs index.
- The [README](README.md) is a landing page: what the status line does, a tour of its parts with a
  figure each, and how to get it.
- Figures drawn from the render tests' expected output in the house style by
  [figures.gen.js](docs/assets/figures.gen.js), as PNGs that read the same in GitHub's light and
  dark themes; the [reading guide](docs/guide/reading-the-status-line.md) shows them part by part.
- A render case with every kind of `⚠` row at once, which pins their order: 146 renders of 45
  cases.
- [Package.ps1](scripts/Package.ps1) builds the release zip, render-tested, with its SHA-256 and
  its release notes, and the [install guide](docs/guide/install.md) downloads it and fetches cship
  beside it.
- The docs are grouped by reader: [guide/](docs/guide/) for using it, [reference/](docs/reference/)
  for how it works, [design/](docs/design/) for why, with the packaging plan and the rejected
  designs there. The README's technical content moved into them.
- Render tests: 143 renders of 44 cases compared byte for byte with the recorded output, offline,
  beside live sessions. See [development.md](docs/development.md#the-render-tests).
- [Deploy.ps1](scripts/Deploy.ps1) tests, backs up, copies with retries, verifies and rolls back.
- Every text file is LF; `.work/` is the scratch area.
- The docs' mockups show example.com addresses, and their stale claims are corrected.
- The [decisions page](docs/design/limits-decisions.html) records the limit-row questions of 23 and
  24 September: the rejected window-average forecast, and the decisions behind forest `→`, the
  breakdown, the alarm, the amber notice and the grey `⇢`.

## 2026-09-23

- The scoped row's projection sits right after its own bar instead of padded to the left
  column's width.
- A new session no longer opens with a red `⚠` row: before its first reply Claude Code reports
  the context as null, which is a zero, not a gap, and the transcript is only written at the first
  prompt.
- `CSHIP_OFFLINE` renders from fixture files without touching the registry cache, the usage lock
  or the network.
- Every source that fails is named in a red `⚠` row, and its figure reads `—` instead of a false
  `0`. When cship prints nothing, the rest of the status line is drawn on its own. In use since
  2026-08-19.
- Each row projects to its own reset, uncapped; a bar lights a cell as soon as its tenth begins, so
  101% is visibly an eleventh cell; a limit without a reset time reads `↻ —`.

## 2026-08-15

- Docs for the limit rows, the dev loop, and the weekly-wall design that was built and reverted.

## 2026-08-14

- The first version: tokens and tool calls for the whole agent tree (the main conversation and every
  sub-agent at any depth), checked against independent PowerShell implementations on an 86-agent
  session.
- The verification scripts take `-Sid` and find the transcript themselves.
