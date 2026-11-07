using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Dwight.Llm;
using Dwight.Session;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// Brief DF: a billed call always leaves its one ledger line. Brief CT closed "a billed call that then fails" for the four verbs; three states it did not
  /// reach are held here: a reply that arrives after Stop, a billed refusal in Explain's prose path, and a repair turn that throws. The first half runs
  /// <see cref="StructuredOutput.Get"/> with a fake provider through every way it can end and counts the lines the caller's own two decisions would write, so
  /// a fourth gap cannot appear without turning it red. LlmActions needs Grasshopper and cannot load here, so the second half pins by text that it takes the
  /// decisions at the places the states are reached (the same split as <c>PreviewSpendTests</c>).
  /// </summary>
  public class LedgerGapsTests
  {
    // ---- a provider that bills each turn and fails on a chosen one --------------------------------------------------------------------

    private sealed class Provider : ILlmProvider
    {
      public int FailOnTurn;                    // 0: never. 1 or 2: throw on that turn (the provider answers nothing, so nothing is billed)
      public bool InvalidFirst = true;          // turn 1 returns a reply that fails validation, so a repair turn follows
      public bool StopAfterTurn1;               // the token fires while the first reply is arriving
      public CancellationTokenSource Cts;
      public int Turns;
      public string ModelId => "fake";
      public ProviderCapability Capability => default(ProviderCapability);

      public LlmResponse Send(LlmRequest request, CancellationToken cancel = default)
      {
        Turns++;
        if (FailOnTurn == Turns) throw new LlmException("the provider failed on turn " + Turns);
        var good = Turns > 1 || !InvalidFirst;
        var r = new LlmResponse { StructuredJson = good ? "{\"ok\":true}" : "{\"ok\":false}", Usage = new LlmUsage { InputTokens = 1000 * Turns, OutputTokens = 100 * Turns } };
        if (Turns == 1 && StopAfterTurn1) Cts.Cancel();
        return r;
      }
    }

    private static (bool ok, string error) Validate(string json) => json.Contains("true") ? (true, null) : (false, "not ok");

    /// <summary>What a verb's completion does with a call: record the usage on the run's spend from the result OR from the exception it threw, then write the
    /// run's one line if there is usage and none was written. Returns how many lines were written, and the tokens the spend held.</summary>
    private static (int lines, int input, bool threw, string how) Caller(Provider p, CancellationTokenSource cts = null)
    {
      StoredSpend spend = null;
      var threw = false; var how = "completed";
      try
      {
        var res = StructuredOutput.Get(p, "s", "u", "tool", "d", "{}", Validate, 1000, cts?.Token ?? CancellationToken.None);
        spend = Spend(res.Usage);
      }
      catch (Exception ex)
      {
        threw = true; how = ex.GetType().Name;
        if (UsageCarrier.TryTake(ex, out var paid)) spend = Spend(paid);
      }
      var ledgered = false;
      var lines = SpendLedgerCore.TakeRunLine(spend, ref ledgered) ? 1 : 0;
      return (lines, spend?.InputTokens ?? 0, threw, how);
    }

    private static StoredSpend Spend(LlmUsage u) =>
      new StoredSpend { Model = "fake", Provider = "Fake", InputTokens = u.InputTokens, OutputTokens = u.OutputTokens, CacheReadTokens = u.CacheReadInputTokens, CacheWriteTokens = u.CacheCreationInputTokens };

    // ---- gap 3: a repair turn that throws ---------------------------------------------------------------------------------------------

    [Fact]
    public void A_repair_turn_that_throws_leaves_one_line_holding_the_first_turns_tokens()
    {
      var r = Caller(new Provider { FailOnTurn = 2 });
      Assert.True(r.threw);
      Assert.Equal("LlmException", r.how);   // the exception's type is unchanged, so every existing catch still catches it
      Assert.Equal(1, r.lines);
      Assert.Equal(1000, r.input);           // turn 1's input tokens, which the old local lost
    }

    [Fact]
    public void A_first_turn_that_throws_bills_nothing_and_leaves_no_line()
    {
      var r = Caller(new Provider { FailOnTurn = 1 });
      Assert.True(r.threw);
      Assert.Equal(0, r.lines);
      Assert.Equal(0, r.input);
    }

    [Fact]
    public void A_call_that_never_fails_leaves_exactly_one_line_with_both_turns_in_it()
    {
      var r = Caller(new Provider());
      Assert.False(r.threw);
      Assert.Equal(1, r.lines);
      Assert.Equal(3000, r.input);           // 1000 + 2000
    }

    [Fact]
    public void A_call_whose_first_reply_is_valid_makes_one_turn_and_one_line()
    {
      var p = new Provider { InvalidFirst = false };
      var r = Caller(p);
      Assert.Equal(1, p.Turns);
      Assert.Equal(1, r.lines);
      Assert.Equal(1000, r.input);
    }

    [Fact]
    public void A_stop_that_lands_between_the_two_turns_leaves_the_first_turns_line()
    {
      var cts = new CancellationTokenSource();
      var p = new Provider { StopAfterTurn1 = true, Cts = cts };
      var r = Caller(p, cts);
      Assert.True(r.threw);
      Assert.Equal("OperationCanceledException", r.how);
      Assert.Equal(1, p.Turns);              // the repair turn was never sent
      Assert.Equal(1, r.lines);
      Assert.Equal(1000, r.input);
    }

    /// <summary>The fourth gap cannot appear unseen: every way the call can end, with every turn the failure can fall on, leaves at most one line, and exactly one
    /// whenever any turn was billed.</summary>
    [Theory]
    [InlineData(0, false, false)]
    [InlineData(1, false, false)]
    [InlineData(2, false, false)]
    [InlineData(0, true, false)]
    [InlineData(1, true, false)]
    [InlineData(0, false, true)]
    public void Every_way_the_call_can_end_leaves_one_line_when_anything_was_billed_and_never_two(int failOnTurn, bool validFirst, bool stopAfterFirst)
    {
      var cts = new CancellationTokenSource();
      var p = new Provider { FailOnTurn = failOnTurn, InvalidFirst = !validFirst, StopAfterTurn1 = stopAfterFirst, Cts = cts };
      var r = Caller(p, cts);

      var billedTurns = Math.Max(0, p.Turns - (failOnTurn > 0 && failOnTurn <= p.Turns ? 1 : 0));
      Assert.True(r.lines <= 1);
      Assert.Equal(billedTurns > 0 ? 1 : 0, r.lines);
    }

    [Fact]
    public void An_exception_that_carries_no_usage_says_so()
    {
      Assert.False(UsageCarrier.TryTake(new LlmException("x"), out _));
      Assert.False(UsageCarrier.TryTake(null, out _));
    }

    [Fact]
    public void Nothing_is_attached_for_a_call_that_billed_nothing()
    {
      var ex = new LlmException("x");
      UsageCarrier.Attach(ex, new LlmUsage());
      UsageCarrier.Attach(ex, null);
      Assert.False(UsageCarrier.TryTake(ex, out _));
    }

    // ---- gap 2: a billed refusal, as Explain's prose path throws it --------------------------------------------------------------------

    [Fact]
    public void A_billed_refusal_keeps_its_usage_on_the_exception_and_an_unbilled_one_has_none()
    {
      var billed = new LlmException("refused");
      UsageCarrier.Attach(billed, new LlmUsage { InputTokens = 800, OutputTokens = 40 });
      Assert.True(UsageCarrier.TryTake(billed, out var u));
      Assert.Equal(800, u.InputTokens);

      var spend = Spend(u);
      var ledgered = false;
      Assert.True(SpendLedgerCore.TakeRunLine(spend, ref ledgered));
      Assert.False(SpendLedgerCore.TakeRunLine(spend, ref ledgered));   // and a row that follows writes no second line
    }

    // ---- the places the states are reached (LlmActions cannot load here, so it is read) -------------------------------------------------

    private static string Source(params string[] parts)
    {
      var dir = new DirectoryInfo(AppContext.BaseDirectory);
      while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Dwight.sln"))) dir = dir.Parent;
      Assert.True(dir != null);
      return File.ReadAllText(Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray())).Replace("\r\n", "\n");
    }

    private static readonly string Actions = Source("Dwight", "LlmActions.cs");

    [Fact]
    public void No_completion_lambda_checks_the_currency_before_it_writes_the_line()
    {
      // The old shape, in all four completions: the early return came first and the bill was lost with it.
      Assert.DoesNotContain("Ui(() => { if (!DwightSession.IsCurrent(gen)) return; DwightSession.Render(popup, script, null);", Actions);
      Assert.DoesNotContain("Ui(() =>\n          {\n            if (!DwightSession.IsCurrent(gen)) return;\n            DwightSession.Render(popup, script, null);", Actions);
      Assert.DoesNotContain("Ui(() =>\n              {\n                if (!DwightSession.IsCurrent(gen)) return;\n                var prim = chainPrimary(i);", Actions);
    }

    [Fact]
    public void Each_of_the_four_completions_writes_its_line_before_it_tests_the_currency()
    {
      // Explain on canvas and Explain prose
      // Brief DE: the walk's completion sets the lesson on the panel between the currency check and the render, so its text differs; the order the test is about does not.
      Assert.Equal(1, Regex.Matches(Actions, Regex.Escape("Ui(() => { LedgerRun(ctx); if (!DwightSession.IsCurrent(gen)) return; DwightSession.Render(popup, script, null);")).Count);
      Assert.Equal(1, Regex.Matches(Actions, Regex.Escape("Ui(() => { LedgerRun(ctx); if (!DwightSession.IsCurrent(gen)) return; (popup as WebPanel)?.SetTeaching(")).Count);
      // Diagnose
      Assert.Contains("LedgerRun(ctx);   // Brief DF: the line comes before the currency check", Actions);
      // the chain row, whose row then writes none
      Assert.Contains("LedgerRun(chainCtx);   // Brief DF: the line before the currency check; the row below carries SpendLedgered so it writes none", Actions);
      Assert.Contains("SpendLedgered = chainCtx.SpendLedgered,", Actions);
    }

    [Fact]
    public void A_chain_row_that_is_stopped_or_throws_still_writes_its_line()
    {
      // The chain row has no Run wrapper to catch for it, so its own two catches write the line the bill is owed.
      Assert.Contains("catch (OperationCanceledException) { Ui(() => { chainCtx.Doc = Instances.ActiveCanvas?.Document; EstimateStopped(chainCtx); LedgerRun(chainCtx); });", Actions);   // Brief DL: a stopped request that went out is estimated first
      Assert.Contains("catch (Exception ex) { Ui(() => { chainCtx.Doc = Instances.ActiveCanvas?.Document; LedgerRun(chainCtx); if (DwightSession.IsCurrent(gen))", Actions);
    }

    [Fact]
    public void Every_paid_call_in_the_four_verbs_carries_its_bill_out_of_a_throw()
    {
      Assert.Equal(5, Regex.Matches(Actions, "StructuredOutput\\.Get\\(").Count);
      // Explain steps, Diagnose, Simplify and the chain row read the carrier through CarryPaid; to-script reads it inline (its spend is cumulative); the prose call too.
      // Brief DG: six, the sixth being Reorganise's labelling (minus the definition). Brief DE: seven, the seventh being a Stop between the lesson's turns.
      Assert.Equal(7, Regex.Matches(Actions, "CarryPaid\\(").Count - 1);
      Assert.Contains("if (UsageCarrier.TryTake(ex, out var paid)) { Accumulate(spent, paid);", Actions);
    }

    [Fact]
    public void The_prose_path_attaches_a_billed_refusals_usage_before_it_throws()
    {
      Assert.Contains("if (LlmRefusal.IsBilled(response)) UsageCarrier.Attach(refusedWith, response.Usage);".Replace("refusedWith", "refused"), Actions);
      Assert.Contains("catch (Exception ex) { CarryPaid(ctx, model, ex, sw); throw; }   // Brief DF: a billed refusal keeps its line", Actions);
    }

    [Fact]
    public void Get_attaches_the_usage_so_far_to_whatever_it_throws()
    {
      var so = Source("Dwight", "Llm", "StructuredOutput.cs");
      Assert.Contains("UsageCarrier.Attach(ex, result.Usage);", so);
      Assert.Contains("throw;", so);
    }
  }
}
