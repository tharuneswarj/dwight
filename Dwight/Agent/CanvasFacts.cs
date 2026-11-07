using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace Dwight.Agent
{
  /// <summary>
  /// WHETHER THE DEFINITION IS SAVED - and nothing else about its file.
  ///
  /// EXACTLY TWO BOOLEANS, AND THAT IS THE PRIVACY BOUND. The architect's ruling allows saved / unsaved and
  /// modified-since-save ONLY: no path and no file name, because a name carries a client's name as easily as a path
  /// does. The Grasshopper read never touches FilePath, DisplayName (which carries the file name), Author or
  /// Properties - and this type has nowhere to put them if it did. A test asserts these are its only two members, so
  /// a later "helpful" third field fails by name rather than shipping.
  /// </summary>
  internal sealed class FileStateFact
  {
    /// <summary>The definition has a file on disk: it was opened from one, or saved at least once
    /// (<c>GH_Document.IsFilePathDefined</c>).</summary>
    public bool Saved { get; set; }

    /// <summary>It has changes since it was last saved (<c>GH_Document.IsModified</c>). Meaningless when it has
    /// never been saved, and not sent then.</summary>
    public bool ModifiedSinceSave { get; set; }
  }

  internal static class FileStateReport
  {
    public const string SavedAndClean = "The definition is saved, and nothing has changed since it was last saved.";
    public const string SavedAndModified = "The definition has changes that have not been saved.";

    /// <summary>Why modifiedSinceSave is ABSENT for a never-saved definition: "modified since a save" has no
    /// referent when there was no save, so sending it - as either value - would be a claim about nothing.</summary>
    public const string NeverSaved =
      "The definition has never been saved, so everything on the canvas is unsaved. Whether it changed since a save " +
      "has no meaning here, so it is not sent.";

    public static ToolResult Build(FileStateFact fact)
    {
      if (fact == null)
        return ToolResult.Fail(ToolErrors.NoDocument, "No Grasshopper document is open.");

      var root = new JsonObject { ["saved"] = fact.Saved };
      if (fact.Saved)
      {
        root["modifiedSinceSave"] = fact.ModifiedSinceSave;
        root["state"] = fact.ModifiedSinceSave ? SavedAndModified : SavedAndClean;
      }
      else
        root["state"] = NeverSaved;
      return ToolResult.Success(root);
    }
  }

  /// <summary>A rectangle on the Grasshopper canvas. Read to decide relations and NEVER SENT (ADR-058): no field of
  /// the layout payload can carry one.</summary>
  internal struct CanvasBox
  {
    public double X0, Y0, X1, Y1;
    public CanvasBox(double x0, double y0, double x1, double y1) { X0 = x0; Y0 = y0; X1 = x1; Y1 = y1; }

    /// <summary>Overlap with positive area. Boxes that only touch along an edge do not overlap.</summary>
    public bool Overlaps(CanvasBox o) => X0 < o.X1 && o.X0 < X1 && Y0 < o.Y1 && o.Y0 < Y1;

    public bool Contains(CanvasBox o) => X0 <= o.X0 && Y0 <= o.Y0 && o.X1 <= X1 && o.Y1 <= Y1;
  }

  /// <summary>One top-level component on the canvas, by its registry id.</summary>
  internal sealed class LayoutComponent
  {
    public int Id { get; set; }
    public CanvasBox Box { get; set; }
  }

  /// <summary>One group, as read. <see cref="Key"/> is its InstanceGuid, for deterministic tie-breaking only - it is
  /// never sent (ADR-058), and determinism never keys off a position (the WP-A rule).</summary>
  internal sealed class LayoutGroup
  {
    public string Key { get; set; }
    public string Name { get; set; }

    /// <summary>The scaffold address, when this is one of Dwight's regions - recognised by
    /// <c>ScaffoldAddress.FromGroupName</c>, the one test get_scaffold_map and build_region use.</summary>
    public string Address { get; set; }
    public CanvasBox Box { get; set; }
    public List<int> Members { get; set; } = new List<int>();

    /// <summary>Members that are not top-level components: scribbles, nested groups, anything without an id.</summary>
    public int OtherObjects { get; set; }
  }

  /// <summary>
  /// U23: THE CANVAS LAYOUT AS FACT - which components overlap, what each group holds, how the groups sit relative
  /// to one another, and which wires run backwards. NO COORDINATES (ADR-058, the architect's ruling): positions are
  /// READ to decide relations, and only the relations leave.
  ///
  /// GROUP NUMBERS ARE LOCAL TO ONE READ. They are positions in this answer's list, NOT ids: no tool takes them,
  /// and the payload says so plainly, because the risk is not that a tool refuses one but that a model tries. A
  /// scaffold region's address is its durable name.
  ///
  /// BACKWARD WIRES are the one fact here about the definition's logic rather than its tidiness: Reorganise's law is
  /// that flow reads left to right (edge-monotonicity), so a wire whose source sits right of its target is a place
  /// the layout contradicts the logic.
  ///
  /// GH-free (ADR-091): boxes in, relations out, every decision tested.
  /// </summary>
  internal static class CanvasLayoutReport
  {
    public const int GroupCap = 30;
    public const int MemberCap = 50;
    public const int PairCap = 25;

    public const string NoteText =
      "Group numbers are positions in THIS answer's list, valid for this read only. They are NOT ids and cannot be " +
      "passed to any tool; a Dwight region's address is its lasting name. Everything here is derived from where " +
      "things sit on the canvas, and no position or size is sent.";

    public const string BackwardText =
      "A backward wire runs from a component that sits entirely to the right of the component it feeds - against the " +
      "left-to-right flow a tidy definition reads in.";

    public static ToolResult Build(IReadOnlyList<LayoutComponent> components, IReadOnlyList<LayoutGroup> groups,
      IReadOnlyList<(int From, int To)> wires)
    {
      components = components ?? Array.Empty<LayoutComponent>();
      groups = groups ?? Array.Empty<LayoutGroup>();
      wires = wires ?? Array.Empty<(int, int)>();

      // READING ORDER: by left edge, then top edge, then the group's own key - a position decides the order, and a
      // guid, never a pivot-derived id, breaks the tie.
      var ordered = groups
        .OrderBy(g => g.Box.X0).ThenBy(g => g.Box.Y0).ThenBy(g => g.Key ?? "", StringComparer.Ordinal)
        .ToList();
      var listed = ordered.Take(GroupCap).ToList();
      var number = new Dictionary<LayoutGroup, int>();
      for (var i = 0; i < listed.Count; i++) number[listed[i]] = i + 1;

      var root = new JsonObject { ["note"] = NoteText };

      var groupArr = new JsonArray();
      foreach (var g in listed)
      {
        var o = new JsonObject { ["n"] = number[g] };
        if (!string.IsNullOrWhiteSpace(g.Name)) o["name"] = g.Name.Trim();
        if (!string.IsNullOrWhiteSpace(g.Address)) o["address"] = g.Address;

        var members = (g.Members ?? new List<int>()).Distinct().OrderBy(id => id).ToList();
        o["members"] = new JsonArray(members.Take(MemberCap).Select(id => (JsonNode)id).ToArray());
        if (members.Count > MemberCap) o["membersOmitted"] = members.Count - MemberCap;
        if (g.OtherObjects > 0) o["otherObjects"] = g.OtherObjects;

        var left = Nearest(g, listed, Side.Left);
        var right = Nearest(g, listed, Side.Right);
        var above = Nearest(g, listed, Side.Above);
        var below = Nearest(g, listed, Side.Below);
        if (left != null) o["left"] = number[left];
        if (right != null) o["right"] = number[right];
        if (above != null) o["above"] = number[above];
        if (below != null) o["below"] = number[below];
        groupArr.Add(o);
      }
      root["groups"] = groupArr;
      if (ordered.Count > listed.Count)
        root["groupsOmitted"] = ordered.Count - listed.Count;

      // UNGROUPED: the components in no group at all.
      var grouped = new HashSet<int>(groups.SelectMany(g => g.Members ?? new List<int>()));
      root["ungrouped"] = components.Count(c => !grouped.Contains(c.Id));

      // OVERLAPS: a component hidden under another. Sort and sweep by left edge - never every pair.
      var overlaps = OverlappingPairs(components);
      root["overlaps"] = Pairs(overlaps.Take(PairCap), "a", "b");
      root["overlapsTotal"] = overlaps.Count;

      // GROUP OVERLAPS: two groups whose boxes cross. A group drawn wholly inside another is NESTING, not a clash,
      // so containment is not reported. Among the listed groups only, so every number here is one in the list.
      var groupOverlaps = new List<(int A, int B)>();
      for (var i = 0; i < listed.Count; i++)
        for (var j = i + 1; j < listed.Count; j++)
        {
          var a = listed[i].Box;
          var b = listed[j].Box;
          if (a.Overlaps(b) && !a.Contains(b) && !b.Contains(a)) groupOverlaps.Add((i + 1, j + 1));
        }
      root["groupOverlaps"] = Pairs(groupOverlaps.Take(PairCap), "a", "b");
      root["groupOverlapsTotal"] = groupOverlaps.Count;

      // BACKWARD WIRES, one per component pair however many wires join them.
      var boxOf = components.GroupBy(c => c.Id).ToDictionary(g => g.Key, g => g.First().Box);
      var backward = wires
        .Where(w => w.From != w.To && boxOf.ContainsKey(w.From) && boxOf.ContainsKey(w.To))
        .Where(w => boxOf[w.From].X0 >= boxOf[w.To].X1)
        .Distinct()
        .OrderBy(w => w.From).ThenBy(w => w.To)
        .ToList();
      root["backwardWires"] = Pairs(backward.Take(PairCap), "from", "to");
      root["backwardWiresTotal"] = backward.Count;
      root["backwardMeans"] = BackwardText;

      return ToolResult.Success(root);
    }

    private enum Side { Left, Right, Above, Below }

    /// <summary>
    /// The nearest group on one side: entirely beyond this group's edge on that side, and sharing some of its span on
    /// the other axis (so "right of" means across from it, not diagonally away). Nearest by gap; a tie goes to the
    /// earlier group in reading order. The canvas's y grows DOWNWARD, so "below" is the larger y.
    /// </summary>
    private static LayoutGroup Nearest(LayoutGroup g, List<LayoutGroup> all, Side side)
    {
      LayoutGroup best = null;
      var bestGap = double.MaxValue;
      var a = g.Box;
      foreach (var other in all)
      {
        if (ReferenceEquals(other, g)) continue;
        var b = other.Box;
        double gap;
        switch (side)
        {
          case Side.Right: if (b.X0 < a.X1 || !SharesY(a, b)) continue; gap = b.X0 - a.X1; break;
          case Side.Left: if (b.X1 > a.X0 || !SharesY(a, b)) continue; gap = a.X0 - b.X1; break;
          case Side.Below: if (b.Y0 < a.Y1 || !SharesX(a, b)) continue; gap = b.Y0 - a.Y1; break;
          default: if (b.Y1 > a.Y0 || !SharesX(a, b)) continue; gap = a.Y0 - b.Y1; break;
        }
        if (gap < bestGap) { bestGap = gap; best = other; }
      }
      return best;
    }

    private static bool SharesY(CanvasBox a, CanvasBox b) => a.Y0 < b.Y1 && b.Y0 < a.Y1;
    private static bool SharesX(CanvasBox a, CanvasBox b) => a.X0 < b.X1 && b.X0 < a.X1;

    /// <summary>Every pair of components whose boxes overlap, as (smaller id, larger id), sorted. A sort-and-sweep
    /// on the left edge, so the cost follows the overlaps rather than the square of the component count.</summary>
    internal static List<(int A, int B)> OverlappingPairs(IReadOnlyList<LayoutComponent> components)
    {
      var byLeft = components.OrderBy(c => c.Box.X0).ThenBy(c => c.Id).ToList();
      var found = new HashSet<(int, int)>();
      var active = new List<LayoutComponent>();
      foreach (var c in byLeft)
      {
        active.RemoveAll(x => x.Box.X1 <= c.Box.X0);
        foreach (var x in active)
          if (x.Id != c.Id && x.Box.Overlaps(c.Box))
            found.Add(x.Id < c.Id ? (x.Id, c.Id) : (c.Id, x.Id));
        active.Add(c);
      }
      return found.OrderBy(p => p.Item1).ThenBy(p => p.Item2).Select(p => (p.Item1, p.Item2)).ToList();
    }

    private static JsonArray Pairs(IEnumerable<(int, int)> pairs, string first, string second) =>
      new JsonArray(pairs.Select(p => (JsonNode)new JsonObject { [first] = p.Item1, [second] = p.Item2 }).ToArray());
  }
}
