using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;
using Grasshopper.Kernel.Undo;
using Dwight.Data;
using Dwight.Layout;
using Dwight.UI;

namespace Dwight.Agent
{
  /// <summary>
  /// <c>propose_scaffold</c> - the map (Phase-8 WP-3 Stage C; the #219 substrate, ruled at #190 Q1).
  ///
  /// THE DIVISION, and it is ADR-056 in its cleanest form anywhere in the product: the model decomposed
  /// the work and named it; EVERY COORDINATE HERE IS COMPUTED. The regions become synthetic nodes, the
  /// declared dependencies become edges, and the SAME layout engine that arranges a real definition
  /// (ADR-084) arranges them - so a scaffold map is spaced by the same law that spaces a Reorganise, and
  /// the model never emits a position.
  ///
  /// THE VERDICT INSTRUMENT IS THE BYTE-IDENTICAL FINGERPRINT (#190 Q1), not <c>build_region</c>'s trio. A
  /// scaffold lays down groups and scribbles, which are non-computational, so the correct claim is the
  /// strong one: this changed NOTHING about what the definition computes. The trio would be the wrong
  /// instrument - there is no completeness to prove about work that has not been done.
  ///
  /// IT IS PURELY ADDITIVE (#221). It never moves, replaces, or deletes anything already on the canvas -
  /// not even a user's own groups. Reorganise may replace a group because it re-expresses the organisation
  /// of the SAME components; a scaffold describes work not yet done, so taking a user's group to make room
  /// for a plan would be a mutation nobody consented to. The whole map is anchored clear of existing work.
  /// </summary>
  internal static class GhProposeScaffold
  {
    private const string UndoName = "Dwight: scaffold map";

    /// <summary>How far clear of the existing definition the map is laid down, so it reads as a plan
    /// BESIDE the work rather than an annotation on top of it.</summary>
    private const float ClearanceX = 400f;

    /// <summary>
    /// EVERY ADDRESS ALREADY SPOKEN FOR, from BOTH halves (#720) - and it is a union because each half
    /// alone is wrong in its own direction:
    ///
    ///   - the CANVAS alone misses a region the user UNDID. Its group is gone, but the stored plan still
    ///     names it, <c>get_scaffold_map</c> still reports it, and the conversation still says "A2" - so
    ///     minting A2 again would put a new region under a name the thread is already using.
    ///   - the STORED MAP alone misses a group whose record was lost or aged out (#355), and misses every
    ///     map laid down by a different conversation on the same file. Those groups are on the canvas, and
    ///     they are what <c>TryFindGroup</c> would collide with.
    ///
    /// <c>ScaffoldMapRecord.Read</c> already performs exactly this join, both directions of mismatch
    /// included, and it is the reader <c>get_scaffold_map</c> answers from - so the addresses the map
    /// REPORTS and the addresses a new map avoids are the same set, by construction (#248: "which regions
    /// exist" must not come to mean two different things).
    /// </summary>
    private static bool TryInUseAddresses(GH_Document doc, string storedMapJson,
      out IReadOnlyCollection<string> inUse, out ToolResult error)
    {
      inUse = null; error = null;
      try
      {
        inUse = ScaffoldMapRecord.Read(storedMapJson, GhScaffoldMap.CanvasRegions(doc))
          .Regions.Select(r => r.Address)
          .Where(a => !string.IsNullOrWhiteSpace(a))
          .Distinct(StringComparer.Ordinal)
          .ToList();
        return true;
      }
      catch (Exception ex)
      {
        // A map we cannot read is NOT a licence to mint over it. "Nothing is in use" is a claim, and this
        // is the one path that cannot make it - an empty set here would be the #720 defect wearing a
        // fallback's clothes (#208). Refuse, before anything touches the canvas.
        error = ToolResult.Fail(ToolErrors.PreconditionRefused,
          "Dwight could not read which scaffold addresses this canvas already uses (" + ex.Message + "), and it " +
          "will not lay down a map that might answer to a name something else already answers to.");
        return false;
      }
    }

    /// <summary>Vertical gap between regions in one layer, once they are re-stacked in address order.</summary>
    private const double VerticalGap = 60;

    public static void Run(GH_Document doc, AgentIdRegistry ids, JsonObject args, string storedMapJson, Action<ToolResult> done)
    {
      if (doc == null) { done?.Invoke(ToolResult.Fail(ToolErrors.NoDocument, "No Grasshopper document is open.")); return; }

      // ---- 1. validate everything before anything touches the canvas --------------------------------
      if (!TryInUseAddresses(doc, storedMapJson, out var inUse, out var inUseError))
      { done?.Invoke(inUseError); return; }

      if (!ScaffoldPlanParser.TryParse(args, ids.State, inUse, out var plan, out var planError))
      { done?.Invoke(planError); return; }

      // ---- 2. geometry, entirely computed ----------------------------------------------------------
      Dictionary<string, ClusterLayout.ClusterBox> boxes;
      try { boxes = Arrange(plan); }
      catch (Exception ex)
      {
        done?.Invoke(ToolResult.Fail(ToolErrors.PreconditionRefused, "The map could not be laid out (" + ex.Message + ")."));
        return;
      }

      // ---- 3. the baseline, captured before the mutation --------------------------------------------
      string before;
      try { before = BehaviourFingerprint.Of(doc); }
      catch (Exception ex)
      {
        done?.Invoke(ToolResult.Fail(ToolErrors.PreconditionRefused, "Could not read the current state (" + ex.Message + ")."));
        return;
      }

      doc.ScheduleSolution(10, d =>
      {
        var actions = new List<IGH_UndoAction>();
        var committed = false;
        var placed = new List<ScaffoldRegion>();
        var unplaceable = new List<string>();
        var placedGroups = new List<Guid>();   // #291(2): what the map's own frame will be fitted to
        try
        {
          var offset = ClearOfExistingWork(d, boxes.Values);

          var i = 0;
          foreach (var region in plan.Regions)
          {
            var box = boxes[region.Ref];
            var left = (float)box.MinX + offset.X;
            var top = (float)box.MinY + offset.Y;

            if (CanvasWriter.AddScaffoldRegion(d, region.GroupName, region.HeaderText, region.BodyText,
                  left, top, (float)(box.MaxX - box.MinX), i, actions, out var placedGroup))
            {
              placed.Add(region);
              if (placedGroup != null) placedGroups.Add(placedGroup.InstanceGuid);
            }
            else
              unplaceable.Add(region.GroupName);   // reported BY NAME (#221c), never silently dropped
            i++;
          }

          Dwight.Session.ChangeLog.NoteChat(d, d.UndoUtil.RecordEvent(UndoName, actions), UndoName, ConsentCard.DoneFor("scaffold"));   // LAST statement in the try: one Ctrl-Z, or none; Brief AP keeps the id
          committed = true;

          // ---- 4. the verdict: byte-identical, or it did something it must not have -------------------
          var after = BehaviourFingerprint.Of(d);
          if (before != after)
          {
            UndoOnce(d);
            done?.Invoke(ToolResult.Fail(ToolErrors.VerificationFailed,
              "The map changed what this definition computes, which a scaffold must never do, so it was " +
              "reverted - the canvas is as it was. This is a bug; please report it.",
              Verdict(false, placed, unplaceable, plan)));
            return;
          }

          try { Instances.RedrawCanvas(); } catch { }

          // ---- 5. THE MAP FRAMES ITSELF (#291(2)) --------------------------------------------------
          //
          // A map lands CLEAR OF ALL EXISTING WORK (#242), chosen so it can never disturb anything - and
          // that same choice is what made it unreachable: it is placed exactly where the user is not
          // looking. A map you cannot get to is not a map.
          //
          // This is the ONE automatic framing in the product, and it is consented narrowly: the user
          // approved this map seconds ago and the map IS the deliverable. Everything else that moves the
          // canvas is a click. See NavReason.
          GhRegionTidy.NotePlaces(d, placedGroups);   // the amendment to #369: where the plugin laid them, so a move by the user can be told from none
          if (placedGroups.Count > 0) CanvasNav.GoTo(placedGroups, NavReason.MapJustLanded);

          done?.Invoke(ToolResult.Success(Verdict(true, placed, unplaceable, plan)));
        }
        catch (Exception ex)
        {
          if (!committed) CanvasWriter.RollbackOrRecord(d, actions, UndoName);
          else UndoOnce(d);
          done?.Invoke(ToolResult.Fail(ToolErrors.VerificationFailed,
            "The map could not be laid down and the canvas was left unchanged (" + ex.Message + ")."));
        }
      });
    }

    /// <summary>
    /// Lay the regions out with the SAME engine a real definition uses (ADR-084): one synthetic node per
    /// region, sized from what its body scribble will draw, edged by the declared dependencies. Horizontal
    /// is the flow, exactly as everywhere else - so the map reads left to right for the same reason a
    /// definition does.
    /// </summary>
    private static Dictionary<string, ClusterLayout.ClusterBox> Arrange(ScaffoldPlan plan)
    {
      var idByRef = plan.Regions.Select((r, i) => (r.Ref, i)).ToDictionary(t => t.Ref, t => t.i, StringComparer.OrdinalIgnoreCase);

      var nodes = plan.Regions
        .Select(r => new LayoutNode(idByRef[r.Ref], StableKey(r.Key), r.Width, r.Height))
        .ToList();

      var edges = plan.Edges
        .Select(e => new LayoutEdge(idByRef[e.From], idByRef[e.To]))
        .ToList();

      // One region per cluster: the cluster IS the region, so the engine's cluster boxes are the map.
      var macro = plan.Regions.ToDictionary(r => idByRef[r.Ref], r => idByRef[r.Ref]);

      var layout = ClusterLayout.Layout(nodes, edges, macro,
        new List<(string Kind, IReadOnlyList<int> Clusters)>(),   // no LLM arrangement hints: the flow decides
        new LayoutOptions { Arrangement = ArrangementMode.HorizontalChain });

      var byId = layout.Clusters.ToDictionary(c => c.Id);
      var boxes = plan.Regions.ToDictionary(r => r.Ref, r => byId[idByRef[r.Ref]], StringComparer.OrdinalIgnoreCase);

      OrderVerticallyByAddress(plan, boxes);
      return boxes;
    }

    /// <summary>
    /// D3: within a layer, the VERTICAL order must follow the ADDRESS.
    ///
    /// Horizontal position is the dependency layer and was always right. Vertical position, though, came
    /// out of the engine with nothing tying it to the address - so a map could read A3, B1, B2 across the
    /// top with A1 in the middle and A2 at the bottom. An address whose whole job is to say "read this
    /// first", printed third, is a map arguing with itself.
    ///
    /// The engine's SPACING is kept exactly as computed - only which region occupies which vertical slot
    /// changes, by swapping the slots among the regions of one layer. So the layout stays the engine's
    /// (ADR-084) and the reading order becomes the plugin's, which is where addressing already lives.
    /// </summary>
    private static void OrderVerticallyByAddress(ScaffoldPlan plan, Dictionary<string, ClusterLayout.ClusterBox> boxes)
    {
      foreach (var layer in plan.Regions.GroupBy(r => r.Layer))
      {
        var inAddressOrder = layer.OrderBy(AddressIndex).ToList();
        if (inAddressOrder.Count < 2) continue;

        // Re-stack the layer from its own top, in address order, each region keeping its own height.
        // Stacking rather than swapping tops is what keeps it overlap-free when the boxes differ in
        // height - which they do, because a box is sized from what its body scribble draws.
        var top = inAddressOrder.Min(r => boxes[r.Ref].MinY);
        foreach (var region in inAddressOrder)
        {
          var box = boxes[region.Ref];
          var height = box.MaxY - box.MinY;   // read BEFORE either bound moves
          box.MinY = top;
          box.MaxY = top + height;
          top += height + VerticalGap;
        }
      }
    }

    /// <summary>The number in an address (A1 -> 1, A10 -> 10). Sorting the KEY as text would put A10
    /// before A2, which is the same class of defect one level down.
    ///
    /// Delegates to <see cref="ScaffoldAddress"/> (WP-4 Stage A): the address's meaning now has FOUR
    /// consumers - this re-stack, the reader that parses it back off a group name, the producer's
    /// dependsOn translation, and WP-4's build - and two definitions of it disagreeing is the #248 class
    /// with nothing to notice.</summary>
    private static int AddressIndex(ScaffoldRegion region) =>
      ScaffoldAddress.SortKey(region.Key).Number;

    /// <summary>
    /// A deterministic identity per region, derived from its ADDRESS.
    ///
    /// The engine keys every ordering tie-break off this (the WP-A lesson: ordering on a positional id
    /// made re-runs cycle between arrangements). A scaffold region has no InstanceGuid because it is not
    /// an object yet, so one is derived from the address - stable, so the same plan lays out identically
    /// every time it is computed.
    /// </summary>
    private static Guid StableKey(string key)
    {
      using (var md5 = MD5.Create())
        return new Guid(md5.ComputeHash(Encoding.UTF8.GetBytes("dwight-scaffold:" + (key ?? ""))));
    }

    /// <summary>
    /// Where the whole map goes: to the RIGHT of everything already on the canvas, and top-aligned with
    /// it. The map is additive and must not land on the user's work (#221) - overlap is the layout's
    /// problem WITHIN the map, and clearance is its problem OUTSIDE it.
    /// </summary>
    private static PointF ClearOfExistingWork(GH_Document d, IEnumerable<ClusterLayout.ClusterBox> boxes)
    {
      var minX = boxes.Min(b => b.MinX);
      var minY = boxes.Min(b => b.MinY);

      float rightEdge = 0, topEdge = 0;
      try
      {
        var bounds = d.Objects.Where(o => o?.Attributes != null).Select(o => o.Attributes.Bounds).ToList();
        if (bounds.Count > 0)
        {
          rightEdge = bounds.Max(b => b.Right) + ClearanceX;
          topEdge = bounds.Min(b => b.Top);
        }
      }
      catch { }

      return new PointF(rightEdge - (float)minX, topEdge - (float)minY);
    }

    /// <summary>
    /// The verdict - and, per the standing rule, what it does NOT prove (#193a/#206).
    ///
    /// It proves the map landed and that nothing about the definition's computation changed. It says
    /// nothing about whether the DECOMPOSITION is a good one: that is a judgement the human makes by
    /// reading the map, which is the entire reason the map exists.
    /// </summary>
    private static JsonObject Verdict(bool ok, List<ScaffoldRegion> placed, List<string> unplaceable, ScaffoldPlan plan)
    {
      // #355: the assigned half - the join key and the address-space dependsOn - is built by the GH-free
      // function the READER is tested against, so the producer and the consumer of that join cannot drift
      // apart. The tested path is the product path.
      var regions = ScaffoldMapRecord.AssignedRegions(placed, plan.Regions);

      var verdict = new JsonObject
      {
        ["applied"] = ok,
        ["preserved"] = ok,
        ["regions"] = regions,
        ["undo"] = ok ? "one Ctrl-Z removes the whole map" : "nothing to undo - the canvas is unchanged",
        ["proves"] = "The map was laid down as specified, and the definition computes exactly what it " +
                     "computed before - a scaffold adds only groups and scribbles, which are not part of " +
                     "the computation.",
        ["doesNotProve"] = "That this is a GOOD decomposition, or that the regions are the right ones. " +
                           "Nothing has been built yet; the map is a plan for the user to read and work from.",
      };

      if (unplaceable.Count > 0)
        verdict["unplaceable"] = new JsonArray(unplaceable.Select(n => (JsonNode)n).ToArray());
      if (plan.ReversedEdges.Count > 0)
        verdict["reversedForReadingOrder"] = new JsonArray(plan.ReversedEdges.Select(e => (JsonNode)e).ToArray());
      if (plan.DroppedAnchors.Count > 0)
        verdict["droppedAnchors"] = new JsonArray(plan.DroppedAnchors.Select(a => (JsonNode)a).ToArray());

      return verdict;
    }

    private static void UndoOnce(GH_Document doc)
    {
      Dwight.Session.ChangeLog.Reverting(doc);   // Brief AP: never offered for redo
      try { doc.Undo(); } catch { }
      try { Instances.RedrawCanvas(); } catch { }
    }
  }
}
