using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Msagl.Core.Geometry;
using Microsoft.Msagl.Core.Geometry.Curves;
using Microsoft.Msagl.Core.Layout;
using Microsoft.Msagl.Layout.Layered;
using Microsoft.Msagl.Miscellaneous;
using MsaglNode = Microsoft.Msagl.Core.Layout.Node;
using MsaglEdge = Microsoft.Msagl.Core.Layout.Edge;

namespace Dwight.Layout
{
  /// <summary>
  /// The MSAGL implementation of the layered-layout core (ADR-063; confirmed in the Rhino 8 host, WP-0).
  /// One weakly-connected DAG in, each node's centre out, left-to-right by depth, overlap-free by
  /// construction because MSAGL is fed the REAL measured component sizes. GH-free and Rhino-free, so it is
  /// unit-tested headlessly.
  ///
  /// Determinism: nodes are added in id order and edges in (from,to) order, and MSAGL's Sugiyama pipeline is
  /// deterministic for fixed input ordering (verified byte-identical across passes in WP-0). No seed or
  /// randomised pass is enabled.
  /// </summary>
  internal sealed class MsaglLayout : ILayoutAlgorithm
  {
    /// <summary>A degenerate node size guard - MSAGL needs a positive rectangle; a component that reports a
    /// zero/uncomputed bound falls back to a small default rather than collapsing the layout.</summary>
    private const double MinExtent = 1.0;
    private const double FallbackSize = 50.0;

    public IReadOnlyDictionary<int, PointD> LayoutBlock(
      IReadOnlyList<LayoutNode> nodes,
      IReadOnlyList<LayoutEdge> edges,
      LayoutOptions options)
    {
      options = options ?? LayoutOptions.Default;
      var result = new Dictionary<int, PointD>();
      if (nodes == null || nodes.Count == 0) return result;

      var graph = new GeometryGraph();
      var byId = new Dictionary<int, MsaglNode>();
      var keyById = new Dictionary<int, Guid>();
      foreach (var n in nodes) keyById[n.Id] = n.Key;

      // Deterministic node creation order by the STABLE key (InstanceGuid), NOT the pivot-derived id - so a
      // re-layout (which churns ids) feeds MSAGL the same order and reproduces the same layout. Each node is
      // sized to the real component bounds.
      foreach (var n in nodes.OrderBy(n => n.Key))
      {
        var w = n.Width > MinExtent ? n.Width : FallbackSize;
        var h = n.Height > MinExtent ? n.Height : FallbackSize;
        var node = new MsaglNode(CurveFactory.CreateRectangle(w, h, new Point(0, 0)), n.Id);
        graph.Nodes.Add(node);
        byId[n.Id] = node;
      }

      // A single-node block has no layering to do - it sits at the origin (the engine packs it like any
      // other block). This also sidesteps MSAGL running a layout on a trivial graph.
      if (graph.Nodes.Count == 1)
      {
        var only = graph.Nodes.First();
        result[(int)only.UserData] = new PointD(only.Center.X, only.Center.Y);
        return result;
      }

      // WP-4.5: dedup PARALLEL edges (two components wired on several params share one layout edge) before
      // MSAGL, so a multi-wire connection does not bias the layering weight; self-loops are skipped.
      var seenEdges = new HashSet<(int, int)>();
      foreach (var e in (edges ?? Array.Empty<LayoutEdge>())
                 .Where(e => keyById.ContainsKey(e.FromId) && keyById.ContainsKey(e.ToId))
                 .OrderBy(e => keyById[e.FromId]).ThenBy(e => keyById[e.ToId]))   // stable (guid) edge order
      {
        if (e.FromId == e.ToId || !seenEdges.Add((e.FromId, e.ToId))) continue;
        if (byId.TryGetValue(e.FromId, out var a) && byId.TryGetValue(e.ToId, out var b) && !ReferenceEquals(a, b))
          graph.Edges.Add(new MsaglEdge(a, b));
      }

      var settings = new SugiyamaLayoutSettings
      {
        // MSAGL's layered layout flows top-to-bottom by default; a +90 deg rotation makes it left-to-right
        // (depth grows with x), which is Grasshopper's dataflow convention. Verified in WP-0.
        Transformation = PlaneTransformation.Rotation(Math.PI / 2),
        LayerSeparation = options.LayerSeparation,
        NodeSeparation = options.NodeSeparation,
      };

      LayoutHelpers.CalculateLayout(graph, settings, null);

      foreach (var node in graph.Nodes)
        result[(int)node.UserData] = new PointD(node.Center.X, node.Center.Y);
      return result;
    }
  }
}
