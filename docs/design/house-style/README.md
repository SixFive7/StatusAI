<sub>[StatusAI](../../../README.md) / [Documentation](../../README.md)</sub>

# House style

How the status line is drawn in its documentation: the rules the
[decisions page](../limits-decisions.html), the example in this folder and the figures on the README
and the guide all follow.

## Read this before opening `weekly-wall.html`

`weekly-wall.html` is here **as a style reference only**. Its subject matter is wrong.

It documents a change that clamped the 5h session projection to the 7d weekly reset, on the
premise that the session counter is zeroed when the week rolls over. That premise is false. The
change was built, deployed and then reverted in full, and `src/Program.cs` contains none of it.

If you work on this code, **do not implement anything described in that file.** There is no
`BarCut`, no `WallColor`, no `walled` flag, no `projFull`, no `⇥` mark and no `┃` mark in the
product, and none of them should be added. See [limits.md](../../reference/limits.md) for the
correct limit model, and
[rejected-designs.md](../rejected-designs.md#rejected-clamping-the-session-projection-to-the-weekly-reset)
for the full record of the rejection.

The file carries a correction band at the top saying the same thing. Otherwise it was left intact
on purpose: a template is more useful as a real worked example than as a lorem-ipsum skeleton, and
this one happens to show how to document a design that did not survive.

## What to copy

Copy the presentation of `weekly-wall.html`, which comes down to these rules.

The terminal is drawn cell by cell, the way a terminal draws it. Every character is a fixed `1ch`
cell (`display:inline-block; width:1ch; text-align:center`), so a mockup is right to the character
column instead of roughly aligned. The whole layout problem in `docs/reference/layout.md` is column
arithmetic, and a mockup that fudges the alignment cannot be used to check a design.

Colours are taken from the source. Every colour in that page is a literal from `Program.cs`:
`#7DCFFF` cyan, `#E0AF68` amber, `#F7768E` red, `#6E738D` dim, `#A6E3A1` green, `#C6F6C1` light
green and `#B4BEFE` lavender, on a `#16161E` ground. A doc that invents a colour is describing
something the binary does not do.

`weekly-wall.gen.js` generates the HTML from a row spec. Writing several hundred `<span>` cells by
hand guarantees drift between the mockup and the arithmetic it claims to illustrate, and the
generator also makes the mockup checkable: the check at the bottom of this file counts cells
instead of trusting the eye.

Before and after go in two panes at the same width and the same numbers, so a reader can compare
them by eye. State the width cost in characters; if it is zero, prove it by showing that both rows
measure the same.

Callouts are anchored to real character columns. Brackets are positioned with
`left: calc(var(--c) * 1ch)` against measured offsets into the row, so a callout points at exactly
the cells it names. Keep the annotation elements at the terminal's own `font-size`, or `ch`
resolves to a different width and the anchors silently drift.

The terminal pane is a device, not page chrome, so it stays dark in both themes, as a screenshot
would. The page chrome around it gets the full three-state light/dark treatment: bare `:root`,
`prefers-color-scheme` guarded by `:not([data-theme="light"])`, and `[data-theme="dark"]`.

Finally, say what a design costs: the character budget, the glyph widths, which column grows. A
design note for the status line is not finished until it has a width figure.

## Figures for Markdown pages

GitHub shows an HTML file as source, so the pages people read on GitHub (the README and the guide)
get their figures as images. [figures.gen.js](../../assets/figures.gen.js) draws them in this style
and has Edge, headless, export each one as a PNG; [docs/assets/README.md](../../assets/README.md)
lists them, with the command that regenerates them all. The rules above hold, and six more:

- Figures are drawn from the render tests, never typed. Each one names renders in `tests/expected`
  (the bytes the binary is held to) and the lines and columns to show. A crop never splits a
  two-column cell, and a callout finds its columns by matching the text it names, so a change of
  output either carries the callout along or stops the generator.
- A cell is 8 px wide at 13,33 px, JetBrains Mono's own advance, so a glyph's box and its cell are
  one thing; an emoji gets two. The widths are Windows Terminal's: two columns for East-Asian Wide
  emoji, one for Neutral and Ambiguous glyphs and for cship's Nerd Font icons. A glyph whose width
  the generator does not know stops it.
- The device sits on a transparent ground. The terminal stays dark and the margin around it is
  transparent, with a shadow that fades out inside it, so GitHub's light and dark pages show the
  same figure with nothing to re-theme. The callout labels sit inside the device, on its ground,
  for the same reason.
- The PNGs are taken at twice the density, so the text stays sharp on a high-density screen and
  when GitHub scales a wide figure down.
- Keep a figure to 93 columns or fewer where that can be helped. GitHub's README column is about
  830 px, which holds a device of 93 columns at its own size; a wider one is scaled down with
  everything in it. The whole status line is 133 columns at the default width and is shown whole
  in two figures only, the plain one and the hero; every other figure is cropped to the rows and
  columns it is about.
- Every `<img>` has alt text and a `width` equal to the figure's CSS width, which is half its pixel
  width, so GitHub draws it at its own size, or narrower where the column is. The generator checks
  both on every Markdown page, and refuses a label that leaves its device or comes within 10 px of
  another.

## Regenerating

```bash
node weekly-wall.gen.js weekly-wall.html
```

No dependencies beyond Node. Check a regenerated mockup by counting its cells rather than by
looking at it. In the two-column composition the left rows are 60 cells and the composed rows 120:

```bash
node -e "const h=require('fs').readFileSync('weekly-wall.html','utf8');
[...h.matchAll(/<div class=\"ln\">((?:(?!<div class=\"ln\">)[\s\S])*?)<\/div>/g)]
  .forEach((m,i)=>console.log(i,(m[1].match(/<i class=\"g /g)||[]).length,'cells'))"
```

## Glyph widths

Not everything the status line draws is single-width. The emoji are two columns wide in Windows
Terminal, and they appear only where that width is accounted for: the token grid declares two
columns for each of its icons instead of measuring them, the layout counts the account's `👤` as
two columns, and the meta segment's `📝`, `💸` and `💰` sit at the end of the model line, where
nothing lines up after them and the worst case was measured.

Everywhere else a glyph has to be single-width. Every row opens with one (see the alignment rule in
[layout.md](../../reference/layout.md#alignment-why-the-rule-exists)), and the limit rows are laid
out by counting one column per character, so a new glyph for those rows must be single-width too.
The marks in the template, `⇥` U+21E5 and `┃` U+2503, sit in the same narrow East-Asian classes
(Neutral and Ambiguous) as the glyphs the limit rows already use, `│` U+2502, `↻` U+21BB, `→`
U+2192, `⇢` U+21E2, `●` U+25CF, `○` U+25CB and `✗` U+2717, so they render single-width wherever
those do. That check is why they were safe to propose. Run it again for any new glyph, whatever
becomes of the design that brings it in.
