using System;
using System.Collections.Generic;
using System.Linq;

namespace Dwight.Agent
{
  /// <summary>
  /// WHAT THE TRIAL ACTUALLY OBSERVED. Facts only - no judgement, and no sentence. The judgement is
  /// <see cref="ScriptEditVerdict"/>'s, which is why it can be tested without Grasshopper.
  /// </summary>
  internal sealed class ScriptTrialFacts
  {
    /// <summary>Did the off-canvas solve RUN and get read? False for the solver-disabled path, a lost
    /// document, or a timeout - and a trial that did not run may never read as a pass (#109).</summary>
    public bool Ran { get; set; }

    /// <summary>The new source's own compile or runtime errors, verbatim. Empty means it built and ran.</summary>
    public IReadOnlyList<string> Errors { get; set; } = new string[0];

    /// <summary>How many of the original's outputs were compared 1:1 against the new version's. Appended
    /// outputs are excluded: they are new by definition, and "preserves" means the outputs that EXISTED
    /// still produce what they produced.</summary>
    public int OutputsCompared { get; set; }

    /// <summary>Of those, how many carried ANY data on either side - a NON-NULL item (<c>Data.OutputItems</c>); an
    /// output of nulls carries none. Zero is the vacuous case, and it is the reason this field exists rather than
    /// being inferred from the mismatch list being empty.</summary>
    public int OutputsWithData { get; set; }

    /// <summary>The ports whose data differed, by name. Names only - never values (#71).</summary>
    public IReadOnlyList<string> Mismatched { get; set; } = new string[0];

    /// <summary>
    /// How many INPUTS this edit appends. They are left unwired during the trial, because nothing feeds
    /// them yet - and that has a consequence every surface has to carry: if the new source READS an
    /// appended input, the copy sees nothing on it, so the outputs may legitimately differ and a
    /// "preserves" claim can fail for a reason the user would call "of course it did".
    ///
    /// It is a FACT rather than an excuse. The refusal says the new input was not wired, so the reader is
    /// not left to guess; and a card, if one is ever offered, must not imply the appended port was
    /// exercised. Rare paths with plausible-looking failures are how a good feature gets a reputation for
    /// being flaky.
    /// </summary>
    public int AppendedInputs { get; set; }

    /// <summary>Was the ORIGINAL already reporting an error before Dwight touched it? Only reachable under
    /// claim=changes, where fixing a broken script is the common case.</summary>
    public bool OriginalWasErrored { get; set; }

    /// <summary>Is it STILL reporting one after the edit? The fact that turns "no new errors" from a
    /// technicality into an answer, for a user whose whole request was "fix this".</summary>
    public bool StillErrored { get; set; }
  }

  internal enum ScriptEditOutcome
  {
    /// <summary>The trial could not run at all - the solver is off, the document is gone, it timed out.
    /// Never a pass, and never silently treated as one.</summary>
    CouldNotCheck = 0,

    /// <summary>The new source did not build, or threw while running.</summary>
    DidNotRun,

    /// <summary>claim=preserves: compared, and every compared output is identical.</summary>
    Preserved,

    /// <summary>claim=preserves: compared, and they differ. The model was wrong about its own edit.</summary>
    NotPreserved,

    /// <summary>
    /// claim=preserves: it ran clean, and THERE WAS NOTHING TO COMPARE - every compared output was empty on
    /// both sides, so the check confirmed nothing.
    ///
    /// IT IS ITS OWN OUTCOME AND NOT A FOOTNOTE ON <see cref="Preserved"/>, which is the whole point. A
    /// vacuous proof that shares an outcome with a real one gets laundered into a fact by the paperwork
    /// downstream of the honest sentence: recorded as "preserved", reported as "preserved", and stated to
    /// the model next turn as "you verified it" (#624). #109's law is that a vacuous trial must never READ
    /// as verified - on any surface, at any later time - and the only way to hold that is to make the two
    /// different values rather than one value with two renderings.
    /// </summary>
    NothingToCompare,

    /// <summary>claim=changes: it built, it ran, and the definition reports no NEW errors.</summary>
    RanClean,
  }

  /// <summary>
  /// THE VERDICT FOR AN EDIT, AND THE WORDS FOR IT - on all three surfaces that outlive the card.
  ///
  /// WHY THE THREE ARE HERE TOGETHER rather than at their own call sites. The claim outlives the card: what
  /// the user approves is stated once, but the outcome is then RECORDED, REPORTED back to the user, and
  /// stated to the model at the start of its next turn. A vacuous check that is honest on the card and
  /// "preserved" in the record has been laundered by its own paperwork - and the model, which reads the
  /// transcript and not the screen, would be entitled to say "you verified it". Putting the three renderings
  /// beside the outcome they come from is what makes that testable in one place.
  ///
  /// GH-free (ADR-091), so the property the architect asked for is asserted rather than reviewed: the word
  /// "verified" cannot appear on the vacuous path, on any of the three.
  /// </summary>
  internal static class ScriptEditVerdict
  {
    /// <summary>
    /// WHAT THE EDIT CLAIMS TO DO, in Dwight's words, from the ENUM alone - shared by every surface that
    /// has to state it, so the card and the report cannot describe the same edit differently.
    ///
    /// IT CARRIES NO DEIXIS, and that is the rule rather than an accident: no "above", "below", "shown
    /// here". A sentence that points at its own surroundings belongs to ONE surface and cannot be shared -
    /// "the difference is in the diff above" is true on a card and FALSE in a report, where there is no
    /// diff above, and it would point at something that is not there. Escaping is a property of the
    /// destination (#335); so is deixis. Each surface adds its own pointing words.
    /// </summary>
    public static string ClaimClause(ScriptEditClaim claim) =>
      claim == ScriptEditClaim.Preserves
        ? "It is meant to leave what the component produces unchanged."
        : "IT CHANGES WHAT THE COMPONENT PRODUCES.";

    /// <summary>
    /// The outcome, from the claim and the facts. A pure function, and the ONLY place an outcome is
    /// decided - so <see cref="ScriptEditOutcome.Preserved"/> is unreachable without something actually
    /// compared, by construction rather than by a check somebody has to remember.
    /// </summary>
    public static ScriptEditOutcome Outcome(ScriptEditClaim claim, ScriptTrialFacts f)
    {
      if (f == null || !f.Ran) return ScriptEditOutcome.CouldNotCheck;
      if (f.Errors != null && f.Errors.Count > 0) return ScriptEditOutcome.DidNotRun;

      if (claim != ScriptEditClaim.Preserves) return ScriptEditOutcome.RanClean;

      if (f.Mismatched != null && f.Mismatched.Count > 0) return ScriptEditOutcome.NotPreserved;

      // THE ORDER HERE IS THE GUARANTEE. Vacuity is tested AFTER the mismatch check and BEFORE the pass, so
      // a run with nothing to compare can never fall through into Preserved - which is exactly how it would
      // have got there, since an empty comparison produces an empty mismatch list.
      if (f.OutputsCompared <= 0 || f.OutputsWithData <= 0) return ScriptEditOutcome.NothingToCompare;

      return ScriptEditOutcome.Preserved;
    }

    /// <summary>Is this an outcome the user may be offered a card for? A trial that did not run, did not
    /// build, or disproved its own claim never reaches one.</summary>
    public static bool MayOfferCard(ScriptEditOutcome outcome) =>
      outcome == ScriptEditOutcome.Preserved
      || outcome == ScriptEditOutcome.NothingToCompare
      || outcome == ScriptEditOutcome.RanClean;

    /// <summary>
    /// THE CARD'S CLAUSE: what Dwight actually checked, in Dwight's own words, computed from what was
    /// compared rather than from what was claimed (#235 - the authority line contains not one word the
    /// model wrote).
    /// </summary>
    public static string CardLine(ScriptEditOutcome outcome, ScriptTrialFacts f)
    {
      switch (outcome)
      {
        case ScriptEditOutcome.Preserved:
          // The appended clause is not decoration: without it the sentence implies the whole new interface
          // was exercised, and an appended input is unwired by construction during the trial.
          return "Dwight ran this version beside the original on the same inputs and compared " +
                 Outputs(f.OutputsCompared) + " - they are identical." + UnexercisedClause(f);

        case ScriptEditOutcome.NothingToCompare:
          // The honest sentence, with a door - every refusal in this product has one, and a statement of
          // emptiness deserves the same.
          return "Dwight ran this version beside the original, but both produced no data, so this did not " +
                 "confirm anything. If you want it checked against real data, give the component inputs and " +
                 "ask again.";

        case ScriptEditOutcome.RanClean:
          // IT STATES ONLY WHAT WAS CHECKED. It used to end "This changes what the component produces -
          // the difference is in the diff above", which duplicated the card's own claim clause AND
          // carried deixis into a shared sentence. The CLAIM is the card's job (ClaimClause); the
          // POINTING is each surface's own; this is the verdict's, and the verdict's job is the check.
          return "Dwight ran this version and the definition still solves" +
                 (f != null && f.OriginalWasErrored
                   ? f.StillErrored
                     ? ". The error the component was reporting before is still there."
                     : ". The error it was reporting before is gone."
                   : " with no new errors.");

        default:
          // NULL, NOT A SENTENCE. An outcome that may not be offered has no card, and the first version of
          // this returned "Dwight could not check this edit" for ALL of them - which is FALSE for
          // NotPreserved, where Dwight checked thoroughly and the check is exactly why there is no card. A
          // default branch that manufactures a plausible sentence for states it was not written for is the
          // #613 class: a claim reached by a state nobody enumerated. Returning null makes the absence of a
          // card the type's answer rather than a sentence nobody reads carefully.
          return null;
      }
    }

    /// <summary>
    /// THE SUBJECT OF A REPORT - the component by nickname (or type word) AND id, as <c>set_value</c>'s report
    /// names its slider (U37, #930). The gate's thread lost track of which component a report was about:
    /// "Dwight verified the outputs are unchanged (1 output compared)" named nothing, the model matched it to
    /// the wrong component and stopped trusting its own record. A report that does not say what it applied
    /// to cannot be matched to the canvas by the model or the user.
    /// </summary>
    public static string Subject(ScriptEditChange change)
    {
      if (change == null) return null;
      var label = string.IsNullOrWhiteSpace(change.Label) ? "the script component" : change.Label.Trim();
      return label + " (" + change.TargetId + ")";
    }

    /// <summary>Brief CP: the access an edit changes, in one sentence for the card and the report. Null when it changes none.</summary>
    public static string AccessSentence(ScriptEditChange change)
    {
      var changes = change?.AccessChanges ?? new AccessChange[0];
      if (changes.Count == 0) return null;
      return "It changes how " + string.Join(" and ", changes.Select(c => c.Name + " is fed, from " + c.From + " to " + c.To + " access")) + ".";
    }

    /// <summary>The report, true of what this edit did. One that kept its source did not replace it, and one that changed an input's access says so.</summary>
    public static string WithAccess(string report, ScriptEditChange change)
    {
      var line = report ?? "";
      if (change != null && change.KeepsSource)
        line = line.Replace("Dwight replaced the source of " + Subject(change) + ". ", "Dwight kept the source of " + Subject(change) + " as it was. ");
      var said = AccessSentence(change);
      return said == null ? line : line.TrimEnd() + " " + said;
    }

    /// <summary>
    /// WHAT THE USER READS AFTERWARDS. It carries the same distinction the card did, because an outcome
    /// that softens on its way into the report is one nobody can act on later - and it names its SUBJECT
    /// (<see cref="Subject"/>), because a report with no subject cannot be matched to the canvas (#930).
    /// A null subject renders the sentence without one, for the caller that has none to give.
    /// </summary>
    public static string ReportLine(ScriptEditOutcome outcome, ScriptTrialFacts f, string subject)
    {
      // The sentence keeps its "Applied. " lead (the renderer strips exactly that before its own bold lead-in),
      // and the subject follows it as a clause of its own, or sits inside the verified sentence.
      var named = !string.IsNullOrWhiteSpace(subject);
      var of = named ? " of " + subject.Trim() : "";
      var replaced = named ? "Dwight replaced the source of " + subject.Trim() + ". " : "";

      switch (outcome)
      {
        case ScriptEditOutcome.Preserved:
          return "Applied. Dwight verified the outputs" + of + " are unchanged (" + Outputs(f.OutputsCompared) + " compared).";

        // THE REPORT MUST NOT LOSE THE CLAIM. After applying, a user still needs to know the edit was
        // declared as a CHANGE rather than a preservation - so the claim is rendered here from the SAME
        // source the card renders it from (ClaimClause), and the report does not quietly become the
        // weaker of the two surfaces because the duplication was cut on the card's side.
        case ScriptEditOutcome.RanClean:
          return "Applied. " + replaced + ClaimClause(ScriptEditClaim.Changes) + " Dwight checked the definition still solves" +
                 (f != null && f.OriginalWasErrored && !f.StillErrored ? ", and the error it was reporting is gone." : ".");

        case ScriptEditOutcome.NothingToCompare:
          // DELIBERATELY WRITTEN WITHOUT THE LOADED VOCABULARY. The first version read "could NOT confirm
          // the outputs are unchanged", which is honest and carries the word "unchanged" one negation away
          // from a claim. A test that scans this path for those words is blunt on purpose - a blunt scan
          // cannot be fooled by a later edit that drops the "not", and on a consent surface a missed
          // negation is the whole defect. So the sentence avoids the vocabulary rather than negating it.
          return "Applied. " + replaced + "Dwight compared nothing: the component produced no data on either version, so " +
                 "its outputs were never checked.";

        default:
          // A REFUSED OUTCOME HAS NO REPORT OF ITS OWN: nothing was applied, so there is no "afterwards". These
          // branches used to hold their own "Not applied." sentences - which no user could reach, because a
          // report is only built after a card may be offered, while a test kept their wording green (#837: a
          // test over a sentence nobody can read). The sentence a user DOES read is RefusedLine's, at turn end,
          // and this delegates to it so there is one wording for a refusal, not two that can drift.
          return RefusedLine(null, outcome, f);
      }
    }

    /// <summary>
    /// WHAT THE USER READS WHEN A TURN ENDS WITH AN EDIT REFUSED - in Dwight's own words, or NULL when the outcome
    /// may be offered (a card then carries it, and a second sentence would be noise).
    ///
    /// U20's sitting found the gap (row 3c). A refusal goes to the MODEL mid-loop, deliberately: the model can
    /// correct the source or re-declare the claim, and a "Not applied" shown to the user before a retry that then
    /// earns a card would be wrong. But when the TURN ENDS with no card for that component, the refusal is a
    /// finished fact about what Dwight checked - and it reached the user only as the model's paraphrase, the two
    /// voices #235 forbids. "Terminal" means exactly that: the turn ended with no card for the target, NOT "the
    /// attempts ran out" - the usual case is a model that stops after one refusal and explains it itself.
    ///
    /// So the caller, at turn end, calls this with each edited target's LAST outcome and shows what comes back.
    /// A card ends a turn (#611a), so a target whose last outcome was offered has a card, and this returns null.
    ///
    /// IT NAMES THE COMPONENT, because there is no card beside it to say which one, and it carries no deixis:
    /// nothing here points at anything around it (#335's rule for destinations, applied to pointing).
    /// </summary>
    public static string RefusedLine(string label, ScriptEditOutcome outcome, ScriptTrialFacts f)
    {
      if (MayOfferCard(outcome)) return null;
      var what = string.IsNullOrWhiteSpace(label) ? "the script component" : label.Trim();

      switch (outcome)
      {
        case ScriptEditOutcome.NotPreserved:
          // The user is told the MODEL was wrong about its own edit, and that DWIGHT is the thing that
          // caught it - the outcome sentence is the reassuring one, and it is last.
          return "Not applied. Dwight wrote a change to " + what + " and said it would keep its outputs the " +
                 "same. It checked by running the new version beside the original, and the outputs differ on " +
                 Names(f?.Mismatched) + "." + NotWiredYetClause(f) + " Nothing on your canvas was touched.";

        case ScriptEditOutcome.DidNotRun:
          return "Not applied. Dwight's new version of " + what + " did not run, so Dwight did not offer it. " +
                 "Nothing on your canvas was touched.";

        default:
          // The same reason the model was given (U37, #930), so the user's line and the model's fact cannot
          // tell two stories about one refusal.
          return "Not applied. Dwight could not check its edit to " + what + ReasonParenthesis(f) +
                 ", so it did not offer it. Nothing on your canvas was touched.";
      }
    }

    private static string ReasonParenthesis(ScriptTrialFacts f)
    {
      var reason = Reason(f);
      return reason == null ? "" : " (" + reason + ")";
    }

    /// <summary>
    /// WHAT THE MODEL IS TOLD WHEN THE TRIAL DID NOT CLEAR - the refusal's whole body, and the repair loop's
    /// engine (ADR-092's shape): the outcome, where it stands on its attempts, and for a failure to run, the
    /// compiler's or interpreter's own words with the reflected signatures of every type they named.
    ///
    /// IT LIVED IN THE GH-COUPLED PANEL, and that is how it shipped wrong. The header was chosen from the edit's
    /// display LABEL, which is a nickname or a friendly word and never a type name, so every C# compile failure
    /// was sent "FAILED TO RUN. The interpreter said:" - and nothing could test it where it was. It is here now,
    /// choosing from the edit's <see cref="ScriptEditChange.Compiled"/>, which is derived from the component's
    /// type; the panel passes the reflection in and composes nothing itself.
    /// </summary>
    public static string RefusalBody(ScriptEditChange change, ScriptEditOutcome outcome, ScriptTrialFacts facts,
      int attempt, int maxAttempts, Func<IReadOnlyList<string>, string> reflectedApi)
    {
      var left = maxAttempts - attempt;
      var standing = left > 0
        ? " This was attempt " + attempt + " of " + maxAttempts + " on this component."
        : " That was your last of " + maxAttempts + " attempts on this component: do not send " +
          "another version - tell the user what you were trying to change and what went wrong.";

      var body = ModelFact(outcome, facts) + standing;

      if (outcome == ScriptEditOutcome.DidNotRun && facts?.Errors != null && facts.Errors.Count > 0)
      {
        // THE REFLECTION IS C#-ONLY (U37, #930; PR #35's finding 1 seen live in row 3h). It reflects RhinoCommon
        // types out of a .NET COMPILER's error; a Python NameError names a Python name, and answering it with
        // "FooBarWidget: NO SUCH TYPE in RhinoCommon" is a category error that the model then repeated to the
        // user as "cross-checked against RhinoCommon". So it is asked only for a compiled language - and never
        // for one whose language is unknown, which would be a guess about which compiler spoke (#208).
        string api = null;
        if (change?.Compiled == true)
          try { api = reflectedApi?.Invoke(facts.Errors); } catch { api = null; }   // reflection is help, never a gate
        body += Environment.NewLine + Environment.NewLine +
                Dwight.Simplify.CompileRepair.Text(facts.Errors, change?.Compiled, api);
      }

      return body;
    }

    /// <summary>
    /// WHAT THE MODEL IS TOLD, at the start of the turn after (#573/#624). It is the surface that matters
    /// most for the vacuous case: chrome cannot correct the model, so a screen that says "could not
    /// confirm" while the transcript says "preserved" leaves the model believing the stronger thing - and
    /// the model is the one that will later tell the user about it.
    /// </summary>
    public static string ModelFact(ScriptEditOutcome outcome, ScriptTrialFacts f)
    {
      switch (outcome)
      {
        case ScriptEditOutcome.Preserved:
          return "Your last edit was applied, and its outputs were compared against the original's and found " +
                 "identical (" + Outputs(f.OutputsCompared) + ").";

        case ScriptEditOutcome.NothingToCompare:
          return "Your last edit was applied, but the equivalence check CONFIRMED NOTHING: the component " +
                 "produced no data on either version, so nothing was compared. Do not tell the user their " +
                 "outputs were checked - they were not.";

        case ScriptEditOutcome.RanClean:
          return "Your last edit was applied and the definition still solves" +
                 (f != null && f.OriginalWasErrored
                   ? f.StillErrored ? ", and the error it was reporting before is STILL THERE." : ", and the error it was reporting before is gone."
                   : " with no new errors.") +
                 " Its outputs were NOT compared against the original's - you declared this a change.";

        case ScriptEditOutcome.NotPreserved:
          return "Your last edit was NOT applied. You declared it would preserve the component's behaviour, " +
                 "and it does not: the outputs differ on " + Names(f?.Mismatched) + "." + NotWiredYetClause(f) +
                 " Either correct the source so the behaviour is unchanged, or ask again declaring \"changes\" " +
                 "if the difference is intended - the user will read the diff and decide.";

        case ScriptEditOutcome.DidNotRun:
          return "Your last edit was NOT applied: the new source did not run.";

        default:
          // THE REASON IS CARRIED (U37, #930). The trial writes why it did not run into Errors - the document
          // gone, the session not held, the solver disabled, the copy refused, a timeout - and for three rows
          // of the gate this sentence dropped it, so neither the model nor the user learned which of seven
          // paths fired, and the model invented a cause each time. A fact the plugin states beats a guess the
          // model has to make; the reason is the plugin's own sentence, carried verbatim.
          return "Your last edit was NOT applied, because Dwight could not check it" + ReasonClause(f) +
                 " The canvas is unchanged.";
      }
    }

    /// <summary>
    /// WHY A TRIAL DID NOT RUN, as the trial recorded it, for the two channels that say "could not check it"
    /// (#624 - one reason, both channels). ": <reason>." when the trial recorded one; when it recorded none -
    /// facts null, as the panel's own timeout path leaves them - the sentence says THAT, rather than nothing,
    /// because a silence about the reason is what the model fills in itself.
    /// </summary>
    private static string ReasonClause(ScriptTrialFacts f)
    {
      var reason = Reason(f);
      return reason == null ? ", and its check recorded no reason." : ": " + reason + ".";
    }

    /// <summary>The recorded reason(s), joined and trimmed of their own final stop, or null when none.</summary>
    private static string Reason(ScriptTrialFacts f)
    {
      var parts = (f?.Errors ?? new string[0])
        .Where(e => !string.IsNullOrWhiteSpace(e))
        .Select(e => e.Trim().TrimEnd('.'))
        .Where(e => e.Length > 0)
        .ToList();
      return parts.Count == 0 ? null : string.Join("; ", parts);
    }

    /// <summary>On a PASS: say that an appended input was not part of what was exercised, so the sentence
    /// cannot be read as covering the whole new interface.</summary>
    private static string UnexercisedClause(ScriptTrialFacts f)
    {
      var n = f?.AppendedInputs ?? 0;
      if (n <= 0) return "";
      return n == 1
        ? " The new input is not wired to anything yet, so it was not part of that check."
        : " The " + n + " new inputs are not wired to anything yet, so they were not part of that check.";
    }

    /// <summary>
    /// On a FAILED preserves: name the likely innocent cause before the user reaches for a worse one. If
    /// the new source reads an input nothing feeds yet, a difference is expected rather than alarming - and
    /// a failure the reader would call "of course it did", reported without that clause, is how a working
    /// feature earns a reputation for being flaky.
    /// </summary>
    private static string NotWiredYetClause(ScriptTrialFacts f)
    {
      var n = f?.AppendedInputs ?? 0;
      if (n <= 0) return "";
      return n == 1
        ? " The new input is not wired to anything yet, so the check ran without it - if the script reads it, that alone would explain the difference."
        : " The " + n + " new inputs are not wired to anything yet, so the check ran without them - if the script reads them, that alone would explain the difference.";
    }

    private static string Outputs(int n) => n + " output" + (n == 1 ? "" : "s");

    private static string Names(IReadOnlyList<string> names) =>
      names == null || names.Count == 0 ? "at least one output" : string.Join(", ", names);
  }
}
