# Install

## Requirements

- **Windows** and **Rhino 8, version 8.24 or later.** Dwight targets Rhino 8 only; Rhino 7 is not supported.
- An API key from a model provider (see [Providers and keys](providers-and-keys.md)), or a local Ollama install if you want nothing to leave your machine.

## Installing

Dwight is packaged for Rhino's package manager (Yak); the package definition is `packaging/manifest.yml` in the repository. Install the package from Rhino 8's package manager, then restart Rhino and open Grasshopper. A **Dwight** menu appears in Grasshopper's menu bar.

To install from a release download instead, or from your own build, copy `Dwight.gha` and the dependency DLLs that sit beside it into:

```
%APPDATA%\Grasshopper\Libraries\Dwight\
```

(`dotnet build Dwight/Dwight.csproj` does this copy for you, and `-p:CI=true` skips it; close Rhino first, because a running Rhino holds the file and the copy then fails.) Restart Rhino and open Grasshopper.

## First run

1. In Grasshopper choose **Dwight > Settings...** and pick a provider. Paste your API key; it is stored in the Windows Credential Manager, never in a plain file.
2. Select some components on the canvas.
3. Press **Ctrl+Shift+Right-click** on the canvas. The five verbs appear at the cursor. The same actions are under the **Dwight** menu.

The first time Dwight starts on a profile it may open a short tour; it can be closed at any point.

If something wedges, **Dwight > Restart Dwight** drops the running task and resets Dwight; a **Stop** button cancels a run in progress.

## What Dwight stores on your machine

- Your API keys: Windows Credential Manager, one entry per provider.
- `%APPDATA%\Dwight\settings.json`: your settings.
- `%APPDATA%\Dwight\sessions\`: your command history and Build conversations, one file per Grasshopper document, so they survive a restart, a move and Save-As.
- `%APPDATA%\Dwight\labelcache.json`: cached region labels, so the same shape of graph is not labelled (and billed) twice.
