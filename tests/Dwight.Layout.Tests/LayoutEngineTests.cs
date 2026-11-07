using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Dwight.Layout.Tests
{
  /// <summary>
  /// Headless tests for the Phase 6 layout engine (WP-A). They guard the properties the phase doc makes
  /// non-negotiable - "a layout done wrong is worse than no tool": no overlap on real-varied sizes,
  /// left-to-right dataflow, deterministic byte-identical re-runs, 2D forest packing of disconnected
  /// threads, and the wire-fade thresholds. No Rhino, no MSAGL types leak into the assertions.
  /// </summary>
  public class LayoutEngineTests
  {
    private static LayoutEngine Engine() => LayoutEngine.Msagl();

    /// <summary>A deterministic guid for an id, sorting in id order (the int field dominates Guid.CompareTo)
    /// so ordering-by-key matches ordering-by-id in these fixtures - while still exercising the guid path.</summary>
    private static Guid G(int i) => new Guid(i, 0, 0, new byte[8]);

    // ---- builders -----------------------------------------------------------------------------------

    /// <summary>A chain a -> b -> c -> ... with varied sizes (mimicking real components).</summary>
    private static (List<LayoutNode>, List<LayoutEdge>) Chain(int n)
    {
      var nodes = new List<LayoutNode>();
      var edges = new List<LayoutEdge>();
      for (var i = 1; i <= n; i++) nodes.Add(new LayoutNode(i, G(i), 40 + (i % 5) * 20, 24 + (i % 3) * 18));
      for (var i = 1; i < n; i++) edges.Add(new LayoutEdge(i, i + 1));
      return (nodes, edges);
    }

    /// <summary>A diamond: 1 -> 2, 1 -> 3, 2 -> 4, 3 -> 4 (the WP-0 known graph, varied sizes).</summary>
    private static (List<LayoutNode>, List<LayoutEdge>) Diamond()
    {
      var nodes = new List<LayoutNode>
      {
        new LayoutNode(1, G(1), 60, 28), new LayoutNode(2, G(2), 90, 44),
        new LayoutNode(3, G(3), 70, 28), new LayoutNode(4, G(4), 110, 60),
      };
      var edges = new List<LayoutEdge>
      {
        new LayoutEdge(1, 2), new LayoutEdge(1, 3), new LayoutEdge(2, 4), new LayoutEdge(3, 4),
      };
      return (nodes, edges);
    }

    /// <summary>A wide, deliberately messy DAG with several depths, fan-out and fan-in and varied sizes -
    /// the closest synthetic stand-in for a real definition's overlap stress.</summary>
    private static (List<LayoutNode>, List<LayoutEdge>) MessyDag()
    {
      var rndSizes = new (double w, double h)[]
      { (50,24),(120,40),(64,60),(180,30),(44,44),(90,80),(70,28),(150,52),(40,20),(110,36),(60,64),(96,48) };
      var nodes = new List<LayoutNode>();
      for (var i = 1; i <= 12; i++) nodes.Add(new LayoutNode(i, G(i), rndSizes[i - 1].w, rndSizes[i - 1].h));
      var edges = new List<LayoutEdge>
      {
        new LayoutEdge(1,2), new LayoutEdge(1,3), new LayoutEdge(2,4), new LayoutEdge(3,4),
        new LayoutEdge(4,5), new LayoutEdge(4,6), new LayoutEdge(5,7), new LayoutEdge(6,7),
        new LayoutEdge(7,8), new LayoutEdge(2,8), new LayoutEdge(8,9), new LayoutEdge(8,10),
        new LayoutEdge(9,11), new LayoutEdge(10,11), new LayoutEdge(11,12), new LayoutEdge(3,12),
      };
      return (nodes, edges);
    }

    private static Dictionary<int, LayoutNode> SizeMap(IEnumerable<LayoutNode> nodes) => nodes.ToDictionary(n => n.Id);

    /// <summary>Do two node boxes overlap (with a tiny tolerance for exact edge-touching)?</summary>
    private static bool BoxesOverlap(PointD ca, LayoutNode na, PointD cb, LayoutNode nb)
    {
      const double eps = 0.5;
      var ax0 = ca.X - na.Width / 2; var ax1 = ca.X + na.Width / 2;
      var ay0 = ca.Y - na.Height / 2; var ay1 = ca.Y + na.Height / 2;
      var bx0 = cb.X - nb.Width / 2; var bx1 = cb.X + nb.Width / 2;
      var by0 = cb.Y - nb.Height / 2; var by1 = cb.Y + nb.Height / 2;
      var sepX = ax1 <= bx0 + eps || bx1 <= ax0 + eps;
      var sepY = ay1 <= by0 + eps || by1 <= ay0 + eps;
      return !(sepX || sepY);
    }

    private static void AssertNoOverlap(LayoutResult r, Dictionary<int, LayoutNode> sizes)
    {
      var ids = r.Centers.Keys.ToList();
      for (var i = 0; i < ids.Count; i++)
        for (var j = i + 1; j < ids.Count; j++)
        {
          var a = ids[i]; var b = ids[j];
          Assert.False(BoxesOverlap(r.Centers[a], sizes[a], r.Centers[b], sizes[b]),
            $"nodes {a} and {b} overlap at {r.Centers[a].X},{r.Centers[a].Y} / {r.Centers[b].X},{r.Centers[b].Y}");
        }
    }

    // ---- determinism --------------------------------------------------------------------------------

    [Fact]
    public void Layout_is_byte_identical_across_runs()
    {
      var (nodes, edges) = MessyDag();
      var first = Engine().Layout(nodes, edges);
      var second = Engine().Layout(nodes, edges);

      Assert.Equal(first.Centers.Count, second.Centers.Count);
      foreach (var kv in first.Centers)
      {
        Assert.True(second.Centers.TryGetValue(kv.Key, out var p), $"missing node {kv.Key} on re-run");
        Assert.Equal(kv.Value.X, p.X);   // exact: a deterministic layout reproduces to the bit
        Assert.Equal(kv.Value.Y, p.Y);
      }
    }

    [Fact]
    public void Determinism_is_independent_of_input_node_ordering()
    {
      var (nodes, edges) = MessyDag();
      var shuffled = nodes.OrderByDescending(n => n.Id).ToList();   // reverse the input order
      var a = Engine().Layout(nodes, edges);
      var b = Engine().Layout(shuffled, edges);
      foreach (var kv in a.Centers)
      {
        Assert.True(b.Centers.TryGetValue(kv.Key, out var p));
        Assert.Equal(kv.Value.X, p.X);
        Assert.Equal(kv.Value.Y, p.Y);
      }
    }

    [Fact]
    public void Per_guid_layout_of_a_connected_block_is_stable_when_ids_are_reassigned()
    {
      // Per-guid stability for a single connected component (MSAGL is order-insensitive here, so this holds
      // even on id-ordered code - it documents the property, it is NOT the bug-catcher; see the multi-block
      // test below for that).
      var (nodes, edges) = MessyDag();                 // ids 1..12, guids G(1..12)
      var first = Engine().Layout(nodes, edges);

      int Remap(int id) => 13 - id;                    // reverse the id assignment; KEEP each node's guid
      var nodes2 = nodes.Select(n => new LayoutNode(Remap(n.Id), n.Key, n.Width, n.Height)).ToList();
      var edges2 = edges.Select(e => new LayoutEdge(Remap(e.FromId), Remap(e.ToId))).ToList();
      var second = Engine().Layout(nodes2, edges2);

      AssertSamePerGuid(nodes, first, nodes2, second);
    }

    [Fact]
    public void Multi_block_packing_is_stable_when_ids_are_reassigned()
    {
      // THE WP-A runtime bug reproduced. Laying out moves pivots, so the next DwightGraph snapshot re-sorts
      // by pivot and gives the SAME components DIFFERENT short-int ids. With block-packing ordered by min
      // *id*, the blocks then pack in a different order and land in different positions - the layout cycles.
      // Three identical-shaped, disconnected blocks (so heights tie and the packing ORDER is what decides
      // placement). Reassigning ids reverses the min-id order but leaves the min-KEY order untouched, so the
      // per-guid positions must be identical. Fails on min-id packing; passes on min-key packing.
      LayoutNode N(int id, int keyIndex) => new LayoutNode(id, G(keyIndex), 80, 30);
      var nodes = new List<LayoutNode>
      {
        N(1, 1), N(2, 2),    // block A (guids G1,G2)
        N(3, 3), N(4, 4),    // block B (guids G3,G4)
        N(5, 5), N(6, 6),    // block C (guids G5,G6)
      };
      var edges = new List<LayoutEdge> { new LayoutEdge(1, 2), new LayoutEdge(3, 4), new LayoutEdge(5, 6) };
      var first = Engine().Layout(nodes, edges);
      Assert.Equal(3, first.BlockCount);

      // Reassign ids so the min-id-per-block order reverses (A:5,6  B:3,4  C:1,2) while guids are unchanged.
      var nodes2 = new List<LayoutNode>
      {
        new LayoutNode(5, G(1), 80, 30), new LayoutNode(6, G(2), 80, 30),   // block A keeps G1,G2, now ids 5,6
        new LayoutNode(3, G(3), 80, 30), new LayoutNode(4, G(4), 80, 30),   // block B unchanged
        new LayoutNode(1, G(5), 80, 30), new LayoutNode(2, G(6), 80, 30),   // block C keeps G5,G6, now ids 1,2
      };
      var edges2 = new List<LayoutEdge> { new LayoutEdge(5, 6), new LayoutEdge(3, 4), new LayoutEdge(1, 2) };
      var second = Engine().Layout(nodes2, edges2);

      AssertSamePerGuid(nodes, first, nodes2, second);
    }

    /// <summary>Assert two layout runs place every node at the same canvas centre, matched by stable key
    /// (guid) rather than by id - the id may differ between runs by design.</summary>
    private static void AssertSamePerGuid(List<LayoutNode> a, LayoutResult ra, List<LayoutNode> b, LayoutResult rb)
    {
      var byKeyA = a.ToDictionary(n => n.Key, n => ra.Centers[n.Id]);
      var byKeyB = b.ToDictionary(n => n.Key, n => rb.Centers[n.Id]);
      Assert.Equal(byKeyA.Count, byKeyB.Count);
      foreach (var kv in byKeyA)
      {
        Assert.True(byKeyB.TryGetValue(kv.Key, out var p), "a guid went missing across the re-id run");
        Assert.Equal(kv.Value.X, p.X);   // byte-identical per guid, regardless of id churn
        Assert.Equal(kv.Value.Y, p.Y);
      }
    }

    // ---- no overlap (the make-or-break property) ----------------------------------------------------

    [Fact]
    public void Diamond_has_no_overlap()
    {
      var (nodes, edges) = Diamond();
      var r = Engine().Layout(nodes, edges);
      AssertNoOverlap(r, SizeMap(nodes));
    }

    [Fact]
    public void Messy_dag_with_varied_sizes_has_no_overlap()
    {
      var (nodes, edges) = MessyDag();
      var r = Engine().Layout(nodes, edges);
      AssertNoOverlap(r, SizeMap(nodes));
    }

    [Fact]
    public void Long_chain_has_no_overlap()
    {
      var (nodes, edges) = Chain(40);
      var r = Engine().Layout(nodes, edges);
      AssertNoOverlap(r, SizeMap(nodes));
    }

    // ---- left-to-right dataflow ---------------------------------------------------------------------

    [Fact]
    public void Chain_flows_left_to_right()
    {
      var (nodes, edges) = Chain(6);
      var r = Engine().Layout(nodes, edges);
      for (var i = 1; i < 6; i++)
        Assert.True(r.Centers[i].X < r.Centers[i + 1].X,
          $"node {i} (x={r.Centers[i].X}) should be left of node {i + 1} (x={r.Centers[i + 1].X})");
    }

    [Fact]
    public void Diamond_source_is_left_of_sink()
    {
      var (nodes, edges) = Diamond();
      var r = Engine().Layout(nodes, edges);
      Assert.True(r.Centers[1].X < r.Centers[4].X, "the source (1) must be left of the sink (4)");
      // the two mid nodes (2,3) sit between source and sink on the depth axis
      Assert.True(r.Centers[1].X < r.Centers[2].X && r.Centers[2].X < r.Centers[4].X);
      Assert.True(r.Centers[1].X < r.Centers[3].X && r.Centers[3].X < r.Centers[4].X);
    }

    // ---- 2D forest packing --------------------------------------------------------------------------

    [Fact]
    public void Disconnected_threads_pack_as_separate_blocks_without_overlap()
    {
      // Two independent chains (1-2-3) and (4-5-6) - a forest of two weakly-connected components.
      var nodes = new List<LayoutNode>();
      for (var i = 1; i <= 6; i++) nodes.Add(new LayoutNode(i, G(i), 80, 30));
      var edges = new List<LayoutEdge>
      { new LayoutEdge(1,2), new LayoutEdge(2,3), new LayoutEdge(4,5), new LayoutEdge(5,6) };

      var r = Engine().Layout(nodes, edges);
      Assert.Equal(2, r.BlockCount);
      AssertNoOverlap(r, SizeMap(nodes));   // the two blocks must not collide after packing
    }

    [Fact]
    public void Single_node_lays_out_as_one_block()
    {
      var nodes = new List<LayoutNode> { new LayoutNode(1, G(1), 50, 50) };
      var r = Engine().Layout(nodes, Array.Empty<LayoutEdge>());
      Assert.Equal(1, r.BlockCount);
      Assert.Single(r.Centers);
    }

    [Fact]
    public void Isolated_nodes_each_form_a_block()
    {
      var nodes = new List<LayoutNode> { new LayoutNode(1, G(1), 50, 50), new LayoutNode(2, G(2), 50, 50), new LayoutNode(3, G(3), 50, 50) };
      var r = Engine().Layout(nodes, Array.Empty<LayoutEdge>());
      Assert.Equal(3, r.BlockCount);
      AssertNoOverlap(r, SizeMap(nodes));
    }

    [Fact]
    public void Empty_input_yields_empty_result()
    {
      var r = Engine().Layout(new List<LayoutNode>(), new List<LayoutEdge>());
      Assert.Empty(r.Centers);
      Assert.Equal(0, r.BlockCount);
    }

    // ---- wire-fade thresholds -----------------------------------------------------------------------

    // expected is the WireDisplay underlying value (Default=0, Faint=1, Hidden=2) so the public test
    // signature does not expose the internal enum.
    [Theory]
    [InlineData(0, 0)]
    [InlineData(499, 0)]
    [InlineData(500, 1)]
    [InlineData(999, 1)]
    [InlineData(1000, 2)]
    [InlineData(5000, 2)]
    public void Fade_thresholds_map_length_to_display(double length, int expected)
    {
      Assert.Equal(expected, (int)LayoutEngine.FadeFor(length, LayoutOptions.Default));
    }

    [Fact]
    public void Short_wires_get_no_fade_recommendation()
    {
      var (nodes, edges) = Diamond();   // a tight graph: every wire is short
      var r = Engine().Layout(nodes, edges);
      Assert.All(r.Fades, f => Assert.NotEqual(WireDisplay.Default, f.Display)); // only non-default emitted
      Assert.Empty(r.Fades);   // and on a tight diamond there should be none
    }

    [Fact]
    public void A_far_spanning_wire_is_recommended_faint_or_hidden()
    {
      // A "skip" edge from the first node of a long chain directly to the last forces a long span.
      var (nodes, edges) = Chain(30);
      edges.Add(new LayoutEdge(1, 30));
      var r = Engine().Layout(nodes, edges);
      var skip = r.Fades.FirstOrDefault(f => f.FromId == 1 && f.ToId == 30);
      Assert.True(skip.Display == WireDisplay.Faint || skip.Display == WireDisplay.Hidden,
        $"the 1->30 skip wire should fade (length {skip.Length})");
    }
  }
}
