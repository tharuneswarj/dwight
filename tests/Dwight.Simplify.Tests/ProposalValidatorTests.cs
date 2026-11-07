using System;
using System.Collections.Generic;
using Dwight.Simplify;
using Xunit;

namespace Dwight.Simplify.Tests
{
  /// <summary>
  /// The minimal-diff proposal validator (WP-2 RR3). It guarantees keep/remove partition the selection (no
  /// disguised delete-replace of a kept component), every add type is placeable, the output surface is not
  /// collapsed (the 1-Panel whole-canvas class), and rewire refs are well-formed - all before any preview.
  /// </summary>
  public class ProposalValidatorTests
  {
    private static SimplifyContext Ctx(int[] selection, int[] producers, Func<string, bool> allow = null) =>
      new SimplifyContext
      {
        SelectionIds = new HashSet<int>(selection),
        OutputProducers = new HashSet<int>(producers),
        TypeAllowed = allow ?? (t => t != "Nonexistent"),
      };

    // selection {1,2,3}; node 3 produces the output; node 2 is a dead relay. keep 1+3, remove 2, add a Slider.
    private static SimplifyProposal RelayRemoval()
    {
      var p = new SimplifyProposal();
      p.Keep.Add(1); p.Keep.Add(3);
      p.Remove.Add(2);
      p.Add.Add(new AddNode { Type = "Number Slider", Nick = "radius" });
      p.Rewires.Add(new Rewire { From = "a0", FromPort = 0, To = "k3", ToPort = 0 });
      p.Changes.Add(new Change { Class = "remove-dead", Text = "The relay (2) does nothing; wired the slider straight to (3).", Confirm = false });
      return p;
    }

    [Fact]
    public void A_wellformed_partitioning_output_preserving_proposal_passes()
    {
      var (ok, error) = ProposalValidator.Validate(RelayRemoval(), Ctx(new[] { 1, 2, 3 }, new[] { 3 }));
      Assert.True(ok, error);
    }

    [Fact]
    public void An_all_keep_already_clean_proposal_passes()
    {
      var p = new SimplifyProposal();
      p.Keep.AddRange(new[] { 1, 2, 3 });
      p.Changes.Add(new Change { Class = "reduce", Text = "Already clean - nothing to improve.", Confirm = false });
      var (ok, error) = ProposalValidator.Validate(p, Ctx(new[] { 1, 2, 3 }, new[] { 3 }));
      Assert.True(ok, error);
    }

    [Fact]
    public void A_selected_component_neither_kept_nor_removed_fails()
    {
      var p = new SimplifyProposal();
      p.Keep.Add(1); p.Remove.Add(2);   // 3 is unaccounted for
      var (ok, error) = ProposalValidator.Validate(p, Ctx(new[] { 1, 2, 3 }, new[] { 3 }));
      Assert.False(ok);
      Assert.Contains("3", error);
    }

    [Fact]
    public void A_component_both_kept_and_removed_fails()
    {
      var p = new SimplifyProposal();
      p.Keep.AddRange(new[] { 1, 2 }); p.Remove.Add(2); p.Remove.Add(3);
      var (ok, _) = ProposalValidator.Validate(p, Ctx(new[] { 1, 2, 3 }, new[] { 3 }));
      Assert.False(ok);
    }

    [Fact]
    public void Keeping_a_component_outside_the_selection_fails()
    {
      var p = new SimplifyProposal();
      p.Keep.AddRange(new[] { 1, 2, 3, 99 });   // 99 is not in the selection
      var (ok, _) = ProposalValidator.Validate(p, Ctx(new[] { 1, 2, 3 }, new[] { 3 }));
      Assert.False(ok);
    }

    [Fact]
    public void An_unknown_added_type_fails_and_names_it()
    {
      var p = RelayRemoval();
      p.Add[0].Type = "Nonexistent";
      var (ok, error) = ProposalValidator.Validate(p, Ctx(new[] { 1, 2, 3 }, new[] { 3 }));
      Assert.False(ok);
      Assert.Contains("Nonexistent", error);
    }

    [Fact]
    public void Collapsing_the_output_surface_fails()
    {
      // the 1-Panel whole-canvas class: remove every producer, add a single Panel - fewer outputs than before.
      var p = new SimplifyProposal();
      p.Remove.AddRange(new[] { 1, 2, 3, 4, 5 });
      p.Add.Add(new AddNode { Type = "Panel" });
      var (ok, error) = ProposalValidator.Validate(p, Ctx(new[] { 1, 2, 3, 4, 5 }, new[] { 3, 4, 5 }));
      Assert.False(ok);
      Assert.Contains("collapse", error);
    }

    [Fact]
    public void Removing_a_named_relay_fails()
    {
      // RR4: a named relay is expressed design intent - deterministically sealed from removal.
      var p = new SimplifyProposal();
      p.Keep.Add(1); p.Keep.Add(3);
      p.Remove.Add(2);
      var ctx = Ctx(new[] { 1, 2, 3 }, new[] { 3 });
      ctx.SealedFromRemoval.Add(2);
      var (ok, error) = ProposalValidator.Validate(p, ctx);
      Assert.False(ok);
      Assert.Contains("named relay", error);
    }

    [Fact]
    public void A_rewire_to_a_component_not_in_the_proposal_fails()
    {
      var p = RelayRemoval();
      p.Rewires.Add(new Rewire { From = "k9", FromPort = 0, To = "k3", ToPort = 0 });   // k9 was not kept
      var (ok, _) = ProposalValidator.Validate(p, Ctx(new[] { 1, 2, 3 }, new[] { 3 }));
      Assert.False(ok);
    }

    [Fact]
    public void Removing_a_component_whose_kept_consumer_is_not_rewired_fails()
    {
      // RR4 completion: component 2 feeds kept component 3; the plan removes 2 but forgets to rewire 3 - reject
      // at preview rather than starve 3 and revert at layer 2.
      var p = new SimplifyProposal();
      p.Keep.Add(1); p.Keep.Add(3);
      p.Remove.Add(2);   // no rewire targeting k3
      var ctx = Ctx(new[] { 1, 2, 3 }, new[] { 3 });
      ctx.Consumers[2] = new HashSet<int> { 3 };
      var (ok, error) = ProposalValidator.Validate(p, ctx);
      Assert.False(ok);
      Assert.Contains("unwired", error);
    }

    [Fact]
    public void Removing_a_component_whose_kept_consumer_IS_rewired_passes()
    {
      var p = new SimplifyProposal();
      p.Keep.Add(1); p.Keep.Add(3);
      p.Remove.Add(2);
      p.Rewires.Add(new Rewire { From = "k1", FromPort = 0, To = "k3", ToPort = 0 });   // 3 re-fed from 1
      var ctx = Ctx(new[] { 1, 2, 3 }, new[] { 3 });
      ctx.Consumers[2] = new HashSet<int> { 3 };
      var (ok, error) = ProposalValidator.Validate(p, ctx);
      Assert.True(ok, error);
    }

    [Fact]
    public void Removing_a_component_whose_only_consumer_is_also_removed_passes()
    {
      var p = new SimplifyProposal();
      p.Keep.Add(1);
      p.Remove.Add(2); p.Remove.Add(3);   // 2 feeds 3, but 3 is removed too
      var ctx = Ctx(new[] { 1, 2, 3 }, new[] { 1 });
      ctx.Consumers[2] = new HashSet<int> { 3 };
      var (ok, error) = ProposalValidator.Validate(p, ctx);
      Assert.True(ok, error);
    }

    [Fact]
    public void A_selection_local_type_is_placeable_even_when_not_whitelisted()
    {
      // RR3 #3: "Insert in Tree" (present on the canvas but failing the whitelist by name) must be placeable
      // because it is already in the selection. The injected TypeAllowed encodes that; here it rejects only a
      // genuinely-new unknown, so a selection-local type passes.
      var p = RelayRemoval();
      p.Add[0].Type = "Insert in Tree";
      var (ok, error) = ProposalValidator.Validate(p, Ctx(new[] { 1, 2, 3 }, new[] { 3 }, allow: t => t == "Insert in Tree" || t == "Number Slider"));
      Assert.True(ok, error);
    }
  }
}
