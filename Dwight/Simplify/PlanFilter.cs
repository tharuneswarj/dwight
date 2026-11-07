using System.Collections.Generic;

namespace Dwight.Simplify
{
  /// <summary>
  /// Stage 6 selective accept (headless): flatten a set of ticked <see cref="ChangeGroup"/>s into a flat
  /// <see cref="SimplifyProposal"/> the existing validate -> resolve -> predict -> apply path consumes UNCHANGED.
  /// "Apply all" is just "all groups ticked" (<paramref name="ticked"/> null), so full and selective accept share
  /// one path. Each group's ops are concatenated in order; a group's change-LOCAL add refs (<c>a&lt;i&gt;</c>) are
  /// remapped to the global add list (<c>k</c>/<c>i</c>/<c>o</c> refs are global and pass through); <c>keep</c> is
  /// DERIVED as the selection minus every ticked remove, so an unticked group's components simply stay kept.
  ///
  /// Compositional by construction: a subset that turns out inconsistent (a rewire orphaned by an unticked group)
  /// fails the re-run <see cref="ProposalValidator"/>, and anything that slips past still rides the layer-1/2
  /// revert net - a mis-grouped filter can only revert, never corrupt.
  /// </summary>
  internal static class PlanFilter
  {
    public static SimplifyProposal Flatten(IReadOnlyList<ChangeGroup> groups, ISet<int> ticked, ISet<int> selectionIds)
    {
      var p = new SimplifyProposal();
      if (groups != null)
        for (var g = 0; g < groups.Count; g++)
        {
          if (ticked != null && !ticked.Contains(g)) continue;
          var group = groups[g];
          if (group == null) continue;

          foreach (var id in group.Remove ?? new List<int>()) p.Remove.Add(id);

          var addBase = p.Add.Count;   // this group's local a<i> becomes global a<addBase + i>
          foreach (var a in group.Add ?? new List<AddNode>()) p.Add.Add(a);
          foreach (var w in group.Rewire ?? new List<Rewire>())
            p.Rewires.Add(new Rewire { From = RemapRef(w?.From, addBase), FromPort = w?.FromPort ?? 0, To = RemapRef(w?.To, addBase), ToPort = w?.ToPort ?? 0 });
          foreach (var r in group.Rename ?? new List<Rename>()) p.Renames.Add(r);

          p.Changes.Add(new Change { Class = group.Class, Text = group.Text, Confirm = group.Confirm });
        }

      // keep = selection minus every (ticked) remove - an unticked group's components are untouched, so kept.
      var removed = new HashSet<int>(p.Remove);
      if (selectionIds != null)
        foreach (var id in selectionIds) if (!removed.Contains(id)) p.Keep.Add(id);

      return p;
    }

    /// <summary>Remap a change-local added-component ref to the global add list; all other ref kinds are global
    /// and pass through unchanged.</summary>
    private static string RemapRef(string reff, int addBase)
    {
      if (addBase == 0 || string.IsNullOrEmpty(reff) || reff.Length < 2 || reff[0] != 'a') return reff;
      return int.TryParse(reff.Substring(1), out var i) ? "a" + (i + addBase) : reff;
    }
  }
}
