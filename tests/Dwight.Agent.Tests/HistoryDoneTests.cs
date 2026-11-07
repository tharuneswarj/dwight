using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;
using Dwight.Agent;
using Dwight.Session;

namespace Dwight.Agent.Tests
{
  /// <summary>Brief BQ: what History says happened. Each route's sentence states what ran, the chat id is kept at the approval, and no card's
  /// question reaches a row. The sites themselves are GH-coupled, so they are read as source (the HistorySitesTests pattern).</summary>
  public class HistoryDoneTests
  {
    private static readonly string[] Kinds = { "reorganise", "simplify", "solve", "scaffold", "build", "set_value", "edit_script", "edit", "run_script" };

    [Fact]
    public void Every_kind_has_a_past_tense_with_no_question()
    {
      foreach (var kind in Kinds)
      {
        var done = ConsentCard.DoneFor(kind);
        Assert.False(string.IsNullOrWhiteSpace(done), kind);
        Assert.EndsWith(".", done);
        Assert.DoesNotContain("?", done);
      }
    }

    [Fact]
    public void No_title_any_card_can_store_shows_a_question_in_a_row()
    {
      foreach (var kind in Kinds)
      {
        var scope = HistoryCore.CardScope(ConsentCard.Title(kind), new[] { "A1", "B1" });
        foreach (var name in new[] { "Move", "", null, "Dwight: build region", "Dwight: anything new" })
          Assert.DoesNotContain("?", HistoryCore.LineOf(new StoredChange { Scope = scope, UndoName = name }));
      }
    }

    [Fact]
    public void A_build_names_the_region_that_ran()
    {
      Assert.Equal("Built A1.", ConsentCard.BuildDone("A1", false));
      Assert.Equal("Rebuilt A1.", ConsentCard.BuildDone(" A1 ", true));
      Assert.Equal(ConsentCard.DoneFor("build"), ConsentCard.BuildDone(null, false));
    }

    [Fact]
    public void A_spot_says_what_its_card_named()
    {
      var p = new SpotPlacement { Address = "Spot 5", Beside = "empty_number" };
      p.Components.Add("a Square");
      Assert.Equal("Placed a Square, Spot 5, near empty_number.", ConsentCard.SpotDone(p));
      p.Beside = null;
      p.Components.Add("a Circle");
      Assert.Equal("Placed a Square and a Circle, Spot 5, in clear space on the canvas.", ConsentCard.SpotDone(p));
      Assert.Equal(ConsentCard.DoneFor("build"), ConsentCard.SpotDone(null));
    }

    [Fact]
    public void A_value_change_uses_the_name_the_user_gave_it()
    {
      Assert.Equal("Set Radius.", ConsentCard.SetValueDone(new SetValueChange { Id = 4, Type = "Number Slider", Nick = "Radius" }));
      Assert.Equal("Set the Number Slider.", ConsentCard.SetValueDone(new SetValueChange { Id = 4, Type = "Number Slider" }));
    }

    [Fact]
    public void A_script_edit_names_the_script()
    {
      Assert.Equal("Edited Loft.", ConsentCard.EditScriptDone(new ScriptEditChange { Label = "Loft" }));
      Assert.Equal(ConsentCard.DoneFor("edit_script"), ConsentCard.EditScriptDone(null));
    }

    [Fact]
    public void An_edit_counts_only_the_rows_left_ticked()
    {
      var f = EditFixture.Make();
      Assert.True(EditOpsAsk.Validate(EditFixture.Ops(EditFixture.WireOp(3, "Result", 6, null), EditFixture.DeleteOp(5), EditFixture.WireOp(7, "Result", 6, null)),
        f.Canvas(), out var whole, out _));
      Assert.Equal("Added 2 wires, deleted 1 component.", ConsentCard.EditDone(whole));

      // the path a click takes (GhEditOps.RunApproved): the ticks choose the rows, and the plan is validated from those rows alone
      Assert.True(EditOpsAsk.Subset(whole.Record, new[] { 0 }, out var ops, out var rows, out _));
      Assert.True(EditOpsAsk.Validate(ops, f.Canvas(), rows, out var ticked, out _));
      Assert.Equal("Added 1 wire.", ConsentCard.EditDone(ticked));

      Assert.True(EditOpsAsk.Subset(whole.Record, new[] { 1 }, out ops, out rows, out _));
      Assert.True(EditOpsAsk.Validate(ops, f.Canvas(), rows, out ticked, out _));
      Assert.Equal("Deleted 1 component.", ConsentCard.EditDone(ticked));
    }

    // ---------------------------------------------------------------- the sites, read as source

    private static string Repo(params string[] parts)
    {
      var dir = AppContext.BaseDirectory;
      while (dir != null && !File.Exists(Path.Combine(dir, "Dwight.sln"))) dir = Path.GetDirectoryName(dir);
      Assert.NotNull(dir);
      return Path.Combine(new[] { dir }.Concat(parts).ToArray());
    }

    private static string Source(string rel) => File.ReadAllText(Repo("Dwight", rel));

    [Fact]
    public void Every_chat_site_says_what_ran_and_only_the_spot_defers_to_its_card()
    {
      var sites = Directory.GetFiles(Repo("Dwight"), "*.cs", SearchOption.AllDirectories)
        .Where(f => !f.EndsWith("ChangeLog.cs", StringComparison.Ordinal))
        .SelectMany(f => File.ReadAllText(f).Split('\n').Select(l => (f, l)))
        .Where(x => x.l.Contains("ChangeLog.NoteChat("))
        .ToList();
      Assert.True(sites.Count >= 7, "the scan found only " + sites.Count + " NoteChat sites; it is not reading the source");   // 7 when written
      Assert.Contains("ConsentCard.BuildDone(spec.Region, replacing.Count > 0)", Source("Agent/GhBuildRegion.cs"));
      Assert.Contains("ledger.IsApprovedSpot(spec.Region) ? null :", Source("Agent/GhBuildRegion.cs"));
      Assert.Contains("ConsentCard.EditDone(plan)", Source("Agent/GhEditOps.cs"));
      Assert.Contains("ConsentCard.EditScriptDone(change)", Source("Agent/GhEditScript.cs"));
      Assert.Contains("ConsentCard.SetValueDone(change)", Source("Agent/GhSetValue.cs"));
      Assert.Contains("ConsentCard.DoneFor(\"scaffold\")", Source("Agent/GhProposeScaffold.cs"));
      Assert.Equal(2, Regex.Matches(Source("Agent/GhRegionTidy.cs"), @"UndoName, TidyDone\)").Count);
    }

    [Fact]
    public void Every_approval_in_the_chat_holds_the_chat_id_and_the_spot_its_sentence()
    {
      var src = Source("UI/DwightScaffold.cs");
      var approvals = Regex.Matches(src, @"ChangeLog\.Approve\(.*?\);", RegexOptions.Singleline).Select(m => m.Value).ToList();
      Assert.Equal(2, approvals.Count);
      Assert.All(approvals, a => Assert.Contains("threadId", a));
      Assert.Contains("seed?.SpotDone", approvals[1]);
      Assert.Contains("seed.SpotDone = ConsentCard.SpotDone(prepared?.Placement)", src);
    }

    [Fact]
    public void A_verbs_change_never_takes_a_chat_id_or_a_cards_sentence()
    {
      var src = Source("Session/ChangeLog.cs");
      var verbBranch = src.IndexOf("if (a.ForVerb) _approval.Remove(doc);", StringComparison.Ordinal);
      var threadSet = src.IndexOf("c.ThreadId =", StringComparison.Ordinal);
      Assert.True(verbBranch > 0 && threadSet > verbBranch, "the chat id must be set in the chat's branch, after the verb's");
      Assert.Contains("else", src.Substring(verbBranch, threadSet - verbBranch));
    }

    [Fact]
    public void A_chat_moves_when_the_user_speaks_and_not_on_an_echo_or_on_opening()
    {
      var src = Source("UI/DwightScaffold.cs");
      var spoke = Regex.Matches(src, @"CommandHistory\.ThreadSpoke\(").Count;
      Assert.Equal(1, spoke);
      var door = src.Substring(src.IndexOf("internal static void AppendUserText(", StringComparison.Ordinal));
      door = door.Substring(0, door.IndexOf("AppendApprovalEcho(", StringComparison.Ordinal));
      Assert.Contains("CommandHistory.ThreadSpoke(doc, threadId)", door);
      Assert.True(door.IndexOf("AppendBoth(", StringComparison.Ordinal) < door.IndexOf("ThreadSpoke(", StringComparison.Ordinal),
        "the move refreshes the dock, so the message that names the chat must be stored first");

      var wire = src.Substring(src.IndexOf("private static void Wire(IResultPanel panel", StringComparison.Ordinal));
      wire = wire.Substring(0, wire.IndexOf("private static void OnUserMessage(", StringComparison.Ordinal));
      Assert.DoesNotContain("ThreadSpoke", wire);
      Assert.DoesNotContain("ThreadSpoke", Source("DwightSession.cs"));
    }

    [Fact]
    public void The_dock_validates_what_the_shell_sends()
    {
      var src = Source("UI/DwightDock.cs");
      Assert.Contains("verb == \"back\" || verb == \"redo\" || verb == \"before\"", src);
      Assert.Contains("if (!Guid.TryParse(arg, out var id)) return;", src);
      var open = src.Substring(src.IndexOf("private void OpenChat(string threadId)", StringComparison.Ordinal));
      open = open.Substring(0, open.IndexOf("</summary>", StringComparison.Ordinal));
      Assert.Contains("foreach (var v in _view)", open);   // only a chat the dock is showing; anything else opens nothing
      Assert.Contains("v.Cmd.ThreadId == threadId", open);
      Assert.DoesNotContain("view[\"rows\"]", src);         // the shell moved off them, so nothing sends the old fields
      Assert.DoesNotContain("view[\"tabs\"]", src);
      // Section 8: a row says whether it is a chat, from the same test as its subtitle. Brief DG: the row is written by DockRows, which the dock hands both.
      Assert.Contains("!string.IsNullOrEmpty(c.ThreadId), RowLive(c), c.State, Ago(c.Time), v.From", src);
    }
  }
}
