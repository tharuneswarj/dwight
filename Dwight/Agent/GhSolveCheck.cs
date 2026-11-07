using System;
using System.Linq;
using Grasshopper.Kernel;
using Dwight.Data;

namespace Dwight.Agent
{
  /// <summary>
  /// <c>solve_and_check</c> - run the definition and report what Grasshopper says about it: runtime
  /// messages and per-output item counts, the same deterministic signals Diagnose reads (ADR-078).
  ///
  /// THIS IS THE ONE TOOL THAT SOLVES. Reads are zero-token, solve-free and mutation-free (#190);
  /// solving is observable work (it can take seconds and it recomputes geometry), so it is its own tool
  /// rather than a hidden cost inside a read.
  ///
  /// THREADING, THE HARD-WON PART (#117/#130). The solve is never run synchronously: a synchronous
  /// <c>NewSolution</c> from a background caller is exactly what wedged the bridge for five minutes
  /// during WP-0. Instead this arms <c>SolutionEnd</c>, schedules the solution, and reports from the
  /// callback - with a WATCHDOG so a hung solve fails this CALL rather than hanging the agent forever.
  /// And when nothing is expired there is no solve at all: the current state already IS the solved
  /// state, so reading it directly is both cheaper and more honest than forcing a recompute.
  /// </summary>
  internal static class GhSolveCheck
  {
    /// <summary>How long to wait for a solution before failing the call. Generous - a big definition
    /// legitimately takes a while - but finite, because a wedged solve must never hang the agent.</summary>
    private const int WatchdogMs = 120_000;

    public static void Run(GH_Document doc, AgentIdRegistry ids, Action<ToolResult> done)
    {
      if (doc == null) { done?.Invoke(ToolResult.Fail(ToolErrors.NoDocument, "No Grasshopper document is open.")); return; }

      if (!SolverEnabled())
      {
        // Never a vacuous pass (#109): if it cannot be checked, say so rather than report a clean scan.
        done?.Invoke(ToolResult.Fail(ToolErrors.SolverDisabled,
          "The Grasshopper solver is disabled, so the definition cannot be solved or checked. " +
          "Enable it and call solve_and_check again."));
        return;
      }

      bool needsSolve;
      try { needsSolve = doc.Objects.Any(o => o is IGH_ActiveObject a && a.Phase == GH_SolutionPhase.Blank); }
      catch { needsSolve = true; }

      if (!needsSolve) { done?.Invoke(Report(doc, ids, solved: false)); return; }   // already current

      GH_Document.SolutionEndEventHandler handler = null;
      Eto.Forms.UITimer timeout = null;
      var fired = false;

      void Finish(ToolResult result)
      {
        if (fired) return;
        fired = true;
        try { if (handler != null) doc.SolutionEnd -= handler; } catch { }
        try { timeout?.Stop(); } catch { }
        done?.Invoke(result);
      }

      handler = (s, e) =>
      {
        // Read only - never mutate the document inside SolutionEnd.
        try { Finish(Report(doc, ids, solved: true)); }
        catch (Exception ex) { Finish(ToolResult.Fail(ToolErrors.PreconditionRefused, "The solve could not be read (" + ex.Message + ").")); }
      };

      try
      {
        doc.SolutionEnd += handler;
        timeout = new Eto.Forms.UITimer { Interval = WatchdogMs / 1000.0 };
        timeout.Elapsed += (s, e) => Finish(ToolResult.Fail(ToolErrors.PreconditionRefused,
          "The definition did not finish solving within " + (WatchdogMs / 1000) + "s, so nothing can be reported about it."));
        timeout.Start();
        doc.ScheduleSolution(10);   // scheduled, never a synchronous NewSolution (#117)
      }
      catch (Exception ex)
      {
        Finish(ToolResult.Fail(ToolErrors.PreconditionRefused, "The solve could not be started (" + ex.Message + ")."));
      }
    }

    private static bool SolverEnabled()
    {
      try { return GH_Document.EnableSolutions; } catch { return true; }
    }

    /// <summary>
    /// Project the solved state into the tool result, in registry-id space.
    ///
    /// ONLY WHAT HAS SOMETHING TO SAY. This listed every node's item counts, which on a real definition
    /// meant several hundred rows of <c>"outCounts":[1]</c> - a wall of noise for a tool whose whole job
    /// is "tell me what is wrong", and one the WP-3 loop would pay for on every call. A component that
    /// produced data is the boring case; a component that produced NOTHING is the diagnostic signal, and
    /// so is the only one listed. The omission is stated in the payload and the total is given beside it,
    /// because a silent cap reads as "that was everything" when it was not.
    /// </summary>
    private static ToolResult Report(GH_Document doc, AgentIdRegistry ids, bool solved)
    {
      var graph = DwightGraph.Build(doc, Array.Empty<Guid>());
      ids.Sync(graph.Nodes.Select(n => n.Guid));

      // The projection is GH-free (SolveCheckReport, U27 #917): every figure it states carries its meaning in
      // the payload - "solved" above all, whose false used to live only in a comment here and was explained
      // to the user by the model as an unwired definition.
      var rows = graph.Nodes.Select(n => new SolveCheckRow
      {
        Id = ids.Issue(n.Guid),
        Type = n.Type,
        Msgs = n.Msgs.Select(m => new MsgDto { Level = m.Level, Text = m.Text }).ToList(),
        OutCounts = n.OutCounts ?? Array.Empty<int>(),
      });
      return ToolResult.Success(SolveCheckReport.Build(rows, solved));
    }
  }
}
