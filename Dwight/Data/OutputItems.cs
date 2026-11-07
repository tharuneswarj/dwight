using System;
using System.Collections;

namespace Dwight.Data
{
  /// <summary>
  /// WHETHER AN OUTPUT HOLDS ANYTHING - one rule, for every trial that has to ask (U20's equivalence check, U19's
  /// emptiness oracle).
  ///
  /// "HAS DATA" MEANS AT LEAST ONE NON-NULL ITEM (the architect's ruling for U19, applied to U20 where it was missing).
  /// An errored script still emits a null item on every output (Phase-7 WP-4, measured live), and a script that runs
  /// clean and sets its output to null or None puts an item there too - so an item COUNT says nothing about whether
  /// anything was produced. U20's trial counted items, which let two outputs that were both nothing count as "carrying
  /// data", compare equal, and read as "identical": the vacuous check #109 exists to stop, laundered by a count.
  ///
  /// GH-free (ADR-091): the caller hands in the param's items (<c>VolatileData.AllData(false)</c> - nulls INCLUDED,
  /// which is the point), so the rule is tested here rather than trusted there.
  /// </summary>
  internal static class OutputItems
  {
    /// <summary>Every item, and the ones that are not null.</summary>
    public static (int Items, int NonNull) Count(IEnumerable items) => Count(items, null);

    /// <summary>
    /// The same count, where the caller can say which items are NOTHING though they are not null (U47): Grasshopper wraps a
    /// script's <c>None</c> in a goo whose value is null, so "not null" alone read a script that produced nothing as one that
    /// produced a thing. The predicate arrives as a delegate because the wrapper types are Grasshopper's; the RULE - a
    /// nothing is not data - is here.
    /// </summary>
    public static (int Items, int NonNull) Count(IEnumerable items, Func<object, bool> isNothing)
    {
      var all = 0;
      var nonNull = 0;
      if (items == null) return (0, 0);
      foreach (var item in items)
      {
        all++;
        if (item == null) continue;
        var nothing = false;
        if (isNothing != null) { try { nothing = isNothing(item); } catch { nothing = false; } }
        if (!nothing) nonNull++;
      }
      return (all, nonNull);
    }

    public static bool HasNonNull(IEnumerable items) => Count(items).NonNull > 0;
  }
}
