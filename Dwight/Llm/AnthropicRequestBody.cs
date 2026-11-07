using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace Dwight.Llm
{
  /// <summary>
  /// Brief BZ: what one model's wire takes, from the catalogue and never from its id (ADR-082). The default is the shape every row before Sonnet 5.5 was measured on, so a caller
  /// with no row builds the body it always built.
  /// </summary>
  internal sealed class AnthropicWire
  {
    public bool ForcedTool = true;
    public bool StructuredFormat;
    public string ThinkingOffEffort;

    /// <summary>Ask the API to enforce the prefix check on this request whatever the account's age. See <see cref="StrictPrefixCheckForGate"/>.</summary>
    public bool StrictPrefixCheck;

    /// <summary>
    /// THE GATE'S SWITCH, in memory and off in the product. The prefix check is enforced by default only on accounts created on or after 31 August 2026, so on an older key a clean run of
    /// a long turn proves nothing, and a person on a new key is the one who would get the 400. "Any value, including error, opts the request into enforcement" (Preserved thinking).
    /// </summary>
    internal static bool StrictPrefixCheckForGate;

    /// <summary>The positive control for that switch (#212): elide on a bound row anyway. With the strict check on, the next step must be a 400. If it is not, the check is not running and a clean
    /// row beside it has measured nothing. Never set in the product.</summary>
    internal static bool ElideAnywayForGate;

    /// <summary>The header <c>block_binding</c> needs, or null. Sending the field without it is a 400.</summary>
    public string BetaHeader => StrictPrefixCheck ? "thinking-binding-controls-2026-08-01" : null;

    public static readonly AnthropicWire Default = new AnthropicWire();

    public static AnthropicWire Of(Dwight.ModelInfo model) => model == null ? Default : new AnthropicWire
    {
      ForcedTool = model.ForcedToolChoice,
      StructuredFormat = model.StructuredFormat,
      ThinkingOffEffort = model.ThinkingOffEffort,
      StrictPrefixCheck = model.ThinkingBound && StrictPrefixCheckForGate,
    };
  }

  /// <summary>
  /// The Anthropic Messages request body, built as a pure function (ADR-091). Extracted from
  /// <see cref="AnthropicProvider"/> at Phase-8 WP-2 so the exact JSON Dwight puts on the wire can be
  /// ASSERTED rather than trusted - this is the one place a mistake would silently change behaviour for
  /// all five shipped verbs at once.
  ///
  /// THE INERTNESS CONTRACT (#192a). With <see cref="ThinkingWireState.Omit"/> the emitted body is
  /// byte-identical to what shipped before thinking existed: no <c>thinking</c> key, no
  /// <c>output_config</c> key, every other key unchanged and in the same order. The thinking fields are
  /// appended LAST precisely so their absence leaves the rest of the document untouched.
  /// </summary>
  internal static class AnthropicRequestBody
  {
    /// <summary>Does Dwight's connection to Anthropic carry tools to the model? TRUE - this builder writes
    /// <c>request.Tools</c> into the body's <c>tools</c> array. Declared HERE, beside the code that makes it
    /// true, and held to it by a test that builds a body and looks for the tool (lane B, 3.1 step 2): the
    /// declaration cannot drift from the behaviour in either direction.</summary>
    internal const bool PassesTools = true;

    /// <summary>Set when the last built body changed something the caller asked for - today, an effort
    /// stepped down to fit the ceiling (#216). A silent degrade is how "we asked for max" becomes
    /// folklore, so the adjustment is surfaced rather than absorbed. Best-effort and last-write-wins:
    /// it is a diagnostic, never a control signal.</summary>
    internal static string LastPlanNote;

    /// <summary>The server tool's wire type (Brief V). The 20250305 version, deliberately: it is the basic search, and its
    /// <c>allowed_callers</c> defaults to <c>["direct"]</c>, so nothing here asks the provider to run code.</summary>
    internal const string WebSearchToolType = "web_search_20250305";

    /// <summary>Searches the provider may run in one request. Past it the provider returns a
    /// <c>max_uses_exceeded</c> error inside the result, which Dwight reports in its own voice.</summary>
    internal const int WebSearchMaxUses = 5;

    public static string Build(LlmRequest request, string modelId, ThinkingWireState thinking, AnthropicWire wire = null)
    {
      wire = wire ?? AnthropicWire.Default;
      var structuredAsFormat = wire.StructuredFormat && !string.IsNullOrEmpty(request.StructuredSchemaJson) && !string.IsNullOrEmpty(request.StructuredName);
      // Cleared per build so the note always describes THE LAST BODY BUILT. It used to be written only
      // in the Adaptive branch, so a later non-adaptive request left the previous call's note standing -
      // harmless while nobody read it, and a stale reading the moment somebody did. WP-3's turn meta
      // line is that somebody (#223).
      LastPlanNote = null;

      var root = new JsonObject
      {
        ["model"] = modelId,
        ["max_tokens"] = request.MaxTokens,
        ["stream"] = true,
      };

      // Sampling parameters are REMOVED on Opus 4.7+ and the Claude 5 family - sending one is a 400.
      // The product never sets Temperature (it is an eval-only lever, pinned to a model that still
      // accepts it), so this stays absent on every shipping path; a guard test holds that true.
      if (request.Temperature.HasValue) root["temperature"] = request.Temperature.Value;

      // Brief BZ: the instructions were written for a tool, and no tool goes with this body. Said once here so a verb's prompt need not know which wire it is on.
      var system = structuredAsFormat ? (request.System ?? "") + FormatNote(request) : request.System;
      if (!string.IsNullOrEmpty(system))
      {
        if (request.CacheSystem)
        {
          // One cache_control breakpoint on the system block caches the tools + system prefix
          // together (render order is tools -> system -> messages). Prompt caching, ADR-037.
          root["system"] = new JsonArray
          {
            new JsonObject
            {
              ["type"] = "text",
              ["text"] = system,
              ["cache_control"] = new JsonObject { ["type"] = "ephemeral" },
            },
          };
        }
        else
        {
          root["system"] = system;
        }
      }

      var messages = new JsonArray();
      foreach (var m in request.Messages)
      {
        var blocks = new JsonArray();
        foreach (var b in m.Blocks)
        {
          switch (b.Kind)
          {
            case "text":
            {
              var text = new JsonObject { ["type"] = "text", ["text"] = b.Text ?? "" };
              // Brief V: a text block that carried citations returns with them, unchanged (their encrypted_index is
              // a reference the API reads on the next turn). Absent unless web search was on.
              if (!string.IsNullOrEmpty(b.CitationsJson)) text["citations"] = ReplayedRaw(b, b.CitationsJson);
              blocks.Add(text);
              break;
            }
            // Brief DX: an image the person attached. It may take the transcript's cache breakpoint, as a text block may.
            case LlmBlock.ImageKind:
              blocks.Add(new JsonObject
              {
                ["type"] = "image",
                ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = b.MediaType, ["data"] = b.Base64 },
              });
              break;
            // Brief V: the provider's own search, returned EXACTLY as received - the search the model made and the
            // result it got back, encrypted_content and all. Rebuilding either is a 400 on the next request.
            case "server_tool_use":
            case "web_search_tool_result":
              blocks.Add(ReplayedRaw(b, b.RawJson));
              break;
            case "tool_use":
              blocks.Add(new JsonObject
              {
                ["type"] = "tool_use",
                ["id"] = b.ToolUseId,
                ["name"] = b.ToolName,
                ["input"] = ReplayedInput(b.ToolName, b.ToolInputJson),
              });
              break;
            case "tool_result":
              blocks.Add(new JsonObject
              {
                ["type"] = "tool_result",
                ["tool_use_id"] = b.ToolUseId,
                ["content"] = b.Text ?? "",
              });
              break;
            // #192b: a thinking block is echoed back VERBATIM. An empty summary with a populated
            // signature is the norm (display defaults to omitted), and it must still round-trip -
            // rebuilding or dropping the block is a 400 on the next turn.
            case "thinking":
              blocks.Add(new JsonObject
              {
                ["type"] = "thinking",
                ["thinking"] = b.Text ?? "",
                ["signature"] = b.Signature ?? "",
              });
              break;
            case "redacted_thinking":
              blocks.Add(new JsonObject { ["type"] = "redacted_thinking", ["data"] = b.Data ?? "" });
              break;
          }
        }
        messages.Add(new JsonObject
        {
          ["role"] = m.Role == LlmRole.User ? "user" : "assistant",
          ["content"] = blocks,
        });
      }
      // U57: the second breakpoint rides the LAST cacheable block of the LAST message, so each step reads
      // the whole prior transcript from cache and writes only its new tail. Placed here, on the wire body
      // only - the stored transcript never carries it. Same gate as the system breakpoint. A thinking
      // block is echoed verbatim and cannot carry cache_control, so it walks back to the last other block.
      if (request.CacheSystem) MarkTranscriptTail(messages);
      root["messages"] = messages;

      var tools = new JsonArray();
      if (request.Tools != null)
        foreach (var t in request.Tools)
          tools.Add(new JsonObject { ["name"] = t.Name, ["description"] = t.Description, ["input_schema"] = Schema(t.InputSchemaJson) });

      // Structured output (ADR-058): expose the desired shape as a single tool and FORCE it, so the
      // reply arrives as a shape-constrained tool_use block. Forced tool choice is compatible with
      // ADAPTIVE thinking (it is only the retired fixed-budget mode that rejects it), which is what
      // lets ADR-058's structured path and ADR-082's thinking coexist.
      // Brief BZ: where forcing is a 400, the shape travels as output_config.format instead, and no tool is offered.
      var structured = !structuredAsFormat && !string.IsNullOrEmpty(request.StructuredSchemaJson) && !string.IsNullOrEmpty(request.StructuredName);
      if (structured)
        tools.Add(new JsonObject
        {
          ["name"] = request.StructuredName,
          ["description"] = request.StructuredDescription ?? "",
          ["input_schema"] = Schema(request.StructuredSchemaJson),
        });

      // Brief V: Anthropic's own search, beside Dwight's tools. web_search_20250305 is the basic version: allowed_callers
      // defaults to direct, so no code execution is provisioned. Appended last so that with the setting off the array
      // is exactly what it was.
      if (request.ServerWebSearch)
        tools.Add(new JsonObject
        {
          ["type"] = WebSearchToolType,
          ["name"] = "web_search",
          ["max_uses"] = WebSearchMaxUses,
        });

      if (tools.Count > 0) root["tools"] = tools;
      if (structured)
        root["tool_choice"] = new JsonObject { ["type"] = "tool", ["name"] = request.StructuredName };
      else if (request.ForbidTools && tools.Count > 0)
        root["tool_choice"] = new JsonObject { ["type"] = "none" };
      else if (!string.IsNullOrEmpty(request.ForceToolName) && wire.ForcedTool)
        root["tool_choice"] = new JsonObject { ["type"] = "tool", ["name"] = request.ForceToolName };

      // ---- thinking, appended last so Omit leaves everything above byte-identical (#192a) ----------
      switch (thinking)
      {
        case ThinkingWireState.Adaptive:
        {
          root["thinking"] = new JsonObject { ["type"] = "adaptive" };
          // Only here: block_binding works with adaptive alone, and beside between_tools it is a 400.
          if (wire.StrictPrefixCheck)
            root["thinking"]["block_binding"] = new JsonObject { ["prefix_mismatch_behavior"] = "error" };

          // #216: max_tokens is the ANSWER budget; the reserve goes ON TOP, here at the seam rather
          // than at every call site. Measured: effort "max" against a bare 4096 budget spent all of it
          // thinking and returned nothing. The plan may step the EFFORT down to fit the ceiling - never
          // the answer, which is the deliverable - and says so when it does.
          // Brief BZ: where the API's default differs by model, the level is said rather than left to it (Opus 5.5 defaults to medium, Sonnet 5.5 to high).
          var effort = string.IsNullOrWhiteSpace(request.Effort) && !string.IsNullOrEmpty(wire.ThinkingOffEffort) ? "medium" : request.Effort;
          var plan = ThinkingBudget.Resolve(request.MaxTokens, effort);
          root["max_tokens"] = plan.MaxTokens;
          LastPlanNote = plan.Note;

          // Effort is a sibling of thinking, NOT a field inside it, and only meaningful with adaptive.
          if (!string.IsNullOrWhiteSpace(plan.Effort))
            root["output_config"] = new JsonObject { ["effort"] = plan.Effort };
          break;
        }
        case ThinkingWireState.AlwaysOn:
        {
          // Brief BZ: comprehension on a model that cannot stop thinking. No field, the lowest effort the row names, and room for the thinking, which counts inside max_tokens
          // even when its text is not returned. A budget sized for no thinking would cut the answer off.
          var plan = ThinkingBudget.Resolve(request.MaxTokens, request.Effort ?? wire.ThinkingOffEffort ?? "low");
          root["max_tokens"] = plan.MaxTokens;
          LastPlanNote = plan.Note;
          root["output_config"] = new JsonObject { ["effort"] = plan.Effort };
          break;
        }
        case ThinkingWireState.BetweenTools:
        {
          // Brief BZ: the lowest thinking setting on this model. No other field may ride in it, and effort above high is a 400 with it.
          root["thinking"] = new JsonObject { ["type"] = "between_tools" };
          var effort = request.Effort ?? wire.ThinkingOffEffort ?? "low";
          root["output_config"] = new JsonObject { ["effort"] = effort == "max" || effort == "xhigh" ? "high" : effort };
          break;
        }
        case ThinkingWireState.Disabled:
          // Said explicitly, because on this family silence would leave thinking RUNNING - and
          // comprehension must not think (ADR-082). No effort is sent: raising it above `high` while
          // disabled is rejected, and nothing here ever sets one.
          root["thinking"] = new JsonObject { ["type"] = "disabled" };
          break;
        case ThinkingWireState.Omit:
        default:
          break;   // exactly the pre-WP-2 body
      }

      // Brief BZ: the structured shape, beside any effort: output_config is one object.
      if (structuredAsFormat)
      {
        var config = root["output_config"] as JsonObject;
        if (config == null) root["output_config"] = config = new JsonObject();
        config["format"] = new JsonObject { ["type"] = "json_schema", ["schema"] = StrictSchema(request.StructuredSchemaJson) };
      }

      return root.ToJsonString();
    }

    /// <summary>What a tool's name and description carried, for a model given no tool. The description holds instructions (no ids for added nodes, say), so it must not be lost.</summary>
    private static string FormatNote(LlmRequest request) =>
      "\n\nThis request offers no tools. Where these instructions say to call " + request.StructuredName +
      ", return that object as your whole reply instead. " + (request.StructuredDescription ?? "");

    /// <summary>
    /// A schema we authored, in the form <c>output_config.format</c> takes: <c>additionalProperties</c> must be false on every object, and our flat schemas leave it out. Optional
    /// fields stay optional (the page's own example leaves them out of <c>required</c>), so the validators keep reading what they read. The keywords the format refuses and its
    /// limits (24 optional parameters, 16 unions) are held by a test over every published schema, because a schema is ours and a 400 here would cost every verb.
    /// </summary>
    internal static JsonNode StrictSchema(string json)
    {
      var schema = JsonNode.Parse(json);
      Close(schema);
      return schema;
    }

    private static void Close(JsonNode node)
    {
      if (!(node is JsonObject o)) return;
      if (o["properties"] is JsonObject props)
      {
        o["additionalProperties"] = false;
        foreach (var p in props) Close(p.Value);
      }
      Close(o["items"]);
      foreach (var key in new[] { "anyOf", "allOf" })
        if (o[key] is JsonArray list) foreach (var part in list) Close(part);
    }

    /// <summary>
    /// The reply's structured result, wherever this wire put it. On the format path the JSON is the reply's text, and a reply cut off or refused holds no such thing: the page says
    /// such output "might not match your schema", so it is left out for the seam's own truncation and refusal outcomes to speak.
    /// </summary>
    public static void SurfaceStructured(LlmResponse parsed, LlmRequest request, AnthropicWire wire)
    {
      if (string.IsNullOrEmpty(request.StructuredName)) return;
      if ((wire ?? AnthropicWire.Default).StructuredFormat)
      {
        if (parsed.StopReason != "max_tokens" && parsed.StopReason != LlmRefusal.StopReason && !string.IsNullOrWhiteSpace(parsed.Text))
          parsed.StructuredJson = parsed.Text;
        return;
      }
      var tu = parsed.Blocks.Find(b => b.Kind == "tool_use" && b.ToolName == request.StructuredName);
      if (tu != null && !string.IsNullOrEmpty(tu.ToolInputJson)) parsed.StructuredJson = tu.ToolInputJson;
    }

    /// <summary>Turn a non-200 into a friendly message. Never echoes the API key. <paramref name="retentionClause"/> is the row's own sentence for a 400: the API's words for a retention
    /// refusal are not pinned anywhere we can read, so the sentence is true of every 400 on such a row rather than matched from a message.</summary>
    public static string DescribeError(int status, string body, string retentionClause = null, TimeZoneInfo zone = null)
    {
      string apiMessage = null;
      try { apiMessage = JsonNode.Parse(body)?["error"]?["message"]?.GetValue<string>(); }
      catch { /* leave null */ }

      var limit = ProviderLimit.OfAnthropic(status, body);   // Brief CO: before the status, because a limit arrives as a 400 or as a 429
      if (limit != ProviderLimitKind.None) return ProviderLimit.Sentence(limit, "Anthropic", apiMessage, zone);

      switch (status)
      {
        case 401:
          return "Claude rejected the API key (401). Re-enter it in Dwight > Settings.";
        case 403:
          return "The API key lacks permission for this request (403).";
        case 429:
          return "Claude is rate-limiting your key (429). Wait a moment and try again. " + ProviderLimit.NothingChanged;
        case 529:
          return "Claude is temporarily overloaded (529). Try again shortly.";
        default:
          var detail = apiMessage ?? (body != null && body.Length > 200 ? body.Substring(0, 200) : body);
          var said = $"Claude returned an error ({status}): {detail}";
          return status == 400 && !string.IsNullOrEmpty(retentionClause) ? said + " " + retentionClause : said;
      }
    }

    /// <summary>The sentence for a row that needs data retention (Fable 5.1, 30 days): an organisation on zero data retention gets a plain 400 with no obvious request problem.</summary>
    public static string RetentionClause(string displayName, int days) =>
      days <= 0 ? null : displayName + " needs " + days + "-day data retention, so an organisation on zero data retention gets this error. If yours is one, pick another model.";

    private static void MarkTranscriptTail(JsonArray messages)
    {
      if (messages.Count == 0) return;
      if (!(messages[messages.Count - 1]["content"] is JsonArray blocks)) return;
      for (var i = blocks.Count - 1; i >= 0; i--)
      {
        var type = (string)blocks[i]["type"];
        if (type == "thinking" || type == "redacted_thinking") continue;
        // The provider's search blocks are returned verbatim, so they take no breakpoint of ours either.
        if (type == "server_tool_use" || type == "web_search_tool_result") continue;
        blocks[i]["cache_control"] = new JsonObject { ["type"] = "ephemeral" };
        return;
      }
    }

    /// <summary>
    /// A tool_use block's input, replayed into the transcript - <b>the model's own earlier words</b>, read
    /// back to it as the record of what it did. REPLAYED TRUTHFULLY OR NOT AT ALL (#827, #873 step 2).
    ///
    /// <para>It used to fall back to <c>{}</c> on anything it could not read, so the model read a call it
    /// never made - <c>build_region</c> with no arguments - followed by a result describing work on arguments
    /// it could no longer see it supplied. <i>A guard that repairs is a guard that hides</i> (#297): the
    /// rewrite turned a diagnosable defect into a transcript that lies about the past, to the one reader that
    /// cannot check it against anything.</para>
    ///
    /// <para>REFUSED HERE, AT THE DESTINATION (#873(c), #335), and not in the provider-neutral pre-flight:
    /// Anthropic needs <c>input</c> to be an OBJECT, so this destination cannot replay a malformed one
    /// faithfully. OpenAI carries arguments as a STRING and can, so a refusal everywhere would kill turns that
    /// could have been replayed truthfully.</para>
    ///
    /// <para>The sentence claims only what is true: earlier steps of the same turn may already have run their
    /// tools, so it says THIS step was not sent - never that nothing ran.</para>
    /// </summary>
    private static JsonNode ReplayedInput(string toolName, string json)
    {
      var call = string.IsNullOrWhiteSpace(toolName) ? "an earlier tool call" : "the earlier call to \"" + toolName + "\"";

      if (string.IsNullOrWhiteSpace(json))
        throw Unreplayable(call, "has no arguments recorded");

      JsonNode node;
      try { node = JsonNode.Parse(json); }
      catch (System.Text.Json.JsonException) { throw Unreplayable(call, "could not be read"); }

      // Parsing is not the bar (#828): an array or a scalar parses and still cannot be a call's arguments.
      if (!(node is JsonObject)) throw Unreplayable(call, "is not a set of arguments");
      return node;
    }

    /// <summary>A block the provider sent and wants back UNCHANGED (Brief V). Parsed only to be placed in the body; a
    /// record that cannot be read is refused, never replayed as something else (#297).</summary>
    private static JsonNode ReplayedRaw(LlmBlock b, string json)
    {
      var what = "the earlier " + (b.Kind ?? "block");
      if (string.IsNullOrWhiteSpace(json)) throw Unreplayable(what, "was not recorded");
      try { return JsonNode.Parse(json); }
      catch (System.Text.Json.JsonException) { throw Unreplayable(what, "could not be read"); }
    }

    private static LlmException Unreplayable(string call, string what) =>
      new LlmException("Dwight stopped this turn before its next step: the record of " + call + " " + what +
                       ", so replaying it would misstate what the model asked for. That step was not sent.");

    /// <summary>A schema we authored, on its way to the provider. <b>There is no empty fallback here on
    /// purpose</b> (<see cref="LlmSchemaCheck"/>): publishing <c>{}</c> advertises a tool that takes no
    /// parameters and then refuses every call to it, which is a failure shaped exactly like success.
    /// <see cref="LlmSchemaCheck.Check"/> refuses such a request at the provider seam before this runs,
    /// so reaching this throw means the check was bypassed - and a loud throw is the right answer to
    /// that, where an empty object would hand the defect straight back to the next caller.</summary>
    private static JsonNode Schema(string json) => JsonNode.Parse(json);
  }
}
