using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Dwight.Agent;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// Brief EA, from the thirteenth gate's row 3: a C# script whose inputs the model named a, b, c, d and whose output it named total
  /// failed three times, 37 cents, with "script mix is born with a port called a already". That was false. The check stood ahead of the
  /// removal U78 put after it, so it refused the input a for an output a the build was about to remove. A name the plugin can land, it
  /// lands. A name it cannot, it refuses before any card or build, naming the port.
  ///
  /// The rules follow the bench's readings on the thirteenth build, and the PR records them: x, y and a land on the side that replaces
  /// them in both languages; one name on both sides fails to compile in C# and runs in Python; Rhino's C# script refuses out, string and
  /// value; Python only warns about a keyword, which the code still cannot read or assign; a second output called out lands beside the
  /// message output with nothing to tell them apart.
  /// </summary>
  public class ScriptPortClashTests
  {
    private const string Mix = "private void RunScript(double a, double b, double c, double d, ref object total)\n{\n  total = a + b + c + d;\n}";
    private const string Py = "r = a + x";
    private const string PyType = "Python 3 Script";
    private const string CsType = "C# Script";

    private static readonly string[] Installed = { "Number Slider", "Panel", PyType, CsType };
    private static bool TypeExists(string t) => Installed.Contains(Dwight.Components.ComponentMatchCore.BareName(t), StringComparer.OrdinalIgnoreCase);
    private static IReadOnlyList<string> Nearest(string t) => Installed;
    private static IdState Alive(int id) => IdState.Alive;

    // The ports as the live graph reports them: x, y, the "out" message output and a.
    private static PortShape Ports(string type)
    {
      var bare = Dwight.Components.ComponentMatchCore.BareName(type);
      if (bare.IndexOf("Script", StringComparison.OrdinalIgnoreCase) >= 0)
        return new PortShape
        {
          Inputs = new[] { "x", "y" }, Outputs = new[] { "out", "a" },
          InputNames = new[] { "x", "y" }, OutputNames = new[] { "out", "a" },
        };
      if (bare == "Number Slider") return new PortShape { Inputs = new string[0], Outputs = new string[0] };
      return null;
    }

    private static JsonObject Comp(string r, string type, Action<JsonObject> more = null)
    {
      var o = new JsonObject { ["ref"] = r, ["type"] = type };
      more?.Invoke(o);
      return o;
    }

    private static JsonArray Names(params string[] names) => names == null ? null : new JsonArray(names.Select(n => (JsonNode)n).ToArray());

    private static JsonObject Script(string type, string[] inputs, string[] outputs, string source = Py) => Comp("s", type, o =>
    {
      o["source"] = source;
      if (inputs != null) o["inputs"] = Names(inputs);
      if (outputs != null) o["outputs"] = Names(outputs);
    });

    private static JsonObject Wire(string fromRef, string fromParam, string toRef, string toParam)
    {
      var from = new JsonObject { ["ref"] = fromRef };
      if (fromParam != null) from["param"] = fromParam;
      var to = new JsonObject { ["ref"] = toRef };
      if (toParam != null) to["param"] = toParam;
      return new JsonObject { ["from"] = from, ["to"] = to };
    }

    private static bool Parse(JsonArray wires, out BuildSpec spec, out ToolResult error, params JsonObject[] components)
    {
      var args = new JsonObject { ["components"] = new JsonArray(components.Cast<JsonNode>().ToArray()) };
      if (wires != null) args["wires"] = wires;
      return BuildSpecParser.TryParse(args, TypeExists, Nearest, Alive, Ports, null, out spec, out error);
    }

    private static ToolResult Refusal(string type, string[] inputs, string[] outputs)
    {
      Parse(null, out _, out var e, Script(type, inputs, outputs));
      return e;
    }

    private static string GhBuildRegionSource() =>
      File.ReadAllText(Path.Combine(RepoRoot(), "Dwight", "Agent", "GhBuildRegion.cs"));

    private static string RepoRoot()
    {
      var dir = AppContext.BaseDirectory;
      while (dir != null && !File.Exists(Path.Combine(dir, "Dwight.sln"))) dir = Path.GetDirectoryName(dir);
      Assert.NotNull(dir);
      return dir;
    }

    // ---- what the gate hit --------------------------------------------------------------------------------------------

    /// <summary>The spec the gate's three attempts declared, with its four wires.</summary>
    [Fact]
    public void The_gates_script_with_inputs_a_b_c_d_and_output_total_parses_and_wires_by_name()
    {
      var wires = new JsonArray(Wire("sa", null, "s", "a"), Wire("sb", null, "s", "b"), Wire("sc", null, "s", "c"), Wire("sd", null, "s", "d"));
      Assert.True(Parse(wires, out var spec, out var e,
        Comp("sa", "Number Slider"), Comp("sb", "Number Slider"), Comp("sc", "Number Slider"), Comp("sd", "Number Slider"),
        Script(CsType, new[] { "a", "b", "c", "d" }, new[] { "total" }, Mix)), e?.Message);

      Assert.Equal(new[] { "a", "b", "c", "d" }, spec.Wires.Select(w => w.ToParam));
      var script = Assert.Single(SpotScripts.Of(spec, Ports));
      Assert.Equal(new[] { "a", "b", "c", "d" }, script.Inputs);
      Assert.Equal(new[] { "total" }, script.Outputs);
      Assert.Empty(script.UnwiredInputs);
    }

    /// <summary>The same script through the region door: a card that listed no ports, and a build that declares them, as the gate's did.</summary>
    [Fact]
    public void The_gates_script_passes_the_cards_gate_when_the_card_listed_no_ports()
    {
      Assert.True(Parse(null, out var spec, out var e, Script(CsType, new[] { "a", "b", "c", "d" }, new[] { "total" }, Mix)), e?.Message);
      spec.Region = "A1";
      var card = new ApprovedScript { Region = "A1", Language = BuildScriptTypes.CSharp, Source = spec.Components[0].Source };

      Assert.Null(BuildScriptGate.Adopt(spec, false, new[] { card }, out var subs));
      Assert.True(Assert.Single(subs).PortsTakenFromBuild);
      Assert.Equal(new[] { "a", "b", "c", "d" }, spec.Components[0].Inputs);
    }

    // ---- what lands, on the bench's rows and the ones between them --------------------------------------------------------

    [Theory]
    [InlineData(CsType, "a,x", "r")]        // the brief's reading, C1
    [InlineData(PyType, "a,x", "r")]        // Y1
    [InlineData(CsType, "x,y", "a")]        // every default, on the side that replaces it, C2
    [InlineData(PyType, "x,y", "a")]        // Y2
    [InlineData(CsType, "p,q", "r")]        // the control, C3
    [InlineData(PyType, "p,q", "r")]        // Y3
    [InlineData(PyType, "a,x", "a")]        // one name on both sides runs in Python, Y4
    [InlineData(PyType, "out,q", "r")]      // an input called out, beside the message output, Y6
    [InlineData(CsType, "y,x", "a")]        // in another order
    [InlineData(CsType, "p,q", "x,y")]      // the inputs' defaults as outputs
    [InlineData(CsType, "a", "x")]          // both swapped
    [InlineData(CsType, "x", "r")]          // one of two defaults, the other removed
    [InlineData(CsType, "Point3d,q", "r")]  // a type's name is not a reserved word, K4
    [InlineData(PyType, "print,q", "r")]    // a builtin only draws Rhino's warning, L3
    [InlineData(CsType, "Value,q", "r")]    // C# minds case
    [InlineData(PyType, "none,q", "r")]     // and so does Python
    public void A_name_lands_where_the_language_and_the_removal_allow_it(string type, string inputs, string outputs)
    {
      var i = inputs.Split(',');
      var o = outputs.Split(',');
      Assert.True(Parse(null, out var spec, out var e, Script(type, i, o)), e?.Message);
      var script = Assert.Single(SpotScripts.Of(spec, Ports));
      Assert.Equal(i, script.Inputs);
      Assert.Equal(o, script.Outputs);
    }

    [Theory]
    [InlineData(CsType)]
    [InlineData(PyType)]
    public void One_side_declared_keeps_the_other_sides_defaults_and_those_do_not_clash(string type)
    {
      Assert.Null(Refusal(type, new[] { "x" }, null));        // inputs x; output a is kept
      Assert.Null(Refusal(type, new[] { "y", "x" }, null));
      Assert.Null(Refusal(type, null, new[] { "a" }));        // inputs x, y are kept; output a
      Assert.Null(Refusal(type, null, new[] { "A" }));
      Assert.Null(Refusal(type, null, null));                 // nothing declared: x, y and a, as born
    }

    [Fact]
    public void In_python_an_input_called_a_keeps_the_output_a_it_was_born_with()
    {
      Assert.Null(Refusal(PyType, new[] { "a", "b" }, null));
      Assert.Null(Refusal(PyType, null, new[] { "x" }));
    }

    [Fact]
    public void A_wire_reaches_a_declared_input_called_a_and_comes_from_a_declared_output_called_a()
    {
      var wires = new JsonArray(Wire("n", null, "s", "x"), Wire("s", "a", "p", null));
      Assert.True(Parse(wires, out var spec, out var e, Comp("n", "Number Slider"), Comp("p", "Panel"), Script(PyType, new[] { "x", "q" }, new[] { "a" })), e?.Message);
      Assert.Equal("x", spec.Wires[0].ToParam);
      Assert.Equal("a", spec.Wires[1].FromParam);

      // the control: y is one of the inputs this script replaced and did not declare again, so a wire to it is still refused
      Parse(new JsonArray(Wire("n", null, "s", "y")), out _, out var gone, Comp("n", "Number Slider"), Script(PyType, new[] { "x", "q" }, new[] { "a" }));
      Assert.Contains("No input named \"y\"", gone.Message);
    }

    [Fact]
    public void A_python_script_with_one_name_on_both_sides_wires_each_side_to_its_own_port()
    {
      var wires = new JsonArray(Wire("n", null, "s", "a"), Wire("s", "a", "p", null));
      Assert.True(Parse(wires, out var spec, out var e, Comp("n", "Number Slider"), Comp("p", "Panel"), Script(PyType, new[] { "a", "x" }, new[] { "a" })), e?.Message);
      Assert.Equal("a", spec.Wires[0].ToParam);
      Assert.Equal("a", spec.Wires[1].FromParam);
    }

    // ---- what is refused, before any card or build ------------------------------------------------------------------------

    [Fact]
    public void A_csharp_input_called_a_with_no_outputs_declared_is_refused_naming_the_output_it_keeps()
    {
      var e = Refusal(CsType, new[] { "a", "b" }, null);
      Assert.Equal(ToolErrors.InvalidArgs, e.Code);
      Assert.Contains("\"a\" cannot be both an input and an output of one C# script (component \"s\")", e.Message);
      Assert.Contains("two parameters of one method, which does not compile", e.Message);
      Assert.Contains("it declares no \"outputs\", so it keeps the output a", e.Message);
      Assert.Contains("its inputs are a, b", e.Message);
      Assert.Contains("Declare its \"outputs\"", e.Message);
      Assert.Equal("a", e.Detail["port"].GetValue<string>());
      Assert.Equal(new[] { "a", "b" }, e.Detail["inputs"].AsArray().Select(n => n.GetValue<string>()));
      Assert.Equal(new[] { "a" }, e.Detail["outputs"].AsArray().Select(n => n.GetValue<string>()));
    }

    [Fact]
    public void A_csharp_output_called_x_with_no_inputs_declared_is_refused_naming_the_inputs_it_keeps()
    {
      var e = Refusal(CsType, null, new[] { "x" });
      Assert.Equal(ToolErrors.InvalidArgs, e.Code);
      Assert.Contains("\"x\" cannot be both an input and an output", e.Message);
      Assert.Contains("it declares no \"inputs\", so it keeps the inputs x, y", e.Message);
      Assert.Contains("its outputs are x", e.Message);
      Assert.Contains("Declare its \"inputs\"", e.Message);
      Assert.Equal(new[] { "x", "y" }, e.Detail["inputs"].AsArray().Select(n => n.GetValue<string>()));
    }

    /// <summary>The bench's C4: "The parameter name 'a' is a duplicate".</summary>
    [Fact]
    public void One_declared_name_on_both_sides_of_a_csharp_script_is_refused_with_both_lists()
    {
      var e = Refusal(CsType, new[] { "a", "x" }, new[] { "a" });
      Assert.Equal(ToolErrors.InvalidArgs, e.Code);
      Assert.Contains("\"a\" cannot be both an input and an output", e.Message);
      Assert.Contains("its inputs are a, x and its outputs are a", e.Message);
      Assert.Contains("Rename one of them", e.Message);

      Assert.Contains("\"len\" cannot be both", Refusal(CsType, new[] { "len" }, new[] { "len" }).Message);   // not only a default's name
    }

    [Fact]
    public void The_comparison_across_the_sides_ignores_case_as_the_others_do()
    {
      Assert.Contains("\"A\" cannot be both", Refusal(CsType, new[] { "A" }, null).Message);
      Assert.Contains("\"Len\" cannot be both", Refusal(CsType, new[] { "Len" }, new[] { "len" }).Message);
      Assert.Contains("\"K\" is named twice", Refusal(CsType, new[] { "k", "K" }, null).Message);
    }

    /// <summary>The bench's C5, C6, K1, K2 and K3: Rhino's own error, or the compiler's, on each.</summary>
    [Theory]
    [InlineData("out")]
    [InlineData("class")]
    [InlineData("string")]
    [InlineData("value")]
    [InlineData("in")]
    [InlineData("object")]
    [InlineData("params")]
    [InlineData("file")]
    [InlineData("group")]
    public void A_csharp_reserved_word_is_refused_on_either_side_naming_the_word(string word)
    {
      var asInput = Refusal(CsType, new[] { word, "q" }, new[] { "r" });
      Assert.Equal(ToolErrors.InvalidArgs, asInput.Code);
      Assert.Contains("\"" + word + "\" cannot be an input name (component \"s\")", asInput.Message);
      Assert.Contains("reserved word " + word, asInput.Message);
      Assert.Contains("Pick another name", asInput.Message);

      var asOutput = Refusal(CsType, new[] { "p", "q" }, new[] { word });
      Assert.Contains("\"" + word + "\" cannot be an output name", asOutput.Message);
    }

    // ---- where C#'s reserved words come from ------------------------------------------------------------------------------------

    [Fact]
    public void What_rhino_answers_is_the_list_and_it_says_so()
    {
      var (words, source) = CSharpReservedWords.Resolve(() => new[] { "zork", "value", "" });
      Assert.Equal(CSharpReservedWords.FromRhino, source);
      Assert.Contains("zork", words);
      Assert.Contains("value", words);
      Assert.DoesNotContain("class", words);   // Rhino's answer is the list, not an addition to Dwight's
      Assert.DoesNotContain("", words);
      Assert.DoesNotContain("ZORK", words);    // C# minds case
    }

    [Fact]
    public void A_rhino_that_does_not_answer_gets_the_fallback_and_it_says_so()
    {
      Func<IEnumerable<string>>[] silent = { null, () => null, () => new string[0], () => new[] { "" }, () => throw new InvalidOperationException("no language") };
      foreach (var read in silent)
      {
        var (words, source) = CSharpReservedWords.Resolve(read);
        Assert.Equal(CSharpReservedWords.FromList, source);
        Assert.Same(CSharpReservedWords.Fallback, words);
      }
    }

    [Theory]
    [InlineData("class")]
    [InlineData("string")]
    [InlineData("out")]
    [InlineData("value")]   // the bench's K3: not a C# reserved word, and Rhino refuses it
    [InlineData("file")]    // on Rhino's list and no C# reserved word, as with the rest of these
    [InlineData("group")]
    [InlineData("set")]
    [InlineData("from")]
    [InlineData("args")]
    public void The_fallback_holds_what_the_bench_read_from_rhino(string word) => Assert.Contains(word, CSharpReservedWords.Fallback);

    /// <summary>The list as it was read from Rhino 8 on the fourteenth build, 2 November 2026: 120 words. A change here is a new measurement, and the PR says so.</summary>
    [Fact]
    public void The_fallback_is_the_measured_list()
    {
      Assert.Equal(120, CSharpReservedWords.Fallback.Count);
      Assert.DoesNotContain("Point3d", CSharpReservedWords.Fallback);   // the bench's K4 landed
      Assert.DoesNotContain("Value", CSharpReservedWords.Fallback);     // C# minds case
    }

    /// <summary>The route, read from the IL of ScriptVariableParam.ValidateVariableName. Reflection over Rhino's own assembly, which is not here, so this reads the text; the bench says which source answered.</summary>
    [Fact]
    public void The_read_goes_the_way_rhinos_own_validation_goes()
    {
      var s = File.ReadAllText(Path.Combine(RepoRoot(), "Dwight", "Agent", "BuildScript.cs"));
      foreach (var part in new[] { "\"Rhino.Runtime.Code.RhinoCode\"", "GetProperty(\"Languages\"", "GetProperty(\"CSharp\"", "m.Name == \"QueryLatest\"", "GetProperty(\"Support\")", "GetProperty(\"Syntax\")", "GetProperty(\"Keywords\")" })
        Assert.Contains(part, s);
      Assert.Contains("language == CSharp && CSharpReservedWords.Contains(name)", s);
      Assert.Contains("if (resolved.Source == FromRhino) _read = resolved.Words;", s);   // only a read that succeeds is kept
    }

    /// <summary>The bench's L1, L2 and L4: Rhino only warned, and output class stayed empty, because no source can assign it.</summary>
    [Theory]
    [InlineData("lambda")]
    [InlineData("class")]
    [InlineData("None")]
    [InlineData("in")]
    [InlineData("def")]
    public void A_python_keyword_is_refused_on_either_side_naming_the_word(string word)
    {
      var asInput = Refusal(PyType, new[] { word, "q" }, new[] { "r" });
      Assert.Equal(ToolErrors.InvalidArgs, asInput.Code);
      Assert.Contains("\"" + word + "\" cannot be an input name", asInput.Message);
      Assert.Contains(word + " is a Python keyword", asInput.Message);

      Assert.Contains("\"" + word + "\" cannot be an output name", Refusal(PyType, new[] { "p", "q" }, new[] { word }).Message);
    }

    /// <summary>The bench's Y5: it ran, beside the message output, as two outputs of one name, and a wire names a port.</summary>
    [Theory]
    [InlineData(PyType, "out")]
    [InlineData(PyType, "OUT")]
    [InlineData(CsType, "out")]
    public void An_output_called_out_is_refused_because_every_script_has_one(string type, string name)
    {
      var e = Refusal(type, new[] { "p", "q" }, new[] { name });
      Assert.Equal(ToolErrors.InvalidArgs, e.Code);
      Assert.Contains("cannot be an output name", e.Message);
      Assert.Contains("message output called out", e.Message);
    }

    /// <summary>The door a region's script goes through first, so the refusal comes before the person is shown a card.</summary>
    [Fact]
    public void The_card_refuses_the_same_names_and_takes_the_same_names()
    {
      var refused = new JsonArray(new JsonObject { ["region"] = "A1", ["type"] = CsType, ["source"] = Mix, ["inputs"] = Names("a", "b", "c", "d") });
      Assert.False(ScriptsAsk.Resolve(refused, new[] { "A1" }, out _, out var e));
      Assert.Contains("\"a\" cannot be both an input and an output", e.Message);

      var reserved = new JsonArray(new JsonObject { ["region"] = "A1", ["type"] = CsType, ["source"] = Mix, ["inputs"] = Names("value"), ["outputs"] = Names("total") });
      Assert.False(ScriptsAsk.Resolve(reserved, new[] { "A1" }, out _, out e));
      Assert.Contains("reserved word value", e.Message);

      var taken = new JsonArray(new JsonObject { ["region"] = "A1", ["type"] = CsType, ["source"] = Mix, ["inputs"] = Names("a", "b", "c", "d"), ["outputs"] = Names("total") });
      Assert.True(ScriptsAsk.Resolve(taken, new[] { "A1" }, out var scripts, out e), e?.Message);
      Assert.Equal(new[] { "a", "b", "c", "d" }, Assert.Single(scripts).Inputs);

      var python = new JsonArray(new JsonObject { ["region"] = "A1", ["type"] = PyType, ["source"] = Py, ["inputs"] = Names("a", "x") });
      Assert.True(ScriptsAsk.Resolve(python, new[] { "A1" }, out _, out e), e?.Message);
    }

    // ---- the pure rules -----------------------------------------------------------------------------------------------------

    [Fact]
    public void The_rule_compares_the_ports_as_they_will_be_built()
    {
      var none = new string[0];
      Assert.Null(BuildScriptTypes.NameOnBothSides(none, none));
      Assert.Null(BuildScriptTypes.NameOnBothSides(null, null));
      Assert.Null(BuildScriptTypes.NameOnBothSides(new[] { "x", "y" }, new[] { "a" }));
      Assert.Null(BuildScriptTypes.NameOnBothSides(new[] { "a", "b" }, new[] { "total" }));
      Assert.Equal("a", BuildScriptTypes.NameOnBothSides(new[] { "a", "b" }, none));
      Assert.Equal("x", BuildScriptTypes.NameOnBothSides(none, new[] { "x" }));
      Assert.Equal("y", BuildScriptTypes.NameOnBothSides(null, new[] { "q", "y" }));
      Assert.Equal("q", BuildScriptTypes.NameOnBothSides(new[] { "p", "q" }, new[] { "q", "r" }));
      Assert.Equal("p", BuildScriptTypes.NameOnBothSides(new[] { "p", "q" }, new[] { "q", "p" }));   // the first input that clashes
    }

    [Fact]
    public void A_language_keyword_is_only_refused_in_its_own_language()
    {
      Assert.NotNull(BuildScriptTypes.NameRefusal(BuildScriptTypes.CSharp, "input", "string"));
      Assert.Null(BuildScriptTypes.NameRefusal(BuildScriptTypes.Python, "input", "string"));
      Assert.NotNull(BuildScriptTypes.NameRefusal(BuildScriptTypes.Python, "input", "lambda"));
      Assert.Null(BuildScriptTypes.NameRefusal(BuildScriptTypes.CSharp, "input", "lambda"));
      Assert.Null(BuildScriptTypes.NameRefusal(BuildScriptTypes.Python, "input", "out"));
      Assert.NotNull(BuildScriptTypes.NameRefusal(BuildScriptTypes.Python, "output", "out"));
    }

    /// <summary>What the model reads about these names, field by field, each held on the parser's side by the tests above (#315).</summary>
    [Fact]
    public void The_descriptors_say_what_the_parser_does()
    {
      var props = BuildSpecSchema.Components()["items"]["properties"];
      foreach (var field in new[] { "inputs", "outputs" })
      {
        var said = props[field]["description"].GetValue<string>();
        Assert.Contains("x, y and a are names like any other", said);
        Assert.Contains("a keyword of the script's language", said);
        Assert.DoesNotContain("none may be", said);
      }
      var outputs = props["outputs"]["description"].GetValue<string>();
      Assert.Contains("In a C# script one name cannot be both an input and an output", outputs);
      Assert.Contains("a C# input called a needs \"outputs\" declared", outputs);
      Assert.Contains("or out is refused", outputs);
    }

    // ---- the live order, read from the source ---------------------------------------------------------------------------

    /// <summary>GhBuildRegion is GH-coupled and not linked here, so this reads its SOURCE. The bound is stated: it proves the text's order, and the bench rows prove the component.</summary>
    [Fact]
    public void The_live_check_runs_after_the_defaults_are_removed_and_before_the_declared_ports_are_added()
    {
      var s = GhBuildRegionSource();
      var apply = s.IndexOf("private static void ApplyScript(", StringComparison.Ordinal);
      Assert.True(apply > 0);
      var remove = s.IndexOf("ScriptComponentShim.RemoveDefaults(", apply, StringComparison.Ordinal);
      var read = s.IndexOf("var haveInputs = new HashSet<string>(", apply, StringComparison.Ordinal);
      var check = s.IndexOf("still has a port called", apply, StringComparison.Ordinal);
      var append = s.IndexOf("ScriptComponentShim.Append(", apply, StringComparison.Ordinal);
      Assert.True(remove > 0 && read > 0 && check > 0 && append > 0);
      Assert.True(remove < read, "the live ports are read after the defaults have gone");
      Assert.True(read < check && check < append, "the check stands before the declared ports are added");
      Assert.DoesNotContain("is born with a port called", s);
    }

    /// <summary>A name across the sides lands in Python (the bench's Y4), so the live check compares each side with itself.</summary>
    [Fact]
    public void The_live_check_compares_side_by_side()
    {
      var s = GhBuildRegionSource();
      var apply = s.IndexOf("private static void ApplyScript(", StringComparison.Ordinal);
      Assert.Contains("(c.Inputs ?? new List<string>()).Where(haveInputs.Contains)", s.Substring(apply));
      Assert.Contains("(c.Outputs ?? new List<string>()).Where(haveOutputs.Contains)", s.Substring(apply));
    }

    // ---- the receipt ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_receipt_names_the_ports_as_declared()
    {
      var facts = new ScriptBuildFacts
      {
        Ref = "s", Name = "mix", Language = BuildScriptTypes.CSharp, Errors = new string[0],
        Inputs = new[] { "x", "y" }, UnwiredInputs = new[] { "y" },
        Outputs = new[] { new ScriptOutputRead { Port = "a", Items = 1, NonNull = 1 } },
      };
      var outcome = ScriptBuildVerdict.Outcome(facts);
      Assert.Equal(ScriptBuildOutcome.Produced, outcome);
      var note = ScriptBuildVerdict.Note(facts, outcome);
      Assert.Contains("produced a: 1 item", note);
      Assert.Contains("nothing is wired to its input y", note);
    }
  }
}
