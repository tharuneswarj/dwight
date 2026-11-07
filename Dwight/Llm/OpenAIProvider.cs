using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;

namespace Dwight.Llm
{
  /// <summary>
  /// The OpenAI-compatible adapter (ADR-066): the Chat Completions wire shape over plain HTTPS, reused for
  /// <b>Ollama local and Cloud</b> by pointing the base URL elsewhere (one HTTP shape, different base +
  /// auth). The OpenAI instance's tool-bearing turns go to the Responses endpoint instead (Brief BV, <see cref="OpenAIResponses"/>).
  /// Streaming prose for Explain; structured output coerced per capability - OpenAI uses
  /// <c>response_format: json_schema</c> (server-guided), Ollama/weak models use <c>json_object</c> + the
  /// schema in the system prompt; either way the <see cref="StructuredOutput"/> seam validates and repairs.
  /// The API key only ever rides the Authorization header - never the body, never a log (ADR-011).
  /// </summary>
  internal sealed class OpenAIProvider : ILlmProvider
  {
    /// <summary>Does Dwight's connection carry tools to an OpenAI-compatible model (OpenAI, and Ollama local and
    /// Cloud, which share this adapter)? TRUE - <see cref="BuildBody"/> writes <c>request.Tools</c> into the
    /// body's <c>tools</c> array (<c>{type:"function", function:{name, description, parameters}}</c>), replays an
    /// assistant turn's calls as <c>tool_calls</c> and answers each with a <c>tool</c> message, and
    /// <see cref="ReadStream"/> assembles the streamed <c>delta.tool_calls</c> back into whole calls. Declared
    /// HERE, beside the code that makes it true, and held to it by a test that builds a body carrying a tool
    /// (lane B, 3.1 step 2): the flag can neither lag the code nor run ahead of it.</summary>
    internal const bool PassesTools = true;

    private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };

    private readonly string _endpoint;     // {baseUrl}/chat/completions
    private readonly string _responsesEndpoint;   // {baseUrl}/responses, for the OpenAI instance's tool-bearing turns
    private readonly string _apiKey;       // null/empty for a keyless local endpoint
    private readonly string _label;        // for friendly errors ("OpenAI" / "Ollama")
    // The catalogue's `reasoning` fact for this model, passed in by the factory (the catalogue needs the live plugin,
    // this adapter is GH-free and tested without it).
    private readonly bool _reasoningModel;
    private readonly bool _useCompletionTokens;  // OpenAI newer models want max_completion_tokens; Ollama wants max_tokens

    internal bool IsOpenAI => string.Equals(_label, "OpenAI", StringComparison.Ordinal);

    /// <summary>Brief BT: only the OpenAI instance writes tool_choice (on the Responses endpoint, Brief BV); the Ollama instances of this adapter send nothing.</summary>
    public bool SendsForcedTool => IsOpenAI;

    /// <summary>Brief BV: a forced step keeps its reasoning here. OpenAI's documentation neither allows nor forbids a forced tool beside reasoning in words, but
    /// it says Astra refuses "none" and shows Astra with tool_choice "required", so the two do meet; the gate measures a NAMED function on each model. If
    /// OpenAI refuses it, the turn says so: nothing falls back to reasoning off.</summary>
    public bool ThinksWhileForced => IsOpenAI;

    /// <summary>Brief BV: the one test of which endpoint a request goes to. Verbs carry no tools, and no Ollama instance ever moves.</summary>
    internal bool UsesResponses(LlmRequest request) => IsOpenAI && request.Tools != null && request.Tools.Count > 0;

    internal string EndpointFor(LlmRequest request) => UsesResponses(request) ? _responsesEndpoint : _endpoint;

    public string ModelId { get; }
    public ProviderCapability Capability { get; }

    public OpenAIProvider(string baseUrl, string apiKey, string modelId, ProviderCapability capability,
      bool useCompletionTokens, string label, bool reasoningModel = false)
    {
      _reasoningModel = reasoningModel;
      _endpoint = baseUrl.TrimEnd('/') + "/chat/completions";
      _responsesEndpoint = baseUrl.TrimEnd('/') + "/responses";
      _apiKey = apiKey;
      ModelId = modelId;
      Capability = capability;
      _useCompletionTokens = useCompletionTokens;
      _label = label;
    }

    public LlmResponse Send(LlmRequest request, CancellationToken cancel = default)
    {
      // The schema pre-flight (LlmSchemaCheck): a tool whose schema cannot be published is not
      // published. Refused HERE rather than only in the agent loop, because the five verbs never
      // run that loop - this is the seam every request passes through, before the HTTP call and
      // before anything is spent.
      var badSchema = LlmSchemaCheck.Check(request);
      if (badSchema != null) throw new LlmException("Dwight could not send this request: " + badSchema + ".");

      var structured = !string.IsNullOrEmpty(request.StructuredSchemaJson) && !string.IsNullOrEmpty(request.StructuredName);
      var viaResponses = UsesResponses(request);
      if (viaResponses && structured)
        throw new LlmException("Dwight could not send this request: it asked for tools and a structured answer together, which nothing in Dwight does on OpenAI.");
      var body = viaResponses ? OpenAIResponses.BuildBody(request, ModelId) : BuildBody(request, structured);
      LlmTrace.RecordRequest(ModelId, body);   // DEBUG-only; compiles out of Release (#199)

      using (var msg = new HttpRequestMessage(HttpMethod.Post, EndpointFor(request)))
      {
        if (!string.IsNullOrEmpty(_apiKey)) msg.Headers.Add("Authorization", "Bearer " + _apiKey);
        msg.Content = new StringContent(body, Encoding.UTF8, "application/json");

        HttpResponseMessage resp;
        try { resp = Http.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, cancel).GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { throw; }   // WP-3.1
        catch (Exception ex) { throw new LlmException($"Could not reach {_label} ({ex.Message}). Check the endpoint and your connection."); }

        using (resp)
        {
          if (!resp.IsSuccessStatusCode)
          {
            var errBody = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            throw new LlmException(DescribeError(_label, (int)resp.StatusCode, errBody, ModelId));
          }
          using (var stream = resp.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
          using (var reader = new StreamReader(stream, Encoding.UTF8))
          {
            var parsed = viaResponses ? OpenAIResponses.ReadStream(reader, cancel, _label) : ReadStream(reader, cancel, _label);   // U38: refuses a damaged reply
            LlmTrace.RecordResponse(parsed.Usage, OpenAIResponses.ReasoningItems(parsed), parsed.StopReason);   // the inbound half (#199): tokens and items are two witnesses
            if (structured && !string.IsNullOrWhiteSpace(parsed.Text))
              parsed.StructuredJson = parsed.Text;   // the whole content is the JSON object
            return parsed;
          }
        }
      }
    }

    // internal, not private: the PassesTools test builds a real body through it (lane B, 3.1 step 2).
    internal string BuildBody(LlmRequest request, bool structured)
    {
      // The system text; for the weaker (json_object) path the schema is injected here so the model knows
      // the shape (server-side enforcement is unavailable on that path).
      var system = request.System ?? "";
      var strictSchema = structured && Capability == ProviderCapability.StrictSchema;
      if (structured && !strictSchema)
        system += "\n\nReturn ONLY a single JSON object that matches this JSON Schema (no prose, no fences):\n" + request.StructuredSchemaJson;

      var messages = new JsonArray();
      if (!string.IsNullOrEmpty(system))
        messages.Add(new JsonObject { ["role"] = "system", ["content"] = system });
      foreach (var m in request.Messages)
        AddMessage(messages, m);

      var root = new JsonObject
      {
        ["model"] = ModelId,
        ["messages"] = messages,
        ["stream"] = true,
        ["stream_options"] = new JsonObject { ["include_usage"] = true },
      };

      // The agent's tools, from the SAME neutral descriptors the Anthropic body is built from (the loop maps
      // AgentToolCatalog.All() once; this is the second translation of that one list, never a second list).
      // tool_choice is left out: the default is "auto" wherever tools are present, and the loop decides by reading the
      // reply. (Ollama's OpenAI endpoint does not accept tool_choice at all.) Only Ollama reaches here with tools; the
      // OpenAI instance's tool-bearing turns go to the Responses endpoint (Brief BV), so nothing here sets a reasoning effort.
      // Brief CN: with no tool_choice to say "none" in, a step that may not call a tool is sent without them.
      if (request.Tools != null && request.Tools.Count > 0 && !request.ForbidTools)
      {
        var tools = new JsonArray();
        foreach (var t in request.Tools)
          tools.Add(new JsonObject
          {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
              ["name"] = t.Name,
              ["description"] = t.Description,
              ["parameters"] = Schema(t.InputSchemaJson),
            },
          });
        root["tools"] = tools;
      }
      // #216 applied to this adapter: the answer budget is the ANSWER, and the thinking reserve rides on top. On Ollama a
      // reasoning model's thinking tokens count inside max_tokens, so with the bare answer budget the model can spend all
      // of it thinking. Measured 25 October, GLM-5.3 on Ollama Cloud: 4000 of 4000 tokens thinking, no answer, no call.
      // The OpenAI instance is unchanged here (its verbs carry no tools and use max_completion_tokens).
      var maxOut = request.MaxTokens;
      if (!IsOpenAI && _reasoningModel) maxOut += ThinkingBudget.ReserveHigh;
      root[_useCompletionTokens ? "max_completion_tokens" : "max_tokens"] = maxOut;
      if (request.Temperature.HasValue) root["temperature"] = request.Temperature.Value;   // RR20 #62 (eval only)

      if (strictSchema)
      {
        // response_format json_schema (strict:false - our flat schemas use optional fields, which strict
        // mode forbids; the seam's validate+repair covers the rest). True server-enforcement would need
        // strict-compliant schemas - a later refinement.
        root["response_format"] = new JsonObject
        {
          ["type"] = "json_schema",
          ["json_schema"] = new JsonObject
          {
            ["name"] = request.StructuredName,
            ["schema"] = Schema(request.StructuredSchemaJson),
            ["strict"] = false,
          },
        };
      }
      else if (structured)
      {
        root["response_format"] = new JsonObject { ["type"] = "json_object" };
      }

      return root.ToJsonString();
    }

    /// <summary>
    /// One neutral message, written in Chat Completions' shape.
    ///
    /// <para>An assistant turn that called tools is replayed VERBATIM: its text as <c>content</c> (null when it had
    /// none, which the API accepts beside tool_calls) and each call as a <c>tool_calls</c> entry whose
    /// <c>arguments</c> is the string the model streamed, byte for byte - never re-serialised, so the model reads
    /// its own words back (#827). A call with no arguments recorded is REFUSED rather than replayed as
    /// <c>{}</c>: a transcript that lies about the past to the one reader that cannot check it (#297).</para>
    ///
    /// <para>A user turn that carried tool results becomes one <c>tool</c> message per result, each with its
    /// <c>tool_call_id</c>, written BEFORE any text of the same turn - the API requires the answers to follow the
    /// assistant message that asked immediately.</para>
    /// </summary>
    private static void AddMessage(JsonArray messages, LlmMessage m)
    {
      if (m.Role == LlmRole.Assistant)
      {
        var calls = new JsonArray();
        var text = new StringBuilder();
        foreach (var b in m.Blocks)
        {
          if (b.Kind == "text") text.Append(b.Text);
          else if (b.Kind == "tool_use")
          {
            if (string.IsNullOrWhiteSpace(b.ToolInputJson))
              throw new LlmException("Dwight stopped this turn before its next step: the record of the earlier call to \"" +
                                     (b.ToolName ?? "a tool") + "\" has no arguments recorded, so replaying it would misstate " +
                                     "what the model asked for. That step was not sent.");
            calls.Add(new JsonObject
            {
              ["id"] = b.ToolUseId,
              ["type"] = "function",
              ["function"] = new JsonObject { ["name"] = b.ToolName, ["arguments"] = b.ToolInputJson },
            });
          }
        }
        var msg = new JsonObject { ["role"] = "assistant" };
        msg["content"] = calls.Count > 0 && text.Length == 0 ? null : (JsonNode)text.ToString();
        if (calls.Count > 0) msg["tool_calls"] = calls;
        messages.Add(msg);
        return;
      }

      var userText = new StringBuilder();
      var hasResults = false;
      var images = new JsonArray();
      foreach (var b in m.Blocks)
      {
        if (b.Kind == "tool_result")
        {
          hasResults = true;
          messages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = b.ToolUseId, ["content"] = b.Text ?? "" });
        }
        else if (b.Kind == "text") userText.Append(b.Text);
        // Brief DX: only an Ollama instance meets an image here, because the OpenAI instance's chat turns go to the Responses endpoint. This is Ollama's OpenAI-compatible endpoint, whose
        // page shows an image_url part holding a data URL. Its native /api/chat has an images array, and that page does not list one, so writing it here would risk the model answering without the image.
        else if (b.Kind == LlmBlock.ImageKind)
          images.Add(new JsonObject { ["type"] = "image_url", ["image_url"] = "data:" + b.MediaType + ";base64," + b.Base64 });
      }
      if (images.Count > 0)
      {
        if (userText.Length > 0) images.Add(new JsonObject { ["type"] = "text", ["text"] = userText.ToString() });
        messages.Add(new JsonObject { ["role"] = "user", ["content"] = images });
      }
      else if (!hasResults || userText.Length > 0)
        messages.Add(new JsonObject { ["role"] = "user", ["content"] = userText.ToString() });
    }

    /// <summary>Accumulate an OpenAI-style SSE stream (<c>data: {choices:[{delta:{content}}]}</c>) into one
    /// text response, plus the final usage chunk - through the ONE spec-correct reader (U38, #873), so a damaged
    /// reply is REFUSED as a failed run in Dwight's words (<see cref="SseReader.Refusal"/>) rather than skipped
    /// into a shorter answer. Beyond a damaged event, it refuses a stream that ends before any
    /// <c>finish_reason</c> arrived: a reply cut short. <c>[DONE]</c> is this protocol's sentinel, not JSON.</summary>
    /// <param name="label">"OpenAI" or "Ollama" - this one reader serves both, and their streams need not match,
    /// so the capture is filed under whichever actually sent it.</param>
    internal static LlmResponse ReadStream(TextReader reader, CancellationToken cancel, string label)
    {
      // Lane B (#873): the raw stream, recorded inside a capture window only; changes nothing parsed.
      using (var cap = StreamCapture.Begin((label ?? "openai").ToLowerInvariant()))
      {
        try { return Assemble(reader, cap, cancel, label ?? "OpenAI"); }
        catch (StreamDamageException damage) { throw SseReader.Refusal(damage); }
      }
    }

    internal const string OllamaLocalLabel = "Ollama (local)";

    /// <summary>The usage of a "length" finish shows the PROMPT filled the window: it reached within two tokens of the
    /// total, or the model produced at most one token. -1 means the field never arrived.</summary>
    internal static bool PromptFilledWindow(int promptTokens, int completionTokens, int totalTokens)
      => (totalTokens > 0 && promptTokens >= totalTokens - 2) || (completionTokens >= 0 && completionTokens <= 1);

    private static LlmResponse Assemble(TextReader reader, StreamCapture.Recording cap, CancellationToken cancel, string label)
    {
      var response = new LlmResponse();
      var text = new StringBuilder();
      var calls = new SortedDictionary<int, Call>();
      string finishReason = null;
      var finished = false;
      var sawReasoning = false;   // reasoning deltas arrived (read only to know the reply was ALL thinking)
      int usagePrompt = -1, usageCompletion = -1, usageTotal = -1;

      foreach (var (ev, node) in SseReader.JsonEvents(reader, sentinel: "[DONE]", cap: cap, cancel: cancel))
      {
        if (node == null) continue;   // [DONE]
        SseReader.Shape(ev, () =>
        {
          // An error reported mid-stream is the provider's own failure, said in its own words - not damage.
          var err = node["error"];
          if (err != null)
            throw new LlmException(label + " returned a streaming error: " + (err["message"]?.GetValue<string>() ?? err.ToJsonString()));

          var choices = node["choices"]?.AsArray();
          if (choices != null && choices.Count > 0)
          {
            // Read only `content` and `tool_calls`. Thinking models (e.g. DeepSeek V4 Pro on Ollama Cloud) stream
            // their reasoning in a separate `reasoning` / `reasoning_content` field, which we deliberately ignore -
            // so the structured JSON is never polluted by reasoning tokens (model-catalogue note 4).
            var d = choices[0]?["delta"];
            var delta = d?["content"]?.GetValue<string>();
            var thought = (d?["reasoning"] ?? d?["reasoning_content"]) as JsonValue;
            if (thought != null && thought.TryGetValue<string>(out var th) && !string.IsNullOrEmpty(th)) sawReasoning = true;
            if (delta != null)
            {
              text.Append(delta);
            }

            var fragments = d?["tool_calls"]?.AsArray();
            if (fragments != null)
              foreach (var f in fragments) ApplyToolCall(ev, f, calls);

            var fr = choices[0]?["finish_reason"]?.GetValue<string>();
            if (fr != null) { finishReason = fr; finished = true; }
          }
          ApplyUsage(response.Usage, node["usage"]);
          var uNode = node["usage"];
          if (uNode != null)
          {
            LlmTrace.RecordRawUsage(uNode.ToJsonString());
            usagePrompt = ReadInt(uNode, "prompt_tokens", usagePrompt);
            usageCompletion = ReadInt(uNode, "completion_tokens", usageCompletion);
            usageTotal = ReadInt(uNode, "total_tokens", usageTotal);
          }
        });
      }

      if (!finished)
        throw new StreamDamageException(0, 0, "ended before the model finished its reply (no finish_reason arrived)");

      // A reply cut at the token limit is Truncated, whatever it was in the middle of saying. Any call it had begun
      // is incomplete BY CONSTRUCTION, so none is dispatched: the turn ends on the stated outcome instead of acting
      // on half a call (or refusing it as "damage" when nothing was damaged but the budget).
      var truncated = string.Equals(finishReason, "length", StringComparison.Ordinal);
      var whole = new List<Call>();
      if (!truncated)
        foreach (var kv in calls)
        {
          kv.Value.Close();
          whole.Add(kv.Value);
        }

      // The provider says it stopped to call tools but none arrived: a reply with a part missing, not a short answer.
      if (!truncated && whole.Count == 0 && string.Equals(finishReason, "tool_calls", StringComparison.Ordinal))
        throw new StreamDamageException(0, 0, "ended with finish_reason \"tool_calls\" but no tool call arrived");

      // The loop reads StopReason through AgentLoopCore.PostResponse, which knows Anthropic's words: "max_tokens" is
      // a cut-off, tool use is whatever carries a call. Translated HERE, at the adapter, because the wire's own
      // words differ ("length"; Ollama says "stop" even beside a call) and the loop must not learn each dialect.
      // Ollama's own default context is 4096 tokens: when the prompt alone fills it the model is cut off after one
      // token with finish_reason "length". That is not the answer budget; say so, for the local instance only.
      response.ContextWindowFilled = truncated && string.Equals(label, OllamaLocalLabel, StringComparison.Ordinal)
        && PromptFilledWindow(usagePrompt, usageCompletion, usageTotal);
      response.ThoughtWithoutAnswer = truncated && sawReasoning && text.Length == 0 && calls.Count == 0;
      response.StopReason = truncated ? "max_tokens" : whole.Count > 0 ? "tool_use" : finishReason;

      // The text block is kept when there is text, and always when there is no call (a reply with nothing to say
      // is still a reply). An EMPTY text block beside a call is dropped: the transcript pre-flight refuses one.
      if (text.Length > 0 || whole.Count == 0)
        response.Blocks.Add(LlmBlock.TextBlock(text.ToString()));
      foreach (var c in whole)
        response.Blocks.Add(LlmBlock.ToolUse(c.Id, c.Name, c.Arguments.ToString()));
      return response;
    }

    /// <summary>One <c>delta.tool_calls</c> element: <c>{index, id, type, function:{name, arguments}}</c>. The first
    /// fragment of a call carries its id and name; later ones carry only <c>arguments</c> pieces, keyed by
    /// <c>index</c>. Refused here, by event: a fragment for a call that never started (its first fragment was lost),
    /// and a call started twice. Ollama sends a call whole in one fragment and may omit <c>index</c>; an index-less
    /// fragment that names a call starts the next one, and an index-less one that does not continues the last.</summary>
    private static void ApplyToolCall(SseEvent ev, JsonNode f, SortedDictionary<int, Call> calls)
    {
      if (!(f is JsonObject)) throw new StreamDamageException(ev.Index, ev.Line, "carries a tool call that is not an object");
      var id = f["id"]?.GetValue<string>();
      var fn = f["function"];
      var name = fn?["name"]?.GetValue<string>();
      var args = fn?["arguments"];
      var starts = !string.IsNullOrEmpty(id) || !string.IsNullOrEmpty(name);

      int idx;
      var given = f["index"]?.GetValue<int>();
      if (given.HasValue) idx = given.Value;
      else if (starts) idx = calls.Count == 0 ? 0 : calls.Keys.Max() + 1;
      else if (calls.Count > 0) idx = calls.Keys.Max();
      else throw new StreamDamageException(ev.Index, ev.Line, "carries tool-call arguments for a call that never started, so an earlier event was lost");

      if (!calls.TryGetValue(idx, out var call))
      {
        if (!starts)
          throw new StreamDamageException(ev.Index, ev.Line,
            "continues tool call " + idx + ", which never started, so an earlier event was lost");
        call = new Call { Index = idx, Id = string.IsNullOrEmpty(id) ? "call_" + Guid.NewGuid().ToString("N").Substring(0, 12) : id, Name = name };
        calls[idx] = call;
      }
      else if (!string.IsNullOrEmpty(id) && !string.Equals(id, call.Id, StringComparison.Ordinal))
        throw new StreamDamageException(ev.Index, ev.Line, "starts tool call " + idx + " a second time");
      else if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(call.Name) && !string.Equals(name, call.Name, StringComparison.Ordinal))
        throw new StreamDamageException(ev.Index, ev.Line, "renames tool call " + idx + " from \"" + call.Name + "\" to \"" + name + "\"");
      else if (!string.IsNullOrEmpty(name) && string.IsNullOrEmpty(call.Name)) call.Name = name;

      call.LastEvent = ev.Index;
      call.LastLine = ev.Line;
      if (args == null) return;
      if (args is JsonValue v && v.TryGetValue<string>(out var piece))
      {
        if (piece.Length == 0) return;
        call.Arguments.Append(piece);
        call.Fragments++;
      }
      else if (args is JsonObject)
      {
        // Some servers send the arguments as an object rather than a string; it is still the model's own words.
        call.Arguments.Append(args.ToJsonString());
        call.Fragments++;
      }
      else throw new StreamDamageException(ev.Index, ev.Line, "carries tool-call arguments that are neither text nor an object");
    }

    /// <summary>A tool call being assembled from stream fragments.</summary>
    private sealed class Call
    {
      public int Index;
      public string Id;
      public string Name;
      public int Fragments;
      public int LastEvent;
      public int LastLine;
      public readonly StringBuilder Arguments = new StringBuilder();

      /// <summary>The call is finished: it must have a name, and its fragments must join into a JSON OBJECT (parsing
      /// is not the bar, #828). With no fragments there are no arguments to dispatch - refused, never defaulted.</summary>
      public void Close()
      {
        var what = string.IsNullOrEmpty(Name) ? "a tool call" : "the call to \"" + Name + "\"";
        if (string.IsNullOrEmpty(Name))
          throw new StreamDamageException(LastEvent, LastLine, "ends " + what + " (number " + Index + ") that never named a tool, so the call was not made");
        if (Fragments == 0 || Arguments.Length == 0)
          throw new StreamDamageException(LastEvent, LastLine, "ends " + what + " with no arguments sent, so the call was not made");
        JsonNode parsed;
        try { parsed = JsonNode.Parse(Arguments.ToString()); }
        catch (System.Text.Json.JsonException)
        {
          throw new StreamDamageException(LastEvent, LastLine,
            "ends " + what + " whose argument fragments do not join into valid JSON, so the call was not made");
        }
        if (!(parsed is JsonObject))
          throw new StreamDamageException(LastEvent, LastLine, "ends " + what + " whose arguments are not a set of arguments, so the call was not made");
      }
    }

    private static void ApplyUsage(LlmUsage usage, JsonNode u)
    {
      if (usage == null || u == null) return;
      // Brief BT: prompt_tokens INCLUDES the cached and the cache-write tokens (the captures sum: 9268 = 8949 + 316 + 3), where the
      // loop's InputTokens is the uncached rest, as Anthropic's is. Read from the captures' own shape; Ollama Cloud sends
      // cached_tokens alone, and a reply with no details keeps the whole prompt as input.
      var prompt = ReadInt(u, "prompt_tokens", usage.TotalInputTokens);
      var details = u["prompt_tokens_details"];
      var cached = details != null ? ReadInt(details, "cached_tokens", 0) : usage.CacheReadInputTokens;
      var written = details != null ? ReadInt(details, "cache_write_tokens", 0) : usage.CacheCreationInputTokens;
      usage.CacheReadInputTokens = cached;
      usage.CacheCreationInputTokens = written;
      usage.InputTokens = Math.Max(0, prompt - cached - written);
      usage.OutputTokens = ReadInt(u, "completion_tokens", usage.OutputTokens);
    }

    private static int ReadInt(JsonNode parent, string key, int fallback)
    {
      try { var v = parent[key]; return v != null ? v.GetValue<int>() : fallback; } catch { return fallback; }
    }

    /// <summary>A schema we authored, on its way to the provider. It used to fall back to an empty object
    /// on a parse failure, which asks the model for a shape we never described and cannot be told apart
    /// from success. <see cref="LlmSchemaCheck.Check"/> refuses such a request in <see cref="Send"/>
    /// before this runs; reaching this throw means that check was bypassed, and loud is the right answer.</summary>
    private static JsonNode Schema(string json) => JsonNode.Parse(json);

    internal static string DescribeError(string label, int status, string body, string modelId = null)
    {
      string apiMessage = null;
      try { apiMessage = JsonNode.Parse(body)?["error"]?["message"]?.GetValue<string>(); } catch { }
      var limit = ProviderLimit.OfOpenAIBody(label, body);   // Brief CO
      if (limit != ProviderLimitKind.None) return ProviderLimit.Sentence(limit, label, apiMessage);
      switch (status)
      {
        case 401: return $"{label} rejected the API key (401). Re-enter it in Dwight > Settings.";
        case 403: return $"The API key lacks permission for this request (403).";
        case 404: return $"{label} could not find the model" + (string.IsNullOrEmpty(modelId) ? "" : $" \"{modelId}\"") + " (404). Check the model id in Settings (or that it is pulled locally).";
        case 429: return $"{label} is rate-limiting (429). Wait a moment and try again. " + ProviderLimit.NothingChanged;
        default:
          var detail = apiMessage ?? (body != null && body.Length > 200 ? body.Substring(0, 200) : body);
          return $"{label} returned an error ({status}): {detail}";
      }
    }
  }
}
