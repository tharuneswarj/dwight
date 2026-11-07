using System.Collections.Generic;
using System.Linq;
using Xunit;
using Dwight.Session;

namespace Dwight.Session.Tests
{
  /// <summary>The header writes four things on the thread and the thread keeps them: its model pin, its posture, its approval mode and its Full control.
  /// Brief AO also folds a legacy verb name in the ledger on read.</summary>
  public class ChatHeaderThreadTests
  {
    private static StoredThread Thread(string id, string modelId, string posture, string mode = null, bool? full = null) => new StoredThread
    {
      Id = id, Phase = "plan", ModelId = modelId, Posture = posture, ApprovalMode = mode, FullControl = full,
      Messages = new List<StoredMessage> { new StoredMessage { Who = "dw", Html = "<p>hi</p>" } },
    };

    private static SessionRecord Rec(params StoredThread[] threads) => new SessionRecord
    {
      DocumentId = "66fd7275-7717-4065-90eb-483736e9296a", Labels = new List<string> { @"C:\work\site.gh" }, Fingerprint = "ABCD", Threads = threads.ToList(),
    };

    [Fact]
    public void A_reopened_thread_shows_what_the_header_last_set()
    {
      var json = SessionStoreCore.Write(Rec(Thread("a", "claude-sonnet-5", null, "never", true), Thread("b", null, "quality", "scripts_and_deletions", false)));
      var read = SessionStoreCore.Read(json).Record.Threads;

      var a = read.Single(t => t.Id == "a");
      Assert.Equal("claude-sonnet-5", a.ModelId); Assert.Null(a.Posture); Assert.Equal("never", a.ApprovalMode); Assert.Equal(true, a.FullControl);
      var b = read.Single(t => t.Id == "b");
      Assert.Null(b.ModelId); Assert.Equal("quality", b.Posture); Assert.Equal("scripts_and_deletions", b.ApprovalMode); Assert.Equal(false, b.FullControl);
      Assert.Contains("\"modelId\"", json);
      Assert.Contains("\"posture\"", json);
    }

    [Fact]
    public void A_blank_pin_or_posture_is_none_and_a_pre_header_thread_has_neither()
    {
      var json = SessionStoreCore.Write(Rec(Thread("old", null, null), Thread("blank", "   ", "  ")));
      Assert.DoesNotContain("\"modelId\"", json);   // nothing is invented on write

      var read = SessionStoreCore.Read(json).Record.Threads;
      Assert.All(read, t => { Assert.Null(t.ModelId); Assert.Null(t.Posture); });
    }

    [Fact]
    public void A_pin_is_trimmed_and_a_posture_is_lower_cased_on_read_and_write()
    {
      var read = SessionStoreCore.Read(SessionStoreCore.Write(Rec(Thread("t", "  claude-opus-5 ", " QUALITY ")))).Record.Threads.Single();
      Assert.Equal("claude-opus-5", read.ModelId);
      Assert.Equal("quality", read.Posture);
    }

    [Fact]
    public void The_settings_default_is_not_on_the_read_path_of_a_threads_pin()
    {
      var cs = System.IO.File.ReadAllText(System.IO.Path.Combine(RepoRoot(), "Dwight", "Session", "CommandHistory.cs"));
      Assert.Contains("public static string ThreadModelId(GH_Document doc, string threadId) => FindThread(doc, threadId)?.ModelId ?? \"\";", cs);
      Assert.Contains("public static string ThreadPosture(GH_Document doc, string threadId) => FindThread(doc, threadId)?.Posture;", cs);
      Assert.Contains("public static void SetThreadApprovalMode(", cs);
      Assert.Contains("public static void SetThreadFullControl(", cs);
    }

    // ---- the ledger ---------------------------------------------------------------------------------------------------

    private static string LedgerJson(string verb) =>
      "{\"utc\":\"2026-10-30T10:00:00Z\",\"documentId\":\"d\",\"documentName\":\"n\",\"kind\":\"turn\",\"verb\":\"" + verb + "\",\"threadId\":\"t\",\"turn\":1," +
      "\"provider\":\"Anthropic\",\"model\":\"claude-sonnet-5\",\"inputTokens\":10,\"outputTokens\":5,\"cents\":1.5,\"seconds\":2}";

    [Fact]
    public void A_ledger_line_that_says_Scaffold_counts_under_Build_and_one_that_says_Build_is_unchanged()
    {
      Assert.Equal("Build", SpendLedgerCore.Parse(LedgerJson("Scaffold")).Verb);
      Assert.Equal("Build", SpendLedgerCore.Parse(LedgerJson("Build")).Verb);
      Assert.Equal("Explain", SpendLedgerCore.Parse(LedgerJson("Explain")).Verb);
    }

    [Fact]
    public void The_summary_counts_a_Scaffold_line_and_a_Build_line_together_under_Build()
    {
      var lines = new[] { LedgerJson("Scaffold"), LedgerJson("Build") };
      var sum = SpendLedgerCore.Summarise(lines, new System.DateTime(2026, 10, 30, 12, 0, 0), System.TimeZoneInfo.Utc);

      var byVerb = sum.ByVerb[(int)LedgerPeriod.AllTime];
      Assert.Equal(new[] { "Build" }, byVerb.Keys.ToArray());   // one bucket, and it is not called Scaffold
      Assert.Equal(2, byVerb["Build"].Turns);   // both lines, one bucket
      Assert.Equal(3.0, byVerb["Build"].Cents, 3);
    }

    private static string RepoRoot()
    {
      var dir = System.AppContext.BaseDirectory;
      while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir, "Dwight.sln")))
        dir = System.IO.Path.GetDirectoryName(dir);
      Assert.NotNull(dir);
      return dir;
    }
  }
}
