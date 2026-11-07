using System.Collections.Generic;
using System.Linq;
using Dwight.Agent;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// Phase-9 U2: the two pure halves of the large-document tools (study §6, finding 5). The bounding-box
  /// pre-filter that keeps find_intersecting from testing every pair, and the ranking cap that keeps
  /// spatial_query from returning the whole document. Doubles in, decisions out; RhinoCommon never enters.
  /// </summary>
  public class GeometryFilterTests
  {
    private static (double[] min, double[] max) Box(double x0, double x1) =>
      (new[] { x0, 0.0, 0.0 }, new[] { x1, 1.0, 1.0 });

    [Fact]
    public void TWO_BOXES_OVERLAP_WHEN_THEY_SHARE_VOLUME_AND_A_TOUCH_COUNTS_WITHIN_TOLERANCE()
    {
      var (aMin, aMax) = Box(0, 1);
      Assert.True(BoxOverlap.Overlaps(aMin, aMax, Box(0.5, 2).min, Box(0.5, 2).max, tol: 0.001));
      Assert.False(BoxOverlap.Overlaps(aMin, aMax, Box(2, 3).min, Box(2, 3).max, tol: 0.001));
      Assert.True(BoxOverlap.Overlaps(aMin, aMax, Box(1.0005, 3).min, Box(1.0005, 3).max, tol: 0.001));   // a touch, inside tol
      Assert.False(BoxOverlap.Overlaps(aMin, aMax, Box(1.01, 3).min, Box(1.01, 3).max, tol: 0.001));
    }

    [Fact]
    public void THE_PREFILTER_RETURNS_ONLY_CANDIDATE_PAIRS_AND_NEVER_A_PAIR_WITH_ITSELF()
    {
      var boxes = new List<(double[] min, double[] max)> { Box(0, 1), Box(0.5, 1.5), Box(5, 6) };
      var pairs = BoxOverlap.Prefilter(boxes, boxes, tol: 0.001, maxPairs: 100, out var capped, selfPairs: true);

      Assert.False(capped);
      Assert.Single(pairs);
      Assert.Equal((0, 1), pairs[0]);
    }

    [Fact]
    public void THE_PREFILTER_CAPS_ITS_PAIR_LIST_AND_SAYS_SO()
    {
      var boxes = Enumerable.Range(0, 30).Select(_ => Box(0, 1)).ToList();   // every pair overlaps: 435 of them
      var pairs = BoxOverlap.Prefilter(boxes, boxes, tol: 0.001, maxPairs: 50, out var capped, selfPairs: true);

      Assert.True(capped);
      Assert.Equal(50, pairs.Count);
    }

    [Fact]
    public void ACROSS_TWO_SETS_EVERY_A_IS_PAIRED_WITH_EVERY_OVERLAPPING_B()
    {
      var a = new List<(double[] min, double[] max)> { Box(0, 1), Box(10, 11) };
      var b = new List<(double[] min, double[] max)> { Box(0.5, 2), Box(10.5, 12), Box(50, 51) };
      var pairs = BoxOverlap.Prefilter(a, b, tol: 0.001, maxPairs: 100, out _, selfPairs: false);

      Assert.Equal(2, pairs.Count);
      Assert.Contains((0, 0), pairs);
      Assert.Contains((1, 1), pairs);
    }

    [Fact]
    public void NEAREST_RANKS_BY_DISTANCE_CAPS_AND_COUNTS_THE_REST()
    {
      var hits = new List<(int Index, double Distance)> { (3, 9.0), (1, 1.0), (2, 4.0), (0, 0.5) };
      var ranked = RankCap.Nearest(hits, limit: 2, out var omitted);

      Assert.Equal(new[] { 0, 1 }, ranked.Select(h => h.Index).ToArray());
      Assert.Equal(2, omitted);
    }
  }
}
