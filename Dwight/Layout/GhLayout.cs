using System;
using System.Collections.Generic;
using Grasshopper.Kernel;
using Dwight.Data;

namespace Dwight.Layout
{
  /// <summary>
  /// The Grasshopper adapter for the layout engine (Phase 6 WP-A): turns a <see cref="DwightGraph"/> + the
  /// live document into the headless <see cref="LayoutNode"/>/<see cref="LayoutEdge"/> input, reading each
  /// component's REAL measured size from <c>Attributes.Bounds</c>. Feeding the real size is the phase doc's
  /// hard requirement - fixed rectangles break no-overlap on the wide/tall real components. Runs on the UI
  /// thread (live bounds are only valid there); GH-only, so it is not part of the headless test set.
  /// </summary>
  internal static class GhLayout
  {
    private const double FallbackSize = 50.0;

    /// <summary>Build the engine input for a subset of graph nodes (the selection, or every node for a
    /// whole-canvas lay-out). Edges are those of the graph that fall entirely within the subset, so a
    /// selection lays out only on its internal wiring.</summary>
    public static (List<LayoutNode> nodes, List<LayoutEdge> edges) BuildInput(DwightGraph graph, GH_Document doc, ISet<int> ids)
    {
      var nodes = new List<LayoutNode>();
      foreach (var n in graph.Nodes)
      {
        if (!ids.Contains(n.Id)) continue;
        var (w, h) = MeasuredSize(doc, n.Guid);
        nodes.Add(new LayoutNode(n.Id, n.Guid, w, h));   // guid is the stable ordering key (re-run determinism)
      }

      var edges = new List<LayoutEdge>();
      foreach (var e in graph.Edges)
        if (ids.Contains(e.FromNode) && ids.Contains(e.ToNode))
          edges.Add(new LayoutEdge(e.FromNode, e.ToNode));

      return (nodes, edges);
    }

    /// <summary>The component's measured width/height from its live attributes, falling back to a small
    /// square when the bound is zero/uncomputed (a fresh or collapsed object) so layout never collapses.</summary>
    private static (double w, double h) MeasuredSize(GH_Document doc, Guid guid)
    {
      try
      {
        var b = doc?.FindObject(guid, true)?.Attributes?.Bounds;
        if (b.HasValue && b.Value.Width > 1 && b.Value.Height > 1) return (b.Value.Width, b.Value.Height);
      }
      catch { /* fail safe - degrade to the fallback size */ }
      return (FallbackSize, FallbackSize);
    }
  }
}
