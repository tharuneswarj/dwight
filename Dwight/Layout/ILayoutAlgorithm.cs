using System.Collections.Generic;

namespace Dwight.Layout
{
  /// <summary>
  /// The swappable layered-layout core (ADR-063). MSAGL (<see cref="MsaglLayout"/>) is the committed
  /// implementation behind this seam; a hand-ported Sugiyama pipeline (network-simplex layering +
  /// size/port-aware Brandes-Kopf) is the documented, unbuilt fallback - kept swappable for clean
  /// substitution, NOT as a hedge (MSAGL is confirmed in the Rhino 8 host, WP-0).
  ///
  /// An implementation lays out ONE weakly-connected DAG. The engine (<see cref="LayoutEngine"/>) owns the
  /// graph-wide concerns the layered core does not: splitting the forest into connected components,
  /// packing those blocks in 2D, and computing wire-fade. Implementations MUST be deterministic - identical
  /// input yields identical output - so a re-run never scrambles the user's spatial memory.
  /// </summary>
  internal interface ILayoutAlgorithm
  {
    /// <summary>Lay out a single weakly-connected DAG with measured node sizes, left-to-right (depth on x).
    /// Returns each node's centre in a LOCAL coordinate space (origin arbitrary; the engine translates the
    /// block during packing). A single-node block returns that node at the origin.</summary>
    IReadOnlyDictionary<int, PointD> LayoutBlock(
      IReadOnlyList<LayoutNode> nodes,
      IReadOnlyList<LayoutEdge> edges,
      LayoutOptions options);
  }
}
