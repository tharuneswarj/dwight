using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Dwight.Segment.Tests
{
  /// <summary>
  /// Tests the deterministic Louvain + connectivity-refinement detector. The two that matter most: every
  /// output community is CONNECTED (the guarantee Louvain alone lacks, the reason we ported it), and the
  /// partition is deterministic. Plus sanity on cliques/bridges and resolution control (so the segmenter's
  /// binary-search on resolution is sound).
  /// </summary>
  public class CommunityDetectorTests
  {
    private static Guid G(int i) => new Guid(i, 0, 0, new byte[8]);

    /// <summary>Build a weighted graph from (a,b,w) triples; node ids inferred, ordered by guid (= id).</summary>
    private static WeightedGraph WG(params (int a, int b, double w)[] edges)
    {
      var ids = new SortedSet<int>();
      foreach (var (a, b, _) in edges) { ids.Add(a); ids.Add(b); }
      var nodeIds = ids.OrderBy(i => G(i)).ToList();
      var we = edges.Select(e =>
      {
        var (a, b) = G(e.a).CompareTo(G(e.b)) <= 0 ? (e.a, e.b) : (e.b, e.a);
        return new WeightedEdge(a, b, e.w, synthetic: false);
      }).ToList();
      return new WeightedGraph(nodeIds, we);
    }

    private static IReadOnlyDictionary<int, int> Detect(WeightedGraph g, double resolution = 1.0)
      => new LouvainConnectivity().Detect(g, resolution);

    private static int CommunityCount(IReadOnlyDictionary<int, int> p) => p.Values.Distinct().Count();

    /// <summary>Assert every community induced on the graph is a single connected component.</summary>
    private static void AssertAllCommunitiesConnected(WeightedGraph g, IReadOnlyDictionary<int, int> part)
    {
      var adj = new Dictionary<int, List<int>>();
      foreach (var id in g.NodeIds) adj[id] = new List<int>();
      foreach (var e in g.Edges) { adj[e.A].Add(e.B); adj[e.B].Add(e.A); }

      foreach (var community in part.Values.Distinct())
      {
        var members = part.Where(kv => kv.Value == community).Select(kv => kv.Key).ToHashSet();
        if (members.Count <= 1) continue;
        var seen = new HashSet<int> { members.First() };
        var queue = new Queue<int>(); queue.Enqueue(members.First());
        while (queue.Count > 0)
        {
          var u = queue.Dequeue();
          foreach (var v in adj[u]) if (members.Contains(v) && seen.Add(v)) queue.Enqueue(v);
        }
        Assert.True(seen.Count == members.Count, $"community {community} is disconnected ({seen.Count}/{members.Count})");
      }
    }

    private static (int a, int b, double w) E(int a, int b, double w = 1.0) => (a, b, w);

    /// <summary>A clique over the given ids (every pair connected).</summary>
    private static IEnumerable<(int, int, double)> Clique(double w, params int[] ids)
    {
      for (var i = 0; i < ids.Length; i++)
        for (var j = i + 1; j < ids.Length; j++)
          yield return (ids[i], ids[j], w);
    }

    // ---- sanity -------------------------------------------------------------------------------------

    [Fact]
    public void Two_cliques_joined_by_one_weak_bridge_split_into_two_communities()
    {
      var edges = Clique(1.0, 1, 2, 3).Concat(Clique(1.0, 4, 5, 6)).Append(E(3, 4, 0.05)).ToArray();
      var part = Detect(WG(edges));
      Assert.Equal(2, CommunityCount(part));
      Assert.Equal(part[1], part[2]);
      Assert.Equal(part[2], part[3]);
      Assert.Equal(part[4], part[5]);
      Assert.NotEqual(part[1], part[4]);
    }

    [Fact]
    public void A_single_clique_is_one_community()
    {
      var part = Detect(WG(Clique(1.0, 1, 2, 3, 4, 5).ToArray()));
      Assert.Equal(1, CommunityCount(part));
    }

    [Fact]
    public void Two_disconnected_triangles_are_two_connected_communities()
    {
      var edges = Clique(1.0, 1, 2, 3).Concat(Clique(1.0, 4, 5, 6)).ToArray();
      var g = WG(edges);
      var part = Detect(g);
      Assert.Equal(2, CommunityCount(part));
      AssertAllCommunitiesConnected(g, part);
    }

    // ---- the guarantees -----------------------------------------------------------------------------

    [Fact]
    public void Every_community_is_connected()
    {
      // two cliques bridged, plus a separate disconnected pair - exercises both modularity split and the
      // connectivity refinement.
      var edges = Clique(1.0, 1, 2, 3, 4).Concat(Clique(1.0, 5, 6, 7, 8))
        .Append(E(4, 5, 0.1)).Append(E(10, 11, 1.0)).ToArray();
      var g = WG(edges);
      AssertAllCommunitiesConnected(g, Detect(g));
    }

    [Fact]
    public void Partition_is_deterministic_across_runs()
    {
      var edges = Clique(1.0, 1, 2, 3, 4).Concat(Clique(1.0, 5, 6, 7, 8)).Append(E(4, 5, 0.1)).ToArray();
      var g = WG(edges);
      var a = Detect(g);
      var b = Detect(g);
      Assert.Equal(a.Count, b.Count);
      // same partition: identical labels (the detector is fully deterministic) AND identical co-membership.
      foreach (var kv in a) Assert.Equal(kv.Value, b[kv.Key]);
      foreach (var x in a.Keys)
        foreach (var y in a.Keys)
          Assert.Equal(a[x] == a[y], b[x] == b[y]);
    }

    [Fact]
    public void Higher_resolution_yields_at_least_as_many_communities()
    {
      // one big clique: at gamma=1 it is a single community; at high gamma it fractures.
      var g = WG(Clique(1.0, 1, 2, 3, 4, 5, 6, 7, 8).ToArray());
      var low = CommunityCount(Detect(g, 0.5));
      var high = CommunityCount(Detect(g, 6.0));
      Assert.True(high >= low, $"high-resolution communities {high} should be >= low {low}");
      Assert.True(high > 1, "a high resolution should fracture a single clique");
    }

    [Fact]
    public void Empty_and_singleton_graphs_are_handled()
    {
      Assert.Empty(new LouvainConnectivity().Detect(new WeightedGraph(new List<int>(), new List<WeightedEdge>()), 1.0));
      var one = new LouvainConnectivity().Detect(new WeightedGraph(new List<int> { 7 }, new List<WeightedEdge>()), 1.0);
      Assert.Single(one);
    }
  }
}
