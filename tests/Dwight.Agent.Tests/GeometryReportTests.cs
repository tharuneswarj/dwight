using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Dwight.Agent;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// Phase-9 U2: the geometry reports are BOUNDED BY CONSTRUCTION. Every tool returns scalars, short
  /// strings or small bounded lists - no vertex arrays, no control points, no serialised geometry, ever.
  ///
  /// The bound is asserted STRUCTURALLY here, not reviewed: the emitted JSON is walked, and the only
  /// numeric arrays permitted are 3-vectors (a point, a box corner) and the only arrays of those are the
  /// sample lists, capped at <see cref="GeometryReport.MaxSamples"/>. A future field that dumped
  /// coordinates would fail this walk on its first test run.
  /// </summary>
  public class GeometryReportTests
  {
    private static GeoObject Box(int i) => new GeoObject
    {
      Index = i, Type = "Mesh", BboxMin = new[] { 0.0, 0.0, 0.0 }, BboxMax = new[] { 1.0, 1.0, 1.0 },
      Valid = true, Closed = true, Faces = 6, Vertices = 8, Area = 6.0, Volume = 1.0,
    };

    private static GeometrySet Set(int total, int skip, int limit, int nonGeometry = 0)
    {
      var s = new GeometrySet { NodeId = 7, Port = "Mesh", Total = total, NonGeometry = nonGeometry, Skip = skip, Limit = limit };
      for (var i = skip; i < Math.Min(total, skip + limit); i++) s.Objects.Add(Box(i));
      return s;
    }

    /// <summary>THE STRUCTURAL BOUND. Walks any emitted result and refuses a coordinate list.</summary>
    private static void AssertBounded(JsonNode node)
    {
      switch (node)
      {
        case JsonObject o:
          foreach (var kv in o) AssertBounded(kv.Value);
          break;
        case JsonArray a:
          if (a.Count > 0 && a.All(x => x is JsonValue v && v.TryGetValue<double>(out _)))
            Assert.True(a.Count <= 3, "a numeric array of " + a.Count + " entries is a coordinate dump");
          else if (a.Count > 0 && a.All(x => x is JsonArray))
            Assert.True(a.Count <= GeometryReport.MaxSamples, "an array of " + a.Count + " vectors exceeds the sample cap");
          foreach (var x in a) AssertBounded(x);
          break;
      }
    }

    [Fact]
    public void THE_SUMMARY_CARRIES_THE_FACTS_AND_OMITS_WHAT_IT_DOES_NOT_HAVE()
    {
      var r = GeometryReport.Summary(Set(total: 1, skip: 0, limit: 25));
      Assert.True(r.Ok);
      var o = (JsonObject)r.Result["objects"][0];

      Assert.Equal(0, o["i"].GetValue<int>());
      Assert.Equal("Mesh", o["type"].GetValue<string>());
      Assert.Equal(1.0, o["bbox"]["max"][0].GetValue<double>());
      Assert.True(o["closed"].GetValue<bool>());
      Assert.Equal(6, o["faces"].GetValue<int>());
      Assert.Equal(1.0, o["volume"].GetValue<double>());
      Assert.False(o.ContainsKey("solid"));     // never read for a mesh: absent, not false
      Assert.False(o.ContainsKey("degree"));
      AssertBounded(r.Result);
    }

    [Fact]
    public void A_PAGE_STATES_WHAT_IT_OMITTED_AND_HOW_TO_GET_THE_REST()
    {
      var r = GeometryReport.Summary(Set(total: 60, skip: 0, limit: 25, nonGeometry: 3));
      Assert.Equal(60, r.Result["total"].GetValue<int>());
      Assert.Equal(25, r.Result["shown"].GetValue<int>());
      Assert.Equal(3, r.Result["nonGeometry"].GetValue<int>());
      Assert.Contains("35 more", r.Result["omitted"].GetValue<string>());
      Assert.Contains("skip", r.Result["omitted"].GetValue<string>());

      var last = GeometryReport.Summary(Set(total: 60, skip: 50, limit: 25));
      Assert.Equal(10, last.Result["shown"].GetValue<int>());
      Assert.Null(last.Result["omitted"]);
    }

    [Fact]
    public void AN_EMPTY_OUTPUT_IS_SAID_TO_BE_EMPTY_NEVER_A_VACUOUS_REPORT()
    {
      var r = GeometryReport.Summary(Set(total: 0, skip: 0, limit: 25));
      Assert.True(r.Ok);
      Assert.Equal(0, r.Result["total"].GetValue<int>());
      Assert.True(r.Result["empty"].GetValue<bool>());
    }

    [Fact]
    public void NAKED_EDGES_ARE_A_COUNT_PLUS_A_CAPPED_SAMPLE_NEVER_THE_POLYLINES()
    {
      var s = Set(total: 1, skip: 0, limit: 25);
      s.Objects[0].NakedEdges = 40;
      s.Objects[0].NakedSamples = Enumerable.Range(0, 40).Select(k => new[] { (double)k, 0.0, 0.0 }).ToList();

      var r = GeometryReport.NakedEdges(s, k: 5);
      var o = (JsonObject)r.Result["objects"][0];
      Assert.Equal(40, o["nakedEdges"].GetValue<int>());
      Assert.Equal(5, ((JsonArray)o["sample"]).Count);
      Assert.Contains("35", o["sampleOmitted"].GetValue<string>());
      AssertBounded(r.Result);
    }

    [Fact]
    public void A_SAMPLE_REQUEST_OVER_THE_SAMPLE_CAP_IS_HELD_TO_THE_CAP_AND_SAYS_SO()
    {
      var s = Set(total: 1, skip: 0, limit: 25);
      s.Objects[0].NakedEdges = 40;
      s.Objects[0].NakedSamples = Enumerable.Range(0, 40).Select(k => new[] { (double)k, 0.0, 0.0 }).ToList();

      var r = GeometryReport.NakedEdges(s, k: 500);
      var o = (JsonObject)r.Result["objects"][0];
      Assert.Equal(GeometryReport.MaxSamples, ((JsonArray)o["sample"]).Count);
      Assert.Contains(GeometryReport.MaxSamples.ToString(), r.Result["sampleCap"].GetValue<string>());
      AssertBounded(r.Result);
    }

    [Fact]
    public void CHECK_MESH_RETURNS_THE_COUNTS_PER_CLASS_A_TRUNCATED_LOG_AND_RHINOS_OWN_CAVEAT()
    {
      var s = Set(total: 1, skip: 0, limit: 25);
      s.Objects[0].Valid = false;
      s.Objects[0].ValidLog = new string('x', 2000);
      s.Objects[0].Check = new MeshCheckFacts
      {
        Counts = new Dictionary<string, int> { ["NakedEdgeCount"] = 4, ["NonManifoldEdgeCount"] = 0 },
        Enabled = new List<string> { "CheckForNakedEdges", "CheckForNonManifoldEdges" },
      };

      var r = GeometryReport.CheckMesh(s);
      var o = (JsonObject)r.Result["objects"][0];
      Assert.False(o["valid"].GetValue<bool>());
      Assert.True(o["log"].GetValue<string>().Length <= GeometryReport.MaxLogChars + 32);
      Assert.Contains("truncated", o["log"].GetValue<string>());
      Assert.Equal(4, o["counts"]["NakedEdgeCount"].GetValue<int>());
      Assert.Equal(2, ((JsonArray)o["checked"]).Count);
      Assert.Contains("self-intersection", r.Result["caveat"].GetValue<string>(), StringComparison.OrdinalIgnoreCase);
      AssertBounded(r.Result);
    }

    [Fact]
    public void A_CHECK_THAT_COULD_NOT_RUN_SAYS_SO_WITH_ITS_REASON_NEVER_A_CLEAN_ZERO()
    {
      // #392's re-homing rule: the unmeasured case fails SAFE and the message carries the discriminator.
      var s = Set(total: 1, skip: 0, limit: 25);
      s.Objects[0].Check = new MeshCheckFacts { Failed = "Mesh.Check threw: object reference not set" };

      var r = GeometryReport.CheckMesh(s);
      var o = (JsonObject)r.Result["objects"][0];
      Assert.False(o.ContainsKey("counts"));
      Assert.Contains("Mesh.Check threw", o["checkFailed"].GetValue<string>());
    }

    [Fact]
    public void CHECK_BREP_REPORTS_VALIDITY_MANIFOLD_SOLID_AND_NAKED_EDGE_COUNT()
    {
      var s = Set(total: 1, skip: 0, limit: 25);
      var b = s.Objects[0];
      b.Type = "Brep"; b.Valid = true; b.Manifold = true; b.Solid = false; b.Faces = 5; b.Edges = 12; b.NakedEdges = 4;

      var r = GeometryReport.CheckBrep(s);
      var o = (JsonObject)r.Result["objects"][0];
      Assert.True(o["manifold"].GetValue<bool>());
      Assert.False(o["solid"].GetValue<bool>());
      Assert.Equal(4, o["nakedEdges"].GetValue<int>());
      AssertBounded(r.Result);
    }

    [Fact]
    public void MASS_PROPERTIES_ARE_SCALARS_AND_ONE_CENTROID()
    {
      var s = Set(total: 1, skip: 0, limit: 25);
      s.Objects[0].Centroid = new[] { 0.5, 0.5, 0.5 };

      var r = GeometryReport.MassProperties(s);
      var o = (JsonObject)r.Result["objects"][0];
      Assert.Equal(6.0, o["area"].GetValue<double>());
      Assert.Equal(1.0, o["volume"].GetValue<double>());
      Assert.Equal(0.5, o["centroid"][2].GetValue<double>());
      AssertBounded(r.Result);
    }

    [Fact]
    public void INTERSECTIONS_REPORT_THE_FUNNEL_AND_EVERY_PAIR_SAYS_WHETHER_IT_WAS_ACTUALLY_TESTED()
    {
      var pairs = new List<PairFact>
      {
        new PairFact { A = 0, B = 1, Tested = true, Intersects = true, Kind = "mesh-mesh" },
        new PairFact { A = 0, B = 2, Tested = true, Intersects = false, Kind = "mesh-mesh" },
        new PairFact { A = 1, B = 2, Tested = false, Failed = "BrepBrep returned false" },
      };
      var r = GeometryReport.Intersections(candidates: 10, afterPrefilter: 3, pairs: pairs, capped: false);

      Assert.Equal(10, r.Result["candidatePairs"].GetValue<int>());
      Assert.Equal(3, r.Result["afterBoundingBoxFilter"].GetValue<int>());
      Assert.Equal(2, r.Result["tested"].GetValue<int>());
      var arr = (JsonArray)r.Result["pairs"];
      Assert.True(arr[0]["intersects"].GetValue<bool>());
      Assert.False(arr[1]["intersects"].GetValue<bool>());
      Assert.False(arr[2].AsObject().ContainsKey("intersects"));   // untested: no verdict, only the reason
      Assert.Contains("BrepBrep returned false", arr[2]["testFailed"].GetValue<string>());
      Assert.Equal(1, r.Result["intersecting"].GetValue<int>());
      AssertBounded(r.Result);
    }

    [Fact]
    public void A_CAPPED_PAIR_LIST_SAYS_IT_WAS_CAPPED()
    {
      var r = GeometryReport.Intersections(candidates: 5000, afterPrefilter: 900, pairs: new List<PairFact>(), capped: true);
      Assert.True(r.Result["capped"].GetValue<bool>());
      Assert.Contains("narrow", r.Result["omitted"].GetValue<string>(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_SPATIAL_RESULT_IS_INDICES_AND_DISTANCES_CAPPED_WITH_THE_OMISSION_STATED()
    {
      var hits = Enumerable.Range(0, 40).Select(i => (Index: i, Distance: (double)i)).ToList();
      var r = GeometryReport.Spatial("near", hits, limit: 5, total: 500);
      var arr = (JsonArray)r.Result["hits"];
      Assert.Equal(5, arr.Count);
      Assert.Equal(0, arr[0]["i"].GetValue<int>());
      Assert.Equal(4.0, arr[4]["distance"].GetValue<double>());
      Assert.Equal(500, r.Result["searched"].GetValue<int>());
      Assert.Contains("35 more", r.Result["omitted"].GetValue<string>());
      AssertBounded(r.Result);
    }
  }
}
