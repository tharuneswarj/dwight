using System;
using System.Collections.Generic;
using System.Linq;

namespace Dwight.Segment
{
  /// <summary>
  /// Edge betweenness via Brandes (Phase 6 WP-B Stage 2, ADR-087). For each edge, the share of all-pairs
  /// shortest paths that run through it - a high value marks a bridge between dense sub-modules (a boundary),
  /// a low value an internal edge. This is the principled, position-free replacement for the "long wire"
  /// signal: Stage 2 inverts it into edge weights so community detection cuts at the bridges. Computed
  /// UNDIRECTED for the first build (it pairs with undirected Louvain; the directed upgrade is a
  /// benchmark-driven call). Deterministic - sources and adjacency are visited in guid order, so the float
  /// accumulation is byte-identical run to run (and invariant to id reassignment: scattered == tidy).
  /// Headless and GH-free.
  /// </summary>
  internal static class Betweenness
  {
    /// <summary>Undirected edge betweenness over <paramref name="nodes"/> with key-sorted undirected
    /// adjacency. Returns canonical (loKey, hiKey) edge -&gt; betweenness.</summary>
    public static Dictionary<(int, int), double> EdgeBetweenness(
      IReadOnlyList<int> nodes, IReadOnlyDictionary<int, List<int>> adj, IReadOnlyDictionary<int, Guid> keyById)
    {
      var eb = new Dictionary<(int, int), double>();
      var ordered = nodes.OrderBy(n => keyById[n]).ToList();   // deterministic source order

      foreach (var s in ordered)
      {
        // single-source shortest-path counts (BFS, unweighted)
        var stack = new List<int>();
        var pred = new Dictionary<int, List<int>>();
        var sigma = new Dictionary<int, double>();
        var dist = new Dictionary<int, int>();
        foreach (var n in nodes) { pred[n] = new List<int>(); sigma[n] = 0.0; dist[n] = -1; }
        sigma[s] = 1.0; dist[s] = 0;

        var queue = new Queue<int>();
        queue.Enqueue(s);
        while (queue.Count > 0)
        {
          var v = queue.Dequeue();
          stack.Add(v);
          foreach (var w in adj[v])   // already key-sorted
          {
            if (dist[w] < 0) { dist[w] = dist[v] + 1; queue.Enqueue(w); }
            if (dist[w] == dist[v] + 1) { sigma[w] += sigma[v]; pred[w].Add(v); }
          }
        }

        // dependency accumulation (reverse BFS order)
        var delta = new Dictionary<int, double>();
        foreach (var n in nodes) delta[n] = 0.0;
        for (var i = stack.Count - 1; i >= 0; i--)
        {
          var w = stack[i];
          foreach (var v in pred[w])
          {
            var c = (sigma[v] / sigma[w]) * (1.0 + delta[w]);
            var key = Canon(v, w, keyById);
            eb.TryGetValue(key, out var prev);
            eb[key] = prev + c;
            delta[v] += c;
          }
        }
      }

      foreach (var k in eb.Keys.ToList()) eb[k] /= 2.0;   // undirected: each path counted from both ends
      return eb;
    }

    private static (int, int) Canon(int a, int b, IReadOnlyDictionary<int, Guid> keyById)
      => keyById[a].CompareTo(keyById[b]) <= 0 ? (a, b) : (b, a);
  }
}
