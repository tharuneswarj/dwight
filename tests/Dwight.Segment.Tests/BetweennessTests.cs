using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Dwight.Segment.Tests
{
  /// <summary>
  /// Tests Brandes edge betweenness - the Stage-2 boundary signal. The headline is the barbell: a single
  /// bridge edge between two cliques must carry the highest betweenness (every cross-clique shortest path runs
  /// through it), which is exactly the cut Stage 2 should weaken. Plus determinism / id-reassignment
  /// invariance (scattered == tidy).
  /// </summary>
  public class BetweennessTests
  {
    private static Guid G(int i) => new Guid(i, 0, 0, new byte[8]);

    private static Dictionary<(int, int), double> Compute(int[] ids, params (int a, int b)[] edges)
    {
      var keyById = ids.ToDictionary(i => i, i => G(i));
      var adj = ids.ToDictionary(i => i, i => new List<int>());
      foreach (var (a, b) in edges) { adj[a].Add(b); adj[b].Add(a); }
      foreach (var k in adj.Keys.ToList()) adj[k] = adj[k].OrderBy(n => keyById[n]).ToList();
      return Betweenness.EdgeBetweenness(ids.ToList(), adj, keyById);
    }

    private static double Of(Dictionary<(int, int), double> eb, int a, int b)
    {
      var key = G(a).CompareTo(G(b)) <= 0 ? (a, b) : (b, a);
      return eb.TryGetValue(key, out var v) ? v : 0.0;
    }

    [Fact]
    public void The_bridge_edge_of_a_barbell_has_the_highest_betweenness()
    {
      // two triangles {1,2,3} and {4,5,6}, bridged by 3-4
      var eb = Compute(new[] { 1, 2, 3, 4, 5, 6 },
        (1, 2), (1, 3), (2, 3), (4, 5), (4, 6), (5, 6), (3, 4));

      var bridge = Of(eb, 3, 4);
      var intra = new[] { Of(eb, 1, 2), Of(eb, 1, 3), Of(eb, 2, 3), Of(eb, 4, 5), Of(eb, 4, 6), Of(eb, 5, 6) };
      Assert.All(intra, w => Assert.True(bridge > w, $"bridge {bridge} must exceed intra-clique edge {w}"));
    }

    [Fact]
    public void Betweenness_is_deterministic_and_id_reassignment_invariant()
    {
      var a = Compute(new[] { 1, 2, 3, 4, 5, 6 }, (1, 2), (1, 3), (2, 3), (4, 5), (4, 6), (5, 6), (3, 4));
      // reverse the ids (keep guids attached to the same nodes): the per-guid betweenness must match
      int R(int x) => 7 - x;
      var b = Compute(new[] { 1, 2, 3, 4, 5, 6 }.Select(R).ToArray(),
        (R(1), R(2)), (R(1), R(3)), (R(2), R(3)), (R(4), R(5)), (R(4), R(6)), (R(5), R(6)), (R(3), R(4)));

      // compare the bridge by guid: bridge nodes 3,4 -> guids G(3),G(4); under remap they are ids R(3)=4,R(4)=3
      Assert.Equal(Of(a, 3, 4), Of(b, R(3), R(4)), 9);
    }
  }
}
