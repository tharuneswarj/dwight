using System.Collections.Generic;
using Dwight.Simplify;
using Xunit;

namespace Dwight.Simplify.Tests
{
  /// <summary>
  /// Layer 2's multiset compare (ruling G.2). The selection's output data before vs after is the same BAG of
  /// (tree signature + value digest) samples, order-independent - no old-&gt;new mapping. A reorder is still
  /// equivalent; a changed value (the sabotage) or a merged/dropped output makes the bags differ, so the apply
  /// reverts and reports.
  /// </summary>
  public class OutputEquivalenceTests
  {
    private static OutputSample S(string tree, string value) => new OutputSample(tree, value);

    [Fact]
    public void The_same_bag_in_a_different_order_is_equivalent()
    {
      var before = new List<OutputSample> { S("2:3,3", "vA"), S("1:5", "vB") };
      var after = new List<OutputSample> { S("1:5", "vB"), S("2:3,3", "vA") };   // reordered
      Assert.True(OutputEquivalence.Equivalent(before, after));
    }

    [Fact]
    public void A_changed_output_value_is_not_equivalent()
    {
      // The sabotage layer 2 must catch: same structure, one output computes a different value.
      var before = new List<OutputSample> { S("1:3", "1,2,3") };
      var after = new List<OutputSample> { S("1:3", "1,2,9") };
      Assert.False(OutputEquivalence.Equivalent(before, after));
    }

    [Fact]
    public void Merging_two_outputs_into_one_is_not_equivalent()
    {
      // A legitimate merge of two outputs into one changes the bag size - the correct conservative outcome is
      // "different, revert and say why", not a silent accept.
      var before = new List<OutputSample> { S("1:3", "vA"), S("1:2", "vB") };
      var after = new List<OutputSample> { S("1:5", "vAB") };
      Assert.False(OutputEquivalence.Equivalent(before, after));
    }

    [Fact]
    public void A_duplicated_value_is_a_multiset_not_a_set()
    {
      // Two outputs carrying identical data must both be present after - a set would collapse them, a multiset
      // does not, so dropping one of a duplicated pair is caught.
      var before = new List<OutputSample> { S("1:1", "v"), S("1:1", "v") };
      var after = new List<OutputSample> { S("1:1", "v") };
      Assert.False(OutputEquivalence.Equivalent(before, after));
    }
  }
}
