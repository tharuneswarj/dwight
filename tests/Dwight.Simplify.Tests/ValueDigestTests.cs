using System.Collections.Generic;
using System.Linq;
using Dwight.Simplify;
using Xunit;

namespace Dwight.Simplify.Tests
{
  /// <summary>
  /// Layer 2's value digest - the last thing standing between a wrong result and a deleted original.
  ///
  /// RR18 (second finding): it used to SAMPLE - the first 24 branches, the first 8 items each - and that is the
  /// FALSE-POSITIVE class, the one failure mode the whole verification stack exists to prevent. A script whose
  /// 100-item output differed at item 20 matched on its first 8 items and its count, digested EQUAL, passed
  /// verification, and the user's components were deleted in favour of a silently wrong script.
  ///
  /// A truncation MARKER was not a mitigation: two truncated digests with matching prefixes still compare equal.
  /// That IS the bug. (The old test asserting the marker is below, INVERTED - it encoded the defect.)
  /// The digest is now uncapped via a rolling hash: complete coverage, O(1) memory, fixed-size token.
  /// </summary>
  public class ValueDigestTests
  {
    private static (int, IReadOnlyList<string>) Br(int count, params string[] items) => (count, (IReadOnlyList<string>)items);

    private static List<string> Items(int n, int differAt = -1) =>
      Enumerable.Range(0, n).Select(i => i == differAt ? "CHANGED" : "item" + i).ToList();

    private static string Digest(params List<string>[] branches)
    {
      var bs = branches.Select(b => (b.Count, (IReadOnlyList<string>)b)).ToList();
      return ValueDigest.Compose(bs.Count, bs);
    }

    // ---- the RR18 false-positive hole -----------------------------------------------------------------

    [Fact]
    public void A_list_of_100_differing_at_item_20_DIFFERS_the_sampled_digest_PASSED_this()
    {
      // same count, same first 8 items. The old digest called these equal - and then deleted the originals.
      Assert.NotEqual(Digest(Items(100)), Digest(Items(100, differAt: 20)));
    }

    [Fact]
    public void A_difference_at_the_very_LAST_item_of_a_long_list_still_DIFFERS()
    {
      Assert.NotEqual(Digest(Items(1000)), Digest(Items(1000, differAt: 999)));
    }

    [Fact]
    public void Fifty_branches_differing_at_branch_30_DIFFER_past_the_old_branch_cap()
    {
      var before = Enumerable.Range(0, 50).Select(_ => Items(3)).ToArray();
      var after = Enumerable.Range(0, 50).Select(b => b == 30 ? Items(3, differAt: 1) : Items(3)).ToArray();

      Assert.NotEqual(Digest(before), Digest(after));
    }

    [Fact]
    public void The_digest_stays_a_FIXED_SIZE_token_however_much_data_it_covers()
    {
      // complete coverage must not mean an unbounded digest - that is what the rolling hash buys.
      var small = Digest(Items(2));
      var huge = Digest(Enumerable.Range(0, 200).Select(_ => Items(200)).ToArray());

      Assert.Equal(small.Length, huge.Length);
      Assert.DoesNotContain("+", huge);   // the old truncation marker is GONE, not merely relocated
    }

    // ---- the properties that had to survive the change ------------------------------------------------

    [Fact]
    public void Same_data_produces_the_same_digest()
    {
      // a same-inputs re-solve is deterministic, so an unchanged output MUST digest identically - otherwise every
      // apply reverts and the feature is dead in the other direction.
      var a = ValueDigest.Compose(2, new[] { Br(2, "1", "2"), Br(1, "3") });
      var b = ValueDigest.Compose(2, new[] { Br(2, "1", "2"), Br(1, "3") });
      Assert.Equal(a, b);
    }

    [Fact]
    public void A_changed_item_value_changes_the_digest()
    {
      Assert.NotEqual(ValueDigest.Compose(1, new[] { Br(2, "1", "2") }),
                      ValueDigest.Compose(1, new[] { Br(2, "1", "9") }));
    }

    [Fact]
    public void The_path_count_changes_the_digest()
    {
      Assert.NotEqual(ValueDigest.Compose(2, new[] { Br(1, "1") }),
                      ValueDigest.Compose(3, new[] { Br(1, "1") }));
    }

    [Fact]
    public void Item_order_within_a_branch_matters()
    {
      Assert.NotEqual(ValueDigest.Compose(1, new[] { Br(2, "1", "2") }),
                      ValueDigest.Compose(1, new[] { Br(2, "2", "1") }));
    }

    [Fact]
    public void An_item_count_change_DIFFERS_even_when_every_shared_item_matches()
    {
      Assert.NotEqual(Digest(Items(50)), Digest(Items(51)));
    }

    [Fact]
    public void The_branch_STRUCTURE_matters_not_just_the_flattened_items()
    {
      // {a,b} in one branch is not {a} and {b} in two - the separators are hashed, so the shape counts.
      Assert.NotEqual(Digest(new List<string> { "a", "b" }),
                      Digest(new List<string> { "a" }, new List<string> { "b" }));
    }
  }
}
