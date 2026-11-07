using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;
using Dwight.Agent;

namespace Dwight.Agent.Tests
{
  /// <summary>Brief BH, from the second gate's AR, AV and H rows: a reverted spot build is taken back properly (U108), an approval the plugin
  /// applies is followed up under an automatic mode when it does not land (U105), an empty input is named even when the script does not raise
  /// (U106), and "Raising the card" is an announcement (U107).</summary>
  [Collection(TurnShapeTests.StaticRecord)]   // touches TurnShape's static counters (Clear, Examined, AnnouncedChangeCount): never in parallel with the classes that read them; main went red on the #222 merge with a count of 3 for 1
  public class SpotRevertTests
  {
    // ---------------------------------------------------------------- U108

    [Fact]
    public void A_builds_revert_marks_the_record_first_and_the_guarded_path_removes_the_record_too()
    {
      Assert.Equal(new[] { BuildRevertPlan.Step.MarkTakenBack, BuildRevertPlan.Step.Undo }, BuildRevertPlan.For(referencesMissing: false));
      Assert.Equal(new[] { BuildRevertPlan.Step.MarkTakenBack, BuildRevertPlan.Step.RemoveObjects, BuildRevertPlan.Step.RemoveRecord },
        BuildRevertPlan.For(referencesMissing: true));
    }

    [Fact]
    public void UndoOnce_follows_the_plan_on_both_paths()
    {
      var gh = Read("Dwight", "Agent", "GhBuildRegion.cs");
      var body = Between(gh, "private static string UndoOnce(GH_Document doc, RevertKit kit)", "private static Dictionary<Guid, string> NamesOf(");
      var mark = body.IndexOf("var marked = Dwight.Session.ChangeLog.Reverting(doc);", StringComparison.Ordinal);
      var branch = body.IndexOf("if (missing != null)", StringComparison.Ordinal);
      Assert.True(mark > 0 && mark < branch, "the record is marked as taken back before either path");
      var guarded = Between(body, "if (missing != null)", "else");
      var removeObjects = guarded.IndexOf("doc.RemoveObject(o, false)", StringComparison.Ordinal);
      var removeRecord = guarded.IndexOf("doc.UndoServer?.RemoveRecord(marked)", StringComparison.Ordinal);
      Assert.True(removeObjects > 0 && removeRecord > removeObjects, "the guarded path removes what was placed, then the dead record");
      Assert.Contains("if (marked != Guid.Empty)", guarded);   // only a record that is Dwight's and on top is ever removed
      Assert.Contains("try { doc.Undo(); } catch { }", Between(body, "else", "Instances.RedrawCanvas()"));
    }

    [Fact]
    public void Reverting_returns_the_record_it_marked()
    {
      var log = Read("Dwight", "Session", "ChangeLog.cs");
      Assert.Contains("public static Guid Reverting(GH_Document doc)", log);
      Assert.Contains("return top[0];", log);
    }

    // ---------------------------------------------------------------- U105

    [Fact]
    public void Only_an_automatic_approval_that_did_not_land_is_followed_up()
    {
      Assert.True(AutoFollowUp.AfterApplied(approvedAutomatically: true, landed: false));
      Assert.False(AutoFollowUp.AfterApplied(approvedAutomatically: true, landed: true));
      Assert.False(AutoFollowUp.AfterApplied(approvedAutomatically: false, landed: false));   // a click: the user is there to read it
      Assert.False(AutoFollowUp.AfterApplied(approvedAutomatically: false, landed: true));
      Assert.Equal("Dwight is continuing after the revert.", AutoFollowUp.Line);
    }

    [Fact]
    public void Every_approval_the_plugin_applies_runs_the_decision_when_it_does_not_land()
    {
      var cs = Read("Dwight", "UI", "DwightScaffold.cs");
      Assert.Contains("ApplySpot(wp, doc, threadId, plan, autoMode != null);", cs);
      var spot = Between(cs, "private static void ApplySpot(", "private static void OnExplainRegionClick(");
      Assert.Equal(2, Regex.Matches(spot, Regex.Escape("FollowUpIfAuto(wp, doc, threadId, autoApproved, landed: false);")).Count);   // the guard and the failed verdict
      var edit = Between(cs, "case ConsentRoute.Edit:", "case ConsentRoute.SetValue:");
      Assert.Contains("else FollowUpIfAuto(wp, doc, threadId, autoMode != null, landed: false);", edit);
      var script = Between(cs, "case ConsentRoute.EditScript:", "case ConsentRoute.Edit:");
      Assert.Contains("FollowUpIfAuto(wp, doc, threadId, autoMode != null, ok);", script);
      var helper = Between(cs, "private static void FollowUpIfAuto(", "private static void ApplySpot(");
      Assert.Contains("if (!AutoFollowUp.AfterApplied(autoApproved, landed)) return;", helper);
      Assert.Contains("RunTurn(wp, doc, threadId, TurnCapability.ReadOnly);", helper);
    }

    // ---------------------------------------------------------------- U106

    private static ScriptBuildFacts Facts(string[] empty, string[] unwired) => new ScriptBuildFacts
    {
      Ref = "s1", Name = "Squares", Language = "python",
      Inputs = new[] { "x", "y" }, UnwiredInputs = unwired, EmptyInputs = empty,
      Outputs = new[] { new ScriptOutputRead { Port = "a", Items = 0, NonNull = 0 } },
    };

    [Fact]
    public void A_guarded_script_that_returned_nothing_on_an_empty_input_is_told_the_input_was_empty()
    {
      var j = ScriptBuildVerdict.Judge(new[] { Facts(new[] { "x" }, new string[0]) }, null);
      Assert.False(j.Stands);
      Assert.Contains("Script \"s1\" (Squares): its output a holds no data. Its input x read no data: the sources upstream hold nothing to compute with; Diagnose can say why.", j.Detail);
      Assert.DoesNotContain("returned nothing for what it was given", j.Detail);
      Assert.EndsWith("The build was reverted; the canvas is as it was.", j.Refusal);
      Assert.DoesNotContain("fix the code", j.Refusal);   // the code was not the reason
      Assert.Equal("Its inputs x, y read no data: the sources upstream hold nothing to compute with; Diagnose can say why.",
        ScriptBuildVerdict.EmptyInputsWhileRunning(new[] { "x", "y" }));
    }

    [Fact]
    public void Returned_nothing_for_what_it_was_given_stays_only_when_every_input_held_data()
    {
      var j = ScriptBuildVerdict.Judge(new[] { Facts(new string[0], new string[0]) }, null);
      Assert.Contains("Every input is wired, so the code returned nothing for what it was given.", j.Detail);
      Assert.Contains("fix the code", j.Refusal);
      // an unwired input is still named first, as before
      var unwired = ScriptBuildVerdict.Judge(new[] { Facts(new[] { "x" }, new[] { "y" }) }, null);
      Assert.Contains("Nothing is wired to its input y.", unwired.Detail);
    }

    // ---------------------------------------------------------------- U107

    [Theory]
    [InlineData("Raising the scaffold card first for one region named \"Squares\", then I'll build it.")]
    [InlineData("I'll raise the build card for region A1 now.")]
    [InlineData("Let me raise a card for that change.")]
    [InlineData("Raise the map for the three regions first.")]
    public void Announcing_a_card_and_raising_none_is_caught(string message)
    {
      Assert.True(TurnShape.AnnouncedIntentWithoutCard(message, mutationAvailable: true, lastStepToolCalls: 0, requestCalls: 0));
    }

    [Theory]
    [InlineData("Let me explain what the definition does.")]
    [InlineData("I'll summarise the regions for you.")]
    public void A_read_or_an_answer_is_still_not_an_announcement(string message)
    {
      Assert.False(TurnShape.AnnouncedIntentWithoutCard(message, mutationAvailable: true, lastStepToolCalls: 0, requestCalls: 0));
    }

    // ---------------------------------------------------------------- helpers

    private static string Between(string s, string from, string to)
    {
      var a = s.IndexOf(from, StringComparison.Ordinal); Assert.True(a >= 0, "missing: " + from);
      var b = s.IndexOf(to, a + from.Length, StringComparison.Ordinal); Assert.True(b > a, "missing after it: " + to);
      return s.Substring(a, b - a);
    }

    private static string Read(params string[] parts)
    {
      var dir = AppContext.BaseDirectory;
      while (dir != null && !File.Exists(Path.Combine(dir, "Dwight.sln"))) dir = Path.GetDirectoryName(dir);
      Assert.NotNull(dir);
      return File.ReadAllText(Path.Combine(new[] { dir }.Concat(parts).ToArray()));
    }
  }
}
