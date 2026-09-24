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
- **Warnings that say why**: a red `⚠` row names every source that failed, an amber one names a
  limit it does not draw yet, and a red alarm appears if usage is ever billed beyond the plan.
- **One usage fetch for every open session**, at most once every 50 seconds.

Known limits: numbers are in Dutch notation (`1.234,56`); the width is 141 columns unless
`CSHIP_WIDTH` says otherwise, and the token grid needs at least 121; the binary is x64 only and
unsigned; there is no installer or auto-update yet.

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

- The docs are grouped by reader: [guide/](docs/guide/) for using it, [reference/](docs/reference/)
  for how it works, [design/](docs/design/) for why, with the packaging plan and the rejected
  designs there. The README's technical content moved into them, and it is becoming a landing
  page. `17623f1` `7f9b88f` `23496ac` `a89974a`
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
