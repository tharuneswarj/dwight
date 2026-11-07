using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Dwight.UI;
using Xunit;

namespace Dwight.Session.Tests
{
  /// <summary>
  /// Brief DD: Solve Replay is a Walk that plays itself, with the solve's time under each step. The pace, the play, the times and every sentence are pure and tested here; the session, the bar and the paint are
  /// bound to Grasshopper and pinned by their text, and the Rhino rows are the architect's. No model call in this unit, and a test holds that too.
  /// </summary>
  public class SolveReplayTests
  {
    private static readonly Guid A = Guid.NewGuid(), B = Guid.NewGuid(), C = Guid.NewGuid(), D = Guid.NewGuid();

    private static WalkState Walk(int steps)
    {
      var list = Enumerable.Range(0, steps).Select(_ => (IReadOnlyList<Guid>)new List<Guid> { Guid.NewGuid() }).ToList();
      var walk = new WalkState(list, new List<Guid>());
      walk.GoTo(0);   // on step 1; a Walk that has only arrived is Brief DH's, and has its own row below
      return walk;
    }

    private static TimeFact Ran(Guid id, string label, double ms) => new TimeFact { Guid = id, Label = label, Recorded = true, Ms = ms };

    private static TimeFact Never(Guid id, string label) => new TimeFact { Guid = id, Label = label, Recorded = false, Ms = 0 };

    // ---- the pace --------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_pace_is_bounded_between_three_and_nine_seconds()
    {
      Assert.Equal(3, WalkPace.MinSeconds);
      Assert.Equal(9, WalkPace.MaxSeconds);
      Assert.Equal(3, WalkPace.Seconds(0, 0));
      Assert.Equal(3, WalkPace.Seconds(-5, -5));
      Assert.Equal(9, WalkPace.Seconds(500, 50_000));
      foreach (var components in new[] { 0, 1, 3, 7, 20, 100 })
        foreach (var chars in new[] { 0, 40, 120, 400, 2000 })
          Assert.InRange(WalkPace.Seconds(components, chars), WalkPace.MinSeconds, WalkPace.MaxSeconds);
    }

    /// <summary>Pinned on the numbers: 4 beats, one a component, one per 10 characters, at half a second a beat.</summary>
    [Theory]
    [InlineData(1, 20, 3.5)]
    [InlineData(3, 40, 5.5)]
    [InlineData(5, 60, 7.5)]
    public void The_pace_is_its_rule(int components, int chars, double seconds) => Assert.Equal(seconds, WalkPace.Seconds(components, chars), 6);

    [Fact]
    public void A_longer_step_never_holds_for_less()
    {
      for (var c = 0; c < 40; c++) Assert.True(WalkPace.Seconds(c + 1, 30) >= WalkPace.Seconds(c, 30));
      for (var n = 0; n < 400; n += 5) Assert.True(WalkPace.Seconds(3, n + 5) >= WalkPace.Seconds(3, n));
      Assert.True(WalkPace.Seconds(4, 30) > WalkPace.Seconds(3, 30));
      Assert.True(WalkPace.Seconds(3, 60) > WalkPace.Seconds(3, 30));
    }

    [Fact]
    public void The_beat_is_the_systems_named_duration()
    {
      Assert.Contains("--dur-slow:" + WalkPace.BeatMs + "ms", Repo("Dwight", "Resources", "dwight-tokens.css"));
    }

    // ---- the play --------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Play_reaches_the_end_and_stops_there_and_says_so()
    {
      var walk = Walk(3);
      var play = new WalkPlay();

      Assert.False(play.Start(walk));
      Assert.True(play.Playing);
      Assert.False(play.Ended);

      Assert.True(play.Advance(walk));
      Assert.Equal(1, walk.Index);
      Assert.True(play.Playing);

      Assert.True(play.Advance(walk));
      Assert.Equal(2, walk.Index);
      Assert.False(play.Playing);
      Assert.True(play.Ended);

      Assert.False(play.Advance(walk));
      Assert.Equal(2, walk.Index);
    }

    [Fact]
    public void Back_and_Next_still_work_at_the_end()
    {
      var walk = Walk(3);
      var play = new WalkPlay();
      play.Start(walk);
      play.Advance(walk);
      play.Advance(walk);

      Assert.True(walk.Back());
      play.UserMoved();
      Assert.Equal(1, walk.Index);
      Assert.False(play.Ended);
      Assert.False(play.Playing);
    }

    [Fact]
    public void Play_at_the_end_starts_again_from_the_first_step()
    {
      var walk = Walk(3);
      walk.GoTo(2);
      var play = new WalkPlay();

      Assert.True(play.Start(walk));
      Assert.Equal(0, walk.Index);
      Assert.True(play.Playing);
      Assert.False(play.Ended);
    }

    /// <summary>A play that ran out and is played again is no longer at its end, and neither is one the person stepped back from.</summary>
    [Fact]
    public void Playing_again_after_the_end_is_no_longer_ended()
    {
      var walk = Walk(2);
      var play = new WalkPlay();
      play.Start(walk);
      play.Advance(walk);
      Assert.True(play.Ended);

      Assert.True(play.Start(walk));
      Assert.False(play.Ended);
      Assert.True(play.Playing);
      Assert.Equal(0, walk.Index);
    }

    [Fact]
    public void Play_starts_from_where_the_person_is()
    {
      var walk = Walk(5);
      walk.GoTo(2);
      var play = new WalkPlay();

      Assert.False(play.Start(walk));
      Assert.Equal(2, walk.Index);
      Assert.True(play.Advance(walk));
      Assert.Equal(3, walk.Index);
    }

    [Fact]
    public void A_walk_of_one_step_has_nowhere_to_go()
    {
      var walk = Walk(1);
      var play = new WalkPlay();

      Assert.False(play.Start(walk));
      Assert.False(play.Playing);
      Assert.True(play.Ended);
    }

    [Fact]
    public void A_walk_that_is_not_live_does_not_play()
    {
      var walk = Walk(3);
      walk.Leave();
      var play = new WalkPlay();

      Assert.False(play.Start(walk));
      Assert.False(play.Playing);
      Assert.False(play.Advance(walk));
      Assert.False(new WalkPlay().Start(null));
    }

    /// <summary>A Teaching step waits for its answer, so a lesson has no Play, or a timer would walk past the question.</summary>
    [Fact]
    public void A_lesson_has_no_play()
    {
      Assert.True(WalkPlay.CanPlay(5, lesson: false));
      Assert.False(WalkPlay.CanPlay(5, lesson: true));
      Assert.False(WalkPlay.CanPlay(1, lesson: false));
      Assert.Contains("if (!CanPlay()) return;", Session);
      Assert.Contains("private static bool CanPlay() => WalkPlay.CanPlay(_state.Count, _panel?.Deck != null);", Session);   // Brief EI: a deck waits on each card
      Assert.Contains("if(can===false) p.disabled=true;", Repo("Dwight", "Resources", "panel-shell.html"));
      Assert.False(JsonNode.Parse(WalkText.View(0, 3, "x", null, canPlay: false))["canplay"].GetValue<bool>());
    }

    [Fact]
    public void Any_input_pauses_and_says_whether_it_was_playing()
    {
      var walk = Walk(3);
      var play = new WalkPlay();

      Assert.False(play.Pause());
      play.Start(walk);
      Assert.True(play.Pause());
      Assert.False(play.Playing);
      Assert.False(play.Pause());

      // A timer that fires a moment after the pause moves nothing.
      Assert.False(play.Advance(walk));
      Assert.Equal(0, walk.Index);
    }

    /// <summary>Esc during play pauses, and the next Esc leaves. An arrow is what the person asked for, so it moves as well.</summary>
    [Fact]
    public void Esc_during_play_only_pauses_and_an_arrow_still_moves()
    {
      Assert.Equal(WalkKey.None, WalkPlay.WhilePlaying(WalkKey.Leave));
      Assert.Equal(WalkKey.Next, WalkPlay.WhilePlaying(WalkKey.Next));
      Assert.Equal(WalkKey.Back, WalkPlay.WhilePlaying(WalkKey.Back));
      Assert.Equal(WalkKey.None, WalkPlay.WhilePlaying(WalkKey.None));
    }

    // ---- what the bar is told --------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_view_carries_the_play_and_the_line_of_times()
    {
      var view = JsonNode.Parse(WalkText.View(2, 6, "x", null, playing: true, ended: false, time: "Slowest here."));
      Assert.True(view["playing"].GetValue<bool>());
      Assert.Equal("Slowest here.", view["time"].GetValue<string>());
      Assert.Equal("Step 3 of 6", view["label"].GetValue<string>());
    }

    [Fact]
    public void The_view_says_the_end_of_the_walk_where_the_step_number_is()
    {
      var view = JsonNode.Parse(WalkText.View(5, 6, "x", null, playing: false, ended: true));
      Assert.Equal("Step 6 of 6. End of the steps", view["label"].GetValue<string>());
      Assert.False(view["playing"].GetValue<bool>());
      Assert.Null(view["time"]);
    }

    [Fact]
    public void A_view_without_them_reads_as_it_did()
    {
      var view = JsonNode.Parse(WalkText.View(0, 2, "x", null));
      Assert.Equal("Step 1 of 2", view["label"].GetValue<string>());
      Assert.False(view["playing"].GetValue<bool>());
      Assert.Null(view["time"]);
    }

    [Fact]
    public void The_panel_says_the_end_of_the_walk_before_the_times()
    {
      Assert.Equal("End of the steps. Slowest here.", WalkText.PanelLine(true, "Slowest here."));
      Assert.Equal("End of the steps.", WalkText.PanelLine(true, null));
      Assert.Equal("Slowest here.", WalkText.PanelLine(false, "Slowest here."));
      Assert.Null(WalkText.PanelLine(false, ""));
    }

    // ---- the shares ------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_longest_in_the_selection_sets_the_full_width()
    {
      var t = new WalkTimes(new[] { Ran(A, "Remap", 48), Ran(B, "Move", 12), Ran(C, "Area", 1) });

      Assert.Equal(1.0, t.Fraction(A), 6);
      Assert.Equal(0.25, t.Fraction(B), 6);
      Assert.Equal(1.0 / 48, t.Fraction(C), 6);
      Assert.Equal(61, t.TotalMs, 6);
      Assert.Equal(A, t.Slowest.Guid);
    }

    [Fact]
    public void One_component_with_all_the_time_fills_its_bar_and_the_rest_have_none()
    {
      var t = new WalkTimes(new[] { Ran(A, "Remap", 200), Ran(B, "Move", 0), Ran(C, "Area", 0) });
      Assert.Equal(1.0, t.Fraction(A), 6);
      Assert.Equal(0, t.Fraction(B));
      Assert.Equal(200, t.TotalMs, 6);
    }

    [Fact]
    public void All_zero_has_no_slowest_and_no_share_and_never_divides_by_zero()
    {
      var t = new WalkTimes(new[] { Ran(A, "Remap", 0), Ran(B, "Move", 0) });
      Assert.Null(t.Slowest);
      Assert.Equal(0, t.Fraction(A));
      Assert.Equal(0, t.TotalMs);
    }

    [Fact]
    public void A_component_not_read_has_no_share()
    {
      var t = new WalkTimes(new[] { Ran(A, "Remap", 10) });
      Assert.Equal(0, t.Fraction(D));
      Assert.Null(t.Of(D));
    }

    /// <summary>A reading under a millisecond is not time anyone can measure, and a never-ran component's zero is not time at all.</summary>
    [Fact]
    public void Under_a_millisecond_and_never_ran_are_not_measured()
    {
      var t = new WalkTimes(new[] { Ran(A, "Remap", 0.4), Never(B, "Loft"), Ran(C, "Move", 2) });
      Assert.False(WalkTimes.Measured(t.Of(A)));
      Assert.False(WalkTimes.Measured(t.Of(B)));
      Assert.True(WalkTimes.Measured(t.Of(C)));
      Assert.Equal(2, t.TotalMs, 6);
      Assert.Equal(0, t.Fraction(A));
    }

    [Fact]
    public void A_never_ran_reading_with_a_number_on_it_is_still_never_ran()
    {
      var t = new WalkTimes(new[] { new TimeFact { Guid = A, Label = "Stale", Recorded = false, Ms = 500 }, Ran(B, "Move", 2) });
      Assert.Equal(B, t.Slowest.Guid);
      Assert.Equal(2, t.TotalMs, 6);
      Assert.Equal(0, t.Fraction(A));
    }

    [Fact]
    public void A_tie_is_settled_the_same_way_every_time()
    {
      var low = new Guid("00000000-0000-0000-0000-000000000001");
      var high = new Guid("00000000-0000-0000-0000-000000000002");
      Assert.Equal(low, new WalkTimes(new[] { Ran(high, "H", 5), Ran(low, "L", 5) }).Slowest.Guid);
      Assert.Equal(low, new WalkTimes(new[] { Ran(low, "L", 5), Ran(high, "H", 5) }).Slowest.Guid);
    }

    [Fact]
    public void Nothing_read_is_nothing_and_never_a_crash()
    {
      var t = new WalkTimes(null);
      Assert.Null(t.Slowest);
      Assert.Null(t.SummaryLine());
      Assert.Null(t.StepLine(null));
      Assert.Null(t.StepLine(new[] { A }));
      Assert.Null(new WalkTimes(new TimeFact[] { null }).Slowest);
    }

    // ---- the sentences ---------------------------------------------------------------------------------------------------------------------------

    private static WalkTimes Selection() => new WalkTimes(new[] { Ran(A, "Remap (12)", 48), Ran(B, "Move (3)", 13), Ran(C, "Area (4)", 0), Never(D, "Loft (9)") });

    [Fact]
    public void A_step_with_measurable_time_names_its_slowest_against_the_selection()
    {
      Assert.Equal("Slowest here: Remap (12), 48 ms of the selection's 61 ms.", Selection().StepLine(new[] { A, B }));
      Assert.Equal("Slowest here: Move (3), 13 ms of the selection's 61 ms.", Selection().StepLine(new[] { B, C }));
    }

    [Fact]
    public void A_step_where_nothing_takes_measurable_time_says_so()
    {
      Assert.Equal("Nothing here takes measurable time.", Selection().StepLine(new[] { C }));
    }

    [Fact]
    public void A_step_that_never_ran_says_never_ran_and_not_that_it_is_fast()
    {
      Assert.Equal("Never ran: Loft (9).", Selection().StepLine(new[] { D }));
      Assert.DoesNotContain("measurable", Selection().StepLine(new[] { D }));
    }

    [Fact]
    public void Never_ran_is_said_beside_either_of_the_others()
    {
      Assert.Equal("Slowest here: Remap (12), 48 ms of the selection's 61 ms. Never ran: Loft (9).", Selection().StepLine(new[] { A, D }));
      Assert.Equal("Nothing here takes measurable time. Never ran: Loft (9).", Selection().StepLine(new[] { C, D }));
    }

    [Fact]
    public void Past_three_the_never_ran_are_counted()
    {
      var ids = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToList();
      var t = new WalkTimes(ids.Select((g, i) => Never(g, "N" + i)));
      Assert.Equal("Never ran: N0, N1, N2 and 2 more.", t.StepLine(ids));
      Assert.Equal("Never ran: N0, N1, N2.", t.StepLine(ids.Take(3)));
    }

    [Fact]
    public void A_big_number_reads_the_same_on_every_machine()
    {
      var t = new WalkTimes(new[] { Ran(A, "Solver", 1234.4) });
      Assert.Equal("Slowest here: Solver, 1,234 ms of the selection's 1,234 ms.", t.StepLine(new[] { A }));
    }

    /// <summary>U23: the figures are each component's last time, and do not add up to a solve. So the word "solve" is not on the total.</summary>
    [Fact]
    public void The_total_is_the_selections_and_never_called_a_solves()
    {
      Assert.Equal("This selection's components last took 61 ms. Slowest: Remap (12), 48 ms. 1 component has never run.", Selection().SummaryLine());
      Assert.DoesNotContain("solve", Selection().SummaryLine());
      Assert.DoesNotContain("solve", Selection().StepLine(new[] { A }));
    }

    [Fact]
    public void The_summary_says_each_case()
    {
      Assert.Equal("This selection's components last took 5 ms. Slowest: Move, 5 ms.", new WalkTimes(new[] { Ran(A, "Move", 5) }).SummaryLine());
      Assert.Equal("Nothing in this selection takes measurable time.", new WalkTimes(new[] { Ran(A, "Move", 0) }).SummaryLine());
      Assert.Equal("Nothing in this selection takes measurable time. 2 components have never run.", new WalkTimes(new[] { Ran(A, "Move", 0), Never(B, "x"), Never(C, "y") }).SummaryLine());
      Assert.Equal("Nothing in this selection has run yet.", new WalkTimes(new[] { Never(A, "x") }).SummaryLine());
    }

    // ---- a settled solve only --------------------------------------------------------------------------------------------------------------------

    private static readonly DateTime Clock = new DateTime(2026, 11, 1, 13, 16, 40);

    [Fact]
    public void The_line_says_when_it_was_read_and_not_that_everything_computed_then()
    {
      Assert.Equal("Slowest here: Remap (12), 48 ms of the selection's 61 ms. Read at 13:16, after the last solve.", WalkTimeLine.For(Selection(), new[] { A }, Clock, false, false));
      Assert.Equal("Read at 13:16, after the last solve.", WalkTimeLine.Clock(Clock));
    }

    [Fact]
    public void While_a_solve_runs_the_line_says_it_is_waiting_and_there_are_no_times()
    {
      Assert.Equal("Waiting for the solve to settle.", WalkTimeLine.For(null, new[] { A }, null, waiting: true, gaveUp: false));
    }

    /// <summary>U47, U48: a wait inside a solution must have a clock.</summary>
    [Fact]
    public void The_wait_has_a_clock_and_says_when_it_ran_out()
    {
      var since = new DateTime(2026, 11, 1, 7, 0, 0, DateTimeKind.Utc);
      Assert.False(WalkTimeLine.WaitIsOver(since, since.AddSeconds(WalkTimeLine.MaxWaitSeconds - 1)));
      Assert.True(WalkTimeLine.WaitIsOver(since, since.AddSeconds(WalkTimeLine.MaxWaitSeconds)));
      Assert.Equal(30, WalkTimeLine.MaxWaitSeconds);
      Assert.Equal("The solve has not settled, so there are no times yet.", WalkTimeLine.For(null, new[] { A }, null, waiting: true, gaveUp: true));
    }

    [Fact]
    public void A_read_beats_a_wait_and_with_nothing_there_is_no_line()
    {
      Assert.StartsWith("Slowest here:", WalkTimeLine.For(Selection(), new[] { A }, Clock, waiting: true, gaveUp: true));
      Assert.Null(WalkTimeLine.For(null, new[] { A }, null, false, false));
      Assert.Null(WalkTimeLine.For(Selection(), new[] { Guid.NewGuid() }, Clock, false, false));
    }

    [Fact]
    public void With_no_clock_the_line_says_no_clock()
    {
      Assert.Equal("Nothing here takes measurable time.", WalkTimeLine.For(Selection(), new[] { C }, null, false, false));
    }

    // ---- the bars --------------------------------------------------------------------------------------------------------------------------------

    private static readonly RectangleF Box = new RectangleF(100, 200, 120, 40);

    [Fact]
    public void A_bar_sits_under_its_component_and_its_length_is_its_share()
    {
      var t = new WalkTimes(new[] { Ran(A, "x", 48), Ran(B, "y", 12) });
      var bars = TimeBars.Bars(t, new (Guid, RectangleF?)[] { (A, Box), (B, Box) });

      Assert.Equal(2, bars.Count);
      Assert.Equal(new RectangleF(100, 240 + TimeBars.Gap, 120, TimeBars.Height), bars[0]);
      Assert.Equal(30, bars[1].Width, 3);
      Assert.Equal(100, bars[1].X, 3);
    }

    [Fact]
    public void The_bar_stays_inside_the_walks_hole()
    {
      var t = new WalkTimes(new[] { Ran(A, "x", 48) });
      var bar = TimeBars.Bars(t, new (Guid, RectangleF?)[] { (A, Box) })[0];
      var hole = WalkGeometry.Holes(new RectangleF?[] { Box })[0];
      Assert.True(hole.Contains(bar));
    }

    [Fact]
    public void A_tiny_share_is_still_seen()
    {
      var t = new WalkTimes(new[] { Ran(A, "x", 10000), Ran(B, "y", 1) });
      var bars = TimeBars.Bars(t, new (Guid, RectangleF?)[] { (B, Box) });
      Assert.Equal(TimeBars.MinLength, bars[0].Width, 3);
    }

    [Fact]
    public void No_measurable_time_has_no_bar_and_never_ran_has_a_dot_and_no_bar()
    {
      var t = new WalkTimes(new[] { Ran(A, "x", 48), Ran(B, "y", 0), Never(C, "z") });
      var step = new (Guid, RectangleF?)[] { (B, Box), (C, Box) };

      Assert.Empty(TimeBars.Bars(t, step));
      var dot = Assert.Single(TimeBars.Dots(t, step));
      Assert.Equal(new RectangleF(100, 240 + TimeBars.Gap, TimeBars.DotSize, TimeBars.DotSize), dot);
    }

    /// <summary>The brief's case: a step whose components all read under 1 ms draws no bars at all.</summary>
    [Fact]
    public void A_step_all_under_a_millisecond_draws_nothing()
    {
      var t = new WalkTimes(new[] { Ran(A, "x", 0.2), Ran(B, "y", 0.9), Ran(C, "big", 50) });
      var step = new (Guid, RectangleF?)[] { (A, Box), (B, Box) };
      Assert.Empty(TimeBars.Bars(t, step));
      Assert.Empty(TimeBars.Dots(t, step));
    }

    [Fact]
    public void A_component_gone_from_the_canvas_draws_nothing_and_no_read_draws_nothing()
    {
      var t = new WalkTimes(new[] { Ran(A, "x", 48), Never(B, "y") });
      var gone = new (Guid, RectangleF?)[] { (A, null), (B, null) };
      Assert.Empty(TimeBars.Bars(t, gone));
      Assert.Empty(TimeBars.Dots(t, gone));
      Assert.Empty(TimeBars.Bars(null, new (Guid, RectangleF?)[] { (A, Box) }));
      Assert.Empty(TimeBars.Dots(null, new (Guid, RectangleF?)[] { (A, Box) }));
      Assert.Empty(TimeBars.Bars(t, null));
    }

    // ---- the host, by the text of the code ---------------------------------------------------------------------------------------------------------

    private static string Repo(params string[] parts)
    {
      var dir = new DirectoryInfo(AppContext.BaseDirectory);
      while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Dwight.sln"))) dir = dir.Parent;
      Assert.NotNull(dir);
      return File.ReadAllText(Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray())).Replace("\r\n", "\n");
    }

    private static string Session => Repo("Dwight", "UI", "WalkSession.cs");

    private static string Between(string text, string from, string to)
    {
      var a = text.IndexOf(from, StringComparison.Ordinal);
      Assert.True(a >= 0, from);
      var b = text.IndexOf(to, a + from.Length, StringComparison.Ordinal);
      Assert.True(b > a, to);
      return text.Substring(a, b - a);
    }

    /// <summary>"No model call in this unit": nothing it adds can reach a provider, and the recorded steps are not touched.</summary>
    [Fact]
    public void Nothing_here_can_reach_a_model()
    {
      foreach (var src in new[] { Repo("Dwight", "UI", "SolveReplayCore.cs"), Session })
      {
        Assert.DoesNotContain("ILlmProvider", src);
        Assert.DoesNotContain("StructuredOutput", src);
        Assert.DoesNotContain("NewProvider", src);
      }
    }

    [Fact]
    public void The_times_are_never_read_during_a_solution()
    {
      var read = Between(Session, "private static void ReadTimes()", "private static void OnSolutionEnd(");
      Assert.Contains("doc.SolutionState == GH_ProcessStep.Process", read);
      Assert.InRange(read.IndexOf("doc.SolutionState", StringComparison.Ordinal), 0, read.IndexOf("ProcessorMs", StringComparison.Ordinal));
      var solving = read.Substring(read.IndexOf("doc.SolutionState", StringComparison.Ordinal));
      Assert.InRange(solving.IndexOf("_waitingSinceUtc = DateTime.UtcNow", StringComparison.Ordinal), 0, solving.IndexOf("return;", StringComparison.Ordinal));
      Assert.InRange(solving.IndexOf("return;", StringComparison.Ordinal), 0, solving.IndexOf("ProcessorMs", StringComparison.Ordinal));

      var end = Between(Session, "private static void OnSolutionEnd(", "The tick holds the wait's clock");
      Assert.InRange(end.IndexOf("canvas.BeginInvoke(", StringComparison.Ordinal), 0, end.IndexOf("ReadTimes();", StringComparison.Ordinal));
    }

    [Fact]
    public void The_wait_is_held_by_the_bars_tick_and_ends_on_its_clock()
    {
      var wait = Between(Session, "private static void CheckWait()", "The bars under the step's components");
      Assert.Contains("_doc.SolutionState != GH_ProcessStep.Process", wait);
      Assert.Contains("WalkTimeLine.WaitIsOver(_waitingSinceUtc.Value, DateTime.UtcNow)", wait);
      Assert.Contains("_gaveUp = true;", wait);
      var tick = Between(Session, "public static void Tick()", "private static void Begin(");
      Assert.Contains("CheckWait();", tick);
    }

    [Fact]
    public void A_walk_starting_reads_the_times_and_leaving_lets_everything_go()
    {
      var begin = Between(Session, "private static void Begin(", "private static bool End()");
      Assert.Contains("_play = new WalkPlay();", begin);
      Assert.Contains("_doc.SolutionEnd += OnSolutionEnd;", begin);
      Assert.InRange(begin.IndexOf("ReadTimes();", StringComparison.Ordinal), 0, begin.IndexOf("_tick.Start();", StringComparison.Ordinal));   // Brief DH: at arrival, before any step

      var end = Between(Session, "private static bool End()", "private static void Show(");
      Assert.Contains("_play.Pause();", end);
      Assert.Contains("_pace?.Stop();", end);
      Assert.Contains("HookInput(false);", end);
      Assert.Contains("_doc.SolutionEnd -= OnSolutionEnd;", end);
      Assert.Contains("_times = null;", end);
      Assert.Contains("_panel.SetWalkTotal(null);", end);
    }

    [Theory]
    [InlineData("public static void Next()\n    {\n      Interrupt();")]
    [InlineData("public static void Back() { Interrupt();")]
    public void A_control_pauses_before_it_moves(string site) => Assert.Contains(site, Session);

    [Fact]
    public void A_dot_or_a_card_pauses_a_key_pauses_and_a_lost_bar_pauses()
    {
      var goTo = Between(Session, "public static void GoTo(WebPanel panel, int i)", "private static void LightOnly(");
      Assert.InRange(goTo.IndexOf("Interrupt();", StringComparison.Ordinal), 0, goTo.IndexOf("_state.GoTo(i)", StringComparison.Ordinal));

      var key = Between(Session, "private static void OnCanvasKey(", "private static void Do(");
      Assert.InRange(key.IndexOf("Interrupt();", StringComparison.Ordinal), 0, key.IndexOf("if (key == WalkKey.None) return;", StringComparison.Ordinal));
      Assert.Contains("Do(wasPlaying ? WalkPlay.WhilePlaying(key) : key, false);", key);

      Assert.Contains("Do(Interrupt() ? WalkPlay.WhilePlaying(key) : key, fromBar);", Session);
      Assert.Contains("if (now == WalkSurface.Hidden) Interrupt();", Session);
    }

    /// <summary>The canvas's mouse is hooked only while playing, and only pauses: it is not handled, so a click still does what it does.</summary>
    [Fact]
    public void What_goes_under_a_cards_sentence_goes_in_its_column_never_in_its_corner_pin()
    {
      var shell = Repo("Dwight", "Resources", "panel-shell.html");
      Assert.Contains("function _cardBody(card){ return card.querySelector('.stxt').parentNode; }", shell);
      Assert.Contains("t.className='walktime'; t.textContent=line; _cardBody(at).appendChild(t);", shell);
      Assert.Contains("g.textContent=gone; _cardBody(cards[k]).appendChild(g);", shell);
      Assert.DoesNotContain("cards[k].lastElementChild", shell);
      Assert.DoesNotContain("at.lastElementChild", shell);
    }

    [Fact]
    public void A_letter_the_canvas_never_hears_still_pauses_and_is_not_eaten()
    {
      var hook = Between(Session, "private static void HookInput(bool on)", "private static void OnCanvasInput(");
      Assert.Contains("if (_inputForm != null) _inputForm.KeyDown -= OnFormKey;", hook);
      Assert.Contains("_inputForm = on ? _canvas.FindForm() : null;", hook);
      Assert.Contains("if (_inputForm != null) _inputForm.KeyDown += OnFormKey;", hook);
      var key = Between(Session, "private static void OnFormKey(", "Both surfaces again");
      Assert.Contains("if (e.Handled) Interrupt();", key);
      Assert.DoesNotContain("e.Handled = true", key);
    }

    [Fact]
    public void The_mouse_is_only_hooked_while_playing_and_never_eaten()
    {
      var arm = Between(Session, "private static void Arm()", "private static void OnPace(");
      Assert.Contains("if (!IsLive || !_play.Playing) { HookInput(false); return; }", arm);
      Assert.Contains("HookInput(true);", arm);
      Assert.Contains("private static void OnCanvasInput(object sender, System.Windows.Forms.MouseEventArgs e) => Interrupt();", Session);
      var hook = Between(Session, "private static void HookInput(bool on)", "private static void OnCanvasInput(");
      Assert.Contains("_canvas.MouseDown += OnCanvasInput; _canvas.MouseWheel += OnCanvasInput;", hook);
      Assert.Contains("_canvas.MouseDown -= OnCanvasInput; _canvas.MouseWheel -= OnCanvasInput;", hook);
    }

    [Fact]
    public void The_pace_comes_from_the_step_and_the_timer_stops_itself_first()
    {
      var arm = Between(Session, "private static void Arm()", "private static void OnPace(");
      Assert.Contains("WalkPace.Seconds(_state.Current.Count, WalkText.PlainText(_panel.WalkHtml(_state.Index)).Length)", arm);
      var pace = Between(Session, "private static void OnPace(", "private static void HookInput(");
      Assert.InRange(pace.IndexOf("_pace?.Stop();", StringComparison.Ordinal), 0, pace.IndexOf("_play.Advance(_state)", StringComparison.Ordinal));
      Assert.Contains("if (!IsLive || !_play.Playing) return;", pace);
    }

    [Fact]
    public void The_bar_draws_what_the_session_knows_and_the_paint_is_bound_to_the_walks_document()
    {
      Assert.Contains("WalkBar.Get()?.Present(ViewNow()) ?? WalkSurface.Panel", Session);
      Assert.Contains("GoneNow(), _play.Playing, _play.Ended, TimeLine(), CanPlay(),\n        TreeLens.IsLive ? TreeLens.ShowsAll : (bool?)null,", Session);
      Assert.Contains("private static string TimeLine() => WalkTimeLine.For(_times, _state.Current, _timesAtLocal, _waitingSinceUtc != null, _gaveUp);", Session);

      var paint = Between(Session, "internal static void PaintTimes(", "/// <summary>Esc, the close");
      Assert.Contains("!ReferenceEquals(doc, _doc)", paint);
      Assert.DoesNotContain("ProcessorMs", paint);
      Assert.DoesNotContain("ReadTimes", paint);
      Assert.Contains("TimeBars.InkAlpha, UiTokens.TealWire", paint);

      Assert.Contains("WalkSession.PaintTimes(g, doc);", Repo("Dwight", "UI", "CanvasGlow.cs"));
    }

    [Fact]
    public void The_read_uses_the_plugins_own_has_computed_and_its_own_milliseconds()
    {
      var read = Between(Session, "private static void ReadTimes()", "private static void OnSolutionEnd(");
      Assert.Contains("Dwight.Agent.GhSolveTiming.HasComputed(active)", read);
      Assert.Contains("Dwight.Data.Payloads.ProcessorMs(doc, id)", read);
      Assert.Contains("TreeLens.LabelOf(active)", read);
    }

    [Fact]
    public void The_bars_controls_go_back_to_the_session_and_its_page_pauses_on_any_input_but_Play()
    {
      var bar = Repo("Dwight", "UI", "WalkBar.cs");
      Assert.Contains("case \"play\": WalkSession.TogglePlay(); break;", bar);
      Assert.Contains("case \"input\": WalkSession.Interrupt(); break;", bar);

      var page = Repo("Dwight", "Resources", "walk-bar.html");
      Assert.Contains("onclick=\"dwightSend('play')\"", page);
      Assert.Contains("document.addEventListener('mousedown',function(e){ if(!_onPlay(e.target)) dwightSend('input'); });", page);
      Assert.Contains("if(!(_onPlay(e.target)&&(e.key===' '||e.key==='Enter'))) dwightSend('input');", page);
      Assert.Contains("document.getElementById('walkplay-ic').setAttribute('href',d.playing?'#ic-pause':'#ic-play');", page);   // Brief EC unit 3: the panel's icons, its word the label
      Assert.Contains("p.setAttribute('aria-label',word);", page);
      Assert.Contains("p.disabled=d.n<2||d.canplay===false;", page);

      // The line sits under the sentence in its own scrolling box, as the gone line does, so a narrow bar keeps the sentence first.
      Assert.Contains("t.className='walktime'; t.textContent=d.time; s.appendChild(t);", page);
    }

    /// <summary>The panel is the first home of Play (Brief DH makes it the default Walk): one icon button in the footer's row, with the same calls as the bar's.</summary>
    [Fact]
    public void Play_sits_in_the_panels_footer_and_goes_back_to_the_session()
    {
      var shell = Repo("Dwight", "Resources", "panel-shell.html");
      var row = Between(shell, "<span class=\"walknav\" id=\"walknav\">", "</span>");
      Assert.Contains("id=\"walk-play\"", row);
      Assert.Contains("onclick=\"dwightSend('walk:play')\"", row);
      Assert.Contains("href=\"#ic-play\"", row);
      Assert.InRange(row.IndexOf("id=\"walk-next\"", StringComparison.Ordinal), 0, row.IndexOf("id=\"walk-play\"", StringComparison.Ordinal));
      Assert.InRange(row.IndexOf("id=\"walk-play\"", StringComparison.Ordinal), 0, row.IndexOf("id=\"walk-ask\"", StringComparison.Ordinal));
      Assert.Contains("document.getElementById('walk-play').disabled=cards.length<2;", shell);

      var panel = Repo("Dwight", "UI", "WebPanel.cs");
      Assert.Contains("if (cmd == \"walk:play\") { WalkSession.TogglePlay(); return; }", panel);
      Assert.Contains("if (cmd == \"walk:input\") { WalkSession.Interrupt(); return; }", panel);
    }

    [Fact]
    public void While_it_plays_the_panel_pauses_on_any_input_but_Play_and_its_arrows_stay_the_hosts()
    {
      var shell = Repo("Dwight", "Resources", "panel-shell.html");
      Assert.Contains("document.addEventListener('mousedown',function(e){ if(_walkPlaying&&!_onWalkPlay(e.target)) dwightSend('walk:input'); });", shell);
      var keys = Between(shell, "document.addEventListener('keydown',function(e){ if(!_walkPlaying) return;", "// The step's line of times");
      Assert.Contains("if(_onWalkPlay(e.target)&&(e.key===' '||e.key==='Enter')) return;", keys);
      Assert.Contains("dwightSend('walk:input');", keys);
    }

    [Fact]
    public void Both_surfaces_are_told_the_same_state_and_a_walk_ending_clears_the_panel()
    {
      var tell = Between(Session, "private static void TellPanel()", "private static string TimeLine()");
      Assert.Contains("_panel.SetWalkPlaying(_play.Playing, CanPlay());", tell);
      Assert.Contains("_panel.SetWalkLine(WalkText.PanelLine(_play.Ended, TimeLine()));", tell);

      var redraw = Between(Session, "private static void Redraw()", "A lesson stops");
      Assert.Contains("TellPanel();", redraw);

      var show = Between(Session, "private static void Show(NavReason why)", "private static string GoneNow()");
      Assert.InRange(show.IndexOf("Mark(surface);", StringComparison.Ordinal), 0, show.IndexOf("TellPanel();", StringComparison.Ordinal));

      var end = Between(Session, "private static bool End()", "private static void Show(");
      Assert.Contains("_panel.SetWalkPlaying(false); _panel.SetWalkLine(null);", end);

      Assert.Contains("internal void SetWalkPlaying(bool on, bool canPlay = true) => Run($\"dwightWalkPlaying(", Repo("Dwight", "UI", "WebPanel.cs"));
    }

    [Fact]
    public void The_two_glyphs_come_from_the_one_table_and_the_sprite_holds_them()
    {
      Assert.Contains("play: 'player-play', pause: 'player-pause',", Repo("tools", "build-icon-sprite.js"));
      var sprite = Repo("Dwight", "Resources", "dwight-icons.svg");
      Assert.Contains("id=\"ic-play\"", sprite);
      Assert.Contains("id=\"ic-pause\"", sprite);
    }

    [Fact]
    public void The_public_page_says_it()
    {
      var page = Repo("docs", "public", "verbs.md");
      Assert.Contains("Play walks it by itself, and any key or click pauses it.", page);
      Assert.Contains("the time it last took against the slowest in the selection", page);
    }

    [Fact]
    public void The_changelog_says_it_and_the_panel_places_the_total_as_text()
    {
      Assert.Contains("1 November 2026: a Walk can play itself", Repo("docs", "public", "changelog.md"));
      Assert.Contains("p.textContent=line;", Repo("Dwight", "Resources", "panel-shell.html"));
      Assert.Contains("internal void SetWalkTotal(string line) => Run($\"dwightWalkTotal({Js(line)})\");", Repo("Dwight", "UI", "WebPanel.cs"));
    }
  }
}
