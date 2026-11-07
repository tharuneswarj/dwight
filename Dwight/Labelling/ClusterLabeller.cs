using System.Collections.Generic;
using System.Threading;
using Dwight.Data;
using Dwight.Llm;

namespace Dwight.Labelling
{
  /// <summary>
  /// The minimal LLM labelling pass (Phase 6 WP-C). This is the GH ENTRY: it projects the live
  /// <see cref="DwightGraph"/> + the segmenter's cluster partition into the headless <see cref="LabelInput"/>
  /// and per-cluster content hashes (via <see cref="GhLabel"/>), then hands the actual orchestration - cache
  /// mixing, the one model call, parse/cache, and the failure fallback (WP-3.4) - to the GH-free, unit-tested
  /// <see cref="ClusterLabellerCore"/>. The model never sees the graph and never emits a coordinate; on a
  /// re-run with no edits every cluster is a cache hit and there is no LLM call at all.
  /// </summary>
  internal static class ClusterLabeller
  {
    public static ClusterLabellerCore.Result Label(DwightGraph graph, IReadOnlyDictionary<int, int> macro,
      ILlmProvider provider, CancellationToken cancel = default)
    {
      var input = GhLabel.BuildInput(graph, macro);          // UI thread: reads (materialised) signatures
      var hashes = GhLabel.ContentHashes(graph, macro);
      return ClusterLabellerCore.LabelFrom(input, hashes, provider, DwightConfig.MaxTokens, cancel);
    }

    /// <summary>Brief CS: the size of what <see cref="Label"/> would send, 0 when it would send nothing.</summary>
    public static int PromptChars(DwightGraph graph, IReadOnlyDictionary<int, int> macro) =>
      ClusterLabellerCore.PromptChars(GhLabel.BuildInput(graph, macro), GhLabel.ContentHashes(graph, macro));
  }
}
