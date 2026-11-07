using System;
using System.Collections.Generic;
using System.Threading;
using Dwight.Labelling;
using Dwight.Llm;
using Dwight.Session;
using Xunit;

namespace Dwight.Labelling.Tests
{
  /// <summary>
  /// Brief DG: Reorganise's labelling carries its first turn's bill out of a repair turn that throws, so the run's ledger line holds it. The labeller is the
  /// fifth place <see cref="UsageCarrier"/> applies (Brief DF put it on <c>StructuredOutput.Get</c>, and the labeller calls that): this is the same theory DF's
  /// tests ran over Get, run over the labeller, counting the lines the verb's own two decisions would write.
  /// </summary>
  public class LabellerLedgerTests
  {
    private sealed class Provider : ILlmProvider
    {
      public int FailOnTurn;            // 0: never; 1 or 2: throw on that turn (a provider that answered nothing bills nothing)
      public bool InvalidFirst = true;  // turn 1 returns an unusable reply, so a repair turn follows
      public bool StopAfterTurn1;
      public CancellationTokenSource Cts;
      public int Turns;
      public string ModelId => "fake";
      public ProviderCapability Capability => ProviderCapability.ToolCall;

      public LlmResponse Send(LlmRequest request, CancellationToken cancel = default)
      {
        Turns++;
        if (FailOnTurn == Turns) throw new LlmException("the provider failed on turn " + Turns);
        var good = Turns > 1 || !InvalidFirst;
        var r = new LlmResponse
        {
          StructuredJson = good ? "{\"labels\":[{\"id\":0,\"label\":\"Roof\"},{\"id\":1,\"label\":\"Site\"}],\"arrangement\":[]}" : "{\"labels\":[]}",
          Usage = new LlmUsage { InputTokens = 1000 * Turns, OutputTokens = 100 * Turns },
        };
        if (Turns == 1 && StopAfterTurn1) Cts.Cancel();
        return r;
      }
    }

    private static LabelInput Input()
    {
      var clusters = new List<ClusterSummary>();
      for (var i = 0; i < 2; i++) { var c = new ClusterSummary { Id = i }; c.Types["Comp"] = 1; clusters.Add(c); }
      return new LabelInput(clusters, new List<(int, int)> { (0, 1) });
    }

    private static Dictionary<int, string> Hashes() => new Dictionary<int, string> { [0] = "hash-0", [1] = "hash-1" };

    private static StoredSpend Spend(LlmUsage u) =>
      new StoredSpend { Model = "fake", Provider = "Fake", InputTokens = u.InputTokens, OutputTokens = u.OutputTokens };

    /// <summary>What Reorganise does with the labelling: spend from the result, or from the exception the call threw, then the run's one line.</summary>
    private static (int lines, int input, string how) Caller(Provider p, CancellationTokenSource cts = null)
    {
      LabelCache.Clear();
      StoredSpend spend = null;
      var how = "completed";
      try
      {
        var r = ClusterLabellerCore.LabelFrom(Input(), Hashes(), p, 4000, cts?.Token ?? CancellationToken.None);
        spend = Spend(r.Usage);
      }
      catch (Exception ex)
      {
        how = ex.GetType().Name;
        if (UsageCarrier.TryTake(ex, out var paid)) spend = Spend(paid);
      }
      var ledgered = false;
      return (SpendLedgerCore.TakeRunLine(spend, ref ledgered) ? 1 : 0, spend?.InputTokens ?? 0, how);
    }

    [Fact]
    public void A_repair_turn_that_throws_leaves_one_line_holding_the_first_turns_tokens()
    {
      var r = Caller(new Provider { FailOnTurn = 2 });
      Assert.Equal("LlmException", r.how);   // the labelling still fails in the type it always did
      Assert.Equal(1, r.lines);
      Assert.Equal(1000, r.input);
    }

    [Fact]
    public void A_first_turn_that_throws_bills_nothing_and_leaves_no_line()
    {
      var r = Caller(new Provider { FailOnTurn = 1 });
      Assert.Equal(0, r.lines);
      Assert.Equal(0, r.input);
    }

    [Fact]
    public void A_labelling_that_never_fails_leaves_one_line_with_both_turns_in_it()
    {
      var r = Caller(new Provider());
      Assert.Equal("completed", r.how);
      Assert.Equal(1, r.lines);
      Assert.Equal(3000, r.input);
    }

    [Fact]
    public void A_stop_between_the_two_turns_leaves_the_first_turns_line()
    {
      var cts = new CancellationTokenSource();
      var p = new Provider { StopAfterTurn1 = true, Cts = cts };
      var r = Caller(p, cts);
      Assert.Equal("OperationCanceledException", r.how);
      Assert.Equal(1, p.Turns);
      Assert.Equal(1, r.lines);
      Assert.Equal(1000, r.input);
    }

    [Theory]
    [InlineData(0, false, false)]
    [InlineData(1, false, false)]
    [InlineData(2, false, false)]
    [InlineData(0, true, false)]
    [InlineData(1, true, false)]
    [InlineData(0, false, true)]
    public void Every_way_the_labelling_can_end_leaves_one_line_when_anything_was_billed_and_never_two(int failOnTurn, bool validFirst, bool stopAfterFirst)
    {
      var cts = new CancellationTokenSource();
      var p = new Provider { FailOnTurn = failOnTurn, InvalidFirst = !validFirst, StopAfterTurn1 = stopAfterFirst, Cts = cts };
      var r = Caller(p, cts);

      var billedTurns = Math.Max(0, p.Turns - (failOnTurn > 0 && failOnTurn <= p.Turns ? 1 : 0));
      Assert.True(r.lines <= 1);
      Assert.Equal(billedTurns > 0 ? 1 : 0, r.lines);
    }
  }
}
