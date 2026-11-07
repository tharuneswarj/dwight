using System;
using System.Collections.Generic;
using System.Linq;
using Dwight.Agent;
using Xunit;
using static Dwight.Agent.Tests.EditFixture;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// U48 (the second U6 gate run): the edit click refused "row 1 names a port that is no longer there" for a Number Slider
  /// wired into a Construct Point's X input, by nickname and by full name, while the card had accepted the row.
  ///
  /// THE CAUSE, reproduced here without Grasshopper: the apply resolved each port by its guid with a lookup that answers
  /// for TOP-LEVEL OBJECTS. A parameter on the canvas (the slider) is one; a component's input param is not. The fixture is
  /// built the way the live canvas is - a component's ports have their own guids, distinct from the component's - so the
  /// old lookup is modelled (<see cref="TopLevelOnly"/>) and shown to fail on exactly the rows the gate failed, with the
  /// new index as the positive control (#212): the same rows pass through it.
  ///
  /// WHAT THESE CANNOT WITNESS: that <c>GhEditOps.PortIndexOf</c> walks a real document into the same guids the fixture
  /// assumes, and that the wire then lands. Both are checked by hand in Rhino before a release.
  /// </summary>
  public class EditPortIndexTests
  {
    /// <summary>The live canvas with a Construct Point: input "X coordinate" (nickname "X"), as the gate document had it.</summary>
    private static EditFixture WithConstructPoint()
    {
      var f = EditFixture.Make();
      f.Nodes[11] = new EditNode
      {
        Id = 11, Guid = G(11), Label = "CP", IsParam = false,
        Inputs = new List<EditPort>
        {
          new EditPort { Name = "X coordinate", Nick = "X", Guid = G(11, 1) },
          new EditPort { Name = "Y coordinate", Nick = "Y", Guid = G(11, 2) },
          new EditPort { Name = "Z coordinate", Nick = "Z", Guid = G(11, 3) },
        },
        Outputs = new List<EditPort> { new EditPort { Name = "Point", Nick = "Pt", Guid = G(11, 100) } },
      };
      return f;
    }

    /// <summary>The lookup that failed the gate: only an object's own guid resolves, never a port inside a component.</summary>
    private static Func<Guid, bool> TopLevelOnly(EditFixture f)
    {
      var tops = new HashSet<Guid>(f.Canvas().Nodes.Values.Select(n => n.Guid));
      return tops.Contains;
    }

    private static EditPlan Plan(EditFixture f, params System.Text.Json.Nodes.JsonObject[] ops)
    {
      Assert.True(EditOpsAsk.Validate(Ops(ops), f.Canvas(), out var plan, out var error), error?.Message);
      return plan;
    }

    [Theory]
    [InlineData("X")]
    [InlineData("X coordinate")]
    public void A_slider_into_a_component_input_resolves_by_nickname_or_full_name(string spelling)
    {
      var f = WithConstructPoint();
      var plan = Plan(f, WireOp(1, null, 11, spelling));   // "Wire S (1) to CP (11) input X"
      var index = EditPortCheck.FromCanvas(f.Canvas());

      Assert.Null(EditPortCheck.FirstUnresolved(plan, g => index.Find(g) != null));
    }

    [Fact]
    public void The_lookup_that_failed_the_gate_does_fail_on_this_row_and_the_sentence_is_true()
    {
      var f = WithConstructPoint();
      var plan = Plan(f, WireOp(1, null, 11, "X"));

      // the source (a slider) resolves under the old lookup; the component's input does not - the gate's exact shape
      Assert.True(TopLevelOnly(f)(plan.Steps[0].FromPort.Guid));
      Assert.False(TopLevelOnly(f)(plan.Steps[0].ToPort.Guid));

      var said = EditPortCheck.FirstUnresolved(plan, TopLevelOnly(f));
      Assert.NotNull(said);
      Assert.Contains("row 1", said);
      Assert.Contains("input port X on CP (11)", said);
      Assert.DoesNotContain("no longer there", said);
    }

    [Fact]
    public void Every_port_the_validator_can_name_resolves_through_the_index()
    {
      var f = WithConstructPoint();
      var index = EditPortCheck.FromCanvas(f.Canvas());
      foreach (var n in f.Canvas().Nodes.Values)
        foreach (var p in n.Inputs.Concat(n.Outputs))
          Assert.Same(p, index.Find(p.Guid));
    }

    [Fact]
    public void An_unwire_row_and_a_component_to_component_row_resolve_too()
    {
      var f = WithConstructPoint();
      var plan = Plan(f, UnwireOp(1, null, 3, "A"), WireOp(3, "Result", 11, "Y coordinate"));
      var index = EditPortCheck.FromCanvas(f.Canvas());

      Assert.Null(EditPortCheck.FirstUnresolved(plan, g => index.Find(g) != null));
    }

    [Fact]
    public void A_port_that_really_is_gone_is_named_by_row_end_port_and_owner()
    {
      var f = WithConstructPoint();
      var plan = Plan(f, WireOp(2, null, 11, "X"), WireOp(1, null, 11, "Y"));
      var gone = plan.Steps[1].ToPort.Guid;
      var index = EditPortCheck.FromCanvas(f.Canvas());

      var said = EditPortCheck.FirstUnresolved(plan, g => g != gone && index.Find(g) != null);

      Assert.NotNull(said);
      Assert.Contains("row 2's input port Y on CP (11)", said);   // row 1 resolved, so row 2 is the one named
      Assert.DoesNotContain("row 1", said);
    }

    [Fact]
    public void A_delete_row_names_no_port_so_it_is_never_refused_for_one()
    {
      var f = WithConstructPoint();
      var plan = Plan(f, DeleteOp(11));

      Assert.Null(EditPortCheck.FirstUnresolved(plan, g => false));
    }
  }
}
