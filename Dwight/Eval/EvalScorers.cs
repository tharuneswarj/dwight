using System.Collections.Generic;
using System.Linq;

namespace Dwight.Eval
{
  /// <summary>
  /// The eval-harness scorers (ADR-066) - the verifiable core, kept as PURE functions over ids and plans
  /// so they have no Grasshopper dependency and can be reasoned about directly. Three scorers for the three
  /// working actions: <b>groundedness</b> (no invented components, every named id real) across
  /// Explain/Diagnose/Reorganise; <b>Diagnose accuracy</b> (found faults + roots match the corpus); and
  /// <b>Reorganise structural validity</b> (the plan is well-formed and non-destructive). Simplify/Scaffold
  /// scorers are added when those actions land. The host-dependent loading + live execution lives in
  /// <see cref="EvalHarness"/>, which feeds these.
  /// </summary>
  internal static class EvalScorers
  {
    // ---- groundedness (Explain / Diagnose / Reorganise) ----------------------------------------------

    internal sealed class GroundednessResult
    {
      public int Total;
      public int Grounded;
      public readonly List<string> Invented = new List<string>();
      public double Rate => Total == 0 ? 1.0 : (double)Grounded / Total;
      public bool Passed => Invented.Count == 0;   // any invented id fails (anti-hallucination is absolute)
    }

    /// <summary>Every id a result names must exist in the snapshot (ADR-058 groundedness). An invented id
    /// is a hard fail - a weaker model never gets to pass on a component it made up.</summary>
    public static GroundednessResult Groundedness(IEnumerable<string> referencedIds, ISet<string> validIds)
    {
      var r = new GroundednessResult();
      foreach (var id in referencedIds ?? Enumerable.Empty<string>())
      {
        r.Total++;
        if (validIds != null && validIds.Contains(id)) r.Grounded++;
        else r.Invented.Add(id);
      }
      return r;
    }

    // ---- Diagnose accuracy ----------------------------------------------------------------------------

    internal sealed class DiagnoseResult
    {
      public int Expected, Found, Correct;       // fault sets
      public int ExpectedRoots, CorrectRoots;    // root sets
      public double Recall => Expected == 0 ? 1.0 : (double)Correct / Expected;
      public double Precision => Found == 0 ? (Expected == 0 ? 1.0 : 0.0) : (double)Correct / Found;
      public double RootRecall => ExpectedRoots == 0 ? 1.0 : (double)CorrectRoots / ExpectedRoots;
      public double F1 => (Precision + Recall) == 0 ? 0 : 2 * Precision * Recall / (Precision + Recall);
    }

    /// <summary>Compare the deterministically-found faults + roots against the corpus's known faults
    /// (precision/recall on faults; recall on roots). The fault scan is deterministic, so this scores the
    /// heuristics - especially the silent-failure heuristic (ADR-078) - against real definitions.</summary>
    public static DiagnoseResult DiagnoseAccuracy(
      ISet<string> foundFaults, ISet<string> foundRoots,
      ISet<string> expectedFaults, ISet<string> expectedRoots)
    {
      foundFaults ??= new HashSet<string>(); foundRoots ??= new HashSet<string>();
      expectedFaults ??= new HashSet<string>(); expectedRoots ??= new HashSet<string>();
      return new DiagnoseResult
      {
        Expected = expectedFaults.Count,
        Found = foundFaults.Count,
        Correct = foundFaults.Count(expectedFaults.Contains),
        ExpectedRoots = expectedRoots.Count,
        CorrectRoots = foundRoots.Count(expectedRoots.Contains),
      };
    }

    // ---- Reorganise structural validity --------------------------------------------------------------

    internal sealed class ReorganiseResult
    {
      public readonly List<string> Problems = new List<string>();
      public bool Passed => Problems.Count == 0;
    }

    /// <summary>A Reorganise plan must be well-formed and NON-DESTRUCTIVE (ADR-031): every guid it moves or
    /// groups must be a real selected component, no component placed twice, no group empty. (Rewiring /
    /// value changes are impossible by construction - the plan carries none - so this checks references and
    /// shape.)</summary>
    public static ReorganiseResult ReorganiseValidity(
      IEnumerable<string> movedGuids, IEnumerable<IReadOnlyList<string>> groupMembers, ISet<string> validGuids)
    {
      var r = new ReorganiseResult();
      validGuids ??= new HashSet<string>();

      var moved = (movedGuids ?? Enumerable.Empty<string>()).ToList();
      foreach (var g in moved.Where(g => !validGuids.Contains(g)).Distinct())
        r.Problems.Add($"move references an unknown component: {g}");
      foreach (var dup in moved.GroupBy(g => g).Where(grp => grp.Count() > 1).Select(grp => grp.Key))
        r.Problems.Add($"component placed more than once: {dup}");

      var groups = (groupMembers ?? Enumerable.Empty<IReadOnlyList<string>>()).ToList();
      for (var i = 0; i < groups.Count; i++)
      {
        if (groups[i] == null || groups[i].Count == 0) { r.Problems.Add($"group {i} is empty"); continue; }
        foreach (var m in groups[i].Where(m => !validGuids.Contains(m)).Distinct())
          r.Problems.Add($"group {i} references an unknown component: {m}");
      }
      return r;
    }
  }
}
