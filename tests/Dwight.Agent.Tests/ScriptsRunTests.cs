using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Dwight.Agent;
using Dwight.Llm;
using Dwight.Models;
using Dwight.Session;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// Brief CD (Tharun's ruling, #1189): with "Do not ask" and Full control both on, a script runs without asking. The table's cell is pinned in
  /// <see cref="ApprovalModeTests"/>; what is pinned here is what surrounds it: the receipt keeps the whole source, a person is told once in either
  /// order, the words agree with the table, and the report reaches the model so the turn carries on (#156).
  /// </summary>
  [Collection(TurnShapeTests.StaticRecord)]
  public class ScriptsRunTests
  {
    private const string N = ApprovalMode.Never;
    private const string A = ApprovalMode.Ask;
    private const string S = ApprovalMode.ScriptsAndDeletions;

    private static string RepoFile(params string[] parts)
    {
      var dir = new DirectoryInfo(AppContext.BaseDirectory);
      while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Dwight.sln"))) dir = dir.Parent;
      Assert.True(dir != null);
      return File.ReadAllText(Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray())).Replace("\r\n", "\n");
    }

    private static string Panel() => RepoFile("Dwight", "UI", "DwightScaffold.cs");

    // ---- the receipt ---------------------------------------------------------------------------------------------------------------------

    private static readonly string LongSource = string.Join("\n", Enumerable.Range(0, 3000).Select(i => "print('<line " + i + ">')"));

    [Fact]
    public void The_receipt_is_marked_folded_has_no_button_and_holds_the_whole_source()
    {
      var html = ConsentCard.RunScriptHtml("Bake the curves", "tok", LongSource, false, N);

      Assert.Contains(ApprovalMode.AutoStateLine(N), html);
      Assert.DoesNotContain("dwightSend('consent:", html);
      Assert.Contains(System.Net.WebUtility.HtmlEncode(LongSource), html);   // THE TEXT SHOWN IS THE TEXT RUN, never cut
      Assert.Contains("<details class=\"consent-script\">", html);            // folded behind its plugin-written summary
      Assert.DoesNotContain("<details class=\"consent-script\" open", html);
    }

    [Fact]
    public void The_card_that_waits_is_the_card_it_was()
    {
      var html = ConsentCard.RunScriptHtml("Bake the curves", "tok", "print(1)", true);

      Assert.Contains("dwightSend('consent:tok')", html);
      Assert.DoesNotContain(ApprovalMode.AutoStateLine(N), html);
      Assert.Contains("<details class=\"consent-script\" open", html);
    }

    // ---- a person knows what they have chosen ----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(false, N, true, N, true)]    // Do not ask first, then Full control
    [InlineData(true, A, true, N, true)]     // Full control first, then Do not ask, from either mode that asks
    [InlineData(true, S, true, N, true)]
    [InlineData(true, N, true, N, false)]    // already said
    [InlineData(true, N, false, N, false)]   // turning it off says nothing
    [InlineData(true, N, true, A, false)]
    [InlineData(false, A, true, A, false)]   // one of the two is not both
    [InlineData(false, S, true, S, false)]
    [InlineData(true, A, true, S, false)]
    [InlineData(false, A, false, N, false)]
    public void The_line_is_said_on_the_step_into_both_in_either_order_and_at_no_other_time(bool wasFull, string wasMode, bool isFull, string isMode, bool said)
    {
      Assert.Equal(said, ChatHeader.ScriptsRunNewly(wasFull, wasMode, isFull, isMode));
    }

    [Fact]
    public void The_line_says_what_the_ruling_asked_and_a_chat_opened_with_both_says_it_under_the_greeting()
    {
      Assert.Equal("Scripts run without asking in this chat, and one Ctrl-Z may not undo everything they touch.", ChatHeader.ScriptsRunLine);

      Assert.Contains(ChatHeader.ScriptsRunLine, ChatHeader.OpeningScriptsHtml(N, true));
      Assert.Equal("", ChatHeader.OpeningScriptsHtml(N, false));
      Assert.Equal("", ChatHeader.OpeningScriptsHtml(A, true));
      Assert.Equal("", ChatHeader.OpeningScriptsHtml(S, true));
    }

    /// <summary>The panel's three moments, pinned by their text because it links Grasshopper: the chat's opening and each of the two header switches.</summary>
    [Fact]
    public void The_panel_says_it_at_the_opening_and_from_both_switches()
    {
      var cs = Panel();
      Assert.Contains("GreetingHtml() + ChatHeader.OpeningScriptsHtml(ApprovalMode.Normalise(DwightSettings.Current.ApprovalMode), DwightSettings.Current.FullControl)", cs);
      Assert.Contains("if (ChatHeader.ScriptsRunNewly(full, wasMode, full, mode)) AppendPluginHtml(wp, doc, threadId, System.Net.WebUtility.HtmlEncode(ChatHeader.ScriptsRunLine));", cs);
      Assert.Contains("if (ChatHeader.ScriptsRunNewly(was, modeNow, on, modeNow)) AppendPluginHtml(wp, doc, threadId, System.Net.WebUtility.HtmlEncode(ChatHeader.ScriptsRunLine));", cs);
    }

    /// <summary>The sentences a person reads before choosing, in Settings, and each is true whichever switch they turn first.</summary>
    [Fact]
    public void Each_settings_sentence_names_the_other_switch_so_it_is_true_in_both_orders()
    {
      Assert.Contains("With the Do not ask approval mode, a script runs without asking", FullControl.SettingSentence);
      Assert.Contains("With Full control on, a script runs without asking too", ApprovalMode.Description(N));
      Assert.DoesNotContain("without asking", ApprovalMode.Description(A));
      Assert.DoesNotContain("without asking", ApprovalMode.Description(S));

      var settings = RepoFile("Dwight", "Resources", "settings.html");
      Assert.Contains(FullControl.SettingSentence, settings);
      Assert.Contains(ApprovalMode.Description(N), settings);
    }

    // ---- what the model is told (#315: a prompt claim carries a test on the behaviour's side) ------------------------------------------------

    [Theory]
    [InlineData(N, true)]
    [InlineData(A, false)]
    [InlineData(S, false)]
    [InlineData(null, false)]
    public void The_thread_fact_says_what_the_table_decides(string mode, bool runsWithoutAsking)
    {
      var decision = ApprovalMode.Decide(mode, FullControl.Kind, ApprovalMode.FactsFor(FullControl.Kind, 1, 0, true));
      Assert.Equal(runsWithoutAsking, decision == ApprovalDecision.Auto);
      Assert.Equal(runsWithoutAsking, FullControl.RunsWithoutAsking(mode));

      var fact = FullControl.ThreadFact(mode);
      Assert.Equal(runsWithoutAsking, fact.Contains("which runs without asking when your turn ends"));
      Assert.Equal(!runsWithoutAsking, fact.Contains("which the user reads whole on a card before it runs"));
      Assert.Contains(FullControl.EnvironmentSentence, fact);
      Assert.Contains("will not call the result checked or verified", fact);
    }

    [Fact]
    public void Every_description_of_the_card_is_true_under_both_and_full_control_off_stays_no_door()
    {
      Assert.DoesNotContain("after the user approves", FullControl.ToolDescription);
      Assert.Contains("on the user's approval, or at once in a chat whose approval mode is Do not ask", FullControl.KindCan);
      Assert.False(FullControl.RunsWithoutAsking(N, false));

      var panel = Panel();
      Assert.Contains("Dwight.Agent.FullControl.ThreadFact(CommandHistory.ThreadApprovalMode(doc, threadId))", panel);
      Assert.Contains("if (fullControl) situation", panel);
    }

    // ---- the door --------------------------------------------------------------------------------------------------------------------------

    /// <summary>The script door, in the order that keeps the rest true: the same script is caught first, the waiting card is drawn, and only then the
    /// decision adds the automatic plan every automatic card takes, whose click runs it and whose receipt is folded.</summary>
    [Fact]
    public void The_door_takes_the_path_every_automatic_card_takes()
    {
      var panel = Panel();
      var same = panel.IndexOf("FullControl.SameScript(_consents.WaitingSeed(threadId", StringComparison.Ordinal);
      var mint = panel.IndexOf("var runToken = MintConsent(", StringComparison.Ordinal);
      var draw = panel.IndexOf("pendingCards.Add(ConsentCard.RunScriptHtml(runWhy", StringComparison.Ordinal);
      var decide = panel.IndexOf("ApprovalMode.Decide(approvalMode, Dwight.Agent.FullControl.Kind, ApprovalMode.FactsFor(Dwight.Agent.FullControl.Kind, 1, 0, fullControl))", StringComparison.Ordinal);
      var auto = panel.IndexOf("AutoHtml = ConsentCard.RunScriptHtml(runWhy, runToken, runGrant.Source, false, ApprovalMode.Normalise(approvalMode))", StringComparison.Ordinal);
      Assert.True(0 < same && same < mint && mint < draw && draw < decide && decide < auto, "same script, mint, waiting card, decision, receipt");

      Assert.Contains("RunApprovedScript(wp, doc, threadId, grant, autoMode != null);", panel);
    }

    /// <summary>A token an automatic approval took is no longer pending, so the turn after it is told of no waiting card. The fact is for the modes that still ask,
    /// and for a stopped turn, whose card really does wait.</summary>
    [Fact]
    public void After_an_automatic_run_nothing_waits_and_a_card_that_waits_still_says_so()
    {
      var store = new ConsentTokens();
      var seed = new HandoffSeed { Source = "print(1)", Why = "w" };
      var token = store.Mint("t1", FullControl.Kind, seed);
      Assert.NotNull(store.WaitingSeed("t1", FullControl.Kind));   // a card that waits

      Assert.True(store.Take(token, "t1", out _, out _, out _));
      Assert.Null(store.WaitingSeed("t1", FullControl.Kind));      // the same door the automatic approval goes through
    }

    // ---- the follow-up ----------------------------------------------------------------------------------------------------------------------

    private sealed class Counting : ILlmProvider
    {
      private readonly Queue<LlmResponse> _replies;
      public Counting(params LlmResponse[] replies) { _replies = new Queue<LlmResponse>(replies); }
      public string ModelId => "counting";
      public ProviderCapability Capability => default(ProviderCapability);
      public readonly List<LlmRequest> Seen = new List<LlmRequest>();
      public LlmResponse Send(LlmRequest request, CancellationToken cancel = default)
      {
        Seen.Add(request);
        return _replies.Dequeue();
      }
    }

    private static TurnResult Drive(ILlmProvider provider, List<LlmMessage> messages) =>
      AgentTurnDriver.Drive(new TurnDrive
      {
        Provider = provider,
        System = "system",
        Messages = messages,
        Limits = new TurnLimits { MaxSteps = 8, WallClockMs = 0, MaxTranscriptChars = 1_000_000, AnswerBudget = 1000 },
        Execute = c => "{}",
      }, System.Diagnostics.Stopwatch.StartNew());

    private static StoredMessage Me(string text) => new StoredMessage { Who = "me", Src = text, Html = text };
    private static StoredMessage Dw(string text) => new StoredMessage { Who = "dw", Src = text, Html = text };
    private static StoredMessage Chrome(string text) => new StoredMessage { Who = "dw", Html = text };

    /// <summary>What the thread holds when the follow-up runs, in the order the panel writes it, and through the real transcript and the real driver. It ends on the report,
    /// so #156's refusal is not met and the turn carries on.</summary>
    [Fact]
    public void An_automatic_script_reports_to_the_model_through_the_report_door_and_the_turn_carries_on()
    {
      var ran = new RunOutcome { Before = new DocSnapshot(), After = new DocSnapshot() };
      var line = RunScriptApproval.PanelLine("Bake the curves", ran);
      var report = RunScriptApproval.TranscriptLine(ran, line, true);
      var stored = new List<StoredMessage>
      {
        Me("Bake the curves to a new layer."),
        Dw("I will run a script for that."),
        Chrome(ConsentCard.RunScriptHtml("Bake the curves", "tok", "print(1)", false, N)),
        Me(ApprovalMode.AutoUserText),
        Chrome(RunScriptApproval.AutoRunLine),
        new StoredMessage { Who = ChatToTranscript.Report, Src = report, Html = line },
      };
      var provider = new Counting(AgentTurnDriverTests.Says("It ran; nothing moved."));

      var result = Drive(provider, ChatToTranscript.Build(stored));

      Assert.Equal(TurnOutcome.Complete, result.Outcome);
      var last = provider.Seen.Single().Messages.Last();
      Assert.Equal(LlmRole.User, last.Role);
      Assert.Contains(last.Blocks, b => b.Text != null && b.Text.Contains("[Run by Dwight automatically, under this chat's Do not ask mode]"));
      Assert.Contains(last.Blocks, b => b.Text != null && b.Text.Contains("do not call it verified"));
    }

    /// <summary>Nobody clicked, so nothing says they did; the model is not handed a sentence it would repeat to the person as "you approved".</summary>
    [Fact]
    public void Nothing_says_the_person_approved_a_script_they_did_not_click()
    {
      var ran = new RunOutcome { Before = new DocSnapshot(), After = new DocSnapshot() };
      var unavailable = new RunOutcome { Unavailable = "No runtime." };

      Assert.DoesNotContain("you approved", RunScriptApproval.AutoRunLine);
      Assert.DoesNotContain("user's approval", RunScriptApproval.TranscriptLine(ran, "x", true));
      Assert.DoesNotContain("the user approved", RunScriptApproval.TranscriptLine(unavailable, "x", true));
      Assert.DoesNotContain("user's approval", RunScriptApproval.OverranTranscript("x", 60, true));

      Assert.StartsWith("[Run by Dwight on the user's approval] ", RunScriptApproval.TranscriptLine(ran, "x"));   // the click's words are unchanged
      Assert.True(RunScriptApproval.FollowUp(ran, false));
      Assert.False(RunScriptApproval.FollowUp(unavailable, false));
    }

    // ---- the record -------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_public_pages_say_it_and_no_longer_say_a_script_always_asks()
    {
      var build = RepoFile("docs", "public", "build.md");
      Assert.Contains(FullControl.SettingSentence, build);
      Assert.Contains(ChatHeader.ScriptsRunLine, build);
      Assert.DoesNotContain("always asks, whatever the approval mode", build);
      Assert.DoesNotContain("a script Dwight cannot check always asks", build);

      Assert.Contains("under the Do not ask approval mode it runs without asking", RepoFile("docs", "public", "known-limitations.md"));
      Assert.Contains("under the Do not ask approval mode, afterwards", RepoFile("docs", "public", "privacy.md"));
      Assert.Contains("31 October 2026: with Full control and Do not ask both on, a script runs without asking.", RepoFile("docs", "public", "changelog.md"));
    }
  }
}
