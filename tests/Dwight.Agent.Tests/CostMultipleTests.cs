using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dwight.Agent;
using Dwight.Models;
using Dwight.Session;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// Brief CW (Tharun's ruling of 1 November 2026): Balanced stays the chat's default, and any other choice says its price against Balanced, per token, from the catalogue. Read on
  /// the shipped file through the real resolver, not a fixture: the sentences have to be true of the rows a person can reach.
  /// </summary>
  public class CostMultipleTests
  {
    private static readonly JsonNode Catalogue = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "models.json")));

    private static ModelInfo FromJson(JsonNode m)
    {
      var c = m["capabilities"];
      return new ModelInfo(m["id"].GetValue<string>(), m["displayName"].GetValue<string>(), m["provider"].GetValue<string>(), m["tier"].GetValue<string>(),
        m["priceIn"].GetValue<double>(), m["priceOut"].GetValue<double>(), m["contextWindow"].GetValue<int>(),
        pricingModel: m["pricingModel"]?.GetValue<string>(), status: m["status"]?.GetValue<string>(),
        structured: c?["structuredOutput"]?.GetValue<bool>() ?? false, agentic: c?["agentic"]?.GetValue<bool>() ?? false,
        reasoning: c?["reasoning"]?.GetValue<bool>() ?? false, reasoningKnob: c?["reasoningKnob"]?.GetValue<bool>() ?? false, thinkingDefault: c?["thinkingDefault"]?.GetValue<string>(),
        preference: m["preference"]?.GetValue<int>(), pinOnly: c?["pinOnly"]?.GetValue<bool>() ?? false, retentionDays: c?["retentionDays"]?.GetValue<int>() ?? 0);
    }

    private static IReadOnlyList<ModelInfo> Shipped(string provider) =>
      Catalogue["models"].AsArray().Where(m => m["provider"].GetValue<string>() == provider).Select(FromJson).ToList();

    private static ModelInfo Row(string id) => FromJson(Catalogue["models"].AsArray().Single(m => m["id"].GetValue<string>() == id));

    private static IEnumerable<string> Providers => Catalogue["models"].AsArray().Select(m => m["provider"].GetValue<string>()).Distinct();

    /// <summary>What the chat resolves a posture to on a provider: the arguments are the ones DwightSettings passes, pinned below.</summary>
    private static ModelInfo Resolve(string provider, string posture) =>
      ModelResolver.Resolve(DwightTask.Scaffold, TaskScope.Any, false, posture, Shipped(provider)).Model;

    private static ModelInfo Balanced(string provider) => Resolve(provider, "balanced");

    private static IReadOnlyDictionary<string, ModelInfo> PostureModels(string provider) =>
      ChatHeader.Postures.ToDictionary(p => p.Id, p => Resolve(provider, p.Id));

    private static ModelInfo Priced(double sum, string id = "p") => new ModelInfo(id, "Model " + id, "X", "Strong", sum, 0, 0);

    private const string AboutTwo = "about 2 times Balanced's price per token";
    private const string AboutFive = "about 5 times Balanced's price per token";

    // ---- the shipped catalogue --------------------------------------------------------------------------------------

    [Fact]
    public void On_Anthropic_Balanced_is_Sonnet_55_and_nothing_is_said_against_itself()
    {
      Assert.Equal("claude-sonnet-5-5", Balanced("Anthropic").Id);
      Assert.Null(ChatHeader.PriceLine(Balanced("Anthropic"), Balanced("Anthropic")));
      Assert.Null(ChatHeader.PriceLine(new ModelInfo("CLAUDE-SONNET-5-5", "S", "Anthropic", "Strong", 9, 9, 0), Balanced("Anthropic")));   // an id matches as the catalogue's Find does, whatever the case
    }

    [Theory]
    [InlineData("quality")]
    [InlineData("ultra")]
    public void On_Anthropic_Quality_and_Ultra_are_Opus_55_at_about_2_times(string posture)
    {
      Assert.Equal("claude-opus-5-5", Resolve("Anthropic", posture).Id);
      Assert.Equal(AboutTwo, ChatHeader.PriceLine(Resolve("Anthropic", posture), Balanced("Anthropic")));
    }

    [Fact]
    public void Fable_51_is_about_5_times() =>
      Assert.Equal(AboutFive, ChatHeader.PriceLine(Row("claude-fable-5-1"), Balanced("Anthropic")));

    [Fact]
    public void On_Anthropic_Economy_resolves_to_the_Balanced_model_so_it_has_no_line()
    {
      Assert.Equal("claude-sonnet-5-5", Resolve("Anthropic", "economy").Id);
      Assert.Null(ChatHeader.PriceLine(Resolve("Anthropic", "economy"), Balanced("Anthropic")));
    }

    [Fact]
    public void On_OpenAI_Economy_costs_more_than_Balanced_and_the_line_says_so()
    {
      Assert.Equal("gpt-5.6-terra", Resolve("OpenAI", "economy").Id);
      Assert.Equal("gpt-6.1-sol", Balanced("OpenAI").Id);
      Assert.Equal("about 1.2 times Balanced's price per token", ChatHeader.PriceLine(Resolve("OpenAI", "economy"), Balanced("OpenAI")));
      Assert.Equal(AboutFive, ChatHeader.PriceLine(Resolve("OpenAI", "quality"), Balanced("OpenAI")));
    }

    [Theory]
    [InlineData("claude-sonnet-5", "claude-sonnet-5-5", ChatHeader.SamePriceLine)]
    [InlineData("claude-sonnet-4-6", "claude-sonnet-5-5", "about 1.5 times Balanced's price per token")]
    [InlineData("claude-opus-5", "claude-sonnet-5-5", "about 2.5 times Balanced's price per token")]
    [InlineData("claude-haiku-4-5", "claude-sonnet-5-5", "about half Balanced's price per token")]
    [InlineData("claude-haiku-5-5", "claude-sonnet-5-5", "less than half Balanced's price per token")]
    [InlineData("gemini-4-argon", "gemini-3.8-flash", "about 2.7 times Balanced's price per token")]
    [InlineData("gemini-3.1-pro-preview", "gemini-3.8-flash", "about 3 times Balanced's price per token")]
    [InlineData("gemini-3.5-flash-lite", "gemini-3.8-flash", "a little less than Balanced per token")]
    public void The_shipped_rows_read_as_their_prices_say(string id, string balanced, string line) =>
      Assert.Equal(line, ChatHeader.PriceLine(Row(id), Row(balanced)));

    [Fact]
    public void An_Ollama_local_row_says_nothing_is_billed_and_every_local_posture_is_the_Balanced_model()
    {
      Assert.Equal("qwen3.5:9b", Balanced("OllamaLocal").Id);
      Assert.Equal("runs on this machine, nothing is billed", ChatHeader.PriceLine(Row("gemma4:12b"), Balanced("OllamaLocal")));
      foreach (var p in ChatHeader.Postures) Assert.Null(ChatHeader.PriceLine(Resolve("OllamaLocal", p.Id), Balanced("OllamaLocal")));
    }

    [Fact]
    public void An_Ollama_Cloud_row_says_its_plan_and_never_that_it_runs_on_this_machine()
    {
      Assert.Equal("glm-5.3:cloud", Balanced("OllamaCloud").Id);
      Assert.Equal("kimi-k3:cloud", Resolve("OllamaCloud", "quality").Id);
      Assert.Equal(SpendMath.OllamaCloudCostShort, ChatHeader.PriceLine(Resolve("OllamaCloud", "quality"), Balanced("OllamaCloud")));
      foreach (var m in Shipped("OllamaCloud")) Assert.NotEqual(ChatHeader.NotBilledLine, ChatHeader.PriceLine(m, Balanced("OllamaCloud")));
    }

    [Fact]
    public void A_provider_whose_Balanced_is_also_its_Quality_gives_Quality_no_line()
    {
      var only = new[] { new ModelInfo("s", "S", "X", "Strong", 2, 10, 0, structured: true, agentic: true) };
      var balanced = ModelResolver.Resolve(DwightTask.Scaffold, TaskScope.Any, false, "balanced", only).Model;
      var quality = ModelResolver.Resolve(DwightTask.Scaffold, TaskScope.Any, false, "quality", only).Model;
      Assert.Same(balanced, quality);
      Assert.Null(ChatHeader.PriceLine(quality, balanced));
    }

    // ---- one test per branch of the rounding ------------------------------------------------------------------------

    [Theory]
    [InlineData(1.0, "the same price per token as Balanced")]
    [InlineData(1.04, "a little more than Balanced per token")]   // not "about 1 times"
    [InlineData(1.17, "about 1.2 times Balanced's price per token")]
    [InlineData(2.0, "about 2 times Balanced's price per token")]   // not "2.0"
    [InlineData(2.67, "about 2.7 times Balanced's price per token")]
    [InlineData(2.96, "about 3 times Balanced's price per token")]
    [InlineData(3.0, "about 3 times Balanced's price per token")]
    [InlineData(3.11, "about 3 times Balanced's price per token")]
    [InlineData(3.6, "about 4 times Balanced's price per token")]
    [InlineData(5.0, "about 5 times Balanced's price per token")]
    [InlineData(0.99, "a little less than Balanced per token")]
    [InlineData(0.6, "a little less than Balanced per token")]
    [InlineData(0.59, "about half Balanced's price per token")]
    [InlineData(0.4, "about half Balanced's price per token")]
    [InlineData(0.39, "less than half Balanced's price per token")]
    [InlineData(0.05, "less than half Balanced's price per token")]
    public void Each_branch_of_the_rounding(double times, string line) =>
      Assert.Equal(line, ChatHeader.PriceLine(Priced(times), Priced(1.0, "b")));

    [Fact]
    public void The_sum_is_input_plus_output_the_one_the_resolver_orders_by()
    {
      Assert.Equal(AboutTwo, ChatHeader.PriceLine(new ModelInfo("a", "A", "X", "Top", 1, 3, 0), new ModelInfo("b", "B", "X", "Strong", 1, 1, 0)));
      Assert.Contains(".ThenBy(m => m.InputPerMTok + m.OutputPerMTok)", Repo("Dwight", "Models", "ModelPick.cs"));
      Assert.Contains("private static double PriceOf(ModelInfo m) => m.InputPerMTok + m.OutputPerMTok;", Repo("Dwight", "Agent", "ChatHeader.cs"));
    }

    [Fact]
    public void A_decimal_comma_locale_still_reads_a_point()
    {
      var was = CultureInfo.CurrentCulture;
      try
      {
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        Assert.Equal("about 2.7 times Balanced's price per token", ChatHeader.PriceLine(Priced(2.67), Priced(1.0, "b")));
      }
      finally { CultureInfo.CurrentCulture = was; }
    }

    [Fact]
    public void Where_the_catalogue_gives_nothing_to_compare_nothing_is_claimed()
    {
      Assert.Null(ChatHeader.PriceLine(null, Priced(1)));
      Assert.Null(ChatHeader.PriceLine(Priced(2), null));
      Assert.Null(ChatHeader.PriceLine(Priced(2), Priced(0, "b")));   // no division by a free Balanced
      Assert.Null(ChatHeader.PriceLine(Row("claude-opus-5-5"), Row("kimi-k3:cloud")));   // a plan has no per-token price to multiply
      Assert.Null(ChatHeader.PriceLine(Priced(0), Priced(1, "b")));   // a per-token row with no price is not "on this machine"
      Assert.Equal(ChatHeader.NotBilledLine, ChatHeader.PriceLine(Row("gemma4:12b"), null));   // a local row needs no comparison
    }

    [Fact]
    public void The_line_is_per_token_and_quotes_no_measurement()
    {
      foreach (var provider in Providers)
        foreach (var m in Shipped(provider))
        {
          var line = ChatHeader.PriceLine(m, Balanced(provider)) ?? "";
          Assert.DoesNotContain("¢", line);
          Assert.DoesNotContain("$", line);
          Assert.DoesNotContain("—", line);
        }
    }

    // ---- the hover: every row of the picker ---------------------------------------------------------------------------

    private static string AsHover(string line) => line == null ? null : char.ToUpperInvariant(line[0]) + line.Substring(1) + ".";

    [Fact]
    public void Every_row_of_the_picker_on_every_provider_carries_its_line_as_the_hover()
    {
      Assert.NotEmpty(Providers);
      foreach (var provider in Providers)
      {
        var rows = Shipped(provider);
        var balanced = Balanced(provider);
        var postures = PostureModels(provider);
        var choices = ChatHeader.ModelChoices(rows, balanced, postures, false);

        Assert.Equal(ChatHeader.ModelChoices(rows).Select(c => c.Id), choices.Select(c => c.Id));   // the same list, in the same order
        foreach (var c in choices)
        {
          var model = c.Group == "posture" ? postures[c.Id.Substring(ChatHeader.PosturePrefix.Length)] : rows.Single(r => r.Id == c.Id);
          Assert.Equal(AsHover(ChatHeader.PriceLine(model, balanced)), c.Title);
        }
      }
    }

    [Fact]
    public void The_Anthropic_picker_reads_as_ruled()
    {
      var titles = ChatHeader.ModelChoices(Shipped("Anthropic"), Balanced("Anthropic"), PostureModels("Anthropic"), false).ToDictionary(c => c.Id, c => c.Title);
      Assert.Null(titles["posture:economy"]);
      Assert.Null(titles["posture:balanced"]);
      Assert.Equal("About 2 times Balanced's price per token.", titles["posture:quality"]);
      Assert.Equal("About 2 times Balanced's price per token.", titles["posture:ultra"]);
      Assert.Null(titles["claude-sonnet-5-5"]);
      Assert.Equal("The same price per token as Balanced.", titles["claude-sonnet-5"]);
      Assert.Equal("About 2 times Balanced's price per token.", titles["claude-opus-5-5"]);
      Assert.False(titles.ContainsKey("claude-fable-5-1"));   // agentic false on the shipped row: the chat's picker does not offer it
    }

    [Fact]
    public void A_row_with_a_retention_sentence_says_the_price_first_then_the_sentence_whole()
    {
      var fable = Row("claude-fable-5-1");
      fable.PinAgenticForGate();
      var choice = ChatHeader.ModelChoices(new[] { fable }, Balanced("Anthropic"), null, false).Single(c => c.Id == fable.Id);
      Assert.Equal("About 5 times Balanced's price per token. " + fable.RetentionSentence, choice.Title);
      Assert.Equal(fable.RetentionSentence, ChatHeader.RowTitle(fable, fable));   // no price against itself, and the sentence still stands
    }

    [Fact]
    public void The_document_carries_the_hover_and_a_row_with_none_has_no_title()
    {
      var sonnet = Balanced("Anthropic");
      var json = (JsonObject)JsonNode.Parse(ChatHeader.Json(new ChatHeaderInput
      {
        Provider = "Anthropic", ProviderModels = Shipped("Anthropic"), ThreadModelId = "", Posture = "balanced", Resolved = sonnet,
        ApprovalMode = "ask", Spend = SpendMath.Sum(new StoredSpend[0]), Balanced = sonnet, PostureModels = PostureModels("Anthropic"),
      }));
      var rows = json["model"]["choices"].AsArray().ToDictionary(c => c["id"].GetValue<string>());
      Assert.Equal("About 2 times Balanced's price per token.", rows["posture:quality"]["title"].GetValue<string>());
      Assert.False(rows["posture:balanced"].AsObject().ContainsKey("title"));
      Assert.Equal(new[] { "posture:balanced" }, rows.Where(r => r.Value["on"].GetValue<bool>()).Select(r => r.Key).ToArray());
    }

    [Fact]
    public void A_Settings_pin_wins_so_every_posture_row_names_the_pin_and_its_price()
    {
      var pin = Row("claude-opus-5");
      var sonnet = Balanced("Anthropic");
      var postures = ChatHeader.Postures.ToDictionary(p => p.Id, p => pin);
      var titles = ChatHeader.ModelChoices(Shipped("Anthropic"), sonnet, postures, true).Where(c => c.Group == "posture").Select(c => c.Title).Distinct().ToList();
      Assert.Equal(new[] { "Claude Opus 5 is pinned for every action, about 2.5 times Balanced's price per token." }, titles);

      Assert.Equal("Claude Sonnet 5.5 is pinned for every action, the same model as Balanced.", ChatHeader.PostureTitle(sonnet, sonnet, true));
    }

    // ---- the transcript: once per change ---------------------------------------------------------------------------------

    private static string Change(string was, string now, string provider, ModelInfo model = null, bool settingsPinned = false) =>
      ChatHeader.ChangeLine(was, now, model ?? Resolve(provider, now.Substring(ChatHeader.PosturePrefix.Length)), Balanced(provider), settingsPinned);

    [Fact]
    public void A_change_of_posture_says_its_model_and_its_price_in_the_plugins_voice()
    {
      Assert.Equal("Quality uses Claude Opus 5.5 here, about 2 times Balanced's price per token.", Change("posture:balanced", "posture:quality", "Anthropic"));
      Assert.Equal("Ultra uses Claude Opus 5.5 here, about 2 times Balanced's price per token.", Change("posture:quality", "posture:ultra", "Anthropic"));
      Assert.Equal("Quality uses GPT-6 Astra here, about 5 times Balanced's price per token.", Change("posture:balanced", "posture:quality", "OpenAI"));
    }

    [Fact]
    public void A_change_that_costs_no_more_than_Balanced_still_says_so()
    {
      Assert.Equal("Economy uses Claude Sonnet 5.5 here, the same model as Balanced.", Change("posture:balanced", "posture:economy", "Anthropic"));
      Assert.Equal("Quality uses Qwen3.5 9B (local) here, the same model as Balanced.", Change("posture:balanced", "posture:quality", "OllamaLocal"));

      var sonnet = Balanced("Anthropic");
      Assert.Equal("Claude Sonnet 5 for this chat, the same price per token as Balanced.", ChatHeader.ChangeLine("posture:balanced", "claude-sonnet-5", Row("claude-sonnet-5"), sonnet, false));
      Assert.Equal("Claude Sonnet 5.5 for this chat, the same model as Balanced.", ChatHeader.ChangeLine("posture:balanced", "claude-sonnet-5-5", sonnet, sonnet, false));
      Assert.Equal("Claude Haiku 4.5 for this chat, about half Balanced's price per token.", ChatHeader.ChangeLine("posture:balanced", "claude-haiku-4-5", Row("claude-haiku-4-5"), sonnet, false));
    }

    [Fact]
    public void A_pin_says_its_name_for_this_chat_and_its_price()
    {
      var sonnet = Balanced("Anthropic");
      Assert.Equal("Claude Fable 5.1 for this chat, about 5 times Balanced's price per token.", ChatHeader.ChangeLine("posture:balanced", "claude-fable-5-1", Row("claude-fable-5-1"), sonnet, false));
      Assert.Equal("Claude Opus 5.5 for this chat, about 2 times Balanced's price per token.", ChatHeader.ChangeLine("posture:quality", "claude-opus-5-5", Row("claude-opus-5-5"), sonnet, false));
      Assert.Equal("Gemma 4 12B (local) for this chat, runs on this machine, nothing is billed.", ChatHeader.ChangeLine("posture:balanced", "gemma4:12b", Row("gemma4:12b"), Balanced("OllamaLocal"), false));
      Assert.Equal("Kimi K3 (cloud) for this chat, cost depends on your plan.", ChatHeader.ChangeLine("posture:balanced", "kimi-k3:cloud", Row("kimi-k3:cloud"), Balanced("OllamaCloud"), false));
    }

    [Fact]
    public void Back_to_Balanced_names_the_model_and_no_price()
    {
      Assert.Equal("Back to Balanced: Claude Sonnet 5.5.", Change("posture:quality", "posture:balanced", "Anthropic"));
      Assert.Equal("Back to Balanced: Claude Sonnet 5.5.", Change("claude-opus-5-5", "posture:balanced", "Anthropic"));
    }

    [Fact]
    public void Under_a_Settings_pin_a_change_of_posture_names_the_pin_because_the_next_turn_runs_on_it()
    {
      var pin = Row("claude-opus-5-5");
      Assert.Equal("Claude Opus 5.5 is pinned for every action, about 2 times Balanced's price per token.", Change("posture:quality", "posture:balanced", "Anthropic", pin, true));
      Assert.Equal("Claude Opus 5.5 is pinned for every action, about 2 times Balanced's price per token.", Change("posture:balanced", "posture:ultra", "Anthropic", pin, true));
    }

    [Fact]
    public void One_line_per_change_and_none_for_a_redraw_or_for_choosing_what_is_already_chosen()
    {
      // What the host does: read the choice, write, read it again. A header sent twice with the same record is the same choice both times.
      var said = new List<string>();
      var record = ChatHeader.CurrentChoice("", "balanced");
      void Pick(string pin, string posture)
      {
        var now = ChatHeader.CurrentChoice(pin, posture);
        var model = !string.IsNullOrEmpty(pin) ? Row(pin) : Resolve("Anthropic", posture);
        var line = ChatHeader.ChangeLine(record, now, model, Balanced("Anthropic"), false);
        if (line != null) said.Add(line);
        record = now;
      }

      Pick("", "balanced");            // a new thread on the Settings posture, Balanced clicked: nothing moved, and "Back to" would be false
      Assert.Empty(said);
      Pick("", "quality");
      Pick("", "quality");             // a redraw, or the same row clicked again
      Pick("", "quality");
      Assert.Single(said);
      Pick("claude-opus-5-5", "");     // a pin moves the choice although it is the same model
      Assert.Equal(2, said.Count);
      Pick("", "balanced");
      Assert.Equal(new[]
      {
        "Quality uses Claude Opus 5.5 here, about 2 times Balanced's price per token.",
        "Claude Opus 5.5 for this chat, about 2 times Balanced's price per token.",
        "Back to Balanced: Claude Sonnet 5.5.",
      }, said);
    }

    [Fact]
    public void Nothing_to_name_says_nothing()
    {
      Assert.Null(ChatHeader.ChangeLine("posture:balanced", "posture:quality", null, Balanced("Anthropic"), false));
      Assert.Null(ChatHeader.ChangeLine("posture:balanced", "", Balanced("Anthropic"), Balanced("Anthropic"), false));
    }

    [Fact]
    public void The_current_choice_is_the_row_the_picker_marks_on()
    {
      Assert.Equal("posture:quality", ChatHeader.CurrentChoice("", "quality"));
      Assert.Equal("posture:quality", ChatHeader.CurrentChoice(null, "quality"));
      Assert.Equal("claude-opus-5-5", ChatHeader.CurrentChoice("claude-opus-5-5", "quality"));
      Assert.Equal("posture:balanced", ChatHeader.CurrentChoice("", null));
      Assert.Contains("var on = string.Equals(c.Id, CurrentChoice(i.ThreadModelId, i.Posture), StringComparison.Ordinal);", Repo("Dwight", "Agent", "ChatHeader.cs"));
    }

    // ---- Settings ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Settings_says_the_same_line_under_the_profile_for_the_provider_in_force()
    {
      var sonnet = Balanced("Anthropic");
      Assert.Equal("In a chat, Balanced uses Claude Sonnet 5.5.", ChatHeader.SettingsLine("balanced", sonnet, sonnet, false));
      Assert.Equal("In a chat, Quality uses Claude Opus 5.5, about 2 times Balanced's price per token.", ChatHeader.SettingsLine("quality", Resolve("Anthropic", "quality"), sonnet, false));
      Assert.Equal("In a chat, Economy uses Claude Sonnet 5.5, the same model as Balanced.", ChatHeader.SettingsLine("economy", Resolve("Anthropic", "economy"), sonnet, false));
      Assert.Equal("In a chat, Economy uses GPT-5.6 Terra, about 1.2 times Balanced's price per token.", ChatHeader.SettingsLine("economy", Resolve("OpenAI", "economy"), Balanced("OpenAI"), false));
      Assert.Null(ChatHeader.SettingsLine("quality", null, sonnet, false));
    }

    [Fact]
    public void Settings_under_a_pin_names_the_pin_for_every_action_including_one_the_chat_refuses()
    {
      // Fable is reachable here and nowhere else in the product: it is not agentic, so the chat refuses it in these same words ("You have pinned ... for every action").
      Assert.False(Row("claude-fable-5-1").Agentic);
      Assert.Equal("Claude Fable 5.1 is pinned for every action, about 5 times Balanced's price per token.",
                   ChatHeader.SettingsLine("balanced", Row("claude-fable-5-1"), Balanced("Anthropic"), true));
    }

    [Fact]
    public void Settings_pin_list_says_the_price_on_each_row_and_Fable_keeps_its_retention()
    {
      var fable = Row("claude-fable-5-1");
      Assert.Equal("About 5 times Balanced's price per token. " + fable.RetentionSentence, ChatHeader.RowTitle(fable, Balanced("Anthropic")));
      Assert.Null(ChatHeader.RowTitle(Balanced("Anthropic"), Balanced("Anthropic")));
    }

    [Fact]
    public void The_panel_reads_the_one_function_and_sends_the_line_again_when_what_it_says_changes()
    {
      var panel = Repo("Dwight", "UI", "DwightSettingsPanel.cs");
      Assert.Contains("Dwight.Agent.ChatHeader.SettingsLine(s.Posture, resolved?.Model, s.BalancedChatModel(), !string.IsNullOrEmpty(pinId))", panel);
      Assert.Contains("[\"postureLine\"] = PostureLine(s)", panel);      // a provider change goes through PushInit
      Assert.Contains("st.Posture = p; st.Save(); Run($\"dwightSetStatus({Js(\"Saved\")})\"); Run($\"dwightPostureLine({Js(PostureLine(st))})\");", panel);
      Assert.Equal(2, Regex.Matches(panel, Regex.Escape("Run($\"dwightPostureLine({Js(PostureLine(st))})\");")).Count);   // the profile, and the pin
      Assert.Contains("[\"sentence\"] = Dwight.Agent.ChatHeader.RowTitle(m, balanced)", panel);   // the pin list is the only door to Fable
      Assert.Contains("var balanced = s.BalancedChatModel();", panel);

      var html = Repo("Dwight", "Resources", "settings.html");
      Assert.Contains("<div class=\"hint\" id=\"postureline\" hidden></div>", html);
      Assert.True(html.IndexOf("id=\"posture\"", StringComparison.Ordinal) < html.IndexOf("id=\"postureline\"", StringComparison.Ordinal));
      Assert.True(html.IndexOf("id=\"postureline\"", StringComparison.Ordinal) < html.IndexOf("id=\"override\"", StringComparison.Ordinal));
      Assert.Contains("e.textContent=t||''; e.hidden=!t;", html);
      Assert.Contains("dwightPostureLine(d.postureLine);", html);
    }

    // ---- Economy's sentence is about ourselves, so it carries a test on the behaviour's side (#315) ----------------------------

    private const string EconomyWords = "the lowest band each task can use on this provider, thinking off where the model allows";

    private static double Price(ModelInfo m) => m.InputPerMTok + m.OutputPerMTok;

    [Fact]
    public void Settings_says_what_Economy_does_and_never_that_it_is_cheapest()
    {
      var html = Repo("Dwight", "Resources", "settings.html");
      Assert.Contains("<div class=\"opt\">Economy: " + EconomyWords + "</div>", html);
      Assert.DoesNotContain("cheapest", html, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Quality's and Ultra's sentences are about ourselves too (1 November 2026, after PR #195): "strongest" was false where
    // Quality and Ultra resolve to the same model (Anthropic: both Opus 5.5, Fable stronger and never resolved to) ----------------

    private const string QualityWords = "the highest band each task can use on this provider, thinking high where the task allows";
    private const string UltraWords = "the same model as Quality, thinking at its deepest where the task allows";

    [Fact]
    public void Settings_says_what_Quality_and_Ultra_do_and_never_stronger_or_strongest()
    {
      var html = Repo("Dwight", "Resources", "settings.html");
      Assert.Contains("<div class=\"opt\">Quality: " + QualityWords + "</div>", html);
      Assert.Contains("<div class=\"opt\">Ultra: " + UltraWords + "</div>", html);
      Assert.DoesNotContain("strongest", html, StringComparison.OrdinalIgnoreCase);
      Assert.DoesNotContain("Quality: stronger", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Explain", "Single", false)]
    [InlineData("Explain", "Large", false)]
    [InlineData("Diagnose", "Any", true)]
    [InlineData("Simplify", "Any", true)]
    [InlineData("Label", "Any", true)]
    [InlineData("Scaffold", "Any", false)]
    public void On_every_provider_and_task_Ultra_is_Qualitys_model_at_the_tasks_ceiling_with_deeper_thinking(string taskName, string scopeName, bool structured)
    {
      var task = Enum.Parse<DwightTask>(taskName);
      var scope = Enum.Parse<TaskScope>(scopeName);
      Assert.NotEmpty(Providers);
      foreach (var provider in Providers)
      {
        var quality = ModelResolver.Resolve(task, scope, structured, "quality", Shipped(provider));
        var ultra = ModelResolver.Resolve(task, scope, structured, "ultra", Shipped(provider));
        if (quality.Model == null) { Assert.Null(ultra.Model); continue; }
        Assert.Equal(quality.Model.Id, ultra.Model.Id);
        // Quality sits at the task's ceiling: no profile resolves to a higher band on this provider and task.
        foreach (var profile in new[] { "economy", "balanced", "quality", "ultra" })
        {
          var other = ModelResolver.Resolve(task, scope, structured, profile, Shipped(provider)).Model;
          Assert.True(ModelPick.Idx(other.Tier) <= ModelPick.Idx(quality.Model.Tier), provider + " " + taskName + " " + profile);
        }
        // Where the task thinks, Ultra's effort is the deepest and Quality's is high; where it does not, neither sends one.
        if (ultra.Thinking) { Assert.Equal("max", ultra.Effort); Assert.Equal("high", quality.Effort); }
        else Assert.Null(ultra.Effort);
      }
    }

    [Fact]
    public void The_positive_control_on_OpenAI_Economy_is_the_lower_band_and_the_dearer_row()
    {
      var economy = Resolve("OpenAI", "economy");
      var balanced = Balanced("OpenAI");
      Assert.Equal("gpt-5.6-terra", economy.Id);
      Assert.Equal("gpt-6.1-sol", balanced.Id);
      Assert.True(ModelPick.Idx(economy.Tier) < ModelPick.Idx(balanced.Tier), "the band is what Economy lowers");
      Assert.True(Price(economy) > Price(balanced), "this is the row that made \"cheapest\" false");
    }

    [Fact]
    public void On_every_provider_the_chats_Economy_is_the_lowest_band_that_can_run_the_chat()
    {
      Assert.NotEmpty(Providers);
      foreach (var provider in Providers)
      {
        var lowest = Shipped(provider).Where(m => m.Agentic).Min(m => ModelPick.Idx(m.Tier));
        Assert.Equal(lowest, ModelPick.Idx(Resolve(provider, "economy").Tier));
      }
    }

    [Theory]
    [InlineData("Explain", "Single", false)]
    [InlineData("Explain", "Small", false)]
    [InlineData("Explain", "Large", false)]
    [InlineData("Explain", "Small", true)]
    [InlineData("Diagnose", "Any", true)]
    [InlineData("Simplify", "Any", true)]
    [InlineData("Label", "Any", true)]
    [InlineData("Scaffold", "Any", false)]
    public void On_every_provider_and_task_no_profile_sits_in_a_lower_band_than_Economy(string taskName, string scopeName, bool structured)
    {
      var task = Enum.Parse<DwightTask>(taskName);
      var scope = Enum.Parse<TaskScope>(scopeName);
      foreach (var provider in Providers)
      {
        var rows = Shipped(provider);
        var economy = ModelPick.Idx(ModelResolver.Resolve(task, scope, structured, "economy", rows).Model.Tier);
        foreach (var p in ChatHeader.Postures)
          Assert.True(economy <= ModelPick.Idx(ModelResolver.Resolve(task, scope, structured, p.Id, rows).Model.Tier), provider + " " + task + " " + p.Id);
      }
    }

    [Fact]
    public void Economys_thinking_is_off_on_every_provider_and_where_the_model_allows_is_there_for_a_reason()
    {
      foreach (var provider in Providers)
      {
        var economy = ModelResolver.Resolve(DwightTask.Scaffold, TaskScope.Any, false, "economy", Shipped(provider));
        Assert.False(economy.Thinking, provider);
        Assert.Null(economy.Effort);
      }

      // The control: the same provider's Balanced does think, so the flag is seen to move.
      Assert.True(ModelResolver.Resolve(DwightTask.Scaffold, TaskScope.Any, false, "balanced", Shipped("Anthropic")).Thinking);
      // And the reason for the clause: Economy on Anthropic is a row nothing can turn thinking fully off on.
      Assert.True(Resolve("Anthropic", "economy").CannotDisableThinking);
    }

    // ---- the host's seams, by text: Grasshopper is needed to run them ---------------------------------------------------------

    private static string Repo(params string[] parts)
    {
      var dir = new DirectoryInfo(AppContext.BaseDirectory);
      while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Dwight.sln"))) dir = dir.Parent;
      return File.ReadAllText(Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray())).Replace("\r\n", "\n");
    }

    private static string Method(string source, string signature)
    {
      var at = source.IndexOf(signature, StringComparison.Ordinal);
      Assert.True(at >= 0, signature);
      var next = source.IndexOf("\n    private static ", at + signature.Length, StringComparison.Ordinal);
      return next < 0 ? source.Substring(at) : source.Substring(at, next - at);
    }

    [Fact]
    public void The_line_is_written_only_by_the_model_command_and_through_the_chrome_door()
    {
      var cs = Repo("Dwight", "UI", "DwightScaffold.cs");
      Assert.Single(Regex.Matches(cs, Regex.Escape("ChatHeader.ChangeLine(")));
      Assert.Single(Regex.Matches(cs, Regex.Escape("SayModelChange(wp, doc, threadId, wasChoice);")));

      var say = Method(cs, "private static void SayModelChange(");
      Assert.Contains("if (line != null) AppendPluginHtml(wp, doc, threadId, System.Net.WebUtility.HtmlEncode(line));", say);
      Assert.Contains("AppendBoth(wp, doc, threadId, \"dw\", html, null);", cs);   // the door has no Src, so the line never enters the model's transcript

      var command = Method(cs, "private static void OnHeaderCommand(");
      var model = command.Substring(command.IndexOf("cmd.StartsWith(\"model:\"", StringComparison.Ordinal));
      model = model.Substring(0, model.IndexOf("cmd.StartsWith(\"approval:\"", StringComparison.Ordinal));
      var was = model.IndexOf("var wasChoice = ModelChoiceOf(doc, threadId);", StringComparison.Ordinal);
      var firstWrite = model.IndexOf("CommandHistory.SetThreadModel(", StringComparison.Ordinal);
      var said = model.IndexOf("SayModelChange(wp, doc, threadId, wasChoice);", StringComparison.Ordinal);
      Assert.True(was >= 0 && was < firstWrite, "the choice is read before the write");
      Assert.True(firstWrite < said, "and the line is said after it");

      foreach (var redraw in new[] { Method(cs, "private static string BuildHeaderJson("), Method(cs, "private static void RefreshHeader(") })
      {
        Assert.DoesNotContain("AppendPluginHtml", redraw);
        Assert.DoesNotContain("SayModelChange", redraw);
      }
    }

    [Fact]
    public void The_host_hands_the_header_what_a_turn_would_really_run_on()
    {
      var cs = Repo("Dwight", "UI", "DwightScaffold.cs");
      var json = Method(cs, "private static string BuildHeaderJson(");
      Assert.Contains("Balanced = settings.BalancedChatModel(),", json);
      Assert.Contains("PostureModels = ChatHeader.Postures.ToDictionary(p => p.Id, p => settings.ResolveChatModel(\"\", p.Id, out _, out _)?.Model),", json);
      Assert.Contains("SettingsPinned = !string.IsNullOrEmpty(settingsPinId),", json);

      var say = Method(cs, "private static void SayModelChange(");
      Assert.Contains("settings.ResolveChatModel(threadModelId, CommandHistory.ThreadPosture(doc, threadId), out _, out var pinId)", say);
      Assert.Contains("var settingsPinned = string.IsNullOrEmpty(threadModelId) && !string.IsNullOrEmpty(pinId);", say);
    }

    [Fact]
    public void Balanced_is_resolved_as_a_chat_resolves_it_and_the_test_reads_the_same_resolver()
    {
      var settings = Repo("Dwight", "Config", "DwightSettings.cs");
      Assert.Contains("ModelResolver.Resolve(DwightTask.Scaffold, TaskScope.Any, false, profile, EffectiveProvider)", settings);
      Assert.Contains("ModelResolver.Resolve(DwightTask.Scaffold, TaskScope.Any, false, DwightConfig.PostureBalanced, EffectiveProvider).Model", settings);

      var resolver = Repo("Dwight", "Models", "ModelResolver.cs");
      Assert.Contains("string profile, string provider) =>\n      Resolve(task, scope, structured, profile, ModelCatalogue.ForProvider(provider));", resolver);
    }

    [Fact]
    public void No_default_moved()
    {
      Assert.Contains("public string Posture { get; set; } = DwightConfig.PostureBalanced;", Repo("Dwight", "Config", "DwightSettings.cs"));
      Assert.Equal("posture:balanced", ChatHeader.CurrentChoice("", null));
    }
  }
}
