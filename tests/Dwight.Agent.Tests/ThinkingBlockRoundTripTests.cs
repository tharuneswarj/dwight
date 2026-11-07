using System;
using System.Linq;
using System.Text.Json.Nodes;
using Dwight.Llm;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// Ruling #192(b) - the thinking-block round-trip.
  ///
  /// THE FAILURE THIS PREVENTS. With thinking on, a continuation must echo the assistant turn EXACTLY
  /// as received; rebuilding it, or dropping a thinking or redacted_thinking block, is a 400. The
  /// neutral block model carried only text/tool_use/tool_result, so a thinking block was parsed off the
  /// stream and then silently discarded - which means the very first repair round-trip in
  /// StructuredOutput would have failed the moment thinking was ever enabled, before WP-3's agent loop
  /// existed to be blamed.
  ///
  /// The awkward case is the ordinary one: display defaults to omitted on the current families, so a
  /// thinking block normally arrives with EMPTY text and only a signature. A round-trip that quietly
  /// drops "empty" blocks would fail exactly where it matters most.
  /// </summary>
  public class ThinkingBlockRoundTripTests
  {
    private static string Emit(params LlmBlock[] blocks)
    {
      var request = new LlmRequest
      {
        MaxTokens = 4096,
        Messages = { LlmMessage.Assistant(blocks.ToList()) },
      };
      return AnthropicRequestBody.Build(request, "claude-test", ThinkingWireState.Adaptive);
    }

    private static JsonArray BlocksOf(string json) =>
      ((JsonObject)JsonNode.Parse(json))["messages"].AsArray()[0]["content"].AsArray();

    [Fact]
    public void A_thinking_block_is_echoed_back_with_its_signature()
    {
      var blocks = BlocksOf(Emit(LlmBlock.Thinking("Weighing the loft options...", "SIG-abc123")));

      Assert.Single(blocks);
      Assert.Equal("thinking", blocks[0]["type"].GetValue<string>());
      Assert.Equal("Weighing the loft options...", blocks[0]["thinking"].GetValue<string>());
      Assert.Equal("SIG-abc123", blocks[0]["signature"].GetValue<string>());
    }

    [Fact]
    public void AN_EMPTY_TEXT_THINKING_BLOCK_STILL_ROUND_TRIPS()
    {
      // THE NORM, not an edge case: display defaults to omitted, so the summary is blank and the
      // signature is the whole substance. Dropping it as "empty" would 400 the next turn.
      var blocks = BlocksOf(Emit(LlmBlock.Thinking("", "SIG-only")));

      Assert.Single(blocks);
      Assert.Equal("thinking", blocks[0]["type"].GetValue<string>());
      Assert.Equal("", blocks[0]["thinking"].GetValue<string>());
      Assert.Equal("SIG-only", blocks[0]["signature"].GetValue<string>());
    }

    [Fact]
    public void A_redacted_thinking_block_is_carried_verbatim_and_never_inspected()
    {
      var blocks = BlocksOf(Emit(LlmBlock.RedactedThinking("OPAQUE-PAYLOAD-xyz")));

      Assert.Single(blocks);
      Assert.Equal("redacted_thinking", blocks[0]["type"].GetValue<string>());
      Assert.Equal("OPAQUE-PAYLOAD-xyz", blocks[0]["data"].GetValue<string>());
    }

    [Fact]
    public void A_mixed_assistant_turn_preserves_every_block_and_their_order()
    {
      // The real shape of a thinking turn that calls a tool: thinking, then text, then tool_use. Order
      // matters - the thinking block must accompany the tool_use it reasoned toward.
      var json = Emit(
        LlmBlock.Thinking("", "SIG-1"),
        LlmBlock.TextBlock("Let me check the boundary."),
        LlmBlock.ToolUse("toolu_1", "get_component", "{\"id\":7}"));

      var blocks = BlocksOf(json);
      Assert.Equal(3, blocks.Count);
      Assert.Equal("thinking", blocks[0]["type"].GetValue<string>());
      Assert.Equal("text", blocks[1]["type"].GetValue<string>());
      Assert.Equal("tool_use", blocks[2]["type"].GetValue<string>());
      Assert.Equal("toolu_1", blocks[2]["id"].GetValue<string>());
    }

    [Fact]
    public void A_thinking_block_survives_the_repair_round_trips_echo()
    {
      // StructuredOutput's repair does exactly this: LlmMessage.Assistant(response.Blocks). Before the
      // fix, the thinking block never reached this point at all.
      var received = new System.Collections.Generic.List<LlmBlock>
      {
        LlmBlock.Thinking("", "SIG-repair"),
        LlmBlock.ToolUse("toolu_9", "emit_plan", "{}"),
      };

      var echoed = BlocksOf(Emit(received.ToArray()));
      Assert.Equal("thinking", echoed[0]["type"].GetValue<string>());
      Assert.Equal("SIG-repair", echoed[0]["signature"].GetValue<string>());
    }

    [Fact]
    public void Thinking_blocks_do_not_leak_into_the_prose_text()
    {
      // LlmResponse.Text joins TEXT blocks only. A thinking summary must never be mistaken for the
      // model's answer, or it would end up rendered in a panel or parsed as JSON.
      var response = new LlmResponse
      {
        Blocks =
        {
          LlmBlock.Thinking("internal deliberation", "SIG"),
          LlmBlock.TextBlock("The visible answer."),
        },
      };

      Assert.Equal("The visible answer.", response.Text);
      Assert.DoesNotContain("internal deliberation", response.Text);
    }

    [Fact]
    public void Null_signature_and_data_emit_as_empty_strings_rather_than_null_json()
    {
      // Fail safe (ADR-029): a malformed block must not put a null where the API expects a string.
      var json = Emit(new LlmBlock { Kind = "thinking", Text = null, Signature = null },
                      new LlmBlock { Kind = "redacted_thinking", Data = null });

      var blocks = BlocksOf(json);
      Assert.Equal("", blocks[0]["thinking"].GetValue<string>());
      Assert.Equal("", blocks[0]["signature"].GetValue<string>());
      Assert.Equal("", blocks[1]["data"].GetValue<string>());
    }
  }
}
