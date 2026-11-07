using System.Collections.Generic;

namespace Dwight.Agent
{
  // ---- Phase-9 U2: the GH-free FACTS the geometry read set reports (#381 R3, study §6) ----------------
  //
  // THE BOUND IS IN THE TYPES. Every field here is a scalar, a short string, a 3-vector, or a list of
  // 3-vectors that the emitter caps at GeometryReport.MaxSamples. There is no field a vertex array, a
  // control-point list or a serialised geometry could travel in - the same discipline OutputDiff keeps
  // for the refine loop's feedback (#96): what cannot be represented cannot leak. GeometryReportTests
  // walks the emitted JSON and refuses any numeric array longer than 3.
  //
  // CONTENTS SCALE WITH THE DATA; A FACT DOES NOT (the Q6 ruling, ADR-058 extended as ADR-093 extended
  // it). Ten naked-edge locations whether the mesh has twelve or twelve thousand.

  /// <summary>One geometry object on a named output, as the GH reader saw it. Null means NOT READ for
  /// this type (a mesh has no degree; a curve has no volume) and is omitted from the payload.</summary>
  internal sealed class GeoObject
  {
    public int Index { get; set; }
    public string Type { get; set; }
    public double[] BboxMin { get; set; }
    public double[] BboxMax { get; set; }
    public bool? Valid { get; set; }
    /// <summary>IsValidWithLog's text, verbatim from RhinoCommon; the emitter truncates and says so.</summary>
    public string ValidLog { get; set; }
    public bool? Closed { get; set; }
    public bool? Solid { get; set; }
    public bool? Manifold { get; set; }
    public bool? Planar { get; set; }
    public int? Faces { get; set; }
    public int? Vertices { get; set; }
    public int? Edges { get; set; }
    public int? Segments { get; set; }
    public int? Degree { get; set; }
    public double? Length { get; set; }
    public double? Area { get; set; }
    public double? Volume { get; set; }
    public double[] Centroid { get; set; }
    public MeshCheckFacts Check { get; set; }
    public int? NakedEdges { get; set; }
    /// <summary>Representative naked-edge locations (midpoints). The READER may collect more than the
    /// cap; the EMITTER never returns more than <c>GeometryReport.MaxSamples</c> and says how many it cut.</summary>
    public List<double[]> NakedSamples { get; set; }
    /// <summary>A read that threw, with the reason verbatim - the unmeasured case fails SAFE and its
    /// message carries the discriminator (#392).</summary>
    public string Failed { get; set; }
  }

  /// <summary>What Mesh.Check reported, by DEFECT CLASS. The class names are whatever the loaded
  /// RhinoCommon's MeshCheckParameters exposes, read by reflection (ADR-092's pattern), so no list here
  /// can be stale and the payload is its own discriminator (#392 (a)). <see cref="Enabled"/> names the
  /// flags that were switched on before the check ran - every bool flag, by reflection, so a check that
  /// silently checks nothing is unrepresentable (#392 (b), #212 solved structurally).</summary>
  internal sealed class MeshCheckFacts
  {
    public Dictionary<string, int> Counts { get; set; } = new Dictionary<string, int>();
    public List<string> Enabled { get; set; } = new List<string>();
    public string Failed { get; set; }
  }

  /// <summary>One page of one output's geometry, plus the counts that let the page state what it omitted.</summary>
  internal sealed class GeometrySet
  {
    public int NodeId { get; set; }
    public string Port { get; set; }
    /// <summary>Geometry objects on the output, in total (before paging).</summary>
    public int Total { get; set; }
    /// <summary>Items on the output that were not geometry at all (numbers, text, nulls).</summary>
    public int NonGeometry { get; set; }
    public int Skip { get; set; }
    public int Limit { get; set; }
    public List<GeoObject> Objects { get; set; } = new List<GeoObject>();
    /// <summary>A bound the reader applied across the page (a time budget spent), stated for the model.</summary>
    public string Note { get; set; }
  }

  /// <summary>One candidate pair from find_intersecting. <see cref="Tested"/> false means the true test
  /// did not run or did not answer - and then there is NO verdict, only <see cref="Failed"/> (#392 (f)).</summary>
  internal sealed class PairFact
  {
    public int A { get; set; }
    public int B { get; set; }
    public bool Tested { get; set; }
    public bool Intersects { get; set; }
    public string Kind { get; set; }
    public string Failed { get; set; }
  }
}
