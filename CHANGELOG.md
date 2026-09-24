# Changelog

What changed, newest first. Releases are headed by their version; the entries before the first
release are by date.

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
  seconds old.

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
