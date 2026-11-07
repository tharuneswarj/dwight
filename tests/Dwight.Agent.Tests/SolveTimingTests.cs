using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Dwight.Agent;
using Dwight.Data;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// Phase-9 U23 (lane C) - "THIS DEFINITION IS SLOW: WHERE IS THE TIME GOING?", answered from solves that
  /// already happened.
  ///
  /// Two properties decide whether this read tells the truth, and most of these tests are about them rather than
  /// about ranking:
  ///
  ///   1. THE FIGURES ARE EACH COMPONENT'S LAST COMPUTATION, NOT ONE SOLVE. A component that did not recompute
  ///      keeps its earlier time, so the figures do not add up to a solve's duration - and the read must say so
  ///      rather than invite the sum.
  ///   2. A ZERO IS NEVER READ ON ITS OWN (#843, distrust the zero). "Fast" and "never ran" both arrive as zero,
  ///      and they are opposite answers to "is this slow?".
  /// </summary>
  public class SolveTimingTests
  {
    // ---- the shared rounding: ONE function, and it must not have changed ---------------------------

    /// <summary>
    /// THE CHARACTERISATION THAT MAKES THE EXTRACTION SAFE. The rounding was inline in Simplify's payload and was
    /// moved out so the timing read could share it - one rounding of one number, or a user sees two answers to
    /// one question. A move must not change behaviour, so the exact Phase-7 arithmetic is pinned here, value by
    /// value, including the boundaries.
    /// </summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(2, 0)]
    [InlineData(3, 5)]
    [InlineData(7, 5)]
    [InlineData(8, 10)]
    [InlineData(12, 10)]
    [InlineData(13, 15)]
    [InlineData(1000, 1000)]
    public void The_bucket_is_simplifys_arithmetic_unchanged(int ms, int bucket)
    {
      Assert.Equal(bucket, SolveTimeBucket.Of(ms));
    }

    /// <summary>The zero-bucket threshold is DERIVED from the rounding, and this pins the derivation: a sentence
    /// that states the threshold by hand goes stale the day the rounding changes (#777).</summary>
    [Fact]
    public void The_largest_zero_reading_is_derived_from_the_rounding()
    {
      Assert.Equal(2, SolveTimeBucket.LargestZeroReading);
      Assert.Equal(0, SolveTimeBucket.Of(SolveTimeBucket.LargestZeroReading));
      Assert.NotEqual(0, SolveTimeBucket.Of(SolveTimeBucket.LargestZeroReading + 1));
      Assert.Equal("2.5 ms", SolveTimingReport.ZeroBoundText());
    }

    // ---- Simplify's hotspots: the guard tests the value that is sent (#875) --------------------------

    private static Dictionary<int, int> Hotspots(params (int Id, int Ms)[] readings) =>
      SolveTimeBucket.Hotspots(readings.Select(r => new KeyValuePair<int, int>(r.Id, r.Ms)));

    /// <summary>
    /// THE DEFECT (#875). The guard was <c>ms &gt;= 2</c> on the raw reading and the payload sent the bucket, where
    /// 2 ms rounds to 0 - so Simplify could name a component as a hotspot with <c>"ms": 0</c>, a figure saying it
    /// is not slow. Every reading that rounds to zero is checked, not just the one that broke, because the rule is
    /// "a zero bucket is never a hotspot", not "2 is not a hotspot".
    /// </summary>
    [Fact]
    public void A_component_whose_time_rounds_to_zero_is_never_a_hotspot()
    {
      for (var ms = 0; ms <= SolveTimeBucket.LargestZeroReading; ms++)
        Assert.Empty(Hotspots((1, ms)));

      var first = SolveTimeBucket.LargestZeroReading + 1;
      Assert.Equal(new Dictionary<int, int> { [1] = SolveTimeBucket.Of(first) }, Hotspots((1, first)));
    }

    [Fact]
    public void Every_hotspot_is_sent_with_a_nonzero_bucket()
    {
      var hot = Hotspots((1, 2), (2, 3), (3, 40), (4, 1), (5, 12));

      Assert.Equal(new[] { 3, 5, 2 }, hot.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).ToArray());
      Assert.All(hot.Values, v => Assert.True(v > 0));
      Assert.Equal(40, hot[3]);   // the bucket, never the raw reading
      Assert.Equal(10, hot[5]);
    }

    /// <summary>Phase 7's cap of eight is kept - and the cap applies to components that are measurably slow, so a
    /// selection of many sub-bucket components and a few slow ones names only the slow ones.</summary>
    [Fact]
    public void At_most_eight_hotspots_and_only_measurable_ones()
    {
      var readings = Enumerable.Range(1, 12).Select(i => (i, i * 10))
        .Concat(Enumerable.Range(100, 20).Select(i => (i, 2)))
        .ToArray();

      var hot = Hotspots(readings);

      Assert.Equal(SolveTimeBucket.HotspotCap, hot.Count);
      Assert.Equal(Enumerable.Range(5, 8).OrderBy(i => i), hot.Keys.OrderBy(i => i));
    }

    // ---- fixtures -----------------------------------------------------------------------------------

    private static SolveTimeFact Ran(int id, int ms, int? region = null, string type = "Loft", string nick = null) =>
      new SolveTimeFact { Id = id, Type = type, Nick = nick, Recorded = true, Ms = ms, Region = region };

    private static SolveTimeFact NeverRan(int id, int ms = 0, int? region = null) =>
      new SolveTimeFact { Id = id, Type = "Loft", Recorded = false, Ms = ms, Region = region };

    private static JsonObject Run(IEnumerable<SolveTimeFact> facts, IEnumerable<AgentCluster> regions = null,
      int limit = SolveTimingReport.DefaultLimit)
    {
      var r = SolveTimingReport.Build(facts.ToList(), (regions ?? new AgentCluster[0]).ToList(), "stamp", limit);
      Assert.True(r.Ok, r.Message);
      return (JsonObject)r.Result;
    }

    private static int[] ListedIds(JsonObject o) =>
      ((JsonArray)o["components"]).Select(c => c["id"].GetValue<int>()).ToArray();

    // ---- DISTRUST THE ZERO -------------------------------------------------------------------------

    /// <summary>
    /// THE CENTRAL TEST. A definition that has never been solved reads zero on every component - exactly what a
    /// fast one reads. An empty ranking there would tell the model "nothing is slow" about a definition that has
    /// not run. So nothing-recorded is its own statement, with the way through, and never an empty list.
    /// </summary>
    [Fact]
    public void A_definition_that_has_never_computed_says_so_rather_than_reading_as_fast()
    {
      var o = Run(new[] { NeverRan(1), NeverRan(2), NeverRan(3) });

      Assert.Equal(0, o["recorded"].GetValue<int>());
      Assert.Equal(3, o["notRecorded"].GetValue<int>());
      Assert.Equal(0, o["negligible"].GetValue<int>());
      Assert.NotNull(o["nothingRecorded"]);
      Assert.Contains("never runs a solve itself", o["nothingRecorded"].GetValue<string>());
      Assert.Null(o["components"]);   // no ranking at all - not an empty one that reads as an answer
    }

    /// <summary>A component that has not computed is never counted as NEGLIGIBLE, even though its reading is zero -
    /// "too quick to measure" and "did not run" are opposite facts, and the counts keep them apart.</summary>
    [Fact]
    public void A_zero_from_a_component_that_never_ran_is_not_counted_as_fast()
    {
      var o = Run(new[] { Ran(1, 0), Ran(2, 40), NeverRan(3) });

      Assert.Equal(2, o["recorded"].GetValue<int>());
      Assert.Equal(1, o["negligible"].GetValue<int>());   // only component 1
      Assert.Equal(1, o["notRecorded"].GetValue<int>());  // component 3, NOT folded into negligible
    }

    /// <summary>
    /// RECORDED WINS OVER THE READING. A component disabled after it ran can still hold its old processor time; it
    /// does not compute now, so it is not where the time is going and must not be listed as the slowest thing on
    /// the canvas on the strength of a stale number.
    /// </summary>
    [Fact]
    public void A_stale_time_on_a_component_that_does_not_compute_now_is_never_listed()
    {
      var o = Run(new[] { Ran(1, 10), NeverRan(2, ms: 900) });
      Assert.Equal(new[] { 1 }, ListedIds(o));
      Assert.Equal(1, o["notRecorded"].GetValue<int>());
    }

    /// <summary>Everything that ran was negligible: a real answer, stated as a fact about components - and it does
    /// NOT claim the definition is fast, because the preview and the solve's overhead are not measured here.</summary>
    [Fact]
    public void When_everything_that_ran_was_negligible_it_says_so_without_calling_the_definition_fast()
    {
      var o = Run(new[] { Ran(1, 0), Ran(2, 1), Ran(3, 2) });

      Assert.Equal(3, o["negligible"].GetValue<int>());
      Assert.Empty((JsonArray)o["components"]);
      var note = o["allNegligible"].GetValue<string>();
      Assert.Contains("2.5 ms", note);
      Assert.DoesNotContain("is fast", note, StringComparison.OrdinalIgnoreCase);
      Assert.Null(o["nothingRecorded"]);
    }

    // ---- WHAT THE FIGURES MEAN ---------------------------------------------------------------------

    /// <summary>
    /// THE PAYLOAD SAYS WHAT ITS NUMBERS ARE AND ARE NOT, on every answer. The likeliest wrong inference - that the
    /// figures add up to how long a solve takes - is exactly the one this read must not invite, because they come
    /// from different solves.
    /// </summary>
    [Fact]
    public void Every_answer_states_that_the_figures_are_not_one_solve_and_what_is_not_measured()
    {
      foreach (var o in new[] { Run(new[] { Ran(1, 40) }), Run(new[] { NeverRan(1) }), Run(new[] { Ran(1, 0) }) })
      {
        var m = o["measures"].GetValue<string>();
        Assert.Contains("LAST TIME IT COMPUTED", m);
        Assert.Contains("do not add up to a solve's duration", m);
        Assert.Contains("NOT MEASURED HERE", m);
        Assert.Contains("preview", m);
      }
    }

    /// <summary>No total is ever reported. A sum of last-computation times is not a measurement anybody took, and a
    /// field that looked like one would be read as the solve's duration.</summary>
    [Fact]
    public void No_total_solve_time_is_ever_reported()
    {
      var o = Run(new[] { Ran(1, 40), Ran(2, 30) });
      Assert.Null(o["total"]);
      Assert.Null(o["totalMs"]);
      Assert.Null(o["solveMs"]);
    }

    // ---- ONE REPRESENTATION OF ONE NUMBER ----------------------------------------------------------

    /// <summary>The unrounded reading never leaves the plugin - only its bucket does, through the same function
    /// Simplify's hotspot uses, so the two surfaces cannot give two answers about one component.</summary>
    [Fact]
    public void Only_the_bucket_leaves_never_the_raw_reading()
    {
      var o = Run(new[] { Ran(1, 13) });
      Assert.Equal(15, ((JsonArray)o["components"])[0]["ms"].GetValue<int>());
      Assert.Equal(SolveTimeBucket.Of(13), ((JsonArray)o["components"])[0]["ms"].GetValue<int>());
    }

    // ---- ranking and bounds ------------------------------------------------------------------------

    [Fact]
    public void Components_are_ranked_slowest_first_by_the_unrounded_reading()
    {
      // 12 and 11 both bucket to 10 - the unrounded reading still decides their order, so rounding never
      // silently reorders the list.
      var o = Run(new[] { Ran(1, 11), Ran(2, 12), Ran(3, 300), Ran(4, 40) });
      Assert.Equal(new[] { 3, 4, 2, 1 }, ListedIds(o));
    }

    [Fact]
    public void Past_the_limit_the_rest_are_counted_and_the_bound_is_stated()
    {
      var facts = Enumerable.Range(1, 14).Select(i => Ran(i, 10 * i)).ToList();
      var o = Run(facts, limit: 10);

      Assert.Equal(10, ((JsonArray)o["components"]).Count);
      var omitted = o["omitted"].GetValue<string>();
      Assert.StartsWith("4 more components", omitted);
      Assert.Contains("at most " + SolveTimingReport.MaxLimit, omitted);
    }

    [Fact]
    public void Nothing_is_said_about_omissions_when_nothing_was_omitted()
    {
      Assert.Null(Run(new[] { Ran(1, 40) })["omitted"]);
    }

    [Fact]
    public void A_nickname_is_sent_only_when_the_user_set_one()
    {
      var o = Run(new[] { Ran(1, 40, nick: "Roof"), Ran(2, 30, nick: "  ") });
      var arr = (JsonArray)o["components"];
      Assert.Equal("Roof", arr[0]["nick"].GetValue<string>());
      Assert.Null(arr[1]["nick"]);
    }

    // ---- where the time is, by region -------------------------------------------------------------

    private static AgentCluster Region(int id, string label) => new AgentCluster { Id = id, Label = label };

    /// <summary>The answer a user can act on is a REGION, not a list of four hundred components. Shares are of the
    /// UNROUNDED total, so rounding never moves a region's rank or its share.</summary>
    [Fact]
    public void Regions_are_ranked_by_their_share_of_the_recorded_time()
    {
      var o = Run(new[] { Ran(1, 70, region: 0), Ran(2, 20, region: 1), Ran(3, 10, region: 1) },
                  new[] { Region(0, "Roof panels"), Region(1, "Grid") });

      var regions = (JsonArray)o["regions"];
      Assert.Equal(0, regions[0]["id"].GetValue<int>());
      Assert.Equal(70, regions[0]["share"].GetValue<int>());
      Assert.Equal("Roof panels", regions[0]["label"].GetValue<string>());
      Assert.Equal(30, regions[1]["share"].GetValue<int>());
    }

    /// <summary>A region where nothing recorded time is not where the time goes, and is not listed - an entry at
    /// 0% is a row the reader has to work out is irrelevant.</summary>
    [Fact]
    public void A_region_with_no_recorded_time_is_not_listed()
    {
      var o = Run(new[] { Ran(1, 50, region: 0), NeverRan(2, region: 1) },
                  new[] { Region(0, "A"), Region(1, "B") });
      Assert.Single((JsonArray)o["regions"]);
    }

    [Fact]
    public void Past_the_region_cap_the_rest_are_counted()
    {
      var facts = Enumerable.Range(0, SolveTimingReport.RegionCap + 3).Select(i => Ran(i + 1, 10 + i, region: i)).ToList();
      var regions = Enumerable.Range(0, SolveTimingReport.RegionCap + 3).Select(i => Region(i, "R" + i)).ToList();
      var o = Run(facts, regions);

      Assert.Equal(SolveTimingReport.RegionCap, ((JsonArray)o["regions"]).Count);
      Assert.StartsWith("3 more regions", o["regionsOmitted"].GetValue<string>());
    }

    /// <summary>The partition stamp travels, so a region id from this read is refused on a changed canvas rather
    /// than resolved to a different region (#195).</summary>
    [Fact]
    public void The_partition_stamp_travels_with_the_region_ids()
    {
      Assert.Equal("stamp", Run(new[] { Ran(1, 40) })["partition"].GetValue<string>());
    }
  }
}
