using System;
using System.Collections.Generic;
using Dwight.Agent;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// #291 - the conversation made navigable, and kept honest as the canvas moves on.
  ///
  /// The two properties these exist to hold down are opposites of each other, and both are the
  /// mis-grounding class wearing a UI costume: a click must never take you somewhere PLAUSIBLE when the
  /// real target is gone, and a number that was never a component must never LOOK like one you can go to.
  /// </summary>
  public class ChatAnchorTests
  {
    // 1..3 live, 4 retired, everything else never issued.
    private static IdState State(int id) =>
      id >= 1 && id <= 3 ? IdState.Alive : id == 4 ? IdState.Retired : IdState.Unknown;

    // ---- #322: the parenthesised form ONLY --------------------------------------------------

    [Fact]
    public void Bare_numbers_in_prose_are_never_anchors()
    {
      // The reason #322 exists: on a few-hundred-component definition most small integers ARE issued ids,
      // so linkifying bare digits would turn every measurement into navigation to an unrelated component.
      var prose = "Lay them out as a 4 by 3 grid, 10 units apart, over 2 polygons.";
      Assert.Empty(ChatAnchors.Extract(prose, State));
      Assert.Equal(prose, ChatAnchors.Linkify(prose, State));
    }

    [Fact]
    public void The_parenthesised_form_is_an_anchor()
    {
      Assert.Equal(new[] { 1, 2 }, ChatAnchors.Extract("The Loft (1) feeds the Cull (2).", State));
    }

    [Fact]
    public void A_number_that_was_never_issued_is_not_clickable()
    {
      // An affordance is an assertion that its target exists.
      var html = ChatAnchors.Linkify("See the widget (9999) there.", State);
      Assert.DoesNotContain("dwightSend", html);
      Assert.Contains("(9999)", html);
      Assert.Empty(ChatAnchors.Extract("See the widget (9999) there.", State));
    }

    [Fact]
    public void A_retired_id_stays_clickable_so_the_click_can_explain_itself()
    {
      // Deliberate: it WAS a real component and the message is about it. Silently un-linking it would
      // rewrite history; the honest answer belongs at the click, not at the render.
      Assert.Equal(new[] { 4 }, ChatAnchors.Extract("The Sift (4) was the problem.", State));
      Assert.Contains("anchor:4", ChatAnchors.Linkify("The Sift (4) was the problem.", State));
    }

    [Fact]
    public void Ids_are_de_duplicated_in_the_order_written()
    {
      Assert.Equal(new[] { 2, 1 }, ChatAnchors.Extract("(2) then (1) then (2) again.", State));
    }

    // ---- Linkify does not corrupt the html it runs over --------------------------------------

    [Fact]
    public void Numbers_inside_tags_are_left_alone()
    {
      var html = "<div data-x=\"(1)\">the Loft (1)</div>";
      var outp = ChatAnchors.Linkify(html, State);
      Assert.Contains("data-x=\"(1)\"", outp);          // the attribute is untouched
      Assert.Contains("anchor:1", outp);                 // the prose is linked
    }

    [Fact]
    public void Numbers_inside_code_and_pre_are_left_alone()
    {
      Assert.DoesNotContain("dwightSend", ChatAnchors.Linkify("<p><code>arr(1)</code></p>", State));
      Assert.DoesNotContain("dwightSend", ChatAnchors.Linkify("<pre>f(2)\ng(3)</pre>", State));
    }

    [Fact]
    public void Code_ends_and_prose_after_it_is_linked_again()
    {
      var outp = ChatAnchors.Linkify("<code>f(1)</code> then the Cull (2).", State);
      Assert.DoesNotContain("anchor:1", outp);
      Assert.Contains("anchor:2", outp);
    }

    [Fact]
    public void Empty_and_malformed_input_are_safe()
    {
      Assert.Equal("", ChatAnchors.Linkify("", State));
      Assert.Equal("()", ChatAnchors.Linkify("()", State));
      Assert.Equal("(x)", ChatAnchors.Linkify("(x)", State));
      Assert.Contains("<b", ChatAnchors.Linkify("<b unclosed", State));
    }

    // ---- Bar 2: honest across time -----------------------------------------------------------

    [Fact]
    public void All_alive_lands_with_nothing_to_say()
    {
      var r = ChatAnchors.Resolve(new[] { 1, 2 }, State);
      Assert.Equal(AnchorOutcome.Landed, r.Outcome);
      Assert.Equal(new[] { 1, 2 }, r.Alive);
      Assert.Equal("", r.Message);
    }

    [Fact]
    public void All_gone_says_so_and_offers_nowhere_to_go()
    {
      var r = ChatAnchors.Resolve(new[] { 4 }, State);
      Assert.Equal(AnchorOutcome.AllRetired, r.Outcome);
      Assert.Empty(r.Alive);                                   // nothing to frame - so nothing moves
      Assert.Equal("That component is no longer on the canvas.", r.Message);
    }

    [Fact]
    public void An_id_that_never_existed_resolves_the_same_way_as_a_retired_one()
    {
      // Both mean "there is nowhere to go". Neither may fall through to a position.
      var r = ChatAnchors.Resolve(new[] { 9999 }, State);
      Assert.Equal(AnchorOutcome.AllRetired, r.Outcome);
      Assert.Empty(r.Alive);
    }

    [Fact]
    public void A_partial_loss_shows_the_survivors_AND_states_the_loss()
    {
      var r = ChatAnchors.Resolve(new[] { 1, 4, 2 }, State);
      Assert.Equal(AnchorOutcome.PartlyRetired, r.Outcome);
      Assert.Equal(new[] { 1, 2 }, r.Alive);
      Assert.Contains("no longer on the canvas", r.Message);
      Assert.Contains("showing the rest", r.Message);
    }

    [Fact]
    public void Several_losses_are_counted_rather_than_generalised()
    {
      var r = ChatAnchors.Resolve(new[] { 1, 4, 9998, 9999 }, State);
      Assert.Equal(AnchorOutcome.PartlyRetired, r.Outcome);
      Assert.StartsWith("3 of those components", r.Message);
    }

    [Fact]
    public void Nothing_named_is_not_an_error()
    {
      var r = ChatAnchors.Resolve(new int[0], State);
      Assert.Equal(AnchorOutcome.Nothing, r.Outcome);
      Assert.Equal("", r.Message);
    }

    [Fact]
    public void A_message_from_an_hour_ago_resolves_against_the_canvas_as_it_is_NOW()
    {
      // The same stored anchors, read twice, either side of a deletion. This is the whole point of storing
      // ids rather than anything positional: staleness becomes a refusal, not a wrong destination.
      var anchors = ChatAnchors.Extract("The Loft (1) and the Sift (4).", State);

      IdState Before(int id) => IdState.Alive;
      IdState After(int id) => id == 1 ? IdState.Alive : IdState.Retired;

      Assert.Equal(AnchorOutcome.Landed, ChatAnchors.Resolve(anchors, Before).Outcome);
      Assert.Equal(AnchorOutcome.PartlyRetired, ChatAnchors.Resolve(anchors, After).Outcome);
    }
    // ---- panel graduation's context line (#365) ---------------------------------------------------
    // Brief EC unit 5b moved the line to the model door, in Markdown; its ids reach the screen through Linkify like the rest of Dwight's words. Its tests are RunHandOffTests.

    /// <summary>#328: the anchor convention has exactly ONE home: the seed's "(5096)" becomes the same anchor as any prose's, through Linkify.</summary>
    [Fact]
    public void The_seeds_ids_become_the_SAME_anchor_form_as_linkified_prose()
    {
      var line = RunHandOff.ContextLine("Explain", new DateTime(2026, 11, 2, 14, 2, 0), new[] { Guid.NewGuid() }, _ => "Loft", _ => 42);
      Assert.Contains("dwightSend('anchor:42')", ChatAnchors.Linkify(line, _ => IdState.Alive));
      Assert.Contains("dwightSend('anchor:42')", ChatAnchors.Linkify("the Loft component (42)", _ => IdState.Alive));
    }
  }
}
