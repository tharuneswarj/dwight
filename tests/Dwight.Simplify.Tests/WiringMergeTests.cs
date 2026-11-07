using System.Collections.Generic;
using Dwight.Simplify;
using Xunit;

namespace Dwight.Simplify.Tests
{
  /// <summary>
  /// In-place source substitution (RR4 review completion). The load-bearing cases: a relay replacement
  /// substitutes IN POSITION (not appended), and an input with kept sources on either side of a removed one keeps
  /// [kept0, replacement, kept1] order - so layer 1 does not false-STOP a correct multi-source apply.
  /// </summary>
  public class WiringMergeTests
  {
    private static List<int> Merge(int[] current, int[] removed, int[] declared)
    {
      var rem = new HashSet<int>(removed);
      return WiringMerge.InPlace(current, s => rem.Contains(s), declared);
    }

    [Fact]
    public void A_relay_source_is_substituted_in_position_not_appended()
    {
      Assert.Equal(new[] { 9 }, Merge(current: new[] { 1 }, removed: new[] { 1 }, declared: new[] { 9 }));
    }

    [Fact]
    public void Two_kept_sources_around_a_relay_keep_order_kept0_replacement_kept1()
    {
      Assert.Equal(new[] { 0, 9, 2 }, Merge(current: new[] { 0, 1, 2 }, removed: new[] { 1 }, declared: new[] { 9 }));
    }

    [Fact]
    public void A_retained_kept_source_survives_and_a_new_source_appends()
    {
      // an input the proposal does not restate keeps its kept source; a purely-additional declared source appends.
      Assert.Equal(new[] { 0, 9 }, Merge(current: new[] { 0 }, removed: new int[0], declared: new[] { 9 }));
    }

    [Fact]
    public void Multiple_removals_are_substituted_in_order()
    {
      Assert.Equal(new[] { 8, 1, 9 }, Merge(current: new[] { 0, 1, 2 }, removed: new[] { 0, 2 }, declared: new[] { 8, 9 }));
    }

    [Fact]
    public void A_removed_source_with_no_declared_replacement_is_dropped()
    {
      Assert.Equal(new[] { 0 }, Merge(current: new[] { 0, 1 }, removed: new[] { 1 }, declared: new int[0]));
    }
  }
}
