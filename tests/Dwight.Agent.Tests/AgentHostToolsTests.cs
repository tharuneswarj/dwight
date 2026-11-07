using System;
using System.Linq;
using System.Text.Json.Nodes;
using Dwight.Agent;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// The HOST tool set (#230 Fork 1) - what you can do IN THIS SURFACE, as opposed to the catalogue,
  /// which is what you can do TO THE CANVAS and IS the MCP list_tools response.
  ///
  /// The invariant these tests exist for is the sharpening that came with the ruling: a host tool may
  /// NEVER be a write path. That is held two ways here - declared costs, and name separation - and a
  /// third way in the loop, which hands the host handler no scope at all.
  /// </summary>
  public class AgentHostToolsTests
  {
    [Fact]
    public void No_host_tool_declares_that_it_mutates_or_solves()
    {
      // Host tools are UI-only by construction. Anything here that needed to touch the canvas would
      // belong in the catalogue instead, where the verdict law applies (ADR-098).
      Assert.NotEmpty(AgentHostTools.All());
      Assert.All(AgentHostTools.All(), t =>
      {
        Assert.False(t.Mutates, t.Name + " is a host tool and must not mutate");
        Assert.False(t.Solves, t.Name + " is a host tool and must not solve");
      });
    }

    [Fact]
    public void The_host_set_and_the_catalogue_do_not_overlap()
    {
      // A name in both would make "which source is this from" ambiguous at dispatch - and dispatch is
      // exactly where the write path is decided.
      var catalogue = AgentToolCatalog.All().Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
      Assert.All(AgentHostTools.All(), t => Assert.DoesNotContain(t.Name, catalogue));
    }

    [Fact]
    public void The_canvas_contract_does_not_advertise_a_consent_tool()
    {
      // The catalogue IS the MCP surface. A card-rendering tool in it would export Dwight's consent model
      // as part of the canvas contract and reach v1.1 as a broken tool.
      Assert.False(AgentToolCatalog.Knows(AgentHostTools.RequestMutation));
      Assert.True(AgentHostTools.Knows(AgentHostTools.RequestMutation));
    }

    [Fact]
    public void Asking_for_permission_is_available_at_every_capability()
    {
      // A read-only turn that cannot ask has no way out of read-only, so this is the one tool every turn
      // holds. It is enumerated rather than waved through - the host set is grant-checked like the
      // catalogue.
      foreach (var capability in (TurnCapability[])Enum.GetValues(typeof(TurnCapability)))
      {
        Assert.True(AgentLoopCore.IsGranted(capability, AgentHostTools.RequestMutation));
        Assert.Contains(AgentLoopCore.ToolsFor(capability), t => t.Name == AgentHostTools.RequestMutation);
      }
    }

    [Fact]
    public void Every_host_tool_is_granted_somewhere()
    {
      // The catalogue's conformance rule (A1) applied to the second source, so a host tool added later is
      // granted nowhere until somebody decides - the same guard, both sources.
      var capabilities = (TurnCapability[])Enum.GetValues(typeof(TurnCapability));
      Assert.All(AgentHostTools.All(),
        t => Assert.True(capabilities.Any(c => AgentLoopCore.IsGranted(c, t.Name)), t.Name + " is granted nowhere"));
    }

    // ---- request_mutation's own contract -----------------------------------------------------------

    private static JsonObject Args(string kind, string why)
    {
      var o = new JsonObject();
      if (kind != null) o["kind"] = kind;
      if (why != null) o["why"] = why;
      return o;
    }

    [Theory]
    [InlineData("reorganise")]
    [InlineData("simplify")]
    [InlineData("solve")]
    [InlineData("scaffold")]
    public void The_kinds_this_build_can_honour_are_accepted(string kind)
    {
      Assert.True(AgentHostTools.TryParseRequest(Args(kind, "it would help"), out var parsed, out var why, out var error));
      Assert.Equal(kind, parsed);
      Assert.Equal("it would help", why);
      Assert.Null(error);
    }

    [Fact]
    public void Scaffold_graduated_when_the_flow_behind_it_existed()
    {
      // REWRITTEN at Stage C. Through Stage B this asserted that "scaffold" was REFUSED, because
      // propose_scaffold did not exist and a card whose Approve leads nowhere is #174's dead-button class.
      // Stage C built the flow, so the enum grew - and the rule it was protecting is unchanged and is
      // asserted below: a kind is offerable only when something can honour it.
      Assert.True(AgentHostTools.TryParseRequest(Args("scaffold", "let me map it out"), out var kind, out _, out _));
      Assert.Equal("scaffold", kind);
      Assert.NotEqual(ConsentRoute.Unknown, ConsentCard.Route("scaffold"));
    }

    [Fact]
    public void A_kind_this_build_cannot_honour_is_refused_by_name_with_the_real_list()
    {
      // The durable form of the rule the scaffold case used to carry: refused, named, and told what IS
      // available (#208's shape with ADR-092's repair payload), never silently ignored and never a
      // rendered dead card.
      Assert.False(AgentHostTools.TryParseRequest(Args("rewire_everything", "trust me"), out _, out _, out var error));

      var json = error.ToJson();
      Assert.Contains("rewire_everything", json);   // says what was asked for
      Assert.Contains("reorganise", json);          // and what it could have asked for instead
      Assert.Contains("simplify", json);
      Assert.Contains("solve", json);
      Assert.Contains("scaffold", json);
    }

    [Fact]
    public void An_unknown_kind_is_refused_the_same_way()
    {
      Assert.False(AgentHostTools.TryParseRequest(Args("delete_everything", "trust me"), out _, out _, out var error));
      Assert.Contains("available", error.ToJson());
    }

    [Fact]
    public void A_request_with_no_reason_is_refused()
    {
      // The user is being asked to approve something; a card with no reason on it is a card they cannot
      // make a decision from.
      Assert.False(AgentHostTools.TryParseRequest(Args("solve", null), out _, out _, out var error));
      Assert.False(AgentHostTools.TryParseRequest(Args("solve", "   "), out _, out _, out _));
      Assert.NotNull(error);
    }

    [Fact]
    public void The_published_enum_and_the_parser_agree()
    {
      // The #219 lesson in a tool on its first day: the published descriptor IS the contract, so it is
      // what the parser gets tested against. Every kind the schema advertises must parse.
      var schema = AgentHostTools.All().First(t => t.Name == AgentHostTools.RequestMutation).InputSchemaJson;
      foreach (var kind in AgentHostTools.Kinds)
      {
        Assert.Contains("\"" + kind + "\"", schema);
        Assert.True(AgentHostTools.TryParseRequest(Args(kind, "because"), out _, out _, out _));
      }
    }

    // ---- the solve capability (#222's generalisation) -----------------------------------------------

    [Fact]
    public void An_approved_solve_grants_solving_and_nothing_that_writes()
    {
      // Consent-to-WORK, not consent-to-mutate: the click that allows a re-solve must not also allow a
      // build. One mechanism, two uses, and the grant stays exact.
      Assert.True(AgentLoopCore.IsGranted(TurnCapability.ReadAndSolve, AgentToolCatalog.SolveAndCheck));
      Assert.True(AgentLoopCore.IsGranted(TurnCapability.ReadAndSolve, AgentToolCatalog.GetOverview));
      Assert.False(AgentLoopCore.IsGranted(TurnCapability.ReadAndSolve, AgentToolCatalog.BuildRegion));
    }

    [Fact]
    public void An_approved_scaffold_grants_the_map_and_no_way_to_build_it()
    {
      // The map IS the point (ADR-054), and building it is a separate consent that belongs to WP-4.
      // Approving a plan must not quietly approve its execution - that would collapse two decisions the
      // user is entitled to make separately.
      Assert.True(AgentLoopCore.IsGranted(TurnCapability.Scaffold, AgentToolCatalog.ProposeScaffold));
      Assert.True(AgentLoopCore.IsGranted(TurnCapability.Scaffold, AgentToolCatalog.GetOverview));
      Assert.False(AgentLoopCore.IsGranted(TurnCapability.Scaffold, AgentToolCatalog.BuildRegion));
      Assert.False(AgentLoopCore.IsGranted(TurnCapability.Scaffold, AgentToolCatalog.SolveAndCheck));

      // And the converse: a read-only turn cannot draw a map either. Drawing on the canvas is a change.
      Assert.False(AgentLoopCore.IsGranted(TurnCapability.ReadOnly, AgentToolCatalog.ProposeScaffold));
    }

    // ---- #378a: the descriptor tells the truth about what a kind DOES -------------------------------

    /// <summary>
    /// THE FIELD FAILURE BEHIND THIS. The model needed a change no kind offers, picked the nearest one -
    /// "solve" - and used it as a proxy for making that change. The old description ("use solve when you
    /// need Grasshopper to recompute before you can answer") was TRUE and still permitted the reading,
    /// because it said what solve is FOR without ever saying what it does NOT do.
    /// </summary>
    [Fact]
    public void The_solve_kind_says_that_it_changes_nothing()
    {
      // F6: the scopes now come from ONE table, so the assertion is that the PUBLISHED schema actually
      // carries it - which is the drift this could still have - plus the meaning itself.
      Assert.Contains("CHANGES NOTHING", AgentHostTools.KindScope["solve"].Cannot);
      Assert.Contains(AgentHostTools.ScopeOf("solve"), Descriptor().InputSchemaJson);
      Assert.Contains("never be a way to apply a change", Descriptor().Description);
    }

    /// <summary>
    /// The other half, and the one that gives the model somewhere to GO. A model with a need and no
    /// matching kind has to do something; told only what it may not do, it will still pick the nearest
    /// kind. Naming the missing capability is a move it can actually make.
    /// </summary>
    [Fact]
    public void The_descriptor_tells_the_model_what_to_do_when_no_kind_fits()
    {
      var d = Descriptor().Description;
      Assert.Contains("DO NOT PICK THE NEAREST ONE", d);
      Assert.Contains("NAME the capability that is missing", d);
    }

    /// <summary>
    /// A PROMPT MAY NOT COACH THE MODEL TO PROMISE THE PRODUCT'S FUTURE (#315: a prompt claim about our own
    /// behaviour carries a test on the behaviour's side). The descriptor used to say "what Dwight cannot do
    /// yet", inviting the model to tell a user a capability is coming - which no user-visible text may say, so
    /// no prompt may coach it either. What Dwight cannot do is stated as a present fact.
    /// </summary>
    [Fact]
    public void The_descriptor_states_what_dwight_cannot_do_as_a_fact_not_a_future()
    {
      var d = Descriptor().Description;
      Assert.Contains("Being told plainly what Dwight cannot do is useful to the user", d);
      Assert.DoesNotContain("cannot do yet", d);
    }

    /// <summary>
    /// #316's shape: the published enum and the prose that explains it are one contract, so a kind added
    /// to <see cref="AgentHostTools.Kinds"/> without a word about what it does fails HERE, rather than in
    /// the field as a model guessing at it.
    /// </summary>
    [Fact]
    public void Every_offered_kind_is_actually_described_to_the_model()
    {
      var schema = Descriptor().InputSchemaJson;
      foreach (var kind in AgentHostTools.Kinds)
        Assert.Contains(kind + " =", schema);
    }

    // ---- #574/#577: every kind says what it CAN and CANNOT do ---------------------------------------

    /// <summary>
    /// THE STRUCTURAL TEST, AND THE REASON THE TABLE CHANGED SHAPE. F6 wrote only the "does NOT" half of
    /// each kind on purpose, and it produced three more instances of the class it was fixing: a region
    /// replacement routed to Simplify, the agent denying it could set initial values minutes after doing
    /// it, and a claim that simplify "explicitly does not write scripts". A DESCRIPTION THAT STATES ONLY
    /// WHAT A KIND DOES NOT DO INVITES THE MODEL TO INVENT WHAT IT DOES.
    ///
    /// This fails if EITHER half is empty for ANY kind, so writing one half is not something a later edit
    /// can do by accident.
    /// </summary>
    [Fact]
    public void Every_kind_states_BOTH_what_it_can_and_what_it_cannot_do()
    {
      foreach (var kind in AgentHostTools.Kinds)
      {
        var c = AgentHostTools.KindScope[kind];
        Assert.False(string.IsNullOrWhiteSpace(c.Can), kind + " has no CAN half");
        Assert.False(string.IsNullOrWhiteSpace(c.Cannot), kind + " has no CANNOT half");
      }
    }

    /// <summary>The CANNOT halves still carry the misreadings F6 was written against.</summary>
    [Theory]
    [InlineData("simplify", "set values, domains, or rounding")]
    [InlineData("reorganise", "change logic")]
    [InlineData("solve", "CHANGES NOTHING")]
    [InlineData("scaffold", "EMPTY")]
    [InlineData("build", "did not place")]
    public void Each_kind_still_says_what_it_does_not_do(string kind, string disclaimer)
    {
      Assert.Contains(disclaimer, AgentHostTools.KindScope[kind].Cannot);
    }

    /// <summary>
    /// #574, BOTH MISSES FROM ONE ENTRY. build's CAN half names initial values - so the agent cannot deny
    /// placing sliders at 300 and 400 - and names rebuilding as the way a region's contents are replaced -
    /// so a replacement request matches build rather than simplify's "logic".
    /// </summary>
    [Fact]
    public void Build_says_it_sets_initial_values_and_can_rebuild_a_region()
    {
      var can = AgentHostTools.KindScope["build"].Can;

      Assert.Contains("INITIAL VALUES", can);
      Assert.Contains("Number Slider", can);
      Assert.Contains("Panel", can);
      Assert.Contains("REBUILD", can);
      Assert.Contains("names each one on the card", can);
      Assert.Contains("replaced with different logic", can);
    }

    /// <summary>
    /// #628/#633, TODAY'S TRUTH ABOUT WIRING, FROM THE RULE AND NOT THE SYMPTOM. The model promised on a build
    /// card to "wire A1's point grid into B1's XY Plane" - two components that already existed - and
    /// preservation reverted it. The sentence first ruled ("cannot connect a wire into a component that already
    /// exists") was false the other way: BuildPreservation's rule 3 ACCEPTS a new source appended to an existing
    /// input when the source is a component that same build places. Stating it would have told the model not to
    /// make the one reconnection that works. So the CANNOT half says exactly what rule 3 rejects.
    /// </summary>
    [Fact]
    public void Build_cannot_states_the_wiring_rule_preservation_enforces()
    {
      var cannot = AgentHostTools.KindScope["build"].Cannot;

      Assert.Contains("connect two components that already exist to each other", cannot);
      Assert.Contains("remove, reorder or replace any wire that already exists", cannot);
      Assert.DoesNotContain("into a component that already exists", cannot);
      Assert.Contains(cannot, AgentHostTools.CapabilitySentence());
    }

    /// <summary>
    /// #643/#644, TRUE FOR WHAT THIS COMMIT MAKES TRUE. #628's sentence said a new wire into an existing component
    /// "must come from a component that same build places" - and preservation compared sources with component
    /// guids, so a new COMPONENT's output reverted (Finding B). With sources judged by output param, anything the
    /// build places may feed an existing component. And a rebuild still cannot cut a wire into what it keeps.
    /// </summary>
    [Fact]
    public void Build_cannot_says_component_outputs_may_feed_existing_components_and_a_rebuild_cannot_cut()
    {
      var cannot = AgentHostTools.KindScope["build"].Cannot;

      Assert.Contains("a new wire into an existing component may come from anything that same build places, " +
                      "component outputs included", cannot);
      Assert.Contains("a rebuild cannot cut a wire from the components it removes into anything outside what it removes",
                      cannot);
      Assert.DoesNotContain("must come from a component", cannot);
    }

    /// <summary>#625/#647: the CANNOT half says a rebuild reconnects what it would cut, in place, and that a wire
    /// nothing new should feed is the user's to delete first - so a planning turn neither promises a deletion build
    /// cannot make nor tells the user a rebuild is impossible.</summary>
    [Fact]
    public void Build_cannot_says_a_rebuild_reconnects_in_place_and_the_user_deletes_an_unwanted_wire()
    {
      var cannot = AgentHostTools.KindScope["build"].Cannot;

      Assert.Contains("it reconnects each one, in place, to an output of the new build", cannot);
      Assert.Contains("a wire nothing new should feed must be removed by the user by hand first", cannot);
    }

    /// <summary>#625 commit 7: the CAN half says a rebuild reconnects, in place, what its removed components fed outside
    /// the region - so a planning turn offers the rebuild it can make, instead of only the CANNOT half's constraint.</summary>
    [Fact]
    public void Build_can_says_a_rebuild_reconnects_what_it_removed_fed_outside_the_region()
    {
      Assert.Contains("reconnecting, each in its own place, anything outside the region those components fed",
                      AgentHostTools.KindScope["build"].Can);
    }

    /// <summary>#625's narrow sibling, true for what commit 5 makes true: the CAN half names the restore - and bounds it
    /// to a fresh build and a loss that is the only change.</summary>
    [Fact]
    public void Build_can_says_a_fresh_build_may_restore_an_input_that_only_lost_its_source()
    {
      var can = AgentHostTools.KindScope["build"].Can;

      Assert.Contains("A FRESH build of a region can also restore an input on a component Dwight placed whose only " +
                      "change is losing its source when that region's earlier components were removed", can);
    }

    /// <summary>
    /// #577, BOUNDED IN BOTH DIRECTIONS. simplify's CAN half names to-script, which the agent denied
    /// existed. Its CANNOT half says a script collapse needs existing components - so it cannot write a
    /// script from a plan - which is the reason the refusal was right and the agent never gave.
    /// </summary>
    [Fact]
    public void Simplify_names_to_script_and_the_bound_that_stops_it_scripting_a_plan()
    {
      var c = AgentHostTools.KindScope["simplify"];

      Assert.Contains("C# or Python script component", c.Can);
      Assert.Contains("EXISTING components", c.Can);
      Assert.Contains("cannot write a script from a PLAN", c.Cannot);
    }

    /// <summary>The correction must not over-claim the other way: to-script's chooser promises "a
    /// verified-correct script when Dwight can, a labelled draft when it can't", so the description may not
    /// promise verification unconditionally.</summary>
    [Fact]
    public void Simplify_does_not_promise_a_verified_script_unconditionally()
    {
      Assert.Contains("labelled draft when it cannot", AgentHostTools.KindScope["simplify"].Can);
    }

    /// <summary>What the model actually receives carries both halves for every kind - the table is only
    /// half the fix if the generated sentence drops one of them.</summary>
    [Fact]
    public void The_generated_sentence_carries_both_halves_for_every_kind()
    {
      var sentence = AgentHostTools.CapabilitySentence();

      foreach (var kind in AgentHostTools.Kinds)
      {
        Assert.Contains(AgentHostTools.KindScope[kind].Can, sentence);
        Assert.Contains(AgentHostTools.KindScope[kind].Cannot, sentence);
      }
    }

    /// <summary>The probe's accessor is string-in, string-out, and says nothing about a kind that does
    /// not exist rather than inventing one.</summary>
    [Fact]
    public void ScopeOf_reads_a_kind_by_name_and_returns_null_for_one_that_does_not_exist()
    {
      Assert.Contains("CANNOT:", AgentHostTools.ScopeOf("build"));
      Assert.Null(AgentHostTools.ScopeOf("delete"));
      Assert.Null(AgentHostTools.ScopeOf(null));
    }

    /// <summary>Every offered kind has a scope. A kind added to the enum without one would be described
    /// to the model as merely "available", which is how the nearest-sounding kind gets picked.</summary>
    [Fact]
    public void Every_offered_kind_has_a_stated_scope()
    {
      foreach (var kind in AgentHostTools.Kinds)
        Assert.True(AgentHostTools.KindScope.ContainsKey(kind), kind + " has no stated scope");
    }

    /// <summary>And the scope table describes nothing that is not offered - a scope for a kind the
    /// surface does not have would be a description of a capability that cannot be requested.</summary>
    [Fact]
    public void The_scope_table_describes_nothing_the_surface_does_not_offer()
    {
      foreach (var described in AgentHostTools.KindScope.Keys)
        Assert.Contains(described, AgentHostTools.Kinds);
    }

    // ---- F3: the capability sentence is GENERATED, not written ---------------------------------------

    /// <summary>
    /// The strongest form of #316 available: the claim is generated from the thing it describes, so it
    /// cannot name a capability that does not exist or omit one that does. The gate caught the model
    /// promising a "delete" card that no kind backs - had it simply CALLED request_mutation with that
    /// kind it would have been refused by name with the real list, so this puts the list where it is
    /// composing prose rather than only where it is choosing a tool.
    /// </summary>
    [Fact]
    public void The_capability_sentence_names_every_kind_and_counts_them_correctly()
    {
      var sentence = AgentHostTools.CapabilitySentence();

      Assert.Contains("approve exactly " + AgentHostTools.Kinds.Count + " things", sentence);
      foreach (var kind in AgentHostTools.Kinds)
        Assert.Contains(kind + ":", sentence);
    }

    /// <summary>And it tells the model what to do when none of them fit, which is the half that gives it
    /// somewhere to go instead of the nearest-sounding kind.</summary>
    [Fact]
    public void The_capability_sentence_says_what_to_do_when_no_kind_fits()
    {
      var sentence = AgentHostTools.CapabilitySentence();

      Assert.Contains("DO NOT pick the nearest one", sentence);
      Assert.Contains("NAME the missing capability", sentence);
    }

    private static ToolDescriptor Descriptor() =>
      AgentHostTools.All().Single(t => t.Name == AgentHostTools.RequestMutation);

    // ---- #378b: the loop-breaker --------------------------------------------------------------------

    private const string StampA = "behaviour-aaa|pivots-111";
    private const string StampB = "behaviour-bbb|pivots-222";

    /// <summary>#613: a solve approval arms the breaker only once its turn has dispatched solve_and_check.
    /// Every test below that needs an ARMED breaker says both halves.</summary>
    private static void SolveRan(MutationRequestLog log, string stamp)
    {
      log.RecordApproved("solve", stamp);
      log.ToolDispatched("solve", AgentToolCatalog.SolveAndCheck);
    }

    /// <summary>
    /// The loop, exactly as it ran: ask, approved, nothing changed, ask the same thing again. The second
    /// ask is refused DETERMINISTICALLY - no prompt, no model cooperation required (#146).
    /// </summary>
    [Fact]
    public void A_repeat_request_after_an_approval_that_changed_nothing_is_refused()
    {
      var log = new MutationRequestLog();
      SolveRan(log, StampA);

      Assert.False(AgentHostTools.TryParseRequest(Args("solve", "I need to recompute"), log, StampA,
        out _, out _, out _, out var error));
      Assert.Equal(ToolErrors.PreconditionRefused, error.Code);
    }

    /// <summary>
    /// #383: the refusal must carry the WAY FORWARD. A bare "no" leaves the model exactly where it was,
    /// which is the state that produced the loop in the first place.
    /// </summary>
    [Fact]
    public void The_refusal_tells_the_model_to_say_what_it_cannot_do()
    {
      var log = new MutationRequestLog();
      SolveRan(log, StampA);
      AgentHostTools.TryParseRequest(Args("solve", "again"), log, StampA, out _, out _, out _, out var error);

      Assert.Contains("cannot make this change", error.Message);
      Assert.Contains("name the capability that is missing", error.Message);
    }

    /// <summary>
    /// #383's required test, and the one that keeps the breaker from becoming a wall. The user does
    /// something on the canvas between the two asks - so the second ask is NOT a repeat, and it goes
    /// through. The model is being stopped from repeating itself, never from ever asking twice.
    /// </summary>
    [Fact]
    public void A_user_canvas_change_between_two_same_kind_requests_un_refuses_the_second()
    {
      var log = new MutationRequestLog();
      SolveRan(log, StampA);

      // ...the user edits the definition. Whoever changed it, the stamp is no longer the one recorded.
      Assert.True(AgentHostTools.TryParseRequest(Args("solve", "the graph moved on"), log, StampB,
        out var kind, out _, out _, out _));
      Assert.Equal("solve", kind);
    }

    [Fact]
    public void A_different_kind_is_never_blocked_by_another_kinds_approval()
    {
      var log = new MutationRequestLog();
      SolveRan(log, StampA);

      Assert.True(AgentHostTools.TryParseRequest(Args("reorganise", "it is a mess"), log, StampA,
        out _, out _, out _, out _));
    }

    /// <summary>
    /// A MINT IS NOT AN APPROVAL. A card the user declined, or never answered, leaves the canvas
    /// unchanged too - and refusing on that would punish the model for a decision the user made. Only an
    /// approval that actually ran is evidence that asking again is futile.
    /// </summary>
    [Fact]
    public void A_first_request_is_never_refused_because_nothing_has_been_approved_yet()
    {
      Assert.True(AgentHostTools.TryParseRequest(Args("solve", "first time"), new MutationRequestLog(), StampA,
        out _, out _, out _, out _));
    }

    /// <summary>
    /// Fail open on KNOWLEDGE, never on identity (#208). An unreadable canvas yields no stamp, and a
    /// breaker firing on ignorance would refuse real work on no evidence at all.
    /// </summary>
    [Fact]
    public void An_unknown_canvas_stamp_disarms_the_breaker_rather_than_arming_it()
    {
      var log = new MutationRequestLog();
      SolveRan(log, StampA);

      Assert.True(AgentHostTools.TryParseRequest(Args("solve", "cannot read the canvas"), log, null,
        out _, out _, out _, out _));
      Assert.False(log.RepeatWithNothingChanged("solve", null));
    }

    /// <summary>The breaker is opt-in at the call site: the old overload keeps working and refuses
    /// nothing, so no existing caller silently acquires a guard it was never given.</summary>
    [Fact]
    public void Without_a_log_nothing_is_ever_broken()
    {
      Assert.True(AgentHostTools.TryParseRequest(Args("solve", "no log here"), null, StampA,
        out _, out _, out _, out _));
    }

    // ---- #613: the breaker arms when the flow RUNS, and only solve's run proves futility ------------

    /// <summary>
    /// #609 link 2, for solve: approved, and the turn never called solve_and_check. Nothing ran, so nothing
    /// is evidence - the next solve request mints a card.
    /// </summary>
    [Fact]
    public void An_approved_solve_turn_that_calls_no_tool_leaves_the_breaker_disarmed()
    {
      var log = new MutationRequestLog();
      log.RecordApproved("solve", StampA);

      Assert.False(log.RepeatWithNothingChanged("solve", StampA));
      Assert.True(AgentHostTools.TryParseRequest(Args("solve", "it never ran"), log, StampA,
        out _, out _, out _, out _));
    }

    [Fact]
    public void Dispatching_solve_and_check_arms_it_with_the_stamp_taken_at_approval()
    {
      var log = new MutationRequestLog();
      log.RecordApproved("solve", StampA);
      log.ToolDispatched("solve", AgentToolCatalog.SolveAndCheck);

      Assert.True(log.RepeatWithNothingChanged("solve", StampA));
      Assert.False(log.RepeatWithNothingChanged("solve", StampB));
    }

    [Theory]
    [InlineData("solve", AgentToolCatalog.GetOverview)]
    [InlineData("solve", AgentToolCatalog.BuildRegion)]
    [InlineData("build", AgentToolCatalog.SolveAndCheck)]
    [InlineData(null, AgentToolCatalog.SolveAndCheck)]
    public void Any_other_tool_or_turn_arms_nothing(string approvedKind, string tool)
    {
      var log = new MutationRequestLog();
      log.RecordApproved("solve", StampA);
      log.ToolDispatched(approvedKind, tool);

      Assert.False(log.RepeatWithNothingChanged("solve", StampA));
    }

    /// <summary>Reorganise and Simplify run from the click itself, so the click IS the run - unchanged.</summary>
    [Theory]
    [InlineData("reorganise")]
    [InlineData("simplify")]
    public void Reorganise_and_simplify_still_arm_at_redemption(string kind)
    {
      var log = new MutationRequestLog();
      log.RecordApproved(kind, StampA);

      Assert.True(log.RepeatWithNothingChanged(kind, StampA));
      Assert.False(AgentHostTools.TryParseRequest(Args(kind, "again"), log, StampA,
        out _, out _, out _, out _));
    }

    /// <summary>
    /// THE CASE #613 RULED ON. A build that ran and REVERTED left the canvas exactly as it was - and a
    /// breaker armed on it would tell the model to say Dwight cannot build, which is false. A retry carries
    /// a new spec, so the next build request mints a card however the last one ended.
    /// </summary>
    [Theory]
    [InlineData("build", AgentToolCatalog.BuildRegion)]
    [InlineData("scaffold", AgentToolCatalog.ProposeScaffold)]
    public void A_build_or_scaffold_that_ran_and_reverted_leaves_the_next_request_mintable(string kind, string tool)
    {
      var log = new MutationRequestLog();
      log.RecordApproved(kind, StampA);
      log.ToolDispatched(kind, tool);   // it ran, reverted, and the stamp is back where it was

      Assert.False(log.RepeatWithNothingChanged(kind, StampA));
      Assert.True(AgentHostTools.TryParseRequest(Args(kind, "try again with a different spec"), log, StampA,
        out _, out _, out _, out _));
    }

    /// <summary>
    /// #679, #613's law applied to set_value. It runs from the click, so it LOOKS like an at-redemption kind - but the
    /// breaker's sentence ("nothing on the canvas has changed... say plainly that you cannot make this change") must be
    /// true in every state that arms it. A set_value that applied without moving the stamp (a domain- or rounding-only
    /// change, while the stamp read the slider's value alone), or one that REVERTED, leaves the stamp where it was - and
    /// the next set_value, for this slider or any other, would be refused with that false sentence. So it never arms.
    /// </summary>
    [Fact]
    public void A_set_value_that_left_the_stamp_unchanged_leaves_the_next_request_mintable()
    {
      Assert.Equal(BreakerArming.Never, MutationRequestLog.ArmingOf("set_value"));

      var log = new MutationRequestLog();
      log.RecordApproved("set_value", StampA);   // applied domain-only, or reverted: the stamp is still A

      Assert.False(log.RepeatWithNothingChanged("set_value", StampA));
      Assert.True(AgentHostTools.TryParseRequest(Args("set_value", "set the other slider's domain"), log, StampA,
        out _, out _, out _, out var error), error?.Message);
    }

    /// <summary>A second approval of solve before the first ran supersedes it - the new one has not run.</summary>
    [Fact]
    public void A_fresh_solve_approval_disarms_until_its_own_run()
    {
      var log = new MutationRequestLog();
      SolveRan(log, StampA);
      log.RecordApproved("solve", StampA);

      Assert.False(log.RepeatWithNothingChanged("solve", StampA));
    }

    [Theory]
    [InlineData((int)TurnCapability.Build, "build", AgentToolCatalog.BuildRegion)]
    [InlineData((int)TurnCapability.ReadAndSolve, "solve", AgentToolCatalog.SolveAndCheck)]
    [InlineData((int)TurnCapability.Scaffold, "scaffold", AgentToolCatalog.ProposeScaffold)]
    public void Each_approved_capability_names_its_kind_and_the_tool_that_carries_it_out(
      int capabilityValue, string kind, string tool)
    {
      var capability = (TurnCapability)capabilityValue;   // the enum is internal, so a public theory takes its value
      Assert.Equal(kind, AgentHostTools.KindApprovedBy(capability));
      Assert.Equal(tool, AgentHostTools.ActingToolFor(kind));
      Assert.True(AgentLoopCore.IsGranted(capability, tool));
    }

    [Fact]
    public void A_read_only_turn_carries_no_approved_kind()
    {
      Assert.Null(AgentHostTools.KindApprovedBy(TurnCapability.ReadOnly));
    }

    // ---- #611d: inside an approved turn, the same kind is refused with a pointer at its tool ----------

    /// <summary>
    /// The build that never ran asked for its own approval again (#609 link 1). The answer is the tool,
    /// named in the refusal - which #611a now makes readable inside the turn.
    /// </summary>
    [Theory]
    [InlineData("build", AgentToolCatalog.BuildRegion)]
    [InlineData("solve", AgentToolCatalog.SolveAndCheck)]
    [InlineData("scaffold", AgentToolCatalog.ProposeScaffold)]
    public void The_just_approved_kind_is_refused_with_a_pointer_to_its_tool(string kind, string tool)
    {
      Assert.False(AgentHostTools.TryParseRequest(Args(kind, "raising it now"), new MutationRequestLog(), StampA,
        kind, out _, out _, out _, out var error));

      Assert.Equal(ToolErrors.AlreadyApproved, error.Code);
      Assert.Contains("already approved", error.Message);
      Assert.Contains("Call " + tool + " now", error.Message);
    }

    [Theory]
    [InlineData("build", "reorganise")]
    [InlineData("build", "solve")]
    [InlineData("solve", "build")]
    public void A_different_kind_inside_an_approved_turn_still_parses(string approvedThisTurn, string asked)
    {
      Assert.True(AgentHostTools.TryParseRequest(Args(asked, "a different question"), new MutationRequestLog(),
        StampA, approvedThisTurn, out var kind, out _, out _, out _));
      Assert.Equal(asked, kind);
    }

    /// <summary>Ahead of the breaker: inside a solve turn whose solve already ran unchanged, the model is
    /// pointed at the tool it holds, not told it cannot make the change.</summary>
    [Fact]
    public void The_same_kind_refusal_answers_before_the_breaker_does()
    {
      var log = new MutationRequestLog();
      SolveRan(log, StampA);

      AgentHostTools.TryParseRequest(Args("solve", "again"), log, StampA, "solve", out _, out _, out _, out var error);

      Assert.Equal(ToolErrors.AlreadyApproved, error.Code);
    }

    /// <summary>A read-only turn holds no approval, so nothing is refused as already approved there.</summary>
    [Fact]
    public void Outside_an_approved_turn_the_same_request_parses()
    {
      Assert.True(AgentHostTools.TryParseRequest(Args("build", "please"), new MutationRequestLog(), StampA,
        null, out _, out _, out _, out _));
    }

    // ---- #617: the same-kind refusal is TRUE for the regions it names ------------------------------

    private static JsonObject BuildArgs(params string[] regions)
    {
      var o = Args("build", "next region");
      o["regions"] = new JsonArray(regions.Select(r => (JsonNode)r).ToArray());
      return o;
    }

    private static ToolResult RefusalInA1Turn(JsonObject args, params string[] approved)
    {
      Assert.False(AgentHostTools.TryParseRequest(args, new MutationRequestLog(), StampA, "build",
        approved.Length == 0 ? new[] { "A1" } : approved, out _, out _, out _, out var error));
      return error;
    }

    /// <summary>Regions the turn already holds: the pointer is true, so it is what the model reads.</summary>
    [Fact]
    public void A_request_inside_the_approved_set_gets_the_pointer()
    {
      Assert.Equal(ToolErrors.AlreadyApproved, RefusalInA1Turn(BuildArgs("A1")).Code);
      Assert.Equal(ToolErrors.AlreadyApproved, RefusalInA1Turn(BuildArgs()).Code);
      Assert.Equal(ToolErrors.AlreadyApproved, RefusalInA1Turn(BuildArgs("A1", "A3"), "A1", "A3").Code);
    }

    /// <summary>
    /// THE CASE #617 NAMES. Tharun approves one region at a time from a plan of six, so a model offering A2
    /// inside the A1 turn is likely. "Already approved - call build_region" would be false for A2: the ledger
    /// would refuse it. The refusal says what IS true, in words the model can relay.
    /// </summary>
    [Fact]
    public void A_region_outside_the_approval_is_told_it_needs_its_own()
    {
      var error = RefusalInA1Turn(BuildArgs("A2"));

      Assert.Equal(ToolErrors.RegionNotApproved, error.Code);
      Assert.Equal("This turn is approved to build A1 only. Build it now with build_region. A2 needs its own " +
                   "approval: tell the user, and they can ask for it after this build.", error.Message);
      Assert.DoesNotContain("already approved", error.Message);
    }

    [Fact]
    public void Any_region_outside_makes_the_whole_request_outside()
    {
      var error = RefusalInA1Turn(BuildArgs("A1", "A2"));

      Assert.Equal(ToolErrors.RegionNotApproved, error.Code);
      Assert.Contains("A2 needs its own approval", error.Message);
      Assert.DoesNotContain("A1 needs", error.Message);
    }

    [Fact]
    public void Both_sets_are_named_in_the_plural_when_they_are_plural()
    {
      var error = RefusalInA1Turn(BuildArgs("B2", "C1"), "A1", "A3");

      Assert.Contains("approved to build A1, A3 only. Build them now with build_region.", error.Message);
      Assert.Contains("B2, C1 need their own approval: tell the user, and they can ask for them after this build.",
        error.Message);
    }

    /// <summary>The model's raw strings are never echoed: a requested entry that is not an address is still
    /// outside the approval, and the refusal says so without repeating it.</summary>
    [Fact]
    public void A_requested_entry_that_is_not_an_address_is_outside_but_never_named()
    {
      var error = RefusalInA1Turn(BuildArgs("<b>every region</b>"));

      Assert.Equal(ToolErrors.RegionNotApproved, error.Code);
      Assert.Contains("Any other region needs its own approval", error.Message);
      Assert.DoesNotContain("every region", error.Message);
      Assert.DoesNotContain("<b>", error.Message);
    }

    /// <summary>A solve or scaffold turn has no region set, so the regions field changes nothing there.</summary>
    [Fact]
    public void Without_an_approved_region_set_the_pointer_stands()
    {
      Assert.False(AgentHostTools.TryParseRequest(BuildArgs("A2"), new MutationRequestLog(), StampA, "build",
        null, out _, out _, out _, out var error));
      Assert.Equal(ToolErrors.AlreadyApproved, error.Code);
    }

    // ---- #626/#635: the pointer is true only while the region has attempts left ----------------------

    private static bool ParseInBuildTurn(JsonObject args, string[] approved, string[] exhausted,
                                         out string kind, out ToolResult error) =>
      AgentHostTools.TryParseRequest(args, new MutationRequestLog(), StampA, "build", approved,
        r => exhausted.Contains(r), out kind, out _, out _, out error);

    /// <summary>
    /// THE FIELD CASE (#626). The model spent A1's attempts, found its own bug, asked to rebuild A1 - and was
    /// told "call build_region now" twice, which build_region would refuse. Every requested region exhausted
    /// means a new approval is the only way forward, so the request is put to the user as a card: a new turn
    /// with a fresh budget, bounded by a click.
    /// </summary>
    [Fact]
    public void A_request_whose_every_region_is_exhausted_mints_a_card()
    {
      Assert.True(ParseInBuildTurn(BuildArgs("A1"), new[] { "A1" }, new[] { "A1" }, out var kind, out var error));
      Assert.Equal("build", kind);
      Assert.Null(error);
    }

    [Fact]
    public void A_request_whose_regions_still_have_attempts_keeps_the_pointer()
    {
      Assert.False(ParseInBuildTurn(BuildArgs("A1"), new[] { "A1" }, new string[0], out _, out var error));
      Assert.Equal(ToolErrors.AlreadyApproved, error.Code);
      Assert.Contains("Call build_region now", error.Message);
    }

    /// <summary>Mixed: the pointer is kept - and made true, by naming only the region it is true for, and telling
    /// the model how the exhausted one gets a fresh attempt without costing the live one its build.</summary>
    [Fact]
    public void A_mixed_request_keeps_the_pointer_for_the_live_region_only()
    {
      Assert.False(ParseInBuildTurn(BuildArgs("A1", "A2"), new[] { "A1", "A2" }, new[] { "A1" }, out _, out var error));

      Assert.Equal(ToolErrors.AlreadyApproved, error.Code);
      Assert.Equal("A2 is already approved and still has attempts in this turn: call build_region for it now. " +
                   "A1 has used all its attempts in this turn - after A2, request A1 on its own and the user gets " +
                   "a card for a fresh attempt.", error.Message);
    }

    /// <summary>#635: a request naming no regions, when every region the turn holds is exhausted, would get a
    /// pointer that is false for all of them. It is told to name what it means to rebuild.</summary>
    [Fact]
    public void A_request_naming_no_regions_when_every_held_region_is_exhausted_is_told_to_name_them()
    {
      Assert.False(ParseInBuildTurn(BuildArgs(), new[] { "A1", "A2" }, new[] { "A1", "A2" }, out _, out var error));

      Assert.Equal(ToolErrors.InvalidArgs, error.Code);
      Assert.Contains("name the regions to rebuild", error.Message);
    }

    [Fact]
    public void A_request_naming_no_regions_with_attempts_left_keeps_the_pointer()
    {
      Assert.False(ParseInBuildTurn(BuildArgs(), new[] { "A1", "A2" }, new[] { "A1" }, out _, out var error));
      Assert.Equal(ToolErrors.AlreadyApproved, error.Code);
    }

    /// <summary>Exhaustion never widens a request: a region outside the approval is still told it needs its
    /// own, whatever the turn has spent.</summary>
    [Fact]
    public void Exhaustion_does_not_override_the_outside_the_approval_refusal()
    {
      Assert.False(ParseInBuildTurn(BuildArgs("A1", "A2"), new[] { "A1" }, new[] { "A1", "A2" }, out _, out var error));
      Assert.Equal(ToolErrors.RegionNotApproved, error.Code);
    }

    /// <summary>A minted card still needs its "why": the user is being asked, so they read what it is for.</summary>
    [Fact]
    public void An_exhausted_request_with_no_why_is_refused_for_the_why()
    {
      var args = BuildArgs("A1");
      args.Remove("why");

      Assert.False(ParseInBuildTurn(args, new[] { "A1" }, new[] { "A1" }, out _, out var error));
      Assert.Equal(ToolErrors.InvalidArgs, error.Code);
      Assert.Contains("\"why\"", error.Message);
    }
  }
}
