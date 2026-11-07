using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;
using Dwight.Session;

namespace Dwight.Session.Tests
{
  /// <summary>
  /// Brief DL, the ledger's half: a stopped run whose usage never came back writes one line marked as an ESTIMATE, the Usage section shows estimated cents apart from billed ones, an
  /// estimate is replaced (not doubled) by the bill if it arrives after all, an estimate never feeds the ratio it was made from, and the reopened row says "estimated". The estimate's
  /// own arithmetic and the stopped-call scenarios are in the Agent tests, where the provider seam is.
  /// </summary>
  public class EstimatedLineTests
  {
    private const string Doc = "66fd7275-7717-4065-90eb-483736e9296a";
    private static readonly DateTime Now = new DateTime(2026, 11, 2, 10, 0, 0, DateTimeKind.Local);

    private static StoredSpend Billed(double cents, int input = 4000, int output = 900) =>
      new StoredSpend { Model = "claude-sonnet-5", Provider = "Anthropic", InputTokens = input, OutputTokens = output, Cents = cents, CostText = SpendMath.CentsText(cents), Chars = 12000 };

    private static StoredSpend Estimate(double cents, int input = 3000) =>
      new StoredSpend { Model = "claude-sonnet-5", Provider = "Anthropic", InputTokens = input, OutputTokens = 0, Cents = cents, CostText = SpendMath.CentsText(cents), Chars = 12000, Estimated = StoppedEstimateMark };

    private const string StoppedEstimateMark = "stopped";

    private static string Line(StoredSpend s, string verb = "Explain", string runId = null) =>
      SpendLedgerCore.Serialize(SpendLedgerCore.ForVerb(s, Now.ToUniversalTime(), Doc, "tower.gh", verb, s.Provider, runId));

    // ---- the line -------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void An_estimated_line_carries_its_mark_and_its_run_and_a_billed_line_carries_neither()
    {
      var estimated = Line(Estimate(2.1), runId: "run-1");
      Assert.Contains("\"estimated\":\"stopped\"", estimated);
      Assert.Contains("\"runId\":\"run-1\"", estimated);
      var billed = Line(Billed(6.0));
      Assert.DoesNotContain("estimated", billed);
      Assert.DoesNotContain("runId", billed);
    }

    [Fact]
    public void The_estimate_round_trips_and_an_older_line_reads_as_a_bill()
    {
      var back = SpendLedgerCore.Parse(Line(Estimate(2.1, 3000), runId: "run-1"));
      Assert.Equal("stopped", back.Estimated);
      Assert.Equal("run-1", back.RunId);
      Assert.Equal(0, back.OutputTokens);
      Assert.Equal(3000, back.InputTokens);
      Assert.Equal(12000, back.Chars);

      var old = SpendLedgerCore.Parse("{\"utc\":\"2026-10-30T02:00:00Z\",\"documentId\":\"d\",\"documentName\":\"a.gh\",\"kind\":\"verb\",\"verb\":\"Explain\",\"provider\":\"p\",\"model\":\"m\",\"inputTokens\":10,\"cacheReadTokens\":0,\"cacheWriteTokens\":0,\"outputTokens\":5,\"cents\":1.5,\"seconds\":2}");
      Assert.Null(old.Estimated);
      Assert.Null(old.RunId);
    }

    [Fact]
    public void The_csv_export_has_an_estimated_cell_for_an_estimate_and_an_empty_one_for_a_bill()
    {
      var rows = SpendLedgerCore.CsvRows(new[] { Line(Estimate(2.1), runId: "r"), Line(Billed(6.0)) }).ToList();
      Assert.EndsWith(",chars,estimated", rows[0]);
      Assert.EndsWith(",12000,stopped", rows[1]);
      Assert.EndsWith(",12000,", rows[2]);
    }

    [Fact]
    public void An_estimate_never_feeds_the_ratio_it_was_made_from()
    {
      var sample = new LedgerSample();
      for (var i = 0; i < 3; i++) sample.Add(SpendLedgerCore.Parse(Line(Billed(6.0, input: 4000))));      // 3 chars per token, measured
      var before = sample.CharsPerToken("Anthropic", "claude-sonnet-5");
      var floor = sample.FloorCharsPerToken("Anthropic", "claude-sonnet-5");
      for (var i = 0; i < 20; i++) sample.Add(SpendLedgerCore.Parse(Line(Estimate(2.1, input: 1000), runId: "r" + i)));   // 12 chars per token, an estimate
      Assert.Equal(before, sample.CharsPerToken("Anthropic", "claude-sonnet-5"));
      Assert.Equal(3.0, floor);
      Assert.Equal(floor, sample.FloorCharsPerToken("Anthropic", "claude-sonnet-5"));   // Brief EG: fed back, the estimate's 12 would be the new maximum, and the floor would lift itself
    }

    // ---- the Usage section's totals -----------------------------------------------------------------------------------------------------

    private static LedgerSummary Sum(params string[] lines) => SpendLedgerCore.Summarise(lines, Now);

    [Fact]
    public void Estimated_cents_are_kept_apart_from_billed_ones()
    {
      var t = Sum(Line(Billed(341.0)), Line(Estimate(12.0), runId: "r1")).Total(LedgerPeriod.AllTime);
      Assert.Equal(341.0, t.Cents);
      Assert.Equal(12.0, t.EstimatedCents);
      Assert.Equal(1, t.EstimatedRows);
      Assert.Equal(2, t.Runs);
      Assert.Equal("$3.41 + ~12.0¢ est.", SpendLedgerCore.CostText(t));                                    // the table cell: narrow
      Assert.Equal("$3.41 billed, about 12 c estimated for stopped runs", SpendLedgerCore.CostSentence(t));     // the sentence, where there is room
    }

    [Fact]
    public void A_period_with_only_estimates_says_only_that()
    {
      var t = Sum(Line(Estimate(12.0), runId: "r1")).Total(LedgerPeriod.AllTime);
      Assert.Equal(0, t.Cents);
      Assert.Equal("~12.0¢ est.", SpendLedgerCore.CostText(t));
      Assert.Equal("about 12 c estimated for stopped runs", SpendLedgerCore.CostSentence(t));
    }

    [Fact]
    public void A_period_with_no_estimate_reads_as_it_always_did()
    {
      var t = Sum(Line(Billed(12.3))).Total(LedgerPeriod.AllTime);
      Assert.Equal(0, t.EstimatedRows);
      Assert.Equal(SpendMath.CentsText(12.3), SpendLedgerCore.CostText(t));
      Assert.Equal(SpendMath.CentsText(12.3), SpendLedgerCore.CostSentence(t));
    }

    [Fact]
    public void The_period_line_for_the_eto_fallback_carries_the_whole_sentence()
    {
      var t = Sum(Line(Billed(341.0)), Line(Estimate(12.0), runId: "r1")).Total(LedgerPeriod.AllTime);
      Assert.EndsWith("$3.41 billed, about 12 c estimated for stopped runs", SpendLedgerCore.PeriodLine(t));
    }

    [Theory]
    [InlineData(0.04, "less than 0.1 c")]
    [InlineData(4.0, "about 4 c")]
    [InlineData(12.34, "about 12.3 c")]
    [InlineData(250.0, "about $2.50")]
    public void A_sum_of_estimates_is_never_stated_to_the_cent(double cents, string words)
    {
      Assert.Equal(words, SpendLedgerCore.EstimateWord(cents));
    }

    [Fact]
    public void The_estimate_shows_in_the_by_verb_and_the_by_model_groups_too()
    {
      var s = Sum(Line(Billed(6.0)), Line(Estimate(2.0), runId: "r1"));
      Assert.Equal(2.0, s.ByVerb[(int)LedgerPeriod.AllTime]["Explain"].EstimatedCents);
      Assert.Equal(6.0, s.ByVerb[(int)LedgerPeriod.AllTime]["Explain"].Cents);
      Assert.Equal(2.0, s.ByModel[(int)LedgerPeriod.AllTime].Values.Single().EstimatedCents);
    }

    // ---- a bill that arrives after the estimate replaces it ------------------------------------------------------------------------------------

    [Fact]
    public void A_bill_with_the_estimates_run_key_replaces_it_and_is_counted_once()
    {
      var s = Sum(Line(Estimate(2.0), runId: "run-1"), Line(Billed(7.5), runId: "run-1"));
      var t = s.Total(LedgerPeriod.AllTime);
      Assert.Equal(1, t.Runs);
      Assert.Equal(0, t.EstimatedRows);
      Assert.Equal(7.5, t.Cents);
      Assert.Equal(0, s.Skipped);   // a superseded estimate is not a line that could not be read
    }

    [Fact]
    public void A_bill_for_another_run_replaces_nothing()
    {
      var t = Sum(Line(Estimate(2.0), runId: "run-1"), Line(Billed(7.5), runId: "run-2")).Total(LedgerPeriod.AllTime);
      Assert.Equal(2, t.Runs);
      Assert.Equal(1, t.EstimatedRows);
    }

    [Fact]
    public void The_replacement_may_come_before_or_after_the_estimate_in_the_file_and_the_file_is_never_rewritten()
    {
      var dir = Path.Combine(Path.GetTempPath(), "dwight-est-" + Guid.NewGuid().ToString("N"));
      try
      {
        var path = Path.Combine(dir, SpendLedgerCore.FileName);
        SpendLedgerCore.Append(path, SpendLedgerCore.ForVerb(Estimate(2.0), Now.ToUniversalTime(), Doc, "a.gh", "Explain", "Anthropic", "run-1"));
        SpendLedgerCore.Append(path, SpendLedgerCore.ForVerb(Billed(7.5), Now.ToUniversalTime(), Doc, "a.gh", "Explain", "Anthropic", "run-1"));
        var lines = SpendLedgerCore.ReadLines(path).ToList();
        Assert.Equal(2, lines.Count);                    // both stay on disk: the ledger only ever adds
        var t = SpendLedgerCore.Summarise(SpendLedgerCore.ReadLines(path), Now).Total(LedgerPeriod.AllTime);
        Assert.Equal(1, t.Runs);
        Assert.Equal(7.5, t.Cents);
      }
      finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // ---- the spend and the row ------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_total_with_an_estimated_part_is_an_estimate_and_a_bill_stays_a_bill()
    {
      Assert.Equal("stopped", SpendMath.Combine(Billed(1.0), Estimate(2.0)).Estimated);
      Assert.Equal("stopped", SpendMath.Combine(Estimate(2.0), Billed(1.0)).Estimated);
      Assert.Null(SpendMath.Combine(Billed(1.0), Billed(2.0)).Estimated);
    }

    [Fact]
    public void The_mark_survives_the_session_store_and_a_bill_writes_none()
    {
      var rec = new SessionRecord { Commands = new List<StoredCommand> { new StoredCommand { Action = "Explain", Subtitle = "x", State = "stopped", Spend = Estimate(2.1) }, new StoredCommand { Action = "Explain", Subtitle = "y", Spend = Billed(1.0) } } };
      var text = SpendJson(rec);
      var back = SessionStoreCore.Read(SessionStoreCore.Write(rec)).Record.Commands;
      Assert.Equal("stopped", back[0].Spend.Estimated);
      Assert.Null(back[1].Spend.Estimated);
      Assert.Equal(1, text.Split(new[] { "\"estimated\":\"stopped\"" }, StringSplitOptions.None).Length - 1);
    }

    private static string SpendJson(SessionRecord rec) => SessionStoreCore.Write(rec);

    [Theory]
    [InlineData(4.0, "stopped · about 4 c (estimated)")]
    [InlineData(0.04, "stopped · less than 0.1 c (estimated)")]
    public void A_reopened_stopped_row_says_estimated_in_its_status_line(double cents, string line)
    {
      Assert.Equal(line, DockRows.StatusWord("stopped", Estimate(cents)));
    }

    [Fact]
    public void An_estimate_for_a_model_with_no_price_says_its_own_words_and_estimated()
    {
      var local = new StoredSpend { Model = "qwen", InputTokens = 900, Estimated = "stopped", CostText = "local · free" };
      Assert.Equal("stopped · local · free (estimated)", DockRows.StatusWord("stopped", local));
    }

    [Fact]
    public void A_bill_in_a_stopped_row_still_reads_as_a_bill()
    {
      Assert.Equal("stopped · ~6.0¢", DockRows.StatusWord("stopped", Billed(6.0)));
    }

    // ---- the places the estimate is made and written (LlmActions, DwightSession and the scaffold need Grasshopper, so they are read) -----------------

    private static string Source(params string[] parts)
    {
      var dir = new DirectoryInfo(AppContext.BaseDirectory);
      while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Dwight.sln"))) dir = dir.Parent;
      Assert.True(dir != null);
      return File.ReadAllText(Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray())).Replace("\r\n", "\n");
    }

    [Fact]
    public void Every_paid_request_is_marked_sent_just_before_it_goes_out_and_cleared_when_it_returns()
    {
      var actions = Source("Dwight", "LlmActions.cs");
      Assert.Equal(7, System.Text.RegularExpressions.Regex.Matches(actions, "MarkSent\\(").Count - 1);   // Explain steps, Explain prose, Diagnose, the chain row, to-script, Simplify, Reorganise's labelling
      Assert.Equal(7, System.Text.RegularExpressions.Regex.Matches(actions, "ClearSent\\(").Count - 1);
      foreach (var call in new[] { "res = StructuredOutput.Get(", "(text, usage) = ConverseProse(", "res = ClusterLabeller.Label(" })
        foreach (var at in AllIndexes(actions, call))
        {
          var before = actions.Substring(Math.Max(0, at - 400), Math.Min(400, at));
          Assert.Contains("MarkSent(", before);   // the mark is the line before the request, never after it
        }
    }

    private static IEnumerable<int> AllIndexes(string text, string what)
    {
      for (var i = text.IndexOf(what, StringComparison.Ordinal); i >= 0; i = text.IndexOf(what, i + 1, StringComparison.Ordinal)) yield return i;
    }

    [Fact]
    public void A_stop_estimates_before_it_writes_the_line_in_every_place_a_run_is_cancelled()
    {
      var actions = Source("Dwight", "LlmActions.cs");
      Assert.Contains("catch (OperationCanceledException) { Ui(() => { EstimateStopped(ctx); LedgerRun(ctx); });", actions);
      Assert.Contains("EstimateStopped(chainCtx); LedgerRun(chainCtx); });", actions);
      Assert.Contains("{ Ui(() => { EstimateStopped(ctx); LedgerRun(ctx); DwightSession.Done(gen); }); }", actions);
    }

    [Fact]
    public void A_stop_before_anything_was_sent_writes_nothing_because_the_estimate_needs_a_request_in_flight()
    {
      var actions = Source("Dwight", "LlmActions.cs");
      var est = actions.Substring(actions.IndexOf("private static void EstimateStopped(", StringComparison.Ordinal));
      est = est.Substring(0, est.IndexOf("private static void CarryPaid(", StringComparison.Ordinal));
      Assert.Contains("if (model == null || SpendLedgerCore.HasUsage(ctx.Spend)) return;", est);
      Assert.Contains("ctx.SentModel = null;", est);
      Assert.Contains("if (ctx == null || model == null || chars <= 0) return;", actions);   // MarkSent: no characters, nothing sent
    }

    [Fact]
    public void The_estimated_line_carries_the_runs_key_and_a_late_bill_replaces_it_with_the_same_one()
    {
      var actions = Source("Dwight", "LlmActions.cs");
      var ledger = actions.Substring(actions.IndexOf("private static void LedgerRun(", StringComparison.Ordinal));
      ledger = ledger.Substring(0, ledger.IndexOf("/// <summary>Brief CS", StringComparison.Ordinal));
      Assert.Contains("if (wasEstimate && ctx.SpendLedgered) ctx.EstimateWritten = true;", ledger);
      Assert.Contains("SpendLedger.RecordVerb(ctx.Doc, ctx.Action, DateTime.UtcNow, ctx.Spend, ctx.RunId);", ledger);
      Assert.Contains("ctx.Replaced = true;", ledger);
      var shim = Source("Dwight", "Session", "SpendLedger.cs");
      Assert.Contains("spend.Estimated != null ? runId : null", shim);   // only an estimate carries the key; a bill carries it only as the replacement
    }

    [Fact]
    public void A_stopped_chat_turn_estimates_its_in_flight_step_and_writes_one_turn_line()
    {
      var driver = Source("Dwight", "Agent", "AgentTurnDriver.cs");
      Assert.Contains("result.InFlightChars = RequestChars(d.System, messages);", driver);
      var scaffold = Source("Dwight", "UI", "DwightScaffold.cs");
      var turn = Source("Dwight", "Agent", "TurnSpend.cs");
      Assert.Contains("if (result == null || result.Outcome != TurnOutcome.Stopped || result.InFlightChars <= 0) return billed;", turn);
      Assert.Contains("StoppedEstimate.Of(model, result.InFlightChars, elapsed, sample, result.InFlightImageTokens);", turn);   // the one price for every stopped request, the cache's
      Assert.Contains("CommandHistory.AppendThreadSpend(doc, threadId, spent);", scaffold);   // the one write: the turn's line comes from the history, as it always did
    }
  }
}
