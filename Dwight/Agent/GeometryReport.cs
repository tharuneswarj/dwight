using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace Dwight.Agent
{
  /// <summary>
  /// Phase-9 U2: the geometry tools' EMITTER - facts in, a bounded payload out. GH-free (ADR-091).
  ///
  /// THREE RULES, each from the record:
  ///   - BOUNDED BY CONSTRUCTION. The only numeric arrays ever emitted are 3-vectors, and the only lists
  ///     of them are samples capped at <see cref="MaxSamples"/>. Asserted structurally by the tests.
  ///   - EVERY OMISSION IS STATED (ADR-035 at the tool surface, #134/#209): a page says how many more
  ///     there are and how to get them; a cut sample says how many locations it dropped; a cut log says
  ///     how long it was.
  ///   - ABSENT MEANS ABSENT. A fact that was not read for this type is omitted, never emitted as false
  ///     or zero - a clean zero that measures nothing is #212's class, and it is closed by shape here.
  /// </summary>
  internal static class GeometryReport
  {
    /// <summary>Representative locations per object, at most - whether the mesh has twelve naked edges
    /// or twelve thousand. A fact does not scale with the data (the Q6 ruling).</summary>
    public const int MaxSamples = 10;

    /// <summary>Chars of an IsValidWithLog text returned before it is cut and marked.</summary>
    public const int MaxLogChars = 300;

    // ---- the envelope ---------------------------------------------------------------------------------

    private static JsonObject Envelope(GeometrySet s)
    {
      var shown = s.Objects?.Count ?? 0;
      var o = new JsonObject
      {
        ["id"] = s.NodeId,
        ["port"] = s.Port,
        ["total"] = s.Total,
        ["nonGeometry"] = s.NonGeometry,
        ["shown"] = shown,
        ["skip"] = s.Skip,
      };
      if (s.Total == 0) o["empty"] = true;
      if (!string.IsNullOrEmpty(s.Note)) o["note"] = s.Note;
      var rest = s.Total - (s.Skip + shown);
      if (rest > 0)
        o["omitted"] = rest + " more object" + (rest == 1 ? "" : "s") + " not shown; call again with skip=" +
                       (s.Skip + shown) + ".";
      return o;
    }

    private static JsonObject Base(GeoObject g)
    {
      var o = new JsonObject { ["i"] = g.Index, ["type"] = g.Type };
      if (g.Failed != null) o["readFailed"] = g.Failed;
      return o;
    }

    private static JsonArray Vec(double[] v) =>
      new JsonArray((v ?? Array.Empty<double>()).Take(3).Select(d => (JsonNode)d).ToArray());

    private static void Put(JsonObject o, string key, bool? v) { if (v.HasValue) o[key] = v.Value; }
    private static void Put(JsonObject o, string key, int? v) { if (v.HasValue) o[key] = v.Value; }
    private static void Put(JsonObject o, string key, double? v) { if (v.HasValue) o[key] = v.Value; }

    private static void PutLog(JsonObject o, GeoObject g)
    {
      Put(o, "valid", g.Valid);
      if (string.IsNullOrEmpty(g.ValidLog)) return;
      o["log"] = g.ValidLog.Length <= MaxLogChars
        ? g.ValidLog
        : g.ValidLog.Substring(0, MaxLogChars) + " [truncated: " + g.ValidLog.Length + " chars]";
    }

    private static JsonArray Objects(GeometrySet s, Func<GeoObject, JsonObject> one)
    {
      var arr = new JsonArray();
      foreach (var g in s.Objects ?? new List<GeoObject>()) arr.Add(one(g));
      return arr;
    }

    // ---- the tools ------------------------------------------------------------------------------------

    /// <summary>get_geometry_summary: type, bounding box, validity, closed/solid, counts, area, volume.</summary>
    public static ToolResult Summary(GeometrySet s)
    {
      var r = Envelope(s);
      r["objects"] = Objects(s, g =>
      {
        var o = Base(g);
        if (g.BboxMin != null && g.BboxMax != null)
          o["bbox"] = new JsonObject { ["min"] = Vec(g.BboxMin), ["max"] = Vec(g.BboxMax) };
        Put(o, "valid", g.Valid);
        Put(o, "closed", g.Closed);
        Put(o, "solid", g.Solid);
        Put(o, "planar", g.Planar);
        Put(o, "faces", g.Faces);
        Put(o, "vertices", g.Vertices);
        Put(o, "edges", g.Edges);
        Put(o, "segments", g.Segments);
        Put(o, "degree", g.Degree);
        Put(o, "length", g.Length);
        Put(o, "area", g.Area);
        Put(o, "volume", g.Volume);
        return o;
      });
      return ToolResult.Success(r);
    }

    /// <summary>get_naked_edges: a COUNT per object plus at most <paramref name="k"/> (and never more
    /// than <see cref="MaxSamples"/>) representative locations. Never the polylines.</summary>
    public static ToolResult NakedEdges(GeometrySet s, int k)
    {
      var r = Envelope(s);
      var take = Math.Max(1, Math.Min(k, MaxSamples));
      if (k > MaxSamples)
        r["sampleCap"] = "At most " + MaxSamples + " locations per object are ever returned; " + k + " was asked.";

      r["objects"] = Objects(s, g =>
      {
        var o = Base(g);
        Put(o, "nakedEdges", g.NakedEdges);
        var samples = g.NakedSamples ?? new List<double[]>();
        if (samples.Count > 0)
        {
          o["sample"] = new JsonArray(samples.Take(take).Select(v => (JsonNode)Vec(v)).ToArray());
          if (samples.Count > take)
            o["sampleOmitted"] = (samples.Count - take) + " more location" + (samples.Count - take == 1 ? "" : "s") + " not shown";
        }
        return o;
      });
      return ToolResult.Success(r);
    }

    /// <summary>check_mesh: validity with its log, and Mesh.Check's counts PER DEFECT CLASS.</summary>
    public static ToolResult CheckMesh(GeometrySet s)
    {
      var r = Envelope(s);
      r["objects"] = Objects(s, g =>
      {
        var o = Base(g);
        PutLog(o, g);
        Put(o, "closed", g.Closed);
        Put(o, "faces", g.Faces);
        Put(o, "vertices", g.Vertices);
        if (g.Check != null)
        {
          if (g.Check.Failed != null)
          {
            o["checkFailed"] = g.Check.Failed;   // no counts: a check that did not run reports nothing as a number
          }
          else
          {
            var counts = new JsonObject();
            foreach (var kv in g.Check.Counts.OrderBy(kv => kv.Key, StringComparer.Ordinal)) counts[kv.Key] = kv.Value;
            o["counts"] = counts;
            o["checked"] = new JsonArray(g.Check.Enabled.Select(n => (JsonNode)n).ToArray());
          }
        }
        return o;
      });
      // Rhino's own ceiling, inherited and stated rather than overclaimed (study §6, finding 1).
      r["caveat"] = "Rhino's own Check documents that self-intersection classes are not reliably detected; " +
                    "a zero in a self-intersection count is not proof of none.";
      return ToolResult.Success(r);
    }

    /// <summary>check_brep: IsValidWithLog, IsManifold, IsSolid, and the naked-edge count.</summary>
    public static ToolResult CheckBrep(GeometrySet s)
    {
      var r = Envelope(s);
      r["objects"] = Objects(s, g =>
      {
        var o = Base(g);
        PutLog(o, g);
        Put(o, "manifold", g.Manifold);
        Put(o, "solid", g.Solid);
        Put(o, "faces", g.Faces);
        Put(o, "edges", g.Edges);
        Put(o, "nakedEdges", g.NakedEdges);
        return o;
      });
      return ToolResult.Success(r);
    }

    /// <summary>get_mass_properties: length / area / volume and ONE centroid per object.</summary>
    public static ToolResult MassProperties(GeometrySet s)
    {
      var r = Envelope(s);
      r["objects"] = Objects(s, g =>
      {
        var o = Base(g);
        Put(o, "length", g.Length);
        Put(o, "area", g.Area);
        Put(o, "volume", g.Volume);
        if (g.Centroid != null) o["centroid"] = Vec(g.Centroid);
        return o;
      });
      return ToolResult.Success(r);
    }

    /// <summary>find_intersecting: the FUNNEL (candidates, after the bounding-box filter, tested) and a
    /// verdict per pair that was ACTUALLY TESTED. An untested pair carries its reason and no verdict.</summary>
    public static ToolResult Intersections(int candidates, int afterPrefilter, List<PairFact> pairs, bool capped)
    {
      pairs = pairs ?? new List<PairFact>();
      var arr = new JsonArray();
      foreach (var p in pairs)
      {
        var o = new JsonObject { ["a"] = p.A, ["b"] = p.B };
        if (p.Kind != null) o["kind"] = p.Kind;
        if (p.Tested) o["intersects"] = p.Intersects;
        else o["testFailed"] = p.Failed ?? "the intersection test did not run";
        arr.Add(o);
      }
      var r = new JsonObject
      {
        ["candidatePairs"] = candidates,
        ["afterBoundingBoxFilter"] = afterPrefilter,
        ["tested"] = pairs.Count(p => p.Tested),
        ["intersecting"] = pairs.Count(p => p.Tested && p.Intersects),
        ["pairs"] = arr,
        ["capped"] = capped,
      };
      if (capped)
        r["omitted"] = "The pair list was capped before every candidate was tested; narrow the selection " +
                       "or the target to test the rest.";
      return ToolResult.Success(r);
    }

    /// <summary>spatial_query: indices and distances, ranked, capped, the omission stated.</summary>
    public static ToolResult Spatial(string query, List<(int Index, double Distance)> hits, int limit, int total)
    {
      hits = hits ?? new List<(int, double)>();
      var shown = hits.Take(Math.Max(1, limit)).ToList();
      var arr = new JsonArray();
      foreach (var h in shown) arr.Add(new JsonObject { ["i"] = h.Index, ["distance"] = h.Distance });
      var r = new JsonObject
      {
        ["query"] = query,
        ["searched"] = total,
        ["shown"] = shown.Count,
        ["hits"] = arr,
      };
      var rest = hits.Count - shown.Count;
      if (rest > 0) r["omitted"] = rest + " more hit" + (rest == 1 ? "" : "s") + " not shown; raise \"limit\" or narrow the query.";
      return ToolResult.Success(r);
    }
  }
}
