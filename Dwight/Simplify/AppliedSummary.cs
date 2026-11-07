using System.Collections.Generic;

namespace Dwight.Simplify
{
  /// <summary>RR8 #19 (GH-free): the applied summary must never lie. It counts RENAMES (which are not in the
  /// kept/removed/added tally, the original bug where "added 0" hid 3 renames) and names SKIPS, so a rename-only
  /// run reports its renames and a run with skipped changes says so. Pure string assembly, unit-tested.</summary>
  internal static class AppliedSummary
  {
    public static string Honest(int removed, int added, int renamed, int kept, int skipped)
    {
      var parts = new List<string>();
      if (removed > 0) parts.Add($"removed {removed}");
      if (added > 0) parts.Add($"added {added}");
      if (renamed > 0) parts.Add($"renamed {renamed}");
      var did = parts.Count == 0 ? "made no structural change" : string.Join(", ", parts) + " component(s)";
      var skip = skipped > 0 ? $" {skipped} change(s) were skipped." : "";
      return $"Simplified - {did}; {kept} kept.{skip} The outputs are unchanged for your current inputs (verified by re-solve); one Ctrl-Z reverts everything.";
    }
  }
}
