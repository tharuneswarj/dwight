using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using GhJSON.Core;
using GhJSON.Grasshopper;
using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Parameters;
using Grasshopper.Kernel.Special;
using Grasshopper.Kernel.Undo;
using Grasshopper.Kernel.Undo.Actions;
using Dwight.Layout;

namespace Dwight.Data
{
  /// <summary>
  /// The crash-safe paths that write to the canvas. Every method honours the guardrails:
  /// UI-thread marshaling via <c>ScheduleSolution</c> (ADR-010), exactly one
  /// <c>GH_UndoRecord</c> per user request (ADR-019), and never crashing Rhino (ADR-029).
  /// Callers must already be on the UI thread.
  /// </summary>
  /// <summary>How a Reorganise apply ended (WP-1.3), reported from INSIDE the scheduled solution so the panel
  /// renders the real result instead of an unconditional "verified".</summary>
  /// <summary>RR20 #85: Draft is a to-script-specific outcome - the script BUILT and RAN but its outputs did NOT
  /// match, so it is offered as a clearly-labelled DRAFT TO INSPECT (never presented as done, never a Replace),
  /// originals untouched. The verification stack earns the right to ship a mediocre generation rate precisely
  /// because a failure is "couldn't confirm", never a silent "wrong".</summary>
  internal enum ApplyStatus { Success, Stopped, Failed, Draft }

  /// <summary>The outcome of an apply, surfaced to the panel.</summary>
  internal sealed class ApplyOutcome
  {
    public ApplyStatus Status;
    public string Title;     // panel state title for Stopped / Failed
    public string Summary;   // success summary, or the stop/fail explanation
    /// <summary>RR17 #37: WHY it failed, structured for a retry rather than for display - the raw material the
    /// Phase-8 bounded refine loop feeds back to the model (its own compiler error, its own per-output diff).
    /// Null on success and wherever there is nothing a model could act on.</summary>
    public RefineFeedback Refine;
    public static ApplyOutcome Ok(string s) => new ApplyOutcome { Status = ApplyStatus.Success, Summary = s };
    public static ApplyOutcome Stop(string t, string s) => new ApplyOutcome { Status = ApplyStatus.Stopped, Title = t, Summary = s };
    public static ApplyOutcome Fail(string t, string s) => new ApplyOutcome { Status = ApplyStatus.Failed, Title = t, Summary = s };
    public static ApplyOutcome AsDraft(string t, string s) => new ApplyOutcome { Status = ApplyStatus.Draft, Title = t, Summary = s };
    /// <summary>Attach the machine-usable failure detail to an outcome (fluent, so the failure paths read the same).</summary>
    public ApplyOutcome With(RefineFeedback refine) { Refine = refine; return this; }
  }

  internal static class CanvasWriter
  {
    /// <summary>Undo the collected actions (auto-revert) - and if the rollback ITSELF throws partway, record the
    /// still-applied prefix as one undo event so a single Ctrl-Z finishes reverting (WP-1.1). Never throws.
    /// Internal since Phase-8 WP-2 Stage D: <c>build_region</c> reverts on the same discipline rather than
    /// growing a second rollback with its own failure modes (#193b).</summary>
    internal static void RollbackOrRecord(GH_Document d, List<IGH_UndoAction> actions, string recordName)
    {
      var k = actions.Count - 1;
      try { for (; k >= 0; k--) actions[k].Undo(d); }
      catch { try { if (k >= 0) Dwight.Session.ChangeLog.NoteRevertRemainder(d, d.UndoUtil.RecordEvent(recordName, actions.Take(k + 1).ToList()), recordName); } catch { } }
      try { Instances.RedrawCanvas(); } catch { }
    }

    /// <summary>
    /// Task 1.3 round-trip: place a working copy of <paramref name="json"/> offset to the
    /// right of the originals. Additive; one undo record. Strips internalized data first so
    /// <c>Put</c> cannot crash (ADR-029).
    /// </summary>
    public static void PlaceCopyOffset(GH_Document doc, string json, Action<string> log)
    {
      var cleaned = GhJsonGuards.StripInternalizedData(json, out var stripped);

      doc.ScheduleSolution(10, d =>
      {
        try
        {
          var before = new HashSet<Guid>(d.Objects.Select(o => o.InstanceGuid));

          GhJsonGrasshopper.Put(GhJson.FromJson(cleaned));

          var placed = d.Objects.Where(o => !before.Contains(o.InstanceGuid)).ToList();
          if (placed.Count == 0)
          {
            log("Dwight: nothing was placed (the library could not reconstruct the selection).");
            return;
          }

          OffsetClear(placed);
          Dwight.Session.ChangeLog.NoteVerb(d, d.UndoUtil.RecordAddObjectEvent("Dwight: place GhJSON copy", placed), "Dwight: place GhJSON copy", "Dev");

          if (stripped.Any)
            log($"Dwight: {stripped.ParameterCount} internalized value(s) could not be reconstructed " +
                "(GhJSON v1.0 limitation) and were left empty in the copy: " + string.Join(", ", stripped.Notes));
          log($"Dwight: placed a working copy of {placed.Count} object(s) (Ctrl-Z to remove).");
        }
        catch (Exception ex)
        {
          log($"Dwight: failed to place the GhJSON copy - {ex.Message}");
        }
      });
    }

    /// <summary>
    /// Phase 6 WP-A: apply a deterministic engine layout to the live canvas as MOVES ONLY - no groups, no
    /// labels, no wire changes - inside one <c>GH_UndoRecord</c> (ADR-019), so a single Ctrl-Z reverts it.
    /// Behaviour-neutral by construction: only pivots change. The whole laid-out set is anchored so its
    /// bounding-box top-left stays where it was (the disorientation guard, ADR-062) rather than jumping to
    /// the canvas origin. The engine emits target CENTRES; we move each object by the delta from its live
    /// centre, so the pivot-to-bounds relationship is preserved exactly. Caller must be on the UI thread;
    /// every step is crash-safe (ADR-029). This is the WP-A runtime gate's apply path - no LLM, no key.
    /// </summary>
    public static void ApplyLayout(GH_Document doc, DwightGraph graph, LayoutResult result, Action<string> log)
    {
      doc.ScheduleSolution(10, d =>
      {
        try
        {
          // Resolve each laid-out node to its live object + current centre, and find the anchor shift that
          // aligns the NEW bounding box's top-left with the ORIGINAL's (keep the cluster where it was).
          var items = new List<(IGH_DocumentObject obj, PointF curCenter, PointD target, float w, float h)>();
          double origMinX = double.MaxValue, origMinY = double.MaxValue;
          double newMinX = double.MaxValue, newMinY = double.MaxValue;

          foreach (var kv in result.Centers)
          {
            var node = graph.NodeById(kv.Key);
            if (node == null) continue;
            var obj = d.FindObject(node.Guid, true);
            if (obj?.Attributes == null) continue;          // not on canvas / id changed - skip safely
            var b = obj.Attributes.Bounds;
            var curCenter = new PointF(b.X + b.Width / 2f, b.Y + b.Height / 2f);
            items.Add((obj, curCenter, kv.Value, b.Width, b.Height));
            origMinX = Math.Min(origMinX, b.X);
            origMinY = Math.Min(origMinY, b.Y);
            newMinX = Math.Min(newMinX, kv.Value.X - b.Width / 2.0);
            newMinY = Math.Min(newMinY, kv.Value.Y - b.Height / 2.0);
          }

          if (items.Count == 0) { log("Dwight: layout produced no moves (no laid-out object found on the canvas)."); return; }

          var shiftX = origMinX - newMinX;
          var shiftY = origMinY - newMinY;

          var actions = new List<IGH_UndoAction>();
          var moved = 0;
          foreach (var it in items)
          {
            var targetX = (float)(it.target.X + shiftX);
            var targetY = (float)(it.target.Y + shiftY);
            var dx = targetX - it.curCenter.X;
            var dy = targetY - it.curCenter.Y;
            // Re-run idempotence guard: with the engine keyed off the stable guid, a re-layout resolves every
            // component to its current position; the 0.5px tolerance absorbs sub-pixel bounds-recompute noise
            // so a re-run is a true no-op (it never re-records moves and never drifts).
            if (Math.Abs(dx) < 0.5f && Math.Abs(dy) < 0.5f) continue;
            actions.Add(new GH_PivotAction(it.obj));                       // capture old pivot BEFORE moving
            var p = it.obj.Attributes.Pivot;
            it.obj.Attributes.Pivot = new PointF(p.X + dx, p.Y + dy);
            it.obj.Attributes.ExpireLayout();
            moved++;
          }

          if (actions.Count == 0) { log("Dwight: layout - everything is already in place, nothing moved."); return; }

          Dwight.Session.ChangeLog.NoteVerb(d, d.UndoUtil.RecordEvent("Dwight: Lay out (engine only)", actions), "Dwight: Lay out (engine only)", "Dev");
          Instances.RedrawCanvas();
          log($"Dwight: laid out {moved} component(s) across {result.BlockCount} block(s) - moves only, wiring and values unchanged, one Ctrl-Z reverts. Fade hints: {result.Fades.Count} wire(s).");
        }
        catch (Exception ex)
        {
          log($"Dwight: layout apply failed - {ex.Message}");
        }
      });
    }

    /// <summary>Soft, low-alpha cluster fills (cycled). Colour carries no universal meaning (ADR-085) - it is
    /// a per-stage readability cue, kept low-alpha so blocks read when zoomed out.</summary>
    private static readonly Color[] ClusterPalette =
    {
      Color.FromArgb(55, 120, 180, 90), Color.FromArgb(55, 95, 150, 210), Color.FromArgb(55, 235, 160, 70),
      Color.FromArgb(55, 175, 130, 210), Color.FromArgb(55, 235, 210, 90), Color.FromArgb(55, 90, 200, 190),
      Color.FromArgb(55, 220, 120, 140), Color.FromArgb(55, 150, 150, 150),
    };

    /// <summary>
    /// Phase 6 WP-D: apply the nested cluster layout as a behaviour-preserving Reorganise. In ONE
    /// GH_UndoRecord (ADR-031): move components to the engine's coordinates (anchored so the set stays where it
    /// was), add a FLAT coloured group per cluster named with the LLM label (never nested, ADR-085), add a
    /// label scribble header, and fade long wires (display-only). The BEHAVIOUR FINGERPRINT is captured before
    /// and after and asserted identical (ADR-086): only pivots, groups, scribbles, colour and wire-DISPLAY
    /// change, none of which touch computation, so the definition computes byte-identical geometry - and if the
    /// fingerprint ever differs, Dwight says so and the user reverts with one Ctrl-Z. Caller is on the UI thread.
    /// </summary>
    public static void ApplyClusteredReorganise(GH_Document doc, DwightGraph graph, Dwight.Layout.ClusterLayout.Result layout,
      IReadOnlyDictionary<int, int> macro, IReadOnlyDictionary<int, string> labels, Action<string> log, Action<ApplyOutcome> done = null,
      IDictionary<Guid, Dwight.Layout.PointD> centresSink = null)
    {
      doc.ScheduleSolution(10, d =>
      {
        var actions = new List<IGH_UndoAction>();
        var recorded = false;
        try
        {
          var before = BehaviourFingerprint.Of(d);

          var idToGuid = IdToGuidFromGraph(graph, layout.Centers.Keys);
          var replaced = ReplaceContainedGroups(d, new HashSet<Guid>(idToGuid.Values), actions);   // RR15 #31: replace fully-contained existing groups
          var (guidToCluster, guidToCentre) = GuidMaps(idToGuid, macro, layout.Centers);
          var (moved, grouped, _, shiftX, shiftY) = ApplyLayoutCore(d, idToGuid, layout, labels, actions);
          FillCentresSink(centresSink, idToGuid, layout.Centers, shiftX, shiftY);   // WP-1 RR1: for the reopen liveness check
          var faded = FadeInterGroupWires(d, guidToCluster, guidToCentre, actions);   // inter-group only, undoable

          // Guard BEFORE recording (WP-1.3): if the fingerprint moved, this apply changed computation - a bug -
          // so AUTO-REVERT (matching the best-practice path) and report STOPPED, never a false "verified".
          var after = BehaviourFingerprint.Of(d);
          if (before != after)
          {
            RollbackOrRecord(d, actions, "Dwight: Reorganise (rolled back)");
            log("Dwight: STOPPED - Reorganise would change what this definition computes (a bug); auto-reverted. Please report this.");
            done?.Invoke(ApplyOutcome.Stop("Reorganise stopped",
              "Reorganise would have changed what this definition computes, so Dwight reverted it - nothing on the canvas changed. This is a bug; please report it."));
            return;
          }

          if (actions.Count > 0) { Dwight.Session.ChangeLog.NoteVerb(d, d.UndoUtil.RecordEvent("Dwight: Reorganise", actions), "Dwight: Reorganise", DwightVerbs.Reorganise); recorded = true; Instances.RedrawCanvas(); }
          var summary = $"Reorganised - moved {moved} component(s), {grouped} labelled group(s), faded {faded} wire(s)." +
                        (replaced > 0 ? $" Replaced {replaced} existing group(s)." : "") +
                        " Wiring and values are unchanged (verified); one Ctrl-Z reverts the layout and the wire display.";
          log(summary);
          done?.Invoke(ApplyOutcome.Ok(summary));
        }
        catch (Exception ex)
        {
          // A mid-apply throw may have mutated the document: make everything done so far Ctrl-Z-revertible in one
          // event (the guardrail: never leave a rewired document with no undo), then report the failure (WP-1.1).
          try { if (!recorded && actions.Count > 0) Dwight.Session.ChangeLog.NoteVerb(d, d.UndoUtil.RecordEvent("Dwight: Reorganise (incomplete)", actions), "Dwight: Reorganise (incomplete)", DwightVerbs.Reorganise); } catch { }
          try { Instances.RedrawCanvas(); } catch { }
          log($"Dwight: reorganise apply failed - {ex.Message}");
          done?.Invoke(ApplyOutcome.Fail("Reorganise hit a problem",
            "Dwight hit an error while applying the layout: " + ex.Message +
            (actions.Count > 0 ? " Any partial change reverts with one Ctrl-Z." : "")));
        }
      });
    }

    /// <summary>
    /// Phase 6 WP-D, the BEST-PRACTICE pass (the gated follow-on, ADR-085 group contract). Everything the core
    /// Reorganise does, but instead of fading long inter-group wires it makes each group's interface EXPLICIT:
    /// for every wire that crosses a group boundary it inserts a labelled output param on group N, a labelled
    /// input param on group N+1, and wires them param-to-param - so a reader sees each group's named inputs and
    /// outputs and the wiring straightens. EVERY inserted element is a provably PASS-THROUGH generic param
    /// (one source, no tree toggle, no value), so the relay-transparent behaviour fingerprint splices it out
    /// and stays identical; if any insertion were NOT pass-through the fingerprint would differ and the whole
    /// pass ABORTS (one Ctrl-Z, nothing applied). Caller is on the UI thread.
    /// </summary>
    public static void ApplyBestPractice(GH_Document doc, DwightGraph graph, IReadOnlyDictionary<int, int> macro,
      IReadOnlyDictionary<int, string> labels, IReadOnlyList<(string Kind, IReadOnlyList<int> Clusters)> hints,
      Dwight.Layout.ArrangementMode arrangement, Action<string> log, Action<ApplyOutcome> done = null)
    {
      doc.ScheduleSolution(10, d =>
      {
        var actions = new List<IGH_UndoAction>();
        var recorded = false;
        try
        {
          var before = BehaviourFingerprint.Of(d);

          // RR15 #31: replace the groups this Reorganise fully contains (before inserting Dwight's own grouping).
          var replaced = ReplaceContainedGroups(d, new HashSet<Guid>(IdToGuidFromGraph(graph, macro.Keys).Values), actions);

          // 1. INSERT the boundary params FIRST (real pass-through components) and capture the records, so the
          //    layout below can place them as first-class nodes. Ordering fix: segment -> INSERT -> layout -> apply.
          var records = InsertBoundaryParams(d, graph, macro, actions);

          // 2. build the AUGMENTED layout graph EXPLICITLY from the records - NOT by rebuilding DwightGraph from
          //    the doc, which would depend on GH reflecting the just-created wires (the source of the "params in
          //    a separate region" bug: unreflected wires -> disconnected nodes -> forest-packed into a band).
          //    Built in code it is deterministic: each param is a real node with real edges
          //    (source -> P_out -> P_in -> consumer), so it lays out AT the boundary, in its cluster.
          var ids = new HashSet<int>(macro.Keys);
          var (lnodes, ledges) = GhLayout.BuildInput(graph, d, ids);
          var nextId = lnodes.Count == 0 ? 0 : lnodes.Max(n => n.Id) + 1;

          var augNodes = new List<Dwight.Layout.LayoutNode>(lnodes);
          var augEdges = new List<Dwight.Layout.LayoutEdge>();
          foreach (var e in ledges)   // keep intra-cluster edges; inter-cluster edges are replaced by param routing
            if (macro.TryGetValue(e.FromId, out var cf) && macro.TryGetValue(e.ToId, out var ct) && cf == ct)
              augEdges.Add(e);

          var augMacro = new Dictionary<int, int>(macro);
          var idToGuid = IdToGuidFromGraph(graph, ids);
          var pOutIds = new List<(int id, int cluster)>();   // outputs -> snap to their group's RIGHT edge
          var pInIds = new List<(int id, int cluster)>();    // inputs  -> snap to their group's LEFT edge
          foreach (var r in records)
          {
            var srcCompId = graph.NodeByGuid(r.SrcCompGuid)?.Id ?? -1;
            if (srcCompId < 0) continue;
            var outId = nextId++;
            augNodes.Add(new Dwight.Layout.LayoutNode(outId, r.OutGuid, BoundaryParamW, BoundaryParamH));
            augMacro[outId] = r.Cu; idToGuid[outId] = r.OutGuid; pOutIds.Add((outId, r.Cu));
            augEdges.Add(new Dwight.Layout.LayoutEdge(srcCompId, outId));
            foreach (var pin in r.Pins)
            {
              var inId = nextId++;
              augNodes.Add(new Dwight.Layout.LayoutNode(inId, pin.InGuid, BoundaryParamW, BoundaryParamH));
              augMacro[inId] = pin.Cv; idToGuid[inId] = pin.InGuid; pInIds.Add((inId, pin.Cv));
              augEdges.Add(new Dwight.Layout.LayoutEdge(outId, inId));
              foreach (var cg in pin.ConsumerCompGuids)
              { var cid = graph.NodeByGuid(cg)?.Id ?? -1; if (cid >= 0) augEdges.Add(new Dwight.Layout.LayoutEdge(inId, cid)); }
            }
          }

          // WP-4.7: a boundary insertion can be SKIPPED by its per-boundary catch; that crossing then gets no
          // param routing, and the filter above dropped its original inter-cluster edge - so the two wired
          // clusters would forest-pack apart. Keep a DIRECT cluster edge for any inter-cluster crossing the
          // records did not handle, so wired clusters stay adjacent (a handled crossing is already connected via
          // its params; the layout dedups a coincidental duplicate).
          var handledCrossings = new HashSet<(int, int)>();
          foreach (var r in records) foreach (var pin in r.Pins) handledCrossings.Add((r.Cu, pin.Cv));
          foreach (var e in ledges)
            if (macro.TryGetValue(e.FromId, out var cf2) && macro.TryGetValue(e.ToId, out var ct2)
                && cf2 != ct2 && !handledCrossings.Contains((cf2, ct2)))
              augEdges.Add(e);

          // RR16 #33: the boundary layout honours the Settings arrangement (compact/chain/vertical/grid), like a
          // normal Reorganise's first-shown mode - it just stays terminal (no post-hoc switcher, params inserted).
          var layout = Dwight.Layout.ClusterLayout.Layout(augNodes, augEdges, augMacro, hints, new Dwight.Layout.LayoutOptions { Arrangement = arrangement });
          var sizeById = augNodes.ToDictionary(n => n.Id, n => (n.Width, n.Height));
          SnapParamsToEdges(layout, augMacro, sizeById, pInIds, pOutIds);   // inputs in a left-edge column, outputs in a right-edge column

          // 3. apply from that ONE layout, so components, groups, scribbles and params are mutually consistent:
          //    move (incl the params, engine-placed at the boundary) + flat labelled groups (params auto-join via
          //    augMacro) + scribbles (boxes final) + apply the wire-display setting to the boundary crossings.
          var (moved, grouped, _, _, _) = ApplyLayoutCore(d, idToGuid, layout, labels, actions);
          var faded = SetBoundaryWireDisplay(d, records, actions);   // every boundary crossing honours the setting (no length gate)

          // 4a. WP-3 COMPLETENESS: every inserted boundary param must be CONNECTED - a source AND >=1 recipient.
          //     A pass-through param with a source but NO recipient is relay-spliced OUT of the fingerprint, so the
          //     hash guard below cannot see that orphan; completeness catches it and rolls back (the airtight-C
          //     generalization, reusing the GH-free decision core).
          if (!BoundaryParamsConnected(d, records, out var orphan))
          {
            RollbackOrRecord(d, actions, "Dwight: Reorganise (best-practice, rolled back)");
            log("Dwight: STOPPED - a boundary param was left unconnected (" + orphan + "); rolled back, nothing applied.");
            done?.Invoke(ApplyOutcome.Stop("Best-practice stopped",
              "A labelled boundary param was left unconnected, so Dwight rolled the change back - nothing on the canvas changed. This is a bug; please report it."));
            return;
          }

          // 4b. guard on the FINAL applied graph: pass-through params splice out of the relay-transparent
          //    fingerprint, so an identical hash proves behaviour is preserved. If not, roll back and abort.
          var after = BehaviourFingerprint.Of(d);
          if (before != after)
          {
            RollbackOrRecord(d, actions, "Dwight: Reorganise (best-practice, rolled back)");
            log("Dwight: STOPPED - the best-practice boundary insertion would change what this definition computes; " +
                "rolled back, nothing applied. (This is a bug; please report it.)");
            done?.Invoke(ApplyOutcome.Stop("Best-practice stopped",
              "The best-practice boundary insertion would have changed what this definition computes, so Dwight rolled it back - nothing on the canvas changed. This is a bug; please report it."));
            return;
          }

          if (actions.Count > 0) { Dwight.Session.ChangeLog.NoteVerb(d, d.UndoUtil.RecordEvent("Dwight: Reorganise (best-practice)", actions), "Dwight: Reorganise (best-practice)", DwightVerbs.Reorganise); recorded = true; Instances.RedrawCanvas(); }
          var summary = $"Reorganised (best-practice) - moved {moved} component(s), {grouped} labelled group(s), " +
                        $"{records.Count} labelled boundary interface(s), faded {faded} boundary wire(s)." +
                        (replaced > 0 ? $" Replaced {replaced} existing group(s)." : "") +
                        " Wiring is behaviour-identical (verified, pass-through); one Ctrl-Z reverts.";
          log(summary);
          done?.Invoke(ApplyOutcome.Ok(summary));
        }
        catch (Exception ex)
        {
          // Undo-first guarantee (WP-1.1): InsertBoundaryParams/ReplaceSource already mutated the document, so on
          // any throw make everything done so far Ctrl-Z-revertible in one event before reporting.
          try { if (!recorded && actions.Count > 0) Dwight.Session.ChangeLog.NoteVerb(d, d.UndoUtil.RecordEvent("Dwight: Reorganise (best-practice, incomplete)", actions), "Dwight: Reorganise (best-practice, incomplete)", DwightVerbs.Reorganise); } catch { }
          try { Instances.RedrawCanvas(); } catch { }
          log($"Dwight: best-practice apply failed - {ex.Message}");
          done?.Invoke(ApplyOutcome.Fail("Best-practice hit a problem",
            "Dwight hit an error during the best-practice pass: " + ex.Message +
            (actions.Count > 0 ? " Any partial change reverts with one Ctrl-Z." : "")));
        }
      });
    }

    /// <summary>Shared core for both Reorganise paths: move components (anchored) and add one flat labelled
    /// coloured group + scribble header per cluster. Returns the per-cluster group map (for boundary insertion)
    /// and the anchor shift. Appends to <paramref name="actions"/>; the caller owns the undo record.</summary>
    private static (int moved, int grouped, Dictionary<int, GH_Group> groups, double shiftX, double shiftY) ApplyLayoutCore(
      GH_Document d, IReadOnlyDictionary<int, Guid> idToGuid, Dwight.Layout.ClusterLayout.Result layout,
      IReadOnlyDictionary<int, string> labels, List<IGH_UndoAction> actions)
    {
      // anchor shift (engine frame -> canvas): keep the laid-out set's top-left where it was (ADR-062).
      var (shiftX, shiftY) = AnchorShift(d, idToGuid, layout.Centers);
      var moved = MoveToTargets(d, idToGuid, layout.Centers, shiftX, shiftY, actions);

      var i = 0;
      var grouped = 0;
      var groups = new Dictionary<int, GH_Group>();
      foreach (var box in layout.Clusters.OrderBy(b => b.Id))
      {
        var members = box.Members.Select(id => idToGuid.TryGetValue(id, out var g) ? (Guid?)g : null)
          .Where(g => g.HasValue && d.FindObject(g.Value, true) != null).Select(g => g.Value).ToList();
        if (members.Count == 0) { i++; continue; }

        labels.TryGetValue(box.Id, out var label);
        // U29 (#948): the one generic label - "Region N" - never "Cluster", which is a Grasshopper component type.
        label = string.IsNullOrWhiteSpace(label) ? Dwight.Labelling.RegionVocabulary.GenericLabel(box.Id) : label;

        try
        {
          var group = new GH_Group { NickName = label, Colour = ClusterPalette[i % ClusterPalette.Length] };
          foreach (var g in members) group.AddObject(g);
          d.AddObject(group, false, d.Objects.Count);
          actions.Add(new GH_AddObjectAction(group));
          groups[box.Id] = group;
          grouped++;
          // Place the scribble in the cluster's reserved HEADER band (just above the body, which the layout
          // pushed down by HeaderHeight) and make it a GROUP MEMBER, so the label reads INSIDE the box as a
          // header and the box wraps it - while the reserved band keeps neighbouring clusters clear. Box from
          // the final canvas bounds (box + shift), not live bounds (stale right after a move + ExpireLayout).
          var bx = (float)(box.MinX + shiftX); var by = (float)(box.MinY + shiftY);
          var bw = (float)(box.MaxX - box.MinX);
          if (TryAddLabelScribble(d, label, bx, by, bw, out var scribble))
          {
            actions.Add(new GH_AddObjectAction(scribble));
            try { group.AddObject(scribble.InstanceGuid); } catch { /* membership is best-effort */ }
          }
        }
        catch { /* skip a single bad group; the rest still applies (ADR-029) */ }
        i++;
      }
      return (moved, grouped, groups, shiftX, shiftY);
    }

    /// <summary>WP-1 runtime round 1 (the flip bug fix): a MOVES-ONLY arrangement flip. The first Reorganise owns
    /// creation of the groups, scribbles, colours and labels; a subsequent flip ONLY moves the components and
    /// repositions the existing scribbles to the new cluster headers (GH groups follow their members). So
    /// flipping N times leaves ONE set of groups in N shapes - never the stacked duplicate group sets the old
    /// full-apply flip produced - and it preserves any user edits to the group NAMES between flips. Moves only,
    /// so the behaviour fingerprint stays byte-identical; one anchored Ctrl-Z per flip, outcome surfaced.</summary>
    public static void ApplyArrangementMove(GH_Document doc, DwightGraph graph, Dwight.Layout.ClusterLayout.Result layout,
      IReadOnlyDictionary<Guid, int> guidToCluster, string arrangementLabel, Action<string> log, Action<ApplyOutcome> done = null,
      IDictionary<Guid, Dwight.Layout.PointD> centresSink = null)
    {
      doc.ScheduleSolution(10, d =>
      {
        var actions = new List<IGH_UndoAction>();
        var recorded = false;
        try
        {
          var before = BehaviourFingerprint.Of(d);

          var idToGuid = IdToGuidFromGraph(graph, layout.Centers.Keys);
          var (shiftX, shiftY) = AnchorShift(d, idToGuid, layout.Centers);
          var moved = MoveToTargets(d, idToGuid, layout.Centers, shiftX, shiftY, actions);
          FillCentresSink(centresSink, idToGuid, layout.Centers, shiftX, shiftY);   // WP-1 RR1: for the reopen liveness check

          // reposition the existing scribbles to the new group headers (same placement as the first apply).
          var headerByCluster = new Dictionary<int, PointF>();
          foreach (var box in layout.Clusters)
            headerByCluster[box.Id] = new PointF((float)(box.MinX + shiftX), (float)(box.MinY + shiftY) - 30f * 1.8f - 10f);
          MoveScribblesToHeaders(d, guidToCluster, headerByCluster, actions);

          // moves only -> the fingerprint MUST be identical; if not, this is a bug, so auto-revert and say so.
          var after = BehaviourFingerprint.Of(d);
          if (before != after)
          {
            RollbackOrRecord(d, actions, "Dwight: Re-arrange (rolled back)");
            log("Dwight: STOPPED - the re-arrange would change what this definition computes (a bug); auto-reverted.");
            done?.Invoke(ApplyOutcome.Stop("Re-arrange stopped",
              "Re-arranging would have changed what this definition computes, so Dwight reverted it - nothing on the canvas changed. This is a bug; please report it."));
            return;
          }

          if (actions.Count > 0) { Dwight.Session.ChangeLog.NoteVerb(d, d.UndoUtil.RecordEvent("Dwight: Re-arrange", actions), "Dwight: Re-arrange", DwightVerbs.Reorganise); recorded = true; Instances.RedrawCanvas(); }
          var summary = $"{arrangementLabel} arrangement - moved {moved} component(s). " +
                        "Wiring and values are unchanged (verified); one Ctrl-Z reverts the arrangement.";
          log(summary);
          done?.Invoke(ApplyOutcome.Ok(summary));
        }
        catch (Exception ex)
        {
          try { if (!recorded && actions.Count > 0) Dwight.Session.ChangeLog.NoteVerb(d, d.UndoUtil.RecordEvent("Dwight: Re-arrange (incomplete)", actions), "Dwight: Re-arrange (incomplete)", DwightVerbs.Reorganise); } catch { }
          try { Instances.RedrawCanvas(); } catch { }
          log($"Dwight: re-arrange failed - {ex.Message}");
          done?.Invoke(ApplyOutcome.Fail("Re-arrange hit a problem",
            "Dwight hit an error while re-arranging: " + ex.Message + (actions.Count > 0 ? " Any partial change reverts with one Ctrl-Z." : "")));
        }
      });
    }

    /// <summary>Move each Dwight group's scribble to its cluster's new header. A Dwight labelled group is
    /// self-identified as one holding a GH_Scribble whose sibling component members map to a cluster (so a
    /// user's own group, which carries no Dwight scribble, is left alone). Moves only; the fingerprint ignores
    /// scribbles, so this never affects the guard.</summary>
    private static int MoveScribblesToHeaders(GH_Document d, IReadOnlyDictionary<Guid, int> guidToCluster,
      IReadOnlyDictionary<int, PointF> headerByCluster, List<IGH_UndoAction> actions)
    {
      var moved = 0;
      foreach (var obj in d.Objects.ToList())
      {
        if (!(obj is GH_Group g)) continue;
        var scribbleGuid = Guid.Empty;
        int? cluster = null;
        foreach (var mid in GroupMemberIds(g))
        {
          var m = d.FindObject(mid, false);
          if (m is GH_Scribble) { if (scribbleGuid == Guid.Empty) scribbleGuid = mid; }
          else if (cluster == null && guidToCluster.TryGetValue(mid, out var c)) cluster = c;
        }
        if (scribbleGuid == Guid.Empty || cluster == null) continue;                 // not a Dwight labelled group
        if (!headerByCluster.TryGetValue(cluster.Value, out var target)) continue;

        var s = d.FindObject(scribbleGuid, false);
        if (s?.Attributes == null) continue;
        var p = s.Attributes.Pivot;
        var dx = target.X - p.X; var dy = target.Y - p.Y;
        if (Math.Abs(dx) < 0.5f && Math.Abs(dy) < 0.5f) continue;
        actions.Add(new GH_PivotAction(s));
        s.Attributes.Pivot = new PointF(p.X + dx, p.Y + dy);
        try { var b = s.Attributes.Bounds; s.Attributes.Bounds = new RectangleF(b.X + dx, b.Y + dy, b.Width, b.Height); } catch { }
        s.Attributes.ExpireLayout();
        moved++;
      }
      return moved;
    }

    private static IEnumerable<Guid> GroupMemberIds(GH_Group g)
    {
      try { return g.ObjectIDs ?? new List<Guid>(); } catch { return new List<Guid>(); }
    }

    /// <summary>RR15 #31: Reorganise REPLACES the groups it fully contains. Delete every existing group whose
    /// component members are ALL inside the reorganised scope (a group spilling outside is left alone), plus its
    /// own label scribbles (they travel with the box), in the SAME undo record - then Dwight's grouping is applied
    /// cleanly, no overlapping boxes. Behaviourally safe: groups + scribbles are non-computational (the fingerprint
    /// ignores them). Returns the count for the summary. GH read; the containment decision is the GH-free core.</summary>
    private static int ReplaceContainedGroups(GH_Document d, ISet<Guid> scope, List<IGH_UndoAction> actions)
    {
      try
      {
        var groups = d.Objects.OfType<GH_Group>().ToList();
        var compMembers = new List<IReadOnlyList<Guid>>();
        foreach (var g in groups)
        {
          var comps = new List<Guid>();
          foreach (var mid in GroupMemberIds(g))
          {
            var m = d.FindObject(mid, false);
            if (m != null && !(m is GH_Group) && !(m is GH_Scribble)) comps.Add(mid);   // component members only
          }
          compMembers.Add(comps);
        }

        var removed = 0;
        foreach (var idx in Dwight.Simplify.GroupContainment.ContainedGroups(compMembers, scope))
        {
          var g = groups[idx];
          try
          {
            foreach (var mid in GroupMemberIds(g).ToList())   // the group's own label scribbles go with it
            {
              var m = d.FindObject(mid, false);
              if (m is GH_Scribble) { actions.Add(new GH_RemoveObjectAction(m)); d.RemoveObject(m, false); }
            }
            actions.Add(new GH_RemoveObjectAction(g)); d.RemoveObject(g, false);
            removed++;
          }
          catch { /* skip a single bad group; the rest still applies (ADR-029) */ }
        }
        return removed;
      }
      catch { return 0; }
    }

    /// <summary>WP-1 RR2: does the reorganised set still have its Dwight groups (a group holding a GH_Scribble
    /// whose sibling members map to a cluster)? A full undo removes them, so a restore must recreate rather than
    /// just move. Read on the UI thread before scheduling the apply.</summary>
    public static bool HasArrangementGroups(GH_Document d, IReadOnlyDictionary<Guid, int> guidToCluster)
    {
      if (d == null || guidToCluster == null) return false;
      foreach (var obj in d.Objects)
      {
        if (!(obj is GH_Group g)) continue;
        bool hasScribble = false, hasClusterMember = false;
        foreach (var mid in GroupMemberIds(g))
        {
          var m = d.FindObject(mid, false);
          if (m is GH_Scribble) hasScribble = true;
          else if (guidToCluster.ContainsKey(mid)) hasClusterMember = true;
        }
        if (hasScribble && hasClusterMember) return true;
      }
      return false;
    }

    /// <summary>WP-1 RR1: record the FINAL canvas centre (engine centre + anchor shift) each component was moved
    /// to, keyed by guid, so a dock reopen can tell whether the arrangement is still in place (stored centre vs
    /// live pivot) and offer to restore it if the user undid or moved things.</summary>
    private static void FillCentresSink(IDictionary<Guid, Dwight.Layout.PointD> sink,
      IReadOnlyDictionary<int, Guid> idToGuid, IReadOnlyDictionary<int, Dwight.Layout.PointD> centres, double shiftX, double shiftY)
    {
      if (sink == null) return;
      sink.Clear();
      foreach (var kv in centres)
        if (idToGuid.TryGetValue(kv.Key, out var guid))
          sink[guid] = new Dwight.Layout.PointD(kv.Value.X + shiftX, kv.Value.Y + shiftY);
    }

    /// <summary>The shift that puts the laid-out set's bounding-box top-left back where it was, from the live
    /// bounds (read once, before any move). Objects with degenerate (un-laid-out) bounds - e.g. just-inserted
    /// boundary params - are SKIPPED, so the anchor is taken from the real set, not their (0,0) placeholder.</summary>
    private static (double, double) AnchorShift(GH_Document d, IReadOnlyDictionary<int, Guid> idToGuid, IReadOnlyDictionary<int, Dwight.Layout.PointD> centres)
    {
      double origMinX = double.MaxValue, origMinY = double.MaxValue, newMinX = double.MaxValue, newMinY = double.MaxValue;
      foreach (var kv in centres)
      {
        if (!idToGuid.TryGetValue(kv.Key, out var guid)) continue;
        var obj = d.FindObject(guid, true);
        if (obj?.Attributes == null) continue;
        var b = obj.Attributes.Bounds;
        if (b.Width < 1 || b.Height < 1) continue;   // skip fresh/degenerate bounds - anchor on the real set
        origMinX = Math.Min(origMinX, b.X); origMinY = Math.Min(origMinY, b.Y);
        newMinX = Math.Min(newMinX, kv.Value.X - b.Width / 2.0); newMinY = Math.Min(newMinY, kv.Value.Y - b.Height / 2.0);
      }
      return origMinX == double.MaxValue ? (0.0, 0.0) : (origMinX - newMinX, origMinY - newMinY);
    }

    /// <summary>Move objects to target CENTRES (engine frame + the anchor shift). Moves only; one GH_PivotAction
    /// each, so they ride the single undo record. The current centre is the bounds centre when laid out, but
    /// falls back to the PIVOT for fresh objects (just-inserted params) whose bounds are still (0,0) - otherwise
    /// the move would add the target to the pivot and land them at double the offset (the origin-doubling bug).</summary>
    private static int MoveToTargets(GH_Document d, IReadOnlyDictionary<int, Guid> idToGuid, IReadOnlyDictionary<int, Dwight.Layout.PointD> centres,
      double shiftX, double shiftY, List<IGH_UndoAction> actions)
    {
      var moved = 0;
      foreach (var kv in centres)
      {
        if (!idToGuid.TryGetValue(kv.Key, out var guid)) continue;
        var obj = d.FindObject(guid, true);
        if (obj?.Attributes == null) continue;
        var b = obj.Attributes.Bounds;
        var p = obj.Attributes.Pivot;
        float curX, curY;
        if (b.Width < 1 || b.Height < 1) { curX = p.X; curY = p.Y; }                 // fresh param: pivot is the anchor
        else { curX = b.X + b.Width / 2f; curY = b.Y + b.Height / 2f; }              // laid out: bounds centre
        var dx = (float)(kv.Value.X + shiftX) - curX; var dy = (float)(kv.Value.Y + shiftY) - curY;
        if (Math.Abs(dx) < 0.5f && Math.Abs(dy) < 0.5f) continue;
        actions.Add(new GH_PivotAction(obj));
        obj.Attributes.Pivot = new PointF(p.X + dx, p.Y + dy);
        obj.Attributes.ExpireLayout();
        moved++;
      }
      return moved;
    }

    /// <summary>A larger scribble header placed just above the group's top edge, at the group's final canvas
    /// bounds - the navigation layer (ADR-085 §7), it does not fade when zoomed out.</summary>
    private static bool TryAddLabelScribble(GH_Document d, string text, float groupLeft, float groupTop, float groupWidth, out GH_Scribble scribble)
      => TryAddLabelScribble(d, text, groupLeft, groupTop, groupWidth, 30f, above: true, out scribble);

    /// <summary><paramref name="above"/> false places the scribble's top-left AT <paramref name="groupTop"/>
    /// instead of in the band above it - the scaffold map positions its own bands, so it needs the exact
    /// point rather than the Reorganise offset. The default keeps every shipped caller byte-identical.</summary>
    private static bool TryAddLabelScribble(GH_Document d, string text, float groupLeft, float groupTop,
      float groupWidth, float size, bool above, out GH_Scribble scribble)
    {
      scribble = null;
      text = text?.Trim();
      if (string.IsNullOrWhiteSpace(text)) return false;
      var s = new GH_Scribble { Text = text };
      try { s.Font = new Font("Space Grotesk", size, FontStyle.Bold); } catch { /* GDI substitutes */ }
      s.CreateAttributes();
      var x = groupLeft; var y = above ? groupTop - size * 1.8f - 10f : groupTop;   // above the group's top edge, or at it
      // WP-4.7: clamp the header width to the group plus its reserved side bands, so a long label never spills
      // out past the group and collides with the neighbouring cluster (the side band is what keeps groups clear).
      var maxW = groupWidth + 2f * (float)BoundarySideBand;
      var w = Math.Min(Math.Max(groupWidth, text.Length * size * 0.55f), maxW); var h = size * 1.7f;
      try { s.Attributes.Pivot = new PointF(x, y); s.Attributes.Bounds = new RectangleF(x, y, w, h); s.Attributes.ExpireLayout(); }
      catch { /* placement is best-effort */ }
      d.AddObject(s, false, d.Objects.Count);
      scribble = s;
      return true;
    }

    /// <summary>
    /// Phase-8 WP-3 Stage C: lay down ONE scaffold region - a labelled, coloured <see cref="GH_Group"/>
    /// whose initial members are its own two scribbles (#221 option 2).
    ///
    /// WHY A GROUP AT ALL, WHEN NOTHING IS BUILT YET: it is the only realisation that gives WP-4 a durable
    /// canvas-side handle. A scribble alone leaves nothing to address a region BY; placeholder components
    /// would pollute the user's definition with objects they must delete. A group exists, is flat
    /// (ADR-085), is behaviour-neutral - which the byte-identical verdict then PROVES rather than assumes -
    /// and it GROWS as WP-4 adds built components to it.
    ///
    /// TWO SCRIBBLES, reusing the ADR-085 machinery rather than a second convention (#221a): a large
    /// HEADER carrying the ADDRESS and the name (scribbles do not fade when zoomed out, so this is the
    /// navigation layer), and a smaller BODY carrying the intent and the pseudo-code, so the box says what
    /// the region is FOR. The group wraps both, so its box is the region.
    ///
    /// Additive only: it never touches, moves, or replaces anything already on the canvas (#221) - a
    /// scaffold describes work not yet done, so deleting a user's group to make room for a plan would be
    /// a mutation nobody consented to.
    /// </summary>
    internal static bool AddScaffoldRegion(GH_Document d, string groupName, string headerText, string bodyText,
      float left, float top, float width, int paletteIndex, List<IGH_UndoAction> actions, out GH_Group group)
    {
      group = null;
      try
      {
        var g = new GH_Group { NickName = groupName, Colour = ClusterPalette[Math.Abs(paletteIndex) % ClusterPalette.Length] };
        d.AddObject(g, false, d.Objects.Count);
        actions.Add(new GH_AddObjectAction(g));

        // The BODY sits inside the box, at the exact point the layout reserved for it; the HEADER rides in
        // the band above it, exactly as a Reorganise cluster's label does.
        if (TryAddLabelScribble(d, bodyText, left, top, width, 11f, above: false, out var body))
        {
          actions.Add(new GH_AddObjectAction(body));
          try { g.AddObject(body.InstanceGuid); } catch { }
        }
        if (TryAddLabelScribble(d, headerText, left, top, width, out var header))
        {
          actions.Add(new GH_AddObjectAction(header));
          try { g.AddObject(header.InstanceGuid); } catch { }
        }

        group = g;
        return true;
      }
      catch
      {
        // One bad region never costs the rest of the map (ADR-029); the caller reports it BY NAME (#221c).
        return false;
      }
    }

    /// <summary>Fade long INTER-group wires to Faint (the 500px research norm) - never intra-group wires, which
    /// are short, local, and read worse faded (Bug 3). The wire display is per-input-param, so a param is only
    /// faded when ALL its sources are inter-group (so a fade can never hide an intra-group wire). The old
    /// display state is captured in the undo record, so Ctrl-Z restores it (Bug 2). Display-only - not in the
    /// behaviour fingerprint.</summary>
    private static int FadeInterGroupWires(GH_Document d, IReadOnlyDictionary<Guid, int> guidToCluster,
      IReadOnlyDictionary<Guid, Dwight.Layout.PointD> guidToCentre, List<IGH_UndoAction> actions)
    {
      const double longWire = 500.0;
      if (!TryWireDisplayTarget(out var target)) return 0;   // the user chose "solid" -> leave the wires
      var faded = 0;
      try
      {
        foreach (var obj in d.Objects)
        {
          if (!guidToCentre.TryGetValue(obj.InstanceGuid, out var tc) || !guidToCluster.TryGetValue(obj.InstanceGuid, out var tCluster)) continue;
          var inputs = obj is IGH_Component c ? (IEnumerable<IGH_Param>)c.Params.Input : obj is IGH_Param p ? new[] { p } : Array.Empty<IGH_Param>();
          foreach (var ip in inputs)
          {
            if (ip?.Sources == null || ip.Sources.Count == 0) continue;
            var anyIntra = false; var anyLongInter = false;
            foreach (var s in ip.Sources)
            {
              var owner = s?.Attributes?.GetTopLevel?.DocObject;
              var sguid = owner?.InstanceGuid ?? Guid.Empty;
              if (owner == null || !guidToCluster.TryGetValue(sguid, out var sCluster)) { anyIntra = true; break; }   // unknown -> treat as protected
              if (sCluster == tCluster) { anyIntra = true; break; }                                                  // an intra-group wire -> never fade this param
              if (guidToCentre.TryGetValue(sguid, out var sc))
              {
                var dx = tc.X - sc.X; var dy = tc.Y - sc.Y;
                if (Math.Sqrt(dx * dx + dy * dy) >= longWire) anyLongInter = true;
              }
            }
            if (anyIntra || !anyLongInter) continue;
            if (ip.WireDisplay != target)
            {
              actions.Add(new GH_WireDisplayAction(ip));   // GH's built-in - captures the OLD display, so Ctrl-Z restores it
              ip.WireDisplay = target;
              ip.Attributes?.ExpireLayout();
              faded++;
            }
          }
        }
      }
      catch { }
      return faded;
    }

    /// <summary>
    /// The ADR-085 group contract (best-practice pass), inserted BEFORE layout. For every wire crossing a group
    /// boundary, add a labelled OUTPUT param on the upstream group and a labelled INPUT param on the downstream
    /// group, wired source -> P_out -> P_in -> consumer. Deduped by (source output, downstream cluster) so a
    /// fan-out shares one boundary pair. EVERY inserted param is a generic, single-source, no-transform
    /// PASS-THROUGH, so it computes nothing new (the relay-transparent fingerprint splices it out). Each
    /// retargeted consumer carries a GH_WireAction so the rewiring rides the undo record. The params are PARKED
    /// near their source's current position (so the layout anchor is not skewed) and POSITIONED + GROUPED by the
    /// subsequent layout, which treats them as first-class nodes. Returns each inserted param's guid -> cluster.
    /// </summary>
    private static List<BoundaryRecord> InsertBoundaryParams(GH_Document d, DwightGraph graph,
      IReadOnlyDictionary<int, int> macro, List<IGH_UndoAction> actions)
    {
      var records = new List<BoundaryRecord>();

      // collect crossings grouped by SOURCE param: src -> (downstream cluster -> the consumer inputs). Grouping
      // by source (not source x cluster) lets one shared output param feed every downstream group, with a
      // separate input param in each - so a curve from group A that feeds B and C reads as ONE output on A and
      // one labelled input each on B and C, not a duplicate output per consumer.
      var bySrc = new Dictionary<Guid, Dictionary<int, List<IGH_Param>>>();
      var srcParamByGuid = new Dictionary<Guid, IGH_Param>();
      var srcClusterByGuid = new Dictionary<Guid, int>();
      var srcOwnerByGuid = new Dictionary<Guid, IGH_DocumentObject>();

      foreach (var obj in d.Objects)
      {
        var tNode = graph.NodeByGuid(obj.InstanceGuid);
        if (tNode == null || !macro.TryGetValue(tNode.Id, out var cv)) continue;
        var inputs = obj is IGH_Component c ? (IEnumerable<IGH_Param>)c.Params.Input : obj is IGH_Param p ? new[] { p } : Array.Empty<IGH_Param>();
        foreach (var ip in inputs)
        {
          if (ip?.Sources == null) continue;
          foreach (var s in ip.Sources.ToList())
          {
            var owner = s?.Attributes?.GetTopLevel?.DocObject;
            var sNode = owner == null ? null : graph.NodeByGuid(owner.InstanceGuid);
            if (sNode == null || !macro.TryGetValue(sNode.Id, out var cu) || cu == cv) continue;   // intra / unknown -> leave it
            if (!bySrc.TryGetValue(s.InstanceGuid, out var cvMap)) { cvMap = new Dictionary<int, List<IGH_Param>>(); bySrc[s.InstanceGuid] = cvMap; }
            if (!cvMap.TryGetValue(cv, out var list)) { list = new List<IGH_Param>(); cvMap[cv] = list; }
            list.Add(ip);
            srcParamByGuid[s.InstanceGuid] = s;
            srcClusterByGuid[s.InstanceGuid] = cu;
            srcOwnerByGuid[s.InstanceGuid] = owner;
          }
        }
      }

      var wireCaptured = new HashSet<Guid>();
      foreach (var srcGuid in bySrc.Keys.OrderBy(g => g))
      {
        if (!srcParamByGuid.TryGetValue(srcGuid, out var srcParam)) continue;
        var cu = srcClusterByGuid[srcGuid];
        var srcOwner = srcOwnerByGuid[srcGuid];
        try
        {
          var srcName = string.IsNullOrWhiteSpace(srcParam.NickName) ? (srcParam.Name ?? "out") : srcParam.NickName;
          var nick = Truncate(srcName, 24);
          var basePt = srcOwner?.Attributes?.Pivot ?? new PointF(0, 0);

          // ONE shared output param on the source's group. (AddObject leaves a fresh param's Bounds at the
          // origin even with the pivot set, so ParkParam sets pivot AND bounds together; the layout relocates it.)
          var pOut = new Param_GenericObject { NickName = nick };
          pOut.CreateAttributes();
          d.AddObject(pOut, false, d.Objects.Count); actions.Add(new GH_AddObjectAction(pOut));
          ParkParam(pOut, basePt.X + 20, basePt.Y);
          pOut.AddSource(srcParam);

          // ...and ONE input param per downstream group, each fed from the shared output.
          var pins = new List<PinRecord>();
          var k = 1;
          foreach (var cvkv in bySrc[srcGuid].OrderBy(x => x.Key))
          {
            var pIn = new Param_GenericObject { NickName = nick };
            pIn.CreateAttributes();
            d.AddObject(pIn, false, d.Objects.Count); actions.Add(new GH_AddObjectAction(pIn));
            ParkParam(pIn, basePt.X + 40, basePt.Y + 20 * k); k++;
            pIn.AddSource(pOut);

            var consumerComps = new HashSet<Guid>();
            foreach (var ip in cvkv.Value)
            {
              if (wireCaptured.Add(ip.InstanceGuid)) actions.Add(new GH_WireAction(ip));   // capture original sources once
              ip.ReplaceSource(srcParam, pIn);
              ip.Attributes?.ExpireLayout();
              var co = ip.Attributes?.GetTopLevel?.DocObject;
              if (co != null) consumerComps.Add(co.InstanceGuid);
            }
            pins.Add(new PinRecord { Cv = cvkv.Key, InGuid = pIn.InstanceGuid, ConsumerCompGuids = consumerComps.ToList() });
          }

          records.Add(new BoundaryRecord { SrcCompGuid = srcOwner?.InstanceGuid ?? Guid.Empty, Cu = cu, OutGuid = pOut.InstanceGuid, Pins = pins });
        }
        catch { /* skip a single bad boundary; the fingerprint guard still verifies the rest */ }
      }
      return records;
    }

    /// <summary>Place a freshly-inserted param at (x,y) with its Bounds centred there, so its bounds-centre and
    /// pivot agree before the move (a new GH param's AddObject leaves Bounds at the origin otherwise).</summary>
    private static void ParkParam(IGH_Param p, float x, float y)
    {
      try
      {
        p.Attributes.Pivot = new PointF(x, y);
        p.Attributes.Bounds = new RectangleF(x - (float)(BoundaryParamW / 2), y - (float)(BoundaryParamH / 2), (float)BoundaryParamW, (float)BoundaryParamH);
      }
      catch { }
    }

    /// <summary>Lay the boundary params out in a vertical COLUMN just outside each group's body - input params
    /// to the left, output params to the right - stacked (no mutual overlap) and clear of the body components
    /// (no component overlap). Centred on the body's vertical midpoint, ordered by the params' engine y so the
    /// reading order is preserved. This presents every group as [inputs] -> logic -> [outputs] regardless of
    /// where the internal consumer/source sits. Body bounds use the REAL members' measured sizes.</summary>
    private static void SnapParamsToEdges(Dwight.Layout.ClusterLayout.Result layout, Dictionary<int, int> augMacro,
      Dictionary<int, (double w, double h)> size, List<(int id, int cluster)> pIns, List<(int id, int cluster)> pOuts)
    {
      var paramIds = new HashSet<int>(pIns.Select(p => p.id));
      foreach (var p in pOuts) paramIds.Add(p.id);

      // body bounds per cluster from the REAL members (their measured size), excluding the params.
      var left = new Dictionary<int, double>(); var right = new Dictionary<int, double>();
      var top = new Dictionary<int, double>(); var bot = new Dictionary<int, double>();
      foreach (var kv in layout.Centers)
      {
        if (paramIds.Contains(kv.Key) || !augMacro.TryGetValue(kv.Key, out var c)) continue;
        var (w, h) = size.TryGetValue(kv.Key, out var s) ? s : (60.0, 30.0);
        var l = kv.Value.X - w / 2; var r = kv.Value.X + w / 2; var t = kv.Value.Y - h / 2; var b = kv.Value.Y + h / 2;
        left[c] = left.TryGetValue(c, out var lv) ? Math.Min(lv, l) : l;
        right[c] = right.TryGetValue(c, out var rv) ? Math.Max(rv, r) : r;
        top[c] = top.TryGetValue(c, out var tv) ? Math.Min(tv, t) : t;
        bot[c] = bot.TryGetValue(c, out var bv) ? Math.Max(bv, b) : b;
      }

      const double gap = 30, vgap = 14;
      void Place(List<(int id, int cluster)> ps, bool isInput)
      {
        foreach (var grp in ps.GroupBy(p => p.cluster))
        {
          var c = grp.Key;
          if (!left.ContainsKey(c)) continue;
          var col = grp.Select(p => p.id).Where(layout.Centers.ContainsKey).OrderBy(i => layout.Centers[i].Y).ToList();
          if (col.Count == 0) continue;
          var cx = isInput ? left[c] - gap - BoundaryParamW / 2 : right[c] + gap + BoundaryParamW / 2;
          var totalH = col.Count * BoundaryParamH + (col.Count - 1) * vgap;
          var y = (top[c] + bot[c]) / 2 - totalH / 2 + BoundaryParamH / 2;
          foreach (var id in col) { layout.Centers[id] = new Dwight.Layout.PointD(cx, y); y += BoundaryParamH + vgap; }
        }
      }
      Place(pIns, true);
      Place(pOuts, false);
    }

    /// <summary>The user's chosen wire display for inter-group wires (Settings): returns false for "solid"
    /// (leave them), else the Faint/Hidden target. Display-only - it never touches what the definition computes.</summary>
    private static bool TryWireDisplayTarget(out GH_ParamWireDisplay target)
    {
      target = GH_ParamWireDisplay.faint;
      var mode = (Dwight.Config.DwightSettings.Current?.ReorganiseWireDisplay ?? "faint").Trim().ToLowerInvariant();
      if (mode == "solid" || mode == "default" || mode == "none") return false;
      target = mode == "hidden" ? GH_ParamWireDisplay.hidden : GH_ParamWireDisplay.faint;
      return true;
    }

    /// <summary>Apply the wire-display setting to EVERY boundary crossing (the pOut -> pIn wires) - no length
    /// gate, since these are the deliberate group-interface links. The display sits on the input param (pIn), so
    /// only the inter-group crossing is affected; the intra-group src -> pOut and pIn -> consumer wires stay
    /// solid. Each change rides the undo record. Returns the count changed.</summary>
    private static int SetBoundaryWireDisplay(GH_Document d, List<BoundaryRecord> records, List<IGH_UndoAction> actions)
    {
      if (!TryWireDisplayTarget(out var target)) return 0;
      var n = 0;
      foreach (var r in records)
        foreach (var pin in r.Pins)
          if (d.FindObject(pin.InGuid, true) is IGH_Param p && p.WireDisplay != target)
          {
            actions.Add(new GH_WireDisplayAction(p));
            p.WireDisplay = target;
            p.Attributes?.ExpireLayout();
            n++;
          }
      return n;
    }

    /// <summary>WP-3 completeness: every inserted boundary param must be CONNECTED (a source AND >=1 recipient),
    /// so an orphaned pass-through param - which the relay-transparent fingerprint splices out and cannot catch -
    /// is caught here. Returns false with a human reason on the first orphan. Reuses the GH-free decision core.</summary>
    private static bool BoundaryParamsConnected(GH_Document d, IReadOnlyList<BoundaryRecord> records, out string reason)
    {
      var effects = new List<(bool, string)>();
      foreach (var r in records ?? new List<BoundaryRecord>())
      {
        effects.Add((ParamConnected(d, r.OutGuid), "a labelled boundary output param"));
        foreach (var pin in r.Pins ?? new List<PinRecord>())
          effects.Add((ParamConnected(d, pin.InGuid), "a labelled boundary input param"));
      }
      reason = Dwight.Simplify.Completeness.FirstNoOp(effects);
      return reason == null;
    }

    private static bool ParamConnected(GH_Document d, Guid g)
    {
      try { return d.FindObject(g, false) is IGH_Param p && p.SourceCount > 0 && p.Recipients != null && p.Recipients.Count > 0; }
      catch { return false; }
    }

    /// <summary>One inserted group-boundary interface: the upstream source component, its cluster, the ONE
    /// shared output param, and one input param per downstream group (each with the consumers it feeds) - enough
    /// to add them to the layout graph as real nodes/edges.</summary>
    private sealed class BoundaryRecord
    {
      public Guid SrcCompGuid;
      public int Cu;
      public Guid OutGuid;
      public List<PinRecord> Pins;
    }

    /// <summary>One downstream input param of a boundary interface: its group, its guid, and the components it feeds.</summary>
    private sealed class PinRecord
    {
      public int Cv;
      public Guid InGuid;
      public List<Guid> ConsumerCompGuids;
    }

    private const double BoundaryParamW = 50, BoundaryParamH = 24;
    private const double BoundarySideBand = 90;   // mirrors LayoutOptions.SideBand - the scribble-width clamp (WP-4.7)

    /// <summary>id -> guid for a set of graph nodes (the laid-out reals).</summary>
    private static Dictionary<int, Guid> IdToGuidFromGraph(DwightGraph graph, IEnumerable<int> ids)
    {
      var map = new Dictionary<int, Guid>();
      foreach (var id in ids) { var n = graph.NodeById(id); if (n != null) map[id] = n.Guid; }
      return map;
    }

    /// <summary>From an id->guid map + the cluster map + the layout centres, the guid-keyed cluster and centre
    /// lookups the move/group/fade work off (so reals and inserted params are handled uniformly).</summary>
    private static (Dictionary<Guid, int>, Dictionary<Guid, Dwight.Layout.PointD>) GuidMaps(
      IReadOnlyDictionary<int, Guid> idToGuid, IReadOnlyDictionary<int, int> macro, IReadOnlyDictionary<int, Dwight.Layout.PointD> centres)
    {
      var gc = new Dictionary<Guid, int>();
      foreach (var kv in macro) if (idToGuid.TryGetValue(kv.Key, out var g)) gc[g] = kv.Value;
      var gp = new Dictionary<Guid, Dwight.Layout.PointD>();
      foreach (var kv in centres) if (idToGuid.TryGetValue(kv.Key, out var g)) gp[g] = kv.Value;
      return (gc, gp);
    }

    private static string Truncate(string s, int max)
      => string.IsNullOrEmpty(s) ? s : (s.Length <= max ? s : s.Substring(0, max - 1) + "…");

    /// <summary>Shift a placed set clear to the right of the originals so it does not overlap.</summary>
    private static void OffsetClear(List<IGH_DocumentObject> placed)
    {
      var withAttr = placed.Where(o => o.Attributes != null).ToList();
      if (withAttr.Count == 0) return;

      var minX = withAttr.Min(o => o.Attributes.Pivot.X);
      var maxX = withAttr.Max(o => o.Attributes.Pivot.X);
      var dx = (maxX - minX) + 200f;

      foreach (var o in withAttr)
      {
        var p = o.Attributes.Pivot;
        o.Attributes.Pivot = new PointF(p.X + dx, p.Y);
        o.Attributes.ExpireLayout();
      }
    }
  }
}
