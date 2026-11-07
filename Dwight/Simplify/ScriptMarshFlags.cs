using System;
using System.Collections.Generic;

namespace Dwight.Simplify
{
  /// <summary>
  /// U85: the three base-class flags a hand-made script component carries and a shim-made one is born without when SetSource
  /// runs before AddObject (measured through the bridge, Brief AC). With MarshOutputs false a Python list assigned to an output
  /// emits as ONE stringified item. GH-free so the guarded setter is tested against a fake. The names are measured, not guessed;
  /// reflection reaches nothing else here.
  /// </summary>
  internal static class ScriptMarshFlags
  {
    public static readonly string[] Names = { "MarshInputs", "MarshOutputs", "MarshGuids" };

    private static readonly object Gate = new object();
    private static readonly List<string> Absent = new List<string>();

    /// <summary>Names found absent or unwritable on a component (a future build), recorded rather than silent.</summary>
    public static IReadOnlyList<string> AbsentNames { get { lock (Gate) return Absent.ToArray(); } }

    /// <summary>Set each flag true if the component has a writable bool property of that name; leave an absent one alone and
    /// record it. Returns the names set. Never throws.</summary>
    public static IReadOnlyList<string> Set(object comp)
    {
      var set = new List<string>();
      if (comp == null) return set;
      foreach (var name in Names)
      {
        try
        {
          var prop = comp.GetType().GetProperty(name, typeof(bool));
          if (prop == null || !prop.CanWrite) { Record(name); continue; }
          prop.SetValue(comp, true);
          set.Add(name);
        }
        catch { Record(name); }
      }
      return set;
    }

    private static void Record(string name) { lock (Gate) if (!Absent.Contains(name)) Absent.Add(name); }
  }
}
