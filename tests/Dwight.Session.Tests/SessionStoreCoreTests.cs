using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using Dwight.Session;

namespace Dwight.Session.Tests
{
  /// <summary>
  /// WP-1 step 1: the durable store's GH-free serialization core (ADR-067/ADR-091).
  ///
  /// The governing asymmetry, and why these tests are written the way they are: the LABEL CACHE is correctly
  /// LOSSY (any doubt -> drop -> one re-bill), but this store holds the USER'S OWN WORK - command history and
  /// Scaffold threads. So "degrade to empty" is right for a file we cannot understand, and WRONG as a licence
  /// to overwrite one. Hence the two outcomes below: Empty (nothing usable, safe to replace) versus
  /// FutureVersion (written by a newer Dwight - readable as empty, but the file is PRESERVED, never clobbered).
  /// </summary>
  public class SessionStoreCoreTests
  {
    private static StoredCommand Cmd(string action, string time = "2026-07-22T10:00:00Z") =>
      new StoredCommand
      {
        Action = action,
        Subtitle = "3 components",
        State = "ok",
        SourceGuids = new List<string> { "11111111-1111-1111-1111-111111111111" },
        TimeUtc = time,
        CostLine = "Sonnet · 1.2s",
        LiveClass = "content",
        Origin = @"C:\work\site.gh",
        Script = new List<StoredOp>
        {
          new StoredOp { Op = "SetMarkdown", Args = new List<string> { "# hello" } },
          new StoredOp { Op = "SetStatus",   Args = new List<string> { "Sonnet · 1.2s" } },
        },
      };

    private static SessionRecord Rec(params StoredCommand[] cmds) =>
      new SessionRecord
      {
        DocumentId = "66fd7275-7717-4065-90eb-483736e9296a",
        Labels = new List<string> { @"C:\work\site.gh" },
        Fingerprint = "ABCD",
        Commands = cmds.ToList(),
      };

    // ---------------------------------------------------------------- round trip

    [Fact]
    public void RoundTrips_a_command_with_its_script()
    {
      var json = SessionStoreCore.Write(Rec(Cmd("Explain")));
      var read = SessionStoreCore.Read(json);

      Assert.Equal(StoreReadOutcome.Ok, read.Outcome);
      var c = Assert.Single(read.Record.Commands);
      Assert.Equal("Explain", c.Action);
      Assert.Equal("ok", c.State);
      Assert.Equal("content", c.LiveClass);
      Assert.Equal("11111111-1111-1111-1111-111111111111", Assert.Single(c.SourceGuids));
      Assert.Equal(@"C:\work\site.gh", c.Origin);   // #179: provenance survives the store
      Assert.Equal(2, c.Script.Count);
      Assert.Equal("SetMarkdown", c.Script[0].Op);
      Assert.Equal("# hello", Assert.Single(c.Script[0].Args));
    }

    [Fact]
    public void A_command_thread_id_round_trips()
    {
      // #182: a Scaffold row points at its thread by id.
      var cmd = Cmd("Scaffold");
      cmd.ThreadId = "thread-abc";
      var read = SessionStoreCore.Read(SessionStoreCore.Write(Rec(cmd)));
      Assert.Equal("thread-abc", Assert.Single(read.Record.Commands).ThreadId);
    }

    [Fact]
    public void RoundTrips_a_scaffold_thread()
    {
      var rec = Rec();
      rec.Threads = new List<StoredThread>
      {
        new StoredThread
        {
          Id = "t1",
          Phase = "scaffold",
          ScaffoldMap = "A1,A2,B1",
          BuildProgress = "A1",
          Messages = new List<StoredMessage>
          {
            new StoredMessage { Who = "me", Html = "build me a facade", Utc = "2026-07-22T10:00:00Z" },
            new StoredMessage { Who = "dw", Html = "here is the plan",  Utc = "2026-07-22T10:00:05Z" },
          },
        },
      };

      var read = SessionStoreCore.Read(SessionStoreCore.Write(rec));
      var t = Assert.Single(read.Record.Threads);
      Assert.Equal("scaffold", t.Phase);
      Assert.Equal("A1,A2,B1", t.ScaffoldMap);
      Assert.Equal("A1", t.BuildProgress);
      Assert.Equal(2, t.Messages.Count);
      Assert.Equal("build me a facade", t.Messages[0].Html);
    }

    [Fact]
    public void Where_the_plugin_laid_a_region_survives_a_write_and_an_older_file_reads_with_none()
    {
      var id = Guid.NewGuid();
      var rec = Rec(Cmd("Explain"));
      rec.RegionPlaces = new List<StoredRegionPlace>();
      RegionPlacesCore.Note(rec.RegionPlaces, id, 120.5, -40);

      var read = SessionStoreCore.Read(SessionStoreCore.Write(rec)).Record;

      Assert.True(RegionPlacesCore.IsAt(read.RegionPlaces, id, 120.5, -40));
      Assert.Null(SessionStoreCore.Read(SessionStoreCore.Write(Rec(Cmd("Explain")))).Record.RegionPlaces);
    }

    /// <summary>Brief DO, item 8: the id a note remembers, the same one the row's ledger line carries. It is additive, so an older row reads with none and is not lost.</summary>
    [Fact]
    public void A_rows_run_id_survives_a_write_and_an_older_row_reads_with_none()
    {
      var cmd = Cmd("Explain");
      cmd.RunId = "run-1";
      Assert.Equal("run-1", SessionStoreCore.Read(SessionStoreCore.Write(Rec(cmd))).Record.Commands[0].RunId);

      var old = SessionStoreCore.Read("{\"schema\":1,\"minor\":8,\"documentId\":\"d\",\"commands\":[{\"action\":\"Explain\"}]}").Record;
      Assert.Equal("Explain", old.Commands[0].Action);
      Assert.Null(old.Commands[0].RunId);
    }

    [Fact]
    public void Write_stamps_the_current_schema_version()
    {
      var read = SessionStoreCore.Read(SessionStoreCore.Write(Rec(Cmd("Explain"))));
      Assert.Equal(SessionStoreCore.SchemaMajor, read.Record.Schema);
      Assert.Equal(SessionStoreCore.SchemaMinor, read.Record.Minor);
    }

    // ---------------------------------------------------------------- degrade to empty, never throw

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{")]                                     // truncated
    [InlineData("{\"schema\":1,\"commands\":[{\"acti")]    // truncated mid-record
    [InlineData("[]")]                                    // right syntax, wrong shape
    [InlineData("null")]
    public void Unreadable_input_reads_as_Empty_and_never_throws(string json)
    {
      var read = SessionStoreCore.Read(json);
      Assert.Equal(StoreReadOutcome.Empty, read.Outcome);
      Assert.NotNull(read.Record);
      Assert.Empty(read.Record.Commands);
      Assert.Empty(read.Record.Threads);
    }

    [Fact]
    public void Read_never_returns_null_collections()
    {
      // a hand-written minimal file: every collection absent
      var read = SessionStoreCore.Read("{\"schema\":1}");
      Assert.NotNull(read.Record.Commands);
      Assert.NotNull(read.Record.Threads);
      Assert.NotNull(read.Record.Labels);
    }

    [Fact]
    public void Null_collections_inside_a_command_are_normalized()
    {
      var read = SessionStoreCore.Read("{\"schema\":1,\"commands\":[{\"action\":\"Explain\"}]}");
      var c = Assert.Single(read.Record.Commands);
      Assert.NotNull(c.SourceGuids);
      Assert.NotNull(c.Script);
      Assert.NotNull(c.Labels);
    }

    // ---------------------------------------------------------------- the version policy

    [Fact]
    public void A_future_major_reads_as_empty_but_is_flagged_for_PRESERVATION()
    {
      // the whole point: a newer Dwight's file must not be silently destroyed by an older one.
      var json = "{\"schema\":" + (SessionStoreCore.SchemaMajor + 1) + ",\"commands\":[{\"action\":\"Explain\"}]}";
      var read = SessionStoreCore.Read(json);

      Assert.Equal(StoreReadOutcome.FutureVersion, read.Outcome);
      Assert.Empty(read.Record.Commands);      // we do not guess at a format we do not know
    }

    [Fact]
    public void A_higher_minor_of_the_same_major_is_READ_not_rejected()
    {
      var json = "{\"schema\":1,\"minor\":99,\"commands\":[{\"action\":\"Explain\"}]}";
      var read = SessionStoreCore.Read(json);
      Assert.Equal(StoreReadOutcome.Ok, read.Outcome);
      Assert.Single(read.Record.Commands);
    }

    [Fact]
    public void Unknown_fields_are_ignored_rather_than_fatal()
    {
      var json = "{\"schema\":1,\"whoKnows\":42,\"commands\":[{\"action\":\"Explain\",\"futureThing\":\"x\"}]}";
      var read = SessionStoreCore.Read(json);
      Assert.Equal(StoreReadOutcome.Ok, read.Outcome);
      Assert.Equal("Explain", Assert.Single(read.Record.Commands).Action);
    }

    [Fact]
    public void Unknown_fields_SURVIVE_a_read_write_cycle()
    {
      // Stronger than "ignored": a same-major file written by a NEWER minor carries fields this build does not
      // know. Dropping them would silently delete the user's data the moment they opened an older Dwight once.
      var json = "{\"schema\":1,\"minor\":99,\"futureTop\":{\"a\":1},\"commands\":[{\"action\":\"Explain\",\"futureCmd\":\"keep me\"}]}";
      var rewritten = SessionStoreCore.Write(SessionStoreCore.Read(json).Record);

      Assert.Contains("futureTop", rewritten);
      Assert.Contains("keep me", rewritten);
    }

    [Fact]
    public void The_graph_slot_round_trips_opaquely()
    {
      // WP-3 populates this; WP-1 only guarantees it survives, so no migration is needed later.
      var json = "{\"schema\":1,\"graph\":{\"clusters\":[1,2,3]}}";
      var rewritten = SessionStoreCore.Write(SessionStoreCore.Read(json).Record);
      Assert.Contains("clusters", rewritten);
    }

    // ---------------------------------------------------------------- bounds

    [Fact]
    public void Commands_are_capped_and_the_NEWEST_are_kept()
    {
      // the list is newest-first, so the cap trims the tail
      var many = Enumerable.Range(0, SessionStoreCore.MaxCommands + 25)
                           .Select(i => Cmd("Action" + i)).ToArray();
      var read = SessionStoreCore.Read(SessionStoreCore.Write(Rec(many)));

      Assert.Equal(SessionStoreCore.MaxCommands, read.Record.Commands.Count);
      Assert.Equal("Action0", read.Record.Commands[0].Action);                       // newest survives
      Assert.DoesNotContain(read.Record.Commands, c => c.Action == "Action" + (SessionStoreCore.MaxCommands + 24));
    }

    [Fact]
    public void Threads_are_capped()
    {
      var rec = Rec();
      rec.Threads = Enumerable.Range(0, SessionStoreCore.MaxThreads + 5)
        .Select(i => new StoredThread { Id = "t" + i, Messages = new List<StoredMessage>() }).ToList();

      var read = SessionStoreCore.Read(SessionStoreCore.Write(rec));
      Assert.Equal(SessionStoreCore.MaxThreads, read.Record.Threads.Count);
      Assert.Equal("t0", read.Record.Threads[0].Id);
    }

    [Fact]
    public void Thread_messages_are_capped_keeping_the_most_RECENT()
    {
      // messages are chronological (oldest first), so the cap trims the HEAD - a long conversation keeps its
      // live tail rather than its opening.
      var rec = Rec();
      rec.Threads = new List<StoredThread>
      {
        new StoredThread
        {
          Id = "t1",
          Messages = Enumerable.Range(0, SessionStoreCore.MaxMessagesPerThread + 10)
            .Select(i => new StoredMessage { Who = "me", Html = "m" + i }).ToList(),
        },
      };

      var t = Assert.Single(SessionStoreCore.Read(SessionStoreCore.Write(rec)).Record.Threads);
      Assert.Equal(SessionStoreCore.MaxMessagesPerThread, t.Messages.Count);
      Assert.Equal("m10", t.Messages[0].Html);                                        // oldest 10 dropped
      Assert.Equal("m" + (SessionStoreCore.MaxMessagesPerThread + 9), t.Messages.Last().Html);
    }

    // ---------------------------------------------------------------- grounding

    [Fact]
    public void Unparseable_guids_are_dropped_not_thrown()
    {
      var json = "{\"schema\":1,\"commands\":[{\"action\":\"Explain\",\"sourceGuids\":" +
                 "[\"11111111-1111-1111-1111-111111111111\",\"not-a-guid\",\"\"]}]}";
      var c = Assert.Single(SessionStoreCore.Read(json).Record.Commands);
      Assert.Equal("11111111-1111-1111-1111-111111111111", Assert.Single(c.SourceGuids));
    }

    [Fact]
    public void A_command_with_no_action_is_dropped()
    {
      var json = "{\"schema\":1,\"commands\":[{\"subtitle\":\"orphan\"},{\"action\":\"Explain\"}]}";
      var c = Assert.Single(SessionStoreCore.Read(json).Record.Commands);
      Assert.Equal("Explain", c.Action);
    }

    // ---------------------------------------------------------------- #165: no empty files

    [Fact]
    public void HasContent_is_false_for_an_empty_record()
    {
      // #165: a store file is written only once it has something to keep, so an untitled scratch document
      // the user never uses does not litter %APPDATA% with an empty file.
      Assert.False(SessionStoreCore.HasContent(new SessionRecord()));
      Assert.False(SessionStoreCore.HasContent(Rec()));
      Assert.False(SessionStoreCore.HasContent(null));
    }

    [Fact]
    public void HasContent_is_true_once_there_is_a_command_or_a_thread()
    {
      Assert.True(SessionStoreCore.HasContent(Rec(Cmd("Explain"))));

      var withThread = Rec();
      withThread.Threads = new List<StoredThread> { new StoredThread { Id = "t1" } };
      Assert.True(SessionStoreCore.HasContent(withThread));
    }

    [Fact]
    public void Write_tolerates_a_null_record()
    {
      var json = SessionStoreCore.Write(null);
      Assert.Equal(StoreReadOutcome.Ok, SessionStoreCore.Read(json).Outcome);
    }

    // ---------------------------------------------------------------- step 4: a command write must not wipe siblings

    [Fact]
    public void Rewriting_ONLY_the_commands_preserves_threads_and_graph_and_extension_data()
    {
      // The load-bearing invariant behind step 4's persist path: CommandHistory rewrites a record's Commands on
      // every action, and it must keep the OTHER sections intact - a Scaffold thread (step 5), the graph slot
      // (WP-3), and any newer minor's unknown fields. Losing them on a command write would silently delete the
      // user's conversation the first time they ran a command after opening the chat.
      var json = "{\"schema\":1,\"minor\":7," +
                 "\"threads\":[{\"id\":\"t1\",\"phase\":\"scaffold\",\"scaffoldMap\":\"A1,B1\"," +
                   "\"messages\":[{\"who\":\"me\",\"html\":\"build\"}]}]," +
                 "\"graph\":{\"clusters\":[1,2]}," +
                 "\"futureTop\":{\"keep\":true}," +
                 "\"commands\":[{\"action\":\"Explain\"}]}";

      var rec = SessionStoreCore.Read(json).Record;

      // simulate the shim: replace ONLY Commands, exactly as CommandHistory.Persist does
      rec.Commands = new List<StoredCommand> { Cmd("Reorganise"), Cmd("Diagnose") };

      var reread = SessionStoreCore.Read(SessionStoreCore.Write(rec)).Record;

      Assert.Equal(2, reread.Commands.Count);                                   // the new commands landed
      var t = Assert.Single(reread.Threads);                                    // the thread survived
      Assert.Equal("A1,B1", t.ScaffoldMap);
      Assert.Equal("build", Assert.Single(t.Messages).Html);
      Assert.NotNull(reread.Graph);                                             // the graph slot survived
      Assert.Contains("futureTop", SessionStoreCore.Write(reread));            // the unknown field survived
    }

    [Fact]
    public void Threads_round_trip_alongside_commands()
    {
      var rec = Rec(Cmd("Explain"));
      rec.Threads = new List<StoredThread>
      {
        new StoredThread { Id = "t1", Phase = "plan", Messages = new List<StoredMessage>
          { new StoredMessage { Who = "dw", Html = "hi", Utc = "2026-07-22T10:00:00Z" } } },
      };

      var reread = SessionStoreCore.Read(SessionStoreCore.Write(rec)).Record;
      Assert.Single(reread.Commands);
      Assert.Single(reread.Threads);
      Assert.Equal("plan", reread.Threads[0].Phase);
    }
  }
}
