using System.Collections.Generic;
using System.Linq;
using Dwight.Labelling;
using Xunit;

namespace Dwight.Labelling.Tests
{
  /// <summary>
  /// WP-5: the GH-free serialization core behind the label cache's on-disk persistence. The contract that
  /// matters is not "it round-trips" but "it can never cost the user a Reorganise": every bad input reads as an
  /// empty cache, and the bound keeps the file from growing without limit. The file I/O itself lives in
  /// <see cref="LabelCache"/> and is opt-in by the host, so nothing here touches %APPDATA%.
  /// </summary>
  public class LabelCacheStoreTests
  {
    private static LabelCacheStore.LabelDto L(string hash, string label, long seq) =>
      new LabelCacheStore.LabelDto { Hash = hash, Label = label, Seq = seq };

    [Fact]
    public void RoundTrips_labels_and_arrangements()
    {
      var arr = new LabelCacheStore.ArrangeDto
      {
        Hash = "shape-1",
        Seq = 7,
        Hints = new List<LabelCacheStore.HintDto>
        {
          new LabelCacheStore.HintDto { Kind = "row", Clusters = new List<int> { 1, 2 } },
          new LabelCacheStore.HintDto { Kind = "stack", Clusters = new List<int> { 3 } },
        },
      };
      var json = LabelCacheStore.Write(9, new[] { L("h1", "Roof", 5), L("h2", "Site Boundary", 9) }, new[] { arr });

      var file = LabelCacheStore.Read(json);

      Assert.Equal(LabelCacheStore.Version, file.Version);
      Assert.Equal(9, file.Seq);
      Assert.Equal("Roof", file.Labels.Single(e => e.Hash == "h1").Label);
      Assert.Equal(9, file.Labels.Single(e => e.Hash == "h2").Seq);
      var read = Assert.Single(file.Arrangements);
      Assert.Equal("shape-1", read.Hash);
      Assert.Equal(new[] { "row", "stack" }, read.Hints.Select(h => h.Kind));
      Assert.Equal(new[] { 1, 2 }, read.Hints[0].Clusters);
    }

    [Fact]
    public void Corrupt_file_reads_as_empty_never_throws()
    {
      foreach (var bad in new[] { "", "   ", "{", "not json at all", "[1,2,3]", "null" })
      {
        var file = LabelCacheStore.Read(bad);
        Assert.Empty(file.Labels);
        Assert.Empty(file.Arrangements);
      }
    }

    [Fact]
    public void Unknown_version_reads_as_empty_rather_than_being_guessed_at()
    {
      var json = LabelCacheStore.Write(3, new[] { L("h1", "Roof", 1) }, null)
        .Replace("\"version\":1", "\"version\":99");

      var file = LabelCacheStore.Read(json);

      Assert.Empty(file.Labels);   // a cold cache costs one re-bill; a misread cache would cost correctness
    }

    [Fact]
    public void Entries_with_no_hash_or_label_are_dropped()
    {
      var json = "{\"version\":1,\"seq\":2,\"labels\":[{\"h\":\"\",\"v\":\"Roof\",\"s\":1},{\"h\":\"h1\",\"s\":2},{\"h\":\"h2\",\"v\":\"Site\",\"s\":2}],\"arrangements\":null}";

      var file = LabelCacheStore.Read(json);

      Assert.Equal("h2", Assert.Single(file.Labels).Hash);
      Assert.Empty(file.Arrangements);
    }

    [Fact]
    public void Trim_keeps_the_most_recent_entries_at_the_cap()
    {
      var many = Enumerable.Range(1, LabelCacheStore.MaxEntries + 50).Select(i => L("h" + i, "label" + i, i)).ToList();

      var kept = LabelCacheStore.Trim(many, e => e.Seq);

      Assert.Equal(LabelCacheStore.MaxEntries, kept.Count);
      Assert.Equal(51, kept.First().Seq);                                  // the 50 oldest are evicted
      Assert.Equal(LabelCacheStore.MaxEntries + 50, kept.Last().Seq);      // ascending, so recency survives a re-read
    }

    [Fact]
    public void Write_bounds_the_file_to_the_cap()
    {
      var many = Enumerable.Range(1, LabelCacheStore.MaxEntries + 10).Select(i => L("h" + i, "label" + i, i));

      var file = LabelCacheStore.Read(LabelCacheStore.Write(999, many, null));

      Assert.Equal(LabelCacheStore.MaxEntries, file.Labels.Count);
      Assert.DoesNotContain(file.Labels, e => e.Hash == "h1");   // the oldest went, not a random one
    }

    [Fact]
    public void Read_heals_a_sequence_stamp_below_its_own_entries()
    {
      // a hand-edited or half-written file whose seq lags the entries would let the next write collide with an
      // existing stamp, corrupting the recency order the cap evicts by.
      var json = "{\"version\":1,\"seq\":0,\"labels\":[{\"h\":\"h1\",\"v\":\"Roof\",\"s\":42}],\"arrangements\":[]}";

      var file = LabelCacheStore.Read(json);

      Assert.Equal(42, file.Seq);
    }
  }
}
