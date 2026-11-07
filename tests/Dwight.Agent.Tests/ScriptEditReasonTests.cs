using System;
using System.Collections.Generic;
using Dwight.Agent;
using Dwight.Simplify;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// Phase-9 U37 (#930) - THREE SENTENCES THAT WERE CLAIMS ABOUT OUR OWN BEHAVIOUR, AND WERE FALSE (#315).
  ///
  ///   1. A CouldNotCheck dropped the reason the trial recorded. Three rows of the gate refused this way, and the
  ///      model invented a cause each time. The reason is now carried, verbatim, into the model's fact and the
  ///      user's turn-end line - one reason, both channels (#624).
  ///   2. The edit report named no component, so the model matched it to the wrong one and stopped trusting its own
  ///      thread. It now names its subject by nickname and id, as set_value's report does.
  ///   3. A Python NameError was answered with RhinoCommon reflection and "use only what the signatures above show"
  ///      with nothing above. The reflection is C#-only.
  /// </summary>
  public class ScriptEditReasonTests
  {
    private const string CSharp = "RhinoCodePluginGH.Components.CSharpComponent";
    private const string Python = "RhinoCodePluginGH.Components.Python3Component";

    /// <summary>The seven NotRun reasons the trial writes, as it writes them (ScriptEditTrial.NotRun).</summary>
    public static IEnumerable<object[]> TrialReasons()
    {
      yield return new object[] { "the document is no longer available" };
      yield return new object[] { "Dwight was not holding the session, so it did not put a test copy on the canvas - another operation could have read it mid-check" };
      yield return new object[] { "the Grasshopper solver is disabled" };
      yield return new object[] { "the component is no longer on the canvas" };
      yield return new object[] { "Dwight could not create a copy of that script component to test the change against." };
      yield return new object[] { "Dwight could not add the input \"offset\" to the script." };
      yield return new object[] { "the edited script did not finish solving in time" };
    }

    private static ScriptTrialFacts NotRun(string reason) =>
      new ScriptTrialFacts { Ran = false, Errors = new[] { reason } };

    private static ScriptEditChange Change(string type, string nick = null, int id = 11)
    {
      var live = new LiveScript
      {
        Type = type, Nick = nick, Source = "a = x\n", SourceReadable = true,
        Inputs = new[] { "x" }, Outputs = new[] { "a" },
      };
      var seed = new HandoffSeed
      {
        Target = id, Claim = "changes", Source = "a = x * 2\n",
        AddInputs = new List<string>(), AddOutputs = new List<string>(),
      };
      Assert.True(EditScriptAsk.Validate(seed, _ => IdState.Alive, _ => live, out var change, out var error), error?.ToJson());
      return change;
    }

    // ---- 1. a CouldNotCheck says why -------------------------------------------------------------------

    /// <summary>THE DEFECT. The model's fact carries the trial's own reason, verbatim - for every path the trial
    /// can refuse on.</summary>
    [Theory]
    [MemberData(nameof(TrialReasons))]
    public void The_model_is_told_why_the_check_did_not_run(string reason)
    {
      var fact = ScriptEditVerdict.ModelFact(ScriptEditOutcome.CouldNotCheck, NotRun(reason));

      Assert.StartsWith("Your last edit was NOT applied, because Dwight could not check it: ", fact);
      Assert.Contains(reason.TrimEnd('.'), fact);
      Assert.EndsWith("The canvas is unchanged.", fact);
    }

    /// <summary>And so is the user, in the turn-end line - the same reason, so the two channels cannot tell two
    /// stories about one refusal (#624).</summary>
    [Theory]
    [MemberData(nameof(TrialReasons))]
    public void The_user_is_told_the_same_reason_at_turn_end(string reason)
    {
      var line = ScriptEditVerdict.RefusedLine("\"Area\"", ScriptEditOutcome.CouldNotCheck, NotRun(reason));

      Assert.StartsWith("Not applied. Dwight could not check its edit to \"Area\" (", line);
      Assert.Contains(reason.TrimEnd('.'), line);
      Assert.EndsWith("Nothing on your canvas was touched.", line);
    }

    /// <summary>The refusal the model receives mid-loop is the fact plus its standing, so the reason reaches the
    /// model on the turn it happened, not only afterwards.</summary>
    [Fact]
    public void The_refusal_body_carries_the_reason_too()
    {
      var body = ScriptEditVerdict.RefusalBody(Change(Python), ScriptEditOutcome.CouldNotCheck,
        NotRun("the Grasshopper solver is disabled"), 1, 3, null);

      Assert.Contains("the Grasshopper solver is disabled", body);
      Assert.Contains("attempt 1 of 3", body);
    }

    /// <summary>NO REASON IS SAID TO BE NO REASON. Facts can be null (the panel's own timeout path records the
    /// target as unchecked with none) - then the sentence says the check recorded no reason, rather than falling
    /// silent, because the silence is what the model fills in itself.</summary>
    [Fact]
    public void No_recorded_reason_is_stated_as_such_not_omitted()
    {
      var fact = ScriptEditVerdict.ModelFact(ScriptEditOutcome.CouldNotCheck, null);
      Assert.Contains("recorded no reason", fact);
      Assert.Equal("Your last edit was NOT applied, because Dwight could not check it, and its check recorded no reason. " +
                   "The canvas is unchanged.", fact);

      var line = ScriptEditVerdict.RefusedLine("\"Area\"", ScriptEditOutcome.CouldNotCheck, null);
      Assert.Equal("Not applied. Dwight could not check its edit to \"Area\", so it did not offer it. " +
                   "Nothing on your canvas was touched.", line);
    }

    /// <summary>A reason's own full stop is not doubled, and several reasons are joined.</summary>
    [Fact]
    public void Reasons_are_joined_and_their_own_stop_is_not_doubled()
    {
      var facts = new ScriptTrialFacts { Ran = false, Errors = new[] { "first thing failed.", "second thing failed." } };
      var fact = ScriptEditVerdict.ModelFact(ScriptEditOutcome.CouldNotCheck, facts);
      Assert.Contains(": first thing failed; second thing failed. The canvas is unchanged.", fact);
      Assert.DoesNotContain("..", fact);
    }

    // ---- 2. the report names its subject ---------------------------------------------------------------

    [Fact]
    public void The_subject_is_the_nickname_and_the_id()
    {
      Assert.Equal("\"F1 area\" (11)", ScriptEditVerdict.Subject(Change(Python, nick: "F1 area", id: 11)));
      Assert.Equal("C# Script (12)", ScriptEditVerdict.Subject(Change(CSharp, id: 12)));   // no nickname: the type word
      Assert.Null(ScriptEditVerdict.Subject(null));
    }

    /// <summary>THE DEFECT: "Dwight verified the outputs are unchanged (1 output compared)" named nothing. Every
    /// offerable outcome's report now carries the subject.</summary>
    [Theory]
    [InlineData("Preserved")]
    [InlineData("RanClean")]
    [InlineData("NothingToCompare")]
    public void The_report_names_the_component_by_nickname_and_id(string outcomeName)
    {
      var outcome = (ScriptEditOutcome)Enum.Parse(typeof(ScriptEditOutcome), outcomeName);
      var facts = new ScriptTrialFacts { Ran = true, OutputsCompared = 1, OutputsWithData = 1 };
      var subject = ScriptEditVerdict.Subject(Change(Python, nick: "F1 area", id: 11));

      var report = ScriptEditVerdict.ReportLine(outcome, facts, subject);

      Assert.StartsWith("Applied. ", report);
      Assert.Contains("\"F1 area\" (11)", report);
    }

    /// <summary>The exact sentence for the gate's row 3b, named.</summary>
    [Fact]
    public void A_preserved_report_reads_as_the_gate_expects()
    {
      var facts = new ScriptTrialFacts { Ran = true, OutputsCompared = 1, OutputsWithData = 1 };
      var report = ScriptEditVerdict.ReportLine(ScriptEditOutcome.Preserved, facts, "\"F1 area\" (11)");
      Assert.Equal("Applied. Dwight verified the outputs of \"F1 area\" (11) are unchanged (1 output compared).", report);
    }

    /// <summary>The renderer strips exactly the "Applied. " lead the sentence begins with, so the subject clause
    /// survives into the HTML and the lead is not doubled.</summary>
    [Fact]
    public void The_named_report_renders_without_doubling_the_lead()
    {
      var facts = new ScriptTrialFacts { Ran = true };
      var html = ScriptEditReport.AppliedHtml(ScriptEditVerdict.ReportLine(ScriptEditOutcome.RanClean, facts, "\"F2\" (12)"));
      Assert.StartsWith("<p><strong>Applied.</strong> Dwight replaced the source of &quot;F2&quot; (12). ", html);
      Assert.Equal(1, html.Split(new[] { "Applied" }, StringSplitOptions.None).Length - 1);
    }

    /// <summary>A null subject - the caller with none to give - leaves the sentences exactly as they read before U37. The
    /// two-argument overload that rendered this form for the panel is gone (wave-2 wiring): the panel now passes
    /// <c>ScriptEditVerdict.Subject(editScript)</c>, and this pin is on the three-argument form with a null.</summary>
    [Fact]
    public void Without_a_subject_the_report_stands_as_before()
    {
      var facts = new ScriptTrialFacts { Ran = true, OutputsCompared = 1, OutputsWithData = 1 };
      Assert.Equal("Applied. Dwight verified the outputs are unchanged (1 output compared).",
        ScriptEditVerdict.ReportLine(ScriptEditOutcome.Preserved, facts, null));
      Assert.Equal("Applied. " + ScriptEditVerdict.ClaimClause(ScriptEditClaim.Changes) + " Dwight checked the definition still solves.",
        ScriptEditVerdict.ReportLine(ScriptEditOutcome.RanClean, facts, null));
    }

    // ---- 3. the reflection is C#-only ------------------------------------------------------------------

    /// <summary>THE DEFECT, seen live in row 3h: a Python NameError answered with RhinoCommon reflection and a clause
    /// pointing at signatures that were not there. The reflection is never asked for on an interpreted language.</summary>
    [Fact]
    public void A_python_failure_gets_no_reflection_and_no_signatures_clause()
    {
      var asked = false;
      var facts = new ScriptTrialFacts { Ran = true, Errors = new[] { "Error running script: name 'FooBarWidget' is not defined [6:1]" } };

      var body = ScriptEditVerdict.RefusalBody(Change(Python), ScriptEditOutcome.DidNotRun, facts, 1, 3,
        errs => { asked = true; return "FooBarWidget: NO SUCH TYPE in RhinoCommon. You invented it - use a type that exists."; });

      Assert.False(asked);
      Assert.DoesNotContain("RhinoCommon", body);
      Assert.DoesNotContain("signatures", body);
      Assert.Contains(CompileRepair.InterpretedHeader, body);
      Assert.Contains("name 'FooBarWidget' is not defined", body);
    }

    /// <summary>The positive control: a C# compile failure still gets the reflection - the machinery behind the
    /// measured compile-rate is untouched.</summary>
    [Fact]
    public void A_csharp_failure_still_gets_the_reflection()
    {
      var asked = false;
      var facts = new ScriptTrialFacts { Ran = true, Errors = new[] { "error CS0246: The type or namespace name 'FooBarWidget' could not be found" } };

      var body = ScriptEditVerdict.RefusalBody(Change(CSharp), ScriptEditOutcome.DidNotRun, facts, 1, 3,
        errs => { asked = true; return "FooBarWidget: NO SUCH TYPE in RhinoCommon."; });

      Assert.True(asked);
      Assert.Contains("FooBarWidget: NO SUCH TYPE in RhinoCommon.", body);
      Assert.Contains("use only what the signatures above show", body);
    }

    /// <summary>A language that could not be determined is not guessed to be C# for the reflection's sake (#208).</summary>
    [Fact]
    public void An_undetermined_language_gets_no_reflection_either()
    {
      var asked = false;
      var change = new ScriptEditChange { Label = "script component", Language = "script" };
      var facts = new ScriptTrialFacts { Ran = true, Errors = new[] { "something went wrong" } };

      var body = ScriptEditVerdict.RefusalBody(change, ScriptEditOutcome.DidNotRun, facts, 1, 3, errs => { asked = true; return "x"; });

      Assert.False(asked);
      Assert.Contains(CompileRepair.UnknownLanguageHeader, body);
    }
  }
}
