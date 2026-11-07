# Known limitations

Each limit below is where it is for a stated reason. None is a date; a limit moves when a measurement moves it.

## Simplify to-script is experimental

On the project's evaluation (48 real sub-selections of a 205-component definition, Claude Sonnet, temperature 0), converting a selection to a script **compiled 87.5% of the time and was verified equal to the originals 39.6% of the time.** The bar for removing the "experimental" label was 60% verified, and no change tried moved the number. In an earlier run a different model scored about the same (38% against 34%), so the limit is not the model's; it sits in what the model is told about the graph.

This is safe to offer because every failure is caught: Dwight verifies the script beside your originals before it changes anything, and a script it cannot verify is labelled a draft. The failure is "it could not", not "it was wrong". The comparison is bit-exact on purpose; it is not loosened to raise the number.

## An automatic approval is still a change

An automatic approval is still a change the panel reports: the card stays in the conversation, marked as approved automatically, and the close line counts it. Under "Do not ask" a turn can make several in a row, each its own Ctrl-Z.

## Full control is not checked

With Full control on, a script Dwight runs is not checked, and under the Do not ask approval mode it runs without asking: Dwight reports what changed, one Ctrl-Z undoes only what Grasshopper recorded, and a script that overruns its clock keeps running on Rhino's thread, where Dwight cannot stop it.

## The Build chat needs a model that can drive it

Every provider connection sends the chat's tools to the model. Within a provider, only models documented to call tools, and not in the small, fast band, are marked as able to drive the chat. With another model Dwight refuses the chat in plain words before sending anything, says so, and names the ones that can. The other four verbs are not tied to any one provider.

## How far each provider was measured

Each provider was run live on 25 October 2026 on a small definition (a slider feeding a circle): one read turn, one spot build that was approved, and one undo. OpenAI was run again on 31 October, after its chat moved to the Responses endpoint.

- **Anthropic**: Sonnet 5 passed all three.
- **OpenAI**: on 31 October, on the Responses endpoint and a scratch definition, GPT-5.6 Terra, GPT-5.6 Sol, GPT-6.1 Sol and GPT-6 Astra each passed a read turn, a build and its undo. GPT-5.6 Sol and GPT-6.1 Sol also built a lamp from one person's request and follow-ups, in eight runs of eight. The chat keeps the model's reasoning across a turn's tool calls, and the profile sets how deep; Economy chooses GPT-5.6 Terra and turns it off, which GPT-6.1 Sol and GPT-6 Astra refuse.
- **Google**: Gemini 3.8 Flash passed all three.
- **Ollama Cloud**: GLM-5.3 passed all three.
- **Ollama local**: measured on qwen3.5:9b. It reads the definition correctly, and the read turn took 126.6 seconds for four steps on the test machine, which offloads part of the model to the CPU. A build on that machine ran past the task timeout while the model was still reasoning, so no local build has been confirmed. gpt-oss:20b did not complete a reply on the Ollama version tested (0.30.10): the stream closed before the model gave a finish reason, and Dwight reported that as a failed turn.
- Ollama starts with a 4096-token context window, which the chat's instructions fill on their own. Start the server with `OLLAMA_CONTEXT_LENGTH=32768` set. Without it Dwight says the window is too small.

The 25 October runs used one small definition, and the refusal path for a damaged reply is covered by tests on captured streams, not by a live run. If a reply is damaged, the reader refuses it as a failed run, in Dwight's words; it does not turn it into a shorter answer.

## Platform

Windows, Rhino 8.24 or later. Rhino 7 is not supported, and macOS is not supported (keys are held in the Windows Credential Manager).

## What the chat does not do in v1

- **The only scripts it writes are Rhino 8 C# Script and Python 3 Script components.** A build can place one with its source, and the complete source is shown on the card before anything runs. The older GhPython and VB/C# script components are not offered.
- **It changes existing wiring and components only through one tickable card.** Each wire it adds or removes, and each component it deletes, is its own row you can untick, and it does not touch anything you did not tick. One card holds at most 40 rows; for more, it asks you to take the first 40 and ask again for the rest. A build on its own only connects what that build places (and a rebuild reconnects what it removed).
- **Without a map it places at most 12 components at once.** They land as one block, and past that a block stops being something you can follow. More than 12 wants a map, which lays its regions down with room.
- **It does not change values beyond one slider or one panel per card**, and no other component type.
- **Web search returns text, never geometry.** It can tell the model a dimension or a standard; it cannot fetch a drawing or a model, and OpenAI and Google do not have it yet.
- **It does not create or modify Rhino geometry or objects, except through a Full control script.** Rhino's document is read-only to Dwight except under Full control, where a script you read may change it and Ctrl-Z may not bring it back. Geometry is read through what Grasshopper outputs, as facts.
- **It does not read Rhino objects Grasshopper does not reference.**
- **It cannot see your screen.** It judges geometry from facts, not from a picture, and nothing in Dwight takes a capture of the canvas or the viewport. When it needs the picture it says so: "I cannot see your screen. Take a screenshot and attach it with the paper-clip or Ctrl+V and I will read it."
- **It reads one image a turn, and only in the Build chat.** A second image on the same turn replaces the first. Explain, Diagnose, Reorganise and Simplify do not take images. A model without vision refuses the image and says its own name. Attaching the image sends it with that turn and every later turn of the chat, the newest first when a chat's images are too many for one request. An image guides what Dwight plans; it names nothing on your canvas. The chip gives a token count only on Claude models from 4.7 on, whose rule Anthropic states and a measurement on Sonnet 5.5 confirmed; on any other model it says the provider counts the image, and the ledger shows what each counted.
- **It has no query language, and the only file it takes is an image you attach.** Without Full control it cannot run analysis code of its own or read files you point it at; under Full control it can run a script you read, which is reported and never checked.
- **The map belongs to its conversation.** A new conversation reads a canvas's regions by address; it does not inherit another conversation's map.
- **A script region is checked for solving and for its ports, not for geometry.** The verdict on a script Dwight places says the definition still solves and the wires landed; it does not judge what the script draws.

## What the model says about your domain is the model's

Dwight checks that every component it names is on your canvas, verifies every change it makes by solving again and reverts it if the result differs from what it declared, and bills every cost as shown. It does not check a claim about what a component does, what a unit means or what a value stands for in your trade. Those sentences are the model's reading, and a model can read a component the wrong way round or take a number for the wrong unit: in testing, one read a Cull Pattern as the inverse of what it does, and another took a G-code feed value as millimetres per second. When a sentence will be taught or built from, check it against the component's own help or your own knowledge, as you would a colleague's first reading. This is the same for every provider and model.

## Explain

- **Explain on one component has no Step by step.** Step by step goes through a selection, so with one component Explain is Detailed (or Short, if you choose it).
- **Most Learn cards are Dwight's reading.** The why questions and their answers are written by the model; Dwight checks only that every component they name is in your selection, and each one is marked as Dwight's reading. At most one card is read from your solved definition and one from its wires, and neither asks about geometry. A deck has seven cards at most. If Dwight cannot get valid questions from the model, Step by step runs without them and says so; the Explain is not failed by it.
- **An answer in your own words is judged by a model.** Your provider's fast model reads your answer beside the right one and says right, partly or wrong, and it can be wrong itself. Partly counts as missed. Where your provider has no fast model, as on some local setups, you pick an option instead, and the card says so.
- **Learn cannot ask you to click a component on the canvas yet.** You answer on the card: one option, every option that applies, or a value you type.
- **Learn's hatch is a drawing, not a lock.** You can open the panel or hover the port and see the answer. The card says so.
- **A Learn run reopened from History shows its score and cannot be played again.** Its answers were read from your definition as it was then; run Learn again for a fresh deck.
- **The cost of a run you stop is often an estimate.** If you stop a run after its request has gone out, the provider still bills it. If its reply had begun and said its count, Dwight records that count, which leaves out what the model wrote before the stop. Otherwise the count never reaches Dwight, and the Usage section and the run's row show an estimate from the size of the request, marked estimated (for example, "about 4 c"). It is the input's price alone: the request counted at the fewest tokens for its size that your recent runs on that model have used on this computer, and priced as if all of it had been read from the provider's cache. So it is at or under what any of those runs would have cost for it, and that is as far as it goes. A request the model counts more tightly than any Dwight has measured can cost less than it says, and so can a chat turn, because Dwight measures how the verbs' requests count, not the chat's. Until a model has three runs measured, Dwight goes by its provider's runs, and until the provider has three, by a stated ratio. A request the provider read for the first time cost more than it says. A chat turn's conversation says what the number is. A run stopped before anything was sent costs nothing and shows nothing.

## MCP

Dwight has no MCP server in v1. The chat's tool set is defined as data, so an MCP server could expose the same tools; none ships and none is configurable.

## Reorganise and Simplify scope

Reorganise and Simplify work on the selection you give them. Reorganise on a whole canvas asks you to confirm first. Groups that extend beyond a Reorganise selection are left alone.
