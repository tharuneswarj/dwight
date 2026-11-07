using System;
using System.Drawing;
using System.IO;
using System.Linq;
using Dwight.Branding;
using Dwight.UI;
using Xunit;

namespace Dwight.Session.Tests
{
  /// <summary>
  /// Brief DJ: what the canvas is drawn with. The numbers, the contrasts behind them and the theme switch are pure and pinned here, against the two canvas colours Rhino 2 captured. Whether it looks like the
  /// mock-ups is the eleventh gate's to judge, on captures beside them: nothing here has been seen on a canvas.
  /// </summary>
  public class CanvasLookTests
  {
    private static readonly Color LightCanvas = Color.FromArgb(212, 208, 200);
    private static readonly Color DarkCanvas = Color.FromArgb(38, 40, 44);

    public static TheoryData<int, int, int> BothCanvases() => new TheoryData<int, int, int> { { 212, 208, 200 }, { 38, 40, 44 } };

    // ---- the theme switch: a pure function of the canvas's colour ---------------------------------------------------------------------------------

    [Fact]
    public void The_theme_is_read_from_the_canvas_colour()
    {
      Assert.False(CanvasLook.IsDark(LightCanvas));
      Assert.True(CanvasLook.IsDark(DarkCanvas));
      Assert.False(CanvasLook.IsDark(Color.White));
      Assert.True(CanvasLook.IsDark(Color.Black));
    }

    [Fact]
    public void The_switch_sits_at_its_stated_luminance()
    {
      var justUnder = Color.FromArgb(117, 117, 117);
      var justOver = Color.FromArgb(119, 119, 119);
      Assert.True(CanvasLook.Luminance(justUnder) < CanvasLook.DarkBelow);
      Assert.True(CanvasLook.IsDark(justUnder));
      Assert.True(CanvasLook.Luminance(justOver) >= CanvasLook.DarkBelow);
      Assert.False(CanvasLook.IsDark(justOver));
    }

    [Fact]
    public void The_measure_is_the_standard_one()
    {
      Assert.Equal(21.0, CanvasLook.Contrast(Color.White, Color.Black), 3);
      Assert.Equal(1.0, CanvasLook.Contrast(LightCanvas, LightCanvas), 6);
      Assert.Equal(CanvasLook.Contrast(LightCanvas, DarkCanvas), CanvasLook.Contrast(DarkCanvas, LightCanvas), 9);
      Assert.Equal(Color.FromArgb(128, 128, 128).ToArgb(), CanvasLook.Over(Color.FromArgb(128, 255, 255, 255), Color.Black).ToArgb());
    }

    // ---- the wash -----------------------------------------------------------------------------------------------------------------------------------

    /// <summary>The first fault the captures showed: painted in the canvas's own colour, the wash changed almost nothing on the canvas it was meant to push back. The second: at 120 and 90 it was heavy and uniform, so the band is the island reading clearly with the rest still the definition. At 100 the light canvas measures 2.21, and at 120 it left the band at 2.67.</summary>
    [Theory]
    [MemberData(nameof(BothCanvases))]
    public void The_wash_moves_the_canvas_away_from_itself_and_the_step_stands_apart(int r, int g, int b)
    {
      var canvas = Color.FromArgb(r, g, b);
      var washed = CanvasLook.Over(CanvasLook.Wash(canvas), canvas);
      Assert.InRange(CanvasLook.Contrast(canvas, washed), 1.5, 2.3);
    }

    /// <summary>The light canvas went from 70 to 100 on Tharun's word on the twelfth build, that people use the light canvas and the mock-up's dimmed regions are darker; 100 is the architect's reading of "a little darker". The dark canvas needs nothing, so its 60 stands.</summary>
    [Fact]
    public void The_wash_is_dark_on_a_light_canvas_and_light_on_a_dark_one()
    {
      Assert.True(CanvasLook.Luminance(Opaque(CanvasLook.Wash(LightCanvas))) < CanvasLook.Luminance(LightCanvas));
      Assert.True(CanvasLook.Luminance(Opaque(CanvasLook.Wash(DarkCanvas))) > CanvasLook.Luminance(DarkCanvas));
      Assert.Equal(100, CanvasLook.Wash(LightCanvas).A);
      Assert.Equal(60, CanvasLook.Wash(DarkCanvas).A);
    }

    /// <summary>A component under it stays legible and clearly secondary: black text on Grasshopper's grey, under the wash, on both canvases. The floor is 4.5, WCAG AA for ordinary text, and no longer the 5.0 it was at 70: at 100 on the light canvas this measures 4.83, and at 110 it is 4.38, so this is what stops the light wash going much further.</summary>
    [Theory]
    [MemberData(nameof(BothCanvases))]
    public void What_is_under_the_wash_stays_legible(int r, int g, int b)
    {
      var wash = CanvasLook.Wash(Color.FromArgb(r, g, b));
      var fill = CanvasLook.Over(wash, Color.FromArgb(199, 199, 199));
      var text = CanvasLook.Over(wash, Color.FromArgb(20, 20, 20));
      Assert.InRange(CanvasLook.Contrast(fill, text), 4.5, 7.0);
    }

    private static Color Opaque(Color c) => Color.FromArgb(c.R, c.G, c.B);

    // ---- the glow -----------------------------------------------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(BothCanvases))]
    public void The_edge_clears_three_to_one_on_its_canvas(int r, int g, int b)
    {
      var canvas = Color.FromArgb(r, g, b);
      Assert.True(CanvasLook.Contrast(CanvasLook.Edge(canvas), canvas) >= 3.0);
    }

    /// <summary>Why it takes two values: either one alone fails on the other canvas.</summary>
    [Fact]
    public void Neither_amber_clears_both_canvases_so_the_edge_chooses()
    {
      Assert.True(CanvasLook.Contrast(UiTokens.Brand, LightCanvas) < 3.0);
      Assert.True(CanvasLook.Contrast(UiTokens.BrandInk, DarkCanvas) < 3.0);
      Assert.Equal(UiTokens.BrandInk, CanvasLook.Edge(LightCanvas));
      Assert.Equal(UiTokens.Brand, CanvasLook.Edge(DarkCanvas));
    }

    [Fact]
    public void The_halo_is_brighter_and_strongest_at_the_component()
    {
      Assert.Equal(160, CanvasLook.HaloAlpha(1));
      Assert.Equal(120, CanvasLook.HaloAlpha(2));
      Assert.Equal(80, CanvasLook.HaloAlpha(3));
      Assert.Equal(2f, CanvasLook.EdgeWidth);
    }

    /// <summary>The ruling's test: the outermost ring is still visible on the dark canvas. It was at alpha 54.</summary>
    [Theory]
    [MemberData(nameof(BothCanvases))]
    public void The_outermost_ring_is_still_seen(int r, int g, int b)
    {
      var canvas = Color.FromArgb(r, g, b);
      var ring = CanvasLook.Over(Color.FromArgb(CanvasLook.HaloAlpha(3), UiTokens.Brand), canvas);
      Assert.True(CanvasLook.Contrast(ring, canvas) >= 1.15, CanvasLook.Contrast(ring, canvas).ToString());
    }

    // ---- the wires ----------------------------------------------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(BothCanvases))]
    public void The_wire_clears_three_to_one_on_both_canvases(int r, int g, int b) =>
      Assert.True(CanvasLook.Contrast(UiTokens.TealWire, Color.FromArgb(r, g, b)) >= 3.0);

    /// <summary>Why the wash leaves a band along a lit wire: a teal that clears both canvases nearly vanishes over the wash on either. If this ever stops being true, the band is no longer needed.</summary>
    [Theory]
    [MemberData(nameof(BothCanvases))]
    public void Over_the_wash_the_wire_would_be_lost_so_the_wash_leaves_it_a_band(int r, int g, int b)
    {
      var canvas = Color.FromArgb(r, g, b);
      Assert.True(CanvasLook.Contrast(UiTokens.TealWire, CanvasLook.Over(CanvasLook.Wash(canvas), canvas)) < 2.0);
      Assert.True(CanvasLook.WireBand >= CanvasLook.LensWireWidth + 8f);
    }

    [Fact]
    public void The_wire_numbers_are_the_rulings()
    {
      Assert.Equal(3f, CanvasLook.MarkWidth);
      Assert.Equal(8f, CanvasLook.MarkHaloWidth);
      Assert.Equal(70, CanvasLook.MarkHaloAlpha);
      Assert.Equal(2.5f, CanvasLook.LensWireWidth);
    }

    [Fact]
    public void The_wires_curve_bends_by_half_its_run_and_never_less_than_18()
    {
      var (c1, c2) = CanvasLook.WireControls(new PointF(100, 50), new PointF(300, 90));
      Assert.Equal(new PointF(200, 50), c1);
      Assert.Equal(new PointF(200, 90), c2);

      var (near1, near2) = CanvasLook.WireControls(new PointF(100, 50), new PointF(110, 50));
      Assert.Equal(new PointF(118, 50), near1);
      Assert.Equal(new PointF(92, 50), near2);

      var (back1, back2) = CanvasLook.WireControls(new PointF(300, 50), new PointF(100, 90));
      Assert.Equal(new PointF(400, 50), back1);
      Assert.Equal(new PointF(0, 90), back2);
    }

    // ---- the chips ----------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_chip_is_a_pill_with_readable_words()
    {
      Assert.Equal(7.5f, CanvasLook.PillRadius(15f));
      Assert.True(CanvasLook.ChipFontPx >= 11f);
      Assert.True(CanvasLook.Contrast(UiTokens.TealWire, ColorTranslator.FromHtml(UiTokens.SurfaceRaisedHex)) >= 4.5);
    }

    // ---- where they are used, by the text of the code --------------------------------------------------------------------------------------------------

    private static string Repo(params string[] parts)
    {
      var dir = new DirectoryInfo(AppContext.BaseDirectory);
      while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Dwight.sln"))) dir = dir.Parent;
      Assert.NotNull(dir);
      return File.ReadAllText(Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray())).Replace("\r\n", "\n");
    }

    private static string Glow => Repo("Dwight", "UI", "CanvasGlow.cs");
    private static string Lens => Repo("Dwight", "UI", "TreeLens.cs");

    private static string Between(string text, string from, string to)
    {
      var a = text.IndexOf(from, StringComparison.Ordinal);
      Assert.True(a >= 0, from);
      var b = text.IndexOf(to, a + from.Length, StringComparison.Ordinal);
      Assert.True(b > a, to);
      return text.Substring(a, b - a);
    }

    [Fact]
    public void The_colour_is_read_from_the_canvas_at_paint_time_and_never_stored()
    {
      Assert.Contains("internal static Color CanvasColour => WalkGeometry.CanvasColour(GH_Skin.canvas_mono, GH_Skin.canvas_mono_color, GH_Skin.canvas_back);", Glow);
      Assert.Contains("new SolidBrush(CanvasLook.Wash(CanvasColour))", Glow);
      Assert.Contains("CanvasLook.Edge(CanvasColour)", Glow);
    }

    [Fact]
    public void The_wash_has_round_holes_and_leaves_the_steps_wires_clear()
    {
      var wash = Between(Glow, "private static void DrawWash(", "private static void ExcludeWire(");
      Assert.Contains("Rounded(hole, CanvasLook.HoleRadius)", wash);
      Assert.Contains("foreach (var (a, b) in TreeLens.StepWires()) ExcludeWire(region, a, b);", wash);
      Assert.InRange(wash.IndexOf("ExcludeWire(", StringComparison.Ordinal), 0, wash.IndexOf("g.FillRegion(wash, region);", StringComparison.Ordinal));

      var band = Between(Glow, "private static void ExcludeWire(", "private static void DrawGlow(");
      Assert.Contains("new Pen(Color.Black, CanvasLook.WireBand)", band);
      Assert.Contains("band.Widen(pen);", band);
      Assert.Contains("CanvasLook.WireControls(a, b)", band);
      Assert.Contains("band.AddBezier(a, c1, c2, b);", band);
    }

    /// <summary>The thinking lavender keeps its pulse, because the edge takes the same intensity, and the Diagnose root keeps its red at the same geometry.</summary>
    [Fact]
    public void The_edge_goes_over_the_halo_and_only_the_amber_chooses()
    {
      var glow = Between(Glow, "private static void DrawGlow(", "private static void DrawBadges(");
      Assert.Contains("(int)(CanvasLook.HaloAlpha(ring) * intensity)", glow);
      Assert.Contains("var edge = color.ToArgb() == Amber.ToArgb() ? CanvasLook.Edge(CanvasColour) : color;", glow);
      Assert.Contains("Color.FromArgb((int)(255 * intensity), edge), CanvasLook.EdgeWidth)", glow);
      Assert.InRange(glow.IndexOf("for (int ring = 3;", StringComparison.Ordinal), 0, glow.IndexOf("var edge =", StringComparison.Ordinal));
    }

    [Fact]
    public void The_marks_take_the_new_pens()
    {
      Assert.Contains("private static readonly Color MarkInk = Dwight.Branding.UiTokens.TealWire;", Glow);
      Assert.Contains("Color.FromArgb(CanvasLook.MarkHaloAlpha, MarkInk)", Glow);
      Assert.DoesNotContain("new Pen(MarkInk, 2.2f)", Glow);
      Assert.DoesNotContain("new Pen(MarkInk, 2.6f)", Glow);
      Assert.DoesNotContain("new Pen(MarkGlow, 5f)", Glow);
      Assert.DoesNotContain("new Pen(MarkGlow, 6f)", Glow);
    }

    /// <summary>The tenth gate's double chip: the marks' own label lay under the Lens's, so it read "4 bra[4 lists, 1 to 3 each]anch". One chip per wire.</summary>
    [Fact]
    public void Where_the_Lens_is_live_the_marks_draw_no_chip_of_their_own()
    {
      var connection = Between(Glow, "private static void DrawConnection(", "private static List<(PointF, PointF)> WireSegments(");
      Assert.Contains("best >= LabelMinLength && !TreeLens.IsLive", connection);
      var arrow = Between(Glow, "private static void DrawArrow(", "private static void DrawArrowHead(");
      Assert.Contains("Dist(start, end) >= LabelMinLength && !TreeLens.IsLive", arrow);
    }

    [Fact]
    public void The_lens_lights_the_steps_wires_and_draws_its_chips_as_pills()
    {
      var paint = Between(Lens, "internal static void Paint(", "private static bool InView(");
      Assert.Contains("new Pen(Ink, CanvasLook.LensWireWidth)", paint);
      Assert.Contains("foreach (var (a, b) in StepWires())", paint);
      Assert.Contains("new Font(\"Consolas\", CanvasLook.ChipFontPx, FontStyle.Regular, GraphicsUnit.Pixel)", paint);
      Assert.InRange(paint.IndexOf("g.DrawBezier(wire,", StringComparison.Ordinal), 0, paint.IndexOf("DrawChip(g,", StringComparison.Ordinal));

      var chip = Between(Lens, "private static void DrawChip(", "private static LensRect Box(");
      var seat = Between(Lens, "private static bool SeatChip(", "private static void DrawChip(");
      Assert.Contains("CanvasGlow.Rounded(rect, CanvasLook.PillRadius(rect.Height))", chip);
      Assert.Contains("g.FillPath(fill, pill);", chip);
      Assert.Contains("g.DrawPath(ink, pill);", chip);
      Assert.Contains("seat = ChipPlace.Seat(new ChipWire", seat);
      Assert.Contains("}, w, h, placed, keepOff);", seat);
      Assert.DoesNotContain("FillRectangle", chip);

      Assert.Contains("private static readonly Color PillFill = ColorTranslator.FromHtml(UiTokens.SurfaceRaisedHex);", Lens);
    }

    [Fact]
    public void Only_the_steps_own_wires_are_lit()
    {
      var wires = Between(Lens, "internal static List<(PointF A, PointF B)> StepWires()", "T, or the Every wire button");
      Assert.Contains("if (!c.InStep) continue;", wires);
      Assert.Contains("if (!_live) return wires;", wires);
    }

    /// <summary>The ring round a port is an edge, so it takes the edge's ink: the brand amber is 1.87 on the light canvas.</summary>
    [Fact]
    public void The_port_ring_takes_the_edges_ink_by_the_canvas_colour() =>
      Assert.Contains("new Pen(CanvasLook.Edge(CanvasGlow.CanvasColour), 2f)", Lens);

    /// <summary>T and the panel's button are one toggle, so the panel's is there when the keyboard is elsewhere: T needs the canvas to have focus, and a click on a step takes it away.</summary>
    [Fact]
    public void The_panels_button_and_T_are_one_toggle()
    {
      var key = Between(Lens, "private static void OnFormKey(", "The canvas holds the keys unless");
      Assert.Contains("if (action == LensKeyAction.Toggle) Toggle();", key);

      var panel = Repo("Dwight", "UI", "WebPanel.cs");
      Assert.Contains("if (cmd == \"lens:all\") { TreeLens.Toggle(); return; }", panel);
      Assert.InRange(panel.IndexOf("cmd == \"lens:all\"", StringComparison.Ordinal), 0, panel.IndexOf("cmd.StartsWith(\"lens:\"", StringComparison.Ordinal));
      Assert.Contains("TreeLens.IsLive, TreeLens.ShowsAll)", panel);

      Assert.Contains("WalkSession.LensChanged();", Between(Lens, "public static void Toggle()", "Is every wire of the selection asked for"));
      var session = Repo("Dwight", "UI", "WalkSession.cs");
      Assert.Contains("public static void LensChanged() { if (IsLive) Redraw(); }", session);
      Assert.Contains("TreeLens.IsLive ? TreeLens.ShowsAll : (bool?)null", session);
      Assert.Contains("case \"everywire\": TreeLens.Toggle(); break;", Repo("Dwight", "UI", "WalkBar.cs"));
    }

    /// <summary>The ruling: an icon button in the library's class, its word the tooltip, on the panel's footer and on the bar. The panel's sits outside the Walk's own controls, because a lens outlives its Walk and T must never be the only door.</summary>
    [Fact]
    public void Every_wire_is_an_icon_button_with_its_word_on_both_surfaces()
    {
      const string face = "class=\"iconbtn\" id=\"{0}\" title=\"Every wire\" aria-label=\"Every wire\" aria-pressed=\"false\"";
      var shell = Repo("Dwight", "Resources", "panel-shell.html");
      Assert.Contains(string.Format(face, "lens-every"), shell);
      Assert.Contains("onclick=\"dwightSend('lens:all')\"", shell);
      Assert.InRange(shell.IndexOf("id=\"walk-leave\"", StringComparison.Ordinal), 0, shell.IndexOf("id=\"lens-every\"", StringComparison.Ordinal));
      Assert.InRange(shell.IndexOf("</span>", shell.IndexOf("id=\"walk-leave\"", StringComparison.Ordinal), StringComparison.Ordinal), 0, shell.IndexOf("id=\"lens-every\"", StringComparison.Ordinal));
      Assert.DoesNotContain("d.toggle", shell);
      // Ask is the Walk's door to the chat, so Chat gives way to the sixth control at the panel's narrowest; the fit gate holds that state.
      Assert.Contains("class=\"btn walkgives\" id=\"askbtn\"", shell);
      Assert.Contains(".foot.walking .walkgives{display:none}", Repo("Dwight", "Resources", "dwight-components.css"));

      // Brief EC unit 3: on the bar the Wires button became the Lens switch, beside Focus and Dim, checked when the Lens shows every wire, its word the tooltip.
      var bar = Repo("Dwight", "Resources", "walk-bar.html");
      Assert.Contains("<button class=\"sw\" id=\"walklens\" role=\"switch\" aria-label=\"Every wire\" aria-checked=\"false\" title=\"Every wire: the Lens on all of them\" disabled onclick=\"dwightSend('everywire')\">", bar);
      Assert.Contains("w.setAttribute('aria-checked',d.everywire===true?'true':'false');", bar);
      Assert.Contains("<use href=\"#ic-wires\"/>", shell);
      Assert.Contains("wires: 'vector-spline'", Repo("tools", "build-icon-sprite.js"));
      Assert.Contains("id=\"ic-wires\"", Repo("Dwight", "Resources", "dwight-icons.svg"));
    }

    /// <summary>The time bars are wires' kin: the same ink, so they hold on both canvases.</summary>
    [Fact]
    public void The_time_bars_take_the_wire_ink() =>
      Assert.Contains("Color.FromArgb(TimeBars.InkAlpha, UiTokens.TealWire)", Repo("Dwight", "UI", "WalkSession.cs"));
  }
}
