using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Dwight.Agent;
using Dwight.Simplify;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// Brief CP: a script declares each input's access. On the lamp a Bambu script took 42,828 G-code lines on an item input, ran once per line and made 1,070,700 outputs, and nobody could ask for
  /// anything else. The parser, the record, the card and the build are held here; that the real component runs once is the architect's Rhino row.
  /// </summary>
  public class ScriptAccessTests
  {
    private static JsonObject Script(string inputs, string type = BuildScriptTypes.PythonTypeName) =>
      JsonNode.Parse("{\"ref\":\"s\",\"type\":\"" + type + "\",\"source\":\"result = 1\",\"inputs\":" + inputs + ",\"outputs\":[\"result\"]}").AsObject();

    private static bool Parse(JsonObject component, out List<string> inputs, out Dictionary<string, string> access, out ToolResult error) =>
      BuildScriptParser.TryParse(component, component["type"].GetValue<string>(), "s", out _, out inputs, out _, out _, out access, out error);

    // ---- the parser ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("list", "list")]
    [InlineData("tree", "tree")]
    [InlineData("LIST", "list")]
    [InlineData(" tree ", "tree")]
    public void The_two_words_that_differ_from_the_default_are_held(string word, string held)
    {
      Assert.True(Parse(Script("[{\"name\":\"gcode\",\"access\":\"" + word + "\"}]"), out var inputs, out var access, out var error), error?.Message);

      Assert.Equal(new[] { "gcode" }, inputs);
      Assert.Equal(held, access["gcode"]);
    }

    [Theory]
    [InlineData("[{\"name\":\"gcode\",\"access\":\"item\"}]")]
    [InlineData("[{\"name\":\"gcode\"}]")]
    [InlineData("[\"gcode\"]")]
    public void The_default_is_item_however_it_is_said(string inputs)
    {
      Assert.True(Parse(Script(inputs), out var names, out var access, out var error), error?.Message);

      Assert.Equal(new[] { "gcode" }, names);
      Assert.Empty(access);
      Assert.Equal("item", PortAccess.Of(access, "gcode"));
    }

    [Fact]
    public void Names_and_words_mix_and_keep_their_order()
    {
      Assert.True(Parse(Script("[\"temp\",{\"name\":\"gcode\",\"access\":\"list\"},{\"name\":\"rows\",\"access\":\"tree\"}]"), out var names, out var access, out var error), error?.Message);

      Assert.Equal(new[] { "temp", "gcode", "rows" }, names);
      Assert.Equal(new[] { "temp (item)", "gcode (list)", "rows (tree)" }, PortAccess.Shown(names, access));
    }

    [Theory]
    [InlineData("\"quick\"", "\"quick\" is not an access for the input \"gcode\"")]
    [InlineData("\"\"", "is not an access for the input \"gcode\"")]
    [InlineData("3", "3")]
    public void An_unknown_word_is_refused_by_name_with_the_three_words(string word, string says)
    {
      Assert.False(Parse(Script("[{\"name\":\"gcode\",\"access\":" + word + "}]"), out _, out _, out var error));

      Assert.Equal(ToolErrors.InvalidArgs, error.Code);
      Assert.Contains(says, error.Message);
      Assert.Contains("\"item\", \"list\" or \"tree\"", error.Message);
      Assert.Contains("runs once per element", error.Message);   // the refusal carries the rule, so the model can act on it
    }

    [Theory]
    [InlineData("[{\"access\":\"list\"}]")]
    [InlineData("[{\"name\":\"\",\"access\":\"list\"}]")]
    [InlineData("[{\"name\":7}]")]
    public void An_input_with_no_name_is_refused_as_before(string inputs)
    {
      Assert.False(Parse(Script(inputs), out _, out _, out var error));

      Assert.Contains("needs a name", error.Message);
    }

    [Fact]
    public void An_output_takes_no_word()
    {
      var component = JsonNode.Parse("{\"ref\":\"s\",\"type\":\"Python 3 Script\",\"source\":\"r = 1\",\"outputs\":[{\"name\":\"r\",\"access\":\"list\"}]}").AsObject();

      Assert.False(Parse(component, out _, out _, out var error));

      Assert.Contains("needs a name", error.Message);
    }

    [Fact]
    public void The_spec_parser_carries_the_word_to_the_component()
    {
      var args = JsonNode.Parse("{\"components\":[" + Script("[{\"name\":\"gcode\",\"access\":\"list\"}]").ToJsonString() + "]}").AsObject();

      Assert.True(BuildSpecParser.TryParse(args, _ => true, _ => new string[0], _ => IdState.Alive, out var spec, out var error), error?.Message);

      Assert.Equal("list", spec.Components.Single().InputAccess["gcode"]);
    }

    // ---- the record --------------------------------------------------------------------------------------------------

    [Fact]
    public void The_placement_record_round_trips_the_word_through_the_parser()
    {
      var args = JsonNode.Parse("{\"components\":[" + Script("[\"temp\",{\"name\":\"gcode\",\"access\":\"list\"},{\"name\":\"rows\",\"access\":\"tree\"}]").ToJsonString() + "]}").AsObject();
      Assert.True(BuildSpecParser.TryParse(args, _ => true, _ => new string[0], _ => IdState.Alive, out var spec, out _));

      var record = SpotAsk.Serialise(spec);
      Assert.True(BuildSpecParser.TryParse(record, _ => true, _ => new string[0], _ => IdState.Alive, out var again, out var error), error?.Message);

      var c = again.Components.Single();
      Assert.Equal(new[] { "temp", "gcode", "rows" }, c.Inputs);
      Assert.Equal("item", PortAccess.Of(c.InputAccess, "temp"));
      Assert.Equal("list", PortAccess.Of(c.InputAccess, "gcode"));
      Assert.Equal("tree", PortAccess.Of(c.InputAccess, "rows"));
    }

    /// <summary>A script that declares nothing has a record byte for byte what it was, so nothing saved or compared moves.</summary>
    [Fact]
    public void A_record_with_nothing_declared_holds_plain_names()
    {
      Assert.Equal("[\"a\",\"b\"]", PortAccess.Record(new[] { "a", "b" }, null).ToJsonString());
      Assert.Equal("[\"a\",{\"name\":\"b\",\"access\":\"list\"}]", PortAccess.Record(new[] { "a", "b" }, new Dictionary<string, string> { ["b"] = "list" }).ToJsonString());
    }

    [Fact]
    public void A_region_cards_script_round_trips_the_word()
    {
      var raw = new JsonArray(ScriptItem("A1", "[{\"name\":\"gcode\",\"access\":\"list\"},\"temp\"]"));
      Assert.True(ScriptsAsk.Resolve(raw, new[] { "A1" }, out var scripts, out var error), error?.Message);
      Assert.Equal("list", scripts[0].InputAccess["gcode"]);

      var back = ScriptsAsk.FromRecord(ScriptsAsk.Record(scripts)).Single();

      Assert.Equal(new[] { "gcode", "temp" }, back.Inputs);
      Assert.Equal("list", PortAccess.Of(back.InputAccess, "gcode"));
      Assert.Equal("item", PortAccess.Of(back.InputAccess, "temp"));
    }

    private static JsonObject ScriptItem(string region, string inputs) =>
      JsonNode.Parse("{\"region\":\"" + region + "\",\"type\":\"Python 3 Script\",\"source\":\"result = gcode\",\"inputs\":" + inputs + ",\"outputs\":[\"result\"]}").AsObject();

    private static BuildSpec RegionSpec(string inputs, string source = "result = gcode")
    {
      var args = JsonNode.Parse("{\"region\":\"A1\",\"components\":[{\"ref\":\"s\",\"type\":\"Python 3 Script\",\"source\":\"" + source + "\",\"inputs\":" + inputs + ",\"outputs\":[\"result\"]}]}").AsObject();
      Assert.True(BuildSpecParser.TryParse(args, _ => true, _ => new string[0], _ => IdState.Alive, out var spec, out var error), error?.Message);
      return spec;
    }

    private static List<ApprovedScript> Card(string inputs)
    {
      Assert.True(ScriptsAsk.Resolve(new JsonArray(ScriptItem("A1", inputs)), new[] { "A1" }, out var scripts, out var error), error?.Message);
      return scripts;
    }

    /// <summary>The card is the contract: what the person read is what runs, whatever the later build says, and the model is told.</summary>
    [Fact]
    public void A_build_that_declares_another_access_is_built_with_the_cards_and_told_so()
    {
      var spec = RegionSpec("[\"gcode\"]");

      Assert.Null(BuildScriptGate.Adopt(spec, false, Card("[{\"name\":\"gcode\",\"access\":\"list\"}]"), out var subs));

      Assert.Equal("list", spec.Components.Single().InputAccess["gcode"]);
      Assert.Contains("port names, order and access on the card", subs.Single().Sentence());
    }

    [Fact]
    public void A_build_cannot_give_itself_list_access_the_card_did_not_show()
    {
      var spec = RegionSpec("[{\"name\":\"gcode\",\"access\":\"list\"}]");

      Assert.Null(BuildScriptGate.Adopt(spec, false, Card("[\"gcode\"]"), out var subs));

      Assert.Equal("item", PortAccess.Of(spec.Components.Single().InputAccess, "gcode"));
      Assert.NotEmpty(subs);
    }

    [Fact]
    public void A_build_that_agrees_with_the_card_says_nothing_about_access()
    {
      var spec = RegionSpec("[{\"name\":\"gcode\",\"access\":\"list\"}]");

      Assert.Null(BuildScriptGate.Adopt(spec, false, Card("[{\"name\":\"gcode\",\"access\":\"list\"}]"), out var subs));

      Assert.Equal("list", spec.Components.Single().InputAccess["gcode"]);
      Assert.Empty(subs);
    }

    /// <summary>A sourceless component takes the card's script whole, its access with it.</summary>
    [Fact]
    public void A_script_filled_from_the_card_keeps_the_cards_access()
    {
      var args = JsonNode.Parse("{\"region\":\"A1\",\"components\":[{\"ref\":\"s\",\"type\":\"Python 3 Script\"}]}").AsObject();

      Assert.Equal(1, BuildScriptGate.FillFromCard(args, Card("[{\"name\":\"gcode\",\"access\":\"list\"}]")));

      Assert.True(BuildSpecParser.TryParse(args, _ => true, _ => new string[0], _ => IdState.Alive, out var spec, out var error), error?.Message);
      Assert.Equal("list", spec.Components.Single().InputAccess["gcode"]);
    }

    /// <summary>The card named no ports, so the build's stand, with the access the build declared.</summary>
    [Fact]
    public void Ports_taken_from_the_build_keep_the_builds_access()
    {
      Assert.True(ScriptsAsk.Resolve(new JsonArray(JsonNode.Parse("{\"region\":\"A1\",\"type\":\"Python 3 Script\",\"source\":\"result = gcode\"}")), new[] { "A1" }, out var card, out var cardError), cardError?.Message);
      var spec = RegionSpec("[{\"name\":\"gcode\",\"access\":\"list\"}]");

      Assert.Null(BuildScriptGate.Adopt(spec, false, card, out _));

      Assert.Equal("list", spec.Components.Single().InputAccess["gcode"]);
    }

    // ---- the card --------------------------------------------------------------------------------------------------

    [Fact]
    public void The_summary_says_each_inputs_access_the_default_included()
    {
      Assert.Equal("Python, 1 line. Inputs: gcode (list), temp (item). Outputs: result. Touches the Rhino document: no.",
        ScriptSummary.Line("Python", "x = 1", new[] { "gcode", "temp" }, new[] { "result" }, inputAccess: new Dictionary<string, string> { ["gcode"] = "list" }));
      Assert.Equal("Python, 1 line. Inputs: none. Outputs: none. Touches the Rhino document: no.", ScriptSummary.Line("Python", "x = 1", null, null));
    }

    [Fact]
    public void A_placement_card_and_a_region_card_both_show_it()
    {
      var spec = RegionSpec("[{\"name\":\"gcode\",\"access\":\"list\"}]");
      var spot = new SpotPlacement { Address = "Spot 1", Components = new List<string> { "a Script" }, Scripts = SpotScripts.Of(spec, null) };
      Assert.Contains("Inputs: gcode (list).", ConsentCard.SpotSourceBlocks(spot));

      var html = ConsentCard.ScriptsSection(Card("[{\"name\":\"gcode\",\"access\":\"list\"},\"temp\"]"));
      Assert.Contains("Inputs: gcode (list), temp (item).", html);
    }

    // ---- what a read shows -------------------------------------------------------------------------------------------

    [Fact]
    public void A_read_of_a_script_says_how_each_input_is_fed()
    {
      var world = new Dwight.AgentEval.EvalWorld(Dwight.AgentEval.EvalFixture.Parse("empty", "{\"canvas\":{\"components\":[]}}"), new Dwight.AgentEval.ComponentList());
      var place = JsonNode.Parse("{\"components\":[" + Script("[\"temp\",{\"name\":\"gcode\",\"access\":\"list\"}]").ToJsonString() + "]}").AsObject();
      Assert.True(world.PlaceSpot(place, "Spot 1", true).Ok);

      var access = JsonNode.Parse(world.Answer(AgentToolCatalog.GetComponent, "{\"id\":1}", out _))["result"]["script"]["inputAccess"];

      Assert.Equal("item", access["temp"].GetValue<string>());
      Assert.Equal("list", access["gcode"].GetValue<string>());
    }

    // ---- C#: what the method is handed ------------------------------------------------------------------------------

    [Fact]
    public void A_list_inputs_hint_is_its_elements_and_the_signature_hands_it_a_list()
    {
      var names = new[] { "gcode", "temp" };
      var access = new Dictionary<int, string> { [0] = "list", [1] = "item" };

      var plan = CSharpSignature.Plan(CSharpSignature.Parse("void RunScript(List<string> gcode, double temp, ref object a)"), names, new[] { "a" }, new Dictionary<int, string>(), access);

      Assert.Equal("string", plan.InputHints[0]);
      Assert.Equal("double", plan.InputHints[1]);
      Assert.Empty(plan.Notes);
      Assert.Equal("private void RunScript(List<string> gcode, double temp, ref object a)", CSharpSignature.Generated(names, plan.InputHints, new[] { "a" }, plan.OutputHints, access));
    }

    [Fact]
    public void A_tree_input_is_handed_a_data_tree_and_an_item_one_is_handed_the_type()
    {
      var access = new Dictionary<int, string> { [0] = "tree", [1] = "item" };

      var plan = CSharpSignature.Plan(CSharpSignature.Parse("void RunScript(DataTree<Curve> rows, Curve one)"), new[] { "rows", "one" }, new string[0], new Dictionary<int, string>(), access);

      Assert.Equal("Curve", plan.InputHints[0]);
      Assert.Equal("private void RunScript(DataTree<Curve> rows, Curve one)", CSharpSignature.Generated(new[] { "rows", "one" }, plan.InputHints, new string[0], plan.OutputHints, access));
    }

    /// <summary>A method that asks for a list on an item input is the defect, and it gets no hint and a note, as it always did.</summary>
    [Fact]
    public void A_list_in_the_method_on_an_item_input_stays_unmapped()
    {
      var plan = CSharpSignature.Plan(CSharpSignature.Parse("void RunScript(List<string> gcode)"), new[] { "gcode" }, new string[0], new Dictionary<int, string>());

      Assert.Empty(plan.InputHints);
      Assert.Contains(plan.Notes, n => n.Contains("no hint for type List<string> on input gcode"));
    }

    // ---- the shim and the build, by their text: they are Grasshopper-bound, and the Rhino row is what proves them -------

    private static string Read(params string[] parts)
    {
      var dir = new DirectoryInfo(AppContext.BaseDirectory);
      while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Dwight.sln"))) dir = dir.Parent;
      Assert.True(dir != null);
      return File.ReadAllText(Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray()));
    }

    [Theory]
    [InlineData("access == ScriptAccess.Item ? GH_ParamAccess.item : access == ScriptAccess.List ? GH_ParamAccess.list : GH_ParamAccess.tree;")]
    [InlineData("try { p.Access = ParamAccessOf(access); } catch { }")]
    [InlineData("if (access == ScriptAccess.Tree)")]
    [InlineData("GetProperty(\"AllowTreeAccess\")?.SetValue(p, true)")]
    [InlineData("SetAccess(p, spec.Access);")]                 // to-script's path and this one are one function
    [InlineData("SetAccess(port, access);")]
    [InlineData("if (port.Access == ParamAccessOf(access)) return true;")]    // read back: an input that did not take it is a failed build, never a quiet item
    public void The_shim_sets_each_access_one_way_and_reads_it_back(string line)
    {
      Assert.Contains(line, Read("Dwight", "Simplify", "ScriptComponentShim.cs"));
    }

    [Theory]
    [InlineData("ApplyDeclaredAccess(comp, c);")]              // after the ports exist
    [InlineData("ApplyDeclaredAccess(scComp, kv.Key);")]       // and again after a hint, which can replace the parameter
    [InlineData("ScriptComponentShim.SetInputAccess(comp, kv.Key, PortAccess.ToScript(kv.Value), out var accessError)")]
    [InlineData("throw new InvalidOperationException(accessError);")]
    [InlineData("CSharpSignature.Parse(kv.Key.Source), inNames, outNames, hints, accessOf);")]         // a C# method's List<T> is read as the access says
    [InlineData("plan.InputHints, outNames, plan.OutputHints, accessOf);")]
    public void The_build_applies_the_declaration_and_a_failure_stops_it(string line)
    {
      Assert.Contains(line, Read("Dwight", "Agent", "GhBuildRegion.cs"));
    }

    [Theory]
    [InlineData("item", ScriptAccessName.Item)]
    [InlineData("list", ScriptAccessName.List)]
    [InlineData("tree", ScriptAccessName.Tree)]
    public void Each_word_means_its_access_to_the_shim(string word, ScriptAccessName name)
    {
      Assert.Equal(name.ToString(), PortAccess.ToScript(word).ToString());
    }

    public enum ScriptAccessName { Item, List, Tree }

    // ---- the edit: an access change on an input that exists -----------------------------------------------------------

    private const string RuntimeType = "RhinoCodePluginGH.Components.Python3Component";

    private static LiveScript Live(params string[] inputs) => new LiveScript
    {
      Type = RuntimeType, Nick = "Bambu", Source = "result = gcode", SourceReadable = true, Inputs = inputs, Outputs = new[] { "result" },
    };

    private static HandoffSeed EditSeed(string source = "result = gcode", params (string Name, string Word)[] access)
    {
      var seed = new HandoffSeed { Target = 4, Claim = "changes", Source = source };
      foreach (var (name, word) in access) seed.InputAccess[name] = word;
      return seed;
    }

    private static bool Validate(HandoffSeed seed, LiveScript live, out ScriptEditChange change, out ToolResult error) =>
      EditScriptAsk.Validate(seed, _ => IdState.Alive, _ => live, out change, out error);

    [Fact]
    public void An_edit_may_change_an_existing_inputs_access_and_keep_the_source()
    {
      Assert.True(Validate(EditSeed("result = gcode", ("gcode", "list")), Live("gcode", "temp"), out var change, out var error), error?.Message);

      var one = change.AccessChanges.Single();
      Assert.Equal(("gcode", "item", "list"), (one.Name, one.From, one.To));
      Assert.True(change.KeepsSource);
      Assert.Equal("list", change.AccessToSet["gcode"]);
    }

    [Fact]
    public void The_same_source_with_no_access_change_is_still_an_empty_card()
    {
      Assert.False(Validate(EditSeed(), Live("gcode"), out _, out var error));
      Assert.Contains("identical", error.Message);

      var alreadyList = Live("gcode");
      alreadyList.InputAccess = new Dictionary<string, string> { ["gcode"] = "list" };
      Assert.False(Validate(EditSeed("result = gcode", ("gcode", "list")), alreadyList, out _, out var same));   // it already is what is declared
      Assert.Contains("identical", same.Message);
    }

    [Fact]
    public void A_change_back_to_item_is_a_change()
    {
      var live = Live("gcode");
      live.InputAccess = new Dictionary<string, string> { ["gcode"] = "list" };

      Assert.True(Validate(EditSeed("result = gcode", ("gcode", "item")), live, out var change, out var error), error?.Message);

      Assert.Equal("list", change.AccessChanges.Single().From);
      Assert.Equal("item", change.AccessChanges.Single().To);
    }

    [Fact]
    public void An_unknown_word_on_an_edit_is_refused_by_name_with_the_three_words()
    {
      Assert.False(Validate(EditSeed("result = 2", ("gcode", "quick")), Live("gcode"), out _, out var error));

      Assert.Contains("\"quick\" is not an access for the input \"gcode\"", error.Message);
      Assert.Contains("\"item\", \"list\" or \"tree\"", error.Message);
    }

    [Fact]
    public void An_input_that_is_not_there_is_refused_with_the_ones_that_are()
    {
      Assert.False(Validate(EditSeed("result = 2", ("nope", "list")), Live("gcode", "temp"), out _, out var error));

      Assert.Contains("no input called \"nope\"", error.Message);
      Assert.Contains("gcode, temp", error.Message);
    }

    [Fact]
    public void An_input_is_found_by_its_name_in_any_case_and_keeps_the_canvass_spelling()
    {
      Assert.True(Validate(EditSeed("result = 2", ("GCODE", "list")), Live("gcode"), out var change, out var error), error?.Message);

      Assert.Equal("gcode", change.AccessChanges.Single().Name);
    }

    [Fact]
    public void An_appended_inputs_access_is_held_apart_from_a_change()
    {
      var seed = EditSeed("result = k", ("k", "tree"), ("m", "item"));
      seed.AddInputs.Add("k");
      seed.AddInputs.Add("m");

      Assert.True(Validate(seed, Live("gcode"), out var change, out var error), error?.Message);

      Assert.Empty(change.AccessChanges);
      Assert.Equal("tree", change.AddedInputAccess["k"]);
      Assert.False(change.AddedInputAccess.ContainsKey("m"));
      Assert.Equal("tree", change.AccessToSet["k"]);
    }

    [Fact]
    public void The_request_carries_both_forms_as_sent()
    {
      var args = JsonNode.Parse("{\"kind\":\"edit_script\",\"why\":\"x\",\"target\":4,\"claim\":\"changes\",\"source\":\"r = 1\"," +
                                "\"set_access\":[{\"name\":\"gcode\",\"access\":\"list\"},{\"name\":\"odd\",\"access\":\"quick\"},{\"name\":\"num\",\"access\":3}]," +
                                "\"add_inputs\":[{\"name\":\"k\",\"access\":\"tree\"},\"m\"]}").AsObject();

      Assert.True(AgentHostTools.TryParseRequest(args, new MutationRequestLog(), "stamp", null, null, null, out _, out _, out var seed, out var error), error?.Message);

      Assert.Equal(new[] { "k", "m" }, seed.AddInputs);
      Assert.Equal("list", seed.InputAccess["gcode"]);
      Assert.Equal("quick", seed.InputAccess["odd"]);   // a word it does not know is carried, so the ask can refuse it by name
      Assert.Equal("3", seed.InputAccess["num"]);
      Assert.Equal("tree", seed.InputAccess["k"]);
      Assert.False(seed.InputAccess.ContainsKey("m"));
    }

    [Fact]
    public void The_click_reads_what_the_ask_read()
    {
      var seed = EditSeed("result = 2", ("gcode", "list"));

      var plan = SeedPlan.From("edit_script", seed, _ => IdState.Unknown);

      Assert.Equal("list", plan.InputAccess["gcode"]);
    }

    // ---- what the person reads about an edit ----------------------------------------------------------------------------

    private static ScriptEditChange Change(string source, params (string Name, string Word)[] access)
    {
      Assert.True(Validate(EditSeed(source, access), Live("gcode", "temp"), out var change, out var error), error?.Message);
      return change;
    }

    private static string Authority(ScriptEditChange change) =>
      ConsentCard.EditScriptAuthority(change, ScriptEditOutcome.RanClean, new ScriptTrialFacts { Ran = true });

    [Fact]
    public void A_card_that_only_changes_access_does_not_say_it_replaces_anything()
    {
      var said = Authority(Change("result = gcode", ("gcode", "list")));

      Assert.Contains("keep the source of \"Bambu\" as it is, and change how its inputs are fed", said);
      Assert.DoesNotContain("replace the source", said);
      Assert.Contains("It changes how gcode is fed, from item to list access.", said);
      Assert.Contains("puts your original source and access back", said);
    }

    [Fact]
    public void A_card_that_changes_both_says_both()
    {
      var said = Authority(Change("result = gcode[0]", ("gcode", "list")));

      Assert.Contains("replace the source of \"Bambu\"", said);
      Assert.Contains("It changes how gcode is fed, from item to list access.", said);
    }

    [Fact]
    public void A_card_with_no_access_change_reads_as_it_did()
    {
      var said = Authority(Change("result = 2"));

      Assert.Contains("replace the source of \"Bambu\"", said);
      Assert.DoesNotContain("is fed", said);
      Assert.Contains("puts your original source back and tells you so", said);
    }

    [Fact]
    public void The_act_says_what_the_card_does()
    {
      Assert.Contains("Change how the inputs of &quot;Bambu&quot; are fed.", ConsentCard.EditScriptHtml("why", "tok", Change("result = gcode", ("gcode", "list")), ScriptEditOutcome.RanClean, new ScriptTrialFacts { Ran = true }));
      Assert.Contains("Replace the source of &quot;Bambu&quot;.", ConsentCard.EditScriptHtml("why", "tok", Change("result = 2"), ScriptEditOutcome.RanClean, new ScriptTrialFacts { Ran = true }));
    }

    [Fact]
    public void An_added_inputs_access_is_on_the_edit_cards_summary()
    {
      var seed = EditSeed("result = k", ("k", "list"));
      seed.AddInputs.Add("k");
      Assert.True(Validate(seed, Live("gcode"), out var change, out _));

      Assert.Contains("Adds inputs: k (list).", ConsentCard.EditScriptHtml("why", "tok", change, ScriptEditOutcome.RanClean, new ScriptTrialFacts { Ran = true }));
    }

    [Fact]
    public void The_report_says_what_the_edit_did()
    {
      var facts = new ScriptTrialFacts { Ran = true };
      string Report(ScriptEditChange c) => ScriptEditVerdict.WithAccess(ScriptEditVerdict.ReportLine(ScriptEditOutcome.RanClean, facts, ScriptEditVerdict.Subject(c)), c);

      var kept = Report(Change("result = gcode", ("gcode", "list")));
      Assert.Contains("Dwight kept the source of \"Bambu\" (4) as it was.", kept);
      Assert.DoesNotContain("replaced the source", kept);
      Assert.EndsWith("It changes how gcode is fed, from item to list access.", kept);

      var both = Report(Change("result = 2", ("gcode", "list")));
      Assert.Contains("Dwight replaced the source of \"Bambu\" (4).", both);
      Assert.EndsWith("from item to list access.", both);

      Assert.DoesNotContain("is fed", Report(Change("result = 2")));
    }

    // ---- the undo -------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_restore_holds_the_old_access_beside_the_source_and_the_ports()
    {
      var restore = new ScriptEditRestore("a", "b", 2, 1, new[] { "k" }, new string[0],
        new Dictionary<string, string> { ["gcode"] = "item" }, new Dictionary<string, string> { ["gcode"] = "list", ["k"] = "tree" });

      Assert.Equal("a", restore.BeforeSource);
      Assert.Equal("item", restore.BeforeAccess["gcode"]);
      Assert.False(restore.BeforeAccess.ContainsKey("k"));   // undo removes that port, so it has no access to go back to
      Assert.Equal("list", restore.AfterAccess["gcode"]);
      Assert.Equal("tree", restore.AfterAccess["k"]);
    }

    [Fact]
    public void A_restore_with_no_access_change_holds_none()
    {
      var restore = new ScriptEditRestore("a", "b", 2, 1, null, null);

      Assert.Empty(restore.BeforeAccess);
      Assert.Empty(restore.AfterAccess);
    }

    [Theory]
    [InlineData("live.InputAccess = comp.Params.Input.Where(p => p.Access != GH_ParamAccess.item)")]  // the ask compares against what the canvas holds
    [InlineData("var accessBefore = (change.AccessChanges ?? new AccessChange[0]).ToDictionary(")]   // read live, before anything moves
    [InlineData("obj.Params.Input.Count, outParams.Count, change.AddInputs, change.AddOutputs, accessBefore, change.AccessToSet);")]
    [InlineData("if (!SetAccess(obj, change.AccessToSet, out var accessError))")]                     // the apply
    [InlineData("{ Revert(d, target, change, finish, accessError); return; }")]                      // an input that did not take it is taken back
    [InlineData("if (!SetAccess(comp, _restore.BeforeAccess, out error))")]                          // undo
    [InlineData("if (!SetAccess(comp, _restore.AfterAccess, out error))")]                           // redo
    public void The_apply_and_its_undo_carry_access_beside_the_source_and_ports(string line)
    {
      Assert.Contains(line, Read("Dwight", "Agent", "GhEditScript.cs"));
    }

    /// <summary>The trial checks the script the card will apply, so a copy fed differently would check something else.</summary>
    [Theory]
    [InlineData("Dwight", "Simplify", "ScriptEditTrial.cs", "ScriptComponentShim.SetInputAccess(copy, kv.Key, kv.Value, out var accessError)")]
    [InlineData("Dwight", "UI", "DwightScaffold.cs", "change.AccessToSet.ToDictionary(kv => kv.Key, kv => PortAccess.ToScript(kv.Value), StringComparer.OrdinalIgnoreCase)")]
    public void The_trial_feeds_its_copy_as_the_card_says(string a, string b, string c, string line)
    {
      Assert.Contains(line, Read(a, b, c));
    }

    /// <summary>The harness runs its own copy of these two lines, so a row cannot witness the panel's.</summary>
    [Theory]
    [InlineData("seed.Report = ScriptEditVerdict.WithAccess(ScriptEditVerdict.ReportLine(editOutcome, editFacts, ScriptEditVerdict.Subject(editScript)), editScript);")]
    [InlineData("InputAccess = new Dictionary<string, string>(plan.InputAccess, StringComparer.OrdinalIgnoreCase),")]
    public void The_panel_reports_the_access_and_a_click_carries_it(string line)
    {
      Assert.Contains(line, Read("Dwight", "UI", "DwightScaffold.cs"));
    }

    [Fact]
    public void A_read_of_a_live_script_takes_the_access_from_its_inputs()
    {
      Assert.Contains("p.Access == GH_ParamAccess.list ? PortAccess.List : p.Access == GH_ParamAccess.tree ? PortAccess.Tree : PortAccess.Item", Read("Dwight", "Agent", "GhAgent.cs"));
    }

    // ---- through the row: the harness runs the real ask and click over a stated world -----------------------------------

    private const string Request = AgentHostTools.RequestMutation;

    private static string Placed(string inputs) =>
      "{\"kind\":\"build\",\"why\":\"x\",\"place\":{\"components\":[{\"ref\":\"p\",\"type\":\"Python 3 Script\",\"nick\":\"Bambu\",\"source\":\"result = gcode\",\"inputs\":" + inputs + ",\"outputs\":[\"result\"]}]}}";

    private static Dwight.AgentEval.EvalWorld WorldWith(string inputs)
    {
      var world = new Dwight.AgentEval.EvalWorld(Dwight.AgentEval.EvalFixture.Parse("empty", "{\"canvas\":{\"components\":[]}}"), new Dwight.AgentEval.ComponentList());
      Assert.True(world.PlaceSpot(JsonNode.Parse(Placed(inputs))["place"].AsObject(), "Spot 1", true).Ok);
      return world;
    }

    [Fact]
    public void An_approved_access_change_is_what_the_next_read_sees()
    {
      var world = WorldWith("[\"gcode\",\"temp\"]");
      var seed = new HandoffSeed { Target = 1, Claim = "changes", Source = "result = gcode" };
      seed.InputAccess["gcode"] = "list";
      Assert.True(world.PrepareScript(seed, out var error), error?.Message);
      Assert.Contains("It changes how gcode is fed, from item to list access.", seed.Report);

      Assert.True(world.ApplyScript(SeedPlan.From("edit_script", seed, _ => IdState.Unknown), out var text), text);

      var script = JsonNode.Parse(world.Answer(AgentToolCatalog.GetComponent, "{\"id\":1}", out _))["result"]["script"];
      Assert.Equal("list", script["inputAccess"]["gcode"].GetValue<string>());
      Assert.Equal("item", script["inputAccess"]["temp"].GetValue<string>());
      Assert.Equal("result = gcode", script["source"].GetValue<string>());
    }

    [Fact]
    public void A_placement_declaring_list_access_reads_as_list_and_an_edit_back_to_item_is_seen()
    {
      var world = WorldWith("[{\"name\":\"gcode\",\"access\":\"list\"}]");
      var seed = new HandoffSeed { Target = 1, Claim = "changes", Source = "result = gcode" };
      seed.InputAccess["gcode"] = "item";
      Assert.True(world.PrepareScript(seed, out var error), error?.Message);
      Assert.True(world.ApplyScript(SeedPlan.From("edit_script", seed, _ => IdState.Unknown), out _));

      Assert.Equal("item", JsonNode.Parse(world.Answer(AgentToolCatalog.GetComponent, "{\"id\":1}", out _))["result"]["script"]["inputAccess"]["gcode"].GetValue<string>());
    }

    [Fact]
    public void A_click_after_the_access_has_moved_says_there_is_nothing_to_do()
    {
      var world = WorldWith("[\"gcode\"]");
      var seed = new HandoffSeed { Target = 1, Claim = "changes", Source = "result = gcode" };
      seed.InputAccess["gcode"] = "list";
      Assert.True(world.PrepareScript(seed, out _));
      var plan = SeedPlan.From("edit_script", seed, _ => IdState.Unknown);
      Assert.True(world.ApplyScript(plan, out _));

      Assert.False(world.ApplyScript(plan, out var again));

      Assert.Contains("identical", again);
    }
  }
}
