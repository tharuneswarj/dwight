using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Dwight.Layout.Tests
{
  /// <summary>
  /// Tests the nested cluster layout (WP-D) and the LLM/engine division it enforces: HORIZONTAL is the flow
  /// (engine, from wires) and the LLM hints refine ONLY the vertical, with FLOW WINS on any conflict. The two
  /// that matter most: a flow dependency always orders clusters left-to-right (the engine owns horizontal),
  /// and a hint that contradicts a wire is dropped (a hallucinated hint can never reorder along the flow).
  /// </summary>
  public class ClusterLayoutTests
  {
    private static Guid G(int i) => new Guid(i, 0, 0, new byte[8]);
    private static LayoutNode N(int id, double w = 60, double h = 30) => new LayoutNode(id, G(id), w, h);
    private static LayoutEdge E(int a, int b) => new LayoutEdge(a, b);
    private static (string, IReadOnlyList<int>) Hint(string kind, params int[] clusters) => (kind, (IReadOnlyList<int>)clusters);

    private static double AvgX(ClusterLayout.Result r, IEnumerable<int> ids) => ids.Average(i => r.Centers[i].X);

    [Fact]
    public void Horizontal_order_is_the_flow_the_engine_owns_it()
    {
      // cluster 0 {1,2} feeds cluster 1 {3,4} (edge 2->3). The wire dependency must put 0 left of 1.
      var nodes = new[] { 1, 2, 3, 4 }.Select(i => N(i)).ToList();
      var edges = new List<LayoutEdge> { E(1, 2), E(2, 3), E(3, 4) };
      var macro = new Dictionary<int, int> { [1] = 0, [2] = 0, [3] = 1, [4] = 1 };

      var r = ClusterLayout.Layout(nodes, edges, macro, null);
      Assert.True(AvgX(r, new[] { 1, 2 }) < AvgX(r, new[] { 3, 4 }), "the upstream cluster must sit left of the one it feeds");
    }

    [Fact]
    public void Flow_wins_a_hint_that_contradicts_a_wire_is_dropped()
    {
      // cluster 0 feeds 1 AND 2 (so 1 and 2 are co-depth peers). A 'stack' hint over {1,2} is valid (same
      // layer, mutually unreachable). A 'stack' over {0,1} contradicts the wire 0->1 and must be dropped.
      var nodes = new[] { 1, 2, 3, 4, 5, 6 }.Select(i => N(i)).ToList();
      var edges = new List<LayoutEdge> { E(1, 2), E(2, 3), E(3, 4), E(2, 5), E(5, 6) };
      var macro = new Dictionary<int, int> { [1] = 0, [2] = 0, [3] = 1, [4] = 1, [5] = 2, [6] = 2 };
      var hints = new List<(string, IReadOnlyList<int>)> { Hint("stack", 1, 2), Hint("stack", 0, 1) };

      var r = ClusterLayout.Layout(nodes, edges, macro, hints);

      Assert.True(r.PeerPairs >= 1, "the co-depth {1,2} stack should survive as a peer pair");
      Assert.True(r.DroppedHintPairs >= 1, "the {0,1} stack contradicts the flow and must be dropped");
      // flow still owns horizontal: cluster 0 is left of both 1 and 2
      Assert.True(AvgX(r, new[] { 1, 2 }) < AvgX(r, new[] { 3, 4 }));
      Assert.True(AvgX(r, new[] { 1, 2 }) < AvgX(r, new[] { 5, 6 }));
      // and 1 and 2 are co-depth (the engine put them at the same flow x)
      Assert.True(Math.Abs(AvgX(r, new[] { 3, 4 }) - AvgX(r, new[] { 5, 6 })) < 60);
    }

    [Fact]
    public void No_component_boxes_overlap_across_clusters()
    {
      var nodes = Enumerable.Range(1, 12).Select(i => N(i, 40 + (i % 3) * 20, 24 + (i % 2) * 18)).ToList();
      var edges = new List<LayoutEdge>
      {
        E(1, 2), E(2, 3), E(3, 4), E(4, 5),       // a chain spanning clusters
        E(5, 6), E(6, 7), E(7, 8), E(8, 9), E(9, 10), E(10, 11), E(11, 12),
      };
      var macro = new Dictionary<int, int> { [1] = 0, [2] = 0, [3] = 0, [4] = 1, [5] = 1, [6] = 1, [7] = 2, [8] = 2, [9] = 2, [10] = 3, [11] = 3, [12] = 3 };

      var r = ClusterLayout.Layout(nodes, edges, macro, null);
      var size = nodes.ToDictionary(n => n.Id, n => (n.Width, n.Height));
      var ids = r.Centers.Keys.ToList();
      for (var i = 0; i < ids.Count; i++)
        for (var j = i + 1; j < ids.Count; j++)
        {
          var a = ids[i]; var b = ids[j];
          var (aw, ah) = size[a]; var (bw, bh) = size[b];
          var sepX = r.Centers[a].X + aw / 2 <= r.Centers[b].X - bw / 2 + 0.5 || r.Centers[b].X + bw / 2 <= r.Centers[a].X - aw / 2 + 0.5;
          var sepY = r.Centers[a].Y + ah / 2 <= r.Centers[b].Y - bh / 2 + 0.5 || r.Centers[b].Y + bh / 2 <= r.Centers[a].Y - ah / 2 + 0.5;
          Assert.True(sepX || sepY, $"components {a} and {b} overlap");
        }
    }

    [Fact]
    public void Layout_is_deterministic()
    {
      var nodes = new[] { 1, 2, 3, 4, 5, 6 }.Select(i => N(i)).ToList();
      var edges = new List<LayoutEdge> { E(1, 2), E(2, 3), E(3, 4), E(2, 5), E(5, 6) };
      var macro = new Dictionary<int, int> { [1] = 0, [2] = 0, [3] = 1, [4] = 1, [5] = 2, [6] = 2 };
      var hints = new List<(string, IReadOnlyList<int>)> { Hint("stack", 1, 2) };

      var a = ClusterLayout.Layout(nodes, edges, macro, hints);
      var b = ClusterLayout.Layout(nodes, edges, macro, hints);
      foreach (var kv in a.Centers)
      {
        Assert.Equal(kv.Value.X, b.Centers[kv.Key].X);
        Assert.Equal(kv.Value.Y, b.Centers[kv.Key].Y);
      }
    }

    [Fact]
    public void Inserted_boundary_params_land_in_their_own_cluster()
    {
      // Reproduce the best-practice augmented graph: cluster 0 {1,2,3 chain} feeds cluster 1 {4}, routed through
      // boundary params - pOut(10) in cluster 0 (fed by 3), pIn(11) in cluster 1 (feeds 4), with 10->11 the
      // crossing. The params are connected by INTRA-cluster edges (3->10 in c0, 11->4 in c1), so each must land
      // WITH its cluster, not forest-packed off in a corner (the live-canvas bug: params ~15000px from cluster).
      var nodes = new List<LayoutNode> { N(1), N(2), N(3), N(4), N(10, 50, 24), N(11, 50, 24) };
      var edges = new List<LayoutEdge> { E(1, 2), E(2, 3), E(3, 10), E(10, 11), E(11, 4) };
      var macro = new Dictionary<int, int> { [1] = 0, [2] = 0, [3] = 0, [10] = 0, [4] = 1, [11] = 1 };

      var r = ClusterLayout.Layout(nodes, edges, macro, null);

      double Cx(IEnumerable<int> ids) => ids.Average(i => r.Centers[i].X);
      double Cy(IEnumerable<int> ids) => ids.Average(i => r.Centers[i].Y);
      double Dist(int id, IEnumerable<int> ids)
      {
        var dx = r.Centers[id].X - Cx(ids); var dy = r.Centers[id].Y - Cy(ids);
        return Math.Sqrt(dx * dx + dy * dy);
      }

      // pOut(10) belongs to cluster 0 - it must be far closer to {1,2,3} than to cluster 1 {4}, and within a
      // sane intra-cluster distance (a few hundred px), not a forest-pack offset.
      Assert.True(Dist(10, new[] { 1, 2, 3 }) < Dist(10, new[] { 4 }), "pOut drifted away from its own cluster 0");
      Assert.True(Dist(11, new[] { 4 }) < Dist(11, new[] { 1, 2, 3 }), "pIn drifted away from its own cluster 1");
      Assert.True(Dist(10, new[] { 1, 2, 3 }) < 600, $"pOut is {Dist(10, new[] { 1, 2, 3 }):F0}px from cluster 0 - forest-packed, not in the block");
    }

    [Fact]
    public void Cyclic_clusters_order_by_dominant_wire_count()
    {
      // Clusters 0 and 1 mutually depend - 0->1 by TWO wires, 1->0 by ONE. A cyclic cluster graph has no strict
      // left-to-right order, so one link must reverse; it must be the THINNER (1->0), so the dominant dataflow
      // puts cluster 0 left of cluster 1.
      var nodes = new[] { 1, 2, 3, 4 }.Select(i => N(i)).ToList();
      var edges = new List<LayoutEdge>
      {
        E(1, 2),            // intra cluster 0
        E(3, 4),            // intra cluster 1
        E(2, 3), E(1, 4),   // 0 -> 1 (two wires - dominant)
        E(4, 1),            // 1 -> 0 (one wire - the thin feedback)
      };
      var macro = new Dictionary<int, int> { [1] = 0, [2] = 0, [3] = 1, [4] = 1 };

      var r = ClusterLayout.Layout(nodes, edges, macro, null);
      var c0 = new[] { 1, 2 }.Average(i => r.Centers[i].X);
      var c1 = new[] { 3, 4 }.Average(i => r.Centers[i].X);
      Assert.True(c0 < c1, "dominant 0->1 (2 wires) must order cluster 0 left of cluster 1, reversing only the thin 1->0");
      // WP-4.3: reversing (not dropping) the thin feedback keeps the pair ADJACENT - one flow step apart in y,
      // not drifted into separate forest blocks.
      var y0 = new[] { 1, 2 }.Average(i => r.Centers[i].Y);
      var y1 = new[] { 3, 4 }.Average(i => r.Centers[i].Y);
      Assert.True(Math.Abs(y0 - y1) < 200, "the cyclic pair should stay adjacent, not drift to separate layers");
    }

    [Fact]
    public void Disconnected_pipelines_do_not_overlap_and_hints_cannot_union_across_components()
    {
      // WP-4.2: two INDEPENDENT pipelines (no wire between them) - cluster 0->1 and cluster 2->3. Forest packing
      // can stack them at the same x-range; without the component guard, RefineVertical would merge same-x
      // clusters ACROSS the two pipelines and re-stack them on top of each other, and a hint could union them.
      var nodes = new[] { 1, 2, 3, 4 }.Select(i => N(i, 80, 40)).ToList();
      var edges = new List<LayoutEdge> { E(1, 2), E(3, 4) };   // 1->2 in one pipeline, 3->4 in the other; no cross edge
      var macro = new Dictionary<int, int> { [1] = 0, [2] = 1, [3] = 2, [4] = 3 };
      // a hint tries to stack cluster 0 (pipeline A) with cluster 2 (pipeline B) - it must NOT survive.
      var hints = new List<(string, IReadOnlyList<int>)> { Hint("stack", 0, 2) };
      // a tall target aspect forces the packer to STACK the two pipelines (share an x-range) - where the bug bit.
      var r = ClusterLayout.Layout(nodes, edges, macro, hints, new LayoutOptions { TargetAspect = 0.05 });

      Assert.Equal(0, r.PeerPairs);   // the cross-component hint produced no surviving peer pair

      // zero group-rectangle overlap
      for (var i = 0; i < r.Clusters.Count; i++)
        for (var j = i + 1; j < r.Clusters.Count; j++)
        {
          var a = r.Clusters[i]; var b = r.Clusters[j];
          var sepX = a.MaxX <= b.MinX + 0.5 || b.MaxX <= a.MinX + 0.5;
          var sepY = a.MaxY <= b.MinY + 0.5 || b.MaxY <= a.MinY + 0.5;
          Assert.True(sepX || sepY, $"cluster boxes {a.Id} and {b.Id} overlap");
        }
    }
  }
}
