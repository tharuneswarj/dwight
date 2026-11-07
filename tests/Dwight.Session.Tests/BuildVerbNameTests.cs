using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;
using Dwight.Session;

namespace Dwight.Session.Tests
{
  /// <summary>
  /// THE FIFTH VERB WAS RENAMED (30 October 2026): Scaffold became Build, and Scaffold is now the map tool inside
  /// it. A record written by an earlier build says "Scaffold" in its command rows, and that is the user's own
  /// history on disk - so every READ of a persisted verb name accepts "Scaffold" as an alias of "Build", and
  /// nothing writes it back as Scaffold. The two readers that are GH-free are tested here: the store's own
  /// normaliser (applied on read AND write) and the verb-name helper the GH-bound row mapping and the panel's
  /// chat check both go through.
  /// </summary>
  public class BuildVerbNameTests
  {
    private static StoredCommand Row(string action) => new StoredCommand
    {
      Action = action,
      Subtitle = "thread",
      State = "ok",
      SourceGuids = new List<string>(),
      TimeUtc = "2026-10-01T10:00:00Z",
      CostLine = " - ",
      LiveClass = "content",
      ThreadId = "abc123",
      Script = new List<StoredOp> { new StoredOp { Op = "SetChat", Args = new List<string> { "[]" } } },
    };

    private static SessionRecord Rec(params StoredCommand[] cmds) => new SessionRecord
    {
      DocumentId = "66fd7275-7717-4065-90eb-483736e9296a",
      Labels = new List<string> { @"C:\work\site.gh" },
      Fingerprint = "ABCD",
      Commands = cmds.ToList(),
    };

    /// <summary>A file as an earlier build wrote it: the same JSON, with the verb's old name in the row.</summary>
    private static string LegacyJson(string verbOnDisk)
    {
      var json = SessionStoreCore.Write(Rec(Row("Explain")));
      var legacy = Regex.Replace(json, "\"action\"\\s*:\\s*\"Explain\"", "\"action\": \"" + verbOnDisk + "\"");
      Assert.NotEqual(json, legacy);   // the substitution really happened, or the test proves nothing
      return legacy;
    }

    [Fact]
    public void A_row_written_with_Scaffold_reads_back_as_a_Build_row()
    {
      var read = SessionStoreCore.Read(LegacyJson("Scaffold"));

      Assert.Equal(StoreReadOutcome.Ok, read.Outcome);
      var c = Assert.Single(read.Record.Commands);
      Assert.Equal("Build", c.Action);
      Assert.Equal("abc123", c.ThreadId);          // the thread it points at is untouched
      Assert.Equal("SetChat", c.Script.Single().Op);   // and so is what it replays
    }

    [Fact]
    public void A_row_read_as_Scaffold_is_written_back_as_Build_and_never_as_Scaffold()
    {
      var read = SessionStoreCore.Read(LegacyJson("Scaffold"));
      var written = SessionStoreCore.Write(read.Record);

      Assert.Contains("\"Build\"", written);
      Assert.DoesNotContain("\"Scaffold\"", written);
      Assert.Equal("Build", SessionStoreCore.Read(written).Record.Commands.Single().Action);
    }

    [Fact]
    public void A_record_handed_to_the_writer_with_the_old_name_is_still_written_as_Build()
    {
      // the write side alone: a row built in this process from an older value must not reach the disk as Scaffold
      var written = SessionStoreCore.Write(Rec(Row("Scaffold")));
      Assert.DoesNotContain("\"Scaffold\"", written);
      Assert.Equal("Build", SessionStoreCore.Read(written).Record.Commands.Single().Action);
    }

    [Fact]
    public void A_row_written_with_Build_round_trips_unchanged()
    {
      var written = SessionStoreCore.Write(Rec(Row("Build")));
      var c = SessionStoreCore.Read(written).Record.Commands.Single();

      Assert.Equal("Build", c.Action);
      Assert.Equal("abc123", c.ThreadId);
      Assert.Equal(written, SessionStoreCore.Write(SessionStoreCore.Read(written).Record));
    }

    [Fact]
    public void The_other_four_verbs_are_not_touched()
    {
      foreach (var verb in new[] { "Explain", "Diagnose", "Reorganise", "Simplify" })
      {
        var c = SessionStoreCore.Read(SessionStoreCore.Write(Rec(Row(verb)))).Record.Commands.Single();
        Assert.Equal(verb, c.Action);
      }
    }

    [Fact]
    public void The_verb_name_helper_treats_Scaffold_as_Build_in_either_case_and_leaves_everything_else_alone()
    {
      Assert.Equal("Build", DwightVerbs.Canonical("Scaffold"));
      Assert.Equal("Build", DwightVerbs.Canonical("scaffold"));
      Assert.Equal("Build", DwightVerbs.Canonical("Build"));
      Assert.Equal("Explain", DwightVerbs.Canonical("Explain"));
      Assert.Null(DwightVerbs.Canonical(null));

      Assert.True(DwightVerbs.IsBuild("Build"));
      Assert.True(DwightVerbs.IsBuild("Scaffold"));   // the panel's chat check, for a name that arrives from an old record
      Assert.False(DwightVerbs.IsBuild("Simplify"));
      Assert.False(DwightVerbs.IsBuild(null));
    }

    [Fact]
    public void The_name_the_menu_shows_for_the_fifth_verb_is_Build()
    {
      Assert.Equal("Build", DwightVerbs.Build);
      Assert.Equal("Build", DwightVerbs.All[4].Name);
      Assert.Equal(new[] { "Explain", "Diagnose", "Reorganise", "Simplify", "Build" }, DwightVerbs.Names);
    }
  }
}
