using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Dwight.Session
{
  /// <summary>
  /// THE SIZE-AWARE THREAD BOUND (Phase-8 WP-3 Stage 0; the thrice-deferred item from #170/#182).
  ///
  /// <c>MaxMessagesPerThread</c> guards a runaway COUNT, which is the wrong axis for the risk that
  /// actually arrives with the agent: one agent message can be large on its own, and twenty threads of
  /// large messages are re-read from disk on every document open. A count cap cannot see that.
  ///
  /// TWO BOUNDS, AND EACH DEGRADES HONESTLY:
  ///   - PER MESSAGE, an over-long message is TRUNCATED WITH A VISIBLE MARKER, never dropped. Dropping
  ///     would make a conversation silently discontinuous, which is the failure this store exists to
  ///     prevent; truncating says so on screen.
  ///   - PER THREAD, the oldest messages are trimmed from the HEAD, exactly as the count cap already
  ///     does, so a long conversation keeps its LIVE TAIL rather than its opening. The last message is
  ///     never trimmed away: a thread cannot be emptied by a byte cap.
  ///
  /// WHAT IS DELIBERATELY EXEMPT: <c>scaffoldMap</c> and <c>buildProgress</c> sit OUTSIDE the message
  /// list (#167), so no amount of trimming can lose the plan. That exemption is the reason the caps can
  /// be this blunt - the artifact worth protecting is not in the trimmed collection.
  ///
  /// HTML-SAFE TRUNCATION IS NOT OPTIONAL HERE. A stored message is HTML, and the shell appends it with
  /// <c>insertAdjacentHTML</c>. Cutting a string mid-tag - or mid-element - leaves an unclosed
  /// <c>&lt;div&gt;</c> that swallows every message rendered after it, so a size bound applied naively
  /// would corrupt the whole thread's rendering to save a few kilobytes. The cut is therefore taken at
  /// an element boundary and any still-open elements are closed.
  ///
  /// GH-free and headless-tested (ADR-091).
  /// </summary>
  internal static class ChatBudget
  {
    /// <summary>
    /// Headroom held back from the content so the marker AND the closing tags fit INSIDE the budget.
    ///
    /// Without it the bound is not idempotent: the closers and the marker push the result back over
    /// <c>maxChars</c>, so the next <c>Normalize</c> - and <c>Normalize</c> runs on read AND write -
    /// truncates the already-truncated message and appends a SECOND marker, shrinking the message a
    /// little on every save. Reserving the overhead up front makes a trimmed message a fixed point.
    ///
    /// Sized for the marker (~120 characters including the length digits) plus a generous element nesting
    /// depth - roughly 15-20 levels against the 6-8 that Markdig output and Dwight's own cards actually
    /// reach.
    ///
    /// RECORDED LIMIT, stated precisely rather than kindly: nesting deeper than the reserve covers still
    /// produces WELL-FORMED output, and it does converge - but it converges by being truncated a second
    /// time on the next cycle and STACKING A SECOND MARKER. That is a visible artefact, not a clean
    /// settle. Unreachable from either real author, so no guard is bought for it; the behaviour is
    /// written down so a future reader meets it as a known bound rather than as a bug.
    /// </summary>
    private const int Reserve = 256;

    /// <summary>Elements that carry no closing tag, so they never enter the open-element stack.</summary>
    private static readonly HashSet<string> Void = new HashSet<string>
    {
      "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param", "source",
      "track", "wbr",
    };

    /// <summary>
    /// Truncate one message's HTML to <paramref name="maxChars"/>, well-formed, with an honest marker.
    /// Idempotent: a value already within the budget is returned unchanged, so running on both read and
    /// write cannot compound.
    ///
    /// PRECONDITION, load-bearing for a function whose whole job is well-formedness: attribute values are
    /// ENTITY-ESCAPED. The scan finds a tag's end with <c>IndexOf('&gt;')</c>, so a literal <c>&gt;</c>
    /// inside an attribute value would end the tag early, mis-read the element name, and could emit one
    /// unbalanced closing tag. Both real authors satisfy it - Markdig escapes attribute content, and the
    /// plugin door writes Dwight's own simple cards - so this is a stated precondition rather than a
    /// defect, and a future author who breaks it has been warned in the one place it matters.
    /// </summary>
    public static string TrimHtml(string html, int maxChars) => TrimHtml(html, maxChars, out _);

    public static string TrimHtml(string html, int maxChars, out bool truncated)
    {
      truncated = false;
      if (string.IsNullOrEmpty(html) || maxChars <= 0 || html.Length <= maxChars) return html ?? "";
      truncated = true;

      var content = maxChars > Reserve ? maxChars - Reserve : maxChars;   // headroom for closers + marker
      var open = new List<string>();
      var cut = 0;      // the last index (exclusive) it is safe to cut at - never inside a tag
      var i = 0;

      while (i < html.Length && i < content)
      {
        if (html[i] != '<') { i++; cut = i; continue; }

        var close = html.IndexOf('>', i);
        if (close < 0) break;             // an unterminated tag: everything from here is unsafe
        if (close >= content) break;      // the tag would straddle the budget: cut before it

        var tag = html.Substring(i + 1, close - i - 1).Trim();
        if (tag.Length > 0 && tag[0] != '!' && tag[0] != '?')   // skip comments and declarations wholesale
        {
          if (tag[0] == '/')
          {
            var name = NameOf(tag.Substring(1));
            for (var s = open.Count - 1; s >= 0; s--)
              if (open[s] == name) { open.RemoveRange(s, open.Count - s); break; }
          }
          else if (!tag.EndsWith("/"))
          {
            var name = NameOf(tag);
            if (name.Length > 0 && !Void.Contains(name)) open.Add(name);
          }
        }

        i = close + 1;
        cut = i;
      }

      var sb = new StringBuilder(html.Substring(0, cut));
      for (var s = open.Count - 1; s >= 0; s--) sb.Append("</").Append(open[s]).Append('>');
      sb.Append("<p><em>Dwight kept the first part of this message (it ran to ")
        .Append(html.Length)
        .Append(" characters); the rest is not stored.</em></p>");
      return sb.ToString();
    }

    /// <summary>Head-trim a thread's messages until the whole thread fits <paramref name="maxChars"/>,
    /// keeping the live tail. The last message always survives - an empty thread would be a worse lie
    /// than a short one.</summary>
    public static List<StoredMessage> TrimThread(List<StoredMessage> messages, int maxChars)
    {
      var msgs = messages ?? new List<StoredMessage>();
      if (msgs.Count <= 1 || maxChars <= 0) return msgs;

      long total = 0;
      foreach (var m in msgs) total += Size(m);
      if (total <= maxChars) return msgs;

      var start = 0;
      while (start < msgs.Count - 1 && total > maxChars) { total -= Size(msgs[start]); start++; }
      return start == 0 ? msgs : msgs.Skip(start).ToList();
    }

    /// <summary>Truncate plain text (a message's SOURCE) with the same honest marker. No tag balancing:
    /// it is not markup, and it is never injected - it goes back to the model, not to the shell.</summary>
    public static string TrimText(string text, int maxChars)
    {
      if (string.IsNullOrEmpty(text) || maxChars <= 0 || text.Length <= maxChars) return text ?? "";
      var content = maxChars > Reserve ? maxChars - Reserve : maxChars;
      return text.Substring(0, content) +
             "\n\n[Dwight kept the first part of this message (it ran to " + text.Length +
             " characters); the rest is not stored.]";
    }

    /// <summary>What one message costs the file. The timestamp and author are small but real, and a
    /// bound that ignores them under-counts a thread of many short messages. The SOURCE counts too - it
    /// is stored beside the rendered HTML and is what the next turn resends.</summary>
    public static int Size(StoredMessage m) =>
      m == null ? 0 : (m.Html?.Length ?? 0) + (m.Src?.Length ?? 0) + (m.Who?.Length ?? 0) + (m.Utc?.Length ?? 0) + (m.Image?.Sha256?.Length ?? 0);   // Brief DX: an image costs the file its hash, never its bytes

    /// <summary>The element name from a tag's interior, lower-cased.</summary>
    private static string NameOf(string tagInterior)
    {
      var n = 0;
      while (n < tagInterior.Length && !char.IsWhiteSpace(tagInterior[n]) && tagInterior[n] != '/') n++;
      return tagInterior.Substring(0, n).ToLowerInvariant();
    }
  }
}
