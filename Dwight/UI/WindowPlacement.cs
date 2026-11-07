using System;
using System.Collections.Generic;

namespace Dwight.UI
{
  /// <summary>
  /// Where a floating Dwight window goes: centred on the screen Grasshopper is on, and never off it. Pure
  /// arithmetic - no Eto, no Grasshopper - so it is tested headlessly; <see cref="WindowPlacer"/> is the
  /// half that reads the live screens and moves the window.
  ///
  /// <para>WHY IT EXISTS (Tharun's sitting, 2026-09-19). The first-run tour opened at the bottom left at 150%
  /// scaling, cut off at the taskbar. Its arithmetic was right for his screen - (380, 56) on a 1280x693
  /// logical working area is centred and inside, which is why re-opening it from Settings looked right - so
  /// the defect was TIMING (see <see cref="WindowPlacer"/>). But the arithmetic had two real faults of its
  /// own, and they are what this fixes:</para>
  /// <list type="number">
  /// <item>NOTHING CLAMPED. A window larger than the working area went off its top or left.</item>
  /// <item>THE HALF-SIZES WERE CONSTANTS - 260 and 290 centre only a 520x580 window. The tour is being
  /// redesigned at a new size, so the old line would have put it off-centre on day one. Here the window's
  /// ACTUAL size is an input, and nothing about any one window's layout is known to this file.</item>
  /// </list>
  /// </summary>
  internal static class WindowPlacement
  {
    /// <summary>A rectangle in Eto LOGICAL points (never WinForms device pixels - #176).</summary>
    internal readonly struct Rect
    {
      public readonly double X, Y, Width, Height;
      public Rect(double x, double y, double width, double height) { X = x; Y = y; Width = width; Height = height; }
      public bool Contains(double px, double py) => px >= X && px < X + Width && py >= Y && py < Y + Height;
      public override string ToString() => "(" + X + "," + Y + " " + Width + "x" + Height + ")";
    }

    /// <summary>
    /// The top-left for a window of <paramref name="width"/> x <paramref name="height"/> in
    /// <paramref name="area"/>: CENTRED when it fits, and ENTIRELY INSIDE when it fits.
    ///
    /// <para>When it does NOT fit on an axis, "entirely inside" is impossible, and the window is pinned to the
    /// area's top (or left) edge rather than centred - so its title bar and close button stay on screen and it
    /// can always be dragged or closed. A window centred and overflowing would hide exactly those.</para>
    /// </summary>
    public static (double X, double Y) Centre(Rect area, double width, double height) =>
      (Axis(area.X, area.Width, width), Axis(area.Y, area.Height, height));

    private static double Axis(double start, double span, double size)
    {
      if (size >= span) return start;                              // does not fit: pin to the leading edge
      var centred = start + (span - size) / 2.0;
      return Math.Max(start, Math.Min(centred, start + span - size));   // fits: centred, and clamped inside
    }

    /// <summary>
    /// Which screen to use, by index into <paramref name="screens"/>: the one Grasshopper's window is on; if that
    /// is unknown (at startup the editor may not be showing yet), the one under the mouse; failing both, the
    /// primary. "Primary" alone is wrong on two monitors - it is not necessarily where the user is looking.
    /// </summary>
    public static int PickScreen(IReadOnlyList<Rect> screens, (double X, double Y)? grasshopperCentre,
                                 (double X, double Y)? mouse, int primaryIndex)
    {
      if (screens == null || screens.Count == 0) return -1;
      var fallback = primaryIndex >= 0 && primaryIndex < screens.Count ? primaryIndex : 0;

      foreach (var p in new[] { grasshopperCentre, mouse })
      {
        if (p == null) continue;
        for (var i = 0; i < screens.Count; i++)
          if (screens[i].Contains(p.Value.X, p.Value.Y)) return i;
      }
      return fallback;
    }
  }
}
