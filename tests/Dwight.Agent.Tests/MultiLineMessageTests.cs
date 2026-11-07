using System.Collections.Generic;
using System.Net;
using Dwight.Llm;
using Dwight.Session;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// Lane D (#153): a message the user types on two lines is two lines everywhere it goes - on their own
  /// screen, in the record, and in what the model reads.
  ///
  /// <para>The newline was traced through every hop before any of this was written. The composer posts the
  /// string verbatim (WebView2 <c>postMessage</c>); <c>WebPanel.Handle</c> takes it by <c>Substring</c>;
  /// <c>AppendUserText</c> keeps the raw text as the message's SOURCE, which is what the model reads. The one
  /// hop that broke was the DISPLAY: the screen copy is <c>HtmlEncode(text)</c>, which leaves the newline in
  /// the markup, and HTML collapses it to a space. So the model read two lines and the user's own bubble
  /// showed one. That is fixed in the shell (a <c>white-space:pre-wrap</c> rule, witnessed by the render
  /// harness); these tests hold the C# halves either side of it.</para>
  /// </summary>
  public class MultiLineMessageTests
  {
    /// <summary>
    /// THE PREMISE THE SHELL'S FIX STANDS ON: the encoder <c>AppendUserText</c> uses today leaves a newline
    /// in place rather than turning it into markup. <c>pre-wrap</c> is only right because the newline arrives
    /// as a character. If the encoder ever emitted <c>&lt;br&gt;</c> as well, the bubble would show a blank
    /// line between every line typed; if it stripped the newline, the fix would silently stop mattering.
    ///
    /// <para>STATED BOUND: this pins the ENCODER, not <c>AppendUserText</c> itself, which lives in the
    /// GH-coupled panel and cannot be run headlessly. A change to which encoder that door calls would not be
    /// seen here. It is a premise pin rather than a guard, so it passes on its first run by design - there is
    /// no way to see it fail without changing the platform.</para>
    /// </summary>
    [Fact]
    public void The_encoder_the_user_door_uses_leaves_a_newline_as_a_newline()
    {
      Assert.Equal("line one\nline two", WebUtility.HtmlEncode("line one\nline two"));
    }

    /// <summary>The same premise at the edges that would make it wrong: escaping still happens around the
    /// newline, so a user who types markup on two lines still sees their markup as text, on two lines.</summary>
    [Fact]
    public void Escaping_still_happens_around_the_newline()
    {
      Assert.Equal("&lt;b&gt;one&lt;/b&gt;\ntwo", WebUtility.HtmlEncode("<b>one</b>\ntwo"));
    }

    // ---- the model's side: a two-line message ARRIVES as two lines ------------------------------------

    /// <summary>What the composer posts for two lines (asserted exactly by the render harness), and an
    /// indented second line, because the model receives spaces verbatim too.</summary>
    private const string Typed = "line one\n  line two";

    /// <summary>A user message exactly as <c>AppendUserText</c> records it: the escaped screen copy, and the
    /// raw text as its SOURCE - the half the model reads.</summary>
    private static StoredMessage AsRecorded(string typed) => new StoredMessage
    {
      Who = "me",
      Html = WebUtility.HtmlEncode(typed),
      Src = typed,
      Utc = "2026-09-19T00:00:00.0000000Z",
    };

    [Fact]
    public void A_two_line_message_reaches_the_model_as_two_lines()
    {
      var transcript = ChatToTranscript.Build(new List<StoredMessage> { AsRecorded(Typed) });

      var only = Assert.Single(transcript);
      Assert.Equal(LlmRole.User, only.Role);
      Assert.Equal(Typed, only.Blocks[0].Text);
    }

    /// <summary>
    /// THE HOP THE BRIEF DID NOT NAME, found by tracing: a REOPENED thread builds the model's transcript from
    /// the record on disk, not from memory. So the newline has to survive the store too - written, read back,
    /// and only then handed to the model. Below the store's size bound nothing touches the text
    /// (<c>ChatBudget.TrimText</c> acts only past it, and cuts by <c>Substring</c>, never by whitespace).
    /// </summary>
    [Fact]
    public void A_reopened_thread_still_hands_the_model_two_lines()
    {
      var rec = new SessionRecord
      {
        Threads = new List<StoredThread>
        {
          new StoredThread { Id = "t1", Messages = new List<StoredMessage> { AsRecorded(Typed) } },
        },
      };

      var reopened = SessionStoreCore.Read(SessionStoreCore.Write(rec));
      var messages = Assert.Single(reopened.Record.Threads).Messages;

      Assert.Equal(Typed, Assert.Single(ChatToTranscript.Build(messages)).Blocks[0].Text);
    }

    /// <summary>... and the SCREEN copy survives the store with its newline, which is what lets the shell's
    /// pre-wrap rule show a reopened thread's message on two lines, the same as the live one.</summary>
    [Fact]
    public void A_reopened_thread_still_shows_two_lines()
    {
      var rec = new SessionRecord
      {
        Threads = new List<StoredThread>
        {
          new StoredThread { Id = "t1", Messages = new List<StoredMessage> { AsRecorded(Typed) } },
        },
      };

      var reopened = SessionStoreCore.Read(SessionStoreCore.Write(rec));
      var html = Assert.Single(Assert.Single(reopened.Record.Threads).Messages).Html;

      Assert.Equal(WebUtility.HtmlEncode(Typed), html);
      Assert.Contains("\n", html);
    }
  }
}
