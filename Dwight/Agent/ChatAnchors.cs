using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;

namespace Dwight.Agent
{
  /// <summary>What resolving a message's anchors against the CURRENT canvas produced.</summary>
  internal enum AnchorOutcome
  {
    /// <summary>Nothing to resolve - the message named no components.</summary>
    Nothing = 0,
    /// <summary>Every named component is still there.</summary>
    Landed = 1,
    /// <summary>Some are still there and some are gone. The survivors are shown AND the loss is stated.</summary>
    PartlyRetired = 2,
    /// <summary>They are all gone. Nothing moves, and the user is told why.</summary>
    AllRetired = 3,
  }

  internal sealed class AnchorResolution
  {
    public AnchorOutcome Outcome { get; set; } = AnchorOutcome.Nothing;
    public IReadOnlyList<int> Alive { get; set; } = new int[0];
    public IReadOnlyList<int> Retired { get; set; } = new int[0];

    /// <summary>What to tell the user, in their language. Empty when there is nothing to say.</summary>
    public string Message { get; set; } = "";
  }

  /// <summary>
  /// Making the conversation navigable on the canvas (#291) - the ids the agent writes in its prose become
  /// places you can go, and stay honest when the canvas moves on.
  ///
  /// THE ONE RULE THAT MAKES IT SAFE: AN ANCHOR IS AN AGENT ID. Never a guid, never a coordinate, never an
  /// index into anything. That is not a preference - it is what closes the mis-grounding class at a UI
  /// surface. Resolution is id -> registry -> guid -> is-it-in-the-document-right-now, which either finds
  /// the object or finds nothing; there is no "roughly where it was" to drift to. A control that silently
  /// takes you somewhere plausible is the panel claiming what the canvas contradicts, in navigation form,
  /// and the absence of a positional fallback is what makes it impossible rather than merely unlikely
  /// (#208's shape: resolve exactly or refuse).
  ///
  /// AND THE SECOND RULE, WHICH IS ITS MIRROR: only ids the registry has ISSUED become clickable at all. A
  /// model writing "(9999)" gets plain text, because an affordance is an assertion that its target exists -
  /// offering a link to a component that never did is the same defect inverted.
  ///
  /// #322: ONLY THE PARENTHESISED FORM. The agent writes prose full of numbers that are not ids - "a 4 by 3
  /// grid", "10 units apart", "12 polygons" - and on a few-hundred-component definition most small integers
  /// ARE issued ids. Linkifying bare digits would turn measurements into navigation to unrelated
  /// components. The parenthesised convention is already canon (ADR-079, mirrored by the canvas badges).
  /// </summary>
  internal static class ChatAnchors
  {
    /// <summary>
    /// The ids a message refers to: the parenthesised form only, de-duplicated, in the order written, and
    /// filtered to ids the registry has actually issued.
    /// </summary>
    public static IReadOnlyList<int> Extract(string text, Func<int, IdState> state)
    {
      var found = new List<int>();
      var seen = new HashSet<int>();
      foreach (var id in Parenthesised(text ?? ""))
      {
        if (!seen.Add(id)) continue;
        if (state != null && state(id) == IdState.Unknown) continue;   // never issued: not an anchor
        found.Add(id);
      }
      return found;
    }

    /// <summary>
    /// Where a click should take the user, or why it cannot. Honest across time by construction: this reads
    /// the CURRENT state, so a message from an hour ago resolves against the canvas as it is now.
    /// </summary>
    public static AnchorResolution Resolve(IEnumerable<int> ids, Func<int, IdState> state)
    {
      var alive = new List<int>();
      var retired = new List<int>();

      foreach (var id in (ids ?? Enumerable.Empty<int>()).Distinct())
      {
        var s = state == null ? IdState.Unknown : state(id);
        if (s == IdState.Alive) alive.Add(id);
        else retired.Add(id);   // retired OR never issued - either way there is nowhere to go
      }

      var r = new AnchorResolution { Alive = alive, Retired = retired };

      if (alive.Count == 0 && retired.Count == 0) { r.Outcome = AnchorOutcome.Nothing; return r; }

      if (alive.Count == 0)
      {
        r.Outcome = AnchorOutcome.AllRetired;
        r.Message = retired.Count == 1
          ? "That component is no longer on the canvas."
          : "Those components are no longer on the canvas.";
        return r;
      }

      if (retired.Count == 0) { r.Outcome = AnchorOutcome.Landed; return r; }

      // The case that must never be silent: showing fewer than the message named, without saying so, is a
      // surface quietly disagreeing with itself.
      r.Outcome = AnchorOutcome.PartlyRetired;
      r.Message = retired.Count == 1
        ? "One of those components is no longer on the canvas; showing the rest."
        : retired.Count + " of those components are no longer on the canvas; showing the rest.";
      return r;
    }

    /// <summary>
    /// Turn "(42)" into a click target, in ALREADY-ESCAPED html.
    ///
    /// It runs over the OUTPUT of the Markdown renderer, never over model text, so nothing the model wrote
    /// can become markup here (ADR-088 intact) - the only markup introduced is ours, and only around a run
    /// of digits we have already checked against the registry.
    ///
    /// Two exclusions, both deliberate: never inside a tag (an id-looking number in an attribute is not
    /// prose), and never inside code or pre (a number in a code sample is not a component reference).
    /// </summary>
    public static string Linkify(string html, Func<int, IdState> state)
    {
      if (string.IsNullOrEmpty(html)) return html ?? "";

      var sb = new StringBuilder(html.Length + 64);
      var i = 0;
      var codeDepth = 0;

      while (i < html.Length)
      {
        var c = html[i];

        if (c == '<')
        {
          var end = html.IndexOf('>', i);
          if (end < 0) { sb.Append(html, i, html.Length - i); break; }
          var tag = html.Substring(i, end - i + 1);
          if (IsTag(tag, "code") || IsTag(tag, "pre")) codeDepth++;
          else if (IsCloseTag(tag, "code") || IsCloseTag(tag, "pre")) codeDepth = Math.Max(0, codeDepth - 1);
          sb.Append(tag);
          i = end + 1;
          continue;
        }

        if (c == '(' && codeDepth == 0)
        {
          var j = i + 1;
          while (j < html.Length && char.IsDigit(html[j])) j++;
          if (j > i + 1 && j < html.Length && html[j] == ')' &&
              int.TryParse(html.Substring(i + 1, j - i - 1), out var id) &&
              (state == null || state(id) != IdState.Unknown))
          {
            // "anchor:", NOT "locate:" - the shell already had a locate: command carrying a Simplify
            // CHANGE INDEX. Reusing it would have sent a click on component (3) to the handler for the
            // third change, which is a wrong destination that nothing would have reported.
            sb.Append("<a class=\"anch\" href=\"#\" onclick=\"dwightSend('anchor:")
              .Append(id)
              .Append("');return false;\">(")
              .Append(id)
              .Append(")</a>");
            i = j + 1;
            continue;
          }
        }

        sb.Append(c);
        i++;
      }

      return sb.ToString();
    }

    // ---- helpers -------------------------------------------------------------------------------

    private static IEnumerable<int> Parenthesised(string text)
    {
      for (var i = 0; i < text.Length; i++)
      {
        if (text[i] != '(') continue;
        var j = i + 1;
        while (j < text.Length && char.IsDigit(text[j])) j++;
        if (j > i + 1 && j < text.Length && text[j] == ')' &&
            int.TryParse(text.Substring(i + 1, j - i - 1), out var id))
        {
          yield return id;
          i = j;
        }
      }
    }

    private static bool IsTag(string tag, string name) =>
      tag.Length > name.Length + 1 &&
      tag.StartsWith("<" + name, StringComparison.OrdinalIgnoreCase) &&
      (tag[name.Length + 1] == '>' || tag[name.Length + 1] == ' ');

    private static bool IsCloseTag(string tag, string name) =>
      tag.StartsWith("</" + name, StringComparison.OrdinalIgnoreCase);

    // The graduation context line (#365) lived here as chrome the model never read. Brief EC unit 5b moved it to the model door, written in Markdown by
    // RunHandOff.ContextLine, so its ids reach the screen through Linkify like any of Dwight's words, and this file stays the anchor's one home.
  }
}
