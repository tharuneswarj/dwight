#!/usr/bin/env node
/*
 * Raw control characters in source (#317).
 *
 * WHY THIS EXISTS. A prompt shipped with a literal 0x08 in it, because an editing step ate one backslash
 * of a "\begin" and left C# to interpret "\b". Nothing caught it: it compiles, it renders as nothing, and
 * it reads correctly in every diff and review - an invisible byte inside text we send to a model.
 *
 * That was the FOURTH escape-eating incident of the work-package and the SECOND to ship. The first shipped
 * one (a JS quote) cost two runtime rounds and got tools/check-shell-js.js; this is the same remedy for the
 * same class - make it fail once, mechanically, at authorship.
 *
 * THE RULE: no raw C0 control byte in source except tab, CR and LF. A control character that is genuinely
 * wanted is written as an ESCAPE (C#: "\u001F"), which is visible, greppable, and cannot be produced by an
 * editing accident. So the gate does not need an allow-list of "deliberate" bytes - the deliberate ones
 * stop being bytes.
 */
const fs = require('fs');
const path = require('path');

const ROOTS = ['Dwight', 'tools', 'tests'];
const EXTS = new Set(['.cs', '.html', '.css', '.js', '.json', '.md', '.csproj', '.sln', '.yml', '.yaml']);
const SKIP = new Set(['bin', 'obj', 'node_modules', '.git', '.vs']);

function* walk(dir) {
  for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
    if (e.isDirectory()) { if (!SKIP.has(e.name)) yield* walk(path.join(dir, e.name)); continue; }
    if (EXTS.has(path.extname(e.name))) yield path.join(dir, e.name);
  }
}

let scanned = 0;
const findings = [];

for (const root of ROOTS) {
  if (!fs.existsSync(root)) continue;
  for (const file of walk(root)) {
    scanned++;
    const text = fs.readFileSync(file, 'utf8');
    let line = 1;
    for (let i = 0; i < text.length; i++) {
      const c = text.charCodeAt(i);
      if (c === 10) { line++; continue; }
      if (c === 9 || c === 13) continue;
      if (c < 32 || c === 127) {
        const ctx = text.slice(Math.max(0, i - 45), i).replace(/\s+/g, ' ');
        findings.push({ file, line, code: c, ctx });
      }
    }
  }
}

for (const f of findings)
  console.log(`  FAIL  ${f.file}:${f.line}  raw control byte 0x${f.code.toString(16).toUpperCase().padStart(2, '0')}` +
              `  after: ...${f.ctx}`);

console.log(`\n${scanned} source file(s) scanned; ${findings.length} raw control byte(s) found.`);
if (findings.length) {
  console.log('Write intended control characters as escapes ("\\u001F" in C#), never as raw bytes.');
  process.exit(1);
}
