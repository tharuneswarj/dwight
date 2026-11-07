# Build

Build is the tool you talk to: it reads the definition open in Grasshopper, plans named regions, builds them, and checks every build. Every change is shown on a card, approved by you or, under an approval mode you chose, automatically, one Ctrl-Z each. Open it from the **Build** entry in the menu. The conversation is its interface, and it is kept per Grasshopper document, so it is there again after a restart.

Scaffold is the map tool inside Build: it lays down empty named regions from a plan, so you can see the work before any component is placed. Build works through a set of small tools of its own. Some read, and run without asking. Everything that changes the canvas goes through a **consent card**.

## Approval

By default every change waits for your click on its card. A setting under Build in Settings chooses how a new conversation handles that, and the conversation keeps the mode it was opened with.

- Ask every time: every change waits for your click on its card.
- Ask for scripts and deletions only: Dwight applies sliders, wires, placements and builds of components on its own, shows each card marked as approved automatically, and still asks before a script runs or a component is deleted.
- Do not ask: every change Dwight can check is applied on its own; it still asks before Reorganise and Simplify. With Full control on, a script runs without asking too, and one Ctrl-Z may not undo everything it touches. Every other change is one Ctrl-Z.

A conversation keeps the mode it was opened with, so changing this does not change one that is already open.

One thing never changes with the mode: Reorganise and Simplify always ask, because they leave the chat for a tool with its own card and its own apply button. A script Dwight cannot check asks under the first two modes. Under any mode the card is still in the conversation, marked "Approved automatically" with the mode that approved it, the change is still checked, and Dwight still says what stands when it finishes, including how many changes were approved without a click. Each one is a single Ctrl-Z.

## Full control

Full control: everything Build has, plus Dwight can run a script you have read, with full access to Rhino and this definition. Dwight cannot check what such a script does, and one Ctrl-Z may not undo everything it touches. It costs more tokens, and nothing it does is proven. For people who know Grasshopper and Rhino and want speed. Turn it on only when you need it. With the Do not ask approval mode, a script runs without asking, and you read it afterwards.

It is off by default and sits in Settings under Build. A conversation keeps the setting it was opened with. When it is on, Dwight can ask to run one Python 3 script. The card is titled "Run this script?" and shows the complete code, open the first time a conversation shows one and folded after, with a summary line above it. Dwight runs exactly that text, once, when you approve. Under Do not ask it runs without asking: the card is still in the conversation, marked as approved automatically, with the whole code inside it to read afterwards. When a chat has both, it says so once: "Scripts run without asking in this chat, and one Ctrl-Z may not undo everything they touch."

The script runs in Rhino's Python 3 runtime, not inside a script component, so there is no ghenv and no scriptcontext. Dwight adds five lines before your script that import Rhino, Grasshopper and System and bind rhdoc (the Rhino document) and ghdoc (this definition). Nothing else is added, and the card says so beside the code. What the script prints goes to the report, and a failure's position is a line of the code you read.

A script is not checked. Afterwards Dwight reports what changed in the definition (components, wires, groups, errors and whether its behaviour fingerprint changed), with anything the script printed. It does not call that verified. One Ctrl-Z undoes what Grasshopper recorded: the components the script added. Anything else the script touched, in the definition or in Rhino, may not come back. If a script runs longer than its clock, Dwight says it is still running on Rhino's thread and cannot stop it.

## What it can read

Reads run on the canvas as it is now, change nothing, and send only what is listed in [Privacy](privacy.md).

- **The definition**: an overview with its regions and the flow between them, one region, one component, the wires into and out of a component, the data-tree signature on a wire, a search by name or type, and the contents of a cluster (a password-protected cluster is reported, never read).
- **What a component holds**: a slider's value and domain, a panel's text, a toggle's state, a value list's selection, internalised data (as a type, a value and a count), a script's source.
- **Faults**: Grasshopper's own error and warning messages, and a re-solve with the result.
- **Geometry, as facts and never as geometry**: type, bounding box, validity, closed or solid, counts, length, area, volume, naked-edge counts, whether objects intersect or sit near a point or box.
- **The setting around it**: the document's units and tolerances, the Grasshopper plugins you have loaded, whether the definition is saved, how the canvas is laid out, where solve time goes, and the description and ports of any installed component. When none is on your canvas, Dwight reads the ports from a temporary instance of the installed type, which is never added to your definition and never solved. If that fails or takes more than a few seconds, it says the ports are unavailable.

## Showing it an image

Attach a PNG or JPEG with the paper-clip beside the text box, paste one with Ctrl+V, or drop a file on the box. It shows above the box with its size and, on a Claude model from 4.7 on, about how many tokens it adds; on any other model it says the provider counts them. An x takes it away. One image goes with a turn, and a second replaces the first. Dwight downscales it to 1568 px on its longest edge, and keeps it with the chat on your computer.

A sketch, a floor plan or a reference tells it what you want. It names nothing on your canvas, so Dwight still plans and builds through the same tools and the same cards. A model without vision refuses the image and says its own name.

Dwight cannot see your screen, and nothing in it takes a capture of the canvas or the viewport. When a question needs the picture, take a screenshot and attach it. What leaves your machine is in [Privacy](privacy.md).

## What it can change

Each of these reaches you as a card. Nothing happens until you approve, or, under an approval mode you chose, until it is approved automatically and shown as such.

- **Plan regions** (`scaffold`): lays down a map of named, empty regions for a new design, each with a purpose, pseudo-code and the inputs and outputs it expects.
- **Build** (`build`): places and wires real components into regions you approved, with initial values (a slider's domain, value and rounding; a panel's text), and can place a C# or Python script with its complete source shown on the card. It can also place up to 12 components without a map, as one block: the card names the first five and says how many there are, and lists them all under "What this does and what it cannot". It can **rebuild** a region: it removes the components Dwight placed there that you have not edited since (naming each on the card first), builds the region again, and reconnects what those components fed, in place.
- **Change one value** (`set_value`): one existing Number Slider's domain, rounding and value, or one existing Panel's text, exactly as the card states it.
- **Change wiring and delete components** (`edit`): adds wires, removes wires and deletes existing components, as a card of rows you can tick or leave (at most 40 rows on one card). The card states the total and names what each deleted component fed. The plugin applies only the ticked rows, checks the result, and puts everything back if anything else differs. A delete that empties a group Dwight made removes that group too; a group of your own stays, and the report names it. One Ctrl-Z undoes it.
- **Edit one script** (`edit_script`): rewrites the source of one existing C# or Python script component, and may append new inputs or outputs. The card says whether the edit "preserves" the outputs (Dwight runs the new version beside the original on the same data and will not offer it if they differ) or "changes" them (Dwight checks the definition still solves and you read the diff). You see the diff and the complete new source before anything runs.
- **Hand off** to the Reorganise or Simplify flows, which carry their own preview and consent.

## Build style

**Steps per turn**, under Build in Settings, sets how many steps a chat turn may take before Dwight asks it to say where it got to: 12 by default, from 4 to 40, shown on the line under the text box. An approved build has its own budget of 30. A turn that runs out says what it read and what it was about to do, and the next turn is told what it read so it can go on from there.

A setting under Build in Settings chooses how a build goes. **Components** (the default) places every step as a component on the canvas, so sliders reach everything and Dwight checks the wiring. **Scripts** prefers one C# or Python script where the logic is a loop or a formula, with inputs and outputs still placed as components; that means far fewer steps and tokens, and the script's complete source is shown on its card before it runs. **Let Dwight choose** uses components for inputs, outputs and anything you will tweak, and a script for the algorithmic middle. A script in a region build is listed on the build card itself, under "Scripts in this build": the card shows each script's complete source, untruncated, before you approve, and Dwight builds only that exact source (a script that differs by a character is refused and needs its own card). The build card names the style it will use.

## The consent card

A card names its **scope**: the regions or the one component it covers. The plugin holds the change to that scope; a build cannot start without an approved scope and cannot reach outside it. Approving applies the change as **one Ctrl-Z**. A waiting card is short, with the reason for asking visible and the full text one click away; an answered card is a one-line receipt you can open.

Every change comes back with a **verdict** the plugin computes, never the model. A build is held to three legs: a failed preservation or completeness leg reverts it, and solve health is reported, never hidden (a region that stands but gives Grasshopper new errors is "built, with errors"; a solve that does not finish reverts the build). A value change gets a two-leg verdict, and a script edit is verified as described above. The build's three legs:

- **Preservation**: what already existed is untouched (checked against a fingerprint of the definition's behaviour).
- **Completeness**: what was asked for is present.
- **Solve health**: the definition still solves, and any new errors are counted on the verdict.

The verdict says what it proves and what it does not. A verdict reading "as asked" is evidence about structure and solve state; it is not a judgement that the design is good.

The Timeline, one icon away from History in Dwight's window, lists every change Dwight made to this file, up to the latest 200, with its live state (standing, undone, gone or position unknown), and walks Grasshopper's own undo stack back and forward, naming any changes of yours it would undo before it moves.

## Requirements

Build runs only on models the catalogue marks as able to drive it, through a provider connection that carries tools. That means a key for Anthropic, OpenAI, Google or Ollama Cloud, or Ollama running on your machine, with a model the catalogue marks as able to drive it. Each of these was run live; how far, and with which models, is in [Providers and keys](providers-and-keys.md). Dwight keeps the cost and token counts of each chat turn, shows the cost so far on the line under the text box, and holds the totals in Usage in Settings. If your chosen model or provider cannot run Build, Dwight says so before anything is sent. See [Providers and keys](providers-and-keys.md).
