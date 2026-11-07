using System.Collections.Generic;
using System.Linq;
using Xunit;
using Dwight.Session;

namespace Dwight.Session.Tests
{
  /// <summary>Brief I: cost and tokens survive the session. Written through the store's real read path
  /// (Write then Read), because that is the path a closed and reopened Rhino takes.</summary>
  public class SpendTests
  {
    private static StoredSpend Priced(int inp, int outp, double cents, int steps = 0, int calls = 0) => new StoredSpend
    {
      Model = "claude-sonnet-4-5",
      ElapsedMs = 3200,
      InputTokens = inp,
      OutputTokens = outp,
      CacheReadTokens = 1000,
      CacheWriteTokens = 200,
      Cents = cents,
      CostText = SpendMath.CentsText(cents),
      Steps = steps,
      ToolCalls = calls,
    };

    private static SessionRecord Rec() => new SessionRecord
    {
      DocumentId = "66fd7275-7717-4065-90eb-483736e9296a",
      Fingerprint = "ABCD",
      Commands = new List<StoredCommand>
      {
        new StoredCommand { Action = "Explain", Subtitle = "3 components", State = "ok", TimeUtc = "2026-10-25T10:00:00Z",
                            CostLine = "Sonnet · 3.2s · ~1.4¢", Spend = Priced(900, 300, 1.4) },
      },
      Threads = new List<StoredThread>
      {
        new StoredThread
        {
          Id = "t1", Phase = "plan", Messages = new List<StoredMessage>(),
          Turns = new List<StoredSpend> { Priced(10000, 2000, 4.1, 3, 5), Priced(30000, 4000, 8.2, 2, 1) },
        },
      },
    };

    [Fact]
    public void A_verb_runs_spend_reads_back_with_the_same_numbers()
    {
      var read = SessionStoreCore.Read(SessionStoreCore.Write(Rec()));
      Assert.Equal(StoreReadOutcome.Ok, read.Outcome);
      var s = Assert.Single(read.Record.Commands).Spend;
      Assert.NotNull(s);
      Assert.Equal("claude-sonnet-4-5", s.Model);
      Assert.Equal(3200, s.ElapsedMs);
      Assert.Equal(900, s.InputTokens);
      Assert.Equal(300, s.OutputTokens);
      Assert.Equal(1000, s.CacheReadTokens);
      Assert.Equal(200, s.CacheWriteTokens);
      Assert.Equal(1.4, s.Cents);
      Assert.Equal("~1.4¢", s.CostText);

      // Brief AK: the central ledger's line is built at the same site from the same spend, so beside the session
      // row it carries the same numbers - and the provider, which the session file has never held.
      var spend = Rec().Commands[0].Spend;
      spend.Provider = "anthropic";
      var line = SpendLedgerCore.Parse(SpendLedgerCore.Serialize(SpendLedgerCore.ForVerb(spend,
        new System.DateTime(2026, 10, 25, 10, 0, 0, System.DateTimeKind.Utc), read.Record.DocumentId, "tower.gh", "Explain", spend.Provider)));
      Assert.Equal((s.Model, s.InputTokens, s.OutputTokens, s.CacheReadTokens, s.CacheWriteTokens, s.Cents),
        (line.Model, (int)line.InputTokens, (int)line.OutputTokens, (int)line.CacheReadTokens, (int)line.CacheWriteTokens, line.Cents));
      Assert.Equal(3.2, line.Seconds);
      Assert.Equal("anthropic", line.Provider);
      Assert.DoesNotContain("provider", SessionStoreCore.Write(Rec()));   // the session file's shape is unchanged
    }

    [Fact]
    public void A_chat_turns_spend_reads_back_with_steps_and_tool_calls()
    {
      var read = SessionStoreCore.Read(SessionStoreCore.Write(Rec()));
      var turns = Assert.Single(read.Record.Threads).Turns;
      Assert.Equal(2, turns.Count);
      Assert.Equal(3, turns[0].Steps);
      Assert.Equal(5, turns[0].ToolCalls);
      Assert.Equal(8.2, turns[1].Cents);

      // Brief AK: the turn's ledger line, beside the thread's row: the same numbers, its ordinal on the thread.
      var line = SpendLedgerCore.Parse(SpendLedgerCore.Serialize(SpendLedgerCore.ForTurn(Rec().Threads[0].Turns[1],
        System.DateTime.UtcNow, read.Record.DocumentId, "tower.gh", DwightVerbs.Build, "anthropic", "t1", 2)));
      Assert.Equal((turns[1].Steps, turns[1].ToolCalls, turns[1].Cents, (long)turns[1].TotalInputTokens),
        (line.Steps, line.ToolCalls, line.Cents, line.TotalInputTokens));
      Assert.Equal(2, line.Turn);
      Assert.Equal("t1", line.ThreadId);
    }

    [Fact]
    public void The_thread_total_is_computed_from_its_turns_and_not_stored()
    {
      var json = SessionStoreCore.Write(Rec());
      Assert.DoesNotContain("totalCents", json);
      var turns = SessionStoreCore.Read(json).Record.Threads[0].Turns;
      var total = SpendMath.Sum(turns);
      Assert.Equal(2, total.Turns);
      Assert.Equal(12.3, total.Cents, 6);
      Assert.Equal(42400, total.InputTokens);   // 11200 + 31200, cached input counted
      Assert.Equal("2 turns · ~12.3¢ · 42k in / 6k out", SpendMath.ChatLine(total));
    }

    // U111: the mark is on the message, survives the file, and only a marked message carries it to the shell.
    [Fact]
    public void An_approval_echo_keeps_its_mark_through_the_file_and_a_typed_line_never_has_one()
    {
      var marked = new StoredMessage { Who = "me", Html = "Yes, go ahead.", Src = "Yes, go ahead.", Echo = true };
      var typed = new StoredMessage { Who = "me", Html = "Yes, go ahead.", Src = "Yes, go ahead." };

      var chat = System.Text.Json.Nodes.JsonNode.Parse(ChatTranscript.ToChatJson(new[] { marked, typed })).AsArray();
      Assert.True(chat[0]["echo"].GetValue<bool>());
      Assert.Null(chat[1]["echo"]);

      var writtenMarked = System.Text.Json.JsonSerializer.Serialize(marked);
      var writtenTyped = System.Text.Json.JsonSerializer.Serialize(typed);
      Assert.Contains("\"echo\":true", writtenMarked);
      Assert.DoesNotContain("echo", writtenTyped);   // absent from the file unless true
      Assert.True(System.Text.Json.JsonSerializer.Deserialize<StoredMessage>(writtenMarked).Echo);
      Assert.False(System.Text.Json.JsonSerializer.Deserialize<StoredMessage>(writtenTyped).Echo);
    }

    [Fact]
    public void A_record_without_spend_an_old_file_reads_as_absent_not_a_crash()
    {
      const string old = "{\"schema\":1,\"minor\":3,\"documentId\":\"66fd7275-7717-4065-90eb-483736e9296a\"," +
        "\"commands\":[{\"action\":\"Explain\",\"subtitle\":\"3 components\",\"state\":\"ok\",\"costLine\":\"Sonnet · 1.2s\"}]," +
        "\"threads\":[{\"id\":\"t1\",\"phase\":\"plan\",\"messages\":[]}]}";
      var read = SessionStoreCore.Read(old);
      Assert.Equal(StoreReadOutcome.Ok, read.Outcome);
      Assert.Null(Assert.Single(read.Record.Commands).Spend);
      Assert.Null(read.Record.Threads[0].Turns);
      var total = SpendMath.Sum(read.Record.Threads[0].Turns);
      Assert.Equal(0, total.Turns);
      Assert.Equal("", SpendMath.ChatLine(total));
    }

    [Fact]
    public void A_local_run_keeps_its_words_and_no_invented_number()
    {
      var local = new StoredSpend { Model = "gpt-oss", InputTokens = 500, OutputTokens = 200, CostText = "local · free" };
      var rec = Rec();
      rec.Threads[0].Turns = new List<StoredSpend> { local };
      var turn = SessionStoreCore.Read(SessionStoreCore.Write(rec)).Record.Threads[0].Turns.Single();
      Assert.Null(turn.Cents);
      Assert.Equal("local · free", turn.CostText);
      Assert.Equal("1 turn · local · free · 500 in / 200 out", SpendMath.ChatLine(SpendMath.Sum(new[] { turn })));
    }

    [Fact]
    public void Unknown_fields_on_a_spend_survive_a_read_write_cycle()
    {
      const string newer = "{\"schema\":1,\"minor\":9,\"commands\":[{\"action\":\"Explain\",\"spend\":" +
        "{\"model\":\"m\",\"inputTokens\":5,\"futureField\":\"keep me\"}}]}";
      var rewritten = SessionStoreCore.Write(SessionStoreCore.Read(newer).Record);
      Assert.Contains("futureField", rewritten);
      Assert.Contains("keep me", rewritten);
    }

    [Theory]
    [InlineData(0, "0")]
    [InlineData(523, "523")]
    [InlineData(1234, "1.2k")]
    [InlineData(9999, "10k")]
    [InlineData(41300, "41k")]
    public void Tokens_read_short(long n, string expected) => Assert.Equal(expected, SpendMath.TokensText(n));

    [Fact]
    public void A_very_small_sum_reads_as_under_a_tenth_of_a_cent()
      => Assert.Equal("<0.1¢", SpendMath.CentsText(0.04));

    [Fact]
    public void Two_attempts_of_one_run_add_up_and_null_is_neutral()
    {
      var sum = SpendMath.Combine(Priced(100, 20, 1.0), Priced(200, 30, 2.5));
      Assert.Equal(300, sum.InputTokens);
      Assert.Equal(50, sum.OutputTokens);
      Assert.Equal(3.5, sum.Cents);
      Assert.Equal("~3.5¢", sum.CostText);
      var one = Priced(1, 1, 1.0);
      Assert.Same(one, SpendMath.Combine(null, one));
      Assert.Same(one, SpendMath.Combine(one, null));
    }
  }
}
