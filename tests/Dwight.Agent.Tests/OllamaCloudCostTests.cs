using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dwight.Session;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>Ollama Cloud sells pay-as-you-go as well as plans, and Dwight cannot tell which a key is on, so nothing the product says about
  /// a turn may claim a subscription covers it. These pin the words in every place they appear. A stored row keeps what it wrote.</summary>
  public class OllamaCloudCostTests
  {
    private const string Short = "Ollama Cloud · cost depends on your plan";
    private const string Sentence = "Ollama Cloud: Dwight cannot see what this cost, because it depends on your plan.";

    private static string Repo(params string[] parts)
    {
      var dir = new DirectoryInfo(AppContext.BaseDirectory);
      while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Dwight.sln"))) dir = dir.Parent;
      Assert.NotNull(dir);
      return File.ReadAllText(Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray()));
    }

    [Fact]
    public void The_words_are_these_and_they_hold_on_every_plan()
    {
      Assert.Equal(Short, SpendMath.OllamaCloudCostText);
      Assert.Equal("cost depends on your plan", SpendMath.OllamaCloudCostShort);
      Assert.Equal("by your plan", SpendMath.OllamaCloudCostPick);
      Assert.Equal(Sentence, SpendMath.OllamaCloudCostSentence);
    }

    [Fact]
    public void The_row_and_the_stored_spend_use_the_words_and_local_stays_free()
    {
      var src = Repo("Dwight", "LlmActions.cs");
      Assert.Contains("else if (model.IsSubscription) sp.CostText = SpendMath.OllamaCloudCostText;", src);
      Assert.Contains("else if (model.IsSubscription) costText = SpendMath.OllamaCloudCostText;", src);
      Assert.DoesNotContain("· subscription", src);
      Assert.Contains("if (model.IsLocal) sp.CostText = \"local · free\";", src);
      Assert.Contains("if (model.IsLocal) costText = \"local · free\";", src);
    }

    [Fact]
    public void The_model_dropdown_uses_the_words_and_local_stays_free()
    {
      var src = Repo("Dwight", "UI", "DwightSettingsPanel.cs");
      Assert.Contains("m.IsLocal ? \" · free\" : m.IsSubscription ? \" · \" + Dwight.Session.SpendMath.OllamaCloudCostPick", src);
      Assert.DoesNotContain("· subscription", src);
    }

    [Fact]
    public void The_footer_says_the_short_words()
    {
      var t = SpendMath.Sum(new[] { new StoredSpend { InputTokens = 41000, OutputTokens = 6000, CostText = Short } });
      Assert.Equal("1 turn · " + Short + " · 41k in / 6k out", SpendMath.ChatLine(t));
    }

    [Fact]
    public void The_hover_says_the_whole_sentence_on_a_turn_just_run_and_on_a_reopened_chat()
    {
      // A turn just run: the meta line the row shows, cost line first.
      Assert.Equal("GLM-5.3 (cloud) · 7.8s · cached · 3 steps · " + Sentence,
        SpendMath.CostHover("GLM-5.3 (cloud) · 7.8s · " + Short + " · cached · 3 steps"));
      // A reopened chat: the same facts read back from the store.
      var stored = new StoredSpend { ElapsedMs = 7800, CostText = Short, Steps = 3, ToolCalls = 2, InputTokens = 118000, OutputTokens = 1100 };
      Assert.Equal("GLM-5.3 (cloud) · 7.8s · 3 steps · 2 tool calls · 118k in / 1.1k out · " + Sentence,
        SpendMath.CostHover(SpendMath.TurnReceipt(stored, "GLM-5.3 (cloud)")));
      Assert.Equal(Sentence, SpendMath.CostHover(Short));
      Assert.Equal("3 steps · " + Sentence, SpendMath.CostHover(Short + " · 3 steps"));
      // Both paths go through it.
      var scaffold = Repo("Dwight", "UI", "DwightScaffold.cs");
      Assert.Contains("out var held)) return SpendMath.CostHover(held);", scaffold);
      Assert.Contains("return SpendMath.CostHover(SpendMath.TurnReceipt(last, name));", scaffold);
    }

    [Theory]
    [InlineData("GLM-5.3 (cloud) · 7.8s · ~7.0¢ · 3 steps")]
    [InlineData("1.0s · local · free · 1 step")]
    [InlineData("GLM-5.3 (cloud) · 7.8s · Ollama Cloud · subscription · 3 steps")]   // a past row keeps what it wrote
    [InlineData("")]
    public void The_hover_leaves_every_other_line_as_it_was(string line) => Assert.Equal(line, SpendMath.CostHover(line));

    [Fact]
    public void A_stored_row_keeps_the_words_it_was_written_with()
    {
      var past = JsonSerializer.Deserialize<StoredSpend>("{\"model\":\"glm-5.3:cloud\",\"elapsedMs\":1000,\"costText\":\"Ollama Cloud · subscription\",\"steps\":1}");
      Assert.Equal("Ollama Cloud · subscription", past.CostText);
      Assert.Contains("\"costText\":\"Ollama Cloud \\u00B7 subscription\"", JsonSerializer.Serialize(past));
      Assert.Equal("1.0s · Ollama Cloud · subscription · 1 step", SpendMath.TurnReceipt(past, null));
    }

    [Fact]
    public void The_ledger_goes_by_the_catalogues_own_provider_value()
    {
      Assert.Contains("ProviderOllamaCloud = \"" + SpendMath.OllamaCloudProvider + "\"", Repo("Dwight", "DwightConfig.cs"));
      var catalogue = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "models.json")));
      var rows = catalogue["models"].AsArray().Where(m => m["pricingModel"]?.GetValue<string>() == "subscription").ToList();
      Assert.NotEmpty(rows);
      Assert.All(rows, m => Assert.Equal(SpendMath.OllamaCloudProvider, m["provider"].GetValue<string>()));
    }

    [Fact]
    public void The_public_page_says_it_and_no_longer_says_covered()
    {
      var page = Repo("docs", "public", "providers-and-keys.md");
      Assert.Contains("Ollama Cloud says that Dwight cannot see what a run cost, because it depends on your plan.", page);
      Assert.DoesNotContain("covered by its subscription", page);
      Assert.Contains("A local model shows as free.", page);
    }
  }
}
