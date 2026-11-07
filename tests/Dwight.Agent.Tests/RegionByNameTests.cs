using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Dwight.Agent;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// WHAT #178 LEFT. A component placed into a region by name is recorded at its spot, because the spot stays the ledger's key, so the region's own facts do not know it. A build into a
  /// region holding only such components was refused as occupied, saying Dwight had NO RECORD of components it had just placed. Now it is a rebuild that removes nothing and keeps them,
  /// and the card says so. Every one of them stays: nothing here adds to what a rebuild may remove.
  /// </summary>
  public class RegionByNameTests
  {
    private const int Cap = 3;
    private static readonly Guid Slider = new Guid("11111111-0000-0000-0000-000000000001");
    private static readonly Guid Panel = new Guid("11111111-0000-0000-0000-000000000002");
    private static readonly Guid Built = new Guid("22222222-0000-0000-0000-000000000001");
    private static readonly Guid Stranger = new Guid("33333333-0000-0000-0000-000000000001");

    private static Dictionary<Guid, string> AsLeft(params Guid[] guids) => guids.ToDictionary(g => g, g => "as-left:" + g.ToString("N"));

    private static BuildLedger LedgerWithNamedPlacement()
    {
      var ledger = new BuildLedger(new object());
      ledger.CommitBuild("Spot 1", AsLeft(Slider, Panel));
      return ledger;
    }

    // ---- the claim --------------------------------------------------------------------------------

    [Fact]
    public void A_region_holding_only_what_was_placed_by_name_is_a_rebuild_that_removes_nothing()
    {
      var claim = LedgerWithNamedPlacement().Resolve("A1", AsLeft(Slider, Panel), Cap);

      Assert.Equal(RebuildDecision.Replace, claim.Decision);
      Assert.Empty(claim.Remove);
      Assert.Empty(claim.Adopted);
      Assert.Equal(0, claim.Theirs);
      Assert.Equal(new[] { Slider, Panel }, claim.PlacedEarlier);
    }

    /// <summary>The removal is a function of the claim, so this is the whole safety property: a guid that is on the canvas, in the region and on Dwight's record is still not removable.</summary>
    [Fact]
    public void Nothing_placed_earlier_can_be_removed()
    {
      var claim = LedgerWithNamedPlacement().Resolve("A1", AsLeft(Slider, Panel), Cap);

      Assert.Empty(claim.RemovalPresentIn(_ => true));
    }

    [Fact]
    public void One_the_user_has_edited_since_is_still_kept_and_still_placed_earlier()
    {
      var now = AsLeft(Slider, Panel);
      now[Panel] = "user-changed";

      var claim = LedgerWithNamedPlacement().Resolve("A1", now, Cap);

      Assert.Equal(RebuildDecision.Replace, claim.Decision);
      Assert.Empty(claim.RemovalPresentIn(_ => true));
      Assert.Equal(new[] { Slider, Panel }, claim.PlacedEarlier);
    }

    /// <summary>The card and the build run one claim, so the card's preview must agree with the build.</summary>
    [Fact]
    public void The_cards_claim_is_the_same_one()
    {
      var claim = LedgerWithNamedPlacement().ResolveForNextTurn("A1", AsLeft(Slider, Panel));

      Assert.Equal(RebuildDecision.Replace, claim.Decision);
      Assert.Equal(new[] { Slider, Panel }, claim.PlacedEarlier);
    }

    [Fact]
    public void A_later_rebuild_removes_only_what_that_build_placed_and_keeps_the_named_ones()
    {
      var ledger = LedgerWithNamedPlacement();
      ledger.CommitBuild("A1", AsLeft(Built));

      var claim = ledger.Resolve("A1", AsLeft(Slider, Panel, Built), Cap);

      Assert.Equal(RebuildDecision.Replace, claim.Decision);
      Assert.Equal(new[] { Built }, claim.Remove);
      Assert.Equal(new[] { Slider, Panel }, claim.PlacedEarlier);
      Assert.Equal(0, claim.Theirs);   // the card used to say "leave the 2 it did not place"
    }

    [Fact]
    public void A_component_on_the_regions_own_record_counts_once_and_as_the_regions()
    {
      var ledger = LedgerWithNamedPlacement();
      ledger.CommitBuild("A1", AsLeft(Slider));

      var claim = ledger.Resolve("A1", AsLeft(Slider), Cap);

      Assert.Equal(new[] { Slider }, claim.Remove);
      Assert.Empty(claim.PlacedEarlier);
      Assert.Equal(0, claim.Theirs);
    }

    [Fact]
    public void A_component_recorded_at_two_spots_counts_once()
    {
      var ledger = LedgerWithNamedPlacement();
      ledger.CommitBuild("Spot 2", AsLeft(Slider));

      var claim = ledger.Resolve("A1", AsLeft(Slider, Stranger), Cap);

      Assert.Equal(new[] { Slider }, claim.PlacedEarlier);
      Assert.Equal(1, claim.Theirs);
    }

    [Fact]
    public void A_stranger_beside_them_still_refuses_because_it_is_a_strangers()
    {
      var claim = LedgerWithNamedPlacement().Resolve("A1", AsLeft(Slider, Panel, Stranger), Cap);

      Assert.Equal(RebuildDecision.Occupied, claim.Decision);
      Assert.Equal(1, claim.Theirs);
      Assert.Equal(new[] { Slider, Panel }, claim.PlacedEarlier);
    }

    [Fact]
    public void The_regions_own_edited_work_beside_them_still_refuses()
    {
      var ledger = LedgerWithNamedPlacement();
      ledger.CommitBuild("A1", AsLeft(Built));
      var now = AsLeft(Slider, Built);
      now[Built] = "user-changed";

      var claim = ledger.Resolve("A1", now, Cap);

      Assert.Equal(RebuildDecision.Occupied, claim.Decision);
      Assert.Equal(new[] { Built }, claim.Adopted);
      Assert.Equal(new[] { Slider }, claim.PlacedEarlier);
    }

    /// <summary>A region's work the user dragged into another region stays what it was before this: only a spot's facts count.</summary>
    [Fact]
    public void Another_regions_work_is_not_placed_earlier()
    {
      var ledger = new BuildLedger(new object());
      ledger.CommitBuild("B1", AsLeft(Built));

      var claim = ledger.Resolve("A1", AsLeft(Built), Cap);

      Assert.Equal(RebuildDecision.Occupied, claim.Decision);
      Assert.Empty(claim.PlacedEarlier);
      Assert.Equal(1, claim.Theirs);
    }

    [Fact]
    public void A_named_placement_undone_leaves_a_fresh_region()
    {
      var claim = LedgerWithNamedPlacement().Resolve("A1", new Dictionary<Guid, string>(), Cap);

      Assert.Equal(RebuildDecision.Fresh, claim.Decision);
      Assert.Empty(claim.PlacedEarlier);
    }

    [Fact]
    public void The_attempt_cap_still_comes_first()
    {
      var ledger = LedgerWithNamedPlacement();
      for (var i = 0; i < Cap; i++) ledger.CountAttempt("A1");

      Assert.Equal(RebuildDecision.Exhausted, ledger.Resolve("A1", AsLeft(Slider, Panel), Cap).Decision);
    }

    /// <summary>A loose spot has no boundary and no other spot's fact can be in it by walking its own record, so the placement's own path answers as it did.</summary>
    [Fact]
    public void A_spots_own_claim_finds_nothing_placed_earlier()
    {
      var ledger = LedgerWithNamedPlacement();
      ledger.CommitBuild("Spot 2", AsLeft(Built));

      var claim = ledger.ResolveWithoutBoundary("Spot 2", g => "as-left:" + g.ToString("N"), Cap);

      Assert.Equal(new[] { Built }, claim.Remove);
      Assert.Empty(claim.PlacedEarlier);
    }

    // ---- the card ---------------------------------------------------------------------------------

    private static RebuildComponent Named(string nick, string type) => new RebuildComponent { Nick = nick, Type = type };

    [Fact]
    public void The_card_says_what_stays_in_its_own_sentence()
    {
      var line = RebuildPreview.Sentence(new[]
      {
        new RegionRebuild { Address = "A1", PlacedEarlier = new[] { Named("Width", "Number Slider"), Named("Panel", "Panel") } },
      });

      Assert.Equal("A1 holds 2 components placed earlier; they stay: Width (Number Slider), Panel.", line);
    }

    [Fact]
    public void One_component_is_said_as_one()
    {
      var line = RebuildPreview.Sentence(new[]
      {
        new RegionRebuild { Address = "A1", PlacedEarlier = new[] { Named("Width", "Number Slider") } },
      });

      Assert.Equal("A1 holds 1 component placed earlier; it stays: Width (Number Slider).", line);
    }

    [Fact]
    public void Beside_a_removal_both_are_said_and_nothing_placed_earlier_is_called_a_strangers()
    {
      var line = RebuildPreview.Sentence(new[]
      {
        new RegionRebuild
        {
          Address = "A1",
          Remove = new[] { Named("Offset", "Offset Curve") },
          PlacedEarlier = new[] { Named("Width", "Number Slider") },
        },
      });

      Assert.Equal("In A1, Dwight will remove the 1 component it placed there - Offset (Offset Curve) - and build it again. " +
                   "A1 holds 1 component placed earlier; it stays: Width (Number Slider).", line);
      Assert.DoesNotContain("did not place", line);
    }

    [Fact]
    public void A_long_list_is_bounded_and_says_how_many_it_left_out()
    {
      var line = RebuildPreview.Sentence(new[]
      {
        new RegionRebuild { Address = "A1", PlacedEarlier = Enumerable.Range(1, 7).Select(i => Named("N" + i, "Panel")).ToList() },
      });

      Assert.StartsWith("A1 holds 7 components placed earlier; they stay: N1 (Panel)", line);
      Assert.EndsWith(", and 2 more.", line);
    }

    // ---- what the model is told ------------------------------------------------------------------

    private static KeptComponent Kept(int id, string nick, bool earlier) =>
      new KeptComponent { Id = id, Type = "Number Slider", Nick = nick, PlacedEarlier = earlier };

    private static string KeptFact(params KeptComponent[] kept) =>
      ApprovedTurnFact.KeptFact(new Dictionary<string, IReadOnlyList<KeptComponent>> { ["A1"] = kept });

    /// <summary>"The user has edited" would be false of a component nobody touched, and a model relays what it is told.</summary>
    [Fact]
    public void The_turns_fact_says_placed_earlier_and_never_edited()
    {
      var fact = KeptFact(Kept(41, "Width", true), Kept(42, "Height", true));

      Assert.Contains("These are components Dwight placed earlier, so the rebuild leaves them exactly as they are: wire from them by id, and do not place them again.", fact);
      Assert.DoesNotContain("edited", fact);
    }

    [Fact]
    public void The_turns_fact_for_one()
    {
      var fact = KeptFact(Kept(41, "Width", true));

      Assert.Contains("This is a component Dwight placed earlier, so the rebuild leaves it exactly as it is: wire from it by id, and do not place it again.", fact);
    }

    [Fact]
    public void The_turns_fact_for_a_mix_says_both()
    {
      Assert.Contains("These are components Dwight placed earlier, some of them edited by the user since, so the rebuild leaves them", KeptFact(Kept(41, "Width", true), Kept(42, "Height", false)));
    }

    [Fact]
    public void The_turns_fact_for_edited_ones_reads_as_it_did()
    {
      Assert.Contains("These are components the user has edited since Dwight placed them, so the rebuild leaves them exactly as they are", KeptFact(Kept(41, "Width", false), Kept(42, "Height", false)));
      Assert.Contains("This is a component the user has edited since Dwight placed it, so the rebuild leaves it exactly as it is", KeptFact(Kept(41, "Width", false)));
    }

    private static string Duplicate(params KeptComponent[] kept)
    {
      var spec = new BuildSpec { Region = "A1" };
      foreach (var k in kept) spec.Components.Add(new BuildComponent { Ref = "r" + k.Id, Type = k.Type, Nick = k.Nick });
      return RebuildDuplicates.Check(spec, kept, "A1").Message;
    }

    [Fact]
    public void A_duplicate_of_one_placed_earlier_is_refused_in_true_words()
    {
      var message = Duplicate(Kept(41, "Width", true));

      Assert.Contains("keeps Number Slider \"Width\" (41) - a component Dwight placed earlier, so it stays exactly as it is.", message);
      Assert.DoesNotContain("edited", message);
    }

    [Fact]
    public void A_duplicate_of_several_placed_earlier_is_said_as_several()
    {
      Assert.Contains("- components Dwight placed earlier, so they stay exactly as they are.", Duplicate(Kept(41, "Width", true), Kept(42, "Height", true)));
    }

    [Fact]
    public void A_duplicate_of_a_mix_says_both()
    {
      Assert.Contains("- components Dwight placed earlier, some of them edited since, so they stay", Duplicate(Kept(41, "Width", true), Kept(42, "Height", false)));
    }

    [Fact]
    public void A_duplicate_of_edited_ones_reads_as_it_did()
    {
      Assert.Contains("- a component Dwight placed that has been edited since, so it stays exactly as it is.", Duplicate(Kept(41, "Width", false)));
      Assert.Contains("- components Dwight placed that have been edited since, so they stay exactly as they are.", Duplicate(Kept(41, "Width", false), Kept(42, "Height", false)));
    }

    // ---- the refusal that remains, whose sentences had no test at all ------------------------------

    private static RebuildClaim Occupied(int adopted, int theirs, bool record, int earlier) =>
      RebuildClaim.Of(RebuildDecision.Occupied, null, Enumerable.Repeat(Built, adopted).ToList(), theirs, record, Enumerable.Repeat(Slider, earlier).ToList());

    [Fact]
    public void The_mixed_refusal_reads_as_it_did_with_nothing_placed_earlier()
    {
      Assert.Equal("A1 holds 1 component(s) Dwight placed but you have since edited, and 2 it has no record of placing. Dwight removes only its own work that you have left alone, " +
                   "so there is nothing here for it to clear. Nothing was changed. Delete what you want gone and ask again, or build somewhere else.",
        BuildLedger.OccupiedMessage("A1", Occupied(1, 2, true, 0)));
    }

    [Fact]
    public void The_adopted_refusal_reads_as_it_did_with_nothing_placed_earlier()
    {
      Assert.Equal("Dwight placed the 3 component(s) in A1, but you have edited them since - so they are yours now and Dwight will not remove them. Nothing was changed. " +
                   "Delete them yourself if you want the region rebuilt from scratch.",
        BuildLedger.OccupiedMessage("A1", Occupied(3, 0, true, 0)));
    }

    [Fact]
    public void The_no_record_refusal_reads_as_it_did_with_nothing_placed_earlier()
    {
      Assert.Equal("Dwight has NO RECORD of placing the 2 component(s) in A1, so it will not remove them. That may be because it did not place them - or because it did, in a session " +
                   "before Grasshopper was restarted: Dwight's record of what it built does not survive a restart. Nothing was changed. Clear the region yourself if you want it rebuilt from scratch.",
        BuildLedger.OccupiedMessage("A1", Occupied(0, 2, false, 0)));
    }

    [Fact]
    public void The_strangers_refusal_reads_as_it_did_with_nothing_placed_earlier()
    {
      Assert.Equal("A1 holds 2 component(s) Dwight did not place, so it will not remove them to make room. Nothing was changed. Clear the region yourself if you want it rebuilt, or build somewhere else.",
        BuildLedger.OccupiedMessage("A1", Occupied(0, 2, true, 0)));
    }

    [Theory]
    [InlineData(1, 2, true)]
    [InlineData(3, 0, true)]
    [InlineData(0, 2, false)]
    [InlineData(0, 2, true)]
    public void Every_refusal_counts_only_the_strangers_as_strangers_and_says_what_stays(int adopted, int theirs, bool record)
    {
      var message = BuildLedger.OccupiedMessage("A1", Occupied(adopted, theirs, record, 2));

      Assert.EndsWith(" A1 also holds 2 component(s) placed earlier, which stay.", message);
    }

    /// <summary>The false sentence this exists to end: what was placed earlier is not among what Dwight has no record of.</summary>
    [Theory]
    [InlineData(0, 1, false, "Dwight has NO RECORD of placing the 1 component(s) in A1,")]
    [InlineData(0, 1, true, "A1 holds 1 component(s) Dwight did not place,")]
    [InlineData(1, 1, true, "A1 holds 1 component(s) Dwight placed but you have since edited, and 1 it has no record of placing.")]
    public void What_was_placed_earlier_is_not_counted_among_the_strangers(int adopted, int theirs, bool record, string start)
    {
      Assert.StartsWith(start, BuildLedger.OccupiedMessage("A1", Occupied(adopted, theirs, record, 2)));
    }

    [Theory]
    [InlineData(1, 0, true, 0, "every component Dwight placed here has been edited since, so none of it is Dwight's to remove")]
    [InlineData(1, 0, true, 2, "every component Dwight placed here has been edited since, so none of it is Dwight's to remove")]
    [InlineData(0, 1, true, 2, "what Dwight placed here earlier stays, and the rest of this region is not Dwight's to remove")]
    [InlineData(0, 1, false, 2, "what Dwight placed here earlier stays, and the rest of this region is not Dwight's to remove")]
    [InlineData(0, 1, true, 0, "nothing in this region was placed by Dwight")]
    [InlineData(0, 1, false, 0, "Dwight has no record for this region - it may never have built here, or its record did not survive a Grasshopper restart")]
    public void The_cause_for_a_model_makes_the_same_distinction(int adopted, int theirs, bool record, int earlier, string cause)
    {
      Assert.Equal(cause, BuildLedger.OccupiedCause(Occupied(adopted, theirs, record, earlier)));
    }

    // ---- the shim, by its text: it is Grasshopper-bound, and the Rhino row is what proves it ---------

    private static string BuildRegionText()
    {
      var dir = new DirectoryInfo(AppContext.BaseDirectory);
      while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Dwight.sln"))) dir = dir.Parent;
      Assert.True(dir != null);
      return File.ReadAllText(Path.Combine(dir.FullName, "Dwight", "Agent", "GhBuildRegion.cs"));
    }

    [Theory]
    [InlineData("BuildLedger.OccupiedMessage(spec.Region, claim),")]
    [InlineData("[\"cause\"] = BuildLedger.OccupiedCause(claim),")]
    [InlineData("[\"placedEarlier\"] = claim.PlacedEarlier.Count,")]
    [InlineData("var duplicate = RebuildDuplicates.Check(spec, KeptOf(doc, ids, claim), spec.Region);")]          // the build
    [InlineData("var found = KeptOf(doc, ids, claim);")]                                                         // the turn's fact
    [InlineData("claim.PlacedEarlier.Select(g => (g, true))")]                                                   // both read what was placed earlier
    [InlineData("PlacedEarlier = earlier });")]
    [InlineData("PlacedEarlier = claim.PlacedEarlier.Select(g => Describe(doc, g)).Where(c => c != null).ToList(),")]   // the card
    [InlineData("claim.Adopted.Count == 0 && claim.Theirs == 0 && claim.PlacedEarlier.Count == 0;")]             // the re-space still leaves such a region where it is
    public void The_build_the_card_and_the_turns_fact_read_one_claim(string line)
    {
      Assert.Contains(line, BuildRegionText());
    }
  }
}
