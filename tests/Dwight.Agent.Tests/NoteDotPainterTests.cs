using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Dwight.Branding;
using Dwight.Session;
using Dwight.UI;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// Brief EH, the eighteenth gate's row 5: the overlay event's Graphics carries the viewport's matrix, and the dots and the chip were drawn through it, a quarter of their
  /// size and shifted by the pan, while the hit test read the true screen list. Here the painter draws on a bitmap whose Graphics carries the gate's own matrix (zoom 0.25,
  /// pan (440, 286)), and the pixels say where it drew.
  /// </summary>
  public class NoteDotPainterTests
  {
    // Large enough to hold where the viewport's matrix would send a dot at (200, 150): 0.25 x (200, 150) + (440, 286) = (490, 323.5).
    private const int W = 600, H = 400;
    private static readonly Color Canvas = Color.FromArgb(255, 40, 80, 160);   // a blue that no token is

    /// <summary>The gate's matrix: a canvas point p lands at 0.25 p + (440, 286), the form Rhino 2 fitted the captured dots to.</summary>
    private static Matrix Viewport()
    {
      var m = new Matrix();
      m.Scale(0.25f, 0.25f);
      m.Translate(440f, 286f, MatrixOrder.Append);
      return m;
    }

    private sealed class Board : IDisposable
    {
      public readonly Bitmap Bitmap = new Bitmap(W, H, PixelFormat.Format32bppArgb);
      public readonly Graphics G;

      public Board(bool viewport = true)
      {
        G = Graphics.FromImage(Bitmap);
        G.Clear(Canvas);
        if (viewport) using (var m = Viewport()) G.Transform = m;
      }

      /// <summary>Every pixel as ARGB, row by row, read once the drawing is done.</summary>
      public int[] Pixels()
      {
        G.Flush();
        var data = Bitmap.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
          var px = new int[W * H];
          for (var y = 0; y < H; y++) Marshal.Copy(data.Scan0 + y * data.Stride, px, y * W, W);
          return px;
        }
        finally { Bitmap.UnlockBits(data); }
      }

      public void Dispose() { G.Dispose(); Bitmap.Dispose(); }
    }

    private static Color At(int[] px, int x, int y) => Color.FromArgb(px[y * W + x]);

    private static bool Same(Color a, Color b) => a.R == b.R && a.G == b.G && a.B == b.B;

    /// <summary>Every pixel the drawing changed.</summary>
    private static IEnumerable<Point> Painted(int[] px)
    {
      for (var y = 0; y < H; y++)
        for (var x = 0; x < W; x++)
          if (!Same(At(px, x, y), Canvas)) yield return new Point(x, y);
    }

    private static NoteDot Dot(double x, double y, int members = 1, NoteStatus status = NoteStatus.Fresh, double diameter = NoteDots.Diameter) =>
      new NoteDot(Enumerable.Range(0, members).Select(i => new DotSource(new NoteRect("n" + i, 0, 0, 240, 60, false), x, y, status)).ToList(), x, y, diameter);

    /// <summary>The card's mono face is not loaded in a test, so the count is drawn in the system's; the painter draws in whatever face it is handed.</summary>
    private static Font CountFont() => new Font(FontFamily.GenericMonospace, 8f);

    private static void SameMatrix(Matrix expected, Matrix actual) => Assert.Equal(expected.Elements, actual.Elements);

    // ---- the dot -----------------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_dot_is_a_10_px_disc_at_its_screen_point_through_the_viewports_matrix(bool dark)
    {
      using (var board = new Board())
      using (var font = CountFont())
      {
        NoteDotPainter.Draw(board.G, new[] { Dot(200, 150) }, font, dark);
        var px = board.Pixels();

        Assert.True(Same(UiTokens.BrandAccent(dark), At(px, 200, 150)), "the centre is the brand's amber");
        Assert.True(Same(Canvas, At(px, 490, 323)), "nothing where the viewport's matrix would have put it");
        Assert.True(Same(Canvas, At(px, 490, 324)));

        // 10 px across: painted 3 px from the centre each way (a 2.5 px dot is not), and not 7 px out (a 40 px dot is).
        foreach (var (x, y) in new[] { (203, 150), (197, 150), (200, 153), (200, 147) }) Assert.False(Same(Canvas, At(px, x, y)), $"({x}, {y}) is inside the dot");
        foreach (var (x, y) in new[] { (207, 150), (193, 150), (200, 157), (200, 143) }) Assert.True(Same(Canvas, At(px, x, y)), $"({x}, {y}) is outside the dot");

        Assert.All(Painted(px), p => Assert.True(Math.Abs(p.X - 200) <= 6 && Math.Abs(p.Y - 150) <= 6, $"painted at ({p.X}, {p.Y}), outside the dot"));
      }
    }

    [Theory]
    [InlineData("Fresh", true)]
    [InlineData("Stale", false)]
    [InlineData("Gone", false)]
    public void A_fresh_dot_is_filled_in_amber_and_a_changed_or_gone_one_in_the_cards_surface(string status, bool amber)
    {
      using (var board = new Board())
      using (var font = CountFont())
      {
        NoteDotPainter.Draw(board.G, new[] { Dot(200, 150, status: Enum.Parse<NoteStatus>(status)) }, font, false);
        Assert.True(Same(amber ? UiTokens.BrandAccent(false) : UiTokens.Surface(false), At(board.Pixels(), 200, 150)));
      }
    }

    [Fact]
    public void A_merged_dot_says_its_count_inside_its_own_circle()
    {
      using (var board = new Board())
      using (var font = CountFont())
      {
        var diameter = NoteDots.MergedDiameter(board.G.MeasureString("12", font, PointF.Empty, NoteCard.Exact).Width);
        NoteDotPainter.Draw(board.G, new[] { Dot(200, 150, members: 12, diameter: diameter) }, font, false);
        var px = board.Pixels();

        var reach = diameter / 2 + 1.5;
        Assert.All(Painted(px), p => Assert.True((p.X - 200) * (p.X - 200) + (p.Y - 150) * (p.Y - 150) <= reach * reach, $"painted at ({p.X}, {p.Y}), outside the dot"));
        var ink = Painted(px).Where(p => Math.Abs(p.X - 200) <= 5 && Math.Abs(p.Y - 150) <= 4).Select(p => At(px, p.X, p.Y)).Count(c => c.R < 100);
        Assert.True(ink > 0, "the count is drawn in the dark ink at the dot's centre");
      }
    }

    /// <summary>
    /// The merge measures a count on the overlay's Graphics, through the viewport's matrix and Grasshopper's hint, and the painter draws it with the identity and the
    /// anti-aliased hint. That is right only because a typographic measure is in world units and ignores both, which the shim's comment claims, so it is held here.
    /// </summary>
    [Fact]
    public void A_count_measured_through_the_viewports_matrix_is_its_width_on_the_screen()
    {
      using (var font = CountFont())
      using (var plain = new Board(viewport: false))
      using (var board = new Board())
      {
        plain.G.TextRenderingHint = TextRenderingHint.AntiAlias;
        board.G.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        foreach (var count in new[] { "2", "12", "400" })
          Assert.Equal(plain.G.MeasureString(count, font, PointF.Empty, NoteCard.Exact).Width, board.G.MeasureString(count, font, PointF.Empty, NoteCard.Exact).Width, 3);
      }
    }

    // ---- the chip ----------------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_chip_paints_inside_the_rectangle_it_reports_at_the_bottom_left_and_nowhere_else(bool dark)
    {
      using (var board = new Board())
      {
        var r = NoteDotPainter.DrawChip(board.G, "3 notes", H, dark);
        var px = board.Pixels();

        Assert.Equal(12f, r.X);
        Assert.Equal(H - 12f, r.Bottom, 3);
        Assert.True(Same(dark ? UiTokens.BrandSubDark : UiTokens.BrandTint, At(px, 14, (int)(r.Y + r.Height / 2))), "the chip's background, inside its rectangle");
        var edge = RectangleF.Inflate(r, 1f, 1f);   // the 1 px border is drawn on the rectangle's edge
        Assert.All(Painted(px), p => Assert.True(edge.Contains(p), $"painted at ({p.X}, {p.Y}), outside the chip at {r}"));
      }
    }

    [Fact]
    public void Nothing_to_say_paints_no_chip()
    {
      using (var board = new Board())
      {
        Assert.Equal(RectangleF.Empty, NoteDotPainter.DrawChip(board.G, null, H, false));
        Assert.Empty(Painted(board.Pixels()));
      }
    }

    // ---- the caller's state ------------------------------------------------------------------------------------------------------------------

    /// <summary>Grasshopper paints its widgets and the rest of its frame after the overlay event, so the matrix, the smoothing and the hint leave as they came, thrown or not.</summary>
    [Fact]
    public void The_painter_hands_back_the_callers_matrix_smoothing_and_hint_as_they_came()
    {
      using (var font = CountFont())
      using (var board = new Board())
      using (var expected = Viewport())
      {
        var g = board.G;
        g.SmoothingMode = SmoothingMode.None;   // not the painter's AntiAlias (GDI+ reads HighSpeed back as None, so the mode read back is what is compared)
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        var smooth = g.SmoothingMode;

        NoteDotPainter.Draw(g, new[] { Dot(200, 150), Dot(300, 150, members: 3, diameter: 18) }, font, false);
        SameMatrix(expected, g.Transform);
        Assert.Equal(smooth, g.SmoothingMode);
        Assert.Equal(TextRenderingHint.ClearTypeGridFit, g.TextRenderingHint);

        NoteDotPainter.DrawChip(g, "and 4 more notes", H, false);
        SameMatrix(expected, g.Transform);

        // A merged dot handed no face throws inside the drawing; the finally still hands everything back.
        Assert.ThrowsAny<ArgumentException>(() => NoteDotPainter.Draw(g, new[] { Dot(200, 150, members: 2, diameter: 16) }, null, false));
        SameMatrix(expected, g.Transform);
        Assert.Equal(smooth, g.SmoothingMode);
        Assert.Equal(TextRenderingHint.ClearTypeGridFit, g.TextRenderingHint);
      }
    }

    // ---- the shim, held by its text ----------------------------------------------------------------------------------------------------------

    private static string Repo(params string[] parts)
    {
      var dir = new DirectoryInfo(AppContext.BaseDirectory);
      while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Dwight.sln"))) dir = dir.Parent;
      Assert.NotNull(dir);
      return File.ReadAllText(Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray())).Replace("\r\n", "\n");
    }

    private static string Between(string text, string from, string to)
    {
      var a = text.IndexOf(from, StringComparison.Ordinal);
      Assert.True(a >= 0, from);
      var b = text.IndexOf(to, a + from.Length, StringComparison.Ordinal);
      Assert.True(b >= 0, to);
      return text.Substring(a, b - a);
    }

    /// <summary>Everything the overlay draws goes through the painter, so nothing on it is drawn through the viewport's matrix again.</summary>
    [Fact]
    public void The_overlay_draws_only_through_the_painter()
    {
      var notes = Repo("Dwight", "UI", "CanvasNotes.cs");
      var overlay = Between(notes, "private static void OnOverlay(GH_Canvas sender)", "// ---- the press, the drag and the click");
      Assert.Contains("try { _chip = NoteDotPainter.DrawChip(sender.Graphics, text, sender.Height, Dark); }", overlay);
      Assert.Contains("_dots = NoteDots.Merge(_dotSources, sender.Width, sender.Height, s => g.MeasureString(s, font, PointF.Empty, NoteCard.Exact).Width);", overlay);
      Assert.Contains("NoteDotPainter.Draw(g, _dots, font, Dark);", overlay);
      foreach (var direct in new[] { "g.Fill", "g.Draw", "Transform" }) Assert.DoesNotContain(direct, overlay);
    }
  }
}
