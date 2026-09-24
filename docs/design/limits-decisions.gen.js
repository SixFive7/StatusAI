// Generates limits-decisions.html from limits-decisions.data.json. Node only, no dependencies:
//
//   node limits-decisions.gen.js limits-decisions.html
//
// House style (house-style/README.md): every terminal character is a fixed 1ch cell, every colour
// is a literal from Program.cs, before/after panes share one width, callouts sit on measured
// columns, and the terminal and the charts are devices that stay dark in both themes.
//
// The mockup rows come from three sources, and the generator refuses to run if they disagree:
//  * rows of the build deployed when the questions came up are the exact bytes it drew through
//    CSHIP_OFFLINE (data.renders), converted cell by cell;
//  * rows of the builds deployed after Q2 and Q3 (data.asbuilt) and after Q4 and Q6
//    (data.asbuilt2) are captured the same way;
//  * rows for options that were never built come from port(), a line-for-line port of the three
//    builds' RenderRows / Bar / Hm / BuildAccount / Compose / WithBreakdown. Before anything is
//    written, port() re-renders every captured case of all three builds from its inputs and must
//    reproduce the binary's bytes exactly.
// The page names each build by the local time it was deployed, its 'deployed' in the data.
const fs = require('fs');
const path = require('path');
const D = JSON.parse(fs.readFileSync(path.join(__dirname, 'limits-decisions.data.json'), 'utf8'));
// '2026-09-24 01:32' as a label, '24 Sep 01:32', in a sentence, '24 September at 01:32', and the time
const MONTHS = ['January', 'February', 'March', 'April', 'May', 'June', 'July', 'August', 'September',
                'October', 'November', 'December'];
function build(deployed) {
  const d = +deployed.slice(8, 10), m = MONTHS[+deployed.slice(5, 7) - 1], t = deployed.slice(11, 16);
  return { tag: `${d} ${m.slice(0, 3)} ${t}`, when: `${d} ${m} at ${t}`, time: t };
}
const B0 = build(D.renders.meta.deployed), B1 = build(D.asbuilt.deployed), B2 = build(D.asbuilt2.deployed);
const TERM = 141;             // TermWidth() with CSHIP_WIDTH unset
const COLS = TERM - 4;        // Compose() never lets a line past term - 4

// ---- port of the drawing code (Program.cs)
const RST = '\x1b[0m', DIM = '\x1b[38;2;110;115;141m', RED = '\x1b[1;38;2;247;118;142m',
      AMB = '\x1b[38;2;224;175;104m', CYA = '\x1b[38;2;125;207;255m', GRN = '\x1b[38;2;166;227;161m',
      LGR = '\x1b[38;2;198;246;193m', TXT = '\x1b[38;2;169;177;214m',
      FOREST = '\x1b[38;2;40;164;40m';     // Program.cs Forest, from Q2 on
const NODATA = '—';
// C# Math.Round(double) rounds half to even
const roundEven = x => { const f = Math.floor(x), r = x - f; return r > 0.5 ? f + 1 : r < 0.5 ? f : (f % 2 === 0 ? f : f + 1); };
function Hm(hours) {
  if (hours < 0) hours = 0;
  let h = Math.trunc(hours), m = roundEven((hours - h) * 60);
  if (m === 60) { h++; m = 0; }
  if (h >= 24) return `${Math.trunc(h / 24)}d${String(h % 24).padStart(2, '0')}h`;
  return h > 0 ? `${h}h${String(m).padStart(2, '0')}m` : `${m}m`;
}
const BarFill = p => Math.max(0, Math.ceil(p / 10));
const Zone = i => i >= 10 ? RED : i >= 8 ? AMB : CYA;
// muted (Q6): every cell dim, with the same glyphs, for a segment that is shown but does not apply
function Bar(pct, padTo = 0, cap = 15, muted = false) {
  const fill = BarFill(pct), len = Math.min(cap, Math.max(10, fill));
  let s = '', cur = '';
  for (let i = 1; i <= len; i++) {
    const on = i <= fill, col = on && !muted ? Zone(i) : DIM;
    if (col !== cur) { s += col; cur = col; }
    s += on ? (i >= 11 ? '✗' : '●') : '○';
  }
  s += RST;
  if (padTo > len) s += ' '.repeat(padTo - len);
  return s;
}
const PctColor = p => p >= 90 ? RED : p >= 70 ? AMB : CYA;
const SevColor = s => s === 'critical' ? RED : s === 'warning' ? AMB : '';
const WINDOW_H = { '5h': 5, '7d': 168 };        // anything else is a weekly scoped row
const windowLen = label => WINDOW_H[label] || 168;

// rows: { label, pct, hrs, hasReset, rate, gated, sev, trend }. trend false is a meter that no
// history series follows, which the build deployed after Q2 and Q3 drew with → —. The proposals
// add:
//   mode: 'c' (proposed) | 'c-raw' (no 12 h rule) | 'c-old' (old 0.5 %/h test) | 'lookback' (r24 drives → and ⇢)
//   r24, projLabel ('⇢' | '⇢avg')
// opt.afterReset (5h only, Q2): 'never' | 'first' | 'dash'
// opt.forest: the → colour of Q2, when the row's own reset comes before 100%
// opt.muted: the ⇢ segment of a row at 100%, drawn wholly dim (Q6)
// opt.maxed (Q6's options, never built): 'cap' ⇢ at 100% | 'hide' the ⇢ segment, left blank
function renderRows(rows, leftCount, opt = {}) {
  const term = opt.term || TERM;
  const d = rows.map(r => {
    const now = Math.min(Math.max(r.pct, 0), 100);
    const rate = Math.min(r.mode === 'lookback' ? r.r24 : r.rate, 40);
    const trend = r.trend !== false;
    const burning = trend && !r.gated && rate > 0.5;
    let proj = burning && r.hasReset ? Math.min(roundEven(now + rate * r.hrs), 300) : now;
    if (r.mode === 'c' || r.mode === 'c-raw' || r.mode === 'c-old') {
      const el = windowLen(r.label) - r.hrs, avg = el > 0 ? now / el : 0;
      const ok = r.mode === 'c-old' ? avg > 0.5 : avg * r.hrs >= 1;
      const minEl = r.mode === 'c' ? 12 : 0;
      proj = r.hasReset && ok && el >= minEl ? Math.min(roundEven(now + avg * r.hrs), 300) : now;
    }
    let hide = false;
    if (opt.maxed && now >= 100) { if (opt.maxed === 'cap') proj = Math.min(proj, 100); else { proj = now; hide = true; } }
    let to100, overshoot = false, forest = false, first = false;
    if (!trend) to100 = NODATA;
    else if (r.gated) to100 = 'early';
    else if (burning && now < 100) {
      const h = (100 - now) / rate;
      overshoot = r.hasReset && h < r.hrs;
      forest = !!opt.forest && r.hasReset && !overshoot;
      to100 = Hm(h);
      if (opt.afterReset && r.label === '5h' && r.hasReset && !overshoot) {
        if (opt.afterReset === 'never') to100 = 'never';
        else if (opt.afterReset === 'dash') to100 = NODATA;
        else if (opt.afterReset === 'first') { to100 = 'first'; first = true; }
      }
    } else to100 = now >= 100 ? 'maxed' : 'never';
    return { label: r.label, now, proj, reset: r.hasReset ? Hm(r.hrs) : NODATA, to100, overshoot, forest, sev: r.sev,
             pl: r.projLabel || '⇢', first, hide };
  });
  const z = () => [0, 0];
  const wLbl = z(), wNow = z(), wReset = z(), wTo = z(), wProj = z(), wPl = [1, 1];
  d.forEach((x, i) => {
    const c = i < leftCount ? 0 : 1;
    wLbl[c] = Math.max(wLbl[c], x.label.length); wNow[c] = Math.max(wNow[c], String(x.now).length);
    wReset[c] = Math.max(wReset[c], x.reset.length); wTo[c] = Math.max(wTo[c], x.to100.length);
    wProj[c] = Math.max(wProj[c], String(x.proj).length); wPl[c] = Math.max(wPl[c], x.pl.length);
  });
  const wNowBar = [10, 10];
  d.forEach((x, i) => { const c = i < leftCount ? 0 : 1; wNowBar[c] = Math.max(wNowBar[c], Math.min(10, BarFill(x.now))); });
  const W = w => Math.max(w[0], w[1]);
  // wPl is 1 in the product, so its term is 0 there; a longer ⇢ label is charged like any other column
  const fixedPart = 4 + 1 + 2 + 15 * 2 + wLbl[0] + wLbl[1]
                  + 2 * (W(wNow) + W(wReset) + W(wTo) + W(wProj) + W(wNowBar)) + 2 * (W(wPl) - 1);
  const capBar = Math.min(Math.max(Math.trunc((term - 2 - fixedPart) / 2), 10), 30);
  const wBar = [10, 10];
  d.forEach((x, i) => { const c = i < leftCount ? 0 : 1; wBar[c] = Math.max(wBar[c], Math.min(capBar, Math.max(BarFill(x.now), BarFill(x.proj)))); });
  const lines = d.map((x, i) => {
    const c = i < leftCount ? 0 : 1, lbl = x.label.padEnd(wLbl[c]), sevc = SevColor(x.sev);
    let s = sevc ? sevc + lbl + RST : lbl;
    s += ` ${Bar(x.now, wNowBar[c], 10)} ${PctColor(x.now)}${String(x.now).padStart(wNow[c])}%${RST}`;
    s += ` ${GRN}↻${RST} ${LGR}${x.reset.padEnd(wReset[c])}${RST}`;
    s += x.first ? ` ${GRN}↻${RST}${DIM} ${x.to100.padEnd(wTo[c])}${RST}`
                 : ` ${x.overshoot ? RED : x.forest ? FOREST : DIM}→ ${x.to100.padEnd(wTo[c])}${RST}`;
    const blocked = !!opt.muted && x.now >= 100;
    const seg = ` ${DIM}${x.pl.padEnd(wPl[c])}${RST} ${Bar(x.proj, wBar[c], capBar, blocked)} ${blocked || x.proj <= 100 ? DIM : RED}${String(x.proj).padStart(wProj[c])}%${RST}`;
    s += x.hide ? ' '.repeat(vis(seg)) : seg;
    return s;
  });
  return { lines, capBar, d };
}
const account = (email, plan, extra = '') => `👤 ${TXT}${email}${RST}` + (plan ? ` ${DIM}· ${plan}${RST}` : '') + extra;
function vis(s) { let n = 0; for (let i = 0; i < s.length; i++) { if (s[i] === '\x1b') { while (i < s.length && s[i] !== 'm') i++; continue; } n++; } return n; }
// WithBreakdown() (Q3): the product breakdown after the account, in whole entries or not at all:
// every entry while they fit, then without the 0% ones, then none.
function withBreakdown(acct, bd, room) {
  if (!bd || !bd.length) return acct;
  const sep = ` ${DIM}·${RST} `;
  for (const zeros of [true, false]) {
    const shown = bd.filter(e => zeros || e[1] !== '0');
    if (!shown.length) continue;
    const tail = sep + shown.map(e => `${TXT}${e[0]} ${e[1]}%${RST}`).join(sep);
    if (vis(acct) + 1 + vis(tail) <= room) return acct + tail;
  }
  return acct;
}
// Compose(): two columns when they fit, each block line prefixed "\x1b[0m " by the host insert.
// opt.rightExtra (the Q3 b mockup) and opt.asBuilt (the two builds deployed after the questions):
// meters past the third go under the scoped row, in the right column; opt.asBuilt also adds the
// breakdown in the room that is left.
function compose(rows, acct, opt = {}) {
  const term = opt.term || TERM, gap = '  ', extra = opt.asBuilt || opt.rightExtra;
  if (rows.length >= 2) {
    const rowW = vis(rows[0]);
    let rightW = Math.max(rows.length > 2 ? vis(rows[2]) : 0, acct.length > 0 ? vis(acct) + 1 : 0);
    if (extra) for (let i = 3; i < rows.length; i++) rightW = Math.max(rightW, vis(rows[i]));
    if (1 + rowW + 2 + rightW <= term - 4) {
      const acctLine = opt.asBuilt ? withBreakdown(acct, opt.bd, term - 4 - (1 + rowW + 2)) : acct;
      const b = [rows[0] + (acctLine ? gap + acctLine : ''), rows[1] + (rows.length > 2 ? gap + rows[2] : '')];
      for (let i = 3; i < rows.length; i++) b.push(extra ? ' '.repeat(rowW) + gap + rows[i] : rows[i]);
      return b.map(l => RST + ' ' + l);
    }
  }
  throw new Error('stacked layout not needed on this page');
}
// one composition: rows (5h, 7d, scoped...) and the account, made into the block lines exactly as the binary emits them
function port(rows, email, plan = 'Max 20', opt = {}) {
  const r = renderRows(rows, 2, opt);
  return { lines: compose(r.lines, account(email, plan, opt.acctExtra || ''), opt), capBar: r.capBar, d: r.d };
}

// ---- self-check: port() must reproduce every capture
const checks = [];
function check(name, got, want) {
  const same = got.length === want.length && got.every((l, i) => l === want[i]);
  checks.push([name, same]);
  if (!same) {
    console.error('PORT MISMATCH', name); want.forEach((l, i) => { console.error(' exe :', JSON.stringify(l)); console.error(' port:', JSON.stringify(got[i])); });
    process.exit(1);
  }
}
for (const [name, cse] of Object.entries(D.renders.cases)) check(`${B0.tag} ${name}`, port(cse.rows, cse.email, cse.plan).lines, cse.lines);
// data.asbuilt: the limit rows and the account line; its ⚠ credit row is text the port does not draw
const abOpt = c => ({ asBuilt: true, forest: true, bd: c.bd });
for (const [name, cse] of Object.entries(D.asbuilt.cases))
  check(`${B1.tag} ${name}`, port(cse.rows, cse.fixture.email, cse.plan, abOpt(cse)).lines, cse.lines.filter(l => !l.includes('⚠')));
// data.asbuilt2: three known meters (the fixture's others are flagged, not drawn), and Q6's grey
const ab2Opt = c => ({ asBuilt: true, forest: true, muted: true, bd: c.bd });
for (const [name, cse] of Object.entries(D.asbuilt2.cases))
  check(`${B2.tag} ${name}`, port(cse.rows, cse.fixture.email, cse.plan, ab2Opt(cse)).lines, cse.lines.filter(l => !l.includes('⚠')));

// ---- ANSI to 1ch cells
const esc = s => String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
const CLS = { '125;207;255': 'cy', '224;175;104': 'or', '247;118;142': 'rd', '110;115;141': 'dm', '166;227;161': 'gr',
              '198;246;193': 'lg', '169;177;214': 'tx', '180;190;254': 'lv', '40;164;40': 'fo' };
const WIDE = new Set([0x1F464, 0x1F4B3]);          // 👤 💳: East-Asian Wide, two columns
function toCells(s) {
  let fg = null, bold = false; const out = [];
  for (let i = 0; i < s.length;) {
    if (s[i] === '\x1b') {
      const j = s.indexOf('m', i), p = s.slice(i + 2, j).split(';');
      for (let k = 0; k < p.length; k++) {
        if (p[k] === '0' || p[k] === '') { fg = null; bold = false; }
        else if (p[k] === '1') bold = true;
        else if (p[k] === '38' && p[k + 1] === '2') { fg = `${p[k + 2]};${p[k + 3]};${p[k + 4]}`; k += 4;
          if (!CLS[fg]) throw new Error('colour not in the Program.cs palette: ' + fg); }
        else throw new Error('unhandled SGR ' + p.join(';'));
      }
      i = j + 1; continue;
    }
    const cp = s.codePointAt(i), ch = String.fromCodePoint(cp); i += ch.length;
    out.push({ ch, cls: fg ? CLS[fg] : 'df', bold, w: WIDE.has(cp) ? 2 : 1 });
  }
  return out;
}
// the plain text with exactly one UTF-16 unit per column (a wide cell becomes two private-use
// placeholders), so a regex match index IS a column
const colText = cells => cells.map(c => c.w === 2 ? '' : c.ch.length === 1 ? c.ch : '').join('');
const width = cells => cells.reduce((n, c) => n + c.w, 0);
function span(cells, re, from = 0) {
  const t = colText(cells), g = new RegExp(re.source, 'g'); g.lastIndex = from;
  const m = g.exec(t); if (!m) throw new Error('callout target not found: ' + re + ' in ' + t);
  return { col: m.index, len: m[0].length };
}
const cellHtml = cells => cells.map(c => `<i class="g ${c.cls}${c.bold ? ' b' : ''}${c.w === 2 ? ' w2' : ''}">${esc(c.ch)}</i>`).join('');

// ---- panes and callouts
const LINES = [];      // every rendered status line, for the cell-count report
function lineHtml(cells, gut, tag) {
  LINES.push({ tag, cells: cells.length, cols: width(cells) });
  return `<div class="ln">${gut !== undefined ? `<span class="gut">${esc(gut)}</span>` : ''}${cellHtml(cells)}</div>`;
}
// marks: { col, len, text, tone: 'chg' | 'same' | 'am', row: 0|1 }
function annot(marks, off = 0) {
  const b = marks.map(m => `<span class="brk ${m.tone || ''}" style="--c:${m.col + off};--w:${m.len}"></span>`).join('');
  const rows = [0, 1].map(r => marks.filter(m => (m.row || 0) === r));
  const l = rows.filter(rs => rs.length).map(rs => `<div class="alabs">${rs.map(m =>
    `<span class="alab" style="--c:${m.col + off}"><span class="${m.tone || ''}">${esc(m.text)}</span></span>`).join('')}</div>`).join('');
  return `<div class="annot">${b}</div>${l}`;
}
// lines: [{ cells, gut, marks }]
const fit = (...sets) => Math.max(...sets.flat().map(l => width(l.cells)));
function pane({ title, tag, tagCls, note, lines, gutW = 0, cols, id }) {
  if (cols === undefined) cols = fit(lines);
  const body = lines.map(L => lineHtml(L.cells, gutW ? (L.gut || '') : undefined, title) + (L.marks ? annot(L.marks, gutW) : '')).join('\n');
  return `<div class="pane"${id ? ` id="${id}"` : ''}>
  ${title || tag || note ? `<div class="phead">${title ? `<span class="ptitle">${title}</span>` : ''}${tag ? `<span class="tag ${tagCls || ''}">${esc(tag)}</span>` : ''}${note ? `<span class="pnote">${note}</span>` : ''}</div>` : ''}
  <div class="term"><div class="term-inner" style="--cols:${cols + gutW};--gut:${gutW}">${body}</div></div></div>`;
}
const exeCells = key => { const c = D.renders.cases[key]; if (!c) throw new Error('no capture ' + key); return c.lines.map(toCells); };

module.exports = { port, renderRows, toCells, span, Hm, roundEven };   // for ad-hoc checks
if (require.main !== module) return;

// ---- page helpers
const M = D.moments, F = D.facts, EMAIL = 'someone@example.com';
const P0 = x => `${Math.round(x * 100)}%`;
const f1 = x => Number(x).toFixed(1);
const sg = x => (x > 0 ? '+' : x < 0 ? '-' : '') + Math.abs(x).toFixed(1);
const asC = (rows, mode = 'c', extra = {}) => rows.map(r => r.label === '5h' ? r : { ...r, mode, ...extra });
const asLook = m => m.rows.map(r => r.label === '7d' ? { ...r, mode: 'lookback', r24: m.w.r24 }
                                  : r.label === 'Fable' ? { ...r, mode: 'lookback', r24: m.f.r24 } : r);
const portCells = (rows, email = EMAIL, opt = {}) => port(rows, email, 'Max 20', opt).lines.map(toCells);
const exe = stamp => exeCells('w ' + stamp);
const PROJ = /⇢\S* [●○✗]+ *\d+%/, ARROW = /→ \S+/;
const f7 = cells => ({ a: span(cells, ARROW), p: span(cells, PROJ) });
function fS(cells, label = 'Fable') { const from = colText(cells).indexOf(label); return { a: span(cells, ARROW, from), p: span(cells, PROJ, from), at: from }; }
const mk = (s, text, tone, row = 0) => ({ col: s.col, len: s.len, text, tone, row });
const hhmm = stamp => stamp.slice(6, 11);
const day = { '09-19': 'Sat', '09-20': 'Sun', '09-21': 'Mon', '09-22': 'Tue', '09-23': 'Wed' };
const when = stamp => stamp === '09-23 19:56:54' ? 'Wed 19:57' : `${day[stamp.slice(0, 5)]} ${hhmm(stamp)}`;
const widthNote = (a, b) => a === b ? `unchanged at ${a} columns` : `from ${a} to ${b} columns (${b - a > 0 ? '+' : '-'}${Math.abs(b - a)})`;

// ---- charts (SVG, dark device panel, Program.cs colours)
const C = { truth: '#C6F6C1', today: '#F7768E', c: '#7DCFFF', look: '#E0AF68', none: '#6E738D',
            ink: '#A9B1D6', mute: '#6E738D', ground: '#16161E' };
const decode = rle => rle.flatMap(([v, n]) => Array(n).fill(v));
const S = D.series, T0 = S.t0, STEP = S.step, WS = D.window.start, SW = D.window.switch;
const LOC = t => new Date((t + 7200) * 1000).toISOString().slice(0, 16).replace('T', ' ');   // CEST
const dayName = t => ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'][new Date((t + 7200) * 1000).getUTCDay()];
const fx = n => Math.round(n * 10) / 10;
function stepPath(rle, X, Y) {
  let t = T0, d = '';
  rle.forEach(([v, n], i) => { d += (i === 0 ? `M${fx(X(t))},${fx(Y(v))}` : `V${fx(Y(v))}`); t += n * STEP; d += `H${fx(X(t))}`; });
  return d;
}
const MOMENTS = [['09-19 22:00:54', 'first whole point of the week'], ['09-21 12:00:54', 'midday lull'],
                 ['09-21 17:00:54', 'Monday burst'], ['09-22 10:00:54', 'idle morning'],
                 ['09-22 16:00:54', 'busiest hour of the week'], ['09-23 19:56:54', 'the screenshot']];
const stampT = stamp => { const [md, t] = stamp.split(' '); return Date.UTC(2026, 8, +md.slice(3), ...t.split(':').map(Number)) / 1000 - 7200; };
function weekChart() {
  const Wd = 1100, H = 440, m = { l: 44, r: 150, t: 44, b: 38 };
  const x0 = m.l, x1 = Wd - m.r, y0 = H - m.b, y1 = m.t, tEnd = SW + 180;
  const X = t => x0 + (t - WS) / (tEnd - WS) * (x1 - x0), Y = v => y0 - v / 300 * (y0 - y1);
  let g = '';
  for (let v = 0; v <= 300; v += 50) {
    g += `<line x1="${x0}" x2="${x1}" y1="${fx(Y(v))}" y2="${fx(Y(v))}" stroke="${C.mute}" stroke-opacity="${v === 0 ? .9 : .22}" stroke-width="1"/>`;
    g += `<text x="${x0 - 8}" y="${fx(Y(v)) + 4}" text-anchor="end" class="ax">${v}${v === 300 ? '%' : ''}</text>`;
  }
  // the limit
  g += `<line x1="${x0}" x2="${x1}" y1="${fx(Y(100))}" y2="${fx(Y(100))}" stroke="${C.ink}" stroke-opacity=".75" stroke-width="1.5"/>`;
  g += `<text x="${x0 + 6}" y="${fx(Y(100)) - 6}" class="ax strong">100%: the limit</text>`;
  // days
  for (let t = WS + 19 * 3600; t < tEnd; t += 86400)
    g += `<line x1="${fx(X(t))}" x2="${fx(X(t))}" y1="${y1}" y2="${y0}" stroke="${C.mute}" stroke-opacity=".16"/>`;
  const dayMarks = [[WS, WS + 19 * 3600, 'Sat 19'], ...[0, 1, 2].map(i => [WS + 19 * 3600 + i * 86400, WS + 19 * 3600 + (i + 1) * 86400, ['Sun 20', 'Mon 21', 'Tue 22'][i]]), [WS + 19 * 3600 + 3 * 86400, tEnd, 'Wed 23']];
  for (const [a, b, lbl] of dayMarks) g += `<text x="${fx((X(a) + X(b)) / 2)}" y="${y0 + 22}" text-anchor="middle" class="ax">${lbl}</text>`;
  // events: the reset that opened the window, the account switch that ends the data
  g += `<line x1="${x0}" x2="${x0}" y1="${y1 - 26}" y2="${y0}" stroke="${C.ink}" stroke-width="1.5"/>`;
  g += `<text x="${x0 + 6}" y="${y1 - 16}" class="ax strong">↻ Sat 05:00, the weekly window opens</text>`;
  g += `<line x1="${fx(X(SW))}" x2="${fx(X(SW))}" y1="${y1 - 26}" y2="${y0}" stroke="${C.ink}" stroke-width="1.5"/>`;
  g += `<text x="${fx(X(SW)) - 6}" y="${y1 - 16}" text-anchor="end" class="ax strong">Wed 20:03, account switched; this meter is not observed after it</text>`;
  // series: context first, the story on top
  const series = [['today', C.today, 1.5], ['lookback', C.look, 2], ['c', C.c, 2], ['api', C.truth, 2]];
  for (const [k, col, w] of series)
    g += `<path d="${stepPath(S[k], X, Y)}" fill="none" stroke="${col}" stroke-width="${w}" stroke-linejoin="round" stroke-linecap="round"/>`;
  // moments: numbered, on the weekly % line
  MOMENTS.forEach(([stamp], i) => {
    const t = stampT(stamp), k = Math.round((t - T0) / STEP), v = decode(S.api)[k];
    g += `<line x1="${fx(X(t))}" x2="${fx(X(t))}" y1="${y1 - 2}" y2="${fx(Y(v))}" stroke="${C.ink}" stroke-opacity=".35" stroke-width="1"/>`;
    g += `<circle cx="${fx(X(t))}" cy="${fx(Y(v))}" r="4.5" fill="${C.truth}" stroke="${C.ground}" stroke-width="2"/>`;
    g += `<circle cx="${fx(X(t))}" cy="${y1 - 2}" r="8" fill="${C.ink}"/><text x="${fx(X(t))}" y="${y1 + 2}" text-anchor="middle" class="num">${i + 1}</text>`;
  });
  // end labels with leaders
  const last = k => decode(S[k]).at(-1);
  const ends = [['⇢ today', 'today'], ['⇢ 24 h lookback', 'lookback'], ['⇢ c', 'c'], ['weekly %', 'api']].map(([n, k]) => ({ n, k, v: last(k), y: Y(last(k)) }))
    .sort((a, b) => a.y - b.y);
  for (let i = 1; i < ends.length; i++) if (ends[i].y - ends[i - 1].y < 16) ends[i].y = ends[i - 1].y + 16;
  const xe = X(T0 + S.n * STEP);
  for (const e of ends) {
    g += `<path d="M${fx(xe)},${fx(Y(e.v))} L${fx(x1 + 10)},${fx(e.y)} H${fx(x1 + 16)}" fill="none" stroke="${C.mute}" stroke-width="1"/>`;
    g += `<text x="${x1 + 20}" y="${fx(e.y) + 4}" class="ax"><tspan class="strong">${e.v}%</tspan> ${esc(e.n)}</text>`;
  }
  const hover = `<line id="xh" x1="0" x2="0" y1="${y1}" y2="${y0}" stroke="${C.ink}" stroke-width="1" visibility="hidden"/>`;
  const svg = `<svg id="week" viewBox="0 0 ${Wd} ${H}" role="img" aria-labelledby="week-t week-d">
<title id="week-t">Weekly usage and three forecasts of it, Sat 19 Sep 05:00 to Wed 23 Sep 20:03</title>
<desc id="week-d">The weekly percentage rises from 0 to 90. The current forecast (⇢ today) swings between the current value and 300 percent many times a day; option c rises smoothly to 136 percent; a 24-hour lookback sits between them. Values are in the table view below.</desc>
${g}${hover}<rect id="hit" x="${x0}" y="${y1}" width="${x1 - x0}" height="${y0 - y1}" fill="transparent"/></svg>`;
  const geo = { x0, x1, WS, tEnd, T0, STEP, n: S.n, Wd };
  const blob = JSON.stringify({ geo, api: S.api, today: S.today, c: S.c, lookback: S.lookback });
  return { svg, blob };
}
function barsChart({ groups, series, max, unit, H = 250, id }) {
  const Wd = 560, m = { l: 36, r: 12, t: 16, b: 34 }, x0 = m.l, x1 = Wd - m.r, y0 = H - m.b, y1 = m.t;
  const Y = v => y0 - v / max * (y0 - y1), gw = (x1 - x0) / groups.length, bw = 22, gap = 2;
  let g = '';
  for (let v = 0; v <= max; v += max / 5) {
    g += `<line x1="${x0}" x2="${x1}" y1="${fx(Y(v))}" y2="${fx(Y(v))}" stroke="${C.mute}" stroke-opacity="${v === 0 ? .9 : .22}"/>`;
    g += `<text x="${x0 - 6}" y="${fx(Y(v)) + 4}" text-anchor="end" class="ax">${v}</text>`;
  }
  groups.forEach((grp, gi) => {
    const cx = x0 + gw * (gi + .5), tot = series.length * bw + (series.length - 1) * gap;
    series.forEach((s, si) => {
      const v = grp.values[si], x = cx - tot / 2 + si * (bw + gap), y = Y(v), h = y0 - y, r = Math.min(4, h);
      g += `<path d="M${fx(x)},${y0} V${fx(y + r)} Q${fx(x)},${fx(y)} ${fx(x + r)},${fx(y)} H${fx(x + bw - r)} Q${fx(x + bw)},${fx(y)} ${fx(x + bw)},${fx(y + r)} V${y0} Z" fill="${s.col}"><title>${esc(s.name)}, ${esc(grp.name)}: ${v}${unit}</title></path>`;
      g += `<text x="${fx(x + bw / 2)}" y="${fx(y) - 5}" text-anchor="middle" class="ax val sm">${f1(v)}</text>`;
    });
    g += `<text x="${fx(cx)}" y="${y0 + 20}" text-anchor="middle" class="ax">${esc(grp.name)}</text>`;
  });
  return `<svg${id ? ` id="${id}"` : ''} viewBox="0 0 ${Wd} ${H}" role="img" aria-label="${esc(series.map(s => s.name).join(', '))} by ${esc(groups.map(g => g.name).join(', '))}">${g}</svg>`;
}
const legend = items => `<div class="legend">${items.map(([name, col, kind]) =>
  `<span><i class="${kind === 'bar' ? 'sw' : 'key'}" style="${kind === 'bar' ? 'background' : 'border-top-color'}:${col}"></i>${esc(name)}</span>`).join('')}</div>`;

// ---- sections
const m57 = M['09-23 19:56:54'];
const t57 = exe('09-23 19:56:54'), c57 = portCells(asC(m57.rows));
const t57a = f7(t57[1]), t57f = fS(t57[1]), c57a = f7(c57[1]), c57f = fS(c57[1]), c57h = f7(c57[0]);
const secChange = `
<section class="sec gapped" id="change">
  <h2>What c would have changed at 19:57</h2>
  <p class="note">The moment of the screenshot, drawn twice at the terminal's width (${COLS} usable of ${TERM} columns).
  The top pane is the binary deployed when the question came up, on ${B0.when}, run through <code>CSHIP_OFFLINE</code> with the
  screenshot's inputs. That build had already stopped padding Fable's percentage out to the width of the 7d bar, so Fable's 24%
  sits right after its own bar here instead of far to the right as in the screenshot; every figure is the same.
  The bottom pane is the same inputs through c. Only the two <code>⇢</code> figures on the weekly rows move;
  every <code>→</code> and the whole 5h row stay exactly as they were.</p>
  ${pane({ title: 'Today', tag: B0.tag, tagCls: 'bad', cols: COLS, note: '→ and ⇢ are one pace: the last 60 minutes, stretched over the 57 hours to the reset.',
    lines: [{ cells: t57[0] }, { cells: t57[1], marks: [mk(t57a.a, '60-min pace', 'am', 1), mk(t57a.p, 'that pace for 57 h', 'am'), mk(t57f.p, 'pace 0', 'am')] }] })}
  ${pane({ title: 'c', tag: 'rejected design', tagCls: 'bad', cols: COLS, note: "⇢ becomes the week's own average pace; → keeps the 60-min pace.",
    lines: [{ cells: c57[0], marks: [mk(c57h.p, '5h unchanged', 'same')] }, { cells: c57[1], marks: [mk(c57a.a, 'unchanged', 'same', 1), mk(c57a.p, '226% to 135%', 'chg'), mk(c57f.a, 'unchanged', 'same', 1), mk(c57f.p, '24% to 36%', 'chg')] }] })}
  <p class="note">Line 1 goes ${widthNote(width(t57[0]), width(c57[0]))}, line 2 ${widthNote(width(t57[1]), width(c57[1]))}.
  135% draws ${Math.min(BarFill(135), 22)} bar cells where 226% drew 22, and the 5h row is padded to the same bar column, so both lines
  shrink by the same amount. The worst case does not change, since c goes through the same width solver, the same 300% cap and
  the same bar cap (22 cells at ${TERM} columns). The line is shorter here because the value is smaller, not because of a new rule.</p>
  <p class="note">Where 135% and 36% come from. 7d: ${m57.w.now}% after ${f1(m57.el)} h is ${f1(m57.w.avg)} %/h, and the ${f1(168 - m57.el)} h left to the reset add ${f1(m57.w.avg * (168 - m57.el))} points, which makes ${m57.w.pC}%.
  Fable: ${m57.f.now}% after the same ${f1(m57.el)} h is ${m57.f.avg.toFixed(3)} %/h, which makes ${m57.f.pC}%. Today's rule never projected Fable, because its 60-minute
  pace was 0. The replay's own <code>→ ${D.check1957.to} ⇢ ${D.check1957.pN}%</code> at 19:56:54 matches the screenshot exactly.</p>
</section>`;

// the week chart
const wk = weekChart();
const apiA = decode(S.api), todA = decode(S.today), cA = decode(S.c), lbA = decode(S.lookback);
const kC100 = cA.findIndex(v => v >= 100), tC100 = T0 + kC100 * STEP;
const cMoves = []; for (let k = 60; k < cA.length; k++) if (todA[k] > apiA[k]) cMoves.push(cA[k] - cA[k - 60]);
const cMoveMed = cMoves.sort((a, b) => a - b)[Math.floor(cMoves.length / 2)];
const rowsTable = D.hourly.map(r => `<tr><td>${esc(r[0])}</td><td>${r[1]}%</td><td>${r[2]}%</td><td>${r[3]}%</td><td>${r[4]}%</td></tr>`).join('');
const momentsTable = MOMENTS.map(([stamp, what], i) => {
  const m = M[stamp], k = D.renders.cases['w ' + stamp], plain = k.lines[1].replace(/\x1b\[[0-9;]*m/g, '');
  const arrow = (plain.match(/→ (\S+)/) || [])[1];
  const later = m.later.every(v => v === null) ? 'not observed' : m.later.map(v => v === null ? 'n/a' : v + '%').join(' / ');
  return `<tr><td><span class="numchip">${i + 1}</span> ${when(stamp)}</td><td>${esc(what)}</td><td>${m.w.now}%</td><td class="mono">→ ${esc(arrow)}</td><td>${m.w.pN}%</td><td>${m.w.pC}%</td><td>${m.w.p24}%</td><td>${later}</td></tr>`;
}).join('');
const secWeek = `
<section class="sec gapped" id="week-sec">
  <h2>The week, Sat 05:00 to Wed 20:03</h2>
  <p class="note">The 7d meter and three ways of forecasting it, every minute. Up to 19:02 the meter is reconstructed
  from this machine's own usage; from 19:02:54 it is the registry's real samples. The status line saw whole percents
  sampled every 60 seconds, and so does this replay. <b>⇢ today</b> is what the binary would have drawn at each minute.</p>
  <div class="viz" id="week-viz">
    ${legend([['weekly % (API, reconstructed)', C.truth], ['⇢ today (60-min pace)', C.today], ['⇢ c (window average)', C.c], ['⇢ 24 h lookback (Q1 c)', C.look]])}
    <div class="plot" tabindex="0" aria-describedby="week-help">${wk.svg}<div class="tip" id="tip" hidden></div></div>
    <p class="vizhelp" id="week-help">Hover over the chart, or focus it and use the arrow keys (with Shift, an hour at a time), to read every series at one minute.</p>
  </div>
  <table class="states compact">
    <thead><tr><th>Moment</th><th></th><th>7d</th><th>→ (both)</th><th>⇢ today</th><th>⇢ c</th><th>⇢ 24 h</th><th>7d 6 / 12 / 24 h later</th></tr></thead>
    <tbody>${momentsTable}</tbody></table>
  <p class="note">At 20:03 the account was switched, so the real end of the week is never observed: nothing here says whether 100%
  would have arrived before Saturday 05:00. Today's ⇢ spent ${P0(D.stats.pN.over100)} of the week above 100% and
  ${P0(D.stats.pN.at300)} pinned at the 300% cap; c crossed 100% on ${dayName(tC100)} ${LOC(tC100).slice(11)}, with the meter at ${apiA[kC100]}%, and stayed above it.</p>
  <details class="tv"><summary>Table view: the chart's values, hourly</summary>
    <table class="states compact"><thead><tr><th>Local time</th><th>7d</th><th>⇢ today</th><th>⇢ c</th><th>⇢ 24 h</th></tr></thead><tbody>${rowsTable}</tbody></table>
  </details>
</section>`;

// accuracy
const B = D.backtest, H3 = [6, 12, 24];
const methods = [['today', 'today', C.today], ['c', 'c', C.c], ['lookback', '24 h lookback', C.look], ['none', 'no change', C.none]];
const btSvg = barsChart({ groups: H3.map(h => ({ name: `${h} h ahead`, values: methods.map(([k]) => B[`${k}_${h}`].mae) })),
  series: methods.map(([, name, col]) => ({ name, col })), max: 25, unit: ' points' });
const btRows = methods.map(([k, name, col]) => `<tr><td><i class="sw" style="background:${col}"></i>${esc(name)}</td>${H3.map(h => {
  const b = B[`${k}_${h}`]; return `<td>${f1(b.mae)} <span class="dimtxt">${sg(b.bias)}</span></td>`; }).join('')}<td>${H3.reduce((n, h) => n + B[`${k}_${h}`].ge100, 0)}</td></tr>`).join('');
const falseRows = D.false100.map(x => `<tr><td>${x.method === 'today' ? 'today' : '24 h lookback'}</td><td>${esc(x.at)}</td><td>${x.h} h</td><td>${x.now}%</td><td class="bad-t">${x.fc}%</td><td>${x.actual}%</td></tr>`).join('');
const secAcc = `
<section class="sec gapped" id="accuracy">
  <h2>Accuracy: each forecast against what the meter said later</h2>
  <p class="note">Every hour, each method's pace is extended 6, 12 and 24 hours ahead and compared with the 7d value the API
  reported at that time. Forecasts are left uncapped. The reconstructed meter peaked at 90% at 20:02 and never reached 100%, so
  every forecast of 100% in this window was a false alarm.</p>
  <div class="two">
    <div class="viz">${legend(methods.map(([, n, c]) => [n, c, 'bar']))}${btSvg}<p class="vizhelp">Mean absolute error in points (lower is better).</p></div>
    <table class="states compact"><thead><tr><th>Method</th><th>6 h</th><th>12 h</th><th>24 h</th><th>said 100% or more</th></tr></thead>
      <tbody>${btRows}</tbody></table>
  </div>
  <p class="note">Each cell reads <b>mean absolute error</b>, then <span class="dimtxt">bias</span> (negative means an under-forecast).
  At 12 h and 24 h, today's method does worse than simply assuming no further use. Its small average bias hides two opposite
  errors: after a busy hour it over-forecasts (${H3.map(h => sg(B['today_' + h].bias_busy)).join(' / ')} at 6, 12 and 24 h), after a
  quiet one it under-forecasts (${H3.map(h => sg(B['today_' + h].bias_quiet)).join(' / ')}). c under-forecasts after both
  (${H3.map(h => sg(B['c_' + h].bias_busy)).join(' / ')} and ${H3.map(h => sg(B['c_' + h].bias_quiet)).join(' / ')}), which is the week-shape bias of downside 7 below.</p>
  <details class="tv" open><summary>The ${D.false100.length} forecasts of 100% (none came true)</summary>
    <table class="states compact"><thead><tr><th>Method</th><th>Made at</th><th>Horizon</th><th>7d then</th><th>Forecast</th><th>7d at the horizon</th></tr></thead><tbody>${falseRows}</tbody></table>
  </details>
</section>`;

// downsides
function film(stamps, maker, marks, gut = s => when(s)) {
  const lines = stamps.map(s => ({ cells: maker(s), gut: gut(s) }));
  if (marks) lines[lines.length - 1].marks = marks(lines[lines.length - 1].cells);
  return lines;
}
const BURST = ['09-22 14:30:54', '09-22 15:00:54', '09-22 15:30:54', '09-22 16:00:54', '09-22 16:30:54', '09-22 17:00:54'];
const burstT = film(BURST, s => exe(s)[1], cells => [mk(f7(cells).a, 'never again at 17:00', 'am', 1), mk(f7(cells).p, '178, 275, 300, then 64%', 'am')]);
const burstC = film(BURST, s => portCells(asC(M[s].rows))[1], cells => [mk(f7(cells).a, 'same as today', 'same', 1), mk(f7(cells).p, `${M[BURST[0]].w.pC}% to ${Math.max(...BURST.map(s => M[s].w.pC))}% over 2 h`, 'chg')]);
const tn = portCells(asC(M['09-22 22:00:54'].rows))[1], tnF = f7(tn);
const mo = portCells(asC(M['09-21 17:00:54'].rows))[1], moF = f7(mo);
const decay = [0, 6, 12, 24, 36, 48, 57].map(h => { const el = (D.window.end + 45 + h * 3600 - WS) / 3600; return [h, LOC(D.window.end + 45 + h * 3600).slice(11), dayName(D.window.end + 45 + h * 3600), Math.round(90 + 90 / el * (168 - el))]; });
// early window what-if: the week's busiest hour as the first hour after the reset, then nothing
const early = [1, 3, 6, 12, 24].map(el => ({ el, row: { label: '7d', pct: 7, hrs: 168 - el, hasReset: true, rate: el === 1 ? 7.7 : 0, gated: false, sev: 'normal' } }));
const one = (row, opt) => toCells(RST + ' ' + renderRows([row], 1, opt).lines[0]);
const earlyRaw = early.map(e => ({ cells: one({ ...e.row, mode: 'c-raw' }), gut: `${e.el} h in` }));
const early12 = early.map(e => ({ cells: one({ ...e.row, mode: 'c' }), gut: `${e.el} h in` }));
earlyRaw[0].marks = [mk(f7(earlyRaw[0].cells).p, '7 %/h for 167 h, capped at 300', 'am')];
early12[3].marks = [mk(f7(early12[3].cells).p, 'projects from 12 h', 'chg')];
// the 0.5 %/h jump
const jump = ['09-21 17:00:54', '09-21 18:00:54'];
const jumpOld = film(jump, s => portCells(asC(M[s].rows, 'c-old'))[1], cells => [mk(f7(cells).p, '27% to 85% in one hour', 'am')]);
const jumpNew = film(jump, s => portCells(asC(M[s].rows))[1], cells => [mk(f7(cells).p, '76% to 85%', 'chg')]);
// 168 h
const L57 = [-24, -6, 0, 6, 24].map(dl => { const el = m57.el + dl; return [dl, Math.round(89 + 89 / el * (168 - m57.el))]; });
function weekTrack() {
  const Wd = 1100, H = 92, x0 = 20, x1 = Wd - 20, X = h => x0 + h / 168 * (x1 - x0), el = m57.el;
  let g = `<rect x="${x0}" y="30" width="${x1 - x0}" height="14" rx="3" fill="${C.mute}" fill-opacity=".25"/>`;
  g += `<rect x="${x0}" y="30" width="${fx(X(el) - x0)}" height="14" rx="3" fill="${C.c}"/>`;
  for (let d = 0; d <= 7; d++) g += `<line x1="${fx(X(d * 24))}" x2="${fx(X(d * 24))}" y1="26" y2="48" stroke="${C.mute}" stroke-opacity=".5"/><text x="${fx(X(d * 24))}" y="64" text-anchor="${d === 0 ? 'start' : d === 7 ? 'end' : 'middle'}" class="ax">${['Sat', 'Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'][d]} 05:00</text>`;
  g += `<text x="${fx((x0 + X(el)) / 2)}" y="22" text-anchor="middle" class="ax strong">elapsed ${f1(el)} h = 168 h - ↻ ${f1(168 - el)} h</text>`;
  g += `<text x="${fx((X(el) + x1) / 2)}" y="22" text-anchor="middle" class="ax">↻ ${f1(168 - el)} h to the reset</text>`;
  g += `<text x="${fx(X(el))}" y="84" text-anchor="middle" class="ax strong">19:57</text>`;
  return `<svg viewBox="0 0 ${Wd} ${H}" role="img" aria-label="The 168-hour weekly window: ${f1(el)} hours elapsed at 19:57, ${f1(168 - el)} to the reset">${g}</svg>`;
}
// week shape
const shape = D.shape;
const shapeSvg = (() => {
  const Wd = 560, H = 230, m = { l: 36, r: 12, t: 18, b: 34 }, x0 = m.l, x1 = Wd - m.r, y0 = H - m.b, y1 = m.t, max = 50;
  const Y = v => y0 - v / max * (y0 - y1), gw = (x1 - x0) / shape.length, avgDay = 90 / (m57.el / 24);
  let g = '';
  for (let v = 0; v <= max; v += 10) g += `<line x1="${x0}" x2="${x1}" y1="${fx(Y(v))}" y2="${fx(Y(v))}" stroke="${C.mute}" stroke-opacity="${v === 0 ? .9 : .22}"/><text x="${x0 - 6}" y="${fx(Y(v)) + 4}" text-anchor="end" class="ax">${v}</text>`;
  shape.forEach((s, i) => {
    const cx = x0 + gw * (i + .5), bw = 24, y = Y(s.added), r = Math.min(4, y0 - y);
    g += `<path d="M${fx(cx - bw / 2)},${y0} V${fx(y + r)} Q${fx(cx - bw / 2)},${fx(y)} ${fx(cx - bw / 2 + r)},${fx(y)} H${fx(cx + bw / 2 - r)} Q${fx(cx + bw / 2)},${fx(y)} ${fx(cx + bw / 2)},${fx(y + r)} V${y0} Z" fill="${C.truth}"><title>${s.day}: +${s.added} points</title></path>`;
    g += `<text x="${fx(cx)}" y="${fx(y) - 5}" text-anchor="middle" class="ax val">+${s.added}</text><text x="${fx(cx)}" y="${y0 + 20}" text-anchor="middle" class="ax">${s.day.slice(0, 3)}${s.day.startsWith('Wed') ? ' to 20:02' : ''}</text>`;
  });
  g += `<line x1="${x0}" x2="${x1}" y1="${fx(Y(avgDay))}" y2="${fx(Y(avgDay))}" stroke="${C.c}" stroke-width="2"/><text x="${x0 + 6}" y="${fx(Y(avgDay)) - 6}" text-anchor="start" class="ax strong">c's pace: ${f1(avgDay)} points a day</text>`;
  return `<svg viewBox="0 0 ${Wd} ${H}" role="img" aria-label="Weekly points added per day: ${shape.map(s => s.day.slice(0, 3) + ' ' + s.added).join(', ')}">${g}</svg>`;
})();
const DG = D.disagree, AR = D.arrow;
const secDown = `
<section class="sec gapped" id="downsides">
  <h2>What c costs: seven downsides, each shown at a moment it happened</h2>

  <div class="dn"><h3>1. It reacts slowly when a burst starts</h3>
  <p>Tuesday from 15:00 to 16:30 was the busiest stretch of the week (+${M[BURST[5]].w.now - M[BURST[0]].w.now} points in two and a half hours).
  c's ⇢ climbed from ${M[BURST[0]].w.pC}% to ${Math.max(...BURST.map(s => M[s].w.pC))}% over two hours, while <code>→</code>, which c leaves alone, turned into
  a red countdown within half an hour. Over the week, an hour in which today's pace was burning moved c's ⇢ by a median of ${cMoveMed} points.</p>
  ${pane({ title: 'Today', note: '7d and Fable line, every 30 minutes', lines: burstT, gutW: 11, cols: fit(burstT, burstC) })}
  ${pane({ title: 'c', lines: burstC, gutW: 11, cols: fit(burstT, burstC) })}</div>

  <div class="dn"><h3>2. And when you stop: <code>→ never ⇢ 128%</code> all Tuesday night</h3>
  <p>Nothing ran from 21:00 to the morning. <code>→</code> says so, but c's ⇢ keeps the week's average and stays above 100%.
  The row then holds two answers to two questions, and reads like a contradiction: <b>${P0(DG.B_share)} of the week</b>
  (${f1(DG.B_hours)} h) showed <code>→ never</code> beside a ⇢ at or above 100% (median ${DG.B_gap_med} points over).</p>
  ${pane({ title: 'c, Tue 22:00', lines: [{ cells: tn, marks: [mk(tnF.a, 'never: nothing running', 'am', 1), mk(tnF.p, "the week's average", 'chg')] }] })}
  <p>The account switch is the extreme case. If the old account is never used again, c keeps predicting an overshoot for
  about 40 hours: ${decay.map(([h, t, d, v]) => `<b>${v}%</b> at ${d} ${t}`).join(', ')}. Today's method drops to <code>→ never</code> and <code>⇢ 90%</code> within the hour.</p></div>

  <div class="dn"><h3>3. The other contradiction: <code>→ 15h13m ⇢ 76%</code> on Monday at 17:00</h3>
  <p>Mid-burst, the 60-minute pace says 100% arrives in 15 hours, while the week's average says the week ends at 76%.
  <b>${P0(DG.A_share)} of the week</b> (${f1(DG.A_hours)} h) looked like this, with ⇢ a median ${DG.A_gap_med} points under 100.
  Under today's method both figures come from one pace and can never disagree.</p>
  ${pane({ title: 'c, Mon 17:00', lines: [{ cells: mo, marks: [mk(moF.a, 'red: 100% before the reset', 'am', 1), mk(moF.p, '76%: no overshoot', 'chg')] }] })}</div>

  <div class="dn"><h3>4. The first hours of a window, and the 12-hour fix</h3>
  <p>Dividing by the hours elapsed is unstable while there are only a few of them. It did not bite this week: the first whole
  point came ${F.first_point_el} h in, on ${F.first_point}, where c showed 10% against today's 163%, and the last four weekly resets
  (all Saturday 05:00) saw no local use at all in their first six hours. Suppose instead that the week's busiest hour came
  first after the reset, then nothing. This is the 7d row alone, as c would draw it:</p>
  <div class="stack">
  ${pane({ title: 'c, no minimum', tag: 'shows 300%', tagCls: 'bad', lines: earlyRaw, gutW: 8, cols: fit(earlyRaw, early12) })}
  ${pane({ title: 'c, first 12 h show the current value', tag: 'proposed', tagCls: 'good', lines: early12, gutW: 8, cols: fit(earlyRaw, early12) })}
  </div>
  <p>The true end of that week would be 7%. Waiting 12 hours removes the 300% and 196% frames; 24 hours would also hold
  back the 98% at 12 h but delays every legitimate projection by a day.</p></div>

  <div class="dn"><h3>5. It needs the window length, which the API does not send</h3>
  <p>c divides by the time since the window opened, and the usage endpoint only reports <code>resets_at</code>. So the
  length has to be a constant per kind: 168 h for the weekly rows, 5 h for the session. Both hold on this account. The
  registry saw the week open at ${F.vfW} (${F.rsW_minus_168h} is <code>resets_at</code> minus 168 h) and the session at
  ${F.vfS} for a reset at ${F.rsS}. A wrong constant fails silently:</p>
  <div class="viz">${weekTrack()}</div>
  <table class="states compact"><thead><tr><th>Assumed week length</th>${L57.map(([dl]) => `<th>${168 + dl} h</th>`).join('')}</tr></thead>
    <tbody><tr><td>c's 7d ⇢ at 19:57</td>${L57.map(([dl, v]) => `<td>${dl === 0 ? `<b>${v}%</b>` : v + '%'}</td>`).join('')}</tr></tbody></table></div>

  <div class="dn"><h3>6. The old 0.5 %/h test makes c jump</h3>
  <p>Today a row only projects above 0.5 %/h. Applied to c's average, that switches the projection on all at once: the
  week's average crossed 0.5 %/h between 17:00 and 18:00 on Monday. The proposed test, which projects when that adds at
  least one point by the reset, has no such edge.</p>
  <div class="stack">
  ${pane({ title: 'c, test: above 0.5 %/h', tag: `max hourly jump ${D.stats.pC_old.max}`, tagCls: 'bad', lines: jumpOld, gutW: 10, cols: fit(jumpOld, jumpNew) })}
  ${pane({ title: 'c, test: adds at least 1 point', tag: `max hourly move ${D.stats.pC.max}`, tagCls: 'good', lines: jumpNew, gutW: 10, cols: fit(jumpOld, jumpNew) })}
  </div></div>

  <div class="dn"><h3>7. Its error depends on the shape of the week</h3>
  <div class="two"><div class="viz">${shapeSvg}</div>
  <div><p>This week started quiet and ended busy: two points over the weekend, then 44, 23 and 21. The window average is
  diluted by the quiet days, so c under-forecast at every horizon, with a bias of ${sg(B.c_6.bias)}, ${sg(B.c_12.bias)} and
  ${sg(B.c_24.bias)} points at 6, 12 and 24 h. A week with its busy days first would flip the sign. One week of data
  cannot say which shape is more common on this account.</p>
  <p>The remaining 57 hours at 19:57 were Thursday, Friday and three nights, with no weekend among them, so the average
  most likely under-states them too.</p></div></div></div>
</section>`;

// → flicker
const NIGHT = ['09-23 00:00:54', '09-23 00:30:54', '09-23 01:00:54', '09-23 01:30:54', '09-23 02:00:54', '09-23 02:30:54', '09-23 03:00:54', '09-23 03:30:54'];
const nightC = film(NIGHT, s => portCells(asC(M[s].rows))[1], cells => [mk(f7(cells).a, 'never or a red countdown', 'am', 1), mk(f7(cells).p, 'c: 127 to 132%', 'chg')]);
const secFlicker = `
<section class="sec gapped" id="flicker">
  <h2>What c does not fix: the → flicker</h2>
  <p class="note">c only re-sources ⇢. <code>→</code> still comes from 60 minutes of whole-percent samples, so a single
  one-point step landing mid-window turns it into a red countdown for a few minutes. Wednesday night from 00:00 to 03:30, with c:</p>
  ${pane({ title: 'c, Wed 00:00 to 03:30', note: "every 30 minutes; → is the same under today's method", lines: nightC, gutW: 11 })}
  <table class="states compact"><tbody>
    <tr><td>→ says never</td><td><b>${P0(AR.never)}</b> of the week's minutes; a red countdown in ${P0(AR.red)}</td></tr>
    <tr><td>Flips</td><td><b>${f1(AR.flips_day)}</b> a day between never and a countdown; ${AR.episodes} red episodes, with a median life of <b>${AR.ep_med_min} minutes</b></td></tr>
    <tr><td>Countdowns</td><td>median ${AR.to_med} (10th to 90th percentile: ${AR.to_p10} to ${AR.to_p90}); ${AR.expired} countdown-minutes expired inside the data, <b>${AR.expired_hit}</b> of them with the meter at 100%</td></tr>
  </tbody></table>
  <p class="note">The 24-hour lookback in Q1 c was the only option that calmed <code>→</code> as well: it flips
  ${f1(D.arrow24.flips_day)} times a day instead of ${f1(AR.flips_day)}.</p>
</section>`;

// the proposed build
const secBuild = `
<section class="sec gapped" id="build">
  <h2>The build that was proposed (rejected, never implemented)</h2>
  <table class="states"><tbody>
    <tr><td>⇢ on 7d and Fable</td><td><b>now + now / elapsed x hours to reset</b>, where elapsed = 168 h - hours to reset. Capped at 300% as today.</td></tr>
    <tr><td>When ⇢ projects</td><td>Only if that adds <b>at least one point</b> by the reset. For ⇢ this replaces the test for more than 0.5 %/h, which never projected Fable this week.</td></tr>
    <tr><td>First 12 h</td><td>⇢ shows the current value until 12 hours of the window have passed.</td></tr>
    <tr><td>→ and its red</td><td>Unchanged: the 60-minute pace, the gating and the overshoot test all stay.</td></tr>
    <tr><td>5h row</td><td>Unchanged. Over ${F.fivehour.windows} reconstructed session windows c would score a mean error of ${F.fivehour.mae_c} points against today's ${F.fivehour.mae_today} (no change: ${F.fivehour.mae_none}), a small gain on a row whose short horizon already limits the damage.</td></tr>
    <tr><td>Width</td><td><b>Nothing extra in the worst case.</b> The width solver, the 300% cap and the bar cap are untouched; c can only draw a shorter bar than today's ⇢ for the same row. At 19:57 both lines are ${width(t57[1]) - width(c57[1])} columns narrower.</td></tr>
    <tr><td>Where</td><td>One more pace per row where <code>RenderRows</code> projects, and a window length per limit kind (the API sends only <code>resets_at</code>).</td></tr>
  </tbody></table>
</section>`;

// ---- as built (data.asbuilt, then data.asbuilt2)
const AB = D.asbuilt, abc = name => AB.cases[name];
const abCells = name => abc(name).lines.map(toCells);
const abShot = abCells('shot'), abOver = abCells('overshoot7d'), abLive = abCells('live'), abE38 = abCells('email38'),
      abE58 = abCells('email58'), abFour = abCells('fourth'), abCS = abCells('credit-spent'), abCA = abCells('credit-at100');
// the breakdown's tail on the account line, from its first · to the end
const tailOf = cells => { const t = colText(cells), i = t.indexOf('· CC'); if (i < 0) throw new Error('no breakdown'); return { col: i, len: t.length - i }; };
const warnSpan = cells => span(cells, /⚠.*$/);
// Q2 is a colour and nothing else: the port with and without forest must differ in exactly the
// cells of the 5h → field, and only in their class
const q2only = (() => {
  const a = port(m57.rows, EMAIL).lines.map(toCells)[0], b = port(m57.rows, EMAIL, 'Max 20', { forest: true }).lines.map(toCells)[0];
  if (a.length !== b.length) throw new Error('forest changed the width');
  const diff = a.map((c, i) => (c.ch !== b[i].ch || c.cls !== b[i].cls || c.bold !== b[i].bold) ? i : -1).filter(i => i >= 0);
  if (diff.some(i => a[i].ch !== b[i].ch)) throw new Error('forest changed a character');
  const s = span(a, ARROW);
  if (diff.length !== s.len || diff[0] !== s.col) throw new Error('forest reached outside the → field');
  return { cells: diff.length, from: a[s.col].cls, to: b[s.col].cls };
})();
// Q6 is a colour and nothing else: the port with and without the grey differs only inside the maxed
// row's ⇢ segment, and only in class
const q6only = (() => {
  const c = D.asbuilt2.cases['maxed5h'];
  const a = port(c.rows, c.fixture.email, c.plan, abOpt(c)).lines.map(toCells)[0];
  const b = port(c.rows, c.fixture.email, c.plan, ab2Opt(c)).lines.map(toCells)[0];
  if (a.length !== b.length) throw new Error('the grey changed the width');
  const diff = a.map((x, i) => (x.ch !== b[i].ch || x.cls !== b[i].cls || x.bold !== b[i].bold) ? i : -1).filter(i => i >= 0);
  if (diff.some(i => a[i].ch !== b[i].ch)) throw new Error('the grey changed a character');
  const t = colText(a), c0 = t.indexOf('⇢'), c1 = t.indexOf('%', c0);
  if (diff.some(i => i < c0 || i > c1)) throw new Error('the grey reached outside the ⇢ segment');
  // the segment is ⇢, a space, the bar, a space, the percentage
  const p0 = t.lastIndexOf(' ', c1) + 1, pct = t.slice(p0, c1 + 1);
  const bar = diff.filter(i => i > c0 && i < p0).length, pctN = diff.filter(i => i >= p0).length;
  if (bar + pctN !== diff.length || pctN !== pct.length) throw new Error('the grey changed something besides the bar and the percentage');
  if (a[c0].cls !== 'dm' || b[c0].cls !== 'dm') throw new Error('the ⇢ glyph is not dim in both');
  return { cells: diff.length, bar, pct };
})();
// the binary deployed after Q4 and Q6 (data.asbuilt2): drawn from the same fixtures, where they overlap
const AB2 = D.asbuilt2, ab2c = name => AB2.cases[name];
const ab2Cells = name => ab2c(name).lines.map(toCells);
const nShot = ab2Cells('shot'), nOver = ab2Cells('overshoot7d'), nLive = ab2Cells('live'), nE38 = ab2Cells('email38'),
      nE58 = ab2Cells('email58'), nFour = ab2Cells('fourth'), nCS = ab2Cells('credit-spent'), nCA = ab2Cells('credit-at100'),
      nCOff = ab2Cells('credit-off-at100'), nMax = ab2Cells('maxed5h'), nMany = ab2Cells('many-ignored'), nUnk = ab2Cells('unknown'),
      nFC = ab2Cells('fourth-credit');
// which captures of the build deployed after Q2 and Q3 does the one after Q4 and Q6 still draw byte for byte?
const SAME = {}, SUPER = [];
for (const name of Object.keys(AB.cases)) {
  const a = AB.cases[name].lines, b = AB2.cases[name] && AB2.cases[name].lines;
  if (!b) continue;
  SAME[name] = a.length === b.length && a.every((l, i) => l === b[i]);
  if (!SAME[name]) SUPER.push(name);
}
const noticeSpan = cells => span(cells, /⚠ meters.*$/);
const moreSpan = cells => span(cells, /\+\d+ more/);
const segOf = cells => { const t = colText(cells), i = t.indexOf('⇢'), j = t.indexOf('%', i); return { col: i, len: j - i + 1 }; };
const AB_NOTE = `Drawn by the binary deployed on ${B2.when} (sha256 ${AB2.exe_sha256.slice(0, 12)}...), through <code>CSHIP_OFFLINE</code>
  from the implementation's own fixtures, at ${TERM} columns.`;
const secAsBuilt = `
<section class="sec gapped" id="asbuilt">
  <h2>As built: ${B2.tag}</h2>
  <p class="note">${AB_NOTE} This is Q2 to Q6 as they stand. Of the ${Object.keys(SAME).length} fixtures drawn by both
  this build and the one deployed at ${B1.time}, ${Object.values(SAME).filter(Boolean).length} render byte-identical; the other
  ${SUPER.length} (${SUPER.join(', ')}) are kept at the end of this section, marked superseded. The rows of Q1 further down are
  from the binary that was deployed when the questions came up, on ${B0.when}.</p>
  ${pane({ title: 'Q2: the 19:57 screenshot, rebuilt', tag: `since ${B1.tag}`, tagCls: 'pick', cols: COLS,
    note: 'The same inputs as the screenshot. The 5h → is forest because its reset (3h23m) comes before 100% would (7h29m).',
    lines: [{ cells: nShot[0], marks: [mk(span(nShot[0], ARROW), 'forest: the reset comes first', 'fo'), mk(tailOf(nShot[0]), 'Q3: the breakdown', 'chg')] },
            { cells: nShot[1], marks: [mk(f7(nShot[1]).a, 'red: 100% comes first', 'rd')] }] })}
  ${pane({ title: 'Q2: both colours on one screen', tag: 'fixture', tagCls: 'no', cols: COLS,
    note: 'A 5h row whose reset comes first above a 7d row that runs out first. The account line had no room left here, so the breakdown is left off.',
    lines: [{ cells: nOver[0], marks: [mk(span(nOver[0], ARROW), 'forest', 'fo')] }, { cells: nOver[1], marks: [mk(f7(nOver[1]).a, 'red', 'rd')] }] })}
  <p class="note">Q2 costs no width. Drawn with and without forest, the port differs in exactly ${q2only.cells} cells, those of
  <code>→ 7h29m</code>, whose class goes from <code>${q2only.from}</code> to <code>${q2only.to}</code>, and in nothing else. Red, <code>never</code>,
  <code>early</code> and <code>maxed</code> are unchanged, and the rule is the same on every row.</p>
  <div class="stack">
  ${pane({ title: 'Q3: the breakdown gives way', tag: 'all entries', tagCls: 'no', cols: COLS, lines: [{ cells: nLive[0], marks: [mk(tailOf(nLive[0]), 'CC 99% · Chat 0% · Cowork 1%', 'chg')] }, { cells: nLive[1] }] })}
  ${pane({ tag: 'a longer account: the 0% entries go first', tagCls: 'no', cols: COLS, lines: [{ cells: nE38[0], marks: [mk(tailOf(nE38[0]), 'without its 0% entry', 'chg')] }, { cells: nE38[1] }] })}
  ${pane({ tag: 'longer still: none', tagCls: 'no', cols: COLS, lines: [{ cells: nE58[0] }, { cells: nE58[1] }] })}
  </div>
  <p class="note">The breakdown takes no part in the layout decision. It gets whatever room the line has left (every entry,
  then without the 0% ones, then none) and is never wrapped or cut inside an entry. Line 1 here is ${width(nLive[0])},
  ${width(nE38[0])} and ${width(nE58[0])} columns, all inside ${COLS}.</p>
  ${pane({ title: 'Q4: a meter this status line does not know', tag: `since ${B2.tag}`, tagCls: 'pick', cols: COLS,
    note: 'The three known meters are drawn as always; the fourth is not drawn, only named, in an amber notice at the bottom.',
    lines: [{ cells: nFour[0] }, { cells: nFour[1] }, { cells: nFour[2], marks: [mk(noticeSpan(nFour[2]), 'amber: review cship-usage', 'am')] }] })}
  ${pane({ tag: 'several: named while they fit, then +N more', tagCls: 'no', cols: COLS,
    lines: [{ cells: nMany[0] }, { cells: nMany[1] }, { cells: nMany[2], marks: [mk(moreSpan(nMany[2]), `${ab2c('many-ignored').fixture.ignored.length} ignored`, 'am')] }] })}
  ${pane({ tag: 'unknown kinds are named by their kind', tagCls: 'no', cols: COLS, lines: [{ cells: nUnk[0] }, { cells: nUnk[1] }, { cells: nUnk[2] }] })}
  ${pane({ title: 'Q5: on credit', tag: `since ${B1.tag}`, tagCls: 'pick', cols: COLS,
    note: 'A red ⚠ row of its own, always last; the amber notice, when there is one, sits above it.',
    lines: [{ cells: nFC[0] }, { cells: nFC[1] }, { cells: nFC[2] }, { cells: nFC[3], marks: [mk(warnSpan(nFC[3]), 'red: spend.used above 0', 'rd')] }] })}
  ${pane({ title: 'Q6: a row at 100%', tag: `since ${B2.tag}`, tagCls: 'pick', cols: COLS,
    note: 'The 5h row is maxed. Its ⇢ segment keeps its glyphs, and so its width, but every cell is dim: greyed, like a disabled control.',
    lines: [{ cells: nMax[0], marks: [mk(segOf(nMax[0]), 'greyed: blocked until ↻', 'same')] }, { cells: nMax[1] }] })}
  ${pane({ tag: 'maxed with credits on', tagCls: 'no', cols: COLS,
    lines: [{ cells: nCA[0], marks: [mk(segOf(nCA[0]), 'greyed', 'same')] }, { cells: nCA[1] }, { cells: nCA[2], marks: [mk(warnSpan(nCA[2]), '5h at 100% with credits on', 'rd')] }] })}
  <p class="note">The grey adds no width either. Drawn with and without it, the port differs in exactly ${q6only.cells} cells (the
  ${q6only.bar} of the bar and the ${q6only.pct.length} of <code>${q6only.pct}</code>, all inside the maxed row's
  <code>⇢</code> segment), and only in colour. The <code>⇢</code> glyph was dim already.</p>
  <div class="superseded">
    <div class="warn slim"><div class="warn-tag">Superseded: ${B1.tag}</div>
      <p>How the binary deployed on ${B1.when} drew the ${SUPER.length} fixtures that the one deployed at ${B2.time} draws differently.
      They are kept because Q4 and Q6 are about these rows.</p></div>
    ${pane({ title: 'Q3: a fourth meter, drawn', tag: 'superseded by Q4', tagCls: 'bad', cols: COLS,
      note: 'Every meter got a row, past the third a line of its own in the right column, and a meter that no history series followed drew <code>→ —</code>.',
      lines: [{ cells: abFour[0] }, { cells: abFour[1] }, { cells: abFour[2], marks: [mk(span(abFour[2], /→ —/), 'no history series: Q4', 'am')] }] })}
    ${pane({ title: 'Q3: maxed with credits on, ⇢ in colour', tag: 'superseded by Q6', tagCls: 'bad', cols: COLS,
      lines: [{ cells: abCA[0], marks: [mk(segOf(abCA[0]), '⇢ 204% in colour: Q6', 'am')] }, { cells: abCA[1] }, { cells: abCA[2] }] })}
  </div>
</section>`;

// ---- decisions
// status: 'chosen' | 'rejected' | 'no' (not chosen) | 'rec' (recommended, still open)
const STATUS = { chosen: ['pick', 'chosen'], rejected: ['bad', 'rejected'], no: ['no', 'not chosen'], rec: ['good', 'recommended'] };
function optCard({ letter, title, status, lines, cols, note, gutW, body }) {
  const [cls, label] = STATUS[status] || [];
  return `<div class="opt${status === 'chosen' || status === 'rec' ? ' rec' : ''}">
    <div class="opt-h"><span class="letter">${letter}</span><span class="opt-t">${title}</span>${label ? `<span class="tag ${cls}">${label}</span>` : ''}</div>
    ${lines ? pane({ lines, gutW: gutW || 0, cols }) : ''}
    ${note ? `<p class="opt-w">${note}</p>` : ''}${body || ''}</div>`;
}
const DECIDED = (text, tag = 'pick') => `<span class="tag ${tag}">${text}</span>`;
// Q1
const mMo = M['09-21 17:00:54'];
const q1 = {
  a: { rows57: asC(m57.rows), rowsMo: asC(mMo.rows) },
  b: { rows57: asC(m57.rows, 'c', { projLabel: '⇢avg' }), rowsMo: asC(mMo.rows, 'c', { projLabel: '⇢avg' }) },
  c: { rows57: asLook(m57), rowsMo: asLook(mMo) },
};
function q1lines(k) {
  if (k === 'd') { const a = exe('09-23 19:56:54'), b = exe('09-21 17:00:54'); return [{ cells: a[0], gut: '19:57' }, { cells: a[1], gut: '19:57' }, { cells: b[1], gut: 'Mon 17:00' }]; }
  const a = portCells(q1[k].rows57), b = portCells(q1[k].rowsMo);
  return [{ cells: a[0], gut: '19:57' }, { cells: a[1], gut: '19:57' }, { cells: b[1], gut: 'Mon 17:00' }];
}
const q1w = k => { const L = q1lines(k); return L.map(l => width(l.cells)); };
const wD = q1w('d');
const Q1COLS = fit(...['a', 'b', 'c', 'd'].map(q1lines), abShot.map(c => ({ cells: c })));
const q1note = k => { const w = q1w(k); return `Width against today: at 19:57 line 1 ${widthNote(wD[0], w[0])} and line 2 ${widthNote(wD[1], w[1])}; at Mon 17:00 line 2 ${widthNote(wD[2], w[2])}.`; };
const capB = port(q1.b.rows57, EMAIL).capBar;
const secQ1 = `
<div class="q" id="q1"><h3 class="qh">Q1: Switch the weekly rows to c? ${DECIDED('rejected, 2026-09-23', 'bad')}</h3>
  <div class="warn slim">
    <div class="warn-tag">Rejected design, not the product</div>
    <p><b>Decided 2026-09-23: keep today's method.</b> One pace, the last 60 minutes, keeps driving both <code>→</code> and
    <code>⇢</code> on every row. c and its two variants were not built. The replay they were judged on is the
    <a href="#rejected-c">rejected block</a> above; the record is in
    <a href="rejected-designs.md#rejected-a-window-average-projection-option-c">rejected-designs.md</a>.</p>
  </div>
  <p class="primer">Today one number, the pace of the last 60 minutes, drives both <code>→</code> (how soon 100% comes
  if this continues) and <code>⇢</code> (where the week ends). Over a 57-hour horizon that number swings between 0 and
  300% many times a day. The question was which pace each glyph should use on the 7d and Fable rows; the 5h row was out of
  scope. Each option is shown at 19:57 and at Monday 17:00, where the options differ most, drawn by the rules of the build
  deployed on ${B0.when}.</p>
  ${optCard({ letter: 'a', title: 'c as proposed: ⇢ from the window average, → from the 60-min pace', status: 'rejected', lines: q1lines('a'), gutW: 10, cols: Q1COLS, note: q1note('a') + ' Error at 6, 12 and 24 h: ' + H3.map(h => f1(B['c_' + h].mae)).join(' / ') + '; no false 100%. Recommended, and rejected.' })}
  ${optCard({ letter: 'b', title: "As a, with ⇢ labelled so the two paces don't read as one", status: 'no', lines: q1lines('b'), gutW: 10, cols: Q1COLS,
    note: q1note('b') + ` Against a: ${q1w('b').map((w, i) => `+${w - q1w('a')[i]}`).join(' / ')} columns. The label costs 3 columns on every row of a column that carries it, so the 5h row pads to match, and the solver takes 6 columns from the bars: the bar cap drops from 22 to ${capB}.` })}
  ${optCard({ letter: 'c', title: '24-hour lookback for both → and ⇢', status: 'no', lines: q1lines('c'), gutW: 10, cols: Q1COLS,
    note: q1note('c') + ` At 19:57 the replay gives <code>→ ${D.check1957.to24} ⇢ ${D.check1957.p24}%</code> (from whole percents; the earlier estimate of 12h45m / 138% used usage instead). Error: ${H3.map(h => f1(B['lookback_' + h].mae)).join(' / ')}, bias ${H3.map(h => sg(B['lookback_' + h].bias)).join(' / ')}; ${H3.reduce((n, h) => n + B['lookback_' + h].ge100, 0)} false 100% forecasts; ⇢ swings up to ${D.stats.p24.max} points in an hour (c: ${D.stats.pC.max}); → flips ${f1(D.arrow24.flips_day)} times a day. It needs 24 h of history instead of 1 h: about 1,440 samples in the registry value, and Theil-Sen over them is about 1 M pairs per render, so in practice it would have to be a simpler slope.` })}
  ${optCard({ letter: 'd', title: "Keep today's method", status: 'chosen', lines: q1lines('d'), gutW: 10, cols: Q1COLS,
    note: `The ${B0.tag} build, as deployed when this was decided. Error ${H3.map(h => f1(B['today_' + h].mae)).join(' / ')}; ${H3.reduce((n, h) => n + B['today_' + h].ge100, 0)} false 100% forecasts; in ${P0(D.stats.pN.ge50)} of minutes ⇢ is at least 50 points away from where it was an hour earlier.`,
    body: pane({ title: `As built: ${B1.tag}`, tag: 'the same forecast', tagCls: 'pick', cols: Q1COLS, gutW: 10,
      lines: [{ cells: abShot[0], gut: '19:57' }, { cells: abShot[1], gut: '19:57' }] }) + `<p class="opt-w">The forecast is unchanged; the 5h <code>→</code> is forest (Q2) and the account carries the breakdown (Q3).</p>` })}
  <p class="recline"><b>Decided: d, keep today's method.</b> Option a had been recommended. The replay found c steadier and more
  accurate at every horizon, but slower to react when use starts or stops, under-forecasting all week, and drawing an
  <code>→</code> and a <code>⇢</code> that disagreed on the same row ${P0(DG.A_share + DG.B_share)} of the time.</p>
</div>`;
// Q2
const q2row = opt => portCells(m57.rows, EMAIL, opt)[0];
const q2a = t57[0], q2b = q2row({ afterReset: 'never' }), q2c = q2row({ afterReset: 'first' }), q2d = q2row({ afterReset: 'dash' });
const Q2COLS = COLS;
const q2m = (cells, text, tone) => [mk(span(cells, /(→|↻) \S+/, 25), text, tone)];
const secQ2 = `
<div class="q" id="q2"><h3 class="qh">Q2: What → shows when 100% would come after the row's own reset ${DECIDED('decided, 2026-09-24')}</h3>
  <p class="primer">At 19:57 the 5h row said <code>→ 7h29m</code>, but its window reset in 3h23m: the counter restarts
  first, so 100% never arrives in this window. Only the dim colour (instead of red) said so. Four options were drawn, a to d
  below; the one chosen was a fifth, e.</p>
  ${optCard({ letter: 'e', title: 'Keep the time, and draw <code>→</code> and the time in forest green <code>#28A428</code>', status: 'chosen', cols: Q2COLS,
    lines: [{ cells: abShot[0], marks: [mk(span(abShot[0], ARROW), 'forest: the reset comes first', 'fo')] }],
    note: `As deployed on ${B1.when}. When the reset comes first, so that 100% cannot be reached in this window, the time that used to be dim is drawn in forest green, greener than the reset time beside it. It takes no width: only the ${q2only.cells} cells of <code>→ 7h29m</code> change, and only their colour. The line is longer than a's because of the breakdown from Q3.` })}
  ${optCard({ letter: 'a', title: 'Keep the time, dim', status: 'no', cols: Q2COLS, lines: [{ cells: q2a, marks: q2m(q2a, 'after the reset: only the colour says so', 'am') }], note: `The ${B0.tag} build, ${width(q2a)} columns. The chosen option, e, is this row with the time in forest green.` })}
  ${optCard({ letter: 'b', title: '<code>never</code>', status: 'no', cols: Q2COLS, lines: [{ cells: q2b, marks: q2m(q2b, 'not in this window', 'chg') }], note: `Width ${widthNote(width(q2a), width(q2b))}. Recommended, but it would have dropped the pace that the time still carries.` })}
  ${optCard({ letter: 'c', title: 'A new marker: <code>↻ first</code>', status: 'no', cols: Q2COLS, lines: [{ cells: q2c, marks: q2m(q2c, '↻ replaces →', 'chg') }], note: `Width ${widthNote(width(q2a), width(q2c))}: <code>↻ first</code> takes the same seven cells as <code>→ 7h29m</code>. ↻ is U+21BB, East-Asian Ambiguous like every glyph already on the row, so it is single-width wherever they are.` })}
  ${optCard({ letter: 'd', title: '<code>—</code>', status: 'no', cols: Q2COLS, lines: [{ cells: q2d, marks: q2m(q2d, '— padded to the column', 'am') }], note: `Width ${widthNote(width(q2a), width(q2d))}. Everywhere else in the binary, <code>—</code> already means that a source reported nothing.` })}
  <p class="recline"><b>Decided: e, forest green, implemented on 2026-09-24.</b> It keeps the time from a, so the pace
  stays readable, and gives the harmless case a colour of its own, greener than the reset time beside it. Red, <code>never</code>,
  <code>early</code> and <code>maxed</code> are unchanged, on the 5h, 7d and scoped rows alike.</p>
</div>`;
// Q3
const U = D.usage, q3 = D.renders.cases['q3 22:14:20'];
const cowork = { label: 'Cowork', pct: 12, hrs: q3.rows[2].hrs, hasReset: true, rate: 0, gated: false, sev: 'normal' };
const q3a = q3.lines.map(toCells);
const q3bRows = [...q3.rows, cowork];
const q3b = port(q3bRows, q3.email, 'Max 20', { rightExtra: true }).lines.map(toCells);
const bd = U.breakdown.filter(r => !(r.key === 'other' && r.percent === 0));
const short = { claude_code: 'CC', chat: 'Chat', cowork: 'Cowork', other: 'Other' };
const bdAnsi = bd.map(r => `${TXT}${short[r.key] || r.display_name} ${r.percent}${RST}`).join(` ${DIM}·${RST} `);
const q3c = port(q3bRows, q3.email, 'Max 20', { rightExtra: true, acctExtra: ` ${DIM}·${RST} ${bdAnsi}` }).lines.map(toCells);
const credits = toCells(RST + ' ' + `credits ${Bar(25, 10, 10)} ${PctColor(25)}25%${RST} ${TXT}$12,40${RST} ${DIM}of${RST} ${TXT}$50,00${RST} ${DIM}· extra usage, monthly${RST}`);
const bdSpan = span(q3c[0], /CC \d+.*$/);
const Q3COLS = fit([...q3a, ...q3b, ...q3c, credits, ...abFour, ...abCS, ...nFour].map(c => ({ cells: c })));
const limitsList = U.limits.map(l => `<code>${l.kind}</code> ${l.percent}%${l.scope && l.scope.model ? ' (' + l.scope.model.display_name + ')' : ''}${l.is_active ? ' (<b>is_active</b>)' : ''}`).join(', ');
const secQ3 = `
<div class="q" id="q3"><h3 class="qh">Q3: Show more of what the usage API returns? ${DECIDED('decided, 2026-09-24')}</h3>
  <p class="primer">The live response at 22:14 (the switched-to account) carried ${U.limits.length} meters: ${limitsList}.
  Each had <code>kind</code>, <code>group</code>, <code>percent</code>, <code>severity</code>, <code>resets_at</code>, <code>scope</code> and <code>is_active</code>.
  The response also carried a <code>seven_day_breakdown</code> of ${U.breakdown.map(r => r.display_name + ' ' + r.percent).join(', ')}, and <code>spend</code> and
  <code>extra_usage</code>, both off. The ${B0.tag} build showed the session, <code>weekly_all</code> and the highest <code>weekly_scoped</code>
  row, so a fourth meter or an unknown kind was dropped without a trace. The option rows are that moment as they were
  drawn for the question (the forecast inputs come from the 31 registry samples that survived, and the Cowork meter is hypothetical).</p>
  ${optCard({ letter: 'a', title: 'Leave it as it is', status: 'no', cols: Q3COLS, gutW: 5, lines: q3a.map(c => ({ cells: c })), note: `The ${B0.tag} build. 2 lines; widths ${q3a.map(width).join(' / ')}.` })}
  ${optCard({ letter: 'b', title: 'Every meter the server sends, in its order and severity colours', status: 'no', cols: Q3COLS, gutW: 5, lines: q3b.map(c => ({ cells: c })),
    note: `Built as part of c. One more line per extra meter, in the right column under Fable; line 2 ${widthNote(width(q3a[1]), width(q3b[1]))}.` })}
  ${optCard({ letter: 'c', title: 'b plus the product breakdown, after the account', status: 'chosen', cols: Q3COLS, gutW: 5, lines: q3c.map((c, i) => ({ cells: c, marks: i === 0 ? [mk(bdSpan, 'seven_day_breakdown', 'chg')] : undefined })),
    note: `The mockup the choice was made on. It was built with two changes: the numbers carry a % sign, and being on credit adds a full warning line at the bottom of the block, in place of d.`,
    body: pane({ title: `As built: ${B1.tag}`, tag: 'every meter: superseded by Q4', tagCls: 'bad', cols: Q3COLS, gutW: 5,
      lines: [{ cells: abFour[0], marks: [mk(tailOf(abFour[0]), 'with %', 'chg')] }, { cells: abFour[1] }, { cells: abFour[2], marks: [mk(span(abFour[2], /→ —/), '→ —: see Q4', 'am')] }] })
      + pane({ title: `As built: ${B2.tag}`, tag: 'after Q4', tagCls: 'pick', cols: Q3COLS, gutW: 5,
      lines: [{ cells: nFour[0] }, { cells: nFour[1] }, { cells: nFour[2], marks: [mk(noticeSpan(nFour[2]), 'ignored and flagged: Q4', 'am')] }] })
      + pane({ cols: Q3COLS, gutW: 5, lines: [{ cells: nCS[2], marks: [mk(warnSpan(nCS[2]), 'on credit: see Q5', 'rd')] }] })
      + `<p class="opt-w">The fourth-meter fixture drawn by both builds, then the credit row. The breakdown and the credit alarm are unchanged since the ${B1.tag} build; the fourth meter is no longer drawn but flagged (Q4).</p>` })}
  ${optCard({ letter: 'd', title: 'b plus a credits line, only while spend or extra usage is on', status: 'no', lines: [...q3b.map(c => ({ cells: c, gut: 'off' })), ...q3b.map(c => ({ cells: c, gut: 'on' })), { cells: credits, gut: 'on' }], gutW: 5, cols: Q3COLS,
    note: `Replaced by the ⚠ row: being on credit is an alarm, not a meter.` })}
  <p class="recline"><b>Decided: c, extended, implemented on 2026-09-24.</b> The breakdown reads
  <code>CC 99% · Chat 0% · Cowork 1%</code>, dropping its 0% entries and then itself when room runs out, and being on credit
  gets a red <code>⚠</code> row of its own, always last. The part that drew every meter, with <code>→ —</code> on a meter no history
  series followed, was <b>superseded</b> the same morning by Q4: the three known meters are drawn and any other is
  flagged.</p>
</div>`;
// ---- Q4 to Q6, decided 2026-09-24
// Q4: the option rows are data.asbuilt's capture (a) and the port on the same inputs with that one
// meter changed (b to d); the chosen rows are data.asbuilt2's captures.
const four = abc('fourth');
const q4row = patch => port(four.rows.map((r, i) => i === 3 ? { ...r, ...patch } : r), four.fixture.email, four.plan, abOpt(four)).lines.map(toCells);
const q4a = abFour, q4b = q4row({ trend: true, gated: true }), q4c = q4row({ trend: true, gated: false, rate: 0 }),
      q4d = q4row({ trend: true, gated: false, rate: 1.2 });
const Q4M = (cells, text, tone) => [mk(span(cells, /→ \S+/, colText(cells).indexOf('Cowork')), text, tone)];
const q4w = cells => cells.map(width).join(' / ');
const Q4COLS = fit([...q4a, ...q4b, ...q4c, ...q4d, ...nFour, ...nMany].map(c => ({ cells: c })));
const secQ4 = `
<div class="q" id="q4"><h3 class="qh">Q4: What should → show for a meter the history doesn't track? ${DECIDED('decided, 2026-09-24')}</h3>
  <p class="primer">The history keeps three trend series: the session, <code>weekly_all</code> and one scoped meter. The
  ${B1.tag} build drew every meter the server sent, so a fourth meter, or a second scoped one, was drawn with no trend at all: no rate, so
  no forecast. It drew that <code>→</code> as <code>—</code>, the sentinel for a source that reported nothing, and its
  <code>⇢</code> bar at the current value. No account has sent a fourth meter yet; the rows use the fixture's hypothetical
  Cowork meter at 12%.</p>
  ${optCard({ letter: 'e', title: 'Draw the three known meters, and flag any other for review', status: 'chosen', cols: Q4COLS, gutW: 5,
    lines: [...nFour.map((c, i) => ({ cells: c, gut: i === 0 ? 'one' : '', marks: i === 2 ? [mk(noticeSpan(c), 'not drawn: named, in amber', 'am')] : undefined })),
            ...nMany.map((c, i) => ({ cells: c, gut: i === 0 ? 'many' : '', marks: i === 2 ? [mk(moreSpan(c), 'what fits, then +N more', 'am')] : undefined }))],
    note: `As deployed on ${B2.when}, and none of a to d: the status line keeps working for the three meters it knows and leaves any other meter out until the code has been reviewed for it. The session, <code>weekly_all</code> and one model-scoped <code>weekly_scoped</code> (the one the history follows, else the highest) are drawn, each with its own series. Anything else is not drawn but named in an amber <code>⚠</code> row, above the credit alarm. <code>→ —</code> and the extra right-column lines went with it. Widths ${q4w(nFour)} and ${q4w(nMany)}.` })}
  ${optCard({ letter: 'a', title: '<code>—</code>', status: 'no', cols: Q4COLS, gutW: 5, lines: q4a.map((c, i) => ({ cells: c, marks: i === 2 ? Q4M(c, 'no series, nothing to say', 'am') : undefined })),
    note: `As deployed on ${B1.when}, and superseded by e. Widths ${q4w(q4a)}. It was the one recommended.` })}
  ${optCard({ letter: 'b', title: '<code>early</code>', status: 'no', cols: Q4COLS, gutW: 5, lines: q4b.map((c, i) => ({ cells: c, marks: i === 2 ? Q4M(c, 'promises a trend', 'am') : undefined })),
    note: `Widths ${q4w(q4b)}. <code>early</code> says that a trend is coming once there is enough data, and for this meter none ever came.` })}
  ${optCard({ letter: 'c', title: '<code>never</code>', status: 'no', cols: Q4COLS, gutW: 5, lines: q4c.map((c, i) => ({ cells: c, marks: i === 2 ? Q4M(c, 'asserts: not burning', 'am') : undefined })),
    note: `Widths ${q4w(q4c)}. <code>never</code> asserts the meter is not burning, which was unknown.` })}
  ${optCard({ letter: 'd', title: 'Track every meter', status: 'no', cols: Q4COLS, gutW: 5,
    lines: [...q4b.map((c, i) => ({ cells: c, gut: i === 2 ? 'new' : '' })), ...q4d.map((c, i) => ({ cells: c, gut: i === 2 ? 'then' : '' }))],
    note: `Its own series: <code>early</code> for the first ten minutes, then a real trend, here a hypothetical 1,2 %/h. Widths then ${q4w(q4d)}. The cost is in the forecast: <code>hist</code> holds one field per series, so it would become one series per meter with its own window checks.` })}
  <p class="recline"><b>Decided: e, ignored and flagged pending review, on 2026-09-24.</b> A meter this status line has
  never been checked against is no longer drawn on trust; the amber row says which one, and that the code needs a look. This
  supersedes the part of Q3 that drew every meter.</p>
</div>`;
// Q5: a is the capture; b is the same cells in reverse video across the width; c is the capture of a
// maxed row on credit with its → maxed recoloured red.
const csWarn = nCS[2];
const rvBar = [...csWarn, ...Array(COLS - width(csWarn)).fill(0).map(() => ({ ch: ' ', cls: 'df', bold: false, w: 1 }))].map(c => ({ ...c, cls: 'rv', bold: true }));
const redMaxed = (() => { const s = span(nCA[0], /→ maxed/); return nCA[0].map((c, i) => i >= s.col && i < s.col + s.len ? { ...c, cls: 'rd', bold: true } : c); })();
const secQ5 = `
<div class="q" id="q5"><h3 class="qh">Q5: How should the on-credit warning look? ${DECIDED('decided: a')}</h3>
  <p class="primer">Q3 adds a full warning line at the bottom of the block while on credit. The ${B1.tag} build draws it as a red
  <code>⚠</code> row of its own, last in the block, like every other <code>⚠</code> row. It could also be a bar of red across
  the whole width, or the row plus a pointer at the cause.</p>
  ${optCard({ letter: 'a', title: 'Its own red ⚠ row, like the others', status: 'chosen', cols: COLS, lines: nCS.map((c, i) => ({ cells: c, marks: i === 2 ? [mk(warnSpan(c), 'as built', 'rd')] : undefined })),
    note: `As deployed on ${B1.when}, and unchanged in the ${B2.tag} build, which draws it here. +1 line of ${width(csWarn)} columns; nothing else moves.` })}
  ${optCard({ letter: 'b', title: 'A red bar across the whole width', status: 'no', cols: COLS, lines: [{ cells: nCS[0] }, { cells: nCS[1] }, { cells: rvBar }],
    note: `The same text in reverse video (the terminal's own background on the red), padded to ${COLS} columns (<code>term - 4</code>) so the colour reaches the edge. It would have been the loudest thing the status line draws.` })}
  ${optCard({ letter: 'c', title: "a, plus the maxed meter's <code>→ maxed</code> in red while on credit", status: 'no', cols: COLS,
    lines: [{ cells: redMaxed, marks: [mk(span(redMaxed, /→ maxed/), 'red: the cause', 'rd')] }, { cells: nCA[1] }, { cells: nCA[2] }],
    note: `The credit-at-100% fixture as the ${B2.tag} build draws it, with <code>→ maxed</code> recoloured. It points at the cause, for a little more logic.` })}
  <p class="recline"><b>Decided: a, no change.</b> The alarm stays its own red row, as the ${B1.tag} build draws it.</p>
</div>`;
// Q6: the chosen rows are data.asbuilt2's capture; a to c are the port on the same inputs (the port
// reproduces data.asbuilt's capture of the same rows); d's on-credit state is data.asbuilt's own capture.
const cOff = ab2c('credit-off-at100');
const q6port = opt => port(cOff.rows, cOff.fixture.email, cOff.plan, { ...abOpt(cOff), ...opt }).lines.map(toCells);
const q6a = q6port({}), q6b = q6port({ maxed: 'cap' }), q6c = q6port({ maxed: 'hide' });
const Q6COLS = fit([...nCOff, ...q6a, ...q6b, ...q6c, ...abCA].map(c => ({ cells: c })));
const q6w = cells => `${width(cells[0])} / ${width(cells[1])}`;
const secQ6 = `
<div class="q" id="q6"><h3 class="qh">Q6: A maxed meter still shows a projection ${DECIDED('decided, 2026-09-24')}</h3>
  <p class="primer">When a meter reaches 100%, <code>→</code> shows <code>maxed</code>, but <code>⇢</code> could still project far
  above it: <code>⇢ 204%</code> in the credit fixture. The projection is the last hour's pace, measured while climbing to 100%.
  Once the meter is blocked, the counter stops rising, the pace fades and <code>⇢</code> drifts back towards 100% as the hour fills
  with flat samples. Until then it shows demand that cannot be acted on, and <code>↻</code>, the time until the window resets, is the
  useful number. The behaviour predates these changes; it was noticed while testing Q3.</p>
  ${optCard({ letter: 'e', title: 'A variant of a: keep ⇢, greyed out like a disabled control', status: 'chosen', cols: Q6COLS, gutW: 4,
    lines: [{ cells: nCOff[0], marks: [mk(segOf(nCOff[0]), 'every cell dim', 'same')] }, { cells: nCOff[1] }],
    note: `As deployed on ${B2.when}: a, with the segment greyed out to show that it does not apply, like a disabled control. The glyph, every bar cell including the ✗ marks, and the percentage are drawn dim; the row keeps its shape and the pace stays readable. Widths ${q6w(nCOff)}, the same as a's: only colours change.` })}
  ${optCard({ letter: 'a', title: 'Keep it', status: 'no', cols: Q6COLS, gutW: 4, lines: [{ cells: q6a[0], marks: [mk(segOf(q6a[0]), 'demand past the limit', 'am')] }, { cells: q6a[1] }],
    note: `As the ${B1.tag} build drew it. Widths ${q6w(q6a)}. It shows how hard the meter was pushed, and reads oddly beside <code>maxed</code>. e starts from this one.` })}
  ${optCard({ letter: 'b', title: 'Cap ⇢ at 100% when maxed', status: 'no', cols: Q6COLS, gutW: 4, lines: [{ cells: q6b[0], marks: [mk(segOf(q6b[0]), 'capped', 'am')] }, { cells: q6b[1] }],
    note: `Widths ${q6w(q6b)}. True, but it repeats <code>maxed</code>.` })}
  ${optCard({ letter: 'c', title: 'Hide ⇢ when maxed, leaving ↻ to carry the message', status: 'no', cols: Q6COLS, gutW: 4, lines: [{ cells: q6c[0] }, { cells: q6c[1] }],
    note: `Widths ${q6w(q6c)}: the segment is left blank, so nothing after it moves. The cleanest option, and the recommended one, but the burst is no longer visible.` })}
  ${optCard({ letter: 'd', title: 'As c, except while on credit, when ⇢ shows how far into paid usage you are heading', status: 'no', cols: Q6COLS, gutW: 4,
    lines: [{ cells: q6c[0], gut: 'off' }, { cells: q6c[1], gut: 'off' }, { cells: abCA[0], gut: 'on' }, { cells: abCA[1], gut: 'on' }, { cells: abCA[2], gut: 'on' }],
    note: `Credits off: as c. Credits on: in colour, as the ${B1.tag} build drew the credit fixture. Useful in the one case where use keeps growing; a little more logic.` })}
  <p class="recline"><b>Decided: e, a variant of a, built on 2026-09-24.</b> A row at 100% keeps its <code>⇢</code> segment,
  greyed; <code>→</code> still reads <code>maxed</code>, or <code>early</code> while there is no trend yet.</p>
</div>`;

// method
const secMethod = `
<section class="sec gapped" id="method">
  <h2>How this page was made, and what it cannot see</h2>
  <table class="states"><tbody>
    <tr><td>Real</td><td>The 19:57 figures (the screenshot and the registry's 61 samples from 19:02:54 to 20:02:39); every row
      labelled ${B0.tag} (bytes from the binary deployed when Q1 to Q3 came up, sha256 ${D.renders.meta.exe_sha256.slice(0, 12)}...),
      ${B1.tag} (deployed after Q2 and Q3, sha256 ${AB.exe_sha256.slice(0, 12)}...) or ${B2.tag} (deployed after Q4 and Q6,
      sha256 ${AB2.exe_sha256.slice(0, 12)}...), the last two drawing the implementation's fixtures, all
      through <code>CSHIP_OFFLINE</code>; and the 22:14 usage response.</td></tr>
    <tr><td>Reconstructed</td><td>The 7d meter from Sat 05:00 to 19:02: this machine's Claude Code usage at list price, scaled to the
      real samples (it reproduced ${P0(F.recon_last_hour_match)} of the last hour's whole percents before they were spliced in). The 5h
      windows come from response times. Severity for past moments (label colour only) is taken as normal below 75%.</td></tr>
    <tr><td>Not visible</td><td>claude.ai on the web, desktop or phone (a chat window was open), cloud sessions and
      routines, and other machines. In the last hour the meter rose 4 points (3.01 to 4.99 after rounding) where this machine's usage predicts
      2.8, so usage of that order may be missing from busy hours. This is one week only, and c's bias depends on the shape of the week.</td></tr>
    <tr><td>Mockups</td><td>Rows for options that were never built come from a port of <code>RenderRows</code>,
      <code>Bar</code>, <code>Hm</code>, <code>BuildAccount</code>, <code>Compose</code> and <code>WithBreakdown</code> as the three builds
      have them. The generator refuses to run unless the port reproduces all ${checks.length} captured renders byte for byte:
      ${Object.keys(D.renders.cases).length} from the ${B0.tag} build, ${Object.keys(AB.cases).length} from the ${B1.tag} build and
      ${Object.keys(AB2.cases).length} from the ${B2.tag} build (their ⚠ rows are shown as captured). Every character is a 1ch cell; 👤 is two. The account addresses, in the
      captures and the port alike, are example.com ones of the same length, so every width is as drawn.</td></tr>
    <tr><td>Colours</td><td>Every mark is a Program.cs literal (${F.palette_check.palette.join(', ')}; forest #28A428 since Q2;
      the amber notice #E0AF68 since Q4; a maxed row's <code>⇢</code> segment in dim #6E738D since Q6),
      on the terminal ground #16161E the template uses. A palette validator passes the chart colours on colour-blind separation
      (worst delta E ${F.palette_check.cvd_worst}), normal vision (${F.palette_check.normal_worst}) and contrast (at least ${F.palette_check.contrast_min}:1).
      It fails them on its lightness band, since every Program.cs hue is lighter than the band on a dark ground, and on the chroma
      floor for the context line. The Program.cs rule wins here.</td></tr>
    <tr><td>Regenerate</td><td><code>node docs/design/limits-decisions.gen.js docs/design/limits-decisions.html</code>, with Node alone; the data is in
      <code>docs/design/limits-decisions.data.json</code>.</td></tr>
  </tbody></table>
</section>`;

// ---- the page
const css = `
:root{
  --bg:#F1F2F8; --surface:#FFFFFF; --sunken:#E8EAF3;
  --ink:#191C29; --ink2:#565D7A;
  --rule:#D6D9E8; --accent:#4A56B8; --accent-soft:#E6E8F8;
  --warn-bg:#FCEFF2; --warn-line:#E4B2BD; --warn-ink:#A82C4C;
  --shadow:0 1px 2px rgba(25,28,41,.06),0 8px 28px rgba(25,28,41,.07);
  --mono:ui-monospace,"Cascadia Mono","Cascadia Code",Consolas,"DejaVu Sans Mono",monospace;
}
@media (prefers-color-scheme: dark){
  :root:not([data-theme="light"]){
    --bg:#101219; --surface:#191C26; --sunken:#0C0E14;
    --ink:#E6E9F5; --ink2:#9AA1BF; --rule:#282C3B;
    --accent:#B4BEFE; --accent-soft:#22273A;
    --warn-bg:#231721; --warn-line:#5C2E3D; --warn-ink:#F7768E;
    --shadow:0 1px 2px rgba(0,0,0,.4),0 10px 34px rgba(0,0,0,.35);
  }
}
:root[data-theme="dark"]{
  --bg:#101219; --surface:#191C26; --sunken:#0C0E14;
  --ink:#E6E9F5; --ink2:#9AA1BF; --rule:#282C3B;
  --accent:#B4BEFE; --accent-soft:#22273A;
  --warn-bg:#231721; --warn-line:#5C2E3D; --warn-ink:#F7768E;
  --shadow:0 1px 2px rgba(0,0,0,.4),0 10px 34px rgba(0,0,0,.35);
}
*{box-sizing:border-box}
body{margin:0; background:var(--bg); color:var(--ink);
  font-family:"Segoe UI",system-ui,-apple-system,"Helvetica Neue",sans-serif;
  font-size:16px; line-height:1.6; -webkit-font-smoothing:antialiased}
.wrap{max-width:1180px; margin:0 auto; padding:56px 28px 88px; display:grid; gap:48px}
code{font-family:var(--mono); font-size:.86em; background:var(--sunken); padding:1px 5px; border-radius:3px; color:var(--ink)}

/* status band: what was decided, and what is still open */
.band{border:1px solid var(--accent); background:var(--accent-soft); border-radius:6px; padding:24px 26px; display:grid; gap:12px}
.band-tag{font-family:var(--mono); font-size:11.5px; letter-spacing:.16em; text-transform:uppercase; font-weight:700; color:var(--accent)}
.band h2{margin:0; color:var(--ink); font-size:19px; line-height:1.3; font-family:"Segoe UI",system-ui,sans-serif;
  font-weight:700; text-transform:none; letter-spacing:-.01em}
.band p{margin:0; color:var(--ink2); font-size:15px; max-width:88ch}
.band ul{margin:0; padding-left:20px; color:var(--ink2); font-size:15px; display:grid; gap:5px}
.band b{color:var(--ink); font-weight:600}
.band a{color:var(--accent)}
.dtab{border-collapse:collapse; font-size:14.5px; width:100%}
.dtab td{padding:6px 14px 6px 0; border-top:1px solid color-mix(in srgb, var(--accent) 25%, transparent); color:var(--ink2); vertical-align:top}
.dtab tr:first-child td{border-top:0}
.dtab td:first-child{font-family:var(--mono); font-weight:700; white-space:nowrap}
.dtab td:nth-child(2){color:var(--ink); font-weight:600}
.dtab td:last-child{white-space:nowrap; font-size:13px}
.dtab tr.open td:nth-child(3) b{color:var(--accent)}

/* the correction band, as in house-style/weekly-wall.html */
.warn{border:1px solid var(--warn-line); background:var(--warn-bg); border-radius:6px; padding:24px 26px; display:grid; gap:12px}
.warn-tag{font-family:var(--mono); font-size:11.5px; letter-spacing:.16em; text-transform:uppercase; font-weight:700; color:var(--warn-ink)}
.warn h2{margin:0; color:var(--warn-ink); font-size:19px; line-height:1.3; font-family:"Segoe UI",system-ui,sans-serif;
  font-weight:700; text-transform:none; letter-spacing:-.01em}
.warn p{margin:0; color:var(--ink2); font-size:15px; max-width:96ch}
.warn p b{color:var(--ink); font-weight:600}
.warn a{color:var(--accent)}
.warn.slim{padding:14px 18px; gap:6px}
.rejected{display:grid; gap:48px; padding:22px 12px; border:1px dashed var(--warn-line); border-radius:8px}
.superseded{display:grid; gap:14px; padding:16px 12px; border:1px dashed var(--warn-line); border-radius:8px}

header{display:grid; gap:14px; max-width:72ch}
.eyebrow{font-family:var(--mono); font-size:11.5px; letter-spacing:.16em; text-transform:uppercase; color:var(--accent); font-weight:600}
h1{font-family:var(--mono); font-size:clamp(30px,4.4vw,44px); line-height:1.08; margin:0; font-weight:700; letter-spacing:-.02em}
.lede{margin:0; color:var(--ink2); font-size:17px}
.lede b{color:var(--ink); font-weight:600}

h2{font-family:var(--mono); font-size:13px; letter-spacing:.14em; text-transform:uppercase; margin:0; font-weight:700; color:var(--ink)}
h3{font-size:16.5px; margin:0; line-height:1.35}
.sec{display:grid}
.gapped{gap:20px}
.note{border-left:2px solid var(--accent); padding:2px 0 2px 18px; color:var(--ink2); font-size:14.5px; max-width:96ch; margin:0}
.note b{color:var(--ink); font-weight:600}

/* terminal panes: devices, dark in both themes */
.pane{display:grid; gap:0; min-width:0}
.phead{display:flex; align-items:baseline; gap:12px; flex-wrap:wrap; padding:0 0 10px}
.ptitle{font-family:var(--mono); font-size:12px; letter-spacing:.14em; text-transform:uppercase; font-weight:700; color:var(--ink)}
.pnote{font-size:13.5px; color:var(--ink2)}
.tag{font-family:var(--mono); font-size:11px; letter-spacing:.06em; font-weight:600; padding:2px 8px; border-radius:3px; white-space:nowrap}
.tag.bad{color:#F7768E; background:rgba(247,118,142,.13); border:1px solid rgba(247,118,142,.3)}
.tag.good{color:#7DCFFF; background:rgba(125,207,255,.11); border:1px solid rgba(125,207,255,.3)}
:root:not([data-theme="dark"]) .tag.good{color:#1F6E97}
:root:not([data-theme="dark"]) .tag.bad{color:#A82C4C}
@media (prefers-color-scheme: dark){ :root:not([data-theme="light"]) .tag.good{color:#7DCFFF} :root:not([data-theme="light"]) .tag.bad{color:#F7768E} }
.tag.pick{color:#A6E3A1; background:rgba(166,227,161,.12); border:1px solid rgba(166,227,161,.35)}
:root:not([data-theme="dark"]) .tag.pick{color:#2B6B31}
@media (prefers-color-scheme: dark){ :root:not([data-theme="light"]) .tag.pick{color:#A6E3A1} }
.tag.no{color:var(--ink2); background:var(--sunken); border:1px solid var(--rule)}
.qh .tag{vertical-align:3px; margin-left:8px}
.term{background:#16161E; border:1px solid #262738; border-radius:6px; padding:16px 16px 18px; overflow-x:auto; box-shadow:var(--shadow);
  font-family:var(--mono); font-size:13px; line-height:1.75}
/* the frame is the terminal's own width, so panes at the same --cols are the same width to the character */
.term-inner{display:block; width:calc(var(--cols) * 1ch); box-shadow:1px 0 0 rgba(110,115,141,.35)}
.ln{white-space:pre; color:#C0CAF5; height:1.75em}
.gut{display:inline-block; width:calc(var(--gut) * 1ch); color:#6E738D; font-style:normal}
.g{display:inline-block; width:1ch; text-align:center; font-style:normal}
.g.w2{width:2ch}
.b{font-weight:700}
.cy{color:#7DCFFF} .or{color:#E0AF68} .rd{color:#F7768E} .dm{color:#6E738D} .gr{color:#A6E3A1} .lg{color:#C6F6C1}
.tx{color:#A9B1D6} .lv{color:#B4BEFE} .df{color:#C0CAF5} .fo{color:#28A428}
.rv{background:#F7768E; color:#16161E}          /* reverse video of the red: Q5 b */
.annot{position:relative; height:12px; margin-top:1px}
.brk{position:absolute; left:calc(var(--c) * 1ch); width:calc(var(--w) * 1ch); height:8px; border:1px solid #E0AF68; border-top:0;
  border-bottom-left-radius:3px; border-bottom-right-radius:3px; opacity:.8}
.brk.chg{border-color:#7DCFFF} .brk.same{border-color:#6E738D} .brk.fo{border-color:#28A428} .brk.rd{border-color:#F7768E}
.alabs{position:relative; height:16px}
.alab{position:absolute; left:calc(var(--c) * 1ch); top:0; font-family:var(--mono); font-size:13px; white-space:nowrap; line-height:1}
.alab span{font-size:10.5px; letter-spacing:.08em; text-transform:uppercase; color:#E0AF68; font-weight:700}
.alab .chg{color:#7DCFFF} .alab .same{color:#6E738D} .alab .fo{color:#28A428} .alab .rd{color:#F7768E}

/* charts: the same device treatment as the terminal */
.viz{background:#16161E; border:1px solid #262738; border-radius:6px; padding:16px 18px 12px; box-shadow:var(--shadow); min-width:0}
.viz svg{display:block; width:100%; height:auto}
.viz .ax{font:12px "Segoe UI",system-ui,sans-serif; fill:#A9B1D6; font-variant-numeric:tabular-nums}
.viz .ax.strong, .viz .strong{fill:#A9B1D6; font-weight:600}
.viz .ax.val{fill:#A9B1D6; font-weight:600}
.viz .ax.sm{font-size:11px}
.viz .glt{font-size:15px}
.viz .num{font:700 11px "Segoe UI",system-ui,sans-serif; fill:#16161E}
.legend{display:flex; flex-wrap:wrap; gap:6px 18px; font-size:13px; color:#A9B1D6; margin:0 0 8px}
.legend .key{display:inline-block; width:18px; border-top:2px solid; vertical-align:middle; margin-right:7px}
.legend .sw, .states .sw{display:inline-block; width:11px; height:11px; border-radius:2px; vertical-align:-1px; margin-right:7px}
.vizhelp{margin:6px 0 0; font-size:12.5px; color:#6E738D}
.plot{position:relative; outline:none}
.plot:focus-visible{box-shadow:0 0 0 2px #7DCFFF; border-radius:4px}
.tip{position:absolute; top:8px; background:#16161E; border:1px solid #6E738D; border-radius:4px; padding:8px 10px; font-size:12.5px;
  color:#A9B1D6; pointer-events:none; min-width:180px; box-shadow:0 6px 18px rgba(0,0,0,.45)}
.tip b{display:block; color:#A9B1D6; font-weight:600; margin-bottom:4px}
.tip div{display:flex; justify-content:space-between; gap:14px; font-variant-numeric:tabular-nums}
.tip i{display:inline-block; width:14px; border-top:2px solid; vertical-align:middle; margin-right:6px}
.tip strong{font-weight:700}

.stack{display:grid; gap:14px}
.two{display:grid; grid-template-columns:repeat(auto-fit,minmax(460px,1fr)); gap:18px; align-items:start}
.states{width:100%; border-collapse:collapse; font-size:14.5px}
.states th{text-align:left; font-family:var(--mono); font-size:10.5px; letter-spacing:.13em; text-transform:uppercase; color:var(--ink2);
  font-weight:700; padding:0 14px 8px 0; border-bottom:1px solid var(--rule)}
.states td{padding:10px 14px 10px 0; border-bottom:1px solid var(--rule); color:var(--ink2); vertical-align:top; font-variant-numeric:tabular-nums}
.states tr:last-child td{border-bottom:0}
.states td:first-child{color:var(--ink); font-weight:600; white-space:nowrap}
.states td b{color:var(--ink)}
.states.compact td{padding:7px 14px 7px 0; font-size:14px}
.dimtxt{color:var(--ink2); opacity:.8; font-size:.9em}
.bad-t{color:var(--warn-ink); font-weight:600}
.mono{font-family:var(--mono); font-size:13px}
.gl{font-family:var(--mono); font-size:1.28em; line-height:0; vertical-align:-.03em}
.numchip{display:inline-block; width:18px; height:18px; line-height:18px; border-radius:50%; background:#A9B1D6; color:#16161E;
  font:700 11px "Segoe UI",system-ui,sans-serif; text-align:center; margin-right:6px}
details.tv{border:1px solid var(--rule); border-radius:6px; padding:10px 14px; background:var(--surface)}
details.tv summary{cursor:pointer; font-size:14px; color:var(--ink2); font-weight:600}
details.tv table{margin-top:10px}
details.tv[open] > table{display:table}
.tv tbody{font-variant-numeric:tabular-nums}

.dn{display:grid; gap:12px; padding:22px 0 0; border-top:1px solid var(--rule)}
.dn p{margin:0; color:var(--ink2); font-size:15px; max-width:100ch}
.dn p b{color:var(--ink)}
.q{display:grid; gap:16px; padding:26px 0 0; border-top:1px solid var(--rule)}
.qh{font-size:19px}
.primer{margin:0; color:var(--ink2); font-size:15px; max-width:100ch}
.primer b{color:var(--ink)}
.opt{display:grid; gap:8px; background:var(--surface); border:1px solid var(--rule); border-radius:6px; padding:14px 16px 12px}
.opt.rec{border-color:var(--accent); box-shadow:0 0 0 1px var(--accent)}
.opt-h{display:flex; align-items:center; gap:10px; flex-wrap:wrap}
.letter{font-family:var(--mono); font-weight:700; font-size:13px; width:24px; height:24px; line-height:24px; text-align:center;
  border-radius:4px; background:var(--sunken); color:var(--ink)}
.opt-t{font-weight:600; font-size:15px}
.opt-w{margin:0; font-size:13.5px; color:var(--ink2)}
.opt .term{box-shadow:none}
.recline{margin:0; font-size:15px; color:var(--ink2); border-left:2px solid var(--accent); padding-left:16px}
.recline b{color:var(--ink)}
@media (prefers-reduced-motion:reduce){*{animation:none!important;transition:none!important}}
`;
const script = `
(() => {
  const D = JSON.parse(document.getElementById('week-data').textContent);
  const dec = r => r.flatMap(([v, n]) => Array(n).fill(v));
  const S = { api: dec(D.api), today: dec(D.today), c: dec(D.c), lookback: dec(D.lookback) };
  const g = D.geo, svg = document.getElementById('week'), xh = document.getElementById('xh'),
        tip = document.getElementById('tip'), plot = svg.parentNode;
  const days = ['Sun','Mon','Tue','Wed','Thu','Fri','Sat'];
  const lbl = t => { const d = new Date((t + 7200) * 1000); return days[d.getUTCDay()] + ' ' + d.toISOString().slice(11, 16); };
  const rows = [['weekly %', 'api', '#C6F6C1'], ['⇢ today', 'today', '#F7768E'], ['⇢ c', 'c', '#7DCFFF'], ['⇢ 24 h', 'lookback', '#E0AF68']];
  let k = g.n - 1;
  function show(i) {
    k = Math.max(0, Math.min(g.n - 1, i));
    const t = g.T0 + k * g.STEP, x = g.x0 + (t - g.WS) / (g.tEnd - g.WS) * (g.x1 - g.x0);
    xh.setAttribute('x1', x); xh.setAttribute('x2', x); xh.setAttribute('visibility', 'visible');
    tip.replaceChildren();
    const h = document.createElement('b'); h.textContent = lbl(t); tip.appendChild(h);
    for (const [name, key, col] of rows) {
      const r = document.createElement('div'), a = document.createElement('span'), i = document.createElement('i'), v = document.createElement('strong');
      i.style.borderTopColor = col; a.appendChild(i);
      if (name.startsWith('⇢')) { const gl = document.createElement('span'); gl.className = 'gl'; gl.textContent = '⇢'; a.appendChild(gl); a.appendChild(document.createTextNode(name.slice(1))); }
      else a.appendChild(document.createTextNode(name));
      v.textContent = S[key][k] + '%';
      r.appendChild(a); r.appendChild(v); tip.appendChild(r);
    }
    tip.hidden = false;
    const px = x / g.Wd * svg.getBoundingClientRect().width;
    tip.style.left = (px > plot.clientWidth - 220 ? px - 200 : px + 12) + 'px';
  }
  function fromEvent(e) {
    const r = svg.getBoundingClientRect(), x = (e.clientX - r.left) / r.width * g.Wd;
    const t = g.WS + (x - g.x0) / (g.x1 - g.x0) * (g.tEnd - g.WS);
    show(Math.round((t - g.T0) / g.STEP));
  }
  svg.addEventListener('pointermove', fromEvent);
  svg.addEventListener('pointerleave', () => { if (document.activeElement !== plot) { tip.hidden = true; xh.setAttribute('visibility', 'hidden'); } });
  plot.addEventListener('focus', () => show(k));
  plot.addEventListener('blur', () => { tip.hidden = true; xh.setAttribute('visibility', 'hidden'); });
  plot.addEventListener('keydown', e => {
    const s = e.shiftKey ? 60 : 10;
    if (e.key === 'ArrowLeft') { show(k - s); e.preventDefault(); }
    if (e.key === 'ArrowRight') { show(k + s); e.preventDefault(); }
  });
})();`;

const DECISIONS = [
  ['q1', 'Q1', 'Switch the weekly rows to c?', '<b>Rejected.</b> Keep the 60-minute pace for <code>→</code> and <code>⇢</code>.', 'decided 2026-09-23, recorded in rejected-designs.md'],
  ['q2', 'Q2', "What → shows when 100% would come after the row's own reset", '<b>e.</b> Keep the time; draw <code>→</code> and the time in forest green <code>#28A428</code>.', `deployed ${B1.tag}`],
  ['q3', 'Q3', 'Show more of what the usage API returns?', '<b>c, extended.</b> <code>CC 99% · Chat 0% · Cowork 1%</code> after the account; a red <code>⚠</code> row when on credit. The part that drew every meter was superseded by Q4.', `deployed ${B1.tag}`],
  ['q4', 'Q4', "What should → show for a meter the history doesn't track?", '<b>e, none of a to d.</b> Draw only the three known meters; name any other in an amber <code>⚠</code> row, pending review.', `deployed ${B2.tag}`],
  ['q5', 'Q5', 'How should the on-credit warning look?', '<b>a.</b> Its own red <code>⚠</code> row, as built.', 'no change, 2026-09-24'],
  ['q6', 'Q6', 'A maxed meter still shows a projection', '<b>e, a variant of a.</b> Keep <code>⇢</code>, drawn wholly dim like a disabled control.', `deployed ${B2.tag}`],
];
const html = `<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>Limit rows: decisions of 23 and 24 September 2026</title>
<style>${css}</style></head>
<body><div class="wrap">
  <div class="band">
    <div class="band-tag">Decided 23 and 24 Sep 2026, nothing pending</div>
    <h2>The forecast stays as it is. What changed: a harmless → is drawn in forest green, and only the three known meters get a row, with any other flagged for review.</h2>
    <table class="dtab"><tbody>
    ${DECISIONS.map(([id, q, text, what, when]) => `<tr class="${when === 'pending' ? 'open' : ''}"><td>${id ? `<a href="#${id}">${q}</a>` : q}</td><td>${esc(text)}</td><td>${what}</td><td>${when}</td></tr>`).join('\n    ')}
    </tbody></table>
    <p>Rows labelled with a time are what the binary deployed at that time drew: <b>${B0.tag}</b> is the one deployed when
    Q1 to Q3 came up, <b>${B1.tag}</b> the one after Q2 and Q3 (superseded in part), and <b>${B2.tag}</b> the one after Q4
    and Q6. Every other row is an option drawn by the same cell rules. The limit model is
    in <a href="../reference/limits.md">limits.md</a>; the house style is <a href="house-style/README.md">house-style/README.md</a>.</p>
  </div>

  <header>
    <div class="eyebrow">Decision record for the limit rows, 23 and 24 Sep 2026</div>
    <h1>Limit-row decisions</h1>
    <p class="lede">At 19:57 on 23 September the 7d row said <b>100% in 4h35m</b> and <b>226% at the reset</b>, both from the
    pace of one busy hour. What to do about that, and about the other questions it led to on the same rows, was decided over the next hours.
    This page is the record: what was built, the design that was rejected together with the replay it was judged on, and every
    option as it was drawn. Nothing on it is pending.</p>
  </header>
  ${secAsBuilt}
  <section class="rejected" id="rejected-c">
    <div class="warn">
      <div class="warn-tag">Rejected design, not the product</div>
      <h2>Everything in this block describes option c, which was rejected on 23 September 2026.</h2>
      <p>c would have taken <code>⇢</code> on the weekly rows from the window's own average pace instead of the last 60 minutes.
      The replay below found it <b>steadier and more accurate</b>, but <b>slower to react</b>, under-forecasting a week that
      sped up, and drawing an <code>→</code> and a <code>⇢</code> that disagreed on the same row ${P0(DG.A_share + DG.B_share)} of the
      time. The decision was to keep the current behaviour. Nothing below was built; it stays here so the design does not have to be
      worked out again. The record is in <a href="rejected-designs.md#rejected-a-window-average-projection-option-c">rejected-designs.md</a>.</p>
    </div>
    ${secChange}
    ${secWeek}
    ${secAcc}
    ${secDown}
    ${secFlicker}
    ${secBuild}
  </section>
  <section class="sec gapped" id="decisions"><h2>Decisions</h2>
  ${secQ1}
  ${secQ2}
  ${secQ3}
  ${secQ4}
  ${secQ5}
  ${secQ6}
  </section>
  ${secMethod}
</div>
<script type="application/json" id="week-data">${wk.blob}</script>
<script>${script}</script>
</body></html>
`;

// Outside the 1ch cells, draw ⇢ a size up so its dashes stay visible. Cells, scripts and the
// tooltip are left alone; SVG text gets a tspan instead of a span.
function glyphs(h) {
  return h.split(/(<i class="g[^"]*">[^<]*<\/i>|<script[\s\S]*?<\/script>|<svg[\s\S]*?<\/svg>|<title>[\s\S]*?<\/title>)/).map(part => {
    if (part.startsWith('<i class="g') || part.startsWith('<script') || part.startsWith('<title')) return part;
    if (part.startsWith('<svg')) return part.replace(/(<text[^>]*>)([^<]*⇢[^<]*)(<\/text>)/g, (m, a, t, b) => a + t.replace(/⇢/g, '<tspan class="glt">⇢</tspan>') + b)
                                             .replace(/(<tspan class="strong">[^<]*<\/tspan>[^<]*)⇢/g, (m, a) => a + '<tspan class="glt">⇢</tspan>');
    return part.replace(/⇢/g, '<span class="gl">⇢</span>');
  }).join('');
}
const outPath = process.argv[2] || path.join(__dirname, 'limits-decisions.html');
fs.writeFileSync(outPath, glyphs(html), 'utf8');
// ---- report
console.log('written', outPath, (Buffer.byteLength(html) / 1024).toFixed(0) + ' KB');
console.log(`port reproduces ${checks.filter(c => c[1]).length}/${checks.length} captured binary renders byte for byte`);
const tooWide = LINES.filter(l => l.cols > COLS);
console.log(`status-line rows drawn: ${LINES.length}; widest ${Math.max(...LINES.map(l => l.cols))} columns (limit ${COLS}); over the limit: ${tooWide.length}`);
