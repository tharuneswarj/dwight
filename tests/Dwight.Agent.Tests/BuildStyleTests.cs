using System.Linq;
using System.Text.Json.Nodes;
using Dwight.Agent;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// U63: the build style. The setting itself lives on DwightSettings (plugin project, not compiled here); what is held here is
  /// everything the setting DOES - how a stored value is read, the fact the model is told, the clause the card carries - and, for
  /// every claim those sentences make about Dwight's own behaviour, a test on the behaviour's side (#315).
  /// </summary>
  public class BuildStyleTests
  {
    private const string Situation = "SITUATION";

    // ---- the stored value ------------------------------------------------------------------------------------

    [Fact]
    public void The_default_is_components_and_a_stored_value_reads_back_as_itself()
    {
      Assert.Equal("components", BuildStyle.Components);
      foreach (var v in BuildStyle.All) Assert.Equal(v, BuildStyle.Normalise(v));
      Assert.Equal(new[] { "components", "scripts", "auto" }, BuildStyle.All);
    }

    [Fact]
    public void An_unknown_blank_or_hand_edited_value_is_the_default_never_an_error()
    {
      Assert.Equal("components", BuildStyle.Normalise(null));
      Assert.Equal("components", BuildStyle.Normalise(""));
      Assert.Equal("components", BuildStyle.Normalise("everything"));
      Assert.Equal("scripts", BuildStyle.Normalise(" Scripts "));
      Assert.False(BuildStyle.IsValid("everything"));
      Assert.False(BuildStyle.IsValid(null));
      Assert.True(BuildStyle.IsValid("auto"));
    }

    // ---- the fact the model is told ----------------------------------------------------------------------------

    [Fact]
    public void The_fact_is_pinned_per_style()
    {
      Assert.Equal("Build style: components.", BuildStyle.Fact("components"));
      Assert.Equal("Build style: components.", BuildStyle.Fact(null));   // an unreadable setting is the default

      var scripts = BuildStyle.Fact("scripts");
      Assert.StartsWith("Build style: scripts - prefer one script component (", scripts);
      Assert.Contains("a loop, a recursion or a formula", scripts);
      Assert.Contains("place inputs (sliders, panels, params) and outputs as components so they stay reachable", scripts);

      var auto = BuildStyle.Fact("auto");
      Assert.StartsWith("Build style: let Dwight choose - components for inputs, outputs and anything the user will tweak", auto);
      Assert.Contains("a script", auto);
      Assert.Contains("algorithmic middle", auto);

      Assert.NotEqual(scripts, auto);
      foreach (var f in BuildStyle.All.Select(BuildStyle.Fact))
      {
        Assert.DoesNotContain(((char)0x2014).ToString(), f);   // no em dash in a sentence a model reads
        Assert.DoesNotContain(((char)0x2013).ToString(), f);
      }
    }

    /// <summary>The claim the script styles make about Dwight - how a script reaches a region build - held against the gate, the
    /// descriptor and the card it describes (#315). If any of them moves, this fails, not the model.</summary>
    [Fact]
    public void The_fact_about_how_a_script_reaches_a_region_build_is_true_of_the_gate_the_descriptor_and_the_card()
    {
      foreach (var style in new[] { "scripts", "auto" })
      {
        var f = BuildStyle.Fact(style);
        Assert.Contains(BuildScriptTypes.PythonTypeName, f);
        Assert.Contains(BuildScriptTypes.CSharpTypeName, f);
        Assert.Contains("request_mutation's \"scripts\"", f);
        Assert.Contains("build_region that same source, unchanged", f);
        Assert.Contains("A script that was not on the card is refused", f);
      }
      Assert.Contains("per region", BuildStyle.Fact("scripts"));
      Assert.Contains("inputs (sliders, panels, params) and outputs as components so they stay reachable", BuildStyle.Fact("scripts"));

      var approved = new[] { new ApprovedScript { Region = "A1", Language = BuildScriptTypes.Python, Source = "a = x" } };
      var comp = new BuildComponent { Ref = "c", Type = BuildScriptTypes.PythonTypeName, Source = "a = x" };
      var spec = new BuildSpec { Region = "A1" };
      spec.Components.Add(comp);

      // the gate: not shown -> refused; shown -> let through; the same words in another region -> refused
      Assert.NotNull(BuildScriptGate.Check(spec, isApprovedSpot: false));
      Assert.Null(BuildScriptGate.Check(spec, isApprovedSpot: false, approvedScripts: approved));
      spec.Region = "A2";
      Assert.NotNull(BuildScriptGate.Check(spec, isApprovedSpot: false, approvedScripts: approved));

      // the descriptor: "scripts" is published with the fields the fact names
      var schema = AgentHostTools.All()[0].InputSchema;
      var item = schema["properties"]["scripts"]["items"]["properties"];
      foreach (var f in new[] { "region", "type", "source", "inputs", "outputs" }) Assert.NotNull(item[f]);

      // the card: it shows what the fact says it shows
      Assert.Contains("Scripts in this build", ConsentCard.ScriptsSection(approved));
    }

    [Fact]
    public void The_fact_reaches_the_system_text_and_an_absent_one_changes_nothing()
    {
      var without = TurnSystemText.Compose(Situation, null, null, TurnCapability.ReadOnly, null);
      var withDefault = TurnSystemText.Compose(Situation, null, null, TurnCapability.ReadOnly, null, null, null, null,
        BuildStyle.Fact("components"));
      var withScripts = TurnSystemText.Compose(Situation, null, null, TurnCapability.ReadOnly, null, null, null, null,
        BuildStyle.Fact("scripts"));

      Assert.DoesNotContain("Build style", without);
      Assert.Contains("\n\nBuild style: components.", withDefault);
      Assert.Contains("\n\nBuild style: scripts - ", withScripts);
      Assert.Equal(without + "\n\nBuild style: components.", withDefault);
    }

    [Fact]
    public void On_an_approved_turn_the_approval_is_still_the_last_thing_the_model_reads()
    {
      var text = TurnSystemText.Compose(Situation, null, null, TurnCapability.Build, new[] { "A1" }, null, null, null,
        BuildStyle.Fact("auto"));
      Assert.True(text.IndexOf("Build style:") < text.IndexOf(ApprovedTurnFact.Heading));
    }

    // ---- the card line -----------------------------------------------------------------------------------------

    [Fact]
    public void The_build_card_line_names_the_style_per_style()
    {
      var components = ConsentCard.BuildAuthority("build", new[] { "A1" }, null, "components");
      Assert.StartsWith("Dwight will place and wire components in region A1, as components - and nowhere else. ", components);

      var scripts = ConsentCard.BuildAuthority("build", new[] { "A1", "A2" }, null, "scripts");
      Assert.StartsWith("Dwight will place and wire components in regions A1, A2, building in scripts where the logic is algorithmic", scripts);
      Assert.Contains("each script's whole source is shown below", scripts);
      Assert.Contains(" - and nowhere else. ", scripts);

      var auto = ConsentCard.BuildAuthority("build", new[] { "A1" }, null, "auto");
      Assert.Contains("choosing components or scripts as the logic suits", auto);

      Assert.NotEqual(scripts, auto);
      Assert.NotEqual(components, auto);
    }

    [Fact]
    public void A_card_with_no_style_is_byte_identical_to_before_and_the_safety_sentence_is_kept()
    {
      Assert.Equal(ConsentCard.BuildAuthority("build", new[] { "A1" }),
                   ConsentCard.BuildAuthority("build", new[] { "A1" }, null, null));
      foreach (var style in BuildStyle.All)
      {
        var line = ConsentCard.BuildAuthority("build", new[] { "A1" }, "It removes B.", style);
        Assert.Contains("It removes B. ", line);            // the removal sentence still precedes the reassurance
        Assert.Contains("reverts itself", line);
        Assert.Contains("Ctrl-Z", line);
        Assert.DoesNotContain(((char)0x2014).ToString(), line);
      }
    }

    [Fact]
    public void The_style_rides_the_full_card_and_a_non_build_card_is_untouched()
    {
      var html = ConsentCard.Html("build", "Because.", "tok", null, new[] { "A1" }, null, null, "scripts");
      Assert.Contains("building in scripts where the logic is algorithmic", html);

      var reorganise = ConsentCard.Html("reorganise", "Because.", "tok", null, null, null, null, "scripts");
      Assert.DoesNotContain("building in scripts", reorganise);
      Assert.Equal(ConsentCard.Html("reorganise", "Because.", "tok"), reorganise);
    }

    // ---- the setting and its control (the plugin project is not compiled here, so its source is read) -----------------

    private static string RepoFile(params string[] parts)
    {
      var dir = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
      while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "Dwight.sln"))) dir = dir.Parent;
      Assert.True(dir != null, "the repository root (Dwight.sln) could not be found from " + System.AppContext.BaseDirectory);
      return System.IO.File.ReadAllText(System.IO.Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray()));
    }

    [Fact]
    public void The_setting_is_persisted_beside_the_others_with_the_defaults_the_brief_gives()
    {
      var settings = RepoFile("Dwight", "Config", "DwightSettings.cs");
      Assert.Contains("public string BuildStyle { get; set; } = Dwight.Agent.BuildStyle.Components;", settings);
      Assert.Contains("public bool WebSearchEnabled { get; set; }", settings);   // a bool defaults to false: off
    }

    [Fact]
    public void The_panel_stores_only_a_valid_value_and_sends_the_stored_one_to_the_control()
    {
      var panel = RepoFile("Dwight", "UI", "DwightSettingsPanel.cs");
      Assert.Contains("[\"buildStyle\"] = Dwight.Agent.BuildStyle.Normalise(s.BuildStyle)", panel);
      Assert.Contains("[\"webSearch\"] = s.WebSearchEnabled", panel);
      Assert.Contains("cmd.StartsWith(\"buildstyle:\"", panel);
      Assert.Contains("if (Dwight.Agent.BuildStyle.IsValid(v))", panel);
      Assert.Contains("cmd.StartsWith(\"websearch:\"", panel);
      Assert.Contains("st.WebSearchEnabled = v == \"on\"", panel);
    }

    [Fact]
    public void The_control_says_the_sentences_it_was_ruled_to_say_verbatim()
    {
      var html = RepoFile("Dwight", "Resources", "settings.html");
      // Brief BO: the same words, one line per option, which the dropdown reads into its menu when the page loads.
      Assert.Contains("<div class=\"opt\">Components: every step is a component on the canvas, sliders reach everything, Dwight checks the wiring.</div>", html);
      Assert.Contains("<div class=\"opt\">Scripts: one C# or Python script per region where the logic is a loop or a formula, inputs and outputs still as components, " +
        "far fewer steps and tokens, the source shown whole before it runs.</div>", html);
      Assert.Contains("<div class=\"opt\">Let Dwight choose: components for inputs, outputs and anything you will tweak, scripts for the algorithmic middle.</div>", html);
      Assert.Contains("Let the model search the web through your provider's own search. Off by default. " +
        "The query leaves your machine exactly as your prompts already do; Dwight itself never fetches a page.", html);
      Assert.Contains("id=\"buildstyle\" data-vals=\"components scripts auto\"", html);
      Assert.Contains("dwightSend('buildstyle:'+v)", html);
      Assert.Contains("dwightSend('websearch:'+v)", html);
    }

    // ---- the descriptor is unchanged ----------------------------------------------------------------------------

    [Fact]
    public void The_style_adds_nothing_to_any_published_descriptor()
    {
      // the style is a fact and a card clause; no tool grew a parameter for it (the model cannot set the user's style)
      foreach (var d in AgentHostTools.All())
        Assert.Null(d.InputSchema["properties"]?["buildStyle"]);
      foreach (var d in AgentToolCatalog.All())
        Assert.Null(d.InputSchema?["properties"]?["buildStyle"]);
    }
  }
}
