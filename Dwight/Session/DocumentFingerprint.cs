using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Dwight.Session
{
  /// <summary>
  /// A stable, order-independent hash of a document's structural content (WP-1, ADR-067).
  ///
  /// THE SPIKE CHANGED THIS TYPE'S JOB. ADR-067 named a content fingerprint as the FALLBACK MATCHER for
  /// document identity, in case <c>DocumentID</c> proved unstable. It did not: the id is stamped in the .gh
  /// archive, exists at document birth, survives Write/Read, and nothing in Grasshopper re-keys it outside
  /// cluster internals. So the fingerprint is not a fallback that never runs - it has three real jobs:
  ///
  ///   1. THE Guid.Empty ANCHOR - a .gh with no DocumentID header (an untested edge, handled defensively)
  ///      would otherwise collide every such file onto one shared record.
  ///   2. DIVERGENCE DETECTION - has this document's content moved away from what the record was written
  ///      against? A Save-As inherits the id, so an id match alone does not mean the same content.
  ///   3. COLLISION DISAMBIGUATION (held in reserve) - an original, its Save-As, and every Explorer copy
  ///      genuinely share one id, and Grasshopper will hold two of them open at once.
  ///
  /// GH-free by construction (ADR-091): it takes tokens, not a <c>GH_Document</c>, so the hashing law is
  /// unit-tested headlessly and the live read stays a thin shim. The discipline mirrors
  /// <see cref="Dwight.Labelling.LabelCache.Hash"/>, which has held since Phase 6.
  /// </summary>
  internal static class DocumentFingerprint
  {
    /// <summary>
    /// Hash a document's content tokens. Tokens are sorted ordinally before hashing, so the result is
    /// independent of enumeration order - the same document always hashes the same, however it was walked.
    ///
    /// DUPLICATES ARE SIGNIFICANT: the tokens are sorted, NOT de-duplicated, because two Loft components are
    /// not one Loft component. A set-based hash would erase exactly the change we want to detect.
    ///
    /// Null or empty input yields a stable non-null hash (the hash of nothing), never null and never a throw:
    /// this feeds a file NAME, so an empty string here would be a path bug rather than a degraded cache.
    /// </summary>
    public static string Compute(IEnumerable<string> contentTokens)
    {
      var tokens = (contentTokens ?? Enumerable.Empty<string>())
        .Select(t => t ?? "")
        .OrderBy(t => t, StringComparer.Ordinal);

      var canonical = string.Join("|", tokens);
      using (var sha = SHA256.Create())
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(canonical)));
    }
  }
}
