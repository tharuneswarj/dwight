using System.Collections.Generic;
using System.Linq;

namespace Dwight.Data
{
  /// <summary>
  /// HOW A COMPONENT'S SOLVE TIME IS ROUNDED BEFORE ANY MODEL SEES IT - one function, used by every surface that
  /// sends one (Lane C, U23).
  ///
  /// IT WAS INLINE IN SIMPLIFY'S PAYLOAD UNTIL U23, and extracting it is the point rather than a tidy-up. Simplify
  /// has sent this value since Phase 7 as its hotspot signal (<c>nodes[].ms</c>), and U23's solve-timing read sends
  /// the same fact about the same components. Two roundings of one number would show a user two answers to one
  /// question - and they WILL compare Simplify's hotspot against the timing read, because both describe the same
  /// slow component. So there is one rounding, here, and both callers use it.
  ///
  /// THE ROUNDING IS COARSE ON PURPOSE, and it is kept exactly as Phase 7 measured it. A component's processor
  /// time varies from one run to the next by more than a millisecond, so a 1 ms figure would present timer noise
  /// as a measurement. Five-millisecond buckets say "this is slow" without claiming a precision the reading does
  /// not have.
  ///
  /// GH-free (ADR-091): an integer in, an integer out.
  /// </summary>
  internal static class SolveTimeBucket
  {
    /// <summary>
    /// A whole-millisecond reading, rounded to the nearest 5 ms. The arithmetic is Simplify's, unchanged:
    /// 0, 1 and 2 round to 0; 3 to 7 round to 5; 8 to 12 round to 10.
    /// </summary>
    public static int Of(int ms) => ((ms + 2) / 5) * 5;

    /// <summary>
    /// THE LARGEST WHOLE-MILLISECOND READING THAT ROUNDS TO ZERO - derived from <see cref="Of"/> rather than
    /// written down beside it, and pinned by a test.
    ///
    /// It exists because a report has to SAY what a zero bucket means, and a sentence that states a threshold
    /// by hand goes stale the day the rounding changes (#777: the words and the mapping have one source, or the
    /// mapping lapses silently). The timing read's wording is built from this.
    /// </summary>
    public static int LargestZeroReading
    {
      get
      {
        var ms = 0;
        while (Of(ms + 1) == 0) ms++;
        return ms;
      }
    }

    /// <summary>At most this many components are named as hotspots in Simplify's payload - Phase 7's number,
    /// unchanged.</summary>
    public const int HotspotCap = 8;

    /// <summary>
    /// SIMPLIFY'S HOTSPOTS: which of the selection's components are sent with a time, and the time sent - keyed by
    /// component id. Readings are (id, whole ms) pairs in the order the payload walks them.
    ///
    /// THE GUARD TESTS THE VALUE THAT IS SENT (#875). It used to be <c>ms &gt;= 2</c> on the raw reading while the
    /// payload sent the BUCKET, and 2 ms rounds to 0 - so a component could go out as a hotspot with <c>"ms": 0</c>,
    /// named as slow by a figure that says it is not. A threshold beside the rounding is a second definition of
    /// "measurable" that can drift from the first; testing the bucket itself cannot. So a component whose time
    /// rounds to zero is never a hotspot, whatever the rounding becomes.
    ///
    /// The selection is HERE rather than inline in the payload builder so the guard is the one the test drives:
    /// a test of a function beside the builder stays green when the builder is reverted (#779).
    /// </summary>
    public static Dictionary<int, int> Hotspots(IEnumerable<KeyValuePair<int, int>> readings)
    {
      // A dictionary, as Phase 7's was: an id read twice keeps one entry (and cannot throw in ToDictionary below).
      var sent = new Dictionary<int, int>();
      foreach (var r in readings ?? Enumerable.Empty<KeyValuePair<int, int>>())
      {
        var bucket = Of(r.Value);
        if (bucket > 0) sent[r.Key] = bucket;
      }

      // Slowest first; OrderByDescending is stable, so ties keep the payload's own order, as Phase 7's did.
      return sent.OrderByDescending(kv => kv.Value).Take(HotspotCap).ToDictionary(kv => kv.Key, kv => kv.Value);
    }
  }
}
