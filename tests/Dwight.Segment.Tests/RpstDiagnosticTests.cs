using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using Xunit.Abstractions;

namespace Dwight.Segment.Tests
{
  /// <summary>
  /// EVIDENCE-GATHERING for the "always one group" bug (systematic debugging, Phase 1). These tests measure
  /// the RAW RPST (matched-region count + depth) before granularity descent, so we can see WHICH half fails:
  /// no regions match (dominance collapse) vs. regions match but the descent does not walk them.
  /// </summary>
  public class RpstDiagnosticTests
  {
    private readonly ITestOutputHelper _out;
    public RpstDiagnosticTests(ITestOutputHelper output) { _out = output; }

    private static Guid G(int i) => new Guid(i, 0, 0, new byte[8]);
    private static SegNode N(int id, bool leaf = false) => new SegNode(id, G(id), leaf);
    private static SegEdge E(int a, int b) => new SegEdge(a, b);

    private static (int regions, int depth, int rootInterior) RpstOf(List<SegNode> nodes, List<SegEdge> edges)
    {
      var g = DataflowGraph.Build(new SegInput(nodes, edges), new HashSet<int>());
      var idom = Dominators.Compute(g, reverse: false);
      var ipdom = Dominators.Compute(g, reverse: true);
      var root = Rpst.Build(g, idom, ipdom);
      return (Rpst.CountRegions(root), Rpst.MaxDepth(root), root.Interior.Count);
    }

    private static (List<SegNode>, List<SegEdge>) TwoDiamonds()
    {
      var nodes = Enumerable.Range(1, 8).Select(i => N(i)).ToList();
      var edges = new List<SegEdge> { E(1, 2), E(1, 3), E(2, 4), E(3, 4), E(4, 5), E(5, 6), E(5, 7), E(6, 8), E(7, 8) };
      return (nodes, edges);
    }

    [Fact]
    public void Clean_branching_has_matched_regions()
    {
      var (nodes, edges) = TwoDiamonds();
      var (regions, depth, root) = RpstOf(nodes, edges);
      _out.WriteLine($"clean two diamonds: matched regions = {regions}, depth = {depth}, root interior = {root}");
      Assert.True(regions >= 2, "clean branching should yield matched SESE regions");
    }

    [Fact]
    public void A_midchain_panel_tap_does_not_destroy_the_matched_regions()
    {
      // The real-file pattern: a panel (leaf) tapping a mid-stream node. It is output-less, so it adds an edge
      // to the super-sink, creating a path that bypasses the diamond's reconvergence. HYPOTHESIS: this drops
      // ipdom to Sink and collapses the matched regions to zero (only the root), i.e. "one group".
      var (nodes, edges) = TwoDiamonds();
      nodes.Add(N(99, leaf: true));
      edges.Add(E(2, 99));   // node 2 -> panel -> (super-sink)
      var (regions, depth, root) = RpstOf(nodes, edges);
      _out.WriteLine($"two diamonds + 1 panel tap: matched regions = {regions}, depth = {depth}, root interior = {root}");
      Assert.True(regions >= 2, "a single panel tap must not collapse all SESE structure");
    }

    [Fact]
    public void Four_disconnected_pipelines_should_be_four_groups_not_one()
    {
      // The real-file pattern that I suspect collapses: several independent pipelines, each a chain with no
      // clean SESE sub-structure. They share no wires, so they MUST land in different groups.
      var nodes = new List<SegNode>();
      var edges = new List<SegEdge>();
      for (var p = 0; p < 4; p++)
      {
        var b = p * 10 + 1;
        for (var i = 0; i < 10; i++) nodes.Add(N(b + i));
        for (var i = 0; i < 9; i++) edges.Add(E(b + i, b + i + 1));
      }
      var r = new Segmenter().Segment(new SegInput(nodes, edges));
      _out.WriteLine($"4 disconnected chains -> groups = {r.Macro.Values.Distinct().Count()}");
      Assert.True(r.Macro.Values.Distinct().Count() >= 4, "four independent pipelines must not be one group");
    }

    [Fact]
    public void A_three_in_three_out_cluster_is_one_region()
    {
      // Tharun's concrete case: a real GH group is many-in-many-out (3 labelled input params + 3 output
      // params, one logical unit), NOT a one-edge-in-one-edge-out tube. Prove the segmenter sees it as ONE
      // group via the virtual super-source/sink, not as fragments.
      var nodes = Enumerable.Range(1, 12).Select(i => N(i, leaf: false)).ToList();
      var edges = new List<SegEdge>
      {
        E(1, 4), E(2, 4), E(3, 5),              // 3 inputs feed in
        E(4, 6), E(5, 6), E(6, 7), E(6, 8),     // internal logic
        E(7, 9), E(8, 9),
        E(7, 10), E(8, 11), E(9, 12),           // 3 outputs come out
      };
      var r = new Segmenter().Segment(new SegInput(nodes, edges));
      _out.WriteLine($"3-in-3-out cluster: groups = {r.Macro.Values.Distinct().Count()}, " +
                     $"matched SESE regions = {r.Diagnostics.MatchedRegions}, weak components = {r.Diagnostics.WeakComponents}");
      Assert.Equal(1, r.Macro.Values.Distinct().Count());   // one logical unit -> one group
    }
  }
}
