using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Xunit;

namespace Dwight.Labelling.Tests
{
  /// <summary>
  /// Pins the WP-C send format: the per-cluster summary fields, the payload shape, the grounded/ capped parse,
  /// and the content-hash cache. The summary must be a compact deterministic projection - never a coordinate,
  /// never a raw id beyond the cluster index, never the wiring.
  /// </summary>
  public class ClusterSummariesTests
  {
    // two clusters: {1,2,3} (Curve/Divide/Divide) feeding {4,5} (Loft/Panel) across edge 3->4
    private static LabelInput Build()
    {
      var macro = new Dictionary<int, int> { [1] = 0, [2] = 0, [3] = 0, [4] = 1, [5] = 1 };
      var types = new Dictionary<int, string> { [1] = "Curve", [2] = "Divide", [3] = "Divide", [4] = "Loft", [5] = "Panel" };
      var edges = new List<(int, int)> { (1, 2), (2, 3), (3, 4), (4, 5) };
      var sigs = new Dictionary<(int, int), string> { [(3, 4)] = "4 branches, 1 each" };
      return ClusterSummaries.Build(macro, types, edges, sigs);
    }

    [Fact]
    public void Summary_has_histogram_boundaries_central_and_flow()
    {
      var input = Build();
      Assert.Equal(2, input.Clusters.Count);

      var c0 = input.Clusters.First(c => c.Id == 0);
      Assert.Equal(2, c0.Types["Divide"]);          // histogram
      Assert.Equal(1, c0.Types["Curve"]);
      Assert.Contains("Loft (4 branches, 1 each)", c0.Out);   // out-boundary carries the tree signature
      Assert.Empty(c0.In);                          // nothing enters cluster 0 from another cluster
      Assert.NotEmpty(c0.Central);

      var c1 = input.Clusters.First(c => c.Id == 1);
      Assert.Contains("Divide (4 branches, 1 each)", c1.In);  // what enters cluster 1

      Assert.Contains((0, 1), input.Flow);          // the inter-cluster flow edge
      Assert.DoesNotContain((1, 0), input.Flow);
    }

    [Fact]
    public void Payload_is_deterministic_and_carries_no_coordinates_or_guids()
    {
      var a = ClusterSummaries.Payload(Build());
      var b = ClusterSummaries.Payload(Build());
      Assert.Equal(a, b);                            // byte-identical

      Assert.DoesNotContain("pivot", a);
      Assert.DoesNotContain("guid", a);
      var root = JsonNode.Parse(a).AsObject();
      Assert.Equal("label.clusters", (string)root["action"]);
      Assert.Equal(2, root["clusters"].AsArray().Count);
    }

    [Fact]
    public void Parse_grounds_ids_and_caps_label_length()
    {
      var valid = new HashSet<int> { 0, 1 };
      var json = "{\"labels\":[{\"id\":0,\"label\":\"  Roof loft surface generator extra \"},{\"id\":1,\"label\":\"Panel output\"},{\"id\":9,\"label\":\"ghost\"}]}";
      var labels = ClusterSummaries.ParseLabels(json, valid, maxWords: 4);

      Assert.Equal("Roof loft surface generator", labels[0]);   // capped to 4 words, trimmed
      Assert.Equal("Panel output", labels[1]);
      Assert.False(labels.ContainsKey(9));                       // ungrounded id dropped
    }

    [Fact]
    public void Validate_requires_labels_and_arrangement()
    {
      Assert.True(ClusterSummaries.Validate("{\"labels\":[{\"id\":0,\"label\":\"x\"}],\"arrangement\":[]}").ok);  // empty arrangement is fine
      Assert.False(ClusterSummaries.Validate("{\"labels\":[{\"id\":0,\"label\":\"x\"}]}").ok);                    // missing arrangement
      Assert.False(ClusterSummaries.Validate("{\"labels\":[],\"arrangement\":[]}").ok);                           // empty labels
      Assert.False(ClusterSummaries.Validate("{}").ok);
    }

    [Fact]
    public void Arrangement_parse_grounds_ids_and_drops_unknown_kinds()
    {
      var valid = new HashSet<int> { 0, 1, 2, 5 };
      var json = "{\"labels\":[],\"arrangement\":[" +
        "{\"kind\":\"row\",\"clusters\":[0,2,5]}," +
        "{\"kind\":\"stack\",\"clusters\":[1,9]}," +     // 9 ungrounded -> leaves [1] (<2 clusters) -> hint dropped
        "{\"kind\":\"teleport\",\"clusters\":[0,1]}" +   // unknown kind -> dropped
        "]}";
      var hints = ClusterSummaries.ParseArrangement(json, valid);
      Assert.Single(hints);
      Assert.Equal("row", hints[0].Kind);
      Assert.Equal(new[] { 0, 2, 5 }, hints[0].Clusters);
    }

    [Fact]
    public void Arrangement_parse_reads_only_relationships_never_a_position()
    {
      // even if the model sneaks a coordinate into an item, only kind + clusters are ever read.
      var json = "{\"labels\":[],\"arrangement\":[{\"kind\":\"adjacent\",\"clusters\":[0,1],\"x\":100,\"y\":200,\"gap\":50}]}";
      var hints = ClusterSummaries.ParseArrangement(json, new HashSet<int> { 0, 1 });
      Assert.Single(hints);
      Assert.Equal("adjacent", hints[0].Kind);
      Assert.Equal(new[] { 0, 1 }, hints[0].Clusters);   // x/y/gap ignored - no geometry enters
    }

    [Fact]
    public void Payload_shows_cached_clusters_by_label_and_misses_by_summary()
    {
      // the full-picture, flat-budget shape: a cache hit is shown by its label (no token cost), a miss by its
      // full summary - the model sees every cluster plus the flow in one call.
      var input = Build();
      var payload = ClusterSummaries.Payload(input, new HashSet<int> { 1 }, new Dictionary<int, string> { [0] = "Curve divide" });
      var clusters = JsonNode.Parse(payload)["clusters"].AsArray();
      Assert.Equal(2, clusters.Count);

      var c0 = clusters.First(c => (int)c["id"] == 0).AsObject();
      Assert.Equal("Curve divide", (string)c0["label"]);   // cached cluster shown by label only
      Assert.False(c0.ContainsKey("types"));               // not re-summarised

      var c1 = clusters.First(c => (int)c["id"] == 1).AsObject();
      Assert.True(c1.ContainsKey("types"));                // the miss carries its full summary
    }

    [Fact]
    public void Cache_hash_is_order_independent_and_stable()
    {
      var h1 = LabelCache.Hash(new[] { "guidA", "Loft", "guidB", "Move" });
      var h2 = LabelCache.Hash(new[] { "Move", "guidB", "Loft", "guidA" });   // different order, same content
      Assert.Equal(h1, h2);

      var h3 = LabelCache.Hash(new[] { "guidA", "Loft", "guidC", "Move" });   // different content
      Assert.NotEqual(h1, h3);

      LabelCache.Clear();
      Assert.False(LabelCache.TryGet(h1, out _));
      LabelCache.Set(h1, "Roof loft");
      Assert.True(LabelCache.TryGet(h1, out var label));
      Assert.Equal("Roof loft", label);
    }
  }
}
