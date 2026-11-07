using System;
using System.Runtime.InteropServices;
using Eto.Forms;

namespace Dwight.UI
{
  /// <summary>
  /// Windows 11 DWM polish for the frameless WebView windows: SHARP corners (the Phase-10
  /// drawing-set look - rounding explicitly suppressed) + the system drop shadow, so a panel reads
  /// as a floating window rather than a flat sticker on the canvas. Every call is best-effort
  /// (try/catch) - on Windows 10 or any failure the window simply stays square; it never crashes
  /// Rhino (ADR-029, fail-safe).
  /// </summary>
  internal static class NativeWindowEffects
  {
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33; // Win11 build 22000+
    private const int DWMWCP_DONOTROUND = 1;               // keep the corners sharp
    private const int DWMWA_NCRENDERING_POLICY = 2;        // force non-client (shadow) rendering
    private const int DWMNCRP_ENABLED = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS { public int Left, Right, Top, Bottom; }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    private const uint SWP_FRAMECHANGED = 0x0020, SWP_NOMOVE = 0x0002, SWP_NOSIZE = 0x0001,
                       SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;

    /// <summary>Keep the corners sharp and re-enable the drop shadow on a frameless Eto window. Call
    /// AFTER the window is shown - the native handle must already exist.</summary>
    public static void ApplyFloating(Window window)
    {
      try
      {
        var hwnd = window?.NativeHandle ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero) return;

        // Sharp corners (Win11 would otherwise round a frameless window on its own; explicitly opt
        // out - the drawing-set look). Returns non-zero and changes nothing on Win10 - harmless.
        var pref = DWMWCP_DONOTROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));

        // A 1px extended frame re-enables the DWM drop shadow on a borderless window. The WebView paints
        // over the 1px margin, so there is no visible glass intrusion; the shadow is drawn outside.
        var m = new MARGINS { Left = 1, Right = 1, Top = 1, Bottom = 1 };
        DwmExtendFrameIntoClientArea(hwnd, ref m);

        // Runtime feedback (UI overhaul round 1): with DONOTROUND, NON-RESIZABLE frameless windows
        // (the dock, settings, the toast) lost their shadow - the ROUND preference had been supplying
        // it, and only the resizable panel's WS_THICKFRAME kept one. Explicitly enabling non-client
        // rendering + a frame-changed nudge restores the DWM shadow with the corners still sharp.
        var pol = DWMNCRP_ENABLED;
        DwmSetWindowAttribute(hwnd, DWMWA_NCRENDERING_POLICY, ref pol, sizeof(int));
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
          SWP_FRAMECHANGED | SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
      }
      catch { /* leave the window square - never crash Rhino */ }
    }

    // ---- owned-window helper (shared by the panel + the dock) --------------------------

    private const int GWLP_HWNDPARENT = -8;

    [DllImport("user32.dll", CharSet = CharSet.Auto, EntryPoint = "SetWindowLongPtr")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
    [DllImport("user32.dll", CharSet = CharSet.Auto, EntryPoint = "SetWindowLong")]
    private static extern IntPtr SetWindowLong32(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    /// <summary>Make a frameless Eto window an OWNED window of the Grasshopper editor (fallback: the
    /// Rhino main window): it stays above its owner, hides from Alt-Tab, travels with Rhino, and never
    /// hovers over other apps. Call after Show(). Fail-safe.</summary>
    public static void SetGhOwner(Window window)
    {
      try
      {
        var hwnd = window?.NativeHandle ?? IntPtr.Zero;
        var owner = OwnerHandle();
        if (hwnd == IntPtr.Zero || owner == IntPtr.Zero) return;
        if (IntPtr.Size == 8) SetWindowLongPtr64(hwnd, GWLP_HWNDPARENT, owner);
        else SetWindowLong32(hwnd, GWLP_HWNDPARENT, owner);
      }
      catch { }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    private const uint GA_ROOT = 2;

    /// <summary>Brief DH: the top-level window a child window sits in. A floating Rhino view's handle is its inner view, and only its frame can be owned or moved.</summary>
    public static IntPtr RootOf(IntPtr hwnd)
    {
      try { return hwnd == IntPtr.Zero ? IntPtr.Zero : GetAncestor(hwnd, GA_ROOT); }
      catch { return IntPtr.Zero; }
    }

    /// <summary>Brief DH: own a window that is not ours, so it stays above a maximised Grasshopper. Fail-safe, as <see cref="SetGhOwner"/> is.</summary>
    public static void SetOwner(IntPtr hwnd, IntPtr owner)
    {
      try
      {
        if (hwnd == IntPtr.Zero || owner == IntPtr.Zero) return;
        if (IntPtr.Size == 8) SetWindowLongPtr64(hwnd, GWLP_HWNDPARENT, owner);
        else SetWindowLong32(hwnd, GWLP_HWNDPARENT, owner);
      }
      catch { }
    }

    /// <summary>Brief DH: move a window, in screen pixels, without taking the keyboard or changing its place in the stack.</summary>
    public static void MoveTo(IntPtr hwnd, System.Drawing.Rectangle screen)
    {
      try
      {
        if (hwnd == IntPtr.Zero) return;
        SetWindowPos(hwnd, IntPtr.Zero, screen.X, screen.Y, screen.Width, screen.Height, SWP_NOZORDER | SWP_NOACTIVATE);
      }
      catch { }
    }

    private static IntPtr OwnerHandle()
    {
      try { var ed = Grasshopper.Instances.DocumentEditor; if (ed != null && ed.Handle != IntPtr.Zero) return ed.Handle; }
      catch { }
      try { return Rhino.RhinoApp.MainWindowHandle(); }
      catch { return IntPtr.Zero; }
    }
  }
}
