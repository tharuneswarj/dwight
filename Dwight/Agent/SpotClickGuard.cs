using System;
using System.Threading;

namespace Dwight.Agent
{
  /// <summary>
  /// A CLICK THAT CHANGES THE CANVAS AND SAYS NOTHING IS THE ONE THING THE CONSENT MODEL FORBIDS (U47; #682, #338).
  ///
  /// The U19 gate (row 3b) approved a script whose fed input produced nothing. The click logged "APPROVED" and then
  /// NOTHING: no report, no turn, no toast - and the build was left standing, unverified. Whatever threw, it threw where
  /// no catch was waiting, and Grasshopper swallows an exception out of an event handler without a word.
  ///
  /// So the spot click runs inside this guard. An exception ANYWHERE in the verdict path or in the report that follows it
  /// yields (1) a revert, when the build is still standing, and (2) a report in Dwight's voice that carries the real
  /// message - never silence. GH-free (ADR-091): the three effects arrive as delegates, so the guarantee is asserted
  /// rather than reviewed, and the GH-coupled shell only supplies the undo-count probe and the report door.
  ///
  /// THE REVERT IS CONDITIONAL ON THE BUILD STILL STANDING. A double undo would take back the user's own previous step,
  /// which is worse than the silence it replaces; the shell's <c>standing</c> probe compares the document's undo count
  /// with its value before the build ran.
  /// </summary>
  internal static class SpotClickGuard
  {
    /// <summary>
    /// ONE REPORT PER BUILD, WHOEVER GETS THERE FIRST (U47, second pass). The verdict arrives from a SolutionEnd handler; the
    /// watchdog arrives from a timer. If both are allowed to speak, a late verdict reports a build the watchdog has already
    /// taken back - or reverts it twice, which undoes the user's own previous step. The first claim wins, the second is
    /// told so and does nothing.
    /// </summary>
    internal sealed class ReportLatch
    {
      private int _claimed;

      public bool Delivered => Volatile.Read(ref _claimed) == 1;

      /// <summary>True exactly once: for the first caller.</summary>
      public bool TryClaim() => Interlocked.CompareExchange(ref _claimed, 1, 0) == 0;
    }

    /// <summary>
    /// THE WATCHDOG'S WAY INTO THE GUARD: a check that never finished is an exception the guard reports in its own words and
    /// reverts for, exactly as one that threw. The timeout path and the throw path are one path, so the report line is
    /// asserted once, not twice.
    /// </summary>
    public static bool TimedOut(int seconds, Func<bool> standing, Action revert, Action<string> report) =>
      Run(() => throw new TimeoutException("the check did not finish within " + seconds + " seconds"), standing, revert, report);

    /// <summary>What the user (and, through the report door, the model) reads when the check itself failed.</summary>
    public static string ReportLine(string message, bool reverted, bool revertFailed = false)
    {
      var m = (message ?? "").Trim();
      if (m.Length == 0) m = "no further detail";
      m = m.TrimEnd('.', ' ');
      return "Dwight hit a problem while checking the build: " + m + ". " +
             (reverted ? "The build was reverted - the canvas is as it was."
               : revertFailed ? "Dwight could not take the build back itself - press Ctrl-Z once to undo it."
               : "The canvas is as it was.");
    }

    /// <summary>
    /// Run <paramref name="body"/>. If it throws: revert when <paramref name="standing"/> says the build is still on the
    /// canvas, then report. Neither effect may throw out of here - a guard whose own catch can be silent is the defect
    /// it guards against. Returns true when the body completed.
    /// </summary>
    public static bool Run(Action body, Func<bool> standing, Action revert, Action<string> report)
    {
      try { body(); return true; }
      catch (Exception ex)
      {
        var reverted = false;
        var failed = false;
        try
        {
          if (standing != null && standing()) { revert?.Invoke(); reverted = true; }
        }
        catch { failed = true; /* the line below must not claim a revert that did not happen */ }

        try { report?.Invoke(ReportLine(ex.Message, reverted, failed)); } catch { }
        return false;
      }
    }
  }
}
