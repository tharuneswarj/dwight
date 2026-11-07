using System;
using System.Linq;

namespace Dwight.Session
{
  /// <summary>
  /// Which store record a document owns (WP-1 step 2; ADR-067 as refined by the spike).
  ///
  /// THE SPIKE RETIRED THE EPHEMERAL ID. ADR-067 specified "unsaved documents use an ephemeral session id
  /// promoted to the DocumentID record on first save". Measurement showed there is nothing to promote: a
  /// brand-new in-memory <c>GH_Document</c> already carries a unique, non-empty <c>DocumentID</c> BEFORE any
  /// save, and that same id is what <c>Write</c> stamps into the .gh archive. So a record keyed at document
  /// birth is already correctly keyed after the first save, after a Save-As, and after every reopen - the
  /// promotion step was a mechanism for a problem that does not exist. This is a deliberate, ruled refinement
  /// of ADR-067, not a silent deviation.
  ///
  /// WHAT THE KEY DELIBERATELY IGNORES: the file path. That is the entire point of anchoring to the id - a
  /// move, a rename, a folder reorganisation, or a git branch checkout must not detach a Scaffold thread from
  /// its file. The path is kept only as a human-readable label on the record.
  ///
  /// WHAT THE KEY CANNOT DO: separate a Save-As (or an Explorer copy) from its original - they genuinely share
  /// one id, confirmed live. That is handled above this type, by the second-live-doc rule, not by re-keying.
  /// </summary>
  internal static class DocumentKey
  {
    /// <summary>Prefix for a record anchored on content rather than on a DocumentID, so the two kinds of key
    /// can never be confused for one another when reading the sessions folder.</summary>
    private const string FingerprintPrefix = "fp-";

    /// <summary>
    /// The record key for a document, or NULL when the document cannot be identified at all - and null means
    /// MEMORY-ONLY, which is the safe answer. An unidentifiable document must never be given some other
    /// document's history; losing a session is recoverable, attaching the wrong one is not.
    /// </summary>
    public static string For(Guid documentId, string fingerprint)
    {
      if (documentId != Guid.Empty) return documentId.ToString("D");

      // No anchor in the file (an older or foreign .gh). Fall back to content, so such documents are still
      // told apart from one another instead of all sharing a single Guid.Empty record.
      if (string.IsNullOrWhiteSpace(fingerprint)) return null;

      var safe = Sanitize(fingerprint);
      return safe.Length == 0 ? null : FingerprintPrefix + safe;
    }

    /// <summary>
    /// The key becomes a path component (<c>%APPDATA%\Dwight\sessions\{key}.json</c>), so it is filtered to a
    /// conservative alphabet rather than merely checked. The fingerprint is computed, not user-supplied, so
    /// this is defence in depth - but a value that reaches a file path is worth defending in depth.
    /// </summary>
    private static string Sanitize(string raw)
    {
      var kept = raw.Where(c => (c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'));
      return new string(kept.ToArray());
    }
  }
}
