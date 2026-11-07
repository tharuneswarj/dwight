using System;
using System.Collections.Generic;
using System.Linq;
using Dwight.Components;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// Stage 0: the whitelist's GH-free half. Two things are under test and both are load-bearing for the
  /// agent surface - NORMALISATION (a trivially different spelling of an installed component must not
  /// read as a false absence - the ADR-083 lesson) and NEAREST-MATCH RANKING (the repair payload that
  /// turns an unknown_component_type refusal into something the model can act on, #190).
  /// </summary>
  public class ComponentMatchCoreTests
  {
    // A representative slice of real Grasshopper component names.
    private static readonly string[] Installed =
    {
      "Loft", "Extrude", "Series", "Number Slider", "Panel", "Divide Curve", "Curve",
      "Construct Point", "Deconstruct Point", "Merge", "Graft Tree", "Flatten Tree", "Move",
    };

    // ---- normalisation -------------------------------------------------------------------------

    [Theory]
    [InlineData("Series", "series")]
    [InlineData("  Series  ", "series")]
    [InlineData("Series.", "series")]
    [InlineData("Divide   Curve", "divide curve")]
    [InlineData("DIVIDE-CURVE", "dividecurve")]   // punctuation is DROPPED, not spaced
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Normalize_is_lowercase_single_spaced_and_punctuation_free(string input, string expected) =>
      Assert.Equal(expected, ComponentMatchCore.Normalize(input));

    // ---- nearest-match ranking -----------------------------------------------------------------

    [Fact]
    public void Exact_name_ranks_first()
    {
      var near = ComponentMatchCore.Nearest("Loft", Installed);
      Assert.Equal("Loft", near[0]);
    }

    [Fact]
    public void A_case_and_punctuation_variant_still_finds_the_component()
    {
      Assert.Equal("Divide Curve", ComponentMatchCore.Nearest("divide-curve", Installed)[0]);
      Assert.Equal("Number Slider", ComponentMatchCore.Nearest("NUMBER SLIDER", Installed)[0]);
    }

    [Fact]
    public void A_near_miss_typo_suggests_the_real_component()
    {
      // The case the refusal payload exists for: the model invented a plausible-but-wrong name.
      Assert.Equal("Loft", ComponentMatchCore.Nearest("Lofr", Installed)[0]);
      Assert.Contains("Extrude", ComponentMatchCore.Nearest("Extude", Installed));
    }

    [Fact]
    public void A_prefix_query_finds_the_longer_installed_name()
    {
      var near = ComponentMatchCore.Nearest("Construct", Installed);
      Assert.Equal("Construct Point", near[0]);
    }

    [Fact]
    public void An_exact_match_outranks_a_shorter_unrelated_name()
    {
      // Band dominates length: "Graft Tree" must not lose to a shorter fuzzy hit.
      var near = ComponentMatchCore.Nearest("Graft Tree", Installed);
      Assert.Equal("Graft Tree", near[0]);
    }

    [Fact]
    public void A_wholly_unrelated_query_suggests_nothing_rather_than_noise()
    {
      // An honest empty list beats a confidently wrong suggestion - the demote-to-advice spirit.
      Assert.Empty(ComponentMatchCore.Nearest("zzzzzzzzqqqqqqq", Installed));
    }

    [Fact]
    public void An_empty_query_suggests_nothing()
    {
      Assert.Empty(ComponentMatchCore.Nearest("", Installed));
      Assert.Empty(ComponentMatchCore.Nearest(null, Installed));
    }

    [Fact]
    public void Results_are_capped_and_deduplicated()
    {
      var dupes = new List<string> { "Loft", "Loft", "Lofted", "Loft" };
      var near = ComponentMatchCore.Nearest("Loft", dupes, max: 5);
      Assert.Equal(near.Distinct().Count(), near.Count);

      Assert.True(ComponentMatchCore.Nearest("Curve", Installed, max: 2).Count <= 2);
    }

    [Fact]
    public void Ranking_is_deterministic_across_input_order()
    {
      // Byte-stable suggestions: the same query returns the same list regardless of enumeration order,
      // the same discipline the layout engine keys off InstanceGuid for.
      var forward = ComponentMatchCore.Nearest("Curve", Installed);
      var reversed = ComponentMatchCore.Nearest("Curve", Installed.Reverse().ToArray());
      Assert.Equal(forward, reversed);
    }

    [Fact]
    public void Null_candidate_list_is_handled_rather_than_thrown()
    {
      Assert.Empty(ComponentMatchCore.Nearest("Loft", null));   // fail safe, ADR-029
    }

    // ---- the distance primitive ----------------------------------------------------------------

    [Theory]
    [InlineData("loft", "loft", 0)]
    [InlineData("loft", "lofr", 1)]
    [InlineData("loft", "lof", 1)]
    [InlineData("", "abc", 3)]
    [InlineData("abc", "", 3)]
    public void Levenshtein_measures_the_expected_distance(string a, string b, int expected) =>
      Assert.Equal(expected, ComponentMatchCore.Levenshtein(a, b, cap: 99));

    [Fact]
    public void Levenshtein_abandons_past_the_cap_rather_than_computing_on()
    {
      Assert.Equal(3, ComponentMatchCore.Levenshtein("abcdefgh", "zzzzzzzz", cap: 2));  // cap + 1
    }

    // ---- #975: nearest on the NAME part of a qualified query ----------------------------------------------------

    private static readonly string[] NumberLike = { "Partition Number", "Mirror Cut Numbers", "Number Slider", "Number", "Integer" };

    [Fact]
    public void Nearest_for_a_qualified_query_ranks_on_the_name_part()
    {
      var near = Dwight.Components.ComponentMatchCore.Nearest("Params > Input > Number", NumberLike);
      Assert.Equal("Number", near[0]);                                   // was never offered at all
      Assert.Equal(Dwight.Components.ComponentMatchCore.Nearest("Number", NumberLike), near);   // same as the bare ask
    }

    [Fact]
    public void Nearest_canonical_for_a_qualified_query_presents_canonical_forms_with_the_name_first()
    {
      var cands = new[]
      {
        (Name: "Partition Number", Cat: "Sets", Sub: "Tree"),
        (Name: "Number", Cat: "Params", Sub: "Primitive"),
        (Name: "Mirror Cut Numbers", Cat: "Sets", Sub: "Text"),
      };
      var near = Dwight.Components.ComponentMatchCore.NearestCanonical("Params > Input > Number", cands,
        c => c.Name, c => c.Cat, c => c.Sub);
      Assert.Equal("Number", near[0]);
    }
  }
}
