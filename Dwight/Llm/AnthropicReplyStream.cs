using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;

namespace Dwight.Llm
{
  /// <summary>
  /// The Anthropic Messages reply stream, assembled into one <see cref="LlmResponse"/> (U38, #873). Moved out of
  /// <see cref="AnthropicProvider"/> - which needs the live model catalogue - so the assembly can be tested on
  /// the streams Anthropic really sent (ADR-091), byte for byte.
  ///
  /// <para>It reads through <see cref="SseReader"/>, so a damaged EVENT is refused there. What it adds is the
  /// damage only the Messages protocol can see, each refused before anything in the reply is used:</para>
  /// <list type="bullet">
  /// <item>a delta or a stop for a content block that never started (its start event was lost), or a block
  /// started twice;</item>
  /// <item><b>a tool call whose argument fragments do not join into a JSON object</b> - the case the old loop
  /// could not even see: it dispatched whatever the surviving fragments made;</item>
  /// <item>a stream that ends before <c>message_stop</c>, or with a block still open - a reply cut short,
  /// which the old loop returned as a shorter answer.</item>
  /// </list>
  ///
  /// <para>A tool call with no argument fragments carries the <c>input</c> its start event sent (<c>{}</c>, for a
  /// tool that takes none) - the model's own words, not an invented default. One that has neither is refused.</para>
  /// </summary>
  internal static class AnthropicReplyStream
  {
    /// <summary>Read the whole stream, or throw: a damaged reply is an <see cref="LlmException"/> carrying
    /// <see cref="SseReader.Refusal"/>'s sentence; a streamed <c>error</c> event is an LlmException with
    /// Anthropic's message; Stop is an <see cref="System.OperationCanceledException"/>.</summary>
    public static LlmResponse Read(TextReader reader, CancellationToken cancel = default)
    {
      // Lane B (#873): the raw stream, recorded inside a capture window only. Records; changes nothing parsed.
      using (var cap = StreamCapture.Begin("anthropic"))
      {
        var response = new LlmResponse();
        try { return Assemble(reader, cap, cancel, response); }
        catch (StreamDamageException damage) { throw SseReader.Refusal(damage); }
        catch (System.OperationCanceledException stopped) { UsageCarrier.Attach(stopped, response.Usage); throw; }   // message_start said the input's count: a Stop after it knows the bill's input
      }
    }

    private static LlmResponse Assemble(TextReader reader, StreamCapture.Recording cap, CancellationToken cancel, LlmResponse response)
    {
      var building = new SortedDictionary<int, Block>();
      var stopped = false;

      foreach (var (ev, node) in SseReader.JsonEvents(reader, sentinel: null, cap: cap, cancel: cancel))
      {
        SseReader.Shape(ev, () =>
        {
          switch (node["type"]?.GetValue<string>())
          {
            case "message_start":
              ApplyUsage(response.Usage, node["message"]?["usage"]);
              break;

            case "content_block_start":
            {
              var idx = Index(ev, node);
              if (building.ContainsKey(idx))
                throw new StreamDamageException(ev.Index, ev.Line, "starts content block " + idx + " a second time");
              var cb = node["content_block"];
              var kind = cb?["type"]?.GetValue<string>();
              var b = new Block { Kind = kind };
              if (kind == "text")
                b.Text.Append(cb["text"]?.GetValue<string>() ?? "");
              else if (kind == "tool_use")
              {
                b.ToolUseId = cb["id"]?.GetValue<string>();
                b.ToolName = cb["name"]?.GetValue<string>();
                b.StartInput = cb["input"];
              }
              else if (kind == "server_tool_use")
              {
                // Brief V: the provider's own search call. It streams like a client tool_use (a start, then input
                // fragments), but it is the PROVIDER'S to run - it never reaches Dwight's dispatch - and it returns to
                // the provider exactly as received, so the whole start block is kept and only its input is completed.
                b.ToolUseId = cb["id"]?.GetValue<string>();
                b.ToolName = cb["name"]?.GetValue<string>();
                b.StartInput = cb["input"];
                b.Start = cb.DeepClone() as JsonObject;
              }
              else if (kind == "web_search_tool_result")
              {
                // The result arrives COMPLETE in its start event, with no deltas (the provider's streaming page). Kept
                // verbatim: encrypted_content must come back unchanged. An error is a 200 with an error object inside;
                // it is noted so Dwight can say so, and is NOT a failed run.
                b.ToolUseId = cb["tool_use_id"]?.GetValue<string>();
                b.Raw = cb.ToJsonString();
                if (cb["content"] is JsonObject err && err["type"]?.GetValue<string>() == "web_search_tool_result_error")
                  response.WebSearchErrors.Add(err["error_code"]?.GetValue<string>() ?? "unavailable");
              }
              else if (kind == "thinking")
              {
                // #192b: seed both halves. Display defaults to omitted, so the summary usually stays
                // empty and the signature is the whole substance - it must still round-trip verbatim.
                b.Text.Append(cb["thinking"]?.GetValue<string>() ?? "");
                b.Signature.Append(cb["signature"]?.GetValue<string>() ?? "");
              }
              else if (kind == "redacted_thinking")
                b.Data = cb["data"]?.GetValue<string>();
              building[idx] = b;
              break;
            }

            case "content_block_delta":
            {
              var b = Open(ev, node, building, "continues");
              var delta = node["delta"];
              switch (delta?["type"]?.GetValue<string>())
              {
                case "text_delta":
                {
                  var chunk = delta["text"]?.GetValue<string>() ?? "";
                  LlmTrace.RecordDecodedText(chunk);   // D2: what the DECODER produced, code points and all
                  b.Text.Append(chunk);
                  break;
                }
                // Brief V: a citation attached to the text being streamed. Kept whole and returned on the same block.
                case "citations_delta":
                  if (delta["citation"] != null) b.Citations.Add(delta["citation"].DeepClone());
                  break;
                case "input_json_delta":
                  b.Input.Append(delta["partial_json"]?.GetValue<string>() ?? "");
                  b.Fragments++;
                  break;
                // #192b: thinking arrives as its own delta pair - summary text, then the signature.
                case "thinking_delta": b.Text.Append(delta["thinking"]?.GetValue<string>() ?? ""); break;
                case "signature_delta": b.Signature.Append(delta["signature"]?.GetValue<string>() ?? ""); break;
                // Any other delta type (citations, a type newer than this reader) adds nothing this reader
                // carries, so it is passed over - not damage.
              }
              break;
            }

            case "content_block_stop":
            {
              var b = Open(ev, node, building, "closes");
              b.Closed = true;
              // Brief BZ: damage is held until the stop reason is known. A refusal can arrive mid-stream, and what it cut short is to be discarded, not reported as a damaged reply.
              if (b.Kind == "tool_use" || b.Kind == "server_tool_use")
              {
                try { b.FinalInput = Arguments(ev, b); }
                catch (StreamDamageException damage) { b.Damage = damage; }
              }
              break;
            }

            case "message_delta":
            {
              var sr = node["delta"]?["stop_reason"]?.GetValue<string>();
              if (sr != null) response.StopReason = sr;
              // The vendor's pages show stop_details on the whole message and do not say where a stream carries it. It is informational, so both places are read.
              if ((node["delta"]?["stop_details"] ?? node["stop_details"]) is JsonObject details) response.RefusalCategory = details["category"]?.GetValue<string>();
              ApplyUsage(response.Usage, node["usage"]); // cumulative output (+ any cache fields)
              // Keep the usage object VERBATIM alongside the parse (#199 instrument): "we read 0" and
              // "the field was never sent" are the same number downstream, and only the raw form tells
              // them apart. DEBUG-only; compiles out of Release.
              LlmTrace.RecordRawUsage(node["usage"]?.ToJsonString());
              break;
            }

            case "message_stop":
              stopped = true;
              break;

            case "error":
            {
              var m = node["error"]?["message"]?.GetValue<string>();
              throw new LlmException(m ?? "Claude returned a streaming error.");
            }
            // ping, and any event type newer than this reader: nothing to do.
          }
        });
      }

      // A reply cut short is not a shorter answer (U38): the old loop returned whatever had arrived.
      if (!LlmRefusal.Is(response))
      {
        foreach (var kv in building)
        {
          if (!kv.Value.Closed)
            throw new StreamDamageException(0, 0, "ended with content block " + kv.Key + " still open, so the reply was cut short");
          if (kv.Value.Damage != null) throw kv.Value.Damage;
        }
      }
      if (!stopped)
        throw new StreamDamageException(0, 0, "ended before the model finished its reply (no message_stop arrived)");

      foreach (var kv in building)
      {
        var b = kv.Value;
        if (!b.Closed || b.Damage != null) continue;   // only a refusal gets here with either: its partial output is discarded by the caller anyway
        if (b.Kind == "text")
        {
          var text = LlmBlock.TextBlock(b.Text.ToString());
          if (b.Citations.Count > 0) text.CitationsJson = b.Citations.ToJsonString();
          response.Blocks.Add(text);
        }
        else if (b.Kind == "server_tool_use")
        {
          var raw = b.Start ?? new JsonObject();
          raw["input"] = JsonNode.Parse(b.FinalInput);
          response.Blocks.Add(LlmBlock.ServerToolUse(b.ToolUseId, raw.ToJsonString()));
        }
        else if (b.Kind == "web_search_tool_result")
          response.Blocks.Add(LlmBlock.WebSearchResult(b.ToolUseId, b.Raw));
        else if (b.Kind == "tool_use")
          response.Blocks.Add(LlmBlock.ToolUse(b.ToolUseId, b.ToolName, b.FinalInput));
        // #192b: thinking blocks are MATERIALISED, not dropped. They were parsed and then discarded once,
        // so any continuation echoed a stripped assistant turn - a 400 on the very first repair round-trip
        // once thinking was ever enabled.
        else if (b.Kind == "thinking")
          response.Blocks.Add(LlmBlock.Thinking(b.Text.ToString(), b.Signature.ToString()));
        else if (b.Kind == "redacted_thinking")
          response.Blocks.Add(LlmBlock.RedactedThinking(b.Data));
      }
      return response;
    }

    /// <summary>The arguments of a finished tool call, exactly as the model sent them - or a refusal. The
    /// fragments must join into a JSON OBJECT (parsing is not the bar, #828); with no fragments the start
    /// event's own <c>input</c> is the model's words.</summary>
    private static string Arguments(SseEvent stop, Block b)
    {
      var call = string.IsNullOrEmpty(b.ToolName) ? "a tool call" : "the call to \"" + b.ToolName + "\"";
      if (b.Fragments > 0 && b.Input.Length > 0)
      {
        var joined = b.Input.ToString();
        JsonNode args;
        try { args = JsonNode.Parse(joined); }
        catch (System.Text.Json.JsonException)
        {
          throw new StreamDamageException(stop.Index, stop.Line,
            "closes " + call + " whose argument fragments do not join into valid JSON, so the call was not made");
        }
        if (!(args is JsonObject))
          throw new StreamDamageException(stop.Index, stop.Line,
            "closes " + call + " whose arguments are not a set of arguments, so the call was not made");
        return joined;
      }
      if (b.StartInput is JsonObject start) return start.ToJsonString();
      throw new StreamDamageException(stop.Index, stop.Line, "closes " + call + " with no arguments sent, so the call was not made");
    }

    private static int Index(SseEvent ev, JsonNode node)
    {
      var idx = node["index"]?.GetValue<int>();
      if (idx == null) throw new StreamDamageException(ev.Index, ev.Line, "names no content block");
      return idx.Value;
    }

    private static Block Open(SseEvent ev, JsonNode node, SortedDictionary<int, Block> building, string verb)
    {
      var idx = Index(ev, node);
      if (!building.TryGetValue(idx, out var b))
        throw new StreamDamageException(ev.Index, ev.Line,
          verb + " content block " + idx + ", which never started, so an earlier event was lost");
      if (b.Closed)
        throw new StreamDamageException(ev.Index, ev.Line, verb + " content block " + idx + " after it had closed");
      return b;
    }

    /// <summary>Accumulate the usage fields the Messages API reports across the stream
    /// (input + cache counts arrive in <c>message_start</c>, cumulative output in
    /// <c>message_delta</c>). Absent fields keep the prior value (best-effort, ADR-029).</summary>
    private static void ApplyUsage(LlmUsage usage, JsonNode u)
    {
      if (usage == null || u == null) return;
      usage.InputTokens = ReadInt(u, "input_tokens", usage.InputTokens);
      usage.OutputTokens = ReadInt(u, "output_tokens", usage.OutputTokens);
      usage.CacheReadInputTokens = ReadInt(u, "cache_read_input_tokens", usage.CacheReadInputTokens);
      usage.CacheCreationInputTokens = ReadInt(u, "cache_creation_input_tokens", usage.CacheCreationInputTokens);
      // #199: billed reasoning tokens - the INBOUND half of the ADR-082 proof. Nested under
      // output_tokens_details, and on a stream it arrives on the final message_delta.
      var details = u["output_tokens_details"];
      if (details != null) usage.ThinkingTokens = ReadInt(details, "thinking_tokens", usage.ThinkingTokens);
      // Brief V: searches the provider ran and bills for, cumulative like the rest of message_delta's usage.
      var server = u["server_tool_use"];
      if (server != null) usage.WebSearchRequests = ReadInt(server, "web_search_requests", usage.WebSearchRequests);
    }

    private static int ReadInt(JsonNode parent, string key, int fallback)
    {
      try { var v = parent[key]; return v != null ? v.GetValue<int>() : fallback; }
      catch { return fallback; }
    }

    /// <summary>A content block being assembled from stream deltas.</summary>
    private sealed class Block
    {
      public string Kind;
      public bool Closed;
      public StreamDamageException Damage;                   // a call that did not close into arguments, reported unless the reply was refused
      public string ToolUseId;
      public string ToolName;
      public JsonNode StartInput;                            // tool_use: the input its start event carried
      public int Fragments;                                  // tool_use: input_json_delta events seen
      public string FinalInput;                              // tool_use: decided at its stop
      public string Data;                                    // redacted_thinking payload (opaque)
      public readonly StringBuilder Text = new StringBuilder();
      public readonly StringBuilder Input = new StringBuilder();
      public readonly StringBuilder Signature = new StringBuilder();   // thinking (#192b)
      public JsonObject Start;                               // server_tool_use: its start block, returned with the input completed
      public string Raw;                                     // web_search_tool_result: the block verbatim
      public readonly JsonArray Citations = new JsonArray(); // text: the citations streamed onto it
    }
  }
}
