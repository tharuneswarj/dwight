using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.Msagl.Core.Geometry;
using Microsoft.Msagl.Core.Geometry.Curves;
using Microsoft.Msagl.Core.Layout;
using Microsoft.Msagl.Layout.Layered;
using Microsoft.Msagl.Miscellaneous;
using Rhino;

namespace Dwight.Layout
{
  /// <summary>
  /// Phase 6, WP-0 - the MSAGL integration spike (the phase doc's "spike first, report before
  /// committing"). It is throwaway: it lays out a small known DAG with measured node sizes, reads the
  /// coordinates back, and lays the SAME graph out twice to confirm determinism. The point is to prove
  /// three things before WP-A is built on MSAGL:
  ///   1. MSAGL references and runs inside the Rhino 8 / net7 plugin host (Tharun runs this in Rhino),
  ///   2. the Sugiyama layered layout produces sane left-to-right coordinates we can map back to pivots,
  ///   3. identical input yields byte-identical output (the determinism the whole engine depends on,
  ///      ADR-063).
  /// If this loads and the two passes match, MSAGL is the engine; if it conflicts in the Rhino host, the
  /// fallback is porting the Sugiyama pipeline (the phase doc's documented Plan B). Reachable from the
  /// Dwight top-bar menu under Dev; it writes its report to the Rhino command line and touches nothing.
  /// </summary>
  internal static class MsaglSpike
  {
    public static void Run()
    {
      try
      {
        var report = BuildReport();
        RhinoApp.WriteLine(report);
      }
      catch (Exception ex)
      {
        // A failure here IS the spike's answer (MSAGL did not load / the API drifted), so surface it
        // plainly rather than swallowing it.
        RhinoApp.WriteLine("Dwight MSAGL spike FAILED: " + ex.GetType().Name + " - " + ex.Message);
        if (ex.InnerException != null)
          RhinoApp.WriteLine("  inner: " + ex.InnerException.GetType().Name + " - " + ex.InnerException.Message);
      }
    }

    /// <summary>Lay the known graph out twice and produce a human-readable report (coordinates +
    /// determinism verdict). Pure - no canvas access - so it is safe to call from anywhere.</summary>
    public static string BuildReport()
    {
      var first = LayOutKnownGraph();
      var second = LayOutKnownGraph();

      var deterministic = CoordsEqual(first, second);

      var sb = new StringBuilder();
      sb.AppendLine("Dwight MSAGL spike (Phase 6 WP-0)");
      sb.AppendLine("  MSAGL version: " + MsaglVersion());
      sb.AppendLine("  Known DAG: A -> B, A -> C, B -> D, C -> D (a diamond), laid out left-to-right.");
      sb.AppendLine("  Node centres after layout (x, y):");
      foreach (var kv in first.OrderBy(k => k.Key, StringComparer.Ordinal))
        sb.AppendLine("    " + kv.Key + ": " + Fmt(kv.Value));
      sb.AppendLine("  Determinism (two passes, same input): " + (deterministic ? "PASS - byte-identical" : "FAIL - coordinates differ"));
      sb.AppendLine(deterministic
        ? "  => MSAGL loaded and laid out deterministically. If you see this in Rhino, MSAGL is viable for WP-A."
        : "  => MSAGL laid out but was NOT deterministic with default settings - WP-A must pin seeds/ordering.");
      return sb.ToString();
    }

    /// <summary>Build the known diamond DAG with measured rectangle sizes and run the Sugiyama layered
    /// layout left-to-right, returning each node's resolved centre keyed by its id.</summary>
    private static Dictionary<string, Point> LayOutKnownGraph()
    {
      var graph = new GeometryGraph();

      // Deterministic node creation order (A, B, C, D); sizes vary like real components do.
      var a = AddNode(graph, "A", 60, 28);
      var b = AddNode(graph, "B", 90, 44);
      var c = AddNode(graph, "C", 70, 28);
      var d = AddNode(graph, "D", 110, 60);

      graph.Edges.Add(new Edge(a, b));
      graph.Edges.Add(new Edge(a, c));
      graph.Edges.Add(new Edge(b, d));
      graph.Edges.Add(new Edge(c, d));

      var settings = new SugiyamaLayoutSettings
      {
        // Grasshopper flows left-to-right; MSAGL's layered layout defaults to top-to-bottom, so rotate
        // the result a quarter turn. (WP-A will own the exact transform + GH-pivot mapping; here we only
        // need a sane horizontal spread to eyeball.)
        Transformation = PlaneTransformation.Rotation(Math.PI / 2),
        LayerSeparation = 40,
        NodeSeparation = 20,
      };

      LayoutHelpers.CalculateLayout(graph, settings, null);

      var result = new Dictionary<string, Point>();
      foreach (var n in graph.Nodes)
        result[(string)n.UserData] = n.Center;
      return result;
    }

    private static Node AddNode(GeometryGraph graph, string id, double w, double h)
    {
      var node = new Node(CurveFactory.CreateRectangle(w, h, new Point(0, 0)), id);
      graph.Nodes.Add(node);
      return node;
    }

    /// <summary>Two coordinate maps are equal when every node lands at the exact same centre (the byte-
    /// identical bar; no tolerance - a deterministic layout must reproduce exactly).</summary>
    private static bool CoordsEqual(Dictionary<string, Point> a, Dictionary<string, Point> b)
    {
      if (a.Count != b.Count) return false;
      foreach (var kv in a)
      {
        if (!b.TryGetValue(kv.Key, out var p)) return false;
        if (p.X != kv.Value.X || p.Y != kv.Value.Y) return false;
      }
      return true;
    }

    private static string Fmt(Point p) =>
      "(" + p.X.ToString("0.0", CultureInfo.InvariantCulture) + ", " + p.Y.ToString("0.0", CultureInfo.InvariantCulture) + ")";

    private static string MsaglVersion()
    {
      try { return typeof(GeometryGraph).Assembly.GetName().Version?.ToString() ?? "?"; }
      catch { return "?"; }
    }
  }
}
