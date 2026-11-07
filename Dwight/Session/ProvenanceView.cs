using System;

namespace Dwight.Session
{
  /// <summary>
  /// The provenance-aware dock filter (WP-1 #179, Option C). One DocumentID record can hold commands from a
  /// file AND its Save-As copies (they share the file-borne DocumentID - the spike proved Save-As inherits it),
  /// so every command carries an ORIGIN path stamp and the dock VIEW is filtered by it. The disk layer is
  /// untouched: one record, the union of origins, atomic writes (#163/#166 stand). This is reversible display
  /// logic, and it is the whole decision - GH-free and unit-tested, so the rule is locked, not a review habit.
  ///
  /// The two regimes (#179(3)):
  ///   - 2+ same-id documents LIVE  -> each doc's dock shows only its OWN rows (no live interleave between a
  ///     clean file and its messy copy). Foreign rows are hidden, never annotated.
  ///   - one document open          -> the FULL lineage shows, and a row from another origin is FOREIGN,
  ///     carrying an honest "from &lt;name&gt;" label (the #166 divergence-annotation pattern, as provenance).
  /// </summary>
  internal static class ProvenanceView
  {
    /// <summary>Decide a row's visibility and whether it is foreign to the active document.
    /// <paramref name="multipleLiveSiblings"/> is true when 2+ open documents share this DocumentID.</summary>
    public static (bool show, bool foreign) Decide(string rowOrigin, string myOrigin, bool multipleLiveSiblings)
    {
      bool own = SameOrigin(rowOrigin, myOrigin);
      if (multipleLiveSiblings) return (own, false);   // live: own rows only, no interleave, no annotation
      return (true, !own);                             // single: full lineage, foreign rows annotated
    }

    /// <summary>Origins are file paths, compared case-insensitively (Windows paths). A null and "" are the same
    /// origin - the unsaved state before a first save re-stamps them.</summary>
    public static bool SameOrigin(string a, string b) =>
      string.Equals(a ?? "", b ?? "", StringComparison.OrdinalIgnoreCase);
  }
}
