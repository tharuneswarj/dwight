using System;
using System.Collections.Generic;
using System.Linq;
using Dwight.Agent;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// Phase-9 U20 - THE VERDICT, AND THE ONE CLASS IT EXISTS TO MAKE UNREPRESENTABLE.
  ///
  /// THE DANGEROUS CASE IS NOT THE WALL, IT IS THE FALSE GREEN. If the component being edited has no data
  /// flowing into it right now, the original and the new version both produce nothing, and a 1:1 compare of
  /// nothing against nothing PASSES. The check confirmed exactly zero and reads as "Dwight verified the
  /// outputs are identical" - true, worthless, and indistinguishable from the real thing.
  ///
  /// AND THE CLAIM OUTLIVES THE CARD, which is what makes it more than a wording problem. The outcome is
  /// recorded, reported back to the user afterwards, and stated to the MODEL at the start of its next turn
  /// (#624). An honest sentence on the card and "preserved" in the record is a vacuous proof laundered by
  /// its own paperwork - and the model, which reads the transcript and never the screen, would then be
  /// entitled to tell the user their outputs were checked.
  ///
  /// So vacuity is its own OUTCOME rather than a footnote on the passing one, and these tests hold that on
  /// all three surfaces at once.
  /// </summary>
  public class ScriptEditVerdictTests
  {
    private static ScriptTrialFacts Facts(int compared = 2, int withData = 2, string[] mismatched = null,
      string[] errors = null, bool ran = true, bool wasErrored = false, bool stillErrored = false,
      int appendedInputs = 0) =>
      new ScriptTrialFacts
      {
        Ran = ran,
        Errors = errors ?? new string[0],
        OutputsCompared = compared,
        OutputsWithData = withData,
        Mismatched = mismatched ?? new string[0],
        OriginalWasErrored = wasErrored,
        StillErrored = stillErrored,
        AppendedInputs = appendedInputs,
      };

    /// <summary>Every surface that actually EXISTS for an outcome. An outcome that may not be offered has
    /// no card, so the card is not swept for it - and <see cref="ScriptEditVerdict.CardLine"/> returns null
    /// there rather than a plausible sentence, which is asserted below.</summary>
    private static string[] Surfaces(ScriptEditOutcome o, ScriptTrialFacts f)
    {
      var card = ScriptEditVerdict.CardLine(o, f);
      return card == null
        ? new[] { ScriptEditVerdict.ReportLine(o, f, null), ScriptEditVerdict.ModelFact(o, f) }
        : new[] { card, ScriptEditVerdict.ReportLine(o, f, null), ScriptEditVerdict.ModelFact(o, f) };
    }

    /// <summary>
    /// AN OUTCOME THAT CANNOT BE OFFERED HAS NO CARD SENTENCE, and that is the honest answer rather than a
    /// tidy one. The first version of CardLine returned "Dwight could not check this edit" for every
    /// unoffered outcome - FALSE for NotPreserved, where Dwight checked thoroughly and the check is exactly
    /// why there is no card. A default branch manufacturing a plausible sentence for a state it was not
    /// written for is #613's class: a claim reached by a state nobody enumerated.
    /// </summary>
    [Fact]
    public void An_outcome_that_cannot_be_offered_has_no_card_sentence_rather_than_a_wrong_one()
    {
      foreach (var o in new[] { ScriptEditOutcome.NotPreserved, ScriptEditOutcome.DidNotRun, ScriptEditOutcome.CouldNotCheck })
      {
        Assert.False(ScriptEditVerdict.MayOfferCard(o));
        Assert.Null(ScriptEditVerdict.CardLine(o, Facts()));
      }
    }

    // ---- the outcome is decided in one place, and Preserved needs something compared ------------------

    [Fact]
    public void A_clean_run_with_matching_outputs_is_preserved()
    {
      Assert.Equal(ScriptEditOutcome.Preserved,
        ScriptEditVerdict.Outcome(ScriptEditClaim.Preserves, Facts()));
    }

    [Fact]
    public void A_clean_run_with_differing_outputs_is_not_preserved()
    {
      Assert.Equal(ScriptEditOutcome.NotPreserved,
        ScriptEditVerdict.Outcome(ScriptEditClaim.Preserves, Facts(mismatched: new[] { "area" })));
    }

    /// <summary>
    /// THE ONE THAT WOULD HAVE PASSED. Nothing carried data, so the mismatch list is empty - and an empty
    /// mismatch list is exactly what a successful compare looks like. The vacuity test therefore has to sit
    /// between the mismatch check and the pass, or this falls straight through into Preserved.
    /// </summary>
    [Fact]
    public void A_clean_run_that_compared_nothing_is_never_preserved()
    {
      Assert.Equal(ScriptEditOutcome.NothingToCompare,
        ScriptEditVerdict.Outcome(ScriptEditClaim.Preserves, Facts(compared: 3, withData: 0)));

      Assert.Equal(ScriptEditOutcome.NothingToCompare,
        ScriptEditVerdict.Outcome(ScriptEditClaim.Preserves, Facts(compared: 0, withData: 0)));
    }

    /// <summary>Partial data is a REAL comparison - one output carrying data is something compared - so it
    /// passes. The vacuous case is "nothing at all", not "less than everything".</summary>
    [Fact]
    public void One_output_carrying_data_is_a_real_comparison()
    {
      Assert.Equal(ScriptEditOutcome.Preserved,
        ScriptEditVerdict.Outcome(ScriptEditClaim.Preserves, Facts(compared: 3, withData: 1)));
    }

    /// <summary>A trial that never ran is never a pass - #109's law, and the reason Ran exists as a fact
    /// rather than being inferred from an empty error list.</summary>
    [Fact]
    public void A_trial_that_did_not_run_is_never_a_pass()
    {
      Assert.Equal(ScriptEditOutcome.CouldNotCheck, ScriptEditVerdict.Outcome(ScriptEditClaim.Preserves, Facts(ran: false)));
      Assert.Equal(ScriptEditOutcome.CouldNotCheck, ScriptEditVerdict.Outcome(ScriptEditClaim.Changes, Facts(ran: false)));
      Assert.Equal(ScriptEditOutcome.CouldNotCheck, ScriptEditVerdict.Outcome(ScriptEditClaim.Preserves, null));
    }

    [Fact]
    public void A_source_that_did_not_build_is_its_own_outcome_on_either_claim()
    {
      var broken = Facts(errors: new[] { "NameError: name 'q' is not defined" });
      Assert.Equal(ScriptEditOutcome.DidNotRun, ScriptEditVerdict.Outcome(ScriptEditClaim.Preserves, broken));
      Assert.Equal(ScriptEditOutcome.DidNotRun, ScriptEditVerdict.Outcome(ScriptEditClaim.Changes, broken));
    }

    /// <summary>A declared CHANGE is never compared against the original - that is what declaring it means -
    /// so an empty comparison is not a vacuity problem there, and must not be reported as one.</summary>
    [Fact]
    public void A_declared_change_is_not_subject_to_the_equivalence_outcome_at_all()
    {
      Assert.Equal(ScriptEditOutcome.RanClean,
        ScriptEditVerdict.Outcome(ScriptEditClaim.Changes, Facts(compared: 0, withData: 0)));
      Assert.Equal(ScriptEditOutcome.RanClean,
        ScriptEditVerdict.Outcome(ScriptEditClaim.Changes, Facts(mismatched: new[] { "area" })));
    }

    // ---- only some outcomes may reach a card ---------------------------------------------------------

    [Fact]
    public void Only_a_checked_or_honestly_unchecked_edit_may_be_offered()
    {
      Assert.True(ScriptEditVerdict.MayOfferCard(ScriptEditOutcome.Preserved));
      Assert.True(ScriptEditVerdict.MayOfferCard(ScriptEditOutcome.RanClean));
      Assert.True(ScriptEditVerdict.MayOfferCard(ScriptEditOutcome.NothingToCompare));

      Assert.False(ScriptEditVerdict.MayOfferCard(ScriptEditOutcome.NotPreserved));
      Assert.False(ScriptEditVerdict.MayOfferCard(ScriptEditOutcome.DidNotRun));
      Assert.False(ScriptEditVerdict.MayOfferCard(ScriptEditOutcome.CouldNotCheck));
    }

    // ---- THE ASSERTION THE ARCHITECT ASKED FOR, ON ALL THREE SURFACES AT ONCE -------------------------

    /// <summary>
    /// THE TEST THIS TYPE EXISTS FOR. On the vacuous path the words that would launder it - verified,
    /// identical, unchanged, preserved - may not appear ANYWHERE: not on the card the user approves, not in
    /// the report they read afterwards, and not in the fact the model is told next turn. Any one of the
    /// three leaking it is enough for the claim to be cited later as a check that happened.
    /// </summary>
    [Fact]
    public void Nothing_on_the_vacuous_path_may_read_as_verified_on_any_surface()
    {
      var facts = Facts(compared: 3, withData: 0);
      var outcome = ScriptEditVerdict.Outcome(ScriptEditClaim.Preserves, facts);
      Assert.Equal(ScriptEditOutcome.NothingToCompare, outcome);

      foreach (var surface in Surfaces(outcome, facts))
      {
        foreach (var forbidden in new[] { "verified", "identical", "unchanged", "preserved" })
          Assert.DoesNotContain(forbidden, surface, StringComparison.OrdinalIgnoreCase);

        // AND IT MUST SAY SO POSITIVELY, rather than merely avoiding the words above. Silence about a
        // check that did not happen reads as a check that did - which is the whole class. The test asks
        // for the SUBSTANCE (an emptiness word next to a checking word) rather than for a list of
        // approved phrasings: an allow-list of sentences fails every time the sentence is improved, which
        // teaches people to edit the test instead of reading it.
        var saysEmpty = surface.IndexOf("nothing", StringComparison.OrdinalIgnoreCase) >= 0
                        || surface.IndexOf("no data", StringComparison.OrdinalIgnoreCase) >= 0
                        || surface.IndexOf("never", StringComparison.OrdinalIgnoreCase) >= 0;
        var saysWhatOf = surface.IndexOf("compar", StringComparison.OrdinalIgnoreCase) >= 0
                         || surface.IndexOf("check", StringComparison.OrdinalIgnoreCase) >= 0
                         || surface.IndexOf("confirm", StringComparison.OrdinalIgnoreCase) >= 0;
        Assert.True(saysEmpty && saysWhatOf,
          "a vacuous check must state its own emptiness, not merely avoid claiming otherwise: " + surface);
      }
    }

    /// <summary>The statement of emptiness carries a DOOR, like every refusal in this product - and the
    /// door is one the user can actually walk through today.</summary>
    [Fact]
    public void The_statement_of_emptiness_tells_the_user_how_to_get_it_checked()
    {
      var facts = Facts(compared: 2, withData: 0);
      Assert.Contains("give the component inputs", ScriptEditVerdict.CardLine(ScriptEditOutcome.NothingToCompare, facts));
    }

    /// <summary>And the model is told NOT to relay a check that did not happen - because it reads the
    /// transcript and never the screen, so it is the surface most likely to repeat the stronger claim.</summary>
    [Fact]
    public void The_model_is_told_not_to_claim_a_check_that_did_not_happen()
    {
      var fact = ScriptEditVerdict.ModelFact(ScriptEditOutcome.NothingToCompare, Facts(compared: 2, withData: 0));
      Assert.Contains("CONFIRMED NOTHING", fact);
      Assert.Contains("Do not tell the user", fact);
    }

    /// <summary>By contrast the REAL pass says so on every surface - or the distinction above would be
    /// bought by making the honest case unreadable too.</summary>
    [Fact]
    public void A_real_pass_does_say_the_outputs_were_compared_and_identical()
    {
      var facts = Facts(compared: 2, withData: 2);
      Assert.Contains("identical", ScriptEditVerdict.CardLine(ScriptEditOutcome.Preserved, facts));
      Assert.Contains("verified", ScriptEditVerdict.ReportLine(ScriptEditOutcome.Preserved, facts, null));
      Assert.Contains("identical", ScriptEditVerdict.ModelFact(ScriptEditOutcome.Preserved, facts));
    }

    // ---- a false "preserves", reported to two audiences -----------------------------------------------

    /// <summary>
    /// The USER is told the model was wrong about its own edit, that DWIGHT is the thing that caught it,
    /// and - last, because it is the sentence that settles the reader - that nothing was touched.
    /// </summary>
    [Fact]
    public void A_false_preserves_names_who_claimed_what_and_ends_on_the_reassurance()
    {
      var report = ScriptEditVerdict.ReportLine(ScriptEditOutcome.NotPreserved, Facts(mismatched: new[] { "area", "centroid" }), null);
      Assert.Contains("said it would keep", report);
      Assert.Contains("area, centroid", report);
      Assert.EndsWith("Nothing on your canvas was touched.", report);
    }

    /// <summary>
    /// The MODEL gets the correction in the transcript - chrome cannot correct it (#624) - with BOTH doors
    /// named. Re-declaring as "changes" is not laundering: it is the weaker claim, and the user reads the
    /// diff and decides.
    /// </summary>
    [Fact]
    public void A_false_preserves_gives_the_model_both_doors()
    {
      var fact = ScriptEditVerdict.ModelFact(ScriptEditOutcome.NotPreserved, Facts(mismatched: new[] { "area" }));
      Assert.Contains("correct the source", fact);
      Assert.Contains("\"changes\"", fact);
    }

    /// <summary>The ports are named and the VALUES are not - the feedback-precision law, which is enforced
    /// structurally elsewhere and must not be undone by a sentence here (#71).</summary>
    [Fact]
    public void A_false_preserves_names_the_ports_and_never_the_values()
    {
      var facts = Facts(mismatched: new[] { "area" });
      foreach (var surface in Surfaces(ScriptEditOutcome.NotPreserved, facts))
      {
        Assert.Contains("area", surface);
        Assert.DoesNotContain("expected", surface, StringComparison.OrdinalIgnoreCase);
      }
    }

    // ---- the report renderers, whose NAMES are claims ------------------------------------------------

    /// <summary>
    /// THE BUG THIS PINS, and it was mine. The click first rendered BOTH outcomes through
    /// set_value's NotAppliedHtml - which hardcodes the words "Not applied." in its own markup - so a
    /// SUCCESSFUL edit read "Not applied. Applied. IT CHANGES WHAT THE COMPONENT PRODUCES": a report
    /// contradicting itself in its first two words.
    ///
    /// It is #680 one layer out. A renderer that carries a CLAIM in its body inherits that claim, however
    /// neutral its signature looks - NotAppliedHtml(string) reads like an escape-this-sentence helper and
    /// is actually a sentence with a hole in it, and the sentence says the change did not happen.
    /// </summary>
    [Fact]
    public void An_applied_report_never_says_not_applied()
    {
      var report = ScriptEditVerdict.ReportLine(ScriptEditOutcome.RanClean, Facts(), null);

      var html = ScriptEditReport.AppliedHtml(report);
      Assert.Contains("<strong>Applied.</strong>", html);
      Assert.DoesNotContain("Not applied", html);

      var text = ScriptEditReport.AppliedText(report);
      Assert.DoesNotContain("Not applied", text);
    }

    /// <summary>
    /// AND THE CHOICE IS TESTED, not just the renderers. Fixing the borrowed renderer left the DECISION
    /// inline in the GH-coupled click, so reinstating the bug turned NOTHING red - the tests asserted the
    /// renderers and could not reach the call site. #612 twice in one day. The mapping is now a function,
    /// and this is the assertion that would have caught the original defect.
    /// </summary>
    [Fact]
    public void The_applied_outcome_never_selects_the_refusal_renderer()
    {
      var report = ScriptEditVerdict.ReportLine(ScriptEditOutcome.RanClean, Facts(), null);

      Assert.DoesNotContain("Not applied", ScriptEditReport.Html(true, report));
      Assert.DoesNotContain("Not applied", ScriptEditReport.Text(true, report));
      Assert.Contains("Not applied", ScriptEditReport.Html(false, "the source moved"));
      Assert.Contains("unchanged", ScriptEditReport.Text(false, "the source moved"));
    }

    /// <summary>And the sentence is not doubled: the verdict's line already begins "Applied." because it
    /// has to stand alone in the transcript, so the renderer's own bold lead-in strips it rather than
    /// printing "Applied. Applied.".</summary>
    [Fact]
    public void The_applied_lead_in_is_not_doubled()
    {
      var html = ScriptEditReport.AppliedHtml("Applied. Dwight checked the definition still solves.");
      Assert.Equal("<p><strong>Applied.</strong> Dwight checked the definition still solves.</p>", html);
    }

    /// <summary>A refusal says so, and says it in both channels - a screen and a transcript that disagree
    /// is how a model comes to believe a change ran (#624).</summary>
    [Fact]
    public void A_refusal_says_not_applied_on_both_channels()
    {
      Assert.Contains("Not applied", ScriptEditReport.NotAppliedHtml("the source moved"));
      Assert.Contains("canvas is unchanged", ScriptEditReport.NotAppliedText("the source moved"));
    }

    /// <summary>Both renderers escape, because their input reaches a WebView (ADR-088). The text forms do
    /// not, because their destination is a prompt where entities are noise (#335).</summary>
    [Fact]
    public void The_html_renderers_escape_and_the_text_ones_do_not()
    {
      Assert.Contains("&lt;b&gt;", ScriptEditReport.AppliedHtml("a <b> tag"));
      Assert.Contains("&lt;b&gt;", ScriptEditReport.NotAppliedHtml("a <b> tag"));
      Assert.Contains("a <b> tag", ScriptEditReport.AppliedText("a <b> tag"));
    }

    // ---- deixis is a property of the destination -----------------------------------------------------

    /// <summary>
    /// NO SENTENCE THIS TYPE PRODUCES MAY POINT AT ITS OWN SURROUNDINGS.
    ///
    /// The defect this catches, found by the architect reading the card: CardLine's clean-run case ended
    /// "the difference is in the diff ABOVE". On a card that is true. In a report there IS no diff above,
    /// so the sentence would point at something that is not there - and every string here is a candidate
    /// for more than one surface. Escaping is a property of the destination (#335); so is deixis. Each
    /// surface adds its own pointing words, and this type adds none.
    ///
    /// It is deliberately MECHANICAL rather than a review note: it catches the next one, which will not
    /// look like this one.
    /// </summary>
    [Fact]
    public void No_shared_sentence_points_at_its_own_surroundings()
    {
      var positional = new[] { "above", "below", "on the left", "on the right", "shown here", "as shown" };

      var sentences = new List<string>
      {
        ScriptEditVerdict.ClaimClause(ScriptEditClaim.Preserves),
        ScriptEditVerdict.ClaimClause(ScriptEditClaim.Changes),
      };
      foreach (var outcome in Enum.GetValues(typeof(ScriptEditOutcome)).Cast<ScriptEditOutcome>())
        foreach (var f in new[] { Facts(), Facts(compared: 0, withData: 0), Facts(mismatched: new[] { "a" }),
                                  Facts(wasErrored: true, stillErrored: true), Facts(appendedInputs: 2) })
        {
          sentences.Add(ScriptEditVerdict.CardLine(outcome, f));
          sentences.Add(ScriptEditVerdict.ReportLine(outcome, f, null));
          sentences.Add(ScriptEditVerdict.ModelFact(outcome, f));
        }

      foreach (var s in sentences.Where(x => x != null))
        foreach (var word in positional)
          Assert.DoesNotContain(word, s, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// AND THE REPORT DOES NOT LOSE THE CLAIM. Cutting the duplicated clause from the card's side must not
    /// quietly make the report the weaker surface: after applying, a user still needs to know the edit was
    /// declared a CHANGE rather than a preservation - rendered from the same source the card uses.
    /// </summary>
    [Fact]
    public void The_report_still_states_that_a_change_was_declared_as_one()
    {
      var report = ScriptEditVerdict.ReportLine(ScriptEditOutcome.RanClean, Facts(), null);
      Assert.Contains(ScriptEditVerdict.ClaimClause(ScriptEditClaim.Changes), report);
    }

    // ---- an appended port is unwired during the trial, and no surface may imply otherwise ------------

    /// <summary>
    /// A PASS MUST NOT IMPLY THE WHOLE NEW INTERFACE WAS EXERCISED. An appended input is unwired by
    /// construction while the trial runs - nothing feeds it yet - so "Dwight compared the outputs and they
    /// are identical" would be read as covering a port that was never touched.
    /// </summary>
    [Fact]
    public void A_pass_says_an_appended_input_was_not_part_of_the_check()
    {
      var card = ScriptEditVerdict.CardLine(ScriptEditOutcome.Preserved, Facts(appendedInputs: 1));
      Assert.Contains("not wired to anything yet", card);
      Assert.Contains("not part of that check", card);
    }

    /// <summary>And says nothing of the sort when nothing was appended - a clause about a port that does
    /// not exist is one the reader has to work out is irrelevant.</summary>
    [Fact]
    public void A_pass_with_no_appended_ports_says_nothing_about_wiring()
    {
      Assert.DoesNotContain("not wired", ScriptEditVerdict.CardLine(ScriptEditOutcome.Preserved, Facts()));
    }

    /// <summary>
    /// AND A FAILED PRESERVES NAMES THE LIKELY INNOCENT CAUSE BEFORE THE READER REACHES FOR A WORSE ONE.
    /// If the new source reads an input nothing feeds yet, a difference is expected rather than alarming.
    /// A failure the reader would call "of course it did", reported without that clause, is how a working
    /// feature earns a reputation for being flaky.
    /// </summary>
    [Fact]
    public void A_failed_preserves_with_an_appended_input_explains_the_likely_innocent_cause()
    {
      var facts = Facts(mismatched: new[] { "area" }, appendedInputs: 1);
      foreach (var surface in Surfaces(ScriptEditOutcome.NotPreserved, facts))
      {
        Assert.Contains("not wired to anything yet", surface);
        Assert.Contains("explain the difference", surface);
      }
    }

    [Fact]
    public void The_appended_clause_is_plural_when_there_is_more_than_one()
    {
      var card = ScriptEditVerdict.CardLine(ScriptEditOutcome.Preserved, Facts(appendedInputs: 2));
      Assert.Contains("The 2 new inputs are", card);
      Assert.DoesNotContain("The new input is", card);
    }

    // ---- fixing a broken script: "no new errors" must not read as "fixed" -----------------------------

    /// <summary>
    /// A user whose whole request was "fix this" will read "no new errors" as "fixed". So when the original
    /// was already broken, the plugin states plainly which it is - and the two sentences are different
    /// facts, not two tones (#253 in the one sentence they will act on).
    /// </summary>
    [Fact]
    public void Fixing_a_broken_script_says_whether_the_old_error_is_actually_gone()
    {
      var fixedIt = Facts(wasErrored: true, stillErrored: false);
      var didNot = Facts(wasErrored: true, stillErrored: true);

      Assert.Contains("is gone", ScriptEditVerdict.CardLine(ScriptEditOutcome.RanClean, fixedIt));
      Assert.Contains("still there", ScriptEditVerdict.CardLine(ScriptEditOutcome.RanClean, didNot));
      Assert.Contains("STILL THERE", ScriptEditVerdict.ModelFact(ScriptEditOutcome.RanClean, didNot));
    }

    /// <summary>And a component that was NOT broken says neither - a sentence about an error that never
    /// existed is a sentence the user has to work out is irrelevant.</summary>
    [Fact]
    public void An_edit_to_a_healthy_script_says_nothing_about_errors_it_never_had()
    {
      var line = ScriptEditVerdict.CardLine(ScriptEditOutcome.RanClean, Facts());
      Assert.Contains("no new errors", line);
      Assert.DoesNotContain("still there", line);
      Assert.DoesNotContain("is gone", line);
    }
  }
}
