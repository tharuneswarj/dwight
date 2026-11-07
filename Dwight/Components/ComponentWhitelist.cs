using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Grasshopper;
using Grasshopper.Kernel;

namespace Dwight.Components
{
  /// <summary>One installed component, distilled from its <see cref="IGH_ObjectProxy"/>.</summary>
  internal sealed class ComponentInfo
  {
    public string Name { get; set; }
    public Guid ComponentGuid { get; set; }
    public string Category { get; set; }
    public string SubCategory { get; set; }
    public string Description { get; set; }

    /// <summary>"Curve > Primitive > Rectangle" - the form that names exactly this one when the bare name does not (U25).</summary>
    public string Qualified => ComponentMatchCore.Qualified(Category, SubCategory, Name);

    public string ToLine()
    {
      var d = string.IsNullOrWhiteSpace(Description) ? "" : Description.Replace("\r", " ").Replace("\n", " ");
      if (d.Length > 140) d = d.Substring(0, 140) + "...";
      return $"{Name} | {ComponentGuid} | {Category} > {SubCategory} | {d}";
    }
  }

  /// <summary>
  /// Layer 1 of the component knowledge layer (ADR-017): the live whitelist enumerated from
  /// <c>ComponentServer</c>, so the model can only reference components that actually exist
  /// on this machine - native and plugin alike. Exposed to Claude as the
  /// <c>search_components</c> tool, and used to inject the selection's component types into
  /// the prompt. Ports are not captured in v1 (reading them requires instantiating every
  /// proxy, which is slow and can fault); name/GUID/category/description is enough to choose
  /// a component, and wiring detail is an agent-loop (Phase 4) concern.
  /// </summary>
  internal static class ComponentWhitelist
  {
    private static readonly object _gate = new object();
    private static List<ComponentInfo> _cache;
    private static Dictionary<Guid, ComponentInfo> _byGuid;
    // U25 (#915): EVERY proxy per name, never the first. "Rectangle" names a param and a component; keeping one of
    // them made the other unreachable by any spelling, and which one was kept was enumeration luck.
    private static Dictionary<string, List<ComponentInfo>> _byName;      // exact name -> every component with it
    private static Dictionary<string, List<ComponentInfo>> _byNameNorm;  // normalised name -> the same (ADR-083 lesson)

    /// <summary>The separator of a qualified name, for the reads that present one (lane C): " > ".</summary>
    public const string QualifiedSeparator = ComponentMatchCore.QualifiedSeparator;

    /// <summary>
    /// Phase-8 WP-2: drop the cached enumeration so the NEXT call re-reads <c>ComponentServer</c>.
    /// The cache is built once per session, so a plugin the user loaded after Dwight first enumerated
    /// would otherwise stay invisible for the rest of the session - and the agent's whitelist guard
    /// would refuse a component that is genuinely installed. Called when the agent tool surface opens.
    /// </summary>
    public static void Reset()
    {
      lock (_gate) { _cache = null; _byGuid = null; _byName = null; _byNameNorm = null; }
    }

    public static IReadOnlyList<ComponentInfo> All()
    {
      if (_cache != null) return _cache;

      var list = new List<ComponentInfo>();
      var byGuid = new Dictionary<Guid, ComponentInfo>();
      var byName = new Dictionary<string, List<ComponentInfo>>(StringComparer.OrdinalIgnoreCase);
      var byNameNorm = new Dictionary<string, List<ComponentInfo>>();

      var server = Instances.ComponentServer;
      if (server != null)
      {
        foreach (var proxy in server.ObjectProxies)
        {
          try
          {
            if (proxy == null || proxy.Obsolete) continue;
            // Only real placeable components/params, not hidden infrastructure.
            if (proxy.Kind != GH_ObjectType.CompiledObject && proxy.Kind != GH_ObjectType.UserObject)
              continue;

            var info = new ComponentInfo
            {
              Name = proxy.Desc?.Name ?? "(unnamed)",
              ComponentGuid = proxy.Guid,
              Category = proxy.Desc?.Category ?? "",
              SubCategory = proxy.Desc?.SubCategory ?? "",
              Description = proxy.Desc?.Description ?? "",
            };
            list.Add(info);
            if (!byGuid.ContainsKey(info.ComponentGuid)) byGuid[info.ComponentGuid] = info;
            if (!string.IsNullOrWhiteSpace(info.Name))
            {
              if (!byName.TryGetValue(info.Name, out var bucket)) byName[info.Name] = bucket = new List<ComponentInfo>();
              bucket.Add(info);
            }
            var norm = Normalize(info.Name);
            if (norm.Length > 0)
            {
              if (!byNameNorm.TryGetValue(norm, out var bucket)) byNameNorm[norm] = bucket = new List<ComponentInfo>();
              bucket.Add(info);
            }
          }
          catch
          {
            // Skip any proxy that misbehaves rather than failing the whole enumeration.
          }
        }
      }

      lock (_gate)
      {
        _cache = list;
        _byGuid = byGuid;
        _byName = byName;
        _byNameNorm = byNameNorm;
      }
      return _cache;
    }

    /// <summary>WP-2 (ADR-034): does a component with this type name exist on this machine? The
    /// validate-before-instantiate guard for Simplify's LLM proposals. Exact match first, then a NORMALISED
    /// match (trim + collapse whitespace + case-insensitive, the ADR-083 lesson - RR3 #3), so a trivially
    /// different spelling of an installed component is not a false failure. TRUE for an AMBIGUOUS bare name
    /// too (U25): something by that name IS installed; which one is the question <see cref="Resolve"/> answers
    /// and <see cref="GuidFor"/> refuses to guess at.</summary>
    public static bool Exists(string typeName) => Resolve(typeName, out _, out _) != NameResolution.Unknown;

    /// <summary>WP-2: the component GUID to instantiate for a proposed type name (null if unknown - OR AMBIGUOUS,
    /// U25: a bare name owned by two components instantiates neither; the qualified form instantiates the one it
    /// names). Exact then normalised. The plugin stamps a pre-generated InstanceGuid onto the created object so the
    /// delta predictor stays a pure function (ruling G.1); this is the PROXY guid used to create it, not the instance guid.</summary>
    public static Guid? GuidFor(string typeName)
    {
      var info = Lookup(typeName);
      return info != null ? info.ComponentGuid : (Guid?)null;
    }

    /// <summary>WP-2: the installed component behind a type name, or null - null too when the bare name is owned by
    /// more than one (U25; #208: resolve exactly or refuse). Public so the agent tool surface can answer
    /// <c>kb_lookup</c> from the live SDK self-description (ADR-068's v1 lookup) without a second enumeration.</summary>
    public static ComponentInfo Find(string typeName) => Lookup(typeName);

    /// <summary>
    /// U25 (#915): RESOLVE A TYPE NAME EXACTLY, OR SAY WHY NOT. Found with the one component; Ambiguous with every
    /// qualified form the bare name could mean ("Params > Geometry > Rectangle", "Curve > Primitive > Rectangle");
    /// Unknown. A qualified form in <paramref name="typeName"/> resolves to the one it names. The decision is
    /// <see cref="ComponentMatchCore.Resolve{T}"/>'s, over the bucket that shares the name - the same answer it would
    /// give over the whole catalogue, because resolution never looks past the name.
    /// </summary>
    public static NameResolution Resolve(string typeName, out ComponentInfo info, out IReadOnlyList<string> qualifiedForms)
    {
      info = null;
      qualifiedForms = new string[0];
      if (string.IsNullOrWhiteSpace(typeName)) return NameResolution.Unknown;
      All();
      var bucket = Bucket(ComponentMatchCore.BareName(typeName));
      if (bucket == null) return NameResolution.Unknown;
      return ComponentMatchCore.Resolve(typeName, bucket, c => c.Name, c => c.Category, c => c.SubCategory,
        out info, out qualifiedForms);
    }

    /// <summary>
    /// THE INSTALLED SPELLING a spot's record and card carry (U25): the bare name where one component owns it, the
    /// qualified form where several do - so a record written today parses back tomorrow to the same component. Null
    /// when the name does not resolve to one component.
    /// </summary>
    public static string Canonical(string typeName) =>
      Resolve(typeName, out var info, out _) == NameResolution.Found ? CanonicalName(info) : null;

    /// <summary>A component's canonical name: bare if no other installed component shares it, qualified otherwise.
    /// For the reads that present names (lane C) as much as for the build.</summary>
    public static string CanonicalName(ComponentInfo info)
    {
      if (info == null) return null;
      All();
      var bucket = Bucket(info.Name);
      return bucket != null && bucket.Count > 1 ? info.Qualified : info.Name;
    }

    private static ComponentInfo Lookup(string typeName) =>
      Resolve(typeName, out var info, out _) == NameResolution.Found ? info : null;

    /// <summary>Every installed component sharing a bare name - exact bucket first, else the normalised one.</summary>
    private static List<ComponentInfo> Bucket(string bareName)
    {
      if (string.IsNullOrWhiteSpace(bareName)) return null;
      var t = bareName.Trim();
      if (_byName != null && _byName.TryGetValue(t, out var exact)) return exact;
      var norm = Normalize(t);
      if (norm.Length > 0 && _byNameNorm != null && _byNameNorm.TryGetValue(norm, out var n)) return n;
      return null;
    }

    /// <summary>WP-2 (#190): the nearest INSTALLED type names to an unrecognised one, best first. This is
    /// the repair payload on an <c>unknown_component_type</c> refusal - the plugin computes the ground
    /// truth and hands it back, so the model can correct itself instead of guessing again (ADR-092's
    /// shape at the tool boundary). Ranking lives in <see cref="ComponentMatchCore"/> and is tested there.
    /// U25: ranked by BARE name (so a typo still finds its target) and presented CANONICAL - qualified where the
    /// bare name is shared - so a suggestion taken next resolves rather than being refused as ambiguous.</summary>
    public static IReadOnlyList<string> Nearest(string typeName, int max = 5) =>
      ComponentMatchCore.NearestCanonical(typeName, All(), c => c.Name, c => c.Category, c => c.SubCategory, max);

    /// <summary>Normalise a component name for tolerant matching. Delegates to the GH-free core so the
    /// matching rule has ONE definition - the whitelist and the nearest-match ranking can never drift.</summary>
    private static string Normalize(string name) => ComponentMatchCore.Normalize(name);

    /// <summary>Case-insensitive search over name/category/description, best matches first.</summary>
    public static IReadOnlyList<ComponentInfo> Search(string query, int max = 25)
    {
      var all = All();
      if (string.IsNullOrWhiteSpace(query)) return all.Take(max).ToList();

      var q = query.Trim();
      bool Contains(string s) => s != null && s.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;

      return all
        .Where(c => Contains(c.Name) || Contains(c.Category) || Contains(c.SubCategory) || Contains(c.Description))
        .OrderBy(c => c.Name != null && c.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
        .ThenBy(c => c.Name?.Length ?? int.MaxValue)
        .Take(max)
        .ToList();
    }

    /// <summary>Look up the installed components matching a set of component GUIDs.</summary>
    public static IReadOnlyList<ComponentInfo> Describe(IEnumerable<Guid> componentGuids)
    {
      All(); // ensure built
      var seen = new HashSet<Guid>();
      var result = new List<ComponentInfo>();
      foreach (var g in componentGuids)
        if (seen.Add(g) && _byGuid != null && _byGuid.TryGetValue(g, out var info))
          result.Add(info);
      return result;
    }

    public static string ToText(IEnumerable<ComponentInfo> items)
    {
      var sb = new StringBuilder();
      foreach (var i in items) sb.AppendLine(i.ToLine());
      return sb.ToString().TrimEnd();
    }
  }
}
