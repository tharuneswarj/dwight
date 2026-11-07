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
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// Brief BV: OpenAI's tool-bearing turns on the Responses endpoint. The body against the real serialiser and the published descriptors; the reader
  /// against the REDUCED live captures of 31 October 2026, and against HAND-MADE fixtures for the three replies no request met.
  /// </summary>
  [Collection(StreamFixtures.CaptureWindow)]
  public class OpenAIResponsesTests
  {
    private const string CallOne = StreamFixtures.ResponsesCall1;
    private const string CallTwo = StreamFixtures.ResponsesCall2;
    private const string Parallel = StreamFixtures.ResponsesParallel;
    private const string TextReply = StreamFixtures.ResponsesText;
    private const string DoneCall = "\"type\":\"response.output_item.done\",\"item\":{\"id\":\"fc_";
    private const string AddedCall = "\"type\":\"response.output_item.added\",\"item\":{\"id\":\"fc_";
    private const string Incomplete = "openai-responses-HANDMADE-incomplete-at-the-budget.sse";
    private const string Failed = "openai-responses-HANDMADE-failed.sse";
    private const string Refusal = "openai-responses-HANDMADE-refusal.sse";

    private static List<LlmTool> PublishedTools() =>
      AgentToolCatalog.All().Concat(AgentHostTools.All())
        .Select(t => new LlmTool { Name = t.Name, Description = t.Description, InputSchemaJson = t.InputSchemaJson }).ToList();

    private static LlmRequest Turn(bool thinking = true, string effort = "medium", int budget = 4000) => new LlmRequest
    {
      System = "system words",
      Messages = { LlmMessage.User("build A1") },
      Tools = PublishedTools(),
      MaxTokens = budget,
      Thinking = thinking,
      Effort = effort,
    };

    private static JsonObject Body(LlmRequest r) => (JsonObject)JsonNode.Parse(OpenAIResponses.BuildBody(r, "gpt-test"));

    private static LlmResponse Read(string text) => OpenAIResponses.ReadStream(StreamFixtures.Reader(text), CancellationToken.None, "OpenAI");

    private static OpenAIProvider OpenAi() => new OpenAIProvider("https://api.example/v1", null, "gpt-test", ProviderCapability.StrictSchema, true, "OpenAI");

    // ---- the route ---------------------------------------------------------------------------------------

    [Fact]
    public void Only_the_OpenAI_instances_tool_bearing_turns_move()
    {
      var openAi = OpenAi();
      Assert.Equal("https://api.example/v1/responses", openAi.EndpointFor(Turn()));
      var verb = new LlmRequest { System = "s", Messages = { LlmMessage.User("explain") } };
      Assert.Equal("https://api.example/v1/chat/completions", openAi.EndpointFor(verb));
      foreach (var label in new[] { "Ollama Cloud", OpenAIProvider.OllamaLocalLabel })
      {
        var ollama = new OpenAIProvider("https://ollama.example/v1", null, "m", ProviderCapability.Format, false, label);
        Assert.False(ollama.UsesResponses(Turn()));
        Assert.Equal("https://ollama.example/v1/chat/completions", ollama.EndpointFor(Turn()));
      }
    }

    /// <summary>The golden files are what this adapter sent BEFORE this unit, written from the unchanged code.</summary>
    [Theory]
    [InlineData("ollama-cloud-chat-body-with-tools.json", "Ollama Cloud", "glm-5.3:cloud", ProviderCapability.PromptOnly)]
    [InlineData("ollama-local-chat-body-with-tools.json", OpenAIProvider.OllamaLocalLabel, "qwen3.5:9b", ProviderCapability.Format)]
    public void Ollamas_body_is_byte_identical(string golden, string label, string model, object capability)
    {
      var request = new LlmRequest
      {
        System = "system words",
        Messages =
        {
          LlmMessage.User("build A1"),
          LlmMessage.Assistant(new List<LlmBlock> { LlmBlock.TextBlock("Reading first."), LlmBlock.ToolUse("call_1", "get_overview", "{}") }),
          new LlmMessage { Role = LlmRole.User, Blocks = { LlmBlock.ToolResult("call_1", "{\"ok\":true}") } },
        },
        Tools =
        {
          new LlmTool { Name = "get_overview", Description = "Read the overview.", InputSchemaJson = "{\"type\":\"object\",\"properties\":{}}" },
          new LlmTool { Name = "request_mutation", Description = "Ask for a card.", InputSchemaJson = "{\"type\":\"object\",\"properties\":{\"kind\":{\"type\":\"string\"}},\"required\":[\"kind\"]}" },
        },
        MaxTokens = 4000,
        ForceToolName = "request_mutation",
        Thinking = true,
        Effort = "high",
      };
      var provider = new OpenAIProvider("https://ollama.example/v1", null, model, (ProviderCapability)capability, false, label, reasoningModel: true);
      var want = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "golden", golden));
      Assert.Equal(want, provider.BuildBody(request, false));
    }

    [Fact]
    public void A_verb_on_OpenAI_keeps_its_chat_completions_body()
    {
      var body = (JsonObject)JsonNode.Parse(OpenAi().BuildBody(new LlmRequest { System = "s", Messages = { LlmMessage.User("explain") }, MaxTokens = 1000 }, false));
      Assert.Equal("system", body["messages"][0]["role"].GetValue<string>());
      Assert.Equal(1000, body["max_completion_tokens"].GetValue<int>());
      Assert.True(body["stream_options"]["include_usage"].GetValue<bool>());
      foreach (var absent in new[] { "tools", "reasoning_effort", "reasoning", "store", "input", "instructions", "tool_choice" }) Assert.False(body.ContainsKey(absent), absent);
    }

    // ---- the body ----------------------------------------------------------------------------------------

    [Fact]
    public void Nothing_is_stored_and_encrypted_reasoning_is_asked_for_by_name()
    {
      var body = Body(Turn());
      Assert.False(body["store"].GetValue<bool>());
      Assert.Equal("reasoning.encrypted_content", body["include"].AsArray().Single().GetValue<string>());
      Assert.True(body["stream"].GetValue<bool>());
      Assert.Equal("gpt-test", body["model"].GetValue<string>());
      Assert.Equal("system words", body["instructions"].GetValue<string>());
      foreach (var absent in new[] { "messages", "stream_options", "max_completion_tokens", "max_tokens", "reasoning_effort", "previous_response_id" }) Assert.False(body.ContainsKey(absent), absent);
    }

    /// <summary>The conformance test: every published descriptor, flat, with its schema unchanged. A third translation of one list, never a second list.</summary>
    [Fact]
    public void Every_published_tool_is_on_the_wire_in_this_endpoints_shape()
    {
      var tools = Body(Turn())["tools"].AsArray();
      var published = PublishedTools();
      Assert.Equal(published.Count, tools.Count);
      for (var i = 0; i < published.Count; i++)
      {
        var t = tools[i].AsObject();
        Assert.Equal("function", t["type"].GetValue<string>());
        Assert.Equal(published[i].Name, t["name"].GetValue<string>());
        Assert.Equal(published[i].Description, t["description"].GetValue<string>());
        Assert.Equal(JsonNode.Parse(published[i].InputSchemaJson).ToJsonString(), t["parameters"].ToJsonString());
        Assert.False(t["strict"].GetValue<bool>());   // our schemas use optional fields, and omitting this asks for strict with a quiet fallback
        Assert.False(t.ContainsKey("function"));
      }
    }

    [Fact]
    public void A_forced_tool_is_this_endpoints_tool_choice_and_an_ordinary_turn_has_none()
    {
      var forced = Turn();
      forced.ForceToolName = AgentHostTools.RequestMutation;
      Assert.Equal("{\"type\":\"function\",\"name\":\"" + AgentHostTools.RequestMutation + "\"}", Body(forced)["tool_choice"].ToJsonString());
      Assert.False(Body(Turn()).ContainsKey("tool_choice"));
    }

    [Theory]
    [InlineData("low", ThinkingBudget.ReserveLow)]
    [InlineData("medium", ThinkingBudget.ReserveMedium)]
    [InlineData("high", ThinkingBudget.ReserveHigh)]
    [InlineData("max", ThinkingBudget.ReserveMax)]
    public void The_effort_is_sent_and_the_reserve_rides_on_top_of_the_answer_budget(string effort, int reserve)
    {
      var body = Body(Turn(effort: effort));
      Assert.Equal(effort, body["reasoning"]["effort"].GetValue<string>());
      Assert.Equal(4000 + reserve, body["max_output_tokens"].GetValue<int>());
    }

    [Fact]
    public void With_no_effort_named_the_API_default_runs_and_it_is_reserved_for()
    {
      var body = Body(Turn(effort: null));
      Assert.False(body.ContainsKey("reasoning"));
      Assert.Equal(4000 + ThinkingBudget.ReserveMedium, body["max_output_tokens"].GetValue<int>());
    }

    [Fact]
    public void Thinking_off_says_none_out_loud_and_adds_no_reserve()
    {
      var body = Body(Turn(thinking: false, effort: null));
      Assert.Equal("none", body["reasoning"]["effort"].GetValue<string>());
      Assert.Equal(4000, body["max_output_tokens"].GetValue<int>());
    }

    [Fact]
    public void A_recorded_call_with_no_arguments_is_refused_in_our_own_words()
    {
      var r = Turn();
      r.Messages.Add(LlmMessage.Assistant(new List<LlmBlock> { LlmBlock.ToolUse("call_1", "get_overview", "") }));
      r.Messages.Add(new LlmMessage { Role = LlmRole.User, Blocks = { LlmBlock.ToolResult("call_1", "x") } });
      var ex = Assert.Throws<LlmException>(() => OpenAIResponses.BuildBody(r, "m"));
      Assert.Contains("has no arguments recorded", ex.Message);
    }

    [Fact]
    public void Text_from_an_earlier_turn_is_a_plain_assistant_message()
    {
      var r = Turn();
      r.Messages.Add(LlmMessage.Assistant(new List<LlmBlock> { LlmBlock.TextBlock("Earlier answer.") }));
      r.Messages.Add(LlmMessage.User("and then?"));
      var input = Body(r)["input"].AsArray();
      Assert.Equal("{\"role\":\"assistant\",\"content\":\"Earlier answer.\"}", input[1].ToJsonString());
      Assert.Equal("{\"role\":\"user\",\"content\":\"and then?\"}", input[2].ToJsonString());
    }

    // ---- the reader --------------------------------------------------------------------------------------

    [Fact]
    public void A_call_comes_back_with_its_reasoning_item_first_and_its_arguments_whole()
    {
      var r = Read(StreamFixtures.Read(CallTwo));
      Assert.Equal(2, r.Blocks.Count);
      Assert.Equal(LlmBlock.ResponseItemKind, r.Blocks[0].Kind);
      Assert.Equal("ENCRYPTED-CONTENT-REDUCED-5", JsonNode.Parse(r.Blocks[0].RawJson)["encrypted_content"].GetValue<string>());
      Assert.Equal("tool_use", r.Blocks[1].Kind);
      Assert.Equal("call_zhLVt1yjG8GLyYztyfjFzBos", r.Blocks[1].ToolUseId);
      Assert.Equal("get_scaffold_map", r.Blocks[1].ToolName);
      Assert.Equal("{}", r.Blocks[1].ToolInputJson);
      Assert.Equal("tool_use", r.StopReason);
      Assert.True(r.WantsTool);
    }

    [Fact]
    public void Text_is_read_from_its_message_item_and_the_reasoning_is_not_prose()
    {
      var r = Read(StreamFixtures.Read(TextReply));
      Assert.StartsWith("**A1", r.Text);
      Assert.Equal("stop", r.StopReason);
      Assert.False(r.WantsTool);
      Assert.Equal("final_answer", JsonNode.Parse(r.Blocks.Single(b => b.Kind == "text").RawJson)["phase"].GetValue<string>());
    }

    [Fact]
    public void Usage_splits_the_input_and_keeps_reasoning_beside_the_output()
    {
      var u = Read(StreamFixtures.Read(CallTwo)).Usage;   // the wire's own numbers: 11435 = 11062 cached + 370 written + 3
      Assert.Equal(3, u.InputTokens);
      Assert.Equal(11062, u.CacheReadInputTokens);
      Assert.Equal(370, u.CacheCreationInputTokens);
      Assert.Equal(11435, u.TotalInputTokens);
      Assert.Equal(76, u.OutputTokens);
      Assert.Equal(58, u.ThinkingTokens);
    }

    [Fact]
    public void A_reply_cut_at_the_budget_is_a_truncation_and_its_half_call_is_never_dispatched()
    {
      var r = Read(StreamFixtures.Read(Incomplete));
      Assert.Equal("max_tokens", r.StopReason);
      Assert.Empty(r.ToolUses);
      Assert.True(r.ThoughtWithoutAnswer);   // 4000 of 4000 tokens were reasoning
      Assert.Equal(TurnOutcome.Truncated, AgentLoopCore.PostResponse(r.StopReason, r.ToolUses.Count > 0));
    }

    [Fact]
    public void A_reply_cut_for_any_other_reason_says_so_and_is_not_a_result()
    {
      var filtered = StreamFixtures.Read(Incomplete).Replace("\"reason\":\"max_output_tokens\"", "\"reason\":\"content_filter\"");
      Assert.Contains("content filter", Assert.Throws<LlmException>(() => Read(filtered)).Message);
      var other = StreamFixtures.Read(Incomplete).Replace("\"reason\":\"max_output_tokens\"", "\"reason\":\"max_messages\"");
      Assert.Contains("max_messages", Assert.Throws<LlmException>(() => Read(other)).Message);
    }

    [Fact]
    public void A_failed_event_says_what_it_is()
    {
      var ex = Assert.Throws<LlmException>(() => Read(StreamFixtures.Read(Failed)));
      Assert.Contains("could not finish this reply", ex.Message);
      Assert.Contains("The server had an error processing your request.", ex.Message);
      Assert.Contains("server_error", ex.Message);
    }

    [Fact]
    public void An_error_event_is_the_providers_failure_in_its_own_words_not_damage()
    {
      var text = "event: error\ndata: {\"type\":\"error\",\"code\":\"rate_limit_exceeded\",\"message\":\"Slow down.\",\"sequence_number\":1}\n\n";
      var ex = Assert.Throws<LlmException>(() => Read(text));
      Assert.Equal("OpenAI returned a streaming error: Slow down.", ex.Message);
    }

    [Fact]
    public void A_refusal_says_what_it_is()
    {
      var ex = Assert.Throws<LlmException>(() => Read(StreamFixtures.Read(Refusal)));
      Assert.Equal("OpenAI's model refused this request: I cannot help with that.", ex.Message);
    }

    [Theory]
    [InlineData(CallOne)]
    [InlineData(TextReply)]
    public void A_stream_that_ends_without_its_terminal_event_is_damage(string fixture)
    {
      var text = StreamFixtures.Read(fixture);
      var k = StreamFixtures.IndexOf(text, "\"type\":\"response.completed\"");
      var ex = Assert.Throws<LlmException>(() => Read(StreamFixtures.Join(StreamFixtures.Blocks(text).Take(k - 1))));
      Assert.Contains("no terminal event arrived", ex.Message);
    }

    [Fact]
    public void A_stream_with_nothing_in_it_is_damage_not_a_crash() =>
      Assert.Contains("no terminal event arrived", Assert.Throws<LlmException>(() => Read("")).Message);

    [Fact]
    public void A_truncated_event_is_refused_by_its_number()
    {
      var text = StreamFixtures.Read(CallOne);
      var k = StreamFixtures.IndexOf(text, DoneCall);
      var damaged = StreamFixtures.Damage(text, k, b => b.Substring(0, b.IndexOf("\"item\"")));
      var ex = Assert.Throws<LlmException>(() => Read(damaged));
      Assert.Contains("event " + k + " (line ", ex.Message);
    }

    [Fact]
    public void An_item_that_starts_and_never_finishes_in_a_finished_reply_is_damage()
    {
      var text = StreamFixtures.Read(CallOne);
      var blocks = StreamFixtures.Blocks(text);
      var k = StreamFixtures.IndexOf(text, DoneCall);
      blocks.RemoveAt(k - 1);
      var ex = Assert.Throws<LlmException>(() => Read(StreamFixtures.Join(blocks)));
      Assert.Contains("ended without finishing it", ex.Message);
    }

    [Fact]
    public void An_item_that_finishes_without_starting_means_an_event_was_lost()
    {
      var text = StreamFixtures.Read(CallOne);
      var blocks = StreamFixtures.Blocks(text);
      var k = StreamFixtures.IndexOf(text, AddedCall);
      blocks.RemoveAt(k - 1);
      Assert.Contains("never started", Assert.Throws<LlmException>(() => Read(StreamFixtures.Join(blocks))).Message);
    }

    [Theory]
    [InlineData("\"arguments\":\"{\\\"types\\\":[\"", "not valid JSON")]
    [InlineData("\"arguments\":\"[7]\"", "not a set of arguments")]
    [InlineData("\"arguments\":\"\"", "no arguments sent")]
    public void Arguments_that_are_not_an_object_are_refused_not_dispatched(string arguments, string says)
    {
      const string real = "\"arguments\":\"{\\\"types\\\":[\\\"Number Slider\\\",\\\"Number\\\",\\\"Series\\\"]}\"";
      var original = StreamFixtures.Read(Parallel);
      Assert.Contains(real, original);
      var text = original.Replace(real, arguments);
      Assert.Contains(says, Assert.Throws<LlmException>(() => Read(text)).Message);
    }

    [Fact]
    public void A_call_with_no_id_cannot_be_answered_and_is_refused()
    {
      var text = StreamFixtures.Read(CallOne).Replace("\"call_id\":\"call_uqrKMKNWEfQCnmUuWkPASj5m\"", "\"call_id\":\"\"");
      Assert.Contains("no call id", Assert.Throws<LlmException>(() => Read(text)).Message);
    }

    [Fact]
    public void A_cancelled_read_aborts()
    {
      using (var cts = new CancellationTokenSource())
      {
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
          OpenAIResponses.ReadStream(StreamFixtures.Reader(StreamFixtures.Read(CallOne)), cts.Token, "OpenAI"));
      }
    }

    [Fact]
    public void A_done_sentinel_is_tolerated_and_is_not_the_end_of_a_reply()
    {
      Assert.StartsWith("**A1", Read(StreamFixtures.Read(TextReply) + "data: [DONE]\n\n").Text);
      var cut = StreamFixtures.Read(CallOne);
      var k = StreamFixtures.IndexOf(cut, "\"type\":\"response.completed\"");
      var ex = Assert.Throws<LlmException>(() => Read(StreamFixtures.Join(StreamFixtures.Blocks(cut).Take(k - 1)) + "data: [DONE]\n\n"));
      Assert.Contains("no terminal event arrived", ex.Message);
    }

    [Fact]
    public void An_event_type_this_build_has_not_met_is_passed_over_but_an_item_it_has_not_met_goes_back()
    {
      var added = "event: response.output_item.added\ndata: {\"type\":\"response.output_item.added\",\"output_index\":9,\"item\":{\"type\":\"configuration_update\"}}\n\n" +
                  "event: response.brand_new\ndata: {\"type\":\"response.brand_new\"}\n\n" +
                  "event: response.output_item.done\ndata: {\"type\":\"response.output_item.done\",\"output_index\":9,\"item\":{\"type\":\"configuration_update\",\"reasoning\":{\"effort\":\"high\"}}}\n\n";
      var text = StreamFixtures.Read(CallOne);
      var r = Read(added + text);
      var item = r.Blocks.Last();
      Assert.Equal(LlmBlock.ResponseItemKind, item.Kind);
      Assert.Equal("configuration_update", JsonNode.Parse(item.RawJson)["type"].GetValue<string>());
    }

    // ---- the transcript ----------------------------------------------------------------------------------

    [Fact]
    public void The_pre_flight_refuses_a_provider_item_with_nothing_in_it()
    {
      var messages = new List<LlmMessage>
      {
        LlmMessage.User("hi"),
        LlmMessage.Assistant(new List<LlmBlock> { LlmBlock.ResponseItem(""), LlmBlock.TextBlock("x") }),
      };
      Assert.Contains("output item with nothing in it", AgentLoopCore.CheckTranscript(messages));
      messages[1].Blocks[0] = LlmBlock.ResponseItem("{\"type\":\"reasoning\"}");
      Assert.Null(AgentLoopCore.CheckTranscript(messages));
    }

    [Fact]
    public void Encrypted_reasoning_counts_against_the_transcripts_budget()
    {
      var messages = new List<LlmMessage> { LlmMessage.Assistant(new List<LlmBlock> { LlmBlock.ResponseItem(new string('x', 1000)) }) };
      Assert.Equal(1000, AgentLoopCore.Size(messages));
    }

    /// <summary>A provider that plays real streams through the real reader, and builds the real body for every request it is sent.</summary>
    private sealed class WireProvider : ILlmProvider
    {
      private readonly Queue<string> _streams;
      public WireProvider(params string[] fixtures) { _streams = new Queue<string>(fixtures); }
      public string ModelId => "gpt-test";
      public ProviderCapability Capability => ProviderCapability.StrictSchema;
      public bool SendsForcedTool => true;
      public bool ThinksWhileForced => true;
      public readonly List<JsonObject> Bodies = new List<JsonObject>();
      public readonly List<LlmRequest> Seen = new List<LlmRequest>();

      public LlmResponse Send(LlmRequest request, CancellationToken cancel = default)
      {
        Seen.Add(request);
        Bodies.Add((JsonObject)JsonNode.Parse(OpenAIResponses.BuildBody(request, ModelId)));
        return OpenAIResponses.ReadStream(StreamFixtures.Reader(StreamFixtures.Read(_streams.Dequeue())), cancel, "OpenAI");
      }
    }

    private static TurnResult Drive(ILlmProvider provider, string force = null) =>
      AgentTurnDriver.Drive(new TurnDrive
      {
        Provider = provider,
        System = "system",
        Messages = new List<LlmMessage> { LlmMessage.User("build A1") },
        Tools = PublishedTools(),
        Limits = new TurnLimits { MaxSteps = 8, WallClockMs = 0, MaxTranscriptChars = 1_000_000, AnswerBudget = 1000 },
        Thinking = true,
        Effort = "medium",
        ForceToolName = force,
        Execute = b => "{\"ok\":true,\"result\":{\"read\":\"" + b.ToolName + "\"}}",
      }, Stopwatch.StartNew());

    private static string[] Types(JsonObject body) =>
      body["input"].AsArray().Select(i => i["type"]?.GetValue<string>() ?? i["role"].GetValue<string>()).ToArray();

    [Fact]
    public void Two_tool_rounds_carry_every_reasoning_item_back_in_order_then_text()
    {
      var wire = new WireProvider(CallOne, CallTwo, TextReply);
      var result = Drive(wire);

      Assert.Equal(TurnOutcome.Complete, result.Outcome);
      Assert.StartsWith("**A1", result.Text);
      Assert.Equal(3, wire.Bodies.Count);

      Assert.Equal(new[] { "user" }, Types(wire.Bodies[0]));
      Assert.Equal(new[] { "user", "reasoning", "function_call", "function_call_output" }, Types(wire.Bodies[1]));
      Assert.Equal(new[] { "user", "reasoning", "function_call", "function_call_output", "reasoning", "function_call", "function_call_output" }, Types(wire.Bodies[2]));

      var last = wire.Bodies[2]["input"].AsArray();
      Assert.Equal("ENCRYPTED-CONTENT-REDUCED-2", last[1]["encrypted_content"].GetValue<string>());
      Assert.Equal("rs_0d19c4028af674bd016ae5d01c306887d286f3c5089451c72b", last[1]["id"].GetValue<string>());
      Assert.Equal("ENCRYPTED-CONTENT-REDUCED-5", last[4]["encrypted_content"].GetValue<string>());
      Assert.Equal("call_uqrKMKNWEfQCnmUuWkPASj5m", last[2]["call_id"].GetValue<string>());
      Assert.Equal("fc_0d19c4028af674bd016ae5d01cbbe887d2a009e06260bd9e78", last[2]["id"].GetValue<string>());
      Assert.Equal("call_uqrKMKNWEfQCnmUuWkPASj5m", last[3]["call_id"].GetValue<string>());
      Assert.Equal("{}", last[5]["arguments"].GetValue<string>());
      Assert.Equal("call_zhLVt1yjG8GLyYztyfjFzBos", last[6]["call_id"].GetValue<string>());
      Assert.Contains("get_overview", last[3]["output"].GetValue<string>());
      Assert.All(wire.Bodies, b => Assert.False(b["store"].GetValue<bool>()));
      Assert.Equal(new[] { "medium", "medium", "medium" }, wire.Bodies.Select(b => b["reasoning"]["effort"].GetValue<string>()).ToArray());
    }

    [Fact]
    public void A_truncated_step_ends_the_turn_as_a_truncation()
    {
      var wire = new WireProvider(Incomplete);
      var result = Drive(wire);
      Assert.Equal(TurnOutcome.Truncated, result.Outcome);
      Assert.Single(wire.Bodies);
    }

    // ---- the forced step ---------------------------------------------------------------------------------

    [Fact]
    public void A_forced_step_keeps_its_reasoning_here_and_this_is_a_fact_of_the_connection()
    {
      Assert.True(((ILlmProvider)OpenAi()).ThinksWhileForced);
      foreach (var label in new[] { "Ollama Cloud", OpenAIProvider.OllamaLocalLabel })
        Assert.False(((ILlmProvider)new OpenAIProvider("https://x/v1", null, "m", ProviderCapability.Format, false, label)).ThinksWhileForced);
      Assert.False(((ILlmProvider)new GoogleProvider(null, "gemini-test")).ThinksWhileForced);

      var wire = new WireProvider(TextReply);
      Drive(wire, force: AgentHostTools.RequestMutation);
      Assert.Equal(AgentHostTools.RequestMutation, wire.Seen[0].ForceToolName);
      Assert.True(wire.Seen[0].Thinking);
      Assert.Equal("medium", wire.Seen[0].Effort);
      Assert.Equal("medium", wire.Bodies[0]["reasoning"]["effort"].GetValue<string>());
      Assert.Equal(AgentHostTools.RequestMutation, wire.Bodies[0]["tool_choice"]["name"].GetValue<string>());
    }

    // ---- the effort a chat is sent --------------------------------------------------------------------------

    [Theory]
    [InlineData("OpenAI", "balanced", true)]
    [InlineData("OpenAI", "quality", true)]
    [InlineData("OpenAI", "ultra", true)]
    [InlineData("OpenAI", "economy", false)]
    [InlineData("Anthropic", "balanced", false)]   // its wire does not move
    [InlineData("Google", "ultra", false)]
    [InlineData("OllamaCloud", "quality", false)]
    public void A_pinned_chat_thinks_on_OpenAI_alone(string provider, string profile, bool thinks)
    {
      var pin = new ModelInfo("m", "M", provider, "Strong", 1, 1, 1000, reasoning: true, reasoningKnob: true);
      Assert.Equal(thinks, ChatHeader.PinThinks(pin, provider, profile));
    }

    [Fact]
    public void A_pinned_OpenAI_model_with_no_reasoning_is_not_asked_to_think() =>
      Assert.False(ChatHeader.PinThinks(new ModelInfo("m", "M", "OpenAI", "Strong", 1, 1, 1000), "OpenAI", "quality"));

    [Fact]
    public void A_pin_takes_the_effort_its_profile_gives_and_the_settings_read_the_one_test()
    {
      Assert.Equal("medium", ThinkingWire.EffortForProfile("balanced"));
      Assert.Equal("high", ThinkingWire.EffortForProfile("quality"));
      Assert.Equal("max", ThinkingWire.EffortForProfile("ultra"));
      var settings = File.ReadAllText(RepoFile("Dwight", "Config", "DwightSettings.cs"));
      Assert.Contains("Dwight.Agent.ChatHeader.PinThinks(pin, EffectiveProvider, profile)", settings);
      Assert.Contains("Effort = thinks ? Dwight.Llm.ThinkingWire.EffortForProfile(profile) : null", settings);
    }

    /// <summary>#216 with Brief BT's successor: a chat turn's answer budget (ChatAnswerBudget) stays whole, with the reserve on top, at every profile's effort.</summary>
    [Theory]
    [InlineData("medium", ThinkingBudget.ReserveMedium)]
    [InlineData("high", ThinkingBudget.ReserveHigh)]
    [InlineData("max", ThinkingBudget.ReserveMax)]
    public void The_chat_budget_carries_the_reserve_on_top(string effort, int reserve)
    {
      var budget = ChatAnswerBudget.Turn;
      Assert.Equal(budget + reserve, Body(Turn(effort: effort, budget: budget))["max_output_tokens"].GetValue<int>());
      Assert.Equal(effort, Body(Turn(effort: effort, budget: budget))["reasoning"]["effort"].GetValue<string>());
    }

    // ---- eligibility -------------------------------------------------------------------------------------

    [Fact]
    public void A_model_can_be_pinned_for_its_gate_in_memory()
    {
      var model = new ModelInfo("gate-model", "Gate", "OpenAI", "Strong", 1, 1, 1000);
      Assert.False(model.Agentic);
      model.PinAgenticForGate();
      Assert.True(model.Agentic);
    }

    // ---- the real wire -----------------------------------------------------------------------------------

    private static List<JsonObject> Events(string fixture) =>
      StreamFixtures.Read(fixture).Split('\n').Where(l => l.StartsWith("data: ", StringComparison.Ordinal))
        .Select(l => (JsonObject)JsonNode.Parse(l.Substring(6))).ToList();

    [Fact]
    public void Every_reduced_capture_reads_and_holds_neither_the_prompt_nor_any_real_encrypted_content()
    {
      foreach (var f in StreamFixtures.ResponsesLive)
      {
        var text = StreamFixtures.Read(f);
        Assert.StartsWith(": REDUCED from a live capture", text);
        Assert.DoesNotContain("\"instructions\"", text);
        Assert.DoesNotContain("\"tools\":", text);
        foreach (var ev in Events(f).Where(e => e["item"]?["encrypted_content"] != null))
          Assert.StartsWith("ENCRYPTED-CONTENT-REDUCED-", ev["item"]["encrypted_content"].GetValue<string>());
        Assert.NotNull(Read(text));
      }
    }

    [Fact]
    public void The_real_wire_ends_on_its_terminal_event_with_no_done_sentinel_and_the_reader_passes_over_its_extra_fields()
    {
      foreach (var f in StreamFixtures.ResponsesLive)
      {
        Assert.DoesNotContain("[DONE]", StreamFixtures.Read(f));
        Assert.Equal("response.completed", Events(f).Last()["type"].GetValue<string>());
      }
      var text = Events(TextReply);
      Assert.Contains(text, e => e["type"].GetValue<string>() == "response.content_part.done");
      Assert.Contains(text, e => e["type"].GetValue<string>() == "response.output_text.delta" && e["obfuscation"] != null);
      Assert.Contains(text, e => e["part"]?["logprobs"] != null);
    }

    /// <summary>The added event carries an encrypted_content too, and it is NOT the one to send back: in all 31 reasoning items the gate captured it differs from the done
    /// event's (1228 characters against 3320 on one), as OpenAI's types warn ("may be incomplete while the item is in progress"). The reduction kept them apart, as
    /// different placeholders, so this is a real witness that the reader takes the done event's.</summary>
    [Fact]
    public void The_encrypted_content_carried_back_is_the_done_events_and_not_the_added_events_incomplete_one()
    {
      var events = Events(CallOne);
      var added = events.First(e => e["type"].GetValue<string>() == "response.output_item.added" && e["item"]["type"].GetValue<string>() == "reasoning");
      var done = events.First(e => e["type"].GetValue<string>() == "response.output_item.done" && e["item"]["type"].GetValue<string>() == "reasoning");
      Assert.NotEqual(added["item"]["encrypted_content"].GetValue<string>(), done["item"]["encrypted_content"].GetValue<string>());
      Assert.True(added["item"].AsObject().ContainsKey("content"));
      Assert.True(added["item"].AsObject().ContainsKey("summary"));
      var raw = JsonNode.Parse(Read(StreamFixtures.Read(CallOne)).Blocks[0].RawJson);
      Assert.Equal(done["item"]["encrypted_content"].GetValue<string>(), raw["encrypted_content"].GetValue<string>());
    }

    [Fact]
    public void Three_parallel_calls_come_back_in_order_each_answered_by_its_own_id()
    {
      var r = Read(StreamFixtures.Read(Parallel));
      Assert.Equal(new[] { "get_overview", "get_scaffold_map", "kb_lookup" }, r.ToolUses.Select(c => c.ToolName).ToArray());
      Assert.Equal("{\"types\":[\"Number Slider\",\"Number\",\"Series\"]}", r.ToolUses[2].ToolInputJson);
      Assert.Equal(LlmBlock.ResponseItemKind, r.Blocks[0].Kind);

      var wire = new WireProvider(Parallel, TextReply);
      Drive(wire);
      Assert.Equal(new[] { "user", "reasoning", "function_call", "function_call", "function_call", "function_call_output", "function_call_output", "function_call_output" }, Types(wire.Bodies[1]));
      var input = wire.Bodies[1]["input"].AsArray();
      for (var i = 0; i < 3; i++)
        Assert.Equal(input[2 + i]["call_id"].GetValue<string>(), input[5 + i]["call_id"].GetValue<string>());
    }

    /// <summary>The round trip's whole claim: what the wire's done event said is what the next step sends, field for field, phase and call_id and logprobs included.</summary>
    [Theory]
    [InlineData(CallOne)]
    [InlineData(Parallel)]
    [InlineData(StreamFixtures.ResponsesForced)]
    public void Every_output_item_goes_back_exactly_as_the_wire_sent_it(string fixture)
    {
      var wire = new WireProvider(fixture, TextReply);
      Drive(wire);
      var sent = wire.Bodies[1]["input"].AsArray().Where(i => i["type"]?.GetValue<string>() != "function_call_output" && i["role"]?.GetValue<string>() != "user")
        .Select(i => i.ToJsonString()).ToArray();
      var done = Events(fixture).Where(e => e["type"].GetValue<string>() == "response.output_item.done").Select(e => e["item"].ToJsonString()).ToArray();
      Assert.Equal(done, sent);
    }

    [Fact]
    public void A_named_forced_step_at_medium_came_back_on_the_real_wire_with_its_reasoning()
    {
      var events = Events(StreamFixtures.ResponsesForced);
      var echo = events.Last()["response"];
      Assert.Equal("gpt-6-astra", echo["model"].GetValue<string>());
      Assert.Equal("{\"type\":\"function\",\"name\":\"request_mutation\"}", echo["tool_choice"].ToJsonString());
      Assert.Equal("medium", echo["reasoning"]["effort"].GetValue<string>());
      Assert.False(echo["store"].GetValue<bool>());
      Assert.Equal(130, echo["usage"]["output_tokens_details"]["reasoning_tokens"].GetValue<int>());

      var r = Read(StreamFixtures.Read(StreamFixtures.ResponsesForced));
      Assert.Equal(new[] { LlmBlock.ResponseItemKind, "text", "tool_use" }, r.Blocks.Select(b => b.Kind).ToArray());
      Assert.Equal("request_mutation", r.ToolUses.Single().ToolName);
      Assert.Equal("commentary", JsonNode.Parse(r.Blocks[1].RawJson)["phase"].GetValue<string>());
      Assert.Equal("tool_use", r.StopReason);
    }

    [Fact]
    public void GPT_6_1_Sols_read_answer_reads()
    {
      var r = Read(StreamFixtures.Read(StreamFixtures.ResponsesSolText));
      Assert.StartsWith("The canvas contains one Series component", r.Text);
      Assert.Equal(30, r.Usage.OutputTokens);
    }

    // ---- the catalogue ------------------------------------------------------------------------------------

    [Theory]
    [InlineData("gpt-6.1-sol")]
    [InlineData("gpt-6-astra")]
    public void The_two_models_the_gate_passed_are_agentic_and_say_what_was_measured_and_when(string id)
    {
      var catalogue = JsonNode.Parse(File.ReadAllText(RepoFile("Dwight", "Resources", "models.json")));
      Assert.True(catalogue["catalogueVersion"].GetValue<int>() >= 10);
      var m = catalogue["models"].AsArray().Single(x => x["id"].GetValue<string>() == id);
      Assert.True(m["capabilities"]["agentic"].GetValue<bool>());
      var note = m["speedNote"].GetValue<string>();
      Assert.Contains("MEASURED 2026-10-31", note);
      Assert.Contains("Responses endpoint", note);
      Assert.Contains("read, a build and its Ctrl-Z", note);
      Assert.Contains("named forced step", note);
      Assert.Contains("Agentic: true", note);
    }

    // ---- the trace ----------------------------------------------------------------------------------------

    [Fact]
    public void The_adapter_records_the_trace_as_the_others_do()
    {
      var src = File.ReadAllText(RepoFile("Dwight", "Llm", "OpenAIProvider.cs"));
      Assert.Contains("LlmTrace.RecordRequest(ModelId, body);", src);
      Assert.Contains("LlmTrace.RecordResponse(parsed.Usage, OpenAIResponses.ReasoningItems(parsed), parsed.StopReason);", src);
    }

    [Fact]
    public void Reasoning_items_are_counted_from_the_content_not_the_usage()
    {
      Assert.Equal(1, OpenAIResponses.ReasoningItems(Read(StreamFixtures.Read(CallOne))));
      Assert.Equal(0, OpenAIResponses.ReasoningItems(Read(StreamFixtures.Read(TextReply))));
    }

#if DEBUG
    [Fact]
    public void A_responses_turn_reads_back_from_the_trace_what_was_sent_and_what_the_wire_said()
    {
      LlmTrace.RecordRequest("gpt-test", OpenAIResponses.BuildBody(Turn(effort: "medium"), "gpt-test"));
      var r = Read(StreamFixtures.Read(CallTwo));
      LlmTrace.RecordResponse(r.Usage, OpenAIResponses.ReasoningItems(r), r.StopReason);

      var reading = LlmTrace.LastThinking();
      Assert.Equal("reasoning", reading.Sent);
      Assert.Equal("medium", reading.Effort);
      Assert.Equal(58, reading.Tokens);
      Assert.Equal(1, reading.Blocks);
      Assert.True(reading.ReportedThinkingField);
      Assert.Contains("\"reasoning_tokens\":58", reading.RawUsage);
      Assert.Contains("thinking SENT:    reasoning  (effort medium)", LlmTrace.LastExchangeReport());
    }
#endif

    private static string RepoFile(params string[] parts)
    {
      var dir = new DirectoryInfo(AppContext.BaseDirectory);
      while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Dwight.sln"))) dir = dir.Parent;
      Assert.NotNull(dir);
      return Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
    }
  }
}
