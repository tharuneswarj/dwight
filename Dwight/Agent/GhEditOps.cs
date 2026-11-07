using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;
using Grasshopper.Kernel.Undo;
using Grasshopper.Kernel.Undo.Actions;
using Dwight.Data;
using Dwight.Simplify;

namespace Dwight.Agent
{
  /// <summary>
  /// THE APPLIER FOR CONSENTED, BATCHED EDITS (Phase-9 U6 + U21, ruled at #760): add wires, remove wires, delete existing
  /// components - the rows the user left ticked, in ONE undo record, with a verdict computed on exactly that set.
  ///
  /// THE DECISIONS ARE GH-FREE AND TESTED (ADR-091): which rows are valid is <see cref="EditOpsAsk"/>, what the card says
  /// is <see cref="ConsentCard"/>, and whether the result is the one the ticked rows should have produced is
  /// <see cref="EditPrediction"/>. This file only carries them to the canvas, and reads the canvas into the shape they take.
  ///
  /// THE ORDER, which is the design (the same as Simplify's layer 1, the verdict this is modelled on - ADR-090):
  ///   1. EVERY TARGET STILL EXISTS. The card was drawn from the canvas as it was; the plan the click hands this has just
  ///      been re-validated against the canvas as it is NOW (<see cref="RunApproved"/>), and this re-checks inside the
  ///      scheduled callback, where nothing can move between the check and the write. Missing -> nothing is touched.
  ///   2. CAPTURE BEFORE MUTATING - the behaviour fingerprint of the document, in the callback's first act, so the
  ///      prediction is taken from the state the user approved.
  ///   3. SNAPSHOT EVERY INPUT THE EDIT TOUCHES (a <c>GH_WireAction</c> before the first change to it). That includes the
  ///      inputs a delete starves: the removal's own undo action restores the objects, and nothing guaranteed it restored
  ///      what they fed, so the revert could put a component back and leave its consumers disconnected (#623's half-revert,
  ///      one wire further out - the snapshot is the same cure GhBuildRegion applies to a rebuild).
  ///   4. MUTATE, in card order, then PREDICT: the after-document must equal the before-document plus ONLY the declared
  ///      change (<see cref="EditPrediction.Matches"/>). A wire that landed on the wrong port, a component that went with
  ///      the one deleted, a value that moved: any difference reverts everything, and the report NAMES what differed.
  ///   5. RecordEvent is the LAST statement in the try: before it a throw rolls back by hand with no event on the stack,
  ///      so the top of the undo stack is still the user's own last action; after it the whole change is one Ctrl-Z.
  ///
  /// "UNDO-FIRST" HERE MEANS SNAPSHOT-FIRST. GhEditScript records Dwight's own action before its first setter because a
  /// script component does not survive Grasshopper's parameter-level record (U36, #929). Wires and removals are exactly
  /// what <c>GH_WireAction</c> and <c>GH_RemoveObjectAction</c> exist for, and GhBuildRegion and SimplifyApply both use
  /// them the same way: snapshots first, one RecordEvent last.
  /// </summary>
  internal static class GhEditOps
  {
    public const string UndoName = "Dwight: edit wiring";

    // ==== reading the canvas into the validator's shape =============================================================

    /// <summary>
    /// The live canvas as <see cref="EditOpsAsk"/> reads it: every component and parameter with its ports, the sources
    /// wired into each input, and the group it sits in. ON THE UI THREAD ONLY - it walks the document and constructs
    /// nothing. Ids come from the thread's registry, synced first, so a name in a sentence and an id the model read are the
    /// same id.
    /// </summary>
    public static EditCanvas ReadCanvas(GH_Document doc, AgentIdRegistry ids)
    {
      var canvas = new EditCanvas { State = ids.State };
      if (doc == null) return canvas;

      try { ids.Sync(doc.Objects.Where(o => o != null).Select(o => o.InstanceGuid)); } catch { }
      var label = GhBuildRegion.LabelOfId(doc, ids);

      var region = new Dictionary<Guid, string>();
      try
      {
        foreach (var g in doc.Objects.OfType<GH_Group>())
        {
          var name = (g.NickName ?? "").Trim();
          if (name.Length == 0) continue;
          foreach (var member in g.ObjectIDs)
            if (!region.ContainsKey(member)) region[member] = name;
        }
      }
      catch { }

      foreach (var obj in doc.Objects)
      {
        try
        {
          if (obj == null || obj is GH_Group || obj is GH_Scribble) continue;
          if (!ids.TryGetId(obj.InstanceGuid, out var id)) continue;

          EditNode node = null;
          if (obj is IGH_Component comp)
          {
            node = new EditNode
            {
              Id = id, Guid = obj.InstanceGuid, IsParam = false,
              Inputs = comp.Params.Input.Select(PortOf).ToList(),
              Outputs = comp.Params.Output.Select(PortOf).ToList(),
            };
          }
          else if (obj is IGH_Param param)
          {
            var port = PortOf(param);
            var accepts = AcceptsInput(param);
            node = new EditNode
            {
              Id = id, Guid = obj.InstanceGuid, IsParam = true, AcceptsInput = accepts,
              Inputs = accepts ? new List<EditPort> { port } : new List<EditPort>(),
              Outputs = new List<EditPort> { port },   // the SAME port object: one implicit port, both ends
            };
          }
          if (node == null) continue;

          node.Label = label(id) ?? "component";
          region.TryGetValue(obj.InstanceGuid, out var r);
          node.Region = r;
          canvas.Nodes[id] = node;
        }
        catch { /* fail safe (ADR-029): an object we cannot read is simply not editable */ }
      }
      return canvas;
    }

    private static EditPort PortOf(IGH_Param p)
    {
      var port = new EditPort { Name = SafeName(p), Nick = SafeNick(p), Guid = p.InstanceGuid };
      try { foreach (var s in p.Sources) if (s != null) port.Sources.Add(s.InstanceGuid); } catch { }
      return port;
    }

    private static string SafeName(IGH_Param p) { try { return p.Name; } catch { return null; } }
    private static string SafeNick(IGH_Param p) { try { return p.NickName; } catch { return null; } }

    /// <summary>Does a wire land on this parameter at all? A slider, a toggle, a value list have no input grip - the
    /// canvas will not draw a wire into one, and neither will Dwight.</summary>
    private static bool AcceptsInput(IGH_Param p)
    {
      try { return p.Attributes?.HasInputGrip ?? true; } catch { return true; }
    }

    // ==== the ask ========================================================================================================

    /// <summary>
    /// Validate the model's rows against the live canvas and build the plan the card is drawn from. UI thread. On success
    /// <paramref name="record"/> is the plugin's resolved form of the ops (canvas port spellings), which the caller writes
    /// back onto the seed so the click re-validates what the card showed.
    /// </summary>
    public static bool Ask(GH_Document doc, AgentIdRegistry ids, JsonArray ops, out EditPlan plan, out ToolResult error)
    {
      plan = null;
      if (doc == null)
      {
        error = ToolResult.Fail(ToolErrors.NoDocument, "No Grasshopper document is open.");
        return false;
      }
      return EditOpsAsk.Validate(ops, ReadCanvas(doc, ids), out plan, out error);
    }

    // ==== the click ======================================================================================================

    /// <summary>
    /// The user approved: re-validate the TICKED rows against the canvas as it is now, and apply exactly those. UI thread.
    /// <paramref name="done"/> is invoked exactly once with (applied, the sentence the user and the model both read).
    ///
    /// Nothing falls back (#208, #338): an index the card never had, a row that no longer resolves, a missing tick set - each
    /// REFUSES with the reason and nothing is touched. Zero ticked is a report, not a quiet no-op.
    /// </summary>
    public static void RunApproved(GH_Document doc, AgentIdRegistry ids, JsonArray record, IReadOnlyList<int> ticked,
      Action<bool, string> done)
    {
      if (ticked == null)
      {
        done(false, "Dwight did not receive which changes were ticked, so it applied none of them. Nothing was changed.");
        return;
      }
      if (!EditOpsAsk.Subset(record, ticked, out var ops, out var cardRows, out var refusal))
      {
        done(false, refusal);
        return;
      }
      if (ops.Count == 0)
      {
        done(false, "Nothing was ticked, so nothing was applied. The canvas is unchanged.");
        return;
      }

      if (!EditOpsAsk.Validate(ops, ReadCanvas(doc, ids), cardRows, out var plan, out var error))
      {
        done(false, "Nothing was applied. " + error.Message);
        return;
      }
      Apply(doc, plan, record?.Count ?? plan.Steps.Count, done);
    }

    // ==== the apply ======================================================================================================

    public static void Apply(GH_Document doc, EditPlan plan, int cardTotal, Action<bool, string> done)
    {
      var finished = false;
      void Finish(bool ok, string text) { if (finished) return; finished = true; try { done?.Invoke(ok, text); } catch { } }

      if (doc == null || plan == null || plan.Steps.Count == 0)
      { Finish(false, "The document is no longer available, so nothing was changed."); return; }

      try
      {
        doc.ScheduleSolution(10, d =>
        {
          var actions = new List<IGH_UndoAction>();
          // U48: ONE INDEX OVER EVERY PORT, built here, inside the callback where nothing can move.
          var ports = PortIndexOf(d);
          try
          {
            // ---- 1. every target, resolved to exactly one live object, before anything is touched -----------
            foreach (var step in plan.Steps)
            {
              foreach (var n in new[] { step.From, step.To, step.Target })
                if (n != null && d.FindObject(n.Guid, false) == null)
                {
                  Finish(false, "Nothing was changed: " + EditOpsAsk.Ref(n) + " is no longer on the canvas (row " + step.Row +
                                "). Ask again and Dwight will look at what is there now.");
                  return;
                }
            }
            // A port is found by its OWN guid in the index above - not through FindObject, which answers only for
            // top-level objects and so never found a component's input (U48). The sentence names what is true.
            var unresolved = EditPortCheck.FirstUnresolved(plan, g => ports.Find(g) != null);
            if (unresolved != null)
            {
              Finish(false, "Nothing was changed: " + unresolved);
              return;
            }

            // ---- 2. capture BEFORE - the callback's first act -------------------------------------------------
            var before = BehaviourFingerprint.ReadDoc(d);
            // Brief CL: the count and who holds whom, before the first delete can touch either. The fingerprint sees neither.
            var objectsBefore = d.Objects.Count;
            var groupsBefore = d.Objects.OfType<GH_Group>().Select(g => new EditGroupFacts
            {
              Guid = g.InstanceGuid, Name = g.NickName, Members = (g.ObjectIDs ?? new List<Guid>()).ToList(),
            }).ToList();

            // ---- 3. snapshot every input the edit touches, before the first change to any of them ---------------
            foreach (var g in plan.TouchedInputs)
            {
              var ip = ports.Find(g);
              if (ip != null) actions.Add(new GH_WireAction(ip));
            }

            // ---- 4. mutate, in card order --------------------------------------------------------------------
            var deleted = new List<Guid>();
            foreach (var step in plan.Steps)
            {
              switch (step.Kind)
              {
                case EditOpKind.Wire:
                  ports.Find(step.ToPort.Guid).AddSource(ports.Find(step.FromPort.Guid));
                  break;
                case EditOpKind.Unwire:
                  ports.Find(step.ToPort.Guid).RemoveSource(ports.Find(step.FromPort.Guid));
                  break;
                default:
                  var obj = d.FindObject(step.Target.Guid, false);
                  actions.Add(new GH_RemoveObjectAction(obj));
                  d.RemoveObject(obj, false);
                  deleted.Add(step.Target.Guid);
                  break;
              }
            }

            // Brief CL: a group Dwight made goes with the last thing in it, in this same record, so one Ctrl-Z brings back both. Last in the list, so an undo restores it first.
            var groups = EditEmptyGroups.Decide(groupsBefore, deleted, g => d.FindObject(g, false) != null);
            foreach (var g in groups.Remove)
            {
              var group = d.FindObject(g, false);
              actions.Add(new GH_RemoveObjectAction(group));
              d.RemoveObject(group, false);
            }

            // ---- the verdict: PREDICTED DELTA (layer 1), on the applied set alone ------------------------------
            var actual = BehaviourFingerprint.ReadDoc(d);
            if (!EditPrediction.Matches(before, plan, actual, out var differing))
            {
              SimplifyApply.Rollback(d, actions);
              Finish(false, EditPrediction.RevertText(differing, plan.Labels, g => Name(d, g)));
              return;
            }

            // Brief CL: what the fingerprint cannot see, by count. Anything but the deletes and their groups is a change nobody approved.
            var objectsAfter = d.Objects.Count;
            var expectedObjects = EditPrediction.ExpectedObjects(objectsBefore, deleted.Count, groups.Remove.Count);
            if (objectsAfter != expectedObjects)
            {
              SimplifyApply.Rollback(d, actions);
              Finish(false, EditPrediction.CountRevertText(expectedObjects, objectsAfter));
              return;
            }

            // Marked for recompute; the scheduled solution runs after this callback. Every owner of an input whose
            // sources changed, deleted-source consumers included (they are in TouchedInputs).
            foreach (var g in plan.TouchedInputs)
            {
              try { ports.Find(g)?.ExpireSolution(false); } catch { }
            }

            // RecordEvent is the LAST statement in the try: one Ctrl-Z, or none.
            Dwight.Session.ChangeLog.NoteChat(d, d.UndoUtil.RecordEvent(UndoName, actions), UndoName, ConsentCard.EditDone(plan));   // Brief AP: the record's id; Brief BQ: what the ticked rows did
            try { Instances.RedrawCanvas(); } catch { }

            Finish(true, EditOpsAsk.AppliedReport(plan, cardTotal, objectsBefore, objectsAfter, groups));
          }
          catch (Exception ex)
          {
            // PRE-COMMIT failure only: manual rollback, no event on the stack.
            try { SimplifyApply.Rollback(d, actions); } catch { }
            Finish(false, "Dwight hit a problem applying the changes and put back any it had made (" + ex.Message +
                          "). Nothing on the canvas is changed.");
          }
        });
      }
      catch (Exception ex)
      {
        Finish(false, "Dwight could not schedule the changes, so nothing was changed (" + ex.Message + ").");
      }
    }

    /// <summary>Every parameter on the canvas that a wire can touch, by its own guid: a top-level param (a slider, a panel)
    /// and each input and output param of each component. U48's cure - see <see cref="EditPortIndex{T}"/>.</summary>
    private static EditPortIndex<IGH_Param> PortIndexOf(GH_Document d)
    {
      var index = new EditPortIndex<IGH_Param>();
      try
      {
        foreach (var obj in d.Objects)
        {
          try
          {
            if (obj is IGH_Component comp)
            {
              foreach (var p in comp.Params.Input) index.Add(p.InstanceGuid, p);
              foreach (var p in comp.Params.Output) index.Add(p.InstanceGuid, p);
            }
            else if (obj is IGH_Param param) index.Add(param.InstanceGuid, param);
          }
          catch { /* an object we cannot read is simply not in the index */ }
        }
      }
      catch { }
      return index;
    }

    private static string Name(GH_Document d, Guid guid)
    {
      try
      {
        var o = d.FindObject(guid, true);
        if (o == null) return "a component";
        return !string.IsNullOrWhiteSpace(o.NickName) ? o.NickName : (o.Name ?? "a component");
      }
      catch { return "a component"; }
    }
  }
}
