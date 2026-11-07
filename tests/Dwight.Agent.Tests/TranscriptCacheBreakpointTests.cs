using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Dwight.Llm;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// U57: the second prompt-cache breakpoint rides the LAST cacheable block of the LAST message, so every step of
  /// a turn reads the prior transcript from cache and writes only its new tail. Placed on the wire body only; the
  /// stored transcript never carries it. Gated by the same flag as the system breakpoint.
  /// </summary>
  public class TranscriptCacheBreakpointTests
  {
    private static LlmRequest Req(bool cache, params LlmMessage[] messages)
    {
      var r = new LlmRequest { System = "You are Dwight.", MaxTokens = 4096, CacheSystem = cache };
      r.Messages.AddRange(messages);
      return r;
    }

    private static JsonObject Body(LlmRequest r, ThinkingWireState s = ThinkingWireState.Omit) =>
      (JsonObject)JsonNode.Parse(AnthropicRequestBody.Build(r, "claude-test", s));

    private static List<string> Breakpoints(JsonObject body)
    {
      var found = new List<string>();
      if (body["system"] is JsonArray sys)
        foreach (var b in sys) if (b["cache_control"] != null) found.Add("system");
      var msgs = body["messages"].AsArray();
      for (var m = 0; m < msgs.Count; m++)
      {
        var blocks = msgs[m]["content"].AsArray();
        for (var b = 0; b < blocks.Count; b++)
          if (blocks[b]["cache_control"] != null) found.Add("m" + m + "b" + b + ":" + blocks[b]["type"]);
      }
      return found;
    }

    private static LlmMessage Assistant(params LlmBlock[] b) => LlmMessage.Assistant(b.ToList());
    private static LlmMessage Results(params LlmBlock[] b) => new LlmMessage { Role = LlmRole.User, Blocks = b.ToList() };

    [Fact]
    public void A_three_message_transcript_has_exactly_two_breakpoints_system_and_the_final_block()
    {
      var body = Body(Req(true,
        LlmMessage.User("build a canopy"),
        Assistant(LlmBlock.TextBlock("looking"), LlmBlock.ToolUse("t1", "kb_lookup", "{\"type\":\"Loft\"}")),
        Results(LlmBlock.ToolResult("t1", "ok"), LlmBlock.ToolResult("t2", "also ok"))));

      Assert.Equal(new[] { "system", "m2b1:tool_result" }, Breakpoints(body));
      Assert.Equal("ephemeral", body["messages"][2]["content"][1]["cache_control"]["type"].GetValue<string>());
    }

    [Fact]
    public void A_final_message_ending_in_a_thinking_block_puts_the_breakpoint_on_the_last_other_block()
    {
      var body = Body(Req(true,
        LlmMessage.User("go"),
        Assistant(LlmBlock.TextBlock("answer"), LlmBlock.Thinking("", "sig"), LlmBlock.RedactedThinking("blob"))),
        ThinkingWireState.Adaptive);

      Assert.Equal(new[] { "system", "m1b0:text" }, Breakpoints(body));
      // The thinking blocks still round-trip verbatim.
      Assert.Equal("sig", body["messages"][1]["content"][1]["signature"].GetValue<string>());
    }

    [Fact]
    public void Without_the_caching_flag_there_is_no_breakpoint_anywhere()
    {
      var body = Body(Req(false, LlmMessage.User("hi"), Assistant(LlmBlock.TextBlock("yo")), LlmMessage.User("again")));
      Assert.Empty(Breakpoints(body));
    }

    [Fact]
    public void The_stored_transcript_never_carries_the_breakpoint_and_the_body_is_stable()
    {
      var request = Req(true, LlmMessage.User("hi"), Results(LlmBlock.ToolResult("t1", "ok")));
      var first = AnthropicRequestBody.Build(request, "claude-test", ThinkingWireState.Omit);

      // Extending the transcript moves the breakpoint; the earlier block is clean again.
      request.Messages.Add(Assistant(LlmBlock.TextBlock("next")));
      var second = Body(request);
      Assert.Equal(new[] { "system", "m2b0:text" }, Breakpoints(second));
      Assert.Null(second["messages"][1]["content"][0]["cache_control"]);

      // Building does not mutate the request: the same request yields the same bytes.
      request.Messages.RemoveAt(2);
      Assert.Equal(first, AnthropicRequestBody.Build(request, "claude-test", ThinkingWireState.Omit));
    }
  }
}
