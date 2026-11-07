#!/usr/bin/env node
/*
 * Token-alias gate (#899).
 *
 * WHY THIS EXISTS. Dark mode painted LIGHT brand colours on every surface. dwight-tokens.css declared its
 * aliases (--brand:var(--amber-7) and fourteen more) on :root only, while body.dark redeclared only the
 * SCALE. A custom property's var() is resolved on the element that DECLARES it, so html resolved every
 * alias against the light scale and body inherited the resolved light value: under dark, --brand-tint
 * computed to #FDF0E2 and --accent-ink to #A0581F. The surface roles had been patched one by one in
 * body.dark - a hand-kept second list - and the brand aliases were simply the ones nobody had copied yet.
 *
 * THE RULE: in every theme, every alias computes to exactly what its target computes to. The list of
 * aliases is READ FROM THE TOKEN FILE, never typed here: a custom property is an alias when every
 * declaration of it in the file is var(--target) with one and the same target. A property the file gives a
 * literal anywhere is a per-theme ROLE (--divider is deliberately a different step in dark), not an alias;
 * roles are printed by name on every run, so nothing is excluded silently.
 *
 * WHY A REAL BROWSER AND NOT jsdom. Measured before choosing: jsdom's getComputedStyle NEVER resolves
 * var() - it returns "var(--amber-7)" for --brand, and it returns the same text before and after the fix.
 * A gate built on jsdom would have passed on the very defect it exists to catch. Do not "simplify" this
 * back to jsdom. The instrument is headless Chrome: the same Blink engine WebView2 renders the shells in,
 * preinstalled on the CI runner image. (Edge's headless mode printed nothing on the dev machine, so it is
 * not a candidate.) On the defective file this gate reports --brand-tint #FDF0E2 and --accent-ink #A0581F
 * under dark - the values measured independently inside the product's WebView2.
 *
 * WHY IT CANNOT QUIETLY PASS. It tests itself first: a planted copy of the defect must be found and a
 * fixed copy must come back clean, or it reports the INSTRUMENT as broken instead of judging the product.
 * No browser found, no output, or no aliases read is a failure, never a pass. And it checks its own
 * premise: the probe page carries the tokens and components CSS only, so any other shell file that starts
 * declaring custom properties fails the gate until the page carries it too.
 *
 * Usage: node tools/check-token-aliases.js            (the gate)
 *        node tools/check-token-aliases.js --dump F   (also write every custom property, on html and body,
 *                                                      in both themes, to F as JSON - a before/after witness)
 * DWIGHT_CHROMIUM overrides the browser path.
 */
const fs = require('fs');
const os = require('os');
const path = require('path');
const cp = require('child_process');
const url = require('url');

const RES = path.join('Dwight', 'Resources');
const TOKENS = path.join(RES, 'dwight-tokens.css');
const COMPONENTS = path.join(RES, 'dwight-components.css');
const MARK = '@@DWIGHT_TOKEN_PROBE@@';
const THEMES = ['light', 'dark'];
const ELEMENTS = ['html', 'body'];

// ---------------------------------------------------------------------------------------------
// Reading the file: which properties are aliases, and of what.
// ---------------------------------------------------------------------------------------------
const stripComments = (css) => css.replace(/\/\*[\s\S]*?\*\//g, '');

function readAliases(css) {
  const byName = new Map();
  for (const m of stripComments(css).matchAll(/(--[\w-]+)\s*:\s*([^;{}]*);/g)) {
    if (!byName.has(m[1])) byName.set(m[1], []);
    byName.get(m[1]).push(m[2].trim());
  }
  const aliases = [], roles = [];
  for (const [name, values] of byName) {
    const targets = values.map((v) => (/^var\(\s*(--[\w-]+)\s*\)$/.exec(v) || [])[1]);
    if (!targets.some(Boolean)) continue;                           // never an alias anywhere
    if (targets.every((t) => t && t === targets[0])) aliases.push([name, targets[0]]);
    else roles.push(name);                                          // an alias in one place, not in another
  }
  return { aliases, roles, declared: [...byName.keys()] };
}

// ---------------------------------------------------------------------------------------------
// Measuring: one headless Chrome load per (stylesheet, theme); computed values off html, body and a
// descendant of body (what a component reads).
// ---------------------------------------------------------------------------------------------
function findChromium() {
  if (process.env.DWIGHT_CHROMIUM) return fs.existsSync(process.env.DWIGHT_CHROMIUM) ? process.env.DWIGHT_CHROMIUM : null;
  const bases = [process.env.PROGRAMFILES, process.env['PROGRAMFILES(X86)'], process.env.LOCALAPPDATA].filter(Boolean);
  const candidates = bases.map((b) => path.join(b, 'Google', 'Chrome', 'Application', 'chrome.exe'))
    .concat(['/usr/bin/google-chrome', '/usr/bin/google-chrome-stable', '/usr/bin/chromium', '/usr/bin/chromium-browser']);
  return candidates.find((c) => fs.existsSync(c)) || null;
}

function measure(browser, css, theme, names) {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'dwight-token-probe-'));
  try {
    const page = path.join(dir, 'probe.html');
    fs.writeFileSync(page, `<!doctype html><html><head><meta charset="utf-8"><style>${css}</style></head>
<body class="${theme === 'dark' ? 'dark' : ''}"><div id="probe"></div><pre id="out"></pre><script>
(function(){
  var names = ${JSON.stringify(names)}, at = { html: document.documentElement, body: document.body,
    descendant: document.getElementById('probe') }, values = {};
  Object.keys(at).forEach(function(k){
    var cs = getComputedStyle(at[k]); values[k] = {};
    names.forEach(function(n){ values[k][n] = cs.getPropertyValue(n).trim(); });
  });
  document.getElementById('out').textContent = '${MARK}' + JSON.stringify({ ua: navigator.userAgent, values: values }) + '${MARK}';
})();
</script></body></html>`);
    const run = cp.spawnSync(browser, ['--headless=new', '--disable-gpu', '--no-first-run', '--no-default-browser-check',
      '--disable-extensions', '--user-data-dir=' + path.join(dir, 'profile'), '--dump-dom', url.pathToFileURL(page).href],
      { encoding: 'utf8', timeout: 90000 });
    const hit = new RegExp(MARK + '([\\s\\S]*?)' + MARK).exec(run.stdout || '');
    if (!hit) throw new Error(`no measurement came back (exit ${run.status}${run.error ? ', ' + run.error.message : ''}; stderr: ${(run.stderr || '').trim().slice(0, 300) || 'empty'})`);
    return JSON.parse(hit[1].replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&amp;/g, '&'));
  } finally {
    try { fs.rmSync(dir, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 }); } catch (e) { /* a temp dir */ }
  }
}

// Every alias must compute to the value at the END of its chain (--clay -> --brand -> --amber-7), on body and
// below it (where the shells paint). Comparing only against the immediate target passes an alias of an alias
// when both are wrong the same way - measured: it reported 12 of the 15 broken aliases. The chain's end must
// also compute to SOMETHING - two empty strings are equal and prove nothing.
function chainOf(alias, aliases) {
  const next = new Map(aliases), chain = [];
  for (let n = next.get(alias); n && !chain.includes(n); n = next.get(n)) chain.push(n);
  return chain;
}

function failures(aliases, measured) {
  const out = [];
  for (const where of ['body', 'descendant']) {
    const v = measured.values[where];
    for (const [alias] of aliases) {
      const chain = chainOf(alias, aliases), end = chain[chain.length - 1];
      if (!v[end]) out.push({ alias, where, why: `${chain.join(' -> ')} computes to nothing` });
      else if (v[alias] !== v[end]) out.push({ alias, where, why: `${alias} = ${v[alias] || '(nothing)'}, its target ${chain.join(' -> ')} = ${v[end]}` });
    }
  }
  return out;
}

// ---------------------------------------------------------------------------------------------
// The instrument's premise: the probe page carries EVERY declaration the product makes. The token file and
// the components file are the shared layer; a shell that declares custom properties of its own (the tour's
// --ink / --line / --paper layer, declared on body so they follow the theme) is carried into a probe of its
// own, on top of the shared layers, and its aliases are judged exactly as the shared ones are. A declaration
// the probe does not carry would be a colour this gate never measured - so the carried set is derived from
// the files, never listed by hand, and each shell is reported with what was carried for it. The one thing
// the probe cannot carry is a var() alias inside an inline style attribute (it binds to one element the probe
// does not have); a shell that does that breaks the instrument, as before.
// ---------------------------------------------------------------------------------------------
function shellDeclarations() {
  const shells = [];
  for (const f of fs.readdirSync(RES).filter((f) => /\.(html|css)$/.test(f)).sort()) {
    if (f === path.basename(TOKENS) || f === path.basename(COMPONENTS)) continue;
    const text = fs.readFileSync(path.join(RES, f), 'utf8');
    const blocks = f.endsWith('.css') ? [stripComments(text)]
      : [...text.matchAll(/<style[^>]*>([\s\S]*?)<\/style>/g)].map((m) => stripComments(m[1]));
    const css = blocks.join('\n');
    const inline = f.endsWith('.css') ? '' : [...text.matchAll(/style\s*=\s*"([^"]*)"/g)].map((m) => m[1]).join(';\n');
    const names = (s) => [...new Set([...s.matchAll(/(?:^|[\s;{"])(--[\w-]+)\s*:/g)].map((m) => m[1]))];
    const declared = names(css), inlineDeclared = names(' ' + inline);
    const inlineAliases = [...new Set([...inline.matchAll(/(--[\w-]+)\s*:\s*var\(/g)].map((m) => m[1]))];
    const scripted = [...new Set([...text.matchAll(/setProperty\(\s*['"](--[\w-]+)/g)].map((m) => m[1]))];
    if (declared.length || inlineDeclared.length || scripted.length)
      shells.push({ file: f, css, declared, inlineDeclared, inlineAliases, scripted });
  }
  return shells;
}

// ---------------------------------------------------------------------------------------------
function main() {
  const dumpAt = process.argv.indexOf('--dump');
  const dumpFile = dumpAt > 0 ? process.argv[dumpAt + 1] : null;
  const say = (s) => console.log(s);
  const broken = (why) => { say(`\nINSTRUMENT BROKEN - the gate cannot judge the product: ${why}`); process.exit(1); };

  const browser = findChromium();
  if (!browser) broken(process.env.DWIGHT_CHROMIUM
    ? `DWIGHT_CHROMIUM points at ${process.env.DWIGHT_CHROMIUM}, which does not exist.`
    : 'no Chromium found (looked for Chrome in Program Files, LocalAppData and /usr/bin; set DWIGHT_CHROMIUM).');

  // 1. Self-test on a planted copy of the defect, and on the same copy fixed.
  // --c-chain is an alias of an alias, broken the same way as its target - the case a link-by-link compare misses.
  const PLANTED = ':root{ --c-scale:#111111; --c-alias:var(--c-scale); --c-chain:var(--c-alias); } body.dark{ --c-scale:#222222; }';
  const FIXED = PLANTED.replace(':root{', ':root, body{');
  const ctl = readAliases(PLANTED);
  if (ctl.aliases.map((a) => a[0]).join() !== '--c-alias,--c-chain') broken(`the alias reader found ${JSON.stringify(ctl.aliases)} in the control, expected --c-alias and --c-chain.`);
  const names = (a) => [...new Set(a.flat())];
  let planted, fixed;
  try {
    planted = measure(browser, PLANTED, 'dark', names(ctl.aliases));
    fixed = measure(browser, FIXED, 'dark', names(ctl.aliases));
  } catch (e) { broken(e.message); }
  const pf = failures(ctl.aliases, planted).filter((f) => f.where === 'body'), ff = failures(ctl.aliases, fixed);
  if (pf.map((f) => f.alias).join() !== '--c-alias,--c-chain') broken(`the planted defect was NOT found in full (found ${JSON.stringify(pf.map((f) => f.alias))}; the browser reported ${JSON.stringify(planted.values.body)}). An engine that does not resolve var() cannot see this class.`);
  if (ff.length !== 0) broken(`the fixed control was reported as failing: ${ff.map((f) => f.why).join('; ')}`);
  const engine = (/(HeadlessChrome\/[\d.]+|Chrome\/[\d.]+)/.exec(planted.ua) || [planted.ua])[0];
  say(`Token-alias gate (#899), measured in ${engine}.`);
  say(`  self-test: planted defect found, alias and alias-of-alias (${pf.map((f) => f.why).join('; ')}); fixed control clean.`);

  // 2. The premise, then the product: the shared layer first, then every shell that declares on top of it.
  const shells = shellDeclarations();
  for (const s of shells)
    if (s.inlineAliases.length)
      broken(`${s.file} declares ${s.inlineAliases.join(', ')} as var() aliases in inline style attributes, which the probe page cannot carry.`);

  const shared = fs.readFileSync(TOKENS, 'utf8') + '\n' + fs.readFileSync(COMPONENTS, 'utf8');
  const probes = [{ name: `${path.basename(TOKENS)} + ${path.basename(COMPONENTS)}`, css: shared, own: null }]
    .concat(shells.map((s) => ({ name: `${s.file}, carried on the shared layers`, css: shared + '\n' + s.css, own: s })));

  const all = [], dump = {};
  for (const p of probes) {
    const product = readAliases(p.css);
    if (p.own === null && product.aliases.length === 0) broken(`no aliases were read from ${TOKENS}.`);
    const ownAliases = p.own === null ? product.aliases : product.aliases.filter(([n]) => p.own.declared.includes(n));
    const ownRoles = p.own === null ? product.roles : product.roles.filter((n) => p.own.declared.includes(n));
    if (p.own === null) {
      say(`  aliases read from ${TOKENS}: ${product.aliases.length}`);
      say(`  per-theme roles, not aliases (given a literal somewhere in the file): ${ownRoles.length ? ownRoles.join(', ') : 'none'}`);
    } else {
      const extras = [];
      if (ownRoles.length) extras.push(`per-theme roles ${ownRoles.join(', ')}`);
      if (p.own.inlineDeclared.length) extras.push(`inline literals ${p.own.inlineDeclared.join(', ')}`);
      if (p.own.scripted.length) extras.push(`set from script ${p.own.scripted.join(', ')}`);
      say(`  ${p.name}: ${ownAliases.length} alias(es) of its own${extras.length ? ' (not judged: ' + extras.join('; ') + ')' : ''}`);
      if (ownAliases.length === 0) continue;
    }
    for (const theme of THEMES) {
      let m;
      try { m = measure(browser, p.css, theme, product.declared); } catch (e) { broken(e.message); }
      if (p.own === null) dump[theme] = { html: m.values.html, body: m.values.body };
      // Every alias in the probe is judged - the shared ones again under the shell's own rules, because a shell
      // that broke a shared alias would paint it wrong just the same - and the chain runs to its END through both.
      const f = failures(product.aliases, m);
      const shown = f.filter((x) => x.where === 'body');                 // body and its descendants agree by inheritance;
      const extra = f.filter((x) => x.where !== 'body' && !shown.some((s) => s.alias === x.alias));  // show a split if not
      say(`  ${p.own ? '  ' : ''}${theme}: ${product.aliases.length - new Set(f.map((x) => x.alias)).size} of ${product.aliases.length} aliases hold` + (f.length ? `, ${new Set(f.map((x) => x.alias)).size} FAIL` : ''));
      for (const x of shown.concat(extra)) say(`    FAIL under ${theme} (${p.name}, ${x.where}): ${x.why}`);
      all.push(...f);
    }
  }
  if (dumpFile) { fs.writeFileSync(dumpFile, JSON.stringify(dump, null, 2) + '\n'); say(`  dump: every custom property on html and body, both themes -> ${dumpFile}`); }

  if (all.length) {
    say(`\nFAILED: an alias that does not compute to its target paints the wrong colour in that theme. Declare the`);
    say(`aliases where every themed element resolves them (the token block's selector), never a second list in body.dark.`);
    process.exit(1);
  }
  say(`\nAll aliases compute to their targets in both themes${shells.length ? ', in the shared layers and in ' + shells.map((s) => s.file).join(', ') : ''}.`);
}

main();
