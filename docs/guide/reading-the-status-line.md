# Reading the status line

StatusAI is Claude Code's status line: cship's model line with a meta segment appended, two rows of
tokens for the whole agent tree, and a row for each usage limit. This page reads them top to
bottom.

```
  Opus 5 (1M context) ⚡ max  ●●●●●●●●●●○○○○○○○○○○○○○○○○○○○○ 34%   ⏱ 44m   📝 +220 -0   💸 $12,71/€11,02/h   💰 $9,32/€8,08
 │🔺🪵 0,238k    🔺🌿 9,444k    🔺🌳 9,682k    💾🪵 1,447M    💾🌿 240,7k    💾🌳 1,687M    🔧🪵    101    🔧🌿    129    🔧🌳    230
 │🔻🪵 286,9k    🔻🌿 1,549k    🔻🌳 288,5k    📖🪵 58,60M    📖🌿 4,830M    📖🌳 63,43M    🪙🪵 60,33M    🪙🌿 5,081M    🪙🌳 65,42M
 5h ●○○○○○○○○○  9% ↻ 4h35m → 7h35m  ⇢ ●●●●●●●○○○        64%  👤 you@example.com · Max 20 · CC 99% · Chat 0% · Cowork 1%
 7d ●●●●●●○○○○ 54% ↻ 1d02h → 11h30m ⇢ ●●●●●●●●●●✗✗✗✗✗✗ 159%  Fable ●●○○○○○○○○ 17% ↻ 1d02h → never ⇢ ●●○○○○○○○○ 17%
```

The sample is drawn by the current binary from the render tests' `showcase` fixture: a
mid-session payload, a main thread with two sub-agents, and an example set of limits. In a
terminal every figure has its colour, and several of the colours mean something; they are described
below, row by row.

## Why the whole tree

Claude Code's status line payload has no cumulative token figure at all, and everything it *does*
expose covers the main conversation only. Sub-agent turns are not in the main transcript — every
child, at any depth, writes its own file under `<session>/subagents/`.

Measured across three real sessions, sub-agents were **28 %, 78 % and 98 %** of total consumption.
On the session StatusAI was built in, the main transcript was **8 %** of the tokens spent.

Any status line that reads only `transcript_path` is therefore showing you a rounding error.
StatusAI walks the whole tree. How it counts, and why the obvious ways double-count, is in
[accounting.md](../reference/accounting.md).

## The model line

The model line is cship's: the model, the effort level and the context bar. StatusAI appends the
meta segment to it:

| | shows |
|---|---|
| `⏱ 44m` | how long the session has run |
| `📝 +220 -0` | lines added and removed, once there are any |
| `💸 $12,71/€11,02/h` | cost per hour so far, once the session is 72 seconds old |
| `💰 $9,32/€8,08` | cost so far |

The cost is Anthropic's **client-side list-price estimate**, converted to euros at the European
Central Bank's daily reference rate. On a subscription plan that is not what anyone is billed: it
is a relative-effort gauge, not an invoice. A figure the payload did not carry reads `—`, never `0`.

With starship installed, a prompt line comes before the model line — the directory, git, RAM, the
GPU and the time. The sample leaves it out; [install.md](install.md) says what it needs.

## The token grid

**Two rows, split by direction of travel.**

| | columns 1–3 | columns 4–6 | columns 7–9 |
|---|---|---|---|
| **top — what left** | 🔺 fresh prompt | 💾 written to cache | 🔧 tool calls |
| **bottom — what came back** | 🔻 output generated | 📖 served from cache | 🪙 all token types summed |

Columns 7–9 sit outside the send/receive metaphor — they're the summary group, a count above and a
total below.

**Three columns per group, one per scope**, holding vertically all the way across:

- 🪵 **trunk** — this main conversation (columns 1, 4, 7)
- 🌿 **branch** — everything the sub-agents did (columns 2, 5, 8)
- 🌳 **tree** — both together (columns 3, 6, 9)

Reinforced by colour — main dim, sub lavender, total bold cyan — so a scope reads straight down
without parsing a glyph.

**Units.** `k` = ×1.000, `M` = ×1.000.000, `G` = ×1.000.000.000 tokens. Four significant figures,
always exactly six characters. Tool calls carry no unit.

Arrows are relative to **you**, not the model: 🔺 left, 🔻 came back. 💾 and 📖 deliberately carry
no arrow — a direction on them invites reading it against the cache rather than against the
conversation, and the two frames disagree.

### Reading the sample

- `🔺🪵 0,238k` against `📖🪵 58,60M` — the main thread sent 238 genuinely new prompt tokens while
  58,6 million came back out of cache. That ratio is caching working: the same context is re-read
  every turn at a tenth of the rate.
- `🪙🌿 5,081M` against `🪙🪵 60,33M` — sub-agents versus main thread, the number nothing else on
  the bar shows.
- `🔧🌿 129` against `🔧🪵 101` — the agents made more tool calls than the main thread despite a
  twelfth of the tokens. That's what delegation should look like.

## The limit rows

Up to three rows, one per usage limit: `5h` is the five-hour session, `7d` the week, and the third,
named after its model — `Fable` in the sample — is the weekly limit scoped to one model. Each reads
left to right:

```
7d ●●●●●●○○○○ 54% ↻ 1d02h → 11h30m ⇢ ●●●●●●●●●●✗✗✗✗✗✗ 159%
   now        now  resets  100% in   at the reset, at this pace
```

- **Now.** Ten cells, one per tenth begun: cyan, amber from the eighth, red on the tenth. The
  percentage turns amber at 70% and red at 90%, and the label takes the colour the server gives
  the limit's severity.
- **`↻`** the time until this limit resets, or `—` when the server did not say.
- **`→`** how soon 100% arrives at the pace of the last 60 minutes, and its colour says whether
  that matters: **red** when 100% comes before the reset — the window runs out — and
  **forest green** when the reset comes first, so at this pace it never does; grey when there is
  no reset time to compare with. It reads `early` until there is enough history for a trend,
  `maxed` at 100% and `never` when the limit is not moving.
- **`⇢`** where the limit stands at its reset if the pace holds, capped at 300%. Past 100% the
  bar grows `✗` cells in red. A row already at 100% draws this whole segment grey, like a
  disabled control: it is blocked until the reset, so where it is heading does not apply yet.

When the line is wide enough the rows sit in two columns — `5h` beside the account, `7d` beside the
scoped row — and otherwise they stack. How the projection is built, and which of the server's
meters are drawn, is in [limits.md](../reference/limits.md).

## The account line

```
👤 you@example.com · Max 20 · CC 99% · Chat 0% · Cowork 1%
```

The signed-in account and its plan, then how this week's usage splits across products: Claude Code,
chats, Cowork, and any other product while it is above 0%. The split is shown in whole entries or
not at all — without its 0% entries first, then none — so it never wraps. `👤 not signed in` means
Claude Code is not signed in to a Claude account, with an API key for instance; the limit rows need
one.

## When a row says ⚠

A figure that could not be read never looks like a real zero. Every source that failed is named in
a **red** `⚠` row at the foot of the block — a payload that cannot be read, a missing cost or
context figure, a transcript that should exist and does not, a usage fetch that is suspended, cship
printing nothing — and the figure itself reads `—`. A brand-new session, before its first reply,
is not a failure and says nothing.

Two more rows can appear there, and neither is a failure:

- **An amber notice** when the usage API sends a limit this status line does not draw yet: it is
  named, pending a review of the code, rather than drawn on trust.
- **A red on-credit alarm**, always last, when usage is being billed beyond the plan:
  `⚠ on credit — $12,40 spent beyond the plan this period`.

Every reason and when it appears is in [layout.md](../reference/layout.md#the--row).
