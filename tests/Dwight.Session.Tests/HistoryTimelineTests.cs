using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using Dwight.Session;

namespace Dwight.Session.Tests
{
  /// <summary>Brief BQ: History as a timeline of events, on modelled stacks. Index 0 of each list is the newest record, as Grasshopper.dll's IL
  /// has it. The scenarios also render tools/ui-history-events.json, the file the shell's fixtures read, so the shell is measured on what the
  /// host can send (#1139).</summary>
  public class HistoryTimelineTests
  {
    private static readonly DateTime Now = new DateTime(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc);

    private static Guid G(int n) => new Guid(n, 0, 0, new byte[8]);

    private static string Ago(int minutes) => Now.AddMinutes(-minutes).ToString("o");

    private static StoredChange Chat(int id, string thread, string done, int minutes, bool reverted = false, string by = HistoryCore.ByYou) => new StoredChange
    {
      UndoId = id == 0 ? "" : G(id).ToString("D"), UndoName = "Dwight: build region", Kind = "Build", Scope = "Build these regions? A1",
      ThreadId = thread, Done = done, Utc = Ago(minutes), Reverted = reverted, ApprovedBy = by,
    };

    private static StoredChange Verb(int id, string kind, string undoName, int minutes) => new StoredChange
    {
      UndoId = G(id).ToString("D"), UndoName = undoName, Kind = kind, Scope = HistoryCore.ScopeFromRecordName(undoName), Utc = Ago(minutes), ApprovedBy = HistoryCore.ByYou,
    };

    private static TimelineInput Input(IEnumerable<StoredChange> changes, StackSnapshot stack) => new TimelineInput
    {
      Changes = changes.ToList(),
      Stack = stack,
      NowUtc = Now,
      TitleOf = id => id == "abc" ? "A tower whose floor plates twist" : id == "def" ? HistoryCore.NewChatTitle : null,
      CanOpen = id => id == "abc",
    };

    private static List<JsonObject> Items(TimelineInput input) => HistoryCore.TimelineItems(input).Select(n => n.AsObject()).ToList();

    private static string S(JsonNode n) => n?.GetValue<string>();

    private static string Shape(IEnumerable<JsonObject> items) => string.Join(" | ", items.Select(i =>
      S(i["type"]) == "event" ? S(i["verb"]) + ":" + S(i["state"]) + ":" + i["count"]!.GetValue<int>() : S(i["type"])));

    private static StackSnapshot Undo(params (int Id, string Name)[] newestFirst) => new StackSnapshot
    {
      UndoIds = newestFirst.Select(x => G(x.Id)).ToArray(),
      UndoNames = newestFirst.Select(x => x.Name).ToArray(),
    };

    // ---------------------------------------------------------------- the events

    [Fact]
    public void The_sandwich_another_verb_between_a_chats_changes_splits_it_and_both_rows_carry_the_name()
    {
      var items = Items(Input(new[]
      {
        Chat(3, "abc", "Built B1.", 5),
        Verb(2, "Reorganise", "Dwight: Reorganise", 7),
        Chat(1, "abc", "Built A1.", 9),
      }, Undo((3, "Dwight: build region"), (2, "Dwight: Reorganise"), (1, "Dwight: build region"))));

      Assert.Equal("Build:standing:1 | Reorganise:standing:1 | Build:standing:1", Shape(items));
      Assert.Equal("A tower whose floor plates twist", S(items[0]["title"]));
      Assert.Equal("A tower whose floor plates twist", S(items[2]["title"]));
      Assert.Equal("Reorganised the layout.", S(items[1]["title"]));
      Assert.Null(items[1]["threadId"]);
      Assert.Null(items[1]["openChat"]);
      Assert.Equal("abc", S(items[0]["threadId"]));
      Assert.Equal(HistoryCore.OpenChatButton, S(items[0]["openChat"]));
    }

    [Fact]
    public void Another_chat_between_splits_consecutive_changes_of_one_chat_are_one_event()
    {
      var items = Items(Input(new[]
      {
        Chat(4, "abc", "Built C1.", 2),
        Chat(3, "abc", "Built B1.", 3),
        Chat(2, "def", "Set Radius.", 4),
        Chat(1, "abc", "Built A1.", 5),
      }, Undo((4, "b"), (3, "b"), (2, "s"), (1, "b"))));
      Assert.Equal("Build:standing:2 | Build:standing:1 | Build:standing:1", Shape(items));
      Assert.Equal(new[] { "Built C1.", "Built B1." }, items[0]["steps"]!.AsArray().Select(s => S(s!["line"])));
    }

    [Fact]
    public void A_verbs_changes_are_one_event_each_and_a_chat_id_on_one_does_not_group_it()
    {
      var a = Verb(2, "Reorganise", "Dwight: Reorganise", 2);
      var b = Verb(1, "Reorganise", "Dwight: Re-arrange", 3);
      a.ThreadId = "abc"; b.ThreadId = "abc";
      var items = Items(Input(new[] { a, b }, Undo((2, "x"), (1, "y"))));
      Assert.Equal("Reorganise:standing:1 | Reorganise:standing:1", Shape(items));
    }

    [Fact]
    public void The_users_records_inside_a_run_are_quiet_steps_and_never_split_it()
    {
      var items = Items(Input(new[] { Chat(2, "abc", "Built B1.", 2), Chat(1, "abc", "Built A1.", 4) },
        Undo((2, "b"), (90, "Move"), (1, "b"))));
      Assert.Single(items);
      Assert.Equal(2, items[0]["count"]!.GetValue<int>());
      var steps = items[0]["steps"]!.AsArray();
      Assert.Equal(3, steps.Count);
      Assert.True(steps[1]!["yours"]!.GetValue<bool>());
      Assert.Equal("Yours: Move", S(steps[1]!["line"]));
      Assert.Null(steps[1]!["action"]);
    }

    [Fact]
    public void The_users_records_between_events_are_one_quiet_line_of_at_most_three_names()
    {
      var items = Items(Input(new[] { Chat(2, "abc", "Built B1.", 2), Verb(1, "Simplify", "Dwight: Simplify (better logic)", 4) },
        Undo((2, "b"), (90, "Move"), (91, "Move"), (92, "Delete"), (93, "Wire"), (94, "Wire"), (1, "s"))));
      Assert.Equal("Build:standing:1 | yours | Simplify:standing:1", Shape(items));
      Assert.Equal("Yours: Move, Move, Delete and 2 more", S(items[1]["line"]));
      Assert.Equal(5, items[1]["ids"]!.AsArray().Count);
    }

    [Fact]
    public void The_users_newest_records_show_and_those_older_than_Dwights_oldest_do_not()
    {
      var items = Items(Input(new[] { Chat(1, "abc", "Built A1.", 4) }, Undo((90, "Move"), (1, "b"), (91, "Paste"))));
      Assert.Equal("yours | Build:standing:1", Shape(items));
      Assert.Equal("Yours: Move", S(items[0]["line"]));
    }

    [Fact]
    public void An_event_never_spans_You_are_here_so_a_row_has_one_state()
    {
      var stack = new StackSnapshot
      {
        UndoIds = new[] { G(1) }, UndoNames = new[] { "b" },
        RedoIds = new[] { G(2), G(3) }, RedoNames = new[] { "b", "b" },   // index 0 comes back next: the oldest of what was undone
      };
      var items = Items(Input(new[] { Chat(3, "abc", "Built C1.", 2), Chat(2, "abc", "Built B1.", 3), Chat(1, "abc", "Built A1.", 4) }, stack));
      Assert.Equal("Build:undone:2 | here | Build:standing:1", Shape(items));
      Assert.Equal(HistoryCore.HereLine, S(items[1]["line"]));
      Assert.Equal(S(items[0]["title"]), S(items[2]["title"]));
    }

    [Fact]
    public void You_are_here_shows_only_when_something_is_undone_and_a_change_Dwight_took_back_is_not_undone()
    {
      var stand = Items(Input(new[] { Chat(1, "abc", "Built A1.", 4) }, Undo((1, "b"))));
      Assert.DoesNotContain(stand, i => S(i["type"]) == "here");

      // A verdict's revert leaves its record on the redo list. Nobody undid it, so no marker, and its step stays in its event.
      var stack = new StackSnapshot { UndoIds = new[] { G(1) }, UndoNames = new[] { "b" }, RedoIds = new[] { G(2) }, RedoNames = new[] { "b" } };
      var items = Items(Input(new[] { Chat(2, "abc", "Built B1.", 2, reverted: true), Chat(1, "abc", "Built A1.", 4) }, stack));
      Assert.Equal("Build:standing:2", Shape(items));
      var first = items[0]["steps"]!.AsArray()[0]!;
      Assert.Equal("takenback", S(first["state"]));
      Assert.Equal(HistoryCore.WordTakenBack, S(first["word"]));
      Assert.Equal(HistoryCore.RevertedLine, S(first["reverted"]));
      Assert.Null(first["action"]);
    }

    [Fact]
    public void The_users_own_undone_record_draws_the_marker_too()
    {
      var stack = new StackSnapshot { UndoIds = new[] { G(1) }, UndoNames = new[] { "b" }, RedoIds = new[] { G(90) }, RedoNames = new[] { "Move" } };
      Assert.Equal("yours | here | Build:standing:1", Shape(Items(Input(new[] { Chat(1, "abc", "Built A1.", 4) }, stack))));
    }

    [Fact]
    public void Earlier_records_group_under_their_kind_alone_and_are_never_rewritten()
    {
      var a = new StoredChange { UndoId = G(2).ToString("D"), UndoName = "Dwight: set Number Slider \"Radius\" (4)", Kind = "Build", Scope = "Set a value?", Utc = Ago(2) };
      var b = new StoredChange { UndoId = G(1).ToString("D"), UndoName = "Dwight: build region", Kind = "Build", Scope = "Build these regions? A1, B1", Utc = Ago(3) };
      var items = Items(Input(new[] { a, b }, Undo()));
      Assert.Equal("Build:gone:2", Shape(items));
      Assert.Equal("", S(items[0]["title"]));
      Assert.Null(items[0]["threadId"]);
      Assert.Null(items[0]["openChat"]);
      Assert.Equal("Set a value?", a.Scope);   // read through, not written back
      Assert.Null(a.Done);
    }

    [Fact]
    public void A_chat_gone_from_the_store_is_named_by_its_kind_and_cannot_be_opened()
    {
      var items = Items(Input(new[] { Chat(1, "evicted", "Built A1.", 4) }, Undo((1, "b"))));
      Assert.Equal("", S(items[0]["title"]));
      Assert.Equal("evicted", S(items[0]["threadId"]));
      Assert.Null(items[0]["openChat"]);
    }

    [Fact]
    public void An_events_state_is_its_plainest_reading_and_taken_back_only_when_every_step_was()
    {
      var gone = Items(Input(new[] { Chat(2, "abc", "Built B1.", 2), Chat(1, "abc", "Built A1.", 3) }, Undo()));
      Assert.Equal("gone", S(gone[0]["state"]));
      Assert.Empty(gone[0]["actions"]!.AsArray());

      var unknown = Items(Input(new[] { Chat(0, "abc", "Built A1.", 3) }, Undo()));
      Assert.Equal("unknown", S(unknown[0]["state"]));
      Assert.Equal(HistoryCore.WordUnknown, S(unknown[0]["word"]));
      Assert.StartsWith("utc:", S(unknown[0]["key"]));

      var taken = Items(Input(new[] { Chat(1, "abc", "Built A1.", 3, reverted: true) }, Undo()));
      Assert.Equal("takenback", S(taken[0]["state"]));
      Assert.Equal(HistoryCore.WordTakenBack, S(taken[0]["word"]));
      Assert.Empty(taken[0]["actions"]!.AsArray());

      var mixed = Items(Input(new[] { Chat(2, "abc", "Built B1.", 2, reverted: true), Chat(1, "abc", "Built A1.", 3) }, Undo((1, "b"))));
      Assert.Equal("standing", S(mixed[0]["state"]));
    }

    [Fact]
    public void The_key_is_the_oldest_steps_id_so_it_is_stable_as_the_chat_grows()
    {
      var one = Items(Input(new[] { Chat(1, "abc", "Built A1.", 4) }, Undo((1, "b"))));
      var two = Items(Input(new[] { Chat(2, "abc", "Built B1.", 2), Chat(1, "abc", "Built A1.", 4) }, Undo((2, "b"), (1, "b"))));
      Assert.Equal(G(1).ToString("D"), S(one[0]["key"]));
      Assert.Equal(S(one[0]["key"]), S(two[0]["key"]));
    }

    [Fact]
    public void An_events_approval_is_its_most_common_and_each_step_keeps_its_own()
    {
      var auto = HistoryCore.ByAuto("Do not ask");
      var items = Items(Input(new[] { Chat(3, "abc", "c", 1, by: auto), Chat(2, "abc", "b", 2), Chat(1, "abc", "a", 3) }, Undo((3, "b"), (2, "b"), (1, "b"))));
      Assert.Equal(HistoryCore.ByYou, S(items[0]["by"]));   // the render's case: the newest step's alone would read "approved automatically"
      var steps = items[0]["steps"]!.AsArray();
      Assert.Equal(auto, S(steps[0]!["by"]));
      Assert.Equal(HistoryCore.ByYou, S(steps[1]!["by"]));
      Assert.Equal(HistoryCore.ByYou, S(steps[2]!["by"]));
    }

    [Fact]
    public void The_newest_steps_approval_wins_a_tie()
    {
      var auto = HistoryCore.ByAuto("Do not ask");
      var newestAuto = Items(Input(new[] { Chat(2, "abc", "b", 2, by: auto), Chat(1, "abc", "a", 3) }, Undo((2, "b"), (1, "b"))));
      Assert.Equal(auto, S(newestAuto[0]["by"]));
      var newestYou = Items(Input(new[] { Chat(2, "abc", "b", 2), Chat(1, "abc", "a", 3, by: auto) }, Undo((2, "b"), (1, "b"))));
      Assert.Equal(HistoryCore.ByYou, S(newestYou[0]["by"]));
    }

    [Fact]
    public void A_step_with_no_approval_says_none_and_the_users_steps_have_no_by()
    {
      var items = Items(Input(new[] { Chat(2, "abc", "b", 2, by: ""), Chat(1, "abc", "a", 3, by: "") }, Undo((2, "b"), (90, "Move"), (1, "b"))));
      Assert.Null(items[0]["by"]);
      var steps = items[0]["steps"]!.AsArray();
      Assert.Null(steps[0]!["by"]);
      Assert.Equal("Yours: Move", S(steps[1]!["line"]));
      Assert.False(steps[1]!.AsObject().ContainsKey("by"));
    }

    // ---------------------------------------------------------------- the buttons

    private static string Actions(JsonObject ev) =>
      string.Join(",", ev["actions"]!.AsArray().Select(a => S(a!["action"]) + "=" + Guid.Parse(S(a["id"])).ToString("D").Substring(0, 8)));

    [Fact]
    public void A_standing_event_offers_going_back_to_here_only_with_something_above_and_always_before_this()
    {
      var top = Items(Input(new[] { Chat(2, "abc", "b", 2), Chat(1, "abc", "a", 3) }, Undo((2, "b"), (1, "b"))));
      Assert.Equal("before=00000001", Actions(top[0]));   // its newest step is on top: nothing to go back to

      var under = Items(Input(new[] { Verb(3, "Simplify", "Dwight: Simplify (better logic)", 1), Chat(2, "abc", "b", 2), Chat(1, "abc", "a", 3) },
        Undo((3, "s"), (2, "b"), (1, "b"))));
      Assert.Equal("back=00000002,before=00000001", Actions(under[1]));
      Assert.Equal(HistoryCore.GoBackBeforeButton, S(under[1]["actions"]!.AsArray()[1]!["button"]));
    }

    [Fact]
    public void An_undone_event_offers_redo_to_its_newest_step_and_a_gone_one_nothing()
    {
      var stack = new StackSnapshot { RedoIds = new[] { G(1), G(2) }, RedoNames = new[] { "b", "b" } };
      var items = Items(Input(new[] { Chat(2, "abc", "b", 2), Chat(1, "abc", "a", 3) }, stack));
      Assert.Equal("here", S(items[1]["type"]));
      Assert.Equal("redo=00000002", Actions(items[0]));
      Assert.Equal(HistoryCore.RedoButton, S(items[0]["actions"]!.AsArray()[0]!["button"]));
    }

    [Fact]
    public void A_step_carries_its_own_action_and_its_id_for_the_tint()
    {
      var items = Items(Input(new[] { Verb(3, "Simplify", "Dwight: Simplify (better logic)", 1), Chat(2, "abc", "b", 2), Chat(1, "abc", "a", 3) },
        Undo((3, "s"), (2, "b"), (1, "b"))));
      var steps = items[1]["steps"]!.AsArray();
      Assert.Equal(G(2).ToString("D"), S(steps[0]!["id"]));
      Assert.Equal("back", S(steps[0]!["action"]!["action"]));
      Assert.Equal(G(1).ToString("D"), S(steps[1]!["action"]!["id"]));
      Assert.Null(items[0]["steps"]!.AsArray()[0]!["action"]);   // the newest change on the stack: nothing above it
    }

    [Fact]
    public void Going_back_before_includes_the_change_and_can_undo_the_oldest()
    {
      var stack = Undo((90, "Move"), (2, "Dwight: b"), (1, "Dwight: a"));
      var ids = new HashSet<Guid> { G(1), G(2) };
      var plan = HistoryCore.PlanBackBefore(G(1), stack, ids);
      Assert.True(plan.Back);
      Assert.Equal(3, plan.Steps);
      Assert.Equal(new[] { G(90), G(2), G(1) }, plan.ExpectedIds);
      Assert.Equal("This undoes 3 changes, 1 of them yours (Move) and 2 of Dwight's. Go back?", HistoryCore.Confirmation(plan));

      var only = HistoryCore.PlanBackBefore(G(1), Undo((1, "Dwight: a")), ids);
      Assert.Equal(1, only.Steps);
      Assert.Equal("This undoes 1 change, Dwight's. Go back?", HistoryCore.Confirmation(only));
      Assert.Null(HistoryCore.PlanBack(G(1), Undo((1, "Dwight: a")), ids));   // what could never be undone before this plan
      Assert.Null(HistoryCore.PlanBackBefore(G(7), stack, ids));
    }

    [Fact]
    public void The_row_a_walk_to_here_leaves_standing_is_not_among_the_tinted_ids()
    {
      var stack = Undo((3, "s"), (2, "b"), (1, "b"));
      var ids = new HashSet<Guid> { G(1), G(2), G(3) };
      Assert.Equal(new[] { G(3) }, HistoryCore.PlanBack(G(2), stack, ids).ExpectedIds);
      Assert.Equal(new[] { G(3), G(2) }, HistoryCore.PlanBackBefore(G(2), stack, ids).ExpectedIds);
    }

    // ---------------------------------------------------------------- the lines

    [Theory]
    [InlineData("Dwight: build region", "Build these regions? A1", "Built A1.")]
    [InlineData("Dwight: build region", "Build these regions? A1, B1", "Built one of A1, B1.")]   // one record is one region: it never claims them all
    [InlineData("Dwight: build region", "Build these regions?", "Built a region.")]
    [InlineData("Dwight: scaffold map", "Lay down a scaffold map?", "Laid down a scaffold map.")]
    [InlineData("Dwight: edit wiring", "Make these changes?", "Changed existing components.")]
    [InlineData("Dwight: tidy built regions", "Build these regions? A1", "Tidied the built regions.")]
    [InlineData("Dwight: set Number Slider \"Radius\" (4)", "Set a value?", "Set Radius.")]
    [InlineData("Dwight: set the Number Slider (4)", "Set a value?", "Set the Number Slider.")]
    [InlineData("Dwight: edit Loft", "Change this script?", "Edited Loft.")]
    [InlineData("Dwight: Reorganise", "Reorganise this definition?", "Reorganised the layout.")]
    [InlineData("Dwight: Reorganise (best-practice)", "Reorganise (best-practice)", "Reorganised the layout with labelled inputs and outputs.")]
    [InlineData("Dwight: Reorganise (incomplete)", "", "Began a Reorganise that did not finish.")]
    [InlineData("Dwight: Re-arrange", "Re-arrange", "Re-arranged the layout.")]
    [InlineData("Dwight: Re-arrange (incomplete)", "", "Began a re-arrange that did not finish.")]
    [InlineData("Dwight: Simplify (better logic)", "Simplify this selection?", "Simplified the logic.")]
    [InlineData("Dwight: To script (C#, alongside)", "", "Added a C# script alongside the selection.")]
    [InlineData("Dwight: To script (Python 3, replaced 5)", "", "Replaced 5 components with a Python 3 script.")]
    [InlineData("Dwight: To script (C#, replaced 1)", "", "Replaced 1 component with a C# script.")]
    [InlineData("Dwight: To script (Python 3, unverified draft)", "", "Added an unverified Python 3 draft script.")]
    public void A_stored_row_reads_as_what_happened(string undoName, string scope, string line)
    {
      var c = new StoredChange { UndoName = undoName, Scope = scope };
      Assert.Equal(line, HistoryCore.LineOf(c));
      Assert.DoesNotContain("?", HistoryCore.LineOf(c));
    }

    [Theory]
    [InlineData("Reorganise this definition?", "Reorganised this definition.")]
    [InlineData("Simplify this selection?", "Simplified this selection.")]
    [InlineData("Re-run the definition?", "Re-ran the definition.")]
    [InlineData("Lay down a scaffold map?", "Laid down a scaffold map.")]
    [InlineData("Build these regions? A1", "Built a region.")]
    [InlineData("Set a value?", "Set one value.")]
    [InlineData("Change this script?", "Changed a script.")]
    [InlineData("Make these changes?", "Changed existing components.")]
    [InlineData("Run this script?", "Ran a Python 3 script.")]
    [InlineData("Something else entirely?", "Something else entirely.")]
    public void A_stored_title_with_an_unknown_record_name_still_never_shows_its_question(string scope, string line)
    {
      Assert.Equal(line, HistoryCore.LineOf(new StoredChange { UndoName = "Move", Scope = scope }));
    }

    [Fact]
    public void The_sites_own_sentence_wins_and_Full_control_keeps_its_delta()
    {
      Assert.Equal("Built A1.", HistoryCore.LineOf(new StoredChange { Done = "Built A1.", UndoName = "Dwight: build region", Scope = "Build these regions? A1, B1" }));
      Assert.Equal("+3 components, -1 wire (what Grasshopper recorded)",
        HistoryCore.LineOf(new StoredChange { FullControl = true, Delta = "+3 components, -1 wire", Done = "ignored" }));
    }

    [Fact]
    public void The_standing_line_reads_as_clauses_and_never_says_a_question()
    {
      Assert.Equal("Standing: Built A1; Set Radius; Laid down a scaffold map; and 1 more.",
        HistoryCore.StandingLine(new[] { "Built A1.", "Set Radius.", "Laid down a scaffold map.", "Built B1." }));
      Assert.DoesNotContain("?", HistoryCore.StandingLine(new[] { "Set a value?" }));
    }

    [Theory]
    [InlineData(0, "now")]
    [InlineData(9, "9m")]
    [InlineData(180, "3h")]
    [InlineData(2880, "2d")]
    public void The_time_is_short(int minutes, string text) => Assert.Equal(text, HistoryCore.ShortAgo(Ago(minutes), Now));

    [Fact]
    public void A_time_that_cannot_be_read_is_blank() => Assert.Equal("", HistoryCore.ShortAgo("not a time", Now));

    // ---------------------------------------------------------------- the chat's name and place

    private static StoredMessage Me(string src, bool echo = false) => new StoredMessage { Who = "me", Src = src, Html = src, Echo = echo };

    [Fact]
    public void A_chats_name_is_the_first_thing_the_user_asked_with_whitespace_collapsed()
    {
      Assert.Equal("A tower whose floor plates twist", HistoryCore.ThreadTitle(new[]
      {
        new StoredMessage { Who = "dw", Html = "<p>Hello</p>" },
        new StoredMessage { Who = "ai", Src = "Here is what the run found." },
        Me("  A tower\r\n  whose   floor plates\ttwist "),
        Me("And then?"),
      }));
    }

    [Fact]
    public void An_approvals_echo_is_not_what_the_user_asked_with_the_flag_or_from_an_earlier_build()
    {
      Assert.Equal("Twist it", HistoryCore.ThreadTitle(new[]
      {
        Me(Dwight.Agent.ApprovalMode.ClickUserText, echo: true),
        Me(Dwight.Agent.ApprovalMode.AutoUserText),   // an earlier build stored these without the flag
        Me(Dwight.Agent.ApprovalMode.ClickUserText),
        Me("Twist it"),
      }));
    }

    [Fact]
    public void A_chat_with_no_message_yet_is_a_new_chat()
    {
      Assert.Equal(HistoryCore.NewChatTitle, HistoryCore.ThreadTitle(new[]
      {
        new StoredMessage { Who = "dw", Html = "<p>Looking at what Explain found.</p>" },
        new StoredMessage { Who = "ai", Src = "The region takes a curve." },   // a chat opened from a verb's Chat button
      }));
      Assert.Equal(HistoryCore.NewChatTitle, HistoryCore.ThreadTitle(null));
    }

    [Fact]
    public void A_long_first_message_is_bounded()
    {
      var title = HistoryCore.ThreadTitle(new[] { Me(new string('x', 5000)) });
      Assert.Equal(HistoryCore.MaxTitleChars, title.Length);
    }

    [Fact]
    public void A_chats_name_comes_from_a_real_persisted_thread()
    {
      var rec = new SessionRecord
      {
        Threads = new List<StoredThread>
        {
          new StoredThread { Id = "abc", Phase = "plan", Messages = new List<StoredMessage> { new StoredMessage { Who = "dw", Html = "<p>Hi</p>" }, Me("Twist the plates") } },
        },
        Changes = new List<StoredChange> { Chat(1, "abc", "Built A1.", 1) },
      };
      var read = SessionStoreCore.Read(SessionStoreCore.Write(rec)).Record;
      Assert.Equal("Twist the plates", HistoryCore.ThreadTitle(read.Threads[0].Messages));
      Assert.Equal("abc", read.Changes[0].ThreadId);
      Assert.Equal("Built A1.", read.Changes[0].Done);
    }

    [Fact]
    public void A_change_from_an_earlier_build_reads_without_a_chat_or_a_sentence_and_writes_neither()
    {
      var c = JsonSerializer.Deserialize<StoredChange>("{\"undoId\":\"\",\"undoName\":\"Dwight: build region\",\"kind\":\"Build\",\"scope\":\"Build these regions? A1\"}");
      Assert.Null(c.ThreadId);
      Assert.Null(c.Done);
      var back = JsonSerializer.Serialize(c);
      Assert.DoesNotContain("threadId", back);
      Assert.DoesNotContain("\"done\"", back);
      Assert.Equal("Built A1.", HistoryCore.LineOf(c));
    }

    [Fact]
    public void Speaking_moves_only_the_chat_to_the_front_and_keeps_the_rest_in_order()
    {
      var list = new List<string> { "explain", "chat-a", "reorganise", "chat-b" };
      Assert.True(HistoryCore.MoveToFront(list, x => x == "chat-b"));
      Assert.Equal(new[] { "chat-b", "explain", "chat-a", "reorganise" }, list);
      Assert.True(HistoryCore.MoveToFront(list, x => x == "chat-b"));   // already on top: nothing else moves
      Assert.Equal(new[] { "chat-b", "explain", "chat-a", "reorganise" }, list);
      Assert.False(HistoryCore.MoveToFront(list, x => x == "gone"));
      Assert.False(HistoryCore.MoveToFront<string>(null, x => true));
    }

    // ---------------------------------------------------------------- the fixture

    private static List<StoredChange> ScenarioChanges() => new List<StoredChange>
    {
      Chat(8, "def", "Placed a Square, Spot 5, beside empty_number.", 2),
      Verb(7, "Simplify", "Dwight: Simplify (better logic)", 3),
      Chat(6, "abc", "Set Radius.", 5, reverted: true),
      Chat(5, "abc", "Built C1.", 6, by: HistoryCore.ByAuto("Do not ask")),
      Verb(4, "Reorganise", "Dwight: Reorganise (best-practice)", 8),
      Chat(3, "abc", "Added 2 wires, deleted 1 component.", 9),
      Chat(2, "abc", "Built A1.", 11),
      Chat(1, "abc", "Laid down a scaffold map.", 12),
      new StoredChange { UndoId = G(21).ToString("D"), UndoName = "Dwight: build region", Kind = "Build", Scope = "Build these regions? X1, X2", Utc = Ago(600) },
      new StoredChange { UndoId = G(20).ToString("D"), UndoName = "Dwight: edit wiring", Kind = "Build", Scope = "Make these changes?", Utc = Ago(610), Reverted = true },
      new StoredChange { UndoId = "", UndoName = "Dwight: Re-arrange", Kind = "Reorganise", Scope = "Re-arrange", Utc = Ago(3000) },
      new StoredChange { UndoId = G(30).ToString("D"), UndoName = "Dwight: run a script", Kind = "Build", Scope = "Run this script?", ThreadId = "abc", FullControl = true, Delta = "+4 components, +6 wires, 0 groups", Utc = Ago(3100), ApprovedBy = HistoryCore.ByYou },
    };

    private static readonly (int, string)[] StandingStack =
    {
      (5, "Dwight: build region"), (92, "Delete"), (4, "Dwight: Reorganise (best-practice)"), (3, "Dwight: edit wiring"), (91, "Move"),
      (2, "Dwight: build region"), (1, "Dwight: scaffold map"),
    };

    /// <summary>What the file holds: one scripted file, each state of the view as the host would send it.</summary>
    private static JsonObject Fixture()
    {
      var ids = new HashSet<Guid>(ScenarioChanges().Where(c => c.Id != null).Select(c => c.Id.Value));

      // Nothing undone: the two newest changes were taken off by a later file state, so this stack holds the rest.
      var closed = Undo(StandingStack);
      var closedChanges = ScenarioChanges().Skip(2).ToList();

      var undone = Undo(StandingStack);
      undone.RedoIds = new[] { G(7), G(8) };
      undone.RedoNames = new[] { "Dwight: Simplify (better logic)", "Dwight: build region" };

      var plan = HistoryCore.PlanBackBefore(G(3), closed, ids);
      // What the walk's result says, read as HistoryWalk reads it: from the stack the walk leaves.
      var left = Undo(StandingStack.Skip(plan.Steps).ToArray());
      var result = HistoryCore.Result(true, plan.Steps, closedChanges.Where(c => HistoryCore.StateOf(c, left) == ChangeState.Standing).Select(HistoryCore.LineOf).ToList());

      return new JsonObject
      {
        ["note"] = "Rendered by HistoryTimelineTests from a scripted scenario through HistoryCore.TimelineView; it fails if this file differs. The shell's fixtures read it, so they hold only what the host can send (#1139). Regenerate with DWIGHT_WRITE_FIXTURES=1.",
        ["closed"] = HistoryCore.TimelineView(Input(closedChanges, closed), null, null, true, null),
        ["undone"] = HistoryCore.TimelineView(Input(ScenarioChanges(), undone), null, null, true, null),
        ["confirm"] = HistoryCore.TimelineView(Input(closedChanges, closed), HistoryCore.Confirmation(plan), plan.ExpectedIds, true, null),
        ["note-result"] = HistoryCore.TimelineView(Input(closedChanges, closed), null, null, true, result),
        ["empty"] = HistoryCore.TimelineView(Input(Array.Empty<StoredChange>(), new StackSnapshot()), null, null, true, null),
        ["dockRows"] = DockListScenario(),
      };
    }

    /// <summary>Brief DG: the dock's list as the host sends it, every row made by <see cref="DockRows.Row"/>. The list shows no cost on any row, a stopped or
    /// failed one included (Brief BQ section 9), so the fixture holds those states with the fields the shell reads and no cost among them.</summary>
    private static JsonArray DockListScenario() => new JsonArray(
      DockRows.Row("Build", "A tower whose floor plates twist", true, null, "ok", "4m ago", null),
      DockRows.Row("Explain", "1,087 components", false, null, "stopped", "9m ago", null),
      DockRows.Row("Simplify", "better logic", false, null, "ok", "22m ago", null),
      DockRows.Row("Diagnose", "3 faults", false, null, "failed", "38m ago", null),
      DockRows.Row("Explain", "8 components", false, null, "ok", "3h ago", "site_v2.gh"));

    private static string RepoRoot()
    {
      var root = new DirectoryInfo(AppContext.BaseDirectory);
      while (root != null && !File.Exists(Path.Combine(root.FullName, "Dwight.sln"))) root = root.Parent;
      Assert.True(root != null, "the repository root (Dwight.sln) could not be found from " + AppContext.BaseDirectory);
      return root.FullName;
    }

    // The shell writes no word of its own, so the step count arrives as words, and a single change has none to show.
    [Fact]
    public void An_events_count_arrives_as_words()
    {
      var items = Fixture()["closed"]!["items"]!.AsArray().Where(i => S(i!["type"]) == "event").ToList();
      Assert.Contains(items, i => i!["count"]!.GetValue<int>() == 3 && S(i["countWord"]) == "3 steps");
      Assert.Contains(items, i => i!["count"]!.GetValue<int>() == 1 && i["countWord"] == null);
    }

    [Fact]
    public void The_shells_fixture_file_is_the_real_output_of_the_view()
    {
      var text = Fixture().ToJsonString(new JsonSerializerOptions
      {
        WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
      }).Replace("\r\n", "\n") + "\n";
      var path = Path.Combine(RepoRoot(), "tools", "ui-history-events.json");
      if (Environment.GetEnvironmentVariable("DWIGHT_WRITE_FIXTURES") == "1") File.WriteAllText(path, text, new System.Text.UTF8Encoding(false));
      Assert.True(File.Exists(path), "tools/ui-history-events.json is missing; run this test with DWIGHT_WRITE_FIXTURES=1");
      Assert.True(File.ReadAllText(path).Replace("\r\n", "\n") == text, "tools/ui-history-events.json is not the view's real output; run this test with DWIGHT_WRITE_FIXTURES=1 and read the diff");
    }

    [Fact]
    public void The_view_serialises_the_way_the_host_sends_it()
    {
      var sent = Fixture()["confirm"]!.ToJsonString();   // DwightDock calls ToJsonString with no options
      var read = JsonNode.Parse(sent)!;
      Assert.NotEmpty(read["confirmIds"]!.AsArray());
      Assert.NotEmpty(read["items"]!.AsArray());
    }

    [Fact]
    public void The_fixtures_scenario_has_every_shape_the_shell_must_draw()
    {
      var undone = Fixture()["undone"]!["items"]!.AsArray().Select(i => i!.AsObject()).ToList();
      Assert.Contains(undone, i => S(i["type"]) == "here");
      Assert.Contains(undone, i => S(i["type"]) == "yours");
      var states = undone.Where(i => S(i["type"]) == "event").Select(i => S(i["state"])).ToList();
      foreach (var want in new[] { "standing", "undone", "gone", "unknown" }) Assert.Contains(want, states);
      var closed = Fixture()["closed"]!["items"]!.AsArray().Select(i => i!.AsObject()).ToList();
      // the sandwich: one chat's name on the rows either side of Reorganise, and once more below an earlier build's records
      Assert.Equal(3, closed.Count(i => S(i["verb"]) == "Build" && S(i["title"]) == "A tower whose floor plates twist" && i["steps"]!.AsArray().Count > 0));
    }

    [Fact]
    public void No_displayed_line_in_the_fixture_holds_a_question_mark()
    {
      foreach (var view in new[] { "closed", "undone", "confirm", "note-result", "empty" })
        foreach (var item in Fixture()[view]!["items"]!.AsArray())
        {
          Assert.DoesNotContain("?", S(item!["line"]) ?? "");
          Assert.DoesNotContain("?", S(item["title"]) ?? "");
          foreach (var step in item["steps"]?.AsArray() ?? new JsonArray()) Assert.DoesNotContain("?", S(step!["line"]));
        }
    }
  }
}
