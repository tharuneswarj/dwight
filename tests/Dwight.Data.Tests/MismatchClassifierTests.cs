using System.Collections.Generic;
using Dwight.Data;
using Xunit;

namespace Dwight.Data.Tests
{
  /// <summary>
  /// Phase-8 WP-0 (#95): the eval's dual classifier, graduated into a GH-free core so it can render the PRODUCTION
  /// output-diff feedback too. It BUCKETS an already-failed comparison; it never decides pass/fail (that stays the
  /// bit-exact OutputEquivalence gate). The buckets are the RR20 #57 taxonomy:
  ///   STRUCTURE - different item count / branch count. A real failure.
  ///   ORDER     - the same items in a different sequence. A real failure (GH lists are positional), but the value
  ///               oracle's tractable class - the model understood the graph, enumerated wrong.
  ///   WRAPPER   - strict digest differs, the relaxed (shape) digest agrees -> our digest was over-strict (RR18).
  ///   VALUE     - same shape, genuinely different numbers.
  /// The facts it returns (item + branch counts) are VALUE-FREE by construction (#89): counts, never values,
  /// never positions, never the items themselves.
  /// </summary>
  public class MismatchClassifierTests
  {
    // strict per-item digests are opaque strings (the product hashes goo to these); loose are the relaxed shape
    // digests. The classifier only compares them - it never parses them - so plain tokens are a faithful stand-in.
    private static List<string> L(params string[] xs) => new List<string>(xs);

    [Fact]
    public void Different_item_count_is_structure()
    {
      var f = MismatchClassifier.Classify(1, L("a", "b", "c"), L("a", "b", "c"),
                                          1, L("a", "b"), L("a", "b"));
      Assert.Equal(MismatchKind.Structure, f.Kind);
    }

    [Fact]
    public void Different_branch_count_is_structure()
    {
      var f = MismatchClassifier.Classify(2, L("a", "b"), L("a", "b"),
                                          1, L("a", "b"), L("a", "b"));
      Assert.Equal(MismatchKind.Structure, f.Kind);
    }

    [Fact]
    public void Same_items_different_sequence_is_order()
    {
      // same multiset of strict digests, different order -> ORDER (checked before wrapper/value).
      var f = MismatchClassifier.Classify(1, L("a", "b", "c"), L("la", "lb", "lc"),
                                          1, L("c", "a", "b"), L("lc", "la", "lb"));
      Assert.Equal(MismatchKind.Order, f.Kind);
    }

    [Fact]
    public void Strict_differs_but_relaxed_agrees_is_wrapper()
    {
      // the RR18 class: an Addition's GH_Integer(150) vs a script's GH_Number(150.0) - same value, different bytes.
      var f = MismatchClassifier.Classify(1, L("int:150"), L("n:150"),
                                          1, L("num:150"), L("n:150"));
      Assert.Equal(MismatchKind.Wrapper, f.Kind);
    }

    [Fact]
    public void Same_shape_different_numbers_is_value()
    {
      // strict differs AND relaxed differs, not a permutation -> a genuinely different number (the #50 leak).
      var f = MismatchClassifier.Classify(1, L("n:47"), L("n:47"),
                                          1, L("n:12"), L("n:12"));
      Assert.Equal(MismatchKind.Value, f.Kind);
    }

    [Fact]
    public void Facts_carry_value_free_counts_only()
    {
      var f = MismatchClassifier.Classify(2, L("a", "b", "c"), L("a", "b", "c"),
                                          1, L("x", "y"), L("x", "y"));
      Assert.Equal(3, f.ItemsBefore);
      Assert.Equal(2, f.ItemsAfter);
      Assert.Equal(2, f.PathsBefore);
      Assert.Equal(1, f.PathsAfter);
    }

    [Fact]
    public void Order_is_decided_before_wrapper_when_both_could_apply()
    {
      // a pure reorder makes the relaxed lists ALSO permutations of each other, so wrapper would fire too - but
      // order is the more specific, more actionable class and must win.
      var f = MismatchClassifier.Classify(1, L("a", "b"), L("a", "b"),
                                          1, L("b", "a"), L("b", "a"));
      Assert.Equal(MismatchKind.Order, f.Kind);
    }

    [Fact]
    public void Empty_before_and_after_never_throws()
    {
      // a defensive edge: the classifier is only ever called on a KNOWN mismatch, but it must not throw on empties.
      var f = MismatchClassifier.Classify(0, L(), L(), 0, L(), L());
      Assert.Equal(0, f.ItemsBefore);
    }
  }
}
