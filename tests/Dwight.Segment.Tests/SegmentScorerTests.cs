using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Dwight.Segment.Tests
{
  /// <summary>
  /// Tests the benchmark scorer: ARI is label-invariant and behaves at the extremes, boundary agreement
  /// reads cut-edges correctly, and - the honesty guard - the summary keeps clean and messy files separate
  /// rather than averaging them into one number that could hide a layout-echoing segmenter.
  /// </summary>
  public class SegmentScorerTests
  {
    private static Dictionary<int, int> P(params (int id, int cl)[] xs) => xs.ToDictionary(x => x.id, x => x.cl);

    [Fact]
    public void Ari_of_identical_partitions_is_one()
    {
      var p = P((1, 0), (2, 0), (3, 1), (4, 1));
      Assert.Equal(1.0, SegmentScorer.AdjustedRandIndex(p, p), 10);
    }

    [Fact]
    public void Ari_is_label_invariant()
    {
      var a = P((1, 0), (2, 0), (3, 1), (4, 1));
      var b = P((1, 7), (2, 7), (3, 9), (4, 9));   // same partition, different labels
      Assert.Equal(1.0, SegmentScorer.AdjustedRandIndex(a, b), 10);
    }

    [Fact]
    public void Ari_of_all_one_versus_all_singletons_is_about_zero()
    {
      var allOne = P((1, 0), (2, 0), (3, 0), (4, 0), (5, 0), (6, 0));
      var singles = P((1, 1), (2, 2), (3, 3), (4, 4), (5, 5), (6, 6));
      Assert.True(Math.Abs(SegmentScorer.AdjustedRandIndex(allOne, singles)) < 1e-9);
    }

    [Fact]
    public void Ari_of_a_partial_agreement_is_between_zero_and_one()
    {
      var a = P((1, 0), (2, 0), (3, 0), (4, 1), (5, 1), (6, 1));
      var b = P((1, 0), (2, 0), (3, 1), (4, 1), (5, 1), (6, 1));   // one node moved
      var ari = SegmentScorer.AdjustedRandIndex(a, b);
      Assert.True(ari > 0.0 && ari < 1.0, $"expected partial agreement, got {ari}");
    }

    [Fact]
    public void Boundary_agreement_is_perfect_when_partitions_match()
    {
      var seg = P((1, 0), (2, 0), (3, 1), (4, 1));
      var edges = new[] { (1, 2), (2, 3), (3, 4) };   // 2-3 is the cut
      var (p, r, f1) = SegmentScorer.BoundaryAgreement(seg, seg, edges);
      Assert.Equal(1.0, f1, 10);
      Assert.Equal(1.0, p, 10);
      Assert.Equal(1.0, r, 10);
    }

    [Fact]
    public void Boundary_recall_drops_when_the_segmenter_misses_a_human_cut()
    {
      var human = P((1, 0), (2, 0), (3, 1), (4, 1));   // human cuts 2-3
      var seg = P((1, 0), (2, 0), (3, 0), (4, 0));      // segmenter merges everything (no cuts)
      var edges = new[] { (1, 2), (2, 3), (3, 4) };
      var (_, recall, _) = SegmentScorer.BoundaryAgreement(seg, human, edges);
      Assert.Equal(0.0, recall, 10);   // missed the only human cut
    }

    [Fact]
    public void Summary_keeps_clean_and_messy_separate_and_never_averages_them()
    {
      var scores = new[]
      {
        new SegmentScorer.FileScore("clean1", messy: false, ari: 0.90, boundaryF1: 0.92),
        new SegmentScorer.FileScore("clean2", messy: false, ari: 0.88, boundaryF1: 0.90),
        new SegmentScorer.FileScore("messy1", messy: true,  ari: 0.40, boundaryF1: 0.45),
        new SegmentScorer.FileScore("messy2", messy: true,  ari: 0.50, boundaryF1: 0.55),
      };
      var s = SegmentScorer.Summarise(scores);
      Assert.Equal(2, s.CleanCount);
      Assert.Equal(2, s.MessyCount);
      Assert.Equal(0.89, s.CleanAri, 6);
      Assert.Equal(0.45, s.MessyAri, 6);
      // the headline gap a single average would have hidden: 0.89 clean vs 0.45 messy
      Assert.True(s.CleanAri - s.MessyAri > 0.3, "the clean-vs-messy gap must remain visible");
    }
  }
}
