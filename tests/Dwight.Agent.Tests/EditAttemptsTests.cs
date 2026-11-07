using Dwight.Agent;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// U20 row 3c: an edit that ENDS THE TURN refused reaches the user in Dwight's own words, not only in the
  /// model's paraphrase (#235's two voices).
  ///
  /// The refusal goes to the MODEL mid-loop, deliberately - it can correct the source or re-declare the claim -
  /// so the line is decided at turn end, from each target's LAST outcome, and only for a target that got no
  /// card. These tests pin that decision; the sentence itself is lane C's (ScriptEditVerdict.RefusedLine) and
  /// the pairing with the renderer is pinned beside it.
  ///
  /// STATED BOUND: the panel that fills this during a turn and renders it at the end is GH-bound. What is
  /// tested here is every decision it makes; the two calls it makes with the result are not.
  /// </summary>
  public class EditAttemptsTests
  {
    private const int Target = 12;
    private const string Label = "\"Offset curves\"";

    private static ScriptTrialFacts DidNotBuild() =>
      new ScriptTrialFacts { Ran = true, Errors = new[] { "CS0103: The name 'crv' does not exist in the current context" } };

    private static ScriptTrialFacts Differs() =>
      new ScriptTrialFacts { Ran = true, OutputsCompared = 1, OutputsWithData = 1, Mismatched = new[] { "Result" } };

    private static ScriptTrialFacts Identical() =>
      new ScriptTrialFacts { Ran = true, OutputsCompared = 1, OutputsWithData = 1 };

    private static ScriptTrialFacts RanClean() => new ScriptTrialFacts { Ran = true };

    // ---- the two cases the ruling named ----------------------------------------------------------------

    /// <summary>The usual case: the model is refused once, explains in its own words, and stops. Dwight's own
    /// line follows - naming the component, since there is no card beside it to say which one.</summary>
    [Fact]
    public void A_refused_edit_the_model_then_stops_on_ends_with_dwights_own_line()
    {
      var edits = new EditAttempts();
      edits.Trialled(Target, Label, ScriptEditOutcome.DidNotRun, DidNotBuild());

      var lines = edits.RefusedLines(stopped: false);

      Assert.Single(lines);
      Assert.Equal(ScriptEditVerdict.RefusedLine(Label, ScriptEditOutcome.DidNotRun, DidNotBuild()), lines[0]);
      Assert.Contains("Offset curves", lines[0]);
    }

    /// <summary>Refused, then the model re-declares the claim and the trial earns a card: the card carries the
    /// outcome, and a "Not applied" beside it would read as the offer being withdrawn.</summary>
    [Fact]
    public void A_refused_edit_followed_by_a_carded_redeclare_ends_with_no_line()
    {
      var edits = new EditAttempts();
      edits.Trialled(Target, Label, ScriptEditOutcome.NotPreserved, Differs());
      edits.Trialled(Target, Label, ScriptEditOutcome.RanClean, RanClean());

      Assert.Empty(edits.RefusedLines(stopped: false));
    }

    // ---- the edges -------------------------------------------------------------------------------------

    /// <summary>A Stop already says what happened; a report of work the user interrupted would talk over it.</summary>
    [Fact]
    public void A_stop_says_nothing_more()
    {
      var edits = new EditAttempts();
      edits.Trialled(Target, Label, ScriptEditOutcome.DidNotRun, DidNotBuild());

      Assert.Empty(edits.RefusedLines(stopped: true));
    }

    /// <summary>A trial that timed out or came back empty has no outcome of its own. It is reported as what it
    /// is - Dwight could not check the edit - never as silence.</summary>
    [Fact]
    public void A_trial_that_never_answered_is_reported_as_could_not_check()
    {
      var edits = new EditAttempts();
      edits.Unchecked(Target, Label);

      var lines = edits.RefusedLines(stopped: false);

      Assert.Single(lines);
      Assert.Equal(ScriptEditVerdict.RefusedLine(Label, ScriptEditOutcome.CouldNotCheck, null), lines[0]);
    }

    /// <summary>
    /// Parallel tool use can put a card for a target AND a later refused try at the same target in one step.
    /// The card is live and true, so the target gets no line: "no card this turn" is the rule, not "the last
    /// outcome was refused".
    /// </summary>
    [Fact]
    public void A_card_earlier_in_the_turn_is_not_contradicted_by_a_later_refusal()
    {
      var edits = new EditAttempts();
      edits.Trialled(Target, Label, ScriptEditOutcome.Preserved, Identical());
      edits.Trialled(Target, Label, ScriptEditOutcome.DidNotRun, DidNotBuild());

      Assert.Empty(edits.RefusedLines(stopped: false));
    }

    /// <summary>One line per target, carrying its LAST outcome, in the order the targets were first edited.</summary>
    [Fact]
    public void Each_target_is_reported_once_with_its_last_outcome_in_first_edited_order()
    {
      var edits = new EditAttempts();
      edits.Trialled(5, "\"Offset\"", ScriptEditOutcome.DidNotRun, DidNotBuild());
      edits.Trialled(3, "\"Loft\"", ScriptEditOutcome.DidNotRun, DidNotBuild());
      edits.Trialled(5, "\"Offset\"", ScriptEditOutcome.NotPreserved, Differs());

      var lines = edits.RefusedLines(stopped: false);

      Assert.Equal(2, lines.Count);
      Assert.Equal(ScriptEditVerdict.RefusedLine("\"Offset\"", ScriptEditOutcome.NotPreserved, Differs()), lines[0]);
      Assert.Equal(ScriptEditVerdict.RefusedLine("\"Loft\"", ScriptEditOutcome.DidNotRun, DidNotBuild()), lines[1]);
    }

    /// <summary>
    /// THE BUDGET IS UNCHANGED BY THIS TYPE: only a refused trial spends an attempt, shared across a compile
    /// failure and a failed preserves. A card spends none, and neither does a trial that never answered - the
    /// model's source was never judged.
    /// </summary>
    [Fact]
    public void Only_a_refused_trial_spends_an_attempt()
    {
      var edits = new EditAttempts();
      Assert.Equal(0, edits.Spent(Target));

      edits.Trialled(Target, Label, ScriptEditOutcome.DidNotRun, DidNotBuild());
      edits.Trialled(Target, Label, ScriptEditOutcome.NotPreserved, Differs());
      Assert.Equal(2, edits.Spent(Target));

      edits.Unchecked(Target, Label);
      edits.Trialled(Target, Label, ScriptEditOutcome.RanClean, RanClean());
      Assert.Equal(2, edits.Spent(Target));

      Assert.Equal(0, edits.Spent(99));
    }
  }
}
