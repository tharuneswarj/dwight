using Eto.Drawing;
using Dwight.Branding;

namespace Dwight.UI.Drawing
{
  /// <summary>Draws the pixel-art Dwight shirt mark with Eto <see cref="Graphics"/>, from the same
  /// grid the ribbon/menu icon uses - so the custom-drawn (fallback) UI matches the icon. Grid is
  /// <see cref="DwightIcon.Grid"/>; the colour dict mirrors its palette (Eto colours can't share
  /// System.Drawing values directly).</summary>
  internal static class DwightGlyph
  {
    private static readonly System.Collections.Generic.Dictionary<char, Color> Pal =
      new System.Collections.Generic.Dictionary<char, Color>
      {
        ['S'] = Color.Parse("#E3B03C"),
        ['O'] = Color.Parse("#1E2628"),
        ['T'] = Color.Parse("#847536"),
        ['C'] = Color.Parse("#BE8C2D"),
      };

    public static void Draw(Graphics g, float x, float y, float size)
    {
      var grid = DwightIcon.Grid;
      var n = grid.Length;
      var s = size / n;
      for (var gy = 0; gy < n; gy++)
      {
        var row = grid[gy];
        for (var gx = 0; gx < n; gx++)
        {
          var c = row[gx];
          if (c == '.' || !Pal.TryGetValue(c, out var col)) continue;
          g.FillRectangle(col, x + gx * s, y + gy * s, s + 0.6f, s + 0.6f);
        }
      }
    }
  }
}
