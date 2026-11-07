using System;
using System.Collections.Generic;

namespace Dwight.Simplify
{
  /// <summary>RR11 #22 (GH-free): the atomic demotion + re-index step. Remove every change group the predicate
  /// rejects - the WHOLE group, so a CASE-2 externalize never loses its remove while keeping its add (or vice
  /// versa) - then re-stamp each surviving add's <see cref="AddNode.OriginChange"/> to its NEW position. The #21
  /// canvas locator and Stage-6 selective-accept both key off those indices, so this re-stamp is what keeps the
  /// applied state coherent after some changes are demoted out. Returns the removed groups in original order.</summary>
  internal static class ExternalizeDemotion
  {
    public static List<ChangeGroup> Apply(List<ChangeGroup> groups, Func<ChangeGroup, bool> keep)
    {
      var demoted = new List<ChangeGroup>();
      if (groups == null || keep == null) return demoted;

      for (var i = groups.Count - 1; i >= 0; i--)
        if (!keep(groups[i])) { demoted.Add(groups[i]); groups.RemoveAt(i); }   // atomic: the whole group goes
      demoted.Reverse();   // we walked back-to-front; restore original order

      for (var i = 0; i < groups.Count; i++)
        foreach (var a in groups[i].Add ?? new List<AddNode>())
          if (a != null) a.OriginChange = i;   // indices are now contiguous and match the rendered list

      return demoted;
    }
  }
}
