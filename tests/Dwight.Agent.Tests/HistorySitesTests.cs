using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// Brief AP: every undo record Dwight pushes has its id kept at the source. History counts a record whose id it does
  /// not know as the USER'S, so a site that pushes a record without handing the id to ChangeLog would make "Go back"
  /// name Dwight's own change as one of yours. This scans the plugin's source for every call that pushes a record and
  /// requires the id capture on the same line; a new site that forgets it fails here, not in a sitting.
  /// </summary>
  public class HistorySitesTests
  {
    private static readonly Regex Push = new Regex(@"\.(UndoUtil\.Record\w*Event|RecordUndoEvent)\(", RegexOptions.Compiled);

    [Fact]
    public void Every_record_Dwight_pushes_hands_its_id_to_the_change_log()
    {
      var root = RepoRoot();
      var sites = 0;
      var missing = Directory.GetFiles(Path.Combine(root, "Dwight"), "*.cs", SearchOption.AllDirectories)
        .SelectMany(f => File.ReadAllLines(f).Select((line, i) => (f, line, i)))
        .Where(x => !x.line.TrimStart().StartsWith("//", StringComparison.Ordinal) && Push.IsMatch(x.line))
        .Select(x => { sites++; return x; })
        .Where(x => !x.line.Contains("ChangeLog.Note"))
        .Select(x => Path.GetRelativePath(root, x.f) + ":" + (x.i + 1) + "  " + x.line.Trim())
        .ToList();
      Assert.True(sites >= 20, "the scan found only " + sites + " record sites; it is not reading the source");   // the scan's own control (22 when written)
      Assert.True(missing.Count == 0, "Record sites without the id capture:\n" + string.Join("\n", missing));
    }

    [Fact]
    public void Every_self_revert_marks_the_record_before_it_undoes()
    {
      // The helpers each site reverts through; each must call ChangeLog.Reverting before its undo, so History never offers
      // to redo a change that failed its check.
      var root = RepoRoot();
      foreach (var rel in new[] { "Agent/GhBuildRegion.cs", "Agent/GhEditScript.cs", "Agent/GhProposeScaffold.cs", "Agent/GhRegionTidy.cs",
                                  "Agent/GhSetValue.cs", "Simplify/SimplifyApply.cs", "UI/DwightScaffold.cs" })
      {
        var src = File.ReadAllText(Path.Combine(root, "Dwight", rel));
        Assert.True(src.Contains("ChangeLog.Reverting("), rel + " reverts without marking the record");
      }
    }

    private static string RepoRoot()
    {
      var dir = AppContext.BaseDirectory;
      while (dir != null && !File.Exists(Path.Combine(dir, "Dwight.sln"))) dir = Path.GetDirectoryName(dir);
      Assert.NotNull(dir);
      return dir;
    }
  }
}
