using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Dwight.Agent;
using Dwight.Data;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// APPROVAL MODES (Brief AL, Tharun's rulings of 30 October 2026).
  ///
  /// The decision is ONE pure function, <see cref="ApprovalMode.Decide"/>, and it is the whole law, so every cell of its table
  /// is pinned here. One row is fixed by law and no mode moves it: a verb hand-off always asks. run_script asks too, except in one cell: "Do not ask" with Full control on.
  /// The law that survives every mode is the consent model's real one: the forbidden thing is a change the panel says nothing
  /// about, not a change the user did not click - so the card, the transcript line, the close line and the state sentence are
  /// pinned too.
  /// </summary>
  public class ApprovalModeTests
  {
    private const string A = ApprovalMode.Ask;
    private const string S = ApprovalMode.ScriptsAndDeletions;
    private const string N = ApprovalMode.Never;

    private static ApprovalFacts Plain => new ApprovalFacts();
    private static ApprovalFacts Scripts => new ApprovalFacts { HasScripts = true };
    private static ApprovalFacts Deletes => new ApprovalFacts { HasDeletions = true };

    // ---- THE TABLE, EVERY CELL ----------------------------------------------------------------------------------------

    public static IEnumerable<object[]> Cells()
    {
      // column 1: solve, scaffold, set_value, build without scripts, edit without deletions
      foreach (var kind in new[] { "solve", "scaffold", "set_value", "build", "edit" })
      {
        yield return new object[] { A, kind, "plain", false };
        yield return new object[] { S, kind, "plain", true };
        yield return new object[] { N, kind, "plain", true };
      }
      // column 2: build with scripts, edit_script
      yield return new object[] { A, "build", "scripts", false };
      yield return new object[] { S, "build", "scripts", false };
      yield return new object[] { N, "build", "scripts", true };
      yield return new object[] { A, "edit_script", "scripts", false };
      yield return new object[] { S, "edit_script", "scripts", false };
      yield return new object[] { N, "edit_script", "scripts", true };
      // column 3: edit with a deletion
      yield return new object[] { A, "edit", "deletes", false };
      yield return new object[] { S, "edit", "deletes", false };
      yield return new object[] { N, "edit", "deletes", true };
      // column 4: reorganise, simplify (fixed by law)
      foreach (var kind in new[] { "reorganise", "simplify" })
        foreach (var mode in new[] { A, S, N })
          yield return new object[] { mode, kind, "plain", false };
      // column 5: run_script (fixed by law)
      foreach (var mode in new[] { A, S, N })
        yield return new object[] { mode, "run_script", "plain", false };
    }

    private static ApprovalFacts FactsNamed(string name) =>
      name == "scripts" ? Scripts : name == "deletes" ? Deletes : Plain;

    [Theory]
    [MemberData(nameof(Cells))]
    public void THE_TABLE_CELL(string mode, string kind, string facts, bool expectedAuto)
    {
      Assert.Equal(expectedAuto ? ApprovalDecision.Auto : ApprovalDecision.Ask, ApprovalMode.Decide(mode, kind, FactsNamed(facts)));
    }

    [Fact]
    public void The_table_has_every_cell_of_the_ruling()
    {
      // 3 modes x (5 kinds in column 1 + 2 in column 2 + 1 in column 3 + 2 hand-offs + run_script) = 3 x 11
      Assert.Equal(33, Cells().Count());
    }

    // ---- the two rows fixed by law --------------------------------------------------------------------------------------

    [Fact]
    public void A_verb_hand_off_always_asks_whatever_the_mode_and_whatever_the_facts()
    {
      foreach (var mode in ApprovalMode.All)
        foreach (var kind in new[] { "reorganise", "simplify" })
          foreach (var facts in new[] { Plain, Scripts, Deletes, new ApprovalFacts { IsVerbHandOff = true } })
            Assert.Equal(ApprovalDecision.Ask, ApprovalMode.Decide(mode, kind, facts));

      // and a fact that says "hand-off" moves ANY kind to Ask, so a caller cannot forget the kind's own name
      Assert.Equal(ApprovalDecision.Ask, ApprovalMode.Decide(N, "build", new ApprovalFacts { IsVerbHandOff = true }));
    }

    [Fact]
    public void run_script_runs_without_asking_in_one_cell_and_asks_in_every_other()
    {
      foreach (var mode in ApprovalMode.All)
        foreach (var facts in new[] { Plain, Scripts, Deletes })
          Assert.Equal(ApprovalDecision.Ask, ApprovalMode.Decide(mode, ApprovalMode.KindRunScript, facts));   // Full control off: the default a forgetful caller gets

      foreach (var fullControl in new[] { false, true })
        foreach (var mode in ApprovalMode.All)
          Assert.Equal(mode == N && fullControl ? ApprovalDecision.Auto : ApprovalDecision.Ask,
                       ApprovalMode.Decide(mode, ApprovalMode.KindRunScript, ApprovalMode.FactsFor(ApprovalMode.KindRunScript, 1, 0, fullControl)));

      Assert.Equal(ApprovalDecision.Ask, ApprovalMode.Decide(N, ApprovalMode.KindRunScript, null));
      Assert.Equal(ApprovalDecision.Ask, ApprovalMode.Decide("yolo", ApprovalMode.KindRunScript, new ApprovalFacts { FullControl = true }));
    }

    /// <summary>Full control moves that one cell and nothing else: every other kind reads the same with it on or off.</summary>
    [Fact]
    public void Full_control_moves_no_other_cell()
    {
      foreach (var mode in ApprovalMode.All)
        foreach (var kind in new[] { "solve", "scaffold", "build", "set_value", "edit_script", "edit", "spot", "reorganise", "simplify" })
          foreach (var facts in new[] { Plain, Scripts, Deletes })
            Assert.Equal(ApprovalMode.Decide(mode, kind, facts),
                         ApprovalMode.Decide(mode, kind, new ApprovalFacts { HasScripts = facts.HasScripts, HasDeletions = facts.HasDeletions, FullControl = true }));
    }

    [Fact]
    public void An_unknown_kind_or_mode_fails_closed_to_Ask()
    {
      Assert.Equal(ApprovalDecision.Ask, ApprovalMode.Decide(N, "something_new", Plain));
      Assert.Equal(ApprovalDecision.Ask, ApprovalMode.Decide(N, null, Plain));
      Assert.Equal(ApprovalDecision.Ask, ApprovalMode.Decide("yolo", "solve", Plain));
      Assert.Equal(ApprovalDecision.Ask, ApprovalMode.Decide(null, "solve", Plain));
    }

    [Fact]
    public void An_edit_script_card_is_a_script_by_its_kind_even_if_the_caller_forgot_the_fact()
    {
      Assert.Equal(ApprovalDecision.Ask, ApprovalMode.Decide(S, "edit_script", Plain));
      Assert.Equal(ApprovalDecision.Ask, ApprovalMode.Decide(S, "edit_script", null));
    }

    /// <summary>The decision cannot see the model's words: its whole signature is the mode, the kind and facts the plugin computed.
    /// A persuasive reason has nowhere to go (#235).</summary>
    [Fact]
    public void Decide_takes_a_mode_a_kind_and_computed_facts_and_nothing_else()
    {
      var m = typeof(ApprovalMode).GetMethod(nameof(ApprovalMode.Decide), BindingFlags.Public | BindingFlags.Static);
      var ps = m.GetParameters();
      Assert.Equal(new[] { "mode", "kind", "facts" }, ps.Select(p => p.Name).ToArray());
      Assert.Equal(new[] { typeof(string), typeof(string), typeof(ApprovalFacts) }, ps.Select(p => p.ParameterType).ToArray());
      Assert.Equal(typeof(ApprovalDecision), m.ReturnType);

      var fields = typeof(ApprovalFacts).GetProperties().Select(p => p.Name).OrderBy(x => x).ToArray();
      Assert.Equal(new[] { "FullControl", "HasDeletions", "HasScripts", "IsVerbHandOff" }, fields);   // four booleans; no text field exists
      Assert.All(typeof(ApprovalFacts).GetProperties(), p => Assert.Equal(typeof(bool), p.PropertyType));
    }

    [Fact]
    public void Facts_come_from_the_cards_own_record()
    {
      Assert.True(ApprovalMode.FactsFor("build", 1, 0).HasScripts);
      Assert.False(ApprovalMode.FactsFor("build", 0, 0).HasScripts);
      Assert.True(ApprovalMode.FactsFor("edit_script", 0, 0).HasScripts);
      Assert.True(ApprovalMode.FactsFor("edit", 0, 2).HasDeletions);
      Assert.False(ApprovalMode.FactsFor("edit", 0, 0).HasDeletions);   // an unwire alone is not a deletion
      Assert.True(ApprovalMode.FactsFor("reorganise", 0, 0).IsVerbHandOff);
      Assert.True(ApprovalMode.FactsFor("simplify", 0, 0).IsVerbHandOff);
      Assert.False(ApprovalMode.FactsFor("build", 0, 0).IsVerbHandOff);
    }

    // ---- the normaliser and the thread's mode ------------------------------------------------------------------------

    [Fact]
    public void A_hand_edited_value_is_the_default()
    {
      Assert.Equal(A, ApprovalMode.Normalise(null));
      Assert.Equal(A, ApprovalMode.Normalise(""));
      Assert.Equal(A, ApprovalMode.Normalise("always"));
      Assert.Equal(A, ApprovalMode.Normalise("NEVER!"));
      Assert.Equal(N, ApprovalMode.Normalise("  Never "));
      Assert.Equal(S, ApprovalMode.Normalise("SCRIPTS_AND_DELETIONS"));
      Assert.All(ApprovalMode.All, m => Assert.True(ApprovalMode.IsValid(m)));
      Assert.Equal(new[] { "ask", "scripts_and_deletions", "never" }, ApprovalMode.All);
    }

    [Fact]
    public void A_thread_reads_its_own_mode_and_a_thread_with_none_stored_is_ask()
    {
      Assert.Equal(N, ApprovalMode.ForThread("never"));
      Assert.Equal(S, ApprovalMode.ForThread("scripts_and_deletions"));
      Assert.Equal(A, ApprovalMode.ForThread(null));   // a conversation from before modes existed
      Assert.Equal(A, ApprovalMode.ForThread("hand edited"));

      // THE GUARANTEE IS THE SIGNATURE: the thread's mode is read from the thread alone, so a changed Settings default cannot reach it.
      var m = typeof(ApprovalMode).GetMethod(nameof(ApprovalMode.ForThread), BindingFlags.Public | BindingFlags.Static);
      Assert.Single(m.GetParameters());
    }

    // ---- the sentences, verbatim ---------------------------------------------------------------------------------------

    [Fact]
    public void The_three_Settings_sentences_are_the_rulings_words()
    {
      Assert.Equal("Ask every time: every change waits for your click on its card.", ApprovalMode.Description(A));
      Assert.Equal("Ask for scripts and deletions only: Dwight applies sliders, wires, placements and builds of components on its own, " +
                   "shows each card marked as approved automatically, and still asks before a script runs or a component is deleted.",
                   ApprovalMode.Description(S));
      Assert.Equal("Do not ask: every change Dwight can check is applied on its own; it still asks before Reorganise and Simplify. With Full control on, " +
                   "a script runs without asking too, and one Ctrl-Z may not undo everything it touches. Every other change is one Ctrl-Z.", ApprovalMode.Description(N));
      Assert.Equal("A conversation keeps the mode it was opened with, so changing this does not change one that is already open.",
                   ApprovalMode.ThreadHint);
      Assert.Equal(new[] { "Ask every time", "Ask for scripts and deletions only", "Do not ask" },
                   ApprovalMode.All.Select(ApprovalMode.Label).ToArray());
    }

    [Fact]
    public void The_state_line_is_verbatim_and_ask_has_none()
    {
      Assert.Equal("Approved automatically (Ask for scripts and deletions only).", ApprovalMode.AutoStateLine(S));
      Assert.Equal("Approved automatically (Do not ask).", ApprovalMode.AutoStateLine(N));
      Assert.Null(ApprovalMode.AutoStateLine(A));
    }

    [Fact]
    public void The_transcript_line_and_the_close_clause_are_the_pinned_sentences()
    {
      Assert.Equal("Approved automatically.", ApprovalMode.AutoUserText);
      Assert.Null(ApprovalMode.CloseClause(0));
      Assert.Equal("1 card was approved automatically.", ApprovalMode.CloseClause(1));
      Assert.Equal("2 cards were approved automatically.", ApprovalMode.CloseClause(2));
      Assert.Equal("7 cards were approved automatically.", ApprovalMode.CloseClause(7));
    }

    [Fact]
    public void The_count_is_per_thread_and_a_reset_starts_a_new_run()
    {
      var c = new AutoApprovalCount();
      Assert.Equal(0, c.Get("t1"));
      c.Add("t1"); c.Add("t1"); c.Add("t2");
      Assert.Equal(2, c.Get("t1"));
      Assert.Equal(1, c.Get("t2"));
      c.Reset("t1");
      Assert.Equal(0, c.Get("t1"));
      Assert.Equal(1, c.Get("t2"));
    }

    // ---- the card ------------------------------------------------------------------------------------------------------

    private static ApprovedScript Py(string source) =>
      new ApprovedScript { Region = "B1", Language = BuildScriptTypes.Python, Source = source, Inputs = { "Count", "Radius" }, Outputs = { "Pts" } };

    private static string BuildCard(string autoMode, params ApprovedScript[] scripts) =>
      ConsentCard.Html("build", "Because.", "tok", null, new[] { "B1" }, null, null, "components", scripts, autoMode);

    [Fact]
    public void A_card_approved_automatically_carries_the_state_line_under_its_title_and_has_no_button()
    {
      foreach (var mode in new[] { S, N })
      {
        var html = BuildCard(mode);
        var title = html.IndexOf("consent-act", StringComparison.Ordinal);
        var state = html.IndexOf(ApprovalMode.AutoStateLine(mode), StringComparison.Ordinal);
        var authority = html.IndexOf("Dwight will place and wire components in region B1", StringComparison.Ordinal);
        Assert.True(title >= 0 && state > title && state < authority, "the state line sits between the title and the authority line");
        Assert.Contains("<p class=\"consent-note consent-auto\">", html);
        Assert.DoesNotContain("<button", html);
        Assert.DoesNotContain("Yes, go ahead", html);
        Assert.Contains("Dwight's reason for asking", html);   // the card is still the whole card: reason, scope, authority
      }
    }

    [Fact]
    public void A_card_that_waits_is_byte_identical_to_the_card_before_modes()
    {
      var before = ConsentCard.Html("build", "Because.", "tok", null, new[] { "B1" }, null, null, "components", null);
      Assert.Equal(before, BuildCard(null));
      Assert.Equal(before, BuildCard(A));   // "ask" never marks a card
      Assert.Contains("Yes, go ahead", before);
      Assert.DoesNotContain("consent-auto", before);
    }

    [Fact]
    public void Every_card_kind_loses_its_button_and_gains_the_state_line_when_approved_automatically()
    {
      var setValue = new SetValueChange { Id = 4, Type = "Number Slider", Nick = "Count", Before = "5", After = "8" };
      Assert.Contains("<button", ConsentCard.Html("set_value", "why", "tok", setValue));
      var sv = ConsentCard.Html("set_value", "why", "tok", setValue, N);
      Assert.DoesNotContain("<button", sv);
      Assert.Contains("Approved automatically (Do not ask).", sv);

      var change = new ScriptEditChange
      {
        TargetId = 12, Label = "\"Area\"", Claim = ScriptEditClaim.Changes, Language = "python",
        NewSource = "a = 1\nb = 2\n", BaseSource = "a = 1\n", Diff = SourceDiff.Between("a = 1\n", "a = 1\nb = 2\n"),
        AddInputs = new string[0], AddOutputs = new string[0],
      };
      var facts = new ScriptTrialFacts { Ran = true };
      var es = ConsentCard.EditScriptHtml("why", "tok", change, ScriptEditOutcome.RanClean, facts, S);
      Assert.DoesNotContain("<button", es);
      Assert.Contains("Approved automatically (Ask for scripts and deletions only).", es);

      var plan = new EditPlan();
      plan.Steps.Add(new EditStep { Row = 1, Kind = EditOpKind.Wire, Text = "Wire A to B" });
      Assert.Contains("<button", ConsentCard.EditHtml("why", "tok", plan));
      var eh = ConsentCard.EditHtml("why", "tok", plan, N);
      Assert.DoesNotContain("<button", eh);
      Assert.Contains("Approved automatically (Do not ask).", eh);

      var spot = new SpotPlacement { Address = "Spot 1", Components = new List<string> { "Number Slider \"Count\"" } };
      Assert.Equal(2, CountOf(ConsentCard.SpotHtml("why", spot, "g", "l"), "<button"));
      var sp = ConsentCard.SpotHtml("why", spot, "g", "l", N);
      Assert.DoesNotContain("<button", sp);
      Assert.Contains("Approved automatically (Do not ask).", sp);
    }

    private static int CountOf(string text, string needle)
    {
      var n = 0; var i = 0;
      while ((i = text.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
      return n;
    }

    /// <summary>The reason a card shows is the model's, escaped, whatever the mode: markup in it is text, and it is not an input of the decision.</summary>
    [Fact]
    public void The_models_reason_is_escaped_on_a_marked_card_too()
    {
      var html = ConsentCard.Html("build", "<script>alert(1)</script>", "tok", null, new[] { "B1" }, null, null, "components", null, N);
      Assert.DoesNotContain("<script>", html);
      Assert.Contains("&lt;script&gt;", html);
    }

    // ---- the folded script card ------------------------------------------------------------------------------------------

    [Fact]
    public void The_script_summary_line_for_a_python_script_that_does_not_touch_the_document()
    {
      var src = "import math\nr = Radius\nPts = [r * math.cos(i) for i in range(Count)]\n";
      Assert.Equal("Python, 3 lines. Inputs: Count (item), Radius (item). Outputs: Pts. Touches the Rhino document: no.",
                   ScriptSummary.Line("Python", src, new[] { "Count", "Radius" }, new[] { "Pts" }));
    }

    [Fact]
    public void The_script_summary_line_for_a_csharp_script_that_touches_the_document()
    {
      var src = "private void RunScript(int n, ref object a)\n{\n  var d = Rhino.RhinoDoc.ActiveDoc;\n  a = d.Objects.Count;\n}\n";
      Assert.Equal("C#, 5 lines. Inputs: n (item). Outputs: a. Touches the Rhino document: yes.",
                   ScriptSummary.Line("C#", src, new[] { "n" }, new[] { "a" }));
    }

    [Fact]
    public void Every_marker_of_the_document_test_counts_and_nothing_else_does()
    {
      Assert.Equal(new[] { "Rhino.RhinoDoc", "scriptcontext.doc", "RhinoDocument", "doc.Objects", "Rhino.Runtime" }, ScriptSummary.DocumentMarkers);
      foreach (var m in ScriptSummary.DocumentMarkers) Assert.True(ScriptSummary.TouchesDocument("x = " + m + ".y"), m);
      Assert.False(ScriptSummary.TouchesDocument("import Rhino.Geometry as rg\nx = rg.Point3d(0,0,0)"));
      Assert.False(ScriptSummary.TouchesDocument("scriptcontext.sticky['k'] = 1"));
      Assert.False(ScriptSummary.TouchesDocument(null));
      Assert.False(ScriptSummary.TouchesDocument(""));
    }

    /// <summary>The line is a text match and says so, on the summary itself, so nobody reads it as an analysis.</summary>
    [Fact]
    public void The_summary_says_it_is_a_text_match()
    {
      var note = ScriptSummary.TextTestNote;
      Assert.Contains("plain text match", note);
      Assert.Contains("Rhino.RhinoDoc, scriptcontext.doc, RhinoDocument, doc.Objects and Rhino.Runtime", note);
      Assert.Contains("not an analysis", note);
      Assert.Contains(System.Net.WebUtility.HtmlEncode(note), BuildCard(null, Py("x = 1\n")));
    }

    [Fact]
    public void Line_counts_and_empty_port_lists()
    {
      Assert.Equal(0, ScriptSummary.LineCount(null));
      Assert.Equal(1, ScriptSummary.LineCount("a"));
      Assert.Equal(1, ScriptSummary.LineCount("a\n"));
      Assert.Equal(2, ScriptSummary.LineCount("a\r\nb"));
      Assert.Equal("Python, 1 line. Inputs: none. Outputs: none. Touches the Rhino document: no.",
                   ScriptSummary.Line("Python", "x = 1", null, new string[0]));
      Assert.Equal("Python, 1 line. Adds inputs: k (item). Adds outputs: none. Touches the Rhino document: no.",
                   ScriptSummary.Line("Python", "x = 1", new[] { "k" }, null, portsAreAdditions: true));
    }

    [Fact]
    public void The_build_card_folds_each_script_behind_its_summary_with_the_whole_source_inside()
    {
      var src = string.Join("\n", Enumerable.Range(0, 84).Select(i => "v" + i + " = " + i)) + "\nlast_marker = 1";
      var html = BuildCard(null, Py(src));

      var details = html.IndexOf("<details class=\"consent-script\">", StringComparison.Ordinal);
      var summaryAt = html.IndexOf("<summary class=\"consent-script-summary\"", StringComparison.Ordinal);
      var pre = html.IndexOf("<pre class=\"consent-source\">", StringComparison.Ordinal);
      var close = html.IndexOf("</details>", StringComparison.Ordinal);
      Assert.True(details >= 0 && details < summaryAt && summaryAt < pre && pre < close, "details > summary > pre, in that order");
      Assert.Contains("Region B1. Python, 85 lines. Inputs: Count (item), Radius (item). Outputs: Pts. Touches the Rhino document: no.", html);
      Assert.Contains("v0 = 0", html);
      Assert.Contains("last_marker = 1</pre>", html);   // the whole source, to its last line, inside the fold
      Assert.DoesNotContain("<details class=\"consent-script\" open", html);
    }

    [Fact]
    public void The_edit_script_card_folds_the_new_source_behind_a_summary_that_names_the_ports_it_adds()
    {
      var change = new ScriptEditChange
      {
        TargetId = 12, Label = "\"Area\"", Claim = ScriptEditClaim.Changes, Language = "python",
        NewSource = "area = w * h\noffset = area * k\n", BaseSource = "area = w * h\n", Diff = SourceDiff.Between("area = w * h\n", "area = w * h\noffset = area * k\n"),
        AddInputs = new[] { "k" }, AddOutputs = new[] { "offset" },
      };
      var html = ConsentCard.EditScriptHtml("why", "tok", change, ScriptEditOutcome.RanClean, new ScriptTrialFacts { Ran = true });

      Assert.Contains("<details class=\"consent-script\">", html);
      Assert.Contains("Python, 2 lines. Adds inputs: k (item). Adds outputs: offset. Touches the Rhino document: no.", html);
      Assert.Contains("<pre class=\"consent-source\">area = w * h\noffset = area * k\n</pre>", html);   // THE TEXT SHOWN IS THE TEXT APPLIED (#153)
      Assert.Contains("<button", html);   // a card that waits still has its button
    }

    [Fact]
    public void The_expanded_flag_exists_and_is_false_for_every_kind_this_build_knows()
    {
      foreach (var kind in new[] { "solve", "scaffold", "build", "set_value", "edit_script", "edit", "reorganise", "simplify", "run_script" })
        Assert.False(ConsentCard.ExpandedByDefaultFor(kind), kind);

      var open = ConsentCard.ScriptsSection(new[] { Py("x = 1") }, expandedByDefault: true);
      Assert.Contains("<details class=\"consent-script\" open>", open);
      Assert.Contains("<pre class=\"consent-source\">x = 1</pre>", open);
    }

    // ---- the close line ------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_close_line_gains_the_clause_only_when_there_is_one()
    {
      var turn = new Dictionary<string, RegionOutcome> { ["B1"] = RegionOutcome.Built };
      var plain = BuildProgress.ClosingHtml(new[] { "B1" }, turn, stoppedEarly: false);
      Assert.DoesNotContain("approved automatically", plain);
      Assert.Equal(plain, BuildProgress.ClosingHtml(new[] { "B1" }, turn, stoppedEarly: false, autoClause: null));

      var withClause = BuildProgress.ClosingHtml(new[] { "B1" }, turn, false, ApprovalMode.CloseClause(2));
      Assert.Contains("Build finished.", withClause);
      Assert.Contains("2 cards were approved automatically.</p>", withClause);

      // and the clause rides the line when nothing was built, too: what stands is still said first
      var nothing = BuildProgress.ClosingHtml(new[] { "B1" }, new Dictionary<string, RegionOutcome> { ["B1"] = RegionOutcome.Reverted }, false,
                                              ApprovalMode.CloseClause(1));
      Assert.Contains("Nothing was built:", nothing);
      Assert.EndsWith("1 card was approved automatically.</p>", nothing);
    }

    // ---- the Settings surfaces ------------------------------------------------------------------------------------------

    private static string RepoFile(params string[] parts)
    {
      var dir = AppContext.BaseDirectory;
      while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir, "Dwight.sln")))
        dir = System.IO.Path.GetDirectoryName(dir);
      Assert.NotNull(dir);
      return System.IO.File.ReadAllText(System.IO.Path.Combine(new[] { dir }.Concat(parts).ToArray()));
    }

    [Fact]
    public void Settings_html_has_the_three_way_control_and_the_exact_sentences()
    {
      var html = RepoFile("Dwight", "Resources", "settings.html");
      Assert.Contains("<label>Approval</label>", html);
      foreach (var mode in ApprovalMode.All)
      {
        Assert.Contains(mode, html.Substring(html.IndexOf("id=\"approval\" data-vals=", StringComparison.Ordinal), 80));
        Assert.Contains("<div class=\"opt\">" + ApprovalMode.Description(mode) + "</div>", html);   // the menu shows the mode's own name and its sentence (Tharun, Brief BO)
      }
      Assert.Contains(ApprovalMode.ThreadHint, html);
      Assert.Contains("dwightSend('approval:'+v)", html);
      Assert.Contains("d.approvalMode", html);
      // it sits under the Build heading, beside Build style
      Assert.True(html.IndexOf("data-pane=\"Build\"", StringComparison.Ordinal) < html.IndexOf("<label>Approval</label>", StringComparison.Ordinal));
      Assert.True(html.IndexOf("<label>Build style</label>", StringComparison.Ordinal) < html.IndexOf("<label>Approval</label>", StringComparison.Ordinal));
    }

    [Fact]
    public void The_Eto_dialog_has_the_same_three_way_choice_from_the_same_sentences()
    {
      var cs = RepoFile("Dwight", "UI", "SettingsDialog.cs");
      Assert.Contains("ApprovalMode.All", cs);
      Assert.Contains("ApprovalMode.Label(mode)", cs);
      Assert.Contains("ApprovalMode.Description(", cs);
      Assert.Contains("ApprovalMode.ThreadHint", cs);
      Assert.Contains("s.ApprovalMode = ApprovalModeAt(", cs);

      var panel = RepoFile("Dwight", "UI", "DwightSettingsPanel.cs");
      Assert.Contains("cmd.StartsWith(\"approval:\"", panel);
      Assert.Contains("ApprovalMode.IsValid(v)", panel);
    }

    [Fact]
    public void The_settings_default_is_ask()
    {
      var cs = RepoFile("Dwight", "Config", "DwightSettings.cs");
      Assert.Contains("public string ApprovalMode { get; set; } = Dwight.Agent.ApprovalMode.Ask;", cs);
    }

    /// <summary>The panel's wiring, pinned by its text because it links Grasshopper and cannot be run here: the decision is made from the THREAD's
    /// mode through the one function, an automatic approval takes the click's own path, and it records itself as the pinned user line.</summary>
    [Fact]
    public void The_panel_wires_the_decision_through_the_one_function_and_the_clicks_own_path()
    {
      var cs = RepoFile("Dwight", "UI", "DwightScaffold.cs");
      Assert.Contains("CommandHistory.ThreadApprovalMode(doc, threadId)", cs);                 // the thread's mode, never the default
      Assert.Contains("ApprovalMode.Decide(approvalMode, kind, ApprovalMode.FactsFor(", cs);   // the one decision
      Assert.Contains("OnConsentClick(wp, doc, threadId, a.RawToken, a.Mode)", cs);            // the click's own path
      Assert.Contains("autoMode != null ? ApprovalMode.AutoUserText : ApprovalMode.ClickUserText", cs);   // Brief BG: one constant
      Assert.Equal("Yes, go ahead.", ApprovalMode.ClickUserText);
      Assert.Contains("result.Outcome == TurnOutcome.Complete ? pendingAuto", cs);              // a stopped turn never shows a card marked approved
      Assert.Contains("ApprovalMode.Normalise(DwightSettings.Current.ApprovalMode)", cs);       // the default is copied once, at open
      Assert.DoesNotContain("DwightSettings.Current.ApprovalMode", cs.Replace("ApprovalMode.Normalise(DwightSettings.Current.ApprovalMode)", ""));
    }

    // ---- the docs --------------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_public_docs_state_the_modes_and_the_two_rows_that_never_change()
    {
      var build = RepoFile("docs", "public", "build.md");
      Assert.Contains("## Approval", build);
      foreach (var mode in ApprovalMode.All) Assert.Contains(ApprovalMode.Description(mode), build);
      Assert.Contains("Reorganise and Simplify always ask", build);
      Assert.Contains("A script Dwight cannot check asks under the first two modes", build);
      Assert.DoesNotContain("run_script", build);   // an unbuilt kind is not named in a public page
      Assert.Contains(ApprovalMode.ThreadHint, build);

      var limits = RepoFile("docs", "public", "known-limitations.md");
      Assert.Contains("An automatic approval is still a change the panel reports", limits);
      Assert.Contains("a turn can make several in a row", limits);

      var changelog = RepoFile("docs", "public", "changelog.md");
      Assert.Contains("30 October 2026: the Build chat has three approval modes", changelog);
    }
  }
}
