using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Dwight.Agent;
using Dwight.Llm;
using Dwight.Simplify;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// Brief AA, after the first night on the scripts path (U78 to U83): a declared script has only its declared ports (U78), the
  /// plugin states the Rhino 8 conventions and how to choose a language (U79, U80), the planner is told to plan fewer regions
  /// (U81), a cited reply keeps its line structure (U82), and a script whose ports were renamed between the card and the build
  /// is built with the card's ports and the wires mapped by position (U83).
  ///
  /// Every sentence the plugin tells the model here is a claim about our own behaviour, so each is held against the behaviour
  /// it describes (#315). What no test can witness is named where it applies: the live component is only seen in Rhino.
  /// </summary>
  [Collection(StreamFixtures.CaptureWindow)]
  public class ScriptsQualityTests
  {
    private static readonly string[] Installed = { "Number Slider", "Panel", "Python 3 Script", "C# Script" };
    private static bool TypeExists(string t) => Installed.Contains(Dwight.Components.ComponentMatchCore.BareName(t), StringComparer.OrdinalIgnoreCase);
    private static IReadOnlyList<string> Nearest(string t) => Installed;
    private static IdState Alive(int id) => IdState.Alive;

    // The ports as the live graph reports them: a script is born with x, y and a (and the "out" message output, which is not
    // a script variable and which the shim never removes).
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

    private const string PySource = "pts = [rg.Point3d(i, 0, 0) for i in range(int(length))]";

    // ======================================================================================================
    // U78 - a declared script has only the ports it declares
    // ======================================================================================================

    private static string GhBuildRegionSource() =>
      File.ReadAllText(Path.Combine(RepoRoot(), "Dwight", "Agent", "GhBuildRegion.cs"));

    /// <summary>The order the brief pins: the defaults are removed, then the declared ports are added (ApplyScript), then the
    /// hints are chosen, then the component enters the document. GhBuildRegion is GH-coupled and not linked here, so this reads
    /// its SOURCE (the bound is stated: it proves the text's order, not the behaviour; the live component is checked in Rhino).</summary>
    [Fact]
    public void U78_the_defaults_are_removed_then_the_declared_ports_added_then_the_hints_then_AddObject()
    {
      var s = GhBuildRegionSource();
      var apply = s.IndexOf("private static void ApplyScript(", StringComparison.Ordinal);
      Assert.True(apply > 0);
      var remove = s.IndexOf("ScriptComponentShim.RemoveDefaults(", apply, StringComparison.Ordinal);
      var append = s.IndexOf("ScriptComponentShim.Append(", apply, StringComparison.Ordinal);
      Assert.True(remove > 0 && append > 0);
      Assert.True(remove < append, "the defaults go before the declared ports are added");

      var applyCall = s.IndexOf("ApplyScript(obj, c);", StringComparison.Ordinal);
      var hint = s.IndexOf("SelectInputHints(", StringComparison.Ordinal);
      var add = s.IndexOf("d.AddObject(obj, false", StringComparison.Ordinal);
      Assert.True(applyCall > 0 && applyCall < hint && hint < add);
    }

    [Fact]
    public void U78_the_removal_is_by_declared_side_and_only_for_a_script_that_carries_source()
    {
      var declares = new BuildComponent { Ref = "s", Type = BuildScriptTypes.PythonTypeName, Source = "a = 1" };
      declares.Inputs.Add("length"); declares.Outputs.Add("pts");
      Assert.True(BuildScriptTypes.ReplacesDefaultInputs(declares));
      Assert.True(BuildScriptTypes.ReplacesDefaultOutputs(declares));

      var onlyOut = new BuildComponent { Ref = "s", Type = BuildScriptTypes.PythonTypeName, Source = "a = 1" };
      onlyOut.Outputs.Add("pts");
      Assert.False(BuildScriptTypes.ReplacesDefaultInputs(onlyOut));   // declares no inputs: it keeps x and y
      Assert.True(BuildScriptTypes.ReplacesDefaultOutputs(onlyOut));

      var neither = new BuildComponent { Ref = "s", Type = BuildScriptTypes.PythonTypeName, Source = "a = x + y" };
      Assert.False(BuildScriptTypes.ReplacesDefaultInputs(neither));
      Assert.False(BuildScriptTypes.ReplacesDefaultOutputs(neither));

      Assert.False(BuildScriptTypes.ReplacesDefaultInputs(new BuildComponent { Ref = "p", Type = "Panel" }));   // not a script
      // the shim's defaults are the ones the removal names
      Assert.Equal(new[] { "x", "y" }, BuildScriptTypes.DefaultInputs);
      Assert.Equal(new[] { "a" }, BuildScriptTypes.DefaultOutputs);
    }

    [Fact]
    public void U78_a_wire_resolves_against_the_declared_ports_only_so_the_removed_defaults_cannot_be_named()
    {
      Func<JsonArray, ToolResult> parse = wires =>
      {
        Parse(wires, out _, out var e,
          Comp("s", "Number Slider"),
          Comp("c", "Python 3 Script", o => { o["source"] = PySource; o["inputs"] = new JsonArray("length", "gap"); o["outputs"] = new JsonArray("pts", "n"); }));
        return e;
      };

      Assert.Null(parse(new JsonArray(Wire("s", null, "c", "length"))));          // control: the declared input
      var gone = parse(new JsonArray(Wire("s", null, "c", "x")));                  // the removed default
      Assert.NotNull(gone);
      Assert.Contains("No input named \"x\"", gone.Message);

      // outputs: "a" is gone, "pts" is there, and the message output "out" is not a default and stays
      Func<string, ToolResult> from = port =>
      {
        Parse(new JsonArray(Wire("c", port, "p", null)), out _, out var e,
          Comp("p", "Panel"),
          Comp("c", "Python 3 Script", o => { o["source"] = PySource; o["inputs"] = new JsonArray("length", "gap"); o["outputs"] = new JsonArray("pts", "n"); }));
        return e;
      };
      Assert.Null(from("pts"));
      Assert.Null(from("out"));
      Assert.Contains("No output named \"a\"", from("a").Message);
    }

    [Fact]
    public void U78_a_script_that_declares_only_outputs_keeps_x_and_y_and_one_that_declares_nothing_keeps_all_three()
    {
      Parse(new JsonArray(Wire("s", null, "c", "x")), out _, out var e,
        Comp("s", "Number Slider"),
        Comp("c", "Python 3 Script", o => { o["source"] = PySource; o["outputs"] = new JsonArray("pts"); }));
      Assert.Null(e);

      Parse(new JsonArray(Wire("c", "a", "p", null)), out _, out var e2,
        Comp("p", "Panel"),
        Comp("c", "Python 3 Script", o => o["source"] = "a = x + y"));
      Assert.Null(e2);
    }

    [Fact]
    public void U78_the_card_and_the_verdict_count_only_the_declared_inputs_as_unwired()
    {
      Assert.True(Parse(new JsonArray(Wire("s", null, "c", "length")), out var spec, out var e,
        Comp("s", "Number Slider"),
        Comp("c", "Python 3 Script", o => { o["source"] = PySource; o["inputs"] = new JsonArray("length", "gap"); })), e?.Message);

      var script = Assert.Single(SpotScripts.Of(spec, Ports));
      Assert.Equal(new[] { "gap" }, script.UnwiredInputs);         // never "x, y": they are not on the component
      Assert.False(script.AllInputsUnwired);

      // control: a script that declares no inputs still reports the defaults
      Assert.True(Parse(null, out var plain, out _, Comp("c", "Python 3 Script", o => o["source"] = "a = x + y")));
      Assert.Equal(new[] { "x", "y" }, Assert.Single(SpotScripts.Of(plain, Ports)).UnwiredInputs);
    }

    [Fact]
    public void U78_the_descriptors_say_declared_ports_replace_the_defaults()
    {
      var schema = BuildSpecSchema.Components().ToJsonString();
      Assert.Contains("REPLACE x and y", schema);
      Assert.Contains("REPLACE a", schema);
      Assert.DoesNotContain("APPENDED after x and y", schema);

      var scripts = AgentHostTools.All()[0].InputSchema["properties"]["scripts"]["items"]["properties"];
      Assert.Contains("REPLACE x and y", scripts["inputs"]["description"].GetValue<string>());
      Assert.Contains("REPLACE a", scripts["outputs"]["description"].GetValue<string>());
      Assert.Contains("x, y and a are names like any other", schema);   // Brief EA: held on the parser's side in the conventions test
      Assert.DoesNotContain("none may be", schema);
    }

    // ======================================================================================================
    // U79, U80, U81 - the facts
    // ======================================================================================================

    [Fact]
    public void U79_the_conventions_fact_follows_how_to_carry_a_script_in_scripts_and_auto_and_is_pinned()
    {
      foreach (var style in new[] { "scripts", "auto" })
      {
        var f = BuildStyle.Fact(style);
        var carry = f.IndexOf("A script that was not on the card is refused.", StringComparison.Ordinal);
        var conventions = f.IndexOf(BuildStyle.ScriptConventions, StringComparison.Ordinal);
        Assert.True(carry > 0 && conventions > carry, style + ": the conventions come after how to carry a script");
      }
      Assert.DoesNotContain("Script conventions", BuildStyle.Fact("components"));

      var c = BuildStyle.ScriptConventions;
      Assert.Contains("In a Python 3 script: each declared output is a variable you assign; assign a list to emit a list, a single value to emit one item;", c);
      Assert.Contains("emit Rhino.Geometry types (Point3d, Line, Curve, Brep), never tuples or plain numbers for geometry;", c);
      Assert.Contains("(a Line-fed input is a Rhino.Geometry.Line)", c);
      Assert.Contains("`import Rhino.Geometry as rg` is available.", c);
      Assert.Contains("In a C# script: the same ports are the method's parameters", c);
      Assert.Contains("RhinoCommon types", c);
      Assert.DoesNotContain(((char)0x2014).ToString(), c);
      Assert.DoesNotContain(((char)0x2013).ToString(), c);
    }

    /// <summary>The conventions fact makes claims about Dwight; each is held against the code that does it.</summary>
    [Fact]
    public void U79_the_claims_the_conventions_fact_makes_about_dwight_are_true_of_the_code()
    {
      var c = BuildStyle.ScriptConventions;

      // "a script that declares inputs has exactly those inputs, in place of x and y ... declares outputs ... in place of a"
      Assert.Contains("declares inputs has exactly those inputs, in place of x and y", c);
      Assert.Contains("declares outputs has exactly those outputs, in place of a", c);
      // ... "x, y and a are names like any other": the parser takes them on the side that replaces them (Brief EA)
      Assert.Contains("x, y and a are names like any other", c);
      foreach (var free in new[] { "x", "y", "X" })
      {
        Parse(null, out _, out var e, Comp("c", "Python 3 Script", o => { o["source"] = PySource; o["inputs"] = new JsonArray(free); }));
        Assert.Null(e);
      }
      Parse(null, out _, out var ea, Comp("c", "Python 3 Script", o => { o["source"] = PySource; o["outputs"] = new JsonArray("a"); }));
      Assert.Null(ea);
      // ... "In a C# script one name cannot be both ... so a C# input called a needs its outputs declared"
      Assert.Contains("In a C# script one name cannot be both an input and an output", c);
      Assert.Contains("a C# input called a needs its outputs declared", c);
      Parse(null, out _, out var alone, Comp("c", "C# Script", o => { o["source"] = PySource; o["inputs"] = new JsonArray("a"); }));
      Assert.Contains("cannot be both an input and an output", alone.Message);
      Parse(null, out _, out var declared, Comp("c", "C# Script", o => { o["source"] = PySource; o["inputs"] = new JsonArray("a"); o["outputs"] = new JsonArray("pts"); }));
      Assert.Null(declared);
      // ... "A keyword of the script's language cannot be a port, and in C# that includes words such as value, file, group and set; nor can an output be called out"
      Assert.Contains("A keyword of the script's language cannot be a port, and in C# that includes words such as value, file, group and set; nor can an output be called out", c);
      foreach (var word in new[] { "value", "file", "group", "set" })
      {
        Parse(null, out _, out var said, Comp("c", "C# Script", o => { o["source"] = PySource; o["inputs"] = new JsonArray(word); }));
        Assert.Contains("reserved word " + word, said.Message);
      }
      Parse(null, out _, out var kw, Comp("c", "Python 3 Script", o => { o["source"] = PySource; o["inputs"] = new JsonArray("lambda"); }));
      Assert.Contains("Python keyword", kw.Message);
      Parse(null, out _, out var val, Comp("c", "C# Script", o => { o["source"] = PySource; o["inputs"] = new JsonArray("value"); }));
      Assert.Contains("reserved word value", val.Message);
      Parse(null, out _, out var msg, Comp("c", "Python 3 Script", o => { o["source"] = PySource; o["outputs"] = new JsonArray("out"); }));
      Assert.Contains("message output called out", msg.Message);
      // ... "a script that declares neither keeps x, y and a": see U78_a_script_that_declares_only_outputs_...

      // "an input arrives typed by its hint where what feeds it has a known type (a Line-fed input is a Rhino.Geometry.Line)"
      Assert.Equal("Line", ScriptTypeMap.HintFor("Line", ScriptLanguage.Python3));
      Assert.Equal("Line", ScriptTypeMap.HintFor("Line", ScriptLanguage.CSharp));
      Assert.Equal("Line", ScriptTypeMap.HintsByInput(new[] { new KeyValuePair<int, string>(0, "Line") }, ScriptLanguage.Python3)[0]);
      // ... "otherwise as generic data": a type with no hint, or nothing known, selects nothing
      Assert.Null(ScriptTypeMap.HintFor("Generic Data", ScriptLanguage.Python3));
      Assert.Empty(ScriptTypeMap.HintsByInput(new[] { new KeyValuePair<int, string>(0, "Generic Data") }, ScriptLanguage.Python3));

      // "In a C# script: the same ports are the method's parameters": a bare RunScript method is the shape the component takes,
      // and a body-only method is wrapped in the Script_Instance class around it
      var wrapped = UsingNormalizer.ToComponentTemplate("private void RunScript(double length, ref object pts) { pts = length; }");
      Assert.Contains(UsingNormalizer.TemplateClass, wrapped);
      Assert.Contains("RunScript(double length, ref object pts)", wrapped);
      Assert.Contains("using Rhino.Geometry;", wrapped);   // "RhinoCommon types are in scope"
    }

    [Fact]
    public void U80_the_language_sentence_is_pinned_in_scripts_and_auto_and_absent_from_components()
    {
      const string sentence = "Use Python 3 unless the user asks for C# or the work needs typed RhinoCommon APIs or speed on large loops; " +
                              "C# compiles on placement and a compile error is a runtime message you will see in the verdict.";
      Assert.Equal(sentence, BuildStyle.LanguageChoice);
      Assert.EndsWith(sentence, BuildStyle.Fact("scripts"));
      Assert.EndsWith(sentence, BuildStyle.Fact("auto"));
      Assert.DoesNotContain("Use Python 3 unless", BuildStyle.Fact("components"));
    }

    /// <summary>The sentence says C# can be placed and that its compile error reaches the verdict. Both held against the code.</summary>
    [Fact]
    public void U80_a_csharp_script_is_placeable_and_its_compile_error_is_carried_into_the_verdict()
    {
      // placeable: the type is a script type, takes source, and the one parse accepts it
      Assert.Equal(BuildScriptTypes.CSharp, BuildScriptTypes.LanguageOf(BuildScriptTypes.CSharpTypeName));
      Assert.Contains(BuildScriptTypes.CSharpTypeName, BuildStyle.Fact("scripts"));
      Assert.True(Parse(null, out var spec, out var e, Comp("c", "C# Script",
        o => { o["source"] = "private void RunScript(double length, ref object pts) { pts = length; }"; o["inputs"] = new JsonArray("length"); o["outputs"] = new JsonArray("pts"); })), e?.Message);
      Assert.True(spec.Components.Single().CarriesSource);

      // the compile error is carried verbatim to the model, under the compiler's header
      var facts = new ScriptBuildFacts
      {
        Ref = "c", Name = "Loft", Language = BuildScriptTypes.CSharp,
        Errors = new[] { "error CS0103: The name 'lenght' does not exist in the current context" },
        Outputs = new[] { new ScriptOutputRead { Port = "pts", Items = 1, NonNull = 0 } },
        Inputs = new[] { "length" }, UnwiredInputs = new string[0],
      };
      var judged = ScriptBuildVerdict.Judge(new[] { facts }, null);
      Assert.False(judged.Stands);
      Assert.Contains(CompileRepair.CompiledHeader, judged.Refusal);
      Assert.Contains("CS0103", judged.Refusal);
      Assert.Contains("'lenght' does not exist", judged.Refusal);
    }

    [Fact]
    public void U81_the_scripts_fact_tells_the_planner_to_plan_fewer_larger_regions_and_so_does_auto_where_the_logic_is_algorithmic()
    {
      Assert.Equal("Plan fewer, larger regions: one inputs region, one script region per algorithmic stage, one outputs region; do not split a loop across regions.",
        BuildStyle.FewerRegions);
      Assert.Contains(BuildStyle.FewerRegions, BuildStyle.Fact("scripts"));

      var auto = BuildStyle.Fact("auto");
      Assert.Contains("Where the logic is algorithmic, plan fewer, larger regions: one inputs region, one script region per algorithmic stage, " +
                      "one outputs region; do not split a loop across regions.", auto);
      Assert.DoesNotContain("fewer", BuildStyle.Fact("components"));
    }

    // ======================================================================================================
    // U82 - consecutive text blocks are joined verbatim
    // ======================================================================================================

    private const string TableSplit = "anthropic-HANDMADE-cited-table-split-across-blocks.sse";

    [Fact]
    public void U82_a_text_block_split_by_a_citation_inside_a_table_row_is_joined_with_nothing_between_the_halves()
    {
      var r = AnthropicReplyStream.Read(StreamFixtures.Reader(StreamFixtures.Read(TableSplit)));

      Assert.Equal(new[] { "text", "text", "text" }, r.Blocks.Select(b => b.Kind));
      Assert.Null(r.Blocks[0].CitationsJson);
      Assert.NotNull(r.Blocks[1].CitationsJson);   // the middle of a row carries the citation

      Assert.Equal("| Part | Length |\n|---|---|\n| Bridge | 12 m |\n| Hall | 30 m |\n\nFirst paragraph.\n\nSecond paragraph.", r.Text);
    }

    [Fact]
    public void U82_two_paragraphs_in_one_block_are_unchanged_and_blocks_something_separates_keep_their_one_newline()
    {
      var one = new LlmResponse();
      one.Blocks.Add(LlmBlock.TextBlock("First.\n\nSecond."));
      Assert.Equal("First.\n\nSecond.", one.Text);

      var parted = new LlmResponse();
      parted.Blocks.Add(LlmBlock.TextBlock("I'll search."));
      parted.Blocks.Add(LlmBlock.ServerToolUse("srv_1", "{}"));
      parted.Blocks.Add(LlmBlock.TextBlock("Here is what I found."));
      Assert.Equal("I'll search.\nHere is what I found.", parted.Text);

      Assert.Equal("", new LlmResponse().Text);
      var consecutive = new LlmResponse();
      consecutive.Blocks.Add(LlmBlock.TextBlock("a"));
      consecutive.Blocks.Add(LlmBlock.TextBlock("b"));
      consecutive.Blocks.Add(LlmBlock.TextBlock(" c"));
      Assert.Equal("ab c", consecutive.Text);
    }

    // ======================================================================================================
    // U83 - renamed ports between the card and the build are mapped by position when the counts match
    // ======================================================================================================

    private const string CardSource = "import math\npts = [rg.Point3d(i, 0, 0) for i in range(int(L))]\nn = len(pts)";

    private static ApprovedScript Card(string[] inputs, string[] outputs)
    {
      var a = new ApprovedScript { Region = "B1", Language = BuildScriptTypes.Python, Source = CardSource };
      a.Inputs.AddRange(inputs); a.Outputs.AddRange(outputs);
      return a;
    }

    private static BuildSpec Build(string source, string[] inputs, string[] outputs, params BuildWire[] wires)
    {
      var spec = new BuildSpec { Region = "B1" };
      var c = new BuildComponent { Ref = "t", Type = BuildScriptTypes.PythonTypeName, Source = source };
      c.Inputs.AddRange(inputs); c.Outputs.AddRange(outputs);
      spec.Components.Add(c);
      spec.Wires.AddRange(wires);
      return spec;
    }

    [Fact]
    public void U83_the_observed_serpentine_case_with_different_counts_is_still_refused_naming_both_lists()
    {
      // the card declared L, H, pk, n; the build sent length, peak, pos - the counts differ, so this is not a rename
      var spec = Build("pts = []", new[] { "length" }, new[] { "peak", "pos" });
      var refused = BuildScriptGate.Adopt(spec, false, new[] { Card(new[] { "L", "H" }, new[] { "pk", "n" }) }, out var subs);

      Assert.NotNull(refused);
      Assert.Contains("declares inputs L, H; this build declares length", refused.Message);
      Assert.Contains("Nothing was built", refused.Message);
      Assert.Empty(subs);
      Assert.Equal("pts = []", spec.Components[0].Source);   // a refusal rewrites nothing
      Assert.Equal(new[] { "length" }, spec.Components[0].Inputs);
    }

    [Fact]
    public void U83_the_same_script_with_the_same_counts_and_other_names_builds_the_cards_ports_and_maps_the_wires_by_position()
    {
      var spec = Build("pts = [rg.Point3d(i, 0, 0) for i in range(int(length))]", new[] { "length", "height" }, new[] { "peak", "pos" },
        new BuildWire { FromRef = "s1", ToRef = "t", ToParam = "length" },
        new BuildWire { FromRef = "s2", ToRef = "t", ToParam = "height" },
        new BuildWire { FromRef = "t", FromParam = "pos", ToRef = "p" },
        new BuildWire { FromRef = "t", FromParam = "peak", ToRef = "q" },
        new BuildWire { FromRef = "other", FromParam = "pos", ToRef = "p" });   // a wire on another component is not touched
      spec.Reconnects.Add(new BuildReconnect { ToId = 7, ToParam = "k", FromRef = "t", FromParam = "peak" });

      var refused = BuildScriptGate.Adopt(spec, false, new[] { Card(new[] { "L", "H" }, new[] { "pk", "n" }) }, out var subs);

      Assert.Null(refused);
      var c = spec.Components[0];
      Assert.Equal(new[] { "L", "H" }, c.Inputs);                // the card's ports
      Assert.Equal(new[] { "pk", "n" }, c.Outputs);
      Assert.Equal(CardSource, c.Source);                          // and the card's source, which uses the card's names

      Assert.Equal("L", spec.Wires[0].ToParam);                    // the i-th declared input is the i-th card input
      Assert.Equal("H", spec.Wires[1].ToParam);
      Assert.Equal("n", spec.Wires[2].FromParam);                  // pos was the second output
      Assert.Equal("pk", spec.Wires[3].FromParam);                 // peak was the first
      Assert.Equal("pos", spec.Wires[4].FromParam);                // another component's wire is left alone
      Assert.Equal("pk", spec.Reconnects[0].FromParam);

      var sub = Assert.Single(subs);
      Assert.Equal(4, sub.PortsMappedByPosition.Count);
      Assert.Contains("ports taken from the card; your names were mapped by position", sub.Sentence());
      Assert.Contains("length as L", sub.Sentence());
      Assert.Equal("L", sub.ToJson()["portsMappedByPosition"]["length"].GetValue<string>());
    }

    [Fact]
    public void U83_a_rename_in_a_different_order_is_by_position_not_by_name_and_sets_that_already_agree_are_untouched()
    {
      // the names agree as sets (order only): the U73 path, no mapping, no note about names
      var sameSet = Build(CardSource, new[] { "H", "L" }, new[] { "n", "pk" },
        new BuildWire { FromRef = "s", ToRef = "t", ToParam = "H" });
      Assert.Null(BuildScriptGate.Adopt(sameSet, false, new[] { Card(new[] { "L", "H" }, new[] { "pk", "n" }) }, out var subs));
      Assert.Equal(new[] { "L", "H" }, sameSet.Components[0].Inputs);
      Assert.Equal("H", sameSet.Wires[0].ToParam);                 // by NAME: H is still H
      Assert.Empty(Assert.Single(subs).PortsMappedByPosition);
      Assert.Null(subs[0].ToJson()["portsMappedByPosition"]);
    }

    [Fact]
    public void U83_two_candidates_for_one_build_are_not_guessed_between()
    {
      // two cards of the same shape in the same region and language: which one the build means is not forced, so it is refused
      var spec = Build("pts = []", new[] { "a1" }, new[] { "o1" });
      var refused = BuildScriptGate.Adopt(spec, false,
        new[] { Card(new[] { "L" }, new[] { "pk" }), Card(new[] { "M" }, new[] { "qk" }) }, out var subs);
      Assert.NotNull(refused);
      Assert.Empty(subs);
    }

    [Fact]
    public void U83_the_existing_U73_cases_still_hold()
    {
      // a different region, a different language, or different counts are refused as they were
      var otherRegion = Build(CardSource, new[] { "a" }, new[] { "b" });
      otherRegion.Region = "B2";
      Assert.NotNull(BuildScriptGate.Adopt(otherRegion, false, new[] { Card(new[] { "L" }, new[] { "pk" }) }, out _));

      var otherCounts = Build(CardSource, new[] { "a" }, new[] { "b", "c" });
      Assert.NotNull(BuildScriptGate.Adopt(otherCounts, false, new[] { Card(new[] { "L" }, new[] { "pk" }) }, out _));

      var exact = Build(CardSource, new[] { "L" }, new[] { "pk" });
      Assert.Null(BuildScriptGate.Adopt(exact, false, new[] { Card(new[] { "L" }, new[] { "pk" }) }, out var none));
      Assert.Empty(none);
    }

    private static string RepoRoot()
    {
      var dir = new DirectoryInfo(AppContext.BaseDirectory);
      while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Dwight.sln"))) dir = dir.Parent;
      Assert.True(dir != null, "the repository root (Dwight.sln) could not be found");
      return dir.FullName;
    }
  }
}
