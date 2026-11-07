using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using Dwight.Agent;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// U65: scripts inside a region build, on the one condition that keeps the law behind the gate - the approved card showed the
  /// complete source of every script, untruncated, and the build sends that same source. Plus the settings badge that must not
  /// claim "nothing leaves your machine" while web search is on with a local provider.
  /// </summary>
  public class RegionScriptsTests
  {
    private const string Py = "import math\nfor i in range(3):\n    a = i < 2 and x > 0\n";

    private static JsonArray Raw(string region = "A1", string type = "Python 3 Script", string source = Py) =>
      new JsonArray(new JsonObject { ["region"] = region, ["type"] = type, ["source"] = source });

    private static ApprovedScript Approved(string region = "A1", string source = Py) =>
      new ApprovedScript { Region = region, Language = BuildScriptTypes.Python, Source = source };

    private static BuildSpec SpecWith(string region, string source, params string[] inputs)
    {
      var spec = new BuildSpec { Region = region };
      var c = new BuildComponent { Ref = "s", Type = BuildScriptTypes.PythonTypeName, Source = source };
      c.Inputs.AddRange(inputs);
      spec.Components.Add(c);
      return spec;
    }

    // ---- 1. the gate -----------------------------------------------------------------------------------------

    [Fact]
    public void P1_The_gate_accepts_script_source_in_a_region_build_the_card_showed()
    {
      Assert.Null(BuildScriptGate.Check(SpecWith("A1", Py), isApprovedSpot: false, approvedScripts: new[] { Approved() }));
    }

    [Fact]
    public void P1_The_gate_still_refuses_where_the_card_could_not_have_shown_the_source()
    {
      // no scripts on the card
      Assert.NotNull(BuildScriptGate.Check(SpecWith("A1", Py), false, null));
      Assert.NotNull(BuildScriptGate.Check(SpecWith("A1", Py), false, new ApprovedScript[0]));
      // a character changed: since U73 the gate no longer refuses it, it BUILDS THE CARD'S TEXT (see the U73 tests below);
      // a changed PORT is still refused, with the reason and the sentence
      var changed = BuildScriptGate.Check(SpecWith("A1", Py, "n"), false, new[] { Approved() });
      Assert.NotNull(changed);
      Assert.Equal(BuildScriptGate.Reason, changed.Detail?["reason"]?.GetValue<string>());
      Assert.Contains("Nothing was built", changed.Message);
      // the right words in a region the card did not name for it
      Assert.NotNull(BuildScriptGate.Check(SpecWith("A2", Py), false, new[] { Approved("A1") }));
      // an appended port the card did not list
      Assert.NotNull(BuildScriptGate.Check(SpecWith("A1", Py, "n"), false, new[] { Approved() }));
      // one approval covers one script: two identical components need two approved
      var two = SpecWith("A1", Py);
      two.Components.Add(new BuildComponent { Ref = "t", Type = BuildScriptTypes.PythonTypeName, Source = Py });
      Assert.NotNull(BuildScriptGate.Check(two, false, new[] { Approved() }));
      Assert.Null(BuildScriptGate.Check(two, false, new[] { Approved(), Approved() }));
    }

    [Fact]
    public void P1_The_placement_door_is_unchanged()
    {
      Assert.Null(BuildScriptGate.Check(SpecWith("S1", Py), isApprovedSpot: true));
    }

    [Fact]
    public void P1_The_ask_resolves_scripts_by_the_builds_own_parser_and_refuses_what_the_card_cannot_show()
    {
      Assert.True(ScriptsAsk.Resolve(Raw(), new[] { "A1", "A2" }, out var ok, out var e), e?.Message);
      Assert.Single(ok);
      Assert.Equal(Py.TrimEnd('\n'), ok[0].Source);   // normalised exactly as the build's parse normalises it
      Assert.Equal("python", ok[0].Language);

      Assert.False(ScriptsAsk.Resolve(Raw(region: "B9"), new[] { "A1" }, out _, out e));
      Assert.Contains("does not approve", e.Message);
      Assert.False(ScriptsAsk.Resolve(Raw(type: "Panel"), new[] { "A1" }, out _, out e));
      Assert.Contains("not a script component", e.Message);
      Assert.False(ScriptsAsk.Resolve(new JsonArray(new JsonObject { ["region"] = "A1", ["type"] = "C# Script" }), new[] { "A1" }, out _, out e));
      Assert.Contains("no \"source\"", e.Message);
      Assert.False(ScriptsAsk.Resolve(Raw(source: new string('x', EditScriptAsk.MaxSourceChars + 1)), new[] { "A1" }, out _, out e));
      Assert.True(ScriptsAsk.Resolve(null, new[] { "A1" }, out var none, out e) && none.Count == 0);

      var many = new JsonArray(Enumerable.Range(0, ScriptsAsk.MaxScripts + 1)
        .Select(i => (JsonNode)new JsonObject { ["region"] = "A1", ["type"] = "Python 3 Script", ["source"] = Py }).ToArray());
      Assert.False(ScriptsAsk.Resolve(many, new[] { "A1" }, out _, out e));
    }

    [Fact]
    public void P1_What_the_ask_resolved_is_what_the_click_grants_and_the_build_compares()
    {
      // ask -> record -> shape-checked plan -> ledger -> the gate, with the model's own spelling of the source (CRLF) at the build
      Assert.True(ScriptsAsk.Resolve(Raw(source: Py.Replace("\n", "\r\n")), new[] { "A1" }, out var resolved, out _));
      var plan = ScriptsAsk.FromRecord(ScriptsAsk.Record(resolved));
      Assert.Equal(resolved[0].Source, plan[0].Source);

      var ledger = new BuildLedger(new object());
      ledger.AuthoriseScripts(plan);
      ledger.AuthoriseScripts(new[] { Approved("A9", "evil") });   // once only: a turn cannot add to what it was approved to run
      Assert.Single(ledger.ApprovedScripts);

      // the build's own parse of the model's spelling lands on the same bytes the card showed
      var declared = new JsonObject { ["source"] = Py.Replace("\n", "\r\n") };
      Assert.True(BuildScriptParser.TryParse(declared, BuildScriptTypes.PythonTypeName, "s", out var built, out _, out _, out var err), err?.Message);
      var comp = new BuildComponent { Ref = "s", Type = BuildScriptTypes.PythonTypeName, Source = built };
      Assert.True(plan[0].Matches("A1", comp));
    }

    // ---- U73: the card is the contract, so the approved source is built ---------------------------------------

    private const string Card3 = "import math\n# the card\nfor i in range(3):\n    a = i < 2 and x > 0\n";
    private const string Typed3 = "import math\n\n# the card (retyped)\nfor i in range(3):\n    a = i < 2 and x > 0\n";

    private static ApprovedScript B1Card()
    {
      var a = new ApprovedScript { Region = "B1", Language = BuildScriptTypes.Python, Source = Card3 };
      a.Inputs.AddRange(new[] { "baseline", "count", "amplitude" });
      a.Outputs.Add("heights_out");
      return a;
    }

    private static BuildSpec B1Spec(string source, string[] inputs, string[] outputs)
    {
      var spec = new BuildSpec { Region = "B1" };
      var c = new BuildComponent { Ref = "t", Type = BuildScriptTypes.PythonTypeName, Source = source };
      c.Inputs.AddRange(inputs); c.Outputs.AddRange(outputs);
      spec.Components.Add(c);
      return spec;
    }

    [Fact]
    public void U73_A_build_whose_source_differs_by_a_comment_and_a_blank_line_is_built_from_the_cards_source_and_says_where()
    {
      var spec = B1Spec(Typed3, new[] { "baseline", "count", "amplitude" }, new[] { "heights_out" });
      var refused = BuildScriptGate.Adopt(spec, false, new[] { B1Card() }, out var subs);

      Assert.Null(refused);
      Assert.Equal(Card3, spec.Components[0].Source);      // what the placement and the verdict's declared leg now read
      var sub = Assert.Single(subs);
      Assert.True(sub.SourceDiffered);
      Assert.Equal(2, sub.FirstDifferenceLine);            // line 2: the card has "# the card", the model sent a blank line
      Assert.Equal("", sub.SentLine);
      Assert.Equal("# the card", sub.CardLine);
      var json = sub.ToJson();
      Assert.True(json["sourceTakenFromCard"].GetValue<bool>());
      Assert.Equal(2, json["firstDifferenceLine"].GetValue<int>());
      Assert.Contains("line 2", sub.Sentence());
      Assert.Contains("built from the source on the card", sub.Sentence());
    }

    [Fact]
    public void U73_Differing_lines_are_quoted_cut_to_120_characters()
    {
      var long1 = new string('a', 300);
      Assert.True(ScriptsAsk.FirstDifference("x\n" + long1, "x\nb", out var line, out var sent, out var card));
      Assert.Equal(2, line);
      Assert.Equal(120, sent.Length);
      Assert.Equal("b", card);
      Assert.False(ScriptsAsk.FirstDifference("same\r\ntext", "same\ntext", out _, out _, out _));
    }

    [Fact]
    public void U73_A_build_whose_inputs_differ_is_refused_naming_both_lists_with_the_approved_record_in_detail()
    {
      var spec = B1Spec(Typed3, new[] { "count", "amplitude" }, new[] { "heights_out" });
      var refused = BuildScriptGate.Adopt(spec, false, new[] { B1Card() }, out var subs);

      Assert.NotNull(refused);
      Assert.Contains("The card's script for B1 declares inputs baseline, count, amplitude; this build declares count, amplitude", refused.Message);
      Assert.Contains("Nothing was built", refused.Message);
      Assert.Equal("inputs", refused.Detail["fields"][0].GetValue<string>());
      var record = refused.Detail["approved"][0];
      Assert.Equal("B1", record["region"].GetValue<string>());
      Assert.Equal(Card3, record["source"].GetValue<string>());
      Assert.Equal("heights_out", record["outputs"][0].GetValue<string>());
      Assert.Equal(Typed3, spec.Components[0].Source);   // a refusal rewrites nothing
      Assert.Empty(subs);
    }

    [Fact]
    public void U73_A_build_whose_outputs_differ_is_refused_naming_both_lists()
    {
      var spec = B1Spec(Card3, new[] { "baseline", "count", "amplitude" }, new[] { "heights_out", "extra" });
      var refused = BuildScriptGate.Adopt(spec, false, new[] { B1Card() }, out _);
      Assert.Contains("declares outputs heights_out; this build declares heights_out, extra", refused.Message);
      Assert.Equal("outputs", refused.Detail["fields"][0].GetValue<string>());
    }

    [Fact]
    public void U73_Ports_in_a_different_order_match_and_the_cards_order_is_used()
    {
      var spec = B1Spec(Typed3, new[] { "amplitude", "baseline", "count" }, new[] { "heights_out" });
      Assert.Null(BuildScriptGate.Adopt(spec, false, new[] { B1Card() }, out var subs));
      Assert.Equal(new[] { "baseline", "count", "amplitude" }, spec.Components[0].Inputs);
      Assert.True(Assert.Single(subs).PortOrderDiffered);

      // order alone, same text: still reported, and says nothing about source
      var onlyOrder = B1Spec(Card3, new[] { "count", "baseline", "amplitude" }, new[] { "heights_out" });
      Assert.Null(BuildScriptGate.Adopt(onlyOrder, false, new[] { B1Card() }, out var subs2));
      var one = Assert.Single(subs2);
      Assert.False(one.SourceDiffered);
      Assert.Null(one.ToJson()["sourceTakenFromCard"]);
      Assert.True(one.ToJson()["portOrderTakenFromCard"].GetValue<bool>());
    }

    [Fact]
    public void U73b_Port_names_compare_without_case_and_the_cards_spelling_and_order_are_built()
    {
      // the observed case: the card declared line, Count, Amplitude, Frequency; the build sent count, amplitude, frequency, line
      var card = new ApprovedScript { Region = "B1", Language = BuildScriptTypes.Python, Source = Card3 };
      card.Inputs.AddRange(new[] { "line", "Count", "Amplitude", "Frequency" });
      card.Outputs.Add("Heights");
      var spec = B1Spec(Card3, new[] { "count", "amplitude", "frequency", "line" }, new[] { "heights" });

      Assert.Null(BuildScriptGate.Check(spec, false, new[] { card }));
      Assert.Null(BuildScriptGate.Adopt(spec, false, new[] { card }, out var subs));
      Assert.Equal(new[] { "line", "Count", "Amplitude", "Frequency" }, spec.Components[0].Inputs);
      Assert.Equal(new[] { "Heights" }, spec.Components[0].Outputs);
      Assert.True(Assert.Single(subs).PortOrderDiffered);
    }

    [Fact]
    public void U73b_A_true_difference_still_refuses_naming_both_lists()
    {
      var card = new ApprovedScript { Region = "B1", Language = BuildScriptTypes.Python, Source = Card3 };
      card.Inputs.AddRange(new[] { "line", "Count" });
      // U83: the SAME number of inputs under other names is a rename and builds by position, so the genuine difference
      // that still refuses is a different count
      var spec = B1Spec(Card3, new[] { "line", "total", "extra" }, new string[0]);
      var refused = BuildScriptGate.Adopt(spec, false, new[] { card }, out _);
      Assert.NotNull(refused);
      Assert.Contains("declares inputs line, Count; this build declares line, total, extra", refused.Message);
    }

    [Fact]
    public void U73b_Two_names_in_one_list_that_differ_only_by_case_refuse()
    {
      var card = new ApprovedScript { Region = "B1", Language = BuildScriptTypes.Python, Source = Card3 };
      card.Inputs.AddRange(new[] { "count", "amplitude" });
      // the build sends count and Count: two ports, against a card that has one count
      var dup = B1Spec(Card3, new[] { "count", "Count" }, new string[0]);
      Assert.NotNull(BuildScriptGate.Adopt(dup, false, new[] { card }, out _));
      // and a card that holds both does not cover a build that holds count and amplitude
      var dupCard = new ApprovedScript { Region = "B1", Language = BuildScriptTypes.Python, Source = Card3 };
      dupCard.Inputs.AddRange(new[] { "count", "Count" });
      var plain = B1Spec(Card3, new[] { "count", "amplitude" }, new string[0]);
      Assert.NotNull(BuildScriptGate.Adopt(plain, false, new[] { dupCard }, out _));
    }

    [Fact]
    public void U73_An_identical_script_substitutes_nothing()
    {
      var spec = B1Spec(Card3, new[] { "baseline", "count", "amplitude" }, new[] { "heights_out" });
      Assert.Null(BuildScriptGate.Adopt(spec, false, new[] { B1Card() }, out var subs));
      Assert.Empty(subs);
    }

    [Fact]
    public void U73_No_card_and_wrong_region_refusals_name_the_region()
    {
      var spec = B1Spec(Card3, new[] { "baseline", "count", "amplitude" }, new[] { "heights_out" });
      spec.Region = "C1";

      var noCard = BuildScriptGate.Adopt(spec, false, null, out _);
      Assert.Contains("No script was shown for region C1 on the card", noCard.Message);
      Assert.Equal("region", noCard.Detail["fields"][0].GetValue<string>());
      Assert.Null(noCard.Detail["approved"]);

      var wrongRegion = BuildScriptGate.Adopt(spec, false, new[] { B1Card() }, out _);
      Assert.Contains("No script was shown for region C1 on the card", wrongRegion.Message);
    }

    [Fact]
    public void U73_A_different_language_is_refused_with_the_field_named()
    {
      var spec = B1Spec(Card3, new[] { "baseline", "count", "amplitude" }, new[] { "heights_out" });
      spec.Components[0].Type = BuildScriptTypes.CSharpTypeName;
      var refused = BuildScriptGate.Adopt(spec, false, new[] { B1Card() }, out _);
      Assert.Equal("language", refused.Detail["fields"][0].GetValue<string>());
      Assert.Contains("The card's script for B1 is Python 3, this build's script \"t\" is C#", refused.Message);
    }

    [Fact]
    public void U73_One_card_script_still_covers_one_component()
    {
      var spec = B1Spec(Typed3, new[] { "baseline", "count", "amplitude" }, new[] { "heights_out" });
      var second = new BuildComponent { Ref = "u", Type = BuildScriptTypes.PythonTypeName, Source = Typed3 };
      second.Inputs.AddRange(new[] { "baseline", "count", "amplitude" }); second.Outputs.Add("heights_out");
      spec.Components.Add(second);

      var refused = BuildScriptGate.Adopt(spec, false, new[] { B1Card() }, out _);
      Assert.NotNull(refused);
      Assert.Equal("count", refused.Detail["fields"][0].GetValue<string>());
      Assert.Null(BuildScriptGate.Adopt(spec, false, new[] { B1Card(), B1Card() }, out var subs));
      Assert.Equal(2, subs.Count);
    }

    [Fact]
    public void U73_The_placement_door_is_untouched_and_the_panels_line_says_when_the_card_was_built()
    {
      var spec = B1Spec(Typed3, new[] { "baseline" }, new[] { "heights_out" });
      Assert.Null(BuildScriptGate.Adopt(spec, true, null, out var subs));
      Assert.Empty(subs);
      Assert.Equal(Typed3, spec.Components[0].Source);

      var verdict = new JsonObject
      {
        ["newErrors"] = 0,
        ["scriptsFromCard"] = new JsonArray(new ScriptSubstitution { Ref = "t", SourceDiffered = true, FirstDifferenceLine = 2, SentLine = "", CardLine = "# the card" }.ToJson()),
      };
      Assert.Contains("Built from the source on the card.", BuildPlacedFacts.Summary(verdict));
      Assert.DoesNotContain("Built from the source on the card.", BuildPlacedFacts.Summary(new JsonObject { ["newErrors"] = 0 }));
    }

    // ---- 2. the card -----------------------------------------------------------------------------------------

    private static string Card(params ApprovedScript[] scripts) =>
      ConsentCard.Html("build", "Because.", "tok", null, new[] { "A1" }, null, null, "scripts", scripts);

    [Fact]
    public void P2_The_card_shows_a_section_after_the_authority_line_with_the_warning_and_the_whole_source()
    {
      var s = new ApprovedScript { Region = "A1", Language = BuildScriptTypes.Python, Source = Py };
      s.Inputs.Add("n");
      var html = Card(s);

      var authority = html.IndexOf("Dwight will place and wire components in region A1");
      var heading = html.IndexOf("Scripts in this build");
      var warning = html.IndexOf("These run on your machine when you approve. Read them.");
      var code = html.IndexOf("<pre class=\"consent-source\">");
      var why = html.IndexOf("Dwight's reason for asking");
      // Brief BG: the reason is on the card's face, before the fold that holds the authority line and the scripts section.
      Assert.True(why >= 0 && why < authority && authority < heading && heading < warning && warning < code);
      // Brief AL: the section is folded behind the plugin's own summary line (language, lines, ports by name, the document test).
      Assert.Contains("Region A1. Python, ", html);
      Assert.Contains(" Inputs: n (item). Outputs: none. Touches the Rhino document: no.", html);
    }

    [Fact]
    public void P2_Nothing_is_truncated_and_a_long_script_is_in_the_card_to_its_last_line_though_it_is_folded()
    {
      var long_ = string.Join("\n", Enumerable.Range(0, 400).Select(i => "a" + i + " = " + i)) + "\nlast_line_marker = 1\n";
      var html = Card(new ApprovedScript { Region = "A1", Language = BuildScriptTypes.CSharp, Source = long_ });
      Assert.Contains("last_line_marker = 1", html);
      Assert.Contains("a0 = 0", html);
      // Brief AL: FOLDED by default - a native details element, closed - with the WHOLE source inside it. The consent law (#48) is that
      // the complete source is in the card before it runs, not that it is painted open; nothing hides it from the DOM.
      Assert.Contains("<details class=\"consent-script\">", html);
      Assert.DoesNotContain("<details class=\"consent-script\" open", html);
      Assert.DoesNotContain("display:none", html);
      Assert.DoesNotContain("max-height", html);
      Assert.DoesNotContain("...", html);
    }

    [Fact]
    public void P2_A_build_with_no_scripts_is_byte_identical_to_the_card_before()
    {
      var before = ConsentCard.Html("build", "Because.", "tok", null, new[] { "A1" }, null, null, "scripts");
      Assert.Equal(before, Card());
      Assert.Equal(before, ConsentCard.Html("build", "Because.", "tok", null, new[] { "A1" }, null, null, "scripts", null));
      Assert.DoesNotContain("Scripts in this build", before);
    }

    [Fact]
    public void P2_A_card_with_a_script_and_the_same_card_without_differ_exactly_by_the_section()
    {
      var s = new ApprovedScript { Region = "A1", Language = BuildScriptTypes.Python, Source = Py };
      var with = Card(s);
      var without = Card();
      var section = ConsentCard.ScriptsSection(new[] { s });

      Assert.NotEmpty(section);
      Assert.Equal(without.Length + section.Length, with.Length);
      Assert.Equal(with.Replace(section, ""), without);
    }

    [Fact]
    public void P2_The_shown_source_equals_the_declared_source_after_html_decoding_and_markup_in_it_is_inert()
    {
      const string Hostile = "a = \"<b onload=x>\" & '</pre><script>alert(1)</script>'\nif x < 2 and y > 1: pass\n";
      var html = Card(new ApprovedScript { Region = "A1", Language = BuildScriptTypes.Python, Source = Hostile });

      Assert.DoesNotContain("<script>", html);
      Assert.DoesNotContain("<b onload", html);

      var start = html.IndexOf("<pre class=\"consent-source\">") + "<pre class=\"consent-source\">".Length;
      var end = html.IndexOf("</pre>", start);
      Assert.Equal(Hostile, WebUtility.HtmlDecode(html.Substring(start, end - start)));
    }

    [Fact]
    public void P2_Only_a_build_card_carries_the_section()
    {
      var s = new ApprovedScript { Region = "A1", Language = BuildScriptTypes.Python, Source = Py };
      Assert.DoesNotContain("Scripts in this build",
        ConsentCard.Html("reorganise", "Because.", "tok", null, null, null, null, "scripts", new[] { s }));
    }

    // ---- 3. the fact and clause -----------------------------------------------------------------------------

    [Fact]
    public void P3_The_approved_turn_is_told_which_scripts_the_card_showed()
    {
      var fact = ApprovedTurnFact.ScriptsFact(new[] { Approved() });
      Assert.StartsWith("SCRIPTS THE USER READ:", fact);
      Assert.Contains("A1 - Python 3 Script", fact);
      Assert.Contains("unchanged", fact);
      Assert.Contains(Py, fact);   // Brief CA: the source itself, since a turn starts from chat history
      Assert.Null(ApprovedTurnFact.ScriptsFact(null));
      Assert.Null(ApprovedTurnFact.ScriptsFact(new ApprovedScript[0]));

      var text = TurnSystemText.Compose("S", null, null, TurnCapability.Build, new[] { "A1" }, null, null, null, null,
        new[] { Approved() });
      Assert.Contains("SCRIPTS THE USER READ:", text);
      Assert.True(text.IndexOf(ApprovedTurnFact.Heading) < text.IndexOf("SCRIPTS THE USER READ:"));
      Assert.DoesNotContain("SCRIPTS THE USER READ:", TurnSystemText.Compose("S", null, null, TurnCapability.Build, new[] { "A1" }));
    }

    // ---- 4. the verdict claims no more for a script region ----------------------------------------------------

    [Fact]
    public void P4_The_verdict_still_says_what_it_does_not_prove_and_never_geometry()
    {
      var src = System.IO.File.ReadAllText(RepoFile("Dwight", "Agent", "BuildVerdict.cs"));
      Assert.Contains("that this region computes what you actually want - check the result yourself", src);
      Assert.Contains("that the build landed as specified", src);
      Assert.DoesNotContain("the script is correct", src);
      var docs = System.IO.File.ReadAllText(RepoFile("docs", "public", "known-limitations.md"));
      Assert.Contains("checked for solving and for its ports, not for geometry", docs);
    }

    private static string RepoFile(params string[] parts)
    {
      var dir = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
      while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "Dwight.sln"))) dir = dir.Parent;
      Assert.NotNull(dir);
      return System.IO.Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
    }

    // ---- the privacy badge ------------------------------------------------------------------------------------

    [Fact]
    public void The_badge_is_unchanged_with_web_search_off_and_names_the_exception_with_it_on()
    {
      Assert.Equal("Fully local. Nothing leaves your machine.", LocalBadge.Text(isLocal: true, webSearchEnabled: false));
      Assert.Equal("Fully local, except web search: queries go to ollama.com with your Ollama key.",
        LocalBadge.Text(isLocal: true, webSearchEnabled: true));
      Assert.Equal("", LocalBadge.Text(isLocal: false, webSearchEnabled: true));
      Assert.Equal("", LocalBadge.Text(isLocal: false, webSearchEnabled: false));
    }

    [Fact]
    public void The_panel_sends_the_badge_text_and_the_page_shows_it()
    {
      var panel = System.IO.File.ReadAllText(RepoFile("Dwight", "UI", "DwightSettingsPanel.cs"));
      Assert.Contains("[\"localBadge\"] = Dwight.Agent.LocalBadge.Text(s.IsLocal, s.WebSearchEnabled)", panel);
      Assert.Contains("PushInit(); }   // the privacy badge reads this setting", panel);
      var html = System.IO.File.ReadAllText(RepoFile("Dwight", "Resources", "settings.html"));
      Assert.Contains("document.getElementById('localtext').textContent = d.localBadge", html);
    }

    // ---- the heading over the models (Brief FN) ---------------------------------------------------------------------

    /// <summary>Ollama on this machine has no key, so its list of models is not "on your key": the heading follows the provider in force.</summary>
    [Fact]
    public void The_models_heading_names_the_key_only_where_there_is_one()
    {
      Assert.Equal("Models on this machine", LocalBadge.ModelsHeading(isLocal: true));
      Assert.Equal("Models on your key", LocalBadge.ModelsHeading(isLocal: false));
      // Every provider that is not local needs a key, so the keyed words are true wherever they are shown.
      foreach (var p in Dwight.DwightConfig.Providers)
        if (!Dwight.DwightConfig.IsLocalProvider(p)) Assert.True(Dwight.DwightConfig.ProviderNeedsKey(p), p + " is not local and needs no key, so \"Models on your key\" would be false for it");
    }

    [Fact]
    public void The_panel_sends_the_heading_with_every_init_and_a_change_of_provider_sends_init()
    {
      var panel = System.IO.File.ReadAllText(RepoFile("Dwight", "UI", "DwightSettingsPanel.cs"));
      Assert.Contains("[\"modelsHeading\"] = Dwight.Agent.LocalBadge.ModelsHeading(s.IsLocal)", panel);
      Assert.Contains("PushInit();   // refresh override list + key status for the new provider", panel);
      var html = System.IO.File.ReadAllText(RepoFile("Dwight", "Resources", "settings.html"));
      Assert.Contains("<label id=\"modelshead\">Models on your key</label>", html);
      Assert.Contains("document.getElementById('modelshead').textContent = d.modelsHeading", html);
    }
  }
}
