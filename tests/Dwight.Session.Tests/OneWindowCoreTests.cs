using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;
using Dwight.UI;
using Dwight.Session;

namespace Dwight.Session.Tests
{
  /// <summary>Brief BR: the one window's decisions, cell by cell, as ApprovalModeTests pins its table; its placement at 100% and 150%; and the
  /// host and session seams, read as source because they are GH-coupled.</summary>
  public class OneWindowCoreTests
  {
    private const string A = "A", B = "B", X = "X";

    private static WindowStep Go(bool open, string face, string running, WindowEvent e, string page = null, string done = null) =>
      OneWindowCore.Next(new WindowFacts { Open = open, Face = face, Running = running, DonePage = done }, e, page);

    // ---------------------------------------------------------------- the table

    /// <summary>Open before, face, running, event, page -> open after, face after, disposed.</summary>
    [Theory]
    // a chip click opens on what it last showed, or History if nothing yet
    [InlineData(false, null, null, "ChipClick", null, true, null, null)]
    [InlineData(false, A, null, "ChipClick", null, true, A, null)]
    [InlineData(false, A, A, "ChipClick", null, true, A, null)]
    [InlineData(false, A, B, "ChipClick", null, true, A, null)]
    // a minimise always means the chip, disposes nothing, and comes back to the same place
    [InlineData(true, null, null, "Minimise", null, false, null, null)]
    [InlineData(true, A, null, "Minimise", null, false, A, null)]
    [InlineData(true, A, A, "Minimise", null, false, A, null)]
    [InlineData(true, A, B, "Minimise", null, false, A, null)]
    [InlineData(false, A, null, "Minimise", null, false, A, null)]
    // History: a finished conversation is finished with, and a running one is kept
    [InlineData(true, null, null, "Home", null, true, null, null)]
    [InlineData(true, A, null, "Home", null, true, null, A)]
    [InlineData(true, A, A, "Home", null, true, null, null)]
    [InlineData(true, A, B, "Home", null, true, null, A)]
    // a verb, a new chat or a reopened row, from any view, from the chip too
    [InlineData(false, null, null, "Open", X, true, X, null)]
    [InlineData(true, null, null, "Open", X, true, X, null)]
    [InlineData(true, A, null, "Open", X, true, X, A)]
    [InlineData(false, A, null, "Open", X, true, X, A)]
    [InlineData(true, A, A, "Open", X, true, X, null)]    // a new chat during a run: the running one is not disturbed
    [InlineData(true, A, B, "Open", X, true, X, A)]
    [InlineData(true, null, B, "Open", B, true, B, null)] // reopening the row of the one left running shows it, live
    [InlineData(true, A, B, "Open", B, true, B, A)]
    [InlineData(true, A, null, "Open", A, true, A, null)]
    // a turn ending disposes it only if it is out of sight; the chip still comes back to it
    [InlineData(true, A, A, "TurnEnded", A, true, A, null)]
    [InlineData(false, A, A, "TurnEnded", A, false, A, null)]
    [InlineData(true, null, B, "TurnEnded", B, true, null, B)]
    [InlineData(false, null, B, "TurnEnded", B, false, null, B)]
    [InlineData(true, A, B, "TurnEnded", B, true, A, B)]
    [InlineData(true, A, null, "TurnEnded", null, true, A, null)]
    // a document switch never jumps an open window; the chip then opens on History
    [InlineData(true, A, null, "DocumentSwitch", null, true, A, null)]
    [InlineData(true, null, null, "DocumentSwitch", null, true, null, null)]
    [InlineData(false, A, null, "DocumentSwitch", null, false, null, A)]
    [InlineData(false, A, A, "DocumentSwitch", null, false, null, null)]
    [InlineData(false, null, B, "DocumentSwitch", null, false, null, null)]
    // a page closed from outside: its watchdog, or Restart
    [InlineData(true, A, null, "PageClosed", A, true, null, null)]
    [InlineData(false, A, null, "PageClosed", A, false, null, null)]
    [InlineData(true, A, null, "PageClosed", B, true, A, null)]
    public void The_table(bool open, string face, string running, string e, string page, bool openAfter, string faceAfter, string disposed)
    {
      var step = Go(open, face, running, Enum.Parse<WindowEvent>(e), page);
      Assert.Equal(openAfter, step.Open);
      Assert.Equal(faceAfter, step.Face);
      Assert.Equal(disposed, step.Dispose);
    }

    [Fact]
    public void No_step_leaves_more_than_three_live_pages_and_never_disposes_what_is_running_or_showing()
    {
      var pages = new[] { null, A, B, X };
      foreach (WindowEvent e in Enum.GetValues(typeof(WindowEvent)))
      foreach (var open in new[] { true, false })
      foreach (var face in pages)
      foreach (var running in new[] { null, A, B })
      foreach (var page in new[] { A, B, X })
      {
        if (e == WindowEvent.TurnEnded && page != running) continue;   // a turn ends on the page it ran on
        var step = Go(open, face, running, e, page);
        var label = e + " open=" + open + " face=" + face + " running=" + running + " page=" + page;
        if (step.Dispose != null)
        {
          Assert.True(step.Dispose != step.Face, "disposed what is showing: " + label);
          if (e != WindowEvent.TurnEnded) Assert.True(step.Dispose != running, "disposed the running page: " + label);
        }
        // What may survive a step is the face and the running page: every other conversation page is the one this step disposes.
        var survivors = new[] { face, running, e == WindowEvent.Open ? page : null, step.Face }.Where(p => p != null && p != step.Dispose).Distinct()
          .Where(p => p == step.Face || (p == running && e != WindowEvent.TurnEnded)).ToList();
        Assert.True(survivors.Count <= 2, "more than two conversation pages live: " + label);
      }
    }

    [Theory]
    [InlineData("ChipClick")]
    [InlineData("Home")]
    [InlineData("Open")]
    public void A_flip_focuses_the_page(string name)
    {
      var e = Enum.Parse<WindowEvent>(name);
      var step = Go(e != WindowEvent.ChipClick, A, null, e, X);
      Assert.True(step.FocusPage);
      Assert.False(step.FocusCanvas);
    }

    /// <summary>Sixth gate: Timeline, minimise, switch document, click, and the chip opened on the Timeline.</summary>
    [Fact]
    public void A_document_switch_under_the_chip_resets_the_home_page_and_one_over_an_open_window_does_not()
    {
      Assert.True(Go(false, null, null, WindowEvent.DocumentSwitch).ResetHome);
      Assert.True(Go(false, A, null, WindowEvent.DocumentSwitch).ResetHome);
      Assert.True(Go(false, A, A, WindowEvent.DocumentSwitch).ResetHome);
      Assert.False(Go(true, null, null, WindowEvent.DocumentSwitch).ResetHome);   // never jumps an open window: a Timeline being read stays
      Assert.False(Go(true, A, null, WindowEvent.DocumentSwitch).ResetHome);
      foreach (var e in new[] { WindowEvent.ChipClick, WindowEvent.Minimise, WindowEvent.Home, WindowEvent.Open, WindowEvent.TurnEnded, WindowEvent.PageClosed })
        Assert.False(Go(false, A, null, e, A).ResetHome);
      Assert.Contains("if (step.ResetHome) Run(\"dwightDockHome()\");", Dock);
    }

    [Fact]
    public void A_new_page_focuses_its_composer_once_it_is_drawn_and_a_flip_onto_a_drawn_one_insists()
    {
      var p = Panel;
      var focus = p.Substring(p.IndexOf("internal void FocusPage()", StringComparison.Ordinal));
      Assert.Contains("if (_composerDrawn) Run(\"dwightFocusComposer(true)\");", focus);
      Assert.Contains("else _focusWhenDrawn = true;", focus);
      var chat = p.Substring(p.IndexOf("public void SetChat(string messagesJson)", StringComparison.Ordinal));
      var draw = chat.IndexOf("dwightChat(", StringComparison.Ordinal);
      var late = chat.IndexOf("dwightFocusComposer(false)", StringComparison.Ordinal);
      Assert.True(draw > 0 && late > draw, "the late focus must be queued behind the chat that draws the composer");
    }

    [Theory]
    [InlineData(5000, 5000, 1348, 628)]     // past the bottom right
    [InlineData(-500, -500, 112, 212)]      // past the top left
    [InlineData(1000, 400, 1000, 400)]      // inside: it holds
    public void A_move_stays_inside_the_canvas(int x, int y, int atX, int atY)
    {
      var b = OneWindowCore.Move(x, y, 340, 460, new OneWindowCore.Box(100, 200, 1600, 900));
      Assert.Equal((atX, atY, 340, 460), (b.X, b.Y, b.W, b.H));
      var at150 = OneWindowCore.Move(x, y, 340, 460, OneWindowCore.CanvasLogical(150, 300, 2400, 1350, 144));
      Assert.Equal(b.ToString(), at150.ToString());
    }

    [Fact]
    public void A_window_larger_than_the_canvas_keeps_its_head_on_it()
    {
      var b = OneWindowCore.Move(900, 900, 340, 760, new OneWindowCore.Box(0, 0, 300, 200));
      Assert.Equal((12, 12), (b.X, b.Y));
    }

    [Fact]
    public void The_hosts_move_goes_through_the_clamp()
    {
      var move = Dock.Substring(Dock.IndexOf("private void MoveTo(string xy)", StringComparison.Ordinal));
      Assert.Contains("_userBox = OneWindowCore.Move(x, y, ClientSize.Width, ClientSize.Height, canvas.Value);", move);
    }

    [Fact]
    public void A_minimise_hands_the_keyboard_to_the_canvas_and_only_a_minimise_does()
    {
      Assert.True(Go(true, A, null, WindowEvent.Minimise).FocusCanvas);
      Assert.True(Go(true, null, null, WindowEvent.Minimise).FocusCanvas);
      Assert.False(Go(false, A, null, WindowEvent.Minimise).FocusCanvas);   // already the chip: the canvas's keys are not taken
      foreach (var e in new[] { WindowEvent.ChipClick, WindowEvent.Home, WindowEvent.Open, WindowEvent.TurnEnded, WindowEvent.DocumentSwitch, WindowEvent.PageClosed })
        Assert.False(Go(true, A, null, e, A).FocusCanvas);
    }

    [Fact]
    public void A_turn_ending_or_a_document_switch_never_takes_focus()
    {
      Assert.False(Go(true, A, A, WindowEvent.TurnEnded, A).FocusPage);
      Assert.False(Go(true, A, null, WindowEvent.DocumentSwitch).FocusPage);
      Assert.False(Go(false, A, null, WindowEvent.DocumentSwitch).FocusPage);
    }

    [Fact]
    public void The_glow_clears_when_a_finished_conversation_is_left_and_comes_back_when_it_returns()
    {
      Assert.True(Go(true, A, null, WindowEvent.Minimise).ClearGlow);
      Assert.True(Go(false, A, null, WindowEvent.ChipClick).Relight);
      // left while running, it keeps its glow until it ends
      Assert.False(Go(true, A, A, WindowEvent.Minimise).ClearGlow);
      Assert.False(Go(false, A, A, WindowEvent.ChipClick).Relight);
      Assert.True(Go(false, A, A, WindowEvent.TurnEnded, A).ClearGlow);
      Assert.False(Go(true, A, A, WindowEvent.TurnEnded, A).ClearGlow);   // it is showing: it settles, and stays
      Assert.False(Go(false, null, null, WindowEvent.ChipClick).Relight);
    }

    [Fact]
    public void A_turn_that_ends_out_of_sight_is_news_until_the_user_comes_back()
    {
      Assert.False(Go(true, A, A, WindowEvent.TurnEnded, A).DoneUnseen);
      Assert.True(Go(false, A, A, WindowEvent.TurnEnded, A).DoneUnseen);
      Assert.True(Go(true, null, B, WindowEvent.TurnEnded, B).DoneUnseen);
      Assert.True(Go(true, A, B, WindowEvent.TurnEnded, B).DoneUnseen);
    }

    [Fact]
    public void Done_clears_when_the_user_looks_at_that_conversation_and_not_when_the_window_opens_on_something_else()
    {
      Assert.True(Go(false, A, null, WindowEvent.ChipClick, done: A).ClearDoneUnseen);   // the chip opens on the very page
      Assert.True(Go(true, null, null, WindowEvent.Open, A, done: A).ClearDoneUnseen);
      Assert.False(Go(false, null, null, WindowEvent.ChipClick, done: A).ClearDoneUnseen);   // the chip opens on History
      Assert.False(Go(true, null, null, WindowEvent.Open, X, done: A).ClearDoneUnseen);      // another conversation
      Assert.False(Go(true, A, null, WindowEvent.Minimise, done: A).ClearDoneUnseen);        // going to the chip is not looking
      Assert.False(Go(false, A, null, WindowEvent.ChipClick).ClearDoneUnseen);

      // a page disposed out of sight comes back as its row
      Assert.True(OneWindowCore.RowClearsDone("t1", "t1"));
      Assert.False(OneWindowCore.RowClearsDone("t1", "t2"));
      Assert.False(OneWindowCore.RowClearsDone("t1", null));
      Assert.True(OneWindowCore.RowClearsDone(null, null));
    }

    [Theory]
    [InlineData("waiting", "Waiting for you")]
    [InlineData("running", "Working")]
    [InlineData(null, null)]
    [InlineData("anything else", null)]
    public void A_live_row_carries_the_plugins_words(string live, string words) => Assert.Equal(words, HistoryCore.LiveWords(live));

    [Fact]
    public void The_chips_state_holds_it_is_told_again_on_a_reload_and_does_not_rest_on_the_ring()
    {
      var dock = Dock;
      var refresh = Regex.Match(dock, "public void Refresh\\(\\).*?\\n    }", RegexOptions.Singleline).Value;
      Assert.Contains("dwightChipState(", refresh);
      Assert.Contains("Refresh(); };", dock.Substring(dock.IndexOf("_web.DocumentLoaded +=", StringComparison.Ordinal), 160));   // a load refreshes, so a reload is told
      // Brief DG moved the row's writing to DockRows: the live word is still the plugin's, and the dock still hands it the live state.
      Assert.Contains("RowLive(c), c.State", dock);
      Assert.Contains("[\"liveWord\"] = HistoryCore.LiveWords(live)", Source("Dwight", "Session", "DockRows.cs"));
      // a waiting card is read from the store on every tick, so nothing but answering it ends the state
      Assert.Equal("waiting", OneWindowCore.ChipState(false, true, false));
      Assert.Equal("waiting", OneWindowCore.ChipState(false, true, false));
    }

    // ---------------------------------------------------------------- while Dwight is working

    [Theory]
    [InlineData("Minimise", "Allow")]
    [InlineData("History", "Allow")]
    [InlineData("Timeline", "Allow")]
    [InlineData("ReadFinished", "Allow")]
    [InlineData("NewChat", "Allow")]
    [InlineData("SendSameChat", "Refuse")]
    [InlineData("SendOtherChat", "Refuse")]
    [InlineData("VerbFromChord", "Refuse")]
    [InlineData("Walk", "Refuse")]
    [InlineData("ClickCard", "Refuse")]
    public void What_works_during_a_run(string request, string answer) => Assert.Equal(Enum.Parse<RunAnswer>(answer), OneWindowCore.DuringRun(Enum.Parse<RunRequest>(request)));

    [Fact]
    public void A_request_the_table_has_not_been_told_about_is_refused() => Assert.Equal(RunAnswer.Refuse, OneWindowCore.DuringRun((RunRequest)999));

    [Fact]
    public void The_refusal_says_previous_message_only_in_the_chat_that_is_running()
    {
      Assert.Equal("Dwight is still working on the previous message. Wait for it to finish, or press Stop.", OneWindowCore.BusyLine("t1", "t1"));
      Assert.Equal("Dwight is working in another chat. Wait for it to finish, or stop it there.", OneWindowCore.BusyLine("t1", "t2"));
      Assert.Equal("Dwight is working on another task. Wait for it to finish, or stop it there.", OneWindowCore.BusyLine("t1", null));   // a verb: it is not a chat
      Assert.DoesNotContain("previous message", OneWindowCore.BusyLine("t1", "t2"));
      Assert.DoesNotContain("previous message", OneWindowCore.BusyLine("t1", null));
      Assert.False(OneWindowCore.BusyHasWayThere("t1", "t1"));   // Stop is on this page
      Assert.True(OneWindowCore.BusyHasWayThere("t1", "t2"));
      Assert.True(OneWindowCore.BusyHasWayThere("t1", null));
    }

    [Fact]
    public void A_walk_during_a_run_is_refused_in_one_sentence() =>
      Assert.Equal("Dwight is working. Wait for it to finish, or stop it, then go back.", HistoryCore.WalkBusyLine);

    // ---------------------------------------------------------------- what the shells are told

    [Theory]
    [InlineData("t1", "t1", false, "running")]
    [InlineData("t1", "t1", true, "running")]    // a card minted during its own turn is not shown until the turn ends
    [InlineData("t1", "t2", true, "waiting")]    // a card waiting is not a run
    [InlineData("t1", null, true, "waiting")]
    [InlineData("t1", "t2", false, null)]
    [InlineData("t1", null, false, null)]
    [InlineData(null, "t2", true, null)]         // a verb's row
    [InlineData("", null, false, null)]
    public void A_rows_live_state(string thread, string running, bool pending, string word) => Assert.Equal(word, OneWindowCore.LiveWord(thread, running, pending));

    [Theory]
    [InlineData(false, false, false, null)]
    [InlineData(true, false, false, "thinking")]
    [InlineData(false, true, false, "waiting")]
    [InlineData(false, false, true, "done")]
    [InlineData(true, true, false, "waiting")]   // a card in another chat outranks a run: only the user can end it
    [InlineData(true, false, true, "thinking")]
    [InlineData(false, true, true, "waiting")]
    [InlineData(true, true, true, "waiting")]
    public void The_chips_state(bool thinking, bool waiting, bool done, string state) => Assert.Equal(state, OneWindowCore.ChipState(thinking, waiting, done));

    [Theory]
    [InlineData(null, "waiting", false, true)]
    [InlineData(null, "done", false, false)]        // the ring means only "you are needed" (#1159)
    [InlineData("thinking", "done", false, false)]
    [InlineData("thinking", "waiting", false, true)]
    [InlineData("waiting", "waiting", false, false)]   // once, not on every tick
    [InlineData(null, "thinking", false, false)]       // lavender stays the thinking state
    [InlineData("waiting", null, false, false)]
    [InlineData(null, "waiting", true, false)]         // no chip to ring
    public void The_ring_draws_once_on_a_waiting_card_and_never_on_a_finished_turn(string before, string after, bool open, bool rings) =>
      Assert.Equal(rings, OneWindowCore.ChipRings(before, after, open));

    // ---------------------------------------------------------------- placement, at 100% and 150%

    private static OneWindowCore.Box Canvas(double dpi) => OneWindowCore.CanvasLogical((int)(100 * dpi / 96), (int)(200 * dpi / 96), (int)(1600 * dpi / 96), (int)(900 * dpi / 96), dpi);

    [Theory]
    [InlineData(96)]
    [InlineData(144)]
    public void Physical_pixels_become_logical_points_so_nothing_lands_off_the_screen(double dpi)
    {
      var c = Canvas(dpi);
      Assert.Equal((100, 200, 1600, 900), (c.X, c.Y, c.W, c.H));
    }

    [Fact]
    public void Placement_at_150_percent_is_the_same_logical_rectangle_as_at_100()
    {
      Assert.Equal(OneWindowCore.Window(Canvas(96)).ToString(), OneWindowCore.Window(Canvas(144)).ToString());
      Assert.Equal(OneWindowCore.Chip(Canvas(96), 0).ToString(), OneWindowCore.Chip(Canvas(144), 0).ToString());
      // the #176 defect, as a control: treating physical pixels as logical moves it right off this canvas
      var raw = OneWindowCore.CanvasLogical(150, 300, 2400, 1350, 96);
      Assert.NotEqual(OneWindowCore.Window(Canvas(144)).ToString(), OneWindowCore.Window(raw).ToString());
    }

    [Fact]
    public void The_chip_sits_in_the_corner_clear_of_the_compass()
    {
      var chip = OneWindowCore.Chip(new OneWindowCore.Box(100, 200, 1600, 900), 0);
      Assert.Equal((1596, 1040, 48, 48), (chip.X, chip.Y, chip.W, chip.H));
      Assert.Equal(1496, OneWindowCore.Chip(new OneWindowCore.Box(100, 200, 1600, 900), 100).X);
    }

    [Fact]
    public void The_window_has_one_size_inside_the_canvas_above_the_compass()
    {
      var w = OneWindowCore.Window(new OneWindowCore.Box(100, 200, 1600, 900));
      Assert.Equal((1348, 260, 340, 760), (w.X, w.Y, w.W, w.H));
      Assert.Equal(200 + 900 - OneWindowCore.WindowBottomClear, w.Y + w.H);   // its bottom edge, above the compass
    }

    [Fact]
    public void A_short_canvas_shrinks_the_window_and_keeps_its_head_on_the_canvas()
    {
      var w = OneWindowCore.Window(new OneWindowCore.Box(0, 0, 1000, 500));
      Assert.Equal((648, 12, 340, 408), (w.X, w.Y, w.W, w.H));
      var tiny = OneWindowCore.Window(new OneWindowCore.Box(0, 0, 300, 200));
      Assert.Equal(12, tiny.X);
      Assert.Equal(12, tiny.Y);
      Assert.Equal(276, tiny.W);
      Assert.Equal(OneWindowCore.WindowMinH, tiny.H);
    }

    [Fact]
    public void The_chips_nudge_stays_in_its_band()
    {
      var c = new OneWindowCore.Box(0, 0, 1000, 800);
      Assert.Equal(0, OneWindowCore.ChipNudge(c, 896 + 24));
      Assert.Equal(OneWindowCore.MaxNudge, OneWindowCore.ChipNudge(c, 0));
      Assert.Equal(-(OneWindowCore.CornerClear - OneWindowCore.EdgeMargin), OneWindowCore.ChipNudge(c, 5000));
    }

    [Fact]
    public void A_resize_moves_the_edge_it_holds_and_stays_on_the_canvas_above_the_minimum()
    {
      var canvas = new OneWindowCore.Box(0, 0, 1600, 900);
      var start = new OneWindowCore.Box(1000, 100, 400, 500);
      var left = OneWindowCore.Resize("l", start, -50, 0, canvas);
      Assert.Equal((950, 100, 450, 500), (left.X, left.Y, left.W, left.H));
      var top = OneWindowCore.Resize("t", start, 0, -30, canvas);
      Assert.Equal((1000, 70, 400, 530), (top.X, top.Y, top.W, top.H));
      var bottom = OneWindowCore.Resize("b", start, 0, 40, canvas);
      Assert.Equal(540, bottom.H);

      var small = OneWindowCore.Resize("t", start, 0, 900, canvas);
      Assert.Equal(OneWindowCore.WindowMinH, small.H);
      Assert.Equal(100 + 500 - OneWindowCore.WindowMinH, small.Y);
      var narrow = OneWindowCore.Resize("l", start, 900, 0, canvas);
      Assert.Equal(OneWindowCore.WindowW, narrow.W);

      var off = OneWindowCore.Resize("b", start, 0, 5000, canvas);
      Assert.Equal(888, off.Y + off.H);
      var offTop = OneWindowCore.Resize("t", start, 0, -5000, canvas);
      Assert.Equal(12, offTop.Y);
    }

    // ---------------------------------------------------------------- the seams, read as source

    private static string Source(params string[] parts)
    {
      var dir = new DirectoryInfo(AppContext.BaseDirectory);
      while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Dwight.sln"))) dir = dir.Parent;
      Assert.True(dir != null);
      return File.ReadAllText(Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray()));
    }

    private static string Scaffold => Source("Dwight", "UI", "DwightScaffold.cs");
    private static string Panel => Source("Dwight", "UI", "WebPanel.cs");
    private static string Dock => Source("Dwight", "UI", "DwightDock.cs");

    [Fact]
    public void A_page_is_not_a_window_and_keeps_what_the_session_harness_and_gate_tools_reach()
    {
      var p = Panel;
      Assert.Contains("internal sealed class WebPanel : IResultPanel", p);
      Assert.DoesNotContain(": Form", p);
      foreach (var name in new[] { "private readonly WebView _web;", "private static WebPanel _current;", "public Action<string> OnConsent;", "public event EventHandler<EventArgs> Closed;",
                                   "public void Close()", "public static void CloseCurrent()" })
        Assert.Contains(name, p);
    }

    [Fact]
    public void The_host_keeps_the_names_the_gate_tools_reflect_on()
    {
      Assert.Contains("private static DwightDock _instance;", Dock);
      Assert.Contains("private bool _loaded, _expanded;", Dock);
      Assert.Contains("private readonly WebView _web;", Dock);
    }

    [Fact]
    public void A_page_has_its_own_new_view_so_an_old_documents_message_cannot_land_on_a_new_owner()
    {
      Assert.Contains("_web = new WebView { BrowserContextMenuEnabled = false };", Panel);
      Assert.Contains("_web.MessageReceived += (s, e) => Handle(e.Message);", Panel);
      Assert.Contains("if (string.IsNullOrEmpty(cmd) || _closed) return;", Panel);
    }

    [Fact]
    public void No_host_means_the_verbs_still_work()
    {
      Assert.Contains("if (host == null) throw new InvalidOperationException(", Panel);
      Assert.Contains("return ResultPopup.ShowThinking(title);", Source("Dwight", "UI", "ResultSurface.cs"));
      Assert.Contains("ArmLoadWatchdog();", Panel);
    }

    [Fact]
    public void The_shells_close_and_Esc_minimise()
    {
      var cmds = Regex.Match(Dock, "internal bool HandleWindowCommand\\(string cmd\\).*?return false;", RegexOptions.Singleline).Value;
      Assert.Contains("cmd == \"close\" || cmd == \"minimise\"", cmds);
      Assert.Contains("cmd == \"nav:home\"", cmds);
      Assert.Contains("cmd == \"nav:running\"", cmds);
      var shell = Source("Dwight", "Resources", "panel-shell.html");
      Assert.Contains("dwightSend('nav:home')", shell);
      Assert.Contains("dwightSend('minimise')", shell);
      Assert.Contains("e.key==='Escape') dwightSend('minimise')", Source("Dwight", "Resources", "dock-shell.html"));
    }

    [Fact]
    public void New_chat_on_the_home_page_goes_through_the_menus_own_door()
    {
      Assert.Contains("{ DwightCanvasPopup.Safe(() => DwightScaffold.Open()); return; }", Dock);
      Assert.Contains("cmd == \"new\"", Dock);
      Assert.Contains("public static void Open() { OpenThread(); }", Scaffold);   // no busy check of its own: a new chat during a run is allowed
      Assert.Equal(RunAnswer.Allow, OneWindowCore.DuringRun(RunRequest.NewChat));
      Assert.Contains("DwightCanvasPopup.Safe(() => Dwight.UI.DwightScaffold.Open())", Source("Dwight", "DwightTopBarMenu.cs"));   // the menu's own door
    }

    [Fact]
    public void A_verbs_Dismiss_is_not_a_minimise_the_page_goes_and_the_window_goes_to_the_chip()
    {
      Assert.Contains("if (cmd == \"dismiss\") { Step(WindowEvent.Home); Step(WindowEvent.Minimise); return true; }", Dock);
      var shell = Source("Dwight", "Resources", "panel-shell.html");
      Assert.Equal(5, Regex.Matches(shell, "dwightSend\\(\\\\'dismiss\\\\'\\)").Count);
      Assert.DoesNotContain("dwightSend('close')", shell);
      Assert.DoesNotContain("dwightSend(\\'close\\')", shell);

      // the two steps, through the core: a finished proposal is disposed, and the chip opens on History
      var home = Go(true, A, null, WindowEvent.Home);
      Assert.Equal(A, home.Dispose);
      var chip = Go(home.Open, home.Face, null, WindowEvent.Minimise);
      Assert.False(chip.Open);
      Assert.Null(chip.Face);
      Assert.True(chip.FocusCanvas);
    }

    [Fact]
    public void A_click_during_a_run_is_refused_before_the_card_is_spent_and_an_automatic_approval_is_not_a_click()
    {
      var s = Scaffold;
      var guard = s.IndexOf("if (autoMode == null && DwightSession.IsBusy)", StringComparison.Ordinal);
      var spend = s.IndexOf("if (!TakeConsent(token, threadId, out var kind, out var seed, out var reason))", StringComparison.Ordinal);
      Assert.True(guard > 0 && spend > guard, "the busy refusal must come before the card is spent");
      Assert.Contains("BusyHtml(threadId)", s.Substring(guard, spend - guard));
    }

    [Fact]
    public void Sending_during_a_run_is_refused_in_this_chats_own_words()
    {
      Assert.Contains("if (ctx == null) { AppendPluginHtml(wp, doc, threadId, BusyHtml(threadId)); return; }", Scaffold);
      Assert.Contains("dwightSend('nav:running')", Scaffold);
      Assert.Contains("private const string BusyNotice = OneWindowCore.BusySameChat;", Scaffold);
    }

    [Fact]
    public void A_walk_is_refused_during_a_run_at_the_ask_and_again_at_the_click()
    {
      var walk = Source("Dwight", "UI", "HistoryWalk.cs");
      Assert.Equal(2, Regex.Matches(walk, "DwightSession\\.IsBusy").Count);
      Assert.Equal(2, Regex.Matches(walk, "HistoryCore\\.WalkBusyLine").Count);
      var ask = walk.IndexOf("if (Dwight.DwightSession.IsBusy) return HistoryCore.WalkBusyLine;", StringComparison.Ordinal);
      Assert.True(ask > 0 && ask < walk.IndexOf("HistoryCore.PlanBackBefore", StringComparison.Ordinal), "the refusal comes before the walk is counted");
    }

    [Fact]
    public void A_verb_during_a_run_keeps_its_toast_and_the_window_does_not_turn()
    {
      var session = Source("Dwight", "DwightSession.cs");
      var busy = session.IndexOf("lock (_lock) { if (_running) return null; }", StringComparison.Ordinal);
      var show = session.IndexOf("ctx.Panel = adopt ?? ResultSurface.Show(", StringComparison.Ordinal);
      Assert.True(busy > 0 && show > busy, "the busy check must come before the window turns");
      Assert.Contains("DwightToast.Show(\"Dwight is already on a task\")", Source("Dwight", "LlmActions.cs"));
    }

    [Fact]
    public void A_verbs_in_panel_undo_is_refused_during_a_run()
    {
      var verbs = Source("Dwight", "LlmActions.cs");
      foreach (var head in new[] { "private static void UndoApplied(WebPanel w, SimplifyApplyState st)", "private static void UndoToScript(WebPanel w, ToScriptApplyState st)" })
      {
        var at = verbs.IndexOf(head, StringComparison.Ordinal);
        Assert.True(at > 0, head);
        var body = verbs.Substring(at, verbs.IndexOf(".Undo()", at, StringComparison.Ordinal) - at);
        Assert.Contains("if (DwightSession.IsBusy)", body);
      }
    }

    [Fact]
    public void Reading_a_finished_conversation_during_a_run_does_not_take_the_running_ones_glow()
    {
      var session = Source("Dwight", "DwightSession.cs");
      var reopen = session.Substring(session.IndexOf("public static void Reopen(DwightCommand cmd)", StringComparison.Ordinal));
      Assert.Contains("DwightDock.Host()?.ShowLive(cmd.ThreadId) == true", reopen);
      Assert.True(reopen.IndexOf("if (!running)", StringComparison.Ordinal) < reopen.IndexOf("CanvasGlow.Show(cmd.SourceGuids", StringComparison.Ordinal));
    }

    [Fact]
    public void A_page_is_disposed_after_its_own_message_has_returned()
    {
      var close = Regex.Match(Panel, "public void Close\\(\\).*?\\n    }", RegexOptions.Singleline).Value;
      Assert.Contains("AsyncInvoke", close);
      Assert.Contains("_web.Dispose()", close);
    }
  }
}
