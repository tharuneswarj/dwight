using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Dwight.Segment.Tests
{
  /// <summary>
  /// Tests the two Stage-2 boundary terms (param-on-the-edge and entry-crossing). The value of the terms is
  /// the rigid-file ARI/precision (the runtime benchmark gate); these tests pin the parts that must be right
  /// for that to be measured honestly: the entry-detection core, the OVER-FIRE GUARD (a coherent tangle is
  /// not shredded), the penalty mechanism, and scattered==tidy through both terms (topological -> invariant).
  /// </summary>
  public class BoundaryTermsTests
  {
    private static Guid G(int i) => new Guid(i, 0, 0, new byte[8]);
    private static SegNode N(int id, bool leaf = false, bool param = false) => new SegNode(id, G(id), leaf, param);
    private static SegEdge E(int a, int b) => new SegEdge(a, b);

    private static void Dense(List<SegEdge> edges, int lo, int hi)
    {
      for (var i = lo; i <= hi; i++) for (var j = i + 1; j <= hi; j++) edges.Add(E(i, j));
    }

    // ---- the entry-crossing core ---------------------------------------------------------------------

    [Fact]
    public void Shares_real_post_dominator_distinguishes_crossing_from_internal_edges()
    {
      // two independent chains 1->2->3 and 4->5->6 (joined only at the virtual source/sink)
      var nodes = new[] { 1, 2, 3, 4, 5, 6 }.Select(i => N(i)).ToList();
      var edges = new List<SegEdge> { E(1, 2), E(2, 3), E(4, 5), E(5, 6) };
      var g = DataflowGraph.Build(new SegInput(nodes, edges), new HashSet<int>());
      var ipdom = Dominators.Compute(g, reverse: true);

      Assert.True(Dominators.SharesRealPostDominator(ipdom, 1, 2));    // same chain -> shared real post-dom
      Assert.True(Dominators.SharesRealPostDominator(ipdom, 1, 3));
      Assert.False(Dominators.SharesRealPostDominator(ipdom, 1, 4));   // different chains -> only the sink
      Assert.False(Dominators.SharesRealPostDominator(ipdom, 3, 6));
    }

    // ---- the over-fire guard (Tharun's hold #1) ------------------------------------------------------

    [Fact]
    public void The_boundary_terms_do_not_fire_inside_a_coherent_tangle()
    {
      // a single dense tournament: every internal node has high in-degree and everything reconverges at the
      // end, so neither the entry-crossing (in-degree guard + shared post-dom) nor the param term should fire.
      // The partition must be identical with the terms on and off - the guard prevents shredding.
      var nodes = Enumerable.Range(1, 26).Select(i => N(i)).ToList();
      var edges = new List<SegEdge>();
      Dense(edges, 1, 26);
      var input = new SegInput(nodes, edges);

      var on = new Segmenter(new SegOptions { Stage2TargetSize = 12 }).Segment(input).Macro;
      var off = new Segmenter(new SegOptions { Stage2TargetSize = 12, ParamBoundaryEnabled = false, EntryCrossingEnabled = false }).Segment(input).Macro;

      foreach (var x in on.Keys)
        foreach (var y in on.Keys)
          Assert.Equal(on[x] == on[y], off[x] == off[y]);   // byte-identical partition: the terms fired on nothing
    }

    // ---- the penalty mechanism -----------------------------------------------------------------------

    [Fact]
    public void A_flagged_seam_edge_is_cut()
    {
      // two clusters joined by one edge, that edge flagged as a param seam: Stage 2 must cut there.
      var nodes = Enumerable.Range(1, 16).Select(i => N(i)).ToList();
      var edges = new List<SegEdge>();
      Dense(edges, 1, 8); Dense(edges, 9, 16); edges.Add(E(8, 9));
      var boundary = new Dictionary<(int, int), (bool param, bool entry)> { [(8, 9)] = (true, false) };

      var sub = Stage2.Subdivide(nodes, edges, boundary, new SegOptions { Stage2TargetSize = 8 });

      Assert.Equal(2, sub.Values.Distinct().Count());
      Assert.Equal(sub[1], sub[8]);
      Assert.Equal(sub[9], sub[16]);
      Assert.NotEqual(sub[1], sub[9]);   // the seam is the cut
    }

    // ---- scattered == tidy through BOTH terms (Tharun's hold #3) -------------------------------------

    [Fact]
    public void Scattered_equals_tidy_through_both_boundary_terms()
    {
      // a rigid blob (multi-bridge two clusters) with a bare PARAM node on it. Run 1 has a tidy layout; run 2
      // reassigns ids AND SCATTERS the pivots (keeping guids + IsParam). With the wire-length term OFF the
      // partition must be byte-identical per guid - the topological terms (kind + post-dominance) never read
      // position. This is the actual scattered==tidy proof, not just an id flip.
      var nodes = new List<SegNode>();
      for (var i = 1; i <= 24; i++) nodes.Add(new SegNode(i, G(i), false, i == 12, i * 20, 0));   // tidy row; node 12 a bare param
      var edges = new List<SegEdge>();
      Dense(edges, 1, 12); Dense(edges, 13, 24);
      edges.Add(E(10, 13)); edges.Add(E(11, 14)); edges.Add(E(12, 15));
      var o = new SegOptions { Stage2TargetSize = 11, WireLengthEnabled = false };

      var first = new Segmenter(o).Segment(new SegInput(nodes, edges));
      Assert.True(first.Diagnostics.RigidSubdivided >= 1);

      int R(int id) => 25 - id;
      // scattered pivots: deterministic but unrelated to the tidy layout, to prove position is ignored.
      var nodes2 = nodes.Select(n => new SegNode(R(n.Id), n.Key, n.IsLeafType, n.IsParam, (R(n.Id) * 37) % 500, (R(n.Id) * 53) % 500)).ToList();
      var edges2 = edges.Select(e => new SegEdge(R(e.FromId), R(e.ToId))).ToList();
      var second = new Segmenter(o).Segment(new SegInput(nodes2, edges2));

      var firstByKey = nodes.ToDictionary(n => n.Key, n => first.Macro[n.Id]);
      var secondByKey = nodes2.ToDictionary(n => n.Key, n => second.Macro[n.Id]);
      foreach (var ka in firstByKey.Keys)
        foreach (var kb in firstByKey.Keys)
          Assert.Equal(firstByKey[ka] == firstByKey[kb], secondByKey[ka] == secondByKey[kb]);
    }
  }
}
