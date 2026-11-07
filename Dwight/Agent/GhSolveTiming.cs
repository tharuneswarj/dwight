using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Grasshopper.Kernel;
using Dwight.Data;

namespace Dwight.Agent
{
  /// <summary>
  /// U23's solve-timing read - the Grasshopper half. It gathers FACTS and decides nothing: what each reading
  /// means, what to list, and how to say it are all <see cref="SolveTimingReport"/>'s, which is GH-free and tested.
  ///
  /// A READ IN THE STRICT SENSE (#190): zero-token, solve-free, mutation-free. It never calls
  /// <c>NewSolution</c> or <c>ScheduleSolution</c>, even on a definition that has never been solved - that case
  /// is reported as a fact with its way through, because running a solve to answer "where is the time going"
  /// would change the answer it was asked for, and would be the read doing work the user did not approve.
  ///
  /// ONE READ OF THE VALUE. The milliseconds come from <c>Payloads.ProcessorMs</c> - the same read Simplify's
  /// hotspot signal has used since Phase 7 - and the rounding from <see cref="SolveTimeBucket"/>, which Simplify
  /// now shares. The two surfaces describe the same components and must not disagree about them.
  /// </summary>
  internal static class GhSolveTiming
  {
    public static ToolResult Run(GH_Document doc, AgentScope scope, JsonObject args)
    {
      if (doc == null || scope == null)
        return ToolResult.Fail(ToolErrors.NoDocument, "No Grasshopper document is open.");

      if (!SolveTimingReport.TryParseArgs(args, out var limit, out var limitError)) return limitError;

      // The same snapshot get_overview builds, WITH its regions, so the region ids this read names are the ones a
      // follow-up get_region will accept. The partition is stamped on the scope exactly as get_overview stamps it
      // (#195), so a region id from here is refused on a changed canvas rather than resolved to something else.
      var snap = GhAgent.Build(doc, scope.Ids, true);
      var stamp = AgentToolCore.ShapeHash(snap);
      scope.PartitionStamp = stamp;

      var regionOf = new Dictionary<int, int>();
      foreach (var c in snap.Clusters)
        foreach (var m in c.Members)
          regionOf[m] = c.Id;

      var facts = new List<SolveTimeFact>();
      foreach (var n in snap.Nodes)
      {
        // TOP-LEVEL ONLY. A cluster's own processor time already covers the components inside it, so counting
        // its internals as well would count the same work twice and move time into regions it does not belong to.
        if (n.Inside != null) continue;
        if (!scope.Ids.TryGetGuid(n.Id, out var guid)) continue;
        if (!(doc.FindObject(guid, false) is IGH_ActiveObject active)) continue;

        facts.Add(new SolveTimeFact
        {
          Id = n.Id,
          Type = n.Type,
          Nick = n.Nick,
          Recorded = HasComputed(active),
          Ms = Payloads.ProcessorMs(doc, guid),
          Region = regionOf.TryGetValue(n.Id, out var r) ? r : (int?)null,
        });
      }

      return SolveTimingReport.Build(facts, snap.Clusters, stamp, limit);
    }

    /// <summary>
    /// Has this object computed? The plugin's EXISTING definition of "not yet solved" - <c>Phase == Blank</c>,
    /// used at three call sites to decide whether a definition needs solving - rather than a second definition
    /// invented here. A disabled object does not compute, so it is never recorded whatever its phase says.
    /// </summary>
    internal static bool HasComputed(IGH_ActiveObject active)
    {
      try { return !active.Locked && active.Phase != GH_SolutionPhase.Blank; }
      catch { return false; }   // cannot tell is NOT RECORDED - never a zero that reads as "fast"
    }
  }
}
