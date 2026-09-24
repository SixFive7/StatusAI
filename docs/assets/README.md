# Figures

Every image here is generated. [figures.gen.js](figures.gen.js) draws them from the render tests'
expected output — the exact bytes the binary draws for each fixture, in
[tests/expected](../../tests/expected) — in the [house style](../design/house-style/README.md), and
Microsoft Edge, headless, turns each into a PNG at twice its size. Regenerate them all with

```bash
node docs/assets/figures.gen.js
```

after a change of output is recorded with `Test-Renders.ps1 -Update`; `--check` draws them into
`.work/figures/check` and compares them with these, byte for byte. It needs Node, Edge and
JetBrainsMono Nerd Font, and refuses to run on a colour outside the palette, a glyph whose width it
does not know, or a callout that does not land.

A render is named `<case>.w<width>`: the case in [tests/cases.json](../../tests/cases.json), drawn
at that many columns. Sizes are CSS pixels; each PNG has twice as many.

| figure | shows | drawn from | size |
|---|---|---|---|
| [hero.png](hero.png) | The whole status line at the default 141 columns: the model line with the meta segment, the token grid, the limit rows with the account and the breakdown. | `showcase.w141` | 1146 × 288 |
| [limits.png](limits.png) | The 5h and 7d rows of the `shot` fixture, every segment named: 7d at 89% runs out in 4h35m, before its reset, so the time is red; 5h resets before it would, so its time is forest green. | `shot.w141` | 586 × 197 |
| [states.png](states.png) | One limit row per state of →: red, forest, never, early, and maxed with its ⇢ segment greyed. | `shot.w141`, `rows-early.w141`, `maxed-5h.w141` | 650 × 333 |
| [first-run.png](first-run.png) | A session seconds old, before its first reply, with no history yet: the model line, then the limit rows, every one reading → early; no token rows and no ⚠ row. | `rows-early.w141` | 930 × 177 |
| [stacked.png](stacked.png) | At 100 columns the limit rows stack, with the account on a line of its own. | `showcase.w100` | 554 × 174 |
| [breakdown.png](breakdown.png) | The account line: the signed-in address, the plan, and how this week’s usage split across products. | `showcase.w141` | 546 × 129 |
| [breakdown-fit.png](breakdown-fit.png) | The breakdown in whole entries or none: every entry, then without its 0% entries, then none, as the address grows. | `showcase.w141`, `long-email-35.w141`, `long-email-46.w141` | 682 × 150 |
| [model.png](model.png) | cship’s model line: the model, the effort level and a 30-cell context bar. | `showcase.w141` | 610 × 129 |
| [meta.png](meta.png) | The meta segment: session time, lines added and removed, cost per hour and cost so far, in dollars and euros. | `showcase.w141` | 530 × 129 |
| [context-cost.png](context-cost.png) | The model line from its context bar on: how full the context is, then the meta segment — session time, lines changed, cost per hour and cost so far. | `showcase.w141` | 826 × 129 |
| [tokens.png](tokens.png) | The token grid: three groups of three columns, each group one kind of count, each column one scope. | `showcase.w120` | 1018 × 180 |
| [warnings.png](warnings.png) | The three kinds of ⚠ row, in the order they come: a source that failed, in red; a limit this status line does not draw yet, in amber; usage billed beyond the plan, in red and always last. | `every-warning.w120` | 754 × 255 |
| [sessions.png](sessions.png) | An illustration of three open sessions over five minutes: each render either fetches the usage (only when the shared copy is 50 seconds old or more) or draws the shared copy. | the rule in `GetUsage()`, played out over invented render times | 794 × 283 |
| [download.png](download.png) | A button: Download for Windows. | the palette: cyan on the terminal ground | 300 × 71 |
| [decisions-week.png](decisions-week.png) | The week chart from the decisions page: the 7d meter and three ways of forecasting it, minute by minute, from Saturday 05:00 to Wednesday 20:03. | [limits-decisions.html](../design/limits-decisions.html) | 998 × 467 |

The generator rewrites this page on every full run: edit the generator, not the page.
