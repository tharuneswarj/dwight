using Dwight.Llm;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// #216 - the answer budget and the thinking reserve, measured into existence.
  ///
  /// The Stage-D probe put effort "max" against a bare 4096-token budget and the model spent
  /// thinking_tokens 4096 = output_tokens 4096, returning ZERO characters of answer. Thinking is billed
  /// INSIDE max_tokens and nothing coupled the two, so a caller that raised effort without raising its
  /// budget bought a truncated call at full price.
  ///
  /// The invariant these tests hold: THE ANSWER BUDGET IS NEVER REDUCED. Effort steps down to fit;
  /// the deliverable does not shrink to make room for the reasoning about it.
  /// </summary>
  public class ThinkingBudgetTests
  {
    [Fact]
    public void THE_RESERVE_IS_ADDED_ON_TOP_AND_NEVER_CARVED_OUT()
    {
      // The whole bug in one assertion: what the caller asked to spend on the answer is still available
      // for the answer after the reserve exists.
      var plan = ThinkingBudget.Resolve(4096, "medium");

      Assert.Equal(4096, plan.AnswerBudget);
      Assert.Equal(4096 + ThinkingBudget.ReserveMedium, plan.MaxTokens);
      Assert.Equal("medium", plan.Effort);
      Assert.False(plan.SteppedDown);
      Assert.Null(plan.Note);
    }

    [Fact]
    public void THE_MEASURED_FAILURE_CANNOT_RECUR()
    {
      // The exact configuration that returned nothing: effort max, 4096 answer budget.
      var plan = ThinkingBudget.Resolve(4096, "max");
      Assert.True(plan.MaxTokens - 4096 >= 4096,
        "the reserve must exceed the 4096 thinking tokens actually observed being spent");
    }

    [Theory]
    [InlineData("low", ThinkingBudget.ReserveLow)]
    [InlineData("medium", ThinkingBudget.ReserveMedium)]
    [InlineData("high", ThinkingBudget.ReserveHigh)]
    [InlineData("max", ThinkingBudget.ReserveMax)]
    public void Each_effort_level_reserves_its_own_amount(string effort, int expected)
    {
      Assert.Equal(expected, ThinkingBudget.ReserveFor(effort));
    }

    [Fact]
    public void Adaptive_with_no_effort_still_reserves_because_the_API_default_still_thinks()
    {
      // Silence means "take the API default", which is not "do not think" - so it gets headroom too.
      Assert.True(ThinkingBudget.ReserveFor(null) > 0);
      var plan = ThinkingBudget.Resolve(4096, null);
      Assert.True(plan.MaxTokens > 4096);
      Assert.False(plan.SteppedDown);   // nothing was asked for, so nothing was stepped down
    }

    // ---- the ceiling: effort gives way, the answer does not -----------------------------------------

    [Fact]
    public void WHEN_THE_SUM_EXCEEDS_THE_CEILING_THE_EFFORT_STEPS_DOWN_AND_THE_ANSWER_IS_KEPT_WHOLE()
    {
      var plan = ThinkingBudget.Resolve(20_000, "max", ceiling: 30_000);

      Assert.Equal(20_000, plan.AnswerBudget);          // untouched, which is the point
      Assert.True(plan.MaxTokens <= 30_000);
      Assert.NotEqual("max", plan.Effort);
      Assert.True(plan.SteppedDown);
    }

    [Fact]
    public void A_STEP_DOWN_IS_ALWAYS_RECORDED_NEVER_SILENT()
    {
      // "We asked for max" becomes folklore precisely when the degrade leaves no trace.
      var plan = ThinkingBudget.Resolve(20_000, "max", ceiling: 30_000);

      Assert.NotNull(plan.Note);
      Assert.Contains("max", plan.Note);
      Assert.Contains(plan.Effort, plan.Note);
    }

    [Fact]
    public void It_steps_down_only_as_far_as_it_must()
    {
      // 20k + high(16k) = 36k > 35k, but 20k + medium(8k) = 28k fits. It must not fall to "low".
      var plan = ThinkingBudget.Resolve(20_000, "max", ceiling: 35_000);
      Assert.Equal("medium", plan.Effort);
    }

    [Fact]
    public void An_effort_that_already_fits_is_left_alone()
    {
      var plan = ThinkingBudget.Resolve(1_000, "max");
      Assert.Equal("max", plan.Effort);
      Assert.False(plan.SteppedDown);
    }

    [Fact]
    public void AN_ANSWER_BUDGET_THAT_FILLS_THE_CEILING_KEEPS_THE_ANSWER_AND_SAYS_WHAT_IT_COST()
    {
      // The one case where the guarantee cannot be kept. The answer is still not shrunk - the reserve is
      // what gives - and the note says so rather than letting a truncation arrive unexplained.
      var plan = ThinkingBudget.Resolve(50_000, "max", ceiling: 48_000);

      Assert.Equal(50_000, plan.AnswerBudget);
      Assert.True(plan.MaxTokens >= 50_000, "the answer budget must survive even when nothing else can");
      Assert.NotNull(plan.Note);
    }

    // ---- the seam, not just the rule ----------------------------------------------------------------

    private static System.Text.Json.Nodes.JsonObject Body(ThinkingWireState state, string effort, int maxTokens)
    {
      var request = new LlmRequest { System = "s", MaxTokens = maxTokens, Effort = effort };
      request.Messages.Add(LlmMessage.User("hello"));
      return (System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse(
        AnthropicRequestBody.Build(request, "claude-sonnet-5", state));
    }

    [Fact]
    public void THE_ADAPTIVE_BODY_CARRIES_THE_RESERVE_ON_TOP_OF_THE_ANSWER_BUDGET()
    {
      // The rule is only worth anything where it is applied - at the seam every call passes through.
      var body = Body(ThinkingWireState.Adaptive, "max", 4096);
      Assert.Equal(4096 + ThinkingBudget.ReserveMax, body["max_tokens"].GetValue<int>());
      Assert.Equal("adaptive", body["thinking"]["type"].GetValue<string>());
      Assert.Equal("max", body["output_config"]["effort"].GetValue<string>());
    }

    [Fact]
    public void A_NON_THINKING_BODY_IS_UNTOUCHED_BY_ANY_OF_THIS()
    {
      // #192a's inertness proof still holds: with thinking off, the budget is exactly what was asked
      // for. A reserve on a call that cannot think would be pure overstatement of the ceiling.
      Assert.Equal(4096, Body(ThinkingWireState.Omit, "max", 4096)["max_tokens"].GetValue<int>());
      Assert.Equal(4096, Body(ThinkingWireState.Disabled, "max", 4096)["max_tokens"].GetValue<int>());
    }

    [Fact]
    public void The_seam_sends_the_STEPPED_DOWN_effort_rather_than_the_one_that_did_not_fit()
    {
      var body = Body(ThinkingWireState.Adaptive, "max", ThinkingBudget.Ceiling - ThinkingBudget.ReserveLow);
      Assert.Equal("low", body["output_config"]["effort"].GetValue<string>());
      Assert.True(body["max_tokens"].GetValue<int>() <= ThinkingBudget.Ceiling);
    }

    [Fact]
    public void A_request_that_sets_no_budget_takes_the_pinned_one_not_a_smaller_default()
    {
      // The reconciled drift: LlmRequest used to default to 4096 while the config said 16000.
      Assert.Equal(ThinkingBudget.DefaultAnswerBudget, new LlmRequest().MaxTokens);
    }

    [Fact]
    public void The_defaults_leave_room_for_the_pinned_answer_budget_at_every_effort()
    {
      // The configuration that actually ships: ADR-030's 16000-token answer budget. At every effort the
      // plan must fit the ceiling, so no shipped call is ever silently degraded by the defaults alone.
      foreach (var effort in new[] { "low", "medium", "high", "max" })
      {
        var plan = ThinkingBudget.Resolve(ThinkingBudget.DefaultAnswerBudget, effort);
        Assert.True(plan.MaxTokens <= ThinkingBudget.Ceiling,
          effort + ": the shipped answer budget plus its reserve exceeds the ceiling");
      }
    }
  }
}
