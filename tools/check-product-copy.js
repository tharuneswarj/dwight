#!/usr/bin/env node
/*
 * Product copy gate (#883): no user-visible string promises a future of the product.
 *
 * WHY THIS EXISTS. A pass over the product's own words found strings telling users what would arrive "in a
 * later phase" - one pointing at a segmenter that had shipped phases earlier - a Settings hint every user
 * opens promising features "in a later pass", and a paperclip in the Scaffold chat whose tooltip said
 * "Attachments arrive with Scaffold", inside Scaffold, which had shipped. The class came back because nothing
 * checked for it (#318). This checks for it.
 *
 * THE RULE, IN THE WORDS IT WAS RULED IN: a string promising a future of the PRODUCT breaks it; a string
 * stating the present of the USER'S OWN WORK does not. "Nothing has been built yet" is a true fact about their
 * canvas. So this gate deliberately does NOT check "yet": sixteen of its hits on the day it was written were
 * true, and a gate that cries wolf gets disabled (#336). It checks only the high-precision phrases - the ones
 * with no true reading in a shipped product. Words a single function owns can be guarded more tightly where
 * they are written (see AgentEligibilityTests.No_refusal_promises_a_future).
 *
 * WHAT IT READS: USER-VISIBLE text only. C# string literals, with comments stripped; HTML with its comments,
 * its <style> blocks, its script comments and its class names stripped. Code comments are not user-visible,
 * and a gate that also read them would be a grep.
 *
 * WHY IT TESTS ITSELF FIRST. Before it judges the product it runs over tools/product-copy-controls/: every
 * line of the positive controls must be found, and nothing in the negative controls may be. If either fails,
 * the INSTRUMENT is broken and it says so rather than passing - a gate that has never been seen to find
 * anything has not been shown to look (#212).
 */
const fs = require('fs');
const path = require('path');

const PATTERNS = [
  /\blater (phase|pass|release|version)\b/i,
  /\bin a later\b/i,
  /\bplanned for\b/i,
  /\bcoming soon\b/i,
  /\bsoon\b/i,
  /\barrives? with\b/i,
  /\bfuture (release|version)\b/i,
];

function firstMatch(s) {
  for (const p of PATTERNS) { const m = s.match(p); if (m) return m[0]; }
  return null;
}

// Cut a line at a // comment that is OUTSIDE any string literal. quotes: the characters that open a string.
function cutLineComment(line, quotes) {
  let q = null;
  for (let i = 0; i < line.length; i++) {
    const c = line[i];
    if (q) {
      if (c === '\\') { i++; continue; }
      if (c === q) q = null;
    } else if (quotes.includes(c)) {
      q = c;
    } else if (c === '/' && line[i + 1] === '/') {
      return line.slice(0, i);
    }
  }
  return line;
}

// Blank out /* */ blocks and <!-- --> comments while keeping line numbers.
function blank(text, re) { return text.replace(re, (m) => m.replace(/[^\n]/g, ' ')); }

// C#: the string literals on each line, after comments are removed.
function csUserText(src) {
  const text = blank(src, /\/\*[\s\S]*?\*\//g);
  return text.split('\n').map((line) => {
    const code = cutLineComment(line, '"');
    return (code.match(/\$?@?"(?:[^"\\]|\\.|"")*"/g) || []).join(' ');
  });
}

// HTML: text and attributes, without comments, styles, script comments or class names.
function htmlUserText(src) {
  let text = blank(src, /<!--[\s\S]*?-->/g);
  text = blank(text, /<style[\s\S]*?<\/style>/gi);
  text = blank(text, /\/\*[\s\S]*?\*\//g);
  return text.split('\n').map((line) => cutLineComment(line, '"\'`').replace(/class=(["'])[^"']*\1/g, ''));
}

function scan(file) {
  const src = fs.readFileSync(file, 'utf8');
  const lines = file.endsWith('.cs') ? csUserText(src) : file.endsWith('.html') ? htmlUserText(src) : [];
  const hits = [];
  lines.forEach((l, i) => { const m = firstMatch(l); if (m) hits.push({ file, line: i + 1, what: m, text: l.trim() }); });
  return hits;
}

function walk(dir, out = []) {
  for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
    if (['bin', 'obj', 'node_modules', '.git'].includes(e.name)) continue;
    const p = path.join(dir, e.name);
    if (e.isDirectory()) walk(p, out);
    else if (p.endsWith('.cs') || p.endsWith('.html')) out.push(p);
  }
  return out;
}

// ---- 1. the instrument, on its controls ----------------------------------------------------------
const CONTROLS = path.join(__dirname, 'product-copy-controls');
let broken = 0;
for (const f of fs.readdirSync(CONTROLS)) {
  const file = path.join(CONTROLS, f);
  const hits = scan(file);
  if (f.startsWith('positive')) {
    // A planted defect is a line carrying a comment that BEGINS with PLANTED. Not merely any line containing
    // the word - the first run of this gate counted the header comment that EXPLAINS the marker as a planted
    // line, missed it, and (correctly) reported itself broken rather than passing.
    const planted = fs.readFileSync(file, 'utf8').split('\n')
      .map((l, i) => ({ l, n: i + 1 })).filter((x) => /(\/\/|<!--)\s*PLANTED\b/.test(x.l));
    for (const p of planted) {
      if (!hits.some((h) => h.line === p.n)) { console.log('  INSTRUMENT BROKEN: missed planted ' + f + ':' + p.n); broken++; }
    }
    if (planted.length === 0) { console.log('  INSTRUMENT BROKEN: ' + f + ' plants nothing'); broken++; }
  } else if (f.startsWith('negative')) {
    for (const h of hits) { console.log('  INSTRUMENT BROKEN: flagged a non-user-visible line ' + f + ':' + h.line + ' [' + h.what + ']'); broken++; }
  }
}
if (broken) {
  console.error('\nThe product copy gate failed its OWN controls - it is the gate that is wrong, not the product.');
  process.exit(1);
}
console.log('Controls: every planted defect found, every comment ignored.');

// ---- 2. the product ------------------------------------------------------------------------------
const hits = walk(path.join(__dirname, '..', 'Dwight')).flatMap(scan);
for (const h of hits) console.log('  ' + path.relative(path.join(__dirname, '..'), h.file) + ':' + h.line + '  [' + h.what + ']  ' + h.text.slice(0, 140));
console.log('\n' + hits.length + ' user-visible string(s) promising a future of the product.');
if (hits.length) {
  console.error('Say what the user can do NOW - usually "select a smaller part" - with no date and no promise.');
  process.exit(1);
}
