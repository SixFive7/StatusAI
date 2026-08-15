# StatusAI

A Claude Code status line that shows **what the whole agent tree actually costs** — not just the
main conversation.

```
  Opus 5 (1M context) ⚡ max  ●●●●●●●●●●○○○○○○○○○○○○○○○○○○○○ 34%   ⏱ 44m   📝 +220 -0   💸 $12,71/€11,02/h   💰 $9,32/€8,08
 │🔺🪵 0,238k    🔺🌿 9,444k    🔺🌳 9,682k    💾🪵 1,447M    💾🌿 240,7k    💾🌳 1,687M    🔧🪵    101    🔧🌿    129    🔧🌳    230
 │🔻🪵 286,9k    🔻🌿 1,549k    🔻🌳 288,5k    📖🪵 58,60M    📖🌿 4,830M    📖🌳 63,43M    🪙🪵 60,33M    🪙🌿 5,081M    🪙🌳 65,42M
 5h ●○○○○○○○○○  9% ↻ 4h35m → 4h33m  ⇢ ●●●●●●●●●● 101%  👤 you@example.com · Max 20
 7d ●●●●●○○○○○ 54% ↻ 1d02h → 11h30m ⇢ ●●●●●●●●●○  86%  Fable ●●○○○○○○○○ 17% ↻ 1d02h → never  ⇢ ●●○○○○○○○○  17%
```

## Why it exists

Claude Code's status line payload has no cumulative token figure at all, and everything it *does*
expose covers the main conversation only. Sub-agent turns are not in the main transcript — every
child, at any depth, writes its own file under `<session>/subagents/`.

Measured across three real sessions, sub-agents were **28 %, 78 % and 98 %** of total consumption.
On the session that produced the sample above, the main transcript was **8 %** of the tokens spent.

Any status line that reads only `transcript_path` is therefore showing you a rounding error.
StatusAI walks the whole tree.

## What you're looking at

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

## Reading the sample

- `🔺🪵 0,238k` against `📖🪵 58,60M` — the main thread sent 238 genuinely new prompt tokens while
  58,6 million came back out of cache. That ratio is caching working: the same context is re-read
  every turn at a tenth of the rate.
- `🪙🌿 5,081M` against `🪙🪵 60,33M` — sub-agents versus main thread, the number nothing else on
  the bar shows.
- `🔧🌿 129` against `🔧🪵 101` — the agents made more tool calls than the main thread despite a
  twelfth of the tokens. That's what delegation should look like.

## Status

Working, installed, and verified — but currently a **single-machine setup**, not a product.
See [PLAN.md](PLAN.md) for the packaging and distribution plan.

Two things must change before anyone else runs it:

- `Nl()` forces nl-NL number formatting unconditionally
- `TermWidth()` returns a hardcoded 141 columns

`src/Program.cs` is byte-identical to the source behind the deployed binary. One change has been
attempted since the initial commit — clamping the 5h projection to the weekly reset — and it was
reverted in full because the premise was false; [docs/limits.md](docs/limits.md) records why so it
is not re-derived. All work since the accounting was written has been presentation.

## Layout

```
src/       Program.cs and the csproj — the whole implementation
config/    cship.toml and starship.toml as currently deployed
scripts/   independent PowerShell implementations used to verify the accounting
test/      a captured status-line stdin payload for offline rendering
docs/      architecture, accounting rules, layout rules
```

## Documentation

| doc | covers |
|---|---|
| [docs/architecture.md](docs/architecture.md) | components, the render chain, data sources, portability |
| [docs/accounting.md](docs/accounting.md) | how tokens and tool calls are counted, the traps, verification |
| [docs/layout.md](docs/layout.md) | the grid, number formatting, alignment, width budgets |
| [docs/limits.md](docs/limits.md) | the OAuth usage endpoint, the limit rows, the projection, one rejected design |
| [docs/development.md](docs/development.md) | build, deploy and test on a live machine |
| [docs/templates/](docs/templates/) | house style for visual documentation |
| [PLAN.md](PLAN.md) | packaging, Velopack auto-update, install surface, landmines |

> **Agents working in this repository:** `docs/templates/weekly-wall.html` documents a feature that
> was **reverted because its premise was false**. It is kept as a style reference only. Read
> [docs/templates/README.md](docs/templates/README.md) before opening it, and never implement
> anything it describes.

## Verification

The accounting is checked against independent PowerShell implementations that share no code with
the binary. On a frozen 86-agent, depth-6 session:

| | main | sub | total |
|---|---|---|---|
| tokens | 38.727.133 | 15.222.671 | **53.949.804** |
| tool calls | 153 | 385 | **538** |

```powershell
./scripts/Split-MainVsTree.ps1 -Sid <session-id>    # tokens
./scripts/Verify-Tools.ps1     -Sid <session-id>    # tool calls
./scripts/Decode-TokCache.ps1  -Sid <session-id>    # what the binary cached
```

Run these after any Claude Code upgrade. The transcript layout is undocumented, and if it changes
the sub-agent half fails **silently and downward** rather than erroring.

## Credits

Renders on top of [cship](https://github.com/stephenleo/cship) (Apache-2.0) and, optionally,
[starship](https://starship.rs) (ISC).
