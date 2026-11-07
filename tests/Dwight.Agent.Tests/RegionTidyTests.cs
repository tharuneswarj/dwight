using System;
using System.Collections.Generic;
using System.Linq;
using Dwight.Agent;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// WP-4 Stage D: the moved-set guard, and what happens to a region's body scribble once it is built.
  ///
  /// THE GUARD EXISTS BECAUSE NEITHER INHERITED INSTRUMENT CAN SEE A MOVE. BuildPreservation compares
  /// FpDoc snapshots and the behaviour fingerprint carries no pivots - both correctly, because both are
  /// guards about BEHAVIOUR. So a tidy that dragged the user's components across the canvas would pass
  /// "preserved: true" AND byte-identical, and neither would be lying. A verdict resting on an instrument
  /// that cannot see the change is #208 in geometry's costume, which is why the tidy needed its own.
  /// </summary>
  public class RegionTidyTests
  {
    private static readonly Guid Ours = new Guid("11111111-0000-0000-0000-000000000001");
    private static readonly Guid AlsoOurs = new Guid("11111111-0000-0000-0000-000000000002");
    private static readonly Guid Theirs = new Guid("22222222-0000-0000-0000-000000000001");

    private static Dictionary<Guid, TidyPoint> At(params (Guid G, double X, double Y)[] items) =>
      items.ToDictionary(i => i.G, i => new TidyPoint(i.X, i.Y));

    // ---- the declaration, computed BEFORE the move ------------------------------------------------

    /// <summary>#353's sharpening. The declaration comes from the ENGINE'S TARGETS against the pivots as
    /// they are NOW - a check that derived its expectation from the observation it is checking would agree
    /// with whatever happened and certify nothing.</summary>
    [Fact]
    public void The_declaration_is_the_engines_targets_against_the_CURRENT_pivots()
    {
      var current = At((Ours, 0, 0), (AlsoOurs, 10, 10), (Theirs, 50, 50));
      var targets = At((Ours, 100, 0), (AlsoOurs, 10, 10));   // one moves, one is already where it goes

      var declared = RegionTidy.Declare(current, targets);

      Assert.Equal(new[] { Ours }, declared);
    }

    /// <summary>Declaring an object that will NOT move would make exact agreement impossible and quietly
    /// weaken the check to "nothing extra moved" - so a no-op target is excluded by construction.</summary>
    [Fact]
    public void An_object_already_at_its_target_is_not_declared()
    {
      var declared = RegionTidy.Declare(At((Ours, 5, 5)), At((Ours, 5, 5)));

      Assert.Empty(declared);
    }

    [Fact]
    public void A_target_for_something_not_on_the_canvas_declares_nothing()
    {
      Assert.Empty(RegionTidy.Declare(At((Ours, 0, 0)), At((Theirs, 9, 9))));
    }

    // ---- the check ---------------------------------------------------------------------------------

    [Fact]
    public void A_tidy_that_moved_exactly_what_it_declared_is_kept()
    {
      var before = At((Ours, 0, 0), (Theirs, 50, 50));
      var after = At((Ours, 100, 0), (Theirs, 50, 50));

      var check = RegionTidy.Check(before, after, new[] { Ours });

      Assert.True(check.Ok);
      Assert.Equal(TidyVerdict.AsDeclared, check.Verdict);
    }

    /// <summary>THE ONE THIS GUARD EXISTS FOR: something moved that the tidy never named. On this canvas
    /// that is the user's own component being dragged by a layout pass it was never part of - invisible to
    /// preservation, invisible to the fingerprint, and caught only here.</summary>
    [Fact]
    public void A_component_moving_that_was_NOT_declared_reverts_the_tidy()
    {
      var before = At((Ours, 0, 0), (Theirs, 50, 50));
      var after = At((Ours, 100, 0), (Theirs, 80, 50));   // Theirs moved and nobody said it would

      var check = RegionTidy.Check(before, after, new[] { Ours });

      Assert.False(check.Ok);
      Assert.Equal(TidyVerdict.Undeclared, check.Verdict);
      Assert.Equal(new[] { Theirs }, check.Undeclared);
      Assert.Contains("did not declare", check.Message);
      Assert.Contains("reverted", check.Message);
    }

    /// <summary>The other direction is a failure too: a tidy that declared a move and did not make it did
    /// not do what it said, and a partial layout is not a layout.</summary>
    [Fact]
    public void A_declared_move_that_did_not_happen_reverts_the_tidy()
    {
      var before = At((Ours, 0, 0), (AlsoOurs, 10, 10));
      var after = At((Ours, 100, 0), (AlsoOurs, 10, 10));   // AlsoOurs was declared and stayed put

      var check = RegionTidy.Check(before, after, new[] { Ours, AlsoOurs });

      Assert.False(check.Ok);
      Assert.Equal(TidyVerdict.Incomplete, check.Verdict);
      Assert.Equal(new[] { AlsoOurs }, check.Missing);
    }

    /// <summary>The dangerous direction is reported FIRST and on its own. "Something moved that nobody
    /// declared" and "something declared did not move" are different failures, and the reader needs the
    /// one that means work outside the contract was touched.</summary>
    [Fact]
    public void An_undeclared_move_outranks_an_incomplete_one()
    {
      var before = At((Ours, 0, 0), (AlsoOurs, 10, 10), (Theirs, 50, 50));
      var after = At((Ours, 0, 0), (AlsoOurs, 10, 10), (Theirs, 80, 50));

      var check = RegionTidy.Check(before, after, new[] { Ours });

      Assert.Equal(TidyVerdict.Undeclared, check.Verdict);
    }

    /// <summary>The check reads EVERY object, not only the tidy's own targets - a guard that looked only
    /// at what it moved could not see the thing it exists to catch.</summary>
    [Fact]
    public void The_check_sees_objects_the_tidy_never_targeted()
    {
      var before = At((Theirs, 0, 0));
      var after = At((Theirs, 40, 0));

      Assert.Equal(TidyVerdict.Undeclared, RegionTidy.Check(before, after, new Guid[0]).Verdict);
    }

    /// <summary>Float noise is not a move. The same epsilon decides the declaration and the observation,
    /// which is what keeps the two comparable at all.</summary>
    [Fact]
    public void Float_noise_below_the_epsilon_is_not_a_move()
    {
      var before = At((Theirs, 10, 10));
      var after = At((Theirs, 10.001, 10.001));

      Assert.True(RegionTidy.Check(before, after, new Guid[0]).Ok);
      Assert.Empty(RegionTidy.Declare(before, after));
    }

    [Fact]
    public void A_deleted_object_is_not_this_guards_subject()
    {
      // Deletion is preservation's question, and it has its own answer there. This guard is about MOVEMENT
      // and must not produce a second, weaker verdict about something already covered.
      var check = RegionTidy.Check(At((Theirs, 0, 0)), new Dictionary<Guid, TidyPoint>(), new Guid[0]);

      Assert.True(check.Ok);
    }

    [Fact]
    public void A_tidy_that_moved_nothing_and_declared_nothing_is_fine()
    {
      Assert.True(RegionTidy.Check(At((Theirs, 0, 0)), At((Theirs, 0, 0)), new Guid[0]).Ok);
    }

    // ---- the body scribble (#238) ------------------------------------------------------------------

    /// <summary>#238: a built region whose canvas still describes the plan is a stale claim. The components
    /// ARE the description now.</summary>
    [Fact]
    public void A_built_regions_body_scribble_is_REMOVED()
    {
      Assert.Equal(ScribbleAction.Remove, RegionTidy.ForRegion(RegionOutcome.Built));
    }

    /// <summary>A region abandoned mid-correction that still described its plan would be the most
    /// misleading state of all - so its body says what happened instead.</summary>
    [Fact]
    public void A_region_that_built_with_errors_has_its_body_REPLACED()
    {
      Assert.Equal(ScribbleAction.Replace, RegionTidy.ForRegion(RegionOutcome.BuiltWithErrors));

      var text = RegionTidy.StoppedText("A1", 3);
      Assert.Contains("A1", text);
      Assert.Contains("3 attempts", text);
      Assert.Contains("conversation", text);
    }

    /// <summary>Q4's addition: A REGION NEVER TOUCHED KEEPS ITS BODY. The plan is still a plan, and a
    /// region nobody built is not stale - removing its description would destroy the map to tidy it.</summary>
    [Fact]
    public void An_untouched_regions_body_scribble_STAYS()
    {
      Assert.Equal(ScribbleAction.Keep, RegionTidy.ForRegion(null));
    }

    /// <summary>A reverted region landed nothing, so its plan still describes work to be done.</summary>
    [Fact]
    public void A_reverted_regions_body_scribble_stays_too()
    {
      Assert.Equal(ScribbleAction.Keep, RegionTidy.ForRegion(RegionOutcome.Reverted));
    }
    // ---- the anchor: the property the guard CANNOT express (#368) ---------------------------------

    /// <summary>
    /// The guard verifies WHICH objects moved, never WHERE TO - so a layout that relocated a whole region
    /// across the canvas would have every move DECLARED and be dutifully certified. This is the property
    /// that rules it out, and it is arithmetic rather than a check.
    /// </summary>
    [Fact]
    public void The_laid_out_block_KEEPS_the_members_prior_top_left()
    {
      var current = At((Ours, 500, 300), (AlsoOurs, 560, 340));
      // The engine lays out around its own origin, wherever that happens to be.
      var engine = At((Ours, -40, -40), (AlsoOurs, 60, 10));

      var placed = RegionTidy.Anchor(engine, current);

      Assert.Equal(500, placed.Values.Min(p => p.X), 3);
      Assert.Equal(300, placed.Values.Min(p => p.Y), 3);
    }

    /// <summary>The SHAPE the engine computed survives the anchoring - it is a translation, not a
    /// re-layout. Anchoring that also distorted the arrangement would defeat the point of running the
    /// engine at all.</summary>
    [Fact]
    public void Anchoring_translates_the_engines_layout_without_reshaping_it()
    {
      var current = At((Ours, 500, 300), (AlsoOurs, 560, 340));
      var engine = At((Ours, -40, -40), (AlsoOurs, 60, 10));

      var placed = RegionTidy.Anchor(engine, current);

      // The engine put AlsoOurs 100 right and 50 below Ours; the anchored result must agree.
      Assert.Equal(100, placed[AlsoOurs].X - placed[Ours].X, 3);
      Assert.Equal(50, placed[AlsoOurs].Y - placed[Ours].Y, 3);
    }

    [Fact]
    public void Anchoring_ignores_targets_for_objects_that_are_not_there()
    {
      var placed = RegionTidy.Anchor(At((Theirs, 0, 0)), At((Ours, 10, 10)));

      Assert.Empty(placed);
    }

    // ---- crowding: DETECT AND SAY, never re-space (#369) ------------------------------------------

    private static RegionTidy.TidyBox Box(string a, double x, double y, double w, double h) =>
      new RegionTidy.TidyBox(a, x, y, w, h);

    /// <summary>#239 named this: a region sized for TEXT grows when it is built and can crowd its
    /// neighbour. The tidy does not fix it - re-packing would move regions the user never asked about -
    /// but "we do not fix it" must not become "we do not mention it".</summary>
    [Fact]
    public void A_built_region_that_grew_into_its_neighbour_is_REPORTED()
    {
      var boxes = new[] { Box("A1", 0, 0, 200, 100), Box("B1", 150, 0, 200, 100) };

      var pairs = RegionTidy.Crowding(boxes, new HashSet<string> { "A1" });

      Assert.Single(pairs);
      Assert.Equal("A1", pairs[0].Grown);      // the one that CHANGED is named first
      Assert.Equal("B1", pairs[0].Neighbour);

      var text = RegionTidy.CrowdingText(pairs);
      Assert.Contains("A1 now overlaps B1", text);
      Assert.Contains("not re-spaced", text);
      Assert.Contains("Reorganise", text);     // a notice with no route out would be worse than none
    }

    /// <summary>An overlap between two regions this run never touched is not news, and reporting it would
    /// be this tidy taking credit for someone else's mess.</summary>
    [Fact]
    public void An_overlap_between_two_UNTOUCHED_regions_is_not_reported()
    {
      var boxes = new[] { Box("A1", 0, 0, 200, 100), Box("B1", 150, 0, 200, 100) };

      Assert.Empty(RegionTidy.Crowding(boxes, new HashSet<string> { "C1" }));
    }

    /// <summary>Two boxes laid out flush against each other is the engine doing its job. Reporting a
    /// touching edge as a collision would make the notice noise, and a noisy notice is one nobody reads.</summary>
    [Fact]
    public void Touching_edges_are_not_a_collision()
    {
      var boxes = new[] { Box("A1", 0, 0, 200, 100), Box("B1", 200, 0, 200, 100) };

      Assert.Empty(RegionTidy.Crowding(boxes, new HashSet<string> { "A1" }));
    }

    [Fact]
    public void Regions_that_do_not_overlap_produce_no_notice()
    {
      var boxes = new[] { Box("A1", 0, 0, 100, 100), Box("B1", 400, 0, 100, 100) };

      Assert.Empty(RegionTidy.Crowding(boxes, new HashSet<string> { "A1" }));
      Assert.Null(RegionTidy.CrowdingText(RegionTidy.Crowding(boxes, new HashSet<string> { "A1" })));
    }

    [Fact]
    public void Several_crowded_pairs_are_all_named_in_address_order()
    {
      var boxes = new[]
      {
        Box("A1", 0, 0, 200, 100), Box("B1", 150, 0, 200, 100),
        Box("A2", 0, 200, 200, 100), Box("B2", 150, 200, 200, 100),
      };

      var pairs = RegionTidy.Crowding(boxes, new HashSet<string> { "A2", "A1" });

      Assert.Equal(new[] { "A1", "A2" }, pairs.Select(p => p.Grown).ToArray());
    }

  }
}
