using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Dwight.Agent;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// FULL CONTROL (U90). The switch is off by default and carried per conversation; with it off the tool list and the prompt are what
  /// they were; with it on, one more kind of card exists and one more tool, and the card is the user's read of the whole source. The run
  /// itself touches Rhino and is the architect's gate; everything the plugin SAYS about it is here.
  /// </summary>
  public class FullControlTests
  {
    /// <summary>U104: the preamble, byte for byte as the brief pins it (each line ends in a line feed).</summary>
    internal const string PreambleText =
      "# --- Dwight's preamble: these names are bound before your script runs ---\n" +
      "import Rhino, Grasshopper, System\n" +
      "rhdoc = Rhino.RhinoDoc.ActiveDoc\n" +
      "ghdoc = Grasshopper.Instances.ActiveCanvas.Document if Grasshopper.Instances.ActiveCanvas else None\n" +
      "# --- your script follows ---\n";

    private const string Src ="import rhinoscriptsyntax as rs\nfor n in range(3):\n    print(n)\n";

    private static JsonObject Args(string json) => (JsonObject)JsonNode.Parse(json);

    // ---- the descriptor, and who is offered it ---------------------------------------------------------------------

    [Fact]
    public void The_run_script_descriptor_is_the_rulings_words_and_a_schema_that_parses()
    {
      var d = FullControl.Descriptor();
      Assert.Equal("run_script", d.Name);
      Assert.True(d.Mutates);
      Assert.False(d.Solves);
      Assert.Equal(
        "Run Python 3 inside Rhino with RhinoCommon and the Grasshopper API, on the active document, once the complete source has been shown and " +
        "approved on a card. Use it when a build card cannot express the change: editing many existing components, data extraction, Rhino " +
        "document work, anything the build tools refuse. Dwight does not check what the script does; it reports what changed. " +
        "The script runs in Rhino's Python 3 runtime, not inside a script component: there is no ghenv and no scriptcontext; rhdoc is the " +
        "Rhino document and ghdoc is this definition, bound by Dwight before your script; print goes to the report. Prefer the " +
        "build tools when they can do the job.", d.Description);

      var schema = JsonNode.Parse(d.InputSchemaJson) as JsonObject;
      Assert.NotNull(schema);
      var props = (JsonObject)schema["properties"];
      Assert.Equal(new[] { "purpose", "source", "timeoutSeconds" }, props.Select(p => p.Key).OrderBy(x => x).ToArray());
      Assert.Equal(new[] { "source", "purpose" }, ((JsonArray)schema["required"]).Select(n => n.GetValue<string>()).ToArray());
      Assert.False(schema["additionalProperties"].GetValue<bool>());
      Assert.Contains("60", props["timeoutSeconds"]["description"].GetValue<string>());
      Assert.Contains("600", props["timeoutSeconds"]["description"].GetValue<string>());
    }

    [Fact]
    public void With_Full_control_off_the_tool_list_is_the_one_there_was_for_every_capability()
    {
      foreach (TurnCapability cap in Enum.GetValues(typeof(TurnCapability)))
      {
        var before = AgentLoopCore.ToolsFor(cap);
        var off = AgentLoopCore.ToolsFor(cap, false);
        Assert.Equal(before.Count, off.Count);
        for (var i = 0; i < before.Count; i++)
        {
          // (the catalogues build fresh descriptors on each call, so it is the CONTENT that is held equal, name by name, in order)
          Assert.Equal(before[i].Name, off[i].Name);
          Assert.Equal(before[i].Description, off[i].Description);
          Assert.Equal(before[i].Mutates, off[i].Mutates);
          Assert.Equal(before[i].InputSchemaJson, off[i].InputSchemaJson);
        }
        Assert.DoesNotContain(off, t => t.Name == FullControl.ToolName);
        Assert.DoesNotContain("run_script", off.First(t => t.Name == AgentHostTools.RequestMutation).InputSchemaJson);
      }
    }

    [Fact]
    public void With_Full_control_on_request_mutation_offers_the_kind_and_only_an_approved_run_turn_holds_the_tool()
    {
      foreach (TurnCapability cap in Enum.GetValues(typeof(TurnCapability)))
      {
        var on = AgentLoopCore.ToolsFor(cap, true);
        var request = on.First(t => t.Name == AgentHostTools.RequestMutation);
        var schema = JsonNode.Parse(request.InputSchemaJson) as JsonObject;
        var kinds = ((JsonArray)schema["properties"]["kind"]["enum"]).Select(n => n.GetValue<string>()).ToList();
        Assert.Contains("run_script", kinds);
        Assert.NotNull(schema["properties"]["timeoutSeconds"]);
        Assert.Contains("kind=run_script", schema["properties"]["source"]["description"].GetValue<string>());

        var holdsTool = on.Any(t => t.Name == FullControl.ToolName);
        Assert.Equal(cap == TurnCapability.RunScript, holdsTool);
      }
    }

    [Fact]
    public void Publishing_the_kind_does_not_change_the_base_descriptor()
    {
      var baseSchema = AgentHostTools.All().First(t => t.Name == AgentHostTools.RequestMutation).InputSchemaJson;
      _ = AgentLoopCore.ToolsFor(TurnCapability.ReadOnly, true);
      Assert.Equal(baseSchema, AgentHostTools.All().First(t => t.Name == AgentHostTools.RequestMutation).InputSchemaJson);
      Assert.DoesNotContain("run_script", baseSchema);
    }

    [Fact]
    public void The_MCP_list_and_the_host_list_do_not_carry_run_script_and_no_other_capability_is_granted_it()
    {
      Assert.DoesNotContain(AgentToolCatalog.All(), t => t.Name == FullControl.ToolName);
      Assert.DoesNotContain(AgentHostTools.All(), t => t.Name == FullControl.ToolName);
      foreach (TurnCapability cap in Enum.GetValues(typeof(TurnCapability)))
        Assert.Equal(cap == TurnCapability.RunScript, AgentLoopCore.IsGranted(cap, FullControl.ToolName));
      Assert.DoesNotContain("run_script", AgentHostTools.Kinds);   // the kind is published per conversation, never in the static list
    }

    [Fact]
    public void An_approved_run_turn_reads_and_runs_and_builds_nothing()
    {
      var names = AgentLoopCore.ToolsFor(TurnCapability.RunScript, true).Select(t => t.Name).ToList();
      Assert.Contains(FullControl.ToolName, names);
      Assert.DoesNotContain(AgentToolCatalog.BuildRegion, names);
      Assert.DoesNotContain(AgentToolCatalog.SolveAndCheck, names);
      Assert.Equal("run_script", AgentHostTools.KindApprovedBy(TurnCapability.RunScript));
      Assert.Equal("run_script", AgentHostTools.ActingToolFor("run_script"));
    }

    [Fact]
    public void Run_script_asks_in_every_mode_unless_the_caller_states_Full_control()
    {
      foreach (var mode in ApprovalMode.All)
        Assert.Equal(ApprovalDecision.Ask, ApprovalMode.Decide(mode, FullControl.Kind, new ApprovalFacts { HasScripts = true }));
      Assert.Equal(ApprovalMode.KindRunScript, FullControl.Kind);
    }

    // ---- the prompt -------------------------------------------------------------------------------------------------

    [Fact]
    public void The_master_prompt_says_nothing_of_Full_control_and_a_conversation_with_it_on_is_told_as_a_fact()
    {
      Assert.DoesNotContain("FULL CONTROL", Dwight.Prompts.Prompts.AgentMaster);
      Assert.DoesNotContain("run_script", Dwight.Prompts.Prompts.AgentMaster);

      var fact = FullControl.ThreadFact();
      Assert.StartsWith("FULL CONTROL IS ON FOR THIS CONVERSATION.", fact);
      Assert.Contains("kind=run_script", fact);
      Assert.Contains("complete source", fact);
      Assert.Contains("Dwight does not check what the script does", fact);
      Assert.Contains("Never describe a run as proven", fact);
    }

    [Fact]
    public void The_approved_turn_fact_names_the_tool_and_forbids_the_word_checked()
    {
      var fact = ApprovedTurnFact.For(TurnCapability.RunScript, null);
      Assert.NotNull(fact);
      Assert.Contains("run_script", fact);
      Assert.Contains("identical source", fact);
      Assert.Contains("do not call the result checked, verified or proven", fact);
    }

    // ---- the card ---------------------------------------------------------------------------------------------------

    [Fact]
    public void The_card_has_the_exact_title_and_authority_the_whole_source_and_the_reason()
    {
      Assert.Equal(ConsentRoute.RunScript, ConsentCard.Route("run_script"));
      Assert.Equal("Run this script?", ConsentCard.Title("run_script"));
      Assert.Equal("Dwight will run the code below inside Rhino, with full access to this definition and the Rhino document. This is Full " +
                   "control: Dwight cannot check what the script does, and cannot stop it once it has started. One Ctrl-Z undoes what " +
                   "Grasshopper recorded; changes to the Rhino document or outside it may not come back.", ConsentCard.Authority("run_script"));

      var html = ConsentCard.RunScriptHtml("Rename every slider.", "tok", Src, expanded: true);
      Assert.Contains("<strong>Run this script?</strong>", html);
      Assert.Contains(System.Net.WebUtility.HtmlEncode(FullControl.CardAuthority), html);
      Assert.Contains("<pre class=\"consent-source\">" + System.Net.WebUtility.HtmlEncode(Src) + "</pre>", html);   // all of it, byte for byte
      Assert.Contains("Dwight's reason for asking", html);
      Assert.Contains("Rename every slider.", html);
      Assert.Contains("Python 3, 3 lines. Touches the Rhino document: no.", html);
      Assert.Contains("<button", html);   // it always has its button
      Assert.Contains("consent:tok", html);
    }

    [Fact]
    public void The_first_card_in_a_conversation_opens_expanded_and_the_second_folded()
    {
      var first = ConsentCard.RunScriptHtml("why", "t1", Src, ConsentCard.ExpandedByDefaultFor("run_script", firstOfKindInThread: true));
      var second = ConsentCard.RunScriptHtml("why", "t2", Src, ConsentCard.ExpandedByDefaultFor("run_script", firstOfKindInThread: false));
      Assert.Contains("<details class=\"consent-script\" open>", first);
      Assert.Contains("<details class=\"consent-script\">", second);
      Assert.DoesNotContain(" open>", second);
      // the whole source is in both, folded or not
      Assert.Contains(System.Net.WebUtility.HtmlEncode(Src), first);
      Assert.Contains(System.Net.WebUtility.HtmlEncode(Src), second);
    }

    [Fact]
    public void The_summary_says_it_is_a_text_match_and_flags_a_script_that_touches_the_document()
    {
      var touching = "import scriptcontext\nscriptcontext.doc.Objects.Count\n";
      var html = ConsentCard.RunScriptHtml("why", "t", touching, true);
      Assert.Contains("Touches the Rhino document: yes.", html);
      Assert.Contains("plain text match", html);
    }

    [Fact]
    public void The_model_cannot_move_the_card_by_what_it_writes()
    {
      var html = ConsentCard.RunScriptHtml("<script>alert(1)</script>", "tok", "x = '</pre><b>'", true);
      Assert.DoesNotContain("<script>", html);
      Assert.Contains("&lt;script&gt;", html);
      Assert.Contains("&lt;/pre&gt;&lt;b&gt;", html);   // the source is escaped as text, so it cannot close the block it sits in
      Assert.Equal("Run this script?", ConsentCard.Title("run_script"));
    }

    // ---- the ask and the call ---------------------------------------------------------------------------------------

    [Fact]
    public void A_valid_ask_becomes_a_grant_with_the_default_clock()
    {
      Assert.True(FullControl.TryParseAsk(Args("{\"kind\":\"run_script\",\"source\":\"print(1)\\r\\nprint(2)\"}"), " Count them. ", out var g, out var err));
      Assert.Null(err);
      Assert.Equal("print(1)\nprint(2)", g.Source);   // line endings normalised: one text everywhere
      Assert.Equal("Count them.", g.Purpose);
      Assert.Equal(60, g.TimeoutSeconds);
    }

    [Theory]
    [InlineData("{\"kind\":\"run_script\"}", "source")]
    [InlineData("{\"kind\":\"run_script\",\"source\":\"   \"}", "source")]
    [InlineData("{\"kind\":\"run_script\",\"source\":\"x\",\"timeoutSeconds\":0}", "between 1 and 600")]
    [InlineData("{\"kind\":\"run_script\",\"source\":\"x\",\"timeoutSeconds\":601}", "between 1 and 600")]
    [InlineData("{\"kind\":\"run_script\",\"source\":\"x\",\"timeoutSeconds\":\"soon\"}", "whole number")]
    public void An_ask_that_cannot_be_a_card_is_refused_by_name(string json, string mentions)
    {
      Assert.False(FullControl.TryParseAsk(Args(json), "Because.", out var g, out var err));
      Assert.Null(g);
      Assert.Contains(mentions, err.Message);
    }

    [Fact]
    public void An_ask_without_a_reason_or_over_the_size_bound_is_refused()
    {
      Assert.False(FullControl.TryParseAsk(Args("{\"source\":\"x\"}"), "  ", out _, out var noWhy));
      Assert.Contains("why", noWhy.Message);

      var big = new string('x', EditScriptAsk.MaxSourceChars + 1);
      Assert.False(FullControl.TryParseAsk(Args("{\"source\":\"" + big + "\"}"), "Because.", out _, out var tooBig));
      Assert.Contains("reads all of it", tooBig.Message);
      Assert.True(FullControl.TryParseAsk(Args("{\"source\":\"x\",\"timeoutSeconds\":600}"), "Because.", out var edge, out _));
      Assert.Equal(600, edge.TimeoutSeconds);
    }

    [Fact]
    public void The_call_after_the_approval_needs_the_source_and_the_purpose()
    {
      Assert.True(FullControl.TryParseCall(Args("{\"source\":\"a\\r\\nb\",\"purpose\":\" Do it. \",\"timeoutSeconds\":30}"), out var s, out var p, out var t, out _));
      Assert.Equal("a\nb", s); Assert.Equal("Do it.", p); Assert.Equal(30, t);

      Assert.False(FullControl.TryParseCall(Args("{\"purpose\":\"x\"}"), out _, out _, out _, out var e1));
      Assert.Contains("source", e1.Message);
      Assert.False(FullControl.TryParseCall(Args("{\"source\":\"x\"}"), out _, out _, out _, out var e2));
      Assert.Contains("purpose", e2.Message);
      Assert.False(FullControl.TryParseCall(Args("{\"source\":\"x\",\"purpose\":\"y\",\"timeoutSeconds\":9999}"), out _, out _, out _, out _));
    }

    // ---- the grant: the text shown is the text run -------------------------------------------------------------------

    [Fact]
    public void The_same_source_matches_whatever_the_line_endings()
    {
      Assert.Null(FullControl.FirstDifference("a\nb\nc", "a\r\nb\r\nc"));
      Assert.Null(FullControl.FirstDifference(Src, Src));
    }

    [Fact]
    public void A_differing_source_is_refused_naming_the_first_differing_line_and_both_sides()
    {
      var msg = FullControl.FirstDifference("a = 1\nb = 2\nc = 3", "a = 1\nb = 99\nc = 3");
      Assert.Contains("first differs at line 2", msg);
      Assert.Contains("\"b = 2\"", msg);
      Assert.Contains("\"b = 99\"", msg);
      Assert.Contains("not the one the user approved", msg);
    }

    [Fact]
    public void A_longer_or_shorter_source_differs_at_the_line_that_is_missing()
    {
      Assert.Contains("line 3", FullControl.FirstDifference("a\nb", "a\nb\nextra"));
      Assert.Contains("nothing (the line is missing)", FullControl.FirstDifference("a\nb", "a\nb\nextra"));
      Assert.Contains("you sent nothing (the line is missing)", FullControl.FirstDifference("a\nb\nc", "a\nb"));
      Assert.Contains("line 1", FullControl.FirstDifference("print(1)", "print(2)"));
    }

    [Fact]
    public void A_long_differing_line_is_bounded_in_the_refusal()
    {
      var msg = FullControl.FirstDifference("x", new string('y', 500));
      Assert.Contains("...", msg);
      Assert.True(msg.Length < 600);
    }

    [Fact]
    public void One_approval_covers_one_run()
    {
      var g = new RunScriptGrant { Source = "x" };
      Assert.True(g.TryUse());
      Assert.False(g.TryUse());
      Assert.False(g.TryUse());
    }

    [Fact]
    public void The_runtime_gets_the_approved_text_with_the_language_header_added_once()
    {
      // U104: the preamble follows the header, whoever wrote the header.
      Assert.Equal("#! python 3\n" + PreambleText + Src, FullControl.ExecutableSource(Src));
      Assert.Equal("#! python 3\n" + PreambleText + "print(1)", FullControl.ExecutableSource("#! python 3\nprint(1)"));   // header never doubled
      Assert.Equal("#!python3\n" + PreambleText + "print(1)", FullControl.ExecutableSource("#!python3\nprint(1)"));
    }

    // ---- the report: a report, never a verdict ------------------------------------------------------------------------

    private static DocSnapshot Snap(int c, int w, int g, int e, string fp) => new DocSnapshot { Components = c, Wires = w, Groups = g, Errors = e, Fingerprint = fp };

    [Fact]
    public void The_report_is_formatted_from_two_snapshots()
    {
      var report = FullControl.Report("Rename every slider.", Snap(10, 12, 2, 0, "aaaa"), Snap(13, 16, 2, 0, "bbbb"), "renamed 4");
      Assert.Equal("Ran Rename every slider. Changed: +3 components, +4 wires, 0 groups, 0 new errors; the definition's behaviour fingerprint changed. " +
                   "Printed output: renamed 4. One Ctrl-Z undoes what Grasshopper recorded.", report);
    }

    [Fact]
    public void The_report_says_what_did_not_change_and_counts_losses()
    {
      var report = FullControl.Report("Tidy up", Snap(10, 12, 2, 1, "same"), Snap(9, 10, 1, 3, "same"), null);
      Assert.Contains("Changed: -1 component, -2 wires, -1 group, 2 new errors;", report);
      Assert.Contains("the definition's behaviour fingerprint did not change", report);
      Assert.Contains("Printed output: none.", report);

      var fewer = FullControl.Report("Fix", Snap(5, 5, 0, 4, "a"), Snap(5, 5, 0, 1, "a"), "");
      Assert.Contains("3 fewer errors", fewer);
      Assert.Contains("0 components", fewer);
    }

    [Fact]
    public void Printed_output_is_bounded_and_the_cut_is_said()
    {
      var long_ = new string('z', 2500);
      var bounded = FullControl.BoundStdout(long_);
      Assert.StartsWith(new string('z', 2000), bounded);
      Assert.EndsWith("[cut at 2000 of 2500 characters]", bounded);
      Assert.Equal("short", FullControl.BoundStdout("  short \n"));
      Assert.Equal("none", FullControl.BoundStdout(null));
      Assert.Equal(2000, FullControl.MaxStdoutChars);
    }

    [Fact]
    public void A_script_that_raised_is_a_result_with_its_exception_verbatim_and_what_changed_before_it_stopped()
    {
      // Brief AY (U101): the runner records one event for the 2 objects added before the failure, and it stands after the run.
      var record = Guid.Parse("22222222-2222-2222-2222-222222222222");
      var o = new RunOutcome { Exception = "ZeroDivisionError: division by zero (at line 7)", Before = Snap(4, 3, 0, 0, "a"), After = Snap(6, 3, 0, 1, "b"), Stdout = "step 1", Seconds = 0.4,
                               Added = 2, RecordId = record, TopAfter = record };
      var r = FullControl.ToResult("Compute ratios", o);
      Assert.True(r.Ok);
      var json = (JsonObject)r.Result;
      Assert.True(json["ran"].GetValue<bool>());
      Assert.True(json["raised"].GetValue<bool>());
      var text = json["report"].GetValue<string>();
      Assert.StartsWith("The script for Compute ratios raised: ZeroDivisionError: division by zero (at line 7)", text);
      Assert.Contains("Before it failed, changed: +2 components, 0 wires, 0 groups, 1 new errors;", text);
      Assert.Contains("Printed output: step 1.", text);
      Assert.EndsWith("One Ctrl-Z undoes the 2 objects the script added.", text);   // measured from the record (U101)
    }

    [Fact]
    public void A_runtime_that_cannot_be_reached_runs_nothing_and_says_so()
    {
      var r = FullControl.ToResult("Anything", new RunOutcome { Unavailable = "Dwight cannot reach Rhino's Python 3 runtime on this Rhino (its script API has changed)." });
      Assert.False(r.Ok);
      Assert.Contains("Nothing was run, and nothing on the canvas changed.", r.Message);
    }

    [Fact]
    public void No_report_calls_a_script_verified_or_checked()
    {
      var all = new List<string>
      {
        FullControl.Report("Do it", Snap(1, 1, 1, 0, "a"), Snap(2, 2, 2, 0, "b"), "out"),
        FullControl.RaisedReport("Do it", "Boom", Snap(1, 1, 1, 0, "a"), Snap(2, 2, 2, 1, "b"), "out"),
        FullControl.OverranReport("Do it", 60),
        FullControl.LateReport("Do it", 60, 71.26, FullControl.Report("Do it", Snap(1, 1, 1, 0, "a"), Snap(1, 1, 1, 0, "a"), "")),
        FullControl.StillRunning,
        FullControl.UndoClause,
        FullControl.CardAuthority,
        FullControl.SettingSentence,
      };
      foreach (var s in all)
      {
        Assert.DoesNotContain("verified", s, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("proven", s.Replace("nothing it does is proven", ""), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("checked", s, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stopped", s.Replace("cannot stop", ""), StringComparison.OrdinalIgnoreCase);
      }
    }

    // ---- the clock --------------------------------------------------------------------------------------------------

    [Fact]
    public void The_clock_never_says_stopped_it_says_the_script_is_still_running()
    {
      Assert.Equal("The script is still running on Rhino's thread; Dwight cannot stop it.", FullControl.StillRunning);
      var overran = FullControl.OverranReport("Slow work.", 60);
      Assert.Equal("The script for Slow work has not finished after 60 seconds. The script is still running on Rhino's thread; Dwight cannot stop it.", overran);

      var r = FullControl.StillRunningResult("Slow work.", 60);
      Assert.True(r.Ok);
      var json = (JsonObject)r.Result;
      Assert.True(json["stillRunning"].GetValue<bool>());
      Assert.False(json["ran"].GetValue<bool>());
      Assert.Contains(FullControl.StillRunning, json["report"].GetValue<string>());
    }

    [Fact]
    public void A_late_finish_is_reported_with_its_real_time_against_its_clock()
    {
      var late = FullControl.LateReport("Slow work.", 60, 71.26, "Ran Slow work. Changed: 0 components.");
      Assert.Equal("The script for Slow work has now finished, after 71.3 seconds against a clock of 60. Ran Slow work. Changed: 0 components.", late);
    }

    [Fact]
    public void The_clock_defaults_to_60_and_never_exceeds_600()
    {
      Assert.Equal(60, FullControl.DefaultTimeoutSeconds);
      Assert.Equal(600, FullControl.MaxTimeoutSeconds);
      Assert.Equal(60, new RunScriptGrant().TimeoutSeconds);
    }

    // ---- the Settings sentence, the payload row, the wiring ---------------------------------------------------------------

    private static string RepoPath(params string[] parts)
    {
      var dir = AppContext.BaseDirectory;
      while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir, "Dwight.sln")))
        dir = System.IO.Path.GetDirectoryName(dir);
      Assert.NotNull(dir);
      return System.IO.Path.Combine(new[] { dir }.Concat(parts).ToArray());
    }

    private static string RepoFile(params string[] parts) => System.IO.File.ReadAllText(RepoPath(parts));

    [Fact]
    public void The_Settings_sentence_is_the_rulings_words_and_both_surfaces_carry_it()
    {
      Assert.Equal(
        "Full control: everything Build has, plus Dwight can run a script you have read, with full access to Rhino and this definition. " +
        "Dwight cannot check what such a script does, and one Ctrl-Z may not undo everything it touches. It costs more tokens, and " +
        "nothing it does is proven. For people who know Grasshopper and Rhino and want speed. Turn it on only when you need it. " +
        "With the Do not ask approval mode, a script runs without asking, and you read it afterwards.",
        FullControl.SettingSentence);

      var html = RepoFile("Dwight", "Resources", "settings.html");
      Assert.Contains(FullControl.SettingSentence, html);
      Assert.Contains("<label>Full control</label>", html);
      Assert.Contains("setFullControl(this.getAttribute('aria-checked')==='true'?'off':'on')", html);
      Assert.Contains("dwightSend('fullcontrol:'+v)", html);
      Assert.Contains("d.fullControl", html);
      Assert.True(html.IndexOf("<label>Approval</label>", StringComparison.Ordinal) < html.IndexOf("<label>Full control</label>", StringComparison.Ordinal));

      var eto = RepoFile("Dwight", "UI", "SettingsDialog.cs");
      Assert.Contains("Dwight.Agent.FullControl.SettingSentence", eto);
      Assert.Contains("s.FullControl = _fullControl.Checked == true;", eto);

      var panel = RepoFile("Dwight", "UI", "DwightSettingsPanel.cs");
      Assert.Contains("cmd.StartsWith(\"fullcontrol:\"", panel);
    }

    [Fact]
    public void The_switch_is_off_by_default()
    {
      var cs = RepoFile("Dwight", "Config", "DwightSettings.cs");
      Assert.Contains("public bool FullControl { get; set; }", cs);   // a bool property with no initialiser is false
      Assert.DoesNotContain("FullControl { get; set; } = true", cs);
    }

    [Fact]
    public void The_payload_contract_carries_a_row_for_what_run_script_sends_back()
    {
      var row = Dwight.Data.PayloadContract.All().Single(r => r.Surface == "tool:run_script");
      Assert.Contains("Full control", row.Title);
      Assert.Contains("2,000 characters", row.Sends);
    }

    [Fact]
    public void The_panel_wires_the_ask_the_click_and_the_loop_through_the_threads_own_switch()
    {
      var cs = RepoFile("Dwight", "UI", "DwightScaffold.cs");
      Assert.Contains("CommandHistory.ThreadFullControl(doc, threadId)", cs);                 // the thread's value, never the default
      Assert.Contains("DwightSettings.Current.FullControl);", cs);                            // copied onto the thread once, at open
      Assert.Contains("if (fullControl && string.Equals(StrOf(args, \"kind\")", cs);            // the kind is only honoured when the switch is on
      Assert.Contains("Dwight.Agent.FullControl.TryParseAsk(", cs);
      Assert.Contains("ConsentCard.ExpandedByDefaultFor(Dwight.Agent.FullControl.Kind, firstRunScriptCard)", cs);
      Assert.Contains("MarkRunScriptCardShown(doc, threadId)", cs);
      // U100: the click runs the grant itself; no turn is started for the model to run it in.
      Assert.Contains("RunApprovedScript(wp, doc, threadId, grant, autoMode != null);", cs);
      Assert.DoesNotContain("TurnCapability.RunScript, null, null, grant", cs);

      var loop = RepoFile("Dwight", "Agent", "AgentLoop.cs");
      Assert.Contains("if (!req.FullControl || req.Capability != TurnCapability.RunScript || req.RunGrant == null)", loop);
      Assert.Contains("FirstDifference(req.RunGrant.Source, sent)", loop);
      Assert.Contains("req.RunGrant.TryUse()", loop);
      Assert.Contains("THE TEXT SHOWN IS THE TEXT RUN: the grant's source, never the model's copy of it.", loop);
      Assert.Contains("ExecutableSource(req.RunGrant.Source)", loop);
    }

    [Fact]
    public void The_run_goes_through_a_scheduled_solution_names_its_undo_event_and_reaches_the_runtime_by_a_probe()
    {
      var gh = RepoFile("Dwight", "Agent", "GhRunScript.cs");
      Assert.Contains("doc.ScheduleSolution(", gh);                       // guardrail 1
      Assert.Contains("RecordAddObjectEvent(UndoName(purpose)", gh);      // guardrail 2: one event, named by the purpose
      Assert.Contains("RhinoPython3.Probe(", gh);                         // the probe runs before the script does
      Assert.Contains("Rhino.Runtime.Code.RhinoCode", gh);
      Assert.Contains("\"Dwight: \"", gh);
      Assert.DoesNotContain("PythonScript.Create", gh);                   // no silent fall back to the IronPython 2 runtime
    }

    // ---- the docs and the ADR ------------------------------------------------------------------------------------------

    [Fact]
    public void The_public_docs_state_Full_control_in_the_users_words()
    {
      var build = RepoFile("docs", "public", "build.md");
      Assert.Contains("## Full control", build);
      Assert.Contains(FullControl.SettingSentence, build);
      Assert.Contains("\"Run this script?\"", build);
      Assert.Contains("Under Do not ask it runs without asking", build);
      Assert.Contains(ChatHeader.ScriptsRunLine, build);
      Assert.Contains("still running on Rhino's thread and cannot stop it", build);
      Assert.DoesNotContain("run_script", build);   // the tool's name is internal; the page speaks of what the user sees

      var privacy = RepoFile("docs", "public", "privacy.md");
      Assert.Contains("a script Dwight runs for you has the access you have", privacy);
      Assert.Contains("The source came from the model and you read it on the card before it ran", privacy);

      Assert.Contains("## Full control is not checked", RepoFile("docs", "public", "known-limitations.md"));
      Assert.Contains("30 October 2026: Full control", RepoFile("docs", "public", "changelog.md"));
    }

    // The draft is a development document, and the public tree does not carry it. With no draft, this test measures that it is
    // running in the public tree: docs/phases must be absent too, so a development tree that lost the draft fails here.
    [Fact]
    public void The_ADR_draft_states_the_scoped_exception_to_ADR_056_and_its_three_conditions()
    {
      if (!System.IO.File.Exists(RepoPath("docs", "phases", "adr-100-full-control-draft.md")))
      {
        Assert.False(System.IO.Directory.Exists(RepoPath("docs", "phases")), "The ADR-100 draft is missing from a development tree (docs/phases is here).");
        return;
      }
      var adr = RepoFile("docs", "phases", "adr-100-full-control-draft.md");
      Assert.Contains("ADR-100", adr);
      Assert.Contains("ADR-056", adr);
      Assert.Contains("The user chose the mode.", adr);
      Assert.Contains("The user read the source.", adr);
      Assert.Contains("It always asks.", adr);
      Assert.Contains("Rhino offers no way to interrupt a script on its own thread", adr);
    }
  }
}
