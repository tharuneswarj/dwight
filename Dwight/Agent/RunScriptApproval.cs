using System;

namespace Dwight.Agent
{
  /// <summary>
  /// U100: THE APPROVAL RUNS THE GRANT. The approved source is on the card's own record, so nothing the model says afterwards is
  /// needed to run it - U18's law, "the record is the spec", applied to Full control. On approval of a <c>run_script</c> card the plugin
  /// runs the grant itself, as <c>ApplySpot</c> applies a spot card and the SetValue route applies a slider: no model call sits between
  /// the click and the run. The report goes into the thread as the plugin's line (on screen, and as a transcript line the model reads),
  /// and ONE read-only turn follows so the model can read what changed and carry on.
  ///
  /// Before this, the approval started a turn in which the model had to call <c>run_script</c> again with the identical source. At the
  /// gate (row D) Sonnet 5 never did across three approvals: twice it sent a different text, which the exact-text gate refused, and
  /// twice it raised a fresh card. The sentence promised a run that never came.
  ///
  /// GH-free (ADR-091): the words and the decisions; the run itself is <c>GhRunScript.Run</c>, called from the click.
  /// </summary>
  internal static class RunScriptApproval
  {
    /// <summary>Said on approval, before the run. The stop clause is on the card's authority line, where it is read before the click.</summary>
    public const string ApprovedLine = "Running the script you approved.";

    /// <summary>Brief CD: nobody clicked, so nothing says they did. The mode is what they chose.</summary>
    public const string AutoRunLine = "Running the script, approved automatically because this chat is on Do not ask.";

    private static string OnWhoseWord(bool automatic) => automatic ? "automatically, under this chat's Do not ask mode" : "on the user's approval";

    /// <summary>Said when the approval was redeemed but the conversation no longer allows a script, or the record lost its source.</summary>
    public const string NotRunLine =
      "Dwight did not run the script: Full control is not on for this conversation, or the approved source was lost.";

    /// <summary>Said when one approval is redeemed a second time. One approval covers one run.</summary>
    public const string UsedLine = "That approval covered one run and it has been used. Ask again if another script is needed.";

    /// <summary>The panel's line for a run that came back: the probe's sentence when nothing ran, the raised report when the script
    /// failed, the change report otherwise. Never "verified".</summary>
    public static string PanelLine(string purpose, RunOutcome o)
    {
      if (o == null) return "The script did not report. " + FullControl.UndoClause;
      if (o.Unavailable != null) return o.Unavailable.Trim() + " Nothing was run, and nothing on the canvas changed.";
      return FullControl.ReportFor(purpose, o);
    }

    /// <summary>The same report as the transcript line the follow-up turn reads at its start: who ran it and on whose word, then the
    /// report, then what to do with it. A script that never started says so, so the model does not believe it ran.</summary>
    public static string TranscriptLine(RunOutcome o, string panelLine, bool automatic = false)
    {
      var line = (panelLine ?? "").Trim();
      if (o == null || o.Unavailable != null)
        return (automatic ? "[Not run: this script was approved automatically, and Dwight ran nothing] " : "[Not run: the user approved this script, and Dwight ran nothing] ") + line;
      return "[Run by Dwight " + OnWhoseWord(automatic) + "] " + line +
             " This is a report of what changed, not a check. Read what changed, tell the user in your own words, and do not call it verified.";
    }

    /// <summary>The panel's line when the clock ran out first. The late finish is reported when it comes.</summary>
    public static string OverranLine(string purpose, int timeoutSeconds) => FullControl.OverranReport(purpose, timeoutSeconds);

    public static string OverranTranscript(string purpose, int timeoutSeconds, bool automatic = false) =>
      "[Run by Dwight " + OnWhoseWord(automatic) + ", still running] " + OverranLine(purpose, timeoutSeconds) +
      " Do not ask to run it again; Dwight reports the finish when it comes.";

    /// <summary>Whether the one read-only follow-up turn runs: after a script that ran, whether it finished or raised (things may have
    /// changed either way). Not after a script that never started (nothing changed) or one still running (the canvas is mid-change).</summary>
    public static bool FollowUp(RunOutcome o, bool overran) => !overran && o != null && o.Unavailable == null;
  }
}
