using System.Collections.Generic;

namespace Dwight.Labelling
{
  // The headless DTOs for the minimal LLM labelling pass (Phase 6 WP-C). Like the segmenter, the format core
  // is GH-free so it is unit-tested outside Rhino. The summary is the ONLY thing sent to the model: a compact,
  // deterministic projection per cluster - never coordinates, never ids beyond a cluster index, never the raw
  // graph (ADR-056). The model returns only a short label per cluster id.

  /// <summary>One cluster's compact summary: what kinds of components it holds, what crosses its input and
  /// output boundary (with tree signatures), and its few most-central components. ~50-500 tokens.</summary>
  internal sealed class ClusterSummary
  {
    public int Id { get; set; }
    /// <summary>Component-type histogram, e.g. {"Loft":3,"Move":2}.</summary>
    public Dictionary<string, int> Types { get; } = new Dictionary<string, int>();
    /// <summary>What enters from other clusters: "&lt;type&gt; (&lt;tree signature&gt;)" phrases.</summary>
    public List<string> In { get; } = new List<string>();
    /// <summary>What leaves to other clusters.</summary>
    public List<string> Out { get; } = new List<string>();
    /// <summary>The few highest in-cluster-degree component types (what the cluster is "about").</summary>
    public List<string> Central { get; } = new List<string>();
  }

  /// <summary>The whole labelling request for one hierarchy level: every cluster's summary plus the
  /// inter-cluster flow edges, so the model names siblings coherently (it sees 0 -&gt; 2 -&gt; 5 and names them as
  /// a pipeline). Sent in ONE call.</summary>
  internal sealed class LabelInput
  {
    public IReadOnlyList<ClusterSummary> Clusters { get; }
    public IReadOnlyList<(int from, int to)> Flow { get; }
    public LabelInput(IReadOnlyList<ClusterSummary> clusters, IReadOnlyList<(int from, int to)> flow)
    { Clusters = clusters; Flow = flow; }
  }

  /// <summary>
  /// One COARSE arrangement relationship the model advises over the cluster graph (Phase 6 WP-C). It is a
  /// RELATIONSHIP, never a position: the model says which clusters relate how; the deterministic layout engine
  /// owns all geometry and realises it (ADR-056) - it decides the axis, the gap, and the pixels. The
  /// vocabulary is deliberately closed (a schema enum) so "where does this go" can never leak a coordinate:
  ///   row      - sit side by side, left to right (sequential stages);
  ///   stack    - stack vertically (parallel sublogics at the same stage);
  ///   grid     - arrange as a 2D grid (many peers);
  ///   adjacent - keep these near each other.
  /// </summary>
  internal sealed class ArrangementHint
  {
    public string Kind { get; set; }            // row | stack | grid | adjacent
    public List<int> Clusters { get; } = new List<int>();
  }
}
