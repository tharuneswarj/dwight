using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dwight.UI;
using Xunit;

namespace Dwight.Session.Tests
{
  /// <summary>
  /// Brief DH (Tharun's ruling of 1 November 2026): THE PANEL IS THE WALK BY DEFAULT, AND FOCUS MODE IS THE OTHER WAY TO WALK. The state, the placement, the dim and the words are GH-free. The host's half, which needs
  /// Grasshopper and Rhino, is pinned by text at the end, and its Rhino rows are the eleventh gate's.
  /// </summary>
  public class FocusCoreTests
  {
    private static Guid G(int n) => new Guid(n, 0, 0, new byte[8]);

    private static WalkState Arrived(int steps) =>
      new WalkState(Enumerable.Range(0, steps).Select(i => (IReadOnlyList<Guid>)new[] { G(i + 1) }).ToList(), new[] { G(9) });

    private static OneWindowCore.Box Canvas(int w, int h = 800) => new OneWindowCore.Box(100, 50, w, h);

    private static bool Meet(OneWindowCore.Box a, OneWindowCore.Box b) =>
      a.X < b.X + b.W && b.X < a.X + a.W && a.Y < b.Y + b.H && b.Y < a.Y + a.H;

    // ---- the arrival law: no wash and no framing until the first step ---------------------------------------------------------------------------

    [Fact]
    public void A_walk_arrives_live_with_no_step()
    {
      var walk = Arrived(3);

      Assert.True(walk.Live);
      Assert.False(walk.Started);
      Assert.Equal(-1, walk.Index);
      Assert.Empty(walk.Current);
      Assert.Equal(new[] { G(9) }, walk.Source);
    }

    [Fact]
    public void Next_from_arrival_is_step_one()
    {
      var walk = Arrived(3);

      Assert.True(walk.Next());
      Assert.True(walk.Started);
      Assert.Equal(0, walk.Index);
      Assert.Equal(new[] { G(1) }, walk.Current);
    }

    [Fact]
    public void Back_does_nothing_at_arrival_and_nothing_goes_back_to_it()
    {
      var walk = Arrived(3);
      Assert.False(walk.Back());
      Assert.False(walk.Started);

      walk.Next();
      Assert.False(walk.Back());
      Assert.Equal(0, walk.Index);
      Assert.False(walk.GoTo(-1));
      Assert.Equal(0, walk.Index);
    }

    [Fact]
    public void A_step_card_goes_straight_to_its_step_from_arrival()
    {
      var walk = Arrived(3);

      Assert.True(walk.GoTo(2));
      Assert.True(walk.Started);
      Assert.Equal(new[] { G(3) }, walk.Current);
    }

    [Fact]
    public void A_walk_can_be_left_before_it_has_begun()
    {
      var walk = Arrived(3);

      Assert.True(walk.Leave());
      Assert.False(walk.Live);
      Assert.False(walk.Started);
      Assert.False(walk.Next());
      Assert.Empty(walk.Current);
    }

    [Fact]
    public void A_walk_that_has_left_is_not_on_a_step()
    {
      var walk = Arrived(3);
      walk.Next();
      walk.Leave();

      Assert.False(walk.Started);
      Assert.Empty(walk.Current);
    }

    /// <summary>Solve Replay met the arrival at this seam: Play from there held an empty step for a whole beat before anything moved. It is as plain an ask as Next.</summary>
    [Fact]
    public void Play_on_a_walk_that_has_only_arrived_goes_to_step_one_at_once()
    {
      var walk = Arrived(3);
      var play = new WalkPlay();

      Assert.True(play.Start(walk));
      Assert.True(play.Playing);
      Assert.Equal(0, walk.Index);
    }

    [Fact]
    public void A_walk_that_has_only_arrived_has_no_step_for_a_play_to_time()
    {
      Assert.Empty(Arrived(3).Current);
      Assert.Null(new WalkTimes(new List<TimeFact>()).StepLine(Arrived(3).Current));
    }

    // ---- the Focus state machine -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Focus_is_off_by_default()
    {
      var focus = new FocusState();

      Assert.False(focus.Wanted);
      Assert.False(focus.Active);
    }

    [Fact]
    public void On_during_a_walk_arranges_the_windows()
    {
      var focus = new FocusState();

      Assert.Equal(FocusDo.Engage, focus.Toggle(true));
      Assert.True(focus.Wanted);
      Assert.True(focus.Active);
    }

    [Fact]
    public void On_with_no_walk_is_remembered_and_moves_nothing()
    {
      var focus = new FocusState();

      Assert.Equal(FocusDo.Nothing, focus.Toggle(false));
      Assert.True(focus.Wanted);
      Assert.False(focus.Active);
      Assert.Equal(FocusDo.Engage, focus.StepShown());
      Assert.True(focus.Active);
    }

    [Fact]
    public void Off_puts_the_windows_back_and_stays_off()
    {
      var focus = new FocusState();
      focus.Toggle(true);

      Assert.Equal(FocusDo.Restore, focus.Toggle(true));
      Assert.False(focus.Wanted);
      Assert.False(focus.Active);
      Assert.Equal(FocusDo.Nothing, focus.StepShown());
      Assert.False(focus.Active);
    }

    [Fact]
    public void Off_when_nothing_is_arranged_has_nothing_to_put_back()
    {
      var focus = new FocusState();
      focus.Toggle(false);

      Assert.Equal(FocusDo.Nothing, focus.Toggle(false));
      Assert.False(focus.Wanted);
    }

    [Fact]
    public void A_step_arranges_nothing_twice()
    {
      var focus = new FocusState();
      focus.Toggle(true);

      Assert.Equal(FocusDo.Nothing, focus.StepShown());
      Assert.Equal(FocusDo.Nothing, focus.StepShown());
    }

    [Fact]
    public void A_step_with_focus_unwanted_arranges_nothing()
    {
      var focus = new FocusState();

      Assert.Equal(FocusDo.Nothing, focus.StepShown());
      Assert.False(focus.Active);
    }

    /// <summary>Esc, the bar's close and the Walk's own Leave all end the Walk, so they are this one row: Esc in Focus leaves the Walk and Focus together.</summary>
    [Fact]
    public void A_walk_ending_puts_the_windows_back()
    {
      var focus = new FocusState();
      focus.Toggle(true);

      Assert.Equal(FocusDo.Restore, focus.WalkEnded());
      Assert.False(focus.Active);
    }

    [Fact]
    public void A_walk_ending_twice_puts_them_back_once()
    {
      var focus = new FocusState();
      focus.Toggle(true);
      focus.WalkEnded();

      Assert.Equal(FocusDo.Nothing, focus.WalkEnded());
    }

    [Fact]
    public void A_walk_ending_with_focus_off_does_nothing()
    {
      Assert.Equal(FocusDo.Nothing, new FocusState().WalkEnded());
    }

    /// <summary>Leaving a Walk is not turning Focus off: the choice is remembered for the session, and the next Walk comes back to it on its first step.</summary>
    [Fact]
    public void The_choice_outlives_the_walk_and_the_next_one_comes_back_on_its_first_step()
    {
      var focus = new FocusState();
      focus.Toggle(true);
      focus.WalkEnded();

      Assert.True(focus.Wanted);
      Assert.False(focus.Active);
      Assert.Equal(FocusDo.Engage, focus.StepShown());
      Assert.True(focus.Active);
    }

    /// <summary>A second Explain starting while on: the run ends the first Walk, so the window comes back for it, and the second Walk returns to Focus when it reaches a step.</summary>
    [Fact]
    public void A_second_explain_starting_while_on()
    {
      var focus = new FocusState();
      focus.Toggle(true);

      Assert.Equal(FocusDo.Restore, focus.WalkEnded());   // DwightSession.Start's Drop
      Assert.Equal(FocusDo.Nothing, focus.WalkEnded());   // and the new page arriving finds nothing to end
      Assert.True(focus.Wanted);
      Assert.Equal(FocusDo.Engage, focus.StepShown());
    }

    [Fact]
    public void A_decline_turns_it_off_so_it_does_not_ask_again_on_every_step()
    {
      var focus = new FocusState();
      focus.Toggle(true);

      focus.Declined();

      Assert.False(focus.Wanted);
      Assert.False(focus.Active);
      Assert.Equal(FocusDo.Nothing, focus.StepShown());
      Assert.Equal(FocusDo.Nothing, focus.WalkEnded());
    }

    // ---- the surface: the panel is the Walk unless Focus is on ------------------------------------------------------------------------------------

    [Theory]
    [InlineData(1400)]
    [InlineData(600)]
    [InlineData(300)]
    public void With_focus_off_the_panel_is_the_walk_however_much_room_there_is(int w)
    {
      Assert.Equal(WalkSurface.Panel, WalkGeometry.SurfaceFor(false, true, Canvas(w), null));
    }

    [Fact]
    public void With_focus_off_nothing_needs_the_canvas_box()
    {
      Assert.Equal(WalkSurface.Panel, WalkGeometry.SurfaceFor(false, true, null, null));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_canvas_not_showing_is_hidden_either_way(bool focus)
    {
      Assert.Equal(WalkSurface.Hidden, WalkGeometry.SurfaceFor(focus, false, Canvas(1400), null));
    }

    [Fact]
    public void With_focus_on_the_bar_shows_where_it_fits_and_the_fallback_stays()
    {
      Assert.Equal(WalkSurface.Bar, WalkGeometry.SurfaceFor(true, true, Canvas(1400), null));
      Assert.Equal(WalkSurface.Panel, WalkGeometry.SurfaceFor(true, true, Canvas(300), null));
      Assert.Equal(WalkSurface.Hidden, WalkGeometry.SurfaceFor(true, true, null, null));
    }

    // ---- the placement, on rectangles -------------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_viewport_sits_in_the_canvass_top_right_corner()
    {
      var v = FocusGeometry.Viewport(Canvas(1600)).Value;

      Assert.Equal(100 + 1600 - v.W - OneWindowCore.EdgeMargin, v.X);
      Assert.Equal(50 + OneWindowCore.EdgeMargin, v.Y);
      Assert.Equal(480, v.W);   // three tenths of the canvas
      Assert.Equal(360, v.H);   // four by three
    }

    [Theory]
    [InlineData(600, FocusGeometry.ViewportMinW)]
    [InlineData(4000, FocusGeometry.ViewportMaxW)]
    public void The_viewport_is_kept_to_a_useful_width(int canvasW, int viewportW)
    {
      Assert.Equal(viewportW, FocusGeometry.Viewport(Canvas(canvasW)).Value.W);
    }

    /// <summary>THE LAW: the viewport is never over the bar and the bar never over the viewport, at any canvas and whatever height the bar asks for, with the chip anywhere it can be nudged.</summary>
    [Fact]
    public void The_viewport_and_the_bar_never_meet()
    {
      var read = 0;
      foreach (var w in Enumerable.Range(0, 60).Select(i => 300 + i * 60))
        foreach (var h in Enumerable.Range(0, 40).Select(i => 200 + i * 40))
          foreach (var nudge in new[] { 0, 75, 150 })
          {
            var canvas = Canvas(w, h);
            var chip = OneWindowCore.Chip(canvas, nudge);
            if (WalkGeometry.Surface(true, canvas, chip) != WalkSurface.Bar) continue;
            var viewport = FocusGeometry.Viewport(canvas);
            if (viewport == null) continue;

            foreach (var wantH in new[] { 0, WalkGeometry.BarMinH, 100, WalkGeometry.BarMaxH, 1000 })
            {
              read++;
              Assert.False(Meet(viewport.Value, WalkGeometry.Bar(canvas, wantH, chip)), canvas + " nudge " + nudge + " bar " + wantH);
            }
          }
      Assert.True(read > 1000, "the sweep read " + read + " placements");   // a sweep that skipped everything would prove nothing
    }

    [Fact]
    public void The_viewport_stays_on_the_canvas()
    {
      foreach (var w in Enumerable.Range(0, 60).Select(i => 300 + i * 60))
        foreach (var h in Enumerable.Range(0, 40).Select(i => 200 + i * 40))
        {
          var canvas = Canvas(w, h);
          if (!(FocusGeometry.Viewport(canvas) is OneWindowCore.Box v)) continue;
          Assert.True(v.X >= canvas.X && v.Y >= canvas.Y && v.X + v.W <= canvas.X + canvas.W && v.Y + v.H <= canvas.Y + canvas.H, canvas.ToString());
          Assert.True(v.H >= FocusGeometry.ViewportMinH, canvas.ToString());
          Assert.True(v.W >= FocusGeometry.ViewportMinW, canvas.ToString());
        }
    }

    [Theory]
    [InlineData(200, 800)]    // too narrow for one
    [InlineData(1400, 300)]   // too short once the bar has its room
    public void Where_no_viewport_fits_there_is_none(int w, int h)
    {
      Assert.Null(FocusGeometry.Viewport(Canvas(w, h)));
    }

    [Fact]
    public void A_short_canvas_gets_a_shorter_viewport_rather_than_one_under_the_bar()
    {
      var canvas = Canvas(1600, 400);
      var v = FocusGeometry.Viewport(canvas).Value;

      Assert.True(v.H < v.W * 3 / 4);
      Assert.True(v.Y + v.H <= canvas.Y + canvas.H - WalkGeometry.BarMaxH - OneWindowCore.EdgeMargin);
    }

    [Fact]
    public void Framing_keeps_a_step_clear_of_the_viewport()
    {
      var canvas = Canvas(1600);
      var v = FocusGeometry.Viewport(canvas);

      Assert.Equal(v.Value.W + OneWindowCore.EdgeMargin, FocusGeometry.RightClear(canvas, v));
      Assert.Equal(0, FocusGeometry.RightClear(canvas, null));
    }

    /// <summary>Incident #176: a window's box is logical points and Rhino's position is screen pixels, and they part at any scale but 100%.</summary>
    [Theory]
    [InlineData(96, 100, 50, 480, 360)]
    [InlineData(120, 125, 62, 600, 450)]   // 62.5 rounds to even, as OneWindowCore's own arithmetic does
    [InlineData(144, 150, 75, 720, 540)]
    [InlineData(0, 100, 50, 480, 360)]
    public void The_viewports_position_is_screen_pixels(double dpi, int x, int y, int w, int h)
    {
      var r = FocusGeometry.Physical(new OneWindowCore.Box(100, 50, 480, 360), dpi);

      Assert.Equal(new System.Drawing.Rectangle(x, y, w, h), r);
    }

    // ---- the decline: a number that is true ---------------------------------------------------------------------------------------------------------

    /// <summary>The sentence's number is what the bar needs and no other: one point narrower and it does not fit, at that width it does.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(75)]
    [InlineData(150)]
    public void The_number_in_the_sentence_is_the_one_the_bar_needs(int nudge)
    {
      var n = 0;
      for (var w = 200; w < 2000; w++)
      {
        var canvas = Canvas(w);
        if (WalkGeometry.Surface(true, canvas, OneWindowCore.Chip(canvas, nudge)) == WalkSurface.Bar) { n = w; break; }
      }

      Assert.Equal(n, FocusGeometry.NeededWidth(Canvas(n), OneWindowCore.Chip(Canvas(n), nudge)));
    }

    [Fact]
    public void With_no_chip_the_number_is_the_compass_corners()
    {
      var needed = FocusGeometry.NeededWidth(Canvas(800), null);

      Assert.Equal(WalkSurface.Bar, WalkGeometry.Surface(true, Canvas(needed), null));
      Assert.Equal(WalkSurface.Panel, WalkGeometry.Surface(true, Canvas(needed - 1), null));
    }

    [Fact]
    public void A_narrow_canvas_is_told_narrow_with_its_number()
    {
      var canvas = Canvas(500);

      Assert.Equal("Grasshopper's window is too narrow for the bar; Focus needs at least 548 points.", FocusText.Decline(canvas, OneWindowCore.Chip(canvas, 0)));
    }

    [Fact]
    public void A_short_canvas_is_told_short_and_not_sent_to_the_wrong_edge()
    {
      var canvas = Canvas(1400, 300);

      Assert.Equal("Grasshopper's window is too short for the bar; Focus needs at least " + WalkGeometry.BarMinCanvasH + " points of height.", FocusText.Decline(canvas, OneWindowCore.Chip(canvas, 0)));
    }

    // ---- the dim's law --------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_dim_is_on_by_default_in_the_settings_and_the_fixtures_show_the_default()
    {
      Assert.Contains("public bool WalkDim { get; set; } = true;", Repo("Dwight", "Config", "DwightSettings.cs"));
      Assert.Contains("walkDim: true,", Repo("tools", "ui-fixtures.js"));   // the render read "off" until this, because a fixture with no walkDim draws the switch as off
    }

    /// <summary>A canvas that records what it was asked to draw, in order.</summary>
    private sealed class FakeGlow : IWalkGlow
    {
      public readonly List<string> Calls = new List<string>();
      public void ShowStep(IReadOnlyList<Guid> step, bool wash) => Calls.Add("show " + string.Join(",", step.Select(g => g.ToString().Substring(0, 1))) + " wash=" + wash);
      public void SetWash(bool wash) => Calls.Add("wash=" + wash);
      public void HideStep() => Calls.Add("hide");
    }

    private static readonly Guid A = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000000"), B = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000000");

    /// <summary>Brief EC unit 2 (Tharun: "it needs me to go to the next one in order to start the dim"): the switch washes the step showing in the same call, twice over, and the step stays the same step.</summary>
    [Fact]
    public void A_change_to_the_dim_applies_to_the_step_showing_at_once()
    {
      var glow = new FakeGlow();
      var canvas = new WalkCanvas(glow);
      canvas.StepShown(new[] { A, B }, false);

      Assert.True(canvas.DimChanged(true));
      Assert.True(canvas.DimChanged(false));

      Assert.Equal(new[] { "show a,b wash=False", "wash=True", "wash=False" }, glow.Calls);
    }

    [Fact]
    public void The_dim_draws_nothing_with_no_step_and_the_next_step_reads_the_setting()
    {
      var glow = new FakeGlow();
      var canvas = new WalkCanvas(glow);
      Assert.False(canvas.DimChanged(true));   // a Walk that has only arrived has no step lit, and the canvas is the finished Explain's
      canvas.StepShown(new[] { A }, true);
      canvas.Ended();
      Assert.False(canvas.DimChanged(false));
      Assert.Equal(new[] { "show a wash=True" }, glow.Calls);
    }

    /// <summary>Brief EC unit 4 (Tharun: "when minimised, highlights and Dim should go away without closing the chat"): minimise takes the step off, restore puts the same step back with the dim as it is now.</summary>
    [Fact]
    public void Minimised_the_step_comes_off_the_canvas_and_restored_the_same_step_comes_back()
    {
      var glow = new FakeGlow();
      var canvas = new WalkCanvas(glow);
      canvas.StepShown(new[] { A, B }, true);

      Assert.True(canvas.WindowMinimised(focusActive: false));
      Assert.True(canvas.Hidden);
      Assert.False(canvas.DimChanged(false));   // the switch moved while minimised is read when the step comes back, not drawn over a canvas that shows nothing
      Assert.False(canvas.WindowMinimised(false));
      Assert.True(canvas.Relight(dim: false));
      Assert.False(canvas.Hidden);
      Assert.False(canvas.Relight(dim: false));   // a restore with nothing hidden draws nothing

      Assert.Equal(new[] { "show a,b wash=True", "hide", "show a,b wash=False" }, glow.Calls);
    }

    [Fact]
    public void Focus_own_chip_is_not_a_minimise_and_the_canvas_stays_lit()
    {
      var glow = new FakeGlow();
      var canvas = new WalkCanvas(glow);
      canvas.StepShown(new[] { A }, true);
      Assert.False(canvas.WindowMinimised(focusActive: true));
      Assert.False(canvas.Hidden);
      Assert.Equal(new[] { "show a wash=True" }, glow.Calls);
    }

    [Fact]
    public void The_walks_page_shown_again_relights_its_step_whatever_its_chat_lit()
    {
      var glow = new FakeGlow();
      var canvas = new WalkCanvas(glow);
      canvas.StepShown(new[] { B }, false);
      Assert.True(canvas.Relight(dim: true, force: true));
      Assert.Equal(new[] { "show b wash=False", "show b wash=True" }, glow.Calls);
    }

    [Fact]
    public void A_walk_that_has_ended_has_nothing_to_hide_or_relight()
    {
      var glow = new FakeGlow();
      var canvas = new WalkCanvas(glow);
      canvas.StepShown(new[] { A }, true);
      canvas.Ended();
      Assert.False(canvas.WindowMinimised(false));
      Assert.False(canvas.Relight(true, force: true));
      Assert.Single(glow.Calls);
    }

    // ---- Brief EC unit 1: the bar's X is Focus off, and the Walk stays ----------------------------------------------------------------------------------------

    [Fact]
    public void The_bars_close_turns_focus_off_and_leaves_the_walk_where_it_is()
    {
      var focus = new FocusState();
      Assert.Equal(FocusDo.Engage, focus.Toggle(walkLive: true));
      Assert.Equal(FocusDo.Restore, focus.BarClosed());
      Assert.False(focus.Wanted);
      Assert.False(focus.Active);
      Assert.Equal(FocusDo.Nothing, focus.BarClosed());   // a stale click on a bar that has gone does nothing
    }

    [Fact]
    public void Esc_in_focus_is_the_bars_close_and_out_of_focus_it_leaves_as_it_always_did()
    {
      Assert.Equal(WalkKey.FocusOff, WalkKeys.InFocus(WalkKey.Leave, focusActive: true));
      Assert.Equal(WalkKey.Leave, WalkKeys.InFocus(WalkKey.Leave, focusActive: false));
      Assert.Equal(WalkKey.Next, WalkKeys.InFocus(WalkKey.Next, focusActive: true));
      Assert.Equal(WalkKey.None, WalkPlay.WhilePlaying(WalkKey.Leave));   // while playing, Esc only pauses, in Focus too
    }

    // ---- the seams, by text (ADR-091): the Rhino rows are the eleventh gate's ----------------------------------------------------------------------------

    private static string Repo(params string[] parts)
    {
      var dir = AppContext.BaseDirectory;
      while (dir != null && !File.Exists(Path.Combine(dir, "Dwight.sln"))) dir = Path.GetDirectoryName(dir);
      Assert.NotNull(dir);
      return File.ReadAllText(Path.Combine(new[] { dir }.Concat(parts).ToArray())).Replace("\r\n", "\n");
    }

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
      Assert.True(b >= 0, to);
      return source.Substring(a, b - a);
    }

    private static string Session => Repo("Dwight", "UI", "WalkSession.cs");
    private static string Focus => Repo("Dwight", "UI", "WalkFocus.cs");

    [Fact]
    public void A_finished_explain_arrives_quietly_and_changes_nothing_on_the_canvas()
    {
      var finished = Between(Session, "public static void RunFinished(", "/// <summary>A step card or a dot.");

      InOrder(finished, "DwightDock.Host()?.IsFace(panel) != true) return;", "Arrive(panel);");
      Assert.Contains("_panel.MarkWalkArrived(_state.Count);", finished);
      foreach (var moves in new[] { "ShowWalkStep", "FrameStep", "CanvasNav", "Show(", "Step(", "Begin(" })
        Assert.DoesNotContain(moves, finished);
    }

    [Fact]
    public void A_replay_arrives_the_same_way_and_only_when_nothing_is_running()
    {
      Assert.Contains("if (panel == null || DwightSession.IsBusy) return;", Between(Session, "public static void Reopened(", "private static void Arrive("));
      InOrder(Repo("Dwight", "DwightSession.cs"), "public static void Reopen(DwightCommand cmd)", "Render(panel, cmd.Script, cmd.Live);", "Dwight.UI.WalkSession.Reopened(panel as Dwight.UI.WebPanel);", "CanvasGlow.Show(cmd.SourceGuids, thinking: false, owner: panel);");
    }

    [Fact]
    public void Every_step_is_shown_through_the_one_call_that_ends_the_walk_if_it_breaks()
    {
      var session = Session;
      Assert.Contains("if (_state.Next()) { _play.UserMoved(); Step(NavReason.UserClicked); }", session);
      Assert.Contains("if (IsLive && _state.Back()) { _play.UserMoved(); Step(NavReason.UserClicked); } }", session);
      Assert.Contains("if (_state.GoTo(i)) { _play.UserMoved(); Step(NavReason.UserClicked); }", session);
      InOrder(Between(session, "private static void Step(NavReason why)", "private static void Show("), "try { Show(why); }", "catch (Exception ex)", "Drop();");
    }

    /// <summary>The bar reported a canvas nobody can see, and that paused a playing Walk. With no bar by default, nothing would have.</summary>
    [Fact]
    public void A_walk_does_not_play_on_to_a_canvas_nobody_can_see_with_no_bar_to_say_so()
    {
      InOrder(Between(Session, "public static void Tick()", "private static void Begin("), "CheckWait();", "if (_play.Playing && !WalkBar.CanvasShowingNow()) Interrupt();", "if (_focus.Active) Replace();");
    }

    [Fact]
    public void A_played_step_breaking_ends_the_walk_too()
    {
      var session = Session;
      Assert.Contains("if (_play.Start(_state)) Step(NavReason.UserClicked); else Redraw();", session);
      Assert.Contains("if (_play.Advance(_state)) Step(NavReason.UserClicked); else Redraw();", session);
    }

    [Fact]
    public void A_redraw_makes_no_bar_with_focus_off()
    {
      Assert.Contains("try { if (IsLive && _focus.Active) WalkBar.Get()?.Present(ViewNow()); } catch { }", Between(Session, "private static void Redraw()", "/// <summary>A lesson stops on each card"));
    }

    [Fact]
    public void Arriving_tells_the_panel_whether_play_is_on_offer()
    {
      InOrder(Between(Session, "private static void Arrive(WebPanel panel)", "/// <summary>A step card or a dot."), "_panel.MarkWalkArrived(_state.Count);", "TellPanel();", "PushSwitches();");
    }

    [Fact]
    public void Everything_a_walk_started_stops_inside_the_try_so_the_windows_still_go_back()
    {
      InOrder(Between(Session, "private static bool End()", "/// <summary>Show the current step"),
        "try\n", "_play.Pause();", "_pace?.Stop();", "HookInput(false);", "_doc.SolutionEnd -= OnSolutionEnd;", "_panel.SetWalkTotal(null); _panel.SetWalkPlaying(false); _panel.SetWalkLine(null);", "finally\n", "WalkFocus.Restore();");
    }

    [Fact]
    public void Focus_engages_on_a_step_before_anything_reads_it()
    {
      InOrder(Between(Session, "private static void Show(NavReason why)", "/// <summary>The surfaces say the step"),
        "if (_focus.StepShown() == FocusDo.Engage) Engage();", "_onCanvas.StepShown(guids, DwightSettings.Current.WalkDim);", "Tell(why);", "PushSwitches();");
      // The step is drawn by the one sink, with its own marks and its page as the owner, so the dim switch and a restore wash and relight the Walk's glow and no other.
      var sink = Between(Session, "private sealed class Glow : IWalkGlow", "/// <summary>The chat opened from this Walk's step");
      Assert.Contains("CanvasGlow.ShowWalkStep(step, _panel?.WalkMarks(_state?.Index ?? -1), _panel, wash);", sink);
      Assert.Contains("CanvasGlow.SetWalkWash(wash, _panel);", sink);
      Assert.Contains("CanvasGlow.HideWalkStep();", sink);
    }

    [Fact]
    public void The_bar_is_only_ever_made_in_focus()
    {
      var session = Session;
      InOrder(Between(session, "private static WalkSurface Present()", "/// <summary>Arrange the windows"),
        "if (_focus.Active && !IsDeck) return WalkBar.Get()?.Present(ViewNow()) ?? WalkSurface.Panel;", "return WalkGeometry.SurfaceFor(false, WalkBar.CanvasShowingNow(), null, null);");
      Assert.Contains("try { WalkBar.Existing?.Dismiss(); } catch { }", session);
      Assert.Equal(3, session.Split(new[] { "WalkBar.Get()" }, StringSplitOptions.None).Length - 1);   // Present, Redraw and Replace, each only in Focus
      Assert.Contains("try { if (IsLive && _focus.Active) Replace(); } catch { }", session);
      Assert.Contains("if (_focus.Active) Replace();", Between(session, "public static void Tick()", "private static void Begin("));
    }

    [Fact]
    public void However_a_walk_ends_the_windows_go_back_in_a_finally_with_the_state_already_gone()
    {
      InOrder(Between(Session, "private static bool End()", "/// <summary>Show the current step"),
        "if (_state == null || !_state.Leave()) return false;", "try\n", "finally\n", "_state = null;", "if (_focus.WalkEnded() == FocusDo.Restore) WalkFocus.Restore();");
    }

    [Fact]
    public void Rhino_closing_ends_the_walk_so_the_windows_go_back()
    {
      Assert.Contains("RhinoApp.Closing += (s, e) => { try { Dwight.UI.WalkSession.Drop();", Repo("Dwight", "DwightPriority.cs"));
    }

    // ---- the view closed by hand (the twelfth gate) --------------------------------------------------------------------

    [Fact]
    public void Closing_the_view_by_hand_reads_as_focus_off_and_the_windows_go_back()
    {
      var focus = new FocusState();
      focus.Toggle(true);

      Assert.Equal(FocusDo.Restore, focus.ViewClosed());
      Assert.False(focus.Wanted);
      Assert.False(focus.Active);
    }

    [Fact]
    public void The_next_step_after_a_hand_close_does_not_open_another_view()
    {
      var focus = new FocusState();
      focus.Toggle(true);
      focus.ViewClosed();

      Assert.Equal(FocusDo.Nothing, focus.StepShown());
      Assert.False(focus.Active);
    }

    [Fact]
    public void The_switch_turns_focus_on_again_after_a_hand_close()
    {
      var focus = new FocusState();
      focus.Toggle(true);
      focus.ViewClosed();

      Assert.Equal(FocusDo.Engage, focus.Toggle(true));
      Assert.True(focus.Active);
    }

    [Fact]
    public void With_no_windows_arranged_there_is_no_view_to_close_and_the_choice_stands()
    {
      var focus = new FocusState();
      focus.Toggle(false);

      Assert.Equal(FocusDo.Nothing, focus.ViewClosed());
      Assert.True(focus.Wanted);
      Assert.Equal(FocusDo.Engage, focus.StepShown());
    }

    [Fact]
    public void The_tick_notices_the_view_is_gone_before_it_places_anything()
    {
      var tick = Between(Session, "public static void Tick()", "private static void FocusViewClosed()");

      InOrder(tick, "if (_focus.Active && WalkFocus.ViewClosedByHand()) FocusViewClosed();", "if (_focus.Active) Replace();");
    }

    [Fact]
    public void A_hand_close_goes_off_as_the_switch_would_and_the_walk_stays_on_its_step()
    {
      var closed = Between(Session, "private static void FocusViewClosed()", "private static void Begin(");

      InOrder(closed, "if (_focus.ViewClosed() != FocusDo.Restore) return;", "WalkFocus.Restore();", "if (_state?.Started == true) Tell(NavReason.UserClicked);", "PushSwitches();", "DwightToast.Show(FocusText.ViewClosed)");
      Assert.DoesNotContain("Interrupt(", closed);
      Assert.DoesNotContain("_play", closed);
    }

    [Fact]
    public void A_focus_that_never_opened_a_view_has_none_to_close_and_a_failed_read_says_no()
    {
      var read = Between(Focus, "public static bool ViewClosedByHand()", "/// <summary>Every window back");

      InOrder(read, "if (_prior == null || _viewId == Guid.Empty) return false;", "Views.Find(_viewId) == null", "catch { return false; }");
    }

    [Fact]
    public void Nothing_in_play_reads_the_view()
    {
      foreach (var part in new[]
      {
        Between(Session, "public static void TogglePlay()", "/// <summary>Any input pauses"),
        Between(Session, "private static void Arm()", "/// <summary>The lens's toggle moved"),
      })
      {
        Assert.DoesNotContain("WalkFocus", part);
        Assert.DoesNotContain("_focus", part);
      }
    }

    [Fact]
    public void The_control_starts_a_walk_that_has_only_arrived_and_off_keeps_the_walk_on_its_step()
    {
      var toggle = Between(Session, "public static void ToggleFocus()", "public static void BarClosed()");

      InOrder(toggle, "switch (_focus.Toggle(IsLive))", "case FocusDo.Engage:", "if (!_state.Started) _state.Next();", "Engage();", "Step(NavReason.UserClicked);",
        "case FocusDo.Restore:", "WalkFocus.Restore();", "Tell(NavReason.UserClicked);", "PushSwitches();");
      Assert.DoesNotContain("Drop(", toggle);
    }

    /// <summary>
    /// Brief EC unit 1 (Tharun: "close Focus Mode, and come back, in the panel the Focus and Dim option goes away"; Rhino 2 measured it: the X was Leave, and Leave ended the Walk). The bar's X, its
    /// Focus switch and Esc in Focus all take the panel's own Focus-off path: the windows back, the same step told to the panel, which shows its footer, the switches pushed, the keys back to the canvas.
    /// Nothing on that path ends the Walk.
    /// </summary>
    [Fact]
    public void The_bars_close_is_focus_off_and_the_walk_stays_on_its_step_in_the_panel()
    {
      var closed = Between(Session, "public static void BarClosed()", "/// <summary>The Dim switch, from the panel");
      InOrder(closed, "if (_focus.BarClosed() != FocusDo.Restore) { PushSwitches(); return; }", "WalkFocus.Restore();", "if (_state?.Started == true) Tell(NavReason.UserClicked);", "PushSwitches();", "DwightDock.GiveKeysToCanvas();");
      foreach (var ends in new[] { "End()", "Leave(", "MarkWalkStep(-1" }) Assert.DoesNotContain(ends, closed);
      Assert.Contains("_panel.MarkWalkStep(_state.Index, surface != WalkSurface.Bar, gone);", Session);   // Tell's Mark: a step at or above zero, so the panel shows its footer and its row

      var bar = Repo("Dwight", "UI", "WalkBar.cs");
      Assert.Contains("case \"close\": WalkSession.BarClosed(); break;", bar);
      Assert.Contains("case \"focus\": WalkSession.BarClosed(); break;", bar);
      Assert.DoesNotContain("Leave(", bar);
      Assert.Contains("case WalkKey.FocusOff: BarClosed(); break;", Session);
      Assert.Contains("switch (WalkKeys.InFocus(key, _focus.Active))", Session);
    }

    [Fact]
    public void The_switches_are_pushed_to_the_bar_and_the_panel_from_the_same_facts()
    {
      var push = Between(Session, "private static void PushSwitches()", "/// <summary>The bar's document for the current step.");
      Assert.Contains("_panel?.MarkWalkSwitches(_focus.Wanted, DwightSettings.Current.WalkDim);", push);
      Assert.Contains("WalkBar.Existing?.MarkSwitches(_focus.Active, DwightSettings.Current.WalkDim);", push);
    }

    [Fact]
    public void A_decline_turns_focus_off_and_is_said()
    {
      InOrder(Between(Session, "private static void Engage()", "/// <summary>The canvas or the one window moved"),
        "var arranged = WalkFocus.Engage(IsDeck, out var say);", "if (!arranged) _focus.Declined();", "DwightToast.Show(say);");
    }

    /// <summary>Brief EC unit 2: the switch saves the setting and washes the step showing, in the one call, through the canvas's hold; it moves no step and frames nothing.</summary>
    [Fact]
    public void The_dim_switch_washes_the_step_showing_and_moves_nothing()
    {
      var setDim = Between(Session, "public static void SetDim(bool on)", "/// <summary>\n    /// Brief EC unit 4");

      InOrder(setDim, "settings.WalkDim = on;", "settings.Save();", "if (IsLive) _onCanvas.DimChanged(on);", "PushSwitches();");
      foreach (var moves in new[] { "Show(", "Step(", "Tell(", "FrameStep" })
        Assert.DoesNotContain(moves, setDim);
    }

    /// <summary>Two doors set the wash: a step, and the dim switch on the step showing, which only the Walk's own glow takes. Every other way of setting a glow clears it.</summary>
    [Fact]
    public void Only_a_step_and_the_dim_switch_set_the_wash()
    {
      var glow = Repo("Dwight", "UI", "CanvasGlow.cs");

      Assert.Contains("_walkWash = wash;", Between(glow, "public static void ShowWalkStep(", "public static void SetWalkWash("));
      var set = Between(glow, "public static void SetWalkWash(", "public static void HideWalkStep(");
      InOrder(set, "if (!ReferenceEquals(owner, _owner)) return;", "_walkWash = wash;");
      Assert.Equal(2, glow.Split(new[] { "_walkWash = wash;" }, StringSplitOptions.None).Length - 1);
      Assert.DoesNotContain("_walkWash = true", glow);
    }

    [Fact]
    public void Turning_focus_off_does_not_redraw_the_step()
    {
      Assert.DoesNotContain("ShowWalkStep", Between(Session, "private static void Tell(NavReason why)", "/// <summary>The bar only in Focus."));
    }

    /// <summary>Brief EC unit 4: the window's minimise is the one place the step comes off, Focus's own collapse excepted; the restore relights it; while it is off, the canvas's keys are Grasshopper's.</summary>
    [Fact]
    public void Minimise_takes_the_step_off_and_restore_puts_it_back_and_the_keys_go_to_grasshopper_meanwhile()
    {
      var dock = Repo("Dwight", "UI", "DwightDock.cs");
      var apply = Between(dock, "private void Apply(WindowStep step)", "private void ShowViews()");
      InOrder(apply, "if (_expanded && !step.Open) WalkSession.WindowMinimised();", "var restoring = !_expanded && step.Open;", "if (restoring) WalkSession.WindowRestored();");

      var minimised = Between(Session, "public static void WindowMinimised()", "public static void WindowRestored()");
      InOrder(minimised, "if (!IsLive || !_state.Started) return;", "if (_focus.Active) return;", "Interrupt();", "_onCanvas.WindowMinimised(false);");
      var restored = Between(Session, "public static void WindowRestored()", "/// <summary>Brief EC unit 5b: the Walk's page is in front again");
      Assert.Contains("if (_onCanvas.Relight(DwightSettings.Current.WalkDim)) UpdateCover();", restored);
      foreach (var moves in new[] { "FrameStep", "Tell(", "Step(", "Engage" }) Assert.DoesNotContain(moves, restored);

      Assert.Contains("if (_onCanvas.Hidden) return;", Between(Session, "private static void OnCanvasKey(", "var key = WalkKeys.Dispatch("));

      // The step comes off by emptying what is drawn; the owner, the badges and the lens's state stay for the restore.
      var hide = Between(Repo("Dwight", "UI", "CanvasGlow.cs"), "public static void HideWalkStep()", "/// <summary>The Walk has left");
      foreach (var keeps in new[] { "_labels", "_owner", "TreeLens" }) Assert.DoesNotContain(keeps, hide);
    }

    [Fact]
    public void Engaging_collapses_then_maximises_then_measures_and_a_misfit_goes_back()
    {
      // Brief EO unit 6: in Learn the panel stays where it is as the card and there is no bar, so the window is not collapsed or moved and the bar's room is not asked for.
      InOrder(Between(Focus, "public static bool Engage(bool keepPanel, out string say)", "private static bool OpenViewport("),
        "_prior = new Prior", "if (!keepPanel) DwightDock.Host()?.Collapse();", "editor.WindowState = FormWindowState.Maximized;", "if (!keepPanel) DwightDock.Host()?.FollowCanvas();", "var canvas = DwightDock.CanvasLogical();",
        "if (keepPanel && canvas is OneWindowCore.Box kept)", "if (!OpenViewport(kept, editor)) say = FocusText.NoViewport;", "return true;",
        "WalkGeometry.Surface(WalkBar.CanvasShowingNow(), box, chip) != WalkSurface.Bar", "FocusText.Decline(", "Restore();", "return false;",
        "if (!OpenViewport(box, editor)) say = FocusText.NoViewport;", "return true;", "catch (Exception ex)", "Restore();", "return false;");
    }

    [Fact]
    public void What_it_remembers_is_the_editors_own_state_and_its_normal_bounds()
    {
      Assert.Contains("State = editor.WindowState,", Focus);
      Assert.Contains("Bounds = editor.WindowState == FormWindowState.Normal ? editor.Bounds : editor.RestoreBounds,", Focus);
      Assert.Contains("Active = RhinoDoc.ActiveDoc?.Views.ActiveView,", Focus);
    }

    [Fact]
    public void The_viewport_is_a_floating_view_at_the_placement_functions_place_owned_by_the_editor()
    {
      var open = Between(Focus, "private static bool OpenViewport(", "/// <summary>The canvas moved");

      InOrder(open, "_viewBox = FocusGeometry.Viewport(canvas);", "doc.Views.Add(ViewTitle, DefinedViewportProjection.Perspective, FocusGeometry.Physical(_viewBox.Value, Dpi()), true);",
        "_viewId = view.MainViewport.Id;", "PushViewInfo(new ViewInfo(from), false);", "NativeWindowEffects.RootOf(view.Handle)", "NativeWindowEffects.SetOwner(_viewRoot, editor.Handle);", "DwightDock.GiveKeysToCanvas();");
    }

    [Fact]
    public void Focus_edits_nothing_in_the_definition_or_the_rhino_document()
    {
      foreach (var edits in new[] { "Objects.Add", "ExpireSolution", "NewSolution", "AddObject", "RemoveObject", "UndoUtil" })
        Assert.DoesNotContain(edits, Focus);
    }

    [Fact]
    public void The_restore_takes_each_step_on_its_own_and_runs_once()
    {
      InOrder(Between(Focus, "public static void Restore()", "private static void CloseViewport("),
        "var prior = _prior;", "_prior = null;", "if (prior == null) return;",
        "try { CloseViewport(prior); } catch { }", "editor.WindowState = prior.State;", "if (prior.State == FormWindowState.Normal && !prior.Bounds.IsEmpty) editor.Bounds = prior.Bounds;",
        "catch { }", "try { WalkBar.Existing?.Dismiss(); } catch { }", "try { DwightDock.Host()?.OpenIfClosed(); } catch { }");
    }

    [Fact]
    public void A_viewport_the_person_already_closed_is_simply_not_found()
    {
      InOrder(Between(Focus, "private static void CloseViewport(", "private static double Dpi()"), "doc?.Views.Find(id);", "view?.Close();", "doc.Views.ActiveView = prior.Active;");
    }

    [Fact]
    public void The_viewport_follows_the_canvas_only_when_its_place_changes()
    {
      var place = Between(Focus, "public static void Place()", "/// <summary>Every window back");

      InOrder(place, "FocusGeometry.Viewport(canvas.Value)", "box.Value.ToString() == _viewBox.Value.ToString()) return;", "NativeWindowEffects.MoveTo(_viewRoot, FocusGeometry.Physical(box.Value, Dpi()))");
    }

    [Fact]
    public void Framing_clears_whatever_the_window_and_the_viewport_take()
    {
      Assert.Contains("Math.Max(window, WalkFocus.RightClear(box.Value, scale))", Repo("Dwight", "UI", "WalkBar.cs"));
      Assert.Contains("_prior == null ? 0f : FocusGeometry.RightClear(canvas, _viewBox) * scale;", Focus);
    }

    [Fact]
    public void The_glow_is_the_walks_only_once_it_is_on_a_step()
    {
      Assert.Contains("public static bool OnAStep(WebPanel panel) => Owns(panel) && _state.Started;", Session);
    }

    [Fact]
    public void Asking_needs_a_step()
    {
      Assert.Contains("if (_state?.Started != true || _panel == null) return;", Between(Session, "public static void Ask()", "/// <summary>The surface moved"));
    }

    [Fact]
    public void The_panel_controls_reach_the_same_calls()
    {
      var panel = Repo("Dwight", "UI", "WebPanel.cs");

      Assert.Contains("if (cmd == \"walk:focus\") { DwightCanvasPopup.Safe(WalkSession.ToggleFocus); return; }", panel);
      Assert.Contains("if (cmd == \"walk:dim:on\" || cmd == \"walk:dim:off\") { WalkSession.SetDim(cmd == \"walk:dim:on\"); return; }", panel);
    }

    [Fact]
    public void Settings_has_the_switch_under_explain_and_saves_it()
    {
      var settings = Repo("Dwight", "UI", "DwightSettingsPanel.cs");

      Assert.Contains("[\"walkDim\"] = s.WalkDim,", settings);
      Assert.Contains("if (v == \"on\" || v == \"off\") WalkSession.SetDim(v == \"on\");", settings);   // Brief EC unit 2: the same door as the panel's and the bar's, so a Walk on a step takes it at once
      var html = Repo("Dwight", "Resources", "settings.html");
      InOrder(html, "data-pane=\"Explain\"", "id=\"walkdim\"", "data-pane=\"Build\"");
      Assert.DoesNotContain("takes effect on the next step", html);
    }

    [Fact]
    public void The_footers_own_row_stays_where_it_was_and_the_count_and_switches_sit_above_it()
    {
      InOrder(Repo("Dwight", "Resources", "panel-shell.html"), "<div class=\"walkrow\" id=\"walkrow\">", "<span class=\"walkcount\" id=\"walk-count\">", "id=\"walk-focus\" role=\"switch\"", "id=\"walk-dim\" role=\"switch\"", "<div class=\"foot\">",
        "id=\"walk-back\"", "id=\"walk-next\"", "id=\"walk-ask\"", "id=\"walk-leave\"", "id=\"askbtn\"", "id=\"copybtn\"");
    }

    /// <summary>A render caught both: ".walkopts" was already Learn's, whose later rule kept the row showing after a Walk left, and an icon button's display beat the hidden attribute.</summary>
    [Fact]
    public void The_row_hides_when_the_walk_leaves_and_the_title_bars_focus_with_it()
    {
      var css = Repo("Dwight", "Resources", "dwight-components.css");

      Assert.Equal(1, css.Split(new[] { "\n.walkrow{" }, StringSplitOptions.None).Length - 1);
      Assert.Contains(".walkrow{display:none;", css);
      Assert.Contains(".walkrow.on{display:flex}", css);
      Assert.Contains(".iconbtn[hidden]{display:none}", css);
      InOrder(css, ".iconbtn{", ".iconbtn[hidden]{display:none}");
    }
  }
}
