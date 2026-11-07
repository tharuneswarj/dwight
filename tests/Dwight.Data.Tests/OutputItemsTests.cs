using System.Collections.Generic;
using Dwight.Data;
using Xunit;

namespace Dwight.Data.Tests
{
  /// <summary>
  /// "HAS DATA" MEANS A NON-NULL ITEM. U20's trial counted items, so an output holding only nulls - what an errored
  /// script emits, and what a clean script that sets its output to None emits - counted as carrying data, and a
  /// "preserves" check of nothing against nothing read as verified. These pin the rule both trials now share.
  /// </summary>
  public class OutputItemsTests
  {
    [Fact]
    public void An_output_holding_only_nulls_carries_no_data()
    {
      Assert.False(OutputItems.HasNonNull(new object[] { null }));
      Assert.False(OutputItems.HasNonNull(new object[] { null, null, null }));
      Assert.Equal((3, 0), OutputItems.Count(new object[] { null, null, null }));
    }

    [Fact]
    public void One_non_null_item_is_data()
    {
      Assert.True(OutputItems.HasNonNull(new object[] { null, "x", null }));
      Assert.Equal((3, 1), OutputItems.Count(new object[] { null, "x", null }));
    }

    [Fact]
    public void Nothing_at_all_is_no_data()
    {
      Assert.False(OutputItems.HasNonNull(new object[0]));
      Assert.False(OutputItems.HasNonNull(null));
      Assert.Equal((0, 0), OutputItems.Count(null));
    }

    [Fact]
    public void A_typed_sequence_counts_the_same_way()
    {
      IEnumerable<string> goo = new List<string> { null, "a", "b" };
      Assert.Equal((3, 2), OutputItems.Count(goo));
    }

    // ---- U47: a wrapped nothing is not data ---------------------------------------------------------------------------

    private sealed class Wrapped { public object Value; }

    private static bool WrapsNothing(object o) => o is Wrapped w && w.Value == null;

    [Fact]
    public void A_wrapper_that_holds_nothing_is_not_data_when_the_caller_says_so()
    {
      // what Grasshopper holds for a script's `a = None`: an item that is not null, around a value that is
      var none = new object[] { new Wrapped { Value = null } };

      Assert.Equal((1, 1), OutputItems.Count(none));                    // the old reading: "one item of data"
      Assert.Equal((1, 0), OutputItems.Count(none, WrapsNothing));       // the rule: one item, none of it data
    }

    [Fact]
    public void A_wrapper_around_a_value_is_still_data_positive_control()
    {
      var items = new object[] { new Wrapped { Value = null }, new Wrapped { Value = 12.0 } };
      Assert.Equal((2, 1), OutputItems.Count(items, WrapsNothing));
    }

    [Fact]
    public void A_predicate_that_throws_never_hides_an_item()
    {
      Assert.Equal((1, 1), OutputItems.Count(new object[] { "x" }, o => throw new System.InvalidOperationException("boom")));
    }
  }
}
