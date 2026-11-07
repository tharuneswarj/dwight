using System.Collections.Generic;
using System.Linq;
using Dwight.Agent;
using Dwight.Llm;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// The multi-tool loop's decisions (WP-3 Stage A). The two that matter most are what a turn may CALL
  /// (#220 - the capability gate, which is ADR-075's whole mechanism) and when a turn must STOP (#223 -
  /// bounded, and honest about what it produced).
  /// </summary>
  public class AgentLoopCoreTests
  {
    private static LlmMessage ToolResults(params (string id, string text)[] results)
    {
      var m = new LlmMessage { Role = LlmRole.User };
      foreach (var r in results) m.Blocks.Add(LlmBlock.ToolResult(r.id, r.text));
      return m;
    }

    // ---- #657/#667: the backstop banner above prose that followed no build ------------------------------

    private static TurnResult Ended(string text, int buildRegionCalls = 0) =>
      new TurnResult { Outcome = TurnOutcome.Complete, Text = text, ApprovedToolCalls = buildRegionCalls };

    /// <summary>
    /// THE BACKSTOP (#657). If the turn still ends with no build dispatched, a plugin line sits ABOVE the model's prose -
    /// the model's text is never edited (#235). The sentence is #667's neutral form, adopted verbatim because a plugin line
    /// above prose must be true whatever the prose says: a fabricated "B1 rebuilt" and an honest "I cannot build B1".
    /// </summary>
    [Fact]
    public void A_build_turn_that_called_no_build_region_gets_the_banner_above_its_prose()
    {
      var banner = AgentLoopCore.NoBuildBanner(TurnCapability.Build, Ended("B1 rebuilt: radius now 8."), new[] { "B1" });

      Assert.Equal("<strong>No build ran in this turn:</strong> build_region was never called for B1, so nothing on the " +
                   "canvas changed. Read the message below with that in mind.", banner);
    }

    [Fact]
    public void The_banner_is_the_same_true_sentence_over_an_honest_message()
    {
      Assert.Equal(
        AgentLoopCore.NoBuildBanner(TurnCapability.Build, Ended("B1 rebuilt."), new[] { "B1" }),
        AgentLoopCore.NoBuildBanner(TurnCapability.Build, Ended("I cannot build B1: the plan has no radius."), new[] { "B1" }));
    }

    /// <summary>"Nothing on the canvas changed" is true only when NO build_region ran at all: an unaddressed build lands
    /// components. So any build_region call - even one the repair did not count - means no banner; the close reports it.</summary>
    [Fact]
    public void No_banner_when_any_build_region_call_was_dispatched()
    {
      Assert.Null(AgentLoopCore.NoBuildBanner(TurnCapability.Build, Ended("B1 is built.", buildRegionCalls: 1), new[] { "B1" }));
    }

    [Theory]
    [InlineData((int)TurnCapability.ReadOnly)]
    [InlineData((int)TurnCapability.ReadAndSolve)]
    [InlineData((int)TurnCapability.Scaffold)]
    public void No_banner_on_a_turn_that_is_not_an_approved_build(int capability)
    {
      Assert.Null(AgentLoopCore.NoBuildBanner((TurnCapability)capability, Ended("B1 rebuilt."), new[] { "B1" }));
    }

    /// <summary>With no prose there is nothing for the banner to sit above - the close already speaks.</summary>
    [Fact]
    public void No_banner_when_there_is_no_prose()
    {
      Assert.Null(AgentLoopCore.NoBuildBanner(TurnCapability.Build, Ended(""), new[] { "B1" }));
      Assert.Null(AgentLoopCore.NoBuildBanner(TurnCapability.Build, Ended(null), new[] { "B1" }));
    }

    [Fact]
    public void The_banner_names_only_approved_addresses_and_carries_no_instruction_to_the_model()
    {
      var banner = AgentLoopCore.NoBuildBanner(TurnCapability.Build, Ended("done"), new[] { "B1", "<script>x</script>", "C2" });

      Assert.Contains("never called for B1, C2, so", banner);
      Assert.DoesNotContain("<script>", banner);
      Assert.DoesNotContain("Call it now", banner);
      Assert.DoesNotContain("PLUGIN FACT", banner);
    }

    // ---- #672: the empty-reply filler never talks over a build that ran ------------------------------------

    private static TurnResult Silent(int approvedBuildDispatches = 0, bool askedForConsent = false) =>
      new TurnResult
      {
        Outcome = TurnOutcome.Complete, Text = "",
        ApprovedBuildDispatches = approvedBuildDispatches, ApprovedToolCalls = approvedBuildDispatches,
        AskedForConsent = askedForConsent,
      };

    /// <summary>
    /// THE FINDING (#672). #666 clears the pre-repair prose, so a repaired turn that builds and then writes nothing reached
    /// "Dwight had nothing to say to that." directly above a verified build and its running lines - the EXPECTED rendering
    /// of a successful repair. An approved build that dispatched is spoken for by its running lines and the close.
    /// </summary>
    [Fact]
    public void A_silent_build_turn_that_dispatched_an_approved_build_shows_no_filler()
    {
      Assert.False(AgentLoopCore.ShowEmptyReplyFiller(TurnCapability.Build, Silent(approvedBuildDispatches: 1)));
    }

    /// <summary>A turn that is not an approved build keeps the filler when it says nothing - including one carrying a
    /// dispatch count, so the exception is the BUILD capability's, not the count's alone.</summary>
    [Theory]
    [InlineData((int)TurnCapability.ReadOnly, 0)]
    [InlineData((int)TurnCapability.ReadAndSolve, 0)]
    [InlineData((int)TurnCapability.Scaffold, 0)]
    [InlineData((int)TurnCapability.ReadOnly, 1)]
    public void A_silent_turn_that_is_not_an_approved_build_still_shows_the_filler(int capability, int dispatches)
    {
      Assert.True(AgentLoopCore.ShowEmptyReplyFiller((TurnCapability)capability, Silent(approvedBuildDispatches: dispatches)));
    }

    /// <summary>A build turn that dispatched nothing and said nothing: the filler is TRUE there, and the close says what
    /// was not attempted (#672).</summary>
    [Fact]
    public void A_silent_build_turn_with_no_dispatch_still_shows_the_filler()
    {
      Assert.True(AgentLoopCore.ShowEmptyReplyFiller(TurnCapability.Build, Silent()));
    }

    /// <summary>#256, now inside the function: a shown consent card is definite, and the filler never talks over it.</summary>
    [Fact]
    public void A_turn_that_showed_a_card_shows_no_filler()
    {
      Assert.False(AgentLoopCore.ShowEmptyReplyFiller(TurnCapability.Scaffold, Silent(askedForConsent: true)));
    }

    [Fact]
    public void A_turn_with_text_shows_no_filler()
    {
      Assert.False(AgentLoopCore.ShowEmptyReplyFiller(TurnCapability.ReadOnly, Ended("Here is the map.")));
      Assert.False(AgentLoopCore.ShowEmptyReplyFiller(TurnCapability.ReadOnly, null));
    }

    // ---- the capability gate (#220) ----------------------------------------------------------------

    /// <summary>The CATALOGUE half of what a capability offers - the canvas contract, separated from the
    /// host set so each source's rule can be asserted on its own terms (#230 Fork 1).</summary>
    private static IEnumerable<string> CanvasToolsFor(TurnCapability capability)
    {
      var host = AgentHostTools.All().Select(t => t.Name).ToHashSet();
      return AgentLoopCore.ToolsFor(capability).Select(t => t.Name).Where(n => !host.Contains(n)).OrderBy(n => n);
    }

    [Fact]
    public void A_read_only_turn_is_offered_exactly_the_tools_that_cannot_spend_solve_or_mutate()
    {
      var offered = AgentLoopCore.ToolsFor(TurnCapability.ReadOnly);

      Assert.NotEmpty(offered);
      // Holds across BOTH sources: the host tool is UI-only, so it satisfies this too.
      Assert.All(offered, t => Assert.False(t.Mutates, t.Name + " must not be offered to a read-only turn"));
      Assert.All(offered, t => Assert.False(t.Solves, t.Name + " must not be offered to a read-only turn"));

      // Its CANVAS half is exactly the catalogue's pure reads - derived from the catalogue's own facts,
      // not a hand-kept list that could drift from the surface it guards.
      var expected = AgentToolCatalog.All().Where(t => !t.Mutates && !t.Solves).Select(t => t.Name).OrderBy(n => n);
      Assert.Equal(expected, CanvasToolsFor(TurnCapability.ReadOnly));
    }

    [Fact]
    public void A_read_only_turn_may_not_call_the_tools_that_do_work()
    {
      // #222 Q6: solve_and_check is excluded for a sharper reason than latency - a solve is an
      // observable side effect on the user's document, and a read must never do work they did not ask
      // for. build_region is excluded because writing is what the consent card is FOR.
      Assert.False(AgentLoopCore.IsGranted(TurnCapability.ReadOnly, AgentToolCatalog.BuildRegion));
      Assert.False(AgentLoopCore.IsGranted(TurnCapability.ReadOnly, AgentToolCatalog.SolveAndCheck));
      Assert.True(AgentLoopCore.IsGranted(TurnCapability.ReadOnly, AgentToolCatalog.GetOverview));
    }

    [Fact]
    public void A_build_turn_may_call_the_working_tools()
    {
      Assert.True(AgentLoopCore.IsGranted(TurnCapability.Build, AgentToolCatalog.BuildRegion));
      Assert.True(AgentLoopCore.IsGranted(TurnCapability.Build, AgentToolCatalog.SolveAndCheck));
      Assert.True(AgentLoopCore.IsGranted(TurnCapability.Build, AgentToolCatalog.GetRegion));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("delete_everything")]
    [InlineData("build_regionn")]
    public void An_invented_tool_name_is_refused_at_every_capability(string invented)
    {
      // The loop asks before dispatching, so a hallucinated tool never reaches the surface - and a
      // near-miss name must not be helpfully resolved to the real thing (#208: exactly, or refuse).
      Assert.False(AgentLoopCore.IsGranted(TurnCapability.ReadOnly, invented));
      Assert.False(AgentLoopCore.IsGranted(TurnCapability.Build, invented));
    }

    [Fact]
    public void A_build_turn_grants_an_enumerated_set_rather_than_everything()
    {
      // A1: Build used to answer true to everything, so a tool added to the catalogue later was
      // auto-granted the moment it appeared - "may this turn write" decided by default rather than by
      // anyone. Its grant is now exactly the reads plus the two tools that do declared work.
      var expected = AgentToolCatalog.All()
        .Where(t => (!t.Mutates && !t.Solves) || t.Name == AgentToolCatalog.SolveAndCheck || t.Name == AgentToolCatalog.BuildRegion)
        .Select(t => t.Name)
        .OrderBy(n => n);

      Assert.Equal(expected, CanvasToolsFor(TurnCapability.Build));
    }

    [Fact]
    public void Every_catalogue_tool_is_granted_somewhere_or_declared_ungranted()
    {
      // THE CONFORMANCE TEST FOR CAPABILITY (A1), the same shape as #208's descriptor-parser test: a new
      // tool on the surface is granted NOWHERE until somebody decides, and this fails until they do -
      // rather than the tool quietly turning up on a turn because a grant answered true by default.
      var capabilities = (TurnCapability[])System.Enum.GetValues(typeof(TurnCapability));

      foreach (var tool in AgentToolCatalog.All())
      {
        var granted = capabilities.Any(c => AgentLoopCore.IsGranted(c, tool.Name));
        var declaredUngranted = AgentLoopCore.Ungranted
          .Any(n => string.Equals(n, tool.Name, System.StringComparison.OrdinalIgnoreCase));

        Assert.True(granted ^ declaredUngranted,
          tool.Name + " must be granted by exactly one of: some capability, or the explicit Ungranted list. " +
          "granted=" + granted + " declaredUngranted=" + declaredUngranted);
      }
    }

    [Fact]
    public void Everything_the_loop_offers_comes_from_a_declared_source()
    {
      // The durable form of "the loop is a CALLER of tools, never a tool": it can only ever advertise
      // what a published surface already defines, so the MCP face (ADR-069/098) and the chat agent cannot
      // drift into two different tool sets.
      //
      // AMENDED at Stage B (#230 Fork 1), deliberately and not by nudging: there are now TWO declared
      // sources - the CATALOGUE (what you can do to the canvas, and the MCP list_tools response) and the
      // HOST set (what you can do in this surface). The rule that matters is unchanged: two declared
      // sources, never an undeclared third.
      var known = AgentToolCatalog.All().Select(t => t.Name)
        .Concat(AgentHostTools.All().Select(t => t.Name))
        .ToHashSet();

      foreach (var capability in (TurnCapability[])System.Enum.GetValues(typeof(TurnCapability)))
        Assert.All(AgentLoopCore.ToolsFor(capability), t => Assert.Contains(t.Name, known));
    }

    // ---- the transcript bound (#209/#134) ----------------------------------------------------------

    [Fact]
    public void Elision_replaces_the_oldest_results_first_and_reports_how_many()
    {
      var messages = new List<LlmMessage>
      {
        LlmMessage.User("what does this do?"),
        ToolResults(("t1", new string('a', 400))),
        ToolResults(("t2", new string('b', 400))),
        ToolResults(("t3", new string('c', 400))),
      };

      var elided = AgentLoopCore.Elide(messages, 900);

      Assert.True(elided >= 1);
      Assert.Equal(AgentLoopCore.ElidedMarker, messages[1].Blocks[0].Text);      // oldest went first
      Assert.NotEqual(AgentLoopCore.ElidedMarker, messages[3].Blocks[0].Text);   // newest survived
      Assert.True(AgentLoopCore.Size(messages) <= 900);
    }

    [Fact]
    public void Elision_keeps_every_block_so_pairing_and_thinking_survive()
    {
      // Dropping a MESSAGE would break the tool_use/tool_result pairing the API requires and would drop
      // thinking blocks whose verbatim round-trip keeps a continuation valid (#192b). So elision must
      // hollow out a result's text and never remove a block.
      var assistant = new LlmMessage
      {
        Role = LlmRole.Assistant,
        Blocks =
        {
          LlmBlock.Thinking("", "sig-abc"),
          LlmBlock.ToolUse("t1", AgentToolCatalog.GetOverview, "{}"),
        },
      };
      var messages = new List<LlmMessage> { assistant, ToolResults(("t1", new string('x', 5000))) };

      AgentLoopCore.Elide(messages, 100);

      Assert.Equal(2, messages.Count);
      Assert.Equal(2, messages[0].Blocks.Count);
      Assert.Equal("sig-abc", messages[0].Blocks[0].Signature);          // signature intact
      Assert.Equal("t1", messages[0].Blocks[1].ToolUseId);               // tool_use intact
      Assert.Equal("t1", messages[1].Blocks[0].ToolUseId);               // its result still answers it
      Assert.Equal(AgentLoopCore.ElidedMarker, messages[1].Blocks[0].Text);
    }

    [Fact]
    public void Elision_stops_when_there_is_nothing_left_to_give()
    {
      // A transcript that is over budget with no tool results in it cannot be trimmed. It must return
      // rather than spin - the caller reports the overrun instead.
      var messages = new List<LlmMessage> { LlmMessage.User(new string('u', 5000)) };
      Assert.Equal(0, AgentLoopCore.Elide(messages, 100));
    }

    [Fact]
    public void An_already_elided_result_is_not_counted_twice()
    {
      var messages = new List<LlmMessage> { ToolResults(("t1", new string('a', 4000)), ("t2", new string('b', 4000))) };

      var first = AgentLoopCore.Elide(messages, 50);
      var second = AgentLoopCore.Elide(messages, 50);

      Assert.Equal(2, first);
      Assert.Equal(0, second);   // idempotent: a second pass finds nothing new to elide
    }

    // ---- the stop conditions (#223) ----------------------------------------------------------------

    private static TurnLimits Limits(int steps = 12, int wall = 300_000) =>
      new TurnLimits { MaxSteps = steps, WallClockMs = wall, MaxTranscriptChars = 120_000, AnswerBudget = 4_000 };

    [Fact]
    public void Preflight_stops_on_cancellation_before_anything_is_spent()
    {
      Assert.Equal(TurnOutcome.Stopped, AgentLoopCore.PreFlight(true, 0, 0, Limits()));
    }

    [Fact]
    public void Preflight_exhausts_at_the_step_cap_and_times_out_at_the_wall_clock()
    {
      Assert.Equal(TurnOutcome.Exhausted, AgentLoopCore.PreFlight(false, 12, 0, Limits(steps: 12)));
      Assert.Equal(TurnOutcome.TimedOut, AgentLoopCore.PreFlight(false, 0, 300_000, Limits(wall: 300_000)));
      Assert.Null(AgentLoopCore.PreFlight(false, 11, 299_999, Limits()));
    }

    [Fact]
    public void A_truncated_reply_is_its_own_outcome_even_when_it_asked_for_a_tool()
    {
      // #216d: a reply cut off at the token limit has no more to give, and must never be read as a
      // finished answer. It outranks "it wants a tool", because continuing would build on half a thought.
      Assert.Equal(TurnOutcome.Truncated, AgentLoopCore.PostResponse("max_tokens", hasToolUse: true));
      Assert.Equal(TurnOutcome.Truncated, AgentLoopCore.PostResponse("max_tokens", hasToolUse: false));
    }

    [Fact]
    public void A_reply_with_no_tool_call_completes_the_turn()
    {
      Assert.Equal(TurnOutcome.Complete, AgentLoopCore.PostResponse("end_turn", hasToolUse: false));
      Assert.Null(AgentLoopCore.PostResponse("tool_use", hasToolUse: true));
    }

    [Fact]
    public void Every_unhappy_outcome_says_what_happened_and_a_complete_one_says_nothing()
    {
      Assert.Null(AgentLoopCore.NoteFor(TurnOutcome.Complete, Limits(), true));

      foreach (var outcome in new[]
        { TurnOutcome.Exhausted, TurnOutcome.TimedOut, TurnOutcome.Truncated, TurnOutcome.Stopped, TurnOutcome.Failed })
        Assert.False(string.IsNullOrWhiteSpace(AgentLoopCore.NoteFor(outcome, Limits(), true)));
    }

    [Fact]
    public void An_exhausted_turn_offers_what_it_produced_rather_than_discarding_it()
    {
      var kept = AgentLoopCore.NoteFor(TurnOutcome.Exhausted, Limits(steps: 12), hasText: true);
      var empty = AgentLoopCore.NoteFor(TurnOutcome.Exhausted, Limits(steps: 12), hasText: false);

      Assert.Contains("12", kept);                       // says what the budget was
      Assert.Contains("Above is where it got to", kept); // and that the partial answer is kept
      Assert.DoesNotContain("Above is where", empty);    // never claims partial value it does not have
      Assert.Equal("Dwight used this turn's budget of 12 steps. Above is where it got to; ask again to carry on from there.", kept);
      Assert.Equal("Dwight used this turn's budget of 12 steps without finishing. Ask again to carry on.", empty);
    }

    // ---- the meta line -----------------------------------------------------------------------------

    [Fact]
    public void The_meta_line_states_the_elision_and_the_effort_step_down()
    {
      // Both are facts the user paid for and would otherwise never see - the WP-2 lesson (a witness
      // captured and not consulted is not a witness) applied to a turn.
      var meta = AgentLoopCore.Meta(new TurnResult
      {
        Steps = 3,
        ToolCalls = 4,
        ElidedToolResults = 2,
        PlanNote = "Effort stepped down from \"max\" to \"high\".",
      }, "Sonnet 5 · 6.2s · ~3c");

      Assert.Contains("Sonnet 5", meta);
      Assert.Contains("3 steps", meta);
      Assert.Contains("4 tool calls", meta);
      Assert.Contains("2 earlier result", meta);
      Assert.Contains("stepped down", meta);
    }

    [Fact]
    public void The_meta_line_stays_quiet_about_what_did_not_happen()
    {
      var meta = AgentLoopCore.Meta(new TurnResult { Steps = 1 }, "Haiku 4.5 · 0.8s");

      Assert.DoesNotContain("trimmed", meta);
      Assert.DoesNotContain("tool call", meta);
      Assert.Contains("1 step", meta);
    }
    // ---- U70: the empty reply after a consumed tool result ----------------------------------------------------

    [Fact]
    public void U70_A_turn_that_read_a_tool_result_and_wrote_no_text_says_so_instead_of_the_generic_filler()
    {
      var read = new TurnResult { Outcome = TurnOutcome.Complete, Text = "", Steps = 2, ToolCalls = 1 };
      Assert.True(AgentLoopCore.ShowEmptyReplyFiller(TurnCapability.ReadOnly, read));   // the line is still shown...
      Assert.Equal("The model returned no text after reading the tool result.", AgentLoopCore.EmptyReplyLine(read));
      Assert.DoesNotContain("nothing to say", AgentLoopCore.EmptyReplyLine(read));
    }

    [Fact]
    public void U70_A_turn_with_no_tool_call_keeps_the_generic_filler()
    {
      Assert.Equal("Dwight had nothing to say to that.", AgentLoopCore.EmptyReplyLine(Silent()));
      Assert.Equal("Dwight had nothing to say to that.", AgentLoopCore.EmptyReplyLine(null));
    }

    // ---- U71: the search tool leaves a line like the others ---------------------------------------------------

    [Fact]
    public void U71_The_client_search_tool_is_logged_in_the_host_tools_shape()
    {
      Assert.Equal("tool web_search ok=true",
        AgentLoopCore.DispatchLogLine(WebSearch.ToolName, "{\"ok\":true,\"result\":{\"results\":[]}}"));
      Assert.Equal("tool web_search ok=false code=precondition_refused",
        AgentLoopCore.DispatchLogLine(WebSearch.ToolName, ToolResult.Fail(ToolErrors.PreconditionRefused, "no").ToJson()));
    }

    [Fact]
    public void U71_A_provider_side_search_leaves_its_count_and_none_leaves_nothing()
    {
      Assert.Equal("search (provider-side) n=3", AgentLoopCore.SearchLogLine(3));
      Assert.Null(AgentLoopCore.SearchLogLine(0));
    }

    [Fact]
    public void U71_The_loop_hands_the_search_envelope_to_the_log_and_the_scaffold_logs_the_provider_count()
    {
      var root = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
      while (root != null && !System.IO.File.Exists(System.IO.Path.Combine(root.FullName, "Dwight.sln"))) root = root.Parent;
      var loop = System.IO.File.ReadAllText(System.IO.Path.Combine(root.FullName, "Dwight", "Agent", "AgentLoop.cs"));
      Assert.Contains("req.OnLog?.Invoke(AgentLoopCore.DispatchLogLine(call.ToolName, searchEnvelope))", loop);
      var panel = System.IO.File.ReadAllText(System.IO.Path.Combine(root.FullName, "Dwight", "UI", "DwightScaffold.cs"));
      Assert.Contains("AgentLoopCore.SearchLogLine(result.Usage?.WebSearchRequests ?? 0)", panel);
      Assert.Contains("AgentLoopCore.EmptyReplyLine(result)", panel);
    }
  }
}
