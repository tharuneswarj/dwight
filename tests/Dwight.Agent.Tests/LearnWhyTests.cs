using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dwight.Models;
using Dwight.Session;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// Brief EO: what Learn's later calls cost and where they are written. A judged answer and Play again are one ledger line each under Explain with the Learn label, which
  /// the totals count under their verb and the "Continue?" estimate never reads; the judge runs on the provider's Fast band, chosen from the catalogue and never by id; a Learn
  /// run's question before it counts the quiz's request too (the architect's ruling 3). And the GH-bound halves, pinned by source (ADR-091): every claim here is held on the
  /// SpendLedgerCore side, as the brief asks, or on the code that makes it true.
  /// </summary>
  public class LearnWhyTests
  {
    private static StoredSpend Spend(string model, int input, int output, double cents, long chars) => new StoredSpend
    {
      Model = model, Provider = "anthropic", InputTokens = input, OutputTokens = output, Cents = cents, Chars = chars, ElapsedMs = 1000,
    };

    private static string Line(StoredSpend s, string label = null, int minute = 0) =>
      SpendLedgerCore.Serialize(SpendLedgerCore.ForVerb(s, new DateTime(2026, 11, 3, 10, minute, 0, DateTimeKind.Utc), "doc", "file.gh", "Explain", "anthropic", null, label));

    // ---- the ledger: one line per judged answer, under Explain with the Learn label ------------------------------------------------------------------------------------

    [Fact]
    public void A_judged_answers_line_carries_the_learn_label_and_reads_back_with_it()
    {
      var text = Line(Spend("claude-haiku-5-5", 900, 60, 0.1, 1800), "learn");
      Assert.Contains("\"label\":\"learn\"", text);
      var back = SpendLedgerCore.Parse(text);
      Assert.Equal("learn", back.Label);
      Assert.Equal("Explain", back.Verb);
      Assert.Equal(LedgerLine.KindVerb, back.Kind);
      var plain = Line(Spend("claude-haiku-5-5", 900, 60, 0.1, 1800));
      Assert.DoesNotContain("\"label\"", plain);                       // an older reader and every other line see nothing new
      Assert.Null(SpendLedgerCore.Parse(plain).Label);
      Assert.Contains("internal const string LearnLabel = \"learn\";", Repo("Dwight", "LlmActions.Learn.cs"));
    }

    [Fact]
    public void The_totals_count_a_labelled_line_under_its_verb()
    {
      var lines = new[] { Line(Spend("claude-sonnet-5-5", 9000, 1500, 3.0, 18000)), Line(Spend("claude-haiku-5-5", 900, 60, 0.1, 1800), "learn", 1) };
      var sum = SpendLedgerCore.Summarise(lines, new DateTime(2026, 11, 3, 16, 0, 0, DateTimeKind.Local));
      var all = sum.ByVerb[3]["Explain"];
      Assert.Equal(2, all.Runs);
      Assert.Equal(3.1, all.Cents, 6);
      Assert.Single(sum.ByVerb[3]);                                   // no group of its own: a judged answer is part of Explain's spend
    }

    /// <summary>A judged answer is a tenth of a read's size and answers in a sentence: were the sample to read it, the "Continue?" estimate for the verb would fall to it.</summary>
    [Fact]
    public void The_estimate_before_a_run_never_reads_a_labelled_line()
    {
      var verbLines = Enumerable.Range(0, 3).Select(i => Line(Spend("claude-haiku-5-5", 9000, 1500, 3.0, 18000), null, i));
      var judged = Enumerable.Range(0, 10).Select(i => Line(Spend("claude-haiku-5-5", 900, 60, 0.1, 9000), "learn", 10 + i));
      var sample = SpendLedgerCore.Sample(verbLines.Concat(judged));
      Assert.Equal(1500, sample.OutputTokens("Explain", "claude-haiku-5-5"));
      Assert.Equal(2.0, sample.CharsPerToken("anthropic", "claude-haiku-5-5").Value, 6);
      var onlyJudged = SpendLedgerCore.Sample(judged);
      Assert.Null(onlyJudged.OutputTokens("Explain", "claude-haiku-5-5"));   // ten judged answers are no sample at all
    }

    // ---- the judge's model: the Fast band, from the catalogue ------------------------------------------------------------------------------------------------------------

    private static ModelInfo M(string id, string tier, double inPer, double outPer, bool structured = true, string reconcile = "green", bool local = false) =>
      new ModelInfo(id, id, local ? "ollama" : "anthropic", tier, inPer, outPer, 200_000, structured: structured) { Reconcile = reconcile };

    [Fact]
    public void The_judge_runs_on_the_cheapest_fast_model_the_key_can_call()
    {
      var rows = new[] { M("top", "Top", 15, 75), M("mid", "Mid", 3, 15), M("fast-dear", "Fast", 1, 5), M("fast-cheap", "Fast", 0.8, 4), M("fast-red", "Fast", 0.1, 0.5, reconcile: "red") };
      Assert.Equal("fast-cheap", ModelResolver.Judge(rows).Id);
      Assert.Equal("fast-dear", ModelResolver.Judge(new[] { M("fast-dear", "Fast", 1, 5), M("fast-plain", "Fast", 0.1, 0.5, structured: false) }).Id);   // structured output first
      Assert.Equal("fast-plain", ModelResolver.Judge(new[] { M("fast-plain", "Fast", 0.1, 0.5, structured: false) }).Id);
    }

    [Fact]
    public void A_provider_with_no_fast_model_judges_nothing()
    {
      Assert.Null(ModelResolver.Judge(new[] { M("mid", "Mid", 3, 15), M("top", "Top", 15, 75) }));
      Assert.Null(ModelResolver.Judge(new[] { M("fast-amber", "Fast", 1, 5, reconcile: "amber") }));
      Assert.Null(ModelResolver.Judge(Array.Empty<ModelInfo>()));
      Assert.Null(ModelResolver.Judge((IReadOnlyList<ModelInfo>)null));
    }

    /// <summary>Read from the shipped file, never this machine's copy (ModelCatalogue.ForProvider reads %APPDATA%, which CI does not have). Anthropic's judge is Claude
    /// Haiku 5.5, the cheapest fast row, which is the model the report's price for one judged answer is on.</summary>
    [Fact]
    public void On_the_shipped_catalogue_each_provider_has_a_fast_judge()
    {
      var rows = ModelCatalogue.Parse(Repo("Dwight", "Resources", "models.json"));
      foreach (var provider in new[] { Dwight.DwightConfig.ProviderAnthropic, Dwight.DwightConfig.ProviderOpenAI, Dwight.DwightConfig.ProviderGoogle, Dwight.DwightConfig.ProviderOllamaCloud, Dwight.DwightConfig.ProviderOllamaLocal })
      {
        var judge = ModelResolver.Judge(rows.Where(m => m.Provider == provider).ToList());
        Assert.NotNull(judge);
        Assert.Equal("Fast", judge.Tier);
      }
      Assert.Equal("claude-haiku-5-5", ModelResolver.Judge(rows.Where(m => m.Provider == Dwight.DwightConfig.ProviderAnthropic).ToList()).Id);
    }

    // ---- the estimates -------------------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_learn_runs_question_counts_both_answers_and_a_judged_answer_is_a_sentence()
    {
      Assert.Equal(LargeRun.DefaultOutputTokens(Dwight.DwightVerbs.Explain) + LargeRun.DefaultOutputTokens(LargeRun.Quiz), LargeRun.DefaultOutputTokens(LargeRun.Learn));
      Assert.InRange(LargeRun.DefaultOutputTokens(LargeRun.Judge), 1, 200);
      var model = M("sonnet", "Mid", 3, 15);
      Assert.True(LargeRun.Estimate(model, 20000, LargeRun.Learn, null) > LargeRun.Estimate(model, 20000, Dwight.DwightVerbs.Explain, null));
    }

    // ---- the GH-bound halves, pinned by source (ADR-091) -----------------------------------------------------------------------------------------------------------------

    private static string Repo(params string[] parts)
    {
      var dir = new DirectoryInfo(AppContext.BaseDirectory);
      while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Dwight.sln"))) dir = dir.Parent;
      Assert.True(dir != null, "the repository root (Dwight.sln) could not be found");
      return File.ReadAllText(Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray())).Replace("\r\n", "\n");
    }

    [Fact]
    public void A_learn_runs_question_before_it_counts_the_quiz_request()
    {
      var actions = Repo("Dwight", "LlmActions.cs");
      Assert.Contains("var learnChars = TeachingGate.Active(learn, mode == ExplainModes.Walk) ? TeachingCall.RequestChars(user.Length) : 0;", actions);
      Assert.Contains("}, null, learnChars);", actions);
      Assert.Contains("AskSentence(components, system.Length + user.Length + Math.Max(0, moreChars), model, verb, ctx?.Doc);", actions);
    }

    [Fact]
    public void Each_later_call_is_one_line_under_Explain_with_the_learn_label_and_a_stop_leaves_an_estimate()
    {
      var learn = Repo("Dwight", "LlmActions.Learn.cs");
      Assert.Equal(2, learn.Split(new[] { "SpendLedger.RecordLabelled(doc, DwightVerbs.Explain, spend, LearnLabel);" }, StringSplitOptions.None).Length - 1);   // the judge and Play again
      Assert.Equal(2, learn.Split(new[] { "StoppedEstimate.Of(model, chars, sw.Elapsed, SpendLedger.ReadSample())" }, StringSplitOptions.None).Length - 1);
      Assert.Contains("var model = ModelResolver.Judge(DwightSettings.Current.EffectiveProvider);", learn);   // the Fast band, never an id
      Assert.DoesNotContain("claude-", learn);
      var ledger = Repo("Dwight", "Session", "SpendLedger.cs");
      Assert.Contains("if (!SpendLedgerCore.HasUsage(spend)) return;", ledger);
    }

    [Fact]
    public void A_judge_is_stopped_by_leaving_its_card_the_walk_ending_a_new_run_and_its_stop()
    {
      var session = Repo("Dwight", "UI", "WalkSession.cs");
      Assert.Contains("LlmActions.StopDeckCall(_state.Index);   // Brief EO: leaving a card stops the judge reading its answer", session);
      Assert.Contains("LlmActions.StopDeckCall();   // Brief EO: a judge still reading an answer is stopped when the Walk ends", session);
      Assert.Contains("LlmActions.StopDeckCall();   // Brief EO: a new run stops a deck's call in flight", session);
      Assert.Contains("if (outcome == DeckOutcome.Judging) LlmActions.JudgeAnswer(_panel, _state.Index);", session);
      var panel = Repo("Dwight", "UI", "WebPanel.cs");
      Assert.Contains("case \"stop\": LlmActions.StopDeckCall(); break;", panel);
      var learn = Repo("Dwight", "LlmActions.Learn.cs");
      Assert.Contains("if (cts == null || (keepCard >= 0 && keepCard == _deckCallCard)) return;", learn);
      Assert.Contains("deck.Judged(card, r.Verdict, r.Sentence, CostLine(model, spend));", learn);   // a stop is no verdict: the card could not judge
    }

    /// <summary>Brief EO unit 5 and #322: a name on a card pans and lights; it never selects, so the person's next action still operates on their own selection.</summary>
    [Fact]
    public void A_name_on_a_card_pans_and_lights_and_never_selects()
    {
      var session = Repo("Dwight", "UI", "WalkSession.cs");
      var look = session.Substring(session.IndexOf("public static void DeckLook(WebPanel panel, int id)", StringComparison.Ordinal));
      look = look.Substring(0, look.IndexOf("\n    }\n", StringComparison.Ordinal));
      Assert.Contains("CanvasNav.GoTo(one, NavReason.UserClicked);", look);
      Assert.Contains("if (!Owns(panel)) CanvasGlow.ShowMarks(one, null, panel);", look);
      Assert.DoesNotContain("Select(", look);
      Assert.DoesNotContain("Selected", look);
      Assert.Contains("case \"look\": if (int.TryParse(arg, out var look)) WalkSession.DeckLook(this, look); break;", Repo("Dwight", "UI", "WebPanel.cs"));
    }

    /// <summary>Brief EO unit 6: Focus in Learn maximises Grasshopper and opens the viewport, and keeps the panel as the card: no bar. The Walk's Focus is untouched.</summary>
    [Fact]
    public void Focus_in_learn_keeps_the_panel_and_opens_no_bar()
    {
      var session = Repo("Dwight", "UI", "WalkSession.cs");
      Assert.Contains("if (_focus.Active && !IsDeck) return WalkBar.Get()?.Present(ViewNow()) ?? WalkSurface.Panel;", session);
      Assert.Contains("var arranged = WalkFocus.Engage(IsDeck, out var say);", session);
      Assert.Contains("if (!IsDeck) { try { WalkBar.Get()?.Sync(); } catch { } }", session);
      var focus = Repo("Dwight", "UI", "WalkFocus.cs");
      Assert.Contains("if (!keepPanel) DwightDock.Host()?.Collapse();", focus);
      Assert.Contains("if (!keepPanel) DwightDock.Host()?.FollowCanvas();", focus);
    }

    [Fact]
    public void Play_again_says_its_estimate_first_and_asks_above_the_threshold_before_anything_is_sent()
    {
      var learn = Repo("Dwight", "LlmActions.Learn.cs");
      var again = learn.Substring(learn.IndexOf("internal static void PlayAgain(", StringComparison.Ordinal));
      var asks = again.IndexOf("if (sentence != null) { panel.ShowDeck(DeckViews.Confirm(sentence)); return; }", StringComparison.Ordinal);
      var sends = again.IndexOf("TeachingCall.Ask(NewProvider(model), ask, DwightConfig.MaxTokens, token)", StringComparison.Ordinal);
      Assert.True(asks > 0 && sends > asks, "Play again asks before it sends");
      Assert.Contains("LargeRun.Ask(graph.Selection.Count, chars, model, threshold, LargeRun.Quiz,", again);
      Assert.Contains("Deck.AgainEstimate = LlmActions.AgainEstimate(this);", Repo("Dwight", "UI", "WebPanel.cs"));
      Assert.Contains("case \"againgo\": LlmActions.PlayAgain(this, true); break;", Repo("Dwight", "UI", "WebPanel.cs"));
    }
  }
}
