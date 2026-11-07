using System;
using System.Collections.Generic;
using System.Linq;
using Dwight;
using Dwight.Models;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// Ruling #199 (secondary) - model selection must be a function of the DATA, never of its layout.
  ///
  /// THE FRAGILITY THIS CLOSES. Catalogue v2 put two models in the Strong band at an identical price sum
  /// (Sonnet 5 and Sonnet 4.6, both $3/$15). The comparison ordered by band distance then price, so that
  /// tie fell through to whatever order the entries sat in the JSON file - Sonnet 5 won only by being on
  /// the earlier line. Reordering two lines in a data file, an edit nobody would think twice about and
  /// one a USER may make in their own copy, would have silently moved every Strong-band task onto a
  /// different model. Nothing would error. The bill and the behaviour would just change.
  /// </summary>
  public class ModelPickTests
  {
    private static ModelInfo M(string id, string tier, double inPer, double outPer, int? preference = null) =>
      new ModelInfo(id, id, "Anthropic", tier, inPer, outPer, 1_000_000, structured: true, preference: preference);

    /// <summary>The real shape of the shipped Anthropic band, prices and preferences as catalogued.</summary>
    private static List<ModelInfo> Anthropic() => new List<ModelInfo>
    {
      M("claude-haiku-4-5", "Fast", 1.0, 5.0),
      M("claude-sonnet-5", "Strong", 3.0, 15.0, preference: 10),
      M("claude-opus-5", "Top", 5.0, 25.0, preference: 10),
      M("claude-sonnet-4-6", "Strong", 3.0, 15.0, preference: 50),
      M("claude-opus-4-8", "Top", 5.0, 25.0, preference: 50),
    };

    // ---- the tie, settled explicitly ---------------------------------------------------------------

    [Fact]
    public void A_strong_comprehension_task_resolves_to_sonnet_5()
    {
      // Diagnose is Strong->Strong (ADR-082), and this is the model that must win it.
      var picked = ModelPick.Choose(Anthropic(), target: "Strong", floor: "Strong", ceiling: "Strong", structuredOnly: true);
      Assert.Equal("claude-sonnet-5", picked.Id);
    }

    [Fact]
    public void THE_CHOICE_SURVIVES_REORDERING_THE_CATALOGUE()
    {
      // The property that matters: same data, any order, same answer. Every permutation of the tied
      // pair - and the whole list reversed - must resolve identically.
      var forward = ModelPick.Choose(Anthropic(), "Strong", "Strong", "Strong", true);

      var reversed = Anthropic();
      reversed.Reverse();
      Assert.Equal(forward.Id, ModelPick.Choose(reversed, "Strong", "Strong", "Strong", true).Id);

      var shuffled = Anthropic().OrderBy(m => m.Id, StringComparer.Ordinal).ToList();
      Assert.Equal(forward.Id, ModelPick.Choose(shuffled, "Strong", "Strong", "Strong", true).Id);
    }

    [Fact]
    public void Preference_only_breaks_a_tie_and_never_outranks_price()
    {
      // ADR-082 keeps price primary (the profile is a spend dial). A strongly-preferred expensive model
      // must NOT beat a cheaper one in the same band - preference is a tie-break, not a thumb on the scale.
      var models = new List<ModelInfo>
      {
        M("cheap", "Strong", 1.0, 1.0, preference: 90),
        M("dear",  "Strong", 9.0, 9.0, preference: 1),
      };
      Assert.Equal("cheap", ModelPick.Choose(models, "Strong", "Strong", "Strong", true).Id);
    }

    [Fact]
    public void With_no_preference_stated_the_id_still_gives_a_total_order()
    {
      // Two entries tied on band, price AND preference must still resolve deterministically rather than
      // falling back to list position.
      var a = new List<ModelInfo> { M("bbb", "Mid", 1, 1), M("aaa", "Mid", 1, 1) };
      var b = new List<ModelInfo> { M("aaa", "Mid", 1, 1), M("bbb", "Mid", 1, 1) };
      Assert.Equal(ModelPick.Choose(a, "Mid", "Mid", "Mid", true).Id,
                   ModelPick.Choose(b, "Mid", "Mid", "Mid", true).Id);
    }

    // ---- band behaviour (unchanged, guarded) -------------------------------------------------------

    [Fact]
    public void A_task_picks_within_its_band()
    {
      Assert.Equal("claude-haiku-4-5", ModelPick.Choose(Anthropic(), "Fast", "Fast", "Fast", true).Id);
      Assert.Equal("claude-opus-5", ModelPick.Choose(Anthropic(), "Top", "Top", "Top", true).Id);
    }

    [Fact]
    public void An_empty_band_falls_back_to_the_nearest_rather_than_returning_nothing()
    {
      // The Ollama-Cloud Fast case after a red entry: no model in band, so the nearest is used and the
      // task still runs.
      var noFast = Anthropic().Where(m => m.Tier != "Fast").ToList();
      var picked = ModelPick.Choose(noFast, "Fast", "Fast", "Fast", true);
      Assert.NotNull(picked);
      Assert.Equal("Strong", picked.Tier);   // nearest band up
    }

    [Fact]
    public void The_structured_filter_is_honoured()
    {
      var models = new List<ModelInfo>
      {
        new ModelInfo("loose", "loose", "Anthropic", "Strong", 1, 1, 1000, structured: false),
        new ModelInfo("strict", "strict", "Anthropic", "Strong", 5, 5, 1000, structured: true),
      };
      Assert.Equal("strict", ModelPick.Choose(models, "Strong", "Strong", "Strong", structuredOnly: true).Id);
      Assert.Equal("loose", ModelPick.Choose(models, "Strong", "Strong", "Strong", structuredOnly: false).Id);
    }

    [Fact]
    public void An_empty_or_null_pool_returns_null_rather_than_throwing()
    {
      Assert.Null(ModelPick.Choose(new List<ModelInfo>(), "Mid", "Fast", "Top", true));
      Assert.Null(ModelPick.Choose(null, "Mid", "Fast", "Top", true));
    }

    /// <summary>U52: a pin is one id for every provider, so it only counts under the provider it belongs to. This is the
    /// rule BOTH the Settings provider switch (clears the pin) and DwightSettings.ResolveModel (ignores a stale one) read:
    /// GPT-5.6 Terra pinned under OpenAI, provider switched to Google, was sent to Google and came back a 404.</summary>
    [Fact]
    public void A_pin_belongs_only_to_its_own_provider()
    {
      var terra = new ModelInfo("gpt-5.6-terra", "GPT-5.6 Terra", "OpenAI", "Mid", 1, 1, 100000, structured: true);

      Assert.True(ModelPick.PinBelongsTo(terra, "OpenAI"));
      Assert.False(ModelPick.PinBelongsTo(terra, "Google"));
      Assert.False(ModelPick.PinBelongsTo(terra, "Anthropic"));
      Assert.False(ModelPick.PinBelongsTo(null, "OpenAI"));
    }

    [Fact]
    public void The_cleared_pin_sentence_names_the_model_and_its_provider()
    {
      var terra = new ModelInfo("gpt-5.6-terra", "GPT-5.6 Terra", "OpenAI", "Mid", 1, 1, 100000, structured: true);
      var gem = new ModelInfo("gemini-x", "Gemini X", "Google", "Mid", 1, 1, 100000, structured: true);

      Assert.Equal("Pin cleared: GPT-5.6 Terra is an OpenAI model", ModelPick.PinClearedMessage(terra));
      Assert.Equal("Pin cleared: Gemini X is a Google model", ModelPick.PinClearedMessage(gem));
    }

    [Fact]
    public void An_unknown_tier_reads_as_Mid_rather_than_throwing()
    {
      Assert.Equal(ModelPick.Idx("Mid"), ModelPick.Idx("nonsense"));
    }

    // ---- U72: the Scaffold chat is resolved among the models that can run it ----------------------------------

    private static List<ModelInfo> Shipped(string provider)
    {
      var catalogue = (System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse(
        System.IO.File.ReadAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "models.json")));
      return catalogue["models"].AsArray()
        .Where(m => m["provider"].GetValue<string>() == provider)
        .Select(m =>
        {
          var caps = m["capabilities"];
          return new ModelInfo(m["id"].GetValue<string>(), m["displayName"].GetValue<string>(), provider, m["tier"].GetValue<string>(),
            m["priceIn"].GetValue<double>(), m["priceOut"].GetValue<double>(), m["contextWindow"].GetValue<int>(),
            structured: caps?["structuredOutput"]?.GetValue<bool>() ?? false,
            agentic: caps?["agentic"]?.GetValue<bool>() ?? false,
            preference: m["preference"]?.GetValue<int>());
        }).ToList();
    }

    /// <summary>The Scaffold policy (Strong..Top, ModelResolver.Policy) applied the way ModelResolver applies it.</summary>
    private static ModelInfo ChatModel(string provider, bool economy)
    {
      var floor = "Strong";
      var pool = ModelPick.AgentPool(Shipped(provider), economy, ref floor);
      return ModelPick.Choose(pool, floor, floor, "Top", structuredOnly: true);
    }

    [Fact]
    public void U72_On_OpenAI_with_no_pin_the_economy_chat_resolves_to_the_lowest_agentic_band_GPT_5_6_Terra()
    {
      Assert.Equal("gpt-5.6-terra", ChatModel("OpenAI", economy: true).Id);
      // Economy thinks with reasoning.effort none, which GPT-6.1 Sol and Astra refuse: they must not be what Economy resolves to, and Terra being the lowest band is why.
      Assert.False(ChatModel("OpenAI", economy: true).Id == "gpt-6.1-sol" || ChatModel("OpenAI", economy: true).Id == "gpt-6-astra");
    }

    [Theory]
    [InlineData("OpenAI", false)]
    [InlineData("OpenAI", true)]
    [InlineData("Google", false)]
    [InlineData("Google", true)]
    [InlineData("Anthropic", false)]
    [InlineData("OllamaLocal", false)]
    [InlineData("OllamaCloud", true)]
    public void U72_Every_profile_on_a_provider_with_an_agentic_row_resolves_to_an_agentic_model(string provider, bool economy)
    {
      var m = ChatModel(provider, economy);
      Assert.NotNull(m);
      Assert.True(m.Agentic, provider + " resolved " + m.Id);
    }

    [Fact]
    public void What_each_profile_gets_on_OpenAI_since_the_two_models_passed()
    {
      var floor = "Strong";
      var pool = ModelPick.AgentPool(Shipped("OpenAI"), false, ref floor);
      Assert.Equal("gpt-6.1-sol", ModelPick.Choose(pool, "Strong", "Strong", "Top", true).Id);   // Balanced
      Assert.Equal("gpt-6-astra", ModelPick.Choose(pool, "Top", "Strong", "Top", true).Id);      // Quality and Ultra reach the ceiling, and Top now has a row
    }

    [Fact]
    public void U72_The_dial_still_means_something_on_the_chat()
    {
      Assert.Equal("gpt-6.1-sol", ChatModel("OpenAI", economy: false).Id);   // Strong's cheapest since it passed its gate on 31 October 2026, at half GPT-5.6 Sol's price
    }

    [Fact]
    public void U72_A_provider_with_no_agentic_row_gets_its_whole_list_so_the_refusal_is_unchanged()
    {
      var none = new List<ModelInfo>
      {
        new ModelInfo("a", "A", "X", "Mid", 1, 1, 1000, structured: true),
        new ModelInfo("b", "B", "X", "Strong", 2, 2, 1000, structured: true),
      };
      var floor = "Strong";
      var pool = ModelPick.AgentPool(none, economy: true, ref floor);
      Assert.Same(none, pool);
      Assert.Equal("Strong", floor);
      Assert.False(ModelPick.Choose(pool, floor, floor, "Top", true).Agentic);   // AgentEligibility.Check refuses this one
    }

    [Fact]
    public void U72_Only_the_Scaffold_task_takes_the_agentic_pool_and_a_pin_is_returned_before_the_resolver()
    {
      var root = AppContext.BaseDirectory;
      var dir = new System.IO.DirectoryInfo(root);
      while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "Dwight.sln"))) dir = dir.Parent;
      var resolver = System.IO.File.ReadAllText(System.IO.Path.Combine(dir.FullName, "Dwight", "Models", "ModelResolver.cs"));
      Assert.Contains("if (task == DwightTask.Scaffold) all = ModelPick.AgentPool(", resolver);
      Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(resolver, @"AgentPool\(").Count);

      var settings = System.IO.File.ReadAllText(System.IO.Path.Combine(dir.FullName, "Dwight", "Config", "DwightSettings.cs"));
      Assert.True(settings.IndexOf("PinBelongsTo(o, EffectiveProvider)", StringComparison.Ordinal)
                  < settings.IndexOf("ModelResolver.Resolve(", StringComparison.Ordinal));   // the pin wins, unsubstituted
    }
  }
}
