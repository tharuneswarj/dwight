using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Dwight.Agent;
using Dwight.Llm;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// THE REAL LOOP, DRIVEN BY A SCRIPT (#612).
  ///
  /// These tests exist because a test BESIDE the loop was green while the loop was wrong: GateFixTests
  /// asserted that request_mutation ends the turn by name, and a refused request therefore ended the turn
  /// unread (#609 link 3). Nothing here re-implements a step - the provider is scripted, tool dispatch is
  /// scripted, and everything between them is the product's own code.
  ///
  /// SERIAL WITH TurnShapeTests. Every turn the loop finishes is observed by the static TurnShape record,
  /// and those tests Clear it and count - run in parallel, a driven turn lands in their denominator. Seen
  /// red once (Both_detectors_count_against_the_same_turns, 1 run in 7), an instrument defect of this
  /// unit's own making, not a subject one.
  /// </summary>
  [Collection(TurnShapeTests.StaticRecord)]
  public class AgentTurnDriverTests
  {
    /// <summary>A provider that replays a fixed list of replies and records what it was sent each time.</summary>
    internal sealed class ScriptedProvider : ILlmProvider
    {
      private readonly Queue<LlmResponse> _replies;
      public ScriptedProvider(params LlmResponse[] replies) { _replies = new Queue<LlmResponse>(replies); }

      public string ModelId => "scripted";
      public ProviderCapability Capability => default(ProviderCapability);

      /// <summary>The tool_result texts present in the transcript at each send, in order.</summary>
      public readonly List<List<string>> ToolResultsSeen = new List<List<string>>();
      public readonly List<string> SystemsSeen = new List<string>();

      /// <summary>A snapshot of the whole message list at each send.</summary>
      public readonly List<List<LlmMessage>> MessagesSeen = new List<List<LlmMessage>>();

      public LlmResponse Send(LlmRequest request, CancellationToken cancel = default)
      {
        MessagesSeen.Add(request.Messages.ToList());
        ToolResultsSeen.Add(request.Messages
          .SelectMany(m => m.Blocks)
          .Where(b => b.Kind == "tool_result")
          .Select(b => b.Text)
          .ToList());
        SystemsSeen.Add(request.System);
        if (_replies.Count == 0) throw new InvalidOperationException("the script ran out of replies");
        return _replies.Dequeue();
      }
    }

    internal static LlmResponse Calls(string id, string tool, string argsJson, string prose = null)
    {
      var r = new LlmResponse { StopReason = "tool_use" };
      if (prose != null) r.Blocks.Add(LlmBlock.TextBlock(prose));
      r.Blocks.Add(LlmBlock.ToolUse(id, tool, argsJson));
      return r;
    }

    internal static LlmResponse Says(string prose) =>
      new LlmResponse { StopReason = "end_turn", Blocks = { LlmBlock.TextBlock(prose) } };

    internal const string Refused =
      "{\"ok\":false,\"error\":{\"code\":\"precondition_refused\",\"message\":\"REFUSAL-TEXT-FOR-THE-MODEL\"}}";

    internal const string CardShown =
      "{\"ok\":true,\"result\":{\"cardShown\":true,\"kind\":\"build\",\"approved\":false,\"turnEnds\":true}}";

    internal static TurnResult Drive(ScriptedProvider provider, Func<LlmBlock, string> execute, int maxSteps = 8,
                                     string actingTool = null, IReadOnlyList<string> approvedRegions = null)
    {
      var clock = Stopwatch.StartNew();
      return AgentTurnDriver.Drive(new TurnDrive
      {
        Provider = provider,
        System = "system",
        Messages = new List<LlmMessage> { LlmMessage.User("build A1") },
        Limits = new TurnLimits { MaxSteps = maxSteps, WallClockMs = 0, MaxTranscriptChars = 1_000_000, AnswerBudget = 1000 },
        Execute = execute,
        ActingTool = actingTool,
        ApprovedRegions = approvedRegions,
      }, clock);
    }

    // ---- #657/#666: an approved build turn that ends without building gets ONE repair step ------------

    private const string Built = "{\"ok\":true,\"result\":{\"status\":\"applied\",\"region\":\"B1\"}}";

    private static readonly string[] B1 = { "B1" };

    /// <summary>The user-role texts the provider was sent at each request - where an in-turn plugin fact would appear.</summary>
    private static List<string> UserTextsAt(ScriptedProvider provider, int request) =>
      provider.MessagesSeen[request].Where(m => m.Role == LlmRole.User)
        .SelectMany(m => m.Blocks).Where(b => b.Kind == "text").Select(b => b.Text).ToList();

    /// <summary>
    /// THE FIELD CASE (#657). Approved to rebuild B1, the model wrote "B1 rebuilt: radius now 8, verified clean" and never
    /// called build_region. The turn now gets one more step carrying a plugin fact - mechanically actionable (the tool,
    /// the region), which is the ADR-094 condition under which a closed loop works.
    /// </summary>
    [Fact]
    public void An_approved_build_that_ends_without_building_gets_one_repair_step_with_the_fact()
    {
      var provider = new ScriptedProvider(
        Says("B1 rebuilt: radius now 8, verified clean."),
        Calls("t1", AgentToolCatalog.BuildRegion, "{\"region\":\"B1\",\"components\":[{\"ref\":\"r\",\"type\":\"Number Slider\"}]}"),
        Says("B1 is rebuilt with the radius at 8."));

      var result = Drive(provider, call => Built, actingTool: AgentToolCatalog.BuildRegion, approvedRegions: B1);

      Assert.True(result.BuildRepairFired);
      Assert.Equal(3, result.Steps);
      Assert.Contains(UserTextsAt(provider, 1), t => t == AgentLoopCore.BuildRepairFact(B1));
      Assert.Equal("PLUGIN FACT (from Dwight, not the user): nothing has been built - build_region was not called for " +
                   "B1, which the user approved. Call it now, or say plainly why you cannot.", AgentLoopCore.BuildRepairFact(B1));
      Assert.Equal(1, result.ApprovedBuildDispatches);
      Assert.Equal("B1 is rebuilt with the radius at 8.", result.Text);
    }

    /// <summary>
    /// #666's ADDITION: the pre-repair prose is DISCARDED when the repair fires - it was written about a build that had not
    /// happened. If the repaired turn builds and then writes no new text, the fabricated paragraph would otherwise render
    /// over a real build it never described.
    /// </summary>
    [Fact]
    public void The_pre_repair_prose_is_discarded_so_it_never_renders_over_the_real_build()
    {
      var provider = new ScriptedProvider(
        Says("B1 rebuilt: radius now 8, verified clean."),
        Calls("t1", AgentToolCatalog.BuildRegion, "{\"region\":\"B1\",\"components\":[{\"ref\":\"r\",\"type\":\"Number Slider\"}]}"),
        new LlmResponse { StopReason = "end_turn" });

      var result = Drive(provider, call => Built, actingTool: AgentToolCatalog.BuildRegion, approvedRegions: B1);

      Assert.True(result.BuildRepairFired);
      Assert.Equal("", result.Text);
    }

    /// <summary>#672 through the REAL loop: the successful repair - skipped, repaired, built, silent - is exactly the turn
    /// that must not render "Dwight had nothing to say to that." above its verified build.</summary>
    [Fact]
    public void A_repaired_built_silent_turn_shows_no_empty_reply_filler()
    {
      var provider = new ScriptedProvider(
        Says("B1 rebuilt: radius now 8, verified clean."),
        Calls("t1", AgentToolCatalog.BuildRegion, "{\"region\":\"B1\",\"components\":[{\"ref\":\"r\",\"type\":\"Number Slider\"}]}"),
        new LlmResponse { StopReason = "end_turn" });

      var result = Drive(provider, call => Built, actingTool: AgentToolCatalog.BuildRegion, approvedRegions: B1);

      Assert.True(result.BuildRepairFired);
      Assert.Equal(1, result.ApprovedBuildDispatches);
      Assert.False(AgentLoopCore.ShowEmptyReplyFiller(TurnCapability.Build, result));
    }

    /// <summary>#666: the fact exists IN-TURN ONLY. It rides the provider request and nothing the caller renders or stores -
    /// not the text, not the note.</summary>
    [Fact]
    public void The_repair_fact_is_never_in_what_the_turn_hands_back_to_render_or_store()
    {
      var provider = new ScriptedProvider(Says("Done."), Says("I cannot build B1: the approved plan has no radius input."));

      var result = Drive(provider, call => Built, actingTool: AgentToolCatalog.BuildRegion, approvedRegions: B1);

      Assert.Contains(UserTextsAt(provider, 1), t => t.StartsWith("PLUGIN FACT"));
      Assert.DoesNotContain("PLUGIN FACT", result.Text ?? "");
      Assert.DoesNotContain("PLUGIN FACT", result.Note ?? "");
      Assert.Equal("I cannot build B1: the approved plan has no radius input.", result.Text);
    }

    [Fact]
    public void No_repair_when_build_region_was_dispatched_for_an_approved_region()
    {
      var provider = new ScriptedProvider(
        Calls("t1", AgentToolCatalog.BuildRegion, "{\"region\":\"B1\",\"components\":[]}"),
        Says("B1 is built."),
        Says("this step must never be requested"));

      var result = Drive(provider, call => Built, actingTool: AgentToolCatalog.BuildRegion, approvedRegions: B1);

      Assert.False(result.BuildRepairFired);
      Assert.Equal(2, result.Steps);
    }

    /// <summary>"For any approved region" (#657): an unaddressed build, or one for a region the user did not approve, is
    /// not the approved build having run.</summary>
    [Theory]
    [InlineData("{\"components\":[]}")]
    [InlineData("{\"region\":\"C1\",\"components\":[]}")]
    public void The_repair_fires_when_the_only_build_was_unaddressed_or_for_another_region(string args)
    {
      var provider = new ScriptedProvider(
        Calls("t1", AgentToolCatalog.BuildRegion, args),
        Says("Built."),
        Says("I could not build B1."));

      var result = Drive(provider, call => Built, actingTool: AgentToolCatalog.BuildRegion, approvedRegions: B1);

      Assert.True(result.BuildRepairFired);
      Assert.Equal(0, result.ApprovedBuildDispatches);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(AgentToolCatalog.SolveAndCheck)]
    public void No_repair_on_a_turn_that_is_not_an_approved_build(string actingTool)
    {
      var provider = new ScriptedProvider(Says("Here is the map."), Says("this step must never be requested"));

      var result = Drive(provider, call => "{}", actingTool: actingTool, approvedRegions: B1);

      Assert.False(result.BuildRepairFired);
      Assert.Equal(1, result.Steps);
    }

    /// <summary>At most once per turn: a second skip ends the turn - exactly two end-turn replies consumed, no third request.</summary>
    [Fact]
    public void A_second_skip_ends_the_turn()
    {
      var provider = new ScriptedProvider(
        Says("B1 rebuilt."),
        Says("B1 is rebuilt, really."),
        Says("this step must never be requested"));

      var result = Drive(provider, call => Built, actingTool: AgentToolCatalog.BuildRegion, approvedRegions: B1);

      Assert.True(result.BuildRepairFired);
      Assert.Equal(2, result.Steps);
      Assert.Equal(2, provider.MessagesSeen.Count);
      Assert.Equal(TurnOutcome.Complete, result.Outcome);
    }

    [Fact]
    public void No_repair_on_a_truncated_end()
    {
      var provider = new ScriptedProvider(
        new LlmResponse { StopReason = "max_tokens", Blocks = { LlmBlock.TextBlock("B1 rebuilt: rad") } },
        Says("this step must never be requested"));

      var result = Drive(provider, call => Built, actingTool: AgentToolCatalog.BuildRegion, approvedRegions: B1);

      Assert.False(result.BuildRepairFired);
      Assert.Equal(TurnOutcome.Truncated, result.Outcome);
    }

    /// <summary>The fact names only approved ADDRESSES - never a raw string that is not one.</summary>
    [Fact]
    public void The_repair_fact_names_only_approved_addresses()
    {
      var fact = AgentLoopCore.BuildRepairFact(new[] { "B1", "<b>all</b>", "C2" });

      Assert.Contains("was not called for B1, C2, which the user approved", fact);
      Assert.DoesNotContain("<b>", fact);
    }

    // ---- #624: approved build turns that never dispatched build_region -----------------------------

    /// <summary>
    /// THE FIELD CASE, COUNTED. An approved build turn wrote "Both fixes landed" and dispatched no build_region.
    /// A detector, not a guard: it counts through the real loop, with its denominator, so a zero can be told
    /// from an absence (#212).
    /// </summary>
    [Fact]
    public void An_approved_build_turn_that_never_dispatches_build_region_is_counted()
    {
      TurnShape.Clear();

      Drive(new ScriptedProvider(Says("Both fixes landed and verified.")),
            call => throw new InvalidOperationException("no tool should run"),
            actingTool: AgentToolCatalog.BuildRegion);

      Assert.Equal(1, TurnShape.ApprovedBuildTurns);
      Assert.Equal(1, TurnShape.ApprovedBuildTurnsWithoutBuild);
      TurnShape.Clear();
    }

    [Fact]
    public void An_approved_build_turn_that_dispatches_build_region_is_in_the_denominator_only()
    {
      TurnShape.Clear();

      Drive(new ScriptedProvider(
              Calls("t1", AgentToolCatalog.GetOverview, "{}"),
              Calls("t2", AgentToolCatalog.BuildRegion, "{\"region\":\"A1\"}"),
              Says("A1 is built.")),
            call => "{\"ok\":true,\"result\":{}}",
            actingTool: AgentToolCatalog.BuildRegion);

      Assert.Equal(1, TurnShape.ApprovedBuildTurns);
      Assert.Equal(0, TurnShape.ApprovedBuildTurnsWithoutBuild);
      TurnShape.Clear();
    }

    /// <summary>A read-only turn holds no approval, so it is not in this denominator at all - and neither is
    /// an approved solve, whose acting tool is not build_region.</summary>
    [Fact]
    public void Turns_that_are_not_approved_builds_are_not_in_the_denominator()
    {
      TurnShape.Clear();

      Drive(new ScriptedProvider(Says("Here is the map.")), call => "{}");
      Drive(new ScriptedProvider(Says("Solved.")), call => "{}", actingTool: AgentToolCatalog.SolveAndCheck);

      Assert.Equal(2, TurnShape.Examined);
      Assert.Equal(0, TurnShape.ApprovedBuildTurns);
      Assert.Equal(0, TurnShape.ApprovedBuildTurnsWithoutBuild);
      TurnShape.Clear();
    }

    // ---- #659/#669: announced a change while requesting and acting on nothing, counted through the loop ----

    /// <summary>Counted through the REAL loop: a turn that read, then announced a rebuild, and requested nothing.</summary>
    [Fact]
    public void A_driven_turn_that_announces_a_rebuild_and_requests_nothing_is_counted()
    {
      TurnShape.Clear();

      var result = Drive(new ScriptedProvider(
                           Calls("t1", AgentToolCatalog.GetRegion, "{\"region\":\"A1\"}"),
                           Says("I'll rebuild A1 with the radius at 8.")),
                         call => "{\"ok\":true,\"result\":{}}");

      Assert.Equal(0, result.RequestCalls);
      Assert.Equal(1, TurnShape.Examined);
      Assert.Equal(1, TurnShape.AnnouncedChangeCount);
      TurnShape.Clear();
    }

    /// <summary>The loop counts request_mutation calls - shown card or refused alike, because both ASKED - and a turn
    /// that asked is not counted, whatever its prose announced.</summary>
    [Fact]
    public void A_driven_turn_that_requested_is_not_counted_and_its_requests_are_counted_shown_or_refused()
    {
      TurnShape.Clear();

      var shown = Drive(new ScriptedProvider(
                          Calls("t1", AgentHostTools.RequestMutation, "{\"kind\":\"build\",\"regions\":[\"A1\"]}",
                                prose: "Raising a card to rebuild A1.")),
                        call => CardShown);

      var refused = Drive(new ScriptedProvider(
                            Calls("t1", AgentHostTools.RequestMutation, "{\"kind\":\"build\"}"),
                            Says("I'll rebuild A1 once the card can be raised.")),
                          call => Refused);

      Assert.Equal(1, shown.RequestCalls);
      Assert.Equal(1, refused.RequestCalls);
      Assert.Equal(2, TurnShape.Examined);
      Assert.Equal(0, TurnShape.AnnouncedChangeCount);
      TurnShape.Clear();
    }

    // ---- #611a: the turn ends on the RESULT, not the tool name ---------------------------------------

    /// <summary>
    /// THE FLIPPED CHARACTERIZATION. At #612 this test pinned the defect - a refused request ended the turn
    /// in one step with AskedForConsent set, and the model never read the refusal (#609 link 3). Now the
    /// refusal is fed back and the model gets a second step, in which it can act on what it was told.
    /// </summary>
    [Fact]
    public void A_refused_request_is_read_and_the_turn_takes_a_second_step()
    {
      var provider = new ScriptedProvider(
        Calls("t1", AgentHostTools.RequestMutation, "{\"kind\":\"build\",\"why\":\"x\"}", "Raising the build now."),
        Says("I read the refusal."));

      var result = Drive(provider, call => Refused);

      Assert.Equal(2, result.Steps);
      Assert.Contains(provider.ToolResultsSeen[1], t => t.Contains("REFUSAL-TEXT-FOR-THE-MODEL"));
      Assert.False(result.AskedForConsent);
      Assert.Equal(1, result.RefusedRequests);
      Assert.Equal("I read the refusal.", result.Text);
    }

    /// <summary>A card the user can see still ends the turn at once (#256) - the answer arrives as a later
    /// message, so another step could only ask the same question again.</summary>
    [Fact]
    public void A_shown_card_ends_the_turn_with_no_further_step()
    {
      var provider = new ScriptedProvider(
        Calls("t1", AgentHostTools.RequestMutation, "{\"kind\":\"build\",\"why\":\"x\"}"),
        Says("this step must never be requested"));

      var result = Drive(provider, call => CardShown);

      Assert.Equal(1, result.Steps);
      Assert.True(result.AskedForConsent);
      Assert.Equal(TurnOutcome.Complete, result.Outcome);
      Assert.Null(result.Note);
    }

    /// <summary>
    /// THE BOUND. The first refusal carries the way forward; a second means it did not take, and a third
    /// ask would spend the user's money on a loop - the very loop #378b was built to stop.
    /// </summary>
    [Fact]
    public void A_second_refused_request_in_the_same_turn_ends_it()
    {
      var provider = new ScriptedProvider(
        Calls("t1", AgentHostTools.RequestMutation, "{\"kind\":\"build\",\"why\":\"x\"}"),
        Calls("t2", AgentHostTools.RequestMutation, "{\"kind\":\"build\",\"why\":\"again\"}"),
        Says("this step must never be requested"));

      var result = Drive(provider, call => Refused);

      Assert.Equal(2, result.Steps);
      Assert.Equal(2, result.RefusedRequests);
      Assert.False(result.AskedForConsent);
      Assert.Equal(TurnOutcome.Complete, result.Outcome);
    }

    // ---- #973: THE BOUND COUNTS REFUSALS THE MODEL HAS READ ------------------------------------------------------

    private static string Refusal(string code, string message, System.Text.Json.Nodes.JsonObject detail = null) =>
      ToolResult.Fail(code, message, detail).ToJson();

    /// <summary>Execute by call order: the n-th dispatch returns the n-th envelope.</summary>
    private static Func<LlmBlock, string> InOrder(params string[] envelopes)
    {
      var i = 0;
      return call => envelopes[Math.Min(i++, envelopes.Length - 1)];
    }

    /// <summary>SHAPE 1. invalid_args, then a DIFFERENT refusal: the second one's diagnostic was never read, so it does
    /// not end the turn - it is sent back, and the model's next step is the one that acts on it.</summary>
    [Fact]
    public void A_second_refusal_with_a_different_diagnostic_is_sent_back_and_the_turn_continues()
    {
      var provider = new ScriptedProvider(
        Calls("t1", AgentHostTools.RequestMutation, "{\"kind\":\"build\"}"),
        Calls("t2", AgentHostTools.RequestMutation, "{\"kind\":\"build\",\"why\":\"x\"}"),
        Says("I read the second diagnostic and will fix the call."));

      var result = Drive(provider, InOrder(
        Refusal(ToolErrors.InvalidArgs, "FIRST-DIAGNOSTIC"),
        Refusal(ToolErrors.PreconditionRefused, "SECOND-DIAGNOSTIC-THE-MODEL-NEVER-READ")));

      Assert.Equal(3, result.Steps);                                       // was 2: the turn ended unread
      Assert.Equal(2, result.RefusedRequests);
      Assert.Contains(provider.ToolResultsSeen[2], t => t.Contains("SECOND-DIAGNOSTIC-THE-MODEL-NEVER-READ"));
      Assert.Null(result.Note);
      Assert.Equal("I read the second diagnostic and will fix the call.", result.Text);
    }

    /// <summary>SHAPE 2 (the positive control): the SAME refusal twice. The model read it and repeated the call - the loop
    /// the bound exists to stop - so the turn ends.</summary>
    [Fact]
    public void The_same_refusal_twice_still_ends_the_turn()
    {
      var provider = new ScriptedProvider(
        Calls("t1", AgentHostTools.RequestMutation, "{\"kind\":\"build\"}"),
        Calls("t2", AgentHostTools.RequestMutation, "{\"kind\":\"build\"}"),
        Says("this step must never be requested"));

      var result = Drive(provider, InOrder(Refusal(ToolErrors.InvalidArgs, "SAME-TEXT")));

      Assert.Equal(2, result.Steps);
      Assert.StartsWith("Dwight stopped:", result.Note);
    }

    /// <summary>The cap keeps the asking finite even when every refusal is new: two read, a third ends it.</summary>
    [Fact]
    public void A_third_refusal_ends_the_turn_even_when_each_was_different()
    {
      var provider = new ScriptedProvider(
        Calls("t1", AgentHostTools.RequestMutation, "{\"kind\":\"build\"}"),
        Calls("t2", AgentHostTools.RequestMutation, "{\"kind\":\"build\"}"),
        Calls("t3", AgentHostTools.RequestMutation, "{\"kind\":\"build\"}"),
        Says("this step must never be requested"));

      var result = Drive(provider, InOrder(
        Refusal(ToolErrors.InvalidArgs, "ONE"),
        Refusal(ToolErrors.InvalidArgs, "TWO"),
        Refusal(ToolErrors.AlreadyApproved, "THREE")));

      Assert.Equal(3, result.Steps);
      Assert.Equal(3, result.RefusedRequests);
      Assert.Equal("Dwight stopped: it asked twice to approve a build that this turn was already carrying out.", result.Note);   // the LAST refusal's class
    }

    /// <summary>SHAPE 3. When the bound does end the turn the user line names the LAST refusal's reason, not the generic
    /// "left out something Dwight needed" - classified from the refusal's structure, never its message (#235).</summary>
    [Fact]
    public void The_user_line_names_the_last_refusals_reason()
    {
      var detail = new System.Text.Json.Nodes.JsonObject { ["typesThatAcceptState"] = new System.Text.Json.Nodes.JsonArray("Number") };
      var note = AgentLoopCore.RefusedRequestNote("{\"kind\":\"place\"}",
        Refusal(ToolErrors.InvalidArgs, "MODEL-ADDRESSED state sentence", detail));
      Assert.Contains("asked to give a component a value it cannot hold", note);
      Assert.DoesNotContain("left out something", note);
      Assert.DoesNotContain("MODEL-ADDRESSED", note);

      var ports = new System.Text.Json.Nodes.JsonObject { ["reason"] = BuildSpecParser.NoSuchPortReason, ["available"] = new System.Text.Json.Nodes.JsonArray("P (Plane)") };
      Assert.Contains("named a connection point that is not on that component",
        AgentLoopCore.RefusedRequestNote("{\"kind\":\"place\"}", Refusal(ToolErrors.InvalidArgs, "x", ports)));

      // positive control: an invalid_args refusal with no classifiable structure keeps the generic sentence
      Assert.Contains("left out something Dwight needed",
        AgentLoopCore.RefusedRequestNote("{\"kind\":\"place\"}", Refusal(ToolErrors.InvalidArgs, "x")));
    }

    [Fact]
    public void Refusal_identity_is_the_code_and_the_message()
    {
      Assert.Equal(AgentLoopCore.RefusalIdentity(Refusal("a", "m")), AgentLoopCore.RefusalIdentity(Refusal("a", "m")));
      Assert.NotEqual(AgentLoopCore.RefusalIdentity(Refusal("a", "m")), AgentLoopCore.RefusalIdentity(Refusal("a", "n")));
      Assert.NotEqual(AgentLoopCore.RefusalIdentity(Refusal("a", "m")), AgentLoopCore.RefusalIdentity(Refusal("b", "m")));
    }

    /// <summary>A turn that ends on the bound says why, in the plugin's voice - otherwise an empty reply
    /// would read "Dwight had nothing to say to that" over a turn that tried twice and was refused.</summary>
    [Fact]
    public void The_bound_says_why_the_turn_stopped_and_never_relays_the_refusal()
    {
      var provider = new ScriptedProvider(
        Calls("t1", AgentHostTools.RequestMutation, "{\"kind\":\"build\",\"why\":\"x\"}"),
        Calls("t2", AgentHostTools.RequestMutation, "{\"kind\":\"build\",\"why\":\"again\"}"));

      var result = Drive(provider, call => Refused);

      Assert.StartsWith("Dwight stopped:", result.Note);
      Assert.DoesNotContain("REFUSAL-TEXT-FOR-THE-MODEL", result.Note);
    }

    /// <summary>Only a request counts toward the bound. A catalogue tool that refuses is an ordinary
    /// result the model works with, as it always was.</summary>
    [Fact]
    public void A_refused_catalogue_tool_does_not_count_toward_the_bound()
    {
      var provider = new ScriptedProvider(
        Calls("t1", AgentToolCatalog.GetOverview, "{}"),
        Calls("t2", AgentToolCatalog.GetOverview, "{}"),
        Says("Done."));

      var result = Drive(provider, call => Refused);

      Assert.Equal(3, result.Steps);
      Assert.Equal(0, result.RefusedRequests);
      Assert.Null(result.Note);
    }

    /// <summary>
    /// #611d THROUGH THE LOOP: the approved build turn asks for its own approval, reads the pointer, and its
    /// next step calls build_region. The request is answered by the product's own parse - only the canvas
    /// half of the host (region validation, the card) is left out, and a same-kind request never reaches it.
    /// </summary>
    [Fact]
    public void The_approved_kind_refusal_is_read_and_the_next_step_calls_the_tool()
    {
      var provider = new ScriptedProvider(
        Calls("t1", AgentHostTools.RequestMutation, "{\"kind\":\"build\",\"why\":\"Raising the build now\",\"regions\":[\"A1\"]}"),
        Calls("t2", AgentToolCatalog.BuildRegion, "{\"region\":\"A1\"}"),
        Says("A1 is built."));

      var dispatched = new List<string>();
      var result = Drive(provider, call =>
      {
        dispatched.Add(call.ToolName);
        if (call.ToolName != AgentHostTools.RequestMutation) return "{\"ok\":true,\"result\":{\"built\":\"A1\"}}";

        return InAnA1BuildTurn(call);
      });

      Assert.Equal(new[] { AgentHostTools.RequestMutation, AgentToolCatalog.BuildRegion }, dispatched);
      Assert.Contains(provider.ToolResultsSeen[1], t => t.Contains("Call build_region now"));
      Assert.Equal(1, result.RefusedRequests);
      Assert.Equal(1, result.ActingCalls);
      Assert.Equal("A1 is built.", result.Text);
    }

    /// <summary>
    /// #617 THROUGH THE LOOP: inside the A1 turn the model offers A2, reads that A2 needs its own approval and
    /// that A1 is its to build, and its next step builds A1 - not A2, and not a second card.
    /// </summary>
    [Fact]
    public void The_outside_set_refusal_is_read_and_the_next_step_builds_the_approved_region()
    {
      var provider = new ScriptedProvider(
        Calls("t1", AgentHostTools.RequestMutation, "{\"kind\":\"build\",\"why\":\"A2 next\",\"regions\":[\"A2\"]}"),
        Calls("t2", AgentToolCatalog.BuildRegion, "{\"region\":\"A1\"}"),
        Says("A1 is built. A2 needs your approval separately."));

      var dispatched = new List<string>();
      var result = Drive(provider, call =>
      {
        dispatched.Add(call.ToolName + " " + call.ToolInputJson);
        return InAnA1BuildTurn(call);
      });

      Assert.Equal(2, dispatched.Count);
      Assert.StartsWith(AgentToolCatalog.BuildRegion + " ", dispatched[1]);
      Assert.Contains("\"A1\"", dispatched[1]);
      Assert.Contains(provider.ToolResultsSeen[1], t => t.Contains("A2 needs its own approval"));
      Assert.DoesNotContain(provider.ToolResultsSeen[1], t => t.Contains("already approved"));
      Assert.Equal(1, result.RefusedRequests);
    }

    /// <summary>
    /// #626 THROUGH THE LOOP: A1's attempts are spent, the model asks to rebuild A1, and the request is put to
    /// the user as a card - which ends the turn - instead of pointing it at a tool that would refuse.
    /// </summary>
    [Fact]
    public void An_exhausted_rebuild_request_shows_a_card_and_ends_the_turn()
    {
      var provider = new ScriptedProvider(
        Calls("t1", AgentHostTools.RequestMutation,
              "{\"kind\":\"build\",\"why\":\"I wired the count into the step port - rebuild A1\",\"regions\":[\"A1\"]}"),
        Says("this step must never be requested"));

      var result = Drive(provider, call =>
      {
        var args = AgentToolCore.ParseArgs(call.ToolInputJson, out _);
        return AgentHostTools.TryParseRequest(args, new MutationRequestLog(), "stamp", "build", new[] { "A1" },
          r => r == "A1", out _, out _, out _, out var error) ? CardShown : error.ToJson();
      });

      Assert.Equal(1, result.Steps);
      Assert.True(result.AskedForConsent);
      Assert.Equal(0, result.RefusedRequests);
    }

    /// <summary>
    /// #625 THROUGH THE LOOP: the repair loop at the boundary. A rebuild of A1 that does not reconnect B1's XY Plane
    /// is refused with the cut list and the way out; the model reads it, and its next build_region declares the
    /// reconnection and passes the same boundary check. The spec parse and the boundary match are the product's
    /// own functions - only the canvas read of the cuts is a fixture.
    /// </summary>
    [Fact]
    public void A_boundary_refusal_is_read_and_the_next_build_declares_the_reconnection()
    {
      var provider = new ScriptedProvider(
        Calls("t1", AgentToolCatalog.BuildRegion,
              "{\"region\":\"A1\",\"components\":[{\"ref\":\"pt\",\"type\":\"Construct Point\"}]}"),
        Calls("t2", AgentToolCatalog.BuildRegion,
              "{\"region\":\"A1\",\"components\":[{\"ref\":\"pt\",\"type\":\"Construct Point\"}]," +
              "\"reconnect\":[{\"to\":{\"id\":19,\"param\":\"Origin\"},\"from\":{\"ref\":\"pt\",\"param\":\"Point\"}}]}"),
        Says("A1 is rebuilt, and B1's XY Plane is still fed."));

      var cut = new CutWire
      {
        ConsumerGuid = Guid.NewGuid(), ConsumerId = 19, ConsumerType = "XY Plane", ConsumerRegion = "B1",
        InputGuid = Guid.NewGuid(), InputLabel = "Origin", Position = 0,
        SourceGuid = Guid.NewGuid(), SourceType = "Construct Point", SourceOutputLabel = "Point",
      };

      var result = Drive(provider, call =>
      {
        var args = AgentToolCore.ParseArgs(call.ToolInputJson, out _);
        if (!BuildSpecParser.TryParse(args, _ => true, _ => new string[0], id => IdState.Alive, out var spec, out var parseError))
          return parseError.ToJson();
        return RebuildBoundary.Match(spec.Region, new[] { cut }, spec.Reconnects, out _, out var boundary)
          ? "{\"ok\":true,\"result\":{\"status\":\"applied\"}}"
          : boundary.ToJson();
      });

      Assert.Equal(3, result.Steps);
      Assert.Contains(provider.ToolResultsSeen[1], t => t.Contains("boundary_cut") && t.Contains("XY Plane (19) in B1 input Origin"));
      Assert.Contains(provider.ToolResultsSeen[1], t => t.Contains("tell the user to delete the wire themselves first"));
      Assert.Contains(provider.ToolResultsSeen[2], t => t.Contains("\"applied\""));
      Assert.Equal(2, result.ActingCalls);
    }

    /// <summary>The host's request parse as a build turn approved for A1 runs it - the product's own
    /// function, with the canvas half (region validation, the card) left out because a same-kind request
    /// never reaches it.</summary>
    private static string InAnA1BuildTurn(LlmBlock call)
    {
      if (call.ToolName != AgentHostTools.RequestMutation) return "{\"ok\":true,\"result\":{\"built\":\"A1\"}}";

      var args = AgentToolCore.ParseArgs(call.ToolInputJson, out _);
      return AgentHostTools.TryParseRequest(args, new MutationRequestLog(), "stamp", "build", new[] { "A1" },
        out _, out _, out _, out var error) ? CardShown : error.ToJson();
    }

    [Fact]
    public void Asking_twice_for_the_approval_being_carried_out_is_named_as_that()
    {
      var note = AgentLoopCore.RefusedRequestNote("{\"kind\":\"build\"}", Envelope(ToolErrors.AlreadyApproved));
      Assert.Equal("Dwight stopped: it asked twice to approve a build that this turn was already carrying out.", note);
    }

    [Fact]
    public void Asking_twice_for_regions_outside_the_approval_is_named_as_that()
    {
      var note = AgentLoopCore.RefusedRequestNote("{\"kind\":\"build\"}", Envelope(ToolErrors.RegionNotApproved));
      Assert.Equal("Dwight stopped: it asked twice to approve regions outside the build you approved.", note);
    }

    // ---- #615: the user-facing note, from the refusal's CLASS ---------------------------------------

    private static string Envelope(string code, string reason = null) =>
      ToolResult.Fail(code, "MODEL-ADDRESSED: say plainly that you cannot, and call build_region now.",
        reason == null ? null : new System.Text.Json.Nodes.JsonObject { ["reason"] = reason }).ToJson();

    [Fact]
    public void Regions_that_are_not_on_the_canvas_are_named_as_that()
    {
      var note = AgentLoopCore.RefusedRequestNote("{\"kind\":\"build\"}", Envelope(ToolErrors.UnknownRegion));
      Assert.Contains("regions that are not on the canvas", note);
    }

    [Fact]
    public void A_re_run_that_already_ran_unchanged_is_named_as_that()
    {
      var note = AgentLoopCore.RefusedRequestNote("{\"kind\":\"solve\"}",
        Envelope(ToolErrors.PreconditionRefused, AgentHostTools.RepeatUnchangedReason));
      Assert.Contains("a re-run that had already run without changing anything", note);
    }

    [Fact]
    public void Any_other_refusal_gets_the_generic_sentence()
    {
      var note = AgentLoopCore.RefusedRequestNote("{\"kind\":\"reorganise\"}", Envelope("a_code_nobody_mapped"));
      Assert.Contains("neither request could be put to you", note);
      Assert.Contains("a Reorganise", note);
    }

    /// <summary>#907/#908 (Tharun's U20 sitting): a request refused as malformed twice ended a turn with the
    /// fully generic sentence above, which told him nothing beyond "it stopped" - the same gap U20's own row
    /// 3c is about, one layer up. invalid_args now gets its own class-level sentence: Dwight's own words for
    /// WHAT KIND OF THING went wrong, never the refusal's raw message (#235 - nothing written for the model
    /// reaches the user; see <see cref="AgentHostTools.RequestMutation"/>'s refusal sites for why that
    /// message cannot simply be shown instead).</summary>
    [Fact]
    public void Asking_twice_with_something_missing_from_the_request_is_named_as_that()
    {
      var note = AgentLoopCore.RefusedRequestNote("{\"kind\":\"edit_script\"}", Envelope(ToolErrors.InvalidArgs));
      Assert.Contains("left out something Dwight needed", note);
      Assert.DoesNotContain("neither request could be put to you", note);
    }

    /// <summary>TWO VOICES (#235): the note is the plugin speaking to the user. Nothing written for the
    /// model reaches it - not the refusal's message, and not a kind the model made up.</summary>
    [Theory]
    [InlineData(ToolErrors.UnknownRegion, null)]
    [InlineData(ToolErrors.PreconditionRefused, AgentHostTools.RepeatUnchangedReason)]
    [InlineData(ToolErrors.InvalidArgs, null)]
    [InlineData(ToolErrors.AlreadyApproved, null)]
    [InlineData(ToolErrors.RegionNotApproved, null)]
    [InlineData("a_code_nobody_mapped", null)]
    public void No_model_addressed_sentence_reaches_the_note(string code, string reason)
    {
      var note = AgentLoopCore.RefusedRequestNote("{\"kind\":\"<b>delete everything</b>\"}", Envelope(code, reason));

      Assert.StartsWith("Dwight stopped:", note);
      Assert.DoesNotContain("MODEL-ADDRESSED", note);
      Assert.DoesNotContain("say plainly", note, StringComparison.OrdinalIgnoreCase);
      Assert.DoesNotContain("build_region", note);
      Assert.DoesNotContain("delete everything", note);
    }

    [Fact]
    public void An_unreadable_refusal_still_gets_a_sentence()
    {
      Assert.StartsWith("Dwight stopped:", AgentLoopCore.RefusedRequestNote(null, "not json"));
    }

    // ---- #610's discriminator: the dispatch log line ------------------------------------------------

    [Fact]
    public void The_dispatch_line_says_whether_a_request_showed_a_card_or_was_refused()
    {
      Assert.Equal("tool request_mutation ok=true cardShown=true",
        AgentLoopCore.DispatchLogLine(AgentHostTools.RequestMutation, CardShown));
      Assert.Equal("tool request_mutation ok=false code=precondition_refused cardShown=false",
        AgentLoopCore.DispatchLogLine(AgentHostTools.RequestMutation, Refused));
      Assert.Equal("tool get_overview ok=true",
        AgentLoopCore.DispatchLogLine(AgentToolCatalog.GetOverview, "{\"ok\":true,\"result\":{}}"));
    }

    // ---- the loop's other behaviours, which the extraction must not move -----------------------------

    [Fact]
    public void A_reply_with_no_tool_call_completes_in_one_step()
    {
      var provider = new ScriptedProvider(Says("Here is the map."));

      var result = Drive(provider, call => throw new InvalidOperationException("no tool should run"));

      Assert.Equal(1, result.Steps);
      Assert.Equal(TurnOutcome.Complete, result.Outcome);
      Assert.Equal("Here is the map.", result.Text);
    }

    [Fact]
    public void A_catalogue_tool_result_is_fed_back_and_the_loop_continues()
    {
      var provider = new ScriptedProvider(
        Calls("t1", AgentToolCatalog.GetOverview, "{}"),
        Says("Done."));

      var result = Drive(provider, call => "{\"ok\":true,\"result\":{\"marker\":\"OVERVIEW\"}}");

      Assert.Equal(2, result.Steps);
      Assert.Equal(1, result.ToolCalls);
      Assert.Contains(provider.ToolResultsSeen[1], t => t.Contains("OVERVIEW"));
    }

    [Fact]
    public void The_step_budget_still_bounds_a_turn_that_keeps_calling_tools()
    {
      var provider = new ScriptedProvider(
        Calls("t1", AgentToolCatalog.GetOverview, "{}"),
        Calls("t2", AgentToolCatalog.GetOverview, "{}"),
        Calls("t3", AgentToolCatalog.GetOverview, "{}"));

      var result = Drive(provider, call => "{\"ok\":true,\"result\":{}}", maxSteps: 2);

      Assert.Equal(TurnOutcome.Exhausted, result.Outcome);
      Assert.Equal(2, result.Steps);
    }
  }
}
