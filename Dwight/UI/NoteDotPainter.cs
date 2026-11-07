using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using Dwight.Branding;

namespace Dwight.UI
{
  /// <summary>
  /// THE NOTE DOTS AND THE COUNT CHIP, IN SCREEN PIXELS (Brief EH). Grasshopper raises <c>CanvasPostPaintOverlay</c> from <c>Regen_DrawMiddleGround</c> with its Graphics still
  /// carrying the viewport's canvas-to-screen matrix (read off Grasshopper.dll 8.36.26293, and measured at the eighteenth gate, where the dots came out a quarter of their size and
  /// shifted by the pan). A dot, its merge radius and the chip are sizes on the screen, and their hit tests compare screen points, so each call here draws with the identity
  /// transform and hands the caller's matrix back as it came, thrown or not, as Grasshopper's own widgets do: its paint goes on after ours.
  ///
  /// GH-free: it takes a Graphics, so it is tested on a bitmap whose Graphics carries a scale and a translation.
  /// </summary>
  internal static class NoteDotPainter
  {
    /// <summary>Every dot, at its own place and size on the screen, the last on top.</summary>
    public static void Draw(Graphics g, IReadOnlyList<NoteDot> dots, Font font, bool dark)
    {
      if (g == null || dots == null || dots.Count == 0) return;
      var kept = ToScreen(g);
      var smooth = g.SmoothingMode;
      var hint = g.TextRenderingHint;
      try
      {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAlias;
        foreach (var d in dots) DrawDot(g, d, font, dark);
      }
      finally { g.SmoothingMode = smooth; g.TextRenderingHint = hint; Restore(g, kept); }
    }

    /// <summary>
    /// The count chip at the canvas's bottom-left, 12 px in and 18 px up: "and N more notes" when a cap holds, or "N notes" when they are hidden. Returns the rectangle it
    /// painted, in screen pixels, which a click is tested against; empty when there is nothing to paint.
    /// </summary>
    public static RectangleF DrawChip(Graphics g, string text, float height, bool dark)
    {
      if (g == null || string.IsNullOrEmpty(text)) return RectangleF.Empty;
      var kept = ToScreen(g);
      try
      {
        using (var font = new Font("Segoe UI", 8f))
        {
          var size = g.MeasureString(text, font);
          var r = new RectangleF(12f, height - size.Height - 18f, size.Width + 14f, size.Height + 6f);
          using (var bg = new SolidBrush(dark ? UiTokens.BrandSubDark : UiTokens.BrandTint)) g.FillRectangle(bg, r);
          using (var pen = new Pen(UiTokens.BorderStrong(dark), 1f)) g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);
          using (var tb = new SolidBrush(UiTokens.Text(dark))) g.DrawString(text, font, tb, r.X + 7f, r.Y + 3f);
          return r;
        }
      }
      finally { Restore(g, kept); }
    }

    /// <summary>Filled in the brand's amber with a darker ring for a fresh note or a merged dot; the card's surface ringed in the stale sentence's ink for a changed one; that ring dashed for a gone one.</summary>
    private static void DrawDot(Graphics g, NoteDot d, Font font, bool dark)
    {
      var radius = (float)d.Diameter / 2f;
      var box = new RectangleF((float)d.X - radius, (float)d.Y - radius, 2f * radius, 2f * radius);
      var filled = d.Look == NoteDotLook.Filled;
      using (var fill = new SolidBrush(filled ? UiTokens.BrandAccent(dark) : UiTokens.Surface(dark))) g.FillEllipse(fill, box);
      using (var ring = new Pen(filled ? (dark ? UiTokens.BrandHover : UiTokens.BrandInk) : UiTokens.BrandInkOn(dark), 1.5f))
      {
        if (d.Look == NoteDotLook.HollowDashed) ring.DashStyle = DashStyle.Dash;
        g.DrawEllipse(ring, box);
      }
      if (!d.Merged) return;
      var count = d.Count.ToString(CultureInfo.InvariantCulture);
      var size = Measure(g, count, font);
      using (var ink = new SolidBrush(UiTokens.Text(false)))   // the dark ink on the amber in both themes: the dark theme's amber is lighter, not darker
        g.DrawString(count, font, ink, (float)d.X - size.Width / 2f, (float)d.Y - size.Height / 2f, NoteCard.Exact);
    }

    private static SizeF Measure(Graphics g, string text, Font font) => g.MeasureString(text, font, PointF.Empty, NoteCard.Exact);

    /// <summary>The caller's matrix, kept (the getter hands back a copy), and the identity set for our own drawing.</summary>
    private static Matrix ToScreen(Graphics g)
    {
      var kept = g.Transform;
      g.ResetTransform();
      return kept;
    }

    /// <summary>The caller's matrix back, in a finally: Grasshopper paints its widgets and the rest of its frame after the overlay event.</summary>
    private static void Restore(Graphics g, Matrix kept)
    {
      g.Transform = kept;
      kept.Dispose();
    }
  }
}
