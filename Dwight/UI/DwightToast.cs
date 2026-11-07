using System;
using Eto.Drawing;
using Eto.Forms;

namespace Dwight.UI
{
  /// <summary>
  /// The busy notice (ADR-072): a themed, transient message shown <b>top-centre of the canvas, just
  /// under the toolbar</b> - not a near-cursor box (the 3b near-cursor text read badly and is replaced).
  /// Raised when a second action is invoked while one runs; it never interrupts the running job, it just
  /// says so and fades out on its own. Eto-drawn, light/dark per theme, fail-safe (never crashes Rhino).
  /// </summary>
  internal sealed class DwightToast : Form
  {
    public static void Show(string message)
    {
      try
      {
        var t = new DwightToast(message ?? "");
        t.PlaceTopCentre();
        t.Show();
        NativeWindowEffects.ApplyFloating(t);
        t.StartFade();
      }
      catch { }
    }

    private readonly bool _dark;

    private DwightToast(string message)
    {
      _dark = DwightTheme.Current() == "dark";
      WindowStyle = WindowStyle.None;
      ShowInTaskbar = false;
      Topmost = true;
      Resizable = false;

      var bg = Color.Parse(_dark ? Dwight.Branding.UiTokens.SurfaceDarkHex : Dwight.Branding.UiTokens.SurfaceRaisedHex);
      var border = Color.Parse(_dark ? Dwight.Branding.UiTokens.BorderStrongDarkHex : Dwight.Branding.UiTokens.BorderStrongHex);
      var ink = Color.Parse(_dark ? Dwight.Branding.UiTokens.TextDarkHex : Dwight.Branding.UiTokens.TextHex);
      var accent = Color.Parse(_dark ? Dwight.Branding.UiTokens.BrandDarkHex : Dwight.Branding.UiTokens.BrandInkHex); // amber accent dot
      BackgroundColor = bg;

      var font = SystemFonts.Default(9.5f);
      // Fill EDGE-TO-EDGE with the surface colour and let the OS round the window corners (ApplyFloating);
      // drawing a smaller rounded card inside a square window left the window's corners poking out white.
      var d = new Drawable();
      d.Paint += (s, e) =>
      {
        var g = e.Graphics;
        g.AntiAlias = true;
        g.Clear(bg);
        g.DrawRectangle(border, 0.5f, 0.5f, d.Width - 1, d.Height - 1);
        var dotY = (d.Height - 7f) / 2f;
        g.FillEllipse(accent, 14f, dotY, 7f, 7f);
        var th = g.MeasureString(font, message).Height;
        g.DrawText(font, ink, 28f, (d.Height - th) / 2f, message);
      };
      using (var probe = new Bitmap(2, 2, PixelFormat.Format32bppRgba))
      using (var pg = new Graphics(probe))
      {
        var w = (int)pg.MeasureString(font, message).Width;
        d.Size = new Size(w + 44, 30);
      }
      Content = d;
      ClientSize = d.Size;
    }

    /// <summary>Anchor to the top-centre of the GH canvas, just below the toolbar; fall back to the
    /// cursor's screen working area top-centre.</summary>
    private void PlaceTopCentre()
    {
      try
      {
        var canvas = Grasshopper.Instances.ActiveCanvas;
        if (canvas != null && canvas.Width > 0)
        {
          // Physical (WinForms) -> logical (Eto) so the toast is not flung off-screen at non-100% DPI (#176).
          var s = canvas.DeviceDpi / 96.0;
          var tl = canvas.PointToScreen(System.Drawing.Point.Empty);
          int tlX = (int)Math.Round(tl.X / s), tlY = (int)Math.Round(tl.Y / s);
          int cw = (int)Math.Round(canvas.Width / s);
          Location = new Point(tlX + (cw - Width) / 2, tlY + 14);
          return;
        }
      }
      catch { }
      try
      {
        var area = Screen.PrimaryScreen.WorkingArea;
        Location = new Point((int)(area.X + (area.Width - Width) / 2), (int)area.Y + 60);
      }
      catch { }
    }

    /// <summary>Hold briefly, then fade out and close - self-dismissing, no user action needed.</summary>
    private void StartFade()
    {
      const double tick = 0.05, hold = 1.5, fade = 0.6;
      var elapsed = 0.0;
      var timer = new UITimer { Interval = tick };
      timer.Elapsed += (s, e) =>
      {
        elapsed += tick;
        if (elapsed < hold) return;
        var p = 1.0 - (elapsed - hold) / fade;
        if (p <= 0) { try { timer.Stop(); Close(); } catch { } }
        else { try { Opacity = p; } catch { } }
      };
      timer.Start();
    }
  }
}
