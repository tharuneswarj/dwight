using System;
using System.Linq;
using System.Windows.Forms;
using Grasshopper.GUI.Canvas;

namespace Dwight
{
  /// <summary>
  /// Dwight's cursor-summoned pop-up menu (ADR-024). Summoned by <b>Ctrl+Shift+Right-click only</b>
  /// (ADR-047, supersedes ADR-025) - a chord Grasshopper does not use, so its native canvas menu is
  /// never touched. The plain-right-click option was removed (it stranded the user and could not
  /// cleanly suppress the native menu). Dwight is also reachable from the top-bar menu and (later)
  /// the dock (ADR-046).
  /// </summary>
  internal static class DwightCanvasPopup
  {
    private static GH_Canvas _canvas;

    public static void Attach(GH_Canvas canvas)
    {
      if (canvas == null) return;
      if (_canvas != null) _canvas.MouseUp -= OnCanvasMouseUp; // never double-subscribe
      _canvas = canvas;
      _canvas.MouseUp += OnCanvasMouseUp;
    }

    private static void OnCanvasMouseUp(object sender, MouseEventArgs e)
    {
      if (e.Button != MouseButtons.Right) return;
      // The chord lives in ONE place (Dwight.UI.DwightChord), because the first-run tour prints it and a
      // printed chord that disagrees with the handler teaches the user something untrue.
      if (!Dwight.UI.DwightChord.Held(Control.ModifierKeys)) return;
      var menu = BuildMenu(NotePointAt(e.Location));
      // WP-4.7 (hotfix, runtime round 1): dispose the strip and its per-item icon images (GDI resources)
      // AFTER the message loop is done with the menu. Disposing synchronously inside Closed ran while
      // WinForms was still unwinding the close - it crashed Rhino on click-away dismissal and could tear
      // the strip down before a clicked item's event dispatched (menu actions silently swallowed).
      // BeginInvoke posts the teardown behind the current dispatch; the leak fix stays, the timing moves.
      menu.Closed += (s, ev) =>
      {
        try { _canvas.BeginInvoke((Action)(() => DisposeMenu(menu))); }
        catch { }   // canvas handle gone (shutdown): leak one strip rather than dispose mid-teardown
      };
      menu.Show(_canvas, e.Location);
    }

    private static void DisposeMenu(ContextMenuStrip menu)
    {
      try
      {
        void DisposeItems(ToolStripItemCollection items)
        {
          foreach (ToolStripItem it in items)
          {
            try { it.Image?.Dispose(); } catch { }
            if (it is ToolStripMenuItem mi && mi.HasDropDownItems) DisposeItems(mi.DropDownItems);
          }
        }
        DisposeItems(menu.Items);
        menu.Dispose();
      }
      catch { }
    }

    /// <summary>The chord's point in document coordinates, for "Note here" when nothing is selected.</summary>
    private static System.Drawing.PointF NotePointAt(System.Drawing.Point screen)
    {
      try { return _canvas.Viewport.UnprojectPoint(screen); } catch { return default; }
    }

    /// <summary>The menu's shape is <see cref="Dwight.UI.ChordMenu"/>'s, GH-free so a test builds it (Brief EU); here it reads the live state and gets the live verbs.</summary>
    private static ContextMenuStrip BuildMenu(System.Drawing.PointF notePoint) =>
      Dwight.UI.ChordMenu.Build(DwightActions.CurrentSelection().Count, Dwight.UI.CanvasNotes.Shown, Dwight.UI.DwightTheme.Current() == "dark", new Dwight.UI.ChordDoors
      {
        ExplainWalk = () => LlmActions.Explain(Dwight.UI.ExplainModes.Walk),
        ExplainDetailed = () => LlmActions.Explain(Dwight.UI.ExplainModes.Detailed),
        ExplainShort = () => LlmActions.Explain(Dwight.UI.ExplainModes.Short),
        ExplainLearn = () => LlmActions.Explain(Dwight.UI.ExplainModes.Learn),
        Diagnose = LlmActions.Diagnose,
        Reorganise = LlmActions.Reorganise,
        Simplify = () => LlmActions.Simplify(),
        Build = () => Dwight.UI.DwightScaffold.Open(),
        ReadSelection = DwightActions.ReadSelection,
        DumpSelection = DwightActions.DumpSelectionAsGhJson,
        NoteHere = () => NoteHere(notePoint),
        SetNotesShown = Dwight.UI.CanvasNotes.SetShown,
        Restart = () => DwightSession.Restart(),
        Settings = LlmActions.OpenSettings,
        ReportProblem = Dwight.UI.DwightFeedbackPanel.Open,
        Safe = Safe,
      });

    /// <summary>The chord's "Note here", and the dock's New note (Brief ED) with the viewport's centre as its point: the first selected component, or with none, a free note at the point.</summary>
    internal static void NoteHere(System.Drawing.PointF point)
    {
      var doc = Grasshopper.Instances.ActiveCanvas?.Document;
      if (doc == null) return;
      var selection = DwightActions.CurrentSelection().Select(o => o.InstanceGuid).ToList();
      Dwight.UI.DwightNotePanel.OpenNew(doc, Dwight.UI.CanvasNotesCore.AnchorFor(selection), point);
    }

    /// <summary>Fail-safe dispatch (WP-1.5): a menu action must never throw into Rhino's unhandled-exception
    /// machinery (the "never crash Rhino" guardrail). On failure, log, unwedge a busy session, and toast.</summary>
    internal static void Safe(Action action)
    {
      try { action(); }
      catch (Exception ex)
      {
        try { Dwight.Feedback.LastErrors.Record("A menu action failed - " + ex.Message); } catch { }
        try { Rhino.RhinoApp.WriteLine("Dwight: menu action failed - " + ex.Message); } catch { }
        try
        {
          if (DwightSession.IsBusy) DwightSession.Restart();   // a wedged run: hard reset (clears glow, closes panel)
          else Dwight.UI.DwightToast.Show("Dwight hit a problem");
        }
        catch { }
      }
    }
  }
}
