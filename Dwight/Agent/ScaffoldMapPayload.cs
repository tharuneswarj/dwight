using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace Dwight.Agent
{
  /// <summary>
  /// The <c>get_scaffold_map</c> payload, assembled GH-FREE so its partition can be tested (ADR-091).
  ///
  /// It lives in its own file for one reason. F4, at the WP-4 gate, was a defect in which THE TOOL TOLD
  /// THE TRUTH AND THE TRUTH WAS DROPPED: the user undid the map, asked what was on it, and the model
  /// described all nine regions as present - twice - while every one of them carried onCanvas:false,
  /// exactly as the descriptor promised. A fix for that must not itself be unverifiable, and it could not
  /// be verified while it sat inside a shim that needs a live document.
  /// </summary>
  internal static class ScaffoldMapPayload
  {
    /// <summary>
    /// THE MAP PAYLOAD, GH-FREE so the partition can be tested (ADR-091).
    ///
    /// It is extracted for one reason: F4 was a defect in which the tool told the truth and the truth was
    /// dropped, and a fix for that must not itself be unverifiable. Everything above this line needs a
    /// live document; nothing below it does.
    /// </summary>
    public static JsonObject Build(MapReadResult map, int truncated, int maxRegions)
    {
      // F4, from the WP-4 gate: THE FLAG WAS TRUE AND THE MODEL SKIMMED PAST IT, TWICE.
      //
      // The user undid the map and asked what was on it. Every region came back with onCanvas:false -
      // the tool was honest, exactly as its descriptor promises - and the model described all nine as
      // though they were there, twice, until the user asked point-blank whether there were any maps on
      // the canvas.
      //
      // The fix is the payload's SHAPE, not a prompt line. A boolean beside a name is a thing a reader
      // can skim; two separate lists are not. Regions that are NOT on the canvas are no longer
      // enumerable in the same breath as ones that are, so "list the map" CANNOT produce a list that
      // mixes them. That is ADR-056 applied to a read: make the wrong answer unrepresentable rather
      // than discouraged.
      //
      // `onCanvas` stays on each entry. Removing it would break the published contract for a caller who
      // reads a single region, and the partition is the load-bearing half.
      var regions = new JsonArray();
      var absent = new JsonArray();
      foreach (var r in map.Regions)
      {
        var region = new JsonObject
        {
          ["address"] = r.Address,
          ["name"] = r.Name ?? "",
          ["onCanvas"] = r.OnCanvas,
          ["componentsInside"] = r.ComponentCount,
          ["planKnown"] = r.PlanKnown,
        };
        if (r.PlanKnown)
        {
          region["purpose"] = r.Purpose ?? "";
          if (r.Pseudo.Count > 0) region["pseudo"] = Arr(r.Pseudo);
          if (r.Inputs.Count > 0) region["takes"] = Arr(r.Inputs);
          if (r.Outputs.Count > 0) region["gives"] = Arr(r.Outputs);
          if (r.AnchorIds.Count > 0) region["anchorIds"] = new JsonArray(r.AnchorIds.Select(i => (JsonNode)i).ToArray());
        }
        if (r.DependsOn.Count > 0) region["dependsOn"] = Arr(r.DependsOn);
        (r.OnCanvas ? regions : absent).Add(region);
      }

      var result = new JsonObject
      {
        // ON THE CANVAS. These are the only regions that exist to build into, point at, or describe as
        // present. The field name is deliberately unchanged so a caller reading "regions" gets a
        // strictly SAFER answer than before rather than a differently-shaped one.
        ["regions"] = regions,
        ["planAvailable"] = map.PlanAvailable,
      };

      // RECORDED BUT GONE - a separate list, and a sentence saying what that means, because the number
      // alone would be another thing to skim. It is emitted only when it is non-empty: a map with
      // nothing missing should not carry a paragraph about absence.
      if (absent.Count > 0)
      {
        result["absentFromCanvas"] = absent;
        result["absentNote"] =
          absent.Count + " region(s) below are in the stored plan but are NOT on the canvas - they were " +
          "undone, deleted, or never laid. They cannot be built into and must not be described to the " +
          "user as though they are there. The canvas is the authority; the stored plan is a memory of " +
          "what was intended.";
      }

      var notes = new List<string>(map.Notes);
      if (truncated > 0)
        notes.Add(truncated + " further scaffold group(s) on this canvas were not listed (the map is " +
                  "capped at " + maxRegions + " regions per call).");
      if (!map.PlanAvailable)
        notes.Add("Region names and addresses here come from the canvas itself and are reliable; what each " +
                  "region is FOR is not available.");
      if (notes.Count > 0) notes.Insert(0, "Read what these say before acting on the map.");
      if (notes.Count > 0) result["notes"] = Arr(notes);

      // SAY IT PLAINLY WHEN THE CANVAS IS BARE - including the F4 case, which the old condition missed.
      //
      // "No regions at all" and "every region was undone" are the same fact to a reader and were two
      // different code paths: with nine undone regions, map.Regions.Count is nine, so the sentence below
      // stayed silent and the caller got an empty list with nothing saying why. That silence is what the
      // model filled in with nine regions that were not there.
      if (regions.Count == 0)
        result["empty"] = absent.Count > 0
          ? "NOTHING is on this canvas: all " + absent.Count + " region(s) of the stored plan have been " +
            "undone or deleted. Do not describe them as present. propose_scaffold can lay the map down " +
            "again, with the user's approval."
          : "No scaffold regions are on this canvas. propose_scaffold lays one down; it needs " +
            "the user's approval first.";

      return result;
    }

    private static JsonArray Arr(IEnumerable<string> items) =>
      new JsonArray(items.Select(s => (JsonNode)(s ?? "")).ToArray());
  }
}
