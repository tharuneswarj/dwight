using System.Drawing;
using System.Drawing.Drawing2D;

namespace Dwight.Branding
{
  /// <summary>
  /// The Dwight mark: the pixel-art SHIRT AND TIE (ADR-073; Phase-10 reskin - the beet is retired).
  /// Drawn from a code-defined pixel grid extracted from the brand asset
  /// (from the brand icon source) so it is self-contained, crisp, and version-controlled
  /// (no binary asset). Used for the Grasshopper ribbon/component icon (24px), the menu headers
  /// (16px), and - via <see cref="MarkSvg"/> - every WebView shell titlebar, so one grid is the
  /// single source of truth. Shared with <see cref="Dwight.UI.Drawing.DwightGlyph"/>.
  /// </summary>
  internal static class DwightIcon
  {
    /// <summary>28x28: 'S' shirt (mustard amber), 'O' outline (near-black), 'T' tie (olive),
    /// 'C' collar/sleeve shade (darker amber), '.' transparent.</summary>
    public static readonly string[] Grid =
    {
      "............................",
      "............................",
      "............................",
      "............................",
      "........OOOOOOOOOOOO........",
      "........OSCCCCCCCCSO........",
      ".......OSSSCCCCCCSSSO.......",
      "....OOSOSSSOOCCOOSSSOSOO....",
      "...OSSSOOSSSOOOOSSSOSSSSO...",
      "..OSSSSSOSSSOTTOSSSOSSSSSO..",
      ".OSSSSSSCOSOOTTOOSOCSSSSSSO.",
      "OSSSSSSSSCOSOOOOSOCSSSSSSSOO",
      ".OSSSCSSSSCSSOOSSCSSSSCSSSO.",
      "..OSCOSSSSSSOTTOSSSSSSOCSO..",
      "...OCOSSSSSSOTTOSSSSSSOCO...",
      "...OOOSSSSSSOTTOSSSSSSOOO...",
      ".....OSSSSSSTTTTSSSSSSO.....",
      ".....OSSSSSSTTTTSSSSSSO.....",
      ".....OSSSSSOTTTTOSSSSSO.....",
      ".....OSSSSSOTTTTOSSSSSO.....",
      ".....OCSSSSOTTTTOSSSSCO.....",
      ".....OOOOOOOTTTTOOOOOOO.....",
      "............OTTO............",
      ".............OO.............",
      "............................",
      "............................",
      "............................",
      "............................",
    };

    // char -> colour: S shirt amber, O outline, T tie olive, C shade.
    private static readonly System.Collections.Generic.Dictionary<char, Color> Palette =
      new System.Collections.Generic.Dictionary<char, Color>
      {
        ['S'] = ColorTranslator.FromHtml("#E3B03C"),
        ['O'] = ColorTranslator.FromHtml("#1E2628"),
        ['T'] = ColorTranslator.FromHtml("#847536"),
        ['C'] = ColorTranslator.FromHtml("#BE8C2D"),
      };

    /// <summary>Build the icon at the requested square size. The grid is painted onto a
    /// 1px-per-cell master and nearest-neighbour scaled, so it stays crisp at any size
    /// (including the 16px menu render, which is smaller than the grid).</summary>
    public static Bitmap Create(int size)
    {
      var n = Grid.Length;
      var bmp = new Bitmap(size, size);
      using (var master = new Bitmap(n, n))
      {
        for (var y = 0; y < n; y++)
        for (var x = 0; x < n; x++)
        {
          var c = Grid[y][x];
          if (c == '.' || !Palette.TryGetValue(c, out var col)) continue;
          master.SetPixel(x, y, col);
        }
        using (var g = Graphics.FromImage(bmp))
        {
          g.Clear(Color.Transparent);
          g.InterpolationMode = InterpolationMode.NearestNeighbor;
          g.PixelOffsetMode = PixelOffsetMode.Half;
          g.DrawImage(master, new Rectangle(0, 0, size, size));
        }
      }
      return bmp;
    }

    /// <summary>The same shirt as an inline SVG (pixel rects from the grid) - one source of truth so
    /// the WebView shell marks match the ribbon/menu bitmap exactly. Injected by ShellAssets via the
    /// <c>&lt;!--__DWIGHT_MARK__--&gt;</c> placeholder. Mark colours are theme-independent.</summary>
    public static string MarkSvg()
    {
      var n = Grid.Length;
      var sb = new System.Text.StringBuilder();
      sb.Append($"<svg class=\"mark\" viewBox=\"0 0 {n} {n}\" shape-rendering=\"crispEdges\" aria-hidden=\"true\">");
      for (var y = 0; y < n; y++)
      for (var x = 0; x < n; x++)
      {
        var c = Grid[y][x];
        if (c == '.' || !Palette.TryGetValue(c, out var col)) continue;
        sb.Append($"<rect x=\"{x}\" y=\"{y}\" width=\"1.04\" height=\"1.04\" fill=\"#{col.R:X2}{col.G:X2}{col.B:X2}\"/>");
      }
      sb.Append("</svg>");
      return sb.ToString();
    }
  }
}
