using System;
using System.Collections.Generic;
using System.Linq;
using Dwight.Agent;
using Dwight.Simplify;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// U20's two sitting defects, held where they can be tested (3c and 3h, 2026-09-19).
  ///
  ///   3h. A C# edit that did not compile was told "FAILED TO RUN. The interpreter said:". The refusal chose its
  ///       header from the edit's display LABEL - a nickname or a friendly word, never a type name - so every C#
  ///       failure read as Python. It lived in the GH-coupled panel, where nothing could test it. The language is
  ///       now a field derived from the component's TYPE, and the refusal body is composed here.
  ///
  ///   3c. A refused edit reached the user only as the model's paraphrase. The user-facing sentence existed and
  ///       could not be reached. RefusedLine is the sentence the panel shows when a turn ends with no card.
  ///
  /// Every chain here starts from EditScriptAsk.Validate on a LiveScript with a real type name, because the
  /// defect was in the hand-off between the ask and the refusal, and a test that starts at the refusal with a
  /// language already filled in would have passed over it.
  /// </summary>
  public class ScriptEditRefusalTests
  {
    private const string CSharp = "RhinoCodePluginGH.Components.CSharpComponent";
    private const string Python = "RhinoCodePluginGH.Components.Python3Component";
    private const string IronPython = "RhinoCodePluginGH.Components.IronPython2Component";
    private const string LegacyCSharp = "ScriptComponents.Component_CSNET_Script";
    private const string LegacyVb = "ScriptComponents.Component_VBNET_Script";
    private const string UnknownScript = "RhinoCodePluginGH.Components.SomeFutureComponent";
    private const string Before = "a = x\n";
    private const int Id = 7;

    private static readonly IReadOnlyList<string> CompileError =
      new[] { "error CS0246: The type or namespace name 'LoftOptions' could not be found" };

    /// <summary>The edit, as the ASK produces it - from a live script with a real type name.</summary>
    private static ScriptEditChange Change(string type, string nick = null)
    {
      var live = new LiveScript
      {
        Type = type, Nick = nick, Source = Before, SourceReadable = true,
        Inputs = new[] { "x" }, Outputs = new[] { "a" },
      };
      var seed = new HandoffSeed
      {
        Target = Id, Claim = "changes", Source = "a = x * 2\n",
        AddInputs = new List<string>(), AddOutputs = new List<string>(),
      };
      Assert.True(EditScriptAsk.Validate(seed, _ => IdState.Alive, _ => live, out var change, out var error),
        error?.ToJson());
      return change;
    }

    private static ScriptTrialFacts Failed(IReadOnlyList<string> errors = null) =>
      new ScriptTrialFacts { Ran = true, Errors = errors ?? CompileError, OutputsCompared = 1, OutputsWithData = 1 };

    private static string Body(ScriptEditChange change, ScriptEditOutcome outcome = ScriptEditOutcome.DidNotRun,
      ScriptTrialFacts facts = null, int attempt = 1, Func<IReadOnlyList<string>, string> api = null) =>
      ScriptEditVerdict.RefusalBody(change, outcome, facts ?? Failed(), attempt, 3, api);

    // ---- 3h: the language is the TYPE's, never the label's ---------------------------------------------

    /// <summary>THE DEFECT, end to end: a C# component whose edit does not compile is told the compiler's
    /// words. It read "FAILED TO RUN. The interpreter said:" until this was fixed.</summary>
    [Fact]
    public void A_csharp_edit_that_does_not_compile_is_given_the_compilers_words()
    {
      var body = Body(Change(CSharp));

      Assert.Contains(CompileRepair.CompiledHeader, body);
      Assert.DoesNotContain(CompileRepair.InterpretedHeader, body);
      Assert.Contains("LoftOptions", body);
    }

    /// <summary>
    /// The other direction - AND IT PROVES NOTHING ON ITS OWN. Against the original label bug this test stayed
    /// GREEN: a Python component's label ("Python 3 Script") is not a type name either, so the language rule read
    /// it as "script", which fell to Python - the right header by accident. That is exactly how the bug shipped:
    /// Python edits looked fine, so nothing about Python could show it. The C# test above, and the nickname test
    /// below, are the ones that can fail; this one is here so the Python header stays pinned once they pass.
    /// </summary>
    [Fact]
    public void A_python_edit_that_does_not_run_is_given_the_interpreters_words()
    {
      var body = Body(Change(Python), facts: Failed(new[] { "NameError: name 'y' is not defined" }));

      Assert.Contains(CompileRepair.InterpretedHeader, body);
      Assert.DoesNotContain(CompileRepair.CompiledHeader, body);
    }

    [Theory]
    [InlineData(CSharp, "csharp", true)]
    [InlineData(LegacyCSharp, "csharp", true)]
    [InlineData(LegacyVb, "vb", true)]
    [InlineData(Python, "python", false)]
    [InlineData(IronPython, "ironpython", false)]
    public void The_edit_carries_the_language_of_the_components_type(string type, string language, bool compiled)
    {
      var change = Change(type);

      Assert.Equal(language, change.Language);
      Assert.Equal(compiled, change.Compiled);
    }

    /// <summary>THE LABEL NEVER DECIDES, in both directions - which is the property the defect broke. A nickname
    /// is the user's text and can say anything, including the name of the other language.</summary>
    [Fact]
    public void A_nickname_naming_the_other_language_changes_nothing_in_either_direction()
    {
      var pythonCalledCSharp = Change(Python, nick: "C# Script");
      var csharpCalledPython = Change(CSharp, nick: "python");

      Assert.Equal("python", pythonCalledCSharp.Language);
      Assert.Contains(CompileRepair.InterpretedHeader, Body(pythonCalledCSharp));
      Assert.Equal("csharp", csharpCalledPython.Language);
      Assert.Contains(CompileRepair.CompiledHeader, Body(csharpCalledPython));
    }

    /// <summary>ONE DEFINITION: the friendly word a nickname-less component is described by comes from the same
    /// field the refusal reads, so the two cannot name different languages.</summary>
    [Theory]
    [InlineData(CSharp, "C# Script")]
    [InlineData(Python, "Python 3 Script")]
    public void The_description_and_the_language_come_from_one_field(string type, string described)
    {
      Assert.Equal(described, Change(type).Label);
    }

    /// <summary>
    /// A LANGUAGE THAT CANNOT BE DETERMINED IS NOT GUESSED (#208 - fail open on knowledge, never on identity). Naming
    /// a compiler or an interpreter there would state a guess as a fact, and the model would repair against the
    /// wrong language's rules. The header says only what is known.
    /// </summary>
    [Fact]
    public void An_undetermined_language_gets_a_header_that_names_neither()
    {
      var change = Change(UnknownScript);
      var body = Body(change);

      Assert.Null(change.Compiled);
      Assert.Contains(CompileRepair.UnknownLanguageHeader, body);
      Assert.DoesNotContain("compiler", body, StringComparison.OrdinalIgnoreCase);
      Assert.DoesNotContain("interpreter", body, StringComparison.OrdinalIgnoreCase);
    }

    // ---- lane A's three, adopted (the architect's routing, #248) -----------------------------------------
    //
    // Written by lane A against its own parked fix and seen to fail against the original label bug; they test THIS
    // code now, so they live here. Their CASES are kept exactly. Only the call changed: lane A's
    // EditScriptLanguage.IsCSharp(change, typeFullName) becomes what now derives the language - the ask, fed a live
    // script with that Type - and each asserts on change.Language. A label in lane A's cases is a NICKNAME here,
    // because a nickname is how user text reaches the label.

    private const string CSharpType = CSharp;
    private const string PythonType = Python;

    /// <summary>
    /// A C# target's compile failure must be diagnosed as a COMPILER'S. The refusal used to decide from the
    /// change's Label - a display name LanguageOf cannot read - so every C# edit came back labelled as Python and
    /// the model was told "FAILED TO RUN. The interpreter said:" about a compile error. A wrong diagnosis is the
    /// #92/#94 class: the repair loop only works while its diagnostic points the right way. (Lane A.)
    /// </summary>
    [Fact]
    public void A_csharp_target_is_diagnosed_as_csharp_from_its_type()
    {
      var csharp = Change(CSharpType);
      Assert.Equal("C# Script", csharp.Label);   // the label lane A's case used, reached the way a user reaches it
      Assert.Equal("csharp", csharp.Language);
      Assert.NotEqual("csharp", Change(PythonType).Language);
    }

    /// <summary>
    /// THE CASE THAT MAKES THE ORIGINAL MISTAKE VISIBLE. A Python component whose label happens to read like C#
    /// must still be diagnosed as Python, and a C# component with a nickname that names no language must still be
    /// diagnosed as C#. Either would come out wrong from a decision made on the label - which is the point: a
    /// display name is whatever the user typed, and it cannot identify a language. (Lane A.)
    /// </summary>
    [Theory]
    [InlineData("C# Script", PythonType, false)]
    [InlineData("\"Offset curves\"", CSharpType, true)]
    [InlineData("my helper", CSharpType, true)]
    public void The_label_never_decides_the_language(string label, string type, bool csharp)
    {
      Assert.Equal(csharp, Change(type, nick: label).Language == "csharp");
    }

    /// <summary>
    /// A target that is gone or unreadable reports no type; it must not be guessed into C#. (Lane A.) Adjusted to
    /// this unit's decision on the unknown case: it is not guessed into PYTHON either - its language is undetermined,
    /// and the refusal then names neither a compiler nor an interpreter.
    /// </summary>
    [Fact]
    public void An_unreadable_target_is_not_guessed_to_be_csharp()
    {
      var unreadable = new LiveScript { Type = null, Nick = "C# Script" };
      var change = new ScriptEditChange { Label = "C# Script", Language = unreadable.Language };

      Assert.NotEqual("csharp", change.Language);
      Assert.Null(change.Compiled);
    }

    // ---- the rest of the body ---------------------------------------------------------------------------

    /// <summary>Only a failure to RUN carries a repair message. A false preserves carries the two ways forward
    /// instead - a compiler header there would describe a failure that did not happen.</summary>
    [Fact]
    public void Only_a_failure_to_run_carries_the_repair_message()
    {
      var body = Body(Change(CSharp), ScriptEditOutcome.NotPreserved,
        new ScriptTrialFacts { Ran = true, OutputsCompared = 1, OutputsWithData = 1, Mismatched = new[] { "a" } });

      Assert.DoesNotContain(CompileRepair.CompiledHeader, body);
      Assert.DoesNotContain(CompileRepair.InterpretedHeader, body);
      Assert.Contains("\"changes\"", body);
    }

    [Fact]
    public void The_reflected_signatures_are_given_the_errors_and_appended()
    {
      IReadOnlyList<string> asked = null;
      var body = Body(Change(CSharp), api: errs => { asked = errs; return "LoftOptions: NO SUCH TYPE in RhinoCommon."; });

      Assert.Same(CompileError, asked);
      Assert.Contains("LoftOptions: NO SUCH TYPE in RhinoCommon.", body);
    }

    /// <summary>Reflection is help, never a gate: a reflection that throws still leaves the compiler's own words.</summary>
    [Fact]
    public void A_reflection_that_throws_still_leaves_the_compilers_words()
    {
      var body = Body(Change(CSharp), api: _ => throw new InvalidOperationException("no RhinoCommon here"));

      Assert.Contains(CompileRepair.CompiledHeader, body);
      Assert.DoesNotContain("no RhinoCommon here", body);
    }

    [Fact]
    public void The_body_says_where_the_model_stands_on_its_attempts()
    {
      Assert.Contains("attempt 1 of 3", Body(Change(CSharp), attempt: 1));
      Assert.Contains("That was your last of 3 attempts", Body(Change(CSharp), attempt: 3));
    }

    /// <summary>
    /// THE MEASURED CASE, BYTE FOR BYTE. to-script's stated numbers (87.5% compile, 39.6% verify) were measured with
    /// this text, and the architect would not change the instrument behind a published number - so with signatures
    /// present it must be EXACTLY what it was, five-error cap and all. Any change to this string fails here by name.
    /// </summary>
    [Fact]
    public void With_signatures_the_repair_text_is_byte_identical_to_the_measured_one()
    {
      var errors = Enumerable.Range(1, 7).Select(i => "e" + i).ToArray();
      var nl = Environment.NewLine;

      var text = CompileRepair.Text(errors, true, "Loft: NO SUCH TYPE in RhinoCommon.");

      Assert.Equal(
        CompileRepair.CompiledHeader + nl + "  e1" + nl + "  e2" + nl + "  e3" + nl + "  e4" + nl + "  e5" + nl + nl +
        "Here are the REAL signatures, reflected from the RhinoCommon assembly loaded in this Rhino" + nl +
        "(this is ground truth - if a type you used is missing below, it DOES NOT EXIST):" + nl +
        "Loft: NO SUCH TYPE in RhinoCommon." + nl + nl +
        "Fix the error and return the corrected script. Do not change the interface - the inputs and" + nl +
        "outputs, in order, are fixed. Do not invent an API: use only what the signatures above show." + nl,
        text);
    }

    /// <summary>
    /// WITHOUT SIGNATURES, THE TEXT POINTS AT NOTHING (lane C's finding, ruled in). It used to close "use only what the
    /// signatures above show" whether or not any were shown - a claim about the message's own contents that was false
    /// whenever no type was reflected. The clause is dropped in exactly that case, and nowhere else.
    /// </summary>
    [Fact]
    public void Without_signatures_the_repair_text_does_not_point_at_signatures()
    {
      var nl = Environment.NewLine;
      var text = CompileRepair.Text(new[] { "e1" }, true, null);

      Assert.DoesNotContain("signatures", text);
      Assert.Equal(
        CompileRepair.CompiledHeader + nl + "  e1" + nl + nl +
        "Fix the error and return the corrected script. Do not change the interface - the inputs and" + nl +
        "outputs, in order, are fixed. Do not invent an API." + nl,
        text);
    }

    // ---- 3c: the sentence a user reads when the turn ends with the edit refused -------------------------

    // The outcome is internal, so a public theory takes its NAME.
    private static ScriptEditOutcome Outcome(string name) => (ScriptEditOutcome)Enum.Parse(typeof(ScriptEditOutcome), name);

    [Theory]
    [InlineData("Preserved")]
    [InlineData("NothingToCompare")]
    [InlineData("RanClean")]
    public void An_outcome_that_is_offered_has_no_turn_end_line_because_a_card_carries_it(string outcome)
    {
      Assert.Null(ScriptEditVerdict.RefusedLine("\"Area\"", Outcome(outcome), Failed()));
    }

    /// <summary>With no card beside it, the line must say WHICH component - and end on the reassurance.</summary>
    [Theory]
    [InlineData("NotPreserved")]
    [InlineData("DidNotRun")]
    [InlineData("CouldNotCheck")]
    public void A_refused_outcome_names_the_component_and_ends_on_the_reassurance(string outcome)
    {
      var line = ScriptEditVerdict.RefusedLine("\"Area\"", Outcome(outcome),
        new ScriptTrialFacts { Ran = true, Mismatched = new[] { "area" }, Errors = CompileError });

      Assert.StartsWith("Not applied.", line);
      Assert.Contains("\"Area\"", line);
      Assert.EndsWith("Nothing on your canvas was touched.", line);
    }

    [Fact]
    public void A_false_preserves_line_names_the_ports_that_differ()
    {
      var line = ScriptEditVerdict.RefusedLine("\"Area\"", ScriptEditOutcome.NotPreserved,
        new ScriptTrialFacts { Ran = true, OutputsCompared = 2, OutputsWithData = 2, Mismatched = new[] { "area", "centroid" } });

      Assert.Contains("said it would keep its outputs the same", line);
      Assert.Contains("area, centroid", line);
    }

    /// <summary>It is shown with no card around it, so it may point at nothing around it.</summary>
    [Fact]
    public void The_turn_end_line_points_at_nothing_around_it()
    {
      var positional = new[] { "above", "below", "this component", "shown here", "as shown", "the diff" };
      foreach (var outcome in new[] { ScriptEditOutcome.NotPreserved, ScriptEditOutcome.DidNotRun, ScriptEditOutcome.CouldNotCheck })
      {
        var line = ScriptEditVerdict.RefusedLine("\"Area\"", outcome, Failed());
        foreach (var word in positional)
          Assert.DoesNotContain(word, line, StringComparison.OrdinalIgnoreCase);
      }
    }

    /// <summary>The pairing the panel uses: the refusal renderer, whose bold lead-in replaces the sentence's own
    /// "Not applied." rather than doubling it.</summary>
    [Fact]
    public void The_turn_end_line_renders_through_the_refusal_renderer_without_doubling()
    {
      var html = ScriptEditReport.NotAppliedHtml(
        ScriptEditVerdict.RefusedLine("\"Area\"", ScriptEditOutcome.DidNotRun, Failed()));

      Assert.StartsWith("<p><strong>Not applied.</strong> Dwight&#39;s new version of &quot;Area&quot;", html);
      Assert.Equal(1, html.Split(new[] { "Not applied" }, StringSplitOptions.None).Length - 1);
    }

    /// <summary>A report for a refused outcome is never built - but if one ever is, it says what the turn-end line
    /// says, because there is one wording for a refusal.</summary>
    [Fact]
    public void A_report_for_a_refused_outcome_is_the_turn_end_wording()
    {
      var f = Failed();
      Assert.Equal(ScriptEditVerdict.RefusedLine(null, ScriptEditOutcome.DidNotRun, f),
                   ScriptEditVerdict.ReportLine(ScriptEditOutcome.DidNotRun, f, null));
    }
  }
}
