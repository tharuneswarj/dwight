using System.Collections.Generic;
using Dwight.Agent;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// #611c: an approved turn is TOLD what it was approved for. The build that never ran was told to raise
  /// cards and never told that its own turn was the approval (#609 link 1) - so these read the system text
  /// the turn is actually sent, composed by the same function the panel calls.
  /// </summary>
  public class ApprovedTurnFactTests
  {
    private const string Situation = "\n\nSITUATION: stub";

    [Fact]
    public void The_build_turn_system_text_names_every_approved_region()
    {
      var text = TurnSystemText.Compose(Situation, null, null, TurnCapability.Build, new[] { "A1", "A2", "B3" });

      Assert.Contains(ApprovedTurnFact.Heading, text);
      Assert.Contains("building A1, A2, B3", text);
      Assert.Contains("Build them now with " + AgentToolCatalog.BuildRegion, text);
      Assert.Contains("do not ask for it again", text);
    }

    [Fact]
    public void A_single_region_reads_as_one()
    {
      var fact = ApprovedTurnFact.For(TurnCapability.Build, new[] { "A1" });

      Assert.Contains("building A1. Build it now with build_region.", fact);
      Assert.Contains("outside that one", fact);
    }

    [Theory]
    [InlineData((int)TurnCapability.Build, AgentToolCatalog.BuildRegion)]
    [InlineData((int)TurnCapability.ReadAndSolve, AgentToolCatalog.SolveAndCheck)]
    [InlineData((int)TurnCapability.Scaffold, AgentToolCatalog.ProposeScaffold)]
    public void It_names_the_acting_tool_and_says_the_approval_is_given(int capabilityValue, string tool)
    {
      var fact = ApprovedTurnFact.For((TurnCapability)capabilityValue, new[] { "A1" });

      Assert.StartsWith(ApprovedTurnFact.Heading, fact);
      Assert.Contains(tool, fact);
      Assert.Contains(" now", fact);
      Assert.Contains("The approval is already given, so do not ask for it again.", fact);
    }

    /// <summary>A typed message is always read-only and holds no approval - telling it otherwise would be
    /// the plugin asserting a state the system is not in.</summary>
    [Fact]
    public void A_read_only_turn_carries_no_approval_fact()
    {
      Assert.Null(ApprovedTurnFact.For(TurnCapability.ReadOnly, new[] { "A1" }));
      Assert.DoesNotContain(ApprovedTurnFact.Heading,
        TurnSystemText.Compose(Situation, null, null, TurnCapability.ReadOnly, null));
    }

    [Fact]
    public void A_build_with_no_regions_states_no_approval()
    {
      Assert.Null(ApprovedTurnFact.For(TurnCapability.Build, new List<string>()));
      Assert.Null(ApprovedTurnFact.For(TurnCapability.Build, null));
    }

    /// <summary>
    /// The move out of the panel changes nothing a read-only turn is sent: master prompt, situation, canvas
    /// fact, in that order, byte for byte what RunTurn assembled before.
    /// </summary>
    [Fact]
    public void A_read_only_turn_is_sent_exactly_what_it_was_before()
    {
      var before = Dwight.Prompts.Prompts.AgentMasterWith(AgentHostTools.CapabilitySentence()) + Situation +
                   "\n\n" + "CANVAS: changed";

      Assert.Equal(before, TurnSystemText.Compose(Situation, "CANVAS: changed", null, TurnCapability.ReadOnly, null));
    }

    /// <summary>#625/#647: the approved build turn is told what its rebuilds cut, inside the approval it carries out,
    /// so the boundary is part of the last thing the model reads - and it ends on the way out.</summary>
    [Fact]
    public void An_approved_rebuild_is_told_its_cut_list_as_part_of_the_approval()
    {
      var cuts = new Dictionary<string, IReadOnlyList<CutWire>>
      {
        ["A1"] = new[]
        {
          new CutWire { ConsumerId = 19, ConsumerType = "XY Plane", ConsumerRegion = "B1", InputLabel = "Origin",
                        SourceType = "Construct Point", SourceOutputLabel = "Point" },
        },
      };

      var text = TurnSystemText.Compose(Situation, "CANVAS: changed", null, TurnCapability.Build, new[] { "A1" }, cuts);

      Assert.True(text.IndexOf(ApprovedTurnFact.Heading) < text.IndexOf(RebuildBoundary.TurnHeading));
      Assert.Contains("XY Plane (19) in B1 input Origin", text);
      Assert.EndsWith(RebuildBoundary.WayOut, text);
    }

    [Fact]
    public void A_read_only_turn_is_never_told_a_cut_list()
    {
      var cuts = new Dictionary<string, IReadOnlyList<CutWire>> { ["A1"] = new[] { new CutWire { ConsumerId = 19 } } };

      Assert.DoesNotContain(RebuildBoundary.TurnHeading,
        TurnSystemText.Compose(Situation, null, null, TurnCapability.ReadOnly, null, cuts));
    }

    /// <summary>The approval comes LAST, after the canvas fact - the most recent thing the model reads.</summary>
    [Fact]
    public void The_approval_follows_the_canvas_fact()
    {
      var text = TurnSystemText.Compose(Situation, "CANVAS: changed", null, TurnCapability.Build, new[] { "A1" });

      Assert.True(text.IndexOf("CANVAS: changed") < text.IndexOf(ApprovedTurnFact.Heading));
      Assert.EndsWith("Build no region outside that one.", text);
    }
  }
}
