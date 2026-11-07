using System.Collections.Generic;
using System.Linq;
using Dwight.Session;
using Xunit;

namespace Dwight.Session.Tests
{
  /// <summary>
  /// Phase-9 U4: a PLUGIN REPORT is a new VALUE in an existing field (StoredMessage.Who = "report") - the
  /// plugin's own account of a change it applied on the user's approval. WP-1's law is that the text shown
  /// is the text recorded is the text replayed, and a fourth door has to satisfy it like the other three:
  /// the store must carry the author value whole across a write and a read, and the transcript selector
  /// must keep the message as conversation (it has a source) rather than chrome.
  /// </summary>
  public class ReportRoundTripTests
  {
    private static StoredMessage RoundTrip(StoredMessage m)
    {
      var rec = new SessionRecord
      {
        Threads = new List<StoredThread> { new StoredThread { Id = "t1", Messages = new List<StoredMessage> { m } } },
      };
      return SessionStoreCore.Read(SessionStoreCore.Write(rec)).Record.Threads[0].Messages[0];
    }

    [Fact]
    public void A_REPORT_ROUND_TRIPS_WHOLE_AUTHOR_HTML_AND_SOURCE()
    {
      var m = RoundTrip(new StoredMessage
      {
        Who = "report",
        Html = "<p><strong>Applied.</strong> Number Slider \"Count\" (42) from 0.25 to 40.</p>",
        Src = "[Applied by Dwight on the user's approval] Number Slider \"Count\" (42) from 0.25 to 40.",
        Utc = "2026-09-08T00:00:00.0000000Z",
      });

      Assert.Equal("report", m.Who);   // never coerced to "dw" or "me": the author value is the discriminator
      Assert.StartsWith("[Applied by Dwight", m.Src);
      Assert.Contains("<strong>Applied.</strong>", m.Html);
    }

    [Fact]
    public void A_REPORT_IS_CONVERSATION_NOT_CHROME()
    {
      // It carries a source, so the model sees it; chrome (no source) is excluded by construction (#228).
      var kept = ChatTranscript.SelectConversation(new[]
      {
        new StoredMessage { Who = "dw", Html = "<p>greeting</p>", Src = null },
        new StoredMessage { Who = "report", Html = "<p>Applied.</p>", Src = "[Applied by Dwight on the user's approval] x" },
      });
      Assert.Single(kept);
      Assert.Equal("report", kept[0].Who);

      // And the screen-side replay carries the author value verbatim, so the shell renders it as Dwight
      // on a reopen exactly as it did when it landed (the text shown is the text replayed).
      var json = ChatTranscript.ToChatJson(new[] { new StoredMessage { Who = "report", Html = "<p>Applied.</p>", Src = "x" } });
      Assert.Contains("\"who\":\"report\"", json);
    }

    [Fact]
    public void THE_SCHEMA_MINOR_DECLARES_THE_NEW_VALUE()
    {
      // A new value in an existing field is additive - an older build reads it as a string - but it must
      // be DECLARED, because the older build would render it as a user bubble and map it as the model's
      // words; the minor is how a reader learns the file may hold one.
      Assert.True(SessionStoreCore.SchemaMinor >= 3);
      var rec = SessionStoreCore.Read(SessionStoreCore.Write(new SessionRecord())).Record;
      Assert.Equal(SessionStoreCore.SchemaMinor, rec.Minor);
    }
  }
}
