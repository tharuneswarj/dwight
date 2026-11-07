using System.Collections.Generic;
using System.Linq;
using Dwight.Data;

namespace Dwight.Labelling
{
  /// <summary>
  /// The Grasshopper adapter for the labelling pass (Phase 6 WP-C): projects the <see cref="DwightGraph"/> +
  /// the segmenter's cluster partition into the headless <see cref="LabelInput"/> (component types, wires,
  /// tree-signature phrases), and computes each cluster's stable content hash for the cache. Runs on the UI
  /// thread (tree signatures read live volatile data). GH-only, so not in the headless test set.
  /// </summary>
  internal static class GhLabel
  {
    public static LabelInput BuildInput(DwightGraph graph, IReadOnlyDictionary<int, int> macro)
    {
      var typeById = new Dictionary<int, string>();
      foreach (var n in graph.Nodes) if (macro.ContainsKey(n.Id)) typeById[n.Id] = n.Type;

      var edges = new List<(int, int)>();
      var sigByEdge = new Dictionary<(int, int), string>();
      foreach (var e in graph.Edges)
      {
        if (!macro.ContainsKey(e.FromNode) || !macro.ContainsKey(e.ToNode)) continue;
        edges.Add((e.FromNode, e.ToNode));
        var sig = e.Tree?.Describe();
        if (!string.IsNullOrWhiteSpace(sig)) sigByEdge[(e.FromNode, e.ToNode)] = sig;
      }
      return ClusterSummaries.Build(macro, typeById, edges, sigByEdge);
    }

    /// <summary>Per-cluster content hash (member guids + types + internal edges), so a re-run after an edit
    /// re-bills only the changed cluster. WP-4.7: this DELIBERATELY omits the boundary neighbour phrases and the
    /// tree signatures - a cluster's LABEL depends only on what it IS (its members + internal wiring), so an
    /// edit elsewhere (a neighbour's rename, a data-count change on a crossing wire) must NOT invalidate this
    /// cluster's cached name. The arrangement, which does depend on the whole picture, is cached separately by
    /// the graph-shape hash (ClusterLabellerCore.ArrangementHash).</summary>
    public static Dictionary<int, string> ContentHashes(DwightGraph graph, IReadOnlyDictionary<int, int> macro)
    {
      var membersByCluster = macro.GroupBy(kv => kv.Value).ToDictionary(g => g.Key, g => new HashSet<int>(g.Select(kv => kv.Key)));
      var guidById = graph.Nodes.ToDictionary(n => n.Id, n => n.Guid);
      var typeById = graph.Nodes.ToDictionary(n => n.Id, n => n.Type);

      var result = new Dictionary<int, string>();
      foreach (var c in membersByCluster)
      {
        var tokens = new List<string>();
        foreach (var m in c.Value)
        {
          if (!guidById.TryGetValue(m, out var g)) continue;
          tokens.Add("n:" + g);
          tokens.Add("t:" + g + ":" + (typeById.TryGetValue(m, out var t) ? t : ""));
        }
        foreach (var e in graph.Edges)
          if (c.Value.Contains(e.FromNode) && c.Value.Contains(e.ToNode) &&
              guidById.TryGetValue(e.FromNode, out var gf) && guidById.TryGetValue(e.ToNode, out var gt))
            tokens.Add("e:" + gf + ">" + gt);
        result[c.Key] = LabelCache.Hash(tokens);
      }
      return result;
    }
  }
}
