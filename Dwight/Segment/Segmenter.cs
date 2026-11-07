using System;
using System.Collections.Generic;
using System.Linq;

namespace Dwight.Segment
{
  /// <summary>
  /// The deterministic RPST-first segmenter (Phase 6 WP-B Stage 1, ADR-087). Boundaries come from dataflow
  /// TOPOLOGY, not position. Pipeline, in order:
  ///   1. hub isolation - lift shared-input fan-out hubs out before clustering (a shared slider must not
  ///      collapse distinct sublogics into one);
  ///   2. virtual super-source/super-sink + dominator and post-dominator trees;
  ///   3. RPST - the nested tree of single-entry-single-exit regions, read from dominance;
  ///   4. granularity descent - lock fragments in a definition-relative size band, never a fixed count.
  /// Stage 2 (edge-betweenness + directed Leiden inside oversized rigid regions) is added ONLY if the Stage-1
  /// benchmark needs it - it is not built here. Deterministic (guid tie-break everywhere) and scope-agnostic:
  /// it runs on whatever SegInput it is given (selection, region, or whole), no special-casing.
  ///
  /// The honest ceiling (ADR-087, designed around, not hidden): structure recovers the coarse skeleton and
  /// most sub-units, but provably cannot split a branchless "string of pearls" (no topological signal), see
  /// tree restructuring hidden inside a component/script, or tell two topologically-identical parallel
  /// sublogics apart. Those three are exactly what the downstream LLM naming pass (WP-C) resolves.
  /// </summary>
  internal sealed class Segmenter
  {
    private readonly SegOptions _options;

    public Segmenter(SegOptions options = null) { _options = options ?? SegOptions.Default; }

    public SegResult Segment(SegInput input)
    {
      var result = new SegResult();
      if (input == null || input.Nodes.Count == 0) return result;

      var keyById = input.Nodes.ToDictionary(n => n.Id, n => n.Key);

      // 1. hub isolation
      var hubs = DataflowGraph.Hubs(input, _options);
      var hubSet = new HashSet<int>(hubs);

      // 2-3. virtual endpoints + dominator/post-dominator trees + RPST
      var g = DataflowGraph.Build(input, hubSet);
      var idom = Dominators.Compute(g, reverse: false);
      var ipdom = Dominators.Compute(g, reverse: true);
      var root = Rpst.Build(g, idom, ipdom);

      // 4. granularity descent -> the locked fragments (root + leftovers split by weak connectivity)
      var fragments = Rpst.Fragments(root, g, _options);

      // assemble the partition: each fragment a group; hubs a shared "global inputs" group; any uncovered
      // real node (should not happen - the root covers everything) becomes its own stray group.
      var macro = new Dictionary<int, int>();
      var gid = 0;
      foreach (var frag in fragments)
      {
        var any = false;
        foreach (var id in frag) if (!macro.ContainsKey(id)) { macro[id] = gid; any = true; }
        if (any) gid++;
      }
      var hubGroupId = -1;
      if (hubs.Count > 0) { hubGroupId = gid++; foreach (var h in hubs) macro[h] = hubGroupId; }
      foreach (var n in input.Nodes) if (!macro.ContainsKey(n.Id)) macro[n.Id] = gid++;

      // 5. Stage 2 (ADR-087): subdivide oversized RIGID groups only - the connected blobs RPST could not
      // decompose - with betweenness-weighted Louvain + the two boundary terms. Never the hub group.
      var boundary = ClassifyBoundaries(g, ipdom, input, keyById);
      var (rigidRegions, rigidSubdivided) = RunStage2(input, macro, keyById, hubGroupId, boundary, ref gid);

      // 5b. GranularityMin (WP-4.6): after flattening + Stage 2, fold a sub-minimum group into the neighbour it
      // is MOST WIRED to (topological, so the scattered==tidy invariance holds; an isolated small fragment is
      // left alone). After Stage 2 - not before - so Stage 2's oversized-split runs first and is not re-merged.
      MergeSmallFragments(macro, input, hubGroupId);

      // 6. cleanup: reassign clear misfits to the group they are actually wired into (topological primary,
      // geometric distance corroborating - the safe role for position).
      Cleanup(macro, input, hubGroupId);

      result.Macro = Canonicalize(macro, keyById);
      result.Hubs = hubs;
      result.Regions = BuildRegions(result.Macro);
      result.Diagnostics = new SegDiagnostics
      {
        NodeCount = input.Nodes.Count,
        HubCount = hubs.Count,
        RootInteriorSize = root.Interior.Count,
        MatchedRegions = Rpst.CountRegions(root),
        RpstDepth = Rpst.MaxDepth(root),
        GroupCount = result.MacroCount,
        WeakComponents = SpineWeakComponents(g),
        RigidRegions = rigidRegions,
        RigidSubdivided = rigidSubdivided,
      };
      return result;
    }

    /// <summary>Find the oversized rigid groups (size &gt; the granularity max; the hub group excluded) and hand
    /// each to Stage 2, remapping its nodes onto fresh global group ids. Returns (rigid regions found, regions
    /// actually subdivided). A region Stage 2 cannot split (one community out) is left as it was.</summary>
    private (int found, int subdivided) RunStage2(SegInput input, Dictionary<int, int> macro, Dictionary<int, Guid> keyById,
      int hubGroupId, IReadOnlyDictionary<(int, int), (bool param, bool entry)> boundary, ref int nextGid)
    {
      if (!_options.Stage2Enabled) return (0, 0);

      var oversized = macro.GroupBy(kv => kv.Value)
        .Where(grp => grp.Key != hubGroupId && grp.Count() > _options.GranularityMax)
        .OrderBy(grp => grp.Key)
        .ToList();
      var subdivided = 0;

      foreach (var grp in oversized)
      {
        var members = new HashSet<int>(grp.Select(kv => kv.Key));
        var regionNodes = input.Nodes.Where(n => members.Contains(n.Id)).ToList();
        var regionEdges = input.Edges.Where(e => members.Contains(e.FromId) && members.Contains(e.ToId)).ToList();

        var sub = Stage2.Subdivide(regionNodes, regionEdges, boundary, _options);   // node -> local sub-community
        if (sub.Values.Distinct().Count() <= 1) continue;                           // could not split - leave it

        var localToGlobal = new Dictionary<int, int>();
        foreach (var kv in sub.OrderBy(k => keyById.TryGetValue(k.Key, out var g) ? g : Guid.Empty))
        {
          if (!localToGlobal.TryGetValue(kv.Value, out var gg)) localToGlobal[kv.Value] = gg = nextGid++;
          macro[kv.Key] = gg;
        }
        subdivided++;
      }
      return (oversized.Count, subdivided);
    }

    /// <summary>
    /// Classify each spine edge into the two boundary signals (ADR-087 boundary terms), both topological:
    ///   param   - the edge is incident to a bare parameter component (the author's seam marker);
    ///   entry   - the edge is a long crossing INTO a region entry node, GUARDED so it fires only on a genuine
    ///             entry: E has few real incoming edges, E's outflow funnels into one downstream region
    ///             (ipdom(E) is a real node, not the sink, so a multi-output divergence never qualifies), and
    ///             U shares no real post-dominator with E (they reconverge only at the global sink).
    /// Returned keyed by the canonical (guid-ordered) edge, for Stage 2 to penalise.
    /// </summary>
    private Dictionary<(int, int), (bool param, bool entry)> ClassifyBoundaries(
      DataflowGraph g, Dictionary<int, int> ipdom, SegInput input, Dictionary<int, Guid> keyById)
    {
      var isParam = new Dictionary<int, bool>();
      foreach (var n in input.Nodes) isParam[n.Id] = n.IsParam;

      var result = new Dictionary<(int, int), (bool param, bool entry)>();
      foreach (var e in input.Edges)
      {
        if (e.FromId == e.ToId) continue;
        if (!ipdom.ContainsKey(e.FromId) || !ipdom.ContainsKey(e.ToId)) continue;   // not a spine edge (hub/leaf)

        var param = (isParam.TryGetValue(e.FromId, out var pf) && pf) || (isParam.TryGetValue(e.ToId, out var pt) && pt);
        var entry = IsEntryCrossing(g, ipdom, e.FromId, e.ToId);
        if (!param && !entry) continue;

        var canon = keyById[e.FromId].CompareTo(keyById[e.ToId]) <= 0 ? (e.FromId, e.ToId) : (e.ToId, e.FromId);
        result.TryGetValue(canon, out var cur);
        result[canon] = (cur.param || param, cur.entry || entry);
      }
      return result;
    }

    /// <summary>
    /// Reassign clear misfits after clustering: a component more strongly WIRED to a neighbouring group than
    /// to its own moves to the group it is actually wired into. Connection strength is the PRIMARY criterion
    /// (topological, scatter-proof); geometric distance only CORROBORATES a tie (the safe role for position),
    /// and is gated off in the scattered==tidy path so structure-only determinism holds. Hubs are never moved.
    /// </summary>
    private void Cleanup(Dictionary<int, int> macro, SegInput input, int hubGroupId)
    {
      if (!_options.CleanupEnabled) return;

      var adj = new Dictionary<int, List<int>>();
      void Link(int a, int b) { if (!adj.TryGetValue(a, out var l)) adj[a] = l = new List<int>(); l.Add(b); }
      foreach (var e in input.Edges) { if (e.FromId == e.ToId) continue; Link(e.FromId, e.ToId); Link(e.ToId, e.FromId); }

      var pivot = input.Nodes.ToDictionary(n => n.Id, n => (X: n.PivotX, Y: n.PivotY));
      var order = input.Nodes.OrderBy(n => n.Key).Select(n => n.Id).ToList();

      for (var pass = 0; pass < _options.CleanupPasses; pass++)
      {
        var changed = false;
        var centroid = _options.WireLengthEnabled ? Centroids(macro, pivot) : null;
        foreach (var n in order)
        {
          if (!macro.TryGetValue(n, out var own) || own == hubGroupId) continue;
          if (!adj.TryGetValue(n, out var nbrs)) continue;

          var strength = new Dictionary<int, int>();
          foreach (var m in nbrs)
          {
            if (!macro.TryGetValue(m, out var gm) || gm == hubGroupId) continue;
            strength.TryGetValue(gm, out var c); strength[gm] = c + 1;
          }
          strength.TryGetValue(own, out var ownStr);

          var cands = strength.Where(kv => kv.Key != own).ToList();
          if (cands.Count == 0) continue;
          var maxStr = cands.Max(kv => kv.Value);
          var tied = cands.Where(kv => kv.Value == maxStr).Select(kv => kv.Key);
          var bestG = centroid != null
            ? tied.OrderBy(gp => DistTo(n, gp, centroid, pivot)).ThenBy(gp => gp).First()
            : tied.OrderBy(gp => gp).First();

          if (maxStr > ownStr) { macro[n] = bestG; changed = true; }                 // primary: more wired elsewhere
          else if (maxStr == ownStr && maxStr > 0 && centroid != null &&             // tie: the closer group wins
                   DistTo(n, bestG, centroid, pivot) < DistTo(n, own, centroid, pivot))
          { macro[n] = bestG; changed = true; }
        }
        if (!changed) break;
      }
    }

    /// <summary>WP-4.6: merge each below-<see cref="SegOptions.GranularityMin"/> group into the neighbour group it
    /// is most strongly WIRED to (edge count is primary, so it is scatter-proof; ties by group id). Isolated small
    /// groups (no cross-group wire) and the hub group are never merged. Bounded passes so a chain of tiny groups
    /// coalesces without looping. A no-op when GranularityMin &lt;= 1 (nothing is below the floor).</summary>
    private void MergeSmallFragments(Dictionary<int, int> macro, SegInput input, int hubGroupId)
    {
      if (_options.GranularityMin <= 1) return;

      var adj = new Dictionary<int, List<int>>();
      void Link(int a, int b) { if (!adj.TryGetValue(a, out var l)) adj[a] = l = new List<int>(); l.Add(b); }
      foreach (var e in input.Edges) { if (e.FromId == e.ToId) continue; Link(e.FromId, e.ToId); Link(e.ToId, e.FromId); }

      for (var pass = 0; pass < _options.GranularityMin; pass++)   // bounded coalescing
      {
        var sizes = macro.GroupBy(kv => kv.Value).ToDictionary(gp => gp.Key, gp => gp.Count());
        var small = sizes.Where(kv => kv.Key != hubGroupId && kv.Value < _options.GranularityMin)
                         .Select(kv => kv.Key).OrderBy(id => id).ToList();
        if (small.Count == 0) break;

        var changed = false;
        foreach (var sg in small)
        {
          var members = macro.Where(kv => kv.Value == sg).Select(kv => kv.Key).ToList();
          if (members.Count == 0 || members.Count >= _options.GranularityMin) continue;   // may have grown this pass

          var strength = new Dictionary<int, int>();
          foreach (var n in members)
            if (adj.TryGetValue(n, out var nbrs))
              foreach (var m in nbrs)
                if (macro.TryGetValue(m, out var gm) && gm != sg && gm != hubGroupId)
                { strength.TryGetValue(gm, out var c); strength[gm] = c + 1; }
          if (strength.Count == 0) continue;   // isolated small fragment - keep it as its own group

          var maxStr = strength.Values.Max();
          var target = strength.Where(kv => kv.Value == maxStr).Select(kv => kv.Key).OrderBy(id => id).First();
          foreach (var n in members) macro[n] = target;
          changed = true;
        }
        if (!changed) break;
      }
    }

    private static Dictionary<int, (double X, double Y, int N)> Centroids(Dictionary<int, int> macro, Dictionary<int, (double X, double Y)> pivot)
    {
      var acc = new Dictionary<int, (double X, double Y, int N)>();
      foreach (var kv in macro)
      {
        if (!pivot.TryGetValue(kv.Key, out var p)) continue;
        acc.TryGetValue(kv.Value, out var a);
        acc[kv.Value] = (a.X + p.X, a.Y + p.Y, a.N + 1);
      }
      return acc;
    }

    private static double DistTo(int n, int group, Dictionary<int, (double X, double Y, int N)> centroid, Dictionary<int, (double X, double Y)> pivot)
    {
      if (!pivot.TryGetValue(n, out var p) || !centroid.TryGetValue(group, out var c) || c.N == 0) return double.MaxValue;
      var dx = p.X - c.X / c.N; var dy = p.Y - c.Y / c.N;
      return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>Is edge u-&gt;e a long crossing into a genuine region entry? (The over-fire guard is built in.)</summary>
    private bool IsEntryCrossing(DataflowGraph g, Dictionary<int, int> ipdom, int u, int e)
    {
      var inDegree = g.Predecessors(e).Count(g.IsReal);
      if (inDegree > _options.EntryMaxInDegree) return false;                       // not a narrow entry
      if (!ipdom.TryGetValue(e, out var ipe) || ipe == DataflowGraph.Sink) return false; // outflow not coherent
      return !Dominators.SharesRealPostDominator(ipdom, u, e);                      // u is cross-region
    }

    /// <summary>The number of weakly-connected components of the spine (after hub removal) - the floor on the
    /// group count, and the signal that distinguishes "many independent pipelines" from "one giant tangle"
    /// (where Stage 2 would be needed).</summary>
    private static int SpineWeakComponents(DataflowGraph g)
    {
      var real = g.Nodes.Where(g.IsReal).ToHashSet();
      var seen = new HashSet<int>();
      var n = 0;
      foreach (var start in real)
      {
        if (!seen.Add(start)) continue;
        n++;
        var queue = new Queue<int>();
        queue.Enqueue(start);
        while (queue.Count > 0)
        {
          var u = queue.Dequeue();
          foreach (var v in g.Successors(u).Concat(g.Predecessors(u)))
            if (g.IsReal(v) && real.Contains(v) && seen.Add(v)) queue.Enqueue(v);
        }
      }
      return n;
    }

    /// <summary>Order communities by their minimum member guid, then relabel 0..K-1 - so the partition labels
    /// are deterministic and guid-rank ordered, independent of the descent's emission order.</summary>
    private static IReadOnlyDictionary<int, int> Canonicalize(IReadOnlyDictionary<int, int> part, Dictionary<int, Guid> keyById)
    {
      var minKey = new Dictionary<int, Guid>();
      foreach (var kv in part)
      {
        var k = keyById.TryGetValue(kv.Key, out var g) ? g : Guid.Empty;
        if (!minKey.TryGetValue(kv.Value, out var cur) || k.CompareTo(cur) < 0) minKey[kv.Value] = k;
      }
      var order = minKey.OrderBy(kv => kv.Value).Select(kv => kv.Key).ToList();
      var map = new Dictionary<int, int>();
      for (var i = 0; i < order.Count; i++) map[order[i]] = i;

      var result = new Dictionary<int, int>(part.Count);
      foreach (var kv in part) result[kv.Key] = map[kv.Value];
      return result;
    }

    private static IReadOnlyList<SegRegion> BuildRegions(IReadOnlyDictionary<int, int> macro)
    {
      var byId = new Dictionary<int, SegRegion>();
      foreach (var rid in macro.Values.Distinct().OrderBy(r => r))
        byId[rid] = new SegRegion { Id = rid, Level = 0, ParentId = -1 };
      foreach (var kv in macro.OrderBy(kv => kv.Key)) byId[kv.Value].Members.Add(kv.Key);
      return byId.Values.OrderBy(r => r.Id).ToList();
    }
  }
}
