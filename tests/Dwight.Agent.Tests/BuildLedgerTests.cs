using System;
using System.Collections.Generic;
using System.Linq;
using Dwight.Agent;
using Dwight.Data;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// WP-4 Stage B: what a rebuild is allowed to delete, and what it must refuse.
  ///
  /// This is the most dangerous capability WP-4 introduces. <c>build_region</c> only ADDS, and a build
  /// that lands, preserves and completes but produces runtime errors is left STANDING - deliberately, so
  /// a region that legitimately errors until its neighbours exist is not thrown away. Correcting such a
  /// region therefore means replacing what is there, and replacing means DELETING.
  ///
  /// The four constraints of #352 are the contract, and each has a test whose failure would be a real
  /// deletion of somebody's work.
  /// </summary>
  public class BuildLedgerTests
  {
    private const int Cap = 3;
    private static readonly Guid Ours1 = new Guid("11111111-0000-0000-0000-000000000001");
    private static readonly Guid Ours2 = new Guid("11111111-0000-0000-0000-000000000002");

    private static BuildLedger Fresh() => new BuildLedger(new object());

    /// <summary>Placement, with an as-left token per component. "as-left" is the state Dwight left it in.</summary>
    private static Dictionary<Guid, string> AsLeft(params Guid[] guids) =>
      guids.ToDictionary(g => g, g => "as-left:" + g.ToString("N"));

    /// <summary>What the region holds NOW. By default every component reads exactly as Dwight left it;
    /// pass a guid to <paramref name="adopted"/> to say the user has since changed that one.</summary>
    private static Dictionary<Guid, string> Now(IEnumerable<Guid> guids, params Guid[] adopted) =>
      guids.ToDictionary(g => g, g => adopted.Contains(g) ? "user-changed:" + g.ToString("N")
                                                          : "as-left:" + g.ToString("N"));

    /// <summary>N components in the region, none of them Dwight's - for the cases that only care how
    /// many things are there.</summary>
    private static Dictionary<Guid, string> NowFor(int count) =>
      Enumerable.Range(0, count).ToDictionary(_ => Guid.NewGuid(), i => "someone-elses:" + i);

    // ---- constraint 1: guids, keyed by address ----------------------------------------------------

    [Fact]
    public void The_ledger_holds_the_GUIDS_this_turn_created_per_address()
    {
      var ledger = Fresh();
      ledger.CommitBuild("A1", AsLeft(Ours1, Ours2));

      Assert.Equal(new[] { Ours1, Ours2 }, ledger.Resolve("A1", Now(new[] { Ours1, Ours2 }), Cap).Remove);
      // another region's claim is not this one's
      Assert.Equal(RebuildDecision.Occupied, ledger.Resolve("B1", Now(new[] { Ours1, Ours2 }), Cap).Decision);
    }

    [Fact]
    public void Recording_twice_in_one_region_accumulates_without_duplicating()
    {
      var ledger = Fresh();
      ledger.CommitBuild("A1", AsLeft(Ours1));
      ledger.CommitBuild("A1", AsLeft(Ours1, Ours2));

      Assert.Equal(new[] { Ours1, Ours2 }, ledger.Resolve("A1", Now(new[] { Ours1, Ours2 }), Cap).Remove);
    }

    // ---- #623/#632: a FACT is never destroyed, so a revert and an undo both resolve ----------------

    private static readonly Guid New1 = new Guid("22222222-0000-0000-0000-000000000001");
    private static readonly Guid New2 = new Guid("22222222-0000-0000-0000-000000000002");

    /// <summary>
    /// (a) #623's SEQUENCE AT THE SEAM. A rebuild resolves, removes its claim, counts the attempt, fails its
    /// verdict and reverts - and the revert restores the removed components under their own guids. The ledger
    /// used to forget them inside the mutation, so the next attempt read "NO RECORD ... restarted". Nothing
    /// but a VERIFIED commit writes the ledger now, and nothing ever forgets, so the second attempt resolves
    /// exactly as the first did.
    /// </summary>
    [Fact]
    public void A_counted_but_uncommitted_rebuild_resolves_again_to_the_same_removal()
    {
      var ledger = Fresh();
      ledger.CommitBuild("A1", AsLeft(Ours1, Ours2));

      var first = ledger.Resolve("A1", Now(new[] { Ours1, Ours2 }), Cap);
      ledger.CountAttempt("A1");   // the attempt ran, failed its verdict, and was reverted: no commit

      var second = ledger.Resolve("A1", Now(new[] { Ours1, Ours2 }), Cap);

      Assert.Equal(RebuildDecision.Replace, second.Decision);
      Assert.Equal(first.Remove, second.Remove);
      Assert.True(second.HasRecordForRegion);
    }

    /// <summary>
    /// (b) THE USER'S Ctrl-Z OF A VERIFIED REBUILD. Every build line promises "one Ctrl-Z removes it", and
    /// the undo brings the previous components back under their own guids while the rebuild's vanish. Those
    /// old components are still Dwight's and still as Dwight left them, so a rebuild must be able to replace
    /// them - which it could not while a commit forgot what it replaced.
    /// </summary>
    [Fact]
    public void After_the_user_undoes_a_verified_rebuild_the_OLD_components_are_replaceable()
    {
      var ledger = Fresh();
      ledger.CommitBuild("A1", AsLeft(Ours1, Ours2));
      ledger.Resolve("A1", Now(new[] { Ours1, Ours2 }), Cap);
      ledger.CommitBuild("A1", AsLeft(New1, New2));   // the rebuild verified: New* on the canvas, Ours* removed
      ledger.Resolve("A1", Now(new[] { New1, New2 }), Cap);

      // Ctrl-Z: the old ones are back exactly as they were, the new ones are gone.
      var claim = ledger.Resolve("A1", Now(new[] { Ours1, Ours2 }), Cap);

      Assert.Equal(RebuildDecision.Replace, claim.Decision);
      Assert.Equal(new[] { Ours1, Ours2 }, claim.Remove);
      Assert.Equal(0, claim.Theirs);
    }

    /// <summary>(c) And the redo that follows: the rebuild's components are back, and they are the claim.</summary>
    [Fact]
    public void After_a_redo_the_NEW_components_are_replaceable()
    {
      var ledger = Fresh();
      ledger.CommitBuild("A1", AsLeft(Ours1, Ours2));
      ledger.CommitBuild("A1", AsLeft(New1, New2));
      ledger.Resolve("A1", Now(new[] { Ours1, Ours2 }), Cap);   // undone

      var claim = ledger.Resolve("A1", Now(new[] { New1, New2 }), Cap);   // redone

      Assert.Equal(RebuildDecision.Replace, claim.Decision);
      Assert.Equal(new[] { New1, New2 }, claim.Remove);
      Assert.Equal(0, claim.Theirs);
    }

    /// <summary>(d) Filtering in place of pruning widens nothing: a component the user has edited is still
    /// never in Remove, whether its fact was dormant in between or not.</summary>
    [Fact]
    public void An_adopted_component_is_never_in_Remove_after_it_was_dormant()
    {
      var ledger = Fresh();
      ledger.CommitBuild("A1", AsLeft(Ours1, Ours2));
      ledger.Resolve("A1", Now(new[] { Ours1 }), Cap);   // Ours2 dragged out of the region for a while

      var claim = ledger.Resolve("A1", Now(new[] { Ours1, Ours2 }, Ours2), Cap);   // back, and edited

      Assert.Equal(new[] { Ours1 }, claim.Remove);
      Assert.Equal(new[] { Ours2 }, claim.Adopted);
      Assert.DoesNotContain(Ours2, claim.RemovalPresentIn(_ => true));
    }

    /// <summary>
    /// (e) THE BOUND. Facts are never destroyed by a rebuild, so they are bounded instead: at most
    /// <see cref="BuildLedger.MaxFactsPerRegion"/> per region, dropping the OLDEST first. A dropped fact makes
    /// that component read as not-Dwight's, which is the safe direction - it is left standing, never removed.
    /// </summary>
    [Fact]
    public void Facts_per_region_are_bounded_and_the_oldest_go_first()
    {
      var ledger = Fresh();
      var oldest = Guid.NewGuid();
      ledger.CommitBuild("A1", AsLeft(oldest));
      var rest = Enumerable.Range(0, BuildLedger.MaxFactsPerRegion).Select(_ => Guid.NewGuid()).ToArray();
      ledger.CommitBuild("A1", AsLeft(rest));

      Assert.Equal(BuildLedger.MaxFactsPerRegion, ledger.FactCount("A1"));

      var claim = ledger.Resolve("A1", Now(new[] { oldest, rest[rest.Length - 1] }), Cap);
      Assert.Equal(new[] { rest[rest.Length - 1] }, claim.Remove);
      Assert.Equal(1, claim.Theirs);   // the dropped one is left alone, never removed
    }

    // ---- #625/#648(v): the as-left refresh after Dwight's own reconnection is COMPARE-AND-SET ---------

    private static readonly Guid Consumer = new Guid("33333333-0000-0000-0000-000000000001");

    private static AsLeftState State(string token, Guid? input = null, params Guid[] sources) => new AsLeftState
    {
      Token = token,
      Sources = input.HasValue
        ? new Dictionary<Guid, IReadOnlyList<Guid>> { [input.Value] = sources.ToList() }
        : null,
      Outputs = new[] { Guid.NewGuid() },
    };

    /// <summary>
    /// THE CASE THE REFRESH EXISTS FOR. B1's XY Plane was exactly as Dwight left it. A rebuild of A1 reconnected it,
    /// which changed its sources and so its token. Without the refresh, Dwight's own rebuild makes its downstream
    /// read as edited-by-the-user, and the next rebuild of B1 LEAVES it.
    /// </summary>
    [Fact]
    public void An_untouched_consumer_is_refreshed_and_stays_Dwights_to_replace()
    {
      var ledger = Fresh();
      ledger.CommitBuild("B1", new Dictionary<Guid, string> { [Consumer] = "t-as-left" });

      Assert.True(ledger.RefreshAsLeft(Consumer, expectedBefore: "t-as-left", State("t-after-reconnect")));

      var claim = ledger.Resolve("B1", new Dictionary<Guid, string> { [Consumer] = "t-after-reconnect" }, Cap);
      Assert.Equal(new[] { Consumer }, claim.Remove);
      Assert.Empty(claim.Adopted);
    }

    /// <summary>
    /// THE LAUNDER TEST (#648 v). The user had ALREADY edited the XY Plane before the rebuild ran. Refreshing its
    /// record to the post-reconnection state would turn their edit into "untouched since Dwight left it" - a licence
    /// to delete their work at the next rebuild. Compare-and-set refuses, and the component stays theirs.
    /// </summary>
    [Fact]
    public void An_already_adopted_consumer_is_NEVER_refreshed()
    {
      var ledger = Fresh();
      ledger.CommitBuild("B1", new Dictionary<Guid, string> { [Consumer] = "t-as-left" });

      // Immediately before the rebuild the user's edit had already changed it.
      Assert.False(ledger.RefreshAsLeft(Consumer, expectedBefore: "t-user-edited", State("t-after-reconnect")));

      var claim = ledger.Resolve("B1", new Dictionary<Guid, string> { [Consumer] = "t-after-reconnect" }, Cap);
      Assert.Empty(claim.Remove);
      Assert.Equal(new[] { Consumer }, claim.Adopted);
    }

    [Fact]
    public void A_refresh_finds_the_consumer_in_whichever_region_holds_it()
    {
      var ledger = Fresh();
      ledger.CommitBuild("A1", AsLeft(Ours1));
      ledger.CommitBuild("B1", new Dictionary<Guid, string> { [Consumer] = "t0" });

      Assert.True(ledger.RefreshAsLeft(Consumer, "t0", State("t1")));
      Assert.Equal("t1", ledger.AsLeftOf(Consumer).Token);
      Assert.Equal("as-left:" + Ours1.ToString("N"), ledger.AsLeftOf(Ours1).Token);   // the other region untouched
    }

    [Fact]
    public void A_refresh_of_something_Dwight_never_placed_does_nothing()
    {
      var ledger = Fresh();
      ledger.CommitBuild("B1", AsLeft(Ours1));

      Assert.False(ledger.RefreshAsLeft(Consumer, "t0", State("t1")));
      Assert.Null(ledger.AsLeftOf(Consumer));
      Assert.Equal(1, ledger.FactCount("B1"));
    }

    [Fact]
    public void A_refresh_with_no_known_before_state_does_nothing()
    {
      var ledger = Fresh();
      ledger.CommitBuild("B1", new Dictionary<Guid, string> { [Consumer] = "t0" });

      Assert.False(ledger.RefreshAsLeft(Consumer, null, State("t1")));
      Assert.False(ledger.RefreshAsLeft(Consumer, "", State("t1")));
      Assert.Equal("t0", ledger.AsLeftOf(Consumer).Token);
    }

    /// <summary>What the narrow sibling will diff against: the ordered sources per input and the output guids,
    /// recorded at commit and carried by a refresh.</summary>
    [Fact]
    public void A_commit_and_a_refresh_record_the_structured_as_left_state()
    {
      var ledger = Fresh();
      var input = Guid.NewGuid();
      var src1 = Guid.NewGuid();
      var src2 = Guid.NewGuid();

      ledger.CommitBuild("B1", new Dictionary<Guid, AsLeftState> { [Consumer] = State("t0", input, src1) });
      var recorded = ledger.AsLeftOf(Consumer);
      Assert.Equal(new[] { src1 }, recorded.Sources[input]);
      Assert.Single(recorded.Outputs);

      ledger.RefreshAsLeft(Consumer, "t0", State("t1", input, src2, src1));
      Assert.Equal(new[] { src2, src1 }, ledger.AsLeftOf(Consumer).Sources[input]);
    }

    /// <summary>#625's narrow sibling: after a verified RESTORE the record is set to the post-build state, compared
    /// against the STORED token (the diff proved the lost source was the only difference). If the record moved in
    /// between, the compare-and-set still refuses.</summary>
    [Fact]
    public void A_restore_refreshes_against_the_stored_record_and_refuses_if_it_moved()
    {
      var ledger = Fresh();
      ledger.CommitBuild("B1", new Dictionary<Guid, string> { [Consumer] = "t-as-left-with-A1s-source" });

      Assert.True(ledger.RefreshAsLeft(Consumer, ledger.AsLeftOf(Consumer).Token, State("t-restored")));
      Assert.Equal("t-restored", ledger.AsLeftOf(Consumer).Token);

      Assert.False(ledger.RefreshAsLeft(Consumer, "t-as-left-with-A1s-source", State("t-again")));
    }

    [Fact]
    public void A_token_only_commit_records_no_structure_rather_than_an_empty_one()
    {
      var ledger = Fresh();
      ledger.CommitBuild("A1", AsLeft(Ours1));

      Assert.Null(ledger.AsLeftOf(Ours1).Sources);
      Assert.Null(ledger.AsLeftOf(Ours1).Outputs);
    }

    // ---- #631: a rebuild removes what the CARD named, never what the user has since edited -----------

    /// <summary>
    /// THE CARD IS A CONTRACT ABOUT SCOPE (#338), IN THE DELETE PATH. The consent card is drawn from the
    /// resolved claim's Remove and says "LEAVE the 1 you have edited since". The build used to remove
    /// everything the ledger had recorded that was still on the canvas - Remove AND Adopted - so it deleted
    /// the very component the card promised to leave, and preservation certified it (the adopted guid was
    /// in the replaced set, so it was spliced out of the baseline).
    ///
    /// The removal set now exists only as a function of the resolved claim, and the ledger has no guid
    /// list a caller could remove from instead.
    /// </summary>
    [Fact]
    public void A_rebuild_removes_only_what_the_claim_names_never_an_adopted_component()
    {
      var ledger = Fresh();
      ledger.CommitBuild("A1", AsLeft(Ours1, Ours2));

      // The user has edited Ours2 since Dwight placed it.
      var claim = ledger.Resolve("A1", Now(new[] { Ours1, Ours2 }, Ours2), Cap);
      var removal = claim.RemovalPresentIn(_ => true);

      Assert.Equal(new[] { Ours1 }, removal);
      Assert.DoesNotContain(Ours2, removal);
    }

    /// <summary>The removal set is also bounded by what is on the canvas at the moment of removal: a
    /// component that vanished between the resolve and the delete is not deleted twice.</summary>
    [Fact]
    public void The_removal_set_keeps_only_what_is_still_on_the_canvas()
    {
      var ledger = Fresh();
      ledger.CommitBuild("A1", AsLeft(Ours1, Ours2));

      var claim = ledger.Resolve("A1", Now(new[] { Ours1, Ours2 }), Cap);

      Assert.Equal(new[] { Ours2 }, claim.RemovalPresentIn(g => g == Ours2));
      Assert.Empty(claim.RemovalPresentIn(null));
    }

    // ---- constraint 3: an empty ledger REFUSES, never falls back to group membership ---------------

    /// <summary>THE ONE THAT MATTERS MOST. The region holds components and this turn did not place them,
    /// so we cannot name what we would be deleting - and by now they may be the USER'S. Falling back to
    /// "delete what is in the group" is #208's silent fallback pointed at a delete.</summary>
    [Fact]
    public void A_region_holding_components_this_turn_did_NOT_create_is_REFUSED()
    {
      var ledger = Fresh();   // knows nothing about A1

      Assert.Equal(RebuildDecision.Occupied, ledger.Resolve("A1", NowFor(4), maxAttempts: Cap).Decision);
    }

    // ---- F1(b): a FACT outlives a turn; a PERMISSION does not -------------------------------------

    /// <summary>
    /// THE DEFECT THIS REPLACES. This test used to assert the OPPOSITE - that a region built by an
    /// earlier turn was, to this one, somebody else's work - and it passed, because that is exactly what
    /// the code did. A user who built A1 and then asked in their NEXT MESSAGE to rebuild it was told
    /// Dwight had not placed components Dwight had just placed.
    ///
    /// "Dwight placed this" is a fact, and a fact does not stop being true when the user sends another
    /// message. What keeps the removal safe is no longer the scope - it is re-derivation, plus a consent
    /// card that names what will go.
    /// </summary>
    [Fact]
    public void What_Dwight_PLACED_survives_a_new_turn()
    {
      var ledger = Fresh();
      ledger.CommitBuild("A1", AsLeft(Ours1));

      ledger.BeginTurn(new object());   // the user sends another message

      var claim = ledger.Resolve("A1", Now(new[] { Ours1 }), maxAttempts: Cap);

      Assert.Equal(RebuildDecision.Replace, claim.Decision);
      Assert.Equal(new[] { Ours1 }, claim.Remove);
    }

    /// <summary>
    /// AND THE PERMISSION DOES NOT SURVIVE WITH IT. This is the half that makes widening the fact's
    /// lifetime safe: an approval that outlived the turn it was given in would be a STANDING permission,
    /// which is the privilege concern in its purest form.
    /// </summary>
    [Fact]
    public void An_AUTHORISATION_never_survives_the_turn_it_was_given_in()
    {
      var ledger = Fresh();
      ledger.Authorise(new[] { "A1" });
      Assert.True(ledger.IsAuthorityScoped);

      ledger.BeginTurn(new object());

      Assert.False(ledger.IsAuthorityScoped);
      Assert.Empty(ledger.Authorised);
    }

    /// <summary>The attempt cap is the turn's too - carried further it would retire a region permanently
    /// after three tries, which is a different and much worse rule than the one it was written for.</summary>
    [Fact]
    public void The_attempt_cap_resets_with_the_turn()
    {
      var ledger = Fresh();
      for (var i = 0; i < Cap; i++) ledger.CountAttempt("A1");
      Assert.Equal(Cap, ledger.Attempts("A1"));
      Assert.Equal(RebuildDecision.Exhausted, ledger.Resolve("A1", NowFor(0), Cap).Decision);

      ledger.BeginTurn(new object());

      // U55: the next turn starts at ZERO attempts, so the sentence "the attempts start again on your next turn" is true.
      Assert.Equal(0, ledger.Attempts("A1"));

      Assert.NotEqual(RebuildDecision.Exhausted, ledger.Resolve("A1", NowFor(0), Cap).Decision);
    }

    /// <summary>U55: the exhausted refusal and the build_region descriptor both say the count restarts on the next turn,
    /// and the ledger behaves that way (the test above). The sentence is pinned so it cannot drift from the behaviour.</summary>
    [Fact]
    public void The_exhausted_refusal_and_the_descriptor_say_the_attempts_restart_next_turn()
    {
      var msg = BuildLedger.ExhaustedMessage("B1", 3);
      Assert.StartsWith("You have used all 3 attempts at B1 in this turn.", msg);
      Assert.Contains("The attempts start again on your next turn", msg);
      Assert.Contains("on a later turn you may ask to build it again", msg);

      var desc = AgentToolCatalog.All().Single(t => t.Name == "build_region").Description;
      Assert.Contains("a limited number of attempts per region in each turn", desc);
      Assert.Contains("The count starts again on the next turn.", desc);
    }

    // ---- F1(b): re-derivation, and what the user has ADOPTED ---------------------------------------

    /// <summary>
    /// A claim on something the user has since DELETED, or dragged out of the group, has stopped being a
    /// claim. Re-derivation is what makes a long-lived claim safe: it is checked against the canvas at
    /// the moment of use rather than trusted from storage.
    /// </summary>
    [Fact]
    public void A_claim_on_something_no_longer_there_is_not_a_claim()
    {
      var ledger = Fresh();
      ledger.CommitBuild("A1", AsLeft(Ours1, Ours2));

      // Only Ours1 is still in the region; the user deleted Ours2.
      var claim = ledger.Resolve("A1", Now(new[] { Ours1 }), maxAttempts: Cap);

      Assert.Equal(new[] { Ours1 }, claim.Remove);
      Assert.DoesNotContain(Ours2, claim.Remove);
    }

    /// <summary>
    /// THE ADOPTION CASE, and the whole reason the as-left token exists. Dwight placed it; the user has
    /// since changed it; it is theirs now in every sense that matters, and a rebuild leaves it alone.
    /// </summary>
    [Fact]
    public void A_component_the_user_has_CHANGED_is_left_alone()
    {
      var ledger = Fresh();
      ledger.CommitBuild("A1", AsLeft(Ours1, Ours2));

      var claim = ledger.Resolve("A1", Now(new[] { Ours1, Ours2 }, Ours2), maxAttempts: Cap);

      Assert.Equal(RebuildDecision.Replace, claim.Decision);
      Assert.Equal(new[] { Ours1 }, claim.Remove);
      Assert.Equal(new[] { Ours2 }, claim.Adopted);
    }

    /// <summary>When the user has adopted EVERYTHING Dwight placed there is nothing left to remove, so
    /// the rebuild refuses rather than proceeding with an empty removal.</summary>
    [Fact]
    public void When_every_placed_component_has_been_adopted_the_rebuild_is_REFUSED()
    {
      var ledger = Fresh();
      ledger.CommitBuild("A1", AsLeft(Ours1));

      var claim = ledger.Resolve("A1", Now(new[] { Ours1 }, Ours1), maxAttempts: Cap);

      Assert.Equal(RebuildDecision.Occupied, claim.Decision);
      Assert.Empty(claim.Remove);
      Assert.Equal(new[] { Ours1 }, claim.Adopted);
    }

    /// <summary>An unreadable token counts as CHANGED. A check that decides what gets DELETED must fail
    /// towards leaving things standing.</summary>
    [Fact]
    public void An_unreadable_token_counts_as_changed_so_the_component_survives()
    {
      var ledger = Fresh();
      ledger.CommitBuild("A1", new Dictionary<Guid, string> { [Ours1] = null });

      var claim = ledger.Resolve("A1", new Dictionary<Guid, string> { [Ours1] = null }, maxAttempts: Cap);

      Assert.Empty(claim.Remove);
      Assert.Equal(new[] { Ours1 }, claim.Adopted);
    }

    /// <summary>The user's own components are counted and never touched - and that count is what the
    /// consent card shows under "and leave".</summary>
    [Fact]
    public void Components_that_were_never_Dwights_are_counted_and_never_removed()
    {
      var ledger = Fresh();
      ledger.CommitBuild("A1", AsLeft(Ours1));

      var present = Now(new[] { Ours1 });
      present[Guid.NewGuid()] = "someone-elses";

      var claim = ledger.Resolve("A1", present, maxAttempts: Cap);

      Assert.Equal(new[] { Ours1 }, claim.Remove);
      Assert.Equal(1, claim.Theirs);
    }

    [Fact]
    public void A_region_this_turn_built_may_be_REPLACED()
    {
      var ledger = Fresh();
      ledger.CommitBuild("A1", AsLeft(Ours1, Ours2));

      var claim = ledger.Resolve("A1", Now(new[] { Ours1, Ours2 }), maxAttempts: Cap);

      Assert.Equal(RebuildDecision.Replace, claim.Decision);
      Assert.Equal(new[] { Ours1, Ours2 }, claim.Remove);
      Assert.Empty(claim.Adopted);
      Assert.Equal(0, claim.Theirs);
    }

    [Fact]
    public void An_empty_region_is_a_FRESH_build()
    {
      Assert.Equal(RebuildDecision.Fresh, Fresh().Resolve("A1", NowFor(0), maxAttempts: Cap).Decision);
    }

    // ---- constraint 4: a rebuild counts against the cap -------------------------------------------

    [Fact]
    public void The_cap_is_reached_after_the_declared_number_of_attempts()
    {
      var ledger = Fresh();
      for (var i = 0; i < Cap; i++)
      {
        Assert.NotEqual(RebuildDecision.Exhausted, ledger.Resolve("A1", NowFor(0), Cap).Decision);
        ledger.CountAttempt("A1");
      }

      Assert.Equal(RebuildDecision.Exhausted, ledger.Resolve("A1", NowFor(0), Cap).Decision);
      Assert.Equal(Cap, ledger.Attempts("A1"));
    }

    /// <summary>Exhaustion outranks everything: a region whose budget is spent is refused even though it
    /// is one this turn created and could otherwise replace. Otherwise a model that can always replace
    /// its own work loops on one region until the turn's step budget runs out, paying for each attempt.</summary>
    [Fact]
    public void Exhaustion_outranks_a_replaceable_region()
    {
      var ledger = Fresh();
      ledger.CommitBuild("A1", AsLeft(Ours1));
      for (var i = 0; i < Cap; i++) ledger.CountAttempt("A1");

      Assert.Equal(RebuildDecision.Exhausted, ledger.Resolve("A1", NowFor(1), maxAttempts: Cap).Decision);
    }

    /// <summary>
    /// #626: THE CARD PREVIEWS THE TURN IT OPENS, NOT THE TURN IT IS DRAWN IN. A request minted after A1's
    /// attempts are spent is drawn inside the exhausted turn, where Resolve answers Exhausted with nothing to
    /// remove - so the card would name no removal while the approved turn, with a fresh budget, removes all of
    /// it. The preview resolves as the next turn will.
    /// </summary>
    [Fact]
    public void The_next_turns_resolve_ignores_this_turns_spent_attempts()
    {
      var ledger = Fresh();
      ledger.CommitBuild("A1", AsLeft(Ours1, Ours2));
      for (var i = 0; i < Cap; i++) ledger.CountAttempt("A1");

      Assert.Equal(RebuildDecision.Exhausted, ledger.Resolve("A1", Now(new[] { Ours1, Ours2 }), Cap).Decision);

      var next = ledger.ResolveForNextTurn("A1", Now(new[] { Ours1, Ours2 }, Ours2));
      Assert.Equal(RebuildDecision.Replace, next.Decision);
      Assert.Equal(new[] { Ours1 }, next.Remove);
      Assert.Equal(new[] { Ours2 }, next.Adopted);
    }

    [Fact]
    public void The_next_turns_resolve_agrees_with_Resolve_when_attempts_remain()
    {
      var ledger = Fresh();
      ledger.CommitBuild("A1", AsLeft(Ours1));
      var present = Now(new[] { Ours1 });
      present[Guid.NewGuid()] = "someone-elses";

      var now = ledger.Resolve("A1", present, Cap);
      var next = ledger.ResolveForNextTurn("A1", present);

      Assert.Equal(now.Decision, next.Decision);
      Assert.Equal(now.Remove, next.Remove);
      Assert.Equal(now.Theirs, next.Theirs);
      Assert.Equal(RebuildDecision.Fresh, ledger.ResolveForNextTurn("A1", NowFor(0)).Decision);
      Assert.Equal(RebuildDecision.Fresh, ledger.ResolveForNextTurn(null, present).Decision);
    }

    [Fact]
    public void The_cap_is_per_REGION_not_per_turn()
    {
      var ledger = Fresh();
      for (var i = 0; i < Cap; i++) ledger.CountAttempt("A1");

      Assert.Equal(RebuildDecision.Exhausted, ledger.Resolve("A1", NowFor(0), Cap).Decision);
      Assert.Equal(RebuildDecision.Fresh, ledger.Resolve("B1", NowFor(0), Cap).Decision);
    }

    /// <summary>Q3 (#354): a build with no region has no per-region state to cap. Stated rather than
    /// implied - the turn's step budget is what bounds it, and the report says so.</summary>
    [Fact]
    public void An_un_addressed_build_is_always_Fresh_and_uncapped()
    {
      var ledger = Fresh();
      for (var i = 0; i < 10; i++) ledger.CountAttempt(null);

      Assert.Equal(RebuildDecision.Fresh, ledger.Resolve(null, NowFor(9), maxAttempts: Cap).Decision);
    }

    // ---- the preservation baseline a rebuild is judged against ------------------------------------

    private static FpObject Obj(Guid guid, string type, params Guid[] sources) => new FpObject
    {
      Guid = guid,
      TypeName = type,
      Inputs = { new FpInput { Sources = sources.ToList() } },
    };

    private static readonly Guid Existing = new Guid("99999999-0000-0000-0000-000000000001");
    private static readonly Guid Upstream = new Guid("99999999-0000-0000-0000-000000000002");

    private static readonly Guid OursOut1 = new Guid("11111111-0000-0000-0000-0000000000a1");
    private static readonly Guid OursOut2 = new Guid("11111111-0000-0000-0000-0000000000a2");

    private static BuildIdentity Rebuild(Guid[] replaced, Guid[] createdObjects, Guid[] createdSources) =>
      new BuildIdentity
      {
        ReplacedObjects = new HashSet<Guid>(replaced),
        CreatedObjects = new HashSet<Guid>(createdObjects),
        CreatedSources = new HashSet<Guid>(createdSources),
      };

    /// <summary>
    /// THE ORDINARY REBUILD STILL LANDS. The region's previous attempt fed nothing outside itself: the rebuild
    /// removes Ours1, places Ours2 wired from the same upstream, and nothing that stays was touched.
    /// </summary>
    [Fact]
    public void A_rebuild_that_feeds_nothing_outside_what_it_removes_is_preserved()
    {
      var before = new FpDoc { Objects = { Obj(Upstream, "Slider"), Obj(Ours1, "Panel", Upstream) } };
      var after = new FpDoc { Objects = { Obj(Upstream, "Slider"), Obj(Ours2, "Panel", Upstream) } };

      var result = BuildPreservation.Check(before, after, Rebuild(new[] { Ours1 }, new[] { Ours2 }, new[] { Ours2 }));

      Assert.True(result.Preserved, result.Describe());
    }

    /// <summary>
    /// #625/#643, THE SILENT DISCONNECT, NOW CAUGHT. A replaced floating PARAM (a Panel, whose own guid is its
    /// wire's source) fed a pre-existing Loft. The old baseline spliced its guid out of the Loft's sources, so a
    /// rebuild that placed nothing in its place left the Loft disconnected and certified it preserved. A wire from
    /// what a rebuild removes into what it keeps is a CUT; undeclared, it is tampering.
    /// </summary>
    [Fact]
    public void An_undeclared_cut_from_a_replaced_PARAM_is_NOT_preserved()
    {
      var before = new FpDoc { Objects = { Obj(Ours1, "Panel"), Obj(Existing, "Loft", Ours1) } };
      var after = new FpDoc { Objects = { Obj(Existing, "Loft") } };   // rebuilt to place nothing at all

      var result = BuildPreservation.Check(before, after, Rebuild(new[] { Ours1 }, new Guid[0], new Guid[0]));

      Assert.False(result.Preserved);
      Assert.Contains(Existing, result.Rewired);
    }

    /// <summary>
    /// #625/#643, THE SAME CUT FROM A COMPONENT: Ours1 is a Construct Point whose OUTPUT param OursOut1 fed the
    /// pre-existing XY Plane. The rebuild placed a new Construct Point (output OursOut2) and appended it - an
    /// undeclared replacement is still a cut, and a new source never excuses losing an old one.
    /// </summary>
    [Fact]
    public void An_undeclared_cut_from_a_replaced_COMPONENTs_output_is_NOT_preserved()
    {
      var before = new FpDoc { Objects = { Obj(Ours1, "Construct Point"), Obj(Existing, "XY Plane", OursOut1) } };
      var after = new FpDoc { Objects = { Obj(Ours2, "Construct Point"), Obj(Existing, "XY Plane", OursOut2) } };

      var result = BuildPreservation.Check(before, after,
        Rebuild(new[] { Ours1 }, new[] { Ours2 }, new[] { OursOut2 }));

      Assert.False(result.Preserved);
      Assert.Contains(Existing, result.Rewired);
    }

    /// <summary>And the loosening is EXACTLY bounded: a component that is not in the replaced set still
    /// counts as deleted.</summary>
    [Fact]
    public void Deleting_anything_NOT_in_the_replaced_set_still_fails_preservation()
    {
      var before = new FpDoc { Objects = { Obj(Existing, "Loft"), Obj(Ours1, "Panel") } };
      var after = new FpDoc { Objects = { Obj(Ours1, "Panel") } };   // the EXISTING one vanished

      var result = BuildPreservation.Check(before, after, Rebuild(new[] { Ours1 }, new Guid[0], new Guid[0]));

      Assert.False(result.Preserved);
      Assert.Contains(Existing, result.Deleted);
    }

    /// <summary>A rebuild has no licence to touch a pre-existing component's own properties. The baseline
    /// changes membership, never the rules applied to what remains.</summary>
    [Fact]
    public void A_rebuild_may_still_not_alter_a_pre_existing_component()
    {
      var before = new FpDoc { Objects = { Obj(Ours1, "Panel"), new FpObject { Guid = Existing, TypeName = "Slider", ValueToken = "5" } } };
      var after = new FpDoc { Objects = { new FpObject { Guid = Existing, TypeName = "Slider", ValueToken = "9" } } };

      var result = BuildPreservation.Check(before, after, Rebuild(new[] { Ours1 }, new Guid[0], new Guid[0]));

      Assert.False(result.Preserved);
      Assert.Contains(Existing, result.Mutated);
    }

    /// <summary>With nothing replaced, the rebuild path cannot have changed the ordinary build's verdict.</summary>
    [Fact]
    public void With_nothing_replaced_the_check_is_unchanged()
    {
      var before = new FpDoc { Objects = { Obj(Existing, "Loft", Upstream), Obj(Upstream, "Slider") } };
      var after = new FpDoc { Objects = { Obj(Existing, "Loft", Upstream), Obj(Upstream, "Slider") } };

      Assert.True(BuildPreservation.Check(before, after, new BuildIdentity()).Preserved);
      Assert.True(BuildPreservation.Check(before, after, null).Preserved);
    }
  }
}
