using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Dwight.Agent;
using Dwight.Simplify;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// WAVE 5: what the U19 gate pass found (the private phase notes, 21 October 2026) - U47 (a click that changes
  /// the canvas and says nothing), U45 (the C# source in the component's template), U44 (a build's report header),
  /// U46 (the reflected signatures are the model's, not the user's).
  ///
  /// GH-FREE (ADR-091). What only a Rhino sitting can witness is in the U19 gate's sitting rows 5-8: that the
  /// SolutionEnd handler's real exception is the one reported, that doc.Undo() really takes the build back from inside
  /// the guard, and that Rhino 8's C# component compiles the wrapped class.
  /// </summary>
  public class WaveFiveSeamsTests
  {
    // ---- U47 - the click never goes silent ------------------------------------------------------------------

    [Fact]
    public void AN_EXCEPTION_ANYWHERE_IN_THE_CLICK_REPORTS_IN_DWIGHTS_VOICE_AND_REVERTS_A_STANDING_BUILD()
    {
      var reverted = 0; var reports = new List<string>();
      var ok = SpotClickGuard.Run(() => throw new InvalidOperationException("the verdict composer threw"),
        standing: () => true, revert: () => reverted++, report: reports.Add);

      Assert.False(ok);
      Assert.Equal(1, reverted);
      var line = Assert.Single(reports);
      Assert.StartsWith("Dwight hit a problem while checking the build: the verdict composer threw.", line);
      Assert.Contains("The build was reverted", line);
    }

    [Fact]
    public void POSITIVE_CONTROL_A_CLEAN_CLICK_REPORTS_NOTHING_AND_REVERTS_NOTHING()
    {
      var reverted = 0; var reports = new List<string>(); var ran = false;
      var ok = SpotClickGuard.Run(() => ran = true, () => true, () => reverted++, reports.Add);
      Assert.True(ok);
      Assert.True(ran);
      Assert.Equal(0, reverted);
      Assert.Empty(reports);
    }

    [Fact]
    public void A_BUILD_THE_VERDICT_ALREADY_REVERTED_IS_NEVER_UNDONE_A_SECOND_TIME()
    {
      // Two undos would take back the user's own previous step - worse than the silence this replaces.
      var reverted = 0; var reports = new List<string>();
      SpotClickGuard.Run(() => throw new Exception("report threw"), () => false, () => reverted++, reports.Add);
      Assert.Equal(0, reverted);
      var line = Assert.Single(reports);
      Assert.DoesNotContain("The build was reverted", line);
      Assert.Contains("The canvas is as it was", line);
    }

    [Fact]
    public void A_REVERT_THAT_FAILS_IS_SAID_NOT_CLAIMED()
    {
      var reports = new List<string>();
      SpotClickGuard.Run(() => throw new Exception("x"), () => true, () => throw new Exception("undo failed"), reports.Add);
      var line = Assert.Single(reports);
      Assert.DoesNotContain("The build was reverted", line);
      Assert.Contains("Ctrl-Z", line);
    }

    [Fact]
    public void THE_GUARDS_OWN_EFFECTS_CANNOT_BE_SILENT_OR_ESCAPE()
    {
      // a report that throws must not escape the guard into Grasshopper's swallowing event handler either
      var ok = SpotClickGuard.Run(() => throw new Exception("a"), () => true, () => { }, _ => throw new Exception("report door threw"));
      Assert.False(ok);
    }

    [Fact]
    public void THE_REPORT_LINE_HAS_A_STABLE_SHAPE_EVEN_FOR_AN_EMPTY_MESSAGE()
    {
      Assert.Equal("Dwight hit a problem while checking the build: no further detail. The build was reverted - the canvas is as it was.",
        SpotClickGuard.ReportLine(null, reverted: true));
    }

    private static ScriptBuildFacts FedButEmpty() => new ScriptBuildFacts
    {
      Ref = "calc", Name = "Calc", Language = "python",
      Inputs = new[] { "x", "y", "k" },
      UnwiredInputs = new[] { "x", "y" },   // k is fed from the slider - exactly the gate's row 3(b)
      Outputs = new[] { new ScriptOutputRead { Port = "a", Items = 1, NonNull = 0 } },   // `a = None` holds one NULL item
    };

    /// <summary>The gate's failing row, end to end without Grasshopper: the facts the solve showed -> the verdict -> the
    /// refusal -> the two readings the click composes. Every step must produce text; any throw is the defect.</summary>
    [Fact]
    public void THE_EMPTY_BRANCH_WITH_A_FED_INPUT_PRODUCES_A_REFUSAL_A_VERDICT_AND_BOTH_READINGS()
    {
      var f = FedButEmpty();
      Assert.Equal(ScriptBuildOutcome.Empty, ScriptBuildVerdict.Outcome(f));   // fed, so NOT Unexercised

      var j = ScriptBuildVerdict.Judge(new[] { f }, null);
      Assert.False(j.Stands);
      Assert.Contains("its output a holds no data", j.Refusal);
      Assert.Contains("Nothing is wired to its inputs x, y", j.Refusal);
      Assert.Contains("was reverted - the canvas is as it was", j.Refusal);
      Assert.NotNull(j.UserRefusal);
      Assert.NotNull(j.UserDetail);

      var result = ToolResult.Fail(ToolErrors.VerificationFailed, j.Refusal,
        BuildVerdict.Build(false, new List<int>(new int[2]), 1, 1, null, 2, 0, new JsonArray(),
          note: j.Detail, region: "Spot 1", attemptsLeft: 2, failedLeg: ScriptBuildVerdict.Leg, userMessage: j.UserRefusal));

      Assert.False(result.Ok);
      Assert.Equal("script", result.Detail["failedLeg"].GetValue<string>());
      Assert.Equal("reverted", result.Detail["status"].GetValue<string>());

      // the click's two readings
      var html = SpotAsk.FailedHtml(result);
      var text = SpotAsk.FailedText(result);
      Assert.Contains("Reverted.", html);
      Assert.Contains("holds no data", html);
      Assert.StartsWith("[Attempted by Dwight on the user's approval, and REVERTED - the canvas is as it was]", text);
    }

    [Fact]
    public void POSITIVE_CONTROL_THE_SAME_SCRIPT_WITH_NOTHING_FED_IS_UNEXERCISED_NOT_EMPTY()
    {
      var f = FedButEmpty();
      f.UnwiredInputs = new[] { "x", "y", "k" };
      Assert.Equal(ScriptBuildOutcome.Unexercised, ScriptBuildVerdict.Outcome(f));
      Assert.True(ScriptBuildVerdict.Judge(new[] { f }, null).Stands);
    }

    [Fact]
    public void A_THROW_WHILE_COMPOSING_THE_REPORT_STILL_REPORTS_AND_REVERTS()
    {
      // the callback the spot click hands to the build, with a result whose composer cannot be trusted
      var reports = new List<string>(); var reverted = 0;
      Action<ToolResult> callback = result => SpotClickGuard.Run(
        () => { if (result.Detail["boom"] != null) throw new InvalidOperationException("composer threw"); },
        () => true, () => reverted++, reports.Add);

      callback(ToolResult.Fail(ToolErrors.VerificationFailed, "m", new JsonObject { ["boom"] = true }));

      Assert.Equal(1, reverted);
      Assert.Contains("composer threw", Assert.Single(reports));
    }

    // ---- U44 - a build's header never says set_value ---------------------------------------------------------

    [Fact]
    public void A_REVERTED_BUILDS_REPORT_NEVER_NAMES_SET_VALUE()
    {
      var revert = ToolResult.Fail(ToolErrors.VerificationFailed, "Your script FAILED TO RUN.", new JsonObject());
      var refused = ToolResult.Fail(ToolErrors.PreconditionRefused, "That address is taken.");

      foreach (var r in new[] { revert, refused })
      {
        Assert.DoesNotContain("set_value", SpotAsk.FailedText(r));
        Assert.DoesNotContain("set_value", SpotAsk.FailedHtml(r));
      }
      Assert.DoesNotContain("set_value", SpotAsk.NotAppliedText("x"));

      // two facts, two headers: a revert applied something and put it back; a refusal changed nothing
      Assert.StartsWith("[Attempted by Dwight on the user's approval, and REVERTED", SpotAsk.FailedText(revert));
      Assert.StartsWith("[Not applied - the canvas is unchanged]", SpotAsk.FailedText(refused));
      Assert.Contains("Not applied.", SpotAsk.FailedHtml(refused));
      Assert.DoesNotContain("Not applied.", SpotAsk.FailedHtml(revert));
    }

    [Fact]
    public void THE_OLD_BORROWED_HEADER_IS_STILL_WHAT_SET_VALUE_SAYS_PROVING_THE_TEST_CAN_SEE_IT()
    {
      // positive control (#212): the assertion above would pass vacuously if "set_value" were never in play
      Assert.Contains("set_value", SetValueReport.NotAppliedText("x"));
    }

    // ---- U46 - the reflection is the model's -------------------------------------------------------------------

    private static string BigReflection() =>
      string.Join("\n", Enumerable.Range(0, 200).Select(i => "  public static Brep Member" + i + "(Curve a, double b)")) +
      "\n... and 154 more members";

    private static ScriptBuildFacts CompileFailure() => new ScriptBuildFacts
    {
      Ref = "loft", Name = "Loft", Language = "csharp",
      Errors = new[] { "(3,5): error CS0246: The type or namespace name 'LoftOptions' could not be found" },
      Inputs = new[] { "x", "y" }, UnwiredInputs = new[] { "x", "y" },
      Outputs = new[] { new ScriptOutputRead { Port = "a", Items = 1, NonNull = 0 } },
    };

    [Fact]
    public void THE_USERS_LINE_CARRIES_THE_COMPILERS_WORDS_AND_NO_REFLECTED_SIGNATURES()
    {
      var j = ScriptBuildVerdict.Judge(new[] { CompileFailure() }, errs => BigReflection());

      // the model's refusal keeps the reflection - it is its repair material
      Assert.Contains("static Brep Member0", j.Refusal);
      Assert.Contains("Fix the script and build again; this is not a limit of the tool.", j.Refusal);

      // the user's reading does not
      Assert.Contains("LoftOptions", j.UserRefusal);
      Assert.Contains("DID NOT COMPILE", j.UserRefusal);
      Assert.DoesNotContain("static ", j.UserRefusal);
      Assert.DoesNotContain("154 more members", j.UserRefusal);
      Assert.DoesNotContain("this is not a limit of the tool", j.UserRefusal);
      Assert.True(j.UserRefusal.Length < 1500, "user line is " + j.UserRefusal.Length + " characters");
      Assert.True(j.Refusal.Length > 8 * j.UserRefusal.Length / 2, "control: the model's text is the long one");
    }

    [Fact]
    public void THE_CLICK_SHOWS_THE_USER_READING_AND_THE_TRANSCRIPT_KEEPS_THE_MODELS()
    {
      var j = ScriptBuildVerdict.Judge(new[] { CompileFailure() }, errs => BigReflection());
      var result = ToolResult.Fail(ToolErrors.VerificationFailed, j.Refusal,
        BuildVerdict.Build(false, new List<int>(new int[1]), 0, 0, null, 1, 0, new JsonArray(),
          note: j.Detail, failedLeg: "script", userMessage: j.UserRefusal));

      var html = SpotAsk.FailedHtml(result);
      var text = SpotAsk.FailedText(result);
      Assert.DoesNotContain("static ", html);
      Assert.Contains("static Brep Member0", text);   // the model reads the reflection at its next turn
    }

    [Fact]
    public void A_HUGE_COMPILER_MESSAGE_IS_BOUNDED_ON_THE_USERS_LINE()
    {
      var f = CompileFailure();
      f.Errors = new[] { new string('E', 20000) };
      var j = ScriptBuildVerdict.Judge(new[] { f }, null);
      Assert.True(j.UserRefusal.Length <= ScriptBuildVerdict.MaxUserChars + 600, "length " + j.UserRefusal.Length);
      Assert.Contains("(cut for length)", j.UserRefusal);
      Assert.True(j.Refusal.Length > 20000 / 2);   // the model's copy is whole
    }

    [Fact]
    public void PYTHON_HAS_NO_REFLECTION_AND_THE_TWO_READINGS_AGREE_ON_THE_FACTS()
    {
      var f = CompileFailure(); f.Language = "python"; f.Errors = new[] { "NameError: name 'q' is not defined [2:1]" };
      var j = ScriptBuildVerdict.Judge(new[] { f }, errs => throw new Exception("must not be asked"));
      Assert.Contains("name 'q' is not defined", j.UserRefusal);
      Assert.Contains("name 'q' is not defined", j.Refusal);
    }

    [Fact]
    public void A_REFUSAL_WITHOUT_A_USER_VARIANT_FALLS_BACK_TO_ITS_MESSAGE()
    {
      var r = ToolResult.Fail(ToolErrors.VerificationFailed, "plain message", new JsonObject());
      Assert.Equal("plain message", SpotAsk.UserMessage(r));
      Assert.Contains("plain message", SpotAsk.FailedHtml(r));
    }

    // ---- U45 - the C# source in the component's template ---------------------------------------------------------

    private const string BodyOnly =
      "using System.Text;\n" +
      "private void RunScript(Curve x, Curve y, ref object a)\n" +
      "{\n" +
      "  var list = new List<Curve> { x, y };\n" +
      "  a = list.Count;\n" +
      "}";

    [Fact]
    public void A_BODY_ONLY_RUNSCRIPT_IS_WRAPPED_IN_THE_CLASS_TEMPLATE_WITH_THE_USINGS_UNIONED()
    {
      var t = UsingNormalizer.ToComponentTemplate(BodyOnly);

      Assert.Contains("public class Script_Instance : GH_ScriptInstance", t);
      Assert.Contains("using Rhino.Geometry;", t);                     // the baseline: Curve resolves
      Assert.Contains("using System.Collections.Generic;", t);          // the baseline: List<> resolves
      Assert.Contains("using System.Text;", t);                        // the model's own survives
      Assert.Equal(1, CountOf(t, "using System.Text;"));               // lifted, not duplicated
      Assert.Contains("  private void RunScript(Curve x, Curve y, ref object a)", t);   // the method, inside the class

      // order: every using precedes the class, and the method is inside its braces
      var classAt = t.IndexOf("public class Script_Instance", StringComparison.Ordinal);
      Assert.True(t.LastIndexOf("using ", StringComparison.Ordinal) < classAt);
      Assert.True(t.IndexOf("private void RunScript", StringComparison.Ordinal) > classAt);
      Assert.EndsWith("}", t);
    }

    [Fact]
    public void A_COMPLETE_CLASS_PASSES_THROUGH_BYTE_FOR_BYTE()
    {
      var full = "using System;\n\npublic class Script_Instance : GH_ScriptInstance\n{\n  private void RunScript(object x, ref object a)\n  {\n    a = x;\n  }\n}\n";
      Assert.Equal(full, UsingNormalizer.ToComponentTemplate(full));
    }

    [Fact]
    public void THE_TEMPLATE_IS_IDEMPOTENT()
    {
      var once = UsingNormalizer.ToComponentTemplate(BodyOnly);
      Assert.Equal(once, UsingNormalizer.ToComponentTemplate(once));
    }

    [Fact]
    public void STATEMENTS_ONLY_TAKE_THE_USINGS_UNION_AND_NO_CLASS()
    {
      var t = UsingNormalizer.ToComponentTemplate("a = new List<int>().Count;");
      Assert.Contains("using System.Collections.Generic;", t);
      Assert.DoesNotContain("class Script_Instance", t);
      Assert.Contains("a = new List<int>().Count;", t);
    }

    [Fact]
    public void A_HELPER_METHOD_BESIDE_RUNSCRIPT_GOES_INSIDE_THE_CLASS_TOO()
    {
      var t = UsingNormalizer.ToComponentTemplate(
        "private void RunScript(object x, ref object a) { a = Twice(1); }\nprivate int Twice(int n) { return n * 2; }");
      var close = t.LastIndexOf('}');
      Assert.True(t.IndexOf("private int Twice", StringComparison.Ordinal) < close);
      Assert.True(t.IndexOf("private int Twice", StringComparison.Ordinal) > t.IndexOf("public class", StringComparison.Ordinal));
    }

    // the parse, the card and python

    private static readonly string[] Installed = { "Number Slider", "Python 3 Script", "C# Script" };

    private static BuildSpec ParseOne(string type, string source)
    {
      var args = new JsonObject
      {
        ["components"] = new JsonArray(new JsonObject { ["ref"] = "c", ["type"] = type, ["source"] = source }),
      };
      Assert.True(BuildSpecParser.TryParse(args,
        t => Installed.Contains(Dwight.Components.ComponentMatchCore.BareName(t), StringComparer.OrdinalIgnoreCase),
        t => Installed, id => IdState.Alive,
        t => new PortShape { Inputs = new[] { "x", "y" }, Outputs = new[] { "a" }, InputNames = new[] { "x", "y" }, OutputNames = new[] { "a" } },
        null, out var spec, out var error), error?.Message);
      return spec;
    }

    [Fact]
    public void THE_PARSE_STORES_THE_NORMALISED_CSHARP_AND_THE_CARD_SHOWS_EXACTLY_THAT_TEXT()
    {
      var spec = ParseOne("C# Script", BodyOnly);
      var stored = spec.Components.Single().Source;
      Assert.Contains("public class Script_Instance : GH_ScriptInstance", stored);
      Assert.Contains("using Rhino.Geometry;", stored);

      Assert.True(SpotAsk.Validate(new HandoffSeed
      {
        Place = new JsonObject { ["components"] = new JsonArray(new JsonObject { ["ref"] = "c", ["type"] = "C# Script", ["source"] = BodyOnly }) },
      }, Resolvers(), _ => null, out var ask, out var err), err?.ToJson());

      var html = ConsentCard.SpotHtml("why", ask.Placement, "t1", "t2");
      var shown = System.Net.WebUtility.HtmlEncode(ask.Spec.Components.Single().Source);
      Assert.Equal(stored, ask.Spec.Components.Single().Source);                                   // the record, the card, the build
      Assert.Contains("<pre class=\"consent-source\">" + shown + "</pre>", html);                // THE TEXT SHOWN IS THE TEXT APPLIED
      Assert.Contains("Script_Instance", html);

      // and the record round-trips to the SAME text, so the click builds what the card showed (#153)
      var again = BuildSpecParserRoundTrip(ask);
      Assert.Equal(stored, again);
    }

    [Fact]
    public void PYTHON_IS_LEFT_EXACTLY_AS_WRITTEN()
    {
      var py = "private void RunScript(Curve x):\n  pass\na = x * y";
      Assert.Equal(py, ParseOne("Python 3 Script", py).Components.Single().Source);
    }

    [Fact]
    public void A_COMPLETE_CLASS_IN_A_SPEC_IS_NOT_REWRITTEN()
    {
      var full = "public class Script_Instance : GH_ScriptInstance\n{\n  private void RunScript(object x, ref object a)\n  {\n    a = x;\n  }\n}";
      Assert.Equal(full, ParseOne("C# Script", full).Components.Single().Source);
    }

    // ---- helpers

    private static int CountOf(string haystack, string needle)
    {
      var n = 0; var i = 0;
      while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
      return n;
    }

    private static string BuildSpecParserRoundTrip(SpotAsk ask)
    {
      Assert.True(BuildSpecParser.TryParse(ask.Record,
        t => Installed.Contains(Dwight.Components.ComponentMatchCore.BareName(t), StringComparer.OrdinalIgnoreCase),
        t => Installed, id => IdState.Alive,
        t => new PortShape { Inputs = new[] { "x", "y" }, Outputs = new[] { "a" }, InputNames = new[] { "x", "y" }, OutputNames = new[] { "a" } },
        null, out var spec, out var error), error?.Message);
      return spec.Components.Single().Source;
    }

    private static SpecResolvers Resolvers() => new SpecResolvers
    {
      TypeExists = t => Installed.Contains(Dwight.Components.ComponentMatchCore.BareName(t), StringComparer.OrdinalIgnoreCase),
      Nearest = t => Installed, IdState = id => IdState.Alive,
      PortsOfType = t => new PortShape { Inputs = new[] { "x", "y" }, Outputs = new[] { "a" }, InputNames = new[] { "x", "y" }, OutputNames = new[] { "a" } },
      PortsOfId = null,
      CanonicalType = t => Installed.FirstOrDefault(i => string.Equals(i, Dwight.Components.ComponentMatchCore.BareName(t), StringComparison.OrdinalIgnoreCase)) ?? t,
    };
  }
}
