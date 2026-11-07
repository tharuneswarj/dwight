using System;
using System.Collections.Generic;
using System.Linq;

namespace Dwight.Segment
{
  /// <summary>
  /// Stage 2 (Phase 6 WP-B, ADR-087): subdivide ONE oversized rigid region the RPST could not decompose. It
  /// is never the primary partitioner - the Segmenter hands it only the connected blobs Stage 1 leaves. The
  /// pipeline, deterministic throughout (guid order, fixed seeds), so scattered == tidy holds:
  ///   1. local hub isolation - re-run the SAME shared-input rule Stage 1 uses, region-relative, BEFORE
  ///      betweenness, so a shared input (global or borderline-local) is lifted and can never be mistaken for
  ///      a module boundary. The two stages agree: a shared input is not a boundary.
  ///   2. undirected edge betweenness (Brandes) on the core - high = a bridge between sub-modules.
  ///   3. invert into weights, definition-relative (1 / (1 + b/mean)) - bridges weak, internals strong.
  ///   4. resolution-tuned Louvain (the parked CommunityDetector) toward a benchmark-calibrated target size.
  ///   5. reattach lifted local hubs to a neighbouring sub-community.
  /// Returns each region node -> a LOCAL sub-community id; the Segmenter maps those to fresh global ids. The
  /// acceptance signal is NOT the sub-count (modularity hits any target) - it is the rigid-file ARI.
  /// </summary>
  internal static class Stage2
  {
    public static Dictionary<int, int> Subdivide(IReadOnlyList<SegNode> regionNodes, IReadOnlyList<SegEdge> regionEdges,
      IReadOnlyDictionary<(int, int), (bool param, bool entry)> boundary, SegOptions o)
    {
      var keyById = regionNodes.ToDictionary(n => n.Id, n => n.Key);

      // 1. local hub isolation (region-relative; the SAME rule as Stage 1)
      var hubs = DataflowGraph.Hubs(new SegInput(regionNodes, regionEdges), o);
      var hubSet = new HashSet<int>(hubs);
      var coreIds = new HashSet<int>(regionNodes.Where(n => !hubSet.Contains(n.Id)).Select(n => n.Id));
      var coreEdges = regionEdges.Where(e => coreIds.Contains(e.FromId) && coreIds.Contains(e.ToId) && e.FromId != e.ToId).ToList();

      // 2. undirected, key-sorted adjacency + edge betweenness on the core
      var adj = new Dictionary<int, List<int>>();
      foreach (var id in coreIds) adj[id] = new List<int>();
      foreach (var e in coreEdges)
      {
        if (!adj[e.FromId].Contains(e.ToId)) adj[e.FromId].Add(e.ToId);
        if (!adj[e.ToId].Contains(e.FromId)) adj[e.ToId].Add(e.FromId);
      }
      foreach (var k in adj.Keys.ToList()) adj[k] = adj[k].OrderBy(n => keyById[n]).ToList();
      var coreList = coreIds.OrderBy(i => keyById[i]).ToList();
      var eb = Betweenness.EdgeBetweenness(coreList, adj, keyById);

      // 3. invert betweenness into edge weights, definition-relative (by the mean)
      var bmean = eb.Count > 0 ? eb.Values.Average() : 0.0;

      // wire-length bonus prep (gated): a long drawn wire weakly lowers an edge's weight. Median-normalised,
      // so definition-relative; with the gate off, no position is read at all.
      var pivot = regionNodes.ToDictionary(n => n.Id, n => (X: n.PivotX, Y: n.PivotY));
      double Length((int, int) c)
      {
        if (!pivot.TryGetValue(c.Item1, out var p) || !pivot.TryGetValue(c.Item2, out var q)) return 0.0;
        var dx = p.X - q.X; var dy = p.Y - q.Y;
        return Math.Sqrt(dx * dx + dy * dy);
      }
      var medianLen = 0.0;
      if (o.WireLengthEnabled)
      {
        var lens = coreEdges
          .Select(e => Length(keyById[e.FromId].CompareTo(keyById[e.ToId]) <= 0 ? (e.FromId, e.ToId) : (e.ToId, e.FromId)))
          .Where(l => l > 0).OrderBy(l => l).ToList();
        medianLen = lens.Count > 0 ? lens[lens.Count / 2] : 0.0;
      }

      var weighted = new List<WeightedEdge>();
      var seen = new HashSet<(int, int)>();
      foreach (var e in coreEdges)
      {
        var canon = keyById[e.FromId].CompareTo(keyById[e.ToId]) <= 0 ? (e.FromId, e.ToId) : (e.ToId, e.FromId);
        if (!seen.Add(canon)) continue;
        eb.TryGetValue(canon, out var b);
        var w = bmean > 0 ? 1.0 / (1.0 + b / bmean) : 1.0;   // bridge edges -> small weight

        // the two boundary terms (ADR-087): a seam edge - param relay or long entry-crossing - is weakened
        // further, so Louvain cuts there. Each is gated for the benchmark's per-term on/off delta.
        if (boundary != null && boundary.TryGetValue(canon, out var fl))
        {
          if (fl.param && o.ParamBoundaryEnabled) w *= o.ParamBoundaryFactor;
          if (fl.entry && o.EntryCrossingEnabled) w *= o.EntryCrossingFactor;
        }

        // wire-length bonus: a wire longer than the region median is weakly weakened (low, never primary).
        if (o.WireLengthEnabled && medianLen > 0)
        {
          var len = Length(canon);
          if (len > medianLen) w *= Math.Max(1.0 - o.WireLengthFactor, 1.0 - o.WireLengthFactor * (len / medianLen - 1.0));
        }

        weighted.Add(new WeightedEdge(canon.Item1, canon.Item2, w, synthetic: false));
      }
      var wg = new WeightedGraph(coreList, weighted);

      // 4. resolution-tuned Louvain toward the benchmark-calibrated target size
      var target = Math.Max(1, (int)Math.Round(coreList.Count / (double)Math.Max(1, o.Stage2TargetSize)));
      var corePart = TuneResolution(wg, target);

      // 5. reattach lifted hubs to a neighbour's sub-community
      var result = new Dictionary<int, int>();
      foreach (var kv in corePart) result[kv.Key] = kv.Value;

      var fullAdj = new Dictionary<int, List<int>>();
      void Link(int a, int b) { if (!fullAdj.TryGetValue(a, out var l)) fullAdj[a] = l = new List<int>(); if (!l.Contains(b)) l.Add(b); }
      foreach (var e in regionEdges) { if (e.FromId == e.ToId) continue; Link(e.FromId, e.ToId); Link(e.ToId, e.FromId); }

      var remaining = hubs.OrderBy(h => keyById[h]).ToList();
      var progress = true;
      while (progress && remaining.Count > 0)
      {
        progress = false;
        var next = new List<int>();
        foreach (var h in remaining)
        {
          var neighbours = (fullAdj.TryGetValue(h, out var l) ? l : new List<int>())
            .Where(result.ContainsKey).OrderBy(x => keyById[x]).ToList();
          if (neighbours.Count > 0) { result[h] = result[neighbours[0]]; progress = true; }
          else next.Add(h);
        }
        remaining = next;
      }
      var nextLocal = result.Count > 0 ? result.Values.Max() + 1 : 0;
      foreach (var h in remaining) result[h] = nextLocal++;   // isolated hub -> own sub-community

      return result;
    }

    /// <summary>Binary-search the modularity resolution toward <paramref name="target"/> sub-communities.
    /// NOTE: hitting the target count is not success - modularity hits any count; acceptance is the rigid-file
    /// ARI. The target only sets a sane granularity; the benchmark calibrates the target size.</summary>
    private static IReadOnlyDictionary<int, int> TuneResolution(WeightedGraph g, int target)
    {
      var detector = new LouvainConnectivity();
      if (g.NodeIds.Count == 0) return new Dictionary<int, int>();
      if (target <= 1) return detector.Detect(g, 0.0001);

      int Count(IReadOnlyDictionary<int, int> p) => p.Values.Distinct().Count();
      double lo = 0.05, hi = 50.0;
      var best = detector.Detect(g, 1.0);
      var bestDiff = Math.Abs(Count(best) - target);

      for (var iter = 0; iter < 24; iter++)
      {
        var mid = (lo + hi) / 2.0;
        var part = detector.Detect(g, mid);
        var count = Count(part);
        var diff = Math.Abs(count - target);
        if (diff < bestDiff || (diff == bestDiff && count < Count(best))) { best = part; bestDiff = diff; }
        if (count == target) return part;
        if (count < target) lo = mid; else hi = mid;
      }
      return best;
    }
  }
}
