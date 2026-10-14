# Changelog

What changed, newest first. Releases are headed by their version; what is not in a release yet is
under *Unreleased*, and the entries before the first release are by date.

## 1.1.0 - 2026-10-14

The second release, a zip with `statusai.exe` and the configuration it is used with, as the first
was. The status line follows a switch of account at once and never draws the previous account's
limits as the new one's, waits out a 429 for as long as the usage endpoint asks, and, where it has
no limit rows to show, says why at the first failed fetch. The same `statusai.exe` is also the
plugin behind a key on an Elgato Stream Deck, which for now is installed from a build.

**Switching accounts**

- The usage every session shares is tied to the token it was fetched with. A good fetch stores the
  first 8 hex digits of the token's SHA-256 beside the account, never the token. A render that
  finds another token in `.credentials.json` fetches at once, still one session at a time, where it
  used to draw a copy up to 50 seconds old. See
  [when the token changes](docs/reference/limits.md#when-the-token-changes).
- The previous account's figures are never drawn as the new one's. Until the new account's first
  good fetch, normally made by the render that sees the switch, there are no limit rows, and if
  that fetch fails its `⚠` row comes at once: `no limit rows for the new account yet`. A token
  Claude Code renews for the same account keeps the rows as they were while its fetch runs.
- A response that a switch overtakes, whose token is no longer the one in `.credentials.json` when
  it arrives, is dropped, and the next render fetches with the new token.
- A switch clears the reset times along with the history the forecast is drawn from, and a change
  of the model behind the scoped weekly row clears that row's. They were kept, so a row that had no
  reset time just then threw its new history away a second time once it got one, and read
  `→ early` again.
- At a switch made with claude-swap, the figure claude-swap fetched for the new account, if it is
  from after the switch and under a minute old, is drawn in place of a fetch of StatusAI's own, so
  that the switch sets off one request fewer. The next regular fetch adds what that figure does not
  carry: the severity, the breakdown, the on-credit alarm and the meters notice. claude-swap is
  optional and never required: without it, or where its figure cannot be used, StatusAI fetches as
  before. See
  [claude-swap's figure at a switch](docs/reference/limits.md#claude-swaps-figure-at-a-switch).
- `.credentials.json`, `~/.claude.json` and the rest of the files another program may replace are
  read with the sharing that lets it replace them meanwhile. `.credentials.json` was read in a way
  that let others only read it, so for the milliseconds StatusAI had it open, Windows refused
  claude-swap's switch and Claude Code's renewal of the token.
- A `~/.claude.json` that does not parse is read once more, 25 ms later. On Windows claude-swap
  rewrites that file in place, so a read during a switch could find it cut short
  ([claude-swap#461](https://github.com/realiti4/claude-swap/issues/461)), and the account line
  then said `not signed in` for a render.

**A 429 that says how long**

- A 429 now holds the fetch off for as long as its `Retry-After` says: no render tries the token
  that drew it before that time. On 13 October a 429 held every caller off for 300 seconds after a
  switch, where StatusAI ignored the header and tried again a minute later. The hold is an hour at
  most, where Claude Code caps it as well, and a 429 without the header is retried as before. See
  [a 429 that says how long](docs/reference/limits.md#a-429-that-says-how-long).
- The `⚠` row comes at the first such 429 and names when it lifts:
  `⚠ usage — HTTP 429 from api.anthropic.com until 12:49:50; the limit rows are 5m old`. That is 15
  columns more than the same row without it, 95 at the longest; where that does not fit, the
  reason is shortened with `…`, like any other.

**Smaller changes**

- Before any fetch has worked, on a first run or with the registry emptied, the first failed fetch
  raises the `⚠` row at once, `⚠ usage — timed out after 3 s; no limit rows yet`, where it waited
  for the second. With nobody signed in the status line stays quiet, as before.
- The usage request names the installed Claude Code's version, `claude-code/2.1.296` say, where it
  always said `claude-code/2.1.90`: the version the status-line payload carries, or else the newest
  that the folders of the VS Code extension or of the native installer are named for, and `2.1.90`
  only where there is neither. Nothing is started to find it. `statusai --user-agent` prints what
  the next request sends. See [the endpoint](docs/reference/limits.md#the-endpoint).
- A usage response without `limits[]` is drawn from the older `five_hour` and `seven_day` objects
  with their reset times as well, where both rows said `↻ —` and had no projection. No response
  captured has lacked `limits[]`. See [response shape](docs/reference/limits.md#response-shape).

**The Stream Deck key, from a build**

- `statusai.exe` is also the plugin behind a key on an Elgato Stream Deck. The key draws the 5h and
  7d rows with the status line's own cells and colours, their countdowns to the reset and their
  times to 100%, in Bahnschrift, the DIN typeface Windows comes with. A short press opens a new
  Windows Terminal tab in the window used last, and a hold of half a second a new window, either of
  them in front of whatever was there. Both open the terminal's default profile, so the key opens
  Claude Code where that profile runs it. See [the Stream Deck key](docs/guide/stream-deck.md).
- It keeps the shared usage up to date while Claude works where no status line runs, in the VS
  Code extension for instance: once every 62 seconds while a session writes its transcript, and not
  while Claude is idle, while the key is not visible, or while a terminal's status line is fetching
  already. After a switch of account it asks once for the new account's figures, Claude working or
  not, and while a 429 holds the token off it asks nothing. It fetches through
  `statusai --refresh`, a render's own fetch with nothing drawn, so it shares the 50 seconds and
  the one fetch at a time with the status line, and the two never add up.
- When fetching fails, the key greys both meters and says why and how old they are, as the status
  line's `⚠` row does, cut to fit. A 429 that says how long reads `⚠ 429 until 12:50`, and after a
  switch the key says `new account` until the new account's figures come.
- The process that stays up for the key holds 11,0 MB of memory, 3,6 MB of it private, and a
  refresh is a second process for half a second.
- The zip does not carry the key yet: `Package.ps1` does not build the plugin's installer. It is
  installed from a build with `Deploy.ps1 -Deck`, and needs the Stream Deck app 6.5 or later and
  Windows Terminal. From then on `Deploy.ps1` keeps the plugin's copy of `statusai.exe` in step
  with the status line's, and warns when the PC has no Bahnschrift, without which the app draws the
  key in Tahoma and the two times on a line run into each other. See
  [install](docs/guide/stream-deck.md#install).

To update from 1.0.0, run the [install block](docs/guide/install.md#download) again in the new
zip's folder, with Claude Code closed. It leaves the `cship.toml` in place as it is, and the one in
the zip has not changed.

It needs Windows 10 or 11 on x64, and Claude Code in a terminal, signed in with a Claude account for
the limit rows; the VS Code extension's chat panel shows no status line. The terminal has to draw
emoji two columns wide, as Windows Terminal does, and be at least 121 columns wide for the token
grid. cship needs the Microsoft Visual C++ Redistributable, which most PCs already have and the
install block checks for.

Known limits: numbers are in Dutch notation (`1.234,56`); `statusai.exe` is x64 only; it and
cship are unsigned, so Windows 11's Smart App Control blocks them where it is on; there is no
installer or auto-update yet.

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
