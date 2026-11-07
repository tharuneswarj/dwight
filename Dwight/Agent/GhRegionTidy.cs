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
using Dwight.Layout;

namespace Dwight.Agent
{
  /// <summary>What one tidy pass did, for the summary the user reads.</summary>
  internal sealed class TidyOutcome
  {
    public bool Ran;
    public int Moved;
    public int RegionsTidied;
    public int ScribblesCleared;
    public string Note = "";
    /// <summary>The regions moved apart, a count (the amendment to #369).</summary>
    public int RegionsMoved;
    /// <summary>#369: a built region that still overlaps a neighbour after the tidy: something it may not
    /// move, or no clear place. Stated, never left unsaid.</summary>
    public string Crowding;
  }

  /// <summary>
  /// AFTER THE BUILD: lay the built regions out, and stop their scribbles lying (WP-4 Stage D; #238/#239).
  ///
  /// WHY IT RUNS AT ALL. A build places components in a plain grid inside a box that was sized for TEXT -
  /// #239 ruled the map "reserves nothing, it READS" - so the region needs a real layout once it holds
  /// real work. And a built region whose canvas still describes the PLAN is a stale claim (#238): the
  /// honest-surface law turned around, where the canvas must not carry a description its own contents
  /// contradict.
  ///
  /// WHEN IT RUNS: ONCE PER RUN, as the run's last step, in its OWN undo record (Q1). Per-region would
  /// churn the canvas three times for one instruction; one record at the end means each Ctrl-Z undoes
  /// exactly one thing, and a separate record lets the user keep the build and drop the tidy.
  ///
  /// WHAT IT MOVES, AND THE ONE THING IT WILL NOT DO. It lays out the MEMBERS of the regions this run
  /// built, and it moves no group and no other region. Re-packing the map would move regions the user
  /// never asked about - possibly ones holding their own work - which is exactly the unasked widening
  /// #338 is about, in geometry. If a grown region now overlaps a neighbour, that is REPORTED rather than
  /// silently fixed; Reorganise is the deliberate, consented, guarded way to re-space a canvas.
  ///
  /// AMENDED BESIDE THAT SENTENCE (31 October 2026; the reason above stands, and this is narrower than a
  /// re-pack). It may also move the regions this card approved, among themselves, and empty regions of a
  /// plugin-laid map that hold nothing yet. It never moves a region off this card that holds components,
  /// or anything the user has placed or moved: that stands still, what grew moves around it if it can, and
  /// if it cannot the overlap is reported as before, and the report is true. See <see cref="RegionSpacing"/>.
  ///
  /// AND IT CARRIES ITS OWN GUARD (#353), because neither inherited instrument can see a move: the moved
  /// set must equal the set declared BEFORE the move from the engine's own targets, or the whole tidy is
  /// reverted. See <see cref="RegionTidy"/>.
  /// </summary>
  internal static class GhRegionTidy
  {
    private const string UndoName = "Dwight: tidy built regions";
    private const string TidyDone = "Tidied the built regions.";

    /// <summary>
    /// Tidy the regions this run built. <paramref name="outcomes"/> maps a region address to how its build
    /// ended, which decides both whether it is laid out and what happens to its body scribble.
    /// </summary>
    public static TidyOutcome Run(GH_Document doc, IReadOnlyDictionary<string, RegionOutcome> outcomes,
                                  IReadOnlyDictionary<string, int> attempts, BuildLedger ledger)
    {
      var result = new TidyOutcome();
      if (doc == null || outcomes == null || outcomes.Count == 0) return result;

      var actions = new List<IGH_UndoAction>();
      var recorded = false;

      try
      {
        // ---- 1. every pivot BEFORE, over the WHOLE document ---------------------------------------
        //
        // The whole document, not only what we are about to touch: the guard exists to catch something
        // moving that the tidy never named, and a snapshot of only our own targets could not see that.
        // Read after the regions' layout is forced, so a box and a pivot are read from one state.
        ForceRegionLayout(doc);
        var before = Pivots(doc);

        // ---- 2. the targets, and therefore the DECLARATION, computed before anything is written ----
        var targets = new Dictionary<Guid, TidyPoint>();
        var tidied = 0;
        foreach (var address in outcomes.Keys.OrderBy(a => ScaffoldAddress.SortKey(a).Letters, StringComparer.Ordinal)
                                             .ThenBy(a => ScaffoldAddress.SortKey(a).Number))
        {
          if (outcomes[address] != RegionOutcome.Built && outcomes[address] != RegionOutcome.BuiltWithErrors) continue;
          if (!GhScaffoldMap.TryFindGroup(doc, address, out var group, out _, out _)) continue;

          // Brief DK: a spot is laid out once, as it lands (SpotLayout), so a rebuild leaves what it kept where it was. Arranging its members here would move them.
          if (ScaffoldAddress.IsSpot(address)) continue;

          var members = GhScaffoldMap.ComponentsIn(doc, group);
          if (members.Count < 2) continue;   // nothing to arrange

          foreach (var kv in LayoutFor(doc, members, ledger?.LastBuilt(address))) targets[kv.Key] = kv.Value;
          tidied++;
        }

        // The regions that grew into each other, moved apart in the same record, under the same declaration.
        var regionsMoved = Respace(doc, outcomes, ledger, before, targets);

        var declared = RegionTidy.Declare(before, targets);
        if (declared.Count == 0)
        {
          result.Ran = true;
          result.Note = "the built regions were already laid out";
          result.Crowding = CrowdingNote(doc, outcomes.Keys, false);
          // The scribbles still need attention even when nothing moved.
          result.ScribblesCleared = Scribbles(doc, outcomes, attempts, actions);
          if (actions.Count > 0) { Dwight.Session.ChangeLog.NoteChat(doc, doc.UndoUtil.RecordEvent(UndoName, actions), UndoName, TidyDone); recorded = true; }
          try { Instances.RedrawCanvas(); } catch { }
          return result;
        }

        // ---- 3. move, undo-first -------------------------------------------------------------------
        foreach (var guid in declared)
        {
          var obj = doc.FindObject(guid, false);
          if (obj?.Attributes == null) continue;
          actions.Add(new GH_PivotAction(obj));
          var t = targets[guid];
          obj.Attributes.Pivot = new PointF((float)t.X, (float)t.Y);
          try { obj.Attributes.ExpireLayout(); } catch { }
        }

        result.ScribblesCleared = Scribbles(doc, outcomes, attempts, actions);

        Dwight.Session.ChangeLog.NoteChat(doc, doc.UndoUtil.RecordEvent(UndoName, actions), UndoName, TidyDone);   // LAST statement before the check: one Ctrl-Z, or none; Brief AP keeps the id
        recorded = true;

        // ---- 4. THE GUARD: exactly what was declared, or revert ------------------------------------
        var check = RegionTidy.Check(before, Pivots(doc), declared);
        if (!check.Ok)
        {
          UndoOnce(doc);
          result.Ran = true;
          result.Note = check.Message;
          result.Moved = 0;
          result.ScribblesCleared = 0;
          result.Crowding = CrowdingNote(doc, outcomes.Keys, false);
          return result;
        }

        try { Instances.RedrawCanvas(); } catch { }

        result.Ran = true;
        result.Moved = declared.Count(g => !(doc.FindObject(g, false) is GH_Scribble));
        result.RegionsTidied = tidied;
        result.RegionsMoved = regionsMoved;
        NoteScribbles(doc, declared);   // only what this tidy moved: noting the rest would launder a move the user made
        result.Crowding = CrowdingNote(doc, outcomes.Keys, regionsMoved > 0);
        return result;
      }
      catch (Exception ex)
      {
        // Fail safe (ADR-029). A tidy is cosmetic; it must never cost the build it follows.
        if (!recorded) CanvasWriter.RollbackOrRecord(doc, actions, UndoName);
        else UndoOnce(doc);
        result.Ran = true;
        result.Note = "the layout pass could not complete (" + ex.Message + ") and was reverted";
        return result;
      }
    }

    /// <summary>
    /// The re-space (the amendment to #369): read every box as it WILL be, ask <see cref="RegionSpacing"/>, and add the moves to the targets so the one
    /// declaration holds them all (#353). It writes nothing. A failure here costs the re-space and never the tidy: the overlap is then reported as it stands.
    /// </summary>
    private static int Respace(GH_Document doc, IReadOnlyDictionary<string, RegionOutcome> outcomes, BuildLedger ledger,
      IReadOnlyDictionary<Guid, TidyPoint> before, Dictionary<Guid, TidyPoint> targets)
    {
      try
      {
        var regions = doc.Objects.OfType<GH_Group>()
          .Select(g => ScaffoldAddress.FromGroupName(g.NickName, out var a, out _) ? (Address: a, Group: g) : (Address: null, Group: g))
          .Where(r => r.Address != null).ToList();
        if (regions.Count < 1) return 0;

        var theirMembers = new HashSet<Guid>(regions.SelectMany(r => r.Group.ObjectIDs ?? new List<Guid>()));
        var theirGroups = new HashSet<Guid>(regions.Select(r => r.Group.InstanceGuid));
        var places = Dwight.Session.CommandHistory.RegionPlaces(doc);
        var boxes = new List<SpaceBox>();
        var moving = new Dictionary<string, List<Guid>>(StringComparer.Ordinal);

        foreach (var r in regions)
        {
          var components = GhScaffoldMap.ComponentsIn(doc, r.Group);
          var scribbles = (r.Group.ObjectIDs ?? new List<Guid>()).Select(id => doc.FindObject(id, false)).OfType<GH_Scribble>().ToList();
          var onCard = outcomes.TryGetValue(r.Address, out var o) && (o == RegionOutcome.Built || o == RegionOutcome.BuiltWithErrors);
          var going = onCard && RegionTidy.ForRegion(o) == ScribbleAction.Remove ? BodyOf(r.Group, scribbles) : null;
          var staying = scribbles.Where(s => s != going).Select(s => s.InstanceGuid).ToList();

          var role = RegionSpacing.RoleOf(
            onCard,
            holdsComponents: components.Count > 0,
            onlyWhatThisBuildPlaced: onCard && GhBuildRegion.HoldsOnlyWhatDwightLeft(doc, ledger, r.Address),
            atThePluginsPlace: scribbles.All(s => Dwight.Session.RegionPlacesCore.IsAt(places, s.InstanceGuid, s.Attributes.Pivot.X, s.Attributes.Pivot.Y)),
            isMapRegion: ScaffoldAddress.IsAddress(r.Address),
            duplicated: regions.Count(x => x.Address == r.Address) > 1);

          var box = BoxAfterTidy(doc, r.Group, r.Address, components, staying, targets);
          boxes.Add(new SpaceBox(r.Address, box.X, box.Y, box.W, box.H, role));
          if (role != SpaceRole.Fixed) moving[r.Address] = components.Concat(staying).ToList();
        }

        // Everything else on the canvas stands. A member of a region is that region's box, and a region's own group is not an obstacle to itself.
        boxes.AddRange(CanvasBoxes(doc, new HashSet<Guid>(theirGroups.Concat(theirMembers))));

        var plan = RegionSpacing.Plan(boxes);
        foreach (var move in plan.Moves) RegionSpacing.Shift(targets, before, moving[move.Key], move.Value);
        return plan.Moves.Count;
      }
      catch { return 0; }
    }

    /// <summary>THE ONE MEASUREMENT OF WHAT IS ON THE CANVAS: every object's box as it was last laid out, with an empty box left out. The re-space and a landing both read this (Brief CM).</summary>
    internal static List<SpaceBox> CanvasBoxes(GH_Document doc, ISet<Guid> except = null)
    {
      var boxes = new List<SpaceBox>();
      foreach (var other in doc.Objects)
      {
        if (other?.Attributes == null || (except != null && except.Contains(other.InstanceGuid))) continue;
        var b = other.Attributes.Bounds;
        if (b.Width > 0 && b.Height > 0) boxes.Add(new SpaceBox("", b.X, b.Y, b.Width, b.Height, SpaceRole.Fixed));
      }
      return boxes;
    }

    /// <summary>The boxes a landing reads, with the regions' layout forced first: their boxes are only read when a canvas draws, and a build may be about to run before one has.</summary>
    internal static List<SpaceBox> MeasuredCanvas(GH_Document doc, ISet<Guid> except = null)
    {
      ForceRegionLayout(doc);
      return CanvasBoxes(doc, except);
    }

    /// <summary>
    /// WHERE A RUN'S REGIONS ENDED UP, read after the tidy because the tidy moves them, so a place stated any earlier could be false (Brief CM). Null when nothing stands. A failure
    /// costs the sentence and the frame, never the build.
    /// </summary>
    internal static RunLanding Landings(GH_Document doc, IReadOnlyDictionary<string, RegionOutcome> outcomes)
    {
      try
      {
        ForceRegionLayout(doc);
        var built = new List<(string Address, GH_Group Group)>();
        foreach (var address in outcomes.Keys.OrderBy(a => ScaffoldAddress.SortKey(a).Letters, StringComparer.Ordinal).ThenBy(a => ScaffoldAddress.SortKey(a).Number))
        {
          if (outcomes[address] != RegionOutcome.Built && outcomes[address] != RegionOutcome.BuiltWithErrors) continue;
          if (GhScaffoldMap.TryFindGroup(doc, address, out var group, out _, out _)) built.Add((address, group));
        }
        if (built.Count == 0) return null;

        var theirs = new HashSet<Guid>(built.SelectMany(b => (b.Group.ObjectIDs ?? new List<Guid>()).Concat(new[] { b.Group.InstanceGuid })));
        var canvas = CanvasBoxes(doc, theirs);
        var view = GhBuildRegion.ViewOf(doc);
        var line = Landing.RunLine(built.Select(b =>
        {
          var r = b.Group.Attributes.Bounds;
          return (b.Address, Landing.Where(new SpaceBox("", r.X, r.Y, r.Width, r.Height, SpaceRole.Fixed), canvas, view));
        }));
        return new RunLanding { Line = line, Frame = built.Select(b => b.Group.InstanceGuid).ToList() };
      }
      catch { return null; }
    }

    /// <summary>The sentence for a run's regions, and what the view frames.</summary>
    internal sealed class RunLanding
    {
      public string Line;
      public List<Guid> Frame;
    }

    /// <summary>A region's box with the tidy's moves applied and a body that is about to go left out, measured against the group as it stands.</summary>
    private static RegionTidy.TidyBox BoxAfterTidy(GH_Document doc, GH_Group group, string address, IReadOnlyList<Guid> components, IReadOnlyList<Guid> staying,
      IReadOnlyDictionary<Guid, TidyPoint> targets)
    {
      var b = group.Attributes.Bounds;
      var now = new RegionTidy.TidyBox(address, b.X, b.Y, b.Width, b.Height);

      RectangleF? membersNow = null, membersAfter = null;
      foreach (var id in group.ObjectIDs ?? new List<Guid>())
      {
        var o = doc.FindObject(id, false);
        if (o?.Attributes == null || o is GH_Group) continue;
        var m = o.Attributes.Bounds;
        membersNow = membersNow.HasValue ? RectangleF.Union(membersNow.Value, m) : m;
        if (!components.Contains(id) && !staying.Contains(id)) continue;
        if (targets.TryGetValue(id, out var t)) m.Offset((float)t.X - o.Attributes.Pivot.X, (float)t.Y - o.Attributes.Pivot.Y);
        membersAfter = membersAfter.HasValue ? RectangleF.Union(membersAfter.Value, m) : m;
      }
      if (!membersNow.HasValue || !membersAfter.HasValue) return now;

      return RegionSpacing.PredictBox(now, Box(address, membersNow.Value), Box(address, membersAfter.Value));
    }

    private static RegionTidy.TidyBox Box(string address, RectangleF r) => new RegionTidy.TidyBox(address, r.X, r.Y, r.Width, r.Height);

    /// <summary>A group's box and its members' are read when a canvas draws, and this runs before one has. Forced here for the regions only, so what the
    /// re-space and the crowding sentence read is where things are. A failure leaves the box as last drawn.</summary>
    private static void ForceRegionLayout(GH_Document doc)
    {
      foreach (var g in doc.Objects.OfType<GH_Group>().ToList())
      {
        if (!ScaffoldAddress.FromGroupName(g.NickName, out _, out _)) continue;
        try
        {
          foreach (var id in g.ObjectIDs ?? new List<Guid>())
          {
            var o = doc.FindObject(id, false);
            if (o?.Attributes == null) continue;
            o.Attributes.ExpireLayout();
            o.Attributes.PerformLayout();
          }
          g.Attributes.ExpireLayout();
          g.Attributes.PerformLayout();
        }
        catch { }
      }
    }

    /// <summary>
    /// NOTE WHERE THE PLUGIN LAID THESE REGIONS, the only witness of whether the user has moved one since (<see cref="Dwight.Session.RegionPlacesCore"/>). Called when a
    /// map lands. Only scribbles: a region's are there from the map's first moment, and a component is judged by the ledger.
    /// </summary>
    internal static void NotePlaces(GH_Document doc, IEnumerable<Guid> groups)
    {
      try
      {
        var scribbles = new List<Guid>();
        foreach (var id in groups)
          if (doc.FindObject(id, false) is GH_Group g && ScaffoldAddress.FromGroupName(g.NickName, out _, out _))
            scribbles.AddRange(g.ObjectIDs ?? new List<Guid>());
        NoteScribbles(doc, scribbles);
      }
      catch { }
    }

    /// <summary>The scribbles among these ids, each read with its layout forced, so a pivot here is the pivot <see cref="ForceRegionLayout"/> gives the re-space to compare.</summary>
    private static void NoteScribbles(GH_Document doc, IEnumerable<Guid> ids)
    {
      try
      {
        var notes = new List<(Guid, double, double)>();
        foreach (var s in ids.Select(id => doc.FindObject(id, false)).OfType<GH_Scribble>())
        {
          try { s.Attributes.ExpireLayout(); s.Attributes.PerformLayout(); } catch { }
          notes.Add((s.InstanceGuid, s.Attributes.Pivot.X, s.Attributes.Pivot.Y));
        }
        if (notes.Count > 0) Dwight.Session.CommandHistory.NoteRegionPlaces(doc, notes);
      }
      catch { }
    }

    /// <summary>
    /// One region's members, laid out by the SAME engine a definition uses (ADR-084, #95 - the engine
    /// exists and is proven; a second arrangement scheme would be a fork of it), anchored so the block
    /// starts where the members already are rather than jumping across the canvas.
    /// </summary>
    private static Dictionary<Guid, TidyPoint> LayoutFor(GH_Document doc, IReadOnlyList<Guid> members, IReadOnlyCollection<Guid> lastBuilt)
    {
      var placed = new Dictionary<Guid, TidyPoint>();
      var objects = members.Select(g => doc.FindObject(g, false)).Where(o => o?.Attributes != null).ToList();
      if (objects.Count < 2) return placed;

      var idByGuid = new Dictionary<Guid, int>();
      var nodes = new List<LayoutNode>();
      for (var i = 0; i < objects.Count; i++)
      {
        var o = objects[i];
        idByGuid[o.InstanceGuid] = i;
        var b = o.Attributes.Bounds;
        nodes.Add(new LayoutNode(i, o.InstanceGuid, Math.Max(20, b.Width), Math.Max(20, b.Height)));
      }

      // The wires BETWEEN these members - what makes the layout a flow rather than a grid.
      var edges = new List<LayoutEdge>();
      foreach (var o in objects)
      {
        var inputs = o is IGH_Component c ? (IEnumerable<IGH_Param>)c.Params.Input
                   : o is IGH_Param p ? new[] { p } : Array.Empty<IGH_Param>();
        foreach (var ip in inputs)
          foreach (var src in ip?.Sources ?? new List<IGH_Param>())
          {
            var owner = src?.Attributes?.GetTopLevel?.DocObject;
            if (owner == null) continue;
            if (idByGuid.TryGetValue(owner.InstanceGuid, out var from) && idByGuid.TryGetValue(o.InstanceGuid, out var to))
              edges.Add(new LayoutEdge(from, to));
          }
      }

      var centres = LayoutEngine.Msagl().Layout(nodes, edges, LayoutOptions.Default).Centers;
      if (centres.Count == 0) return placed;

      // ANCHOR: the laid-out block keeps the top-left the members already had, so a tidy ARRANGES INSIDE a
      // region and can never relocate it. The guard cannot express that - it verifies WHICH objects moved,
      // never WHERE TO - so the property is arithmetic, and it lives GH-free in RegionTidy.Anchor where it
      // is asserted rather than reviewed (#300: the tested path is the product path).
      var byGuid = centres.ToDictionary(kv => objects[kv.Key].InstanceGuid,
                                        kv => new TidyPoint(kv.Value.X, kv.Value.Y));
      var current = objects.ToDictionary(o => o.InstanceGuid,
                                         o => new TidyPoint(o.Attributes.Pivot.X, o.Attributes.Pivot.Y));

      foreach (var kv in RegionTidy.Anchor(byGuid, current)) placed[kv.Key] = kv.Value;
      IntoColumns(objects, placed, lastBuilt);
      return placed;
    }

    /// <summary>
    /// Brief DP: the flow keeps the middle, and the inputs and outputs stand either side of it in columns, read from the live wiring. Worked in true bounds, because a target here is a pivot and a pivot is not a corner, and moved
    /// back onto the pivots as a difference, so each keeps its own distance from its bounds. A failure costs the columns and never the tidy: the flow stands.
    /// </summary>
    private static void IntoColumns(IReadOnlyList<IGH_DocumentObject> objects, Dictionary<Guid, TidyPoint> placed, IReadOnlyCollection<Guid> lastBuilt)
    {
      try
      {
        var members = objects.Where(o => placed.ContainsKey(o.InstanceGuid)).ToList();
        var block = new HashSet<Guid>(objects.Select(o => o.InstanceGuid));
        var roles = GhBlockRoles.Read(members, block);
        var flow = members.Select(o =>
        {
          var b = o.Attributes.Bounds;
          var t = placed[o.InstanceGuid];
          return new SpaceBox("", b.X + t.X - o.Attributes.Pivot.X, b.Y + t.Y - o.Attributes.Pivot.Y, b.Width, b.Height, SpaceRole.Fixed);
        }).ToList();
        var rebuilt = lastBuilt != null && lastBuilt.Count > 0;
        var keptAtY = members.Select(o => rebuilt && !lastBuilt.Contains(o.InstanceGuid) ? (double?)o.Attributes.Bounds.Y : null).ToList();

        var moved = BlockColumns.AroundFlow(flow, roles, keptAtY, GhBlockRoles.Wires(members));
        for (var i = 0; i < members.Count; i++)
        {
          var t = placed[members[i].InstanceGuid];
          placed[members[i].InstanceGuid] = new TidyPoint(t.X + moved[i].X - flow[i].X, t.Y + moved[i].Y - flow[i].Y);
        }
      }
      catch { }
    }

    /// <summary>
    /// The body scribbles (#238). The HEADER always stays - it carries the address and it is how a region
    /// is found again - so only the body is touched, and it is identified EXACTLY: the group's scribble
    /// whose text is not the group's own name. If that cannot be resolved to exactly one, nothing is
    /// touched (#208: exactly, or leave it alone - a wrongly-deleted scribble is the user's map).
    /// </summary>
    private static int Scribbles(GH_Document doc, IReadOnlyDictionary<string, RegionOutcome> outcomes,
                                 IReadOnlyDictionary<string, int> attempts, List<IGH_UndoAction> actions)
    {
      var touched = 0;
      foreach (var kv in outcomes)
      {
        var action = RegionTidy.ForRegion(kv.Value);
        if (action == ScribbleAction.Keep) continue;
        if (!GhScaffoldMap.TryFindGroup(doc, kv.Key, out var group, out _, out _)) continue;

        GH_Scribble body;
        try { body = BodyOf(group, (group.ObjectIDs ?? new List<Guid>()).Select(id => doc.FindObject(id, false)).OfType<GH_Scribble>()); }
        catch { continue; }
        if (body == null) continue;   // ambiguous or absent: leave the user's map alone

        if (action == ScribbleAction.Remove)
        {
          actions.Add(new GH_RemoveObjectAction(body));
          doc.RemoveObject(body, false);
        }
        else
        {
          actions.Add(new GH_GenericObjectAction(body));
          var count = attempts != null && attempts.TryGetValue(kv.Key, out var n) ? n : 1;
          try { body.Text = RegionTidy.StoppedText(kv.Key, count); } catch { continue; }
        }
        touched++;
      }
      return touched;
    }

    /// <summary>The region's one body scribble: the scribble whose text is not the group's own name. Exactly one, or null (#208).</summary>
    private static GH_Scribble BodyOf(GH_Group group, IEnumerable<GH_Scribble> scribbles)
    {
      var header = (group.NickName ?? "").Trim();
      var bodies = scribbles.Where(s => !string.Equals((s.Text ?? "").Trim(), header, StringComparison.Ordinal)).ToList();
      return bodies.Count == 1 ? bodies[0] : null;
    }

    /// <summary>
    /// After the tidy: does a region this run built still overlap something (#369)?
    ///
    /// DETECT AND SAY what the re-space may not fix. A group's box is derived from its MEMBERS, so a region
    /// sized for text grows when it is built - #239 named exactly this. Stating a fact about the canvas needs
    /// no consent; moving their work does.
    ///
    /// Read AFTER the move, with the layout forced, because the boxes as they now stand are the whole point. It moves nothing.
    /// Brief CL: only objects are read, never a group's own box (<see cref="RegionTidy.CrowdingBoxes"/>).
    /// </summary>
    private static string CrowdingNote(GH_Document doc, IEnumerable<string> builtAddresses, bool respaced) =>
      OverlapSentence(doc, builtAddresses, null, null, respaced);

    /// <summary>
    /// Brief CL: the same measure for a spot, which has no tidy, so its overlap went unsaid on the first real files. What the ledger records placing there is the spot's, with a group or without one.
    /// </summary>
    internal static string SpotOverlapNote(GH_Document doc, string spot, BuildLedger ledger) =>
      OverlapSentence(doc, new[] { spot }, spot, ledger?.PlacedAt(spot), false);

    /// <summary>Every object on the canvas, read once with its owner, and the sentence from <see cref="RegionTidy"/>. Null when nothing overlaps, or if the read fails: a notice must never cost the build it follows.</summary>
    private static string OverlapSentence(GH_Document doc, IEnumerable<string> builtAddresses, string spot, IReadOnlyList<Guid> placed, bool respaced)
    {
      try
      {
        ForceRegionLayout(doc);
        var regions = new List<(string Address, IReadOnlyList<RegionTidy.TidyBox> Members)>();
        var owned = new HashSet<Guid>();   // first owner wins: one object in two groups must not read as overlapping itself

        if (placed != null)
        {
          var members = new List<RegionTidy.TidyBox>();
          foreach (var id in placed)
          {
            var o = doc.FindObject(id, false);
            if (o?.Attributes == null || !owned.Add(id)) continue;
            o.Attributes.ExpireLayout();
            o.Attributes.PerformLayout();
            members.Add(Box(spot, o.Attributes.Bounds));
          }
          regions.Add((spot, members));
        }

        foreach (var g in doc.Objects.OfType<GH_Group>())
        {
          if (!ScaffoldAddress.FromGroupName(g.NickName, out var address, out _)) continue;
          var members = new List<RegionTidy.TidyBox>();
          foreach (var id in g.ObjectIDs ?? new List<Guid>())
          {
            var member = doc.FindObject(id, false);
            if (member?.Attributes == null || member is GH_Group || !owned.Add(id)) continue;
            members.Add(Box(address, member.Attributes.Bounds));
          }
          regions.Add((address, members));
        }

        var others = new List<(RegionTidy.TidyBox Box, bool IsFrame)>();
        foreach (var o in doc.Objects)
        {
          if (o?.Attributes == null || owned.Contains(o.InstanceGuid)) continue;
          var b = o.Attributes.Bounds;
          others.Add((new RegionTidy.TidyBox("", b.X, b.Y, b.Width, b.Height, LivePorts.PortLabel(o.Name, o.NickName)), o is GH_Group || o is GH_Markup));
        }

        var built = new HashSet<string>(builtAddresses ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
        return RegionTidy.CrowdingText(RegionTidy.Crowding(RegionTidy.CrowdingBoxes(regions, others), built), respaced);
      }
      catch { return null; }
    }

    private static Dictionary<Guid, TidyPoint> Pivots(GH_Document doc)
    {
      var map = new Dictionary<Guid, TidyPoint>();
      try
      {
        foreach (var o in doc.Objects)
        {
          if (o?.Attributes == null) continue;
          map[o.InstanceGuid] = new TidyPoint(o.Attributes.Pivot.X, o.Attributes.Pivot.Y);
        }
      }
      catch { }
      return map;
    }

    private static void UndoOnce(GH_Document doc)
    {
      Dwight.Session.ChangeLog.Reverting(doc);   // Brief AP: never offered for redo
      try { doc.Undo(); } catch { }
      try { Instances.RedrawCanvas(); } catch { }
    }
  }
}
