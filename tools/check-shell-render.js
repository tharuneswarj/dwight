#!/usr/bin/env node
/*
 * Shell render smoke harness (#275, built as the gate for #291/D1).
 *
 * WHY THIS EXISTS. tools/check-shell-js.js proves the inline script PARSES. It cannot prove any function
 * in it still WORKS - and the regression that bought it did not stop there: a parse error killed the whole
 * script block, so every one of the five verbs completed its work and left the loader up forever, with
 * nothing thrown because there was nothing to throw. Syntax was the cheapest half. This is the other half.
 *
 * WHAT IT ASSERTS, and deliberately no more: every window-facing entry point in every shell, called with
 * fixture data, (1) does not throw and (2) produces its named DOM change. NOT appearance. The harness
 * catches "the function is broken"; only Tharun's per-verb render check catches "it looks wrong", and this
 * does not replace it.
 *
 * WHY jsdom RATHER THAN A STUB. Measured before choosing: the shells use getElementById (64), classList
 * (31), innerHTML (25), querySelector/All (32), createElement, appendChild, closest, insertAdjacentHTML
 * and getComputedStyle. innerHTML assignment followed by querySelectorAll over the result means a stub
 * would need an HTML parser AND a selector engine - larger than the thing it tests, which is the stated
 * threshold for taking the dependency. Nothing here ships: the .gha embeds the HTML, not this.
 *
 * WHAT THE SHELLS CARRY HERE (Brief BK): the shared CSS is inlined as ShellAssets.Inline does, so a computed-style
 * assertion can witness a rule in dwight-tokens.css or dwight-components.css as well as one in the shell's own block. The
 * fonts are left out; nothing here measures a glyph. jsdom does not resolve var() and does no layout, so an assertion about
 * a value or a size belongs to tools/ui-measure.js, which runs in a real browser. This harness still does not judge
 * appearance, and its style gates are at the end: tools/ui-style-audit.js over the source, tools/ui-measure.js on the page.
 *
 * WHY IT CANNOT QUIETLY GO STALE. Any dwight* function a shell exposes that has no fixture FAILS the gate
 * unless it is named in DECLARED_UNCOVERED with a reason. A maintained instrument must fail safe and fail
 * loud; one that silently covers 30 of 50 entry points is the instrument this project keeps having to fix.
 */
const fs = require('fs');
const path = require('path');
const { JSDOM } = require('jsdom');

const SHELLS = path.join('Dwight', 'Resources');
const SCRIPT_BLOCK = /<script[^>]*>([\s\S]*?)<\/script>/;
const ENTRY_POINT = /function\s+(dwight[\w$]*)\s*\(/g;

// ---------------------------------------------------------------------------------------------
// Fixtures. Shapes are taken from the field names each function actually reads, cross-checked
// against the C# producers in Dwight/UI/WebPanel.cs - not invented.
// ---------------------------------------------------------------------------------------------
const J = (o) => JSON.stringify(o);
const ANCHOR = (href) => '<a id="p" href="' + href + '" onclick="dwightSend(\'leak\')">x</a>';
const HELD = () => J({ src: 'data:image/png;base64,iVBORw0KGgo=', line: '1568 x 1000 px, +1 image, about 2,100 tokens', note: '' });
const IMG_THREAD = () => J([{ who: 'me', html: 'look', img: { sha: 'abc', meta: '1568 x 1000 px' } }]);

// The tour's init payload, in the shape DwightVerbs/DwightChord produce it. The names are fixture
// INPUT, not an assertion about the product's verb list - the shell renders what the plugin hands it.
const TOUR_VERBS = [
  { name: 'Explain', desc: 'read back what it does' },
  { name: 'Diagnose', desc: 'find what is wrong' },
  { name: 'Reorganise', desc: 'lay it out' },
  { name: 'Simplify', desc: 'better logic, as a diff' },
  { name: 'Build', desc: 'plan and build' },
];
const TOUR_INIT = J({ verbs: TOUR_VERBS, chord: 'Ctrl+Shift+Right-click', needsKey: true, docs: '' });
// Brief AO: the chat header's document, in the shape ChatHeader.Json writes. The texts are fixture INPUT - the shell draws what the host sends.
// Brief BG: a waiting card and its receipt, as ConsentCard.Card and CardReceipt.Of render them (the C# tests pin the C# side).
const CARD_WAIT = '<div class="consent" data-kind="solve" data-act="Re-run the definition."><p class="consent-act"><strong>Re-run the definition.</strong></p>' +
  '<div class="consent-why"><span class="consent-why-label">Dwight\'s reason for asking</span>RCPT-REASON</div>' +
  '<div class="consent-btns"><button class="btn primary" onclick="dwightSend(\'consent:tok-a\')">Yes, go ahead</button></div>' +
  '<p class="consent-line">Nothing on the canvas changes.</p><details class="consent-more"><summary>What this does and what it cannot</summary>' +
  '<p class="consent-note">RCPT-AUTHORITY</p></details></div>';
const RECEIPT = (state) => '<div class="receipt" tabindex="0" role="button" aria-expanded="false" data-state="' + state + '"><div class="rc-row">' +
  '<svg class="ic" aria-hidden="true"><use href="#ic-dot"/></svg><span class="rc-text">Re-run the definition.</span></div>' +
  '<div class="rc-why"><span class="rc-state ok">' + state + '</span> RCPT-REASON</div><div class="rc-full">' + CARD_WAIT + '</div></div>';
const TV = require('./ui-teaching-views.json');
const DECK_STEPS = J({ summary: 'A loft.', steps: [{ text: 'A', ids: [] }, { text: 'B', ids: [] }] });
const W_STEP = (i, n, gone) => J(Object.assign({ label: 'Step ' + (i + 1) + ' of ' + n, html: 'SENTENCE-HTML <code>Loft</code>', i, n }, gone ? { gone } : {}));
const HDR_MODELS = [
  { id: 'posture:economy', label: 'Economy', group: 'posture', on: false }, { id: 'posture:balanced', label: 'Balanced', group: 'posture', on: true },
  { id: 'posture:quality', label: 'Quality', group: 'posture', on: false }, { id: 'posture:ultra', label: 'Ultra', group: 'posture', on: false },
  { id: 'claude-sonnet-5', label: 'Sonnet 5', group: 'model', on: false }, { id: 'claude-opus-5', label: 'Opus 5', group: 'model', on: false },
];
const HDR_MODES = [
  { id: 'ask', label: 'Ask every time', title: 'a', on: true }, { id: 'scripts_and_deletions', label: 'Scripts and deletions', title: 'b', on: false },
  { id: 'never', label: 'Do not ask', title: 'c', on: false },
];
const HDR = (o) => J(Object.assign({
  model: { text: 'Balanced \u00B7 Sonnet 5', title: 'TIP-MODEL', choices: HDR_MODELS }, approval: { text: 'Ask every time', title: 'TIP-APPROVAL', mode: 'ask', choices: HDR_MODES },
  full: { on: false, text: 'Full control off', tip: 'TIP-FULL-OFF', title: 'sentence' }, cost: { text: '~23.5\u00A2 \u00B7 3 turns', title: 'TIP-RECEIPT' },
}, o || {}));
const TOUR_KEYED = J({ verbs: TOUR_VERBS, chord: 'Ctrl+Shift+Right-click', needsKey: false, docs: '' });

// SENTINEL DATA for the redesigned tour. None of these strings exists anywhere in the product: they are made up to be
// unmistakable, so seeing one in the rendered document proves it ARRIVED through dwightTourInit and was not typed into
// tour.html. The chord is not the real chord for the same reason. (The source scan further down is the other half: it
// fails if the REAL strings are typed into the shell.)
const SENT_VERBS = ['A', 'B', 'C', 'D', 'E'].map((k) => ({ name: 'SENT-VERB-' + k, desc: 'SENT-LINE-' + k, tag: 'SENT-TAG-' + k }));
const SENT = (needsKey, docs, keys) => J({
  verbs: SENT_VERBS, chord: 'Mod+Alt+Tap', needsKey, docs: docs || '', keys: keys || '',
  trust: ['SENT-TRUST-1', 'SENT-TRUST-2', 'SENT-TRUST-3'],
  consent: { act: 'SENT-CARD-ACT.', line: 'SENT-CARD-LINE.', more: 'SENT-CARD-MORE', reasonLabel: 'SENT-REASON-LABEL', approve: 'SENT-APPROVE' },
});
const SENT_ALL = [].concat(
  ['A', 'B', 'C', 'D', 'E'].flatMap((k) => ['SENT-VERB-' + k, 'SENT-TAG-' + k]),
  ['Mod+Alt+Tap', '>Mod<', '>Alt<', '>Tap<', 'SENT-TRUST-1', 'SENT-TRUST-2', 'SENT-TRUST-3'],
  ['SENT-CARD-ACT.', 'SENT-CARD-LINE.', 'SENT-CARD-MORE', 'SENT-REASON-LABEL', 'SENT-APPROVE']);
const T_INIT = [['dwightTourInit', [SENT(false)]]];        // a key is stored
const T_INIT_NOKEY = [['dwightTourInit', [SENT(true)]]];   // a key is still needed
const T_LAST = [['dwightTourGo', [4]]];

// Brief AP: a History payload shaped like DwightDock.RefreshHistory's, with sentinels in place of HistoryCore's words.
// Brief ED: a Notes pane shaped like NotesPane.View's, with sentinels in place of its words.
const NPANE = (over) => J(Object.assign({
  heading: 'NOTES-HEAD', newNote: 'NEW-NOTE', empty: 'EMPTY-NOTES', show: 'Show notes', count: 2,
  rows: [{ id: 'abc123', words: 'WORDS-A', who: 'WHO-A', when: 'WHEN-A', about: 'ABOUT-A', state: 'stale', stateWord: 'STATE-A' },
         { id: 'def456', words: 'WORDS-B', who: 'WHO-B', when: 'WHEN-B', about: null, state: 'fresh', stateWord: '' }],
}, over));
const HIST = (over) => J(Object.assign({
  items: [
    { type: 'event', key: 'KEY-A', state: 'standing', word: 'WORD-A', verb: 'Build', title: 'TITLE-A', count: 2, countWord: 'COUNT-A', time: 'TIME-A', threadId: 'abc', openChat: 'OPEN-CHAT', by: 'BY-A',
      actions: [{ action: 'before', id: '11111111-1111-1111-1111-111111111111', button: 'BTN-BEFORE' }, { action: 'back', id: 'not-a-guid', button: 'NEVER' }],
      steps: [
        { id: '11111111-1111-1111-1111-111111111111', state: 'standing', word: 'WORD-A', line: 'STEP-A', time: 'T1', by: 'BY-STEP', yours: false, reverted: null, action: { action: 'back', id: '11111111-1111-1111-1111-111111111111', button: 'BTN-STEP' } },
        { id: '33333333-3333-3333-3333-333333333333', state: 'standing', word: 'WORD-A', line: 'YOURS-STEP', time: '', yours: true, reverted: null, action: null },
      ] },
    { type: 'yours', line: 'YOURS-LINE', ids: ['44444444-4444-4444-4444-444444444444'] },
    { type: 'here', line: 'HERE-LINE' },
    { type: 'event', key: 'KEY-B', state: 'undone', word: 'WORD-B', verb: 'Reorganise', title: '', count: 1, countWord: null, time: 'TIME-B', threadId: null, openChat: null, by: null, actions: [],
      steps: [{ id: '', state: 'takenback', word: 'taken back', line: 'STEP-B', time: 'T2', by: null, yours: false, reverted: 'REVERTED-B', action: null }] },
  ],
  heading: 'HEAD-W', historyHeading: 'LIST-HEAD', newChat: 'NEW-W', expandAll: 'EXPAND-W', collapseAll: 'COLLAPSE-W', back: 'BACK-W', historyEmpty: 'LIST-EMPTY', empty: 'EMPTY-LINE', confirm: null, confirmIds: [], yes: 'YES-BTN', no: 'NO-BTN', note: null,
}, over));

// Brief BC: every info icon on the Settings page, by the id of the explanation it opens.

// Brief AK: a Usage payload shaped like SpendLedgerCore.UsageJson, with sentinels in place of the plugin's words.
const UROW = (label) => ({ label, runs: 1, turns: 2, tokensIn: '41k', tokensOut: '6k', cost: '~12.3¢' });
const USAGE = (skipped) => J({
  periods: [UROW('PERIOD-TODAY'), UROW('PERIOD-WEEK'), UROW('PERIOD-MONTH'), UROW('PERIOD-ALL')],
  byModel: [[], [], [UROW('MODEL-MONTH')], [UROW('MODEL-ALL')]],
  byVerb: [[], [], [UROW('VERB-MONTH')], []],
  byDocument: [[], [], [UROW('DOC-MONTH')], [UROW('DOC-ALL')]],
  fileLine: 'FILE-LINE', skippedLine: skipped, emptyLine: 'EMPTY-LINE',
});
const FIXTURES = {
  'panel-shell.html': [
    ['dwightSetAction', ['Explain'], { changed: true }],
    ['dwightSetBody', ['<p>hello</p>'], { sel: '#body', nonEmpty: true }],
    ['dwightPlain', ['plain text', false], { changed: true }],
    ['dwightPlain', ['an error', true], { changed: true }],
    ['dwightThinking', ['Reading your definition', true], { changed: true }],
    // Same shape as the chat block below: the staged strip these two write into is built by dwightThinking,
    // so they are exercised after it - the order the product uses.
    ['dwightRunMeta', ['Sonnet - 1.2s - 900 tokens'], { pre: [['dwightThinking', ['Reading', true]]], changed: true }],
    ['dwightSetStage', [1], { pre: [['dwightThinking', ['Reading', true]]], changed: true }],
    ['dwightArrange', ['compact'], { changed: true, text: [['#arrange .dd-txt', 'Compact']], absent: '#arrange .seg' }],
    ['dwightArrange', ['grid'], { text: [['#arrange .dd-txt', 'Grid']] }],
    ['dwightOutcome', ['Verified'], { changed: true }],
    ['dwightShowState', ['notice', 'Nothing to simplify', 'Select some components.', 'Retry', 'retry', '', ''],
      { changed: true }],
    ['dwightSteps', [J({ steps: [{ text: 'Loft the curves', ids: [1, 2] }], summary: 'A loft.' })], { changed: true }],
    // Brief DC: the offer on the summary and on each step. It is a button the person clicks, not a note Dwight writes.
    ['dwightSteps', [J({ steps: [{ text: 'Loft the curves', ids: [1, 2] }, { text: 'Cap it', ids: [3] }], summary: 'A loft.' })], { sel: '.step > .pincorner', containsAll: ['pin:summary', 'pin:step:0', 'pin:step:1', 'Pin as a note'], absent: '.stxt .pincorner, .btn.pin', computed: ['.step .pincorner', 'position', 'absolute'] }],
    // The glyph names itself by its word, not a label under the sentence; it is the corner's, so it hides until the card is hovered or current.
    ['dwightSteps', [J({ steps: [{ text: 'A', ids: [1] }] })], { attr: [['.pincorner', 'aria-label', 'Pin as a note'], ['.pincorner', 'title', 'Pin as a note']], computed: ['.pincorner', 'opacity', '0'] }],
    ['dwightStepCurrent', [0, true, ''], { pre: [['dwightSteps', [J({ steps: [{ text: 'A', ids: [1] }] })]]], computed: ['.step.on .pincorner', 'opacity', '1'] }],
    // Brief DA: the card the Walk is on is the host's to say; while one is live, the page passes left and right on, and it stops when the Walk leaves.
    ['dwightStepCurrent', [1], { pre: [['dwightSteps', [J({ steps: [{ text: 'A', ids: [] }, { text: 'B', ids: [] }] })]]], changed: true, sel: '.step:nth-of-type(2).on', absent: '.step:nth-of-type(1).on' }],
    ['dwightSteps', [J({ steps: [{ text: 'One', ids: [1] }, { text: 'Two', ids: [1, 2] }] })], { text: [['.step:nth-of-type(1) .cmpt', '1 component'], ['.step:nth-of-type(2) .cmpt', '2 components']], excludes: ['1 components'] }],
    ['dwightStepCurrent', [0], { pre: [['dwightSteps', [J({ steps: [{ text: 'A', ids: [] }, { text: 'B', ids: [] }] })]]], keys: ['ArrowRight'], postsExactly: 'walkkey:ArrowRight' }],
    ['dwightStepCurrent', [-1], { pre: [['dwightSteps', [J({ steps: [{ text: 'A', ids: [] }] })]], ['dwightStepCurrent', [0]]], keys: ['ArrowRight'], postsAll: [], absent: '.step.on' }],
    // Tharun's sketch: while a Walk is live the footer carries its four controls, in one spot, whether or not the bar is showing; the current card carries none. Icons from the one table in the library's icon button, each with its word as tooltip and accessible name, the status giving way, the ends disabled.
    ['dwightStepCurrent', [1, false, ''], { pre: [['dwightSteps', [J({ steps: [{ text: 'A', ids: [] }, { text: 'B', ids: [] }, { text: 'C', ids: [] }] })]]], sel: '.foot.walking .walknav #walk-back.iconbtn use[href="#ic-up"]', absent: '.walknav [disabled]', excludes: ['>Back<', '>Next<', '>Ask<', '>Leave<'],
      attr: [['#walk-back', 'title', 'Back'], ['#walk-back', 'aria-label', 'Back'], ['#walk-next', 'title', 'Next'], ['#walk-next', 'aria-label', 'Next'], ['#walk-ask', 'title', 'Ask'], ['#walk-ask', 'aria-label', 'Ask'], ['#walk-leave', 'title', 'Leave'], ['#walk-leave', 'aria-label', 'Leave']] }],
    ['dwightStepCurrent', [1, true, ''], { pre: [['dwightSteps', [J({ steps: [{ text: 'A', ids: [] }, { text: 'B', ids: [] }, { text: 'C', ids: [] }] })]]], sel: '.foot.walking #walk-next.iconbtn use[href="#ic-down"]', absent: '.steps .iconbtn:not(.pincorner)' }],
    ['dwightStepCurrent', [1, true, ''], { pre: [['dwightSteps', [J({ steps: [{ text: 'A', ids: [] }, { text: 'B', ids: [] }, { text: 'C', ids: [] }] })]]], sel: '#walk-ask.iconbtn use[href="#ic-chat"]', absent: '.steps button:not(.pincorner)' }],   // Brief DC: the one exception is the corner pin glyph, which is about the card and does not move when the highlight moves; the Walk's controls (Back, Next, Ask, Leave) stay the footer's alone
    ['dwightStepCurrent', [1, true, ''], { pre: [['dwightSteps', [J({ steps: [{ text: 'A', ids: [] }, { text: 'B', ids: [] }, { text: 'C', ids: [] }] })]]], sel: '#walk-leave.iconbtn use[href="#ic-close"]' }],
    ['dwightSetTheme', ['dark'], { sel: '#walknav', absent: '.foot.walking' }],
    // Brief DH: a finished Explain has arrived. The controls are there, no card is current, Next is step 1, and there is nothing yet to go back to or ask about.
    ['dwightWalkArrived', [3], { pre: [['dwightSteps', [J({ steps: [{ text: 'A', ids: [] }, { text: 'B', ids: [] }, { text: 'C', ids: [] }] })]]], sel: '.foot.walking #walk-back[disabled]', absent: '.step.on', text: [['#walk-count', '3 steps']] }],
    ['dwightWalkArrived', [3], { pre: [['dwightSteps', [J({ steps: [{ text: 'A', ids: [] }, { text: 'B', ids: [] }, { text: 'C', ids: [] }] })]]], sel: '#walk-ask[disabled]', absent: '#walk-next[disabled]' }],
    ['dwightWalkArrived', [3], { pre: [['dwightSteps', [J({ steps: [{ text: 'A', ids: [] }, { text: 'B', ids: [] }, { text: 'C', ids: [] }] })]]], sel: '#walkrow.on', absent: '#walk-focus-title[hidden]' }],
    ['dwightWalkArrived', [3], { pre: [['dwightSteps', [J({ steps: [{ text: 'A', ids: [] }, { text: 'B', ids: [] }, { text: 'C', ids: [] }] })]]], keys: ['ArrowDown'], postsExactly: 'walkkey:ArrowDown' }],
    ['dwightWalkArrived', [1], { pre: [['dwightSteps', [J({ steps: [{ text: 'A', ids: [] }] })]]], text: [['#walk-count', '1 step']], sel: '#walk-play[disabled]' }],
    // The text check's wiring: each of these is true of the old substring check's blind spot or of a word not on the page, and each must fail the way it says.
    ['dwightWalkArrived', [1], { pre: [['dwightSteps', [J({ steps: [{ text: 'A', ids: [] }] })]]], text: [['#walk-count', '1 ste']], mustFail: '#walk-count reads "1 step", not "1 ste"' }],
    ['dwightWalkArrived', [1], { pre: [['dwightSteps', [J({ steps: [{ text: 'A', ids: [] }] })]]], text: [['#walk-count', '']], mustFail: '#walk-count reads "1 step", not ""' }],
    ['dwightWalkArrived', [1], { pre: [['dwightSteps', [J({ steps: [{ text: 'A', ids: [] }] })]]], textContains: [['#walk-count', '9 steps']], mustFail: 'with no "9 steps"' }],
    ['dwightWalkArrived', [3], { pre: [['dwightSteps', [J({ steps: [{ text: 'A', ids: [] }, { text: 'B', ids: [] }, { text: 'C', ids: [] }] })]]], sel: '.walknav #walk-play', absent: '#walk-play[disabled]' }],
    // The footer says where the Walk is, beside the icons, and asking works again once there is a step.
    ['dwightStepCurrent', [1, true, ''], { pre: [['dwightSteps', [J({ steps: [{ text: 'A', ids: [] }, { text: 'B', ids: [] }, { text: 'C', ids: [] }] })]], ['dwightWalkArrived', [3]]], text: [['.walkrow #walk-count', '2 of 3']], absent: '#walk-ask[disabled]' }],
    ['dwightStepCurrent', [-1, false, ''], { pre: [['dwightSteps', [J({ steps: [{ text: 'A', ids: [] }] })]], ['dwightWalkArrived', [1]]], sel: '#walk-focus-title[hidden]', absent: '#walkrow.on' }],
    ['dwightStepCurrent', [-1, false, ''], { pre: [['dwightSteps', [J({ steps: [{ text: 'A', ids: [] }] })]], ['dwightStepCurrent', [0, false, '']]], text: [['#walk-count', '']], absent: '.foot.walking' }],
    // Brief EC unit 1 (Tharun's sitting): after the bar's X the host turns Focus off and tells the panel the same step. The footer, the walk row and both switches are there, Focus off.
    ['dwightStepCurrent', [1, true, ''], { pre: [['dwightSteps', [J({ steps: [{ text: 'A', ids: [] }, { text: 'B', ids: [] }, { text: 'C', ids: [] }] })]], ['dwightStepCurrent', [1, false, '']], ['dwightWalkSwitches', [true, true]], ['dwightWalkSwitches', [false, true]]],
      sel: '.foot.walking #walk-leave', text: [['#walk-count', '2 of 3']], attr: [['#walk-focus', 'aria-checked', 'false'], ['#walk-dim', 'aria-checked', 'true'], ['#walkrow', 'class', 'walkrow on']], absent: '#walk-focus-title[hidden], .steps .step:nth-of-type(1).on' }],
    // Focus and the dim: real switches, and the title bar's Focus says the same.
    ['dwightWalkSwitches', [true, false], { pre: [['dwightWalkArrived', [3]]], attr: [['#walk-focus', 'aria-checked', 'true'], ['#walk-dim', 'aria-checked', 'false'], ['#walk-focus-title', 'aria-pressed', 'true']] }],
    ['dwightWalkSwitches', [false, true], { pre: [['dwightWalkArrived', [3]]], attr: [['#walk-focus', 'aria-checked', 'false'], ['#walk-dim', 'aria-checked', 'true'], ['#walk-focus-title', 'aria-pressed', 'false']] }],
    ['dwightWalkSwitches', [false, true], { pre: [['dwightWalkArrived', [3]]], click: ['#walk-focus'], postsExactly: 'walk:focus' }],
    ['dwightWalkSwitches', [false, true], { pre: [['dwightWalkArrived', [3]]], click: ['#walk-focus-title'], postsExactly: 'walk:focus' }],
    ['dwightWalkSwitches', [false, true], { pre: [['dwightWalkArrived', [3]]], click: ['#walk-dim'], postsExactly: 'walk:dim:off' }],
    ['dwightWalkSwitches', [false, false], { pre: [['dwightWalkArrived', [3]]], click: ['#walk-dim'], postsExactly: 'walk:dim:on' }],
    // Brief EI: Learn's deck in the panel, from the host's views (TV, the writer's real output). Every word set as text; the page holds no answer and posts each control to the host.
    ['dwightDeck', [TV.start], { pre: [['dwightSteps', [DECK_STEPS]]], sel: '#deck .deck-card.start .deck-start', text: [['#deck .deck-count', '4 questions'], ['#deck .deck-kicker', 'Learn']],
      attr: [['.app', 'class', 'app deckon'], ['#body', 'class', 'docbody deckon']], click: ['#deck .deck-start'], postsExactly: 'deck:start' }],
    ['dwightDeck', [TV.start], { pre: [['dwightSteps', [DECK_STEPS]]], keys: ['Enter'], postsExactly: 'deck:start' }],
    ['dwightDeck', [TV.one], { pre: [['dwightSteps', [DECK_STEPS]]], text: [['#deck .deck-of', '2 of 4'], ['#deck .deck-opt:nth-of-type(3) .deck-key', 'C']], sel: '#deck .deck-tick.right + .deck-tick.on', absent: '#deck .deck-check, #deck .deck-turn, #deck .deck-stamp',
      click: ['#deck .deck-opt:nth-of-type(2)'], postsExactly: 'deck:answer:o%3A1' }],
    ['dwightDeck', [TV.one], { pre: [['dwightSteps', [DECK_STEPS]]], keys: ['2'], postsExactly: 'deck:answer:o%3A1' }],
    ['dwightDeck', [TV.one], { pre: [['dwightSteps', [DECK_STEPS]]], keys: ['ArrowRight'], postsExactly: 'deck:next' }],
    ['dwightDeck', [TV.one], { pre: [['dwightSteps', [DECK_STEPS]]], keys: ['ArrowLeft'], postsExactly: 'deck:back' }],
    ['dwightDeck', [TV.one], { pre: [['dwightSteps', [DECK_STEPS]]], click: ['#deck .deck-back'], postsExactly: 'deck:back' }],
    ['dwightDeck', [TV.all], { pre: [['dwightSteps', [DECK_STEPS]]], text: [['#deck .deck-prompt', 'Choose all that apply.']], click: ['#deck .deck-opt:nth-of-type(1)', '#deck .deck-opt:nth-of-type(3)', '#deck .deck-check'],
      postsExactly: 'deck:answer:s%3A0%2C2', attr: [['#deck .deck-opt:nth-of-type(1)', 'aria-pressed', 'true'], ['#deck .deck-opt:nth-of-type(2)', 'aria-pressed', 'false']] }],
    ['dwightDeck', [TV.all], { pre: [['dwightSteps', [DECK_STEPS]]], keys: ['3', '1', '3', '1', 'Enter'], postsNothing: true }],   // ticked and unticked again: nothing to check
    ['dwightDeck', [TV.all], { pre: [['dwightSteps', [DECK_STEPS]]], keys: ['1', '3', 'Enter'], postsExactly: 'deck:answer:s%3A0%2C2' }],
    ['dwightDeck', [TV.all], { pre: [['dwightSteps', [DECK_STEPS]]], click: ['#deck .deck-check'], postsNothing: true }],
    ['dwightDeck', [TV.enter], { pre: [['dwightSteps', [DECK_STEPS]]], setAfter: [['#deck .deck-field input', 'value', ' 12 ']], click: ['#deck .deck-check'], postsExactly: 'deck:answer:v%3A12',
      attr: [['#deck .deck-field input', 'placeholder', 'Type a number']], text: [['#deck .deck-peek', 'You can peek; this is practice, not a lock.']] }],
    ['dwightDeck', [TV.enter], { pre: [['dwightSteps', [DECK_STEPS]]], click: ['#deck .deck-check'], postsNothing: true }],
    ['dwightDeck', [TV.notANumber], { pre: [['dwightSteps', [DECK_STEPS]]], text: [['#deck .deck-line', 'Type a number, such as 12 or 3.5.']] }],
    ['dwightDeck', [TV.right], { pre: [['dwightSteps', [DECK_STEPS]]], text: [['#deck .deck-stamp', 'Right'], ['#deck .deck-answer', 'The answer is 12.'], ['#deck .deck-mark', 'Read from your definition'], ['#deck .deck-next', 'Next']],
      sel: '#deck .deck-field input[disabled]', absent: '#deck .deck-hint, #deck .deck-given, #deck .deck-check, #deck .deck-peek', click: ['#deck .deck-next'], postsExactly: 'deck:next' }],
    ['dwightDeck', [TV.right], { pre: [['dwightSteps', [DECK_STEPS]]], keys: ['Enter'], postsExactly: 'deck:next' }],
    ['dwightDeck', [TV.wrong], { pre: [['dwightSteps', [DECK_STEPS]]], text: [['#deck .deck-given', 'You answered List Length (13).'], ['#deck .deck-answer', 'The answer is Move (15).'], ['#deck .deck-hint', 'Follow the Points wire.']],
      sel: '#deck div.deck-opt.false, #deck div.deck-opt.true, #deck .deck-turn.wrong', absent: '#deck .deck-stamp, #deck button.deck-opt' }],   // a turned card's options are rows, not buttons
    ['dwightDeck', [TV.wrong], { pre: [['dwightSteps', [DECK_STEPS]]], keys: ['1'], postsNothing: true }],   // a turned card takes no answer
    // Brief EO: a why-card, the model's reading. Its names are the chat's anchors and post the component's run id; an open option answers and holds no anchor.
    ['dwightDeck', [TV.whyOpen], { pre: [['dwightSteps', [DECK_STEPS]]], text: [['#deck .deck-q .anch', 'Move (15)'], ['#deck .deck-own label', 'or say it in your own words']],
      sel: '#deck .deck-q .anch[role="link"]', absent: '#deck .deck-opt .anch, #deck .deck-turn', click: ['#deck .deck-q .anch'], postsExactly: 'deck:look:15' }],
    ['dwightDeck', [TV.whyOpen], { pre: [['dwightSteps', [DECK_STEPS]]], setAfter: [['#deck-own', 'value', ' So each is lofted alone ']], click: ['#deck .deck-owncheck'], postsExactly: 'deck:answer:t%3ASo%20each%20is%20lofted%20alone' }],
    ['dwightDeck', [TV.whyOpen], { pre: [['dwightSteps', [DECK_STEPS]]], setAfter: [['#deck-own', 'value', 'So each is lofted alone']], events: [['#deck-own', 'keydown', { key: 'Enter' }]], postsExactly: 'deck:answer:t%3ASo%20each%20is%20lofted%20alone' }],
    ['dwightDeck', [TV.whyOpen], { pre: [['dwightSteps', [DECK_STEPS]]], setAfter: [['#deck-own', 'value', '   ']], click: ['#deck .deck-owncheck'], postsNothing: true }],   // nothing typed, nothing sent
    ['dwightDeck', [TV.whyOpen], { pre: [['dwightSteps', [DECK_STEPS]]], click: ['#deck .deck-opt:nth-of-type(1)'], postsExactly: 'deck:answer:o%3A0' }],
    // While the judge reads: lavender, its Stop, and the options closed; a key picks nothing.
    ['dwightDeck', [TV.judging], { pre: [['dwightSteps', [DECK_STEPS]]], text: [['#deck .deck-judging span', 'Judging your answer'], ['#deck .deck-stop', 'Stop']], sel: '#deck button.deck-opt[disabled]',
      absent: '#deck .deck-own, #deck .deck-check', click: ['#deck .deck-stop'], postsExactly: 'deck:stop' }],
    ['dwightDeck', [TV.judging], { pre: [['dwightSteps', [DECK_STEPS]]], keys: ['1'], postsNothing: true }],
    ['dwightDeck', [TV.notJudged], { pre: [['dwightSteps', [DECK_STEPS]]], text: [['#deck .deck-line', 'Dwight could not judge that answer. Pick an option instead.']], absent: '#deck .deck-own, #deck .deck-judging',
      click: ['#deck .deck-opt:nth-of-type(1)'], postsExactly: 'deck:answer:o%3A0' }],
    ['dwightDeck', [TV.noJudge], { pre: [['dwightSteps', [DECK_STEPS]]], textContains: [['#deck .deck-line', 'there is none to use here, so pick an option']], absent: '#deck .deck-own' }],
    // Right is green, partly amber, wrong red (the classes the library colours; its rules are held below); a why-card shows why, with its names as doors.
    ['dwightDeck', [TV.partly], { pre: [['dwightSteps', [DECK_STEPS]]], sel: '#deck .deck-turn.partly .deck-why .deck-whyhead', text: [['#deck .deck-stamp.partly', 'Partly'], ['#deck .deck-judged', 'Each is a branch, yes; the loft is why.'],
      ['#deck .deck-by', 'Judged by Dwight · About 0.1 cents, on Claude Haiku 5.'], ['#deck .deck-mark.dwight', "Dwight's reading"]] }],
    ['dwightDeck', [TV.whyWrong], { pre: [['dwightSteps', [DECK_STEPS]]], sel: '#deck .deck-turn.wrong .deck-why', text: [['#deck .deck-mark.dwight', "Dwight's reading"], ['#deck .deck-next', 'Your score'], ['#deck .deck-whyhead', 'Why']],
      absent: '#deck .deck-stamp', click: ['#deck .deck-whytext .anch'], postsExactly: 'deck:look:16' }],
    ['dwightDeck', [TV.right], { pre: [['dwightSteps', [DECK_STEPS]]], sel: '#deck .deck-turn.right, #deck .deck-stamp.right, #deck .deck-tick.right' }],
    ['dwightDeck', [TV.wrong], { pre: [['dwightSteps', [DECK_STEPS]]], sel: '#deck .deck-tick.wrong' }],
    // The result (Brief EO unit 4): the score, the plugin's reading, three doors and two quiet links.
    ['dwightDeck', [TV.result], { pre: [['dwightSteps', [DECK_STEPS]]], text: [['#deck .deck-stamp.big', '2 of 4'], ['#deck .deck-mhead', 'Missed'], ['#deck .deck-miss', 'Question 2: the answer is Move (15).']],
      textContains: [['#deck .deck-reading', 'Missed: why the data tree is grafted, flattened or merged, on step 4; the question about the wiring, on step 2. Right: the reading from the live solve.']], click: ['#deck .deck-miss'], postsExactly: 'deck:miss:1' }],
    ['dwightDeck', [TV.result], { pre: [['dwightSteps', [DECK_STEPS]]], text: [['#deck .deck-doors button:nth-of-type(1)', 'Understand'], ['#deck .deck-doors button:nth-of-type(2)', 'Play again · about 2 cents'], ['#deck .deck-doors button:nth-of-type(3)', 'Ask the chat']],
      sel: '#deck .deck-doors button.primary:nth-of-type(1)', click: ['#deck .deck-doors button:nth-of-type(1)'], postsExactly: 'deck:understand' }],
    ['dwightDeck', [TV.result], { pre: [['dwightSteps', [DECK_STEPS]]], click: ['#deck .deck-doors button:nth-of-type(2)'], postsExactly: 'deck:playagain' }],
    ['dwightDeck', [TV.result], { pre: [['dwightSteps', [DECK_STEPS]]], click: ['#deck .deck-doors button:nth-of-type(3)'], postsExactly: 'deck:ask' }],
    ['dwightDeck', [TV.result], { pre: [['dwightSteps', [DECK_STEPS]]], text: [['#deck .deck-links .deck-link:nth-of-type(1)', 'Try again'], ['#deck .deck-links .deck-link:nth-of-type(2)', 'Step by step']],
      absent: '#deck .deck-doors button:nth-of-type(4)', click: ['#deck .deck-links .deck-link:nth-of-type(1)'], postsExactly: 'deck:again' }],
    ['dwightDeck', [TV.result], { pre: [['dwightSteps', [DECK_STEPS]]], click: ['#deck .deck-links .deck-link:nth-of-type(2)'], postsExactly: 'deck:walk' }],
    ['dwightDeck', [TV.perfect], { pre: [['dwightSteps', [DECK_STEPS]]], textContains: [['#deck .deck-reading', 'Every one right.']], text: [['#deck .deck-streak', '3 in a row']], absent: '#deck .deck-mhead, #deck .deck-miss' }],
    ['dwightDeck', [TV.reopened], { pre: [['dwightSteps', [DECK_STEPS]]], textContains: [['#deck .deck-note', 'To play again, run Learn again']], text: [['#deck .deck-links .deck-link', 'Step by step']],
      absent: '#deck .deck-doors button:nth-of-type(3), #deck .deck-links .deck-link:nth-of-type(2), #deck .deck-back' }],
    // Understand walks the missed cards with why, and goes back to the score; no call.
    ['dwightDeck', [TV.understand], { pre: [['dwightSteps', [DECK_STEPS]]], text: [['#deck .deck-kicker', 'Understand'], ['#deck .deck-count', '2 of 2'], ['#deck .deck-next', 'Back to the score'], ['#deck .deck-whyhead', 'Why']],
      click: ['#deck .deck-next'], postsExactly: 'deck:score' }],
    ['dwightDeck', [TV.understand], { pre: [['dwightSteps', [DECK_STEPS]]], click: ['#deck .deck-back'], postsExactly: 'deck:understand:0' }],
    // Too few cards to deal, the question before Play again, and the card while it writes.
    ['dwightDeck', [TV.few], { pre: [['dwightSteps', [DECK_STEPS]]], textContains: [['#deck .deck-line', 'a deck needs three, so none is dealt this time']], absent: '#deck .deck-stamp',
      click: ['#deck .deck-doors button:nth-of-type(1)'], postsExactly: 'deck:playagain' }],
    ['dwightDeck', [TV.confirm], { pre: [['dwightSteps', [DECK_STEPS]]], textContains: [['#deck .deck-line', 'Continue?']], text: [['#deck .deck-doors button:nth-of-type(1)', 'Continue']],
      click: ['#deck .deck-doors button:nth-of-type(1)'], postsExactly: 'deck:againgo' }],
    ['dwightDeck', [TV.confirm], { pre: [['dwightSteps', [DECK_STEPS]]], click: ['#deck .deck-doors button:nth-of-type(2)'], postsExactly: 'deck:score' }],
    ['dwightDeck', [TV.writing], { pre: [['dwightSteps', [DECK_STEPS]]], text: [['#deck .deck-judging span', 'Writing a fresh deck of questions']], click: ['#deck .deck-doors button:nth-of-type(1)'], postsExactly: 'deck:stop' }],
    // The card's footer carries Focus and Dim, as the panel's own switches say them.
    ['dwightDeck', [TV.one], { pre: [['dwightSteps', [DECK_STEPS]]], after: [['dwightWalkSwitches', [true, false]]], attr: [['#deck-focus', 'aria-checked', 'true'], ['#deck-dim', 'aria-checked', 'false']] }],
    ['dwightDeck', [TV.one], { pre: [['dwightSteps', [DECK_STEPS]], ['dwightWalkSwitches', [false, true]]], click: ['#deck-focus'], postsExactly: 'walk:focus' }],
    ['dwightDeck', [TV.one], { pre: [['dwightSteps', [DECK_STEPS]], ['dwightWalkSwitches', [false, true]]], click: ['#deck-dim'], postsExactly: 'walk:dim:off' }],
    // Markup never runs: the plugin's words, even shaped like tags, are text.
    ['dwightDeck', [J({ view: 'card', k: 1, n: 1, of: '1 of 1', ticks: ['on'], form: 'one', question: '<b>Q</b>', options: [{ key: 'A', text: '<i>x</i>', state: 'open' }], turned: false })],
      { pre: [['dwightSteps', [DECK_STEPS]]], text: [['#deck .deck-q', '<b>Q</b>'], ['#deck .deck-text', '<i>x</i>']], absent: '#deck .deck-q b, #deck .deck-text i' }],
    // An empty view takes the deck away and the steps show; "Walk this" leaves the way back above them.
    ['dwightDeck', [''], { pre: [['dwightSteps', [DECK_STEPS]], ['dwightDeck', [TV.start]]], absent: '#deck, .app.deckon, #body.deckon', sel: '#body .steps' }],
    ['dwightDeck', [''], { pre: [['dwightSteps', [DECK_STEPS]], ['dwightDeck', [TV.one]]], keys: ['2', 'ArrowRight'], postsNothing: true }],
    ['dwightDeckBack', ['BACK-<b>x</b>'], { pre: [['dwightSteps', [DECK_STEPS]]], text: [['#deckback', 'BACK-<b>x</b>']], absent: '#deckback b', click: ['#deckback'], postsExactly: 'deck:quiz' }],
    ['dwightDeckBack', [''], { pre: [['dwightSteps', [DECK_STEPS]], ['dwightDeckBack', ['BACK']]], absent: '#deckback' }],
    ['dwightStepCurrent', [-1, false, ''], { pre: [['dwightSteps', [J({ steps: [{ text: 'A', ids: [] }] })]], ['dwightStepCurrent', [0, false, '']]], absent: '.foot.walking' }],
    ['dwightStepCurrent', [0, true, ''], { pre: [['dwightSteps', [J({ steps: [{ text: 'A', ids: [] }, { text: 'B', ids: [] }] })]]], sel: '#walk-back[disabled]', absent: '#walk-next[disabled]' }],
    ['dwightStepCurrent', [1, true, ''], { pre: [['dwightSteps', [J({ steps: [{ text: 'A', ids: [] }, { text: 'B', ids: [] }] })]]], sel: '#walk-next[disabled]', absent: '#walk-back[disabled]' }],
    ['dwightStepCurrent', [0, true, ''], { pre: [['dwightSteps', [J({ steps: [{ text: 'A', ids: [] }] })]]], sel: '#walk-next[disabled]' }],
    ['dwightStepCurrent', [1, true, ''], { pre: [['dwightSteps', [J({ steps: [{ text: 'A', ids: [] }, { text: 'B', ids: [] }, { text: 'C', ids: [] }] })]]], click: ['#walk-back'], postsExactly: 'walk:back' }],
    ['dwightStepCurrent', [1, true, ''], { pre: [['dwightSteps', [J({ steps: [{ text: 'A', ids: [] }, { text: 'B', ids: [] }, { text: 'C', ids: [] }] })]]], click: ['#walk-next'], postsExactly: 'walk:next' }],
    ['dwightStepCurrent', [1, true, ''], { pre: [['dwightSteps', [J({ steps: [{ text: 'A', ids: [] }, { text: 'B', ids: [] }] })]]], click: ['#walk-ask'], postsExactly: 'walk:ask' }],
    ['dwightStepCurrent', [1, true, ''], { pre: [['dwightSteps', [J({ steps: [{ text: 'A', ids: [] }, { text: 'B', ids: [] }] })]]], click: ['#walk-leave'], postsExactly: 'walk:leave' }],
    ['dwightStepCurrent', [1, false, ''], { pre: [['dwightSteps', [J({ steps: [{ text: 'A', ids: [] }, { text: 'B', ids: [] }, { text: 'C', ids: [] }] })]]], keys: ['ArrowUp'], postsExactly: 'walkkey:ArrowUp' }],
    ['dwightStepCurrent', [1, false, ''], { pre: [['dwightSteps', [J({ steps: [{ text: 'A', ids: [] }, { text: 'B', ids: [] }, { text: 'C', ids: [] }] })]]], keys: ['ArrowDown'], postsExactly: 'walkkey:ArrowDown' }],
    ['dwightStepCurrent', [1, false, ''], { pre: [['dwightSteps', [J({ steps: [{ text: 'A', ids: [] }, { text: 'B', ids: [] }, { text: 'C', ids: [] }] })]]], keys: ['ArrowLeft'], postsExactly: 'walkkey:ArrowLeft' }],
    ['dwightStepCurrent', [-1, false, ''], { pre: [['dwightSteps', [J({ steps: [{ text: 'A', ids: [] }] })]], ['dwightStepCurrent', [0, false, '']]], keys: ['ArrowDown'], postsAll: [] }],
    // The gone line: on the card only where the bar cannot say it, as text, and gone when the bar is back.
    ['dwightStepCurrent', [0, true, 'GONE-LINE'], { pre: [['dwightSteps', [J({ steps: [{ text: 'A', ids: [] }] })]]], text: [['.step.on .walkgone', 'GONE-LINE']] }],
    ['dwightStepCurrent', [0, true, '<b>x</b>'], { pre: [['dwightSteps', [J({ steps: [{ text: 'A', ids: [] }] })]]], text: [['.walkgone', '<b>x</b>']], absent: '.walkgone b' }],
    ['dwightStepCurrent', [0, false, 'GONE-LINE'], { pre: [['dwightSteps', [J({ steps: [{ text: 'A', ids: [] }] })]]], excludes: ['GONE-LINE'] }],
    ['dwightStepCurrent', [1, false, ''], { pre: [['dwightSteps', [J({ steps: [{ text: 'A', ids: [] }, { text: 'B', ids: [] }] })]], ['dwightStepCurrent', [0, true, 'GONE-LINE']]], excludes: ['GONE-LINE'] }],
    ['dwightStepCurrent', [-1, false, ''], { pre: [['dwightSteps', [J({ steps: [{ text: 'A', ids: [] }] })]], ['dwightStepCurrent', [0, true, 'GONE-LINE']]], excludes: ['GONE-LINE'] }],
    ['dwightComposerDraft', ['DRAFT-WORDS'], { pre: [['dwightChat', ['[]']]], postsAll: [], valueIs: ['#ci', 'DRAFT-WORDS'] }],
    // Known risk (Brief DC, PrefillComposer removed): this function does nothing when the composer is not drawn yet, so a draft sent in the same breath as a thread opening can be lost. The old helper retried 30 times; the Rhino row for Note-to-chat on a slow open is the architect's.
    ['dwightComposerDraft', ['LOST-DRAFT'], { postsAll: [], absent: '#ci' }],
    // Brief DD: Play on the footer. A click on it plays, its own mousedown is not an interruption, and while it plays any other click or key is, and the arrows stay the host's.
    ['dwightStepCurrent', [1, true, ''], { pre: [['dwightSteps', [J({ steps: [{ text: 'A', ids: [] }, { text: 'B', ids: [] }, { text: 'C', ids: [] }] })]]], click: ['#walk-play'], postsExactly: 'walk:play', absent: '#walk-play[disabled]' }],
    ['dwightStepCurrent', [0, true, ''], { pre: [['dwightSteps', [J({ steps: [{ text: 'A', ids: [] }] })]]], sel: '#walk-play[disabled]' }],
    ['dwightWalkPlaying', [true], { attr: [['#walk-play', 'aria-label', 'Pause'], ['#walk-play', 'aria-pressed', 'true'], ['#walk-play-ic', 'href', '#ic-pause']] }],
    ['dwightWalkPlaying', [false], { pre: [['dwightWalkPlaying', [true]]], attr: [['#walk-play', 'aria-label', 'Play'], ['#walk-play', 'aria-pressed', 'false'], ['#walk-play-ic', 'href', '#ic-play']] }],
    ['dwightWalkPlaying', [true], { keys: ['x'], postsExactly: 'walk:input' }],
    ['dwightWalkPlaying', [false, false], { sel: '#walk-play[disabled]' }],
    ['dwightWalkPlaying', [false], { keys: ['x'], postsNone: true }],
    ['dwightWalkLine', ['<b>LINE</b>'], { pre: [['dwightSteps', [J({ steps: [{ text: 'A', ids: [] }, { text: 'B', ids: [] }] })]], ['dwightStepCurrent', [1, false, '']]], text: [['.step.on .walktime', '<b>LINE</b>']], absent: '.walktime b' }],
    // The eleventh gate (Tharun): the line and the gone line are rows in the card's column, under its count, and the pin holds only its glyph.
    ['dwightWalkLine', ['LINE'], { pre: [['dwightSteps', [J({ steps: [{ text: 'A', ids: [] }, { text: 'B', ids: [] }] })]], ['dwightStepCurrent', [1, false, '']]], sel: '.step.on .cmpt + .walktime', absent: '.pincorner .walktime' }],
    ['dwightStepCurrent', [1, true, 'GONE'], { pre: [['dwightSteps', [J({ steps: [{ text: 'A', ids: [] }, { text: 'B', ids: [] }] })]]], sel: '.step.on .cmpt + .walkgone', absent: '.pincorner .walkgone' }],
    ['dwightWalkLine', [''], { pre: [['dwightSteps', [J({ steps: [{ text: 'A', ids: [] }] })]], ['dwightStepCurrent', [0, false, '']], ['dwightWalkLine', ['LINE']]], absent: '.walktime' }],
    ['dwightWalkTotal', ['<b>TOTAL-LINE</b>'], { pre: [['dwightSteps', [J({ steps: [{ text: 'A', ids: [] }], summary: 'A loft.' })]]], changed: true, text: [['#walktotal', '<b>TOTAL-LINE</b>']], absent: '#walktotal b' }],
    ['dwightWalkTotal', [''], { pre: [['dwightSteps', [J({ steps: [{ text: 'A', ids: [] }], summary: 'A loft.' })]], ['dwightWalkTotal', ['TOTAL-LINE']]], absent: '#walktotal' }],
    // Brief DJ: Every wire shows while a lens is live and says its state, with or without lines to draw, and goes when the lens does.
    // Brief DO: a result's one offer, on its card, one click, and a second offer replaces the first.
    ['dwightPinOffer', ['tok1', 'Pin what changed'], { pre: [['dwightOutcome', ['Done.']]], sel: '.outcome.haspin .pincorner.shown', attr: [['.pincorner', 'title', 'Pin what changed']] }],
    ['dwightPinOffer', ['tok1', 'Pin what changed'], { pre: [['dwightOutcome', ['Done.']]], click: ['.pincorner'], postsExactly: 'pin:tok1', sel: '.pincorner[disabled]' }],
    ['dwightPinOffer', ['tok2', 'Pin the cause'], { pre: [['dwightFaults', [J({ chains: [{ root: { title: 'Loft', guid: 'g', cls: 'error', cause: 'c', fix: 'f' } }] })]], ['dwightPinOffer', ['tok1', 'x']]], sel: '.fault.root.haspin .pincorner', attr: [['.pincorner', 'title', 'Pin the cause']] }],
    ['dwightPinOffer', ['tok1', 'x'], { absent: '.pincorner.shown' }],
    ['dwightLens', [J({ step: -1, lines: [], everywire: false })], { absent: '#lens-every.pressed', attr: [['#lens-every', 'style', ''], ['#lens-every', 'aria-pressed', 'false'], ['#lens-every', 'title', 'Every wire']] }],
    // That row's empty string asserts the button has no display:none. With no lens live it does, and this row must fail saying so.
    ['dwightLens', [J({ step: -1, lines: [] })], { attr: [['#lens-every', 'style', '']], mustFail: '#lens-every style reads "display:none", not ""' }],
    ['dwightLens', [J({ step: -1, lines: [], everywire: true })], { sel: '#lens-every.pressed', attr: [['#lens-every', 'aria-pressed', 'true']] }],
    ['dwightLens', [J({ step: -1, lines: [], everywire: true })], { click: ['#lens-every'], postsExactly: 'lens:all' }],
    ['dwightLens', [J({ step: -1, lines: [] })], { pre: [['dwightLens', [J({ step: -1, lines: [], everywire: true })]]], absent: '#lens-every.pressed', attr: [['#lens-every', 'style', 'display: none;']] }],
    ['dwightLens', [J({ step: 0, lines: [{ text: '38 items leave port B of Dispatch (23), and port B connects to nothing.', action: 'Select Dispatch (23)' }, { text: 'And 1 more line like these in what is lit.' }], note: 'Chips on this step only: more than 40 wires are in view. Zoom in for the rest.' })],
      { pre: [['dwightSteps', [J({ steps: [{ text: 'Loft the curves', ids: [1, 2] }], summary: 'A loft.' })]]], changed: true, sel: '#lens .lens-line', text: [['.lens-act', 'Select Dispatch (23)']] }],
    ['dwightFaults', [J({ lead: 'Two faults', stray: [], chains: [{ root: 'Loft', members: [{ text: 'null input' }] }] })],
      { changed: true }],
    ['dwightChains', [J({ lead: 'Two chains', stray: [], chains: [{ rootTitle: 'Loft', count: 2, cls: 'err' }] })],
      { changed: true }],
    ['dwightSimplifyChooser', [J([{ id: 'logic', name: 'Better logic', desc: 'Improve robustness', experimental: false, live: true }])],
      { changed: true }],
    // #883: a mode served NOT live is not offered at all. It used to render with a "soon" badge - a future
    // claim - on a card that still sent simplify:<id> when clicked. An affordance asserts its target exists
    // (#321), so an unavailable mode gets no card, and no template for "soon" remains to render.
    ['dwightSimplifyChooser', [J([
        { id: 'logic', name: 'Better logic', desc: 'Improve robustness', live: true, experimental: true },
        { id: 'later', name: 'A mode that is not live', desc: 'unavailable', live: false },
      ])],
      { changed: true, contains: 'Better logic', excludes: ['soon', 'A mode that is not live', 'simplify:later'] }],
    ['dwightSimplifyPreview', [J({
      canApply: true, title: 'Three changes',
      changes: [{ title: 'Replace with Cull', cls: 'edit', removes: [{ id: 4, type: 'Sift' }], adds: [{ type: 'Cull', nick: 'Cull' }], renames: [] }],
      advice: [{ text: 'Consider grafting.' }],
    })], { changed: true }],
    ['dwightToScriptPreview', [J({
      language: 'C#', name: 'Script', verified: true, allowReplace: true, replaces: 5, summary: 'Collapses the loft.',
      inputs: [{ name: 'crv', type: 'Curve', access: 'list' }], outputs: [{ name: 'srf', type: 'Brep', access: 'item' }],
      source: 'private void RunScript() { }', diagnosis: '',
    })], { changed: true }],
    ['dwightSimplifyOutcome', ['ok', 'Applied', 'Three changes landed.', 'Verified', J([]), ''], { changed: true }],
    ['dwightSimplifyApplied', [J({
      summary: 'Three changes', verified: true, inPlace: true, skipped: [], advice: [], draft: false,
      changes: [{ cls: 'edit', text: 'Replaced Sift with Cull', ops: [{ type: 'add', id: 7, nick: 'Cull' }] }],
    })], { changed: true }],
    ['dwightChat', [J([{ who: 'dw', html: '<p>Hello</p>' }, { who: 'me', html: 'Plan a facade' }])], { changed: true }],
    // #883: the composer carries no attachment control. A paperclip whose tooltip said "Attachments arrive with
    // Scaffold" sat inside Scaffold, which had shipped, for a feature that had not - an affordance asserting a
    // target that does not exist (#321). If attachments ship, the control comes back with them.
    ['dwightChat', [J([])], { changed: true, excludes: ['Attachments arrive', 'class="up"'] }],

    // The chat DOM is BUILT BY dwightChat - #ci, #chatstop, #thread and the composer do not exist in the
    // static markup. So every chat entry point is exercised in the order the product uses it, not against
    // a bare shell: a pass here means it works where it actually runs.
    ['dwightChatAppend', ['dw', '<p>A reply</p>'], { pre: [['dwightChat', ['[]']]], changed: true }],
    // Sixth gate: the composer takes focus once it exists, and the late call never takes it from something the user moved to.
    ['dwightFocusComposer', [false], { pre: [['dwightChat', ['[]']]], docFocused: true, sel: '#ci:focus' }],
    // Brief EC unit 5b: a chat opened from a Walk's step has the way back to that Walk, in the host's words, set as text; it survives the thread being drawn again, and goes when the Walk does.
    ['dwightChatWalkBack', ['BACK-LABEL'], { pre: [['dwightChat', ['[]']]], text: [['#chatback-lbl', 'BACK-LABEL']], absent: '#chatback[hidden]', click: ['#chatback button'], postsExactly: 'nav:walk' }],
    ['dwightChat', ['[]'], { pre: [['dwightChatWalkBack', ['KEPT-LABEL']]], text: [['#chatback-lbl', 'KEPT-LABEL']], absent: '#chatback[hidden]' }],
    ['dwightChatWalkBack', [''], { pre: [['dwightChat', ['[]']], ['dwightChatWalkBack', ['BACK-LABEL']]], sel: '#chatback[hidden]' }],
    ['dwightChatWalkBack', ['<b>x</b>'], { pre: [['dwightChat', ['[]']]], text: [['#chatback-lbl', '<b>x</b>']], absent: '#chatback b' }],
    ['dwightChat', ['[]'], { sel: '#chatback[hidden]' }],
    ['dwightFocusComposer', [false], { pre: [['dwightChat', ['[]']]], docUnfocused: true, focusArrives: true, sel: '#ci:focus' }],
    ['dwightFocusComposer', [false], { pre: [['dwightChat', ['[]']]], docFocused: true, focusFirst: '.composer .send', absent: '#ci:focus' }],
    ['dwightFocusComposer', [false], { pre: [['dwightChat', ['[]']]], docUnfocused: true, absent: '#ci:focus' }],
    ['dwightFocusComposer', [true], { pre: [['dwightChat', ['[]']]], focusFirst: '.composer .send', docUnfocused: true, sel: '#ci:focus' }],
    // Lane D (#153): the user's own two-line message must SHOW as two lines. Its markup was always right -
    // AppendUserText HtmlEncodes the text, which leaves the newline in place - and HTML then collapsed it
    // to a space, so the model read two lines while the user's own bubble showed one. Only a computed
    // style can see that. The NEGATIVE half matters as much: a model bubble is rendered Markdown, where
    // pre-wrap would turn the newlines between block elements into blank lines.
    ['dwightChatAppend', ['me', 'line one\nline two'],
      { pre: [['dwightChat', ['[]']]], changed: true, computed: ['.msg.me .bubble', 'whiteSpace', 'pre-wrap'] }],
    ['dwightChatAppend', ['dw', '<p>A reply</p>'],
      { pre: [['dwightChat', ['[]']]], changed: true, computed: ['.msg.dw .bubble', 'whiteSpace', ''] }],
    // ---- Brief BG: A CARD THAT WAITS IS OPEN; A CARD THAT WAS ANSWERED IS A RECEIPT ROW. Every state, its text in the DOM, the click.
    ['dwightChat', [J([{ who: 'dw', html: CARD_WAIT }])], { changed: true, sel: '.msg.dw .consent .consent-more', text: [['.consent-btns', 'Yes, go ahead'], ['.consent-line', 'Nothing on the canvas changes.']] }],
    ['dwightChat', [J([{ who: 'dw', html: RECEIPT('approved by you') }])], { changed: true, sel: '.msg.rc .receipt[data-state="approved by you"]', absent: '.msg.dw .receipt',
      text: [['.rc-text', 'Re-run the definition.'], ['.rc-why .rc-state', 'approved by you'], ['.rc-why', 'approved by you RCPT-REASON']], textContains: [['.rc-full', 'RCPT-AUTHORITY']],
      computed: ['.receipt .rc-full', 'display', 'none'] }],
    // the row opens on a click and the full card is shown; its spent button stays hidden
    ['dwightChat', [J([{ who: 'dw', html: RECEIPT('approved by you') }])], { changed: true, click: ['.receipt .rc-row'], sel: '.receipt.open[aria-expanded="true"]',
      computed: ['.receipt .rc-full', 'display', 'block'] }],
    ['dwightChat', [J([{ who: 'dw', html: RECEIPT('superseded') }])], { changed: true, sel: '.receipt[data-state="superseded"]' }],
    ['dwightChat', [J([{ who: 'dw', html: RECEIPT('approved automatically (Do not ask)') }])], { changed: true, sel: '.receipt[data-state="approved automatically (Do not ask)"]' }],
    // Tharun's ruling: the echo line is not rendered when a receipt with a state immediately precedes it ...
    ['dwightChat', [J([{ who: 'dw', html: RECEIPT('approved by you') }, { who: 'me', html: 'Yes, go ahead.', echo: true }])], { changed: true, sel: '.receipt', absent: '.msg.me' }],
    ['dwightChat', [J([{ who: 'dw', html: RECEIPT('approved automatically (Do not ask)') }, { who: 'me', html: 'Approved automatically.', echo: true }])], { changed: true, sel: '.receipt', absent: '.msg.me' }],
    // U111: by the host's mark, never by the words. The same words TYPED after a receipt are drawn, on a reopened thread and live.
    ['dwightChat', [J([{ who: 'dw', html: RECEIPT('reverted') }, { who: 'me', html: 'Yes, go ahead.' }])], { changed: true, sel: '.msg.me', text: [['.msg.me', 'Yes, go ahead.']] }],
    ['dwightChatAppend', ['me', 'Yes, go ahead.'], { pre: [['dwightChat', [J([{ who: 'dw', html: RECEIPT('reverted') }])]]], changed: true, sel: '.msg.me', text: [['.msg.me', 'Yes, go ahead.']] }],
    // ... and only then: the same line after a waiting card, and the user's own words after a receipt, are rendered (the controls).
    ['dwightChat', [J([{ who: 'dw', html: CARD_WAIT }, { who: 'me', html: 'Yes, go ahead.', echo: true }])], { changed: true, sel: '.msg.me', text: [['.msg.me', 'Yes, go ahead.']] }],
    ['dwightChat', [J([{ who: 'dw', html: RECEIPT('approved by you') }, { who: 'me', html: 'Now make it bigger.' }])], { changed: true, sel: '.msg.me', text: [['.msg.me', 'Now make it bigger.']] }],
    // the live path: the host swaps the answered card for its receipt in place, and an echo appended after a receipt is not drawn
    ['dwightCardAnswered', ['tok-a', RECEIPT('approved by you')], { pre: [['dwightChat', [J([{ who: 'dw', html: CARD_WAIT }])]]], changed: true,
      sel: '.msg.rc .receipt[data-state="approved by you"]', absent: '.msg.dw .consent-btns' }],
    ['dwightChatAppend', ['me', 'Yes, go ahead.', true], { pre: [['dwightChat', [J([{ who: 'dw', html: RECEIPT('approved by you') }])]]], absent: '.msg.me' }],
    ['dwightChatAppend', ['me', 'Yes, go ahead.', true], { pre: [['dwightChat', [J([{ who: 'dw', html: CARD_WAIT }])]]], changed: true, sel: '.msg.me[data-echo]' }],
    // the swap takes the marked echo with it, and leaves a typed line that follows the card
    ['dwightCardAnswered', ['tok-a', RECEIPT('approved by you')], { pre: [['dwightChat', [J([{ who: 'dw', html: CARD_WAIT }, { who: 'me', html: 'Yes, go ahead.', echo: true }])]]], changed: true, sel: '.msg.rc', absent: '.msg.me' }],
    ['dwightCardAnswered', ['tok-a', RECEIPT('approved by you')], { pre: [['dwightChat', [J([{ who: 'dw', html: CARD_WAIT }, { who: 'me', html: 'Yes, go ahead.' }])]]], changed: true, sel: '.msg.rc + .msg.me' }],
    ['dwightSetTheme', ['dark'], { pre: [['dwightChat', [J([{ who: 'dw', html: CARD_WAIT }, { who: 'dw', html: RECEIPT('approved by you') }])]]], sel: '.receipt', text: [['.consent-line', 'Nothing on the canvas changes.']] }],
    // ---- Brief AO: THE CHAT'S HEADER, in each state. The shell draws what the host sends and posts every change back through the host.
    ['dwightChatHeader', [HDR()], { pre: [['dwightChat', ['[]']]], changed: true, sel: '.composer #chathdr.on',
      text: [['#hdr-cost', '~23.5\u00A2 \u00B7 3 turns']], attr: [['#hdr-model', 'title', 'TIP-MODEL'], ['#hdr-model', 'aria-label', 'TIP-MODEL'], ['#hdr-approval', 'title', 'TIP-APPROVAL'], ['#hdr-full', 'title', 'TIP-FULL-OFF'], ['#hdr-cost', 'title', 'TIP-RECEIPT']] }],
    ['dwightChatHeader', [HDR({ full: { on: true, text: 'Full control on', tip: 'TIP-FULL-ON', title: 'sentence' } })], { pre: [['dwightChat', ['[]']]], changed: true, sel: '#hdr-full.on', attr: [['#hdr-full', 'title', 'TIP-FULL-ON']] }],
    ['dwightChatHeader', [HDR()], { pre: [['dwightChat', ['[]']]], changed: true, absent: '#hdr-full.on', attr: [['#hdr-full', 'title', 'TIP-FULL-OFF']] }],
    // Brief BI: the three are icons with no text at all; the left of the line is the cost and nothing else
    ['dwightChatHeader', [HDR()], { pre: [['dwightChat', ['[]']]], changed: true, sel: '#chathdr .linecost + .icons #hdr-model.iconbtn', excludes: ['Balanced \u00B7 Sonnet 5</span>', 'Ask every time</span>', 'Full control off</span>'] }],
    ['dwightChatHeader', [HDR({ approval: { text: 'Do not ask', title: 'T', mode: 'never', choices: HDR_MODES }, cost: { text: 'not priced \u00B7 3 turns', title: '' } })], { pre: [['dwightChat', ['[]']]], changed: true, text: [['#hdr-cost', 'not priced \u00B7 3 turns']], absent: '#hdr-approval circle' }],
    ['dwightChatHeader', [HDR({ cost: { text: 'no turns yet', title: '' } })], { pre: [['dwightChat', ['[]']]], changed: true, text: [['#hdr-cost', 'no turns yet']], attr: [['#hdr-cost', 'title', 'no turns yet']] }],
    // the shield says the mode: a tick when everything asks, a dot for scripts and deletions, empty when nothing does
    ['dwightChatHeader', [HDR()], { pre: [['dwightChat', ['[]']]], changed: true, sel: '#hdr-approval use[href="#ic-ask"]' }],
    ['dwightChatHeader', [HDR({ approval: { text: 'Scripts and deletions', title: 'T', mode: 'scripts_and_deletions', choices: HDR_MODES } })], { pre: [['dwightChat', ['[]']]], changed: true, sel: '#hdr-approval use[href="#ic-scripts_and_deletions"]' }],
    // the header is text, never markup: a tooltip or a cost with angle brackets stays text
    ['dwightChatHeader', [HDR({ cost: { text: '<b>x</b>', title: '<b>y</b>' } })], { pre: [['dwightChat', ['[]']]], changed: true, text: [['#hdr-cost', '<b>x</b>']], absent: '#chathdr b' }],
    // a click on the cost goes to the host
    ['dwightChatHeader', [HDR()], { pre: [['dwightChat', ['[]']]], click: ['#hdr-cost'], postsExactly: 'hdr:usage' }],
    // the footer is gone in a chat: a status there is the turn's live step, on the line in place of the cost, and clearing it puts the cost back
    ['dwightSetStatus', ['STEP-LINE', false], { pre: [['dwightChat', ['[]']], ['dwightChatHeader', [HDR()]]], sel: '#hdr-cost.live', text: [['#hdr-cost', 'STEP-LINE']], excludes: ['"status">STEP-LINE'] }],
    ['dwightSetStatus', ['', false], { pre: [['dwightChat', ['[]']], ['dwightChatHeader', [HDR()]], ['dwightSetStatus', ['STEP-LINE', false]]], absent: '#hdr-cost.live', text: [['#hdr-cost', '~23.5\u00A2 \u00B7 3 turns']] }],
    // dark, too: the same line after the theme switch
    ['dwightSetTheme', ['dark'], { pre: [['dwightChat', ['[]']], ['dwightChatHeader', [HDR()]]], sel: '.composer #chathdr.on', text: [['#hdr-cost', '~23.5\u00A2 \u00B7 3 turns']] }],
    // the menus: postures first, then the models that run the chat; picking posts through the host and the shell keeps nothing
    ['dwightHdrMenu', ['model'], { pre: [['dwightChat', ['[]']], ['dwightChatHeader', [HDR()]]], sel: '#hdr-menu .it.on', textContains: [['#hdr-menu', 'Profile'], ['#hdr-menu', 'Economy'], ['#hdr-menu', 'Ultra'], ['#hdr-menu', 'Models that run the chat'], ['#hdr-menu', 'Sonnet 5']] }],
    // Brief CW: a posture row and a model row each carry the host's price as the hover, and a row with none has no title.
    ['dwightHdrMenu', ['model'], { pre: [['dwightChat', ['[]']], ['dwightChatHeader', [HDR({ model: { text: 'T', title: 'T', choices: HDR_MODELS.map((c) => (c.id === 'posture:quality' ? Object.assign({ title: 'PRICE-POSTURE' }, c) : c.id === 'claude-opus-5' ? Object.assign({ title: 'PRICE-MODEL' }, c) : c)) } })]]],
      attr: [['#hdr-menu .it:nth-of-type(3)', 'title', 'PRICE-POSTURE'], ['#hdr-menu .it:nth-of-type(6)', 'title', 'PRICE-MODEL']], absent: '#hdr-menu .it:nth-of-type(2)[title]' }],
    ['dwightHdrMenu', ['approval'], { pre: [['dwightChat', ['[]']], ['dwightChatHeader', [HDR()]]], textContains: [['#hdr-menu', 'Ask every time'], ['#hdr-menu', 'Scripts and deletions'], ['#hdr-menu', 'Do not ask']] }],
    ['dwightHdrMenu', ['model'], { pre: [['dwightChat', ['[]']], ['dwightChatHeader', [HDR()]]], after: [['dwightHdrMenu', ['model']]], absent: '#hdr-menu' }],
    ['dwightHdrMenu', ['model'], { pre: [['dwightChat', ['[]']], ['dwightChatHeader', [HDR()]]], click: ['#hdr-menu .it:nth-of-type(5)'], postsExactly: 'hdr:model:claude-sonnet-5' }],
    ['dwightHdrMenu', ['approval'], { pre: [['dwightChat', ['[]']], ['dwightChatHeader', [HDR()]]], click: ['#hdr-menu .it:nth-of-type(3)'], postsExactly: 'hdr:approval:never' }],
    ['dwightChatHeader', [HDR()], { pre: [['dwightChat', ['[]']]], click: ['#hdr-full'], postsExactly: 'hdr:full:on' }],
    ['dwightChatHeader', [HDR({ full: { on: true, text: 'Full control on', tip: 's', title: 's' } })], { pre: [['dwightChat', ['[]']]], click: ['#hdr-full'], postsExactly: 'hdr:full:off' }],
    // Brief BD: the controls sit in the composer, under the text box. The host's header may arrive BEFORE the composer exists, so it is kept and
    // drawn when dwightChat builds the row; and the row is inside the composer's frame, after the send button.
    ['dwightChat', ['[]'], { pre: [['dwightChatHeader', [HDR()]]], changed: true, sel: '.composer #chathdr.on #hdr-model', text: [['#hdr-cost', '~23.5\u00A2 \u00B7 3 turns']] }],
    ['dwightChatHeader', [HDR()], { pre: [['dwightChat', ['[]']]], changed: true, absent: '#bar #chathdr' }],
    // each chip carries a glyph and no image file; the cost has no glyph and its text stays in the row
    ['dwightChatHeader', [HDR()], { pre: [['dwightChat', ['[]']]], changed: true, sel: '#hdr-model svg.ic', absent: '#chathdr img' }],
    // Settings says these are under the chat's text box (ChatHeader.SettingsHint); its words are pinned by a test, and this holds the place it names.
    ['dwightChatHeader', [HDR()], { pre: [['dwightChat', ['[]']]], sel: '.composer #chathdr #hdr-model' }],
    ['dwightChatHeader', [HDR()], { pre: [['dwightChat', ['[]']]], sel: '.composer #chathdr #hdr-approval' }],
    ['dwightChatHeader', [HDR()], { pre: [['dwightChat', ['[]']]], sel: '.composer #chathdr #hdr-full' }],
    ['dwightChatHeader', [HDR()], { pre: [['dwightChat', ['[]']]], changed: true, sel: '#hdr-approval svg.ic' }],
    ['dwightChatHeader', [HDR()], { pre: [['dwightChat', ['[]']]], changed: true, sel: '#hdr-full svg.ic' }],
    ['dwightChatHeader', [HDR()], { pre: [['dwightChat', ['[]']]], changed: true, sel: '#hdr-cost', absent: '#hdr-cost svg' }],
    // the controls are text and glyphs, in tab order after the text box (the composer's own controls come first)
    ['dwightChatHeader', [HDR()], { pre: [['dwightChat', ['[]']]], changed: true, computed: ['#chathdr', 'order', '9'] }],
    // a header that cannot be read draws nothing and throws nothing
    ['dwightChatHeader', ['{not json'], { changed: false }],
    // ... and a REOPENED thread renders its persisted messages through dwightChat, not dwightChatAppend.
    ['dwightChat', [J([{ who: 'me', html: 'line one\nline two' }])],
      { changed: true, computed: ['.msg.me .bubble', 'whiteSpace', 'pre-wrap'] }],

    // Lane D: THE COMPOSER. Two lines typed must be two lines POSTED - exactly, which is why these use
    // postsExactly: the old single-line composer, handed the same two lines, posted "chat:line oneline two"
    // and `posts: true` scored that a pass.
    ['dwightComposerSend', [],
      { pre: [['dwightChat', ['[]']]], set: [['#ci', 'value', 'line one\nline two']],
        postsExactly: 'chat:line one\nline two', valueIs: ['#ci', ''] }],
    // The outside is trimmed, the inside is kept - and what is trimmed is trimmed from BOTH the bubble and
    // the model, since both are built from the one string posted here.
    ['dwightComposerSend', [],
      { pre: [['dwightChat', ['[]']]], set: [['#ci', 'value', '\n\nline one\n  line two\n\n']],
        postsExactly: 'chat:line one\n  line two' }],
    ['dwightComposerSend', [],
      { pre: [['dwightChat', ['[]']]], set: [['#ci', 'value', ' \n \n ']], postsNothing: true }],
    // A PASTED INDENTED BLOCK keeps EVERY line's indent - the first line's included. A whole-string trim()
    // took the first line's indent and left the rest, so the RELATIVE indentation changed; in Python the
    // relative indentation IS the program. Shown still equalled sent, so #153 held between bubble and
    // model - what broke was TYPED versus sent. (Found at review of this PR.)
    ['dwightComposerSend', [],
      { pre: [['dwightChat', ['[]']]], set: [['#ci', 'value', '\n    def area(r):\n        return 3.14 * r * r\n']],
        postsExactly: 'chat:    def area(r):\n        return 3.14 * r * r' }],
    // ... and a first line that is only whitespace is a blank line, not an indent: it goes.
    ['dwightComposerSend', [],
      { pre: [['dwightChat', ['[]']]], set: [['#ci', 'value', '   \n    indented\n']],
        postsExactly: 'chat:    indented' }],
    // Enter sends ...
    ['dwightComposerKey', [{ key: 'Enter', shiftKey: false, isComposing: false, preventDefault() {} }],
      { pre: [['dwightChat', ['[]']]], set: [['#ci', 'value', 'line one\nline two']],
        postsExactly: 'chat:line one\nline two' }],
    // ... Shift+Enter breaks the line: nothing is sent and nothing is cleared ...
    ['dwightComposerKey', [{ key: 'Enter', shiftKey: true, isComposing: false, preventDefault() {} }],
      { pre: [['dwightChat', ['[]']]], set: [['#ci', 'value', 'line one']],
        postsNothing: true, valueIs: ['#ci', 'line one'] }],
    // ... and an Enter that COMMITS an IME candidate is not the end of a message.
    ['dwightComposerKey', [{ key: 'Enter', shiftKey: false, isComposing: true, preventDefault() {} }],
      { pre: [['dwightChat', ['[]']]], set: [['#ci', 'value', 'half-typed']],
        postsNothing: true, valueIs: ['#ci', 'half-typed'] }],
    ['dwightComposerKey', [{ key: 'a', shiftKey: false, isComposing: false, preventDefault() {} }],
      { pre: [['dwightChat', ['[]']]], set: [['#ci', 'value', 'abc']], postsNothing: true }],
    // The grow step. jsdom has NO LAYOUT - scrollHeight is always 0 - so this proves only that the function
    // runs and writes a height. Whether the box really grows to about six lines and then scrolls is a
    // runtime check, and it is on the gate list rather than claimed here.
    ['dwightComposerGrow', [], { pre: [['dwightChat', ['[]']]], changed: true }],

    ['dwightChatFile', ['me', 'facade.gh', '2.1 MB', 'gh'], { pre: [['dwightChat', ['[]']]], changed: true }],
    // ADR-088's second wall: a click on an anchor that got past the render goes nowhere and runs nothing. postsNothing is the stopPropagation: the anchor's own onclick would post.
    ['dwightChatAppend', ['dw', ANCHOR('javascript:void(0)')], { pre: [['dwightChat', ['[]']]], events: [['#p', 'click', {}]], prevented: true, postsNothing: true }],
    ['dwightChatAppend', ['dw', ANCHOR('JAVASCRIPT:void(0)')], { pre: [['dwightChat', ['[]']]], events: [['#p', 'click', {}]], prevented: true, postsNothing: true }],
    ['dwightChatAppend', ['dw', ANCHOR("javascript:void(document.documentElement.setAttribute(%27data-dwight-probe%27,%27ran%27))")], { pre: [['dwightChat', ['[]']]], events: [['#p', 'click', {}]], prevented: true, postsNothing: true }],
    ['dwightChatAppend', ['dw', ANCHOR('%6Aavascript:void(0)')], { pre: [['dwightChat', ['[]']]], events: [['#p', 'click', {}]], prevented: true, postsNothing: true }],
    ['dwightChatAppend', ['dw', ANCHOR('&#1;java&#9;script:void(0)')], { pre: [['dwightChat', ['[]']]], events: [['#p', 'click', {}]], prevented: true, postsNothing: true }],
    ['dwightChatAppend', ['dw', ANCHOR('data:text/html,x')], { pre: [['dwightChat', ['[]']]], events: [['#p', 'click', {}]], prevented: true, postsNothing: true }],
    ['dwightChatAppend', ['dw', ANCHOR('vbscript:x')], { pre: [['dwightChat', ['[]']]], events: [['#p', 'click', {}]], prevented: true, postsNothing: true }],
    ['dwightChatAppend', ['dw', ANCHOR('file:///C:/x')], { pre: [['dwightChat', ['[]']]], events: [['#p', 'click', {}]], prevented: true, postsNothing: true }],
    ['dwightChatAppend', ['dw', ANCHOR('foo/bar')], { pre: [['dwightChat', ['[]']]], events: [['#p', 'click', {}]], prevented: true, postsNothing: true }],
    ['dwightChatAppend', ['dw', ANCHOR('#')], { pre: [['dwightChat', ['[]']]], events: [['#p', 'click', {}]], prevented: true, postsNothing: true }],   // # with no class is no anchor of ours
    ['dwightChatAppend', ['dw', ANCHOR('%68ttps://example.com')], { pre: [['dwightChat', ['[]']]], events: [['#p', 'click', {}]], prevented: true, postsNothing: true }],   // to a browser this is a relative target, and decoding it would wave it through
    ['dwightChatAppend', ['dw', ANCHOR('&#9;https://example.com')], { pre: [['dwightChat', ['[]']]], events: [['#p', 'click', {}]], prevented: true, postsNothing: true }],   // anything in front of the scheme fails, and the render leaves none on a kept target
    ['dwightChatAppend', ['dw', ANCHOR('javascript:%ZZ')], { pre: [['dwightChat', ['[]']]], events: [['#p', 'click', {}]], prevented: true, postsNothing: true }],   // one that will not decode
    ['dwightChatAppend', ['dw', '<a id="p" href="javascript:void(0)" onclick="dwightSend(\'leak\')"><b id="inner">x</b></a>'], { pre: [['dwightChat', ['[]']]], events: [['#inner', 'click', {}]], prevented: true, postsNothing: true }],   // a click on what is inside the link
    ['dwightChatAppend', ['dw', ANCHOR('javascript:void(0)')], { pre: [['dwightChat', ['[]']]], events: [['#p', 'auxclick', {}]], prevented: true, postsNothing: true }],   // a middle click
    // What it must let through: a web link, whatever its case, and the plugin's own anchor, whose click is its onclick.
    ['dwightChatAppend', ['dw', ANCHOR('https://example.com/a')], { pre: [['dwightChat', ['[]']]], events: [['#p', 'click', {}]], prevented: false, postsExactly: 'leak' }],
    ['dwightChatAppend', ['dw', ANCHOR('HTTP://example.com')], { pre: [['dwightChat', ['[]']]], events: [['#p', 'click', {}]], prevented: false, postsExactly: 'leak' }],
    ['dwightChatAppend', ['dw', '<a id="p" class="anch" href="#" onclick="dwightSend(\'anchor:5\');return false;">(5)</a>'], { pre: [['dwightChat', ['[]']]], events: [['#p', 'click', {}]], postsExactly: 'anchor:5' }],
    // A step's sentence and a detailed answer are drawn into the same page, and the wall stands ahead of them too.
    ['dwightSteps', [J({ steps: [{ text: ANCHOR('javascript:void(0)'), ids: [] }], summary: 'A loft.' })], { events: [['#p', 'click', {}]], prevented: true, postsNothing: true }],
    ['dwightSetBody', [ANCHOR('javascript:void(0)')], { events: [['#p', 'click', {}]], prevented: true, postsNothing: true }],
    // Brief DX: the image waiting to go, its thumbnail in the thread, and the words where a file is gone.
    ['dwightChatHeld', [HELD()], { pre: [['dwightChat', ['[]']]], changed: true, sel: '#chatheld.on img', textContains: [['#chatheld', '+1 image, about 2,100 tokens']] }],
    ['dwightChatHeld', [''], { pre: [['dwightChat', ['[]']], ['dwightChatHeld', [HELD()]]], changed: true, text: [['#chatheld', '']] }],
    // An image with no words is a turn, and the chip stays until the host accepts it: a refused send keeps it.
    ['dwightComposerSend', [], { pre: [['dwightChat', ['[]']], ['dwightChatHeld', [HELD()]]], postsExactly: 'chat:', sel: '#chatheld.on img' }],
    // A redraw of the composer does not lose it.
    ['dwightChat', ['[]'], { pre: [['dwightChat', ['[]']], ['dwightChatHeld', [HELD()]]], sel: '#chatheld.on img' }],
    ['dwightChatAttach', [], { pre: [['dwightChat', ['[]']]], postsExactly: 'chatimg:pick' }],
    ['dwightChatHeld', [HELD()], { pre: [['dwightChat', ['[]']]], click: ['#chatheld-x'], postsExactly: 'chatimg:clear' }],
    // A reopened thread and a live message ask for the bytes the same way, once per image.
    ['dwightChat', [IMG_THREAD()], { sel: '.imgmsg[data-img="abc"]', postsExactly: 'chatimg:want:abc' }],
    ['dwightChatAppend', ['me', 'look', false, J({ sha: 'abc', meta: '1568 x 1000 px' })], { pre: [['dwightChat', ['[]']]], sel: '.imgmsg[data-img="abc"]', postsExactly: 'chatimg:want:abc' }],
    ['dwightChatImageBytes', ['abc', 'data:image/png;base64,iVBORw0KGgo=', ''], { pre: [['dwightChat', [IMG_THREAD()]]], changed: true, sel: '.imgmsg[data-img="abc"] img' }],
    ['dwightChatImageBytes', ['abc', '', 'image not found'], { pre: [['dwightChat', [IMG_THREAD()]]], changed: true, sel: '.imgmsg.gone', text: [['.imgmsg.gone .imeta', 'image not found']] }],
    // Paste and drop live in listeners. The over-large and the wrong type answer without reading a byte; words on the clipboard win; a drop off the composer does nothing.
    ['dwightChat', ['[]'], { events: [['#ci', 'paste', { clipboardData: { items: [{ type: 'image/png', kind: 'file', getAsFile: () => ({ size: 20971521, type: 'image/png' }) }] } }]], postsExactly: 'chatimg:big' }],
    ['dwightChat', ['[]'], { events: [['#ci', 'paste', { clipboardData: { items: [{ type: 'image/gif', kind: 'file', getAsFile: () => ({ size: 10, type: 'image/gif' }) }] } }]], postsExactly: 'chatimg:type' }],
    ['dwightChat', ['[]'], { events: [['#ci', 'paste', { clipboardData: { items: [{ type: 'text/plain', kind: 'string' }, { type: 'image/png', kind: 'file', getAsFile: () => ({ size: 20971521, type: 'image/png' }) }] } }]], postsNothing: true }],
    ['dwightChat', ['[]'], { events: [['.composer', 'drop', { dataTransfer: { types: ['Files'], files: [{ size: 20971521, type: 'image/png' }] } }]], postsExactly: 'chatimg:big' }],
    ['dwightChat', ['[]'], { events: [['#thread', 'drop', { dataTransfer: { types: ['Files'], files: [{ size: 20971521, type: 'image/png' }] } }]], postsNothing: true }],
    ['dwightChatPlan', [J({ groups: [{ key: 'A1', name: 'Inputs', lead: 'Sliders' }] })],
      { pre: [['dwightChat', ['[]']]], changed: true }],
    ['dwightChatBusy', [true], { pre: [['dwightChat', ['[]']]], changed: true }],
    ['dwightChatDrop', [true], { pre: [['dwightChat', ['[]']]], changed: true }],
    ['dwightChatUpload', [42], { pre: [['dwightChat', ['[]']]], changed: true }],
    ['dwightSetStatus', ['Working', false], { changed: true }],
    ['dwightSetTheme', ['dark'], { changed: true }],
    ['dwightStop', [], { posts: true }],
    ['dwightSend', ['explain'], { posts: true }],
    ['dwightDecorateCode', [], { throwsOnly: true }],
  ],
  'dock-shell.html': [
    ['dwightDockList', [J([{ action: 'Explain', icon: 'e', subtitle: '15 components', meta: '2m ago', state: 'ok', from: '' }])],
      { changed: true, text: [['.rec .ra', 'Explain'], ['.rec .rs', '15 components'], ['.rec .rw', '2m ago']] }],
    // Section 9: a chat's row is its name and its time, read from the flag and never guessed from the verb; a state sits beside the title.
    // Brief DG, with Brief BQ section 9 over it: the list draws no cost on any row, a stopped or failed one included. A cost the host were ever to send is not drawn.
    ['dwightDockList', [J([{ action: 'Explain', icon: 'e', subtitle: '1,087 components', meta: '9m ago', state: 'stopped', cost: 'COST-WORD', from: '' }])],
      { text: [['#list .rstate', 'stopped']], notText: [['#list', 'COST-WORD']] }],
    ['dwightDockList', [J([{ action: 'Diagnose', icon: 'd', subtitle: '3 faults', meta: '38m ago', state: 'failed', cost: 'COST-WORD', from: '' }])],
      { text: [['#list .rstate', 'failed']], notText: [['#list', 'COST-WORD']] }],
    ['dwightDockList', [J([{ action: 'Build', icon: 'b', chat: true, subtitle: 'CHAT-NAME', meta: '4m ago', state: 'stopped', from: '' }])],
      { text: [['.rec .ra', 'CHAT-NAME'], ['.rec .rw', '4m ago'], ['.rec .r1 .rstate', 'stopped']], absent: '.rec .rs', excludes: ['Build'] }],
    ['dwightDockList', [J([{ action: 'Build', icon: 'b', subtitle: 'NOT-A-CHAT', meta: '4m ago', state: 'ok', from: '' }])],
      { text: [['.rec .ra', 'Build'], ['.rec .rs', 'NOT-A-CHAT']] }],
    ['dwightDockFile', ['facade.gh'], { changed: true }],
    // Brief DI: the dock's row. The host says the state, and a click flips it and tells the host.
    ['dwightDockNotes', [false], { attr: [['#dnotes', 'aria-checked', 'false']] }],
    ['dwightDockNotes', [true], { pre: [['dwightDockNotes', [false]]], attr: [['#dnotes', 'aria-checked', 'true']] }],
    ['dwightDockNotes', [true], { click: ['#dnotes'], postsExactly: 'notes:off', attr: [['#dnotes', 'aria-checked', 'false']] }],
    ['dwightDockNotes', [false], { click: ['#dnotes'], postsExactly: 'notes:on', attr: [['#dnotes', 'aria-checked', 'true']] }],
    ['dwightDockExpanded', [true], { changed: true }],
    ['dwightSetTheme', ['dark'], { changed: true }],
    ['dwightSend', ['open'], { posts: true }],
    // Brief BQ: the Timeline. Every word arrives from HistoryCore, so sentinels prove the shell places what it is handed: closed rows
    // are one line each with their state as the mark, a row opens in place, one button expands all and collapses all, who approved
    // it comes before the steps with the actions as icons carrying their pinned words, a step's approval only where it differs, an
    // action only where the plugin offers one and only with a well-formed record id, the question above the tinted rows, the result note, and the empty lines.
    ['dwightDockHistory', [HIST({})], { changed: true, containsAll: ['TITLE-A', 'COUNT-A', 'TIME-A', 'YOURS-LINE', 'HERE-LINE', 'Reorganise'],
      sel: '#timeline .ev.standing', absent: '#timeline .ev-rest', excludes: ['STEP-A', 'BTN-BEFORE'] }],
    ['dockToggleRow', ['KEY-A'], { pre: [['dwightDockHistory', [HIST({})]]], changed: true,
      containsAll: ['BY-A', 'BTN-BEFORE', 'OPEN-CHAT', 'STEP-A', 'BY-STEP', 'BTN-STEP', 'YOURS-STEP'], sel: '#timeline .ev.open .ev-head[aria-expanded="true"]', excludes: ['NEVER', 'STEP-B'], absent: '#timeline .tag' }],
    ['dockAll', [], { pre: [['dwightDockHistory', [HIST({})]]], changed: true, text: [['#allword', 'COLLAPSE-W']], containsAll: ['STEP-A', 'STEP-B', 'REVERTED-B'], excludes: ['NEVER'] }],
    ['dockAll', [], { pre: [['dwightDockHistory', [HIST({})]], ['dockAll', []]], text: [['#allword', 'EXPAND-W']], absent: '#timeline .ev.open' }],
    ['dockToggleRow', ['KEY-A'], { pre: [['dwightDockHistory', [HIST({})]]], text: [['#allword', 'COLLAPSE-W']] }],
    ['dwightDockHistory', [HIST({ confirm: 'CONFIRM-LINE', confirmIds: ['11111111-1111-1111-1111-111111111111', '44444444-4444-4444-4444-444444444444'] })],
      { containsAll: ['CONFIRM-LINE', 'YES-BTN', 'NO-BTN'], sel: '#timeline .ev.inwalk', text: [['#timeline .yo.inwalk', 'YOURS-LINE'], ['#timeline .hctext', 'CONFIRM-LINE']] }],
    ['dwightDockHistory', [HIST({ note: 'NOTE-LINE' })], { text: [['#timeline .hnote', 'NOTE-LINE']] }],
    ['dwightDockHistory', [HIST({ items: [] })], { text: [['#timeline .dock-empty', 'EMPTY-LINE']], sel: '#allbtn[hidden]' }],
    ['dwightDockList', ['[]'], { pre: [['dwightDockHistory', [HIST({})]]], text: [['#list .dock-empty', 'LIST-EMPTY']] }],
    ['dockView', ['timeline'], { pre: [['dwightDockHistory', [HIST({})]]], sel: 'body.timeline #navbtn[title="BACK-W"]', changed: true, text: [['#action', 'HEAD-W'], ['#allword', 'EXPAND-W']], absent: '#allbtn[hidden]' }],
    // The question freezes the rows, so the button offers nothing while it is up: one the walk cuts through is forced open, and "Expand all" beside an open row is false.
    ['dockView', ['timeline'], { pre: [['dwightDockHistory', [HIST({ confirm: 'CONFIRM-LINE' })]]], sel: '#allbtn[hidden]' }],
    ['dwightDockHistory', [HIST({})], { text: [['#action', 'LIST-HEAD'], ['#newword', 'NEW-W']], absent: '#newbtn[hidden]' }],
    ['dwightDockHome', [], { pre: [['dwightDockHistory', [HIST({})]], ['dockView', ['timeline']]], absent: 'body.timeline', text: [['#action', 'LIST-HEAD']], sel: '#newbtn:not([hidden])' }],
    ['dwightChipState', ['waiting'], { sel: 'html[data-chip="waiting"]' }],
    ['dwightChipState', [''], { pre: [['dwightChipState', ['waiting']]], absent: 'html[data-chip="waiting"]' }],
    // A row that is working or waiting says so in the time's place, in the plugin's word.
    ['dwightDockList', [J([{ action: 'Build', icon: 'b', chat: true, subtitle: 'CHAT-NAME', meta: '4m ago', state: 'ok', live: 'waiting', liveWord: 'LIVE-WORD', from: '' }])],
      { text: [['.rec .rlive', 'LIVE-WORD']], absent: '.rec .rw' }],
    ['dockView', ['list'], { pre: [['dwightDockHistory', [HIST({ confirm: 'CONFIRM-LINE' })]], ['dockView', ['timeline']]], absent: 'body.timeline', posts: true }],
    // The tour's last button points at this window (DwightDock.DrawAttention -> dwightDockAttention). Three things
    // have to be true and each has its own fixture: the ring APPEARS; something is scheduled to take it away, so it
    // STOPS by itself (fakeTimers records the timer without waiting 3.6 s); and running that timer really removes it.
    ['dwightDockAttention', [], { fakeTimers: true, sel: '.dock.attn', changed: true, timerScheduled: true }],
    ['dwightDockAttention', [], { fakeTimers: true, flushTimers: true, absent: '.dock.attn' }],
    // Brief BR: the chip's state is the host's. The look reads this attribute; a state cleared leaves it empty.
    ['dwightChipState', ['waiting'], { sel: 'html[data-chip="waiting"]' }],
    ['dwightChipState', [''], { pre: [['dwightChipState', ['done']]], sel: 'html[data-chip=""]' }],
    ['dwightDockAttentionStop', [], { fakeTimers: true, pre: [['dwightDockAttention', []]], changed: true, absent: '.dock.attn' }],
    // Brief ED: the Notes pane. Every word arrives from NotesPane, so sentinels prove the shell places what it is handed: a row's words, time and line; the pane's
    // heading in the bar; New note only in the pane; the count on the icon; a row posting where to go and New note its own door; the empty line; words as text.
    ['dwightDockNoteList', [NPANE({})], { pre: [['dockView', ['notes']]], text: [['#noterows .rec .ra', 'WORDS-A'], ['#noterows .rec .rw', 'WHEN-A'], ['#noterows .rec .rs', 'WHO-A \u00b7 ABOUT-A \u00b7 STATE-A'], ['#action', 'NOTES-HEAD'], ['#noteword', 'NEW-NOTE']], absent: '#notenew[hidden]' }],
    ['dwightDockNoteList', [NPANE({})], { pre: [['dockView', ['notes']]], text: [['#noterows .rec:nth-of-type(2) .rs', 'WHO-B']], excludes: ['null'] }],
    ['dwightDockNoteList', [NPANE({})], { pre: [['dockView', ['notes']]], click: ['#noterows .rec'], postsExactly: 'note:go:abc123' }],
    ['dwightDockNoteList', [NPANE({})], { pre: [['dockView', ['notes']]], click: ['#notenew'], postsExactly: 'note:new' }],
    ['dwightDockNoteList', [NPANE({})], { pre: [['dwightDockHistory', [HIST({})]], ['dockView', ['notes']]], sel: '#newbtn[hidden]', absent: '#notenew[hidden]' }],   // New chat is History's, New note the pane's
    ['dwightDockNoteList', [NPANE({ rows: [], count: 0 })], { pre: [['dockView', ['notes']]], text: [['#noterows .dock-empty', 'EMPTY-NOTES']], sel: '#nbadge[hidden]' }],
    ['dwightDockNoteList', [NPANE({})], { text: [['#nbadge', '2']], absent: '#nbadge[hidden]', attr: [['#nbtn', 'aria-label', 'NOTES-HEAD, 2'], ['#nbtn', 'title', 'NOTES-HEAD']], sel: '#notenew[hidden]' }],
    ['dwightDockNoteList', [NPANE({ rows: [{ id: 'x', words: '<b>w</b>', who: 'you', when: 'now', about: null, state: 'fresh', stateWord: 'fresh' }] })], { text: [['#noterows .ra', '<b>w</b>']], absent: '#noterows b' }],
    ['dwightDockNoteList', ['not json'], { pre: [['dwightDockNoteList', [NPANE({})]]], text: [['#noterows .rec .ra', 'WORDS-A']] }],
    // Open, the pane's own icon goes and the back arrow comes, as the Timeline's do.
    ['dockView', ['notes'], { pre: [['dwightDockExpanded', [true]]], sel: 'body.notes', postsExactly: 'note:list', absent: 'body.timeline', computed: ['#nbtn', 'display', 'none'] }],
    ['dockView', ['notes'], { pre: [['dwightDockExpanded', [true]]], computed: ['#navbtn', 'display', 'inline-grid'] }],
    ['dockView', ['list'], { pre: [['dwightDockExpanded', [true]], ['dockView', ['notes']]], computed: ['#navbtn', 'display', 'none'] }],
    ['dockView', ['timeline'], { pre: [['dockView', ['notes']]], sel: 'body.timeline', absent: 'body.notes' }],
    ['dwightDockHome', [], { pre: [['dockView', ['notes']]], absent: 'body.notes' }],
    // The switch is at the pane's head: the same setting, the same command.
    ['dwightDockNotes', [true], { pre: [['dockView', ['notes']]], click: ['#notes #dnotes'], postsExactly: 'notes:off' }],
  ],
  'settings.html': [
    ['dwightSettingsInit', [J({
      providers: [{ id: 'anthropic', name: 'Anthropic', isLocal: false }], provider: 'anthropic',
      postures: [{ id: 'balanced', name: 'Balanced' }], posture: 'balanced',
      models: [], overrides: [], theme: 'dark', wires: 'faint', arrangement: 'compact',
      hasKey: true, needsKey: true,
    })], { changed: true }],
    // The "Fully local" band is the host's fact: there only when the provider is local, whatever else the page holds.
    ['dwightSettingsInit', [J({ providers: [], postures: [], models: [], overrides: [], theme: 'light', isLocal: false, localBadge: 'BADGE' })], { changed: true, absent: '#localbadge.show' }],
    // Brief FN: the heading over the models is the host's words for the provider in force (LocalBadge.ModelsHeading), as the badge is.
    ['dwightSettingsInit', [J({ providers: [], postures: [], models: [], overrides: [], theme: 'light', isLocal: true, modelsHeading: 'MODELS-HEADING' })], { text: [['#modelshead', 'MODELS-HEADING']] }],
    // Brief DH: the Dim switch says what the setting says, and posts the other.
    ['dwightSettingsInit', [J({ providers: [], postures: [], models: [], overrides: [], theme: 'light', walkDim: true })], { attr: [['#walkdim', 'aria-checked', 'true']] }],
    ['dwightSettingsInit', [J({ providers: [], postures: [], models: [], overrides: [], theme: 'light', walkDim: false })], { attr: [['#walkdim', 'aria-checked', 'false']] }],
    ['dwightSettingsInit', [J({ providers: [], postures: [], models: [], overrides: [], theme: 'light', walkDim: true })], { click: ['#walkdim'], postsExactly: 'walkdim:off' }],
    ['dwightSettingsInit', [J({ providers: [], postures: [], models: [], overrides: [], theme: 'light', isLocal: true, localBadge: 'BADGE' })], { changed: true, sel: '#localbadge.show', text: [['#localtext', 'BADGE']] }],
    ['dwightReconcile', [J({ applied: [] })], { throwsOnly: true }],
    ['dwightKey', [true, true], { changed: true }],
    ['dwightSetTheme', ['dark'], { changed: true }],
    ['dwightSetStatus', ['Saved'], { changed: true }],
    ['dwightSetSends', ['<b>Explain</b> — structure only.<br>'], { changed: true }],
    // Brief CW: the profile's price under its control, the host's sentence as text, nothing shown when there is none.
    ['dwightPostureLine', ['POSTURE-LINE'], { changed: true, text: [['#postureline', 'POSTURE-LINE']], absent: '#postureline[hidden]' }],
    ['dwightPostureLine', [''], { pre: [['dwightPostureLine', ['POSTURE-LINE']]], sel: '#postureline[hidden]', excludes: ['POSTURE-LINE'] }],
    ['dwightPostureLine', ['<b>x</b>'], { text: [['#postureline', '<b>x</b>']], absent: '#postureline b' }],
    ['dwightSettingsInit', [J({ providers: [], postures: [], models: [], overrides: [], theme: 'light', postureLine: 'POSTURE-LINE' })], { changed: true, text: [['#postureline', 'POSTURE-LINE']], absent: '#postureline[hidden]' }],
    // Brief BI: the chat's cost lands here: the Usage group, its totals asked for
    ['dwightOpenUsage', [], { postsExactly: 'usage:open', sel: '.railitem.on[data-g="Usage"]', absent: '[data-pane="Usage"][hidden]' }],
    // Brief BO: one group at a time, and the ledger read only when Usage is wanted.
    ['dwightOpenKey', [], { pre: [['dwightSettingsGo', ['Build']]], sel: '.railitem.on[data-g="Connection"]', absent: '#keyinput[style*="none"]' }],
    ['dwightOpenKey', [], { sel: '#keybtns[style*="none"]' }],
    // Brief EN: under the key input, the site's keys page - shown only when the host pushes it (FirstRunCore.KeysUrl), and its click a bare "keys".
    ['dwightOpenKey', [], { pre: [['dwightSettingsInit', [J({ providers: [], postures: [], models: [], overrides: [], theme: 'light', needsKey: true, keys: 'https://example.test/keys' })]]],
      sel: '#keyinput #keyval ~ #keyhelp:not([hidden]) button.settings-keyhelp', text: [['#keyhelp', 'No key yet? How to get one, and what it costs']] }],
    ['dwightOpenKey', [], { pre: [['dwightSettingsInit', [J({ providers: [], postures: [], models: [], overrides: [], theme: 'light', needsKey: true, keys: 'https://example.test/keys' })]]],
      click: ['#keyhelp button'], postsExactly: 'keys' }],
    ['dwightOpenKey', [], { pre: [['dwightSettingsInit', [J({ providers: [], postures: [], models: [], overrides: [], theme: 'light', needsKey: true })]]], sel: '#keyhelp[hidden]' }],
    // Brief ES unit 3: About names the build in the host's one line, the line the report sends; with none pushed, no line.
    ['dwightSettingsInit', [J({ providers: [], postures: [], models: [], overrides: [], theme: 'light', build: '0.9.0+2331576 · Release 78E49132F60B43EF' })],
      { changed: true, sel: '[data-pane="About"] #buildline:not([hidden])', text: [['#buildline', 'Dwight 0.9.0+2331576 · Release 78E49132F60B43EF']] }],
    ['dwightSettingsInit', [J({ providers: [], postures: [], models: [], overrides: [], theme: 'light' })], { sel: '#buildline[hidden]', absent: '#buildline:not([hidden])' }],
    ['dwightSettingsInit', [J({ providers: [], postures: [], models: [], overrides: [], theme: 'light', build: '<b>x</b>' })], { text: [['#buildline', 'Dwight <b>x</b>']], absent: '#buildline b' }],
    ['showKeyInput', [false], { pre: [['dwightOpenKey', []]], sel: '#keyinput[style*="none"]', absent: '#keybtns[style*="none"]' }],
    ['dwightSettingsGo', ['Build'], { changed: true, postsAll: [], sel: '.railitem.on[data-g="Build"]', absent: '[data-pane="Build"][hidden]' }],
    ['dwightSettingsGo', ['Usage'], { postsExactly: 'usage:open', sel: '[data-pane="Build"][hidden]' }],
    // The rail's pin, from the host's remembered setting and from its own button.
    ['dwightRailPin', [true], { changed: true, sel: '#railslot.pinned', attr: [['#railpin', 'aria-pressed', 'true']] }],
    ['setRailPin', [true], { postsExactly: 'railpin:on', sel: '#railslot.pinned' }],
    ['setRailPin', [false], { pre: [['dwightRailPin', [true]]], postsExactly: 'railpin:off', absent: '#railslot.pinned' }],
    // Every choice a dropdown, its name closed; on or off a real switch, with its sentence showing only when on.
    ['dwightSettingsInit', [J({ providers: [{ id: 'p', name: 'PROVIDER-NAME' }], provider: 'p', postures: [{ id: 'balanced', name: 'Balanced' }], posture: 'balanced', models: [], overrides: [], theme: 'dark', approvalMode: 'scripts_and_deletions', railPinned: true, fullControl: true, webSearch: false })],
      { text: [['#provider .dd-txt', 'PROVIDER-NAME'], ['#approval .dd-txt', 'Ask for scripts and deletions only'], ['#theme .dd-txt', 'Dark']], sel: '#railslot.pinned', attr: [['#fullcontrol', 'aria-checked', 'true'], ['#fullcontrol', 'role', 'switch'], ['#websearch', 'aria-checked', 'false']], absent: '#hint-fullcontrol[hidden]' }],
    ['setFullControl', ['on'], { postsExactly: 'fullcontrol:on', attr: [['#fullcontrol', 'aria-checked', 'true']], absent: '#hint-fullcontrol[hidden]' }],
    ['setFullControl', ['off'], { pre: [['setFullControl', ['on']]], postsExactly: 'fullcontrol:off', sel: '#hint-fullcontrol[hidden]' }],
    ['setShowNotes', ['off'], { postsExactly: 'shownotes:off', attr: [['#shownotes', 'aria-checked', 'false']] }],
    ['setShowNotes', ['on'], { pre: [['setShowNotes', ['off']]], postsExactly: 'shownotes:on', attr: [['#shownotes', 'aria-checked', 'true']] }],
    ['dwightShowNotes', [false], { attr: [['#shownotes', 'aria-checked', 'false']] }],
    ['setWebSearch', ['on'], { postsExactly: 'websearch:on', attr: [['#websearch', 'aria-checked', 'true']], absent: '#hint-websearch[hidden]' }],
    ['dwightSend', ['save'], { posts: true }],
    // Brief AK: the Usage section. Every cell arrives worded (SpendLedgerCore.UsageJson), so sentinels prove the shell
    // places what it is handed: the period table, the breakdowns for the chosen period, the file line, and the skipped
    // line only when there is one. The two buttons post their own commands.
    ['dwightUsage', [USAGE('')], { changed: true, containsAll: ['PERIOD-TODAY', 'MODEL-MONTH', 'VERB-MONTH', 'DOC-MONTH', 'FILE-LINE'],
      sel: '#usage-skipped[style*="none"]' }],
    ['dwightUsage', [USAGE('SKIPPED-LINE')], { text: [['#usage-skipped', 'SKIPPED-LINE']], absent: '#usage-skipped[style*="none"]' }],
    ['setUsagePeriod', [3], { pre: [['dwightUsage', [USAGE('')]]], changed: true, containsAll: ['MODEL-ALL', 'EMPTY-LINE'],
      text: [['#usage-period .dd-txt', 'All time']] }],
    ['dwightSend', ['usage:export'], { postsExactly: 'usage:export' }],
    ['dwightSend', ['usage:folder'], { postsExactly: 'usage:folder' }],
  ],

  // Report a problem. The block is pushed by the plugin (ProblemReport), so the shell holds no copy of the facts: a
  // sentinel proves it renders whatever it is handed. The mail button exists only when the plugin says an address is
  // configured, and what the user typed travels in the message verbatim (sendIssue/sendMail are the buttons' handlers).
  // Brief DA: the Walk's bar. It holds no value of its own: what it shows arrives through this one call, and every control posts back to the host.
  'walk-bar.html': [
    // ADR-088's second wall, on a step's sentence in the bar.
    ['dwightWalkStep', [J({ label: 'Step 1', html: ANCHOR('javascript:void(0)') })], { events: [['#p', 'click', {}]], prevented: true, postsExactly: 'h:0' }],   // h:0 is the bar's own height, and no leak beside it
    // Brief DJ: the Lens's one control, since Brief EC unit 3 a switch beside Focus and Dim. Absent with no lens, so it is off and cannot move; a click goes to the host, which draws it again.
    ['dwightWalkStep', [W_STEP(1, 3)], { sel: '#walklens[disabled]', attr: [['#walklens', 'role', 'switch'], ['#walklens', 'aria-label', 'Every wire'], ['#walklens', 'aria-checked', 'false']] }],
    ['dwightWalkStep', [J({ label: 'Step 2 of 3', html: 'x', i: 1, n: 3, everywire: false })], { absent: '#walklens[disabled]', attr: [['#walklens', 'aria-checked', 'false']] }],
    ['dwightWalkStep', [J({ label: 'Step 2 of 3', html: 'x', i: 1, n: 3, everywire: true })], { absent: '#walklens[disabled]', attr: [['#walklens', 'aria-checked', 'true']] }],
    // Brief EC unit 3: Focus and Dim read what the host says, in the step's document and pushed on their own when either moves.
    ['dwightWalkStep', [J({ label: 'Step 2 of 3', html: 'x', i: 1, n: 3, focus: true, dim: false })], { attr: [['#walkfocus', 'aria-checked', 'true'], ['#walkdim', 'aria-checked', 'false']] }],
    ['dwightWalkSwitches', [false, true], { pre: [['dwightWalkStep', [J({ label: 'Step 2 of 3', html: 'x', i: 1, n: 3, focus: true, dim: false })]]], attr: [['#walkfocus', 'aria-checked', 'false'], ['#walkdim', 'aria-checked', 'true']] }],
    ['dwightWalkStep', [W_STEP(1, 3)], { click: ['#walkfocus'], postsAll: ['h:0', 'focus'] }],
    ['dwightWalkStep', [J({ label: 'Step 2 of 3', html: 'x', i: 1, n: 3, focus: true, dim: true })], { click: ['#walkdim'], postsAll: ['h:0', 'dim:off'] }],
    ['dwightWalkStep', [J({ label: 'Step 2 of 3', html: 'x', i: 1, n: 3, focus: true, dim: false })], { click: ['#walkdim'], postsAll: ['h:0', 'dim:on'] }],
    ['dwightWalkStep', [W_STEP(2, 6)], { changed: true, text: [['#walkstep', 'Step 3 of 6'], ['#walksent', 'SENTENCE-HTML']], sel: '#walksent code', attr: [['.walkdot.on', 'aria-current', 'step']], absent: '#walkback[disabled]' }],
    ['dwightWalkStep', [W_STEP(0, 6)], { sel: '#walkback[disabled]', absent: '#walknext[disabled]', attr: [['#walkback', 'title', 'Back: Left arrow'], ['#walknext', 'title', 'Next: Right arrow'], ['#walkask', 'title', 'Ask the chat about this step'], ['#walkclose', 'title', 'Back to the panel: Esc']],
      excludes: ['>Back<', '>Next<', 'Ask about this step<', 'Leave the walk'] }],
    ['dwightWalkStep', [W_STEP(5, 6)], { sel: '#walknext[disabled]', absent: '#walkback[disabled]' }],
    // A step whose components have gone keeps its sentence, with the line under it, and Ask stays enabled: the person still wants to read what the step said.
    ['dwightWalkStep', [W_STEP(2, 6, 'GONE-LINE')], { sel: '#walksent div.walkgone', text: [['.walkgone', 'GONE-LINE'], ['#walksent', 'SENTENCE-HTML']], absent: '#walkask[disabled]' }],
    // Brief FN: the words, the gone line and the time sit in their own block inside the scrolling box, because that block's height is what the bar asks for
    // (tools/check-walk-bar-height.js measures it at three scales). Words outside it would be words the ask cannot see.
    ['dwightWalkStep', [J({ label: 'Step 2 of 3', html: 'SENTENCE-HTML <code>Loft</code>', i: 1, n: 3, gone: 'GONE-LINE', time: 'TIME-LINE' })], { sel: '#walksent > #walkwords > div.walkgone + div.walktime',
      text: [['#walkwords > .walkgone', 'GONE-LINE'], ['#walkwords > .walktime', 'TIME-LINE']], containsAll: ['SENTENCE-HTML <code>Loft</code><div class="walkgone">'] }],
    ['dwightWalkStep', [W_STEP(2, 6, '<b>x</b>')], { text: [['.walkgone', '<b>x</b>']], absent: '.walkgone b' }],
    // Brief EI: on the Focus bar a Learn card sits in the sentence's place, drawn by the panel's own renderer; the bar keeps its own switches and arrows.
    ['dwightWalkStep', [W_STEP(2, 6)], { absent: '.deck' }],
    ['dwightWalkStep', [TV.bar], { sel: '#walksent .deck .deck-card.card', text: [['#walkstep', 'Learn'], ['#walksent .deck-of', '2 of 4']], absent: '#deck-focus, #deck-dim, #walksent .deck-foot .swrow',
      click: ['#walksent .deck-opt:nth-of-type(3)'], postsAll: ['h:0', 'deck:answer:o%3A2'] }],
    ['dwightWalkStep', [TV.bar], { keys: ['3'], postsAll: ['h:0', 'deck:answer:o%3A2'] }],
    ['dwightWalkStep', [TV.bar], { absent: '#walksent .deck-back, #walksent .deck-foot' }],   // the bar's own arrows, and no footer with nothing in it
    ['dwightWalkStep', [TV.bar], { keys: ['ArrowRight'], postsAll: ['h:0', 'key:ArrowRight'] }],   // the arrows stay the host's, as on any step
    ['dwightWalkStep', [TV.barWrong], { text: [['#walksent .deck-answer', 'The answer is Move (15).'], ['#walksent .deck-next', 'Next']], click: ['#walksent .deck-next'], postsAll: ['h:0', 'deck:next'] }],
    ['dwightWalkStep', [W_STEP(2, 6)], { pre: [['dwightWalkStep', [TV.bar]]], absent: '.deck', text: [['#walksent', 'SENTENCE-HTML Loft']] }],   // a step after a card: the card is gone
    ['dwightWalkStep', [J({ label: '<b>x</b>', html: '', i: 0, n: 3 })], { text: [['#walkstep', '<b>x</b>']], absent: '#walkstep b' }],
    ['dwightWalkStep', [W_STEP(1, 3)], { click: ['.walkdot:nth-of-type(3)'], postsAll: ['h:0', 'goto:2'] }],
    ['dwightWalkStep', [W_STEP(1, 3)], { click: ['#walknext'], postsAll: ['h:0', 'next'] }],
    // Brief DD: Play is Pause while playing, the line of times is text, and a Walk of one step has nothing to play.
    ['dwightWalkStep', [W_STEP(1, 3)], { click: ['#walkplay'], postsAll: ['h:0', 'play'], attr: [['#walkplay', 'aria-label', 'Play'], ['#walkplay-ic', 'href', '#ic-play'], ['#walkplay', 'aria-pressed', 'false']], absent: '.walktime' }],
    ['dwightWalkStep', [J({ label: 'Step 2 of 3', html: 'x', i: 1, n: 3, playing: true, time: '<b>Slowest</b> here.' })], { text: [['#walksent .walktime', '<b>Slowest</b> here.']], attr: [['#walkplay', 'aria-label', 'Pause'], ['#walkplay-ic', 'href', '#ic-pause'], ['#walkplay', 'aria-pressed', 'true']], absent: '.walktime b' }],
    ['dwightWalkStep', [J({ label: 'Step 1 of 1', html: 'x', i: 0, n: 1 })], { sel: '#walkplay[disabled]' }],
    ['dwightWalkStep', [J({ label: 'Step 1 of 3', html: 'x', i: 0, n: 3, canplay: false })], { sel: '#walkplay[disabled]' }],
    ['dwightWalkStep', [W_STEP(1, 3)], { click: ['#walkback'], postsAll: ['h:0', 'back'] }],
    ['dwightWalkStep', [W_STEP(1, 3)], { click: ['#walkask'], postsAll: ['h:0', 'ask'] }],
    ['dwightWalkStep', [W_STEP(1, 3)], { click: ['#walkclose'], postsAll: ['h:0', 'close'] }],   // Brief EC unit 1: the host reads it as Focus off, never as the end of the Walk
    ['dwightWalkStep', [J({ label: 'Step 2 of 3', html: 'x', i: 1, n: 3, everywire: false })], { click: ['#walklens'], postsAll: ['h:0', 'everywire'] }],
    ['dwightWalkStep', [W_STEP(1, 3)], { keys: ['ArrowRight'], postsAll: ['h:0', 'key:ArrowRight'] }],
    ['dwightWalkStep', [W_STEP(1, 3)], { keys: ['Escape'], postsAll: ['h:0', 'key:Escape'] }],
    ['dwightSetTheme', ['dark'], { changed: true, sel: 'body.dark' }],
    ['dwightSend', ['next'], { posts: true }],
  ],
  'feedback.html': [
    ['dwightFeedbackInit', [J({ block: 'SENT-BLOCK Dwight: 0.0.0', canMail: false, applied: 'dark' })],
      { sel: '#block', contains: 'SENT-BLOCK', text: [['#block', 'SENT-BLOCK Dwight: 0.0.0']] }],
    ['dwightFeedbackInit', [J({ block: 'b', canMail: false, applied: 'light' })], { absent: '#mailbtn:not([style*="none"])' }],
    ['dwightFeedbackInit', [J({ block: 'b', canMail: true, applied: 'light' })], { sel: '#mailbtn:not([style*="none"])' }],
    ['sendIssue', [], { pre: [['dwightFeedbackInit', [J({ block: 'b', canMail: true, applied: 'light' })]]],
      set: [['#what', 'value', '  it froze  ']], postsExactly: 'issue:it froze' }],
    ['sendMail', [], { pre: [['dwightFeedbackInit', [J({ block: 'b', canMail: true, applied: 'light' })]]],
      set: [['#what', 'value', 'it froze']], postsExactly: 'mail:it froze' }],
    ['dwightSetTheme', ['dark'], { changed: true }],
    ['dwightSetStatus', ['Opened in your browser'], { changed: true }],
    ['dwightSend', ['close'], { posts: true }],
  ],

  // Dwight notes: the editor. The text, the head and the stale sentence are pushed by the plugin (DwightNotes / NoteState), so the shell holds no copy
  // of them: sentinels prove it renders whatever it is handed. Delete and Ask exist only when the plugin says so, and what the person typed travels in
  // the message verbatim (saveNote is the button's handler).
  'note.html': [
    // ADR-088's second wall, on a note's rendered text.
    ['dwightNoteInit', [J({ head: 'h', text: 'x', html: ANCHOR('javascript:void(0)'), canDelete: true, canAsk: true, applied: 'light' })], { events: [['#p', 'click', {}]], prevented: true, postsNothing: true }],
    ['dwightNoteInit', [J({ head: 'h', text: 'x', html: ANCHOR('https://example.com'), canDelete: true, canAsk: true, applied: 'light' })], { events: [['#p', 'click', {}]], prevented: false, postsExactly: 'leak' }],
    ['dwightNoteInit', [J({ head: 'SENT-HEAD', text: 'SENT-TEXT', about: 'SENT-ABOUT', canDelete: false, canAsk: false, applied: 'dark' })],
      { sel: '#text', text: [['#head', 'SENT-HEAD'], ['#about', 'SENT-ABOUT']] }],
    ['dwightNoteInit', [J({ head: 'h', text: '', canDelete: false, canAsk: false, applied: 'light' })], { absent: '#delbtn:not([style*="none"])' }],
    ['dwightNoteInit', [J({ head: 'h', text: 'x', canDelete: true, canAsk: true, applied: 'light' })], { sel: '#delbtn:not([style*="none"])' }],
    ['dwightNoteInit', [J({ head: 'h', text: '', stale: 'SENT-STALE', canDelete: true, canAsk: true, applied: 'light' })], { sel: '#stale:not([style*="none"])', text: [['#stale', 'SENT-STALE']] }],
    ['dwightNoteInit', [J({ head: 'h', text: '', canDelete: true, canAsk: true, applied: 'light' })], { absent: '#stale:not([style*="none"])' }],
    ['saveNote', [], { set: [['#text', 'value', '  keep it at 40  ']], postsExactly: 'save:keep it at 40' }],
    // Brief DO: the tools write the marks where the selection is, and a second press takes them off.
    ['noteWrap', ['**'], { set: [['#text', 'value', 'a word here'], ['#text', 'selectionStart', 2], ['#text', 'selectionEnd', 6]], valueIs: ['#text', 'a **word** here'] }],
    ['noteWrap', ['`'], { set: [['#text', 'value', 'the Loft'], ['#text', 'selectionStart', 4], ['#text', 'selectionEnd', 8]], valueIs: ['#text', 'the `Loft`'] }],
    ['noteWrap', ['**'], { set: [['#text', 'value', 'a **word** here'], ['#text', 'selectionStart', 4], ['#text', 'selectionEnd', 8]], valueIs: ['#text', 'a word here'] }],
    ['noteWrap', ['**'], { set: [['#text', 'value', ''], ['#text', 'selectionStart', 0], ['#text', 'selectionEnd', 0]], valueIs: ['#text', '****'] }],
    ['noteBullets', [], { set: [['#text', 'value', 'one\ntwo\nthree'], ['#text', 'selectionStart', 1], ['#text', 'selectionEnd', 5]], valueIs: ['#text', '- one\n- two\nthree'] }],
    ['noteBullets', [], { set: [['#text', 'value', '- one\n- two'], ['#text', 'selectionStart', 0], ['#text', 'selectionEnd', 9]], valueIs: ['#text', 'one\ntwo'] }],
    ['noteBullets', [], { set: [['#text', 'value', '- one\ntwo'], ['#text', 'selectionStart', 0], ['#text', 'selectionEnd', 8]], valueIs: ['#text', '- one\n- two'] }],
    // Brief ED: lists. Enter in a list line continues it with the next mark and the same indent and renumbers what follows; Enter on an empty item ends the list;
    // Shift+Enter, a plain line or the caret in a marker is the textarea's own new line, left alone.
    ['noteEditing', [true], { set: [['#text', 'value', '- one'], ['#text', 'selectionStart', 5], ['#text', 'selectionEnd', 5]], events: [['#text', 'keydown', { key: 'Enter' }]], prevented: true, valueIs: ['#text', '- one\n- '] }],
    ['noteEditing', [true], { set: [['#text', 'value', '* one'], ['#text', 'selectionStart', 5], ['#text', 'selectionEnd', 5]], events: [['#text', 'keydown', { key: 'Enter' }]], prevented: true, valueIs: ['#text', '* one\n- '] }],
    ['noteEditing', [true], { set: [['#text', 'value', '1. one'], ['#text', 'selectionStart', 6], ['#text', 'selectionEnd', 6]], events: [['#text', 'keydown', { key: 'Enter' }]], prevented: true, valueIs: ['#text', '1. one\n2. '] }],
    ['noteEditing', [true], { set: [['#text', 'value', '  3) one'], ['#text', 'selectionStart', 8], ['#text', 'selectionEnd', 8]], events: [['#text', 'keydown', { key: 'Enter' }]], prevented: true, valueIs: ['#text', '  3) one\n  4) '] }],
    ['noteEditing', [true], { set: [['#text', 'value', '1. a\n2. b\n3. c'], ['#text', 'selectionStart', 4], ['#text', 'selectionEnd', 4]], events: [['#text', 'keydown', { key: 'Enter' }]], valueIs: ['#text', '1. a\n2. \n3. b\n4. c'] }],
    ['noteEditing', [true], { set: [['#text', 'value', '- on|e'.replace('|', '')], ['#text', 'selectionStart', 4], ['#text', 'selectionEnd', 4]], events: [['#text', 'keydown', { key: 'Enter' }]], valueIs: ['#text', '- on\n- e'] }],
    ['noteEditing', [true], { set: [['#text', 'value', '- one\n- '], ['#text', 'selectionStart', 8], ['#text', 'selectionEnd', 8]], events: [['#text', 'keydown', { key: 'Enter' }]], prevented: true, valueIs: ['#text', '- one\n'] }],
    ['noteEditing', [true], { set: [['#text', 'value', '- one'], ['#text', 'selectionStart', 5], ['#text', 'selectionEnd', 5]], events: [['#text', 'keydown', { key: 'Enter' }], ['#text', 'keydown', { key: 'Enter' }]], prevented: true, valueIs: ['#text', '- one\n'] }],
    ['noteEditing', [true], { set: [['#text', 'value', '- one'], ['#text', 'selectionStart', 5], ['#text', 'selectionEnd', 5]], events: [['#text', 'keydown', { key: 'Enter', shiftKey: true }]], prevented: false, valueIs: ['#text', '- one'] }],
    ['noteEditing', [true], { set: [['#text', 'value', 'plain'], ['#text', 'selectionStart', 5], ['#text', 'selectionEnd', 5]], events: [['#text', 'keydown', { key: 'Enter' }]], prevented: false, valueIs: ['#text', 'plain'] }],
    ['noteEditing', [true], { set: [['#text', 'value', '- one'], ['#text', 'selectionStart', 1], ['#text', 'selectionEnd', 1]], events: [['#text', 'keydown', { key: 'Enter' }]], prevented: false, valueIs: ['#text', '- one'] }],
    // The numbered tool numbers from 1 and takes them off again; each list tool turns the other's lines into its own; a blank line between items stays blank.
    ['noteNumbers', [], { set: [['#text', 'value', 'one\ntwo\nthree'], ['#text', 'selectionStart', 0], ['#text', 'selectionEnd', 13]], valueIs: ['#text', '1. one\n2. two\n3. three'] }],
    ['noteNumbers', [], { set: [['#text', 'value', '- one\n- two'], ['#text', 'selectionStart', 0], ['#text', 'selectionEnd', 11]], valueIs: ['#text', '1. one\n2. two'] }],
    ['noteNumbers', [], { set: [['#text', 'value', '3. one\n\n7. two'], ['#text', 'selectionStart', 0], ['#text', 'selectionEnd', 14]], valueIs: ['#text', 'one\n\ntwo'] }],
    ['noteNumbers', [], { set: [['#text', 'value', 'one\n\ntwo'], ['#text', 'selectionStart', 0], ['#text', 'selectionEnd', 8]], valueIs: ['#text', '1. one\n\n2. two'] }],
    ['noteNumbers', [], { set: [['#text', 'value', ''], ['#text', 'selectionStart', 0], ['#text', 'selectionEnd', 0]], valueIs: ['#text', '1. '] }],
    ['noteBullets', [], { set: [['#text', 'value', '1. one\n2) two'], ['#text', 'selectionStart', 0], ['#text', 'selectionEnd', 13]], valueIs: ['#text', '- one\n- two'] }],
    // Brief ED: the pin switch. A new note has none; the host says where an existing one is and in what words; a click asks, and the switch moves only when the host says.
    ['dwightNotePin', [J({ on: true, label: 'PIN-LABEL', stale: null })], { pre: [['dwightNoteInit', [J({ head: 'h', text: 'x', html: '<p>x</p>', canDelete: true, canAsk: true, applied: 'light', pin: { on: false, label: 'Free' } })]]],
      attr: [['#notepin', 'aria-checked', 'true']], text: [['#pinword', 'PIN-LABEL']], absent: '#pinrow[hidden]' }],
    ['dwightNotePin', [J({ on: false, label: 'Free', stale: 'SENT-STALE' })], { pre: [['dwightNoteInit', [J({ head: 'h', text: 'x', html: '<p>x</p>', canDelete: true, canAsk: true, applied: 'light', pin: { on: true, label: 'Pinned to x' } })]]],
      text: [['#stale', 'SENT-STALE'], ['#pinword', 'Free']], sel: '#stale:not([style*="none"])' }],
    ['dwightNotePin', ['not json'], { pre: [['dwightNoteInit', [J({ head: 'h', text: 'x', html: '<p>x</p>', canDelete: true, canAsk: true, applied: 'light', pin: { on: true, label: 'KEPT' } })]]], text: [['#pinword', 'KEPT']] }],
    ['dwightNoteInit', [J({ head: 'h', text: 'x', html: '<p>x</p>', canDelete: true, canAsk: true, applied: 'light', pin: { on: true, label: 'Pinned to Slider' } })], { click: ['#notepin'], postsExactly: 'pin:off', attr: [['#notepin', 'aria-checked', 'true']], text: [['#pinword', 'Pinned to Slider']] }],
    ['dwightNoteInit', [J({ head: 'h', text: 'x', html: '<p>x</p>', canDelete: true, canAsk: true, applied: 'light', pin: { on: false, label: 'Free' } })], { click: ['#notepin'], postsExactly: 'pin:on', attr: [['#notepin', 'aria-checked', 'false']] }],
    ['dwightNoteInit', [J({ head: 'h', text: '', canDelete: false, canAsk: true, applied: 'light' })], { sel: '#pinrow[hidden]' }],
    // The switch is the read view's: Edit takes it away, and a note that opens in the editor has none showing.
    ['noteEditing', [true], { pre: [['dwightNoteInit', [J({ head: 'h', text: 'x', html: '<p>x</p>', canDelete: true, canAsk: true, applied: 'light', pin: { on: true, label: 'Pinned to Slider' } })]]], sel: '#pinrow[hidden]' }],
    ['dwightNoteInit', [J({ head: 'h', text: 'x', canDelete: true, canAsk: true, applied: 'light', pin: { on: true, label: 'Pinned to Slider' } })], { sel: '#pinrow[hidden]' }],
    ['dwightNotePin', [J({ on: false, label: 'Free' })], { pre: [['dwightNoteInit', [J({ head: 'h', text: 'x', canDelete: true, canAsk: true, applied: 'light', pin: { on: true, label: 'x' } })]]], sel: '#pinrow[hidden]', text: [['#pinword', 'Free']] }],
    ['noteTable', [2], { set: [['#text', 'value', ''], ['#text', 'selectionStart', 0], ['#text', 'selectionEnd', 0]], valueIs: ['#text', '| Heading | Heading |\n| --- | --- |\n|   |   |\n'] }],
    ['noteTable', [4], { set: [['#text', 'value', 'Before'], ['#text', 'selectionStart', 6], ['#text', 'selectionEnd', 6]], valueIs: ['#text', 'Before\n\n| Heading | Heading | Heading | Heading |\n| --- | --- | --- | --- |\n|   |   |   |   |\n'] }],
    // An existing note opens as it reads, its text the plugin's rendering, and Edit is the marks. A new note opens ready to type.
    ['dwightNoteInit', [J({ head: 'h', text: '**x**', html: '<p><strong>x</strong></p>', canDelete: true, canAsk: true, applied: 'light' })], { sel: '#read strong', absent: '#read[hidden], #editbtn[hidden]' }],
    ['dwightNoteInit', [J({ head: 'h', text: '', canDelete: false, canAsk: false, applied: 'light' })], { sel: '#read[hidden]', absent: '#edit[hidden]' }],
    ['noteEditing', [true], { pre: [['dwightNoteInit', [J({ head: 'h', text: 'x', html: '<p>x</p>', canDelete: true, canAsk: true, applied: 'light' })]]], sel: '#read[hidden]', absent: '#edit[hidden]' }],
    ['dwightSetTheme', ['dark'], { changed: true }],
    ['dwightSetStatus', ['A note holds at most 600 characters'], { changed: true }],
    ['dwightSend', ['ask'], { postsExactly: 'ask' }],
    ['dwightSend', ['delete'], { postsExactly: 'delete' }],
    ['dwightSend', ['close'], { posts: true }],
  ],

  // The first-run tour. Note what is NOT asserted here and why: the tour shell holds no list of verbs
  // and no chord string of its own - both are pushed in by the plugin (DwightVerbs / DwightChord), so
  // there is no second copy in this file to go stale when a verb is renamed. What the harness can prove
  // is the other half - that the renderer puts on screen whatever it is handed, all five rows and the
  // chord - and that the last card's action adapts to the key fact rather than being fixed markup.
  // The last step's promise is static copy - a claim about ADR-075's mutation gate, an architecture and not a
  // constant, so nothing here binds it to the gate. Since the redesign it IS pinned to its ruled wording ("In the
  // chat, nothing changes without a card."; the old unscoped "Nothing changes your canvas without a card" was false
  // outside the chat), so it cannot be reworded back into a universal without a fixture failing. The fixtures below
  // the first five were added with the redesign and use SENTINEL data; the first five are the originals.
  'tour.html': [
    ['dwightTourInit', [TOUR_INIT], { sel: '#verbs', nonEmpty: true, contains: 'Ctrl+Shift+Right-click' }],
    ['dwightTourInit', [TOUR_INIT], { contains: 'Reorganise' }],
    // The last card, with a key still missing: the primary action must offer the key, never run a verb.
    ['dwightTourGo', [4], { pre: [['dwightTourInit', [TOUR_INIT]]], changed: true, contains: 'Add your API key' }],
    // ... and with a key already stored, the same card just finishes. ("Done" became "Open Dwight" in the redesign: the
    // last button now closes the tour AND points at the dock, and the brief names it. Same fixture, new word.)
    ['dwightTourGo', [4], { pre: [['dwightTourInit', [TOUR_KEYED]]], changed: true, contains: 'Open Dwight' }],
    ['dwightNext', [], { pre: [['dwightTourInit', [TOUR_INIT]], ['dwightTourGo', [4]]], posts: true }],
    ['dwightSetTheme', ['dark'], { changed: true }],
    ['dwightSend', ['close'], { posts: true }],
    // The host calls this on load so the arrow keys and Escape work without a click first (2026-10-25). It only moves
    // focus, so there is nothing on the surface to assert; the row proves it exists and runs without throwing.
    ['dwightTourFocus', [], { pre: [['dwightTourInit', [TOUR_INIT]]] }],

    // ---- THE REDESIGNED TOUR, every entry point, driven with SENTINEL data -------------------------------------------
    // 1. Everything the plugin pushes is on the surface: each verb's NAME and TAG, the chord as keycaps, the trust badges,
    //    the consent card's own words. Sentinels cannot be satisfied by anything typed into tour.html.
    ['dwightTourInit', [SENT(false)], { containsAll: SENT_ALL }],
    // 2. Each step, with the copy that was RULED (mantis-38, 2026-09-19) pinned, and the one headline that takes its verb
    //    from the push ("SENT-VERB-E is a conversation" - the real one reads "Build is a conversation").
    ['dwightTourGo', [0], { pre: T_INIT, text: [['#act', 'Welcome'], ['#h', 'Tools for Grasshopper, on the canvas.']], restVisible: true }],
    ['dwightTourGo', [1], { pre: T_INIT, text: [['#h', 'Five verbs.'], ['#p', 'Most start from what you select.']], restVisible: true }],
    ['dwightTourGo', [2], { pre: T_INIT, text: [['#h', 'Three places Dwight lives.'], ['#p', 'History keeps every answer.']], restVisible: true }],
    ['dwightTourGo', [3], { pre: T_INIT, text: [['#h', 'SENT-VERB-E is a conversation.']], restVisible: true }],
    ['dwightTourGo', [4], { pre: T_INIT, restVisible: true,
      text: [['#h', 'In the chat, nothing changes without a card.'], ['#p', 'Dwight checks what it builds, and Ctrl-Z undoes it. Full control, off by default, is the one exception.']] }],
    // 3. Motion plays ONCE when a step is reached, and again when the picture is clicked (data-plays counts sequences).
    ['dwightTourInit', [SENT(false)], { sel: '#app[data-plays="1"]' }],
    ['dwightTourGo', [1], { pre: T_INIT, sel: '#app[data-plays="2"]' }],
    ['dwightTourReplay', [], { pre: T_INIT.concat([['dwightTourGo', [1]]]), sel: '#app[data-plays="3"]' }],
    // ... and with prefers-reduced-motion it never plays at all: every step is its final state.
    ['dwightTourGo', [2], { reducedMotion: true, pre: T_INIT, sel: 'html.reduced', absent: '#app[data-plays]' }],
    // 4. The finish. With a key stored the last action is "finish" (the plugin closes the tour and points at the dock);
    //    with none it is "settings". Neither can be a verb, and an earlier step's Next posts nothing at all.
    ['dwightNext', [], { pre: T_INIT.concat(T_LAST), postsAll: ['finish'], text: [['#next', 'Open Dwight']] }],
    ['dwightNext', [], { pre: T_INIT_NOKEY.concat(T_LAST), postsAll: ['settings'], text: [['#next', 'Add your API key'], ['#keynote', 'API key']] }],
    ['dwightNext', [], { pre: T_INIT.concat([['dwightTourGo', [0]]]), postsAll: [], text: [['#h', 'Five verbs.']] }],
    ['dwightTourGo', [4], { pre: T_INIT, absent: '#keynote.on' }],
    // The docs button: only on the last step, and only when the plugin pushed a URL.
    ['dwightTourGo', [4], { pre: [['dwightTourInit', [SENT(false, 'https://example.test/docs')]]], sel: '#docs:not([hidden])' }],
    ['dwightTourGo', [4], { pre: T_INIT, absent: '#docs:not([hidden])' }],
    ['dwightTourGo', [0], { pre: [['dwightTourInit', [SENT(false, 'https://example.test/docs')]]], absent: '#docs:not([hidden])' }],
    // Brief EN: "How to get a key" - on the last step, on the key's own line, only while no key is stored and only when the plugin
    // pushed the keys page. It sends a bare "keys" (the plugin decides the address), and the Documentation button stays beside it.
    ['dwightTourGo', [4], { pre: [['dwightTourInit', [SENT(true, 'https://example.test/docs', 'https://example.test/keys')]]],
      sel: '#keynote.on #getkey:not([hidden])', absent: '#docs[hidden]', text: [['#getkey', 'How to get a key'], ['#next', 'Add your API key']] }],
    ['dwightTourGo', [4], { pre: [['dwightTourInit', [SENT(true, '', 'https://example.test/keys')]]], click: ['#getkey'], postsAll: ['keys'] }],
    ['dwightTourGo', [4], { pre: [['dwightTourInit', [SENT(false, '', 'https://example.test/keys')]]], absent: '#getkey:not([hidden])' }],
    ['dwightTourGo', [4], { pre: T_INIT_NOKEY, absent: '#getkey:not([hidden])' }],
    ['dwightTourGo', [3], { pre: [['dwightTourInit', [SENT(true, '', 'https://example.test/keys')]]], absent: '#getkey:not([hidden])' }],
    // 5. The keyboard: arrows move (and clamp at both ends - the right arrow on the last step does NOT finish), Escape closes.
    ['dwightTourGo', [0], { pre: T_INIT, keys: ['ArrowRight', 'ArrowRight'], text: [['#h', 'Three places Dwight lives.']] }],
    ['dwightTourGo', [3], { pre: T_INIT, keys: ['ArrowLeft'], text: [['#h', 'Three places Dwight lives.']] }],
    ['dwightTourGo', [0], { pre: T_INIT, keys: ['ArrowLeft'], text: [['#h', 'Tools for Grasshopper, on the canvas.']] }],
    ['dwightTourGo', [4], { pre: T_INIT, keys: ['ArrowRight'], text: [['#h', 'In the chat']], postsAll: [] }],
    ['dwightTourGo', [1], { pre: T_INIT, keys: ['Escape'], postsAll: ['close'] }],
  ],
};

// Entry points with no fixture must be DECLARED, with a reason. Silence is a failure.
const DECLARED_UNCOVERED = {
  'panel-shell.html': {
    dwightFault: 'takes a live DOM element from an onclick binding, not fixture data',
    dwightDiagThis: 'reads the current selection out of an already-rendered fault card',
    dwightApplySelected: 'takes the clicked button element and reads sibling checkbox state',
    dwightToScriptGo: 'takes the clicked button element',
    dwightCopyBlock: 'takes the clicked button and needs a clipboard',
    dwightCopyFallback: 'clipboard fallback path; execCommand is not meaningfully testable here',

  },
};

// ---------------------------------------------------------------------------------------------

// OPT-IN STUBS (first-run tour + dock attention). A fixture asks for these by name in its expectations; a fixture
// that does not gets exactly the DOM it always got, so nothing already covered can change under it.
//   fakeTimers    - setTimeout is RECORDED, not run, so "it stops by itself" can be asserted without waiting: the
//                   fixture can see that a timer was scheduled and then run it (flushTimers).
//   reducedMotion - window.matchMedia exists and reports prefers-reduced-motion: reduce (jsdom ships none).
function shellDom(file, opts = {}) {
  const html = fs.readFileSync(path.join(SHELLS, file), 'utf8')
    .split('/*__DWIGHT_TOKENS__*/').join(fs.readFileSync(path.join(SHELLS, 'dwight-tokens.css'), 'utf8'))
    .split('/*__DWIGHT_COMPONENTS__*/').join(fs.readFileSync(path.join(SHELLS, 'dwight-components.css'), 'utf8'))
    .split('<!--__DWIGHT_ICONS__-->').join(fs.readFileSync(path.join(SHELLS, 'dwight-icons.svg'), 'utf8'))
    .split('/*__DWIGHT_DROPDOWN__*/').join(fs.readFileSync(path.join(SHELLS, 'dwight-dropdown.js'), 'utf8'))
    .split('/*__DWIGHT_LINKS__*/').join(fs.readFileSync(path.join(SHELLS, 'dwight-links.js'), 'utf8'))
    .split('/*__DWIGHT_DECK__*/').join(fs.readFileSync(path.join(SHELLS, 'dwight-deck.js'), 'utf8'));
  const posted = [];
  const dom = new JSDOM(html, {
    runScripts: 'dangerously',
    pretendToBeVisual: true,
    beforeParse(window) {
      // The host bridge. Present before parse, because a shell may post at load.
      window.chrome = { webview: { postMessage: (m) => posted.push(m), addEventListener() {} } };
      if (opts.fakeTimers) {
        window.__timers = [];
        window.setTimeout = (fn, ms) => { window.__timers.push({ fn, ms }); return window.__timers.length; };
        window.clearTimeout = () => {};
      }
      if (opts.reducedMotion) {
        window.matchMedia = (q) => ({
          matches: /prefers-reduced-motion:\s*reduce/.test(q), media: q,
          addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {},
        });
      }
    },
  });
  return { window: dom.window, posted };
}

function entryPoints(file) {
  const src = fs.readFileSync(path.join(SHELLS, file), 'utf8');
  const script = src.match(SCRIPT_BLOCK)[1];
  return [...script.matchAll(ENTRY_POINT)].map((m) => m[1]);
}

function label(fn, args) {
  const shown = args.map((a) => {
    const s = typeof a === 'string' ? a : JSON.stringify(a);
    return s.length > 24 ? s.slice(0, 21) + '...' : s;
  });
  return fn + '(' + shown.join(', ') + ')';
}

// Every shell that carries an inline script must appear in FIXTURES. Without this, the harness
// iterates the fixtures rather than the shells, so a NEW shell is not "uncovered" - it is invisible,
// and the run still prints "0 failed". That is family A (the instrument cannot register a failure),
// sitting inside the one instrument whose own header promises it cannot go quietly stale. Found while
// adding the first-run tour shell, which would have been the first surface to slip through it.
function unfixturedShells() {
  return fs
    .readdirSync(SHELLS)
    .filter((f) => f.endsWith('.html'))
    .filter((f) => !FIXTURES[f])
    .filter((f) => SCRIPT_BLOCK.test(fs.readFileSync(path.join(SHELLS, f), 'utf8')));
}

let failures = 0;
let checked = 0;

// WHAT A `text` ROW MEANS. It used to be a substring of the raw text, so "1 step" passed on "1 steps" and an empty string passed on anything: Brief DH's
// rule breaks stayed green on both. Now an empty string asserts the element is empty, and anything else must stand as its own words, the whole text or
// a run of it with no word carrying on either side. A hyphen counts as part of a word, so STEP-A does not pass on STEP-AB or STEP-A-B. A row that does mean a substring says so, in `textContains`.
const normText = (s) => String(s == null ? '' : s).replace(/\s+/g, ' ').trim();
const WORD_CHAR = /[\p{L}\p{N}_-]/u;
function textReads(actual, want) {
  const a = normText(actual);
  const w = normText(want);
  if (!w) return !a;
  for (let at = a.indexOf(w); at >= 0; at = a.indexOf(w, at + 1)) {
    if (!WORD_CHAR.test(a.charAt(at - 1)) && !WORD_CHAR.test(a.charAt(at + w.length))) return true;
  }
  return false;
}
// What a row's `text` and `textContains` found wrong, or null. A row that means a substring says so ([selector, substring]), and an empty one is refused, because everything contains it.
function textProblem(document, expect) {
  for (const [sel, want] of expect.text || []) {
    const el = document.querySelector(sel);
    if (!el) return 'no element matches ' + sel;
    if (!textReads(el.textContent, want)) return sel + ' reads ' + JSON.stringify(normText(el.textContent).slice(0, 60)) + ', not ' + JSON.stringify(want);
  }
  for (const [sel, sub] of expect.textContains || []) {
    const el = document.querySelector(sel);
    if (!el) return 'no element matches ' + sel;
    if (!normText(sub)) return 'textContains on ' + sel + ' asks for an empty string, which proves nothing; text with an empty string asserts the element is empty';
    if (normText(el.textContent).indexOf(normText(sub)) < 0) return sel + ' reads ' + JSON.stringify(normText(el.textContent).slice(0, 60)) + ', with no ' + JSON.stringify(sub);
  }
  return null;
}
// The comparison's own control: an instrument that has only ever said yes has not been shown to measure.
for (const [actual, want, reads] of [
  ['', '', true], ['  \n ', '', true], ['x', '', false],
  ['1 step', '1 step', true], ['1 steps', '1 step', false], ['  1\n step ', '1 step', true],
  ['2 of 3', '2 of 3', true], ['12 of 30', '2 of 3', false], ['Step 2 of 3.', '2 of 3', true],
  ['STEP-A', 'STEP-A', true], ['STEP-AB', 'STEP-A', false], ['STEP-A-B', 'STEP-A', false], ['XSTEP-A', 'STEP-A', false],
  ['a STEP-A b STEP-AB', 'STEP-A', true], ['STEP-AB a STEP-A', 'STEP-A', true], ['', 'x', false],
]) {
  if (textReads(actual, want) !== reads) {
    console.log('  FAIL  the text check says ' + !reads + ' for ' + JSON.stringify(actual) + ' reading ' + JSON.stringify(want));
    failures++;
  }
}
{
  const { document } = new JSDOM('<p id="a">1 steps</p><p id="b">  </p>').window;
  for (const [expect, holds] of [
    [{ text: [['#a', '1 steps']] }, true], [{ text: [['#a', '1 step']] }, false], [{ text: [['#b', '']] }, true], [{ text: [['#a', '']] }, false], [{ text: [['#gone', 'x']] }, false], [{ text: [['#gone', '']] }, false],
    [{ textContains: [['#a', '1 step']] }, true], [{ textContains: [['#a', '9']] }, false], [{ textContains: [['#a', '']] }, false], [{ textContains: [['#b', ' ']] }, false], [{ textContains: [['#gone', 'x']] }, false],
    [{}, true],
  ]) {
    if ((textProblem(document, expect) === null) !== holds) {
      console.log('  FAIL  the text check says ' + !holds + ' for ' + JSON.stringify(expect));
      failures++;
    }
  }
}

for (const file of unfixturedShells()) {
  console.log('=== ' + file);
  console.log('  FAIL  the shell carries an inline script and has no FIXTURES entry, so nothing here is');
  console.log('        exercised. Add its entry points to FIXTURES (or declare them uncovered, with a');
  console.log('        reason) - a shell this harness cannot see is a shell it cannot gate.\n');
  failures++;
}

for (const file of Object.keys(FIXTURES)) {
  console.log('=== ' + file);

  const declared = DECLARED_UNCOVERED[file] || {};
  const fixtured = new Set(FIXTURES[file].map((f) => f[0]));

  // Staleness first: a new entry point must be fixtured or consciously declared.
  for (const fn of entryPoints(file)) {
    if (fixtured.has(fn) || declared[fn]) continue;
    console.log('  FAIL  ' + fn + '  has no fixture and is not declared uncovered');
    failures++;
  }
  for (const fn of Object.keys(declared)) console.log('  --    ' + fn + '  not covered: ' + declared[fn]);

  for (const [fn, args, expect] of FIXTURES[file]) {
    checked++;
    // A FRESH DOM per entry point: one function's damage must never mask or explain another's.
    const { window, posted } = shellDom(file, expect);
    const what = label(fn, args);

    if (typeof window[fn] !== 'function') {
      console.log('  FAIL  ' + what + '  is not defined on window');
      failures++;
      window.close();
      continue;
    }

    // outerHTML, not innerHTML: a function whose whole job is to toggle a class ON the body (the theme
    // switch) changes nothing inside it, and an innerHTML witness would call that a failure.
    let before;
    let postedBefore = 0;
    let timersScheduled = 0;
    let lastEventPrevented;
    try {
      for (const [pfn, pargs] of expect.pre || []) window[pfn](...pargs);
      // Lane D: put a value where a user would have typed it - a PROPERTY, not markup, so the entry point
      // reads it exactly as it reads the user's input. [selector, property, value].
      for (const [sel, prop, val] of expect.set || []) window.document.querySelector(sel)[prop] = val;
      if (expect.focusFirst) window.document.querySelector(expect.focusFirst).focus();
      if (expect.docUnfocused) window.document.hasFocus = () => false;
      if (expect.docFocused) window.document.hasFocus = () => true;
      before = window.document.body.outerHTML;
      postedBefore = posted.length;   // so the post assertions judge THIS call, never its setup
      window[fn](...args);
      for (const [afn, aargs] of expect.after || []) window[afn](...aargs);
      // setAfter: a value typed where the call has just made the field (Brief EI's typed answer). [selector, property, value].
      for (const [sel, prop, val] of expect.setAfter || []) window.document.querySelector(sel)[prop] = val;
      if (expect.focusArrives) { window.document.hasFocus = () => true; window.dispatchEvent(new window.Event('focus')); }
      // click: elements clicked AFTER the call (the chat header's chips post their change to the host through onclick).
      for (const sel of expect.click || []) { const target = window.document.querySelector(sel); if (!target) throw new Error('no element to click: ' + sel); target.click(); }
      // events: an event carrying what the browser would (clipboardData, dataTransfer), for behaviour that lives in a listener.
      for (const [sel, type, props] of expect.events || []) { const target = window.document.querySelector(sel); if (!target) throw new Error('no element for the event: ' + sel); const ev = new window.Event(type, { bubbles: true, cancelable: true }); Object.assign(ev, props); target.dispatchEvent(ev); lastEventPrevented = ev.defaultPrevented; }
      // keys: KeyboardEvents dispatched on the document AFTER the call, for a shell whose behaviour lives in a
      // listener rather than in a named function (the tour's arrows and Escape).
      for (const k of expect.keys || []) window.document.dispatchEvent(new window.KeyboardEvent('keydown', { key: k, bubbles: true }));
      timersScheduled = (window.__timers || []).length;
      if (expect.flushTimers) for (const t of (window.__timers || []).splice(0)) t.fn();
    } catch (e) {
      console.log('  FAIL  ' + what + '  threw: ' + (e && e.message));
      failures++;
      window.close();
      continue;
    }
    const after = window.document.body.outerHTML;

    let ok = true;
    let why = '';
    if (expect.sel) {
      const el = window.document.querySelector(expect.sel);
      if (!el) { ok = false; why = 'no element matches ' + expect.sel; }
      else if (expect.nonEmpty && !el.innerHTML.trim()) { ok = false; why = expect.sel + ' stayed empty'; }
    }
    if (ok && expect.changed && before === after) { ok = false; why = 'the document body did not change'; }
    if (ok && expect.contains && after.indexOf(expect.contains) < 0) {
      ok = false; why = 'the document never showed ' + JSON.stringify(expect.contains);
    }
    // #883: what the document must NEVER show - a claim the product must not make, whatever the data. The
    // other half of `contains`, for removals: a removal is only proven by a witness that looks for it.
    //
    // It reads the RENDERED document with every <script> removed, never `after`. Each shell's script sits
    // inside <body>, so body.outerHTML carries the script's SOURCE - and its first run reported "soon" as
    // shown when the only "soon" left was a code comment explaining why the badge was gone. An instrument
    // reporting its own reading as the subject's (family D); sorted before reporting, and fixed here.
    if (ok && expect.excludes) {
      const shown = window.document.body.cloneNode(true);
      shown.querySelectorAll('script').forEach((s) => s.remove());
      const rendered = shown.outerHTML;
      for (const bad of [].concat(expect.excludes)) {
        if (rendered.indexOf(bad) >= 0) { ok = false; why = 'the document showed ' + JSON.stringify(bad); break; }
      }
    }
    if (ok && expect.posts && posted.length === 0) { ok = false; why = 'nothing was posted to the host'; }
    // Lane D (#153): a COMPUTED style, for the one class of defect a DOM witness cannot see - the markup is
    // right and the rendering is wrong. The two-line message that showed as one line had perfect markup.
    // Only rules in the shell's OWN style block are visible here (see the header).
    if (ok && expect.computed) {
      const [sel, prop, want] = expect.computed;
      const el = window.document.querySelector(sel);
      const got = el ? window.getComputedStyle(el)[prop] : undefined;
      if (!el) { ok = false; why = 'no element matches ' + sel; }
      else if (got !== want) { ok = false; why = sel + ' ' + prop + ' is ' + JSON.stringify(got) + ', not ' + JSON.stringify(want); }
    }
    // Lane D: WHAT was posted, exactly. `posts` only checks that SOMETHING was - and the old single-line
    // composer, given two lines, posted "chat:line oneline two" (the input strips the newline and welds
    // the words), which `posts` scores as a pass. This call must post exactly one message, equal to this.
    const callPosts = posted.slice(postedBefore);
    if (ok && expect.postsExactly !== undefined) {
      if (callPosts.length !== 1 || callPosts[0] !== expect.postsExactly) {
        ok = false; why = 'posted ' + JSON.stringify(callPosts) + ', not exactly ' + JSON.stringify([expect.postsExactly]);
      }
    }
    // ... and the negative: a key that must NOT send (Shift+Enter, an IME commit) posts nothing at all.
    if (ok && expect.prevented !== undefined && lastEventPrevented !== expect.prevented) {
      ok = false; why = 'the event was ' + (lastEventPrevented ? '' : 'not ') + 'prevented, and the row wants ' + (expect.prevented ? 'it prevented' : 'it left alone');
    }
    if (ok && expect.postsNothing && callPosts.length !== 0) {
      ok = false; why = 'posted ' + JSON.stringify(callPosts) + ' when it must post nothing';
    }
    // ... and what is left in the box afterwards: [selector, expected value].
    if (ok && expect.valueIs) {
      const [sel, want] = expect.valueIs;
      const el = window.document.querySelector(sel);
      if (!el) { ok = false; why = 'no element matches ' + sel; }
      else if (el.value !== want) { ok = false; why = sel + ' holds ' + JSON.stringify(el.value) + ', not ' + JSON.stringify(want); }
    }
    // Appended expectations (all opt-in). Each states what it saw, so a failure names its own cause.
    // postsAll (the tour's rows): EVERY message the host received across the whole run, in order, as an array -
    // distinct from postsExactly above, which judges the ONE message THIS call posted. Two names, two questions.
    if (ok && expect.postsAll && JSON.stringify(posted) !== JSON.stringify(expect.postsAll)) {
      ok = false; why = 'posted ' + JSON.stringify(posted) + ', expected exactly ' + JSON.stringify(expect.postsAll);
    }
    for (const needle of (ok && expect.containsAll) || []) {
      if (after.indexOf(needle) < 0) { ok = false; why = 'the document never showed ' + JSON.stringify(needle); break; }
    }
    if (ok && expect.notContains && after.indexOf(expect.notContains) >= 0) {
      ok = false; why = 'the document showed ' + JSON.stringify(expect.notContains) + ' and must not';
    }
    if (ok) {
      const problem = textProblem(window.document, expect);
      if (problem) { ok = false; why = problem; }
    }
    // Brief DG: text that must NOT be on the page ([selector, substring]), for a sentinel the host sends and the shell must never draw.
    for (const [sel, sub] of (ok && expect.notText) || []) {
      const el = window.document.querySelector(sel);
      if (el && el.textContent.indexOf(sub) >= 0) { ok = false; why = sel + ' draws ' + JSON.stringify(sub) + ', which it must never draw'; break; }
    }
    // Brief BD: an attribute that carries text (a control's title), [selector, attribute, substring]. An empty string asserts the attribute is empty, because everything contains it.
    for (const [sel, name, sub] of (ok && expect.attr) || []) {
      const el = window.document.querySelector(sel);
      if (!el) { ok = false; why = 'no element matches ' + sel; break; }
      const got = el.getAttribute(name) || '';
      if (sub === '' ? got !== '' : got.indexOf(sub) < 0) { ok = false; why = sel + ' ' + name + ' reads ' + JSON.stringify(got.slice(0, 60)) + ', not ' + JSON.stringify(sub); break; }
    }
    if (ok && expect.absent && window.document.querySelector(expect.absent)) { ok = false; why = expect.absent + ' is still present'; }
    if (ok && expect.timerScheduled && timersScheduled === 0) { ok = false; why = 'nothing scheduled a timer, so nothing would ever stop it'; }
    // restVisible: EVERY element of the active scene RESTS visible - not transparent, not hidden, not display:none - with
    // no animation running. The brief's rule ("motion only plays a sequence from a visible state, never parks something at
    // opacity 0"), checked against the tour's own <style> block (the shared layers stay invisible to this harness, as its
    // header says). An element marked data-transient (the thinking dots) exists only WHILE a sequence plays, and is exempt
    // by name, so the exemption is a visible decision in the markup rather than a hole in this check.
    if (ok && expect.restVisible) {
      const scene = window.document.querySelector('.scene.on');
      if (!scene) { ok = false; why = 'no active scene'; }
      else for (const el of [scene, ...scene.querySelectorAll('*')]) {
        if (el.closest('[data-transient]')) continue;
        const cs = window.getComputedStyle(el);
        if (cs.opacity === '0' || cs.visibility === 'hidden' || cs.display === 'none') {
          ok = false; why = 'at rest, <' + el.tagName.toLowerCase() + ' class="' + el.className + '"> is ' +
            (cs.display === 'none' ? 'display:none' : cs.visibility === 'hidden' ? 'visibility:hidden' : 'opacity:0');
          break;
        }
      }
    }

    // A row built to fail, naming the words it must fail with. It is the wiring's control: with the check it exercises not running, every other row would simply pass.
    if (expect.mustFail) {
      const asBuilt = !ok && why.indexOf(expect.mustFail) >= 0;
      why = asBuilt ? '' : 'this row is built to fail with ' + JSON.stringify(expect.mustFail) + (ok ? ', and it passed, so what it exercises is not running' : ', and it failed with: ' + why);
      ok = asBuilt;
    }

    console.log('  ' + (ok ? ' OK ' : 'FAIL') + '  ' + what + (ok ? '' : '  ' + why));
    if (!ok) failures++;
    window.close();
  }
  console.log('');
}

// ---------------------------------------------------------------------------------------------
// THE TOUR'S SOURCE, SCANNED. The sentinel fixtures prove that what the plugin pushes ARRIVES. They cannot prove the
// other half - that the REAL strings were not also typed into the shell, where they would go stale in silence the first
// time a verb is reworded. So this reads the plugin's own strings out of the C# that holds them and fails if any appears
// in tour.html: the verb names, each verb's line and TAG, the trust badges, the consent card's labels and titles - and a
// chord typed out ("Ctrl+", "Right-click"). "A tagline typed into tour.html must make a test fail" is this.
//
// And the tour's ONLY way of acting is a short message to the plugin. Every literal it can send is held to
// {close, settings, docs, keys, finish}: none of them is a verb, which is the "a first-run surface must not spend a new user's
// first money" rule as a gate rather than a comment.
//
// Both scans have a FLOOR - a scan that finds almost nothing FAILS instead of passing - so a moved or reformatted C#
// file cannot quietly turn this into a check of nothing.
function stripCsComments(s) { return s.replace(/\/\*[\s\S]*?\*\//g, '').replace(/(^|[^:"])\/\/[^\n]*/g, '$1'); }
function stripShellComments(s) {
  return s.replace(/<!--[\s\S]*?-->/g, '').replace(/\/\*[\s\S]*?\*\//g, '').replace(/(^|[\s;{}])\/\/[^\n]*/g, '$1');
}
const csLiterals = (s) => [...s.matchAll(/"((?:[^"\\]|\\.)*)"/g)].map((m) => m[1]);
// BRIEF EO UNIT 3: RIGHT IS GREEN, PARTLY AMBER, WRONG RED. The rows above prove the deck's verdict classes are on the page; this proves the library colours them from the
// verdict tokens and not from the amber of the chrome, so a later restyle that turns a wrong answer amber again fails here.
console.log('=== the deck\'s verdict colours');
{
  const css = fs.readFileSync(path.join(SHELLS, 'dwight-components.css'), 'utf8').replace(/\r\n/g, '\n');
  const rules = [
    ['.deck-turn.right', '--ok-tint', '--ok'], ['.deck-turn.partly', '--warn-tint', '--warn'], ['.deck-turn.wrong', '--err-tint', '--err'],
    ['.deck-tick.right', '--ok'], ['.deck-tick.partly', '--warn'], ['.deck-tick.wrong', '--err'],
    ['.deck-stamp.right', '--ok-ink', '--ok'], ['.deck-stamp.partly', '--warn-ink', '--warn'],
    ['.deck-opt.true', '--ok'], ['.deck-opt.false', '--err'],
  ];
  for (const [sel, ...tokens] of rules) {
    checked++;
    const m = css.match(new RegExp('(^|\\n)' + sel.replace(/\./g, '\\.') + '\\{([^}]*)\\}'));
    const body = m ? m[2] : '';
    const missing = tokens.filter((t) => !body.includes('var(' + t + ')'));
    if (!m || missing.length || body.includes('var(--accent')) {
      failures++;
      console.log('  FAIL  ' + sel + (m ? ' does not take ' + (missing.join(', ') || 'only the verdict tokens') : ' has no rule of its own in the library'));
    }
  }
  console.log('   OK   ' + rules.length + ' verdict rules looked at');
}

console.log('=== tour.html source scans');
{
  const read = (...p) => stripCsComments(fs.readFileSync(path.join(...p), 'utf8'));
  const verbs = read('Dwight', 'DwightVerbs.cs');
  const core = read('Dwight', 'UI', 'FirstRunCore.cs');
  const consent = read('Dwight', 'Agent', 'ConsentCard.cs');
  const region = (src, from, to) => { const i = src.indexOf(from); if (i < 0) return ''; const j = src.indexOf(to, i); return src.slice(i, j < 0 ? undefined : j); };

  const held = new Set();
  csLiterals([...verbs.matchAll(/public const string \w+ = "[^"]*";/g)].map((m) => m[0]).join('\n')).forEach((l) => held.add(l));  // the names
  csLiterals(region(verbs, 'public static readonly Verb[] All', '};')).forEach((l) => held.add(l));                                 // lines + tags
  csLiterals(region(core, 'Trust =', '};')).forEach((l) => held.add(l));                                                            // trust badges
  for (const c of ['ApproveLabel', 'ReasonLabel']) csLiterals(region(core, c + ' =', ';')).forEach((l) => held.add(l));             // card labels
  csLiterals(region(consent, 'public static string Title(string kind)', '\n    }')).forEach((l) => held.add(l));                   // card titles
  for (const c of ['MoreLabel', 'CheckedLine']) csLiterals(region(consent, c + ' =', ';')).forEach((l) => held.add(l));              // the waiting card's words

  const shell = stripShellComments(fs.readFileSync(path.join(SHELLS, 'tour.html'), 'utf8'));
  const FLOOR = 24;   // 5 names + 5 lines + 5 tags + 3 badges + 2 labels + 8 titles = 28 today
  checked++;
  if (held.size < FLOOR) {
    console.log('  FAIL  the scan found only ' + held.size + ' plugin string(s) to look for (expected at least ' + FLOOR + ')');
    console.log('        - a C# file moved or was reformatted, and this scan would otherwise be checking nothing.');
    failures++;
  } else console.log('   OK   looked for ' + held.size + ' plugin strings in tour.html');

  for (const lit of held) {
    if (lit.length < 4 || !shell.includes(lit)) continue;
    checked++; failures++;
    console.log('  FAIL  tour.html contains ' + JSON.stringify(lit) + ', which the plugin holds and PUSHES - a typed copy goes stale in silence');
  }
  checked++;
  if (/\b(Ctrl|Shift|Alt)\s*\+|Right-click/.test(shell)) {
    failures++;
    console.log('  FAIL  tour.html contains a typed chord (Ctrl+ / Shift+ / Alt+ / Right-click); it is pushed from DwightChord');
  } else console.log('   OK   no chord typed into tour.html');

  const ALLOWED = new Set(['close', 'settings', 'docs', 'keys', 'finish']);
  const sends = new Set();
  for (const m of shell.matchAll(/dwightSend\(([^)]*)\)/g))
    for (const l of m[1].matchAll(/'([^']*)'|"([^"]*)"/g)) sends.add(l[1] !== undefined ? l[1] : l[2]);
  checked++;
  if (sends.size < 3) {
    failures++;
    console.log('  FAIL  the scan found only ' + sends.size + ' message(s) the tour can send (expected at least 3) - it would be checking nothing');
  } else console.log('   OK   the tour can send: ' + [...sends].sort().join(', '));
  for (const s of sends) if (!ALLOWED.has(s)) {
    checked++; failures++;
    console.log('  FAIL  tour.html can send ' + JSON.stringify(s) + ', which is not one of {close, settings, docs, keys, finish}');
  }

  // DARK LINE-WORK MUST STAY LEGIBLE. The tour's dark outlines and hard shadows are ONE role, --line in the dark block of dwight-tokens.css,
  // chosen by eye and flippable in a single line - which is exactly why the bound cannot be left to the eye. Line-work that
  // depicts components has to stay readable, not merely look softer, and WCAG 1.4.11 sets the floor for the outlines of
  // interface components at 3:1. Read from the real token file, against every surface the line-work touches (the card, the
  // scene ground behind it, the title bar the window border meets). "Calmer still" than the floor fails HERE, on purpose:
  // lowering MIN_LINE_CONTRAST is then a visible decision, not a side effect of a one-line flip.
  const MIN_LINE_CONTRAST = 3;
  const tokens = fs.readFileSync(path.join(SHELLS, 'dwight-tokens.css'), 'utf8');
  const darkBlock = tokens.slice(tokens.indexOf('body.dark{'));
  const lightBlock = tokens.slice(0, tokens.indexOf('body.dark{'));
  // Resolve a token the way the cascade does. Since #45 the surface roles are ALIASES declared once on body
  // (--surface:var(--s2)) that follow whichever scale the theme sets, so body.dark no longer restates them as hex:
  // a literal in the dark block wins; otherwise follow var(--x) - from the dark block first, then the light one -
  // into the dark scale. Bounded, so a cycle resolves to null and the check fails rather than spins.
  const tok = (n, depth = 0) => {
    if (depth > 6) return null;
    const def = (b) => b.match(new RegExp('--' + n + ':\\s*(#[0-9A-Fa-f]{6}|var\\(--([\\w-]+)\\))'));
    const m = def(darkBlock) || def(lightBlock);
    if (!m) return null;
    return m[2] ? tok(m[2], depth + 1) : m[1];
  };
  const lin = (c) => { c /= 255; return c <= 0.03928 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4); };
  const lum = (hex) => { const n = parseInt(hex.slice(1), 16); return 0.2126 * lin((n >> 16) & 255) + 0.7152 * lin((n >> 8) & 255) + 0.0722 * lin(n & 255); };
  const ratio = (a, b) => { const [hi, lo] = [lum(a), lum(b)].sort((p, q) => q - p); return (hi + 0.05) / (lo + 0.05); };
  checked++;
  const lineM = /--line:\s*var\(--(s\d+)\)/.exec(darkBlock);
  const grounds = ['raised', 'surface', 'bar'].map(tok);
  if (!lineM || !tok(lineM[1]) || grounds.some((g) => !g)) {
    failures++;
    console.log('  FAIL  cannot resolve the dark line-work: expected body.dark{--line:var(--sN)} in dwight-tokens.css and the dark');
    console.log('        scale plus --raised, --surface and --bar in dwight-tokens.css - a check that cannot resolve is a failure');
  } else {
    const line = tok(lineM[1]);
    const rs = grounds.map((g) => ratio(line, g));
    if (Math.min(...rs) < MIN_LINE_CONTRAST) {
      failures++;
      console.log('  FAIL  dark line-work --' + lineM[1] + ' (' + line + ') is ' + Math.min(...rs).toFixed(2) + ':1 at its weakest, below the ' +
        MIN_LINE_CONTRAST + ':1 that outlines of interface components need (WCAG 1.4.11): ' + rs.map((r) => r.toFixed(2)).join(' / ') + ' on card / scene ground / title bar');
    } else console.log('   OK   dark line-work --' + lineM[1] + ' (' + line + ') is ' + rs.map((r) => r.toFixed(2)).join(' / ') +
      ':1 on the card / scene ground / title bar (floor ' + MIN_LINE_CONTRAST + ':1)');
  }
}
console.log('');

// ---------------------------------------------------------------------------------------------
// C# -> JS NAMES. Every fixture above proves a shell function WORKS; nothing above proves the C# window still ASKS
// for one that exists. A rename in either file passes every fixture and does nothing at runtime, because
// ExecuteScript swallows the ReferenceError - the dock's ring would simply never draw. So for the two surfaces the
// first-run tour owns, every Run("dwight...(") the window class makes must name a function its shell defines, and
// mustCall is the floor: a scan that finds nothing FAILS instead of passing, which is how this instrument avoids
// being one that cannot register a failure.
const CALLERS = [
  { cs: path.join('Dwight', 'UI', 'DwightDock.cs'), shell: 'dock-shell.html',
    mustCall: ['dwightDockNotes', 'dwightDockAttention', 'dwightDockList', 'dwightDockFile', 'dwightDockExpanded', 'dwightSetTheme', 'dwightDockHistory', 'dwightChipState', 'dwightDockHome', 'dwightDockNoteList'] },
  { cs: path.join('Dwight', 'UI', 'FirstRunTour.cs'), shell: 'tour.html',
    mustCall: ['dwightTourInit', 'dwightSetTheme'] },
  { cs: path.join('Dwight', 'UI', 'DwightFeedbackPanel.cs'), shell: 'feedback.html',
    mustCall: ['dwightFeedbackInit', 'dwightSetTheme'] },
  { cs: path.join('Dwight', 'UI', 'DwightNotePanel.cs'), shell: 'note.html',
    mustCall: ['dwightNoteInit', 'dwightSetTheme', 'dwightSetStatus', 'dwightNotePin'] },
  { cs: path.join('Dwight', 'UI', 'WalkBar.cs'), shell: 'walk-bar.html',
    mustCall: ['dwightWalkStep', 'dwightSetTheme'] },
];
console.log('=== C# calls into shells');
for (const c of CALLERS) {
  const called = new Set([...fs.readFileSync(c.cs, 'utf8').matchAll(/Run\(\$?"(dwight[\w$]*)\(/g)].map((m) => m[1]));
  const defined = new Set(entryPoints(c.shell));
  for (const fn of c.mustCall) {
    checked++;
    const ok = called.has(fn) && defined.has(fn);
    const why = !called.has(fn) ? c.cs + ' no longer calls it' : !defined.has(fn) ? c.shell + ' does not define it' : '';
    console.log('  ' + (ok ? ' OK ' : 'FAIL') + '  ' + path.basename(c.cs) + ' -> ' + fn + (ok ? '' : '  ' + why));
    if (!ok) failures++;
  }
  for (const fn of called) {
    if (defined.has(fn) || c.mustCall.includes(fn)) continue;
    checked++; failures++;
    console.log('  FAIL  ' + path.basename(c.cs) + ' calls ' + fn + ', which ' + c.shell + ' does not define');
  }
}
console.log('');

console.log(checked + ' entry-point call(s) exercised across ' + Object.keys(FIXTURES).length +
            ' shell(s); ' + failures + ' failed.\n');

// THE UI SYSTEM (Brief BK). The source audit, then the measuring leg in a real browser, which needs a Chromium as the
// token-alias gate does. Each tests itself first, with a planted stray value that must fail.
failures += require('./ui-style-audit.js').run();
failures += require('./render-ui-sheet.js').run();
const measured = require('child_process').spawnSync(process.execPath, [path.join(__dirname, 'ui-measure.js')], { stdio: 'inherit' });
if (measured.status !== 0) failures++;
// Brief FN: the Focus bar's height is what its words need, at 1, 1.25 and 1.5, with the old Range measure planted back as its control.
const barHeight = require('child_process').spawnSync(process.execPath, [path.join(__dirname, 'check-walk-bar-height.js')], { stdio: 'inherit' });
if (barHeight.status !== 0) failures++;
// Brief FN: the note editor and Report a problem open with nothing to scroll at their ClientSize, with the old sizes planted back as the control.
const windowFit = require('child_process').spawnSync(process.execPath, [path.join(__dirname, 'check-window-fit.js')], { stdio: 'inherit' });
if (windowFit.status !== 0) failures++;

if (failures) process.exit(1);
