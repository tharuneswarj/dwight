using System.Collections.Generic;
using System.Linq;
using Dwight.Simplify;
using Xunit;

namespace Dwight.Simplify.Tests
{
  /// <summary>
  /// Stage 6 selective accept: flatten the ticked change groups into a flat proposal, remapping each group's
  /// change-LOCAL add refs to the global add list. The KIND split means a mis-grouped filter can only ever fail
  /// re-validation or revert - so these tests pin the composition itself: ticking a change includes its whole op
  /// group; unticking leaves its components as keeps; and local a-refs remap correctly across groups.
  /// </summary>
  public class PlanFilterTests
  {
    private static ISet<int> Set(params int[] xs) => new HashSet<int>(xs);

    // group 0: externalize - remove the panel (2), add a slider, wire the slider (local a0) to kept 3.
    private static ChangeGroup Externalize()
    {
      var g = new ChangeGroup { Class = "externalize", Text = "slider for 2", Confirm = false };
      g.Remove.Add(2);
      g.Add.Add(new AddNode { Type = "Number Slider", Nick = "radius" });
      g.Rewire.Add(new Rewire { From = "a0", FromPort = 0, To = "k3", ToPort = 1 });
      return g;
    }

    // group 1: remove a dead relay (4), wire kept 5 straight from external input i0.
    private static ChangeGroup RelayRemoval()
    {
      var g = new ChangeGroup { Class = "remove-dead", Text = "dead relay 4", Confirm = true };
      g.Remove.Add(4);
      g.Rewire.Add(new Rewire { From = "i0", FromPort = 0, To = "k5", ToPort = 0 });
      return g;
    }

    // group 2: rename kept 6.
    private static ChangeGroup RenameOnly()
    {
      var g = new ChangeGroup { Class = "rename", Text = "name 6", Confirm = false };
      g.Rename.Add(new Rename { Id = 6, Nick = "iso directions" });
      return g;
    }

    private static List<ChangeGroup> AllThree() => new List<ChangeGroup> { Externalize(), RelayRemoval(), RenameOnly() };

    [Fact]
    public void Flattening_all_groups_unions_every_op_and_derives_keep()
    {
      var p = PlanFilter.Flatten(AllThree(), ticked: null, selectionIds: Set(1, 2, 3, 4, 5, 6));
      Assert.Equal(new[] { 2, 4 }, p.Remove.OrderBy(x => x).ToArray());
      Assert.Equal(new[] { 1, 3, 5, 6 }, p.Keep.OrderBy(x => x).ToArray());   // selection minus removes
      Assert.Single(p.Add);
      Assert.Single(p.Renames);
      Assert.Equal(6, p.Renames[0].Id);
    }

    [Fact]
    public void Ticking_only_the_rename_leaves_every_component_kept_and_no_destructive_ops()
    {
      var p = PlanFilter.Flatten(AllThree(), ticked: Set(2), selectionIds: Set(1, 2, 3, 4, 5, 6));
      Assert.Empty(p.Remove);
      Assert.Empty(p.Add);
      Assert.Empty(p.Rewires);
      Assert.Equal(new[] { 1, 2, 3, 4, 5, 6 }, p.Keep.OrderBy(x => x).ToArray());   // nothing removed -> all kept
      Assert.Single(p.Renames);
    }

    [Fact]
    public void Unticking_a_relay_removal_keeps_the_relay_and_drops_its_rewire()
    {
      // take the externalize (0) and the rename (2), reject the relay removal (1): relay 4 stays kept, i0->k5 gone.
      var p = PlanFilter.Flatten(AllThree(), ticked: Set(0, 2), selectionIds: Set(1, 2, 3, 4, 5, 6));
      Assert.DoesNotContain(4, p.Remove);
      Assert.Contains(4, p.Keep);
      Assert.DoesNotContain(p.Rewires, w => w.To == "k5");   // the relay's rewire is excluded with its group
    }

    [Fact]
    public void Change_local_add_refs_remap_to_the_global_add_list()
    {
      // two externalize groups, each wiring its OWN local a0. Flattened, group 0's a0 stays a0, group 1's a0 -> a1.
      var g0 = Externalize();                                   // add index 0 globally; rewire from a0
      var g1 = new ChangeGroup { Class = "externalize", Text = "slider for 7", Confirm = false };
      g1.Remove.Add(7);
      g1.Add.Add(new AddNode { Type = "Number Slider" });
      g1.Rewire.Add(new Rewire { From = "a0", FromPort = 0, To = "k8", ToPort = 0 });   // LOCAL a0

      var p = PlanFilter.Flatten(new List<ChangeGroup> { g0, g1 }, ticked: null, selectionIds: Set(2, 3, 7, 8));
      Assert.Equal(2, p.Add.Count);
      var toK3 = p.Rewires.Single(w => w.To == "k3");
      var toK8 = p.Rewires.Single(w => w.To == "k8");
      Assert.Equal("a0", toK3.From);   // group 0's local a0 -> global a0
      Assert.Equal("a1", toK8.From);   // group 1's local a0 -> global a1 (remapped)
    }

    [Fact]
    public void A_non_add_ref_is_left_untouched_by_the_remap()
    {
      var p = PlanFilter.Flatten(new List<ChangeGroup> { RelayRemoval() }, ticked: null, selectionIds: Set(4, 5));
      var w = p.Rewires.Single();
      Assert.Equal("i0", w.From);   // an external-input ref is global, never remapped
      Assert.Equal("k5", w.To);
    }
  }
}
