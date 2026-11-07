using System;
using System.Collections.Generic;
using System.Linq;
using Dwight.Agent;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// U18's SCOPE GATE - the half that decides what a spot card may actually do.
  ///
  /// THE DEFECT THIS UNIT OPENS, AND CLOSES. `build_region` has always allowed a build with no region
  /// (#354 Q3), and `BuildLedger.MayBuild` returned TRUE for an empty address with the comment
  /// "un-addressed: the card is the scope". THAT COMMENT WAS ASPIRATIONAL - no code made the card the
  /// scope. It was safe only because three gates stopped the chat ever reaching an un-addressed build,
  /// and U18 opens exactly those gates. A card saying "place a Panel" would have admitted forty
  /// components with every guard passing: #338 with the lock left off.
  ///
  /// THE FIX IS THAT A SPOT HAS A REAL ADDRESS. The card approves one minted address, the turn is
  /// authorised for it, and MayBuild refuses everything else by the path that already refuses everything
  /// else. THE ADDRESS IS THE ENFORCEMENT, NOT THE BOOKKEEPING.
  /// </summary>
  public class SpotScopeTests
  {
    // ---- the bound ----------------------------------------------------------------------------------

    private static List<string> Components(int n) =>
      Enumerable.Range(1, n).Select(i => "a Panel " + i).ToList();

    [Fact]
    public void A_placement_small_enough_to_name_is_described()
    {
      Assert.True(SpotPlacement.TryDescribe(Components(SpotPlacement.MaxComponents), "Spot 1", "Curve",
        out var placement, out var error));

      Assert.Null(error);
      Assert.Equal(SpotPlacement.MaxComponents, placement.Components.Count);
      Assert.Equal("Spot 1", placement.Address);
    }

    /// <summary>
    /// Past the bound it REFUSES, and names the door: work too large for one card is work that wants a
    /// map. A refusal is only honest when there was nothing else to do (#613/#626), and here there is.
    ///
    /// This is also what keeps Scaffold's reason to exist: U18 removes the friction of needing a map for
    /// ONE panel; it must not become a way to build a whole definition without one.
    /// </summary>
    [Fact]
    public void A_placement_too_large_to_name_is_refused_toward_propose_scaffold()
    {
      Assert.False(SpotPlacement.TryDescribe(Components(SpotPlacement.MaxComponents + 1), "Spot 1", null,
        out var placement, out var error));

      Assert.Null(placement);
      Assert.Equal(ToolErrors.PreconditionRefused, error.Code);
      Assert.Contains("propose_scaffold", error.Message);
      Assert.Equal(SpotPlacement.MaxComponents + 1, error.Detail["asked"].GetValue<int>());
      Assert.Equal(SpotPlacement.MaxComponents, error.Detail["mostThatCanBeNamed"].GetValue<int>());
    }

    /// <summary>The bound is read from its constant, so if it ever moves this test moves with it rather than
    /// pinning a number the card no longer honours. SpotCapTests pins the number itself.</summary>
    [Fact]
    public void The_bound_is_the_constant()
    {
      var atLimit = SpotPlacement.TryDescribe(Components(SpotPlacement.MaxComponents), "Spot 1", null, out _, out _);
      var overLimit = SpotPlacement.TryDescribe(Components(SpotPlacement.MaxComponents + 1), "Spot 1", null, out _, out _);

      Assert.True(atLimit);
      Assert.False(overLimit);
    }

    [Fact]
    public void A_placement_of_nothing_is_refused()
    {
      Assert.False(SpotPlacement.TryDescribe(new List<string>(), "Spot 1", null, out _, out var error));
      Assert.Equal(ToolErrors.InvalidArgs, error.Code);
    }

    // ---- the scope resolver's spot case -------------------------------------------------------------

    /// <summary>A spot is approved BEFORE its group exists - the build creates it - so it cannot be
    /// resolved against live canvas regions the way a map region is. It arrives through its own
    /// parameter, which is what keeps "approve something not on the canvas" inexpressible for a plan
    /// region.</summary>
    [Fact]
    public void A_minted_spot_resolves_even_though_it_is_not_on_the_canvas()
    {
      var resolution = BuildScope.Resolve(new string[0], new[] { "A1", "A2" }, mintedSpot: "Spot 1");

      Assert.Equal(BuildScopeOutcome.Approved, resolution.Outcome);
      Assert.Equal(new[] { "Spot 1" }, resolution.Regions.ToArray());
    }

    /// <summary>A spot that ALREADY exists is a collision, not a rebuild opportunity: the mint continues
    /// past every spot in use, so one that exists means the canvas moved under the card.</summary>
    [Fact]
    public void A_spot_already_on_the_canvas_is_refused_by_name()
    {
      var resolution = BuildScope.Resolve(new string[0], new[] { "Spot 1", "A1" }, mintedSpot: "Spot 1");

      Assert.Equal(BuildScopeOutcome.Refuse, resolution.Outcome);
      Assert.Contains("Spot 1 is already on the canvas", resolution.Message);
    }

    /// <summary>One card, one kind of scope. A card approving regions AND a new spot would be a contract
    /// about two different things, and #338 is that the sentence the user read IS the scope.</summary>
    [Fact]
    public void A_card_cannot_approve_both_regions_and_a_spot()
    {
      var resolution = BuildScope.Resolve(new[] { "A1" }, new[] { "A1" }, mintedSpot: "Spot 1");

      Assert.Equal(BuildScopeOutcome.Refuse, resolution.Outcome);
      Assert.Contains("two different kinds of scope", resolution.Message);
    }

    [Fact]
    public void Something_that_is_not_a_spot_address_cannot_enter_through_the_spot_door()
    {
      foreach (var notASpot in new[] { "A1", "spot 1", "Spot", "Spot 0", "anything" })
      {
        var resolution = BuildScope.Resolve(new string[0], new[] { "A1" }, mintedSpot: notASpot);

        Assert.Equal(BuildScopeOutcome.Refuse, resolution.Outcome);
        Assert.Contains("is not a spot address", resolution.Message);
      }
    }

    /// <summary>The ordinary path is untouched: with no minted spot, every existing rule still holds.</summary>
    [Fact]
    public void Without_a_minted_spot_the_existing_rules_are_unchanged()
    {
      Assert.Equal(BuildScopeOutcome.Refuse, BuildScope.Resolve(new string[0], new[] { "A1" }).Outcome);
      Assert.Equal(BuildScopeOutcome.Approved, BuildScope.Resolve(new[] { "A1" }, new[] { "A1" }).Outcome);
      Assert.Equal(BuildScopeOutcome.PartlyMissing,
        BuildScope.Resolve(new[] { "A1", "A2" }, new[] { "A1" }).Outcome);
    }

    // ---- THE TEST THAT CATCHES A SCOPE VIOLATION ----------------------------------------------------

    /// <summary>
    /// THE ONE #338 WOULD HAVE NEEDED. An approved spot card authorises exactly one address; a build
    /// aimed anywhere else is refused before the canvas is touched, however convincingly it was asked
    /// for. Its value is that it goes red if anyone later "simplifies" the spot address away and returns
    /// to an un-addressed build - which is precisely what U18 had to close.
    /// </summary>
    [Fact]
    public void An_approved_spot_authorises_that_address_and_nothing_else()
    {
      var ledger = new BuildLedger(new object());
      ledger.AuthoriseSpot("Spot 1", grouped: true);

      Assert.True(ledger.MayBuild("Spot 1"));
      Assert.False(ledger.MayBuild("Spot 2"));
      Assert.False(ledger.MayBuild("A1"));
      Assert.False(ledger.MayBuild(""));       // the hole U18 closed: no address is not a free pass
      Assert.False(ledger.MayBuild(null));
    }

    /// <summary>Granted once, like every other authority: a second grant would be a turn widening its own
    /// scope, which is the one thing the whole gate exists to prevent.</summary>
    [Fact]
    public void A_turn_cannot_grant_itself_a_second_spot()
    {
      var ledger = new BuildLedger(new object());
      ledger.AuthoriseSpot("Spot 1", grouped: true);
      ledger.AuthoriseSpot("Spot 2", grouped: false);

      Assert.True(ledger.MayBuild("Spot 1"));
      Assert.False(ledger.MayBuild("Spot 2"));
      Assert.Equal("Spot 1", ledger.ApprovedSpot);
      Assert.True(ledger.SpotIsGrouped("Spot 1"));
    }

    /// <summary>And a spot grant cannot be widened into a region grant either.</summary>
    [Fact]
    public void A_spot_grant_does_not_admit_regions_afterwards()
    {
      var ledger = new BuildLedger(new object());
      ledger.AuthoriseSpot("Spot 1", grouped: false);
      ledger.Authorise(new[] { "A1", "A2" });

      Assert.False(ledger.MayBuild("A1"));
      Assert.True(ledger.MayBuild("Spot 1"));
    }

    // ---- the grouping choice is the user's, and the model cannot reach it ---------------------------

    /// <summary>
    /// THE #346 GUARD. The mode pre-select was retired because the model was asked to nominate a value it
    /// never nominated, leaving a chooser nobody answered. Here the grouping decision lives on the TURN'S
    /// PERMISSION, set by the click, and there is no argument through which the model could name it - a
    /// `grouped` field on the build spec would have rebuilt #346 exactly.
    /// </summary>
    [Fact]
    public void The_grouping_choice_lives_on_the_turn_and_answers_only_for_that_spot()
    {
      var grouped = new BuildLedger(new object());
      grouped.AuthoriseSpot("Spot 1", grouped: true);
      var loose = new BuildLedger(new object());
      loose.AuthoriseSpot("Spot 1", grouped: false);

      Assert.True(grouped.SpotIsGrouped("Spot 1"));
      Assert.False(loose.SpotIsGrouped("Spot 1"));

      // It answers for THIS spot only - never for a region, and never for another spot.
      Assert.False(grouped.SpotIsGrouped("A1"));
      Assert.False(grouped.SpotIsGrouped("Spot 2"));
      Assert.False(grouped.SpotIsGrouped(null));
      Assert.True(grouped.IsApprovedSpot("Spot 1"));
      Assert.False(grouped.IsApprovedSpot("A1"));
    }

    // ---- THE RECORD IS MINTED EITHER WAY (the guarantee a refactor would quietly drop) --------------

    private static readonly Guid One = new Guid("18181818-0000-0000-0000-000000000001");
    private static readonly Guid Two = new Guid("18181818-0000-0000-0000-000000000002");

    private static Dictionary<Guid, string> AsLeft(params Guid[] guids) =>
      guids.ToDictionary(g => g, g => "as-left:" + g.ToString("N"));

    /// <summary>
    /// THE WHOLE REASON #770's AMENDMENT WAS CHEAP: THE RECORD IS THE ENFORCEMENT, THE GROUP IS A VIEW
    /// OF IT. The ledger entry, the attempt cap and the scope all key on the minted spot, so they are
    /// identical whether or not a box was drawn.
    ///
    /// This is the test that goes red if a later refactor ties the record to the group - which is the
    /// tempting simplification, because with a group present the boundary path is the only one you see.
    /// The grouped ledger resolves through its BOX; the loose one resolves through its RECORD; both must
    /// reach the same claim over the same components.
    /// </summary>
    [Fact]
    public void A_loose_placement_is_recorded_and_rebuildable_exactly_as_a_grouped_one_is()
    {
      var tokens = AsLeft(One, Two);

      var grouped = new BuildLedger(new object());
      grouped.AuthoriseSpot("Spot 1", grouped: true);
      grouped.CommitBuild("Spot 1", tokens);

      var loose = new BuildLedger(new object());
      loose.AuthoriseSpot("Spot 1", grouped: false);
      loose.CommitBuild("Spot 1", tokens);

      // The grouped one is handed what its BOX holds. The loose one has no box, so the ledger walks its
      // own facts and asks only what each component's token is now.
      var byBox = grouped.Resolve("Spot 1", tokens, maxAttempts: 3);
      var byRecord = loose.ResolveWithoutBoundary("Spot 1",
        g => tokens.TryGetValue(g, out var t) ? t : null, maxAttempts: 3);

      Assert.Equal(RebuildDecision.Replace, byBox.Decision);
      Assert.Equal(RebuildDecision.Replace, byRecord.Decision);
      Assert.Equal(byBox.Remove.OrderBy(g => g).ToArray(), byRecord.Remove.OrderBy(g => g).ToArray());
    }

    /// <summary>A loose placement is still SCOPED and still attempt-capped - the two properties that have
    /// nothing to do with whether a box exists, and everything to do with the address.</summary>
    [Fact]
    public void A_loose_placement_is_still_scoped_and_still_attempt_capped()
    {
      var loose = new BuildLedger(new object());
      loose.AuthoriseSpot("Spot 1", grouped: false);

      Assert.True(loose.MayBuild("Spot 1"));
      Assert.False(loose.MayBuild("Spot 2"));

      for (var i = 0; i < 3; i++) loose.CountAttempt("Spot 1");
      Assert.Equal(RebuildDecision.Exhausted,
        loose.ResolveWithoutBoundary("Spot 1", _ => null, maxAttempts: 3).Decision);
    }

    /// <summary>
    /// A LOOSE PLACEMENT'S "theirs" IS ZERO BY CONSTRUCTION, and that is the honest consequence of having
    /// no box rather than a weakened claim: a grouped region counts everything inside it that is not
    /// Dwight's and leaves it alone, and a placement with no boundary has no inside for a stranger's
    /// component to be in. It is why the loose card sentence says Dwight will only ever remove what it
    /// placed there itself.
    /// </summary>
    [Fact]
    public void A_loose_placement_never_claims_anything_it_did_not_place()
    {
      var loose = new BuildLedger(new object());
      loose.AuthoriseSpot("Spot 1", grouped: false);
      loose.CommitBuild("Spot 1", AsLeft(One));

      var stranger = new Guid("99999999-0000-0000-0000-000000000009");
      var claim = loose.ResolveWithoutBoundary("Spot 1",
        g => g == One ? "as-left:" + One.ToString("N") : "somebody-elses", maxAttempts: 3);

      Assert.Equal(new[] { One }, claim.Remove.ToArray());
      Assert.DoesNotContain(stranger, claim.Remove);
      Assert.Equal(0, claim.Theirs);
    }

    // ---- THE TURN AFTER AN APPLIED SPOT CANNOT BUILD -----------------------------------------------

    /// <summary>
    /// THE TEST WHOSE ENTIRE VALUE IS WHAT IT PREVENTS LATER.
    ///
    /// A spot is applied by the PLUGIN at the click, from the card's own record, and the turn that
    /// follows is a re-read. If a later refactor "helpfully" hands the placement to the approved turn
    /// instead - so the model calls build_region itself - then the card could name one set while another
    /// is placed, and EVERY GUARD WOULD PASS WITH A TRUE VERDICT, because nothing in the system reads the
    /// card. This is what goes red when someone re-opens that door.
    ///
    /// It is satisfied BY CONSTRUCTION rather than by a check: the continuation is ReadOnly, and ReadOnly
    /// grants no mutating tool at all. That is stronger than refusing a spot address inside build_region,
    /// because there is no turn in which the call is reachable to be refused.
    /// </summary>
    [Fact]
    public void The_read_that_follows_an_applied_spot_cannot_reach_build_region()
    {
      Assert.False(AgentLoopCore.IsGranted(TurnCapability.ReadOnly, AgentToolCatalog.BuildRegion));
      Assert.False(AgentLoopCore.IsGranted(TurnCapability.ReadOnly, AgentToolCatalog.ProposeScaffold));

      // ...and the control: a real build turn DOES reach it, so the assertion above is about the
      // capability rather than about the tool being unreachable everywhere (#212).
      Assert.True(AgentLoopCore.IsGranted(TurnCapability.Build, AgentToolCatalog.BuildRegion));
    }

    /// <summary>And a build turn approved for REGIONS cannot be talked into building at a spot address -
    /// the ledger refuses it by the path that already refuses every other unapproved address.</summary>
    [Fact]
    public void A_region_turn_cannot_build_at_a_spot()
    {
      var ledger = new BuildLedger(new object());
      ledger.Authorise(new[] { "A1", "A2" });

      Assert.False(ledger.MayBuild("Spot 1"));
      Assert.True(ledger.MayBuild("A1"));
    }

    // ---- the capability text is a contract, so it is tested on the BEHAVIOUR's side (#315) ----------

    /// <summary>
    /// #315: EVERYTHING WE TELL A MODEL ABOUT OURSELVES CAN GO STALE IN SILENCE, and only a test at the
    /// far end notices. build's CAN now claims Dwight can place components without a map; this
    /// asserts the claim against the machinery rather than against the string, so the sentence cannot
    /// outlive the behaviour it describes.
    /// </summary>
    [Fact]
    public void The_capability_sentence_claims_map_free_placement_and_the_machinery_allows_it()
    {
      var said = AgentHostTools.ScopeOf("build");
      Assert.Contains("WITHOUT a map", said);
      Assert.Contains("names every one on the card", said);

      // ...and the behaviour side of that claim, end to end: a placement small enough to name is
      // describable, a spot address is mintable, a turn can be authorised for it, and the build is
      // permitted at that address and nowhere else.
      Assert.True(SpotPlacement.TryDescribe(new[] { "a Panel" }, ScaffoldAddress.NextSpot(new string[0]),
        "Curve", out var placement, out _));

      var ledger = new BuildLedger(new object());
      ledger.AuthoriseSpot(placement.Address, grouped: true);

      Assert.Equal("Spot 1", placement.Address);
      Assert.True(ledger.MayBuild(placement.Address));
      Assert.False(ledger.MayBuild("A1"));
    }

    /// <summary>The other half of the same claim: work too large to name is refused toward the map, and
    /// the capability sentence says so. A CANNOT nobody enforces is the #315 defect in reverse.</summary>
    [Fact]
    public void The_capability_sentence_points_past_the_bound_and_the_bound_refuses_there()
    {
      Assert.Contains("propose_scaffold", AgentHostTools.ScopeOf("build"));

      Assert.False(SpotPlacement.TryDescribe(Components(SpotPlacement.MaxComponents + 1), "Spot 1", null,
        out _, out var error));
      Assert.Contains("propose_scaffold", error.Message);
    }

    /// <summary>The published descriptor is the contract the parser is tested against (#200/#202), and it
    /// must no longer invite the un-addressed build U18 closed.</summary>
    [Fact]
    public void The_descriptor_no_longer_offers_to_build_without_a_region()
    {
      var schema = AgentToolCatalog.SchemaOf(AgentToolCatalog.BuildRegion);

      Assert.DoesNotContain("Omit to add to the definition without a region", schema);
      Assert.Contains("Spot 1", schema);
    }

    /// <summary>A turn approved for REGIONS has no spot at all, so nothing can read a grouping choice out
    /// of it.</summary>
    [Fact]
    public void A_region_turn_carries_no_spot()
    {
      var ledger = new BuildLedger(new object());
      ledger.Authorise(new[] { "A1" });

      Assert.Null(ledger.ApprovedSpot);
      Assert.False(ledger.SpotIsGrouped("A1"));
      Assert.False(ledger.IsApprovedSpot("A1"));
    }
  }
}
