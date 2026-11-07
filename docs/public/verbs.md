# The five tools

Dwight adds five tools to Grasshopper (the menu and some other pages also call them verbs). Select components, press **Ctrl+Shift+Right-click** (or use the **Dwight** menu in Grasshopper's menu bar), and choose a tool. The menu is five entries; the depth lives in the panel that opens beside the canvas. Results appear there, and the components they refer to glow on the canvas.

Each tool does its exact work in the plugin, in code, and uses a language model for the one part that needs judgement: what a region is for, why an input is empty, what to call a group, what to propose.

Explain and Diagnose only read. Reorganise, Simplify and Build can change your definition, and each does so as **one undo step** (Ctrl-Z).

## Explain

*What does this do?* Three modes:

- **Step by step**: the default for two or more components. A finished Explain arrives in the panel with the whole selection lit, and the canvas stays as it is until you ask for a step. Then the explanation happens on the canvas, one step at a time, in the order the data flows: the arrows in the panel's footer or the arrow keys, with Esc on the canvas or the close button in the footer to leave. Each step lights its components and dims the rest, and the **Dim canvas** switch, in the panel, on the Focus bar and in Settings under Explain, turns the dimming on or off at once. Minimising Dwight's window takes the step off the canvas, and opening it again puts the same step back. Play walks it by itself, and any key or click pauses it. **Focus**, a switch beside it, gives Step by step the whole screen: the panel collapses to its chip, a bar along the bottom of the canvas tells the steps with the footer's controls and switches for Focus, Dim and the Lens, Grasshopper is maximised, and a Rhino viewport sits over the canvas's top-right corner. Turning Focus off, with the bar's close button, its Focus switch, Esc or by closing that viewport, puts every window back and leaves it on the same step in the panel. **Ask the chat about this step** opens the Explain's own chat, one for each run, with the step's sentence ready to send and it still on its step; **Back to step by step** in the chat returns to it. The panel's **Chat** button opens the same chat. Under each lit component a thin line shows the time it last took against the slowest in the selection, and the step's sentence is followed by the slowest component in that step.
- **Detailed**: the selection in depth, foregrounding the data-tree structure flowing through the wires. The default for one component.
- **Short**: a few sentences on the selection.
- **Learn**: a deck of quiz cards on your selection, one card at a time in the panel. Choose it from the Explain menu, with Step by step, for two or more components. Most cards ask why: why these components are used, why the data is a list or a single item there, why it is grafted, flattened or merged, why the definition takes this approach. Dwight writes those questions and their answers in one request, and every one is marked as Dwight's reading. At most one card asks what a port holds or how many items a wire carries, read from your solved definition, and at most one asks which component a wire reaches or which components feed one; a deck has seven cards at most. The first card says what the deck covers and how many questions it has, and nothing on the canvas moves until you press Start. Each card then takes the canvas to what it asks about and lights it; with Focus on, Grasshopper fills the screen and a viewport opens, and the panel stays as the card. You pick one answer, tick every answer that applies, or type a value. A why question can also be answered in your own words: Dwight judges them with one small request to your provider's fast model, right, partly or wrong, and that request's cost shows on the card. Every name on a card takes the canvas to that component and lights it, without selecting it. A value you are asked about is hidden on the canvas behind a hatch you can still peek past: it is practice, not a lock. When you answer, the card turns: right in green with a stamp, partly in amber, wrong in red with the true answer, a hint and why. If Dwight cannot write three questions it can check against your selection, no deck is dealt and the card says so. The deck ends with your score, a sentence on what you missed by kind and step, and three ways on: Understand, the missed questions one at a time with why, at no cost; Play again, a fresh deck of why questions on the same selection, one more request whose estimate is on the button; and Ask the chat, with the questions, your answers and the score. Under them, Try again shows the same cards in a new order at no cost, and Step by step goes through the same selection one step at a time. The score ("4 of 6 right") is kept with the Explain's row in this file's history, and the run reopened from History shows its score. Learn costs one more request per run, and says what that cost.

**What travels on the wires.** In Step by step, each step also shows what travels on the wires of the components it lights, in chips on the canvas ("96 points", "12 lists of 8", "58 of 96", "nothing"), and where data stops, the step says so. Dwight writes those words itself from the solve, with no model call and no cost.

Explain's Step by step, Detailed and Short modes send structure only: component types, your nicknames, wires and tree signatures for the selection and its neighbours. No values and no geometry. Learn sends one more request, which does carry values: see [Privacy](privacy.md).

## Diagnose

*Why is this broken or empty?* Dwight scans the selection (or the whole canvas) deterministically for errors, warnings and silent failures, traces each back to its root cause, and then explains it in plain language. It changes nothing.

Diagnose forwards Grasshopper's own error and warning text word for word. Components sometimes put a file path or a value in that text, so it is the one place something of yours can leave without a verb intending it. See [Privacy](privacy.md).

## Reorganise

*Clean this up so I can read it.* Reorganise finds the regions in your selection from the wiring (topology, not position), asks the model once to name each region and suggest how regions sit relative to one another, and lays the result out: your components move, and flat labelled coloured groups appear, with a scribble title for each.

- It never changes logic. Before it keeps a layout, the plugin compares a fingerprint of the definition's behaviour (wires, components, values) with the one it took beforehand, and rolls the layout back if they differ.
- Four arrangements are offered in the panel: **compact**, **horizontal chain**, **vertical** and **grid**. Switching between them re-runs the layout only; it does not call the model again.
- Groups fully inside your selection are replaced by the new ones; a group that extends outside your selection is left alone.
- Dwight asks whether to also create labelled inputs and outputs at the region boundaries. Say no and it only arranges.
- Without an API key it still arranges, with generic region names.

## Simplify

*Make this better or smaller.* Always shown to you first, never applied unseen. Two modes:

- **Better logic**: a minimal set of changes, grouped, each of which you can accept or leave. The aim is robustness, not component count. Dwight applies only what survives two checks: the definition's resulting structure must equal what the declared changes predict, and the outputs your selection delivers must be identical before and after. Anything that fails is reverted and reported by name. Suggestions the plugin can compute but should not apply on its own (a Merge, a Flatten or Graft) appear as advice.
- **To a script (C# or Python)**: collapses the selection into one script component. **Experimental.** The complete script source is shown before anything runs. Dwight solves the script beside your originals on the same data and compares every output; if it matches, you can replace the originals or add the script alongside them. If it cannot be verified, you get a labelled draft rather than a replacement. See [Known limitations](known-limitations.md) for the measured numbers.

## Build

*Help me build or extend this.* The tool you talk to: it reads your definition, plans a design as named regions, builds and rebuilds them, changes one slider or panel, rewires and deletes existing components (only the rows you tick), and edits one script component, and checks every build. Each change sits behind a consent card: short while it waits, a one-line receipt you can open once answered. The Timeline, one icon away from History in Dwight's window, lists every change Dwight made with its live state and walks Grasshopper's own undo stack back and forward. You can show it an image with the paper-clip, Ctrl+V or a drop: a sketch or a reference guides what it plans, and it names nothing on your canvas. The full description is in [Build](build.md).
