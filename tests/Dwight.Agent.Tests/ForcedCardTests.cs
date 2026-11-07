using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using Dwight.Agent;
using Dwight.Llm;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// Brief BT: the follow-up to an announcement that raised no card forces request_mutation. Each provider's request body through its real
  /// serialiser with the published descriptor; then the real driver with a scripted model; then the panel's wiring, read from the source
  /// because the panel is GH-bound. What a real model does under forcing is the gate's row, not this file's.
  /// </summary>
  [Collection(TurnShapeTests.StaticRecord)]
  public class ForcedCardTests
  {
    private const string Request = AgentHostTools.RequestMutation;

    /// <summary>The tools a chat turn offers, as the loop maps them, the host's request_mutation among them.</summary>
    private static List<LlmTool> PublishedTools() =>
      AgentToolCatalog.All().Concat(AgentHostTools.All())
        .Select(t => new LlmTool { Name = t.Name, Description = t.Description, InputSchemaJson = t.InputSchemaJson }).ToList();

    private static LlmRequest Forced(bool thinking = false, string effort = null) => new LlmRequest
    {
      System = "s",
      Messages = { LlmMessage.User("PLUGIN FACT (from Dwight, not the user): raise the card now.") },
      Tools = PublishedTools(),
      ForceToolName = Request,
      Thinking = thinking,
      Effort = effort,
    };

    private static OpenAIProvider OpenAi(string label) =>
      new OpenAIProvider("https://example.invalid/v1", null, "model-test", ProviderCapability.StrictSchema, true, label);

    private static JsonObject OpenAiBody(string label, LlmRequest r) => (JsonObject)JsonNode.Parse(OpenAi(label).BuildBody(r, structured: false));

    // ---- the wire, per provider ------------------------------------------------------------------------

    [Fact]
    public void OpenAI_names_the_function_in_the_Responses_shape_and_keeps_every_tool()
    {
      // Brief BV: OpenAI's tool-bearing turns moved to the Responses endpoint, whose forced tool is flat.
      var body = (JsonObject)JsonNode.Parse(OpenAIResponses.BuildBody(Forced(thinking: true, effort: "medium"), "model-test"));

      Assert.Equal("function", body["tool_choice"]["type"].GetValue<string>());
      Assert.Equal(Request, body["tool_choice"]["name"].GetValue<string>());
      Assert.Equal("medium", body["reasoning"]["effort"].GetValue<string>());
      Assert.Equal(PublishedTools().Count, body["tools"].AsArray().Count);
      Assert.Contains(body["tools"].AsArray(), t => t["name"].GetValue<string>() == Request);
      Assert.True(((ILlmProvider)OpenAi("OpenAI")).SendsForcedTool);
    }

    [Theory]
    [InlineData("Ollama Cloud")]
    [InlineData(OpenAIProvider.OllamaLocalLabel)]
    public void Ollama_never_receives_tool_choice_and_says_it_cannot_force(string label)
    {
      var body = OpenAiBody(label, Forced());

      Assert.False(body.ContainsKey("tool_choice"));
      Assert.False(body.ContainsKey("reasoning_effort"));
      Assert.Contains(body["tools"].AsArray(), t => t["function"]["name"].GetValue<string>() == Request);
      Assert.False(((ILlmProvider)OpenAi(label)).SendsForcedTool);
    }

    [Fact]
    public void Ordinary_turns_keep_their_bodies()
    {
      var unforced = Forced();
      unforced.ForceToolName = null;

      Assert.False(((JsonObject)JsonNode.Parse(OpenAIResponses.BuildBody(unforced, "model-test"))).ContainsKey("tool_choice"));
      var google = (JsonObject)JsonNode.Parse(new GoogleProvider(null, "gemini-test").BuildBody(unforced, structured: false));
      Assert.Equal("{\"mode\":\"AUTO\"}", google["toolConfig"]["functionCallingConfig"].ToJsonString());
      var anthropic = (JsonObject)JsonNode.Parse(AnthropicRequestBody.Build(unforced, "claude-test", ThinkingWireState.Omit));
      Assert.Null(anthropic["tool_choice"]);
    }

    [Fact]
    public void Google_asks_for_mode_ANY_with_the_one_allowed_name()
    {
      var body = (JsonObject)JsonNode.Parse(new GoogleProvider(null, "gemini-test").BuildBody(Forced(), structured: false));

      var config = body["toolConfig"]["functionCallingConfig"];
      Assert.Equal("ANY", config["mode"].GetValue<string>());
      Assert.Equal(Request, config["allowedFunctionNames"].AsArray().Single().GetValue<string>());
      Assert.Contains(body["tools"].AsArray().Single()["functionDeclarations"].AsArray(), d => d["name"].GetValue<string>() == Request);
      Assert.True(((ILlmProvider)new GoogleProvider(null, "gemini-test")).SendsForcedTool);
    }

    /// <summary>A forced step sends thinking off, so the Anthropic body is one of the two the comprehension verbs already ship: Disabled on an
    /// adaptive-on model (Sonnet 5, Opus 5), nothing on an adaptive-off one (Sonnet 4.6, Opus 4.8). Neither carries an effort.</summary>
    [Theory]
    [InlineData((int)ThinkingWireState.Disabled, "disabled")]
    [InlineData((int)ThinkingWireState.Omit, null)]
    public void Anthropic_forces_the_tool_on_the_wire_comprehension_already_ships(int state, string thinkingType)
    {
      var body = (JsonObject)JsonNode.Parse(AnthropicRequestBody.Build(Forced(), "claude-test", (ThinkingWireState)state));

      Assert.Equal("tool", body["tool_choice"]["type"].GetValue<string>());
      Assert.Equal(Request, body["tool_choice"]["name"].GetValue<string>());
      Assert.Equal(thinkingType, body["thinking"]?["type"]?.GetValue<string>());
      Assert.Null(body["output_config"]);
      Assert.Contains(body["tools"].AsArray(), t => t["name"].GetValue<string>() == Request);
    }

    /// <summary>What decides which of those two bodies a model gets: the catalogue's fact, through the real resolver (ADR-082). A task that does
    /// not want thinking never reaches Adaptive, which is why a forced step cannot send forced-plus-adaptive on any row.</summary>
    [Theory]
    [InlineData("adaptive-on", (int)ThinkingWireState.Disabled)]
    [InlineData("adaptive-off", (int)ThinkingWireState.Omit)]
    [InlineData("none", (int)ThinkingWireState.Omit)]
    public void A_forced_step_never_reaches_adaptive_whatever_the_catalogue_says(string thinkingDefault, int expected)
    {
      var state = ThinkingWire.Resolve(taskWantsThinking: false, ThinkingWire.IsOnByDefault(thinkingDefault), ThinkingWire.SupportsAdaptive(thinkingDefault));
      Assert.Equal((ThinkingWireState)expected, state);
    }

    // ---- the driver ------------------------------------------------------------------------------------

    /// <summary>A scripted model that says what its connection can do and records every request it was sent.</summary>
    private sealed class Recording : ILlmProvider
    {
      private readonly Queue<LlmResponse> _replies;
      public Recording(bool forces, params LlmResponse[] replies) { SendsForcedTool = forces; _replies = new Queue<LlmResponse>(replies); }

      public string ModelId => "scripted";
      public ProviderCapability Capability => default(ProviderCapability);
      public bool SendsForcedTool { get; }
      public readonly List<LlmRequest> Seen = new List<LlmRequest>();
      public readonly List<List<string>> ToolResultsSeen = new List<List<string>>();

      public LlmResponse Send(LlmRequest request, CancellationToken cancel = default)
      {
        Seen.Add(request);
        ToolResultsSeen.Add(request.Messages.SelectMany(m => m.Blocks).Where(b => b.Kind == "tool_result").Select(b => b.Text).ToList());
        if (_replies.Count == 0) throw new InvalidOperationException("the script ran out of replies");
        return _replies.Dequeue();
      }
    }

    private static TurnResult Drive(Recording provider, Func<LlmBlock, string> execute, string force = Request, List<LlmTool> tools = null) =>
      AgentTurnDriver.Drive(new TurnDrive
      {
        Provider = provider,
        System = "system",
        Messages = new List<LlmMessage> { LlmMessage.User("PLUGIN FACT") },
        Tools = tools ?? PublishedTools(),
        Limits = new TurnLimits { MaxSteps = 8, WallClockMs = 0, MaxTranscriptChars = 1_000_000, AnswerBudget = 1000 },
        Thinking = true,
        Effort = "medium",
        ServerWebSearch = true,
        ForceToolName = force,
        Execute = execute,
      }, Stopwatch.StartNew());

    private static string RefusalNamed(string message) =>
      "{\"ok\":false,\"error\":{\"code\":\"precondition_refused\",\"message\":\"" + message + "\"}}";

    private static LlmResponse Asks(string id) => AgentTurnDriverTests.Calls(id, Request, "{\"kind\":\"scaffold\"}");

    [Fact]
    public void Announced_and_no_card_then_forced_then_a_card()
    {
      // The turn the catch reads. Nothing is forced on it: nothing had been announced yet.
      var first = new Recording(true, AgentTurnDriverTests.Says("I'll create a separate lamp scaffold first."));
      var announced = Drive(first, c => throw new InvalidOperationException("no tool was called"), force: null);
      Assert.Null(first.Seen.Single().ForceToolName);
      Assert.True(first.Seen.Single().Thinking);
      Assert.True(AgentLoopCore.AnnouncedForForcing(TurnCapability.ReadOnly, announced));
      Assert.True(AutoFollowUp.ShouldRunForAnnouncement(true, announced.ProviderForces, true, false));

      // The follow-up: forced, with thinking off and its search out of the way, and one call ends it on a card.
      var second = new Recording(true, Asks("t1"));
      var followUp = Drive(second, c => AgentTurnDriverTests.CardShown);
      var sent = second.Seen.Single();
      Assert.Equal(Request, sent.ForceToolName);
      Assert.False(sent.Thinking);
      Assert.Null(sent.Effort);
      Assert.False(sent.ServerWebSearch);
      Assert.True(followUp.AskedForConsent);
      Assert.True(followUp.Forced);
      Assert.False(AgentLoopCore.FollowUpRaisedNoCard(true, followUp));
    }

    [Fact]
    public void A_refusal_goes_to_the_model_once_and_the_next_forced_call_can_raise_the_card()
    {
      var provider = new Recording(true, Asks("t1"), Asks("t2"));
      var results = new Queue<string>(new[] { RefusalNamed("REFUSAL-FOR-THE-MODEL"), AgentTurnDriverTests.CardShown });

      var result = Drive(provider, c => results.Dequeue());

      Assert.Equal(2, result.Steps);
      Assert.Contains(provider.ToolResultsSeen[1], t => t.Contains("REFUSAL-FOR-THE-MODEL"));
      Assert.Equal(Request, provider.Seen[1].ForceToolName);
      Assert.True(result.AskedForConsent);
    }

    /// <summary>The one-try bound inside the turn. Two different refusals would pass #973's bound and buy a third call; a forced turn stops at
    /// two, and the sentence on screen is the plugin's, naming the class and not the words written for the model.</summary>
    [Fact]
    public void Two_different_refusals_end_a_forced_turn_with_the_plugins_sentence()
    {
      var provider = new Recording(true, Asks("t1"), Asks("t2"), Asks("t3"));
      var results = new Queue<string>(new[] { RefusalNamed("FIRST-FOR-THE-MODEL"), RefusalNamed("SECOND-FOR-THE-MODEL"), AgentTurnDriverTests.CardShown });

      var result = Drive(provider, c => results.Dequeue());

      Assert.Equal(2, result.Steps);
      Assert.False(result.AskedForConsent);
      Assert.True(AgentLoopCore.FollowUpRaisedNoCard(true, result));
      var sentence = AutoFollowUp.NoCardSentence(result.Note, result.RefusedRequests);
      Assert.Equal(result.Note, sentence);
      Assert.StartsWith("Dwight stopped:", sentence);
      Assert.DoesNotContain("FOR-THE-MODEL", sentence);
    }

    [Fact]
    public void The_same_refusal_twice_ends_it_too()
    {
      var provider = new Recording(true, Asks("t1"), Asks("t2"), Asks("t3"));

      var result = Drive(provider, c => RefusalNamed("SAME"));

      Assert.Equal(2, result.Steps);
      Assert.NotNull(result.Note);
    }

    /// <summary>Forcing is the provider's promise and a real one still answers in prose sometimes. The prose is not the reason.</summary>
    [Fact]
    public void A_forced_turn_that_answers_in_prose_gets_the_plugins_sentence_and_not_its_excuse()
    {
      const string excuse = "I can't raise the build card from this turn because the required mutation request is unavailable.";
      var provider = new Recording(true, AgentTurnDriverTests.Says(excuse));

      var result = Drive(provider, c => throw new InvalidOperationException("no tool was called"));

      Assert.True(AgentLoopCore.FollowUpRaisedNoCard(true, result));
      Assert.Equal(AutoFollowUp.NoCardLine, AutoFollowUp.NoCardSentence(result.Note, result.RefusedRequests));
      Assert.DoesNotContain("unavailable", AutoFollowUp.NoCardLine);
    }

    /// <summary>Ollama: nothing forced reaches the provider, so its reads still work, and its bound is the ordinary one.</summary>
    [Fact]
    public void A_connection_that_cannot_force_runs_the_same_turn_unforced_with_its_thinking()
    {
      var provider = new Recording(false,
        AgentTurnDriverTests.Calls("r1", AgentToolCatalog.GetOverview, "{}"),
        Asks("t1"),
        AgentTurnDriverTests.Says("I could not."));
      var results = new Queue<string>(new[] { "{\"ok\":true,\"result\":{}}", RefusalNamed("ONE") });

      var result = Drive(provider, c => results.Dequeue());

      Assert.All(provider.Seen, r => Assert.Null(r.ForceToolName));
      Assert.True(provider.Seen[0].Thinking);
      Assert.Equal("medium", provider.Seen[0].Effort);
      Assert.False(result.Forced);
      Assert.Equal(3, result.Steps);
      Assert.Equal(AutoFollowUp.RefusedLine, AutoFollowUp.NoCardSentence(result.Note, result.RefusedRequests));
      Assert.False(AutoFollowUp.ShouldRunForAnnouncement(false, result.ProviderForces, true, false));
    }

    /// <summary>A forced call too long for the answer budget is cut off, not half-run. The adapters dispatch no call from a cut reply, so the
    /// turn ends on the Truncated outcome with its own note, and the try is spent. The report states how long a script that allows.</summary>
    [Fact]
    public void A_forced_call_cut_off_by_the_budget_is_not_dispatched_and_keeps_its_own_note()
    {
      var provider = new Recording(true, new LlmResponse { StopReason = "max_tokens" });

      var result = Drive(provider, c => throw new InvalidOperationException("a cut-off call must not run"));

      Assert.Equal(TurnOutcome.Truncated, result.Outcome);
      Assert.NotNull(result.Note);
      Assert.True(AgentLoopCore.FollowUpRaisedNoCard(true, result));
      Assert.Equal(1000, provider.Seen.Single().MaxTokens);   // the answer budget, whole: a forced step reserves nothing for thinking
    }

    [Fact]
    public void A_tool_the_turn_does_not_offer_is_never_forced()
    {
      var provider = new Recording(true, AgentTurnDriverTests.Says("Nothing to do."));

      var result = Drive(provider, c => "{}", tools: PublishedTools().Where(t => t.Name != Request).ToList());

      Assert.Null(provider.Seen.Single().ForceToolName);
      Assert.False(result.Forced);
      Assert.False(result.RequestOffered);
    }

    [Fact]
    public void No_forcing_on_a_turn_that_raised_a_card_or_was_stopped_or_is_a_build()
    {
      var provider = new Recording(true, AgentTurnDriverTests.Calls("t1", Request, "{\"kind\":\"build\"}", prose: "I'll build A1 now."));
      var carded = Drive(provider, c => AgentTurnDriverTests.CardShown, force: null);
      Assert.True(carded.AskedForConsent);
      Assert.False(AgentLoopCore.AnnouncedForForcing(TurnCapability.ReadOnly, carded));

      const string announcing = "I'll build A1 now.";
      Assert.True(AgentLoopCore.AnnouncedForForcing(TurnCapability.ReadOnly, new TurnResult { Outcome = TurnOutcome.Complete, LastStepText = announcing }));
      Assert.False(AgentLoopCore.AnnouncedForForcing(TurnCapability.Build, new TurnResult { Outcome = TurnOutcome.Complete, LastStepText = announcing }));
      Assert.False(AgentLoopCore.AnnouncedForForcing(TurnCapability.ReadOnly, new TurnResult { Outcome = TurnOutcome.Stopped, LastStepText = announcing }));
      Assert.False(AgentLoopCore.AnnouncedForForcing(TurnCapability.ReadOnly, new TurnResult { Outcome = TurnOutcome.Truncated, LastStepText = announcing }));
      Assert.False(AgentLoopCore.FollowUpRaisedNoCard(true, new TurnResult { Outcome = TurnOutcome.Stopped }));
      Assert.False(AgentLoopCore.FollowUpRaisedNoCard(false, new TurnResult { Outcome = TurnOutcome.Complete }));
    }

    /// <summary>Thread f21fed05, the lamp on Sonnet 5 (#1168): reasoning prose beside calls the turn did make. Prose on a step that called is
    /// not the turn's last word, and an empty last reply does not make it so.</summary>
    [Theory]
    [InlineData("All four regions are built and verified.")]
    [InlineData("")]
    public void Reasoning_prose_beside_a_call_stays_quiet_whatever_the_last_reply_says(string lastWord)
    {
      var provider = new Recording(true,
        AgentTurnDriverTests.Calls("r1", AgentToolCatalog.GetOverview, "{}", prose: "I'll build conservatively."),
        AgentTurnDriverTests.Calls("r2", AgentToolCatalog.GetOverview, "{}", prose: "Now D1: SilkWorm Output, then I'll wire it."),
        lastWord.Length > 0 ? AgentTurnDriverTests.Says(lastWord) : new LlmResponse { StopReason = "end_turn" });

      var result = Drive(provider, c => "{\"ok\":true,\"result\":{}}", force: null);

      Assert.Equal(0, result.LastStepToolCalls);
      Assert.Equal(lastWord, result.LastStepText);
      Assert.False(AgentLoopCore.AnnouncedForForcing(TurnCapability.ReadOnly, result));
    }

    [Fact]
    public void One_forced_try_per_user_message_per_thread()
    {
      var bound = new ForcedTryBound();

      Assert.True(bound.TryTake("t"));
      Assert.False(bound.TryTake("t"));   // an approval's next turn announces again: the line, no second try
      Assert.True(bound.TryTake("other"));
      bound.Reset("t");                   // a message of the user's own
      Assert.True(bound.TryTake("t"));
    }

    // ---- the turn line ---------------------------------------------------------------------------------

    [Fact]
    public void The_turn_line_says_thread_stop_offered_and_forced_and_carries_no_payload()
    {
      var provider = new Recording(true, Asks("t1"));
      var result = Drive(provider, c => AgentTurnDriverTests.CardShown);
      result.Text = "PROSE-THAT-MUST-NOT-APPEAR";

      Assert.Equal("completion on the LIVE generation (outcome Complete) thread=abc stop=tool_use request_mutation offered=yes forced=yes",
                   AgentLoopCore.TurnEndLine("abc", result));
      Assert.Equal("completion on the LIVE generation (outcome Complete) thread=abc stop=none request_mutation offered=no forced=no",
                   AgentLoopCore.TurnEndLine("abc", new TurnResult { Outcome = TurnOutcome.Complete }));
    }

    // ---- cached tokens, from the captures ---------------------------------------------------------------

    [Fact]
    public void The_OpenAI_reader_splits_a_real_captures_prompt_into_cached_written_and_the_rest()
    {
      var r = OpenAIProvider.ReadStream(StreamFixtures.Reader(StreamFixtures.Read(StreamFixtures.OpenAIToolCall)), default, "OpenAI");

      // {"prompt_tokens":9210,"prompt_tokens_details":{"cached_tokens":8890,"cache_write_tokens":317}}
      Assert.Equal(8890, r.Usage.CacheReadInputTokens);
      Assert.Equal(317, r.Usage.CacheCreationInputTokens);
      Assert.Equal(3, r.Usage.InputTokens);
      Assert.Equal(9210, r.Usage.TotalInputTokens);
      Assert.True(r.Usage.CacheHit);
    }

    [Fact]
    public void The_reader_takes_Ollamas_cached_tokens_and_keeps_a_reply_with_no_details_whole()
    {
      var ollama = OpenAIProvider.ReadStream(StreamFixtures.Reader(StreamFixtures.Read(StreamFixtures.OllamaToolCall)), default, "Ollama Cloud");
      Assert.Equal(10922, ollama.Usage.TotalInputTokens);
      Assert.True(ollama.Usage.CacheReadInputTokens > 0);
      Assert.Equal(0, ollama.Usage.CacheCreationInputTokens);

      var plain = OpenAIProvider.ReadStream(StreamFixtures.Reader(StreamFixtures.Read(StreamFixtures.OpenAIParallelCallsHandMade)), default, "OpenAI");
      Assert.Equal(900, plain.Usage.InputTokens);
      Assert.Equal(0, plain.Usage.CacheReadInputTokens);
    }

    // ---- the panel, read from its source because it is GH-bound --------------------------------------------

    [Fact]
    public void The_panel_forces_only_the_follow_up_and_decides_on_the_strict_catch()
    {
      var dir = new DirectoryInfo(AppContext.BaseDirectory);
      while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Dwight.sln"))) dir = dir.Parent;
      Assert.True(dir != null, "the repository root (Dwight.sln) could not be found from " + AppContext.BaseDirectory);
      var source = File.ReadAllText(Path.Combine(dir.FullName, "Dwight", "UI", "DwightScaffold.cs"));

      Assert.Contains("ForceToolName = isAnnouncementFollowUp ? AgentHostTools.RequestMutation : null", source);
      Assert.Contains("AgentLoopCore.AnnouncedForForcing(capability, result), isAnnouncementFollowUp)", source);
      Assert.Contains("_forcedTries.TryTake(threadId)", source);
      Assert.Contains("_forcedTries.Reset(threadId);", source);
      // The model's prose stays off the screen and out of the transcript on a follow-up with no card.
      Assert.Contains("if (!string.IsNullOrWhiteSpace(result.Text) && !followUpNoCard)", source);
      Assert.Contains("AutoFollowUp.NoCardSentence(note, result.RefusedRequests)", source);
      // A forced card takes the door every card takes: the host tool, which reads the mode, and has no idea it was forced.
      Assert.DoesNotContain("forced", source.Substring(source.IndexOf("private static string HostTool(", StringComparison.Ordinal), 2000), StringComparison.OrdinalIgnoreCase);
      // The Anthropic adapter holds an HTTP client this project does not link, so its one line is read, as the panel's are.
      // Brief BZ: it reads the row's fact now, and a row that says nothing is the true every earlier row was measured on.
      Assert.Contains("public bool SendsForcedTool => Row?.ForcedToolChoice ?? true;", File.ReadAllText(Path.Combine(dir.FullName, "Dwight", "Llm", "AnthropicProvider.cs")));
    }
  }
}
