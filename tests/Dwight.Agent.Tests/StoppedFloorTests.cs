using System;
using System.IO;
using System.Linq;
using Dwight.Data;
using Dwight.Models;
using Dwight.Session;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// Brief EG, the eighteenth gate's row 22: a scratch six-component Explain on Sonnet 5.5 sent 4,143 characters and was billed 1,713 cache-read tokens and 4 input, 1,717 in all. Stopped
  /// before the count came back, its estimate said 2,407, converted at the 1.72 characters per token of the Haiku 5.5 lines that filled the provider's one window, and stood 27 percent above
  /// the bill in cents. These run the real writer, reader and estimate on a sample shaped like that ledger (numbers only), with the catalogue's own rows.
  /// </summary>
  public class StoppedFloorTests
  {
    private const int Row22Chars = 4143;
    private const long Row22Bill = 1713 + 4;   // cache-read and input: the provider's own count, on the unmarked line row 22 wrote once it arrived
    private static readonly TimeSpan Elapsed = TimeSpan.FromSeconds(0.4);

    private static readonly ModelInfo[] Catalogue = ModelCatalogue.Parse(File.ReadAllText(Path.Combine(Repo(), "Dwight", "Resources", "models.json"))).ToArray();
    private static readonly ModelInfo Sonnet = Catalogue.Single(m => m.Id == "claude-sonnet-5-5");
    private static readonly ModelInfo Haiku = Catalogue.Single(m => m.Id == "claude-haiku-5-5");
    private static readonly ModelInfo Opus = Catalogue.Single(m => m.Id == "claude-opus-5-5");

    private static readonly double Row22Ratio = (double)Row22Chars / Row22Bill;   // 2.41
    private static readonly double HaikuRatio = 4197.0 / 2446;                    // 1.72, the gates' Haiku lines

    /// <summary>A verb line as the writer writes it.</summary>
    private static string Line(ModelInfo model, long chars, int input, int cacheRead = 0, int cacheWrite = 0, int output = 900) =>
      SpendLedgerCore.Serialize(SpendLedgerCore.ForVerb(
        new StoredSpend { Model = model.Id, InputTokens = input, CacheReadTokens = cacheRead, CacheWriteTokens = cacheWrite, OutputTokens = output, Cents = 1, Chars = chars },
        new DateTime(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc), "doc", "scratch.gh", DwightVerbs.Explain, model.Provider));

    private static string HaikuLine() => Line(Haiku, 4197, 4, cacheWrite: 2442);

    /// <summary>The day the gate read: five Sonnet 5.5 runs from 1.80 to row 22's 2.41, then a window's worth of Haiku 5.5 runs, so the provider's one window holds Haiku alone.</summary>
    private static LedgerSample TheDay() => SpendLedgerCore.Sample(new[]
    {
      Line(Sonnet, 3600, 4, cacheWrite: 1996),                   // 1.80
      Line(Sonnet, 3800, 4, cacheWrite: 1996),                   // 1.90
      Line(Sonnet, 4000, 4, cacheWrite: 1996),                   // 2.00
      Line(Sonnet, 4640, 4, cacheRead: 1996),                    // 2.32
      Line(Sonnet, Row22Chars, 4, cacheRead: 1713, output: 1),   // 2.41, row 22's own bill
    }.Concat(Enumerable.Range(0, LedgerSample.Window).Select(_ => HaikuLine())));

    [Fact]
    public void Each_model_keeps_its_own_ratio_and_its_floor_is_the_most_characters_per_token_it_measured()
    {
      var day = TheDay();
      Assert.Equal(Row22Ratio, day.FloorCharsPerToken(Sonnet.Provider, Sonnet.Id).Value, 9);
      Assert.Equal(2.0, day.CharsPerToken(Sonnet.Provider, Sonnet.Id).Value, 9);
      Assert.Equal(HaikuRatio, day.FloorCharsPerToken(Haiku.Provider, Haiku.Id).Value, 9);
      Assert.Equal(HaikuRatio, day.CharsPerToken(Haiku.Provider, Haiku.Id).Value, 9);
      // The provider's window is all Haiku: what row 22 was converted at, and what a model with no lines of its own still falls back to.
      Assert.Equal(HaikuRatio, day.CharsPerToken(Opus.Provider, Opus.Id).Value, 9);
    }

    [Fact]
    public void Under_three_lines_a_model_takes_its_providers_window_and_under_three_there_the_stated_default()
    {
      var twoOpus = SpendLedgerCore.Sample(new[] { HaikuLine(), HaikuLine(), HaikuLine(), Line(Opus, 6000, 4, cacheWrite: 1996), Line(Opus, 6000, 4, cacheWrite: 1996) });
      Assert.Equal(HaikuRatio, twoOpus.CharsPerToken(Opus.Provider, Opus.Id).Value, 9);   // the provider's five: their median is Haiku's
      Assert.Equal(3.0, twoOpus.FloorCharsPerToken(Opus.Provider, Opus.Id).Value, 9);     // and their most is Opus's own two

      var gpt = new ModelInfo("gpt-test", "GPT test", DwightConfig.ProviderOpenAI, "Mid", 1.25, 10.0, 400_000);
      Assert.Null(twoOpus.CharsPerToken(gpt.Provider, gpt.Id));
      Assert.Null(twoOpus.FloorCharsPerToken(gpt.Provider, gpt.Id));
      var stated = SelectionSerializer.EstimateTokens(7000, SelectionSerializer.DefaultCharsPerToken(gpt.Provider));
      Assert.Equal(stated, LargeRun.FloorInputTokens(gpt, 7000, twoOpus));
      Assert.Equal(stated, StoppedEstimate.Of(gpt, 7000, Elapsed, twoOpus).InputTokens);

      var twoSonnet = SpendLedgerCore.Sample(new[] { Line(Sonnet, 4800, 4, cacheWrite: 1996), Line(Sonnet, 4800, 4, cacheWrite: 1996) });
      Assert.Null(twoSonnet.FloorCharsPerToken(Sonnet.Provider, Sonnet.Id));
      Assert.Equal(SelectionSerializer.EstimateTokens(Row22Chars, SelectionSerializer.DefaultCharsPerToken(Sonnet.Provider)),
        StoppedEstimate.Of(Sonnet, Row22Chars, Elapsed, twoSonnet).InputTokens);
    }

    [Fact]
    public void Row_22_stopped_is_at_or_under_its_bill()
    {
      var e = StoppedEstimate.Of(Sonnet, Row22Chars, Elapsed, TheDay());
      // 4,143 / 2.41, rounded up as EstimateTokens rounds: 1,718 at most, against the bill's 1,717.
      Assert.True(e.InputTokens <= 1718, e.InputTokens + " tokens estimated against a bill of " + Row22Bill);
      var bill = Sonnet.CostCents(4, 1, 1713, 0);
      Assert.True(e.Cents.Value <= bill, e.Cents.Value + " cents estimated against a bill of " + bill);

      // The defect, for the record: at the provider's window the same request read 2,415 tokens, 40 percent over the bill's tokens (27 percent over its cents, with the input and output it left out).
      Assert.True(SelectionSerializer.EstimateTokens(Row22Chars, HaikuRatio) > Row22Bill * 1.25);
    }

    /// <summary>"Continue?" estimates the likely bill, so it keeps the median: the floor is for a number the page calls one.</summary>
    [Fact]
    public void The_question_before_a_read_keeps_the_median()
    {
      var day = TheDay();
      const int chars = Row22Chars * 1000;   // row 22 a thousand times over, so the cents can tell the median from the floor
      var median = SelectionSerializer.EstimateTokens(chars, 2.0);
      Assert.Equal(median, LargeRun.InputTokens(Sonnet, chars, day));
      Assert.True(LargeRun.FloorInputTokens(Sonnet, chars, day) < median);

      var answer = Sonnet.CostCents(0, LargeRun.OutputTokens(Sonnet, DwightVerbs.Explain, day), 0, 0);
      var cents = LargeRun.EstimateCents(Sonnet, median) + answer;
      Assert.Equal(cents, LargeRun.Estimate(Sonnet, chars, DwightVerbs.Explain, day), 9);
      Assert.Equal(LargeRun.Sentence(6, chars, LargeRun.CostText(cents), Sonnet.DisplayName, LargeRun.ForReadAndAnswer),
        LargeRun.Sentence(6, chars, Sonnet, DwightVerbs.Explain, day));

      // A threshold above what the floor would say and at or under the median's: it asks.
      var atFloor = LargeRun.EstimateCents(Sonnet, LargeRun.FloorInputTokens(Sonnet, chars, day)) + answer;
      var threshold = (int)Math.Floor(atFloor) + 1;
      Assert.InRange(threshold, 1, (int)Math.Floor(cents));
      Assert.True(LargeRun.IsOver(Sonnet, chars, threshold, DwightVerbs.Explain, day));
    }

    // ---- Brief EG's addendum: a run stopped after the provider's count arrived teaches the ratio ----------------------------------------------------------------

    private static StoredSpend Counted() => new StoredSpend { Model = Sonnet.Id, Provider = Sonnet.Provider, InputTokens = 4, CacheReadTokens = 1713, OutputTokens = 1, Cents = Sonnet.CostCents(4, 1, 1713, 0) };

    private static string VerbLineOf(StoredSpend s) =>
      SpendLedgerCore.Serialize(SpendLedgerCore.ForVerb(s, new DateTime(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc), "doc", "scratch.gh", DwightVerbs.Explain, Sonnet.Provider));

    /// <summary>The eighteenth's own 2.41 line was one of these: written from the provider's usage with no characters, so the window never saw it. Carried with the request in flight, it does.</summary>
    [Fact]
    public void A_run_stopped_after_the_providers_count_carries_its_characters_and_enters_the_sample()
    {
      var carried = SpendMath.Carried(null, Counted(), Row22Chars);
      Assert.Equal(Row22Chars, carried.Chars);
      Assert.Null(carried.Estimated);                                       // the provider's own count, never an estimate
      var line = VerbLineOf(carried);
      Assert.Contains("\"chars\":4143", line);
      var sample = SpendLedgerCore.Sample(new[] { line, line, line });
      Assert.Equal(Row22Ratio, sample.FloorCharsPerToken(Sonnet.Provider, Sonnet.Id).Value, 9);
      Assert.Equal(Row22Ratio, sample.CharsPerToken(Sonnet.Provider, Sonnet.Id).Value, 9);

      // Nothing marked in flight: no characters, so the line teaches nothing, as before.
      var unmarked = SpendMath.Carried(null, Counted(), 0);
      Assert.Equal(0, unmarked.Chars);
      Assert.Null(SpendLedgerCore.Sample(new[] { VerbLineOf(unmarked), VerbLineOf(unmarked), VerbLineOf(unmarked) }).FloorCharsPerToken(Sonnet.Provider, Sonnet.Id));
    }

    [Fact]
    public void A_carried_bill_joins_the_run_as_a_finished_attempt_does_and_half_a_run_is_never_measured()
    {
      var first = new StoredSpend { Model = Sonnet.Id, Provider = Sonnet.Provider, InputTokens = 2000, OutputTokens = 300, Cents = 1, Chars = 4000 };
      var both = SpendMath.Carried(first, Counted(), Row22Chars);
      Assert.Equal(4000 + Row22Chars, both.Chars);                          // two requests, both counted, as Simplify's attempts are
      Assert.Equal(2000 + 1717, both.TotalInputTokens);
      // A run whose earlier call had no characters (Learn's Explain, then its lesson): the line has none, as a finished one would.
      Assert.Equal(0, SpendMath.Carried(new StoredSpend { Model = Sonnet.Id, InputTokens = 2000, Cents = 1 }, Counted(), Row22Chars).Chars);
      Assert.Same(first, SpendMath.Carried(first, null, Row22Chars));        // a call that billed nothing changes nothing
    }

    [Fact]
    public void Every_carried_bill_goes_through_the_one_door_with_the_request_in_flight()
    {
      var src = File.ReadAllText(Path.Combine(Repo(), "Dwight", "LlmActions.cs")).Replace("\r\n", "\n");
      Assert.Contains("ctx.Spend = SpendMath.Carried(ctx.Spend, SpendOf(model, paid, sw.Elapsed), ctx.SentModel != null ? ctx.SentChars : 0);", src);
      Assert.DoesNotContain("SpendMath.Combine(ctx.Spend, SpendOf(model, paid, sw.Elapsed))", src);
      // to-script keeps its own refine loop's bill: on a throw, the characters its success path gives.
      Assert.Contains("ctx.Spend = SpendOf(model, spent, elapsed + sw.Elapsed, chars: attempt == 1 ? system.Length + message.Length : 0);", src);
      Assert.DoesNotContain("ctx.Spend = SpendOf(model, spent, elapsed + sw.Elapsed);", src);
      // The mark is set before the send and cleared by the reply alone, so a catch reads the request that was in flight; Learn's lesson follows a cleared mark.
      Assert.Contains("private static void ClearSent(DwightSession.Context ctx) { if (ctx != null) ctx.SentModel = null; }", src);
      var walk = src.IndexOf("Prompts.Prompts.ExplainStepsSchema, ValidateSteps", StringComparison.Ordinal);
      Assert.True(src.IndexOf("ClearSent(ctx);", walk, StringComparison.Ordinal) < src.IndexOf("TeachingCall.Ask(", walk, StringComparison.Ordinal));
    }

    private static string Repo()
    {
      var dir = new DirectoryInfo(AppContext.BaseDirectory);
      while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Dwight.sln"))) dir = dir.Parent;
      return dir?.FullName ?? throw new InvalidOperationException("no repository above the test binaries");
    }
  }
}
