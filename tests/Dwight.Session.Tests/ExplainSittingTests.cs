using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dwight.Agent;
using Dwight.UI;
using Xunit;

namespace Dwight.Session.Tests
{
  /// <summary>
  /// Brief EC, from Tharun's sitting on the seventeenth and Rhino 2's measurement of it: the Ask keeps the Walk on its step behind its chat (the one window's kept page),
  /// the run's one chat found by its key whichever door asks, the step's sentence in the chat's ids, and the lesson's lines as its chat carries them.
  /// </summary>
  public class ExplainSittingTests
  {
    private const string Walk = "W", Chat = "C", Other = "O", Running = "R";

    private static WindowStep Go(bool open, string face, string running, string kept, WindowEvent e, string page = null) =>
      OneWindowCore.Next(new WindowFacts { Open = open, Face = face, Running = running, Kept = kept }, e, page);

    // ---- the kept Walk (unit 5b) ---------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Asking_from_the_walk_opens_its_chat_and_keeps_the_walks_page_behind_it()
    {
      var s = Go(true, Walk, null, Walk, WindowEvent.AskOpen, Chat);
      Assert.Equal(Chat, s.Face);
      Assert.Null(s.Dispose);
      Assert.Equal(Walk, s.Kept);
      Assert.Null(s.CloseKept);
    }

    [Fact]
    public void A_second_ask_from_the_bar_with_the_chat_in_front_changes_nothing_in_the_window()
    {
      var s = Go(true, Chat, null, Walk, WindowEvent.AskOpen, Chat);
      Assert.Equal(Chat, s.Face);
      Assert.Null(s.Dispose);
      Assert.Equal(Walk, s.Kept);
    }

    [Fact]
    public void Back_to_the_walk_brings_its_page_and_lets_the_chat_go_unless_it_runs()
    {
      var s = Go(true, Chat, null, Walk, WindowEvent.Open, Walk);
      Assert.Equal(Walk, s.Face);
      Assert.Equal(Chat, s.Dispose);
      Assert.Null(s.Kept);
      Assert.Null(s.CloseKept);

      var running = Go(true, Chat, Chat, Walk, WindowEvent.Open, Walk);
      Assert.Null(running.Dispose);   // a turn in the chat goes on, hidden, as any running page does
    }

    [Fact]
    public void Going_home_keeps_the_walk_because_history_is_a_way_back()
    {
      var s = Go(true, Chat, null, Walk, WindowEvent.Home);
      Assert.Null(s.Face);
      Assert.Equal(Chat, s.Dispose);
      Assert.Equal(Walk, s.Kept);
      Assert.Null(s.CloseKept);
    }

    [Fact]
    public void Opening_anything_else_closes_the_kept_walk_and_its_walk_ends_with_it()
    {
      var s = Go(true, Chat, null, Walk, WindowEvent.Open, Other);
      Assert.Equal(Other, s.Face);
      Assert.Equal(Chat, s.Dispose);
      Assert.Equal(Walk, s.CloseKept);
      Assert.Null(s.Kept);
    }

    [Fact]
    public void Minimise_and_the_chip_keep_the_walk_kept()
    {
      Assert.Equal(Walk, Go(true, Chat, null, Walk, WindowEvent.Minimise).Kept);
      Assert.Equal(Walk, Go(false, Chat, null, Walk, WindowEvent.ChipClick).Kept);
      Assert.Null(Go(true, Chat, null, Walk, WindowEvent.Minimise).CloseKept);
    }

    [Fact]
    public void A_kept_page_closed_from_outside_is_no_longer_kept()
    {
      Assert.Null(Go(true, Chat, null, Walk, WindowEvent.PageClosed, Walk).Kept);
    }

    [Fact]
    public void Nothing_kept_behaves_as_it_always_did()
    {
      var s = Go(true, Walk, null, null, WindowEvent.Open, Other);
      Assert.Equal(Walk, s.Dispose);
      Assert.Null(s.CloseKept);
      Assert.Null(s.Kept);
    }

    [Fact]
    public void Never_more_than_home_the_face_one_running_and_one_kept()
    {
      var s = Go(true, Chat, Running, Walk, WindowEvent.AskOpen, Chat);
      var live = new[] { s.Face, Running, s.Kept }.Where(p => p != null).Distinct().Count();
      Assert.True(live <= 3);   // with home, four
    }

    [Fact]
    public void The_host_keeps_the_walk_only_for_its_own_ask_and_closes_it_with_its_walk()
    {
      var dock = Repo("Dwight", "UI", "DwightDock.cs");
      Assert.Contains("if (e == WindowEvent.Open && page != null && (_asking || page == WalkSession.AskedPageId)) e = WindowEvent.AskOpen;", dock);
      Assert.Contains("Kept = _keptId }, e, page);", dock);
      Assert.Contains("_keptId = step.Kept;", dock);
      Assert.Contains("var keptGone = Page(step.CloseKept);", dock);
      var ended = Between(dock, "internal void WalkEnded(WebPanel walkPage)", "private void GoToRunning()");
      Assert.Contains("if (_faceId != walkPage.PageId) { try { walkPage.Close(); } catch { } }", ended);
      Assert.Contains("try { DwightDock.Host()?.WalkEnded(page); } catch { }", Repo("Dwight", "UI", "WalkSession.cs"));
      Assert.Contains("if (cmd == \"nav:walk\") { ShowKeptWalk(null); return true; }", dock);
      Assert.Contains("if (string.IsNullOrEmpty(cmd.ThreadId) && DwightDock.Host()?.ShowKeptWalk(cmd.RunId) == true) return;", Repo("Dwight", "DwightSession.cs"));
    }

    // ---- the step's sentence in the chat's ids (unit 5) -----------------------------------------------------------------------------------------------------------

    private static readonly Guid Access = Guid.NewGuid(), Script = Guid.NewGuid(), Filter = Guid.NewGuid();
    private static readonly Func<int, int?> ChatIds = RunIds.Through(
      new Dictionary<Guid, int> { [Access] = 395, [Script] = 409, [Filter] = 425 },
      g => g == Access ? 5096 : g == Script ? 5097 : (int?)null);

    /// <summary>Tharun's sitting: the draft said 395 and 409, the chat had them as 5096 and 5097, and the chat told him his step was wrong.</summary>
    [Fact]
    public void The_ask_line_says_the_chats_ids_and_drops_one_it_cannot_place()
    {
      Assert.Equal("About step 2 of this Explain: The accessDirection text (5096) feeds the GhPython Script (5097), whose result reaches Stream Filter.",
        WalkText.AskLine(1, "<p>The <code>accessDirection</code> text (395) feeds the GhPython Script (409), whose result reaches Stream Filter (425).</p>", ChatIds));
    }

    [Fact]
    public void An_ask_line_with_no_translation_shows_no_run_ids_at_all()
    {
      Assert.Equal("About step 1 of this Explain: add1 feeds mul.", WalkText.AskLine(0, "add1 (7) feeds mul (9).", null));
    }

    // ---- one run, one chat (unit 5) -----------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Two_asks_from_one_run_find_one_chat_and_another_run_finds_none()
    {
      var threads = new List<StoredThread> { new StoredThread { Id = "t1", RunKey = "runA" }, new StoredThread { Id = "t2" } };
      Assert.Equal("t1", HistoryCore.ThreadOfRun(threads, "runA"));
      Assert.Equal("t1", HistoryCore.ThreadOfRun(threads, "runA"));   // the footer's Chat and every step's Ask: the same key, the same chat
      Assert.Null(HistoryCore.ThreadOfRun(threads, "runB"));
      Assert.Null(HistoryCore.ThreadOfRun(threads, null));            // a row from an older build has no key, and each door opens a chat as it did
      Assert.Null(HistoryCore.ThreadOfRun(threads, ""));
    }

    [Fact]
    public void A_runs_chat_round_trips_its_key_and_is_named_for_its_run()
    {
      var rec = new SessionRecord { Threads = new List<StoredThread> { new StoredThread { Id = "t1", RunKey = "runA", RunTitle = HistoryCore.RunThreadTitle("Explain", new DateTime(2026, 11, 2, 14, 2, 0)), Messages = new List<StoredMessage>() } } };
      var read = SessionStoreCore.Read(SessionStoreCore.Write(rec)).Record;
      Assert.Equal("runA", read.Threads[0].RunKey);
      Assert.Equal("Explain, 14:02", read.Threads[0].RunTitle);
      Assert.Equal("Explain, 14:02", HistoryCore.ThreadName(read.Threads[0]));
      read.Threads[0].Messages.Add(new StoredMessage { Who = "me", Src = "why is the sum 7?", Html = "why is the sum 7?" });
      Assert.Equal("Explain, 14:02", HistoryCore.ThreadName(read.Threads[0]));   // named for the run whatever is asked in it

      var older = SessionStoreCore.Read("{\"schema\":1,\"minor\":10,\"threads\":[{\"id\":\"t2\",\"messages\":[{\"who\":\"me\",\"src\":\"hello there\",\"html\":\"x\"}]}]}").Record;
      Assert.Null(older.Threads[0].RunKey);
      Assert.Equal("hello there", HistoryCore.ThreadName(older.Threads[0]));
    }

    // The lesson as its chat carries it (unit 5b) is the deck's now (Brief EI): TeachingDeckTests holds its lines, its ask draft and its numbering.

    private static string Repo(params string[] parts)
    {
      var dir = AppContext.BaseDirectory;
      while (dir != null && !File.Exists(Path.Combine(dir, "Dwight.sln"))) dir = Path.GetDirectoryName(dir);
      Assert.NotNull(dir);
      return File.ReadAllText(Path.Combine(new[] { dir }.Concat(parts).ToArray())).Replace("\r\n", "\n");
    }

    private static string Between(string source, string from, string to)
    {
      var a = source.IndexOf(from, StringComparison.Ordinal);
      Assert.True(a >= 0, from);
      var b = source.IndexOf(to, a + from.Length, StringComparison.Ordinal);
      Assert.True(b >= 0, to);
      return source.Substring(a, b - a);
    }
  }
}
