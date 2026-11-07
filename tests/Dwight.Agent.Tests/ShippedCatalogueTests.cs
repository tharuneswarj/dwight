using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Dwight.Models;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// The SHIPPED catalogue's invariants (#188/#189/#192). models.json stopped being a display list the
  /// moment the thinking wire started resolving from it: a wrong <c>thinkingDefault</c> now means
  /// comprehension silently thinking and being billed for it, or a 400 on a family that rejects the
  /// field. Data that decides behaviour gets asserted like code.
  /// </summary>
  public class ShippedCatalogueTests
  {
    private static readonly JsonObject Catalogue =
      (JsonObject)JsonNode.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "models.json")));

    private static JsonArray Models => Catalogue["models"].AsArray();

    private static IEnumerable<JsonNode> ByProvider(string provider) =>
      Models.Where(m => m["provider"].GetValue<string>() == provider);

    private static string Caps(JsonNode m, string field) => m["capabilities"]?[field]?.GetValue<string>();
    private static bool CapsBool(JsonNode m, string field)
    {
      var v = m["capabilities"]?[field];
      return v != null && v.GetValue<bool>();
    }

    // ---- vision (Brief DX): the fact a refusal names, and code reads it, so every row states it ------------

    [Fact]
    public void Every_row_says_whether_it_reads_images()
    {
      foreach (var m in Models)
        Assert.True(m["capabilities"]?["vision"] != null, m["id"] + " states no vision fact: it would refuse an image with a reason nobody checked");
      Assert.True(Catalogue["catalogueVersion"].GetValue<int>() >= 14, "the vision facts cannot reach an install without the bump");
    }

    [Theory]
    // What each vendor's page said on 2 November 2026. A row here changes only with its page.
    [InlineData("claude-sonnet-5-5", true)] [InlineData("claude-opus-5-5", true)] [InlineData("claude-haiku-4-5", true)]
    [InlineData("gpt-5.6-terra", true)] [InlineData("gpt-6-astra", true)]
    [InlineData("gemini-3.8-flash", true)] [InlineData("gemini-4-argon", true)]
    [InlineData("qwen3.5:9b", true)] [InlineData("gemma4:12b", true)] [InlineData("kimi-k3:cloud", true)]
    [InlineData("qwen3:8b", false)] [InlineData("gpt-oss:20b", false)] [InlineData("phi4-mini", false)] [InlineData("nemotron-3-nano:4b", false)]
    [InlineData("glm-5.3:cloud", false)] [InlineData("deepseek-v4-pro:0813", false)]
    // Their pages say the model was retired and list nothing, and silence is the safe state.
    [InlineData("glm-5.1:cloud", false)] [InlineData("deepseek-v4-flash:preview", false)]
    public void The_vision_fact_is_the_vendors(string id, bool vision) =>
      Assert.Equal(vision, CapsBool(Models.First(m => m["id"].GetValue<string>() == id), "vision"));

    [Fact]
    public void Every_provider_family_has_a_chat_model_that_reads_an_image_so_a_refusal_has_somewhere_to_send_you()
    {
      foreach (var provider in new[] { "Anthropic", "OpenAI", "Google", "OllamaLocal", "OllamaCloud" })
        Assert.Contains(ByProvider(provider), m => CapsBool(m, "agentic") && CapsBool(m, "vision"));
    }

    [Fact]
    public void The_shipped_catalogue_reaches_the_model_row_and_a_row_that_says_nothing_refuses()
    {
      // Through the parser the plugin uses, on the file that ships: ModelCatalogue.All would read this machine's own copy in %APPDATA%.
      var rows = ModelCatalogue.Parse(Catalogue.ToJsonString());
      Assert.True(rows.Single(m => m.Id == "claude-sonnet-5-5").Vision);
      Assert.False(rows.Single(m => m.Id == "qwen3:8b").Vision);
      Assert.False(new Dwight.ModelInfo("hand-added", "Hand added", "OllamaLocal", "Mid", 0, 0, 1000).Vision);
      Assert.False(ModelCatalogue.Parse("{\"models\":[{\"id\":\"a\",\"provider\":\"OllamaLocal\",\"capabilities\":{}}]}").Single().Vision);
    }

    /// <summary>The rows the measured image rule applies to: Claude 4.7 and later by Anthropic's page, measured on Sonnet 5.5. The page caps Sonnet 4.6 and Haiku 4.5, and Gemini and qwen measured far from it, so none of them shows a number.</summary>
    [Fact]
    public void Only_the_rows_the_measured_image_rule_applies_to_say_so()
    {
      Assert.Equal(
        new[] { "claude-fable-5-1", "claude-haiku-5-5", "claude-opus-4-8", "claude-opus-5", "claude-opus-5-5", "claude-sonnet-5", "claude-sonnet-5-5" },
        Models.Where(m => CapsBool(m, "imagePatchTokens")).Select(m => m["id"].GetValue<string>()).OrderBy(i => i, StringComparer.Ordinal));
      foreach (var m in Models.Where(m => CapsBool(m, "imagePatchTokens")))
        Assert.True(CapsBool(m, "vision"), m["id"] + " counts an image and reads none");
      Assert.True(Catalogue["catalogueVersion"].GetValue<int>() >= 15, "the fact cannot reach an install without the bump");

      var rows = ModelCatalogue.Parse(Catalogue.ToJsonString());
      Assert.True(rows.Single(m => m.Id == "claude-sonnet-5-5").ImagePatchTokens);
      foreach (var id in new[] { "claude-sonnet-4-6", "claude-haiku-4-5", "gemini-3.8-flash", "qwen3.5:9b", "gpt-5.6-terra" })
        Assert.False(rows.Single(m => m.Id == id).ImagePatchTokens, id);
      Assert.False(new Dwight.ModelInfo("hand-added", "Hand added", "Anthropic", "Mid", 1, 1, 1000, vision: true).ImagePatchTokens);
    }

    // ---- delivery ---------------------------------------------------------------------------------

    [Fact]
    public void The_catalogue_version_is_bumped_so_the_refresh_can_actually_reach_an_install()
    {
      // The whole point of the delivery fix: a lineup change that does not bump this is invisible to
      // every existing user, because their seeded copy wins.
      Assert.True(Catalogue["catalogueVersion"].GetValue<int>() >= 2);
    }

    [Fact]
    public void The_bump_discipline_is_stated_in_the_file_itself()
    {
      // The contract sits where the change is made, so the next person to edit the lineup reads it.
      Assert.Contains("BUMP IT", Catalogue["versionNote"].GetValue<string>());
    }

    // ---- shape ------------------------------------------------------------------------------------

    [Fact]
    public void Every_entry_has_the_fields_the_resolver_reads()
    {
      foreach (var m in Models)
      {
        Assert.False(string.IsNullOrWhiteSpace(m["id"]?.GetValue<string>()));
        Assert.False(string.IsNullOrWhiteSpace(m["displayName"]?.GetValue<string>()));
        Assert.False(string.IsNullOrWhiteSpace(m["provider"]?.GetValue<string>()));
        Assert.Contains(m["tier"].GetValue<string>(), new[] { "Fast", "Mid", "Strong", "Top" });
      }
    }

    [Fact]
    public void Model_ids_are_unique()
    {
      var ids = Models.Select(m => m["id"].GetValue<string>()).ToList();
      Assert.Equal(ids.Distinct().Count(), ids.Count);
    }

    // ---- the thinking fact (#192) -----------------------------------------------------------------

    [Fact]
    public void Every_entry_declares_a_legal_thinking_default()
    {
      // An unrecognised value would fall through to "none" and silently stop disabling thinking on a
      // default-on model - the exact failure the three-state wire exists to prevent.
      foreach (var m in Models)
        Assert.Contains(Caps(m, "thinkingDefault"), new[] { "adaptive-on", "adaptive-off", "adaptive-always", "adaptive-between-tools", "none" });
    }

    [Fact]
    public void The_claude_5_family_is_marked_default_ON()
    {
      // These are the models where OMITTING the field runs thinking. Getting this wrong means every
      // comprehension call on them thinks, and is billed for it, with nothing to show for it.
      foreach (var id in new[] { "claude-sonnet-5", "claude-opus-5", "claude-haiku-5-5" })
      {
        var m = Models.First(x => x["id"].GetValue<string>() == id);
        Assert.Equal("adaptive-on", Caps(m, "thinkingDefault"));
      }
    }

    [Fact]
    public void The_retained_4x_family_is_marked_default_OFF()
    {
      foreach (var id in new[] { "claude-sonnet-4-6", "claude-opus-4-8" })
      {
        var m = Models.First(x => x["id"].GetValue<string>() == id);
        Assert.Equal("adaptive-off", Caps(m, "thinkingDefault"));
      }
    }

    [Fact]
    public void The_evals_pinned_baseline_model_is_still_in_the_catalogue()
    {
      // #188's eval pin: every stamped measurement (81.2/41.7 et al.) depends on this id remaining
      // resolvable. Dropping it would silently break baseline comparability.
      Assert.Contains(Models, m => m["id"].GetValue<string>() == "claude-sonnet-4-6");
    }

    [Fact]
    public void Fable_5_is_excluded_and_Fable_5_1_is_listed_only_for_a_person_to_pin()
    {
      // #188: the 30-day-retention requirement and refusal-handling burden are wrong for a BYO-key tool. Brief BZ
      // narrows that and keeps its reason: the refusal is now handled and the retention said, and it is a person's
      // choice, never a profile's. Fable 5 itself stays out.
      Assert.DoesNotContain(Models, m => m["id"].GetValue<string>() == "claude-fable-5");
      foreach (var fable in Models.Where(m => m["id"].GetValue<string>().Contains("fable")))
      {
        Assert.Equal("claude-fable-5-1", fable["id"].GetValue<string>());
        Assert.True(CapsBool(fable, "pinOnly"));
        Assert.False(CapsBool(fable, "agentic"));
      }
    }

    // ---- the agentic fact (#189) ------------------------------------------------------------------

    [Fact]
    public void No_Fast_band_model_is_agentic_on_any_provider()
    {
      // #189: a Fast model can emit a tool call and still be the wrong model to drive a multi-step loop.
      foreach (var m in Models.Where(x => x["tier"].GetValue<string>() == "Fast"))
        Assert.False(CapsBool(m, "agentic"), m["id"].GetValue<string>() + " is Fast and must not be agentic");
    }

    [Fact]
    public void A_local_model_is_agentic_only_if_it_is_Mid_band_and_larger_than_eight_billion_parameters()
    {
      // #189's guarantee, restated for the lane that lets the chat run on Ollama: a 4-8B model is never handed the
      // agent loop. The rule is data a test can hold - the tier AND the size in the id - not a flag nobody checks.
      var allowed = new[] { "qwen3.5:9b", "gemma4:12b" };   // gpt-oss:20b left on 2026-10-25: its measured stream ended unfinished
      foreach (var m in ByProvider("OllamaLocal").Where(x => CapsBool(x, "agentic")))
      {
        Assert.Equal("Mid", m["tier"].GetValue<string>());
        Assert.Contains(m["id"].GetValue<string>(), allowed);
      }
      // ... and the two that are 8B or smaller stay out even though they are Mid or list tools.
      Assert.False(CapsBool(Models.First(x => x["id"].GetValue<string>() == "qwen3:8b"), "agentic"));
    }

    [Fact]
    public void Every_agentic_row_says_where_its_tool_calling_is_documented()
    {
      // A true that cites nothing is a claim with no source. Anthropic's rows predate the note; every other agentic row
      // carries it in its speedNote (the catalogue has no comments).
      foreach (var m in Models.Where(x => CapsBool(x, "agentic") && x["provider"].GetValue<string>() != "Anthropic"))
        Assert.Contains("Agentic: true", m["speedNote"].GetValue<string>());
    }

    [Theory]
    [InlineData("gpt-6.1-sol")]
    [InlineData("gpt-6-astra")]
    public void The_two_OpenAI_models_that_failed_on_chat_completions_passed_on_Responses_and_say_both(string id)
    {
      // 25 October 2026: chat-completions returned 400 for reasoning_effort "none" on these two. 31 October 2026: they passed on the Responses endpoint.
      var m = Models.First(x => x["id"].GetValue<string>() == id);
      Assert.True(CapsBool(m, "agentic"));
      var note = m["speedNote"].GetValue<string>();
      Assert.Contains("reasoning_effort", note);
      Assert.Contains("25 October 2026", note);
      Assert.Contains("MEASURED 2026-10-31", note);
      Assert.Contains("Responses endpoint", note);
      Assert.DoesNotContain("READ FROM DOCS, NOT MEASURED", note);
    }

    [Theory]
    [InlineData("gpt-5.6-terra", "MEASURED 2026-10-25: read turn passed; spot build and undo passed.")]
    [InlineData("gpt-5.6-sol", "MEASURED 2026-10-25: read turn passed.")]
    [InlineData("gemini-3.8-flash", "MEASURED 2026-10-25")]
    [InlineData("glm-5.3:cloud", "MEASURED 2026-10-25")]
    [InlineData("claude-sonnet-5", "MEASURED 2026-10-25")]
    public void The_rows_the_gate_passed_say_measured_and_stay_agentic(string id, string measured)
    {
      var m = Models.First(x => x["id"].GetValue<string>() == id);
      Assert.True(CapsBool(m, "agentic"));
      var note = m["speedNote"].GetValue<string>();
      Assert.Contains(measured, note);
      Assert.DoesNotContain("READ FROM DOCS, NOT MEASURED", note);
    }

    [Fact]
    public void The_three_local_rows_say_what_was_measured_on_25_October_2026()
    {
      var q = Models.First(x => x["id"].GetValue<string>() == "qwen3.5:9b");
      Assert.True(CapsBool(q, "agentic"));
      var qn = q["speedNote"].GetValue<string>();
      Assert.Contains("MEASURED 2026-10-25 on Ollama 0.30.10 with OLLAMA_CONTEXT_LENGTH=32768", qn);
      Assert.Contains("231 s of reasoning", qn);
      Assert.Contains("Needs a 32k context", qn);
      Assert.DoesNotContain("READ FROM DOCS, NOT MEASURED", qn);

      var g = Models.First(x => x["id"].GetValue<string>() == "gpt-oss:20b");
      Assert.False(CapsBool(g, "agentic"));
      var gn = g["speedNote"].GetValue<string>();
      Assert.Contains("MEASURED 2026-10-25 on Ollama 0.30.10", gn);
      Assert.Contains("six reasoning deltas with no finish, no call and no usage", gn);
      Assert.Contains("Not agentic until a later measurement passes", gn);

      var m = Models.First(x => x["id"].GetValue<string>() == "gemma4:12b");
      Assert.True(CapsBool(m, "agentic"));
      Assert.Contains("READ FROM DOCS (tools listed); NOT MEASURED - the one attempt on 2026-10-25 hit a server memory error", m["speedNote"].GetValue<string>());
    }

    [Fact]
    public void Every_agentic_model_also_supports_structured_output()
    {
      // The agent surface is schema-driven end to end; a model that cannot hold a schema cannot drive it.
      foreach (var m in Models.Where(x => CapsBool(x, "agentic")))
        Assert.True(CapsBool(m, "structuredOutput"), m["id"].GetValue<string>());
    }

    // ---- the October 2026 refresh (#910-#912) ------------------------------------------------------

    [Fact]
    public void Every_entry_STATES_its_thinking_default_and_agentic_fact_explicitly()
    {
      // #912. The parser reads an ABSENT thinkingDefault as "none" and an absent agentic as false - the
      // safe state. Safe is not the same as stated: an entry that omits either is one nobody decided,
      // and the next adaptive-on model added without the fact would run thinking on every comprehension
      // call while reading as "none". The file must carry the decision for every row.
      foreach (var m in Models)
      {
        var caps = m["capabilities"] as JsonObject;
        var id = m["id"].GetValue<string>();
        Assert.NotNull(caps);
        Assert.True(caps.ContainsKey("thinkingDefault"), id + " does not state thinkingDefault");
        Assert.True(caps.ContainsKey("agentic"), id + " does not state agentic");
      }
    }

    [Fact]
    public void Every_agentic_model_sits_on_a_provider_whose_connection_carries_tools()
    {
      // #884 enforces agentic at the door; this makes the DATA match the CODE in the other direction: a true on a row
      // whose provider's adapter drops the tools is a claim with no code behind it. The provider names are the
      // catalogue's; the facts are the adapters' own declarations.
      var passes = new System.Collections.Generic.Dictionary<string, bool>
      {
        ["Anthropic"] = Dwight.Llm.AnthropicRequestBody.PassesTools,
        ["OpenAI"] = Dwight.Llm.OpenAIProvider.PassesTools,
        ["OllamaLocal"] = Dwight.Llm.OpenAIProvider.PassesTools,
        ["OllamaCloud"] = Dwight.Llm.OpenAIProvider.PassesTools,
        ["Google"] = Dwight.Llm.GoogleProvider.PassesTools,
      };
      foreach (var m in Models.Where(x => CapsBool(x, "agentic")))
        Assert.True(passes[m["provider"].GetValue<string>()], m["id"].GetValue<string>() + " is agentic on a provider that carries no tools");
    }

    [Fact]
    public void Haiku_5_5_is_marked_default_ON_and_never_agentic()
    {
      // Vendor: "Adaptive thinking is on by default" and "disabled" is accepted at effort high or below.
      // Fast band, so it never reaches Scaffold; agentic false like Haiku 4.5 (#884).
      var m = Models.First(x => x["id"].GetValue<string>() == "claude-haiku-5-5");
      Assert.Equal("adaptive-on", Caps(m, "thinkingDefault"));
      Assert.False(CapsBool(m, "agentic"));
      Assert.Equal("Fast", m["tier"].GetValue<string>());
    }

    [Fact]
    public void The_5_5_generation_that_rejects_disabled_thinking_and_forced_tools_is_listed_with_those_facts()
    {
      // #910. Sonnet 5.5 and Opus 5.5 return 400 on thinking {type:"disabled"} and on forced
      // tool_choice - the two shapes ThinkingWire and AnthropicRequestBody send. Listed, Opus 5.5 would
      // win the Top band on price and fail every structured verb. They come in with the successor unit
      // (omit + lowest effort / between_tools; output_config.format or auto + strict), never before.
      // Brief BZ is that unit. A row that lacked one of these facts would be the #910 failure, listed.
      foreach (var (id, thinking) in new[] { ("claude-sonnet-5-5", "adaptive-between-tools"), ("claude-opus-5-5", "adaptive-always") })
      {
        var m = Models.Single(x => x["id"].GetValue<string>() == id);
        Assert.Equal(thinking, Caps(m, "thinkingDefault"));
        Assert.False(m["capabilities"]["forcedToolChoice"].GetValue<bool>());
        Assert.True(CapsBool(m, "structuredFormat"));
        Assert.True(CapsBool(m, "thinkingBound"));
      }
      Assert.Contains("between_tools", Catalogue["excludedNote"].GetValue<string>());
    }

    [Fact]
    public void Every_entry_declares_a_legal_status()
    {
      // "retired" joined the vocabulary at #912. The reconciler treats anything other than preview/paid
      // as red when the key cannot list it, so a typo here silently changes a model's colour.
      foreach (var m in Models)
        Assert.Contains(m["status"].GetValue<string>(), new[] { "stable", "preview", "confirm", "paid", "retired" });
    }

    [Fact]
    public void A_retired_model_is_kept_but_can_never_win_a_band()
    {
      // Rule 4 of the refresh: a vendor-retired id is MARKED, never deleted, because a user's Settings
      // may still name it and the reconciler is what tells them. But before the reconciler has run every
      // entry is "unknown", so a retired row that tied on price would be picked. Preference 90 keeps it
      // behind every live row, and this asserts the outcome on the shipped data rather than the number.
      var retired = Models.Where(m => m["status"].GetValue<string>() == "retired").ToList();
      Assert.NotEmpty(retired);
      foreach (var provider in new[] { "Anthropic", "OpenAI", "Google", "OllamaLocal", "OllamaCloud" })
      {
        var shipped = Shipped(provider);
        if (shipped.Count == 0) continue;
        foreach (var band in new[] { "Fast", "Mid", "Strong", "Top" })
        {
          var picked = ModelPick.Choose(shipped, band, band, band, structuredOnly: true);
          if (picked == null) continue;
          var row = Models.First(m => m["id"].GetValue<string>() == picked.Id);
          Assert.NotEqual("retired", row["status"].GetValue<string>());
        }
      }
    }

    [Fact]
    public void The_catalogue_version_is_at_least_the_October_2026_revision()
    {
      // The lineup changed (nine new ids, two retirements, three corrected ids); without the bump no
      // existing install sees any of it, because the seeded user copy wins.
      Assert.True(Catalogue["catalogueVersion"].GetValue<int>() >= 7);   // 6: the chat on OpenAI and Ollama (agentic flags, gpt-oss:20b); 7: and on Google
    }

    [Fact]
    public void GLM_5_3_carries_the_26_October_measurement_stays_agentic_and_the_version_moved_to_9()
    {
      Assert.True(Catalogue["catalogueVersion"].GetValue<int>() >= 9);   // 9: the GLM-5.3 note; a seeded install takes it only on a bump

      var glm = Models.Single(m => m["id"].GetValue<string>() == "glm-5.3:cloud");
      var note = glm["speedNote"].GetValue<string>();
      Assert.Contains("MEASURED 2026-10-25: read turn and spot build passed.", note);   // the earlier measurement is kept
      Assert.Contains("MEASURED 2026-10-26: a 5x5-grid build failed on request_mutation formatting slips and ended on the asked-twice stop; " +
                      "reads and simple builds pass, complex build specs are unreliable on this model.", note);
      Assert.True(glm["capabilities"]["agentic"].GetValue<bool>());
    }

    // ---- parsed shape -----------------------------------------------------------------------------

    [Fact]
    public void An_absent_or_unrecognised_thinking_default_reads_as_the_SAFE_state()
    {
      // Silence must mean "never send a thinking field", not a guess. A catalogue entry that omits the
      // fact - or a future value this build has never heard of - must degrade to sending nothing,
      // because the alternative is a 400 or a silently-thinking comprehension call.
      foreach (var unknown in new[] { null, "", "   ", "none", "enabled", "budget", "ADAPTIVE-MAYBE" })
      {
        Assert.False(Dwight.Llm.ThinkingWire.IsOnByDefault(unknown), $"[{unknown}]");
        Assert.False(Dwight.Llm.ThinkingWire.SupportsAdaptive(unknown), $"[{unknown}]");
      }
    }

    [Fact]
    public void The_thinking_flags_derive_from_the_declared_default()
    {
      Assert.True(Dwight.Llm.ThinkingWire.IsOnByDefault("adaptive-on"));
      Assert.True(Dwight.Llm.ThinkingWire.SupportsAdaptive("adaptive-on"));

      Assert.False(Dwight.Llm.ThinkingWire.IsOnByDefault("adaptive-off"));
      Assert.True(Dwight.Llm.ThinkingWire.SupportsAdaptive("adaptive-off"));
    }

    // ---- resolution against the REAL shipped data (#199) -------------------------------------------

    /// <summary>Parse the shipped catalogue into the same objects the resolver sees. Tests the DATA, not
    /// a hand-written copy of it - a fixture can drift from the file, and this is the file.</summary>
    private static List<Dwight.ModelInfo> Shipped(string provider) => Models
      .Where(m => m["provider"].GetValue<string>() == provider)
      .Select(m => new Dwight.ModelInfo(
        m["id"].GetValue<string>(), m["displayName"].GetValue<string>(), provider, m["tier"].GetValue<string>(),
        m["priceIn"].GetValue<double>(), m["priceOut"].GetValue<double>(), m["contextWindow"].GetValue<int>(),
        structured: CapsBool(m, "structuredOutput"),
        thinkingDefault: Caps(m, "thinkingDefault"), agentic: CapsBool(m, "agentic"),
        preference: m["preference"]?.GetValue<int>(), pinOnly: CapsBool(m, "pinOnly")))
      .ToList();

    [Fact]
    public void A_STRONG_comprehension_task_resolves_to_sonnet_5_5_on_the_shipped_catalogue()
    {
      // Diagnose is Strong->Strong. Sonnet 5.5, Sonnet 5 and Sonnet 4.6 tie on band and price, so this asserts the
      // tie is settled by the catalogue's stated preference and not by line order. Sonnet 5.5 since the flip of
      // 1 November 2026 (Tharun's "flip it" after the eighth gate measured it); Sonnet 5 before it.
      var picked = ModelPick.Choose(Shipped("Anthropic"), "Strong", "Strong", "Strong", structuredOnly: true);
      Assert.Equal("claude-sonnet-5-5", picked.Id);
    }

    [Fact]
    public void A_TOP_task_resolves_to_opus_5_5_on_the_shipped_catalogue()
    {
      // Build's ceiling. Opus 5.5, Opus 5 and Opus 4.8 tie exactly as the Sonnets do - found by the tie-break
      // test rather than by reading, and fixed in the data. Opus 5.5 since the flip of 1 November 2026.
      var picked = ModelPick.Choose(Shipped("Anthropic"), "Top", "Strong", "Top", structuredOnly: true);
      Assert.Equal("claude-opus-5-5", picked.Id);
    }

    [Fact]
    public void EVERY_shipped_band_resolves_independently_of_catalogue_order()
    {
      // The general property across the whole shipped file: for every provider and every band, the
      // reversed catalogue resolves to the same model. Subscription and local entries all price at
      // zero, so most of those bands are ties - this is where order-dependence would actually bite.
      foreach (var provider in new[] { "Anthropic", "OpenAI", "Google", "OllamaLocal", "OllamaCloud" })
      {
        var forward = Shipped(provider);
        if (forward.Count == 0) continue;
        var reversed = Enumerable.Reverse(forward).ToList();

        foreach (var band in new[] { "Fast", "Mid", "Strong", "Top" })
        {
          var a = ModelPick.Choose(forward, band, band, band, structuredOnly: true);
          var b = ModelPick.Choose(reversed, band, band, band, structuredOnly: true);
          Assert.Equal(a?.Id, b?.Id);
        }
      }
    }

    [Fact]
    public void Every_shipped_entry_resolves_to_a_defined_wire_state()
    {
      // End to end over the REAL catalogue: for both task intents, every entry produces a state, and a
      // model that cannot do adaptive is never asked to.
      foreach (var m in Models)
      {
        var td = Caps(m, "thinkingDefault");
        var onByDefault = Dwight.Llm.ThinkingWire.IsOnByDefault(td);
        var adaptive = Dwight.Llm.ThinkingWire.SupportsAdaptive(td);

        var off = Dwight.Llm.ThinkingWire.Resolve(false, onByDefault, adaptive);
        Assert.Equal(onByDefault ? Dwight.Llm.ThinkingWireState.Disabled : Dwight.Llm.ThinkingWireState.Omit, off);

        var on = Dwight.Llm.ThinkingWire.Resolve(true, onByDefault, adaptive);
        Assert.Equal(adaptive ? Dwight.Llm.ThinkingWireState.Adaptive : Dwight.Llm.ThinkingWireState.Omit, on);
      }
    }
  }
}
