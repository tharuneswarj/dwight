using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Dwight.Agent;
using Dwight.Llm;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// #1171: a chat turn's answer budget is computed from the script limit. The relation, then that #216's reserve and ceiling still hold at
  /// the new number on every provider's real body, then what the catalogue can say about a model's limits.
  /// </summary>
  public class ChatAnswerBudgetTests
  {
    [Fact]
    public void The_budget_carries_the_longest_script_at_the_stated_worst_case_with_the_prose_on_top()
    {
      Assert.Equal(12000, EditScriptAsk.MaxSourceChars);
      Assert.Equal(9200, ChatAnswerBudget.Turn);
      Assert.Equal(ChatAnswerBudget.For(EditScriptAsk.MaxSourceChars), ChatAnswerBudget.Turn);
      // 3 characters to a token, and 30% more for the newer Claude tokenizer.
      Assert.True(ChatAnswerBudget.Turn - ChatAnswerBudget.ProseRoom >= EditScriptAsk.MaxSourceChars / 3.0 * 1.3);
    }

    [Theory]
    [InlineData(12000, 12001)]
    [InlineData(12000, 24000)]
    [InlineData(3000, 6000)]
    public void Raising_the_limit_raises_the_budget(int limit, int raised)
    {
      Assert.True(ChatAnswerBudget.For(raised) > ChatAnswerBudget.For(limit));
    }

    [Fact]
    public void The_config_reads_the_computed_number_for_both_chat_turns_and_types_none()
    {
      var config = File.ReadAllText(Path.Combine(RepoRoot(), "Dwight", "DwightConfig.cs"));

      Assert.Contains("public static int AgentTurnAnswerBudget => Dwight.Agent.ChatAnswerBudget.Turn;", config);
      Assert.Contains("public static int AgentBuildAnswerBudget => Dwight.Agent.ChatAnswerBudget.Turn;", config);
    }

    /// <summary>The verbs keep theirs: their default is ADR-030's ceiling, and it does not move with the script limit.</summary>
    [Fact]
    public void The_verbs_budget_is_untouched()
    {
      Assert.Equal(16_000, ThinkingBudget.DefaultAnswerBudget);
      Assert.Equal(16_000, new LlmRequest().MaxTokens);
    }

    // ---- #216 at the new number -------------------------------------------------------------------------

    [Theory]
    [InlineData("max", ThinkingBudget.ReserveMax)]
    [InlineData("high", ThinkingBudget.ReserveHigh)]
    [InlineData("medium", ThinkingBudget.ReserveMedium)]
    [InlineData("low", ThinkingBudget.ReserveLow)]
    [InlineData(null, ThinkingBudget.ReserveMedium)]
    public void Every_effort_keeps_its_whole_reserve_under_the_ceiling(string effort, int reserve)
    {
      var plan = ThinkingBudget.Resolve(ChatAnswerBudget.Turn, effort);

      Assert.False(plan.SteppedDown);
      Assert.Null(plan.Note);
      Assert.Equal(effort, plan.Effort);
      Assert.Equal(ChatAnswerBudget.Turn, plan.AnswerBudget);
      Assert.Equal(reserve, plan.Reserve);
      Assert.Equal(ChatAnswerBudget.Turn + reserve, plan.MaxTokens);
      Assert.True(plan.MaxTokens <= ThinkingBudget.Ceiling);
    }

    /// <summary>How far the limit can rise before effort max steps down. Past this, the step-down is correct and says so, and the answer stays whole.</summary>
    [Fact]
    public void The_limit_has_room_before_the_ceiling_makes_effort_step_down()
    {
      var room = ThinkingBudget.Ceiling - ThinkingBudget.ReserveMax - ChatAnswerBudget.Turn;
      Assert.Equal(6800, room);

      var tooBig = ThinkingBudget.Resolve(ThinkingBudget.Ceiling - ThinkingBudget.ReserveMax + 1, "max");
      Assert.True(tooBig.SteppedDown);
      Assert.Equal("high", tooBig.Effort);
    }

    private static LlmRequest Turn(bool thinking = false, string effort = null) => new LlmRequest
    {
      System = "s",
      Messages = { LlmMessage.User("q") },
      Tools = AgentHostTools.All().Select(t => new LlmTool { Name = t.Name, Description = t.Description, InputSchemaJson = t.InputSchemaJson }).ToList(),
      MaxTokens = ChatAnswerBudget.Turn,
      Thinking = thinking,
      Effort = effort,
    };

    [Fact]
    public void Anthropic_sends_the_budget_plus_the_reserve_with_thinking_and_the_budget_alone_without()
    {
      var adaptive = JsonNode.Parse(AnthropicRequestBody.Build(Turn(true, "max"), "claude-test", ThinkingWireState.Adaptive));
      Assert.Equal(9200 + ThinkingBudget.ReserveMax, adaptive["max_tokens"].GetValue<int>());
      Assert.Equal("max", adaptive["output_config"]["effort"].GetValue<string>());

      var off = JsonNode.Parse(AnthropicRequestBody.Build(Turn(), "claude-test", ThinkingWireState.Disabled));
      Assert.Equal(9200, off["max_tokens"].GetValue<int>());
    }

    [Theory]
    [InlineData("OpenAI", true, 9200)]
    [InlineData("Ollama Cloud", false, 9200)]
    [InlineData("Ollama Cloud", true, 9200 + ThinkingBudget.ReserveHigh)]
    [InlineData("Ollama (local)", true, 9200 + ThinkingBudget.ReserveHigh)]
    public void The_OpenAI_shape_sends_the_budget_and_Ollama_its_reserve_on_top(string label, bool reasoning, int expected)
    {
      var isOpenAi = label == "OpenAI";
      var p = new OpenAIProvider("https://example.invalid/v1", null, "m", ProviderCapability.Format, isOpenAi, label, reasoningModel: reasoning);

      var body = JsonNode.Parse(p.BuildBody(Turn(), structured: false));

      Assert.Equal(expected, body[isOpenAi ? "max_completion_tokens" : "max_tokens"].GetValue<int>());
    }

    [Fact]
    public void Google_sends_the_budget()
    {
      var body = JsonNode.Parse(new GoogleProvider(null, "gemini-test").BuildBody(Turn(), structured: false));

      Assert.Equal(9200, body["generationConfig"]["maxOutputTokens"].GetValue<int>());
    }

    // ---- what the catalogue can say about a model's limits -----------------------------------------------

    private static JsonArray Models =>
      JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "models.json")))["models"].AsArray();

    /// <summary>The catalogue records the window and has no output-limit field, so this is what it can hold: every model that can reach a chat
    /// turn has room in its window for the largest request Dwight can send, and every row names a window at all.</summary>
    [Fact]
    public void Every_agentic_model_has_room_in_its_window_for_the_largest_request()
    {
      var agentic = Models.Where(m => m["capabilities"]?["agentic"]?.GetValue<bool>() == true).ToList();

      Assert.NotEmpty(agentic);
      var largest = ChatAnswerBudget.Turn + ThinkingBudget.ReserveMax;
      Assert.All(agentic, m => Assert.True(m["contextWindow"].GetValue<int>() >= largest, m["id"].GetValue<string>()));
    }

    /// <summary>The one output limit the catalogue states, in prose, on one row: the request stays under it. If the catalogue ever gains an output
    /// field, this test fails on the second assertion and the budget has a real fact to read instead.</summary>
    [Fact]
    public void The_one_stated_output_limit_holds_and_the_catalogue_has_no_output_field_yet()
    {
      var argon = Models.Single(m => m["id"].GetValue<string>() == "gemini-4-argon");
      Assert.Contains("65,536 output tokens", argon["speedNote"].GetValue<string>());
      Assert.True(ThinkingBudget.Ceiling <= 65_536);

      Assert.DoesNotContain(Models, m => m.AsObject().Any(kv => kv.Key.IndexOf("output", StringComparison.OrdinalIgnoreCase) >= 0));
    }

    private static string RepoRoot()
    {
      var dir = new DirectoryInfo(AppContext.BaseDirectory);
      while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Dwight.sln"))) dir = dir.Parent;
      Assert.True(dir != null, "the repository root (Dwight.sln) could not be found from " + AppContext.BaseDirectory);
      return dir.FullName;
    }
  }
}
