using System;
using System.Collections.Generic;
using System.Text;

namespace Dwight.Agent
{
  /// <summary>
  /// A RUN'S NUMBERS AND A CHAT'S NUMBERS ARE TWO NUMBERINGS OF ONE CANVAS (Brief EC unit 5, Tharun's sitting of 2 November 2026). A verb's result names
  /// components by its own snapshot's short ids, "the accessDirection text (395)"; a chat names the same components by its registry's, 5096. The seventeenth
  /// put the first into the second: the chat read 395 as its own 395 and told the person their step was wrong. An id shown to a model is the plugin's,
  /// translated through the guid, or it is not shown (ADR-058).
  ///
  /// So every carried line goes through here. A bracketed id the run issued becomes the chat's id for the same guid. One the chat cannot place (the component
  /// has gone), and a bracketed number the run never issued, lose the bracket and keep the words around them, because a number left in brackets is an anchor
  /// to a reader of the chat and would point somewhere plausible and wrong. Only the parenthesised form is an id (#322), and never inside code.
  /// GH-free (ADR-091).
  /// </summary>
  internal static class RunIds
  {
    /// <summary>The translation from the run's numbering to the chat's: run id, to the guid the run gave it, to the chat's id for that guid; null where either step fails.</summary>
    public static Func<int, int?> Through(IReadOnlyDictionary<Guid, int> runLabels, Func<Guid, int?> chatIdOf)
    {
      var guidOf = new Dictionary<int, Guid>();
      if (runLabels != null) foreach (var kv in runLabels) guidOf[kv.Value] = kv.Key;
      return id => chatIdOf != null && guidOf.TryGetValue(id, out var g) ? chatIdOf(g) : null;
    }

    /// <summary>Rendered HTML, as a step card holds its sentence: tags are copied as they are, and nothing inside <c>code</c> or <c>pre</c> is touched.</summary>
    public static string RewriteHtml(string html, Func<int, int?> chatId)
    {
      if (string.IsNullOrEmpty(html)) return html ?? "";
      var sb = new StringBuilder(html.Length);
      var code = 0;
      var i = 0;
      while (i < html.Length)
      {
        var c = html[i];
        if (c == '<')
        {
          var end = html.IndexOf('>', i);
          if (end < 0) { sb.Append(html, i, html.Length - i); break; }
          var tag = html.Substring(i, end - i + 1).ToLowerInvariant();
          if (tag.StartsWith("<code", StringComparison.Ordinal) || tag.StartsWith("<pre", StringComparison.Ordinal)) code++;
          else if (tag.StartsWith("</code", StringComparison.Ordinal) || tag.StartsWith("</pre", StringComparison.Ordinal)) code = Math.Max(0, code - 1);
          sb.Append(html, i, end - i + 1);
          i = end + 1;
          continue;
        }
        if (c == '(' && code == 0 && TryIds(html, i, out var ids, out var close)) { Emit(sb, ids, chatId); i = close + 1; continue; }
        sb.Append(c);
        i++;
      }
      return sb.ToString();
    }

    /// <summary>Markdown, as a verb's result and a step's own text are kept: a code span or a fenced block is copied as it is.</summary>
    public static string RewriteMarkdown(string markdown, Func<int, int?> chatId)
    {
      if (string.IsNullOrEmpty(markdown)) return markdown ?? "";
      var sb = new StringBuilder(markdown.Length);
      var i = 0;
      while (i < markdown.Length)
      {
        var c = markdown[i];
        if (c == '`')
        {
          var run = 0;
          while (i + run < markdown.Length && markdown[i + run] == '`') run++;
          var fence = new string('`', run);
          var shut = markdown.IndexOf(fence, i + run, StringComparison.Ordinal);
          var end = shut < 0 ? i + run : shut + run;
          sb.Append(markdown, i, end - i);
          i = end;
          continue;
        }
        if (c == '(' && TryIds(markdown, i, out var ids, out var close)) { Emit(sb, ids, chatId); i = close + 1; continue; }
        sb.Append(c);
        i++;
      }
      return sb.ToString();
    }

    /// <summary>"(42)" or "(42, 43)" at <paramref name="open"/>, not straight after a letter or a digit, so "f(3)" is a call and not an id.</summary>
    private static bool TryIds(string text, int open, out List<int> ids, out int close)
    {
      ids = new List<int>();
      close = -1;
      if (open > 0 && char.IsLetterOrDigit(text[open - 1])) return false;
      var j = open + 1;
      while (true)
      {
        var start = j;
        while (j < text.Length && char.IsDigit(text[j])) j++;
        if (j == start || j - start > 9 || !int.TryParse(text.Substring(start, j - start), out var id)) return false;
        ids.Add(id);
        if (j < text.Length && text[j] == ')') { close = j; return true; }
        if (j + 1 < text.Length && text[j] == ',' && text[j + 1] == ' ') { j += 2; continue; }
        if (j < text.Length && text[j] == ',') { j += 1; continue; }
        return false;
      }
    }

    private static void Emit(StringBuilder sb, List<int> ids, Func<int, int?> chatId)
    {
      var placed = new List<int>();
      foreach (var id in ids)
      {
        var to = chatId?.Invoke(id);
        if (to.HasValue && !placed.Contains(to.Value)) placed.Add(to.Value);
      }
      if (placed.Count > 0) { sb.Append('(').Append(string.Join(", ", placed)).Append(')'); return; }
      // Nothing to show: the bracket goes, and the space before it with it, so "Stream Filter (425) does" reads "Stream Filter does".
      if (sb.Length > 0 && sb[sb.Length - 1] == ' ') sb.Length--;
    }
  }
}
