using Dwight.Agent;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// #573: AFTER THE MAP WAS UNDONE, "WHAT'S ON THE MAP" WAS ANSWERED FROM MEMORY.
  ///
  /// "Same as before - the map hasn't changed. All six regions exist on the canvas." Then, challenged:
  /// "I answered from the plan I remembered proposing, without re-checking the canvas each time." F4 had
  /// already made the map payload unable to list an undone region as present - and it did not matter,
  /// because the model never called the tool. It assumed the canvas cannot change between two of its own
  /// turns, and the prompt line telling it to read before answering did not bind.
  ///
  /// So the plugin states the change as a fact at the start of the next turn, with the region delta - a
  /// fact the model cannot misremember, where "the canvas changed" would still be an instruction.
  /// </summary>
  public class CanvasChangeFactTests
  {
    private static CanvasReading At(string stamp, params string[] regions) =>
      new CanvasReading { Stamp = stamp, Regions = regions };

    // ---- the gate case -------------------------------------------------------------------------------

    /// <summary>THE EXACT MISS: six regions at the end of the last turn, none now. The sentence names both
    /// sides, so "all six regions exist" is no longer an answer the model can give from memory.</summary>
    [Fact]
    public void An_undone_map_is_stated_with_what_was_there_and_what_is_there_now()
    {
      var fact = CanvasChangeFact.For(
        At("with-map", "A1", "A2", "A3", "A4", "A5", "A6"), At("without-map"), true);

      Assert.Contains("HAS CHANGED", fact);
      Assert.Contains("at the end of your last turn: A1, A2, A3, A4, A5, A6", fact);
      Assert.Contains("Now: none", fact);
    }

    // ---- silence where silence is right --------------------------------------------------------------

    /// <summary>A line that appears every turn trains the reader to skip it, and the day it matters it is
    /// skipped. An unchanged canvas says nothing.</summary>
    [Fact]
    public void An_unchanged_canvas_says_nothing()
    {
      Assert.Null(CanvasChangeFact.For(At("same", "A1"), At("same", "A1"), true));
    }

    /// <summary>A first turn has nothing it could be stale about.</summary>
    [Fact]
    public void A_first_turn_says_nothing_even_without_a_record()
    {
      Assert.Null(CanvasChangeFact.For(null, At("now"), false));
    }

    // ---- changed, but the regions did not ----------------------------------------------------------

    /// <summary>A change that left the scaffold alone - a moved component, a rewire - still gets the fact,
    /// without a region line that would imply the map was involved.</summary>
    [Fact]
    public void A_change_that_did_not_touch_the_regions_is_stated_without_a_region_line()
    {
      var fact = CanvasChangeFact.For(At("before", "A1", "B1"), At("after", "B1", "A1"), true);

      Assert.Contains("HAS CHANGED", fact);
      Assert.DoesNotContain("Scaffold regions", fact);   // same set, different order is not a change
    }

    // ---- not known --------------------------------------------------------------------------------

    /// <summary>
    /// After a restart the conversation has earlier turns but Dwight holds no reading from them. Saying
    /// nothing would let the model assume the canvas is as it last described it; saying "it changed"
    /// would be a guess. It says what Dwight KNOWS.
    /// </summary>
    [Fact]
    public void After_a_restart_it_says_it_cannot_tell_rather_than_nothing_or_a_guess()
    {
      var fact = CanvasChangeFact.For(null, At("now", "A1"), true);

      Assert.Contains("cannot tell whether the canvas has changed", fact);
      Assert.Contains("does not survive a restart", fact);
      Assert.DoesNotContain("HAS CHANGED", fact);
    }

    /// <summary>An unreadable stamp on either side is NOT "unchanged" - two unread readings would compare
    /// equal and report a stable canvas that was never measured.</summary>
    [Theory]
    [InlineData(null, "now")]
    [InlineData("then", null)]
    [InlineData("", "")]
    public void An_unreadable_stamp_is_not_known_never_unchanged(string then, string now)
    {
      var fact = CanvasChangeFact.For(At(then), At(now), true);

      Assert.NotNull(fact);
      Assert.Contains("cannot tell", fact);
    }

    // ---- what it must never claim ---------------------------------------------------------------------

    /// <summary>Dwight knows THAT the canvas changed, not WHY. The user, an undo and an approved Reorganise
    /// are the same fact from here, and naming a cause would be inventing one.</summary>
    [Fact]
    public void The_fact_never_says_who_changed_the_canvas()
    {
      var fact = CanvasChangeFact.For(At("a", "A1"), At("b"), true).ToLowerInvariant();

      Assert.DoesNotContain("user", fact);
      Assert.DoesNotContain("you undid", fact);
      Assert.DoesNotContain("undo", fact);
    }

    // ---- bounded and ordered ------------------------------------------------------------------------

    [Fact]
    public void Regions_are_listed_in_reading_order_regardless_of_input_order()
    {
      var fact = CanvasChangeFact.For(At("a", "B1", "A10", "A2"), At("b"), true);

      Assert.Contains("A2, A10, B1", fact);   // numeric, not lexicographic: A2 before A10
    }

    [Fact]
    public void A_long_region_list_is_bounded_and_says_how_many_it_left_out()
    {
      var many = new string[20];
      for (var i = 0; i < 20; i++) many[i] = "A" + (i + 1);

      var fact = CanvasChangeFact.For(At("a", many), At("b"), true);

      Assert.Contains("and 8 more", fact);
      Assert.DoesNotContain("A20", fact);
    }

    // ---- #660: per-region component counts --------------------------------------------------------------

    private static CanvasReading Counted(string stamp, params (string Address, int Count)[] regions) =>
      new CanvasReading
      {
        Stamp = stamp,
        Regions = System.Linq.Enumerable.ToList(System.Linq.Enumerable.Select(regions, r => r.Address)),
        Counts = CanvasReading.CountsByAddress(
          System.Linq.Enumerable.Select(regions, r => new System.Collections.Generic.KeyValuePair<string, int>(r.Address, r.Count))),
      };

    /// <summary>
    /// THE GAP (#660). A change that empties or fills a region without adding or removing its group leaves the
    /// region SET unchanged, so the fact said "the canvas HAS CHANGED" with nothing about where - and a region that
    /// still exists reads as a region still built. The per-region count is the missing fact.
    /// </summary>
    [Fact]
    public void A_region_whose_component_count_changed_is_stated_with_both_counts()
    {
      var fact = CanvasChangeFact.For(Counted("built", ("A1", 8), ("B1", 3)), Counted("undone", ("A1", 0), ("B1", 3)), true);

      Assert.Contains("HAS CHANGED", fact);
      Assert.Contains(" Components per region: A1: 8 at the end of your last turn, now 0.", fact);
      Assert.DoesNotContain("B1:", fact);                 // unchanged counts are not listed
      Assert.DoesNotContain("Scaffold regions", fact);    // the region SET did not change
    }

    [Fact]
    public void Several_changed_counts_are_listed_in_reading_order()
    {
      var fact = CanvasChangeFact.For(
        Counted("a", ("B1", 1), ("A10", 2), ("A2", 3)),
        Counted("b", ("B1", 4), ("A10", 5), ("A2", 6)), true);

      Assert.Contains("Components per region: A2: 3 at the end of your last turn, now 6; " +
                      "A10: 2 at the end of your last turn, now 5; B1: 1 at the end of your last turn, now 4.", fact);
    }

    [Fact]
    public void A_change_that_left_every_count_alone_has_no_counts_line()
    {
      var fact = CanvasChangeFact.For(Counted("before", ("A1", 8)), Counted("after", ("A1", 8)), true);

      Assert.Contains("HAS CHANGED", fact);
      Assert.DoesNotContain("Components per region", fact);
    }

    /// <summary>An added or removed region is already in the region line. The counts line compares only regions
    /// present on BOTH sides - a missing region is not a region with zero components.</summary>
    [Fact]
    public void Added_and_removed_regions_stay_in_the_region_line_not_the_counts_line()
    {
      var fact = CanvasChangeFact.For(Counted("a", ("A1", 8), ("B1", 2)), Counted("b", ("B1", 5), ("C1", 4)), true);

      Assert.Contains("Scaffold regions at the end of your last turn: A1, B1. Now: B1, C1.", fact);
      Assert.Contains("Components per region: B1: 2 at the end of your last turn, now 5.", fact);
      Assert.DoesNotContain("A1:", fact);
      Assert.DoesNotContain("C1:", fact);
    }

    [Fact]
    public void A_long_counts_list_is_bounded_and_says_how_many_it_left_out()
    {
      var before = new (string, int)[15];
      var after = new (string, int)[15];
      for (var i = 0; i < 15; i++) { before[i] = ("A" + (i + 1), 1); after[i] = ("A" + (i + 1), 2); }

      var fact = CanvasChangeFact.For(Counted("a", before), Counted("b", after), true);

      Assert.Contains("A12: 1 at the end of your last turn, now 2; and 3 more regions whose count changed.", fact);
      Assert.DoesNotContain("A13:", fact);
    }

    /// <summary>A reading without counts (not read, or an older record) says nothing about counts rather than
    /// comparing against a zero it never measured.</summary>
    [Fact]
    public void Without_counts_on_either_side_there_is_no_counts_line()
    {
      var withCounts = Counted("a", ("A1", 8));
      var without = At("b", "A1");

      Assert.DoesNotContain("Components per region", CanvasChangeFact.For(withCounts, without, true));
      Assert.DoesNotContain("Components per region", CanvasChangeFact.For(without, Counted("c", ("A1", 0)), true));
    }

    /// <summary>The stamp is the authority on "changed". Counts never speak on a canvas the stamp says is the same.</summary>
    [Fact]
    public void Counts_never_speak_when_the_stamp_is_unchanged()
    {
      Assert.Null(CanvasChangeFact.For(Counted("same", ("A1", 8)), Counted("same", ("A1", 0)), true));
    }

    /// <summary>Two groups carrying one address are not one region, so that address has NO count - never a sum
    /// and never whichever group was read last. Identity decides what a count is of (#208).</summary>
    [Fact]
    public void An_address_held_by_two_groups_has_no_count()
    {
      var counts = CanvasReading.CountsByAddress(new[]
      {
        new System.Collections.Generic.KeyValuePair<string, int>("A1", 3),
        new System.Collections.Generic.KeyValuePair<string, int>("A1", 5),
        new System.Collections.Generic.KeyValuePair<string, int>("B1", 2),
      });

      Assert.False(counts.ContainsKey("A1"));
      Assert.Equal(2, counts["B1"]);
    }

    /// <summary>A count going down is not a deletion and a count going up is not a build - Dwight knows the numbers,
    /// not who or what moved them.</summary>
    [Fact]
    public void The_counts_line_never_names_a_cause()
    {
      var fact = CanvasChangeFact.For(Counted("a", ("A1", 8), ("B1", 0)), Counted("b", ("A1", 0), ("B1", 4)), true)
        .ToLowerInvariant();

      foreach (var word in new[] { "user", "undo", "deleted", "removed", "built", "rebuilt" })
        Assert.DoesNotContain(word, fact);
    }
  }
}
