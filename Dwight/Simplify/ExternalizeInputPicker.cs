using System;
using System.Collections.Generic;

namespace Dwight.Simplify
{
  /// <summary>RR8 #15 (fix A), the GH-free decision at the heart of externalize resolution (ADR-091): given the
  /// candidate input levers already filtered by the live reader (a component's UNWIRED inputs that carry a readable
  /// internalized numeric value) and the model's nominated param name, pick ONE - or signal a drop. The model's
  /// name is a PREFERENCE, never a requirement: it only picks among candidates; an absent or unmatched name falls
  /// back to the SOLE candidate. Zero candidates, or several with no name match (ambiguous), returns -1 so the
  /// caller drops the whole change-group honestly (fix B) rather than externalize the wrong input.</summary>
  internal static class ExternalizeInputPicker
  {
    /// <summary>Each candidate is its identifying strings (label / name / nickname) as the reader saw them.</summary>
    public static int Pick(IReadOnlyList<(string Label, string Name, string Nick)> candidates, string paramName)
    {
      if (candidates == null || candidates.Count == 0) return -1;
      if (!string.IsNullOrWhiteSpace(paramName))
        for (var i = 0; i < candidates.Count; i++)
        {
          var c = candidates[i];
          if (Eq(c.Label, paramName) || Eq(c.Name, paramName) || Eq(c.Nick, paramName)) return i;
        }
      return candidates.Count == 1 ? 0 : -1;   // sole lever, or ambiguous -> caller skips
    }

    private static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
  }
}
