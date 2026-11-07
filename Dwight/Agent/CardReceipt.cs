using System;
using System.Net;
using System.Text.RegularExpressions;

namespace Dwight.Agent
{
  /// <summary>
  /// BRIEF BG: A CARD THAT WAS ANSWERED IS A RECEIPT ROW. One card used to play two roles: the consent surface, which must show everything,
  /// and the record of what happened, which needs one line. A receipt is that line: a glyph for the kind, the act and its scope in one
  /// sentence, the state, and the model's reason as a quiet second line; the whole card as it was sits inside it, folded, so the complete
  /// text stays in the DOM in every state (#338: folded is allowed, removed is not). The shell opens and closes the row on a click.
  ///
  /// The thread's result lines (the verdict, the map laid down, a revert, the close line, the Full control report) take the same shape:
  /// one row that expands to its full text. GH-free (ADR-091).
  /// </summary>
  internal static class CardReceipt
  {
    public const string ApprovedByYou = "approved by you";
    public const string Superseded = "superseded";

    /// <summary>"Approved automatically (Do not ask)." becomes the row's state "approved automatically (Do not ask)".</summary>
    public static string AutoState(string autoStateLine)
    {
      var s = (autoStateLine ?? "").Trim().TrimEnd('.');
      return s.Length == 0 ? s : char.ToLowerInvariant(s[0]) + s.Substring(1);
    }

    /// <summary>The glyphs, inline SVG in the shared stroke style. Fixed plugin markup, never model text.</summary>
    public static string Glyph(string key)
    {
      string name;
      switch (key)
      {
        case "build": name = "build"; break;
        case "run_script": name = "full"; break;
        case "edit": name = "edit"; break;
        case "edit_script": name = "code"; break;
        case "set_value": name = "slider"; break;
        case "scaffold": case "layout": name = "reorganise"; break;
        case "check": name = "check"; break;
        case "revert": name = "undo"; break;
        case "close": name = "flag"; break;
        case "report": name = "file"; break;
        default: name = "dot"; break;
      }
      return "<svg class=\"ic\" aria-hidden=\"true\"><use href=\"#ic-" + name + "\"/></svg>";
    }

    public static bool IsReceipt(string html) =>
      html != null && html.TrimStart().StartsWith("<div class=\"receipt", StringComparison.Ordinal);

    private static readonly Regex KindRx = new Regex("data-kind=\"([^\"]*)\"", RegexOptions.CultureInvariant);
    private static readonly Regex ActRx = new Regex("data-act=\"([^\"]*)\"", RegexOptions.CultureInvariant);
    private static readonly Regex WhyRx = new Regex("<span class=\"consent-why-label\">[^<]*</span>([^<]*)</div>", RegexOptions.CultureInvariant);

    /// <summary>
    /// THE ANSWERED CARD AS A RECEIPT. Everything the row shows is read off the card Dwight itself rendered (its kind, its act, its reason),
    /// so nothing new is computed and a reopened thread renders the same row. A card already a receipt is returned as it is; text that is
    /// not one of Dwight's cards is returned unchanged.
    /// </summary>
    public static string Of(string cardHtml, string state)
    {
      if (string.IsNullOrEmpty(cardHtml) || IsReceipt(cardHtml)) return cardHtml;
      var kind = KindRx.Match(cardHtml);
      var act = ActRx.Match(cardHtml);
      if (!kind.Success || !act.Success) return cardHtml;
      var why = WhyRx.Match(cardHtml);
      return Row(Glyph(WebUtility.HtmlDecode(kind.Groups[1].Value)), act.Groups[1].Value, state, StateClass(state),
                 why.Success ? why.Groups[1].Value : "", cardHtml);
    }

    /// <summary>
    /// A RESULT LINE AS A RECEIPT: the verdict, the map laid down, a revert, the close line, the Full control report. The sentence is the
    /// first sentence of the plugin's own line (its full text is inside), and the state is the outcome's word.
    /// </summary>
    public static string Line(string glyph, string state, string fullHtml)
    {
      if (string.IsNullOrEmpty(fullHtml) || IsReceipt(fullHtml)) return fullHtml;
      return Row(Glyph(glyph), WebUtility.HtmlEncode(FirstSentence(fullHtml)), state, StateClass(state), "", fullHtml);
    }

    /// <summary>
    /// A region's running line as a receipt: built, built with errors, reverted, not clean. A REFUSAL stays prose: the user must read it
    /// (the brief's rule for questions and refusals), so it is returned unchanged.
    /// </summary>
    public static string BuildLine(RegionOutcome outcome, string html)
    {
      return OutcomeWords(outcome, out var glyph, out var state) ? Line(glyph, state, html) : html;
    }

    /// <summary>The glyph and the state word an outcome's receipt carries. False for a refusal, which has none: it stays prose.</summary>
    private static bool OutcomeWords(RegionOutcome outcome, out string glyph, out string state)
    {
      switch (outcome)
      {
        case RegionOutcome.Built: glyph = "check"; state = "built"; return true;
        case RegionOutcome.BuiltWithErrors: glyph = "check"; state = "built, with errors"; return true;
        case RegionOutcome.Reverted: glyph = "revert"; state = "reverted"; return true;
        case RegionOutcome.RevertedWithResidue: glyph = "revert"; state = "not clean"; return true;
        default: glyph = null; state = null; return false;
      }
    }

    /// <summary>
    /// U110: A SPOT BUILD'S VERDICT, IN THE SAME SHAPE AND STATE WORDS AS A REGION'S. The outcome is read from the build's own envelope by
    /// the one classifier the region path uses (<see cref="BuildEnvelope.Classify"/>), never from the text. A spot's report opens "Reverted."
    /// where a region's line does not, and the state already says so, so the row's sentence is the one after it: what happened. A refusal, and
    /// an envelope that cannot be read, stay prose.
    /// </summary>
    public static string SpotLine(ToolResult result, string args, string html)
    {
      var envelope = result == null ? null : BuildEnvelope.Classify(result.ToJson(), args);
      if (envelope == null || !OutcomeWords(envelope.Outcome, out var glyph, out var state)) return html;
      if (string.IsNullOrEmpty(html) || IsReceipt(html)) return html;
      var text = FirstSentence(html);
      if (string.Equals(text, RevertedLead, StringComparison.Ordinal))
        text = FirstSentence(html.Replace("<strong>" + RevertedLead + ".</strong>", ""));
      return Row(Glyph(glyph), WebUtility.HtmlEncode(text), state, StateClass(state), "", html);
    }

    /// <summary>The word <see cref="SpotAsk.FailedHtml"/> opens a reverted spot's report with.</summary>
    private const string RevertedLead = "Reverted";

    /// <summary>The close line as a receipt: "done", or "stopped" when the turn did not finish.</summary>
    public static string CloseLine(string html, bool stoppedEarly) => Line("close", stoppedEarly ? "stopped" : "done", html);

    /// <summary>The map laid down, as a receipt.</summary>
    public static string MapLine(string html) => Line("scaffold", "laid down", html);

    /// <summary>The Full control report, as a receipt. Its state says what the report is: nothing checked it.</summary>
    public static string ReportLine(string html) => Line("report", "not checked", html);

    /// <summary>The words of the first sentence of some plugin HTML, its markup removed. Never cut inside a word.</summary>
    public static string FirstSentence(string html)
    {
      var text = WebUtility.HtmlDecode(Regex.Replace(html ?? "", "<[^>]+>", " "));
      text = Regex.Replace(text, "\\s+", " ").Trim();
      var cut = text.IndexOf(". ", StringComparison.Ordinal);
      var first = cut > 0 ? text.Substring(0, cut) : text.TrimEnd('.');
      return first.Length <= 140 ? first : first.Substring(0, first.LastIndexOf(' ', 139) > 0 ? first.LastIndexOf(' ', 139) : 139) + "...";
    }

    /// <summary>The colour a state is drawn in: approved and checked in the accent, a revert or something not checked as a warning.</summary>
    public static string StateClass(string state)
    {
      var s = state ?? "";
      if (s.StartsWith("approved", StringComparison.Ordinal) || s == "checked" || s == "built" || s == "laid down") return "ok";
      if (s == "reverted" || s == "not checked" || s == "not clean") return "warn";
      return "";
    }

    /// <param name="act">ALREADY ENCODED (read off the card's attribute, or encoded by the caller).</param>
    /// <param name="why">ALREADY ENCODED.</param>
    private static string Row(string glyph, string act, string state, string stateCls, string why, string full)
    {
      var stateHtml = "<span class=\"rc-state" + (string.IsNullOrEmpty(stateCls) ? "" : " " + stateCls) + "\">" +
                      WebUtility.HtmlEncode(state ?? "") + "</span>";
      var hasWhy = !string.IsNullOrWhiteSpace(why);
      return "<div class=\"receipt\" tabindex=\"0\" role=\"button\" aria-expanded=\"false\" data-state=\"" + WebUtility.HtmlEncode(state ?? "") + "\">" +
             "<div class=\"rc-row\">" + glyph + "<span class=\"rc-text\">" + act + "</span>" + (hasWhy ? "" : stateHtml) + "</div>" +
             (hasWhy ? "<div class=\"rc-why\">" + stateHtml + " " + why + "</div>" : "") +   // a real space: the margin is only for the eye, and text read aloud or copied would weld the two
             "<div class=\"rc-full\">" + full + "</div></div>";
    }

    /// <summary>
    /// The host's rewrite: the stored message that holds a card minted with <paramref name="token"/> becomes its receipt with
    /// <paramref name="state"/>. Null when the message does not hold that token (so the caller leaves it untouched).
    /// </summary>
    public static string Answer(string storedHtml, string token, string state)
    {
      if (string.IsNullOrEmpty(storedHtml) || string.IsNullOrEmpty(token) || IsReceipt(storedHtml)) return null;
      if (storedHtml.IndexOf("consent:" + token, StringComparison.Ordinal) < 0) return null;
      var receipt = Of(storedHtml, state);
      return ReferenceEquals(receipt, storedHtml) || receipt == storedHtml ? null : receipt;
    }
  }
}
