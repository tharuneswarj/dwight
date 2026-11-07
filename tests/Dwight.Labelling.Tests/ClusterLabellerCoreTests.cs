using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Dwight.Labelling;
using Dwight.Llm;
using Xunit;

// LabelCache is a shared static store, so these tests must not run concurrently with each other or the other
// LabelCache-touching tests - run the whole labelling test assembly sequentially.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Dwight.Labelling.Tests
{
  /// <summary>
  /// Orchestration tests for the GH-free labelling core (WP-3.4): the cache hit/miss mixing, the failure
  /// fallback that keeps cached labels instead of discarding them, and the arrangement-only validation path.
  /// A <see cref="FakeProvider"/> stands in for the LLM so the whole flow (through the StructuredOutput seam)
  /// runs headlessly. Each test clears the shared cache first.
  /// </summary>
  public class ClusterLabellerCoreTests
  {
    // ---- fake provider + builders ----

    private sealed class FakeProvider : ILlmProvider
    {
      private readonly Queue<LlmResponse> _responses = new Queue<LlmResponse>();
      public int Calls;
      public LlmRequest LastRequest;
      public FakeProvider(params LlmResponse[] responses) { foreach (var r in responses) _responses.Enqueue(r); }
      public string ModelId => "fake";
      public ProviderCapability Capability => ProviderCapability.ToolCall;
      public LlmResponse Send(LlmRequest request, CancellationToken cancel = default)
      {
        Calls++;
        LastRequest = request;
        return _responses.Count > 0 ? _responses.Dequeue() : new LlmResponse();   // empty => null candidate => a failure
      }
    }

    private static LlmResponse Ok(string structuredJson) => new LlmResponse { StructuredJson = structuredJson };

    private static LabelInput Input(int n, params (int, int)[] flow)
    {
      var clusters = new List<ClusterSummary>();
      for (var i = 0; i < n; i++) { var c = new ClusterSummary { Id = i }; c.Types["Comp"] = 1; clusters.Add(c); }
      return new LabelInput(clusters, flow.ToList());
    }

    private static Dictionary<int, string> Hashes(int n)
    {
      var d = new Dictionary<int, string>();
      for (var i = 0; i < n; i++) d[i] = "hash-" + i;
      return d;
    }

    // ---- tests ----

    [Fact]
    public void The_size_a_Reorganise_asks_about_is_the_size_that_goes_out()
    {
      LabelCache.Clear();
      var chars = ClusterLabellerCore.PromptChars(Input(3, (0, 1), (1, 2)), Hashes(3));
      var provider = new FakeProvider(Ok("{\"labels\":[{\"id\":0,\"label\":\"A\"},{\"id\":1,\"label\":\"B\"},{\"id\":2,\"label\":\"C\"}],\"arrangement\":[]}"));
      ClusterLabellerCore.LabelFrom(Input(3, (0, 1), (1, 2)), Hashes(3), provider, 4000);
      Assert.True(chars > 0);
      Assert.Equal(provider.LastRequest.System.Length + provider.LastRequest.Messages[0].Blocks[0].Text.Length, chars);
    }

    [Fact]
    public void A_pass_whose_names_and_arrangement_are_cached_has_nothing_to_ask_about()
    {
      LabelCache.Clear();
      var provider = new FakeProvider(Ok("{\"labels\":[{\"id\":0,\"label\":\"A\"},{\"id\":1,\"label\":\"B\"}],\"arrangement\":[]}"));
      ClusterLabellerCore.LabelFrom(Input(2, (0, 1)), Hashes(2), provider, 4000);
      Assert.Equal(0, ClusterLabellerCore.PromptChars(Input(2, (0, 1)), Hashes(2)));
    }

    [Fact]
    public void A_pass_that_only_needs_a_new_cluster_named_is_sized_for_that_call()
    {
      LabelCache.Clear();
      var provider = new FakeProvider(Ok("{\"labels\":[{\"id\":0,\"label\":\"A\"},{\"id\":1,\"label\":\"B\"}],\"arrangement\":[]}"));
      ClusterLabellerCore.LabelFrom(Input(2, (0, 1)), Hashes(2), provider, 4000);
      var chars = ClusterLabellerCore.PromptChars(Input(3, (0, 1), (1, 2)), Hashes(3));
      var again = new FakeProvider(Ok("{\"labels\":[{\"id\":2,\"label\":\"C\"}],\"arrangement\":[]}"));
      ClusterLabellerCore.LabelFrom(Input(3, (0, 1), (1, 2)), Hashes(3), again, 4000);
      Assert.Equal(again.LastRequest.System.Length + again.LastRequest.Messages[0].Blocks[0].Text.Length, chars);
    }

    [Fact]
    public void Missing_labels_are_named_by_one_model_call()
    {
      LabelCache.Clear();
      var provider = new FakeProvider(Ok("{\"labels\":[{\"id\":0,\"label\":\"Roof\"},{\"id\":1,\"label\":\"Site\"}],\"arrangement\":[]}"));
      var r = ClusterLabellerCore.LabelFrom(Input(2, (0, 1)), Hashes(2), provider, 4000);
      Assert.Equal(1, provider.Calls);
      Assert.True(r.Ok);
      Assert.Equal("Roof", r.Labels[0]);
      Assert.Equal("Site", r.Labels[1]);
      Assert.Equal(2, r.Billed);
    }

    [Fact]
    public void Rerun_with_unchanged_input_hits_cache_and_skips_the_model()
    {
      LabelCache.Clear();
      var first = new FakeProvider(Ok("{\"labels\":[{\"id\":0,\"label\":\"Roof\"},{\"id\":1,\"label\":\"Site\"}],\"arrangement\":[{\"kind\":\"row\",\"clusters\":[0,1]}]}"));
      ClusterLabellerCore.LabelFrom(Input(2, (0, 1)), Hashes(2), first, 4000);
      Assert.Equal(1, first.Calls);

      var second = new FakeProvider();   // would fail if it were called
      var r = ClusterLabellerCore.LabelFrom(Input(2, (0, 1)), Hashes(2), second, 4000);
      Assert.Equal(0, second.Calls);     // labels AND arrangement all cached - no model call
      Assert.True(r.Ok);
      Assert.Equal("Roof", r.Labels[0]);
      Assert.Single(r.Arrangement);
    }

    [Fact]
    public void A_failed_call_keeps_cached_labels_and_fills_the_rest_generic()
    {
      // WP-3.4(i): one cluster was named on a previous run (cached); the labelling call now fails. The cached
      // name must be KEPT (not thrown away with an all-generic result), and the uncached one falls back generic.
      LabelCache.Clear();
      LabelCache.Set("hash-0", "Roof");
      var provider = new FakeProvider();   // fails: no usable structured result
      var r = ClusterLabellerCore.LabelFrom(Input(2, (0, 1)), Hashes(2), provider, 4000);
      Assert.Equal("Roof", r.Labels[0]);       // cached label kept
      Assert.Equal("Region 1", r.Labels[1]);   // generic fallback for the miss (U29 #918: never "Cluster 1")
      Assert.True(r.Ok);                        // still usable - some meaningful names survived
    }

    [Fact]
    public void A_total_failure_with_no_cache_is_all_generic_and_not_ok()
    {
      LabelCache.Clear();
      var provider = new FakeProvider();   // fails, and nothing was cached
      var r = ClusterLabellerCore.LabelFrom(Input(2, (0, 1)), Hashes(2), provider, 4000);
      Assert.Equal("Region 0", r.Labels[0]);
      Assert.Equal("Region 1", r.Labels[1]);
      Assert.False(r.Ok);                  // no meaningful names at all
    }

    [Fact]
    public void The_generic_fallback_is_never_a_grasshopper_component_type_word()
    {
      // U29 (#918): the fallback used to be "Cluster N", and a model reading the overview believed its work had
      // been collapsed into a Grasshopper Cluster. The fallback's noun is held against a fixture of installed type
      // names - with the old word kept as the positive control, so the rule is seen to find something (#212).
      LabelCache.Clear();
      var r = ClusterLabellerCore.LabelFrom(Input(3), Hashes(3), new FakeProvider(), 4000);
      foreach (var label in r.Labels.Values)
      {
        Assert.True(RegionVocabulary.IsGeneric(label), label);
        var noun = label.Substring(0, label.IndexOf(' '));
        Assert.Empty(RegionVocabulary.Collisions(RegionVocabularyTests.NativeTypeNames, new[] { noun }));
      }
      Assert.Equal(new[] { "Cluster" }, RegionVocabulary.Collisions(RegionVocabularyTests.NativeTypeNames, new[] { "Cluster" }));
    }

    [Fact]
    public void Labels_cached_but_arrangement_missing_validates_arrangement_only()
    {
      // WP-3.4(ii): every label is cached, only the arrangement is missing, so the model is asked to arrange
      // only. Its reply carries NO labels array - which the full label validator would reject, but the
      // arrangement-only validator accepts, so the arrangement is parsed and applied.
      LabelCache.Clear();
      LabelCache.Set("hash-0", "Roof");
      LabelCache.Set("hash-1", "Site");
      var provider = new FakeProvider(Ok("{\"arrangement\":[{\"kind\":\"stack\",\"clusters\":[0,1]}]}"));
      var r = ClusterLabellerCore.LabelFrom(Input(2, (0, 1)), Hashes(2), provider, 4000);
      Assert.Equal(1, provider.Calls);
      Assert.Equal("Roof", r.Labels[0]);
      Assert.Equal("Site", r.Labels[1]);
      Assert.Single(r.Arrangement);            // proved: the labels-less reply was accepted (arrangement-only)
      Assert.Equal("stack", r.Arrangement[0].Kind);
    }
  }
}
