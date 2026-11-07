using System;
using System.Linq;
using System.Text.Json.Nodes;
using Dwight.Agent;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// Phase-9 U4: set_value as a request_mutation KIND (the ask) and as a catalogue TOOL (the MCP face) -
  /// both routing to the one applier, so what the user approved is what executes (Q1).
  /// </summary>
  public class SetValueKindTests
  {
    private static JsonObject Args(string json) => (JsonObject)JsonNode.Parse(json);

    [Fact]
    public void SET_VALUE_IS_AN_OFFERABLE_KIND_AND_ITS_ASK_CARRIES_TARGET_AND_STATE_ON_THE_SEED()
    {
      Assert.Contains("set_value", AgentHostTools.Kinds);

      var ok = AgentHostTools.TryParseRequest(
        Args("{\"kind\":\"set_value\",\"why\":\"the count should be 40\",\"target\":42,\"state\":{\"min\":0,\"max\":100,\"value\":40,\"rounding\":\"integer\"}}"),
        out var kind, out _, out var seed, out var error);
      Assert.True(ok, error?.Message);
      Assert.Equal("set_value", kind);
      Assert.Equal(42, seed.Target);
      Assert.Equal(40m, seed.State["value"].GetValue<decimal>());
    }

    [Fact]
    public void A_NON_INTEGER_TARGET_IS_DROPPED_NOT_GUESSED_AND_THE_ASK_LAYER_REFUSES_IT()
    {
      // The parse is lenient the way build's regions are (validated at the ask, not the parse) - but a
      // malformed target never becomes some other integer.
      Assert.True(AgentHostTools.TryParseRequest(Args("{\"kind\":\"set_value\",\"why\":\"x\",\"target\":\"forty-two\"}"), out _, out _, out var seed, out _));
      Assert.Null(seed.Target);
      Assert.Null(seed.State);
    }

    [Fact]
    public void THE_CATALOGUE_ADVERTISES_SET_VALUE_AS_A_MUTATING_TOOL_GRANTED_ON_NO_PANEL_TURN()
    {
      var d = AgentToolCatalog.All().Single(t => t.Name == AgentToolCatalog.SetValue);
      Assert.True(d.Mutates);
      Assert.Contains("EXISTING", d.Description);
      Assert.Contains("state", d.InputSchemaJson);

      // Ungranted in the panel loop by declaration: the plugin applies from the card, so no turn may call it.
      foreach (TurnCapability c in Enum.GetValues(typeof(TurnCapability)))
        Assert.False(AgentLoopCore.IsGranted(c, AgentToolCatalog.SetValue), c + " must not grant set_value");
      Assert.Contains(AgentToolCatalog.SetValue, AgentLoopCore.Ungranted);
    }

    // ---- #678: the continuation is a re-read that holds no approval ------------------------------------------

    /// <summary>
    /// THE SEMANTIC CONFLICT the merge could not see (#678). The branch continued an applied set_value with ReadAndSolve,
    /// which on main IS the approved-solve capability - so the turn would be told "the user has approved a solve" and
    /// every same-kind machinery would treat solve as in flight. The continuation holds no approval: no kind, no fact.
    /// </summary>
    [Fact]
    public void THE_CONTINUATION_HOLDS_NO_APPROVAL_AND_IS_TOLD_OF_NONE()
    {
      Assert.Null(AgentHostTools.KindApprovedBy(SetValueCore.Continuation));
      Assert.Null(ApprovedTurnFact.For(SetValueCore.Continuation, null));

      var system = TurnSystemText.Compose("SITUATION", null, null, SetValueCore.Continuation, null);
      Assert.DoesNotContain(ApprovedTurnFact.Heading, system);
    }

    /// <summary>A re-read cannot solve: the apply's own scheduled solution already recomputed downstream.</summary>
    [Fact]
    public void THE_CONTINUATION_CANNOT_CALL_SOLVE_OR_ANY_TOOL_THAT_ACTS()
    {
      var tools = AgentLoopCore.ToolsFor(SetValueCore.Continuation).Select(t => t.Name).ToList();

      Assert.DoesNotContain(AgentToolCatalog.SolveAndCheck, tools);
      Assert.DoesNotContain(tools, AgentToolCatalog.Acts);
    }

    [Fact]
    public void THE_SEED_PLAN_KNOWS_THE_KIND_AND_CARRIES_TARGET_AND_STATE_THROUGH()
    {
      var seed = new HandoffSeed { Target = 42, State = Args("{\"text\":\"hi\"}"), Why = "because" };
      var plan = SeedPlan.From("set_value", seed, _ => IdState.Alive);
      Assert.NotEmpty(plan.Actions);
      Assert.Equal(42, plan.Target);
      Assert.NotNull(plan.State);
    }
  }
}
