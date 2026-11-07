using System;
using System.Collections.Generic;
using System.Text;

namespace Dwight.Eval
{
  /// <summary>One corpus case: a real definition plus its known faults (ADR-066). Tharun supplies/curates
  /// the .gh files (clean / faulty / large / messy); the expected sets are the ground truth Diagnose is
  /// scored against.</summary>
  internal sealed class EvalCase
  {
    public string Name = "";
    public string GhPath = "";                                        // the .gh file in the corpus
    public HashSet<string> ExpectedFaultGuids = new HashSet<string>();
    public HashSet<string> ExpectedRootGuids = new HashSet<string>();
  }

  /// <summary>The host-dependent seam Tharun wires (this is the runner's "stub" half): load a case and run
  /// the live actions. Loading a .gh into a snapshot and solving it needs the Rhino host, so it lives behind
  /// this interface rather than in the harness - the scorers (<see cref="EvalScorers"/>) are the pure,
  /// fully-built core. An implementation can drive the live document or recorded snapshots.</summary>
  internal interface IEvalDriver
  {
    /// <summary>The instanceGuids present in the case's snapshot (the groundedness whitelist).</summary>
    ISet<string> ValidGuids(EvalCase c);

    /// <summary>Run Diagnose for a (provider, posture); return the found fault + root guids.</summary>
    (ISet<string> faults, ISet<string> roots) RunDiagnose(EvalCase c, string provider, string posture);

    /// <summary>Run Explain; return every component guid the answer named (for the groundedness score).</summary>
    IEnumerable<string> RunExplainReferences(EvalCase c, string provider, string posture);
  }

  /// <summary>
  /// The eval-harness runner (ADR-066) - stood up here, runs continuously, and is the hard gate at Phase 9.
  /// It orchestrates corpus x (provider, posture) and applies the pure <see cref="EvalScorers"/>, emitting a
  /// per-action score report. The model says: a provider/model/tier is shippable only if it passes - the
  /// cost-posture menu may only offer configs that clear the gate. The live execution is injected via
  /// <see cref="IEvalDriver"/> (host-dependent); the orchestration + scoring here are complete.
  /// </summary>
  internal static class EvalHarness
  {
    public static string Run(IReadOnlyList<EvalCase> corpus, IReadOnlyList<(string provider, string posture)> configs, IEvalDriver driver)
    {
      var sb = new StringBuilder();
      sb.AppendLine("Dwight eval harness (ADR-066)");
      if (driver == null)
      {
        sb.AppendLine("No IEvalDriver wired. Implement it (load a .gh case into a snapshot and run the actions,");
        sb.AppendLine("headless or against the live document) and supply the corpus to run the gate.");
        return sb.ToString();
      }
      if (corpus == null || corpus.Count == 0) { sb.AppendLine("No corpus cases supplied."); return sb.ToString(); }
      if (configs == null || configs.Count == 0) { sb.AppendLine("No (provider, posture) configs supplied."); return sb.ToString(); }

      foreach (var c in corpus)
      {
        sb.AppendLine();
        sb.AppendLine($"# {c.Name}");
        ISet<string> valid;
        try { valid = driver.ValidGuids(c); }
        catch (Exception ex) { sb.AppendLine($"  load failed: {ex.Message}"); continue; }

        foreach (var (provider, posture) in configs)
        {
          var tag = $"  [{DwightConfig.ProviderLabel(provider)} / {posture}]";
          try
          {
            var refs = driver.RunExplainReferences(c, provider, posture);
            var g = EvalScorers.Groundedness(Strings(refs), valid);
            sb.AppendLine($"{tag} Explain groundedness: {g.Grounded}/{g.Total}" + (g.Passed ? " (pass)" : $" FAIL invented {string.Join(",", g.Invented)}"));

            var (faults, roots) = driver.RunDiagnose(c, provider, posture);
            var dg = EvalScorers.Groundedness(faults, valid);
            var d = EvalScorers.DiagnoseAccuracy(faults, roots, c.ExpectedFaultGuids, c.ExpectedRootGuids);
            sb.AppendLine($"{tag} Diagnose: P {d.Precision:0.00} R {d.Recall:0.00} rootR {d.RootRecall:0.00}; grounded {(dg.Passed ? "pass" : "FAIL")}");
          }
          catch (Exception ex) { sb.AppendLine($"{tag} errored: {ex.Message}"); }
        }
      }
      return sb.ToString();
    }

    private static IEnumerable<string> Strings(IEnumerable<string> s) => s ?? new List<string>();
  }
}
