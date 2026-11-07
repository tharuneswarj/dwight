using System.Collections.Generic;
using System.Linq;

namespace Dwight.Data
{
  /// <summary>
  /// The Simplify boundary-crossing ordering (Phase 7 WP-2 Stage 5) - GH-free and the SINGLE source of the ext
  /// index so the payload and the resolve builder cannot diverge. <c>Payloads.Simplify</c> emits each boundary
  /// crossing an <c>ext</c> index; the proposal's <c>i&lt;n&gt;</c>/<c>o&lt;n&gt;</c> refs point back at that
  /// index; the resolve builder resolves the refs by asking THIS helper for the same ordered crossings. Deriving
  /// both from one function makes the ordering identity structural, not a coincidence of two hand-copied sorts
  /// (the correctness hinge of ref resolution - progress doc, "assert it, don't assume it").
  ///
  /// A crossing is a distinct selection-side port that a wire crosses the selection edge at: inputs keyed by the
  /// (target node, target param) the external data ARRIVES at; outputs keyed by the (source node, source param)
  /// the selection data LEAVES from. Ordering is <c>OrderBy(node).ThenBy(param)</c> (the payload's exact sort),
  /// and <see cref="Crossing.Edges"/> keeps the realising edges in encounter order - so <c>Edges[0]</c> is the
  /// first-edge-wins source/consumer the payload's sig captured, and the full list gives ExtOut every consumer.
  /// </summary>
  internal static class SimplifyBoundary
  {
    /// <summary>A GH-free wire edge (short-int node ids + param labels), the projection both callers adapt their
    /// edge list into so this helper stays headless-testable.</summary>
    internal readonly struct Edge
    {
      public readonly int FromNode;
      public readonly string FromParam;
      public readonly int ToNode;
      public readonly string ToParam;
      public Edge(int fromNode, string fromParam, int toNode, string toParam)
      { FromNode = fromNode; FromParam = fromParam; ToNode = toNode; ToParam = toParam; }
    }

    /// <summary>One boundary crossing: the selection-side port + every external edge realising it (encounter
    /// order, so <c>Edges[0]</c> is the first-wins edge and the full list is all sources/consumers).</summary>
    internal sealed class Crossing
    {
      public int Node;
      public string Param;
      public List<Edge> Edges = new List<Edge>();
    }

    /// <summary>External-INPUT crossings: edges entering the selection (target in, source out), grouped by the
    /// selection-side (ToNode, ToParam) and ordered as the payload emits them. Ext index = list position.</summary>
    public static List<Crossing> Inputs(IEnumerable<Edge> edges, ISet<int> selection)
      => Group(edges, selection, incoming: true);

    /// <summary>External-OUTPUT crossings: edges leaving the selection (source in, target out), grouped by the
    /// selection-side (FromNode, FromParam) and ordered as the payload emits them. Ext index = list position.</summary>
    public static List<Crossing> Outputs(IEnumerable<Edge> edges, ISet<int> selection)
      => Group(edges, selection, incoming: false);

    private static List<Crossing> Group(IEnumerable<Edge> edges, ISet<int> selection, bool incoming)
    {
      var byKey = new Dictionary<(int, string), Crossing>();
      if (edges != null && selection != null)
        foreach (var e in edges)
        {
          var fromIn = selection.Contains(e.FromNode);
          var toIn = selection.Contains(e.ToNode);
          // incoming: crosses IN (target in, source out); outgoing: crosses OUT (source in, target out).
          if (incoming ? !(toIn && !fromIn) : !(fromIn && !toIn)) continue;
          var key = incoming ? (e.ToNode, e.ToParam) : (e.FromNode, e.FromParam);
          if (!byKey.TryGetValue(key, out var c)) { c = new Crossing { Node = key.Item1, Param = key.Item2 }; byKey[key] = c; }
          c.Edges.Add(e);   // encounter order preserved (first wins for the sig; the full list is all edges)
        }
      // the payload sorts the distinct crossing keys OrderBy(node).ThenBy(param) with the default comparer; using
      // the same LINQ default here keeps the ext index byte-identical to what the model was shown.
      return byKey.Values.OrderBy(c => c.Node).ThenBy(c => c.Param).ToList();
    }
  }
}
