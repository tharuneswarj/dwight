using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;
using Dwight.Agent;

namespace Dwight.Agent.Tests
{
  /// <summary>Brief AW: the executable source reaches the runtime (U99), and the approval of a run_script card runs the grant itself,
  /// with one read-only turn after (U100).</summary>
  public class RunScriptApprovalTests
  {
    private static DocSnapshot Snap(int c, int w, int g, int e, string fp) =>
      new DocSnapshot { Components = c, Wires = w, Groups = g, Errors = e, Fingerprint = fp };

    // ---------------------------------------------------------------- U99

    [Fact]
    public void The_runtime_gets_the_header_when_the_approved_source_has_none_for_LF_and_CRLF()
    {
      Assert.Equal("#! python 3\n" + FullControlTests.PreambleText + "print(1)\nprint(2)", FullControl.ExecutableSource("print(1)\nprint(2)"));
      Assert.Equal("#! python 3\n" + FullControlTests.PreambleText + "print(1)\nprint(2)", FullControl.ExecutableSource("print(1)\r\nprint(2)"));
    }

    [Fact]
    public void A_source_that_carries_a_header_reaches_the_runtime_as_approved()
    {
      // The grant holds the source line-normalised (the card, the grant and the comparison hold one text), so "as approved" is that text.
      foreach (var approved in new[] { "#! python 3\nprint(1)", "#! python3\nprint(1)", "#! python 3\r\nprint(1)" })
      {
        // U104: the model's own header stays first, the preamble follows it, and the approved lines after the header are unchanged.
        var normalised = FullControl.NormaliseSource(approved);
        var executable = FullControl.ExecutableSource(approved);
        Assert.StartsWith(normalised.Split('\n')[0] + "\n" + FullControlTests.PreambleText, executable);
        Assert.EndsWith("# --- your script follows ---\nprint(1)", executable);
        Assert.Equal(FullControl.ExecutableSource(approved), FullControl.ExecutableSource(FullControl.ExecutableSource(approved)));   // idempotent
      }
    }

    [Fact]
    public void The_grant_compares_the_approved_text_never_the_executable_one()
    {
      // The header is added only on the way to the runtime: the approved text and the text a model sends still compare equal.
      Assert.Null(FullControl.FirstDifference("print(1)", "print(1)"));
      Assert.NotNull(FullControl.FirstDifference("print(1)", FullControl.ExecutableSource("print(1)")));
    }

    [Fact]
    public void GhRunScript_hands_the_executable_source_to_the_route()
    {
      var gh = RepoFile("Dwight", "Agent", "GhRunScript.cs");
      Assert.Contains("route.Run(FullControl.ExecutableSource(source), out var stdout, out var stderr, out var exception);", gh);   // Brief AY added stderr
      Assert.DoesNotContain("route.Run(source,", gh);
    }

    // ---------------------------------------------------------------- U100: the sentences

    [Fact]
    public void The_approval_and_its_reports_are_pinned()
    {
      Assert.Equal("Running the script you approved.", RunScriptApproval.ApprovedLine);
      Assert.Equal("Dwight did not run the script: Full control is not on for this conversation, or the approved source was lost.",
        RunScriptApproval.NotRunLine);
      Assert.Equal("That approval covered one run and it has been used. Ask again if another script is needed.", RunScriptApproval.UsedLine);

      var record = Guid.Parse("33333333-3333-3333-3333-333333333333");
      var ran = new RunOutcome { Before = Snap(1, 1, 1, 0, "a"), After = Snap(3, 2, 1, 0, "b"), Stdout = "done", Added = 2, RecordId = record, TopAfter = record };
      var line = RunScriptApproval.PanelLine("Rename the sliders.", ran);
      Assert.Equal(FullControl.ReportFor("Rename the sliders.", ran), line);   // Brief AY: the measured Ctrl-Z clause
      Assert.EndsWith("One Ctrl-Z undoes the 2 objects the script added.", line);
      Assert.StartsWith("Ran Rename the sliders. Changed: +2 components, +1 wire, 0 groups, 0 new errors;", line);
      Assert.Equal("[Run by Dwight on the user's approval] " + line +
                   " This is a report of what changed, not a check. Read what changed, tell the user in your own words, and do not call it verified.",
        RunScriptApproval.TranscriptLine(ran, line));

      var raised = new RunOutcome { Exception = "NameError: x (line 2)", Before = Snap(1, 1, 1, 0, "a"), After = Snap(1, 1, 1, 0, "a") };
      Assert.Equal(FullControl.RaisedReport("Rename", raised.Exception, raised.Before, raised.After, null, null, FullControl.UndoNotPushedLine),
        RunScriptApproval.PanelLine("Rename", raised));   // nothing added before it failed: nothing was recorded (Brief AY, U101)

      var probe = new RunOutcome { Unavailable = "Rhino's Python 3 runtime could not be reached." };
      var probeLine = RunScriptApproval.PanelLine("Rename", probe);
      Assert.Equal("Rhino's Python 3 runtime could not be reached. Nothing was run, and nothing on the canvas changed.", probeLine);
      Assert.Equal("[Not run: the user approved this script, and Dwight ran nothing] " + probeLine, RunScriptApproval.TranscriptLine(probe, probeLine));

      Assert.Equal(FullControl.OverranReport("Rename", 60), RunScriptApproval.OverranLine("Rename", 60));
      Assert.Equal("[Run by Dwight on the user's approval, still running] " + FullControl.OverranReport("Rename", 60) +
                   " Do not ask to run it again; Dwight reports the finish when it comes.", RunScriptApproval.OverranTranscript("Rename", 60));

      foreach (var s in new[] { RunScriptApproval.ApprovedLine, line, probeLine, RunScriptApproval.TranscriptLine(ran, line) })
      {
        Assert.DoesNotContain("—", s);
        Assert.DoesNotContain(" verified.", s.Replace("do not call it verified.", ""));
      }
    }

    [Fact]
    public void One_follow_up_turn_reads_a_script_that_ran_and_none_follows_one_that_never_started_or_is_still_running()
    {
      Assert.True(RunScriptApproval.FollowUp(new RunOutcome(), overran: false));
      Assert.True(RunScriptApproval.FollowUp(new RunOutcome { Exception = "boom" }, overran: false));   // it ran; things may have changed
      Assert.False(RunScriptApproval.FollowUp(new RunOutcome { Unavailable = "no runtime" }, overran: false));
      Assert.False(RunScriptApproval.FollowUp(new RunOutcome(), overran: true));
      Assert.False(RunScriptApproval.FollowUp(null, overran: false));
    }

    [Fact]
    public void The_card_says_Dwight_cannot_stop_the_script_before_the_click()
    {
      Assert.Contains("cannot stop it once it has started", FullControl.CardAuthority);
    }

    // ---------------------------------------------------------------- U100: the route, read from the source

    [Fact]
    public void The_approval_runs_the_grant_from_the_record_before_any_model_turn_and_then_reads_once()
    {
      var cs = RepoFile("Dwight", "UI", "DwightScaffold.cs");
      var route = Between(cs, "if (string.Equals(kind, Dwight.Agent.FullControl.Kind, StringComparison.Ordinal))", "// NARRATIVE only (#220)");
      Assert.Contains("new RunScriptGrant { Source = seed.Source, Purpose = seed.Why", route);   // the card's own record
      Assert.Contains("RunApprovedScript(wp, doc, threadId, grant, autoMode != null);", route);
      Assert.DoesNotContain("RunTurn(", route);                                                  // no model turn before the run

      var run = Between(cs, "private static void RunApprovedScript(", "internal static void AppendReport(");
      var runAt = run.IndexOf("GhRunScript.Run(doc, grant.Source, purpose,", StringComparison.Ordinal);
      Assert.True(runAt > 0, "the click runs the grant's own source");
      Assert.True(run.IndexOf("grant.TryUse()", StringComparison.Ordinal) < runAt, "one approval covers one run, checked before it");
      var turns = Regex.Matches(run, @"RunTurn\(").Count;
      Assert.Equal(1, turns);                                                                     // exactly one follow-up
      Assert.Contains("RunTurn(wp, doc, threadId, TurnCapability.ReadOnly)", run);              // and it holds no approval
      Assert.True(run.IndexOf("RunTurn(", StringComparison.Ordinal) > runAt, "the follow-up comes after the run, never before");
      Assert.Contains("RunScriptApproval.TranscriptLine(outcome, line, automatic)", run);                    // the report the follow-up reads
    }

    [Fact]
    public void No_caller_starts_a_run_script_turn_so_the_second_call_path_is_unreachable()
    {
      // The RunScript capability and AgentLoop's ExecuteRunScript are left in place, unreachable: the only caller that ever started such
      // a turn was the approval, and it now runs the grant itself. The ask (request_mutation, kind run_script) is unchanged.
      // The control: the pattern finds the call the approval used to make, so a "none found" below is a finding.
      Assert.Matches(@"RunTurn\([^;]*TurnCapability\.RunScript", "RunTurn(wp, doc, threadId, TurnCapability.RunScript, null, null, grant);");
      foreach (var file in Directory.GetFiles(Path.Combine(Root(), "Dwight"), "*.cs", SearchOption.AllDirectories))
      foreach (var line in File.ReadAllLines(file))
        Assert.False(Regex.IsMatch(line, @"RunTurn\([^;]*TurnCapability\.RunScript"), Path.GetFileName(file) + " starts a run_script turn: " + line.Trim());
    }

    // ---------------------------------------------------------------- helpers

    private static string Between(string s, string from, string to)
    {
      var a = s.IndexOf(from, StringComparison.Ordinal);
      Assert.True(a >= 0, "missing: " + from);
      var b = s.IndexOf(to, a, StringComparison.Ordinal);
      Assert.True(b > a, "missing after it: " + to);
      return s.Substring(a, b - a);
    }

    private static string Root()
    {
      var dir = AppContext.BaseDirectory;
      while (dir != null && !File.Exists(Path.Combine(dir, "Dwight.sln"))) dir = Path.GetDirectoryName(dir);
      Assert.NotNull(dir);
      return dir;
    }

    private static string RepoFile(params string[] parts) => File.ReadAllText(Path.Combine(new[] { Root() }.Concat(parts).ToArray()));
  }
}
