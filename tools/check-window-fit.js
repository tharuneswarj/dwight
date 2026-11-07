#!/usr/bin/env node
/*
 * Two fixed windows show all they say as they open (Brief FN). Run by tools/check-shell-render.js, and by itself:
 *   node tools/check-window-fit.js
 *
 * WHAT IT HOLDS. The note editor and Report a problem cannot be resized, and each ends on a sentence the person should read before acting: what
 * becomes of the note, and that Dwight sends nothing itself. Brief FL's camera found both half under the footer as the window opened (the note
 * editor at 380 x 360, Report a problem at 460 x 540). So at the size the window has in the product, read from its ClientSize in Dwight/UI, every
 * state the host opens it in must fit with nothing to scroll, at 1, 1.25 and 1.5.
 *
 * HOW. Headless Chrome, as tools/ui-measure.js drives it (--dump-dom, no puppeteer, so CI runs it), once at each scale; each state is the shell in
 * an iframe at the window's size, given the JSON the C# sends.
 *
 * A POSITIVE CONTROL. The same states at the old sizes must fail. A check that cannot see the defect it was written for proves nothing by passing.
 *
 * A LIMIT: this is Blink, which WebView2 wraps, not WebView2 at Windows DPI. It reports layout arithmetic.
 */
const fs = require('fs');
const os = require('os');
const path = require('path');
const cp = require('child_process');
const url = require('url');
const { findChromium, inlineShell } = require('./ui-browser.js');

const ROOT = path.resolve(__dirname, '..');
const MARK = '@@DWIGHT_WINDOW_FIT@@';
const SCALES = [1, 1.25, 1.5];
const OLD = { 'note.html': [380, 360], 'feedback.html': [460, 540] };   // the sizes Brief FL photographed
const WINDOWS = { 'note.html': path.join('Dwight', 'UI', 'DwightNotePanel.cs'), 'feedback.html': path.join('Dwight', 'UI', 'DwightFeedbackPanel.cs') };

// The plugin's own sentences (DwightNotePanel.About, ProblemReport.DiagnosticBlock's shape), and a note as the 4 November take wrote one.
const J = (o) => JSON.stringify(o);
const ABOUT_ON = 'This note is about the selected component. It stays until you delete it.';
const ABOUT_AT = 'This note sits at a point on the canvas. It stays until you delete it.';
const ABOUT_KEPT = "A deleted note is gone: Dwight does not keep it. A note is kept in Dwight's own record for this file, never in the definition.";
const WRITTEN = '**Range** (20) sets the 21 layer heights.\n- Loft (44) needs the rectangles in order\n\n| Input | Sets |\n| --- | --- |\n| Panel (12) | the height domain |\n| List Length (14) | the layer count |';
const BLOCK = (errs) => 'Diagnostics (computed by Dwight; no definition, names, values, geometry, file paths, prompts, replies or keys):\n- Dwight: 1.0.1+3af60fd · Release 28BD25D622F11B7B\n' +
  '- Rhino: 8.36.26293.11001\n- Grasshopper: 8.36.26293.11001\n- OS: Microsoft Windows NT 10.0.26300.0\n- Provider: Anthropic\n- Last verb or chat step: Build\n' + (errs ? '- Last errors:\n  - one\n  - two\n  - three' : '- Last errors: none');
const STATES = [
  { id: 'a new note on a component', file: 'note.html', go: [['dwightNoteInit', J({ head: 'New note', text: '', about: ABOUT_ON, canDelete: false, canAsk: true, applied: 'light' })]] },
  { id: 'a new note being written', file: 'note.html', go: [['dwightNoteInit', J({ head: 'New note', text: WRITTEN, about: ABOUT_ON, canDelete: false, canAsk: true, applied: 'light' })]] },
  { id: 'a new note at a point', file: 'note.html', go: [['dwightNoteInit', J({ head: 'New note', text: '', about: ABOUT_AT, canDelete: false, canAsk: true, applied: 'light' })]] },
  { id: 'your note, edited', file: 'note.html', go: [['dwightNoteInit', J({ head: 'Your note', text: 'The one point the client can move. Keep it under 120 or the solve slows.', about: ABOUT_KEPT, canDelete: true, canAsk: true, applied: 'light' })], ['noteEditing', true]] },
  { id: 'your note, read, pinned', file: 'note.html', go: [['dwightNoteInit', J({ head: 'Your note', text: 'x', html: '<p>The span the client can move.</p>', about: ABOUT_KEPT, canDelete: true, canAsk: true, applied: 'light', pin: { on: true, label: 'Pinned to Remap Numbers' } })]] },
  { id: 'Dwight\'s note, read, changed since, free', file: 'note.html', go: [['dwightNoteInit', J({ head: 'A note Dwight wrote', text: 'x', html: '<p>Dwight built Spot 6: 9 components, with errors.</p>', about: ABOUT_KEPT, stale: 'Written before this component changed.', canDelete: true, canAsk: true, applied: 'light', pin: { on: false, label: 'Free' } })]] },
  { id: 'Report a problem as it opens', file: 'feedback.html', go: [['dwightFeedbackInit', J({ block: BLOCK(false), canIssue: true, canMail: true, applied: 'light' })]] },
  { id: 'Report a problem with three errors', file: 'feedback.html', go: [['dwightFeedbackInit', J({ block: BLOCK(true), canIssue: true, canMail: true, applied: 'light' })]] },
];

function pageScript(runs, shells) {
  const carry = (o) => 'JSON.parse(decodeURIComponent("' + encodeURIComponent(JSON.stringify(o)) + '"))';
  return `(async function(){
  var RUNS=${carry(runs)}, SHELLS=${carry(shells)}, out={dpr:window.devicePixelRatio,res:{},errs:[]};
  for (var i=0;i<RUNS.length;i++){
    var r=RUNS[i], f=document.createElement('iframe');
    f.style.cssText='border:0;position:absolute;left:0;top:0;width:'+r.w+'px;height:'+r.h+'px';
    await new Promise(function(ok){ f.onload=ok; f.srcdoc=SHELLS[r.file]; document.body.appendChild(f); });
    var w=f.contentWindow;
    try{
      await w.document.fonts.ready;
      w.chrome={webview:{postMessage:function(){}}};
      r.go.forEach(function(g){ w[g[0]].apply(w, g.slice(1)); });
      var form=w.document.querySelector('.form'), fr=form.getBoundingClientRect();
      var shown=[].filter.call(form.children,function(e){ return e.offsetParent!==null; }), last=shown[shown.length-1], lr=last.getBoundingClientRect();
      out.res[r.key]={ over:form.scrollHeight-form.clientHeight, below:+(lr.bottom-fr.bottom).toFixed(2), last:(last.id||last.className) };
    }catch(e){ out.errs.push(r.key+': '+e); }
    f.remove();
  }
  document.getElementById('out').textContent='${MARK}'+JSON.stringify(out)+'${MARK}';
})();`;
}

function drive(browser, scale, runs, shells) {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'dwight-window-fit-'));
  try {
    const page = path.join(dir, 'host.html');
    fs.writeFileSync(page, '<!doctype html><html><head><meta charset="utf-8"></head><body><pre id="out"></pre><script>' + pageScript(runs, shells) + '</script></body></html>');
    const run = cp.spawnSync(browser, ['--headless=new', '--disable-gpu', '--no-first-run', '--no-default-browser-check', '--disable-extensions', '--force-device-scale-factor=' + scale,
      '--user-data-dir=' + path.join(dir, 'profile'), '--virtual-time-budget=60000', '--dump-dom', url.pathToFileURL(page).href],
      { encoding: 'utf8', timeout: 240000, maxBuffer: 64 * 1024 * 1024 });
    const hit = new RegExp(MARK + '([\\s\\S]*?)' + MARK).exec(run.stdout || '');
    if (!hit) throw new Error(`no measurement came back (exit ${run.status}${run.error ? ', ' + run.error.message : ''}; stderr: ${(run.stderr || '').trim().slice(0, 300) || 'empty'})`);
    return JSON.parse(hit[1].replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&amp;/g, '&'));
  } finally { try { fs.rmSync(dir, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 }); } catch (e) { /* a temp dir */ } }
}

function run() {
  const say = (s) => console.log(s);
  say('=== The note editor and Report a problem show all they say as they open (Brief FN)');
  const size = {};
  for (const [shell, cs] of Object.entries(WINDOWS)) {
    const m = /ClientSize = new Size\((\d+), (\d+)\)/.exec(fs.readFileSync(path.join(ROOT, cs), 'utf8'));
    if (!m) { say('\nINSTRUMENT BROKEN - no ClientSize in ' + cs); return 1; }
    size[shell] = [+m[1], +m[2]];
  }
  const browser = findChromium();
  if (!browser) { say('\nINSTRUMENT BROKEN - no Chromium (set DWIGHT_CHROMIUM)'); return 1; }
  const shells = { 'note.html': inlineShell(ROOT, 'note.html'), 'feedback.html': inlineShell(ROOT, 'feedback.html') };
  const runs = [];
  for (const s of STATES) {
    runs.push({ key: 'now|' + s.id, file: s.file, w: size[s.file][0], h: size[s.file][1], go: s.go });
    runs.push({ key: 'old|' + s.id, file: s.file, w: OLD[s.file][0], h: OLD[s.file][1], go: s.go });
  }
  let failures = 0;
  for (const scale of SCALES) {
    let out;
    try { out = drive(browser, scale, runs, shells); } catch (e) { say('\nINSTRUMENT BROKEN - ' + e.message); return 1; }
    if (out.errs.length) { say('\nINSTRUMENT BROKEN - ' + out.errs.join('; ')); return 1; }
    if (Math.abs(out.dpr - scale) > 0.001) { say(`\nINSTRUMENT BROKEN - asked for scale ${scale}, the page ran at ${out.dpr}`); return 1; }
    const caught = {};
    for (const s of STATES) {
      const r = out.res['now|' + s.id], ok = r.over <= 0 && r.below <= 0;
      say(`  ${ok ? ' OK ' : 'FAIL'}  scale ${scale}, ${s.id} at ${size[s.file].join(' x ')}: ${ok ? 'nothing to scroll, the last line clear by ' + (-r.below) + ' px' : r.over + ' px to scroll, the last line ' + r.below + ' px under the edge (' + r.last + ')'}`);
      if (!ok) failures++;
      const o = out.res['old|' + s.id];
      if (o.over > 0 && o.below > 0) caught[s.file] = true;
    }
    for (const shell of Object.keys(OLD)) {
      say(`  ${caught[shell] ? ' OK ' : 'FAIL'}  scale ${scale}, ${shell} at the old ${OLD[shell].join(' x ')} is caught`);
      if (!caught[shell]) { say('\nINSTRUMENT BROKEN - the old size passed, so this check cannot see the defect it holds'); return 1; }
    }
  }
  say(failures ? `\n${failures} state(s) failed.` : '\nEvery state fits at every scale, and the old sizes are caught at every scale.');
  return failures ? 1 : 0;
}

if (require.main === module) process.exit(run());
module.exports = { run };
