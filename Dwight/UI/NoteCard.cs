using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using Dwight.Branding;
using Dwight.Session;

namespace Dwight.UI
{
  /// <summary>Widths from GDI, in canvas units, each one asked once. A card's layout is kept between paints, so this is only ever asked when a note's words change.</summary>
  internal sealed class GdiNoteMetrics : INoteMetrics
  {
    private readonly Graphics _g;
    private readonly Dictionary<(string, NoteStyle), double> _seen = new Dictionary<(string, NoteStyle), double>();

    public GdiNoteMetrics(Graphics g) { _g = g; }

    public double Width(string text, NoteStyle style)
    {
      var key = (text ?? "", style);
      if (_seen.TryGetValue(key, out var w)) return w;
      w = key.Item1.Length == 0 ? 0 : _g.MeasureString(key.Item1, NoteCard.FontOf(style, key.Item1), PointF.Empty, NoteCard.Exact).Width;
      _seen[key] = w;
      return w;
    }
  }

  /// <summary>
  /// A NOTE ON THE CANVAS LOOKS LIKE A CARD IN THE PANEL (Brief DO, item 6), and never a Grasshopper scribble. The same tokens as the one window: the surface and its border, the hard
  /// shadow, square corners, the display face for the tag, the body face for the text, the mono face for code and counts, and the amber tag for a note Dwight offered with the ink tag
  /// for a person's, in the panel's own faces from the panel's own bytes (<see cref="NoteFaces"/>). The sizes are <see cref="NoteLayout"/>'s, and this only draws what it laid out.
  ///
  /// GH-free: it takes a Graphics and draws at a point, so it can be rendered beside the panel with no Rhino.
  /// </summary>
  internal static class NoteCard
  {
    internal static readonly StringFormat Exact = MakeExact();

    private static StringFormat MakeExact()
    {
      var f = (StringFormat)StringFormat.GenericTypographic.Clone();
      f.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces | StringFormatFlags.NoWrap;
      return f;
    }

    private static readonly Lazy<NoteFaces> Brand = new Lazy<NoteFaces>(() => NoteFaces.FromCss(EmbeddedCss()));
    private static readonly Lazy<NoteFaces> Fallback = new Lazy<NoteFaces>(NoteFaces.System);

    private static string EmbeddedCss()
    {
      using (var stream = typeof(NoteCard).Assembly.GetManifestResourceStream("Dwight.Fonts.css"))
      {
        if (stream == null) return null;
        using (var reader = new System.IO.StreamReader(stream)) return reader.ReadToEnd();
      }
    }

    /// <summary>The face a stretch of text is measured and drawn in: the panel's, unless the text is one they do not hold.</summary>
    internal static Font FontOf(NoteStyle style, string text)
    {
      var faces = NoteLayout.NeedsSystemFace(text) ? Fallback.Value : Brand.Value;
      return style == NoteStyle.Code ? faces.Code : style == NoteStyle.Bold ? faces.Bold : faces.Body;
    }

    /// <summary>The face a merged dot says its count in, the card's own for a count.</summary>
    internal static Font CountFont => Brand.Value.Count;

    public static void Draw(Graphics g, PointF at, NoteLayoutResult layout, bool byDwight, string date, string stale, bool dark)
    {
      var width = (float)NoteLayout.CardWidth(layout);
      var height = (float)NoteLayout.CardHeight(layout, stale != null);
      var rect = new RectangleF(at.X, at.Y, width, height);
      var ink = UiTokens.Body(dark);
      var accent = UiTokens.BrandInkOn(dark);

      var hint = g.TextRenderingHint;
      var smooth = g.SmoothingMode;
      g.TextRenderingHint = TextRenderingHint.AntiAlias;
      g.SmoothingMode = SmoothingMode.None;   // square corners and hairlines stay crisp
      try
      {
        var shadow = (float)NoteLayout.ShadowOffset;
        using (var b = new SolidBrush(UiTokens.ShadowInk(dark))) g.FillRectangle(b, rect.X + shadow, rect.Y + shadow, width, height);
        using (var b = new SolidBrush(UiTokens.Surface(dark))) g.FillRectangle(b, rect);
        using (var p = new Pen(UiTokens.BorderStrong(dark), 1f)) g.DrawRectangle(p, rect.X, rect.Y, width, height);

        var pad = (float)NoteLayout.Pad;
        using (var b = new SolidBrush(byDwight ? accent : UiTokens.Text(dark)))
          g.DrawString(byDwight ? "Dwight" : "You", Brand.Value.Tag, b, rect.X + pad, rect.Y + pad, Exact);
        if (!string.IsNullOrEmpty(date))
          using (var b = new SolidBrush(UiTokens.Subtle(dark)))
          {
            var w = g.MeasureString(date, Brand.Value.Count, PointF.Empty, Exact).Width;
            g.DrawString(date, Brand.Value.Count, b, rect.Right - pad - w, rect.Y + pad + 1f, Exact);
          }

        var x = rect.X + pad;
        var y = rect.Y + (float)NoteLayout.BodyTop;
        using (var p = new Pen(UiTokens.Border(dark), 1f))
          foreach (var r in layout.Rules) g.DrawLine(p, x + (float)r.X1, y + (float)r.Y1, x + (float)r.X2, y + (float)r.Y2);
        var radius = (float)NoteLayout.BulletRadius;
        g.SmoothingMode = SmoothingMode.AntiAlias;   // a circle, unlike the square corners above, needs it
        using (var b = new SolidBrush(accent))
          foreach (var (bx, by) in layout.Bullets) g.FillEllipse(b, x + (float)bx - radius, y + (float)by - radius, 2f * radius, 2f * radius);
        g.SmoothingMode = SmoothingMode.None;

        for (var i = 0; i < layout.Text.Count; i++)
        {
          var t = layout.Text[i];
          var more = layout.Cut && i == layout.Text.Count - 1;
          var font = FontOf(t.Style, t.Text);
          var top = y + (float)t.Y + ((float)NoteLayout.LineHeight - font.GetHeight(g)) / 2f;
          if (t.Style == NoteStyle.Code)
          {
            var w = g.MeasureString(t.Text, font, PointF.Empty, Exact).Width;
            var chip = new RectangleF(x + (float)t.X - 2f, y + (float)t.Y + 2f, w + 4f, (float)NoteLayout.LineHeight - 4f);
            using (var b = new SolidBrush(UiTokens.Inset(dark))) g.FillRectangle(b, chip);
            using (var p = new Pen(UiTokens.Border(dark), 1f)) g.DrawRectangle(p, chip.X, chip.Y, chip.Width, chip.Height);
          }
          using (var b = new SolidBrush(more ? accent : t.Style == NoteStyle.Body ? ink : UiTokens.Text(dark)))
            g.DrawString(t.Text, font, b, x + (float)t.X, top, Exact);
        }

        if (stale != null)
          using (var b = new SolidBrush(accent))
            g.DrawString(stale, Brand.Value.Count, b, x, y + (float)layout.Height + (float)NoteLayout.BlockGap + 3f, Exact);
      }
      finally
      {
        g.TextRenderingHint = hint;
        g.SmoothingMode = smooth;
      }
    }
  }
}
