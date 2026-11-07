using System.Collections.Generic;
using System.Linq;
using Dwight.Simplify;
using Xunit;

namespace Dwight.Simplify.Tests
{
  /// <summary>RR11 #22: the GH-free core of demote-to-advice - dropping an unresolvable externalize group
  /// ATOMICALLY (a CASE-2 remove+add never splits) and re-stamping OriginChange so the #21 locator and Stage-6
  /// selective-accept stay index-consistent after some changes are demoted out. The live resolution-test itself
  /// (TryResolveExternalize) is GH-coupled and runtime-verified.</summary>
  public class Rr11DemotionTests
  {
    private static ChangeGroup Ext(int targetId, params int[] removes)
    {
      var g = new ChangeGroup { Class = "externalize", Text = "x" };
      g.Add.Add(new AddNode { Type = "Number Slider", ExternalizeTargetId = targetId, OriginChange = 99 });
      foreach (var r in removes) g.Remove.Add(r);
      return g;
    }
    private static ChangeGroup Plain(string cls) { var g = new ChangeGroup { Class = cls, Text = "y" }; return g; }

    [Fact]
    public void An_unresolvable_group_is_removed_and_survivors_are_reindexed()
    {
      var a = Ext(10); var b = Plain("remove-dead"); var c = Ext(20);
      var groups = new List<ChangeGroup> { a, b, c };
      // reject the LAST externalize (index 2) as unresolvable
      var demoted = ExternalizeDemotion.Apply(groups, g => g != c);

      Assert.Equal(new[] { a, b }, groups.ToArray());     // c gone
      Assert.Equal(new[] { c }, demoted.ToArray());        // c returned for advice
      Assert.Equal(0, a.Add[0].OriginChange);              // re-stamped to its new position (was 99)
    }

    [Fact]
    public void A_case2_externalize_demotes_as_a_whole_group_remove_and_add_together()
    {
      // The architect's correctness point: a CASE-2 externalize bundles a Panel REMOVE with the slider ADD. If
      // demotion kept the remove but dropped the add, a hardcoded value would be deleted with nothing replacing
      // it. Apply removes the entire group, so the remove leaves with the add.
      var case2 = Ext(30, removes: new[] { 30 });   // remove Panel 30 + add slider externalizing it
      var keep = Plain("reduce");
      var groups = new List<ChangeGroup> { case2, keep };
      var demoted = ExternalizeDemotion.Apply(groups, g => g != case2);

      Assert.Equal(new[] { keep }, groups.ToArray());
      Assert.Single(demoted);
      Assert.Contains(30, demoted[0].Remove);            // the remove went WITH the group
      Assert.Single(demoted[0].Add);                     // and so did the add
    }

    [Fact]
    public void Middle_demotion_leaves_contiguous_zero_based_indices()
    {
      var a = Ext(1); var b = Ext(2); var c = Ext(3);
      var groups = new List<ChangeGroup> { a, b, c };
      ExternalizeDemotion.Apply(groups, g => g != b);   // drop the middle one

      Assert.Equal(new[] { a, c }, groups.ToArray());
      Assert.Equal(0, a.Add[0].OriginChange);
      Assert.Equal(1, c.Add[0].OriginChange);   // c slid from index 2 to 1
    }

    [Fact]
    public void Nothing_demoted_leaves_the_list_and_indices_intact()
    {
      var a = Ext(1); var b = Plain("rename");
      var groups = new List<ChangeGroup> { a, b };
      var demoted = ExternalizeDemotion.Apply(groups, _ => true);
      Assert.Empty(demoted);
      Assert.Equal(new[] { a, b }, groups.ToArray());
      Assert.Equal(0, a.Add[0].OriginChange);
    }
  }
}
