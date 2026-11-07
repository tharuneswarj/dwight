using System;
using System.Collections.Generic;
using System.Linq;

namespace Dwight.Layout
{
  /// <summary>
  /// Phase 7 WP-1: the alternate cluster-arrangement packers (Grid and HorizontalChain). Compact stays in
  /// <see cref="ClusterLayout"/> (the engine's 2D forest packing, unchanged). These two consume the SAME inputs
  /// the Compact stage does - the cluster nodes (sized by their member block + bands), the acyclic cluster flow
  /// edges, the weakly-connected components, and the LLM's arrangement hints - and emit the cluster CENTRES that
  /// the compose stage then hangs the members off. They never touch the member-block layout (that ran first and
  /// is mode-independent).
  ///
  /// The precedence law is enforced identically to Compact: HORIZONTAL is the flow (a longest-path depth over
  /// the acyclic cluster DAG, the engine's), and hints refine ONLY the vertical - the same-layer +
  /// mutually-unreachable + same-component filter (<see cref="PeerGroups"/>) keeps a hint pair only when the flow
  /// leaves it free, so a hallucinated hint can never reorder along the flow. Everything is guid-keyed
  /// (the cluster's min-member InstanceGuid), never the pivot-derived cluster/node id - the WP-A determinism
  /// lesson - so a re-run reproduces byte-identically. Headless and GH-free, so it is unit-tested.
  /// </summary>
  internal static class ClusterArrangements
  {
    /// <summary>Produce the cluster centres for a non-Compact arrangement. Reports how many hint pairs the
    /// flow-wins filter kept / dropped (for the result panel's honesty, same as Compact).</summary>
    public static Dictionary<int, PointD> Arrange(
      ArrangementMode mode,
      IReadOnlyList<LayoutNode> clusterNodes,
      IReadOnlyList<LayoutEdge> clusterFlowEdges,                 // acyclic (cycle-broken) flow edges
      Dictionary<int, HashSet<int>> reach,                       // transitive reachability over the full cluster DAG
      Dictionary<int, int> comp,                                 // weakly-connected component per cluster
      Dictionary<int, (double W, double H)> size,
      Dictionary<int, Guid> key,                                 // cluster -> min-member InstanceGuid (the stable tie-break)
      IReadOnlyList<(string Kind, IReadOnlyList<int> Clusters)> hints,
      LayoutOptions options,
      out int dropped, out int kept)
    {
      var ids = clusterNodes.Select(n => n.Id).ToList();
      var layer = Layers(ids, clusterFlowEdges);
      var peer = PeerGroups(layer, reach, comp, hints, out dropped, out kept);

      // uniform pitch off the widest / tallest cluster (bands included) - guarantees no overlap. The additive
      // gaps are tightened from the layout defaults (WP-1 RR2 item 7, runtime-tuned - the WP-A precedent), so
      // chain/vertical/grid read compact rather than sparse; the no-overlap invariant is unchanged (pitch still
      // exceeds the widest/tallest cluster by a positive gap).
      var maxW = size.Count > 0 ? size.Values.Max(s => s.W) : 1;
      var maxH = size.Count > 0 ? size.Values.Max(s => s.H) : 1;
      const double ColGap = 26, RowGap = 14, BandGap = 44;
      var colPitch = maxW + ColGap;
      var rowPitch = maxH + RowGap;

      switch (mode)
      {
        case ArrangementMode.Grid: return Grid(ids, layer, peer, key, colPitch, rowPitch);
        case ArrangementMode.Vertical: return Vertical(ids, layer, comp, peer, key, colPitch, rowPitch, BandGap);
        default: return HorizontalChain(ids, layer, comp, peer, key, colPitch, rowPitch, BandGap);
      }
    }

    // ---- flow depth ---------------------------------------------------------------------------------

    /// <summary>Longest-path layer of each cluster over the acyclic flow edges (a source is layer 0). Order-
    /// independent, so deterministic. The in-progress guard makes it safe against any residual cycle the
    /// cycle-breaker could not fully remove (treated as depth 0 rather than recursing forever).</summary>
    private static Dictionary<int, int> Layers(List<int> ids, IReadOnlyList<LayoutEdge> edges)
    {
      var preds = ids.ToDictionary(i => i, i => new List<int>());
      foreach (var e in edges)
        if (preds.ContainsKey(e.ToId) && preds.ContainsKey(e.FromId)) preds[e.ToId].Add(e.FromId);

      var layer = new Dictionary<int, int>();
      var inProgress = new HashSet<int>();
      int Depth(int c)
      {
        if (layer.TryGetValue(c, out var v)) return v;
        if (!inProgress.Add(c)) return 0;   // cycle guard
        var d = 0;
        foreach (var p in preds[c]) d = Math.Max(d, Depth(p) + 1);
        inProgress.Remove(c);
        layer[c] = d;
        return d;
      }
      foreach (var c in ids) Depth(c);
      return layer;
    }

    // ---- the flow-wins hint filter (discrete-layer form of ClusterLayout.BuildPeerGroups) -----------

    /// <summary>Union hint pairs that the flow leaves free: SAME layer (same flow depth) AND SAME weakly-
    /// connected component AND MUTUALLY UNREACHABLE. Anything the flow already orders is dropped - the hint
    /// cannot move it. Identical semantics to the Compact path; keyed on the discrete layer instead of
    /// near-equal engine x (both are the same flow depth).</summary>
    private static Dictionary<int, int> PeerGroups(
      Dictionary<int, int> layer, Dictionary<int, HashSet<int>> reach, Dictionary<int, int> comp,
      IReadOnlyList<(string Kind, IReadOnlyList<int> Clusters)> hints, out int dropped, out int kept)
    {
      dropped = 0; kept = 0;
      var parent = new Dictionary<int, int>();
      int Find(int x) { while (parent.TryGetValue(x, out var p) && p != x) { parent[x] = parent.GetValueOrDefault(p, p); x = p; } return x; }
      void Union(int a, int b) { parent.TryAdd(a, a); parent.TryAdd(b, b); var ra = Find(a); var rb = Find(b); if (ra != rb) parent[ra < rb ? rb : ra] = ra < rb ? ra : rb; }

      int Comp(int c) => comp.TryGetValue(c, out var v) ? v : c;
      bool SameLayer(int a, int b) => layer.TryGetValue(a, out var la) && layer.TryGetValue(b, out var lb) && la == lb && Comp(a) == Comp(b);
      bool Independent(int a, int b) => !(reach.TryGetValue(a, out var ra) && ra.Contains(b)) && !(reach.TryGetValue(b, out var rb) && rb.Contains(a));

      foreach (var h in hints ?? Array.Empty<(string, IReadOnlyList<int>)>())
      {
        var cs = (h.Clusters ?? Array.Empty<int>()).Where(layer.ContainsKey).Distinct().ToList();
        for (var i = 0; i < cs.Count; i++)
          for (var j = i + 1; j < cs.Count; j++)
          {
            if (SameLayer(cs[i], cs[j]) && Independent(cs[i], cs[j])) { Union(cs[i], cs[j]); kept++; }
            else dropped++;
          }
      }
      var group = new Dictionary<int, int>();
      foreach (var c in parent.Keys) group[c] = Find(c);
      return group;
    }

    /// <summary>Order the clusters in one column/layer: peer groups stay contiguous, groups ordered by their
    /// minimum stable key, members by key. Fully guid-keyed, so deterministic.</summary>
    private static List<int> OrderByPeerThenKey(IEnumerable<int> clusters, Dictionary<int, int> peer, Dictionary<int, Guid> key)
    {
      int GroupKey(int c) => peer.TryGetValue(c, out var g) ? g : c;   // singleton = its own id
      Guid K(int c) => key.TryGetValue(c, out var g) ? g : Guid.Empty;
      return clusters
        .GroupBy(GroupKey)
        .OrderBy(grp => grp.Min(K))
        .SelectMany(grp => grp.OrderBy(K))
        .ToList();
    }

    // ---- Grid: columns by depth on a regular pitch --------------------------------------------------

    /// <summary>Every cluster at flow depth d sits in column d at x = d*colPitch (shared across ALL components).
    /// Within a column the clusters stack on a regular row pitch, peer-contiguous. Aligned rows, maximum air.</summary>
    private static Dictionary<int, PointD> Grid(
      List<int> ids, Dictionary<int, int> layer, Dictionary<int, int> peer, Dictionary<int, Guid> key,
      double colPitch, double rowPitch)
    {
      var centres = new Dictionary<int, PointD>();
      foreach (var col in ids.GroupBy(c => layer[c]))
      {
        var ordered = OrderByPeerThenKey(col, peer, key);
        var x = col.Key * colPitch;
        for (var row = 0; row < ordered.Count; row++)
          centres[ordered[row]] = new PointD(x, row * rowPitch + rowPitch / 2);
      }
      return centres;
    }

    // ---- HorizontalChain: one strict left-to-right row per component, bands stacked -------------------

    /// <summary>Each weakly-connected component is TOPOLOGICALLY LINEARIZED onto ONE strict left-to-right row -
    /// the sentence read (runtime round 2 ruling). The order is edge-monotone (by flow depth, so if A reaches B
    /// then A is left of B - the refined precedence-law invariant), and the remaining freedom - the order of
    /// mutually-unreachable CO-DEPTH peers along the row - is where the LLM's hints finally bite in chain mode
    /// (hint-peers adjacent), guid-tie-broken for determinism. Placing co-depth peers at different x violates
    /// nothing: there is no dependency between them to misrepresent. Disconnected components are separate rows
    /// stacked below. (On a pure pipeline there are no co-depth peers, so a single row IS the depth order and
    /// chain coincides with grid - the degenerate case, by construction, not a bug.)</summary>
    private static Dictionary<int, PointD> HorizontalChain(
      List<int> ids, Dictionary<int, int> layer, Dictionary<int, int> comp, Dictionary<int, int> peer,
      Dictionary<int, Guid> key, double colPitch, double rowPitch, double bandGap)
    {
      Guid K(int c) => key.TryGetValue(c, out var g) ? g : Guid.Empty;
      int Comp(int c) => comp.TryGetValue(c, out var v) ? v : c;

      var centres = new Dictionary<int, PointD>();
      var bands = ids.GroupBy(Comp)
        .Select(g => g.ToList())
        .OrderBy(b => b.Min(K))            // deterministic band order by min stable key
        .ToList();

      double cursorY = 0;
      foreach (var band in bands)
      {
        // linearize: edge-monotone by depth, co-depth peers ordered adjacent (hints) then guid -> one row.
        var ordered = band
          .GroupBy(c => layer[c])
          .OrderBy(g => g.Key)
          .SelectMany(g => OrderByPeerThenKey(g, peer, key))
          .ToList();
        var y = cursorY + rowPitch / 2;
        for (var i = 0; i < ordered.Count; i++)
          centres[ordered[i]] = new PointD(i * colPitch, y);   // sequential x along the strict row
        cursorY += rowPitch + bandGap;
      }
      return centres;
    }

    // ---- Vertical: one strict top-to-bottom column per component, columns side by side (WP-1 RR2) ------

    /// <summary>Chain rotated onto Y: each weakly-connected component is TOPOLOGICALLY LINEARIZED down one column
    /// (edge-monotone on Y - if A reaches B, A sits ABOVE B), co-depth peers ordered adjacent (hints) then guid;
    /// disconnected components are placed side-by-side as separate columns. The cluster-internal member layout
    /// (left-to-right) is untouched. The Y-axis twin of <see cref="HorizontalChain"/>.</summary>
    private static Dictionary<int, PointD> Vertical(
      List<int> ids, Dictionary<int, int> layer, Dictionary<int, int> comp, Dictionary<int, int> peer,
      Dictionary<int, Guid> key, double colPitch, double rowPitch, double bandGap)
    {
      Guid K(int c) => key.TryGetValue(c, out var g) ? g : Guid.Empty;
      int Comp(int c) => comp.TryGetValue(c, out var v) ? v : c;

      var centres = new Dictionary<int, PointD>();
      var columns = ids.GroupBy(Comp)
        .Select(g => g.ToList())
        .OrderBy(b => b.Min(K))
        .ToList();

      double cursorX = 0;
      foreach (var col in columns)
      {
        var ordered = col
          .GroupBy(c => layer[c])
          .OrderBy(g => g.Key)
          .SelectMany(g => OrderByPeerThenKey(g, peer, key))
          .ToList();
        var x = cursorX + colPitch / 2;
        for (var i = 0; i < ordered.Count; i++)
          centres[ordered[i]] = new PointD(x, i * rowPitch);   // sequential y down the strict column
        cursorX += colPitch + bandGap;
      }
      return centres;
    }
  }
}
