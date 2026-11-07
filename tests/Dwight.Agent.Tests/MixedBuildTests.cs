using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dwight.Agent;
using Dwight.Simplify;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// Brief AE, after Tharun's mixed Serpentine build (U86 to U89): a card that listed no ports accepts the ports its source names,
  /// a C# script's declared types choose its hints and its compiler lines come back in the model's own numbering, the consequence
  /// of building the card's text is said plainly, and the undo guard names what was missing.
  ///
  /// GhBuildRegion is GH-coupled and not linked here; where the order of its calls is the claim, the SOURCE is read (the bound is
  /// stated: that proves the text's order, not the behaviour, which Tharun's sitting shows).
  /// </summary>
  public class MixedBuildTests
  {
    private static readonly string[] Nine =
      { "FrontPath", "BackPath", "HeightProfile", "Columns", "Rows", "BoxW", "BoxH", "BoxD", "Unzip" };

    private const string SerpentineSource =
      "import Rhino.Geometry as rg\n" +
      "n = int(Columns) * int(Rows)\n" +
      "a = FrontPath.PointAtStart\nb = BackPath.PointAtStart\nh = HeightProfile\n" +
      "w = BoxW; hh = BoxH; d = BoxD; u = Unzip\n";

    private static ApprovedScript Card(string source, string[] inputs = null, string[] outputs = null, string lang = BuildScriptTypes.Python)
    {
      var a = new ApprovedScript { Region = "C1", Language = lang, Source = source };
      a.Inputs.AddRange(inputs ?? new string[0]); a.Outputs.AddRange(outputs ?? new string[0]);
      return a;
    }

    private static BuildSpec Build(string source, string[] inputs, string[] outputs, params BuildWire[] wires)
    {
      var spec = new BuildSpec { Region = "C1" };
      var c = new BuildComponent { Ref = "t", Type = BuildScriptTypes.PythonTypeName, Source = source };
      c.Inputs.AddRange(inputs); c.Outputs.AddRange(outputs);
      spec.Components.Add(c);
      spec.Wires.AddRange(wires);
      return spec;
    }

    // ---------------------------------------------------------------- U86

    [Fact]
    public void U86_the_serpentine_case_builds_with_the_ports_its_source_names()
    {
      var spec = Build(SerpentineSource, Nine, new[] { "pts" },
        new BuildWire { FromRef = "s", ToRef = "t", ToParam = "Columns" });
      var refused = BuildScriptGate.Adopt(spec, false, new[] { Card(SerpentineSource, outputs: new string[0]) }, out var subs);
      // the card listed no inputs and no outputs; "pts" is not in the source, so THIS build is refused naming it
      Assert.NotNull(refused);
      Assert.Contains("pts", refused.Message);

      var spec2 = Build(SerpentineSource, Nine, new string[0],
        new BuildWire { FromRef = "s", ToRef = "t", ToParam = "Columns" });
      var ok = BuildScriptGate.Adopt(spec2, false, new[] { Card(SerpentineSource) }, out var subs2);

      Assert.Null(ok);
      var c = spec2.Components[0];
      Assert.Equal(Nine, c.Inputs);                               // the build's ports stand
      Assert.Equal(SerpentineSource, c.Source);
      Assert.Equal("Columns", spec2.Wires[0].ToParam);            // and so do its wires
      var sub = Assert.Single(subs2);
      Assert.True(sub.PortsTakenFromBuild);
      Assert.False(sub.PortOrderDiffered);
      Assert.Contains("Ports taken from the build's declaration; the card listed none and its source names them.", sub.Sentence());
      Assert.True(sub.ToJson()["portsTakenFromBuild"].GetValue<bool>());
    }

    [Fact]
    public void U86_a_port_the_source_does_not_name_is_refused_naming_it()
    {
      var spec = Build(SerpentineSource, Nine.Concat(new[] { "Twist" }).ToArray(), new string[0]);
      var refused = BuildScriptGate.Adopt(spec, false, new[] { Card(SerpentineSource) }, out var subs);

      Assert.NotNull(refused);
      Assert.Contains("Nothing was built", refused.Message);
      Assert.Contains("does not name Twist", refused.Message);
      Assert.Empty(subs);
      Assert.Equal(10, spec.Components[0].Inputs.Count);          // a refusal rewrites nothing
    }

    [Fact]
    public void U86_the_match_is_a_whole_identifier_and_case_sensitive()
    {
      var a = Card("x = Columns2 + rows");
      var longer = Build("x", new[] { "Columns" }, new string[0]);
      Assert.False(ScriptsAsk.CoversByBuildPorts(a, "C1", longer.Components[0]));   // Columns2 is another identifier
      var lower = Build("x", new[] { "Rows" }, new string[0]);
      Assert.False(ScriptsAsk.CoversByBuildPorts(a, "C1", lower.Components[0]));    // rows is not Rows
      Assert.True(ScriptsAsk.CoversByBuildPorts(a, "C1", Build("x", new[] { "rows" }, new string[0]).Components[0]));
    }

    [Fact]
    public void U86_a_card_that_declared_ports_is_unchanged_and_other_regions_and_languages_still_refuse()
    {
      var withPorts = Card(SerpentineSource, new[] { "Columns" });
      Assert.False(ScriptsAsk.CoversByBuildPorts(withPorts, "C1", Build(SerpentineSource, new[] { "Rows" }, new string[0]).Components[0]));
      // U83's counts rule still decides it: one card input, one build input, renamed -> by position, not by the source
      var spec = Build(SerpentineSource, new[] { "Rows" }, new string[0]);
      Assert.Null(BuildScriptGate.Adopt(spec, false, new[] { withPorts }, out var subs));
      Assert.Equal(new[] { "Columns" }, spec.Components[0].Inputs);
      Assert.False(Assert.Single(subs).PortsTakenFromBuild);

      var otherRegion = Build(SerpentineSource, new[] { "Rows" }, new string[0]);
      otherRegion.Region = "C2";
      Assert.NotNull(BuildScriptGate.Adopt(otherRegion, false, new[] { Card(SerpentineSource) }, out _));
      var csharp = Card(SerpentineSource, lang: BuildScriptTypes.CSharp);
      Assert.NotNull(BuildScriptGate.Adopt(Build(SerpentineSource, new[] { "Rows" }, new string[0]), false, new[] { csharp }, out _));
    }

    [Fact]
    public void U86_two_ports_less_cards_are_not_guessed_between()
    {
      var spec = Build(SerpentineSource, Nine, new string[0]);
      var refused = BuildScriptGate.Adopt(spec, false, new[] { Card(SerpentineSource), Card(SerpentineSource + "# second") }, out _);
      // the first is byte-different from the build (source) but the pairing is not forced
      Assert.Null(BuildScriptGate.Check(spec, false, new[] { Card(SerpentineSource) }));
      Assert.NotNull(refused);
    }

    // ---------------------------------------------------------------- U88

    private const string SerpentineSignature =
      "private void RunScript(Curve front_path, Curve back_path, List<double> height_profile, int columns, int rows, " +
      "double box_w, double box_h, double box_d, bool unzip, ref object pts, ref Curve axis)";

    [Fact]
    public void U88_the_parser_reads_the_models_exact_signature()
    {
      var ps = CSharpSignature.Parse("using System;\n\n" + SerpentineSignature + "\n{\n  pts = null;\n}");

      Assert.Equal(11, ps.Count);
      Assert.Equal(("Curve", "front_path", false), (ps[0].Type, ps[0].Name, ps[0].ByRef));
      Assert.Equal(("List<double>", "height_profile", false), (ps[2].Type, ps[2].Name, ps[2].ByRef));
      Assert.Equal(("int", "columns", false), (ps[3].Type, ps[3].Name, ps[3].ByRef));
      Assert.Equal(("object", "pts", true), (ps[9].Type, ps[9].Name, ps[9].ByRef));
      Assert.Equal(("Curve", "axis", true), (ps[10].Type, ps[10].Name, ps[10].ByRef));
      Assert.Null(CSharpSignature.Parse("var x = 1;"));
      Assert.Equal("Dictionary<string, int>", CSharpSignature.Parse("void RunScript(Dictionary<string, int> d, int k = 3)")[0].Type);
      Assert.Equal("k", CSharpSignature.Parse("void RunScript(Dictionary<string, int> d, int k = 3)")[1].Name);
    }

    [Fact]
    public void U88_the_declared_type_chooses_the_hint_over_the_sources_and_object_is_the_generic()
    {
      var inputs = new[] { "front_path", "back_path", "height_profile", "columns", "rows", "box_w", "box_h", "box_d", "unzip" };
      var outputs = new[] { "pts", "axis" };
      // what the wired sources implied: sliders feed Number -> double, curves feed Curve
      var fromSources = new Dictionary<int, string> { { 0, "Curve" }, { 3, "double" }, { 4, "double" }, { 5, "double" }, { 8, "bool" } };

      var plan = CSharpSignature.Plan(CSharpSignature.Parse(SerpentineSignature), inputs, outputs, fromSources);

      Assert.Equal("int", plan.InputHints[3]);        // columns: the method declares int, the slider's double no longer reaches it
      Assert.Equal("int", plan.InputHints[4]);
      Assert.Equal("double", plan.InputHints[5]);
      Assert.Equal("Curve", plan.InputHints[1]);      // back_path had no source hint; the declared type gives one
      Assert.Equal("bool", plan.InputHints[8]);
      Assert.False(plan.InputHints.ContainsKey(2));   // List<double> maps to nothing and nothing was derived either
      Assert.Equal("Curve", plan.OutputHints[1]);     // ref Curve axis
      Assert.False(plan.OutputHints.ContainsKey(0));  // ref object pts: generic

      var note = Assert.Single(plan.Notes);
      Assert.Equal("no hint for type List<double> on input height_profile; it arrives as its source's type", note);

      // object on an input REMOVES a source-derived hint: the method asked for the generic
      var generic = CSharpSignature.Plan(CSharpSignature.Parse("void RunScript(object a, ref object b)"), new[] { "a" }, new[] { "b" },
        new Dictionary<int, string> { { 0, "Curve" } });
      Assert.False(generic.InputHints.ContainsKey(0));
      Assert.Empty(generic.Notes);

      // a type that maps to nothing leaves the source-derived hint
      var unmapped = CSharpSignature.Plan(CSharpSignature.Parse("void RunScript(float a)"), new[] { "a" }, new string[0],
        new Dictionary<int, string> { { 0, "double" } });
      Assert.Equal("double", unmapped.InputHints[0]);
      Assert.Single(unmapped.Notes);
    }

    [Fact]
    public void U88_every_hint_the_signature_can_choose_is_one_the_probe_knows()
    {
      foreach (var type in new[] { "int", "double", "bool", "string", "Point3d", "Vector3d", "Plane", "Line", "Curve", "Surface", "Brep",
                                   "Mesh", "Box", "System.Int32", "Rhino.Geometry.Curve", "System.Object", "object" })
      {
        var hint = CSharpSignature.HintForType(type, out var mapped);
        Assert.True(mapped, type);
        if (hint != null) Assert.True(ScriptTypeMap.IsKnownHint(hint), type + " -> " + hint);
      }
      Assert.Null(CSharpSignature.HintForType("List<double>", out var m1)); Assert.False(m1);
      Assert.Null(CSharpSignature.HintForType("float", out var m2)); Assert.False(m2);
      Assert.False(ScriptTypeMap.IsKnownHint("Frobnicator"));
      Assert.False(ScriptTypeMap.IsKnownHint(""));
      Assert.True(ScriptTypeMap.IsKnownHint("double"));
    }

    [Fact]
    public void U88_the_generated_signature_shows_what_each_parameter_is_really_handed()
    {
      var sig = CSharpSignature.Generated(new[] { "front_path", "columns", "t" }, new Dictionary<int, string> { { 0, "Curve" }, { 1, "int" } },
        new[] { "pts" }, new Dictionary<int, string>());
      Assert.Equal("private void RunScript(Curve front_path, int columns, object t, ref object pts)", sig);
    }

    private const string ModelScript =
      "using System;\nusing System.Collections.Generic;\nusing Rhino.Geometry;\n\n" +
      "private void RunScript(double length, ref object pts)\n{\n  int cols = (int)length;\n  pts = cols;\n}";

    [Fact]
    public void U88_a_compiled_line_is_given_in_the_models_numbering_with_both_shown()
    {
      var generated = UsingNormalizer.ToComponentTemplate(ModelScript);
      var shift = UsingNormalizer.LineShift(ModelScript, generated);

      // find where the marker line really is in each text
      var compiledLine = generated.Split('\n').ToList().FindIndex(l => l.Contains("int cols")) + 1;
      var modelLine = ModelScript.Split('\n').ToList().FindIndex(l => l.Contains("int cols")) + 1;
      Assert.Equal(7, modelLine);
      Assert.Equal(modelLine, compiledLine - shift);

      var mapped = CSharpSignature.MapDiagnostic("Cannot implicitly convert type 'double' to 'int' [" + compiledLine + ":16]", shift);
      Assert.Equal("Cannot implicitly convert type 'double' to 'int' [line 7 of your script, line " + compiledLine + " of the compiled file, col 16]", mapped);

      // a position inside the header Dwight added is not a line of the model's
      Assert.Contains("in the lines Dwight added", CSharpSignature.MapDiagnostic("x [2:1]", shift));
      // shift zero (Python, a class that passed through) leaves the compiler's text alone
      Assert.Equal("x [24:16]", CSharpSignature.MapDiagnostic("x [24:16]", 0));
      var cls = "using System;\npublic class Script_Instance : GH_ScriptInstance\n{\n}";
      Assert.Equal(0, UsingNormalizer.LineShift(cls, UsingNormalizer.ToComponentTemplate(cls)));
    }

    [Fact]
    public void U88_the_shift_is_measured_for_a_body_with_no_usings_of_its_own_and_survives_the_parse_and_the_record()
    {
      var raw = "private void RunScript(double x, ref object a)\n{\n  a = x;\n}";
      var generated = UsingNormalizer.ToComponentTemplate(raw);
      var line = generated.Split('\n').ToList().FindIndex(l => l.Contains("a = x;")) + 1;
      Assert.Equal(3, line - UsingNormalizer.LineShift(raw, generated));

      var item = new System.Text.Json.Nodes.JsonObject { ["source"] = ModelScript };
      Assert.True(BuildScriptParser.TryParse(item, BuildScriptTypes.CSharpTypeName, "c", out var source, out _, out _, out var parsedShift, out _));
      Assert.Equal(UsingNormalizer.LineShift(ModelScript, source), parsedShift);
      Assert.True(parsedShift > 0);

      var approved = new ApprovedScript { Region = "C1", Language = BuildScriptTypes.CSharp, Source = source, LineShift = parsedShift };
      var back = ScriptsAsk.FromRecord(ScriptsAsk.Record(new[] { approved }));
      Assert.Equal(parsedShift, Assert.Single(back).LineShift);
    }

    [Fact]
    public void U88_a_compile_failure_carries_the_mapped_lines_and_the_generated_signature_and_the_hint_notes()
    {
      var facts = new ScriptBuildFacts
      {
        Ref = "s", Name = "Serpentine", Language = BuildScriptTypes.CSharp, LineShift = 17,
        Errors = new[] { "Cannot implicitly convert type 'double' to 'int' [24:16]" },
        GeneratedSignature = "private void RunScript(Curve front_path, int columns, ref object pts)",
        HintNotes = new[] { "no hint for type List<double> on input height_profile; it arrives as its source's type" },
      };
      var j = ScriptBuildVerdict.Judge(new[] { facts }, null);

      Assert.False(j.Stands);
      Assert.Contains("[line 7 of your script, line 24 of the compiled file, col 16]", j.Refusal);
      Assert.Contains("private void RunScript(Curve front_path, int columns, ref object pts)", j.Refusal);
      Assert.Contains("no hint for type List<double> on input height_profile", j.Refusal);
      Assert.Contains("line 7 of your script", j.UserRefusal);

      // Python is not remapped and gets no signature
      var py = new ScriptBuildFacts { Ref = "p", Name = "P", Language = BuildScriptTypes.Python, LineShift = 17, Errors = new[] { "bad [3:1]" } };
      var pj = ScriptBuildVerdict.Judge(new[] { py }, null);
      Assert.Contains("[3:1]", pj.Refusal);
      Assert.DoesNotContain("of your script", pj.Refusal);
      Assert.DoesNotContain("generated signature", pj.Refusal);
    }

    [Fact]
    public void U88_a_standing_script_says_which_types_arrived_as_their_sources()
    {
      var facts = new ScriptBuildFacts
      {
        Ref = "s", Name = "S", Language = BuildScriptTypes.CSharp,
        Outputs = new[] { new ScriptOutputRead { Port = "pts", Items = 3, NonNull = 3 } },
        HintNotes = new[] { "no hint for type float on input a; it arrives as its source's type" },
      };
      var j = ScriptBuildVerdict.Judge(new[] { facts }, null);
      Assert.True(j.Stands);
      Assert.Contains("no hint for type float on input a; it arrives as its source's type", j.Notes["s"]);
    }

    [Fact]
    public void U88_the_hints_are_chosen_from_the_signature_before_AddObject_in_a_single_hint_call()
    {
      var s = File.ReadAllText(Path.Combine(RepoRoot(), "Dwight", "Agent", "GhBuildRegion.cs"));
      var plan = s.IndexOf("CSharpSignature.Plan(", StringComparison.Ordinal);
      var add = s.IndexOf("d.AddObject(obj, false", StringComparison.Ordinal);
      var inputHints = s.IndexOf("SelectInputHints(", StringComparison.Ordinal);
      var outputHints = s.IndexOf("SelectOutputHints(", StringComparison.Ordinal);
      Assert.True(plan > 0 && plan < inputHints && inputHints < add && outputHints < add);
      Assert.Equal(inputHints, s.LastIndexOf("SelectInputHints(", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- U89

    [Fact]
    public void U89_the_note_says_the_sent_source_was_not_built_before_the_diff_line()
    {
      var spec = Build("n = 1  # corrected", new[] { "Columns" }, new string[0]);
      var card = Card("n = 0  # Columns", new[] { "Columns" });
      Assert.Null(BuildScriptGate.Adopt(spec, false, new[] { card }, out var subs));

      var note = ScriptSubstitution.NoteFor(subs);
      Assert.StartsWith("Your sent source was NOT built. The source on the card the user approved was built instead. " +
                        "To run changed source, raise a new card with it.", note);
      Assert.True(note.IndexOf("first differed", StringComparison.Ordinal) > note.IndexOf("raise a new card with it.", StringComparison.Ordinal));

      // a rename with the same source says nothing about source, so no notice
      var same = ScriptSubstitution.NoteFor(new[] { new ScriptSubstitution { Ref = "t", PortOrderDiffered = true } });
      Assert.DoesNotContain("NOT built", same);
      Assert.Null(ScriptSubstitution.NoteFor(new ScriptSubstitution[0]));
    }

    [Fact]
    public void U89_an_exhausted_region_after_a_substitution_says_the_failed_attempts_ran_the_cards_source()
    {
      Assert.Contains("the attempts that failed ran the card's source, not yours", BuildLedger.ExhaustedMessage("C1", 3, true));
      Assert.DoesNotContain("card's source", BuildLedger.ExhaustedMessage("C1", 3));
      Assert.StartsWith("You have used all 3 attempts at C1 in this turn.", BuildLedger.ExhaustedMessage("C1", 3, true));

      var ledger = new BuildLedger(new object());
      Assert.False(ledger.CardSourceBuilt("C1"));
      ledger.NoteCardSourceBuilt("C1");
      Assert.True(ledger.CardSourceBuilt("C1"));
      Assert.False(ledger.CardSourceBuilt("C2"));
      ledger.BeginTurn(new object());
      Assert.False(ledger.CardSourceBuilt("C1"));   // a turn's fact dies with the turn

      var s = File.ReadAllText(Path.Combine(RepoRoot(), "Dwight", "Agent", "GhBuildRegion.cs"));
      Assert.Contains("ExhaustedMessage(spec.Region, DwightConfig.AgentBuildMaxAttemptsPerRegion, ledger.CardSourceBuilt(spec.Region))", s);
      Assert.Contains("NoteCardSourceBuilt(spec.Region)", s);
      // the failure path carries the notice too, so a model that sent a corrected script is told on the failure itself
      Assert.Contains("ScriptSubstitution.NoteFor(spec.CardScripts)", s);
    }

    // ---------------------------------------------------------------- U87

    [Fact]
    public void U87_the_note_names_the_role_and_the_name_of_what_was_missing()
    {
      var refs = new BuildUndoRefs();
      var group = Guid.NewGuid(); var comp = Guid.NewGuid(); var target = Guid.NewGuid();
      refs.Add(BuildUndoRefs.RoleRegionGroup, "C1", group);
      refs.Add(BuildUndoRefs.RolePlacedComponent, "Serpentine", comp);
      refs.Add(BuildUndoRefs.RoleWireTarget, "front_path", target);

      var missing = refs.FirstMissingDescribed(g => g != target);
      Assert.Equal(target, missing.Guid);
      Assert.Equal("wire target", missing.Role);
      Assert.Equal("front_path", missing.Name);
      Assert.Equal("The undo stack could not restore this build because the wire target 'front_path' no longer exists, so Dwight removed " +
                   "what it had placed instead. Check the wiring of anything the build rewired.", BuildUndoRefs.RevertNote(missing));

      Assert.Equal("placed component", refs.FirstMissingDescribed(g => g != comp).Role);
      Assert.Equal("region group", refs.FirstMissingDescribed(g => g != group).Role);
      Assert.Null(refs.FirstMissingDescribed(g => true));
      Assert.Contains("because the wire source no longer exists", BuildUndoRefs.RevertNote(
        new BuildUndoRefs.MissingRef { Role = BuildUndoRefs.RoleWireSource }));

      // a reference recorded without a description still reverts, with the old generic line
      var plain = new BuildUndoRefs(); var lost = Guid.NewGuid(); plain.Add(lost);
      Assert.Equal(BuildUndoRefs.RevertUsedRecordNote, BuildUndoRefs.RevertNote(plain.FirstMissingDescribed(g => false)));
    }

    [Fact]
    public void U87_the_revert_kit_records_a_role_and_a_name_for_every_reference_and_uses_the_described_note()
    {
      var s = File.ReadAllText(Path.Combine(RepoRoot(), "Dwight", "Agent", "GhBuildRegion.cs"));
      Assert.Contains("Refs.Add(BuildUndoRefs.RoleRegionGroup", s);
      Assert.Contains("Refs.Add(BuildUndoRefs.RolePlacedComponent", s);
      Assert.Contains("Refs.Add(BuildUndoRefs.RoleWireTarget", s);
      Assert.Contains("note = BuildUndoRefs.RevertNote(missing)", s);
    }

    /// <summary>THE READING OF WHY A SCRIPT BUILD'S WIRE ACTIONS CAN POINT AT VANISHED PARAMETERS, held to the text: the architect's
    /// hypothesis (ApplyScript appends ports, then SetSource rebuilds them) is not what the code does. ApplyScript sets the source
    /// FIRST, then removes the defaults, then appends; the hint selection that replaces a parameter runs before AddObject; and
    /// the wires are resolved only afterwards. Nothing in the build itself replaces a script parameter after a wire exists.</summary>
    [Fact]
    public void U87_the_source_is_set_before_the_ports_and_nothing_replaces_a_parameter_after_the_first_wire()
    {
      var s = File.ReadAllText(Path.Combine(RepoRoot(), "Dwight", "Agent", "GhBuildRegion.cs"));
      var apply = s.IndexOf("private static void ApplyScript(", StringComparison.Ordinal);
      var setSource = s.IndexOf("ScriptComponentShim.SetSource(", apply, StringComparison.Ordinal);
      var remove = s.IndexOf("ScriptComponentShim.RemoveDefaults(", apply, StringComparison.Ordinal);
      var append = s.IndexOf("ScriptComponentShim.Append(", apply, StringComparison.Ordinal);
      Assert.True(apply > 0 && apply < setSource && setSource < remove && remove < append, "source, then defaults out, then ports in");

      var wire = s.IndexOf("WireAll(d, spec", StringComparison.Ordinal);
      Assert.True(s.IndexOf("SelectInputHints(", StringComparison.Ordinal) < wire);
      Assert.True(s.IndexOf("SelectOutputHints(", StringComparison.Ordinal) < wire);
      Assert.True(s.IndexOf("ApplyScript(obj, c);", StringComparison.Ordinal) < wire);
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
