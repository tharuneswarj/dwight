using Dwight.Agent;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// THE SCAFFOLD TURN'S FRONT DOOR: eligibility, THEN the key, and the pinned rule (#885).
  ///
  /// These hold the SEQUENCE, which is the panel's. The eligibility DECISION is lane B's and is tested beside it
  /// (AgentEligibilityTests); here it is a stub, so each test can make either half pass or fail on its own.
  ///
  /// STATED BOUND: RunTurn and the DEBUG drill both call <see cref="AgentFrontDoor.Check"/>, but both are
  /// GH-bound, so that they call it - rather than a copy of it - is held by the code, not by a test here.
  /// </summary>
  public class AgentFrontDoorTests
  {
    private const string Anthropic = "anthropic";

    private static ModelInfo Model(string id, bool agentic = true) =>
      new ModelInfo(id, id, Anthropic, "Strong", 3, 15, 200_000, agentic: agentic);

    private static string Refuses(ModelInfo m, string p, bool pinned) => "the agent cannot run on " + m?.Id;
    private static string Allows(ModelInfo m, string p, bool pinned) => null;
    private static bool NoKey(string provider) => false;
    private static bool KeyStored(string provider) => true;

    // ---- THE ORDER ------------------------------------------------------------------------------------

    /// <summary>
    /// THE PROPERTY A RESHUFFLE LOSES. A model the agent cannot run on, with no key stored: the user is told the
    /// agent cannot run here - never "add a key", only to be refused once they have added one.
    /// </summary>
    [Fact]
    public void Eligibility_is_decided_before_the_key()
    {
      var door = AgentFrontDoor.Check(null, Model("claude-haiku-4-5", agentic: false), Anthropic, Refuses, NoKey);

      Assert.Equal(FrontDoorRefusal.NotEligible, door.Refusal);
      Assert.Equal("the agent cannot run on claude-haiku-4-5", door.Text);
    }

    [Fact]
    public void An_eligible_model_with_no_key_is_refused_for_the_key()
    {
      var door = AgentFrontDoor.Check(null, Model("claude-sonnet-5"), Anthropic, Allows, NoKey);
      Assert.Equal(FrontDoorRefusal.NoKey, door.Refusal);
      Assert.Null(door.Text);
    }

    [Fact]
    public void An_eligible_model_with_a_key_may_start()
    {
      Assert.Equal(FrontDoorRefusal.None,
        AgentFrontDoor.Check(null, Model("claude-sonnet-5"), Anthropic, Allows, KeyStored).Refusal);
    }

    // ---- THE PINNED RULE ------------------------------------------------------------------------------

    /// <summary>
    /// PINNED means an override is set AND the resolved model IS that override. The resolver falls back when a
    /// pinned id is not in the catalogue, and a refusal blaming a pin that was not in effect would send the user
    /// to fix the wrong thing. Read off what the decision is actually handed.
    /// </summary>
    [Theory]
    [InlineData("claude-haiku-4-5", "claude-haiku-4-5", true)]    // the pin is in effect
    [InlineData("CLAUDE-HAIKU-4-5", "claude-haiku-4-5", true)]    // ids compare without case
    [InlineData("not-in-the-catalogue", "claude-sonnet-5", false)]  // the resolver fell back: not the pin's doing
    [InlineData("", "claude-sonnet-5", false)]                     // no override at all
    [InlineData(null, "claude-sonnet-5", false)]
    public void The_decision_is_told_the_model_was_pinned_only_when_the_pin_is_in_effect(
      string overrideId, string resolvedId, bool expected)
    {
      bool? told = null;
      AgentFrontDoor.Check(overrideId, Model(resolvedId), Anthropic, (m, p, pinned) => { told = pinned; return null; }, KeyStored);

      Assert.Equal(expected, told);
    }

    // ---- FAILING CLOSED -------------------------------------------------------------------------------

    /// <summary>A caller that forgets a half cannot start a turn: no checker is not a pass.</summary>
    [Fact]
    public void A_missing_check_fails_closed()
    {
      Assert.Equal(FrontDoorRefusal.NotEligible,
        AgentFrontDoor.Check(null, Model("claude-sonnet-5"), Anthropic, null, KeyStored).Refusal);
      Assert.Equal(FrontDoorRefusal.NoKey,
        AgentFrontDoor.Check(null, Model("claude-sonnet-5"), Anthropic, Allows, null).Refusal);
    }
  }
}
