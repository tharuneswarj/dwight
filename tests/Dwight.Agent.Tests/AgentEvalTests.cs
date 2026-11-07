using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using Dwight.Agent;
using Dwight.AgentEval;
using Dwight.Llm;
using Dwight.Models;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// Brief BY: the console harness, over a fake provider. The loop under it is the product's own and is tested elsewhere; what is pinned here is
  /// what the harness adds: the fixture answers, the recorded card, the spend cap, the breaker, the key rule, and that every result says what it is not.
  /// </summary>
  [Collection(TurnShapeTests.StaticRecord)]
  public class AgentEvalTests
  {
    private sealed class Fake : ILlmProvider
    {
      private readonly Queue<LlmResponse> _replies;
      public Fake(bool forces, params LlmResponse[] replies) { SendsForcedTool = forces; _replies = new Queue<LlmResponse>(replies); }
      public string ModelId => "fake";
      public ProviderCapability Capability => default(ProviderCapability);
      public bool SendsForcedTool { get; }
      public readonly List<LlmRequest> Seen = new List<LlmRequest>();
      public Exception Throws;

      public LlmResponse Send(LlmRequest request, CancellationToken cancel = default)
      {
        Seen.Add(request);
        if (Throws != null) throw Throws;
        if (_replies.Count == 0) throw new InvalidOperationException("the script ran out of replies");
        var r = _replies.Dequeue();
        r.Usage = r.Usage ?? new LlmUsage { InputTokens = 100, OutputTokens = 10 };
        return r;
      }
    }

    private static LlmResponse Says(string prose) => AgentTurnDriverTests.Says(prose);
    private static LlmResponse Calls(string id, string tool, string args, string prose = null) => AgentTurnDriverTests.Calls(id, tool, args, prose);

    private static EvalRow Row(string mode = ApprovalMode.Ask, string onCard = "approve", params string[] turns) => new EvalRow
    {
      Id = "row-1", Persona = "a poor student", Provider = "Fake", Model = "fake", ApprovalMode = mode, OnCard = onCard,
      Turns = turns.Length > 0 ? turns.ToList() : new List<string> { "Help me with a wall." },
    };

    private static TurnLimits Limits(TurnCapability c) => new TurnLimits { MaxSteps = 8, WallClockMs = 0, MaxTranscriptChars = 1_000_000, AnswerBudget = 1000 };

    private static RowResult Run(ILlmProvider provider, EvalRow row = null, EvalFixture fixture = null, ComponentList list = null) =>
      new RowRunner(row ?? Row(), fixture ?? new EvalFixture(), list ?? new ComponentList(), provider, Limits, u => u.OutputTokens / 10.0, true, "medium", default).Run();

    private const string ScaffoldAsk = "{\"kind\":\"scaffold\",\"why\":\"lay out the wall\"}";
    private const string ScaffoldPlanArgs = "{\"regions\":[{\"ref\":\"a\",\"name\":\"Inputs\",\"purpose\":\"p\",\"gives\":\"g\",\"pseudocode\":\"c\"}]}";

    // ---- every result says what it is not -------------------------------------------------------------------

    [Fact]
    public void Every_result_and_transcript_opens_by_saying_it_did_not_run_in_Grasshopper()
    {
      var result = Run(new Fake(true, Says("A wall needs a curve.")));

      Assert.Contains("did not run in Grasshopper", result.DidNotRunInGrasshopper);
      Assert.Contains(result.NotReal, line => line.Contains("lane R"));
      var transcript = ResultWriter.Transcript(result);
      Assert.Contains(EvalNotReal.Statement, transcript);
      Assert.True(transcript.IndexOf(EvalNotReal.Statement, StringComparison.Ordinal) < transcript.IndexOf("Turn 1", StringComparison.Ordinal));
    }

    // ---- the real prompt, tools and loop ---------------------------------------------------------------------

    [Fact]
    public void The_model_is_sent_the_products_own_prompt_and_tools()
    {
      var provider = new Fake(true, Says("Hello."));
      Run(provider);

      var sent = provider.Seen.Single();
      Assert.StartsWith(TurnSystemText.Compose("", null, null, TurnCapability.ReadOnly, null).Substring(0, 400), sent.System);
      Assert.Contains("the canvas is EMPTY", sent.System);
      var expected = AgentLoopCore.ToolsFor(TurnCapability.ReadOnly, false);
      Assert.Equal(expected.Select(t => t.Name), sent.Tools.Select(t => t.Name));
      Assert.Equal(expected.Select(t => t.InputSchemaJson), sent.Tools.Select(t => t.InputSchemaJson));
    }

    // ---- fixture answers -------------------------------------------------------------------------------------

    [Fact]
    public void An_empty_canvas_is_answered_by_the_products_own_code()
    {
      var result = Run(new Fake(true, Calls("r1", AgentToolCatalog.GetOverview, "{}"), Says("It is empty.")));

      var tool = result.Turns[0].Tools.Single();
      Assert.Equal("real-core", tool.AnsweredBy);
      Assert.Contains("\"components\":0", tool.Answer);
    }

    [Fact]
    public void A_canned_world_answers_by_tool_and_arguments_and_a_miss_is_counted_as_the_instruments()
    {
      var fixture = EvalFixture.Parse("office", "{\"canned\":{" +
        "\"get_component\":[{\"argsContain\":\"\\\"id\\\":7\",\"envelope\":{\"ok\":true,\"result\":{\"name\":\"Seven\"}}}," +
                           "{\"envelope\":{\"ok\":true,\"result\":{\"name\":\"Other\"}}}]}}");
      var provider = new Fake(true,
        Calls("r1", AgentToolCatalog.GetComponent, "{\"id\":7}"),
        Calls("r2", AgentToolCatalog.GetComponent, "{\"id\":8}"),
        Calls("r3", AgentToolCatalog.GetSolveTiming, "{}"),
        Says("Done."));

      var result = Run(provider, fixture: fixture);

      var tools = result.Turns[0].Tools;
      Assert.Contains("Seven", tools[0].Answer);
      Assert.Contains("Other", tools[1].Answer);
      Assert.Equal("fixture", tools[0].AnsweredBy);
      Assert.Equal("fixture-miss", tools[2].AnsweredBy);
      Assert.Contains(EvalWorld.MissText, tools[2].Answer);
      Assert.DoesNotContain("test", tools[2].Answer);   // told it was a test, a real model stopped and sent its user off to open a document
      Assert.Equal(new[] { AgentToolCatalog.GetSolveTiming }, result.FixtureMisses);
    }

    [Fact]
    public void Kb_lookup_answers_from_the_saved_list_through_the_products_report_with_ports_unavailable()
    {
      var list = ComponentList.Parse("{\"components\":[{\"name\":\"Loft\",\"category\":\"Surface\",\"subCategory\":\"Freeform\",\"description\":\"Create a lofted surface\",\"guid\":\"" + Guid.NewGuid() + "\"}],\"libraries\":[]}");

      var result = Run(new Fake(true, Calls("r1", AgentToolCatalog.KbLookup, "{\"type\":\"Loft\"}"), Says("Yes.")), list: list);

      var tool = result.Turns[0].Tools.Single();
      Assert.Equal("real-core", tool.AnsweredBy);
      var answer = JsonNode.Parse(tool.Answer)["result"];
      Assert.Equal("Surface > Freeform > Loft", answer["qualified"].GetValue<string>());
      Assert.Equal("unavailable", answer["ports"].GetValue<string>());
      Assert.True(result.ComponentListLoaded);
    }

    [Fact]
    public void Without_the_list_kb_lookup_is_a_miss_and_the_result_says_the_list_was_not_loaded()
    {
      var result = Run(new Fake(true, Calls("r1", AgentToolCatalog.KbLookup, "{\"type\":\"Loft\"}"), Says("Hm.")));

      Assert.Equal("fixture-miss", result.Turns[0].Tools.Single().AnsweredBy);
      Assert.False(result.ComponentListLoaded);
    }

    /// <summary>Developer bridges on this machine are not what a user's Grasshopper holds, and a model must not be told it could use one.</summary>
    [Fact]
    public void The_developer_bridges_are_hidden_from_the_list()
    {
      var list = ComponentList.Parse("{\"components\":[" +
        "{\"name\":\"LAMCP Bridge\",\"category\":\"LAMCP\",\"subCategory\":\"Bridge\",\"guid\":\"" + Guid.NewGuid() + "\"}," +
        "{\"name\":\"Grasshopper MCP\",\"category\":\"Params\",\"subCategory\":\"Util\",\"guid\":\"" + Guid.NewGuid() + "\"}," +
        "{\"name\":\"Loft\",\"category\":\"Surface\",\"subCategory\":\"Freeform\",\"guid\":\"" + Guid.NewGuid() + "\"}]," +
        "\"libraries\":[{\"name\":\"GH_MCP\",\"version\":\"1\"},{\"name\":\"Dwight\",\"version\":\"1\"},{\"name\":\"Pufferfish\",\"version\":\"3\"}]}");

      Assert.Equal(new[] { "Loft" }, list.Types.Select(t => t.Name));
      Assert.Equal(new[] { "Pufferfish" }, list.Inventory().Select(l => l.Name));
    }

    [Fact]
    public void A_tool_that_does_not_exist_is_refused_in_the_products_words_and_recorded()
    {
      var result = Run(new Fake(true, Calls("r1", "search_plugins_online", "{}"), Says("I wish I had a tool for that, but I don't have a tool to search plugins.")));

      Assert.Equal(new[] { "search_plugins_online" }, result.UnknownToolsTried);
      var tool = result.Turns[0].Tools.Single();
      Assert.Equal(AgentLoopCore.GrantRefusal("search_plugins_online"), tool.Answer);
      Assert.Equal("refused", tool.AnsweredBy);
      Assert.NotEmpty(result.SaidItLacked);
    }

    // ---- cards -----------------------------------------------------------------------------------------------

    [Fact]
    public void A_card_under_ask_is_recorded_and_the_row_approves_it_as_a_click_and_the_map_is_recorded_not_placed()
    {
      var provider = new Fake(true,
        Calls("q1", AgentHostTools.RequestMutation, ScaffoldAsk),
        Calls("p1", AgentToolCatalog.ProposeScaffold, ScaffoldPlanArgs),
        Calls("m1", AgentToolCatalog.GetScaffoldMap, "{}"),
        Says("The map is down."));

      var result = Run(provider, Row(ApprovalMode.Ask, "approve"));

      var card = result.Turns[0].Cards.Single();
      Assert.Equal("scaffold", card.Kind);
      Assert.Equal("Ask", card.Decision);
      Assert.Equal("approved-by-row", card.Answer);
      Assert.Equal(AgentHostTools.CardShownResult("scaffold"), result.Turns[0].Tools.Single().Answer);
      Assert.Equal("Scaffold", result.Turns[1].Capability);
      Assert.Equal("recorded", result.Turns[1].Tools[0].AnsweredBy);
      Assert.Contains("Inputs", result.Turns[1].Tools[1].Answer);   // the next read sees the map the turn laid down
      Assert.Contains(provider.Seen[1].Messages.SelectMany(m => m.Blocks), b => b.Text == ApprovalMode.ClickUserText);
    }

    [Fact]
    public void Under_do_not_ask_the_mode_approves_it_and_says_so()
    {
      var provider = new Fake(true, Calls("q1", AgentHostTools.RequestMutation, ScaffoldAsk), Says("Nothing more."));

      var result = Run(provider, Row(ApprovalMode.Never, "decline"));

      var card = result.Turns[0].Cards.Single();
      Assert.Equal("Auto", card.Decision);
      Assert.Equal("approved-automatically", card.Answer);
      Assert.Contains(provider.Seen[1].Messages.SelectMany(m => m.Blocks), b => b.Text == ApprovalMode.AutoUserText);
    }

    // ---- Brief CD: the harness follows the panel's script door, or lane A measures the old rule ----

    private const string ScriptAsk = "{\"kind\":\"run_script\",\"why\":\"bake the curves\",\"source\":\"print(1)\"}";

    private static EvalRow ScriptRow(string mode, string onCard, bool fullControl, params string[] turns)
    {
      var row = Row(mode, onCard, turns);
      row.FullControl = fullControl;
      return row;
    }

    private static string AllSent(LlmRequest r) =>
      r.System + "\n" + string.Join("\n", r.Messages.SelectMany(m => m.Blocks).Select(b => b.Text ?? ""));

    [Fact]
    public void A_full_control_script_under_do_not_ask_runs_with_nobody_clicking_and_the_turn_carries_on()
    {
      var provider = new Fake(true, Calls("q1", AgentHostTools.RequestMutation, ScriptAsk), Says("It ran."));

      var result = Run(provider, ScriptRow(ApprovalMode.Never, "decline", true, "Bake the curves."));

      var card = result.Turns[0].Cards.Single();
      Assert.Equal(FullControl.Kind, card.Kind);
      Assert.Equal("Auto", card.Decision);
      Assert.Equal("approved-automatically", card.Answer);
      Assert.Equal("ReadOnly", result.Turns[1].Capability);
      Assert.Contains("which runs without asking when your turn ends", provider.Seen[0].System);

      var followUp = provider.Seen[1];
      Assert.Equal(LlmRole.User, followUp.Messages.Last().Role);
      Assert.Contains("[Run by Dwight automatically, under this chat's Do not ask mode]", AllSent(followUp));
      Assert.Contains(ApprovalMode.AutoUserText, AllSent(followUp));
      Assert.DoesNotContain("A CARD IS WAITING", AllSent(followUp));
    }

    [Theory]
    [InlineData(ApprovalMode.Ask)]
    [InlineData(ApprovalMode.ScriptsAndDeletions)]
    public void Under_a_mode_that_asks_the_script_waits_and_the_next_turn_is_told(string mode)
    {
      var provider = new Fake(true, Calls("q1", AgentHostTools.RequestMutation, ScriptAsk), Says("It is waiting on the card."));

      var result = Run(provider, ScriptRow(mode, "ignore", true, "Bake the curves.", "Build it."));

      var card = result.Turns[0].Cards.Single();
      Assert.Equal("Ask", card.Decision);
      Assert.Equal("ignored-by-row", card.Answer);
      Assert.Contains("which the user reads whole on a card before it runs", provider.Seen[0].System);
      Assert.Contains("A CARD IS WAITING", provider.Seen[1].System);
    }

    [Fact]
    public void Under_a_mode_that_asks_a_row_that_approves_is_a_click_and_says_so()
    {
      var provider = new Fake(true, Calls("q1", AgentHostTools.RequestMutation, ScriptAsk), Says("It ran."));

      var result = Run(provider, ScriptRow(ApprovalMode.Ask, "approve", true, "Bake the curves."));

      Assert.Equal("approved-by-row", result.Turns[0].Cards.Single().Answer);
      Assert.Contains("[Run by Dwight on the user's approval]", AllSent(provider.Seen[1]));
    }

    [Fact]
    public void With_full_control_off_there_is_no_door_under_any_mode()
    {
      var provider = new Fake(true, Calls("q1", AgentHostTools.RequestMutation, ScriptAsk), Says("I cannot run scripts here."));

      var result = Run(provider, ScriptRow(ApprovalMode.Never, "approve", false, "Bake the curves."));

      Assert.Empty(result.Turns[0].Cards);
      Assert.Equal("refused", result.Turns[0].Tools.Single().AnsweredBy);
    }

    [Fact]
    public void Every_result_says_a_script_did_not_run()
    {
      var result = Run(new Fake(true, Says("Nothing to do.")));
      Assert.Contains(result.NotReal, line => line.Contains("Full control script did not run"));
    }

    // ---- Brief CL: the harness reads the line as the panel does, or lane A counts a false one ----

    private const string QuotesItsCard = "Submitted as instructed.";

    [Fact]
    public void A_map_turn_draws_no_line_for_the_card_that_granted_it()
    {
      var provider = new Fake(false, Calls("q1", AgentHostTools.RequestMutation, ScaffoldAsk), Calls("p1", AgentToolCatalog.ProposeScaffold, ScaffoldPlanArgs), Says(QuotesItsCard));

      var result = Run(provider, Row(ApprovalMode.Never, "decline", "Lay it out."));

      Assert.Equal("Scaffold", result.Turns[1].Capability);
      Assert.DoesNotContain(result.Turns.SelectMany(t => t.PluginLines), l => l.Contains("no card was raised") || l.Contains("no new card was raised"));
    }

    [Fact]
    public void A_follow_up_to_an_approval_says_nothing_further_was_asked_on_both_channels()
    {
      var provider = new Fake(false, Calls("q1", AgentHostTools.RequestMutation, ScriptAsk), Says(QuotesItsCard), Says("Understood."));

      var result = Run(provider, ScriptRow(ApprovalMode.Never, "decline", true, "Bake the curves.", "And now?"));

      Assert.Contains(AgentLoopCore.NothingFurtherAskedSentence, result.Turns[1].PluginLines);
      Assert.DoesNotContain(AgentLoopCore.NothingAskedSentence, result.Turns[1].PluginLines);
      Assert.Contains(provider.Seen[2].Messages.SelectMany(m => m.Blocks), b => b.Text != null && b.Text.Contains("no new card was raised"));
    }

    [Fact]
    public void A_typed_message_after_it_is_a_new_exchange_with_the_old_line()
    {
      var provider = new Fake(false, Calls("q1", AgentHostTools.RequestMutation, ScriptAsk), Says("It ran."), Says(QuotesItsCard));

      var result = Run(provider, ScriptRow(ApprovalMode.Never, "decline", true, "Bake the curves.", "And now?"));

      Assert.Contains(AgentLoopCore.NothingAskedSentence, result.Turns[2].PluginLines);
    }

    [Fact]
    public void A_declined_card_starts_no_turn_and_the_next_user_turn_still_runs()
    {
      var provider = new Fake(true, Calls("q1", AgentHostTools.RequestMutation, ScaffoldAsk), Says("Understood."));

      var result = Run(provider, Row(ApprovalMode.Ask, "decline", "Lay it out.", "No, explain it instead."));

      Assert.Equal("declined-by-row", result.Turns[0].Cards.Single().Answer);
      Assert.Equal(2, result.Turns.Count);
      Assert.Equal("ReadOnly", result.Turns[1].Capability);
    }

    [Fact]
    public void The_products_parser_refuses_a_kind_that_does_not_exist_and_no_card_is_recorded()
    {
      var result = Run(new Fake(true, Calls("q1", AgentHostTools.RequestMutation, "{\"kind\":\"teleport\",\"why\":\"x\"}"), Says("Oh.")));

      Assert.Empty(result.Turns[0].Cards);
      Assert.Equal("refused", result.Turns[0].Tools.Single().AnsweredBy);
    }

    [Fact]
    public void A_build_card_for_a_region_that_is_not_there_is_refused_by_name()
    {
      var result = Run(new Fake(true, Calls("q1", AgentHostTools.RequestMutation, "{\"kind\":\"build\",\"why\":\"x\",\"regions\":[\"A1\"]}"), Says("Oh.")));

      Assert.Empty(result.Turns[0].Cards);
      Assert.Contains(ToolErrors.UnknownRegion, result.Turns[0].Tools.Single().Answer);
    }

    // ---- the forced follow-up, in the panel's order ------------------------------------------------------------

    [Fact]
    public void An_announcement_with_no_card_is_followed_by_a_forced_turn_that_raises_it()
    {
      var provider = new Fake(true, Says("I'll create a separate lamp scaffold first."), Calls("q1", AgentHostTools.RequestMutation, ScaffoldAsk), Says("Later."));

      var result = Run(provider, Row(ApprovalMode.Ask, "ignore"));

      Assert.Equal(2, result.Turns.Count);
      Assert.True(result.Turns[1].FollowUp);
      Assert.True(result.Turns[1].Forced);
      Assert.Equal(AgentHostTools.RequestMutation, provider.Seen[1].ForceToolName);
      Assert.Contains(AutoFollowUp.AnnouncementLine, result.Turns[0].PluginLines);
      Assert.Equal("ignored-by-row", result.Turns[1].Cards.Single().Answer);
    }

    [Fact]
    public void A_follow_up_with_no_card_withholds_its_prose_from_the_next_turn_and_says_the_plugins_sentence()
    {
      const string excuse = "I can't raise the build card from this turn.";
      var provider = new Fake(true, Says("I'll build it now."), Says(excuse), Says("Understood."));

      var result = Run(provider, Row(ApprovalMode.Ask, "approve", "Build it.", "Well?"));

      Assert.Equal(excuse, result.Turns[1].WithheldText);
      Assert.Null(result.Turns[1].Text);
      Assert.Contains(AutoFollowUp.NoCardLine, result.Turns[1].PluginLines);
      Assert.DoesNotContain(provider.Seen[2].Messages.SelectMany(m => m.Blocks), b => b.Text == excuse);
      Assert.Contains(provider.Seen[2].Messages.SelectMany(m => m.Blocks), b => b.Text == AutoFollowUp.NoCardLine);
    }

    [Fact]
    public void One_forced_try_per_user_message_and_a_new_message_buys_another()
    {
      var provider = new Fake(true, Says("I'll build it now."), Says("Still no."), Says("I'll build it now."), Says("Still no."), Says("I'll build it now."));

      var result = Run(provider, Row(ApprovalMode.Never, "approve", "One.", "Two."));

      Assert.Equal(new[] { false, true, false, true }, result.Turns.Select(t => t.FollowUp));
    }

    /// <summary>Brief CN, through the harness: a row that runs out of steps gets its closing step, and the next turn is told what it read. Otherwise lane A measures the old rule.</summary>
    [Fact]
    public void A_row_that_runs_out_of_steps_says_where_it_got_to_and_the_next_turn_is_told()
    {
      var script = Enumerable.Range(0, 8).Select(i => Calls("r" + i, AgentToolCatalog.GetOverview, "{}")).ToList();
      script.Add(Says("I read the overview and need to know which region you mean."));
      script.Add(Says("Understood."));
      script.Add(Says("And now the turn after a complete one."));
      var provider = new Fake(true, script.ToArray());

      var result = Run(provider, Row(ApprovalMode.Ask, "approve", "Add a check.", "Carry on.", "And again."));

      Assert.Equal("Exhausted", result.Turns[0].Outcome);
      Assert.True(result.Turns[0].ClosingStep);
      Assert.True(provider.Seen[8].ForbidTools);
      Assert.Contains("Above is where it got to", result.Turns[0].Note);

      Assert.Contains("your last turn ran out of its steps", provider.Seen[9].System);
      Assert.Contains("You read: get_overview ->", provider.Seen[9].System);
      Assert.DoesNotContain("your last turn ran out of its steps", provider.Seen[0].System);

      Assert.Equal("Complete", result.Turns[1].Outcome);
      Assert.DoesNotContain("your last turn ran out of its steps", provider.Seen[10].System);
    }

    // ---- lane A's first read of PR #179: what a turn was told must be in the record ----

    private static EvalRow TwoTurns() => Row(ApprovalMode.Ask, "approve", "Read it all.", "Carry on.");

    private static Fake RunsOut() => new Fake(true,
      Enumerable.Range(0, 8).Select(i => Calls("r" + i, AgentToolCatalog.GetOverview, "{}")).Concat(new[] { Says("I read the overview."), Says("Understood.") }).ToArray());

    [Fact]
    public void A_result_says_what_each_turn_was_told_and_that_it_went_out()
    {
      var result = Run(RunsOut(), TwoTurns());

      Assert.DoesNotContain(result.Turns[0].TurnStartFacts, f => f.Name == "what the last turn read");
      var told = Assert.Single(result.Turns[1].TurnStartFacts, f => f.Name == "what the last turn read");
      Assert.True(told.Sent);
      Assert.Contains("your last turn ran out of its steps", told.Text);
      Assert.Contains("You read: get_overview ->", told.Text);
      Assert.All(result.Turns.SelectMany(t => t.TurnStartFacts), f => Assert.True(f.Sent, f.Name));
      Assert.True(result.Turns[1].SystemChars > 0);

      var transcript = ResultWriter.Transcript(result);
      Assert.Contains("Told at the start (what the last turn read, in the system text): PLUGIN FACT", transcript);
      Assert.Contains("Outcome Exhausted, with its closing step,", transcript);
    }

    /// <summary>The witness must be able to say no, or it witnesses nothing (#212).</summary>
    [Fact]
    public void A_fact_missing_from_the_system_text_is_recorded_as_not_sent_and_a_fact_the_turn_lacks_is_left_out()
    {
      var facts = TurnStartFact.Of("system text holding ONE", ("one", " ONE "), ("two", "TWO"), ("none", null), ("blank", " "));

      Assert.Equal(new[] { "one", "two" }, facts.Select(f => f.Name));
      Assert.True(facts[0].Sent);
      Assert.False(facts[1].Sent);
      Assert.Contains("Told at the start (lost, NOT in the system text): LOST", ResultWriter.Transcript(new RowResult
      {
        RowId = "r", Turns = { new TurnRecord { Number = 1, TurnStartFacts = TurnStartFact.Of("x", ("lost", "LOST")) } },
      }));
    }

    /// <summary>The route to the wire, on every connection, with no call spent: the text a real row composed, through each adapter's own body.</summary>
    [Fact]
    public void What_the_last_turn_read_reaches_every_providers_wire()
    {
      var provider = RunsOut();
      Run(provider, TwoTurns());
      var request = new LlmRequest { System = provider.Seen[9].System, Messages = { LlmMessage.User("x") }, Tools = provider.Seen[9].Tools };
      const string Fact = "your last turn ran out of its steps";

      Assert.Contains(Fact, AnthropicRequestBody.Build(request, "claude-test", ThinkingWireState.Omit));
      Assert.Contains(Fact, OpenAIResponses.BuildBody(request, "model-test"));
      Assert.Contains(Fact, new GoogleProvider(null, "gemini-test").BuildBody(request, structured: false));
      Assert.Contains(Fact, new OpenAIProvider("https://example.invalid/v1", null, "m", ProviderCapability.StrictSchema, true, "Ollama Cloud").BuildBody(request, structured: false));
    }

    [Theory]
    [InlineData("4", 4)]
    [InlineData("12", 12)]
    [InlineData("40", 40)]
    public void The_steps_flag_takes_what_settings_can_offer(string text, int steps)
    {
      Assert.Equal(steps, EvalLimits.ParseSteps(text));
      Assert.Equal(steps, EvalLimits.For(TurnCapability.ReadOnly, steps).MaxSteps);
      Assert.Equal(steps, EvalLimits.For(TurnCapability.Scaffold, steps).MaxSteps);
      Assert.Equal(DwightConfig.AgentBuildMaxStepsPerTurn, EvalLimits.For(TurnCapability.Build, steps).MaxSteps);   // an approved build keeps its own, as in the product
    }

    [Theory]
    [InlineData("3")]
    [InlineData("41")]
    [InlineData("0")]
    [InlineData("-4")]
    [InlineData("12.5")]
    [InlineData("many")]
    [InlineData("")]
    public void The_steps_flag_refuses_what_settings_could_not_offer(string text)
    {
      var ex = Assert.Throws<ArgumentException>(() => EvalLimits.ParseSteps(text));
      Assert.Contains("--steps-per-turn takes a whole number from 4 to 40", ex.Message);
    }

    [Fact]
    public void With_no_flag_the_limits_are_the_products_and_the_result_states_the_number()
    {
      Assert.Equal(DwightConfig.AgentMaxStepsPerTurn, EvalLimits.For(TurnCapability.ReadOnly).MaxSteps);
      Assert.Equal(DwightConfig.AgentTurnWallClockMs, EvalLimits.For(TurnCapability.ReadOnly).WallClockMs);
      Assert.Equal(DwightConfig.AgentBuildWallClockMs, EvalLimits.For(TurnCapability.Build).WallClockMs);

      var result = Run(new Fake(true, Says("Hello.")), Row());   // this file's own limits say 8
      Assert.Equal(8, result.StepsPerTurn);
      Assert.Contains("Steps per turn: 8.", ResultWriter.Transcript(result));
    }

    [Fact]
    public void The_console_has_the_flag_and_hands_it_to_each_row()
    {
      var dir = new DirectoryInfo(AppContext.BaseDirectory);
      while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Dwight.sln"))) dir = dir.Parent;
      Assert.True(dir != null);
      var program = File.ReadAllText(Path.Combine(dir.FullName, "tools", "AgentEval", "Program.cs"));

      Assert.Contains("case \"--steps-per-turn\": o.StepsPerTurn = EvalLimits.ParseSteps(Next()); break;", program);
      Assert.Contains("c => EvalLimits.For(c, o.StepsPerTurn)", program);
      Assert.DoesNotContain("GetEnvironmentVariable", program);
      Assert.Contains("`--steps-per-turn`", File.ReadAllText(Path.Combine(dir.FullName, "tools", "AgentEval", "README.md")));
    }

    [Fact]
    public void A_connection_that_cannot_force_runs_no_follow_up_under_ask()
    {
      var result = Run(new Fake(false, Says("I'll build it now.")), Row(ApprovalMode.Ask));

      Assert.Single(result.Turns);
      Assert.Contains(AgentLoopCore.NothingAskedSentence, result.Turns[0].PluginLines);
    }

    [Fact]
    public void The_row_limit_stops_a_chain_and_says_so()
    {
      var row = Row(ApprovalMode.Ask, "approve", "One.", "Two.", "Three.");
      row.MaxTurns = 2;

      var result = Run(new Fake(true, Says("A."), Says("B."), Says("C.")), row);

      Assert.Equal(2, result.Turns.Count);
      Assert.Contains("limit of 2", result.EndedBecause);
    }

    // ---- the stated worlds -------------------------------------------------------------------------------------

    private static string FixtureText(string name)
    {
      var dir = new DirectoryInfo(AppContext.BaseDirectory);
      while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Dwight.sln"))) dir = dir.Parent;
      Assert.True(dir != null);
      return File.ReadAllText(Path.Combine(dir.FullName, "tools", "AgentEval", "fixtures", name + ".json"));
    }

    private static EvalFixture Shipped(string name) => EvalFixture.Parse(name, FixtureText(name));

    private static JsonNode Result(ToolRecord tool) => JsonNode.Parse(tool.Answer)["result"];

    [Theory]
    [InlineData("empty")]
    [InlineData("small-organised")]
    [InlineData("crowded-disorganised")]
    public void Every_shipped_fixture_says_where_it_came_from_and_the_result_carries_it(string name)
    {
      var fixture = Shipped(name);
      Assert.Contains("stated by hand", fixture.Origin, StringComparison.OrdinalIgnoreCase);

      var result = Run(new Fake(true, Says("Hello.")), fixture: fixture);

      Assert.Equal(fixture.Origin, result.FixtureOrigin);
      Assert.Contains(fixture.Origin, ResultWriter.Transcript(result));
    }

    [Fact]
    public void A_stated_canvas_is_read_by_the_products_own_projections()
    {
      var provider = new Fake(true,
        Calls("r1", AgentToolCatalog.GetOverview, "{}"),
        Calls("r2", AgentToolCatalog.GetRegion, "{\"id\":0}"),
        Calls("r3", AgentToolCatalog.Search, "{\"query\":\"Bay Width\"}"),
        Calls("r4", AgentToolCatalog.GetComponent, "{\"id\":1}"),
        Calls("r5", AgentToolCatalog.GetConnections, "{\"id\":1}"),
        Says("It is a fin facade."));

      var result = Run(provider, fixture: Shipped("small-organised"));

      var tools = result.Turns[0].Tools;
      Assert.All(tools, t => Assert.Equal("real-core", t.AnsweredBy));
      Assert.Equal(3, Result(tools[0])["regions"].AsArray().Count);
      Assert.Contains("the canvas holds 11 objects", provider.Seen[0].System);
      Assert.Contains("Bay Width", tools[2].Answer);
      Assert.Contains("Number Slider", tools[3].Answer);
      Assert.Empty(result.FixtureMisses);
    }

    [Fact]
    public void A_refusal_on_a_stated_canvas_is_the_products_own()
    {
      var result = Run(new Fake(true, Calls("r1", AgentToolCatalog.GetComponent, "{\"id\":999}"), Says("Oh.")), fixture: Shipped("small-organised"));

      var tool = result.Turns[0].Tools.Single();
      Assert.Equal("refused", tool.AnsweredBy);
      Assert.False(JsonNode.Parse(tool.Answer)["ok"].GetValue<bool>());
    }

    [Fact]
    public void The_crowded_canvas_reads_as_crowded_through_the_products_layout_report()
    {
      var result = Run(new Fake(true, Calls("r1", AgentToolCatalog.GetCanvasLayout, "{}"), Says("Crowded.")), fixture: Shipped("crowded-disorganised"));

      var layout = Result(result.Turns[0].Tools.Single());
      Assert.Equal(40, layout["ungrouped"].GetValue<int>());
      Assert.True(layout["overlapsTotal"].GetValue<int>() > 20);
      Assert.True(layout["backwardWiresTotal"].GetValue<int>() > 0);
    }

    [Fact]
    public void Kb_lookup_gives_ports_only_for_a_type_on_the_stated_canvas()
    {
      var list = ComponentList.Parse("{\"components\":[" +
        "{\"name\":\"Series\",\"category\":\"Sets\",\"subCategory\":\"Sequence\",\"guid\":\"" + Guid.NewGuid() + "\"}," +
        "{\"name\":\"Loft\",\"category\":\"Surface\",\"subCategory\":\"Freeform\",\"guid\":\"" + Guid.NewGuid() + "\"}],\"libraries\":[]}");

      var result = Run(new Fake(true, Calls("r1", AgentToolCatalog.KbLookup, "{\"types\":[\"Series\",\"Loft\"]}"), Says("Yes.")),
                       fixture: Shipped("small-organised"), list: list);

      var rows = Result(result.Turns[0].Tools.Single())["results"].AsArray();
      Assert.Contains("Step (N)", rows[0]["ports"].ToJsonString());
      Assert.Equal("unavailable", rows[1]["ports"].GetValue<string>());
    }

    // ---- a build leaves ids the next one can use ------------------------------------------------------------------

    private const string BuildA1 = "{\"region\":\"A1\",\"components\":[{\"ref\":\"r\",\"type\":\"Number Slider\",\"nick\":\"Radius\"}," +
                                   "{\"ref\":\"s\",\"type\":\"Python 3 Script\",\"source\":\"result = radius\",\"inputs\":[\"radius\"],\"outputs\":[\"result\"]}]," +
                                   "\"wires\":[{\"from\":{\"ref\":\"r\"},\"to\":{\"ref\":\"s\",\"param\":\"radius\"}}]}";

    private static List<LlmResponse> MapAndBuild(params LlmResponse[] onTheBuildTurn)
    {
      var replies = new List<LlmResponse>
      {
        Calls("q1", AgentHostTools.RequestMutation, ScaffoldAsk),
        Calls("p1", AgentToolCatalog.ProposeScaffold, ScaffoldPlanArgs),
        Says("The map is down."),
        Calls("q2", AgentHostTools.RequestMutation, "{\"kind\":\"build\",\"why\":\"build it\",\"regions\":[\"A1\"]}"),
      };
      replies.AddRange(onTheBuildTurn);
      return replies;
    }

    private static RowResult RunMapAndBuild(params LlmResponse[] onTheBuildTurn)
    {
      var row = Row(ApprovalMode.Never, "approve", "Lay it out.", "Now build it.");
      return Run(new Fake(true, MapAndBuild(onTheBuildTurn).ToArray()), row);
    }

    private static List<ToolRecord> BuildTurnTools(RowResult result) => result.Turns.Single(t => t.Capability == "Build").Tools;

    [Fact]
    public void A_recorded_build_answers_with_the_products_verdict_and_ids_a_later_read_finds()
    {
      var tools = BuildTurnTools(RunMapAndBuild(
        Calls("b1", AgentToolCatalog.BuildRegion, BuildA1),
        Calls("r1", AgentToolCatalog.GetComponent, "{\"id\":1}"),
        Calls("r2", AgentToolCatalog.GetConnections, "{\"id\":2}"),
        Calls("r3", AgentToolCatalog.GetOverview, "{}"),
        Says("Built.")));

      var verdict = Result(tools[0]);
      Assert.Equal("recorded", tools[0].AnsweredBy);
      Assert.Equal("applied", verdict["status"].GetValue<string>());
      Assert.Equal(new[] { 1, 2 }, verdict["placed"].AsArray().Select(i => i.GetValue<int>()));
      Assert.Equal(1, verdict["wired"].GetValue<int>());
      Assert.True(verdict["preserved"].GetValue<bool>());
      Assert.True(verdict["complete"].GetValue<bool>());
      Assert.Equal("real-core", tools[1].AnsweredBy);
      Assert.Contains("Number Slider", tools[1].Answer);
      Assert.Contains("\"from\":", tools[2].Answer.Replace(" ", ""));
      Assert.Equal(2, Result(tools[3])["counts"]["components"].GetValue<int>() + Result(tools[3])["counts"]["params"].GetValue<int>());
    }

    [Fact]
    public void A_second_region_can_wire_from_the_first_by_id()
    {
      const string second = "{\"region\":\"A1\",\"components\":[{\"ref\":\"p\",\"type\":\"Panel\"}],\"wires\":[{\"from\":{\"id\":1},\"to\":{\"ref\":\"p\"}}]}";
      var result = RunMapAndBuild(
        Calls("b1", AgentToolCatalog.BuildRegion, "{\"region\":\"B9\",\"components\":[{\"ref\":\"r\",\"type\":\"Number Slider\"}]}"),
        Calls("b2", AgentToolCatalog.BuildRegion, second),
        Says("Built."));

      var tools = BuildTurnTools(result);
      Assert.Equal("recorded", tools[1].AnsweredBy);
      Assert.Equal(1, Result(tools[1])["wired"].GetValue<int>());
    }

    [Fact]
    public void A_rebuild_replaces_what_the_last_build_of_that_region_placed_and_retires_its_ids()
    {
      var tools = BuildTurnTools(RunMapAndBuild(
        Calls("b1", AgentToolCatalog.BuildRegion, BuildA1),
        Calls("b2", AgentToolCatalog.BuildRegion, BuildA1),
        Calls("r1", AgentToolCatalog.GetComponent, "{\"id\":1}"),
        Says("Rebuilt.")));

      Assert.Equal(new[] { 3, 4 }, Result(tools[1])["placed"].AsArray().Select(i => i.GetValue<int>()));
      Assert.Equal("refused", tools[2].AnsweredBy);
    }

    /// <summary>Brief CA, in the harness: the prompt now invites a sourceless script, so a row must be answered as the product answers it.</summary>
    [Fact]
    public void A_sourceless_script_takes_the_cards_text_and_a_build_that_stopped_short_continues()
    {
      const string plan = "{\"regions\":[{\"ref\":\"a\",\"name\":\"Inputs\",\"purpose\":\"p\",\"gives\":\"g\",\"pseudocode\":\"c\"},{\"ref\":\"b\",\"name\":\"Form\",\"purpose\":\"p\",\"gives\":\"g\",\"pseudocode\":\"c\",\"dependsOn\":[\"a\"]}]}";
      const string card = "{\"kind\":\"build\",\"why\":\"build\",\"regions\":[\"A1\",\"B1\"],\"scripts\":[{\"region\":\"B1\",\"type\":\"Python 3 Script\",\"source\":\"result = radius\",\"inputs\":[\"radius\"],\"outputs\":[\"result\"]}]}";
      var provider = new Fake(true,
        Calls("q1", AgentHostTools.RequestMutation, ScaffoldAsk),
        Calls("p1", AgentToolCatalog.ProposeScaffold, plan),
        Says("The map is down."),
        Calls("q2", AgentHostTools.RequestMutation, card),
        Calls("b1", AgentToolCatalog.BuildRegion, "{\"region\":\"A1\",\"components\":[{\"ref\":\"r\",\"type\":\"Number Slider\"}]}"),
        Says("A1 is done."),
        Calls("b2", AgentToolCatalog.BuildRegion, "{\"region\":\"B1\",\"components\":[{\"ref\":\"s\",\"type\":\"Python 3 Script\"},{\"ref\":\"p\",\"type\":\"Panel\"}],\"wires\":[{\"from\":{\"ref\":\"s\",\"param\":\"result\"},\"to\":{\"ref\":\"p\"}}]}"),
        Says("B1 is done."));

      var result = Run(provider, Row(ApprovalMode.Never, "approve", "Lay it out.", "Build it."));

      var builds = result.Turns.Where(t => t.Capability == "Build").ToList();
      Assert.Equal(2, builds.Count);
      Assert.Contains(AutoFollowUp.ContinueLine(new[] { "B1" }), builds[0].PluginLines);
      Assert.Equal("recorded", builds[1].Tools.Single().AnsweredBy);
      Assert.Equal(1, Result(builds[1].Tools.Single())["wired"].GetValue<int>());
    }

    [Fact]
    public void The_products_build_parser_still_refuses()
    {
      var tools = BuildTurnTools(RunMapAndBuild(
        Calls("b1", AgentToolCatalog.BuildRegion, "{\"region\":\"A1\",\"components\":[]}"),
        Says("Oh."),
        Says("It was refused, so I will look again.")));

      Assert.Equal("refused", tools[0].AnsweredBy);
    }

    // ---- how often each provider announces with no card ------------------------------------------------------------

    [Theory]
    [InlineData("I'll create a separate lamp map first.", true, true, true)]          // both catches and plain words
    [InlineData("I'll structure this as a wavy shell.", false, false, true)]          // Sol's: only plain words see it
    [InlineData("Let me know which region to build.", true, false, true)]             // the line's catch alone
    [InlineData("A wall needs a curve.", false, false, false)]                        // nothing announced
    public void A_turn_records_how_each_measure_read_it(string prose, bool line, bool forcing, bool plain)
    {
      var result = Run(new Fake(false, Says(prose)), Row(ApprovalMode.Ask));

      var turn = result.Turns[0];
      Assert.Equal(line, turn.AnnouncedLineCatch);
      Assert.Equal(forcing, turn.AnnouncedForcingCatch);
      Assert.Equal(plain, turn.SaidWillNoCard);
      Assert.Equal(plain, turn.AnnouncedSentence != null);
    }

    [Fact]
    public void A_turn_that_asked_or_a_build_turn_records_none_of_them()
    {
      var result = RunMapAndBuild(Says("I'll build it."));

      Assert.All(result.Turns, t => Assert.False(t.AnnouncedForcingCatch && t.Tools.Any(x => x.Name == AgentHostTools.RequestMutation)));
      var build = result.Turns.Single(t => t.Capability == "Build");
      Assert.False(build.AnnouncedForcingCatch);
      Assert.False(build.SaidWillNoCard);
    }

    [Fact]
    public void The_run_totals_them_per_provider_and_model()
    {
      var sol = Run(new Fake(true, Says("I'll structure this."), Calls("q1", AgentHostTools.RequestMutation, ScaffoldAsk), Says("Later.")), Row(ApprovalMode.Ask, "ignore", "One.", "Two."));
      var forced = Run(new Fake(true, Says("I'll build it now."), Says("Still no.")), Row(ApprovalMode.Never));
      forced.Provider = sol.Provider;
      var other = Run(new Fake(true, Says("A wall needs a curve.")), Row());
      other.Model = "other";

      var rows = AnnouncingCounts.ByProvider(new[] { sol, forced, other });

      Assert.Equal(2, rows.Count);
      var fake = rows.Single(r => r.Model == "fake");
      Assert.Equal(3, fake.TurnsThatCouldAsk);
      Assert.Equal(1, fake.TurnsThatAsked);
      Assert.Equal(1, fake.NoCardForcingCatch);
      Assert.Equal(2, fake.NoCardPlainWords);
      Assert.Equal(1, fake.FollowUps);
      Assert.Equal(0, fake.FollowUpsThatRaisedACard);
      Assert.Equal(1, rows.Single(r => r.Model == "other").TurnsThatCouldAsk);
    }

    // ---- the list the product builds, through the harness -------------------------------------------------------------

    [Fact]
    public void A_plugin_of_user_objects_reaches_the_model_through_the_products_join()
    {
      var list = ComponentList.Parse("{\"components\":[{\"name\":\"HB Room\",\"category\":\"Honeybee\",\"subCategory\":\"0 :: Create\",\"guid\":\"" + Guid.NewGuid() +
                                     "\",\"library\":\"" + Guid.NewGuid() + "\",\"userObject\":true}],\"libraries\":[]}");

      var result = Run(new Fake(true, Calls("r1", AgentToolCatalog.GetInstalledPlugins, "{}"), Says("Yes.")), list: list);

      var row = Result(result.Turns[0].Tools.Single())["plugins"].AsArray().Single();
      Assert.Equal("Honeybee", row["name"].GetValue<string>());
      Assert.Equal(1, row["userObjects"].GetValue<int>());
    }

    // ---- the spend cap and the breaker ---------------------------------------------------------------------------

    private static GuardedProvider Guarded(ILlmProvider inner, RunGuard guard, string provider = "Fake") =>
      new GuardedProvider(inner, guard, provider, u => u.OutputTokens);

    [Fact]
    public void The_cap_stops_the_next_request_before_it_is_sent()
    {
      var guard = new RunGuard(5);
      var fake = new Fake(true, new LlmResponse { Usage = new LlmUsage { OutputTokens = 6 } }, Says("unreached"));
      var guarded = Guarded(fake, guard);

      guarded.Send(new LlmRequest());
      var ex = Assert.Throws<LlmException>(() => guarded.Send(new LlmRequest()));

      Assert.Equal(GuardedProvider.CapMessage, ex.Message);
      Assert.Single(fake.Seen);
      Assert.Equal(6, guard.SpentCents);
    }

    [Fact]
    public void A_row_that_hits_the_cap_ends_failed_and_says_why()
    {
      var guard = new RunGuard(0);

      var result = Run(Guarded(new Fake(true, Says("unreached")), guard));

      Assert.Contains(GuardedProvider.CapMessage, result.EndedBecause);
    }

    [Fact]
    public void The_breaker_opens_for_one_provider_after_failures_in_a_row_and_a_success_resets_the_count()
    {
      var guard = new RunGuard(100);
      var failing = new Fake(true) { Throws = new LlmException("overloaded") };
      var a = Guarded(failing, guard, "A");
      var b = Guarded(new Fake(true, Says("fine")), guard, "B");

      for (var i = 0; i < RunGuard.BreakAfter; i++) Assert.Throws<LlmException>(() => a.Send(new LlmRequest()));

      Assert.True(guard.BreakerOpen("A"));
      Assert.Equal(GuardedProvider.BreakerMessage, Assert.Throws<LlmException>(() => a.Send(new LlmRequest())).Message);
      Assert.Equal(RunGuard.BreakAfter, failing.Seen.Count);
      b.Send(new LlmRequest());
      Assert.False(guard.BreakerOpen("B"));

      guard.Failed("C"); guard.Failed("C"); guard.Succeeded("C"); guard.Failed("C"); guard.Failed("C");
      Assert.False(guard.BreakerOpen("C"));
    }

    [Fact]
    public void The_guard_changes_what_may_be_sent_and_never_what_is_sent()
    {
      Assert.True(((ILlmProvider)Guarded(new Fake(true), new RunGuard(1))).SendsForcedTool);
      Assert.False(((ILlmProvider)Guarded(new Fake(false), new RunGuard(1))).SendsForcedTool);
    }

    /// <summary>Brief BZ: an interface member with a default answers false through a wrapper that does not declare it, silently. Left out here, KeepsHistoryAppendOnly would have elided a long
    /// turn on Sonnet 5.5 in the very run that measures it, and read as the model's 400. So every member is held, including one added later.</summary>
    [Fact]
    public void The_guard_declares_every_fact_a_provider_can_state()
    {
      foreach (var p in typeof(ILlmProvider).GetProperties())
        Assert.True(typeof(GuardedProvider).GetProperty(p.Name) != null, "GuardedProvider does not pass " + p.Name + " through");
    }

    // ---- the key rule ---------------------------------------------------------------------------------------------

    private const string Canary = "sk-canary-0123456789abcdef";

    [Fact]
    public void A_key_that_reaches_a_result_is_removed_from_every_file_written()
    {
      var keys = new KeyRule();
      keys.Hold(Canary);
      var fake = new Fake(true) { Throws = new LlmException("Bad key " + Canary + " rejected") };
      var result = Run(fake);
      Assert.Contains(Canary, result.EndedBecause);   // the harness's own record would carry it: the writer is the rule

      var dir = Path.Combine(Path.GetTempPath(), "agent-eval-" + Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(dir);
      try
      {
        ResultWriter.Write(result, dir, keys);
        foreach (var file in Directory.GetFiles(dir))
        {
          var text = File.ReadAllText(file);
          Assert.DoesNotContain(Canary, text);
          Assert.Contains(KeyRule.Redacted, text);
        }
      }
      finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Text_the_model_wrote_is_held_to_the_same_rule()
    {
      var keys = new KeyRule();
      keys.Hold(Canary);
      var result = Run(new Fake(true, Says("Your key is " + Canary + ".")));

      Assert.DoesNotContain(Canary, keys.Scrub(ResultWriter.Transcript(result)));
    }

    [Fact]
    public void The_console_reads_keys_only_from_the_credential_store_and_prints_only_through_the_rule()
    {
      var dir = new DirectoryInfo(AppContext.BaseDirectory);
      while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Dwight.sln"))) dir = dir.Parent;
      Assert.True(dir != null);
      var program = File.ReadAllText(Path.Combine(dir.FullName, "tools", "AgentEval", "Program.cs"));

      Assert.Contains("keys.Hold(CredentialStore.Load(target))", program);
      Assert.DoesNotContain("GetEnvironmentVariable", program);
      Assert.DoesNotContain("File.WriteAll", program);   // every file goes through KeyRule.WriteFile
      Assert.Contains("ModelCatalogue.BundledOnly = true", program);
      foreach (var line in program.Split('\n').Where(l => l.Contains("ex.Message") || l.Contains("failed)") || l.Contains("EndedBecause, ")))
        if (line.Contains("Console.")) Assert.Contains("keys.Scrub", line);
    }

    // ---- a placement with no map, and an edit: the panel's ask, over the stated world ---------------------------------

    private const string Request = AgentHostTools.RequestMutation;
    private const string PlaceNote = "{\"kind\":\"build\",\"why\":\"a note\",\"place\":{\"components\":[{\"ref\":\"p\",\"type\":\"Panel\",\"nick\":\"Note\"}]}}";

    /// <summary>Three stated objects, so the ids are known: a slider (1) wired into a Circle (2), and a Loft (3) with nothing on it.</summary>
    private static EvalFixture StatedWorld() => EvalFixture.Parse("stated", "{\"canvas\":{\"components\":[" +
      "{\"ref\":\"r\",\"type\":\"Number Slider\",\"nick\":\"Radius\",\"kind\":\"param\",\"outputs\":[\"Number\"],\"constant\":\"3\"}," +
      "{\"ref\":\"c\",\"type\":\"Circle\",\"nick\":\"Ring\",\"inputs\":[\"Plane (P)\",\"Radius (R)\"],\"outputs\":[\"Circle (C)\"]}," +
      "{\"ref\":\"l\",\"type\":\"Loft\",\"inputs\":[\"Curves (C)\",\"Options (O)\"],\"outputs\":[\"Loft (L)\"]}]," +
      "\"wires\":[{\"from\":\"r\",\"fromParam\":\"Number\",\"to\":\"c\",\"toParam\":\"Radius (R)\"}]}}");

    private static string PlaceWired(string to) =>
      "{\"kind\":\"build\",\"why\":\"a slider\",\"place\":{\"components\":[{\"ref\":\"s\",\"type\":\"Number Slider\"}],\"wires\":[{\"from\":{\"ref\":\"s\"},\"to\":" + to + "}]}}";

    private static List<string> Lines(RowResult r) => r.Turns.SelectMany(t => t.PluginLines).ToList();

    private static HandoffSeed PlaceSeed(string place) => new HandoffSeed { Place = JsonNode.Parse(place).AsObject() };

    /// <summary>What the click reads, from what the ask wrote: the brief's own case. Null here is the refusal lane A's "place" rows met.</summary>
    [Fact]
    public void The_stated_worlds_ask_gives_the_seed_the_spot_the_click_reads()
    {
      var world = new EvalWorld(StatedWorld(), new ComponentList());
      var seed = PlaceSeed("{\"components\":[{\"ref\":\"p\",\"type\":\"Panel\"}]}");
      Assert.Null(SeedPlan.From("build", seed, id => IdState.Unknown).PreFillSpot);

      Assert.True(world.PrepareSpot(seed, new string[0], out var spot, out var error), error?.Message);

      var plan = SeedPlan.From("build", seed.ForOption(grouped: true), id => IdState.Unknown);
      Assert.Equal("Spot 1", plan.PreFillSpot);
      Assert.True(plan.PreFillSpotGrouped);
      Assert.NotNull(plan.PreFillPlace);
      Assert.Equal("Spot 1", spot.Placement.Address);
      Assert.Contains("Spot 1", seed.SpotDone);
      Assert.NotEqual(BuildScopeOutcome.Refuse, BuildScope.Resolve(plan.Regions, world.Regions, plan.PreFillSpot).Outcome);
    }

    [Theory]
    [InlineData(ApprovalMode.Ask, "approved-by-row")]
    [InlineData(ApprovalMode.ScriptsAndDeletions, "approved-automatically")]
    [InlineData(ApprovalMode.Never, "approved-automatically")]
    public void A_spot_card_reaches_the_build_is_placed_and_grants_one_read(string mode, string answer)
    {
      var provider = new Fake(true, Calls("q1", Request, PlaceNote), Says("It is down."));

      var result = Run(provider, Row(mode, "approve", "Put a note down."), StatedWorld());

      var card = result.Turns[0].Cards.Single();
      Assert.Equal("Spot 1", card.Spot);
      Assert.Equal(answer, card.Answer);
      Assert.DoesNotContain(Lines(result), l => l.Contains("No region was approved"));
      Assert.Contains(Lines(result), l => l.Contains("[not performed]") && l.Contains("Placed Note in Spot 1, "));
      Assert.Equal(new[] { "ReadOnly", "ReadOnly" }, result.Turns.Select(t => t.Capability));   // a re-read, never a build turn: the card is the spec
      Assert.Contains(provider.Seen[1].Messages.SelectMany(m => m.Blocks), b => (b.Text ?? "").Contains("Placed Note in Spot 1, "));
      Assert.Equal("every user turn ran", result.EndedBecause);
      Assert.Contains("at Spot 1", ResultWriter.Transcript(result));
    }

    [Fact]
    public void A_declined_or_ignored_spot_card_places_nothing()
    {
      foreach (var onCard in new[] { "decline", "ignore" })
      {
        var result = Run(new Fake(true, Calls("q1", Request, PlaceNote), Calls("r1", AgentToolCatalog.GetOverview, "{}"), Says("Three.")),
                         Row(ApprovalMode.Ask, onCard, "Put a note down.", "What is there?"), StatedWorld());
        Assert.DoesNotContain(Lines(result), l => l.Contains("Placed"));
        var overview = Result(result.Turns.Last().Tools.Single());
        Assert.Equal(3, overview["counts"]["components"].GetValue<int>() + overview["counts"]["params"].GetValue<int>());
      }
    }

    [Fact]
    public void Later_reads_find_what_a_spot_placed_and_a_later_spot_can_wire_from_it()
    {
      var provider = new Fake(true,
        Calls("q1", Request, PlaceNote),
        Calls("r1", AgentToolCatalog.GetComponent, "{\"id\":4}"),
        Calls("r2", AgentToolCatalog.GetScaffoldMap, "{}"),
        Says("It is there."),
        Calls("q2", Request, "{\"kind\":\"build\",\"why\":\"a second\",\"place\":{\"components\":[{\"ref\":\"q\",\"type\":\"Panel\"}],\"wires\":[{\"from\":{\"id\":4},\"to\":{\"ref\":\"q\"}}]}}"),
        Says("Both."));

      var result = Run(provider, Row(ApprovalMode.Never, "approve", "One.", "Two."), StatedWorld());

      var read = result.Turns.SelectMany(t => t.Tools).ToList();
      Assert.Equal("real-core", read[1].AnsweredBy);
      Assert.Contains("Panel", read[1].Answer);
      Assert.Contains("Spot 1", read[2].Answer);
      var cards = result.Turns.SelectMany(t => t.Cards).ToList();
      Assert.Equal(new[] { "Spot 1", "Spot 2" }, cards.Select(c => c.Spot));   // the mint continues past what it placed
    }

    [Fact]
    public void A_spot_the_conversation_mentions_is_not_minted_and_rows_do_not_share_a_mint()
    {
      var mentioned = Run(new Fake(true, Calls("q1", Request, PlaceNote), Says("Down.")), Row(ApprovalMode.Never, "approve", "Keep clear of Spot 7, it is my reference."), StatedWorld());
      Assert.Equal("Spot 8", mentioned.Turns[0].Cards.Single().Spot);

      var other = Run(new Fake(true, Calls("q1", Request, PlaceNote), Says("Down.")), Row(ApprovalMode.Never, "approve", "Put it down."), StatedWorld());
      Assert.Equal("Spot 1", other.Turns[0].Cards.Single().Spot);
    }

    [Theory]
    [InlineData("{\"components\":[{\"ref\":\"p\",\"type\":\"Panell\"}]}", "Panel")]   // an uninstalled type, with the near-match
    [InlineData("{\"region\":\"Spot 9\",\"components\":[{\"ref\":\"p\",\"type\":\"Panel\"}]}", "is not a region or group on this canvas")]
    [InlineData("{\"components\":[]}", "needs \\u0022place\\u0022")]
    public void A_placement_the_panel_would_refuse_is_refused_at_the_ask_and_raises_no_card(string place, string says)
    {
      var list = ComponentList.Parse("{\"components\":[{\"name\":\"Panel\",\"category\":\"Params\",\"subCategory\":\"Input\",\"guid\":\"" + Guid.NewGuid() + "\"}]}");

      var result = Run(new Fake(true, Calls("q1", Request, "{\"kind\":\"build\",\"why\":\"x\",\"place\":" + place + "}"), Says("Oh.")),
                       Row(ApprovalMode.Never, "approve", "Go."), StatedWorld(), list);

      Assert.Empty(result.Turns[0].Cards);
      var tool = result.Turns[0].Tools.Single();
      Assert.Equal("refused", tool.AnsweredBy);
      Assert.Contains(says, tool.Answer);
    }

    [Fact]
    public void A_bare_name_two_components_share_is_refused_with_their_forms_and_a_qualified_one_is_kept()
    {
      var list = ComponentList.Parse("{\"components\":[" +
        "{\"name\":\"Rectangle\",\"category\":\"Curve\",\"subCategory\":\"Primitive\",\"guid\":\"" + Guid.NewGuid() + "\"}," +
        "{\"name\":\"Rectangle\",\"category\":\"Params\",\"subCategory\":\"Geometry\",\"guid\":\"" + Guid.NewGuid() + "\"}]}");
      var world = new EvalWorld(StatedWorld(), list);

      Assert.False(world.PrepareSpot(PlaceSeed("{\"components\":[{\"ref\":\"p\",\"type\":\"Rectangle\"}]}"), new string[0], out _, out var error));
      Assert.Contains("Curve > Primitive > Rectangle", error.Message);

      var seed = PlaceSeed("{\"components\":[{\"ref\":\"p\",\"type\":\"Curve > Primitive > Rectangle\"}]}");
      Assert.True(world.PrepareSpot(seed, new string[0], out _, out var none), none?.Message);
      Assert.Equal("Curve > Primitive > Rectangle", seed.Place["components"][0]["type"].GetValue<string>());
    }

    [Fact]
    public void The_record_speaks_in_the_plugins_spelling_of_a_type()
    {
      var list = ComponentList.Parse("{\"components\":[{\"name\":\"Panel\",\"category\":\"Params\",\"subCategory\":\"Input\",\"guid\":\"" + Guid.NewGuid() + "\"}]}");
      var seed = PlaceSeed("{\"components\":[{\"ref\":\"p\",\"type\":\"panel\"}]}");

      Assert.True(new EvalWorld(StatedWorld(), list).PrepareSpot(seed, new string[0], out _, out var error), error?.Message);

      Assert.Equal("Panel", seed.Place["components"][0]["type"].GetValue<string>());
    }

    /// <summary>The ports of a component on the stated canvas are known, in every spelling the product accepts, and the record holds the canvas's.</summary>
    [Theory]
    [InlineData("Radius")]
    [InlineData("R")]
    [InlineData("Radius (R)")]
    public void A_wire_into_a_stated_component_resolves_by_any_of_its_ports_spellings(string param)
    {
      var seed = PlaceSeed("{\"components\":[{\"ref\":\"s\",\"type\":\"Number Slider\"}],\"wires\":[{\"from\":{\"ref\":\"s\"},\"to\":{\"id\":2,\"param\":\"" + param + "\"}}]}");

      Assert.True(new EvalWorld(StatedWorld(), new ComponentList()).PrepareSpot(seed, new string[0], out var spot, out var error), error?.Message);

      Assert.Equal("R", seed.Place["wires"][0]["to"]["param"].GetValue<string>());
      Assert.Contains("Ring", spot.Placement.Beside);
    }

    [Fact]
    public void A_wrong_port_a_missing_choice_or_an_invented_id_is_refused_by_name()
    {
      var wrong = Run(new Fake(true, Calls("q1", Request, PlaceWired("{\"id\":2,\"param\":\"Diameter\"}")), Says("Oh.")), Row(ApprovalMode.Never, "approve", "Go."), StatedWorld());
      Assert.Empty(wrong.Turns[0].Cards);
      Assert.Contains("Diameter", wrong.Turns[0].Tools.Single().Answer);
      Assert.Contains("R (Radius)", wrong.Turns[0].Tools.Single().Answer);

      var unnamed = Run(new Fake(true, Calls("q1", Request, PlaceWired("{\"id\":2}")), Says("Oh.")), Row(ApprovalMode.Never, "approve", "Go."), StatedWorld());
      Assert.Empty(unnamed.Turns[0].Cards);
      Assert.Contains("needs a \\u0022param\\u0022", unnamed.Turns[0].Tools.Single().Answer);

      var invented = Run(new Fake(true, Calls("q1", Request, PlaceWired("{\"id\":99,\"param\":\"R\"}")), Says("Oh.")), Row(ApprovalMode.Never, "approve", "Go."), StatedWorld());
      Assert.Empty(invented.Turns[0].Cards);
      Assert.Equal("refused", invented.Turns[0].Tools.Single().AnsweredBy);
    }

    /// <summary>The panel's <c>ShapeOf</c>: a param has one implicit port and no name to give it, whatever the model called it and whatever the fixture lists, so the record names none.</summary>
    [Fact]
    public void A_wire_from_a_stated_param_records_no_port_name()
    {
      var seed = PlaceSeed("{\"components\":[{\"ref\":\"p\",\"type\":\"Panel\"}],\"wires\":[{\"from\":{\"id\":1,\"param\":\"Number\"},\"to\":{\"ref\":\"p\"}}]}");

      Assert.True(new EvalWorld(StatedWorld(), new ComponentList()).PrepareSpot(seed, new string[0], out _, out var error), error?.Message);

      Assert.Null(seed.Place["wires"][0]["from"]["param"]);
    }

    /// <summary>A recorded component of a type with no other instance has stated no ports. That is not known, not "none": the name the model gave stands, where an empty list would read it as a param and drop it.</summary>
    [Fact]
    public void A_wire_into_a_recorded_component_with_no_stated_ports_keeps_the_name_it_was_given()
    {
      var world = new EvalWorld(StatedWorld(), new ComponentList());
      Assert.True(world.PlaceSpot(JsonNode.Parse("{\"components\":[{\"ref\":\"a\",\"type\":\"Addition\"}]}").AsObject(), "Spot 1", true).Ok);
      var seed = PlaceSeed("{\"components\":[{\"ref\":\"s\",\"type\":\"Number Slider\"}],\"wires\":[{\"from\":{\"ref\":\"s\"},\"to\":{\"id\":4,\"param\":\"A\"}}]}");

      Assert.True(world.PrepareSpot(seed, new string[0], out _, out var error), error?.Message);

      Assert.Equal("A", seed.Place["wires"][0]["to"]["param"].GetValue<string>());
    }

    /// <summary>What this world does not know is not a rule: the saved list holds no ports. A wrong port on such a type passes here, which the panel would refuse. Named in every result.</summary>
    [Fact]
    public void A_port_on_a_type_with_no_instance_is_not_known_and_makes_no_rule()
    {
      var seed = PlaceSeed("{\"components\":[{\"ref\":\"a\",\"type\":\"Addition\"},{\"ref\":\"b\",\"type\":\"Addition\"}],\"wires\":[{\"from\":{\"ref\":\"a\",\"param\":\"Nope\"},\"to\":{\"ref\":\"b\",\"param\":\"Nope\"}}]}");

      Assert.True(new EvalWorld(StatedWorld(), new ComponentList()).PrepareSpot(seed, new string[0], out _, out var error), error?.Message);
      Assert.Contains(EvalNotReal.Items, line => line.Contains("a wrong port elsewhere passes"));
    }

    [Fact]
    public void A_placement_naming_scripts_on_the_ask_is_refused_as_the_panel_refuses_it()
    {
      const string ask = "{\"kind\":\"build\",\"why\":\"x\",\"place\":{\"components\":[{\"ref\":\"p\",\"type\":\"Panel\"}]},\"scripts\":[{\"region\":\"A1\",\"type\":\"Python 3 Script\",\"source\":\"a = 1\"}]}";

      var result = Run(new Fake(true, Calls("q1", Request, ask), Says("Oh.")), Row(ApprovalMode.Never, "approve", "Go."), StatedWorld());

      Assert.Empty(result.Turns[0].Cards);
      Assert.Contains("belongs to a build into a map region", result.Turns[0].Tools.Single().Answer);
    }

    /// <summary>The mode decides on what the card holds. With the script count stuck at 0, this card was approved automatically here, and the product asks.</summary>
    [Fact]
    public void A_placement_holding_a_script_asks_under_scripts_and_deletions_and_one_without_does_not()
    {
      const string withScript = "{\"kind\":\"build\",\"why\":\"x\",\"place\":{\"components\":[{\"ref\":\"p\",\"type\":\"Python 3 Script\",\"source\":\"result = 1\",\"outputs\":[\"result\"]}]}}";

      var asks = Run(new Fake(true, Calls("q1", Request, withScript), Says("Down.")), Row(ApprovalMode.ScriptsAndDeletions, "approve", "Go."), StatedWorld());
      Assert.Equal(ApprovalDecision.Ask.ToString(), asks.Turns[0].Cards.Single().Decision);
      Assert.Equal("approved-by-row", asks.Turns[0].Cards.Single().Answer);

      var auto = Run(new Fake(true, Calls("q1", Request, PlaceNote), Says("Down.")), Row(ApprovalMode.ScriptsAndDeletions, "approve", "Go."), StatedWorld());
      Assert.Equal(ApprovalDecision.Auto.ToString(), auto.Turns[0].Cards.Single().Decision);
    }

    private static string EditAsk(string ops) => "{\"kind\":\"edit\",\"why\":\"tidy\",\"ops\":" + ops + "}";

    /// <summary>The edit route's same gap. Deletes were always 0 here, so a delete went through automatically where the product asks.</summary>
    [Fact]
    public void A_delete_asks_under_scripts_and_deletions_and_a_wire_does_not()
    {
      var delete = Run(new Fake(true, Calls("q1", Request, EditAsk("[{\"op\":\"delete\",\"id\":3}]")), Says("Gone.")), Row(ApprovalMode.ScriptsAndDeletions, "ignore", "Go."), StatedWorld());
      Assert.Equal(ApprovalDecision.Ask.ToString(), delete.Turns[0].Cards.Single().Decision);
      Assert.Equal("ignored-by-row", delete.Turns[0].Cards.Single().Answer);

      var wire = Run(new Fake(true, Calls("q1", Request, EditAsk("[{\"op\":\"wire\",\"from\":{\"id\":2},\"to\":{\"id\":3,\"param\":\"Curves\"}}]")), Says("Wired.")),
                     Row(ApprovalMode.ScriptsAndDeletions, "ignore", "Go."), StatedWorld());
      Assert.Equal(ApprovalDecision.Auto.ToString(), wire.Turns[0].Cards.Single().Decision);
    }

    [Theory]
    [InlineData("[{\"op\":\"delete\",\"id\":99}]", "99")]                                                                                  // no such component
    [InlineData("[{\"op\":\"wire\",\"from\":{\"id\":1},\"to\":{\"id\":2,\"param\":\"R\"}}]", "already exists")]                  // the stated wire is read
    [InlineData("[{\"op\":\"unwire\",\"from\":{\"id\":2},\"to\":{\"id\":3,\"param\":\"C\"}}]", "no wire from")]
    [InlineData("[{\"op\":\"wire\",\"from\":{\"id\":3},\"to\":{\"id\":1}}]", "no input to wire into")]                              // a slider has no input grip
    [InlineData("[{\"op\":\"wire\",\"from\":{\"id\":2},\"to\":{\"id\":3,\"param\":\"Nope\"}}]", "Nope")]
    [InlineData("[{\"op\":\"wire\",\"from\":{\"id\":2},\"to\":{\"id\":1}},{\"op\":\"delete\",\"id\":99}]", "row")]             // every bad row, named
    public void An_edit_the_panel_would_refuse_is_refused_at_the_ask_and_raises_no_card(string ops, string says)
    {
      var result = Run(new Fake(true, Calls("q1", Request, EditAsk(ops)), Says("Oh.")), Row(ApprovalMode.Never, "approve", "Go."), StatedWorld());

      Assert.Empty(result.Turns[0].Cards);
      Assert.Equal("refused", result.Turns[0].Tools.Single().AnsweredBy);
      Assert.Contains(says, result.Turns[0].Tools.Single().Answer);
    }

    [Fact]
    public void An_edit_the_stated_canvas_can_resolve_gets_the_plugins_record_and_counts_its_deletes()
    {
      var world = new EvalWorld(StatedWorld(), new ComponentList());
      var seed = new HandoffSeed { Ops = JsonNode.Parse("[{\"op\":\"unwire\",\"from\":{\"id\":1},\"to\":{\"id\":2,\"param\":\"Radius\"}},{\"op\":\"delete\",\"id\":3}]").AsArray() };

      Assert.True(world.PrepareEdit(seed, out var plan, out var error), error?.Message);

      Assert.Equal(1, plan.Unwires);
      Assert.Equal(1, plan.Deletes);
      Assert.Equal("R", plan.Record[0]["to"]["param"].GetValue<string>());   // the canvas's spelling, not the model's
    }

    /// <summary>A component a recorded build placed, of a type with no other instance, has no stated ports. An edit naming one is not refused for what this world does not know.</summary>
    [Fact]
    public void An_edit_of_a_recorded_component_with_no_stated_ports_is_not_refused_for_that()
    {
      var provider = new Fake(true,
        Calls("q1", Request, "{\"kind\":\"build\",\"why\":\"x\",\"place\":{\"components\":[{\"ref\":\"a\",\"type\":\"Addition\"}]}}"),
        Says("Down."),
        Calls("q2", Request, EditAsk("[{\"op\":\"wire\",\"from\":{\"id\":1},\"to\":{\"id\":4,\"param\":\"A\"}}]")),
        Says("Wired."),
        Says("Done."));

      var result = Run(provider, Row(ApprovalMode.Never, "approve", "Place.", "Wire."), StatedWorld());

      Assert.Contains(result.Turns.SelectMany(t => t.Cards), c => c.Kind == "edit");
    }

    // ---- a placement's landing, over the stated world (Brief CM) ------------------------------------------------------

    private static EvalFixture GroupedWorld() => EvalFixture.Parse("grouped", "{\"canvas\":{\"components\":[" +
      "{\"ref\":\"a\",\"type\":\"Panel\",\"nick\":\"Key\",\"group\":\"Answer Key\",\"x\":0,\"y\":0}," +
      "{\"ref\":\"b\",\"type\":\"Circle\",\"nick\":\"Ring\",\"x\":400,\"y\":0,\"inputs\":[\"Plane (P)\",\"Radius (R)\"],\"outputs\":[\"Circle (C)\"]}]}}");

    [Fact]
    public void A_placement_with_no_wires_lands_past_the_drawing_and_says_so()
    {
      var provider = new Fake(true, Calls("q1", Request, PlaceNote), Says("Down."));

      var result = Run(provider, Row(ApprovalMode.Never, "approve", "Put a note down."), StatedWorld());

      Assert.Contains(Lines(result), l => l.Contains("Placed Note in Spot 1, to the right of the drawing, 60 units past its edge."));
      Assert.Contains(provider.Seen[1].Messages.SelectMany(m => m.Blocks), b => (b.Text ?? "").Contains("60 units past its edge"));
    }

    [Fact]
    public void A_placement_fed_by_a_component_lands_right_of_it_and_says_how_far()
    {
      var ask = "{\"kind\":\"build\",\"why\":\"x\",\"place\":{\"components\":[{\"ref\":\"p\",\"type\":\"Panel\",\"nick\":\"Note\"}],\"wires\":[{\"from\":{\"id\":2},\"to\":{\"ref\":\"p\"}}]}}";
      var result = Run(new Fake(true, Calls("q1", Request, ask), Says("Down.")), Row(ApprovalMode.Never, "approve", "Show the ring."), StatedWorld());

      Assert.Contains(Lines(result), l => l.Contains("Placed Note in Spot 1,") && l.Contains("Ring"));
    }

    [Fact]
    public void A_placement_never_lands_on_what_the_world_states()
    {
      // A component where the harness's old grid would have put the first one: nothing here measured a box until the search did.
      var world = new EvalWorld(EvalFixture.Parse("in the way", "{\"canvas\":{\"components\":[{\"ref\":\"z\",\"type\":\"Panel\",\"x\":4160,\"y\":4000},{\"ref\":\"y\",\"type\":\"Panel\",\"x\":0,\"y\":0}]}}"), new ComponentList());

      foreach (var _ in Enumerable.Range(0, 3))
        Assert.True(world.PlaceSpot(JsonNode.Parse("{\"components\":[{\"ref\":\"p\",\"type\":\"Panel\"},{\"ref\":\"q\",\"type\":\"Panel\"},{\"ref\":\"r\",\"type\":\"Panel\"}]}").AsObject(), "Spot 1", false).Ok);

      Assert.Equal(0, ReadOf(world, AgentToolCatalog.GetCanvasLayout, "{}")["result"]["overlapsTotal"].GetValue<int>());
    }

    /// <summary>The teaching file's request, through the row: the card names the group, the click grows it, and later reads find the new component in it.</summary>
    [Fact]
    public void A_placement_naming_a_stated_group_lands_in_it()
    {
      var ask = "{\"kind\":\"build\",\"why\":\"x\",\"place\":{\"region\":\"Answer Key\",\"components\":[{\"ref\":\"p\",\"type\":\"Panel\",\"nick\":\"Check\"}]}}";
      var provider = new Fake(true, Calls("q1", Request, ask), Calls("r1", AgentToolCatalog.GetCanvasLayout, "{}"), Says("In."));

      var result = Run(provider, Row(ApprovalMode.Never, "approve", "Add a check to my answer key region."), GroupedWorld());

      Assert.Equal("Answer Key", result.Turns[0].Cards.Single().Spot);
      Assert.Contains(Lines(result), l => l.Contains("Placed Check in Answer Key,"));
      var members = Result(result.Turns[1].Tools.Single())["groups"][0]["members"].AsArray().Select(m => m.GetValue<int>()).ToList();
      Assert.Equal(new[] { 1, 3 }, members);
    }

    [Fact]
    public void A_placement_naming_a_map_region_lands_in_it_and_its_count_grows()
    {
      var world = new EvalWorld(StatedWorld(), new ComponentList());
      Assert.True(world.PlaceSpot(JsonNode.Parse("{\"components\":[{\"ref\":\"p\",\"type\":\"Panel\",\"nick\":\"Note\"}]}").AsObject(), "Spot 1", true).Ok);
      var seed = PlaceSeed("{\"region\":\"Spot 1\",\"components\":[{\"ref\":\"q\",\"type\":\"Panel\"}]}");
      Assert.True(world.PrepareSpot(seed, new string[0], out var spot, out var error), error?.Message);
      Assert.Equal("Spot 1", spot.Placement.Into);

      var plan = SeedPlan.From("build", seed.ForOption(grouped: true), id => IdState.Unknown);   // the option an automatic approval and a row both take
      Assert.True(world.PlaceSpot(plan.PreFillPlace, plan.PreFillSpot, plan.PreFillSpotGrouped, plan.PreFillIntoGroup).Ok);

      var regions = ReadOf(world, AgentToolCatalog.GetScaffoldMap, "{}")["result"]["regions"].AsArray();
      Assert.Equal(2, regions.Single(r => r["address"].GetValue<string>() == "Spot 1")["componentsInside"].GetValue<int>());
      Assert.Single(regions);   // the minted address is only the ledger's key: no second place was made
    }

    [Fact]
    public void A_user_naming_a_place_the_model_did_not_is_refused_with_no_card_and_separate_says_so_on_the_card()
    {
      var refused = Run(new Fake(true, Calls("q1", Request, PlaceNote), Says("Oh.")), Row(ApprovalMode.Never, "approve", "Add a note to my answer key region."), GroupedWorld());
      Assert.Empty(refused.Turns[0].Cards);
      Assert.Contains("names \\u0022Answer Key\\u0022", refused.Turns[0].Tools.Single().Answer);

      const string apart = "{\"kind\":\"build\",\"why\":\"x\",\"place\":{\"separate\":true,\"components\":[{\"ref\":\"p\",\"type\":\"Panel\",\"nick\":\"Note\"}]}}";
      var separate = Run(new Fake(true, Calls("q1", Request, apart), Says("Apart.")), Row(ApprovalMode.Never, "approve", "Add a note beside my answer key region."), GroupedWorld());
      Assert.Single(separate.Turns[0].Cards);
      Assert.Contains(Lines(separate), l => l.Contains("Placed Note in Spot 1,"));
    }

    /// <summary>The architect's negative, through the row: a bare address is not a destination, so the person gets their build.</summary>
    [Fact]
    public void A_bare_address_in_a_users_words_does_not_refuse_a_placement()
    {
      var result = Run(new Fake(true, Calls("q1", Request, PlaceNote), Says("Down.")), Row(ApprovalMode.Never, "approve", "put a1 beside it"), StatedWorld());

      Assert.Single(result.Turns[0].Cards);
    }

    [Fact]
    public void A_group_gone_by_the_click_is_refused_and_nothing_lands()
    {
      var world = new EvalWorld(GroupedWorld(), new ComponentList());

      var result = world.PlaceSpot(JsonNode.Parse("{\"components\":[{\"ref\":\"p\",\"type\":\"Panel\"}]}").AsObject(), "Spot 1", true, Guid.NewGuid());

      Assert.False(result.Ok);
      Assert.Contains("no longer on the canvas", result.Message);
      Assert.Equal(2, ReadOf(world, AgentToolCatalog.GetOverview, "{}")["result"]["counts"]["components"].GetValue<int>());
    }

    // ---- an approved change reaches the stated world ---------------------------------------------------------------

    private const string Slider40 = "{\"min\":0,\"max\":100,\"value\":40,\"rounding\":\"integer\"}";
    private const string PlaceScript = "{\"kind\":\"build\",\"why\":\"x\",\"place\":{\"components\":[{\"ref\":\"p\",\"type\":\"Python 3 Script\",\"nick\":\"Area\",\"source\":\"result = 1\",\"outputs\":[\"result\"]}]}}";
    private const string Applied = "[Applied by Dwight on the user's approval]";

    private static string ValueAsk(int target, string state) => "{\"kind\":\"set_value\",\"why\":\"x\",\"target\":" + target + ",\"state\":" + state + "}";

    private static string EditScriptAskFor(int target, string source, string claim = "changes", string more = "") =>
      "{\"kind\":\"edit_script\",\"why\":\"x\",\"target\":" + target + ",\"claim\":\"" + claim + "\",\"source\":\"" + source + "\"" + more + "}";

    private static JsonNode ReadOf(EvalWorld world, string tool, string args) => JsonNode.Parse(world.Answer(tool, args, out _));

    private static SeedPlan PlanOf(string kind, HandoffSeed seed) => SeedPlan.From(kind, seed, id => IdState.Unknown);

    private static HandoffSeed ValueSeed(int target, string state) => new HandoffSeed { Target = target, State = JsonNode.Parse(state).AsObject() };

    /// <summary>The Spot 1 script from <see cref="PlaceScript"/>, id 4, on the stated world.</summary>
    private static EvalWorld WorldWithScript()
    {
      var world = new EvalWorld(StatedWorld(), new ComponentList());
      Assert.True(world.PlaceSpot(JsonNode.Parse(PlaceScript)["place"].AsObject(), "Spot 1", true).Ok);
      return world;
    }

    [Theory]
    [InlineData(ApprovalMode.Ask, "approved-by-row")]
    [InlineData(ApprovalMode.Never, "approved-automatically")]
    public void An_approved_value_is_what_the_next_read_sees_and_it_grants_that_one_read(string mode, string answer)
    {
      var provider = new Fake(true, Calls("q1", Request, ValueAsk(1, Slider40)), Calls("r1", AgentToolCatalog.GetComponent, "{\"id\":1}"), Says("It is 40."));

      var result = Run(provider, Row(mode, "approve", "Set it."), StatedWorld());

      Assert.Equal(answer, result.Turns[0].Cards.Single().Answer);
      Assert.Contains(Lines(result), l => l.Contains("[not performed]") && l.Contains("from 3 to 40 (0 to 100, integer)"));
      Assert.Equal(new[] { "ReadOnly", "ReadOnly" }, result.Turns.Select(t => t.Capability));
      Assert.Contains(provider.Seen[1].Messages.SelectMany(m => m.Blocks), b => (b.Text ?? "").Contains(Applied));
      var value = Result(result.Turns[1].Tools.Single())["value"];
      Assert.Equal("slider", value["kind"].GetValue<string>());
      Assert.Equal(40m, value["value"].GetValue<decimal>());
      Assert.Equal(100m, value["max"].GetValue<decimal>());
      Assert.Equal("integer", value["rounding"].GetValue<string>());
    }

    [Theory]
    [InlineData("decline")]
    [InlineData("ignore")]
    public void A_value_nobody_approved_changes_nothing(string onCard)
    {
      var result = Run(new Fake(true, Calls("q1", Request, ValueAsk(1, Slider40)), Calls("r1", AgentToolCatalog.GetComponent, "{\"id\":1}"), Says("Three.")),
                       Row(ApprovalMode.Ask, onCard, "Set it.", "What is it?"), StatedWorld());

      Assert.DoesNotContain(Lines(result), l => l.Contains(Applied));
      Assert.Null(Result(result.Turns.Last().Tools.Single())["value"]);
    }

    /// <summary>A region's brief reads the constant, so a re-read through it must not show the old one.</summary>
    [Fact]
    public void A_regions_brief_shows_the_value_a_card_set()
    {
      var world = new EvalWorld(StatedWorld(), new ComponentList());
      var seed = ValueSeed(1, Slider40);
      Assert.True(world.PrepareValue(seed, out var error), error?.Message);
      Assert.True(world.ApplyValue(PlanOf("set_value", seed)).Ok);

      ReadOf(world, AgentToolCatalog.GetOverview, "{}");
      var region = ReadOf(world, AgentToolCatalog.GetRegion, "{\"id\":0}").ToJsonString();

      Assert.Contains("\"value\":\"40\"", region);
      Assert.DoesNotContain("\"value\":\"3\"", region);
    }

    /// <summary>The second card starts from what the first wrote, and the restore rule has it to read: 2.5 is not on an integer grid, so undo could not restore it.</summary>
    [Fact]
    public void The_value_a_card_set_is_what_the_next_card_starts_from_and_the_restore_rule_reads()
    {
      var world = new EvalWorld(StatedWorld(), new ComponentList());
      var first = ValueSeed(1, "{\"min\":0,\"max\":10,\"value\":2.5}");
      Assert.True(world.PrepareValue(first, out _));
      Assert.True(world.ApplyValue(PlanOf("set_value", first)).Ok);

      Assert.False(world.PrepareValue(ValueSeed(1, "{\"min\":0,\"max\":10,\"value\":3,\"rounding\":\"integer\"}"), out var refused));
      Assert.Contains("undo could not restore", refused.Message);

      var second = ValueSeed(1, "{\"min\":0,\"max\":10,\"value\":4.5}");
      Assert.True(world.PrepareValue(second, out _));
      var report = SetValueReport.Text(world.ApplyValue(PlanOf("set_value", second)));
      Assert.Contains("from 2.5 (0 to 10, float) to 4.5 (0 to 10, float)", report);
    }

    [Fact]
    public void An_approved_panel_text_is_what_a_read_sees()
    {
      var world = new EvalWorld(StatedWorld(), new ComponentList());
      Assert.True(world.PlaceSpot(JsonNode.Parse("{\"components\":[{\"ref\":\"p\",\"type\":\"Panel\"}]}").AsObject(), "Spot 1", true).Ok);
      var seed = ValueSeed(4, "{\"text\":\"hello\"}");
      Assert.True(world.PrepareValue(seed, out var error), error?.Message);
      Assert.True(world.ApplyValue(PlanOf("set_value", seed)).Ok);

      var value = ReadOf(world, AgentToolCatalog.GetComponent, "{\"id\":4}")["result"]["value"];

      Assert.Equal("panel", value["kind"].GetValue<string>());
      Assert.Equal("hello", value["text"].GetValue<string>());
    }

    [Theory]
    [InlineData(99, Slider40, "99")]                                                              // no such component
    [InlineData(2, Slider40, "Circle")]                                                           // a type that holds no state
    [InlineData(1, "{\"min\":10,\"max\":0,\"value\":5}", "min")]                                  // an inverted domain
    public void A_value_the_panel_would_refuse_is_refused_at_the_ask_and_raises_no_card(int target, string state, string says)
    {
      var result = Run(new Fake(true, Calls("q1", Request, ValueAsk(target, state)), Says("Oh.")), Row(ApprovalMode.Never, "approve", "Go."), StatedWorld());

      Assert.Empty(result.Turns[0].Cards);
      Assert.Equal("refused", result.Turns[0].Tools.Single().AnsweredBy);
      Assert.Contains(says, result.Turns[0].Tools.Single().Answer);
    }

    [Fact]
    public void A_click_on_a_value_whose_target_is_no_longer_what_the_card_named_changes_nothing()
    {
      var world = new EvalWorld(StatedWorld(), new ComponentList());
      var seed = ValueSeed(1, Slider40);
      seed.TargetType = "Panel";

      var result = world.ApplyValue(PlanOf("set_value", seed));

      Assert.False(result.Ok);
      Assert.Contains("[Not applied", SetValueReport.Text(result));
      Assert.Contains("not the Panel", result.Message);
      Assert.Null(ReadOf(world, AgentToolCatalog.GetComponent, "{\"id\":1}")["result"]["value"]);
    }

    [Fact]
    public void A_placed_script_shows_its_source_to_a_read()
    {
      var script = ReadOf(WorldWithScript(), AgentToolCatalog.GetComponent, "{\"id\":4}")["result"]["script"];

      Assert.Equal("python", script["language"].GetValue<string>());
      Assert.Equal("result = 1", script["source"].GetValue<string>());
    }

    /// <summary>The panel grants no read after a script edit that lands, so the change is seen on the next user turn. The harness used to grant one.</summary>
    [Theory]
    [InlineData(ApprovalMode.Ask)]
    [InlineData(ApprovalMode.Never)]
    public void An_approved_script_edit_grants_no_read_and_the_next_turn_reads_it(string mode)
    {
      var provider = new Fake(true,
        Calls("q1", Request, PlaceScript),
        Says("Down."),
        Calls("q2", Request, EditScriptAskFor(4, "result = 2", more: ",\"add_inputs\":[\"offset\"],\"add_outputs\":[\"area\"]")),
        Calls("r1", AgentToolCatalog.GetComponent, "{\"id\":4}"),
        Says("It is 2."));

      var result = Run(provider, Row(mode, "approve", "Place it.", "Change it.", "Read it."), StatedWorld());

      Assert.Equal(new[] { "ReadOnly", "ReadOnly", "ReadOnly", "ReadOnly" }, result.Turns.Select(t => t.Capability));
      Assert.Contains(Lines(result), l => l.Contains("[not performed]") && l.Contains(Applied) && l.Contains("IT CHANGES WHAT THE COMPONENT PRODUCES") && l.Contains("\"Area\" (4)"));
      Assert.DoesNotContain(Lines(result), l => l.Contains(AutoFollowUp.Line));
      Assert.Contains(provider.Seen[3].Messages.SelectMany(m => m.Blocks), b => (b.Text ?? "").Contains(Applied));
      var read = Result(result.Turns[3].Tools.Single());
      Assert.Equal("result = 2", read["script"]["source"].GetValue<string>());
      var json = read.ToJsonString();
      Assert.Contains("offset", json);
      Assert.Contains("area", json);
      Assert.Equal(2, read["outCounts"].AsArray().Count);   // the read says the new output holds an item, as every stated output does
    }

    [Fact]
    public void A_preserving_edit_gets_the_products_words_for_what_was_compared_and_one_with_no_outputs_does_not_read_as_verified()
    {
      var world = WorldWithScript();
      var seed = new HandoffSeed { Target = 4, Claim = "preserves", Source = "result = 1 + 0" };
      Assert.True(world.PrepareScript(seed, out var error), error?.Message);
      Assert.Equal(ScriptEditOutcome.Preserved.ToString(), seed.Outcome);
      Assert.Contains("verified the outputs", seed.Report);
      Assert.Equal("result = 1", seed.BaseSource);

      var none = new EvalWorld(StatedWorld(), new ComponentList());
      Assert.True(none.PlaceSpot(JsonNode.Parse("{\"components\":[{\"ref\":\"p\",\"type\":\"C# Script\",\"source\":\"int a = 1;\"}]}").AsObject(), "Spot 1", true).Ok);
      var empty = new HandoffSeed { Target = 4, Claim = "preserves", Source = "int a = 2;" };
      Assert.True(none.PrepareScript(empty, out var noneError), noneError?.Message);
      Assert.Equal(ScriptEditOutcome.NothingToCompare.ToString(), empty.Outcome);
      Assert.DoesNotContain("verified", empty.Report);
    }

    [Theory]
    [InlineData(99, "result = 2", "changes", "", "99")]                                                          // no such component
    [InlineData(1, "result = 2", "changes", "", "not a script component")]                                       // a slider
    [InlineData(4, "result = 1", "changes", "", "identical")]                                                    // the source it already holds: it reads what the placement wrote
    [InlineData(4, "result = 2", "maybe", "", "claim")]                                                          // an unknown claim, refused by name
    [InlineData(4, "result = 2", "changes", ",\"add_outputs\":[\"result\"]", "already has an output")]             // appending what exists
    public void A_script_edit_the_panel_would_refuse_is_refused_at_the_ask_and_raises_no_card(int target, string source, string claim, string more, string says)
    {
      var result = Run(new Fake(true, Calls("q1", Request, PlaceScript), Says("Down."), Calls("q2", Request, EditScriptAskFor(target, source, claim, more)), Says("Oh.")),
                       Row(ApprovalMode.Never, "approve", "Place it.", "Change it."), StatedWorld());

      var turn = result.Turns.Last();
      Assert.Empty(turn.Cards);
      Assert.Equal("refused", turn.Tools.Single().AnsweredBy);
      Assert.Contains(says, turn.Tools.Single().Answer);
    }

    /// <summary>The click's compare-and-set: a card promised a change from a specific text, and if that text has moved the diff is no longer what it would do.</summary>
    [Fact]
    public void A_click_on_a_script_edit_whose_base_moved_changes_nothing()
    {
      var world = WorldWithScript();
      var seed = new HandoffSeed { Target = 4, Claim = "changes", Source = "result = 2", BaseSource = "result = 0" };

      Assert.False(world.ApplyScript(PlanOf("edit_script", seed), out var text));

      Assert.Contains("has changed since Dwight showed you the diff", text);
      Assert.Equal("result = 1", ReadOf(world, AgentToolCatalog.GetComponent, "{\"id\":4}")["result"]["script"]["source"].GetValue<string>());
    }

    [Fact]
    public void A_click_on_a_script_edit_the_world_now_refuses_changes_nothing_and_says_why()
    {
      var world = WorldWithScript();
      var seed = new HandoffSeed { Target = 4, Claim = "changes", Source = "result = 1", BaseSource = "result = 1" };   // identical by the time of the click

      Assert.False(world.ApplyScript(PlanOf("edit_script", seed), out var text));

      Assert.Contains("identical", text);
    }

    [Fact]
    public void An_approved_wire_is_what_the_next_read_sees_and_a_second_is_refused_as_already_there()
    {
      var ask = EditAsk("[{\"op\":\"wire\",\"from\":{\"id\":2},\"to\":{\"id\":3,\"param\":\"Curves\"}}]");
      var provider = new Fake(true, Calls("q1", Request, ask), Calls("r1", AgentToolCatalog.GetConnections, "{\"id\":3}"), Says("Wired."), Calls("q2", Request, ask), Says("Oh."));

      var result = Run(provider, Row(ApprovalMode.Never, "approve", "Wire it.", "Again."), StatedWorld());

      Assert.Equal(new[] { "ReadOnly", "ReadOnly", "ReadOnly" }, result.Turns.Select(t => t.Capability));
      Assert.Contains(Lines(result), l => l.Contains("[not performed]") && l.Contains(Applied));
      Assert.Contains(provider.Seen[1].Messages.SelectMany(m => m.Blocks), b => (b.Text ?? "").Contains(Applied));
      Assert.Contains("\"node\":2", Result(result.Turns[1].Tools.Single())["upstream"].ToJsonString());
      Assert.Contains("already exists", result.Turns[2].Tools.Single().Answer);
    }

    [Fact]
    public void An_approved_unwire_is_gone_from_the_next_read_and_only_that_wire_goes()
    {
      var world = new EvalWorld(StatedWorld(), new ComponentList());
      var wire = new HandoffSeed { Ops = JsonNode.Parse("[{\"op\":\"wire\",\"from\":{\"id\":2},\"to\":{\"id\":3,\"param\":\"Curves\"}}]").AsArray() };
      Assert.True(world.PrepareEdit(wire, out var wired, out _));
      wire.Ops = wired.Record;
      Assert.True(world.ApplyEdit(PlanOf("edit", wire), out _));

      var unwire = new HandoffSeed { Ops = JsonNode.Parse("[{\"op\":\"unwire\",\"from\":{\"id\":1},\"to\":{\"id\":2,\"param\":\"Radius\"}}]").AsArray() };
      Assert.True(world.PrepareEdit(unwire, out var plan, out var error), error?.Message);
      unwire.Ops = plan.Record;
      Assert.True(world.ApplyEdit(PlanOf("edit", unwire), out var text));

      Assert.Contains("Remove the wire", text);
      var read = ReadOf(world, AgentToolCatalog.GetConnections, "{\"id\":2}")["result"];
      Assert.DoesNotContain("\"node\":1", read["upstream"].ToJsonString());
      Assert.Contains("\"node\":3", read["downstream"].ToJsonString());
      Assert.False(world.PrepareEdit(new HandoffSeed { Ops = JsonNode.Parse("[{\"op\":\"unwire\",\"from\":{\"id\":1},\"to\":{\"id\":2,\"param\":\"Radius\"}}]").AsArray() }, out _, out var again));
      Assert.Contains("no wire from", again.Message);
    }

    [Fact]
    public void An_approved_delete_leaves_every_read_and_its_id_says_retired()
    {
      var provider = new Fake(true,
        Calls("q1", Request, EditAsk("[{\"op\":\"delete\",\"id\":2}]")),
        Calls("r1", AgentToolCatalog.GetOverview, "{}"),
        Calls("r2", AgentToolCatalog.GetComponent, "{\"id\":2}"),
        Calls("r3", AgentToolCatalog.GetConnections, "{\"id\":1}"),
        Says("Gone."));

      var result = Run(provider, Row(ApprovalMode.Ask, "approve", "Delete the ring."), StatedWorld());

      var reads = result.Turns[1].Tools;
      var counts = Result(reads[0])["counts"];
      Assert.Equal(2, counts["components"].GetValue<int>() + counts["params"].GetValue<int>());
      Assert.Equal(2, Result(reads[0])["regions"][0]["size"].GetValue<int>());
      Assert.Equal("refused", reads[1].AnsweredBy);
      Assert.Contains(ToolErrors.RetiredId, reads[1].Answer);
      Assert.DoesNotContain("\"node\":2", Result(reads[2]).ToJsonString());
    }

    /// <summary>The flow between regions is cut from the wires, and a read of the overview shows it, so it must follow them.</summary>
    [Fact]
    public void The_flow_between_regions_follows_an_approved_edit()
    {
      var fixture = EvalFixture.Parse("regions", "{\"canvas\":{\"components\":[" +
        "{\"ref\":\"r\",\"type\":\"Number Slider\",\"kind\":\"param\",\"region\":\"Inputs\",\"outputs\":[\"Number\"]}," +
        "{\"ref\":\"c\",\"type\":\"Circle\",\"region\":\"Shape\",\"inputs\":[\"Radius (R)\"],\"outputs\":[\"Circle (C)\"]}]," +
        "\"wires\":[{\"from\":\"r\",\"fromParam\":\"Number\",\"to\":\"c\",\"toParam\":\"Radius (R)\"}]}}");
      var world = new EvalWorld(fixture, new ComponentList());
      Assert.NotEmpty(ReadOf(world, AgentToolCatalog.GetOverview, "{}")["result"]["flow"].AsArray());

      var seed = new HandoffSeed { Ops = JsonNode.Parse("[{\"op\":\"unwire\",\"from\":{\"id\":1},\"to\":{\"id\":2,\"param\":\"Radius\"}}]").AsArray() };
      Assert.True(world.PrepareEdit(seed, out var plan, out var error), error?.Message);
      seed.Ops = plan.Record;
      Assert.True(world.ApplyEdit(PlanOf("edit", seed), out _));

      Assert.Empty(ReadOf(world, AgentToolCatalog.GetOverview, "{}")["result"]["flow"].AsArray());

      var delete = new HandoffSeed { Ops = JsonNode.Parse("[{\"op\":\"delete\",\"id\":2}]").AsArray() };
      Assert.True(world.PrepareEdit(delete, out var deleted, out _));
      delete.Ops = deleted.Record;
      Assert.True(world.ApplyEdit(PlanOf("edit", delete), out _));
      Assert.Equal(1, ReadOf(world, AgentToolCatalog.GetOverview, "{}")["result"]["counts"]["regions"].GetValue<int>());   // a region with nothing left in it is gone
    }

    /// <summary>Brief CL: the product's rule over the stated world. The person's group is named and left; the next test holds a spot's.</summary>
    [Fact]
    public void A_group_of_the_persons_own_that_a_delete_empties_is_named_and_stays()
    {
      var fixture = EvalFixture.Parse("grouped", "{\"canvas\":{\"components\":[{\"ref\":\"a\",\"type\":\"Panel\",\"group\":\"Notes\"},{\"ref\":\"b\",\"type\":\"Panel\",\"x\":400}]}}");
      var world = new EvalWorld(fixture, new ComponentList());
      var delete = new HandoffSeed { Ops = JsonNode.Parse("[{\"op\":\"delete\",\"id\":1}]").AsArray() };
      Assert.True(world.PrepareEdit(delete, out var plan, out var error), error?.Message);
      delete.Ops = plan.Record;

      Assert.True(world.ApplyEdit(PlanOf("edit", delete), out var text));

      Assert.Contains("The group \"Notes\" is now empty; it is not one Dwight made, so it was left.", text);
      Assert.Contains("the canvas went from 3 objects to 2.", text);
    }

    /// <summary>A spot group this world placed goes with the last thing in it, and a read no longer lists the region.</summary>
    [Fact]
    public void A_spot_group_that_a_delete_empties_goes_with_it()
    {
      var world = new EvalWorld(EvalFixture.Parse("empty", "{\"canvas\":{\"components\":[{\"ref\":\"b\",\"type\":\"Panel\",\"x\":400}]}}"), new ComponentList());
      var record = JsonNode.Parse("{\"components\":[{\"ref\":\"p\",\"type\":\"Panel\"}],\"wires\":[]}").AsObject();
      var placed = world.PlaceSpot(record, "Spot 1", true);
      Assert.True(placed.Ok, placed.Message);
      Assert.Contains("Spot 1", world.Regions);

      var delete = new HandoffSeed { Ops = JsonNode.Parse("[{\"op\":\"delete\",\"id\":2}]").AsArray() };   // the world's first id is the stated panel
      Assert.True(world.PrepareEdit(delete, out var plan, out var error), error?.Message);
      delete.Ops = plan.Record;
      Assert.True(world.ApplyEdit(PlanOf("edit", delete), out var text));

      Assert.Contains("held nothing else, so it went too.", text);
      Assert.Contains("the canvas went from 3 objects to 1.", text);
      Assert.DoesNotContain("Spot 1", world.Regions);
    }

    [Fact]
    public void A_deleted_component_leaves_its_group_and_the_layout_read()
    {
      var fixture = EvalFixture.Parse("grouped", "{\"canvas\":{\"components\":[" +
        "{\"ref\":\"a\",\"type\":\"Panel\",\"group\":\"Notes\"},{\"ref\":\"b\",\"type\":\"Panel\",\"group\":\"Notes\",\"x\":200},{\"ref\":\"c\",\"type\":\"Panel\",\"x\":400}]}}");
      var world = new EvalWorld(fixture, new ComponentList());

      foreach (var id in new[] { 1, 3 })
      {
        var seed = new HandoffSeed { Ops = JsonNode.Parse("[{\"op\":\"delete\",\"id\":" + id + "}]").AsArray() };
        Assert.True(world.PrepareEdit(seed, out var plan, out var error), error?.Message);
        seed.Ops = plan.Record;
        Assert.True(world.ApplyEdit(PlanOf("edit", seed), out _));
      }

      var layout = ReadOf(world, AgentToolCatalog.GetCanvasLayout, "{}")["result"];
      Assert.Equal(new[] { 2 }, layout["groups"][0]["members"].AsArray().Select(m => m.GetValue<int>()));
      Assert.Equal(0, layout["ungrouped"].GetValue<int>());   // the loose one has gone from the boxes too
    }

    /// <summary>The loop breaker reads the stamp, and a canvas that changed has a new one. A click that changed nothing leaves it.</summary>
    [Fact]
    public void A_change_that_lands_moves_the_stamp_and_one_that_does_not_leaves_it()
    {
      var world = WorldWithScript();
      var stamp = world.Stamp;

      var value = ValueSeed(1, Slider40);
      Assert.True(world.PrepareValue(value, out _));
      Assert.True(world.ApplyValue(PlanOf("set_value", value)).Ok);
      Assert.NotEqual(stamp, stamp = world.Stamp);

      var script = new HandoffSeed { Target = 4, Claim = "changes", Source = "result = 2" };
      Assert.True(world.PrepareScript(script, out _));
      Assert.True(world.ApplyScript(PlanOf("edit_script", script), out _));
      Assert.NotEqual(stamp, stamp = world.Stamp);

      var edit = new HandoffSeed { Ops = JsonNode.Parse("[{\"op\":\"delete\",\"id\":3}]").AsArray() };
      Assert.True(world.PrepareEdit(edit, out var plan, out _));
      edit.Ops = plan.Record;
      Assert.True(world.ApplyEdit(PlanOf("edit", edit), out _));
      Assert.NotEqual(stamp, stamp = world.Stamp);

      Assert.False(world.ApplyEdit(PlanOf("edit", edit), out var again));   // the same record again: its target has gone
      Assert.Contains("3", again);
      Assert.Equal(stamp, world.Stamp);
    }

    /// <summary>Two cards in one step, where the first removes what the second names: the click re-reads the world, so the second changes nothing and says so, and only an automatic approval goes back to the model for it.</summary>
    [Theory]
    [InlineData(ApprovalMode.Never, true)]
    [InlineData(ApprovalMode.Ask, false)]
    public void A_value_or_an_edit_the_first_card_outdated_is_not_applied(string mode, bool continues)
    {
      foreach (var (target, second, says) in new[]
      {
        (1, ValueAsk(1, Slider40), "[Not applied: the user approved this set_value, and Dwight changed nothing]"),
        (3, EditAsk("[{\"op\":\"delete\",\"id\":3}]"), "[Not applied - the canvas is unchanged]"),
      })
      {
        var both = Calls("q1", Request, EditAsk("[{\"op\":\"delete\",\"id\":" + target + "}]"));
        both.Blocks.Add(LlmBlock.ToolUse("q2", Request, second));
        var provider = new Fake(true, both, Says("I see."), Says("Done."));

        var result = Run(provider, Row(mode, "approve", "Go."), StatedWorld());

        Assert.Contains(Lines(result), l => l.Contains(says));
        Assert.Equal(continues, Lines(result).Contains(AutoFollowUp.Line));
      }
    }

    /// <summary>A rebuild replaces what the region still holds, and a deleted component's id must not stand in that list. The map's count follows too.</summary>
    [Fact]
    public void A_deleted_component_leaves_its_regions_count_and_what_a_rebuild_replaces()
    {
      var world = new EvalWorld(StatedWorld(), new ComponentList());
      var two = JsonNode.Parse("{\"components\":[{\"ref\":\"a\",\"type\":\"Panel\"},{\"ref\":\"b\",\"type\":\"Panel\"}]}").AsObject();
      Assert.True(world.PlaceSpot(two, "Spot 1", true).Ok);

      var seed = new HandoffSeed { Ops = JsonNode.Parse("[{\"op\":\"delete\",\"id\":4}]").AsArray() };
      Assert.True(world.PrepareEdit(seed, out var plan, out var error), error?.Message);
      seed.Ops = plan.Record;
      Assert.True(world.ApplyEdit(PlanOf("edit", seed), out _));

      Assert.Equal(1, ReadOf(world, AgentToolCatalog.GetScaffoldMap, "{}")["result"]["regions"].AsArray().Single(r => r["address"].GetValue<string>() == "Spot 1")["componentsInside"].GetValue<int>());
      var rebuilt = world.PlaceSpot(two, "Spot 1", true);
      Assert.True(rebuilt.Ok);
      Assert.Equal(1, rebuilt.Result["replaced"].GetValue<int>());
      Assert.Equal(ToolErrors.RetiredId, JsonNode.Parse(world.Answer(AgentToolCatalog.GetComponent, "{\"id\":5}", out _))["error"]["code"].GetValue<string>());
    }

    /// <summary>Two cards in one step: the second is stale by the time of its click. Not applied, and only an automatic approval goes back to the model, because nobody clicked.</summary>
    [Theory]
    [InlineData(ApprovalMode.Never, true)]
    [InlineData(ApprovalMode.Ask, false)]
    public void A_script_edit_the_first_card_outdated_is_not_applied_and_only_an_automatic_one_continues(string mode, bool continues)
    {
      var both = Calls("q2", Request, EditScriptAskFor(4, "result = 2"));
      both.Blocks.Add(LlmBlock.ToolUse("q3", Request, EditScriptAskFor(4, "result = 3")));
      var provider = new Fake(true, Calls("q1", Request, PlaceScript), Says("Down."), both, Says("I see."), Says("Done."));

      var result = Run(provider, Row(mode, "approve", "Place it.", "Change it."), StatedWorld());

      Assert.Contains(Lines(result), l => l.Contains("[Not applied - the canvas is unchanged]") && l.Contains("has changed since Dwight showed you the diff"));
      Assert.Equal(continues, Lines(result).Contains(AutoFollowUp.Line));
      Assert.Equal(continues ? 4 : 3, result.Turns.Count);
    }

    /// <summary>
    /// THE PANEL IS GH-BOUND, SO THE HARNESS COPIES ITS ORDER AND NOTHING FAILS WHEN IT MOVES. This fails. Each pair is one step, the panel's line and the harness's. If the panel's half
    /// went red, the panel's ask or click changed: change <c>RowRunner</c> and <c>EvalWorld</c> to match, or rows measure the harness (it happened twice), then re-pin here.
    /// </summary>
    [Theory]
    [InlineData("AppendReport(wp, doc, threadId, System.Net.WebUtility.HtmlEncode(ScreenAsk.Line), ScreenAsk.Fact)", "if (ScreenAsk.Asks(text, false)) Report(ScreenAsk.Fact);")]
    [InlineData("SpotAsk.Prepare(seed, resolvers, GhBuildRegion.LabelOfId(doc, scope.Ids),", "SpotAsk.Prepare(seed, Resolvers(), LabelOf, _mint,")]
    [InlineData("SpotMint.InUse(LiveRegions(doc, null), SavedTexts(doc))", "SpotMint.InUse(Regions, savedTexts)")]
    [InlineData("seed.SpotDone = ConsentCard.SpotDone(prepared?.Placement)", "seed.SpotDone = ConsentCard.SpotDone(spot.Placement)")]
    [InlineData("\"scripts\\\" belongs to a build into a map region. A placement without a map carries a script's source on the \" +", "\"scripts\\\" belongs to a build into a map region. A placement without a map carries a script's source on the \" +")]
    [InlineData("var scriptCount = spot?.Placement?.Scripts?.Count ?? cardScripts?.Count ?? 0;", "scriptCount = spot.Placement?.Scripts?.Count ?? 0;")]
    [InlineData("ApprovalMode.FactsFor(kind, scriptCount, editPlan?.Deletes ?? 0)", "ApprovalMode.FactsFor(kind, scriptCount, editPlan?.Deletes ?? 0)")]
    [InlineData("seed.Ops = editPlan.Record;", "seed.Ops = editPlan.Record;")]
    [InlineData("GhEditOps.Ask(doc, ScopeFor(threadId, doc).Ids, seed?.Ops, out prepared, out refused)", "EditOpsAsk.Validate(seed?.Ops, EditView(seed?.Ops), out plan, out error)")]
    [InlineData("var resolution = BuildScope.Resolve(plan.Regions, LiveRegions(doc, null), plan.PreFillSpot);", "var resolution = BuildScope.Resolve(plan.Regions, _world.Regions, plan.PreFillSpot);")]
    [InlineData("const string lost = \"The placement Dwight offered is no longer on this card, so nothing was built.\";", "const string lost = \"The placement Dwight offered is no longer on this card, so nothing was built.\";")]
    [InlineData("var landed = SpotAsk.PlacedReport(plan.PreFillPlace, plan.PreFillIntoName ?? plan.PreFillSpot, result.Result, plan.PreFillSpotGrouped) + (overlap == null", "var landed = SpotAsk.PlacedReport(plan.PreFillPlace, plan.PreFillIntoName ?? plan.PreFillSpot, result.Result, plan.PreFillSpotGrouped);")]
    [InlineData("m?.Who == \"me\" && m.Src != ApprovalMode.ClickUserText && m.Src != ApprovalMode.AutoUserText)?.Src", "m?.Who == \"me\" && m.Src != ApprovalMode.ClickUserText && m.Src != ApprovalMode.AutoUserText)?.Src")]
    [InlineData("\"The group this card named is no longer on the canvas, so nothing was built. Ask again and Dwight will look at what is there now.\"", "\"The group this card named is no longer on the canvas, so nothing was built. Ask again and Dwight will look at what is there now.\"")]
    [InlineData("FollowUpIfAuto(wp, doc, threadId, autoApproved, landed: false);   // U105: reverted or refused under an automatic mode", "if (AutoFollowUp.AfterApplied(autoApproved, false))")]
    [InlineData("if (!SetValueAsk.Validate(seed, IdStateFor(threadId, doc), id => GhSetValue.ReadLive(doc, scope.Ids, id),", "if (!SetValueAsk.Validate(seed, _ids.State, LiveValue, out var change, out _, out error)) return false;")]
    [InlineData("seed.TargetType = setValue.Type;", "seed.TargetType = change.Type;")]
    [InlineData("if (!EditScriptAsk.Validate(seed, IdStateFor(threadId, doc), id => GhEditScript.ReadLive(doc, editScope.Ids, id),", "if (!EditScriptAsk.Validate(seed, _ids.State, LiveScriptOf, out var change, out error)) return false;")]
    [InlineData("seed.BaseSource = editScript.BaseSource;", "seed.BaseSource = change.BaseSource;")]
    [InlineData("seed.Outcome = editOutcome.ToString();", "seed.Outcome = outcome.ToString();")]
    [InlineData("seed.Report = ScriptEditVerdict.WithAccess(ScriptEditVerdict.ReportLine(editOutcome, editFacts, ScriptEditVerdict.Subject(editScript)), editScript);", "seed.Report = ScriptEditVerdict.WithAccess(ScriptEditVerdict.ReportLine(outcome, facts, ScriptEditVerdict.Subject(change)), change);")]
    [InlineData("InputAccess = new Dictionary<string, string>(plan.InputAccess, StringComparer.OrdinalIgnoreCase),", "InputAccess = new Dictionary<string, string>(plan.InputAccess, StringComparer.OrdinalIgnoreCase),")]
    [InlineData("SetValueCore.MayProceed(idState, liveType, plan.TargetType, out var scopeRefusal)", "SetValueCore.MayProceed(idState, liveType, plan.TargetType, out var scopeRefusal)")]
    [InlineData("var again = new HandoffSeed { Target = plan.Target, State = plan.State };", "var again = new HandoffSeed { Target = plan.Target, State = plan.State };")]
    [InlineData("string.Equals(Dwight.Data.SourceToken.Normalise(plan.BaseSource),", "string.Equals(Dwight.Data.SourceToken.Normalise(plan.BaseSource),")]
    [InlineData("\"That component's source has changed since Dwight showed you the diff, so the change you \" +", "\"That component's source has changed since Dwight showed you the diff, so the change you \" +")]
    [InlineData("AppendReport(wp, doc, threadId, SetValueReport.Html(result), SetValueReport.Text(result));", "var report = SetValueReport.Text(result);")]
    [InlineData("if (result.Ok) RunTurn(wp, doc, threadId, SetValueCore.Continuation);", "if (result.Ok) next.Enqueue(new Pending { Capability = SetValueCore.Continuation });")]
    [InlineData("AppendReport(wp, doc, threadId, ScriptEditReport.Html(ok, text), ScriptEditReport.Text(ok, text));", "var report = ScriptEditReport.Text(ok, text);")]
    [InlineData("FollowUpIfAuto(wp, doc, threadId, autoMode != null, ok);   // U105", "FollowUpIfAuto(record, next, autoApproved, ok);")]
    [InlineData("if (ok) RunTurn(wp, doc, threadId, SetValueCore.Continuation);", "if (ok) next.Enqueue(new Pending { Capability = SetValueCore.Continuation });")]
    [InlineData("else FollowUpIfAuto(wp, doc, threadId, autoMode != null, landed: false);", "else FollowUpIfAuto(record, next, autoApproved, landed: false);")]
    [InlineData("Finish(true, EditOpsAsk.AppliedReport(plan, cardTotal, objectsBefore, objectsAfter, groups));", "text = EditOpsAsk.AppliedReport(edit, edit.Steps.Count, objectsBefore, CountedObjects(), groups);")]
    [InlineData("var groups = EditEmptyGroups.Decide(groupsBefore, deleted, g => d.FindObject(g, false) != null);", "var groups = EditEmptyGroups.Decide(groupsBefore, deleted, g => !deleted.Contains(g));")]
    public void The_panels_ask_and_click_and_the_harnesss_copy_are_still_the_same_steps(string panel, string harness)
    {
      var dir = new DirectoryInfo(AppContext.BaseDirectory);
      while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Dwight.sln"))) dir = dir.Parent;
      Assert.True(dir != null);
      string Read(params string[] parts) => File.ReadAllText(Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray()));

      Assert.Contains(panel, Read("Dwight", "UI", "DwightScaffold.cs") + Read("Dwight", "Agent", "GhEditOps.cs") + Read("Dwight", "Agent", "GhBuildRegion.cs"));
      Assert.Contains(harness, Read("tools", "AgentEval", "Harness", "RowRunner.cs") + Read("tools", "AgentEval", "Harness", "EvalWorld.cs"));
    }

    // ---- the seams leave the product's bytes alone -------------------------------------------------------------------

    [Fact]
    public void The_refusal_and_the_shown_card_keep_their_bytes()
    {
      Assert.Equal("{\"ok\":false,\"error\":{\"code\":\"precondition_refused\",\"message\":\"\\u0022x\\u0022 is not available on this turn. Reads are always available; changing the canvas needs the user to approve it first - ask with request_mutation.\"}}",
                   AgentLoopCore.GrantRefusal("x"));
      var shown = JsonNode.Parse(AgentHostTools.CardShownResult("build"));
      Assert.True(AgentHostTools.ShowedCard(AgentHostTools.RequestMutation, AgentHostTools.CardShownResult("build")));
      Assert.Equal("build", shown["result"]["kind"].GetValue<string>());
      Assert.Equal("Calling request_mutation again cannot produce an answer; it only asks the same question twice. Stop here.",
                   shown["result"]["doNotCallAgain"].GetValue<string>());
    }
  }
}
