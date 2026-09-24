# Changelog

What changed, newest first, with the commits it came from. Releases are headed by their version;
the entries before the first release are by date.

## 0.1.0 — 2026-09-24

The first release: a zip with `cship-usage.exe` and the configuration it is used with.
[install.md](docs/guide/install.md) puts it in place and fetches
[cship](https://github.com/stephenleo/cship) 1.8.0 beside it. It draws:

- **Tokens for the whole agent tree** — the main conversation and every sub-agent at any depth,
  workflows included — in two rows of nine: fresh prompt, cache writes and tool calls above; output,
  cache reads and all tokens below; each for the main thread, the sub-agents and the whole tree.
- **Three usage limits** — the five hours, the week, and the week for one model — each with where
  it stands, when it resets, when 100% arrives at the pace of the last hour, and where it will
  stand at the reset. The time to 100% is red when it comes before the reset and forest green when
  the reset comes first.
- **The account, and where the week went**: `👤 you@example.com · Max 20 · CC 99% · Chat 0% ·
  Cowork 1%`.
- **Time and money**: how long the session has run, the lines it changed, its cost per hour and its
  cost so far, in dollars and euros.
- **Warnings that say why**: a figure that could not be read shows `—` and a red `⚠` row names its
  source, an amber row names a limit it does not draw yet, and a red alarm appears if usage is ever
  billed beyond the plan.
- **One usage fetch at a time for every open session**, and none while the shared copy is under 50
  seconds old.

It needs Windows 10 or 11 on x64, Claude Code in a terminal — the VS Code extension's chat panel
shows no status line — and, for cship, the Microsoft Visual C++ Redistributable, which most PCs
already have and the install block checks for.

Known limits: numbers are in Dutch notation (`1.234,56`); the width is 141 columns unless
`CSHIP_WIDTH` says otherwise, and the token grid needs at least 121; a usage fetch that fails is not
reported, and the limit rows keep the last good one; the binary is x64 only and unsigned, so
Windows 11's Smart App Control blocks it where it is on; there is no installer or auto-update yet.

## 2026-09-24

**The status line**

- A row at 100% keeps its `⇢` segment but draws it grey, like a disabled control: the row is
  blocked until its reset, so where it is heading does not apply yet. `878a8e4`
- Only the three known limits are drawn — the session, the week, and one model-scoped weekly
  limit. Any other the usage API sends is named in an amber notice instead, pending a review of
  the code. `ccf37df`
- The account line shows how this week's usage splits across products, `CC 99% · Chat 0% ·
  Cowork 1%`, in whole entries or not at all. `a84bd3a`
- A red alarm, always the last row, when usage is being billed beyond the plan. `a84bd3a`
- `→` turns forest green when the row's own reset comes before 100% would: at this pace the
  window never runs out. `3a801e1`

**The repository**

- The [install guide](docs/guide/install.md) says where the status line shows, what cship needs,
  what an unsigned program meets on Windows, and what to do when something is missing; its block
  checks that cship starts. The README's steps link it. `5dc4ac7`
- The README and the guides say only what the code and the transcripts bear out: the sub-agents'
  28%, 78% and 98% are sourced in [accounting.md](docs/reference/accounting.md), a claim about the
  build session that could not be checked is gone, and the one failure that raises no row, a usage
  fetch, is named. `fba03ea`
- Every docs page opens with a way back to the README and the docs index. `38df644`
- The [README](README.md) is a landing page: what the status line does, a tour of its parts with a
  figure each, and how to get it. `562812e`
- Figures drawn from the render tests' expected output in the house style by
  [figures.gen.js](docs/assets/figures.gen.js), as PNGs that read the same in GitHub's light and
  dark themes; the [reading guide](docs/guide/reading-the-status-line.md) shows them part by part.
  `2c83e3b` `55266b3`
- A render case with every kind of `⚠` row at once, which pins their order: 146 renders of 45
  cases. `706daf2`
- [Package.ps1](scripts/Package.ps1) builds the release zip, render-tested, with its SHA-256 and
  its release notes, and the [install guide](docs/guide/install.md) downloads it and fetches cship
  beside it. `8cfb749`
- The docs are grouped by reader: [guide/](docs/guide/) for using it, [reference/](docs/reference/)
  for how it works, [design/](docs/design/) for why, with the packaging plan and the rejected
  designs there. The README's technical content moved into them. `17623f1` `7f9b88f` `23496ac`
  `a89974a`
- Render tests: 143 renders of 44 cases compared byte for byte with the recorded output, offline,
  beside live sessions — see [development.md](docs/development.md#the-render-tests). `068ef3b`
- [Deploy.ps1](scripts/Deploy.ps1) tests, backs up, copies with retries, verifies and rolls back.
  `71ae18a`
- The scripts and reports behind the limit-row commits are kept in
  [docs/design/evidence/](docs/design/evidence/). `6defb02`
- Every text file is LF; `.work/` is the scratch area. `9af4082`
- The docs' mockups show example.com addresses, and their stale claims are corrected. `fe5a8f3`
  `71f866c`
- The [decisions page](docs/design/limits-decisions.html) records the limit-row questions of 23–24
  September: the rejected window-average forecast, and the decisions behind forest `→`, the
  breakdown, the alarm, the amber notice and the grey `⇢`. `b63d40d` `5b9dd49`

## 2026-09-23

- The scoped row's projection sits right after its own bar instead of padded to the left
  column's width. `bd399ae`
- A new session no longer opens with a red `⚠` row: before its first reply Claude Code reports
  the context as null, which is a zero, not a gap, and the transcript is only written at the first
  prompt. `bd399ae`
- `CSHIP_OFFLINE` renders from fixture files without touching the registry cache, the usage lock
  or the network. `bd399ae`
- Every source that fails is named in a red `⚠` row, and its figure reads `—` instead of a false
  `0`. When cship prints nothing, the rest of the status line is drawn on its own. `62f06a7`, in
  use since 2026-08-19
- Each row projects to its own reset, uncapped; a bar lights a cell as soon as its tenth begins, so
  101% is visibly an eleventh cell; a limit without a reset time reads `↻ —`. `62f06a7`

## 2026-08-15

- Docs for the limit rows, the dev loop, and the weekly-wall design that was built and reverted.
  `59816fb`

## 2026-08-14

- The first commit: tokens and tool calls for the whole agent tree — the main conversation and
  every sub-agent at any depth — checked against independent PowerShell implementations on an
  86-agent session. `b4d7d99`
- The verification scripts take `-Sid` and find the transcript themselves. `f1528d7`
