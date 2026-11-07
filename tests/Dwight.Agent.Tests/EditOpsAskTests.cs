using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Dwight.Agent;
using Xunit;
using static Dwight.Agent.Tests.EditFixture;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// Phase-9 U6 + U21 (#760): the ask for a consented, batched edit - wire, unwire, delete existing components.
  ///
  /// THE RULE UNDER TEST: every op resolves to exactly one existing target, or the card REFUSES with the failing rows
  /// NAMED (#338). Each refusal below is paired with a POSITIVE CONTROL - the same shape of row, made valid, passes -
  /// because a refusal that has only ever been seen refusing has not been shown to discriminate (#212).
  ///
  /// WHAT THESE CANNOT WITNESS: that the live canvas reads into <see cref="EditCanvas"/> the way the fixture is built
  /// (<c>GhEditOps.ReadCanvas</c> is the only GH-coupled seam), and that a real Grasshopper keeps the sources in the
  /// order the fixture assumes. Both are checked by hand in Rhino before a release.
  /// </summary>
  public class EditOpsAskTests
  {
    private static bool Validate(JsonArray ops, out EditPlan plan, out ToolResult error, EditFixture f = null) =>
      EditOpsAsk.Validate(ops, (f ?? EditFixture.Make()).Canvas(), out plan, out error);

    private static string Refusal(params JsonObject[] ops)
    {
      Assert.False(Validate(Ops(ops), out var plan, out var error), "expected a refusal");
      Assert.Null(plan);
      return error.Message;
    }

    private static EditPlan Accept(params JsonObject[] ops)
    {
      Assert.True(Validate(Ops(ops), out var plan, out var error), error?.Message);
      return plan;
    }

    // ---- ids: unknown, retired, not a component -------------------------------------------------------------------

    [Fact]
    public void An_unknown_id_is_refused_by_name_and_row()
    {
      var m = Refusal(WireOp(3, "Result", 99, "A"));
      Assert.Contains("Row 1", m);
      Assert.Contains("No component with id 99", m);
    }

    [Fact]
    public void A_retired_id_is_refused_distinctly_from_an_unknown_one()
    {
      var m = Refusal(DeleteOp(9));
      Assert.Contains("Component 9 is no longer on the canvas", m);
      Assert.DoesNotContain("No component with id", m);
    }

    /// <summary>
    /// U49 (#1009, found at the U6 gate's row 5): the click-time re-validation reaches the same refusal as the mint, and
    /// the mint's first words - "No card was shown" - are FALSE once the card has been shown and pressed. A refusal is a
    /// claim and must be true in every state that can reach it. The row-numbered overload IS the click-time state.
    /// </summary>
    [Fact]
    public void At_click_time_the_refusal_does_not_claim_that_no_card_was_shown_and_still_names_the_cards_row()
    {
      var canvas = Make().Canvas();
      var ops = Ops(DeleteOp(9));   // id 9 is retired in the fixture: a target that vanished between the card and the click

      // the control: the mint-time form, numbered as the rows arrive
      Assert.False(EditOpsAsk.Validate(ops, canvas, out _, out var mint));
      Assert.StartsWith("No card was shown", mint.Message);
      Assert.Contains("Row 1:", mint.Message);

      // the click: this op was row 2 on the card the user saw
      Assert.False(EditOpsAsk.Validate(ops, canvas, new[] { 2 }, out _, out var click));
      Assert.DoesNotContain("No card was shown", click.Message);
      Assert.Contains("One row of this card no longer resolves to exactly one existing target, so none of the ticked changes was made.", click.Message);
      Assert.Contains("Row 2: Component 9 is no longer on the canvas", click.Message);
      Assert.DoesNotContain("Row 1", click.Message);
    }

    [Fact]
    public void An_id_that_is_alive_but_not_a_component_is_refused_as_that()
    {
      // a group, a note: it has an id and no wires, and "unknown" or "retired" would both be false about it
      var m = Refusal(DeleteOp(8));
      Assert.Contains("not a component or a parameter", m);
    }

    [Fact]
    public void POSITIVE_CONTROL_the_same_rows_with_real_ids_are_accepted()
    {
      Accept(WireOp(3, "Result", 6, null), DeleteOp(5));
    }

    // ---- ports: exact name or nickname, never looser -----------------------------------------------------------------

    [Fact]
    public void A_port_resolves_by_exact_name_AND_by_exact_nickname_to_the_same_port()
    {
      // Addition's output is named "Result" and nicknamed "R": the model reads the name from get_component, the canvas
      // shows the nickname, and both must land on the one port (U41, #975 - refusing the spelling Dwight itself presented
      // was a refusal that was not true).
      var byName = Accept(WireOp(3, "Result", 6, null));
      var byNick = Accept(WireOp(3, "R", 6, null));
      Assert.Equal(byName.Steps[0].FromPort.Guid, byNick.Steps[0].FromPort.Guid);
      Assert.Equal("R", byNick.Steps[0].FromParam);   // the canvas's own spelling, not the model's
    }

    [Fact]
    public void A_near_miss_port_name_is_refused_with_the_real_list_never_resolved_loosely()
    {
      var m = Refusal(WireOp(10, "Z", 6, null));   // Deconstruct has two outputs, so the name is a real choice
      Assert.Contains("No output named", m);
      Assert.Contains("X", m);   // the repair names what exists
      Assert.Contains("Y", m);
    }

    [Fact]
    public void A_one_port_side_has_one_answer_and_the_row_shows_the_port_that_will_be_used()
    {
      // The build's own rule, reused: where there is exactly one port the plugin answers, and an unmatched name is not a
      // second candidate. It is not a silent fallback because the CARD names the port actually used - the user approves
      // "output R", whatever the model typed.
      var plan = Accept(WireOp(3, "Resul", 6, null));
      Assert.Equal("R", plan.Steps[0].FromParam);
      Assert.Contains("Addition (3) R", plan.Steps[0].Text);
    }

    [Fact]
    public void A_choice_with_no_name_is_refused_and_a_forced_port_needs_none()
    {
      var m = Refusal(WireOp(3, "Result", 4, null));   // Multiplication has two inputs
      Assert.Contains("needs a \"param\"", m);
      Assert.Contains("needs a \"param\"", Refusal(WireOp(10, null, 6, null)));   // Deconstruct has two outputs
      Accept(WireOp(3, "Result", 6, null));            // Negative has exactly one input: the plugin answers
      Accept(WireOp(1, null, 5, null));                // a parameter has one implicit port at each end
    }

    [Fact]
    public void A_wire_between_two_outputs_is_refused_specifically()
    {
      var m = Refusal(WireOp(3, "Result", 4, "Result"));   // "Result" is real on Multiplication - on the wrong side
      Assert.Contains("is an OUTPUT, and a wire ends at an INPUT", m);
      Assert.Contains("two outputs", m);
    }

    [Fact]
    public void A_wire_that_starts_at_an_input_is_refused_specifically()
    {
      var m = Refusal(WireOp(3, "A", 6, null));   // Addition has ONE output, yet naming its input must not be swallowed
      Assert.Contains("is an INPUT, and a wire starts at an OUTPUT", m);
    }

    [Fact]
    public void Naming_an_output_where_a_one_input_component_takes_its_input_is_refused_not_forced()
    {
      // Negative (6) has exactly one input, which the build's rule would answer without reading the name. A model that
      // wrote "Y" - Negative's OUTPUT - has the direction backwards, and silently wiring X would carry out a different
      // edit from the one it described. The positive control is the same row with no name, which is simply forced.
      var m = Refusal(WireOp(3, "Result", 6, "Y"));
      Assert.Contains("is an OUTPUT, and a wire ends at an INPUT", m);
      Accept(WireOp(3, "Result", 6, null));
    }

    [Fact]
    public void A_slider_takes_no_wire_into_it()
    {
      var m = Refusal(WireOp(3, "Result", 1, null));
      Assert.Contains("has no input to wire into", m);
    }

    // ---- duplicates, absent wires, loops -------------------------------------------------------------------------------

    [Fact]
    public void A_duplicate_wire_is_refused_and_so_is_a_repeat_inside_one_card()
    {
      var existing = Refusal(WireOp(1, null, 3, "A"));   // 1 -> 3.A already exists
      Assert.Contains("already exists", existing);

      var twice = Refusal(WireOp(3, "Result", 6, null), WireOp(3, "Result", 6, null));
      Assert.Contains("Row 2", twice);
      Assert.DoesNotContain("Row 1:", twice);   // the first is fine; only the repeat is named
    }

    [Fact]
    public void Unwiring_a_wire_that_is_not_there_is_refused_and_ORDER_matters_inside_a_card()
    {
      var none = Refusal(UnwireOp(3, "Result", 6, null));
      Assert.Contains("There is no wire", none);

      // wire then unwire the same pair is valid; unwire then wire is not (nothing to remove yet)
      Accept(WireOp(3, "Result", 6, null), UnwireOp(3, "Result", 6, null));
      Refusal(UnwireOp(3, "Result", 6, null), WireOp(3, "Result", 6, null));
    }

    [Fact]
    public void A_component_cannot_feed_itself_and_a_longer_loop_is_refused_too()
    {
      Assert.Contains("loop", Refusal(WireOp(3, "Result", 3, "A")));

      // 3 feeds 4 already, so 4 -> 3 closes a loop through the existing wire
      var m = Refusal(WireOp(4, "Result", 3, "B"));
      Assert.Contains("feed itself", m);
      // the control: the same shape the other way round is just a wire
      Accept(WireOp(3, "Result", 7, "A"));
    }

    [Fact]
    public void Wiring_into_an_input_that_has_a_source_APPENDS_and_the_row_says_so()
    {
      // The CANNOT half says a wire is ADDED alongside what an input already has. That is a claim about this behaviour
      // (#315), and this is its test: the declared sources are the old ones then the new one, in that order.
      var plan = Accept(WireOp(1, null, 4, "B"), WireOp(2, null, 4, "A"));
      Assert.Contains("added alongside", plan.Steps[1].Text);   // 4.A already had two sources
      var declared = plan.Declared.Wires.Single(w => w.InputGuid == G(4, 1));
      Assert.Equal(new[] { G(3, 100), G(7, 100), G(2) }, declared.Sources);
    }

    // ---- the card as a whole ----------------------------------------------------------------------------------------------

    [Fact]
    public void A_row_that_touches_something_the_same_card_deletes_is_refused_naming_both_rows()
    {
      var m = Refusal(DeleteOp(6), WireOp(3, "Result", 6, null));
      Assert.Contains("Row 2", m);
      Assert.Contains("is deleted by row 1", m);
    }

    [Fact]
    public void Deleting_the_same_component_twice_is_refused()
    {
      Assert.Contains("deleted twice", Refusal(DeleteOp(5), DeleteOp(5)));
    }

    [Fact]
    public void Every_failing_row_is_named_in_ONE_refusal_with_the_rows_as_data_too()
    {
      Assert.False(Validate(Ops(
        WireOp(3, "Result", 99, "A"),     // row 1: unknown
        DeleteOp(5),                      // row 2: fine
        DeleteOp(9),                      // row 3: retired
        WireOp(3, "Result", 4, null)),    // row 4: ambiguous input
        out _, out var error));

      Assert.Contains("3 rows do not resolve", error.Message);
      Assert.Contains("Row 1", error.Message);
      Assert.Contains("Row 3", error.Message);
      Assert.Contains("Row 4", error.Message);
      Assert.DoesNotContain("Row 2", error.Message);
      Assert.Equal(new[] { 1, 3, 4 }, ((JsonArray)error.Detail["rows"]).Select(r => (int)r["row"]).ToArray());
    }

    [Fact]
    public void A_row_past_the_twelfth_is_STILL_checked_the_bound_changes_what_is_shown_not_what_is_validated()
    {
      // #760: "the bound changes what is SHOWN, never what is checked". Thirteen distinct, valid wires, then a bad
      // fourteenth - the card shows twelve rows and collapses the rest, and the fourteenth must still refuse.
      var good = new List<JsonObject>
      {
        WireOp(1, null, 5, null), WireOp(2, null, 5, null), WireOp(7, "Result", 5, null),
        WireOp(1, null, 6, null), WireOp(2, null, 6, null), WireOp(3, "R", 6, null), WireOp(7, "Result", 6, null),
        WireOp(1, null, 4, "B"), WireOp(2, null, 4, "B"), WireOp(3, "R", 4, "B"), WireOp(7, "Result", 4, "B"),
        WireOp(1, null, 4, "A"), WireOp(2, null, 4, "A"),
      };
      Assert.Equal(13, good.Count);
      Assert.True(Validate(Ops(good.ToArray()), out var plan, out var ok), ok?.Message);   // the control: all thirteen pass
      Assert.Equal(13, plan.Steps.Count);

      good.Add(WireOp(1, null, 99, null));   // row 14, beyond what the card shows at once
      Assert.False(Validate(Ops(good.ToArray()), out _, out var error));
      Assert.Contains("Row 14", error.Message);
    }

    [Fact]
    public void Nothing_or_not_an_array_or_too_many_rows_is_refused_with_a_way_forward()
    {
      Assert.False(EditOpsAsk.Validate(null, Make().Canvas(), out _, out var none));
      Assert.Contains("needs \"ops\"", none.Message);
      Assert.False(EditOpsAsk.Validate(new JsonArray(), Make().Canvas(), out _, out _));

      var tooMany = Ops(Enumerable.Range(0, EditOpsAsk.MaxOps + 1).Select(_ => DeleteOp(5)).ToArray());
      Assert.False(EditOpsAsk.Validate(tooMany, Make().Canvas(), out _, out var big));
      Assert.Contains("Ask for the first " + EditOpsAsk.MaxOps, big.Message);   // a refusal that names the door
    }

    [Fact]
    public void A_row_that_is_not_an_object_or_names_an_unknown_op_is_refused_not_guessed_at()
    {
      Assert.False(EditOpsAsk.Validate(new JsonArray(JsonValue.Create("wire it")), Make().Canvas(), out _, out var e1));
      Assert.Contains("not an object", e1.Message);
      Assert.Contains("is not something an edit can do", Refusal(new JsonObject { ["op"] = "rename", ["id"] = 3 }));
      Assert.Contains("no \"op\"", Refusal(new JsonObject { ["id"] = 3 }));
    }

    // ---- the orphan sentences (#625/#647) -----------------------------------------------------------------------------------

    [Fact]
    public void A_delete_that_orphans_a_downstream_input_NAMES_it_in_the_plugins_words()
    {
      // Addition (3) feeds Panel (5) and, with Subtraction (7), Multiplication (4) input A. Deleting 3 leaves the panel with
      // no source at all, and leaves Multiplication's A with the one it still has from Subtraction.
      var plan = Accept(DeleteOp(3));
      var orphans = plan.Steps[0].Orphans;

      Assert.Contains("Deleting Addition (3) leaves Panel (5) with no source", orphans);
      Assert.Contains("Deleting Addition (3) cuts a wire into Multiplication (4) input A, which keeps its other 1 source", orphans);
    }

    [Fact]
    public void An_input_left_with_nothing_says_so_in_the_briefs_exact_form()
    {
      // delete Subtraction too: now Multiplication's A has no source left
      var plan = Accept(DeleteOp(3), DeleteOp(7));
      var all = plan.Steps.SelectMany(s => s.Orphans).ToList();
      Assert.Contains("Deleting Addition (3) and Subtraction (7) leave Multiplication (4) input A with no source", all);
      // on the LAST delete row - the one that completes the cut - so the sentence is true of the row it sits on
      Assert.Contains("Multiplication (4) input A", plan.Steps[1].Orphans.Single(o => o.Contains("input A")));
      Assert.DoesNotContain(plan.Steps[0].Orphans, o => o.Contains("Multiplication"));
    }

    [Fact]
    public void A_row_in_the_same_card_that_re_feeds_the_input_removes_the_orphan_claim()
    {
      // The verdict-of-the-card is computed on the whole card: wiring a slider into Panel in the same card means the
      // panel is NOT left with no source, and a sentence saying it is would be false.
      var plan = Accept(DeleteOp(3), WireOp(1, null, 5, null));
      var all = plan.Steps.SelectMany(s => s.Orphans).ToList();
      Assert.DoesNotContain(all, o => o.Contains("Panel (5) with no source"));
      Assert.Contains(all, o => o.Contains("Panel (5)") && o.Contains("keeps its other 1 source"));
    }

    [Fact]
    public void A_delete_that_cuts_nothing_that_matters_names_nothing()
    {
      // Negative (6) feeds nothing
      var plan = Accept(DeleteOp(6));
      Assert.Empty(plan.Steps[0].Orphans);
    }

    [Fact]
    public void The_orphans_of_an_unticked_delete_are_gone_with_it()
    {
      var f = Make();
      var all = Ops(DeleteOp(3), WireOp(1, null, 6, null));
      Assert.True(EditOpsAsk.Validate(all, f.Canvas(), out var full, out _));
      Assert.NotEmpty(full.Steps[0].Orphans);

      // tick only row 2 (index 1): the delete is not in the applied set, so its warnings are not either
      Assert.True(EditOpsAsk.Subset(full.Record, new[] { 1 }, out var ops, out var rows, out _));
      Assert.True(EditOpsAsk.Validate(ops, f.Canvas(), rows, out var applied, out _));
      Assert.Single(applied.Steps);
      Assert.Empty(applied.Steps.SelectMany(s => s.Orphans));
    }

    // ---- the plan: the record, the declared delta ---------------------------------------------------------------------------

    [Fact]
    public void The_record_is_the_plugins_resolved_form_with_the_canvas_spelling_of_each_port()
    {
      var plan = Accept(WireOp(3, "Result", 6, null), UnwireOp(2, null, 3, "B"), DeleteOp(5));
      Assert.Equal(3, plan.Record.Count);
      Assert.Equal("wire", (string)plan.Record[0]["op"]);
      Assert.Equal("R", (string)plan.Record[0]["from"]["param"]);   // "Result" resolved to the canvas's nickname
      Assert.Equal("X", (string)plan.Record[0]["to"]["param"]);     // a forced port, filled in by the plugin
      Assert.Null(plan.Record[1]["from"]["param"]);                 // a parameter has none to name
      Assert.Equal(5, (int)plan.Record[2]["id"]);

      // and the record re-validates to the same plan - what the click will do with it
      Assert.True(EditOpsAsk.Validate(plan.Record, Make().Canvas(), out var again, out var error), error?.Message);
      Assert.Equal(plan.Steps.Select(s => s.Text), again.Steps.Select(s => s.Text));
    }

    [Fact]
    public void Row_sentences_are_plugin_words_over_canvas_labels_and_ids()
    {
      var plan = Accept(WireOp(1, null, 4, "B"), UnwireOp(2, null, 3, "B"), DeleteOp(6));
      Assert.Equal("Wire Slider X (1) to Multiplication (4) input B", plan.Steps[0].Text);
      Assert.Equal("Remove the wire from Slider Y (2) to Addition (3) input B", plan.Steps[1].Text);
      Assert.Equal("Delete Negative (6) and every wire on it", plan.Steps[2].Text);
    }

    [Fact]
    public void The_declared_delta_lists_each_touched_input_with_its_complete_final_sources()
    {
      var plan = Accept(UnwireOp(1, null, 3, "A"), DeleteOp(7));
      Assert.Contains(G(7), plan.Declared.Removed);
      var a = plan.Declared.Wires.Single(w => w.InputGuid == G(3, 1));
      Assert.Empty(a.Sources);   // 3.A lost its only source
      var m = plan.Declared.Wires.Single(w => w.InputGuid == G(4, 1));
      Assert.Equal(new[] { G(3, 100) }, m.Sources);   // 4.A lost Subtraction's wire to the delete, kept Addition's
      Assert.DoesNotContain(plan.Declared.Wires, w => w.InputGuid == G(7, 1));   // an input on a deleted component goes with it
    }

    // ---- the subset the click applies -----------------------------------------------------------------------------------------

    [Fact]
    public void A_subset_keeps_the_card_numbers_so_a_refusal_at_the_click_names_the_row_the_user_saw()
    {
      var f = Make();
      Assert.True(EditOpsAsk.Validate(Ops(WireOp(3, "Result", 6, null), DeleteOp(5), WireOp(7, "Result", 6, null)),
        f.Canvas(), out var plan, out _));

      // between the ask and the click the canvas moves: Negative (6) is deleted by hand
      var moved = Make();
      moved.Delete(6);
      Assert.True(EditOpsAsk.Subset(plan.Record, new[] { 0, 2 }, out var ops, out var rows, out _));
      Assert.Equal(new[] { 1, 3 }, rows);
      Assert.False(EditOpsAsk.Validate(ops, MovedCanvas(moved, retired: 6), rows, out _, out var error));
      Assert.Contains("Row 1", error.Message);
      Assert.Contains("Row 3", error.Message);
      Assert.DoesNotContain("Row 2", error.Message);   // it was not ticked, so it is not mentioned
    }

    private static EditCanvas MovedCanvas(EditFixture f, int retired)
    {
      var c = f.Canvas();
      var inner = c.State;
      c.State = id => id == retired ? IdState.Retired : inner(id);
      return c;
    }

    [Fact]
    public void A_tick_for_a_row_the_card_never_had_refuses_and_nothing_is_applied()
    {
      var plan = Accept(DeleteOp(5), DeleteOp(6));
      Assert.False(EditOpsAsk.Subset(plan.Record, new[] { 0, 7 }, out _, out _, out var refusal));
      Assert.Contains("a row this card does not have", refusal);
      Assert.False(EditOpsAsk.Subset(plan.Record, new[] { -1 }, out _, out _, out _));   // ConsentTokens.Split's malformed marker
    }

    [Fact]
    public void A_row_ticked_twice_counts_once_and_no_ticks_is_an_empty_set_not_an_error()
    {
      var plan = Accept(DeleteOp(5), DeleteOp(6));
      Assert.True(EditOpsAsk.Subset(plan.Record, new[] { 1, 1, 0 }, out var ops, out var rows, out _));
      Assert.Equal(new[] { 1, 2 }, rows);
      Assert.Equal(2, ops.Count);

      Assert.True(EditOpsAsk.Subset(plan.Record, new int[0], out var none, out _, out _));
      Assert.Empty(none);
    }

    [Fact]
    public void An_unticked_row_is_absent_from_the_applied_plan_entirely()
    {
      var f = Make();
      Assert.True(EditOpsAsk.Validate(Ops(WireOp(3, "Result", 6, null), WireOp(7, "Result", 6, null), DeleteOp(5)),
        f.Canvas(), out var plan, out _));
      Assert.True(EditOpsAsk.Subset(plan.Record, new[] { 0, 2 }, out var ops, out var rows, out _));
      Assert.True(EditOpsAsk.Validate(ops, f.Canvas(), rows, out var applied, out _));

      Assert.Equal(2, applied.Steps.Count);
      Assert.DoesNotContain(applied.Steps, s => s.From?.Id == 7);
      Assert.Equal(new[] { G(3, 100) }, applied.Declared.Wires.Single(w => w.InputGuid == G(6, 1)).Sources);   // Subtraction's wire never made it
    }

    // ---- the ticks that ride the click -----------------------------------------------------------------------------------------

    [Fact]
    public void The_click_is_a_token_plus_ticks_and_a_bare_token_carries_none()
    {
      var t = "0123456789abcdef0123456789abcdef";
      Assert.Equal(t, ConsentTokens.Split(t, out var none));
      Assert.Null(none);

      Assert.Equal(t, ConsentTokens.Split(t + ":0,2,5", out var ticks));
      Assert.Equal(new[] { 0, 2, 5 }, ticks);

      Assert.Equal(t, ConsentTokens.Split(t + ":", out var empty));
      Assert.NotNull(empty);
      Assert.Empty(empty);
    }

    [Fact]
    public void A_malformed_tick_becomes_an_index_no_card_has_so_it_refuses_instead_of_narrowing()
    {
      var t = "0123456789abcdef0123456789abcdef";
      ConsentTokens.Split(t + ":0,x,2", out var ticks);
      Assert.Equal(new[] { 0, -1, 2 }, ticks);
      ConsentTokens.Split(t + ":-3", out var negative);
      Assert.Equal(new[] { -1 }, negative);
    }
  }
}
