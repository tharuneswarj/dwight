using System;
using System.Collections.Generic;
using System.Linq;
using Dwight.Agent;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// #338 - a consent card is a contract about scope.
  ///
  /// The field failure these exist to make impossible: a card named N components, the user approved it and
  /// answered one further question in the verb's own flow, and the verb ran on the whole canvas. The
  /// execution was faultless - byte-identical guard, one Ctrl-Z - and that is exactly why it needs a test
  /// rather than a guard. Correct execution of the wrong scope is still the wrong scope.
  ///
  /// #287's same-door claim was that a seed needs no new guards because it enters where a selection does.
  /// That was right about the guards and wrong about the STATE: the door is crossed several times in one
  /// flow, and only some of what crossed it the first time crossed it again. This is the test the claim
  /// should have carried from the start.
  /// </summary>
  public class VerbScopeTests
  {
    private static List<Guid> N(int n) => Enumerable.Range(0, n).Select(_ => Guid.NewGuid()).ToList();

    // -----------------------------------------------------------------------------------------
    // The two rules.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void A_seeded_route_can_never_reach_the_whole_canvas()
    {
      // Every combination of the OTHER state, including the one that produced the breach: confirmedWhole
      // carried forward as true while the seed had been lost. None of them may widen an approved scope.
      foreach (var confirmed in new[] { true, false })
        foreach (var selection in new[] { new List<Guid>(), N(3) })
          foreach (var seed in new[] { new List<Guid>(), N(2) })
          {
            var r = VerbScope.Resolve(true, seed, selection, confirmed);
            Assert.NotEqual(ScopeOutcome.WholeCanvas, r.Outcome);
            Assert.NotEqual(ScopeOutcome.AskWholeCanvas, r.Outcome);
          }
    }

    [Fact]
    public void An_approved_scope_that_resolves_to_nothing_refuses()
    {
      // It must not fall through to the selection either - "somewhere plausible" is the failure mode.
      var r = VerbScope.Resolve(true, new List<Guid>(), N(5), confirmedWhole: true);
      Assert.Equal(ScopeOutcome.Refuse, r.Outcome);
      Assert.Empty(r.Guids);
      // U28: with no word on what the card named, the copy claims only what holds whether it named none or named retired.
      Assert.Equal(VerbScope.SeededRefusal("reorganise", -1), r.Message);
      Assert.DoesNotContain("I named", r.Message);
    }

    // -----------------------------------------------------------------------------------------
    // U28 (#918): "named none" and "named retired" are different facts, and the refusal says which.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void A_card_that_named_none_is_refused_in_words_that_say_so()
    {
      var r = VerbScope.Resolve(true, new List<Guid>(), N(5), false, "reorganise", named: 0);
      Assert.Equal(ScopeOutcome.Refuse, r.Outcome);
      Assert.Equal("The card named no components, so there was nothing to reorganise and nothing was changed. " +
                   "Ask me again and I will name the components I mean.", r.Message);
      Assert.DoesNotContain("no longer on the canvas", r.Message);   // #918: false for the no-ids case
    }

    [Fact]
    public void A_card_whose_components_were_deleted_is_refused_in_words_that_say_so()
    {
      var r = VerbScope.Resolve(true, new List<Guid>(), null, false, "reorganise", named: 3);
      Assert.Equal("The 3 components the card named are no longer on the canvas, so there was nothing to reorganise " +
                   "and nothing was changed. Ask me again and I will look at what is there now.", r.Message);
      Assert.StartsWith("The component the card named is no longer",
                        VerbScope.Resolve(true, null, null, false, "reorganise", named: 1).Message);
    }

    /// <summary>The unknown case is true in BOTH states that can reach it - it claims neither "named none" nor "deleted".</summary>
    [Fact]
    public void Not_knowing_what_the_card_named_claims_neither()
    {
      var m = VerbScope.SeededRefusal("reorganise", -1);
      Assert.Equal("There was nothing on the canvas for this Reorganise to run on, so nothing was changed. " +
                   "Ask me again and I will look at what is there now.", m);
      Assert.DoesNotContain("no longer", m);
      Assert.DoesNotContain("named no", m);
    }

    /// <summary>Simplify's refusal speaks for Simplify - it used to say "nothing to reorganise".</summary>
    [Fact]
    public void Simplifys_refusal_names_simplify()
    {
      foreach (var named in new[] { -1, 0, 2 })
      {
        var m = VerbScope.Resolve(true, null, null, false, "simplify", named).Message;
        Assert.Contains("implify", m);
        Assert.DoesNotContain("eorganise", m);
      }
    }

    /// <summary>Every refusal copy says nothing was changed - true, because a refused scope runs on nothing.</summary>
    [Fact]
    public void Every_refusal_says_nothing_was_changed()
    {
      foreach (var verb in new[] { "reorganise", "simplify" })
        foreach (var named in new[] { -1, 0, 1, 4 })
        {
          var r = VerbScope.Resolve(true, new List<Guid>(), N(3), true, verb, named);
          Assert.Equal(ScopeOutcome.Refuse, r.Outcome);
          Assert.Contains("nothing was changed", r.Message);
        }
    }

    // -----------------------------------------------------------------------------------------
    // U28: NO CARD OVER NOTHING - the mint-time rule for reorganise and simplify.
    // -----------------------------------------------------------------------------------------

    private static Func<int, IdState> States(params (int id, IdState s)[] map) =>
      id => map.Where(m => m.id == id).Select(m => m.s).DefaultIfEmpty(IdState.Unknown).First();

    /// <summary>#918's field case: kind=reorganise, components EMPTY, nothing selected - refused to the model, by name,
    /// with the field to fill. No card can be minted over nothing.</summary>
    [Fact]
    public void An_ask_naming_none_with_nothing_selected_is_refused_to_the_model()
    {
      var refusal = VerbScope.ResolveAsk("reorganise", new int[0], States(), new int[0], out var scope);

      Assert.NotNull(refusal);
      Assert.Null(scope);
      Assert.False(refusal.Ok);
      Assert.Equal(ToolErrors.InvalidArgs, refusal.Code);
      Assert.Contains("named no components", refusal.Message);
      Assert.Contains("\"components\"", refusal.Message);
    }

    /// <summary>Naming none WITH a selection resolves the selection AT THE ASK, and the card says how many - the published
    /// schema's own promise ("instead of on whatever the user happens to have selected").</summary>
    [Fact]
    public void An_ask_naming_none_resolves_the_selection_at_the_ask_and_the_card_counts_it()
    {
      Assert.Null(VerbScope.ResolveAsk("simplify", null, States(), new[] { 4, 5, 5, 6 }, out var scope));
      Assert.True(scope.FromSelection);
      Assert.Equal(new[] { 4, 5, 6 }, scope.Ids);
      Assert.Equal("On the 3 components that were selected when Dwight asked.", VerbScope.CardScopeLine(scope));
      Assert.Equal("On the 1 component that was selected when Dwight asked.",
                   VerbScope.CardScopeLine(new AskScope { Ids = new[] { 7 }, FromSelection = true }));
    }

    [Fact]
    public void An_ask_naming_live_components_is_for_those_and_never_the_selection()
    {
      var refusal = VerbScope.ResolveAsk("reorganise", new[] { 1, 2, 3 },
        States((1, IdState.Alive), (2, IdState.Retired), (3, IdState.Alive)), new[] { 9, 10 }, out var scope);

      Assert.Null(refusal);
      Assert.False(scope.FromSelection);
      Assert.Equal(new[] { 1, 3 }, scope.Ids);
      Assert.Equal("On the 2 components Dwight named.", VerbScope.CardScopeLine(scope));
    }

    /// <summary>Named, and none of them is there: REFUSED, even with a selection to fall back on. Scope is identity (#208) -
    /// the selection is not what the model asked about. The refusal says which were deleted and which were never issued.</summary>
    [Fact]
    public void An_ask_whose_named_components_are_all_gone_is_refused_and_never_widened_to_the_selection()
    {
      var refusal = VerbScope.ResolveAsk("reorganise", new[] { 12, 14, 99 },
        States((12, IdState.Retired), (14, IdState.Retired)), new[] { 1, 2 }, out var scope);

      Assert.Null(scope);
      Assert.Equal(ToolErrors.RetiredId, refusal.Code);
      Assert.Contains("components 12, 14 were deleted from the canvas", refusal.Message);
      Assert.Contains("component 99 was never issued", refusal.Message);
      Assert.Contains("selection is not used", refusal.Message);

      var invented = VerbScope.ResolveAsk("simplify", new[] { 99 }, States(), new[] { 1 }, out _);
      Assert.Equal(ToolErrors.UnknownId, invented.Code);
    }

    [Theory]
    [InlineData("reorganise", true)]
    [InlineData("Simplify", true)]
    [InlineData("build", false)]
    [InlineData("set_value", false)]
    [InlineData(null, false)]
    public void Only_reorganise_and_simplify_are_component_scoped(string kind, bool expected)
    {
      Assert.Equal(expected, VerbScope.IsComponentScoped(kind));
    }

    // -----------------------------------------------------------------------------------------
    // #338's required assertions, in the words they were ruled in.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Driven_with_an_empty_seed_the_seeded_entry_refuses()
    {
      Assert.Equal(ScopeOutcome.Refuse, VerbScope.Resolve(true, null, null, false).Outcome);
      Assert.Equal(ScopeOutcome.Refuse, VerbScope.Resolve(true, new List<Guid>(), null, false).Outcome);
    }

    [Fact]
    public void Driven_with_N_guids_the_resolved_scope_is_exactly_those_N()
    {
      var seed = N(4);
      var r = VerbScope.Resolve(true, seed, N(500), confirmedWhole: true);

      Assert.Equal(ScopeOutcome.Seeded, r.Outcome);
      Assert.Equal(seed, r.Guids);          // exactly those, in that order
      Assert.Equal(4, r.Guids.Count);       // and emphatically not the 500 beside them
    }

    // -----------------------------------------------------------------------------------------
    // The hand-invoked path is unchanged - the fix must not cost the behaviour that already worked.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void A_hand_made_selection_is_used_as_it_always_was()
    {
      var sel = N(3);
      var r = VerbScope.Resolve(false, null, sel, confirmedWhole: false);
      Assert.Equal(ScopeOutcome.Selection, r.Outcome);
      Assert.Equal(sel, r.Guids);
    }

    [Fact]
    public void Nothing_selected_and_unconfirmed_still_asks_before_touching_everything()
    {
      Assert.Equal(ScopeOutcome.AskWholeCanvas,
                   VerbScope.Resolve(false, null, new List<Guid>(), confirmedWhole: false).Outcome);
    }

    [Fact]
    public void Nothing_selected_and_confirmed_is_the_whole_canvas()
    {
      Assert.Equal(ScopeOutcome.WholeCanvas,
                   VerbScope.Resolve(false, null, new List<Guid>(), confirmedWhole: true).Outcome);
    }

    [Fact]
    public void A_selection_beats_the_whole_canvas_confirm()
    {
      // A stale confirm must not widen a run that has a real selection.
      var sel = N(2);
      var r = VerbScope.Resolve(false, null, sel, confirmedWhole: true);
      Assert.Equal(ScopeOutcome.Selection, r.Outcome);
      Assert.Equal(sel, r.Guids);
    }

    // -----------------------------------------------------------------------------------------
    // Hygiene.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Empty_guids_and_duplicates_do_not_count_as_scope()
    {
      // An approved scope of nothing-but-noise must refuse, not run on the noise.
      var noise = new List<Guid> { Guid.Empty, Guid.Empty };
      Assert.Equal(ScopeOutcome.Refuse, VerbScope.Resolve(true, noise, N(3), false).Outcome);

      var g = Guid.NewGuid();
      var r = VerbScope.Resolve(true, new List<Guid> { g, g, Guid.Empty }, null, false);
      Assert.Equal(ScopeOutcome.Seeded, r.Outcome);
      Assert.Single(r.Guids);
    }
  }
}
