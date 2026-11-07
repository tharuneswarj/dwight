using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Dwight.Agent;
using Dwight.Llm;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// Phase 10, Brief H stage 2: the Google adapter carries the agent's tools through Gemini's function calling:
  /// <c>tools:[{functionDeclarations}]</c> from the same catalogue, the <c>functionCall</c> part of a candidate read back
  /// into a whole call, and the <c>functionResponse</c> part the next request carries.
  ///
  /// <para><b>What these witness.</b> The two tool-call streams are HAND-MADE from the documented generateContent shape
  /// (names and header comments say so). They prove the reader reads the documentation, not what Gemini sends live.</para>
  /// </summary>
  [Collection(StreamFixtures.CaptureWindow)]
  public class GoogleToolWireTests
  {
    private const string Dwight = "Dwight could not read the model's reply in full, so it did not act on it: ";

    private static LlmResponse Read(string text) => GoogleProvider.ReadStream(StreamFixtures.Reader(text), default);

    private static JsonObject Body(LlmRequest r) =>
      (JsonObject)JsonNode.Parse(new GoogleProvider(null, "gemini-test").BuildBody(r, structured: false));

    private static LlmRequest WithTheAgentsTools() => new LlmRequest
    {
      System = "s",
      Messages = { LlmMessage.User("What is on the canvas?") },
      Tools = AgentToolCatalog.All()
        .Select(t => new LlmTool { Name = t.Name, Description = t.Description, InputSchemaJson = t.InputSchemaJson }).ToList(),
    };

    // ---- the request body --------------------------------------------------------------------------------

    /// <summary>ONE DEFINITION SOURCE: every catalogue tool, in order, by name and description; the mode is AUTO.</summary>
    [Fact]
    public void The_body_declares_every_catalogue_tool_once_and_leaves_the_mode_to_AUTO()
    {
      var body = Body(WithTheAgentsTools());
      var decls = body["tools"].AsArray().Single()["functionDeclarations"].AsArray();
      var catalogue = AgentToolCatalog.All();

      Assert.Equal(catalogue.Count, decls.Count);
      for (var i = 0; i < catalogue.Count; i++)
      {
        Assert.Equal(catalogue[i].Name, decls[i]["name"].GetValue<string>());
        Assert.Equal(catalogue[i].Description, decls[i]["description"].GetValue<string>());
      }
      Assert.Equal("AUTO", body["toolConfig"]["functionCallingConfig"]["mode"].GetValue<string>());
    }

    /// <summary>The OpenAPI subset: no JSON-Schema keyword outside it reaches Gemini, a tool with no parameters sends
    /// none (an empty OBJECT is rejected), and a tool WITH parameters keeps its names, types and required list.</summary>
    [Fact]
    public void Parameters_are_reduced_to_the_OpenAPI_subset_and_omitted_for_a_tool_that_takes_none()
    {
      var body = Body(WithTheAgentsTools());
      var decls = body["tools"].AsArray().Single()["functionDeclarations"].AsArray();

      var overview = decls.Single(d => d["name"].GetValue<string>() == AgentToolCatalog.GetOverview);
      Assert.False(overview.AsObject().ContainsKey("parameters"));

      var region = decls.Single(d => d["name"].GetValue<string>() == AgentToolCatalog.GetRegion);
      var parameters = region["parameters"];
      Assert.Equal("object", parameters["type"].GetValue<string>());
      Assert.Equal("integer", parameters["properties"]["id"]["type"].GetValue<string>());
      Assert.Equal("id", parameters["required"].AsArray().Single().GetValue<string>());

      // Walk every parameters object: only subset keywords anywhere in the tree.
      var allowed = new HashSet<string> { "type", "description", "format", "nullable", "enum", "required", "properties", "items" };
      void Walk(JsonNode n, bool propertyMap)
      {
        if (!(n is JsonObject o)) return;
        foreach (var kv in o)
        {
          if (propertyMap) { Walk(kv.Value, false); continue; }
          Assert.Contains(kv.Key, allowed);
          if (kv.Key == "properties") Walk(kv.Value, true);
          else if (kv.Key == "items") Walk(kv.Value, false);
        }
      }
      foreach (var d in decls)
        if (d["parameters"] != null) Walk(d["parameters"], false);
    }

    [Fact]
    public void A_request_without_tools_has_no_tools_or_toolConfig()
    {
      var body = Body(new LlmRequest { System = "s", Messages = { LlmMessage.User("hi") } });
      Assert.False(body.ContainsKey("tools"));
      Assert.False(body.ContainsKey("toolConfig"));
    }

    /// <summary>The model's call goes back as a functionCall part with the parsed args and its thoughtSignature beside it;
    /// each result goes back as a functionResponse part that names the call (the name is looked up from the transcript).</summary>
    [Fact]
    public void The_models_calls_are_replayed_with_their_signature_and_each_result_names_its_call()
    {
      var calls = new List<LlmBlock>
      {
        LlmBlock.TextBlock("Reading."),
        new LlmBlock { Kind = "tool_use", ToolUseId = "call-a", ToolName = "get_region", ToolInputJson = "{\"id\": 2}", Signature = "c2ln" },
        LlmBlock.ToolUse("call-b", "get_overview", "{}"),
      };
      var req = new LlmRequest
      {
        Messages =
        {
          LlmMessage.User("look"),
          LlmMessage.Assistant(calls),
          new LlmMessage { Role = LlmRole.User, Blocks = { LlmBlock.ToolResult("call-a", "{\"ok\":true}"), LlmBlock.ToolResult("call-b", "r2") } },
        },
      };

      var contents = Body(req)["contents"].AsArray();

      Assert.Equal(new[] { "user", "model", "user" }, contents.Select(c => c["role"].GetValue<string>()));
      var model = contents[1]["parts"].AsArray();
      Assert.Equal("Reading.", model[0]["text"].GetValue<string>());
      Assert.Equal("get_region", model[1]["functionCall"]["name"].GetValue<string>());
      Assert.Equal(2, model[1]["functionCall"]["args"]["id"].GetValue<int>());
      Assert.Equal("call-a", model[1]["functionCall"]["id"].GetValue<string>());
      Assert.Equal("c2ln", model[1]["thoughtSignature"].GetValue<string>());
      Assert.Null(model[2]["thoughtSignature"]);

      var results = contents[2]["parts"].AsArray();
      Assert.Equal("get_region", results[0]["functionResponse"]["name"].GetValue<string>());
      Assert.Equal("call-a", results[0]["functionResponse"]["id"].GetValue<string>());
      Assert.Equal("{\"ok\":true}", results[0]["functionResponse"]["response"]["result"].GetValue<string>());
      Assert.Equal("get_overview", results[1]["functionResponse"]["name"].GetValue<string>());
    }

    /// <summary>An id the PLUGIN made up (the model sent none) is a pairing handle only: it never goes back to Google.</summary>
    [Fact]
    public void An_id_the_plugin_generated_is_not_written_back_to_the_provider()
    {
      var id = GoogleProvider.GeneratedIdPrefix + "abc";
      var req = new LlmRequest
      {
        Messages =
        {
          LlmMessage.User("look"),
          LlmMessage.Assistant(new List<LlmBlock> { LlmBlock.ToolUse(id, "get_overview", "{}") }),
          new LlmMessage { Role = LlmRole.User, Blocks = { LlmBlock.ToolResult(id, "r") } },
        },
      };
      var contents = Body(req)["contents"].AsArray();
      Assert.False(contents[1]["parts"].AsArray()[0]["functionCall"].AsObject().ContainsKey("id"));
      Assert.False(contents[2]["parts"].AsArray()[0]["functionResponse"].AsObject().ContainsKey("id"));
    }

    [Fact]
    public void A_recorded_call_with_no_arguments_and_a_result_for_an_unknown_call_are_both_refused()
    {
      var noArgs = new LlmRequest
      {
        Messages = { LlmMessage.User("x"), LlmMessage.Assistant(new List<LlmBlock> { LlmBlock.ToolUse("a", "get_region", null) }) },
      };
      Assert.Contains("That step was not sent.", Assert.Throws<LlmException>(() => Body(noArgs)).Message);

      var orphan = new LlmRequest
      {
        Messages = { LlmMessage.User("x"), new LlmMessage { Role = LlmRole.User, Blocks = { LlmBlock.ToolResult("nope", "r") } } },
      };
      Assert.Contains("is not in the conversation", Assert.Throws<LlmException>(() => Body(orphan)).Message);
    }

    [Fact]
    public void Plain_text_turns_are_written_as_they_always_were()
    {
      var req = new LlmRequest
      {
        Messages = { LlmMessage.User("hi"), LlmMessage.Assistant(new List<LlmBlock> { LlmBlock.TextBlock("hello") }), LlmMessage.User("again") },
      };
      var contents = Body(req)["contents"].AsArray();
      Assert.Equal(new[] { "user", "model", "user" }, contents.Select(c => c["role"].GetValue<string>()));
      Assert.Equal("hello", contents[1]["parts"].AsArray().Single()["text"].GetValue<string>());
    }

    // ---- the reader: the live stream (25 October gate run) and the hand-made parallel one ------------------

    [Fact]
    public void Live_text_parts_then_a_call_part_read_as_text_and_one_whole_call_with_its_id_and_signature()
    {
      var r = Read(StreamFixtures.Read(StreamFixtures.GoogleToolCall));

      Assert.Equal(new[] { "text", "tool_use" }, r.Blocks.Select(b => b.Kind));
      Assert.Equal("I have prepared a build proposal to place an unattached Number Slider named **radius** (range 0.0 to 10.0, initial value 5.0) onto the canvas.", r.Blocks[0].Text);
      Assert.Equal("request_mutation", r.Blocks[1].ToolName);
      Assert.Equal("{\"why\":\"Place an unattached Number Slider named \\u0027radius\\u0027 on the canvas.\",\"kind\":\"build\",\"place\":{\"wires\":[],\"components\":[{\"ref\":\"radius_slider\",\"nick\":\"radius\",\"type\":\"Number Slider\",\"state\":{\"max\":10,\"min\":0,\"value\":5,\"rounding\":\"float\"}}]}}", r.Blocks[1].ToolInputJson);
      Assert.Equal("call_1488139", r.Blocks[1].ToolUseId);   // Gemini 3.8 Flash sent an id on every call
      Assert.StartsWith("EtEQCs4QAWkUfRMaZ7yOyeIEJZ25sHuZpolotq0f", r.Blocks[1].Signature);
      Assert.Equal(2844, r.Blocks[1].Signature.Length);
      Assert.Equal("tool_use", r.StopReason);   // Gemini said STOP; the call in hand is what counts
      Assert.Equal(10685, r.Usage.InputTokens);
      Assert.Equal(127, r.Usage.OutputTokens);
    }

    [Fact]
    public void Live_a_call_with_no_arguments_reads_as_an_empty_object_and_keeps_its_id()
    {
      var r = Read(StreamFixtures.Read(StreamFixtures.GoogleLive04));

      var call = r.Blocks.Single();
      Assert.Equal(("call_2394164", "get_overview", "{}"), (call.ToolUseId, call.ToolName, call.ToolInputJson));
      Assert.Equal("tool_use", r.StopReason);
    }

    [Fact]
    public void HandMade_parallel_calls_one_without_arguments_one_with_an_id()
    {
      var r = Read(StreamFixtures.Read(StreamFixtures.GoogleParallelCallsHandMade));

      var calls = r.ToolUses;
      Assert.Equal(2, calls.Count);
      Assert.Equal(("get_overview", "{}"), (calls[0].ToolName, calls[0].ToolInputJson));
      Assert.Equal(("call-b", "get_component", "{\"id\":3}"), (calls[1].ToolUseId, calls[1].ToolName, calls[1].ToolInputJson));
      Assert.NotEqual(calls[0].ToolUseId, calls[1].ToolUseId);
      Assert.Equal(new[] { "tool_use", "tool_use" }, r.Blocks.Select(b => b.Kind));   // no empty text block beside calls
    }

    /// <summary>Two steps' generated ids never collide, or a later result would be named for an earlier call.</summary>
    [Fact]
    public void Generated_ids_are_unique_across_replies()
    {
      var a = Read(StreamFixtures.Read(StreamFixtures.GoogleParallelCallsHandMade)).ToolUses[0].ToolUseId;
      var b = Read(StreamFixtures.Read(StreamFixtures.GoogleParallelCallsHandMade)).ToolUses[0].ToolUseId;
      Assert.NotEqual(a, b);
    }

    [Fact]
    public void A_thought_part_is_never_part_of_the_answer()
    {
      var text = "data: {\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"pondering\",\"thought\":true},{\"text\":\"Answer.\"}],\"role\":\"model\"},\"finishReason\":\"STOP\"}]}\n\n";
      Assert.Equal("Answer.", Read(text).Blocks.Single().Text);
    }

    // ---- damage and outcomes ------------------------------------------------------------------------------

    [Fact]
    public void A_call_that_names_no_tool_is_refused_by_index()
    {
      var text = StreamFixtures.Read(StreamFixtures.GoogleLive05);
      var k = StreamFixtures.IndexOf(text, "\"functionCall\":");
      var damaged = StreamFixtures.Damage(text, k, b => b.Replace("\"name\": \"get_region\",", ""));

      var ex = Assert.Throws<LlmException>(() => Read(damaged));
      Assert.StartsWith(Dwight + "event " + k + " (line ", ex.Message);
      Assert.Contains("names no tool", ex.Message);
    }

    [Fact]
    public void Arguments_that_are_not_an_object_are_refused_by_index()
    {
      var text = StreamFixtures.Read(StreamFixtures.GoogleLive05);
      var k = StreamFixtures.IndexOf(text, "\"functionCall\":");
      var damaged = StreamFixtures.Damage(text, k, b => b.Replace("\"args\": {\"id\": 0}", "\"args\": [0]"));

      var ex = Assert.Throws<LlmException>(() => Read(damaged));
      Assert.StartsWith(Dwight + "event " + k + " (line ", ex.Message);
      Assert.Contains("\"get_region\" whose arguments are not a set of arguments", ex.Message);
    }

    [Fact]
    public void A_truncated_event_carrying_a_call_is_refused_by_index()
    {
      var text = StreamFixtures.Read(StreamFixtures.GoogleLive05);
      var k = StreamFixtures.IndexOf(text, "\"functionCall\":");
      var damaged = StreamFixtures.Damage(text, k, b => b.Substring(0, b.IndexOf("\"args\"")));

      var ex = Assert.Throws<LlmException>(() => Read(damaged));
      Assert.StartsWith(Dwight + "event " + k + " (line ", ex.Message);
      Assert.Contains("not valid JSON", ex.Message);
    }

    [Fact]
    public void A_reply_cut_short_before_its_finish_reason_is_refused_even_with_a_call_in_hand()
    {
      var text = StreamFixtures.Read(StreamFixtures.GoogleLive05).Replace("\"finishReason\": \"STOP\",", "");

      var ex = Assert.Throws<LlmException>(() => Read(text));
      Assert.Contains("no finishReason arrived", ex.Message);
    }

    [Fact]
    public void A_reply_cut_at_the_token_limit_reports_max_tokens_and_dispatches_no_call()
    {
      var text = StreamFixtures.Read(StreamFixtures.GoogleLive05).Replace("\"finishReason\": \"STOP\"", "\"finishReason\": \"MAX_TOKENS\"");

      var r = Read(text);

      Assert.Equal("max_tokens", r.StopReason);
      Assert.Empty(r.ToolUses);
      Assert.Equal(TurnOutcome.Truncated, AgentLoopCore.PostResponse(r.StopReason, r.ToolUses.Count > 0));
    }

    [Fact]
    public void Googles_own_malformed_call_verdict_is_a_failed_run_in_words_not_a_shorter_answer()
    {
      var text = "data: {\"candidates\":[{\"content\":{\"parts\":[],\"role\":\"model\"},\"finishReason\":\"MALFORMED_FUNCTION_CALL\"}]}\n\n";

      var ex = Assert.Throws<LlmException>(() => Read(text));
      Assert.Contains("MALFORMED_FUNCTION_CALL", ex.Message);
    }

    [Fact]
    public void What_the_reader_assembles_is_what_the_next_body_replays_and_the_transcript_is_sound()
    {
      var r = Read(StreamFixtures.Read(StreamFixtures.GoogleToolCall));
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

      var part = Body(req)["contents"].AsArray()[1]["parts"].AsArray()[1];
      Assert.Equal(r.Blocks[1].Signature, part["thoughtSignature"].GetValue<string>());
      Assert.Equal("call_1488139", part["functionCall"]["id"].GetValue<string>());
      Assert.Equal("build", part["functionCall"]["args"]["kind"].GetValue<string>());
    }

    [Fact]
    public void A_whole_call_makes_the_loop_carry_on()
    {
      var r = Read(StreamFixtures.Read(StreamFixtures.GoogleToolCall));
      Assert.Null(AgentLoopCore.PostResponse(r.StopReason, r.ToolUses.Count > 0));
    }
  }
}
