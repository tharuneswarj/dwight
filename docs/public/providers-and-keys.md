# Providers and keys

Dwight talks directly from your machine to the provider you choose, over HTTPS, with your own key. There is no Dwight server in between.

## Providers

| Provider | Key needed | Notes |
| --- | --- | --- |
| **Anthropic** | Yes | Runs the verbs and the Build chat. Measured on Sonnet 5: a read turn, a spot build and its undo. |
| **OpenAI** | Yes | Runs the verbs, and the Build chat on GPT-5.6 Terra, GPT-5.6 Sol, GPT-6.1 Sol and GPT-6 Astra. Measured on all four on 31 October: a read turn, a build and its undo. |
| **Google** (Gemini) | Yes | Runs the verbs, and the Build chat. Measured on Gemini 3.8 Flash: a read turn, a spot build and its undo. |
| **Ollama, local** | No | Talks to Ollama on your own machine (`localhost:11434`). Nothing leaves your machine unless you turn on web search. Measured on qwen3.5:9b: the chat reads the definition correctly, in 126.6 seconds for a small one on the test machine. A build there ran past the task timeout while the model was still reasoning, so on a machine like that, expect reads and expect builds to be slow or to time out. gpt-oss:20b did not complete a reply on the Ollama version tested (0.30.10). |
| **Ollama Cloud** | Yes | Runs the verbs, and the Build chat. Measured on GLM-5.3: a read turn, a spot build and its undo. |

**Ollama local needs a larger context window.** Ollama starts with a 4096-token window, and the chat's instructions fill it on their own, so the model is cut off after one token. Start the Ollama server with `OLLAMA_CONTEXT_LENGTH=32768` set. If the window is too small, Dwight says so in plain words and asks you to raise it.

Create a key in your provider's own account console. In Grasshopper, open **Dwight > Settings...**, choose the provider and paste the key.

## Where keys live

Keys are stored in the **Windows Credential Manager**, one entry per cloud provider. They are never written to a settings file, a log, or a dump. Ollama local needs no key and stores none.

In **fully-local** mode (Ollama local) Dwight builds only a localhost connection for the model: there is no code path that sends it to a cloud endpoint, and Settings shows a "Fully local. Nothing leaves your machine." badge. Web search, which is off by default, is the one exception: with it on, search queries go to ollama.com (see Web search below).

## Web search

Web search is optional, off by default, and is run through your provider. Dwight never fetches a page itself.

| Provider | Web search |
| --- | --- |
| **Anthropic** | Anthropic's own search, inside the request. Billed by Anthropic per search; Dwight counts them in the cost line. |
| **Google** | Not yet: Google asks that its search suggestions be shown with grounded answers, and the panel does not show them yet. |
| **Ollama, local and Cloud** | Through ollama.com with your Ollama account key. Ollama local needs that key for search only. |
| **OpenAI** | Not yet. |

## Which model runs what

Settings has a profile (Economy, Balanced, Quality, Ultra). Dwight chooses a model for each task from the provider's catalogue according to the profile. An Advanced setting lets you pin one model for everything. Settings can also list the models your key can use.

The Build chat additionally needs a model the catalogue marks as able to drive it: a model that is documented to call tools and is not in the small, fast band. Haiku-class, Flash-Lite-class and Luna-class models, and Ollama models of 8B or fewer parameters, are not used for the chat, and Dwight tells you so before sending anything if you have chosen one. The chat sends the model a set of tools, and every connection (Anthropic, OpenAI, Google and Ollama) carries them. On OpenAI the chat goes through the Responses endpoint, which keeps the model's reasoning across a turn's tool calls; the profile sets how deep, and Economy, which chooses GPT-5.6 Terra, turns it off. The verbs stay on OpenAI's chat endpoint.

## Cost

Every call is billed by your provider to your account. A cancelled run is actually cancelled: the Stop button aborts the HTTP request.

Dwight keeps the cost of each verb run and each chat turn, with the model used and the input and output token counts, in the history for that Grasshopper document. A verb's result shows them in its footer. A chat shows the cost so far on the line under the text box, with the last turn's receipt when you hover it, and choosing it opens Usage in Settings, which holds the totals across every file and every chat. A local model shows as free. Ollama Cloud says that Dwight cannot see what a run cost, because it depends on your plan.
