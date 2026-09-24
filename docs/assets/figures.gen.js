// Generates the figures in docs/assets/ from the render tests' expected output. Node only, no
// dependencies; Microsoft Edge, headless, draws the PNGs:
//
//   node docs/assets/figures.gen.js               every figure, and docs/assets/README.md
//   node docs/assets/figures.gen.js hero tokens   only these
//   node docs/assets/figures.gen.js --check       draw every figure into .work/figures/check and
//                                                 compare it with docs/assets, byte for byte
//
// House style (docs/design/house-style/README.md): every terminal character sits in a fixed cell,
// every colour is a literal from Program.cs, callouts sit on measured columns, and the terminal is a
// device that stays dark in both themes. Here it is also the only thing on a transparent ground, so
// a figure reads the same on GitHub's light and dark pages.
//
// Nothing on a terminal line is typed. Each figure names renders in tests/expected — the exact
// bytes the deployed binary drew for a fixture at a width, which the render tests hold every build
// to — and which of their lines and columns to show. So a figure cannot drift from the binary
// without the render tests failing first, and a change of output recorded with
// `Test-Renders.ps1 -Update` is the one reason to regenerate. The generator refuses to run on a
// colour outside the palette, a glyph whose width it does not know, a callout whose text it cannot
// find, a crop through a two-column cell, or a label that leaves its device or meets another.
'use strict';
const fs = require('fs');
const path = require('path');
const { spawn, spawnSync } = require('child_process');
const { pathToFileURL } = require('url');

const ROOT = path.resolve(__dirname, '..', '..');
const EXPECTED = path.join(ROOT, 'tests', 'expected');
const WORK = path.join(ROOT, '.work', 'figures');

// ───────────────────────────────────────────── ANSI → cells
// Program.cs's palette, by the SGR triple it writes. cship adds one colour of its own: the model name
// is "bold cyan" in config/cship.toml, ANSI colour 6, whose RGB is up to the terminal's scheme; it is
// drawn as the palette's cyan, the colour the same config gives the context bar. Text with no colour
// is the terminal's foreground, #C0CAF5 as on the decisions page.
const RGB = { '125;207;255': 'cy', '224;175;104': 'or', '247;118;142': 'rd', '110;115;141': 'dm', '166;227;161': 'gr',
              '198;246;193': 'lg', '169;177;214': 'tx', '180;190;254': 'lv', '40;164;40': 'fo' };
const ANSI = { 36: 'cy' };
// How many columns Windows Terminal gives each character the renders contain. Wide: East-Asian Wide
// with emoji presentation — the token grid is laid out on that assumption (install.md). Narrow:
// Neutral or Ambiguous, and the Private Use Area, where cship's two Nerd Font icons live. Anything
// else stops the generator until someone has looked its width up.
const NARROW = new Set([0xB7, 0x2014, 0x2026, 0x20AC, 0x2192, 0x21BB, 0x21E2, 0x23F1, 0x2502, 0x25CB, 0x25CF, 0x26A0, 0x2717]);
const WIDE = new Set([0x26A1, 0x1F333, 0x1F33F, 0x1F464, 0x1F4B0, 0x1F4B8, 0x1F4BE, 0x1F4D6, 0x1F4DD, 0x1F527, 0x1F53A,
                      0x1F53B, 0x1FA99, 0x1FAB5]);
function cellWidth(cp) {
  if (cp >= 0x20 && cp < 0x7F) return 1;
  if (NARROW.has(cp) || (cp >= 0xE000 && cp <= 0xF8FF)) return 1;
  if (WIDE.has(cp)) return 2;
  throw new Error(`no column width known for U+${cp.toString(16).toUpperCase()}: look up its East-Asian width, then add it to NARROW or WIDE`);
}
function toCells(line) {
  let fg = null, bold = false; const out = [];
  for (let i = 0; i < line.length;) {
    if (line[i] === '\x1b') {
      const j = line.indexOf('m', i), p = line.slice(i + 2, j).split(';');
      for (let k = 0; k < p.length; k++) {
        if (p[k] === '0' || p[k] === '') { fg = null; bold = false; }
        else if (p[k] === '1') bold = true;
        else if (ANSI[p[k]]) fg = ANSI[p[k]];
        else if (p[k] === '38' && p[k + 1] === '2') {
          const t = `${p[k + 2]};${p[k + 3]};${p[k + 4]}`; k += 4;
          if (!RGB[t]) throw new Error('colour not in the Program.cs palette: ' + t);
          fg = RGB[t];
        } else throw new Error('unhandled SGR ' + p.join(';'));
      }
      i = j + 1; continue;
    }
    const cp = line.codePointAt(i), ch = String.fromCodePoint(cp); i += ch.length;
    out.push({ ch, cls: fg || 'df', b: bold, w: cellWidth(cp) });
  }
  return out;
}
const width = cells => cells.reduce((n, c) => n + c.w, 0);
// The plain text with one UTF-16 unit per column — a wide emoji is already two, ⚡ gets a filler —
// so a regex match index is a column. Match without the u flag.
const colText = cells => cells.map(c => c.w === 2 && c.ch.length === 1 ? c.ch + '\u200B' : c.ch).join('');
function span(cells, re, from = 0) {
  const t = colText(cells), g = new RegExp(re.source, 'g'); g.lastIndex = from;
  const m = g.exec(t); if (!m) throw new Error('callout target not found: ' + re + ' in ' + JSON.stringify(t));
  return { col: m.index, len: m[0].length };
}
// columns [c0, c1) of a line; a crop never splits a two-column cell
function crop(cells, c0, c1 = Infinity) {
  const out = []; let col = 0;
  for (const c of cells) {
    const a = col, b = col + c.w; col = b;
    if (b <= c0 || a >= c1) continue;
    if (a < c0 || b > c1) throw new Error(`a crop at columns ${c0}–${c1} splits ${JSON.stringify(c.ch)} at ${a}`);
    out.push(c);
  }
  return out;
}
const trimEnd = cells => { let n = cells.length; while (n && cells[n - 1].ch === ' ') n--; return cells.slice(0, n); };
const RENDERS = new Map(), USED = new Set();
function line(ref, i) {                        // line i of tests/expected/<ref>.ansi, as cells
  if (!RENDERS.has(ref)) {
    const f = path.join(EXPECTED, ref + '.ansi');
    if (!fs.existsSync(f)) throw new Error('no expected render ' + ref + ' in tests/expected');
    RENDERS.set(ref, fs.readFileSync(f, 'utf8').split('\n').map(toCells));
  }
  const L = RENDERS.get(ref)[i]; if (!L) throw new Error(`${ref} has no line ${i}`);
  USED.add(ref);
  return L;
}
// The two columns of a limit block: the left runs from the label to its ⇢ percentage, and the
// right starts after Compose()'s two-space gap. `from` 0 keeps the block's one-space indent.
const LEFT = /^ (\S.*?\d%)(?=  \S)/;
function leftCol(cells, from = 1) { const s = span(cells, LEFT); return crop(cells, from, s.col + s.len); }
function rightCol(cells) { const s = span(cells, LEFT); return crop(cells, s.col + s.len + 2); }

// ───────────────────────────────────────────── scenes
// A scene is a device: lines of cells, each optionally with a gutter label and callouts. A callout
// is a bracket on exact columns of its line, below it (or above, with up), and a label at the
// bracket's start; labels share a strip row unless given row 1. Cells are 8 px wide at 13,33 px, JetBrains Mono's own advance, so a
// glyph's box and its cell are the same thing.
const CW = 8, LH = 24, FS = 13.333;
const esc = s => String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
const mk = (s, text, tone = 'or', o = {}) => ({ col: s.col, len: s.len, text, tone, ...o });
const cellHtml = cells => cells.map(c => `<i class="g ${c.cls}${c.b ? ' b' : ''}${c.w === 2 ? ' w2' : ''}">${esc(c.ch)}</i>`).join('');
function strip(marks, up, off) {
  if (!marks.length) return '';
  const rows = [0, 1].map(r => marks.filter(m => (m.row || 0) === r)).filter(r => r.length);
  const brk = `<div class="annot${up ? ' up' : ''}">${marks.map(m =>
    `<span class="brk ${m.tone}" style="--c:${m.col + off};--w:${m.len}"></span>`).join('')}</div>`;
  const labs = (up ? [...rows].reverse() : rows).map(rs => `<div class="alabs">${rs.filter(m => m.text).map(m =>
    `<span class="alab ${m.tone}" style="--c:${m.col + off}">${esc(m.text)}</span>`).join('')}</div>`).join('');
  return up ? labs + brk : brk + labs;
}
function terminal({ lines, gut = 0, cols, big = false }) {
  if (cols === undefined) cols = Math.max(...lines.map(L => width(L.cells)));
  for (const L of lines) if (width(L.cells) > cols) throw new Error(`a line of ${width(L.cells)} columns in a scene of ${cols}`);
  const body = lines.map(L => {
    const marks = L.marks || [], below = marks.filter(m => !m.up), above = marks.filter(m => m.up);
    return strip(above, true, gut) + `<div class="ln">${gut ? `<span class="gut">${esc(L.gut || '')}</span>` : ''}${cellHtml(L.cells)}</div>`
         + strip(below, false, gut);
  }).join('\n');
  return { html: `<div class="dev"><div class="term${big ? ' big' : ''}" style="--cols:${cols + gut};--gut:${gut}">${body}</div></div>`,
           cols: cols + gut, widest: Math.max(...lines.map(L => width(L.cells))) };
}
const SCENES = [];
// id, what it shows (the index's description and the default alt text), how it is drawn
function scene(id, shows, build) { SCENES.push({ id, shows, build }); }

// ─── plain: the hero's render with nothing added, exactly what the terminal shows
scene('plain', 'The whole status line exactly as the terminal shows it at the default 141 columns, with nothing added.', () => {
  const R = 'showcase.w141';
  return terminal({ big: true, lines: [0, 1, 2, 3, 4].map(i => ({ cells: line(R, i) })) });
});

// ─── the hero: every row at once, at the default width
scene('hero', 'The whole status line at the default 141 columns: the model line with the meta segment, the token grid, the limit rows with the account and the breakdown.', () => {
  const R = 'showcase.w141', L = [0, 1, 2, 3, 4].map(i => line(R, i));
  return terminal({ big: true, lines: [
    { cells: L[0], marks: [mk(span(L[0], /\S.*?\d+%/), 'model · effort · context', 'cy'),
                           mk(span(L[0], /⏱.*\S/), 'session time · lines · cost per hour · cost', 'tx')] },
    { cells: L[1] },
    { cells: L[2], marks: [mk(span(L[2], /│.*\S/), 'tokens and tool calls: this conversation 🪵 · its sub-agents 🌿 · the whole tree 🌳', 'lv')] },
    { cells: L[3] },
    { cells: L[4], marks: [mk(span(L[4], /7d.*?\d%(?=  )/), 'your limits: now · reset · 100% in · at the reset', 'or'),
                           mk(span(L[4], /Fable.*\S/), 'one model’s week', 'or')] },
  ] });
});

// ─── limits: the left column of the shot fixture — the screenshot of 23 September, 19:57 — every segment named
scene('limits', 'The 5h and 7d rows of the `shot` fixture, every segment named: 7d at 89% runs out in 4h35m, before its reset, so the time is red; 5h resets before it would, so its time is forest green.', () => {
  const R = 'shot.w141', a = leftCol(line(R, 1)), b = leftCol(line(R, 2));
  return terminal({ lines: [
    { cells: a, marks: [mk(span(a, /→ \S+/), '100% in · forest: the reset comes first', 'fo', { up: true })] },
    { cells: b, marks: [mk(span(b, /●.*?\d+%/), 'used now', 'cy'), mk(span(b, /↻ \S+/), 'resets in', 'gr'),
                        mk(span(b, /⇢.*%/), 'at the reset, at this pace', 'rd'),
                        mk(span(b, /→ \S+/), '100% in · red: before the reset', 'rd', { row: 1 })] },
  ] });
});

// ─── the → traffic light, a row of each kind
scene('states', 'One limit row per state of →: red, forest, never, early, and maxed with its ⇢ segment greyed.', () => {
  const rows = [['red', leftCol(line('shot.w141', 2)), 'rd', '100% comes before the reset'],
                ['forest', leftCol(line('shot.w141', 1)), 'fo', 'the reset comes first'],
                ['never', rightCol(line('shot.w141', 2)), 'dm', 'not moving'],
                ['early', leftCol(line('rows-early.w141', 1)), 'dm', 'not enough history for a trend yet'],
                ['maxed', leftCol(line('maxed-5h.w141', 1)), 'dm', 'blocked until the reset, so ⇢ is greyed']];
  return terminal({ gut: 8, lines: rows.map(([g, cells, tone, note]) => ({ cells: trimEnd(cells), gut: g, marks: [mk(span(cells, /→ \S+/), note, tone)] })) });
});

// ─── a first session: no token rows before the first reply, no ⚠ row, and no trend yet
scene('first-run', 'A session seconds old, before its first reply, with no history yet: the model line, then the limit rows, every one reading → early; no token rows and no ⚠ row.', () => {
  const R = 'rows-early.w141', L = [0, 1, 2].map(i => trimEnd(line(R, i)));
  return terminal({ lines: [{ cells: L[0] }, { cells: L[1] },
    { cells: L[2], marks: [mk(span(L[2], /→ early/), 'early: a trend needs ten minutes of history', 'tx')] }] });
});

// ─── the rows stacked, at 100 columns
scene('stacked', 'At 100 columns the limit rows stack, with the account on a line of its own.', () =>
  terminal({ lines: [3, 4, 5, 6].map(i => ({ cells: trimEnd(line('showcase.w100', i)) })) }));

// ─── the account and where the week went
scene('breakdown', 'The account line: the signed-in address, the plan, and how this week’s usage split across products.', () => {
  const a = rightCol(line('showcase.w141', 3));
  return terminal({ lines: [{ cells: a, marks: [mk(span(a, /👤.*Max 20/), 'account · plan', 'tx'), mk(span(a, /CC.*\S/), 'where this week went', 'cy')] }] });
});
scene('breakdown-fit', 'The breakdown in whole entries or none: every entry, then without its 0% entries, then none, as the address grows.', () => {
  const rows = [['all', 'showcase.w141', 3], ['no 0%', 'long-email-35.w141', 1], ['none', 'long-email-46.w141', 1]];
  return terminal({ gut: 7, lines: rows.map(([g, R, i]) => ({ cells: rightCol(line(R, i)), gut: g })) });
});

// ─── cship's half of the model line, and the meta segment StatusAI appends to it
scene('model', 'cship’s model line: the model, the effort level and a 30-cell context bar.', () => {
  const L = line('showcase.w141', 0), m = span(L, /\S.*?\d+%/), c = crop(L, 0, m.col + m.len);
  return terminal({ lines: [{ cells: c, marks: [mk(span(c, /\S Opus.*?\)/), 'model', 'cy'), mk(span(c, /⚡\u200B \S+/), 'effort', 'rd'),
                                                  mk(span(c, /●.*%/), 'context used', 'cy')] }] });
});
scene('meta', 'The meta segment: session time, lines added and removed, cost per hour and cost so far, in dollars and euros.', () => {
  const L = line('showcase.w141', 0), m = span(L, /⏱.*\S/), c = crop(L, m.col, m.col + m.len);
  return terminal({ lines: [{ cells: c, marks: [mk(span(c, /⏱ \S+/), 'session', 'lv'), mk(span(c, /📝 \S+ \S+/), 'lines', 'gr'),
                                                  mk(span(c, /💸 \S+/), 'cost per hour', 'tx'), mk(span(c, /💰 \S+/), 'cost so far', 'tx')] }] });
});

scene('context-cost', 'The model line from its context bar on: how full the context is, then the meta segment — session time, lines changed, cost per hour and cost so far.', () => {
  const L = line('showcase.w141', 0), s = span(L, /●.*\S/), c = crop(L, s.col, s.col + s.len);
  return terminal({ lines: [{ cells: c, marks: [mk(span(c, /●.*?%/), 'context used', 'cy'), mk(span(c, /⏱ \S+/), 'session', 'lv'),
    mk(span(c, /📝 \S+ \S+/), 'lines', 'gr'), mk(span(c, /💸 \S+/), 'cost per hour', 'tx'), mk(span(c, /💰 \S+/), 'cost so far', 'tx')] }] });
});

// ─── the token grid at 120 columns, its gutters at their narrowest
scene('tokens', 'The token grid: three groups of three columns, each group one kind of count, each column one scope.', () => {
  const R = 'showcase.w120', a = line(R, 1), b = line(R, 2);
  const group = (re, text) => mk(span(a, re), text, 'tx', { up: true });
  // the scopes are named under the first group; the value colours carry them across the other two
  const scope = [[/🔻🪵 \S+/, 'main', 'dm'], [/🔻🌿 \S+/, 'sub-agents', 'lv'], [/🔻🌳 \S+/, 'whole tree', 'cy']];
  return terminal({ lines: [
    { cells: a, marks: [group(/🔺🪵.*?🔺🌳 \S+/, '🔺 fresh prompt  🔻 output'), group(/💾🪵.*?💾🌳 \S+/, '💾 cache writes  📖 cache reads'),
                        group(/🔧🪵.*?🔧🌳 +\S+/, '🔧 tool calls  🪙 all tokens')] },
    { cells: b, marks: scope.map(([re, text, tone]) => mk(span(b, re), text, tone)) },
  ] });
});

// ─── warnings: all three kinds of ⚠ row, in their order
scene('warnings', 'The three kinds of ⚠ row, in the order they come: a source that failed, in red; a limit this status line does not draw yet, in amber; usage billed beyond the plan, in red and always last.', () => {
  const R = 'every-warning.w120', w = [3, 4, 5].map(i => trimEnd(line(R, i)));
  return terminal({ lines: [{ cells: leftCol(line(R, 2), 0) },
    { cells: w[0], marks: [mk(span(w[0], /⚠.*\S/), 'a source that failed, and why', 'rd')] },
    { cells: w[1], marks: [mk(span(w[1], /⚠.*\S/), 'a limit it does not draw yet', 'or')] },
    { cells: w[2], marks: [mk(span(w[2], /⚠.*\S/), 'billed beyond the plan: always the last row', 'rd')] }] });
});

// ─── one usage fetch for every open session: the rule in Program.cs, played out
// GetUsage(): a render draws the cached rows while they are under 50 s old; otherwise the one render
// that wins the lock fetches, and every other draws the last cached rows rather than wait. The
// render times are an illustration — three sessions, the middle one busy — and the rule is the
// code's.
const RENDER_TIMES = [[8, 68, 128, 188, 248], [21, 33, 47, 60, 84, 97, 139, 152, 170, 199, 216, 244, 262, 279], [55, 115, 175, 235, 295]];
function playFetches(times, fresh = 50) {
  const ev = times.flatMap((ts, s) => ts.map(t => ({ t, s }))).sort((x, y) => x.t - y.t);
  let last = -Infinity;
  for (const e of ev) { e.fetch = e.t - last >= fresh; if (e.fetch) last = e.t; }
  return ev;
}
scene('sessions', 'An illustration of three open sessions over five minutes: each render either fetches the usage (only when the shared copy is 50 seconds old or more) or draws the shared copy.', () => {
  const ev = playFetches(RENDER_TIMES), T = 300, Wd = 720, H = 196, x0 = 104, x1 = Wd - 18, yApi = 34, lane = i => 76 + i * 30;
  const X = t => x0 + t / T * (x1 - x0), f = n => Math.round(n * 10) / 10;
  let g = '';
  for (let m = 0; m <= 5; m++) {
    g += `<line x1="${f(X(m * 60))}" x2="${f(X(m * 60))}" y1="${yApi - 14}" y2="${lane(2) + 14}" stroke="#6E738D" stroke-opacity=".18"/>`;
    g += `<text x="${f(X(m * 60))}" y="${lane(2) + 34}" text-anchor="middle" class="ax">${m}:00</text>`;
  }
  g += `<text x="${x0 - 14}" y="${yApi + 4}" text-anchor="end" class="ax strong">usage API</text>`;
  [0, 1, 2].forEach(i => {
    g += `<text x="${x0 - 14}" y="${lane(i) + 4}" text-anchor="end" class="ax">session ${i + 1}</text>`;
    g += `<line x1="${x0}" x2="${x1}" y1="${lane(i)}" y2="${lane(i)}" stroke="#6E738D" stroke-opacity=".35"/>`;
  });
  g += `<line x1="${x0}" x2="${x1}" y1="${yApi}" y2="${yApi}" stroke="#6E738D" stroke-opacity=".35"/>`;
  for (const e of ev.filter(e => e.fetch)) {
    g += `<rect x="${f(X(e.t))}" y="${yApi - 5}" width="${f(Math.min(X(e.t + 50), x1) - X(e.t))}" height="10" rx="3" fill="#7DCFFF" fill-opacity=".16"/>`;
    g += `<line x1="${f(X(e.t))}" x2="${f(X(e.t))}" y1="${yApi}" y2="${lane(e.s)}" stroke="#7DCFFF" stroke-opacity=".55" stroke-width="1.5"/>`;
    g += `<circle cx="${f(X(e.t))}" cy="${yApi}" r="4" fill="#7DCFFF"/>`;
  }
  for (const e of ev) g += e.fetch ? `<circle cx="${f(X(e.t))}" cy="${lane(e.s)}" r="5.5" fill="#7DCFFF"/>`
                                   : `<circle cx="${f(X(e.t))}" cy="${lane(e.s)}" r="4.5" fill="#16161E" stroke="#A9B1D6" stroke-opacity=".75" stroke-width="1.5"/>`;
  const n = ev.filter(e => e.fetch).length;
  const svg = `<svg viewBox="0 0 ${Wd} ${H}" width="${Wd}" height="${H}" role="img">${g}</svg>`;
  const legend = `<div class="legend"><span><i class="dot on"></i>fetched, for every session</span><span><i class="dot"></i>drew the shared copy</span>`
               + `<span><i class="band"></i>fresh for 50 s</span><span class="sum">an illustration · ${ev.length} renders · ${n} fetches</span></div>`;
  return { html: `<div class="dev viz">${legend}${svg}</div>`, from: 'the rule in `GetUsage()`, played out over invented render times' };
});

// ─── the download button
scene('download', 'A button: Download for Windows.', () => ({ html: `<div class="btn"><svg viewBox="0 0 24 24" width="22" height="22" aria-hidden="true">
  <path d="M12 3v11m0 0-4.5-4.5M12 14l4.5-4.5M4 16v3a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2v-3" fill="none" stroke="#16161E" stroke-width="2.2"
  stroke-linecap="round" stroke-linejoin="round"/></svg><span>Download for Windows</span></div>`, shotClass: 'bt', from: 'the palette: cyan on the terminal ground' }));

// ─── the decisions page, as it draws itself: its week chart
scene('decisions-week', 'The week chart from the decisions page: the 7d meter and three ways of forecasting it, minute by minute, from Saturday 05:00 to Wednesday 20:03.', () => ({
  capture: { file: path.join(ROOT, 'docs', 'design', 'limits-decisions.html'), selector: '#week-viz', width: 1044,
             // only the chart: the page around it keeps its layout but is not drawn, and the hint to
             // hover it is left out of a picture that cannot be hovered
             css: 'html,body{background:transparent!important} body *{visibility:hidden!important} '
                + '#week-viz, #week-viz *{visibility:visible!important} #week-viz #xh{visibility:hidden!important} .vizhelp{display:none} '
                + '#week-viz{box-shadow:0 1px 2px rgba(10,12,20,.14),0 6px 16px rgba(10,12,20,.14)!important}' } }));

// ───────────────────────────────────────────── page
const css = `
@font-face{font-family:Term; src:local("JetBrainsMono NF"), local("JetBrainsMono NF Regular"), local("JetBrainsMonoNF-Regular"); font-weight:400}
@font-face{font-family:Term; src:local("JetBrainsMono NF Bold"), local("JetBrainsMonoNF-Bold"); font-weight:700}
html,body{margin:0; background:transparent}
body{font-family:"Segoe UI",system-ui,sans-serif}
.shot{display:inline-block; padding:12px 18px 26px; vertical-align:top}
.shot.bt{padding:8px 14px 18px}
.dev{background:#16161E; border:1px solid #262738; border-radius:10px; padding:18px 22px 20px;
  box-shadow:0 1px 2px rgba(10,12,20,.14),0 6px 16px rgba(10,12,20,.14)}
.term{font-family:Term,"Segoe UI Emoji",monospace; font-size:${FS}px; line-height:${LH}px; width:calc(var(--cols) * ${CW}px); color:#C0CAF5}
.ln{white-space:pre; height:${LH}px}
.g{display:inline-block; width:${CW}px; text-align:center; font-style:normal}
.g.w2{width:${2 * CW}px}
.b{font-weight:700}
.gut{display:inline-block; width:calc(var(--gut) * ${CW}px); color:#6E738D; font-style:normal}
.cy{color:#7DCFFF} .or{color:#E0AF68} .rd{color:#F7768E} .dm{color:#6E738D} .gr{color:#A6E3A1} .lg{color:#C6F6C1}
.tx{color:#A9B1D6} .lv{color:#B4BEFE} .df{color:#C0CAF5} .fo{color:#28A428}
.annot{position:relative; height:10px}
.brk{position:absolute; left:calc(var(--c) * ${CW}px); width:calc(var(--w) * ${CW}px - 2px); margin-left:1px; top:1px; height:6px;
  border:1.5px solid currentColor; border-top:0; border-radius:0 0 3px 3px; opacity:.85}
.annot.up .brk{top:3px; border-top:1.5px solid currentColor; border-bottom:0; border-radius:3px 3px 0 0}
.alabs{position:relative; height:17px}
.alab{position:absolute; left:calc(var(--c) * ${CW}px); top:1px; white-space:nowrap; font:700 10.5px/14px Term,"Segoe UI Emoji",monospace;
  letter-spacing:.07em; text-transform:uppercase}
.big .alabs{height:20px} .big .alab{font-size:12.5px; line-height:17px}
.viz{padding:16px 18px 10px}
.viz svg{display:block}
.viz .ax{font:12px "Segoe UI",system-ui,sans-serif; fill:#A9B1D6}
.viz .ax.strong{font-weight:600}
.legend{display:flex; flex-wrap:wrap; gap:6px 20px; font-size:13px; color:#A9B1D6; margin:0 0 4px; align-items:center}
.legend .dot{display:inline-block; width:9px; height:9px; border-radius:50%; border:1.5px solid rgba(169,177,214,.75); margin-right:7px; vertical-align:-1px}
.legend .dot.on{background:#7DCFFF; border-color:#7DCFFF}
.legend .band{display:inline-block; width:22px; height:9px; border-radius:3px; background:rgba(125,207,255,.16); margin-right:7px; vertical-align:-1px}
.legend .sum{margin-left:auto; color:#6E738D}
.btn{display:flex; width:272px; box-sizing:border-box; justify-content:center; align-items:center; gap:10px; background:#7DCFFF; color:#16161E; border-radius:10px; padding:11px 22px 12px 18px;
  font:600 17px/22px "Segoe UI",system-ui,sans-serif; letter-spacing:.01em; box-shadow:0 1px 2px rgba(10,12,20,.18),0 4px 10px rgba(10,12,20,.16)}
`;
const page = (body, title) =>
  `<!doctype html><html lang="en"><head><meta charset="utf-8"><title>${esc(title)}</title><style>${css}</style></head><body>${body}</body></html>`;

// ───────────────────────────────────────────── Edge, headless, over the DevTools protocol
// One browser with its own profile in .work, on a port the OS picks, so it never meets the user's
// Edge or another agent's. Each figure is a clip of one element at twice the density, on a
// transparent background.
function findEdge() {
  const c = [process.env.EDGE, 'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe',
             'C:\\Program Files\\Microsoft\\Edge\\Application\\msedge.exe'].filter(Boolean);
  // newer installs keep the browser in a version folder beside a stub Application folder
  for (const base of ['C:\\Program Files (x86)\\Microsoft\\EdgeCore', 'C:\\Program Files (x86)\\Microsoft\\Edge\\Application']) {
    let v = []; try { v = fs.readdirSync(base).filter(d => /^\d+(\.\d+){3}$/.test(d)); } catch { }
    v.sort((a, b) => { const x = a.split('.').map(Number), y = b.split('.').map(Number); return x.reduce((r, n, i) => r || y[i] - n, 0); });
    c.push(...v.map(d => path.join(base, d, 'msedge.exe')));
  }
  const f = c.find(p => fs.existsSync(p)); if (!f) throw new Error('Microsoft Edge not found; set EDGE to msedge.exe');
  return f;
}
const sleep = ms => new Promise(r => setTimeout(r, ms));
async function launch() {
  const prof = path.join(WORK, 'edge-profile');
  fs.rmSync(prof, { recursive: true, force: true }); fs.mkdirSync(prof, { recursive: true });
  const exe = findEdge();
  const proc = spawn(exe, ['--headless=new', '--disable-gpu', '--hide-scrollbars', '--no-first-run', '--no-default-browser-check',
    '--disable-extensions', '--disable-component-update', '--disable-background-networking', '--disable-sync', '--force-color-profile=srgb',
    `--user-data-dir=${prof}`, '--remote-debugging-port=0', 'about:blank'], { stdio: 'ignore', windowsHide: true });
  const portFile = path.join(prof, 'DevToolsActivePort');
  let port, wsPath;
  for (let i = 0; i < 300 && !wsPath; i++) {
    await sleep(50);
    try { [port, wsPath] = fs.readFileSync(portFile, 'utf8').trim().split(/\r?\n/); } catch { }
  }
  if (!wsPath) { proc.kill(); throw new Error('Edge did not open its DevTools port'); }
  const ws = new WebSocket(`ws://127.0.0.1:${port}${wsPath}`);
  await new Promise((res, rej) => { ws.onopen = res; ws.onerror = () => rej(new Error('DevTools connection failed')); });
  let id = 0; const pending = new Map(), waiters = [];
  ws.onmessage = e => {
    const m = JSON.parse(e.data);
    if (m.id && pending.has(m.id)) { const p = pending.get(m.id); pending.delete(m.id); m.error ? p.rej(new Error(m.error.message)) : p.res(m.result); }
    else if (m.method) for (const w of [...waiters]) if (w.method === m.method && w.sid === m.sessionId) { waiters.splice(waiters.indexOf(w), 1); w.res(m.params); }
  };
  const send = (method, params = {}, sessionId) => { const i = ++id; ws.send(JSON.stringify({ id: i, method, params, sessionId }));
    return new Promise((res, rej) => pending.set(i, { res, rej })); };
  const once = (method, sid) => new Promise(res => waiters.push({ method, sid, res }));
  const { targetId } = await send('Target.createTarget', { url: 'about:blank' });
  const { sessionId: sid } = await send('Target.attachToTarget', { targetId, flatten: true });
  for (const d of ['Page', 'DOM', 'CSS']) await send(d + '.enable', {}, sid);
  const tab = {
    async open(url, w = 1600, h = 1000) {
      await send('Emulation.setDeviceMetricsOverride', { width: w, height: h, deviceScaleFactor: 2, mobile: false }, sid);
      await send('Emulation.setDefaultBackgroundColorOverride', { color: { r: 0, g: 0, b: 0, a: 0 } }, sid);
      const loaded = once('Page.loadEventFired', sid);
      await send('Page.navigate', { url }, sid); await loaded;
      await tab.eval('document.fonts.ready.then(() => true)');
    },
    async eval(expr) {
      const r = await send('Runtime.evaluate', { expression: expr, awaitPromise: true, returnByValue: true }, sid);
      if (r.exceptionDetails) throw new Error('page: ' + (r.exceptionDetails.exception && r.exceptionDetails.exception.description || r.exceptionDetails.text));
      return r.result.value;
    },
    async fonts(selector) {
      const { root } = await send('DOM.getDocument', { depth: 0 }, sid);
      const { nodeId } = await send('DOM.querySelector', { nodeId: root.nodeId, selector }, sid);
      return (await send('CSS.getPlatformFontsForNode', { nodeId }, sid)).fonts.map(f => f.familyName);
    },
    async shoot(rect) {
      const r = await send('Page.captureScreenshot', { format: 'png', clip: { ...rect, scale: 1 }, captureBeyondViewport: true, fromSurface: true }, sid);
      return Buffer.from(r.data, 'base64');
    },
  };
  async function close() {
    try { await Promise.race([send('Browser.close'), sleep(3000)]); } catch { }
    ws.close();
    for (let i = 0; i < 60 && proc.exitCode === null; i++) await sleep(50);
    if (proc.exitCode === null) spawnSync('taskkill', ['/PID', String(proc.pid), '/T', '/F'], { stdio: 'ignore' });
  }
  return { tab, close, exe };
}
// The first thing each run checks: the terminal font is JetBrains Mono's Nerd Font (cship's model
// line draws two of its icons) and the emoji come from Segoe UI Emoji, as in Windows Terminal.
async function checkFonts(tab) {
  const f = path.join(WORK, 'probe.html');
  fs.writeFileSync(f, page(`<div class="term" style="--cols:20"><span id="t">5h 7d \uF2DB \uF1C0</span><span id="e">🪵🌿🌳</span></div>`, 'probe'));
  await tab.open(pathToFileURL(f).href);
  const t = await tab.fonts('#t'), e = await tab.fonts('#e');
  if (!t.length || !t.every(n => /JetBrains/i.test(n))) throw new Error('the terminal text is not drawn in JetBrainsMono Nerd Font (install it): ' + t.join(', '));
  if (!e.includes('Segoe UI Emoji')) throw new Error('the emoji are not drawn in Segoe UI Emoji: ' + e.join(', '));
  return [...new Set([...t, ...e])];
}
// every label inside its device and clear of the others on its strip row; the figure on whole pixels
const LAYOUT = `(() => {
  const dev = document.querySelector('.dev'), d = dev && dev.getBoundingClientRect(), bad = [];
  for (const row of document.querySelectorAll('.alabs')) {
    const r = [...row.children].map(e => ({ t: e.textContent, b: e.getBoundingClientRect() })).sort((a, b) => a.b.left - b.b.left);
    r.forEach((x, i) => {
      if (x.b.left < d.left + 6 || x.b.right > d.right - 6) bad.push('outside its device: ' + x.t);
      if (i && x.b.left < r[i - 1].b.right + 10) bad.push('collides: ' + r[i - 1].t + ' / ' + x.t);
    });
  }
  const s = document.getElementById('shot').getBoundingClientRect();
  return { bad, rect: { x: s.left, y: s.top, width: s.width, height: s.height } };
})()`;
// an element of another page, with room around it for its shadow
const CLIP = (sel, css) => `(() => {
  const st = document.createElement('style'); st.textContent = ${JSON.stringify(css || '')}; document.head.appendChild(st);
  const b = document.querySelector(${JSON.stringify(sel)}).getBoundingClientRect();
  const x = Math.floor(b.left - 18), y = Math.floor(b.top + scrollY - 12);
  return { x, y, width: Math.ceil(b.right + 18) - x, height: Math.ceil(b.bottom + scrollY + 26) - y };
})()`;

// ───────────────────────────────────────────── run
async function main() {
  const args = process.argv.slice(2), check = args.includes('--check'), only = args.filter(a => !a.startsWith('--'));
  const outDir = check ? path.join(WORK, 'check') : __dirname;
  fs.mkdirSync(WORK, { recursive: true }); fs.mkdirSync(outDir, { recursive: true });
  const todo = SCENES.filter(s => !only.length || only.includes(s.id));
  if (only.length && todo.length !== only.length) throw new Error('unknown figure: ' + only.filter(o => !SCENES.some(s => s.id === o)).join(', '));
  const { tab, close, exe } = await launch();
  const report = [];
  try {
    console.log(`edge: ${exe}\nfonts: ${(await checkFonts(tab)).join(', ')}`);
    for (const s of todo) {
      USED.clear();
      const built = s.build(), src = [...USED];
      let rect;
      if (built.capture) {
        const c = built.capture;
        await tab.open(pathToFileURL(c.file).href, c.width, 1000);
        rect = await tab.eval(CLIP(c.selector, c.css));
        src.push(path.relative(ROOT, c.file).replace(/\\/g, '/'));
      } else {
        const f = path.join(WORK, s.id + '.html');
        fs.writeFileSync(f, page(`<div class="shot${built.shotClass ? ' ' + built.shotClass : ''}" id="shot">${built.html}</div>`, s.id));
        await tab.open(pathToFileURL(f).href);
        const r = await tab.eval(LAYOUT);
        if (r.bad.length) throw new Error(`${s.id}: ${r.bad.join('; ')}`);
        rect = r.rect;
      }
      if ([rect.x, rect.y, rect.width, rect.height].some(v => v !== Math.round(v))) throw new Error(`${s.id}: not on whole pixels ${JSON.stringify(rect)}`);
      const png = await tab.shoot(rect);
      fs.writeFileSync(path.join(outDir, s.id + '.png'), png);
      report.push({ id: s.id, shows: s.shows, src, from: built.from, w: rect.width, h: rect.height, bytes: png.length, cols: built.widest });
    }
  } finally { await close(); }
  for (const r of report)
    console.log(`${r.id.padEnd(15)} ${String(r.w).padStart(5)} × ${String(r.h).padEnd(4)} CSS px  ${(r.bytes / 1024).toFixed(0).padStart(4)} KB`
              + (r.cols ? `  widest line ${r.cols} columns` : '') + `  ${r.src.join(', ')}`);
  console.log(`${report.length} figures, ${(report.reduce((n, r) => n + r.bytes, 0) / 1024).toFixed(0)} KB`);
  if (check) {
    let same = 0;
    for (const r of report) {
      const a = fs.readFileSync(path.join(outDir, r.id + '.png')), b = path.join(__dirname, r.id + '.png');
      const ok = fs.existsSync(b) && a.equals(fs.readFileSync(b)); same += ok;
      if (!ok) console.log('DIFFERS', r.id);
    }
    console.log(`${same}/${report.length} identical to docs/assets`);
    if (same !== report.length) process.exitCode = 1;
  } else if (!only.length) index(report);
  if (!checkPages()) process.exitCode = 1;
}
// Every <img> of a figure in the Markdown pages: the figure exists, it has alt text, and its width
// attribute is its CSS width — half the PNG's — so GitHub draws it at its own size, or narrower
// where the column is.
function checkPages() {
  const pages = ['README.md'], walk = d => fs.readdirSync(d, { withFileTypes: true }).forEach(e => {
    const p = path.join(d, e.name);
    if (e.isDirectory()) walk(p); else if (e.name.endsWith('.md')) pages.push(path.relative(ROOT, p));
  });
  walk(path.join(ROOT, 'docs'));
  const bad = []; let n = 0;
  for (const pg of pages) {
    const text = fs.readFileSync(path.join(ROOT, pg), 'utf8');
    for (const m of text.matchAll(/<img\s[^>]*>/g)) {
      const tag = m[0], src = (tag.match(/\bsrc="([^"]+)"/) || [])[1] || '';
      if (!/assets\/[\w-]+\.png$/.test(src)) continue;
      n++;
      const file = path.resolve(ROOT, path.dirname(pg), src);
      if (!fs.existsSync(file)) { bad.push(`${pg}: ${src} does not exist`); continue; }
      const png = fs.readFileSync(file), w = png.readUInt32BE(16) / 2;
      const alt = (tag.match(/\balt="([^"]*)"/) || [])[1], width = Number((tag.match(/\bwidth="(\d+)"/) || [])[1]);
      if (!alt || alt.trim().length < 12) bad.push(`${pg}: ${src} has no alt text worth the name`);
      if (width !== w) bad.push(`${pg}: ${src} has width="${width || ''}", the figure is ${w}`);
    }
  }
  console.log(bad.length ? 'figure references:\n  ' + bad.join('\n  ') : `figure references: ${n} in ${pages.length} pages, every one with alt text and its own width`);
  return !bad.length;
}
// docs/assets/README.md: what each figure shows, what it is drawn from, and its size
function index(report) {
  const rows = report.map(r => `| [${r.id}.png](${r.id}.png) | ${r.shows} | ${[...r.src.map(s => /^[\w-]+\.w\d+$/.test(s) ? `\`${s}\``
    : `[${path.basename(s)}](${path.relative(__dirname, path.join(ROOT, s)).replace(/\\/g, '/')})`), ...(r.from ? [r.from] : [])].join(', ')} | ${r.w} × ${r.h} |`);
  fs.writeFileSync(path.join(__dirname, 'README.md'), `<sub>[StatusAI](../../README.md) › [Documentation](../README.md)</sub>

# Figures

Every image here is generated. [figures.gen.js](figures.gen.js) draws them from the render tests'
expected output — the exact bytes the binary draws for each fixture, in
[tests/expected](../../tests/expected) — in the [house style](../design/house-style/README.md), and
Microsoft Edge, headless, turns each into a PNG at twice its size. Regenerate them all with

\`\`\`bash
node docs/assets/figures.gen.js
\`\`\`

after a change of output is recorded with \`Test-Renders.ps1 -Update\`; \`--check\` draws them into
\`.work/figures/check\` and compares them with these, byte for byte. It needs Node, Edge and
JetBrainsMono Nerd Font, and refuses to run on a colour outside the palette, a glyph whose width it
does not know, or a callout that does not land.

A render is named \`<case>.w<width>\`: the case in [tests/cases.json](../../tests/cases.json), drawn
at that many columns. Sizes are CSS pixels; each PNG has twice as many.

| figure | shows | drawn from | size |
|---|---|---|---|
${rows.join('\n')}

The generator rewrites this page on every full run: edit the generator, not the page.
`);
}
module.exports = { toCells, span, crop, width, colText, launch, playFetches, RENDER_TIMES };
if (require.main === module) main().catch(e => { console.error(e.message || e); process.exit(1); });
