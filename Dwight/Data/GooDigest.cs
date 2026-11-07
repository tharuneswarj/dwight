using System;
using System.Globalization;
using System.Text;
using GH_IO.Serialization;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;

namespace Dwight.Data
{
  /// <summary>
  /// The per-item digest SHIM: unwrap a goo into <see cref="ItemFacts"/>, then let the GH-free
  /// <see cref="ItemDigest"/> decide. Both verification samplers use it - layer 2's solved-output digest
  /// (<c>SimplifyApply.SampleParam</c>) and layer 1's persistent-value digest
  /// (<c>BehaviourFingerprint.PersistentToken</c>).
  ///
  /// It has now failed in BOTH directions, which is why it is shaped the way it is:
  ///
  ///  - RR6, a false POSITIVE: digesting <c>ToString()</c> passed a MOVED RECTANGLE as verified, because geometry
  ///    renders as a type description ("Rectangle") - two different rectangles print the same words. Fixed by
  ///    hashing the goo's serialized bytes.
  ///  - RR18, a false NEGATIVE: hashing the serialized bytes rejected a CORRECT script, every time, because the
  ///    BYTES CARRY THE WRAPPER TYPE. Measured on the live canvas: the original Addition emits GH_Integer(150) in
  ///    42 bytes; the script's untyped output emits GH_Number(150.0) in 44 bytes. Same value, different bytes, so
  ///    no to-script could EVER pass - Tharun proved it by hand-building the same node and getting the same 150.
  ///
  /// So: UNWRAP FIRST, then digest by type-class. A number is compared as a NUMBER (GH_Integer(150),
  /// GH_Number(150.0) and a generically-wrapped 150 are one token - correct, not lax, because Grasshopper coerces
  /// them identically downstream and no definition can tell them apart). Geometry is checked BEFORE any text path
  /// or RR6 walks straight back in. Anything else falls through to the byte-exact serialization, so an unknown type
  /// is still compared strictly rather than waved through.
  /// </summary>
  internal static class GooDigest
  {
    public static string Item(object item) => ItemDigest.From(Unwrap(item));

    /// <summary>Unwrap one item into the facts the digest decides on. The ORDER of these checks is the law:
    /// number, then GEOMETRY, then text, then bytes.</summary>
    private static ItemFacts Unwrap(object item)
    {
      if (item == null) return ItemFacts.Empty();

      var goo = item as IGH_Goo;
      object value = item;
      if (goo != null)
      {
        try { value = goo.ScriptVariable() ?? item; } catch { value = item; }
      }

      // 1. NUMBER - by the unwrapped CLR type, never by convertibility. Normalisation must not become COERCION:
      //    GH_Convert.ToDouble would happily turn the TEXT "150" into the NUMBER 150 and mask a real change.
      //    This is the ONLY class where the wrappers genuinely disagree (measured: GH_Integer vs GH_Number).
      if (IsNumeric(value)) return ItemFacts.OfNumber(Convert.ToDouble(value, CultureInfo.InvariantCulture));

      // 2. GEOMETRY - before ANY text path (RR6), and EXACTLY, not by a coarse signature. The unwrapped geometry
      //    serializes wrapper-free and deterministically (measured), so this is both wrapper-agnostic AND
      //    byte-exact: two same-sized rectangles on different planes still differ, which is the RR6 guarantee.
      //    A signature (bbox + endpoints + length) would be WEAKER - two different curves can share all three -
      //    so it is only ever the fallback when a geometry type refuses to serialize.
      if (value is GeometryBase gb)
      {
        var exact = TryGeometryJson(gb);
        if (exact != null) return ItemFacts.OfGeometry(exact);
        var sig = TryGeometrySignature(goo, value);
        if (sig != null) return ItemFacts.OfGeometry(sig);
      }

      // 3. The geometric STRUCTS (Point3d, Plane, ...): not GeometryBase, but exactly encodable from their
      //    components - wrapper-free and lossless, so they never depend on which goo carried them.
      var canon = TryCanonicalStruct(value);
      if (canon != null) return ItemFacts.OfGeometry(canon);

      // 4. TEXT.
      if (value is string s) return ItemFacts.OfText(s);

      // 5. Byte-exact fallback: an unknown type stays STRICTLY compared (a colour, a boolean, a plugin's own goo).
      //    Sound here because GH gives these canonical wrappers on both sides (measured); it is NOT sound as a
      //    cross-type value comparison, which was the whole RR18 bug.
      if (goo != null)
      {
        var bytes = TrySerialize(goo);
        if (bytes != null && bytes.Length > 0) return ItemFacts.OfBytes(Fnv1a(bytes));
      }
      return ItemFacts.OfText(value.ToString());   // the honest last resort
    }

    /// <summary>The unwrapped geometry's OWN serialization - wrapper-free and exact. This is what keeps RR6 fixed
    /// while RR18 is fixed: the comparison no longer depends on which goo type carried the geometry, but it is
    /// still byte-exact about the geometry itself.</summary>
    private static string TryGeometryJson(GeometryBase geo)
    {
      try
      {
        var json = geo.ToJSON(new Rhino.FileIO.SerializationOptions());
        if (string.IsNullOrEmpty(json)) return null;
        return "j:" + Fnv1a(Encoding.UTF8.GetBytes(json));
      }
      catch { return null; }
    }

    /// <summary>Exact, wrapper-free encodings for the geometric VALUE types (they are structs, so they are not
    /// GeometryBase and have no serializer of their own). Lossless: every component, round-tripped and invariant.</summary>
    private static string TryCanonicalStruct(object v)
    {
      string N(double d) => ItemDigest.Canonical(d);
      switch (v)
      {
        case Point3d p: return $"p:{N(p.X)},{N(p.Y)},{N(p.Z)}";
        case Vector3d v3: return $"v:{N(v3.X)},{N(v3.Y)},{N(v3.Z)}";
        case Line l: return $"l:{N(l.FromX)},{N(l.FromY)},{N(l.FromZ)},{N(l.ToX)},{N(l.ToY)},{N(l.ToZ)}";
        case Plane pl: return $"pl:{N(pl.OriginX)},{N(pl.OriginY)},{N(pl.OriginZ)},{N(pl.XAxis.X)},{N(pl.XAxis.Y)},{N(pl.XAxis.Z)},{N(pl.YAxis.X)},{N(pl.YAxis.Y)},{N(pl.YAxis.Z)}";
        case Interval iv: return $"iv:{N(iv.T0)},{N(iv.T1)}";
        case Circle ci: return $"ci:{N(ci.Radius)},{N(ci.Center.X)},{N(ci.Center.Y)},{N(ci.Center.Z)},{N(ci.Normal.X)},{N(ci.Normal.Y)},{N(ci.Normal.Z)}";
        default: return null;
      }
    }

    private static bool IsNumeric(object v) =>
      v is double || v is float || v is decimal || v is int || v is long || v is short || v is byte
      || v is uint || v is ulong || v is ushort || v is sbyte;
      // bool is deliberately NOT numeric: true == 1 would be a coercion, and it falls to the byte-exact path.

    /// <summary>The geometric signature: the axis-aligned bounding box, a representative curve's endpoints, and its
    /// length. Null when the item carries no geometry. This is what makes two same-sized rectangles on different
    /// planes digest differently (RR6) - and it now runs BEFORE any text path, not merely as a serialization
    /// fallback, so a geometry type can never be captured by <c>ToString()</c>.</summary>
    private static string TryGeometrySignature(IGH_Goo goo, object value)
    {
      try
      {
        var bb = BoundingBox.Unset;
        if (goo is IGH_GeometricGoo g) bb = g.Boundingbox;
        else if (value is GeometryBase gb) bb = gb.GetBoundingBox(true);
        else if (value is Point3d p) bb = new BoundingBox(p, p);

        double sx = 0, sy = 0, sz = 0, ex = 0, ey = 0, ez = 0, len = 0;
        var hasCurve = false;
        if (value is Curve c)
        {
          var st = c.PointAtStart; var en = c.PointAtEnd;
          sx = st.X; sy = st.Y; sz = st.Z; ex = en.X; ey = en.Y; ez = en.Z;
          try { len = c.GetLength(); } catch { }
          hasCurve = true;
        }
        if (!bb.IsValid && !hasCurve) return null;   // not geometry: let the caller fall through to text / bytes
        var min = bb.IsValid ? bb.Min : Point3d.Origin;
        var max = bb.IsValid ? bb.Max : Point3d.Origin;
        return GeometryDigest.Signature(min.X, min.Y, min.Z, max.X, max.Y, max.Z, sx, sy, sz, ex, ey, ez, len);
      }
      catch { return null; }
    }

    /// <summary>Serialize a goo through GH's own writer (the same self-serialization GH uses for copy/paste and
    /// undo). Deterministic within a type - measured - so it is a sound byte-exact fallback; it is NOT sound as a
    /// value comparison ACROSS types, which is precisely the RR18 bug.</summary>
    private static byte[] TrySerialize(IGH_Goo goo)
    {
      try
      {
        var chunk = new GH_LooseChunk("dwight-digest");
        if (!goo.Write(chunk)) return null;
        return chunk.Serialize_Binary();
      }
      catch { return null; }
    }

    // FNV-1a 64-bit: cheap, allocation-free, within-process only (no crypto strength needed).
    private static ulong Fnv1a(byte[] bytes)
    {
      var h = 14695981039346656037UL;
      foreach (var b in bytes) { h ^= b; h *= 1099511628211UL; }
      return h;
    }
  }
}
