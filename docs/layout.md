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

The limit rows — 5h, 7d, the scoped row and any meter after it — use a different mechanism from the
token grid: everything except the two projection bars is known, so `RenderRows` solves for the bar
length that exactly fills the terminal, and recomputes it every render.

```
fixedPart = 4 + 1 + 2 + 2·rowConst + wLbl[0] + wLbl[1] + 2·(wNow + wReset + wTo100 + wProj + wNowBar)
capBar    = clamp((term - 2 - fixedPart) / 2, 10, 30)
```

Every width is kept per column — `[0]` the left, `[1]` the right; each of the five in the bracket is
charged at the wider of the two, which is the max across all rows. See
[widths are per column](#widths-are-per-column) below.

`rowConst` is 15 — the glyphs and spaces in a row outside the label, the four numeric columns and
the two bars. It was 25 and carried the now-bar's ten cells inside it; the now-bar is now the
explicit `wNowBar` term, computed as a max across its column exactly as `wBar` is, so the constant drops
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
lose ten characters between them. The budget also charges every width at its widest across both
columns, so one long value — a `2d03h` reset, a three-digit projection — silently shortens every
bar on the line. This is why a
design note for these rows is not finished until it states its character cost.

### Widths are per column

Column widths are computed per render as the max needed across the rows **of one column**, so
nothing is padded wider than the current values require. The left column's rows stack — `5h` above
`7d` — and have to agree to line up; so do the right column's, the scoped row and every meter under
it. A row beside another has nothing to line up with.

Only the labels used to be kept apart this way, so `5h` / `7d` never inherit the width of a longer
label like `Fable`. The other five widths were shared across both columns, which aligned nothing
and padded the lone right-hand row out to the widest value on the left. A `7d` projecting 226%
draws 22 cells, so every row's projection bar was padded to 22, and `Fable`'s ten-cell bar was
followed by fourteen blank columns before its `24%` — at the far end of the line, next to nothing.
The same line before and after:

```
7d ●●●●●●●●●○ 89% ↻ 2d09h → 4h35m ⇢ ●●●●●●●●●●✗✗✗✗✗✗✗✗✗✗✗✗ 226%  Fable ●●●○○○○○○○ 24% ↻ 2d09h → never ⇢ ●●●○○○○○○○              24%
7d ●●●●●●●●●○ 89% ↻ 2d09h → 4h35m ⇢ ●●●●●●●●●●✗✗✗✗✗✗✗✗✗✗✗✗ 226%  Fable ●●●○○○○○○○ 24% ↻ 2d09h → never ⇢ ●●●○○○○○○○ 24%
```

It was always so, but it only became conspicuous once the 7d horizon was uncapped and a steady
weekly burn began projecting past 200%. In the left column the same padding is what puts `5h`'s
`57%` exactly under `7d`'s `226%`, and that stays.

**The budget is unchanged.** The solver still charges each width at the wider of the two columns,
which is exactly the max across all rows it charged before, so `capBar` is what it always was, and
so is the guarantee: a column's own widths can only be narrower than that, never wider. What the
right-hand row stops spending is simply not spent. `Compose()` only needs the two left-column rows
to be one width, which they still are, since the right column starts after them.

### More than three meters

Every meter the server sends gets a row (see [limits.md](limits.md#every-meter-is-drawn)). The first
two are the left column; the third sits right of `7d`; each one after it takes a line of its own in
the right column, under the one before, with the left column left blank:

```
5h ●●●○○○○○○○ 29% ↻ 3h36m → 2h28m ⇢ ●●●●●●●●●●✗✗✗✗         133%  👤 another@example.com · Max 20 · CC 99% · Chat 0% · Cowork 1%
7d ●●●●●●○○○○ 54% ↻ 4d10h → 6h52m ⇢ ●●●●●●●●●●✗✗✗✗✗✗✗✗✗✗✗✗ 300%  Fable  ●●●●●○○○○○ 47% ↻ 4d10h → never ⇢ ●●●●●○○○○○ 47%
                                                                 Cowork ●●○○○○○○○○ 12% ↻ 4d10h → —     ⇢ ●●○○○○○○○○ 12%
```

The cost is one line per extra meter, and the right column's widths — `Cowork` against `Fable`
here — which the solver charges like any other. The two-column test counts every right-hand row as
well as the account, so a long enough label takes the block to the stacked layout rather than past
the edge. Stacked, every meter is a row of its own, in order.

## The account line and the breakdown

The account sits right of `5h`, and after it the product breakdown — each product's share of this
week's usage, in the account's text colour, `·` dim between them:

```
👤 another@example.com · Max 20 · CC 99% · Chat 0% · Cowork 1%
```

**It takes no part in the layout decision.** The two-column test is made on the account alone, so
the breakdown can never push the rows into the stacked layout. It then gets whatever room that line
has left — `term − 4` minus the left column and the gap, or `term − 5` stacked — in whole entries or
not at all: every entry while they all fit, then without the 0% ones, then none. It never wraps and
is never cut inside an entry. The account is charged as the layout test charges it, one column
wide of the truth, so the line always passes the test the layout was decided by.

At the default 141 columns and the 2026-09-23 figures that leaves 71 columns for the account line:
the whole breakdown fits beside an address of up to 27 characters (`another@example.com` is 19,
with 9 columns to spare), the breakdown without its 0% entries up to 37, and past that none.


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
reported zero: the `↻` column when the payload carried no `resets_at`, the `→` column of a meter no
history series follows, `⏱` / `💰` when the payload carried no `cost` block, and the marker opening
the standalone render below, where the source that reported nothing is cship itself. It is never wider than the value it replaces, so no column it
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

## Palette

Every colour is a literal in `Program.cs`, and a doc that shows one the binary does not draw is
describing something that does not exist. The ground is Windows Terminal's default Campbell
`#0C0C0C` — the Claude Code profile sets no scheme — under a wallpaper at 10% opacity, which can
lift it to `#242424` where the image is white; the docs' pane ground is `#16161E`. Contrast is the
WCAG ratio against each.

| colour | where | Campbell | lightest wallpaper | `#16161E` |
|---|---|---|---|---|
| `#7DCFFF` cyan | bar cells 1–7, percentages under 70; bold, the token-grid totals | 11,40 | 9,05 | 10,48 |
| `#E0AF68` amber | bar cells 8–9, percentages 70–89, `warning` labels | 9,78 | 7,76 | 8,99 |
| `#F7768E` red | bold: bar cells 10+ and `✗`, percentages 90+, `critical` labels, `→` when 100% lands before the reset, projections past 100%, `⚠` rows; plain: removed lines | 7,39 | 5,87 | 6,80 |
| `#28A428` forest | `→` when the row's own reset comes before 100% would | 5,99 | 4,75 | 5,51 |
| `#A6E3A1` green | `↻`, added lines | 13,16 | 10,44 | 12,10 |
| `#C6F6C1` light green | the reset time | 16,16 | 12,83 | 14,86 |
| `#6E738D` dim | empty cells, `→` otherwise, `⇢`, `·`, the plan, the token grid's main scope and `│` | 4,19 | 3,33 | 3,85 |
| `#A9B1D6` text | the account and its breakdown, the meta figures | 9,27 | 7,35 | 8,52 |
| `#B4BEFE` lavender | `⏱`, the token grid's sub scope | 10,93 | 8,68 | 10,05 |

**Forest is the only one chosen against these grounds.** It is X11 `forestgreen` `#228B22` — hue
120°, saturation 61% — lifted from 34% to 40% lightness, because `#228B22` itself is 4,10:1 on the
docs' ground and 3,54:1 where the wallpaper is lightest; 40% is the first step at 4,5:1 on all
three. It has to read as more of a green than the two already on the row, not as a third pastel:
CIELAB chroma 76 against the 41 of `↻`'s `#A6E3A1`, and ΔE2000 21,7 from it and 27,3 from the
reset time's `#C6F6C1`. The screenshot this was checked against draws the ground at `#1B1B1B` on
the median and `#222222` at the 99th percentile, between the table's first two columns.

Dim is the one colour under 4,5:1 on every ground, which is one more reason a `→` worth reading no
longer uses it.

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
missing `context_window.used_percentage`, since cship draws its context bar from that field and a
missing one draws the same `○○○ 0%` as a genuinely empty context — only the colour differs, the
bar's configured style for a number and the default foreground otherwise; a suspended usage fetch;
any of the four ways the token walk can fail; and cship producing no output at all.

**Being on credit has a row of its own**, always last and never joined to the others by `·`: it is
not a source that failed but usage being billed that the plan should have covered, with the amount
spent in it — `⚠ on credit — $12,40 spent beyond the plan this period`. What triggers it is in
[limits.md](limits.md#on-credit). Same red, same budget, and cut the same way as any other reason
if the terminal is too narrow for it.

### "No messages yet" is a zero, not a gap

Until the first API response of a context — every new session, and again after `/clear` — Claude
Code sends `used_percentage`, `remaining_percentage` and `current_usage` all as `null`. That is its
documented *no messages yet*, and in the bundle all three come from one lookup of the last response's
usage (`if(!e)return{used:null,remaining:null}`, the same in 2.1.234 and 2.1.280), so they are null
together or not at all. A payload in exactly that shape is a source honestly reporting nothing yet,
like a `cost` of 0: no response has been measured, so cship's `0%` is the honest reading, and the
row says nothing.

Any other shape is still a missing source, at any age of session: no `context_window`, no
`used_percentage` in it, a `null` beside a `current_usage` that holds a measurement, or a value that
is not a number.

The first version of this check treated the `null` as missing, so every new session opened with a red
row until its first response. Its fixture for a genuine fresh session was `used_percentage: 0`,
which Claude Code never sends — and cship draws `null`, `0` and an absent field as the same
`○○○ 0%`, so a comparison of the visible text could not tell them apart. A fixture for this path has
to be what the payload builder really emits, not a plausible stand-in.

### The transcript is written at the first prompt

Not at session start: every transcript's creation time is its first user record, measured on 2.1.233,
2.1.234 and 2.1.275–2.1.280 alike. A session left open has no file for as long as nobody types, with
nothing wrong.

So a transcript file that does not exist is not reported while `total_duration_ms` is at or below
30 s, **or** while the payload is in the *no messages yet* shape above, at any age. The 30 s is not a
new threshold — `Hm()` rounds to whole minutes, so it is the same boundary as the `⏱ 0m` the meta
segment is showing at the time — but on its own it was too short: a session left idle past it went
red, because the file it expected by then is not written until someone types. Once a context has
been measured the file should be there, and past 30 s its absence is stated as loudly as ever.

The other three token failures — no `transcript_path`, an unusable one, an exception during the walk
— are not explained by a young or unprompted session and are never suppressed by one, at any age.
Neither is a missing duration: a payload that never carried `total_duration_ms` is a failed source
in its own right and counts as old, so beside it only the *no messages yet* shape keeps a missing
transcript quiet.

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
