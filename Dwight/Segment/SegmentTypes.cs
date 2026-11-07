using System;
using System.Collections.Generic;
using System.Linq;

namespace Dwight.Segment
{
  // The headless segmenter DTOs (Phase 6 WP-B, RPST-first, ADR-087). GH-free, so the whole segmenter is
  // unit-tested outside Rhino. Boundaries come from DATAFLOW TOPOLOGY, not position - so a node carries only
  // its stable identity and whether it is a UI/terminal leaf; an edge carries only its direction. Position
  // and component type are deliberately absent: ADR-087 dropped position-proximity and same-type homogeneity
  // as boundary signals after the WP-B benchmark killed them (the adjacency delta was flat/negative on
  // scattered definitions - proximity just re-encodes the wires and echoes the author's layout). InstanceGuid
  // is the only ordering key and the final tie-break everywhere.

  /// <summary>A node to segment: stable identity, whether it is a UI/terminal leaf (slider, panel, value
  /// list, preview), and whether it is a bare PARAMETER component (a standalone data relay - Curve / Number /
  /// generic Param - the author's explicit seam marker; NOT a UI leaf). Both flags are the component's KIND,
  /// purely topological - no position, so they survive a scattered canvas.</summary>
  internal readonly struct SegNode
  {
    public readonly int Id;
    public readonly Guid Key;
    public readonly bool IsLeafType;
    public readonly bool IsParam;
    // the author's ORIGINAL pivot - used ONLY by the gated wire-length term as a weak real-layout bonus and a
    // tie-break, NEVER as a boundary signal and NEVER in the scattered==tidy path. Position is not a primary
    // criterion (it failed under scattering); it is a corroborating bonus where the real layout is present.
    public readonly double PivotX, PivotY;
    public SegNode(int id, Guid key, bool isLeafType, bool isParam = false, double pivotX = 0, double pivotY = 0)
    { Id = id; Key = key; IsLeafType = isLeafType; IsParam = isParam; PivotX = pivotX; PivotY = pivotY; }
  }

  /// <summary>A directed wire: source -> target. Direction is the whole signal RPST needs (dominance is
  /// defined on the directed dataflow).</summary>
  internal readonly struct SegEdge
  {
    public readonly int FromId, ToId;
    public SegEdge(int fromId, int toId) { FromId = fromId; ToId = toId; }
  }

  /// <summary>The directed dataflow graph to segment (the author's untouched topology).</summary>
  internal sealed class SegInput
  {
    public IReadOnlyList<SegNode> Nodes { get; }
    public IReadOnlyList<SegEdge> Edges { get; }
    public SegInput(IReadOnlyList<SegNode> nodes, IReadOnlyList<SegEdge> edges) { Nodes = nodes; Edges = edges; }
  }

  /// <summary>RPST-segmenter tuning (Stage 1). All thresholds are definition-relative, never absolute counts
  /// (ADR-087): the granularity is a size BAND descended to, not a fixed cluster count.</summary>
  internal sealed class SegOptions
  {
    /// <summary>A node is a fan-out HUB when its out-degree exceeds this multiple of the median out-degree -
    /// lifted out before RPST so a shared slider cannot collapse distinct sublogics (the resolution limit).</summary>
    public double HubMedianFactor = 4.0;
    /// <summary>...but never below this absolute out-degree (so a tiny median does not flag ordinary nodes).</summary>
    public int HubMinOutDegree = 6;
    /// <summary>Granularity band: lock an RPST fragment as a group when its interior size is in [min, max];
    /// keep descending when it is bigger; stop before hyper-fragmenting below the minimum.</summary>
    public int GranularityMin = 5;
    public int GranularityMax = 20;

    // ---- Stage 2 (betweenness + Louvain inside oversized rigid regions, ADR-087) ----------------------
    /// <summary>Whether Stage 2 subdivides oversized rigid regions. Off = Stage 1 only (the benchmark's
    /// on/off comparison, so a plausible-but-wrong Stage-2 split shows as no ARI improvement).</summary>
    public bool Stage2Enabled = true;
    /// <summary>The target sub-community SIZE Stage 2 tunes the modularity resolution toward (target count =
    /// region / this). This is a BENCHMARK-CALIBRATED tunable, NOT a fixed law: modularity will hit any count
    /// you ask for, so hitting the count is not success - the only acceptance signal is the rigid-file ARI
    /// against the hand-chunk. Same status as the hub threshold.</summary>
    public int Stage2TargetSize = 12;

    // ---- the two boundary-signal edge-weight terms (Stage 2), both topological -------------------------
    /// <summary>Signal 1 - a wire incident to a bare parameter component (the author's seam marker) is
    /// penalised, strongest at a param->param relay. Off for the benchmark's per-term delta.</summary>
    public bool ParamBoundaryEnabled = true;
    public double ParamBoundaryFactor = 0.4;
    /// <summary>Signal 2 - a long crossing edge INTO a region entry node is penalised. Gated by the over-fire
    /// guard below so it fires only on genuine entries, never on every cross-output edge of a tangle.</summary>
    public bool EntryCrossingEnabled = true;
    public double EntryCrossingFactor = 0.4;
    /// <summary>Over-fire guard: an entry node has at most this many real incoming edges (a narrow entry, not
    /// a convergence point). Build it tight; the benchmark relaxes it if needed.</summary>
    public int EntryMaxInDegree = 2;

    // ---- cleanup pass + the wire-length bonus --------------------------------------------------------
    /// <summary>After clustering, reassign clear misfits - a component more strongly WIRED to a neighbouring
    /// group than to its own moves to the group it is actually wired into. Connection strength is primary
    /// (topological, scatter-proof); geometric distance only corroborates a tie.</summary>
    public bool CleanupEnabled = true;
    public int CleanupPasses = 2;
    /// <summary>The wire-length bonus: a long drawn wire on the author's real layout is a "reaching to another
    /// unit" signal, so it weakly lowers an edge's weight (Stage 2) and breaks cleanup ties. LOW weight, never
    /// primary. Gated OFF for the scattered==tidy path so structure-only determinism is preserved; ON in
    /// production where the real layout is present. Its delta is measured separately like the other terms.</summary>
    public bool WireLengthEnabled = true;
    public double WireLengthFactor = 0.15;

    public static SegOptions Default => new SegOptions();
  }

  /// <summary>One region in the hierarchy: its id, level (0 = macro/locked group, deeper = finer), parent
  /// (-1 at the top), and the node ids it contains.</summary>
  internal sealed class SegRegion
  {
    public int Id { get; set; }
    public int Level { get; set; }
    public int ParentId { get; set; } = -1;
    public List<int> Members { get; } = new List<int>();
  }

  /// <summary>The segmenter's output. <see cref="Macro"/> (node id -&gt; locked-fragment id) is the partition
  /// the benchmark scores against human chunking; <see cref="Regions"/> carries the chosen fragments. Hubs and
  /// any nodes not inside a locked fragment land in their own region (the "global inputs" lane and strays).</summary>
  internal sealed class SegResult
  {
    public IReadOnlyDictionary<int, int> Macro { get; set; } = new Dictionary<int, int>();
    public IReadOnlyList<SegRegion> Regions { get; set; } = new List<SegRegion>();
    public IReadOnlyList<int> Hubs { get; set; } = new List<int>();
    public SegDiagnostics Diagnostics { get; set; } = new SegDiagnostics();
    public int MacroCount => Macro.Count == 0 ? 0 : Macro.Values.Distinct().Count();
  }

  /// <summary>The raw RPST stats (before granularity descent) plus the final group count - surfaced on the
  /// export command so the segmenter's structure is visible: whether the dataflow yielded real nested SESE
  /// regions, or fell to weakly-connected pieces, or is one giant tangle (where Stage 2 would be needed).</summary>
  internal sealed class SegDiagnostics
  {
    public int NodeCount;
    public int HubCount;
    public int RootInteriorSize;   // the spine size the RPST ran on (after leaf/hub handling)
    public int MatchedRegions;     // nested SESE regions found (0 = only the whole-graph region matched)
    public int RpstDepth;          // nesting depth of the region tree
    public int GroupCount;         // final groups after descent + connectivity split + Stage 2
    public int WeakComponents;     // weakly-connected components of the spine (the floor on group count)
    public int RigidRegions;       // oversized rigid groups Stage 1 left (candidates for Stage 2)
    public int RigidSubdivided;    // of those, how many Stage 2 actually split
  }
}
