using System;
using System.Collections.Generic;
using System.Linq;
using Dwight.Session;

namespace Dwight.UI
{
  /// <summary>How wide a stretch of text is in a style. The canvas answers from GDI, and a test from a fixed width per character.</summary>
  internal interface INoteMetrics
  {
    double Width(string text, NoteStyle style);
  }

  internal readonly struct NoteTextItem
  {
    public NoteTextItem(double x, double y, string text, NoteStyle style) { X = x; Y = y; Text = text; Style = style; }
    public double X { get; }
    public double Y { get; }
    public string Text { get; }
    public NoteStyle Style { get; }
  }

  internal readonly struct NoteLine
  {
    public NoteLine(double x1, double y1, double x2, double y2) { X1 = x1; Y1 = y1; X2 = x2; Y2 = y2; }
    public double X1 { get; }
    public double Y1 { get; }
    public double X2 { get; }
    public double Y2 { get; }
  }

  /// <summary>What a card's body holds, in its own coordinates from its top-left corner: text, bullet marks at their centres, a table's rules, and its size.</summary>
  internal sealed class NoteLayoutResult
  {
    public List<NoteTextItem> Text { get; } = new List<NoteTextItem>();
    public List<(double X, double Y)> Bullets { get; } = new List<(double, double)>();
    public List<NoteLine> Rules { get; } = new List<NoteLine>();
    public double Width { get; set; }
    public double Height { get; set; }
    public int Lines { get; set; }
    public int Chars { get; set; }

    /// <summary>The bound cut it, so the last line says <see cref="NoteLayout.AndMore"/> and a click opens the whole note in the panel.</summary>
    public bool Cut { get; set; }
  }

  /// <summary>
  /// A NOTE'S BODY, LAID OUT (Brief DO, item 1), pure: from the subset's blocks to lines and cells on rectangles. The card grows to its content and is bounded: at most
  /// <see cref="MaxLines"/> lines or <see cref="MaxChars"/> characters, whichever comes first, and the words past that are said to exist. A table never loses part of a row.
  ///
  /// The sizes are the panel's at zoom 1, so a note on the canvas and a card in the panel read as one product: 12.5 at a 1.55 line for the body, and the same gaps.
  /// </summary>
  internal static class NoteLayout
  {
    public const int MaxLines = 24;
    /// <summary>On a plain note the 24 lines come first, at about 36 characters to a line. This is the bound a note with a table reaches, at 320 wide, and it holds the card whichever comes first.</summary>
    public const int MaxChars = 1200;
    public const string AndMore = "and more";

    /// <summary>The width a plain note's words wrap to, and a note with a table's, in canvas units.</summary>
    public const double PlainWidth = 220;
    public const double TableWidth = 320;

    public const double LineHeight = 19.4;
    public const double BlockGap = 6;
    public const double BulletIndent = 12;
    /// <summary>A bullet's mark is a filled circle (Brief ED), and a numbered item's numeral stands this far left of its words.</summary>
    public const double BulletRadius = 3;
    public const double NumberGap = 4;
    public const double CellPad = 4;

    /// <summary>The card around the body: its padding, the tag's row, and the shadow the panel's cards have.</summary>
    public const double Pad = 10;
    public const double TagRow = 16;
    public const double TagGap = 6;
    public const double ShadowOffset = 3;
    public const double BodyTop = Pad + TagRow + TagGap;

    public static double CardWidth(NoteLayoutResult layout) => (layout?.Width ?? PlainWidth) + 2 * Pad;

    /// <summary>The card's height, with the stale sentence's line when it has one.</summary>
    public static double CardHeight(NoteLayoutResult layout, bool stale) =>
      BodyTop + (layout?.Height ?? 0) + (stale ? BlockGap + LineHeight : 0) + Pad;

    /// <summary>A letter past Latin is not in the panel's faces, which are cut to it, and GDI's own substitution leaves gaps in a script like Tamil. So a stretch holding one takes the system's face, which has it. Punctuation and arrows are in the panel's.</summary>
    public static bool NeedsSystemFace(string text)
    {
      foreach (var c in text ?? "")
        if (char.IsSurrogate(c) || (c > '\u024F' && char.IsLetter(c))) return true;
      return false;
    }

    public static NoteLayoutResult Build(IReadOnlyList<NoteBlock> blocks, INoteMetrics metrics)
    {
      var all = blocks ?? new List<NoteBlock>();
      var width = all.Any(b => b.Kind == NoteBlockKind.Table) ? TableWidth : PlainWidth;
      var result = new NoteLayoutResult { Width = width };
      var y = 0.0;
      var first = true;

      foreach (var block in all)
      {
        var at = first ? y : y + BlockGap;
        var next = block.Kind == NoteBlockKind.Table ? Table(block, metrics, width, at, result) : Lines(block, metrics, width, at, result);
        first = false;
        y = next;
        if (result.Cut) break;
      }

      if (result.Cut)
      {
        result.Text.Add(new NoteTextItem(0, y + (result.Lines > 0 ? BlockGap : 0), AndMore, NoteStyle.Bold));
        y += (result.Lines > 0 ? BlockGap : 0) + LineHeight;
      }
      result.Height = y;
      return result;
    }

    private static double Lines(NoteBlock block, INoteMetrics metrics, double width, double y, NoteLayoutResult result)
    {
      var bullet = block.Kind == NoteBlockKind.Bullet;
      var numbered = block.Kind == NoteBlockKind.Numbered;
      var indent = bullet ? BulletIndent : numbered ? NumberIndent(metrics) : 0;
      var wrapped = Wrap(block.Runs, metrics, width - indent);
      for (var i = 0; i < wrapped.Count; i++)
      {
        var chars = wrapped[i].Sum(w => w.Text.Length);
        if (!Room(result, 1, chars)) return y;
        if (bullet && i == 0) result.Bullets.Add((BulletIndent / 2 - 2, y + LineHeight / 2));
        if (numbered && i == 0)
        {
          var numeral = block.Number + ".";
          result.Text.Add(new NoteTextItem(indent - NumberGap - metrics.Width(numeral, NoteStyle.Body), y, numeral, NoteStyle.Body));   // right-aligned in the indent
        }
        foreach (var w in wrapped[i]) result.Text.Add(new NoteTextItem(indent + w.X, y, w.Text, w.Style));
        result.Lines++;
        result.Chars += chars;
        y += LineHeight;
      }
      return y;
    }

    /// <summary>Wide enough for two digits, which is all a card's 24 lines can reach, so "1." and "10." end in one place and the items' words start in one place.</summary>
    public static double NumberIndent(INoteMetrics metrics) => metrics.Width("00.", NoteStyle.Body) + NumberGap;

    private static double Table(NoteBlock block, INoteMetrics metrics, double width, double y, NoteLayoutResult result)
    {
      var columns = block.Rows.Count == 0 ? 0 : block.Rows[0].Count;
      if (columns == 0) return y;
      var cell = width / columns;
      var top = y;
      var drawn = 0;

      for (var r = 0; r < block.Rows.Count; r++)
      {
        var header = r == 0;
        var cells = block.Rows[r].Select(c => Wrap(header ? Bold(c) : c, metrics, cell - 2 * CellPad)).ToList();
        var height = Math.Max(1, cells.Max(c => c.Count));
        var chars = cells.Sum(c => c.Sum(l => l.Sum(w => w.Text.Length)));
        if (!Room(result, height, chars)) break;

        for (var c = 0; c < cells.Count; c++)
          for (var l = 0; l < cells[c].Count; l++)
            foreach (var w in cells[c][l])
              result.Text.Add(new NoteTextItem(c * cell + CellPad + w.X, y + l * LineHeight, w.Text, w.Style));

        result.Lines += height;
        result.Chars += chars;
        y += height * LineHeight;
        result.Rules.Add(new NoteLine(0, y, width, y));
        drawn++;
      }

      if (drawn == 0) return y;
      result.Rules.Add(new NoteLine(0, top, width, top));
      for (var c = 0; c <= columns; c++) result.Rules.Add(new NoteLine(c * cell, top, c * cell, y));
      return y;
    }

    /// <summary>Does this much more fit under the bound? When it does not, the bound has cut the note.</summary>
    private static bool Room(NoteLayoutResult result, int lines, int chars)
    {
      if (result.Lines + lines <= MaxLines && result.Chars + chars <= MaxChars) return true;
      result.Cut = true;
      return false;
    }

    private static List<NoteRun> Bold(List<NoteRun> runs) => runs.Select(r => r.Style == NoteStyle.Code ? r : new NoteRun(r.Text, NoteStyle.Bold)).ToList();

    private readonly struct Placed
    {
      public Placed(double x, string text, NoteStyle style) { X = x; Text = text; Style = style; }
      public double X { get; }
      public string Text { get; }
      public NoteStyle Style { get; }
    }

    /// <summary>Greedy wrapping across runs. A word wider than the line breaks at the character that no longer fits, so nothing is ever drawn past the card.</summary>
    private static List<List<Placed>> Wrap(IEnumerable<NoteRun> runs, INoteMetrics metrics, double width)
    {
      var lines = new List<List<Placed>>();
      var line = new List<Placed>();
      var x = 0.0;

      void NewLine()
      {
        lines.Add(line);
        line = new List<Placed>();
        x = 0;
      }

      void Put(string text, NoteStyle style)
      {
        line.Add(new Placed(x, text, style));
        x += metrics.Width(text, style);
      }

      foreach (var run in runs ?? Enumerable.Empty<NoteRun>())
      {
        foreach (var piece in Pieces(run.Text))
        {
          if (piece == " ")
          {
            if (x > 0 && x + metrics.Width(" ", run.Style) <= width) Put(" ", run.Style);
            continue;
          }

          var w = metrics.Width(piece, run.Style);
          if (x + w > width && x > 0) NewLine();
          if (w <= width) { Put(piece, run.Style); continue; }

          var rest = piece;
          while (rest.Length > 0)
          {
            var take = Fit(rest, run.Style, metrics, width - x);
            if (take == 0)
            {
              if (x == 0) take = 1;   // not even one character fits: it goes anyway, or this would never end
              else { NewLine(); continue; }
            }
            Put(rest.Substring(0, take), run.Style);
            rest = rest.Substring(take);
            if (rest.Length > 0) NewLine();
          }
        }
      }
      if (line.Count > 0) lines.Add(line);

      // A space left at the end of a line is not part of its width or its text. Then a line's neighbours of one style are one stretch, so inline code is one chip.
      for (var i = 0; i < lines.Count; i++)
      {
        while (lines[i].Count > 0 && lines[i][lines[i].Count - 1].Text == " ") lines[i].RemoveAt(lines[i].Count - 1);
        var merged = new List<Placed>();
        foreach (var p in lines[i])
        {
          var last = merged.Count - 1;
          if (last >= 0 && merged[last].Style == p.Style && NeedsSystemFace(merged[last].Text) == NeedsSystemFace(p.Text)) merged[last] = new Placed(merged[last].X, merged[last].Text + p.Text, p.Style);
          else merged.Add(p);
        }
        lines[i] = merged;
      }
      return lines;
    }

    private static int Fit(string text, NoteStyle style, INoteMetrics metrics, double width)
    {
      var take = 0;
      while (take < text.Length && metrics.Width(text.Substring(0, take + 1), style) <= width) take++;
      return take;
    }

    /// <summary>Words and single spaces. Any run of white space is one space.</summary>
    private static IEnumerable<string> Pieces(string text)
    {
      var word = new System.Text.StringBuilder();
      var spaced = false;
      foreach (var c in text ?? "")
      {
        if (char.IsWhiteSpace(c))
        {
          if (word.Length > 0) { yield return word.ToString(); word.Clear(); }
          if (!spaced) yield return " ";
          spaced = true;
          continue;
        }
        spaced = false;
        word.Append(c);
      }
      if (word.Length > 0) yield return word.ToString();
    }
  }
}
