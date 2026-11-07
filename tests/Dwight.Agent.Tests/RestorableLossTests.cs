using System;
using System.Collections.Generic;
using System.Linq;
using Dwight.Agent;
using Dwight.Data;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// #625's NARROW SIBLING: the plugin's diff that finds an input whose ONLY change since Dwight left the component is
  /// one source lost from the region being built. Every condition is required, and each failing case below is a way a
  /// looser check would restore a wire into work the user had actually changed.
  /// </summary>
  public class RestorableLossTests
  {
    private static readonly Guid Plane = new Guid("44444444-0000-0000-0000-000000000001");
    private static readonly Guid Origin = new Guid("44444444-0000-0000-0000-0000000000a1");
    private static readonly Guid XAxis = new Guid("44444444-0000-0000-0000-0000000000a2");
    private static readonly Guid Slider = new Guid("44444444-0000-0000-0000-0000000000b1");
    private static readonly Guid LostPoint = new Guid("44444444-0000-0000-0000-0000000000c1");   // A1's old output
    private static readonly Guid OtherLost = new Guid("44444444-0000-0000-0000-0000000000c2");

    private static FpObject XyPlane(Guid[] origin, Guid[] xAxis, string value = null) => new FpObject
    {
      Guid = Plane,
      TypeName = "XY Plane",
      ValueToken = value,
      Inputs =
      {
        new FpInput { Guid = Origin, MappingToken = "none", Sources = origin.ToList() },
        new FpInput { Guid = XAxis, MappingToken = "none", Sources = xAxis.ToList() },
      },
    };

    /// <summary>The state Dwight left it in: Origin fed by [Slider, A1's Point], X-Axis fed by nothing.</summary>
    private static AsLeftState AsLeft(string nick = "Plane", string state = null) => new AsLeftState
    {
      Token = AsLeftRecord.Of(XyPlane(new[] { Slider, LostPoint }, new Guid[0]), nick, state),
      Sources = new Dictionary<Guid, IReadOnlyList<Guid>>
      {
        [Origin] = new[] { Slider, LostPoint },
        [XAxis] = new Guid[0],
      },
      Outputs = new[] { Guid.NewGuid() },
    };

    private static readonly HashSet<Guid> A1Dormant = new HashSet<Guid> { LostPoint };

    [Fact]
    public void An_input_that_lost_only_the_regions_source_is_found_at_that_position()
    {
      var now = XyPlane(new[] { Slider }, new Guid[0]);

      var loss = RestorableLoss.Find(AsLeft(), now, "Plane", null, A1Dormant);

      Assert.NotNull(loss);
      Assert.Equal(Origin, loss.InputGuid);
      Assert.Equal(1, loss.Position);
      Assert.Equal(LostPoint, loss.LostSource);
    }

    [Fact]
    public void Nothing_lost_is_nothing_to_restore()
    {
      Assert.Null(RestorableLoss.Find(AsLeft(), XyPlane(new[] { Slider, LostPoint }, new Guid[0]), "Plane", null, A1Dormant));
    }

    /// <summary>A rename is the user taking the component over - the loss is not the ONLY difference.</summary>
    [Fact]
    public void A_loss_plus_a_rename_is_not_restorable()
    {
      Assert.Null(RestorableLoss.Find(AsLeft(), XyPlane(new[] { Slider }, new Guid[0]), "MyPlane", null, A1Dormant));
    }

    [Fact]
    public void A_loss_plus_a_value_change_is_not_restorable()
    {
      Assert.Null(RestorableLoss.Find(AsLeft(), XyPlane(new[] { Slider }, new Guid[0], value: "edited"), "Plane", null, A1Dormant));
    }

    /// <summary>#680: "nothing but the sources differs" covers the held state the value token cannot see - a domain or
    /// rounding change beside the loss is the user's edit, not a loss to restore.</summary>
    [Fact]
    public void A_loss_plus_a_held_state_change_is_not_restorable()
    {
      var asLeft = AsLeft(state: "slider:0..10@4/Integer/dp0");

      Assert.NotNull(RestorableLoss.Find(asLeft, XyPlane(new[] { Slider }, new Guid[0]), "Plane", "slider:0..10@4/Integer/dp0", A1Dormant));
      Assert.Null(RestorableLoss.Find(asLeft, XyPlane(new[] { Slider }, new Guid[0]), "Plane", "slider:0..20@4/Integer/dp0", A1Dormant));
    }

    [Fact]
    public void A_loss_plus_another_input_rewired_is_not_restorable()
    {
      Assert.Null(RestorableLoss.Find(AsLeft(), XyPlane(new[] { Slider }, new[] { Slider }), "Plane", null, A1Dormant));
    }

    [Fact]
    public void Two_lost_sources_are_not_restorable()
    {
      var asLeft = new AsLeftState
      {
        Token = AsLeftRecord.Of(XyPlane(new[] { Slider, LostPoint, OtherLost }, new Guid[0]), "Plane", null),
        Sources = new Dictionary<Guid, IReadOnlyList<Guid>> { [Origin] = new[] { Slider, LostPoint, OtherLost }, [XAxis] = new Guid[0] },
      };

      Assert.Null(RestorableLoss.Find(asLeft, XyPlane(new[] { Slider }, new Guid[0]), "Plane", null,
        new HashSet<Guid> { LostPoint, OtherLost }));
    }

    /// <summary>A source replaced by another is a rewire, not a loss.</summary>
    [Fact]
    public void A_source_swapped_for_another_is_not_restorable()
    {
      Assert.Null(RestorableLoss.Find(AsLeft(), XyPlane(new[] { OtherLost, Slider }, new Guid[0]), "Plane", null, A1Dormant));
    }

    /// <summary>The lost source must be THIS region's earlier component. A user's source, or another region's, is not
    /// this build's to restore.</summary>
    [Fact]
    public void A_loss_that_was_not_this_regions_output_is_not_restorable()
    {
      Assert.Null(RestorableLoss.Find(AsLeft(), XyPlane(new[] { Slider }, new Guid[0]), "Plane", null, new HashSet<Guid> { OtherLost }));
      Assert.Null(RestorableLoss.Find(AsLeft(), XyPlane(new[] { Slider }, new Guid[0]), "Plane", null, new HashSet<Guid>()));
    }

    /// <summary>BOUND, stated: a fact recorded before this unit carries no structured sources, so it never qualifies.</summary>
    [Fact]
    public void A_fact_recorded_without_structured_sources_never_qualifies()
    {
      var tokenOnly = AsLeftState.OfToken(AsLeft().Token);

      Assert.Null(RestorableLoss.Find(tokenOnly, XyPlane(new[] { Slider }, new Guid[0]), "Plane", null, A1Dormant));
    }

    [Fact]
    public void A_component_whose_inputs_changed_shape_is_not_restorable()
    {
      var now = XyPlane(new[] { Slider }, new Guid[0]);
      now.Inputs.Add(new FpInput { Guid = Guid.NewGuid(), MappingToken = "none" });

      Assert.Null(RestorableLoss.Find(AsLeft(), now, "Plane", null, A1Dormant));
    }

    // ---- the ledger reads the diff is fed from ------------------------------------------------------

    [Fact]
    public void Dormant_outputs_are_only_those_of_the_regions_components_no_longer_on_the_canvas()
    {
      var ledger = new BuildLedger(new object());
      var gone = Guid.NewGuid();
      var standing = Guid.NewGuid();
      var goneOut = Guid.NewGuid();
      var standingOut = Guid.NewGuid();
      ledger.CommitBuild("A1", new Dictionary<Guid, AsLeftState>
      {
        [gone] = new AsLeftState { Token = "t", Outputs = new[] { goneOut } },
        [standing] = new AsLeftState { Token = "t", Outputs = new[] { standingOut } },
      });

      var dormant = ledger.DormantOutputs("A1", g => g == standing);

      Assert.Equal(new[] { goneOut }, dormant);
      Assert.Empty(ledger.DormantOutputs("B1", g => false));
    }

    [Fact]
    public void Restore_candidates_are_the_facts_with_structured_sources_in_any_region()
    {
      var ledger = new BuildLedger(new object());
      var structured = Guid.NewGuid();
      ledger.CommitBuild("B1", new Dictionary<Guid, AsLeftState> { [structured] = AsLeft() });
      ledger.CommitBuild("A1", new Dictionary<Guid, string> { [Guid.NewGuid()] = "token-only" });

      var candidate = Assert.Single(ledger.RestoreCandidates());
      Assert.Equal(structured, candidate.Guid);
      Assert.Equal("B1", candidate.Region);
    }
  }
}
