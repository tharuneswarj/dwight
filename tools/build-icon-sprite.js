#!/usr/bin/env node
/*
 * The one icon table (Tharun's pick, Brief BO: Tabler, one set everywhere, at a 1.5 stroke). It writes Dwight/Resources/dwight-icons.svg,
 * the sprite every shell inlines, so an icon is <svg class="ic"><use href="#ic-NAME"/></svg> and nothing else. tools/ui-style-audit.js
 * fails any icon drawn anywhere else, and any name the sprite does not hold. The stroke is the system's, set by .ic.
 *
 * The package is not a dependency of the build, because the sprite is committed. To change an icon: edit TABLE, then
 *   npm pack the @tabler/icons package at version 3.50.0, then tar xzf tabler-icons-3.50.0.tgz
 *   node tools/build-icon-sprite.js package/icons/outline
 * and keep the version here, in the sprite and in NOTICE.md the same.
 */
const fs = require('fs'), path = require('path');
const VERSION = '3.50.0';
// What the product calls it -> Tabler's name for it.
const TABLE = {
  // the title bar
  goto: 'focus-centered', moon: 'moon', sun: 'sun', minimise: 'minus', close: 'x',
  // marks inside a card or a row
  check: 'check', chev: 'chevron-right', caret: 'chevron-down', ban: 'ban', lock: 'lock', file: 'file-text', undo: 'arrow-back-up', flag: 'flag', dot: 'point', replay: 'rotate-clockwise',
  // the composer's line: the model, the three approval modes under the ids the host sends, Full control, stop and send
  model: 'cpu', ask: 'shield-lock', scripts_and_deletions: 'shield-half', never: 'shield-off', full: 'bolt', stop: 'player-stop', send: 'send', attach: 'paperclip',
  // the five verbs
  explain: 'message-2', diagnose: 'stethoscope', reorganise: 'layout-grid', simplify: 'arrows-minimize', build: 'hammer',
  // what a card changed
  edit: 'pencil', code: 'code', slider: 'adjustments-horizontal',
  // a note's tools, and the offer to pin one. The rail's "pin" above is a sidebar, kept open
  bold: 'bold', list: 'list', numbers: 'list-numbers', notepin: 'pinned',
  // the Lens's Every wire
  wires: 'vector-spline',
  // the Settings rail
  connection: 'plug', note: 'note', you: 'user', usage: 'chart-bar', about: 'info-circle', pin: 'layout-sidebar',
  // the Timeline: its own spine for the view, and one shape per action, each meaning one thing
  menu: 'menu-2', back: 'arrow-left', plus: 'plus',
  timeline: 'git-commit', expand: 'chevrons-down', collapse: 'chevrons-up', here: 'map-pin', redo: 'arrow-forward-up', chat: 'messages',
  // the Walk's two arrows in the panel's footer: the list runs downward
  up: 'arrow-up', down: 'arrow-down',
  // a Walk playing itself: Play on the panel's footer, which is Pause while it plays
  play: 'player-play', pause: 'player-pause',
  // Focus mode, in the window's title bar
  focus: 'arrows-maximize',
};
// Tharun, Brief BO: the moon carries a third more ink than the sun in the same box, so it reads larger. Scaling its path's own numbers about the
// grid's centre keeps its stroke the stroke of its neighbours, with no one-off scale in the CSS.
const SCALE = { moon: 0.85 };
function scalePath(d, k, cx = 12, cy = 12) {
  const toks = d.match(/[a-zA-Z]|-?\d*\.?\d+(?:e-?\d+)?/g); let out = '', i = 0;
  const f = (n) => +n.toFixed(3);
  while (i < toks.length) {
    const c = toks[i++]; out += c; const nums = []; while (i < toks.length && !/[a-zA-Z]/.test(toks[i])) nums.push(+toks[i++]);
    if (c === 'M') out += f(cx + (nums[0] - cx) * k) + ' ' + f(cy + (nums[1] - cy) * k);
    else if (c === 'a') { for (let j = 0; j < nums.length; j += 7) out += [f(nums[j] * k), f(nums[j + 1] * k), nums[j + 2], nums[j + 3], nums[j + 4], f(nums[j + 5] * k), f(nums[j + 6] * k)].join(' ') + ' '; }
    else if (c === 'c' || c === 'l') out += nums.map((n) => f(n * k)).join(' ') + ' ';
    else if (c !== 'z') throw new Error('scalePath: no rule for ' + c);
  }
  return out.trim();
}
const from = process.argv[2];
if (!from) { console.error('usage: node tools/build-icon-sprite.js <the package\'s icons/outline folder>'); process.exit(2); }
let out = `<!-- Tabler Icons ${VERSION} (MIT, see NOTICE.md), outline. Written by tools/build-icon-sprite.js; do not edit by hand. -->\n<svg xmlns="http://www.w3.org/2000/svg" style="display:none" aria-hidden="true">\n`;
for (const [name, tabler] of Object.entries(TABLE)) {
  const s = fs.readFileSync(path.join(from, tabler + '.svg'), 'utf8');
  const inner = /<svg[^>]*>([\s\S]*)<\/svg>/.exec(s)[1].replace(/<path stroke="none" d="M0 0h24v24H0z" fill="none"\s*\/>/, '').replace(/\s*\n\s*/g, '').replace(/\s+\/>/g, '/>').trim();
  const body = SCALE[name] ? inner.replace(/d="([^"]+)"/g, (m, d) => `d="${scalePath(d, SCALE[name])}"`) : inner;
  out += `<symbol id="ic-${name}" viewBox="0 0 24 24" data-tabler="${tabler}">${body}</symbol>\n`;
}
out += '</svg>\n';
fs.writeFileSync(path.join(__dirname, '..', 'Dwight', 'Resources', 'dwight-icons.svg'), out);
console.log(Object.keys(TABLE).length + ' icons written.');
