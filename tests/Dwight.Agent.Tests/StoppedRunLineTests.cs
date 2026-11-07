using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Dwight.Agent;
using Dwight.Llm;
using Dwight.Session;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// Brief DL: a run stopped after its request went out still cost its input, and the ledger says so, as an estimate. The provider here never returns on cancel (it throws
  /// when the token fires, so no reply and no usage ever arrives), and the scenarios are run through the product's own pieces, a real ledger file and its real reader:
  /// one line each for Explain, Diagnose, Reorganise's labelling, Simplify and a chat turn, never a second; a bill that then arrives replaces the estimate; a Stop before the send
  /// writes nothing; and the Usage totals keep estimated cents apart from billed.
  /// </summary>
  public class StoppedRunLineTests
  {
    private const string Doc = "66fd7275-7717-4065-90eb-483736e9296a";
    private static readonly ModelInfo Sonnet = new ModelInfo("claude-sonnet-5", "Sonnet 5", "Anthropic", "Mid", 3.0, 15.0, 1_000_000);

    /// <summary>A provider that never answers: it cancels its own token when sent, and throws, so nothing comes back.</summary>
    private sealed class NeverReplies : ILlmProvider
    {
      public CancellationTokenSource Cts;
      public int Sent;
      public string ModelId => "fake";
      public ProviderCapability Capability => default(ProviderCapability);
      public LlmResponse Send(LlmRequest request, CancellationToken cancel = default)
      {
        Sent++;
        Cts.Cancel();
        cancel.ThrowIfCancellationRequested();
        throw new InvalidOperationException("unreachable: the token was cancelled");
      }
    }

    // ---- the estimate itself -----------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_estimate_is_the_inputs_alone_at_the_ledgers_own_ratio_with_no_output_and_the_mark()
    {
      var e = StoppedEstimate.Of(Sonnet, 12_000, TimeSpan.FromSeconds(4.5), null);
      var tokens = LargeRun.InputTokens(Sonnet, 12_000, null);
      Assert.Equal((int)tokens, e.InputTokens);
      Assert.Equal(0, e.OutputTokens);
      Assert.Equal(Sonnet.CostCents(0, 0, tokens, 0), e.Cents.Value);   // the input at the cache's rate, the least it can cost (the known item closed after #254)
      Assert.Equal("stopped", e.Estimated);
      Assert.Equal(12_000, e.Chars);
      Assert.Equal(4500, e.ElapsedMs);
      Assert.Equal("claude-sonnet-5", e.Model);
      Assert.Equal("Anthropic", e.Provider);
    }

    [Fact]
    public void The_estimate_uses_the_ratio_this_providers_recent_lines_measured_when_there_are_enough()
    {
      var sample = new LedgerSample();
      for (var i = 0; i < 3; i++)
        sample.Add(new LedgerLine { Kind = LedgerLine.KindVerb, Verb = "Explain", Provider = "Anthropic", Model = "m", Chars = 4000, InputTokens = 4000, Utc = "2026-11-01T00:00:00Z" });   // 1 char a token
      var measured = StoppedEstimate.Of(Sonnet, 12_000, TimeSpan.Zero, sample);
      var stated = StoppedEstimate.Of(Sonnet, 12_000, TimeSpan.Zero, null);
      Assert.Equal(12000, measured.InputTokens);
      Assert.True(measured.InputTokens > stated.InputTokens);
    }

    [Fact]
    public void A_request_with_no_characters_or_no_model_has_no_estimate_because_nothing_went_out()
    {
      Assert.Null(StoppedEstimate.Of(Sonnet, 0, TimeSpan.Zero, null));
      Assert.Null(StoppedEstimate.Of(Sonnet, -5, TimeSpan.Zero, null));
      Assert.Null(StoppedEstimate.Of(null, 12_000, TimeSpan.Zero, null));
    }

    [Fact]
    public void A_local_model_is_estimated_in_tokens_and_free_and_a_plan_model_says_its_plan()
    {
      var local = new ModelInfo("qwen", "Qwen", "Ollama", "Mid", 0, 0, 32_000, pricingModel: "free");
      var e = StoppedEstimate.Of(local, 6_000, TimeSpan.Zero, null);
      Assert.Null(e.Cents);
      Assert.Equal("local · free", e.CostText);
      Assert.True(e.InputTokens > 0);
      Assert.Equal("stopped", e.Estimated);
    }

    // ---- the scenarios, through a real ledger -------------------------------------------------------------------------------------------

    private sealed class Run : IDisposable
    {
      private readonly string _dir = Path.Combine(Path.GetTempPath(), "dwight-stop-" + Guid.NewGuid().ToString("N"));
      public readonly string Verb;
      public readonly string RunId = Guid.NewGuid().ToString("N");
      public StoredSpend Spend;
      public bool Ledgered;
      public Run(string verb) { Verb = verb; }
      public string LedgerPath => Path.Combine(_dir, SpendLedgerCore.FileName);

      /// <summary>What a verb does with a request that goes out and is stopped: mark it sent, send it, and on the cancellation estimate it and write the run's one line.</summary>
      public NeverReplies Stopped(bool sent = true, long chars = 12_000)
      {
        var cts = new CancellationTokenSource();
        var provider = new NeverReplies { Cts = cts };
        ModelInfo inFlight = null;
        if (sent) inFlight = Sonnet;                           // MarkSent: just before the send
        try
        {
          if (sent) StructuredOutput.Get(provider, "s", "u", "tool", "d", "{}", j => (true, null), 1000, cts.Token);
          else cts.Cancel();                                   // a Stop before the request went out
          cts.Token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
          // EstimateStopped: a request in flight and no usage anywhere on the run
          if (inFlight != null && !SpendLedgerCore.HasUsage(Spend)) Spend = StoppedEstimate.Of(inFlight, chars, TimeSpan.FromSeconds(4.5), null);
        }
        LedgerRun();
        return provider;
      }

      /// <summary>LedgerRun: the one line, once.</summary>
      public void LedgerRun()
      {
        if (SpendLedgerCore.TakeRunLine(Spend, ref Ledgered))
          SpendLedgerCore.Append(LedgerPath, SpendLedgerCore.ForVerb(Spend, DateTime.UtcNow, Doc, "tower.gh", Verb, Spend.Provider, Spend.Estimated != null ? RunId : null));
      }

      /// <summary>The usage arrives after all: the bill, with the run's key, replaces the estimate.</summary>
      public void BillArrives(double cents, int input = 4000, int output = 900)
      {
        Spend = new StoredSpend { Model = "claude-sonnet-5", Provider = "Anthropic", InputTokens = input, OutputTokens = output, Cents = cents, CostText = SpendMath.CentsText(cents), Chars = 12_000 };
        SpendLedgerCore.Append(LedgerPath, SpendLedgerCore.ForVerb(Spend, DateTime.UtcNow, Doc, "tower.gh", Verb, Spend.Provider, RunId));
      }

      public LedgerLine[] Lines => SpendLedgerCore.ReadLines(LedgerPath).Select(SpendLedgerCore.Parse).ToArray();
      public LedgerTotal Total => SpendLedgerCore.Summarise(SpendLedgerCore.ReadLines(LedgerPath), DateTime.Now).Total(LedgerPeriod.AllTime);
      public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }
    }

    [Theory]
    [InlineData(DwightVerbs.Explain)]
    [InlineData(DwightVerbs.Diagnose)]
    [InlineData(DwightVerbs.Reorganise)]
    [InlineData(DwightVerbs.Simplify)]
    public void A_stopped_verb_whose_request_went_out_leaves_one_estimated_line_and_no_second(string verb)
    {
      using (var r = new Run(verb))
      {
        var p = r.Stopped();
        Assert.Equal(1, p.Sent);
        var line = Assert.Single(r.Lines);
        Assert.Equal(verb, line.Verb);
        Assert.Equal("stopped", line.Estimated);
        Assert.Equal(0, line.OutputTokens);
        Assert.True(line.InputTokens > 0);
        Assert.True(line.Cents > 0);
        Assert.Equal(12_000, line.Chars);
        Assert.Equal(r.RunId, line.RunId);

        r.LedgerRun();   // the row that follows, or a second ending, writes nothing more
        Assert.Single(r.Lines);
      }
    }

    [Fact]
    public void A_stopped_chat_turn_leaves_one_estimated_turn_line_with_the_in_flight_steps_input()
    {
      var provider = new NeverReplies { Cts = new CancellationTokenSource() };
      var result = AgentTurnDriver.Drive(new TurnDrive
      {
        Provider = provider,
        System = "system",
        Messages = new List<LlmMessage> { LlmMessage.User("Add a turbine spacing check.") },
        Tools = new List<LlmTool>(),
        Limits = new TurnLimits { MaxSteps = 4, WallClockMs = 0, MaxTranscriptChars = 1_000_000, AnswerBudget = 1000 },
        Execute = _ => "{}",
        Cancel = provider.Cts.Token,
      }, Stopwatch.StartNew());

      Assert.Equal(TurnOutcome.Stopped, result.Outcome);
      Assert.Equal("system".Length + "Add a turbine spacing check.".Length, result.InFlightChars);

      // what DwightScaffold does with it: one estimate, one turn line
      var estimate = StoppedEstimate.Of(Sonnet, result.InFlightChars, TimeSpan.FromSeconds(3), null);
      estimate.Steps = result.Steps; estimate.ToolCalls = result.ToolCalls;
      var line = SpendLedgerCore.ForTurn(estimate, DateTime.UtcNow, Doc, "tower.gh", DwightVerbs.Build, "Anthropic", "t1", 1);
      Assert.Equal("stopped", line.Estimated);
      Assert.Equal(LedgerLine.KindTurn, line.Kind);
      Assert.Equal(0, line.OutputTokens);
    }

    [Fact]
    public void A_turn_stopped_before_any_step_was_sent_has_no_in_flight_request()
    {
      var provider = new NeverReplies { Cts = new CancellationTokenSource() };
      provider.Cts.Cancel();
      var result = AgentTurnDriver.Drive(new TurnDrive
      {
        Provider = provider, System = "system", Messages = new List<LlmMessage> { LlmMessage.User("hi") }, Tools = new List<LlmTool>(),
        Limits = new TurnLimits { MaxSteps = 4, WallClockMs = 0, MaxTranscriptChars = 1_000_000, AnswerBudget = 1000 },
        Execute = _ => "{}", Cancel = provider.Cts.Token,
      }, Stopwatch.StartNew());
      Assert.Equal(TurnOutcome.Stopped, result.Outcome);
      Assert.Equal(0, provider.Sent);
      Assert.Equal(0, result.InFlightChars);
    }

    [Fact]
    public void A_stop_before_the_request_went_out_writes_nothing()
    {
      using (var r = new Run(DwightVerbs.Explain))
      {
        var p = r.Stopped(sent: false);
        Assert.Equal(0, p.Sent);
        Assert.Empty(r.Lines);
        Assert.Null(r.Spend);
      }
    }

    [Fact]
    public void A_run_that_already_holds_usage_keeps_it_and_is_not_estimated_over()
    {
      using (var r = new Run(DwightVerbs.Simplify))
      {
        r.Spend = new StoredSpend { Model = "claude-sonnet-5", Provider = "Anthropic", InputTokens = 4000, OutputTokens = 900, Cents = 6.0 };   // attempt one was billed
        r.Stopped();
        var line = Assert.Single(r.Lines);
        Assert.Null(line.Estimated);
        Assert.Equal(900, line.OutputTokens);
      }
    }

    [Fact]
    public void A_bill_that_arrives_after_the_estimate_replaces_it_and_the_run_counts_once()
    {
      using (var r = new Run(DwightVerbs.Explain))
      {
        r.Stopped();
        Assert.Equal(1, r.Total.EstimatedRows);
        r.BillArrives(7.5);
        Assert.Equal(2, r.Lines.Length);          // the ledger only adds
        Assert.Equal(1, r.Total.Runs);            // but the run is one
        Assert.Equal(0, r.Total.EstimatedRows);
        Assert.Equal(7.5, r.Total.Cents);
      }
    }

    [Fact]
    public void The_usage_total_splits_estimated_from_billed_across_runs()
    {
      using (var a = new Run(DwightVerbs.Explain))
      {
        a.BillArrives(341.0);   // a billed run, written the way any run is
        var b = new Run(DwightVerbs.Diagnose);
        var estimate = StoppedEstimate.Of(Sonnet, 12_000, TimeSpan.Zero, null);
        SpendLedgerCore.Append(a.LedgerPath, SpendLedgerCore.ForVerb(estimate, DateTime.UtcNow, Doc, "tower.gh", DwightVerbs.Diagnose, "Anthropic", b.RunId));
        var t = a.Total;
        Assert.Equal(341.0, t.Cents);
        Assert.Equal(estimate.Cents.Value, t.EstimatedCents, 6);
        Assert.StartsWith("$3.41 billed, about ", SpendLedgerCore.CostSentence(t));
        Assert.EndsWith(" estimated for stopped runs", SpendLedgerCore.CostSentence(t));
        Assert.StartsWith("$3.41 + ~", SpendLedgerCore.CostText(t));
        b.Dispose();
      }
    }

    [Fact]
    public void The_estimates_cents_are_the_inputs_share_of_a_full_runs_bill_and_never_more()
    {
      // A full run of the same request: its input, plus an answer. The estimate is the input's price alone, so it is below the bill, never above.
      var tokens = LargeRun.InputTokens(Sonnet, 12_000, null);
      var full = Sonnet.CostCents(0, 800, 0, tokens) + 0.0;
      var estimate = StoppedEstimate.Of(Sonnet, 12_000, TimeSpan.Zero, null).Cents.Value;
      Assert.True(estimate > 0);
      Assert.True(estimate <= LargeRun.Estimate(Sonnet, 12_000, DwightVerbs.Explain, null));
    }
  }
}
