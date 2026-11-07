using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using Grasshopper;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using Eto.Forms;

namespace Dwight.UI
{
  /// <summary>A soft amber halo around the bound components (spec §7): a read-only GH canvas paint hook
  /// (no mutation, no undo). Breathes lavender while a request is in flight, settles to amber while the
  /// panel is open. Drawn in CanvasPostPaintObjects, in document coordinates.</summary>
  internal static class CanvasGlow
  {
    private static readonly List<Guid> _guids = new List<Guid>();         // primary glow (full intensity)
    private static readonly List<Guid> _secondary = new List<Guid>();     // secondary glow (lighter) - the chain behind the target (D4)
    private static readonly List<CanvasMark> _marks = new List<CanvasMark>();  // on-canvas Explain annotation layer
    private static bool _thinking;
    private static bool _hooked;
    private static Grasshopper.Kernel.GH_Document _hookedDoc;   // WP-4.7: the document whose SolutionEnd we hooked
    private static GH_Canvas _canvas;
    private static UITimer _pulse;
    private static float _phase;
    private static object _owner;
    private static readonly List<Guid> _hatch = new List<Guid>();   // Brief DE: the components whose value a practice question is covering
    private static bool _walkWash;   // Brief DA: only ShowWalkStep and the Dim switch set it, and every other way of setting a glow clears it, so a glow from anything else cannot have the wash

    // Phase-10 reskin: hexes from UiTokens (the C# token mirror) - AMBER settled, lavender only while
    // thinking, the Diagnose root red kept distinct, the annotation ink retuned to the ecosystem teal.
    private static readonly Color Amber = Dwight.Branding.UiTokens.Brand;
    private static readonly Color Lavender = Dwight.Branding.UiTokens.Lavender;
    private static readonly Color RootRed = Color.FromArgb(179, 38, 30);   // the Diagnose root-cause glow - distinct + dark
    private static Color _primaryColor = Amber;   // colour of the primary (full-intensity) glow tier
    // The annotation ink is the ecosystem teal - distinct from the amber/lavender glow so the
    // layers never stack into mud, vivid on the light canvas, with a soft glow underlay for the highlight.
    private static readonly Color MarkInk = Dwight.Branding.UiTokens.TealWire;   // Brief DJ: measured against both canvas colours
    private static Color MarkGlow => Color.FromArgb(CanvasLook.MarkHaloAlpha, MarkInk);

    /// <summary>The canvas's colour now, which is what a colour that cannot serve both themes chooses by. Read at every paint: a person can change their skin.</summary>
    internal static Color CanvasColour => WalkGeometry.CanvasColour(GH_Skin.canvas_mono, GH_Skin.canvas_mono_color, GH_Skin.canvas_back);
    private static readonly Color BadgeInk = Dwight.Branding.UiTokens.BrandInk;  // the id badge fill

    public static void Show(IEnumerable<Guid> guids, bool thinking, object owner = null)
    {
      if (thinking) TreeLens.Clear(); else TreeLens.KeepOnlyFor(owner);   // Brief DB: a run starting, or another panel's glow, ends the lens
      _owner = owner;
      _walkWash = false;
      _hatch.Clear();
      _guids.Clear();
      _secondary.Clear();
      _marks.Clear();                  // a fresh glow target supersedes any prior step's marks
      _primaryColor = Amber;
      if (guids != null) _guids.AddRange(guids);
      _thinking = thinking;
      if (!thinking) PutNumbersOf(owner);
      Hook();
      Pulse(thinking);
      Invalidate();
    }

    /// <summary>A two-tier settled glow (D4): the target set at full intensity, the chain behind it
    /// lighter. Used by Diagnose (root/target dark, the rest of the chain light) and its dock reopen.</summary>
    public static void ShowTiered(IEnumerable<Guid> primary, IEnumerable<Guid> secondary, object owner = null)
      => ShowTieredInner(primary, secondary, owner, Amber);

    /// <summary>Two-tier glow for Diagnose: the root cause(s) in a distinct DARK RED, the rest of the
    /// chain in the duller default - so the root reads at a glance on the canvas (Tharun).</summary>
    public static void ShowDiagnosis(IEnumerable<Guid> primary, IEnumerable<Guid> secondary, object owner = null)
      => ShowTieredInner(primary, secondary, owner, RootRed);

    private static void ShowTieredInner(IEnumerable<Guid> primary, IEnumerable<Guid> secondary, object owner, Color primaryColor)
    {
      TreeLens.Clear();   // Brief DB: only an Explain has a lens
      _owner = owner;
      _walkWash = false;
      _guids.Clear();
      _secondary.Clear();
      _marks.Clear();
      _primaryColor = primaryColor;
      if (primary != null) _guids.AddRange(primary);
      if (secondary != null) foreach (var g in secondary) if (!_guids.Contains(g)) _secondary.Add(g);
      _thinking = false;
      PutNumbersOf(owner);
      Hook();
      Pulse(false);
      Invalidate();
    }

    /// <summary>Set the guid -> id map for the canvas number-badges (ADR-079). Drawn for whichever
    /// components are currently glowing, so the badges track the active step/fault/selection, not a
    /// persistent payload-wide overlay (architect note). Cleared with the glow. Brief ES: a surface's words are in this numbering, so the canvas wears it and only it.</summary>
    public static void SetLabels(IReadOnlyDictionary<Guid, int> map)
    {
      CanvasNumbers.Wrote(map);
      Invalidate();
    }

    /// <summary>
    /// Brief ES unit 1: the canvas wears the numbers of the surface a person is reading. A page that lights components, writes words while it is shown, or is
    /// turned to puts its own numbering on them: a chat's is its registry's, a verb's its run's, and the agent's show_on_canvas lights in its chat's. A page
    /// with no numbering yet (a run still starting) leaves the canvas as it was.
    /// </summary>
    internal static void PutNumbersOf(object owner)
    {
      try
      {
        var numbers = owner is WebPanel page ? page.Numbers()
                    : owner is Dwight.Agent.AgentScope scope ? CanvasNumbers.Of(scope.Ids)
                    : null;
        if (numbers != null) CanvasNumbers.Wrote(numbers);
        Invalidate();
      }
      catch { }
    }

    public static void SetThinking(bool thinking) { _thinking = thinking; Pulse(thinking); Invalidate(); }

    public static void Clear() { TreeLens.Clear(); _walkWash = false; _hatch.Clear(); _guids.Clear(); _secondary.Clear(); _marks.Clear(); CanvasNumbers.Clear(); _primaryColor = Amber; _thinking = false; _owner = null; Pulse(false); Invalidate(); }

    /// <summary>Draw an on-canvas Explain step's annotation marks (ADR-065): ephemeral, read-only Overlay
    /// marks anchored to component ids and recomputed from live bounds every paint (so pan/zoom never
    /// desyncs them). Replaces the previous step's marks. <paramref name="glow"/> frames the step's
    /// components beneath the marks; passing it keeps the glow + marks on one owner so they clear
    /// together.</summary>
    public static void ShowMarks(IEnumerable<Guid> glow, IEnumerable<CanvasMark> marks, object owner)
    {
      TreeLens.KeepOnlyFor(owner);   // Brief DB: a step of the same panel keeps it
      _owner = owner;
      _walkWash = false;
      _guids.Clear();
      _secondary.Clear();
      _primaryColor = Amber;
      if (glow != null) _guids.AddRange(glow);
      _marks.Clear();
      if (marks != null) _marks.AddRange(marks);
      _thinking = false;
      PutNumbersOf(owner);
      Hook();
      HookSolution();
      Pulse(false);
      Invalidate();
    }

    public static void ClearMarks() { _marks.Clear(); Invalidate(); }

    /// <summary>Brief DA: a Walk's step: the same glow and marks as a step click, with the rest of the canvas washed. The holes are read from live bounds at every paint. Brief DH: the wash is a choice, set here and by the Dim switch (<see cref="SetWalkWash"/>, Brief EC).</summary>
    public static void ShowWalkStep(IEnumerable<Guid> glow, IEnumerable<CanvasMark> marks, object owner, bool wash)
    {
      ShowMarks(glow, marks, owner);
      _walkWash = wash;
      Invalidate();
    }

    /// <summary>Brief EC unit 2: the Dim switch moved while a step is lit. Only the wash changes, in this call; the glow, the marks and the lens stay as they are.</summary>
    public static void SetWalkWash(bool wash, object owner)
    {
      if (!ReferenceEquals(owner, _owner)) return;   // a glow that is not the Walk's (a chat's click on an id, unit 5b) is not washed
      _walkWash = wash;
      Invalidate();
    }

    /// <summary>
    /// Brief EC unit 4: the window was minimised on a step. The step's glow, marks, cover and wash come off the canvas; the owner, the badges and the lens keep their state, so the step comes back
    /// as it was when the window does. Nothing is painted while the lists are empty, the lens's chips included.
    /// </summary>
    public static void HideWalkStep()
    {
      _walkWash = false;
      _hatch.Clear();
      _guids.Clear();
      _secondary.Clear();
      _marks.Clear();
      Invalidate();
    }

    /// <summary>The Walk has left and nothing is to be lit in its place: another run is starting, the document changed, or its page has gone.</summary>
    public static void ClearWalkLayer() { _walkWash = false; _marks.Clear(); _hatch.Clear(); Invalidate(); }

    /// <summary>
    /// Brief DE: cover a practice question's answer where Grasshopper shows it. A drawing and nothing more: a person can still open the panel or hover the port, and the
    /// bar says so. Set after a step is lit, replaced by the next step, and gone with the Walk.
    /// </summary>
    public static void SetHatch(IEnumerable<Guid> components)
    {
      _hatch.Clear();
      if (components != null) _hatch.AddRange(components);
      Invalidate();
    }

    /// <summary>The number badge this component wears, if it wears one, so a sentence can name it as the canvas does.</summary>
    public static int? BadgeOf(Guid guid) => CanvasNumbers.Number(guid);

    /// <summary>Clear only if <paramref name="owner"/> still owns the current glow, so a panel closing
    /// after a newer action opened cannot wipe the newer highlight (architect review #2).</summary>
    public static void ClearIfOwner(object owner) { TreeLens.ClearIfOwner(owner); if (owner == null || ReferenceEquals(owner, _owner)) Clear(); }

    private static void Hook()
    {
      var c = Instances.ActiveCanvas;
      if (c == null) return;
      if (_hooked && _canvas == c) return;
      if (_canvas != null) { try { _canvas.CanvasPostPaintObjects -= OnPaint; } catch { } }
      _canvas = c;
      _canvas.CanvasPostPaintObjects += OnPaint;
      _hooked = true;
    }

    /// <summary>Clear the annotation marks when the document re-solves (architect note): the explanation
    /// was computed against the data that was on the wires, so a new solution can make the marks stale.
    /// The glow (which only frames live components) is left; only the marks are tied to solution state.</summary>
    private static void HookSolution()
    {
      var doc = _canvas?.Document;
      if (doc == null || ReferenceEquals(doc, _hookedDoc)) return;   // WP-4.7: hook THIS document, not just the first ever
      _hookedDoc = doc;
      try { doc.SolutionEnd += (s, e) => { if (_marks.Count > 0) ClearMarks(); }; } catch { _hookedDoc = null; }
    }

    private static void Pulse(bool on)
    {
      if (on)
      {
        if (_pulse == null)
        {
          _pulse = new UITimer { Interval = 0.05 };
          _pulse.Elapsed += (s, e) => { _phase += 0.14f; Invalidate(); };
        }
        _pulse.Start();
      }
      else { try { _pulse?.Stop(); } catch { } }
    }

    private static void Invalidate() { try { _canvas?.Invalidate(); } catch { } }

    private static void OnPaint(GH_Canvas sender)
    {
      if (!_walkWash && _guids.Count == 0 && _secondary.Count == 0 && _marks.Count == 0 && _hatch.Count == 0) return;
      var g = sender?.Graphics;
      var doc = sender?.Document;
      if (g == null || doc == null) return;

      var primaryColor = _thinking ? Lavender : _primaryColor;
      var s = 0.5f + 0.5f * (float)Math.Sin(_phase);
      var breathe = _thinking ? (0.4f + 0.6f * s) : 1f;   // swing 0.4..1.0 so the pulse is clearly visible
      var prior = g.SmoothingMode;
      g.SmoothingMode = SmoothingMode.AntiAlias;
      try
      {
        // Fixed draw order: a Walk's wash under everything of ours, the secondary glow (normal Amber/orange - the usual selection colour, NOT
        // dimmed), then the primary glow (Amber, or dark red for a Diagnose root), then marks, then badges.
        if (_walkWash) DrawWash(g, doc, sender);
        foreach (var id in _secondary) DrawGlow(g, doc, id, Amber, 1f);
        foreach (var id in _guids) DrawGlow(g, doc, id, primaryColor, breathe);

        if (_marks.Count > 0) DrawMarks(g, doc);
        WalkSession.PaintTimes(g, doc);   // Brief DD: under the chips, which lift above the row
        TreeLens.Paint(g, sender);
        if (_hatch.Count > 0) DrawHatch(g, doc);
        if (CanvasNumbers.Numbered) DrawBadges(g, doc);
      }
      catch { }
      finally { g.SmoothingMode = prior; }
    }

    /// <summary>Brief DE: a translucent hatch over a covered value: the whole face of a slider or other param, and the output side of a component. Roles from <see cref="Dwight.Branding.UiTokens"/>.</summary>
    private static void DrawHatch(System.Drawing.Graphics g, GH_Document doc)
    {
      var dark = Dwight.UI.DwightTheme.Current() == "dark";
      foreach (var id in _hatch)
      {
        var obj = doc.FindObject(id, true);
        var b = obj?.Attributes?.Bounds;
        if (b == null) continue;
        var r = b.Value;
        if (!(obj is IGH_Param)) r = new RectangleF(r.X + r.Width * 0.6f, r.Y, r.Width * 0.4f, r.Height);   // a component's value is on its output side
        using (var back = new SolidBrush(Color.FromArgb(215, Dwight.Branding.UiTokens.Surface(dark))))
          g.FillRectangle(back, r);
        using (var hatch = new HatchBrush(HatchStyle.WideUpwardDiagonal, Color.FromArgb(200, Dwight.Branding.UiTokens.Subtle(dark)), Color.Transparent))
          g.FillRectangle(hatch, r);
        using (var pen = new Pen(Dwight.Branding.UiTokens.BorderStrong(dark), 1f))
          g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);
      }
    }

    /// <summary>Brief DA: one filled region over what the canvas shows, in the canvas's own colour, with a hole for each of the step's components. The holes are <see cref="WalkGeometry.Holes"/>'s.</summary>
    private static void DrawWash(System.Drawing.Graphics g, GH_Document doc, GH_Canvas canvas)
    {
      using (var region = new Region(canvas.Viewport.VisibleRegion))
      using (var wash = new SolidBrush(CanvasLook.Wash(CanvasColour)))
      {
        foreach (var hole in WalkGeometry.Holes(_guids.Select(id => doc.FindObject(id, true)?.Attributes?.Bounds)))
          using (var round = Rounded(hole, CanvasLook.HoleRadius))
            region.Exclude(round);
        foreach (var (a, b) in TreeLens.StepWires()) ExcludeWire(region, a, b);
        g.FillRegion(wash, region);
      }
    }

    /// <summary>Brief DJ: the wash leaves a band along the step's wire, so the lit wire runs on the canvas's true colour all the way.</summary>
    private static void ExcludeWire(Region region, PointF a, PointF b)
    {
      try
      {
        var (c1, c2) = CanvasLook.WireControls(a, b);
        using (var band = new GraphicsPath())
        using (var pen = new Pen(Color.Black, CanvasLook.WireBand))
        {
          band.AddBezier(a, c1, c2, b);
          band.Widen(pen);
          region.Exclude(band);
        }
      }
      catch { }
    }

    private static void DrawGlow(System.Drawing.Graphics g, GH_Document doc, Guid id, Color color, float intensity)
    {
      var bounds = doc.FindObject(id, true)?.Attributes?.Bounds;
      if (bounds == null) return;
      for (int ring = 3; ring >= 1; ring--)            // concentric translucent rings = a soft halo
      {
        var grow = ring * 5f;
        var alpha = (int)(CanvasLook.HaloAlpha(ring) * intensity);
        if (alpha <= 0) continue;
        using (var path = Rounded(RectangleF.Inflate(bounds.Value, grow, grow), 6f + grow))
        using (var pen = new Pen(Color.FromArgb(alpha, color), grow))
          g.DrawPath(pen, path);
      }

      // Brief DJ: a solid edge over the halo. The amber is the one that clears this canvas; lavender and the Diagnose root keep their own colour at the same geometry.
      var edge = color.ToArgb() == Amber.ToArgb() ? CanvasLook.Edge(CanvasColour) : color;
      using (var path = Rounded(RectangleF.Inflate(bounds.Value, CanvasLook.EdgeWidth / 2f, CanvasLook.EdgeWidth / 2f), 6f))
      using (var pen = new Pen(Color.FromArgb((int)(255 * intensity), edge), CanvasLook.EdgeWidth))
        g.DrawPath(pen, path);
    }

    /// <summary>Draw each glowing component's short-int id as a small badge at its top-left corner
    /// (ADR-079), so "the Curve component (42)" in the prose is findable on the canvas. Drawn in document
    /// coordinates, so it tracks pan/zoom like the glow.</summary>
    private static void DrawBadges(System.Drawing.Graphics g, GH_Document doc)
    {
      using (var font = new Font("Segoe UI", 6.5f, FontStyle.Bold))
        foreach (var (text, rect) in Badges(g, doc, font))
        {
          using (var path = Rounded(rect, 4f))
          using (var fill = new SolidBrush(BadgeInk))
            g.FillPath(fill, path);
          using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
          using (var tb = new SolidBrush(Color.White))
            g.DrawString(text, font, tb, rect, sf);
        }
    }

    /// <summary>Each badge's number and where it is drawn, measured. One function, so what the Lens keeps its chips off is what is drawn. Brief ES: the function is <see cref="CanvasNumbers.Badges"/>, tested headless.</summary>
    private static List<(string Text, RectangleF Rect)> Badges(System.Drawing.Graphics g, GH_Document doc, Font font) =>
      CanvasNumbers.Badges(_guids.Concat(_secondary), id => doc.FindObject(id, true)?.Attributes?.Bounds, t => g.MeasureString(t, font));

    /// <summary>Where the badges are drawn, for what must keep off them.</summary>
    internal static List<RectangleF> BadgeRects(System.Drawing.Graphics g, GH_Document doc)
    {
      using (var font = new Font("Segoe UI", 6.5f, FontStyle.Bold))
        return Badges(g, doc, font).Select(b => b.Rect).ToList();
    }

    /// <summary>Resolve each mark's ids to live bounds and draw it (ADR-065: the plugin computes all
    /// mark geometry; the model only named the ids). Recomputed every paint from live bounds, so the
    /// marks track pan/zoom and component moves. A missing component is silently skipped, never crashes.</summary>
    private static void DrawMarks(System.Drawing.Graphics g, GH_Document doc)
    {
      // Highlights (component outlines) first, then the flow connections on top. Circles were dropped -
      // the component border is enough (Tharun). The data-label rides only on long-enough connections.
      foreach (var m in _marks)
        if (m.T == "highlight")
          foreach (var b in BoundsOf(doc, m.Ids))
            using (var path = Rounded(RectangleF.Inflate(b, 4f, 4f), 7f))
            {
              using (var glow = new Pen(MarkGlow, CanvasLook.MarkHaloWidth) { LineJoin = LineJoin.Round })
                g.DrawPath(glow, path);                 // neon highlight halo
              using (var core = new Pen(MarkInk, CanvasLook.MarkWidth) { LineJoin = LineJoin.Round })
                g.DrawPath(core, path);
            }

      foreach (var m in _marks)
        if (m.T == "arrow")
          DrawConnection(g, doc, m.From, m.To, m.Label);
    }

    private static IEnumerable<RectangleF> BoundsOf(GH_Document doc, IEnumerable<Guid> ids)
    {
      if (ids == null) yield break;
      foreach (var id in ids)
      {
        var b = doc.FindObject(id, true)?.Attributes?.Bounds;
        if (b != null) yield return b.Value;
      }
    }

    /// <summary>Draw the flow from one component to another (E4c). When a real wire exists between them,
    /// over-draw the actual Grasshopper wire(s) as a thick green bezier from the source output param to
    /// the target input param (so it lands on the right input, even when boxes are close); otherwise fall
    /// back to a centre-to-centre arrow. The data-label (tree signature) rides only on the longest wire
    /// and only when it is long enough to read (E4d/#2).</summary>
    private static void DrawConnection(System.Drawing.Graphics g, GH_Document doc, Guid fromGuid, Guid toGuid, string label)
    {
      var fromObj = doc.FindObject(fromGuid, true);
      var toObj = doc.FindObject(toGuid, true);
      if (fromObj == null || toObj == null) return;

      var wires = WireSegments(fromObj, toObj);
      if (wires.Count == 0)   // no live wire between them -> conceptual arrow fallback
      {
        var fb = fromObj.Attributes?.Bounds;
        var tb = toObj.Attributes?.Bounds;
        if (fb != null && tb != null) DrawArrow(g, fb.Value, tb.Value, label);
        return;
      }

      PointF labA = default, labB = default;
      var best = -1f;
      foreach (var w in wires)
      {
        DrawWireBezier(g, w.Item1, w.Item2);
        var len = Dist(w.Item1, w.Item2);
        if (len > best) { best = len; labA = w.Item1; labB = w.Item2; }
      }
      // Brief DJ: one chip per wire. Where the Lens is live its chip is this wire's, and a label under it read "4 bra[4 lists, 1 to 3 each]anch".
      if (!string.IsNullOrWhiteSpace(label) && best >= LabelMinLength && !TreeLens.IsLive)
        DrawChipLabel(g, Mid(labA, labB), label);
    }

    /// <summary>The actual wire segments between two objects: from each connecting source output param's
    /// right edge to the target input param's left edge (so the highlight targets the real input).</summary>
    private static List<(PointF, PointF)> WireSegments(IGH_DocumentObject fromObj, IGH_DocumentObject toObj)
    {
      var segs = new List<(PointF, PointF)>();
      foreach (var ip in InputParamsOf(toObj))
      {
        if (ip?.Sources == null) continue;
        foreach (var src in ip.Sources)
        {
          var sa = src?.Attributes;
          var ta = ip.Attributes;
          var owner = sa?.GetTopLevel?.DocObject;
          if (sa == null || ta == null || owner == null || owner.InstanceGuid != fromObj.InstanceGuid) continue;
          // Use Grasshopper's real wire grips (the exact per-param attachment points, distinct for each
          // input row) so the highlight tracks the actual wire; fall back to the bounds edge if absent.
          var start = sa.HasOutputGrip ? sa.OutputGrip : new PointF(sa.Bounds.Right, sa.Bounds.Top + sa.Bounds.Height / 2f);
          var end = ta.HasInputGrip ? ta.InputGrip : new PointF(ta.Bounds.Left, ta.Bounds.Top + ta.Bounds.Height / 2f);
          segs.Add((start, end));
        }
      }
      return segs;
    }

    private static IEnumerable<IGH_Param> InputParamsOf(IGH_DocumentObject obj)
    {
      if (obj is IGH_Component c) return c.Params.Input;
      if (obj is IGH_Param p) return new[] { p };
      return System.Array.Empty<IGH_Param>();
    }

    /// <summary>A neon dashed bezier with horizontal tangents, over a soft glow, mimicking a Grasshopper
    /// wire, with an arrowhead at the destination (data-flow direction). Handle is horizontal-only (the
    /// vertical-aware variant made horizontal wires worse - reverted).</summary>
    private static void DrawWireBezier(System.Drawing.Graphics g, PointF a, PointF b)
    {
      var (c1, c2) = CanvasLook.WireControls(a, b);
      using (var glow = new Pen(MarkGlow, CanvasLook.MarkHaloWidth) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        g.DrawBezier(glow, a, c1, c2, b);
      using (var core = new Pen(MarkInk, CanvasLook.MarkWidth) { StartCap = LineCap.Round, EndCap = LineCap.Round, DashStyle = DashStyle.Dash })
        g.DrawBezier(core, a, c1, c2, b);
      DrawArrowHead(g, c2, b);   // arrowhead at the destination, along the end tangent
    }

    /// <summary>The centre-to-centre fallback (no live wire): a neon dashed line over a glow, with a head.</summary>
    private static void DrawArrow(System.Drawing.Graphics g, RectangleF a, RectangleF b, string label)
    {
      var ca = new PointF(a.X + a.Width / 2f, a.Y + a.Height / 2f);
      var cb = new PointF(b.X + b.Width / 2f, b.Y + b.Height / 2f);
      var start = BorderPoint(a, cb);
      var end = BorderPoint(b, ca);

      using (var glow = new Pen(MarkGlow, CanvasLook.MarkHaloWidth) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        g.DrawLine(glow, start, end);
      using (var core = new Pen(MarkInk, CanvasLook.MarkWidth) { DashStyle = DashStyle.Dash })
        g.DrawLine(core, start, end);
      DrawArrowHead(g, start, end);

      if (!string.IsNullOrWhiteSpace(label) && Dist(start, end) >= LabelMinLength && !TreeLens.IsLive)
        DrawChipLabel(g, Mid(start, end), label);
    }

    /// <summary>A filled neon arrowhead at <paramref name="to"/>, oriented along the direction from
    /// <paramref name="from"/> - the data-flow cue at the destination end of a wire.</summary>
    private static void DrawArrowHead(System.Drawing.Graphics g, PointF from, PointF to)
    {
      var ang = (float)Math.Atan2(to.Y - from.Y, to.X - from.X);
      const float head = 10f;
      var p1 = new PointF(to.X - head * (float)Math.Cos(ang - 0.5), to.Y - head * (float)Math.Sin(ang - 0.5));
      var p2 = new PointF(to.X - head * (float)Math.Cos(ang + 0.5), to.Y - head * (float)Math.Sin(ang + 0.5));
      using (var br = new SolidBrush(MarkInk))
        g.FillPolygon(br, new[] { to, p1, p2 });
    }

    /// <summary>A small label chip (the wire's data) on a legible white background.</summary>
    private static void DrawChipLabel(System.Drawing.Graphics g, PointF center, string text)
    {
      using (var font = new Font("Segoe UI", 6.5f, FontStyle.Regular))
      {
        var size = g.MeasureString(text, font);
        var rect = new RectangleF(center.X - size.Width / 2f - 3f, center.Y - size.Height / 2f - 1f, size.Width + 6f, size.Height + 2f);
        using (var path = Rounded(rect, 4f))
        using (var fill = new SolidBrush(Color.FromArgb(235, 255, 255, 255)))
          g.FillPath(fill, path);
        using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
        using (var tb = new SolidBrush(MarkInk))
          g.DrawString(text, font, tb, rect, sf);
      }
    }

    private const float LabelMinLength = 90f;   // doc units: skip the data-label on short, cramped wires (#2)

    private static float Dist(PointF a, PointF b) { var dx = b.X - a.X; var dy = b.Y - a.Y; return (float)Math.Sqrt(dx * dx + dy * dy); }
    private static PointF Mid(PointF a, PointF b) => new PointF((a.X + b.X) / 2f, (a.Y + b.Y) / 2f);

    /// <summary>Where the segment from the rectangle's centre toward <paramref name="toward"/> crosses
    /// the rectangle border - so the fallback arrow touches the box edge, not its centre.</summary>
    private static PointF BorderPoint(RectangleF r, PointF toward)
    {
      var c = new PointF(r.X + r.Width / 2f, r.Y + r.Height / 2f);
      var dx = toward.X - c.X;
      var dy = toward.Y - c.Y;
      if (Math.Abs(dx) < 0.001f && Math.Abs(dy) < 0.001f) return c;
      var hw = r.Width / 2f;
      var hh = r.Height / 2f;
      var scale = 1f / Math.Max(Math.Abs(dx) / hw, Math.Abs(dy) / hh);
      return new PointF(c.X + dx * scale, c.Y + dy * scale);
    }

    internal static GraphicsPath Rounded(RectangleF r, float radius)
    {
      var d = Math.Min(radius, Math.Min(r.Width, r.Height) / 2f) * 2f;
      if (d < 1f) d = 1f;
      var p = new GraphicsPath();
      p.AddArc(r.Left, r.Top, d, d, 180, 90);
      p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
      p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
      p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
      p.CloseFigure();
      return p;
    }
  }

  /// <summary>One typed annotation mark from the closed on-canvas vocabulary (ADR-065): the model emits
  /// it referencing component ids; the plugin resolves the ids to live bounds and computes the geometry.
  /// Phase 4 supports <c>highlight</c>, <c>circle</c>, <c>arrow</c>, and <c>dim</c> at component scope;
  /// region <c>callout</c>s arrive with the segmenter (Phase 6).</summary>
  internal sealed class CanvasMark
  {
    public string T;                                 // "highlight" | "circle" | "arrow"
    public List<Guid> Ids = new List<Guid>();        // targets for highlight / circle
    public Guid From;                                // arrow source
    public Guid To;                                  // arrow target
    public string Label;                             // arrow: the data on the wire (tree signature, ADR-079)
  }
}
