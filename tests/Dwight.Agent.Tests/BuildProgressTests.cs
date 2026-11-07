using System;
using System.Collections.Generic;
using System.Linq;
using Dwight.Agent;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// WP-4 Stage C: the running summary (#96) and the record behind it.
  ///
  /// #96 ruled build-then-report with no per-region pause - the user consented once by naming the regions,
  /// and control comes from watching it happen, plus Stop, plus one Ctrl-Z per step. So each region
  /// reports as it lands, and the record is what lets a reopened thread still say what was built.
  /// </summary>
  public class BuildProgressTests
  {
    private static string Merge(string existing, string address, RegionOutcome outcome, int components = 0,
                                string note = null) =>
      BuildProgress.Merge(existing, address, outcome, components, note, "2026-08-24T00:00:00Z");

    [Fact]
    public void A_regions_outcome_is_recorded_and_reads_back()
    {
      var json = Merge(null, "A1", RegionOutcome.Built, 6);

      var read = BuildProgress.Read(json).Single();

      Assert.Equal("A1", read.Address);
      Assert.Equal(RegionOutcome.Built, read.Outcome);
      Assert.Equal(6, read.Components);
    }

    /// <summary>A region has ONE current state. A rebuild supersedes the earlier attempt rather than
    /// appending to it - a record listing A1 as both built and reverted would be a history of attempts
    /// pretending to be a description of the definition, and the reader who needs this most is a reopened
    /// thread asking what is actually there.</summary>
    [Fact]
    public void A_second_attempt_on_one_region_SUPERSEDES_the_first()
    {
      var json = Merge(Merge(null, "A1", RegionOutcome.Reverted), "A1", RegionOutcome.Built, 4);

      var all = BuildProgress.Read(json);

      Assert.Single(all);
      Assert.Equal(RegionOutcome.Built, all[0].Outcome);
      Assert.Equal(4, all[0].Components);
    }

    [Fact]
    public void Several_regions_are_kept_and_read_back_in_ADDRESS_order()
    {
      var json = Merge(Merge(Merge(null, "B1", RegionOutcome.Built), "A10", RegionOutcome.Built), "A2", RegionOutcome.Built);

      Assert.Equal(new[] { "A2", "A10", "B1" }, BuildProgress.Read(json).Select(r => r.Address).ToArray());
    }

    [Fact]
    public void A_corrupt_or_absent_record_starts_a_new_one_rather_than_throwing()
    {
      Assert.Empty(BuildProgress.Read(null));
      Assert.Empty(BuildProgress.Read("{ not json"));
      Assert.Single(BuildProgress.Read(Merge("{ not json", "A1", RegionOutcome.Built)));
    }

    [Fact]
    public void The_record_is_bounded_and_drops_the_OLDEST()
    {
      var json = (string)null;
      for (var i = 1; i <= BuildProgress.MaxRegions + 2; i++) json = Merge(json, "A" + i, RegionOutcome.Built);

      var all = BuildProgress.Read(json);

      Assert.Equal(BuildProgress.MaxRegions, all.Count);
      Assert.DoesNotContain(all, r => r.Address == "A1");   // the oldest went
      Assert.Contains(all, r => r.Address == "A" + (BuildProgress.MaxRegions + 2));
    }

    // ---- the line the user reads -------------------------------------------------------------------

    [Fact]
    public void A_built_region_reports_what_landed_and_that_it_is_undoable()
    {
      var line = BuildProgress.LineHtml("A1", RegionOutcome.Built, 6, null);

      Assert.Contains("A1", line);
      Assert.Contains("built - 6 components", line);   // U26: never the bare word "verified"
      Assert.False(BuildPlacedFacts.SaysVerified(line), line);
      Assert.Contains("6 components", line);
      Assert.Contains("Ctrl-Z", line);
    }

    /// <summary>THE ONE THAT MUST NOT BE SOFTENED. A region that built and carries errors is neither a
    /// success nor a failure, and reporting it as done would be the surface claiming a state the canvas
    /// contradicts. It says so, and says WHY it is left standing.</summary>
    [Fact]
    public void A_built_but_erroring_region_is_NOT_reported_as_done()
    {
      var line = BuildProgress.LineHtml("A2", RegionOutcome.BuiltWithErrors, 3, null);

      Assert.Contains("reports errors", line);
      Assert.Contains("left standing", line);
      Assert.DoesNotContain("built - ", line);
    }

    [Fact]
    public void A_reverted_region_says_the_canvas_is_unchanged()
    {
      var line = BuildProgress.LineHtml("A3", RegionOutcome.Reverted, 0, null);

      Assert.Contains("REVERTED", line);
      Assert.Contains("as it was", line);
    }

    /// <summary>The note is the tool's own words about what went wrong, and it reaches a surface that
    /// injects HTML - so it is escaped here, at the destination that needs it (#335).</summary>
    [Fact]
    public void A_note_reaching_the_chat_is_escaped()
    {
      var line = BuildProgress.LineHtml("A1", RegionOutcome.Reverted, 0, "<script>alert(1)</script>");

      Assert.DoesNotContain("<script>", line);
      Assert.Contains("&lt;script&gt;", line);
    }

    /// <summary>A region's ADDRESS is model-adjacent text on its way to insertAdjacentHTML too.</summary>
    [Fact]
    public void An_address_reaching_the_chat_is_escaped()
    {
      var line = BuildProgress.LineHtml("<b>A1</b>", RegionOutcome.Built, 1, null);

      Assert.DoesNotContain("<b>", line);
    }
    // ---- the recovery instrument (#143/#212), built BEFORE the question is asked -------------------

    /// <summary>
    /// WP-0's whole lesson: a repair loop can fire 56 times and recover 0, and we only knew because the
    /// firings were counted. WP-4's loop has a runtime message for an oracle - which #94 says SHOULD be
    /// actionable - and "should" is a hypothesis. So it is instrumented from its first run.
    /// </summary>
    [Fact]
    public void A_retry_that_turned_a_broken_region_CLEAN_reads_as_a_recovery()
    {
      var json = Merge(Merge(null, "A1", RegionOutcome.BuiltWithErrors), "A1", RegionOutcome.Built, 5);

      var row = BuildProgress.Rows(json).Single();

      Assert.Equal(2, row.Attempts);
      Assert.Equal(RegionOutcome.BuiltWithErrors, row.FirstOutcome);
      Assert.True(row.Recovered);
    }

    /// <summary>#212 IN ADVANCE: the counter must be able to register BOTH outcomes, or a zero from it
    /// would mean nothing. A retry that did NOT fix the region reads as no recovery - a distinct, reachable
    /// value rather than the only one the instrument can produce.</summary>
    [Fact]
    public void A_retry_that_did_NOT_fix_the_region_is_not_a_recovery()
    {
      var json = Merge(Merge(null, "A1", RegionOutcome.BuiltWithErrors), "A1", RegionOutcome.BuiltWithErrors);

      var row = BuildProgress.Rows(json).Single();

      Assert.Equal(2, row.Attempts);
      Assert.False(row.Recovered);
    }

    /// <summary>A region that was clean first time never needed recovering, so counting it would inflate
    /// the rate the instrument exists to measure honestly.</summary>
    [Fact]
    public void A_region_that_was_clean_first_time_is_not_a_recovery()
    {
      var row = BuildProgress.Rows(Merge(null, "A1", RegionOutcome.Built, 3)).Single();

      Assert.Equal(1, row.Attempts);
      Assert.False(row.Recovered);
    }

    [Fact]
    public void The_FIRST_outcome_survives_every_later_attempt()
    {
      var json = Merge(Merge(Merge(null, "A1", RegionOutcome.Reverted), "A1", RegionOutcome.BuiltWithErrors), "A1", RegionOutcome.Built);

      var row = BuildProgress.Rows(json).Single();

      Assert.Equal(3, row.Attempts);
      Assert.Equal(RegionOutcome.Reverted, row.FirstOutcome);   // not the second attempt's
      Assert.True(row.Recovered);
    }

    // ---- the closing report (#223) -----------------------------------------------------------------

    /// <summary>THE ONE THAT MUST NEVER BE SILENT. A region that was approved and never attempted produces
    /// no running-summary line at all, and silence is exactly how a half-finished build reads as a
    /// finished one.</summary>
    private static string CloseAfter(params RegionOutcome[] attempts)
    {
      var turn = new Dictionary<string, RegionOutcome>();
      foreach (var a in attempts) BuildProgress.FoldTurn(turn, "B1", a);
      return BuildProgress.ClosingHtml(new[] { "B1" }, turn, stoppedEarly: false);
    }

    /// <summary>U76, the observed sequence: B1 built, then two refused rebuilds. The close read "could not be built: B1".</summary>
    [Fact]
    public void U76_A_standing_build_stays_built_and_checked_after_refused_rebuilds()
    {
      var html = CloseAfter(RegionOutcome.Built, RegionOutcome.Refused, RegionOutcome.Refused);
      Assert.Contains("built and checked (placed as declared, nothing existing changed, no new errors): B1", html);
      Assert.DoesNotContain("could not be built", html);
    }

    [Fact]
    public void U76_A_standing_build_with_errors_stays_so_after_a_refusal()
    {
      var html = CloseAfter(RegionOutcome.BuiltWithErrors, RegionOutcome.Refused);
      Assert.Contains("built but still reporting errors: B1", html);
    }

    [Fact]
    public void U76_A_verified_rebuild_reads_built_and_a_reverted_rebuild_reads_reverted()
    {
      Assert.Contains("no new errors): B1", CloseAfter(RegionOutcome.Built, RegionOutcome.Built));
      Assert.Contains("no new errors): B1", CloseAfter(RegionOutcome.Reverted, RegionOutcome.Built));
      var reverted = CloseAfter(RegionOutcome.Built, RegionOutcome.Reverted);
      Assert.Contains("B1 could not be built", reverted);
    }

    [Fact]
    public void U76_A_region_that_never_stood_reads_could_not_be_built()
    {
      Assert.Contains("B1 could not be built", CloseAfter(RegionOutcome.Refused, RegionOutcome.Refused));
      Assert.Contains("B1 could not be built", CloseAfter(RegionOutcome.Reverted, RegionOutcome.Refused));
      Assert.Equal(RegionOutcome.Refused, BuildProgress.Standing(null, RegionOutcome.Refused));
    }

    [Fact]
    public void An_approved_region_that_was_NEVER_ATTEMPTED_is_named_in_the_close()
    {
      var html = BuildProgress.ClosingHtml(new[] { "A1", "A2", "A3" }, Turn(("A1", RegionOutcome.Built)), stoppedEarly: true);

      Assert.Contains("not attempted: A2, A3", html);
      Assert.Contains("Stopped before finishing", html);
    }

    [Fact]
    public void A_clean_finish_names_what_was_built_and_says_it_finished()
    {
      var html = BuildProgress.ClosingHtml(new[] { "A1", "A2" },
        Turn(("A1", RegionOutcome.Built), ("A2", RegionOutcome.Built)), stoppedEarly: false);

      Assert.Contains("Build finished", html);
      // U26: the close says what the check WAS, never the bare word.
      Assert.Contains("built and checked (placed as declared, nothing existing changed, no new errors): A1, A2", html);
      Assert.False(BuildPlacedFacts.SaysVerified(html), html);
      Assert.DoesNotContain("not attempted", html);
    }

    /// <summary>The three imperfect outcomes are separated, because "built but erroring" and "could not be
    /// built" call for different actions from the user.</summary>
    [Fact]
    public void The_close_separates_built_from_erroring_from_failed()
    {
      var html = BuildProgress.ClosingHtml(new[] { "A1", "A2", "A3" },
        Turn(("A1", RegionOutcome.Built), ("A2", RegionOutcome.BuiltWithErrors), ("A3", RegionOutcome.Reverted)), false);

      Assert.Contains("no new errors): A1", html);
      Assert.Contains("still reporting errors: A2", html);
      Assert.Contains("could not be built: A3", html);
      Assert.Contains("Nothing was reverted behind your back", html);
    }

    /// <summary>
    /// #611e, THE GATE'S OWN CASE: an approved build that attempted nothing closed "Build finished. not
    /// attempted: A1" - a headline asserting a state the system did not reach (#253).
    /// </summary>
    [Fact]
    public void Nothing_attempted_never_headlines_Build_finished()
    {
      var html = BuildProgress.ClosingHtml(new[] { "A1" }, Turn(), stoppedEarly: false);

      Assert.DoesNotContain("finished", html, System.StringComparison.OrdinalIgnoreCase);
      Assert.Contains("Nothing was built:</strong> A1 was not attempted.", html);
    }

    [Fact]
    public void Nothing_attempted_across_several_regions_names_them_all()
    {
      var html = BuildProgress.ClosingHtml(new[] { "A1", "A2" }, null, stoppedEarly: false);

      Assert.Contains("Nothing was built:</strong> A1, A2 were not attempted.", html);
    }

    /// <summary>An early stop that attempted nothing says both - it stopped, and nothing was built.</summary>
    [Fact]
    public void A_stop_before_anything_was_attempted_says_it_stopped_and_built_nothing()
    {
      var html = BuildProgress.ClosingHtml(new[] { "A1" }, null, stoppedEarly: true);

      Assert.Contains("Stopped before finishing", html);
      Assert.Contains("Nothing was built", html);
    }

    /// <summary>#614: the partial case is the same class. Some regions attempted and others untouched is
    /// not "finished", however the attempted ones went.</summary>
    [Fact]
    public void A_partial_run_with_untouched_regions_does_not_say_finished()
    {
      var html = BuildProgress.ClosingHtml(new[] { "A1", "A2" }, Turn(("A1", RegionOutcome.Built)), stoppedEarly: false);

      Assert.DoesNotContain("finished", html, System.StringComparison.OrdinalIgnoreCase);
      Assert.Contains("Build ended before every approved region was attempted", html);
      Assert.Contains("no new errors): A1", html);
      Assert.Contains("not attempted: A2", html);
    }

    /// <summary>Every region attempted counts as finished even when some failed - the body separates the
    /// outcomes, and the headline's claim ("finished") is then true.</summary>
    [Fact]
    public void Every_region_attempted_is_finished_even_when_one_failed()
    {
      var html = BuildProgress.ClosingHtml(new[] { "A1", "A2" },
        Turn(("A1", RegionOutcome.Built), ("A2", RegionOutcome.Reverted)), false);

      Assert.Contains("Build finished", html);
      Assert.Contains("could not be built: A2", html);
    }

    /// <summary>
    /// #616, GATE STEP 9's OWN CASE. A1 was built in an earlier turn, so the THREAD's record carries a
    /// "built" row for it - and it must, because a reopened conversation reads that record. A later approved
    /// A1 turn that attempts nothing must still close "Nothing was built": the close is about this turn, and
    /// the earlier row is no evidence of anything this turn did.
    /// </summary>
    [Fact]
    public void A_later_turn_that_attempts_nothing_does_not_close_over_an_earlier_turns_build()
    {
      var threadRecord = Merge(null, "A1", RegionOutcome.Built, 4);           // turn 1 built A1
      Assert.Equal(RegionOutcome.Built, BuildProgress.Rows(threadRecord).Single().Outcome);   // and the thread says so

      var html = BuildProgress.ClosingHtml(new[] { "A1" }, Turn(), stoppedEarly: false);   // turn 2 attempted nothing

      Assert.Contains("Nothing was built:</strong> A1 was not attempted.", html);
      Assert.DoesNotContain("built and checked", html);
    }

    /// <summary>A region this turn rebuilt reports THIS turn's outcome - a revert now is not hidden behind
    /// an earlier success.</summary>
    [Fact]
    public void A_rebuild_this_turn_reports_this_turns_outcome_not_the_threads()
    {
      var html = BuildProgress.ClosingHtml(new[] { "A1" }, Turn(("A1", RegionOutcome.Reverted)), false);

      Assert.Contains("A1 could not be built.", html);
      Assert.DoesNotContain("built and checked", html);
    }

    // ---- #627/#636: "Nothing was built" whenever nothing was built ---------------------------------

    /// <summary>
    /// #627, THE GATE'S CASE. The reverted-rebuild turn closed "Build finished. could not be built: A1". #614
    /// made "finished" mean every approved region was attempted; it never required that anything landed. The
    /// headline now keys on what STANDS, not on what was attempted.
    /// </summary>
    [Fact]
    public void Nothing_built_after_an_attempt_headlines_Nothing_was_built()
    {
      var html = BuildProgress.ClosingHtml(new[] { "A1" }, Turn(("A1", RegionOutcome.Reverted)), stoppedEarly: false);

      Assert.Contains("<strong>Nothing was built:</strong> A1 could not be built.", html);
      Assert.DoesNotContain("finished", html, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Nothing_built_names_the_failed_and_the_untouched_together()
    {
      var html = BuildProgress.ClosingHtml(new[] { "A1", "A2", "B1" },
        Turn(("A1", RegionOutcome.Reverted), ("A2", RegionOutcome.Refused)), stoppedEarly: false);

      Assert.Contains("<strong>Nothing was built:</strong> A1, A2 could not be built; B1 was not attempted.", html);
    }

    [Fact]
    public void A_stop_after_a_failed_attempt_says_it_stopped_and_built_nothing()
    {
      var html = BuildProgress.ClosingHtml(new[] { "A1" }, Turn(("A1", RegionOutcome.Reverted)), stoppedEarly: true);

      Assert.Contains("Stopped before finishing", html);
      Assert.Contains("Nothing was built:</strong> A1 could not be built.", html);
      Assert.DoesNotContain("Build finished", html);
    }

    /// <summary>A region built but still erroring is STANDING on the canvas, so something was built.</summary>
    [Fact]
    public void Built_with_errors_counts_as_built()
    {
      var html = BuildProgress.ClosingHtml(new[] { "A1", "A2" },
        Turn(("A1", RegionOutcome.BuiltWithErrors), ("A2", RegionOutcome.Reverted)), stoppedEarly: false);

      Assert.Contains("Build finished", html);
      Assert.DoesNotContain("Nothing was built", html);
      Assert.Contains("still reporting errors: A1", html);
    }

    /// <summary>#636: the trailer describes attempts that reverted or were refused, so it stays whenever one
    /// did - the user is looking at a canvas that is as it was, and should be told nothing moved behind them.</summary>
    [Fact]
    public void The_trailer_stays_when_something_was_attempted_and_nothing_stood()
    {
      var html = BuildProgress.ClosingHtml(new[] { "A1" }, Turn(("A1", RegionOutcome.Refused)), stoppedEarly: false);

      Assert.Contains("Nothing was reverted behind your back - what is on the canvas is what this says.", html);
    }

    /// <summary>#636: and it goes when nothing was attempted - there was no attempt for it to describe.</summary>
    [Fact]
    public void The_trailer_goes_when_nothing_was_attempted()
    {
      var html = BuildProgress.ClosingHtml(new[] { "A1", "A2" }, Turn(), stoppedEarly: false);

      Assert.DoesNotContain("reverted behind your back", html);
    }

    private static Dictionary<string, RegionOutcome> Turn(params (string Address, RegionOutcome Outcome)[] outcomes) =>
      outcomes.ToDictionary(o => o.Address, o => o.Outcome);

    [Fact]
    public void With_nothing_approved_there_is_no_close_to_write()
    {
      Assert.Null(BuildProgress.ClosingHtml(new string[0], Turn(), false));
      Assert.Null(BuildProgress.ClosingHtml(null, null, false));
    }

    // ---- show-and-explain (ADR-054) ----------------------------------------------------------------

    [Fact]
    public void A_built_region_offers_show_and_explain()
    {
      var line = BuildProgress.LineHtml("A1", RegionOutcome.Built, 5, null);

      Assert.Contains("explainregion:A1", line);
      Assert.Contains("Explain A1", line);
    }

    /// <summary>An affordance is an assertion that its target exists (#321): a region that was reverted has
    /// nothing on the canvas to explain, so it is not offered.</summary>
    [Fact]
    public void A_reverted_region_offers_nothing_to_explain()
    {
      Assert.DoesNotContain("explainregion", BuildProgress.LineHtml("A1", RegionOutcome.Reverted, 0, null));
    }

    /// <summary>#328: its own command prefix. Sharing one with different argument semantics is a silent
    /// mis-route waiting for a value valid in both.</summary>
    [Fact]
    public void Show_and_explain_uses_its_OWN_command_prefix()
    {
      var line = BuildProgress.LineHtml("A1", RegionOutcome.Built, 5, null);

      Assert.DoesNotContain("dwightSend('anchor:", line);
      Assert.DoesNotContain("dwightSend('locate:", line);
    }

    // ---- #394: the success row names the state it set ------------------------------------------------

    /// <summary>
    /// THE CHEAPEST WITNESS AGAINST A REPEAT OF #377. That defect was a value nobody could see - seven
    /// sliders sitting at 0.250 while every surface said built and verified, and it took a person opening
    /// the canvas to catch it. The verdict reports state only on a mismatch, which is right for a verdict;
    /// the ROW is where an unnoticed value becomes noticeable.
    /// </summary>
    [Fact]
    public void A_built_region_names_the_values_it_set()
    {
      var html = BuildProgress.LineHtml("A1", RegionOutcome.Built, 5, null,
        new[] { "Rows=5", "Cols=8" });

      Assert.Contains("Rows=5, Cols=8.", html);
    }

    /// <summary>ADR-035 at a UI row. A forty-component region would otherwise push the conversation off
    /// screen with a list nobody reads, which is how a witness stops being one - so it is bounded, and it
    /// SAYS how many it left out rather than trailing off.</summary>
    [Fact]
    public void A_long_list_of_values_is_bounded_and_says_how_many_it_left_out()
    {
      var line = BuildProgress.StateLine(new[] { "a=1", "b=2", "c=3", "d=4", "e=5", "f=6" });

      Assert.Equal("a=1, b=2, c=3, d=4, and 2 more.", line);
    }

    [Fact]
    public void Exactly_the_bound_is_still_shown_whole()
    {
      Assert.Equal("a=1, b=2, c=3, d=4.", BuildProgress.StateLine(new[] { "a=1", "b=2", "c=3", "d=4" }));
    }

    /// <summary>
    /// A REVERTED BUILD SET NOTHING, so printing what it would have set would describe a canvas that does
    /// not exist - the same class of claim the revert path exists to prevent.
    /// </summary>
    [Fact]
    public void A_reverted_region_names_no_values_because_it_set_none()
    {
      var html = BuildProgress.LineHtml("A1", RegionOutcome.Reverted, 0, "it did not verify",
        new[] { "Rows=5" });

      Assert.DoesNotContain("Rows=5", html);
    }

    // ---- #625: the running line names what was reconnected and restored --------------------------------

    [Fact]
    public void A_built_line_names_what_it_reconnected_and_what_it_restored()
    {
      var html = BuildProgress.LineHtml("A1", RegionOutcome.Built, 6, null, null, new[]
      {
        new ReconnectionNote { Consumer = "XY Plane (19) in B1", Input = "Origin" },
        new ReconnectionNote { Consumer = "Loft (22)", Input = "Curves", Restored = true },
      });

      Assert.Contains("Reconnected: XY Plane (19) in B1 (Origin).", html);
      Assert.Contains("Restored: Loft (22) (Curves).", html);
    }

    [Fact]
    public void A_reverted_line_names_no_reconnection_and_labels_are_encoded()
    {
      var reverted = BuildProgress.LineHtml("A1", RegionOutcome.Reverted, 0, null, null,
        new[] { new ReconnectionNote { Consumer = "XY Plane (19)", Input = "Origin" } });
      Assert.DoesNotContain("Reconnected", reverted);

      var built = BuildProgress.LineHtml("A1", RegionOutcome.Built, 1, null, null,
        new[] { new ReconnectionNote { Consumer = "<b>Plane</b> (19)", Input = "O" } });
      Assert.DoesNotContain("<b>Plane</b>", built);
      Assert.Contains("&lt;b&gt;Plane&lt;/b&gt;", built);
    }

    [Fact]
    public void A_region_with_no_state_bearing_components_adds_nothing_to_its_row()
    {
      Assert.Null(BuildProgress.StateLine(new string[0]));
      Assert.Null(BuildProgress.StateLine(null));
      Assert.DoesNotContain("buildstate", BuildProgress.LineHtml("A1", RegionOutcome.Built, 3, null, null));
    }
  }
}
