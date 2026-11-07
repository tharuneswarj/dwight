using System;
using System.Collections.Generic;
using System.Linq;

namespace Dwight.Segment
{
  /// <summary>
  /// The benchmark scorer (Phase 6 WP-B). Compares the segmenter's macro partition against a human's
  /// hand-chunking, two ways: Adjusted Rand Index (pairwise co-membership, corrected for chance - the
  /// headline) and boundary agreement (do the segmenter's cut-edges match the human's - the secondary read).
  ///
  /// The honesty guard Tharun required is structural in <see cref="Summarise"/>: it NEVER returns one
  /// averaged number. It returns clean-file agreement and messy-file agreement separately, because a
  /// segmenter that scores high on tidy files and low on messy ones is echoing the author's layout, and a
  /// single average would hide exactly that. The messy-file number is the one that matters. Headless and
  /// pure, so it is unit-tested.
  /// </summary>
  internal static class SegmentScorer
  {
    /// <summary>Adjusted Rand Index over the nodes common to both partitions. 1.0 = identical (up to
    /// relabelling), ~0 = no better than chance, can go slightly negative. Label-invariant.</summary>
    public static double AdjustedRandIndex(IReadOnlyDictionary<int, int> a, IReadOnlyDictionary<int, int> b)
    {
      var common = a.Keys.Where(b.ContainsKey).ToList();
      var n = common.Count;
      if (n == 0) return 1.0;

      // contingency table n_ij = |A-cluster i AND B-cluster j|
      var table = new Dictionary<(int, int), int>();
      var aTot = new Dictionary<int, int>();
      var bTot = new Dictionary<int, int>();
      foreach (var id in common)
      {
        var key = (a[id], b[id]);
        table.TryGetValue(key, out var c); table[key] = c + 1;
        aTot.TryGetValue(a[id], out var ca); aTot[a[id]] = ca + 1;
        bTot.TryGetValue(b[id], out var cb); bTot[b[id]] = cb + 1;
      }

      double sumComb = table.Values.Sum(Comb2);
      double sumA = aTot.Values.Sum(Comb2);
      double sumB = bTot.Values.Sum(Comb2);
      double totalComb = Comb2(n);

      var expected = totalComb > 0 ? sumA * sumB / totalComb : 0.0;
      var max = 0.5 * (sumA + sumB);
      var denom = max - expected;
      if (Math.Abs(denom) < 1e-12) return 1.0;   // degenerate (e.g. both all-in-one) -> treat as agreement
      return (sumComb - expected) / denom;
    }

    /// <summary>Boundary agreement over the WIRE edges: a wire is "cut" when its endpoints fall in different
    /// clusters. Returns precision/recall/F1 treating the human's cuts as truth, so a segmenter that merges
    /// across a boundary the human cut shows up as low recall, and one that cuts where the human did not as
    /// low precision.</summary>
    public static (double precision, double recall, double f1) BoundaryAgreement(
      IReadOnlyDictionary<int, int> seg, IReadOnlyDictionary<int, int> human, IEnumerable<(int from, int to)> wireEdges)
    {
      int tp = 0, fp = 0, fn = 0;
      foreach (var (u, v) in wireEdges)
      {
        if (!seg.ContainsKey(u) || !seg.ContainsKey(v) || !human.ContainsKey(u) || !human.ContainsKey(v)) continue;
        var segCut = seg[u] != seg[v];
        var humanCut = human[u] != human[v];
        if (segCut && humanCut) tp++;
        else if (segCut) fp++;
        else if (humanCut) fn++;
      }
      var precision = tp + fp > 0 ? (double)tp / (tp + fp) : 1.0;
      var recall = tp + fn > 0 ? (double)tp / (tp + fn) : 1.0;
      var f1 = precision + recall > 0 ? 2 * precision * recall / (precision + recall) : 0.0;
      return (precision, recall, f1);
    }

    /// <summary>One file's score, tagged clean or messy (messy = the author's layout does not match the
    /// logical grouping - the files that actually test logic-recovery vs position-echo).</summary>
    public readonly struct FileScore
    {
      public readonly string File;
      public readonly bool Messy;
      public readonly double Ari;
      public readonly double BoundaryF1;
      public FileScore(string file, bool messy, double ari, double boundaryF1)
      { File = file; Messy = messy; Ari = ari; BoundaryF1 = boundaryF1; }
    }

    /// <summary>The two-number summary - clean and messy reported SEPARATELY, never averaged together. The
    /// messy number is the headline: it is the one that tells us whether adjacency recovered intent or just
    /// flattered the author's layout.</summary>
    public readonly struct Summary
    {
      public readonly int CleanCount, MessyCount;
      public readonly double CleanAri, MessyAri, CleanBoundaryF1, MessyBoundaryF1;
      public Summary(int cleanCount, int messyCount, double cleanAri, double messyAri, double cleanBoundaryF1, double messyBoundaryF1)
      { CleanCount = cleanCount; MessyCount = messyCount; CleanAri = cleanAri; MessyAri = messyAri; CleanBoundaryF1 = cleanBoundaryF1; MessyBoundaryF1 = messyBoundaryF1; }
    }

    public static Summary Summarise(IEnumerable<FileScore> scores)
    {
      var list = scores.ToList();
      var clean = list.Where(s => !s.Messy).ToList();
      var messy = list.Where(s => s.Messy).ToList();
      double Avg(IEnumerable<double> xs) { var a = xs.ToList(); return a.Count == 0 ? double.NaN : a.Average(); }
      return new Summary(
        clean.Count, messy.Count,
        Avg(clean.Select(s => s.Ari)), Avg(messy.Select(s => s.Ari)),
        Avg(clean.Select(s => s.BoundaryF1)), Avg(messy.Select(s => s.BoundaryF1)));
    }

    private static double Comb2(int x) => x < 2 ? 0.0 : (long)x * (x - 1) / 2.0;   // WP-4.7: long, or x*(x-1) overflows int on a big partition
  }
}
