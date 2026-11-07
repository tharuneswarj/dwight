using System;
using System.Collections.Generic;
using System.Linq;

namespace Dwight.Layout
{
  /// <summary>
  /// The nested cluster layout (Phase 6 WP-D). Two levels, in the order the division requires:
  ///   1. members tight FIRST - lay out each cluster's own components into a compact block (this is what
  ///      pulls scattered members together);
  ///   2. then box + arrange the blocks along the FLOW - feed the cluster DAG (clusters sized by their block,
  ///      inter-cluster wires as edges) to the same engine, so HORIZONTAL = dependency depth, computed from
  ///      the wires with ZERO LLM input (geometry stays the engine's).
  /// The LLM's arrangement hints refine ONLY the vertical (same-depth) grouping the flow leaves open, and only
  /// where flow is silent: a hint pair survives only if its clusters are SAME-LAYER and MUTUALLY UNREACHABLE
  /// in the cluster DAG. Where a hint disagrees with a wire, FLOW WINS (the pair is dropped). So a hallucinated
  /// hint can never reorder anything along the flow axis. Headless and GH-free, so it is unit-tested.
  /// </summary>
  internal static class ClusterLayout
  {
    /// <summary>A laid-out cluster: its id, its member ids, and the final bounding box (for the group).</summary>
    internal sealed class ClusterBox
    {
      public int Id;
      public List<int> Members = new List<int>();
      public double MinX, MinY, MaxX, MaxY;
    }

    internal sealed class Result
    {
      public Dictionary<int, PointD> Centers = new Dictionary<int, PointD>();   // component id -> final canvas centre
      public List<ClusterBox> Clusters = new List<ClusterBox>();
      public int DroppedHintPairs;   // flow-wins: how many hint pairs contradicted the flow and were dropped
      public int PeerPairs;          // hint pairs that survived (same-layer, mutually unreachable)
    }

    public static Result Layout(
      IReadOnlyList<LayoutNode> nodes,
      IReadOnlyList<LayoutEdge> edges,
      IReadOnlyDictionary<int, int> macro,
      IReadOnlyList<(string Kind, IReadOnlyList<int> Clusters)> hints,
      LayoutOptions options = null)
    {
      options = options ?? LayoutOptions.Default;
      hints = hints ?? Array.Empty<(string, IReadOnlyList<int>)>();
      var result = new Result();
      if (nodes == null || nodes.Count == 0) return result;

      var byId = nodes.ToDictionary(n => n.Id);
      var clusters = macro.GroupBy(kv => kv.Value)
        .ToDictionary(g => g.Key, g => g.Select(kv => kv.Key).Where(byId.ContainsKey).ToList());
      var engine = LayoutEngine.Msagl();

      // ---- 1. tight blocks: lay out each cluster's members, record their local offsets + the block bbox ----
      var blocks = new Dictionary<int, Block>();
      foreach (var cl in clusters.OrderBy(c => c.Key))
      {
        var members = cl.Value;
        if (members.Count == 0) continue;
        var memberSet = new HashSet<int>(members);
        var cNodes = members.Select(id => byId[id]).ToList();
        var cEdges = edges.Where(e => memberSet.Contains(e.FromId) && memberSet.Contains(e.ToId)).ToList();
        var local = engine.Layout(cNodes, cEdges, options).Centers;
        blocks[cl.Key] = Block.From(cl.Key, members, local, byId);
      }
      if (blocks.Count == 0) return result;

      // ---- 2. cluster DAG: cluster nodes sized by their block, inter-cluster wires as edges ----------------
      var clusterNodes = blocks.Values
        .Select(b => new LayoutNode(b.Id, b.Key,
          b.Width + options.NodeSeparation + 2 * options.SideBand,            // reserve the input/output param columns
          b.Height + options.NodeSeparation + options.HeaderHeight))
        .ToList();
      var clusterWeight = new Dictionary<(int, int), int>();
      foreach (var e in edges)
      {
        if (!macro.TryGetValue(e.FromId, out var cu) || !macro.TryGetValue(e.ToId, out var cv) || cu == cv) continue;
        if (blocks.ContainsKey(cu) && blocks.ContainsKey(cv))
          clusterWeight[(cu, cv)] = clusterWeight.TryGetValue((cu, cv), out var w) ? w + 1 : 1;
      }
      var clusterEdgeSet = new HashSet<(int, int)>(clusterWeight.Keys);   // FULL graph - true reachability for the hint filter (cycles included)
      // WP-4.2: the weakly-connected component of each cluster. Independent pipelines are forest-packed into
      // separate 2D regions that can SHARE an x-range; without this, RefineVertical's near-equal-x layering
      // would merge clusters from different components into one "layer" and re-stack them on top of a neighbour.
      var comp = ClusterComponents(blocks.Keys, clusterEdgeSet);
      // A cyclic cluster graph has NO strict left-to-right order, so some links must read backward. Break cycles
      // by keeping the HEAVIEST links acyclic and dropping the lightest that would close a cycle - so the
      // dominant dataflow reads left-to-right and only the thinnest feedback links go backward (those fade).
      // This changes ONLY the display ordering fed to the engine; the wiring is untouched (behaviour-preserving).
      var clusterEdges = AcyclicByWeight(clusterWeight).Select(e => new LayoutEdge(e.Item1, e.Item2)).ToList();

      // ---- 3-4. arrange the cluster blocks: HORIZONTAL = flow (the engine's), hints refine VERTICAL only ---
      // The arrangement MODE (Phase 7 WP-1) chooses only how the blocks are packed; the member-block layout
      // above is untouched, and the flow (dependency depth) is the engine's in every mode.
      var reach = Reachability(blocks.Keys, clusterEdgeSet);
      var sizeByCluster = clusterNodes.ToDictionary(n => n.Id, n => (W: n.Width, H: n.Height));
      Dictionary<int, PointD> clusterCentres;
      int dropped, kept;
      if (options.Arrangement == ArrangementMode.Compact)
      {
        // the default: the engine's 2D forest packing, then the flow-wins hint filter + vertical refine.
        clusterCentres = engine.Layout(clusterNodes, clusterEdges, options).Centers;
        var peer = BuildPeerGroups(hints, clusterCentres, reach, comp, options, out dropped, out kept);
        RefineVertical(clusterCentres, sizeByCluster, peer, comp, options);
      }
      else
      {
        // Grid / HorizontalChain: horizontal still comes from the flow depth (the acyclic cluster edges); the
        // mode only changes how co-depth clusters and independent components are arranged in the vertical.
        var keyByCluster = clusterNodes.ToDictionary(n => n.Id, n => n.Key);
        clusterCentres = ClusterArrangements.Arrange(
          options.Arrangement, clusterNodes, clusterEdges, reach, comp, sizeByCluster, keyByCluster, hints, options,
          out dropped, out kept);
      }
      result.DroppedHintPairs = dropped;
      result.PeerPairs = kept;

      // ---- 5. compose: component final centre = cluster centre + (member local - block local centre) -------
      foreach (var b in blocks.Values)
      {
        if (!clusterCentres.TryGetValue(b.Id, out var cc)) continue;
        var box = new ClusterBox { Id = b.Id, Members = b.Members.ToList() };
        double minx = double.MaxValue, miny = double.MaxValue, maxx = double.MinValue, maxy = double.MinValue;
        foreach (var m in b.Members)
        {
          var local = b.LocalCenters[m];
          // shift the body DOWN by half the reserved header, so the top HeaderHeight of the cluster is a clear
          // band for the scribble (the cluster node was inflated by HeaderHeight, so neighbours keep clear).
          var c = new PointD(cc.X + (local.X - b.CenterX), cc.Y + (local.Y - b.CenterY) + options.HeaderHeight / 2);
          result.Centers[m] = c;
          var n = byId[m];
          minx = Math.Min(minx, c.X - n.Width / 2); maxx = Math.Max(maxx, c.X + n.Width / 2);
          miny = Math.Min(miny, c.Y - n.Height / 2); maxy = Math.Max(maxy, c.Y + n.Height / 2);
        }
        box.MinX = minx; box.MinY = miny; box.MaxX = maxx; box.MaxY = maxy;
        result.Clusters.Add(box);
      }
      result.Clusters = result.Clusters.OrderBy(b => b.Id).ToList();
      return result;
    }

    // ---- flow analysis ------------------------------------------------------------------------------

    /// <summary>WP-4.2: the weakly-connected component of each cluster (edges treated undirected), by union-find.
    /// Deterministic (union to the smaller id). Clusters with no inter-cluster edge are their own component.</summary>
    private static Dictionary<int, int> ClusterComponents(IEnumerable<int> clusterIds, HashSet<(int, int)> edges)
    {
      var parent = new Dictionary<int, int>();
      foreach (var c in clusterIds) parent[c] = c;
      int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
      void Union(int a, int b)
      {
        if (!parent.ContainsKey(a) || !parent.ContainsKey(b)) return;
        var ra = Find(a); var rb = Find(b);
        if (ra != rb) parent[ra < rb ? rb : ra] = ra < rb ? ra : rb;
      }
      foreach (var (a, b) in edges) Union(a, b);
      var comp = new Dictionary<int, int>();
      foreach (var c in parent.Keys) comp[c] = Find(c);
      return comp;
    }

    /// <summary>Transitive reachability over the cluster DAG (cycle-safe: a cycle just makes a pair mutually
    /// reachable). reach[a] = every cluster a can reach following the flow.</summary>
    private static Dictionary<int, HashSet<int>> Reachability(IEnumerable<int> clusterIds, HashSet<(int, int)> edges)
    {
      var succ = new Dictionary<int, List<int>>();
      foreach (var c in clusterIds) succ[c] = new List<int>();
      foreach (var (a, b) in edges) if (succ.ContainsKey(a)) succ[a].Add(b);

      var reach = new Dictionary<int, HashSet<int>>();
      foreach (var start in succ.Keys)
      {
        var seen = new HashSet<int>();
        var stack = new Stack<int>(succ[start]);
        while (stack.Count > 0)
        {
          var n = stack.Pop();
          if (!seen.Add(n)) continue;
          if (succ.TryGetValue(n, out var ss)) foreach (var s in ss) stack.Push(s);
        }
        reach[start] = seen;
      }
      return reach;
    }

    /// <summary>Greedy maximum-weight acyclic subgraph: add the cluster links heaviest-first. A link that would
    /// close a cycle (the lightest feedback arc) is REVERSED, not dropped (WP-4.3, ADR-084 as-built): keeping the
    /// flipped constraint holds the feedback cluster ADJACENT to its partner in the layout instead of letting it
    /// drift to an arbitrary layer, while the actual (thin) wire simply renders backward. Only when BOTH
    /// directions would close a cycle (a tight tangle) is the link genuinely dropped. This changes only the
    /// display ordering fed to the engine; the wiring is untouched, so it stays behaviour-preserving.
    /// Deterministic (ties broken by cluster id).</summary>
    private static List<(int, int)> AcyclicByWeight(Dictionary<(int, int), int> weighted)
    {
      var kept = new List<(int, int)>();
      var succ = new Dictionary<int, HashSet<int>>();
      void AddEdge(int a, int b)
      {
        if (!succ.TryGetValue(a, out var s)) { s = new HashSet<int>(); succ[a] = s; }
        if (s.Add(b)) kept.Add((a, b));   // dedup: a reversed edge may coincide with an existing forward one
      }
      foreach (var kv in weighted.OrderByDescending(x => x.Value).ThenBy(x => x.Key.Item1).ThenBy(x => x.Key.Item2))
      {
        var (u, v) = kv.Key;
        if (!Reaches(succ, v, u)) AddEdge(u, v);          // forward keeps the DAG acyclic
        else if (!Reaches(succ, u, v)) AddEdge(v, u);     // reverse the feedback arc (flip direction), stay adjacent
        // else: both directions cycle -> genuinely drop
      }
      return kept;
    }

    private static bool Reaches(Dictionary<int, HashSet<int>> succ, int from, int to)
    {
      if (from == to) return true;
      var seen = new HashSet<int>();
      var stack = new Stack<int>();
      stack.Push(from);
      while (stack.Count > 0)
      {
        var n = stack.Pop();
        if (n == to) return true;
        if (!seen.Add(n)) continue;
        if (succ.TryGetValue(n, out var s)) foreach (var m in s) stack.Push(m);
      }
      return false;
    }

    /// <summary>Build the vertical peer groups from the hints, applying the FLOW-WINS filter: a hint pair is
    /// kept only when the two clusters are SAME LAYER (same flow depth, read off the engine x) AND MUTUALLY
    /// UNREACHABLE (neither feeds the other). Anything the flow already orders is dropped.</summary>
    private static Dictionary<int, int> BuildPeerGroups(
      IReadOnlyList<(string Kind, IReadOnlyList<int> Clusters)> hints, Dictionary<int, PointD> centres,
      Dictionary<int, HashSet<int>> reach, Dictionary<int, int> comp, LayoutOptions options, out int dropped, out int kept)
    {
      dropped = 0; kept = 0;
      var parent = new Dictionary<int, int>();
      int Find(int x) { while (parent.TryGetValue(x, out var p) && p != x) { parent[x] = parent.GetValueOrDefault(p, p); x = p; } return x; }
      void Union(int a, int b) { parent.TryAdd(a, a); parent.TryAdd(b, b); var ra = Find(a); var rb = Find(b); if (ra != rb) parent[ra < rb ? rb : ra] = ra < rb ? ra : rb; }

      var tol = options.LayerSeparation * 0.5;   // same flow depth = near-equal engine x
      int Comp(int c) => comp.TryGetValue(c, out var v) ? v : c;
      // WP-4.2: peers must be in the SAME weakly-connected component - a hint can never union clusters from two
      // independent pipelines (they live in separate forest-packed regions and would collide if stacked).
      bool SameLayer(int a, int b) => Comp(a) == Comp(b) && centres.ContainsKey(a) && centres.ContainsKey(b) && Math.Abs(centres[a].X - centres[b].X) <= tol;
      bool Independent(int a, int b) => !(reach.TryGetValue(a, out var ra) && ra.Contains(b)) && !(reach.TryGetValue(b, out var rb) && rb.Contains(a));

      foreach (var h in hints)
      {
        var cs = (h.Clusters ?? Array.Empty<int>()).Where(centres.ContainsKey).Distinct().ToList();
        for (var i = 0; i < cs.Count; i++)
          for (var j = i + 1; j < cs.Count; j++)
          {
            if (SameLayer(cs[i], cs[j]) && Independent(cs[i], cs[j])) { Union(cs[i], cs[j]); kept++; }
            else dropped++;   // flow wins - this pair is ordered by a wire, the hint cannot move it
          }
      }
      var group = new Dictionary<int, int>();
      foreach (var c in parent.Keys) group[c] = Find(c);
      return group;
    }

    /// <summary>Re-pack each flow layer vertically so peer-group clusters sit contiguously and tightly, keeping
    /// each cluster's engine x (the flow axis is never touched). Sizes are respected, so no overlap.</summary>
    private static void RefineVertical(
      Dictionary<int, PointD> centres, Dictionary<int, (double W, double H)> size,
      Dictionary<int, int> peer, Dictionary<int, int> comp, LayoutOptions options)
    {
      var tol = options.LayerSeparation * 0.5;
      int Comp(int c) => comp.TryGetValue(c, out var v) ? v : c;
      // WP-4.2: group clusters into flow layers by near-equal x, but ONLY WITHIN one weakly-connected component,
      // so an independent pipeline that the forest packer stacked at the same x is never merged into this layer
      // and re-stacked on top of it. Ordering by (component, x) makes each component's clusters contiguous first.
      var byX = centres.Keys.OrderBy(c => Comp(c)).ThenBy(c => centres[c].X).ToList();
      var layers = new List<List<int>>();
      foreach (var c in byX)
      {
        var last = layers.Count > 0 ? layers[layers.Count - 1] : null;
        if (last != null && Comp(last[0]) == Comp(c) && Math.Abs(centres[last[0]].X - centres[c].X) <= tol) last.Add(c);
        else layers.Add(new List<int> { c });
      }

      foreach (var layer in layers)
      {
        if (layer.Count <= 1) continue;
        var meanY = layer.Average(c => centres[c].Y);
        var layerX = layer.Average(c => centres[c].X);
        // order: peer groups stay together; groups and singletons ordered by their min engine-y; stable by key
        int GroupKey(int c) => peer.TryGetValue(c, out var g) ? g : c;
        var groupMinY = layer.GroupBy(GroupKey).ToDictionary(g => g.Key, g => g.Min(c => centres[c].Y));
        var ordered = layer
          .OrderBy(c => groupMinY[GroupKey(c)])
          .ThenBy(c => GroupKey(c))
          .ThenBy(c => centres[c].Y)
          .ToList();

        var total = ordered.Sum(c => size[c].H) + Gaps(ordered, GroupKey, options);
        var y = meanY - total / 2;
        int? prevGroup = null;
        foreach (var c in ordered)
        {
          var g = GroupKey(c);
          if (prevGroup != null) y += prevGroup == g ? options.NodeSeparation : options.BlockBuffer * 0.5;
          y += size[c].H / 2;
          centres[c] = new PointD(layerX, y);
          y += size[c].H / 2;
          prevGroup = g;
        }
      }
    }

    private static double Gaps(List<int> ordered, Func<int, int> groupKey, LayoutOptions o)
    {
      double sum = 0; int? prev = null;
      foreach (var c in ordered) { if (prev != null) sum += prev == groupKey(c) ? o.NodeSeparation : o.BlockBuffer * 0.5; prev = groupKey(c); }
      return sum;
    }

    // ---- a tight block ------------------------------------------------------------------------------

    private sealed class Block
    {
      public int Id;
      public Guid Key;
      public List<int> Members;
      public Dictionary<int, PointD> LocalCenters;
      public double MinX, MinY, MaxX, MaxY;
      public double Width => MaxX - MinX;
      public double Height => MaxY - MinY;
      public double CenterX => (MinX + MaxX) / 2;
      public double CenterY => (MinY + MaxY) / 2;

      public static Block From(int id, List<int> members, IReadOnlyDictionary<int, PointD> local, Dictionary<int, LayoutNode> byId)
      {
        double minx = double.MaxValue, miny = double.MaxValue, maxx = double.MinValue, maxy = double.MinValue;
        var key = new Guid("ffffffff-ffff-ffff-ffff-ffffffffffff");
        foreach (var m in members)
        {
          var c = local[m]; var n = byId[m];
          minx = Math.Min(minx, c.X - n.Width / 2); maxx = Math.Max(maxx, c.X + n.Width / 2);
          miny = Math.Min(miny, c.Y - n.Height / 2); maxy = Math.Max(maxy, c.Y + n.Height / 2);
          if (n.Key.CompareTo(key) < 0) key = n.Key;
        }
        return new Block
        {
          Id = id, Key = key, Members = members,
          LocalCenters = local.ToDictionary(kv => kv.Key, kv => kv.Value),
          MinX = minx, MinY = miny, MaxX = maxx, MaxY = maxy,
        };
      }
    }
  }
}
