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
  /// U19 (#898, #902): DWIGHT WRITES A SCRIPT. A script is one more component in the spec, not a new kind - its source is
  /// parsed by the one parser, shown COMPLETE on the placement card, set inside the build, and judged by a three-state
  /// emptiness oracle in the verdict.
  ///
  /// EVERYTHING HERE IS GH-FREE (ADR-091). What only a Rhino sitting can witness is checked by hand in Rhino,
  /// outside this suite: that the shim really sets the source on a freshly added Rhino 8 script component,
  /// that the live component is born with the ports the parse assumes, that a C# compile error really surfaces as a
  /// runtime error message after the verify solve, and that one Ctrl-Z removes the whole build.
  ///
  /// POSITIVE CONTROLS (#212) are named in each section: a refusal test has a twin that passes the same input shape on
  /// the type that is allowed, and the oracle's three states are asserted DIFFERENT from each other on fixtures that differ
  /// in exactly one fact - a classifier that answered the same for everything would pass every single-state test.
  /// </summary>
  public class BuildScriptTests
  {
    private const string PySource = "area = x * y\na = area\n";

    private static readonly string[] Installed = { "Number Slider", "Panel", "Python 3 Script", "C# Script" };
    private static bool TypeExists(string t) => Installed.Contains(Dwight.Components.ComponentMatchCore.BareName(t), StringComparer.OrdinalIgnoreCase);
    private static IReadOnlyList<string> Nearest(string t) => Installed;
    private static IdState Alive(int id) => IdState.Alive;

    private static PortShape Ports(string type)
    {
      var bare = Dwight.Components.ComponentMatchCore.BareName(type);
      if (bare.IndexOf("Script", StringComparison.OrdinalIgnoreCase) >= 0)
        return new PortShape { Inputs = new[] { "x", "y" }, Outputs = new[] { "a" }, InputNames = new[] { "x", "y" }, OutputNames = new[] { "a" } };
      if (bare == "Number Slider") return new PortShape { Inputs = new string[0], Outputs = new string[0] };
      return null;
    }

    private static JsonObject Comp(string r, string type, Action<JsonObject> more = null)
    {
      var o = new JsonObject { ["ref"] = r, ["type"] = type };
      more?.Invoke(o);
      return o;
    }

    private static JsonObject Spec(JsonArray wires, params JsonObject[] components)
    {
      var o = new JsonObject { ["components"] = new JsonArray(components.Cast<JsonNode>().ToArray()) };
      if (wires != null) o["wires"] = wires;
      return o;
    }

    private static bool Parse(JsonObject args, out BuildSpec spec, out ToolResult error) =>
      BuildSpecParser.TryParse(args, TypeExists, Nearest, Alive, Ports, null, out spec, out error);

    private static JsonObject Wire(string fromRef, string toRef, string toParam) => new JsonObject
    {
      ["from"] = new JsonObject { ["ref"] = fromRef },
      ["to"] = new JsonObject { ["ref"] = toRef, ["param"] = toParam },
    };

    // ---- the parse ---------------------------------------------------------------------------------------------

    [Fact]
    public void A_PYTHON_SCRIPT_CARRIES_ITS_SOURCE_AND_ITS_LANGUAGE_COMES_FROM_THE_TYPE()
    {
      Assert.True(Parse(Spec(null, Comp("calc", "Python 3 Script", o => o["source"] = PySource)), out var spec, out var e), e?.Message);
      var c = spec.Components.Single();
      Assert.True(c.CarriesSource);
      Assert.Equal("area = x * y\na = area", c.Source);   // normalised once: the same bytes the card shows and the component takes
      Assert.Equal(BuildScriptTypes.Python, c.Language);
      Assert.False(BuildScriptTypes.Compiled(c.Language));
    }

    [Theory]
    [InlineData("C# Script")]
    [InlineData("c# script")]
    [InlineData("Maths > Script > C# Script")]
    public void A_CSHARP_SCRIPT_IS_RECOGNISED_ON_THE_CANONICAL_NAME_however_it_is_spelled(string type)
    {
      Assert.True(Parse(Spec(null, Comp("calc", type, o => o["source"] = "private void RunScript() { }")), out var spec, out var e), e?.Message);
      Assert.Equal(BuildScriptTypes.CSharp, spec.Components.Single().Language);
      Assert.True(BuildScriptTypes.Compiled(spec.Components.Single().Language));
    }

    /// <summary>The refusal AND its positive control: the same JSON on a type that takes a source passes, so the
    /// refusal is about the type and not about the shape of the field.</summary>
    [Fact]
    public void A_SOURCE_ON_A_TYPE_THAT_IS_NOT_A_SCRIPT_IS_REFUSED_BY_NAME()
    {
      Action<JsonObject> src = o => o["source"] = PySource;

      Assert.True(Parse(Spec(null, Comp("c", "Python 3 Script", src)), out _, out _));                  // control: allowed

      Assert.False(Parse(Spec(null, Comp("s", "Number Slider", src)), out _, out var e));
      Assert.Equal(ToolErrors.InvalidArgs, e.Code);
      Assert.Contains("\"Number Slider\" is not a script component", e.Message);
      Assert.Contains("\"Python 3 Script\" or \"C# Script\"", e.Message);
      Assert.Contains("(component \"s\")", e.Message);

      // And the ports of a non-script are refused the same way - a slider is not given an input by naming one.
      Assert.False(Parse(Spec(null, Comp("s", "Panel", o => o["inputs"] = new JsonArray("k"))), out _, out var ports));
      Assert.Contains("is not a script component", ports.Message);
      Assert.Contains("\"inputs\"", ports.Message);
    }

    [Fact]
    public void A_ScriptWithNoSourceIsAnOrdinaryComponent()
    {
      Assert.True(Parse(Spec(null, Comp("c", "Python 3 Script")), out var spec, out _));
      Assert.False(spec.Components.Single().CarriesSource);
      Assert.Empty(spec.Components.Single().Inputs);
    }

    [Fact]
    public void Ports_named_without_a_source_are_refused_because_nothing_reads_them()
    {
      Assert.False(Parse(Spec(null, Comp("c", "Python 3 Script", o => o["inputs"] = new JsonArray("offset"))), out _, out var e));
      Assert.Contains("needs the \"source\" that does", e.Message);
    }

    [Fact]
    public void An_empty_source_is_refused_and_says_how_to_place_an_empty_script()
    {
      Assert.False(Parse(Spec(null, Comp("c", "Python 3 Script", o => o["source"] = "   \n  ")), out _, out var e));
      Assert.Contains("\"source\" is empty", e.Message);
      Assert.Contains("leave \"source\" out", e.Message);
    }

    [Fact]
    public void A_source_over_the_consent_bound_is_refused_with_the_reason_for_the_bound()
    {
      var big = new string('a', EditScriptAsk.MaxSourceChars + 1);
      Assert.False(Parse(Spec(null, Comp("c", "Python 3 Script", o => o["source"] = big)), out _, out var e));
      Assert.Contains("a card nobody finishes reading", e.Message);

      var atBound = new string('a', EditScriptAsk.MaxSourceChars);
      Assert.True(Parse(Spec(null, Comp("c", "Python 3 Script", o => o["source"] = atBound)), out _, out _));   // the boundary is exact
    }

    [Fact]
    public void Appended_ports_are_checked_the_way_edit_script_checks_them()
    {
      Func<string[], string[], bool> ok = (i, o) => Parse(Spec(null, Comp("c", "Python 3 Script", c =>
      {
        c["source"] = PySource;
        if (i != null) c["inputs"] = new JsonArray(i.Select(n => (JsonNode)n).ToArray());
        if (o != null) c["outputs"] = new JsonArray(o.Select(n => (JsonNode)n).ToArray());
      })), out _, out _);

      Assert.True(ok(new[] { "offset" }, new[] { "perimeter" }));                // control

      // Brief EA: the build removes a side's defaults before it declares that side, so those names are free. ScriptPortClashTests has the rest.
      Assert.True(ok(new[] { "x" }, null));
      Assert.True(ok(null, new[] { "A" }));

      Parse(Spec(null, Comp("c", "Python 3 Script", c => { c["source"] = PySource; c["inputs"] = new JsonArray("not valid"); })), out _, out var bad);
      Assert.Contains("cannot be an input name", bad.Message);

      Parse(Spec(null, Comp("c", "Python 3 Script", c => { c["source"] = PySource; c["inputs"] = new JsonArray("k", "k"); })), out _, out var twice);
      Assert.Contains("is named twice", twice.Message);
    }

    [Fact]
    public void A_wire_into_an_input_the_spec_itself_appended_resolves()
    {
      var args = Spec(new JsonArray(Wire("s", "c", "offset")),
        Comp("s", "Number Slider"),
        Comp("c", "Python 3 Script", o => { o["source"] = PySource; o["inputs"] = new JsonArray("offset"); }));
      Assert.True(Parse(args, out var spec, out var e), e?.Message);
      Assert.Equal("offset", spec.Wires.Single().ToParam);

      // The control: the same wire to a name the script does NOT have is still refused with the real port list.
      var wrong = Spec(new JsonArray(Wire("s", "c", "offset")),
        Comp("s", "Number Slider"),
        Comp("c", "Python 3 Script", o => o["source"] = PySource));
      Assert.False(Parse(wrong, out _, out var refused));
      Assert.Contains("No input named \"offset\"", refused.Message);
    }

    // ---- code runs only where the user saw it ------------------------------------------------------------------

    [Fact]
    public void A_SCRIPT_IN_A_MAP_REGION_BUILD_IS_REFUSED_BECAUSE_THE_REGION_CARD_DOES_NOT_SHOW_COMPONENTS()
    {
      Assert.True(Parse(Spec(null, Comp("c", "Python 3 Script", o => o["source"] = PySource)), out var withCode, out _));
      Assert.True(Parse(Spec(null, Comp("c", "Python 3 Script")), out var withoutCode, out _));

      var refused = BuildScriptGate.Check(withCode, isApprovedSpot: false);
      Assert.NotNull(refused);
      Assert.Equal(BuildScriptGate.Reason, refused.Detail?["reason"]?.GetValue<string>());
      Assert.Contains("placement card", refused.Message);
      Assert.Contains("Nothing was built", refused.Message);

      Assert.Null(BuildScriptGate.Check(withCode, isApprovedSpot: true));       // control: the spot the user approved
      Assert.Null(BuildScriptGate.Check(withoutCode, isApprovedSpot: false));   // control: no code, no gate
    }

    // ---- the record round trip (#153) ---------------------------------------------------------------------------

    private static SpecResolvers Resolvers() => new SpecResolvers
    {
      TypeExists = TypeExists, Nearest = Nearest, IdState = Alive, PortsOfType = Ports, PortsOfId = null,
      CanonicalType = t => Installed.FirstOrDefault(i => string.Equals(i, Dwight.Components.ComponentMatchCore.BareName(t), StringComparison.OrdinalIgnoreCase)) ?? t,
    };

    private static SpotAsk Ask(JsonObject place)
    {
      Assert.True(SpotAsk.Validate(new HandoffSeed { Place = place }, Resolvers(), _ => null, out var ask, out var error), error?.ToJson());
      return ask;
    }

    [Fact]
    public void THE_RECORD_CARRIES_THE_SOURCE_AND_PARSES_BACK_TO_THE_SAME_SPEC()
    {
      var ask = Ask(Spec(new JsonArray(Wire("s", "c", "offset")),
        Comp("s", "Number Slider"),
        Comp("c", "python 3 script", o => { o["source"] = PySource; o["inputs"] = new JsonArray("offset"); o["outputs"] = new JsonArray("perimeter"); })));

      var record = ask.Record["components"].AsArray().Last().AsObject();
      Assert.Equal("Python 3 Script", record["type"].GetValue<string>());   // the plugin's spelling
      Assert.Equal("area = x * y\na = area", record["source"].GetValue<string>());
      Assert.Equal("offset", record["inputs"][0].GetValue<string>());
      Assert.Equal("perimeter", record["outputs"][0].GetValue<string>());

      // THE BUILD READS THE RECORD: it must come back as the spec the card was drawn from, source byte for byte.
      Assert.True(Parse((JsonObject)ask.Record.DeepClone(), out var reparsed, out var e), e?.Message);
      var c = reparsed.Components.Last();
      Assert.Equal(ask.Spec.Components.Last().Source, c.Source);
      Assert.Equal(new[] { "offset" }, c.Inputs.ToArray());
      Assert.Equal(new[] { "perimeter" }, c.Outputs.ToArray());
    }

    [Fact]
    public void A_placement_without_code_serialises_exactly_as_it_did()
    {
      var ask = Ask(Spec(null, Comp("s", "Number Slider")));
      var o = ask.Record["components"].AsArray().Single().AsObject();
      Assert.False(o.ContainsKey("source"));
      Assert.False(o.ContainsKey("inputs"));
      Assert.False(o.ContainsKey("outputs"));
      Assert.Empty(ask.Placement.Scripts);
    }

    // ---- the card ----------------------------------------------------------------------------------------------

    [Fact]
    public void THE_CARD_STATES_THAT_THE_CODE_WILL_RUN_IN_THE_SENTENCE_EDIT_SCRIPT_USES()
    {
      var ask = Ask(Spec(null, Comp("c", "Python 3 Script", o => o["source"] = PySource)));

      foreach (var grouped in new[] { true, false })
      {
        var line = ConsentCard.SpotAuthority(ask.Placement, grouped);
        Assert.Contains("THE CODE WILL RUN IN YOUR DEFINITION.", line);
        Assert.Contains(ConsentCard.CodeRunsClause, line);
        Assert.Contains("takes the whole build back with it", line);   // a claim about OUR behaviour: held by the oracle tests below
      }

      // ONE SENTENCE FOR ONE FACT: edit_script's card says the same words, from the same constant.
      var edit = ConsentCard.EditScriptAuthority(new ScriptEditChange { Label = "\"F1\"", Claim = ScriptEditClaim.Changes }, ScriptEditOutcome.RanClean, new ScriptTrialFacts { Ran = true });
      Assert.Contains(ConsentCard.CodeRunsClause, edit);
    }

    /// <summary>Brief CV: A SPOT CARRIES AS MANY SCRIPTS AS A REGION CARD MAY. The card shows each one whole, so a spot of twelve would be a card nobody reads, and the
    /// old bound of five components had kept this from arising. Through the whole ask; a component with no source is not a script the card has to show.</summary>
    [Fact]
    public void A_SPOT_CARRIES_NO_MORE_SCRIPTS_THAN_A_REGION_CARD()
    {
      JsonObject[] Scripts(int n) => Enumerable.Range(0, n).Select(i => Comp("c" + i, "Python 3 Script", o => o["source"] = PySource)).ToArray();

      Assert.Equal(10, Ask(Spec(null, Scripts(10))).Placement.Scripts.Count);
      Assert.Equal(10, Ask(Spec(null, Scripts(10).Concat(new[] { Comp("s", "Number Slider"), Comp("t", "Number Slider") }).ToArray())).Placement.Scripts.Count);

      Assert.False(SpotAsk.Validate(new HandoffSeed { Place = Spec(null, Scripts(11)) }, Resolvers(), _ => null, out var ask, out var error));
      Assert.Null(ask);
      Assert.Equal(ToolErrors.InvalidArgs, error.Code);
      Assert.Contains("at most 10 scripts", error.Message);
      Assert.Contains("this asks for 11", error.Message);
      Assert.Equal(ScriptsAsk.TooMany(11).Message, error.Message);
    }

    [Fact]
    public void A_CARD_WITHOUT_CODE_DOES_NOT_CLAIM_CODE_WILL_RUN()
    {
      var ask = Ask(Spec(null, Comp("s", "Number Slider")));
      Assert.DoesNotContain("CODE", ConsentCard.SpotAuthority(ask.Placement, true));
      Assert.DoesNotContain("consent-source", ConsentCard.SpotHtml("why", ask.Placement, "t1", "t2"));
      Assert.Equal("", ConsentCard.SpotScriptClause(ask.Placement));
    }

    [Fact]
    public void THE_COMPLETE_SOURCE_IS_ON_THE_CARD_UNTRUNCATED_AND_ESCAPED()
    {
      // The longest source a card will carry, with markup in it: nothing cut, nothing live.
      var body = "# <b>not markup</b> & \"quoted\"\n" + string.Join("\n", Enumerable.Range(0, 400).Select(i => "v" + i + " = " + i));
      var source = body.Substring(0, Math.Min(body.Length, EditScriptAsk.MaxSourceChars));
      var ask = Ask(Spec(null, Comp("c", "Python 3 Script", o => o["source"] = source)));

      var html = ConsentCard.SpotHtml("why", ask.Placement, "t1", "t2");
      var shown = System.Net.WebUtility.HtmlEncode(ask.Spec.Components.Single().Source);

      Assert.Contains("<pre class=\"consent-source\">" + shown + "</pre>", html);   // all of it, byte for byte
      Assert.DoesNotContain("<b>not markup</b>", html);                              // inert
      Assert.Contains("&lt;b&gt;not markup&lt;/b&gt;", html);
      Assert.Contains("which is what will run", html);
    }

    [Fact]
    public void EVERY_SCRIPT_ON_A_CARD_HAS_ITS_OWN_COMPLETE_SOURCE()
    {
      var ask = Ask(Spec(null,
        Comp("a", "Python 3 Script", o => o["source"] = "a = 'FIRST_SOURCE_MARKER'"),
        Comp("b", "C# Script", o => o["source"] = "// SECOND_SOURCE_MARKER")));
      var html = ConsentCard.SpotHtml("why", ask.Placement, "t1", "t2");
      Assert.Contains("FIRST_SOURCE_MARKER", html);
      Assert.Contains("SECOND_SOURCE_MARKER", html);
      Assert.Contains("(Python 3)", html);
      Assert.Contains("(C#)", html);
      Assert.Contains("Their complete sources are shown below", ConsentCard.SpotAuthority(ask.Placement, true));
    }

    [Fact]
    public void THE_CARD_NAMES_THE_INPUTS_THE_SPEC_LEAVES_UNWIRED()
    {
      // U78: declared inputs REPLACE x and y, so only the declared ones can be unwired. offset is wired by a slider; gap and
      // scale are not.
      var partial = Ask(Spec(new JsonArray(Wire("s", "c", "offset")),
        Comp("s", "Number Slider"),
        Comp("c", "Python 3 Script", o => { o["source"] = PySource; o["nick"] = "Area"; o["inputs"] = new JsonArray("offset", "gap", "scale"); })));
      var line = ConsentCard.SpotAuthority(partial.Placement, true);
      Assert.Contains("Nothing is wired to Area's inputs gap, scale, so it will run without them.", line);
      Assert.DoesNotContain("x, y", line);
      Assert.DoesNotContain("nothing fed to it", line);

      // None wired: the sentence changes to the stronger fact.
      var none = Ask(Spec(null, Comp("c", "Python 3 Script", o => { o["source"] = PySource; o["nick"] = "Area"; })));
      Assert.Contains("Nothing is wired to Area's inputs x, y, so it will run with nothing fed to it.", ConsentCard.SpotAuthority(none.Placement, true));

      // All wired: no such sentence at all (control).
      var all = Ask(Spec(new JsonArray(Wire("s", "c", "x"), Wire("s", "c", "y")),
        Comp("s", "Number Slider"), Comp("c", "Python 3 Script", o => o["source"] = PySource)));
      Assert.DoesNotContain("Nothing is wired", ConsentCard.SpotAuthority(all.Placement, true));
    }

    // ---- the oracle: three states, one fact apart ---------------------------------------------------------------

    private static ScriptBuildFacts Facts(string lang, string[] errors, ScriptOutputRead[] outs, string[] inputs, string[] unwired) =>
      new ScriptBuildFacts
      {
        Ref = "calc", Name = "Area", Language = lang,
        Errors = errors, Outputs = outs, Inputs = inputs, UnwiredInputs = unwired,
      };

    private static ScriptOutputRead Out(string port, int items, int nonNull) => new ScriptOutputRead { Port = port, Items = items, NonNull = nonNull };

    [Fact]
    public void THE_THREE_STATES_ARE_DISTINCT_ON_FIXTURES_THAT_DIFFER_IN_ONE_FACT()
    {
      var two = new[] { "x", "y" };

      var produced = Facts("python", new string[0], new[] { Out("a", 1, 1) }, two, new string[0]);
      var empty = Facts("python", new string[0], new[] { Out("a", 1, 0) }, two, new string[0]);            // the output is a null item
      var unexercised = Facts("python", new string[0], new[] { Out("a", 1, 0) }, two, two);                  // ... and nothing was fed
      var failed = Facts("python", new[] { "NameError" }, new[] { Out("a", 1, 1) }, two, new string[0]);     // data present, but an error

      Assert.Equal(ScriptBuildOutcome.Produced, ScriptBuildVerdict.Outcome(produced));
      Assert.Equal(ScriptBuildOutcome.Empty, ScriptBuildVerdict.Outcome(empty));
      Assert.Equal(ScriptBuildOutcome.Unexercised, ScriptBuildVerdict.Outcome(unexercised));
      Assert.Equal(ScriptBuildOutcome.DidNotRun, ScriptBuildVerdict.Outcome(failed));
    }

    /// <summary>#902: an item COUNT says nothing. One item that is null is not data - it is what an errored script and a
    /// script that assigns None both emit.</summary>
    [Fact]
    public void A_NULL_ITEM_IS_NOT_DATA_AND_ERRORS_ARE_CHECKED_BEFORE_ANY_ITEM_IS_COUNTED()
    {
      Assert.Equal(ScriptBuildOutcome.Empty,
        ScriptBuildVerdict.Outcome(Facts("python", new string[0], new[] { Out("a", 3, 0) }, new[] { "x" }, new string[0])));

      // An errored script that ALSO shows a non-null item: errors win, not the data.
      Assert.Equal(ScriptBuildOutcome.DidNotRun,
        ScriptBuildVerdict.Outcome(Facts("csharp", new[] { "CS1002" }, new[] { Out("a", 1, 1) }, new[] { "x" }, new string[0])));

      // A blank error message is not an error (the runtime sometimes reports an empty string).
      Assert.Equal(ScriptBuildOutcome.Produced,
        ScriptBuildVerdict.Outcome(Facts("python", new[] { "  " }, new[] { Out("a", 1, 1) }, new[] { "x" }, new string[0])));
    }

    [Fact]
    public void A_script_with_no_inputs_that_produces_nothing_is_EMPTY_not_unexercised()
    {
      // Nothing to feed means nothing was withheld: the emptiness is the script's own.
      Assert.Equal(ScriptBuildOutcome.Empty,
        ScriptBuildVerdict.Outcome(Facts("python", new string[0], new[] { Out("a", 0, 0) }, new string[0], new string[0])));
    }

    [Fact]
    public void PRODUCED_STANDS_AND_THE_BUILDLINE_STATES_THE_OUTPUTS_WITH_ITEMS()
    {
      var f = Facts("python", new string[0], new[] { Out("a", 3, 3), Out("b", 1, 0) }, new[] { "x", "y" }, new[] { "y" });
      var j = ScriptBuildVerdict.Judge(new[] { f }, null);

      Assert.True(j.Stands);
      Assert.Null(j.Refusal);
      var note = j.Notes["calc"];
      Assert.Contains("produced a: 3 items", note);
      Assert.Contains("no data on b", note);
      Assert.Contains("nothing is wired to its input y", note);
      Assert.False(BuildPlacedFacts.SaysVerified(note));
    }

    [Fact]
    public void UNEXERCISED_STANDS_WITH_A_CLAUSE_SAYING_NOTHING_WAS_FED()
    {
      var f = Facts("python", new string[0], new[] { Out("a", 1, 0) }, new[] { "x", "y" }, new[] { "x", "y" });
      var j = ScriptBuildVerdict.Judge(new[] { f }, null);

      Assert.True(j.Stands);
      var note = j.Notes["calc"];
      Assert.Contains("NOT EXERCISED", note);
      Assert.Contains("nothing is wired to its inputs x, y", note);
      Assert.Contains("nothing fed to it", note);
      Assert.Contains("its empty output a proves nothing", note);
      Assert.False(BuildPlacedFacts.SaysVerified(note));
      Assert.Equal(ScriptBuildOutcome.Unexercised, j.Outcomes["calc"]);
    }

    [Fact]
    public void EMPTY_IS_REFUSED_NAMING_THE_UNWIRED_INPUTS_AND_THE_EMPTY_OUTPUTS()
    {
      // x is wired, y is not; both outputs hold nothing.
      var f = Facts("python", new string[0], new[] { Out("a", 1, 0), Out("perimeter", 0, 0) }, new[] { "x", "y" }, new[] { "y" });
      var j = ScriptBuildVerdict.Judge(new[] { f }, null);

      Assert.False(j.Stands);
      Assert.Contains("every output (a, perimeter) holds no data", j.Refusal);
      Assert.Contains("Nothing is wired to its input y", j.Refusal);
      Assert.Contains("was reverted - the canvas is as it was", j.Refusal);
      Assert.Contains("Script \"calc\" (Area)", j.Refusal);
      Assert.Contains("not a limit of the tool", j.Refusal);
      Assert.Empty(j.Notes);
    }

    [Fact]
    public void An_empty_script_whose_inputs_are_all_wired_says_so_instead_of_blaming_an_input()
    {
      var f = Facts("python", new string[0], new[] { Out("a", 1, 0) }, new[] { "x", "y" }, new string[0]);
      var j = ScriptBuildVerdict.Judge(new[] { f }, null);
      Assert.False(j.Stands);
      Assert.Contains("Every input is wired, so the code returned nothing for what it was given", j.Refusal);
      Assert.DoesNotContain("Nothing is wired", j.Refusal);
    }

    [Fact]
    public void A_CSHARP_COMPILE_FAILURE_CARRIES_THE_COMPILERS_WORDS_AND_THE_REFLECTED_NO_SUCH_TYPE_LINE()
    {
      var errors = new[] { "error CS0246: The type or namespace name 'LoftOptions' could not be found" };
      var f = Facts("csharp", errors, new[] { Out("a", 1, 0) }, new[] { "x", "y" }, new[] { "x", "y" });
      IReadOnlyList<string> seen = null;
      var j = ScriptBuildVerdict.Judge(new[] { f }, errs =>
      {
        seen = errs;
        return "LoftOptions: NO SUCH TYPE in RhinoCommon. You invented it - use a type that exists.";
      });

      Assert.False(j.Stands);
      Assert.Contains(CompileRepair.CompiledHeader, j.Refusal);                 // "DID NOT COMPILE. The compiler said:"
      Assert.DoesNotContain(CompileRepair.InterpretedHeader, j.Refusal);
      Assert.Contains("CS0246", j.Refusal);
      Assert.Contains("'LoftOptions' could not be found", j.Refusal);
      Assert.Contains("LoftOptions: NO SUCH TYPE in RhinoCommon", j.Refusal);
      Assert.Equal(errors, seen.ToArray());
      Assert.Contains("was reverted - the canvas is as it was", j.Refusal);
      // The build's own instruction, not to-script's "do not change the interface": the model DECLARED this interface.
      Assert.DoesNotContain("Do not change the interface", j.Refusal);
      Assert.Contains("Fix the script and build again", j.Refusal);
    }

    [Fact]
    public void A_PYTHON_FAILURE_GETS_THE_INTERPRETERS_HEADER_AND_NEVER_THE_ROSLYN_REFLECTION()
    {
      var errors = new[] { "NameError: name 'FooBar' is not defined" };
      var f = Facts("python", errors, new[] { Out("a", 1, 0) }, new[] { "x", "y" }, new string[0]);
      var reflected = false;
      var j = ScriptBuildVerdict.Judge(new[] { f }, errs => { reflected = true; return "FooBar: NO SUCH TYPE in RhinoCommon."; });

      Assert.False(reflected, "reflection over RhinoCommon answers a C# compiler's error, not a Python NameError");
      Assert.Contains(CompileRepair.InterpretedHeader, j.Refusal);
      Assert.Contains("NameError", j.Refusal);
      Assert.DoesNotContain("NO SUCH TYPE", j.Refusal);
    }

    [Fact]
    public void A_failure_beside_unwired_inputs_says_the_error_may_be_the_missing_data()
    {
      var f = Facts("python", new[] { "TypeError: unsupported operand type(s) for *: 'NoneType' and 'NoneType'" },
        new[] { Out("a", 1, 0) }, new[] { "x", "y" }, new[] { "x", "y" });
      var j = ScriptBuildVerdict.Judge(new[] { f }, null);
      Assert.Contains("Nothing is wired to its inputs x, y", j.Refusal);
      Assert.Contains("meeting an input with no data", j.Refusal);
    }

    [Fact]
    public void Refusals_are_bounded_and_count_the_rest()
    {
      var many = Enumerable.Range(0, 5).Select(i => new ScriptBuildFacts
      {
        Ref = "s" + i, Name = "S" + i, Language = "python", Errors = new[] { "boom " + i },
        Outputs = new[] { Out("a", 1, 0) }, Inputs = new[] { "x" }, UnwiredInputs = new string[0],
      }).ToList();
      var j = ScriptBuildVerdict.Judge(many, null);
      Assert.Contains("5 scripts that did not run", j.Refusal);
      Assert.Contains("Script \"s2\"", j.Refusal);
      Assert.DoesNotContain("Script \"s3\"", j.Refusal);
      Assert.Contains("And 2 more scripts failed", j.Refusal);
    }

    [Fact]
    public void A_failure_outranks_an_empty_script_in_the_same_build_and_no_scripts_is_a_clean_pass()
    {
      var empty = Facts("python", new string[0], new[] { Out("a", 1, 0) }, new[] { "x" }, new string[0]);
      var bad = Facts("python", new[] { "boom" }, new[] { Out("a", 1, 0) }, new[] { "x" }, new string[0]);
      bad.Ref = "bad";
      var j = ScriptBuildVerdict.Judge(new[] { empty, bad }, null);
      Assert.Contains("did not run", j.Refusal);
      Assert.DoesNotContain("produced nothing", j.Refusal);

      var none = ScriptBuildVerdict.Judge(new ScriptBuildFacts[0], null);
      Assert.True(none.Stands);
      Assert.Empty(none.Notes);
    }

    // ---- the buildline -----------------------------------------------------------------------------------------

    [Fact]
    public void THE_SCRIPTS_NOTE_REACHES_THE_BUILDLINE_THROUGH_THE_VERDICT_PAYLOAD()
    {
      var f = Facts("python", new string[0], new[] { Out("a", 1, 1) }, new[] { "x", "y" }, new string[0]);
      var note = ScriptBuildVerdict.Judge(new[] { f }, null).Notes["calc"];

      var placed = new PlacedComponentFact
      {
        Ref = "calc", Id = 12, Type = "Python 3 Script", Nick = "Area",
        Outputs = { new PlacedOutputFact { Port = "a", Items = 1 } },
        ScriptOutcome = "produced", ScriptNote = note,
      };

      var json = BuildPlacedFacts.ToJson(new[] { placed });
      var back = BuildPlacedFacts.FromJson(json).Single();
      Assert.Equal(note, back.ScriptNote);
      Assert.Equal("produced", back.ScriptOutcome);

      var verdict = new JsonObject { ["newErrors"] = 0, ["placedFacts"] = json };
      var summary = BuildPlacedFacts.Summary(verdict);
      Assert.Contains("script ran without an error and produced a: 1 item", summary);
      Assert.Contains("Area (Python 3 Script) (12)", summary);
      Assert.False(BuildPlacedFacts.SaysVerified(summary));
    }

    [Fact]
    public void A_script_is_not_also_called_an_empty_output_by_the_generic_wording()
    {
      // Items counts the null item an unexercised script emits, so "empty output" would be wrong anyway; and when the
      // items DO read zero, the script note - which counts non-null items - is the one sentence about its data.
      var placed = new PlacedComponentFact
      {
        Ref = "calc", Id = 3, Type = "Python 3 Script",
        Outputs = { new PlacedOutputFact { Port = "a", Items = 0 } },
        ScriptOutcome = "unexercised", ScriptNote = "script NOT EXERCISED - nothing is wired to its inputs x, y",
      };
      var line = BuildPlacedFacts.Line(placed, null);
      Assert.Contains("NOT EXERCISED", line);
      Assert.DoesNotContain("empty output", line);

      // Control: a non-script with the same output still says it.
      placed.ScriptNote = null;
      Assert.Contains("empty output a", BuildPlacedFacts.Line(placed, null));
    }

    // ---- reuse: the shared repair text is unchanged --------------------------------------------------------------

    [Fact]
    public void CompileRepair_Text_is_its_evidence_plus_to_scripts_instruction_byte_for_byte()
    {
      var errors = new[] { "CS0246: nope" };
      foreach (var api in new string[] { null, "Curve: Length" })
      {
        var evidence = CompileRepair.Evidence(errors, true, api);
        var text = CompileRepair.Text(errors, true, api);
        Assert.StartsWith(evidence, text, StringComparison.Ordinal);
        Assert.Contains("Do not change the interface", text);                  // to-script's instruction survives
        Assert.DoesNotContain("Do not change the interface", evidence);        // and is NOT part of the evidence
      }
    }

    // ---- the published contract (#316) ---------------------------------------------------------------------------

    [Fact]
    public void THE_DESCRIPTOR_PUBLISHES_THE_FIELDS_THE_PARSER_READS_ON_BOTH_TOOLS()
    {
      foreach (var components in new[]
      {
        BuildSpecSchema.Components(),
        (JsonObject)((JsonObject)JsonNode.Parse(AgentToolCatalog.SchemaOf(AgentToolCatalog.BuildRegion)))["properties"]["components"],
        (JsonObject)AgentHostTools.All()[0].InputSchema["properties"]["place"]["properties"]["components"],
      })
      {
        var props = (JsonObject)components["items"]["properties"];
        Assert.True(props.ContainsKey("source"));
        Assert.True(props.ContainsKey("inputs"));
        Assert.True(props.ContainsKey("outputs"));
        Assert.Equal("array", props["inputs"]["type"].GetValue<string>());
        Assert.Contains("COMPLETE script", props["source"]["description"].GetValue<string>());
        Assert.Contains("THE CODE RUNS IN THEIR DEFINITION", props["source"]["description"].GetValue<string>());
      }
    }

    /// <summary>#315: a claim in a prompt about what we do carries a test on the behaviour's side. The schema says a script
    /// that cannot run takes the placement back, and the kind table says a placement can include one with its source.</summary>
    [Fact]
    public void THE_KIND_TABLE_SAYS_A_PLACEMENT_CAN_CARRY_A_SCRIPT_WITH_ITS_SOURCE_AND_THE_ASK_ACCEPTS_ONE()
    {
      Assert.Contains("a C# or Python script WITH ITS SOURCE", AgentHostTools.KindScope["build"].Can);

      // The behaviour: the ask validates a script placement (it does not refuse the very thing the table promises).
      var ask = Ask(Spec(null, Comp("c", "C# Script", o => o["source"] = "private void RunScript() { }")));
      Assert.Single(ask.Placement.Scripts);
      Assert.Equal("C#", ask.Placement.Scripts[0].LanguageLabel);
    }

    [Fact]
    public void U98_THE_SPOT_CARDS_SCRIPT_IS_FOLDED_WITH_THE_PLUGINS_SUMMARY_AND_THE_WHOLE_SOURCE_INSIDE()
    {
      var ask = Ask(Spec(null, Comp("calc", "Python 3 Script", o => o["source"] = PySource)));
      var html = ConsentCard.SpotHtml("why", ask.Placement, "t1", "t2");

      Assert.Contains("<details class=\"consent-script\">", html);          // folded
      Assert.DoesNotContain("<details class=\"consent-script\" open>", html);
      Assert.False(ConsentCard.ExpandedByDefaultFor("build"));

      var script = ask.Placement.Scripts.Single();
      var summary = script.Name + ". " + ScriptSummary.Line(script.LanguageLabel, script.Source, script.Inputs, script.Outputs);
      Assert.Contains("<summary class=\"consent-script-summary\"", html);
      Assert.Contains(System.Net.WebUtility.HtmlEncode(summary) + "</summary>", html);
      Assert.Contains("Inputs: x (item), y (item). Outputs: a. Touches the Rhino document: no.", summary);

      // Folded, and complete: the whole source is inside the element, byte for byte.
      var shown = System.Net.WebUtility.HtmlEncode(ask.Spec.Components.Single().Source);
      Assert.Contains("<pre class=\"consent-source\">" + shown + "</pre></details>", html);
    }

    [Fact]
    public void U98_EVERY_SCRIPT_ON_A_SPOT_CARD_HAS_ITS_OWN_FOLD()
    {
      var ask = Ask(Spec(null, Comp("a", "Python 3 Script", o => o["source"] = PySource), Comp("b", "C# Script", o => o["source"] = "var y = 1;")));
      var html = ConsentCard.SpotHtml("why", ask.Placement, "t1", "t2");

      Assert.Equal(2, html.Split(new[] { "<details class=\"consent-script\">" }, StringSplitOptions.None).Length - 1);
    }
  }
}
