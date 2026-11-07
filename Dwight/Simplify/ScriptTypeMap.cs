using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Dwight.Simplify
{
  internal enum ScriptLanguage { CSharp, Python3 }

  /// <summary>How a script param receives its data. Structure, so the PLUGIN owns it (ADR-056) - derived from the
  /// boundary port's live tree signature, never proposed by the model.</summary>
  internal enum ScriptAccess { Item, List, Tree }

  /// <summary>
  /// WP-4 (to-script), the GH-free half (ADR-091): the Grasshopper-goo-to-script-type mapping, the access
  /// derivation, and identifier sanitisation. The governing law, generalised from RR7: the model supplies the
  /// CODE and the NAMES; the plugin computes every piece of STRUCTURE - arity, order, type hint, access - from
  /// the live boundary contract. A model that cannot name a port or a type cannot mis-wire one.
  ///
  /// The hint names are the script component's own (instrumented live via the bridge, not inferred): the two
  /// languages share every geometry hint and differ ONLY on the numeric scalar - C# <c>double</c> vs Python
  /// <c>float</c>. An unmapped type is NOT an error: it falls back to no hint (generic goo), which the model can
  /// still handle - and if its code cannot, the compile error is caught and reported before anything is spliced.
  /// </summary>
  internal static class ScriptTypeMap
  {
    /// <summary>The whitelist name of each language's script component (both confirmed present in Rhino 8;
    /// instantiation is still whitelist-gated, so a missing one fails honestly before any mutation).</summary>
    public static string ProxyName(ScriptLanguage lang) => lang == ScriptLanguage.CSharp ? "C# Script" : "Python 3 Script";

    public static string Label(ScriptLanguage lang) => lang == ScriptLanguage.CSharp ? "C#" : "Python 3";

    /// <summary>The GH param type name (<c>IGH_Param.TypeName</c>) -> the script component's type-hint name.
    /// Everything but the numeric scalar is shared between the languages. Null = no hint (generic goo).</summary>
    private static readonly Dictionary<string, string> Hints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
      { "Integer", "int" },       { "Boolean", "bool" },      { "Text", "string" },
      { "Complex", "Complex" },   { "Time", "DateTime" },     { "Colour", "Color" },
      { "Point", "Point3d" },     { "Vector", "Vector3d" },   { "Plane", "Plane" },
      { "Domain", "Interval" },   { "Domain²", "UVInterval" },{ "Guid", "Guid" },
      { "Box", "Box" },           { "Transform", "Transform" },
      { "Line", "Line" },         { "Circle", "Circle" },     { "Arc", "Arc" },
      { "Curve", "Curve" },       { "Rectangle", "Rectangle3d" },
      { "Mesh", "Mesh" },         { "Surface", "Surface" },   { "Extrusion", "Extrusion" },
      { "SubD", "SubD" },         { "Brep", "Brep" },         { "Point Cloud", "PointCloud" },
      { "Geometry", "GeometryBase" },
      { "Hatch", "Hatch" },       { "Text Dot", "TextDot" },
      { "Text Entity", "TextEntity" }, { "Leader", "Leader" },
      // "Generic Data" and everything unlisted deliberately fall through to no hint.
    };

    /// <summary>The type-hint name for a boundary port's GH type, or null for "No Type Hint". The ONE
    /// language-dependent entry is the numeric scalar (C# double / Python float) - the rest are shared.</summary>
    public static string HintFor(string ghTypeName, ScriptLanguage lang)
    {
      var t = (ghTypeName ?? "").Trim();
      if (t.Length == 0) return null;
      if (t.Equals("Number", StringComparison.OrdinalIgnoreCase))
        return lang == ScriptLanguage.CSharp ? "double" : "float";
      return Hints.TryGetValue(t, out var hint) ? hint : null;
    }

    /// <summary>U88: is this a hint name the probe knows - one <see cref="HintFor"/> can return - so it is safe to select?
    /// The component's Select throws on any other.</summary>
    public static bool IsKnownHint(string hint) =>
      !string.IsNullOrEmpty(hint) && (hint == "double" || hint == "float" || Hints.Values.Contains(hint, StringComparer.Ordinal));

    /// <summary>
    /// U84: the ACCESS a scaffold-built script's DECLARED output is configured with, so it is configured as to-script configures
    /// its outputs. A Python 3 output is LIST access (a list assigned to it emits as items; item access wraps the whole list as
    /// one item, which is what the measurement showed). A declared output has no known type, so it gets no hint: generic goo,
    /// the legitimate contract. A C# output is left as born (null): its item access already emits a list's items, measured. Pure.
    /// </summary>
    internal static ScriptAccess? DeclaredOutputAccess(ScriptLanguage lang) =>
      lang == ScriptLanguage.Python3 ? ScriptAccess.List : (ScriptAccess?)null;

    /// <summary>The hints whose data is geometry: where sources of several of these meet in one input, the shared base type
    /// is the honest hint.</summary>
    private static readonly HashSet<string> GeometryHints = new HashSet<string>(StringComparer.Ordinal)
    {
      "Point3d", "Vector3d", "Plane", "Box", "Line", "Circle", "Arc", "Curve", "Rectangle3d",
      "Mesh", "Surface", "Extrusion", "SubD", "Brep", "PointCloud", "GeometryBase",
      "Hatch", "TextDot", "TextEntity", "Leader",
    };

    /// <summary>
    /// U74: the type-hint name for a script INPUT, from the GH type names of what is wired into it (the live graph, read by
    /// the plugin; ADR-056). One kind of source gives that kind's hint; several geometry kinds give the geometry base type;
    /// anything else mixed, nothing wired, or a type with no hint gives null - no selection, the shim's default for the
    /// language. Only names <see cref="HintFor"/> knows are ever returned, because the component's Select throws on an unknown one.
    /// </summary>
    public static string HintForSources(IEnumerable<string> sourceGhTypeNames, ScriptLanguage lang)
    {
      var hints = new List<string>();
      foreach (var t in sourceGhTypeNames ?? new string[0])
      {
        var h = HintFor(t, lang);
        if (h == null) return null;   // one source with no known hint makes the input mixed with unknown: leave the default
        if (!hints.Contains(h)) hints.Add(h);
      }
      if (hints.Count == 0) return null;
      if (hints.Count == 1) return hints[0];
      return hints.All(GeometryHints.Contains) ? "GeometryBase" : null;
    }

    /// <summary>
    /// U77: the hint for each wired INPUT of a script, computed BEFORE the component enters the document, from the spec's wires.
    /// Each entry is (the input's index on the script, the GH type name of the output feeding it). An input whose sources
    /// give no hint is absent from the result, so nothing is selected on it. Pure: the build resolves the wires to
    /// indexes and type names; this decides.
    /// </summary>
    public static Dictionary<int, string> HintsByInput(IEnumerable<KeyValuePair<int, string>> wires, ScriptLanguage lang)
    {
      var result = new Dictionary<int, string>();
      foreach (var group in (wires ?? new KeyValuePair<int, string>[0]).GroupBy(w => w.Key))
      {
        var hint = HintForSources(group.Select(w => w.Value), lang);
        if (hint != null) result[group.Key] = hint;
      }
      return result;
    }

    /// <summary>The script type the model writes its code against, for the PROMPT and the preview spec - the
    /// declared type of the variable as the user will read it. A hintless param arrives as generic goo.</summary>
    public static string DeclaredType(string ghTypeName, ScriptLanguage lang, ScriptAccess access)
    {
      var hint = HintFor(ghTypeName, lang) ?? (lang == ScriptLanguage.CSharp ? "object" : "object");
      if (lang == ScriptLanguage.CSharp)
        return access == ScriptAccess.Item ? hint
             : access == ScriptAccess.List ? $"List<{hint}>"
                                           : $"DataTree<{hint}>";
      return access == ScriptAccess.Item ? hint
           : access == ScriptAccess.List ? $"list[{hint}]"
                                         : $"DataTree[{hint}]";
    }

    /// <summary>
    /// Derive a param's ACCESS from the live tree signature of the data crossing that boundary port (architect's
    /// rule A): a single item -> item, one flat branch of many -> list, several branches -> tree.
    ///
    /// This does not have to be provably right a priori, and that is what makes it tractable: a wrong access
    /// produces a differently-shaped output tree, which the beside-verification catches and reverts. The bar is
    /// "sensible, with verification as the arbiter", not "correct by proof".
    /// </summary>
    public static ScriptAccess AccessFor(int pathCount, IReadOnlyList<int> itemsPerBranch)
    {
      if (pathCount > 1) return ScriptAccess.Tree;
      var items = itemsPerBranch != null && itemsPerBranch.Count > 0 ? itemsPerBranch[0] : 0;
      return items > 1 ? ScriptAccess.List : ScriptAccess.Item;
    }

    // C# and Python reserved words a variable name may not collide with (the union - one sanitiser, both
    // languages, so a spec is portable between them and a name never has to be re-derived per language).
    private static readonly HashSet<string> Reserved = new HashSet<string>(StringComparer.Ordinal)
    {
      "abstract","as","base","bool","break","byte","case","catch","char","checked","class","const","continue",
      "decimal","default","delegate","do","double","else","enum","event","explicit","extern","false","finally",
      "fixed","float","for","foreach","goto","if","implicit","in","int","interface","internal","is","lock","long",
      "namespace","new","null","object","operator","out","override","params","private","protected","public",
      "readonly","ref","return","sbyte","sealed","short","sizeof","stackalloc","static","string","struct","switch",
      "this","throw","true","try","typeof","uint","ulong","unchecked","unsafe","ushort","using","var","virtual",
      "void","volatile","while",
      "and","as","assert","async","await","del","elif","except","exec","from","global","import","lambda","nonlocal",
      "not","or","pass","raise","with","yield","None","True","False","print","self",
    };

    /// <summary>
    /// Turn the model's chosen NAME into a legal, unique script identifier. The model names things (semantics);
    /// the plugin guarantees the name compiles (structure). A name that sanitises to nothing, or collides, gets a
    /// deterministic positional fallback - so a bad name can never produce a broken component, only a duller one.
    /// </summary>
    public static string VariableName(string proposed, int index, ISet<string> taken)
    {
      var sb = new StringBuilder();
      foreach (var ch in (proposed ?? "").Trim())
      {
        if (char.IsLetterOrDigit(ch) || ch == '_') sb.Append(ch);
        else if (char.IsWhiteSpace(ch) || ch == '-') sb.Append('_');
        // everything else is dropped
      }
      var name = sb.ToString().Trim('_');
      if (name.Length == 0 || char.IsDigit(name[0])) name = "x" + (name.Length == 0 ? index.ToString() : name);
      if (Reserved.Contains(name)) name = "_" + name;

      var unique = name;
      var n = 2;
      while (taken != null && taken.Contains(unique)) unique = name + n++;
      taken?.Add(unique);
      return unique;
    }
  }
}
