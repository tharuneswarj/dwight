# Privacy, per verb

Dwight sends data from your machine to the model provider you chose, using your key, and to nobody else. With Ollama running locally, nothing leaves your machine at all, unless you turn on web search (below).

What each verb and each chat tool sends is written down once, in a table in the source (`Dwight/Data/PayloadContract.cs`), and a unit test builds each chat payload and checks that the fields emitted equal that table. This page is that table in prose. A statement here that overstates what is sent would make you refuse a feature that does not do what you fear, so each line is as narrow as the table allows.

Two things never enter any payload: component GUIDs and canvas coordinates. The model is given short session-local integer ids, and the plugin keeps the mapping.

Claude Fable 5.1 requires Anthropic to keep requests for 30 days, so it is not available under zero data retention. Dwight uses it only if you pin it.

## The five verbs

| Verb | What is sent |
| --- | --- |
| **Explain** (Step by step, Detailed, Short) | Structure only: component types, your nicknames, wires and tree signatures for the selection and its neighbours. No values, no geometry. |
| **Explain, Learn** | Step by step's request, and then one more: Step by step's own request again, its summary and step sentences, the names and numbers of the components a question may name, and for each card Dwight deals itself, the type, nickname and number of the component it asks about, the name of a port, and either the value that port holds or how many items its wire carries, a number or a count, or the names and numbers of the components offered as choices. This is the one Explain that sends a value. |
| **Explain, Learn, an answer in your own words** | Only when you type an answer and send it: the card's question, its right answer and why, as Dwight's quiz wrote them, and what you typed, as you typed it, to your provider's fast model. |
| **Diagnose** | Structure plus Grasshopper's own runtime messages, verbatim. This is the one place a value or a file path can leave without a verb intending it, because a component wrote it into its own error text. |
| **Reorganise** (region labelling) | Structure only: per region, its component types with counts and the flow between regions. No nicknames, no values, no geometry. |
| **Simplify, better logic** | Structure, the source of any script component in the selection (your code), a flag saying that a component holds internalised data, and coarse solve-time buckets. Not the values: the flag is not the number. |
| **Simplify, to a script** | Structure, script sources (your code), and the constant values on the selection's boundary and inside it: a slider's number, a panel's text, because a script has no components to hold them. This is the one verb that sends values. |
| **Build** | Your messages, and the results of the tools below, for as long as the conversation runs. |

A tree signature says how many branches a wire carries and how many items are in each, never what the items are.

## A script Dwight runs for you

With Full control on, a script Dwight runs for you has the access you have, to this definition and to Rhino. The source came from the model and you read it on the card before it ran, or, under the Do not ask approval mode, afterwards; what the script reads or writes is not sent anywhere by Dwight, but what it prints and the counts Dwight reports go back into the conversation, and so to your provider like any other turn.

## A result you carry into the chat

A result's **Chat** button, and Step by step's **Ask the chat about this step**, open that run's own chat with the result in it as Dwight's earlier words: the names of the components it ran on, and an Explain's text or steps, a Learn's questions with the answers you have revealed, a Simplify's proposed or applied changes and its advice, a Diagnose's findings with Grasshopper's own messages, or a Reorganise's outcome and regions. Nothing goes to your provider until you send a message there; then it goes with that message and every message after it, like any other turn.

## The Build chat's tools

A tool's result is sent **only when the model calls it, and only for the components it asks about**. A conversation may call none of them. Every result carries an envelope saying whether the call succeeded, the result, or a coded refusal with its message.

| Tool | What is sent |
| --- | --- |
| Overview | Counts, the regions with their types and labels, the flow between them. |
| Region | Structure plus a hint per component: a script's language, and the constant a slider or panel holds (one line). |
| Component | Structure, your nickname, Grasshopper's runtime messages, and what the component holds: a slider's value and domain, a panel's text (or, for a panel fed by a wire, the values it displays, as text), a toggle's state, a value list's selection, internalised data as a type with its value and a count, a script's source, a plugin's name. |
| Connections | The wires in and out, with tree signatures. |
| Cluster | The same per-component hints as a region, for what is inside one cluster. A password-protected cluster is reported, never read. |
| Tree signature | The signature on one wire, never its contents. |
| Search | Structure plus the per-component hints a region carries. |
| Diagnose | Structure plus Grasshopper's own runtime message per fault, verbatim. |
| Component documentation lookup | An installed component's name, category and its own SDK description; its ports, read from an instance on the canvas or from a temporary instance that never joins the definition. |
| Solve and check | Structure plus Grasshopper's runtime messages after a re-solve, verbatim, and per-output item counts. |
| Geometry facts (summary, mesh check, brep check, naked edges, mass properties, intersection, spatial query) | Facts per object, never the geometry: type, bounding box, validity with Rhino's log, closed or solid, counts, length, area, volume and centroid, naked-edge counts with at most ten sample locations, intersecting pairs as indices, distances. |
| Solve timing | The slowest components by type, id and your nickname, each with the time it took (in 5 ms buckets), and each region's share. Never your geometry or values. |
| Document units | The units and tolerances of Rhino's active document. Never the document's name or path. |
| Installed plugins | Each loaded Grasshopper plugin's name, version, component count and how many objects on this canvas come from it. A plugin installed as user objects is named by its ribbon category, whatever that category is called on your machine. Never where it is installed. |
| File state | Whether your definition is saved, and whether it has unsaved changes. Never its file name or path. |
| Canvas layout | Your group names and which components each holds, how groups sit relative to one another, overlaps, how many components are in no group, which wires run backwards. Never a coordinate. |
| Scaffold map | Each region's address, name, purpose, pseudo-code and declared inputs and outputs, as written on the canvas and in Dwight's record. |
| Build and scaffold (verdicts) | Structure: what was placed and wired, what was preserved, what the verdict proves and does not, how to undo. |
| Set value (verdict) | Structure plus the change itself: which component, what it held and what it holds now (a slider's value and domain, or a panel's text), and the two-leg verdict. |
| An image you attach | That image, downscaled to 1568 px on its longest edge, with the turn you attach it to and with every later turn of that thread, because the conversation is sent whole each time. Never a capture Dwight took itself. |

## Web search

Web search is **off** unless you turn it on in Settings. When it is on, the Build chat's model may search the web when a request depends on something it is not sure of, such as a real place, a product or a standard. Dwight never fetches a web page itself: the search is run by your provider, or by a search service your provider operates, and what leaves your machine is the search query the model writes. A query is written in the model's own words, from your request and what it has read, so it can contain a name or a term from your definition. The results come back as text with source links, and are shown to the model as untrusted text.

| Provider | What leaves your machine when web search is on |
| --- | --- |
| **Anthropic** | Nothing extra beyond the request you already send: Anthropic runs the search inside that request, with its own search. Anthropic bills each search ($10 per 1,000) to your account, and Dwight shows the count in the cost line. |
| **Google** | Not yet: Google asks that its search suggestions be shown with grounded answers, and the panel does not show them yet. Nothing is sent. |
| **Ollama Cloud** | The query goes to ollama.com's search service, with your Ollama account key. Nothing else is in that request. |
| **Ollama, local** | The same: the query goes to ollama.com with an Ollama account key (a free account is enough). **With web search on, Ollama local is no longer fully local.** The model still runs on your machine; the query does not. Without a key stored, Dwight refuses the search and sends nothing. |
| **OpenAI** | Not available yet. Nothing is sent. |

Search results are text, never geometry, and are not stored anywhere by Dwight beyond the conversation they appear in.

## OpenAI and storage

On OpenAI, the Build chat asks OpenAI not to store the conversation (`store: false`), so each step sends the earlier steps again instead of leaving them on OpenAI's side.

## Dwight notes

A note you write on a component, or that you ask Dwight to pin, is kept in Dwight's own record for that file on your machine, never in the definition. It is sent to your provider with the component it is on, as text, whenever that component is part of what a verb or the Build chat sends, and only then; a note about a point on the canvas, or about a component that is not part of the request, is not sent.

## What stays on your machine

- Your API keys (Windows Credential Manager); they are sent only to the provider they belong to, as that provider's authentication.
- Your settings, your command history and your Build conversations (`%APPDATA%\Dwight\`).
- The fingerprints and checks that decide whether a change is kept: they are computed in the plugin, never by the model.

Dwight also keeps one record of what it has spent, across every file and every chat, in `%APPDATA%\Dwight\spend.jsonl`, so Settings can show you how much you have used. Each line is numbers and names only: the time, the file's id and its name (never its folder), the verb, the provider and model, the tokens, the size of the request in characters, the cost and how long it took. It never holds a prompt, a reply, a card, a component name or a path. Dwight only ever adds to it; it never trims or deletes it, and it sends it nowhere.

Settings can ask a provider which models your key can use. That call carries your key and no part of any definition.

## A note on scripts

A script written by the model sits on its card in full, untruncated, and does not run until you approve it, or, under an approval mode you chose, until it is approved automatically and shown as such. A script is the one kind of Dwight output that executes on your machine.

## Reporting a problem

The Report a problem panel builds a message for you to send yourself; Dwight sends nothing. The message holds the Dwight, Rhino, Grasshopper and Windows versions, the name of your provider (never a key or a model setting), the name of the verb or chat step last run, the text of the last three errors Dwight itself reported this session (with file paths and anything shaped like a key removed), and the words you typed. It never holds your definition, component names, values, geometry, file paths, prompts, model replies or API keys.
