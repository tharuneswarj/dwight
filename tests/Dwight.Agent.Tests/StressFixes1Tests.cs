using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Dwight.Agent;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>Brief CA: what the first stress run broke, each from a real line in its record.</summary>
  [Collection(TurnShapeTests.StaticRecord)]   // touches TurnShape's static counters (Clear, Examined, AnnouncedChangeCount): never in parallel with the classes that read them; main went red on the #222 merge with a count of 3 for 1
  public class StressFixes1Tests
  {
    private const string Py = "import math\nresult = radius * 2";

    private static ApprovedScript Card(string region = "B2", string source = Py) => new ApprovedScript
    {
      Region = region, Language = BuildScriptTypes.Python, Source = source,
      Inputs = new List<string> { "radius" }, Outputs = new List<string> { "result" },
    };

    private static JsonObject BuildArgs(string region, string components) =>
      (JsonObject)JsonNode.Parse("{\"region\":\"" + region + "\",\"components\":" + components + "}");

    private static string Panel()
    {
      var dir = new DirectoryInfo(AppContext.BaseDirectory);
      while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Dwight.sln"))) dir = dir.Parent;
      Assert.True(dir != null);
      return File.ReadAllText(Path.Combine(dir.FullName, "Dwight", "UI", "DwightScaffold.cs")).Replace("\r\n", "\n");
    }

    // ---- 1. the approved script at the build step ----------------------------------------------------------------------

    [Fact]
    public void A_sourceless_script_in_an_approved_region_takes_the_cards_text_and_ports_byte_for_byte()
    {
      var args = BuildArgs("B2", "[{\"ref\":\"s\",\"type\":\"Python 3 Script\",\"nick\":\"Shade\"}]");

      Assert.Equal(1, BuildScriptGate.FillFromCard(args, new[] { Card() }));

      var c = args["components"][0];
      Assert.Equal(Py, c["source"].GetValue<string>());
      Assert.Equal(new[] { "radius" }, c["inputs"].AsArray().Select(i => i.GetValue<string>()));
      Assert.Equal(new[] { "result" }, c["outputs"].AsArray().Select(i => i.GetValue<string>()));
      Assert.Equal("Shade", c["nick"].GetValue<string>());
    }

    /// <summary>The whole seam: filled, then the real parser reads it with its wires to the card's port names, then the real gate holds it to the card. #315: the
    /// fact tells the model it may leave these out, so the behaviour's side carries the test.</summary>
    [Fact]
    public void The_filled_spec_passes_the_real_parser_with_wires_to_the_cards_ports_and_the_gate_keeps_the_cards_text()
    {
      var args = BuildArgs("B2", "[{\"ref\":\"r\",\"type\":\"Number Slider\"},{\"ref\":\"s\",\"type\":\"Python 3 Script\"}]");
      args["wires"] = JsonNode.Parse("[{\"from\":{\"ref\":\"r\"},\"to\":{\"ref\":\"s\",\"param\":\"radius\"}}]");
      var cards = new[] { Card() };
      BuildScriptGate.FillFromCard(args, cards);

      Assert.True(BuildSpecParser.TryParse(args, t => true, t => new string[0], id => IdState.Unknown, out var spec, out var error), error?.Message);
      Assert.Null(BuildScriptGate.Adopt(spec, false, cards, out var substitutions));

      var script = spec.Components.Single(c => c.Ref == "s");
      Assert.Equal(Py, script.Source);
      Assert.Empty(substitutions);
    }

    [Fact]
    public void A_region_whose_card_showed_no_script_is_untouched_and_a_script_sent_there_stays_refused()
    {
      var args = BuildArgs("A1", "[{\"ref\":\"s\",\"type\":\"Python 3 Script\"}]");
      Assert.Equal(0, BuildScriptGate.FillFromCard(args, new[] { Card("B2") }));
      Assert.Null(args["components"][0]["source"]);

      var sent = BuildArgs("A1", "[{\"ref\":\"s\",\"type\":\"Python 3 Script\",\"source\":\"a = 1\",\"outputs\":[\"b\"]}]");
      Assert.True(BuildSpecParser.TryParse(sent, t => true, t => new string[0], id => IdState.Unknown, out var spec, out _));
      Assert.NotNull(BuildScriptGate.Adopt(spec, false, new[] { Card("B2") }, out _));
    }

    [Fact]
    public void Only_a_script_component_takes_it_and_the_language_must_match()
    {
      var slider = BuildArgs("B2", "[{\"ref\":\"r\",\"type\":\"Number Slider\"}]");
      Assert.Equal(0, BuildScriptGate.FillFromCard(slider, new[] { Card() }));

      var csharp = BuildArgs("B2", "[{\"ref\":\"s\",\"type\":\"C# Script\"}]");
      Assert.Equal(0, BuildScriptGate.FillFromCard(csharp, new[] { Card() }));
    }

    [Fact]
    public void Several_pair_up_in_the_cards_order_and_a_script_sent_uses_up_its_card()
    {
      var two = new[] { Card("B2", "first = 1"), Card("B2", "second = 2") };

      var both = BuildArgs("B2", "[{\"ref\":\"a\",\"type\":\"Python 3 Script\"},{\"ref\":\"b\",\"type\":\"Python 3 Script\"},{\"ref\":\"c\",\"type\":\"Python 3 Script\"}]");
      Assert.Equal(2, BuildScriptGate.FillFromCard(both, two));
      Assert.Equal("first = 1", both["components"][0]["source"].GetValue<string>());
      Assert.Equal("second = 2", both["components"][1]["source"].GetValue<string>());
      Assert.Null(both["components"][2]["source"]);

      var one = BuildArgs("B2", "[{\"ref\":\"a\",\"type\":\"Python 3 Script\",\"source\":\"first = 1\"},{\"ref\":\"b\",\"type\":\"Python 3 Script\"}]");
      Assert.Equal(1, BuildScriptGate.FillFromCard(one, two));
      Assert.Equal("second = 2", one["components"][1]["source"].GetValue<string>());
    }

    [Fact]
    public void The_build_turn_is_told_the_source_the_ports_and_that_it_may_leave_them_out()
    {
      var fact = ApprovedTurnFact.ScriptsFact(new[] { Card() });

      Assert.Contains(Py, fact);
      Assert.Contains("Inputs: radius. Outputs: result.", fact);
      Assert.Contains("leave \"source\", \"inputs\" and \"outputs\" out", fact);
    }

    [Fact]
    public void The_fact_is_bounded_and_says_what_it_left_out()
    {
      var big = new string('x', ApprovedTurnFact.MaxFactSourceChars - 10);
      var fact = ApprovedTurnFact.ScriptsFact(new[] { Card("A1", big), Card("B1", "this_one_does_not_fit = 123456") });

      Assert.Contains(big, fact);
      Assert.DoesNotContain("this_one_does_not_fit", fact);
      Assert.Contains("is not repeated here", fact);
    }

    [Fact]
    public void A_script_stays_on_file_until_a_build_of_its_region_stands_and_grants_nothing()
    {
      var file = new ScriptsOnFile();
      file.Remember("t", new[] { Card("B2"), Card("C2", "c = 1") });
      file.Remember("other", new[] { Card("Z1") });

      Assert.Equal(new[] { "B2", "C2" }, file.For("t").Select(s => s.Region));
      Assert.Equal(new[] { "C2" }, file.For("t", new[] { "C2", "D1" }).Select(s => s.Region));

      file.Remember("t", new[] { Card("B2", "newer = 1") });
      Assert.Equal("newer = 1", file.For("t", new[] { "B2" }).Single().Source);

      file.Landed("t", "B2");
      Assert.Equal(new[] { "C2" }, file.For("t").Select(s => s.Region));
      Assert.Single(file.For("other"));
      Assert.Empty(file.For("nobody"));
    }

    [Fact]
    public void A_later_turn_is_told_what_is_on_file_and_that_nothing_needs_pasting()
    {
      Assert.Null(ApprovedTurnFact.ScriptsOnFileFact(new ApprovedScript[0]));
      var fact = ApprovedTurnFact.ScriptsOnFileFact(new[] { Card() });
      Assert.StartsWith(ApprovedTurnFact.OnFileHeading, fact);
      Assert.Contains("leave \"scripts\" out", fact);
      Assert.Contains(Py, fact);
    }

    [Fact]
    public void The_panel_wires_the_file_and_the_build_fills_before_it_parses()
    {
      var panel = Panel();
      Assert.Contains("if (resolvedScripts.Count == 0) resolvedScripts = _scriptsOnFile.For(threadId, approved);", panel);
      Assert.Contains("_scriptsOnFile.Remember(threadId, resolvedScripts);", panel);
      Assert.Contains("_scriptsOnFile.Landed(threadId, address);", panel);
      Assert.Contains("ApprovedTurnFact.ScriptsOnFileFact(_scriptsOnFile.For(threadId))", panel);

      var dir = new DirectoryInfo(AppContext.BaseDirectory);
      while (!File.Exists(Path.Combine(dir.FullName, "Dwight.sln"))) dir = dir.Parent;
      var build = File.ReadAllText(Path.Combine(dir.FullName, "Dwight", "Agent", "GhBuildRegion.cs"));
      Assert.True(build.IndexOf("BuildScriptGate.FillFromCard(args, ledger.ApprovedScripts);", StringComparison.Ordinal) <
                  build.IndexOf("LiveResolvers(doc, ids).Parse(args, out var spec", StringComparison.Ordinal));
    }

    // ---- 2. a build that stopped with regions left ---------------------------------------------------------------------

    [Theory]
    [InlineData(true, false, 0, 2, 1, false, true)]    // the field case: A1 built, B1 and C1 left
    [InlineData(true, false, 0, 2, 0, false, true)]    // attempted nothing: one try
    [InlineData(true, false, 0, 2, 0, true, false)]    // a continuation that attempted nothing ends there
    [InlineData(true, false, 0, 1, 1, true, true)]     // a continuation that made progress goes on
    [InlineData(false, false, 0, 2, 1, false, false)]  // "ask": the user keeps the sentence
    [InlineData(true, true, 0, 2, 1, false, false)]    // a stop or a cut-off explains it
    [InlineData(true, false, 1, 2, 1, false, false)]   // a revert has its own follow-up
    [InlineData(true, false, 2, 2, 1, false, false)]   // a refusal has its own follow-up
    [InlineData(true, false, 0, 0, 3, false, false)]   // nothing left
    public void A_build_continues_only_where_nothing_explains_the_stop_and_only_while_it_progresses(
      bool automatic, bool stopped, int endIndex, int left, int attempted, bool continuation, bool expected)
    {
      var end = endIndex == 0 ? AutoFollowUp.FlowEnd.Succeeded : endIndex == 1 ? AutoFollowUp.FlowEnd.Reverted : AutoFollowUp.FlowEnd.Refused;
      Assert.Equal(expected, AutoFollowUp.ShouldContinueBuild(automatic, stopped, end, left, attempted, continuation));
    }

    /// <summary>The bound in numbers: each continuation must attempt one, so five regions can never take more than five continuations.</summary>
    [Fact]
    public void The_chain_is_bounded_by_the_regions()
    {
      var left = 5;
      var turns = 0;
      var continuation = false;
      while (AutoFollowUp.ShouldContinueBuild(true, false, AutoFollowUp.FlowEnd.Succeeded, left, 1, continuation))
      {
        turns++;
        left--;
        continuation = true;
        Assert.True(turns <= 5);
      }
      Assert.Equal(5, turns);
    }

    [Fact]
    public void What_is_not_attempted_comes_from_the_card_and_the_outcomes()
    {
      var outcomes = new Dictionary<string, RegionOutcome> { ["A1"] = RegionOutcome.Built };
      Assert.Equal(new[] { "B1", "C1" }, AutoFollowUp.NotAttempted(new[] { "A1", "B1", "C1" }, outcomes));
      Assert.Empty(AutoFollowUp.NotAttempted(null, outcomes));

      Assert.Equal("Dwight is continuing: B1, C1 approved and not attempted yet.", AutoFollowUp.ContinueLine(new[] { "B1", "C1" }));
      Assert.Contains("B1, C1", AutoFollowUp.ContinueFact(new[] { "B1", "C1" }));
      Assert.Contains("no new card", AutoFollowUp.ContinueFact(new[] { "B1" }));

      var ended = AutoFollowUp.BuildEndedFact(new Dictionary<string, RegionOutcome> { ["A1"] = RegionOutcome.Reverted }, new[] { "A1", "B1" });
      Assert.Contains("B1 was not attempted", ended);
    }

    [Fact]
    public void The_continuation_keeps_the_cards_regions_and_scripts_and_the_automatic_mark()
    {
      Assert.Contains("RunTurn(wp, doc, threadId, TurnCapability.Build, notAttempted, stillGranted, autoApproved: true, isBuildContinuation: true);", Panel());
    }

    // ---- 3. the report and the Rhino document --------------------------------------------------------------------------

    private static DocSnapshot Snap(int rhinoObjects, int rhinoLayers) =>
      new DocSnapshot { Components = 4, Wires = 3, Groups = 0, Errors = 0, Fingerprint = "a", RhinoObjects = rhinoObjects, RhinoLayers = rhinoLayers };

    [Fact]
    public void The_bake_that_read_as_nothing_now_reads_as_what_it_did()
    {
      var report = FullControl.ReportFor("Bake the curves", new RunOutcome { Before = Snap(0, 1), After = Snap(2, 2), Added = 0 });

      Assert.Contains("Changed: 0 components, 0 wires, 0 groups", report);
      Assert.Contains("In the Rhino document: +2 objects, +1 layer.", report);
      Assert.Contains("the script added no objects to this definition", report);
      Assert.Contains(FullControl.RhinoUndoLine, report);
      Assert.Contains("does not reach the Rhino document", FullControl.RhinoUndoLine);
    }

    [Fact]
    public void A_script_that_left_the_rhino_document_alone_says_a_count_cannot_see_an_edit()
    {
      var report = FullControl.ReportFor("Rename", new RunOutcome { Before = Snap(3, 2), After = Snap(3, 2), Added = 0 });

      Assert.Contains("In the Rhino document: 0 objects, 0 layers (a count: an edit that adds or removes nothing does not show here).", report);
      Assert.DoesNotContain(FullControl.RhinoUndoLine, report);
    }

    [Fact]
    public void A_count_that_was_not_read_is_never_a_change_and_is_not_mentioned()
    {
      var before = new DocSnapshot { Components = 1, Fingerprint = "a" };
      var report = FullControl.ReportFor("X", new RunOutcome { Before = before, After = Snap(2, 2), Added = 0 });

      Assert.DoesNotContain("In the Rhino document", report);
      Assert.DoesNotContain(FullControl.RhinoUndoLine, report);
    }

    [Fact]
    public void A_script_that_raised_after_baking_says_so_too()
    {
      var report = FullControl.ReportFor("Bake", new RunOutcome { Exception = "ValueError: no", Before = Snap(0, 1), After = Snap(1, 1), Added = 0 });

      Assert.Contains("In the Rhino document: +1 object, 0 layers.", report);
      Assert.Contains(FullControl.RhinoUndoLine, report);
    }

    [Fact]
    public void The_shim_measures_the_rhino_document_as_counts()
    {
      var dir = new DirectoryInfo(AppContext.BaseDirectory);
      while (!File.Exists(Path.Combine(dir.FullName, "Dwight.sln"))) dir = dir.Parent;
      var shim = File.ReadAllText(Path.Combine(dir.FullName, "Dwight", "Agent", "GhRunScript.cs"));
      Assert.Contains("RhinoObjects = rhinoObjects,", shim);
      Assert.Contains("rhinoLayers = rhino.Layers.ActiveCount;", shim);
    }

    // ---- 4. run_script's repeat, and what a script is told ---------------------------------------------------------------

    private static readonly ConsentTokens Tokens = new ConsentTokens();

    [Fact]
    public void The_card_waiting_in_a_chat_can_be_read_and_stops_being_read_once_it_is_answered_or_replaced()
    {
      var tokens = new ConsentTokens();
      var seed = new HandoffSeed { Source = "print(1)", Why = "print" };
      var first = tokens.Mint("t", FullControl.Kind, seed);

      Assert.Same(seed, tokens.WaitingSeed("t", FullControl.Kind));
      Assert.Null(tokens.WaitingSeed("t", "build"));
      Assert.Null(tokens.WaitingSeed("other", FullControl.Kind));

      Assert.True(tokens.Take(first, "t", out _, out _, out _));
      Assert.Null(tokens.WaitingSeed("t", FullControl.Kind));

      var a = tokens.Mint("t", FullControl.Kind, seed);
      var replacing = new HandoffSeed { Source = "print(2)" };
      tokens.Mint("t", FullControl.Kind, replacing);
      Assert.Same(replacing, tokens.WaitingSeed("t", FullControl.Kind));
      Assert.NotNull(a);
    }

    [Theory]
    [InlineData("print(1)", "print(1)", true)]
    [InlineData("print(1)", "print(2)", false)]
    [InlineData(null, "print(1)", false)]
    public void The_same_script_is_the_grants_own_comparison(string waiting, string asked, bool expected)
    {
      Assert.Equal(expected, FullControl.SameScript(waiting, asked));
    }

    [Fact]
    public void Asking_again_ends_the_turn_on_the_card_that_is_there_and_says_no_new_card_was_raised()
    {
      var answer = FullControl.StillWaitingResult();

      Assert.True(AgentHostTools.ShowedCard(AgentHostTools.RequestMutation, answer));
      var result = JsonNode.Parse(answer)["result"];
      Assert.True(result["alreadyWaiting"].GetValue<bool>());
      Assert.True(result["turnEnds"].GetValue<bool>());
      Assert.Contains("No new card", result["whereItIs"].GetValue<string>());
    }

    [Fact]
    public void A_waiting_card_is_stated_at_turn_start_with_its_source_and_only_the_click_answers_it()
    {
      var fact = FullControl.WaitingFact("Internalize a point", "print('x')");

      Assert.StartsWith("A CARD IS WAITING FOR THE PERSON", fact);
      Assert.Contains("Internalize a point", fact);
      Assert.Contains("print('x')", fact);
      Assert.Contains("A typed message is not an answer", fact);
      Assert.Contains("only their click runs it", fact);
      Assert.DoesNotContain("whatever the approval mode", fact);   // Brief CD: a card that waits is waiting for a click; one under Do not ask never waits

      var panel = Panel();
      Assert.Contains("FullControl.WaitingFact(waitingScript.Why, waitingScript.Source)", panel);
      var same = panel.IndexOf("FullControl.SameScript(_consents.WaitingSeed(threadId", StringComparison.Ordinal);
      var mint = panel.IndexOf("var runToken = MintConsent(", StringComparison.Ordinal);
      Assert.True(same > 0 && same < mint, "the same script is caught before a token is minted");
    }

    /// <summary>The sentence sat on the run_script tool, which a read-only turn is never offered and U100 never calls, so the model asking for a card did not read it.</summary>
    [Fact]
    public void Where_a_script_runs_is_told_where_the_asking_model_reads_it_and_a_failure_repeats_it()
    {
      Assert.Contains(FullControl.EnvironmentSentence, FullControl.ThreadFact());
      Assert.Contains(FullControl.EnvironmentSentence, FullControl.KindCan);
      Assert.Contains("no ghenv", FullControl.EnvironmentSentence);

      var request = AgentLoopCore.ToolsFor(TurnCapability.ReadOnly, true).Single(t => t.Name == AgentHostTools.RequestMutation);
      Assert.Contains("no ghenv", request.InputSchemaJson);

      var raised = FullControl.ReportFor("Internalize", new RunOutcome
      {
        Exception = "ExecuteException: 'NoneType' object has no attribute 'Component' (at [5:1])", Before = Snap(0, 1), After = Snap(0, 1),
      });
      Assert.Contains(FullControl.EnvironmentSentence, raised);
      var ran = FullControl.ReportFor("Internalize", new RunOutcome { Before = Snap(0, 1), After = Snap(0, 1) });
      Assert.DoesNotContain(FullControl.EnvironmentSentence, ran);
    }

    /// <summary>#315: the sentence names what the preamble binds, so a change to either fails here.</summary>
    [Fact]
    public void The_sentence_agrees_with_what_the_preamble_binds()
    {
      var executable = FullControl.ExecutableSource("print(1)");
      Assert.Contains("rhdoc", executable);
      Assert.Contains("ghdoc", executable);
      Assert.DoesNotContain("ghenv", executable);
      Assert.Contains("rhdoc is the Rhino document and ghdoc is this definition", FullControl.EnvironmentSentence);
    }

    // ---- 5. the line beneath a real question -------------------------------------------------------------------------------

    private const string TheFinRow =
      "I'd propose rebuilding the **Fins** region to keep the sliders and add a small script.\n\n" +
      "Would you like me to scaffold this as a plan first, or do you already want me to raise a rebuild card for the Fins region with the tapering script? " +
      "Let me know which, and I'll put the card up - I don't want to pick the design levers without you.";

    [Fact]
    public void The_real_question_from_lane_A_draws_no_line()
    {
      Assert.False(TurnShape.AnnouncedWithoutAsking(TheFinRow, 0, 0));
      Assert.False(AgentLoopCore.AnnouncedNothingAsked(TurnCapability.ReadOnly, new TurnResult { Outcome = TurnOutcome.Complete, Text = TheFinRow }));
      Assert.Null(AgentLoopCore.NothingAskedHtml(TurnCapability.ReadOnly, new TurnResult { Outcome = TurnOutcome.Complete, Text = TheFinRow }));
    }

    [Theory]
    [InlineData("Which of these did you mean?")]
    [InlineData("I'd propose rebuilding B1.\n\nShall I?")]
    [InlineData("Rebuilding B1 is one way. Would you rather keep it?\n")]
    public void A_message_that_ends_on_a_question_did_what_it_should(string text)
    {
      Assert.True(TurnShape.EndsAskingTheUser(text));
      Assert.False(TurnShape.AnnouncedWithoutAsking(text, 0, 0));
    }

    /// <summary>The catch's other cases stand: nothing here stops it reading a real announcement, a question earlier in the message included.</summary>
    [Theory]
    [InlineData("Raising the build card for B1 now.")]
    [InlineData("Is that right? Raising the card now.")]
    [InlineData("Did that land?\n\nRaising the build card for B1 now.")]
    [InlineData("Rebuilding A1 now.")]
    [InlineData("Submitted as instructed, and refused again.")]
    public void A_real_announcement_keeps_the_line(string text)
    {
      Assert.False(TurnShape.EndsAskingTheUser(text));
      Assert.True(TurnShape.AnnouncedWithoutAsking(text, 0, 0));
    }
  }
}
