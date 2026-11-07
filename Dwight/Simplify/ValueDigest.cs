using System.Collections.Generic;
using System.Text;

namespace Dwight.Simplify
{
  /// <summary>
  /// The digest of a boundary output's actual VALUES - the value half of a layer-2 output sample (the tree
  /// signature is the structure half). It answers "does the rewritten graph still compute the same thing?", and it
  /// is the LAST line before the originals are deleted.
  ///
  /// RR18 (second finding, Tharun's list question): this digest used to be SAMPLED - the first 24 branches, the
  /// first 8 items of each. That is a FALSE-POSITIVE hole, and the worst one in the system: a script whose 100-item
  /// output differed at item 20 matched on its first 8 items and its count, digested EQUAL, passed verification,
  /// and the user's components were deleted in favour of a silently wrong script. A truncation MARKER does not
  /// help - two truncated digests with matching prefixes still compare equal, which IS the bug.
  ///
  /// So the comparison surface is now UNCAPPED - every branch, every item - via a ROLLING hash: coverage is
  /// complete while memory stays O(1) and the token stays fixed-size. This is cheap by construction: the surface is
  /// only the boundary output params, and the solve that produced them already paid O(n).
  ///
  /// Pure and headless (strings in, digest out); the GH shim streams the live VolatileData through the
  /// geometry-aware <c>GooDigest.Item</c>. Compared only within one apply (before vs after, same process), so the
  /// hash needs no cross-process stability - only determinism across a re-solve, which the solve guarantees.
  ///
  /// (The FINGERPRINT's persistent-value digest keeps its cap, deliberately: it is a structural tripwire running
  /// canvas-wide on a moves-only Reorganise, not an equivalence proof. That bound is a known limit, recorded.)
  /// </summary>
  internal static class ValueDigest
  {
    /// <summary>Compose the digest over EVERY branch and EVERY item, in the caller's deterministic path order.
    /// Each branch contributes its item count and each of its item digests; nothing is skipped.</summary>
    public static string Compose(int pathCount, IEnumerable<(int Count, IReadOnlyList<string> Items)> branches)
    {
      var h = new Rolling();
      h.Int(pathCount);
      foreach (var br in branches ?? System.Array.Empty<(int, IReadOnlyList<string>)>())
      {
        h.Mark('|');
        h.Int(br.Count);                       // an item count change is a difference even if the items match
        var items = br.Items;
        if (items == null) continue;
        for (var k = 0; k < items.Count; k++)  // EVERY item - no cap, no sample, no truncation marker
        {
          h.Mark(',');
          h.Text(items[k] ?? "");
        }
      }
      return h.Value;
    }

    /// <summary>Compose over a streaming sequence, so a huge output never has to be materialised as lists.</summary>
    public static string Compose(int pathCount, IEnumerable<(int Count, IEnumerable<string> Items)> branches)
    {
      var h = new Rolling();
      h.Int(pathCount);
      foreach (var br in branches ?? System.Array.Empty<(int, IEnumerable<string>)>())
      {
        h.Mark('|');
        h.Int(br.Count);
        if (br.Items == null) continue;
        foreach (var item in br.Items)
        {
          h.Mark(',');
          h.Text(item ?? "");
        }
      }
      return h.Value;
    }

    /// <summary>A rolling FNV-1a 64: O(1) memory over an unbounded stream, fixed-size token out. Position-sensitive
    /// by construction (the separators are hashed too), so the same items in a different ORDER digest differently -
    /// order is behaviour in a Grasshopper list.</summary>
    private struct Rolling
    {
      private ulong _h;
      private bool _init;

      private void Ensure() { if (!_init) { _h = 14695981039346656037UL; _init = true; } }
      private void Byte(byte b) { Ensure(); _h ^= b; _h *= 1099511628211UL; }

      public void Mark(char c) => Byte((byte)c);
      public void Int(int n) { for (var i = 0; i < 4; i++) Byte((byte)(n >> (i * 8))); }
      public void Text(string s) { foreach (var b in Encoding.UTF8.GetBytes(s)) Byte(b); }

      public string Value { get { Ensure(); return "v:" + _h; } }
    }
  }
}
