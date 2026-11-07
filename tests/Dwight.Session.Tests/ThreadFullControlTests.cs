using System.Collections.Generic;
using System.Linq;
using Xunit;
using Dwight.Session;

namespace Dwight.Session.Tests
{
  /// <summary>
  /// FULL CONTROL IS CARRIED BY THE CONVERSATION (U90). It is copied from the Settings default once, when the thread is opened, and persisted with
  /// the thread, so a restart keeps a conversation's choice and turning the default on never switches on a conversation already open. A thread from
  /// before the switch existed has none stored and reads as OFF. The thread also remembers that it has shown a run_script card, so the second opens folded.
  /// </summary>
  public class ThreadFullControlTests
  {
    private static StoredThread Thread(string id, bool? fullControl, bool? shown = null) => new StoredThread
    {
      Id = id,
      Phase = "plan",
      FullControl = fullControl,
      RunScriptCardShown = shown,
      Messages = new List<StoredMessage> { new StoredMessage { Who = "dw", Html = "<p>hi</p>" } },
    };

    private static SessionRecord Rec(params StoredThread[] threads) => new SessionRecord
    {
      DocumentId = "66fd7275-7717-4065-90eb-483736e9296a",
      Labels = new List<string> { @"C:\work\site.gh" },
      Fingerprint = "ABCD",
      Threads = threads.ToList(),
    };

    [Fact]
    public void A_reopened_session_file_carries_each_threads_own_switch_and_its_shown_flag()
    {
      var json = SessionStoreCore.Write(Rec(Thread("on", true, true), Thread("off", false)));
      var read = SessionStoreCore.Read(json).Record.Threads;

      Assert.Equal(true, read.Single(t => t.Id == "on").FullControl);
      Assert.Equal(true, read.Single(t => t.Id == "on").RunScriptCardShown);
      Assert.Equal(false, read.Single(t => t.Id == "off").FullControl);
      Assert.Contains("\"fullControl\"", json);
      Assert.Contains("\"runScriptCardShown\"", json);
    }

    [Fact]
    public void A_thread_from_before_the_switch_has_none_stored_and_reads_as_off()
    {
      var json = SessionStoreCore.Write(Rec(Thread("old", null)));
      Assert.DoesNotContain("\"fullControl\"", json);   // nothing is invented on write

      var t = SessionStoreCore.Read(json).Record.Threads.Single();
      Assert.Null(t.FullControl);
      Assert.False(t.FullControl == true);   // the reader's rule: only an explicit true is on
      Assert.Null(t.RunScriptCardShown);
    }

    [Fact]
    public void A_switch_survives_a_read_write_cycle_unchanged()
    {
      var once = SessionStoreCore.Write(Rec(Thread("t", true, true)));
      var twice = SessionStoreCore.Write(SessionStoreCore.Read(once).Record);
      Assert.Equal(once, twice);
    }

    [Fact]
    public void The_history_reads_the_thread_alone_and_never_the_settings_default()
    {
      // the reader is CommandHistory.ThreadFullControl(doc, threadId): two parameters, neither of them a setting, and it returns only an explicit true
      var cs = System.IO.File.ReadAllText(System.IO.Path.Combine(RepoRoot(), "Dwight", "Session", "CommandHistory.cs"));
      Assert.Contains("public static bool ThreadFullControl(GH_Document doc, string threadId) =>", cs);
      Assert.Contains("FindThread(doc, threadId)?.FullControl == true;", cs);
      Assert.Contains("if (t.FullControl == null && fullControl != null) t.FullControl = fullControl;", cs);   // copied once; never overwritten
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
