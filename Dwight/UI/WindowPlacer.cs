using System;
using System.Collections.Generic;
using System.Linq;
using Eto.Drawing;
using Eto.Forms;

namespace Dwight.UI
{
  /// <summary>
  /// Puts a floating Dwight window where <see cref="WindowPlacement"/> says - centred on the screen Grasshopper
  /// is on, clamped inside its working area - and, in a Debug build, says where it MEANT to put it and where
  /// the window actually IS. Used by every centred surface (the first-run tour, Settings), so a fix here is a
  /// fix to the class rather than to one instance (#877).
  ///
  /// <para>THE TIMING DEFECT IT ANSWERS. The tour opened at the bottom left at startup, and centred correctly
  /// when re-opened from Settings - the same code, so the arithmetic was right and the MOMENT was wrong. At
  /// Grasshopper startup something is not yet settled. Placing "after Show and the native calls" alone would
  /// not reach a later moment: neither call moves a window (ApplyFloating's SetWindowPos carries
  /// SWP_NOMOVE | SWP_NOSIZE; SetGhOwner only sets an owner), so it all runs inside the same synchronous
  /// startup call. So a surface calls this THREE times, idempotently - before Show (so the window never
  /// flashes at a default spot), after Show and the native calls, and when its page has loaded, which is
  /// always after the message loop has run. When the earlier placements were already right, the later ones
  /// change nothing; the Debug log shows which placement it was, so the re-sit CONFIRMS the cause instead of
  /// our inferring it.</para>
  /// </summary>
  internal static class WindowPlacer
  {
    public static void PlaceCentred(Window window, string label)
    {
      if (window == null) return;
      try
      {
        var screens = Screen.Screens?.ToList() ?? new List<Screen>();
        if (screens.Count == 0) return;

        var bounds = screens.Select(s => ToRect(s.Bounds)).ToList();
        var primaryBounds = Screen.PrimaryScreen?.Bounds;
        var primary = primaryBounds == null ? 0 : Math.Max(0, screens.FindIndex(s => s.Bounds == primaryBounds.Value));

        var i = WindowPlacement.PickScreen(bounds, GrasshopperCentre(), MousePoint(), primary);
        if (i < 0) return;

        var area = ToRect(screens[i].WorkingArea);
        var size = window.Size;
        var (x, y) = WindowPlacement.Centre(area, size.Width, size.Height);
        var was = window.Location;
        var intended = new Point((int)Math.Round(x), (int)Math.Round(y));
        window.Location = intended;
#if DEBUG
        // THE MEASUREMENT (#873-style: a fix that also measures). "was" is where the window sat when this
        // placement began - so the page-loaded line shows whether anything moved it after the earlier ones.
        Rhino.RhinoApp.WriteLine("Dwight placement [" + label + "]: screen " + i + " of " + screens.Count +
          ", work area " + area + ", window " + size.Width + "x" + size.Height +
          " - was " + was + ", intended " + intended + ", now " + window.Location);
#endif
      }
      catch { /* placement is cosmetic: never let it cost the window, let alone Rhino */ }
    }

    private static WindowPlacement.Rect ToRect(RectangleF r) => new WindowPlacement.Rect(r.X, r.Y, r.Width, r.Height);

    /// <summary>The centre of Grasshopper's window in Eto LOGICAL points, or null when it is not showing (at
    /// startup it may not be yet). Converted from WinForms physical pixels by DeviceDpi / 96 - the same
    /// conversion WebPanel.AllowedRegion uses, reused rather than re-derived, because #176 put every Dwight
    /// window off-screen by mixing the two and the conversion should exist in one form.</summary>
    private static (double X, double Y)? GrasshopperCentre()
    {
      try
      {
        var ed = Grasshopper.Instances.DocumentEditor;
        if (ed == null || !ed.Visible || ed.WindowState == System.Windows.Forms.FormWindowState.Minimized) return null;
        var s = ed.DeviceDpi / 96.0;
        if (s <= 0) return null;
        var b = ed.Bounds;
        return ((b.X + b.Width / 2.0) / s, (b.Y + b.Height / 2.0) / s);
      }
      catch { return null; }
    }

    private static (double X, double Y)? MousePoint()
    {
      try { var p = Mouse.Position; return (p.X, p.Y); }
      catch { return null; }
    }
  }
}
