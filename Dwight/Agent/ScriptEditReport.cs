using System.Net;

namespace Dwight.Agent
{
  /// <summary>
  /// WHAT THE USER READS AFTER AN EDIT, rendered.
  ///
  /// IT EXISTS BECAUSE BORROWING set_value's RENDERER WAS A BUG, and a visible one. The click first called
  /// <c>SetValueReport.NotAppliedHtml</c> for BOTH outcomes - and that method hardcodes the words "Not
  /// applied." in its own markup, so a SUCCESSFUL edit rendered "Not applied. Applied. IT CHANGES WHAT THE
  /// COMPONENT PRODUCES ..." - a report asserting the opposite of what happened, in its first two words.
  ///
  /// THAT IS #680 ONE LAYER OUT: an instrument that borrows another's token inherits its question, and a
  /// RENDERER that carries a claim in its body inherits that claim. <c>NotAppliedHtml</c> is not a neutral
  /// "escape this sentence" helper, however much it looks like one from its signature - it is a sentence
  /// with a hole in it, and the sentence says the change did not happen.
  ///
  /// So the two outcomes get two renderers whose names match what they assert, and the applied one is
  /// tested for NOT containing the other's words. GH-free and Eto-free (ADR-091), so both properties are
  /// asserted rather than reviewed.
  /// </summary>
  internal static class ScriptEditReport
  {
    /// <summary>
    /// THE CHOICE OF RENDERER, as a function, because the choice is where the bug was.
    ///
    /// Fixing the borrowed renderer left the DECISION - which one to call - inline in the GH-coupled click,
    /// where no test can reach it. So reinstating the bug turned NOTHING red: the tests below assert the
    /// renderers are correct and could not assert that the click calls the right one. That is #612 for the
    /// second time today, the first being in #779's own test suite - a guard asserting a function BESIDE
    /// the decision rather than the decision.
    ///
    /// So the mapping lives here, one function, and the click delegates with no choice left in it. A test
    /// can then hold the property that matters: applied never renders the refusal's words.
    /// </summary>
    public static string Html(bool applied, string text) =>
      applied ? AppliedHtml(text) : NotAppliedHtml(text);

    /// <summary>The same choice for the transcript, so the two channels cannot diverge on WHICH outcome
    /// they are describing - only on wording, which is each channel's own business (#624).</summary>
    public static string Text(bool applied, string text) =>
      applied ? AppliedText(text) : NotAppliedText(text);

    /// <summary>The edit landed. The sentence is the one the CARD was drawn from, so what the user reads
    /// afterwards cannot disagree with what they approved - see <c>HandoffSeed.Report</c> for why it is
    /// carried rather than re-rendered.</summary>
    public static string AppliedHtml(string report) =>
      "<p><strong>Applied.</strong> " + WebUtility.HtmlEncode(Trim(report)) + "</p>";

    /// <summary>The edit did not land, and NOTHING WAS CHANGED. Distinct from a revert, which is a
    /// different fact: a revert applied something and put it back, and saying "not applied" about it
    /// would be false about the undo stack (#683 6c's distinction, kept here).</summary>
    public static string NotAppliedHtml(string reason) =>
      "<p><strong>Not applied.</strong> " + WebUtility.HtmlEncode(Trim(reason)) + "</p>";

    /// <summary>The plain-text forms, for the transcript. The model reads these, so they carry the same
    /// words - a screen and a transcript that disagree is how a model comes to believe a change ran
    /// (#624).</summary>
    public static string AppliedText(string report) => "[Applied by Dwight on the user's approval] " + Trim(report);

    public static string NotAppliedText(string reason) => "[Not applied - the canvas is unchanged] " + Trim(reason);

    /// <summary>The applied sentence already begins "Applied." when it comes from the verdict, so the
    /// renderer's own bold "Applied." would double it. Stripped here rather than at the verdict, because
    /// the verdict's sentence has to stand alone in the transcript where there is no bold lead-in.</summary>
    private static string Trim(string s)
    {
      var t = (s ?? "").Trim();
      const string lead = "Applied. ";
      const string notLead = "Not applied. ";
      if (t.StartsWith(lead, System.StringComparison.Ordinal)) return t.Substring(lead.Length);
      if (t.StartsWith(notLead, System.StringComparison.Ordinal)) return t.Substring(notLead.Length);
      return t;
    }
  }
}
