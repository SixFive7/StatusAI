# Layout

The rules the grid obeys, and the reasoning behind each. Most were arrived at by getting them wrong
first.

## Width budget

Terminal width comes from `TermWidth()` — `CSHIP_WIDTH` if set, otherwise a hardcoded **141**.
Claude Code spawns the status line detached, so the attached console reports a phantom 120×30 and OS
detection is unusable. 141 is a measured value for one machine and **must become a setting** before
anyone else runs this.

```
141  terminal
 -4  host padding
────
137  available
 -1  our indent
 -1  the │ rule
 -2  safety margin (same convention as Compose())
────
133  for nine columns and eight gutters
```

Gutters take whatever is left over, divided by eight, with a floor of 2. Since
`pad = floor((avail - 4 - Σcw) / 8)`, the row can never exceed the budget:

```
row = 2 + Σcw + 8·pad  ≤  2 + Σcw + (avail - 4 - Σcw)  =  avail - 2
```

Current rows measure 133.

## The metric rows solve for their own bar width

The 5h / 7d / scoped rows use a different mechanism from the token grid: everything except the two
projection bars is known, so `RenderRows` solves for the bar length that exactly fills the
terminal, and recomputes it every render.

```
fixedPart = 4 + 1 + 2 + 2·rowConst + wLeft + wRight + 2·(wNow + wReset + wTo100 + wProj)
capBar    = clamp((term - 2 - fixedPart) / 2, 10, 30)
```

`rowConst` is 25 — the glyphs and spaces in a row outside the label and the four numeric columns.
The `4 + 1 + 2` is host padding, our indent, and the column gap; the doubling is because
`Compose()` puts two metric rows side by side (5h with the account line, 7d with the scoped row).

**The consequence is the rule worth remembering: a metric row is laid out twice per line, so any
field added to it costs twice its width in bar budget.** Add a five-character column and the bars
lose ten characters between them. Widths are also maxima across rows, so one long value — a
`2d03h` reset, a three-digit projection — silently shortens every bar on the line. This is why a
design note for these rows is not finished until it states its character cost.

Column widths are computed per render as the max needed across rows, so nothing is padded wider
than the current values require, and the label widths are kept separate for the left column and
the right so `5h` / `7d` never inherit the width of a longer scoped label like `Fable`.

## Number format

**Four significant figures, scaled unit, always exactly six characters.** `0,202k` `9,444k` `240,7k`
`1,397M` `46,92M` `112,0G`.

A fixed *significant-figure* count cannot give a fixed width. Three of them occupy four characters at
`9,44` and `41,7` but only three at `241` — once the integer part fills all three digits the comma
has nowhere to sit. Padding with a space doesn't help either: it's invisible, and right-alignment was
already inserting one.

So the **mantissa is pinned at five characters** and the decimals float to fill it — three decimals
below 10, two below 100, one above. That constrains the glyph count rather than merely the column,
and it makes the column widths, and therefore the whole row, a property of the format instead of the
values.

nl-NL notation throughout: `1.234,56`. `InvariantGlobalization` is on, so no named culture exists at
runtime and every `nl-NL` lookup silently resolves to invariant — the formatting goes through
invariant first and swaps the two separators, keeping the ICU dependency out of the AOT binary.

Tool calls are plain counts with thousands separators, no unit.

## The grid

Nine columns, uniform cell grammar `[kind][scope] value`:

```
column width = 4 (two emoji) + 1 (mandatory space) + 6 (value) = 11
```

Icons align on a column's left edge, digits on its right, with the slack between them. The `+1`
guarantees at least one space between icons and value in every cell.

Scope holds vertically — 🪵 in columns 1, 4, 7; 🌿 in 2, 5, 8; 🌳 in 3, 6, 9 — reinforced by number
colour so a scope reads straight down without parsing a glyph.

| element | colour |
|---|---|
| main | `#6e738d` dim |
| sub | `#b4befe` lavender |
| total | `#7dcfff` bold cyan |
| the `│` rule | `#6e738d` |

## Alignment: why the rule exists

Every row must open with a **single-width** glyph. This is not cosmetic.

The model line opens with a Nerd Font glyph, the metric rows with `5h` / `7d` — all single-width, all
at character column 1. A row opening with a double-width emoji also sits at character column 1, but
its *ink* is inset within a two-column advance, so it renders about half a cell right of everything
else.

That offset is fractional and the character grid is integral, so **no indent value can ever fix it**.
Two attempts proved it empirically: indent 1 put the emoji visibly right of the reference glyphs,
indent 0 put it visibly left. Crossing from too-right to too-left with a one-column shift means the
target lies between two columns.

Leading each token row with `│` — single-width box-drawing — makes character column and screen
column the same number on every row, so the left edges coincide **by construction** rather than by
measurement. It also brackets the two token rows as one block, distinct from the metric rows below.

### Vetting a new glyph

`Vis()` counts every non-escape character as exactly one column, so a double-width glyph anywhere
in a metric row breaks `Compose()`'s two-column arithmetic silently — the right-hand column shifts
and nothing errors.

Every glyph currently drawn — `│` U+2502, `↻` U+21BB, `→` U+2192, `⇢` U+21E2, `●` U+25CF,
`○` U+25CB, `✗` U+2717 — is East-Asian-Ambiguous, which renders single-width in the terminals this
targets. **Check any candidate against that class before using it**; anything Wide or Fullwidth is
disqualified outright, and emoji are only safe in the token grid, where `iw[]` declares two columns
per glyph explicitly.

## Icon vocabulary

Six kind icons, each meaning exactly one thing and appearing in exactly one place:

| icon | counter |
|---|---|
| 🔺 | fresh prompt sent |
| 💾 | prompt written to cache |
| 🔧 | tool calls issued |
| 🔻 | output generated |
| 📖 | prompt served from cache |
| 🪙 | all token types summed |

Design constraints that survived several rounds of rejected alternatives:

**Direction is relative to the user, not the model.** 🔺 left, 🔻 came back. With the rows carrying
send/receive, an up-arrow on the sent row is the only reading that doesn't fight the layout.

**💾 and 📖 carry no arrow.** An earlier design encoded `[direction][scope][cache]` in three icons,
which put a down-arrow on both — correct, since cache writes and cache reads are both *input* tokens,
but it invites reading the arrow against the cache instead of against the conversation. Two reference
frames in one cell. The arrow was also redundant there: it only ever distinguished fresh input from
output, and never varied in the cache columns.

**Orientation must be the whole glyph, not a detail inside it.** 📥/📤 failed because direction lived
in a ~3-pixel arrow inside two otherwise identical trays — 95 % of the glyph was noise. Solid
triangles work because orientation *is* the glyph.

**No coloured up/down emoji pair has different hues**, so the triangle sets are orientation-only by
necessity. Every icon is default-emoji-presentation — no variation selectors — so width and colour
are guaranteed by the codepoint alone.

## Meta segment

Order: `⏱ duration` · `📝 +added -removed` · `💸 rate` · `💰 cost`, dollars and euros separated by a
bare `/` with no spaces. Measured at 130 of 137 columns with the largest realistic figures, and the
euro rate was the last field that could grow it.

The euro figure is a conversion of Anthropic's **client-side list-price estimate**, and on a
subscription plan that isn't what anyone is billed. It is a relative-effort gauge, not an invoice.
