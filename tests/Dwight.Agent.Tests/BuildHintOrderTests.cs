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
  /// U77: a scaffold-built script's type hints are chosen BEFORE the component enters the document, and a revert can never
  /// ask Grasshopper to undo what it can no longer find. GhBuildRegion is GH-coupled and not linked into these tests, so the
  /// order is pinned by reading its SOURCE (the bound is stated: this proves the text's order, not the behaviour; the live
  /// placement and revert are checked through the bridge after deploy). The pure halves are tested directly.
  /// </summary>
  public class BuildHintOrderTests
  {
    private static string Source() =>
      File.ReadAllText(Path.Combine(RepoRoot(), "Dwight", "Agent", "GhBuildRegion.cs"));

    [Fact]
    public void The_hint_is_selected_before_the_first_AddObject_and_never_after_the_wires()
    {
      var s = Source();
      var hint = s.IndexOf("SelectInputHints(", StringComparison.Ordinal);
      var add = s.IndexOf("d.AddObject(obj, false", StringComparison.Ordinal);
      var wire = s.IndexOf("WireAll(d, spec", StringComparison.Ordinal);

      Assert.True(hint > 0 && add > 0 && wire > 0, "the calls the order is read from are all present");
      Assert.True(hint < add, "the hint is chosen before the component is added to the document");
      Assert.True(add < wire);
      Assert.Equal(hint, s.LastIndexOf("SelectInputHints(", StringComparison.Ordinal));   // one call, none after the wires
      Assert.DoesNotContain("HintInputsFromSources", s);
    }

    [Fact]
    public void The_script_source_and_ports_are_set_before_the_hint_so_every_input_exists_to_hint()
    {
      var s = Source();
      Assert.True(s.IndexOf("ApplyScript(obj, c);", StringComparison.Ordinal) < s.IndexOf("SelectInputHints(", StringComparison.Ordinal));
    }

    [Fact]
    public void The_script_gate_decides_before_anything_is_added_to_the_document()
    {
      var s = Source();
      var gate = s.IndexOf("BuildScriptGate.Adopt(", StringComparison.Ordinal);
      Assert.True(gate > 0);
      Assert.True(gate < s.IndexOf("ScheduleSolution(10, d =>", StringComparison.Ordinal));
      Assert.True(gate < s.IndexOf("d.AddObject(obj, false", StringComparison.Ordinal));
    }

    [Fact]
    public void Document_Undo_is_called_only_behind_the_missing_object_check()
    {
      var s = Source();
      var calls = System.Text.RegularExpressions.Regex.Matches(s, @"\bdoc\.Undo\(\)").Count;
      Assert.Equal(1, calls);
      var undoOnce = s.IndexOf("private static string UndoOnce(", StringComparison.Ordinal);
      Assert.True(undoOnce > 0);
      Assert.True(s.IndexOf("FirstMissingDescribed(", undoOnce, StringComparison.Ordinal) < s.IndexOf("doc.Undo()", undoOnce, StringComparison.Ordinal));
    }

    [Fact]
    public void The_guard_names_a_guid_no_action_can_find_and_passes_a_complete_list()
    {
      var here = Guid.NewGuid();
      var gone = Guid.NewGuid();
      var alsoHere = Guid.NewGuid();
      var actions = new List<IReadOnlyList<Guid>>
      {
        new[] { here },
        new[] { alsoHere, gone },   // a wire action whose parameter was replaced
      };
      Func<Guid, bool> exists = g => g != gone;

      Assert.Equal(gone, BuildUndoRefs.FirstMissing(actions, exists));
      Assert.Null(BuildUndoRefs.FirstMissing(actions, g => true));
      Assert.Null(BuildUndoRefs.FirstMissing(new List<IReadOnlyList<Guid>>(), exists));

      var refs = new BuildUndoRefs();
      refs.Add(here); refs.Add(alsoHere, gone);
      Assert.Equal(gone, refs.FirstMissing(exists));
    }

    [Fact]
    public void Pre_add_hints_follow_the_sources_per_input()
    {
      var wires = new[]
      {
        new KeyValuePair<int, string>(0, "Curve"),
        new KeyValuePair<int, string>(1, "Number"),
        new KeyValuePair<int, string>(2, ""),               // a source whose type is unknown
        new KeyValuePair<int, string>(3, "Curve"),
        new KeyValuePair<int, string>(3, "Generic Data"),   // mixed with unknown
      };
      var hints = ScriptTypeMap.HintsByInput(wires, ScriptLanguage.CSharp);

      Assert.Equal("Curve", hints[0]);
      Assert.Equal("double", hints[1]);
      Assert.False(hints.ContainsKey(2));
      Assert.False(hints.ContainsKey(3));
      Assert.Equal("float", ScriptTypeMap.HintsByInput(wires, ScriptLanguage.Python3)[1]);
      Assert.Empty(ScriptTypeMap.HintsByInput(null, ScriptLanguage.CSharp));
    }

    [Fact]
    public void U84_declared_outputs_are_configured_before_AddObject_after_the_ports_exist()
    {
      var s = Source();
      var apply = s.IndexOf("private static void ApplyScript(", StringComparison.Ordinal);
      var append = s.IndexOf("ScriptComponentShim.Append(", apply, StringComparison.Ordinal);
      var configure = s.IndexOf("ScriptComponentShim.ConfigureDeclaredOutputs(", apply, StringComparison.Ordinal);
      Assert.True(apply > 0 && append > apply && configure > append, "outputs are configured after they are added, inside ApplyScript");
      Assert.True(s.IndexOf("ApplyScript(obj, c);", StringComparison.Ordinal) < s.IndexOf("d.AddObject(obj, false", StringComparison.Ordinal));
      Assert.Equal(s.IndexOf("ConfigureDeclaredOutputs(", StringComparison.Ordinal), s.LastIndexOf("ConfigureDeclaredOutputs(", StringComparison.Ordinal));
    }

    [Fact]
    public void U85_marsh_flags_are_set_in_ApplyScript_after_the_outputs_and_before_AddObject_and_by_Shape_after_SetSource()
    {
      var s = Source();
      var apply = s.IndexOf("private static void ApplyScript(", StringComparison.Ordinal);
      var configure = s.IndexOf("ScriptComponentShim.ConfigureDeclaredOutputs(", apply, StringComparison.Ordinal);
      var flags = s.IndexOf("ScriptComponentShim.SetMarshFlags(comp)", apply, StringComparison.Ordinal);
      Assert.True(flags > configure && configure > apply, "the flags are set inside ApplyScript, with the rest of the configuration");
      Assert.True(s.IndexOf("ApplyScript(obj, c);", StringComparison.Ordinal) < s.IndexOf("d.AddObject(obj, false", StringComparison.Ordinal), "ApplyScript, and so the flags, run before AddObject");
      Assert.Equal(s.IndexOf("SetMarshFlags(", StringComparison.Ordinal), s.LastIndexOf("SetMarshFlags(", StringComparison.Ordinal));

      var shim = File.ReadAllText(Path.Combine(RepoRoot(), "Dwight", "Simplify", "ScriptComponentShim.cs"));
      var shape = shim.IndexOf("public static bool Shape(", StringComparison.Ordinal);
      var setSource = shim.IndexOf("setSource.Invoke(comp", shape, StringComparison.Ordinal);
      var shapeFlags = shim.IndexOf("SetMarshFlags(comp)", shape, StringComparison.Ordinal);
      Assert.True(setSource > shape && shapeFlags > setSource, "to-script's Shape sets the flags after the source");
      // to-script adds the component only after Shape returns.
      var toScript = File.ReadAllText(Path.Combine(RepoRoot(), "Dwight", "Simplify", "ToScriptApply.cs"));
      Assert.True(toScript.IndexOf("ScriptComponentShim.Shape(", StringComparison.Ordinal) < toScript.IndexOf("d.AddObject(comp", StringComparison.Ordinal));
      // the property names are the three measured ones and nothing else is reflected by the setter
      var core = File.ReadAllText(Path.Combine(RepoRoot(), "Dwight", "Simplify", "ScriptMarshFlags.cs"));
      Assert.Contains("{ \"MarshInputs\", \"MarshOutputs\", \"MarshGuids\" }", core);
    }

    public class FakeWithFlags { public bool MarshInputs { get; set; } public bool MarshOutputs { get; set; } public bool MarshGuids { get; set; } }
    public class FakeSomeFlags { public bool MarshOutputs { get; set; } public bool MarshGuids { get; } = false; }

    [Fact]
    public void U85_the_guarded_setter_sets_all_three_when_present_and_leaves_and_records_an_absent_one()
    {
      var full = new FakeWithFlags();
      Assert.Equal(new[] { "MarshInputs", "MarshOutputs", "MarshGuids" }, ScriptMarshFlags.Set(full));
      Assert.True(full.MarshInputs && full.MarshOutputs && full.MarshGuids);

      var part = new FakeSomeFlags();
      Assert.Equal(new[] { "MarshOutputs" }, ScriptMarshFlags.Set(part));
      Assert.True(part.MarshOutputs);
      Assert.Contains("MarshInputs", ScriptMarshFlags.AbsentNames);   // no such property: left alone, recorded
      Assert.Contains("MarshGuids", ScriptMarshFlags.AbsentNames);    // read-only: left alone, recorded

      Assert.Empty(ScriptMarshFlags.Set(new object()));
      Assert.Empty(ScriptMarshFlags.Set(null));
    }

    [Fact]
    public void U84_to_script_and_the_scaffold_path_configure_an_output_through_one_function()
    {
      var shim = File.ReadAllText(Path.Combine(RepoRoot(), "Dwight", "Simplify", "ScriptComponentShim.cs"));
      // to-script's interface builder (Shape) and the scaffold path both call ConfigureOutput, and nothing else configures an output.
      var shape = shim.IndexOf("public static bool Shape(", StringComparison.Ordinal);
      Assert.True(shim.IndexOf("ConfigureOutput(outs[j]", shape, StringComparison.Ordinal) > shape);
      var declared = shim.IndexOf("public static void ConfigureDeclaredOutputs(", StringComparison.Ordinal);
      Assert.True(shim.IndexOf("ConfigureOutput(port", declared, StringComparison.Ordinal) > declared);
      Assert.DoesNotContain("Configure(outs[", shim);
      var build = File.ReadAllText(Path.Combine(RepoRoot(), "Dwight", "Agent", "GhBuildRegion.cs"));
      Assert.Contains("ScriptComponentShim.ConfigureDeclaredOutputs(", build);
    }

    [Fact]
    public void U84_a_declared_python_output_is_list_access_with_no_invented_hint_and_csharp_is_left_as_born()
    {
      Assert.Equal(ScriptAccess.List, ScriptTypeMap.DeclaredOutputAccess(ScriptLanguage.Python3));
      Assert.Null(ScriptTypeMap.DeclaredOutputAccess(ScriptLanguage.CSharp));
      Assert.Null(ScriptTypeMap.HintFor(null, ScriptLanguage.Python3));   // a declared output has no type: no hint, nothing Select could throw on
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
