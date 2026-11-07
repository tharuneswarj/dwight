using System;
using System.Collections.Generic;
using System.Linq;

namespace Dwight.Agent
{
  /// <summary>
  /// U77: THE REVERT MUST NEVER ASK GRASSHOPPER TO UNDO WHAT IT CAN NO LONGER FIND. An undo action whose object or parameter has
  /// gone (a script input whose type hint was selected after the action was recorded is replaced by a new parameter) makes
  /// <c>GH_Document.Undo</c> raise its own modal breakpoint, "Undo failed: Parameter could not be found", before any catch of ours runs.
  /// So the build keeps the guids each recorded action refers to, one entry per action, and checks them before it calls Undo.
  /// GH-free (ADR-091): the document's answer to "does this still exist" is a delegate.
  /// </summary>
  internal sealed class BuildUndoRefs
  {
    private readonly List<IReadOnlyList<Guid>> _actions = new List<IReadOnlyList<Guid>>();
    private readonly Dictionary<Guid, MissingRef> _described = new Dictionary<Guid, MissingRef>();

    public const string RolePlacedComponent = "placed component";
    public const string RoleRegionGroup = "region group";
    public const string RoleWireTarget = "wire target";
    public const string RoleWireSource = "wire source";
    /// <summary>U91: the group a grouped spot build makes. Made inside the undo record, and so named in the kit the guarded revert removes from.</summary>
    public const string RoleSpotGroup = "spot group";

    /// <summary>One recorded action and the guids it will need to find when it is undone.</summary>
    public void Add(params Guid[] referenced) => _actions.Add(referenced ?? new Guid[0]);

    /// <summary>U87: one recorded action that refers to ONE object, with the role it plays and its nickname or port name, so a
    /// missing one can be named. The role is one of the <c>Role*</c> constants; the name may be empty.</summary>
    public void Add(string role, string name, Guid referenced)
    {
      _actions.Add(new[] { referenced });
      _described[referenced] = new MissingRef { Guid = referenced, Role = role, Name = name };
    }

    public IReadOnlyList<IReadOnlyList<Guid>> Actions => _actions;

    /// <summary>What an undo could not find: the guid, the role it plays in the build, and its name where known (U87).</summary>
    public sealed class MissingRef
    {
      public Guid Guid { get; set; }
      public string Role { get; set; }
      public string Name { get; set; }
    }

    /// <summary><see cref="FirstMissing(Func{Guid, bool})"/>, with the role and name the build recorded for it. A guid recorded
    /// without a description comes back with no role.</summary>
    public MissingRef FirstMissingDescribed(Func<Guid, bool> exists)
    {
      var guid = FirstMissing(exists);
      if (guid == null) return null;
      return _described.TryGetValue(guid.Value, out var d) ? d : new MissingRef { Guid = guid.Value };
    }

    /// <summary>U87: the verdict's line when the revert used the build's own record: WHICH reference was missing, by role and
    /// name, so "a parameter could not be found" is not all anyone is told.</summary>
    public static string RevertNote(MissingRef missing)
    {
      if (missing == null || string.IsNullOrEmpty(missing.Role)) return RevertUsedRecordNote;
      var what = missing.Role + (string.IsNullOrWhiteSpace(missing.Name) ? "" : " '" + missing.Name.Trim() + "'");
      return "The undo stack could not restore this build because the " + what + " no longer exists, so Dwight removed what it had placed instead. " +
             "Check the wiring of anything the build rewired.";
    }

    /// <summary>The first guid any action refers to that the document no longer holds, or null when every one is still there.</summary>
    public static Guid? FirstMissing(IEnumerable<IReadOnlyList<Guid>> actions, Func<Guid, bool> exists)
    {
      foreach (var action in actions ?? Enumerable.Empty<IReadOnlyList<Guid>>())
        foreach (var guid in action ?? new Guid[0])
          if (guid != Guid.Empty && !(exists?.Invoke(guid) ?? false)) return guid;
      return null;
    }

    public Guid? FirstMissing(Func<Guid, bool> exists) => FirstMissing(_actions, exists);

    /// <summary>What the verdict says when the revert used the build's own record because the undo stack could not.</summary>
    public const string RevertUsedRecordNote =
      "The undo stack could not restore this build, so Dwight removed what it had placed instead. Check the wiring of anything the build rewired.";
  }
}
