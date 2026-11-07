using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;

namespace Dwight.Llm
{
  /// <summary>
  /// OPENAI'S RESPONSES ENDPOINT (Brief BV): the body and the reader for the OpenAI instance's tool-bearing turns. Chat Completions refuses
  /// tool calling with any reasoning effort but none on these models, and refuses function tools outright on GPT-6.1 Sol and GPT-6 Astra.
  /// Verbs stay on Chat Completions, and every Ollama instance stays there too: <see cref="OpenAIProvider"/> routes, and this knows only its wire.
  ///
  /// STORE IS FALSE, so nothing is kept on OpenAI's side between steps. That is why every output item goes back in the next step, UNCHANGED and in
  /// order (the documentation's "must" for reasoning items, its "preserve every item in the response's output array" for the rest), as thinking
  /// blocks and their signatures do on Anthropic (#192b). They ride <see cref="LlmBlock.RawJson"/> inside one turn's transcript, which is built in
  /// memory: the session file holds messages with a source, so an item reaches neither the user's screen nor the disk.
  ///
  /// GH-free and headless-tested (ADR-091).
  /// </summary>
  internal static class OpenAIResponses
  {
    /// <summary>Thinking off. The two models that refuse it say so in a stated 400 (#208): nothing here picks a different effort for them.</summary>
    public const string EffortOff = "none";

    public const string EncryptedReasoning = "reasoning.encrypted_content";

    /// <summary>What the capture window files these streams under, apart from the Chat Completions ones.</summary>
    public const string CaptureLabel = "openai-responses";

    // ---------------------------------------------------------------- the request

    public static string BuildBody(LlmRequest request, string modelId)
    {
      var input = new JsonArray();
      foreach (var m in request.Messages) AddMessage(input, m);

      var root = new JsonObject { ["model"] = modelId };
      if (!string.IsNullOrEmpty(request.System)) root["instructions"] = request.System;
      root["input"] = input;
      root["stream"] = true;
      root["store"] = false;   // the endpoint stores by default; our privacy statements say it does not
      root["include"] = new JsonArray(JsonValue.Create(EncryptedReasoning));

      // The same neutral descriptors the other adapters are built from: a third translation of one list, never a second list. `strict` is said out
      // loud because omitting it asks for strict mode with a quiet fallback, and our schemas use the optional fields strict mode forbids.
      var tools = new JsonArray();
      foreach (var t in request.Tools)
        tools.Add(new JsonObject
        {
          ["type"] = "function",
          ["name"] = t.Name,
          ["description"] = t.Description,
          ["parameters"] = JsonNode.Parse(t.InputSchemaJson),
          ["strict"] = false,
        });
      root["tools"] = tools;
      if (request.ForbidTools) root["tool_choice"] = "none";
      else if (!string.IsNullOrEmpty(request.ForceToolName))
        root["tool_choice"] = new JsonObject { ["type"] = "function", ["name"] = request.ForceToolName };

      // #216: max_output_tokens INCLUDES reasoning, so MaxTokens stays the answer budget and the reserve goes on top. Over the ceiling the effort steps
      // down, never the answer, and the step-down is recorded where the Anthropic body's is, so the driver's one reader of that note sees it.
      if (request.Thinking)
      {
        var plan = ThinkingBudget.Resolve(request.MaxTokens, request.Effort);
        AnthropicRequestBody.LastPlanNote = plan.Note;
        root["max_output_tokens"] = plan.MaxTokens;
        if (!string.IsNullOrWhiteSpace(plan.Effort)) root["reasoning"] = new JsonObject { ["effort"] = plan.Effort };
      }
      else
      {
        root["max_output_tokens"] = request.MaxTokens;
        root["reasoning"] = new JsonObject { ["effort"] = EffortOff };
      }
      if (request.Temperature.HasValue) root["temperature"] = request.Temperature.Value;
      return root.ToJsonString();
    }

    /// <summary>One neutral message as input items. What came from this endpoint goes back as it came. Text from an earlier turn has no item, because
    /// a chat's history is rebuilt from what was said, so it is written as a plain assistant message.</summary>
    private static void AddMessage(JsonArray input, LlmMessage m)
    {
      if (m.Role == LlmRole.Assistant)
      {
        foreach (var b in m.Blocks)
        {
          switch (b.Kind)
          {
            case LlmBlock.ResponseItemKind:
              input.Add(JsonNode.Parse(b.RawJson));
              break;
            case "text":
              input.Add(b.RawJson != null ? JsonNode.Parse(b.RawJson) : new JsonObject { ["role"] = "assistant", ["content"] = b.Text ?? "" });
              break;
            case "tool_use":
              if (string.IsNullOrWhiteSpace(b.ToolInputJson))
                throw new LlmException("Dwight stopped this turn before its next step: the record of the earlier call to \"" +
                                       (b.ToolName ?? "a tool") + "\" has no arguments recorded, so replaying it would misstate " +
                                       "what the model asked for. That step was not sent.");
              input.Add(b.RawJson != null
                ? JsonNode.Parse(b.RawJson)
                : new JsonObject { ["type"] = "function_call", ["call_id"] = b.ToolUseId, ["name"] = b.ToolName, ["arguments"] = b.ToolInputJson });
              break;
          }
        }
        return;
      }

      var text = new StringBuilder();
      var hasResults = false;
      var images = new JsonArray();
      foreach (var b in m.Blocks)
      {
        if (b.Kind == "tool_result")
        {
          hasResults = true;
          input.Add(new JsonObject { ["type"] = "function_call_output", ["call_id"] = b.ToolUseId, ["output"] = b.Text ?? "" });
        }
        else if (b.Kind == "text") text.Append(b.Text);
        else if (b.Kind == LlmBlock.ImageKind)
          images.Add(new JsonObject { ["type"] = "input_image", ["image_url"] = "data:" + b.MediaType + ";base64," + b.Base64 });
      }
      // Brief DX: a user turn is a bare string unless it carries an image, so every body with none is byte for byte what it was.
      if (images.Count > 0)
      {
        if (text.Length > 0) images.Add(new JsonObject { ["type"] = "input_text", ["text"] = text.ToString() });
        input.Add(new JsonObject { ["role"] = "user", ["content"] = images });
      }
      else if (!hasResults || text.Length > 0)
        input.Add(new JsonObject { ["role"] = "user", ["content"] = text.ToString() });
    }

    /// <summary>How many reasoning items came back: the trace's witness from the content stream, independent of the usage object.</summary>
    public static int ReasoningItems(LlmResponse response) =>
      response.Blocks.Count(b => b.Kind == LlmBlock.ResponseItemKind && b.RawJson != null && b.RawJson.Contains("\"type\":\"reasoning\""));

    // ---------------------------------------------------------------- the reader

    /// <summary>One reader, the same law as the others: through <see cref="SseReader"/> and <see cref="StreamCapture"/>, a damaged reply refused and never
    /// repaired. The documentation does not say this endpoint sends <c>[DONE]</c>; OpenAI's own client stops on it, so it is tolerated, and it is not the end
    /// of a reply: that is a terminal event.</summary>
    internal static LlmResponse ReadStream(TextReader reader, CancellationToken cancel, string label)
    {
      using (var cap = StreamCapture.Begin(CaptureLabel))
      {
        try { return Assemble(reader, cap, cancel, label ?? "OpenAI"); }
        catch (StreamDamageException damage) { throw SseReader.Refusal(damage); }
      }
    }

    private static LlmResponse Assemble(TextReader reader, StreamCapture.Recording cap, CancellationToken cancel, string label)
    {
      var items = new SortedDictionary<int, JsonObject>();
      var begun = new Dictionary<int, SseEvent>();
      var finished = new Dictionary<int, SseEvent>();
      JsonNode finalResponse = null;
      string terminal = null;
      SseEvent last = null;

      foreach (var (ev, node) in SseReader.JsonEvents(reader, sentinel: "[DONE]", cap: cap, cancel: cancel))
      {
        last = ev;
        if (node == null || terminal != null) continue;
        SseReader.Shape(ev, () =>
        {
          var type = node["type"]?.GetValue<string>();
          if (string.IsNullOrEmpty(type)) throw new StreamDamageException(ev.Index, ev.Line, "carries no event type");
          switch (type)
          {
            case "error":
              throw new LlmException(label + " returned a streaming error: " + (node["message"]?.GetValue<string>() ?? node.ToJsonString()));
            case "response.failed":
              throw new LlmException(FailedLine(label, node["response"]?["error"]));
            case "response.output_item.added":
              begun[OutputIndex(ev, node)] = ev;
              break;
            case "response.output_item.done":
              var index = OutputIndex(ev, node);
              if (!begun.ContainsKey(index))
                throw new StreamDamageException(ev.Index, ev.Line, "finishes output item " + index + ", which never started, so an earlier event was lost");
              if (!(node["item"] is JsonObject item)) throw new StreamDamageException(ev.Index, ev.Line, "finishes an output item that is not an object");
              items[index] = item;
              finished[index] = ev;
              break;
            case "response.completed":
            case "response.incomplete":
              terminal = type;
              finalResponse = node["response"];
              LlmTrace.RecordRawUsage(finalResponse?["usage"]?.ToJsonString());
              break;
          }
        });
      }

      if (terminal == null)
        throw new StreamDamageException(last?.Index ?? 0, last?.Line ?? 0, "ended before the model finished its reply (no terminal event arrived)");

      var truncated = false;
      if (terminal == "response.incomplete")
      {
        var reason = ReadString(finalResponse?["incomplete_details"]?["reason"]);
        if (reason == "content_filter")
          throw new LlmException(label + " stopped this reply at its content filter, so there is no answer to act on. Nothing further was done.");
        if (reason != "max_output_tokens")
          throw new LlmException(label + " ended this reply incomplete (" + (reason ?? "no reason given") + "), so there is no answer to act on. Nothing further was done.");
        truncated = true;
      }

      // A reply that said it finished with an item still open has a part missing. A reply cut at the budget has one by construction.
      if (!truncated)
        foreach (var open in begun)
          if (!items.ContainsKey(open.Key))
            throw new StreamDamageException(open.Value.Index, open.Value.Line, "starts output item " + open.Key + " and the reply ended without finishing it");

      return Build(items, finished, finalResponse, truncated, label);
    }

    private static LlmResponse Build(SortedDictionary<int, JsonObject> items, Dictionary<int, SseEvent> finished, JsonNode finalResponse, bool truncated, string label)
    {
      var response = new LlmResponse();
      var calls = 0;
      var anyText = false;
      var thought = false;
      string refusal = null;

      foreach (var kv in items)
      {
        var item = kv.Value;
        var type = ReadString(item["type"]);
        if (type == "message")
        {
          var text = new StringBuilder();
          foreach (var part in item["content"] as JsonArray ?? new JsonArray())
          {
            var partType = ReadString(part?["type"]);
            if (partType == "output_text") text.Append(ReadString(part["text"]));
            else if (partType == "refusal") refusal = ReadString(part["refusal"]) ?? "";
          }
          if (text.Length > 0)
          {
            anyText = true;
            response.Blocks.Add(new LlmBlock { Kind = "text", Text = text.ToString(), RawJson = item.ToJsonString() });
          }
        }
        else if (type == "function_call")
        {
          if (truncated) continue;   // incomplete by construction, so it is not dispatched: the turn ends on the stated outcome
          response.Blocks.Add(CallBlock(item, finished[kv.Key]));
          calls++;
        }
        else
        {
          // Reasoning, and anything else the endpoint adds to its output: the documentation says to replay the complete output, so it goes back unread.
          if (type == "reasoning") thought = true;
          response.Blocks.Add(LlmBlock.ResponseItem(item.ToJsonString()));
        }
      }

      if (refusal != null) throw new LlmException(label + "'s model refused this request: " + (refusal.Length == 0 ? "it gave no reason." : refusal));

      ApplyUsage(response.Usage, finalResponse?["usage"]);
      response.ThoughtWithoutAnswer = truncated && !anyText && calls == 0 && (thought || response.Usage.ThinkingTokens > 0);
      response.StopReason = truncated ? "max_tokens" : calls > 0 ? "tool_use" : "stop";

      // A reply with nothing to say is still a reply, as on the other adapters.
      if (!anyText && calls == 0) response.Blocks.Add(LlmBlock.TextBlock(""));
      return response;
    }

    /// <summary>A finished call. The bar is the one every reader holds: it names a tool, it has an id to answer, and its arguments are a JSON OBJECT (#828).</summary>
    private static LlmBlock CallBlock(JsonObject item, SseEvent ev)
    {
      var name = ReadString(item["name"]);
      var callId = ReadString(item["call_id"]);
      var args = ReadString(item["arguments"]);
      var what = string.IsNullOrEmpty(name) ? "a tool call" : "the call to \"" + name + "\"";
      if (string.IsNullOrEmpty(name)) throw new StreamDamageException(ev.Index, ev.Line, "ends " + what + " that never named a tool, so the call was not made");
      if (string.IsNullOrEmpty(callId)) throw new StreamDamageException(ev.Index, ev.Line, "ends " + what + " with no call id, so it could not be answered and was not made");
      if (string.IsNullOrEmpty(args)) throw new StreamDamageException(ev.Index, ev.Line, "ends " + what + " with no arguments sent, so the call was not made");
      JsonNode parsed;
      try { parsed = JsonNode.Parse(args); }
      catch (JsonException) { throw new StreamDamageException(ev.Index, ev.Line, "ends " + what + " whose arguments are not valid JSON, so the call was not made"); }
      if (!(parsed is JsonObject)) throw new StreamDamageException(ev.Index, ev.Line, "ends " + what + " whose arguments are not a set of arguments, so the call was not made");

      var block = LlmBlock.ToolUse(callId, name, args);
      block.RawJson = item.ToJsonString();
      return block;
    }

    private static int OutputIndex(SseEvent ev, JsonNode node)
    {
      var i = node["output_index"];
      if (i == null) throw new StreamDamageException(ev.Index, ev.Line, "names no output index");
      return i.GetValue<int>();
    }

    internal static string FailedLine(string label, JsonNode error)
    {
      var message = ReadString(error?["message"]);
      var code = ReadString(error?["code"]);
      // Brief CO: the same limit, the same sentence, whether it came as a status or in the stream.
      var limit = string.Equals(label, "OpenAI", StringComparison.Ordinal) ? ProviderLimit.OfOpenAICode(code, ReadString(error?["type"])) : ProviderLimitKind.None;
      if (limit != ProviderLimitKind.None) return ProviderLimit.Sentence(limit, label, message);
      return label + " could not finish this reply: " + (message ?? "it gave no reason") + (string.IsNullOrEmpty(code) ? "" : " (" + code + ")") + ". Nothing further was done.";
    }

    /// <summary>input_tokens INCLUDES the cached and cache-written tokens, where the loop's InputTokens is the uncached rest, as in Brief BT's reading of
    /// the other shape. Reasoning tokens are part of the output tokens, and are recorded beside them.</summary>
    private static void ApplyUsage(LlmUsage usage, JsonNode u)
    {
      if (u == null) return;
      var details = u["input_tokens_details"];
      var cached = ReadInt(details?["cached_tokens"]);
      var written = ReadInt(details?["cache_write_tokens"]);
      usage.CacheReadInputTokens = cached;
      usage.CacheCreationInputTokens = written;
      usage.InputTokens = Math.Max(0, ReadInt(u["input_tokens"]) - cached - written);
      usage.OutputTokens = ReadInt(u["output_tokens"]);
      usage.ThinkingTokens = ReadInt(u["output_tokens_details"]?["reasoning_tokens"]);
    }

    private static int ReadInt(JsonNode n) => n != null ? n.GetValue<int>() : 0;

    private static string ReadString(JsonNode n) => n != null ? n.GetValue<string>() : null;
  }
}
