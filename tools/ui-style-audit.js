#!/usr/bin/env node
/*
 * The UI system's source audit (Brief BK). Run by tools/check-shell-render.js, and by itself for its reports:
 *   node tools/ui-style-audit.js                    the gate
 *   node tools/ui-style-audit.js --counts           distinct values per property and surface (the audit's table)
 *
 * WHAT IT HOLDS. A size, a gap, a radius, a colour or a face outside the system does not get in. Every rule of the library and
 * of every shell is strict: one off-system value fails, with its file and line. Every class the C# writes has a rule in the
 * panel's CSS. A class the C# writes from data is listed in DATA_CLASSES, because a text scan cannot see it.
 *
 * WHAT IT CANNOT SEE: whether a declared value WINS. `.hint{font:var(--type-body) ...}` passes here and can still lose to an
 * id selector on every element it matches, which is what #body p did to the cards for a year. That is tools/ui-measure.js's
 * job, in a real browser. A check that reads the source is not a check of the screen.
 *
 * WHY IT CANNOT QUIETLY PASS. It tests itself first: selfTest() plants one stray value of every kind, an orphan class and an
 * inline style in an in-memory copy of the product, and each must be reported, while the real product
 * must parse to a floor of declarations. Anything else reports the INSTRUMENT as broken instead of judging the product.
 */
const fs = require('fs');
const path = require('path');

const ROOT = path.resolve(__dirname, '..');
const RES = path.join(ROOT, 'Dwight', 'Resources');
const SURFACES = { 'dwight-components.css': 'shared', 'panel-shell.html': 'panel', 'dock-shell.html': 'dock', 'settings.html': 'settings', 'feedback.html': 'feedback', 'note.html': 'note', 'walk-bar.html': 'walk', 'tour.html': 'tour' };
const TOKENS = 'dwight-tokens.css';

// Classes the C# writes from a variable, so no literal class="" shows them. Each is held by a C# test on the card or the panel.
const DATA_CLASSES = ['ok', 'warn', 'error', 'warning', 'silent', 'remove-dead', 'externalize', 'robustify', 'reduce', 'rename'];

// The tour's picture of Grasshopper is the host, not Dwight, so it takes nothing from the tokens (tour.html says so).
const HOST_SELECTOR = /(^|[\s,])\.(mini|comp|minibar|minicorner|cur)\b/;
const HOST_TOKEN = /^--(gh-|host-|speed$|d$)/;   // Grasshopper's own look, and the tour's per-element timing, which is data

const lineAt = (s, i) => s.slice(0, i).split('\n').length;
const blankComments = (s) => s.replace(/\/\*[\s\S]*?\*\//g, (m) => m.replace(/[^\n]/g, ' '));

// ---- reading ------------------------------------------------------------------------------------------------------
function parseDecls(body, baseLine, file, selector, at, out) {
  let depth = 0, start = 0;
  for (let k = 0; k <= body.length; k++) {
    const c = body[k];
    if (c === '(') depth++; else if (c === ')') depth--;
    if ((c === ';' && depth === 0) || k === body.length) {
      const raw = body.slice(start, k), idx = raw.indexOf(':');
      if (idx > 0) {
        const prop = raw.slice(0, idx).trim().toLowerCase(), value = raw.slice(idx + 1).trim();
        if (prop && value) out.push({ file, line: baseLine + lineAt(body, start + (raw.length - raw.trimStart().length)) - 1, selector: selector.replace(/\s+/g, ' '), prop, value, at: at || null });
      }
      start = k + 1;
    }
  }
}
function parseCss(css, baseLine, file, out, at) {
  let i = 0;
  while (i < css.length) {
    const open = css.indexOf('{', i); if (open < 0) break;
    const sel = css.slice(i, open).trim();
    let depth = 1, j = open + 1;
    while (j < css.length && depth) { if (css[j] === '{') depth++; else if (css[j] === '}') depth--; j++; }
    const body = css.slice(open + 1, j - 1), line = baseLine + lineAt(css, open + 1) - 1;
    if (/^@(media|supports)/.test(sel)) parseCss(body, line, file, out, sel);
    else if (/^@keyframes/.test(sel)) { const re = /([^{}]+)\{([^{}]*)\}/g; let m; while ((m = re.exec(body))) parseDecls(m[2], line + lineAt(body, m.index + m[0].indexOf('{') + 1) - 1, file, sel + ' ' + m[1].trim(), sel, out); }
    else if (!/^@font-face/.test(sel)) parseDecls(body, line, file, sel, at, out);
    i = j;
  }
}
function declarationsOf(file, text) {
  const out = [];
  if (file.endsWith('.css')) { parseCss(blankComments(text), 1, file, out, null); return out; }
  for (const m of text.matchAll(/<style>([\s\S]*?)<\/style>/g)) {
    if (m[1].includes('__DWIGHT_')) continue;
    parseCss(blankComments(m[1]), lineAt(text, m.index + 7), file, out, null);
  }
  // Inline style="" in the markup and in the shell's script strings. A concatenated value cannot be judged, so it counts.
  for (const m of text.matchAll(/style=\\?["']([^"']*?)\\?["']/g)) {
    const line = lineAt(text, m.index);
    for (const d of m[1].split(';')) {
      const k = d.indexOf(':'); if (k < 0) continue;
      const prop = d.slice(0, k).trim().toLowerCase(), value = d.slice(k + 1).trim();
      if (prop && value) out.push({ file, line, selector: 'inline style', prop, value, at: null, inline: true });
    }
  }
  return out;
}

// The tiers, read from the markers in the token file: a name's tier is where it is declared, never a list typed here.
function readTiers(text) {
  const tiers = {}, parts = text.split(/\/\*@tier:(\w+)\*\//);
  for (let i = 1; i < parts.length; i += 2) for (const m of blankComments(parts[i + 1]).matchAll(/(--[\w-]+)\s*:/g)) if (!tiers[m[1]]) tiers[m[1]] = parts[i];
  return tiers;
}

// ---- judging ------------------------------------------------------------------------------------------------------
const COLOUR_PROP = /^(color|background|background-color|border-color|border-(top|right|bottom|left)-color|outline-color|accent-color|fill|stroke|text-decoration-color|caret-color)$/;
const SPACE_PROP = /^(margin|padding)(-(top|right|bottom|left))?$|^(gap|row-gap|column-gap)$/;
const SIZE_PROP = /^(height|min-height|max-height)$/;
const BORDER_PROP = /^(border|border-(top|right|bottom|left)|outline)$/;
const FREE_SIZE = /^(0|auto|none|inherit|100%|100vh|fit-content|min-content|max-content)$/;
const splitTop = (v) => { const out = []; let d = 0, s = ''; for (const c of v) { if (c === '(') d++; if (c === ')') d--; if (/\s/.test(c) && d === 0) { if (s) out.push(s); s = ''; } else s += c; } if (s) out.push(s); return out; };
const varName = (t) => (/^var\(\s*(--[\w-]+)\s*\)$/.exec(t) || [])[1];

// The kind of breach one declaration commits, or null.
function judge(d, tiers) {
  const v = d.value.replace(/\s*!important\s*$/, ''), p = d.prop;
  if (p.startsWith('--')) return HOST_TOKEN.test(p) ? null : 'local-token';                          // a custom property declared outside the token file

  // Every var() in any value must be a role or a scale. A primitive is the worst of it, and a shell-local name hides a value.
  for (const m of v.matchAll(/var\(\s*(--[\w-]+)/g)) {
    const t = tiers[m[1]];
    if (t === 'primitive') return 'primitive';
    if (!t && !HOST_TOKEN.test(m[1])) return 'local-token';
  }

  if (p === 'font-family') return /^(var\(--font-(display|body|mono)\)|inherit)$/.test(v) ? null : 'type';
  if (p === 'font-size' || p === 'font-weight' || p === 'line-height') return 'type';
  if (p === 'font') return /^(inherit|var\(--type-[\w-]+\) var\(--font-(display|body|mono)\))$/.test(v) ? null : 'type';
  if (p === 'letter-spacing') return /^(0|normal|var\(--track-\w+\))$/.test(v) ? null : 'type';
  if (SPACE_PROP.test(p)) return splitTop(v).every((t) => /^(0|auto|inherit)$/.test(t) || /^var\(--space-[\w-]+\)$/.test(t)) ? null : 'space';
  if (SIZE_PROP.test(p)) return FREE_SIZE.test(v) || /^var\(--(control-h|control-h-sm|icon|icon-sm|mark|mark-sm|mark-lg|border-w|border-w-strong|scrollbar|bar-h|text-max|dot|bullet)\)$/.test(v) || /^\d+vh$/.test(v) ? null : 'height';
  if (p === 'border-radius' || /^border-.*-radius$/.test(p)) return v === 'var(--radius-switch)' ? null : 'radius';   // the switch is the system's one curve (Tharun, Brief BO)
  if (COLOUR_PROP.test(p)) return splitTop(v.replace(/!important/, '')).every((t) => /^(none|transparent|currentColor|inherit|0)$/.test(t) || (varName(t) && tiers[varName(t)] === 'role') || /^var\(--g-/.test(t)) ? null : 'colour';
  if (BORDER_PROP.test(p)) return /^(0|none)$/.test(v) || /^var\(--border-w(-strong)?\) (solid|dashed) (var\(--[\w-]+\)|transparent|currentColor)$/.test(v) ? null : 'border';
  if (p === 'box-shadow') return /^(none|var\(--shadow(-card|-line|-line-sm)?\))$/.test(v) ? null : 'shadow';
  if (p === 'transition' || p === 'animation' || p === 'animation-duration' || p === 'transition-duration') return /\d(ms|s)\b|cubic-bezier|\bease\b|ease-(in|out)/.test(v.replace(/var\([^)]*\)/g, '')) ? 'motion' : null;
  return null;
}

const isHost = (d) => HOST_SELECTOR.test(d.selector) || (d.file === 'tour.html' && d.inline && /left:|top:|width:/.test(d.prop + ':'));

function audit(files, cs) {
  const tiers = readTiers(files[TOKENS]);
  const errors = [];
  let declarations = 0;
  for (const file of Object.keys(SURFACES)) {
    for (const d of declarationsOf(file, files[file])) {
      if (isHost(d) || /^@keyframes/.test(d.at || '')) continue;
      declarations++;
      const kind = judge(d, tiers);
      const idSelector = /#[a-zA-Z]/.test(d.selector) && !d.inline;
      if (/--type-display\b/.test(d.value) && file !== 'tour.html') errors.push(`${file}:${d.line}  ${d.selector}  the display size is the tour's alone (Tharun, R6)`);
      if (kind) errors.push(`${file}:${d.line}  ${d.selector}  ${d.prop}:${d.value}  is off the system (${kind})`);
      if (idSelector) errors.push(`${file}:${d.line}  ${d.selector}  an id selector beats every class, which is how #body p overruled the cards`);
    }
  }

  // ---- one icon table (Tharun, Brief BO): an icon is a <use> of the sprite, and nothing else draws one --------------
  const sprite = files['dwight-icons.svg'] || '';
  const named = new Set([...sprite.matchAll(/<symbol id="ic-([\w-]+)"/g)].map((m) => m[1]));
  const scanIcons = (where, text) => {
    for (const m of text.matchAll(/viewBox=\\?"0 0 24 24\\?"|setAttribute\('viewBox','0 0 24 24'\)/g)) errors.push(`${where}:${lineAt(text, m.index)}  an icon drawn here; icons come from the one table, tools/build-icon-sprite.js (a <use href="#ic-NAME"/>)`);
    for (const m of text.matchAll(/<svg class=\\?"ic[^>]*>\s*<(?!use\b)/g)) errors.push(`${where}:${lineAt(text, m.index)}  an icon drawn here; icons come from the one table, tools/build-icon-sprite.js (a <use href="#ic-NAME"/>)`);
    for (const m of text.matchAll(/#ic-([a-z_][\w-]*)/g)) if (!named.has(m[1])) errors.push(`${where}:${lineAt(text, m.index)}  the icon "${m[1]}" is not in the one table`);
  };
  for (const file of Object.keys(SURFACES)) if (file.endsWith('.html')) { scanIcons(file, files[file]); if (!files[file].includes('<!--__DWIGHT_ICONS__-->')) errors.push(`${file}  does not inline the one icon table`); }
  for (const [p, s] of Object.entries(cs)) scanIcons(p, s);

  // ---- classes the C# writes, against the panel's CSS -----------------------------------------------------------
  const rules = new Set();
  for (const f of ['dwight-components.css', 'panel-shell.html']) for (const d of declarationsOf(f, files[f])) for (const m of d.selector.matchAll(/\.([a-zA-Z_][\w-]*)/g)) rules.add(m[1]);
  const emitted = new Map();
  for (const [p, s] of Object.entries(cs)) {
    for (const m of s.matchAll(/class=\\"([^"\\]*)\\?"?/g)) for (const c of m[1].split(/\s+/).filter(Boolean)) if (!emitted.has(c)) emitted.set(c, p + ':' + lineAt(s, m.index));
    for (const m of s.matchAll(/"(diff-[a-z]+)"/g)) if (!emitted.has(m[1])) emitted.set(m[1], p + ':' + lineAt(s, m.index));
    // The card HTML carries no style. A style attribute written in C# is a value no token and no gate could reach.
    for (const m of s.matchAll(/style=\\"/g)) errors.push(`${p}:${lineAt(s, m.index)}  the C# writes a style attribute; a card takes classes only`);
  }
  for (const c of emitted.keys()) if (!rules.has(c)) errors.push(`${emitted.get(c)}  the C# writes class "${c}", which has no rule in the system. Every class the C# writes has one.`);
  return { errors, emitted: emitted.size, declarations, tiers, dataClasses: DATA_CLASSES };
}

// ---- the product, read from disk ----------------------------------------------------------------------------------
function readProduct() {
  const files = {};
  for (const f of [TOKENS, 'dwight-icons.svg', ...Object.keys(SURFACES)]) files[f] = fs.readFileSync(path.join(RES, f), 'utf8');
  const cs = {};
  const walk = (dir) => { for (const e of fs.readdirSync(dir, { withFileTypes: true })) { const p = path.join(dir, e.name); if (e.isDirectory()) { if (!/^(bin|obj)$/.test(e.name)) walk(p); } else if (e.name.endsWith('.cs')) cs[path.relative(ROOT, p).replace(/\\/g, '/')] = fs.readFileSync(p, 'utf8'); } };
  walk(path.join(ROOT, 'Dwight'));
  return { files, cs };
}

// ---- the self-test: every kind of breach, planted, must be reported -------------------------------------------------
const PLANTS = [
  ['font-size', 'font-size:13px', 'type'], ['a stray face', 'font-family:Arial', 'type'], ['a bare line height', 'line-height:1.4', 'type'],
  ['a type step with no face', 'font:12px Arial', 'type'], ['margin', 'margin:7px', 'space'], ['padding', 'padding:3px var(--space-s)', 'space'], ['gap', 'gap:5px', 'space'],
  ['height', 'height:27px', 'height'], ['radius', 'border-radius:4px', 'radius'], ['a hex', 'color:#123456', 'colour'], ['a mixed colour', 'background:color-mix(in srgb,var(--ok) 5%,transparent)', 'colour'],
  ['a primitive', 'color:var(--s12)', 'primitive'], ['a name the tokens do not hold', 'color:var(--tiffany)', 'local-token'], ['a scale step as a colour', 'color:var(--space-s)', 'colour'],
  ['a literal border', 'border:1px solid var(--border)', 'border'], ['a literal shadow', 'box-shadow:2px 2px 0 0 var(--border)', 'shadow'], ['a literal duration', 'transition:color .15s', 'motion'],
];
const CLEAN = ['font:var(--type-body) var(--font-body)', 'margin:0 var(--space-s)', 'height:var(--control-h)', 'color:var(--subtle)', 'background:transparent', 'border:var(--border-w) solid var(--border)', 'box-shadow:var(--shadow-card)', 'transition:color var(--dur-fast) var(--ease-out)', 'letter-spacing:var(--track-caps)'];

function selfTest(say) {
  const { files, cs } = readProduct();
  const bad = [];
  const clean = audit(files, cs);
  if (clean.declarations < 500) bad.push(`it read only ${clean.declarations} declarations from the product; the parser no longer matches the files`);
  if (clean.emitted < 30) bad.push(`it found only ${clean.emitted} classes in the C#; the scan no longer matches the source`);
  const report = [];
  const withComponents = (decl) => Object.assign({}, files, { 'dwight-components.css': files['dwight-components.css'] + `\n.ui-audit-selftest{${decl}}\n` });
  for (const [name, decl, kind] of PLANTS) {
    const r = audit(withComponents(decl), cs);
    const hit = r.errors.find((e) => e.includes('.ui-audit-selftest') && e.includes(`(${kind})`));
    if (!hit) bad.push(`planted ${name} (${decl}) was not reported as ${kind}`);
    else report.push(`${name}: ${decl}`);
  }
  for (const decl of CLEAN) {
    const r = audit(withComponents(decl), cs);
    if (r.errors.some((e) => e.includes('.ui-audit-selftest'))) bad.push(`a clean declaration was reported as off the system: ${decl}`);
  }
  // an id selector in the system layer
  if (!audit(Object.assign({}, files, { 'dwight-components.css': files['dwight-components.css'] + '\n#ui-audit-selftest{color:var(--text)}\n' }), cs).errors.some((e) => e.includes('id selector'))) bad.push('a planted id selector was not reported');
  // a shell is strict: a stray value in its own style block, and an inline style
  const shell = 'panel-shell.html';
  const added = audit(Object.assign({}, files, { [shell]: files[shell].replace(/<\/style>(?![\s\S]*<\/style>)/, '.ui-audit-selftest{margin:7px}</style>') }), cs);
  if (!added.errors.some((e) => e.startsWith(shell + ':') && e.includes('(space)'))) bad.push('a stray margin added to a shell was not reported');
  const inline = audit(Object.assign({}, files, { [shell]: files[shell].replace('<body', '<div style="margin-bottom:7px"></div><body') }), cs);
  if (!inline.errors.some((e) => e.startsWith(shell + ':') && e.includes('(space)'))) bad.push('an inline style added to a shell was not reported');
  // an icon drawn anywhere but the one table, in a shell or in C#, and a name the table does not hold
  const someShell = Object.keys(SURFACES).find((f) => f.endsWith('.html'));
  const iconCases = [
    ['a stroke-drawn icon in a shell', Object.assign({}, files, { [someShell]: files[someShell] + '<svg class="ic" viewBox="0 0 24 24"><path d="M0 0"/></svg>' }), cs, 'an icon drawn here'],
    ['an icon with no viewBox in a shell', Object.assign({}, files, { [someShell]: files[someShell] + '<svg class="ic"><path d="M0 0"/></svg>' }), cs, 'an icon drawn here'],
    ['an icon drawn in C#', files, Object.assign({}, cs, { 'planted.cs': 'var x = "<svg class=\\"ic\\" viewBox=\\"0 0 24 24\\"></svg>";' }), 'an icon drawn here'],
    ['a name the table does not hold', Object.assign({}, files, { [someShell]: files[someShell] + '<svg class="ic"><use href="#ic-nosuchicon"/></svg>' }), cs, 'not in the one table'],
    ['a shell that does not inline the table', Object.assign({}, files, { [someShell]: files[someShell].replace('<!--__DWIGHT_ICONS__-->', '') }), cs, 'does not inline'],
  ];
  for (const [what, f, c, says] of iconCases) if (!audit(f, c).errors.some((e) => e.includes(says))) bad.push(what + ' was not reported');
  // the display size, anywhere but the tour
  if (!audit(Object.assign({}, files, { 'dwight-components.css': files['dwight-components.css'] + '\n.ui-audit-selftest{font:var(--type-display) var(--font-display)}\n' }), cs).errors.some((e) => e.includes("the tour's alone"))) bad.push('the display size used outside the tour was not reported');
  // an orphan class, and a style attribute written in C#
  const orphan = audit(files, Object.assign({}, cs, { 'planted.cs': 'var x = "<span class=\\"ui-audit-selftest-orphan\\">";' }));
  if (!orphan.errors.some((e) => e.includes('ui-audit-selftest-orphan'))) bad.push('a class the C# writes with no rule was not reported');
  const styled = audit(files, Object.assign({}, cs, { 'planted.cs': 'var x = "<span style=\\"color:red\\">";' }));
  if (!styled.errors.some((e) => e.includes('style attribute'))) bad.push('a style attribute written in C# was not reported');
  if (bad.length) { say('INSTRUMENT BROKEN - the audit cannot judge the product:'); for (const b of bad) say('  ' + b); return false; }
  say(`  self-test: ${PLANTS.length} planted stray values reported (${report.slice(0, 4).join('; ')}; and ${PLANTS.length - 4} more), ${CLEAN.length} clean ones passed; planted id selector, a stray value in a shell, orphan class, C# style attribute, inline style and an icon from outside the one table all reported.`);
  return true;
}

// ---- the counts: the audit's table, from the same reader, for before and after ----------------------------------------
function counts() {
  const { files } = readProduct();
  const tiers = readTiers(files[TOKENS]);
  const light = {};
  for (const m of blankComments(files[TOKENS].slice(0, files[TOKENS].indexOf('body.dark{'))).matchAll(/(--[\w-]+)\s*:\s*([^;{}]+);/g)) light[m[1]] = m[2].trim();
  const res = (v, n = 0) => (n > 6 ? v : v.replace(/var\(\s*(--[\w-]+)\s*\)/g, (a, name) => (light[name] != null ? res(light[name], n + 1) : a)));
  const PROPS = { 'font size': (p) => p === 'font-size', 'line height': (p) => p === 'line-height', 'letter spacing': (p) => p === 'letter-spacing', radius: (p) => p === 'border-radius', shadow: (p) => p === 'box-shadow' };
  const table = {};
  const add = (k, s, v) => { table[k] = table[k] || {}; (table[k][s] = table[k][s] || new Set()).add(v); (table[k].all = table[k].all || new Set()).add(v); };
  for (const [file, surface] of Object.entries(SURFACES)) for (const d of declarationsOf(file, files[file])) {
    if (isHost(d)) continue;
    const v = res(d.value.replace(/\s*!important/, ''));
    for (const [k, f] of Object.entries(PROPS)) if (f(d.prop)) add(k, surface, v);
    if (SPACE_PROP.test(d.prop)) for (const t of splitTop(v)) if (!/^(auto|inherit)$/.test(t)) add('margin, padding, gap', surface, t);
    if (d.prop === 'font' && /^[\d.]+px/.test(v)) add('font size', surface, v.split(/\s/)[0]);
    if (/^(transition|animation|animation-duration|transition-duration)$/.test(d.prop)) for (const m of v.matchAll(/(?<![\w.])(\d*\.?\d+)(ms|s)\b/g)) add('duration', surface, m[0]);
  }
  const S = Object.values(SURFACES);
  const rows = [['property', ...S, 'all'].join(' | ')];
  for (const k of Object.keys(table)) rows.push([k, ...S.map((s) => (table[k][s] ? table[k][s].size : 0)), table[k].all.size].join(' | '));
  return rows.join('\n') + `\ntokens: ${Object.keys(tiers).length} (${['primitive', 'role', 'scale'].map((t) => t + ' ' + Object.values(tiers).filter((x) => x === t).length).join(', ')})`;
}

// The gate. Returns the number of failures, so the harness can add them to its own.
function run(say = console.log) {
  say('=== UI system: source audit');
  if (!selfTest(say)) return 1;
  const { files, cs } = readProduct();
  const r = audit(files, cs);
  say(`  read ${r.declarations} declarations, all strict; the C# writes ${r.emitted} classes.`);
  for (const e of r.errors) say('  FAIL  ' + e);
  if (!r.errors.length) say('   OK   nothing off the system, and every class the C# writes has a rule');
  say('');
  return r.errors.length;
}

module.exports = { run, audit, judge, readTiers, declarationsOf, readProduct };

if (require.main === module) {
  if (process.argv.includes('--counts')) { console.log(counts()); process.exit(0); }
  process.exit(run() ? 1 : 0);
}
