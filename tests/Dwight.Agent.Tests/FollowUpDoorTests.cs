using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Dwight.Agent;
using Dwight.Llm;
using Dwight.Session;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// Release gate, 2 of 2 Sonnet rows with a revert under "Do not ask": "This model does not support assistant message prefill." The follow-up after an
  /// automatically approved build drew its line as chrome, so the transcript ended on the model's closing text. Every door into a turn with no typed
  /// message is walked here: what it leaves in the stored thread, through the real <see cref="ChatToTranscript"/> and the real driver. The doors themselves
  /// are in the GH-bound panel, so what each writes is pinned by reading its source, and a new door fails the count.
  /// </summary>
  [Collection(TurnShapeTests.StaticRecord)]
  public class FollowUpDoorTests
  {
    private sealed class Counting : ILlmProvider
    {
      private readonly Queue<LlmResponse> _replies;
      public Counting(params LlmResponse[] replies) { _replies = new Queue<LlmResponse>(replies); }
      public string ModelId => "counting";
      public ProviderCapability Capability => default(ProviderCapability);
      public int Sends;
      public LlmRole LastRoleSent;
      public LlmResponse Send(LlmRequest request, CancellationToken cancel = default)
      {
        Sends++;
        LastRoleSent = request.Messages[request.Messages.Count - 1].Role;
        return _replies.Dequeue();
      }
    }

    private static StoredMessage Me(string text) => new StoredMessage { Who = "me", Src = text, Html = text };
    private static StoredMessage Dw(string text) => new StoredMessage { Who = "dw", Src = text, Html = text };
    private static StoredMessage Chrome(string text) => new StoredMessage { Who = "dw", Html = text };

    private static TurnResult Drive(ILlmProvider provider, List<LlmMessage> messages, bool webSearch = false) =>
      AgentTurnDriver.Drive(new TurnDrive
      {
        Provider = provider,
        System = "system",
        Messages = messages,
        Limits = new TurnLimits { MaxSteps = 8, WallClockMs = 0, MaxTranscriptChars = 1_000_000, AnswerBudget = 1000 },
        Execute = c => "{}",
        ServerWebSearch = webSearch,
      }, System.Diagnostics.Stopwatch.StartNew());

    /// <summary>A finished build turn, as every door finds the thread: the user's message, the approval's echo, the model's closing text, the plugin's close line.</summary>
    private static List<StoredMessage> AfterAClosingMessage() => new List<StoredMessage>
    {
      Me("Build the lamp."),
      Me(ApprovalMode.AutoUserText),
      Dw("B1 reverted because the script failed, so I will look at it."),
      Chrome("<p>Closed.</p>"),
    };

    /// <summary>Each door into a turn with no typed message, and what it leaves last.</summary>
    public static IEnumerable<object[]> Doors() => new[]
    {
      new object[] { "U94: an automatic build that reverted", ChatToTranscript.Report, AutoFollowUp.BuildEndedFact(new Dictionary<string, RegionOutcome> { ["B1"] = RegionOutcome.Reverted }) },
      new object[] { "U97: an announcement with no card", ChatToTranscript.Report, AutoFollowUp.AnnouncementFact },
      new object[] { "A click or automatic approval: solve, scaffold, build", "me", ApprovalMode.AutoUserText },
      new object[] { "An edit, applied", ChatToTranscript.Report, "Applied." },
      new object[] { "A value, applied", ChatToTranscript.Report, "Applied." },
      new object[] { "A value refused under an automatic mode", ChatToTranscript.Report, "Not applied." },
      new object[] { "A script edit not landed (U105)", ChatToTranscript.Report, "Not applied." },
      new object[] { "A spot placed", ChatToTranscript.Report, "Placed Panel in Spot 5." },
      new object[] { "A spot not landed (U105)", ChatToTranscript.Report, "Reverted." },
      new object[] { "The spot guard's own report (U105)", ChatToTranscript.Report, "[Dwight - the build did not land] It stopped." },
      new object[] { "A run_script that ran", ChatToTranscript.Report, "The script ran." },
      new object[] { "Explain a region", "me", "Explain what region A1 does." },
    };

    [Theory]
    [MemberData(nameof(Doors))]
    public void Every_door_leaves_the_model_something_to_answer_and_the_turn_is_sent(string door, string who, string src)
    {
      var stored = AfterAClosingMessage();
      stored.Add(new StoredMessage { Who = who, Src = src, Html = src });
      var provider = new Counting(AgentTurnDriverTests.Says("Understood."));

      var result = Drive(provider, ChatToTranscript.Build(stored));

      Assert.True(result.Outcome == TurnOutcome.Complete, door);
      Assert.Equal(1, provider.Sends);
      Assert.Equal(LlmRole.User, provider.LastRoleSent);
    }

    /// <summary>The defect itself, through the real path: the old door appended chrome, which the transcript excludes.</summary>
    [Fact]
    public void A_door_that_adds_only_chrome_is_refused_in_our_words_before_any_provider_is_called()
    {
      var stored = AfterAClosingMessage();
      stored.Add(Chrome(AutoFollowUp.Line));
      var provider = new Counting();

      var result = Drive(provider, ChatToTranscript.Build(stored));

      Assert.Equal(TurnOutcome.Failed, result.Outcome);
      Assert.Equal(0, provider.Sends);
      Assert.Equal("Dwight could not assemble this turn: the conversation ended on the model's own message, with nothing new for it to answer.", result.Error);
      Assert.DoesNotContain("400", result.Error);
    }

    [Fact]
    public void It_is_refused_not_repaired_the_list_is_left_as_it_was()
    {
      var messages = new List<LlmMessage> { LlmMessage.User("Hello."), LlmMessage.Assistant(new List<LlmBlock> { LlmBlock.TextBlock("Hi.") }) };

      Drive(new Counting(), messages);

      Assert.Equal(2, messages.Count);
      Assert.Equal(LlmRole.Assistant, messages[1].Role);
    }

    [Fact]
    public void The_rule_reads_only_the_last_message_and_an_empty_list_keeps_its_own_refusal()
    {
      var assistant = LlmMessage.Assistant(new List<LlmBlock> { LlmBlock.TextBlock("Hi.") });
      Assert.Null(AgentLoopCore.CheckEnding(new List<LlmMessage> { assistant, LlmMessage.User("And?") }, false));
      Assert.NotNull(AgentLoopCore.CheckEnding(new List<LlmMessage> { LlmMessage.User("And?"), assistant }, false));
      Assert.Null(AgentLoopCore.CheckEnding(new List<LlmMessage>(), false));
      Assert.Contains("empty", Drive(new Counting(), new List<LlmMessage>()).Error);
    }

    /// <summary>Anthropic's own loop: a paused reply goes back unchanged, so a list ending on the assistant is correct there and only there.</summary>
    [Fact]
    public void A_paused_reply_is_the_one_legal_ending_on_the_assistant()
    {
      var paused = new LlmResponse { StopReason = "pause_turn" };
      paused.Blocks.Add(LlmBlock.TextBlock("Searching."));
      var provider = new Counting(paused, AgentTurnDriverTests.Says("Found it."));

      var result = Drive(provider, new List<LlmMessage> { LlmMessage.User("Search for it.") }, webSearch: true);

      Assert.Equal(TurnOutcome.Complete, result.Outcome);
      Assert.Equal(2, provider.Sends);
      Assert.Equal(LlmRole.Assistant, provider.LastRoleSent);
      Assert.Equal("Found it.", result.Text);
    }

    // ---- the fact ---------------------------------------------------------------------------------------------------

    [Fact]
    public void The_fact_says_what_each_region_did_in_address_order()
    {
      var fact = AutoFollowUp.BuildEndedFact(new Dictionary<string, RegionOutcome>
      {
        ["C1"] = RegionOutcome.Refused,
        ["A1"] = RegionOutcome.Built,
        ["B1"] = RegionOutcome.Reverted,
        ["B2"] = RegionOutcome.RevertedWithResidue,
        ["A2"] = RegionOutcome.BuiltWithErrors,
      });

      Assert.Equal("PLUGIN FACT (from Dwight, not the user): the approved build did not finish: A1 was built; A2 was built, with errors; " +
                   "B1 reverted, so the canvas is as it was before it; B2 reverted, and something of it may remain on the canvas; " +
                   "C1 was refused before anything was touched. Nobody clicked this approval, so nobody is waiting to tell you what to do next.", fact);
      Assert.DoesNotContain("as it was", fact.Substring(fact.IndexOf("B2", StringComparison.Ordinal), 60));   // a revert with residue never claims it
    }

    // ---- the doors, in the panel's source ------------------------------------------------------------------------------

    private static string Panel()
    {
      var dir = new DirectoryInfo(AppContext.BaseDirectory);
      while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Dwight.sln"))) dir = dir.Parent;
      Assert.True(dir != null);
      return File.ReadAllText(Path.Combine(dir.FullName, "Dwight", "UI", "DwightScaffold.cs"));
    }

    /// <summary>A new door fails here, and the person adding it reads this: a turn with no typed message must first put something in the transcript.</summary>
    [Fact]
    public void Every_door_into_a_turn_is_counted()
    {
      Assert.Equal(15, Regex.Matches(Panel(), @"(?<!void )\bRunTurn\(wp, doc, threadId,").Count);
    }

    [Theory]
    [InlineData("AppendReport(wp, doc, threadId, System.Net.WebUtility.HtmlEncode(AutoFollowUp.Line), AutoFollowUp.BuildEndedFact(builtThisTurn, approvedRegions));\n                  RunTurn(wp, doc, threadId, TurnCapability.ReadOnly);")]
    [InlineData("AutoFollowUp.ContinueFact(notAttempted));\n                  var stillGranted = (approvedScripts ?? new ApprovedScript[0]).Where(s => s != null && notAttempted.Contains(s.Region)).ToList();\n                  RunTurn(wp, doc, threadId, TurnCapability.Build, notAttempted, stillGranted, autoApproved: true, isBuildContinuation: true);")]
    [InlineData("AppendReport(wp, doc, threadId, System.Net.WebUtility.HtmlEncode(AutoFollowUp.AnnouncementLine), AutoFollowUp.AnnouncementFact);\n                RunTurn(wp, doc, threadId, TurnCapability.ReadOnly, isAnnouncementFollowUp: true);")]
    [InlineData("AppendReport(wp, doc, threadId, ScriptEditReport.Html(ok, text), ScriptEditReport.Text(ok, text));\n              if (ok) RunTurn(wp, doc, threadId, SetValueCore.Continuation);")]
    [InlineData("AppendReport(wp, doc, threadId, ScriptEditReport.Html(ok, text), ScriptEditReport.Text(ok, text));\n              FollowUpIfAuto(wp, doc, threadId, autoMode != null, ok);")]
    [InlineData("AppendReport(wp, doc, threadId, SetValueReport.Html(result), SetValueReport.Text(result));\n            if (result.Ok) RunTurn(wp, doc, threadId, SetValueCore.Continuation);")]
    [InlineData("\"[Dwight - the build did not land] \" + line);\n        FollowUpIfAuto(wp, doc, threadId, autoApproved, landed: false);")]
    [InlineData("SpotAsk.FailedText(result));\n              FollowUpIfAuto(wp, doc, threadId, autoApproved, landed: false);")]
    [InlineData("\"<p>\" + System.Net.WebUtility.HtmlEncode(landed) + \"</p>\"), landed);\n              RunTurn(wp, doc, threadId, TurnCapability.ReadOnly);")]
    [InlineData("Dwight.Agent.RunScriptApproval.TranscriptLine(outcome, line, automatic));\n        if (Dwight.Agent.RunScriptApproval.FollowUp(outcome, false)) RunTurn(wp, doc, threadId, TurnCapability.ReadOnly);")]
    public void Each_door_writes_to_the_transcript_immediately_before_it_runs(string door)
    {
      var source = Panel().Replace("\r\n", "\n");
      Assert.True(source.Contains(door), door);
    }

    /// <summary>The click's three routes run on the echo written at the top of the click, which the model reads as a user line.</summary>
    [Fact]
    public void The_approval_routes_run_on_the_echo()
    {
      var source = Panel();
      var echo = source.IndexOf("AppendApprovalEcho(wp, doc, threadId, autoMode != null", StringComparison.Ordinal);
      Assert.True(echo > 0);
      foreach (var route in new[] { "RunTurn(wp, doc, threadId, TurnCapability.ReadAndSolve);", "RunTurn(wp, doc, threadId, TurnCapability.Scaffold);", "RunTurn(wp, doc, threadId, TurnCapability.Build, resolution.Regions" })
        Assert.True(source.IndexOf(route, StringComparison.Ordinal) > echo, route);
    }

    /// <summary>The line stays chrome only where a report has just been written, in the two follow-ups that speak after one.</summary>
    [Fact]
    public void The_line_alone_is_only_ever_drawn_after_a_report()
    {
      var source = Panel().Replace("\r\n", "\n");
      foreach (Match m in Regex.Matches(source, @"AppendPluginHtml\(wp, doc, threadId, System\.Net\.WebUtility\.HtmlEncode\(AutoFollowUp\.Line\)\);"))
      {
        var before = source.Substring(Math.Max(0, m.Index - 400), Math.Min(400, m.Index));
        Assert.True(before.Contains("AutoFollowUp.ShouldRun(autoMode != null, AutoFollowUp.FlowEnd.Refused)") || before.Contains("if (!AutoFollowUp.AfterApplied(autoApproved, landed)) return;"), before);
      }
    }
  }
}
