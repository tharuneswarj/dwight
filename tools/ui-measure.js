#!/usr/bin/env node
/*
 * The UI system's measuring leg (Brief BK). Run by tools/check-shell-render.js, and by itself for its reports:
 *   node tools/ui-measure.js                    the gate
 *   node tools/ui-measure.js --counts           what the page computes, per surface, in both themes (the audit's table)
 *
 * WHY IT EXISTS. tools/ui-style-audit.js reads the source, and the source can look perfect while the screen ignores it:
 * `#body p` (1,0,1) beat `.consent .consent-note` (0,2,0), so the card's whole note style never applied to anything and every
 * reading of the CSS said it did. So this leg asks the page, in the engine WebView2 wraps: on the elements you match, did you
 * win? Three questions, each with a planted positive control:
 *
 *   1. Does a rule lose on every element it matches, to an id selector? One that does fails.
 *   2. Does every rule of the system layer win, and is every one of them on the sheet? A rule the sheet does not show has
 *      not been seen by anybody.
 *   3. Do the ruled values compute? --subtle's step, and the fill and the text on it (Tharun, R1), in both themes.
 *
 * WHY HEADLESS CHROME AND NOT jsdom, measured for the token-alias gate: jsdom never resolves var() and does no layout. Each
 * state renders in an iframe at the shell's real window size, because Chrome's headless window will not go under 500px wide.
 * The CSP is stripped from the copy measured so the fixtures can call in; nothing here measures the CSP.
 *
 * A LIMIT, stated where its next reader will look: this is Blink, not WebView2 at Windows DPI. It reports arithmetic and
 * cascade, never how the screen looks. Fixtures reach what they reach, so a count is a floor.
 */
const fs = require('fs');
const os = require('os');
const path = require('path');
const cp = require('child_process');
const url = require('url');
const { STATES } = require('./ui-fixtures.js');

const ROOT = path.resolve(__dirname, '..');
const RES = path.join(ROOT, 'Dwight', 'Resources');
const SHEET = path.join(ROOT, 'docs', 'dwight-ui-system-sheet.html');
const MARK = '@@DWIGHT_UI_MEASURE@@';
const read = (n) => fs.readFileSync(path.join(RES, n), 'utf8');

function findChromium() {
  if (process.env.DWIGHT_CHROMIUM) return fs.existsSync(process.env.DWIGHT_CHROMIUM) ? process.env.DWIGHT_CHROMIUM : null;
  const bases = [process.env.PROGRAMFILES, process.env['PROGRAMFILES(X86)'], process.env.LOCALAPPDATA].filter(Boolean);
  const candidates = bases.map((b) => path.join(b, 'Google', 'Chrome', 'Application', 'chrome.exe'))
    .concat(['/usr/bin/google-chrome', '/usr/bin/google-chrome-stable', '/usr/bin/chromium', '/usr/bin/chromium-browser']);
  return candidates.find((c) => fs.existsSync(c)) || null;
}

function markSvg() {
  const cs = fs.readFileSync(path.join(ROOT, 'Dwight', 'Branding', 'DwightIcon.cs'), 'utf8');
  const grid = [...cs.split('Grid =')[1].split('};')[0].matchAll(/"([^"]+)"/g)].map((m) => m[1]);
  const pal = {}; for (const m of cs.matchAll(/\['(\w)'\] = ColorTranslator\.FromHtml\("(#[0-9A-F]+)"\)/g)) pal[m[1]] = m[2];
  let s = `<svg class="mark" viewBox="0 0 ${grid.length} ${grid.length}" shape-rendering="crispEdges" aria-hidden="true">`;
  grid.forEach((row, y) => [...row].forEach((c, x) => { if (pal[c]) s += `<rect x="${x}" y="${y}" width="1.04" height="1.04" fill="${pal[c]}"/>`; }));
  return s + '</svg>';
}

// As ShellAssets.Inline does, with the CSP out so the fixtures can call in. The fonts are heavy and only --counts needs them.
function inlineShell(file, withFonts) {
  const mark = markSvg();
  return read(file).replace(/<meta http-equiv="Content-Security-Policy"[^>]*>/, '')
    .split('/*__DWIGHT_FONTS__*/').join(withFonts ? read('dwight-fonts.css') : '')
    .split('/*__DWIGHT_TOKENS__*/').join(read('dwight-tokens.css'))
    .split('/*__DWIGHT_COMPONENTS__*/').join(read('dwight-components.css'))
    .split('<!--__DWIGHT_ICONS__-->').join(read('dwight-icons.svg'))
    .split('/*__DWIGHT_DROPDOWN__*/').join(read('dwight-dropdown.js')).split('/*__DWIGHT_LINKS__*/').join(read('dwight-links.js')).split('/*__DWIGHT_DECK__*/').join(read('dwight-deck.js'))
    .split('<!--__DWIGHT_MARK__-->').join(mark);
}

// The walk runs inside each iframe.
function walkPage() {
  const parseCol = (c) => {
    if (!c) return null; let m;
    if ((m = /^rgba?\(([\d.]+),\s*([\d.]+),\s*([\d.]+)(?:,\s*([\d.]+))?\)/.exec(c))) return [+m[1], +m[2], +m[3], m[4] == null ? 1 : +m[4]];
    if ((m = /^color\(srgb ([\d.e-]+) ([\d.e-]+) ([\d.e-]+)(?: \/ ([\d.e-]+))?\)/.exec(c))) return [m[1] * 255, m[2] * 255, m[3] * 255, m[4] == null ? 1 : +m[4]];
    return null;
  };
  const over = (f, b) => { const a = f[3]; return [f[0] * a + b[0] * (1 - a), f[1] * a + b[1] * (1 - a), f[2] * a + b[2] * (1 - a), 1]; };
  const lum = (c) => { const g = (v) => { v /= 255; return v <= .03928 ? v / 12.92 : Math.pow((v + .055) / 1.055, 2.4); }; return .2126 * g(c[0]) + .7152 * g(c[1]) + .0722 * g(c[2]); };
  const ratio = (a, b) => { const x = lum(a), y = lum(b); return (Math.max(x, y) + .05) / (Math.min(x, y) + .05); };
  const hex = (c) => '#' + c.slice(0, 3).map((v) => Math.round(v).toString(16).padStart(2, '0')).join('').toUpperCase();
  const bgOf = (el) => { const stack = []; let e = el; while (e && e.nodeType === 1) { const c = parseCol(getComputedStyle(e).backgroundColor); if (c && c[3] > 0) { stack.push(c); if (c[3] >= 1) break; } e = e.parentElement; } let base = [255, 255, 255, 1]; for (let i = stack.length - 1; i >= 0; i--) base = stack[i][3] >= 1 ? stack[i] : over(stack[i], base); return base; };
  const name = (el) => { const cl = (el.getAttribute && el.getAttribute('class')) ? '.' + el.getAttribute('class').trim().split(/\s+/).join('.') : ''; return el.tagName.toLowerCase() + cl; };
  const key = (el) => { const p = el.parentElement; return (p && p !== document.body ? name(p) + ' > ' : '') + name(el); };
  const out = { text: [], box: [], controls: [], icons: [], overflow: null };
  for (const el of document.querySelectorAll('body *')) {
    if (el.tagName === 'SCRIPT' || el.tagName === 'STYLE') continue;
    if (el.closest('svg') && el.tagName.toLowerCase() !== 'svg') continue;
    const r = el.getBoundingClientRect();
    if (!r.width || !r.height) continue;
    const cs = getComputedStyle(el);
    if (cs.visibility === 'hidden' || cs.display === 'none') continue;
    const k = key(el);
    const hasText = [...el.childNodes].some((n) => n.nodeType === 3 && n.textContent.trim());
    const ph = el.tagName === 'INPUT' || el.tagName === 'TEXTAREA';
    if (hasText || ph) {
      const fg = parseCol(cs.color) || [0, 0, 0, 1];
      const bg = bgOf(el);
      const solid = fg[3] < 1 ? over(fg, bg) : fg;
      out.text.push({ k, dull: !!el.closest('.ev.undone:not(.open):not(.inwalk)>.ev-head,.yo.dull:not(.inwalk)'), fam: cs.fontFamily.split(',')[0].replace(/["']/g, '').trim(), size: parseFloat(cs.fontSize), weight: cs.fontWeight, lh: cs.lineHeight === 'normal' ? 'normal' : +(parseFloat(cs.lineHeight) / parseFloat(cs.fontSize)).toFixed(2), ls: cs.letterSpacing, tt: cs.textTransform, color: hex(solid), bg: hex(bg), cr: +ratio(solid, bg).toFixed(2), sample: (el.textContent || '').trim().slice(0, 40) });
    }
    const pad = [cs.paddingTop, cs.paddingRight, cs.paddingBottom, cs.paddingLeft].join(' ');
    const mar = [cs.marginTop, cs.marginRight, cs.marginBottom, cs.marginLeft].join(' ');
    out.box.push({ k, pad, mar, gap: cs.columnGap === 'normal' ? null : cs.columnGap + '/' + cs.rowGap, bw: [cs.borderTopWidth, cs.borderRightWidth, cs.borderBottomWidth, cs.borderLeftWidth].join(' '), bs: cs.borderTopStyle, radius: cs.borderTopLeftRadius, shadow: cs.boxShadow, h: +r.height.toFixed(2), w: +r.width.toFixed(2), right: r.right });
    const isControl = /^(BUTTON|INPUT|TEXTAREA|SELECT|SUMMARY)$/.test(el.tagName) || el.classList.contains('dd-sel') || el.classList.contains('modecard') || el.classList.contains('dd-item');
    if (isControl) {
      const fg = parseCol(cs.color) || [0, 0, 0, 1]; const bg = bgOf(el);
      out.controls.push({ k, tag: el.tagName, type: el.getAttribute('type') || '', h: +r.height.toFixed(2), w: +r.width.toFixed(2), size: parseFloat(cs.fontSize), fam: cs.fontFamily.split(',')[0].replace(/["']/g, '').trim(), weight: cs.fontWeight, pad, bw: cs.borderTopWidth, shadow: cs.boxShadow, outline: cs.outlineStyle + ' ' + cs.outlineWidth, cr: +ratio(fg[3] < 1 ? over(fg, bg) : fg, bg).toFixed(2), label: (el.textContent || el.getAttribute('title') || '').trim().slice(0, 28) });
    }
    if (el.tagName.toLowerCase() === 'svg') out.icons.push({ k, cls: el.getAttribute('class') || '', w: +r.width.toFixed(2), h: +r.height.toFixed(2), stroke: cs.strokeWidth });
  }
  // Which declared rule properties actually win on the elements they match.
  const PROPS = ['font-size', 'color', 'line-height', 'font-weight', 'font-family', 'letter-spacing', 'margin-top', 'margin-bottom', 'padding-top', 'padding-left', 'background-color'];
  const rules = [];
  const eat = (list, at) => { for (const r of list) { if (r.cssRules && r.media) { if (matchMedia(r.media.mediaText).matches) eat(r.cssRules, r.media.mediaText); continue; } if (r.selectorText) rules.push(r); } };
  for (const sh of document.styleSheets) { try { eat(sh.cssRules); } catch (e) {} }
  const idRules = rules.filter((r) => /#[a-zA-Z]/.test(r.selectorText));
  // The library: the sheet that holds .btn.primary:hover, a rule no page draws for itself. Inlined or linked, it is one sheet.
  const lib = (rules.find((r) => r.selectorText === '.btn.primary:hover') || {}).parentStyleSheet;
  out.rules = [];
  for (const r of rules) {
    const sels = r.selectorText.split(',').map((x) => x.trim()).filter((x) => !x.includes(':') );
    if (!sels.length) continue;
    for (const prop of PROPS) {
      const v = r.style.getPropertyValue(prop); if (!v || v === 'inherit') continue;
      let matched = 0, won = 0, byId = 0, seen = '';
      for (const sel of sels) {
        let els; try { els = [...document.querySelectorAll(sel)]; } catch (e) { continue; }
        for (const el of els) {
          if (el.closest('svg')) continue; const b = el.getBoundingClientRect(); if (!b.width || !b.height) continue;
          const probe = document.createElement(el.tagName === 'BUTTON' ? 'span' : 'span'); probe.style.setProperty('position', 'absolute'); probe.style.setProperty('visibility', 'hidden'); if (prop !== 'font-size') probe.style.setProperty('font-size', getComputedStyle(el).fontSize); probe.style.setProperty(prop, v);
          (el.parentElement || el).appendChild(probe); const want = getComputedStyle(probe).getPropertyValue(prop); probe.remove();
          const got = getComputedStyle(el).getPropertyValue(prop);
          matched++; if (want === got) won++; else { seen = got + ' (declared ' + want + ')'; if (idRules.some((br) => br !== r && br.style.getPropertyValue(prop) && el.matches(br.selectorText))) byId++; }
        }
      }
      if (matched) out.rules.push({ sel: r.selectorText, prop, decl: v, matched, won, byId, seen, lib: r.parentStyleSheet === lib });
    }
  }
  // The system layer: which of its rules select anything on this page, whatever properties they set.
  out.sys = rules.filter((r) => r.parentStyleSheet === lib).map((r) => { let hit = 0; try { hit = document.querySelectorAll(r.selectorText.replace(/::?[a-z-]+(\([^)]*\))?/g, '').replace(/\s+,/g, ',').replace(/>\s*(,|$)/g, '$1').split(',').map((p) => p.trim()).filter(Boolean).join(',') || '*').length; } catch (e) { hit = -1; } return { sel: r.selectorText, hit }; });
  // R7, restated (Tharun, Brief BO): a closed dropdown shows each option's whole name on one line, a name in a menu never wraps, and a menu that fits its window
  // does not scroll. Each dropdown's longest name is put in its closed control and measured there.
  const inkW = (e) => { const r = document.createRange(); r.selectNodeContents(e); return r.getBoundingClientRect().width; };
  out.ddOverflow = [...document.querySelectorAll('.dd')].filter((d) => d.getBoundingClientRect().width > 0 && d.dwightNames).flatMap((d) => { const t = d.querySelector('.dd-txt'); if (!t) return []; const was = t.textContent, room = t.getBoundingClientRect().width; const over = d.dwightNames.filter((n) => { t.textContent = n; return inkW(t) > room + 0.25; }); t.textContent = was; return over; });
  out.menuOver = [...document.querySelectorAll('.ddm')].filter((m) => { const b = m.getBoundingClientRect(); return b.top < 0 || b.bottom > innerHeight || m.scrollHeight > m.clientHeight + 1 || [...m.querySelectorAll('.ddo-name')].some((n) => n.getBoundingClientRect().height > 24 || inkW(n) > n.parentElement.getBoundingClientRect().width + 0.25); }).map((m) => m.getAttribute('aria-label') || 'a menu');
  // A table cell's words stay inside the cell: "partly priced" is what stops a false zero. Read at the window's real height, so a scrollbar takes its width.
  // Brief BQ: a closed History row is one line, however long its title; and a question, whose words have no upper length, always leaves its buttons in view and room for a row.
  out.rowOver = [...document.querySelectorAll('.ev:not(.open)>.ev-head')].filter((h) => { const t = h.querySelector('.t'); if (!t || !t.getBoundingClientRect().width) return false; const r = document.createRange(); r.selectNodeContents(t); return new Set([...r.getClientRects()].map((c) => Math.round(c.top))).size > 1 || h.scrollHeight > h.clientHeight + 1; }).map((h) => (h.textContent || '').trim().slice(0, 40));
  out.confirmOver = [...document.querySelectorAll('.hconfirm')].filter((c) => { const p = c.closest('.dpane'); if (!p) return false; const pb = p.getBoundingClientRect(), bb = c.querySelector('.btn').getBoundingClientRect(); return bb.bottom > pb.bottom || p.clientHeight - c.offsetHeight < 32; }).map((c) => (c.textContent || '').trim().slice(0, 40));
  // Brief BR: one title bar. In an open window "Dwight" and minimise stand in the same place in every view, and no verb's name clips.
  const bar = document.querySelector('.titlebar'), barWho = bar && bar.querySelector('.who');
  if (barWho && barWho.getBoundingClientRect().width) { const pr = bar.querySelectorAll('.iconbtn'), mins = [...pr].filter((b) => b.getBoundingClientRect().width), last = mins[mins.length - 1], act = bar.querySelector('.act'); out.bar = { w: innerWidth, h: bar.offsetHeight, who: Math.round(barWho.getBoundingClientRect().left), min: last ? Math.round(last.getBoundingClientRect().left) : null, clip: !!(act && act.scrollWidth > act.clientWidth) && (act.textContent || '').trim() }; }
  // A group's label in a menu is a heading, and cannot be taken for a choice (Tharun): heavier and no fainter than its options, set outside them, and not a thing to click.
  out.grpOver = [...document.querySelectorAll('.chipmenu .grp')].filter((g) => { const o = g.nextElementSibling; if (!o || !g.getBoundingClientRect().width) return false; const gc = getComputedStyle(g), oc = getComputedStyle(o), lum = (c) => { const m = c.match(/[\d.]+/g).map(Number); return 0.2126 * m[0] + 0.7152 * m[1] + 0.0722 * m[2]; }; const dark = document.body.classList.contains('dark'), inked = dark ? lum(gc.color) >= lum(oc.color) : lum(gc.color) <= lum(oc.color); return !(parseInt(gc.fontWeight, 10) >= 600 && parseInt(gc.fontWeight, 10) > parseInt(oc.fontWeight, 10) && inked && gc.fontFamily !== oc.fontFamily && gc.cursor !== 'pointer' && g.tagName !== 'BUTTON' && parseFloat(oc.paddingLeft) > parseFloat(gc.paddingLeft)); }).map((g) => (g.textContent || '').trim());
  // Words stacked one per line mean a sentence is in a box far too narrow for it, whatever put it there. A class meant for another surface did this to the key's status (#139), and no check on one row would find the next one.
  out.wordColumn = (() => { const found = [], w = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT); while (w.nextNode()) { const n = w.currentNode, text = n.nodeValue.trim(), words = text.split(/\s+/).filter(Boolean), el = n.parentElement; if (words.length < 3 || !el || el.closest('svg,script,style,template,pre')) continue; const r = document.createRange(); r.selectNodeContents(n); const rects = [...r.getClientRects()].filter((c) => c.width > 0); if (rects.length && new Set(rects.map((c) => Math.round(c.top))).size >= words.length) found.push(text.slice(0, 50)); } return found; })();
  // The key's status sits on one line beside its dot, and in a window too narrow for that it wraps as a sentence, in all the room the row has.
  { const st = document.querySelector('.keyrow .st'), row = st && st.parentElement, dot = row && row.querySelector('.keydot'); if (st && st.getBoundingClientRect().width) { const rg = document.createRange(); rg.selectNodeContents(st); const b = st.getBoundingClientRect(); out.keyLine = { w: innerWidth, text: (st.textContent || '').trim(), lines: new Set([...rg.getClientRects()].map((c) => Math.round(c.top))).size, box: Math.round(b.width), room: Math.round(row.getBoundingClientRect().width - (dot ? dot.getBoundingClientRect().width + parseFloat(getComputedStyle(row).columnGap || 0) : 0)) }; } }
  // Nothing that floats over a card holds words. Its corner is for its pin and the glyph in it: the Walk's step line once landed inside that pin and sat over the sentence, a word or two to a line, which the stacked-words check above let through (the eleventh gate, Tharun).
  out.floatText = [...document.querySelectorAll('.step *, .leadwrap *, .outcome *, .fault *')].filter((e) => getComputedStyle(e).position === 'absolute' && e.textContent.trim().length > 0).map((e) => e.textContent.trim().slice(0, 50));
  // Every control in a footer has to be on the window. A sixth control in a Walk's footer put Copy off the edge at 340 wide, and nothing here looked (Brief DJ).
  out.footOver = [...document.querySelectorAll('.foot')].filter((f) => f.getBoundingClientRect().width > 0 && f.scrollWidth > f.clientWidth + 1).map((f) => f.scrollWidth - f.clientWidth);
  out.cellClip = [...document.querySelectorAll('.usage th, .usage td:not(.empty), .sendstable th, .form .btn, .foot .btn, .dock .btn, .dock .tag')].filter((c) => c.getBoundingClientRect().width > 0).map((c) => {
    const s = getComputedStyle(c), r = document.createRange(); r.selectNodeContents(c);
    const over = r.getBoundingClientRect().width - (c.getBoundingClientRect().width - parseFloat(s.paddingLeft) - parseFloat(s.paddingRight) - parseFloat(s.borderLeftWidth) - parseFloat(s.borderRightWidth));
    return over > 0.25 ? (c.textContent || '').trim().slice(0, 40) + ' by ' + over.toFixed(1) + 'px' : null;
  }).filter(Boolean);
  // The tour's window is fixed, so its words must fit the room under the picture.
  out.copyOver = [...document.querySelectorAll('.copy')].filter((c) => c.scrollHeight > c.clientHeight).map((c) => (c.textContent || '').trim().slice(0, 50));
  out.fitItems = [...document.querySelectorAll('.dd, .usage th, .sendstable th, .btn, .tag')].filter((e) => e.getBoundingClientRect().width > 0).length;
  out.fontsLoaded = [...document.fonts].filter((f) => f.status === 'loaded').map((f) => f.family.replace(/['"]/g, ''));
  const sb = document.createElement('div'); sb.style.cssText = 'overflow:scroll;width:100px;height:50px;position:absolute;visibility:hidden'; document.body.appendChild(sb); out.scrollbarW = sb.offsetWidth - sb.clientWidth; sb.remove();
  const rect = (q) => { const e = document.querySelector(q); if (!e) return null; const b = e.getBoundingClientRect(); return b.height ? { h: +b.height.toFixed(2), w: +b.width.toFixed(2), top: +b.top.toFixed(2), mt: getComputedStyle(e).marginTop } : null; };
  out.composer = { composer: rect('.composer'), textarea: rect('.composer textarea'), send: rect('.composer .send'), stop: rect('.composer .stopbtn'), chathdr: rect('.chathdr'), foot: rect('.foot') };
  const de = document.documentElement;
  out.overflow = { scrollW: de.scrollWidth, clientW: de.clientWidth, bodyScrollW: document.body.scrollWidth, tooWide: [...document.querySelectorAll('body *')].filter((e) => { const r = e.getBoundingClientRect(); return r.width > 0 && r.right > innerWidth + .5 && !e.closest('pre') && !e.closest('svg'); }).slice(0, 12).map((e) => key(e) + ' ' + Math.round(e.getBoundingClientRect().right)) };
  return out;
}

// One Chrome, one host page, every state in turn in an iframe; the results come back through --dump-dom.
function drive(browser, shells, states, themes) {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'dwight-ui-measure-'));
  try {
    const page = path.join(dir, 'host.html');
    const data = (o) => JSON.stringify(o).replace(/</g, '\\u003c');
    fs.writeFileSync(page, `<!doctype html><html><head><meta charset="utf-8"></head><body><pre id="out"></pre><script>
(async function(){
  var SHELLS=${data(shells)}, STATES=${data(states)}, THEMES=${data(themes)}, WALK=${data(walkPage.toString())}, res={}, errs=[];
  for (var i=0;i<STATES.length;i++){ for (var t=0;t<THEMES.length;t++){
    var st=STATES[i], theme=THEMES[t], f=document.createElement('iframe');
    f.style.cssText='border:0;position:absolute;left:0;top:0;width:'+st.w+'px;height:'+st.h+'px';
    await new Promise(function(r){ f.onload=r; f.srcdoc=SHELLS[st.file]; document.body.appendChild(f); });
    var w=f.contentWindow;
    try{
      await w.document.fonts.ready;
      w.chrome=w.chrome||{}; w.chrome.webview={postMessage:function(){}};
      st.go.forEach(function(g){ w.eval(g); });
      if (st.bodyClass) w.document.body.classList.add(st.bodyClass);
      if (w.dwightSetTheme) w.dwightSetTheme(theme); else w.document.body.classList.toggle('dark',theme==='dark');
      w.document.getAnimations().forEach(function(a){ try{a.finish();}catch(e){a.pause();} });
      void w.document.body.offsetWidth; await w.document.fonts.ready;
      var data=w.eval('('+WALK+')()');
      if (st.vars) { data.vars={}; st.vars.forEach(function(n){ data.vars[n]=w.getComputedStyle(w.document.body).getPropertyValue(n).trim(); }); }
      res[st.id+'|'+theme]=data;
    }catch(e){ errs.push(st.id+'|'+theme+': '+e); }
    f.remove();
  } }
  document.getElementById('out').textContent='${MARK}'+JSON.stringify({ua:navigator.userAgent,res:res,errs:errs})+'${MARK}';
})();
</script></body></html>`);
    const run = cp.spawnSync(browser, ['--headless=new', '--disable-gpu', '--no-first-run', '--no-default-browser-check', '--disable-extensions',
      '--user-data-dir=' + path.join(dir, 'profile'), '--virtual-time-budget=600000', '--dump-dom', url.pathToFileURL(page).href],
      { encoding: 'utf8', timeout: 480000, maxBuffer: 1024 * 1024 * 1024 });
    const hit = new RegExp(MARK + '([\\s\\S]*?)' + MARK).exec(run.stdout || '');
    if (!hit) throw new Error(`no measurement came back (exit ${run.status}${run.error ? ', ' + run.error.message : ''}; stderr: ${(run.stderr || '').trim().slice(0, 300) || 'empty'})`);
    return JSON.parse(hit[1].replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&amp;/g, '&'));
  } finally { try { fs.rmSync(dir, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 }); } catch (e) { /* a temp dir */ } }
}

const surfaceOf = (id) => id.split('-')[0].split('|')[0];

// A rule that matched something, won nothing, and lost to an id selector, across every state of a surface.
function idLosers(res) {
  const ag = new Map();
  for (const [id, v] of Object.entries(res)) for (const r of v.rules) {
    if (/#[a-zA-Z]/.test(r.sel)) continue;
    const k = surfaceOf(id) + ' | ' + r.sel + ' | ' + r.prop;
    const a = ag.get(k) || { won: 0, byId: 0 }; a.won += r.won; a.byId += r.byId; ag.set(k, a);
  }
  return [...ag.entries()].filter(([, a]) => a.won === 0 && a.byId > 0).map(([k]) => k).sort();
}

const PLANT_LOSES = '<!doctype html><html><head><style>#body p{font-size:13px}.planted{font-size:11px}</style></head><body><div id="body"><p class="planted">x</p><p style="color:#AAAAAA;background:#FFFFFF">too faint</p></div><div class="foot" style="display:flex;width:50px;overflow:hidden"><span style="flex:0 0 100px;height:4px"></span></div><div class="step" style="position:relative"><span style="position:absolute">words in the corner</span></div></body></html>';
const PLANT_FIXED = '<!doctype html><html><head><style>:where(#body) p{font-size:13px}.planted{font-size:11px}</style></head><body><div id="body"><p class="planted">x</p></div><div class="dd" id="plantdd" style="width:200px"><span class="dd-txt" style="display:block;width:200px;white-space:nowrap;overflow:hidden">x</span></div><script>document.getElementById("plantdd").dwightNames=["Fits"]<\/script><table class="usage" style="table-layout:fixed;width:200px"><tr><th>Fits</th></tr></table><div class="ev"><button class="ev-head" style="display:block;width:200px;white-space:nowrap"><span class="t">Fits</span></button></div><div class="ev undone"><button class="ev-head" style="display:block;background:#FFFFFF"><span class="t" style="color:#8D8D86">Dull</span></button></div><div class="dpane" style="height:200px"><div class="hconfirm" style="height:60px"><button class="btn">Go</button></div></div><div class="chipmenu" style="width:200px"><div class="grp" style="font:600 12px Arial;color:#000000;padding-left:2px">A heading</div><button class="it" style="font:400 12px Courier;color:#333333;padding-left:12px;border:0;background:none">Option</button></div><div class="keyrow" style="display:flex;width:300px"><span class="keydot" style="width:8px;height:8px;flex:none"></span><span class="st" style="flex:1;font:12px Arial;white-space:nowrap">Stored in one line</span></div><div class="titlebar" style="display:flex;width:200px"><span class="who">D</span><span class="act">Fits</span><button class="iconbtn" style="width:8px;height:8px"></button></div><div class="foot" style="display:flex;width:200px;overflow:hidden"><span style="flex:0 0 100px;height:4px"></span></div><div class="step" style="position:relative"><span style="position:absolute"></span></div></body></html>';
// A separator is a decoration and a checkbox has no text; everything else with words in it counts.
// THE ONE NAMED EXCEPTION. Undone rows above "You are here" go duller, and not struck through, because they can be redone: Tharun asked for about 3:1 at rest (Brief BQ section 9), and they return to full contrast on hover, focus, open and in a walk. It has its own floor, so it cannot drift lower unseen.
const DULL_FLOOR = 3;
const dullText = (v) => (v.text || []).filter((t) => t.dull);
const lowText = (v) => (v.text || []).filter((t) => t.cr < 4.5 && !t.dull && !/(^|\s|>)input[.\s]?/.test(t.k.split(' > ').pop()) && !/\.sep$/.test(t.k)).map((t) => `${t.k.split(' > ').pop()} ${t.size}px ${t.color} on ${t.bg} is ${t.cr}:1`);
const PLANT_SEG = '<div class="dd" id="plantdd" style="width:50px"><span class="dd-txt" style="display:block;width:50px;white-space:nowrap;overflow:hidden">x</span></div><script>document.getElementById("plantdd").dwightNames=["A label far too long"]<\/script><table class="usage" style="table-layout:fixed;width:40px"><tr><th>Overrunning</th></tr></table><div class="ev"><button class="ev-head" style="display:block;width:40px"><span class="t">A title far too long for one line</span></button></div><div class="ev undone"><button class="ev-head" style="display:block;background:#FFFFFF"><span class="t" style="color:#DDDDDD">Too dull</span></button></div><div class="dpane" style="height:60px;overflow:auto"><div class="hconfirm" style="height:200px"><button class="btn">Go</button></div></div><div class="chipmenu" style="width:200px"><div class="grp" style="cursor:pointer;color:#888888">Looks like a choice</div><button class="it">Option</button></div><div class="keyrow" style="display:flex;width:300px"><span class="keydot" style="width:8px;height:8px"></span><span class="st" style="display:grid;grid-template-columns:16px 1fr">Stored in Windows Credential Manager</span></div><div class="titlebar" style="display:flex;width:60px"><span class="who" style="margin-left:40px">D</span><span class="act" style="display:block;width:10px;overflow:hidden;white-space:nowrap">A long verb</span><button class="iconbtn" style="width:8px;height:8px"></button></div>';
const PLANT_STATES = [{ id: 'planted-loses', file: 'loses', w: 340, h: 200, go: [] }, { id: 'planted-fixed', file: 'fixed', w: 340, h: 200, go: [] }];

// The committed sheet where it exists. In the public tree, which does not carry it, the page render-ui-sheet.js generates from the
// tokens: the development tree's gate holds the committed file equal to that page, so both trees measure the same sheet.
function sheetText() {
  if (fs.existsSync(SHEET)) return { text: fs.readFileSync(SHEET, 'utf8'), from: 'the committed sheet' };
  return { text: require('./render-ui-sheet.js').page(), from: 'the sheet generated from the tokens (docs/dwight-ui-system-sheet.html is not in this tree)' };
}

function sheetHtml(text) {
  // The committed sheet links the CSS; measuring it inlines the same files.
  return text.replace(/<link rel="stylesheet" href="[^"]*dwight-fonts\.css">/, '')
    .replace(/<link rel="stylesheet" href="[^"]*dwight-tokens\.css">/, () => '<style>' + read('dwight-tokens.css') + '</style>')
    .replace(/<link rel="stylesheet" href="[^"]*dwight-components\.css">/, () => '<style>' + read('dwight-components.css') + '</style>');
}

// The distinct places "Dwight" and minimise stand across the open windows of one width, and the height of the bar: one of each, or the bars are not one component.
function barDrift(res) { const seen = new Set(); for (const v of Object.values(res)) if (v.bar && v.bar.w === 340) seen.add([v.bar.h, v.bar.who, v.bar.min].join('/')); return [...seen]; }

// One line at the window's real width; two at most, as a sentence in all the row's room, in a narrower one.
const keyLineBad = (k) => k.lines > (k.w >= 460 ? 1 : 2) || k.box < k.room - 1;

function runGate() {
  const say = (s) => console.log(s);
  const broken = (why) => { say('\nINSTRUMENT BROKEN - the measuring leg cannot judge the product: ' + why); process.exit(1); };
  say('=== UI system: measured in a browser');
  const browser = findChromium();
  if (!browser) broken('no Chromium found (looked for Chrome in Program Files, LocalAppData and /usr/bin; set DWIGHT_CHROMIUM).');

  // 1. The self-test: a rule beaten by an id must be found, and the same rule with the id made weightless must not.
  let planted;
  try { planted = drive(browser, { loses: PLANT_LOSES.replace('</body>', PLANT_SEG + '</body>'), fixed: PLANT_FIXED }, PLANT_STATES, ['light']); } catch (e) { broken(e.message); }
  const lp = idLosers({ 'planted-loses|light': planted.res['planted-loses|light'] }), lf = idLosers({ 'planted-fixed|light': planted.res['planted-fixed|light'] });
  if (lp.join() !== 'planted | .planted | font-size') broken(`the planted loser was not found (found ${JSON.stringify(lp)}).`);
  if (lf.length) broken(`the fixed control was reported as losing: ${JSON.stringify(lf)}.`);
  if (lowText(planted.res['planted-loses|light']).length < 1 || lowText(planted.res['planted-fixed|light']).length !== 0) broken('text planted at a low contrast was not told apart from text that clears it.');
  if (planted.res['planted-loses|light'].ddOverflow.length !== 1 || planted.res['planted-fixed|light'].ddOverflow.length !== 0) broken('the planted dropdown name that overflows was not told apart from the one that fits.');
  if (planted.res['planted-loses|light'].floatText.length !== 1 || planted.res['planted-fixed|light'].floatText.length !== 0) broken('the planted words floating over a card were not told apart from an empty corner.');
  if (planted.res['planted-loses|light'].footOver.length !== 1 || planted.res['planted-fixed|light'].footOver.length !== 0) broken('the planted footer that runs off its window was not told apart from the one that fits.');
  if (planted.res['planted-loses|light'].cellClip.length !== 1 || planted.res['planted-fixed|light'].cellClip.length !== 0) broken('the planted table cell that overflows was not told apart from the one that fits.');
  if (dullText(planted.res['planted-loses|light']).filter((t) => t.cr < DULL_FLOOR).length !== 1 || dullText(planted.res['planted-fixed|light']).filter((t) => t.cr < DULL_FLOOR).length !== 0) broken('a dull row below its own floor was not told apart from one at it.');
  { const l = planted.res['planted-loses|light'].bar, f = planted.res['planted-fixed|light'].bar; if (!l || !f || !l.clip || f.clip || l.who === f.who) broken('a planted title bar that moved or clipped a verb was not told apart from one that did not.'); }
  if (planted.res['planted-loses|light'].grpOver.length !== 1 || planted.res['planted-fixed|light'].grpOver.length !== 0) broken('a planted menu label that looks like a choice was not told apart from a real heading.');
  if (!planted.res['planted-loses|light'].wordColumn.some((t) => t.startsWith('Stored in Windows')) || planted.res['planted-fixed|light'].wordColumn.length !== 0) broken('a planted sentence stacked one word per line was not told apart from one on a line.');
  { const l = planted.res['planted-loses|light'].keyLine, f = planted.res['planted-fixed|light'].keyLine; if (!l || !f || !keyLineBad(l) || keyLineBad(f)) broken('a planted key status in a narrow box was not told apart from one on a line.'); }
  if (planted.res['planted-loses|light'].rowOver.length !== 1 || planted.res['planted-fixed|light'].rowOver.length !== 0) broken('the planted History row that wraps was not told apart from the one that fits.');
  if (planted.res['planted-loses|light'].confirmOver.length !== 1 || planted.res['planted-fixed|light'].confirmOver.length !== 0) broken('the planted question that fills its pane was not told apart from the one that leaves room.');
  const engine = (/(HeadlessChrome\/[\d.]+|Chrome\/[\d.]+)/.exec(planted.ua) || [planted.ua])[0];
  say(`  ${engine}. self-test: a planted .planted{font-size:11px} losing to #body p{font-size:13px} is found; the same rule with :where(#body) is clean; a dropdown name too long for its closed control is found, and one that fits is not; a table cell whose words overrun it is found, and one that fits is not; a History row that wraps and a question that fills its pane are found, and their controls are not; a sentence stacked one word per line is found, and one on a line is not; text planted under 4.5:1 is found.`);

  // 2. The product: every state, light. Losing is about the cascade, and the theme does not move it.
  const shells = {}; for (const f of new Set(STATES.map((s) => s.file))) shells[f] = inlineShell(f, false);
  let run; try { run = drive(browser, shells, STATES, ['light']); } catch (e) { broken(e.message); }
  if (run.errs.length) broken('a state threw: ' + run.errs[0]);
  if (Object.keys(run.res).length !== STATES.length) broken(`only ${Object.keys(run.res).length} of ${STATES.length} states came back.`);
  const rulesSeen = Object.values(run.res).reduce((n, v) => n + v.rules.length, 0);
  if (rulesSeen < 1000) broken(`only ${rulesSeen} rule properties were measured; the walk no longer matches the shells.`);
  const losers = idLosers(run.res);
  let failures = 0;
  say(`  ${STATES.length} states, ${rulesSeen} rule properties measured; ${losers.length} lose every element they match to an id selector.`);
  for (const l of losers) { failures++; say('  FAIL  ' + l + '  loses on every element it matches to an id selector. The rule does nothing; the screen ignores it.'); }

  // Painted contrast, in both themes: the token floors cannot see a role on a tint.
  {
    let both; try { both = drive(browser, shells, STATES, ['light', 'dark']); } catch (e) { broken(e.message); }
    const seenLow = new Set();
    for (const [id, v] of Object.entries(both.res)) for (const l of lowText(v)) { const k = id.split('|')[1] + ' ' + l; if (seenLow.has(k)) continue; seenLow.add(k); failures++; say(`  FAIL  ${id}: ${l}, under the 4.5:1 floor.`); }
    if (!seenLow.size) say(`   OK   every sentence and fact painted on a surface clears 4.5:1 in both themes, ${STATES.length} states.`);
    let grpSeen = 0;
    for (const [id, v] of Object.entries(both.res)) { if (id.startsWith('panel-chat-menu')) grpSeen++; for (const t of new Set(v.grpOver || [])) { failures++; say(`  FAIL  ${id}: the menu's group label "${t}" can be taken for a choice.`); } }
    if (grpSeen < 2) { failures++; say('  FAIL  the open picker was not measured in both themes.'); }
    else say('   OK   the picker\'s group labels read as headings over their options, in both themes.');
    // The named exception, with its ratio stated on every run.
    const dull = { light: [], dark: [] };
    for (const [id, v] of Object.entries(both.res)) for (const t of dullText(v)) { dull[id.split('|')[1]].push(t.cr); if (t.cr < DULL_FLOOR) { failures++; say(`  FAIL  ${id}: the dull row "${t.sample}" is ${t.cr}:1, under its own floor of ${DULL_FLOOR}:1.`); } }
    if (!dull.light.length || !dull.dark.length) { failures++; say('  FAIL  no state reached a dull row, so the named exception was not measured.'); }
    else say(`   OK   the one named exception, undone rows at rest: ${Math.min(...dull.light)}:1 in light and ${Math.min(...dull.dark)}:1 in dark, on a floor of ${DULL_FLOOR}:1 (Tharun, Brief BQ section 9).`);
  }

  // 3. The system layer, on the sheet: every rule wins, and every rule is shown.
  if (!fs.existsSync(SHEET) && require('./render-ui-sheet.js').DEV_TREE) broken('docs/dwight-ui-system-sheet.html does not exist; node tools/render-ui-sheet.js writes it.');
  const source = sheetText();
  say('   the sheet measured: ' + source.from);
  let sheet; try { sheet = drive(browser, { sheet: sheetHtml(source.text) }, [{ id: 'sheet', file: 'sheet', w: 900, h: 2400, go: [], vars: ['--subtle', '--accent-inverse', '--accent', '--link'] }], ['light', 'dark']); } catch (e) { broken(e.message); }
  if (sheet.errs.length) broken('the sheet threw: ' + sheet.errs[0]);
  const light = sheet.res['sheet|light'], dark = sheet.res['sheet|dark'];
  const css = read('dwight-components.css');
  if (!light.sys || light.sys.length < 30) broken(`only ${light.sys ? light.sys.length : 0} system rules were read off the sheet.`);
  // Seen is across the sheet and every surface: a rule nothing reaches has not been looked at by anybody.
  const surfaces = Object.values(run.res);
  const pages = [light, ...surfaces];
  const seen = new Map(), hits = new Map();
  for (const page of pages) {
    for (const h of page.sys || []) hits.set(h.sel, Math.max(hits.get(h.sel) || 0, h.hit));
    for (const r of page.rules) if (r.lib) { const k = r.sel + ' | ' + r.prop; const a = seen.get(k) || { sel: r.sel, prop: r.prop, decl: r.decl, matched: 0, won: 0, shown: '' }; a.matched += r.matched; a.won += r.won; if (r.seen) a.shown = r.seen; seen.set(k, a); }
  }
  // Whether words fit is a question about the real faces and a real scrollbar, so these two are read on a run that has both, and it says so if it does not.
  {
    const faced = {}; for (const f of new Set(STATES.map((s) => s.file))) faced[f] = inlineShell(f, true);
    let fit; try { fit = drive(browser, faced, STATES, ['light']); } catch (e) { broken(e.message); }
    if (fit.errs.length) broken('a state threw on the run with the real fonts: ' + fit.errs[0]);
    for (const [id, v] of Object.entries(fit.res)) {
      if (v.scrollbarW < 1) broken(`${id}: a scrollbar takes no width here (${v.scrollbarW}px), so a fit measured here is optimistic.`);
      if (v.fitItems && !v.fontsLoaded.length) broken(`${id}: no brand font loaded, so a fit measured here is measured in the wrong face.`);
      for (const t of new Set(v.ddOverflow || [])) { failures++; say(`  FAIL  ${id}: the dropdown option "${t}" does not fit its closed control on one line.`); }
      for (const t of v.copyOver || []) { failures++; say(`  FAIL  ${id}: the words "${t}" do not fit the room under the tour's picture.`); }
      for (const t of v.menuOver || []) { failures++; say(`  FAIL  ${id}: the menu "${t}" does not show whole in its window, or a name in it wraps.`); }
      if (v.bar && v.bar.clip) { failures++; say(`  FAIL  ${id}: the title bar clips the verb's name "${v.bar.clip}".`); }
      for (const t of new Set(v.grpOver || [])) { failures++; say(`  FAIL  ${id}: the menu's group label "${t}" can be taken for a choice.`); }
      for (const t of new Set(v.wordColumn || [])) { failures++; say(`  FAIL  ${id}: "${t}" is stacked one word per line.`); }
      if (v.keyLine && keyLineBad(v.keyLine)) { failures++; say(`  FAIL  ${id}: the key's status "${v.keyLine.text}" takes ${v.keyLine.lines} lines in a ${v.keyLine.box}px box, with ${v.keyLine.room}px of room.`); }
      for (const t of new Set(v.rowOver || [])) { failures++; say(`  FAIL  ${id}: the History row "${t}" is more than one line at rest.`); }
      for (const t of new Set(v.confirmOver || [])) { failures++; say(`  FAIL  ${id}: the question "${t}" leaves its buttons out of view, or no room for a row.`); }
      for (const t of new Set(v.cellClip || [])) { failures++; say(`  FAIL  ${id}: the words "${t}" run past their cell, button or tag.`); }
      for (const t of new Set(v.floatText || [])) { failures++; say(`  FAIL  ${id}: "${t}" floats over a card.`); }
      for (const t of v.footOver || []) { failures++; say(`  FAIL  ${id}: the footer runs ${t}px past its window, so a control is out of reach.`); }
    }
    const keys = Object.entries(fit.res).filter(([id, v]) => /^settings-keyline-/.test(id) && v.keyLine);
    if (keys.length < 7) { failures++; say(`  FAIL  only ${keys.length} of the 7 key-status states were measured.`); }
    else say(`   OK   no sentence on any surface is stacked one word per line; the key's status: ${keys.map(([id, v]) => id.replace('settings-keyline-', '').replace(/\|.*/, '') + ' ' + v.keyLine.lines).join(', ')} line(s).`);
    const drift = barDrift(fit.res);
    if (drift.length !== 1) { failures++; say(`  FAIL  the title bar is not one design: height, "Dwight" and minimise stand at ${drift.join(' and ')} (height/x/x) across the open windows.`); }
    else say(`   OK   one title bar: height, "Dwight" and minimise at ${drift[0]} in every open window, and no verb's name clips.`);
    say(`   OK   fit is measured in the real fonts with a real scrollbar: ${Object.values(fit.res)[0].scrollbarW}px, ${Object.keys(fit.res).length} states.`);
  }
  for (const a of seen.values()) if (a.matched && a.won === 0) { failures++; say(`  FAIL  ${a.sel} { ${a.prop}: ${a.decl} } matches ${a.matched} element(s) and wins on none (the page shows ${a.shown}).`); }
  const unshown = [...hits.entries()].filter(([, hit]) => hit < 1);
  for (const [sel] of unshown) { failures++; say(`  FAIL  ${sel}  is in the library and nothing on the sheet or a surface reaches it.`); }
  say(`  the library: ${hits.size} rules, ${unshown.length ? unshown.length + ' unreached' : 'all reached'} across the sheet and ${surfaces.length} state(s); ${seen.size} declared values measured, none losing on every element.`);

  // 4. The ruled values.
  const want = [['light', light, '--subtle', '#6E6E68'], ['dark', dark, '--subtle', '#8F8D86']];
  for (const [t, d, n, v] of want) if ((d.vars[n] || '').toUpperCase() !== v) { failures++; say(`  FAIL  in ${t}, ${n} computes to ${d.vars[n]}, the system says ${v}.`); }
  // R1: the fill and its text are per-theme. Tharun chose these by eye, so a change to them is a decision and not a side effect.
  for (const [t, d, fill, text] of [['light', light, '#AD6426', '#FFFFFF'], ['dark', dark, '#E09458', '#111110']]) {
    if ((d.vars['--accent'] || '').toUpperCase() !== fill) { failures++; say(`  FAIL  in ${t}, --accent computes to ${d.vars['--accent']}, Tharun's ruling (R1) is ${fill}.`); }
    if ((d.vars['--accent-inverse'] || '').toUpperCase() !== text) { failures++; say(`  FAIL  in ${t}, --accent-inverse computes to ${d.vars['--accent-inverse']}, Tharun's ruling (R1) is ${text}.`); }
  }
  if (!failures) say('   OK   the ruled values compute in both themes; nothing loses to an id.');
  say(''); return failures;
}

// ---- the audit's table, measured the same way, for before and after -----------------------------------------------------
function runCounts() {
  const browser = findChromium(); if (!browser) { console.error('no Chromium'); process.exit(1); }
  const shells = {}; for (const f of new Set(STATES.map((s) => s.file))) shells[f] = inlineShell(f, true);
  const run = drive(browser, shells, STATES, ['light', 'dark']);
  if (run.errs.length) { console.error(run.errs.join('\n')); process.exit(1); }
  const a = {}; const KEYS = ['size', 'family', 'weight', 'lineHeight', 'style', 'colourLight', 'colourDark', 'padding', 'margin', 'gap', 'shadow', 'controlHeight', 'iconBox'];
  const add = (s, k, v) => { a[s] = a[s] || {}; (a[s][k] = a[s][k] || new Set()).add(v); a.all = a.all || {}; (a.all[k] = a.all[k] || new Set()).add(v); };
  let low = 0; const lows = new Set();
  for (const [id, v] of Object.entries(run.res)) {
    const s = surfaceOf(id), dark = id.endsWith('|dark');
    for (const t of v.text) {
      if (/\.comp|\.minibar|\.minicorner/.test(t.k)) continue;
      add(s, 'size', t.size); add(s, 'family', t.fam); add(s, 'weight', t.weight); add(s, 'lineHeight', t.lh); add(s, 'style', t.fam + ' ' + t.size + ' ' + t.weight); add(s, dark ? 'colourDark' : 'colourLight', t.color);
      if (t.cr < 4.5 && !/input\./.test(t.k) && !t.k.endsWith('span.arr')) lows.add((dark ? 'dark' : 'light') + t.k.split(' > ').pop() + t.color + t.bg);
    }
    for (const b of v.box) { add(s, 'padding', b.pad); add(s, 'margin', b.mar); if (b.gap) add(s, 'gap', b.gap); if (b.shadow !== 'none') add(s, 'shadow', b.shadow); }
    for (const c of v.controls) if (c.type !== 'checkbox') add(s, 'controlHeight', c.h);
    for (const i of v.icons) if (!/\bw\b/.test(i.cls)) add(s, 'iconBox', i.w + 'x' + i.h);
  }
  const S = ['panel', 'dock', 'settings', 'feedback', 'note', 'tour', 'all'];
  console.log(['computed', ...S].join(' | '));
  for (const k of KEYS) console.log([k, ...S.map((s) => (a[s] && a[s][k] ? a[s][k].size : 0))].join(' | '));
  console.log('text pairs under 4.5:1 | ' + lows.size);
}

if (process.argv.includes('--counts')) runCounts();
else process.exit(runGate() ? 1 : 0);
