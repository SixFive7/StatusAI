# Documentation templates

## ⚠ Read this before opening `weekly-wall.html`

`weekly-wall.html` is here **as a style reference only**. Its subject matter is wrong.

It documents a change that clamped the 5h session projection to the 7d weekly reset, on the
premise that the session counter is zeroed when the week rolls over. **That premise is false.**
The change was built, deployed, and then reverted in full. `src/Program.cs` contains none of it.

If you are an agent working on this repository: **do not implement anything described in that
file.** There is no `BarCut`, no `WallColor`, no `walled` flag, no `projFull`, no `⇥` mark and no
`┃` mark in the product, and none of them should be added. See [../limits.md](../limits.md) for
the correct limit model and the full record of the rejection.

The file carries a correction band at the top saying the same thing. It was left otherwise intact
on purpose — a template is more useful as a real worked example than as a lorem-ipsum skeleton,
and this one happens to be a good example of documenting a design that did not survive.

## What to copy

The house style for visual documentation of the status line:

**Render the terminal as the terminal.** Every character is a fixed `1ch` cell
(`display:inline-block; width:1ch; text-align:center`), so mockups are correct to the character
column rather than approximately aligned. This matters more than it sounds: the whole layout
problem in `../layout.md` is column arithmetic, and a mockup that fudges alignment cannot be used
to check a design.

**Take the palette from the source, not from taste.** Every colour in that page is a literal from
`Program.cs` — `#7DCFFF` cyan, `#E0AF68` amber, `#F7768E` red, `#6E738D` dim, `#A6E3A1` green,
`#C6F6C1` light green, `#B4BEFE` lavender, on a `#16161E` ground. If a doc invents a colour, the
doc is describing something the binary does not do.

**Generate, don't hand-write.** `weekly-wall.gen.js` emits the HTML from a row spec. Hand-writing
several hundred `<span>` cells guarantees drift between the mockup and the arithmetic it claims to
illustrate. The generator also makes the mockup checkable — the verification at the bottom of this
file counts cells rather than trusting the eye.

**Before and after, at identical width.** Two panes at the same numbers, so a reader can diff them
visually. State the width cost explicitly in characters; if it is zero, prove it by showing both
rows measure the same.

**Anchor annotations to real character columns.** Brackets are positioned with
`left: calc(var(--c) * 1ch)` against measured offsets into the row, so a callout points at exactly
the cells it names. Keep the annotation elements at the terminal's own `font-size` or `ch`
resolves to a different width and the anchors silently drift.

**The terminal pane is a device, not page chrome.** It stays dark in both themes — you do not
re-theme a screenshot. Page chrome around it gets the full three-state light/dark treatment
(bare `:root`, `prefers-color-scheme` guarded by `:not([data-theme="light"])`, and
`[data-theme="dark"]`).

**Say what a design costs.** Character budget, glyph widths, which column grows. A status-line
design note without a width figure is not finished.

## Regenerating

```bash
node weekly-wall.gen.js weekly-wall.html
```

No dependencies beyond Node. Verify a regenerated mockup by counting cells rather than looking at
it — for the two-column composition the left rows are 60 cells and the composed rows 120:

```bash
node -e "const h=require('fs').readFileSync('weekly-wall.html','utf8');
[...h.matchAll(/<div class=\"ln\">((?:(?!<div class=\"ln\">)[\s\S])*?)<\/div>/g)]
  .forEach((m,i)=>console.log(i,(m[1].match(/<i class=\"g /g)||[]).length,'cells'))"
```

## Glyph widths

Everything the status line draws is single-width, and any new glyph must be too — see the
alignment rule in [../layout.md](../layout.md). The marks in the template (`⇥` U+21E5, `┃` U+2503)
sit in the same East-Asian-Ambiguous class as the glyphs already in use (`│` U+2502, `↻` U+21BB,
`→` U+2192, `⇢` U+21E2, `●` U+25CF, `○` U+25CB, `✗` U+2717), so they render single-width wherever
those do. That check is the reason they were safe to propose — it is worth repeating for anything
new, whatever the fate of the design that introduces it.
