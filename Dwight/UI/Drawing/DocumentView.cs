using System;
using System.Collections.Generic;
using System.Linq;
using Eto.Drawing;
using Eto.Forms;
using Dwight.Branding;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MdTable = Markdig.Extensions.Tables.Table;
using MdRow = Markdig.Extensions.Tables.TableRow;
using MdCell = Markdig.Extensions.Tables.TableCell;

namespace Dwight.UI.Drawing
{
  /// <summary>
  /// Renders Explain's Markdown by CUSTOM-DRAWING the parsed AST onto an Eto <see cref="Drawable"/>
  /// (ADR-045 revised): full control over typography, wrapping, line rhythm, rounded inline-code
  /// pills, code blocks, and a clean table - the things native Eto controls couldn't do. Markdig is
  /// the parser only. Layout is computed lazily during paint (when the width changes) into a flat
  /// list of draw ops; the control reports its content height so the host <see cref="Scrollable"/>
  /// scrolls it. Wholly fail-safe (any layout/paint error degrades to plain text, never crashes).
  /// </summary>
  internal sealed class DocumentView : Drawable
  {
    private static readonly MarkdownPipeline Pipeline =
      new MarkdownPipelineBuilder().UsePipeTables().UseEmphasisExtras().Build();

    // ---- doc-specific brand shades ----
    // R11: these were dark-surface values, so on a light surface an answer rendered at 1.64:1. They are read when drawn, because the theme can change.
    private static Color Lead    => Brand.OnSurface;                 // bright lead text
    private static Color Body    => Brand.BodyText;                     // body
    private static Color Heading => Brand.OnSurface;
    private static Color Section => Brand.AccentInk;                 // uppercase section labels
    private static Color Bullet  => Brand.AccentDeep;
    private static Color Subtle  => Brand.Subtle;
    private static Color CodeFg  => Brand.AccentInk;
    private static Color CodePill => new Color(Brand.AccentBright, 0.10f);
    private static Color CodeEdge => new Color(Brand.AccentBright, 0.20f);
    private static Color Divider => Brand.Divider;
    private static Color CellEdge => Brand.Divider;

    private const float PadX = 22f, PadTop = 16f, PadBottom = 18f;

    private MarkdownDocument _doc;
    private string _plain;            // plain mode (errors / simple messages)
    private Color _plainColor = Brand.OnSurface;
    private readonly List<Action<Graphics>> _ops = new List<Action<Graphics>>();
    private int _laidOutWidth = -1;
    private int _contentHeight;

    public DocumentView()
    {
      BackgroundColor = Brand.Surface;
      Paint += OnPaint;
      SizeChanged += (s, e) => { if (Width != _laidOutWidth) { _laidOutWidth = -1; Invalidate(); } };
    }

    public void SetMarkdown(string markdown)
    {
      _plain = null;
      try { _doc = Markdown.Parse(markdown ?? "", Pipeline); }
      catch { _doc = null; _plain = markdown ?? ""; }
      _laidOutWidth = -1;
      Invalidate();
    }

    public void SetPlain(string text, Color? color = null)
    {
      _doc = null;
      _plain = text ?? "";
      _plainColor = color ?? Body;
      _laidOutWidth = -1;
      Invalidate();
    }

    // ---- paint -------------------------------------------------------------------------

    private void OnPaint(object sender, PaintEventArgs e)
    {
      var g = e.Graphics;
      g.AntiAlias = true;
      g.FillRectangle(Brand.Surface, e.ClipRectangle);

      if (Width > 60 && _laidOutWidth != Width)
      {
        try { Layout(g, Width); }
        catch { _ops.Clear(); SafePlainLayout(g, Width, _plain ?? "Could not render this answer."); }
        _laidOutWidth = Width;
        if (Height != _contentHeight) Height = _contentHeight; // let the Scrollable know
      }

      foreach (var op in _ops)
      {
        try { op(g); } catch { /* skip a bad op */ }
      }
    }

    // ---- layout ------------------------------------------------------------------------

    private void Layout(Graphics g, int width)
    {
      _ops.Clear();
      var right = width - PadX;
      var y = PadTop;

      if (_doc == null)
      {
        y = SafePlainLayout(g, width, _plain ?? "");
        _contentHeight = (int)Math.Ceiling(y + PadBottom);
        return;
      }

      var blocks = _doc.OfType<Block>().ToList();
      for (var i = 0; i < blocks.Count; i++)
        y = RenderBlock(g, blocks[i], PadX, right, y, indent: 0, isFirst: i == 0);

      _contentHeight = (int)Math.Ceiling(y + PadBottom);
    }

    private float SafePlainLayout(Graphics g, int width, string text)
    {
      var font = Brand.Body(12.5f);
      var runs = new List<Run> { new Run(text, font, _plainColor, false) };
      return Flow(g, runs, PadX, width - PadX, PadTop, font);
    }

    private float RenderBlock(Graphics g, Block block, float left, float right, float y, int indent, bool isFirst)
    {
      switch (block)
      {
        case HeadingBlock h: return RenderHeading(g, h, left, right, y);
        case ParagraphBlock p:
        {
          var font = isFirst ? Brand.Body(13f) : Brand.Body(12.5f);
          var col = isFirst ? Lead : Body;
          var runs = Inlines(p.Inline, font, col);
          var end = Flow(g, runs, left, right, y, font);
          return end + Gap(font, 0.55f);
        }
        case ListBlock list:
        {
          var font = Brand.Body(12.5f);
          var ordinal = 1;
          foreach (var item in list.OfType<ListItemBlock>())
          {
            var marker = list.IsOrdered ? $"{ordinal++}." : null;
            y = RenderListItem(g, item, marker, left + indent * 18f, right, y, font);
          }
          return y + Gap(font, 0.35f);
        }
        case Markdig.Syntax.QuoteBlock q:
        {
          var font = Brand.Body(12.5f, FontStyle.Italic);
          foreach (var child in q.OfType<ParagraphBlock>())
          {
            var runs = Inlines(child.Inline, font, Subtle);
            var barY = y;
            var end = Flow(g, runs, left + 12f, right, y, font);
            var top = barY; var bottom = end;
            _ops.Add(gr => gr.FillRectangle(new Color(Brand.AccentDeep, 0.5f), left, top, 2.5f, Math.Max(2f, bottom - top - 4f)));
            y = end + Gap(font, 0.4f);
          }
          return y;
        }
        case CodeBlock code: return RenderCodeBlock(g, code, left, right, y);
        case ThematicBreakBlock _:
        {
          var ly = y + 6f;
          _ops.Add(gr => gr.FillRectangle(Divider, left, ly, right - left, 1f));
          return y + 16f;
        }
        case ContainerBlock cb when !(cb is MdTable):
        {
          foreach (var child in cb.OfType<Block>()) y = RenderBlock(g, child, left, right, y, indent, false);
          return y;
        }
        case MdTable table: return RenderTable(g, table, left, right, y);
        default: return y;
      }
    }

    private float RenderHeading(Graphics g, HeadingBlock h, float left, float right, float y)
    {
      var text = PlainInline(h.Inline);
      if (h.Level >= 2 && h.Level <= 3 && text.Length <= 42 && !text.Contains(" – ") && CountWords(text) <= 5)
      {
        // section label: small, uppercase, amber, with a trailing hairline rule
        var font = Brand.Title(10.5f, FontStyle.Bold);
        y += 12f;
        var label = text.ToUpperInvariant();
        var size = g.MeasureString(font, label);
        var ty = y;
        _ops.Add(gr => gr.DrawText(font, Section, left, ty, label));
        var lineX = left + size.Width + 10f; var lineY = ty + size.Height / 2f;
        if (lineX < right) _ops.Add(gr => gr.FillRectangle(new Color(Brand.AccentBright, 0.16f), lineX, lineY, right - lineX, 1f));
        return y + size.Height + 8f;
      }

      // prominent heading
      var hsize = h.Level <= 1 ? 15f : 13f;
      var hf = Brand.Title(hsize, FontStyle.Bold);
      y += h.Level <= 1 ? 8f : 14f;
      var runs = Inlines(h.Inline, hf, Heading);
      var end = Flow(g, runs, left, right, y, hf);
      return end + 7f;
    }

    private float RenderListItem(Graphics g, ListItemBlock item, string marker, float left, float right, float y, Font font)
    {
      var textLeft = left + 18f;
      var first = true;
      foreach (var child in item.OfType<Block>())
      {
        if (child is ParagraphBlock p)
        {
          var lineY = y;
          if (first)
          {
            var lh = LineHeight(g, font);
            if (marker == null)
            {
              var by = lineY + lh / 2f - 2.5f;
              _ops.Add(gr => gr.FillEllipse(Bullet, left + 4f, by, 4.5f, 4.5f));
            }
            else
            {
              var mf = Brand.Body(font.Size); var m = marker;
              var my = lineY + (lh - g.MeasureString(mf, m).Height) / 2f;
              _ops.Add(gr => gr.DrawText(mf, Bullet, left, my, m));
            }
          }
          var runs = Inlines(p.Inline, font, Body);
          y = Flow(g, runs, textLeft, right, y, font);
          y += Gap(font, 0.18f);
        }
        else if (child is ListBlock nested)
        {
          var ord = 1;
          foreach (var sub in nested.OfType<ListItemBlock>())
            y = RenderListItem(g, sub, nested.IsOrdered ? $"{ord++}." : null, textLeft, right, y, font);
        }
        first = false;
      }
      return y;
    }

    private float RenderCodeBlock(Graphics g, CodeBlock code, float left, float right, float y)
    {
      var text = code.Lines.ToString()?.TrimEnd() ?? "";
      var font = Brand.Mono(11.5f);
      var lines = text.Length == 0 ? new[] { "" } : text.Replace("\r", "").Split('\n');
      var lh = g.MeasureString(font, "Ag").Height + 3f;
      const float padV = 9f, padH = 12f;
      var top = y + 2f;
      var h = lines.Length * lh + 2 * padV;
      var rect = new RectangleF(left, top, right - left, h);
      _ops.Add(gr => gr.FillPath(Brand.SurfaceDeep, GraphicsPath.GetRoundRect(rect, 9f)));
      _ops.Add(gr => gr.DrawPath(Brand.Divider, GraphicsPath.GetRoundRect(rect, 9f)));
      var ly = top + padV;
      foreach (var line in lines)
      {
        var yy = ly; var ln = line;
        _ops.Add(gr => gr.DrawText(font, Brand.OnSurface, left + padH, yy, ln));
        ly += lh;
      }
      return top + h + 10f;
    }

    private float RenderTable(Graphics g, MdTable table, float left, float right, float y)
    {
      var rows = table.OfType<MdRow>().ToList();
      if (rows.Count == 0) return y;
      var header = rows.FirstOrDefault(r => r.IsHeader);
      var body = rows.Where(r => !r.IsHeader).ToList();
      var cols = Math.Max(
        header?.OfType<MdCell>().Count() ?? 0,
        body.Select(r => r.OfType<MdCell>().Count()).DefaultIfEmpty(0).Max());
      if (cols == 0) return y;

      var total = right - left;
      // first column hugs content (capped), remaining split evenly
      var headFont = Brand.Title(10f, FontStyle.Bold);
      var cellFont = Brand.Body(12f);
      var col0 = Math.Min(total * 0.42f, MaxCellWidth(g, header, body, 0, cellFont, headFont) + 16f);
      var rest = cols > 1 ? (total - col0) / (cols - 1) : 0f;
      float ColX(int c) => left + (c == 0 ? 0 : col0 + (c - 1) * rest);
      float ColW(int c) => c == 0 ? col0 : rest;

      y += 4f;
      if (header != null)
      {
        var hcells = header.OfType<MdCell>().ToList();
        var hy = y;
        for (var c = 0; c < cols; c++)
        {
          var t = c < hcells.Count ? CellText(hcells[c]).ToUpperInvariant() : "";
          var cx = ColX(c) + 2f; var ty = hy;
          _ops.Add(gr => gr.DrawText(headFont, Subtle, cx, ty, t));
        }
        var hh = g.MeasureString(headFont, "Ag").Height;
        y += hh + 7f;
        var ry = y; _ops.Add(gr => gr.FillRectangle(Divider, left, ry, total, 1f));
        y += 6f;
      }

      foreach (var r in body)
      {
        var cells = r.OfType<MdCell>().ToList();
        var rowTop = y;
        var maxBottom = y;
        for (var c = 0; c < cols; c++)
        {
          var runs = c < cells.Count ? Inlines(CellInline(cells[c]), cellFont, c == 0 ? Heading : Body) : new List<Run>();
          var bottom = Flow(g, runs, ColX(c) + 2f, ColX(c) + ColW(c) - 6f, rowTop, cellFont);
          if (bottom > maxBottom) maxBottom = bottom;
        }
        y = maxBottom + 7f;
        var ry = y - 3f; _ops.Add(gr => gr.FillRectangle(CellEdge, left, ry, total, 1f));
      }
      return y + 8f;
    }

    private float MaxCellWidth(Graphics g, MdRow header, List<MdRow> body, int col, Font cellFont, Font headFont)
    {
      var w = 0f;
      if (header != null)
      {
        var cells = header.OfType<MdCell>().ToList();
        if (col < cells.Count) w = Math.Max(w, g.MeasureString(headFont, CellText(cells[col]).ToUpperInvariant()).Width);
      }
      foreach (var r in body)
      {
        var cells = r.OfType<MdCell>().ToList();
        if (col < cells.Count) w = Math.Max(w, g.MeasureString(cellFont, CellText(cells[col])).Width);
      }
      return w;
    }

    // ---- inline flow (wrapping, pills) -------------------------------------------------

    private struct Run
    {
      public string Text; public Font Font; public Color Color; public bool Code;
      public Run(string t, Font f, Color c, bool code) { Text = t; Font = f; Color = c; Code = code; }
    }

    /// <summary>Lay runs left-to-right starting at <paramref name="x0"/>, wrapping at
    /// <paramref name="right"/> (hanging indent at x0). Emits draw ops; returns the y past the last line.</summary>
    private float Flow(Graphics g, List<Run> runs, float x0, float right, float y, Font baseFont)
    {
      var lh = LineHeight(g, baseFont);
      var x = x0;
      var spaceW = g.MeasureString(baseFont, " ").Width;

      foreach (var run in runs)
      {
        if (string.IsNullOrEmpty(run.Text)) continue;
        if (run.Code)
        {
          var token = run.Text;
          var tw = g.MeasureString(run.Font, token).Width;
          var pillW = tw + 12f;
          if (x + pillW > right && x > x0) { x = x0; y += lh; }
          PlacePill(run.Font, token, x, y, lh, tw);
          x += pillW + 2f;
        }
        else
        {
          foreach (var word in SplitWords(run.Text))
          {
            if (word == " ") { x += spaceW; continue; }
            var ww = g.MeasureString(run.Font, word).Width;
            if (x + ww > right && x > x0) { x = x0; y += lh; }
            var wx = x; var wy = y; var f = run.Font; var col = run.Color; var w = word;
            var th = g.MeasureString(f, w).Height;
            var ty = wy + (lh - th) / 2f;
            _ops.Add(gr => gr.DrawText(f, col, wx, ty, w));
            x += ww;
          }
        }
      }
      return y + lh;
    }

    private void PlacePill(Font font, string text, float x, float y, float lh, float tw)
    {
      var th = lh * 0.74f;
      var rect = new RectangleF(x, y + (lh - th) / 2f, tw + 11f, th);
      var bg = CodePill; var edge = CodeEdge; var fg = CodeFg; var t = text;
      var textH = th; // approx
      _ops.Add(gr =>
      {
        var path = GraphicsPath.GetRoundRect(rect, 5f);
        gr.FillPath(bg, path);
        gr.DrawPath(edge, path);
        var ts = gr.MeasureString(font, t);
        gr.DrawText(font, fg, x + 5.5f, y + (lh - ts.Height) / 2f, t);
      });
    }

    private static IEnumerable<string> SplitWords(string text)
    {
      // keep single spaces as their own token so the caller advances by a space width
      var sb = new System.Text.StringBuilder();
      foreach (var ch in text.Replace("\r", "").Replace("\n", " "))
      {
        if (ch == ' ') { if (sb.Length > 0) { yield return sb.ToString(); sb.Clear(); } yield return " "; }
        else sb.Append(ch);
      }
      if (sb.Length > 0) yield return sb.ToString();
    }

    // ---- inline AST -> runs ------------------------------------------------------------

    private List<Run> Inlines(ContainerInline inlines, Font baseFont, Color color)
    {
      var runs = new List<Run>();
      Walk(inlines, baseFont, color, false, false, false, runs);
      return runs;
    }

    private void Walk(ContainerInline inlines, Font baseFont, Color color, bool bold, bool italic, bool code, List<Run> runs)
    {
      if (inlines == null) return;
      foreach (var inline in inlines)
      {
        switch (inline)
        {
          case LiteralInline lit:
            runs.Add(new Run(lit.Content.ToString(), Styled(baseFont, bold, italic), color, false));
            break;
          case CodeInline ci:
            runs.Add(new Run(ci.Content, Brand.Mono(baseFont.Size - 1f), color, true));
            break;
          case EmphasisInline em:
            Walk(em, baseFont, color, bold || em.DelimiterCount >= 2, italic || em.DelimiterCount == 1, code, runs);
            break;
          case LinkInline link:
            Walk(link, baseFont, Brand.Accent, bold, italic, code, runs);
            break;
          case LineBreakInline _:
            runs.Add(new Run(" ", baseFont, color, false));
            break;
          case ContainerInline cont:
            Walk(cont, baseFont, color, bold, italic, code, runs);
            break;
          default:
            var s = inline?.ToString();
            if (!string.IsNullOrEmpty(s)) runs.Add(new Run(s, Styled(baseFont, bold, italic), color, false));
            break;
        }
      }
    }

    private static Font Styled(Font baseFont, bool bold, bool italic)
    {
      if (!bold && !italic) return baseFont;
      var style = (bold ? FontStyle.Bold : FontStyle.None) | (italic ? FontStyle.Italic : FontStyle.None);
      return Brand.Body(baseFont.Size, style);
    }

    // ---- small helpers -----------------------------------------------------------------

    private const float LineSpacing = 1.45f;
    private static float LineHeight(Graphics g, Font f) => g.MeasureString(f, "Ag").Height * LineSpacing;
    private static float Gap(Font font, float factor) => font.LineHeight * factor;
    private static int CountWords(string s) => s.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Length;

    private static string PlainInline(ContainerInline inlines)
    {
      var sb = new System.Text.StringBuilder();
      AppendText(inlines, sb);
      return sb.ToString().Trim();
    }

    private static string CellText(MdCell cell)
    {
      var sb = new System.Text.StringBuilder();
      foreach (var p in cell.OfType<ParagraphBlock>()) AppendText(p.Inline, sb);
      return sb.ToString().Trim();
    }

    private static ContainerInline CellInline(MdCell cell) => cell.OfType<ParagraphBlock>().FirstOrDefault()?.Inline;

    private static void AppendText(ContainerInline inlines, System.Text.StringBuilder sb)
    {
      if (inlines == null) return;
      foreach (var inline in inlines)
      {
        if (inline is LiteralInline lit) sb.Append(lit.Content.ToString());
        else if (inline is CodeInline ci) sb.Append(ci.Content);
        else if (inline is LineBreakInline) sb.Append(' ');
        else if (inline is ContainerInline cont) AppendText(cont, sb);
      }
    }
  }
}
