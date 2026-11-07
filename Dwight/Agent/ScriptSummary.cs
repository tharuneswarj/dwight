using System;
using System.Collections.Generic;
using System.Linq;

namespace Dwight.Agent
{
  /// <summary>
  /// THE SUMMARY LINE A FOLDED SCRIPT CARD SHOWS (Brief AL). Written by the PLUGIN from the script record - language, line
  /// count, the ports by name, and whether the source touches the Rhino document - and never from the model's text, so a
  /// folded card cannot say anything about the code that the code does not say about itself.
  ///
  /// "Touches the Rhino document" is a PLAIN TEXT MATCH for five markers and nothing more. It is not an analysis: a script that
  /// reaches the document some other way reads "no", and a comment that mentions one reads "yes". The summary's tooltip
  /// says so, and a test pins the markers, so nobody later mistakes the line for a guarantee. What protects the user is the
  /// card above it, which holds the COMPLETE source, folded or not (#48).
  ///
  /// GH-free (ADR-091).
  /// </summary>
  internal static class ScriptSummary
  {
    /// <summary>The five markers, matched as plain case-sensitive text.</summary>
    public static readonly string[] DocumentMarkers =
    {
      "Rhino.RhinoDoc", "scriptcontext.doc", "RhinoDocument", "doc.Objects", "Rhino.Runtime",
    };

    public const string TouchesLabel = "Touches the Rhino document";

    /// <summary>Said on the summary as a tooltip, so the line is never read as more than it is.</summary>
    public static string TextTestNote =>
      "Touches the Rhino document is a plain text match for " + string.Join(", ", DocumentMarkers.Take(DocumentMarkers.Length - 1)) +
      " and " + DocumentMarkers[DocumentMarkers.Length - 1] + " in the source. It is not an analysis of what the code does.";

    public static bool TouchesDocument(string source)
    {
      if (string.IsNullOrEmpty(source)) return false;
      foreach (var m in DocumentMarkers)
        if (source.IndexOf(m, StringComparison.Ordinal) >= 0) return true;
      return false;
    }

    /// <summary>Lines in the source as the card shows it: a trailing line break does not make another line, and an empty
    /// source has none.</summary>
    /// <summary>"1 line", "84 lines".</summary>
    public static string LineCountText(string source)
    {
      var n = LineCount(source);
      return n + (n == 1 ? " line" : " lines");
    }

    public static int LineCount(string source)
    {
      if (string.IsNullOrEmpty(source)) return 0;
      var s = source.Replace("\r\n", "\n").Replace('\r', '\n');
      if (s.EndsWith("\n", StringComparison.Ordinal)) s = s.Substring(0, s.Length - 1);
      return s.Split('\n').Length;
    }

    /// <summary>The language in the user's words, for a stored language name.</summary>
    public static string LanguageLabel(string language)
    {
      switch ((language ?? "").ToLowerInvariant())
      {
        case "python": return "Python";
        case "csharp": return "C#";
        case "vb": return "VB";
        case "ironpython": return "IronPython";
        default: return "Script";
      }
    }

    private static string Names(IReadOnlyList<string> ports) =>
      ports == null || ports.Count == 0 ? "none" : string.Join(", ", ports);

    /// <summary>"Python 3, 84 lines. Touches the Rhino document: yes." - for a script that is not a component and so declares no ports
    /// (the run_script card).</summary>
    public static string LineWithoutPorts(string languageLabel, string source)
    {
      var lines = LineCount(source);
      return languageLabel + ", " + lines + (lines == 1 ? " line" : " lines") + ". " +
             TouchesLabel + ": " + (TouchesDocument(source) ? "yes" : "no") + ".";
    }

    /// <summary>
    /// "Python, 84 lines. Inputs: gcode (list), temp (item). Outputs: Pts. Touches the Rhino document: no." For an edit, whose record
    /// carries only the ports it ADDS, the port clauses say "Adds inputs" and "Adds outputs".
    ///
    /// Brief CP: EACH INPUT'S ACCESS IS SAID, the default included, because it decides how many times the code runs and a person can see it wrongly here before approving.
    /// </summary>
    public static string Line(string languageLabel, string source, IReadOnlyList<string> inputs, IReadOnlyList<string> outputs,
                              bool portsAreAdditions = false, IReadOnlyDictionary<string, string> inputAccess = null)
    {
      var lines = LineCount(source);
      var inLabel = portsAreAdditions ? "Adds inputs" : "Inputs";
      var outLabel = portsAreAdditions ? "Adds outputs" : "Outputs";
      return languageLabel + ", " + lines + (lines == 1 ? " line" : " lines") + ". " +
             inLabel + ": " + Names(PortAccess.Shown(inputs, inputAccess)) + ". " + outLabel + ": " + Names(outputs) + ". " +
             TouchesLabel + ": " + (TouchesDocument(source) ? "yes" : "no") + ".";
    }
  }
}
