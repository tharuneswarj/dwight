using System;
using Dwight.Agent;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// Phase-9 U36 (#929) - WHAT ONE CTRL-Z WRITES BACK after a script edit.
  ///
  /// WHAT THESE TESTS CAN AND CANNOT WITNESS (#325). The undo path needs Grasshopper: whether the custom action
  /// replaces the generic per-object record that raised "Undo failed: Target parameter does not exist in document",
  /// whether Ctrl-Z restores the source and removes the appended ports with no dialog, and whether a second Ctrl-Z
  /// undoes the user's previous action - those are the gate row's, and only a sitting can see them. What CAN be
  /// held here is the record's DATA and its arithmetic: it holds the before-source byte for byte, the approved
  /// source, and the port counts; and it never removes more than Dwight appended or below the count the component
  /// had before.
  /// </summary>
  public class ScriptEditRestoreTests
  {
    private const string Before = "a = x * y   \n# the user's own spacing\n";
    private const string After = "a = x * y + float(offset)\n";

    private static ScriptEditRestore Restore(string[] inputs = null, string[] outputs = null) =>
      new ScriptEditRestore(Before, After, beforeInputCount: 2, beforeOutputCount: 1,
        appendedInputs: inputs ?? new string[0], appendedOutputs: outputs ?? new string[0]);

    /// <summary>The source undo writes back is the EXACT text read before the edit - never a normalised form.
    /// The user's own text is what they are owed back.</summary>
    [Fact]
    public void Undo_holds_the_before_source_byte_for_byte_and_redo_the_approved_one()
    {
      var r = Restore();
      Assert.Equal(Before, r.BeforeSource);
      Assert.Equal(After, r.AfterSource);
    }

    /// <summary>The ordinary case: the component has exactly what it had plus what Dwight appended, so undo removes
    /// exactly the appended ports and redo appends exactly those names.</summary>
    [Fact]
    public void Undo_removes_exactly_the_appended_ports()
    {
      var r = Restore(inputs: new[] { "offset" }, outputs: new[] { "debug", "extra" });

      Assert.Equal(1, r.InputsToRemove(currentInputCount: 3));
      Assert.Equal(2, r.OutputsToRemove(currentOutputCount: 3));
      Assert.Equal(new[] { "offset" }, r.AppendedInputs);
      Assert.Equal(new[] { "debug", "extra" }, r.AppendedOutputs);
      Assert.True(r.AppendsPorts);
    }

    /// <summary>A body-only edit restores the source and touches no port.</summary>
    [Fact]
    public void A_body_only_edit_removes_no_ports()
    {
      var r = Restore();
      Assert.False(r.AppendsPorts);
      Assert.Equal(0, r.InputsToRemove(2));
      Assert.Equal(0, r.OutputsToRemove(1));
    }

    /// <summary>NEVER MORE THAN DWIGHT APPENDED. If the component somehow has more ports than before plus the
    /// appended ones, the extra are not Dwight's to remove.</summary>
    [Fact]
    public void Undo_never_removes_more_than_it_appended()
    {
      var r = Restore(inputs: new[] { "offset" });
      Assert.Equal(1, r.InputsToRemove(currentInputCount: 5));
    }

    /// <summary>AND NEVER BELOW THE COUNT THE COMPONENT HAD BEFORE. If the component has fewer ports than expected,
    /// the restore removes only what sits above the before-count - possibly nothing.</summary>
    [Fact]
    public void Undo_never_removes_below_the_before_count()
    {
      var r = Restore(inputs: new[] { "offset", "scale" });
      Assert.Equal(1, r.InputsToRemove(currentInputCount: 3));   // only one above the two it had
      Assert.Equal(0, r.InputsToRemove(currentInputCount: 2));
      Assert.Equal(0, r.InputsToRemove(currentInputCount: 1));
    }

    /// <summary>Nulls are tolerated and never become a null field: a record that would throw inside an undo is a
    /// record that fails the undo.</summary>
    [Fact]
    public void Null_inputs_become_empty_not_null()
    {
      var r = new ScriptEditRestore(null, null, -1, -1, null, null);
      Assert.Equal("", r.BeforeSource);
      Assert.Equal("", r.AfterSource);
      Assert.Equal(0, r.BeforeInputCount);
      Assert.Equal(0, r.BeforeOutputCount);
      Assert.Empty(r.AppendedInputs);
      Assert.Empty(r.AppendedOutputs);
      Assert.Equal(0, r.InputsToRemove(3));
    }
  }
}
