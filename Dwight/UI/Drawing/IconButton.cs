using System;
using Eto.Drawing;
using Eto.Forms;
using Dwight.Branding;

namespace Dwight.UI.Drawing
{
  /// <summary>A small square glyph button (close ✕ / minimise ▁) for the custom title bar - a
  /// real control so its click is consumed (not swallowed by window-drag). Hover highlight.</summary>
  internal sealed class IconButton : Drawable
  {
    private readonly string _glyph;
    private bool _hover;

    public event EventHandler<EventArgs> Clicked;
    public Color HoverFill = new Color(Brand.White, 0.12f);

    public IconButton(string glyph, int size = 30)
    {
      _glyph = glyph;
      Width = size;
      Height = size;
      Cursor = Cursors.Pointer;
      MouseEnter += (s, e) => { _hover = true; Invalidate(); };
      MouseLeave += (s, e) => { _hover = false; Invalidate(); };
      MouseUp += (s, e) => { if (e.Buttons == MouseButtons.Primary) Clicked?.Invoke(this, EventArgs.Empty); };
      Paint += OnPaint;
    }

    private void OnPaint(object sender, PaintEventArgs e)
    {
      var g = e.Graphics;
      g.AntiAlias = true;
      if (_hover)
      {
        var r = new RectangleF(2.5f, 2.5f, Width - 5, Height - 5);
        g.FillPath(HoverFill, GraphicsPath.GetRoundRect(r, 6f));
      }
      var font = Brand.Body(13f);
      var ts = g.MeasureString(font, _glyph);
      g.DrawText(font, _hover ? Brand.OnSurface : Brand.Subtle, (Width - ts.Width) / 2f, (Height - ts.Height) / 2f - 0.5f, _glyph);
    }
  }
}
