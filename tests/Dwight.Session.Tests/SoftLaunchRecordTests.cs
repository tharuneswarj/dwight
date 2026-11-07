using System.IO;
using System.Linq;
using Xunit;
using Dwight.Session;

namespace Dwight.Session.Tests
{
  /// <summary>
  /// Brief EK unit 1: the soft launch ships 0.9.0 on Wednesday and 1.0.0 arrives as an upgrade on Sunday, so the notes and chats a teammate
  /// writes on 0.9.0 must read whole at 1.0.0. The store's gate is the SCHEMA MAJOR, an integer that is not the plugin's version, so the
  /// plugin version moving from 0.9.0 to 1.0.0 cannot set a record aside. These tests pin the three things that keep that true.
  /// </summary>
  public class SoftLaunchRecordTests
  {
    // A record exactly as 0.9.0 writes it: schema 1, minor 11, a command with its spend, a chat with a turn, a Dwight note, a change.
    private const string RecordWrittenAt090 =
      "{\"schema\":1,\"minor\":11,\"documentId\":\"66fd7275-7717-4065-90eb-483736e9296a\",\"labels\":[\"C:\\\\work\\\\site.gh\"],\"fingerprint\":\"ABCD\"," +
      "\"updatedUtc\":\"2026-11-05T09:30:00Z\"," +
      "\"commands\":[{\"action\":\"Explain\",\"subtitle\":\"3 components\",\"state\":\"ok\",\"sourceGuids\":[\"11111111-1111-1111-1111-111111111111\"]," +
      "\"timeUtc\":\"2026-11-05T09:30:00Z\",\"costLine\":\"Sonnet 5.5\",\"liveClass\":\"content\",\"runId\":\"r1\"}]," +
      "\"threads\":[{\"id\":\"t1\",\"messages\":[{\"who\":\"user\",\"html\":\"<p>why is this slow</p>\",\"src\":\"why is this slow\"}]}]," +
      "\"notes\":[{\"id\":\"n1\",\"anchor\":\"22222222-2222-2222-2222-222222222222\",\"text\":\"Client wants the spacing at 400.\",\"by\":\"person\",\"whenUtc\":\"2026-11-05T09:31:00Z\"}]}";

    [Fact]
    public void A_record_written_at_0_9_0_reads_whole_at_the_current_build()
    {
      var read = SessionStoreCore.Read(RecordWrittenAt090);

      Assert.Equal(StoreReadOutcome.Ok, read.Outcome);
      var c = Assert.Single(read.Record.Commands);
      Assert.Equal("Explain", c.Action);
      Assert.Equal("r1", c.RunId);
      var t = Assert.Single(read.Record.Threads);
      Assert.Equal("why is this slow", Assert.Single(t.Messages).Src);
      var n = Assert.Single(read.Record.Notes);
      Assert.Equal("Client wants the spacing at 400.", n.Text);
      Assert.Equal("66fd7275-7717-4065-90eb-483736e9296a", read.Record.DocumentId);
    }

    [Fact]
    public void Reading_then_writing_it_loses_nothing_a_teammate_wrote()
    {
      var again = SessionStoreCore.Read(SessionStoreCore.Write(SessionStoreCore.Read(RecordWrittenAt090).Record));

      Assert.Equal(StoreReadOutcome.Ok, again.Outcome);
      Assert.Equal("Explain", Assert.Single(again.Record.Commands).Action);
      Assert.Equal("why is this slow", Assert.Single(Assert.Single(again.Record.Threads).Messages).Src);
      Assert.Equal("Client wants the spacing at 400.", Assert.Single(again.Record.Notes).Text);
    }

    [Fact]
    public void The_gate_is_a_schema_major_of_1_so_a_bump_to_2_is_a_ruling_and_fails_here_first()
    {
      // A record is set aside only when its schema is HIGHER than SchemaMajor. 0.9.0 writes schema 1. If 1.0.0 raised SchemaMajor, 0.9.0 files
      // would still read (1 is not higher than 2), but if a 0.9.0 build ever wrote a record above the current major it would be set aside.
      // Pinning 1 makes either change a decision someone has to make on purpose.
      Assert.Equal(1, SessionStoreCore.SchemaMajor);
      Assert.True(SessionStoreCore.SchemaMinor >= 11, "0.9.0 writes minor 11; the minor only ever goes up");
      Assert.Equal(StoreReadOutcome.FutureVersion, SessionStoreCore.Read("{\"schema\":2,\"minor\":0}").Outcome);
    }

    [Fact]
    public void The_store_never_reads_the_plugin_version_so_0_9_0_to_1_0_0_cannot_gate_it()
    {
      var root = RepoRoot();
      var core = File.ReadAllText(Path.Combine(root, "Dwight", "Session", "SessionStoreCore.cs"));
      var store = File.ReadAllText(Path.Combine(root, "Dwight", "Session", "SessionStore.cs"));
      foreach (var src in new[] { core, store })
      {
        Assert.DoesNotContain("AssemblyVersion", src);
        Assert.DoesNotContain("GetName().Version", src);
        Assert.DoesNotContain("InformationalVersion", src);
      }
      Assert.Contains("rec.Schema > SchemaMajor", core);   // not vacuous: the one gate is where this test says it is
      var csproj = File.ReadAllText(Path.Combine(root, "Dwight", "Dwight.csproj"));
      Assert.Contains("<Version>1.0.2</Version>", csproj);   // Brief EZ, then Brief FD, then Brief FQ: the pin moves with the release (1.0.1 carries the package icon, 1.0.2 the five shell fixes); the gate above is the rule and stays
    }

    private static string RepoRoot()
    {
      var dir = System.AppContext.BaseDirectory;
      while (dir != null && !File.Exists(Path.Combine(dir, "Dwight.sln"))) dir = Path.GetDirectoryName(dir);
      Assert.NotNull(dir);
      return dir;
    }
  }
}
