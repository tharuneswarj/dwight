using System;
using System.Collections.Generic;

namespace Dwight.Layout
{
  // The headless layout DTOs (Phase 6 WP-A, ADR-063). Deliberately GH-free and MSAGL-free: the layout
  // engine reasons over plain ids + measured sizes so it can be unit-tested outside Rhino. The Grasshopper
  // adapter (GhLayout) builds these from a DwightGraph + live Attributes.Bounds; CanvasWriter applies the
  // result back. guid/pivot never appear here - only the DwightGraph short-int id crosses this boundary.

  /// <summary>A double-precision point in layout space. GH pivots are float; we keep the engine in double
  /// (MSAGL's precision) and convert only at the CanvasWriter boundary.</summary>
  internal readonly struct PointD
  {
    public readonly double X, Y;
    public PointD(double x, double y) { X = x; Y = y; }
  }

  /// <summary>A node to lay out: its stable id and its MEASURED size (real component width/height from
  /// Attributes.Bounds). Feeding the real size is what makes the layout overlap-free on real components -
  /// fixed rectangles break on the wide/tall ones (the phase doc's explicit warning).</summary>
  internal readonly struct LayoutNode
  {
    public readonly int Id;
    /// <summary>The STABLE identity (the component's InstanceGuid), used as the deterministic ordering key
    /// everywhere in the engine. The pivot-derived short-int <see cref="Id"/> is NOT stable across
    /// re-layouts - laying out moves a component's pivot, so the next DwightGraph snapshot re-sorts and
    /// assigns it a different Id (ADR-076 orders ids by pivot). Ordering on Id therefore made a re-run
    /// non-deterministic (the WP-A runtime bug: the layout cycled between arrangements). Ordering on Key is
    /// invariant to the layout, so a re-run reproduces byte-identically. Id remains only the per-run handle
    /// back to the graph.</summary>
    public readonly Guid Key;
    public readonly double Width, Height;
    public LayoutNode(int id, Guid key, double width, double height)
    { Id = id; Key = key; Width = width; Height = height; }
  }

  /// <summary>A directed wire: source node -> target node (param detail is not needed for layout).</summary>
  internal readonly struct LayoutEdge
  {
    public readonly int FromId, ToId;
    public LayoutEdge(int fromId, int toId) { FromId = fromId; ToId = toId; }
  }

  /// <summary>How a wire should display after layout (the 500/1000px norm from the research). Hidden is a
  /// recommendation only - the phase doc gates real hiding on named params at both ends, so the WP-D apply
  /// step decides whether a Hidden recommendation becomes Faint or a true hide.</summary>
  internal enum WireDisplay { Default, Faint, Hidden }

  /// <summary>The user-facing arrangement of the CLUSTER blocks (Phase 7 WP-1). Only the cluster-arrangement
  /// stage of the nested layout branches on this; the member-block layout INSIDE each cluster is untouched by
  /// the mode. The precedence law holds in every mode: HORIZONTAL is the flow (dependency depth from the
  /// cluster DAG, the engine's), and the LLM's arrangement hints refine ONLY the remaining vertical freedom
  /// (stacking order in grid/compact, tie-breaks within a band in chain) - a hint can never reorder along the
  /// flow (the same-layer + mutually-unreachable filter applies unchanged).</summary>
  internal enum ArrangementMode
  {
    /// <summary>The default: nested cluster layout + 2D forest packing toward the screen aspect. Dense,
    /// balanced, the general-purpose read (today's behaviour, named).</summary>
    Compact,
    /// <summary>Every cluster on ONE strict left-to-right row - a topological linearization of the cluster DAG
    /// (the sentence read). Edge-monotone (if A reaches B, A is left of B), so mutually-unreachable co-depth
    /// peers sit at different x; disconnected components are separate rows stacked below.</summary>
    HorizontalChain,
    /// <summary>Chain's vertical twin (WP-1 RR2): the same topological linearization run down the Y axis - if A
    /// reaches B, A sits ABOVE B - with disconnected components placed side-by-side as columns. The
    /// cluster-internal member layout stays left-to-right.</summary>
    Vertical,
    /// <summary>Flow layers become columns on a regular pitch; same-layer clusters stack vertically within
    /// their column. Maximum air and scanability - the poster read.</summary>
    Grid,
  }

  /// <summary>A wire-fade recommendation: which edge, what display, and the resolved length (px).</summary>
  internal readonly struct WireFade
  {
    public readonly int FromId, ToId;
    public readonly WireDisplay Display;
    public readonly double Length;
    public WireFade(int fromId, int toId, WireDisplay display, double length)
    { FromId = fromId; ToId = toId; Display = display; Length = length; }
  }

  /// <summary>Tunable layout constants (the eval harness / Tharun's runtime feedback refine these). Defaults
  /// chosen from the research: ~200px inter-block buffer, 500/1000px fade thresholds.</summary>
  internal sealed class LayoutOptions
  {
    /// <summary>Gap between dependency layers (the x / depth axis). Tightened from runtime feedback - the
    /// engine was pulling components further apart than needed; the no-overlap guarantee is independent of this.</summary>
    public double LayerSeparation = 48;
    /// <summary>Gap between same-depth siblings (the y axis - vertical division).</summary>
    public double NodeSeparation = 18;
    /// <summary>Buffer between weakly-connected components / clusters when packing the forest. Trimmed from
    /// ~200px so groups read compact rather than scattered (runtime feedback); still well clear of overlap.</summary>
    public double BlockBuffer = 120;
    /// <summary>Vertical space reserved at the TOP of each cluster for the group's scribble header, so the label
    /// sits INSIDE the group box (not floating above it) and neighbouring clusters keep clear. Only the nested
    /// cluster layout reads this; the headless block engine ignores it.</summary>
    public double HeaderHeight = 70;
    /// <summary>Horizontal space reserved on EACH side of a cluster for its boundary-param columns (inputs on the
    /// left, outputs on the right, snapped just outside the body). Without it the cluster-DAG packs clusters by
    /// body width alone and one group's output column collides with the next group's input column. Only the
    /// nested cluster layout reads this; the headless block engine ignores it.</summary>
    public double SideBand = 90;
    /// <summary>Target width/height ratio for 2D forest packing, so a tall forest becomes a screen-friendly
    /// grid of blocks rather than one impossibly-wide strip.</summary>
    public double TargetAspect = 1.6;
    /// <summary>Wire length (px) at/above which a wire is recommended Faint.</summary>
    public double FaintThreshold = 500;
    /// <summary>Wire length (px) at/above which a wire is recommended Hidden.</summary>
    public double HiddenThreshold = 1000;
    /// <summary>MSAGL lays out y-up; Grasshopper's canvas is y-down. Flip within each block so the layout
    /// reads the same orientation in GH as MSAGL intends. A one-flag toggle if Tharun sees it mirrored.</summary>
    public bool FlipY = true;
    /// <summary>The cluster-arrangement mode (Phase 7 WP-1). Only the nested cluster layout's arrangement stage
    /// reads this; Compact reproduces today's forest-packed behaviour exactly.</summary>
    public ArrangementMode Arrangement = ArrangementMode.Compact;

    public static LayoutOptions Default => new LayoutOptions();
  }

  /// <summary>The engine's output: each node's target CENTRE in canvas space, the wire-fade recommendations,
  /// and how many weakly-connected blocks were packed. The caller maps centre -> pivot via live bounds (a
  /// delta move), so the pivot-to-bounds relationship is preserved exactly.</summary>
  internal sealed class LayoutResult
  {
    public Dictionary<int, PointD> Centers { get; } = new Dictionary<int, PointD>();
    public List<WireFade> Fades { get; } = new List<WireFade>();
    public int BlockCount { get; set; }
  }
}
