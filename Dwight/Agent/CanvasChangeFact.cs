using System;
using System.Collections.Generic;
using System.Linq;

namespace Dwight.Agent
{
  /// <summary>The canvas as Dwight left it, or as it is now: a state stamp and the scaffold addresses on it.</summary>
  internal sealed class CanvasReading
  {
    /// <summary>The canvas stamp (behaviour fingerprint plus pivot digest). Null or empty means it could
    /// not be read, which is never treated as "unchanged".</summary>
    public string Stamp { get; set; }

    /// <summary>The scaffold region addresses on the canvas at that moment.</summary>
    public IReadOnlyList<string> Regions { get; set; } = new string[0];

    /// <summary>#660: components inside each scaffold region (the region's scribbles excluded), by address. Null
    /// means the counts were not read, which is never compared against as zero.</summary>
    public IReadOnlyDictionary<string, int> Counts { get; set; }

    /// <summary>
    /// The counts by address. An address carried by MORE THAN ONE group is not one region, so it gets no count at
    /// all - never a sum, and never whichever group was read last. Identity decides what a count is of (#208).
    /// </summary>
    public static IReadOnlyDictionary<string, int> CountsByAddress(IEnumerable<KeyValuePair<string, int>> regions)
    {
      var counts = new Dictionary<string, int>(StringComparer.Ordinal);
      var ambiguous = new HashSet<string>(StringComparer.Ordinal);
      foreach (var r in regions ?? Enumerable.Empty<KeyValuePair<string, int>>())
      {
        if (string.IsNullOrWhiteSpace(r.Key) || ambiguous.Contains(r.Key)) continue;
        if (counts.Remove(r.Key)) { ambiguous.Add(r.Key); continue; }
        counts[r.Key] = r.Value;
      }
      return counts;
    }
  }

  /// <summary>One object's contribution to the canvas stamp beyond the behaviour fingerprint: where it sits (null when the
  /// pivot could not be read) and its whole held state (<see cref="StateToken"/>), null for a type that holds none.</summary>
  internal sealed class CanvasStampPart
  {
    public Guid Guid { get; set; }
    public int? X { get; set; }
    public int? Y { get; set; }
    public string State { get; set; }
  }

  /// <summary>
  /// THE CANVAS STAMP, composed GH-free (#680): the behaviour fingerprint, plus a digest of every object's position and
  /// WHOLE held state.
  ///
  /// The stamp answers "has anything on the canvas changed?" - for the #573 canvas fact and for the loop-breaker. The
  /// fingerprint alone is value-only for a slider (right for ITS question, behaviour), so a domain- or rounding-only change
  /// used to leave the stamp byte-identical: the canvas fact stayed silent, and the breaker would read "nothing changed"
  /// after a change that landed. The state is read here, beside the positions, and the fingerprint is not touched.
  /// </summary>
  internal static class CanvasStampCore
  {
    public static string Compose(string fingerprint, IEnumerable<CanvasStampPart> parts)
    {
      var places = new System.Text.StringBuilder();
      foreach (var p in (parts ?? Enumerable.Empty<CanvasStampPart>()).Where(p => p != null).OrderBy(p => p.Guid))
      {
        places.Append(p.Guid.ToString("N"));
        if (p.X.HasValue && p.Y.HasValue) places.Append('@').Append(p.X.Value).Append(',').Append(p.Y.Value);
        if (p.State != null) places.Append('#').Append(p.State);   // #680: what it holds, not only what it computes
        places.Append(';');
      }

      using (var sha = System.Security.Cryptography.SHA256.Create())
        return (fingerprint ?? "") + "|" + Convert.ToHexString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(places.ToString())));
    }
  }

  /// <summary>
  /// WHAT THE MODEL IS TOLD, AT THE START OF A TURN, ABOUT THE CANVAS HAVING CHANGED (#573).
  ///
  /// THE MISS. After Tharun undid the scaffold map, the agent said "Same as before - the map hasn't
  /// changed. All six regions exist on the canvas", and on challenge: "I answered from the plan I
  /// remembered proposing, without re-checking the canvas each time." F4 had already fixed the map
  /// payload's SHAPE so an undone region could not be listed as present - and it did not matter, because
  /// this time the model never called the tool. It assumed the canvas cannot change between two of its
  /// own turns.
  ///
  /// WHY A FACT AND NOT A SENTENCE IN THE PROMPT. The prompt already says "you cannot see the canvas; you
  /// have tools that read it, and you use them before you answer", and the model answered from memory
  /// anyway. An instruction the model must remember to follow does not bind. A fact the plugin states does
  /// not need remembering - ADR-056 applied to the model's belief about the world.
  ///
  /// AND THE FACT CARRIES THE REGION DELTA, not only "the canvas changed". "The canvas changed" is still an
  /// instruction the model has to choose to act on. "Scaffold regions at the end of your last turn: A1 to
  /// A6. Now: none." is a fact it cannot misremember, and it is exactly the miss.
  ///
  /// THREE STATES, AND ONLY TWO OF THEM SAY ANYTHING:
  ///   - UNCHANGED: silence. A line that appears every turn trains the reader to skip it, and the day it
  ///     matters it is skipped.
  ///   - CHANGED: the fact, with the region delta when the regions moved.
  ///   - NOT KNOWN: the conversation has earlier turns but Dwight holds no reading from them - after a
  ///     restart, or when the canvas could not be read. Said as what Dwight knows, not as what happened.
  ///
  /// IT NEVER SAYS WHO CHANGED THE CANVAS. Dwight knows that it changed, not why - the user, an undo, or an
  /// approved Reorganise are all the same fact from here, and guessing would be the invented-cause class
  /// F2 exists to remove.
  ///
  /// GH-free, so the sentence the model is given is testable (ADR-091). The readings are taken by the
  /// panel, at two sites - a turn's completion AND a tidy's completion, because the tidy is scheduled
  /// asynchronously and finishes after the turn; a reading taken only at turn end would come before
  /// Dwight's own layout moves and tell the next turn "the canvas changed" about Dwight's own tidy.
  /// </summary>
  internal static class CanvasChangeFact
  {
    /// <summary>How many addresses to list before counting the rest (ADR-035).</summary>
    public const int MaxListed = 12;

    /// <summary>
    /// The sentence for the start of a turn, or null when nothing should be said.
    /// </summary>
    /// <param name="last">The canvas as Dwight last left it in this conversation, or null if Dwight holds
    /// no reading.</param>
    /// <param name="now">The canvas as it is at the start of this turn.</param>
    /// <param name="conversationHasEarlierTurns">A first turn has nothing it could be stale about.</param>
    public static string For(CanvasReading last, CanvasReading now, bool conversationHasEarlierTurns)
    {
      if (!conversationHasEarlierTurns) return null;

      if (last == null || string.IsNullOrEmpty(last.Stamp) || now == null || string.IsNullOrEmpty(now.Stamp))
        return "CANVAS: Dwight cannot tell whether the canvas has changed since this conversation last " +
               "ran - its record of the canvas does not survive a restart. Anything said about the canvas " +
               "earlier in this conversation may no longer be true. Read it with your tools before " +
               "describing it.";

      if (string.Equals(last.Stamp, now.Stamp, StringComparison.Ordinal)) return null;

      var fact = "CANVAS: the canvas HAS CHANGED since your last turn. Anything you said about it before " +
                 "may no longer be true, so read it with your tools before describing it.";

      var before = Sorted(last.Regions);
      var after = Sorted(now.Regions);
      if (!before.SequenceEqual(after, StringComparer.Ordinal))
        fact += " Scaffold regions at the end of your last turn: " + Listed(before) + ". Now: " +
                Listed(after) + ".";

      fact += CountsLine(last.Counts, now.Counts, before, after);

      return fact;
    }

    /// <summary>
    /// #660: THE COUNTS LINE. A change that empties or fills a region without adding or removing its group leaves
    /// the region set unchanged, so the fact above says the canvas changed and nothing about where. This names each
    /// region present on BOTH sides whose component count moved. An added or removed region is the region line's,
    /// never a count of zero; a side with no counts says nothing rather than comparing against a zero it never read.
    /// Numbers only - a count going down is not a deletion and going up is not a build.
    /// </summary>
    private static string CountsLine(IReadOnlyDictionary<string, int> then, IReadOnlyDictionary<string, int> now,
                                     List<string> before, List<string> after)
    {
      if (then == null || now == null) return "";

      var changed = before
        .Where(r => after.Contains(r, StringComparer.Ordinal))
        .Where(r => then.TryGetValue(r, out var a) && now.TryGetValue(r, out var b) && a != b)
        .Select(r => r + ": " + then[r] + " at the end of your last turn, now " + now[r])
        .ToList();
      if (changed.Count == 0) return "";

      var line = " Components per region: " + string.Join("; ", changed.Take(MaxListed));
      if (changed.Count > MaxListed) line += "; and " + (changed.Count - MaxListed) + " more regions whose count changed";
      return line + ".";
    }

    private static List<string> Sorted(IReadOnlyList<string> regions) =>
      (regions ?? new string[0])
        .Where(r => !string.IsNullOrWhiteSpace(r))
        .Distinct(StringComparer.Ordinal)
        .OrderBy(r => ScaffoldAddress.SortKey(r).Letters, StringComparer.Ordinal)
        .ThenBy(r => ScaffoldAddress.SortKey(r).Number)
        .ToList();

    private static string Listed(List<string> regions)
    {
      if (regions.Count == 0) return "none";
      if (regions.Count <= MaxListed) return string.Join(", ", regions);
      return string.Join(", ", regions.Take(MaxListed)) + " and " + (regions.Count - MaxListed) + " more";
    }
  }
}
