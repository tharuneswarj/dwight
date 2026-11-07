using System;
using System.Collections.Generic;
using System.Linq;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;
using Dwight.Data;

namespace Dwight.Segment
{
  /// <summary>
  /// The Grasshopper adapter for the RPST segmenter (Phase 6 WP-B Stage 1, ADR-087): turns a
  /// <see cref="DwightGraph"/> + the live document into the headless <see cref="SegInput"/>. Because the
  /// segmenter reads dataflow TOPOLOGY and not position or type, this is now trivial - a node carries only its
  /// stable identity and whether it is a UI/terminal leaf (so a shared slider can be isolated as a hub), and
  /// an edge carries only its direction. No pivots, no Category, no tree-boundary reads: ADR-087 dropped
  /// position-proximity and same-type homogeneity as boundary signals after the benchmark killed them.
  /// GH-only, so not in the headless test set.
  /// </summary>
  internal static class GhSegment
  {
    public static SegInput BuildInput(DwightGraph graph, GH_Document doc, ISet<int> ids)
    {
      var nodes = new List<SegNode>();
      foreach (var n in graph.Nodes)
      {
        if (!ids.Contains(n.Id)) continue;
        var leaf = IsLeafType(Find(doc, n.Guid));
        // a bare parameter component (a standalone data relay) is a seam marker: param-kind, not a UI leaf.
        // This is the component's KIND from DwightGraph - topological, no position.
        var isParam = n.Kind == "param" && !leaf;
        // the original pivot is carried ONLY for the gated wire-length bonus (never a boundary signal).
        nodes.Add(new SegNode(n.Id, n.Guid, leaf, isParam, n.Pivot.X, n.Pivot.Y));
      }

      var edges = new List<SegEdge>();
      foreach (var e in graph.Edges)
        if (ids.Contains(e.FromNode) && ids.Contains(e.ToNode) && e.FromNode != e.ToNode)
          edges.Add(new SegEdge(e.FromNode, e.ToNode));   // source -> target: the dataflow direction RPST needs

      return new SegInput(nodes, edges);
    }

    private static IGH_DocumentObject Find(GH_Document doc, Guid g)
    {
      try { return doc?.FindObject(g, true); } catch { return null; }
    }

    /// <summary>A UI / terminal type (slider, panel, value list, toggle, swatch, scribble, button). A
    /// high-fan-out leaf is a shared-input hub the segmenter isolates.</summary>
    private static bool IsLeafType(IGH_DocumentObject obj)
    {
      switch (obj)
      {
        case null: return false;
        case GH_NumberSlider _:
        case GH_Panel _:
        case GH_ValueList _:
        case GH_BooleanToggle _:
        case GH_ColourSwatch _:
        case GH_Scribble _:
        case GH_ButtonObject _:
        case GH_MultiDimensionalSlider _:
          return true;
        default:
          return false;
      }
    }
  }
}
