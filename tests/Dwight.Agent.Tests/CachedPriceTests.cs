using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Dwight.Models;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>The catalogue's cached-input prices, read from each vendor's own page on 31 October 2026, and the cost that reads them. Most of each chat step's input is
  /// cached, so these decide whether the number a person sees is right.</summary>
  public class CachedPriceTests
  {
    private static JsonNode Catalogue => JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "models.json")));

    private static JsonNode Row(string id) => Catalogue["models"].AsArray().Single(m => m["id"].GetValue<string>() == id);

    private static string Repo(params string[] parts)
    {
      var dir = new DirectoryInfo(AppContext.BaseDirectory);
      while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Dwight.sln"))) dir = dir.Parent;
      Assert.NotNull(dir);
      return File.ReadAllText(Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray()));
    }

    [Theory]
    [InlineData("gpt-6-luna", 0.01, 0.125)]
    [InlineData("gpt-5.6-luna", 0.02, 0.25)]
    [InlineData("gpt-5.6-terra", 0.20, 2.50)]
    [InlineData("gpt-6.1-sol", 0.10, 2.50)]
    [InlineData("gpt-5.6-sol", 0.40, 5.00)]
    [InlineData("gpt-6-astra", 1.00, 12.50)]
    [InlineData("claude-haiku-5-5", 0.01, 0.125)]
    [InlineData("claude-haiku-4-5", 0.10, 1.25)]
    [InlineData("claude-sonnet-5", 0.20, 2.50)]
    [InlineData("claude-opus-5", 0.50, 6.25)]
    [InlineData("claude-sonnet-4-6", 0.30, 3.75)]
    [InlineData("claude-opus-4-8", 0.50, 6.25)]
    public void A_row_with_a_write_price_says_both_from_the_vendors_page(string id, double read, double write)
    {
      var m = Row(id);
      Assert.Equal(read, m["priceCachedIn"].GetValue<double>());
      Assert.Equal(write, m["priceCacheWrite"].GetValue<double>());
      Assert.Contains("read 2026-10-31", m["cachePriceSource"].GetValue<string>());
    }

    [Theory]
    [InlineData("gemini-3.5-flash-lite", 0.03)]
    [InlineData("gemini-3.8-flash", 0.075)]
    [InlineData("gemini-3.6-flash", 0.075)]
    [InlineData("gemini-4-argon", 0.10)]
    [InlineData("gemini-3.1-pro-preview", 0.20)]
    public void Googles_implicit_cache_has_a_read_price_and_bills_no_write(string id, double read)
    {
      var m = Row(id);
      Assert.Equal(read, m["priceCachedIn"].GetValue<double>());
      Assert.Null(m["priceCacheWrite"]);
      Assert.Contains("read 2026-10-31", m["cachePriceSource"].GetValue<string>());
    }

    [Fact]
    public void Every_paid_row_on_the_three_vendors_is_covered_and_a_time_limited_price_names_its_end()
    {
      foreach (var m in Catalogue["models"].AsArray().Where(x => new[] { "OpenAI", "Anthropic", "Google" }.Contains(x["provider"].GetValue<string>())))
      {
        var read = m["priceCachedIn"];
        Assert.True(read != null, m["id"] + " has no cached price");
        Assert.True(read.GetValue<double>() < m["priceIn"].GetValue<double>(), m["id"] + ": a cache hit costs less than input");
      }
      Assert.Contains("through 2026-12-31, then $0.15", Row("gemini-3.8-flash")["cachePriceSource"].GetValue<string>());
      Assert.Contains("through 2026-11-30, then $0.20", Row("gemini-4-argon")["cachePriceSource"].GetValue<string>());
      Assert.True(Catalogue["catalogueVersion"].GetValue<int>() >= 11);
    }

    [Fact]
    public void The_cost_reads_the_rows_price_and_keeps_the_ratio_only_where_a_row_has_none()
    {
      var stated = new ModelInfo("m", "M", "OpenAI", "Strong", 2.0, 10.0, 1000, cacheReadPerMTok: 0.10, cacheWritePerMTok: 2.50);
      Assert.Equal(0.10, stated.CacheReadPerMTok);
      Assert.Equal(2.50, stated.CacheWritePerMTok);

      var none = new ModelInfo("m", "M", "OpenAI", "Strong", 2.0, 10.0, 1000);
      Assert.Equal(0.20, none.CacheReadPerMTok, 10);
      Assert.Equal(2.50, none.CacheWritePerMTok, 10);
    }

    /// <summary>GPT-6.1 Sol's read answer from the gate's capture: 9332 input, of which 8993 cached and 336 written, and 30 output. The ratio read the cached part at twice its price.</summary>
    [Fact]
    public void A_real_step_on_GPT_6_1_Sol_costs_what_the_page_says()
    {
      var sol = new ModelInfo("gpt-6.1-sol", "Sol", "OpenAI", "Strong", 2.0, 10.0, 1000, cacheReadPerMTok: 0.10, cacheWritePerMTok: 2.50);
      var byHand = (3 * 2.0 + 30 * 10.0 + 8993 * 0.10 + 336 * 2.50) / 1_000_000.0 * 100.0;
      Assert.Equal(byHand, sol.CostCents(3, 30, 8993, 336), 10);
      var byRatio = new ModelInfo("r", "R", "OpenAI", "Strong", 2.0, 10.0, 1000).CostCents(3, 30, 8993, 336);
      Assert.True(byRatio > sol.CostCents(3, 30, 8993, 336));
    }

    [Fact]
    public void The_catalogue_carries_the_prices_to_the_row_and_the_one_cost_uses_them()
    {
      Assert.Contains("cacheReadPerMTok: e.priceCachedIn, cacheWritePerMTok: e.priceCacheWrite", Repo("Dwight", "Models", "ModelCatalogue.cs"));
      Assert.Contains("model.CostCents(u.InputTokens, u.OutputTokens, u.CacheReadInputTokens, u.CacheCreationInputTokens)", Repo("Dwight", "LlmActions.cs"));
    }

    /// <summary>A past line keeps what it was written with: the record holds cents, and nothing that reads the record or the ledger knows a price.</summary>
    [Theory]
    [InlineData("Spend.cs")]
    [InlineData("SpendLedgerCore.cs")]
    [InlineData("SpendLedger.cs")]
    public void Past_ledger_lines_are_never_repriced(string file)
    {
      var src = Repo("Dwight", "Session", file);
      Assert.DoesNotContain("PerMTok", src);
      Assert.DoesNotContain("CostCents", src);
      Assert.DoesNotContain("ModelCatalogue", src);
    }
  }
}
