using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Grasshopper;
using Grasshopper.Kernel;
using Dwight.Agent;
using Dwight.Data;

namespace Dwight.Simplify
{
  /// <summary>
  /// PHASE-9 U20 - THE TRIAL: does this edited source build, run, and (when the model claimed it) still
  /// produce what the original produced?
  ///
  /// IT IS to-script's BESIDE-VERIFY POINTED AT A COMPONENT THAT ALREADY EXISTS, and that is the whole
  /// design rather than a convenience. The copy is wired from THE ORIGINAL'S OWN SOURCES and solved in the
  /// SAME SOLVE as the original, so the two see identical data and there is no stale baseline by
  /// construction - the property that made to-script's proof worth having.
  ///
  /// THE COPY IS DUPLICATED, NOT RE-DERIVED. to-script computes a new script's interface from a boundary
  /// contract because no component exists yet; here one does, so its ports, type hints and access are
  /// COPIED through Grasshopper's own serialisation rather than recomputed from a reading of it. Every
  /// recomputation is a chance to differ from the thing being compared against, and a trial whose copy
  /// differs from the original in a way nobody declared is measuring the wrong question.
  ///
  /// WHAT IT NEVER DOES: no undo record, nothing committed, the canvas untouched. The copy is placed far
  /// clear of the definition, solved, read and removed - and it is removed in a FRESH callback, never
  /// inside SolutionEnd.
  ///
  /// WHY NOTHING ELSE CAN SEE THE COPY WHILE IT EXISTS, written here because the code depends on it and
  /// the reason lives in another file.
  ///
  /// The copy IS on the live canvas for a while, and this trial spans FOUR scheduled steps - add and wire,
  /// the solve, the SolutionEnd read, then a fresh callback to remove - so it is NOT protected by sitting
  /// inside a single callback. Another scheduled callback could in principle land between the steps, take
  /// a behaviour fingerprint that INCLUDED the copy, and later compare it against one taken after the copy
  /// was gone: preservation would see an object removed that it never placed and REVERT a perfectly good
  /// apply.
  ///
  /// What stops that is <c>DwightSession.Start</c>'s busy gate - <c>if (_running) return null</c>, ONE
  /// OPERATION AT A TIME, process-wide. Every verb enters through <c>Begin</c>, and an agent turn holds the
  /// same claim through <c>BeginOn</c>, so a user clicking Reorganise mid-trial does not get a callback
  /// scheduled between these steps: THE VERB NEVER STARTS - it shows the busy notice and bails. Every
  /// fingerprint call site in the verbs is therefore unreachable while this runs.
  ///
  /// WHAT WOULD BREAK IT, named so a later change cannot remove the guarantee quietly: any path that
  /// schedules a solution WITHOUT holding the session, or a session that ever permits two operations at
  /// once. A guarantee living only in a comment is one an async refactor deletes without noticing, so
  /// <see cref="Run"/> REFUSES when no session is held rather than assuming one.
  ///
  /// THE BOUND, STATED HERE BECAUSE A CARD MUST NEVER IMPLY MORE THAN THE TRIAL TOOK. This is SINGLE-INPUT
  /// verification, #72's ceiling: it proves the new source reproduces the original FOR THE CURRENT INPUT
  /// STATE, never that it computes the same function. A script that overfits the one state it was tested
  /// against passes here. And a second, opposite class: a script using randomness without a seed differs
  /// between the two instances and FAILS an honest preserves - a false negative, which is the safe
  /// direction (a good edit refused, never a bad one passed) and whose way through is to declare "changes".
  /// </summary>
  internal static class ScriptEditTrial
  {
    /// <summary>How long the off-canvas solve may take before the trial gives up. A trial that never
    /// returns is a turn that never ends, so this is a real bound rather than a formality - and a timeout
    /// reports CouldNotCheck, never a pass (#109).</summary>
    private const double TimeoutSeconds = 20.0;

    /// <summary>
    /// Run the edited source beside the original and report what was observed - FACTS ONLY. The judgement
    /// is <see cref="ScriptEditVerdict"/>'s, which is GH-free and therefore tested; nothing in this file
    /// decides an outcome.
    /// </summary>
    public static void Run(GH_Document doc, Guid targetGuid, string newSource,
      IReadOnlyList<string> addInputs, IReadOnlyList<string> addOutputs, Action<ScriptTrialFacts> done,
      IReadOnlyDictionary<string, ScriptAccess> access = null)
    {
      if (doc == null) { done?.Invoke(NotRun("the document is no longer available")); return; }

      // THE PRECONDITION THAT MAKES THE COPY UNOBSERVABLE, asserted rather than assumed (see the summary).
      // The trial puts a component on the live canvas across several scheduled steps, and the only thing
      // that stops another operation scheduling a callback between them is the session's one-at-a-time
      // busy gate. So: no session held, no trial. This converts a comment into a precondition, and it is
      // the assertion that fails loudly the day someone calls a trial from a path that does not hold one.
      if (!DwightSession.IsBusy)
      {
        done?.Invoke(NotRun("Dwight was not holding the session, so it did not put a test copy on the canvas " +
                            "- another operation could have read it mid-check"));
        return;
      }

      // A trial that cannot SOLVE has not checked anything, and must not read as one that did (#109).
      if (SimplifyApply.SolverDisabled(doc)) { done?.Invoke(NotRun("the Grasshopper solver is disabled")); return; }
      SimplifyApply.EnsureSolved(doc);   // never compare against a stale baseline (the Stage-5 freshness guard)

      doc.ScheduleSolution(10, d =>
      {
        IGH_Component copy = null;
        try
        {
          var original = d.FindObject(targetGuid, false) as IGH_Component;
          if (original == null) { done?.Invoke(NotRun("the component is no longer on the canvas")); return; }

          if (!ScriptComponentShim.TryDuplicate(original, out copy, out var duplicateError))
          { done?.Invoke(NotRun(duplicateError)); return; }

          if (!ScriptComponentShim.SetSource(copy, newSource, out var sourceError))
          { done?.Invoke(NotRun(sourceError)); return; }

          // The original's outputs, by index, are what a "preserves" claim is measured against. Captured
          // BEFORE the appended ports exist, so appended outputs are excluded from the compare - they are
          // new by definition, and "preserves" means the outputs that EXISTED still produce what they did.
          var originalOutputs = ScriptComponentShim.ScriptOutputs(original);
          var comparedCount = originalOutputs.Count;

          if (!ScriptComponentShim.Append(copy, addInputs, addOutputs, out var appendError))
          { done?.Invoke(NotRun(appendError)); return; }

          // Brief CP: the copy is fed as the card says the component will be, or the check would be of a different script.
          foreach (var kv in access ?? new Dictionary<string, ScriptAccess>())
            if (!ScriptComponentShim.SetInputAccess(copy, kv.Key, kv.Value, out var accessError))
            { done?.Invoke(NotRun(accessError)); return; }

          try { copy.CreateAttributes(); } catch { }
          try
          {
            var at = original.Attributes.Pivot;
            copy.Attributes.Pivot = new PointF(at.X, at.Y + 4000f);   // far clear of the definition
          }
          catch { }
          d.AddObject(copy, false);

          // THE COPY MUST START UNWIRED, AND THAT IS ASSERTED RATHER THAN ASSUMED.
          //
          // A component's own serialisation writes its input params' SOURCE REFERENCES, so a copy read
          // back from the original's chunk can arrive with Sources already populated - and if those
          // references resolve in this document, the wiring below would AddSource a source the input
          // already has. Two identical sources on one input is not a no-op in Grasshopper: the data
          // arrives TWICE, the copy's outputs diverge from the original's, and a declared "preserves"
          // then fails for a reason that has nothing to do with the edit. Worse, a script insensitive to
          // the duplication would PASS while double-fed.
          //
          // So the sources are cleared explicitly and the result is CHECKED. "It came out unwired on this
          // Rhino" is exactly the kind of fact that changes under a version and takes the verdict with it,
          // so it is not something to rely on.
          if (!Unwire(copy, out var unwireError)) { TryRemove(d, copy); done?.Invoke(NotRun(unwireError)); return; }

          // Wire the copy from THE ORIGINAL'S OWN SOURCES, input by input, so both see the same data in
          // the same solve. These are the upstream output params themselves, so the copy hangs off the very
          // same producers - nothing upstream is duplicated and nothing is solved separately.
          //
          // The bound is the ORIGINAL's input count, so an APPENDED input is never reached and stays
          // unwired. That is correct rather than tolerated - nothing feeds it yet - but it has a
          // consequence the verdict has to carry: if the new source READS that input, the copy sees
          // nothing on it and the outputs may legitimately differ. See ScriptTrialFacts.AppendedInputs.
          for (var i = 0; i < original.Params.Input.Count && i < copy.Params.Input.Count; i++)
          {
            var from = original.Params.Input[i];
            var to = copy.Params.Input[i];
            try { foreach (var src in from.Sources.ToList()) to.AddSource(src); } catch { }
          }

          Arm(d, original, copy, comparedCount, addInputs?.Count ?? 0, done);
        }
        catch (Exception ex)
        {
          TryRemove(d, copy);
          done?.Invoke(NotRun(ex.Message));
        }
      });
    }

    /// <summary>Arm the solve, read the verdict once, and remove the copy in a FRESH callback - never
    /// mutate the document inside SolutionEnd.</summary>
    private static void Arm(GH_Document doc, IGH_Component original, IGH_Component copy, int comparedCount,
      int appendedInputs, Action<ScriptTrialFacts> done)
    {
      GH_Document.SolutionEndEventHandler handler = null;
      Eto.Forms.UITimer timeout = null;
      var fired = false;

      void Finish(ScriptTrialFacts facts)
      {
        try { if (handler != null) doc.SolutionEnd -= handler; } catch { }
        try { timeout?.Stop(); } catch { }
        doc.ScheduleSolution(1, d2 => { TryRemove(d2, copy); done?.Invoke(facts); });
      }

      handler = (s, e) =>
      {
        if (fired) return;
        fired = true;
        try
        {
          var facts = new ScriptTrialFacts
          {
            Ran = true,
            Errors = ScriptComponentShim.Errors(copy),
            OriginalWasErrored = ScriptComponentShim.Errors(original).Count > 0,
            // Carried into the verdict because every surface has to say that an appended input was NOT
            // part of what was exercised - it is unwired by construction during the trial.
            AppendedInputs = appendedInputs,
          };

          // AN ERRORED SCRIPT STILL EMITS A NULL ITEM ON EVERY OUTPUT (the Phase-7 live finding), so the
          // compare is only meaningful once the copy BUILDS. Errors gate it; the compare is never trusted
          // to notice.
          if (facts.Errors.Count == 0)
          {
            facts.StillErrored = facts.OriginalWasErrored;   // the copy ran clean, so any error left is the original's

            var originalParams = ScriptComponentShim.ScriptOutputs(original).Take(comparedCount).ToList();
            var copyParams = ScriptComponentShim.ScriptOutputs(copy).Take(comparedCount).ToList();

            // THE COMPARE IS THE GH-FREE CORE'S (U35, #928), AND IT IS HANDED THE IDENTITIES. The capture
            // resolves each side by parameter guid through the document - which is exactly how the original
            // was compared with itself when the copy's parameters still carried the original's guids. The
            // shim now renews them and refuses a copy that shares one; the core refuses again if the two
            // sides it is given share one. A refusal is CouldNotCheck: nothing was measured, so it is
            // neither a pass nor a mismatch.
            var comparison = ScriptTrialCompare.Compare(
              Compared(doc, originalParams),
              Compared(doc, copyParams));
            if (comparison.Refused != null) { Finish(NotRun(comparison.Refused)); return; }

            facts.OutputsCompared = comparison.OutputsCompared;
            facts.OutputsWithData = comparison.OutputsWithData;
            facts.Mismatched = comparison.Mismatched;
          }
          else
          {
            facts.StillErrored = facts.OriginalWasErrored;
          }

          Finish(facts);
        }
        catch (Exception ex) { Finish(NotRun(ex.Message)); }
      };

      try { doc.SolutionEnd += handler; } catch { }
      timeout = new Eto.Forms.UITimer { Interval = TimeoutSeconds };
      timeout.Elapsed += (s, e) =>
      {
        if (fired) return;
        fired = true;
        Finish(NotRun("the edited script did not finish solving in time"));
      };
      timeout.Start();
      doc.ScheduleSolution(10);
    }

    /// <summary>
    /// A trial that did not run, with its reason. It is NEVER a pass and never an empty success: Ran stays
    /// false, so <see cref="ScriptEditVerdict.Outcome"/> can only answer CouldNotCheck (#109 - a vacuous
    /// trial must never read as verified).
    /// </summary>
    private static ScriptTrialFacts NotRun(string reason) =>
      new ScriptTrialFacts { Ran = false, Errors = new[] { reason ?? "unknown error" } };

    /// <summary>
    /// Clear every source off the copy's inputs and CHECK that it worked, so the wiring that follows is
    /// building a known state rather than adding to an unknown one. Refuses rather than proceeding if any
    /// input still holds a source: a copy that is double-fed compares wrongly, and a wrong compare under a
    /// "preserves" claim is a verdict about the wrong thing.
    /// </summary>
    private static bool Unwire(IGH_Component copy, out string error)
    {
      error = null;
      try
      {
        foreach (var p in copy.Params.Input)
          if (p.SourceCount > 0) p.RemoveAllSources();

        var stillWired = copy.Params.Input.Count(p => p.SourceCount > 0);
        if (stillWired > 0)
        {
          error = "Dwight could not clear the test copy's inputs on this Rhino, so it will not run the " +
                  "check: a copy that is fed twice would compare wrongly and the result would be about " +
                  "the wiring rather than about the change.";
          return false;
        }
        return true;
      }
      catch (Exception ex)
      {
        error = "Dwight could not prepare the test copy's inputs (" + ex.Message + ").";
        return false;
      }
    }

    /// <summary>
    /// At least one NON-NULL item - <see cref="OutputItems"/>'s rule. It counted items (<c>DataCount &gt; 0</c>) until
    /// lane C found the gap while building U19: an output holding only nulls counted as "carrying data", so a
    /// "preserves" edit where the original and the edit both put nothing on every output compared null against null,
    /// found them identical, and read as VERIFIED rather than NothingToCompare (#109).
    /// </summary>
    private static bool HasData(IGH_Param p)
    {
      try { return p?.VolatileData != null && OutputItems.HasNonNull(p.VolatileData.AllData(false)); } catch { return false; }
    }

    /// <summary>One side of the compare: each output's IDENTITY, name, sample and whether it carried data. The
    /// sample is captured by guid through the document, so the identity it is captured under is the identity
    /// the core checks - a shared one is refused before anything is read as compared.</summary>
    private static List<ComparedOutput> Compared(GH_Document doc, List<IGH_Param> ps)
    {
      var samples = SimplifyApply.CaptureOutputs(doc, ps.Select(p => p.InstanceGuid).ToList());
      var outs = new List<ComparedOutput>();
      for (var i = 0; i < ps.Count; i++)
        outs.Add(new ComparedOutput
        {
          Identity = ps[i].InstanceGuid,
          Name = NameOf(ps[i], i),
          Sample = i < samples.Count ? samples[i] : new OutputSample("?", ""),
          HasData = HasData(ps[i]),
        });
      return outs;
    }

    /// <summary>The port's name as the user sees it, for the mismatch list. Names only - never values,
    /// which is the feedback-precision law and is why this returns a name rather than a sample (#71).</summary>
    private static string NameOf(IGH_Param p, int index)
    {
      try
      {
        if (!string.IsNullOrWhiteSpace(p?.NickName)) return p.NickName;
        if (!string.IsNullOrWhiteSpace(p?.Name)) return p.Name;
      }
      catch { }
      return "output" + (index + 1);
    }

    private static void TryRemove(GH_Document d, IGH_Component copy)
    {
      try { if (copy != null && d.FindObject(copy.InstanceGuid, false) != null) d.RemoveObject(copy, false); } catch { }
      try { Instances.RedrawCanvas(); } catch { }
    }
  }
}
