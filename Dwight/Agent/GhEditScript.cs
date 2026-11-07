using System;
using System.Collections.Generic;
using System.Linq;
using Grasshopper;
using Grasshopper.Kernel;
using Dwight.Data;
using Dwight.Simplify;

namespace Dwight.Agent
{
  /// <summary>
  /// U20's APPLIER - the first path that rewrites the user's own code in place.
  ///
  /// EVERY OTHER WRITE DWIGHT OWNS ADDS SOMETHING. A build places components, set_value moves one number, a
  /// rebuild removes what Dwight itself put there. This one REPLACES text the user may have written, in a
  /// component they own, and a failure here is not "a component too many on the canvas" - it is their
  /// script, gone. So the whole file is arranged around two properties: what the user approved is what
  /// executes, and if anything fails the script that comes back is the one they had.
  ///
  /// THE ORDER, which is the design:
  ///   1. THE BASE IS COMPARE-AND-SET. The card showed a diff FROM a specific text. If the component's
  ///      source has moved since - the user typed in it, an undo restored it - that diff is no longer
  ///      true, and applying anyway would execute a change the user never saw the shape of. Refused with
  ///      nothing touched (#775: a card asserting something untrue at the moment of approval is the one
  ///      thing a card may never be).
  ///   2. CAPTURE BEFORE MUTATING - the fingerprint, and (for a preserves claim) the target's own output
  ///      surface, so the post-apply comparison has a baseline taken in the same state the user approved.
  ///   3. UNDO-FIRST. The undo record is written before the first setter, so there is exactly one Ctrl-Z; an
  ///      error before it must undo nothing, because the top of the stack is then the user's own last action.
  ///      The record is DWIGHT'S OWN ACTION (source + port list), not Grasshopper's generic per-object one,
  ///      which a Rhino 8 script component does not survive (U36, #929).
  ///   4. VERIFY: the PREDICTED DELTA (the after-fingerprint equals the before-document with only this
  ///      component's tokens swapped in) and, for a preserves claim, output equivalence against the
  ///      surface captured in step 2.
  ///   5. REVERT ON FAILURE - AND CHECK THE REVERT. This is the one place that check earns its keep: a
  ///      revert that only half-restored would leave the user's script in a state neither they nor Dwight
  ///      wrote, and reporting "reverted" over it would be the worst sentence in the product.
  ///
  /// THE PREDICTED DELTA WORKS HERE ONLY BECAUSE OF #779. Until this morning a script's source was absent
  /// from the behaviour fingerprint, so an edit to it was INVISIBLE to exactly this check - the prediction
  /// and the actual would have matched whatever the new source said, and the leg would have been vacuous.
  /// The digest is what makes "this component's source changed and nothing else did" a statement with
  /// content.
  /// </summary>
  internal static class GhEditScript
  {
    /// <summary>
    /// The target as it stands NOW - everything <see cref="EditScriptAsk"/> needs to validate against, read
    /// live and never remembered. The two facts beyond the obvious are ERRORED and LOCKED, because a
    /// "preserves" claim against a component that is not computing has no referent: an errored script still
    /// emits a null item on every output, so a broken original and a broken edit compare EQUAL.
    /// </summary>
    public static LiveScript ReadLive(GH_Document doc, AgentIdRegistry ids, int id)
    {
      try
      {
        if (doc == null || ids == null || !ids.TryGetGuid(id, out var guid)) return null;
        var obj = doc.FindObject(guid, false);
        if (obj == null) return null;

        var read = ScriptSourceReflect.TryRead(obj, out var source);
        var live = new LiveScript
        {
          Type = obj.GetType().FullName,
          Nick = NickOf(obj),
          Source = read ? SourceToken.Normalise(source) : null,
          SourceReadable = read,
          Errored = ScriptComponentShim.Errors(obj).Count > 0,
          Locked = IsLocked(obj),
        };

        if (obj is IGH_Component comp)
        {
          live.Inputs = comp.Params.Input.Select(PortName).ToList();
          live.InputAccess = comp.Params.Input.Where(p => p.Access != GH_ParamAccess.item)
            .GroupBy(PortName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => WordOf(g.First().Access), StringComparer.OrdinalIgnoreCase);
          // The script's OWN outputs, excluding the standard "out" print stream the component always
          // carries - the model may not append a port whose name collides with a real one, and "out" is
          // not one it can collide with.
          live.Outputs = ScriptComponentShim.ScriptOutputs(comp).Select(PortName).ToList();
        }
        return live;
      }
      catch { return null; }
    }

    /// <summary>
    /// Apply one validated edit and verify it. <paramref name="done"/> is invoked exactly once, on the UI
    /// thread, with the outcome - applied, or reverted with its reason.
    /// </summary>
    /// <param name="outcome">What the TRIAL concluded, already decided by the GH-free
    /// <see cref="ScriptEditVerdict"/>. It is passed in rather than recomputed so the decision the user
    /// reads afterwards is the one the card was drawn from - a second decision here could disagree with
    /// the first, and the user would have approved the other one.</param>
    /// <param name="reportLine">The sentence the user reads on success, and <paramref name="modelFact"/>
    /// the one the model is told - BOTH RENDERED AT THE ASK, from the same facts the card was drawn from,
    /// and carried here as strings.
    ///
    /// They are passed rather than re-rendered because the TRIAL'S FACTS DO NOT SURVIVE THE CLICK: the
    /// card's record carries strings, not live observations, so re-rendering here would have to invent the
    /// counts it no longer has. The first version of this took the facts and got null - which would have
    /// printed "verified the outputs are unchanged (0 outputs compared)", a sentence that is worse than
    /// vague because it is specific and false. Carrying the rendered sentences makes "the report says what
    /// the card said" structural instead of a coincidence maintained by care.</param>
    public static void Apply(GH_Document doc, Guid target, ScriptEditChange change,
      ScriptEditOutcome outcome, string reportLine, string modelFact, Action<ToolResult> done)
    {
      var finished = false;
      Action<ToolResult> finish = r => { if (finished) return; finished = true; try { done?.Invoke(r); } catch { } };

      if (doc == null || change == null)
      { finish(ToolResult.Fail(ToolErrors.NoDocument, "The document is no longer available, so nothing was changed.")); return; }

      // An outcome the trial did not clear never reaches an apply. Checked here as well as at the card,
      // because this method is the write and a write must not rest on its caller having checked (#208).
      if (!ScriptEditVerdict.MayOfferCard(outcome))
      {
        finish(ToolResult.Fail(ToolErrors.VerificationFailed,
          "Dwight did not apply that edit: " + (reportLine ?? "the check did not clear.")));
        return;
      }

      try
      {
        doc.ScheduleSolution(10, d =>
        {
          // Whether THIS change's undo record exists. An error before it must undo NOTHING - the top of
          // the undo stack is then the user's own last action (#683 6c).
          var recorded = false;
          try
          {
            var obj = d.FindObject(target, false) as IGH_Component;
            if (obj == null)
            {
              finish(ToolResult.Fail(ToolErrors.RetiredId,
                "The component I named is no longer on the canvas (it was deleted, or an undo removed it), " +
                "so nothing was changed. Ask me again and I will look at what is there now."));
              return;
            }

            // ---- 1. THE BASE, COMPARE-AND-SET ---------------------------------------------------------
            if (!ScriptSourceReflect.TryRead(obj, out var sourceNow))
            {
              finish(ToolResult.Fail(ToolErrors.VerificationFailed,
                "Dwight can no longer read that component's source, so it did not replace it - nothing was changed."));
              return;
            }
            if (change.BaseMoved(sourceNow))
            {
              finish(ToolResult.Fail(ToolErrors.VerificationFailed,
                "That component's source has changed since Dwight showed you the diff, so the diff you approved is " +
                "no longer what this edit would do. Nothing was changed. Ask again and Dwight will diff against " +
                "what is there now."));
              return;
            }

            // ---- 2. CAPTURE BEFORE MUTATING -----------------------------------------------------------
            var before = BehaviourFingerprint.ReadDoc(d);
            var outParams = ScriptComponentShim.ScriptOutputs(obj).Select(p => p.InstanceGuid).ToList();
            var beforeOutputs = change.Claim == ScriptEditClaim.Preserves
              ? SimplifyApply.CaptureOutputs(d, outParams)
              : null;

            // ---- 3. UNDO-FIRST, WITH DWIGHT'S OWN ACTION (U36, #929) ----------------------------------
            //
            // This recorded Grasshopper's generic per-object event until the gate pressed Ctrl-Z after a
            // verified apply and got "Undo failed: Target parameter does not exist in document" - the record
            // consumed, the new source still in place, the card's "One Ctrl-Z undoes it" false. A Rhino 8
            // script component does not survive the platform's parameter-level record, so the record is ours:
            // it holds the source read a moment ago (byte for byte) and the port counts, and writes them back
            // through the same shim calls the apply uses. One GH_UndoRecord per approval (mother doc 4.6).
            // Brief CP: the access of each input the edit changes, read live, because it is what undo owes back. A name the edit appends has no before, and undo removes that port.
            var accessBefore = (change.AccessChanges ?? new AccessChange[0]).ToDictionary(c => c.Name,
              c => WordOf(obj.Params.Input.First(p => string.Equals(PortName(p), c.Name, StringComparison.OrdinalIgnoreCase)).Access), StringComparer.OrdinalIgnoreCase);
            var restore = new ScriptEditRestore(sourceNow, change.NewSource,
              obj.Params.Input.Count, outParams.Count, change.AddInputs, change.AddOutputs, accessBefore, change.AccessToSet);
            Dwight.Session.ChangeLog.NoteChat(d, d.UndoUtil.RecordEvent("Dwight: edit " + change.Label, new ScriptEditUndoAction(target, restore)), "Dwight: edit " + change.Label, ConsentCard.EditScriptDone(change));   // Brief AP
            recorded = true;

            if (!ScriptComponentShim.SetSource(obj, change.NewSource, out var setError))
            { Revert(d, target, change, finish, "Dwight could not load the new source: " + setError); return; }

            if (!ScriptComponentShim.Append(obj, change.AddInputs, change.AddOutputs, out var appendError))
            { Revert(d, target, change, finish, appendError); return; }

            if (!SetAccess(obj, change.AccessToSet, out var accessError))
            { Revert(d, target, change, finish, accessError); return; }

            try { obj.ExpireSolution(false); } catch { }   // marked; the scheduled solution recomputes after this callback

            Verify(d, target, change, outcome, reportLine, modelFact, before, beforeOutputs, finish);
          }
          catch (Exception ex)
          {
            if (!recorded)
            {
              finish(ToolResult.Fail(ToolErrors.VerificationFailed,
                "Dwight hit a problem before changing anything, so nothing was changed (" + ex.Message + ")."));
              return;
            }
            Revert(d, target, change, finish, ex.Message);
          }
        });
      }
      catch (Exception ex)
      {
        finish(ToolResult.Fail(ToolErrors.VerificationFailed,
          "Dwight could not schedule the change, so nothing was changed (" + ex.Message + ")."));
      }
    }

    /// <summary>
    /// The two verification legs, after the solve the apply expired.
    ///
    /// The PREDICTED DELTA is the universal one: the after-fingerprint must equal the before-document with
    /// only this component's freshly-harvested tokens swapped in, so anything else that changed - a value,
    /// a wire, another component - fails it. The OUTPUT EQUIVALENCE leg runs only for a "preserves" claim,
    /// against the surface captured before the mutation, and it is a SECOND proof rather than a repeat of
    /// the trial's: the trial compared a COPY, and #643's lesson is that two objects are two identities. A
    /// type hint or an appended port could behave differently in place, and this is where that would show.
    /// </summary>
    private static void Verify(GH_Document doc, Guid target, ScriptEditChange change,
      ScriptEditOutcome outcome, string reportLine, string modelFact, FpDoc before,
      IReadOnlyList<OutputSample> beforeOutputs, Action<ToolResult> finish)
    {
      GH_Document.SolutionEndEventHandler handler = null;
      Eto.Forms.UITimer timeout = null;
      var fired = false;

      void Settle(Action body)
      {
        if (fired) return;
        fired = true;
        try { if (handler != null) doc.SolutionEnd -= handler; } catch { }
        try { timeout?.Stop(); } catch { }
        body();
      }

      handler = (s, e) => Settle(() =>
      {
        try
        {
          var obj = doc.FindObject(target, false);
          if (obj == null) { Revert(doc, target, change, finish, "the component disappeared while Dwight was checking it"); return; }

          var harvested = BehaviourFingerprint.ReadObject(obj);
          var predicted = SetValueCore.Predict(before, target, harvested);
          var actual = BehaviourFingerprintCore.Compute(BehaviourFingerprint.ReadDoc(doc));
          if (!string.Equals(actual, predicted, StringComparison.Ordinal))
          {
            Revert(doc, target, change, finish,
              "the change touched more than that one component's source, which an edit must never do");
            return;
          }

          if (change.Claim == ScriptEditClaim.Preserves)
          {
            var outParams = ScriptComponentShim.ScriptOutputs(obj as IGH_Component).Select(p => p.InstanceGuid).ToList();
            var afterOutputs = SimplifyApply.CaptureOutputs(doc, outParams.Take(beforeOutputs?.Count ?? 0).ToList());
            if (!OutputEquivalence.Equivalent(beforeOutputs, afterOutputs))
            {
              Revert(doc, target, change, finish,
                "the component stopped producing what it produced before, and the edit said it would not");
              return;
            }
          }

          finish(ToolResult.Success(new System.Text.Json.Nodes.JsonObject
          {
            ["applied"] = true,
            ["component"] = change.Label,
            ["claim"] = change.Claim.ToString().ToLowerInvariant(),
            ["outcome"] = outcome.ToString(),
            ["report"] = reportLine ?? "",
            ["note"] = modelFact ?? "",
            ["addedInputs"] = change.AddInputs.Count,
            ["addedOutputs"] = change.AddOutputs.Count,
          }));
        }
        catch (Exception ex) { Revert(doc, target, change, finish, ex.Message); }
      });

      try
      {
        doc.SolutionEnd += handler;
        timeout = new Eto.Forms.UITimer { Interval = 30.0 };
        timeout.Elapsed += (s, e) => Settle(() =>
          Revert(doc, target, change, finish, "the definition did not finish solving, so the change could not be checked"));
        timeout.Start();
        doc.ScheduleSolution(10);
      }
      catch (Exception ex)
      {
        Settle(() => Revert(doc, target, change, finish, ex.Message));
      }
    }

    /// <summary>
    /// REVERT, AND THEN CHECK THE REVERT - which matters here more than anywhere else in the product.
    ///
    /// Every other revert restores components Dwight placed; this one restores TEXT THE USER WROTE. A
    /// revert that only half-worked would leave their script in a state neither they nor Dwight authored,
    /// and "it was reverted - your script is as it was" over that state would be the worst sentence
    /// Dwight can say. So the source is read back and compared against the base the card diffed from, and
    /// when it does not match the report says exactly that instead: the third state (#249/#375) applied to
    /// a revert rather than to a check.
    /// </summary>
    private static void Revert(GH_Document doc, Guid target, ScriptEditChange change,
      Action<ToolResult> finish, string why)
    {
      var restored = false;
      try
      {
        Dwight.Session.ChangeLog.Reverting(doc);   // Brief AP: never offered for redo
        doc.Undo();
        try { Instances.RedrawCanvas(); } catch { }

        var obj = doc.FindObject(target, false);
        restored = obj != null
                   && ScriptSourceReflect.TryRead(obj, out var now)
                   && !change.BaseMoved(now);
      }
      catch { restored = false; }

      if (restored)
      {
        finish(ToolResult.Fail(ToolErrors.VerificationFailed,
          "Dwight applied that edit, checked it, and put it back because " + why +
          ". The component holds exactly the source it held before."));
        return;
      }

      finish(ToolResult.Fail(ToolErrors.VerificationFailed,
        "Dwight applied that edit and tried to put it back because " + why +
        " - AND COULD NOT CONFIRM THE ORIGINAL SOURCE IS RESTORED. Check the component and press Ctrl-Z if " +
        "it is not as you left it. Dwight will not touch it again until you ask."));
    }

    /// <summary>
    /// THE ONE CTRL-Z (U36). Undo writes the BEFORE source back and removes the ports the edit appended; redo
    /// writes the approved source back and appends them again - through the shim's own calls, never through
    /// Grasshopper's parameter-level record, which this component type does not survive (#929).
    ///
    /// A component that is gone THROWS rather than returning quietly: Grasshopper then reports the undo as
    /// failed, which is true, instead of leaving the user believing a restore happened. The data it writes is
    /// <see cref="ScriptEditRestore"/>'s, decided and tested GH-free; this class only carries it to the canvas.
    /// </summary>
    private sealed class ScriptEditUndoAction : Grasshopper.Kernel.Undo.GH_UndoAction
    {
      private readonly Guid _target;
      private readonly ScriptEditRestore _restore;

      public ScriptEditUndoAction(Guid target, ScriptEditRestore restore)
      {
        _target = target;
        _restore = restore;
      }

      public override bool ExpiresSolution => true;

      protected override void Internal_Undo(GH_Document doc)
      {
        var comp = Find(doc);
        if (!ScriptComponentShim.SetSource(comp, _restore.BeforeSource, out var error))
          throw new InvalidOperationException(error);
        var inputs = _restore.InputsToRemove(comp.Params.Input.Count);
        var outputs = _restore.OutputsToRemove(ScriptComponentShim.ScriptOutputs(comp).Count);
        if ((inputs > 0 || outputs > 0) && !ScriptComponentShim.RemoveTrailingPorts(comp, inputs, outputs, out error))
          throw new InvalidOperationException(error);
        if (!SetAccess(comp, _restore.BeforeAccess, out error))   // Brief CP: U94's shape, the old access back beside the source and the ports
          throw new InvalidOperationException(error);
        try { comp.ExpireSolution(false); } catch { }
      }

      protected override void Internal_Redo(GH_Document doc)
      {
        var comp = Find(doc);
        if (!ScriptComponentShim.SetSource(comp, _restore.AfterSource, out var error))
          throw new InvalidOperationException(error);
        if (!ScriptComponentShim.Append(comp, _restore.AppendedInputs, _restore.AppendedOutputs, out error))
          throw new InvalidOperationException(error);
        if (!SetAccess(comp, _restore.AfterAccess, out error))
          throw new InvalidOperationException(error);
        try { comp.ExpireSolution(false); } catch { }
      }

      private IGH_Component Find(GH_Document doc)
      {
        var comp = doc?.FindObject(_target, false) as IGH_Component;
        if (comp == null)
          throw new InvalidOperationException(
            "Dwight could not put the script back: the component it edited is no longer on the canvas.");
        return comp;
      }
    }

    /// <summary>Brief CP: the access words, set and read back, through the shim's one call. False at the first input that did not take it.</summary>
    private static bool SetAccess(IGH_Component comp, IReadOnlyDictionary<string, string> access, out string error)
    {
      error = null;
      foreach (var kv in access ?? new Dictionary<string, string>())
        if (!ScriptComponentShim.SetInputAccess(comp, kv.Key, PortAccess.ToScript(kv.Value), out error)) return false;
      return true;
    }

    private static string WordOf(GH_ParamAccess access) =>
      access == GH_ParamAccess.list ? PortAccess.List : access == GH_ParamAccess.tree ? PortAccess.Tree : PortAccess.Item;

    private static string PortName(IGH_Param p)
    {
      try
      {
        if (!string.IsNullOrWhiteSpace(p?.NickName)) return p.NickName;
        if (!string.IsNullOrWhiteSpace(p?.Name)) return p.Name;
      }
      catch { }
      return "";
    }

    private static bool IsLocked(IGH_DocumentObject obj)
    { try { return obj is IGH_ActiveObject a && a.Locked; } catch { return false; } }

    private static string NickOf(IGH_DocumentObject obj)
    {
      try
      {
        var nick = obj.NickName;
        return string.IsNullOrWhiteSpace(nick) || string.Equals(nick, obj.Name, StringComparison.Ordinal) ? null : nick;
      }
      catch { return null; }
    }
  }
}
