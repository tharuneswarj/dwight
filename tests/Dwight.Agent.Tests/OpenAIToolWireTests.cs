using System;
using System.Linq;
using System.Text.Json.Nodes;
using Dwight.Agent;
using Dwight.Llm;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// Phase 10, Brief H stage 1: the OpenAI-shaped adapter (OpenAI, Ollama local, Ollama Cloud) carries the agent's tools.
  /// It mirrors what the Anthropic wire is held to: ONE tool definition source, the assistant's calls replayed verbatim,
  /// a reader that assembles fragments and refuses damage by event index in Dwight's words, and the flag pinned to the body.
  ///
  /// <para><b>What these witness.</b> The three tool-call streams are HAND-MADE from the documented shape (their file names
  /// and header comments say so): they prove the reader reads the documentation, not what a live provider sends. The
  /// sitting replaces each with a capture.</para>
  /// </summary>
  [Collection(StreamFixtures.CaptureWindow)]
  public class OpenAIToolWireTests
  {
    private const string Dwight = "Dwight could not read the model's reply in full, so it did not act on it: ";

    private static LlmResponse Read(string text, string label = "OpenAI") =>
      OpenAIProvider.ReadStream(StreamFixtures.Reader(text), default, label);

    private static OpenAIProvider Provider(ProviderCapability cap = ProviderCapability.StrictSchema, bool completionTokens = true) =>
      new OpenAIProvider("https://example.invalid/v1", null, "model-test", cap, completionTokens, "OpenAI");

    private static JsonObject Body(OpenAIProvider p, LlmRequest r) => (JsonObject)JsonNode.Parse(p.BuildBody(r, structured: false));

    /// <summary>The agent's real tool list, mapped exactly as <c>AgentLoop.Run</c> maps it.</summary>
    private static LlmRequest WithTheAgentsTools() => new LlmRequest
    {
      System = "s",
      Messages = { LlmMessage.User("What is on the canvas?") },
      Tools = AgentToolCatalog.All()
        .Select(t => new LlmTool { Name = t.Name, Description = t.Description, InputSchemaJson = t.InputSchemaJson }).ToList(),
    };

    // ---- the request body --------------------------------------------------------------------------------

    /// <summary>ONE DEFINITION SOURCE: the tools array is the catalogue's descriptors, in order, name for name, with the
    /// description and the schema untouched - not a second list.</summary>
    [Fact]
    public void The_body_carries_every_catalogue_tool_in_the_function_shape_unchanged()
    {
      var body = Body(Provider(), WithTheAgentsTools());
      var tools = body["tools"].AsArray();
      var catalogue = AgentToolCatalog.All();

      Assert.Equal(catalogue.Count, tools.Count);
      for (var i = 0; i < catalogue.Count; i++)
      {
        Assert.Equal("function", tools[i]["type"].GetValue<string>());
        var fn = tools[i]["function"];
        Assert.Equal(catalogue[i].Name, fn["name"].GetValue<string>());
        Assert.Equal(catalogue[i].Description, fn["description"].GetValue<string>());
        Assert.Equal(JsonNode.Parse(catalogue[i].InputSchemaJson).ToJsonString(), fn["parameters"].ToJsonString());
      }
    }

    /// <summary>tool_choice is LEFT TO auto by being absent (the loop decides by reading the reply; Ollama's endpoint does
    /// not accept the field at all).</summary>
    [Theory]
    [InlineData("StrictSchema", true)]
    [InlineData("Format", false)]
    [InlineData("PromptOnly", false)]
    public void The_body_never_sets_tool_choice_and_sends_the_answer_budget_under_the_key_the_adapter_already_used(string capability, bool completionTokens)
    {
      var cap = (ProviderCapability)Enum.Parse(typeof(ProviderCapability), capability);
      var req = WithTheAgentsTools();
      req.MaxTokens = 1234;
      var body = Body(Provider(cap, completionTokens), req);

      Assert.False(body.ContainsKey("tool_choice"));
      Assert.Equal(1234, body[completionTokens ? "max_completion_tokens" : "max_tokens"].GetValue<int>());
      Assert.False(body.ContainsKey(completionTokens ? "max_tokens" : "max_completion_tokens"));
      // The thinking wire is Anthropic's: nothing of it is sent here.
      Assert.False(body.ContainsKey("thinking"));
    }

    /// <summary>OpenAI refuses function tools with reasoning on (HTTP 400), so a tools body on the OpenAI instance says "none";
    /// Ollama instances of the same adapter send no such key; a body without tools sends none on OpenAI either.</summary>
    [Fact]
    public void The_chat_completions_body_never_sets_a_reasoning_effort()
    {
      // Brief BV: this was "none" on the OpenAI instance with tools; those turns moved to the Responses endpoint.
      var openAi = Body(Provider(), WithTheAgentsTools());
      Assert.False(openAi.ContainsKey("reasoning_effort"));

      var ollama = Body(new OpenAIProvider("https://example.invalid/v1", null, "m", ProviderCapability.Format, false, "Ollama"), WithTheAgentsTools());
      Assert.False(ollama.ContainsKey("reasoning_effort"));

      var noTools = Body(Provider(), new LlmRequest { System = "s", Messages = { LlmMessage.User("hi") } });
      Assert.False(noTools.ContainsKey("reasoning_effort"));
    }

    /// <summary>U53 (#216 at this adapter): a reasoning model on an Ollama instance gets the thinking reserve ON TOP of the
    /// answer budget (GLM-5.3 cloud spent 4000 of 4000 tokens thinking and wrote nothing); a non-reasoning Ollama model
    /// keeps the bare budget, and the OpenAI instance is unchanged whatever the flag says.</summary>
    [Theory]
    [InlineData("Ollama Cloud", true, 4000 + ThinkingBudget.ReserveHigh)]
    [InlineData("Ollama (local)", true, 4000 + ThinkingBudget.ReserveHigh)]
    [InlineData("Ollama Cloud", false, 4000)]
    [InlineData("OpenAI", true, 4000)]
    [InlineData("OpenAI", false, 4000)]
    public void A_reasoning_Ollama_model_gets_the_thinking_reserve_on_top_of_the_answer_budget(string label, bool reasoning, int expected)
    {
      var isOpenAi = label == "OpenAI";
      var p = new OpenAIProvider("https://example.invalid/v1", null, "m", ProviderCapability.Format, isOpenAi, label, reasoningModel: reasoning);
      var req = WithTheAgentsTools();
      req.MaxTokens = 4000;

      Assert.Equal(20000, 4000 + ThinkingBudget.ReserveHigh);
      Assert.Equal(expected, Body(p, req)[isOpenAi ? "max_completion_tokens" : "max_tokens"].GetValue<int>());
    }

    /// <summary>A request with no tools (every verb) puts no `tools` key on the wire.</summary>
    [Fact]
    public void A_request_without_tools_has_no_tools_key()
    {
      var body = Body(Provider(), new LlmRequest { System = "s", Messages = { LlmMessage.User("hi") } });
      Assert.False(body.ContainsKey("tools"));
    }

    /// <summary>THE VERBATIM REPLAY: the assistant's turn goes back with its calls as tool_calls, the arguments string
    /// byte for byte (odd spacing and all), and each result as a `tool` message carrying its tool_call_id.</summary>
    [Fact]
    public void An_assistant_turn_with_calls_is_replayed_verbatim_and_each_result_answers_by_its_id()
    {
      var odd = "{ \"id\" :  2 }";   // spacing a re-serialiser would normalise
      var req = new LlmRequest
      {
        System = "s",
        Messages =
        {
          LlmMessage.User("look"),
          LlmMessage.Assistant(new System.Collections.Generic.List<LlmBlock>
          {
            LlmBlock.TextBlock("Reading."),
            LlmBlock.ToolUse("call_a", "get_region", odd),
            LlmBlock.ToolUse("call_b", "get_overview", "{}"),
          }),
          new LlmMessage { Role = LlmRole.User, Blocks =
          {
            LlmBlock.ToolResult("call_a", "{\"ok\":true}"),
            LlmBlock.ToolResult("call_b", "{\"ok\":true,\"n\":1}"),
          } },
        },
      };

      var messages = Body(Provider(), req)["messages"].AsArray();

      Assert.Equal(new[] { "system", "user", "assistant", "tool", "tool" }, messages.Select(m => m["role"].GetValue<string>()));
      var assistant = messages[2];
      Assert.Equal("Reading.", assistant["content"].GetValue<string>());
      var calls = assistant["tool_calls"].AsArray();
      Assert.Equal(2, calls.Count);
      Assert.Equal("call_a", calls[0]["id"].GetValue<string>());
      Assert.Equal("function", calls[0]["type"].GetValue<string>());
      Assert.Equal("get_region", calls[0]["function"]["name"].GetValue<string>());
      Assert.Equal(odd, calls[0]["function"]["arguments"].GetValue<string>());   // a STRING, not an object, and untouched
      Assert.Equal("call_a", messages[3]["tool_call_id"].GetValue<string>());
      Assert.Equal("{\"ok\":true}", messages[3]["content"].GetValue<string>());
      Assert.Equal("call_b", messages[4]["tool_call_id"].GetValue<string>());
    }

    [Fact]
    public void An_assistant_turn_that_only_called_tools_has_null_content_not_an_empty_string()
    {
      var req = new LlmRequest
      {
        Messages =
        {
          LlmMessage.User("look"),
          LlmMessage.Assistant(new System.Collections.Generic.List<LlmBlock> { LlmBlock.ToolUse("call_a", "get_overview", "{}") }),
          new LlmMessage { Role = LlmRole.User, Blocks = { LlmBlock.ToolResult("call_a", "r") } },
        },
      };
      var assistant = Body(Provider(), req)["messages"].AsArray()[1];
      Assert.Null(assistant["content"]);
      Assert.NotNull(assistant["tool_calls"]);
    }

    /// <summary>A guard that repairs is a guard that hides (#297): a call whose arguments were never recorded is refused,
    /// not replayed as {}.</summary>
    [Fact]
    public void A_recorded_call_with_no_arguments_is_refused_not_replayed_as_empty()
    {
      var req = new LlmRequest
      {
        Messages =
        {
          LlmMessage.User("look"),
          LlmMessage.Assistant(new System.Collections.Generic.List<LlmBlock> { LlmBlock.ToolUse("call_a", "get_region", null) }),
        },
      };
      var ex = Assert.Throws<LlmException>(() => Provider().BuildBody(req, structured: false));
      Assert.Contains("get_region", ex.Message);
      Assert.Contains("That step was not sent.", ex.Message);
    }

    /// <summary>The repair round-trip of the structured path keeps its shape: text-only messages are unchanged.</summary>
    [Fact]
    public void Plain_text_turns_are_written_as_they_always_were()
    {
      var req = new LlmRequest
      {
        System = "sys",
        Messages =
        {
          LlmMessage.User("hi"),
          LlmMessage.Assistant(new System.Collections.Generic.List<LlmBlock> { LlmBlock.TextBlock("hello") }),
          LlmMessage.User("again"),
        },
      };
      var messages = Body(Provider(), req)["messages"].AsArray();
      Assert.Equal(new[] { "system", "user", "assistant", "user" }, messages.Select(m => m["role"].GetValue<string>()));
      Assert.Equal("hello", messages[2]["content"].GetValue<string>());
      Assert.False(messages[2].AsObject().ContainsKey("tool_calls"));
    }

    // ---- the reader: hand-made tool-call streams ----------------------------------------------------------

    [Fact]
    public void Live_OpenAI_call_joins_its_argument_fragments_into_whole_arguments()
    {
      var r = Read(StreamFixtures.Read(StreamFixtures.OpenAIToolCall));

      var call = r.Blocks.Single();   // no text, so no empty text block beside the call
      Assert.Equal("tool_use", call.Kind);
      Assert.Equal("call_m3zjtTSXVUghe5v40Sna28P1", call.ToolUseId);
      Assert.Equal("get_region", call.ToolName);
      Assert.Equal("{\"id\":1}", call.ToolInputJson);
      Assert.Equal("tool_use", r.StopReason);   // the loop reads Anthropic's word; the adapter translates
      Assert.True(r.WantsTool);
      Assert.Equal(9210, r.Usage.TotalInputTokens);   // Brief BT: the prompt whole; ForcedCardTests pins its split
      Assert.Equal(17, r.Usage.OutputTokens);
    }

    [Fact]
    public void HandMade_parallel_calls_keep_their_text_and_their_own_fragments_apart_by_index()
    {
      var r = Read(StreamFixtures.Read(StreamFixtures.OpenAIParallelCallsHandMade));

      Assert.Equal(new[] { "text", "tool_use", "tool_use" }, r.Blocks.Select(b => b.Kind));
      Assert.Equal("I will read the overview and one region.", r.Blocks[0].Text);
      Assert.Equal(("call_handmade_a", "get_overview", "{}"), (r.Blocks[1].ToolUseId, r.Blocks[1].ToolName, r.Blocks[1].ToolInputJson));
      Assert.Equal(("call_handmade_b", "get_region", "{\"id\": 3}"), (r.Blocks[2].ToolUseId, r.Blocks[2].ToolName, r.Blocks[2].ToolInputJson));
    }

    /// <summary>Ollama Cloud (GLM-5.3, 25 October) sends the call whole in one chunk, with an id and an index, and
    /// finishes with "tool_calls".</summary>
    [Fact]
    public void Live_Ollama_call_arrives_whole_with_its_id_and_index()
    {
      var r = Read(StreamFixtures.Read(StreamFixtures.OllamaToolCall), "Ollama Cloud");

      var call = r.Blocks.Single();
      Assert.Equal(("call_d3j5jf59", "get_component", "{\"id\":4}"), (call.ToolUseId, call.ToolName, call.ToolInputJson));
      Assert.Equal("tool_use", r.StopReason);
      Assert.Equal(10922, r.Usage.TotalInputTokens);   // Brief BT: the prompt whole; ForcedCardTests pins its split
      Assert.Equal(92, r.Usage.OutputTokens);
    }

    /// <summary>Some Ollama versions say "stop" with a call in hand: the call is what counts, not the finish word. The
    /// live stream with its finish word changed (the live gate run only ever saw "tool_calls").</summary>
    [Fact]
    public void A_stop_finish_does_not_hide_a_live_Ollama_call()
    {
      var text = StreamFixtures.Read(StreamFixtures.OllamaToolCall).Replace("\"finish_reason\":\"tool_calls\"", "\"finish_reason\":\"stop\"");

      var r = Read(text, "Ollama Cloud");

      Assert.Equal("get_component", r.Blocks.Single().ToolName);
      Assert.Equal("tool_use", r.StopReason);
    }

    /// <summary>The captured text-only streams still read as before: the finish word is the provider's own.</summary>
    [Fact]
    public void A_text_only_reply_keeps_its_finish_reason_and_its_one_text_block()
    {
      var r = Read(StreamFixtures.Read(StreamFixtures.OpenAI07));
      Assert.Equal("stop", r.StopReason);
      Assert.Equal("text", r.Blocks.Single().Kind);
      Assert.False(r.WantsTool);
    }

    // ---- the reader: damage is refused by event index, in Dwight's words ----------------------------------

    [Fact]
    public void A_fragment_for_a_call_whose_first_fragment_was_lost_is_refused_by_index()
    {
      var text = StreamFixtures.Read(StreamFixtures.OpenAIToolCall);
      var blocks = StreamFixtures.Blocks(text);
      // Lose the event that STARTS the call (id and name): event 1 of the live stream.
      blocks.RemoveAt(0);

      var ex = Assert.Throws<LlmException>(() => Read(StreamFixtures.Join(blocks)));
      Assert.StartsWith(Dwight + "event 1 (line ", ex.Message);
      Assert.Contains("continues tool call 0, which never started", ex.Message);
    }

    [Fact]
    public void Arguments_cut_before_they_close_are_refused_not_dispatched_short()
    {
      var text = StreamFixtures.Read(StreamFixtures.OpenAIToolCall);
      var blocks = StreamFixtures.Blocks(text);
      var at = StreamFixtures.IndexOf(text, "\"arguments\":\"1\"");
      blocks.RemoveAt(at - 1);   // the value fragment never arrived: {"id":}

      var ex = Assert.Throws<LlmException>(() => Read(StreamFixtures.Join(blocks)));
      Assert.StartsWith(Dwight + "event ", ex.Message);
      Assert.Contains("the call to \"get_region\"", ex.Message);
      Assert.Contains("do not join into valid JSON", ex.Message);
      Assert.EndsWith("Nothing from this reply was used.", ex.Message);
    }

    [Fact]
    public void A_call_that_sent_no_arguments_at_all_is_refused_never_defaulted()
    {
      var text = StreamFixtures.Read(StreamFixtures.OllamaToolCall).Replace("\"arguments\":\"{\\\"id\\\":4}\"", "\"arguments\":\"\"");

      var ex = Assert.Throws<LlmException>(() => Read(text, "Ollama Cloud"));
      Assert.Contains("the call to \"get_component\" with no arguments sent", ex.Message);
    }

    [Fact]
    public void Arguments_that_join_into_something_other_than_an_object_are_refused()
    {
      var text = StreamFixtures.Read(StreamFixtures.OllamaToolCall).Replace("\"arguments\":\"{\\\"id\\\":4}\"", "\"arguments\":\"[1]\"");

      var ex = Assert.Throws<LlmException>(() => Read(text, "Ollama Cloud"));
      Assert.Contains("whose arguments are not a set of arguments", ex.Message);
    }

    [Fact]
    public void A_call_started_twice_under_one_index_is_refused_by_index()
    {
      var text = StreamFixtures.Read(StreamFixtures.OpenAIToolCall);
      var k = StreamFixtures.IndexOf(text, "\"arguments\":\"id\"");
      var damaged = StreamFixtures.Damage(text, k, b => b.Replace("\"tool_calls\":[{\"index\":0,", "\"tool_calls\":[{\"index\":0,\"id\":\"call_other\","));

      var ex = Assert.Throws<LlmException>(() => Read(damaged));
      Assert.StartsWith(Dwight + "event " + k + " (line ", ex.Message);
      Assert.Contains("starts tool call 0 a second time", ex.Message);
    }

    [Fact]
    public void A_truncated_chunk_inside_a_call_is_refused_by_index()
    {
      var text = StreamFixtures.Read(StreamFixtures.OpenAIToolCall);
      var k = StreamFixtures.IndexOf(text, "\"arguments\":\"id\"");
      var damaged = StreamFixtures.Damage(text, k, b => b.Substring(0, b.IndexOf("\"arguments\"")));

      var ex = Assert.Throws<LlmException>(() => Read(damaged));
      Assert.StartsWith(Dwight + "event " + k + " (line ", ex.Message);
      Assert.Contains("not valid JSON", ex.Message);
    }

    [Fact]
    public void A_reply_cut_short_before_its_finish_reason_is_refused_even_with_a_call_in_hand()
    {
      var text = StreamFixtures.Read(StreamFixtures.OpenAIToolCall);
      var fin = StreamFixtures.IndexOf(text, "\"finish_reason\":\"tool_calls\"");
      var cut = StreamFixtures.Join(StreamFixtures.Blocks(text).Take(fin - 1));

      var ex = Assert.Throws<LlmException>(() => Read(cut));
      Assert.Contains("no finish_reason arrived", ex.Message);
    }

    [Fact]
    public void A_finish_reason_that_promises_calls_none_of_which_arrived_is_refused()
    {
      var text = StreamFixtures.Read(StreamFixtures.OpenAI07).Replace("\"finish_reason\":\"stop\"", "\"finish_reason\":\"tool_calls\"");

      var ex = Assert.Throws<LlmException>(() => Read(text));
      Assert.Contains("no tool call arrived", ex.Message);
    }

    /// <summary>A reply cut at the token limit is Truncated, and the half-built call is NOT dispatched: the turn ends on
    /// the stated outcome, not on an action built from half a call.</summary>
    [Fact]
    public void A_reply_cut_at_the_token_limit_reports_max_tokens_and_dispatches_no_call()
    {
      var text = StreamFixtures.Read(StreamFixtures.OpenAIToolCall).Replace("\"finish_reason\":\"tool_calls\"", "\"finish_reason\":\"length\"");
      var blocks = StreamFixtures.Blocks(text);
      blocks.RemoveAt(StreamFixtures.IndexOf(text, "\"arguments\":\"1\"") - 1);   // ... and the arguments were cut mid-way

      var r = Read(StreamFixtures.Join(blocks));

      Assert.Equal("max_tokens", r.StopReason);
      Assert.Empty(r.ToolUses);
      Assert.Equal(AgentLoopCore.PostResponse(r.StopReason, r.ToolUses.Count > 0), TurnOutcome.Truncated);
    }

    // ---- the loop reads what the adapter says ---------------------------------------------------------------

    [Fact]
    public void A_whole_call_makes_the_loop_carry_on_and_a_plain_stop_ends_the_turn()
    {
      var call = Read(StreamFixtures.Read(StreamFixtures.OpenAIToolCall));
      var plain = Read(StreamFixtures.Read(StreamFixtures.OpenAI07));

      Assert.Null(AgentLoopCore.PostResponse(call.StopReason, call.ToolUses.Count > 0));
      Assert.Equal(TurnOutcome.Complete, AgentLoopCore.PostResponse(plain.StopReason, plain.ToolUses.Count > 0));
    }

    /// <summary>The assembled call replays into the next request exactly as it was received: read -> echo -> same string.</summary>
    [Fact]
    public void What_the_reader_assembles_is_what_the_next_body_replays()
    {
      var r = Read(StreamFixtures.Read(StreamFixtures.OpenAIToolCall));
      var req = new LlmRequest
      {
        Messages =
        {
          LlmMessage.User("look"),
          LlmMessage.Assistant(r.Blocks),
          new LlmMessage { Role = LlmRole.User, Blocks = { LlmBlock.ToolResult(r.ToolUses[0].ToolUseId, "{}") } },
        },
      };
      Assert.Null(AgentLoopCore.CheckTranscript(req.Messages));

      var calls = Body(Provider(), req)["messages"].AsArray()[1]["tool_calls"].AsArray();
      Assert.Equal("{\"id\":1}", calls[0]["function"]["arguments"].GetValue<string>());
    }
  }
}
