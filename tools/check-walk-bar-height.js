#!/usr/bin/env node
/*
 * The Focus bar asks for the height it needs (Brief FN). Run by tools/check-shell-render.js, and by itself:
 *   node tools/check-walk-bar-height.js
 *
 * WHAT IT HOLDS. walk-bar.html sends 'h:N' and WalkBar takes N as the bar's height, held between WalkGeometry.BarMinH and BarMaxH. So N must be
 * what the words need: a sentence that fits in the tallest bar shows whole at the height it asked for, with no fade over its last line, and one
 * that does not fit fades at the tallest bar, because words remain below the edge. Brief FL's camera found the opposite: the page read a Range's
 * rectangle, which hugs the glyphs and is short by each line's leading, so a two-line step asked 93 to 94 px, needed about 97, and its second line
 * sat under the fade.
 *
 * HOW. Headless Chrome, as tools/ui-measure.js drives it (--dump-dom, no puppeteer, so CI runs it), once at each scale Windows is set to here,
 * 1, 1.25 and 1.5. Each case is the shell in an iframe at the bar's width, behaving as the host does: the iframe is given the height the page
 * asked for, held within the limits, and the page is told it was resized, as WebView2 tells it, until the answer stops changing. Headless Chrome
 * under a virtual clock does not fire an iframe's resize by itself, so the check sends it: the page's own listener does the rest.
 *
 * A POSITIVE CONTROL. The same cases run against the shell with the old measure planted back (a Range over the words, read for its rectangle),
 * and the two-line case must fail there. A check that cannot see the defect it was written for proves nothing by passing.
 *
 * A LIMIT: this is Blink, which WebView2 wraps, not WebView2 at Windows DPI. It reports layout arithmetic, which is what the ask is.
 */
const fs = require('fs');
const os = require('os');
const path = require('path');
const cp = require('child_process');
const url = require('url');
const { findChromium, inlineShell } = require('./ui-browser.js');

const ROOT = path.resolve(__dirname, '..');
const MARK = '@@DWIGHT_WALK_BAR@@';
const MIN_H = 78, MAX_H = 148;   // WalkGeometry.BarMinH, BarMaxH (Dwight/UI/WalkCore.cs); held equal below
const SCALES = [1, 1.25, 1.5];

// The lamp's own steps (Tharun's 6 November recording), through the panel's Markdown, at widths the bar takes.
const STEP = (html, extra) => JSON.stringify(Object.assign({ label: 'Step 2 of 7', html, i: 1, n: 7, focus: true, dim: true, everywire: false }, extra || {}));
const TWO = '<code>Range</code> (20) uses <code>Panel</code> (12)\'s domain and the <code>List Length</code> (14) count as steps, producing <strong>21 values</strong> (shown in the <code>Panel</code> (25)) that act as the per-layer heights.';
const LONG = 'The <code>Linear Array</code> (36), driven by <code>Panel</code> (16) via <code>Unit Z</code> (29) and the <code>Number Slider</code> (24) count, copies the plane upward; via <code>Relay</code> (43) these feed <code>Brep | Plane</code> (46), which <strong>slices</strong> the Brep into curves (1000 items in a depth-2 tree).';
const ONE = '<code>Loft</code> (44) lofts the moved rectangles.';
const TIME = 'Slowest here: XY Plane (27), 5 ms of the selection\'s 204 ms. Read at 21:59, after the last solve.';
const CASES = [
  { id: 'one line', w: 880, json: STEP(ONE), fits: true },
  { id: 'two lines', w: 880, json: STEP(TWO), fits: true, control: true },
  { id: 'two lines and the time', w: 880, json: STEP(TWO, { playing: true, time: TIME }), fits: true },
  { id: 'two lines, components gone', w: 880, json: STEP(TWO, { gone: '2 of its 5 components are no longer on the canvas.' }), fits: true },
  { id: 'three lines', w: 880, json: STEP(LONG + ' ' + ONE), fits: true },
  // Narrower, the controls wrap onto a second row, so the same words no longer fit in the tallest bar and must fade there.
  { id: 'too long at 640, the controls on two rows', w: 640, json: STEP(LONG), fits: false },
  { id: 'too long for the tallest bar', w: 420, json: STEP(TWO + ' ' + LONG), fits: false },
];

// What the host page does, as text it runs. The cases and the shells travel percent-encoded, so nothing in them can close the script.
function pageScript(cases, shells, minH, maxH) {
  const carry = (o) => 'JSON.parse(decodeURIComponent("' + encodeURIComponent(JSON.stringify(o)) + '"))';
  return `(async function(){
  var CASES=${carry(cases)}, SHELLS=${carry(shells)}, out={dpr:window.devicePixelRatio,res:{},errs:[]};
  var wait=function(ms){ return new Promise(function(r){ setTimeout(r,ms); }); };
  for (var k in SHELLS) { for (var i=0;i<CASES.length;i++){
    var c=CASES[i], f=document.createElement('iframe'), sent=[];
    f.style.cssText='border:0;position:absolute;left:0;top:0;width:'+c.w+'px;height:${minH}px';
    await new Promise(function(r){ f.onload=r; f.srcdoc=SHELLS[k]; document.body.appendChild(f); });
    var w=f.contentWindow;
    try{
      await w.document.fonts.ready;
      w.chrome={webview:{postMessage:function(m){ sent.push(String(m)); }}};
      w.dwightWalkStep(c.json);
      var h=${minH}, asked=null, rounds=0;
      for (rounds=0;rounds<8;rounds++){
        var last=sent.filter(function(m){ return m.indexOf('h:')===0; }).pop();
        asked=last?+last.slice(2):null;
        var want=Math.max(${minH},Math.min(${maxH},asked==null?${minH}:asked));
        if (want===h && rounds>0) break;
        h=want; f.style.height=h+'px'; await wait(30); w.dispatchEvent(new w.Event('resize')); await wait(30);
      }
      var d=w.document, s=d.getElementById('walksent'), line=s.parentNode, words=d.getElementById('walkwords');
      var sr=s.getBoundingClientRect(), wr=words.getBoundingClientRect();
      out.res[k+'|'+c.id]={ asked:asked, h:h, rounds:rounds, more:line.classList.contains('more'), over:s.scrollHeight-s.clientHeight,
        cut:+(wr.bottom-sr.bottom).toFixed(2), words:+wr.height.toFixed(2), box:+sr.height.toFixed(2) };
    }catch(e){ out.errs.push(k+'|'+c.id+': '+e); }
    f.remove();
  } }
  document.getElementById('out').textContent='${MARK}'+JSON.stringify(out)+'${MARK}';
})();`;
}

function drive(browser, scale, shells) {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'dwight-walk-bar-'));
  try {
    const page = path.join(dir, 'host.html');
    fs.writeFileSync(page, '<!doctype html><html><head><meta charset="utf-8"></head><body><pre id="out"></pre><script>' + pageScript(CASES, shells, MIN_H, MAX_H) + '</script></body></html>');
    const run = cp.spawnSync(browser, ['--headless=new', '--disable-gpu', '--no-first-run', '--no-default-browser-check', '--disable-extensions', '--force-device-scale-factor=' + scale,
      '--user-data-dir=' + path.join(dir, 'profile'), '--virtual-time-budget=120000', '--dump-dom', url.pathToFileURL(page).href],
      { encoding: 'utf8', timeout: 240000, maxBuffer: 64 * 1024 * 1024 });
    const hit = new RegExp(MARK + '([\\s\\S]*?)' + MARK).exec(run.stdout || '');
    if (!hit) throw new Error(`no measurement came back (exit ${run.status}${run.error ? ', ' + run.error.message : ''}; stderr: ${(run.stderr || '').trim().slice(0, 300) || 'empty'})`);
    return JSON.parse(hit[1].replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&amp;/g, '&'));
  } finally { try { fs.rmSync(dir, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 }); } catch (e) { /* a temp dir */ } }
}

// The judgement on one case. A sentence that fits shows whole: nothing below the box's edge and no fade, and the bar no taller than its words
// need by more than the pixel the device rounds to. One that does not fit is at the tallest bar with the fade, because words remain.
function judge(c, r) {
  const bad = [];
  if (r.asked == null) return ['the page asked for no height'];
  if (c.fits) {
    if (r.asked > MAX_H) bad.push(`asked ${r.asked}, past the tallest bar, for a sentence chosen to fit`);
    if (r.more) bad.push(`the fade is over the words at the height it asked for (${r.h})`);
    if (r.over > 1 || r.cut > 0.5) bad.push(`the words run ${Math.max(r.over, r.cut)} px below the bar's edge at ${r.h}`);
    if (r.box - r.words > 1.5 && r.h > MIN_H) bad.push(`the bar is ${(r.box - r.words).toFixed(2)} px taller than its words`);
  } else {
    if (r.h !== MAX_H) bad.push(`a sentence longer than the tallest bar got ${r.h}, not ${MAX_H}`);
    if (!r.more) bad.push('words remain below the edge and no fade says so');
  }
  return bad;
}

function run() {
  const say = (s) => console.log(s);
  say('=== The Focus bar asks for the height its words need (Brief FN)');
  // The limits the check uses are the product's.
  const core = fs.readFileSync(path.join(ROOT, 'Dwight', 'UI', 'WalkCore.cs'), 'utf8');
  const lim = /public const int BarMinH = (\d+), BarMaxH = (\d+)/.exec(core);
  if (!lim || +lim[1] !== MIN_H || +lim[2] !== MAX_H) { say('  FAIL  WalkGeometry.BarMinH and BarMaxH are not ' + MIN_H + ' and ' + MAX_H + '; change this check with them'); return 1; }
  const browser = findChromium();
  if (!browser) { say('\nINSTRUMENT BROKEN - no Chromium (set DWIGHT_CHROMIUM)'); return 1; }
  const shell = inlineShell(ROOT, 'walk-bar.html');
  // The defect, planted back: the ask from a Range over the words, read for its rectangle, as walk-bar.html read it before Brief FN.
  const OLD = "function _walkNeed(){ var b=document.getElementById('walkbar'), s=document.getElementById('walksent'), r=document.createRange(); " +
    "r.selectNodeContents(document.getElementById('walkwords')); return b.offsetHeight-s.clientHeight+Math.ceil(r.getBoundingClientRect().height); }";
  const planted = shell.replace(/function _walkNeed\(\)\{[^\r\n]*\}(?=\r?\n)/, OLD);
  if (planted === shell) { say('\nINSTRUMENT BROKEN - the planted control could not find _walkNeed in walk-bar.html'); return 1; }
  let failures = 0;
  for (const scale of SCALES) {
    let out;
    try { out = drive(browser, scale, { product: shell, planted }); } catch (e) { say('\nINSTRUMENT BROKEN - ' + e.message); return 1; }
    if (out.errs.length) { say('\nINSTRUMENT BROKEN - ' + out.errs.join('; ')); return 1; }
    if (Math.abs(out.dpr - scale) > 0.001) { say(`\nINSTRUMENT BROKEN - asked for scale ${scale}, the page ran at ${out.dpr}`); return 1; }
    for (const c of CASES) {
      const r = out.res['product|' + c.id], bad = judge(c, r);
      say(`  ${bad.length ? 'FAIL' : ' OK '}  scale ${scale}, ${c.id} (${c.w} wide): asked ${r.asked}, bar ${r.h}, words ${r.words} in ${r.box}${r.more ? ', faded' : ''}${bad.length ? ' - ' + bad.join('; ') : ''}`);
      if (bad.length) failures++;
    }
    const ctl = CASES.find((c) => c.control), cr = out.res['planted|' + ctl.id];
    const seen = judge(ctl, cr).length > 0;
    say(`  ${seen ? ' OK ' : 'FAIL'}  scale ${scale}, the planted Range measure is caught on ${ctl.id}: asked ${cr.asked}, words ${cr.words} in ${cr.box}${cr.more ? ', faded' : ''}`);
    if (!seen) { say('\nINSTRUMENT BROKEN - the old measure passed, so this check cannot see the defect it holds'); return 1; }
  }
  say(failures ? `\n${failures} case(s) failed.` : '\nEvery case asked for what its words need, at every scale, and the old measure is caught at every scale.');
  return failures ? 1 : 0;
}

if (require.main === module) process.exit(run());
module.exports = { run };
