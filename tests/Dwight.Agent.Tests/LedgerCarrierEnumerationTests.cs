using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// Brief DG: THE FIFTH SITE CANNOT APPEAR UNREAD. Every call to <c>StructuredOutput.Get</c> and <c>ConverseProse</c> in the product is a paid call that can
  /// throw after it was billed, and the bill rides on the exception (<c>UsageCarrier</c>, Brief DF). This lists every call site, by reading the source as
  /// Brief CU's enumeration does, and requires each to be one of three things: a site whose own catch reads the carrier, a site whose CALLER does (named, and
  /// checked), or a site declared to leave no ledger line, with its reason. A call added later is none of them, so the count of its file moves and this turns red.
  /// </summary>
  public class LedgerCarrierEnumerationTests
  {
    private static string Repo()
    {
      var dir = new DirectoryInfo(AppContext.BaseDirectory);
      while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Dwight.sln"))) dir = dir.Parent;
      Assert.True(dir != null);
      return dir.FullName;
    }

    private static IEnumerable<(string File, int Line, string[] Lines)> Sites(string needle)
    {
      var root = Path.Combine(Repo(), "Dwight");
      foreach (var path in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
      {
        var rel = path.Substring(root.Length + 1).Replace('\\', '/');
        if (rel.StartsWith("bin/", StringComparison.Ordinal) || rel.StartsWith("obj/", StringComparison.Ordinal)) continue;
        var lines = File.ReadAllLines(path);
        for (var i = 0; i < lines.Length; i++)
        {
          var t = lines[i].TrimStart();
          if (t.StartsWith("//", StringComparison.Ordinal) || t.StartsWith("///", StringComparison.Ordinal) || t.StartsWith("*", StringComparison.Ordinal)) continue;
          if (!lines[i].Contains(needle)) continue;
          if (lines[i].Contains(" static ")) continue;   // the definition, not a call
          yield return ("Dwight/" + rel, i + 1, lines);
        }
      }
    }

    /// <summary>The paid calls, by file: how many, and how each is held. A new call in any file moves its count.</summary>
    private static readonly Dictionary<string, (int Count, string Held)> Expected = new Dictionary<string, (int, string)>
    {
      ["Dwight/LlmActions.cs"] = (6, "own catch reads the carrier"),                    // Explain steps, Explain prose, Diagnose, the chain row, to-script, Simplify
      ["Dwight/Labelling/ClusterLabellerCore.cs"] = (1, "the caller reads it"),          // Reorganise's labelling: LlmActions catches around ClusterLabeller.Label
      ["Dwight/UI/TeachingCall.cs"] = (2, "own catch reads the carrier"),                 // Brief DE: the lesson's one call, and Brief EO's judge; each Ask keeps the usage on its result, and the caller carries a Stop's
      ["Dwight/Agent/AgentHarness.cs"] = (1, "no ledger line: the console harness"),     // dev-only; its own spend cap, no product ledger
      ["Dwight/Eval/ToScriptEval.cs"] = (1, "no ledger line: the eval harness"),         // dev-only; its own spend cap, no product ledger
    };

    [Fact]
    public void Every_call_to_Get_and_to_ConverseProse_is_one_the_enumeration_knows()
    {
      var found = Sites("StructuredOutput.Get(").Concat(Sites("ConverseProse(")).GroupBy(s => s.File).ToDictionary(g => g.Key, g => g.Count());
      var expected = Expected.ToDictionary(kv => kv.Key, kv => kv.Value.Count);
      Assert.Equal(expected.OrderBy(k => k.Key).ToList(), found.OrderBy(k => k.Key).ToList());
    }

    [Fact]
    public void Each_call_in_LlmActions_is_followed_by_a_catch_that_reads_the_carrier()
    {
      var sites = Sites("StructuredOutput.Get(").Concat(Sites("ConverseProse(")).Where(s => s.File == "Dwight/LlmActions.cs").ToList();
      Assert.Equal(Expected["Dwight/LlmActions.cs"].Count, sites.Count);
      foreach (var s in sites)
      {
        // The catch follows the call within a few lines (the call's arguments, then the closing of its try). A call with no read in that window is a gap.
        var window = string.Join("\n", s.Lines.Skip(s.Line - 1).Take(14));
        Assert.True(window.Contains("CarryPaid(") || window.Contains("UsageCarrier.TryTake("),
          "LlmActions.cs line " + s.Line + " is a paid call whose catch does not read the carrier (UsageCarrier / CarryPaid): " + s.Lines[s.Line - 1].Trim());
      }
    }

    [Fact]
    public void The_lessons_call_reads_the_carrier_in_its_own_catch_and_a_stop_is_carried_by_its_caller()
    {
      var text = File.ReadAllText(Path.Combine(Repo(), "Dwight", "UI", "TeachingCall.cs"));
      Assert.Contains("if (UsageCarrier.TryTake(ex, out var paid)) result.Usage = paid;", text);
      var actions = File.ReadAllText(Path.Combine(Repo(), "Dwight", "LlmActions.cs"));
      Assert.Contains("catch (OperationCanceledException ex) { CarryPaid(ctx, model, ex, tsw); throw; }", actions);
    }

    [Fact]
    public void The_labeller_is_called_from_a_catch_that_reads_the_carrier()
    {
      var calls = Sites("ClusterLabeller.Label(").Where(s => s.File == "Dwight/LlmActions.cs").ToList();
      Assert.Single(calls);
      var window = string.Join("\n", calls[0].Lines.Skip(calls[0].Line - 1).Take(4));
      Assert.Contains("CarryPaid(ctx, model, ex, sw)", window);
    }

    [Fact]
    public void The_two_harnesses_are_not_the_product_and_say_they_leave_no_line()
    {
      // A harness run is a developer's, bounded by its own spend cap; the product's ledger is the person's record of what Dwight cost them. If either
      // ever writes to the ledger, it must read the carrier like the rest, and this is where that is said out loud.
      foreach (var file in new[] { "Dwight/Agent/AgentHarness.cs", "Dwight/Eval/ToScriptEval.cs" })
      {
        var text = File.ReadAllText(Path.Combine(Repo(), file.Replace('/', Path.DirectorySeparatorChar)));
        Assert.DoesNotContain("SpendLedger.", text);
        Assert.DoesNotContain("LedgerRun(", text);
      }
    }
  }
}
