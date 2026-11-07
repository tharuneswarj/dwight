using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Grasshopper;
using Dwight.Config;
using Dwight.Data;
using Dwight.Llm;
using Dwight.Models;
using Dwight.Session;
using Dwight.UI;

namespace Dwight
{
  /// <summary>
  /// Brief EO: the calls a Learn deck makes after its run, each on the person's click: a why-answer typed in their own words, judged by one small call on the provider's
  /// fast model, and Play again, a fresh deck of why-questions on the same selection. Each is one ledger line under Explain with the Learn label, never folded into the
  /// run's line, because the run is over. One such call at a time: leaving the card, the Walk ending, a new run and the card's Stop each cancel it.
  /// </summary>
  internal static partial class LlmActions
  {
    /// <summary>The label a Learn deck's later calls carry on the ledger (<see cref="LedgerLine.Label"/>).</summary>
    internal const string LearnLabel = "learn";

    private static CancellationTokenSource _deckCall;
    private static int _deckCallCard = -1;

    /// <summary>Stop the deck's call in flight, unless it is the judge of card <paramref name="keepCard"/> (the card still open). Its card then says it could not judge.</summary>
    internal static void StopDeckCall(int keepCard = -2)
    {
      var cts = _deckCall;
      if (cts == null || (keepCard >= 0 && keepCard == _deckCallCard)) return;
      _deckCall = null;
      _deckCallCard = -1;
      try { cts.Cancel(); } catch { }
    }

    private static CancellationToken BeginDeckCall(int card)
    {
      StopDeckCall();
      _deckCall = new CancellationTokenSource();
      _deckCallCard = card;
      return _deckCall.Token;
    }

    private static void EndDeckCall(CancellationToken token)
    {
      if (_deckCall != null && _deckCall.Token == token) { _deckCall = null; _deckCallCard = -1; }
    }

    /// <summary>The card's sentence when typed answers cannot be judged on the provider in force (it has no fast model); null when they can.</summary>
    internal static string JudgeOffLine()
    {
      try { return ModelResolver.Judge(DwightSettings.Current.EffectiveProvider) == null ? DeckText.NoJudge : null; }
      catch { return DeckText.NoJudge; }
    }

    /// <summary>
    /// Brief EO unit 2: judge card <paramref name="card"/>'s own words, off the UI thread, on the fast model. The verdict turns the card; a failure, a refusal or a Stop
    /// leaves it could-not-judge with its options. The line is written whatever happens, the provider's count when it gave one and an estimate when it was stopped first.
    /// </summary>
    internal static void JudgeAnswer(WebPanel panel, int card)
    {
      var deck = panel?.Deck;
      if (deck == null || !deck.IsJudging(card)) return;
      var model = ModelResolver.Judge(DwightSettings.Current.EffectiveProvider);
      if (model == null) { deck.JudgeFailed(card); WalkSession.DeckJudged(panel); return; }
      var c = deck.Cards[card];
      var typed = deck.TypedAt(card);
      var ids = deck.Labels.Keys.ToList();
      var doc = Instances.ActiveCanvas?.Document;
      var token = BeginDeckCall(card);
      var chars = Prompts.Prompts.JudgeSystem.Length + JudgeCall.Payload(c, typed).Length;
      Task.Run(() =>
      {
        var sw = Stopwatch.StartNew();
        JudgeCall.Result r;
        StoredSpend spend;
        try
        {
          r = JudgeCall.Ask(NewProvider(model), c, typed, ids, token);
          spend = SpendOf(model, r.Usage, sw.Elapsed, chars: chars);
        }
        catch (OperationCanceledException ex)
        {
          r = new JudgeCall.Result { Verdict = JudgeVerdict.None, Error = "stopped" };
          spend = UsageCarrier.TryTake(ex, out var paid) ? SpendOf(model, paid, sw.Elapsed, chars: chars) : StoppedEstimate.Of(model, chars, sw.Elapsed, SpendLedger.ReadSample());
        }
        catch (Exception ex)
        {
          r = new JudgeCall.Result { Verdict = JudgeVerdict.None, Error = ex.Message };
          spend = UsageCarrier.TryTake(ex, out var paid) ? SpendOf(model, paid, sw.Elapsed, chars: chars) : null;
        }
        Ui(() =>
        {
          EndDeckCall(token);
          try { SpendLedger.RecordLabelled(doc, DwightVerbs.Explain, spend, LearnLabel); } catch { }
          if (!ReferenceEquals(panel.Deck, deck)) return;   // a fresh deck replaced it
          deck.Judged(card, r.Verdict, r.Sentence, CostLine(model, spend));
          WalkSession.DeckJudged(panel);
        });
      });
    }

    /// <summary>What a judged answer or a fresh deck cost, as the card says it: cents where the catalogue prices the model, "free" for a local one, nothing otherwise.</summary>
    private static string CostLine(ModelInfo model, StoredSpend spend)
    {
      if (spend == null || model == null) return "";
      if (model.IsLocal) return "Free, on " + model.DisplayName + ".";
      if (spend.Cents == null) return "On " + model.DisplayName + ".";
      var cents = spend.Cents.Value;
      return (spend.Estimated != null ? "About " : "") + (cents < 0.1 ? "less than 0.1" : cents.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)) + " cents, on " + model.DisplayName + ".";
    }

    /// <summary>Play again's estimate for the deck's result card: the run's own quiz request on its own model, as the "Continue?" question would count it. Empty when there is none.</summary>
    internal static string AgainEstimate(WebPanel panel)
    {
      try
      {
        var model = panel?.LearnModel;
        if (model == null || panel.LearnChars <= 0 || !LargeRun.IsPriced(model)) return model != null && model.IsLocal ? "free" : "";
        return "about " + LargeRun.CostText(LargeRun.Estimate(model, panel.LearnChars, LargeRun.Quiz, SpendLedger.ReadSample()));
      }
      catch { return ""; }
    }

    /// <summary>
    /// Brief EO unit 4: Play again, a fresh deck of why-questions on the same selection, with the live solve read again. One call on the run's model, its estimate said
    /// first; above the Setting's threshold the card asks "Continue?" before anything is sent. One ledger line under Explain with the Learn label.
    /// </summary>
    internal static void PlayAgain(WebPanel panel, bool confirmed = false)
    {
      var deck = panel?.Deck;
      var model = panel?.LearnModel;
      if (deck == null || deck.Reopened || model == null) return;
      if (DwightSession.IsBusy) { DwightToast.Show("Dwight is already on a task"); return; }
      var doc = Instances.ActiveCanvas?.Document;
      var guids = panel.WalkSource?.ToList() ?? new List<Guid>();
      var graph = DwightGraph.Build(doc, guids);
      if (graph.Selection.Count < 2) { DwightToast.Show("The selection this deck was on is no longer on the canvas."); return; }
      var user = BuildGraphUserMessage(graph, Payloads.ExplainOnCanvas(graph));
      var input = GhTeaching.Deck(graph, GhTeaching.Read(doc, graph.Selection.Select(n => n.Guid)));
      var ask = LearnAsk(input, panel.StepsJson, user);
      var chars = Prompts.Prompts.TeachingSystem.Length + TeachingCall.Payload(ask).Length;

      if (!confirmed)
      {
        var threshold = DwightSettings.Current.AskBeforeRunOverCents;
        var sentence = threshold > 0 ? LargeRun.Ask(graph.Selection.Count, chars, model, threshold, LargeRun.Quiz, SpendLedger.ReadSample(), SpendLedger.DocId(doc), DateTime.UtcNow) : null;
        if (sentence != null) { panel.ShowDeck(DeckViews.Confirm(sentence)); return; }
      }

      var labels = graph.GuidToIdMap();
      CanvasGlow.SetLabels(labels);   // the numbers the new cards name are the numbers the canvas badges show
      var token = BeginDeckCall(-1);
      panel.ShowDeck(DeckViews.Writing());
      Task.Run(() =>
      {
        var sw = Stopwatch.StartNew();
        TeachingCall.Result asked;
        StoredSpend spend;
        try
        {
          asked = TeachingCall.Ask(NewProvider(model), ask, DwightConfig.MaxTokens, token);
          spend = SpendOf(model, asked.Usage, sw.Elapsed, chars: chars);
        }
        catch (OperationCanceledException ex)
        {
          asked = null;
          spend = UsageCarrier.TryTake(ex, out var paid) ? SpendOf(model, paid, sw.Elapsed, chars: chars) : StoppedEstimate.Of(model, chars, sw.Elapsed, SpendLedger.ReadSample());
        }
        Ui(() =>
        {
          EndDeckCall(token);
          try { SpendLedger.RecordLabelled(doc, DwightVerbs.Explain, spend, LearnLabel); } catch { }
          if (!ReferenceEquals(panel.Deck, deck) || panel.IsClosed) return;
          var fresh = asked == null ? null : TeachingCall.Deal(asked.Cards, asked.Failed, ask.Nodes);
          if (fresh == null) { CanvasGlow.PutNumbersOf(panel); panel.ShowDeckResult(); DwightToast.Show(asked == null ? "Play again was stopped." : TeachingText.Unavailable); return; }   // Brief ES: the old deck stays, so its numbers do
          panel.SetRunLabels(labels);   // Brief ES unit 1: the new cards are written in this read's numbering, and the page's numbers follow them
          panel.ReplaceDeck(fresh, fresh.Dealt ? CostLine(model, spend) : null, model, chars);
          try { CommandHistory.SetDeck(doc, panel.RunRow, fresh.ToJson(), 0, fresh.Count, false); } catch { }
          WalkSession.PageChanged(panel);
        });
      });
    }

    /// <summary>What the deck's one call is asked: the plugin's facts planned from what it read, the Walk's steps and words, and the Walk's own request, sent again as it is.</summary>
    private static DeckAsk LearnAsk(DeckInput input, string stepsView, string graphRequest)
    {
      var (steps, sentences) = TeachingSteps.Of(stepsView);
      return new DeckAsk
      {
        Facts = DeckPlanner.Plan(input, steps), Nodes = input.Nodes, Steps = steps, Sentences = sentences, Summary = TeachingSteps.Summary(stepsView), Graph = graphRequest ?? "",
      };
    }
  }
}
