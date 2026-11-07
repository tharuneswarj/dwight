using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using Dwight.Session;

namespace Dwight.Session.Tests
{
  /// <summary>Brief AP: History's core, on modelled stacks. Index 0 of each list is the newest record, as Grasshopper.dll's
  /// IL has it (PushUndoRecord and PushRedoRecord insert at 0; FirstUndoName reads index 0).</summary>
  public class HistoryCoreTests
  {
    // Brief BK: the dock is measured on tools/ui-history-words.json. Every entry is computed here from the product's own
    // code, so a sentence changed in HistoryCore fails this test until the fixture, and the fit it measures, follow.
    [Fact]
    public void FixtureWords_AreTheProductsOwn()
    {
      var root = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
      while (root != null && !System.IO.File.Exists(System.IO.Path.Combine(root.FullName, "Dwight.sln"))) root = root.Parent;
      Assert.True(root != null, "the repository root (Dwight.sln) could not be found from " + AppContext.BaseDirectory);
      var file = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(System.IO.Path.Combine(root.FullName, "tools", "ui-history-words.json"))).RootElement;

      // A stack: index 0 newest. `names` are the user's records in the way, `dwights` the Dwight records after them.
      Guid[] Ids(int n) => Enumerable.Range(0, n).Select(_ => Guid.NewGuid()).ToArray();
      WalkPlan Back(string[] names, int dwights)
      {
        var ids = Ids(names.Length + dwights + 1);
        var all = names.Concat(Enumerable.Repeat("Dwight: build region", dwights + 1)).ToArray();
        var s = new StackSnapshot { UndoIds = ids, UndoNames = all };
        return HistoryCore.PlanBack(ids[ids.Length - 1], s, new HashSet<Guid>(ids.Skip(names.Length)));
      }
      var redoIds = Ids(2);
      var redo = HistoryCore.PlanRedo(redoIds[1], new StackSnapshot { RedoIds = redoIds, RedoNames = new[] { "Dwight: a", "Dwight: b" } }, new HashSet<Guid>(redoIds), null);
      var fullControl = new StoredChange { FullControl = true, Delta = "+4 components, +6 wires, 0 groups" };
      var chat = SpendMath.Sum(new[] {
        new StoredSpend { Cents = 12.3, InputTokens = 41000, OutputTokens = 6000 }, new StoredSpend { Cents = 0 }, new StoredSpend { Cents = 0 } });

      var want = new Dictionary<string, string>
      {
        ["standing"] = HistoryCore.WordStanding, ["undone"] = HistoryCore.WordUndone, ["gone"] = HistoryCore.WordGone, ["unknown"] = HistoryCore.WordUnknown,
        ["reverted"] = HistoryCore.RevertedLine, ["goBack"] = HistoryCore.GoBackButton, ["redo"] = HistoryCore.RedoButton, ["empty"] = HistoryCore.EmptyLine,
        ["confirmBackButton"] = HistoryCore.ConfirmBackButton, ["confirmRedoButton"] = HistoryCore.ConfirmRedoButton, ["cancel"] = HistoryCore.CancelButton,
        ["byYou"] = HistoryCore.ByYou,
        ["byAutoLongest"] = HistoryCore.ByAuto("Ask for scripts and deletions only"),
        ["scopeBuild"] = HistoryCore.RowLine(new StoredChange { UndoName = "Dwight: build region", Scope = HistoryCore.CardScope("Build these regions?", new[] { "A1", "B1" }) }),
        ["scopeVerb"] = HistoryCore.RowLine(new StoredChange { UndoName = "Dwight: Reorganise (best-practice)" }),
        ["takenBack"] = HistoryCore.WordTakenBack, ["goBefore"] = HistoryCore.GoBackBeforeButton, ["here"] = HistoryCore.HereLine,
        ["heading"] = HistoryCore.HeadingTimeline, ["expandAll"] = HistoryCore.ExpandAll, ["collapseAll"] = HistoryCore.CollapseAll, ["back"] = HistoryCore.BackToHistory,
        ["liveWaiting"] = HistoryCore.LiveWaitingWords, ["liveRunning"] = HistoryCore.LiveRunningWords, ["walkBusy"] = HistoryCore.WalkBusyLine,
        ["liveWaiting"] = HistoryCore.LiveWaitingWords, ["liveRunning"] = HistoryCore.LiveRunningWords, ["openChat"] = HistoryCore.OpenChatButton, ["newChat"] = HistoryCore.NewChatTitle, ["historyEmpty"] = HistoryCore.HistoryEmpty,
        ["lineFullControl"] = HistoryCore.RowLine(fullControl),
        ["confirmBack"] = HistoryCore.Confirmation(Back(new[] { "Move", "Move", "Delete" }, 2)),
        ["confirmLong"] = HistoryCore.Confirmation(Back(new[] { "Move", "Move", "Delete", "Move", "Wire", "Wire", "Move", "Delete", "Move", "Move" }, 2)),
        ["confirmRedo"] = HistoryCore.Confirmation(redo),
        ["result"] = HistoryCore.Result(true, 5, new[] { "Built A1.", "Set Radius.", "Added 2 wires, deleted 1 component.", "Built B1.", "Laid down a scaffold map." }),
        ["resultNone"] = HistoryCore.Result(true, 1, Array.Empty<string>()),
        ["moved"] = HistoryCore.MovedLine(true),
        ["stopped"] = HistoryCore.StoppedLine(true, 2, 5, "Dwight: build region", "Move"),
        ["stepFailed"] = HistoryCore.StepFailedLine(true, 2, 5, "Dwight: build region"),
        ["redoCrosses"] = HistoryCore.RedoCrossesRevertLine,
        ["noDocument"] = HistoryCore.NoDocumentLine,
        ["chatMeta"] = SpendMath.ChatLine(chat),
      };
      foreach (var kv in want) Assert.True(file.TryGetProperty(kv.Key, out var got) && got.GetString() == kv.Value, kv.Key + " in tools/ui-history-words.json is not the product's: " + kv.Value);
      foreach (var p in file.EnumerateObject()) Assert.True(p.Name == "note" || want.ContainsKey(p.Name), p.Name + " in tools/ui-history-words.json has no source here, so nothing holds it to the product.");
    }

    private static readonly Guid D1 = Guid.Parse("11111111-1111-1111-1111-111111111111");   // Dwight: the first change
    private static readonly Guid D2 = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid D3 = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid U1 = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");   // the user's Move
    private static readonly Guid U2 = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");   // the user's Delete

    private static StoredChange Change(Guid? id, string scope = "Build these regions? A1") =>
      new StoredChange { UndoId = id?.ToString("D") ?? "", UndoName = "Dwight: build region", Kind = "Build", Scope = scope };

    // Three Dwight changes with the user's Move between the second and the third and a Delete on top:
    // pushed D1, D2, Move, D3, Delete -> the undo list reads newest first.
    private static StackSnapshot Stack() => new StackSnapshot
    {
      UndoIds = new[] { U2, D3, U1, D2, D1 },
      UndoNames = new[] { "Delete", "Dwight: build region", "Move", "Dwight: set Count", "Dwight: scaffold map" },
    };

    private static readonly ISet<Guid> Dwight = new HashSet<Guid> { D1, D2, D3 };

    [Fact]
    public void A_change_is_standing_undone_gone_or_of_unknown_position_from_the_live_lists()
    {
      var s = new StackSnapshot { UndoIds = new[] { D2 }, RedoIds = new[] { D3 } };
      Assert.Equal(ChangeState.Standing, HistoryCore.StateOf(Change(D2), s));
      Assert.Equal(ChangeState.Undone, HistoryCore.StateOf(Change(D3), s));
      Assert.Equal(ChangeState.Gone, HistoryCore.StateOf(Change(D1), s));       // dropped, reopened, or unreachable
      Assert.Equal(ChangeState.Unknown, HistoryCore.StateOf(Change(null), s));  // recorded without an id
      Assert.Equal(ChangeState.Gone, HistoryCore.StateOf(Change(D1), new StackSnapshot()));   // a reopened file's empty stack
    }

    [Fact]
    public void Going_back_to_the_first_change_counts_every_record_above_it_and_names_the_users()
    {
      var p = HistoryCore.PlanBack(D1, Stack(), Dwight);
      Assert.Equal(4, p.Steps);
      Assert.Equal(new[] { U2, D3, U1, D2 }, p.ExpectedIds);
      Assert.Equal(new[] { "Delete", "Move" }, p.YourNames);
      Assert.Equal(2, p.Dwights);
      Assert.Equal("This undoes 4 changes, 2 of them yours (Delete, Move) and 2 of Dwight's. Go back?", HistoryCore.Confirmation(p));
    }

    [Fact]
    public void The_newest_change_offers_no_walk_and_a_change_not_standing_offers_none()
    {
      var s = new StackSnapshot { UndoIds = new[] { D3, D2 }, UndoNames = new[] { "a", "b" } };
      Assert.Null(HistoryCore.PlanBack(D3, s, Dwight));   // nothing above it
      Assert.Null(HistoryCore.PlanBack(D1, s, Dwight));   // not on the list
      Assert.NotNull(HistoryCore.PlanBack(D2, s, Dwight));
    }

    [Fact]
    public void Redoing_to_a_change_includes_it_and_refuses_to_cross_one_Dwight_took_back()
    {
      var s = new StackSnapshot { RedoIds = new[] { D2, U1, D3 }, RedoNames = new[] { "Dwight: set Count", "Move", "Dwight: build region" } };
      var p = HistoryCore.PlanRedo(D3, s, Dwight, new HashSet<Guid>());
      Assert.Equal(3, p.Steps);
      Assert.Null(p.Refusal);
      Assert.Equal("This redoes 3 changes, 1 of them yours (Move) and 2 of Dwight's. Redo?", HistoryCore.Confirmation(p));

      var crossing = HistoryCore.PlanRedo(D3, s, Dwight, new HashSet<Guid> { D2 });
      Assert.Equal("This would redo a change Dwight took back because its check failed, so nothing was redone.", crossing.Refusal);
      Assert.Null(HistoryCore.PlanRedo(D1, s, Dwight, null));
    }

    [Fact]
    public void The_confirmation_reads_right_for_one_change_and_for_all_of_one_kind()
    {
      var one = HistoryCore.PlanBack(D2, new StackSnapshot { UndoIds = new[] { U1, D2 }, UndoNames = new[] { "Move", "x" } }, Dwight);
      Assert.Equal("This undoes 1 change, yours (Move). Go back?", HistoryCore.Confirmation(one));
      var dw = HistoryCore.PlanBack(D1, new StackSnapshot { UndoIds = new[] { D3, D2, D1 }, UndoNames = new[] { "a", "b", "c" } }, Dwight);
      Assert.Equal("This undoes 2 changes, all of them Dwight's. Go back?", HistoryCore.Confirmation(dw));
      var dwOne = HistoryCore.PlanBack(D1, new StackSnapshot { UndoIds = new[] { D2, D1 }, UndoNames = new[] { "a", "b" } }, Dwight);
      Assert.Equal("This undoes 1 change, Dwight's. Go back?", HistoryCore.Confirmation(dwOne));
      var users = HistoryCore.PlanBack(D1, new StackSnapshot { UndoIds = new[] { U2, U1, D1 }, UndoNames = new[] { "Delete", "", "c" } }, Dwight);
      Assert.Equal("This undoes 2 changes, all of them yours (Delete, unnamed). Go back?", HistoryCore.Confirmation(users));
    }

    [Fact]
    public void A_walk_refuses_when_the_stack_moved_between_the_count_and_the_walk()
    {
      var counted = Stack();
      var same = Stack();
      var moved = Stack();
      moved.UndoIds = new[] { Guid.NewGuid() }.Concat(counted.UndoIds).ToArray();   // the user clicked in Grasshopper
      Assert.True(HistoryCore.SameStack(counted, same));
      Assert.False(HistoryCore.SameStack(counted, moved));
      Assert.False(HistoryCore.SameStack(counted, new StackSnapshot { UndoIds = counted.UndoIds, RedoIds = new[] { D1 } }));
      Assert.Equal("The undo history changed after this was counted, so nothing was undone. Look at the list again and choose.",
        HistoryCore.MovedLine(true));
      Assert.Equal("The undo history changed after this was counted, so nothing was redone. Look at the list again and choose.",
        HistoryCore.MovedLine(false));
    }

    [Fact]
    public void Each_step_checks_the_record_on_top_by_id_and_name()
    {
      var p = HistoryCore.PlanBack(D1, Stack(), Dwight);
      Assert.True(HistoryCore.StepMatches(p, 0, U2, "Delete"));
      Assert.False(HistoryCore.StepMatches(p, 0, U2, "Move"));     // same id, another name
      Assert.False(HistoryCore.StepMatches(p, 1, U1, "Move"));     // another record on top
      Assert.False(HistoryCore.StepMatches(p, 4, D1, "x"));        // past the plan
      Assert.Equal("Stopped after 1 of 4 steps, because the next change on the undo history was \"Move\", not the \"Dwight: build region\" that was counted.",
        HistoryCore.StoppedLine(true, 1, 4, "Dwight: build region", "Move"));
      Assert.Equal("Stopped after 0 of 1 step, because Grasshopper could not redo \"Move\".", HistoryCore.StepFailedLine(false, 0, 1, "Move"));
    }

    [Fact]
    public void The_result_says_how_far_it_went_and_what_of_Dwights_stands()
    {
      Assert.Equal("Went back 5 changes. Standing: Built A1; Set Radius.",
        HistoryCore.Result(true, 5, new[] { "Built A1.", "Set Radius." }));
      Assert.Equal("Redid 1 change. None of Dwight's changes are standing.", HistoryCore.Result(false, 1, new string[0]));
      Assert.Equal("Standing: a; b; c; and 2 more.", HistoryCore.StandingLine(new[] { "a", "b", "c", "d", "e" }));
    }

    [Fact]
    public void The_words_are_pinned()
    {
      Assert.Equal(new[] { "standing", "undone", "gone", "position unknown" },
        new[] { ChangeState.Standing, ChangeState.Undone, ChangeState.Gone, ChangeState.Unknown }.Select(HistoryCore.StateWord));
      Assert.Equal("Dwight took this back because its check failed.", HistoryCore.RevertedLine);
      Assert.Equal("approved by you", HistoryCore.ByYou);
      Assert.Equal("approved automatically (Do not ask)", HistoryCore.ByAuto("Do not ask"));
      Assert.Equal("Go back to here", HistoryCore.GoBackButton);
      Assert.Equal("Redo to here", HistoryCore.RedoButton);
      Assert.Equal("Dwight has not changed this file yet.", HistoryCore.EmptyLine);
      Assert.Equal(("Go back", "Redo", "Cancel"), (HistoryCore.ConfirmBackButton, HistoryCore.ConfirmRedoButton, HistoryCore.CancelButton));
    }

    [Fact]
    public void A_full_control_row_shows_its_delta_and_says_it_is_what_Grasshopper_recorded()
    {
      var c = new StoredChange { UndoId = D1.ToString(), Scope = "ignored", FullControl = true, Delta = "+3 components, -1 wire" };
      Assert.Equal("+3 components, -1 wire (what Grasshopper recorded)", HistoryCore.RowLine(c));
      Assert.Equal("Built A1.", HistoryCore.RowLine(Change(D1)));
    }

    [Fact]
    public void A_scope_is_the_cards_title_and_regions_or_the_records_own_name()
    {
      Assert.Equal("Build these regions? A1, B1", HistoryCore.CardScope("Build these regions?", new[] { "A1", " B1 ", "" }));
      Assert.Equal("Set a value?", HistoryCore.CardScope("Set a value?", null));
      Assert.Equal("Reorganise (best-practice)", HistoryCore.ScopeFromRecordName("Dwight: Reorganise (best-practice)"));
      Assert.Equal("Move", HistoryCore.ScopeFromRecordName("Move"));
    }

    // ---------------------------------------------------------------- the store

    [Fact]
    public void Changes_persist_with_their_ids_and_an_older_file_reads_with_none()
    {
      var rec = new SessionRecord
      {
        DocumentId = "66fd7275-7717-4065-90eb-483736e9296a",
        Changes = new List<StoredChange> { Change(D2), new StoredChange { UndoId = D1.ToString(), UndoName = "n", Kind = "Simplify", Reverted = true } },
      };
      var json = SessionStoreCore.Write(rec);
      var read = SessionStoreCore.Read(json);
      Assert.Equal(StoreReadOutcome.Ok, read.Outcome);
      Assert.Equal(2, read.Record.Changes.Count);
      Assert.Equal(D2, read.Record.Changes[0].Id);
      Assert.True(read.Record.Changes[1].Reverted);
      Assert.False(read.Record.Changes[0].Reverted);
      Assert.True(SessionStoreCore.HasContent(new SessionRecord { Changes = new List<StoredChange> { Change(D1) } }));

      // A file from before History: no "changes" key at all.
      var older = SessionStoreCore.Read("{\"schema\":1,\"minor\":4,\"documentId\":\"x\",\"commands\":[]}");
      Assert.Equal(StoreReadOutcome.Ok, older.Outcome);
      Assert.Empty(older.Record.Changes);
    }

    [Fact]
    public void A_change_without_an_id_reads_as_position_unknown_and_a_newer_field_survives()
    {
      var json = "{\"schema\":1,\"minor\":9,\"documentId\":\"x\",\"changes\":[{\"undoName\":\"Dwight: Reorganise\",\"kind\":\"Reorganise\",\"later\":7}]}";
      var read = SessionStoreCore.Read(json);
      var c = Assert.Single(read.Record.Changes);
      Assert.Null(c.Id);
      Assert.Equal(ChangeState.Unknown, HistoryCore.StateOf(c, new StackSnapshot()));
      Assert.Contains("\"later\":7", SessionStoreCore.Write(read.Record));   // a newer minor's field is kept
    }

    [Fact]
    public void The_changes_list_is_capped_newest_first()
    {
      var many = Enumerable.Range(0, HistoryCore.MaxChanges + 5).Select(i => new StoredChange { UndoName = "c" + i }).ToList();
      var read = SessionStoreCore.Read(SessionStoreCore.Write(new SessionRecord { Changes = many }));
      Assert.Equal(HistoryCore.MaxChanges, read.Record.Changes.Count);
      Assert.Equal("c0", read.Record.Changes[0].UndoName);
    }
  }
}
