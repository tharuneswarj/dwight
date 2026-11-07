using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;
using Dwight.Data;

namespace Dwight.Agent
{
  /// <summary>
  /// THE GEOMETRY READ SET's RhinoCommon reader (Phase-9 U2; #381 R3; study §6). GH-only - every
  /// decision it feeds lives GH-free in <see cref="GeometryArgs"/> and <see cref="GeometryReport"/>.
  ///
  /// "Why is this mesh wrong" is a solved deterministic problem and RhinoCommon holds every piece: this
  /// file only READS what it holds and hands the facts to the emitter. No model, no solve, no mutation
  /// (#190) - the reads are over the last solution's volatile data, exactly like get_component's counts.
  ///
  /// THE #392 RE-HOMING RULINGS, applied where each bites:
  ///   (a) the defect classes are whatever this Rhino's MeshCheckParameters exposes, read by REFLECTION
  ///       (ADR-092's pattern) - no hard-coded list can be stale, the payload is its own discriminator;
  ///   (b) every CheckFor* flag is set TRUE by reflection before Check runs, so a check that silently
  ///       checks nothing is UNREPRESENTABLE (#212 solved structurally), and the enabled flags are reported;
  ///   (d) an empty mesh is reported empty and not checked; anything that throws is reported with its
  ///       reason verbatim (readFailed / checkFailed / testFailed) - never a clean zero;
  ///   (e) the naked-edge COUNT and the sample LOCATIONS come from two calls, and a disagreement is said;
  ///   (f) an intersection test that did not answer carries its reason and NO verdict;
  ///   (c) every signature here is a compile-time fact against the referenced RhinoCommon (8.24), read
  ///       by reflection over the assembly before this file was written, not recalled.
  ///
  /// EVERY PAYLOAD IS BOUNDED (ADR-035, #209): objects per page, pairs tested per call, samples per
  /// object, chars of log, and a wall-clock budget on mass properties - each stated where it bites.
  /// </summary>
  internal static class GhGeometry
  {
    /// <summary>True intersection tests per find_intersecting call, at most. The bounding-box pre-filter
    /// runs over every candidate; only the survivors are tested, and only this many of them.</summary>
    public const int MaxPairs = 200;

    /// <summary>Wall clock get_geometry_summary spends on area/volume before it stops computing them
    /// for the rest of the page and says so. get_mass_properties has no budget: it was asked for.</summary>
    public const int MassPropsBudgetMs = 3000;

    // ---- the common pipeline ----------------------------------------------------------------------------

    private sealed class Resolved
    {
      public GeometryTarget Target;
      public AgentNode Node;
      public string Port;
      public IGH_Param Param;
    }

    private static bool TryResolve(GH_Document doc, AgentSnapshot snap, AgentIdRegistry ids, GeometryTarget t,
      out Resolved r, out ToolResult error)
    {
      r = null;
      if (!AgentToolCore.TryResolve(snap, ids, t.Id, out var node, out error)) return false;
      if (!GeometryArgs.TryResolveOutput(node, t.Param, out var port, out error)) return false;

      IGH_Param param = null;
      try
      {
        // At the top level or inside a readable cluster (Phase-9 U1(c)) - the same allow-listed path.
        var obj = GhAgent.FindLive(doc, ids, node);
        if (obj is IGH_Param p) param = p;
        else if (obj is IGH_Component c)
          param = c.Params.Output.FirstOrDefault(x =>
            string.Equals(DwightGraph.ParamLabel(x), port, StringComparison.OrdinalIgnoreCase));
      }
      catch { param = null; }

      if (param == null)
      {
        error = ToolResult.Fail(ToolErrors.RetiredId,
          "Component " + t.Id + " is not on the canvas any more, or its output \"" + port + "\" is gone.",
          new JsonObject { ["id"] = t.Id });
        return false;
      }
      r = new Resolved { Target = t, Node = node, Port = port, Param = param };
      return true;
    }

    /// <summary>Every item on the output, as geometry where it is geometry. A struct that carries
    /// geometry (a point, a line, a box) is promoted so the facts read the same; anything else is
    /// counted as non-geometry and said.</summary>
    /// <remarks>Internal, not private, for ONE other reader: the build's post-solve facts (U26, <c>GhBuildRegion</c>)
    /// read each placed output through this and <see cref="Facts"/> so the buildline states the same facts the
    /// geometry reads would - one reader, never a second one that could disagree.</remarks>
    internal static List<GeometryBase> Items(IGH_Param p, out int nonGeometry)
    {
      var list = new List<GeometryBase>();
      nonGeometry = 0;
      IGH_Structure data = null;
      try { data = p.VolatileData; } catch { }
      if (data == null) return list;
      foreach (var path in data.Paths)
      {
        IList branch = null;
        try { branch = data.get_Branch(path); } catch { }
        if (branch == null) continue;
        foreach (var item in branch)
        {
          var g = ToGeometry(item);
          if (g != null) list.Add(g); else nonGeometry++;
        }
      }
      return list;
    }

    private static GeometryBase ToGeometry(object item)
    {
      if (item == null) return null;
      object v = item;
      if (item is IGH_Goo goo) { try { v = goo.ScriptVariable() ?? item; } catch { v = item; } }
      try
      {
        switch (v)
        {
          case GeometryBase gb: return gb;
          case Point3d pt: return new Point(pt);
          case Line ln: return new LineCurve(ln);
          case Arc arc: return new ArcCurve(arc);
          case Circle ci: return new ArcCurve(ci);
          case Polyline pl: return pl.ToPolylineCurve();
          case Rectangle3d rc: return rc.ToPolyline().ToPolylineCurve();
          case Box bx: return bx.ToBrep();
          case Ellipse el: return el.ToNurbsCurve();
          default: return null;
        }
      }
      catch { return null; }
    }

    private static GeometrySet Page(Resolved r, List<GeometryBase> all, int nonGeometry, out List<(int index, GeometryBase g)> page)
    {
      page = new List<(int, GeometryBase)>();
      for (var i = r.Target.Skip; i < all.Count && page.Count < r.Target.Limit; i++) page.Add((i, all[i]));
      return new GeometrySet
      {
        NodeId = r.Target.Id, Port = r.Port, Total = all.Count, NonGeometry = nonGeometry,
        Skip = r.Target.Skip, Limit = r.Target.Limit,
      };
    }

    private static double[] V(Point3d p) => new[] { p.X, p.Y, p.Z };

    private static string TypeName(GeometryBase g) => g?.GetType().Name ?? "null";

    // THE TOLERANCE IS READ OR REFUSED, NEVER DEFAULTED. It used to fall back to 0.001 when Rhino had no active
    // document (or the read threw), so intersections and nearness were measured against a tolerance nobody set,
    // and nothing in the answer said so (#208, found beside lane C's units read). Both callers now take the one
    // decision - DocumentUnitsReport.TryTolerance - and refuse in the same words get_document_units uses.

    private static string Reason(Exception ex) => ex.GetType().Name + ": " + ex.Message;

    // ---- per-object facts -----------------------------------------------------------------------------

    internal static GeoObject Facts(GeometryBase g, int index, bool massProps, Stopwatch budget, ref int skipped)
    {
      var o = new GeoObject { Index = index, Type = TypeName(g) };
      try
      {
        var bb = g.GetBoundingBox(true);
        if (bb.IsValid) { o.BboxMin = V(bb.Min); o.BboxMax = V(bb.Max); }
        o.Valid = g.IsValid;

        var inBudget = massProps && budget.ElapsedMilliseconds < MassPropsBudgetMs;
        if (massProps && !inBudget) skipped++;

        switch (g)
        {
          case Mesh m:
            o.Closed = m.IsClosed;
            o.Faces = m.Faces.Count;
            o.Vertices = m.Vertices.Count;
            if (inBudget)
            {
              o.Area = AreaMassProperties.Compute(m)?.Area;
              if (m.IsClosed) o.Volume = VolumeMassProperties.Compute(m)?.Volume;
            }
            break;
          case Brep b:
            o.Solid = b.IsSolid;
            o.Manifold = b.IsManifold;
            o.Closed = b.IsSolid;
            o.Faces = b.Faces.Count;
            o.Edges = b.Edges.Count;
            if (inBudget)
            {
              o.Area = AreaMassProperties.Compute(b)?.Area;
              if (b.IsSolid) o.Volume = VolumeMassProperties.Compute(b)?.Volume;
            }
            break;
          case Curve c:
            o.Closed = c.IsClosed;
            o.Planar = c.IsPlanar();
            o.Degree = c.Degree;
            o.Length = c.GetLength();
            if (c is PolylineCurve pc) o.Segments = Math.Max(0, pc.PointCount - 1);
            else if (c is PolyCurve pc2) o.Segments = pc2.SegmentCount;
            break;
          case Surface s:
            o.Closed = s.IsClosed(0) || s.IsClosed(1);
            if (inBudget) o.Area = AreaMassProperties.Compute(s)?.Area;
            break;
        }
      }
      catch (Exception ex) { o.Failed = Reason(ex); }
      return o;
    }

    // ---- the tools ------------------------------------------------------------------------------------

    /// <summary>get_geometry_summary.</summary>
    public static ToolResult Summary(GH_Document doc, AgentSnapshot snap, AgentIdRegistry ids, JsonObject args)
    {
      if (!GeometryArgs.TryParseTarget(args, out var t, out var err)) return err;
      if (!TryResolve(doc, snap, ids, t, out var r, out err)) return err;

      var all = Items(r.Param, out var nonGeo);
      var set = Page(r, all, nonGeo, out var page);
      var budget = Stopwatch.StartNew();
      var skipped = 0;
      foreach (var (i, g) in page) set.Objects.Add(Facts(g, i, massProps: true, budget, ref skipped));
      if (skipped > 0)
        set.Note = "area/volume were skipped for " + skipped + " object(s) after " + MassPropsBudgetMs +
                   " ms; ask get_mass_properties for a smaller page.";
      return GeometryReport.Summary(set);
    }

    /// <summary>check_mesh: IsValidWithLog plus Mesh.Check's counts per defect class.</summary>
    public static ToolResult CheckMesh(GH_Document doc, AgentSnapshot snap, AgentIdRegistry ids, JsonObject args)
    {
      if (!GeometryArgs.TryParseTarget(args, out var t, out var err)) return err;
      if (!TryResolve(doc, snap, ids, t, out var r, out err)) return err;

      var all = Items(r.Param, out var nonGeo);
      var set = Page(r, all, nonGeo, out var page);
      var none = Stopwatch.StartNew();
      var unused = 0;
      foreach (var (i, g) in page)
      {
        var o = Facts(g, i, massProps: false, none, ref unused);
        if (!(g is Mesh m))
        {
          o.Failed = "not a mesh (" + o.Type + ") - use check_brep for a brep, get_geometry_summary for anything";
          set.Objects.Add(o);
          continue;
        }
        if (m.Vertices.Count == 0 || m.Faces.Count == 0)
        {
          o.Failed = "empty mesh (" + m.Vertices.Count + " vertices, " + m.Faces.Count + " faces) - not checked";
          set.Objects.Add(o);
          continue;
        }
        try
        {
          o.Valid = m.IsValidWithLog(out var log);
          o.ValidLog = log;
        }
        catch (Exception ex) { o.Valid = null; o.ValidLog = "unmeasured (IsValidWithLog threw: " + Reason(ex) + ")"; }

        o.Check = RunCheck(m, ref o);
        set.Objects.Add(o);
      }
      return GeometryReport.CheckMesh(set);
    }

    /// <summary>Mesh.Check with EVERY flag on, the counts read back by name. Reflection on purpose (#392
    /// (a)/(b)): the class list is this Rhino's, and a silent all-false parameter set is unrepresentable.</summary>
    private static MeshCheckFacts RunCheck(Mesh m, ref GeoObject o)
    {
      var facts = new MeshCheckFacts();
      try
      {
        // MEASURED (Rhino 8.34): Defaults() already switches every CheckFor* flag on. The reflection pass
        // below is therefore a BELT - against a future Defaults() that stops doing so - and the flags it
        // enabled are reported, so a check that silently checked nothing stays unrepresentable (#392 (b)).
        var mcp = MeshCheckParameters.Defaults();
        object boxed = mcp;
        var props = typeof(MeshCheckParameters).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        foreach (var p in props)
        {
          if (p.PropertyType == typeof(bool) && p.CanWrite && p.Name.StartsWith("CheckFor", StringComparison.Ordinal))
          {
            p.SetValue(boxed, true);
            facts.Enabled.Add(p.Name);
          }
        }
        mcp = (MeshCheckParameters)boxed;

        var log = new Rhino.FileIO.TextLog();
        // MEASURED over the bridge (Rhino 8.34): Check's boolean is NOT a verdict - it returned false on a
        // clean box with every count zero, and true on an EMPTY mesh - so it is read and ignored. The
        // COUNTS are the facts; Check's own text is kept only when a count says there is something to read.
        m.Check(log, ref mcp);

        object after = mcp;
        foreach (var p in props)
          if (p.PropertyType == typeof(int) && p.CanRead && p.Name.EndsWith("Count", StringComparison.Ordinal))
            facts.Counts[p.Name] = (int)p.GetValue(after);

        var text = (log.ToString() ?? "").Trim();
        if (facts.Counts.Values.Any(v => v > 0) && text.Length > 0)
          o.ValidLog = string.IsNullOrEmpty(o.ValidLog) ? "Check: " + text : o.ValidLog + " | Check: " + text;
      }
      catch (Exception ex)
      {
        facts.Failed = "Mesh.Check threw: " + Reason(ex);
      }
      return facts;
    }

    /// <summary>check_brep: IsValidWithLog, IsManifold, IsSolid, and the naked-edge count by valence.</summary>
    public static ToolResult CheckBrep(GH_Document doc, AgentSnapshot snap, AgentIdRegistry ids, JsonObject args)
    {
      if (!GeometryArgs.TryParseTarget(args, out var t, out var err)) return err;
      if (!TryResolve(doc, snap, ids, t, out var r, out err)) return err;

      var all = Items(r.Param, out var nonGeo);
      var set = Page(r, all, nonGeo, out var page);
      var none = Stopwatch.StartNew();
      var unused = 0;
      foreach (var (i, g) in page)
      {
        var o = Facts(g, i, massProps: false, none, ref unused);
        var b = AsBrep(g);
        if (b == null)
        {
          o.Failed = "not a brep (" + o.Type + ") - use check_mesh for a mesh, get_geometry_summary for anything";
          set.Objects.Add(o);
          continue;
        }
        try
        {
          o.Valid = b.IsValidWithLog(out var log);
          o.ValidLog = log;
          o.Manifold = b.IsManifold;
          o.Solid = b.IsSolid;
          o.Faces = b.Faces.Count;
          o.Edges = b.Edges.Count;
          o.NakedEdges = b.Edges.Count(e => e.Valence == EdgeAdjacency.Naked);
        }
        catch (Exception ex) { o.Failed = Reason(ex); }
        set.Objects.Add(o);
      }
      return GeometryReport.CheckBrep(set);
    }

    private static Brep AsBrep(GeometryBase g)
    {
      try
      {
        switch (g)
        {
          case Brep b: return b;
          case Surface s: return s.ToBrep();   // an Extrusion IS a Surface (the compiler said so), and overrides ToBrep
          default: return null;
        }
      }
      catch { return null; }
    }

    /// <summary>get_naked_edges: a COUNT per object plus representative locations, from two calls whose
    /// disagreement is said (#392 (e)).</summary>
    public static ToolResult NakedEdges(GH_Document doc, AgentSnapshot snap, AgentIdRegistry ids, JsonObject args)
    {
      if (!GeometryArgs.TryParseTarget(args, out var t, out var err)) return err;
      var k = 5;
      try { k = args["k"]?.GetValue<int>() ?? 5; } catch { return ToolResult.Fail(ToolErrors.InvalidArgs, "\"k\" must be an integer."); }
      if (k < 1) return ToolResult.Fail(ToolErrors.InvalidArgs, "\"k\" must be at least 1.");
      if (!TryResolve(doc, snap, ids, t, out var r, out err)) return err;

      var all = Items(r.Param, out var nonGeo);
      var set = Page(r, all, nonGeo, out var page);
      var none = Stopwatch.StartNew();
      var unused = 0;
      foreach (var (i, g) in page)
      {
        var o = Facts(g, i, massProps: false, none, ref unused);
        try
        {
          if (g is Mesh m)
          {
            var loops = m.GetNakedEdges();   // null when there are none - the count below says which
            var segments = 0;
            var samples = new List<double[]>();
            if (loops != null)
              foreach (var pl in loops)
              {
                segments += Math.Max(0, pl.Count - 1);
                if (pl.Count >= 2 && samples.Count < GeometryReport.MaxSamples * 2)
                  samples.Add(V((pl[0] + pl[1]) / 2.0));
              }
            o.NakedEdges = segments;
            o.NakedSamples = samples;
            if (loops == null && !m.IsClosed)
              o.Failed = "GetNakedEdges returned nothing on an OPEN mesh - the count is unmeasured, not zero";
          }
          else
          {
            var b = AsBrep(g);
            if (b == null)
            {
              o.Failed = "naked edges are a mesh or brep fact; this is a " + o.Type +
                         " (see closed/open in get_geometry_summary)";
            }
            else
            {
              var naked = b.Edges.Where(e => e.Valence == EdgeAdjacency.Naked).ToList();
              o.NakedEdges = naked.Count;
              o.NakedSamples = naked.Take(GeometryReport.MaxSamples * 2)
                .Select(e => V(e.PointAt(e.Domain.Mid))).ToList();
            }
          }
        }
        catch (Exception ex) { o.Failed = Reason(ex); }
        set.Objects.Add(o);
      }
      return GeometryReport.NakedEdges(set, k);
    }

    /// <summary>get_mass_properties: length / area / volume and one centroid per object. No time budget:
    /// it was asked for, and the page cap bounds it.</summary>
    public static ToolResult MassProperties(GH_Document doc, AgentSnapshot snap, AgentIdRegistry ids, JsonObject args)
    {
      if (!GeometryArgs.TryParseTarget(args, out var t, out var err)) return err;
      if (!TryResolve(doc, snap, ids, t, out var r, out err)) return err;

      var all = Items(r.Param, out var nonGeo);
      var set = Page(r, all, nonGeo, out var page);
      var none = Stopwatch.StartNew();
      var unused = 0;
      foreach (var (i, g) in page)
      {
        var o = Facts(g, i, massProps: false, none, ref unused);
        try
        {
          switch (g)
          {
            case Mesh m:
            {
              var a = AreaMassProperties.Compute(m);
              if (a != null) { o.Area = a.Area; o.Centroid = V(a.Centroid); }
              if (m.IsClosed) { var v = VolumeMassProperties.Compute(m); if (v != null) { o.Volume = v.Volume; o.Centroid = V(v.Centroid); } }
              break;
            }
            case Brep b:
            {
              var a = AreaMassProperties.Compute(b);
              if (a != null) { o.Area = a.Area; o.Centroid = V(a.Centroid); }
              if (b.IsSolid) { var v = VolumeMassProperties.Compute(b); if (v != null) { o.Volume = v.Volume; o.Centroid = V(v.Centroid); } }
              break;
            }
            case Surface s:
            {
              var a = AreaMassProperties.Compute(s);
              if (a != null) { o.Area = a.Area; o.Centroid = V(a.Centroid); }
              break;
            }
            case Curve c:
            {
              var l = LengthMassProperties.Compute(c);
              if (l != null) { o.Length = l.Length; o.Centroid = V(l.Centroid); }
              break;
            }
            case Point p:
              o.Centroid = V(p.Location);
              break;
            default:
              o.Failed = "no mass properties for a " + o.Type;
              break;
          }
        }
        catch (Exception ex) { o.Failed = Reason(ex); }
        set.Objects.Add(o);
      }
      return GeometryReport.MassProperties(set);
    }

    /// <summary>find_intersecting: bounding-box pre-filter over every candidate pair, then the true test
    /// on at most <see cref="MaxPairs"/> survivors. A pair the test could not answer carries its reason
    /// and no verdict (#392 (f)).</summary>
    public static ToolResult Intersections(GH_Document doc, AgentSnapshot snap, AgentIdRegistry ids, JsonObject args)
    {
      if (!GeometryArgs.TryParseTarget(args, out var t, out var err)) return err;
      if (!TryResolve(doc, snap, ids, t, out var r, out err)) return err;

      GeometryTarget other = null;
      if (args["against"] is JsonObject against)
      {
        if (!GeometryArgs.TryParseTarget(against, out other, out err))
          return ToolResult.Fail(ToolErrors.InvalidArgs, "\"against\" " + err.Message);
      }
      Resolved rb = null;
      if (other != null && !TryResolve(doc, snap, ids, other, out rb, out err)) return err;

      var a = Items(r.Param, out _);
      var b = rb == null ? a : Items(rb.Param, out _);
      var self = rb == null;
      if (!GhSessionFacts.TryTolerance(out var tol, out var toleranceRefusal)) return toleranceRefusal;

      var boxesA = a.Select(BoxOf).ToList();
      var boxesB = self ? boxesA : b.Select(BoxOf).ToList();
      var candidates = self ? a.Count * (a.Count - 1) / 2 : a.Count * b.Count;
      var survivors = BoxOverlap.Prefilter(boxesA, boxesB, tol, MaxPairs, out var capped, selfPairs: self);

      var pairs = new List<PairFact>();
      foreach (var (i, j) in survivors) pairs.Add(Test(a[i], b[j], i, j, tol));

      return GeometryReport.Intersections(candidates, survivors.Count, pairs, capped);
    }

    private static (double[] min, double[] max) BoxOf(GeometryBase g)
    {
      try
      {
        var bb = g.GetBoundingBox(true);
        if (bb.IsValid) return (V(bb.Min), V(bb.Max));
      }
      catch { }
      // An invalid box overlaps nothing, so an unreadable object is never reported as intersecting.
      return (new[] { double.MaxValue, double.MaxValue, double.MaxValue }, new[] { double.MinValue, double.MinValue, double.MinValue });
    }

    private static PairFact Test(GeometryBase ga, GeometryBase gb, int i, int j, double tol)
    {
      var f = new PairFact { A = i, B = j };
      try
      {
        var ba = AsBrep(ga); var bb = AsBrep(gb);
        if (ga is Mesh ma && gb is Mesh mb)
        {
          f.Kind = "mesh-mesh";
          var lines = Intersection.MeshMeshFast(ma, mb);
          f.Tested = true;
          f.Intersects = lines != null && lines.Length > 0;
        }
        else if (ba != null && bb != null)
        {
          f.Kind = "brep-brep";
          var ok = Intersection.BrepBrep(ba, bb, tol, out var curves, out var points);
          if (!ok) f.Failed = "BrepBrep returned false";
          else { f.Tested = true; f.Intersects = (curves?.Length ?? 0) > 0 || (points?.Length ?? 0) > 0; }
        }
        else if (ga is Curve ca && gb is Curve cb)
        {
          f.Kind = "curve-curve";
          var ci = Intersection.CurveCurve(ca, cb, tol, tol);
          if (ci == null) f.Failed = "CurveCurve returned null";
          else { f.Tested = true; f.Intersects = ci.Count > 0; }
        }
        else if ((ga is Curve c1 && bb != null) || (gb is Curve && ba != null))
        {
          f.Kind = "curve-brep";
          var curve = ga as Curve ?? (Curve)gb;
          var brep = ga is Curve ? bb : ba;
          var ok = Intersection.CurveBrep(curve, brep, tol, out var curves, out var points);
          if (!ok) f.Failed = "CurveBrep returned false";
          else { f.Tested = true; f.Intersects = (curves?.Length ?? 0) > 0 || (points?.Length ?? 0) > 0; }
        }
        else
        {
          f.Kind = TypeName(ga).ToLowerInvariant() + "-" + TypeName(gb).ToLowerInvariant();
          f.Failed = "pairs of this kind are not tested in this version; their bounding boxes overlap";
        }
      }
      catch (Exception ex)
      {
        f.Tested = false;
        f.Failed = "the test threw: " + Reason(ex);
      }
      return f;
    }

    /// <summary>spatial_query: RTree over the output's bounding boxes; near a point (a growing sphere
    /// search), within a box, or overlapping another object's box. Ranked, capped, the omission stated.</summary>
    public static ToolResult Spatial(GH_Document doc, AgentSnapshot snap, AgentIdRegistry ids, JsonObject args)
    {
      if (!GeometryArgs.TryParseSpatial(args, out var q, out var err)) return err;
      if (!TryResolve(doc, snap, ids, q.Target, out var r, out err)) return err;

      var all = Items(r.Param, out _);
      var boxes = new List<BoundingBox>(all.Count);
      var tree = new RTree();
      var union = BoundingBox.Empty;
      for (var i = 0; i < all.Count; i++)
      {
        BoundingBox bb;
        try { bb = all[i].GetBoundingBox(true); } catch { bb = BoundingBox.Empty; }
        boxes.Add(bb);
        if (!bb.IsValid) continue;
        union.Union(bb);
        tree.Insert(bb, i);
      }

      if (!GhSessionFacts.TryTolerance(out var tol, out var toleranceRefusal)) return toleranceRefusal;
      var hits = new List<(int Index, double Distance)>();

      try
      {
        switch (q.Kind)
        {
          case "near":
          {
            var pt = new Point3d(q.Point[0], q.Point[1], q.Point[2]);
            var extent = union.IsValid ? Math.Max(union.Diagonal.Length, tol) : tol;
            var radius = Math.Max(extent / 64.0, tol);
            var found = new HashSet<int>();
            while (true)
            {
              found.Clear();
              tree.Search(new Sphere(pt, radius), (s, e) => found.Add(e.Id));
              if (found.Count >= q.Target.Limit || radius >= extent * 2.0) break;
              radius *= 2.0;
            }
            foreach (var i in found) hits.Add((i, boxes[i].ClosestPoint(pt).DistanceTo(pt)));
            break;
          }
          case "within":
          {
            var box = new BoundingBox(new Point3d(q.BoxMin[0], q.BoxMin[1], q.BoxMin[2]),
                                      new Point3d(q.BoxMax[0], q.BoxMax[1], q.BoxMax[2]));
            var found = new HashSet<int>();
            tree.Search(box, (s, e) => found.Add(e.Id));
            foreach (var i in found) hits.Add((i, boxes[i].Center.DistanceTo(box.Center)));
            break;
          }
          case "overlapping":
          {
            var ot = new GeometryTarget { Id = q.OtherId, Param = q.OtherParam };
            if (!TryResolve(doc, snap, ids, ot, out var ro, out err)) return err;
            var others = Items(ro.Param, out _);
            if (q.OtherIndex >= others.Count)
              return ToolResult.Fail(ToolErrors.InvalidArgs,
                "\"overlapping.index\" " + q.OtherIndex + " is past the end - that output holds " + others.Count + " geometry object(s).");
            var ob = others[q.OtherIndex].GetBoundingBox(true);
            ob.Inflate(tol);
            var found = new HashSet<int>();
            tree.Search(ob, (s, e) => found.Add(e.Id));
            foreach (var i in found) hits.Add((i, boxes[i].Center.DistanceTo(ob.Center)));
            break;
          }
        }
      }
      catch (Exception ex)
      {
        return ToolResult.Fail(ToolErrors.PreconditionRefused, "The spatial query could not run (" + Reason(ex) + ").");
      }

      var ranked = RankCap.Nearest(hits, int.MaxValue, out _);
      return GeometryReport.Spatial(q.Kind, ranked, q.Target.Limit, all.Count);
    }
  }
}
