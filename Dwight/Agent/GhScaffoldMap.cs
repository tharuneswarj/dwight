using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;

namespace Dwight.Agent
{
  /// <summary>
  /// <c>get_scaffold_map</c> - the map, as it reads right now (WP-4 Stage A).
  ///
  /// WHY IT EXISTS. WP-3 persisted the map to <c>StoredThread.ScaffoldMap</c> and nothing ever read it:
  /// a witness captured and not consulted. The agent could not see the map it had just laid down, because
  /// a turn is seeded from CHAT HISTORY and never from tool traffic (#223), and the map's summary message
  /// is plugin-authored chrome, excluded from the model transcript by construction (#228). WP-4 builds by
  /// address, so this is the tool that makes an address mean something to the model.
  ///
  /// THE CANVAS IS THE AUTHORITATIVE HALF (#137/#221b). Which regions EXIST is read from the GROUPS - a
  /// scaffold region's group carries its address in its name precisely so the map survives the loss of its
  /// record. The stored plan supplies only what the canvas cannot know: the purpose, the pseudo-code, the
  /// declared boundaries, the anchors. So a lost, trimmed or too-old record costs the PLAN and never the
  /// MAP, and the difference is stated rather than papered over.
  ///
  /// The join and every refusal live in <see cref="ScaffoldMapRecord.Read"/>, GH-free and tested
  /// (ADR-091); this shim only reads groups.
  /// </summary>
  internal static class GhScaffoldMap
  {
    /// <summary>Regions reported in one call. A scaffold holds at most 24 by construction
    /// (<c>ScaffoldPlanParser.MaxRegions</c>), so this bounds a canvas carrying several maps rather than
    /// one oversized map - and when it bites it SAYS so (#134: no silent caps).</summary>
    public const int MaxRegions = 48;

    /// <summary>The scaffold regions the CANVAS carries, for a caller that needs the addresses rather
    /// than the whole map payload. One enumeration of what a scaffold group is, shared by the tool, the
    /// consent card's validation, and the build's scope check - so "which regions exist" cannot come to
    /// mean two different things (#248).</summary>
    public static List<CanvasRegion> CanvasRegions(GH_Document doc) => ReadCanvas(doc, out _);

    public static ToolResult Run(GH_Document doc, string storedMapJson)
    {
      if (doc == null) return ToolResult.Fail(ToolErrors.NoDocument, "No Grasshopper document is open.");

      var canvas = ReadCanvas(doc, out var truncated);
      return ToolResult.Success(
        ScaffoldMapPayload.Build(ScaffoldMapRecord.Read(storedMapJson, canvas), truncated, MaxRegions));
    }


    /// <summary>
    /// THE GROUP FOR ONE ADDRESS - exactly one, or nothing (WP-4 Stage B; #208).
    ///
    /// A build is AIMED by this, so it resolves exactly or refuses. Two failures, both refusals:
    ///   - no group carries the address: the caller is told which addresses DO exist, so the refusal
    ///     repairs rather than blocks (ADR-092's shape);
    ///   - TWO groups carry it (a user duplicated a region, or two maps overlap): AMBIGUOUS. Picking the
    ///     first would be a silent fallback beneath a verdict - the exact class #208 exists to close, at
    ///     the exact place it would do the most damage.
    ///
    /// <paramref name="available"/> is filled on failure with the addresses that do resolve, in address
    /// order, so the caller can hand back something actionable.
    /// </summary>
    public static bool TryFindGroup(GH_Document doc, string address, out GH_Group group,
                                    out List<string> available, out bool ambiguous)
    {
      group = null;
      ambiguous = false;
      available = new List<string>();
      if (doc == null || !ScaffoldAddress.IsRegionAddress(address)) return false;

      var matches = new List<GH_Group>();
      try
      {
        foreach (var g in doc.Objects.OfType<GH_Group>())
        {
          if (!ScaffoldAddress.FromGroupName(g.NickName, out var found, out _)) continue;
          available.Add(found);
          if (string.Equals(found, address, StringComparison.Ordinal)) matches.Add(g);
        }
      }
      catch { }

      available = available
        .Distinct(StringComparer.Ordinal)
        .OrderBy(a => ScaffoldAddress.SortKey(a).Letters, StringComparer.Ordinal)
        .ThenBy(a => ScaffoldAddress.SortKey(a).Number)
        .ToList();

      if (matches.Count == 1) { group = matches[0]; return true; }
      ambiguous = matches.Count > 1;
      return false;
    }

    /// <summary>The components a group holds, excluding its own scribbles - what tells a built region
    /// from a planned one, and the number the rebuild decision is made against.</summary>
    public static List<Guid> ComponentsIn(GH_Document doc, GH_Group group)
    {
      var found = new List<Guid>();
      if (doc == null || group == null) return found;
      try
      {
        foreach (var id in group.ObjectIDs ?? new List<Guid>())
        {
          var member = doc.FindObject(id, false);
          if (member == null || member is GH_Group || member is GH_Scribble) continue;
          found.Add(id);
        }
      }
      catch { }
      return found;
    }

    /// <summary>
    /// The canvas half: every GROUP whose name parses as a scaffold address (#221b).
    ///
    /// A group that does not parse is somebody else's group and is left entirely alone - parsing is exact
    /// or nothing (#208), because a build aimed at a mis-read address is a mutation in the wrong place.
    /// The component count excludes the region's own scribbles, which are the map's furniture rather than
    /// its work: counting them would make an empty region look built.
    /// </summary>
    private static List<CanvasRegion> ReadCanvas(GH_Document doc, out int truncated)
    {
      truncated = 0;
      var found = new List<CanvasRegion>();

      try
      {
        foreach (var group in doc.Objects.OfType<GH_Group>())
        {
          if (!ScaffoldAddress.FromGroupName(group.NickName, out var address, out var name)) continue;

          var components = 0;
          try
          {
            foreach (var id in group.ObjectIDs ?? new List<Guid>())
            {
              var member = doc.FindObject(id, false);
              if (member == null || member is GH_Group || member is GH_Scribble) continue;
              components++;
            }
          }
          catch { }

          found.Add(new CanvasRegion { Address = address, Name = name, ComponentCount = components });
        }
      }
      catch { }

      if (found.Count <= MaxRegions) return found;

      // Bounded, and the omission is REPORTED by the caller. Ordered first so what is dropped is the tail
      // of the map rather than an arbitrary slice of it.
      var ordered = found
        .OrderBy(c => ScaffoldAddress.SortKey(c.Address).Letters, StringComparer.Ordinal)
        .ThenBy(c => ScaffoldAddress.SortKey(c.Address).Number)
        .ToList();
      truncated = ordered.Count - MaxRegions;
      return ordered.Take(MaxRegions).ToList();
    }

    private static JsonArray Arr(IEnumerable<string> items) =>
      new JsonArray(items.Select(s => (JsonNode)(s ?? "")).ToArray());
  }
}
