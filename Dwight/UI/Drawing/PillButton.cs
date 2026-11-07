using System;
using Eto.Drawing;
using Eto.Forms;
using Dwight.Branding;

namespace Dwight.UI.Drawing
{
  /// <summary>A small, custom-drawn pill button (rounded, brand-styled) - Eto's native button looks
  /// dated, so the footer/header use this. Ghost style by default, one lime "primary" per surface.
  /// Self-sizes to its text on first paint.</summary>
  internal sealed class PillButton : Drawable
  {
    private readonly bool _primary;
    private bool _hover;
    private string _text;

    public event EventHandler<EventArgs> Clicked;

    public PillButton(string text, bool primary = false)
    {
      _text = text;
      _primary = primary;
      Height = 30;
      Width = 70;
      Cursor = Cursors.Pointer;

      MouseEnter += (s, e) => { _hover = true; Invalidate(); };
      MouseLeave += (s, e) => { _hover = false; Invalidate(); };
      MouseUp += (s, e) => { if (e.Buttons == MouseButtons.Primary) Clicked?.Invoke(this, EventArgs.Empty); };
      Paint += OnPaint;
    }

    public string Text { get => _text; set { _text = value; Invalidate(); } }

    private void OnPaint(object sender, PaintEventArgs e)
    {
      var g = e.Graphics;
      g.AntiAlias = true;
      var font = Brand.Body(12.5f);
      var ts = g.MeasureString(font, _text ?? "");

      var want = (int)Math.Ceiling(ts.Width) + 30;
      if (Width != want) { Width = want; return; } // re-sized; a fresh paint will follow

      var rect = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
      var pill = GraphicsPath.GetRoundRect(rect, Height / 2f);

      if (_primary)
      {
        g.FillPath(Brand.Accent, pill);
      }
      else
      {
        g.FillPath(new Color(Brand.White, _hover ? 0.13f : 0.07f), pill);
        g.DrawPath(new Color(Brand.White, 0.12f), pill);
      }

      var fg = _primary ? Brand.Black : (_hover ? Brand.OnSurface : Brand.Subtle);
      g.DrawText(font, fg, (Width - ts.Width) / 2f, (Height - ts.Height) / 2f, _text ?? "");
    }
  }
}
