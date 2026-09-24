<sub>[StatusAI](../README.md) / Documentation</sub>

# Documentation

Everything about StatusAI beyond the [README](../README.md): how to read it and install it, how it
works, why it looks the way it does, and how to change it. New here? Start with
[reading the status line](guide/reading-the-status-line.md), then [install](guide/install.md).

## Guide

| page | for |
|---|---|
| [guide/reading-the-status-line.md](guide/reading-the-status-line.md) | what every row, glyph and colour on the status line means |
| [guide/install.md](guide/install.md) | downloading or building it, putting it in place, what to do when something is missing, and taking it out again |

## Reference

How it works now.

| page | covers |
|---|---|
| [reference/architecture.md](reference/architecture.md) | components, the render chain, data sources, update cadence, state, portability |
| [reference/accounting.md](reference/accounting.md) | how tokens and tool calls are counted, the traps, verification |
| [reference/layout.md](reference/layout.md) | the grid, number formatting, alignment, width budgets, the palette, the ⚠ rows |
| [reference/limits.md](reference/limits.md) | the OAuth usage endpoint, the three meters drawn and the flag for any other, the product breakdown, the on-credit alarm, the projection |

## Design

Why it is the way it is, and what was tried and rejected.

| page | covers |
|---|---|
| [design/limits-decisions.html](design/limits-decisions.html) | the record of the limit-row decisions of 23 and 24 September 2026, every option drawn cell for cell; open it in a browser, since GitHub shows HTML as source |
| [design/rejected-designs.md](design/rejected-designs.md) | the limit-row designs that were rejected, and why |
| [design/house-style/](design/house-style/) | the house style for visual documentation. **Read its README first: the example's subject matter is wrong on purpose.** |
| [design/packaging-plan.md](design/packaging-plan.md) | packaging, Velopack auto-update, the install surface, the landmines |

## Development

| page | covers |
|---|---|
| [development.md](development.md) | the repository, building, the render tests, deploying, releasing, testing on a live machine and the traps that waste an hour there, and how the docs are kept |

The figures in [assets/](assets/) are drawn from the render tests' output by a generator; its
[README](assets/README.md) lists them and gives the command that regenerates them. What changed, and
when, is in the [changelog](../CHANGELOG.md).
