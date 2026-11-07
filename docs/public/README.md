# Dwight documentation

Dwight is a Grasshopper (Rhino 8) plugin that adds five tools to the canvas: Explain, Diagnose, Reorganise, Simplify and Build. Each tool does its exact work in the plugin, in code, and uses a language model for the one part that needs judgement. It runs on your own API key. These pages are what a first-time user needs.

| Page | What it covers |
| --- | --- |
| [Install](install.md) | Requirements, installing, adding your first key |
| [The five tools](verbs.md) | Explain, Diagnose, Reorganise, Simplify, Build: what each does and how to reach it |
| [Build](build.md) | The tool you talk to: what it can read, plan, build and change today, and the consent card behind every change |
| [Providers and keys](providers-and-keys.md) | Anthropic, OpenAI, Google, Ollama (local and Cloud); where keys are kept; which models were run live for the chat |
| [Privacy, per verb](privacy.md) | What each verb and each chat tool sends to a model, and what stays on your machine |
| [Known limitations](known-limitations.md) | What v1 does not do, and where each limit comes from |
| [Changelog](changelog.md) | What each version contains |

Building from source and the conventions the code keeps are in the repository's [`CONTRIBUTING.md`](../CONTRIBUTING.md) and [`AGENTS.md`](../AGENTS.md).

## Reporting a problem

Choose **Report a problem...** from the Dwight menu in the Grasshopper menu bar, or at the foot of the Ctrl+Shift+Right-click menu. A small panel shows the details Dwight will include, which you can read and select, and a box for what happened. **Open a GitHub issue** opens your browser on a new issue with that text filled in. **Write an email** does the same in your mail app, and appears only when the build carries a support address. Dwight sends nothing itself: you read the text first and decide what to send.

## One rule everything follows from

Each tool does its exact work in the plugin and takes help from a language model only for semantic judgement. Every id, coordinate, bound, segmentation and validation is deterministic C#. Where a tool changes your definition, the plugin checks the result itself, reverts what it cannot verify, and the whole action is one Ctrl-Z.
