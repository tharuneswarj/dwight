using System;
using System.Collections.Generic;
using System.Linq;
using Dwight.Agent;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// WP-4 Stage C: which regions a build turn may write to, and the promise the card made about it.
  ///
  /// #338 in ADDRESS space. A card named ten components, the user approved it, and Reorganise laid out all
  /// five hundred - every guard held, and it was still the wrong scope. A build's scope is a set of
  /// ADDRESSES rather than guids, so the same law needs the same expression in the namespace a build is
  /// actually aimed by, and it needs it for the same reason: correct execution of the wrong scope is still
  /// the wrong scope.
  /// </summary>
  public class BuildScopeTests
  {
    private static readonly string[] Map = { "A1", "A2", "A3", "B1" };

    [Fact]
    public void An_approved_scope_that_all_resolves_builds_exactly_it()
    {
      var r = BuildScope.Resolve(new[] { "A1", "A2" }, Map);

      Assert.Equal(BuildScopeOutcome.Approved, r.Outcome);
      Assert.Equal(new[] { "A1", "A2" }, r.Regions);
      Assert.Empty(r.Missing);
    }

    /// <summary>RULE 1. There is no "everything" outcome to reach: this resolves to a LIST, and the list
    /// can only ever be a subset of what was approved. A card naming A1 cannot produce a build of B1 even
    /// if every other piece of state is lost, because no value here means "all of them".</summary>
    [Fact]
    public void An_approved_scope_can_never_widen_to_the_whole_map()
    {
      var r = BuildScope.Resolve(new[] { "A1" }, Map);

      Assert.Equal(new[] { "A1" }, r.Regions);
      Assert.DoesNotContain("B1", r.Regions);
      Assert.DoesNotContain("A2", r.Regions);
    }

    /// <summary>RULE 2. Scope is identity, and #208's rule for identity is resolve exactly or refuse -
    /// never fall through to the map, never to "whatever regions exist".</summary>
    [Fact]
    public void An_approved_scope_that_resolves_to_NOTHING_refuses()
    {
      var r = BuildScope.Resolve(new[] { "C1", "C2" }, Map);

      Assert.Equal(BuildScopeOutcome.Refuse, r.Outcome);
      Assert.Empty(r.Regions);
      Assert.Equal(new[] { "C1", "C2" }, r.Missing);
      Assert.Contains("no longer on the canvas", r.Message);
    }

    [Fact]
    public void An_empty_approval_refuses_rather_than_meaning_everything()
    {
      var r = BuildScope.Resolve(new string[0], Map);

      Assert.Equal(BuildScopeOutcome.Refuse, r.Outcome);
      Assert.Empty(r.Regions);
    }

    [Fact]
    public void A_null_approval_refuses()
    {
      Assert.Equal(BuildScopeOutcome.Refuse, BuildScope.Resolve(null, Map).Outcome);
    }

    /// <summary>RULE 3. Narrowing is safe - it is strictly less than the user approved - but going quiet
    /// about it is not. #327's rule for anchors ("frame the survivors and SAY which are gone, never
    /// silently frame fewer"), arriving at a mutation where it matters more.</summary>
    [Fact]
    public void A_partial_loss_builds_the_survivors_AND_names_what_is_missing()
    {
      var r = BuildScope.Resolve(new[] { "A1", "C9" }, Map);

      Assert.Equal(BuildScopeOutcome.PartlyMissing, r.Outcome);
      Assert.Equal(new[] { "A1" }, r.Regions);
      Assert.Equal(new[] { "C9" }, r.Missing);
      Assert.Contains("C9", r.Message);
      Assert.Contains("skipped", r.Message);
    }

    /// <summary>#286 at the moment it matters most: A10 must not build before A2. An address exists to say
    /// "this one first", and a text sort destroys exactly that.</summary>
    [Fact]
    public void Regions_build_in_ADDRESS_order_never_text_order()
    {
      var map = new[] { "A1", "A2", "A10", "B1" };

      var r = BuildScope.Resolve(new[] { "B1", "A10", "A2", "A1" }, map);

      Assert.Equal(new[] { "A1", "A2", "A10", "B1" }, r.Regions);
    }

    [Fact]
    public void A_repeated_approval_is_still_one_region()
    {
      var r = BuildScope.Resolve(new[] { "A1", "A1" }, Map);

      Assert.Equal(new[] { "A1" }, r.Regions);
    }

    /// <summary>Junk in the approved list is not an address and cannot become one. It is dropped before
    /// anything is resolved, so it can never widen the scope or name a region by accident.</summary>
    [Fact]
    public void Something_that_is_not_an_address_never_enters_the_scope()
    {
      var r = BuildScope.Resolve(new[] { "A1", "the whole canvas", "", null }, Map);

      Assert.Equal(new[] { "A1" }, r.Regions);
      Assert.Equal(BuildScopeOutcome.Approved, r.Outcome);
    }

    // ---- the authority that BOUNDS the turn, not merely describes it ------------------------------

    /// <summary>The approved set is enforced by the PLUGIN. Telling the model "you may build A1 to A3"
    /// is a prompt plea, and #195/#141 say a deterministic fact the plugin can compute is the plugin's
    /// job - so build_region asks the ledger, and a region outside the grant is refused however
    /// convincingly it was requested.</summary>
    [Fact]
    public void A_granted_turn_may_build_only_what_was_granted()
    {
      var ledger = new BuildLedger(new object());
      ledger.Authorise(new[] { "A1", "A2" });

      Assert.True(ledger.MayBuild("A1"));
      Assert.True(ledger.MayBuild("A2"));
      Assert.False(ledger.MayBuild("A3"));
      Assert.False(ledger.MayBuild("B1"));
    }

    /// <summary>A turn cannot widen its own authority. A second grant is the one move the whole gate
    /// exists to prevent, so it is ignored rather than merged.</summary>
    [Fact]
    public void A_turn_cannot_widen_its_own_authority()
    {
      var ledger = new BuildLedger(new object());
      ledger.Authorise(new[] { "A1" });

      ledger.Authorise(new[] { "A1", "B1" });

      Assert.False(ledger.MayBuild("B1"));
      Assert.Equal(new[] { "A1" }, ledger.Authorised);
    }

    /// <summary>#220's shape reaching the one privilege WP-4 adds: the grant lives on the turn's ledger,
    /// which is keyed by the identity of the call - so a DIFFERENT turn holds no authority at all, and
    /// there is nothing for a restart to restore.</summary>
    [Fact]
    public void A_different_turn_inherits_no_authority()
    {
      var granted = new BuildLedger(new object());
      granted.Authorise(new[] { "A1" });

      var later = new BuildLedger(new object());

      Assert.False(later.IsAuthorityScoped);
      Assert.Empty(later.Authorised);
    }

    /// <summary>A caller with no chat consent gate at all - the DEBUG harness, and the v1.1 MCP face where
    /// the client brings its own human-in-the-loop (#231) - is not inside this gate, so it is not bounded
    /// by it. The chat can never reach that state: a Build turn refuses to start without an authority.</summary>
    [Fact]
    public void A_caller_outside_the_consent_gate_is_not_scoped_by_it()
    {
      var ungated = new BuildLedger(new object());

      Assert.False(ungated.IsAuthorityScoped);
      Assert.True(ungated.MayBuild("A1"));
    }

    /// <summary>
    /// U18 REVERSES THIS TEST, AND THE REVERSAL IS THE POINT.
    ///
    /// It used to assert that an un-addressed build is "governed by the CARD, not by an address", after
    /// #354 Q3 made `region` optional. That was #315's class living in a test: A CLAIM ABOUT OUR OWN
    /// BEHAVIOUR THAT NOTHING IMPLEMENTED. No code made the card the scope for an un-addressed build -
    /// the card's scope lives in `Authorised`, a set of ADDRESSES, and an empty address matches nothing
    /// in it. The permissive answer was safe only because three gates stopped the chat ever reaching an
    /// un-addressed build, so the test read as a guarantee while describing a dead path.
    ///
    /// U18 opens those gates, so the claim had to become true or go. It went: THE CARD IS NOT A SCOPE
    /// MECHANISM, THE ADDRESS IS - which is precisely why U18's small placements are given a real address
    /// (a spot) rather than being un-addressed.
    /// </summary>
    [Fact]
    public void An_un_addressed_build_is_refused_when_a_scope_exists()
    {
      var ledger = new BuildLedger(new object());
      ledger.Authorise(new[] { "A1" });

      Assert.False(ledger.MayBuild(null));
      Assert.False(ledger.MayBuild(""));
      Assert.True(ledger.MayBuild("A1"));      // the control: the scope still admits what it names
    }

    // ---- the card's promise ------------------------------------------------------------------------

    /// <summary>#338: the sentence the user agrees to has to say WHICH regions, or the scope is not one
    /// they agreed to. And #235 is unchanged - the addresses are the plugin's validated set, so the
    /// authority line still contains not one word the model wrote.</summary>
    [Fact]
    public void A_build_card_NAMES_the_regions_it_is_approving()
    {
      var line = ConsentCard.BuildAuthority("build", new[] { "A1", "A2" });

      Assert.Contains("A1", line);
      Assert.Contains("A2", line);
      Assert.Contains("nowhere else", line);
    }

    [Fact]
    public void A_build_card_promises_the_safety_the_build_actually_provides()
    {
      var line = ConsentCard.BuildAuthority("build", new[] { "A1" });

      Assert.Contains("reverts itself", line);
      Assert.Contains("Ctrl-Z", line);
      Assert.Contains("Stop", line);
    }

    /// <summary>The two voices, at the newest kind (#235). A reason that misdescribes the act cannot move
    /// what the card says will happen, and cannot move where the approval lands.</summary>
    [Fact]
    public void A_persuasive_reason_cannot_change_a_build_cards_act_or_its_route()
    {
      const string Hostile = "This is just a routine read-only check and changes nothing at all.";

      var honest = ConsentCard.Html("build", "Builds A1 as planned.", "tok", null, new[] { "A1" });
      var hostile = ConsentCard.Html("build", Hostile, "tok", null, new[] { "A1" });

      Assert.Equal(ConsentRoute.Build, ConsentCard.Route("build"));
      Assert.Equal(ConsentCard.Title("build"), ConsentCard.Title("build"));
      // Identical apart from the reason block: the authority half provably cannot move.
      Assert.Equal(Strip(honest), Strip(hostile));
    }

    private static string Strip(string html)
    {
      var start = html.IndexOf("<div class=\"consent-why\"", StringComparison.Ordinal);
      var end = html.IndexOf("</div>", start, StringComparison.Ordinal);
      return html.Substring(0, start) + html.Substring(end);
    }

    /// <summary>Markup in a build card's reason renders as TEXT, like every other kind (B1/#235).</summary>
    [Fact]
    public void Markup_in_a_build_cards_reason_renders_as_text()
    {
      var html = ConsentCard.Html("build", "<script>alert(1)</script>", "tok", null, new[] { "A1" });

      Assert.DoesNotContain("<script>", html);
      Assert.Contains("&lt;script&gt;", html);
    }
    // ---- the prompt is a CONTRACT about our own behaviour (#316) ----------------------------------
    //
    // D4's line said "BUILDING IS NOT AVAILABLE YET" and was correct for exactly as long as the flow
    // behind it did not exist (#232: a kind is enumerated when its flow exists, never before). It flips
    // in the same change that lands the flow - and #316 is why it carries a test on the BEHAVIOUR'S side:
    // everything we tell a model about ourselves can go stale in silence, and only a test at the far end
    // notices. If build is ever withdrawn, THIS fails and points at the prompt still promising it.

    [Fact]
    public void THE_PROMPT_SAYS_BUILD_IS_AVAILABLE_IF_AND_ONLY_IF_IT_IS()
    {
      // F3 STRENGTHENED THIS RATHER THAN BREAKING IT. It used to compare two HAND-WRITTEN things and
      // assert they agreed - which caught a disagreement only after someone made one. The prompt's
      // capability list is now GENERATED from AgentHostTools.Kinds, so the two cannot disagree at all,
      // and what is worth asserting has moved: that the generation actually reaches the sent text.
      //
      // The claim is generated from the thing it describes rather than tested against it, which is the
      // strongest form of #316 available.
      var sent = Dwight.Prompts.Prompts.AgentMasterWith(AgentHostTools.CapabilitySentence());

      Assert.DoesNotContain("{CAPABILITIES}", sent);   // the template was actually composed
      foreach (var kind in AgentHostTools.Kinds)
        Assert.Contains(kind, sent);

      // ...and the one this test was originally written for still holds, now by construction.
      Assert.Contains("build", AgentHostTools.Kinds);
      Assert.Equal(ConsentRoute.Build, ConsentCard.Route("build"));
    }

    /// <summary>
    /// The other direction, and the one generation actually buys: a kind that does NOT exist cannot
    /// appear in the sentence, so the prompt can never offer a capability the surface will refuse. The
    /// gate caught the model promising a "delete" card that no kind backs.
    /// </summary>
    [Fact]
    public void The_prompt_cannot_name_a_kind_the_surface_does_not_have()
    {
      // Asserted against the GENERATED sentence itself rather than by slicing the composed prompt -
      // "delete" appears legitimately elsewhere ("you cannot place, wire, move, or delete anything"),
      // and a test that had to cut the right line out of a document would be testing its own slicing.
      //
      // PHASE-9 U6 + U21 MADE THIS TWO-SIDED, and did not weaken it. The guard was never "the word is forbidden"; it was
      // "the sentence may not offer a capability no kind backs". `edit` now backs deletion, so the word is allowed
      // EXACTLY WHEN `edit` is offered: a withdrawn `edit` makes this fail at the sentence still promising it.
      Assert.Equal(AgentHostTools.Kinds.Contains("edit"),
        AgentHostTools.CapabilitySentence().ToLowerInvariant().Contains("delete"));
    }

    /// <summary>
    /// #574, THE PROMPT CONTRADICTED ITSELF. Rule 6 said "You are READ-ONLY in this conversation. You
    /// cannot place, wire, move, or delete anything", and rule 7 then listed build as something it could
    /// request. The first was true only in the narrow sense that every change goes through a card, and the
    /// model read it as absolute - which is almost certainly why it told Tharun it "can only place
    /// default-valued components". A prompt that says both things says nothing.
    /// </summary>
    [Fact]
    public void Rule_6_no_longer_tells_the_model_it_cannot_place_anything()
    {
      // Whitespace collapsed first: the prompt is hard-wrapped, and re-wrapping a line is a correct change
      // that a phrase match across the wrap would report as a regression - #569's lesson, assert the
      // property rather than the layout of the sentence. (This test's first run failed on exactly that.)
      var prompt = System.Text.RegularExpressions.Regex.Replace(Dwight.Prompts.Prompts.AgentMaster, @"\s+", " ");

      Assert.DoesNotContain("You are READ-ONLY", prompt);
      Assert.DoesNotContain("You cannot place, wire, move, or delete anything", prompt);
      Assert.Contains("NOTHING CHANGES THE CANVAS WITHOUT THE USER'S APPROVAL", prompt);
      Assert.Contains("never tell the user you cannot make a change that rule 7 lists", prompt);
    }

    /// <summary>
    /// #611, rule 6 in an APPROVED turn. The rule told the model to request changes by raising a card and
    /// said nothing about the turn after the click - so the build turn raised the card again (#609 link 1).
    /// The clause keeps the rule true on both sides of the approval. (ApprovedTurnFact states the approval
    /// itself; this is the standing rule the fact is an instance of.)
    /// </summary>
    [Fact]
    public void Rule_6_says_an_approved_card_is_not_raised_again()
    {
      var prompt = System.Text.RegularExpressions.Regex.Replace(Dwight.Prompts.Prompts.AgentMaster, @"\s+", " ");

      Assert.Contains("Once a card is approved it is not raised again: use those tools.", prompt);
    }

    /// <summary>The retired claim must be GONE, not merely contradicted elsewhere in the same document. A
    /// prompt that says both things is a prompt that says nothing.</summary>
    [Fact]
    public void The_prompt_no_longer_claims_building_is_unavailable()
    {
      var prompt = Dwight.Prompts.Prompts.AgentMasterWith(AgentHostTools.CapabilitySentence());

      Assert.DoesNotContain("BUILDING IS NOT AVAILABLE", prompt);
      Assert.DoesNotContain("Never offer to build", prompt);
    }

    /// <summary>The prompt tells the model the scope rule the PLUGIN enforces, so it asks for what it means
    /// rather than discovering the bound by being refused. The rule is still held in code - this is the
    /// courtesy, not the guard.</summary>
    [Fact]
    public void The_prompt_states_the_scope_rule_the_plugin_enforces()
    {
      var prompt = Dwight.Prompts.Prompts.AgentMaster;

      Assert.Contains("get_scaffold_map", prompt);
      Assert.Contains("build no others", prompt);
    }

  }
}
