using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;
using Dwight.Session;

namespace Dwight.Session.Tests
{
  /// <summary>Brief CT: every paid call leaves one line, whether or not a row follows. The first half runs each
  /// state through the two decisions the plugin takes and a real ledger file, so a line is counted where it lands.
  /// LlmActions, DwightSession and CommandHistory need Grasshopper and cannot load here, so the second half pins
  /// by text that they take those decisions at the places the states are reached.</summary>
  public class PreviewSpendTests
  {
    private const string Doc = "66fd7275-7717-4065-90eb-483736e9296a";

    /// <summary>One run with a preview, as the context carries it: the spend, the flag, and the ledger file the two
    /// decisions write to.</summary>
    private sealed class Run : IDisposable
    {
      private readonly string _dir = Path.Combine(Path.GetTempPath(), "dwight-preview-" + Guid.NewGuid().ToString("N"));
      private readonly string _verb;
      public StoredSpend Spend;
      public bool Ledgered;
      public int HistoryRows;
      public StoredSpend RowSpend;

      public Run(string verb = DwightVerbs.Simplify) { _verb = verb; }

      private string LedgerPath => Path.Combine(_dir, SpendLedgerCore.FileName);

      /// <summary>A model call came back and its usage joined the run's, as both previews add it.</summary>
      public Run Paid(int input, int output, double? cents = null)
      {
        Spend = SpendMath.Combine(Spend, new StoredSpend
        {
          Model = "claude-sonnet-5", Provider = "Anthropic", ElapsedMs = 2000, InputTokens = input, OutputTokens = output,
          Cents = cents, CostText = cents.HasValue ? SpendMath.CentsText(cents.Value) : "local · free",
        });
        return this;
      }

      /// <summary>The run has ended, whichever way: <c>LedgerRun</c>.</summary>
      public Run Ended()
      {
        if (SpendLedgerCore.TakeRunLine(Spend, ref Ledgered)) Write(Spend);
        return this;
      }

      /// <summary>The apply landed: <c>Complete</c> then <c>CommandHistory.Record</c>.</summary>
      public Run Applied()
      {
        HistoryRows++;
        RowSpend = Spend;
        if (SpendLedgerCore.RecordWritesLine(Spend, Ledgered)) Write(Spend);
        return this;
      }

      private void Write(StoredSpend s) =>
        SpendLedgerCore.Append(LedgerPath, SpendLedgerCore.ForVerb(s, DateTime.UtcNow, Doc, "tower.gh", _verb, s.Provider));

      public LedgerLine[] Lines => SpendLedgerCore.ReadLines(LedgerPath).Select(SpendLedgerCore.Parse).ToArray();

      public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }
    }

    // ---------------------------------------------------------------- one line, every state

    [Fact]
    public void A_preview_closed_unapplied_has_one_line_and_no_row()
    {
      using (var r = new Run().Paid(4000, 900, 2.6).Ended())
      {
        var line = Assert.Single(r.Lines);
        Assert.Equal(LedgerLine.KindVerb, line.Kind);
        Assert.Equal(DwightVerbs.Simplify, line.Verb);
        Assert.Equal(Doc, line.DocumentId);
        Assert.Equal(4000, line.InputTokens);
        Assert.Equal(900, line.OutputTokens);
        Assert.Equal(2.6, line.Cents);
        Assert.Equal(0, r.HistoryRows);
      }
    }

    [Fact]
    public void A_preview_applied_has_one_line_and_one_row_and_the_row_keeps_the_cost()
    {
      using (var r = new Run().Paid(4000, 900, 2.6).Ended().Applied())
      {
        Assert.Single(r.Lines);
        Assert.Equal(1, r.HistoryRows);
        Assert.Same(r.Spend, r.RowSpend);
        Assert.Equal(2.6, r.RowSpend.Cents);
      }
    }

    [Fact]
    public void A_preview_applied_and_reverted_has_one_line_because_the_money_was_spent()
    {
      // A verifier's revert ends in SetSimplifyOutcome and never reaches Complete: no row, and the line stands.
      using (var r = new Run().Paid(4000, 900, 2.6).Ended())
      {
        Assert.Single(r.Lines);
        Assert.Equal(0, r.HistoryRows);
        Assert.Equal(2.6, r.Lines[0].Cents);
      }
    }

    [Fact]
    public void A_three_attempt_refine_loop_is_one_line_holding_all_three()
    {
      using (var r = new Run().Paid(1000, 100, 1.0).Paid(1100, 110, 1.5).Paid(1200, 120, 2.0).Ended())
      {
        var line = Assert.Single(r.Lines);
        Assert.Equal(3300, line.InputTokens);
        Assert.Equal(330, line.OutputTokens);
        Assert.Equal(4.5, line.Cents.Value, 6);
        Assert.Equal(6.0, line.Seconds);
      }
    }

    [Fact]
    public void A_refine_loop_then_an_apply_is_still_one_line()
    {
      using (var r = new Run().Paid(1000, 100, 1.0).Paid(1100, 110, 1.5).Paid(1200, 120, 2.0).Ended().Applied())
      {
        Assert.Single(r.Lines);
        Assert.Equal(1, r.HistoryRows);
        Assert.Equal(4.5, r.RowSpend.Cents.Value, 6);
      }
    }

    [Fact]
    public void A_run_ending_twice_writes_once()
    {
      using (var r = new Run().Paid(4000, 900, 2.6).Ended().Ended())
        Assert.Single(r.Lines);
    }

    [Fact]
    public void A_preview_that_fails_before_any_paid_call_has_no_line()
    {
      using (var none = new Run().Ended())
      {
        Assert.Empty(none.Lines);
        Assert.False(none.Ledgered);
      }
      // A refusal the provider does not bill, or a call with nothing reported: a spend of zeros is not a cost.
      using (var zeros = new Run().Paid(0, 0).Ended())
      {
        Assert.Empty(zeros.Lines);
        Assert.False(zeros.Ledgered);
      }
    }

    [Fact]
    public void A_preview_that_fails_after_a_paid_call_has_one_line()
    {
      // The model answered twice and neither answer validated; or the third attempt would not compile.
      using (var r = new Run().Paid(4000, 900, 2.6).Ended())
      {
        Assert.Single(r.Lines);
        Assert.Equal(0, r.HistoryRows);
      }
    }

    [Fact]
    public void A_run_with_any_usage_at_all_has_its_line()
    {
      var bare = new StoredSpend();
      Assert.False(SpendLedgerCore.HasUsage(null));
      Assert.False(SpendLedgerCore.HasUsage(bare));
      Assert.True(SpendLedgerCore.HasUsage(new StoredSpend { InputTokens = 1 }));
      Assert.True(SpendLedgerCore.HasUsage(new StoredSpend { OutputTokens = 1 }));
      Assert.True(SpendLedgerCore.HasUsage(new StoredSpend { CacheReadTokens = 1 }));
      Assert.True(SpendLedgerCore.HasUsage(new StoredSpend { CacheWriteTokens = 1 }));
      Assert.True(SpendLedgerCore.HasUsage(new StoredSpend { Searches = 1 }));
    }

    [Fact]
    public void A_local_model_has_its_line_with_no_cents()
    {
      using (var r = new Run().Paid(4000, 900).Ended().Applied())
        Assert.Null(Assert.Single(r.Lines).Cents);
    }

    [Fact]
    public void Two_runs_are_two_lines_and_each_holds_only_its_own_spend()
    {
      // "Try a corrected proposal" and Retry begin a fresh context, whose spend starts empty. The ledger only ever
      // adds, so the first line cannot be merged into the second, and the two must add up to what was spent.
      using (var first = new Run().Paid(4000, 900, 2.6).Ended())
      using (var second = new Run().Paid(4100, 950, 2.7).Ended())
      {
        Assert.Equal(2.6, Assert.Single(first.Lines).Cents);
        Assert.Equal(2.7, Assert.Single(second.Lines).Cents);
      }
    }

    // ---------------------------------------------------------------- CommandHistory.Record's decision

    [Fact]
    public void Recording_with_the_flag_set_writes_no_line()
    {
      Assert.False(SpendLedgerCore.RecordWritesLine(new StoredSpend { InputTokens = 900 }, spendLedgered: true));
    }

    [Fact]
    public void Recording_without_the_flag_writes_one_line_as_it_always_did()
    {
      // Explain, Diagnose and Reorganise: the flag is never set, so their one line is Record's, as today.
      using (var r = new Run().Paid(4000, 900, 2.6).Applied())
      {
        Assert.Single(r.Lines);
        Assert.Equal(1, r.HistoryRows);
      }
      Assert.True(SpendLedgerCore.RecordWritesLine(new StoredSpend { InputTokens = 900 }, spendLedgered: false));
    }

    [Fact]
    public void Recording_a_row_with_no_spend_writes_no_line()
    {
      Assert.False(SpendLedgerCore.RecordWritesLine(null, spendLedgered: false));
      Assert.False(SpendLedgerCore.RecordWritesLine(null, spendLedgered: true));
    }

    [Fact]
    public void A_preview_with_no_usage_that_is_applied_still_has_the_line_it_had_before()
    {
      // A provider that reported nothing: no line when it ended, and the flag stays off, so the apply writes as today.
      using (var r = new Run().Paid(0, 0).Ended().Applied())
        Assert.Single(r.Lines);
    }

    // ---------------------------------------------------------------- a billed call that then fails, in every verb

    private static StoredSpend Billed() => new StoredSpend { Model = "claude-sonnet-5", Provider = "Anthropic", InputTokens = 4000, OutputTokens = 900, Cents = 2.6 };

    [Theory]
    [InlineData(DwightVerbs.Explain)]
    [InlineData(DwightVerbs.Diagnose)]
    [InlineData(DwightVerbs.Reorganise)]
    [InlineData("Reorganise (best-practice)")]
    public void A_billed_call_that_fails_leaves_one_line_under_its_verb(string verb)
    {
      using (var r = new Run(verb).Paid(4000, 900, 2.6).Ended())
      {
        var line = Assert.Single(r.Lines);
        Assert.Equal(verb, line.Verb);
        Assert.Equal(2.6, line.Cents);
        Assert.Equal(0, r.HistoryRows);
      }
    }

    [Theory]
    [InlineData(DwightVerbs.Explain)]
    [InlineData(DwightVerbs.Diagnose)]
    [InlineData(DwightVerbs.Reorganise)]
    public void A_call_that_fails_unbilled_leaves_no_line(string verb)
    {
      using (var r = new Run(verb).Paid(0, 0).Ended())
        Assert.Empty(r.Lines);
    }

    [Fact]
    public void A_run_keeps_a_calls_spend_if_the_call_worked_or_was_billed_anyway()
    {
      var billed = Billed();
      var zeros = new StoredSpend { Model = "m" };
      Assert.Same(billed, SpendLedgerCore.KeptSpend(true, billed));
      Assert.Same(billed, SpendLedgerCore.KeptSpend(false, billed));
      Assert.Same(zeros, SpendLedgerCore.KeptSpend(true, zeros));   // a pass whose names are all cached, as today
      Assert.Null(SpendLedgerCore.KeptSpend(false, zeros));
      Assert.Null(SpendLedgerCore.KeptSpend(true, null));
      Assert.Null(SpendLedgerCore.KeptSpend(false, null));
    }

    [Fact]
    public void Reorganise_with_a_billed_failed_labelling_that_still_lands_has_one_line_and_its_row_keeps_the_cost()
    {
      using (var r = new Run(DwightVerbs.Reorganise))
      {
        r.Spend = SpendLedgerCore.KeptSpend(false, Billed());
        r.Ended().Applied();
        Assert.Equal(2.6, Assert.Single(r.Lines).Cents);
        Assert.Equal(1, r.HistoryRows);
        Assert.Equal(2.6, r.RowSpend.Cents);
      }
    }

    [Fact]
    public void Reorganise_whose_layout_never_lands_has_its_line_and_no_row()
    {
      // The labelling worked; then a stop, a changed document, or an apply the fingerprint refused.
      using (var r = new Run(DwightVerbs.Reorganise))
      {
        r.Spend = SpendLedgerCore.KeptSpend(true, Billed());
        r.Ended();
        Assert.Single(r.Lines);
        Assert.Equal(0, r.HistoryRows);
      }
    }

    [Fact]
    public void Reorganise_with_an_unbilled_failed_labelling_has_a_row_with_no_cost_and_no_line()
    {
      using (var r = new Run(DwightVerbs.Reorganise))
      {
        r.Spend = SpendLedgerCore.KeptSpend(false, new StoredSpend { Model = "m" });
        r.Ended().Applied();
        Assert.Empty(r.Lines);
        Assert.Equal(1, r.HistoryRows);
        Assert.Null(r.RowSpend);
      }
    }

    [Fact]
    public void Reorganise_with_all_its_names_cached_keeps_the_row_written_line_it_had_before()
    {
      using (var r = new Run(DwightVerbs.Reorganise))
      {
        r.Spend = SpendLedgerCore.KeptSpend(true, new StoredSpend { Model = "m" });
        r.Ended();
        Assert.False(r.Ledgered);
        r.Applied();
        Assert.Equal(0, Assert.Single(r.Lines).TotalInputTokens);
      }
    }

    // ---------------------------------------------------------------- the seams, by text

    private static string Actions => File.ReadAllText(RepoFile("Dwight", "LlmActions.cs")).Replace("\r\n", "\n");
    private static string Session => File.ReadAllText(RepoFile("Dwight", "DwightSession.cs")).Replace("\r\n", "\n");
    private static string History => File.ReadAllText(RepoFile("Dwight", "Session", "CommandHistory.cs")).Replace("\r\n", "\n");

    /// <summary>The text of one method: from its signature up to the next member at the class's indent.</summary>
    private static string Method(string src, string signature)
    {
      var start = src.IndexOf(signature, StringComparison.Ordinal);
      Assert.True(start >= 0, "not found: " + signature);
      var end = src.IndexOf("\n    }\n", start, StringComparison.Ordinal);
      Assert.True(end > start, "no end for: " + signature);
      return src.Substring(start, end - start);
    }

    private static int Count(string text, string what) => Regex.Matches(text, Regex.Escape(what)).Count;

    private static void InOrder(string text, params string[] parts)
    {
      var at = -1;
      foreach (var p in parts)
      {
        var i = text.IndexOf(p, at + 1, StringComparison.Ordinal);
        Assert.True(i > at, "out of order or missing: " + p);
        at = i;
      }
    }

    private const string Line = "LedgerRun(ctx);";

    [Fact]
    public void Record_asks_the_decision_and_passes_it_the_commands_flag()
    {
      var record = Method(History, "public static void Record(GH_Document doc, DwightCommand cmd)");
      InOrder(record, "Persist(doc, key, list);",
        "SpendLedgerCore.RecordWritesLine(cmd.Spend, cmd.SpendLedgered)", "SpendLedger.RecordVerb(doc, cmd.Action, cmd.Time, cmd.Spend)");
    }

    [Fact]
    public void Complete_hands_the_row_the_spend_and_the_flag()
    {
      var complete = Method(Session, "public static void Complete(Context ctx, string subtitle, string costLine,");
      Assert.Contains("Spend = spend,", complete);
      Assert.Contains("SpendLedgered = ctx.SpendLedgered,", complete);
    }

    [Fact]
    public void The_flag_lives_in_memory_and_never_reaches_the_session_file()
    {
      // A row read back never passes through Record, so the flag has nothing to do there, and the wire format is unchanged.
      Assert.DoesNotContain("SpendLedgered", Method(History, "private static StoredCommand ToStored(DwightCommand c)"));
      Assert.DoesNotContain("SpendLedgered", File.ReadAllText(RepoFile("Dwight", "Session", "SessionStoreCore.cs")));
    }

    [Fact]
    public void The_line_comes_from_the_ledgers_own_writer_for_the_runs_document_and_verb()
    {
      var src = Actions;
      var ledgerRun = Method(src, "private static void LedgerRun(DwightSession.Context ctx)");
      Assert.Contains("SpendLedger.RecordRun(ctx.Doc, ctx.Action, ctx.Spend, ref ctx.SpendLedgered, ctx.RunId)", ledgerRun);   // Brief DL: with the run's key
      var shim = File.ReadAllText(RepoFile("Dwight", "Session", "SpendLedger.cs")).Replace("\r\n", "\n");
      InOrder(Method(shim, "public static void RecordRun("), "SpendLedgerCore.TakeRunLine(spend, ref ledgered)", "RecordVerb(doc, verb,");
    }

    [Fact]
    public void Better_logic_writes_when_the_call_returns_whether_or_not_it_worked_or_was_stopped()
    {
      var src = Actions;
      var work = src.Substring(src.IndexOf("private static void BetterLogic(", StringComparison.Ordinal));
      work = work.Substring(0, work.IndexOf("/// <summary>RR5 #2: the user-facing copy", StringComparison.Ordinal));

      // The spend is known before the two outcomes part, so a call that came back unusable still counts.
      InOrder(work, "sw.Stop();", "ctx.Spend = SpendMath.Combine(ctx.Spend, SpendOf(model, res.Usage, sw.Elapsed, chars: system.Length + user.Length));", "if (!res.Ok || groups == null)");
      // Failed: the line before the IsCurrent gate, which a Stop has already closed.
      InOrder(work, "if (!res.Ok || groups == null)", "Ui(() => { " + Line + " if (DwightSession.IsCurrent(gen))", "DwightSession.Failed(ctx, \"error\", failTitle, failMsg)");
      // Worked: the same, before anything renders.
      InOrder(work, "var appliedGroups = groups;", "Ui(() =>", Line, "if (!DwightSession.IsCurrent(gen)) { DwightSession.Done(gen); return; }", "wp.SetSimplifyPreview(");
      Assert.Equal(2, Count(work, Line));
    }

    [Fact]
    public void To_script_writes_only_at_the_loops_ends_and_never_on_the_way_round()
    {
      var src = Actions;
      var attempt = Method(src, "private static void ToScriptAttempt(DwightSession.Context ctx,");

      // One assignment, straight after the call, holding every attempt so far. It is what a Stop or a failure reads.
      // Brief DF changed this from 1 to 2: the second is the catch that carries a throwing repair turn's first-turn bill onto the same spend.
      Assert.Equal(2, Count(attempt, "ctx.Spend = "));
      InOrder(attempt, "Accumulate(spent, res.Usage);", "var total = elapsed + sw.Elapsed;", "ctx.Spend = SpendOf(model, spent, total, chars: attempt == 1 ? system.Length + message.Length : 0);", "if (!res.Ok || proposal == null)");

      // The call came back unusable.
      InOrder(attempt, "if (!res.Ok || proposal == null)", Line, "if (DwightSession.IsCurrent(gen))", "DwightSession.Failed(ctx, \"error\", \"Couldn't write the script\"");
      // Stopped while the reply crossed to the UI thread.
      Assert.Contains("if (!DwightSession.IsCurrent(gen)) { " + Line + " DwightSession.Done(gen); return; }", attempt);
      // Nothing built, three times.
      InOrder(attempt, "ToScriptCompileGate.Trial(", Line, "DwightSession.Failed(ctx, \"error\", \"Dwight couldn't write a script that runs\"");

      // Those are all, so a repair or a refine turn, which recurses, writes nothing.
      Assert.Equal(3, Count(attempt, Line));
      var callback = attempt.Substring(attempt.IndexOf("ToScriptCompileGate.Trial(", StringComparison.Ordinal));
      var failed = callback.IndexOf("DwightSession.Failed(ctx, \"error\", \"Dwight couldn't write a script that runs\"", StringComparison.Ordinal);
      Assert.InRange(callback.IndexOf(Line, StringComparison.Ordinal), callback.LastIndexOf("attempt + 1", failed, StringComparison.Ordinal), failed);

      // Every preview, verified or a draft, goes through one method, and it writes before it renders.
      var preview = Method(src, "private static void ShowToScriptPreview(");
      InOrder(preview, Line, "CanvasGlow.Show(ctx.Guids, false, popup as WebPanel);");
    }

    [Fact]
    public void A_run_ended_by_an_exception_writes_what_it_had_spent()
    {
      // Attempt two's call is stopped or fails: attempt one was paid for and the loop will never reach an end of its own.
      var run = Method(Actions, "private static void Run(Action<int, CancellationToken> work,");
      // Brief DL: the first catch (a Stop or a timeout) now estimates a request that was in flight before it writes the line, so it is not the bare form; the other two are.
      Assert.Equal(2, Count(run, "Ui(() => LedgerRun(ctx));"));
      InOrder(run, "catch (OperationCanceledException)", "Ui(() => { EstimateStopped(ctx); LedgerRun(ctx); });",
        "catch (LlmException ex)", "Ui(() => LedgerRun(ctx));", "Fail(gen, \"Dwight hit a problem\", ex.Message)",
        "catch (Exception ex)", "Ui(() => LedgerRun(ctx));", "Fail(gen, \"Unexpected error\"");
    }

    [Fact]
    public void Run_with_no_context_writes_nothing()
    {
      Assert.Contains("if (ctx == null) return;", Method(Actions, "private static void LedgerRun(DwightSession.Context ctx)"));
    }

    [Fact]
    public void The_applies_still_record_with_the_spend_and_add_no_line_of_their_own()
    {
      var src = Actions;
      var simplify = Method(src, "private static void RenderSimplifyApplied(");
      var script = Method(src, "private static void RenderToScriptApplied(");
      Assert.Contains("live, ctx.Spend);", simplify);
      Assert.Contains("live, ctx.Spend);", script);
      Assert.DoesNotContain("LedgerRun", simplify);
      Assert.DoesNotContain("LedgerRun", script);
    }

    [Fact]
    public void A_reverted_apply_adds_neither_a_row_nor_a_line()
    {
      // Only the applied views record. The verifier's revert renders its outcome and leaves the preview's line standing.
      var src = Actions;
      foreach (var apply in new[] { Method(src, "private static void RunSimplifyApply("), Method(src, "private static void ApplyToScript(") })
      {
        Assert.DoesNotContain("DwightSession.Complete", apply);
        Assert.DoesNotContain("CommandHistory.Record", apply);
        Assert.DoesNotContain("LedgerRun", apply);
      }
    }

    [Fact]
    public void Explain_writes_when_its_walkthrough_call_fails_and_its_row_writes_when_a_call_works()
    {
      var explain = Method(Actions, "private static void ExplainOn(");
      InOrder(explain, "if (!res.Ok)", "ctx.Spend = SpendOf(model, res.Usage, sw.Elapsed, chars: system.Length + user.Length);", Line, "if (DwightSession.IsCurrent(gen))",
        "DwightSession.Failed(ctx, \"error\", \"Couldn't build the walkthrough\", res.Error)", "return;", "var stepsView = WalkOrder.SortView(TranslateSteps(",
        "DwightSession.Complete(ctx, $\"{modeLabel} · {Count(guids.Count)}\", cost, script, null, spend)");
      Assert.Equal(2, Count(explain, "cost, script, null, spend)"));   // the walkthrough's row and the prose's
      // Brief DF changed these from 1 to 3: the failure's, and each completion's, which now writes its line before the currency check so a reply that
      // arrives after Stop still leaves the bill (its row stays absent). Brief DE made the first of the completions 2: it holds the Explain's spend BEFORE the
      // lesson's call (so a Stop during it still leaves the Explain's line) and the combined spend after, which is still the run's one line.
      Assert.Equal(4, Count(explain, "ctx.Spend = "));
      Assert.Equal(3, Count(explain, Line));
    }

    [Fact]
    public void Diagnose_writes_when_its_call_fails_and_its_row_writes_when_it_works()
    {
      var diagnose = Method(Actions, "private static void DiagnoseOn(");
      InOrder(diagnose, "if (!res.Ok)", "ctx.Spend = SpendOf(model, res.Usage, sw.Elapsed, chars: system.Length + user.Length);", Line, "if (DwightSession.IsCurrent(gen))",
        "popup.ShowState(\"error\", \"Diagnose hit a problem\", res.Error", "return;", "var faultsView = BuildFaultsView(",
        "DwightSession.Complete(ctx, subtitle, cost, script, null, spend)");
      // Brief DF changed these from 1 to 2: the failure's, and the completion's, which now writes its line before the currency check.
      Assert.Equal(2, Count(diagnose, "ctx.Spend = "));
      Assert.Equal(2, Count(diagnose, Line));
    }

    [Fact]
    public void A_chain_row_writes_when_its_call_fails_and_its_row_writes_when_it_works()
    {
      // No context of its own in the UI, so one is made for the line: the same writer, the same document and verb its row records.
      // Brief DF changed this: the context is made before the call (so a throw can fill it), the success path writes its line before the currency check,
      // and its row carries the flag, so a row that is recorded writes no second line.
      var chain = Method(Actions, "private static void WireChainPicker(");
      InOrder(chain, "var chainCtx = new DwightSession.Context { Action = \"Diagnose\" };", "StructuredOutput.Get(", "if (!res.Ok)",
        "chainCtx.Spend = SpendOf(model, res.Usage, sw.Elapsed, chars: system.Length + userC.Length);", "LedgerRun(chainCtx);",
        "if (DwightSession.IsCurrent(gen))", "wp.ShowState(\"error\", \"Diagnose hit a problem\", res.Error", "return;",
        "var spend = SpendOf(model, res.Usage, sw.Elapsed, chars: system.Length + userC.Length);", "chainCtx.Spend = spend;", "LedgerRun(chainCtx);", "if (!DwightSession.IsCurrent(gen)) return;",
        "CommandHistory.Record(Instances.ActiveCanvas?.Document, new DwightCommand", "Action = \"Diagnose\",", "Spend = spend,", "SpendLedgered = chainCtx.SpendLedgered,");
      Assert.DoesNotContain("ctx.SpendLedgered = ", chain);
    }

    [Fact]
    public void Reorganise_writes_when_the_labelling_returns_before_anything_can_stop_the_layout()
    {
      var reorganise = Method(Actions, "private static void RunReorganise(");
      InOrder(reorganise, "ClusterLabeller.Label(graph, macro, NewProvider(model), cancel)",
        "spend = SpendLedgerCore.KeptSpend(res.Ok, SpendOf(model, res.Usage, sw.Elapsed, chars: sent));", "ctx.Spend = spend;", "Ui(() => LedgerRun(ctx));",
        "ClusterLayout.Layout(lnodes, ledges", "if (!DwightSession.IsCurrent(gen)) { DwightSession.Done(gen); return; }",
        "ApplyReorganiseAndRender(ctx, gen, popup, subtitle, cost, spend, arrangeState");
      // Brief DL: 2, the second being the labelling's cancellation, which writes the estimate of a request that went out and was stopped.
      Assert.Equal(2, Count(reorganise, "LedgerRun(ctx)"));

      // The row still holds the spend, and writes no line of its own because the flag is on by then.
      var apply = Method(Actions, "private static void ApplyReorganiseAndRender(");
      Assert.Contains("DwightSession.Complete(ctx, subtitle, cost, script, live, spend)", apply);
      Assert.DoesNotContain("LedgerRun", apply);
    }

    [Fact]
    public void Five_sites_hold_a_spend_on_the_context_and_one_call_takes_the_line()
    {
      // Simplify, to-script, Explain's and Diagnose's failure, Reorganise's labelling. Nothing sets the flag except the one
      // call that writes the line, so a verb whose call worked has it off and keeps the line its row writes.
      var src = Actions;
      // Brief DF: the count moved from 5 to 12 as each completion and each carrier site gained its own assignment (see the per-verb tests, each with its reason),
      // and the chain row's row now copies the flag. What has not moved is that the ONLY writer of the flag is the one call, LedgerRun.
      Assert.Equal(14, Regex.Matches(src, @"[cC]tx\.Spend\s*=[^=]").Count);   // 13 at Brief DE; Brief DL's EstimateStopped sets the spend of a stopped request   // 12 at Brief DG; Brief DE's lesson holds the Explain's spend before its call
      Assert.Empty(Regex.Matches(src, @"\bctx\.SpendLedgered\s*=[^=]"));
      Assert.Equal(1, Count(src, "ref ctx.SpendLedgered"));
    }

    [Fact]
    public void A_fresh_generation_starts_with_no_spend_and_no_flag()
    {
      // The two paths that begin again: Retry and "Try a corrected proposal". Carrying either field across would
      // double the spend on the second line, or lose the second line altogether.
      var src = Actions;
      var mode = Method(src, "private static void SimplifyMode(");
      var apply = Method(src, "private static void RunSimplifyApply(");
      foreach (var path in new[] { mode, apply })
      {
        Assert.Contains("DwightSession.Begin(\"Simplify\", bound)", path);
        Assert.DoesNotContain("again.Spend", path);
        Assert.DoesNotContain("again.SpendLedgered", path);
      }
    }

    private static string RepoFile(params string[] parts)
    {
      var dir = AppContext.BaseDirectory;
      while (dir != null)
      {
        var candidate = Path.Combine(new[] { dir }.Concat(parts).ToArray());
        if (File.Exists(candidate)) return candidate;
        dir = Directory.GetParent(dir)?.FullName;
      }
      throw new FileNotFoundException(string.Join("/", parts) + " not found from " + AppContext.BaseDirectory);
    }
  }
}
