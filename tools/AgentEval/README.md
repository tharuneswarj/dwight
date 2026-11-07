# AgentEval: lane A, the real agent loop with no Rhino

This console runs Dwight's real agent loop, prompt and tool descriptors on real providers, with no Rhino and no Grasshopper (Brief BY). It records what the agent would do and say. **It cannot show that a build works**, and every result says so.

## Running it

```
dotnet build tools/AgentEval/AgentEval.csproj -c Release
dotnet tools/AgentEval/bin/Release/net7.0/AgentEval.dll --rows rows.json --out ..\eval-runs\tonight --components ..\eval-runs\gh_component_list.json --cap-cents 300 --concurrency 4
```

| Argument | Default | Means |
|---|---|---|
| `--rows` | required | The rows, below |
| `--out` | required | Where results go. Keep it outside the repository |
| `--cap-cents` | 100 | The spend cap for the whole run |
| `--concurrency` | 4 | Rows running at once |
| `--components` | required | The saved component list, below. Without the argument the run refuses and says what the file is |
| `--fixtures` | `fixtures` beside the program | The canned worlds |
| `--skip-preflight` | off | Skips the one small call per provider and model |
| `--steps-per-turn` | 12 | A chat turn's steps, 4 to 40, as Settings' Steps per turn. An approved build keeps its 30. A number outside that is refused, and each result states the number |
| `--pin-for-gate` | off | Lets a row name a model the catalogue does not yet mark agentic, in memory for this run, because eligibility is by measurement. Sonnet 5.5, Opus 5.5 and Fable 5.1 need it until a gate passes |
| `--strict-thinking-check` | off | Asks Anthropic to enforce the prefix check on these models whatever the key's age. Sent only with adaptive thinking |
| `--thinking-check-control` | off | The strict check, and elision forced back on. The positive control: see below |

The cap is checked before each request and spend is added as each reply arrives, so a run can overshoot by what the requests already in flight cost. Set it below what you can spend.

## Before measuring Sonnet 5.5, Opus 5.5 or Fable 5.1

**Rebuild this program first** (PR #159 or later). An older build's guard dropped provider facts: `KeepsHistoryAppendOnly` would read false, so a long turn would elide and read as the model's 400. The same gap means `ThinksWhileForced` read false in every run before that PR, so forced OpenAI steps in earlier results ran with reasoning off, unlike the product.

**Pass `--pin-for-gate`.** Without it, rows naming these models are skipped with the product's sentence.

**A clean long turn proves little on an old key.** These models check that earlier messages are unchanged, and that is a 400 by default only on accounts created on or after 31 August 2026. So to read the elision row:

1. Run a turn past 120,000 characters on Sonnet 5.5 with `--strict-thinking-check`. It must finish with no 400 and no "trimmed from the payload".
2. Run the same row with `--thinking-check-control`. **It must fail with a 400** at the step after the first elision, naming a thinking block "bound to a different conversation". If it does not, the check is not running, and step 1 measured nothing.

In Rhino the same two switches are `Dwight.Llm.AnthropicWire.StrictPrefixCheckForGate` and `ElideAnywayForGate`, set through the bridge for that session. Nothing in the product sets them.

## A row

```json
{
  "id": "student-climate-1",
  "persona": "a good student",
  "provider": "Anthropic",
  "model": "claude-sonnet-5",
  "approvalMode": "ask",
  "fixture": "empty",
  "onCard": "approve",
  "fullControl": false,
  "buildStyle": "auto",
  "maxTurns": 12,
  "turns": ["First message.", "Second message."]
}
```

- `request` is accepted in place of `turns` for one message. `id` becomes a file name.
- `provider` is `Anthropic`, `OpenAI`, `Google`, `OllamaCloud` or `OllamaLocal`. A model the catalogue does not list as agentic is refused with the product's own sentence, and the row is reported as not run.
- `approvalMode` is `ask`, `scripts_and_deletions` or `never`.
- `onCard` is what the row's user does with a card left waiting: `approve`, `decline` or `ignore`. A card the mode approves is approved whatever this says.

## What comes out

One `<id>.json` and one `<id>.md` per row, and `_run.json` for the run. Each row records every tool called with its arguments and answer, every card, the stop reason, tokens, cents and seconds, any tool the model tried that does not exist, and sentences where it said it lacked something. Those sentences are found by plain words, for a judge to read; they count nothing.

Each tool call says what answered it:

| `answeredBy` | Means |
|---|---|
| `real-core` | The product's own code, over the fixture or the saved list |
| `fixture` | A canned envelope |
| `recorded` | A mutation, recorded and not performed. Its verdict is canned |
| `refused` | The product's own parser or grant check refused it |
| `fixture-miss` | Nothing could answer. This is the instrument's gap, not the product's, and the result lists them |

The model is never told it is in a test. A fixture miss tells it the read failed.

## What is real and what is not

**Real, link-compiled and never copied:** `AgentTurnDriver`, `TurnSystemText` and the master prompt, the tool descriptors, `ChatToTranscript`, the three provider adapters, `TryParseRequest`, `ApprovalMode.Decide`, `ConsentCard.Route`, the forcing decisions, the scaffold and build parsers, the map's record and reader, and `KbLookupReport`.

**A copy, because the original is in the GH-bound panel:** the order of events after a turn, in `RowRunner`, calling those real decisions. If the panel's order changes, this does not notice.

**Not run at all, and listed in every result:**
- every mutation, so a canned "applied" proves nothing;
- card refusals that need a live canvas: named components, a script's trial and its attempts per target;
- the script-against-card comparison at the build, the build's region scope and its attempt cap;
- the build-outcome fact.

**Run over the stated world, by the product's own code:** a placement's check and mint (`SpotAsk.Prepare`), an edit's rows (`EditOpsAsk.Validate`), a value's target and state (`SetValueAsk.Validate`), and a script edit's target, claim, source and ports (`EditScriptAsk.Validate`). A port is known only for a component on the stated canvas or one of its type, because the saved component list holds no ports, so a wrong port on any other type passes here and the panel would refuse it. An approved placement, value, script edit or wire is recorded, and later reads see it. A script edit's trial is stated clean, so a script that would not compile or run gets a card here and its report says it was checked. A stated slider's domain and a stated script's source are not known until a card or a build writes them. A placement lands where the product's own search says, over the stated boxes, so its sentence is the product's; the sizes are stated, and with no view, one with no wires starts past the drawing's edge where the panel starts at the user's view. A map region's place, its sentence and every framing of the view are not here.

A "too strict" finding on a card therefore needs lane R to confirm it.

## Keys

Keys come from the OS credential store, read by this process, and stay in memory. They pass through no argument, environment variable, file or printed line. Everything written goes through `KeyRule`, which removes a key's value and then reads the file back: if a key is still there, the file is deleted and the run stops.

## The component list

`gh_component_list.json` is a JSON list of every component Grasshopper's `ComponentServer` offers, each with its name, category, subcategory, description and guid, dumped once from a Grasshopper session on your own machine, reading `ComponentServer` and opening nothing; pass its path with `--components`. **It is not in git**, because it names that machine's user objects and developer tools. `ComponentList` hides the developer bridges, so a row cannot teach a model it could call one. `kb_lookup` answers `ports: "unavailable"`, as the product does when nothing of that type is on the canvas. Without the file, `kb_lookup` is a fixture miss and the result says the list was not loaded.

## Fixtures

A fixture states a small canvas by hand, and each says so in `origin`, which every result carries. The product's own `AgentToolCore` projections answer the reads over it, and a recorded build adds its components, so later reads see them and the verdict carries real ids.

| Fixture | What it states |
|---|---|
| `empty` | Nothing |
| `small-organised` | 11 components in 3 labelled groups, left to right |
| `crowded-disorganised` | 40 components, no groups, overlapping, with backward wires and no room left |

```json
{
  "origin": "Stated by hand: what this canvas is meant to be.",
  "canvas": {
    "components": [
      { "ref": "r", "type": "Number Slider", "nick": "Radius", "kind": "param", "outputs": ["Number"], "constant": "5", "x": 0, "y": 0, "region": "Inputs", "group": "Inputs" }
    ],
    "wires": [ { "from": "r", "fromParam": "Number", "to": "c", "toParam": "Radius (R)" } ]
  },
  "canned": { "get_solve_timing": { "ok": true, "result": {} } }
}
```

`canned` overrides anything, and answers what the projections cannot: a tool name mapped to its envelope, or to a list of `{ "argsContain": ..., "envelope": ... }` tried in order. Never put a client's definition in a fixture.

## Measuring announcements with no card

Each turn records whether it ended announcing a change with no card, three ways, and `_run.json` totals them per provider and model under `announcing`:

| Field | Means |
|---|---|
| `AnnouncedLineCatch` | What draws the product's turn-end line, its request family and its generous catch together |
| `AnnouncedForcingCatch` | The strict catch that may force a card |
| `SaidWillNoCard` | Plain "I'll", "I will", "let me", "going to". An upper bound, and it over-counts on purpose |

`turnsThatCouldAsk` is the denominator and `turnsThatAsked` is how many called `request_mutation`. None of this changes what a row does. It exists so the catch can be ruled on from numbers.
