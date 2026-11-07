using System;
using System.Collections.Generic;
using Dwight.Simplify;
using Xunit;

namespace Dwight.Simplify.Tests
{
  /// <summary>RR15 #31: Reorganise REPLACES the groups it fully contains. A group is replaced iff every one of its
  /// component members is in the reorganised scope; a group spilling outside the selection is LEFT ALONE so
  /// external components are never touched. Whole-canvas puts every component in scope. The GH read (gathering
  /// each group's component members) + the delete/undo are runtime-verified; this pins the decision.</summary>
  public class Rr15GroupContainmentTests
  {
    private static Guid G(int n) => new Guid($"00000000-0000-0000-0000-{n:D12}");
    private static IReadOnlyList<Guid> Members(params int[] ns) { var l = new List<Guid>(); foreach (var n in ns) l.Add(G(n)); return l; }

    [Fact]
    public void A_fully_contained_group_is_replaced()
    {
      var groups = new List<IReadOnlyList<Guid>> { Members(1, 2, 3) };
      var scope = new HashSet<Guid> { G(1), G(2), G(3), G(4) };
      Assert.Equal(new[] { 0 }, GroupContainment.ContainedGroups(groups, scope).ToArray());
    }

    [Fact]
    public void A_group_spilling_outside_the_scope_is_left_alone()
    {
      // Group {2,3,9} - member 9 is NOT in scope, so the group spans past the selection and must be untouched.
      var groups = new List<IReadOnlyList<Guid>> { Members(2, 3, 9) };
      var scope = new HashSet<Guid> { G(1), G(2), G(3) };
      Assert.Empty(GroupContainment.ContainedGroups(groups, scope));
    }

    [Fact]
    public void Whole_canvas_scope_contains_every_group()
    {
      var groups = new List<IReadOnlyList<Guid>> { Members(1, 2), Members(3), Members(4, 5) };
      var scope = new HashSet<Guid> { G(1), G(2), G(3), G(4), G(5) };
      Assert.Equal(new[] { 0, 1, 2 }, GroupContainment.ContainedGroups(groups, scope).ToArray());
    }

    [Fact]
    public void A_group_with_no_component_members_is_skipped()
    {
      // e.g. a group holding only a scribble (no components) - nothing to re-group, leave it.
      var groups = new List<IReadOnlyList<Guid>> { Members(), Members(1) };
      var scope = new HashSet<Guid> { G(1) };
      Assert.Equal(new[] { 1 }, GroupContainment.ContainedGroups(groups, scope).ToArray());
    }

    [Fact]
    public void Only_the_contained_subset_is_replaced_when_groups_are_mixed()
    {
      var groups = new List<IReadOnlyList<Guid>> { Members(1, 2), Members(2, 9), Members(3) };
      var scope = new HashSet<Guid> { G(1), G(2), G(3) };
      Assert.Equal(new[] { 0, 2 }, GroupContainment.ContainedGroups(groups, scope).ToArray());   // group 1 spills (9)
    }
  }
}
