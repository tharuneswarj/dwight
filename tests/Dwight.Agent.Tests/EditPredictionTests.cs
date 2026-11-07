using System;
using System.Collections.Generic;
using System.Linq;
using Dwight.Agent;
using Dwight.Data;
using Xunit;
using static Dwight.Agent.Tests.EditFixture;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// Phase-9 U6 + U21 (#760): THE VERDICT - predicted delta over the behaviour fingerprint, exactly as Simplify's layer 1
  /// (ADR-090). Predict the after-document from the before-document plus ONLY the declared change; the actual is equal or
  /// the edit is taken back and the difference is reported by name.
  ///
  /// THE ORACLE IS NOT THE CODE UNDER TEST. Every "actual after" below is made by mutating a SECOND fixture by hand
  /// (<see cref="EditFixture.Wire"/>, <see cref="EditFixture.Unwire"/>, <see cref="EditFixture.Delete"/>), written
  /// independently of <see cref="EditOpsAsk"/>'s simulation. A prediction checked against a document the predictor itself
  /// produced would agree with itself on every input.
  ///
  /// POSITIVE CONTROLS (#212): a faithful apply MATCHES (so a mismatch has been seen to be a decision, not a default), and
  /// each sabotage - a wire on the wrong port, an undeclared extra wire, a component that did not go, a no-op, an unticked
  /// row applied anyway, a value that moved - is seen to FAIL, with the right object named.
  ///
  /// WHAT THESE CANNOT WITNESS: that the live <c>BehaviourFingerprint.ReadDoc</c> reads a real document into the same
  /// shape, and that Grasshopper's <c>AddSource</c> / <c>RemoveObject</c> have the effect the fixture's hand mutation
  /// assumes. These are checked by hand in Rhino before a release.
  /// </summary>
  public class EditPredictionTests
  {
    private static EditPlan Plan(EditFixture f, params System.Text.Json.Nodes.JsonObject[] ops)
    {
      Assert.True(EditOpsAsk.Validate(Ops(ops), f.Canvas(), out var plan, out var error), error?.Message);
      return plan;
    }

    // ---- a faithful apply matches ----------------------------------------------------------------------------------------

    [Fact]
    public void Three_wires_applied_exactly_as_declared_match_the_prediction()
    {
      var f = EditFixture.Make();
      var plan = Plan(f, WireOp(3, "Result", 6, null), WireOp(1, null, 5, null), WireOp(2, null, 4, "B"));

      var after = EditFixture.Make();
      after.Wire(3, 0, 6, 0); after.Wire(1, 0, 5, 0); after.Wire(2, 0, 4, 1);

      Assert.True(EditPrediction.Matches(f.Doc(), plan, after.Doc(), out var differing));
      Assert.Empty(differing);
    }

    [Fact]
    public void A_delete_that_takes_its_wires_with_it_matches_the_prediction()
    {
      var f = EditFixture.Make();
      var plan = Plan(f, DeleteOp(3));

      var after = EditFixture.Make();
      after.Delete(3);

      Assert.True(EditPrediction.Matches(f.Doc(), plan, after.Doc(), out _));
    }

    [Fact]
    public void A_mixed_card_wire_unwire_delete_matches()
    {
      var f = EditFixture.Make();
      var plan = Plan(f, UnwireOp(1, null, 3, "A"), DeleteOp(7), WireOp(1, null, 6, null));

      var after = EditFixture.Make();
      after.Unwire(1, 0, 3, 0); after.Delete(7); after.Wire(1, 0, 6, 0);

      Assert.True(EditPrediction.Matches(f.Doc(), plan, after.Doc(), out _));
    }

    [Fact]
    public void The_prediction_is_not_the_before_state_so_a_match_is_a_decision()
    {
      // The control that makes the matches above mean something: the prediction DIFFERS from the starting fingerprint, so
      // "actual == predicted" cannot be satisfied by an edit that did nothing.
      var f = EditFixture.Make();
      var plan = Plan(f, WireOp(3, "Result", 6, null));
      Assert.NotEqual(BehaviourFingerprintCore.Compute(f.Doc()), EditPrediction.Predict(f.Doc(), plan));
    }

    // ---- every sabotage is caught, and named -------------------------------------------------------------------------------

    [Fact]
    public void A_wire_that_landed_on_the_wrong_port_is_caught_and_the_consumer_named()
    {
      var f = EditFixture.Make();
      var plan = Plan(f, WireOp(1, null, 4, "B"));

      var after = EditFixture.Make();
      after.Wire(1, 0, 4, 0);   // the right source, the wrong input: A instead of B

      Assert.False(EditPrediction.Matches(f.Doc(), plan, after.Doc(), out var differing));
      Assert.Contains(G(4), differing);
      Assert.Contains("Multiplication (4)", EditPrediction.RevertText(differing, plan.Labels, g => "?"));
    }

    [Fact]
    public void An_undeclared_extra_change_is_caught_even_when_the_declared_one_landed()
    {
      var f = EditFixture.Make();
      var plan = Plan(f, WireOp(3, "Result", 6, null));

      var after = EditFixture.Make();
      after.Wire(3, 0, 6, 0);   // declared
      after.Wire(2, 0, 5, 0);   // NOT declared: a stray rewire onto the panel

      Assert.False(EditPrediction.Matches(f.Doc(), plan, after.Doc(), out var differing));
      Assert.Equal(new[] { G(5) }, differing);
    }

    [Fact]
    public void An_edit_that_did_nothing_is_caught_the_no_op_class()
    {
      // RR14's law: an accepted change that no-ops is a FAILED change. Equivalence alone would pass this (nothing
      // unintended happened); the prediction fails it because the INTENDED change did not.
      var f = EditFixture.Make();
      var plan = Plan(f, WireOp(3, "Result", 6, null));
      Assert.False(EditPrediction.Matches(f.Doc(), plan, EditFixture.Make().Doc(), out var differing));
      Assert.Contains(G(6), differing);
    }

    [Fact]
    public void A_component_that_survived_its_own_deletion_is_caught()
    {
      var f = EditFixture.Make();
      var plan = Plan(f, DeleteOp(3));

      var after = EditFixture.Make();
      after.Delete(3);
      var doc = after.Doc();
      doc.Objects.Add(f.Doc().Objects.Single(o => o.Guid == G(3)));   // it is still there

      Assert.False(EditPrediction.Matches(f.Doc(), plan, doc, out var differing));
      Assert.Contains(G(3), differing);
    }

    [Fact]
    public void A_delete_that_took_an_extra_component_with_it_is_caught()
    {
      var f = EditFixture.Make();
      var plan = Plan(f, DeleteOp(6));

      var after = EditFixture.Make();
      after.Delete(6); after.Delete(5);   // the panel went too

      Assert.False(EditPrediction.Matches(f.Doc(), plan, after.Doc(), out var differing));
      Assert.Contains(G(5), differing);
    }

    [Fact]
    public void A_value_that_moved_is_caught_the_edit_may_not_touch_a_sliders_number()
    {
      var f = EditFixture.Make();
      f.Nodes[1].Label = "Slider X";
      var before = f.Doc();
      before.Objects.Single(o => o.Guid == G(1)).ValueToken = "slider:1";
      var plan = Plan(f, WireOp(3, "Result", 6, null));

      var after = EditFixture.Make();
      after.Wire(3, 0, 6, 0);
      var actual = after.Doc();
      actual.Objects.Single(o => o.Guid == G(1)).ValueToken = "slider:9";   // undeclared

      Assert.False(EditPrediction.Matches(before, plan, actual, out var differing));
      Assert.Equal(new[] { G(1) }, differing);
    }

    [Fact]
    public void Source_ORDER_is_behaviour_so_a_reordered_input_is_caught()
    {
      var f = EditFixture.Make();
      var plan = Plan(f, WireOp(1, null, 5, null));

      var after = EditFixture.Make();
      after.Wire(1, 0, 5, 0);
      after.InputOf(4, 0).Sources.Reverse();   // an undeclared reordering of 4.A's two sources

      Assert.False(EditPrediction.Matches(f.Doc(), plan, after.Doc(), out var differing));
      Assert.Contains(G(4), differing);
    }

    // ---- the verdict is computed on the APPLIED set only ---------------------------------------------------------------------

    [Fact]
    public void An_unticked_row_that_was_applied_anyway_fails_the_verdict_of_the_ticked_set()
    {
      var f = EditFixture.Make();
      var full = Plan(f, WireOp(3, "Result", 6, null), WireOp(1, null, 5, null));

      // the user ticks row 1 only
      Assert.True(EditOpsAsk.Subset(full.Record, new[] { 0 }, out var ops, out var rows, out _));
      Assert.True(EditOpsAsk.Validate(ops, f.Canvas(), rows, out var applied, out _));

      var onlyTicked = EditFixture.Make();
      onlyTicked.Wire(3, 0, 6, 0);
      Assert.True(EditPrediction.Matches(f.Doc(), applied, onlyTicked.Doc(), out _));

      var everything = EditFixture.Make();
      everything.Wire(3, 0, 6, 0); everything.Wire(1, 0, 5, 0);   // the unticked row went in too
      Assert.False(EditPrediction.Matches(f.Doc(), applied, everything.Doc(), out var differing));
      Assert.Contains(G(5), differing);
    }

    [Fact]
    public void The_same_full_card_matches_only_the_full_result_not_the_ticked_one()
    {
      // the converse control: the verdict really is a function of WHICH plan it is handed
      var f = EditFixture.Make();
      var full = Plan(f, WireOp(3, "Result", 6, null), WireOp(1, null, 5, null));
      var onlyFirst = EditFixture.Make();
      onlyFirst.Wire(3, 0, 6, 0);
      Assert.False(EditPrediction.Matches(f.Doc(), full, onlyFirst.Doc(), out _));
    }

    // ---- the words of the revert -------------------------------------------------------------------------------------------------

    [Fact]
    public void The_revert_names_what_differed_caps_the_list_and_says_the_canvas_is_unchanged()
    {
      var labels = Enumerable.Range(1, 8).ToDictionary(i => G(i), i => "Part (" + i + ")");
      var text = EditPrediction.RevertText(Enumerable.Range(1, 8).Select(i => G(i)).ToList(), labels, g => "?");
      Assert.Contains("Part (1), Part (2), Part (3), Part (4), Part (5) and 3 more", text);
      Assert.Contains("Nothing on the canvas is changed", text);

      // a guid the plan never named still gets a name from the live fallback, never a blank or a guid
      var unnamed = EditPrediction.RevertText(new[] { G(42) }, labels, g => "Mystery");
      Assert.Contains("Mystery", unnamed);
      Assert.DoesNotContain(G(42).ToString(), unnamed);
    }

    [Fact]
    public void A_mismatch_with_no_object_to_name_still_says_something_true()
    {
      var text = EditPrediction.RevertText(new List<Guid>(), new Dictionary<Guid, string>(), null);
      Assert.Contains("did not come out as those changes should have left it", text);
    }
  }
}
