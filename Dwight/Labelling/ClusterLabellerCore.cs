using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Dwight.Llm;

namespace Dwight.Labelling
{
  /// <summary>
  /// The GH-free orchestration of the labelling pass (Phase 6 WP-C): given the already-projected cluster
  /// <see cref="LabelInput"/> and per-cluster content hashes, decide which clusters miss the cache, make at
  /// most one model call through the <see cref="StructuredOutput"/> seam, parse + cache the result, and return
  /// a COMPLETE, usable set of labels + a coarse arrangement. Extracted from the GH-coupled
  /// <see cref="ClusterLabeller"/> (which reads the live <see cref="Dwight.Data.DwightGraph"/>) so this
  /// orchestration - the cache-hit/miss mixing and the failure fallback (WP-3.4) - is unit-tested headlessly
  /// against a fake provider.
  /// </summary>
  internal static class ClusterLabellerCore
  {
    internal sealed class Result
    {
      public bool Ok;
      public string Error;
      public Dictionary<int, string> Labels = new Dictionary<int, string>();
      public List<ArrangementHint> Arrangement = new List<ArrangementHint>();
      public LlmUsage Usage = new LlmUsage();
      public int ClusterCount;
      public int Billed;       // how many clusters were sent to the model (the rest were cached)
    }

    private static string SystemPrompt => Prompts.Prompts.GraphActionMaster + "\n" + Prompts.Prompts.LabelInstruction;

    private static string UserPrompt(string payload) => "Payload:\n" + payload;

    private static void ReadCache(LabelInput input, IReadOnlyDictionary<int, string> hashes, Dictionary<int, string> labels, HashSet<int> misses)
    {
      foreach (var c in input.Clusters)
      {
        if (hashes.TryGetValue(c.Id, out var h) && LabelCache.TryGet(h, out var cached)) labels[c.Id] = cached;
        else misses.Add(c.Id);
      }
    }

    /// <summary>Brief CS: what <see cref="LabelFrom"/> would send for this input, in characters, or 0 when every name and the
    /// arrangement are cached and it would send nothing. One reading of the cache and one prompt, so the size a Reorganise asks
    /// about is the size that goes out.</summary>
    public static int PromptChars(LabelInput input, IReadOnlyDictionary<int, string> hashes)
    {
      var labels = new Dictionary<int, string>();
      var misses = new HashSet<int>();
      ReadCache(input, hashes, labels, misses);
      if (misses.Count == 0 && LabelCache.TryGetArrangement(ArrangementHash(hashes, input.Flow), out _)) return 0;
      return SystemPrompt.Length + UserPrompt(ClusterSummaries.Payload(input, misses, labels)).Length;
    }

    public static Result LabelFrom(LabelInput input, IReadOnlyDictionary<int, string> hashes,
      ILlmProvider provider, int maxTokens, CancellationToken cancel = default)
    {
      var labels = new Dictionary<int, string>();
      var misses = new HashSet<int>();
      ReadCache(input, hashes, labels, misses);
      var cachedCount = input.Clusters.Count - misses.Count;

      var arrHash = ArrangementHash(hashes, input.Flow);
      var arrCached = LabelCache.TryGetArrangement(arrHash, out var cachedArr);   // a COPY (WP-3.4iii)

      var r = new Result { ClusterCount = input.Clusters.Count, Billed = misses.Count };
      if (misses.Count == 0 && arrCached)   // labels AND arrangement all cached - no model call at all
      { r.Ok = true; r.Labels = FillGeneric(labels, input); r.Arrangement = cachedArr; return r; }

      // ONE call, full picture: un-cached summaries + the cached labels (for context) + the full flow. The
      // model returns labels for the summarised clusters AND the coarse arrangement over the whole graph.
      // WP-3.4(ii): when every label is already cached (misses empty) the model is only being asked to arrange,
      // so validate the ARRANGEMENT only - requiring a non-empty labels array would reject a good reply.
      cancel.ThrowIfCancellationRequested();
      var arrangementOnly = misses.Count == 0;
      var payload = ClusterSummaries.Payload(input, misses, labels);
      var res = StructuredOutput.Get(provider, SystemPrompt, UserPrompt(payload),
        Prompts.Prompts.LabelToolName, Prompts.Prompts.LabelToolDescription, Prompts.Prompts.LabelSchema,
        arrangementOnly ? ClusterSummaries.ValidateArrangement : ClusterSummaries.Validate, maxTokens, cancel);
      r.Usage = res.Usage;

      List<ArrangementHint> arrangement;
      if (res.Ok)
      {
        var fresh = ClusterSummaries.ParseLabels(res.Json, misses);
        foreach (var kv in fresh)
        {
          labels[kv.Key] = kv.Value;
          if (hashes.TryGetValue(kv.Key, out var h)) LabelCache.Set(h, kv.Value);   // cache by content hash
        }
        var validIds = new HashSet<int>(input.Clusters.Select(c => c.Id));
        arrangement = ClusterSummaries.ParseArrangement(res.Json, validIds);   // relationships only, grounded
        LabelCache.SetArrangement(arrHash, arrangement);   // stored by copy (WP-3.4iii)
        LabelCache.Flush();   // WP-5: persist ONCE per pass, not per entry - what the model just cost is now durable
      }
      else
      {
        // WP-3.4(i): the labelling call failed - DON'T discard the labels we already had from cache; keep them
        // (the rest fall back to generic below) and reuse the cached arrangement if there is one.
        arrangement = arrCached ? cachedArr : new List<ArrangementHint>();
        r.Error = res.Error;
      }

      r.Labels = FillGeneric(labels, input);   // any still-missing label -> "Region N", so r.Labels is complete
      r.Arrangement = arrangement;
      // Usable if the call succeeded OR we kept meaningful cached names - only "all generic" is a real miss.
      r.Ok = res.Ok || cachedCount > 0;
      return r;
    }

    /// <summary>Ensure every cluster has a label: the ones we found (cached or fresh) plus a generic fallback for
    /// any that are still missing, so the result is always complete and usable (WP-3.4). The fallback is
    /// <see cref="RegionVocabulary.GenericLabel"/> - "Region N", never "Cluster N" (U29 #918): "Cluster" is a
    /// Grasshopper component type, and a model reading it believed its work had been collapsed into one.</summary>
    private static Dictionary<int, string> FillGeneric(Dictionary<int, string> labels, LabelInput input)
    {
      foreach (var c in input.Clusters) if (!labels.ContainsKey(c.Id)) labels[c.Id] = RegionVocabulary.GenericLabel(c.Id);
      return labels;
    }

    /// <summary>The arrangement cache key: the whole cluster-graph shape - the per-cluster CONTENT hashes (so
    /// it is definition-specific, never colliding across files) plus the flow edges. Order-independent.</summary>
    private static string ArrangementHash(IReadOnlyDictionary<int, string> contentHashes, IReadOnlyList<(int from, int to)> flow)
    {
      var tokens = new List<string>();
      foreach (var kv in contentHashes) tokens.Add("c:" + kv.Value);
      foreach (var (from, to) in flow) tokens.Add("f:" + from + ">" + to);
      return LabelCache.Hash(tokens);
    }
  }
}
