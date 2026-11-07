using System.Collections.Generic;
using System.Linq;

namespace Dwight.Agent
{
  /// <summary>
  /// THIS TURN'S edit_script trials, per TARGET: the repair budget, and what the last trial on each target
  /// came to (Phase-9 U20).
  ///
  /// THE BUDGET is keyed by the component id and SHARED between a compile failure and a failed preserves -
  /// both are "try again on this target", and counting them apart is the door a model would use to oscillate
  /// between correcting the source and re-declaring the claim, spending six attempts where it was granted
  /// three. Only a REFUSED trial spends one, exactly as before this type existed.
  ///
  /// THE LAST OUTCOME is what the sitting found missing (row 3c). A refusal goes to the MODEL mid-loop,
  /// deliberately, so it can correct the source or re-declare the claim. But when the TURN ENDS with no card
  /// for that component, the refusal is a finished fact about what Dwight checked - and it reached the user
  /// only as the model's paraphrase, the two voices #235 forbids. So each target's last outcome is kept, and
  /// at turn end <see cref="RefusedLines"/> turns the ones that ended refused into Dwight's own sentence.
  ///
  /// A turn-scoped object rather than thread state, so it dies with the turn as the approved scope does.
  /// GH-free (ADR-091): the panel that fills and reads it cannot be reached by a headless test, and this is the
  /// decision that matters.
  /// </summary>
  internal sealed class EditAttempts
  {
    private sealed class Target
    {
      public int Spent;
      public string Label;
      public ScriptEditOutcome Last;
      public ScriptTrialFacts Facts;
      public bool CardOffered;
    }

    private readonly Dictionary<int, Target> _byTarget = new Dictionary<int, Target>();

    /// <summary>First-edited order, so the turn-end lines come out in the order the user saw the work.</summary>
    private readonly List<int> _order = new List<int>();

    /// <summary>How many of this target's attempts this turn have been spent - refused trials only.</summary>
    public int Spent(int target) => _byTarget.TryGetValue(target, out var t) ? t.Spent : 0;

    /// <summary>
    /// A trial ran and produced an outcome. A refused outcome spends one attempt; an offerable one does not,
    /// and marks the target as HAVING A CARD this turn.
    /// </summary>
    public void Trialled(int target, string label, ScriptEditOutcome outcome, ScriptTrialFacts facts)
    {
      var t = For(target, label);
      t.Last = outcome;
      t.Facts = facts;
      if (ScriptEditVerdict.MayOfferCard(outcome)) t.CardOffered = true;
      else t.Spent++;
    }

    /// <summary>
    /// A trial that produced NO outcome - it timed out, or came back empty. Its last outcome is
    /// <see cref="ScriptEditOutcome.CouldNotCheck"/>, which is what it is: Dwight could not check the edit.
    /// It spends no attempt, as before - the model's source was never judged.
    /// </summary>
    public void Unchecked(int target, string label)
    {
      var t = For(target, label);
      t.Last = ScriptEditOutcome.CouldNotCheck;
      t.Facts = null;
    }

    /// <summary>
    /// THE TURN-END LINES: Dwight's own sentence for every target whose last trial was refused and which got
    /// no card this turn, in first-edited order. Empty on a STOP - "You stopped this task." already says what
    /// happened, and a report of work the user interrupted would talk over it.
    ///
    /// NO CARD THIS TURN, NOT MERELY "THE LAST OUTCOME WAS REFUSED". A card ends the turn after its step, but
    /// parallel tool use is not disabled, so one step can hold a card for a target AND a later refused try at
    /// the same target. The card is live and true; a "Not applied" beside it would read as though the offer
    /// had been withdrawn. So a target with any card this turn gets no line - the card speaks for it.
    /// </summary>
    public IReadOnlyList<string> RefusedLines(bool stopped)
    {
      if (stopped) return new string[0];
      return _order
        .Select(id => _byTarget[id])
        .Where(t => !t.CardOffered)
        .Select(t => ScriptEditVerdict.RefusedLine(t.Label, t.Last, t.Facts))
        .Where(line => line != null)
        .ToList();
    }

    private Target For(int target, string label)
    {
      if (!_byTarget.TryGetValue(target, out var t))
      {
        _byTarget[target] = t = new Target();
        _order.Add(target);
      }
      if (!string.IsNullOrWhiteSpace(label)) t.Label = label;
      return t;
    }
  }
}
