using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Dwight.Models;
using Dwight.Session;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>Brief CS: a verb that will cost real money on a huge selection says so first. The estimate, the threshold's three
  /// states and the sentence are pure and tested here against the real catalogue; the surfaces and their order are GH-coupled,
  /// so they are pinned by the text of the code, and the Rhino row is the architect's.</summary>
  public class LargeRunTests
  {
    private const int Default = LargeRun.DefaultThresholdCents;
    private const string Explain = DwightVerbs.Explain;
    private const string Doc = "11111111-2222-3333-4444-555555555555";
    private static readonly DateTime Now = new DateTime(2026, 11, 1, 6, 0, 0, DateTimeKind.Utc);

    /// <summary>A ledger line as the writer writes it, read back through the parser the question reads with.</summary>
    private static string LineOf(string verb, string provider, string model, long chars, int input, int output, DateTime? utc = null, string doc = Doc)
    {
      var spend = new StoredSpend { Model = model, InputTokens = 4, CacheWriteTokens = input - 4, OutputTokens = output, Cents = 1, Chars = chars };
      return SpendLedgerCore.Serialize(SpendLedgerCore.ForVerb(spend, utc ?? Now.AddHours(-1), doc, "tower.gh", verb, provider));
    }

    private static LedgerSample SampleOf(params string[] lines) => SpendLedgerCore.Sample(lines);

    /// <summary>The gate's own line, three times over: the ninth gate's row 10 on Sonnet 5.5.</summary>
    private static LedgerSample GateLines() => SampleOf(Enumerable.Range(0, 3).Select(_ =>
      LineOf(Explain, "Anthropic", "claude-sonnet-5-5", 75_000, 39_693, 819)).ToArray());

    private static JsonNode Catalogue => JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "models.json")));

    private static JsonNode Row(string id) => Catalogue["models"].AsArray().Single(m => m["id"].GetValue<string>() == id);

    /// <summary>A model as the catalogue states it, so a price here is the price the plugin would read.</summary>
    private static ModelInfo Model(string id)
    {
      var r = Row(id);
      return new ModelInfo(id, r["displayName"].GetValue<string>(), r["provider"].GetValue<string>(), r["tier"].GetValue<string>(),
        r["priceIn"].GetValue<double>(), r["priceOut"].GetValue<double>(), r["contextWindow"].GetValue<int>(),
        pricingModel: r["pricingModel"]?.GetValue<string>(),
        cacheReadPerMTok: r["priceCachedIn"]?.GetValue<double>(),
        cacheWritePerMTok: r["priceCacheWrite"]?.GetValue<double>());
    }

    private static string Repo(params string[] parts)
    {
      var dir = new DirectoryInfo(AppContext.BaseDirectory);
      while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Dwight.sln"))) dir = dir.Parent;
      Assert.NotNull(dir);
      return File.ReadAllText(Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray()));
    }

    private static string Actions => Repo("Dwight", "LlmActions.cs");

    // ---- the estimate ----

    [Fact]
    public void Anthropic_prices_a_read_at_the_catalogues_write_price_because_a_verb_caches_all_of_it()
    {
      Assert.Equal(118.5625, LargeRun.EstimateCents(Model("claude-sonnet-5"), 474_250), 4);
      Assert.Equal(25.0, LargeRun.EstimateCents(Model("claude-sonnet-5"), 100_000), 6);
      Assert.Equal(125.0, LargeRun.EstimateCents(Model("claude-fable-5-1"), 100_000), 6);
    }

    [Theory]
    [InlineData("gpt-5.6-terra", 20.0)]
    [InlineData("gpt-6-astra", 100.0)]
    [InlineData("gemini-3.8-flash", 7.5)]
    public void Another_provider_prices_a_read_as_input(string id, double cents) =>
      Assert.Equal(cents, LargeRun.EstimateCents(Model(id), 100_000), 6);

    [Fact]
    public void The_estimate_goes_through_the_ledgers_own_price()
    {
      var sonnet = Model("claude-sonnet-5");
      Assert.Equal(sonnet.CostCents(0, 0, 0, 123_456), LargeRun.EstimateCents(sonnet, 123_456), 9);
      var terra = Model("gpt-5.6-terra");
      Assert.Equal(terra.CostCents(123_456, 0, 0, 0), LargeRun.EstimateCents(terra, 123_456), 9);
    }

    /// <summary>
    /// THE NINTH GATE'S ROW 10, WITH NOTHING IN THE LEDGER. Its question said "about 5 cents" and the line after Continue was 10.74 cents.
    /// 75,000 characters at Anthropic's stated 2.0 is 37,500 tokens, at the write price of $2.50 a million 9.375 cents; Explain's stated answer
    /// of 1,500 tokens at $10 a million is 1.5 cents; 10.875 in all. A quarter either side of the bill is 8.06 to 13.43, and the brief's test is 8 to 13.
    /// </summary>
    [Fact]
    public void The_gates_case_with_an_empty_ledger_lands_within_a_quarter_of_its_bill()
    {
      var estimate = LargeRun.Estimate(Model("claude-sonnet-5-5"), 75_000, Explain, null);

      Assert.Equal(10.875, estimate, 6);
      Assert.InRange(estimate, 8, 13);
      Assert.Equal(estimate, LargeRun.Estimate(Model("claude-sonnet-5-5"), 75_000, Explain, LedgerSample.Empty), 9);
    }

    /// <summary>With the gate's own lines in the ledger the estimate is the bill: 39,693 tokens at the write price and 819 of output.</summary>
    [Fact]
    public void The_gates_case_with_its_own_lines_in_the_ledger_is_its_bill()
    {
      var sonnet = Model("claude-sonnet-5-5");
      Assert.Equal(sonnet.CostCents(4, 819, 0, 39_689), LargeRun.Estimate(sonnet, 75_000, Explain, GateLines()), 2);
    }

    /// <summary>The row Brief CS was measured on: 1,897,000 characters billed 404,165 write tokens and 102.51 cents, 4.69 characters a token, a read
    /// that tokenizes far better than the gate's. A stated default cannot be right for both, and it is the ledger that lands it within a quarter. Until
    /// then it errs high and says so before the money goes.</summary>
    [Fact]
    public void The_BESS_row_lands_within_a_quarter_once_the_ledger_has_measured_such_reads_and_errs_high_until_it_has()
    {
      const double ledger = 102.51;
      var sonnet = Model("claude-sonnet-5");
      var measured = SampleOf(Enumerable.Range(0, 3).Select(_ => LineOf(Explain, "Anthropic", "claude-sonnet-5", 1_897_000, 404_169, 1467)).ToArray());

      Assert.InRange(Math.Abs(LargeRun.Estimate(sonnet, 1_897_000, Explain, measured) - ledger) / ledger, 0, 0.25);
      Assert.InRange(LargeRun.Estimate(sonnet, 1_897_000, Explain, null), ledger, ledger * 3);
      Assert.NotNull(LargeRun.Ask(4972, 1_897_000, sonnet, Default, Explain));
    }

    // ---- where the two numbers come from ----

    [Theory]
    [InlineData("Anthropic", 2.0)]
    [InlineData("OpenAI", 3.5)]
    [InlineData("Google", 3.5)]
    [InlineData("OllamaLocal", 3.5)]
    [InlineData("OllamaCloud", 3.5)]
    public void Each_provider_has_a_stated_ratio_and_none_is_the_old_four(string provider, double ratio) =>
      Assert.Equal(ratio, Dwight.Data.SelectionSerializer.DefaultCharsPerToken(provider));

    [Theory]
    [InlineData(DwightVerbs.Explain, 1500)]
    [InlineData(DwightVerbs.Diagnose, 900)]
    [InlineData(DwightVerbs.Reorganise, 700)]
    [InlineData(DwightVerbs.Simplify, 2500)]
    [InlineData(LargeRun.ToScript, 3000)]
    public void Each_verb_has_a_stated_answer(string verb, long tokens) => Assert.Equal(tokens, LargeRun.DefaultOutputTokens(verb));

    [Fact]
    public void Three_lines_of_a_known_ratio_win_over_the_stated_one()
    {
      var sonnet = Model("claude-sonnet-5-5");
      var lines = SampleOf(Enumerable.Range(0, 3).Select(_ => LineOf(Explain, "Anthropic", "claude-sonnet-5-5", 40_000, 10_000, 1000)).ToArray());
      Assert.Equal(25_000, LargeRun.InputTokens(sonnet, 100_000, lines));   // 4.0 measured
      Assert.Equal(50_000, LargeRun.InputTokens(sonnet, 100_000, null));    // 2.0 stated
    }

    [Fact]
    public void Two_lines_are_not_enough_and_the_stated_numbers_stand()
    {
      var sonnet = Model("claude-sonnet-5-5");
      var two = SampleOf(Enumerable.Range(0, 2).Select(_ => LineOf(Explain, "Anthropic", "claude-sonnet-5-5", 40_000, 10_000, 99)).ToArray());
      Assert.Equal(50_000, LargeRun.InputTokens(sonnet, 100_000, two));
      Assert.Equal(1500, LargeRun.OutputTokens(sonnet, Explain, two));
    }

    [Fact]
    public void A_verb_with_no_lines_uses_its_own_answer_with_the_measured_ratio()
    {
      var sonnet = Model("claude-sonnet-5-5");
      Assert.Equal(819, LargeRun.OutputTokens(sonnet, Explain, GateLines()));
      Assert.Equal(900, LargeRun.OutputTokens(sonnet, DwightVerbs.Diagnose, GateLines()));
      Assert.Equal(sonnet.CostCents(0, 900, 0, 39_693), LargeRun.Estimate(sonnet, 75_000, DwightVerbs.Diagnose, GateLines()), 2);
    }

    /// <summary>A ledger line calls a to-script run Simplify, so to-script has no lines of its own and keeps its stated answer.</summary>
    [Fact]
    public void To_script_is_not_read_from_Simplifys_lines()
    {
      var opus = Model("claude-opus-5-5");
      var simplify = SampleOf(Enumerable.Range(0, 3).Select(_ => LineOf(DwightVerbs.Simplify, "Anthropic", "claude-opus-5-5", 20_000, 7000, 375)).ToArray());
      Assert.Equal(375, LargeRun.OutputTokens(opus, DwightVerbs.Simplify, simplify));
      Assert.Equal(3000, LargeRun.OutputTokens(opus, LargeRun.ToScript, simplify));
    }

    [Fact]
    public void The_answer_is_priced_at_the_output_price_on_every_provider()
    {
      foreach (var id in new[] { "claude-sonnet-5-5", "gpt-5.6-terra", "gemini-3.8-flash" })
      {
        var m = Model(id);
        Assert.Equal(LargeRun.EstimateCents(m, LargeRun.InputTokens(m, 70_000, null)) + m.CostCents(0, 1500, 0, 0), LargeRun.Estimate(m, 70_000, Explain, null), 9);
      }
    }

    // ---- the threshold's three states ----

    [Fact]
    public void Under_the_threshold_nothing_is_asked()
    {
      // Fifty components on Sonnet 5: 40,000 tokens and the answer, 11.5 cents.
      Assert.Null(LargeRun.Ask(50, 80_000, Model("claude-sonnet-5"), Default, Explain));
    }

    /// <summary>The ninth gate's row 10: the estimate came to 5 at the menu's own 5 cents, and nothing asked.</summary>
    [Fact]
    public void At_the_threshold_it_asks()
    {
      Assert.True(LargeRun.AtOrOver(5.0, 5));
      Assert.False(LargeRun.AtOrOver(4.99, 5));
      Assert.True(LargeRun.AtOrOver(5.01, 5));
      Assert.True(LargeRun.AtOrOver(4.9999999, 5));   // a sum a hair under in floating point is at it

      // Through the estimate: 94,000 tokens and the answer are 25 cents on Sonnet 5, so 188,000 characters are at the default and just under them are not.
      var sonnet = Model("claude-sonnet-5");
      Assert.Equal(25.0, LargeRun.Estimate(sonnet, 188_000, Explain, null), 6);
      Assert.True(LargeRun.IsOver(sonnet, 188_000, Default, Explain, null));
      Assert.False(LargeRun.IsOver(sonnet, 187_990, Default, Explain, null));
      Assert.NotNull(LargeRun.Ask(1000, 188_000, sonnet, Default, Explain));
    }

    [Fact]
    public void The_gates_read_asks_at_the_menus_own_five_cents() =>
      Assert.NotNull(LargeRun.Ask(488, 75_000, Model("claude-sonnet-5-5"), 5, Explain));

    /// <summary>Reorganise's labelling pass with every name cached sends nothing, so it has no read to ask about and no answer to price, whatever the setting.</summary>
    [Fact]
    public void A_read_that_sends_nothing_never_asks()
    {
      var opus = Model("claude-opus-5-5");
      Assert.True(opus.CostCents(0, LargeRun.DefaultOutputTokens(DwightVerbs.Reorganise), 0, 0) >= 1);   // the allowance alone would clear a low setting
      Assert.Null(LargeRun.Ask(500, 0, opus, 1, DwightVerbs.Reorganise));
      Assert.Null(LargeRun.Ask(500, 0, Model("glm-5.3:cloud"), 1, DwightVerbs.Reorganise));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Zero_never_asks_however_large_the_read(int threshold)
    {
      Assert.Null(LargeRun.Ask(4972, 1_897_000, Model("claude-sonnet-5"), threshold, Explain));
      Assert.Null(LargeRun.Ask(4972, 1_897_000, Model("glm-5.3:cloud"), threshold, Explain));
    }

    [Fact]
    public void The_threshold_is_what_decides_not_the_read()
    {
      var sonnet = Model("claude-sonnet-5");
      Assert.True(LargeRun.IsOver(sonnet, 400_000, 10, Explain, null));      // 200,000 tokens and the answer: 51.5 cents
      Assert.False(LargeRun.IsOver(sonnet, 400_000, 100, Explain, null));
    }

    [Fact]
    public void The_price_is_what_decides_too()
    {
      // The same read, over on a model that costs more and under on one that costs less.
      Assert.True(LargeRun.IsOver(Model("claude-fable-5-1"), 100_000, Default, Explain, null));
      Assert.False(LargeRun.IsOver(Model("claude-sonnet-5"), 100_000, Default, Explain, null));
      Assert.False(LargeRun.IsOver(Model("claude-haiku-5-5"), 100_000, Default, Explain, null));
    }

    // ---- the sentence ----

    [Fact]
    public void The_sentence_as_ruled()
    {
      Assert.Equal("This will read 4,972 components, about 1,897,000 characters, about $1.02 on Sonnet 5. Continue?",
        LargeRun.Sentence(4972, 1_897_000, LargeRun.CostText(102), "Sonnet 5"));
    }

    [Fact]
    public void The_sentence_says_what_it_counted_in_the_briefs_own_case()
    {
      Assert.Equal("This will read 488 components, about 75,000 characters, about 11 cents on Claude Sonnet 5.5 for the read and the answer. Continue?",
        LargeRun.Ask(488, 75_000, Model("claude-sonnet-5-5"), 5, Explain));
    }

    [Fact]
    public void The_sentence_from_the_catalogue_names_the_catalogues_model()
    {
      Assert.Equal("This will read 4,972 components, about 1,897,000 characters, about $2.39 on Claude Sonnet 5 for the read and the answer. Continue?",
        LargeRun.Ask(4972, 1_897_000, Model("claude-sonnet-5"), Default, Explain));
    }

    [Fact]
    public void Under_a_dollar_it_says_cents()
    {
      Assert.Equal("This will read 1,200 components, about 480,000 characters, about 62 cents on Claude Sonnet 5 for the read and the answer. Continue?",
        LargeRun.Ask(1200, 480_000, Model("claude-sonnet-5"), Default, Explain));
    }

    // ---- the cache clause: only when this very read went out inside the cache's life, on a row with a cache price ----

    private const string Clause = ", less if the last read is still cached";

    private static string AskedAfter(string earlierLine, string modelId = "claude-sonnet-5-5", string verb = Explain, int chars = 75_000, string doc = Doc) =>
      LargeRun.Ask(488, chars, Model(modelId), 5, verb, SampleOf(earlierLine), doc, Now);

    [Fact]
    public void A_second_read_inside_five_minutes_adds_the_clause_and_the_same_number()
    {
      var line = LineOf(Explain, "Anthropic", "claude-sonnet-5-5", 75_000, 39_693, 819, Now.AddMinutes(-2));
      Assert.Equal("This will read 488 components, about 75,000 characters, about 11 cents on Claude Sonnet 5.5 for the read and the answer, less if the last read is still cached. Continue?",
        AskedAfter(line));
    }

    [Theory]
    [InlineData(5, true)]
    [InlineData(6, false)]
    [InlineData(-1, false)]   // a line from the future says nothing about the cache
    public void The_clause_lasts_five_minutes(int minutesAgo, bool said)
    {
      var line = LineOf(Explain, "Anthropic", "claude-sonnet-5-5", 75_000, 39_693, 819, Now.AddMinutes(-minutesAgo));
      Assert.Equal(said, AskedAfter(line).Contains(Clause));
    }

    [Fact]
    public void Another_read_does_not_earn_the_clause()
    {
      var line = LineOf(Explain, "Anthropic", "claude-sonnet-5-5", 75_000, 39_693, 819, Now.AddMinutes(-2));
      Assert.DoesNotContain(Clause, AskedAfter(line, chars: 75_001));                                 // another selection
      Assert.DoesNotContain(Clause, AskedAfter(line, verb: DwightVerbs.Diagnose));                    // another verb's prompt
      Assert.DoesNotContain(Clause, AskedAfter(line, modelId: "claude-sonnet-5"));                    // another model
      Assert.DoesNotContain(Clause, AskedAfter(line, doc: "99999999-2222-3333-4444-555555555555"));   // another file
      Assert.DoesNotContain(Clause, AskedAfter(line, doc: ""));                                       // a document with no id
    }

    /// <summary>A line with no document says nothing about which file it read, so two of them are not the same read.</summary>
    [Fact]
    public void Two_reads_with_no_document_are_not_the_same_read()
    {
      var line = LineOf(Explain, "Anthropic", "claude-sonnet-5-5", 75_000, 39_693, 819, Now.AddMinutes(-2), doc: "");
      Assert.DoesNotContain(Clause, AskedAfter(line, doc: ""));
    }

    [Fact]
    public void A_row_with_no_cache_price_never_says_the_clause()
    {
      var row = new ModelInfo("x", "Some Model", "Anthropic", "Mid", 2.0, 10.0, 1_000_000);
      Assert.False(row.HasCachedInPrice);
      var line = LineOf(Explain, "Anthropic", "x", 75_000, 39_693, 819, Now.AddMinutes(-2));
      Assert.DoesNotContain(Clause, LargeRun.Ask(488, 75_000, row, 5, Explain, SampleOf(line), Doc, Now));
      Assert.True(Model("claude-sonnet-5-5").HasCachedInPrice);
    }

    [Fact]
    public void An_unpriced_model_never_says_the_clause_or_the_read_and_the_answer()
    {
      var line = LineOf(Explain, "OllamaCloud", "glm-5.3:cloud", 400_000, 120_000, 500, Now.AddMinutes(-2));
      var said = LargeRun.Ask(1000, 400_000, Model("glm-5.3:cloud"), Default, Explain, SampleOf(line), Doc, Now);
      Assert.DoesNotContain(Clause, said);
      Assert.DoesNotContain("the read and the answer", said);
    }

    /// <summary>The threshold is compared with the cold read, whatever the cache may do: the clause is a word, never a discount.</summary>
    [Fact]
    public void The_clause_moves_no_number_and_no_threshold()
    {
      var sonnet = Model("claude-sonnet-5-5");
      var line = LineOf(Explain, "Anthropic", "claude-sonnet-5-5", 75_000, 39_693, 819, Now.AddMinutes(-2));
      Assert.True(LargeRun.IsOver(sonnet, 75_000, 5, Explain, SampleOf(line)));
      Assert.Equal(LargeRun.Estimate(sonnet, 75_000, Explain, null), LargeRun.Estimate(sonnet, 75_000, Explain, SampleOf(line)), 9);
    }

    [Theory]
    [InlineData(102.0, "$1.02")]
    [InlineData(102.51, "$1.03")]
    [InlineData(100.0, "$1.00")]
    [InlineData(99.6, "$1.00")]
    [InlineData(99.4, "99 cents")]
    [InlineData(26.0, "26 cents")]
    [InlineData(1.2, "1 cent")]
    [InlineData(0.4, "less than 1 cent")]
    public void The_cost_is_said_plainly(double cents, string said) => Assert.Equal(said, LargeRun.CostText(cents));

    [Fact]
    public void The_numbers_are_rounded_only_as_far_as_the_estimate_can_claim()
    {
      Assert.Contains("about 1,897,000 characters", LargeRun.Sentence(1, 1_897_499, "x", "m"));
      Assert.Contains("about 1,898,000 characters", LargeRun.Sentence(1, 1_897_500, "x", "m"));
      Assert.Contains("about 999 characters", LargeRun.Sentence(1, 999, "x", "m"));
    }

    [Fact]
    public void One_component_is_one_component() =>
      Assert.StartsWith("This will read 1 component, ", LargeRun.Sentence(1, 1000, "x", "m"));

    [Theory]
    [InlineData("en-IN")]   // would write 18,97,000
    [InlineData("de-DE")]   // would write 1.897.000 and 1,19
    public void The_sentence_reads_the_same_on_every_machine(string culture)
    {
      var before = CultureInfo.CurrentCulture;
      try
      {
        CultureInfo.CurrentCulture = new CultureInfo(culture);
        Assert.Equal("This will read 4,972 components, about 1,897,000 characters, about $2.39 on Claude Sonnet 5 for the read and the answer. Continue?",
          LargeRun.Sentence(4972, 1_897_000, Model("claude-sonnet-5"), Explain));
      }
      finally { CultureInfo.CurrentCulture = before; }
    }

    // ---- a model with no price ----

    [Fact]
    public void An_unpriced_cloud_model_says_the_token_count_and_no_cents()
    {
      var glm = Model("glm-5.3:cloud");
      Assert.False(LargeRun.IsPriced(glm));
      Assert.Equal("This will read 4,972 components, about 1,897,000 characters, about 542,000 tokens on GLM-5.3 (cloud). Continue?",
        LargeRun.Ask(4972, 1_897_000, glm, Default, Explain));
    }

    [Fact]
    public void A_local_model_says_the_token_count_and_no_cents()
    {
      var qwen = Model("qwen3.5:9b");
      Assert.False(LargeRun.IsPriced(qwen));
      Assert.Equal("This will read 1,200 components, about 480,000 characters, about 137,000 tokens on Qwen3.5 9B (local). Continue?",
        LargeRun.Ask(1200, 480_000, qwen, Default, Explain));
    }

    [Fact]
    public void A_priced_model_is_priced_and_says_no_token_count()
    {
      var sentence = LargeRun.Ask(4972, 1_897_000, Model("gpt-5.6-terra"), Default, Explain);
      Assert.True(LargeRun.IsPriced(Model("gpt-5.6-terra")));
      Assert.Contains("about $1.10 on GPT-5.6 Terra for the read and the answer", sentence);   // 542,000 tokens at $2, and 1,500 at $12
      Assert.DoesNotContain("tokens", sentence);
    }

    [Fact]
    public void A_row_at_no_price_is_not_said_to_cost_nothing()
    {
      var unknown = new ModelInfo("x", "Some Model", "OpenAI", "Mid", 0, 0, 1_000_000);
      Assert.False(LargeRun.IsPriced(unknown));
      Assert.Contains("about 542,000 tokens on Some Model", LargeRun.Ask(4972, 1_897_000, unknown, Default, Explain));
    }

    [Theory]
    [InlineData("subscription")]
    [InlineData("free")]
    public void A_row_the_ledger_does_not_price_is_not_priced_here_even_with_a_number_on_it(string pricing)
    {
      // SpendOf reads the pricing model before the numbers; the estimate must not disagree with what the ledger will say.
      var row = new ModelInfo("x", "Some Model", "OllamaCloud", "Mid", 2.0, 10.0, 1_000_000, pricingModel: pricing);
      Assert.False(LargeRun.IsPriced(row));
      Assert.Contains("about 542,000 tokens on Some Model", LargeRun.Ask(4972, 1_897_000, row, Default, Explain));
    }

    [Fact]
    public void An_unpriced_model_asks_at_the_size_that_would_cost_the_threshold_on_Sonnet_5()
    {
      var glm = Model("glm-5.3:cloud");
      Assert.True(LargeRun.IsOver(glm, 350_000, Default, Explain, null));    // 100,000 tokens at the stated 3.5: at it, as a priced model asks
      Assert.False(LargeRun.IsOver(glm, 349_990, Default, Explain, null));
      Assert.False(LargeRun.IsOver(glm, 350_000, 50, Explain, null));
    }

    [Fact]
    public void The_size_an_unpriced_model_asks_at_is_the_one_the_Setting_names()
    {
      // The hint says "the size that would cost this on Claude Sonnet 5", so the constant answers to that row.
      var write = Row("claude-sonnet-5")["priceCacheWrite"].GetValue<double>();
      Assert.Equal(1_000_000 / (write * 100), LargeRun.UnpricedTokensPerCent, 6);
      Assert.Contains("the size that would cost this on Claude Sonnet 5", Repo("Dwight", "Resources", "settings.html"));
      Assert.Equal(Default, Model("claude-sonnet-5").CostCents(0, 0, 0, Default * LargeRun.UnpricedTokensPerCent), 6);
    }

    // ---- where it asks: the order, by the text of the code ----

    /// <summary>The ceiling asks whether a read fits, and a stricter ratio would refuse a read that ran. Brief CY moves the price and leaves it.</summary>
    [Fact]
    public void The_ceiling_keeps_its_own_rough_rule()
    {
      Assert.Contains("var estimated = SelectionSerializer.EstimateTokens(system.Length + user.Length);", Actions);
      Assert.Equal(1000, Dwight.Data.SelectionSerializer.EstimateTokens(4000));
      Assert.Equal(2000, Dwight.Data.SelectionSerializer.EstimateTokens(4000, 2.0));
    }

    /// <summary>Every paid verb call states what it sent, or its line is one the ratio can never be measured from. A second to-script attempt has none on purpose.</summary>
    [Fact]
    public void Every_paid_verb_call_records_what_it_sent()
    {
      var src = Actions;
      Assert.DoesNotContain("SpendOf(model, res.Usage, sw.Elapsed)", src);
      Assert.DoesNotContain("SpendOf(model, usage, sw.Elapsed)", src);
      Assert.Equal(5, Count(src, "SpendOf(model, res.Usage, sw.Elapsed, chars: system.Length + user.Length)"));   // Explain's two, Diagnose's two, Simplify's one
      Assert.Equal(1, Count(src, "SpendOf(model, usage, sw.Elapsed, chars: system.Length + user.Length)"));       // Explain in prose
      Assert.Equal(2, Count(src, "SpendOf(model, res.Usage, sw.Elapsed, chars: system.Length + userC.Length)"));  // a chain from the picker
      Assert.Equal(1, Count(src, "SpendOf(model, res.Usage, sw.Elapsed, chars: sent)"));                          // Reorganise's labelling
      Assert.Contains("SpendOf(model, spent, total, chars: attempt == 1 ? system.Length + message.Length : 0)", src);
      Assert.Contains("var sent = ClusterLabeller.PromptChars(graph, macro);", src);
      // SpendOf is bound to Grasshopper's world, so what it does with the number is held by its text.
      var spendOf = src.Substring(src.IndexOf("internal static StoredSpend SpendOf(", StringComparison.Ordinal), 700);
      Assert.Contains("long chars = 0", spendOf);
      Assert.Contains("Chars = chars,", spendOf);
    }

    private static int Count(string text, string what) => text.Split(new[] { what }, StringSplitOptions.None).Length - 1;

    [Theory]
    [InlineData("\"Reading the canvas…\", system, user, model, learnChars > 0 ? LargeRun.Learn : DwightVerbs.Explain, () =>")]
    [InlineData("\"Diagnosing…\", system, user, model, DwightVerbs.Diagnose, () =>")]
    [InlineData("\"Diagnosing this chain…\", system, userC, model, DwightVerbs.Diagnose, () =>")]
    [InlineData("\"This selection is large\", null, system, user, model, LargeRun.ToScript, () =>")]
    [InlineData("\"Rethinking the logic…\", system, user, model, DwightVerbs.Simplify, () =>")]
    [InlineData("AskSentence(ids.Count, ClusterLabeller.PromptChars(graph, macro), model, DwightVerbs.Reorganise, ctx.Doc)")]
    public void Each_ask_names_what_it_is_asking_about(string site) => Assert.Contains(site, Actions);

    [Fact]
    public void The_question_reads_the_ledger_once_and_a_setting_of_never_reads_nothing()
    {
      var src = Actions;
      var helper = src.Substring(src.IndexOf("private static string AskSentence(", StringComparison.Ordinal), 500);
      Assert.InRange(helper.IndexOf("if (threshold <= 0) return null;", StringComparison.Ordinal), 0, helper.IndexOf("SpendLedger.ReadSample()", StringComparison.Ordinal));
      // Brief DL: 2, the second being the estimate of a stopped request, which reads the same sample for its ratio.
      Assert.Equal(2, Count(src, "SpendLedger.ReadSample()"));

      var shim = Repo("Dwight", "Session", "SpendLedger.cs");
      Assert.Contains("SpendLedgerCore.ReadTail(LedgerPath, SpendLedgerCore.SampleBytes)", shim);
      Assert.Contains("catch { return LedgerSample.Empty; }", shim);
    }

    [Fact]
    public void Every_ceiling_stays_and_the_ask_comes_after_it()
    {
      var src = Actions;
      var at = 0;
      var ceilings = 0;
      while ((at = src.IndexOf("if (!WithinCeiling(system, user, model, out var ceiling))", at, StringComparison.Ordinal)) >= 0)
      {
        ceilings++;
        var ask = src.IndexOf("AskFirstIfLarge(", at, StringComparison.Ordinal);
        Assert.True(ask > at);
        Assert.True(ask - at < 400, "the ask should follow its ceiling directly");
        Assert.Contains("return; }", src.Substring(at, ask - at));   // a read that does not fit stops at its own sentence
        at = ask;
      }
      Assert.Equal(4, ceilings);
    }

    [Theory]
    [InlineData("Reading the canvas…", "ConverseProse(system, user, model, cancel)")]
    [InlineData("\"Diagnosing…\"", "Prompts.Prompts.DiagnoseToolName")]
    [InlineData("\"This selection is large\", null, system, user, model", "ToScriptAttempt(ctx, popup, graph, tsc, lang, model, system, user, attempt: 1")]
    [InlineData("\"Rethinking the logic…\", system, user, model", "StructuredOutput.Get(NewProvider(model), system, user,")]
    public void Each_verb_asks_before_its_paid_call(string ask, string call)
    {
      var src = Actions;
      var from = src.IndexOf("AskFirstIfLarge(", src.IndexOf(ask, StringComparison.Ordinal) - 120, StringComparison.Ordinal);
      Assert.True(from > 0, ask);
      Assert.True(src.IndexOf(call, from, StringComparison.Ordinal) > from, call);
      Assert.Contains("() =>", src.Substring(from, 240));   // the call sits in the closure Continue runs, not after it
    }

    [Fact]
    public void The_helper_goes_straight_on_only_when_there_is_nothing_to_ask()
    {
      var src = Actions;
      var helper = src.Substring(src.IndexOf("private static void AskFirstIfLarge(", StringComparison.Ordinal), 900);
      Assert.Contains("AskSentence(components, system.Length + user.Length + Math.Max(0, moreChars), model, verb, ctx?.Doc)", helper);   // Brief EO: a second request the run will send is counted too
      Assert.Contains("if (sentence == null) { go(); return; }", helper);
    }

    [Fact]
    public void A_chain_from_the_picker_asks_before_its_session_starts_and_cancel_goes_back_to_the_list()
    {
      var src = Actions;
      var at = src.IndexOf("wp.OnChainExplain = i =>", StringComparison.Ordinal);
      var ask = src.IndexOf("AskFirstIfLarge(null, wp, chainCount", at, StringComparison.Ordinal);
      Assert.True(ask > at);
      Assert.True(src.IndexOf("DwightSession.Activate(wp, TimeoutFor(model))", at, StringComparison.Ordinal) > ask);
      Assert.Contains("cancelled: () => wp.SetChains(BuildChainsListView(report, chains))", src.Substring(ask));
    }

    [Fact]
    public void Reorganise_asks_after_the_segmenter_and_before_the_labelling_call_and_only_with_a_key()
    {
      var src = Actions;
      var segment = src.IndexOf("new Segmenter().Segment(segInput).Macro", StringComparison.Ordinal);
      var ask = src.IndexOf("AskSentence(ids.Count, ClusterLabeller.PromptChars(graph, macro), model,", segment, StringComparison.Ordinal);
      var label = src.IndexOf("ClusterLabeller.Label(graph, macro, NewProvider(model), cancel)", segment, StringComparison.Ordinal);
      Assert.True(segment > 0 && ask > segment && label > ask);
      Assert.Contains("if (hasKey && !ctx.CostAnswered)", src.Substring(segment, ask - segment));
      // Waiting for the answer is not a running task: the session ends first, and Continue re-enters with the answer given.
      var said = src.Substring(ask, label - ask);
      Assert.Contains("DwightSession.Done(gen);", said);
      Assert.Contains("costAnswered: true", said);
    }

    [Fact]
    public void An_answer_survives_a_retry_and_a_fresh_run_asks_again()
    {
      var src = Actions;
      Assert.Contains("ctx.Rerun = () => ExplainOn(guids, mode, ctx.CostAnswered);", src);
      Assert.Contains("ctx.Rerun = () => DiagnoseOn(guids, ctx.CostAnswered);", src);
      Assert.Contains("again.CostAnswered = ctx.CostAnswered;", src);
      Assert.Contains("named: named, onScopeRefused: onScopeRefused, costAnswered: ctx.CostAnswered);", src);
      Assert.Contains("if (feedback != null) ctx.CostAnswered = true;", src);
      Assert.Contains("ExplainOn(guids, mode ?? ExplainModes.ForCount(guids.Count));", src);   // a menu click starts fresh: no answer is carried in
      Assert.Contains("public static void Diagnose() => DiagnoseOn(SelectedGuids());", src);
    }

    [Fact]
    public void Continue_will_not_abort_a_run_already_going()
    {
      var src = Actions;
      var ask = src.Substring(src.IndexOf("private static void AskLarge(", StringComparison.Ordinal), 700);
      Assert.InRange(ask.IndexOf("DwightSession.IsBusy", StringComparison.Ordinal), 0, ask.IndexOf("go();", StringComparison.Ordinal));
      Assert.Contains("AskLarge(popup, title, sentence, go, cancelled);", ask);
    }

    [Fact]
    public void Cancel_says_nothing_was_sent_and_writes_nothing()
    {
      var src = Actions;
      var ask = src.Substring(src.IndexOf("private static void AskLarge(", StringComparison.Ordinal), 900);
      Assert.Contains("popup.ShowState(\"notice\", LargeRun.CancelledTitle, LargeRun.CancelledSentence, null, null);", ask);
      Assert.DoesNotContain("Complete(", ask);
      Assert.DoesNotContain("SpendOf(", ask);
      Assert.DoesNotContain("Record(", ask);
      Assert.Equal("Nothing was sent", LargeRun.CancelledTitle);
      Assert.Equal("You cancelled before the read went out, so nothing was spent.", LargeRun.CancelledSentence);
    }

    [Fact]
    public void The_chat_is_not_in_this_brief()
    {
      Assert.DoesNotContain("LargeRun", Repo("Dwight", "Agent", "AgentTurnDriver.cs"));
      Assert.DoesNotContain("LargeRun", Repo("Dwight", "Agent", "AgentLoopCore.cs"));
      Assert.Contains("The chat does not ask.", Repo("Dwight", "Resources", "settings.html"));
    }

    // ---- the surfaces ----

    [Fact]
    public void The_panel_asks_in_the_notice_state_with_continue_and_cancel_each_taken_once()
    {
      var panel = Repo("Dwight", "UI", "WebPanel.cs");
      Assert.Contains("ShowState(\"notice\", title, sentence, Dwight.Session.LargeRun.ContinueLabel, \"large-run-continue\", Dwight.Session.LargeRun.CancelLabel, \"large-run-cancel\");", panel);
      Assert.Contains("else if (what == \"large-run-continue\") AnswerLargeRun(true);", panel);
      Assert.Contains("else if (what == \"large-run-cancel\") AnswerLargeRun(false);", panel);
      var answer = panel.Substring(panel.IndexOf("private void AnswerLargeRun(bool go)", StringComparison.Ordinal), 260);
      var invoked = answer.IndexOf("answer?.Invoke();", StringComparison.Ordinal);
      Assert.InRange(answer.IndexOf("_largeRunContinue = null;", StringComparison.Ordinal), 0, invoked);
      Assert.InRange(answer.IndexOf("_largeRunCancel = null;", StringComparison.Ordinal), 0, invoked);
      Assert.Equal("Continue", LargeRun.ContinueLabel);
      Assert.Equal("Cancel", LargeRun.CancelLabel);
    }

    [Fact]
    public void The_Eto_fallback_asks_in_a_plain_dialog_with_the_same_words()
    {
      var popup = Repo("Dwight", "UI", "ResultPopup.cs");
      Assert.Contains("public void AskToContinue(string title, string sentence, Action onContinue, Action onCancel)", popup);
      Assert.Contains("if (LargeRunDialog.Ask(this, title, sentence)) onContinue?.Invoke();", popup);
      Assert.Contains("else onCancel?.Invoke();", popup);

      var dialog = Repo("Dwight", "UI", "LargeRunDialog.cs");
      Assert.Contains("Text = sentence", dialog);
      Assert.Contains("new Button { Text = LargeRun.ContinueLabel }", dialog);
      Assert.Contains("new Button { Text = LargeRun.CancelLabel }", dialog);
      Assert.Contains("go.Click += (s, e) => Close(true);", dialog);
      Assert.Contains("cancel.Click += (s, e) => Close(false);", dialog);
      Assert.Contains("DefaultButton = cancel;", dialog);      // Enter never spends
      Assert.Contains("catch { return false; }", dialog);      // unanswered is not a yes
    }

    [Fact]
    public void The_Setting_sits_with_Usage_defaults_to_25_cents_and_keeps_zero()
    {
      var html = Repo("Dwight", "Resources", "settings.html");
      var usage = html.IndexOf("data-pane=\"Usage\"", StringComparison.Ordinal);
      var field = html.IndexOf("<label>Ask above</label><div class=\"dd\" id=\"askover\" data-vals=\"0 5 10 25 50 100 200\">", StringComparison.Ordinal);
      Assert.True(usage > 0 && field > usage);
      Assert.Contains("<div class=\"opt\">Never: ", html);
      Assert.Contains("<div class=\"opt\">25 cents: the default.</div>", html);

      // Zero is a real answer, so no "||" fallback may read it as the default.
      Assert.Contains("choice('askover','Ask above',String(d.askOverCents),setAskOver);", html);
      Assert.Contains("function setAskOver(v){ dwightSend('askover:'+v); }", html);

      Assert.Equal(25, LargeRun.DefaultThresholdCents);
      Assert.Contains("public int AskBeforeRunOverCents { get; set; } = Dwight.Session.LargeRun.DefaultThresholdCents;", Repo("Dwight", "Config", "DwightSettings.cs"));
      var panel = Repo("Dwight", "UI", "DwightSettingsPanel.cs");
      Assert.Contains("[\"askOverCents\"] = Math.Max(0, s.AskBeforeRunOverCents),", panel);
      Assert.Contains("cmd.StartsWith(\"askover:\", StringComparison.Ordinal)", panel);
      Assert.Contains("st.AskBeforeRunOverCents = Math.Max(0, askOver); st.Save();", panel);
    }

    [Fact]
    public void The_hint_says_what_the_estimate_is_and_is_not()
    {
      var html = Repo("Dwight", "Resources", "settings.html");
      Assert.Contains("Ask before a verb run whose estimated cost is at or above this; Never does not ask; an unpriced model asks at the size that would cost this on Claude Sonnet 5.", html);
      Assert.Contains("the read and the answer, from the read's size, the catalogue's price and what your recent runs measured.", html);
      Assert.DoesNotContain("does not count the reply", html);
    }
  }
}
