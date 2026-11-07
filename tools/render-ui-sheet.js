#!/usr/bin/env node
/*
 * The system's documentation sheet (Brief BK). `node tools/render-ui-sheet.js` writes docs/dwight-ui-system-sheet.html from
 * dwight-tokens.css; `--check` fails when the committed sheet differs, and when a text role falls under its contrast floor.
 * tools/check-shell-render.js runs the check.
 *
 * WHY IT CANNOT LIE. Tharun's reference sheet obeys its tokens by care: its SVGs hold hexes by hand, right today, with nothing
 * holding them (measured, docs/dwight-ui-audit-2026-10-30.md section 5). Here every role, step and ratio is computed from the
 * token file on each run, and every control on the page is the real class from dwight-components.css. A sheet that went stale
 * would fail the build.
 *
 * Open the file directly: the CSS is linked from Dwight/Resources, so the page always draws what the product ships.
 */
const fs = require('fs');
const path = require('path');

const ROOT = path.resolve(__dirname, '..');
const RES = path.join(ROOT, 'Dwight', 'Resources');
const SHEET = path.join(ROOT, 'docs', 'dwight-ui-system-sheet.html');
// The development tree, told apart as .github/workflows/ci.yml tells it: its docs carry the phases folder, which never ships.
const DEV_TREE = fs.existsSync(path.join(ROOT, 'docs', 'phases'));
const FLOOR = 4.5;

const stripComments = (s) => s.replace(/\/\*[\s\S]*?\*\//g, '');
const tokensText = fs.readFileSync(path.join(RES, 'dwight-tokens.css'), 'utf8');

// ---- the token file, per theme: what a surface computes --------------------------------------------------------------
function readTokens() {
  const tiers = {}, parts = tokensText.split(/\/\*@tier:(\w+)\*\//);
  const base = {}, dark = {};
  for (let i = 1; i < parts.length; i += 2) {
    const tier = parts[i], text = stripComments(parts[i + 1]);
    for (const rule of text.matchAll(/([^{}]+)\{([^{}]*)\}/g)) {
      const sel = rule[1].trim();
      const into = sel === 'body.dark' ? dark : base;
      for (const d of rule[2].matchAll(/(--[\w-]+)\s*:\s*([^;]+);/g)) { into[d[1]] = d[2].trim(); if (into === base) tiers[d[1]] = tier; }
    }
  }
  const resolve = (name, theme, n = 0) => {
    if (n > 12) return null;
    const v = theme === 'dark' && name in dark ? dark[name] : base[name];
    if (v == null) return null;
    return v.replace(/var\(\s*(--[\w-]+)\s*\)/g, (a, x) => resolve(x, theme, n + 1) || a);
  };
  return { tiers, base, resolve };
}
const T = readTokens();

const lin = (c) => { c /= 255; return c <= 0.03928 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4); };
const rgb = (h) => { const n = parseInt(h.slice(1), 16); return [(n >> 16) & 255, (n >> 8) & 255, n & 255]; };
const lum = (h) => { const [r, g, b] = rgb(h); return 0.2126 * lin(r) + 0.7152 * lin(g) + 0.0722 * lin(b); };
const ratio = (a, b) => { const x = lum(a), y = lum(b); return (Math.max(x, y) + 0.05) / (Math.min(x, y) + 0.05); };
const isHex = (v) => /^#[0-9A-Fa-f]{6}$/.test(v || '');
const over = (fg, pct, bg) => { const f = rgb(fg), b = rgb(bg); return '#' + f.map((c, i) => Math.round(c * pct + b[i] * (1 - pct)).toString(16).padStart(2, '0')).join('').toUpperCase(); };

// Each text role on every ground it may stand on. The floor is a test: lowering it is a visible decision.
const GROUNDS = ['--surface', '--raised', '--inset', '--bar'];
const PAIRS = [
  ['--text', GROUNDS], ['--body', GROUNDS], ['--subtle', GROUNDS], ['--accent-ink', GROUNDS], ['--link-ink', GROUNDS], ['--ok-ink', GROUNDS], ['--warn-ink', GROUNDS], ['--err-ink', GROUNDS], ['--info-ink', GROUNDS],
  ['--thinking-ink', GROUNDS], ['--accent-inverse', ['--accent', '--accent-hover']],
];
function contrastTable() {
  const rows = [], failures = [];
  for (const [fg, bgs] of PAIRS) {
    const cells = {};
    for (const theme of ['light', 'dark']) {
      const f = T.resolve(fg, theme);
      const rs = bgs.map((b) => ratio(f, T.resolve(b, theme)));
      const worst = Math.min(...rs);
      cells[theme] = worst;
      if (worst < FLOOR) failures.push(`${fg} in ${theme} is ${worst.toFixed(2)}:1 at its worst (${bgs.join(', ')}), under the ${FLOOR}:1 floor`);
    }
    rows.push({ fg, bgs, ...cells });
  }
  // the diff: an ink on its own tint, over the code block's ground
  for (const [ink, base, pct] of [['--ok-ink', '--ok', 0.12], ['--err-ink', '--err', 0.12]]) for (const theme of ['light', 'dark']) {
    const r = ratio(T.resolve(ink, theme), over(T.resolve(base, theme), pct, T.resolve('--inset', theme)));
    if (r < FLOOR) failures.push(`${ink} on its tint over --inset in ${theme} is ${r.toFixed(2)}:1, under the ${FLOOR}:1 floor`);
  }
  return { rows, failures };
}

// ---- the page ------------------------------------------------------------------------------------------------------
const esc = (s) => String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
const ROLE_GROUPS = [
  ['Text', ['--text', '--body', '--subtle', '--faint']],
  ['Surfaces and lines', ['--surface', '--raised', '--inset', '--bar', '--border', '--border-strong']],
  ['Accent', ['--accent', '--accent-hover', '--accent-tint', '--accent-inverse', '--accent-ink', '--focus']],
  ['Links', ['--link', '--link-ink', '--link-tint']],
  ['Thinking', ['--thinking', '--thinking-ink']],
  ['Verdicts', ['--ok', '--ok-ink', '--warn', '--warn-ink', '--err', '--err-ink', '--info', '--info-ink']],
  ['Group colours', ['--g-input', '--g-geometry', '--g-data', '--g-output']],
];
const swatch = (n) => `<div class="sw"><span class="hue" style="background:var(${n})"></span><code>${n}</code><span class="vals">${T.resolve(n, 'light')} / ${T.resolve(n, 'dark')}</span></div>`;
const STEPS = [
  ['--type-title', '--font-display', 'title', 'Window title, an answer\'s heading, a state\'s title'],
  ['--type-label', '--font-body', 'label', 'A card\'s act, a control\'s label, a receipt\'s sentence'],
  ['--type-body', '--font-body', 'body', 'Answers, card notes, hints, field text'],
  ['--type-control', '--font-mono', 'control', 'The words on a control'],
  ['--type-caption', '--font-mono', 'caption', 'Facts: cost, meta, state, table heads'],
  ['--type-caption-strong', '--font-mono', 'caption, strong', 'The section heading, in caps'],
];
const step = ([t, f, name, use]) => `<div class="tstep"><span style="font:var(${t}) var(${f})">${esc(name)}: ${esc(use)}</span><code>${t} = ${T.base[t]}</code></div>`;
const SPACES = ['--space-2xs', '--space-xs', '--space-s', '--space-m', '--space-l', '--space-xl'];
const space = (n) => `<div class="sp"><span class="bar" style="width:var(${n})"></span><code>${n} = ${T.base[n]}</code></div>`;
const GLYPH = '<svg class="ic" aria-hidden="true"><use href="#ic-check"/></svg>';

const page = () => {
  const { rows } = contrastTable();
  return `<!doctype html>
<!-- GENERATED by tools/render-ui-sheet.js from Dwight/Resources/dwight-tokens.css. Do not edit; the gate compares it. -->
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><title>Dwight UI system</title>
<link rel="stylesheet" href="../Dwight/Resources/dwight-fonts.css">
<link rel="stylesheet" href="../Dwight/Resources/dwight-tokens.css">
<link rel="stylesheet" href="../Dwight/Resources/dwight-components.css">
<style>
  /* The sheet's own furniture; the controls below are the product's. */
  html,body{height:auto;overflow:auto}
  main{max-width:820px;margin:0 auto;padding:24px 16px 64px}
  h1{font:var(--type-title) var(--font-display);color:var(--text);margin:0 0 4px}
  .lede{margin:0 0 8px;color:var(--subtle)}
  .row{display:flex;flex-wrap:wrap;gap:12px;align-items:center;margin:0 0 12px}
  .grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(250px,1fr));gap:4px 16px}
  .sw{display:flex;align-items:center;gap:8px;font:var(--type-caption) var(--font-mono);color:var(--subtle);min-width:0}
  .sw .hue{width:16px;height:16px;flex:none;border:1px solid var(--border-strong)}
  .sw code{color:var(--text);white-space:nowrap}
  .sw .vals{margin-left:auto;font-size:9px;white-space:nowrap}
  .tstep{display:flex;align-items:baseline;justify-content:space-between;gap:16px;margin:0 0 8px;color:var(--text)}
  code{font:var(--type-caption) var(--font-mono);color:var(--subtle);background:none;border:0;padding:0}
  .sp{display:flex;align-items:center;gap:8px;margin:0 0 4px}
  .sp .bar{height:12px;background:var(--accent);flex:none}
  table.ratios{border-collapse:collapse;font:var(--type-caption) var(--font-mono);color:var(--body)}
  table.ratios td,table.ratios th{padding:2px 12px 2px 0;text-align:left;font-weight:400}
  .demo{width:340px;max-width:100%}
</style></head>
<body><main>
<h1>Dwight UI system</h1>
<p class="lede">Generated from the tokens; every control is the product's own class. Hover and tab through them for their states.</p>
<div class="row"><button class="btn sm" id="flip" type="button">Dark</button></div>

<div class="section-head">Colour roles (light / dark)</div>
${ROLE_GROUPS.map(([name, list]) => `<p class="hint">${esc(name)}</p><div class="grid">${list.map(swatch).join('')}</div>`).join('\n')}

<div class="section-head">Contrast, worst ground, floor ${FLOOR}:1</div>
<table class="ratios"><tr><th>Text role</th><th>Light</th><th>Dark</th><th>On</th></tr>
${rows.map((r) => `<tr><td>${r.fg}</td><td>${r.light.toFixed(2)}</td><td>${r.dark.toFixed(2)}</td><td>${r.bgs.join(' ')}</td></tr>`).join('\n')}
</table>

<div class="section-head">Type: five steps, three faces</div>
${STEPS.map(step).join('\n')}

<div class="section-head">Space: six steps</div>
${SPACES.map(space).join('\n')}

<div class="section-head">Controls: one height, ${T.base['--control-h']}; a row's height, ${T.base['--control-h-sm']}</div>
<div class="row"><button class="btn primary" type="button">Yes, go ahead</button><button class="btn" type="button">Loose</button><button class="btn ghost" type="button">Back</button><button class="btn primary" type="button" disabled>Disabled</button><button class="btn sm" type="button">Small</button></div>
<div class="row"><button class="iconbtn" type="button" title="Icon button">${GLYPH}</button><button class="iconbtn on" type="button" title="Icon button, on">${GLYPH}</button><button class="iconbtn" type="button" title="A small icon"><svg class="ic sm" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M20 6L9 17l-5-5"/></svg></button></div>
<div class="demo">
  <div class="dd"><button class="dd-sel" type="button"><span class="dd-txt">Components</span><svg class="ic sm dd-car" aria-hidden="true"><use href="#ic-caret"/></svg></button></div>
  <div class="swrow"><label>Full control</label><button class="sw" role="switch" aria-checked="true" aria-label="Full control" type="button"><i></i></button></div>
  <p class="hint">A hint sits one small step under its control.</p>
  <div class="field"><p class="hint">A field and a dropdown are the same box as a segment.</p><input type="text" value="Tharun"><textarea rows="1">Reply, or describe what to build</textarea></div>
  <div class="dd open"><div class="dd-sel"><span class="dd-txt">Anthropic</span></div><div class="dd-item on">Anthropic</div><div class="dd-item">OpenAI</div></div>
  <p class="hint"><input class="check" type="checkbox" checked> A tick.</p>
</div>

<div class="section-head">Tags: colour only for a verdict</div>
<div class="row"><span class="tag">a fact</span><span class="tag ok">checked</span><span class="tag warn">not checked</span><span class="tag err">reverted</span><span class="tag info">fully local</span></div>

<div class="section-head">Fold, source and diff</div>
<div class="demo"><details class="fold" open><summary>What this does and what it cannot</summary><pre class="code" style="margin:0"><span class="diff-ctx">  import math</span><span class="diff-del">- x = 1</span><span class="diff-add">+ x = 2</span><span class="diff-gap">  </span></pre></details></div>
</main>
<script>document.getElementById('flip').onclick=function(){var d=document.body.classList.toggle('dark');this.textContent=d?'Light':'Dark';};</script>
</body></html>
`;
};

function check() {
  const { failures } = contrastTable();
  const bad = [];
  for (const [n] of PAIRS) if (!isHex(T.resolve(n, 'light')) || !isHex(T.resolve(n, 'dark'))) bad.push(`${n} did not resolve to a hex (${T.resolve(n, 'light')}, ${T.resolve(n, 'dark')}); the reader no longer matches the token file`);
  // The positive control: the pair that was shipped must read as what was measured, and must fail the floor.
  const control = ratio('#FDFDFC', '#D4864A');
  if (Math.abs(control - 2.82) > 0.01) bad.push(`the contrast control reads ${control.toFixed(2)}, expected 2.82`);
  const out = [...bad, ...failures];
  const want = page().replace(/\r\n/g, '\n');
  // The committed sheet is a development document. Where the development tree is (its docs carry the phases folder) a missing sheet
  // is a failure; in the public tree the comparison is skipped, and tools/ui-measure.js measures the sheet page() generates instead.
  if (!fs.existsSync(SHEET) && !DEV_TREE) return { out, skipped: true };
  if (!fs.existsSync(SHEET)) out.push('docs/dwight-ui-system-sheet.html does not exist. Run node tools/render-ui-sheet.js.');
  else if (fs.readFileSync(SHEET, 'utf8').replace(/\r\n/g, '\n') !== want) out.push('docs/dwight-ui-system-sheet.html differs from what the tokens produce. Run node tools/render-ui-sheet.js and commit it.');
  return { out, skipped: false };
}

function run(say = console.log) {
  say('=== UI system: the sheet and the contrast floors');
  const { out, skipped } = check();
  for (const e of out) say('  FAIL  ' + e);
  if (skipped) say('  SKIP  the committed sheet: docs/dwight-ui-system-sheet.html is a development document and is not in this tree, so it is not compared; the measuring leg measures the sheet generated from the tokens.');
  if (!out.length) say(`   OK   ${skipped ? '' : 'the sheet matches the tokens; '}every text role clears ${FLOOR}:1 on its worst ground in both themes (control: 2.82:1 read for the old white on amber)`);
  say('');
  return out.length;
}
module.exports = { run, page, DEV_TREE };

if (require.main === module) {
  if (process.argv.includes('--check')) process.exit(run() ? 1 : 0);
  fs.writeFileSync(SHEET, page());
  console.log('wrote ' + path.relative(ROOT, SHEET));
  const { rows, failures } = contrastTable();
  for (const r of rows) console.log('  ' + r.fg.padEnd(18) + r.light.toFixed(2).padStart(6) + r.dark.toFixed(2).padStart(7));
  for (const f of failures) console.log('  UNDER THE FLOOR: ' + f);
}
