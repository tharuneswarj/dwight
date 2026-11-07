using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;

namespace Dwight.Llm
{
  /// <summary>
  /// The Google Gemini adapter (ADR-066): the Generative Language API over plain HTTPS, streaming prose
  /// for Explain. Structured output uses Gemini's JSON mode (<c>responseMimeType: application/json</c>) with
  /// the flat schema carried in the system instruction; the <see cref="StructuredOutput"/> seam then
  /// validates and repairs (true server-side <c>responseSchema</c> translation is a later refinement). The
  /// key rides the <c>x-goog-api-key</c> header only - never the URL, the body, or a log (ADR-011).
  /// </summary>
  internal sealed class GoogleProvider : ILlmProvider
  {
    /// <summary>Does Dwight's connection carry tools to a Gemini model? TRUE - <see cref="BuildBody"/> writes
    /// <c>request.Tools</c> as <c>tools:[{functionDeclarations:[...]}]</c>, replays the model's <c>functionCall</c>
    /// parts and answers them with <c>functionResponse</c> parts, and <see cref="ReadStream"/> reads the
    /// <c>functionCall</c> parts of a candidate back into whole calls. Declared HERE, beside the code that makes it
    /// true, and held to it by a test that builds a body carrying a tool (lane B, 3.1 step 2).</summary>
    internal const bool PassesTools = true;

    /// <summary>The id given to a call the model sent without one (the field is optional on Gemini's wire). It is
    /// only a handle for pairing a result with its call inside this conversation, so it is NOT written back to
    /// the provider: a replayed call and its response carry no <c>id</c> when the model sent none.</summary>
    internal const string GeneratedIdPrefix = "gcall_";

    private const string Base = "https://generativelanguage.googleapis.com/v1beta/models/";
    private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };

    private readonly string _apiKey;

    public string ModelId { get; }
    public ProviderCapability Capability => ProviderCapability.ResponseSchema;

    /// <summary>Brief BT: <see cref="BuildBody"/> writes it as <c>functionCallingConfig</c> mode ANY with the one allowed name.</summary>
    public bool SendsForcedTool => true;

    public GoogleProvider(string apiKey, string modelId)
    {
      _apiKey = apiKey;
      ModelId = modelId;
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
      var body = BuildBody(request, structured);
      var url = Base + Uri.EscapeDataString(ModelId) + ":streamGenerateContent?alt=sse";

      using (var msg = new HttpRequestMessage(HttpMethod.Post, url))
      {
        msg.Headers.Add("x-goog-api-key", _apiKey ?? "");
        msg.Content = new StringContent(body, Encoding.UTF8, "application/json");

        HttpResponseMessage resp;
        try { resp = Http.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, cancel).GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { throw; }   // WP-3.1
        catch (Exception ex) { throw new LlmException($"Could not reach Google ({ex.Message}). Check your connection."); }

        using (resp)
        {
          if (!resp.IsSuccessStatusCode)
          {
            var errBody = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            throw new LlmException(DescribeError((int)resp.StatusCode, errBody));
          }
          using (var stream = resp.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
          using (var reader = new StreamReader(stream, Encoding.UTF8))
          {
            var parsed = ReadStream(reader, cancel);   // U38: refuses a damaged reply
            if (structured && !string.IsNullOrWhiteSpace(parsed.Text))
              parsed.StructuredJson = parsed.Text;
            return parsed;
          }
        }
      }
    }

    // internal, not private: the PassesTools test builds a real body through it (lane B, 3.1 step 2).
    internal string BuildBody(LlmRequest request, bool structured)
    {
      var system = request.System ?? "";
      if (structured)
        system += "\n\nReturn ONLY a single JSON object that matches this JSON Schema (no prose, no fences):\n" + request.StructuredSchemaJson;

      var contents = new JsonArray();
      foreach (var m in request.Messages)
        contents.Add(Content(m, request.Messages));

      var generationConfig = new JsonObject { ["maxOutputTokens"] = request.MaxTokens };
      if (request.Temperature.HasValue) generationConfig["temperature"] = request.Temperature.Value;   // RR20 #62 (eval only)
      if (structured) generationConfig["responseMimeType"] = "application/json";

      var root = new JsonObject
      {
        ["contents"] = contents,
        ["generationConfig"] = generationConfig,
      };
      if (!string.IsNullOrEmpty(system))
        root["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = system }) };

      // The agent's tools, from the SAME neutral descriptors every other adapter translates (never a second list).
      // Mode AUTO is Gemini's documented default - "the model decides to predict either a function call or a natural
      // language response" - written out so the loop's reliance on it is on the wire and pinned by a test.
      if (request.Tools != null && request.Tools.Count > 0)
      {
        var declarations = new JsonArray();
        foreach (var tool in request.Tools) declarations.Add(Declaration(tool));
        root["tools"] = new JsonArray(new JsonObject { ["functionDeclarations"] = declarations });
        // Brief BT: mode ANY "constrains the model to predict a function call", and the allowed list names the one.
        root["toolConfig"] = new JsonObject
        {
          ["functionCallingConfig"] = request.ForbidTools ? new JsonObject { ["mode"] = "NONE" }
            : string.IsNullOrEmpty(request.ForceToolName)
            ? new JsonObject { ["mode"] = "AUTO" }
            : new JsonObject { ["mode"] = "ANY", ["allowedFunctionNames"] = new JsonArray(request.ForceToolName) },
        };
      }

      return root.ToJsonString();
    }

    /// <summary>
    /// One tool as a <c>FunctionDeclaration</c> (name, description, parameters). Gemini's <c>parameters</c> is an
    /// OpenAPI-subset schema, so the catalogue's JSON Schema is reduced to the keywords that subset has (type,
    /// description, properties, required, items, enum, format, nullable) - <c>additionalProperties</c> and the rest are
    /// dropped. A tool with NO parameters sends no <c>parameters</c> at all: an OBJECT schema with empty
    /// <c>properties</c> is rejected by the API. The reduction removes constraints the plugin's own parser enforces
    /// anyway; it never adds one.
    /// </summary>
    private static JsonObject Declaration(LlmTool tool)
    {
      var d = new JsonObject { ["name"] = tool.Name, ["description"] = tool.Description };
      var reduced = Reduce(JsonNode.Parse(tool.InputSchemaJson)) as JsonObject;
      var props = reduced?["properties"] as JsonObject;
      if (props != null && props.Count > 0) d["parameters"] = reduced;
      return d;
    }

    private static readonly string[] SchemaKeywords = { "type", "description", "format", "nullable", "enum", "required" };

    private static JsonNode Reduce(JsonNode node)
    {
      if (!(node is JsonObject o)) return node?.DeepClone();
      var r = new JsonObject();
      foreach (var k in SchemaKeywords)
        if (o.ContainsKey(k)) r[k] = o[k].DeepClone();
      if (o["properties"] is JsonObject props)
      {
        var p = new JsonObject();
        foreach (var kv in props) p[kv.Key] = Reduce(kv.Value);
        r["properties"] = p;
      }
      if (o["items"] != null) r["items"] = Reduce(o["items"]);
      return r;
    }

    /// <summary>
    /// One neutral message as a Gemini <c>Content</c>.
    ///
    /// <para>The model's turn is replayed with each call as a <c>functionCall</c> part - its arguments the parsed object
    /// the model sent, and the part's <c>thoughtSignature</c> beside it when the model sent one (the docs call
    /// signatures metadata that can be attached to any part, such as <c>functionCall</c> parts; dropping one
    /// breaks reasoning continuity). A call with no arguments recorded is REFUSED, never replayed as <c>{}</c> (#297).</para>
    ///
    /// <para>A result becomes a <c>functionResponse</c> part carrying the call's NAME (Gemini pairs by name, and by id
    /// when the model sent one) found from the call in the transcript, and the result text under <c>response.result</c>.
    /// A result for a call that is not in the transcript is refused: it cannot be named.</para>
    /// </summary>
    private static JsonObject Content(LlmMessage m, List<LlmMessage> all)
    {
      var parts = new JsonArray();
      if (m.Role == LlmRole.Assistant)
      {
        foreach (var b in m.Blocks)
        {
          if (b.Kind == "text") { if (!string.IsNullOrEmpty(b.Text)) parts.Add(new JsonObject { ["text"] = b.Text }); }
          else if (b.Kind == "tool_use")
          {
            if (string.IsNullOrWhiteSpace(b.ToolInputJson))
              throw new LlmException("Dwight stopped this turn before its next step: the record of the earlier call to \"" +
                                     (b.ToolName ?? "a tool") + "\" has no arguments recorded, so replaying it would misstate " +
                                     "what the model asked for. That step was not sent.");
            var call = new JsonObject { ["name"] = b.ToolName, ["args"] = JsonNode.Parse(b.ToolInputJson) };
            if (!IsGenerated(b.ToolUseId)) call["id"] = b.ToolUseId;
            var part = new JsonObject { ["functionCall"] = call };
            if (!string.IsNullOrEmpty(b.Signature)) part["thoughtSignature"] = b.Signature;
            parts.Add(part);
          }
        }
        if (parts.Count == 0) parts.Add(new JsonObject { ["text"] = "" });
        return new JsonObject { ["role"] = "model", ["parts"] = parts };
      }

      var text = new StringBuilder();
      foreach (var b in m.Blocks)
      {
        if (b.Kind == "tool_result")
        {
          var name = NameOfCall(all, b.ToolUseId);
          if (name == null)
            throw new LlmException("Dwight stopped this turn before its next step: a tool result answered a call (" + b.ToolUseId +
                                   ") that is not in the conversation, so it cannot be named for Google. That step was not sent.");
          var response = new JsonObject { ["name"] = name, ["response"] = new JsonObject { ["result"] = b.Text ?? "" } };
          if (!IsGenerated(b.ToolUseId)) response["id"] = b.ToolUseId;
          parts.Add(new JsonObject { ["functionResponse"] = response });
        }
        else if (b.Kind == "text") text.Append(b.Text);
        // Brief DX: an image the person attached, as an inline part, before the words as on the other adapters.
        else if (b.Kind == LlmBlock.ImageKind)
          parts.Add(new JsonObject { ["inlineData"] = new JsonObject { ["mimeType"] = b.MediaType, ["data"] = b.Base64 } });
      }
      if (text.Length > 0 || parts.Count == 0) parts.Add(new JsonObject { ["text"] = text.ToString() });
      return new JsonObject { ["role"] = "user", ["parts"] = parts };
    }

    private static bool IsGenerated(string id) => id != null && id.StartsWith(GeneratedIdPrefix, StringComparison.Ordinal);

    private static string NameOfCall(List<LlmMessage> all, string id)
    {
      foreach (var m in all)
        foreach (var b in m.Blocks)
          if (b.Kind == "tool_use" && string.Equals(b.ToolUseId, id, StringComparison.Ordinal)) return b.ToolName;
      return null;
    }

    /// <summary>Accumulate Gemini's SSE stream (<c>data: {candidates:[{content:{parts:[{text}]}}]}</c>) through
    /// the ONE spec-correct reader (U38, #873): a damaged reply is REFUSED as a failed run in Dwight's words
    /// (<see cref="SseReader.Refusal"/>), never skipped into a shorter answer.
    ///
    /// <para><b>NOT EXERCISED LIVE.</b> Google rejected the key on the capture day, so this is built to the
    /// DOCUMENTED wire shape and tested on a hand-made fixture that says so in its name. The documented stream
    /// has no non-JSON sentinel - every event is a GenerateContentResponse - so none is accepted here. The
    /// capture window (#873) is how a live Google stream becomes a real fixture.</para>
    ///
    /// <para>A reply is FINISHED when a candidate carries <c>finishReason</c>, or when the prompt itself was
    /// blocked (<c>promptFeedback.blockReason</c>, no candidate at all) - the second is a legitimate end the
    /// "ended before the model finished" refusal must not fire on. Its reason becomes the stop reason.</para></summary>
    internal static LlmResponse ReadStream(TextReader reader, CancellationToken cancel)
    {
      // Lane B (#873): the raw stream, recorded inside a capture window only; changes nothing parsed.
      using (var cap = StreamCapture.Begin("google"))
      {
        var response = new LlmResponse();
        try { return Assemble(reader, cap, cancel, response); }
        catch (StreamDamageException damage) { throw SseReader.Refusal(damage); }
        catch (OperationCanceledException stopped) { UsageCarrier.Attach(stopped, response.Usage); throw; }   // each chunk's usageMetadata said the count so far
      }
    }

    private static LlmResponse Assemble(TextReader reader, StreamCapture.Recording cap, CancellationToken cancel, LlmResponse response)
    {
      var text = new StringBuilder();
      var calls = new List<LlmBlock>();
      string finishReason = null;
      var finished = false;

      foreach (var (ev, node) in SseReader.JsonEvents(reader, sentinel: null, cap: cap, cancel: cancel))
      {
        SseReader.Shape(ev, () =>
        {
          var err = node["error"];
          if (err != null)
            throw new LlmException("Google returned a streaming error: " + (err["message"]?.GetValue<string>() ?? err.ToJsonString()));

          var candidate = node["candidates"]?.AsArray() is JsonArray cs && cs.Count > 0 ? cs[0] : null;
          var parts = candidate?["content"]?["parts"]?.AsArray();
          if (parts != null)
            foreach (var p in parts)
            {
              // A part flagged `thought` is the model's reasoning summary, not its answer: never part of the text.
              var isThought = p?["thought"] is JsonValue tv && tv.TryGetValue<bool>(out var th) && th;
              var t = p?["text"]?.GetValue<string>();
              if (t != null && !isThought) text.Append(t);

              if (p?["functionCall"] != null) calls.Add(Call(ev, p));
            }
          var fr = candidate?["finishReason"]?.GetValue<string>();
          if (fr != null) { finishReason = fr; finished = true; }
          var blocked = node["promptFeedback"]?["blockReason"]?.GetValue<string>();
          if (blocked != null) { finishReason = blocked; finished = true; }
          ApplyUsage(response.Usage, node["usageMetadata"]);
        });
      }

      if (!finished)
        throw new StreamDamageException(0, 0, "ended before the model finished its reply (no finishReason arrived)");

      // Google's own verdict that the model's call was unusable: said in its words, not turned into a shorter answer.
      if (string.Equals(finishReason, "MALFORMED_FUNCTION_CALL", StringComparison.Ordinal))
        throw new LlmException("Google reported that the model's tool call was malformed (MALFORMED_FUNCTION_CALL), so Dwight did not act on it. Ask again.");

      // A reply cut at the token limit is Truncated (the loop's word for it is Anthropic's "max_tokens"). Any call it
      // had reached is not dispatched: the turn ends on the stated outcome, not on an action from a cut-off reply.
      var truncated = string.Equals(finishReason, "MAX_TOKENS", StringComparison.Ordinal);
      if (truncated) calls.Clear();

      // The loop reads Anthropic's words: a call in hand is tool_use (Gemini says STOP beside one), a cut-off is
      // max_tokens. Translated HERE, at the adapter, so the loop does not learn each provider's dialect.
      response.StopReason = truncated ? "max_tokens" : calls.Count > 0 ? "tool_use" : finishReason;

      // The text block stays when there is text, and always when there is no call; an empty one beside a call is
      // dropped (the transcript pre-flight refuses an empty text block).
      if (text.Length > 0 || calls.Count == 0)
        response.Blocks.Add(LlmBlock.TextBlock(text.ToString()));
      response.Blocks.AddRange(calls);
      return response;
    }

    /// <summary>One <c>functionCall</c> part, whole (Gemini sends a call as a complete part, not as fragments): name,
    /// <c>args</c> (an object; absent means the tool takes none) and the optional <c>id</c>. Its sibling
    /// <c>thoughtSignature</c> is kept on the block to be sent back. Refused by event: no name, or arguments that are
    /// not an object.</summary>
    private static LlmBlock Call(SseEvent ev, JsonNode part)
    {
      var fc = part["functionCall"];
      var name = fc?["name"]?.GetValue<string>();
      if (string.IsNullOrEmpty(name))
        throw new StreamDamageException(ev.Index, ev.Line, "carries a function call that names no tool, so the call was not made");
      var args = fc["args"];
      if (args != null && !(args is JsonObject))
        throw new StreamDamageException(ev.Index, ev.Line, "carries the call to \"" + name + "\" whose arguments are not a set of arguments, so the call was not made");
      var id = fc["id"]?.GetValue<string>();
      var block = LlmBlock.ToolUse(string.IsNullOrEmpty(id) ? GeneratedIdPrefix + Guid.NewGuid().ToString("N").Substring(0, 10) : id, name, args == null ? "{}" : args.ToJsonString());
      var sig = part["thoughtSignature"]?.GetValue<string>();
      if (!string.IsNullOrEmpty(sig)) block.Signature = sig;
      return block;
    }

    private static void ApplyUsage(LlmUsage usage, JsonNode u)
    {
      if (usage == null || u == null) return;
      usage.InputTokens = ReadInt(u, "promptTokenCount", usage.InputTokens);
      usage.OutputTokens = ReadInt(u, "candidatesTokenCount", usage.OutputTokens);
    }

    private static int ReadInt(JsonNode parent, string key, int fallback)
    {
      try { var v = parent[key]; return v != null ? v.GetValue<int>() : fallback; } catch { return fallback; }
    }

    internal static string DescribeError(int status, string body)
    {
      string apiMessage = null;
      try { apiMessage = JsonNode.Parse(body)?["error"]?["message"]?.GetValue<string>(); } catch { }
      var limit = ProviderLimit.OfGoogle(status, body);   // Brief CO
      if (limit != ProviderLimitKind.None) return ProviderLimit.Sentence(limit, "Google", apiMessage);
      switch (status)
      {
        case 400: return $"Google rejected the request (400){(apiMessage != null ? ": " + apiMessage : "")}.";
        case 401:
        case 403: return "Google rejected the API key (401/403). Re-enter it in Dwight > Settings.";
        case 429: return "Google is rate-limiting (429). Wait a moment and try again. " + ProviderLimit.NothingChanged;
        default:
          var detail = apiMessage ?? (body != null && body.Length > 200 ? body.Substring(0, 200) : body);
          return $"Google returned an error ({status}): {detail}";
      }
    }
  }
}
