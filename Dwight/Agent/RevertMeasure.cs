using System;
using System.Collections.Generic;
using System.Linq;

namespace Dwight.Agent
{
  /// <summary>What a revert LEFT, measured against the document as it stood before the build (U91). Three lists of names, never a claim.</summary>
  internal sealed class RevertMeasurement
  {
    /// <summary>Objects Dwight added during the build that still stand after the revert (a group the revert missed, a component the guarded path left).</summary>
    public List<string> StandingDwight { get; } = new List<string>();

    /// <summary>Objects that were not there before the build and are not Dwight's: another plugin made them while the build ran.</summary>
    public List<string> Appeared { get; } = new List<string>();

    /// <summary>Objects that stood before the build and are not there now.</summary>
    public List<string> Missing { get; } = new List<string>();

    /// <summary>True only when every object standing is one that stood before, and every one that stood before still stands.</summary>
    public bool AsItWas => StandingDwight.Count == 0 && Appeared.Count == 0 && Missing.Count == 0;
  }

  /// <summary>
  /// "THE CANVAS IS AS IT WAS" IS A MEASUREMENT, NEVER AN ASSERTION (U91). A spot build placed a script and a panel and, because the user
  /// ticked "in a group", a group of its own; the script failed, the build reverted, and the thread said the canvas was as it was while the
  /// document held two objects more than before - the spot group, with two dangling members. The guarded revert removes what the kit holds, and
  /// the kit did not hold the group.
  ///
  /// So the sentence is computed from two id sets with names - the objects that stood before the build and the ones that stand after the revert - and
  /// Dwight's own additions. It says "as it was" only when that is true, says what Dwight left behind when it is not, and says what ANOTHER PLUGIN
  /// made while the build ran (a Bifocals label group) without removing it: Dwight removes only what it made. GH-free (ADR-091).
  /// </summary>
  internal static class RevertMeasure
  {
    public const string AsItWasPhrase = "the canvas is as it was";
    public const int MaxNamed = 5;

    /// <summary>Compare. <paramref name="before"/> and <paramref name="after"/> are every object's id with the name a user would call it;
    /// <paramref name="dwightAdded"/> is every object the build added (the kit's record).</summary>
    public static RevertMeasurement Compare(IReadOnlyDictionary<Guid, string> before, IReadOnlyDictionary<Guid, string> after, ISet<Guid> dwightAdded)
    {
      var m = new RevertMeasurement();
      before = before ?? new Dictionary<Guid, string>();
      after = after ?? new Dictionary<Guid, string>();
      dwightAdded = dwightAdded ?? new HashSet<Guid>();

      foreach (var kv in after)
      {
        if (before.ContainsKey(kv.Key)) continue;
        if (dwightAdded.Contains(kv.Key)) m.StandingDwight.Add(Name(kv.Value));
        else m.Appeared.Add(Name(kv.Value));
      }
      foreach (var kv in before)
        if (!after.ContainsKey(kv.Key)) m.Missing.Add(Name(kv.Value));
      return m;
    }

    private static string Name(string n) => string.IsNullOrWhiteSpace(n) ? "(unnamed)" : n.Trim();

    private static string Objects(int n) => n + (n == 1 ? " object" : " objects");

    private static string Named(IReadOnlyList<string> names)
    {
      var shown = names.Take(MaxNamed).ToList();
      var more = names.Count - shown.Count;
      return string.Join(", ", shown) + (more > 0 ? " and " + more + " more" : "");
    }

    /// <summary>
    /// The measured sentence. Clean: "the canvas is as it was". Otherwise, one clause per thing found, each in the words the brief pinned:
    /// <c>Reverted, but N object(s) Dwight added still stand: NAMES; one Ctrl-Z may remove them, or delete them by hand.</c>,
    /// <c>N object(s) appeared that Dwight did not make: NAMES; another plugin made them</c>, and
    /// <c>N object(s) that stood before are now missing: NAMES</c>.
    /// </summary>
    public static string Sentence(RevertMeasurement m)
    {
      if (m == null || m.AsItWas) return AsItWasPhrase;
      var parts = new List<string>();
      if (m.StandingDwight.Count > 0)
        parts.Add("Reverted, but " + Objects(m.StandingDwight.Count) + " Dwight added still " + (m.StandingDwight.Count == 1 ? "stands" : "stand") + ": " +
                  Named(m.StandingDwight) + "; one Ctrl-Z may remove " + (m.StandingDwight.Count == 1 ? "it" : "them") + ", or delete " +
                  (m.StandingDwight.Count == 1 ? "it" : "them") + " by hand.");
      if (m.Appeared.Count > 0)
        parts.Add(Capital(Objects(m.Appeared.Count)) + " appeared that Dwight did not make: " + Named(m.Appeared) + "; another plugin made " +
                  (m.Appeared.Count == 1 ? "it" : "them") + ".");
      if (m.Missing.Count > 0)
        parts.Add(Capital(Objects(m.Missing.Count)) + " that stood before " + (m.Missing.Count == 1 ? "is" : "are") + " now missing: " + Named(m.Missing) + ".");
      return string.Join(" ", parts);
    }

    private static string Capital(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

    /// <summary>
    /// Put the measurement into text written before it was taken. A clean measurement leaves the text byte for byte; otherwise every
    /// "the canvas is as it was" (with or without the full stop that follows it, and in sentence case) becomes the measured sentence, so no
    /// sentence of the verdict can claim what the measurement refused.
    /// </summary>
    public static string Apply(string text, RevertMeasurement m)
    {
      if (string.IsNullOrEmpty(text) || m == null || m.AsItWas) return text;
      var measured = Sentence(m);
      var trimmed = measured.TrimEnd('.');
      return text
        .Replace("The canvas is as it was.", measured)
        .Replace("The canvas is as it was", trimmed)
        .Replace(AsItWasPhrase + ".", measured)
        .Replace(AsItWasPhrase, trimmed);
    }

    /// <summary>The new verdict field for a revert: the measured sentence, so the model and the panel read the same words.</summary>
    public static string UndoField(RevertMeasurement m) =>
      m == null || m.AsItWas ? "nothing to undo - " + AsItWasPhrase : Sentence(m);
  }

  /// <summary>
  /// WHEN AN AUTOMATICALLY APPROVED CHANGE ENDS IN A REVERT OR A REFUSAL, THE MODEL GETS ONE FOLLOW-UP TURN (U94). Under "Ask every time" the report
  /// is the plugin's voice and the user decides what comes next. Under an automatic approval the user delegated the step, nobody is there to type,
  /// and the plan would stop on a diagnostic the model has not read - the law from the gates: a bound that ends a turn must not end it on a
  /// diagnostic nobody has read. A click keeps today's behaviour, and an automatic approval that SUCCEEDED needs none (its own continuation runs).
  /// </summary>
  /// <summary>
  /// U108: THE ORDER OF A BUILD'S REVERT, as data the revert follows and a test reads. The record is marked as taken back FIRST on both
  /// paths. Through the stack: undo it. When the stack cannot undo it (an object it refers to is gone, U77/U87): remove what was placed
  /// by hand, then remove the record, so no dead record stands on the stack for a build that is not on the canvas.
  /// </summary>
  internal static class BuildRevertPlan
  {
    public enum Step { MarkTakenBack, Undo, RemoveObjects, RemoveRecord }

    public static IReadOnlyList<Step> For(bool referencesMissing) =>
      referencesMissing
        ? new[] { Step.MarkTakenBack, Step.RemoveObjects, Step.RemoveRecord }
        : new[] { Step.MarkTakenBack, Step.Undo };
  }

  internal static class AutoFollowUp
  {
    public enum FlowEnd { Succeeded, Reverted, Refused }

    public static bool ShouldRun(bool approvedAutomatically, FlowEnd end) =>
      approvedAutomatically && (end == FlowEnd.Reverted || end == FlowEnd.Refused);

    /// <summary>U105: the same decision for an approval the PLUGIN applies from the card's record (a spot build, an edit, a script edit): it
    /// either landed, or it was reverted or refused, and only an automatic approval that did not land is followed up.</summary>
    public static bool AfterApplied(bool approvedAutomatically, bool landed) =>
      ShouldRun(approvedAutomatically, landed ? FlowEnd.Succeeded : FlowEnd.Refused);

    /// <summary>Said in the thread before the follow-up turn runs.</summary>
    public const string Line = "Dwight is continuing after the revert.";

    /// <summary>
    /// What an automatically approved build's follow-up turn is told. This is the turn's only new message: with the line alone, which is chrome, the
    /// transcript ended on the model's own closing text, and Anthropic refused it as a prefill (400) on every revert under an automatic mode. OpenAI and
    /// Google answered it, which is why it was only seen on Sonnet. Plugin-written from the outcomes the build recorded; why each failed is the build's
    /// own fact at turn start (<see cref="BuildOutcomeFact"/>).
    /// </summary>
    public static string BuildEndedFact(IReadOnlyDictionary<string, RegionOutcome> outcomes, IReadOnlyList<string> approvedRegions = null)
    {
      var parts = (outcomes ?? new Dictionary<string, RegionOutcome>())
        .OrderBy(o => o.Key, StringComparer.Ordinal)
        .Select(o => o.Key + " " + Said(o.Value))
        .Concat(NotAttempted(approvedRegions, outcomes).Select(r => r + " was not attempted"));
      return "PLUGIN FACT (from Dwight, not the user): the approved build did not finish: " + string.Join("; ", parts) + ". Nobody clicked this approval, so nobody is waiting to tell you what to do next.";
    }

    /// <summary>Brief CA: the approved regions the turn's build never reached, in the card's order.</summary>
    public static List<string> NotAttempted(IReadOnlyList<string> approvedRegions, IReadOnlyDictionary<string, RegionOutcome> outcomes) =>
      (approvedRegions ?? new string[0]).Where(r => outcomes == null || !outcomes.ContainsKey(r)).ToList();

    /// <summary>
    /// Brief CA: AN AUTOMATICALLY APPROVED BUILD THAT STOPPED WITH REGIONS LEFT CONTINUES ON ITS OWN. GPT-6.1 Sol stopped early with regions left, and a typed
    /// "Continue" finished it every time, but under an automatic mode nobody is there to type it. Only where nothing explains the stop: a stopped or cut-off
    /// turn, or a revert or a refusal, which has its own follow-up (U94), is left alone. Under "ask" the user keeps the sentence.
    ///
    /// THE BOUND IS PROGRESS. A continuation that attempted nothing ends there, so a chain of them is at most one per approved region, and a first turn
    /// that attempted nothing gets one try. The approval is the card's: the same regions and the same scripts, and no new card.
    /// </summary>
    public static bool ShouldContinueBuild(bool approvedAutomatically, bool stoppedEarly, FlowEnd end, int notAttempted, int attemptedThisTurn, bool thisTurnIsAContinuation) =>
      approvedAutomatically && !stoppedEarly && end == FlowEnd.Succeeded && notAttempted > 0 && (attemptedThisTurn > 0 || !thisTurnIsAContinuation);

    public static string ContinueLine(IEnumerable<string> regions) =>
      "Dwight is continuing: " + string.Join(", ", regions) + " approved and not attempted yet.";

    public static string ContinueFact(IEnumerable<string> regions) =>
      "PLUGIN FACT (from Dwight, not the user): you stopped with regions this approval covers still not attempted: " + string.Join(", ", regions) +
      ". They need no new card. Nobody clicked this approval, so nobody will type to continue it. Build them now with build_region.";

    private static string Said(RegionOutcome outcome)
    {
      switch (outcome)
      {
        case RegionOutcome.Built: return "was built";
        case RegionOutcome.BuiltWithErrors: return "was built, with errors";
        case RegionOutcome.Reverted: return "reverted, so the canvas is as it was before it";
        case RegionOutcome.RevertedWithResidue: return "reverted, and something of it may remain on the canvas";
        case RegionOutcome.Refused: return "was refused before anything was touched";
        default: return "ended in a state Dwight does not know";
      }
    }

    /// <summary>
    /// U97, Brief BT: a turn that announced a change and raised no card gets ONE follow-up read-only turn. Where the connection can force
    /// the card, that is every mode: the follow-up ends in a card, and the mode decides as it decides any card ("ask" a waiting one,
    /// "never" approved and marked). Where it cannot (Ollama), only under an automatic mode: nobody is there to type, and under "ask" the
    /// user has the turn-end line and an unforced turn would only invite the excuse. <paramref name="caught"/> is the strict catch
    /// (<see cref="TurnShape.AnnouncedForForcing"/>), never the line's, and a follow-up is never followed up.
    /// </summary>
    public static bool ShouldRunForAnnouncement(bool automaticMode, bool canForce, bool caught, bool thisTurnIsAFollowUp) =>
      caught && !thisTurnIsAFollowUp && (canForce || automaticMode);

    /// <summary>U97: said in the thread before the announcement follow-up runs.</summary>
    public const string AnnouncementLine = "Dwight is continuing: the last message announced a change and raised no card.";

    /// <summary>U97: the plugin fact the follow-up turn carries, through the report door, so the model reads it first. Brief BT: no escape
    /// clause. "Or say in one sentence why you cannot" invited the excuses the third lamp run wrote (#1165).</summary>
    public const string AnnouncementFact =
      "PLUGIN FACT (from Dwight, not the user): Your last message announced a change and raised no card. " +
      "Raise the card now.";

    /// <summary>Brief BT: the follow-up raised no card and no refusal explains it.</summary>
    public const string NoCardLine = "Dwight asked for the card that message announced and none came, so nothing changed on the canvas.";

    /// <summary>Brief BT: the follow-up's one request was refused, and the bound that names the refusal did not end the turn.</summary>
    public const string RefusedLine = "Dwight asked for the card that message announced, and the request could not be put to you, so nothing changed on the canvas.";

    /// <summary>
    /// Brief BT: the plugin's sentence for a follow-up that ended with no card. The refusal's note when the bound wrote one, because that
    /// is already the plugin's own words for the refusal's class (#235), and otherwise a fixed sentence. Never the model's prose: the
    /// model's explanation is what the brief says is not the reason.
    /// </summary>
    public static string NoCardSentence(string refusalNote, int refusedRequests) =>
      !string.IsNullOrWhiteSpace(refusalNote) ? refusalNote : refusedRequests > 0 ? RefusedLine : NoCardLine;
  }

  /// <summary>
  /// Brief BT: ONE FORCED TRY PER USER MESSAGE. Keyed by thread and cleared by a message of the user's own, the way
  /// <see cref="AutoApprovalCount"/> is, so an approval that starts another turn does not buy another try.
  /// </summary>
  internal sealed class ForcedTryBound
  {
    private readonly System.Collections.Generic.HashSet<string> _used =
      new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
    private readonly object _gate = new object();

    /// <summary>True the first time for this user message; false after that.</summary>
    public bool TryTake(string threadId)
    {
      lock (_gate) return _used.Add(threadId ?? "");
    }

    public void Reset(string threadId)
    {
      lock (_gate) _used.Remove(threadId ?? "");
    }
  }
}
