using System;
using System.Drawing;
using System.Windows.Forms;
using Grasshopper;
using Rhino;
using Rhino.Display;
using Rhino.DocObjects;

namespace Dwight.UI
{
  /// <summary>
  /// WHAT FOCUS MODE DOES TO THE WINDOWS (Brief DH): the host's half, with no decisions in it. <see cref="FocusState"/> says when and <see cref="FocusGeometry"/> says where; this carries it out and puts it all back.
  ///
  /// The members were read from RhinoCommon 8.36 and Grasshopper off disk, with no Rhino: <c>ViewTable.Add(string, DefinedViewportProjection, Rectangle, bool)</c>, <c>RhinoView.Close()</c>, <c>ViewTable.Find(Guid)</c>,
  /// <c>RhinoViewport.PushViewInfo</c>, and <c>GH_DocumentEditor</c> being a WinForms <see cref="Form"/>. None of it edits the definition or the Rhino document's geometry: a floating viewport is a view.
  ///
  /// Each step of the restore stands alone, so one that throws cannot cost the others, and a viewport the person has already closed is simply not found.
  /// </summary>
  internal static class WalkFocus
  {
    private const string ViewTitle = "Dwight Focus";

    private sealed class Prior
    {
      public FormWindowState State;
      public Rectangle Bounds;
      public RhinoView Active;
    }

    private static Prior _prior;
    private static Guid _viewId;
    private static IntPtr _viewRoot;
    private static OneWindowCore.Box? _viewBox;
    private static uint _docSerial;
    private static readonly FocusDocFlag _flag = new FocusDocFlag();

    /// <summary>What the viewport takes from framing in the canvas's own pixels, so a step is never framed under it. Zero when there is none.</summary>
    public static float RightClear(OneWindowCore.Box canvas, float scale) =>
      _prior == null ? 0f : FocusGeometry.RightClear(canvas, _viewBox) * scale;

    /// <summary>
    /// Collapse the window, maximise the editor, and read the canvas it then has. Where the bar will not fit there, everything goes back and <paramref name="say"/> holds why. A viewport that cannot open
    /// costs the viewport and not Focus: <paramref name="say"/> says so and the result is still true.
    /// <para>Brief EO unit 6: in Learn (<paramref name="keepPanel"/>) the panel is the card and there is no bar, so the window stays where it is, uncollapsed, and the bar's room is not asked for:
    /// the editor is maximised and the floating viewport opens, and that is all.</para>
    /// </summary>
    public static bool Engage(bool keepPanel, out string say)
    {
      say = null;
      var editor = Instances.DocumentEditor;
      if (editor == null) { say = "Focus needs Grasshopper's window, and it is not open."; return false; }
      try
      {
        _prior = new Prior
        {
          State = editor.WindowState,
          Bounds = editor.WindowState == FormWindowState.Normal ? editor.Bounds : editor.RestoreBounds,
          Active = RhinoDoc.ActiveDoc?.Views.ActiveView,
        };
        if (!keepPanel) DwightDock.Host()?.Collapse();
        editor.WindowState = FormWindowState.Maximized;
        if (!keepPanel) DwightDock.Host()?.FollowCanvas();

        var canvas = DwightDock.CanvasLogical();
        var chip = DwightDock.BoxNow();
        if (keepPanel && canvas is OneWindowCore.Box kept)
        {
          if (!OpenViewport(kept, editor)) say = FocusText.NoViewport;
          return true;
        }
        if (!(canvas is OneWindowCore.Box box) || WalkGeometry.Surface(WalkBar.CanvasShowingNow(), box, chip) != WalkSurface.Bar)
        {
          say = canvas is OneWindowCore.Box c ? FocusText.Decline(c, chip) : FocusText.Decline(new OneWindowCore.Box(0, 0, 0, 0), chip);
          Restore();
          return false;
        }

        if (!OpenViewport(box, editor)) say = FocusText.NoViewport;
        return true;
      }
      catch (Exception ex)
      {
        Restore();
        say = "Focus could not arrange the windows (" + ex.Message + "), so they are back as they were.";
        return false;
      }
    }

    private static bool OpenViewport(OneWindowCore.Box canvas, Form editor)
    {
      try
      {
        _viewId = Guid.Empty;
        _viewRoot = IntPtr.Zero;
        _viewBox = FocusGeometry.Viewport(canvas);
        var doc = RhinoDoc.ActiveDoc;
        if (_viewBox == null || doc == null) { _viewBox = null; return false; }

        _docSerial = doc.RuntimeSerialNumber;
        RhinoView view = null;
        Keeping(doc, () =>
        {
          view = doc.Views.Add(ViewTitle, DefinedViewportProjection.Perspective, FocusGeometry.Physical(_viewBox.Value, Dpi()), true);
          if (view == null) return;
          _viewId = view.MainViewport.Id;

          // It shows what the person was already looking at, which an empty document's zoom extents could not.
          try
          {
            var from = _prior?.Active?.ActiveViewport;
            if (from != null)
            {
              view.ActiveViewport.PushViewInfo(new ViewInfo(from), false);
              view.ActiveViewport.DisplayMode = from.DisplayMode;
              view.Redraw();
            }
          }
          catch { }
        });
        if (view == null) { _viewBox = null; return false; }

        // Owned by the editor, so clicking the canvas does not send it behind a maximised Grasshopper.
        _viewRoot = NativeWindowEffects.RootOf(view.Handle);
        NativeWindowEffects.SetOwner(_viewRoot, editor.Handle);
        DwightDock.GiveKeysToCanvas();
        return true;
      }
      catch { _viewBox = null; return false; }
    }

    /// <summary>The canvas moved: the viewport goes back to its corner, so the bar never covers it. Only when the place has changed, because this runs on every resize event.</summary>
    public static void Place()
    {
      if (_prior == null || _viewRoot == IntPtr.Zero) return;
      try
      {
        var canvas = DwightDock.CanvasLogical();
        if (canvas == null) return;
        var box = FocusGeometry.Viewport(canvas.Value);
        if (box == null || !_viewBox.HasValue || box.Value.ToString() == _viewBox.Value.ToString()) return;
        _viewBox = box;
        Keeping(RhinoDoc.FromRuntimeSerialNumber(_docSerial), () => NativeWindowEffects.MoveTo(_viewRoot, FocusGeometry.Physical(box.Value, Dpi())));
      }
      catch { }
    }

    /// <summary>Has the person closed the floating view? A Focus that never opened one has none to close, and a read that fails says no, so a failure here cannot take Focus away.</summary>
    public static bool ViewClosedByHand()
    {
      if (_prior == null || _viewId == Guid.Empty) return false;
      try { return RhinoDoc.FromRuntimeSerialNumber(_docSerial)?.Views.Find(_viewId) == null; }
      catch { return false; }
    }

    /// <summary>Every window back as it was, and the window returns, with the document's modified flag as the person left it (<see cref="Keeping"/>). Safe to call twice: the second finds nothing to do. It runs from the Walk's <c>finally</c>, on a decline, and when Rhino closes.</summary>
    public static void Restore()
    {
      var prior = _prior;
      _prior = null;
      if (prior == null) return;

      try { CloseViewport(prior); } catch { }
      _viewBox = null;
      _viewRoot = IntPtr.Zero;
      try
      {
        var editor = Instances.DocumentEditor;
        if (editor != null)
        {
          editor.WindowState = prior.State;
          if (prior.State == FormWindowState.Normal && !prior.Bounds.IsEmpty) editor.Bounds = prior.Bounds;
        }
      }
      catch { }
      try { WalkBar.Existing?.Dismiss(); } catch { }
      try { DwightDock.Host()?.OpenIfClosed(); } catch { }
    }

    private static void CloseViewport(Prior prior)
    {
      var id = _viewId;
      _viewId = Guid.Empty;
      if (id == Guid.Empty) return;
      var doc = RhinoDoc.FromRuntimeSerialNumber(_docSerial);
      Keeping(doc, () =>
      {
        var view = doc?.Views.Find(id);
        view?.Close();
        if (prior.Active != null) { try { doc.Views.ActiveView = prior.Active; } catch { } }
      });
    }

    /// <summary>
    /// A change to the view, under <see cref="FocusDocFlag"/>'s rule: read the flag before it, clear it after it if it read false, and read it once more on the next idle. In a <c>finally</c>, so a change that throws
    /// cannot leave the document marked. With no document (closed since), there is no flag to keep.
    /// </summary>
    private static void Keeping(RhinoDoc doc, Action change)
    {
      if (doc == null) { change(); return; }
      _flag.Before(doc.Modified);
      try { change(); }
      finally
      {
        if (_flag.After()) doc.Modified = false;
        RhinoApp.Idle -= OnIdle;
        RhinoApp.Idle += OnIdle;
      }
    }

    private static void OnIdle(object sender, EventArgs e)
    {
      RhinoApp.Idle -= OnIdle;
      try
      {
        var doc = RhinoDoc.FromRuntimeSerialNumber(_docSerial);
        if (doc != null && _flag.OnIdle(doc.Modified)) doc.Modified = false;
      }
      catch { }
    }

    /// <summary>Rhino is closing, and there may be no idle left: the read that was owed happens now, or Rhino would ask to save a file nobody touched.</summary>
    public static void Settle() => OnIdle(null, EventArgs.Empty);

    private static double Dpi() => Instances.ActiveCanvas?.DeviceDpi ?? 96;
  }
}
