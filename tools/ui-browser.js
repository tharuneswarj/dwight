/*
 * What the checks that drive a real browser share (tools/README.md): finding Chromium the way tools/ui-measure.js does, launching
 * it, and inlining a shell's real assets the way ShellAssets.Inline does, with the CSP stripped so the check can call in.
 *
 * puppeteer-core is deliberately not a dependency of the repo, because CI runs none of these. Install it without saving:
 *   npm install --no-save puppeteer-core@23.11.1
 */
const fs = require('fs');
const os = require('os');
const path = require('path');

function findChromium() {
  if (process.env.DWIGHT_CHROMIUM) return fs.existsSync(process.env.DWIGHT_CHROMIUM) ? process.env.DWIGHT_CHROMIUM : null;
  const bases = [process.env.PROGRAMFILES, process.env['PROGRAMFILES(X86)'], process.env.LOCALAPPDATA].filter(Boolean);
  const candidates = bases.map((b) => path.join(b, 'Google', 'Chrome', 'Application', 'chrome.exe'))
    .concat(['/usr/bin/google-chrome', '/usr/bin/google-chrome-stable', '/usr/bin/chromium', '/usr/bin/chromium-browser']);
  return candidates.find((c) => fs.existsSync(c)) || null;
}

// A real scrollbar stays on: puppeteer hides them by default, which makes every width 8px optimistic.
async function launch(name) {
  let puppeteer;
  try { puppeteer = require('puppeteer-core'); } catch (e) {
    console.error('This check needs puppeteer-core, which the repo does not depend on. Run: npm install --no-save puppeteer-core@23.11.1');
    process.exit(1);
  }
  const executablePath = findChromium();
  if (!executablePath) {
    console.error('No Chromium found (looked for Chrome in Program Files, LocalAppData and /usr/bin; set DWIGHT_CHROMIUM).');
    process.exit(1);
  }
  const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'dwight-' + name + '-'));
  const browser = await puppeteer.launch({ executablePath, headless: 'new', ignoreDefaultArgs: ['--hide-scrollbars'], userDataDir: path.join(tmp, 'profile'), args: ['--no-first-run', '--disable-extensions'] });
  return { browser, tmp, close: async () => { await browser.close(); try { fs.rmSync(tmp, { recursive: true, force: true, maxRetries: 3, retryDelay: 200 }); } catch (e) { /* a profile the OS still holds; it is in the temp folder */ } } };
}

function markSvg(root) {
  const cs = fs.readFileSync(path.join(root, 'Dwight', 'Branding', 'DwightIcon.cs'), 'utf8');
  const grid = [...cs.split('Grid =')[1].split('};')[0].matchAll(/"([^"]+)"/g)].map((m) => m[1]);
  const pal = {}; for (const m of cs.matchAll(/\['(\w)'\] = ColorTranslator\.FromHtml\("(#[0-9A-F]+)"\)/g)) pal[m[1]] = m[2];
  let s = `<svg class="mark" viewBox="0 0 ${grid.length} ${grid.length}" shape-rendering="crispEdges" aria-hidden="true">`;
  grid.forEach((row, y) => [...row].forEach((c, x) => { if (pal[c]) s += `<rect x="${x}" y="${y}" width="1.04" height="1.04" fill="${pal[c]}"/>`; }));
  return s + '</svg>';
}

function inlineShell(root, file, extraCss) {
  const R = (n) => fs.readFileSync(path.join(root, 'Dwight', 'Resources', n), 'utf8');
  return R(file).split('/*__DWIGHT_FONTS__*/').join(R('dwight-fonts.css')).split('/*__DWIGHT_TOKENS__*/').join(R('dwight-tokens.css'))
    .split('/*__DWIGHT_COMPONENTS__*/').join(R('dwight-components.css') + (extraCss || '')).split('<!--__DWIGHT_ICONS__-->').join(R('dwight-icons.svg'))
    .split('/*__DWIGHT_DROPDOWN__*/').join(R('dwight-dropdown.js')).split('/*__DWIGHT_LINKS__*/').join(R('dwight-links.js')).split('/*__DWIGHT_DECK__*/').join(R('dwight-deck.js')).split('<!--__DWIGHT_MARK__-->').join(markSvg(root))
    .replace(/<meta[^>]*Content-Security-Policy[^>]*>/i, '');
}

module.exports = { findChromium, launch, markSvg, inlineShell };
