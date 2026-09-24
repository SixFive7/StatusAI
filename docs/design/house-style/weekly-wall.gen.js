// Generates the artifact HTML. Every terminal character becomes a fixed 1ch cell so the
// column arithmetic in the mockup matches what RenderRows actually emits. From this folder:
//
//   node weekly-wall.gen.js weekly-wall.html
//
// A style reference only: the design it draws was reverted. Read README.md here first.
const fs = require('fs');

const esc = s => s.replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;');

// [text, class] pairs -> one .g span per character
function cells(spec) {
  let out = '';
  for (const [text, cls] of spec)
    for (const ch of text) out += `<i class="g ${cls}">${esc(ch)}</i>`;
  return out;
}
const rep = (n, ch, cls) => [ch.repeat(n), cls];

// ── bars ───────────────────────────────────────────────────────────────────────
// now bars (identical before/after)
const NOW_5H  = [rep(3,'●','cy'), rep(7,'○','dm')];                 // 34%
const NOW_7D  = [rep(7,'●','cy'), rep(1,'●','or'), rep(2,'○','dm')]; // 81%
const NOW_FB  = [rep(2,'●','cy'), rep(8,'○','dm')];                 // 21%

// today's projection bars (cap 16)
const PRJ_5H_OLD = [rep(7,'●','cy'), rep(2,'●','or'), rep(1,'●','rd'), rep(6,'✗','rd')];      // 161%
const PRJ_7D_OLD = [rep(7,'●','cy'), rep(2,'●','or'), rep(7,' ','dm')];                        // 90%
const PRJ_FB_OLD = [rep(2,'●','cy'), rep(8,'○','dm'), rep(6,' ','dm')];                        // 21%

// shipped projection bars (cap 17). Only the session row is walled — both weekly rows
// reset *with* the wall, so they draw exactly as before.
const PRJ_5H_NEW = [rep(7,'●','cy'), rep(1,'●','or'), rep(1,'┃','wl'), rep(2,'●','gh'), rep(6,'✗','gh')];
const PRJ_7D_NEW = [rep(7,'●','cy'), rep(2,'●','or'), rep(8,' ','dm')];
const PRJ_FB_NEW = [rep(2,'●','cy'), rep(8,'○','dm'), rep(7,' ','dm')];

const SP = [' ', 'df'];

// ── rows ───────────────────────────────────────────────────────────────────────
// label, nowBar, now%, resetGlyph+value, etaGlyph+value, projBar, proj%
function row({lbl, lblCls, nowBar, now, nowCls, reset, eta, etaGlyph, etaCls, proj, projBar, projCls}) {
  return [
    [lbl, lblCls], SP,
    ...nowBar, SP,
    [now, nowCls], ['%', nowCls], SP,
    ['↻','gr'], SP, [reset,'lg'], SP,
    [etaGlyph, etaCls], SP, [eta, etaCls], SP,
    ['⇢','dm'], SP,
    ...projBar, SP,
    [proj, projCls], ['%', projCls],
  ];
}

const OLD_5H = row({lbl:'5h', lblCls:'df', nowBar:NOW_5H, now:'34', nowCls:'cy',
  reset:'3h41m', etaGlyph:'→', eta:'1h54m', etaCls:'rd', projBar:PRJ_5H_OLD, proj:'161', projCls:'rd'});
const OLD_7D = row({lbl:'7d', lblCls:'or', nowBar:NOW_7D, now:'81', nowCls:'or',
  reset:'1h11m', etaGlyph:'→', eta:'2h34m', etaCls:'dm', projBar:PRJ_7D_OLD, proj:' 90', projCls:'dm'});
const OLD_FB = row({lbl:'Fable', lblCls:'df', nowBar:NOW_FB, now:'21', nowCls:'cy',
  reset:'1h11m', etaGlyph:'→', eta:'never', etaCls:'dm', projBar:PRJ_FB_OLD, proj:' 21', projCls:'dm'});

const NEW_5H = row({lbl:'5h', lblCls:'df', nowBar:NOW_5H, now:'34', nowCls:'cy',
  reset:'3h41m', etaGlyph:'⇥', eta:'1h11m', etaCls:'wl', projBar:PRJ_5H_NEW, proj:'75', projCls:'dm'});
const NEW_7D = row({lbl:'7d', lblCls:'or', nowBar:NOW_7D, now:'81', nowCls:'or',
  reset:'1h11m', etaGlyph:'→', eta:'2h34m', etaCls:'dm', projBar:PRJ_7D_NEW, proj:'90', projCls:'dm'});
const NEW_FB = row({lbl:'Fable', lblCls:'df', nowBar:NOW_FB, now:'21', nowCls:'cy',
  reset:'1h11m', etaGlyph:'→', eta:'never', etaCls:'dm', projBar:PRJ_FB_NEW, proj:'21', projCls:'dm'});

const GAP = [['  ','df']];                       // the 2-column gap from Compose()
const acct = `<span class="acct"><span class="em">👤</span> <i class="tx">someone@example.com</i> <i class="dm">· Max 20</i></span>`;

const IND = [[' ', 'df']];   // Compose() indents the composed line once, not each row
const line2 = (a, b) => `<div class="ln">${cells([...IND, ...a, ...GAP, ...b])}</div>`;

// ── annotation brackets (character columns, measured against the specs above) ───
function annot(marks) {
  const brk = marks.map(m =>
    `<span class="brk" style="--c:${m.col};--w:${m.len}"></span>`).join('');
  const lbl = marks.map(m =>
    `<span class="alab" style="--c:${m.col}"><span>${m.text}</span></span>`).join('');
  return `<div class="annot">${brk}</div><div class="alabs">${lbl}</div>`;
}

const html = `<title>Weekly Wall — Style Template</title>
<style>
:root{
  --bg:#F1F2F8; --surface:#FFFFFF; --sunken:#E8EAF3;
  --ink:#191C29; --ink2:#565D7A;
  --rule:#D6D9E8; --accent:#4A56B8; --accent-soft:#E6E8F8;
  --warn-bg:#FCEFF2; --warn-line:#E4B2BD; --warn-ink:#A82C4C;
  --shadow:0 1px 2px rgba(25,28,41,.06),0 8px 28px rgba(25,28,41,.07);
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
body{
  margin:0; background:var(--bg); color:var(--ink);
  font-family:"Segoe UI",system-ui,-apple-system,"Helvetica Neue",sans-serif;
  font-size:16px; line-height:1.6; -webkit-font-smoothing:antialiased;
}
.wrap{max-width:1180px; margin:0 auto; padding:56px 28px 88px;
  display:grid; gap:44px;}

/* ── correction band ────────────────────────────────────────────────── */
/* Every property the global h2/p rules also set is restated here, so the band
   never inherits the uppercase mono heading treatment used by the sections. */
.warn{
  border:1px solid var(--warn-line); background:var(--warn-bg);
  border-radius:6px; padding:24px 26px; display:grid; gap:14px;
}
.warn-tag{
  font-family:var(--mono); font-size:11.5px; letter-spacing:.16em;
  text-transform:uppercase; font-weight:700; color:var(--warn-ink);
}
.warn h2{
  margin:0; color:var(--warn-ink); font-size:19px; line-height:1.3;
  font-family:"Segoe UI",system-ui,sans-serif; font-weight:700;
  text-transform:none; letter-spacing:-.01em; text-wrap:balance;
}
.warn p{margin:0; color:var(--ink2); font-size:15px; max-width:80ch; text-wrap:pretty}
.warn p b, .warn li b{color:var(--ink); font-weight:600}
.warn ul{margin:0; padding-left:20px; color:var(--ink2); font-size:15px;
  display:grid; gap:7px; max-width:80ch}
.warn code{font-family:var(--mono); font-size:13px; background:var(--sunken);
  padding:1px 5px; border-radius:3px; color:var(--ink)}
.warn a{color:var(--accent); text-underline-offset:2px}

/* ── header ─────────────────────────────────────────────────────────── */
header{display:grid; gap:14px; max-width:64ch}
.eyebrow{
  font-family:var(--mono); font-size:11.5px; letter-spacing:.16em;
  text-transform:uppercase; color:var(--accent); font-weight:600;
}
h1{
  font-family:var(--mono); font-size:clamp(30px,4.4vw,44px); line-height:1.08;
  margin:0; font-weight:700; letter-spacing:-.02em; text-wrap:balance;
}
.lede{margin:0; color:var(--ink2); font-size:17px; text-wrap:pretty}
.lede b{color:var(--ink); font-weight:600}

/* ── terminal panes ─────────────────────────────────────────────────── */
:root{--mono:ui-monospace,"Cascadia Mono","Cascadia Code",Consolas,"DejaVu Sans Mono",monospace}
section{display:grid; gap:0}
.phead{
  display:flex; align-items:baseline; gap:14px; flex-wrap:wrap;
  padding:0 0 12px;
}
.ptitle{
  font-family:var(--mono); font-size:12px; letter-spacing:.14em;
  text-transform:uppercase; font-weight:700; color:var(--ink);
}
.pnote{font-size:13.5px; color:var(--ink2)}
.tag{
  font-family:var(--mono); font-size:11px; letter-spacing:.06em; font-weight:600;
  padding:2px 8px; border-radius:3px; white-space:nowrap;
}
.tag.bad{color:#F7768E; background:rgba(247,118,142,.13); border:1px solid rgba(247,118,142,.3)}
.tag.good{color:#7DCFFF; background:rgba(125,207,255,.11); border:1px solid rgba(125,207,255,.3)}

/* the pane is a device, not page chrome — it keeps the terminal's own world in both themes */
.term{
  background:#16161E; border:1px solid #262738; border-radius:6px;
  padding:18px 16px 20px; overflow-x:auto; box-shadow:var(--shadow);
  font-family:var(--mono); font-size:13px; line-height:1.75;
}
.term-inner{display:inline-block; min-width:max-content}
.ln{white-space:pre; color:#C0CAF5}
.g{display:inline-block; width:1ch; text-align:center; font-style:normal}
.cy{color:#7DCFFF} .or{color:#E0AF68} .rd{color:#F7768E; font-weight:700}
.dm{color:#6E738D} .gr{color:#A6E3A1} .lg{color:#C6F6C1}
.tx{color:#A9B1D6; font-style:normal} .df{color:#C0CAF5}
.wl{color:#E0AF68; font-weight:700}          /* wall mark — inherits 7d severity */
.gh{color:#3E4257}                            /* cancelled trajectory */
.acct{color:#A9B1D6; white-space:pre}
.acct i{font-style:normal}
.em{font-family:"Segoe UI Emoji",var(--mono)}

/* ── annotation ─────────────────────────────────────────────────────── */
.annot{position:relative; height:13px; margin-top:2px}
.brk{
  position:absolute; left:calc(var(--c) * 1ch); width:calc(var(--w) * 1ch);
  height:9px; border:1px solid #E0AF68; border-top:0;
  border-bottom-left-radius:3px; border-bottom-right-radius:3px; opacity:.75;
}
/* font-size stays at the terminal's 13px so ch resolves to the same cell the
   bars use — the visible label is sized on the inner span instead */
.alabs{position:relative; height:17px}
.alab{
  position:absolute; left:calc(var(--c) * 1ch); top:0;
  font-family:var(--mono); font-size:13px; white-space:nowrap; line-height:1;
}
.alab span{
  font-size:10.5px; letter-spacing:.1em; text-transform:uppercase;
  color:#E0AF68; font-weight:700;
}

/* ── legend ─────────────────────────────────────────────────────────── */
.marks{display:grid; grid-template-columns:repeat(auto-fit,minmax(310px,1fr)); gap:1px;
  background:var(--rule); border:1px solid var(--rule); border-radius:6px; overflow:hidden}
.mark{background:var(--surface); padding:20px 22px; display:grid; gap:9px; align-content:start}
.mark-h{display:flex; align-items:center; gap:10px}
.chip{
  font-family:var(--mono); font-size:15px; font-weight:700; line-height:1;
  background:#16161E; color:#C0CAF5; padding:7px 9px; border-radius:4px;
  border:1px solid #2A2B3D; white-space:nowrap;
}
.chip i{font-style:normal}
.chip .gh{color:#3E4257}
.mark-t{font-family:var(--mono); font-size:12.5px; font-weight:700; letter-spacing:.04em}
.mark p{margin:0; font-size:14.5px; color:var(--ink2); text-wrap:pretty}
.mark p b{color:var(--ink); font-weight:600}
.mark code{font-family:var(--mono); font-size:13px; background:var(--sunken);
  padding:1px 5px; border-radius:3px; color:var(--ink)}

/* ── states table ───────────────────────────────────────────────────── */
.states{width:100%; border-collapse:collapse; font-size:14.5px}
.states th{
  text-align:left; font-family:var(--mono); font-size:10.5px; letter-spacing:.13em;
  text-transform:uppercase; color:var(--ink2); font-weight:700;
  padding:0 16px 10px 0; border-bottom:1px solid var(--rule);
}
.states td{padding:13px 16px 13px 0; border-bottom:1px solid var(--rule);
  color:var(--ink2); vertical-align:top}
.states tr:last-child td{border-bottom:0}
.states td:first-child{color:var(--ink); font-weight:600;
  font-family:var(--mono); font-size:13px; white-space:nowrap}
.states td b{color:var(--ink); font-weight:600}
.mini{font-family:var(--mono); font-size:12.5px; background:#16161E; color:#C0CAF5;
  padding:3px 7px; border-radius:3px; white-space:nowrap; display:inline-block}
.mini .wl{color:#E0AF68} .mini .dm{color:#6E738D} .mini .gr{color:#A6E3A1}
.mini .lg{color:#C6F6C1} .mini .gh{color:#3E4257} .mini .cy{color:#7DCFFF}

.note{
  border-left:2px solid var(--accent); padding:2px 0 2px 18px;
  color:var(--ink2); font-size:14.5px; max-width:74ch; text-wrap:pretty;
}
.note b{color:var(--ink); font-weight:600}
h2{font-family:var(--mono); font-size:13px; letter-spacing:.14em; text-transform:uppercase;
  margin:0 0 18px; font-weight:700; color:var(--ink)}
.sec{display:grid}
.gapped{gap:24px}
.gapped h2{margin-bottom:0}   /* the grid gap already spaces it */
@media (prefers-reduced-motion:reduce){*{animation:none!important;transition:none!important}}
</style>

<div class="wrap">
  <div class="warn">
    <div class="warn-tag">⚠ Style template — not a specification</div>
    <h2>Everything this page describes was reverted. Its premise is false.</h2>
    <p>This file is in the repository for <b>one reason</b>: it is the house style for visual
    documentation of the status line. Copy its <b>presentation</b> — the character-cell terminal
    rendering, the palette lifted verbatim from <code>Program.cs</code>, the before/after panes,
    the column-anchored annotations, the habit of stating what a design costs in characters.
    Do not copy, implement, or reason from its <b>content</b>.</p>
    <ul>
      <li><b>The claim was:</b> the 5-hour session limit resets when the 7-day weekly limit
      resets, so the 5h projection should stop at the weekly boundary.</li>
      <li><b>That is false.</b> The session window is independent — <code>session.resets_at</code>
      follows its own schedule and the weekly reset does not touch it.</li>
      <li><b>The change was built, deployed, and reverted.</b> <code>src/Program.cs</code> carries
      none of it: no <code>BarCut</code>, no <code>WallColor</code>, no <code>walled</code>, no
      <code>projFull</code>. The <code>⇥</code> and <code>┃</code> marks shown below <b>do not
      exist in the product</b> and never will under this rationale.</li>
    </ul>
    <p>The correct limit model is in <a href="../../reference/limits.md">docs/reference/limits.md</a>,
    and the full record of why this was rejected in
    <a href="../rejected-designs.md#rejected-clamping-the-session-projection-to-the-weekly-reset">docs/design/rejected-designs.md</a>.</p>
  </div>

  <header>
    <div class="eyebrow">Documentation style template · reverted design</div>
    <h1>The Weekly Wall</h1>
    <p class="lede"><b>As originally written, this page argued:</b> the 7d window resets at 05:00,
    2h30m before the 5h window's own reset at 07:30 — and takes the 5h counter with it, so the
    session projection has to stop there. That reasoning does not hold. Everything below is
    preserved as written, at the live numbers from 03:49, purely as a worked example of the
    documentation format.</p>
  </header>

  <section class="sec">
    <div class="phead">
      <span class="ptitle">Now</span>
      <span class="tag bad">projects past the wall</span>
      <span class="pnote">5h extrapolates the full 3h41m to its own reset — 161% and a red overshoot that cannot happen.</span>
    </div>
    <div class="term"><div class="term-inner">
      ${line2(OLD_5H, []).replace('</div>', acct + '</div>')}
      ${line2(OLD_7D, OLD_FB)}
    </div></div>
  </section>

  <section class="sec">
    <div class="phead">
      <span class="ptitle">Shipped</span>
      <span class="tag good">wall column + cut bar</span>
      <span class="pnote">Session horizon clamped to the wall. Same row width, to the character. The weekly rows are untouched.</span>
    </div>
    <div class="term"><div class="term-inner">
      ${line2(NEW_5H, []).replace('</div>', acct + '</div>')}
      ${annot([{col:27, len:7, text:'⇥ 7d wall'}, {col:45, len:9, text:'cut + cancelled'}])}
      ${line2(NEW_7D, NEW_FB)}
    </div></div>
  </section>

  <section>
    <h2>The two marks</h2>
    <div class="marks">
      <div class="mark">
        <div class="mark-h">
          <span class="chip"><i class="wl">⇥ 1h11m</i></span>
          <span class="mark-t">The wall column</span>
        </div>
        <p>On a walled row the <b>“time to 100%”</b> field is meaningless — you never get there.
        It is spent on the wall instead: <code>→</code> becomes <code>⇥</code> and the value
        becomes the 7d countdown. Both clocks stay on the row — the row's own reset at
        <code>↻ 3h41m</code>, the one that actually governs at <code>⇥ 1h11m</code>.
        Costs <b>zero characters</b>: <code>→ 1h54m</code> and <code>⇥ 1h11m</code> are the
        same seven cells.</p>
      </div>
      <div class="mark">
        <div class="mark-h">
          <span class="chip"><i class="cy">●●</i><i class="wl">┃</i><i class="gh">●✗✗</i></span>
          <span class="mark-t">The cut in the bar</span>
        </div>
        <p>The projection bar keeps the <b>full 161% trajectory</b> but <code>┃</code> cuts it
        at the wall. Everything left of the cut is where you actually land — 75%. Everything
        right of it keeps its shape, including the six <code>✗</code> of overshoot, but goes
        ghosted: that is the part the week cancels. Bar 16→17, percent column 3→2, so the row
        width is unchanged. It is drawn only where something is actually cut — never on a row
        that was going to end at the wall anyway.</p>
      </div>
    </div>
  </section>

  <section class="gapped">
    <h2>Only the session row is walled</h2>
    <table class="states">
      <thead><tr><th>Row</th><th>Kind</th><th>Against the wall</th></tr></thead>
      <tbody>
        <tr>
          <td>5h</td>
          <td><b>Session.</b> A rolling five-hour window whose reset drifts with use — 07:30
          today, 2h30m the far side of the weekly boundary. The only row that can run into
          the wall.</td>
          <td><span class="mini"><span class="wl">⇥ 1h11m</span></span> and a cut bar</td>
        </tr>
        <tr>
          <td>7d</td>
          <td><b>Weekly.</b> It <em>is</em> the wall.</td>
          <td>Untouched</td>
        </tr>
        <tr>
          <td>Fable</td>
          <td><b>Weekly, model-scoped.</b> Resets <em>with</em> the wall rather than into it —
          04:59:59.821391 against 04:59:59.821140.</td>
          <td>Untouched</td>
        </tr>
      </tbody>
    </table>
    <p class="note">In code this is one field. <code>Wall</code> is <code>+∞</code> for both
    weekly rows, so <code>walled</code> is false and every branch the change adds stays
    dormant for them — and dormant for the session row too, for most of the week, until the
    weekly reset finally overtakes it.</p>
  </section>

  <section>
    <h2>Colour rule</h2>
    <p class="note">Both marks take the <b>7d row's severity colour</b>, not a fixed one —
    amber today because <code>weekly_all</code> reports <code>severity: "warning"</code> at 81%,
    green while it is normal, red once critical. So the wall does not just say <em>when</em> the
    thing that governs you resets, it says <em>how much trouble it is in</em>. It reuses
    <code>SevColor</code>, already in the file. If you would rather it stayed one fixed colour,
    that is a one-line change.</p>
  </section>

  <section class="gapped">
    <h2>Outcome — the premise did not hold</h2>
    <p class="note">The caveat this page shipped with turned out to be the whole story. Nothing
    in the OAuth payload ever confirmed the coupling: <code>session.resets_at</code> stayed at
    07:30 regardless of the weekly boundary, and the array marked
    <code>session: is_active=false</code> against <code>weekly_all: is_active=true</code>. The
    design rested on an observation that was subsequently withdrawn. It was reverted in full —
    source restored and hash-verified against the pre-change file, binary rebuilt from it, and
    both wall glyphs confirmed absent from the rendered output.</p>
    <p class="note"><b>What survives is the presentation, and one habit worth keeping.</b> The
    ghosted-tail bar was chosen because it kept the un-walled 161% trajectory on screen instead
    of deleting it — the only option of the five that would have degraded gracefully if the
    premise were wrong. It was wrong, and it would have. Designing the display so a bad
    assumption stays visible rather than silently disappearing is the transferable lesson here;
    the feature is not.</p>
  </section>
</div>
`;

fs.writeFileSync(process.argv[2], html, 'utf8');
console.log('written', process.argv[2]);
