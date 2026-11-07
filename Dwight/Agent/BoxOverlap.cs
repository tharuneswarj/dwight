using System.Collections.Generic;

namespace Dwight.Agent
{
  /// <summary>
  /// Phase-9 U2: the bounding-box PRE-FILTER behind find_intersecting (study §6, finding 5). "What
  /// intersects this" on a large document is a bounded query, not a scan: the cheap axis-aligned test
  /// runs over every pair, the expensive true test runs only over the survivors, and the survivor list
  /// itself is capped and says so. Pure doubles; RhinoCommon never enters (ADR-091).
  /// </summary>
  internal static class BoxOverlap
  {
    /// <summary>Do two axis-aligned boxes overlap, treating a gap within <paramref name="tol"/> as a touch?</summary>
    public static bool Overlaps(double[] aMin, double[] aMax, double[] bMin, double[] bMax, double tol)
    {
      if (aMin == null || aMax == null || bMin == null || bMax == null) return false;
      for (var k = 0; k < 3; k++)
      {
        if (aMax[k] + tol < bMin[k]) return false;
        if (bMax[k] + tol < aMin[k]) return false;
      }
      return true;
    }

    /// <summary>
    /// The candidate pairs (i in <paramref name="a"/>, j in <paramref name="b"/>) whose boxes overlap.
    /// With <paramref name="selfPairs"/> the two lists are the SAME set and each unordered pair appears
    /// once (i &lt; j), never a box against itself. Stops at <paramref name="maxPairs"/> and reports it.
    /// </summary>
    public static List<(int, int)> Prefilter(
      List<(double[] min, double[] max)> a, List<(double[] min, double[] max)> b,
      double tol, int maxPairs, out bool capped, bool selfPairs)
    {
      capped = false;
      var pairs = new List<(int, int)>();
      if (a == null || b == null) return pairs;

      for (var i = 0; i < a.Count; i++)
      {
        for (var j = selfPairs ? i + 1 : 0; j < b.Count; j++)
        {
          if (!Overlaps(a[i].min, a[i].max, b[j].min, b[j].max, tol)) continue;
          if (pairs.Count >= maxPairs) { capped = true; return pairs; }
          pairs.Add((i, j));
        }
      }
      return pairs;
    }
  }
}
