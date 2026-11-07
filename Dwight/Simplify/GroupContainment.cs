using System.Collections.Generic;
using System.Linq;

namespace Dwight.Simplify
{
  /// <summary>RR15 #31 (GH-free): which existing GH groups a Reorganise REPLACES. A group is replaced iff EVERY
  /// one of its component members is inside the reorganised scope (and it has at least one) - Dwight then re-groups
  /// those components by topology, in the one undo record. A group with any component OUTSIDE the scope spills past
  /// the selection and is LEFT ALONE, so external components are never touched. Whole-canvas -> scope is every
  /// component, so all groups are in scope. Behaviourally safe (the fingerprint ignores groups). GH read gathers
  /// each group's component members; this decision is headless-tested.</summary>
  internal static class GroupContainment
  {
    public static List<int> ContainedGroups(IReadOnlyList<IReadOnlyList<System.Guid>> groupComponentMembers, ISet<System.Guid> scope)
    {
      var result = new List<int>();
      if (groupComponentMembers == null || scope == null) return result;
      for (var i = 0; i < groupComponentMembers.Count; i++)
      {
        var members = groupComponentMembers[i];
        if (members == null || members.Count == 0) continue;   // no component members -> nothing to re-group; leave it
        if (members.All(scope.Contains)) result.Add(i);        // fully contained -> replace
      }
      return result;
    }
  }
}
