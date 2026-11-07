using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;

namespace Dwight.Data
{
  /// <summary>
  /// RR20 #49: API grounding is REFLECTION, not curation.
  ///
  /// The model hallucinates RhinoCommon APIs - it invented `LoftOptions` (which does not exist), confused `Brep`
  /// with `Brep[]`. The instinct is to hand-write an API map or paste in documentation. Both are wrong:
  /// RhinoCommon.dll is ALREADY LOADED IN THIS PROCESS - the very assembly the script will compile against. So we
  /// read the truth straight out of it. That is:
  ///   - always CORRECT (it is the same DLL the compiler sees),
  ///   - VERSION-EXACT to this user's Rhino point release, not to whatever we guessed when we wrote a map,
  ///   - zero-maintenance, offline, free, and no egress (a web search would cost latency, money, and the
  ///     fully-local promise, for a WORSE answer - a forum post is not ground truth; a reflected signature is).
  ///
  /// A LOOKED-UP API CANNOT BE HALLUCINATED. And the shape is ADR-056 exactly: the plugin computes the truth, the
  /// model supplies the judgement about which API to reach for.
  ///
  /// Two halves:
  ///   PUSH (seed): for the components in the selection, a small ANCHOR map (Loft -> Brep, Curve, LoftType) whose
  ///     types are reflected into the prompt. Bounded and exactly relevant. The anchors are the only curated thing
  ///     here, and they are just NAMES - every signature comes from the DLL.
  ///   PULL (tool): lookup_api(typeName) -> the reflected public signatures of that type, so a model taking a
  ///     creative route can ask for ground truth instead of inventing it.
  /// </summary>
  internal static class RhinoApiReflector
  {
    /// <summary>Anchor TYPES per Grasshopper component - the RhinoCommon types a script reproducing that component
    /// will almost certainly need. Only the names are curated; every signature is reflected out of the live DLL, so
    /// this list can never be stale or wrong - at worst it is incomplete, and the lookup_api tool covers that.</summary>
    private static readonly Dictionary<string, string[]> Anchors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
      { "Loft", new[] { "Brep", "LoftType", "Curve" } },
      { "Extrude", new[] { "Surface", "Extrusion", "Brep", "Curve" } },
      { "Offset Curve", new[] { "Curve", "CurveOffsetCornerStyle" } },
      { "Move", new[] { "Transform", "GeometryBase", "Vector3d" } },
      { "Rotate", new[] { "Transform", "GeometryBase", "Plane" } },
      { "Scale", new[] { "Transform", "GeometryBase" } },
      { "Divide Curve", new[] { "Curve", "Point3d" } },
      { "Evaluate Curve", new[] { "Curve", "Point3d", "Vector3d" } },
      { "Curve | Curve", new[] { "Intersection", "CurveIntersections" } },
      { "Region Union", new[] { "Curve", "Curve3d" } },
      { "Boundary Surfaces", new[] { "Brep", "Curve" } },
      { "Surface Split", new[] { "Brep", "Surface", "Curve" } },
      { "Mesh", new[] { "Mesh", "MeshingParameters" } },
      { "Pipe", new[] { "Brep", "Curve" } },
      { "Sweep1", new[] { "Brep", "Curve", "SweepOneRail" } },
      { "Sweep2", new[] { "Brep", "Curve", "SweepTwoRail" } },
      { "Fillet", new[] { "Curve", "Brep" } },
      { "Join Curves", new[] { "Curve" } },
      { "Explode", new[] { "Curve", "PolyCurve", "PolylineCurve" } },
      { "Area", new[] { "AreaMassProperties" } },
      { "Volume", new[] { "VolumeMassProperties" } },
      { "Deconstruct Brep", new[] { "Brep", "BrepFace", "BrepEdge" } },
      { "Cull Pattern", new[] { "Curve" } },
      { "Linear Array", new[] { "Transform", "GeometryBase", "Vector3d" } },
      { "Polar Array", new[] { "Transform", "GeometryBase", "Plane" } },
    };

    /// <summary>Types every geometry script leans on, seeded regardless of the selection.</summary>
    private static readonly string[] Always = { "Point3d", "Vector3d", "Plane", "Curve" };

    /// <summary>PUSH: the reflected signatures for the types the SELECTION's components imply. Bounded by
    /// <paramref name="maxTypes"/> so the prompt stays exactly relevant rather than exhaustive.</summary>
    public static string SeedFor(IEnumerable<string> componentTypes, int maxTypes = 6)
    {
      var wanted = new List<string>();
      foreach (var t in componentTypes ?? Enumerable.Empty<string>())
      {
        if (t == null || !Anchors.TryGetValue(t.Trim(), out var anchors)) continue;
        foreach (var a in anchors) if (!wanted.Contains(a)) wanted.Add(a);
      }
      foreach (var a in Always) if (!wanted.Contains(a)) wanted.Add(a);

      var sb = new StringBuilder();
      foreach (var type in wanted.Take(maxTypes))
      {
        var sig = Signatures(type);
        if (sig != null) sb.AppendLine(sig).AppendLine();
      }
      return sb.Length == 0 ? null : sb.ToString();
    }

    /// <summary>PULL: the public signatures of a RhinoCommon type, reflected out of the loaded assembly. Null when
    /// the type does not exist - which is itself the answer the model needs ("LoftOptions does not exist").</summary>
    public static string Signatures(string typeName, int maxMembers = 40)
    {
      var t = Resolve(typeName);
      if (t == null) return null;

      var sb = new StringBuilder();
      sb.AppendLine($"{t.FullName}{(t.IsAbstract && t.IsSealed ? " (static)" : "")}:");

      var seen = new HashSet<string>(StringComparer.Ordinal);
      var lines = new List<string>();

      foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
                         .Where(m => !m.IsSpecialName))
      {
        var line = "  static " + Format(m);
        if (seen.Add(line)) lines.Add(line);           // the static factories (Brep.CreateFromLoft, ...) come first:
      }                                                 // they are what a script actually calls.
      foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                         .Where(m => !m.IsSpecialName))
      {
        var line = "  " + Format(m);
        if (seen.Add(line)) lines.Add(line);
      }
      foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
      {
        var line = $"  {Short(p.PropertyType)} {p.Name} {{ {(p.CanRead ? "get; " : "")}{(p.CanWrite ? "set; " : "")}}}";
        if (seen.Add(line)) lines.Add(line);
      }
      if (t.IsEnum)
        foreach (var name in Enum.GetNames(t)) lines.Add("  " + name);

      foreach (var line in lines.Take(maxMembers)) sb.AppendLine(line);
      if (lines.Count > maxMembers) sb.AppendLine($"  ... and {lines.Count - maxMembers} more members");
      return sb.ToString();
    }

    /// <summary>Find a type in the LOADED RhinoCommon (and Grasshopper) assemblies - by full name, or by short name
    /// across the geometry namespaces, so the model can just say "Brep".</summary>
    private static Type Resolve(string typeName)
    {
      var name = (typeName ?? "").Trim();
      if (name.Length == 0) return null;
      try
      {
        var asms = AppDomain.CurrentDomain.GetAssemblies()
          .Where(a => { var n = a.GetName().Name; return n == "RhinoCommon" || n == "Grasshopper"; })
          .ToList();

        foreach (var a in asms)
        {
          var direct = a.GetType(name, false, true)
                       ?? a.GetType("Rhino.Geometry." + name, false, true)
                       ?? a.GetType("Rhino.Geometry.Intersect." + name, false, true);
          if (direct != null) return direct;
        }
        foreach (var a in asms)   // last resort: a short-name scan over the public types
        {
          var hit = a.GetExportedTypes().FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
          if (hit != null) return hit;
        }
      }
      catch { }
      return null;
    }

    private static string Format(MethodInfo m)
    {
      var ps = string.Join(", ", m.GetParameters().Select(p =>
        (p.IsOut ? "out " : p.ParameterType.IsByRef ? "ref " : "") + Short(p.ParameterType) + " " + p.Name));
      return $"{Short(m.ReturnType)} {m.Name}({ps})";
    }

    private static string Short(Type t)
    {
      if (t == null) return "void";
      if (t.IsByRef) t = t.GetElementType();
      if (t == typeof(void)) return "void";
      if (t == typeof(double)) return "double";
      if (t == typeof(int)) return "int";
      if (t == typeof(bool)) return "bool";
      if (t == typeof(string)) return "string";
      if (t.IsArray) return Short(t.GetElementType()) + "[]";
      if (t.IsGenericType)
      {
        var args = string.Join(", ", t.GetGenericArguments().Select(Short));
        return t.Name.Split('`')[0] + "<" + args + ">";
      }
      return t.Name;
    }
  }
}
