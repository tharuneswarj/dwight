using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using Dwight.Agent;
using Dwight.Llm;
using Dwight.Models;
using Dwight.Session;
using Xunit;
using static Dwight.Agent.Tests.AgentTurnDriverTests;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// Brief CN: a turn that runs out of steps must leave the person something, and the next turn must not start over. Offshore Wind, 1,289 objects: three turns ended on the plugin's line alone, and each
  /// "carry on" read the same things from scratch. Driven through the real loop with a provider that records what it was sent. The wire is read per adapter; only a Rhino row can say what a model then does.
  /// </summary>
  [Collection(TurnShapeTests.StaticRecord)]
  public class ExhaustedTurnTests
  {
    private const string Ok = "{\"ok\":true,\"result\":{}}";

    private sealed class Recording : ILlmProvider
    {
      private readonly Queue<LlmResponse> _replies;
      public Recording(params LlmResponse[] replies) { _replies = new Queue<LlmResponse>(replies); }
      public string ModelId => "recording";
      public ProviderCapability Capability => default(ProviderCapability);
      public readonly List<LlmRequest> Seen = new List<LlmRequest>();
      public readonly List<List<LlmBlock>> LastBlocks = new List<List<LlmBlock>>();
      public Exception Throws;
      public bool SendsForcedTool { get; set; }
      public LlmResponse Send(LlmRequest request, CancellationToken cancel = default)
      {
        Seen.Add(request);
        LastBlocks.Add(request.Messages.Last().Blocks.ToList());
        if (Throws != null && _replies.Count == 0) throw Throws;
        return _replies.Dequeue();
      }
    }

    private static List<LlmTool> Tools() => new List<LlmTool>
    {
      new LlmTool { Name = AgentToolCatalog.GetOverview, Description = "d", InputSchemaJson = "{\"type\":\"object\",\"properties\":{}}" },
    };

    private static TurnResult Run(Recording provider, int maxSteps = 2, Func<LlmBlock, string> execute = null, string force = null) =>
      AgentTurnDriver.Drive(new TurnDrive
      {
        Provider = provider,
        System = "system",
        Messages = new List<LlmMessage> { LlmMessage.User("Add a turbine spacing check.") },
        Tools = Tools(),
        Limits = new TurnLimits { MaxSteps = maxSteps, WallClockMs = 0, MaxTranscriptChars = 1_000_000, AnswerBudget = 1000 },
        Execute = execute ?? (_ => Ok),
        ForceToolName = force,
      }, Stopwatch.StartNew());

    private static LlmResponse Read(string id) => Calls(id, AgentToolCatalog.GetOverview, "{}");

    private static string Repo(params string[] parts)
    {
      var dir = new DirectoryInfo(AppContext.BaseDirectory);
      while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Dwight.sln"))) dir = dir.Parent;
      Assert.True(dir != null);
      return File.ReadAllText(Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray())).Replace("\r\n", "\n");
    }

    // ---- 1. the closing step -------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_turn_out_of_steps_on_a_call_gets_exactly_one_closing_step_with_the_fact_and_no_tool_allowed()
    {
      var provider = new Recording(Read("a"), Read("b"), Says("I read the overview. I was about to look for the turbines. Tell me which region."));

      var result = Run(provider);

      Assert.Equal(3, provider.Seen.Count);
      Assert.Equal(TurnOutcome.Exhausted, result.Outcome);
      Assert.True(result.ClosingStep);
      Assert.Equal(2, result.Steps);   // the number the note states

      var closing = provider.Seen.Last();
      Assert.True(closing.ForbidTools);
      Assert.NotEmpty(closing.Tools);   // a tool_use in the history needs them defined; see LlmRequest.ForbidTools
      Assert.All(provider.Seen.Take(2), r => Assert.False(r.ForbidTools));

      // In the user turn, beside the results it follows, which were never sent without it.
      var last = provider.LastBlocks.Last();
      Assert.Equal(LlmRole.User, closing.Messages.Last().Role);
      Assert.Contains(last, b => b.Kind == "tool_result");
      Assert.Equal(AgentLoopCore.ClosingStepFact, last.Last().Text);
    }

    [Fact]
    public void The_closing_words_are_the_turns_text_above_the_note()
    {
      var result = Run(new Recording(Calls("a", AgentToolCatalog.GetOverview, "{}", "Reading the overview."), Read("b"), Says("I read the overview.")));

      Assert.Equal("I read the overview.", result.Text);
      Assert.Equal("Dwight used this turn's budget of 2 steps. Above is where it got to; ask again to carry on from there.", result.Note);
    }

    [Fact]
    public void The_fact_asks_what_the_ruling_asked()
    {
      Assert.Equal("PLUGIN FACT (from Dwight, not the user): you have used this turn's steps. Say, in a few sentences for the person: what you have read so far, what you were about to do, " +
                   "and what you need from them, if anything. Do not call a tool.", AgentLoopCore.ClosingStepFact);
    }

    /// <summary>The state a bound can leave with no call behind it: nothing to explain, so nothing is sent.</summary>
    [Fact]
    public void A_turn_that_ended_on_words_gets_no_closing_step()
    {
      Assert.False(AgentLoopCore.EarnsClosingStep(TurnOutcome.Exhausted, 0));
      Assert.True(AgentLoopCore.EarnsClosingStep(TurnOutcome.Exhausted, 1));
      foreach (var other in new[] { TurnOutcome.TimedOut, TurnOutcome.Stopped, TurnOutcome.Complete, TurnOutcome.Failed })
        Assert.False(AgentLoopCore.EarnsClosingStep(other, 3));

      var provider = new Recording(Says("Never sent."));
      var none = Run(provider, maxSteps: 0);
      Assert.Empty(provider.Seen);
      Assert.Equal(TurnOutcome.Exhausted, none.Outcome);
      Assert.False(none.ClosingStep);
      Assert.Equal("Dwight used this turn's budget of 0 steps without finishing. Ask again to carry on.", none.Note);
    }

    [Fact]
    public void A_complete_turn_is_sent_nothing_more()
    {
      var provider = new Recording(Read("a"), Says("Done."));
      var result = Run(provider, maxSteps: 2);

      Assert.Equal(2, provider.Seen.Count);
      Assert.Equal(TurnOutcome.Complete, result.Outcome);
      Assert.False(result.ClosingStep);
      Assert.All(provider.Seen, r => Assert.False(r.ForbidTools));
    }

    /// <summary>"If the model still returns no text, the note stands alone, as now."</summary>
    [Fact]
    public void A_closing_step_with_no_words_leaves_the_note_alone()
    {
      var result = Run(new Recording(Read("a"), Read("b"), Says("")));

      Assert.Equal(TurnOutcome.Exhausted, result.Outcome);
      Assert.Equal("Dwight used this turn's budget of 2 steps without finishing. Ask again to carry on.", result.Note);
    }

    /// <summary>A courtesy that fails must not turn "out of steps" into "failed".</summary>
    [Fact]
    public void A_closing_step_the_provider_refuses_stays_out_of_steps()
    {
      var provider = new Recording(Read("a"), Read("b")) { Throws = new InvalidOperationException("400") };

      var result = Run(provider);

      Assert.Equal(3, provider.Seen.Count);
      Assert.Equal(TurnOutcome.Exhausted, result.Outcome);
      Assert.Null(result.Error);
      Assert.Equal("Dwight used this turn's budget of 2 steps without finishing. Ask again to carry on.", result.Note);
    }

    [Fact]
    public void A_stop_during_the_closing_step_is_a_stop()
    {
      var provider = new Recording(Read("a"), Read("b")) { Throws = new OperationCanceledException() };
      Assert.Equal(TurnOutcome.Stopped, Run(provider).Outcome);
    }

    /// <summary>However the model answers, nothing runs: the step has no way to act.</summary>
    [Fact]
    public void A_call_in_the_closing_reply_is_not_run_and_there_is_no_second_closing_step()
    {
      var executed = 0;
      var provider = new Recording(Read("a"), Read("b"), Calls("c", AgentToolCatalog.GetOverview, "{}", "One more read."));

      var result = Run(provider, execute: _ => { executed++; return Ok; });

      Assert.Equal(2, executed);
      Assert.Equal(3, provider.Seen.Count);
      Assert.Equal(TurnOutcome.Exhausted, result.Outcome);
      Assert.Equal("One more read.", result.Text);
    }

    [Fact]
    public void The_closing_step_is_counted_in_tokens_and_not_in_steps_and_the_log_says_closing()
    {
      var closing = Says("Where I got to.");
      closing.Usage = new LlmUsage { InputTokens = 500, OutputTokens = 70 };
      var first = Read("a"); first.Usage = new LlmUsage { InputTokens = 100, OutputTokens = 10 };
      var second = Read("b"); second.Usage = new LlmUsage { InputTokens = 200, OutputTokens = 20 };

      var result = Run(new Recording(first, second, closing));

      Assert.Equal(800, result.Usage.InputTokens);
      Assert.Equal(100, result.Usage.OutputTokens);
      Assert.Equal(2, result.Steps);
      Assert.EndsWith(" closing=yes", AgentLoopCore.TurnEndLine("abc", result));
      Assert.DoesNotContain("closing", AgentLoopCore.TurnEndLine("abc", new TurnResult { Outcome = TurnOutcome.Complete }));
    }

    /// <summary>It was asked what it was about to do. Saying "I was about to raise the card" announces nothing, and the note says what happened.</summary>
    [Fact]
    public void What_it_was_about_to_do_draws_no_line_about_a_request()
    {
      var result = Run(new Recording(Read("a"), Read("b"), Says("Raising the card now.")));

      Assert.False(AgentLoopCore.AnnouncedNothingAsked(TurnCapability.ReadOnly, result));
      Assert.Null(AgentLoopCore.NothingAskedHtml(TurnCapability.ReadOnly, result));

      // The control: the same words on an ordinary ending do draw it.
      Assert.True(AgentLoopCore.AnnouncedNothingAsked(TurnCapability.ReadOnly, new TurnResult { Outcome = TurnOutcome.Exhausted, Text = "Raising the card now." }));
    }

    /// <summary>A forced step that ran out must not force the one step that may not call a tool.</summary>
    [Fact]
    public void A_forced_turn_forces_nothing_on_its_closing_step()
    {
      var tool = AgentToolCatalog.GetOverview;
      var provider = new Recording(Read("a"), Read("b"), Says("Where I got to.")) { SendsForcedTool = true };

      Run(provider, force: tool);

      Assert.Equal(tool, provider.Seen[0].ForceToolName);   // the control: this connection does force
      Assert.Null(provider.Seen.Last().ForceToolName);
      Assert.True(provider.Seen.Last().ForbidTools);
    }

    // ---- 1. the wire, per adapter -------------------------------------------------------------------------------------------------------------

    private static LlmRequest Closing(bool forbid = true) => new LlmRequest
    {
      System = "s", Messages = { LlmMessage.User("x") }, Tools = Tools(), ForbidTools = forbid,
    };

    [Fact]
    public void Anthropic_keeps_its_tools_and_says_none()
    {
      var body = JsonNode.Parse(AnthropicRequestBody.Build(Closing(), "claude-test", ThinkingWireState.Omit));
      Assert.Equal("none", body["tool_choice"]["type"].GetValue<string>());
      Assert.NotEmpty(body["tools"].AsArray());

      Assert.Null(JsonNode.Parse(AnthropicRequestBody.Build(Closing(false), "claude-test", ThinkingWireState.Omit))["tool_choice"]);
    }

    [Fact]
    public void OpenAI_keeps_its_tools_and_says_none()
    {
      var body = JsonNode.Parse(OpenAIResponses.BuildBody(Closing(), "model-test"));
      Assert.Equal("none", body["tool_choice"].GetValue<string>());
      Assert.NotEmpty(body["tools"].AsArray());

      Assert.Null(JsonNode.Parse(OpenAIResponses.BuildBody(Closing(false), "model-test"))["tool_choice"]);
    }

    [Fact]
    public void Google_keeps_its_tools_and_says_none()
    {
      var body = JsonNode.Parse(new GoogleProvider(null, "gemini-test").BuildBody(Closing(), structured: false));
      Assert.Equal("NONE", body["toolConfig"]["functionCallingConfig"]["mode"].GetValue<string>());
      Assert.NotEmpty(body["tools"].AsArray());

      Assert.Equal("AUTO", JsonNode.Parse(new GoogleProvider(null, "gemini-test").BuildBody(Closing(false), structured: false))["toolConfig"]["functionCallingConfig"]["mode"].GetValue<string>());
    }

    /// <summary>Ollama's endpoint rejects tool_choice (Brief BT, tested), so the only way to say it there is to send none.</summary>
    [Theory]
    [InlineData("Ollama Cloud")]
    [InlineData(OpenAIProvider.OllamaLocalLabel)]
    public void Ollama_has_no_tool_choice_so_its_tools_are_left_off(string label)
    {
      var provider = new OpenAIProvider("https://example.invalid/v1", null, "model-test", ProviderCapability.StrictSchema, true, label);
      var body = (JsonObject)JsonNode.Parse(provider.BuildBody(Closing(), structured: false));
      Assert.False(body.ContainsKey("tools"));
      Assert.False(body.ContainsKey("tool_choice"));

      Assert.True(((JsonObject)JsonNode.Parse(provider.BuildBody(Closing(false), structured: false))).ContainsKey("tools"));
    }

    /// <summary>The tool list stays with the Anthropic body through the driver: the step sends what every other step sent (the preserved-thinking prefix).</summary>
    [Fact]
    public void The_closing_step_sends_the_same_tools_as_the_steps_before_it()
    {
      var provider = new Recording(Read("a"), Read("b"), Says("Done."));
      Run(provider);
      Assert.Equal(provider.Seen[0].Tools.Select(t => t.Name), provider.Seen.Last().Tools.Select(t => t.Name));
    }

    // ---- 2. one call as a line --------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_line_is_the_tool_its_arguments_and_the_shape_of_its_result()
    {
      var overview = "{\"ok\":true,\"result\":{\"counts\":{\"components\":1929,\"params\":12,\"regions\":384},\"regions\":[{\"id\":1},{\"id\":2}],\"meaning\":{\"counts\":\"a long sentence\"}}}";
      Assert.Equal("get_overview -> components 1929, params 12, regions 384", TurnReadLine.Of("get_overview", "{}", overview));

      Assert.Equal("search {\"type\":\"turbine\"} -> matches 0", TurnReadLine.Of("search", "{ \"type\" : \"turbine\" }", "{\"ok\":true,\"result\":{\"matches\":[]}}"));
      Assert.Equal("get_region {\"id\":12} -> id 12, name Fins, nodes 41", TurnReadLine.Of("get_region", "{\"id\":12}", "{\"ok\":true,\"result\":{\"id\":12,\"name\":\"Fins\",\"nodes\":[" + string.Join(",", Enumerable.Range(0, 41)) + "]}}"));
    }

    /// <summary>No payloads: a value, a held text, a script's source and a sentence of the model's are not shapes.</summary>
    [Fact]
    public void A_line_carries_no_payload()
    {
      var envelope = "{\"ok\":true,\"result\":{\"id\":3,\"type\":\"Panel\",\"text\":\"SECRET-PANEL\",\"source\":\"SECRET-SOURCE\",\"value\":{\"value\":5.5},\"does\":\"SECRET-DESCRIPTION\",\"x\":120,\"note\":\"SECRET-NOTE\"}}";
      var line = TurnReadLine.Of("get_component", "{\"id\":3}", envelope);

      Assert.Equal("get_component {\"id\":3} -> id 3, type Panel", line);
      Assert.DoesNotContain("SECRET", line);
      Assert.Equal("done", TurnReadLine.Shape("{\"ok\":true,\"result\":{\"value\":5}}"));
    }

    [Fact]
    public void A_refusal_says_refused_and_its_code_and_anything_unreadable_says_so()
    {
      Assert.Equal("refused: retired_id", TurnReadLine.Shape("{\"ok\":false,\"error\":{\"code\":\"retired_id\",\"message\":\"SECRET\"}}"));
      Assert.Equal("refused", TurnReadLine.Shape("{\"ok\":false}"));
      Assert.Equal("unreadable", TurnReadLine.Shape("not json"));
      Assert.Equal("unreadable", TurnReadLine.Shape(null));
      Assert.Equal("done", TurnReadLine.Shape("{\"ok\":true}"));
      Assert.Equal("done", TurnReadLine.Shape(Ok));
    }

    [Fact]
    public void A_line_is_bounded_and_says_what_it_left_out()
    {
      var fields = string.Join(",", Enumerable.Range(0, 10).Select(i => "\"k" + i + "\":[]"));
      Assert.EndsWith(" and 4 more", TurnReadLine.Shape("{\"ok\":true,\"result\":{" + fields + "}}"));

      var longArgs = "{\"q\":\"" + new string('a', 200) + "\"}";
      var line = TurnReadLine.Of("search", longArgs, Ok);
      Assert.Contains("...", line);
      Assert.True(line.Length < 120, line);

      Assert.Equal("search -> done", TurnReadLine.Of("search", null, Ok));
      Assert.Equal("(none) -> done", TurnReadLine.Of(null, "", Ok));
    }

    // ---- 2. the next turn's fact --------------------------------------------------------------------------------------------------------------

    private static TurnResult Exhausted(params string[] trail) => new TurnResult { Outcome = TurnOutcome.Exhausted, ToolTrail = trail.ToList(), Text = "MODEL-WORDS" };

    [Fact]
    public void The_trail_comes_from_the_loops_own_calls_and_results_and_not_from_what_the_model_said()
    {
      var provider = new Recording(
        Calls("a", AgentToolCatalog.GetOverview, "{}", "MODEL-SAID-ONE"),
        Calls("b", AgentToolCatalog.Search, "{\"type\":\"turbine\"}", "MODEL-SAID-TWO"),
        Says("MODEL-SAID-THREE"));

      var result = Run(provider, execute: c => c.ToolName == AgentToolCatalog.Search
        ? "{\"ok\":true,\"result\":{\"matches\":[]}}"
        : "{\"ok\":true,\"result\":{\"counts\":{\"components\":1929,\"regions\":384}}}");

      Assert.Equal(new[] { "get_overview -> components 1929, regions 384", "search {\"type\":\"turbine\"} -> matches 0" }, result.ToolTrail);

      var reads = new ExhaustedReads();
      reads.Note("t", result);
      var fact = reads.FactFor("t");
      Assert.StartsWith("PLUGIN FACT (from Dwight, not the user): your last turn ran out of its steps", fact);
      Assert.Contains("You read: get_overview -> components 1929, regions 384; search {\"type\":\"turbine\"} -> matches 0.", fact);
      Assert.Contains("Those results are not repeated here.", fact);
      Assert.Contains("Call a tool again only for what you did not read or need the detail of; do not start again from the top.", fact);
      Assert.DoesNotContain("MODEL-SAID", fact);
    }

    [Fact]
    public void The_fact_is_gone_after_a_complete_turn_and_stays_through_any_other_ending()
    {
      var reads = new ExhaustedReads();
      Assert.Null(reads.FactFor("t"));

      reads.Note("t", Exhausted("get_overview -> done"));
      Assert.NotNull(reads.FactFor("t"));

      foreach (var other in new[] { TurnOutcome.Stopped, TurnOutcome.Failed, TurnOutcome.TimedOut, TurnOutcome.Truncated })
      {
        reads.Note("t", new TurnResult { Outcome = other, ToolTrail = { "NOT-KEPT" } });
        Assert.NotNull(reads.FactFor("t"));
        Assert.DoesNotContain("NOT-KEPT", reads.FactFor("t"));
      }

      reads.Note("t", new TurnResult { Outcome = TurnOutcome.Complete });
      Assert.Null(reads.FactFor("t"));
    }

    /// <summary>The gate's own row: three carry-ons. A third turn told only of the second would read the first's again.</summary>
    [Fact]
    public void Consecutive_turns_out_of_steps_add_up_and_each_thread_has_its_own()
    {
      var reads = new ExhaustedReads();
      reads.Note("t", Exhausted("first"));
      reads.Note("t", Exhausted("second"));
      reads.Note("other", Exhausted("elsewhere"));

      Assert.Contains("You read: first; second.", reads.FactFor("t"));
      Assert.DoesNotContain("elsewhere", reads.FactFor("t"));
      Assert.Contains("You read: elsewhere.", reads.FactFor("other"));
    }

    [Fact]
    public void A_turn_out_of_steps_with_no_calls_adds_no_fact()
    {
      var reads = new ExhaustedReads();
      reads.Note("t", Exhausted());
      Assert.Null(reads.FactFor("t"));
    }

    /// <summary>Bounded, as every fact is (ADR-035), keeping what is nearest to where it stopped and saying what it left out.</summary>
    [Fact]
    public void The_fact_is_bounded_by_lines_and_by_characters_and_says_what_it_left_out()
    {
      var reads = new ExhaustedReads();
      reads.Note("t", Exhausted(Enumerable.Range(0, 100).Select(i => "call" + i).ToArray()));
      var byLines = reads.FactFor("t");
      Assert.Contains("call99", byLines);
      Assert.Contains("call40", byLines);
      Assert.DoesNotContain("call39;", byLines);
      Assert.Contains(" 40 earlier call(s) are not listed.", byLines);

      var wide = new ExhaustedReads();
      wide.Note("t", Exhausted(Enumerable.Range(0, 20).Select(i => "c" + i + new string('x', 400)).ToArray()));
      var byChars = wide.FactFor("t");
      Assert.Contains("c19", byChars);
      Assert.DoesNotContain("c0x", byChars);
      Assert.Contains("earlier call(s) are not listed.", byChars);   // a line with no shape to shed still has a limit
      Assert.True(byChars.Length < ExhaustedReads.MaxChars + 600, byChars.Length.ToString());
    }

    /// <summary>A live row of 43 calls: keeping only the newest cut the overview, and what a fact does not list is what gets read again. Every call stays named; the newest also carry their shape.</summary>
    [Fact]
    public void Past_the_shapes_budget_an_older_call_keeps_its_name_so_the_overview_is_never_the_one_lost()
    {
      var calls = new List<string> { "get_overview -> components 32, regions 1", "get_region {\"id\":0} -> nodes 40" };
      calls.AddRange(Enumerable.Range(1, 41).Select(i => "get_component {\"id\":" + i + "} -> id " + i + ", type Number Slider, nick Number Slider, inputs 0, outputs 1, msgs 0 and 1 more"));
      var reads = new ExhaustedReads();
      reads.Note("t", Exhausted(calls.ToArray()));

      var fact = reads.FactFor("t");

      Assert.Contains("You read: get_overview; get_region {\"id\":0}; get_component {\"id\":1}; ", fact);    // older: named
      Assert.Contains("get_component {\"id\":41} -> id 41, type Number Slider", fact);                         // newest: with their shape
      Assert.DoesNotContain("not listed", fact);
      Assert.True(fact.Length < ExhaustedReads.MaxChars + 600, fact.Length.ToString());
    }

    [Fact]
    public void The_panel_states_it_at_turn_start_and_notes_every_ending()
    {
      var panel = Repo("Dwight", "UI", "DwightScaffold.cs");
      var read = panel.IndexOf("var lastReads = _exhaustedReads.FactFor(threadId);", StringComparison.Ordinal);
      var compose = panel.IndexOf("var system = TurnSystemText.Compose(situation,", StringComparison.Ordinal);
      Assert.True(0 < read && read < compose, "the fact must reach the system text");
      Assert.Contains("if (lastReads != null) situation = (situation ?? \"\") + \"\\n\\n\" + lastReads;", panel);
      Assert.Contains("_exhaustedReads.Note(threadId, result);", panel);

      var harness = Repo("tools", "AgentEval", "Harness", "RowRunner.cs");
      Assert.Contains("var lastReads = _exhaustedReads.FactFor(_row.Id);", harness);
      Assert.Contains("_exhaustedReads.Note(_row.Id, result);", harness);
    }

    // ---- 3. the Setting ------------------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(12, 12)]
    [InlineData(4, 4)]
    [InlineData(40, 40)]
    [InlineData(3, 4)]
    [InlineData(0, 4)]
    [InlineData(-5, 4)]
    [InlineData(41, 40)]
    [InlineData(100000, 40)]
    public void The_setting_stays_in_bounds(int stored, int used)
    {
      Assert.Equal(used, ChatSteps.Clamp(stored));
    }

    [Fact]
    public void The_default_is_the_budget_it_replaced_and_a_build_keeps_its_own()
    {
      Assert.Equal(12, DwightConfig.AgentMaxStepsPerTurn);
      Assert.Equal(12, new ChatHeaderInput().StepsPerTurn);
      Assert.Equal(30, DwightConfig.AgentBuildMaxStepsPerTurn);

      var panel = Repo("Dwight", "UI", "DwightScaffold.cs");
      Assert.Contains("MaxSteps = DwightConfig.AgentBuildMaxStepsPerTurn,", panel);
      Assert.Contains("MaxSteps = ChatSteps.Clamp(DwightSettings.Current.StepsPerTurn),", panel);
      Assert.Contains("public int StepsPerTurn { get; set; } = DwightConfig.AgentMaxStepsPerTurn;", Repo("Dwight", "Config", "DwightSettings.cs"));
    }

    [Fact]
    public void Settings_offers_the_bounds_and_says_the_default_and_the_builds_own_budget()
    {
      var html = Repo("Dwight", "Resources", "settings.html");
      Assert.Contains("<label>Steps per turn</label><div class=\"dd\" id=\"steps\" data-vals=\"4 8 12 16 20 30 40\">", html);
      var values = "4 8 12 16 20 30 40".Split(' ').Select(int.Parse).ToArray();
      Assert.Equal(ChatSteps.Min, values.First());
      Assert.Equal(ChatSteps.Max, values.Last());
      Assert.Contains(DwightConfig.AgentMaxStepsPerTurn, values);
      Assert.Contains(">12: the default.</div>", html);
      Assert.Contains("An approved build has its own budget of " + DwightConfig.AgentBuildMaxStepsPerTurn + ".", html);
      Assert.Contains("A longer task, such as a build with many regions or a long script, may need more steps; raise this if turns end before the work is done.</div>", html);   // Brief EE unit 3
      Assert.Contains("choice('steps','Steps per turn',String(d.stepsPerTurn||12),setSteps);", html);
      Assert.Contains("dwightSend('steps:'+v)", html);

      var panel = Repo("Dwight", "UI", "DwightSettingsPanel.cs");
      Assert.Contains("[\"stepsPerTurn\"] = Dwight.Agent.ChatSteps.Clamp(s.StepsPerTurn),", panel);
      Assert.Contains("st.StepsPerTurn = Dwight.Agent.ChatSteps.Clamp(steps); st.Save();", panel);
    }

    /// <summary>The chosen value on the thread's line, so the number the note states is one the person has seen.</summary>
    [Theory]
    [InlineData(12, "12 steps a turn")]
    [InlineData(30, "30 steps a turn")]
    [InlineData(1000, "40 steps a turn")]
    public void The_chats_line_says_the_number(int steps, string text)
    {
      Assert.Equal(text, ChatHeader.StepsChip(steps));
    }

    [Fact]
    public void The_header_carries_it_after_the_cost_and_the_panel_hands_it_in()
    {
      var json = JsonNode.Parse(ChatHeader.Json(new ChatHeaderInput { ProviderModels = new List<ModelInfo>(), Spend = SpendMath.Sum(new StoredSpend[0]), StepsPerTurn = 20 }));
      Assert.Equal("no turns yet · 20 steps a turn", json["cost"]["text"].GetValue<string>());

      Assert.Contains("StepsPerTurn = settings.StepsPerTurn,", Repo("Dwight", "UI", "DwightScaffold.cs"));
    }

    [Fact]
    public void The_pages_say_it()
    {
      Assert.Contains("**Steps per turn**, under Build in Settings", Repo("docs", "public", "build.md"));
      Assert.Contains("12 by default, from 4 to 40", Repo("docs", "public", "build.md"));
      Assert.Contains("An approved build has its own budget of 30.", Repo("docs", "public", "build.md"));
      Assert.Contains("1 November 2026: a chat turn that runs out of steps now says where it got to", Repo("docs", "public", "changelog.md"));
    }
  }
}
