# Changelog

## 1.0.2 - 8 November 2026

Five fixes to Dwight's own windows, found by taking pictures of them, and a plainer package description.

- **The Focus bar** shows a step's whole sentence. A step that ran to a second line had that line cut and faded at the bottom of the bar; the bar now grows to fit it.
- **The note editor** opens tall enough to show its last line, which says what happens to the note. It used to sit half under the buttons until you scrolled.
- **Report a problem** opens tall enough to show its last sentence, which says Dwight sends nothing itself. It too sat half under the buttons.
- **Settings, with Ollama on your machine**, lists its models under "Models on this machine". It said "Models on your key", and a local model needs no key.
- **Usage** names each model the way the rest of Settings does, "Claude Sonnet 5.5 (Anthropic)" rather than "claude-sonnet-5-5 (Anthropic)". The id is on the name's hover, and a model no longer in the catalogue is still named by its id.
- **The package manager's description** says "Open source." and no longer says "free".

## 1.0.1 - 5 November 2026

The package now carries its icon in Rhino's package manager; nothing else changed.

## 1.0.0 - 5 November 2026

The first public version. Windows, Rhino 8.24 or later. Dwight adds five tools to Grasshopper; each does its exact work in the plugin and uses a language model for the one part that needs judgement.

**The five tools**

- **Explain**: Short, Detailed and Step by step, with Learn as a deck of cards that ask about the selection, foregrounding the data-tree structure on the wires.
- **Diagnose**: a deterministic fault scan that traces each fault to its root cause and explains it; changes nothing.
- **Reorganise**: regions found from the wiring, labelled by the model, laid out with flat labelled groups; behaviour-preserving (checked against a fingerprint, rolled back on any difference). Four arrangements (compact, horizontal chain, vertical, grid) switchable without another model call; optional labelled inputs and outputs at region boundaries.
- **Simplify**: *better logic* as a minimal, selectable set of changes verified two ways (predicted structure and identical outputs), with advice for changes it will not apply itself; and *to a script* (C# or Python), **experimental**, verified beside the originals before anything changes.
- **Build**: the tool you talk to; it reads the definition, plans a design as named regions, builds and rebuilds them with a verdict on every change, changes one existing slider or panel, rewires and deletes existing components (only the rows you tick), and edits one existing script component. Every change is shown on a consent card that names its scope, approved by you or, under an approval mode you chose, automatically, and is one Ctrl-Z. Full control, off by default, is the exception: a script it runs is reported, not checked, and one Ctrl-Z may not undo everything it touches.

**Providers**: Anthropic, OpenAI, Google, Ollama local, Ollama Cloud. Keys in the Windows Credential Manager. The Build chat runs on every one of them, on the models the catalogue marks as able to drive it. It was run live on Anthropic (Sonnet 5), OpenAI (GPT-5.6 Terra, GPT-5.6 Sol, GPT-6.1 Sol and GPT-6 Astra), Google (Gemini 3.8 Flash), Ollama Cloud (GLM-5.3) and Ollama local (qwen3.5:9b, reads only). The details and limits are in [Providers and keys](providers-and-keys.md).

**Cost and tokens**: each verb run and each chat turn keeps its model, cost and token counts in the document's history. A verb's footer and a chat's own line show them, and Usage in Settings holds the totals.

**Report a problem**: a menu entry and a chord entry that open a prefilled GitHub issue or email. Dwight sends nothing itself.

**Also**: command history and Build conversations kept per Grasshopper document across restarts; a Stop button that cancels the request itself, and a Restart Dwight command; a profile and a model pin in Settings; a privacy statement per verb and per chat tool, shown in Settings and rendered from the table the tests check.

**Known limits** are listed in [Known limitations](known-limitations.md).

The dated lines below, newest first, record what changed on the way to 1.0.0.

4 November 2026: Step by step no longer says "outside this selection" twice. When the model writes those words itself after a component's number or its nickname, they are now read as the words Dwight adds, and the words twice running are always said once. When the model writes them again later in a sentence that already has them beside a component's number, the later ones are dropped, so a sentence says it once for each component. Diagnose now writes the cause itself for a component whose input has nothing wired into it: "Nothing is wired into x", with the fix "Connect a source to x". Until now the model explained it, and could call it a knock-on of another fault the wires do not connect it to.

4 November 2026: the number beside a component's name and the badge on that component on the canvas are now the same number. The canvas wears the numbers of the page you are reading: a chat's in a chat, a run's in its result and its steps. Until now a chat could name add1 (5) while the canvas showed 7 on it. Settings' About now names the build, its version and a code for the exact file, in the line Report a problem sends, and Play's tooltip says it steps on by itself.

3 November 2026: Learn now teaches the why. Most cards ask why your selection is built as it is: why a component, why a list or a single item, why a graft, a flatten or a merge, why this approach. The model writes those questions and their answers, and each is marked as Dwight's reading; at most one card is read from your solved definition and one from its wires. A why question can be answered in your own words, judged right, partly or wrong by one small request to your provider's fast model, its cost on the card. Right shows in green, partly in amber and wrong in red with why. Every name on a card takes the canvas to that component without selecting it. The last card reads what you missed by kind and step and offers Understand, Play again and Ask the chat, and Focus in Learn keeps the panel as the card instead of opening the bar.

3 November 2026: the table of what each tool sends, which the Privacy page prints, now says Step by step where it said the Walk.

3 November 2026: when no API key is stored, the tour's last step now offers How to get a key, and Settings has a link under the field where you paste a key. Both open the site's page on API keys in your browser: what a key is, how to get one and what it costs.

3 November 2026: Explain's Walk is now called Step by step, in the menus, the panel's header, the Explain deck's button and these pages. It works as it did, and a history row or note written under the old name reads as before. Lines below keep the names they were written under.

3 November 2026: a Learn deck now always mixes in a question about where a wire goes and one about which components feed another, when your selection's wires offer them, instead of filling every card with a value. The chat a deck opens gives each component one number throughout, in the questions and answers as in the steps.

3 November 2026: a run you stop after the provider has counted its request now records that request's size with its bill, so the estimate for a later stopped run learns from it as it does from a run that finished. Until now such a run taught it nothing.

3 November 2026: Learn is now a deck of quiz cards in the panel, one at a time. The first card says what the deck covers; each next card takes the canvas to what it asks about and asks what a port holds, how many items a wire carries, which component a wire reaches or which components feed one, and you pick an answer, tick every one that applies, or type a value. A right answer gets a stamp and a wrong one shows the true answer and a hint. The last card asks why the definition is built as it is and is marked as Dwight's reading; every other answer is read from your definition. The deck ends with your score, the questions you missed, and Walk this, Ask the chat and Try again, which costs nothing. A Learn run reopened from History shows its score.

3 November 2026: the version is now 0.9.0, for the soft launch to a few teammates before 1.0.0. A chat, note or history written on 0.9.0 reads whole on 1.0.0: the session store decides on its own schema number, never on the plugin version, and a test now holds that.

2 November 2026: zoomed out, a note's dot now sits at its card's corner at its full size, where a click opens it, and the chip that counts hidden notes stays at the canvas's bottom-left. Until now both were drawn through the canvas's zoom and pan a second time, so with the canvas zoomed out the dots came out a few pixels wide and away from where a click found them.

2 November 2026: the estimate for a run you stop now counts the request at the fewest tokens for its size that the same model has used in your recent runs, where it used the middle count across every model of the provider. A stopped Claude Sonnet 5.5 Explain was counted at Claude Haiku 5.5's rate and stood above its bill. The known limitations say exactly how far the estimate can be trusted, and the chat no longer calls its estimate a floor.

2 November 2026: the estimate for an Explain, Diagnose, Reorganise or Simplify you stop is now priced, as a chat turn's already was, as if its input had been read from the provider's cache, its cheapest rate. Until now it was priced as a first read, which put a run stopped while reading from the cache well above what it cost.

2 November 2026: when you stop a chat turn and send again at once, the new turn keeps its Stop button and its status line. Until now the stopped turn's late finish could free the text box under the new one while it ran.

2 November 2026: a chat turn cut off by Dwight > Restart Dwight now leaves its cost in the chat's total and in the Usage section, as a stopped turn does, and the chat says what the number is when you open it again. Until now the provider billed it and Dwight recorded nothing.

2 November 2026: on a Walk, the Focus bar's close button and Esc now turn Focus off and leave you on the same step in the panel; only Leave ends a Walk. The Dim switch acts at once, minimising Dwight's window takes the step off the canvas until you open it again, and the bar's controls are icons with switches for Focus, Dim and the Lens. Ask about a step opens the Explain's own chat, one per run, with the step's components numbered as the chat numbers them, and the Walk waits on its step. The chat a result opens now knows which action it came from and carries Simplify's and Diagnose's results too, and a Learn run reads as a lesson, with a score and the questions you missed at the end.

2 November 2026: notes are easier to find and to move. Zoomed out, a note is a small dot where it used to vanish, and dots close together merge into one with a count, which zooms in when you click it. The dock has a Notes tab listing the file's notes: click one to go to it, or start a new one there. A note's card moves when you drag its top edge, and its window has a switch to pin it to a component or leave it free. A list continues when you press Enter, and there is a numbered list beside the bullets.

2 November 2026: the Build chat keeps to its work. Asked the weather, the news or a recipe, it says in a sentence that this is outside what Dwight does and offers what it can do with your definition; it no longer answers, searches for it or raises a card for it. A model that cannot read images now names up to two that can on the same provider when it refuses an image, or the best one elsewhere if its provider has none. Settings' Steps per turn now says that a longer task, such as a build with many regions or a long script, may need more steps. And the chat can light the components it is explaining on your canvas, and dim the rest if it chooses: it points, moves the view to them, and changes nothing, and the glow ends with your next message.

2 November 2026: a chat turn you stop now leaves its cost in the chat's total and in the Usage section: the provider's count if its reply had begun and said one, otherwise a marked estimate, and the conversation says which. Until now a stopped turn recorded nothing, though the provider billed it.

2 November 2026: a Tree Lens chip whose wire is hard against a component now sits on the component's far side, level with its wire, and any chip away from its wire has a line back to it. A Walk's sentence now reads "mul (9), outside this selection" where the name used to fall after the words.

2 November 2026: the Tree Lens's chips no longer cover a step's number, a port's label or one another. A chip stays by its own wire: where the wire is too short to hold it, it sits beside the wire's end, with a short line to the wire if it needs one.

2 November 2026: links in a model's reply open only http and https destinations.

2 November 2026: a Walk on part of a definition now stays on what you selected. Its steps, what it lights, its timings, Learn and Replay all keep to your selection. A component next to it can still be named in a sentence, as outside this selection, and a wire that leaves the selection ends at its edge.

2 November 2026: a script Dwight writes can now name its ports x, y or a, the names a new script component starts with. Before, a script with an input called a could be approved and then fail to build, each attempt paid for. A name that cannot work in the script's language, such as one name used as both an input and an output in C#, is now refused before you see a card, and the reason names the port.

2 November 2026: a Walk's summary line now reads as plain words where the model marked a name in it. It used to show the backticks. A note pinned from the summary keeps the marks and draws them.

2 November 2026: you can show the Build chat an image: a sketch, a floor plan, a reference, a screenshot. Attach it with the paper-clip, paste it with Ctrl+V or drop it on the box. It shows above the box with its size and, on a Claude model from 4.7 on, about how many tokens it adds, and it goes only with the turn you send it on and that chat's later turns. Dwight still cannot see your screen, and says so when you ask it to look: take a screenshot and attach it. A model without vision says its own name and refuses the image.

1 November 2026: a region's input and output columns now read in a settled order, where before it could change from one build to the next: by where their wires go, with anything unwired after them in the order it was planned. And closing Focus's Rhino viewport now turns Focus off and puts the windows back, with the Walk carrying on in the panel on the same step.

1 November 2026: a note can now carry structure: bold, inline code, bullets and a simple table, drawn on the canvas as a card in the panel's own look, and the note's window has tools for them. A long note shows its first lines and "and more", and a click opens it whole. Reorganise, Simplify and Diagnose each offer a note on their result now, one click and never on their own, as Explain and Build already did. A note holds up to 4,000 characters; what goes to your provider with a component stays at 600, with a table as lines.

1 November 2026: whenever Dwight lays components down, inputs now stand in one column at the left and outputs in one column at the right, read from how they are wired, and the rest keeps its rows or its flow. A rebuild keeps the columns: what it kept stays where it was, and a new input takes the next free place below. A column wraps only when it stands far taller than what is beside it, and then the sentence says so.

1 November 2026: notes can be hidden: Show notes is a switch at the foot of the dock and under Notes in Settings, and the chord says Hide notes or Show notes. Hidden, no note is drawn or clickable and a small count stays in the corner of the canvas, which a click on brings them back. A note you add while they are hidden shows itself so you see it land. Hiding is about the canvas only: Dwight still reads your notes, and a note still goes to your provider with the component it is on.

1 November 2026: what Dwight draws on the canvas is easier to see, on a light canvas and a dark one. A lit component has a solid amber edge and a brighter glow; a step's wires are lit in a brighter teal, thicker than Grasshopper's own; a wire's chip is a filled pill you can read at normal zoom, and only one sits on a wire; and the rest of the definition recedes under a tint, with the step's components and wires left clear. An Every wire button, in the footer and on the Walk's bar, goes between the step's wires and every wire of the selection. So does T with the keys on the canvas, which did nothing before. T also reaches Rhino's command line, as every letter does, so the button is the sure way.

1 November 2026: a Walk now runs in the panel by default. A finished Explain arrives quietly, with no dimming and no moving of the view until you ask for a step. The bar along the bottom now belongs to Focus, a new mode that collapses the panel, opens the bar, maximises Grasshopper and puts a Rhino viewport over the canvas's corner; turning it off or leaving the Walk puts every window back. A Dim canvas switch, in the panel and in Settings under Explain, turns the dimming off.

1 November 2026: a run you stop after its request has gone out is now recorded as an estimate, marked as one, from the size of the request: the provider still bills the input, and its count never reaches Dwight. The Usage section keeps estimated cents apart from billed ones, and the run's row says "estimated". A run stopped before anything was sent costs nothing and shows nothing.

1 November 2026: Explain has a fourth way to run, Learn: a Walk that also asks you a practice question on each step that has a number or a count Dwight can read from your solved definition. The answer is hidden behind a hatch you can still peek past, a wrong answer gets a hint and one more try, and the score is kept with the Explain's row. It costs one more request per Walk, and the true values are always Dwight's own reading, never the model's.

1 November 2026: Explain on two or more components is now a Walk by default: one step at a time on the canvas, in the order the data flows, from a bar along the bottom, with Back and Next, the arrow keys and Esc. Detailed and Short are choices in the Explain menu.




1 November 2026: a Walk can play itself: Play on its bar steps on at a pace set by each step's length, and any key or click pauses it. Under each lit component a bar shows the time it last took, against the slowest in the selection, and the step's line names the slowest here. It reads Grasshopper's own timings and costs no tokens.

1 November 2026: an Explain's steps now show what travels on each wire of the components they light, in chips on the canvas: "96 points", "12 lists of 8", "58 of 96", "nothing", "stopped:" with Grasshopper's own words. T on the canvas shows every wire of the selection. Where data stops, the step says so, and a click selects what the line names. None of it costs a token.

1 November 2026: the question before a large run now counts the read and the answer, from what your own recent runs measured, and it asks at the amount you set as well as above it. The record of what Dwight has spent also keeps the size of each verb's request, in characters.

1 November 2026: without a map, Build can place up to 12 components on one card, where it was 5. The card names the first five and says how many there are, and lists them all under "What this does and what it cannot". More than 12 still wants a map.

1 November 2026: a chat turn that runs out of steps now says where it got to, and the next one is told what it had read so it can go on from there. Steps per turn is a setting under Build, from 4 to 40, and the chat's line shows it.

31 October 2026: Build can read the ports of an installed component that is not on your canvas, from a temporary instance that never joins the definition, so it no longer has to guess them for Honeybee, Ladybug and the like.

31 October 2026: Dwight has one window. A conversation, History (every conversation and verb result; it was Conversations) and the Timeline (what changed in this file; it was the History tab) are three views of it, opened from the Dwight mark at the bottom right of the canvas.

31 October 2026: on OpenAI the Build chat goes through the Responses endpoint, which keeps the model's reasoning across a turn's tool calls, and GPT-6.1 Sol and GPT-6 Astra can now run it. With no pin, Balanced now chooses GPT-6.1 Sol, and Quality and Ultra choose GPT-6 Astra.

31 October 2026: when a chat turn announces a change and raises no card, Dwight runs one follow-up turn that must raise it, and the approval mode decides that card as it decides any other. On Ollama, whose endpoint cannot force this, it is an ordinary follow-up, under an automatic mode only.

31 October 2026: regions a build places stand clear of each other, in the map's reading order, and your own work stays where it is.

31 October 2026: the cost reads each model's own cached-input price where the catalogue has one, and Ollama Cloud says that Dwight cannot see what a run cost, because it depends on your plan.

31 October 2026: with Full control and Do not ask both on, a script runs without asking. The card stays in the conversation marked as approved automatically, with the whole code inside it, and the chat says so once. The other two modes still ask.

31 October 2026: a Full control script now runs with rhdoc (the Rhino document) and ghdoc (this definition) already bound by five lines Dwight adds and says so on the card; there is no ghenv, and a failure's line number is a line of the code you read.

30 October 2026: Settings has a Usage section, which reads one record of what Dwight has spent across every file and every chat (`%APPDATA%\Dwight\spend.jsonl`), by period and by model.

30 October 2026: the dock has a History tab, which lists every change Dwight made to this file with its live state and walks Grasshopper's own undo stack back and forward, naming any changes of yours it would undo before it moves.

30 October 2026: a waiting card is short, with the reason for asking visible and the full text one click away, and an answered card is a one-line receipt you can open.

30 October 2026: Full control, an off-by-default switch in Settings under Build, lets the Build chat ask to run one Python 3 script you have read, with full access to Rhino and the definition; it always asks, it is not checked, and it reports what changed.

30 October 2026: the Build chat has three approval modes (Ask every time, Ask for scripts and deletions only, Do not ask), chosen in Settings and kept by each conversation, and a script on a card is folded behind a summary with the whole source one click away.

30 October 2026: the fifth verb is now called Build. Scaffold is the map tool inside it, which lays down empty named regions from a plan.
