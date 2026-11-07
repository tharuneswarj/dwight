using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;
using Grasshopper.Kernel.Undo;
using Grasshopper.Kernel.Undo.Actions;
using Dwight.Data;

namespace Dwight.Simplify
{
  /// <summary>What the user chose on the preview. BOTH choices verify first (the ruling): the choice decides what
  /// happens AFTER the script has proven itself on live data, never whether it is proven.</summary>
  internal enum ToScriptChoice { Replace, Alongside }

  /// <summary>The retained context for a to-script apply, built at preview time on the fresh graph so the accept
  /// works in stable guid space. Every structural field is plugin-computed from the boundary contract.</summary>
  internal sealed class ToScriptResolve
  {
    public GH_Document Doc;
    public ScriptLanguage Language;
    public string Source;
    public ScriptInterface Iface;
    public List<Guid> Originals = new List<Guid>();                                   // the selection's components
    public Dictionary<int, (Guid Src, int Port)> ExtIn = new Dictionary<int, (Guid, int)>();          // input ext -> external SOURCE output port
    public Dictionary<int, List<(Guid Obj, int Port)>> ExtOut = new Dictionary<int, List<(Guid, int)>>(); // output ext -> external CONSUMER input ports
    public List<Guid> BoundaryOutParams = new List<Guid>();   // the ORIGINALS' selection-side output params, ext order (the 1:1 compare target)
    public List<Guid> ConsumerParams = new List<Guid>();      // the external consumer inputs (the post-splice layer-2 surface)
    public RectangleF SelectionBounds;
    public string ShapeHash;                                  // topology guard for a deferred "replace them now"
    public Guid ScriptGuid;                                   // written back on commit (the applied-state / locator)
  }

  /// <summary>
  /// WP-4: the to-script APPLY - the first path that DELETES the user's components, and the first where model
  /// output EXECUTES on their machine. It therefore carries TWO INDEPENDENT PROOFS, because they catch different
  /// failures:
  ///
  ///   1. BESIDE-VERIFY (before anything is replaced): the script is instantiated beside the originals, wired in
  ///      PARALLEL from the same external sources, and solved. Its outputs are compared 1:1 against the originals'
  ///      boundary outputs IN THE SAME SOLVE - so there is no stale baseline by construction. A compile/runtime
  ///      error, or any per-output mismatch, rolls the whole thing back pre-commit: the canvas never changed.
  ///   2. POST-SPLICE LAYER 2 (Replace only): after the consumers are rewired and the originals deleted, the
  ///      definition is re-solved and the external consumers' data is compared to what they held before. Proof 1
  ///      says "the script reproduces the outputs"; proof 2 says "the splice preserved them". Neither implies the
  ///      other - the splice is where a wrong port would show up, and the script is where wrong logic would.
  ///
  /// Plus the spine the phase already earned: layer-1 predicted-delta (a stray edit outside the declared change
  /// reverts), the type-keyed completeness guard (an accepted change that no-ops reverts - RR14's law, extended
  /// here to "the script is wired and every original is gone"), whitelist-gated instantiation, and ONE
  /// GH_UndoRecord for either choice. A script that ERRORS still emits a null item on every output (the live
  /// finding), so errors are checked BEFORE the compare - never left for the compare to notice.
  ///
  /// THE CEILING, NAMED HONESTLY (RR20 #72): this is SINGLE-INPUT verification. It proves the script reproduces the
  /// originals FOR THE CURRENT INPUT STATE. It CANNOT distinguish "computes the same function" from "returns the
  /// constant that happens to be right today" - a script that OVERFITS its one test case passes here. Nothing in
  /// this file should be read as a stronger claim than that. The fix is a SECOND INPUT STATE (perturb the sliders /
  /// boundary constants, re-solve, compare again), and it is deliberately NOT in this phase. It is also exactly why
  /// the refine loop's output feedback may never carry the EXPECTED VALUES (#71): handing the model the number it
  /// failed to produce would invite it to hardcode that number, and this verification would then certify it.
  /// </summary>
  internal static class ToScriptApply
  {
    public static void Run(ToScriptResolve rc, ToScriptChoice choice, Action<ApplyOutcome> done)
    {
      var doc = rc?.Doc;
      if (doc == null) { done?.Invoke(ApplyOutcome.Fail("To script hit a problem", "The document is no longer available.")); return; }

      if (SimplifyApply.SolverDisabled(doc))
      { done?.Invoke(ApplyOutcome.Stop("Enable the solver first", "Dwight verifies a script by solving it beside your components and comparing the results, which needs the Grasshopper solver enabled. Enable it, then try again.")); return; }
      SimplifyApply.EnsureSolved(doc);   // never verify against a stale baseline (the Stage-5 freshness guard)

      doc.ScheduleSolution(10, d =>
      {
        var actions = new List<IGH_UndoAction>();
        try
        {
          var beforeDoc = BehaviourFingerprint.ReadDoc(d);
          var consumerBefore = SimplifyApply.CaptureOutputs(d, rc.ConsumerParams);   // for proof 2 (Replace)

          var comp = ScriptComponentShim.TryCreate(rc.Language, out var createError);
          if (comp == null) { done?.Invoke(ApplyOutcome.Fail("Couldn't build the script", createError)); return; }
          if (!ScriptComponentShim.Shape(comp, rc.Iface, rc.Language, rc.Source, out var shapeError))
          { done?.Invoke(ApplyOutcome.Fail("Couldn't build the script", shapeError)); return; }

          var guid = Guid.NewGuid();
          try { comp.NewInstanceGuid(guid); } catch { guid = comp.InstanceGuid; }
          try { comp.NickName = ScriptTypeMap.Label(rc.Language); } catch { }
          try { comp.CreateAttributes(); } catch { }
          comp.Attributes.Pivot = BesidePivot(rc);   // beside the originals, clear of them - it must not overlap what it is being compared against
          d.AddObject(comp, false);
          actions.Add(new GH_AddObjectAction(comp));   // from here every mutation is in the recorded actions
          rc.ScriptGuid = guid;

          // G.1 harvest: the script's intrinsic tokens BEFORE wiring, so the prediction owns the wiring.
          var harvested = BehaviourFingerprint.ReadObject(comp);

          // wire the script's inputs IN PARALLEL from the very sources the selection reads (the originals keep
          // their own wires - that is what makes this a comparison and not yet a change).
          for (var i = 0; i < rc.Iface.Inputs.Count && i < comp.Params.Input.Count; i++)
          {
            if (!rc.ExtIn.TryGetValue(rc.Iface.Inputs[i].Ext, out var src)) continue;
            var sourceParam = OutputPortParam(d.FindObject(src.Src, false), src.Port);
            if (sourceParam != null) comp.Params.Input[i].AddSource(sourceParam);
          }

          ArmBesideVerify(d, rc, comp, harvested, actions, beforeDoc, consumerBefore, choice, done);
        }
        catch (Exception ex)
        {
          try { SimplifyApply.Rollback(d, actions); } catch { }
          done?.Invoke(ApplyOutcome.Fail("To script hit a problem", "Dwight hit an error building the script: " + ex.Message + " Nothing on the canvas changed."));
        }
      });
    }

    // ==== PROOF 1: the beside-verify ======================================================================

    /// <summary>Hook the imminent solution, then compare the script's outputs to the originals' - captured in the
    /// SAME solve, so both sides saw identical inputs and no tolerance is needed anywhere (the bit-exact standard).
    /// Everything here is PRE-COMMIT: a failure rolls the actions back by hand and no undo event is ever recorded,
    /// so the user's canvas is untouched rather than "restored".</summary>
    private static void ArmBesideVerify(GH_Document doc, ToScriptResolve rc, IGH_Component comp, FpObject harvested,
      List<IGH_UndoAction> actions, FpDoc beforeDoc, List<OutputSample> consumerBefore, ToScriptChoice choice, Action<ApplyOutcome> done)
    {
      GH_Document.SolutionEndEventHandler handler = null;
      Eto.Forms.UITimer timeout = null;
      var fired = false;

      void Unhook()
      {
        try { if (handler != null) doc.SolutionEnd -= handler; } catch { }
        try { timeout?.Stop(); } catch { }
      }

      // RR17 #37: every rejection carries its reason in a MACHINE-USABLE shape as well as a human one, so the
      // Phase-8 bounded refine loop can hand the model its own compiler error / per-output diff and retry - rather
      // than the user paying for a call and getting nothing back.
      void Reject(string title, string message, RefineFeedback refine)
      {
        Unhook();
        SimplifyApply.Rollback(doc, actions);   // pre-commit: the script node and its wires simply never happened
        done?.Invoke(ApplyOutcome.Stop(title, message).With(refine));
      }

      handler = (sender, e) =>
      {
        if (fired) return;
        fired = true;
        try
        {
          // An errored script STILL emits a null item on every output, so the error check MUST precede the
          // compare - a comparison of nulls against nulls would otherwise "pass" a script that never ran.
          var errors = ScriptComponentShim.Errors(comp);
          if (errors.Count > 0)
          {
            var compiles = rc.Language == ScriptLanguage.CSharp;
            Reject(compiles ? "The C# didn't compile" : "The Python didn't run",
              "Dwight wrote a script that doesn't run, so it removed it - nothing on your canvas changed.\n\n" + errors[0],
              compiles ? RefineFeedback.Compile(errors[0]) : RefineFeedback.Runtime(errors[0]));
            return;
          }

          var originals = SimplifyApply.CaptureOutputs(doc, rc.BoundaryOutParams);
          var scriptOuts = SimplifyApply.CaptureOutputs(doc, ScriptComponentShim.ScriptOutputs(comp).Select(p => p.InstanceGuid).ToList());
          var bad = OutputEquivalence.Mismatches(originals, scriptOuts);
          if (bad.Count > 0)
          {
            // RR20 #85: built + runs, but the outputs did NOT match. This is NOT a "couldn't" - it is an UNVERIFIED
            // DRAFT. It cannot Replace (that would delete working components for an unproven script), but it is
            // real, runnable code the user may want to inspect and finish. So keep it ALONGSIDE, clearly labelled
            // as a draft Dwight could not confirm, originals untouched - the externalize demote-to-advice precedent.
            // The mismatch still rides RefineFeedback for the Phase-8 oracle. (A compile/runtime error stays a hard
            // Reject above - there is nothing to inspect in code that does not run.)
            Unhook();
            var report = MismatchReport(rc, bad);
            doc.ScheduleSolution(1, d2 => FinishDraft(d2, rc, comp, harvested, actions, beforeDoc, report,
              RefineFeedback.Mismatch(MismatchedOutputNames(rc, bad)), done));
            return;
          }

          Unhook();   // PASSED. Finish in a fresh scheduled callback - never mutate inside SolutionEnd.
          if (choice == ToScriptChoice.Alongside)
            doc.ScheduleSolution(1, d2 => FinishAlongside(d2, rc, comp, harvested, actions, beforeDoc, done));
          else
            doc.ScheduleSolution(1, d2 => FinishReplace(d2, rc, comp, harvested, actions, beforeDoc, consumerBefore, done));
        }
        catch (Exception ex)
        {
          Reject("Could not verify the script", "Dwight could not compare the script's results (" + ex.Message + "), so it removed it. Nothing on your canvas changed.", null);   // nothing a model could act on
        }
      };

      try { doc.SolutionEnd += handler; } catch { }
      timeout = new Eto.Forms.UITimer { Interval = 20.0 };   // a script can be slow; still never hang forever
      timeout.Elapsed += (s, e) =>
      {
        if (fired) return;
        fired = true;
        Reject("Could not verify the script", "The script did not finish solving in time, so Dwight removed it. Nothing on your canvas changed.", RefineFeedback.Runtime("the script did not finish solving within the time limit - it is too slow or does not terminate"));
      };
      timeout.Start();
    }

    // ==== accept path A: keep the verified copy ===========================================================

    /// <summary>"Add alongside" - the verified parallel copy IS the final state (the ruling): labelled, placed
    /// below the selection, originals untouched. Nearly free, because the copy already exists from the proof.</summary>
    private static void FinishAlongside(GH_Document d, ToScriptResolve rc, IGH_Component comp, FpObject harvested,
      List<IGH_UndoAction> actions, FpDoc beforeDoc, Action<ApplyOutcome> done)
    {
      try
      {
        var group = new GH_Group();
        group.CreateAttributes();
        group.NickName = $"Dwight: {rc.Originals.Count} components as {ScriptTypeMap.Label(rc.Language)}";
        group.Colour = Color.FromArgb(40, 129, 216, 208);
        group.AddObject(comp.InstanceGuid);
        d.AddObject(group, false);
        actions.Add(new GH_AddObjectAction(group));   // groups are non-computational: invisible to the fingerprint

        if (!Layer1Ok(d, rc, comp, harvested, beforeDoc, removed: new List<Guid>()))
        {
          SimplifyApply.Rollback(d, actions);
          done?.Invoke(ApplyOutcome.Stop("To script stopped", "The applied change did not match what the preview declared - a stray edit slipped in - so Dwight reverted it. Nothing on the canvas changed. Please report this.").With(RefineFeedback.Structural("the apply touched something the plan did not declare")));
          return;
        }
        if (!CompletenessOk(d, rc, comp, spliced: false, out var incomplete))
        {
          SimplifyApply.Rollback(d, actions);
          done?.Invoke(ApplyOutcome.Stop("To script reverted", $"The script did not end up wired as intended ({incomplete}), so Dwight removed it - nothing on the canvas changed. Please report this.").With(RefineFeedback.Incomplete(incomplete)));
          return;
        }

        // COMMIT - the LAST statement in the pre-commit try (MUST-FIX 3). A manual Rollback past this point would
        // reverse actions the committed undo record still owns, so a later Ctrl-Z would re-undo already-reverted
        // objects: the scope split is what keeps the two revert mechanisms from both claiming the same actions.
        { var undoName = $"Dwight: To script ({ScriptTypeMap.Label(rc.Language)}, alongside)"; Dwight.Session.ChangeLog.NoteVerb(d, d.UndoUtil.RecordEvent(undoName, actions), undoName, DwightVerbs.Simplify); }
      }
      catch (Exception ex)
      {
        // PRE-COMMIT only: no undo event was recorded, so reversing the actions by hand is the correct revert.
        try { SimplifyApply.Rollback(d, actions); } catch { }
        done?.Invoke(ApplyOutcome.Fail("To script hit a problem", "Dwight hit an error adding the script: " + ex.Message + " Any partial change was reverted."));
        return;
      }

      // POST-COMMIT: reverts via UNDO, never by hand (MUST-FIX 3).
      try
      {
        Instances.RedrawCanvas();
        done?.Invoke(ApplyOutcome.Ok(
          $"Added the {ScriptTypeMap.Label(rc.Language)} script beside your components - they are untouched. " +
          "Its outputs matched theirs exactly on your current data (verified before it was added). Compare them, then delete whichever side you don't want. One Ctrl-Z removes the script."));
      }
      catch (Exception ex)
      {
        var defused = SimplifyApply.RevertAndDefuseRedo(d);
        done?.Invoke(ApplyOutcome.Fail("Could not finish - reverted",
          "Dwight hit an error after adding the script (" + ex.Message + "), so it reverted the change." + SimplifyApply.RedoWarn(defused)));
      }
    }

    // ==== outcome: an UNVERIFIED DRAFT (built + runs, outputs did not match) ================================

    /// <summary>
    /// RR20 #85: the script builds and runs, but its outputs did not match the originals. It is not a "couldn't" -
    /// it is real, runnable code Dwight could not CONFIRM. So it is kept ALONGSIDE as a clearly-labelled DRAFT (the
    /// user's components are never touched, and it is never a Replace - an unverified script must not delete working
    /// components). The layer-1 + completeness guards still run: a draft may be unverified against the ORIGINALS,
    /// but it must still be exactly the node Dwight declared - a stray edit or an inert add is a real fault, draft
    /// or not, and reverts. One undo record; the mismatch rides RefineFeedback for the Phase-8 oracle.
    /// </summary>
    private static void FinishDraft(GH_Document d, ToScriptResolve rc, IGH_Component comp, FpObject harvested,
      List<IGH_UndoAction> actions, FpDoc beforeDoc, string mismatchReport, RefineFeedback refine, Action<ApplyOutcome> done)
    {
      try
      {
        var group = new GH_Group();
        group.CreateAttributes();
        group.NickName = $"Dwight DRAFT (unverified): {rc.Originals.Count} components as {ScriptTypeMap.Label(rc.Language)}";
        group.Colour = Color.FromArgb(40, 232, 162, 61);   // warning amber, not the tiffany of a verified add
        group.AddObject(comp.InstanceGuid);
        d.AddObject(group, false);
        actions.Add(new GH_AddObjectAction(group));

        if (!Layer1Ok(d, rc, comp, harvested, beforeDoc, removed: new List<Guid>()))
        {
          SimplifyApply.Rollback(d, actions);
          done?.Invoke(ApplyOutcome.Stop("To script stopped", "The draft did not match what the preview declared - a stray edit slipped in - so Dwight reverted it. Nothing on the canvas changed. Please report this.").With(RefineFeedback.Structural("the apply touched something the plan did not declare")));
          return;
        }
        if (!CompletenessOk(d, rc, comp, spliced: false, out var incomplete))
        {
          SimplifyApply.Rollback(d, actions);
          done?.Invoke(ApplyOutcome.Stop("To script reverted", $"The draft did not end up wired as intended ({incomplete}), so Dwight removed it - nothing on the canvas changed. Please report this.").With(RefineFeedback.Incomplete(incomplete)));
          return;
        }

        { var undoName = $"Dwight: To script ({ScriptTypeMap.Label(rc.Language)}, unverified draft)"; Dwight.Session.ChangeLog.NoteVerb(d, d.UndoUtil.RecordEvent(undoName, actions), undoName, DwightVerbs.Simplify); }
      }
      catch (Exception ex)
      {
        try { SimplifyApply.Rollback(d, actions); } catch { }
        done?.Invoke(ApplyOutcome.Fail("To script hit a problem", "Dwight hit an error adding the draft: " + ex.Message + " Any partial change was reverted."));
        return;
      }

      try
      {
        Instances.RedrawCanvas();
        done?.Invoke(ApplyOutcome.AsDraft("Draft added - Dwight couldn't confirm it matches",
          "Dwight solved this script beside your components and its results did NOT match, so it did not replace anything - your components are untouched. " +
          "It is real, working code, but Dwight can't vouch that it does the same thing, so it is added as a labelled DRAFT for you to inspect. " +
          mismatchReport + " One Ctrl-Z removes the draft.").With(refine));
      }
      catch (Exception ex)
      {
        var defused = SimplifyApply.RevertAndDefuseRedo(d);
        done?.Invoke(ApplyOutcome.Fail("Could not finish - reverted",
          "Dwight hit an error after adding the draft (" + ex.Message + "), so it reverted it." + SimplifyApply.RedoWarn(defused)));
      }
    }

    // ==== accept path B: splice it in =====================================================================

    /// <summary>"Replace" - rewire the consumers onto the verified script, delete the originals, all inside the
    /// SAME undo record as the add (so one Ctrl-Z restores everything). The originals are only ever deleted AFTER
    /// the replacement has proven itself on live data.</summary>
    private static void FinishReplace(GH_Document d, ToScriptResolve rc, IGH_Component comp, FpObject harvested,
      List<IGH_UndoAction> actions, FpDoc beforeDoc, List<OutputSample> consumerBefore, Action<ApplyOutcome> done)
    {
      try
      {
        // map each ORIGINAL boundary output param -> the script output that now carries it (ext order = 1:1).
        var scriptOuts = ScriptComponentShim.ScriptOutputs(comp);
        var replacement = new Dictionary<Guid, IGH_Param>();
        for (var j = 0; j < rc.BoundaryOutParams.Count && j < scriptOuts.Count; j++)
          replacement[rc.BoundaryOutParams[j]] = scriptOuts[j];

        // rewire every external consumer: a source owned by the selection is swapped IN PLACE for the script's
        // output, and every other source it had is preserved (a consumer may read from outside too).
        var rewired = new List<IGH_Param>();
        foreach (var consumerGuid in rc.ConsumerParams)
        {
          var input = d.FindObject(consumerGuid, false) as IGH_Param ?? FindNestedParam(d, consumerGuid);
          if (input == null) continue;
          var sources = input.Sources?.ToList() ?? new List<IGH_Param>();
          if (!sources.Any(s => s != null && replacement.ContainsKey(s.InstanceGuid))) continue;

          actions.Add(new GH_WireAction(input));
          var final = new List<IGH_Param>();
          foreach (var s in sources)
            final.Add(s != null && replacement.TryGetValue(s.InstanceGuid, out var repl) ? repl : s);
          input.RemoveAllSources();
          foreach (var s in final.Where(s => s != null)) input.AddSource(s);
          rewired.Add(input);
        }

        // the script takes the selection's place on the canvas, then the originals go.
        try
        {
          actions.Add(new GH_PivotAction(comp));
          comp.Attributes.Pivot = new PointF(rc.SelectionBounds.Left, rc.SelectionBounds.Top + rc.SelectionBounds.Height / 2f);
          comp.Attributes.ExpireLayout();
        }
        catch { }

        foreach (var g in rc.Originals)
        {
          var obj = d.FindObject(g, false);
          if (obj == null) continue;
          actions.Add(new GH_RemoveObjectAction(obj));
          d.RemoveObject(obj, false);
        }

        if (!Layer1Ok(d, rc, comp, harvested, beforeDoc, rc.Originals))
        {
          SimplifyApply.Rollback(d, actions);
          done?.Invoke(ApplyOutcome.Stop("To script stopped", "The replacement did not match what the preview declared - a stray edit slipped in - so Dwight reverted it. Nothing on the canvas changed. Please report this.").With(RefineFeedback.Structural("the replacement touched something the plan did not declare")));
          return;
        }
        if (!CompletenessOk(d, rc, comp, spliced: true, out var incomplete))
        {
          SimplifyApply.Rollback(d, actions);
          done?.Invoke(ApplyOutcome.Stop("To script reverted", $"The replacement did not fully take effect ({incomplete}), so Dwight reverted it - nothing on the canvas changed. Please report this.").With(RefineFeedback.Incomplete(incomplete)));
          return;
        }

        // COMMIT - the LAST statement in the pre-commit try (MUST-FIX 3). Past this point the undo record owns
        // these actions, so a manual Rollback would reverse them a SECOND time and leave a later Ctrl-Z undoing
        // objects that are already gone. The two revert mechanisms must never both claim the same actions.
        { var undoName = $"Dwight: To script ({ScriptTypeMap.Label(rc.Language)}, replaced {rc.Originals.Count})"; Dwight.Session.ChangeLog.NoteVerb(d, d.UndoUtil.RecordEvent(undoName, actions), undoName, DwightVerbs.Simplify); }
      }
      catch (Exception ex)
      {
        // PRE-COMMIT only: no undo event was recorded, so reversing the actions by hand is the correct revert.
        try { SimplifyApply.Rollback(d, actions); } catch { }
        done?.Invoke(ApplyOutcome.Fail("To script hit a problem", "Dwight hit an error replacing the components: " + ex.Message + " Any partial change was reverted."));
        return;
      }

      // POST-COMMIT: reverts via UNDO, never by hand (MUST-FIX 3). ArmSpliceVerify owns its own failures the same
      // way (RevertAndDefuseRedo), so a rejected splice is never one Ctrl-Y from being re-applied.
      try
      {
        Instances.RedrawCanvas();
        ArmSpliceVerify(d, rc, consumerBefore, done);   // PROOF 2: the splice preserved what the script reproduced
      }
      catch (Exception ex)
      {
        var defused = SimplifyApply.RevertAndDefuseRedo(d);
        done?.Invoke(ApplyOutcome.Fail("Could not verify - reverted",
          "Dwight could not start the verification after replacing (" + ex.Message + "), so it reverted the change." + SimplifyApply.RedoWarn(defused)));
      }
    }

    /// <summary>PROOF 2 (Replace only): re-solve and compare the EXTERNAL CONSUMERS' data to what they held before
    /// the splice. Proof 1 said the script reproduces the outputs; this says the rewiring delivered them to the
    /// right places. A selection that feeds nothing has an empty surface here - and is refused before it ever
    /// reaches an apply, so this is never vacuously "verified".</summary>
    private static void ArmSpliceVerify(GH_Document doc, ToScriptResolve rc, List<OutputSample> before, Action<ApplyOutcome> done)
    {
      GH_Document.SolutionEndEventHandler handler = null;
      Eto.Forms.UITimer timeout = null;
      var fired = false;

      void Finish(bool ok, bool couldNotVerify, string title, string message)
      {
        try { if (handler != null) doc.SolutionEnd -= handler; } catch { }   // unhook BEFORE any undo
        try { timeout?.Stop(); } catch { }
        if (ok) { done?.Invoke(ApplyOutcome.Ok(SuccessSummary(rc))); return; }
        var defused = SimplifyApply.RevertAndDefuseRedo(doc);
        var msg = message + SimplifyApply.RedoWarn(defused);
        done?.Invoke(couldNotVerify ? ApplyOutcome.Fail(title, msg) : ApplyOutcome.Stop(title, msg));
      }

      handler = (sender, e) =>
      {
        if (fired) return;
        fired = true;
        try
        {
          var after = SimplifyApply.CaptureOutputs(doc, rc.ConsumerParams);
          if (OutputEquivalence.Equivalent(before, after)) Finish(true, false, null, null);
          else Finish(false, false, "To script reverted",
            "The script matched your components on its own, but after replacing them the definition computed something different - so Dwight put everything back. The canvas is as it was.");
        }
        catch (Exception ex)
        {
          Finish(false, true, "Could not verify - reverted", "Dwight could not read the results after replacing (" + ex.Message + "), so it reverted the change to be safe.");
        }
      };

      try { doc.SolutionEnd += handler; } catch { }
      timeout = new Eto.Forms.UITimer { Interval = 20.0 };
      timeout.Elapsed += (s, e) => { if (!fired) { fired = true; Finish(false, true, "Could not verify - reverted", "The re-solve did not finish in time, so Dwight reverted the change to be safe."); } };
      timeout.Start();
    }

    // ==== the shared guards ===============================================================================

    /// <summary>Layer 1 (ADR-090): predict the after-fingerprint from the before-tokens + the declared delta, and
    /// demand the real document match it. The script node's own tokens are the G.1 harvest; the WIRING comes from
    /// the plan, so a stray rewire shows up as actual != predicted instead of being harvested into the prediction.</summary>
    private static bool Layer1Ok(GH_Document d, ToScriptResolve rc, IGH_Component comp, FpObject harvested, FpDoc beforeDoc, List<Guid> removed)
    {
      var plan = new SimplifyPlan();
      foreach (var g in removed ?? new List<Guid>()) plan.Removed.Add(g);
      if (harvested != null) plan.Added.Add(harvested);

      // every input the plan TOUCHED, with its complete final source list, read from the live document.
      foreach (var ip in comp.Params.Input)
        plan.Wires.Add(new PlanWire(ip.InstanceGuid, (ip.Sources ?? new List<IGH_Param>()).Select(s => s.InstanceGuid).ToArray()));
      foreach (var consumerGuid in rc.ConsumerParams)
      {
        var input = d.FindObject(consumerGuid, false) as IGH_Param ?? FindNestedParam(d, consumerGuid);
        if (input == null) continue;
        plan.Wires.Add(new PlanWire(input.InstanceGuid, (input.Sources ?? new List<IGH_Param>()).Select(s => s.InstanceGuid).ToArray()));
      }

      return BehaviourFingerprint.Of(d) == DeltaPredictor.Predict(beforeDoc, plan);
    }

    /// <summary>
    /// The completeness guard (RR14's law, extended to to-script): equivalence proves "nothing unintended changed";
    /// completeness proves "the intended change HAPPENED". The inert-no-op class lives exactly in that gap, and it
    /// is keyed off the TYPE INVARIANT, never off a nullable metadata field (the RR14 root cause):
    ///   - the script component exists, and every one of its inputs has a source (an unwired input is inert);
    ///   - Replace only: every original is GONE, and no external consumer is left starving.
    /// </summary>
    private static bool CompletenessOk(GH_Document d, ToScriptResolve rc, IGH_Component comp, bool spliced, out string failure)
    {
      failure = null;
      try
      {
        var effects = new List<(bool, string)>();
        effects.Add((d.FindObject(rc.ScriptGuid, false) != null, "the script component is not on the canvas"));
        for (var i = 0; i < comp.Params.Input.Count; i++)
          effects.Add((comp.Params.Input[i].SourceCount > 0, "an input of the script was left unconnected"));

        if (spliced)
        {
          foreach (var g in rc.Originals)
            effects.Add((d.FindObject(g, false) == null, "a component that should have been replaced is still present"));
          foreach (var consumerGuid in rc.ConsumerParams)
          {
            var input = d.FindObject(consumerGuid, false) as IGH_Param ?? FindNestedParam(d, consumerGuid);
            if (input == null) continue;
            effects.Add((input.SourceCount > 0, "a downstream component was left with no input"));
          }
        }

        failure = Completeness.FirstNoOp(effects);   // the GH-free decision (headless-tested)
        return failure == null;
      }
      catch (Exception ex) { failure = ex.Message; return false; }
    }

    // ==== placement, reporting, helpers ===================================================================

    /// <summary>Below the selection's bounds - the script must sit CLEAR of the components it is being compared
    /// against, so the user can see both while it is being proven.</summary>
    private static PointF BesidePivot(ToScriptResolve rc)
    {
      var x = float.IsNaN(rc.SelectionBounds.Left) ? 0f : rc.SelectionBounds.Left;
      var y = float.IsNaN(rc.SelectionBounds.Bottom) ? 0f : rc.SelectionBounds.Bottom;
      return new PointF(x, y + 60f);
    }

    /// <summary>Name the outputs that differed (the per-output correspondence to-script uniquely has), so a
    /// mismatch is "Panel Areas did not match", never an opaque "the results differ".</summary>
    private static string MismatchReport(ToScriptResolve rc, List<int> bad)
    {
      var names = MismatchedOutputNames(rc, bad);
      if (names.Count == 0) return "The results differed.";
      return "The results differed at: " + string.Join(", ", names) + ".";
    }

    /// <summary>The differing outputs BY NAME - shared by the human report and the RR17 #37 structured feedback,
    /// so the model's retry is told exactly what the user was told.</summary>
    private static List<string> MismatchedOutputNames(ToScriptResolve rc, List<int> bad) =>
      bad.Where(i => i >= 0 && i < rc.Iface.Outputs.Count)
         .Select(i => rc.Iface.Outputs[i].Name)
         .Where(n => !string.IsNullOrWhiteSpace(n))
         .Take(6)
         .ToList();

    private static string SuccessSummary(ToScriptResolve rc) =>
      $"Replaced {rc.Originals.Count} components with one {ScriptTypeMap.Label(rc.Language)} script. " +
      "Dwight solved the script beside them first and its outputs matched exactly, then re-solved after replacing them to confirm the definition still computes the same results. One Ctrl-Z reverts everything.";

    private static IGH_Param OutputPortParam(IGH_DocumentObject obj, int port)
    {
      if (obj is IGH_Param p) return p;
      if (obj is IGH_Component c && port >= 0 && port < c.Params.Output.Count) return c.Params.Output[port];
      return null;
    }

    /// <summary>A consumer input lives INSIDE a component, so it is not a top-level document object - find it by
    /// walking the components' input params.</summary>
    private static IGH_Param FindNestedParam(GH_Document d, Guid paramGuid)
    {
      try
      {
        foreach (var obj in d.Objects)
        {
          if (obj is IGH_Component c)
            foreach (var ip in c.Params.Input)
              if (ip.InstanceGuid == paramGuid) return ip;
        }
      }
      catch { }
      return null;
    }
  }
}
