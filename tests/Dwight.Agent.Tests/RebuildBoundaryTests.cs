using System;
using System.Collections.Generic;
using System.Linq;
using Dwight.Agent;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// #625: A REBUILD KEEPS ITS BOUNDARY. Every wire the removal would cut into something it keeps is matched by
  /// exactly one declared reconnection, or the build refuses before anything is touched - with the cut list, and
  /// with #647's way out for a wire the user wants gone.
  /// </summary>
  public class RebuildBoundaryTests
  {
    private static readonly Guid XyInput = new Guid("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid LoftInput = new Guid("aaaaaaaa-0000-0000-0000-000000000002");

    private static CutWire Cut(int consumer = 19, string type = "XY Plane", string input = "Origin", int at = 0,
                               Guid? inputGuid = null, string region = "B1", bool unreadable = false) =>
      new CutWire
      {
        ConsumerGuid = Guid.NewGuid(), ConsumerId = consumer, ConsumerType = type, ConsumerRegion = region,
        InputGuid = inputGuid ?? XyInput, InputLabel = input, Position = at,
        SourceGuid = Guid.NewGuid(), SourceType = "Construct Point", SourceOutputLabel = "Point",
        Unreadable = unreadable,
      };

    private static BuildReconnect To(int consumer, string input, string fromRef = "pt", int? at = null) =>
      new BuildReconnect { ToId = consumer, ToParam = input, At = at, FromRef = fromRef, FromParam = "Point" };

    [Fact]
    public void A_build_that_cuts_nothing_and_declares_nothing_passes()
    {
      Assert.True(RebuildBoundary.Match("A1", new CutWire[0], new BuildReconnect[0], out var planned, out var error));
      Assert.Empty(planned);
      Assert.Null(error);
    }

    /// <summary>THE SHAPE OF THE RULE: an undeclared cut refuses, naming the wire in the plugin's own words.</summary>
    [Fact]
    public void An_undeclared_cut_is_refused_with_the_cut_named()
    {
      Assert.False(RebuildBoundary.Match("A1", new[] { Cut() }, new BuildReconnect[0], out var planned, out var error));

      Assert.Empty(planned);
      Assert.Equal(ToolErrors.BoundaryCut, error.Code);
      Assert.Contains("Rebuilding A1 removes components that feed 1 wire outside it", error.Message);
      Assert.Contains("XY Plane (19) in B1 input Origin, fed by Construct Point's Point", error.Message);
      Assert.Contains("Nothing was changed.", error.Message);
      Assert.Contains("\"reconnect\"", error.Message);
    }

    /// <summary>
    /// #647, THE CUT THE USER WANTS. If nothing in the new build should feed the input, "reconnect" alone leaves the
    /// model to loop or invent a mapping. The refusal carries the way out: a build cannot remove that wire; the
    /// user deletes it first, and then the rebuild cuts nothing.
    /// </summary>
    [Fact]
    public void The_refusal_carries_the_way_out_for_a_wire_the_user_wants_gone()
    {
      RebuildBoundary.Match("A1", new[] { Cut() }, new BuildReconnect[0], out _, out var error);

      Assert.Contains(RebuildBoundary.WayOut, error.Message);
      Assert.Contains("a build cannot remove that wire", error.Message);
      Assert.Contains("tell the user to delete the wire themselves first", error.Message);
    }

    [Fact]
    public void A_declared_reconnection_matching_the_cut_is_planned_at_its_position()
    {
      var cut = Cut(at: 2);
      Assert.True(RebuildBoundary.Match("A1", new[] { cut }, new[] { To(19, "origin") }, out var planned, out var error), error?.Message);

      var p = Assert.Single(planned);
      Assert.Same(cut, p.Cut);
      Assert.Equal("pt", p.Declared.FromRef);
      Assert.Equal(2, p.Cut.Position);
    }

    [Fact]
    public void A_declaration_that_matches_no_cut_is_refused_with_the_real_cut_list()
    {
      Assert.False(RebuildBoundary.Match("A1", new[] { Cut() }, new[] { To(19, "Direction"), To(19, "Origin") },
        out _, out var error));

      Assert.Equal(ToolErrors.BoundaryCut, error.Code);
      Assert.Contains("nothing there to reconnect", error.Message);
      Assert.Contains("XY Plane (19) in B1 input Origin", error.Message);
    }

    [Fact]
    public void A_declaration_on_a_build_that_cuts_nothing_is_refused()
    {
      Assert.False(RebuildBoundary.Match("A1", new CutWire[0], new[] { To(19, "Origin") }, out _, out var error));
      Assert.Contains("cuts no wire and has no lost input to restore, so there is nothing to reconnect", error.Message);
    }

    // ---- #625's narrow sibling: a restorable loss is an OPTION, never an obligation -------------------

    private static CutWire Restorable(int consumer = 19, string input = "Origin", int at = 1) =>
      new CutWire
      {
        ConsumerGuid = Guid.NewGuid(), ConsumerId = consumer, ConsumerType = "XY Plane", ConsumerRegion = "B1",
        InputGuid = XyInput, InputLabel = input, Position = at, SourceGuid = Guid.NewGuid(),
        Restore = true, FromRegion = "A1",
      };

    /// <summary>#648 iv: a fresh build cuts nothing, so leaving a restorable loss undeclared is a permission not taken.</summary>
    [Fact]
    public void An_undeclared_restorable_loss_is_never_refused()
    {
      Assert.True(RebuildBoundary.Match("A1", new[] { Restorable() }, new BuildReconnect[0], out var planned, out var error));
      Assert.Empty(planned);
      Assert.Null(error);
    }

    [Fact]
    public void A_declared_restore_is_planned_at_the_position_the_lost_source_held()
    {
      var loss = Restorable(at: 1);
      Assert.True(RebuildBoundary.Match("A1", new[] { loss }, new[] { To(19, "Origin") }, out var planned, out var error), error?.Message);

      var p = Assert.Single(planned);
      Assert.Same(loss, p.Cut);
      Assert.True(p.Cut.Restore);
      Assert.Equal(1, p.Cut.Position);
    }

    [Fact]
    public void A_restorable_loss_does_not_excuse_an_undeclared_cut()
    {
      Assert.False(RebuildBoundary.Match("A1", new[] { Restorable(), Cut(consumer: 20, type: "Loft", input: "Curves", inputGuid: LoftInput) },
        new[] { To(19, "Origin") }, out _, out var error));
      Assert.Contains("Loft (20) in B1 input Curves", error.Message);
    }

    [Fact]
    public void A_restorable_loss_is_described_as_a_loss_not_as_a_cut()
    {
      var described = RebuildBoundary.Describe(Restorable(), false);

      Assert.Equal("XY Plane (19) in B1 input Origin, which lost its source when A1's earlier components were removed",
                   described);
      Assert.DoesNotContain("fed by", described);
    }

    [Fact]
    public void The_turn_fact_offers_a_restore_as_an_option_without_the_way_out()
    {
      var fact = RebuildBoundary.TurnFact(ByRegion(("A1", new[] { Restorable() })));

      Assert.Contains("building A1 may restore 1 input that lost its source when A1's earlier components were removed", fact);
      Assert.Contains("only if the new build should feed it again", fact);
      Assert.DoesNotContain(RebuildBoundary.WayOut, fact);   // the way out is about a cut, and there is none
    }

    /// <summary>Two cut sources on ONE input: "at" is a genuine choice there, so it is required, and each one
    /// goes back at its own position.</summary>
    [Fact]
    public void Two_cuts_on_one_input_need_at_and_are_planned_in_position_order()
    {
      var first = Cut(input: "Curves", type: "Loft", consumer: 7, inputGuid: LoftInput, at: 0);
      var second = Cut(input: "Curves", type: "Loft", consumer: 7, inputGuid: LoftInput, at: 2);

      Assert.False(RebuildBoundary.Match("A1", new[] { first, second }, new[] { To(7, "Curves") }, out _, out var needsAt));
      Assert.Contains("needs \"at\"", needsAt.Message);
      Assert.Contains(" at 0", needsAt.Message);
      Assert.Contains(" at 2", needsAt.Message);

      Assert.True(RebuildBoundary.Match("A1", new[] { first, second },
        new[] { To(7, "Curves", "b", at: 2), To(7, "Curves", "a", at: 0) }, out var planned, out var error), error?.Message);
      Assert.Equal(new[] { 0, 2 }, planned.Select(p => p.Cut.Position));
      Assert.Equal(new[] { "a", "b" }, planned.Select(p => p.Declared.FromRef));
    }

    [Fact]
    public void An_at_that_names_no_cut_is_refused()
    {
      Assert.False(RebuildBoundary.Match("A1", new[] { Cut(at: 0) }, new[] { To(19, "Origin", at: 3) }, out _, out var error));
      Assert.Contains("has no cut wire at 3", error.Message);
    }

    [Fact]
    public void Two_declarations_for_one_cut_are_refused()
    {
      Assert.False(RebuildBoundary.Match("A1", new[] { Cut() }, new[] { To(19, "Origin", "a"), To(19, "Origin", "b") },
        out _, out var error));
      Assert.Contains("Each cut wire takes exactly one", error.Message);
    }

    /// <summary>
    /// A consumer Dwight cannot read - inside a cluster, or not a top-level object - is REFUSED, not guessed at:
    /// cluster internals are not in the fingerprint, so no verdict could check a reconnection there. Even a
    /// declaration cannot make it legal.
    /// </summary>
    [Fact]
    public void A_cut_into_something_Dwight_cannot_read_is_refused_even_when_declared()
    {
      var unreadable = Cut(consumer: 0, type: null, region: null, unreadable: true);

      Assert.False(RebuildBoundary.Match("A1", new[] { unreadable }, new[] { To(0, "Origin") }, out _, out var error));
      Assert.Equal(ToolErrors.BoundaryCut, error.Code);
      Assert.Contains("something Dwight cannot read (inside a cluster", error.Message);
      Assert.Contains("removed or rerouted by hand", error.Message);
    }

    /// <summary>A floating param consumer has one implicit input and no name, so it matches with no param.</summary>
    [Fact]
    public void A_param_consumer_matches_without_an_input_name()
    {
      Assert.True(RebuildBoundary.Match("A1", new[] { Cut(type: "Panel", input: null, consumer: 5) },
        new[] { To(5, null) }, out var planned, out var error), error?.Message);
      Assert.Single(planned);
    }

    [Fact]
    public void Every_undeclared_cut_is_listed_and_only_those()
    {
      var declared = Cut(consumer: 19);
      var missed = Cut(consumer: 20, type: "Loft", input: "Curves", inputGuid: LoftInput, region: "C1");

      Assert.False(RebuildBoundary.Match("A1", new[] { declared, missed }, new[] { To(19, "Origin") }, out _, out var error));

      Assert.Contains("feed 1 wire outside it", error.Message);
      Assert.Contains("Loft (20) in C1 input Curves", error.Message);
      Assert.DoesNotContain("XY Plane (19)", error.Message);
    }

    // ---- #625/#647: the approved build turn is TOLD its cut list, with the way out ---------------------

    private static Dictionary<string, IReadOnlyList<CutWire>> ByRegion(params (string region, CutWire[] cuts)[] entries) =>
      entries.ToDictionary(e => e.region, e => (IReadOnlyList<CutWire>)e.cuts);

    [Fact]
    public void The_turn_fact_names_each_regions_cuts_and_how_to_reconnect_them()
    {
      var fact = RebuildBoundary.TurnFact(ByRegion(("A1", new[] { Cut() })));

      Assert.StartsWith(RebuildBoundary.TurnHeading, fact);
      Assert.Contains("rebuilding A1 cuts 1 wire you must reconnect in \"reconnect\": " +
                      "XY Plane (19) in B1 input Origin, fed by Construct Point's Point.", fact);
    }

    /// <summary>#647: the way out is told up front, where the model plans the build, not only after a refusal.</summary>
    [Fact]
    public void The_turn_fact_carries_the_way_out()
    {
      Assert.EndsWith(RebuildBoundary.WayOut, RebuildBoundary.TurnFact(ByRegion(("A1", new[] { Cut() }))));
    }

    [Fact]
    public void The_turn_fact_says_a_region_with_an_unreadable_cut_cannot_be_rebuilt_until_the_user_acts()
    {
      var fact = RebuildBoundary.TurnFact(ByRegion(
        ("A1", new[] { Cut(consumer: 0, type: null, region: null, unreadable: true) })));

      Assert.Contains("A1 cannot be rebuilt until the user removes or reroutes", fact);
    }

    [Fact]
    public void The_turn_fact_is_silent_when_no_approved_region_cuts_anything()
    {
      Assert.Null(RebuildBoundary.TurnFact(ByRegion(("A1", new CutWire[0]))));
      Assert.Null(RebuildBoundary.TurnFact(null));
    }

    /// <summary>#647's row class reads the refusal's REASON and its unreconnected consumer labels, never its message.</summary>
    [Fact]
    public void The_refusal_detail_carries_its_reason_and_the_unreconnected_consumers()
    {
      RebuildBoundary.Match("A1", new[] { Cut() }, new BuildReconnect[0], out _, out var unreconnected);
      Assert.Equal(RebuildBoundary.ReasonUnreconnected, unreconnected.Detail["reason"].GetValue<string>());
      Assert.Equal("XY Plane (19) in B1 input Origin",
                   Assert.Single(unreconnected.Detail["unreconnected"].AsArray()).GetValue<string>());

      RebuildBoundary.Match("A1", new[] { Cut(unreadable: true) }, new BuildReconnect[0], out _, out var unreadable);
      Assert.Equal(RebuildBoundary.ReasonUnreadable, unreadable.Detail["reason"].GetValue<string>());

      RebuildBoundary.Match("A1", new[] { Cut() }, new[] { To(19, "Direction") }, out _, out var unknown);
      Assert.Equal("unknown_target", unknown.Detail["reason"].GetValue<string>());
      Assert.Empty(unknown.Detail["unreconnected"].AsArray());
    }

    [Fact]
    public void The_refusal_detail_lists_every_cut_with_its_plugin_labels()
    {
      RebuildBoundary.Match("A1", new[] { Cut(at: 1) }, new BuildReconnect[0], out _, out var error);

      var cut = Assert.Single(error.Detail["cuts"].AsArray());
      Assert.Equal(19, cut["consumer"].GetValue<int>());
      Assert.Equal("Origin", cut["input"].GetValue<string>());
      Assert.Equal(1, cut["at"].GetValue<int>());
      Assert.Equal("B1", cut["consumerRegion"].GetValue<string>());
    }
  }
}
