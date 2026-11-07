using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;
using Dwight.Agent;
using Dwight.Session;

namespace Dwight.Session.Tests
{
  /// <summary>
  /// A BUILD CONVERSATION CARRIES ITS OWN APPROVAL MODE (Brief AL). It is copied from the Settings default when the thread is opened and
  /// persisted with the thread, so a restart keeps a thread's choice and changing the default never changes an open thread. The
  /// store's side of that is GH-free and tested here; what reads it back is <see cref="ApprovalMode.ForThread"/>, which takes no
  /// setting, so the default has no path to an open conversation.
  /// </summary>
  public class ThreadApprovalModeTests
  {
    private static StoredThread Thread(string id, string mode) => new StoredThread
    {
      Id = id,
      Phase = "plan",
      ApprovalMode = mode,
      Messages = new List<StoredMessage> { new StoredMessage { Who = "dw", Html = "<p>hi</p>" } },
    };

    private static SessionRecord Rec(params StoredThread[] threads) => new SessionRecord
    {
      DocumentId = "66fd7275-7717-4065-90eb-483736e9296a",
      Labels = new List<string> { @"C:\work\site.gh" },
      Fingerprint = "ABCD",
      Threads = threads.ToList(),
    };

    [Theory]
    [InlineData("ask")]
    [InlineData("scripts_and_deletions")]
    [InlineData("never")]
    public void A_reopened_session_file_carries_each_threads_mode(string mode)
    {
      var json = SessionStoreCore.Write(Rec(Thread("t1", mode)));
      var read = SessionStoreCore.Read(json);

      Assert.Equal(StoreReadOutcome.Ok, read.Outcome);
      Assert.Equal(mode, read.Record.Threads.Single().ApprovalMode);
      Assert.Equal(mode, ApprovalMode.ForThread(read.Record.Threads.Single().ApprovalMode));
      Assert.Contains("\"approvalMode\"", json);
    }

    /// <summary>Two threads in one file, opened under different defaults, each keep their own: the mode is a property of the thread.</summary>
    [Fact]
    public void A_thread_opened_under_one_default_keeps_its_mode_when_the_default_changes()
    {
      var opened = SessionStoreCore.Write(Rec(Thread("older", "never"), Thread("newer", "ask")));
      var read = SessionStoreCore.Read(opened).Record.Threads;

      // the Settings default is now something else entirely; nothing on the read path takes it
      var settingsDefaultNow = ApprovalMode.ScriptsAndDeletions;
      Assert.Equal("never", ApprovalMode.ForThread(read.Single(t => t.Id == "older").ApprovalMode));
      Assert.Equal("ask", ApprovalMode.ForThread(read.Single(t => t.Id == "newer").ApprovalMode));
      Assert.NotEqual(settingsDefaultNow, ApprovalMode.ForThread(read.Single(t => t.Id == "older").ApprovalMode));
    }

    [Fact]
    public void A_thread_from_before_modes_has_none_stored_stays_without_one_and_reads_as_ask()
    {
      var json = SessionStoreCore.Write(Rec(Thread("old", null)));
      Assert.DoesNotContain("\"approvalMode\":\"", json.Replace(" ", ""));   // nothing is invented on write

      var t = SessionStoreCore.Read(json).Record.Threads.Single();
      Assert.Null(t.ApprovalMode);
      Assert.Equal("ask", ApprovalMode.ForThread(t.ApprovalMode));
    }

    [Fact]
    public void A_hand_edited_mode_in_the_file_is_the_default_never_a_mode_nobody_chose()
    {
      var json = SessionStoreCore.Write(Rec(Thread("t1", "never")));
      var edited = Regex.Replace(json, "\"approvalMode\"\\s*:\\s*\"never\"", "\"approvalMode\": \"do-whatever\"");
      Assert.NotEqual(json, edited);

      Assert.Equal("ask", SessionStoreCore.Read(edited).Record.Threads.Single().ApprovalMode);
    }

    [Fact]
    public void A_mode_survives_a_read_write_cycle_unchanged()
    {
      var once = SessionStoreCore.Write(Rec(Thread("t1", "scripts_and_deletions")));
      var twice = SessionStoreCore.Write(SessionStoreCore.Read(once).Record);
      Assert.Equal(once, twice);
    }
  }
}
