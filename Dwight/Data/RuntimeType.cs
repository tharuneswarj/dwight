using System;
using System.Collections;
using System.Globalization;
using System.Linq;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;

namespace Dwight.Data
{
  /// <summary>
  /// RR19/RR20 #40 + #50: what is ACTUALLY on the wire, read off the live SOLVED graph.
  ///
  /// The Linear-Array failure was not a model failure - it was a payload failure. The plugin told the model
  /// "input0: double" and stopped. The model, seeing a Linear Array in the selection, reasonably guessed that the
  /// double was the COUNT, named it count_d, and wrote (int)0.75 = 0 copies. The double was in fact a Unit Z
  /// FACTOR, and its value was 0.75. Both facts were sitting in the live document and neither was sent.
  ///
  /// So: the type is not the meaning. "Curve" does not say "closed planar polyline with 4 segments"; "double" does
  /// not say "the Factor of a Unit Z, currently 0.75". This class reads the CONCRETE type and the CONSTANT VALUE
  /// off the solved goo, and that is free and cannot be wrong - it is measured, not inferred.
  /// </summary>
  internal static class RuntimeType
  {
    /// <summary>The concrete runtime type on a param's wire ("Line curves", "closed planar polylines",
    /// "NurbsCurve degree 3", "Brep, 6 faces"), or null when there is no data to read.</summary>
    public static string Describe(IGH_Param p)
    {
      try
      {
        var data = p?.VolatileData;
        if (data == null || data.PathCount == 0) return null;
        object first = null;
        var count = 0;
        foreach (var path in data.Paths)
        {
          var branch = data.get_Branch(path) as IList;
          if (branch == null) continue;
          count += branch.Count;
          if (first == null && branch.Count > 0) first = branch[0];
        }
        if (first == null) return null;
        var one = DescribeGoo(first);
        return count > 1 ? $"{count} x {one}" : one;
      }
      catch { return null; }
    }

    /// <summary>One goo's concrete type, in the words a programmer needs to write code against it.</summary>
    public static string DescribeGoo(object item)
    {
      try
      {
        object v = item;
        if (item is IGH_Goo g) { try { v = g.ScriptVariable() ?? item; } catch { } }

        switch (v)
        {
          case null: return "null";
          case double d: return "double (" + Num(d) + ")";
          case int i: return "int (" + i + ")";
          case bool b: return "bool (" + (b ? "true" : "false") + ")";
          case string s: return "string (\"" + Trim(s) + "\")";
          case Point3d pt: return $"Point3d ({Num(pt.X)},{Num(pt.Y)},{Num(pt.Z)})";
          case Vector3d vec: return $"Vector3d ({Num(vec.X)},{Num(vec.Y)},{Num(vec.Z)})";
          case Plane pl: return $"Plane (origin {Num(pl.OriginX)},{Num(pl.OriginY)},{Num(pl.OriginZ)})";
          case Interval iv: return $"Interval ({Num(iv.T0)} to {Num(iv.T1)})";
          case Line ln: return $"Line (length {Num(ln.Length)})";
          case Curve c: return DescribeCurve(c);
          case Brep br: return $"Brep ({br.Faces.Count} face(s), {(br.IsSolid ? "solid" : "open")})";
          case Mesh m: return $"Mesh ({m.Faces.Count} face(s), {m.Vertices.Count} vertices)";
          case Surface sf: return $"Surface ({(sf.IsClosed(0) || sf.IsClosed(1) ? "closed" : "open")})";
          default: return v.GetType().Name;
        }
      }
      catch { return null; }
    }

    /// <summary>A curve in the terms that decide what code compiles and what it computes: its concrete class, and
    /// whether it is closed and planar - the facts a script has to branch on.</summary>
    private static string DescribeCurve(Curve c)
    {
      string kind;
      switch (c)
      {
        case LineCurve _: kind = "LineCurve"; break;
        case ArcCurve ac: kind = ac.IsCircle() ? "ArcCurve (circle)" : "ArcCurve (arc)"; break;
        case PolylineCurve pc: kind = $"PolylineCurve ({Math.Max(0, pc.PointCount - 1)} segments)"; break;
        case PolyCurve pc2: kind = $"PolyCurve ({pc2.SegmentCount} segments)"; break;
        case NurbsCurve nc: kind = $"NurbsCurve (degree {nc.Degree})"; break;
        default: kind = c.GetType().Name; break;
      }
      var closed = c.IsClosed ? "closed" : "open";
      var planar = c.IsPlanar() ? ", planar" : "";
      return $"{kind}, {closed}{planar}";
    }

    /// <summary>
    /// The VALUE a port carries when it is a CONSTANT - a slider, a panel, an internalized param. This is the fact
    /// that killed the Linear-Array case: the model was told "double" and guessed a count, when the document
    /// plainly said 0.75. A constant is not context, it is the answer.
    ///
    /// Null when the source is computed (i.e. genuinely varies), because then there is no constant to state.
    /// </summary>
    public static string ConstantValue(IGH_DocumentObject obj)
    {
      try
      {
        if (obj is GH_NumberSlider slider)
          return Num((double)slider.CurrentValue);

        // A FED panel shows what flows into it; its UserText is the placeholder ("Double click to edit panel content...")
        // and is not a constant (U31, #947 - PR #54 deferral 1). Same rule as any param below: computed upstream.
        if (obj is GH_Panel panel)
          return panel.SourceCount > 0 ? null : "\"" + Trim(panel.UserText ?? "") + "\"";

        if (obj is IGH_Param p)
        {
          if (p.SourceCount > 0) return null;   // computed upstream: not a constant
          var d = Describe(p);                  // solved data (an internalized point, a baked curve, ...)
          if (d != null) return d;
        }
      }
      catch { }
      return null;
    }

    private static string Num(double d) => Math.Round(d, 6).ToString("R", CultureInfo.InvariantCulture);
    private static string Trim(string s)
    {
      s = (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
      return s.Length > 60 ? s.Substring(0, 60) + "..." : s;
    }
  }
}
