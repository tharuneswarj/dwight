using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Dwight.Simplify
{
  /// <summary>One parameter of the model's own <c>RunScript</c> declaration (U88).</summary>
  internal sealed class RunScriptParam
  {
    public string Type { get; set; }
    public string Name { get; set; }

    /// <summary>True for <c>ref</c> and <c>out</c>: an output of the script. False: an input.</summary>
    public bool ByRef { get; set; }
  }

  /// <summary>What the model's signature decides about a scaffold C# script's hints (U88): the hint for each input and output
  /// (by index on the component), and the sentences for the types that map to nothing.</summary>
  internal sealed class SignatureHintPlan
  {
    public Dictionary<int, string> InputHints { get; } = new Dictionary<int, string>();
    public Dictionary<int, string> OutputHints { get; } = new Dictionary<int, string>();
    public List<string> Notes { get; } = new List<string>();
  }

  /// <summary>
  /// U88, THE GH-FREE HALF: for a C# scaffold script, the types the model declared in its own <c>RunScript(...)</c> choose the
  /// input hints (so the component hands the method what the method declares, not what the slider happens to be), compiler
  /// lines are mapped back to the model's own numbering, and the signature the component really generates can be shown.
  ///
  /// ONLY HINT NAMES THE SHIM'S PROBE KNOWS ARE EVER SELECTED (<see cref="ScriptTypeMap.IsKnownHint"/>): the component's
  /// Select throws on an unknown one. A type that maps to nothing leaves the source-derived hint and SAYS so.
  /// </summary>
  internal static class CSharpSignature
  {
    // C# type spelling (namespaces stripped) -> the hint name; null = the generic "No Type Hint" (nothing is selected).
    private static readonly Dictionary<string, string> TypeToHint = new Dictionary<string, string>(StringComparer.Ordinal)
    {
      { "int", "int" },           { "Int32", "int" },
      { "double", "double" },     { "Double", "double" },
      { "bool", "bool" },         { "Boolean", "bool" },
      { "string", "string" },     { "String", "string" },
      { "Point3d", "Point3d" },   { "Vector3d", "Vector3d" }, { "Plane", "Plane" },
      { "Line", "Line" },         { "Curve", "Curve" },       { "Surface", "Surface" },
      { "Brep", "Brep" },         { "Mesh", "Mesh" },         { "Box", "Box" },
      { "object", null },         { "Object", null },
    };

    /// <summary>The hint a declared C# parameter type means. <paramref name="mapped"/> is false for a type that means nothing
    /// to the component (the source-derived hint stays); true with a null result is the generic hint ("object").</summary>
    public static string HintForType(string type, out bool mapped)
    {
      mapped = false;
      var t = StripNamespaces((type ?? "").Trim());
      if (!TypeToHint.TryGetValue(t, out var hint)) return null;
      if (hint != null && !ScriptTypeMap.IsKnownHint(hint)) return null;   // never select a name the probe does not know
      mapped = true;
      return hint;
    }

    /// <summary>Brief CP: an input with list or tree access is declared <c>List&lt;T&gt;</c> or <c>DataTree&lt;T&gt;</c> in the method, and its type hint is the element's. On an item input the same
    /// declaration is the defect, so it is left alone and maps to nothing.</summary>
    private static string Element(string t)
    {
      t = StripNamespaces(t);
      foreach (var open in new[] { "List<", "IList<", "IEnumerable<", "DataTree<" })
        if (t.StartsWith(open, StringComparison.Ordinal) && t.EndsWith(">", StringComparison.Ordinal))
          return t.Substring(open.Length, t.Length - open.Length - 1).Trim();
      return t;
    }

    private static string StripNamespaces(string t)
    {
      foreach (var ns in new[] { "System.", "Rhino.Geometry." })
        if (t.StartsWith(ns, StringComparison.Ordinal)) t = t.Substring(ns.Length);
      return t;
    }

    private static readonly Regex RunScriptOpen = new Regex(@"void[ \t]+RunScript[ \t]*\(", RegexOptions.Compiled);

    /// <summary>The parameters of the model's RunScript declaration, or null when the source declares none. Reads the text,
    /// balancing parentheses and angle brackets, so <c>List&lt;double&gt;</c> and default values do not split a parameter.</summary>
    public static List<RunScriptParam> Parse(string source)
    {
      var m = RunScriptOpen.Match(source ?? "");
      if (!m.Success) return null;

      var list = new List<RunScriptParam>();
      var depth = 1; var angle = 0;
      var cur = new StringBuilder();
      var i = m.Index + m.Length;
      for (; i < source.Length; i++)
      {
        var ch = source[i];
        if (ch == '(') depth++;
        else if (ch == ')') { depth--; if (depth == 0) break; }
        else if (ch == '<') angle++;
        else if (ch == '>') angle = Math.Max(0, angle - 1);
        if (ch == ',' && depth == 1 && angle == 0) { Add(list, cur.ToString()); cur.Clear(); continue; }
        cur.Append(ch);
      }
      if (depth != 0) return null;   // an unterminated declaration is not a signature
      Add(list, cur.ToString());
      return list;
    }

    private static void Add(List<RunScriptParam> list, string raw)
    {
      var eq = raw.IndexOf('=');
      if (eq >= 0) raw = raw.Substring(0, eq);
      var tokens = Regex.Replace(raw, @"\s+", " ").Trim().Split(' ').Where(s => s.Length > 0).ToList();
      if (tokens.Count < 2) return;
      var byRef = false;
      while (tokens.Count > 2 && (tokens[0] == "ref" || tokens[0] == "out" || tokens[0] == "in" || tokens[0] == "params" || tokens[0] == "this"))
      { byRef |= tokens[0] == "ref" || tokens[0] == "out"; tokens.RemoveAt(0); }
      if (tokens.Count == 2 && (tokens[0] == "ref" || tokens[0] == "out")) return;   // a modifier and a name: no type to read
      var name = tokens[tokens.Count - 1];
      var type = string.Join(" ", tokens.Take(tokens.Count - 1));
      list.Add(new RunScriptParam { Type = type, Name = name, ByRef = byRef });
    }

    /// <summary>
    /// The hints the signature chooses, over the source-derived <paramref name="sourceHints"/> (index -> hint). A parameter is
    /// matched to its port BY NAME (the port's name is the identifier the code uses); the model's declared type wins over what
    /// the wired source implies. <c>object</c> removes the source-derived hint (the generic one). A type with no hint leaves the
    /// source-derived hint and adds a note.
    /// </summary>
    /// <param name="inputAccess">Brief CP: each input's access word, by index, or null for all item.</param>
    public static SignatureHintPlan Plan(IReadOnlyList<RunScriptParam> signature, IReadOnlyList<string> inputNames,
      IReadOnlyList<string> outputNames, IReadOnlyDictionary<int, string> sourceHints, IReadOnlyDictionary<int, string> inputAccess = null)
    {
      var plan = new SignatureHintPlan();
      foreach (var kv in sourceHints ?? new Dictionary<int, string>()) plan.InputHints[kv.Key] = kv.Value;

      foreach (var p in signature ?? new RunScriptParam[0])
      {
        var names = p.ByRef ? outputNames : inputNames;
        var index = -1;
        for (var k = 0; names != null && k < names.Count; k++)
          if (string.Equals(names[k], p.Name, StringComparison.Ordinal)) { index = k; break; }
        if (index < 0) continue;

        var collection = !p.ByRef && inputAccess != null && inputAccess.TryGetValue(index, out var word) && (word == "list" || word == "tree");
        var hint = HintForType(collection ? Element(p.Type) : p.Type, out var mapped);
        if (!mapped)
        {
          plan.Notes.Add("no hint for type " + p.Type + " on " + (p.ByRef ? "output " : "input ") + p.Name +
                         "; it arrives as " + (p.ByRef ? "the component's default" : "its source's type"));
          continue;
        }
        if (p.ByRef)
        {
          if (hint != null) plan.OutputHints[index] = hint;
        }
        else if (hint == null) plan.InputHints.Remove(index);
        else plan.InputHints[index] = hint;
      }
      return plan;
    }

    /// <summary>The RunScript signature the component really generates for these ports and hints: what each input is handed
    /// (its hint, else <c>object</c>) and what each output is. The model's source is compiled against THIS.</summary>
    /// <param name="inputAccess">Brief CP: each input's access word, by index, or null for all item. "list" is handed <c>List&lt;T&gt;</c>, "tree" <c>DataTree&lt;T&gt;</c>.</param>
    public static string Generated(IReadOnlyList<string> inputNames, IReadOnlyDictionary<int, string> inputHints,
      IReadOnlyList<string> outputNames, IReadOnlyDictionary<int, string> outputHints, IReadOnlyDictionary<int, string> inputAccess = null)
    {
      var parts = new List<string>();
      for (var i = 0; i < (inputNames?.Count ?? 0); i++)
        parts.Add(Handed(HintTypeName(inputHints, i), inputAccess, i) + " " + inputNames[i]);
      for (var i = 0; i < (outputNames?.Count ?? 0); i++)
        parts.Add("ref " + HintTypeName(outputHints, i) + " " + outputNames[i]);
      return "private void RunScript(" + string.Join(", ", parts) + ")";
    }

    private static string Handed(string type, IReadOnlyDictionary<int, string> access, int i)
    {
      var word = access != null && access.TryGetValue(i, out var w) ? w : null;
      return word == "list" ? "List<" + type + ">" : word == "tree" ? "DataTree<" + type + ">" : type;
    }

    private static string HintTypeName(IReadOnlyDictionary<int, string> hints, int i) =>
      hints != null && hints.TryGetValue(i, out var h) && !string.IsNullOrEmpty(h) ? h : "object";

    // "[24:16]" at the end of a compiler diagnostic: line, column, in the COMPILED file.
    private static readonly Regex Position = new Regex(@"\[(\d+):(\d+)\]", RegexOptions.Compiled);

    /// <summary>
    /// A diagnostic with its position in the model's own numbering: "[line 7 of your script, line 24 of the compiled file, col 16]".
    /// <paramref name="shift"/> is how many lines the normaliser put before the model's first line
    /// (<see cref="UsingNormalizer.LineShift"/>); zero leaves the text as the compiler wrote it. A position inside the lines the
    /// normaliser added says so rather than naming a line of the model's that does not hold it.
    /// </summary>
    public static string MapDiagnostic(string diagnostic, int shift)
    {
      if (shift == 0 || string.IsNullOrEmpty(diagnostic)) return diagnostic;
      return Position.Replace(diagnostic, m =>
      {
        var compiled = int.Parse(m.Groups[1].Value);
        var col = m.Groups[2].Value;
        var mine = compiled - shift;
        return mine >= 1
          ? "[line " + mine + " of your script, line " + compiled + " of the compiled file, col " + col + "]"
          : "[line " + compiled + " of the compiled file, in the lines Dwight added around your script, col " + col + "]";
      });
    }
  }
}
