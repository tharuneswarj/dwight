using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dwight.UI;
using Xunit;

namespace Dwight.Session.Tests
{
  /// <summary>
  /// THE WALK (Brief DA, Tharun's ruling of 1 November 2026). The state, the order, the keys, the geometry and the words are GH-free and tested as they run. The seams that need Grasshopper are
  /// pinned by text at the end, and are the tenth gate's to exercise.
  /// </summary>
  public class WalkCoreTests
  {
    private static readonly Guid A = G(1), B = G(2), C = G(3), D = G(4), E = G(5), F = G(6);
    private static Guid G(int n) => new Guid(n, 0, 0, new byte[8]);

    /// <summary>A Walk on step 1. Arriving before it is Brief DH's, in FocusCoreTests.</summary>
    private static WalkState Walk(int steps)
    {
      var walk = new WalkState(Enumerable.Range(0, steps).Select(i => (IReadOnlyList<Guid>)new[] { G(i + 1) }).ToList(), new[] { A, B, C });
      walk.GoTo(0);
      return walk;
    }

    // ---- the state: the bounds say nothing -----------------------------------------------------------------------------

    [Fact]
    public void A_walk_on_its_first_step_is_live_there()
    {
      var w = Walk(3);
      Assert.True(w.Live);
      Assert.Equal(0, w.Index);
      Assert.Equal(new[] { G(1) }, w.Current);
      Assert.Equal(new[] { A, B, C }, w.Source);
    }

    [Fact]
    public void Back_on_step_1_and_Next_on_step_N_do_nothing_and_say_nothing()
    {
      var w = Walk(2);
      Assert.False(w.Back());
      Assert.Equal(0, w.Index);
      Assert.True(w.Next());
      Assert.Equal(1, w.Index);
      Assert.False(w.Next());
      Assert.Equal(1, w.Index);
      Assert.True(w.Back());
      Assert.Equal(0, w.Index);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    public void GoTo_out_of_range_moves_nothing(int i)
    {
      var w = Walk(3);
      w.GoTo(1);
      Assert.False(w.GoTo(i));
      Assert.Equal(1, w.Index);
    }

    [Fact]
    public void GoTo_the_current_step_still_answers_because_the_click_means_take_me_there()
    {
      var w = Walk(3);
      w.GoTo(1);
      Assert.True(w.GoTo(1));
      Assert.Equal(1, w.Index);
    }

    [Fact]
    public void After_leaving_nothing_moves_and_leaving_again_says_nothing()
    {
      var w = Walk(3);
      Assert.True(w.Leave());
      Assert.False(w.Live);
      Assert.False(w.Leave());
      Assert.False(w.Next());
      Assert.False(w.Back());
      Assert.False(w.GoTo(1));
      Assert.Empty(w.Current);
    }

    [Fact]
    public void A_walk_with_no_steps_is_never_live()
    {
      Assert.False(new WalkState(new List<IReadOnlyList<Guid>>(), null).Live);
      Assert.False(new WalkState(null, null).Live);
      Assert.Empty(new WalkState(null, null).Source);
    }

    // ---- the order: the plugin's, from the wires -----------------------------------------------------------------------

    private static List<IReadOnlyCollection<Guid>> Steps(params Guid[][] steps) => steps.Select(s => (IReadOnlyCollection<Guid>)s).ToList();

    [Fact]
    public void A_chain_the_model_wrote_backwards_is_walked_from_the_inputs()
    {
      var order = WalkOrder.Sort(Steps(new[] { C }, new[] { B }, new[] { A }), new[] { (A, B), (B, C) });
      Assert.Equal(new[] { 2, 1, 0 }, order);
    }

    [Fact]
    public void A_chain_in_order_stays_as_the_model_wrote_it()
    {
      Assert.Equal(new[] { 0, 1, 2 }, WalkOrder.Sort(Steps(new[] { A }, new[] { B }, new[] { C }), new[] { (A, B), (B, C) }));
    }

    [Fact]
    public void A_fork_puts_the_source_first_and_keeps_the_models_order_for_the_branches()
    {
      // A feeds B and C, which do not feed each other: a tie, so the model's order stands, either way round.
      Assert.Equal(new[] { 2, 0, 1 }, WalkOrder.Sort(Steps(new[] { C }, new[] { B }, new[] { A }), new[] { (A, B), (A, C) }));
      Assert.Equal(new[] { 2, 0, 1 }, WalkOrder.Sort(Steps(new[] { B }, new[] { C }, new[] { A }), new[] { (A, B), (A, C) }));
    }

    [Fact]
    public void A_cycle_keeps_the_models_order()
    {
      Assert.Equal(new[] { 0, 1, 2 }, WalkOrder.Sort(Steps(new[] { B }, new[] { A }, new[] { C }), new[] { (A, B), (B, A), (B, C), (C, A) }));
      Assert.Equal(new[] { 0, 1 }, WalkOrder.Sort(Steps(new[] { B }, new[] { A }), new[] { (A, B), (B, A) }));
    }

    [Fact]
    public void A_cycle_among_the_steps_that_no_single_wire_shows_is_broken_by_the_models_order_and_terminates()
    {
      // a1 -> b1, b2 -> c1, c2 -> a2: each step precedes the next and nothing reaches back through a component, so the steps go round.
      var a1 = G(11); var a2 = G(12); var b1 = G(21); var b2 = G(22); var c1 = G(31); var c2 = G(32);
      var order = WalkOrder.Sort(Steps(new[] { a1, a2 }, new[] { b1, b2 }, new[] { c1, c2 }), new[] { (a1, b1), (b2, c1), (c2, a2) });
      Assert.Equal(new[] { 0, 1, 2 }, order.OrderBy(i => i).ToArray());
      Assert.Equal(0, order[0]);
    }

    [Fact]
    public void Feeding_through_a_component_in_no_step_still_counts()
    {
      Assert.Equal(new[] { 1, 0 }, WalkOrder.Sort(Steps(new[] { C }, new[] { A }), new[] { (A, B), (B, C) }));
    }

    [Fact]
    public void A_step_that_shares_a_component_does_not_feed_on_that_alone()
    {
      Assert.Equal(new[] { 0, 1 }, WalkOrder.Sort(Steps(new[] { A, B }, new[] { A, C }), Array.Empty<(Guid, Guid)>()));
    }

    [Fact]
    public void One_direction_of_a_two_way_pair_does_not_decide()
    {
      // B feeds A, and A's other component feeds B: both ways, so the model's order stands.
      Assert.Equal(new[] { 0, 1 }, WalkOrder.Sort(Steps(new[] { A, C }, new[] { B }), new[] { (B, A), (C, B) }));
    }

    [Fact]
    public void A_two_way_pair_puts_no_order_between_them_while_a_third_still_precedes_one()
    {
      // A feeds B and B feeds A, each through a different component, so neither must come first. C feeds only A, so A waits for C and B is free to go first.
      var a1 = G(11); var a2 = G(12); var b1 = G(21); var c1 = G(31);
      var order = WalkOrder.Sort(Steps(new[] { a1, a2 }, new[] { b1 }, new[] { c1 }), new[] { (a1, b1), (b1, a2), (c1, a2) });
      Assert.Equal(new[] { 1, 2, 0 }, order);
    }

    [Fact]
    public void A_step_whose_ids_were_all_dropped_stays_where_the_model_put_it()
    {
      var order = WalkOrder.Sort(Steps(new[] { C }, Array.Empty<Guid>(), new[] { A }), new[] { (A, C) });
      Assert.Equal(new[] { 2, 1, 0 }, order);
    }

    [Fact]
    public void No_steps_no_wires_and_no_edges_are_all_quiet()
    {
      Assert.Empty(WalkOrder.Sort(Steps(), null));
      Assert.Empty(WalkOrder.Sort(null, null));
      Assert.Equal(new[] { 0, 1 }, WalkOrder.Sort(Steps(new[] { A }, new[] { B }), null));
    }

    private static string View(params (string Text, Guid[] Ids)[] steps) =>
      new JsonObject
      {
        ["summary"] = "The whole selection.",
        ["steps"] = new JsonArray(steps.Select(s => (JsonNode)new JsonObject { ["text"] = s.Text, ["ids"] = new JsonArray(s.Ids.Select(i => (JsonNode)i.ToString()).ToArray()) }).ToArray()),
      }.ToJsonString();

    [Fact]
    public void The_view_is_reordered_with_its_sentences_its_marks_and_its_summary_intact()
    {
      var view = (JsonObject)JsonNode.Parse(View(("Then the loft", new[] { B }), ("First the curves", new[] { A })));
      view["steps"][0]["marks"] = new JsonArray(new JsonObject { ["t"] = "highlight" });

      var sorted = JsonNode.Parse(WalkOrder.SortView(view.ToJsonString(), new[] { (A, B) }));
      Assert.Equal("The whole selection.", sorted["summary"].GetValue<string>());
      Assert.Equal("First the curves", sorted["steps"][0]["text"].GetValue<string>());
      Assert.Equal("Then the loft", sorted["steps"][1]["text"].GetValue<string>());
      Assert.Equal("highlight", sorted["steps"][1]["marks"][0]["t"].GetValue<string>());
      Assert.Equal(A.ToString(), sorted["steps"][0]["ids"][0].GetValue<string>());
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"steps\":[]}")]
    [InlineData("{\"summary\":\"x\"}")]
    public void A_view_it_cannot_sort_comes_back_as_it_went(string view) =>
      Assert.Equal(view, WalkOrder.SortView(view, new[] { (A, B) }));

    [Fact]
    public void A_step_left_with_a_sentence_and_no_ids_stays_in_the_view_in_place()
    {
      var sorted = JsonNode.Parse(WalkOrder.SortView(View(("Second", new[] { C }), ("Nothing grounded", Array.Empty<Guid>()), ("First", new[] { A })), new[] { (A, C) }));
      Assert.Equal(new[] { "First", "Nothing grounded", "Second" }, sorted["steps"].AsArray().Select(s => s["text"].GetValue<string>()).ToArray());
    }

    // ---- the keys ----------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(WalkKeys.Right, "Next")]
    [InlineData(WalkKeys.Down, "Next")]
    [InlineData(WalkKeys.Left, "Back")]
    [InlineData(WalkKeys.Up, "Back")]
    [InlineData(WalkKeys.Escape, "Leave")]
    [InlineData(65, "None")]
    [InlineData(0, "None")]
    public void Each_key_in_a_live_walk(int key, string does) =>
      Assert.Equal(does, WalkKeys.Dispatch(key, false, true).ToString());

    [Theory]
    [InlineData(WalkKeys.Right)]
    [InlineData(WalkKeys.Left)]
    [InlineData(WalkKeys.Up)]
    [InlineData(WalkKeys.Down)]
    [InlineData(WalkKeys.Escape)]
    public void With_no_live_walk_every_key_is_returned_unhandled(int key) =>
      Assert.Equal(WalkKey.None, WalkKeys.Dispatch(key, false, false));

    [Theory]
    [InlineData(WalkKeys.Right)]
    [InlineData(WalkKeys.Left)]
    [InlineData(WalkKeys.Up)]
    [InlineData(WalkKeys.Down)]
    [InlineData(WalkKeys.Escape)]
    public void A_key_with_a_modifier_is_never_taken_because_Alt_and_an_arrow_is_Grasshoppers_nudge(int key) =>
      Assert.Equal(WalkKey.None, WalkKeys.Dispatch(key, true, true));

    [Theory]
    [InlineData("ArrowRight", WalkKeys.Right)]
    [InlineData("ArrowDown", WalkKeys.Down)]
    [InlineData("ArrowLeft", WalkKeys.Left)]
    [InlineData("ArrowUp", WalkKeys.Up)]
    [InlineData("Escape", WalkKeys.Escape)]
    [InlineData("Enter", 0)]
    [InlineData(null, 0)]
    public void A_pages_key_names_the_same_codes(string dom, int code) =>
      Assert.Equal(code, WalkKeys.CodeOf(dom));

    [Fact]
    public void The_codes_are_the_ones_WinForms_hands_the_hook()
    {
      // No WinForms in this project, so the numbers are stated: Keys.Left is 37, which is also the number GH_DocumentEditor.EditorKeyDown subtracts to find its arrows, and Keys.Escape is 27.
      Assert.Equal((27, 37, 38, 39, 40), (WalkKeys.Escape, WalkKeys.Left, WalkKeys.Up, WalkKeys.Right, WalkKeys.Down));
      Assert.Contains("WalkKeys.Dispatch((int)e.KeyCode, e.Modifiers != System.Windows.Forms.Keys.None, IsLive)", Repo("Dwight", "UI", "WalkSession.cs"));
    }

    // ---- the geometry ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_hole_is_a_components_bounds_grown_by_the_margin()
    {
      var holes = WalkGeometry.Holes(new RectangleF?[] { new RectangleF(10, 20, 100, 50) });
      Assert.Equal(new[] { new RectangleF(10 - WalkGeometry.HoleMargin, 20 - WalkGeometry.HoleMargin, 100 + 2 * WalkGeometry.HoleMargin, 50 + 2 * WalkGeometry.HoleMargin) }, holes);
    }

    [Fact]
    public void Each_component_has_its_own_hole_and_a_component_gone_has_none()
    {
      Assert.Equal(2, WalkGeometry.Holes(new RectangleF?[] { new RectangleF(0, 0, 1, 1), null, new RectangleF(5, 5, 1, 1) }).Count);
      Assert.Empty(WalkGeometry.Holes(new RectangleF?[] { null }));
      Assert.Empty(WalkGeometry.Holes(null));
    }

    [Fact]
    public void The_margin_clears_the_widest_ring_of_the_glow()
    {
      // CanvasGlow's outermost ring is inflated by 15 with a pen 15 wide, so it reaches 22.5 past the bounds. The hole was 24, which left the island 1.5 and none read (the eleventh gate).
      var glow = Repo("Dwight", "UI", "CanvasGlow.cs");
      Assert.Contains("for (int ring = 3; ring >= 1; ring--)", glow);
      Assert.Contains("var grow = ring * 5f;", glow);
      Assert.Contains("new Pen(Color.FromArgb(alpha, color), grow)", glow);
      Assert.Equal(22.5f, CanvasLook.GlowReach);
      Assert.Equal(8f, CanvasLook.IslandMargin);
      Assert.Equal(30.5f, WalkGeometry.HoleMargin);
    }

    /// <summary>Two components near each other share one island, because their holes are cut from one region; and so does the band along a step's wire.</summary>
    [Fact]
    public void One_region_takes_every_hole_and_every_wire_band_so_an_island_is_merged()
    {
      var wash = Between(Repo("Dwight", "UI", "CanvasGlow.cs"), "private static void DrawWash(", "private static void ExcludeWire(");
      Assert.Contains("new Region(canvas.Viewport.VisibleRegion)", wash);
      Assert.Contains("region.Exclude(round);", wash);
      Assert.Contains("ExcludeWire(region, a, b);", wash);
      Assert.Equal(1, Regex.Matches(wash, "new Region").Count);
    }

    [Fact]
    public void The_wash_is_the_canvas_colour_read_not_assumed_so_it_is_dark_under_a_dark_skin()
    {
      var dark = Color.FromArgb(30, 30, 30);
      var mono = Color.FromArgb(200, 10, 10);
      var light = Color.FromArgb(212, 208, 200);
      Assert.Equal(dark, WalkGeometry.CanvasColour(false, mono, dark));
      Assert.Equal(light, WalkGeometry.CanvasColour(false, mono, light));
      Assert.Equal(mono, WalkGeometry.CanvasColour(true, mono, dark));   // Grasshopper's monochrome canvas is its own colour setting
      Assert.InRange(CanvasLook.WashAlphaOnLight, 1, 254);   // translucent: the wires stay readable as context
    }

    [Fact]
    public void A_small_step_stops_at_Grasshoppers_own_zoom_and_never_closer()
    {
      var (zoom, _) = WalkGeometry.Frame(new RectangleF(0, 0, 40, 20), 1600, 1000, 0, 0);
      Assert.Equal(WalkGeometry.ClosestZoom, zoom);
      Assert.Equal(1f, WalkGeometry.ClosestZoom);
    }

    [Fact]
    public void A_wide_step_is_fitted_with_the_same_margin_as_a_general_framing()
    {
      var box = new RectangleF(0, 0, 2000, 100);
      var (zoom, _) = WalkGeometry.Frame(box, 1000, 800, 0, 0);
      Assert.Equal(1000f / (2000f + 2 * (2000f * 0.25f + 60f)), zoom, 4);
    }

    [Fact]
    public void A_huge_step_is_held_at_the_furthest_zoom()
    {
      var (zoom, _) = WalkGeometry.Frame(new RectangleF(0, 0, 100000, 100), 1000, 800, 0, 0);
      Assert.Equal(WalkGeometry.FurthestZoom, zoom);
    }

    [Fact]
    public void With_nothing_in_the_way_the_step_sits_at_the_canvas_centre()
    {
      var (_, mid) = WalkGeometry.Frame(new RectangleF(100, 200, 50, 40), 1000, 800, 0, 0);
      Assert.Equal(new PointF(125, 220), mid);
    }

    [Fact]
    public void The_bar_and_the_window_push_the_step_into_what_they_leave_free()
    {
      var box = new RectangleF(100, 200, 50, 40);
      var (zoom, mid) = WalkGeometry.Frame(box, 1000, 800, 100, 360);
      Assert.Equal(1f, zoom);
      // The free part is 640 by 700, at the top left. Its centre is 180 left and 50 above the canvas's, so the view's centre sits that far past the step's.
      Assert.Equal(new PointF(125 + 180, 220 + 50), mid);
    }

    [Fact]
    public void The_shift_scales_with_the_zoom_so_the_step_lands_in_the_free_part_at_any_zoom()
    {
      var box = new RectangleF(0, 0, 4000, 200);
      var (zoom, mid) = WalkGeometry.Frame(box, 1000, 800, 100, 360);
      var stepCentreOnScreenX = 500 + (2000 - mid.X) * zoom;
      var stepCentreOnScreenY = 400 + (100 - mid.Y) * zoom;
      Assert.Equal(320f, stepCentreOnScreenX, 2);   // the middle of the 640 left free
      Assert.Equal(350f, stepCentreOnScreenY, 2);   // the middle of the 700 left free
    }

    [Fact]
    public void Clears_larger_than_the_canvas_still_leave_it_something_to_fit_into()
    {
      var box = new RectangleF(0, 0, 400, 400);
      var (zoom, mid) = WalkGeometry.Frame(box, 1000, 800, 5000, 5000);
      var (quarterZoom, quarterMid) = WalkGeometry.Frame(box, 1000, 800, 600, 750);   // a quarter of each way is the most the clears may take
      Assert.Equal(quarterZoom, zoom);
      Assert.Equal(quarterMid, mid);
    }

    private static readonly OneWindowCore.Box Canvas = new OneWindowCore.Box(100, 50, 1400, 900);

    [Fact]
    public void Framing_leaves_room_for_the_bar_at_its_tallest_whatever_the_page_says_next()
    {
      Assert.Equal(WalkGeometry.BarMaxH + OneWindowCore.EdgeMargin, WalkGeometry.FrameBottomClear);
      Assert.True(WalkGeometry.FrameBottomClear >= Canvas.Y + Canvas.H - WalkGeometry.Bar(Canvas, 5000, null).Y);
      Assert.Contains("surface == WalkSurface.Bar ? WalkGeometry.FrameBottomClear * scale : 0f", Repo("Dwight", "UI", "WalkBar.cs"));
    }

    [Fact]
    public void A_cut_sentence_says_more_follows_with_a_fade_from_the_bars_own_ground()
    {
      var shell = Repo("Dwight", "Resources", "walk-bar.html");
      Assert.Contains("s.parentNode.classList.toggle('more', s.scrollHeight-s.scrollTop-s.clientHeight>1);", shell);
      Assert.Contains("document.getElementById('walksent').addEventListener('scroll',_walkCut);", shell);   // reading to the end takes the fade away
      Assert.Contains("window.addEventListener('resize',_walkAsk);", shell);
      Assert.Contains("_walkCut(); }", shell);
      var css = Repo("Dwight", "Resources", "dwight-components.css");
      Assert.Contains(".walkline.more::after{", css);
      Assert.Contains("background:var(--fade-surface);pointer-events:none", css);   // a click still reaches the words
      Assert.Contains("--fade-surface:linear-gradient(to bottom,transparent,var(--surface));", Repo("Dwight", "Resources", "dwight-tokens.css"));   // the ground .walkbar paints
      Assert.Contains(".walkbar{display:flex;flex-direction:column;gap:var(--space-s);height:100%;padding:var(--space-s) var(--space-m);background:var(--surface);", css);
    }

    /// <summary>
    /// Brief FN: the height the bar asks for is read from the block that holds its words, which is their line boxes, and never from a Range's rectangle,
    /// which hugs the glyphs and is short by each line's leading: a two-line step asked 93 to 94 px, needed 97, and its second line sat under the fade.
    /// The measurement at 1, 1.25 and 1.5 is tools/check-walk-bar-height.js, which CI runs in a real browser; this holds the shape it measures.
    /// </summary>
    [Fact]
    public void The_bar_asks_for_its_words_line_boxes_never_a_ranges_rectangle()
    {
      var shell = Repo("Dwight", "Resources", "walk-bar.html");
      Assert.DoesNotContain("createRange", shell);
      Assert.Contains("<div class=\"walksent\" id=\"walksent\"><div id=\"walkwords\"></div></div>", shell);   // the words in a block of their own, inside the box that scrolls
      Assert.Contains("return Math.ceil(h('walkbar')-h('walksent')+h('walkwords')-0.01); }", shell);
      Assert.Contains("var s=document.getElementById('walkwords'); s.innerHTML=d.html||'';", shell);   // what the step shows is what the ask measures
      Assert.Contains("function _walkAsk(){ dwightSend('h:'+_walkNeed()); _walkCut(); }", shell);
      Assert.Contains("check-walk-bar-height.js", Repo("tools", "check-shell-render.js"));   // the measurement runs where CI runs the harness
    }

    [Fact]
    public void A_step_whose_components_have_gone_keeps_its_sentence_for_the_bar_and_for_the_composer()
    {
      const string sentence = "<code>Loft</code> joins the curves.";
      var view = JsonNode.Parse(WalkText.View(3, 6, sentence, WalkText.Gone(5, 0)));
      Assert.Equal(sentence, view["html"].GetValue<string>());
      Assert.Equal("Its components are no longer on the canvas.", view["gone"].GetValue<string>());

      // The composer takes the sentence alone: the gone line is the bar's, about the canvas now, and not what the step said.
      Assert.Equal("About step 4 of this Explain: Loft joins the curves.", WalkText.AskLine(3, sentence));
      Assert.Contains(": WalkText.AskLine(index, panel.WalkHtml(index), ids));", Repo("Dwight", "UI", "WalkSession.cs"));   // Brief EI: a card asks with its question instead
      Assert.Contains("g.className='walkgone';", Repo("Dwight", "Resources", "walk-bar.html"));
      Assert.Contains("document.createElement('div'); g.className='walkgone'", Repo("Dwight", "Resources", "walk-bar.html"));   // under the sentence, not run on after it
    }

    [Fact]
    public void The_bar_can_shrink_again_and_does_not_take_the_keyboard_when_it_shows()
    {
      var shell = Repo("Dwight", "Resources", "walk-bar.html");
      // The words' own block, never the box stretched to the window, whose height could never ask for less (Brief FN: and never a Range's rectangle).
      Assert.Contains("return Math.ceil(h('walkbar')-h('walksent')+h('walkwords')-0.01); }", shell);
      Assert.Contains("dwightSend('h:'+_walkNeed());", shell);
      Assert.DoesNotContain("+s.scrollHeight)", shell);
      Assert.Contains("ShowActivated = false;", Repo("Dwight", "UI", "WalkBar.cs"));
    }

    [Fact]
    public void The_bar_lies_along_the_bottom_clear_of_the_compass()
    {
      var bar = WalkGeometry.Bar(Canvas, 78, null);
      Assert.Equal(100 + OneWindowCore.EdgeMargin, bar.X);
      Assert.Equal(50 + 900 - 78 - OneWindowCore.EdgeMargin, bar.Y);
      Assert.Equal(78, bar.H);
      Assert.Equal(100 + 1400 - OneWindowCore.CornerClear, bar.X + bar.W);
    }

    [Fact]
    public void The_bar_stops_a_margin_short_of_the_one_window_where_they_would_meet()
    {
      var window = OneWindowCore.Window(Canvas);
      var bar = WalkGeometry.Bar(Canvas, 96, window);
      Assert.True(window.Y + window.H > bar.Y, "at this height they share a row");
      Assert.Equal(window.X - OneWindowCore.EdgeMargin, bar.X + bar.W);
    }

    [Fact]
    public void The_bars_width_does_not_jump_between_a_short_sentence_and_a_long_one()
    {
      // A window whose bottom edge the tall bar would reach and the short one would not.
      var window = new OneWindowCore.Box(1100, 100, 340, 50 + 900 - 12 - 100 - 100);
      Assert.Equal(WalkGeometry.Bar(Canvas, WalkGeometry.BarMaxH, window).W, WalkGeometry.Bar(Canvas, WalkGeometry.BarMinH, window).W);
      Assert.Equal(1100 - OneWindowCore.EdgeMargin, WalkGeometry.Bar(Canvas, WalkGeometry.BarMinH, window).X + WalkGeometry.Bar(Canvas, WalkGeometry.BarMinH, window).W);
    }

    [Fact]
    public void The_bar_stops_short_of_the_chip_nudged_or_not()
    {
      foreach (var nudge in new[] { 0, 150 })
      {
        var chip = OneWindowCore.Chip(Canvas, nudge);
        Assert.Equal(chip.X - OneWindowCore.EdgeMargin, WalkGeometry.Bar(Canvas, 78, chip).X + WalkGeometry.Bar(Canvas, 78, chip).W);
      }
    }

    [Fact]
    public void A_window_the_user_moved_up_out_of_the_way_takes_nothing_from_the_bar()
    {
      var moved = new OneWindowCore.Box(1000, 60, 340, 400);
      Assert.Equal(WalkGeometry.Bar(Canvas, 78, null).W, WalkGeometry.Bar(Canvas, 78, moved).W);
    }

    [Fact]
    public void A_window_the_user_moved_to_the_left_pushes_the_bar_right_of_it()
    {
      var moved = new OneWindowCore.Box(120, 400, 340, 540);
      var bar = WalkGeometry.Bar(Canvas, 78, moved);
      Assert.Equal(120 + 340 + OneWindowCore.EdgeMargin, bar.X);
      Assert.Equal(100 + 1400 - OneWindowCore.CornerClear, bar.X + bar.W);
    }

    [Theory]
    [InlineData(1, WalkGeometry.BarMinH)]
    [InlineData(100, 100)]
    [InlineData(5000, WalkGeometry.BarMaxH)]
    public void The_bars_height_is_the_pages_within_its_limits(int want, int h) =>
      Assert.Equal(h, WalkGeometry.Bar(Canvas, want, null).H);

    // ---- the surface: where a Walk tells its story, a pure function of the canvas's rectangle ---------------------------------------

    private static OneWindowCore.Box CanvasOf(int w, int h = 900) => new OneWindowCore.Box(100, 50, w, h);

    [Theory]
    [InlineData(488, "Bar")]
    [InlineData(487, "Panel")]
    [InlineData(1400, "Bar")]
    public void With_nothing_in_the_way_the_width_alone_decides(int w, string surface) =>
      Assert.Equal(surface, WalkGeometry.Surface(true, CanvasOf(w), null).ToString());

    [Theory]
    [InlineData(548, "Bar")]
    [InlineData(547, "Panel")]
    public void With_the_chip_in_its_corner_the_bar_needs_a_little_more(int w, string surface) =>
      Assert.Equal(surface, WalkGeometry.Surface(true, CanvasOf(w), OneWindowCore.Chip(CanvasOf(w), 0)).ToString());

    [Theory]
    [InlineData(796, "Bar")]
    [InlineData(795, "Panel")]
    public void With_the_one_window_open_the_bar_needs_room_beside_it(int w, string surface) =>
      Assert.Equal(surface, WalkGeometry.Surface(true, CanvasOf(w), OneWindowCore.Window(CanvasOf(w))).ToString());

    [Fact]
    public void Grasshopper_at_half_the_screen_beside_Rhino_at_150_percent()
    {
      // A 1920 by 1080 screen at 144 dpi, Grasshopper on its right half: physical pixels in, logical points out, as the one window reads them (#176).
      var canvas = OneWindowCore.CanvasLogical(960, 90, 960, 960, 144);
      Assert.Equal(640, canvas.W);
      Assert.Equal(WalkSurface.Panel, WalkGeometry.Surface(true, canvas, OneWindowCore.Window(canvas)));   // the window open: the panel is the Walk
      Assert.Equal(WalkSurface.Bar, WalkGeometry.Surface(true, canvas, OneWindowCore.Chip(canvas, 0)));    // minimised to the chip: there is room
    }

    [Theory]
    [InlineData(320, "Bar")]
    [InlineData(319, "Panel")]
    public void A_canvas_too_short_to_hold_the_bar_and_a_step_above_it_leaves_it_to_the_panel(int h, string surface) =>
      Assert.Equal(surface, WalkGeometry.Surface(true, CanvasOf(1400, h), null).ToString());

    [Fact]
    public void A_canvas_not_showing_hides_the_bar_at_any_size()
    {
      Assert.Equal(WalkSurface.Hidden, WalkGeometry.Surface(false, CanvasOf(1400), null));
      Assert.Equal(WalkSurface.Hidden, WalkGeometry.Surface(false, CanvasOf(200), null));
      Assert.Equal(WalkSurface.Hidden, WalkGeometry.Surface(true, null, null));
    }

    [Theory]
    [InlineData(true, false, true, 800, 600, true)]
    [InlineData(false, false, true, 800, 600, false)]   // the editor closed or hidden
    [InlineData(true, true, true, 800, 600, false)]     // minimised
    [InlineData(true, false, false, 800, 600, false)]
    [InlineData(true, false, true, 0, 600, false)]
    [InlineData(true, false, true, 800, 0, false)]
    public void Whether_the_canvas_is_showing(bool editorVisible, bool minimised, bool canvasVisible, int w, int h, bool showing) =>
      Assert.Equal(showing, WalkGeometry.CanvasShowing(editorVisible, minimised, canvasVisible, w, h));

    [Theory]
    [InlineData(true, true, true, false)]    // the same document
    [InlineData(false, true, true, false)]   // the same document behind a closed editor: the Walk waits
    [InlineData(true, true, false, true)]    // another document
    [InlineData(false, true, false, true)]
    [InlineData(true, false, false, true)]   // the canvas is showing an empty document: the file was closed
    [InlineData(false, false, false, false)] // the editor is closed and says nothing about a document: the Walk waits
    public void What_a_document_change_does(bool showing, bool hasDocument, bool same, bool ends) =>
      Assert.Equal(ends, WalkGeometry.DocumentEnds(showing, hasDocument, same));

    [Fact]
    public void Wherever_the_bar_is_chosen_it_is_on_the_canvas_wide_enough_and_clear_of_the_window()
    {
      var chosen = 0;
      foreach (var w in Enumerable.Range(0, 80).Select(i => 300 + i * 25))
        foreach (var h in new[] { 300, 320, 500, 1000 })
        {
          var canvas = new OneWindowCore.Box(37, 21, w, h);
          var others = new OneWindowCore.Box?[] { null, OneWindowCore.Chip(canvas, 0), OneWindowCore.Chip(canvas, 150), OneWindowCore.Window(canvas),
            new OneWindowCore.Box(canvas.X + 20, canvas.Y + 20, 340, h - 40), new OneWindowCore.Box(canvas.X + w / 2 - 170, canvas.Y + 20, 340, h - 40) };
          foreach (var other in others)
          {
            if (WalkGeometry.Surface(true, canvas, other) != WalkSurface.Bar) continue;
            chosen++;
            foreach (var want in new[] { 1, 100, 5000 })
            {
              var bar = WalkGeometry.Bar(canvas, want, other);
              Assert.True(bar.W >= WalkGeometry.BarMinW, bar + " in " + canvas);
              Assert.True(bar.X >= canvas.X && bar.X + bar.W <= canvas.X + canvas.W, bar + " in " + canvas);
              Assert.True(bar.Y >= canvas.Y && bar.Y + bar.H <= canvas.Y + canvas.H, bar + " in " + canvas);
              if (other is OneWindowCore.Box o)
                Assert.False(bar.X < o.X + o.W && o.X < bar.X + bar.W && bar.Y < o.Y + o.H && o.Y < bar.Y + bar.H, bar + " meets " + o);
            }
          }
        }
      Assert.True(chosen > 100, "the sweep must have reached the bar: " + chosen);
    }

    [Fact]
    public void An_open_window_on_the_right_takes_its_width_from_framing_and_one_on_the_left_takes_none()
    {
      var window = OneWindowCore.Window(Canvas);
      Assert.Equal(100 + 1400 - window.X, WalkGeometry.RightClear(Canvas, window));
      Assert.Equal(0, WalkGeometry.RightClear(Canvas, new OneWindowCore.Box(120, 100, 340, 500)));
      Assert.Equal(0, WalkGeometry.RightClear(Canvas, null));
    }

    // ---- the words ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_steps_are_counted_from_one() => Assert.Equal("Step 3 of 6", WalkText.StepLine(2, 6));

    [Fact]
    public void All_there_says_nothing_and_gone_says_so_in_the_plugins_voice()
    {
      Assert.Null(WalkText.Gone(5, 5));
      Assert.Equal("Its components are no longer on the canvas.", WalkText.Gone(5, 0));
      Assert.Equal("Its components are no longer on the canvas.", WalkText.Gone(0, 0));
      Assert.Equal("2 of its 5 components are no longer on the canvas.", WalkText.Gone(5, 3));
    }

    [Fact]
    public void The_composer_line_holds_the_sentence_as_plain_words()
    {
      Assert.Equal("About step 3 of this Explain: Distance measures to the Sun & back.",
        WalkText.AskLine(2, "<code>Distance</code> measures to the <strong>Sun</strong> &amp; back."));
      Assert.Equal("About step 1 of this Explain: ", WalkText.AskLine(0, ""));   // a step with no sentence leaves the person a line to finish
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("<p>one</p>\n<p>two</p>", "one two")]
    [InlineData("a  <em>b</em>\n c", "a b c")]
    [InlineData("&lt;script&gt;", "<script>")]
    public void Plain_text_strips_tags_and_decodes(string html, string text) =>
      Assert.Equal(text, WalkText.PlainText(html));

    [Fact]
    public void The_bars_document_carries_what_it_draws_and_a_gone_line_only_when_there_is_one()
    {
      var view = JsonNode.Parse(WalkText.View(2, 6, "<code>Loft</code>", null));
      Assert.Equal("Step 3 of 6", view["label"].GetValue<string>());
      Assert.Equal("<code>Loft</code>", view["html"].GetValue<string>());
      Assert.Equal(2, view["i"].GetValue<int>());
      Assert.Equal(6, view["n"].GetValue<int>());
      Assert.Null(view["gone"]);
      Assert.Equal("gone", JsonNode.Parse(WalkText.View(0, 1, null, "gone"))["gone"].GetValue<string>());
      Assert.Equal("", JsonNode.Parse(WalkText.View(0, 1, null, "gone"))["html"].GetValue<string>());
    }

    // ---- the default, and the name --------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0, "detailed")]
    [InlineData(1, "detailed")]
    [InlineData(2, "walk")]
    [InlineData(20, "walk")]
    public void A_walk_for_two_or_more_and_Detailed_for_one(int count, string mode) =>
      Assert.Equal(mode, ExplainModes.ForCount(count));

    [Theory]
    [InlineData("walk", "step by step")]
    [InlineData("detailed", "detailed")]
    [InlineData("short", "short")]
    public void A_row_says_the_modes_name(string mode, string label) => Assert.Equal(label, ExplainModes.Label(mode));

    [Theory]
    [InlineData("Explain", "on canvas · 5 components", "step by step · 5 components")]
    [InlineData("Explain", "On Canvas · 5 components", "step by step · 5 components")]
    [InlineData("Explain", "walk · 5 components", "step by step · 5 components")]
    [InlineData("Explain", "walk", "step by step")]
    [InlineData("Explain", "walkthrough · 5 components", "walkthrough · 5 components")]
    [InlineData("Diagnose", "walk · 5 components", "walk · 5 components")]
    [InlineData("Explain", "detailed · 5 components", "detailed · 5 components")]
    [InlineData("Diagnose", "on canvas · 5 components", "on canvas · 5 components")]
    [InlineData("Explain", null, null)]
    public void A_persisted_row_that_says_on_canvas_reads_as_a_walk(string action, string subtitle, string reads) =>
      Assert.Equal(reads, DwightVerbs.CanonicalSubtitle(action, subtitle));

    [Fact]
    public void The_store_reads_the_old_name_as_a_walk_and_does_not_write_it_back()
    {
      var rec = new SessionRecord { Commands = new List<StoredCommand> { new StoredCommand { Action = "Explain", Subtitle = "on canvas · 3 components" } } };
      var json = SessionStoreCore.Write(rec);
      Assert.DoesNotContain("on canvas", json);
      Assert.Equal("step by step · 3 components", SessionStoreCore.Read(json).Record.Commands.Single().Subtitle);

      // A file an earlier build wrote, under either earlier name.
      foreach (var name in new[] { "on canvas", "walk" })
      {
        var legacy = json.Replace("step by step", name);
        Assert.Contains(name, legacy);
        Assert.Equal("step by step · 3 components", SessionStoreCore.Read(legacy).Record.Commands.Single().Subtitle);
      }
    }

    [Fact]
    public void The_menus_read_Step_by_step_Detailed_Short_with_Step_by_step_only_for_a_group()
    {
      foreach (var menu in new[] { Repo("Dwight", "UI", "ChordMenu.cs"), Repo("Dwight", "DwightTopBarMenu.cs") })   // Brief EU: the chord's shape is ChordMenu's
      {
        var walk = menu.IndexOf("\"Step by step\"", StringComparison.Ordinal);
        Assert.DoesNotContain("\"Walk\"", menu);
        var detailed = menu.IndexOf("\"Detailed\"", StringComparison.Ordinal);
        var shortOne = menu.IndexOf("\"Short\"", StringComparison.Ordinal);
        Assert.True(walk >= 0 && walk < detailed && detailed < shortOne);
        Assert.DoesNotContain("On canvas", menu);
        Assert.DoesNotContain("\"oncanvas\"", menu);
      }
      Assert.Contains("if (selCount >= 2) Sub(explain, \"Step by step\", doors.ExplainWalk);", Repo("Dwight", "UI", "ChordMenu.cs"));
      Assert.Contains("ExplainWalk = () => LlmActions.Explain(Dwight.UI.ExplainModes.Walk),", Repo("Dwight", "DwightCanvasPopup.cs"));
      // Brief DE: Learn is a Walk with questions, so it is group-only and shown with the Walk.
      Assert.Contains("walk.Visible = learn.Visible = DwightActions.CurrentSelection().Count >= 2;", Repo("Dwight", "DwightTopBarMenu.cs"));
    }

    [Fact]
    public void Brief_EO_the_Walk_is_called_Step_by_step_wherever_a_person_reads_it()
    {
      Assert.Equal("Step by step", DeckText.WalkThis);
      Assert.Equal("step by step", ExplainModes.Label(ExplainModes.Walk));   // the panel header: "Explain · step by step · 5 components"
      Assert.Equal("End of the steps", WalkText.EndOfWalk);
      Assert.Equal("Back to step by step", WalkText.BackToWalk);
      foreach (var s in new[] { DeckText.WalkThis, DeckText.WalkThisTitle, WalkText.EndOfWalk, WalkText.BackToWalk, WalkScope.NothingWithin, TeachingText.Unavailable })
        Assert.DoesNotMatch(@"\bwalk(ed)?\b", s.ToLowerInvariant());
      Assert.Equal("walk", ExplainModes.Walk);   // internal id and persisted rows are unchanged
      Assert.Contains("\"Step by step renders in the WebView panel.\"", Repo("Dwight", "UI", "ResultPopup.cs"));   // the plain window's line
      Assert.DoesNotContain("Walk this", Repo("Dwight", "UI", "WebPanel.cs"));
    }

    [Fact]
    public void With_no_mode_named_the_count_decides_and_one_component_cannot_walk()
    {
      var actions = Repo("Dwight", "LlmActions.cs");
      Assert.Contains("ExplainOn(guids, mode ?? ExplainModes.ForCount(guids.Count));", actions);
      // Brief DE: a Learn run is a Walk when it is chosen, so it meets the same two rules.
      Assert.Contains("if ((mode == ExplainModes.Walk || mode == ExplainModes.Learn) && guids.Count < 2) mode = ExplainModes.Detailed;", actions);
      Assert.Contains("if ((mode == ExplainModes.Walk || mode == ExplainModes.Learn) && !(popup is WebPanel)) mode = ExplainModes.Detailed;", actions);
      Assert.DoesNotContain("\"oncanvas\"", actions);
      Assert.DoesNotContain("\"on canvas\"", actions);
    }

    [Theory]
    [InlineData("A walk on the canvas, one step at a time, or a detailed or short answer", "README.md")]
    [InlineData("- **Step by step**: the default for two or more components.", "docs", "public", "verbs.md")]
    [InlineData("- **Explain**: Short, Detailed and Step by step, with Learn as a deck of cards that ask about the selection", "docs", "public", "changelog.md")]
    public void The_public_pages_that_name_the_modes_say_Walk(string says, params string[] page)
    {
      var text = Repo(page);
      Assert.Contains(says, text);
      Assert.DoesNotContain("on-canvas", text);
      Assert.DoesNotContain("On canvas", text);
    }

    [Fact]
    public void Nothing_the_model_is_sent_changed_its_name()
    {
      // The surface's id is in the payload and the contract; the Walk is what the plugin does with the steps, so these stay byte for byte.
      Assert.Contains("Explain(graph, \"explain.oncanvas\", hops: 1)", Repo("Dwight", "Data", "Payloads.cs"));
      Assert.Contains("Payloads.ExplainOnCanvas(graph)", Repo("Dwight", "LlmActions.cs"));
      Assert.Contains("Prompts.Prompts.ExplainOnCanvasInstruction", Repo("Dwight", "LlmActions.cs"));
      Assert.Contains("(\"explain.oncanvas\", \"Explain (walk)\")", Repo("Dwight", "Data", "PayloadContract.cs"));
    }

    // ---- the seams that need Grasshopper, by text --------------------------------------------------------------------------------------

    private static string RepoPath(params string[] parts)
    {
      var dir = new DirectoryInfo(AppContext.BaseDirectory);
      while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Dwight.sln"))) dir = dir.Parent;
      return Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
    }

    private static string Repo(params string[] parts) => File.ReadAllText(RepoPath(parts)).Replace("\r\n", "\n");

    /// <summary>Each in turn, and each present: comparing two IndexOf results passes when the first line has been deleted, because -1 is less than anything. The first mutation pass found three such holes.</summary>
    private static void InOrder(string source, params string[] parts)
    {
      var at = 0;
      foreach (var part in parts)
      {
        var found = source.IndexOf(part, at, StringComparison.Ordinal);
        Assert.True(found >= 0, "not found in order: " + part);
        at = found + part.Length;
      }
    }

    private static string Between(string source, string from, string to)
    {
      var a = source.IndexOf(from, StringComparison.Ordinal);
      Assert.True(a >= 0, from);
      var b = source.IndexOf(to, a + from.Length, StringComparison.Ordinal);
      Assert.True(b > a, to);
      return source.Substring(a, b - a);
    }

    [Fact]
    public void The_order_is_taken_before_anything_records_the_steps()
    {
      var actions = Repo("Dwight", "LlmActions.cs");
      InOrder(actions, "var stepsView = WalkOrder.SortView(TranslateSteps(graph, res.Json), WireGuids(graph));", "PanelScript.New().Op(PanelScript.SetSteps, stepsView)");
    }

    [Fact]
    public void A_finished_run_arrives_after_it_is_recorded_and_only_on_the_page_the_person_is_on()
    {
      var actions = Repo("Dwight", "LlmActions.cs");
      // Brief DE: the lesson goes on the panel before the render, and the row Complete wrote is handed to the panel before the Walk starts, so its score has a row to go on.
      Assert.Contains("DwightSession.Complete(ctx, $\"{modeLabel} · {Count(guids.Count)}\", cost, script, null, spend); if (popup is WebPanel walkPanel) walkPanel.RunRow = ctx.CompletedRow; WalkSession.RunFinished(popup as WebPanel); });", actions);
      var session = Repo("Dwight", "UI", "WalkSession.cs");
      var finished = Between(session, "public static void RunFinished(", "Arrive(panel);");
      Assert.Contains("DwightDock.Host()?.IsFace(panel) != true) return;", finished);
    }

    [Fact]
    public void Every_control_goes_through_the_state_and_a_move_that_cannot_happen_shows_nothing()
    {
      var session = Repo("Dwight", "UI", "WalkSession.cs");
      Assert.Contains("if (_state.GoTo(i)) { _play.UserMoved(); Step(NavReason.UserClicked); }", session);
      Assert.Contains("if (_state.Next()) { _play.UserMoved(); Step(NavReason.UserClicked); }", session);
      Assert.Contains("if (IsLive && _state.Back()) { _play.UserMoved(); Step(NavReason.UserClicked); }", session);

      var panel = Repo("Dwight", "UI", "WebPanel.cs");
      Assert.Contains("WalkSession.GoTo(this, i);", panel);
      Assert.DoesNotContain("CanvasGlow.ShowMarks(", panel);

      // Reading a finished Explain while something else runs lights the step and nothing more.
      var goTo = Between(session, "public static void GoTo(WebPanel panel, int i)", "public static void GoTo(int i)");
      InOrder(goTo, "if (DwightSession.IsBusy) { LightOnly(panel, i); return; }", "Begin(panel, i, NavReason.UserClicked);");
      Assert.DoesNotContain("ShowWalkStep", goTo);
      Assert.DoesNotContain("WalkBar", goTo);

      var bar = Repo("Dwight", "UI", "WalkBar.cs");
      foreach (var call in new[] { "case \"next\": WalkSession.Next();", "case \"back\": WalkSession.Back();", "WalkSession.GoTo(i);", "case \"close\": WalkSession.BarClosed();", "case \"key\": WalkSession.PageKey(arg, true);", "DwightCanvasPopup.Safe(WalkSession.Ask)" })
        Assert.Contains(call, bar);
    }

    [Fact]
    public void The_canvas_key_hook_lives_only_as_long_as_the_walk_and_reads_the_dispatcher()
    {
      var session = Repo("Dwight", "UI", "WalkSession.cs");
      Assert.Contains("_canvas.KeyDown += OnCanvasKey;", Between(session, "private static void Begin(", "private static bool End()"));
      Assert.Contains("_canvas.KeyDown -= OnCanvasKey;", Between(session, "private static bool End()", "private static void Show("));
      var hook = Between(session, "private static void OnCanvasKey(", "private static void Do(");
      Assert.Contains("WalkKeys.Dispatch((int)e.KeyCode, e.Modifiers != System.Windows.Forms.Keys.None, IsLive)", hook);
      Assert.Contains("if (key == WalkKey.None) return;", hook);
    }

    [Fact]
    public void The_wash_is_the_walks_and_every_other_way_of_setting_a_glow_clears_it()
    {
      var glow = Repo("Dwight", "UI", "CanvasGlow.cs");
      Assert.Equal(2, Regex.Matches(glow, Regex.Escape("_walkWash = wash;")).Count);   // a step, and the dim switch on the step showing (Brief EC unit 2)
      Assert.Contains("_walkWash = wash;", Between(glow, "public static void ShowWalkStep(", "public static void SetWalkWash("));
      Assert.Contains("_walkWash = wash;", Between(glow, "public static void SetWalkWash(", "public static void HideWalkStep("));
      Assert.Contains("_walkWash = false;", Between(glow, "public static void Show(IEnumerable<Guid> guids", "public static void ShowTiered("));
      Assert.Contains("_walkWash = false;", Between(glow, "private static void ShowTieredInner(", "public static void SetLabels("));
      Assert.Contains("_walkWash = false;", Between(glow, "public static void ShowMarks(", "public static void ClearMarks("));
      Assert.Contains("public static void Clear() { TreeLens.Clear(); _walkWash = false;", glow);
      Assert.Contains("public static void ClearWalkLayer() { _walkWash = false; _marks.Clear();", glow);

      // ShowWalkStep goes through ShowMarks, so nothing else about a step's glow can differ from a step click's.
      Assert.Contains("ShowMarks(glow, marks, owner);", Between(glow, "public static void ShowWalkStep(", "public static void ClearWalkLayer("));
      foreach (var other in new[] { "ShowDiagnosis(", "ShowTiered(" })
        Assert.DoesNotContain("ShowWalkStep", Between(glow, "public static void " + other, "}"));
    }

    [Fact]
    public void The_wash_is_drawn_under_everything_of_ours_from_live_bounds_chosen_by_the_canvas_colour()
    {
      var glow = Repo("Dwight", "UI", "CanvasGlow.cs");
      var paint = Between(glow, "private static void OnPaint(", "private static void DrawWash(");
      InOrder(paint, "if (_walkWash) DrawWash(g, doc, sender);", "DrawGlow(g, doc, id, Amber, 1f)");
      Assert.Contains("if (!_walkWash && _guids.Count == 0", paint);   // a step with nothing left still washes, so the bar's sentence about it is not alone on a lit canvas
      var wash = Between(glow, "private static void DrawWash(", "private static void DrawGlow(");
      Assert.Contains("CanvasLook.Wash(CanvasColour)", wash);
      Assert.Contains("WalkGeometry.CanvasColour(GH_Skin.canvas_mono, GH_Skin.canvas_mono_color, GH_Skin.canvas_back)", glow);
      Assert.Contains("WalkGeometry.Holes(_guids.Select(", wash);
      Assert.Contains("region.Exclude(round);", wash);
      Assert.Contains("canvas.Viewport.VisibleRegion", wash);
    }

    /// <summary>The bar is told Every wire's state only when a lens is live. Absent means no lens, so a Walk with none has nothing to press.</summary>
    [Fact]
    public void The_bars_view_says_every_wire_only_with_a_lens()
    {
      Assert.Null(JsonNode.Parse(WalkText.View(0, 2, "x", null))["everywire"]);
      Assert.False(JsonNode.Parse(WalkText.View(0, 2, "x", null, everyWire: false))["everywire"].GetValue<bool>());
      Assert.True(JsonNode.Parse(WalkText.View(0, 2, "x", null, everyWire: true))["everywire"].GetValue<bool>());
    }

    [Fact]
    public void Leaving_keeps_the_selection_lit_and_dropping_clears_only_the_walks_layer()
    {
      var session = Repo("Dwight", "UI", "WalkSession.cs");
      var leave = Between(session, "public static void Leave(bool fromBar)", "public static void Drop()");
      Assert.Contains("if (panel != null && !panel.IsClosed && source != null && source.Count > 0) CanvasGlow.Show(source, false, panel);", leave);
      Assert.Contains("if (fromBar) DwightDock.GiveKeysToCanvas();", leave);
      Assert.Contains("if (End()) CanvasGlow.ClearWalkLayer();", session);

      var end = Between(session, "private static bool End()", "private static void Show(");
      Assert.Contains("if (_state == null || !_state.Leave()) return false;", end);
      Assert.Contains("WalkBar.Existing?.Dismiss();", end);
      Assert.Contains("_panel.MarkWalkStep(-1);", end);
    }

    [Fact]
    public void Each_way_a_walk_ends_is_wired()
    {
      var core = Repo("Dwight", "DwightSession.cs");
      var start = Between(core, "private static Context Start(", "if (ctx.Panel is WebPanel wp)");
      InOrder(start, "try { keepsWalk = WalkSession.RunStarting(adopt as WebPanel); } catch { }", "if (!keepsWalk) CanvasGlow.Show(ctx.Guids, thinking: true, owner: ctx.Panel);");
      Assert.Contains("try { WalkSession.Drop(); } catch { }", Between(core, "public static void Restart()", "private static void ForceEnd("));

      // Brief EC unit 5b: another run ends a Walk, except a turn in the chat this Walk's step opened, which leaves it on its step and the canvas lit.
      var starting = Between(Repo("Dwight", "UI", "WalkSession.cs"), "public static bool RunStarting(WebPanel adopt)", "/// <summary>Esc, the close, the summary.");
      InOrder(starting, "if (IsLive && _state.Started && adopt != null && !string.IsNullOrEmpty(_askedThread) && string.Equals(adopt.ThreadId, _askedThread, StringComparison.Ordinal)) return true;", "Drop();", "return false;");

      var session = Repo("Dwight", "UI", "WalkSession.cs");
      Assert.Contains("panel.Closed += OnPageClosed;", session);
      Assert.Contains("private static void OnPageClosed(object sender, EventArgs e) => Drop();", session);
      Assert.Contains("if (WalkGeometry.DocumentEnds(WalkBar.CanvasShowingNow(), doc != null, ReferenceEquals(doc, _doc))) { Drop(); return; }", session);
      Assert.Contains("_tick.Elapsed += (s, e) => Tick();", session);

      Assert.Contains("if (WalkSession.OnAStep(this)) WalkSession.Leave(false);", Repo("Dwight", "UI", "WebPanel.cs"));   // the summary: back to the whole selection
    }

    [Fact]
    public void Minimising_to_see_the_canvas_keeps_a_live_walks_glow()
    {
      Assert.Contains("if (p != null && !WalkSession.OnAStep(p)) CanvasGlow.ClearIfOwner(p);", Repo("Dwight", "UI", "DwightDock.cs"));
      Assert.Contains("|| WalkSession.OnAStep(this)) return; try { CanvasGlow.Show(_sourceGuids, false, this)", Repo("Dwight", "UI", "WebPanel.cs"));
    }

    [Fact]
    public void Asking_selects_the_step_and_leaves_words_in_the_composer_with_no_model_call()
    {
      var ask = Between(Repo("Dwight", "UI", "WalkSession.cs"), "public static void Ask()", "public static void Tick()");
      InOrder(ask, "Select(_state.Current);", "DwightScaffold.GraduateFrom(panel.HandOff(),", "host.KeepingWalk(panel, door)", "_askedThread = chat?.ThreadId;", "chat?.SetWalkBack(WalkText.BackToWalk);");
      foreach (var ends in new[] { "Drop()", "End()", "Leave(" }) Assert.DoesNotContain(ends, ask);   // Brief EC unit 5b: only Leave ends a Walk

      var door = Between(Repo("Dwight", "UI", "DwightScaffold.cs"), "internal static WebPanel GraduateFrom(", "private static WebPanel ReopenRunThread(");
      Assert.Contains("wp.SetComposerDraft(text);", door);
      foreach (var spend in new[] { "RunTurn", "OnUserMessage", "AppendUserText" }) Assert.DoesNotContain(spend, door);

      var shell = Repo("Dwight", "Resources", "panel-shell.html");
      var draft = Between(shell, "function dwightComposerDraft(text)", "function dwightFocusComposer(");
      Assert.Contains("c.value=text||'';", draft);
      Assert.DoesNotContain("dwightSend", draft);
    }

    // docs/dwight-ui-system.md is a development document, and the public tree does not carry it. With no such document, this test
    // measures that it is running in the public tree: docs/phases must be absent too. Every other line reads shipped files and runs in both trees.
    [Fact]
    public void The_bar_is_a_surface_of_the_ui_system()
    {
      if (File.Exists(RepoPath("docs", "dwight-ui-system.md"))) Assert.Contains("`.walkbar`", Repo("docs", "dwight-ui-system.md"));
      else Assert.False(Directory.Exists(RepoPath("docs", "phases")), "docs/dwight-ui-system.md is missing from a development tree (docs/phases is here).");
      Assert.Contains(".walkstrip{display:flex;flex-wrap:wrap;", Repo("Dwight", "Resources", "dwight-components.css"));   // the render at 420 lost Ask and the close before this
      Assert.Contains("'walk-bar.html': 'walk'", Repo("tools", "ui-style-audit.js"));
      Assert.Contains("file: 'walk-bar.html'", Repo("tools", "ui-fixtures.js"));
      Assert.Contains("'walk-bar.html': [", Repo("tools", "check-shell-render.js"));
      Assert.Contains("LogicalName=\"Dwight.WalkBar.html\"", Repo("Dwight", "Dwight.csproj"));
      Assert.Contains("WebViewSupport.LoadResource(\"Dwight.WalkBar.html\")", Repo("Dwight", "UI", "WalkBar.cs"));
      Assert.Contains("NativeWindowEffects.SetGhOwner(this);", Repo("Dwight", "UI", "WalkBar.cs"));

      var csp = new Regex("Content-Security-Policy\" content=\"([^\"]*)\"");
      Assert.Equal(csp.Match(Repo("Dwight", "Resources", "feedback.html")).Groups[1].Value, csp.Match(Repo("Dwight", "Resources", "walk-bar.html")).Groups[1].Value);
    }

    [Fact]
    public void The_bar_follows_the_canvas_by_its_own_events_and_never_reads_the_screen()
    {
      var session = Repo("Dwight", "UI", "WalkSession.cs");
      var begin = Between(session, "private static void Begin(", "private static bool End()");
      var end = Between(session, "private static bool End()", "private static void Show(");
      foreach (var hook in new[] { "_canvas.Move", "_canvas.SizeChanged", "_canvas.VisibleChanged", "_editor.Move", "_editor.Resize", "_editor.VisibleChanged" })
      {
        Assert.Contains(hook + " += OnPlaceChanged;", begin);
        Assert.Contains(hook + " -= OnPlaceChanged;", end);
      }
      Assert.Contains("private static void OnPlaceChanged(object sender, EventArgs e)", session);
      Assert.Contains("WalkBar.Get()?.Sync();", session);

      var bar = Repo("Dwight", "UI", "WalkBar.cs");
      var sync = Between(bar, "internal WalkSurface Sync()", "internal void Dismiss()");
      Assert.Contains("WalkGeometry.SurfaceFor(WalkSession.FocusActive, CanvasShowingNow(), box, DwightDock.BoxNow())", sync);
      Assert.Contains("DwightDock.CanvasLogical()", sync);
      Assert.DoesNotContain("Screen.", bar);
      Assert.Contains("ed.WindowState == System.Windows.Forms.FormWindowState.Minimized", Between(bar, "internal static bool CanvasShowingNow()", "internal WalkSurface Sync()"));
      Assert.DoesNotContain("_tick", bar);   // the backstop for the one window moving is the Walk's own tick, because a Walk with no Focus never makes a bar
      Assert.Contains("if (_focus.Active) Replace();", Between(session, "public static void Tick()", "private static void Begin("));
    }

    [Fact]
    public void Where_the_bar_cannot_be_placed_it_hides_and_the_panel_carries_the_walk_on_the_same_state()
    {
      var bar = Repo("Dwight", "UI", "WalkBar.cs");
      var sync = Between(bar, "internal WalkSurface Sync()", "internal void Dismiss()");
      Assert.Contains("if (now == WalkSurface.Bar) Place();", sync);
      Assert.Contains("else if (_shownOnce && Visible) Visible = false;", sync);
      Assert.Contains("WalkSession.SurfaceChanged(was.Value, now);", sync);

      var session = Repo("Dwight", "UI", "WalkSession.cs");
      var mark = Between(session, "private static void Mark(", "private static void OnPageClosed(");
      Assert.Contains("_panel.MarkWalkStep(_state.Index, surface != WalkSurface.Bar, gone);", mark);

      // The panel's controls go to the same calls the bar's do.
      var panel = Repo("Dwight", "UI", "WebPanel.cs");
      foreach (var call in new[] { "if (cmd == \"walk:next\") { WalkSession.Next(); return; }", "if (cmd == \"walk:back\") { WalkSession.Back(); return; }", "if (cmd == \"walk:ask\") { DwightCanvasPopup.Safe(WalkSession.Ask); return; }", "if (cmd == \"walk:leave\") { WalkSession.Leave(false); return; }" })
        Assert.Contains(call, panel);
    }

    [Fact]
    public void Only_the_bar_stopping_fitting_opens_the_window_so_a_person_who_minimises_it_is_obeyed()
    {
      var change = Between(Repo("Dwight", "UI", "WalkSession.cs"), "public static void SurfaceChanged(", "public static void Tick()");
      Assert.Contains("if (was == WalkSurface.Bar && now == WalkSurface.Panel) DwightDock.Host()?.OpenIfClosed();", change);
      Assert.Contains("internal void OpenIfClosed() { if (!_expanded) Step(WindowEvent.ChipClick); }", Repo("Dwight", "UI", "DwightDock.cs"));
    }

    [Fact]
    public void Framing_leaves_room_for_the_bar_only_where_the_bar_is()
    {
      var clears = Between(Repo("Dwight", "UI", "WalkBar.cs"), "internal static (float Bottom, float Right) Clears(", "private void Handle(");
      Assert.Contains("surface == WalkSurface.Bar ? WalkGeometry.FrameBottomClear * scale : 0f", clears);
    }

    [Fact]
    public void A_closed_editor_keeps_the_walk_and_another_document_ends_it()
    {
      var tick = Between(Repo("Dwight", "UI", "WalkSession.cs"), "public static void Tick()", "private static void Begin(");
      Assert.Contains("WalkGeometry.DocumentEnds(WalkBar.CanvasShowingNow(), doc != null, ReferenceEquals(doc, _doc))", tick);
    }

    [Fact]
    public void The_Tree_Lens_rides_the_step_and_its_lines_sit_under_that_step()
    {
      var session = Repo("Dwight", "UI", "WalkSession.cs");
      var show = Between(session, "private static void Show(", "private static string GoneNow()");
      InOrder(show, "_onCanvas.StepShown(guids, DwightSettings.Current.WalkDim);", "_panel.SetLensStep(i);", "TreeLens.SetCurrent(_state.Current);");

      var end = Between(session, "private static bool End()", "private static void Show(");
      InOrder(end, "_panel.SetLensStep(-1);", "TreeLens.SetCurrent(null);");

      var light = Between(session, "private static void LightOnly(", "public static void GoTo(int i)");
      InOrder(light, "panel.SetLensStep(i);", "TreeLens.SetCurrent(steps[i]);");

      var panel = Repo("Dwight", "UI", "WebPanel.cs");
      Assert.Contains("internal void SetLensStep(int i) => _lensStep = i;", panel);
      Assert.Contains("_stepHtml = ParseStepHtml(stepsJson); _stepTexts = ParseStepTexts(stepsJson, out _stepSummary); Run($\"dwightSteps({Js(WalkOrder.ForPanel(stepsJson))})\"); BeginLens(); }", panel);   // the lens still begins when the steps arrive (the notes branch adds the pin texts to the same line: Brief DC)
    }

    [Fact]
    public void The_lens_is_painted_over_the_wash_not_under_it()
    {
      var paint = Between(Repo("Dwight", "UI", "CanvasGlow.cs"), "private static void OnPaint(", "private static void DrawWash(");
      InOrder(paint, "if (_walkWash) DrawWash(g, doc, sender);", "TreeLens.Paint(g, sender);");
    }

    [Fact]
    public void The_footer_holds_the_controls_in_one_spot_and_the_card_holds_none()
    {
      var shell = Repo("Dwight", "Resources", "panel-shell.html");
      // Left-aligned in the footer, before the status and Chat and Copy, in the library's icon button, each with its word as tooltip and accessible name.
      InOrder(shell, "<div class=\"foot\">", "<span class=\"walknav\" id=\"walknav\">",
        "<button class=\"iconbtn\" id=\"walk-back\" title=\"Back\" aria-label=\"Back\" onclick=\"dwightSend('walk:back')\"><svg class=\"ic\" aria-hidden=\"true\"><use href=\"#ic-up\"/>",
        "<button class=\"iconbtn\" id=\"walk-next\" title=\"Next\" aria-label=\"Next\" onclick=\"dwightSend('walk:next')\"><svg class=\"ic\" aria-hidden=\"true\"><use href=\"#ic-down\"/>",
        "<button class=\"iconbtn\" id=\"walk-ask\" title=\"Ask\" aria-label=\"Ask\" onclick=\"dwightSend('walk:ask')\"><svg class=\"ic\" aria-hidden=\"true\"><use href=\"#ic-chat\"/>",
        "<button class=\"iconbtn\" id=\"walk-leave\" title=\"Leave\" aria-label=\"Leave\" onclick=\"dwightSend('walk:leave')\"><svg class=\"ic\" aria-hidden=\"true\"><use href=\"#ic-close\"/>",
        "<span class=\"meta\" id=\"status\">", "id=\"askbtn\"", "id=\"copybtn\"");
      Assert.DoesNotContain("stepnav", shell);
      Assert.DoesNotContain("stepnav", Repo("Dwight", "Resources", "dwight-components.css"));

      // Hidden with no space held, shown for any live Walk, with or without the bar.
      var css = Repo("Dwight", "Resources", "dwight-components.css");
      Assert.Contains(".walknav{display:none;", css);
      Assert.Contains(".foot.walking .walknav{display:flex}", css);
      Assert.Contains(".foot.walking .meta{display:none}", css);   // at 340 wide the status would keep 24 points beside them, a clipped sliver
      Assert.Contains("document.querySelector('.foot').classList.toggle('walking',on);", shell);
      Assert.Contains("_walkShown(_walkLive);", shell);
      Assert.DoesNotContain("panelIsWalk){", shell);   // the bar being away decides only the gone line
      Assert.Contains("if(panelIsWalk&&gone){", shell);
    }

    [Fact]
    public void The_two_arrows_are_up_and_down_from_the_one_table_and_a_disabled_one_looks_it()
    {
      Assert.Contains("up: 'arrow-up', down: 'arrow-down',", Repo("tools", "build-icon-sprite.js"));
      var sprite = Repo("Dwight", "Resources", "dwight-icons.svg");
      Assert.Contains("<symbol id=\"ic-up\" viewBox=\"0 0 24 24\" data-tabler=\"arrow-up\">", sprite);
      Assert.Contains("<symbol id=\"ic-down\" viewBox=\"0 0 24 24\" data-tabler=\"arrow-down\">", sprite);

      var shell = Repo("Dwight", "Resources", "panel-shell.html");
      Assert.Contains("document.getElementById('walk-back').disabled=i<=0; document.getElementById('walk-next').disabled=i>=cards.length-1;", shell);

      var css = Repo("Dwight", "Resources", "dwight-components.css");
      Assert.Contains(".btn[disabled],.iconbtn[disabled],.field :disabled{opacity:var(--disabled-opacity);cursor:not-allowed}", css);
      Assert.Contains(".iconbtn[disabled]:hover{background:transparent;color:var(--subtle)}", css);   // Back on step 1 does not light up as if it would do something
    }

    [Fact]
    public void All_four_arrows_on_the_panel_page_go_to_the_same_dispatcher_the_bar_uses()
    {
      Assert.Contains("if(e.key==='ArrowLeft'||e.key==='ArrowRight'||e.key==='ArrowUp'||e.key==='ArrowDown'){ e.preventDefault(); dwightSend('walkkey:'+e.key); }", Repo("Dwight", "Resources", "panel-shell.html"));
      Assert.Contains("WalkSession.PageKey(cmd.Substring(8), false);", Repo("Dwight", "UI", "WebPanel.cs"));
      Assert.Equal(WalkKey.Next, WalkKeys.Dispatch(WalkKeys.CodeOf("ArrowDown"), false, true));
      Assert.Equal(WalkKey.Back, WalkKeys.Dispatch(WalkKeys.CodeOf("ArrowUp"), false, true));
    }

    /// <summary>Brief ES unit 2: the mode is Step by step (#1314), so Play steps on; the panel's Play and the bar's say it, at rest and when a pause ends.</summary>
    [Fact]
    public void The_Play_tooltip_says_it_steps_on_in_the_panel_and_in_the_bar()
    {
      const string play = "Play: step on by itself. Any key or click pauses it.";
      foreach (var shell in new[] { "panel-shell.html", "walk-bar.html" })
      {
        var s = Repo("Dwight", "Resources", shell);
        Assert.Equal(2, s.Split(new[] { play }, StringSplitOptions.None).Length - 1);   // the title at rest, and the one a pause gives back
        Assert.DoesNotContain("walk on by itself", s);
      }
    }

    [Fact]
    public void The_bars_controls_are_the_panels_icons_each_naming_its_key_and_its_switches_follow_the_dots()
    {
      // Brief EC unit 3 (Tharun: "the Next, Left, Right and Play arrow can all be icons, like the panel"; "Dim, Canvas and Unfocus toggles should also be there in the Focus mode, along with Close").
      var bar = Repo("Dwight", "Resources", "walk-bar.html");
      Assert.Contains("<button class=\"iconbtn\" id=\"walkback\" title=\"Back: Left arrow\" aria-label=\"Back\" onclick=\"dwightSend('back')\"><svg class=\"ic turned\" aria-hidden=\"true\"><use href=\"#ic-chev\"/></svg></button>", bar);
      Assert.Contains("<button class=\"iconbtn\" id=\"walknext\" title=\"Next: Right arrow\" aria-label=\"Next\" onclick=\"dwightSend('next')\"><svg class=\"ic\" aria-hidden=\"true\"><use href=\"#ic-chev\"/></svg></button>", bar);
      Assert.Contains("id=\"walkplay\" title=\"Play: step on by itself. Any key or click pauses it.\" aria-label=\"Play\"", bar);
      Assert.Contains("<use id=\"walkplay-ic\" href=\"#ic-play\"/>", bar);
      Assert.Contains("id=\"walkask\" title=\"Ask the chat about this step\" aria-label=\"Ask the chat about this step\"", bar);
      Assert.DoesNotContain(">Back</button>", bar);
      Assert.DoesNotContain("Ask about this step</button>", bar);
      Assert.Contains("title=\"" + FocusText.BarClose + "\"", bar);
      Assert.DoesNotContain("Leave the walk", bar);
      InOrder(bar, "id=\"walkdots\"", "id=\"walkfocus\"", "id=\"walkdim\"", "id=\"walklens\"", "id=\"walkclose\"");
      Assert.Contains(".walkbar .ic.turned{transform:rotate(180deg)}", Repo("Dwight", "Resources", "dwight-components.css"));
    }

    [Fact]
    public void The_bars_document_carries_its_switches()
    {
      var view = JsonNode.Parse(WalkText.View(1, 3, "x", null, focus: true, dim: false));
      Assert.True(view["focus"].GetValue<bool>());
      Assert.False(view["dim"].GetValue<bool>());
      Assert.Contains("_focus.Active, DwightSettings.Current.WalkDim, deck);", Repo("Dwight", "UI", "WalkSession.cs"));
    }

    [Fact]
    public void A_steps_framing_has_a_reason_it_can_state()
    {
      var nav = Repo("Dwight", "UI", "CanvasNav.cs");
      Assert.DoesNotContain("WalkStarted", nav);   // Brief DH: a Walk's arrival frames nothing, and a step's framing is always the person's click
      Assert.Contains("WalkGeometry.Frame(box.Value, canvas.Width, canvas.Height, bottomClear, rightClear)", nav);
      Assert.Contains("vp.Zoom = zoom;", nav);
    }
  }
}
