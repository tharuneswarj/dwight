using System;
using System.Collections.Generic;
using System.Linq;

namespace Dwight.Agent
{
  /// <summary>
  /// PHASE-9 U36 - WHAT ONE CTRL-Z AFTER A SCRIPT EDIT WRITES BACK (#929).
  ///
  /// THE DEFECT. The apply recorded its undo with Grasshopper's generic per-object record. A Rhino 8 script
  /// component does not survive that: after a verified apply, Undo raised Grasshopper's own dialog - "Undo
  /// failed: Target parameter does not exist in document" - consumed the record, and left the new source in
  /// place. The card had promised "One Ctrl-Z undoes it".
  ///
  /// SO THE UNDO IS DWIGHT'S OWN, and it restores exactly two things, which are exactly what the edit changed:
  /// the SOURCE (the text the component held before, byte for byte - never a normalised form, because the user's
  /// own text is what they are owed back) and the PORT LIST (the edit only ever APPENDS ports, so the restore
  /// removes the ones it appended, from the end). Redo puts the new source back and appends the same names.
  ///
  /// THIS TYPE IS THE DATA AND THE ARITHMETIC; the Grasshopper action that carries it (<c>GhEditScript</c>) does
  /// only what this says. GH-free (ADR-091), because the undo path itself needs Grasshopper and only a sitting
  /// can witness it - so what CAN be asserted is asserted here: what the record holds, and what it would write.
  /// </summary>
  internal sealed class ScriptEditRestore
  {
    /// <summary>The source the component held BEFORE the edit, exactly as read. What undo writes back.</summary>
    public string BeforeSource { get; }

    /// <summary>The source the user approved. What redo writes back.</summary>
    public string AfterSource { get; }

    /// <summary>How many inputs the component had before the edit appended any.</summary>
    public int BeforeInputCount { get; }

    /// <summary>How many of its OWN outputs (the standard "out" stream excluded) it had before.</summary>
    public int BeforeOutputCount { get; }

    /// <summary>The ports the edit appends, by name, in order - what redo appends again.</summary>
    public IReadOnlyList<string> AppendedInputs { get; }
    public IReadOnlyList<string> AppendedOutputs { get; }

    /// <summary>Brief CP: the access of each input the edit changes, as it was. What undo writes back, beside the source and the ports.</summary>
    public IReadOnlyDictionary<string, string> BeforeAccess { get; }

    /// <summary>Brief CP: the access the edit sets, on the inputs it changes and the ones it appends. What redo writes back.</summary>
    public IReadOnlyDictionary<string, string> AfterAccess { get; }

    public ScriptEditRestore(string beforeSource, string afterSource, int beforeInputCount, int beforeOutputCount,
      IReadOnlyList<string> appendedInputs, IReadOnlyList<string> appendedOutputs,
      IReadOnlyDictionary<string, string> beforeAccess = null, IReadOnlyDictionary<string, string> afterAccess = null)
    {
      BeforeAccess = beforeAccess ?? new Dictionary<string, string>();
      AfterAccess = afterAccess ?? new Dictionary<string, string>();
      BeforeSource = beforeSource ?? "";
      AfterSource = afterSource ?? "";
      BeforeInputCount = Math.Max(0, beforeInputCount);
      BeforeOutputCount = Math.Max(0, beforeOutputCount);
      AppendedInputs = (appendedInputs ?? new string[0]).ToList();
      AppendedOutputs = (appendedOutputs ?? new string[0]).ToList();
    }

    /// <summary>
    /// How many inputs the undo removes, given how many the component has NOW: never more than Dwight appended,
    /// and never below the count it had before. In the ordinary case the two bounds agree; they are both here
    /// because an undo that could take the user's own port is worse than one that leaves Dwight's.
    /// </summary>
    public int InputsToRemove(int currentInputCount) => ToRemove(currentInputCount, BeforeInputCount, AppendedInputs.Count);

    /// <summary>The same rule for the script's own outputs.</summary>
    public int OutputsToRemove(int currentOutputCount) => ToRemove(currentOutputCount, BeforeOutputCount, AppendedOutputs.Count);

    private static int ToRemove(int current, int before, int appended) =>
      Math.Min(Math.Max(0, appended), Math.Max(0, current - before));

    /// <summary>Did the edit change the port list at all? A body-only edit restores the source and touches no port.</summary>
    public bool AppendsPorts => AppendedInputs.Count > 0 || AppendedOutputs.Count > 0;
  }
}
