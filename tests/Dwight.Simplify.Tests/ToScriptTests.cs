using System.Collections.Generic;
using System.Linq;
using Dwight.Simplify;
using Xunit;

namespace Dwight.Simplify.Tests
{
  /// <summary>
  /// WP-4's GH-free half. The property under test throughout is the phase's hardest-won law (RR7 -> RR14):
  /// STRUCTURE IS THE PLUGIN'S. The model supplies code and names; arity, order, type and access are derived from
  /// the live boundary contract, and a model spec that disagrees with it is REFUSED rather than reshaped.
  /// </summary>
  public class ScriptTypeMapTests
  {
    [Theory]
    [InlineData("Number", "double", "float")]     // the ONE language-dependent hint (instrumented, not assumed)
    [InlineData("Integer", "int", "int")]
    [InlineData("Curve", "Curve", "Curve")]
    [InlineData("Brep", "Brep", "Brep")]
    [InlineData("Text", "string", "string")]
    [InlineData("Domain", "Interval", "Interval")]
    [InlineData("Point Cloud", "PointCloud", "PointCloud")]
    public void Maps_gh_types_to_each_language_hint(string ghType, string cs, string py)
    {
      Assert.Equal(cs, ScriptTypeMap.HintFor(ghType, ScriptLanguage.CSharp));
      Assert.Equal(py, ScriptTypeMap.HintFor(ghType, ScriptLanguage.Python3));
    }

    [Theory]
    [InlineData("Generic Data")]
    [InlineData("Some Plugin Type")]
    [InlineData("")]
    [InlineData(null)]
    public void An_unmapped_type_falls_back_to_no_hint_rather_than_guessing(string ghType)
    {
      // no hint = generic goo, a legitimate contract. A WRONG hint would be a silent mis-typing; this cannot be.
      Assert.Null(ScriptTypeMap.HintFor(ghType, ScriptLanguage.CSharp));
      Assert.Null(ScriptTypeMap.HintFor(ghType, ScriptLanguage.Python3));
    }

    [Theory]
    [InlineData(1, new[] { 1 }, "Item")]        // one item -> item
    [InlineData(1, new[] { 7 }, "List")]        // one flat branch of many -> list
    [InlineData(3, new[] { 2, 2, 2 }, "Tree")]  // several branches -> tree
    [InlineData(1, new int[0], "Item")]         // no data read -> the safest assumption
    public void Derives_access_from_the_boundary_tree_signature(int paths, int[] items, string expected)
    {
      // Access does not have to be provably right a priori: a wrong access reshapes the output tree, which the
      // beside-verification catches and reverts. The bar is "sensible, with verification as the arbiter".
      Assert.Equal(expected, ScriptTypeMap.AccessFor(paths, items).ToString());
    }

    [Fact]
    public void Declared_type_wraps_the_hint_in_its_access()
    {
      Assert.Equal("double", ScriptTypeMap.DeclaredType("Number", ScriptLanguage.CSharp, ScriptAccess.Item));
      Assert.Equal("List<Curve>", ScriptTypeMap.DeclaredType("Curve", ScriptLanguage.CSharp, ScriptAccess.List));
      Assert.Equal("DataTree<Brep>", ScriptTypeMap.DeclaredType("Brep", ScriptLanguage.CSharp, ScriptAccess.Tree));
      Assert.Equal("list[float]", ScriptTypeMap.DeclaredType("Number", ScriptLanguage.Python3, ScriptAccess.List));
    }

    [Fact]
    public void Sanitises_a_model_name_into_a_legal_unique_identifier()
    {
      var taken = new HashSet<string>();
      Assert.Equal("siteCurves", ScriptTypeMap.VariableName("siteCurves", 0, taken));
      Assert.Equal("bay_width", ScriptTypeMap.VariableName("bay width", 1, taken));   // spaces -> underscores
      Assert.Equal("x1a_b", ScriptTypeMap.VariableName("1a-b", 2, taken));            // may not START with a digit - but the digit is kept, not dropped
      Assert.Equal("_double", ScriptTypeMap.VariableName("double", 3, taken));        // a reserved word is escaped
      Assert.Equal("x4", ScriptTypeMap.VariableName("!!!", 4, taken));                // nothing legal -> positional
    }

    [Fact]
    public void A_duplicate_name_is_made_unique_rather_than_shadowing()
    {
      var taken = new HashSet<string>();
      Assert.Equal("width", ScriptTypeMap.VariableName("width", 0, taken));
      Assert.Equal("width2", ScriptTypeMap.VariableName("width", 1, taken));
      Assert.Equal("width3", ScriptTypeMap.VariableName("width", 2, taken));
    }
  }

  /// <summary>U74: a scaffold-built script's input takes its type hint from what is wired into it. The mapping is the GH-free
  /// half; the live placement (ScriptComponentShim.HintInputsFromSources) is GH-coupled and not exercised by a test.</summary>
  public class ScriptInputHintTests
  {
    [Theory]
    [InlineData("Curve", "Curve", "Curve")]
    [InlineData("Point", "Point3d", "Point3d")]
    [InlineData("Surface", "Surface", "Surface")]
    [InlineData("Number", "double", "float")]
    [InlineData("Integer", "int", "int")]
    [InlineData("Boolean", "bool", "bool")]
    [InlineData("Text", "string", "string")]
    public void One_kind_of_source_gives_that_kinds_hint_per_language(string ghType, string cs, string py)
    {
      Assert.Equal(cs, ScriptTypeMap.HintForSources(new[] { ghType }, ScriptLanguage.CSharp));
      Assert.Equal(py, ScriptTypeMap.HintForSources(new[] { ghType, ghType }, ScriptLanguage.Python3));
    }

    [Fact]
    public void Several_geometry_kinds_give_the_geometry_base_and_other_mixes_give_none()
    {
      Assert.Equal("GeometryBase", ScriptTypeMap.HintForSources(new[] { "Curve", "Brep" }, ScriptLanguage.Python3));
      Assert.Null(ScriptTypeMap.HintForSources(new[] { "Curve", "Number" }, ScriptLanguage.Python3));
    }

    [Fact]
    public void An_unknown_or_absent_source_maps_to_no_selection()
    {
      Assert.Null(ScriptTypeMap.HintForSources(null, ScriptLanguage.Python3));
      Assert.Null(ScriptTypeMap.HintForSources(new string[0], ScriptLanguage.CSharp));
      Assert.Null(ScriptTypeMap.HintForSources(new[] { "Generic Data" }, ScriptLanguage.Python3));
      Assert.Null(ScriptTypeMap.HintForSources(new[] { "Curve", "Generic Data" }, ScriptLanguage.Python3));
      Assert.Null(ScriptTypeMap.HintForSources(new[] { "" }, ScriptLanguage.CSharp));
    }
  }

  public class ToScriptValidatorTests
  {
    private static ScriptInterface Iface(int ins, int outs)
    {
      var iface = new ScriptInterface();
      for (var i = 0; i < ins; i++) iface.Inputs.Add(new ScriptParam { Ext = i, GhType = "Number", Access = ScriptAccess.Item });
      for (var j = 0; j < outs; j++) iface.Outputs.Add(new ScriptParam { Ext = j, GhType = "Curve", Access = ScriptAccess.List });
      return iface;
    }

    private static ToScriptProposal Proposal(string source, string[] ins, string[] outs) => new ToScriptProposal
    {
      Source = source,
      InputNames = ins.ToList(),
      OutputNames = outs.ToList(),
    };

    [Fact]
    public void Accepts_a_proposal_that_matches_the_boundary_contract()
    {
      var err = ToScriptValidator.Validate(Proposal("a = w;", new[] { "w" }, new[] { "a" }), Iface(1, 1));
      Assert.Null(err);
    }

    [Fact]
    public void Rejects_an_invented_input_the_selection_does_not_have()
    {
      // The model naming a port that does not cross the boundary is exactly how a script gets mis-wired. The
      // arity comes from the live graph, so this is refused - never "repaired" into whatever it meant.
      var err = ToScriptValidator.Validate(Proposal("a = w + z;", new[] { "w", "z" }, new[] { "a" }), Iface(1, 1));
      Assert.Contains("exactly 1 input", err);
    }

    [Fact]
    public void Rejects_a_dropped_output_which_would_starve_a_downstream_component()
    {
      var err = ToScriptValidator.Validate(Proposal("a = w;", new[] { "w" }, new[] { "a" }), Iface(1, 2));
      Assert.Contains("exactly 2 output", err);
    }

    [Fact]
    public void Rejects_an_empty_source()
    {
      Assert.Contains("no source code", ToScriptValidator.Validate(Proposal("   ", new[] { "w" }, new[] { "a" }), Iface(1, 1)));
      Assert.NotNull(ToScriptValidator.Validate(null, Iface(1, 1)));
    }

    [Fact]
    public void Names_are_stamped_onto_the_plugin_interface_sharing_one_identifier_namespace()
    {
      // inputs and outputs share one scope in the generated script, so a name collision across the two must not
      // shadow - "result" as both an input and an output has to resolve to two distinct identifiers.
      var iface = Iface(1, 1);
      var p = Proposal("...", new[] { "result" }, new[] { "result" });

      ToScriptValidator.ApplyNames(p, iface);

      Assert.Equal("result", iface.Inputs[0].Variable);
      Assert.Equal("result2", iface.Outputs[0].Variable);
      Assert.Equal("result", iface.Outputs[0].Name);   // the PRETTY name is the model's; only the identifier is forced unique
    }

    [Fact]
    public void An_unnamed_param_still_gets_a_usable_name()
    {
      var iface = Iface(1, 1);
      ToScriptValidator.ApplyNames(Proposal("...", new[] { "" }, new[] { (string)null }), iface);

      Assert.Equal("input1", iface.Inputs[0].Name);
      Assert.Equal("output1", iface.Outputs[0].Name);
      Assert.False(string.IsNullOrWhiteSpace(iface.Inputs[0].Variable));
    }

    [Fact]
    public void Describe_gives_the_model_the_real_contract_to_write_against()
    {
      var text = ToScriptValidator.Describe(Iface(1, 1), ScriptLanguage.CSharp);

      Assert.Contains("double", text);        // the input's declared type
      Assert.Contains("List<Curve>", text);   // the output's declared type + access
      Assert.Contains("access: item", text);
    }
  }

  public class ToScriptParserTests
  {
    [Fact]
    public void Parses_the_model_reply()
    {
      var p = ToScriptParser.Parse("{\"source\":\"a = w;\",\"summary\":\"doubles it\",\"inputs\":[\"w\"],\"outputs\":[\"a\"]}", ScriptLanguage.CSharp);

      // #141/#146: a C# source is NORMALIZED at birth - the plugin's baseline usings are unioned in here, at the one
      // point every path shares, so the consent surface, the compile gate and the accept all read the same text.
      // The model's BODY is preserved verbatim; only the using header is the plugin's.
      Assert.Contains("a = w;", p.Source);
      Assert.Contains("using System.Collections.Generic;", p.Source);
      Assert.Contains("using Rhino.Geometry;", p.Source);
      Assert.Equal("doubles it", p.Summary);
      Assert.Equal(new[] { "w" }, p.InputNames);
      Assert.Equal(new[] { "a" }, p.OutputNames);
    }

    [Fact]
    public void A_python_reply_is_NOT_normalized()
    {
      // #146: C# only. Python has no using mechanism and is not preempted by the C# format finding.
      var p = ToScriptParser.Parse("{\"source\":\"a = w\",\"summary\":\"s\",\"inputs\":[\"w\"],\"outputs\":[\"a\"]}", ScriptLanguage.Python3);
      Assert.Equal("a = w", p.Source);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("")]
    public void Malformed_json_is_null_not_a_half_built_interface(string json)
    {
      Assert.Null(ToScriptParser.Parse(json, ScriptLanguage.CSharp));
    }
  }

  public class ToScriptOutputCompareTests
  {
    private static OutputSample S(string tree, string val, string label = null) => new OutputSample(tree, val, label);

    [Fact]
    public void Identical_outputs_have_no_mismatches()
    {
      var before = new[] { S("1:1", "abc"), S("3:2", "def") };
      var after = new[] { S("1:1", "abc"), S("3:2", "def") };

      Assert.Empty(OutputEquivalence.Mismatches(before, after));
    }

    [Fact]
    public void Reports_WHICH_output_differs_not_merely_that_one_did()
    {
      // to-script has a 1:1 correspondence Simplify does not (script output j IS boundary output j), which is what
      // lets a mismatch be named per output instead of reported as an opaque "the results differ".
      var before = new[] { S("1:1", "curves"), S("1:1", "areas"), S("1:1", "ok") };
      var after = new[] { S("1:1", "curves"), S("1:1", "DIFFERENT"), S("1:1", "ok") };

      Assert.Equal(new[] { 1 }, OutputEquivalence.Mismatches(before, after));
    }

    [Fact]
    public void A_changed_tree_shape_is_a_mismatch_even_when_the_values_match()
    {
      // the geometry-aware digest catches value drift; the tree signature catches a wrong ACCESS choice, which is
      // precisely the failure mode the plugin's access derivation is allowed to get wrong.
      var before = new[] { S("1:1", "same") };
      var after = new[] { S("3:2", "same") };

      Assert.Equal(new[] { 0 }, OutputEquivalence.Mismatches(before, after));
    }

    [Fact]
    public void A_count_mismatch_reports_every_index_rather_than_zipping_to_the_shorter_list()
    {
      Assert.Equal(new[] { 0, 1 }, OutputEquivalence.Mismatches(new[] { S("1:1", "a") }, new[] { S("1:1", "a"), S("1:1", "b") }));
    }
  }
}
