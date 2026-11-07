# Contributing to Dwight

Thanks for your interest. Dwight is a Grasshopper (Rhino 8) plugin that adds five tools to the canvas, Apache-2.0, on your own API key or fully local with Ollama. [`AGENTS.md`](AGENTS.md) holds the laws the code keeps and the reason behind each; read it before changing anything.

## Prerequisites

- A clean clone builds and passes the whole suite without Rhino installed: the Grasshopper and RhinoCommon assemblies come from the `Grasshopper` NuGet package, and `EnableWindowsTargeting` handles the Windows target. Rhino 8.24+ is needed only to run the plugin.
- The .NET SDK pinned in [`global.json`](global.json). The plugin targets `net7.0-windows`; `EnableWindowsTargeting` lets it build on any Windows machine without Rhino installed.
- Rhino 8 (8.24 or later) to actually run the plugin. The headless test projects need no Rhino.
- Node.js and Python 3 for the gate scripts under `tools/` (CI uses the runner's preinstalled versions), and Chrome or Chromium on the PATH for the token-alias gate.

```
dotnet build Dwight.sln -p:CI=true
dotnet test  Dwight.sln
```

A build without `-p:CI=true` also deploys `Dwight.gha` and its dependencies into `%APPDATA%\Grasshopper\Libraries\Dwight\`. Close Rhino first, or the copy fails and the next person testing is testing the previous build. You cannot drive Rhino's UI from a script: a green build and a green suite are the bar for a pull request, and testing in Rhino is a human step.

## The CI gates

CI (`.github/workflows/ci.yml`, a Windows runner) runs these in order, and every one must pass:

1. **Build** - `dotnet build Dwight.sln -c Debug`.
2. **Test** - `dotnet test Dwight.sln -c Debug`, all eight headless test projects.
3. **Shell JavaScript syntax** - `node tools/check-shell-js.js`. The WebView shells carry inline JavaScript no compiler sees; a parse error defines no functions and fails silently.
4. **Control-character gate** - `node tools/check-source-chars.js`. No invisible control byte in source or prompts.
5. **Product copy gate** - `node tools/check-product-copy.js`. No user-visible string promises a future of the product. It runs its own committed controls first.
6. **Shell render smoke harness** - `npm ci`, then `node tools/check-shell-render.js`. Every window-facing entry point in the shells is called with fixture data and must produce its DOM change. It then runs three legs in a headless Chrome: `tools/ui-measure.js`, `tools/check-walk-bar-height.js` (the Focus bar asks for the height its words need, at 1, 1.25 and 1.5) and `tools/check-window-fit.js` (the note editor and Report a problem open with nothing to scroll).
7. **Token-alias gate** - `node tools/check-token-aliases.js`. Every design-token alias resolves to its target in light and dark themes (needs a headless Chrome).
8. **Python undefined-name gate** - `python tools/check-python-names.py`.
9. **Vulnerable-package gate** - `dotnet list Dwight.sln package --vulnerable --include-transitive` must report nothing. Keep dependencies patched.

CI runs on pull requests that are **ready for review**. Draft pull requests are skipped, so mark a PR ready when you want it checked: a draft with no run is not a draft that passed. Pushes to `main` that change only documentation are skipped.

`package.json` exists only to install jsdom for the shell render harness.

## How the code is organised

- The **deterministic core** is the spine: the model supplies only semantic judgement; the plugin computes every id, coordinate, tree signature, segmentation and validation. If a change asks the model to emit a coordinate or a GUID, it is mis-designed - move that work into C#.
- The safety-critical services (`Dwight/Layout/`, `Dwight/Segment/`, `Dwight/Labelling/`, the behaviour fingerprint) keep a **Grasshopper-free core** that link-compiles into a test project, so they are unit-tested outside Rhino. New logic there belongs in the headless core with tests.
- The only network egress is in `Dwight/Llm/`, to the provider the user configured.

## Expectations for a change

1. **Tests first.** New behaviour and bug fixes come with a test that fails before the change and passes after. A new guard must be seen to fail at least once.
2. **Keep to the laws in `AGENTS.md`.** If a change seems to conflict with one, raise it in the pull request rather than working around it.
3. **The safety guardrails:** canvas changes marshalled to the UI thread via `ScheduleSolution`; one undo record per user request; validate before instantiate; fail safe (never crash Rhino).
4. **Never invent Grasshopper components** - use only what `ComponentServer` enumerates.
5. **Keys and definitions stay out of the repo.** API keys live in the Windows credential store, never in a file, log or fixture. Never commit a `.gh` or `.ghx` file.
6. Keep changes precise and minimal.

## Pull requests

Fill in the pull request template. Keep a PR to one change. State per-project test counts and the CI result in the description. Say plainly if you could not test in Rhino. By contributing you agree your contribution is licensed under Apache-2.0 (section 5 of the licence).

## Reporting issues

Use the issue templates (bug, feature, question). For a Reorganise or Simplify bug, a small `.gh` that reproduces it helps - but never attach a file you cannot share publicly. For a security issue, follow [`SECURITY.md`](SECURITY.md) instead.
