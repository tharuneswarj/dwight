using System.Collections.Generic;
using System.Linq;
using Dwight.Session;
using Xunit;

namespace Dwight.Session.Tests
{
  /// <summary>
  /// The message SOURCE (WP-3 Stage A) - the author's original text kept beside the rendered HTML, so a
  /// reopened conversation can be handed back to the model rather than starting over.
  ///
  /// The property worth protecting: HTML is DERIVED from the source by the door that knows the author, so
  /// the two cannot drift. What the store must do is carry both, bound both, and preserve a newer build's
  /// fields rather than dropping them.
  /// </summary>
  public class MessageSourceTests
  {
    private static SessionRecord WithMessage(StoredMessage m) => new SessionRecord
    {
      Threads = new List<StoredThread> { new StoredThread { Id = "t1", Messages = new List<StoredMessage> { m } } },
    };

    private static StoredMessage RoundTrip(StoredMessage m) =>
      SessionStoreCore.Read(SessionStoreCore.Write(WithMessage(m))).Record.Threads[0].Messages[0];

    [Fact]
    public void The_source_round_trips_beside_the_rendered_html()
    {
      var m = RoundTrip(new StoredMessage
      {
        Who = "dw",
        Html = "<p>The <strong>Loft</strong> (42) builds the roof.</p>",
        Src = "The **Loft** (42) builds the roof.",
        Utc = "2026-08-17T00:00:00.0000000Z",
      });

      Assert.Equal("The **Loft** (42) builds the roof.", m.Src);
      Assert.Equal("<p>The <strong>Loft</strong> (42) builds the roof.</p>", m.Html);
    }

    [Fact]
    public void Chrome_carries_no_source_and_stays_that_way()
    {
      // A null source is what marks a message as UI rather than conversation - a greeting, a notice, a
      // consent card - so the model transcript excludes it by construction. Coercing it to "" would
      // quietly enrol Dwight's own chrome into the conversation.
      var m = RoundTrip(new StoredMessage { Who = "dw", Html = "<p>hello</p>", Utc = "2026-08-17T00:00:00Z" });
      Assert.Null(m.Src);
    }

    [Fact]
    public void An_oversized_source_is_truncated_on_the_same_budget_with_a_marker()
    {
      var huge = new string('s', SessionStoreCore.MaxMessageChars * 3);
      var m = RoundTrip(new StoredMessage { Who = "me", Html = "<p>x</p>", Src = huge, Utc = "u" });

      Assert.True(m.Src.Length <= SessionStoreCore.MaxMessageChars);
      Assert.Contains("the rest is not stored", m.Src);
      Assert.DoesNotContain("<", m.Src);   // plain text: no tag balancing, and nothing to inject
    }

    [Fact]
    public void Truncating_the_source_is_idempotent()
    {
      var once = ChatBudget.TrimText(new string('s', 4000), 500);
      Assert.Equal(once, ChatBudget.TrimText(once, 500));
      Assert.True(once.Length <= 500);
    }

    [Fact]
    public void The_thread_budget_counts_the_source_as_well_as_the_html()
    {
      // The source is real bytes in the file AND real tokens on the next turn's wire. A size bound that
      // counted only the rendered half would under-count every message by about half.
      var withSrc = new StoredMessage { Who = "me", Html = "<p>abc</p>", Src = "abc", Utc = "u" };
      var withoutSrc = new StoredMessage { Who = "me", Html = "<p>abc</p>", Utc = "u" };

      Assert.Equal(ChatBudget.Size(withoutSrc) + 3, ChatBudget.Size(withSrc));
    }

    [Fact]
    public void A_newer_builds_message_fields_survive_an_older_builds_read_write_cycle()
    {
      // The preservation rule (#167) reaching StoredMessage - added with the first field this type ever
      // gained, which is exactly when its absence would have started costing something. Without it, a
      // build that predates the next field would silently strip it on the first save.
      const string json =
        "{\"schema\":1,\"minor\":9,\"threads\":[{\"id\":\"t1\",\"messages\":[" +
        "{\"who\":\"dw\",\"html\":\"<p>hi</p>\",\"src\":\"hi\",\"utc\":\"u\",\"tone\":\"warm\"}]}]}";

      var reread = SessionStoreCore.Write(SessionStoreCore.Read(json).Record);

      Assert.Contains("\"tone\"", reread);
      Assert.Contains("\"warm\"", reread);
    }

    /// <summary>
    /// #291's anchors, both directions of the compatibility question.
    ///
    /// FORWARD is what the test above already proves generically: "tone" stands for any field a NEWER build
    /// adds, and anchors is simply the first real one to arrive since - an older build carries it through
    /// JsonExtensionData rather than stripping it on the first save.
    ///
    /// BACKWARD is this: a record written before anchors existed reads as a message with none, not as a
    /// failure - so a thread that predates the feature simply has nothing to click.
    /// </summary>
    [Fact]
    public void Anchors_round_trip_and_a_record_without_them_reads_as_none()
    {
      var withAnchors = SessionStoreCore.Read(SessionStoreCore.Write(WithMessage(
        new StoredMessage { Who = "dw", Html = "<p>the Loft (7)</p>", Src = "the Loft (7)",
                            Anchors = new List<int> { 7, 9 } })));
      Assert.Equal(new[] { 7, 9 }, withAnchors.Record.Threads[0].Messages[0].Anchors);

      const string older =
        "{\"schema\":1,\"minor\":1,\"threads\":[{\"id\":\"t1\",\"messages\":[" +
        "{\"who\":\"dw\",\"html\":\"<p>hi</p>\",\"src\":\"hi\"}]}]}";
      Assert.Null(SessionStoreCore.Read(older).Record.Threads[0].Messages[0].Anchors);
    }

    [Fact]
    public void A_message_cannot_carry_an_unbounded_anchor_list()
    {
      var many = Enumerable.Range(1, SessionStoreCore.MaxAnchorsPerMessage + 40).ToList();
      var read = SessionStoreCore.Read(SessionStoreCore.Write(WithMessage(
        new StoredMessage { Who = "dw", Html = "x", Src = "x", Anchors = many })));

      var kept = read.Record.Threads[0].Messages[0].Anchors;
      Assert.Equal(SessionStoreCore.MaxAnchorsPerMessage, kept.Count);
      Assert.Equal(1, kept[0]);   // trimmed from the tail: what it leads with is what it is about
    }
  }
}
