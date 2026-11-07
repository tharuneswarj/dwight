using System;
using System.IO;
using System.Linq;
using Dwight.Llm;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// Lane B (#873): the recorder the new stream reader's fixtures come from. A fixture is only worth what the
  /// recording is faithful to, so these hold the three properties a test fixture depends on: the stream is
  /// kept EXACTLY (blank lines included - they are where one SSE event ends), what failed to parse is counted
  /// WITH its denominator and a sample, and the bounds are stated rather than silent.
  ///
  /// <para>One claim no test here can hold, stated so it is not implied: that a capture never contains the API
  /// key. That is STRUCTURAL - the recorder is fed response lines only, and the key rides a request header -
  /// so it is argued from where the calls sit in the three readers, not asserted.</para>
  /// </summary>
  [Collection(StreamFixtures.CaptureWindow)]   // U38: the readers' tests record into the same static window
  public class StreamCaptureTests : IDisposable
  {
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dwight-capture-test-" + Guid.NewGuid().ToString("N"));

    /// <summary>The document every test opens its window on. Recording outside a window records nothing, so
    /// the fidelity tests below run inside one - exactly as the product only records inside one.</summary>
    private const string Scratch = "scratch-doc";

    public StreamCaptureTests()
    {
      StreamCapture.Reset();
      StreamCapture.Open(Scratch);
    }

    public void Dispose()
    {
      StreamCapture.Reset();
      try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
    }

    private static void Record(string provider, params string[] lines)
    {
      using (var cap = StreamCapture.Begin(provider))
        foreach (var l in lines) cap.Line(l);
    }

    private string OnlyFileText()
    {
      StreamCapture.WriteAll(_dir, Scratch);
      return File.ReadAllText(Assert.Single(Directory.GetFiles(_dir)));
    }

    // ---- the stream is kept exactly ----------------------------------------------------------------------

    /// <summary>THE PROPERTY A FIXTURE STANDS ON. SSE separates events with a blank line, so a recorder that
    /// dropped blank lines would hand the new reader's tests a stream in which every event has been welded to
    /// the next - a fixture that is not what the provider sent.</summary>
    [Fact]
    public void A_stream_is_written_exactly_as_read_blank_lines_included()
    {
      Record("anthropic",
        "event: content_block_delta",
        "data: {\"type\":\"content_block_delta\"}",
        "",
        "event: message_stop",
        "data: {\"type\":\"message_stop\"}",
        "");

      Assert.Equal(
        "event: content_block_delta\ndata: {\"type\":\"content_block_delta\"}\n\n" +
        "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n",
        OnlyFileText());
    }

    [Fact]
    public void Nothing_is_written_to_disk_until_asked()
    {
      Record("google", "data: {}");

      Assert.False(Directory.Exists(_dir));
    }

    [Fact]
    public void A_capture_is_filed_under_the_provider_that_sent_it()
    {
      Record("ollama", "data: {}");
      StreamCapture.WriteAll(_dir, Scratch);

      Assert.StartsWith("ollama-", Path.GetFileName(Assert.Single(Directory.GetFiles(_dir))));
    }

    // ---- the counter, with its denominator -----------------------------------------------------------------

    /// <summary>What the silent catch has been absorbing, per provider, WITH the denominator (#212) and a
    /// sample. "0 of 0" and "0 of 4000" print differently here, and a non-zero comes with the payload that
    /// caused it - which is the evidence that decides whether the new reader may refuse such an event.</summary>
    [Fact]
    public void The_counter_reports_failures_with_their_denominator_and_a_sample()
    {
      using (var cap = StreamCapture.Begin("google"))
      {
        cap.Data(); cap.Data(); cap.Data();
        cap.FailedToParse("[DONE]");
      }

      var report = StreamCapture.CounterReport();

      Assert.Contains("google: 1 stream(s), 3 data payload(s), 1 failed to parse", report);
      Assert.Contains("unparsed: [DONE]", report);
    }

    [Fact]
    public void The_counter_says_when_nothing_has_been_read_rather_than_printing_a_zero()
    {
      Assert.Equal("No reply stream has been read in this capture window.", StreamCapture.CounterReport());
    }

    [Fact]
    public void Counts_accumulate_across_streams_so_a_session_is_one_reading()
    {
      for (var i = 0; i < 3; i++)
        using (var cap = StreamCapture.Begin("openai")) { cap.Data(); cap.Data(); }

      Assert.Contains("openai: 3 stream(s), 6 data payload(s), 0 failed to parse", StreamCapture.CounterReport());
    }

    // ---- the bounds, stated rather than silent -------------------------------------------------------------

    [Fact]
    public void Only_the_most_recent_streams_are_kept_per_provider()
    {
      for (var i = 0; i < StreamCapture.KeepPerProvider + 3; i++)
        Record("anthropic", "data: {\"n\":" + i + "}");

      StreamCapture.WriteAll(_dir, Scratch);
      var kept = Directory.GetFiles(_dir).Select(File.ReadAllText).ToList();

      Assert.Equal(StreamCapture.KeepPerProvider, kept.Count);
      Assert.DoesNotContain(kept, t => t.Contains("{\"n\":0}"));   // the oldest went
      Assert.Contains(kept, t => t.Contains("{\"n\":" + (StreamCapture.KeepPerProvider + 2) + "}"));   // the newest stayed
    }

    /// <summary>A stream past the cap is MARKED truncated in the report - a fixture that silently lost its tail
    /// would be a stream the provider never sent, presented as one it did.</summary>
    [Fact]
    public void A_stream_past_the_cap_is_reported_as_truncated()
    {
      var big = new string('x', StreamCapture.MaxCharsPerStream);
      Record("anthropic", "data: {}", big);

      Assert.Contains("TRUNCATED at the cap", StreamCapture.WriteAll(_dir, Scratch));
    }

    /// <summary>A recording is kept once however many times it is disposed - so a reader that disposes it on
    /// an error path and again on the way out cannot double the denominator.</summary>
    [Fact]
    public void Disposing_twice_does_not_count_a_stream_twice()
    {
      var cap = StreamCapture.Begin("anthropic");
      cap.Data();
      cap.Dispose();
      cap.Dispose();

      Assert.Contains("anthropic: 1 stream(s), 1 data payload(s)", StreamCapture.CounterReport());
    }

    // ---- THE CAPTURE WINDOW (#873 review): client content cannot reach a fixture ------------------------
    //
    // The first version kept streams from ANY use of Dwight for the whole session, so a client definition
    // explained in the morning could be written by an afternoon's "scratch" capture - and then committed as a
    // test fixture. These hold that it cannot happen by construction, whatever anyone remembers.

    /// <summary>THE LEAK, CLOSED. A reply read before the window opened is not "recorded and not written" - it
    /// was never recorded, so there is nothing for anyone to write.</summary>
    [Fact]
    public void A_reply_read_before_the_window_opened_is_never_recorded_let_alone_written()
    {
      StreamCapture.Reset();                                  // no window: the morning's client work
      Assert.Null(StreamCapture.Begin("anthropic"));

      StreamCapture.Open(Scratch);                            // the afternoon's scratch capture
      Record("anthropic", "data: {\"scratch\":true}");
      StreamCapture.WriteAll(_dir, Scratch);

      Assert.Contains("scratch", File.ReadAllText(Assert.Single(Directory.GetFiles(_dir))));
    }

    /// <summary>Opening a window forgets everything before it - including a previous window's leftovers.</summary>
    [Fact]
    public void Opening_a_window_clears_everything_recorded_before_it()
    {
      Record("anthropic", "data: {\"earlier\":true}");
      StreamCapture.Open(Scratch);
      Record("anthropic", "data: {\"now\":true}");

      var text = OnlyFileText();
      Assert.Contains("now", text);
      Assert.DoesNotContain("earlier", text);
    }

    /// <summary>A write with no window refuses IN WORDS and writes nothing - the session-wide buffer the first
    /// version kept can no longer be written by accident, because it no longer exists.
    ///
    /// <para>It asserts WHICH refusal, not just that one happened. Its first version checked only for
    /// "REFUSED" - and with the no-window guard deleted it still passed, because the different-document guard
    /// refused instead (after a reset the window's document is null). Right outcome, wrong reason: a test that
    /// cannot tell which guard fired is not a test of either. Found by seeing it fail, which it did not.</para></summary>
    [Fact]
    public void Writing_with_no_window_open_refuses_and_writes_nothing()
    {
      StreamCapture.Reset();

      var result = StreamCapture.WriteAll(_dir, Scratch);

      Assert.StartsWith("REFUSED", result);
      Assert.Contains("no capture window is open", result);
      Assert.False(Directory.Exists(_dir));
    }

    /// <summary>Written from a different document than the window was opened on: refused, nothing written, and
    /// the window stays open so switching back and writing still works.</summary>
    [Fact]
    public void Writing_from_a_different_document_refuses_and_writes_nothing()
    {
      Record("anthropic", "data: {}");

      var result = StreamCapture.WriteAll(_dir, "a-client-doc");

      Assert.StartsWith("REFUSED", result);
      Assert.Contains("different document", result);   // this guard, by name - not whichever refused first
      Assert.False(Directory.Exists(_dir));
      Assert.True(StreamCapture.IsOpen);
    }

    /// <summary>One window, one write: afterwards the window is closed, a second write refuses, and a reply read
    /// after it records nothing.</summary>
    [Fact]
    public void Writing_closes_the_window_so_nothing_is_written_twice()
    {
      Record("anthropic", "data: {}");
      StreamCapture.WriteAll(_dir, Scratch);

      Assert.False(StreamCapture.IsOpen);
      Assert.StartsWith("REFUSED", StreamCapture.WriteAll(_dir, Scratch));
      Assert.Null(StreamCapture.Begin("anthropic"));
    }

    /// <summary>A reply still streaming when its window closes is DROPPED - it does not wait in memory to join a
    /// later window it never belonged to. Its failure sample is reply content too, so it is dropped as well.</summary>
    [Fact]
    public void A_reply_still_streaming_when_the_window_closes_is_dropped_with_its_samples()
    {
      var inFlight = StreamCapture.Begin("google");
      StreamCapture.WriteAll(_dir, Scratch);                  // the window closes mid-stream
      inFlight.FailedToParse("reply text from after the window");
      inFlight.Dispose();

      StreamCapture.Open(Scratch);
      Assert.Equal("No reply stream has been read in this capture window.", StreamCapture.CounterReport());
    }
  }
}
