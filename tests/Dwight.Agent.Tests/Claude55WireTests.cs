using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using Dwight.Agent;
using Dwight.Llm;
using Dwight.Models;
using Dwight.Prompts;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// Brief BZ: Sonnet 5.5, Opus 5.5 and Fable 5.1. Every row is read from the shipped catalogue, every body from the real serialiser, and every reply through the real stream reader, so
  /// the facts and the wire cannot drift apart. What the models do with it is the gate's to read.
  /// </summary>
  public class Claude55WireTests
  {
    private static readonly JsonNode Catalogue = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "models.json")));

    private static JsonNode RowJson(string id) => Catalogue["models"].AsArray().Single(m => m["id"].GetValue<string>() == id);

    private static ModelInfo Row(string id, bool? pinOnly = null, bool? agentic = null)
    {
      var m = RowJson(id);
      var c = m["capabilities"];
      return new ModelInfo(id, m["displayName"].GetValue<string>(), m["provider"].GetValue<string>(), m["tier"].GetValue<string>(),
        m["priceIn"].GetValue<double>(), m["priceOut"].GetValue<double>(), m["contextWindow"].GetValue<int>(),
        structured: c["structuredOutput"]?.GetValue<bool>() ?? false,
        reasoning: c["reasoning"]?.GetValue<bool>() ?? false, reasoningKnob: c["reasoningKnob"]?.GetValue<bool>() ?? false,
        thinkingDefault: c["thinkingDefault"]?.GetValue<string>(), agentic: agentic ?? c["agentic"]?.GetValue<bool>() ?? false,
        preference: m["preference"]?.GetValue<int>(), cacheReadPerMTok: m["priceCachedIn"]?.GetValue<double>(), cacheWritePerMTok: m["priceCacheWrite"]?.GetValue<double>(),
        forcedToolChoice: c["forcedToolChoice"]?.GetValue<bool>() ?? true, structuredFormat: c["structuredFormat"]?.GetValue<bool>() ?? false,
        thinkingBound: c["thinkingBound"]?.GetValue<bool>() ?? false, pinOnly: pinOnly ?? c["pinOnly"]?.GetValue<bool>() ?? false,
        thinkingOffEffort: c["thinkingOffEffort"]?.GetValue<string>(), retentionDays: c["retentionDays"]?.GetValue<int>() ?? 0);
    }

    private static JsonObject BodyFor(string id, LlmRequest r)
    {
      var m = Row(id);
      return (JsonObject)JsonNode.Parse(AnthropicRequestBody.Build(r, id, ThinkingWire.Resolve(r.Thinking, m.ThinkingDefault), AnthropicWire.Of(m)));
    }

    private static LlmRequest Verb() => new LlmRequest
    {
      System = "s", CacheSystem = true, MaxTokens = 16000,
      Messages = { LlmMessage.User("payload") },
      StructuredName = Prompts.Prompts.DiagnoseToolName, StructuredDescription = Prompts.Prompts.DiagnoseToolDescription, StructuredSchemaJson = Prompts.Prompts.DiagnoseSchema,
    };

    // ---- what each model's body is, per the vendor's pages ------------------------------------------------

    [Fact]
    public void Sonnet_55_comprehension_says_between_tools_with_no_other_field_and_a_low_effort()
    {
      var body = BodyFor("claude-sonnet-5-5", Verb());

      var thinking = body["thinking"].AsObject();
      Assert.Equal("between_tools", thinking["type"].GetValue<string>());
      Assert.Single(thinking);    // display, budget_tokens or block_binding beside it is a 400
      Assert.Equal("low", body["output_config"]["effort"].GetValue<string>());
    }

    [Theory]
    [InlineData("claude-opus-5-5")]
    [InlineData("claude-fable-5-1")]
    public void Opus_55_and_Fable_51_comprehension_send_no_thinking_field_a_low_effort_and_room_to_think(string id)
    {
      var body = BodyFor(id, Verb());

      Assert.False(body.ContainsKey("thinking"));   // disabled is a 400 at every effort
      Assert.Equal("low", body["output_config"]["effort"].GetValue<string>());
      Assert.Equal(16000 + ThinkingBudget.ReserveLow, body["max_tokens"].GetValue<int>());   // thinking counts inside max_tokens
    }

    [Theory]
    [InlineData("claude-sonnet-5-5")]
    [InlineData("claude-opus-5-5")]
    [InlineData("claude-fable-5-1")]
    public void A_verb_on_these_rows_is_structured_output_with_no_tool_and_no_forcing(string id)
    {
      var body = BodyFor(id, Verb());

      Assert.False(body.ContainsKey("tool_choice"));
      Assert.False(body.ContainsKey("tools"));
      var format = body["output_config"]["format"];
      Assert.Equal("json_schema", format["type"].GetValue<string>());
      Assert.False(format["schema"]["additionalProperties"].GetValue<bool>());
      Assert.False(format["schema"]["properties"]["faults"]["items"]["additionalProperties"].GetValue<bool>());
      // The tool's name and description carried instructions, and they must reach the model.
      var system = body["system"][0]["text"].GetValue<string>();
      Assert.Contains("say to call diagnose_faults, return that object as your whole reply", system);
      Assert.Contains(Prompts.Prompts.DiagnoseToolDescription, system);
    }

    [Theory]
    [InlineData("claude-sonnet-5-5")]
    [InlineData("claude-opus-5-5")]
    [InlineData("claude-fable-5-1")]
    public void Forcing_never_reaches_these_rows_even_if_a_caller_asks(string id)
    {
      var r = new LlmRequest { System = "s", Messages = { LlmMessage.User("x") }, ForceToolName = "request_mutation",
        Tools = { new LlmTool { Name = "request_mutation", Description = "d", InputSchemaJson = "{\"type\":\"object\",\"properties\":{}}" } } };

      Assert.False(BodyFor(id, r).ContainsKey("tool_choice"));
      Assert.False(Row(id).ForcedToolChoice);
    }

    [Theory]
    [InlineData("claude-sonnet-5-5", "medium")]
    [InlineData("claude-opus-5-5", "high")]
    [InlineData("claude-opus-5-5", "max")]
    [InlineData("claude-fable-5-1", "medium")]
    public void A_chat_that_thinks_sends_adaptive_and_the_profiles_effort_explicitly(string id, string effort)
    {
      var r = new LlmRequest { System = "s", Messages = { LlmMessage.User("x") }, Thinking = true, Effort = effort };

      var body = BodyFor(id, r);

      Assert.Equal("adaptive", body["thinking"]["type"].GetValue<string>());
      Assert.Equal(effort, body["output_config"]["effort"].GetValue<string>());
    }

    [Theory]
    [InlineData("claude-sonnet-5-5")]
    [InlineData("claude-opus-5-5")]
    public void A_chat_that_thinks_with_no_effort_named_still_says_one_because_the_defaults_differ(string id)
    {
      var body = BodyFor(id, new LlmRequest { System = "s", Messages = { LlmMessage.User("x") }, Thinking = true });

      Assert.Equal("medium", body["output_config"]["effort"].GetValue<string>());
    }

    // ---- nothing else moved --------------------------------------------------------------------------------

    /// <summary>The bodies these rows sent before this brief, held whole. Haiku 5.5 is in tonight's run, so its facts are read from its row: it accepts disabled at high or below and it accepts forcing.</summary>
    [Theory]
    [InlineData("claude-haiku-5-5")]
    [InlineData("claude-sonnet-5")]
    [InlineData("claude-opus-5")]
    public void An_adaptive_on_row_still_sends_disabled_and_the_forced_tool(string id)
    {
      var m = Row(id);
      var r = Verb();
      var wired = AnthropicRequestBody.Build(r, id, ThinkingWire.Resolve(r.Thinking, m.ThinkingDefault), AnthropicWire.Of(m));

      Assert.Equal(AnthropicRequestBody.Build(r, id, ThinkingWireState.Disabled), wired);   // byte for byte the shape with no facts at all
      var body = JsonNode.Parse(wired);
      Assert.Equal("disabled", body["thinking"]["type"].GetValue<string>());
      Assert.Equal("tool", body["tool_choice"]["type"].GetValue<string>());
      Assert.False(body.AsObject().ContainsKey("output_config"));
      Assert.True(m.ForcedToolChoice);
      Assert.False(m.StructuredFormat);
      Assert.False(m.CannotDisableThinking);
    }

    [Theory]
    [InlineData("claude-sonnet-4-6")]
    [InlineData("claude-opus-4-8")]
    public void An_adaptive_off_row_still_sends_nothing(string id)
    {
      var m = Row(id);
      var body = JsonNode.Parse(AnthropicRequestBody.Build(Verb(), id, ThinkingWire.Resolve(false, m.ThinkingDefault), AnthropicWire.Of(m))).AsObject();

      Assert.False(body.ContainsKey("thinking"));
      Assert.False(body.ContainsKey("output_config"));
    }

    [Fact]
    public void The_prefix_check_is_marked_on_every_row_it_runs_on_and_no_other()
    {
      // "The prefix check runs on Claude 5.1 and later models. Other models don't run it." (Preserved thinking)
      foreach (var id in new[] { "claude-haiku-5-5", "claude-sonnet-5-5", "claude-opus-5-5", "claude-fable-5-1" }) Assert.True(Row(id).ThinkingBound, id);
      foreach (var id in new[] { "claude-sonnet-5", "claude-opus-5", "claude-sonnet-4-6", "claude-opus-4-8", "claude-haiku-4-5" }) Assert.False(Row(id).ThinkingBound, id);
    }

    // ---- every published schema, against what the format accepts ---------------------------------------------

    public static IEnumerable<object[]> Schemas() => new[]
    {
      new object[] { Prompts.Prompts.ExplainStepsSchema }, new object[] { Prompts.Prompts.DiagnoseSchema }, new object[] { Prompts.Prompts.SimplifySchema },
      new object[] { Prompts.Prompts.ToScriptSchema }, new object[] { Prompts.Prompts.LabelSchema },
    };

    [Theory]
    [MemberData(nameof(Schemas))]
    public void Every_verbs_schema_is_one_the_format_accepts(string schemaJson)
    {
      var schema = AnthropicRequestBody.StrictSchema(schemaJson);
      int optional = 0, unions = 0;
      Walk(schema, ref optional, ref unions);

      Assert.True(optional <= 24, "optional parameters: " + optional);   // the page's limit, across a request
      Assert.True(unions <= 16, "unions: " + unions);
    }

    private static void Walk(JsonNode node, ref int optional, ref int unions)
    {
      if (!(node is JsonObject o)) return;
      foreach (var refused in new[] { "minimum", "maximum", "multipleOf", "exclusiveMinimum", "exclusiveMaximum", "minLength", "maxLength", "maxItems", "uniqueItems", "$ref" })
        Assert.False(o.ContainsKey(refused), refused);
      if (o["minItems"] != null) Assert.True(o["minItems"].GetValue<int>() <= 1);
      if (o["type"] is JsonArray || o.ContainsKey("anyOf")) unions++;
      if (o["properties"] is JsonObject props)
      {
        Assert.False(o["additionalProperties"].GetValue<bool>());
        var required = (o["required"] as JsonArray)?.Select(r => r.GetValue<string>()).ToList() ?? new List<string>();
        foreach (var p in props)
        {
          if (!required.Contains(p.Key)) optional++;
          Walk(p.Value, ref optional, ref unions);
        }
      }
      Walk(o["items"], ref optional, ref unions);
    }

    [Fact]
    public void A_property_named_like_a_keyword_is_not_treated_as_one()
    {
      var schema = AnthropicRequestBody.StrictSchema("{\"type\":\"object\",\"properties\":{\"properties\":{\"type\":\"string\"},\"items\":{\"type\":\"object\",\"properties\":{\"x\":{\"type\":\"integer\"}}}}}");

      Assert.False(schema["properties"]["properties"].AsObject().ContainsKey("additionalProperties"));
      Assert.False(schema["properties"]["items"]["additionalProperties"].GetValue<bool>());
    }

    // ---- the reply, through the real reader -----------------------------------------------------------------

    private static string Sse(params string[] data)
    {
      var s = new System.Text.StringBuilder();
      foreach (var d in data)
      {
        var type = JsonNode.Parse(d)["type"].GetValue<string>();
        s.Append("event: ").Append(type).Append("\ndata: ").Append(d).Append("\n\n");
      }
      return s.ToString();
    }

    private const string Start = "{\"type\":\"message_start\",\"message\":{\"usage\":{\"input_tokens\":412,\"output_tokens\":0}}}";
    private const string Stop = "{\"type\":\"message_stop\"}";

    private static string Delta(string stop, string details = null, int output = 0) =>
      "{\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"" + stop + "\"" + (details == null ? "" : ",\"stop_details\":" + details) + "},\"usage\":{\"output_tokens\":" + output + "}}";

    private static string TextBlocks(int index, string text) =>
      "{\"type\":\"content_block_start\",\"index\":" + index + ",\"content_block\":{\"type\":\"text\",\"text\":\"\"}}|" +
      "{\"type\":\"content_block_delta\",\"index\":" + index + ",\"delta\":{\"type\":\"text_delta\",\"text\":" + JsonValue.Create(text).ToJsonString() + "}}|" +
      "{\"type\":\"content_block_stop\",\"index\":" + index + "}";

    private static string Thinking(int index) =>
      "{\"type\":\"content_block_start\",\"index\":" + index + ",\"content_block\":{\"type\":\"thinking\",\"thinking\":\"\",\"signature\":\"\"}}|" +
      "{\"type\":\"content_block_delta\",\"index\":" + index + ",\"delta\":{\"type\":\"signature_delta\",\"signature\":\"sig" + index + "\"}}|" +
      "{\"type\":\"content_block_stop\",\"index\":" + index + "}";

    private static string Call(int index, string id, string name, string args) =>
      "{\"type\":\"content_block_start\",\"index\":" + index + ",\"content_block\":{\"type\":\"tool_use\",\"id\":\"" + id + "\",\"name\":\"" + name + "\",\"input\":{}}}|" +
      "{\"type\":\"content_block_delta\",\"index\":" + index + ",\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":" + JsonValue.Create(args).ToJsonString() + "}}|" +
      "{\"type\":\"content_block_stop\",\"index\":" + index + "}";

    private static LlmResponse Read(params string[] parts) =>
      AnthropicReplyStream.Read(new StringReader(Sse(parts.SelectMany(p => p.Split('|')).ToArray())));

    [Fact]
    public void A_verbs_reply_on_the_format_path_is_the_replys_text()
    {
      var parsed = Read(Start, Thinking(0), TextBlocks(1, "{\"faults\":[]}"), Delta("end_turn", "null", 9), Stop);
      AnthropicRequestBody.SurfaceStructured(parsed, Verb(), AnthropicWire.Of(Row("claude-opus-5-5")));

      Assert.Equal("{\"faults\":[]}", parsed.StructuredJson);
    }

    [Fact]
    public void A_format_reply_cut_off_or_refused_is_no_result()
    {
      foreach (var stop in new[] { "max_tokens", "refusal" })
      {
        var parsed = Read(Start, TextBlocks(0, "{\"faults\":["), Delta(stop, null, 9), Stop);
        AnthropicRequestBody.SurfaceStructured(parsed, Verb(), AnthropicWire.Of(Row("claude-sonnet-5-5")));
        Assert.Null(parsed.StructuredJson);
      }
    }

    [Fact]
    public void A_refusal_before_any_output_is_read_with_its_category()
    {
      var parsed = Read(Start, Delta("refusal", "{\"type\":\"refusal\",\"category\":\"cyber\",\"explanation\":\"text that is not stable\"}"), Stop);

      Assert.True(LlmRefusal.Is(parsed));
      Assert.Equal("cyber", parsed.RefusalCategory);
      Assert.Empty(parsed.Blocks);
    }

    [Fact]
    public void The_category_is_read_wherever_the_stream_puts_it()
    {
      var parsed = Read(Start, "{\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"refusal\"},\"stop_details\":{\"type\":\"refusal\",\"category\":\"bio\"},\"usage\":{\"output_tokens\":0}}", Stop);

      Assert.Equal("bio", parsed.RefusalCategory);
    }

    [Fact]
    public void A_refused_stream_that_never_closed_its_block_is_still_a_refusal()
    {
      var parsed = Read(Start, "{\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"tool_use\",\"id\":\"t1\",\"name\":\"build_region\",\"input\":{}}}",
        Delta("refusal", "{\"type\":\"refusal\",\"category\":\"cyber\"}", 3), Stop);

      Assert.True(LlmRefusal.Is(parsed));
      Assert.Empty(parsed.ToolUses);
    }

    [Fact]
    public void A_refusal_with_a_null_category_is_still_a_refusal()
    {
      var parsed = Read(Start, Delta("refusal", "{\"type\":\"refusal\",\"category\":null,\"explanation\":null}"), Stop);

      Assert.True(LlmRefusal.Is(parsed));
      Assert.Null(parsed.RefusalCategory);
      Assert.Equal(LlmRefusal.Sentence(null), LlmRefusal.Sentence(parsed.RefusalCategory));
    }

    [Fact]
    public void A_refusal_mid_call_is_a_refusal_and_not_a_damaged_reply()
    {
      var parsed = Read(Start, Call(0, "t1", "build_region", "{\"region\":\"A1\",\"comp"), Delta("refusal", "{\"type\":\"refusal\",\"category\":\"general_harms\"}", 7), Stop);

      Assert.True(LlmRefusal.Is(parsed));
      Assert.Empty(parsed.ToolUses);
    }

    [Fact]
    public void The_same_broken_call_with_no_refusal_is_still_refused_as_damage()
    {
      var ex = Assert.Throws<LlmException>(() => Read(Start, Call(0, "t1", "build_region", "{\"region\":\"A1\",\"comp"), Delta("tool_use", null, 7), Stop));
      Assert.Contains("do not join into valid JSON", ex.Message);
    }

    [Theory]
    [InlineData("cyber", "possible cybersecurity harm")]
    [InlineData("bio", "possible biological harm")]
    [InlineData("frontier_llm", "help building a competing AI model")]
    [InlineData("reasoning_extraction", "a request to write out its private reasoning")]
    [InlineData("general_harms", "another area of its usage policy")]
    public void The_refusal_says_what_the_check_read_and_what_to_do(string category, string read)
    {
      Assert.Equal("Claude declined this request: its safety check read it as " + read +
        ". Ordinary work can trigger this, and anything already done stands. Dwight did not retry on another model. Reword the request, or pick another model.", LlmRefusal.Sentence(category));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("a_word_newer_than_this_build")]
    public void A_refusal_with_no_category_we_can_read_says_only_that_it_was_declined(string category)
    {
      Assert.StartsWith("Claude declined this request. Ordinary work", LlmRefusal.Sentence(category));
    }

    [Theory]
    [InlineData("cyber", 0, false)]          // before any output, in a category the page does not bill
    [InlineData("general_harms", 0, false)]
    [InlineData(null, 0, false)]
    [InlineData("bio", 0, true)]             // before any output, in a billed one
    [InlineData("frontier_llm", 0, true)]
    [InlineData("reasoning_extraction", 0, true)]
    [InlineData("cyber", 40, true)]          // mid-stream is always billed
    public void What_a_refusal_costs_follows_the_page(string category, int output, bool billed)
    {
      var r = new LlmResponse { StopReason = "refusal", RefusalCategory = category, Usage = new LlmUsage { InputTokens = 412, OutputTokens = output } };
      Assert.Equal(billed, LlmRefusal.IsBilled(r));
    }

    // ---- the real paths: a verb and a chat turn --------------------------------------------------------------

    /// <summary>Stands where HTTP stands: the real body from the row's facts, then the real reader on what a script says came back. Everything the adapter does with a row, it does here.</summary>
    private sealed class WireProvider : ILlmProvider
    {
      private readonly ModelInfo _row;
      private readonly Queue<string> _replies;
      public readonly List<string> Bodies = new List<string>();

      public WireProvider(string id, params string[] replies) { _row = Row(id); ModelId = id; _replies = new Queue<string>(replies); }

      public string ModelId { get; }
      public ProviderCapability Capability => ProviderCapability.ToolCall;
      public bool SendsForcedTool => _row.ForcedToolChoice;
      public bool StructuredArrivesAsText => _row.StructuredFormat;
      public bool KeepsHistoryAppendOnly => _row.ThinkingBound;

      public LlmResponse Send(LlmRequest request, CancellationToken cancel = default)
      {
        Bodies.Add(AnthropicRequestBody.Build(request, ModelId, ThinkingWire.Resolve(request.Thinking, _row.ThinkingDefault), AnthropicWire.Of(_row)));
        var parsed = AnthropicReplyStream.Read(new StringReader(_replies.Dequeue()));
        AnthropicRequestBody.SurfaceStructured(parsed, request, AnthropicWire.Of(_row));
        return parsed;
      }
    }

    private static string Reply(params string[] parts) => Sse(parts.SelectMany(p => p.Split('|')).ToArray());

    private static StructuredOutput.Result Diagnose(ILlmProvider p) =>
      StructuredOutput.Get(p, "s", "payload", Prompts.Prompts.DiagnoseToolName, Prompts.Prompts.DiagnoseToolDescription, Prompts.Prompts.DiagnoseSchema,
        j => JsonNode.Parse(j)?["faults"] != null ? (true, null) : (false, "missing faults"), 16000);

    [Theory]
    [InlineData("claude-sonnet-5-5")]
    [InlineData("claude-opus-5-5")]
    [InlineData("claude-fable-5-1")]
    public void A_verb_gets_its_result_through_the_real_path(string id)
    {
      var p = new WireProvider(id, Reply(Start, Thinking(0), TextBlocks(1, "{\"faults\":[]}"), Delta("end_turn", null, 9), Stop));

      var res = Diagnose(p);

      Assert.True(res.Ok, res.Error);
      Assert.Equal("{\"faults\":[]}", res.Json);
      Assert.Contains("\"format\"", p.Bodies[0]);
    }

    [Fact]
    public void A_verbs_repair_on_the_format_path_appends_and_names_no_tool()
    {
      var p = new WireProvider("claude-sonnet-5-5",
        Reply(Start, TextBlocks(0, "{\"nope\":1}"), Delta("end_turn", null, 3), Stop),
        Reply(Start, TextBlocks(0, "{\"faults\":[]}"), Delta("end_turn", null, 3), Stop));

      var res = Diagnose(p);

      Assert.True(res.Ok, res.Error);
      var second = JsonNode.Parse(p.Bodies[1])["messages"].AsArray();
      Assert.Equal(3, second.Count);
      var note = second[2]["content"][0]["text"].GetValue<string>();
      Assert.Contains("Return the corrected JSON object as your whole reply", note);
      Assert.DoesNotContain("diagnose_faults", note);
    }

    [Fact]
    public void A_refused_verb_says_so_and_does_not_spend_a_repair()
    {
      var p = new WireProvider("claude-opus-5-5", Reply(Start, Delta("refusal", "{\"type\":\"refusal\",\"category\":\"bio\"}"), Stop));

      var res = Diagnose(p);

      Assert.False(res.Ok);
      Assert.Contains("possible biological harm", res.Error);
      Assert.Single(p.Bodies);
      Assert.Equal(412, res.Usage.InputTokens);   // bio before output is billed
    }

    [Fact]
    public void A_refused_verb_in_an_unbilled_category_counts_no_cost()
    {
      var p = new WireProvider("claude-opus-5-5", Reply(Start, Delta("refusal", "{\"type\":\"refusal\",\"category\":\"cyber\"}"), Stop));

      Assert.Equal(0, Diagnose(p).Usage.InputTokens);
    }

    private static readonly List<LlmTool> Tools = new List<LlmTool>
    {
      new LlmTool { Name = "get_overview", Description = "d", InputSchemaJson = "{\"type\":\"object\",\"properties\":{}}" },
    };

    private static TurnResult Drive(ILlmProvider p, List<LlmMessage> messages, int maxChars = 120000, int steps = 12, bool thinking = true, Func<LlmBlock, string> execute = null, string force = null) =>
      AgentTurnDriver.Drive(new TurnDrive
      {
        Provider = p, System = "s", Messages = messages, Tools = Tools,
        Limits = new TurnLimits { MaxSteps = steps, WallClockMs = 0, AnswerBudget = 4000, MaxTranscriptChars = maxChars },
        Thinking = thinking, Effort = thinking ? "medium" : null, ForceToolName = force,
        Execute = execute ?? (c => "{\"ok\":true,\"result\":{}}"),
      }, Stopwatch.StartNew());

    [Theory]
    [InlineData("claude-sonnet-5-5")]
    [InlineData("claude-opus-5-5")]
    [InlineData("claude-fable-5-1")]
    public void A_chat_turn_runs_on_the_real_path_and_echoes_its_thinking(string id)
    {
      var p = new WireProvider(id,
        Reply(Start, Thinking(0), Call(1, "t1", "get_overview", "{}"), Delta("tool_use", null, 5), Stop),
        Reply(Start, TextBlocks(0, "Done."), Delta("end_turn", null, 5), Stop));

      var result = Drive(p, new List<LlmMessage> { LlmMessage.User("look") });

      Assert.Equal(TurnOutcome.Complete, result.Outcome);
      Assert.Equal("Done.", result.Text);
      var echoed = JsonNode.Parse(p.Bodies[1])["messages"][1]["content"][0];
      Assert.Equal("thinking", echoed["type"].GetValue<string>());
      Assert.Equal("sig0", echoed["signature"].GetValue<string>());
    }

    /// <summary>The page's three-step check, as a test: system, tools and every earlier message are the same from one step to the next. Only cache_control may move ("adding, moving, or removing").</summary>
    [Fact]
    public void Each_step_of_a_long_turn_is_append_only_with_a_thread_behind_it()
    {
      var replies = new List<string>();
      for (var i = 0; i < 8; i++)
        replies.Add(Reply(Start, Thinking(0), TextBlocks(1, "note " + i), Call(2, "t" + i, "get_overview", "{\"q\":\"café " + i + "\"}"), Delta("tool_use", null, 5), Stop));
      replies.Add(Reply(Start, TextBlocks(0, "Done."), Delta("end_turn", null, 5), Stop));
      var p = new WireProvider("claude-sonnet-5-5", replies.ToArray());
      var seeded = new List<LlmMessage> { LlmMessage.User("first"), LlmMessage.Assistant(new List<LlmBlock> { LlmBlock.TextBlock("an earlier answer") }), LlmMessage.User("now look") };

      var result = Drive(p, seeded, execute: c => "{\"ok\":true,\"result\":\"" + new string('x', 30000) + "\"}");

      Assert.Equal(TurnOutcome.Complete, result.Outcome);
      Assert.Equal(9, p.Bodies.Count);
      for (var i = 1; i < p.Bodies.Count; i++)
      {
        var before = Prefix(p.Bodies[i - 1]);
        var after = Prefix(p.Bodies[i]);
        Assert.Equal(before.System, after.System);
        Assert.Equal(before.Tools, after.Tools);
        Assert.True(after.Messages.Count > before.Messages.Count);
        for (var m = 0; m < before.Messages.Count; m++) Assert.Equal(before.Messages[m], after.Messages[m]);
      }
    }

    private static (string System, string Tools, List<string> Messages) Prefix(string body)
    {
      var o = JsonNode.Parse(body);
      foreach (var m in o["messages"].AsArray())
        foreach (var b in m["content"].AsArray())
          b.AsObject().Remove("cache_control");
      return (o["system"].ToJsonString(), o["tools"].ToJsonString(), o["messages"].AsArray().Select(m => m.ToJsonString()).ToList());
    }

    [Fact]
    public void Past_the_payload_budget_these_rows_do_not_elide_and_others_still_do()
    {
      string Long(LlmBlock c) => "{\"ok\":true,\"result\":\"" + new string('x', 30000) + "\"}";
      string[] Script() => Enumerable.Range(0, 6).Select(i => Reply(Start, Thinking(0), Call(1, "t" + i, "get_overview", "{}"), Delta("tool_use", null, 5), Stop))
        .Concat(new[] { Reply(Start, TextBlocks(0, "Done."), Delta("end_turn", null, 5), Stop) }).ToArray();

      var bound = new WireProvider("claude-sonnet-5-5", Script());
      var keeping = Drive(bound, new List<LlmMessage> { LlmMessage.User("look") }, execute: Long);
      Assert.Equal(0, keeping.ElidedToolResults);
      Assert.DoesNotContain(AgentLoopCore.ElidedMarker, bound.Bodies.Last());
      Assert.DoesNotContain("trimmed from the payload", AgentLoopCore.Meta(keeping, ""));

      var unbound = new WireProvider("claude-sonnet-5", Script());
      var eliding = Drive(unbound, new List<LlmMessage> { LlmMessage.User("look") }, execute: Long);
      Assert.True(eliding.ElidedToolResults > 0);
      Assert.Contains(AgentLoopCore.ElidedMarker.Replace("\"", ""), unbound.Bodies.Last());
    }

    /// <summary>Why a mid-thread model switch and a head-trim cannot break a block: a turn starts from stored words, so no thinking block crosses a turn, whichever model wrote it.</summary>
    [Fact]
    public void A_new_turn_carries_no_thinking_block_so_a_switch_or_a_trim_has_nothing_to_break()
    {
      var stored = new List<Dwight.Session.StoredMessage>
      {
        new Dwight.Session.StoredMessage { Who = "me", Src = "first" },
        new Dwight.Session.StoredMessage { Who = "dw", Src = "an answer from another model" },
        new Dwight.Session.StoredMessage { Who = "me", Src = "next" },
      };

      var seeded = ChatToTranscript.Build(stored);

      Assert.All(seeded, m => Assert.All(m.Blocks, b => Assert.Equal("text", b.Kind)));

      var p = new WireProvider("claude-opus-5-5", Reply(Start, TextBlocks(0, "ok"), Delta("end_turn", null, 1), Stop));
      Drive(p, seeded);
      Assert.DoesNotContain("\"thinking\",\"thinking\"", p.Bodies[0]);
    }

    [Fact]
    public void A_refusal_ends_the_turn_in_words_keeping_what_earlier_steps_said_and_running_nothing()
    {
      var ran = new List<string>();
      var p = new WireProvider("claude-sonnet-5-5",
        Reply(Start, TextBlocks(0, "I will look first."), Call(1, "t1", "get_overview", "{}"), Delta("tool_use", null, 5), Stop),
        Reply(Start, TextBlocks(0, "half a sentence"), Call(1, "t2", "get_overview", "{}"), Delta("refusal", "{\"type\":\"refusal\",\"category\":\"cyber\"}", 9), Stop));

      var result = Drive(p, new List<LlmMessage> { LlmMessage.User("look") }, execute: c => { ran.Add(c.ToolUseId); return "{\"ok\":true,\"result\":{}}"; });

      Assert.Equal(TurnOutcome.Refused, result.Outcome);
      Assert.Equal(LlmRefusal.Sentence("cyber"), result.Note);
      Assert.Equal("I will look first.", result.Text);        // the refused partial is discarded
      Assert.Equal(new[] { "t1" }, ran);                      // the call in the refused reply did not run
      Assert.Equal(14, result.Usage.OutputTokens);            // mid-stream is billed
    }

    [Fact]
    public void A_refusal_does_not_get_the_approved_builds_repair_step()
    {
      var p = new WireProvider("claude-sonnet-5-5", Reply(Start, Delta("refusal", "{\"type\":\"refusal\",\"category\":null}"), Stop));

      var result = AgentTurnDriver.Drive(new TurnDrive
      {
        Provider = p, System = "s", Messages = new List<LlmMessage> { LlmMessage.User("build") }, Tools = Tools,
        Limits = new TurnLimits { MaxSteps = 12, AnswerBudget = 4000, MaxTranscriptChars = 120000 },
        ActingTool = AgentToolCatalog.BuildRegion, ApprovedRegions = new[] { "A1" },
        Execute = c => "{}",
      }, Stopwatch.StartNew());

      Assert.Equal(TurnOutcome.Refused, result.Outcome);
      Assert.False(result.BuildRepairFired);
      Assert.Single(p.Bodies);
      Assert.NotNull(result.Note);
    }

    [Fact]
    public void The_stop_reason_decides_the_outcome_before_any_tool()
    {
      Assert.Equal(TurnOutcome.Refused, AgentLoopCore.PostResponse("refusal", true));
      Assert.Equal(TurnOutcome.Refused, AgentLoopCore.PostResponse("refusal", false));
    }

    // ---- the forced card (Brief BT) ---------------------------------------------------------------------------

    [Theory]
    [InlineData("claude-sonnet-5-5")]
    [InlineData("claude-opus-5-5")]
    [InlineData("claude-fable-5-1")]
    public void The_forced_card_is_false_for_them_as_a_fact_and_they_take_the_unforced_follow_up(string id)
    {
      var p = new WireProvider(id, Reply(Start, TextBlocks(0, "ok"), Delta("end_turn", null, 1), Stop));

      var result = Drive(p, new List<LlmMessage> { LlmMessage.User(AutoFollowUp.AnnouncementFact) }, force: "get_overview");

      Assert.False(result.ProviderForces);
      Assert.False(result.Forced);
      Assert.False(JsonNode.Parse(p.Bodies[0]).AsObject().ContainsKey("tool_choice"));
      // Unforced, the follow-up runs under an automatic mode and not under Ask, with no escape clause in its words.
      Assert.True(AutoFollowUp.ShouldRunForAnnouncement(automaticMode: true, canForce: false, caught: true, thisTurnIsAFollowUp: false));
      Assert.False(AutoFollowUp.ShouldRunForAnnouncement(automaticMode: false, canForce: false, caught: true, thisTurnIsAFollowUp: false));
      Assert.DoesNotContain("why you cannot", AutoFollowUp.AnnouncementFact);
    }

    // ---- the catalogue: facts, prices, and who resolves to what ---------------------------------------------------

    [Theory]
    [InlineData("claude-sonnet-5-5", "Strong", 2.0, 10.0, 0.10, 2.50)]
    [InlineData("claude-opus-5-5", "Top", 4.0, 20.0, 0.20, 5.00)]
    [InlineData("claude-fable-5-1", "Top", 10.0, 50.0, 0.25, 12.50)]
    public void The_prices_are_the_vendors_page_of_31_October(string id, string tier, double input, double output, double read, double write)
    {
      var m = Row(id);
      Assert.Equal(tier, m.Tier);
      Assert.Equal(input, m.InputPerMTok);
      Assert.Equal(output, m.OutputPerMTok);
      Assert.Equal(read, m.CacheReadPerMTok);
      Assert.Equal(write, m.CacheWritePerMTok);
      Assert.Equal(1000000, m.ContextWindow);
    }

    [Fact]
    public void The_catalogue_was_bumped_so_the_rows_reach_an_existing_install()
    {
      Assert.True(Catalogue["catalogueVersion"].GetValue<int>() >= 12);
      Assert.DoesNotContain("deliberately NOT listed", Catalogue["excludedNote"].GetValue<string>());
    }

    private static List<ModelInfo> Anthropic(bool flipped)
    {
      return Catalogue["models"].AsArray().Where(m => m["provider"].GetValue<string>() == "Anthropic").Select(m =>
      {
        var id = m["id"].GetValue<string>();
        // 1 November 2026: the shipped catalogue carries the flip, so the "before" state is stated here, not read from the file.
        var ruled = id == "claude-sonnet-5-5" || id == "claude-opus-5-5";
        return Row(id, pinOnly: ruled ? !flipped : (bool?)null, agentic: ruled ? flipped : (bool?)null);
      }).ToList();
    }

    [Theory]
    [InlineData("Strong", "Strong", "Top", "claude-sonnet-5")]   // Balanced, before the gate
    [InlineData("Top", "Strong", "Top", "claude-opus-5")]        // Quality and Ultra, before the gate
    public void Until_the_gate_passes_nothing_resolves_to_a_new_row(string target, string floor, string ceiling, string expected)
    {
      Assert.Equal(expected, ModelPick.Choose(Anthropic(false), target, floor, ceiling, true).Id);
      var none = "Strong";
      Assert.Equal(expected, ModelPick.Choose(ModelPick.AgentPool(Anthropic(false), false, ref none), target, floor, ceiling, true).Id);
    }

    [Theory]
    [InlineData("Strong", "Strong", "Top", "claude-sonnet-5-5")]   // Balanced
    [InlineData("Top", "Strong", "Top", "claude-opus-5-5")]        // Quality and Ultra
    public void The_flip_is_two_facts_on_two_rows_and_gives_the_ruled_resolution(string target, string floor, string ceiling, string expected)
    {
      var f = floor;
      var pool = ModelPick.AgentPool(Anthropic(true), false, ref f);
      Assert.Equal(expected, ModelPick.Choose(pool, target, f, ceiling, true).Id);
      Assert.Equal(expected, ModelPick.Choose(Anthropic(true), target, floor, ceiling, true).Id);
    }

    [Fact]
    public void No_profile_ever_resolves_to_Fable_even_alone_in_its_band_and_a_pin_still_reaches_it()
    {
      var fable = Row("claude-fable-5-1");
      Assert.True(fable.PinOnly);
      Assert.Null(ModelPick.Choose(new[] { fable }, "Top", "Top", "Top", false));
      foreach (var flipped in new[] { false, true })
        foreach (var target in new[] { "Fast", "Mid", "Strong", "Top" })
          Assert.NotEqual("claude-fable-5-1", ModelPick.Choose(Anthropic(flipped), target, target, target, false).Id);
      // A pin goes by id, not through the choice.
      Assert.False(fable.Agentic);
      fable.PinAgenticForGate();
      Assert.True(fable.Agentic);
      Assert.True(ChatHeader.PinThinks(fable, "Anthropic", "balanced"));
    }

    [Fact]
    public void A_pinned_chat_on_them_thinks_at_the_profile_and_Economy_stays_off_and_Sonnet_5_is_unchanged()
    {
      Assert.True(ChatHeader.PinThinks(Row("claude-sonnet-5-5"), "Anthropic", "balanced"));
      Assert.False(ChatHeader.PinThinks(Row("claude-sonnet-5-5"), "Anthropic", "economy"));
      Assert.False(ChatHeader.PinThinks(Row("claude-sonnet-5"), "Anthropic", "balanced"));
    }

    // ---- the gate's strict check -----------------------------------------------------------------------------------

    [Fact]
    public void The_strict_check_rides_adaptive_with_its_header_and_never_between_tools()
    {
      var m = Row("claude-sonnet-5-5");
      var wire = new AnthropicWire { StructuredFormat = m.StructuredFormat, ForcedTool = false, ThinkingOffEffort = m.ThinkingOffEffort, StrictPrefixCheck = true };

      var chat = new LlmRequest { System = "s", Messages = { LlmMessage.User("x") }, Thinking = true, Effort = "medium" };
      var body = JsonNode.Parse(AnthropicRequestBody.Build(chat, m.Id, ThinkingWireState.Adaptive, wire));
      Assert.Equal("error", body["thinking"]["block_binding"]["prefix_mismatch_behavior"].GetValue<string>());
      Assert.Equal("thinking-binding-controls-2026-08-01", wire.BetaHeader);

      var verb = JsonNode.Parse(AnthropicRequestBody.Build(Verb(), m.Id, ThinkingWireState.BetweenTools, wire));
      Assert.Single(verb["thinking"].AsObject());   // block_binding beside between_tools is a 400
    }

    [Fact]
    public void In_the_product_the_gates_switches_are_off_and_nothing_sets_them()
    {
      Assert.False(AnthropicWire.StrictPrefixCheckForGate);
      Assert.False(AnthropicWire.ElideAnywayForGate);
      Assert.Null(AnthropicWire.Of(Row("claude-sonnet-5-5")).BetaHeader);
      Assert.False(JsonNode.Parse(AnthropicRequestBody.Build(new LlmRequest { System = "s", Messages = { LlmMessage.User("x") }, Thinking = true, Effort = "medium" },
        "claude-sonnet-5-5", ThinkingWireState.Adaptive, AnthropicWire.Of(Row("claude-sonnet-5-5"))))["thinking"].AsObject().ContainsKey("block_binding"));

      var dir = new DirectoryInfo(AppContext.BaseDirectory);
      while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Dwight.sln"))) dir = dir.Parent;
      foreach (var file in Directory.GetFiles(Path.Combine(dir.FullName, "Dwight"), "*.cs", SearchOption.AllDirectories))
      {
        var src = File.ReadAllText(file);
        Assert.DoesNotContain("StrictPrefixCheckForGate =", src.Replace("StrictPrefixCheck = model.ThinkingBound && StrictPrefixCheckForGate", ""));
        Assert.DoesNotContain("ElideAnywayForGate =", src);
      }
      var provider = File.ReadAllText(Path.Combine(dir.FullName, "Dwight", "Llm", "AnthropicProvider.cs"));
      Assert.Contains("var beta = AnthropicWire.Of(Row).BetaHeader;", provider);
      Assert.Contains("(Row?.ThinkingBound ?? false) && !AnthropicWire.ElideAnywayForGate", provider);
    }

    // ---- Fable's retention ----------------------------------------------------------------------------------------

    [Fact]
    public void Fable_says_its_retention_where_it_is_chosen_and_on_the_public_page_and_no_other_row_does()
    {
      var fable = Row("claude-fable-5-1");
      const string Line = "Claude Fable 5.1 requires Anthropic to keep requests for 30 days, so it is not available under zero data retention.";
      Assert.Equal(Line, fable.RetentionSentence);
      foreach (var id in new[] { "claude-opus-5-5", "claude-sonnet-5-5", "claude-sonnet-5", "claude-haiku-5-5" }) Assert.Null(Row(id).RetentionSentence);

      var dir = new DirectoryInfo(AppContext.BaseDirectory);
      while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Dwight.sln"))) dir = dir.Parent;
      Assert.Contains(Line, File.ReadAllText(Path.Combine(dir.FullName, "docs", "public", "privacy.md")));
      Assert.Contains("[\"sentence\"] = Dwight.Agent.ChatHeader.RowTitle(m, balanced)", File.ReadAllText(Path.Combine(dir.FullName, "Dwight", "UI", "DwightSettingsPanel.cs")));
      Assert.EndsWith(Line, ChatHeader.RowTitle(fable, Row("claude-sonnet-5-5")));   // Brief CW puts the price first; the retention sentence still stands whole
      Assert.Contains("sentence:p.sentence||''", File.ReadAllText(Path.Combine(dir.FullName, "Dwight", "Resources", "settings.html")));

      // The chat's picker offers only agentic rows, so it reaches Fable only by a gate's pin, and it says the same there.
      fable.PinAgenticForGate();
      var choice = ChatHeader.ModelChoices(new[] { fable, Row("claude-sonnet-5") }).Single(c => c.Id == "claude-fable-5-1");
      Assert.Equal(Line, choice.Title);
    }

    [Fact]
    public void A_400_on_a_row_that_needs_retention_says_why_and_no_other_error_or_row_does()
    {
      var fable = Row("claude-fable-5-1");
      Assert.Equal(30, fable.RetentionDays);
      Assert.Equal(0, Row("claude-opus-5-5").RetentionDays);
      var clause = AnthropicRequestBody.RetentionClause(fable.DisplayName, fable.RetentionDays);

      var said = AnthropicRequestBody.DescribeError(400, "{\"error\":{\"message\":\"invalid_request_error\"}}", clause);

      Assert.Equal("Claude returned an error (400): invalid_request_error Claude Fable 5.1 needs 30-day data retention, so an organisation on zero data retention gets this error. If yours is one, pick another model.", said);
      Assert.DoesNotContain("retention", AnthropicRequestBody.DescribeError(429, "{}", clause));
      Assert.DoesNotContain("retention", AnthropicRequestBody.DescribeError(400, "{\"error\":{\"message\":\"x\"}}", AnthropicRequestBody.RetentionClause("Claude Opus 5.5", 0)));
      Assert.StartsWith("Claude rejected the API key (401).", AnthropicRequestBody.DescribeError(401, "{}"));
    }
  }
}
