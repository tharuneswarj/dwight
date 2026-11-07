using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Dwight.Agent;
using Dwight.Models;
using Dwight.Session;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// THE BUILD CHAT'S HEADER (Brief AO): the model, the approval mode, Full control and the cost so far, under the title bar. The shell holds no value
  /// of its own, so everything the header says is computed here from the thread's record and the catalogue and is pinned cell by cell.
  /// </summary>
  public class ChatHeaderTests
  {
    // ---- the catalogue, as the shipped file has it ---------------------------------------------------------------------

    private static JsonArray Models =>
      ((JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "models.json"))))["models"].AsArray();

    private static IReadOnlyList<ModelInfo> ForProvider(string provider) =>
      Models.Where(m => m["provider"].GetValue<string>() == provider)
            .Select(m => new ModelInfo(m["id"].GetValue<string>(), m["displayName"].GetValue<string>(), provider, m["tier"].GetValue<string>(),
                                       0, 0, 0, agentic: m["capabilities"]?["agentic"]?.GetValue<bool>() == true))
            .ToList();

    private static IEnumerable<string> Providers => Models.Select(m => m["provider"].GetValue<string>()).Distinct();

    private static ModelInfo Row(string id, string name, string provider, bool agentic, string tier = "Strong") =>
      new ModelInfo(id, name, provider, tier, 0, 0, 0, agentic: agentic);

    // ---- the picker's list -------------------------------------------------------------------------------------------

    [Fact]
    public void The_picker_lists_the_postures_first_then_only_the_providers_agentic_models_for_every_provider()
    {
      Assert.NotEmpty(Providers);
      foreach (var provider in Providers)
      {
        var rows = ForProvider(provider);
        var choices = ChatHeader.ModelChoices(rows);

        Assert.Equal(new[] { "posture:economy", "posture:balanced", "posture:quality", "posture:ultra" }, choices.Take(4).Select(c => c.Id).ToArray());
        Assert.Equal(new[] { "Economy", "Balanced", "Quality", "Ultra" }, choices.Take(4).Select(c => c.Label).ToArray());
        Assert.All(choices.Take(4), c => Assert.Equal("posture", c.Group));

        var models = choices.Skip(4).ToList();
        Assert.All(models, c => Assert.Equal("model", c.Group));
        Assert.Equal(rows.Where(r => r.Agentic).Select(r => r.Id).ToArray(), models.Select(c => c.Id).ToArray());   // exactly the agentic rows, in catalogue order
        foreach (var m in models) Assert.True(rows.Single(r => r.Id == m.Id).Agentic, provider + " " + m.Id);
        Assert.DoesNotContain(models, c => !rows.Single(r => r.Id == c.Id).Agentic);
      }
    }

    [Fact]
    public void No_Fast_band_or_non_agentic_model_ever_appears_on_any_provider()
    {
      foreach (var provider in Providers)
      {
        var rows = ForProvider(provider);
        var offered = ChatHeader.ModelChoices(rows).Where(c => c.Group == "model").Select(c => c.Id).ToHashSet();
        foreach (var fast in rows.Where(r => r.Tier == "Fast")) Assert.DoesNotContain(fast.Id, offered);
        foreach (var no in rows.Where(r => !r.Agentic)) Assert.DoesNotContain(no.Id, offered);
      }
    }

    [Fact]
    public void A_model_of_another_provider_is_not_offered_and_the_host_refuses_to_write_it()
    {
      var anthropic = new[] { Row("a-1", "Sonnet 5", "Anthropic", true), Row("a-2", "Haiku 4.5", "Anthropic", false, "Fast") };
      Assert.True(ChatHeader.IsOffered("a-1", anthropic));
      Assert.False(ChatHeader.IsOffered("a-2", anthropic));          // not agentic
      Assert.False(ChatHeader.IsOffered("gpt-x", anthropic));        // not this provider
      Assert.False(ChatHeader.IsOffered("", anthropic));
      Assert.True(ChatHeader.IsOffered("posture:quality", anthropic));
      Assert.False(ChatHeader.IsOffered("posture:turbo", anthropic));
    }

    [Fact]
    public void A_provider_with_no_agentic_model_still_lists_the_postures()
    {
      var choices = ChatHeader.ModelChoices(new[] { Row("s-1", "Small", "Ollama", false, "Fast") });
      Assert.Equal(4, choices.Count);
      Assert.All(choices, c => Assert.Equal("posture", c.Group));
    }

    // ---- the resolution order ----------------------------------------------------------------------------------------

    [Fact]
    public void A_turn_resolves_the_thread_pin_first_then_the_settings_pin_then_the_posture()
    {
      var threadPin = Row("t-pin", "Thread pin", "Anthropic", true);
      var settingsPin = Row("s-pin", "Settings pin", "Anthropic", true);

      Assert.Same(threadPin, ChatHeader.PickPin("t-pin", threadPin, settingsPin, "Anthropic", out var ignored1));
      Assert.False(ignored1);

      Assert.Same(settingsPin, ChatHeader.PickPin("", null, settingsPin, "Anthropic", out var ignored2));   // no thread pin: the Settings default
      Assert.False(ignored2);

      Assert.Null(ChatHeader.PickPin("", null, null, "Anthropic", out var ignored3));                       // neither: the posture chooses
      Assert.False(ignored3);
    }

    [Fact]
    public void A_thread_pin_that_no_longer_resolves_falls_back_and_says_so()
    {
      var stale = Row("gpt-x", "GPT-5.6 Terra", "OpenAI", true);   // the provider changed under it (U52's case)
      var settingsPin = Row("s-pin", "Settings pin", "Anthropic", true);

      var picked = ChatHeader.PickPin("gpt-x", stale, settingsPin, "Anthropic", out var ignored);
      Assert.True(ignored);
      Assert.Same(settingsPin, picked);   // the next rung of the ladder, never the stale pin

      Assert.Null(ChatHeader.PickPin("gpt-x", stale, null, "Anthropic", out var ignored2));
      Assert.True(ignored2);
      Assert.Null(ChatHeader.PickPin("gone", null, null, "Anthropic", out var ignored3));   // an id the catalogue no longer holds
      Assert.True(ignored3);

      Assert.Equal("The model pinned for this chat is not on this provider, so Dwight used the profile to choose one instead. Pick a model under the chat's text box to pin another.",
                   ChatHeader.PinFallbackLine);
    }

    [Fact]
    public void A_settings_pin_of_another_provider_is_ignored_too()
    {
      Assert.Null(ChatHeader.PickPin("", null, Row("g", "Other", "Google", true), "Anthropic", out _));
    }

    // ---- the chips' texts ----------------------------------------------------------------------------------------------

    private static ChatHeaderInput Input(Action<ChatHeaderInput> edit = null)
    {
      var i = new ChatHeaderInput
      {
        Provider = "Anthropic",
        ProviderModels = new[] { Row("a-1", "Sonnet 5", "Anthropic", true), Row("a-2", "Opus 5", "Anthropic", true) },
        ThreadModelId = "",
        Posture = "balanced",
        Resolved = Row("a-1", "Sonnet 5", "Anthropic", true),
        ApprovalMode = "ask",
        FullControl = false,
        Spend = SpendMath.Sum(new[] { new StoredSpend { Cents = 10.2 }, new StoredSpend { Cents = 8.1 }, new StoredSpend { Cents = 5.2 } }),
      };
      edit?.Invoke(i);
      return i;
    }

    [Fact]
    public void The_model_chip_names_the_pin_or_the_posture_and_what_it_chooses()
    {
      Assert.Equal("Balanced · Sonnet 5", ChatHeader.ModelChip(Input()));
      Assert.Equal("Quality · Sonnet 5", ChatHeader.ModelChip(Input(i => i.Posture = "quality")));
      Assert.Equal("Ultra", ChatHeader.ModelChip(Input(i => { i.Posture = "ultra"; i.Resolved = null; })));

      var opus = Row("a-2", "Opus 5", "Anthropic", true);
      Assert.Equal("Opus 5", ChatHeader.ModelChip(Input(i => { i.ThreadModelId = "a-2"; i.ThreadPin = opus; i.Resolved = opus; })));
    }

    [Fact]
    public void A_stale_pin_shows_its_name_with_not_on_this_provider_beside_it()
    {
      var stale = Row("gpt-x", "GPT-5.6 Terra", "OpenAI", true);
      Assert.Equal("GPT-5.6 Terra (not on this provider)", ChatHeader.ModelChip(Input(i => { i.ThreadModelId = "gpt-x"; i.ThreadPin = stale; })));
      Assert.Equal("some-old-id (not on this provider)", ChatHeader.ModelChip(Input(i => { i.ThreadModelId = "some-old-id"; i.ThreadPin = null; })));
    }

    [Theory]
    [InlineData("ask", "Ask every time")]
    [InlineData("scripts_and_deletions", "Scripts and deletions")]
    [InlineData("never", "Do not ask")]
    [InlineData("nonsense", "Ask every time")]
    [InlineData(null, "Ask every time")]
    public void The_approval_chip_is_the_settings_words_shortened(string mode, string text) =>
      Assert.Equal(text, ChatHeader.ApprovalChip(mode));

    [Fact]
    public void The_full_control_chip_says_on_or_off()
    {
      Assert.Equal("Full control off", ChatHeader.FullControlChip(false));
      Assert.Equal("Full control on", ChatHeader.FullControlChip(true));
    }

    [Fact]
    public void The_cost_chip_is_the_footers_totals_priced_unpriced_and_before_the_first_turn()
    {
      var priced = SpendMath.Sum(new[] { new StoredSpend { Cents = 10.2 }, new StoredSpend { Cents = 8.1 }, new StoredSpend { Cents = 5.2 } });
      Assert.Equal("~23.5¢ · 3 turns", ChatHeader.CostChip(priced));

      var one = SpendMath.Sum(new[] { new StoredSpend { Cents = 0.04 } });
      Assert.Equal("<0.1¢ · 1 turn", ChatHeader.CostChip(one));

      var unpriced = SpendMath.Sum(new[] { new StoredSpend { CostText = "local · free" }, new StoredSpend { CostText = "local · free" }, new StoredSpend { CostText = "local · free" } });
      Assert.Equal("not priced · 3 turns", ChatHeader.CostChip(unpriced));

      Assert.Equal("no turns yet", ChatHeader.CostChip(SpendMath.Sum(new StoredSpend[0])));
    }

    [Fact]
    public void Turning_Full_control_on_says_the_settings_sentence_in_the_thread()
    {
      Assert.StartsWith("Full control is on for this chat: ", ChatHeader.FullControlOnLine);
      Assert.EndsWith(FullControl.SettingSentence, ChatHeader.FullControlOnLine);
    }

    // ---- the document the shell draws -------------------------------------------------------------------------------------

    [Fact]
    public void The_header_document_carries_the_chips_the_lists_and_which_entry_is_on()
    {
      var json = (JsonObject)JsonNode.Parse(ChatHeader.Json(Input(i => { i.ApprovalMode = "never"; i.FullControl = true; })));

      Assert.Equal("Balanced · Sonnet 5", json["model"]["text"].GetValue<string>());
      var choices = json["model"]["choices"].AsArray();
      Assert.Equal(6, choices.Count);   // four postures and two agentic models
      Assert.Equal(new[] { "posture:balanced" }, choices.Where(c => c["on"].GetValue<bool>()).Select(c => c["id"].GetValue<string>()).ToArray());

      Assert.Equal("Do not ask", json["approval"]["text"].GetValue<string>());
      var modes = json["approval"]["choices"].AsArray();
      Assert.Equal(new[] { "ask", "scripts_and_deletions", "never" }, modes.Select(c => c["id"].GetValue<string>()).ToArray());
      Assert.Equal(new[] { "never" }, modes.Where(c => c["on"].GetValue<bool>()).Select(c => c["id"].GetValue<string>()).ToArray());
      Assert.Equal(ApprovalMode.Description("never"), modes[2]["title"].GetValue<string>());

      Assert.True(json["full"]["on"].GetValue<bool>());
      Assert.Equal("Full control on", json["full"]["text"].GetValue<string>());
      Assert.Equal(FullControl.SettingSentence, json["full"]["title"].GetValue<string>());
      Assert.Equal("~23.5¢ · 3 turns · 12 steps a turn", json["cost"]["text"].GetValue<string>());
    }

    [Fact]
    public void A_pinned_model_is_the_entry_that_is_on_and_no_posture_is()
    {
      var opus = Row("a-2", "Opus 5", "Anthropic", true);
      var json = (JsonObject)JsonNode.Parse(ChatHeader.Json(Input(i => { i.ThreadModelId = "a-2"; i.ThreadPin = opus; i.Resolved = opus; })));
      var on = json["model"]["choices"].AsArray().Where(c => c["on"].GetValue<bool>()).Select(c => c["id"].GetValue<string>()).ToArray();
      Assert.Equal(new[] { "a-2" }, on);
    }

    // ---- the Settings hint, the wiring, the docs ---------------------------------------------------------------------

    private static string RepoFile(params string[] parts)
    {
      var dir = AppContext.BaseDirectory;
      while (dir != null && !File.Exists(Path.Combine(dir, "Dwight.sln"))) dir = Path.GetDirectoryName(dir);
      Assert.NotNull(dir);
      return File.ReadAllText(Path.Combine(new[] { dir }.Concat(parts).ToArray()));
    }

    [Fact]
    public void Settings_says_a_chat_keeps_its_own_choices_and_these_are_the_defaults()
    {
      Assert.Equal("A chat keeps its own model, approval mode and Full control; change them under the chat's text box. These are the defaults for a new chat.", ChatHeader.SettingsHint);
      Assert.Contains(ChatHeader.SettingsHint, RepoFile("Dwight", "Resources", "settings.html"));
      Assert.Contains("Dwight.Agent.ChatHeader.SettingsHint", RepoFile("Dwight", "UI", "SettingsDialog.cs"));
    }

    [Fact]
    public void The_host_resolves_the_threads_pin_first_through_the_same_front_door_and_writes_every_change_on_the_thread()
    {
      var cs = RepoFile("Dwight", "UI", "DwightScaffold.cs");
      Assert.Contains("settings.ResolveChatModel(threadModelId, CommandHistory.ThreadPosture(doc, threadId), out var threadPinIgnored, out var effectivePinId)", cs);
      Assert.Contains("AgentFrontDoor.Check(effectivePinId, resolved?.Model, settings.EffectiveProvider,", cs);   // the front door is unchanged; only its pin argument is the thread's
      Assert.Contains("SaidPinFallback(threadId, threadModelId)", cs);
      Assert.Contains("ChatHeader.PinFallbackLine", cs);
      Assert.Contains("wp.OnHeader = cmd => OnHeaderCommand(wp, doc, threadId, cmd);", cs);
      Assert.Contains("ChatHeader.IsOffered(id, ModelCatalogue.ForProvider(DwightSettings.Current.EffectiveProvider))", cs);   // the host checks before it writes
      Assert.Contains("if (on && !was) AppendPluginHtml(wp, doc, threadId, System.Net.WebUtility.HtmlEncode(ChatHeader.FullControlOnLine));", cs);
      Assert.Contains("RefreshHeader(wp, doc, threadId);", cs);

      var settings = RepoFile("Dwight", "Config", "DwightSettings.cs");
      Assert.Contains("Dwight.Agent.ChatHeader.PickPin(threadModelId, threadPin, settingsPin, EffectiveProvider, out threadPinIgnored)", settings);

      var panel = RepoFile("Dwight", "UI", "WebPanel.cs");
      Assert.Contains("cmd.StartsWith(\"hdr:\"", panel);
      Assert.Contains("dwightChatHeader(", panel);
    }

    [Fact]
    public void The_shell_draws_text_never_markup_and_posts_every_change_through_the_host()
    {
      var html = RepoFile("Dwight", "Resources", "panel-shell.html");
      Assert.Contains("function dwightChatHeader(json)", html);
      Assert.Contains("e.textContent=text", html);
      Assert.Contains("dwightSend('hdr:'+which+':'+c.id)", html);
      Assert.Contains("dwightSend('hdr:full:'+(s.full.on?'off':'on'))", html);
      var css = RepoFile("Dwight", "Resources", "dwight-components.css");
      Assert.Contains(".chathdr{display:none;", css);   // chat mode only: hidden until the host draws it
      Assert.Contains(".chathdr .linecost{", css);
      Assert.Contains("font:var(--type-caption) var(--font-mono)", css);
    }

    [Fact]
    public void The_render_harness_covers_the_header_in_each_state_light_and_dark()
    {
      var harness = RepoFile("tools", "check-shell-render.js");
      Assert.Contains("'dwightChatHeader'", harness);
      Assert.Contains("'dwightHdrMenu'", harness);
      Assert.Contains("Full control on", harness);
      Assert.Contains("TIP-FULL-ON", harness);
      Assert.Contains("not priced", harness);
      Assert.Contains("['dwightSetTheme', ['dark'], { pre: [['dwightChat', ['[]']], ['dwightChatHeader'", harness);
    }

    // ---- Brief BD: the controls sit under the text box, as small glyphs with short labels --------------------------------

    [Fact]
    public void The_controls_row_is_part_of_the_composer_and_the_title_bar_no_longer_carries_it()
    {
      var html = RepoFile("Dwight", "Resources", "panel-shell.html");

      // The row is built by dwightChat, inside the composer's frame, after the send button; the static markup under the title bar is gone.
      Assert.Contains("<div class=\"chathdr\" id=\"chathdr\"></div></div>'+", html);
      Assert.DoesNotContain("    <div class=\"chathdr\" id=\"chathdr\"></div>\n    <div class=\"doc\">", html.Replace("\r\n", "\n"));
      Assert.Contains("try{ _hdrDraw(); }catch(e){}", html);        // drawn again whenever the composer is rebuilt, from what the host last sent
      Assert.Contains("function _hdrDraw()", html);
      var css = RepoFile("Dwight", "Resources", "dwight-components.css");
      Assert.Contains("order:9", css);                                // after the textarea, the stop and the send button in tab order
      Assert.Contains(".chathdr .chipmenu{position:absolute;right:0;bottom:100%", css);   // the picker opens upward from the row
    }

    // ---- Brief BI: the composer's one line. The cost on the left, three icons on the right, the footer paragraph gone. ------------------

    [Fact]
    public void The_three_are_icons_and_the_cost_is_the_only_text_on_the_line()
    {
      var html = RepoFile("Dwight", "Resources", "panel-shell.html");
      var sprite = RepoFile("Dwight", "Resources", "dwight-icons.svg");
      foreach (var glyph in new[] { "id=\"ic-model\"", "id=\"ic-full\"" }) Assert.Contains(glyph, sprite);
      Assert.Contains("u.setAttribute('href','#ic-'+icon)", html);   // the one icon table, under the id the host sends
      Assert.Contains("_hdrIcon('hdr-model','','model',s.model.title,", html);
      Assert.Contains("_hdrIcon('hdr-approval','',s.approval.mode,s.approval.title,", html);
      Assert.Contains("_hdrIcon('hdr-full',s.full.on?'on':'','full',s.full.tip,", html);
      Assert.Contains("b.setAttribute('aria-label',title)", html);   // the tooltip is the accessible name
      Assert.DoesNotContain("<img", html.Substring(html.IndexOf("function _hdrIcon", StringComparison.Ordinal), 700));
      var css = RepoFile("Dwight", "Resources", "dwight-components.css");
      Assert.Contains(".iconbtn.on .ic{fill:currentColor}", css);   // filled when on, outlined when off
    }

    [Fact]
    public void The_shield_has_one_variant_per_mode()
    {
      var sprite = RepoFile("Dwight", "Resources", "dwight-icons.svg");
      foreach (var mode in ApprovalMode.All) Assert.Contains("id=\"ic-" + mode + "\"", sprite);
      Assert.Equal("never", ((JsonObject)JsonNode.Parse(ChatHeader.Json(Input(i => i.ApprovalMode = "never"))))["approval"]["mode"].GetValue<string>());
    }

    [Fact]
    public void The_tooltips_are_pinned_per_state()
    {
      Assert.Equal("Model: Balanced chooses, Sonnet 5 now", ChatHeader.ModelTip(Input()));
      Assert.Equal("Model: Ultra chooses", ChatHeader.ModelTip(Input(i => { i.Posture = "ultra"; i.Resolved = null; })));
      var opus = Row("a-2", "Opus 5", "Anthropic", true);
      Assert.Equal("Model: Opus 5, pinned for this chat", ChatHeader.ModelTip(Input(i => { i.ThreadModelId = "a-2"; i.ThreadPin = opus; i.Resolved = opus; })));
      var stale = Row("gpt-x", "GPT-5.6 Terra", "OpenAI", true);
      Assert.Equal("Model: GPT-5.6 Terra, not on this provider; the profile chooses", ChatHeader.ModelTip(Input(i => { i.ThreadModelId = "gpt-x"; i.ThreadPin = stale; })));
      Assert.Equal("Model: some-old-id, not on this provider; the profile chooses", ChatHeader.ModelTip(Input(i => { i.ThreadModelId = "some-old-id"; i.ThreadPin = null; })));

      Assert.Equal("Approval: ask every time", ChatHeader.ApprovalTip("ask"));
      Assert.Equal("Approval: ask for scripts and deletions only", ChatHeader.ApprovalTip("scripts_and_deletions"));
      Assert.Equal("Approval: do not ask", ChatHeader.ApprovalTip("never"));
      Assert.Equal("Approval: ask every time", ChatHeader.ApprovalTip("nonsense"));   // the normaliser's default

      Assert.Equal("Full control: off", ChatHeader.FullControlTip(false));
      Assert.Equal("Full control: on", ChatHeader.FullControlTip(true));
    }

    [Fact]
    public void The_document_carries_the_tooltips_the_mode_and_the_receipt()
    {
      var json = (JsonObject)JsonNode.Parse(ChatHeader.Json(Input(i => { i.FullControl = true; i.Receipt = "Sonnet 5 · 7.8s · ~7.0¢"; })));
      Assert.Equal("Model: Balanced chooses, Sonnet 5 now", json["model"]["title"].GetValue<string>());
      Assert.Equal("Approval: ask every time", json["approval"]["title"].GetValue<string>());
      Assert.Equal("Full control: on", json["full"]["tip"].GetValue<string>());
      Assert.Equal("Sonnet 5 · 7.8s · ~7.0¢", json["cost"]["title"].GetValue<string>());
      Assert.Equal("", ((JsonObject)JsonNode.Parse(ChatHeader.Json(Input())))["cost"]["title"].GetValue<string>());
    }

    [Fact]
    public void The_receipt_reads_back_from_the_store()
    {
      var s = new StoredSpend { ElapsedMs = 7800, Cents = 7.04, CacheReadTokens = 100000, InputTokens = 18000, OutputTokens = 1100, Steps = 3, ToolCalls = 2 };
      Assert.Equal("Sonnet 4.6 · 7.8s · ~7.0¢ · cached · 3 steps · 2 tool calls · 118k in / 1.1k out", SpendMath.TurnReceipt(s, "Sonnet 4.6"));
      Assert.Equal("1.0s · local · free · 1 step", SpendMath.TurnReceipt(new StoredSpend { ElapsedMs = 1000, CostText = "local · free", Steps = 1 }, null));
      Assert.Equal("", SpendMath.TurnReceipt(null, "x"));
      Assert.Equal("", SpendMath.TokensLine(new StoredSpend()));
      Assert.Equal("118k in / 1.1k out", SpendMath.TokensLine(s));
    }

    [Fact]
    public void The_cost_opens_Settings_at_Usage_through_the_host()
    {
      var html = RepoFile("Dwight", "Resources", "panel-shell.html");
      Assert.Contains("dwightSend('hdr:usage')", html);
      var scaffold = RepoFile("Dwight", "UI", "DwightScaffold.cs");
      Assert.Contains("if (cmd == \"usage\") { DwightSettingsPanel.OpenAtUsage(); return; }", scaffold);
      Assert.Contains("function dwightOpenUsage()", RepoFile("Dwight", "Resources", "settings.html"));
    }

    [Fact]
    public void The_footer_paragraph_is_gone_from_a_chat_and_the_step_line_has_a_home()
    {
      var scaffold = RepoFile("Dwight", "UI", "DwightScaffold.cs");
      Assert.DoesNotContain("SpendMath.FooterLine", scaffold);
      Assert.DoesNotContain("wp.SetStatus(metaLine)", scaffold);
      Assert.Contains(".doc.chatmode~.foot{display:none}", RepoFile("Dwight", "Resources", "dwight-components.css"));
      var html = RepoFile("Dwight", "Resources", "panel-shell.html");
      Assert.Contains("if(d&&d.classList.contains('chatmode')){ _live=text||''; _hdrCost(); return; }", html);   // a status in a chat is the live step, on the line
    }

    [Fact]
    public void Nothing_the_host_already_sent_changes()
    {
      // Brief AO's fields and hdr: commands all stand; Brief BI adds to them.
      var html = RepoFile("Dwight", "Resources", "panel-shell.html");
      Assert.Contains("dwightSend('hdr:'+which+':'+c.id)", html);
      Assert.Contains("dwightSend('hdr:full:'+(s.full.on?'off':'on'))", html);
      Assert.Equal("Full control on", ChatHeader.FullControlChip(true));
      Assert.Equal("Full control off", ChatHeader.FullControlChip(false));
    }
  }
}
