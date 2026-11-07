using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Dwight.Llm;
using Dwight.Models;
using Dwight.Session;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// The sixteenth's gate: a chat turn stopped by the person left no ledger line and no estimate line, on Gemini and on Sonnet 5.5. Stop bumps the generation before the turn returns, so
  /// the completion always took its superseded branch and the stopped block further down was dead code, with every unit either side of it green. So these run the real driver and the
  /// real ledger through the one door both branches now share, and read the branch itself from the source, since its seam needs Rhino (ADR-091).
  /// </summary>
  public class StoppedTurnSpendTests
  {
    private const string Doc = "66fd7275-7717-4065-90eb-483736e9296a";
    private static readonly ModelInfo Sonnet = new ModelInfo("claude-sonnet-5", "Sonnet 5", "Anthropic", "Mid", 3.0, 15.0, 1_000_000);
    private const string Done = "{\"ok\":true,\"result\":{}}";

    /// <summary>A provider that plays one script per step. The test's token is the person's Stop.</summary>
    private sealed class Scripted : ILlmProvider
    {
      public readonly Queue<Func<CancellationToken, LlmResponse>> Steps = new Queue<Func<CancellationToken, LlmResponse>>();
      public int Sent;
      public string ModelId => "scripted";
      public ProviderCapability Capability => default(ProviderCapability);
      public LlmResponse Send(LlmRequest request, CancellationToken cancel = default) { Sent++; return Steps.Dequeue()(cancel); }
    }

    private static LlmUsage Billed() => new LlmUsage { InputTokens = 1200, OutputTokens = 300, CacheReadInputTokens = 18000, CacheCreationInputTokens = 200 };
    private static LlmResponse ToolStep() => new LlmResponse { StopReason = "tool_use", Blocks = { LlmBlock.ToolUse("t1", AgentToolCatalog.GetOverview, "{}") }, Usage = Billed() };
    private static LlmResponse LastStep() => new LlmResponse { StopReason = "end_turn", Blocks = { LlmBlock.TextBlock("A plan.") }, Usage = Billed() };

    /// <summary>The request goes out and the Stop lands before any of it comes back.</summary>
    private static Func<CancellationToken, LlmResponse> CutBeforeAnyCount(CancellationTokenSource stop) => cancel =>
    {
      stop.Cancel();
      cancel.ThrowIfCancellationRequested();
      throw new InvalidOperationException("unreachable: the token was cancelled");
    };

    /// <summary>The stream said the provider's count, and then the Stop landed, the way the real readers carry it.</summary>
    private static Func<CancellationToken, LlmResponse> CutAfterACount(CancellationTokenSource stop) => cancel =>
    {
      stop.Cancel();
      var cut = new OperationCanceledException(cancel);
      UsageCarrier.Attach(cut, new LlmUsage { InputTokens = 40, CacheReadInputTokens = 19000, CacheCreationInputTokens = 100, OutputTokens = 2 });
      throw cut;
    };

    private static TurnResult Turn(Scripted provider, CancellationToken stop, Func<LlmBlock, string> execute = null, LlmMessage message = null) =>
      AgentTurnDriver.Drive(new TurnDrive
      {
        Provider = provider,
        System = "system",
        Messages = new List<LlmMessage> { message ?? LlmMessage.User("Add a turbine spacing check.") },
        Tools = AgentToolCatalog.All().Select(t => new LlmTool { Name = t.Name, Description = t.Description, InputSchemaJson = t.InputSchemaJson }).ToList(),
        Limits = new TurnLimits { MaxSteps = 4, WallClockMs = 0, MaxTranscriptChars = 1_000_000, AnswerBudget = 1000 },
        Execute = execute ?? (_ => Done),
        Cancel = stop,
      }, Stopwatch.StartNew());

    /// <summary>What <c>LlmActions.SpendOf</c> makes of a turn's usage, which the door takes as given.</summary>
    private static StoredSpend Bill(TurnResult r, ModelInfo model, TimeSpan elapsed)
    {
      var u = r.Usage;
      if (u == null || (u.TotalInputTokens == 0 && u.OutputTokens == 0)) return null;
      return new StoredSpend
      {
        Model = model.Id, Provider = model.Provider, ElapsedMs = (long)elapsed.TotalMilliseconds, InputTokens = u.InputTokens, OutputTokens = u.OutputTokens,
        CacheReadTokens = u.CacheReadInputTokens, CacheWriteTokens = u.CacheCreationInputTokens, Steps = r.Steps, ToolCalls = r.ToolCalls,
        Cents = model.CostCents(u.InputTokens, u.OutputTokens, u.CacheReadInputTokens, u.CacheCreationInputTokens),
      };
    }

    private static readonly TimeSpan Elapsed = TimeSpan.FromSeconds(3);
    private static StoredSpend Spent(TurnResult r, ModelInfo model = null) => TurnSpend.Of(Bill(r, model ?? Sonnet, Elapsed), model ?? Sonnet, r, Elapsed, null);

    private sealed class Ledger : IDisposable
    {
      private readonly string _dir = Path.Combine(Path.GetTempPath(), "dwight-stopped-turn-" + Guid.NewGuid().ToString("N"));
      private string Path_ => Path.Combine(_dir, SpendLedgerCore.FileName);
      public void Write(StoredSpend s) { if (s != null) SpendLedgerCore.Append(Path_, SpendLedgerCore.ForTurn(s, DateTime.UtcNow, Doc, "tower.gh", DwightVerbs.Build, s.Provider, "t1", 1)); }
      public LedgerLine[] Lines => File.Exists(Path_) ? SpendLedgerCore.ReadLines(Path_).Select(SpendLedgerCore.Parse).ToArray() : new LedgerLine[0];
      public LedgerTotal Total => SpendLedgerCore.Summarise(SpendLedgerCore.ReadLines(Path_), DateTime.Now).Total(LedgerPeriod.AllTime);
      public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }
    }

    // ---- the four ways a stopped turn can stand ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Stopped_with_its_request_out_and_no_count_it_leaves_one_estimated_line_and_says_it_is_an_estimate()
    {
      var stop = new CancellationTokenSource();
      var provider = new Scripted();
      provider.Steps.Enqueue(CutBeforeAnyCount(stop));

      var result = Turn(provider, stop.Token);
      Assert.Equal(TurnOutcome.Stopped, result.Outcome);
      var spent = Spent(result);

      using (var ledger = new Ledger())
      {
        ledger.Write(spent);
        var line = Assert.Single(ledger.Lines);
        Assert.Equal(LedgerLine.KindTurn, line.Kind);
        Assert.Equal("stopped", line.Estimated);
        Assert.Equal(0, line.OutputTokens);
        Assert.True(line.InputTokens > 0);
        Assert.True(line.Cents > 0);
        Assert.Equal(1, ledger.Total.EstimatedRows);
        Assert.Equal(0, ledger.Total.BilledRows);
      }
      Assert.Equal(Sonnet.CostCents(0, 0, spent.InputTokens, 0), spent.Cents.Value, 9);   // the door prices it at the cache's rate, the least rate it can have cost
      // Brief EG: no "floor". A chat turn's ratio is its model's verb runs', and no chat request is measured, so the plugin cannot prove one.
      Assert.Equal("The provider's count for the stopped request never reached Dwight, so this turn is on the ledger as an estimate from the request's size, " + SpendMath.CentsText(spent.Cents.Value) + ".",
        TurnSpend.StoppedLine(spent, result));
    }

    [Fact]
    public void Stopped_after_its_stream_said_the_count_it_leaves_that_count_unmarked_and_says_what_the_count_leaves_out()
    {
      var stop = new CancellationTokenSource();
      var provider = new Scripted();
      provider.Steps.Enqueue(CutAfterACount(stop));

      var result = Turn(provider, stop.Token);
      Assert.Equal(TurnOutcome.Stopped, result.Outcome);
      Assert.True(result.InFlightCounted);
      Assert.Equal(0, result.InFlightChars);   // nothing to estimate: the provider said
      var spent = Spent(result);

      using (var ledger = new Ledger())
      {
        ledger.Write(spent);
        var line = Assert.Single(ledger.Lines);
        Assert.Null(line.Estimated);
        Assert.Equal(19000, line.CacheReadTokens);
        Assert.Equal(40, line.InputTokens);
      }
      var words = TurnSpend.StoppedLine(spent, result);
      Assert.Contains("at what the provider had counted, " + SpendMath.CentsText(spent.Cents.Value), words);
      Assert.Contains("What the model wrote before the stop is not in that count, so it is a floor.", words);
    }

    [Fact]
    public void Stopped_between_steps_it_holds_the_whole_bill_so_one_unmarked_line_and_no_words()
    {
      var stop = new CancellationTokenSource();
      var provider = new Scripted();
      provider.Steps.Enqueue(_ => ToolStep());

      var result = Turn(provider, stop.Token, _ => { stop.Cancel(); return Done; });
      Assert.Equal(TurnOutcome.Stopped, result.Outcome);
      Assert.Equal(0, result.InFlightChars);
      Assert.False(result.InFlightCounted);
      var spent = Spent(result);

      using (var ledger = new Ledger())
      {
        ledger.Write(spent);
        var line = Assert.Single(ledger.Lines);
        Assert.Null(line.Estimated);
        Assert.Equal(300, line.OutputTokens);
      }
      Assert.Null(TurnSpend.StoppedLine(spent, result));   // "floor" would be false: this is the bill
    }

    [Fact]
    public void Stopped_before_anything_went_out_it_writes_nothing_and_says_nothing()
    {
      var stop = new CancellationTokenSource();
      stop.Cancel();
      var provider = new Scripted();

      var result = Turn(provider, stop.Token);
      Assert.Equal(TurnOutcome.Stopped, result.Outcome);
      Assert.Equal(0, provider.Sent);
      var spent = Spent(result);

      Assert.Null(spent);
      Assert.Null(TurnSpend.StoppedLine(spent, result));
      using (var ledger = new Ledger()) { ledger.Write(spent); Assert.Empty(ledger.Lines); }
    }

    // ---- one line, never two -----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_bill_and_an_estimate_are_one_line_with_the_turns_own_clock_and_counts()
    {
      var stop = new CancellationTokenSource();
      var provider = new Scripted();
      provider.Steps.Enqueue(_ => ToolStep());
      provider.Steps.Enqueue(CutBeforeAnyCount(stop));

      var result = Turn(provider, stop.Token);
      Assert.Equal(1, result.Steps);
      Assert.Equal(1, result.ToolCalls);
      var bill = Bill(result, Sonnet, Elapsed);
      var spent = Spent(result);

      Assert.Equal("stopped", spent.Estimated);                 // a total with an estimated part is an estimate, said so
      Assert.Equal(1, spent.Steps);                             // not two
      Assert.Equal(1, spent.ToolCalls);
      Assert.Equal((long)Elapsed.TotalMilliseconds, spent.ElapsedMs);
      Assert.True(spent.InputTokens > bill.InputTokens);
      Assert.True(spent.Cents > bill.Cents);
      using (var ledger = new Ledger()) { ledger.Write(spent); Assert.Single(ledger.Lines); }
    }

    /// <summary>A step that reported no usage is still a step: with no bill to add to, the estimate alone carries the turn's counts.</summary>
    [Fact]
    public void An_estimate_with_no_bill_still_carries_the_turns_steps_and_tool_calls()
    {
      var stop = new CancellationTokenSource();
      var provider = new Scripted();
      provider.Steps.Enqueue(_ => new LlmResponse { StopReason = "tool_use", Blocks = { LlmBlock.ToolUse("t1", AgentToolCatalog.GetOverview, "{}") } });
      provider.Steps.Enqueue(CutBeforeAnyCount(stop));

      var result = Turn(provider, stop.Token);
      Assert.Null(Bill(result, Sonnet, Elapsed));
      var spent = Spent(result);
      Assert.Equal("stopped", spent.Estimated);
      Assert.Equal(1, spent.Steps);
      Assert.Equal(1, spent.ToolCalls);
    }

    [Fact]
    public void A_finished_turn_is_its_bill_alone_with_no_words()
    {
      var provider = new Scripted();
      provider.Steps.Enqueue(_ => LastStep());
      var result = Turn(provider, CancellationToken.None);
      Assert.Equal(TurnOutcome.Complete, result.Outcome);

      var bill = Bill(result, Sonnet, Elapsed);
      Assert.Same(bill, TurnSpend.Of(bill, Sonnet, result, Elapsed, null));
      Assert.Null(TurnSpend.StoppedLine(bill, result));

      // The in-flight fields speak only for a stopped turn, whatever they hold.
      var stale = new TurnResult { Outcome = TurnOutcome.Complete, InFlightChars = 12_000, InFlightCounted = true };
      Assert.Same(bill, TurnSpend.Of(bill, Sonnet, stale, Elapsed, null));
      Assert.Null(TurnSpend.StoppedLine(bill, stale));
    }

    /// <summary>The race: the provider finished and the Stop landed before the completion ran. The turn is Complete, on the superseded branch, and it is the bill, once, with no estimate on top.</summary>
    [Fact]
    public void A_stop_that_lands_after_the_provider_finished_is_the_whole_bill_once_with_no_estimate()
    {
      var stop = new CancellationTokenSource();
      var provider = new Scripted();
      provider.Steps.Enqueue(_ => { stop.Cancel(); return LastStep(); });

      var result = Turn(provider, stop.Token);
      Assert.Equal(TurnOutcome.Complete, result.Outcome);
      Assert.Equal(0, result.InFlightChars);
      var spent = Spent(result);

      using (var ledger = new Ledger())
      {
        ledger.Write(spent);
        var line = Assert.Single(ledger.Lines);
        Assert.Null(line.Estimated);
        Assert.Equal(1200, line.InputTokens);
        Assert.Equal(300, line.OutputTokens);
      }
      Assert.Null(TurnSpend.StoppedLine(spent, result));
    }

    // ---- an image in the stopped request -----------------------------------------------------------------------------------------------------------

    private static LlmMessage WithImages(int images, bool ruleKnown)
    {
      var m = new LlmMessage { Role = LlmRole.User };
      for (var i = 0; i < images; i++) m.Blocks.Add(LlmBlock.Image(ChatImage.Png, "AAAA", 2019, ruleKnown));
      m.Blocks.Add(LlmBlock.TextBlock("like this"));
      return m;
    }

    private static TurnResult StoppedWith(LlmMessage message)
    {
      var stop = new CancellationTokenSource();
      var provider = new Scripted();
      provider.Steps.Enqueue(CutBeforeAnyCount(stop));
      return Turn(provider, stop.Token, null, message);
    }

    [Theory]
    [InlineData(1, "It leaves out the image, which this model's provider counts itself.")]
    [InlineData(2, "It leaves out the 2 images, which this model's provider counts itself.")]
    public void An_image_on_a_row_with_no_rule_is_left_out_of_the_estimate_and_said(int images, string says)
    {
      var result = StoppedWith(WithImages(images, ruleKnown: false));
      var spent = Spent(result);
      Assert.Equal(0, result.InFlightImageTokens);
      Assert.Equal(images, result.InFlightUncountedImages);
      Assert.EndsWith("as an estimate from the request's size, " + SpendMath.CentsText(spent.Cents.Value) + ". " + says, TurnSpend.StoppedLine(spent, result));
      Assert.Equal(Spent(StoppedWith(new LlmMessage { Role = LlmRole.User, Blocks = { LlmBlock.TextBlock("like this") } })).InputTokens, spent.InputTokens);
    }

    [Fact]
    public void An_image_on_a_row_with_the_rule_is_in_the_estimate_and_nothing_is_left_out()
    {
      var result = StoppedWith(WithImages(1, ruleKnown: true));
      var spent = Spent(result);
      Assert.Equal(2019, result.InFlightImageTokens);
      Assert.Equal(0, result.InFlightUncountedImages);
      Assert.EndsWith("as an estimate from the request's size, " + SpendMath.CentsText(spent.Cents.Value) + ".", TurnSpend.StoppedLine(spent, result));
      Assert.True(spent.InputTokens >= 2019);
    }

    /// <summary>The stream's count already holds the image, on any row, so "leaves out the image" would be false there.</summary>
    [Fact]
    public void A_count_the_stream_said_already_holds_the_image()
    {
      var stop = new CancellationTokenSource();
      var provider = new Scripted();
      provider.Steps.Enqueue(CutAfterACount(stop));
      var result = Turn(provider, stop.Token, null, WithImages(1, ruleKnown: false));
      Assert.Equal(0, result.InFlightUncountedImages);
      Assert.DoesNotContain("image", TurnSpend.StoppedLine(Spent(result), result));
    }

    // ---- the floor is one ----------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// A chat step's input is mostly served from the cache, and a verb's can be wholly (a re-run inside the cache's life), so an estimate at the first-read price would stand above the bill and
    /// "floor" would be false. One estimate serves both: on every priced row it is at most the least its input can cost, the cache's read, the plain input and the cache's write alike.
    /// </summary>
    [Fact]
    public void On_every_priced_row_a_stopped_runs_estimate_is_at_most_what_the_input_could_cost()
    {
      var rows = ModelCatalogue.Parse(File.ReadAllText(Path.Combine(Repo(), "Dwight", "Resources", "models.json")));
      var priced = rows.Where(LargeRun.IsPriced).ToList();
      Assert.True(priced.Count >= 5);
      foreach (var m in priced)
      {
        var e = StoppedEstimate.Of(m, 12_000, Elapsed, null);
        var least = new[] { m.CostCents(e.InputTokens, 0, 0, 0), m.CostCents(0, 0, e.InputTokens, 0), m.CostCents(0, 0, 0, e.InputTokens) }.Min();
        Assert.True(e.Cents.Value <= least + 1e-9, m.Id + " estimates above the least its input can cost");
      }
    }

    /// <summary>
    /// The known item recorded with #254, closed: a verb stopped on a re-read inside the cache's life was estimated at the cache's WRITE price, about twelve times its bill on Anthropic. A verb
    /// is cached because StructuredOutput sends with CacheSystem, which breaks on the system block and the transcript's tail; so its estimate is the chat's, through the one function.
    /// </summary>
    [Fact]
    public void A_verbs_estimate_is_the_same_floor_as_a_chat_turns()
    {
      var e = StoppedEstimate.Of(Sonnet, 12_000, Elapsed, null);
      Assert.Equal(Sonnet.CostCents(0, 0, e.InputTokens, 0), e.Cents.Value);
      Assert.True(e.Cents.Value < LargeRun.EstimateCents(Sonnet, e.InputTokens));   // below the first read, which is what made a re-read's estimate stand above its bill

      Assert.Contains("CacheSystem = true,", Source("Dwight", "Llm", "StructuredOutput.cs"));   // why a verb's read can be a cache read at all
      Assert.Contains("ctx.Spend = StoppedEstimate.Of(model, ctx.SentChars, DateTime.UtcNow - ctx.SentAtUtc, SpendLedger.ReadSample());", Source("Dwight", "LlmActions.cs"));
      var estimate = Source("Dwight", "Session", "StoppedEstimate.cs");
      Assert.Contains("spend.Cents = model.CostCents(0, 0, tokens, 0);", estimate);
      Assert.DoesNotContain("LargeRun.EstimateCents", estimate);   // one price for every stopped request, never the first read's
    }

    [Theory]
    [InlineData("free")]
    [InlineData("subscription")]
    public void A_row_with_no_price_states_its_tokens_and_invents_no_cents(string pricing)
    {
      var model = new ModelInfo("m", "M", "Ollama", "Mid", 0, 0, 32_000, pricingModel: pricing);
      var result = StoppedWith(LlmMessage.User(new string('x', 12_000)));
      var spent = Spent(result, model);
      Assert.Null(spent.Cents);
      var words = TurnSpend.StoppedLine(spent, result);
      Assert.EndsWith(" tokens in.", words);
      Assert.DoesNotContain("floor", words);
      Assert.DoesNotContain("¢", words);
    }

    [Fact]
    public void The_receipt_marks_an_estimate_and_leaves_a_bill_as_it_was()
    {
      var result = StoppedWith(LlmMessage.User("words"));
      Assert.Contains(" · estimated", SpendMath.TurnReceipt(Spent(result), "Sonnet 5"));
      Assert.DoesNotContain("estimated", SpendMath.TurnReceipt(new StoredSpend { ElapsedMs = 1000, Cents = 1.0 }, "Sonnet 5"));
    }

    // ---- the readers: what a cut stream had already said -----------------------------------------------------------------------------------------------

    /// <summary>Hands out a stream's lines and fires the Stop as it reads the line at a given index. The reader checks the token before it acts on that line, so an event must be whole, its blank line read, before that index.</summary>
    private sealed class StopAfter : TextReader
    {
      private readonly Queue<string> _lines;
      private readonly int _after;
      private readonly CancellationTokenSource _stop;
      private int _given;
      public StopAfter(CancellationTokenSource stop, int after, params string[] lines) { _stop = stop; _after = after; _lines = new Queue<string>(lines); }
      public override string ReadLine()
      {
        if (_given++ == _after) _stop.Cancel();
        return _lines.Count > 0 ? _lines.Dequeue() : null;
      }
    }

    private const string AnthropicStart = "data: {\"type\":\"message_start\",\"message\":{\"id\":\"m\",\"type\":\"message\",\"role\":\"assistant\",\"content\":[],\"model\":\"x\",\"usage\":{\"input_tokens\":12,\"cache_read_input_tokens\":19000,\"cache_creation_input_tokens\":300,\"output_tokens\":2}}}";
    private const string Ping = "data: {\"type\":\"ping\"}";

    private static LlmUsage CarriedBy(Action read)
    {
      var stopped = Assert.ThrowsAny<OperationCanceledException>(read);
      return UsageCarrier.TryTake(stopped, out var usage) ? usage : null;
    }

    [Fact]
    public void A_cut_anthropic_stream_carries_the_count_message_start_said()
    {
      var stop = new CancellationTokenSource();
      var usage = CarriedBy(() => AnthropicReplyStream.Read(new StopAfter(stop, 3, "event: message_start", AnthropicStart, "", "event: ping", Ping, ""), stop.Token));
      Assert.Equal(12, usage.InputTokens);
      Assert.Equal(19000, usage.CacheReadInputTokens);
      Assert.Equal(300, usage.CacheCreationInputTokens);
    }

    [Fact]
    public void An_anthropic_stream_cut_before_message_start_carries_nothing()
    {
      var stop = new CancellationTokenSource();
      Assert.Null(CarriedBy(() => AnthropicReplyStream.Read(new StopAfter(stop, 0, "event: message_start", AnthropicStart, ""), stop.Token)));
    }

    [Fact]
    public void A_cut_google_stream_carries_the_count_its_last_chunk_said()
    {
      var stop = new CancellationTokenSource();
      const string chunk = "data: {\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"Hi\"}],\"role\":\"model\"}}],\"usageMetadata\":{\"promptTokenCount\":900,\"candidatesTokenCount\":3}}";
      var usage = CarriedBy(() => GoogleProvider.ReadStream(new StopAfter(stop, 2, chunk, "", chunk, ""), stop.Token));
      Assert.Equal(900, usage.InputTokens);
      Assert.Equal(3, usage.OutputTokens);
    }

    /// <summary>OpenAI's and Ollama's streams say their usage only at the end, so a cut one has no count and the turn estimates.</summary>
    [Fact]
    public void A_cut_openai_stream_has_no_count_to_carry()
    {
      var stop = new CancellationTokenSource();
      const string chunk = "data: {\"choices\":[{\"delta\":{\"content\":\"Hi\"}}]}";
      Assert.Null(CarriedBy(() => OpenAIProvider.ReadStream(new StopAfter(stop, 2, chunk, "", chunk, ""), stop.Token, "OpenAI")));
    }

    // ---- the seam, read from the source (ADR-091) ---------------------------------------------------------------------------------------------------------

    private static string Repo()
    {
      var dir = new DirectoryInfo(AppContext.BaseDirectory);
      while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Dwight.sln"))) dir = dir.Parent;
      return dir?.FullName ?? throw new InvalidOperationException("no repository above the test binaries");
    }

    private static string Source(params string[] parts) => File.ReadAllText(Path.Combine(new[] { Repo() }.Concat(parts).ToArray()));

    private static int Count(string text, string needle)
    {
      int n = 0, at = 0;
      while ((at = text.IndexOf(needle, at, StringComparison.Ordinal)) >= 0) { n++; at += needle.Length; }
      return n;
    }

    private static string Between(string text, string from, string to)
    {
      var a = text.IndexOf(from, StringComparison.Ordinal);
      Assert.True(a >= 0, "no " + from);
      var b = text.IndexOf(to, a, StringComparison.Ordinal);
      Assert.True(b > a, "no " + to);
      return text.Substring(a, b - a);
    }

    /// <summary>This is the defect itself: the branch every stopped turn takes returned before the only code that wrote its spend.</summary>
    [Fact]
    public void The_branch_a_stopped_turn_ends_on_writes_its_spend_before_it_clears()
    {
      var scaffold = Source("Dwight", "UI", "DwightScaffold.cs");
      var branch = Between(scaffold, "completion on a SUPERSEDED generation", "return;");
      var written = branch.IndexOf("if (ctx.ForceEnded)", StringComparison.Ordinal);
      var through = branch.IndexOf("RecordTurnSpend(wp, doc, threadId, resolved.Model, result, sw.Elapsed);", StringComparison.Ordinal);
      var done = branch.IndexOf("DwightSession.Done(gen);", StringComparison.Ordinal);
      Assert.True(written > 0 && through > written && done > through);
      Assert.Contains("RefreshHeader(wp, doc, threadId);", branch);   // the chat's total moved
    }

    /// <summary>One door, and the two ends of a completion are exclusive, so a turn cannot write twice.</summary>
    [Fact]
    public void Both_ends_of_a_completion_share_one_door_and_only_the_door_writes()
    {
      var scaffold = Source("Dwight", "UI", "DwightScaffold.cs");
      Assert.Equal(3, Count(scaffold, "RecordTurnSpend(wp, doc, threadId, ") + Count(scaffold, "private static StoredSpend RecordTurnSpend("));
      Assert.Equal(1, Count(scaffold, "CommandHistory.AppendThreadSpend("));
      Assert.Equal(1, Count(scaffold, "TurnSpend.StoppedLine("));
      Assert.DoesNotContain("StoppedEstimate.Of(", scaffold);

      // The ledger line first, so a failed append costs it nothing.
      var door = Between(scaffold, "private static StoredSpend RecordTurnSpend(", "private static void RefreshHeader(");
      var ledger = door.IndexOf("CommandHistory.AppendThreadSpend(doc, threadId, spent);", StringComparison.Ordinal);
      var said = door.IndexOf("AppendPluginHtml(wp, doc, threadId, System.Net.WebUtility.HtmlEncode(line));", StringComparison.Ordinal);
      Assert.True(ledger > 0 && said > ledger);
    }

    /// <summary>A Stop and the clock mark the run before anything reports it, and only a live run: a Stop with nothing running returns first.</summary>
    [Fact]
    public void Stop_and_the_clock_mark_the_run()
    {
      var session = Source("Dwight", "DwightSession.cs");
      var force = Between(session, "private static void ForceEnd(bool timedOut)", "public static void Failed(");
      var live = force.IndexOf("if (!_running)", StringComparison.Ordinal);
      var marked = force.IndexOf("if (_activeCtx != null) _activeCtx.ForceEnded = true;", StringComparison.Ordinal);
      var reported = force.IndexOf("Failed(_activeCtx, \"notice\", title, msg);", StringComparison.Ordinal);
      Assert.True(live > 0 && marked > live && reported > marked);
      Assert.Contains("public static void Stop() => ForceEnd(timedOut: false);", session);
      Assert.Contains("ForceEnd(timedOut: true);", session);
    }

    /// <summary>
    /// The follow-up the architect ruled with #254: a Restart of a chat turn writes its line too, as the verbs' cancellation catch already does, because an unrecorded bill is what Brief AK
    /// forbids. A Restart cancels the same token a Stop cancels, so the driver ends the turn as Stopped on the same path the tests above run, and the completion takes the same door. The mark is
    /// made in the lock, before the generation moves, and only for a run that was live: with nothing running, the context is an old run's, already written.
    /// </summary>
    [Fact]
    public void A_restart_marks_a_live_run_so_its_completion_writes_through_the_same_door()
    {
      var session = Source("Dwight", "DwightSession.cs");
      var restart = Between(session, "public static void Restart()", "private static void ForceEnd(");
      var locked = restart.IndexOf("lock (_lock)", StringComparison.Ordinal);
      var marked = restart.IndexOf("if (_running && _activeCtx != null) _activeCtx.ForceEnded = true;", StringComparison.Ordinal);
      var ended = restart.IndexOf("_running = false; _gen++; _cts?.Cancel();", StringComparison.Ordinal);
      var closed = restart.IndexOf("WebPanel.CloseCurrent();", StringComparison.Ordinal);
      Assert.True(locked >= 0 && marked > locked && ended > marked && closed > ended);   // marked while _running still says whether a run was live, and before its panel goes

      // The completion reads only the mark: the same branch and the same door as a Stop, with nothing of its own for a Restart.
      var scaffold = Source("Dwight", "UI", "DwightScaffold.cs");
      var branch = Between(scaffold, "completion on a SUPERSEDED generation", "return;");
      Assert.Contains("if (ctx.ForceEnded)", branch);
      Assert.DoesNotContain("Restart", Between(scaffold, "if (ctx.ForceEnded)", "return;"));
      Assert.Equal(2, Count(session, "_activeCtx.ForceEnded = true;"));   // Stop and the clock's ForceEnd, and Restart: no third way marks a run
    }

    /// <summary>
    /// The known item recorded with #254: a stopped turn's completion comes back when its reader next hears from the provider, and by then the person may have sent again on the same page.
    /// The orphan frees the composer and clears the status only when nothing live is on its page; a run on another page leaves this one idle, and none at all is the ordinary case.
    /// </summary>
    [Fact]
    public void An_orphan_frees_the_composer_only_when_no_newer_turn_runs_on_its_page()
    {
      var page = new object();
      var other = new object();
      Assert.False(AgentLoopCore.OrphanFreesComposer(page, page));    // the person sent again here: the status and the composer are the newer turn's
      Assert.True(AgentLoopCore.OrphanFreesComposer(page, null));     // nothing runs: hand the composer back, as before
      Assert.True(AgentLoopCore.OrphanFreesComposer(page, other));    // a run on another page: this one is idle
      Assert.True(AgentLoopCore.OrphanFreesComposer(null, null));
    }

    [Fact]
    public void The_orphans_branch_asks_before_it_frees_and_still_writes_its_spend_and_its_total()
    {
      var scaffold = Source("Dwight", "UI", "DwightScaffold.cs");
      var branch = Between(scaffold, "completion on a SUPERSEDED generation", "return;");
      var spend = branch.IndexOf("RecordTurnSpend(wp, doc, threadId, resolved.Model, result, sw.Elapsed);", StringComparison.Ordinal);
      var asked = branch.IndexOf("if (AgentLoopCore.OrphanFreesComposer(wp, DwightSession.RunningPanel))", StringComparison.Ordinal);
      var status = branch.IndexOf("wp.SetStatus(\"\");", StringComparison.Ordinal);
      var busy = branch.IndexOf("wp.SetChatBusy(false);", StringComparison.Ordinal);
      var header = branch.IndexOf("RefreshHeader(wp, doc, threadId);", StringComparison.Ordinal);
      var done = branch.IndexOf("DwightSession.Done(gen);", StringComparison.Ordinal);
      Assert.True(spend > 0 && asked > spend && status > asked && busy > status && header > busy && done > header);
      Assert.Equal(1, Count(branch, "wp.SetChatBusy(false);"));
      Assert.Equal(1, Count(branch, "wp.SetStatus(\"\");"));
      // The Stop's own report frees them at the moment of the Stop, when no newer turn can exist yet.
      Assert.Contains("try { wp.SetChatBusy(false); }", Between(scaffold, "ctx.OnFailed = (kind, title, message) =>", "Task.Run(() =>"));
    }

    [Fact]
    public void A_cut_stream_goes_on_the_turn_as_the_providers_count_not_an_estimate()
    {
      var driver = Source("Dwight", "Agent", "AgentTurnDriver.cs");
      var cut = Between(driver, "catch (OperationCanceledException stopped)", "catch (Exception ex)");
      Assert.True(cut.IndexOf("UsageCarrier.TryTake(stopped, out var counted)", StringComparison.Ordinal) < cut.IndexOf("result.InFlightChars =", StringComparison.Ordinal));
    }
  }
}
