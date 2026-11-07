using System;
using System.Collections.Generic;
using Dwight.Agent;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// U47, second pass: a fed script that produced nothing left the document inside a solution and the click silent.
  ///
  /// WHAT THESE WITNESS (GH-free, ADR-091): (1) the null-item fixture - the script's output holds ONE item and it is
  /// nothing - judged through the verdict as EMPTY from the data in hand, with no solve to wait for; (2) the report latch,
  /// so a verdict and a watchdog can never both speak or both revert; (3) the watchdog's timeout going THROUGH the guard
  /// and yielding a report line, reverting only when the build is still standing.
  ///
  /// WHAT THEY CANNOT WITNESS: that Grasshopper delivers the timer on the UI thread while a document sits inside a
  /// solution, nor which statement the original click was suspended in. Both are the gate's row 5 on a live canvas.
  /// </summary>
  public class SpotWatchdogTests
  {
    private static ScriptBuildFacts Fed(int items, int nonNull) => new ScriptBuildFacts
    {
      Ref = "k", Name = "k", Language = "python",
      Inputs = new[] { "x" }, UnwiredInputs = new string[0],            // fed
      Outputs = new[] { new ScriptOutputRead { Port = "a", Items = items, NonNull = nonNull } },
    };

    // ---- the verdict, from the data in hand ------------------------------------------------------------------------

    [Fact]
    public void A_fed_script_whose_only_output_item_is_nothing_is_judged_empty_and_refused()
    {
      var j = ScriptBuildVerdict.Judge(new[] { Fed(items: 1, nonNull: 0) }, null);

      Assert.False(j.Stands);
      Assert.Equal(ScriptBuildOutcome.Empty, j.Outcomes["k"]);
      Assert.Contains("its output a holds no data", j.Refusal);
      Assert.Contains("Every input is wired", j.Refusal);
    }

    [Fact]
    public void The_same_script_with_one_real_item_stands_positive_control()
    {
      var j = ScriptBuildVerdict.Judge(new[] { Fed(items: 2, nonNull: 1) }, null);

      Assert.True(j.Stands);
      Assert.Equal(ScriptBuildOutcome.Produced, j.Outcomes["k"]);
    }

    [Fact]
    public void An_unfed_script_with_the_same_null_item_is_unexercised_not_refused()
    {
      var f = Fed(1, 0);
      f.UnwiredInputs = new[] { "x" };

      var j = ScriptBuildVerdict.Judge(new[] { f }, null);

      Assert.True(j.Stands);
      Assert.Equal(ScriptBuildOutcome.Unexercised, j.Outcomes["k"]);
    }

    // ---- the latch ------------------------------------------------------------------------------------------------------

    [Fact]
    public void Only_the_first_claim_on_the_report_succeeds()
    {
      var latch = new SpotClickGuard.ReportLatch();

      Assert.False(latch.Delivered);
      Assert.True(latch.TryClaim());
      Assert.True(latch.Delivered);
      Assert.False(latch.TryClaim());   // a verdict arriving after the watchdog, or the other way round
    }

    // ---- the watchdog's timeout goes through the guard ----------------------------------------------------------------

    [Fact]
    public void A_timeout_reverts_a_standing_build_and_reports_in_the_guards_words()
    {
      var lines = new List<string>();
      var reverts = 0;

      var completed = SpotClickGuard.TimedOut(30, () => true, () => reverts++, lines.Add);

      Assert.False(completed);
      Assert.Equal(1, reverts);
      var line = Assert.Single(lines);
      Assert.Contains("the check did not finish within 30 seconds", line);
      Assert.Contains("The build was reverted", line);
    }

    [Fact]
    public void A_timeout_on_a_build_that_is_not_standing_reports_without_a_second_undo()
    {
      var lines = new List<string>();
      var reverts = 0;

      SpotClickGuard.TimedOut(30, () => false, () => reverts++, lines.Add);

      Assert.Equal(0, reverts);   // a double undo would take back the user's own previous step
      Assert.Equal("Dwight hit a problem while checking the build: the check did not finish within 30 seconds. The canvas is as it was.",
        Assert.Single(lines));
    }

    [Fact]
    public void A_timeout_whose_revert_fails_says_so_and_never_claims_the_revert()
    {
      var lines = new List<string>();

      SpotClickGuard.TimedOut(30, () => true, () => throw new InvalidOperationException("no undo"), lines.Add);

      var line = Assert.Single(lines);
      Assert.Contains("press Ctrl-Z once", line);
      Assert.DoesNotContain("was reverted", line);
    }

    [Fact]
    public void A_report_door_that_throws_never_escapes_the_timeout()
    {
      var ex = Record.Exception(() =>
        SpotClickGuard.TimedOut(30, () => true, () => { }, _ => throw new InvalidOperationException("door shut")));

      Assert.Null(ex);
    }

    [Fact]
    public void A_body_that_completes_reports_nothing_positive_control()
    {
      var lines = new List<string>();

      var completed = SpotClickGuard.Run(() => { }, () => true, () => lines.Add("reverted"), lines.Add);

      Assert.True(completed);
      Assert.Empty(lines);
    }
  }
}
