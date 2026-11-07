using System.Collections.Generic;
using Dwight.Agent;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// The full-path drill's VERDICT (#268/#280, WP-4 Stage 0).
  ///
  /// The drill's wiring is GH-coupled and cannot be tested; its DECISION can, and the decision is the
  /// half that has gone wrong before - #249's drill printed pass criteria beside a result that satisfied
  /// none of them, and #268's passed while the product hung. So every clause that makes a pass a pass is
  /// asserted here, and each one is present because its absence produced a false green somewhere in this
  /// phase.
  /// </summary>
  public class FullPathDrillTests
  {
    private const string Ok = "{\"ok\":true,\"result\":{}}";
    private const string Refused =
      "{\"ok\":false,\"error\":{\"code\":\"precondition_refused\",\"message\":\"not available on this turn\"}}";

    private static FullPathObservations Healthy() => new FullPathObservations
    {
      Entered = true,
      ScriptSteps = 3,
      ProviderCalls = 3,
      Outcome = "Complete",
      AnswerRecorded = true,
      ToolResults = new List<(string, string)> { ("get_overview", Ok), ("build_region", Ok) },
    };

    [Fact]
    public void A_clean_run_is_MEASURED()
    {
      var verdict = FullPathDrill.Decide(Healthy(), out var report);

      Assert.Equal(DrillVerdict.Measured, verdict);
      Assert.Contains("MEASURED", report);
      Assert.Contains("PASS", report);
    }

    /// <summary>THE CASE THIS DRILL EXISTS FOR (#273/#276): the turn completes, the record is written,
    /// and nothing reaches the surface. Everything else about the run looks perfect - which is precisely
    /// why the verdict must not read the outcome as a pass.</summary>
    [Fact]
    public void A_completed_turn_that_rendered_NOTHING_is_not_a_pass()
    {
      var o = Healthy();
      o.AnswerRecorded = false;

      var verdict = FullPathDrill.Decide(o, out var report);

      Assert.Equal(DrillVerdict.NotMeasured, verdict);
      Assert.Contains("NOT MEASURED", report);
      Assert.Contains("ANSWER NEVER REACHED THE THREAD", report);
      Assert.DoesNotContain("this is a PASS", report);
    }

    /// <summary>#249's class: a refused tool lets the turn finish perfectly well while the leg the drill
    /// exists to walk never runs. "It returned" is not evidence for a path it short-circuited.</summary>
    [Fact]
    public void A_refused_tool_is_not_a_pass_even_when_the_turn_completes()
    {
      var o = Healthy();
      o.ToolResults = new List<(string, string)> { ("get_overview", Ok), ("build_region", Refused) };

      var verdict = FullPathDrill.Decide(o, out var report);

      Assert.Equal(DrillVerdict.NotMeasured, verdict);
      Assert.Contains("build_region", report);
      Assert.Contains("REFUSED", report);
      Assert.Contains("precondition_refused", report);
    }

    /// <summary>An envelope that cannot be read is not a success. A drill must never read an absence as
    /// a pass - the one habit that produces a green measuring nothing.</summary>
    [Fact]
    public void An_unreadable_envelope_is_not_a_success()
    {
      var o = Healthy();
      o.ToolResults = new List<(string, string)> { ("get_overview", "not json at all") };

      Assert.Equal(DrillVerdict.NotMeasured, FullPathDrill.Decide(o, out var report));
      Assert.Contains("unreadable envelope", report);
    }

    /// <summary>A loop that stopped after one call has not looped, so the steps after it were never
    /// walked - and the drill's whole claim is about the legs, not the arrival.</summary>
    [Fact]
    public void A_loop_that_consumed_fewer_steps_than_the_script_is_not_a_pass()
    {
      var o = Healthy();
      o.ProviderCalls = 1;

      Assert.Equal(DrillVerdict.NotMeasured, FullPathDrill.Decide(o, out var report));
      Assert.Contains("stopped before the script did", report);
    }

    /// <summary>The other direction: the loop outran the script, so whatever it did after the last
    /// scripted step is unknown - and unknown is never a pass.</summary>
    [Fact]
    public void A_loop_that_outran_the_script_is_not_a_pass()
    {
      var o = Healthy();
      o.ProviderCalls = 5;

      Assert.Equal(DrillVerdict.NotMeasured, FullPathDrill.Decide(o, out var report));
      Assert.Contains("ran out of script", report);
    }

    [Fact]
    public void An_outcome_other_than_Complete_is_not_a_pass()
    {
      var o = Healthy();
      o.Outcome = "Failed";

      Assert.Equal(DrillVerdict.NotMeasured, FullPathDrill.Decide(o, out var report));
      Assert.Contains("not Complete", report);
    }

    [Fact]
    public void A_timeout_is_not_a_pass()
    {
      var o = Healthy();
      o.TimedOut = true;

      Assert.Equal(DrillVerdict.NotMeasured, FullPathDrill.Decide(o, out var report));
      Assert.Contains("timed out", report);
    }

    /// <summary>#249: a drill that never entered measured nothing, and must say so rather than reporting
    /// on fields that were never populated.</summary>
    [Fact]
    public void A_run_that_never_entered_is_not_a_pass()
    {
      var verdict = FullPathDrill.Decide(new FullPathObservations { Entered = false }, out var report);

      Assert.Equal(DrillVerdict.NotMeasured, verdict);
      Assert.Contains("never entered", report);
    }

    [Fact]
    public void No_observations_at_all_is_not_a_pass()
    {
      Assert.Equal(DrillVerdict.NotMeasured, FullPathDrill.Decide(null, out var report));
      Assert.Contains("NOT MEASURED", report);
    }

    /// <summary>#280: the coverage statement rides EVERY verdict, pass or fail. A bound that only appears
    /// on a failure is a bound nobody reads on the run they are about to trust.</summary>
    [Fact]
    public void Every_verdict_states_what_it_does_not_cover()
    {
      FullPathDrill.Decide(Healthy(), out var pass);
      var failing = Healthy();
      failing.AnswerRecorded = false;
      FullPathDrill.Decide(failing, out var fail);

      Assert.Contains("NOT COVERED", pass);
      Assert.Contains("NOT COVERED", fail);
      Assert.Contains("PAINTED", pass);
      Assert.Contains("check-shell-render.js", pass);
    }

    /// <summary>The facts are printed whichever way the verdict lands, so a reader can see what was
    /// observed rather than only the conclusion drawn from it.</summary>
    [Fact]
    public void The_observations_are_printed_on_a_failure_too()
    {
      var o = Healthy();
      o.Outcome = "TimedOut";
      o.Detail = "the thread's tail";

      FullPathDrill.Decide(o, out var report);

      Assert.Contains("provider calls:   3 of 3", report);
      Assert.Contains("tool build_region: ok", report);
      Assert.Contains("the thread's tail", report);
    }
  }
}
