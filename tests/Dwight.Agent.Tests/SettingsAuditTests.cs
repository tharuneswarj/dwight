using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// Brief BO (Tharun, 31 October: "think from scratch"): Settings from what the user came to do. One group at a time on a rail of icons that opens into
  /// words, a dropdown for every choice with its sentence in the menu, a real switch for every on or off, and the Eto fallback in the same order with its
  /// explanations as tooltips. These read the shipped files, so the page cannot drift back.
  /// </summary>
  public class SettingsAuditTests
  {
    private static string Html => Read("Dwight", "Resources", "settings.html");

    private static readonly string[] Groups = { "Connection", "Model", "Explain", "Build", "Reorganise", "Notes", "You", "Usage", "About" };

    [Fact]
    public void The_groups_are_in_the_order_a_user_comes_for_them_on_the_rail_and_in_the_page()
    {
      var html = Html;
      Assert.Equal(Groups, Regex.Matches(html, "data-pane=\"(\\w+)\"").Select(m => m.Groups[1].Value));
      Assert.Equal(Groups, Regex.Matches(html, "<button class=\"railitem\" data-g=\"(\\w+)\"").Select(m => m.Groups[1].Value));
      foreach (var g in Groups) Assert.Contains("<span class=\"railword\">" + g + "</span>", html);
      foreach (var g in Groups) Assert.Contains("<h1 class=\"navtitle\">" + g + "</h1>", html);
      Assert.DoesNotContain("Help and Advanced", html);
      Assert.DoesNotContain("<h2", html);
    }

    [Fact]
    public void The_rail_opens_over_the_content_and_its_pin_is_a_remembered_setting()
    {
      var css = Read("Dwight", "Resources", "dwight-components.css");
      Assert.Contains(".railin{position:absolute;", css);   // over the content, so nothing jumps
      Assert.Contains(".railin:hover,.railin:has(:focus-visible){width:var(--rail-open)", css);
      Assert.Contains(".railin:hover{transition:width var(--dur-fast) var(--ease-out) var(--dur-base)}", css);   // a short delay in, none out
      Assert.Contains(".railslot.pinned{width:var(--rail-open)}", css);   // pinned, it pushes the content aside
      Assert.Contains(">Keep names</span>", Html);
      var panel = Read("Dwight", "UI", "DwightSettingsPanel.cs");
      Assert.Contains("[\"railPinned\"] = s.SettingsRailPinned,", panel);
      Assert.Contains("st.SettingsRailPinned = v == \"on\"; st.Save();", panel);
      Assert.Contains("public bool SettingsRailPinned { get; set; }", Read("Dwight", "Config", "DwightSettings.cs"));
    }

    [Fact]
    public void No_info_icon_is_left_because_each_explanation_sits_where_it_is_needed()
    {
      var html = Html;
      Assert.DoesNotContain("infobtn", html);
      Assert.DoesNotContain("toggleHint", html);
      Assert.DoesNotContain("collapsible", html);
      Assert.DoesNotContain("infobtn", Read("Dwight", "Resources", "dwight-components.css"));
    }

    [Fact]
    public void On_or_off_is_a_real_switch_whose_sentence_shows_when_it_is_on()
    {
      var html = Html;
      foreach (var id in new[] { "fullcontrol", "websearch" })
      {
        Assert.Contains("<button class=\"sw\" id=\"" + id + "\" role=\"switch\"", html);
        Assert.Contains("id=\"hint-" + id + "\" hidden>", html);
      }
      Assert.Contains("document.getElementById(sentence).hidden=!on;", html);
      Assert.Contains(".sw{", Read("Dwight", "Resources", "dwight-components.css"));
      Assert.Contains("border-radius:var(--radius-switch)", Read("Dwight", "Resources", "dwight-components.css"));   // Tharun's pick: round
    }

    [Fact]
    public void A_controls_label_is_at_most_three_words()
    {
      var labels = Regex.Matches(Html, "<label>([^<]+)</label>").Select(m => m.Groups[1].Value.Trim()).ToList();
      Assert.Contains("Build style", labels);
      Assert.Contains("Your name", labels);
      Assert.Contains("Pin a model", labels);
      foreach (var l in labels)
      {
        if (l == "Models on your key") continue;   // Tharun's own name for it (his note); the one four-word label, kept on purpose
        Assert.True(l.Split(' ').Length <= 3, "label longer than three words: " + l);
      }
    }

    [Fact]
    public void Usage_reads_the_ledger_only_when_its_group_is_opened()
    {
      var html = Html;
      Assert.Contains("<section class=\"pane\" data-pane=\"Usage\" hidden>", html);
      Assert.Contains("if(id==='Usage'&&was!=='Usage') dwightSend('usage:open');", html);
      var panel = Read("Dwight", "UI", "DwightSettingsPanel.cs");
      Assert.Contains("if (cmd == \"usage:open\") { PushUsage(); return; }", panel);
      var init = Between(panel, "private void PushInit()", "private void PushUsage()");
      Assert.DoesNotContain("PushUsage();", init);   // nothing is read when Settings opens
    }

    [Fact]
    public void Each_option_keeps_its_name_and_its_sentence_in_the_source_so_the_menu_says_them_word_for_word()
    {
      var html = Html;
      // the sentences stay whole in the source, so what a test pins and what the user reads are the same words
      foreach (var mode in ApprovalMode.All) Assert.Contains("<div class=\"opt\">" + ApprovalMode.Description(mode) + "</div>", html);
      Assert.Contains("<div class=\"optnote\">" + ApprovalMode.ThreadHint + "</div>", html);
      Assert.Contains(">" + FullControl.SettingSentence + "</div>", html);
      Assert.Contains(">" + ChatHeader.SettingsHint + "</div>", html);
      Assert.Contains("function ddOpts(id){", html);
    }

    [Fact]
    public void Each_control_sits_in_the_group_a_user_looks_for_it_in()
    {
      var html = Html;
      string Group(string from, string to) => Between(html, "data-pane=\"" + from + "\"", to == null ? "<div class=\"foot\">" : "data-pane=\"" + to + "\"");
      var connection = Group("Connection", "Model");
      Assert.Contains("id=\"provider\"", connection);
      Assert.Contains("id=\"keyst\"", connection);
      Assert.Contains("<label id=\"modelshead\">Models on your key</label>", connection);   // Brief FN: the host words it for the provider (LocalBadge.ModelsHeading)
      var explain = Group("Explain", "Build");
      Assert.Contains("<label>Dim canvas</label>", explain);
      var model = Group("Model", "Explain");
      Assert.Contains("<label>Profile</label>", model);
      Assert.Contains("<label>Pin a model</label>", model);
      var build = Group("Build", "Reorganise");
      Assert.Contains("<label>Build style</label>", build);
      Assert.Contains("<label>Approval</label>", build);
      Assert.Contains("<label>Full control</label>", build);
      Assert.Contains("<label>Web search</label>", build);
      var you = Group("You", "Usage");
      Assert.Contains("<label>Your name</label>", you);
      Assert.Contains("<label>Theme</label>", you);
      var about = Group("About", null);
      Assert.Contains("<label>Model catalogue</label>", about);
      Assert.Contains("<label>What Dwight sends</label>", about);
      Assert.DoesNotContain("<label>Pin a model</label>", about);
    }

    [Fact]
    public void The_approval_modes_have_their_full_names_back()
    {
      // Tharun (Brief BO): "Every time" and "Scripts, deletions" were a cost of the wrong control.
      var html = Html;
      foreach (var mode in ApprovalMode.All) Assert.Contains("<div class=\"opt\">" + ApprovalMode.Label(mode) + ": ", html);
      Assert.DoesNotContain(">Every time<", html);
      Assert.DoesNotContain("Scripts, deletions", html);
    }

    [Fact]
    public void Every_choice_is_a_dropdown_anywhere_and_the_measuring_leg_holds_its_names()
    {
      // Tharun (Brief BO): "let the dropdowns be dropdowns", Usage's period and the Arrangement on a result included.
      foreach (var shell in new[] { "settings.html", "panel-shell.html", "dock-shell.html", "feedback.html", "note.html", "tour.html" })
        Assert.DoesNotContain("class=\"seg", Read("Dwight", "Resources", shell));
      Assert.Contains("id=\"usage-period\" data-vals=\"0 1 2 3\"", Html);
      Assert.Contains("ddMake(dd,{ label:'Arrangement'", Read("Dwight", "Resources", "panel-shell.html"));
      Assert.DoesNotContain(".seg", Read("Dwight", "Resources", "dwight-components.css"));
      var measure = Read("tools", "ui-measure.js");
      Assert.Contains("out.ddOverflow", measure);
      Assert.Contains("out.menuOver", measure);
      Assert.Contains("id: 'settings-menu-'", Read("tools", "ui-fixtures.js"));
      Assert.Contains("role','combobox'", Read("Dwight", "Resources", "dwight-dropdown.js"));
    }

    // The closing PR: every surface is on the system, so the switch that brought them over one at a time is gone, and stays gone.
    [Theory]
    [InlineData("panel-shell.html")]
    [InlineData("dock-shell.html")]
    [InlineData("settings.html")]
    [InlineData("feedback.html")]
    [InlineData("note.html")]
    [InlineData("tour.html")]
    [InlineData("dwight-components.css")]
    [InlineData("dwight-tokens.css")]
    public void The_migration_switch_is_gone(string file)
    {
      var text = Read("Dwight", "Resources", file);
      Assert.DoesNotContain("body.sys", text);
      Assert.DoesNotContain("class=\"sys\"", text);
      Assert.DoesNotContain("@tier:legacy", text);
      Assert.DoesNotContain("@tier:migration", text);
    }

    // Brief BQ: the tour's chat picture draws the list the chat really writes when a map lands, not chips nothing in the product writes.
    [Fact]
    public void The_tours_chat_picture_shows_what_the_chat_writes()
    {
      var tour = Read("Dwight", "Resources", "tour.html");
      Assert.DoesNotContain("gref", tour);
      Assert.DoesNotContain("gref", Read("Dwight", "Resources", "dwight-components.css"));
      Assert.DoesNotContain("gref", Read("tools", "ui-style-audit.js"));
      Assert.Contains("class=\"maplist\"", tour);
      Assert.Contains("class=\\\"maplist\\\"", Read("Dwight", "Agent", "ScaffoldMapRecord.cs"));
    }

    // Brief BR: one bar in every view, with nothing left of the window that fitted itself or the theme button that lived in it.
    [Fact]
    public void There_is_one_title_bar_and_the_chats_bar_has_no_theme_button()
    {
      var panel = Read("Dwight", "Resources", "panel-shell.html");
      var home = Read("Dwight", "Resources", "dock-shell.html");
      foreach (var gone in new[] { "themebtn", "dwightToggleTheme", "_fit", "fit:", "_userSized" }) Assert.DoesNotContain(gone, panel);
      Assert.DoesNotContain("\"fit:", Read("Dwight", "UI", "DwightDock.cs"));
      Assert.DoesNotContain("\"theme:", Read("Dwight", "UI", "WebPanel.cs"));
      Assert.Contains("class=\"titlebar\" id=\"bar\"", panel);
      Assert.Contains("class=\"titlebar dhead\" id=\"tab\"", home);
      Assert.Contains("iconbtn navbtn", panel);
      Assert.Contains("iconbtn navbtn", home);
    }

    // The chat's label takes its mark from a template now the bar has none in a conversation, or every Dwight label would lose it silently.
    [Fact]
    public void The_chats_label_still_has_its_mark()
    {
      var panel = Read("Dwight", "Resources", "panel-shell.html");
      Assert.Contains("<template id=\"dwmark\"><!--__DWIGHT_MARK__--></template>", panel);
      Assert.Contains("document.getElementById('dwmark').innerHTML", panel);
    }

    // Tharun, #1160: the chip pulses for as long as the state lasts. Under reduced motion the global rule stops the animation, and the ring must then stand at full strength, so the dimming lives only in the keyframes.
    [Fact]
    public void The_chip_pulses_and_holds_still_under_reduced_motion()
    {
      var css = Read("Dwight", "Resources", "dwight-components.css");
      Assert.Contains("animation:chippulse var(--loop-breathe) var(--ease-in-out) infinite", css);
      Assert.Contains("@keyframes chippulse{0%,100%{opacity:1}50%{opacity:.35}}", css);
      Assert.Contains("*,*::before,*::after{animation:none !important", css);
      Assert.DoesNotContain("islive-waiting", css);
    }

    // Tharun on the picker: "it shouldn't look like a list." A group's label is a heading, in the body face and the ink, with the options set in under it. The gate measures it in a browser; this holds the words of the rule.
    [Fact]
    public void A_menus_group_label_is_a_heading_not_a_choice()
    {
      var css = Read("Dwight", "Resources", "dwight-components.css");
      Assert.Contains(".chathdr .chipmenu .grp{padding:var(--space-s) var(--space-s) var(--space-xs);font:var(--type-label) var(--font-body);color:var(--text);cursor:default;", css);
      Assert.Contains(".chathdr .chipmenu .grp~.it{padding-left:var(--space-l)}", css);
      Assert.Contains("g.setAttribute('role','heading')", Read("Dwight", "Resources", "panel-shell.html"));
    }

    // Brief BR: the words follow the one window. A consent sentence must be true in every state that reaches it, and the tour must not name a place that has gone.
    [Fact]
    public void The_product_says_history_and_the_dwight_icon_not_the_dock_or_the_panel()
    {
      var card = Read("Dwight", "Agent", "ConsentCard.cs");
      Assert.Contains("replaces this chat in the window - your conversation stays in History and reopens from there.", card);
      Assert.DoesNotContain("stays in the dock", card);
      Assert.DoesNotContain("replaces this panel", card);
      var tour = Read("Dwight", "Resources", "tour.html");
      Assert.Contains("<span class=\"lbl\">The Dwight icon</span>", tour);
      Assert.Contains("bottom right", tour);
      Assert.Contains("History keeps every answer.", tour);
      Assert.DoesNotContain("The dock", tour);
      Assert.Contains("or the Dwight icon (bottom right).", Read("Dwight", "UI", "FirstRunCore.cs"));
      Assert.Contains("from the result after any Reorganise.", Read("Dwight", "Resources", "settings.html"));
    }

    // The user's word is Profile, everywhere they read it. The code's name stays posture, and that is not on screen.
    [Fact]
    public void The_user_reads_profile_never_posture()
    {
      var panel = Read("Dwight", "Resources", "panel-shell.html");
      Assert.Contains("c.group==='posture'?'Profile':'Models that run the chat'", panel);
      Assert.DoesNotContain("'Posture'", panel);
      Assert.Contains("\"Off (use the profile)\"", Read("Dwight", "UI", "DwightSettingsPanel.cs"));
      var header = Read("Dwight", "Agent", "ChatHeader.cs");
      Assert.Contains("the profile chooses", header);
      Assert.DoesNotContain("used the posture", header);
      Assert.DoesNotContain("the posture chooses\"", header);   // the sentence the user reads; the code's comments may say posture
    }

    // The library held a bare .st for the Timeline's step, and Settings' key status is class="st": the sentence went into a 16px grid column, one word per line (#139). The gate measures the row; this holds the name.
    [Fact]
    public void The_key_status_has_no_rule_meant_for_the_timeline()
    {
      var css = Read("Dwight", "Resources", "dwight-components.css");
      Assert.DoesNotMatch(new System.Text.RegularExpressions.Regex(@"(^|[\s,}])\.st[\s.{,]"), css.Replace(".keyrow .st{", ""));
      Assert.Contains(".keyrow .st{flex:1;min-width:0;", css);
      Assert.Contains("'<div class=\"ev-step '", Read("Dwight", "Resources", "dock-shell.html"));
    }

    // Back from a conversation is History, whatever the home page last showed.
    [Fact]
    public void The_three_lines_land_on_history()
    {
      Assert.Contains("if (fromPage) Run(\"dwightDockHome()\");", Read("Dwight", "UI", "DwightDock.cs"));
      Assert.Contains("function dwightDockHome(){ dockView('list'); }", Read("Dwight", "Resources", "dock-shell.html"));
    }

    // Section 9: undone rows go duller and are never struck through, which reads as deleted, and they return to full contrast when reached.
    [Fact]
    public void Undone_rows_go_dull_and_are_not_struck_through()
    {
      var css = Read("Dwight", "Resources", "dwight-components.css");
      Assert.DoesNotContain("line-through", css);
      Assert.Contains(".ev.undone>.ev-head .t,", css);
      Assert.Contains(".ev.undone>.ev-head:hover .t,.ev.undone>.ev-head:focus-visible .t,.ev.undone.open>.ev-head .t,.ev.undone.inwalk>.ev-head .t{color:var(--text)}", css);
    }

    // Section 9: no cost or tokens in the list, and the Timeline's state is the mark, not a word on the row.
    [Fact]
    public void The_list_shows_no_cost_and_the_timeline_no_state_tag()
    {
      var dock = Read("Dwight", "UI", "DwightDock.cs");
      Assert.DoesNotContain("MetaTail", dock);
      // The row's writer moved to DockRows (Brief DG); what this holds has not: the meta is the time, and the writer is handed no spend.
      Assert.Contains("Ago(c.Time), v.From));", dock);
      var shell = Read("Dwight", "Resources", "dock-shell.html");
      Assert.DoesNotContain("class=\"tag'+(st==='standing'", shell);
      Assert.Contains("title=\"'+esc(word)+'\" aria-label=\"'+esc(word)+'\"", shell);
    }

    // The dock's height is one number. The fixtures measure at the number the host opens to, so changing it cannot leave the gate measuring the old window.
    [Fact]
    public void The_dock_is_measured_at_the_height_it_opens_to()
    {
      var h = System.Text.RegularExpressions.Regex.Match(Read("Dwight", "UI", "OneWindowCore.cs"), @"WindowMaxH = (\d+)").Groups[1].Value;
      Assert.False(string.IsNullOrEmpty(h));
      Assert.Contains("const DOCK_H = " + h + ";", Read("tools", "ui-fixtures.js"));
    }

    [Fact]
    public void About_says_what_the_tour_shows_and_makes_no_promise_the_approval_modes_break()
    {
      Assert.Contains("how to reach Dwight, and how it shows you what it changes.", Html);
      Assert.DoesNotContain("never do without", Html);   // false under "Do not ask"
    }

    // R11: the fallback's one defect. It is reached only with no WebView2, so a test reads the source and works out the contrast from the tokens it now uses.
    [Theory]
    [InlineData("Dwight/UI/Drawing/DocumentView.cs", "FromArgb(202, 202, 202)")]
    [InlineData("Dwight/UI/Drawing/DocumentView.cs", "FromArgb(245, 245, 245)")]
    [InlineData("Dwight/UI/Drawing/DocumentView.cs", "FromArgb(214, 214, 214)")]
    [InlineData("Dwight/UI/Drawing/DocumentView.cs", "FillPath(Brand.Black")]
    [InlineData("Dwight/UI/SettingsDialog.cs", "FromArgb(15, 15, 15)")]
    [InlineData("Dwight/UI/SettingsDialog.cs", "FromArgb(31, 31, 31)")]
    [InlineData("Dwight/UI/SettingsDialog.cs", "FromArgb(23, 23, 23)")]
    public void The_fallback_holds_no_colour_chosen_for_one_theme(string file, string gone)
    {
      Assert.DoesNotContain(gone, Read(file.Split('/')));
    }

    [Fact]
    public void The_fallback_follows_the_theme_the_user_chose()
    {
      var brand = Read("Dwight", "Branding", "Brand.cs");
      Assert.Contains("public static bool IsDark => Dwight.UI.DwightTheme.Current() == \"dark\";", brand);
      Assert.Contains("(Brand.WindowsDark ? \"dark\" : \"light\")", Read("Dwight", "UI", "DwightTheme.cs"));
      Assert.DoesNotContain("Brand.WindowsDark", Read("Dwight", "UI", "Drawing", "DocumentView.cs"));
      Assert.DoesNotContain("Brand.WindowsDark", Read("Dwight", "UI", "ResultPopup.cs"));
    }

    [Fact]
    public void The_tours_add_your_key_lands_on_the_field_it_names()
    {
      Assert.Contains("if (cmd == \"settings\") { Close(); DwightCanvasPopup.Safe(() => DwightSettingsPanel.OpenAtKey());", Read("Dwight", "UI", "FirstRunTour.cs"));
      Assert.Contains("function dwightOpenKey(){ dwightSettingsGo('Connection'); showKeyInput(true); }", Html);
      var panel = Read("Dwight", "UI", "DwightSettingsPanel.cs");
      Assert.Contains("if (_keyOnLoad) { _keyOnLoad = false; Run(\"dwightOpenKey()\"); }", panel);
    }

    [Fact]
    public void The_tours_replay_is_a_real_button_and_its_picture_is_only_a_picture()
    {
      var tour = Read("Dwight", "Resources", "tour.html");
      Assert.Contains("<button class=\"replay\" type=\"button\"", tour);
      Assert.Contains("onclick=\"dwightTourReplay()\"", tour);
      Assert.Contains("<div class=\"scenes\" id=\"scenes\">", tour);
    }

    [Fact]
    public void The_chord_menu_draws_the_same_five_verbs_from_the_one_table()
    {
      // Native menus cannot use the sprite, so tools/build-menu-glyphs.js walks the same paths. Every point stays on Tabler's 24 grid.
      var glyphs = Read("Dwight", "Branding", "ActionIcons.Glyphs.cs");
      Assert.Contains("Written by tools/build-menu-glyphs.js", glyphs);
      foreach (var verb in new[] { "Explain", "Diagnose", "Reorganise", "Simplify", "Build" })
      {
        Assert.Contains("private static readonly float[][] " + verb + "Glyph", glyphs);
        Assert.Contains("Draw(g, pen, s, " + verb + "Glyph)", Read("Dwight", "Branding", "ActionIcons.cs"));
      }
      foreach (Match m in Regex.Matches(glyphs, @"(-?\d+(?:\.\d+)?)f"))
      {
        var n = double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(n >= 0 && n <= 24, "a point off the grid: " + n);
      }
    }

    [Fact]
    public void The_eto_fallback_has_the_same_order_and_its_explanations_as_tooltips()
    {
      var eto = Read("Dwight", "UI", "SettingsDialog.cs");
      int I(string s) { var i = eto.IndexOf(s, StringComparison.Ordinal); Assert.True(i > 0, "missing: " + s); return i; }
      Assert.True(I("GroupHeader(\"Provider & models\")") < I("GroupHeader(\"API key\")"));
      Assert.True(I("GroupHeader(\"API key\")") < I("WithTip(GroupHeader(\"Build\"), Dwight.Agent.ChatHeader.SettingsHint)"));
      Assert.True(I("WithTip(GroupHeader(\"Build\")") < I("AddUsage(body);"));
      Assert.True(I("AddUsage(body);") < I("body.Items.Add(GroupHeader(\"Invocation\"));"));
      Assert.Contains("WithTip(_fullControl, Dwight.Agent.FullControl.SettingSentence)", eto);
      Assert.Contains("_approval.ToolTip = Dwight.Agent.ApprovalMode.Description(", eto);
      Assert.DoesNotContain("Hint(Dwight.Agent.FullControl.SettingSentence)", eto);   // an explanation is a tooltip, not a paragraph
    }

    private static string Between(string s, string from, string to)
    {
      var a = s.IndexOf(from, StringComparison.Ordinal); Assert.True(a >= 0, "missing: " + from);
      var b = s.IndexOf(to, a, StringComparison.Ordinal); Assert.True(b > a, "missing after it: " + to);
      return s.Substring(a, b - a);
    }

    private static string Read(params string[] parts)
    {
      var dir = AppContext.BaseDirectory;
      while (dir != null && !File.Exists(Path.Combine(dir, "Dwight.sln"))) dir = Path.GetDirectoryName(dir);
      Assert.NotNull(dir);
      return File.ReadAllText(Path.Combine(new[] { dir }.Concat(parts).ToArray()));
    }
  }
}
