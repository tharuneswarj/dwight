using System;
using System.Text.Json.Nodes;
using Grasshopper;
using Grasshopper.GUI.Base;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;
using Dwight.Data;

namespace Dwight.Agent
{
  /// <summary>
  /// set_value's APPLIER (Phase-9 U4) - the one write path both the panel's consent click and the catalogue
  /// tool (the MCP face) reach. GH-only; every decision it needs is made GH-free before it runs
  /// (<see cref="SetValueAsk"/>, <see cref="SetValueCore"/>).
  ///
  /// WHAT THE USER APPROVED IS WHAT EXECUTES - NOTHING MAY SIT BETWEEN AN APPROVAL AND ITS EXECUTION THAT
  /// COULD DIFFER FROM IT (the Q1 ruling). The card carried a validated (target, state); this applies exactly
  /// that, on the UI thread, in ONE undo record, in stream A's measured order (Type, DecimalPlaces, Minimum,
  /// Maximum, Value), and reads it back.
  ///
  /// THE VERDICT, two legs kept separate: COMPLETE - the component reads back exactly the declared state
  /// (<see cref="GhBuildState.Matches"/>, equality never tolerance, #383); PRESERVED - the behaviour
  /// fingerprint after equals the one predicted from the before-document with only this object's harvested
  /// tokens swapped in (<see cref="SetValueCore.Predict"/>, Simplify's G.1 pattern), so nothing else changed.
  /// Either leg failing REVERTS FIRST (one undo) and then reports. The fingerprint's slider token carries
  /// the value only, so a domain-only change elsewhere is invisible to that leg - stated in the verdict -
  /// and acceptable because this method reaches ONE guid and has no loop.
  ///
  /// NEVER A SYNCHRONOUS SOLVE (#117): the slider is expired without recomputing; the solution that
  /// ScheduleSolution runs after this callback carries the change downstream.
  /// </summary>
  internal static class GhSetValue
  {
    /// <summary>The target as it is NOW: type, nickname, and what it holds in the shared vocabulary.</summary>
    public static LiveTarget ReadLive(GH_Document doc, AgentIdRegistry ids, int id)
    {
      try
      {
        if (doc == null || ids == null || !ids.TryGetGuid(id, out var guid)) return null;
        var obj = doc.FindObject(guid, false);
        if (obj == null) return null;

        // #680: the one held-state reader, shared with the as-left record and the canvas stamp.
        return new LiveTarget { Type = obj.Name, Nick = NickOf(obj), Current = GhBuildState.Read(obj) };
      }
      catch { return null; }
    }

    /// <summary>The catalogue tool: {id, state} through the same validation the card uses, then the apply.</summary>
    public static void Run(GH_Document doc, AgentIdRegistry ids, JsonObject args, Action<ToolResult> done)
    {
      if (!SetValueCore.TryParseArgs(args, out var seed, out var err)) { done?.Invoke(err); return; }
      if (!SetValueAsk.Validate(seed, id => ids.State(id), id => ReadLive(doc, ids, id), out var change, out var declared, out err))
      { done?.Invoke(err); return; }
      if (!ids.TryGetGuid(seed.Target.Value, out var guid))
      { done?.Invoke(ToolResult.Fail(ToolErrors.RetiredId, "Component " + seed.Target + " is not on the canvas.")); return; }
      Apply(doc, guid, declared, change, done);
    }

    /// <summary>Apply one validated change to one existing component and verify it. <paramref name="done"/>
    /// is invoked exactly once, on the UI thread, with the verdict - applied, or reverted with its reason.</summary>
    public static void Apply(GH_Document doc, Guid target, BuildState state, SetValueChange change, Action<ToolResult> done)
    {
      var finished = false;
      Action<ToolResult> finish = r => { if (finished) return; finished = true; try { done?.Invoke(r); } catch { } };

      if (doc == null) { finish(SetValueCore.NoDocument()); return; }

      try
      {
        doc.ScheduleSolution(10, d =>
        {
          // #683 (6c): whether THIS change's undo record exists. An error before it must not undo anything - the top of
          // the undo stack is then the user's own last action.
          var recorded = false;
          try
          {
            var obj = d.FindObject(target, false);
            if (obj == null)
            {
              finish(SetValueCore.GoneAtApply(change));
              return;
            }

            var before = BehaviourFingerprint.ReadDoc(d);

            // ONE undo record for the whole change, taken before the first setter.
            Dwight.Session.ChangeLog.NoteChat(d, obj.RecordUndoEvent("Dwight: set " + change.Label), "Dwight: set " + change.Label, ConsentCard.SetValueDone(change));   // Brief AP: RecordUndoEvent returns the id too
            recorded = true;
            GhBuildState.Apply(obj, state);
            try { obj.ExpireSolution(false); } catch { }   // mark it; the scheduled solution recomputes after this callback

            var complete = GhBuildState.Matches(obj, state, out var mismatch);
            var harvested = BehaviourFingerprint.ReadObject(obj);
            var predicted = SetValueCore.Predict(before, target, harvested);
            var after = BehaviourFingerprintCore.Compute(BehaviourFingerprint.ReadDoc(d));
            var preserved = string.Equals(after, predicted, StringComparison.Ordinal);

            if (!preserved || !complete)
            {
              // REVERT FIRST, then report. An unverified change never stands.
              UndoOnce(d);
              finish(SetValueCore.Reverted(change, preserved, complete, mismatch));
              return;
            }

            finish(SetValueCore.Applied(change));
          }
          catch (Exception ex)
          {
            if (!recorded) { finish(SetValueCore.NotAppliedAfterError(ex.Message)); return; }
            try { UndoOnce(d); } catch { }
            finish(SetValueCore.RevertedAfterError(ex.Message));
          }
        });
      }
      catch (Exception ex)
      {
        finish(SetValueCore.CouldNotSchedule(ex.Message));
      }
    }

    private static void UndoOnce(GH_Document doc)
    {
      Dwight.Session.ChangeLog.Reverting(doc);   // Brief AP: never offered for redo
      try { doc.Undo(); } catch { }
      try { Instances.RedrawCanvas(); } catch { }
    }

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
