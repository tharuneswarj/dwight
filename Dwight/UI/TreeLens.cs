using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using Dwight.Agent;
using Dwight.Branding;
using Dwight.Data;
using Grasshopper;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;

namespace Dwight.UI
{
  /// <summary>
  /// Brief DB: THE TREE LENS. Chips on the wires of what is lit, saying what travels on them, and a line where data stops. Every word is templated by the plugin
  /// (<see cref="TreeWords"/>, <see cref="TreeStops"/>) from facts read here: no model call anywhere, so it is exact, instant and free. Drawn only: nothing here edits the definition.
  ///
  /// THE ONE SEAM IS <see cref="SetCurrent"/>. Whatever says which step is current calls it: today the panel's step click, and the Walk's step change once Brief DA lands.
  ///
  /// THE FACTS ARE READ ON RHINO'S THREAD, ONCE PER CHANGE, NEVER DURING A SOLUTION (U47, U48: a read inside a solution must have a clock, and this one has none). It runs on the step
  /// change, on the toggle, and after a solution has ended. The read reaches only the wires of what is asked for, and nothing is said of a wire it did not read.
  ///
  /// IT CLEARS WITH THE GLOW (<see cref="CanvasGlow.Clear"/>), and T and Esc are only hooked while it is live.
  /// </summary>
  internal static class TreeLens
  {
    private sealed class Chip
    {
      public IGH_Param From;
      public IGH_Param To;
      public string Text;
      public ChipShape Shape;
      public bool InStep;
      public bool Leaves;     // Brief EB: the far end is outside the selection
      public bool Outgoing;   // ... and the near end is this wire's source
    }

    private const float GlyphCell = 2.5f;
    private const float GlyphGap = 0.8f;
    private const float PortRing = 7f;

    private static readonly Color Ink = UiTokens.TealWire;
    private static readonly Color PillFill = ColorTranslator.FromHtml(UiTokens.SurfaceRaisedHex);

    private static bool _live;
    private static LensMode _mode = LensMode.Step;
    private static readonly HashSet<Guid> _selection = new HashSet<Guid>();
    private static readonly HashSet<Guid> _step = new HashSet<Guid>();
    private static readonly List<Chip> _chips = new List<Chip>();
    private static List<LensStop> _stops = new List<LensStop>();
    private static Guid _port;
    private static int _wiresInView;
    private static bool _capped;
    private static GH_Canvas _canvas;
    private static GH_Document _doc;
    private static object _owner;
    private static Form _keyForm;

    /// <summary>Raised when the lines or the cap's sentence change. Emptied when the lens clears, so a closed panel is not held.</summary>
    public static event Action Changed;

    public static bool IsLive => _live;

    /// <summary>A step is current. Esc can end that from the canvas, so a surface asks rather than remembers.</summary>
    public static bool HasStep => _step.Count > 0;

    /// <summary>The lines for what is lit now.</summary>
    public static IReadOnlyList<LensStop> Stops => _stops;

    /// <summary>The cap's sentence, or null when it does not hold.</summary>
    public static string Note => _live ? LensCap.Line(_mode, _wiresInView, _step.Count > 0) : null;

    /// <summary>An Explain's result is live: this is its selection, and T may ask for its wires. <paramref name="owner"/> is the panel showing it, which a glow of its own never ends.</summary>
    public static void Begin(IEnumerable<Guid> selection, object owner)
    {
      Clear();
      _owner = owner;
      _canvas = Instances.ActiveCanvas;
      _doc = _canvas?.Document;
      if (_canvas == null || _doc == null) return;

      foreach (var g in selection ?? Enumerable.Empty<Guid>()) _selection.Add(g);
      _live = true;
      try { _canvas.KeyDown += OnKeyDown; } catch { }
      try { _keyForm = _canvas.FindForm(); if (_keyForm != null) _keyForm.KeyDown += OnFormKey; } catch { }
      try { _doc.SolutionEnd += OnSolutionEnd; } catch { }
      Read();
    }

    /// <summary>THE SEAM: these components are the current step, or null for none. A Walk's step change calls this, and so does the panel's step click.</summary>
    public static void SetCurrent(IEnumerable<Guid> stepGuids)
    {
      if (!_live) return;
      _step.Clear();
      foreach (var g in stepGuids ?? Enumerable.Empty<Guid>()) _step.Add(g);
      _port = Guid.Empty;
      Read();
      Invalidate();
    }

    /// <summary>A glow is about to belong to <paramref name="owner"/>. The lens goes on only if that is the panel it belongs to: a dock reopen glows after it renders, so a test of the glow's previous owner would end the lens it had just begun.</summary>
    public static void KeepOnlyFor(object owner) { if (_live && !ReferenceEquals(owner, _owner)) Clear(); }

    /// <summary>This panel has closed.</summary>
    public static void ClearIfOwner(object owner) { if (_live && ReferenceEquals(owner, _owner)) Clear(); }

    /// <summary>Everything off: no chips, no lines, no hook. Called by <see cref="CanvasGlow.Clear"/>, so the lens goes with the glow.</summary>
    public static void Clear()
    {
      var had = _live;
      _live = false;
      try { if (_canvas != null) _canvas.KeyDown -= OnKeyDown; } catch { }
      try { if (_keyForm != null) _keyForm.KeyDown -= OnFormKey; } catch { }
      _keyForm = null;
      try { if (_doc != null) _doc.SolutionEnd -= OnSolutionEnd; } catch { }
      _canvas = null;
      _doc = null;
      _owner = null;
      _selection.Clear();
      _step.Clear();
      _chips.Clear();
      _stops = new List<LensStop>();
      _port = Guid.Empty;
      _mode = LensMode.Step;
      _wiresInView = 0;
      _capped = false;
      if (had) Tell();
      Changed = null;
    }

    /// <summary>The step's wires, end to end, for what the wash must leave clear of them. Read from live grips at every paint, as the chips are.</summary>
    internal static List<(PointF A, PointF B)> StepWires()
    {
      var wires = new List<(PointF, PointF)>();
      if (!_live) return wires;
      foreach (var c in _chips)
      {
        if (!c.InStep) continue;
        if (c.Leaves) continue;   // Brief EB: it ends at the selection's edge, so it is not lit and the wash leaves it no band
        try { wires.Add((c.From.Attributes.OutputGrip, c.To.Attributes.InputGrip)); } catch { }
      }
      return wires;
    }

    /// <summary>T, or the Every wire button on the panel or the bar: go between the step's chips and every wire of the selection.</summary>
    public static void Toggle()
    {
      if (!_live) return;
      _mode = LensKeys.Toggled(_mode);
      Read();
      Invalidate();
      WalkSession.LensChanged();   // the bar shows the toggle's state, whichever door moved it
    }

    /// <summary>Is every wire of the selection asked for?</summary>
    public static bool ShowsAll => _live && _mode == LensMode.All;

    /// <summary>A click on a line: select the components it names, frame them, and ring the port it is about. The items on a wire cannot be selected, and the line's action says what is.</summary>
    public static void SelectStop(int index)
    {
      if (!_live || index < 0 || index >= _stops.Count) return;
      var stop = _stops[index];
      if (stop.Select.Count == 0) return;
      try
      {
        var doc = _canvas?.Document;
        if (doc == null) return;
        foreach (var o in doc.Objects) if (o?.Attributes != null && o.Attributes.Selected) o.Attributes.Selected = false;
        foreach (var g in stop.Select)
        {
          var o = doc.FindObject(g, true);
          if (o?.Attributes != null) o.Attributes.Selected = true;
        }
        _port = stop.Port;
        CanvasNav.GoTo(stop.Select, NavReason.UserClicked);
        Invalidate();
      }
      catch { }
    }

    /// <summary>Esc on the canvas. T never arrives here: see <see cref="OnFormKey"/>.</summary>
    private static void OnKeyDown(object sender, KeyEventArgs e)
    {
      try
      {
        var key = e.KeyCode == Keys.Escape ? LensKeys.HideKey : "";
        var claimed = (sender as GH_Canvas)?.ActiveInteraction != null || WalkSession.IsLive;   // a live Walk's Esc is the Walk's: it pauses, which keeps the step's chips, or it leaves, which ends them
        var showing = _step.Count > 0 || _mode == LensMode.All;
        var action = LensKeys.Dispatch(key, _live, e.Modifiers != Keys.None, claimed, showing, CanvasActive());
        if (action == LensKeyAction.None) return;

        e.Handled = true;
        _mode = LensMode.Step; _step.Clear(); _port = Guid.Empty;
        Read();
        Invalidate();
      }
      catch { }
    }

    /// <summary>
    /// T, heard on Grasshopper's form. GH_DocumentEditor.EditorKeyDown, on the form with KeyPreview, sends every bare letter and digit to Rhino's command line and marks it handled, so the canvas's own KeyDown never sees one (read from its IL, 8.36; a read at the tenth gate showed T changing nothing). A second handler on the form still runs, after that send, so T is heard and never consumed, and the letter reaches Rhino as every letter does. A letter lost from a typed Trim would break a workflow; chips flipping on are noise the Every wire button fixes in a click.
    /// </summary>
    private static void OnFormKey(object sender, KeyEventArgs e)
    {
      try
      {
        if (e.KeyCode != Keys.T) return;
        var claimed = _canvas?.ActiveInteraction != null;
        var showing = _step.Count > 0 || _mode == LensMode.All;
        var action = LensKeys.Dispatch(LensKeys.ToggleKey, _live, e.Modifiers != Keys.None, claimed, showing, CanvasActive());
        if (action == LensKeyAction.Toggle) Toggle();
      }
      catch { }
    }

    /// <summary>The canvas holds the keys unless a text box or a combo box does, which is Grasshopper's own test at the head of EditorKeyDown.</summary>
    private static bool CanvasActive()
    {
      var active = _canvas?.FindForm()?.ActiveControl;
      return !(active is TextBoxBase) && !(active is ComboBox);
    }

    /// <summary>A solution has changed the facts. Read them once it is over, never inside it.</summary>
    private static void OnSolutionEnd(object sender, GH_SolutionEventArgs e)
    {
      try
      {
        var canvas = _canvas;
        if (_live && canvas != null && canvas.IsHandleCreated) canvas.BeginInvoke((Action)(() => { if (_live) { Read(); Invalidate(); } }));
      }
      catch { }
    }

    private static void Invalidate() { try { _canvas?.Invalidate(); } catch { } }

    private static void Tell() { try { Changed?.Invoke(); } catch { } }

    // ---- reading the facts ---------------------------------------------------------------------------------------------------------------------

    private static void Read()
    {
      try
      {
        _chips.Clear();
        var doc = _canvas?.Document;
        if (doc == null || !ReferenceEquals(doc, _doc)) { _stops = new List<LensStop>(); return; }
        if (doc.SolutionState == GH_ProcessStep.Process) return;   // U47, U48: the end of this solution reads it

        var wires = new HashSet<(Guid, Guid)>();
        foreach (var g in _step) ReadWires(doc, g, true, wires);
        if (_mode == LensMode.All) foreach (var g in _selection) ReadWires(doc, g, false, wires);

        var lit = _step.Count > 0 ? _step : _selection;
        _stops = TreeStops.Find(lit.Select(g => doc.FindObject(g, true)).Where(o => o != null).Select(ReadNode));
      }
      catch { _stops = new List<LensStop>(); }
      Tell();
    }

    private static void ReadWires(GH_Document doc, Guid guid, bool inStep, HashSet<(Guid, Guid)> seen)
    {
      var obj = doc.FindObject(guid, true);
      if (obj == null) return;
      foreach (var input in InputsOf(obj))
        foreach (var source in input.Sources)
          AddWire(source, input, inStep, seen);
      foreach (var output in OutputsOf(obj))
        foreach (var recipient in output.Recipients)
          AddWire(output, recipient, inStep, seen);
    }

    private static void AddWire(IGH_Param from, IGH_Param to, bool inStep, HashSet<(Guid, Guid)> seen)
    {
      if (from == null || to == null || !seen.Add((from.InstanceGuid, to.InstanceGuid))) return;
      var total = Count(from);
      var sig = SigOf(from);
      var fromOwner = OwnerOf(from).InstanceGuid;
      _chips.Add(new Chip
      {
        From = from,
        To = to,
        InStep = inStep,
        Leaves = LensEdge.Leaves(_selection, fromOwner, OwnerOf(to).InstanceGuid),
        Outgoing = _selection.Contains(fromOwner),
        Text = TreeWords.For(sig, CountsOf(from, total), SourceOf(from)),
        Shape = TreeWords.ShapeOf(sig, total),
      });
    }

    private static TreeSigDto SigOf(IGH_Param p)
    {
      var s = TreeSig.From(p);
      return s == null ? null : new TreeSigDto { Paths = s.Paths, Depth = s.Depth, Items = s.Items };
    }

    private static WireCounts CountsOf(IGH_Param from, long total)
    {
      var owner = OwnerOf(from);
      var active = owner as IGH_ActiveObject;
      var counts = new WireCounts { Total = total };
      if (active != null)
      {
        counts.Solved = active.Phase != GH_SolutionPhase.Blank;
        counts.SourceError = active.RuntimeMessages(GH_RuntimeMessageLevel.Error)?.FirstOrDefault();
      }
      if (owner is IGH_Component c && TreeWords.IsFilter(c.Name) && c.Params.Input.Count > 0) counts.OfSource = Count(c.Params.Input[0]);
      return counts;
    }

    private static WireSource SourceOf(IGH_Param p) => new WireSource { Nick = UserNick(OwnerOf(p)), OutputName = p.Name, TypeHint = p.TypeName };

    private static LensNode ReadNode(IGH_DocumentObject obj)
    {
      var node = new LensNode { Guid = obj.InstanceGuid, Type = obj.Name, Label = LabelOf(obj) };
      if (obj is IGH_ActiveObject a)
      {
        node.Errors.AddRange(a.RuntimeMessages(GH_RuntimeMessageLevel.Error) ?? Enumerable.Empty<string>());
        node.Warnings.AddRange(a.RuntimeMessages(GH_RuntimeMessageLevel.Warning) ?? Enumerable.Empty<string>());
      }
      foreach (var o in OutputsOf(obj))
        node.Outputs.Add(new LensOutput { Guid = o.InstanceGuid, Port = o.NickName, Count = Count(o), Recipients = o.Recipients.Count, Source = SourceOf(o) });
      foreach (var i in InputsOf(obj))
      {
        var input = new LensInput { Port = i.NickName, Name = i.Name, Optional = i.Optional, Count = Count(i) };
        foreach (var s in i.Sources)
        {
          var from = OwnerOf(s);
          if (from != null) input.Feeds.Add(new LensFeed { Node = from.InstanceGuid, Label = LabelOf(from), Count = Count(s) });
        }
        node.Inputs.Add(input);
      }
      return node;
    }

    private static IEnumerable<IGH_Param> InputsOf(IGH_DocumentObject obj)
    {
      if (obj is IGH_Component c) return c.Params.Input;
      return obj is IGH_Param p ? new[] { p } : Enumerable.Empty<IGH_Param>();
    }

    private static IEnumerable<IGH_Param> OutputsOf(IGH_DocumentObject obj)
    {
      if (obj is IGH_Component c) return c.Params.Output;
      return obj is IGH_Param p ? new[] { p } : Enumerable.Empty<IGH_Param>();
    }

    private static IGH_DocumentObject OwnerOf(IGH_Param p)
    {
      try { return p.Attributes?.GetTopLevel?.DocObject ?? p; } catch { return p; }
    }

    private static long Count(IGH_Param p)
    {
      try { return p.VolatileData?.DataCount ?? 0; } catch { return 0; }
    }

    /// <summary>The nickname, only when the person set one.</summary>
    private static string UserNick(IGH_DocumentObject obj)
    {
      var nick = obj?.NickName;
      return string.IsNullOrWhiteSpace(nick) || nick == obj.Name ? null : nick;
    }

    /// <summary>What a person finds on the canvas: the nickname or the type, with the badge number where it wears one. Brief ES: one composer, over the numbers the canvas wears.</summary>
    internal static string LabelOf(IGH_DocumentObject obj)
    {
      return CanvasNumbers.Name(CanvasNumbers.Wearing, UserNick(obj) ?? obj.Name, obj.InstanceGuid);
    }

    // ---- drawing -------------------------------------------------------------------------------------------------------------------------------

    /// <summary>Called from the glow's paint, in document coordinates, so a chip follows its wire through every pan, zoom and move.</summary>
    internal static void Paint(Graphics g, GH_Canvas canvas)
    {
      if (!_live || canvas == null || !ReferenceEquals(canvas.Document, _doc)) return;
      try
      {
        var view = canvas.Viewport.VisibleRegion;
        _wiresInView = _chips.Count(c => InView(c, view));

        var capped = LensCap.Capped(_mode, _wiresInView);
        if (capped != _capped)
        {
          _capped = capped;
          if (canvas.IsHandleCreated) canvas.BeginInvoke((Action)Tell);   // never tell a surface from inside a paint
        }

        // Brief DJ: the step's own wires lit, under the chips that sit on them.
        using (var wire = new Pen(Ink, CanvasLook.LensWireWidth) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round })
          foreach (var (a, b) in StepWires())
          {
            var (c1, c2) = CanvasLook.WireControls(a, b);
            g.DrawBezier(wire, a, c1, c2, b);
          }

        var placed = new List<LensRect>();
        var keepOff = KeepOff(g, canvas.Document, view);
        var labels = PortLabels(g, canvas.Document, view);
        using (var font = new Font("Consolas", CanvasLook.ChipFontPx, FontStyle.Regular, GraphicsUnit.Pixel))
        {
          var seats = new List<(Chip Chip, LensRect Seat, IReadOnlyList<(double X, double Y)> Wire)>();
          foreach (var chip in _chips)
            if (LensCap.Draws(chip.InStep, _mode, _wiresInView) && SeatChip(g, chip, font, placed, keepOff, out var seat, out var wire))
              seats.Add((chip, seat, wire));
          // Every line once every chip has its place, so a line can keep off them all, and before any chip is drawn.
          using (var ink = new Pen(Ink, CanvasLook.PillEdgeWidth))
            foreach (var (_, seat, wire) in seats)
              DrawLeader(g, ink, seat, wire, placed, labels);
          foreach (var (chip, seat, _) in seats)
            DrawChip(g, chip, new RectangleF((float)seat.X, (float)seat.Y, (float)seat.W, (float)seat.H), font);
        }

        DrawPort(g, canvas.Document);
      }
      catch { }
    }

    private static bool InView(Chip c, RectangleF view)
    {
      try
      {
        var a = c.From.Attributes.OutputGrip;
        var b = c.To.Attributes.InputGrip;
        return view.Contains(new PointF((a.X + b.X) / 2f, (a.Y + b.Y) / 2f));
      }
      catch { return false; }
    }

    /// <summary>The fifteenth gate: what a chip keeps off, as measured. Every badge, and every component near the view, whose bounds hold its port labels. A group is not one of them: a chip may sit inside a group.</summary>
    private static List<LensRect> KeepOff(Graphics g, GH_Document doc, RectangleF view)
    {
      var off = new List<LensRect>();
      if (_chips.Count == 0) return off;
      foreach (var r in CanvasGlow.BadgeRects(g, doc)) off.Add(new LensRect(r.X, r.Y, r.Width, r.Height));
      var near = RectangleF.Inflate(view, 200f, 200f);   // a chip can land a little past the view's edge
      foreach (var obj in doc.Objects)
        if (obj is IGH_ActiveObject && obj.Attributes != null && near.IntersectsWith(obj.Attributes.Bounds))
          off.Add(Box(obj.Attributes));
      return off;
    }

    /// <summary>The sixteenth gate: what a chip's line keeps off beside other chips, measured. A line may cross a component's body and not the words on it.</summary>
    private static List<LensRect> PortLabels(Graphics g, GH_Document doc, RectangleF view)
    {
      var labels = new List<LensRect>();
      if (_chips.Count == 0) return labels;
      var near = RectangleF.Inflate(view, 200f, 200f);
      foreach (var obj in doc.Objects)
      {
        if (!(obj is IGH_Component c) || obj.Attributes == null || !near.IntersectsWith(obj.Attributes.Bounds)) continue;
        foreach (var port in c.Params.Input.Concat(c.Params.Output))
        {
          var row = port.Attributes?.Bounds;
          if (row == null) continue;
          var size = g.MeasureString(port.NickName ?? "", GH_FontServer.StandardAdjusted);
          labels.Add(ChipPlace.LabelIn(new LensRect(row.Value.X, row.Value.Y, row.Value.Width, row.Value.Height), size.Width, size.Height));
        }
      }
      return labels;
    }

    /// <summary>Find this chip's place, once, before anything is drawn. False when a wire's end has no attributes to measure.</summary>
    private static bool SeatChip(Graphics g, Chip c, Font font, List<LensRect> placed, List<LensRect> keepOff, out LensRect seat, out IReadOnlyList<(double X, double Y)> points)
    {
      seat = default;
      points = null;
      var fromAttr = c.From.Attributes;
      var toAttr = c.To.Attributes;
      if (fromAttr == null || toAttr == null) return false;
      var a = fromAttr.OutputGrip;
      var b = toAttr.InputGrip;

      var size = g.MeasureString(c.Text, font);
      var glyph = GlyphWidth(c.Shape);
      var w = size.Width + glyph + 8f;
      var h = Math.Max(size.Height, GlyphRows(c.Shape) * (GlyphCell + GlyphGap)) + 2f;

      var (c1, c2) = CanvasLook.WireControls(a, b);
      points = ChipPlace.WirePoints(a.X, a.Y, c1.X, c1.Y, c2.X, c2.Y, b.X, b.Y);
      seat = ChipPlace.Seat(new ChipWire
      {
        FromX = a.X, FromY = a.Y, ToX = b.X, ToY = b.Y,
        FromBox = Box(fromAttr), ToBox = Box(toAttr),
        FromSelected = !c.Leaves || c.Outgoing,
        ToSelected = !c.Leaves || !c.Outgoing,
        Points = points,
      }, w, h, placed, keepOff);
      placed.Add(seat);
      return true;
    }

    /// <summary>A chip that is not on its wire says which wire it means, with a line to that wire.</summary>
    private static void DrawLeader(Graphics g, Pen ink, LensRect seat, IReadOnlyList<(double X, double Y)> wire, List<LensRect> placed, List<LensRect> labels)
    {
      var line = ChipPlace.Leader(seat, wire, placed.Where(o => !o.Equals(seat)), labels);
      if (line == null) return;
      g.DrawLines(ink, line.Select(p => new PointF((float)p.X, (float)p.Y)).ToArray());
    }

    private static void DrawChip(Graphics g, Chip c, RectangleF rect, Font font)
    {
      var glyph = GlyphWidth(c.Shape);
      using (var ink = new Pen(Ink, CanvasLook.PillEdgeWidth))
      using (var mark = new SolidBrush(Ink))
      using (var fill = new SolidBrush(PillFill))
      using (var text = new SolidBrush(Ink))
      using (var pill = CanvasGlow.Rounded(rect, CanvasLook.PillRadius(rect.Height)))
      using (var format = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center })
      {
        g.FillPath(fill, pill);
        g.DrawPath(ink, pill);
        DrawGlyph(g, c.Shape, rect, mark);
        g.DrawString(c.Text, font, text, new RectangleF(rect.X + 4f + glyph, rect.Y, rect.Width - 4f - glyph, rect.Height), format);
      }
    }

    private static LensRect Box(IGH_Attributes attributes)
    {
      // A port's GetTopLevel is not the component's live attributes: measured on the sixteenth build, its Bounds read a default (0, 0, 150, 20). Its DocObject is the component, whose own attributes are right.
      var b = (attributes.GetTopLevel?.DocObject?.Attributes ?? attributes).Bounds;
      return new LensRect(b.X, b.Y, b.Width, b.Height);
    }

    private static int GlyphRows(ChipShape shape) => shape == ChipShape.Tree ? 2 : shape == ChipShape.None ? 0 : 1;

    private static int GlyphColumns(ChipShape shape) => shape == ChipShape.Item ? 1 : shape == ChipShape.None ? 0 : 5;

    private static float GlyphWidth(ChipShape shape)
    {
      var columns = GlyphColumns(shape);
      return columns == 0 ? 0f : columns * (GlyphCell + GlyphGap) + 4f;
    }

    /// <summary>One square for one item, a row for a list, two rows for a tree: the shape, drawn square as the drawing set's corners are.</summary>
    private static void DrawGlyph(Graphics g, ChipShape shape, RectangleF chip, Brush ink)
    {
      var rows = GlyphRows(shape);
      var columns = GlyphColumns(shape);
      if (rows == 0) return;
      var top = chip.Y + (chip.Height - (rows * (GlyphCell + GlyphGap) - GlyphGap)) / 2f;
      for (var r = 0; r < rows; r++)
        for (var c = 0; c < columns; c++)
          g.FillRectangle(ink, chip.X + 5f + c * (GlyphCell + GlyphGap), top + r * (GlyphCell + GlyphGap), GlyphCell, GlyphCell);
    }

    /// <summary>The ring on the port a clicked line is about.</summary>
    private static void DrawPort(Graphics g, GH_Document doc)
    {
      if (_port == Guid.Empty) return;
      var attributes = (doc.FindObject(_port, false) as IGH_Param)?.Attributes;
      if (attributes == null) return;
      var p = attributes.OutputGrip;
      using (var pen = new Pen(CanvasLook.Edge(CanvasGlow.CanvasColour), 2f))
        g.DrawEllipse(pen, p.X - PortRing / 2f, p.Y - PortRing / 2f, PortRing, PortRing);
    }
  }
}
