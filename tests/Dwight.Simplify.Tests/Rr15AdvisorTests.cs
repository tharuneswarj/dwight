using System.Collections.Generic;
using System.Linq;
using Dwight.Simplify;
using Xunit;
using F = Dwight.Simplify.BestPracticeAdvisor.InputFacts;

namespace Dwight.Simplify.Tests
{
  /// <summary>WP-3: the deterministic best-practice advisor's DECISION. The lever is restraint - it must NOT nag
  /// (no Merge for every 2-source input, no extraction for every single Flatten), and it SUMMARIZES into one line
  /// per category. Under-suggesting beats a wall of suggestions. The GH read that gathers InputFacts is
  /// runtime-verified.</summary>
  public class Rr15AdvisorTests
  {
    [Fact]
    public void A_two_source_input_never_suggests_a_merge()
    {
      var advice = BestPracticeAdvisor.Suggest(new List<F> { new F(2, null), new F(1, null) });
      Assert.Empty(advice);
    }

    [Fact]
    public void High_fan_in_suggests_merge_once_summarized_not_per_input()
    {
      // Three inputs each merging 3+ wires -> ONE summary line, never three items.
      var advice = BestPracticeAdvisor.Suggest(new List<F> { new F(3, null), new F(4, null), new F(5, null) });
      Assert.Single(advice);
      Assert.Contains("3 inputs merge", advice[0].Observation);
      Assert.Contains("Merge", advice[0].Suggestion);
    }

    [Fact]
    public void A_single_tree_toggle_does_not_fire()
    {
      // One inline Flatten is ordinary - never nag about it.
      Assert.Empty(BestPracticeAdvisor.Suggest(new List<F> { new F(1, "flatten") }));
    }

    [Fact]
    public void Several_tree_toggles_summarize_to_one_line()
    {
      var advice = BestPracticeAdvisor.Suggest(new List<F> { new F(1, "flatten"), new F(1, "graft"), new F(1, "simplify") });
      Assert.Single(advice);
      Assert.Contains("reshape their data tree", advice[0].Observation);
    }

    [Fact]
    public void Flatten_heavy_gets_the_flattened_wording()
    {
      var toggles = Enumerable.Range(0, 5).Select(_ => new F(1, "flatten")).ToList();
      var advice = BestPracticeAdvisor.Suggest(toggles);
      Assert.Single(advice);
      Assert.Contains("flattened 5 inputs", advice[0].Observation);
    }

    [Fact]
    public void Both_categories_can_fire_but_stay_two_lines_max()
    {
      var advice = BestPracticeAdvisor.Suggest(new List<F> { new F(4, null), new F(1, "flatten"), new F(1, "graft") });
      Assert.Equal(2, advice.Count);   // one Merge line + one tree line - never per-input
    }

    [Fact]
    public void Empty_or_null_is_a_good_answer()
    {
      Assert.Empty(BestPracticeAdvisor.Suggest(new List<F>()));
      Assert.Empty(BestPracticeAdvisor.Suggest(null));
    }
  }
}
