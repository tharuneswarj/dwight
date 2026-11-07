using System;
using System.Linq;
using Dwight.Agent;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// U18: THE SECOND ADDRESS SHAPE, AND THE PAIR OF TESTS THAT KEEPS IT SECOND.
  ///
  /// A spot is one small placement the user approved on its own card, belonging to no map. It needs an
  /// address for a reason that is not tidiness: four things key on the address string and every one fails
  /// SILENTLY without it (the ledger records nothing so it is never rebuildable, the attempt cap no-ops,
  /// no rebuild claim is resolved, and the tidy pass is skipped) - and, above all, BuildLedger.MayBuild
  /// refuses only what it can name, so the address IS the scope enforcement.
  ///
  /// It is NOT a plan address, and that is the whole design. #732 settled that a plan address's letter
  /// MEANS its layer in the flow. A placement belonging to no map has no layer, so minting it "A4" would
  /// assert a position in a flow that does not exist, and a model reading get_scaffold_map would see A4
  /// after A3 and reason about it as a downstream stage - a fabricated dependency, which is the class
  /// ADR-058 exists to stop.
  ///
  /// THE DISJOINTNESS IS ASSERTED BOTH WAYS, BY TEST, NEVER BY INSPECTION. That pair is what goes red if
  /// someone later "simplifies" the two parsers into one.
  /// </summary>
  public class SpotAddressTests
  {
    // ---- the two shapes cannot be mistaken for one another -------------------------------------------

    [Theory]
    [InlineData("Spot 1")]
    [InlineData("Spot 2")]
    [InlineData("Spot 17")]
    [InlineData("Spot 240")]
    public void A_spot_address_is_never_a_plan_address(string spot)
    {
      Assert.True(ScaffoldAddress.IsSpot(spot));
      Assert.False(ScaffoldAddress.IsAddress(spot));
      Assert.Equal(AddressKind.Spot, ScaffoldAddress.Kind(spot));
    }

    [Theory]
    [InlineData("A1")]
    [InlineData("B2")]
    [InlineData("Z9")]
    [InlineData("AA1")]
    public void A_plan_address_is_never_a_spot_address(string plan)
    {
      Assert.True(ScaffoldAddress.IsAddress(plan));
      Assert.False(ScaffoldAddress.IsSpot(plan));
      Assert.Equal(AddressKind.Plan, ScaffoldAddress.Kind(plan));
    }

    /// <summary>Both are "a region Dwight addresses" - the question most callers are really asking.</summary>
    [Fact]
    public void Both_kinds_are_region_addresses_and_nothing_else_is()
    {
      Assert.True(ScaffoldAddress.IsRegionAddress("A1"));
      Assert.True(ScaffoldAddress.IsRegionAddress("Spot 1"));

      foreach (var not in new[] { "", "   ", "a1", "spot 1", "Spot", "Spot 0", "Spot -1", "Spot 1x",
                                  "Spot1", "SPOT 1", "1", "A", "A1B", "Spot 1 - Panel" })
      {
        Assert.False(ScaffoldAddress.IsRegionAddress(not), not + " must not read as a region address");
        Assert.Equal(AddressKind.None, ScaffoldAddress.Kind(not));
      }
    }

    /// <summary>"Spot1" without the space would sit uncomfortably close to a plan address; it is refused,
    /// which is what keeps the two grammars from touching at all.</summary>
    [Fact]
    public void The_space_is_load_bearing()
    {
      Assert.False(ScaffoldAddress.IsSpot("Spot1"));
      Assert.False(ScaffoldAddress.IsAddress("Spot1"));
    }

    // ---- minting ------------------------------------------------------------------------------------

    [Fact]
    public void The_first_spot_on_a_bare_canvas_is_Spot_1() =>
      Assert.Equal("Spot 1", ScaffoldAddress.NextSpot(new string[0]));

    /// <summary>#720's law, and it is the same law: a new placement must never mint a name something else
    /// already answers to.</summary>
    [Fact]
    public void A_new_spot_continues_past_every_spot_in_use()
    {
      Assert.Equal("Spot 4", ScaffoldAddress.NextSpot(new[] { "Spot 1", "Spot 2", "Spot 3" }));
      Assert.Equal("Spot 4", ScaffoldAddress.NextSpot(new[] { "Spot 3", "Spot 1" }));   // gaps are not recycled
    }

    /// <summary>Plan addresses are not spots, so a canvas full of map regions does not push the first spot
    /// along - the two namespaces do not crowd each other, which is the point of their being two.</summary>
    [Fact]
    public void Plan_addresses_do_not_move_the_spot_counter()
    {
      Assert.Equal("Spot 1", ScaffoldAddress.NextSpot(new[] { "A1", "A2", "B1", "C9" }));
    }

    /// <summary>The in-use set is joined from group nicknames, so a stray string must not stop a placement
    /// (#375 - an instrument's own mess is never its subject's).</summary>
    [Fact]
    public void Unparseable_entries_do_not_stop_a_mint()
    {
      Assert.Equal("Spot 3", ScaffoldAddress.NextSpot(new[] { "Spot 2", "not an address", null, "", "spot 9" }));
    }

    // ---- ordering -----------------------------------------------------------------------------------

    /// <summary>
    /// Spots sort AFTER every map letter and NUMERICALLY among themselves.
    ///
    /// Left to the unparseable fallback they would all tie at int.MaxValue, and Spot 10 could read before
    /// Spot 2 - losing for the new shape the exact property #286 was raised to protect for the old one.
    /// </summary>
    [Fact]
    public void Spots_sort_after_the_map_and_in_number_order()
    {
      var mixed = new[] { "Spot 10", "B1", "Spot 2", "A10", "A2" };

      var ordered = mixed
        .OrderBy(a => ScaffoldAddress.SortKey(a).Letters, StringComparer.Ordinal)
        .ThenBy(a => ScaffoldAddress.SortKey(a).Number)
        .ToArray();

      Assert.Equal(new[] { "A2", "A10", "B1", "Spot 2", "Spot 10" }, ordered);
    }

    /// <summary>And something nobody can parse still sorts last of all - three classes, one fixed order.</summary>
    [Fact]
    public void An_unparseable_string_still_sorts_behind_a_spot()
    {
      var ordered = new[] { "mystery", "Spot 1", "A1" }
        .OrderBy(a => ScaffoldAddress.SortKey(a).Letters, StringComparer.Ordinal)
        .ThenBy(a => ScaffoldAddress.SortKey(a).Number)
        .ToArray();

      Assert.Equal(new[] { "A1", "Spot 1", "mystery" }, ordered);
    }

    // ---- the canvas side ----------------------------------------------------------------------------

    /// <summary>A spot's group carries its address the same way a map region's does (#221b), so a spot
    /// survives the loss of its record exactly as a map region does.</summary>
    [Fact]
    public void A_spot_group_name_reads_back_as_a_spot()
    {
      Assert.True(ScaffoldAddress.FromGroupName("Spot 1 - Number Panel", out var address, out var name));
      Assert.Equal("Spot 1", address);
      Assert.Equal("Number Panel", name);
      Assert.Equal(AddressKind.Spot, ScaffoldAddress.Kind(address));
    }

    [Fact]
    public void A_map_group_name_still_reads_back_as_a_plan_region()
    {
      Assert.True(ScaffoldAddress.FromGroupName("A1 - Inlet", out var address, out var name));
      Assert.Equal("A1", address);
      Assert.Equal("Inlet", name);
      Assert.Equal(AddressKind.Plan, ScaffoldAddress.Kind(address));
    }

    /// <summary>A group the user made is still nobody's region - the parse is exact or nothing (#208).</summary>
    [Fact]
    public void A_users_own_group_is_not_a_region()
    {
      Assert.False(ScaffoldAddress.FromGroupName("My working area", out _, out _));
      Assert.False(ScaffoldAddress.FromGroupName("Spot - something", out _, out _));
      Assert.False(ScaffoldAddress.FromGroupName("spot 1 - lower case", out _, out _));
    }
  }
}
