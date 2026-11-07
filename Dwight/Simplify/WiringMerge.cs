using System;
using System.Collections.Generic;

namespace Dwight.Simplify
{
  /// <summary>
  /// The in-place source substitution for a touched input (RR4 review completion). ComputeFinalWiring starts from
  /// the input's LIVE current ordered sources, not from the rewires, so multi-source ORDER is preserved maximally
  /// and a proposal that does not restate an untouched kept source cannot destroy it:
  ///   - a surviving source (owner NOT removed) keeps its position;
  ///   - a removed source is substituted IN POSITION by the next declared rewire source (first declared replaces
  ///     the first removal, and so on) - so "wire the relay's source directly" is a true in-place substitution;
  ///   - declared sources not used as a substitution append at the end.
  /// Pure and headless (generic over the source token); the GH shim calls it with live IGH_Params.
  /// </summary>
  internal static class WiringMerge
  {
    public static List<T> InPlace<T>(IReadOnlyList<T> current, Func<T, bool> isRemoved, IReadOnlyList<T> declared)
    {
      current = current ?? Array.Empty<T>();
      declared = declared ?? Array.Empty<T>();
      var final = new List<T>();
      var di = 0;
      foreach (var src in current)
      {
        if (isRemoved == null || !isRemoved(src)) final.Add(src);        // survives - keep position
        else if (di < declared.Count) final.Add(declared[di++]);         // removed - substitute in place
        // else: removed with no replacement -> dropped (the removal-coverage validator catches an unwired consumer)
      }
      while (di < declared.Count) final.Add(declared[di++]);             // extra declared sources append
      return final;
    }
  }
}
