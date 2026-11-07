using System.Drawing;
using System.Drawing.Drawing2D;

namespace Dwight.Branding
{
  /// <summary>The menu action items' glyphs, from the one icon table (Brief BO: Tabler, one set everywhere). Native menus cannot use the shells' sprite, so
  /// tools/build-menu-glyphs.js walks the same five paths into ActionIcons.Glyphs.cs and they are drawn here with GDI+ (round caps and joins, brand amber),
  /// so the native menus and the HTML surfaces read as one set with zero network (ADR-088).</summary>
  internal static partial class ActionIcons
  {
    public static Bitmap Create(string action, int size)
    {
      var bmp = new Bitmap(size, size);
      using (var g = Graphics.FromImage(bmp))
      {
        g.Clear(Color.Transparent);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        // Runtime round 2: draw the glyph a touch smaller (84% inset, centred) and thicker (2.6-unit
        // stroke) so it reads cleanly beside 9pt menu text instead of filling the image margin.
        var s = size * 0.84f / 24f;
        g.TranslateTransform((size - 24f * s) / 2f, (size - 24f * s) / 2f);
        using (var pen = new Pen(UiTokens.Brand, 2.6f * s)
        { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
        {
          switch (action)
          {
            case "Explain": Draw(g, pen, s, ExplainGlyph); break;
            case "Diagnose": Draw(g, pen, s, DiagnoseGlyph); break;
            case "Reorganise": Draw(g, pen, s, ReorganiseGlyph); break;
            case "Simplify": Draw(g, pen, s, SimplifyGlyph); break;
            case "Build": Draw(g, pen, s, BuildGlyph); break;
          }
        }
      }
      return bmp;
    }

    private static void Draw(Graphics g, Pen pen, float s, float[][] strokes)
    {
      foreach (var stroke in strokes)
      {
        var points = new PointF[stroke.Length / 2];
        for (var i = 0; i < points.Length; i++) points[i] = new PointF(stroke[i * 2] * s, stroke[i * 2 + 1] * s);
        if (points.Length >= 2) g.DrawLines(pen, points);
      }
    }
  }
}
