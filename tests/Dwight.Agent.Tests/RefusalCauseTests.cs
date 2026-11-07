using System.Text.Json.Nodes;
using Dwight.Agent;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// F2, from the WP-4 gate: TWICE THE MODEL NARRATED DWIGHT'S INTERNALS FROM GUESSWORK.
  ///
  /// Explaining a refusal it invented "a fresh session boundary or the 'map was gone' episode intervened,
  /// the system no longer recognizes it as mine to remove" - none of which had happened, and the user
  /// caught it. Asked to delete, it said deletion "has to be routed through an approval card so it's
  /// tracked properly" and produced no card, because there is no delete kind.
  ///
  /// THE SHARED CAUSE: our refusals said WHAT was refused and never WHY, so a model that has to account
  /// for our behaviour to a user filled the gap with a plausible story. That is not a prompt-discipline
  /// failure - the model was doing its job with the information it had.
  /// </summary>
  public class RefusalCauseTests
  {
    /// <summary>
    /// The causes a user could ask "why?" about all carry one. That is the architect's test for scope: a
    /// refusal that never reaches the conversation needs nothing, and one that can be rendered to a user
    /// is in.
    /// </summary>
    [Theory]
    [InlineData(ToolErrors.NoDocument)]
    [InlineData(ToolErrors.StaleDocument)]
    [InlineData(ToolErrors.Busy)]
    [InlineData(ToolErrors.SolverDisabled)]
    [InlineData(ToolErrors.UnknownComponentType)]
    [InlineData(ToolErrors.UnknownId)]
    [InlineData(ToolErrors.RetiredId)]
    [InlineData(ToolErrors.UnknownRegion)]
    [InlineData(ToolErrors.RegionOccupied)]
    [InlineData(ToolErrors.RegionAttemptsExhausted)]
    [InlineData(ToolErrors.RegionNotApproved)]
    [InlineData(ToolErrors.VerificationFailed)]
    public void Every_refusal_a_user_could_ask_why_about_carries_a_cause(string code)
    {
      var refusal = ToolResult.Fail(code, "something was refused");

      Assert.Equal(ToolErrors.CauseOf(code), refusal.Detail["cause"].GetValue<string>());
    }

    /// <summary>
    /// A CAUSE IS NOT A RESTATEMENT. The message already says what was refused; a cause that repeated it
    /// would leave exactly the gap the model filled with fiction. Each one says WHY the rule exists.
    /// </summary>
    [Theory]
    [InlineData(ToolErrors.RegionOccupied, "only its own work")]
    [InlineData(ToolErrors.SolverDisabled, "unverified build must never stand")]
    [InlineData(ToolErrors.RetiredId, "has since been deleted")]
    [InlineData(ToolErrors.RegionNotApproved, "approved specific regions")]
    public void The_cause_explains_the_rule_rather_than_repeating_the_message(string code, string reason)
    {
      Assert.Contains(reason, ToolErrors.CauseOf(code));
    }

    /// <summary>
    /// A SITE'S OWN CAUSE ALWAYS WINS. The generic one comes from the code; a site that knows something
    /// more specific - build_region's three-way present/absent/not-known distinction, for instance - must
    /// not have it overwritten by the default.
    /// </summary>
    [Fact]
    public void A_site_that_supplies_its_own_cause_keeps_it()
    {
      var refusal = ToolResult.Fail(ToolErrors.RegionOccupied, "no",
        new JsonObject { ["cause"] = "Dwight has no record for this region" });

      Assert.Equal("Dwight has no record for this region", refusal.Detail["cause"].GetValue<string>());
    }

    /// <summary>Codes whose message IS the cause get none - invalid_args already says what was wrong with
    /// the call, and a generic sentence beside it would be noise the model has to read past.</summary>
    [Fact]
    public void A_code_whose_message_is_already_the_cause_gets_no_extra_sentence()
    {
      Assert.Null(ToolErrors.CauseOf(ToolErrors.InvalidArgs));
      Assert.Null(ToolResult.Fail(ToolErrors.InvalidArgs, "bad call").Detail);
    }

    /// <summary>A cause never displaces what a site was already reporting.</summary>
    [Fact]
    public void Adding_a_cause_does_not_disturb_the_rest_of_the_detail()
    {
      var refusal = ToolResult.Fail(ToolErrors.UnknownRegion, "no such region",
        new JsonObject { ["asked"] = "Z9" });

      Assert.Equal("Z9", refusal.Detail["asked"].GetValue<string>());
      Assert.NotNull(refusal.Detail["cause"]);
    }

    // ---- the latent defect this uncovered -----------------------------------------------------------

    /// <summary>
    /// ToJson() HAD NEVER BEEN IDEMPOTENT, and nothing noticed because most refusals carried no detail
    /// at all, so the second call had nothing to re-parent. Giving every refusal a cause turned a latent
    /// defect into a crash - a JsonNode may have only one parent, and a tool result is exactly the kind
    /// of thing serialised once for the wire and again for a log or a test.
    /// </summary>
    [Fact]
    public void A_refusal_can_be_serialised_more_than_once()
    {
      var refusal = ToolResult.Fail(ToolErrors.UnknownId, "no such id", new JsonObject { ["id"] = 999 });

      var first = refusal.ToJson();
      var second = refusal.ToJson();

      Assert.Equal(first, second);
      Assert.Contains("999", second);
      Assert.Contains("cause", second);
    }

    [Fact]
    public void A_success_can_be_serialised_more_than_once_too()
    {
      var ok = ToolResult.Success(new JsonObject { ["a"] = 1 });

      Assert.Equal(ok.ToJson(), ok.ToJson());
    }
  }
}
