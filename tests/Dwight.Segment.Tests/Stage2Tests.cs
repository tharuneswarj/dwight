using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Dwight.Segment.Tests
{
  /// <summary>
  /// Tests Stage 2 - betweenness-weighted Louvain inside a rigid region. The two that carry Tharun's
  /// requirements: a rigid blob of two dense clusters splits AT the bridge, and a shared input is LIFTED
  /// (consistent with Stage-1 hub isolation), never used as a module boundary. Plus determinism /
  /// scattered==tidy and the Segmenter integration (rigid blob -> more groups with Stage 2 on).
  /// </summary>
  public class Stage2Tests
  {
    private static Guid G(int i) => new Guid(i, 0, 0, new byte[8]);
    private static SegNode N(int id, bool leaf = false) => new SegNode(id, G(id), leaf);
    private static SegEdge E(int a, int b) => new SegEdge(a, b);

    /// <summary>A transitive tournament on [lo,hi]: every i -&gt; j with lo &lt;= i &lt; j &lt;= hi. Internally dense, so
    /// its internal edges carry low betweenness and a single bridge out of it carries high.</summary>
    private static void Dense(List<SegEdge> edges, int lo, int hi)
    {
      for (var i = lo; i <= hi; i++)
        for (var j = i + 1; j <= hi; j++)
          edges.Add(E(i, j));
    }

    [Fact]
    public void A_rigid_blob_of_two_dense_clusters_splits_at_the_bridge()
    {
      var nodes = Enumerable.Range(1, 14).Select(i => N(i)).ToList();
      var edges = new List<SegEdge>();
      Dense(edges, 1, 7);
      Dense(edges, 8, 14);
      edges.Add(E(7, 8));   // the single bridge

      var sub = Stage2.Subdivide(nodes, edges, null, new SegOptions { Stage2TargetSize = 7 });

      Assert.Equal(2, sub.Values.Distinct().Count());
      Assert.Equal(sub[1], sub[7]);                 // cluster A together
      Assert.Equal(sub[8], sub[14]);                // cluster B together
      Assert.NotEqual(sub[1], sub[8]);              // the two clusters are different communities
    }

    [Fact]
    public void A_shared_input_is_lifted_not_made_a_boundary()
    {
      // S (node 1) fans into two otherwise-separate chains. It is the only cross-link, so if it were treated
      // as structure it would fuse the two; Stage 2 must LIFT it (region-relative hub) and recover A vs B.
      var nodes = Enumerable.Range(1, 11).Select(i => N(i)).ToList();
      var edges = new List<SegEdge> { E(2, 3), E(3, 4), E(4, 5), E(5, 6), E(7, 8), E(8, 9), E(9, 10), E(10, 11) };
      for (var t = 2; t <= 11; t++) edges.Add(E(1, t));   // shared input feeds everything (out-degree 10)

      var sub = Stage2.Subdivide(nodes, edges, null, new SegOptions { Stage2TargetSize = 5, HubMinOutDegree = 6 });

      Assert.True(sub.ContainsKey(1));               // the shared input is still placed (reattached)
      Assert.Equal(sub[2], sub[6]);                  // chain A together
      Assert.Equal(sub[7], sub[11]);                 // chain B together
      Assert.NotEqual(sub[2], sub[7]);               // A and B are NOT fused through the shared input
    }

    [Fact]
    public void Stage2_is_deterministic_and_id_reassignment_invariant()
    {
      var nodes = Enumerable.Range(1, 14).Select(i => N(i)).ToList();
      var edges = new List<SegEdge>();
      Dense(edges, 1, 7); Dense(edges, 8, 14); edges.Add(E(7, 8));
      var o = new SegOptions { Stage2TargetSize = 7, WireLengthEnabled = false };

      var first = Stage2.Subdivide(nodes, edges, null, o);

      int R(int id) => 15 - id;   // reverse ids, keep guids + topology
      var nodes2 = nodes.Select(n => new SegNode(R(n.Id), n.Key, n.IsLeafType)).ToList();
      var edges2 = edges.Select(e => new SegEdge(R(e.FromId), R(e.ToId))).ToList();
      var second = Stage2.Subdivide(nodes2, edges2, null, o);

      var firstByKey = nodes.ToDictionary(n => n.Key, n => first[n.Id]);
      var secondByKey = nodes2.ToDictionary(n => n.Key, n => second[n.Id]);
      foreach (var ka in firstByKey.Keys)
        foreach (var kb in firstByKey.Keys)
          Assert.Equal(firstByKey[ka] == firstByKey[kb], secondByKey[ka] == secondByKey[kb]);
    }

    [Fact]
    public void Segmenter_integration_a_rigid_blob_gets_more_groups_with_stage2_on()
    {
      // Two dense clusters joined by MULTIPLE bridges: > granularity max, one connected component, and no
      // single-edge SESE cut (the clusters connect through several edges, not one), so RPST cannot cleanly
      // decompose it - Stage 1 leaves it as one rigid group. But it has real latent community structure, so
      // Stage 2's betweenness finds the cluster boundary and subdivides it (a meaningful split, not fabricated).
      var nodes = Enumerable.Range(1, 24).Select(i => N(i)).ToList();
      var edges = new List<SegEdge>();
      Dense(edges, 1, 12);
      Dense(edges, 13, 24);
      edges.Add(E(10, 13)); edges.Add(E(11, 14)); edges.Add(E(12, 15));   // three bridges, no clean SESE cut
      var input = new SegInput(nodes, edges);

      var on = new Segmenter(new SegOptions { Stage2TargetSize = 12 }).Segment(input);
      var off = new Segmenter(new SegOptions { Stage2Enabled = false, Stage2TargetSize = 12 }).Segment(input);

      Assert.True(on.MacroCount > off.MacroCount, $"Stage 2 should add groups (on {on.MacroCount} vs off {off.MacroCount})");
      Assert.True(on.Diagnostics.RigidSubdivided >= 1, "at least one rigid region should be subdivided");
    }

    [Fact]
    public void Scattered_equals_tidy_holds_through_stage2()
    {
      // The full segmenter, Stage 2 active, must give the byte-identical per-guid partition when ids are
      // reassigned (a scattered layout). Betweenness + modularity are topological, so this must hold.
      var nodes = Enumerable.Range(1, 24).Select(i => N(i)).ToList();
      var edges = new List<SegEdge>();
      Dense(edges, 1, 12); Dense(edges, 13, 24);
      edges.Add(E(10, 13)); edges.Add(E(11, 14)); edges.Add(E(12, 15));
      var o = new SegOptions { Stage2TargetSize = 11, WireLengthEnabled = false };

      var first = new Segmenter(o).Segment(new SegInput(nodes, edges));
      Assert.True(first.Diagnostics.RigidSubdivided >= 1);   // Stage 2 actually ran

      int R(int id) => 25 - id;
      var nodes2 = nodes.Select(n => new SegNode(R(n.Id), n.Key, n.IsLeafType)).ToList();
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
