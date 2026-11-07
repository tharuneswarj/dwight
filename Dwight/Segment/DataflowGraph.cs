using System;
using System.Collections.Generic;
using System.Linq;

namespace Dwight.Segment
{
  /// <summary>
  /// The directed dataflow graph the RPST runs on (Phase 6 WP-B Stage 1, ADR-087). Built from the author's
  /// untouched topology with two additions that make dominance well-defined on a multi-input/multi-output
  /// graph: a virtual <see cref="Source"/> feeding every input-less node and a virtual <see cref="Sink"/>
  /// under every output-less node. Fan-out HUBS (a shared slider feeding many regions) are lifted OUT before
  /// the graph is built, so they cannot collapse distinct sublogics into one cluster - they render later as a
  /// "global inputs" lane. All adjacency is ordered by InstanceGuid so every downstream traversal is
  /// deterministic. Headless and GH-free.
  /// </summary>
  internal sealed class DataflowGraph
  {
    public const int Source = int.MinValue + 1;
    public const int Sink = int.MinValue + 2;

    private readonly Dictionary<int, List<int>> _succ = new Dictionary<int, List<int>>();
    private readonly Dictionary<int, List<int>> _pred = new Dictionary<int, List<int>>();
    private readonly Dictionary<int, Guid> _key = new Dictionary<int, Guid>();

    public IReadOnlyCollection<int> Nodes => _key.Keys;                 // includes Source and Sink
    public IReadOnlyList<int> Successors(int id) => _succ.TryGetValue(id, out var s) ? s : Empty;
    public IReadOnlyList<int> Predecessors(int id) => _pred.TryGetValue(id, out var p) ? p : Empty;
    public Guid KeyOf(int id) => _key.TryGetValue(id, out var k) ? k : Guid.Empty;
    public bool IsReal(int id) => id != Source && id != Sink;

    private static readonly List<int> Empty = new List<int>();

    /// <summary>The shared-input fan-out hubs to lift out before RPST (ADR-087): a node whose out-degree
    /// (distinct downstream targets) is over the definition-relative threshold AND which is input-like - a UI
    /// leaf (slider/param/value list) or a graph source. The input-like condition is the point: a shared
    /// SLIDER feeding 50 components must be lifted so it cannot collapse 50 sublogics into one cluster, but a
    /// central COMPUTATION node that legitimately feeds many consumers (a base surface) is not a noise hub and
    /// is left to anchor its region. Threshold is definition-relative (max of an absolute floor and a multiple
    /// of the median), never an absolute count. Returned in guid order.</summary>
    public static List<int> Hubs(SegInput input, SegOptions options)
    {
      var outDeg = new Dictionary<int, HashSet<int>>();
      var inDeg = new Dictionary<int, HashSet<int>>();
      foreach (var e in input.Edges)
      {
        if (e.FromId == e.ToId) continue;
        if (!outDeg.TryGetValue(e.FromId, out var so)) outDeg[e.FromId] = so = new HashSet<int>();
        so.Add(e.ToId);
        if (!inDeg.TryGetValue(e.ToId, out var si)) inDeg[e.ToId] = si = new HashSet<int>();
        si.Add(e.FromId);
      }
      int Out(int id) => outDeg.TryGetValue(id, out var s) ? s.Count : 0;
      int In(int id) => inDeg.TryGetValue(id, out var s) ? s.Count : 0;

      // median over ALL nodes (the typical node's fan-out), so a star/few-hub graph does not skew the median
      // up to the hub's own degree - the floor then governs sparse graphs and scaling kicks in only on
      // genuinely high-fan-out definitions.
      var degrees = input.Nodes.Select(n => Out(n.Id)).OrderBy(d => d).ToList();
      if (degrees.Count == 0) return new List<int>();
      var median = degrees[degrees.Count / 2];
      var threshold = Math.Max(options.HubMinOutDegree, (int)Math.Ceiling(options.HubMedianFactor * Math.Max(1, median)));

      return input.Nodes
        .Where(n => Out(n.Id) > threshold && (n.IsLeafType || In(n.Id) == 0))   // input-like fan-out only
        .OrderBy(n => n.Key).Select(n => n.Id).ToList();
    }

    public static DataflowGraph Build(SegInput input, ISet<int> excludeHubs)
    {
      var g = new DataflowGraph();
      excludeHubs = excludeHubs ?? new HashSet<int>();

      var keep = input.Nodes.Where(n => !excludeHubs.Contains(n.Id)).ToList();
      foreach (var n in keep) { g._key[n.Id] = n.Key; g._succ[n.Id] = new List<int>(); g._pred[n.Id] = new List<int>(); }
      // virtual endpoints: Source sorts first (Guid.Empty), Sink sorts last (all-0xFF).
      g._key[Source] = Guid.Empty;
      g._key[Sink] = new Guid(new byte[] { 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255 });
      g._succ[Source] = new List<int>(); g._pred[Source] = new List<int>();
      g._succ[Sink] = new List<int>(); g._pred[Sink] = new List<int>();

      var real = new HashSet<int>(keep.Select(n => n.Id));
      foreach (var e in input.Edges)
      {
        if (!real.Contains(e.FromId) || !real.Contains(e.ToId) || e.FromId == e.ToId) continue;
        if (g._succ[e.FromId].Contains(e.ToId)) continue;   // collapse parallel wires to one directed edge
        g._succ[e.FromId].Add(e.ToId);
        g._pred[e.ToId].Add(e.FromId);
      }

      // attach the virtual endpoints to the real input-less / output-less nodes
      foreach (var id in real)
      {
        if (g._pred[id].Count == 0) { g._succ[Source].Add(id); g._pred[id].Add(Source); }
        if (g._succ[id].Count == 0) { g._pred[Sink].Add(id); g._succ[id].Add(Sink); }
      }

      g.SortAdjacency();
      return g;
    }

    private void SortAdjacency()
    {
      foreach (var list in _succ.Values) list.Sort(ByKey);
      foreach (var list in _pred.Values) list.Sort(ByKey);
    }

    private int ByKey(int a, int b)
    {
      var c = _key[a].CompareTo(_key[b]);
      return c != 0 ? c : a.CompareTo(b);
    }
  }
}
