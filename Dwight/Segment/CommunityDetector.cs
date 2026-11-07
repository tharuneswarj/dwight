using System;
using System.Collections.Generic;
using System.Linq;

namespace Dwight.Segment
{
  // ===========================================================================================================
  // PARKED FOR STAGE 2 (ADR-087). This community detector is NOT part of the live Stage-1 RPST segmenter -
  // Stage 1 is pure RPST and does not touch community detection at all. It is kept, deliberately, as the
  // starting point for Stage 2: "directed Leiden inside oversized rigid regions the RPST cannot cleanly
  // decompose," built ONLY IF the Stage-1 benchmark shows it is needed. The Stage-2 upgrade is to make the
  // modularity DIRECTED (Leicht-Newman) and add consensus stabilisation; the deterministic local-moving +
  // connectivity refinement below is the reusable half. Until Stage 2 fires, nothing calls this.
  // (The earlier position-proximity weighting that fed it was benchmarked and deleted; see git history.)
  // ===========================================================================================================

  /// <summary>One undirected weighted edge for the community detector (Stage 2). A and B are node ids with
  /// A's key &lt; B's key (canonical order, for determinism).</summary>
  internal readonly struct WeightedEdge
  {
    public readonly int A, B;
    public readonly double Weight;
    public readonly bool Synthetic;
    public WeightedEdge(int a, int b, double weight, bool synthetic) { A = a; B = b; Weight = weight; Synthetic = synthetic; }
  }

  /// <summary>The weighted graph the Stage-2 detector clusters: node ids + weighted undirected edges.</summary>
  internal sealed class WeightedGraph
  {
    public IReadOnlyList<int> NodeIds { get; }
    public IReadOnlyList<WeightedEdge> Edges { get; }
    public WeightedGraph(IReadOnlyList<int> nodeIds, IReadOnlyList<WeightedEdge> edges) { NodeIds = nodeIds; Edges = edges; }
  }

  /// <summary>
  /// The swappable community-detection core (Stage 2 starting point). <see cref="LouvainConnectivity"/> is
  /// deterministic Louvain modularity optimisation plus a connectivity-refinement pass that splits any
  /// disconnected community into its connected parts (the connectivity guarantee a functional region needs).
  /// Stage 2 upgrades this to directed Leiden; the local-moving + refinement machinery is the reusable half.
  /// </summary>
  internal interface ICommunityDetector
  {
    /// <summary>Partition the weighted graph: node id -&gt; community id. Deterministic for identical input
    /// and identical <paramref name="resolution"/>. Higher resolution yields more, smaller communities.</summary>
    IReadOnlyDictionary<int, int> Detect(WeightedGraph graph, double resolution);
  }

  internal sealed class LouvainConnectivity : ICommunityDetector
  {
    private const double Eps = 1e-12;

    public IReadOnlyDictionary<int, int> Detect(WeightedGraph graph, double resolution)
    {
      var ids = graph.NodeIds.ToList();   // already guid-ranked by EdgeWeights, so index order is stable
      var n = ids.Count;
      var result = new Dictionary<int, int>(n);
      if (n == 0) return result;

      var index = new Dictionary<int, int>(n);
      for (var i = 0; i < n; i++) index[ids[i]] = i;

      // base adjacency (no self-loops); each undirected edge appears in both endpoints' lists.
      var adj = new List<(int j, double w)>[n];
      for (var i = 0; i < n; i++) adj[i] = new List<(int, double)>();
      foreach (var e in graph.Edges)
      {
        if (!index.TryGetValue(e.A, out var a) || !index.TryGetValue(e.B, out var b) || a == b) continue;
        adj[a].Add((b, e.Weight));
        adj[b].Add((a, e.Weight));
      }

      var labels = Louvain(n, adj, resolution);
      labels = RefineConnectivity(n, adj, labels);

      for (var i = 0; i < n; i++) result[ids[i]] = labels[i];
      return result;
    }

    // ---- Louvain (multi-level modularity) -----------------------------------------------------------

    private static int[] Louvain(int n, List<(int j, double w)>[] baseAdj, double gamma)
    {
      var currentOfBase = new int[n];
      for (var i = 0; i < n; i++) currentOfBase[i] = i;

      var N = n;
      var adj = baseAdj;
      var selfLoop = new double[N];   // zero at base

      while (true)
      {
        var (comm, improved) = LocalMoving(N, adj, selfLoop, gamma);
        var renum = Renumber(N, comm);              // distinct labels -> 0..K-1 by ascending min index
        var k = renum.Count;

        for (var b = 0; b < n; b++) currentOfBase[b] = renum[comm[currentOfBase[b]]];

        if (!improved || k == N)
          return currentOfBase;                     // converged

        (adj, selfLoop) = Aggregate(N, adj, selfLoop, comm, renum, k);
        N = k;
      }
    }

    /// <summary>One local-moving phase: repeatedly move each node (ascending index = guid-rank) to the
    /// neighbouring community with the best modularity gain, until stable. Deterministic tie-breaks.</summary>
    private static (int[] comm, bool improved) LocalMoving(int N, List<(int j, double w)>[] adj, double[] selfLoop, double gamma)
    {
      var degree = new double[N];
      for (var i = 0; i < N; i++)
      {
        double d = 2.0 * selfLoop[i];
        foreach (var (_, w) in adj[i]) d += w;
        degree[i] = d;
      }
      var m2 = degree.Sum();   // 2m

      var comm = new int[N];
      var commTot = new double[N];
      for (var i = 0; i < N; i++) { comm[i] = i; commTot[i] = degree[i]; }

      if (m2 <= Eps) return (comm, false);   // no edges -> every node its own community

      var improved = false;
      var moved = true;
      while (moved)
      {
        moved = false;
        for (var i = 0; i < N; i++)
        {
          var ci = comm[i];
          var kin = new Dictionary<int, double>();
          foreach (var (j, w) in adj[i])
            if (j != i) { kin.TryGetValue(comm[j], out var prev); kin[comm[j]] = prev + w; }

          commTot[ci] -= degree[i];   // isolate i

          kin.TryGetValue(ci, out var kinCi);
          var bestC = ci;
          var bestGain = kinCi - gamma * commTot[ci] * degree[i] / m2;

          foreach (var c in kin.Keys.OrderBy(c => c))   // deterministic candidate order
          {
            var gain = kin[c] - gamma * commTot[c] * degree[i] / m2;
            if (gain > bestGain + Eps || (Math.Abs(gain - bestGain) <= Eps && c < bestC))
            { bestGain = gain; bestC = c; }
          }

          comm[i] = bestC;
          commTot[bestC] += degree[i];
          if (bestC != ci) { moved = true; improved = true; }
        }
      }
      return (comm, improved);
    }

    /// <summary>Map the distinct community labels to 0..K-1, ordered by the ascending minimum index that
    /// carries each label - so community indices stay in guid-rank order across aggregation levels.</summary>
    private static Dictionary<int, int> Renumber(int N, int[] comm)
    {
      var firstIndex = new Dictionary<int, int>();
      for (var i = 0; i < N; i++)
        if (!firstIndex.ContainsKey(comm[i])) firstIndex[comm[i]] = i;

      var map = new Dictionary<int, int>(firstIndex.Count);
      var rank = 0;
      foreach (var label in firstIndex.OrderBy(kv => kv.Value).Select(kv => kv.Key))
        map[label] = rank++;
      return map;
    }

    /// <summary>Collapse each community into a super-node: intra-community weight becomes a self-loop,
    /// inter-community weight becomes a single aggregated edge. New node order = renumbered community id.</summary>
    private static (List<(int j, double w)>[] adj, double[] selfLoop) Aggregate(
      int N, List<(int j, double w)>[] adj, double[] selfLoop, int[] comm, Dictionary<int, int> renum, int k)
    {
      var newSelf = new double[k];
      var cross = new Dictionary<(int, int), double>();

      for (var i = 0; i < N; i++)
      {
        var ci = renum[comm[i]];
        newSelf[ci] += selfLoop[i];
        foreach (var (j, w) in adj[i])
        {
          if (j <= i) continue;   // count each undirected edge once
          var cj = renum[comm[j]];
          if (ci == cj) newSelf[ci] += w;
          else
          {
            var key = ci < cj ? (ci, cj) : (cj, ci);
            cross.TryGetValue(key, out var prev);
            cross[key] = prev + w;
          }
        }
      }

      var newAdj = new List<(int, double)>[k];
      for (var c = 0; c < k; c++) newAdj[c] = new List<(int, double)>();
      foreach (var kv in cross)
      {
        newAdj[kv.Key.Item1].Add((kv.Key.Item2, kv.Value));
        newAdj[kv.Key.Item2].Add((kv.Key.Item1, kv.Value));
      }
      return (newAdj, newSelf);
    }

    // ---- connectivity refinement (Leiden's one guarantee Louvain lacks) -----------------------------

    /// <summary>Split any community whose induced subgraph is disconnected into its connected components,
    /// each a new community - so every output region is a connected functional unit. Relabels 0..K'-1 in a
    /// deterministic order (ascending original label, then ascending index).</summary>
    private static int[] RefineConnectivity(int n, List<(int j, double w)>[] adj, int[] labels)
    {
      var newLabels = new int[n];
      for (var i = 0; i < n; i++) newLabels[i] = -1;

      var members = new Dictionary<int, List<int>>();
      for (var i = 0; i < n; i++)
      {
        if (!members.TryGetValue(labels[i], out var list)) members[labels[i]] = list = new List<int>();
        list.Add(i);
      }

      var next = 0;
      foreach (var label in members.Keys.OrderBy(l => l))
      {
        var group = new HashSet<int>(members[label]);
        foreach (var start in members[label].OrderBy(i => i))   // ascending index (guid-rank)
        {
          if (newLabels[start] != -1) continue;
          // BFS within the group
          var queue = new Queue<int>();
          queue.Enqueue(start);
          newLabels[start] = next;
          while (queue.Count > 0)
          {
            var u = queue.Dequeue();
            foreach (var (v, _) in adj[u])
              if (group.Contains(v) && newLabels[v] == -1) { newLabels[v] = next; queue.Enqueue(v); }
          }
          next++;
        }
      }
      return newLabels;
    }
  }
}
