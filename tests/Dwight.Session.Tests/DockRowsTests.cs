using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Xunit;
using Dwight.Session;

namespace Dwight.Session.Tests
{
  /// <summary>
  /// Brief DG, as Tharun's Brief BQ section 9 rules it: THE DOCK LIST SHOWS NO COST ON ANY ROW, a stopped or failed run's included. The row's writer
  /// (<see cref="DockRows"/>, the one the shell's fixtures are rendered from) is handed no spend, so it cannot carry one. What a run that billed and then did
  /// not land cost rides its record, from the same spend the ledger line was written from, and shows in the run's status line when the row is reopened, with its
  /// state beside it. A run that never billed shows no cost and no zero.
  /// </summary>
  public class DockRowsTests
  {
    private static StoredSpend Priced(double cents) =>
      new StoredSpend { Model = "claude-sonnet-5", InputTokens = 41000, OutputTokens = 2100, Cents = cents, CostText = SpendMath.CentsText(cents) };

    // ---- the list: no cost, on any row ----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("ok")]
    [InlineData("stopped")]
    [InlineData("failed")]
    [InlineData(null)]
    public void No_row_in_the_list_carries_a_cost_whatever_its_state(string state)
    {
      var row = DockRows.Row("Explain", "8 components", false, null, state, "3h ago", null);
      Assert.False(row.ContainsKey("cost"));
      Assert.DoesNotContain("¢", row.ToJsonString());
    }

    [Fact]
    public void The_writer_takes_no_spend_so_a_row_cannot_carry_one()
    {
      var m = typeof(DockRows).GetMethod(nameof(DockRows.Row));
      Assert.DoesNotContain(m.GetParameters(), p => p.ParameterType == typeof(StoredSpend));
    }

    [Fact]
    public void The_row_keeps_the_keys_and_the_order_the_shell_has_always_read()
    {
      var row = DockRows.Row("Build", "A tower", true, "running", "ok", "4m ago", "site_v2.gh");
      Assert.Equal(new[] { "icon", "action", "subtitle", "chat", "live", "liveWord", "state", "meta", "from" }, row.Select(kv => kv.Key).ToArray());
      Assert.Equal("B", row["icon"]!.GetValue<string>());
      Assert.True(row["chat"]!.GetValue<bool>());
      Assert.Equal("ok", row["state"]!.GetValue<string>());
    }

    [Fact]
    public void An_unset_state_reads_ok_and_an_unnamed_action_gets_a_dot()
    {
      var row = DockRows.Row(null, null, false, null, null, "now", null);
      Assert.Equal("ok", row["state"]!.GetValue<string>());
      Assert.Equal("·", row["icon"]!.GetValue<string>());
      Assert.Equal("", row["action"]!.GetValue<string>());
    }

    // ---- the status line of a reopened run: the state, then what it cost ----------------------------------------------------------------

    [Theory]
    [InlineData("stopped", 10.7, "stopped · ~10.7¢")]
    [InlineData("failed", 2.6, "failed · ~2.6¢")]
    [InlineData("failed", 0.04, "failed · <0.1¢")]
    public void A_reopened_stopped_or_failed_run_that_billed_reads_its_cents_beside_its_state(string state, double cents, string line)
    {
      Assert.Equal(line, DockRows.StatusWord(state, Priced(cents)));
    }

    [Fact]
    public void A_run_with_no_price_reads_its_own_words_when_it_billed_and_nothing_when_it_did_not()
    {
      var local = new StoredSpend { Model = "qwen", InputTokens = 900, OutputTokens = 80, CostText = "local · free" };
      Assert.Equal("stopped · local · free", DockRows.StatusWord("stopped", local));
      Assert.Null(DockRows.StatusWord("stopped", new StoredSpend { Model = "qwen", CostText = "local · free" }));   // no tokens: nothing was run
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("stopped")]
    public void A_run_that_never_billed_shows_no_cost_and_no_zero(string state)
    {
      Assert.Null(DockRows.StatusWord(state, null));
      Assert.Null(DockRows.StatusWord(state, new StoredSpend { Model = "m", Cents = 0 }));
    }

    [Theory]
    [InlineData("ok")]
    [InlineData(null)]
    [InlineData("")]
    public void A_run_that_landed_has_no_status_word_because_its_own_status_line_is_its_cost(string state)
    {
      Assert.Null(DockRows.StatusWord(state, Priced(10.7)));
    }

    // ---- the places the row is written and filled, and the status line shown (DwightSession, CommandHistory and the dock need Grasshopper, so they are read)

    private static string Source(params string[] parts)
    {
      var dir = new DirectoryInfo(AppContext.BaseDirectory);
      while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Dwight.sln"))) dir = dir.Parent;
      Assert.True(dir != null);
      return File.ReadAllText(Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray())).Replace("\r\n", "\n");
    }

    [Fact]
    public void A_failed_or_stopped_row_is_written_with_the_runs_spend_and_its_flag()
    {
      var s = Source("Dwight", "DwightSession.cs");
      var failed = s.Substring(s.IndexOf("public static void Failed(", StringComparison.Ordinal));
      failed = failed.Substring(0, failed.IndexOf("private static void ArmTimeout", StringComparison.Ordinal));
      Assert.Contains("Spend = ctx.Spend,", failed);
      Assert.Contains("SpendLedgered = ctx.SpendLedgered,", failed);
      Assert.Contains("ctx.FailedRow = row;", failed);
    }

    [Fact]
    public void A_bill_that_arrives_after_the_row_was_written_is_put_on_that_row_when_the_line_is_written()
    {
      var actions = Source("Dwight", "LlmActions.cs");
      var ledger = actions.Substring(actions.IndexOf("private static void LedgerRun(", StringComparison.Ordinal));
      ledger = ledger.Substring(0, ledger.IndexOf("/// <summary>Brief CS", StringComparison.Ordinal));
      Assert.Contains("RecordRun(ctx.Doc, ctx.Action, ctx.Spend, ref ctx.SpendLedgered, ctx.RunId)", ledger);
      // Brief DL: and an estimate on the row is overwritten by the bill, never the other way round.
      Assert.Contains("if (ctx.FailedRow != null && ctx.Spend != null && (ctx.FailedRow.Spend == null || (ctx.FailedRow.Spend.Estimated != null && ctx.Spend.Estimated == null))) CommandHistory.SetSpend(ctx.Doc, ctx.FailedRow, ctx.Spend);", ledger);
      Assert.True(ledger.IndexOf("RecordRun(", StringComparison.Ordinal) < ledger.IndexOf("SetSpend(", StringComparison.Ordinal));   // the line first, then the row

      var history = Source("Dwight", "Session", "CommandHistory.cs");
      var set = history.Substring(history.IndexOf("public static void SetSpend(", StringComparison.Ordinal));
      Assert.Contains("row.Spend = spend;", set);
      Assert.Contains("row.SpendLedgered = true;", set);   // so a later record writes no second line
    }

    [Fact]
    public void The_dock_writes_its_rows_through_the_one_writer_and_the_shell_draws_no_cost()
    {
      var dock = Source("Dwight", "UI", "DwightDock.cs");
      Assert.Contains("arr.Add(DockRows.Row(c.Action,", dock);
      Assert.DoesNotContain("[\"liveWord\"] = HistoryCore.LiveWords(RowLive(c))", dock);   // the old inline writer is gone

      var shell = Source("Dwight", "Resources", "dock-shell.html");
      Assert.DoesNotContain("c.cost", shell);
    }

    [Fact]
    public void A_reopened_run_shows_its_cost_in_its_status_line_with_its_state_beside_it()
    {
      var s = Source("Dwight", "DwightSession.cs");
      var reopen = s.Substring(s.IndexOf("public static void Reopen(", StringComparison.Ordinal));
      var render = reopen.IndexOf("Render(panel, cmd.Script, cmd.Live);", StringComparison.Ordinal);
      // The line is composed by DockRows.ReopenedStatus now (a Learn's score rides on it), so the stopped word and the score cannot replace one another.
      var status = reopen.IndexOf("var status = DockRows.ReopenedStatus(cmd.State, cmd.Spend, cmd.CostLine, cmd.Score);", StringComparison.Ordinal);
      Assert.True(render > 0 && status > render);   // after the panel is drawn, so the line is not replaced by it
      Assert.Contains("panel.SetStatus(status)", reopen);
    }

    [Fact]
    public void The_shells_fixture_holds_the_stopped_and_failed_states_and_no_cost_on_any_row()
    {
      var rows = JsonNode.Parse(Source("tools", "ui-history-events.json"))!["dockRows"]!.AsArray().Select(r => r!.AsObject()).ToList();
      Assert.Contains(rows, r => r["state"]!.GetValue<string>() == "stopped");
      Assert.Contains(rows, r => r["state"]!.GetValue<string>() == "failed");
      Assert.DoesNotContain(rows, r => r.ContainsKey("cost"));
    }
  }
}
