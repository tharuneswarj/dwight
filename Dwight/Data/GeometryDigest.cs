using System;
using System.Text;

namespace Dwight.Data
{
  /// <summary>
  /// The typed geometric signature (RR6) - the fallback item digest when GH self-serialization is unavailable,
  /// and the headless-testable half of the geometry-aware digest. It composes a stable string from a geometry's
  /// axis-aligned bounding box, a representative curve's endpoints, and its length - enough to tell two rectangles
  /// apart that <c>ToString()</c> renders identically (a type description). Pure and GH-free (primitive doubles
  /// in, string out), so it link-compiles into the headless tests.
  ///
  /// BIT-EXACT, no tolerance anywhere (progress doc / RR6): a same-inputs re-solve is deterministic, so identical
  /// geometry yields identical doubles yields an identical signature; any positional or dimensional difference
  /// flips a bit. The doubles are encoded by their exact IEEE-754 bit pattern, so no formatting/rounding can
  /// collapse two nearby-but-distinct values.
  /// </summary>
  internal static class GeometryDigest
  {
    public static string Signature(
      double minX, double minY, double minZ,
      double maxX, double maxY, double maxZ,
      double startX, double startY, double startZ,
      double endX, double endY, double endZ,
      double length)
    {
      var sb = new StringBuilder("g:");
      Append(sb, minX); Append(sb, minY); Append(sb, minZ);
      Append(sb, maxX); Append(sb, maxY); Append(sb, maxZ);
      Append(sb, startX); Append(sb, startY); Append(sb, startZ);
      Append(sb, endX); Append(sb, endY); Append(sb, endZ);
      Append(sb, length);
      return sb.ToString();
    }

    private static void Append(StringBuilder sb, double d) => sb.Append(BitConverter.DoubleToInt64Bits(d)).Append(',');
  }
}
