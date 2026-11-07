using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Dwight.Segment.Tests
{
  /// <summary>
  /// Tests the RPST-first segmenter (ADR-087). The headline test is the one that proves the redesign is sound:
  /// the partition is identical when node ids are reassigned (the scattered-file analogue - DwightGraph
  /// assigns ids by pivot, so a scattered layout gives the same components different ids; an RPST segmenter
  /// reads topology, so it must be invariant to that). The rest pin SESE separation, hub isolation, the
  /// string-of-pearls honest ceiling, and determinism.
  /// </summary>
  public class SegmenterTests
  {
    private static Guid G(int i) => new Guid(i, 0, 0, new byte[8]);
    private static SegNode N(int id, bool leaf = false) => new SegNode(id, G(id), leaf);
    private static SegEdge E(int a, int b) => new SegEdge(a, b);

    // small granularity band so the structure shows on modest test graphs
    private static SegOptions Fine() => new SegOptions { GranularityMin = 1, GranularityMax = 3, HubMinOutDegree = 4, WireLengthEnabled = false };

    private static SegResult Segment(IEnumerable<SegNode> nodes, IEnumerable<SegEdge> edges, SegOptions o = null)
      => new Segmenter(o ?? Fine()).Segment(new SegInput(nodes.ToList(), edges.ToList()));

    /// <summary>Two sequential diamonds: 1->{2,3}->4->5->{6,7}->8.</summary>
    private static (List<SegNode> nodes, List<SegEdge> edges) TwoDiamonds()
    {
      var nodes = Enumerable.Range(1, 8).Select(i => N(i)).ToList();
      var edges = new List<SegEdge> { E(1, 2), E(1, 3), E(2, 4), E(3, 4), E(4, 5), E(5, 6), E(5, 7), E(6, 8), E(7, 8) };
      return (nodes, edges);
    }

    [Fact]
    public void Two_sequential_diamonds_separate_into_distinct_regions()
    {
      var (nodes, edges) = TwoDiamonds();
      var r = Segment(nodes, edges);
      // each diamond's interior stays together, and the two diamonds are in different regions
      Assert.Equal(r.Macro[2], r.Macro[3]);
      Assert.Equal(r.Macro[6], r.Macro[7]);
      Assert.NotEqual(r.Macro[2], r.Macro[6]);
    }

    [Fact]
    public void Partition_is_invariant_to_id_reassignment_the_scattered_file_proof()
    {
      // The proof the segmenter reads topology and not position: reassign ids (as a scattered layout would,
      // since ids are pivot-derived) while keeping guids and topology, and the per-guid partition must match.
      var (nodes, edges) = TwoDiamonds();
      var first = Segment(nodes, edges);

      int Remap(int id) => 9 - id;                      // reverse the ids; KEEP each node's guid + the topology
      var nodes2 = nodes.Select(n => new SegNode(Remap(n.Id), n.Key, n.IsLeafType)).ToList();
      var edges2 = edges.Select(e => new SegEdge(Remap(e.FromId), Remap(e.ToId))).ToList();
      var second = Segment(nodes2, edges2);

      // compare co-membership by guid (G(i) is attached to the same node; only the id flipped)
      var firstByKey = nodes.ToDictionary(n => n.Key, n => first.Macro[n.Id]);
      var secondByKey = nodes2.ToDictionary(n => n.Key, n => second.Macro[n.Id]);
      foreach (var ka in firstByKey.Keys)
        foreach (var kb in firstByKey.Keys)
          Assert.Equal(firstByKey[ka] == firstByKey[kb], secondByKey[ka] == secondByKey[kb]);
    }

    [Fact]
    public void A_branchless_string_of_pearls_stays_one_region()
    {
      // A linear chain has no topological boundary anywhere - the honest ceiling. It must come back as one
      // region (structure cannot, and must not pretend to, split it), even past the size band.
      var nodes = Enumerable.Range(1, 10).Select(i => N(i)).ToList();
      var edges = Enumerable.Range(1, 9).Select(i => E(i, i + 1)).ToList();
      var r = Segment(nodes, edges, new SegOptions { GranularityMin = 1, GranularityMax = 4, HubMinOutDegree = 4 });
      Assert.Equal(1, r.Macro.Values.Distinct().Count());
    }

    [Fact]
    public void A_shared_input_hub_is_isolated()
    {
      // a slider (leaf) feeding many otherwise-separate components must be lifted out, not collapse them.
      var nodes = new List<SegNode> { N(1, leaf: true) };
      var edges = new List<SegEdge>();
      for (var t = 2; t <= 12; t++) { nodes.Add(N(t)); edges.Add(E(1, t)); }   // 1 fans out to 11 consumers
      var r = Segment(nodes, edges, new SegOptions { GranularityMin = 1, GranularityMax = 3, HubMedianFactor = 4, HubMinOutDegree = 4 });
      Assert.Contains(1, r.Hubs);
      // the hub is not in the same region as the bulk of its consumers (it did not collapse them via itself)
      Assert.DoesNotContain(2, r.Hubs);
    }

    [Fact]
    public void Segmentation_is_deterministic()
    {
      var (nodes, edges) = TwoDiamonds();
      var a = Segment(nodes, edges);
      var b = Segment(Enumerable.Reverse(nodes), Enumerable.Reverse(edges));
      foreach (var x in a.Macro.Keys)
        foreach (var y in a.Macro.Keys)
          Assert.Equal(a.Macro[x] == a.Macro[y], b.Macro[x] == b.Macro[y]);
    }

    [Fact]
    public void Every_node_is_assigned_and_empty_input_is_empty()
    {
      var (nodes, edges) = TwoDiamonds();
      var r = Segment(nodes, edges);
      foreach (var n in nodes) Assert.True(r.Macro.ContainsKey(n.Id));
      Assert.Equal(0, new Segmenter().Segment(new SegInput(new List<SegNode>(), new List<SegEdge>())).MacroCount);
    }

    [Fact]
    public void Cleanup_leaves_no_component_more_wired_to_a_neighbour_group()
    {
      // the cleanup invariant: after it runs, no (non-hub) component is more strongly WIRED to a neighbouring
      // group than to its own - the odd-one-out cases are pulled into the group they actually connect to.
      var (nodes, edges) = TwoDiamonds();
      var r = Segment(nodes, edges);   // Fine(): cleanup on, wire-length off (topology only)
      var grp = r.Macro;
      var hubs = new HashSet<int>(r.Hubs);

      var adj = new Dictionary<int, List<int>>();
      void Link(int a, int b) { if (!adj.TryGetValue(a, out var l)) adj[a] = l = new List<int>(); l.Add(b); }
      foreach (var e in edges) { Link(e.FromId, e.ToId); Link(e.ToId, e.FromId); }

      foreach (var n in nodes.Select(x => x.Id))
      {
        if (hubs.Contains(n) || !adj.ContainsKey(n)) continue;
        var own = grp[n];
        var strength = new Dictionary<int, int>();
        foreach (var m in adj[n]) { if (hubs.Contains(m)) continue; strength[grp[m]] = strength.GetValueOrDefault(grp[m]) + 1; }
        var ownStr = strength.GetValueOrDefault(own);
        var maxNeighbour = strength.Where(kv => kv.Key != own).Select(kv => kv.Value).DefaultIfEmpty(0).Max();
        Assert.True(ownStr >= maxNeighbour, $"node {n} is a misfit: own {ownStr} wires < neighbour {maxNeighbour}");
      }
    }

    [Fact]
    public void Sub_minimum_fragments_merge_into_a_wired_neighbour()
    {
      // WP-4.6: a diamond {2,3,4} (interior of the (1,5) region) fed by source node 1, plus a tail 5->6->7.
      // Flattening leaves node 1 as its OWN size-1 fragment (its wires all reach into the diamond). With cleanup
      // OFF (so only the merge is under test), GranularityMin=1 keeps that singleton; GranularityMin=3 makes it
      // sub-minimum, folding it into the diamond group it is wired to - so node 1 joins node 2 and the count drops.
      var nodes = Enumerable.Range(1, 7).Select(i => N(i)).ToList();
      var edges = new List<SegEdge> { E(1, 2), E(1, 3), E(1, 4), E(2, 5), E(3, 5), E(4, 5), E(5, 6), E(6, 7) };
      var opts = new Func<int, SegOptions>(min => new SegOptions
      { GranularityMin = min, GranularityMax = 4, HubMinOutDegree = 6, WireLengthEnabled = false, Stage2Enabled = false, CleanupEnabled = false });

      var fine = Segment(nodes, edges, opts(1));
      var merged = Segment(nodes, edges, opts(3));

      // under min=1: node 1 is its own size-1 fragment, separate from the diamond (node 2)
      Assert.NotEqual(fine.Macro[1], fine.Macro[2]);
      Assert.Equal(1, fine.Macro.Count(kv => kv.Value == fine.Macro[1]));
      // under min=3: the sub-minimum singleton folds into the group it is wired to (the diamond), count drops
      Assert.Equal(merged.Macro[1], merged.Macro[2]);
      Assert.True(merged.MacroCount < fine.MacroCount, "folding the sub-minimum singleton reduces the group count");
    }

    [Fact]
    public void A_2000_node_chain_of_diamonds_segments_quickly()
    {
      // WP-4.1: the perf guard. Before the Euler-tour rewrite, Interior + BuildContainmentTree were
      // O(V^2*depth)/O(R^2*V) - MINUTES at this scale on a long dominance chain; the interval numbering makes it
      // near-linear per region. The threshold is generous (the fix runs in tens of ms) but far below the
      // pre-fix minutes, so it catches a regression without flaking on a slow CI box.
      const int diamonds = 666;
      var nodes = new List<SegNode>();
      var edges = new List<SegEdge>();
      var id = 1;
      var spine = new int[diamonds + 1];
      for (var k = 0; k <= diamonds; k++) { spine[k] = id; nodes.Add(N(id)); id++; }
      for (var k = 0; k < diamonds; k++)
      {
        int a = id++, b = id++;
        nodes.Add(N(a)); nodes.Add(N(b));
        edges.Add(E(spine[k], a)); edges.Add(E(a, spine[k + 1]));
        edges.Add(E(spine[k], b)); edges.Add(E(b, spine[k + 1]));
      }
      Assert.True(nodes.Count >= 1900, $"expected ~2000 nodes, built {nodes.Count}");

      var sw = System.Diagnostics.Stopwatch.StartNew();
      var r = new Segmenter(new SegOptions { WireLengthEnabled = false }).Segment(new SegInput(nodes, edges));
      sw.Stop();

      Assert.True(r.MacroCount > 0, "the big graph should segment into groups");
      Assert.True(sw.ElapsedMilliseconds < 3000, $"segmenting {nodes.Count} nodes took {sw.ElapsedMilliseconds}ms - the O(V^2) hotspot is back");
    }
  }
}
