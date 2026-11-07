using System.Collections.Generic;
using System.Linq;
using Dwight.Simplify;
using Xunit;

namespace Dwight.Simplify.Tests
{
  /// <summary>RR8 #17/#19/#21 headless cores. #19: the applied summary counts renames and names skips (the
  /// "added 0 while 3 renames happened" lie). #21: each add carries its origin change index (set by the parser,
  /// preserved through flatten) so the applied list can locate exactly the components a change created. The GH
  /// rendering + canvas locator are runtime-verified.</summary>
  public class Rr8AppliedTests
  {
    // ---- #19: the honest applied summary ----

    [Fact]
    public void A_rename_only_run_reports_its_renames_not_added_zero()
    {
      // The exact bug: 3 renames, no structural change. The old summary said "added 0" and hid them.
      var s = AppliedSummary.Honest(removed: 0, added: 0, renamed: 3, kept: 10, skipped: 0);
      Assert.Contains("renamed 3", s);
      Assert.DoesNotContain("added 0", s);
    }

    [Fact]
    public void Skips_are_named_in_the_summary()
    {
      var s = AppliedSummary.Honest(removed: 1, added: 1, renamed: 0, kept: 5, skipped: 2);
      Assert.Contains("removed 1", s);
      Assert.Contains("added 1", s);
      Assert.Contains("2 change(s) were skipped", s);
    }

    [Fact]
    public void A_no_op_summary_says_so_rather_than_a_row_of_zeros()
    {
      var s = AppliedSummary.Honest(0, 0, 0, 7, 0);
      Assert.Contains("made no structural change", s);
      Assert.Contains("7 kept", s);
    }

    // ---- #21: OriginChange plumbing (parser sets it; flatten preserves it) ----

    [Fact]
    public void The_parser_stamps_each_add_with_its_change_index()
    {
      var groups = SimplifyProposalParser.Parse(@"{""changes"":[
        {""class"":""remove-dead"",""text"":""x"",""remove"":[2]},
        {""class"":""externalize"",""text"":""y"",""add"":[{""type"":""Number Slider"",""nick"":""r""}]},
        {""class"":""reduce"",""text"":""z"",""add"":[{""type"":""Panel""}]}]}");
      Assert.Equal(1, groups[1].Add[0].OriginChange);   // the slider belongs to change 1
      Assert.Equal(2, groups[2].Add[0].OriginChange);   // the panel to change 2
    }

    [Fact]
    public void Flatten_preserves_each_adds_origin_change()
    {
      // The applied list maps created guids back to their change via OriginChange, so it must survive the
      // group -> flat-proposal flatten (which concatenates adds and remaps only the LOCAL a<i> refs).
      var groups = SimplifyProposalParser.Parse(@"{""changes"":[
        {""class"":""externalize"",""text"":""a"",""add"":[{""type"":""Number Slider""}]},
        {""class"":""externalize"",""text"":""b"",""add"":[{""type"":""Number Slider""}]}]}");
      var p = PlanFilter.Flatten(groups, ticked: null, selectionIds: new HashSet<int>());
      Assert.Equal(new[] { 0, 1 }, p.Add.Select(a => a.OriginChange).ToArray());
    }

    [Fact]
    public void A_dropped_group_leaves_only_the_survivors_origin_changes()
    {
      // fix-B drops a group by NOT ticking it; the flattened adds carry only the surviving change indices, so
      // the applied list and its locator stay keyed correctly.
      var groups = SimplifyProposalParser.Parse(@"{""changes"":[
        {""class"":""externalize"",""text"":""a"",""add"":[{""type"":""Number Slider""}]},
        {""class"":""externalize"",""text"":""b"",""add"":[{""type"":""Number Slider""}]}]}");
      var p = PlanFilter.Flatten(groups, ticked: new HashSet<int> { 1 }, selectionIds: new HashSet<int>());
      Assert.Equal(new[] { 1 }, p.Add.Select(a => a.OriginChange).ToArray());
    }
  }
}
