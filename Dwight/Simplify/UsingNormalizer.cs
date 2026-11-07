using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Dwight.Simplify
{
  /// <summary>
  /// Phase-8 WP-0 (#141/#146): the PLUGIN OWNS THE C# FORMAT CONTRACT.
  ///
  /// Instrumented fact, not an assumption (#141 step 1): the Rhino 8 C# script component supplies NO implicit
  /// using directives. A bare body using <c>List&lt;&gt;</c>/<c>Point3d</c> FAILS to compile; the same body with the
  /// explicit usings COMPILES. So every namespace a script needs must be declared in the source - and the model
  /// declares them unreliably: 12 of 28 build failures in the oracle-on arm were outright missing a using, and 3 of
  /// 9 even in the oracle-off baseline. The output-oracle's retry made it worse by pushing the model toward richer
  /// (list/geometry) code that needs usings the simple first attempt never did.
  ///
  /// The fix is DETERMINISTIC and free (ADR-056: the plugin computes structure, the model supplies judgement): union
  /// a plugin-defined BASELINE set with whatever the model emitted, on EVERY generation. It deletes the whole
  /// missing-using failure class at zero token cost, and it can only ADD - a namespace the model needed that we did
  /// not anticipate always survives.
  ///
  /// C# ONLY (#146). Python has no using mechanism and is deliberately not preempted.
  /// </summary>
  internal static class UsingNormalizer
  {
    /// <summary>The namespaces every generated C# script may assume. Verified once by instrumentation (#146): the
    /// full set injected over a trivial script COMPILES, so the normalizer can never introduce an unreferenced or
    /// unresolvable namespace. Keep this list conservative - it is a floor, not a convenience dump.</summary>
    public static readonly string[] CSharpBaseline =
    {
      "System",
      "System.Collections.Generic",
      "System.Linq",
      "Rhino",
      "Rhino.Geometry",
      "Grasshopper",
      "Grasshopper.Kernel",
      "Grasshopper.Kernel.Data",
      "Grasshopper.Kernel.Types",
    };

    // a PLAIN namespace import only: `using A.B.C;`. Deliberately NOT matching `using static X;` or an alias
    // `using P = A.B;` - those are not namespace imports, carry meaning we must not dedupe away, and stay in place.
    private static readonly Regex PlainUsing = new Regex(@"^[ \t]*using[ \t]+([A-Za-z_][\w]*(?:[ \t]*\.[ \t]*[A-Za-z_][\w]*)*)[ \t]*;[ \t]*$",
      RegexOptions.Compiled);

    public static string Normalize(string source, ScriptLanguage lang)
    {
      if (lang != ScriptLanguage.CSharp) return source ?? "";   // #146: C# only; Python untouched
      source = source ?? "";

      // 1. lift every PLAIN using out of the body (so it cannot be emitted twice), keeping everything else verbatim.
      var declared = new List<string>();
      var body = new List<string>();
      foreach (var line in source.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
      {
        var m = PlainUsing.Match(line);
        if (m.Success) declared.Add(Collapse(m.Groups[1].Value));
        else body.Add(line);
      }

      // 2. UNION: the baseline first (stable, readable order), then anything the model needed that we did not list.
      var merged = new List<string>();
      var seen = new HashSet<string>(StringComparer.Ordinal);
      foreach (var ns in CSharpBaseline) if (seen.Add(ns)) merged.Add(ns);
      foreach (var ns in declared) if (seen.Add(ns)) merged.Add(ns);

      // 3. re-emit: the merged header, one blank line, then the body with its leading blank lines trimmed (so
      // repeated normalization is idempotent and the source never accretes whitespace).
      var sb = new StringBuilder();
      foreach (var ns in merged) sb.Append("using ").Append(ns).Append(";\n");
      sb.Append('\n');
      sb.Append(string.Join("\n", body).TrimStart('\n'));
      return sb.ToString();
    }

    // A type declaration at the start of a line: the script is already a complete class (or carries its own).
    private static readonly Regex HasClass = new Regex(@"^[ \t]*(?:(?:public|internal|private|static|sealed|partial|abstract)[ \t]+)*class[ \t]+[A-Za-z_]\w*",
      RegexOptions.Compiled | RegexOptions.Multiline);

    // A RunScript method declaration at the start of a line: `[modifiers] void RunScript(`.
    private static readonly Regex RunScriptDecl = new Regex(@"^[ \t]*(?:(?:public|private|protected|internal|static)[ \t]+)*void[ \t]+RunScript[ \t]*\(",
      RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>The class a Rhino 8 C# script component holds; the wrapped form is named after it.</summary>
    public const string TemplateClass = "public class Script_Instance : GH_ScriptInstance";

    /// <summary>
    /// U45 (#141/#146 applied to a BUILD's source): THE SOURCE THE COMPONENT RECEIVES IS IN THE COMPONENT'S SHAPE.
    ///
    /// The U19 gate saw a model send the to-script body style - a bare <c>private void RunScript(Curve x, Curve y, ref
    /// object a) { ... }</c> - and the compiler's first errors were "The modifier 'private' is not valid for this item",
    /// "List&lt;&gt; could not be found" and "Curve could not be found": SHAPE before meaning, a deterministic class the
    /// plugin computes (ADR-056), never a billed round-trip.
    ///
    /// Three shapes, decided on the text:
    ///  - a COMPLETE CLASS (any class declaration) passes through byte-for-byte - the model wrote the shape and its own
    ///    usings, and rewriting a class would be guessing;
    ///  - a BODY-ONLY RunScript (a method with no class around it) is wrapped in the Script_Instance template, its
    ///    usings unioned with the baseline and lifted to the top;
    ///  - anything else (statements) takes the usings union only - to-script's own contract.
    /// Idempotent: the wrapped form is a complete class. C# only; Python is the caller's to skip (it has no such shape).
    /// </summary>
    public static string ToComponentTemplate(string source)
    {
      source = source ?? "";
      if (HasClass.IsMatch(source)) return source;
      if (!RunScriptDecl.IsMatch(source)) return Normalize(source, ScriptLanguage.CSharp);

      // Reuse the union (baseline + the model's own usings, lifted), then put the remainder inside the class.
      var unioned = Normalize(source, ScriptLanguage.CSharp);
      var lines = unioned.Split('\n');
      var header = new List<string>();
      var i = 0;
      while (i < lines.Length && lines[i].StartsWith("using ", StringComparison.Ordinal)) header.Add(lines[i++]);
      var body = lines.Skip(i).SkipWhile(l => l.Trim().Length == 0).ToList();
      while (body.Count > 0 && body[body.Count - 1].Trim().Length == 0) body.RemoveAt(body.Count - 1);

      var sb = new StringBuilder();
      foreach (var h in header) sb.Append(h).Append('\n');
      sb.Append('\n').Append(TemplateClass).Append('\n').Append("{\n");
      foreach (var l in body) sb.Append(l.Length == 0 ? "" : "  " + l).Append('\n');
      sb.Append("}");   // no trailing newline: SourceToken trims one, and the shown text must re-parse to itself (#153)
      return sb.ToString();
    }

    /// <summary>
    /// U88: HOW MANY LINES THE NORMALISER PUT BEFORE THE MODEL'S FIRST LINE, so a compiler position in the generated file can be
    /// given in the model's own numbering (model line = compiled line - shift). Measured as the generated line of the first
    /// line of the model's script that survived (its plain usings are lifted into the header) minus that line's number in
    /// the model's text. A complete class passes through untouched: zero. Exact when the model's own usings sit at the top, as
    /// they do; a using in the middle of a script shifts the lines after it by one and is not corrected.
    /// </summary>
    public static int LineShift(string modelSource, string generated)
    {
      if (string.Equals(modelSource, generated, StringComparison.Ordinal)) return 0;
      var mine = (modelSource ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
      var first = -1;
      for (var i = 0; i < mine.Length; i++)
      {
        if (mine[i].Trim().Length == 0 || PlainUsing.IsMatch(mine[i])) continue;
        first = i; break;
      }
      if (first < 0) return 0;

      var gen = (generated ?? "").Split('\n');
      var g = 0;
      while (g < gen.Length && (gen[g].StartsWith("using ", StringComparison.Ordinal) || gen[g].Trim().Length == 0)) g++;
      if (g < gen.Length && gen[g].StartsWith(TemplateClass, StringComparison.Ordinal))
      {
        g++;
        if (g < gen.Length && gen[g].Trim() == "{") g++;
      }
      return g >= gen.Length ? 0 : g - first;
    }

    /// <summary>Normalise whitespace inside a dotted namespace so `Rhino . Geometry` dedupes against `Rhino.Geometry`.</summary>
    private static string Collapse(string ns) => string.Join(".", ns.Split('.').Select(p => p.Trim()));
  }
}
