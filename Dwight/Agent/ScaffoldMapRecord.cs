using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Dwight.Agent
{
  /// <summary>
  /// THE STORED MAP, AND WHAT THE CONVERSATION SAYS ABOUT IT (Phase-8 WP-3 Stage D).
  ///
  /// A scaffold map has two readers with different needs, and conflating them would serve neither:
  ///
  ///   - WP-4 BUILDS from it, so the stored form is the MODEL'S PLAN (the pseudo-code, the declared
  ///     boundaries, the dependencies - everything it will need) PLUS the ADDRESSES the plugin assigned.
  ///     It lives in <c>StoredThread.ScaffoldMap</c>, outside the message list, so the message caps can
  ///     never trim away the plan (#167).
  ///   - THE USER reads the conversation, so the thread gets a SUMMARY message naming what landed by
  ///     address. That message is what a reopen replays - the map slot is data for a machine, not a
  ///     render source, and rendering from both would be two sources of truth for one artifact.
  ///
  /// GH-free (ADR-091): the stored shape and the summary are decisions, so they are tested.
  /// </summary>
  /// <summary>What the CANVAS says about one scaffold region - the authoritative half. Supplied by the
  /// GH shim, which reads the groups; everything here is a fact about objects that exist.</summary>
  internal sealed class CanvasRegion
  {
    public string Address;
    public string Name;
    /// <summary>Components inside the group, not counting the region's own two scribbles. This is how a
    /// caller tells a region that has been built from one that is still only a plan.</summary>
    public int ComponentCount;
  }

  /// <summary>One region as the map reads RIGHT NOW: the canvas's facts joined to the stored plan.</summary>
  internal sealed class MapRegion
  {
    public string Address;
    public string Name;
    public string Purpose;
    public List<string> Pseudo = new List<string>();
    public List<string> Inputs = new List<string>();
    public List<string> Outputs = new List<string>();
    /// <summary>The regions this one needs, IN ADDRESSES - the plugin's namespace, which is what a build
    /// is aimed by and what the user says out loud.</summary>
    public List<string> DependsOn = new List<string>();
    public List<int> AnchorIds = new List<int>();

    /// <summary>Is there a group for this address on the canvas right now?</summary>
    public bool OnCanvas;
    public int ComponentCount;
    /// <summary>Did the stored record supply this region's plan? False for a canvas group the record does
    /// not know, and for every region of a map too old to join (see <see cref="MapReadResult"/>).</summary>
    public bool PlanKnown;
  }

  internal sealed class MapReadResult
  {
    /// <summary>The stored map's own version, or 0 when there is no readable record.</summary>
    public int MapVersion;
    public List<MapRegion> Regions = new List<MapRegion>();
    /// <summary>Everything the reader could not reconcile, in the user's language. Bounded payloads
    /// report what they omitted (#134/#209) - and a map is exactly where a silent omission would send a
    /// build somewhere wrong.</summary>
    public List<string> Notes = new List<string>();
    /// <summary>Was the plan half joinable at all? False means the regions carry canvas facts only.</summary>
    public bool PlanAvailable;
  }

  internal static class ScaffoldMapRecord
  {
    /// <summary>
    /// The stored map's format version.
    ///
    /// v2 is the JOINABLE one. v1 wrote the plan half keyed by the model's <c>ref</c> and the assigned
    /// half keyed by ADDRESS with no ref at all - so getting from "A1" to A1's pseudo-code had no exact
    /// route, and its <c>dependsOn</c> held refs while its <c>key</c> held addresses, meaning the assigned
    /// half expressed its dependency graph in a namespace it could not resolve. WP-4 builds from this
    /// record; a guessed join is a build aimed at the wrong region (#355).
    /// </summary>
    public const int CurrentMapVersion = 2;

    /// <summary>
    /// THE ASSIGNED HALF of a scaffold verdict - what the plugin decided about each region it placed.
    ///
    /// It lives here, GH-free, rather than inside the shim that emits it, because it is the PRODUCER SIDE
    /// OF THE JOIN <see cref="Read"/> depends on, and the two must not be able to drift (#248: two halves
    /// of one contract disagreeing, with nothing to notice). A hand-written fixture standing in for a
    /// producer's output can silently stop resembling it; a shared function cannot.
    ///
    /// Two things it guarantees, both #355:
    ///   - every region carries its <c>ref</c>, which is the ONLY exact route from an address back to its
    ///     plan entry - a positional or name-based join would be a guess, and a guessed join aims a
    ///     mutation at the wrong region;
    ///   - <c>dependsOn</c> is re-expressed in ADDRESSES. It used to hold the model's refs beside a key
    ///     holding an address, so the assigned half stated its dependency graph in a namespace it could
    ///     not resolve.
    /// </summary>
    /// <param name="placed">The regions that actually landed, in the order they were placed.</param>
    /// <param name="all">Every region in the plan - the ref-to-address map is built from this, because a
    /// region that could not be PLACED still HAS an address, and naming it in a dependsOn is truthful.</param>
    public static JsonArray AssignedRegions(IReadOnlyList<ScaffoldRegion> placed, IReadOnlyList<ScaffoldRegion> all)
    {
      var addressByRef = (all ?? new List<ScaffoldRegion>())
        .Where(r => r != null && !string.IsNullOrEmpty(r.Ref) && !string.IsNullOrEmpty(r.Key))
        .GroupBy(r => r.Ref, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(g => g.Key, g => g.First().Key, StringComparer.OrdinalIgnoreCase);

      var regions = new JsonArray();
      foreach (var r in placed ?? new List<ScaffoldRegion>())
      {
        if (r == null) continue;
        regions.Add(new JsonObject
        {
          ["ref"] = r.Ref,
          ["key"] = r.Key,
          ["name"] = r.Name,
          ["group"] = r.GroupName,
          // A ref with no address is DROPPED rather than passed through in the wrong namespace. It cannot
          // arise from a validated plan - the parser already refuses a dependsOn naming a region outside
          // the plan - so this can never silently lose a real dependency.
          ["dependsOn"] = new JsonArray((r.DependsOn ?? new List<string>())
            .Select(d => addressByRef.TryGetValue(d ?? "", out var address) ? address : null)
            .Where(a => a != null)
            .Select(a => (JsonNode)a)
            .ToArray()),
        });
      }
      return regions;
    }

    /// <summary>
    /// The stored map: the model's plan exactly as it was submitted, beside the addresses the plugin
    /// assigned to it. Both halves are needed - the plan alone has no addresses, and the addresses alone
    /// have nothing to build.
    /// </summary>
    public static string Build(string argsJson, string verdictJson)
    {
      // DETACHED copies. A JsonNode carries a parent, and re-parenting one that is still attached throws -
      // which would have thrown on the ORDINARY path, on the UI thread, after a map had already landed
      // and verified. DeepClone is the detach; a re-parse would have to be of the WHOLE document, since
      // indexing back into a wrapper hands back an attached node again.
      var plan = (Parse(argsJson) ?? new JsonObject()).DeepClone();
      var assigned = Regions(verdictJson).DeepClone();

      return new JsonObject
      {
        // The version is the map's own, not the store's: WP-4 reads this shape and must be able to tell
        // an older one from a newer without inspecting its contents.
        ["mapVersion"] = CurrentMapVersion,
        ["plan"] = plan,
        ["assigned"] = assigned,
      }.ToJsonString();
    }

    /// <summary>
    /// THE MAP AS IT READS RIGHT NOW: the canvas joined to the stored plan (WP-4 Stage A).
    ///
    /// TWO SOURCES, AND THE CANVAS IS THE AUTHORITATIVE ONE. Which regions EXIST is a fact about objects,
    /// read from the group names (#221b, and #137's rule that a structural fact is read from the
    /// authoritative structure). The stored record supplies what the canvas cannot know - the purpose,
    /// the pseudo-code, the declared boundaries, the anchors. So a lost or unreadable record costs the
    /// PLAN, never the map: the addresses still answer, and the note says what is missing.
    ///
    /// A MAP TOO OLD TO JOIN REFUSES ITS PLAN HALF, BY NAME (#355). It does not guess the join, and it
    /// does not refuse the whole tool - the canvas half is true regardless of the record's age, and
    /// throwing it away would be its own dishonesty. Nothing from an unjoinable plan leaks into the
    /// result: a region from a v1 map carries canvas facts and empty everything else.
    ///
    /// Both directions of mismatch are REPORTED rather than dropped: a planned region whose group is gone,
    /// and a scaffold group the record does not know about.
    ///
    /// GH-free (ADR-091); the caller supplies <paramref name="canvas"/> from the live document.
    /// </summary>
    public static MapReadResult Read(string storedMapJson, IReadOnlyList<CanvasRegion> canvas)
    {
      var result = new MapReadResult();
      var onCanvas = (canvas ?? new List<CanvasRegion>())
        // BOTH KINDS (U18) - one reader of the canvas, and the PAYLOAD partitions map regions from spots,
        // exactly as it already partitions present from absent. Two readers of "what regions are on this
        // canvas" would be the #248 class, and the partition is a presentation decision either way.
        .Where(c => c != null && ScaffoldAddress.IsRegionAddress(c.Address))
        .GroupBy(c => c.Address, StringComparer.Ordinal)
        .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

      var stored = Parse(storedMapJson);
      if (stored == null)
      {
        result.Notes.Add(string.IsNullOrWhiteSpace(storedMapJson)
          ? "There is no stored plan for this conversation, so only what the canvas itself carries is reported."
          : "The stored plan could not be read, so only what the canvas itself carries is reported.");
        return WithCanvasOnly(result, onCanvas);
      }

      result.MapVersion = Int(stored["mapVersion"]);
      if (result.MapVersion != CurrentMapVersion)
      {
        // #355: no guessed join. Name it, say what to do, and keep the half that is still true.
        result.Notes.Add("This map was laid down by an earlier build (format " + result.MapVersion +
                         ") and does not carry the reference each region needs to be matched to its plan, " +
                         "so only what the canvas itself carries is reported. Lay the map down again with " +
                         "propose_scaffold to restore the plan detail.");
        return WithCanvasOnly(result, onCanvas);
      }

      // ---- the join: assigned[].ref <-> plan.regions[].ref, both the plugin's own bookkeeping --------
      var planByRef = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
      foreach (var node in (stored["plan"]?["regions"] as JsonArray) ?? new JsonArray())
      {
        var o = node as JsonObject;
        var key = Str(o?["ref"]);
        if (o != null && key.Length > 0 && !planByRef.ContainsKey(key)) planByRef[key] = o;
      }

      var seen = new HashSet<string>(StringComparer.Ordinal);
      foreach (var node in (stored["assigned"] as JsonArray) ?? new JsonArray())
      {
        var o = node as JsonObject;
        var address = Str(o?["key"]);
        if (!ScaffoldAddress.IsAddress(address)) continue;
        if (!seen.Add(address)) continue;

        var region = new MapRegion
        {
          Address = address,
          Name = Str(o?["name"]),
          DependsOn = Strings(o?["dependsOn"]),
        };

        var reference = Str(o?["ref"]);
        if (reference.Length > 0 && planByRef.TryGetValue(reference, out var plan))
        {
          region.PlanKnown = true;
          region.Purpose = Str(plan["purpose"]);
          region.Pseudo = Strings(plan["pseudo"]);
          region.Inputs = Strings(plan["inputs"]);
          region.Outputs = Strings(plan["outputs"]);
          region.AnchorIds = Ints(plan["anchorIds"]);
        }
        else
        {
          result.Notes.Add(address + " is in the stored map but its plan could not be matched to it.");
        }

        if (onCanvas.TryGetValue(address, out var live))
        {
          region.OnCanvas = true;
          region.ComponentCount = live.ComponentCount;
        }
        else
        {
          result.Notes.Add(address + " is in the stored plan but has no group on the canvas - it may have " +
                           "been undone or deleted.");
        }

        result.Regions.Add(region);
      }

      // The other direction: a scaffold group the record does not account for.
      foreach (var live in onCanvas.Values.Where(c => !seen.Contains(c.Address)))
      {
        // A SPOT IS NOT AN UNEXPLAINED GROUP (U18). The old note - "nothing is known about what it is
        // for" - would be FALSE of a spot: the user approved it on a card that named exactly what was
        // going into it, and the group carries that name. Saying otherwise would be this reader
        // inventing an absence, which is the same shape of dishonesty as inventing a presence (F4).
        if (ScaffoldAddress.Kind(live.Address) != AddressKind.Spot)
          result.Notes.Add(live.Address + " has a group on the canvas but is not in the stored plan, so " +
                           "nothing is known about what it is for.");
        result.Regions.Add(FromCanvas(live));
      }

      result.PlanAvailable = true;
      Order(result);
      return result;
    }

    private static MapReadResult WithCanvasOnly(MapReadResult result, Dictionary<string, CanvasRegion> onCanvas)
    {
      foreach (var live in onCanvas.Values) result.Regions.Add(FromCanvas(live));
      result.PlanAvailable = false;
      Order(result);
      return result;
    }

    private static MapRegion FromCanvas(CanvasRegion live) => new MapRegion
    {
      Address = live.Address,
      Name = live.Name,
      OnCanvas = true,
      ComponentCount = live.ComponentCount,
      PlanKnown = false,
    };

    /// <summary>Address order - layer, then the number NUMERICALLY (#286). A map that listed A10 before
    /// A2 would be arguing with the one thing its addresses exist to say.</summary>
    private static void Order(MapReadResult result) =>
      result.Regions = result.Regions
        .OrderBy(r => ScaffoldAddress.SortKey(r.Address).Letters, StringComparer.Ordinal)
        .ThenBy(r => ScaffoldAddress.SortKey(r.Address).Number)
        .ToList();

    private static int Int(JsonNode n)
    {
      if (n == null) return 0;
      try { return n.GetValue<int>(); } catch { return 0; }
    }

    private static List<string> Strings(JsonNode n) =>
      ((n as JsonArray) ?? new JsonArray()).Select(Str).Where(s => s.Length > 0).ToList();

    private static List<int> Ints(JsonNode n)
    {
      var ids = new List<int>();
      foreach (var item in (n as JsonArray) ?? new JsonArray())
      {
        try { ids.Add(item.GetValue<int>()); } catch { }
      }
      return ids;
    }

    /// <summary>
    /// The chat summary - plugin-authored, with the model's region names ESCAPED into it as values. The
    /// same rule as the consent card (#235): Dwight's sentence carries the facts, the model's words are
    /// data inside it.
    /// </summary>
    public static string SummaryHtml(string verdictJson)
    {
      var regions = Regions(verdictJson);
      if (regions.Count == 0)
        return "<p>The map came back empty - nothing was laid down.</p>";

      var sb = new StringBuilder();
      sb.Append("<p>Laid down <strong>").Append(regions.Count)
        .Append(regions.Count == 1 ? "</strong> region" : "</strong> regions")
        .Append(". One Ctrl-Z removes the whole map.</p><ul class=\"maplist\">");

      foreach (var node in regions)
      {
        var o = node as JsonObject;
        sb.Append("<li><strong>").Append(Enc(o?["key"])).Append("</strong> ")
          .Append(Enc(o?["name"])).Append("</li>");
      }
      sb.Append("</ul>");

      // The things the map did that the user would otherwise have to notice for themselves.
      var reversed = Array(verdictJson, "reversedForReadingOrder");
      if (reversed.Count > 0)
        sb.Append("<p class=\"mapnote\">Reading order: ").Append(reversed.Count)
          .Append(reversed.Count == 1 ? " dependency was" : " dependencies were")
          .Append(" reversed so the map lays out left to right. Nothing about the plan changed.</p>");

      var unplaceable = Array(verdictJson, "unplaceable");
      if (unplaceable.Count > 0)
        sb.Append("<p class=\"mapnote\">Could not place: ")
          .Append(string.Join(", ", unplaceable.Select(n => WebUtility.HtmlEncode(Str(n)))))
          .Append(".</p>");

      var dropped = Array(verdictJson, "droppedAnchors");
      if (dropped.Count > 0)
        sb.Append("<p class=\"mapnote\">").Append(dropped.Count)
          .Append(dropped.Count == 1 ? " anchor pointed" : " anchors pointed")
          .Append(" at something no longer on the canvas and was left out.</p>");

      return sb.ToString();
    }

    /// <summary>The addresses assigned, for a caller that wants them without re-parsing.</summary>
    public static JsonArray Regions(string verdictJson)
    {
      var root = Parse(verdictJson);
      // The verdict is the tool's wire envelope: { ok, result: { ... } } - or the bare result.
      var result = root?["result"] as JsonObject ?? root;
      return result?["regions"] as JsonArray ?? new JsonArray();
    }

    private static JsonArray Array(string verdictJson, string field)
    {
      var root = Parse(verdictJson);
      var result = root?["result"] as JsonObject ?? root;
      return result?[field] as JsonArray ?? new JsonArray();
    }

    private static JsonObject Parse(string json)
    {
      if (string.IsNullOrWhiteSpace(json)) return null;
      try { return JsonNode.Parse(json) as JsonObject; } catch { return null; }
    }

    private static string Str(JsonNode n)
    {
      if (n == null) return "";
      try { return n.GetValue<string>() ?? ""; } catch { return n.ToString(); }
    }

    private static string Enc(JsonNode n) => WebUtility.HtmlEncode(Str(n));
  }
}
