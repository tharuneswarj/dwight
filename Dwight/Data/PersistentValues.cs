using System.Collections;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;

namespace Dwight.Data
{
  /// <summary>
  /// ONE READER for a param's PERSISTENT (internalised) data - Phase-9 U1's "one read path" rule (#382(9)):
  /// if the verb payload and the agent read disagree about a value, that is a defect, so both read through
  /// here. <c>PersistentData</c> is reached by reflection because it is declared on the generic
  /// <c>GH_PersistentParam&lt;T&gt;</c>, and every failure degrades to "none" (ADR-029).
  ///
  /// The description is ADR-093's shape exactly: the FIRST item's concrete type (with its value where the
  /// item is a primitive), the item count, the path count. Never the geometry.
  /// </summary>
  internal static class PersistentValues
  {
    public static IGH_Structure Structure(IGH_Param p)
    {
      try { return p?.GetType().GetProperty("PersistentData")?.GetValue(p) as IGH_Structure; }
      catch { return null; }
    }

    public static bool Has(IGH_Param p)
    {
      var s = Structure(p);
      return s != null && s.DataCount > 0;
    }

    /// <summary>The internalised data as a type, a count and a path count; false when there is none.</summary>
    public static bool TryDescribe(IGH_Param p, out int count, out int paths, out string item)
    {
      count = 0; paths = 0; item = null;
      var s = Structure(p);
      if (s == null || s.DataCount == 0) return false;

      count = s.DataCount;
      paths = s.PathCount;
      try
      {
        foreach (var path in s.Paths)
        {
          var branch = s.get_Branch(path) as IList;
          if (branch == null || branch.Count == 0) continue;
          item = RuntimeType.DescribeGoo(branch[0]);
          break;
        }
      }
      catch { /* the counts still stand; the item stays unknown rather than wrong */ }
      return true;
    }
  }
}
