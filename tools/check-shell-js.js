// Parse the JavaScript inside every Dwight HTML shell, and fail loudly if any of it does not.
//
// WHY THIS EXISTS (#275). Every inertness proof this phase made was on the C# side, where the compiler
// is the guard. The shells carried no equivalent: three shell changes shipped on review alone, and one
// of them - a Stop button whose onclick escape was mangled to `dwightSend('stop')` inside a
// single-quoted string - was a PARSE ERROR. A parse error kills the whole script block, so every shell
// function ceased to exist and all five verbs completed their work, recorded their history, and left the
// loader on screen forever. Nothing threw, because there was no error to throw: the functions were
// simply never defined.
//
// A C# escape mistake is caught by the compiler. The same mistake in an HTML resource was caught by
// nobody. This is that compiler.
//
// Deliberately dependency-free (node's own parser) and headless, so it runs in CI on every push rather
// than depending on someone remembering to look.

const fs = require('fs');
const path = require('path');
const vm = require('vm');

const dir = path.join(__dirname, '..', 'Dwight', 'Resources');
const shells = fs.readdirSync(dir).filter(f => f.endsWith('.html'));

let failures = 0;
let blocksChecked = 0;

for (const file of shells) {
  const html = fs.readFileSync(path.join(dir, file), 'utf8');
  const blocks = [...html.matchAll(/<script(?![^>]*\bsrc=)[^>]*>([\s\S]*?)<\/script>/g)];

  if (blocks.length === 0) {
    console.log(`  ${file}: no inline script`);
    continue;
  }

  blocks.forEach((m, i) => {
    blocksChecked++;
    try {
      // Compiling is enough: it parses without running, so nothing in the shell executes here.
      new vm.Script(m[1], { filename: `${file}#script[${i}]` });
      console.log(`  OK  ${file} script[${i}]  (${m[1].length} chars)`);
    } catch (err) {
      failures++;
      console.error(`  FAIL ${file} script[${i}]: ${err.message}`);
      const line = (err.stack || '').split('\n').slice(0, 4).join('\n');
      console.error(line);
    }
  });
}

console.log(`\n${blocksChecked} inline script block(s) in ${shells.length} shell(s); ${failures} failed.`);

if (failures > 0) {
  console.error('\nA shell whose script does not parse defines NO functions at all, so every panel');
  console.error('renders its static HTML and nothing else - work completes, history records, and the');
  console.error('loader stays up forever. Fix the syntax before pushing.');
  process.exit(1);
}
