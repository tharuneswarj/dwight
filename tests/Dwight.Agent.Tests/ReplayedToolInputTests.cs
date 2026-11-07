using System.Linq;
using System.Text.Json.Nodes;
using Dwight.Llm;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// #827 / #873 step 2 - a replayed tool call is replayed TRUTHFULLY or not at all.
  ///
  /// <para>A <c>tool_use</c> block in the transcript is <b>the model's own earlier words</b>, read back to it as
  /// the record of what it did. The body builder used to parse it with a catch that returned an empty object,
  /// so an input it could not read was replayed as <c>{}</c>. The model then read a call it never made -
  /// <c>build_region</c> with no arguments - followed by a result describing work on arguments it can no longer
  /// see it supplied. <b>A tool with no parameters is a capability that refuses; a rewritten <c>tool_use</c> is a
  /// transcript that lies about the past, to the one reader that cannot check it against anything.</b></para>
  ///
  /// <para>REFUSED AT THE DESTINATION, not in the provider-neutral pre-flight (ruled #873(c), #335): Anthropic
  /// needs <c>input</c> to be an OBJECT, so a malformed one cannot be replayed faithfully here. OpenAI carries
  /// its arguments as a STRING and could replay the same text verbatim - so refusing everywhere would kill
  /// turns that could have been replayed truthfully.</para>
  /// </summary>
  [Collection(StreamFixtures.CaptureWindow)]   // U38's end-to-end rows run the Anthropic reader
  public class ReplayedToolInputTests
  {
    private static string Emit(string toolName, string inputJson)
    {
      var request = new LlmRequest
      {
        MaxTokens = 4096,
        Messages = { LlmMessage.Assistant(new[] { LlmBlock.ToolUse("toolu_1", toolName, inputJson) }.ToList()) },
      };
      return AnthropicRequestBody.Build(request, "claude-test", ThinkingWireState.Omit);
    }

    private static JsonNode ReplayedInput(string json) =>
      ((JsonObject)JsonNode.Parse(json))["messages"].AsArray()[0]["content"].AsArray()[0]["input"];

    // ---- refused, never rewritten -------------------------------------------------------------------

    /// <summary>THE DEFECT. A damaged record is refused - and the sentence names the tool, because "a call"
    /// out of a turn of eight sends the reader everywhere except the one that was damaged.</summary>
    [Fact]
    public void An_unreadable_replayed_input_is_refused_not_rewritten_to_an_empty_object()
    {
      var ex = Assert.Throws<LlmException>(() => Emit("build_region", "{\"region\":\"A1\",\"comp"));

      Assert.Contains("build_region", ex.Message);
      Assert.Contains("could not be read", ex.Message);
    }

    /// <summary>Parsing is not the bar - the same rule as the schemas (#828): an array or a scalar parses
    /// cleanly and still cannot be a tool call's input.</summary>
    [Theory]
    [InlineData("[1,2,3]")]
    [InlineData("\"a string\"")]
    [InlineData("42")]
    [InlineData("null")]
    public void A_replayed_input_that_parses_but_is_not_an_object_is_refused(string notAnObject)
    {
      var ex = Assert.Throws<LlmException>(() => Emit("get_component", notAnObject));

      Assert.Contains("get_component", ex.Message);
    }

    /// <summary>Every producer of a tool_use block supplies an input - the Anthropic reader carries the one the
    /// model streamed, or its start event's own <c>input</c>, and refuses a call that has neither (U38); the
    /// scripted drill provider defaults an empty one to <c>{}</c>. So a MISSING input is not something the model
    /// said; it is a record that lost the arguments, and inventing <c>{}</c> for it is the same rewrite.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_replayed_call_with_no_input_recorded_is_refused(string missing)
    {
      Assert.Throws<LlmException>(() => Emit("get_overview", missing));
    }

    /// <summary>The refusal must not overclaim. Earlier steps of the SAME turn may already have run their tools,
    /// so "nothing was run" would be false in a reachable state; what is true is that THIS step was not sent.
    /// A refusal is a claim, and it must be true in every state that can reach it.</summary>
    [Fact]
    public void The_refusal_claims_only_what_is_true()
    {
      var ex = Assert.Throws<LlmException>(() => Emit("build_region", "{oops"));

      Assert.Contains("was not sent", ex.Message);
      Assert.DoesNotContain("nothing was run", ex.Message.ToLowerInvariant());
    }

    // ---- and a truthful record goes through untouched -------------------------------------------------

    [Fact]
    public void A_readable_input_is_replayed_exactly()
    {
      var input = ReplayedInput(Emit("build_region", "{\"region\":\"A1\",\"components\":[{\"ref\":\"s\",\"type\":\"Number Slider\"}]}"));

      Assert.Equal("{\"region\":\"A1\",\"components\":[{\"ref\":\"s\",\"type\":\"Number Slider\"}]}", input.ToJsonString());
    }

    /// <summary>A call with no arguments is a real call - get_overview takes none - and <c>{}</c> is exactly what
    /// the model sent for it. The rule is against INVENTING an empty object, not against replaying one.</summary>
    [Fact]
    public void A_real_call_with_no_arguments_is_replayed_as_the_empty_object_it_was()
    {
      Assert.Equal("{}", ReplayedInput(Emit("get_overview", "{}")).ToJsonString());
    }

    // ---- U38: the reader and the destination, end to end, on the calls Anthropic really streamed -------

    /// <summary>Every tool call in the six captured Anthropic streams - assembled by the reader from its
    /// <c>input_json_delta</c> fragments - is replayed at the destination as EXACTLY the arguments the model
    /// sent: the reader and the replay agree about what the model said. A positive control for the refusals
    /// above: they fire on damage, and this shows they do not fire on the real thing.</summary>
    [Theory]
    [InlineData(StreamFixtures.Anthropic01)]
    [InlineData(StreamFixtures.Anthropic02)]
    [InlineData(StreamFixtures.Anthropic03)]
    [InlineData(StreamFixtures.Anthropic04)]
    [InlineData(StreamFixtures.Anthropic05)]
    public void Every_captured_tool_call_is_replayed_exactly_as_the_reader_assembled_it(string fixture)
    {
      var reply = AnthropicReplyStream.Read(StreamFixtures.Reader(StreamFixtures.Read(fixture)));
      var calls = reply.Blocks.Where(b => b.Kind == "tool_use").ToList();
      Assert.NotEmpty(calls);

      var request = new LlmRequest { MaxTokens = 4096, Messages = { LlmMessage.Assistant(reply.Blocks) } };
      var body = JsonNode.Parse(AnthropicRequestBody.Build(request, "claude-test", ThinkingWireState.Omit));
      var replayed = body["messages"][0]["content"].AsArray().Where(b => b["type"].GetValue<string>() == "tool_use").ToList();

      Assert.Equal(calls.Count, replayed.Count);
      for (var i = 0; i < calls.Count; i++)
      {
        Assert.Equal(calls[i].ToolUseId, replayed[i]["id"].GetValue<string>());
        Assert.Equal(calls[i].ToolName, replayed[i]["name"].GetValue<string>());
        Assert.Equal(JsonNode.Parse(calls[i].ToolInputJson).ToJsonString(), replayed[i]["input"].ToJsonString());
      }
    }

    /// <summary>And the order of the two guards, stated: a damaged argument stream is refused by the READER, so
    /// the destination never receives a record it would have to refuse - the destination's refusal stays the
    /// guard for a record damaged by anything else.</summary>
    [Fact]
    public void A_damaged_argument_stream_is_refused_by_the_reader_before_it_can_reach_the_replay()
    {
      var text = StreamFixtures.Read(StreamFixtures.Anthropic01)
        .Replace("\"partial_json\":\"0}\"", "\"partial_json\":\"0\"");

      var ex = Assert.Throws<LlmException>(() => AnthropicReplyStream.Read(StreamFixtures.Reader(text)));
      Assert.StartsWith("Dwight could not read the model's reply in full", ex.Message);
      Assert.Contains("\"get_cluster\"", ex.Message);
    }
  }
}
