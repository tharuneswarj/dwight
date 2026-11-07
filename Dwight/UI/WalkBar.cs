using System;
using System.Collections.Generic;
using System.Text.Json;
using Eto.Drawing;
using Eto.Forms;
using Grasshopper;
using Dwight.Branding;

namespace Dwight.UI
{
  /// <summary>
  /// THE WALK'S BAR (Brief DA): a frameless GH-owned strip along the bottom of the canvas, hosted as the one window is. The one-window law (Brief BR) is one window for the conversation; this is a
  /// run's companion, on Tharun's word, and since Brief DH it is Focus mode's: by default the panel is the Walk, and the bar is not even made. It holds no value of its own: <see cref="WalkSession"/> draws
  /// it, and every control comes back there.
  ///
  /// WHERE IT GOES IS READ FROM THE CANVAS, NEVER THE SCREEN (Tharun's ruling of 1 November 2026): Grasshopper may be full screen, half the screen beside Rhino, minimised, or resized while a Walk is live.
  /// <see cref="Sync"/> asks <see cref="WalkGeometry.SurfaceFor"/>, a pure function of Focus and the canvas's rectangle; it runs on the canvas's and the editor's own events, with the Walk's tick as a backstop
  /// for the one window moving. Where the bar cannot be placed it hides, and the panel is the Walk.
  /// </summary>
  internal sealed class WalkBar : Form
  {
    private static WalkBar _instance;

    private readonly WebView _web;
    private readonly List<string> _pending = new List<string>();
    private bool _loaded, _shownOnce;
    private int _wantH = WalkGeometry.BarMinH;
    private WalkSurface? _surface;   // null while no Walk is live, so a Walk starting is not a change of surface

    /// <summary>The bar if it has been made, for what only has to be taken away. A Walk that never reached Focus never made one.</summary>
    internal static WalkBar Existing => _instance;

    /// <summary>The bar, made when first wanted; null when it cannot exist (no WebView2), and then no Walk reaches it, because no WebPanel exists either.</summary>
    internal static WalkBar Get()
    {
      if (_instance != null) return _instance;
      try { _instance = new WalkBar(); }
      catch (Exception ex) { Rhino.RhinoApp.WriteLine("Dwight: the walk's bar did not attach - " + ex.Message); _instance = null; }
      return _instance;
    }

    private WalkBar()
    {
      if (!WebViewSupport.IsWebView2Available())
        throw new InvalidOperationException("WebView2 runtime is not available");

      WebViewSupport.ForceWebView2();
      WindowStyle = WindowStyle.None;
      ShowInTaskbar = false;
      ShowActivated = false;   // a Walk starting does not take the keyboard: the panel and the canvas both pass their keys on, and the person may be in another program by now
      Topmost = false;
      Resizable = false;
      BackgroundColor = Brand.Surface;

      _web = new WebView { BrowserContextMenuEnabled = false };
      WebViewSupport.HardenNavigation(_web);
      _web.DocumentLoaded += (s, e) => { _loaded = true; foreach (var js in _pending) Exec(js); _pending.Clear(); };
      _web.MessageReceived += (s, e) => Handle(e.Message);
      _web.DocumentTitleChanged += (s, e) => { var t = e.Title; if (!string.IsNullOrEmpty(t) && t.StartsWith("dwight:", StringComparison.Ordinal)) Handle(t.Substring(7)); };
      Content = _web;

      var html = ShellAssets.Inline(WebViewSupport.LoadResource("Dwight.WalkBar.html"));
      if (html != null) _web.LoadHtml(html, null);

      Run($"dwightSetTheme({Js(DwightTheme.Current())})");
      DwightTheme.Changed += t => Run($"dwightSetTheme({Js(t)})");

      Closed += (s, e) => { _instance = null; };
    }

    /// <summary>Draw this step, and say which surface is telling it.</summary>
    internal WalkSurface Present(string viewJson)
    {
      Run($"dwightWalkStep({Js(viewJson)})");
      return Sync();
    }

    /// <summary>Is the canvas on the screen at all, from the editor and the canvas themselves?</summary>
    internal static bool CanvasShowingNow()
    {
      try
      {
        var ed = Instances.DocumentEditor;
        var canvas = Instances.ActiveCanvas;
        return WalkGeometry.CanvasShowing(ed != null && ed.Visible, ed != null && ed.WindowState == System.Windows.Forms.FormWindowState.Minimized,
                                          canvas != null && canvas.Visible, canvas?.Width ?? 0, canvas?.Height ?? 0);
      }
      catch { return false; }
    }

    /// <summary>Put the bar where the canvas says, or take it away. It may run many times a second during a resize, so it changes only what has changed.</summary>
    internal WalkSurface Sync()
    {
      OneWindowCore.Box? box = null;
      try { box = DwightDock.CanvasLogical(); } catch { }
      var now = WalkGeometry.SurfaceFor(WalkSession.FocusActive, CanvasShowingNow(), box, DwightDock.BoxNow());
      if (now == WalkSurface.Bar) Place();
      else if (_shownOnce && Visible) Visible = false;
      var was = _surface;
      _surface = now;
      if (was != null && was != now) WalkSession.SurfaceChanged(was.Value, now);
      return now;
    }

    /// <summary>Brief EC unit 3: the bar's Focus and Dim switches, from the facts the panel's read.</summary>
    internal void MarkSwitches(bool focus, bool dim) => Run($"dwightWalkSwitches({(focus ? "true" : "false")},{(dim ? "true" : "false")})");

    internal void Dismiss()
    {
      _surface = null;
      if (_shownOnce) Visible = false;
    }

    /// <summary>What the bar, an open window and Focus's viewport take from the canvas, in the canvas's own pixels, for framing a step clear of them. The bar's is at its tallest: the page reports its height after the step is framed.</summary>
    internal static (float Bottom, float Right) Clears(WalkSurface surface)
    {
      var canvas = Instances.ActiveCanvas;
      var box = DwightDock.CanvasLogical();
      if (canvas == null || box == null) return (0f, 0f);
      var scale = canvas.DeviceDpi / 96f;
      var window = WalkGeometry.RightClear(box.Value, DwightDock.IsOpenNow ? DwightDock.BoxNow() : null) * scale;
      return (surface == WalkSurface.Bar ? WalkGeometry.FrameBottomClear * scale : 0f, Math.Max(window, WalkFocus.RightClear(box.Value, scale)));
    }

    private void Handle(string message)
    {
      // The title fallback may carry a trailing ":timestamp"; postMessage (forced WebView2) carries the clean command.
      var parts = (message ?? "").Split(':');
      var arg = parts.Length > 1 ? parts[1] : "";
      switch (parts[0])
      {
        case "play": WalkSession.TogglePlay(); break;
        case "everywire": TreeLens.Toggle(); break;
        case "input": WalkSession.Interrupt(); break;
        case "next": WalkSession.Next(); break;
        case "back": WalkSession.Back(); break;
        case "goto": if (int.TryParse(arg, out var i)) WalkSession.GoTo(i); break;
        case "ask": DwightCanvasPopup.Safe(WalkSession.Ask); break;
        case "deck": WalkSession.LivePanel?.DeckCommand(message.Substring(5)); break;   // Brief EI: the card on the bar is the page's card, so its controls are the page's
        // Brief EC unit 1 (Tharun's sitting): the X and the Focus switch are Focus off, and the Walk stays on its step in the panel. The bar has no Leave: that is the panel's.
        case "close": WalkSession.BarClosed(); break;
        case "focus": WalkSession.BarClosed(); break;
        case "dim": if (arg == "on" || arg == "off") WalkSession.SetDim(arg == "on"); break;   // Brief EC unit 2: the same setting as the panel's, at once
        case "key": WalkSession.PageKey(arg, true); break;
        case "h": if (int.TryParse(arg, out var h) && h != _wantH) { _wantH = h; if (WalkSession.IsLive) Sync(); } break;
      }
    }

    private void Place()
    {
      try
      {
        var canvas = DwightDock.CanvasLogical();
        if (canvas == null) return;
        var box = WalkGeometry.Bar(canvas.Value, _wantH, DwightDock.BoxNow());
        if (!_shownOnce)
        {
          _shownOnce = true;
          Show();
          NativeWindowEffects.ApplyFloating(this);
          NativeWindowEffects.SetGhOwner(this);   // above GH, hidden from Alt-Tab, never over other apps
        }
        else if (!Visible) Visible = true;
        var size = new Size(box.W, box.H);
        if (ClientSize != size) ClientSize = size;   // after the first Show too: the pre-Show size is inflated by the host on first creation, as the dock found
        var at = new Point(box.X, box.Y);
        if (Location != at) Location = at;
      }
      catch { }
    }

    private void Run(string js) { var app = Application.Instance; Action a = () => { if (_loaded) Exec(js); else _pending.Add(js); }; if (app != null) app.AsyncInvoke(a); else a(); }
    private void Exec(string js) { try { _web.ExecuteScript(js); } catch { } }
    private static string Js(string s) => JsonSerializer.Serialize(s ?? "");
  }
}
