using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Dwight.Session
{
  /// <summary>How one run of a note's text is drawn.</summary>
  internal enum NoteStyle { Body, Bold, Code }

  /// <summary>A stretch of text in one style.</summary>
  internal sealed class NoteRun
  {
    public NoteRun(string text, NoteStyle style) { Text = text; Style = style; }
    public string Text { get; }
    public NoteStyle Style { get; }
  }

  internal enum NoteBlockKind { Paragraph, Bullet, Numbered, Table }

  /// <summary>A paragraph, a bullet or a numbered item holds <see cref="Runs"/>; a table holds <see cref="Rows"/>, the first of which is its header. A numbered item's <see cref="Number"/> is what the card draws.</summary>
  internal sealed class NoteBlock
  {
    public NoteBlockKind Kind { get; set; }
    public List<NoteRun> Runs { get; set; } = new List<NoteRun>();
    public List<List<List<NoteRun>>> Rows { get; set; } = new List<List<List<NoteRun>>>();
    public int Number { get; set; }
  }

  /// <summary>
  /// A NOTE'S TEXT IS A SMALL MARKDOWN SUBSET (Brief DO; numbers, Brief ED): paragraphs, bold, inline code, bullet and numbered lists and a simple table, the same for a note Dwight offered
  /// and a note a person wrote. This reads it for the canvas, which draws it itself, and flattens it for the model. The panel renders the same text whole through
  /// <c>ModelMarkdown</c>, so what is not in the subset still reads there: here a heading is bold, an emphasis or a link is its words, and nothing is ever an error.
  ///
  /// GH-free (ADR-091), and pure: text in, blocks out.
  /// </summary>
  internal static class NoteMarkdown
  {
    /// <summary>The widest table the canvas draws as a table. A wider one reads as lines, because a column under about 70 units wraps every word.</summary>
    public const int MaxColumns = 4;

    private static readonly Regex Delimiter = new Regex(@"^\s*\|?\s*:?-+:?\s*(\|\s*:?-+:?\s*)*\|?\s*$", RegexOptions.Compiled);
    private static readonly Regex BulletLine = new Regex(@"^\s*[-*+]\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex Heading = new Regex(@"^\s{0,3}#{1,6}\s+(.*?)\s*#*\s*$", RegexOptions.Compiled);
    private static readonly Regex NumberLine = new Regex(@"^\s*\d+[.)]\s+(.*)$", RegexOptions.Compiled);

    public static List<NoteBlock> Parse(string text)
    {
      var blocks = new List<NoteBlock>();
      var lines = Lines(text);
      var paragraph = new List<string>();
      var inRun = 0;   // how many numbered items the run has so far. A blank line does not end one, and anything else does.

      void Flush()
      {
        if (paragraph.Count == 0) return;
        blocks.Add(new NoteBlock { Kind = NoteBlockKind.Paragraph, Runs = Inline(string.Join(" ", paragraph.Select(p => p.Trim()))) });
        paragraph.Clear();
      }

      for (var i = 0; i < lines.Count; i++)
      {
        var line = lines[i];
        if (string.IsNullOrWhiteSpace(line)) { Flush(); continue; }

        // The number drawn is the place in the run and never the number typed, as Markdown does, so "1. 1. 1." reads 1, 2, 3.
        var number = NumberLine.Match(line);
        if (number.Success && !IsTableStart(lines, i))
        {
          Flush();
          blocks.Add(new NoteBlock { Kind = NoteBlockKind.Numbered, Number = ++inRun, Runs = Inline(number.Groups[1].Value.Trim()) });
          continue;
        }
        inRun = 0;

        if (IsTableStart(lines, i))
        {
          Flush();
          var rows = new List<List<string>> { Cells(line) };
          var at = i + 2;
          while (at < lines.Count && lines[at].Contains("|") && !string.IsNullOrWhiteSpace(lines[at])) rows.Add(Cells(lines[at++]));
          i = at - 1;
          var columns = rows[0].Count;
          if (columns > MaxColumns)
          {
            foreach (var row in rows) blocks.Add(new NoteBlock { Kind = NoteBlockKind.Paragraph, Runs = Inline(string.Join(" | ", row)) });
            continue;
          }
          var table = new NoteBlock { Kind = NoteBlockKind.Table };
          foreach (var row in rows)
            table.Rows.Add(Enumerable.Range(0, columns).Select(c => Inline(c < row.Count ? row[c] : "")).ToList());
          blocks.Add(table);
          continue;
        }

        var bullet = BulletLine.Match(line);
        if (bullet.Success)
        {
          Flush();
          blocks.Add(new NoteBlock { Kind = NoteBlockKind.Bullet, Runs = Inline(bullet.Groups[1].Value.Trim()) });
          continue;
        }

        var heading = Heading.Match(line);
        if (heading.Success)
        {
          Flush();
          blocks.Add(new NoteBlock { Kind = NoteBlockKind.Paragraph, Runs = Embolden(Inline(heading.Groups[1].Value)) });
          continue;
        }

        paragraph.Add(line);
      }
      Flush();
      return blocks;
    }

    /// <summary>
    /// What a note's text is to the model (Brief DO, item 4): the marks stay, because the model reads Markdown, and a table is flattened to lines. A person's or Dwight's
    /// words are otherwise untouched, and the bound is <see cref="DwightNotes.Lines"/>'s to keep.
    /// </summary>
    public static string ForModel(string text)
    {
      var lines = Lines(text);
      var output = new List<string>();
      for (var i = 0; i < lines.Count; i++)
      {
        if (!IsTableStart(lines, i)) { output.Add(lines[i]); continue; }
        output.Add(string.Join(" | ", Cells(lines[i])));
        var at = i + 2;
        while (at < lines.Count && lines[at].Contains("|") && !string.IsNullOrWhiteSpace(lines[at])) output.Add(string.Join(" | ", Cells(lines[at++])));
        i = at - 1;
      }
      return string.Join("\n", output).Trim();
    }

    private static List<string> Lines(string text) => (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();

    private static bool IsTableStart(IReadOnlyList<string> lines, int i) =>
      i + 1 < lines.Count && lines[i].Contains("|") && lines[i + 1].Contains("|") && Delimiter.IsMatch(lines[i + 1]);

    /// <summary>A row's cells, trimmed. A pipe inside inline code or after a backslash is the cell's own.</summary>
    private static List<string> Cells(string line)
    {
      var text = line.Trim();
      if (text.StartsWith("|", StringComparison.Ordinal)) text = text.Substring(1);
      var cells = new List<string>();
      var cell = new StringBuilder();
      var inCode = false;
      for (var i = 0; i < text.Length; i++)
      {
        var c = text[i];
        if (c == '\\' && i + 1 < text.Length && text[i + 1] == '|') { cell.Append("\\|"); i++; continue; }
        if (c == '`') inCode = !inCode;
        if (c == '|' && !inCode) { cells.Add(cell.ToString().Trim()); cell.Clear(); continue; }
        cell.Append(c);
      }
      if (cell.ToString().Trim().Length > 0) cells.Add(cell.ToString().Trim());
      return cells;
    }

    private static List<NoteRun> Embolden(List<NoteRun> runs) =>
      runs.Select(r => r.Style == NoteStyle.Code ? r : new NoteRun(r.Text, NoteStyle.Bold)).ToList();

    /// <summary>The words of a line with its inline marks read, for a line a shell shows as escaped text: a Walk's summary, a to-script preview's hint.</summary>
    public static string Words(string text) => string.Concat(Inline(text).Select(run => run.Text));

    /// <summary>The inline marks. An unmatched mark is its own character, so nothing a person typed disappears.</summary>
    public static List<NoteRun> Inline(string text)
    {
      var runs = new List<NoteRun>();
      var plain = new StringBuilder();
      var s = text ?? "";

      void FlushPlain()
      {
        if (plain.Length == 0) return;
        Add(runs, new NoteRun(plain.ToString(), NoteStyle.Body));
        plain.Clear();
      }

      for (var i = 0; i < s.Length; i++)
      {
        var c = s[i];
        if (c == '\\' && i + 1 < s.Length && "\\`*_|[]".IndexOf(s[i + 1]) >= 0) { plain.Append(s[i + 1]); i++; continue; }

        if (c == '`')
        {
          var close = s.IndexOf('`', i + 1);
          if (close > i + 1)
          {
            FlushPlain();
            Add(runs, new NoteRun(s.Substring(i + 1, close - i - 1), NoteStyle.Code));
            i = close;
            continue;
          }
        }

        if (c == '*' || c == '_')
        {
          var mark = new string(c, 2);
          if (string.CompareOrdinal(s, i, mark, 0, 2) == 0)
          {
            var close = s.IndexOf(mark, i + 2, StringComparison.Ordinal);
            if (close > i + 2)
            {
              FlushPlain();
              foreach (var inner in Embolden(Inline(s.Substring(i + 2, close - i - 2)))) Add(runs, inner);
              i = close + 1;
              continue;
            }
          }
          else if (Opens(s, i))
          {
            var close = ClosingEmphasis(s, i);
            if (close > 0)
            {
              // Emphasis is not in the subset: its words, without the marks.
              FlushPlain();
              foreach (var inner in Inline(s.Substring(i + 1, close - i - 1))) Add(runs, inner);
              i = close;
              continue;
            }
          }
        }

        if (c == '[')
        {
          var end = s.IndexOf("](", i + 1, StringComparison.Ordinal);
          var shut = end > 0 ? s.IndexOf(')', end + 2) : -1;
          if (end > i + 1 && shut > 0)
          {
            FlushPlain();
            foreach (var inner in Inline(s.Substring(i + 1, end - i - 1))) Add(runs, inner);
            i = shut;
            continue;
          }
        }

        plain.Append(c);
      }
      FlushPlain();
      return runs;
    }

    private static bool Opens(string s, int i)
    {
      if (i + 1 >= s.Length || char.IsWhiteSpace(s[i + 1])) return false;
      return s[i] == '*' || i == 0 || !char.IsLetterOrDigit(s[i - 1]);   // a_b is a name, not emphasis
    }

    private static int ClosingEmphasis(string s, int open)
    {
      for (var i = open + 1; i < s.Length; i++)
      {
        if (s[i] != s[open] || char.IsWhiteSpace(s[i - 1])) continue;
        if (s[open] == '_' && i + 1 < s.Length && char.IsLetterOrDigit(s[i + 1])) continue;
        return i;
      }
      return -1;
    }

    private static void Add(List<NoteRun> runs, NoteRun run)
    {
      if (run.Text.Length == 0) return;
      var last = runs.Count > 0 ? runs[runs.Count - 1] : null;
      if (last != null && last.Style == run.Style) runs[runs.Count - 1] = new NoteRun(last.Text + run.Text, run.Style);
      else runs.Add(run);
    }
  }
}
