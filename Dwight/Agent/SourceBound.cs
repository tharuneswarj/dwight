using System;
using System.Collections.Generic;

namespace Dwight.Agent
{
  /// <summary>
  /// THE AGENT'S BOUND ON A SCRIPT COMPONENT'S SOURCE (Phase-9 U1, reads-unification, #382(9)).
  ///
  /// The verbs' reader (<c>ScriptSourceReader.TryRead</c>) treats an oversized script as OPAQUE, and that
  /// is right for to-script: it must reproduce the whole behaviour or refuse. The agent is a READER, not a
  /// reproducer, so it takes a bounded PREFIX and says exactly what it left out - the ChatBudget discipline
  /// (truncate with a visible marker and the totals, never drop). Both consumers read through the SAME
  /// reader; only the bound differs, and each bound is stated where it applies.
  ///
  /// The marker is a COMMENT in the script's own language: inert where it sits, visible where the cut
  /// happens, and never a line the model might "fix". Cuts land on line boundaries only - a half statement
  /// is worse than one fewer statement.
  ///
  /// GH-free (ADR-091): strings in, a <see cref="SourceDto"/> out.
  /// </summary>
  internal static class SourceBound
  {
    public static SourceDto Trim(string src, string language, int maxLines, int maxChars)
    {
      if (string.IsNullOrWhiteSpace(src)) return null;
      var s = src.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
      if (s.Length == 0) return null;

      var lines = s.Split('\n');
      var dto = new SourceDto { Language = language, TotalLines = lines.Length, TotalChars = s.Length };

      if (maxLines <= 0) maxLines = 1;
      if (maxChars <= 0) maxChars = 1;

      var kept = new List<string>();
      var chars = 0;
      foreach (var line in lines)
      {
        if (kept.Count >= maxLines) break;
        var add = line.Length + (kept.Count > 0 ? 1 : 0);
        if (chars + add > maxChars && kept.Count > 0) break;   // the first line always survives, whole
        kept.Add(line);
        chars += add;
      }

      if (kept.Count == lines.Length)
      {
        dto.Text = s;
        return dto;
      }

      var omitted = lines.Length - kept.Count;
      dto.Truncated = true;
      dto.Omitted = omitted + " more line" + (omitted == 1 ? "" : "s") + " not shown; " + s.Length + " chars in total";
      dto.Text = string.Join("\n", kept) + "\n" + Comment(language) + " [Dwight cut this here: " + dto.Omitted + "]";
      return dto;
    }

    /// <summary>
    /// The script language, read off the component's TYPE NAME. The Rhino 8 script components live in
    /// <c>RhinoCodePluginGH</c>, which is a plugin and not a referenceable SDK, so this is a string rule -
    /// tested here and confirmed against the loaded assembly by the deploy probe, never trusted from memory
    /// (#137). An unrecognised script type is "script": still a script, honestly unlabelled.
    /// </summary>
    public static string LanguageOf(string typeFullName)
    {
      var n = typeFullName ?? "";
      if (n.StartsWith("RhinoCodePluginGH.", StringComparison.Ordinal))
      {
        if (Has(n, "CSharp")) return "csharp";
        if (Has(n, "IronPython")) return "ironpython";
        if (Has(n, "Python")) return "python";
        return "script";
      }
      if (n.StartsWith("GhPython.", StringComparison.Ordinal)) return "ironpython";   // the legacy GhPython component is IronPython 2
      if (n.StartsWith("ScriptComponents.", StringComparison.Ordinal))
      {
        if (Has(n, "CSNET")) return "csharp";
        if (Has(n, "VBNET")) return "vb";
        return "script";
      }
      return "script";
    }

    private static bool Has(string s, string token) => s.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;

    private static string Comment(string language)
    {
      switch ((language ?? "").ToLowerInvariant())
      {
        case "python":
        case "ironpython": return "#";
        case "vb": return "'";
        default: return "//";
      }
    }
  }
}
