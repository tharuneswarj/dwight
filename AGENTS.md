# AGENTS.md - Dwight

Instructions for an AI agent working on this codebase. Read all of it before changing anything. The same material for humans, with the reasoning behind every rule, is at <https://dwight.tha.run/docs/develop>.

## What this is

Dwight is a Grasshopper plugin for Rhino 8 on Windows, written in C#, that adds five tools to the canvas: Explain, Diagnose, Reorganise, Simplify and Build. Each does its exact work in code and uses a language model for the one part that needs judgement. The user selects components, presses Ctrl+Shift+Right-click, and picks a tool. It runs on the user's own API key, or fully local with Ollama. Apache-2.0.

## Build and test

```
dotnet build Dwight.sln
dotnet test  Dwight.sln
```

- The .NET SDK is pinned in `global.json`. The plugin targets `net7.0-windows`; Rhino 8.24 or later is needed only to RUN it, not to build or test it.
- **Every build deploys** `Dwight.gha` and its dependencies into `%APPDATA%\Grasshopper\Libraries\Dwight\`. Pass `-p:CI=true` to build without touching the user's Grasshopper install. **Use `-p:CI=true` unless you have been asked to deploy.**
- **Rhino must be closed for a deploy to land.** A running Rhino holds the file, the copy fails, and whoever tests next is testing the previous build.
- You cannot drive Rhino's UI. **A green build and a green suite are your bar; testing in Rhino is a human's step.** Say so rather than implying the feature was exercised.
- Report per-project test counts, and a total that equals their sum. A total alone hides a project that stopped running.

## The one rule everything follows from

**The language model supplies semantic judgement. The plugin computes everything exact.**

Every id, coordinate, bound, segmentation, and validation is deterministic C#. If a feature needs the model to emit a coordinate or a component guid, the feature is mis-specified - move that work into the plugin. If a failure class is deterministic and the plugin can compute the answer, the plugin computes it; never spend a model round-trip or a prompt instruction on something code can decide.

## Laws you must not break

Each was paid for by a defect that shipped or nearly did. They read as caution until you know the reason - so if one seems unnecessary, assume you are missing the reason and ask.

1. **A consent card is a contract about scope.** What the card names is what the action may touch, enforced in code. Correct execution of the wrong scope is still the wrong scope, and no behaviour guard will catch it.
2. **The card's consequence line contains no word the model wrote.** The plugin writes what will happen, from the action's kind. The model's reason is shown beneath it, escaped and labelled. Never pass model text into anything that decides what an approval does.
3. **Every mutation returns a verdict and reverts itself when a leg fails** - preservation, completeness, solve health. An edit that cannot be checked is not kept.
4. **No silent fallback under a verdict.** A lookup either resolves exactly or refuses, naming what was available. Never default to "the first one".
5. **A refusal is a claim.** Its stated reason must be true in every state that can reach it. Test refusals like any other output.
6. **Never invent a Grasshopper component.** Only what `ComponentServer` enumerates on that machine. Resolve every name the model returns before placing anything.
7. **One undo record per user request, and every canvas change on the UI thread** via `ScheduleSolution`. Grasshopper is not thread-safe. Never crash Rhino - fail safe.
8. **A published tool descriptor is a contract.** The agent's tool schemas in `Dwight/Agent/AgentToolCatalog.cs` are what an MCP client will see, and the parser is tested against them. A schema that advertises something the parser does not accept is a defect.

## Never

- Put an API key anywhere but the Windows credential store - no settings file, no log, no test fixture.
- Commit a `.gh` or `.ghx` file. Real Grasshopper definitions are usually someone's client work.
- Add telemetry or any network call outside `Dwight/Llm/`. The only egress is to the provider the user configured.
- Weaken a verification to make a test pass. If a check fails, the check is usually right.
- Rewrite a historical record or an old decision. Supersede it with a new one that points back.

## Where things are

| Folder | What lives there |
|---|---|
| `Dwight/Agent/` | The Build chat's agent: tool surface, consent card, build and rebuild, verdicts. Start at `AgentToolCatalog`. |
| `Dwight/Simplify/` | Simplify, to-script, the verification stack, the compile gate. |
| `Dwight/Data/` | What is read from the canvas, the behaviour fingerprint, the payload contract. |
| `Dwight/Llm/` | Providers behind `ILlmProvider`, structured output, thinking controls. The only place that talks to the network. |
| `Dwight/Layout/` `Dwight/Segment/` `Dwight/Labelling/` | Reorganise: layout engine, segmenter, the naming call. |
| `Dwight/Session/` | Per-file history and threads, persisted to disk. |
| `Dwight/UI/` | WebView2 panels under a strict content policy, with a native fallback. |
| `tests/` | Eight headless test projects. |

Safety-critical logic is kept free of Grasshopper so it tests without Rhino. **New logic belongs in that headless core, with its test** - and the test must be seen to fail before the change.

## Before you say you are done

- The build is green and every test project passes; state the counts.
- Every new guard has been seen to fail at least once. A guard nobody has watched fail has not been shown to guard anything.
- On a first run of a new test or instrument, sort every failure into "the instrument is wrong" versus "the product is wrong" before reporting any of them.
- You have not described a feature as working that you could only build, not run.

## Writing conventions

Hyphens, never em dashes. Comments state WHY, not what. Keep changes minimal - boring and correct over clever.
