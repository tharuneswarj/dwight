using System;
using System.Collections.Generic;
using System.Linq;

namespace Dwight.Simplify
{
  /// <summary>
  /// Layer 2 of the Simplify verification stack (ruling G.2): compare the selection's OUTPUT data before vs
  /// after, as an order-independent MULTISET of (tree signature + value digest) samples. No old-&gt;new
  /// correspondence is assumed - if the replacement produces the same BAG of output data, behaviour-as-
  /// observable-data is preserved no matter which param now carries what; a legitimate merge of two outputs into
  /// one makes the bags differ, so it reverts and reports honestly (the correct conservative v1 outcome).
  ///
  /// For a selection with wired downstream consumers the samples are captured at those external inputs; for a
  /// terminal selection (outputs feeding nothing) they are captured over every terminal output param - which is
  /// why the multiset, not a mapping, is the right tool. The compare is pure; the GH shim produces the samples.
  /// </summary>
  internal readonly struct OutputSample
  {
    public readonly string Tree;
    public readonly string Value;
    /// <summary>Informational only (the owning component's nickname/type) so a revert can NAME the mismatched
    /// outputs (G.2). NOT part of the equivalence token.</summary>
    public readonly string Label;
    public OutputSample(string tree, string value, string label = null) { Tree = tree; Value = value; Label = label; }
  }

  internal static class OutputEquivalence
  {
    /// <summary>True iff <paramref name="before"/> and <paramref name="after"/> are the same multiset of
    /// output samples (order-independent).</summary>
    public static bool Equivalent(IReadOnlyList<OutputSample> before, IReadOnlyList<OutputSample> after)
    {
      before = before ?? Array.Empty<OutputSample>();
      after = after ?? Array.Empty<OutputSample>();
      if (before.Count != after.Count) return false;   // a merged / dropped output changes the bag size
      var a = before.Select(Token).OrderBy(x => x, StringComparer.Ordinal);
      var b = after.Select(Token).OrderBy(x => x, StringComparer.Ordinal);
      return a.SequenceEqual(b, StringComparer.Ordinal);   // sorted token lists equal <=> same multiset
    }

    /// <summary>
    /// WP-4 (to-script): the PER-OUTPUT compare, which Simplify cannot use and to-script must. A Simplify
    /// rewrite has no old-&gt;new output correspondence (which is why layer 2 is a multiset), but a script
    /// component's output j IS the selection's boundary output j - both derived from the SAME canonical
    /// <see cref="Dwight.Data.SimplifyBoundary"/> ordering. That correspondence is what lets a mismatch be
    /// reported PER OUTPUT ("Site Curves matched; Panel Areas did not") instead of as an opaque "the results
    /// differ".
    ///
    /// Returns the indices whose (tree + value) digest differs. A length mismatch is a programming error, not a
    /// user-facing state (the arity is plugin-computed from the boundary contract), so it reports every index
    /// rather than silently zipping to the shorter list.
    /// </summary>
    public static List<int> Mismatches(IReadOnlyList<OutputSample> before, IReadOnlyList<OutputSample> after)
    {
      before = before ?? Array.Empty<OutputSample>();
      after = after ?? Array.Empty<OutputSample>();
      if (before.Count != after.Count) return Enumerable.Range(0, Math.Max(before.Count, after.Count)).ToList();
      var bad = new List<int>();
      for (var i = 0; i < before.Count; i++)
        if (!string.Equals(Token(before[i]), Token(after[i]), StringComparison.Ordinal)) bad.Add(i);
      return bad;
    }

    // The unit separator (U+001F) cannot appear in a tree signature or a value digest, so the join is unambiguous.
    private static string Token(OutputSample s) => (s.Tree ?? "") + "\u001F" + (s.Value ?? "");
  }
}
