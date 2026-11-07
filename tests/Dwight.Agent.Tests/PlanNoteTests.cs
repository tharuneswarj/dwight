using System.Text.Json.Nodes;
using Dwight.Llm;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>xUnit runs a collection with parallelization disabled on its own, after the parallel ones have finished.</summary>
  [CollectionDefinition(PlanNoteTests.RunsAlone, DisableParallelization = true)]
  public class PlanNoteCollection { }

  /// <summary>
  /// THE STEP-DOWN NOTE IS A STATIC, AND EVERY ANTHROPIC BODY BUILT ANYWHERE CLEARS IT (<see cref="AnthropicRequestBody.LastPlanNote"/>, last write wins). So a test that builds a body and then reads
  /// the note can lose it to any class running beside it: PR #171's run went red here with the note null, on a change that touched nothing near it. This class holds its own evidence by running alone.
  /// A test that reads that static belongs here.
  /// </summary>
  [Collection(RunsAlone)]
  public class PlanNoteTests
  {
    public const string RunsAlone = "Reads the plan note static, so runs alone";

    [Fact]
    public void Over_the_ceiling_the_effort_steps_down_never_the_answer_and_it_is_recorded()
    {
      var request = new LlmRequest { System = "system words", Messages = { LlmMessage.User("build A1") }, MaxTokens = 20000, Thinking = true, Effort = "max" };

      AnthropicRequestBody.LastPlanNote = null;
      var body = (JsonObject)JsonNode.Parse(OpenAIResponses.BuildBody(request, "gpt-test"));

      Assert.Equal("high", body["reasoning"]["effort"].GetValue<string>());
      Assert.Equal(20000 + ThinkingBudget.ReserveHigh, body["max_output_tokens"].GetValue<int>());
      Assert.Contains("stepped down", AnthropicRequestBody.LastPlanNote);
    }
  }
}
