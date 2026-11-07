using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using Grasshopper;
using Grasshopper.GUI;
using Grasshopper.GUI.Canvas;
using Grasshopper.GUI.Canvas.Interaction;
using Grasshopper.Kernel;
using Dwight.Agent;
using Dwight.Branding;
using Dwight.Config;
using Dwight.Session;

namespace Dwight.UI
{
  /// <summary>
  /// DWIGHT NOTES ON THE CANVAS (Brief DC, PR 2; dots and the drag, Brief ED): the paint and the mouse, as a thin shim over <see cref="CanvasNotesCore"/>,
  /// <see cref="NoteDots"/> and <see cref="NoteDrag"/> (which decide which notes draw, as what, what a click lands on and where a dragged note goes). A note is
  /// drawn in document coordinates beside its component, whenever the document is open, in the library's note look through <see cref="UiTokens"/> and never a
  /// literal colour; too small to read, it is a dot on the overlay. Drawing a note changes no definition, takes no undo record and asks nothing.
  ///
  /// <para>It never captures a click Grasshopper would otherwise take, with one exception. A click is heard on mouse UP and only for a click (no drag) that lands
  /// on a card's own rectangle or a dot's own circle; Grasshopper has already handled the same click by then. The exception is a press on a card's header, which
  /// moves the card (Tharun's ruling): there <see cref="NoteDragInteraction"/> takes over from the window select or the drag Grasshopper had just chosen.</para>
  ///
  /// <para>A note's STATE is read through <see cref="GhNotes"/>'s stamp, cached per solution: a stamp is a fingerprint read, too costly to take on
  /// every pan, and a component's stamp can change only with a solution.</para>
  ///
  /// Fail-safe (ADR-029): a paint or a click that throws costs the note, never Rhino.
  /// </summary>
  internal static class CanvasNotes
  {
    private static GH_Canvas _canvas;
    private static GH_Document _hookedDoc;
    private static readonly Dictionary<string, NoteStatus> _status = new Dictionary<string, NoteStatus>(StringComparer.Ordinal);
    private static IReadOnlyList<NoteRect> _drawn = new List<NoteRect>();
    private static IReadOnlyList<DotSource> _dotSources = new List<DotSource>();
    private static IReadOnlyList<NoteDot> _dots = new List<NoteDot>();
    private static Point _down;
    private static int _hidden;
    private static int _total;
    private static RectangleF _chip;
    private static readonly NoteReveal _reveal = new NoteReveal();
    private static NoteDrag _drag;
    private static bool _dropped;   // the release that ended a drag is not a click

    private static readonly Dictionary<string, (string Text, NoteLayoutResult Layout)> _layouts = new Dictionary<string, (string, NoteLayoutResult)>(StringComparer.Ordinal);

    public static void Attach(GH_Canvas canvas)
    {
      if (canvas == null) return;
      if (_canvas != null)
      {
        _canvas.CanvasPostPaintObjects -= OnPaint;
        _canvas.CanvasPostPaintOverlay -= OnOverlay;
        _canvas.MouseDown -= OnDown;
        _canvas.MouseUp -= OnUp;
      }
      _canvas = canvas;
      _canvas.CanvasPostPaintObjects += OnPaint;
      _canvas.CanvasPostPaintOverlay += OnOverlay;
      _canvas.MouseDown += OnDown;
      _canvas.MouseUp += OnUp;
      CommandHistory.NotesChanged += () => { _status.Clear(); _layouts.Clear(); Invalidate(); };
      DwightTheme.Changed += _ => Invalidate();
    }

    private static void Invalidate() { try { _canvas?.Invalidate(); } catch { } }

    // ---- shown or hidden, one setting with three doors ---------------------------------------------------------------------------------

    /// <summary>Raised when the setting changes, so the dock and Settings say what the chord or the chip just did.</summary>
    public static event Action ShownChanged;

    /// <summary>Raised after a solution, when a note's state may have changed, so the dock's Notes pane says it in the card's words.</summary>
    public static event Action StatesChanged;

    public static bool Shown => DwightSettings.Current.ShowNotes;

    /// <summary>The one way the setting changes: the dock's row, Settings, the chord and the chip all come here.</summary>
    public static void SetShown(bool on)
    {
      var st = DwightSettings.Current;
      if (st.ShowNotes == on) return;
      st.ShowNotes = on;
      st.Save();
      _reveal.Clear();
      Invalidate();
      try { ShownChanged?.Invoke(); } catch { }
    }

    /// <summary>A note was just added. While notes are hidden it shows itself, with the setting unchanged, so the person sees it land.</summary>
    public static void Reveal(string noteId) { if (!Shown) _reveal.Set(noteId); }

    private static bool Dark => DwightTheme.Current() == "dark";

    // ---- state, cached per solution --------------------------------------------------------------------------------------------------

    private static void HookSolution(GH_Document doc)
    {
      if (doc == null || ReferenceEquals(doc, _hookedDoc)) return;
      _hookedDoc = doc;
      _status.Clear();
      _reveal.Clear();
      doc.SolutionEnd += (s, e) => { _status.Clear(); Invalidate(); try { StatesChanged?.Invoke(); } catch { } };
    }

    private static NoteStatus StatusOf(GH_Document doc, StoredNote note)
    {
      if (_status.TryGetValue(note.Id, out var cached)) return cached;
      var status = NoteStatus.Fresh;
      try
      {
        if (Guid.TryParse(note.Anchor, out var guid))
        {
          var obj = doc.FindObject(guid, false);
          string current = null;
          if (obj != null && !string.IsNullOrEmpty(note.Stamp)) current = GhAgent.StampOf(obj)?.Digest;
          status = NoteState.Of(note, obj != null, current);
        }
      }
      catch { }
      _status[note.Id] = status;
      return status;
    }

    // ---- where a note sits -------------------------------------------------------------------------------------------------------------

    /// <summary>The top-left of a note's card in document coordinates (<see cref="NotePlace.Origin"/>), or where the pointer has it while it is being dragged.</summary>
    internal static PointF? Origin(GH_Document doc, StoredNote note)
    {
      var drag = _drag;
      if (drag != null && drag.Moved && drag.NoteId == note.Id) return new PointF((float)drag.X, (float)drag.Y);
      var o = NotePlace.Origin(note, Corner(doc, note));
      return o == null ? (PointF?)null : new PointF((float)o.Value.X, (float)o.Value.Y);
    }

    /// <summary>The top-left corner of the component a note is pinned to, or null when it is free or its component is not on the canvas.</summary>
    internal static (double X, double Y)? Corner(GH_Document doc, StoredNote note)
    {
      if (doc == null || !Guid.TryParse(note?.Anchor, out var guid)) return null;
      var b = doc.FindObject(guid, false)?.Attributes?.Bounds;
      return b == null ? ((double, double)?)null : (b.Value.X, b.Value.Y);
    }

    /// <summary>A note's body, laid out once for its words. GDI's widths are the same at every paint, so a pan measures nothing.</summary>
    private static NoteLayoutResult LayoutOf(Graphics g, StoredNote note)
    {
      if (_layouts.TryGetValue(note.Id, out var kept) && kept.Text == note.Text) return kept.Layout;
      var layout = NoteLayout.Build(NoteMarkdown.Parse(note.Text), new GdiNoteMetrics(g));
      _layouts[note.Id] = (note.Text, layout);
      return layout;
    }

    // ---- paint -----------------------------------------------------------------------------------------------------------------------

    private static void OnPaint(GH_Canvas sender)
    {
      _drawn = new List<NoteRect>();
      _dotSources = new List<DotSource>();
      _hidden = 0;
      _total = 0;
      try
      {
        var g = sender?.Graphics;
        var doc = sender?.Document;
        if (g == null || doc == null) return;
        HookSolution(doc);
        var notes = CommandHistory.Notes(doc);
        _total = notes.Count;   // before any return: the count of hidden notes shows at any zoom
        if (notes.Count == 0) return;
        var show = Shown;
        var revealed = show ? null : _reveal.At(DateTime.UtcNow);
        if (!show && revealed == null) return;   // hidden: nothing is drawn, so _drawn stays empty and nothing can be hit

        var view = sender.Viewport.VisibleRegion;
        var zoom = sender.Viewport.Zoom;
        var byId = new Dictionary<string, (StoredNote Note, NoteStatus Status, PointF Origin)>(StringComparer.Ordinal);
        var rough = new List<NoteRect>();
        foreach (var n in notes)
        {
          var o = Origin(doc, n);
          if (o == null) continue;
          var st = StatusOf(doc, n);
          byId[n.Id] = (n, st, o.Value);
          var layout = LayoutOf(g, n);
          rough.Add(new NoteRect(n.Id, o.Value.X, o.Value.Y, NoteLayout.CardWidth(layout), NoteLayout.CardHeight(layout, st != NoteStatus.Fresh), st != NoteStatus.Fresh));
        }

        // Brief ED: a card too narrow on screen to read is a dot, drawn on the overlay at one size at every zoom. The rest are cards, at most MaxDrawn of them.
        var visible = NotesVisibility.Visible(rough, show, revealed).ToList();
        var chosen = CanvasNotesCore.Choose(visible.Where(r => CanvasNotesCore.CardsAtZoom(zoom, r.W)), view.X, view.Y, view.Width, view.Height, out _hidden);
        var small = CanvasNotesCore.Choose(visible.Where(r => !CanvasNotesCore.CardsAtZoom(zoom, r.W)), view.X, view.Y, view.Width, view.Height, out var past, NoteDots.MaxDots);
        _hidden += past;
        _dotSources = small.Select(r =>
        {
          var at = sender.Viewport.ProjectPoint(new PointF((float)r.X, (float)r.Y));
          return new DotSource(r, at.X, at.Y, byId[r.Id].Status);
        }).ToList();

        var exact = new List<NoteRect>();
        var prior = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        try
        {
          // Farthest first, so the nearest is on top; the hit test walks the other way.
          foreach (var r in chosen.Reverse())
          {
            var (note, status, origin) = byId[r.Id];
            if (RectangleF.Intersect(new RectangleF(origin.X, origin.Y, (float)r.W, (float)r.H), view).IsEmpty) { exact.Add(r); continue; }   // off screen: not painted, not hit
            exact.Add(DrawCard(g, doc, note, status, origin));
          }
        }
        finally { g.SmoothingMode = prior; }
        _drawn = exact.AsEnumerable().Reverse().ToList();
      }
      catch { _drawn = new List<NoteRect>(); _dotSources = new List<DotSource>(); }
    }

    private static NoteRect DrawCard(Graphics g, GH_Document doc, StoredNote note, NoteStatus status, PointF origin)
    {
      var layout = LayoutOf(g, note);
      var stale = NoteState.Sentence(status);
      var width = (float)NoteLayout.CardWidth(layout);
      var height = (float)NoteLayout.CardHeight(layout, stale != null);
      var dark = Dark;

      // The leader: a dotted line from the card to the component it is about, when that component is still here.
      if (Guid.TryParse(note.Anchor, out var guid))
      {
        var b = doc.FindObject(guid, false)?.Attributes?.Bounds;
        if (b != null)
          using (var pen = new Pen(UiTokens.Subtle(dark), 1f) { DashStyle = DashStyle.Dot })
            g.DrawLine(pen, origin.X + width / 2f, origin.Y, b.Value.Left + b.Value.Width / 2f, b.Value.Bottom);
      }

      NoteCard.Draw(g, origin, layout, note.By == DwightNotes.ByDwight, DateLabel(note.WhenUtc), stale, dark);
      return new NoteRect(note.Id, origin.X, origin.Y, width, height, status != NoteStatus.Fresh);
    }

    /// <summary>"31 Oct": the day, in the person's own calendar. A note with no readable time shows none.</summary>
    private static string DateLabel(string whenUtc) =>
      DateTime.TryParse(whenUtc, null, DateTimeStyles.RoundtripKind, out var t)
        ? t.ToLocalTime().ToString("d MMM", CultureInfo.CurrentCulture) : "";

    /// <summary>
    /// The dots and the count chip, in screen pixels. The overlay event's Graphics carries the viewport's matrix on Grasshopper 8.36 (it is raised from the middle ground,
    /// where the cards are drawn), so <see cref="NoteDotPainter"/> resets it for its own drawing and puts it back (Brief EH). The chip says "and N more notes" when a cap holds,
    /// or "N notes" when they are hidden, where a click on it shows them.
    /// </summary>
    private static void OnOverlay(GH_Canvas sender)
    {
      _chip = RectangleF.Empty;
      DrawDots(sender);
      var text = NotesVisibility.Chip(Shown, _total, _hidden);
      if (text == null) return;
      try { _chip = NoteDotPainter.DrawChip(sender.Graphics, text, sender.Height, Dark); }
      catch { }
    }

    /// <summary>Brief ED: every note too small to read as a card is a dot, merged with its neighbours into one that says how many.</summary>
    private static void DrawDots(GH_Canvas sender)
    {
      _dots = new List<NoteDot>();
      try
      {
        var g = sender.Graphics;
        if (g == null || _dotSources.Count == 0) return;
        var font = NoteCard.CountFont;
        // A typographic measure is in world units, the same under the viewport's matrix and any hint (measured, Brief EH), so the count is measured here as drawn.
        _dots = NoteDots.Merge(_dotSources, sender.Width, sender.Height, s => g.MeasureString(s, font, PointF.Empty, NoteCard.Exact).Width);
        NoteDotPainter.Draw(g, _dots, font, Dark);
      }
      catch { _dots = new List<NoteDot>(); }
    }

    // ---- the press, the drag and the click -------------------------------------------------------------------------------------------

    /// <summary>
    /// A press on a card's header starts a drag (Brief ED). Grasshopper's own handler has already run, because <c>GH_Canvas</c> subscribes it in its constructor, and has
    /// chosen its interaction: a window select on empty canvas, or a drag of the object under the press. Only those two are taken over; a wire, a captured slider, a menu
    /// or a modifier is Grasshopper's, and the press is left alone.
    /// </summary>
    private static void OnDown(object sender, MouseEventArgs e)
    {
      _down = e.Location;
      _dropped = false;
      try
      {
        if (e.Button != MouseButtons.Left || Control.ModifierKeys != Keys.None || _drawn.Count == 0) return;
        var canvas = sender as GH_Canvas ?? _canvas;
        var doc = canvas?.Document;
        if (doc == null || canvas.IsActiveObject || canvas.IsActiveWidget) return;
        var active = canvas.ActiveInteraction;
        if (active != null && !(active is GH_WindowSelectInteraction) && !(active is GH_DragInteraction)) return;
        var p = canvas.Viewport.UnprojectPoint(e.Location);
        var id = CanvasNotesCore.HeaderAt(_drawn, p.X, p.Y);
        if (id == null) return;
        _drag = null;
        var note = CommandHistory.Notes(doc).FirstOrDefault(n => n.Id == id);
        var origin = note == null ? null : Origin(doc, note);
        if (origin == null) return;
        _drag = new NoteDrag(id, origin.Value.X, origin.Value.Y, p.X, p.Y, (noteId, x, y) => Drop(doc, noteId, x, y));
        canvas.ActiveInteraction = new NoteDragInteraction(canvas, new GH_CanvasMouseEvent(canvas.Viewport, e));
      }
      catch { _drag = null; }
    }

    /// <summary>The pointer moved while a card is held: it follows, once the move is past a click's slop.</summary>
    internal static void DragMove(double x, double y, int pixelsX, int pixelsY)
    {
      try
      {
        var drag = _drag;
        if (drag == null) return;
        drag.Move(x, y, pixelsX, pixelsY);
        if (drag.Moved) Invalidate();
      }
      catch { }
    }

    /// <summary>The card was let go (one write, through the per-file store), or the canvas lost the mouse (nothing written, the card goes back).</summary>
    internal static void DragEnd(bool released)
    {
      var drag = _drag;
      _drag = null;
      if (drag == null) return;
      try { if (released) _dropped = drag.Drop(); else drag.Abandon(); } catch { }
      Invalidate();
    }

    private static void Drop(GH_Document doc, string noteId, double x, double y)
    {
      var note = CommandHistory.Notes(doc).FirstOrDefault(n => n.Id == noteId);
      if (note != null) CommandHistory.PlaceNote(doc, noteId, NotePlace.Moved(note, x, y, Corner(doc, note)));
    }

    private static void OnUp(object sender, MouseEventArgs e)
    {
      try
      {
        if (e.Button != MouseButtons.Left) return;
        if (_dropped) { _dropped = false; return; }                     // the release that ended a drag is not a click
        if (NoteDrag.IsDrag(e.X - _down.X, e.Y - _down.Y)) return;      // a drag is not a click
        if (Control.ModifierKeys != Keys.None) return;                  // a chord or a multi-select is not a note click
        if (NotesVisibility.ChipClickShows(Shown, _total) && _chip.Contains(e.Location)) { SetShown(true); return; }
        var canvas = sender as GH_Canvas ?? _canvas;
        var doc = canvas?.Document;
        if (doc == null) return;

        var dot = NoteDots.At(_dots, e.X, e.Y);   // the overlay is above the cards, so a dot is asked first
        if (dot != null) { ClickDot(canvas, doc, dot); return; }
        if (_drawn.Count == 0) return;

        var p = canvas.Viewport.UnprojectPoint(e.Location);
        var id = CanvasNotesCore.HitTest(_drawn, p.X, p.Y);
        if (id == null) return;
        var note = CommandHistory.Notes(doc).FirstOrDefault(n => n.Id == id);
        if (note == null) return;

        SelectAnchor(doc, note);
        DwightNotePanel.Open(doc, note);
      }
      catch { }
    }

    /// <summary>A dot does what its card does. A merged dot zooms in until its notes part (<see cref="NoteDots.Separate"/>), and a second click opens one.</summary>
    private static void ClickDot(GH_Canvas canvas, GH_Document doc, NoteDot dot)
    {
      if (!dot.Merged)
      {
        var note = CommandHistory.Notes(doc).FirstOrDefault(n => n.Id == dot.Members[0].Rect.Id);
        if (note == null) return;
        SelectAnchor(doc, note);
        DwightNotePanel.Open(doc, note);
        return;
      }
      var (zoom, x, y) = NoteDots.Separate(dot.Members.Select(m => m.Rect).ToList(), canvas.Viewport.Zoom);
      CanvasNav.Centre(new PointF((float)x, (float)y), (float)zoom, NavReason.UserClicked);
    }

    /// <summary>A click on a note selects the component it is about, so the person sees what it is about and a verb run next has it in scope.</summary>
    private static void SelectAnchor(GH_Document doc, StoredNote note)
    {
      if (!Guid.TryParse(note.Anchor, out var guid)) return;
      var obj = doc.FindObject(guid, false);
      if (obj == null) return;
      foreach (var o in doc.SelectedObjects().ToList()) o.Attributes.Selected = false;
      obj.Attributes.Selected = true;
      Invalidate();
    }

    // ---- the dock's Notes pane (Brief ED, unit 2) -------------------------------------------------------------------------------------

    /// <summary>The pane for a document, its states read from the cache the canvas draws from.</summary>
    public static string PaneJson(GH_Document doc)
    {
      try
      {
        if (doc != null) HookSolution(doc);
        var notes = doc == null ? (IReadOnlyList<StoredNote>)Array.Empty<StoredNote>() : CommandHistory.Notes(doc);
        return NotesPane.View(notes, n => (AnchorName(doc, n), StatusOf(doc, n)), DateTime.UtcNow).ToJsonString();
      }
      catch { return NotesPane.View(null, null, DateTime.UtcNow).ToJsonString(); }
    }

    private static string AnchorName(GH_Document doc, StoredNote note) =>
      Guid.TryParse(note?.Anchor, out var guid) ? doc.FindObject(guid, false)?.NickName : null;

    /// <summary>
    /// A row was clicked: the canvas pans so the note's card, or its dot, is in the middle at the zoom it is at, its component is selected, and while notes are hidden it
    /// shows itself as a new note does. Nothing else changes.
    /// </summary>
    public static void GoTo(GH_Document doc, string noteId)
    {
      try
      {
        var canvas = Instances.ActiveCanvas ?? _canvas;
        if (doc == null || canvas == null) return;
        HookSolution(doc);
        var note = CommandHistory.Notes(doc).FirstOrDefault(n => n.Id == noteId);
        var origin = note == null ? null : Origin(doc, note);
        if (origin == null) return;
        NoteLayoutResult layout;
        using (var g = canvas.CreateGraphics()) layout = LayoutOf(g, note);
        var stale = StatusOf(doc, note) != NoteStatus.Fresh;
        var (x, y) = NotesPane.GoTarget(origin.Value.X, origin.Value.Y, NoteLayout.CardWidth(layout), NoteLayout.CardHeight(layout, stale), canvas.Viewport.Zoom);
        SelectAnchor(doc, note);
        Reveal(note.Id);
        CanvasNav.Centre(new PointF((float)x, (float)y), null, NavReason.UserClicked);
      }
      catch { }
    }
  }

  /// <summary>
  /// THE DRAG OF A NOTE'S CARD, as one of Grasshopper's own interactions (Brief ED, unit 3). Read off Grasshopper 8.36's IL with no Rhino running: <c>GH_Canvas</c>
  /// subscribes its mouse-down handler in its constructor, so ours runs after it has chosen an interaction, and <c>ActiveInteraction</c>'s setter destroys the old one and
  /// takes the new one with no condition. From then on the canvas sends the moves and the release here, and not to the object under the card. Every decision is
  /// <see cref="NoteDrag"/>'s.
  /// </summary>
  internal sealed class NoteDragInteraction : GH_AbstractInteraction
  {
    public NoteDragInteraction(GH_Canvas canvas, GH_CanvasMouseEvent e) : base(canvas, e, true) { }

    public override GH_ObjectResponse RespondToMouseMove(GH_Canvas sender, GH_CanvasMouseEvent e)
    {
      base.RespondToMouseMove(sender, e);
      CanvasNotes.DragMove(e.CanvasX, e.CanvasY, e.ControlX - ControlPointDown.X, e.ControlY - ControlPointDown.Y);
      return GH_ObjectResponse.Handled;
    }

    public override GH_ObjectResponse RespondToMouseUp(GH_Canvas sender, GH_CanvasMouseEvent e)
    {
      CanvasNotes.DragEnd(true);
      return GH_ObjectResponse.Release;
    }

    public override void Destroy()
    {
      CanvasNotes.DragEnd(false);
      base.Destroy();
    }
  }
}
