using System;
using System.Collections.Generic;
using System.Linq;
using Eto.Forms;
using Grasshopper.Kernel;
using Dwight.Session;

namespace Dwight.UI
{
  /// <summary>
  /// History's walk on Grasshopper's undo stack (Brief AP). <see cref="Ask"/> counts the walk from one reading of the
  /// stack and returns the confirmation; <see cref="Confirm"/> reads the stack again, refuses before the first step if it
  /// moved, and otherwise steps <c>PerformUndo</c> or <c>PerformRedo</c> that many times inside one scheduled solution,
  /// checking before each step that the record on top is the one that was counted. Every decision is in
  /// <see cref="HistoryCore"/>; this only reads and steps.
  ///
  /// It calls the undo server's own <c>PerformUndo</c>, not <c>GH_Document.Undo</c>: the document's wrapper turns a failed
  /// step into Grasshopper's assert window (read from its IL), where the server's method throws and this catches it.
  /// UI thread only. Never throws.
  /// </summary>
  internal static class HistoryWalk
  {
    private sealed class Pending
    {
      public GH_Document Doc;
      public WalkPlan Plan;
      public StackSnapshot At;
    }

    private static Pending _pending;

    /// <summary>Is a confirmation waiting? The dock shows it in place of the list.</summary>
    public static bool Waiting(GH_Document doc) => _pending != null && ReferenceEquals(_pending.Doc, doc);

    public static void Cancel() => _pending = null;

    /// <summary>Brief BQ: the records the waiting walk will change, so the rows can be tinted. The row a walk "to here" leaves standing is not among them.</summary>
    public static IReadOnlyList<Guid> WaitingIds(GH_Document doc) =>
      Waiting(doc) ? _pending.Plan.ExpectedIds : (IReadOnlyList<Guid>)Array.Empty<Guid>();

    /// <summary>Count a walk to one change. Returns the confirmation to show, or the refusal; null when there is nothing to offer.
    /// <paramref name="before"/>: go back to before it (Brief BQ), so the change itself is undone too.</summary>
    public static string Ask(GH_Document doc, Guid target, bool back, bool before = false)
    {
      _pending = null;
      if (doc == null) return HistoryCore.NoDocumentLine;
      if (Dwight.DwightSession.IsBusy) return HistoryCore.WalkBusyLine;   // Brief BR
      var snap = ChangeLog.Snapshot(doc);
      var changes = CommandHistory.Changes(doc);
      var ids = new HashSet<Guid>(changes.Where(c => c.Id != null).Select(c => c.Id.Value));
      var reverted = new HashSet<Guid>(changes.Where(c => c.Id != null && c.Reverted).Select(c => c.Id.Value));
      var plan = before ? HistoryCore.PlanBackBefore(target, snap, ids)
        : back ? HistoryCore.PlanBack(target, snap, ids) : HistoryCore.PlanRedo(target, snap, ids, reverted);
      if (plan == null) return null;
      if (plan.Refusal != null) return plan.Refusal;
      _pending = new Pending { Doc = doc, Plan = plan, At = snap };
      return HistoryCore.Confirmation(plan);
    }

    /// <summary>Run the waiting walk. <paramref name="done"/> receives the line to show, on the UI thread.</summary>
    public static void Confirm(GH_Document doc, Action<string> done)
    {
      var p = _pending;
      _pending = null;
      if (p == null || doc == null || !ReferenceEquals(p.Doc, doc)) { Say(done, HistoryCore.MovedLine(p?.Plan?.Back ?? true)); return; }

      // Brief BR: a run may have started since the confirmation was drawn.
      if (Dwight.DwightSession.IsBusy) { Say(done, HistoryCore.WalkBusyLine); return; }

      // The stack must be exactly as it was counted: the user may have clicked in Grasshopper since. Never a partial walk.
      if (!HistoryCore.SameStack(p.At, ChangeLog.Snapshot(doc))) { Say(done, HistoryCore.MovedLine(p.Plan.Back)); return; }

      try
      {
        doc.ScheduleSolution(1, d =>
        {
          var plan = p.Plan;
          var taken = 0;
          string stop = null;
          try
          {
            var server = d.UndoServer;
            for (var i = 0; i < plan.Steps; i++)
            {
              var ids = plan.Back ? server.UndoGuids : server.RedoGuids;
              var names = plan.Back ? server.UndoNames : server.RedoNames;
              var topId = ids != null && ids.Count > 0 ? ids[0] : Guid.Empty;
              var topName = names != null && names.Count > 0 ? names[0] : "";
              if (!HistoryCore.StepMatches(plan, i, topId, topName))
              {
                stop = HistoryCore.StoppedLine(plan.Back, taken, plan.Steps, plan.ExpectedNames[i], topName);
                break;
              }
              try
              {
                if (plan.Back) server.PerformUndo(); else server.PerformRedo();
                taken++;
              }
              catch
              {
                stop = HistoryCore.StepFailedLine(plan.Back, taken, plan.Steps, plan.ExpectedNames[i]);
                break;
              }
            }
          }
          catch { stop = stop ?? HistoryCore.StepFailedLine(plan.Back, taken, plan.Steps, ""); }

          var standing = StandingLabels(d);
          Say(done, stop == null ? HistoryCore.Result(plan.Back, taken, standing) : stop + " " + HistoryCore.StandingLine(standing));
        });
      }
      catch { Say(done, HistoryCore.StepFailedLine(p.Plan.Back, 0, p.Plan.Steps, p.Plan.ExpectedNames.FirstOrDefault())); }
    }

    /// <summary>Dwight's changes that stand now, newest first, by the line their row shows.</summary>
    public static IReadOnlyList<string> StandingLabels(GH_Document doc)
    {
      var snap = ChangeLog.Snapshot(doc);
      return CommandHistory.Changes(doc)
        .Where(c => HistoryCore.StateOf(c, snap) == ChangeState.Standing)
        .Select(HistoryCore.RowLine)
        .ToList();
    }

    private static void Say(Action<string> done, string line)
    {
      if (done == null) return;
      var app = Application.Instance;
      Action a = () => { try { done(line); } catch { } };
      if (app != null) app.AsyncInvoke(a); else a();
    }
  }
}
