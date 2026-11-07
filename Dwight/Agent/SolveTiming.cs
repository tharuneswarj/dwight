using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Dwight.Data;

namespace Dwight.Agent
{
  /// <summary>What the plugin read off ONE top-level component, for the timing read. Facts only - the report
  /// decides what they mean.</summary>
  internal sealed class SolveTimeFact
  {
    public int Id { get; set; }
    public string Type { get; set; }
    public string Nick { get; set; }

    /// <summary>
    /// HAS THIS COMPONENT COMPUTED? The fact that separates the two meanings of a zero reading.
    ///
    /// A component's processor time reads 0 when it ran faster than a millisecond AND when it never ran at all -
    /// opposite answers to "is this slow?". So the reading is never interpreted on its own: it is paired with the
    /// object's solution phase, using the definition the rest of the plugin already uses for "not yet solved"
    /// (<c>Phase == GH_SolutionPhase.Blank</c>, at three call sites), and a disabled component is never recorded,
    /// because it does not compute.
    /// </summary>
    public bool Recorded { get; set; }

    /// <summary>The whole-millisecond reading, unrounded by bucket. Never sent: only its bucket leaves the
    /// plugin, so there is one representation of this number (see <see cref="SolveTimeBucket"/>).</summary>
    public int Ms { get; set; }

    /// <summary>The region (get_overview's partition) this component belongs to, or null.</summary>
    public int? Region { get; set; }
  }

  /// <summary>
  /// U23: "THIS DEFINITION IS SLOW - WHERE IS THE TIME GOING?", answered from solves that ALREADY HAPPENED.
  ///
  /// THE BINDING RULE, and the reason for this file's shape: a read is zero-token, solve-free and mutation-free
  /// (#190), so timing must come from solves that already ran and never from triggering one. Everything here is a
  /// decision about readings the plugin already holds; the Grasshopper read is a separate, thin file.
  ///
  /// WHAT THE NUMBER MEANS, AND WHAT IT IS NOT - which is the part a careless version of this read would get wrong.
  /// A component's processor time is the time it took THE LAST TIME IT COMPUTED. A component that did not expire
  /// in the most recent solve is not recomputed, so it keeps whatever time it recorded whenever it last ran. The
  /// readings therefore do not belong to one solve, and SUMMING THEM DOES NOT GIVE A SOLVE'S DURATION - a read that
  /// reported a total would be stating a measurement nobody took. So this reports each component's own time and
  /// each region's SHARE of the recorded time, and says in its own payload what it does not measure.
  ///
  /// AND A ZERO IS NEVER READ ON ITS OWN (#843, distrust the zero). "Nothing is slow" and "nothing has run" both
  /// arrive as zeros; a report that returned an empty ranking for a definition that has never been solved would
  /// tell the model it is fast. So the recorded / not-recorded distinction is carried through, and a definition
  /// with nothing recorded says THAT, with the way through, rather than an empty list that reads as an answer.
  ///
  /// GH-free (ADR-091): facts in, a tool result out - so the contract test can drive it with the writer's real
  /// output (#649) and every decision here is asserted rather than reviewed.
  /// </summary>
  internal static class SolveTimingReport
  {
    /// <summary>The default number of components listed, and the most a caller may ask for. Twenty-five matches
    /// the geometry reads' cap, so the model meets one bound across the read set rather than several.</summary>
    public const int DefaultLimit = 10;
    public const int MaxLimit = 25;

    /// <summary>At most this many regions are listed. A definition with fifty regions has its time concentrated
    /// in a few of them, and the report says how many more carry time.</summary>
    public const int RegionCap = 8;

    /// <summary>
    /// WHAT THE READINGS MEAN, stated in the payload itself - because a number whose meaning the model has to
    /// guess is a number it will guess wrongly, and the likeliest wrong guess (that the times add up to a solve)
    /// is the one this read exists to avoid.
    ///
    /// The zero-bucket threshold is DERIVED from the rounding rather than written by hand (#777), so this sentence
    /// cannot go on claiming "under 2.5 ms" after someone changes the bucket.
    /// </summary>
    public static string Measures() =>
      "Each figure is the time that component took THE LAST TIME IT COMPUTED, rounded to the nearest 5 ms. A " +
      "component that did not recompute in the latest solve keeps its earlier time, so these figures do not come " +
      "from one solve and do not add up to a solve's duration. Region shares are the fraction of all recorded " +
      "time, computed before rounding. A component whose time rounds to 0 took no more than " +
      ZeroBoundText() + ". NOT MEASURED HERE: time spent drawing the preview in the Rhino viewport, and the " +
      "solve's own overhead between components.";

    /// <summary>The upper bound of the zero bucket, in words, from the rounding itself.</summary>
    public static string ZeroBoundText() => (SolveTimeBucket.LargestZeroReading + 0.5).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " ms";

    /// <summary>
    /// The arguments, validated exactly or refused (#208) - and HERE, in the GH-free core, rather than in the
    /// Grasshopper read.
    ///
    /// IT LIVED IN THE GRASSHOPPER FILE FIRST, and the descriptor-conformance harness refused it: the argument
    /// parse is precisely the half of a tool that the published descriptor is a contract ABOUT, so it has to be
    /// drivable without Grasshopper. The harness's answer to a tool it cannot drive is not an exemption - its
    /// exemption list is deliberately two tools long and says so - but a parse that does not need a document.
    ///
    /// The limit is not clamped silently: a model that asked for fifty and got twenty-five without being told would
    /// reason from a list it believed was longer.
    /// </summary>
    public static bool TryParseArgs(JsonObject args, out int limit, out ToolResult error)
    {
      limit = DefaultLimit;
      error = null;

      var node = args?["limit"];
      if (node == null) return true;

      int value;
      try { value = node.GetValue<int>(); }
      catch
      {
        error = ToolResult.Fail(ToolErrors.InvalidArgs, "\"limit\" must be a whole number from 1 to " + MaxLimit + ".");
        return false;
      }

      if (value < 1 || value > MaxLimit)
      {
        error = ToolResult.Fail(ToolErrors.InvalidArgs,
          "\"limit\" must be from 1 to " + MaxLimit + " - you asked for " + value + ".");
        return false;
      }

      limit = value;
      return true;
    }

    public static ToolResult Build(IReadOnlyList<SolveTimeFact> facts, IReadOnlyList<AgentCluster> regions,
      string partitionStamp, int limit)
    {
      facts = facts ?? Array.Empty<SolveTimeFact>();
      regions = regions ?? Array.Empty<AgentCluster>();
      if (limit < 1) limit = 1;
      if (limit > MaxLimit) limit = MaxLimit;

      var recorded = facts.Where(f => f.Recorded).ToList();
      var measurable = recorded.Where(f => SolveTimeBucket.Of(f.Ms) > 0)
                               .OrderByDescending(f => f.Ms).ThenBy(f => f.Id).ToList();
      var negligible = recorded.Count - measurable.Count;
      var notRecorded = facts.Count - recorded.Count;

      var root = new JsonObject
      {
        ["measures"] = Measures(),
        ["recorded"] = recorded.Count,
        ["negligible"] = negligible,
        ["notRecorded"] = notRecorded,
      };
      if (!string.IsNullOrEmpty(partitionStamp)) root["partition"] = partitionStamp;

      // NOTHING HAS COMPUTED: a fact, and the way through - never an empty ranking that reads as "nothing is slow".
      if (recorded.Count == 0)
      {
        root["nothingRecorded"] =
          "No component has recorded a solve time, so this cannot say where time goes. The definition has not " +
          "been solved since it was opened, or every component is disabled, or the solver is off. Once it has " +
          "been solved, ask again - this read never runs a solve itself.";
        return ToolResult.Success(root);
      }

      // EVERYTHING THAT RAN WAS NEGLIGIBLE: a real answer, stated as a fact about components and nothing more. It
      // does not say the definition is fast - the preview and the solve's overhead are not measured here.
      if (measurable.Count == 0)
        root["allNegligible"] =
          "Every component that has computed took no more than " + ZeroBoundText() + ", so no single component's own " +
          "computation stands out. If the definition still feels slow, the time is not in component computation " +
          "as measured here.";

      var components = new JsonArray();
      foreach (var f in measurable.Take(limit))
      {
        var c = new JsonObject { ["id"] = f.Id, ["type"] = f.Type ?? "", ["ms"] = SolveTimeBucket.Of(f.Ms) };
        if (!string.IsNullOrWhiteSpace(f.Nick)) c["nick"] = f.Nick;
        components.Add(c);
      }
      root["components"] = components;

      var hidden = measurable.Count - Math.Min(limit, measurable.Count);
      if (hidden > 0)
        root["omitted"] = hidden + " more component" + (hidden == 1 ? "" : "s") + " with measurable time " +
                          (hidden == 1 ? "is" : "are") + " not listed. Ask with a larger limit (at most " + MaxLimit + ").";

      // WHERE THE TIME IS, by region - the level a user can act on. Shares are of the UNROUNDED total, so rounding
      // never moves a region's rank; a region with no recorded time is not where the time is, and is not listed.
      var total = recorded.Sum(f => (long)f.Ms);
      if (total > 0 && regions.Count > 0)
      {
        var byRegion = recorded.Where(f => f.Region.HasValue)
          .GroupBy(f => f.Region.Value)
          .Select(g => new { Id = g.Key, Ms = g.Sum(x => (long)x.Ms) })
          .Where(r => r.Ms > 0)
          .OrderByDescending(r => r.Ms).ThenBy(r => r.Id)
          .ToList();

        var labels = regions.ToDictionary(r => r.Id, r => r.Label);
        var regionArr = new JsonArray();
        foreach (var r in byRegion.Take(RegionCap))
        {
          var o = new JsonObject
          {
            ["id"] = r.Id,
            ["ms"] = SolveTimeBucket.Of((int)Math.Min(r.Ms, int.MaxValue)),
            ["share"] = (int)Math.Round(100.0 * r.Ms / total),
          };
          if (labels.TryGetValue(r.Id, out var label) && !string.IsNullOrWhiteSpace(label)) o["label"] = label;
          regionArr.Add(o);
        }
        root["regions"] = regionArr;

        var regionsHidden = byRegion.Count - Math.Min(RegionCap, byRegion.Count);
        if (regionsHidden > 0)
          root["regionsOmitted"] = regionsHidden + " more region" + (regionsHidden == 1 ? "" : "s") +
                                   " with recorded time " + (regionsHidden == 1 ? "is" : "are") + " not listed.";
      }

      return ToolResult.Success(root);
    }
  }
}
