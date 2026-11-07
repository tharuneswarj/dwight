using System;

namespace Dwight.Data
{
  /// <summary>
  /// THE GH-FREE HALF OF READING A SCRIPT COMPONENT'S SOURCE (ADR-091), extracted at #779.
  ///
  /// WHY IT EXISTS, and it is a finding rather than a tidy-up. The digest that closed #779's defect was
  /// written first with its tests against hand-built <c>FpObject</c>s - and when both guards were reverted
  /// to watch them fail, thirteen of fourteen stayed GREEN. They were asserting a pure function BESIDE the
  /// reader while the reader itself, which is where the defect lived, had no test at all. That is #612
  /// exactly ("put the loop itself under test, not a decision beside it"), found in this change's own
  /// suite before it shipped rather than after.
  ///
  /// The reader could not be tested because it took an <c>IGH_DocumentObject</c> and no test project
  /// references Grasshopper. But nothing it DOES needs Grasshopper: recognising a script component is a
  /// rule about a type NAME, and reading the source is reflection over a plain object. Both move here,
  /// where a test can drive them against stand-in types shaped like the real components - and what is left
  /// in the GH-coupled reader is delegation with no decision in it.
  ///
  /// WHAT REMAINS UNTESTABLE HEADLESSLY, named rather than glossed: that the real Rhino types are SHAPED
  /// the way the stand-ins are - that <c>RhinoCodePluginGH</c> still exposes
  /// <c>TryGetSource(out string)</c> and the legacy GhPython component still exposes <c>Code</c>. That is
  /// a question about the loaded assembly, so it is answered where such questions are answered: the
  /// deploy probe and the runtime gate, never from memory (#137).
  /// </summary>
  internal static class ScriptSourceReflect
  {
    /// <summary>The property names the legacy script components expose their source on, in preference
    /// order. Shared with the type rule below so the two cannot drift apart.</summary>
    public static readonly string[] SourceProperties = { "Code", "ScriptSource", "ScriptCode", "Text" };

    /// <summary>
    /// Is this type name one of Grasshopper's script components? A rule about a STRING, so it is tested
    /// here and confirmed against the loaded assembly by the deploy probe - the same discipline
    /// <c>SourceBound.LanguageOf</c> already follows for the language names.
    ///
    /// It matches on NAMESPACE PREFIXES rather than on exact type names, because the concrete component
    /// classes have been renamed across Rhino 8 service releases while their namespaces have not.
    /// </summary>
    public static bool IsScriptTypeName(string typeFullName)
    {
      var n = typeFullName ?? "";
      return n.StartsWith("GhPython.", StringComparison.Ordinal)
          || n.StartsWith("RhinoCodePluginGH.Components.", StringComparison.Ordinal)
          || n.StartsWith("ScriptComponents.", StringComparison.Ordinal);   // the legacy C#/VB script components
    }

    /// <summary>
    /// Read a script component's source by reflection, separating "there is no code here" from "we could
    /// not look" - the distinction #779 turns on, because an empty script Dwight placed and a locked
    /// script it cannot read must fail in opposite directions.
    ///
    /// The order is the one the live canvas requires and was instrumented, not inferred: Rhino 8's script
    /// components answer <c>TryGetSource(out string)</c>; the legacy GhPython component and the older
    /// C#/VB components carry a plain string property instead.
    ///
    /// IT LOOKS THE REFLECTED MEMBERS UP PER CALL, and that is a decision rather than an oversight. The
    /// first version cached the MethodInfo and PropertyInfo per Type, reasoning that the lookup is the
    /// expensive half of a reflected read and the fingerprint walks every object twice per apply. The
    /// measurement said otherwise: on a live canvas the whole read costs 0.00429 ms per script component
    /// and the type TEST on every object is the larger term - so the cache bought nothing anyone could
    /// see, and an unmeasured optimisation inside a guard is a liability rather than a saving (#523).
    /// </summary>
    /// <param name="obj">Any object. Typed as <c>object</c> so this can be driven headlessly - the
    /// reflection never needed the Grasshopper interface, only something to reflect over.</param>
    /// <param name="source">The source exactly as the component gave it, unnormalised - an empty string
    /// included. Null when nothing could be read.</param>
    /// <returns>True when a read actually succeeded, whatever it returned.</returns>
    public static bool TryRead(object obj, out string source)
    {
      source = null;
      if (obj == null) return false;
      var t = obj.GetType();

      // the Rhino 8 script components (C# / Python 3 / IronPython 2): TryGetSource(out string).
      try
      {
        var m = t.GetMethod("TryGetSource", new[] { typeof(string).MakeByRefType() });
        if (m != null)
        {
          var args = new object[] { null };
          if (m.Invoke(obj, args) as bool? == true && args[0] is string src) { source = src; return true; }
        }
      }
      catch { }

      // The legacy GhPython component (a plain settable Code property), and the older script components.
      // A property yielding an EMPTY string is a successful read, but it is not the best answer available:
      // the original walked past it to the next name, so that walk is preserved and an empty read is only
      // returned once nothing better has been found.
      var read = false;
      foreach (var name in SourceProperties)
      {
        try
        {
          var p = t.GetProperty(name);
          if (p == null || p.PropertyType != typeof(string) || !p.CanRead) continue;
          if (!(p.GetValue(obj) is string src)) continue;
          if (!string.IsNullOrWhiteSpace(src)) { source = src; return true; }
          if (!read) { source = src; read = true; }
        }
        catch { }
      }
      return read;
    }
  }
}
