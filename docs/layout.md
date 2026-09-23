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
fixedPart = 4 + 1 + 2 + 2·rowConst + wLeft + wRight + 2·(wNow + wReset + wTo100 + wProj + wNowBar)
capBar    = clamp((term - 2 - fixedPart) / 2, 10, 30)
```

`rowConst` is 15 — the glyphs and spaces in a row outside the label, the four numeric columns and
the two bars. It was 25 and carried the now-bar's ten cells inside it; the now-bar is now the
explicit `wNowBar` term, computed as a max across rows exactly as `wBar` is, so the constant drops
by ten and the total is unchanged. Both values are one higher than the measured truth (14, was 24),
which underfills the line by two columns and can never overflow it.

`wNowBar` is always 10 in practice: `now` is clamped to 0..100 and the fill is `ceil(pct/10)`, so
ten cells is the ceiling, and `capNowBar` makes that a guarantee rather than an accident. It is a
term rather than a constant so that an eleventh cell would cost bar budget instead of silently
shifting everything to its right on one row — which is what `Compose()`'s two-column arithmetic
cannot survive.

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
`○` U+25CB, `✗` U+2717, `—` U+2014, `…` U+2026 — is East-Asian-Ambiguous, which renders
single-width in the terminals this targets. **Check any candidate against that class before using
it**; anything Wide or Fullwidth is disqualified outright, and emoji are only safe in the token
grid, where `iw[]` declares two columns per glyph explicitly.

`—` is the one sentinel for *this source reported nothing at all*, as distinct from a source that
reported zero: the `↻` column when the payload carried no `resets_at`, `⏱` / `💰` when the payload
carried no `cost` block, and the marker opening the standalone render below, where the source that
reported nothing is cship itself. It is never wider than the value it replaces, so no column it
appears in can grow. `…` only ever appears inside a truncated `⚠` reason.

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

With no host line to sit on, the segment becomes the first row of the block instead — see
[when there is no host line](#when-there-is-no-host-line).

## The ⚠ row

Every source that failed states why, in red, at the foot of the block: one row while the reasons fit
on it joined by `·`, one row each once they do not, because a wrapped status line costs the same
height as a split one and reads far worse. A source that honestly reported *zero* says nothing —
the row exists so that a blank figure is never indistinguishable from a real one.

What can appear there: an unreadable payload; a missing `cost` block or either of its two figures; a
missing `context_window.used_percentage`, since cship draws its context bar from that field and an
empty bar is byte-identical to a genuinely empty context; a suspended usage fetch; any of the four
ways the token walk can fail; and cship producing no output at all.

**One exception, and only one.** A transcript file that does not exist yet is not reported while
`total_duration_ms` is at or below 30 s. That is not a new threshold — `Hm()` rounds to whole
minutes, so it is the same boundary as the `⏱ 0m` the meta segment is showing at the time. The
transcript may not exist yet on the opening renders of a brand-new session, so a session seconds old
can be in that state with nothing wrong — and a red row on the first frame of every session is how a
warning row stops being read at all. Past 30 s the file should be there, and its absence is stated
as loudly as ever.

The other three token failures — no `transcript_path`, an unusable one, an exception during the walk
— are not explained by a young session and are never suppressed by one, at any age. Neither is a
missing duration: a payload that never carried `total_duration_ms` is a failed source in its own
right, so it counts as old and the transcript warning stands.

## When there is no host line

The block is normally *inserted*: cship's stdout is split, the meta segment is appended to its last
non-empty line, and our rows go in beneath it. When cship returns nothing there is no line to append
to and nowhere to insert, and skipping the insert — which is what this did — printed **nothing at
all**. An empty status line is the one output indistinguishable from the binary being uninstalled,
crashed, or never run, and it arrived at the worst possible moment: cship parses the same stdin we
do, so a malformed payload silences both of us at once and takes the diagnostic with it.

The block is now emitted on its own instead. Almost nothing in it comes from stdin — the limit rows
are the registry cache and the API, the account line is the credentials file — so what is genuinely
lost is the two token rows and the four meta figures, each of which already degrades to a sentinel
with a stated reason.

Two layout consequences:

- **The meta segment has no host line to ride on**, so it takes the first row of the block and opens
  with `—`, the sentinel for a source that reported nothing at all — here, cship. Single-width, so
  this row's left edge lands on column 1 exactly as `│` and `5h` do, by construction and not by
  measurement. If the segment is empty because cost and duration both genuinely read zero, the row
  is not drawn at all.
- **The width budget is unchanged.** The four columns of host padding are Claude Code's, not
  cship's, and the indent is still ours. Indent + marker + space costs 3 columns, putting the meta
  segment's measured worst case at 133 of 137 — the same width the token grid is built to.

**cship being *absent* is a different case and takes a different path.** `RunCship`'s `catch`
returns the raw stdin, which is a non-empty line, so the block is inserted beneath the echoed JSON
exactly as usual. Standalone happens only when cship ran and said nothing.
