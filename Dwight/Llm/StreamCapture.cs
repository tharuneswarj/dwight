using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Dwight.Llm
{
  /// <summary>
  /// The raw reply streams, captured INSIDE the plugin, so a test can be written against what each provider
  /// really sends (lane B, #873).
  ///
  /// <para>WHY IT EXISTS. All three stream readers skip an event that fails to parse, in silence. Replacing
  /// that with a refusal is only safe if "fails to parse" really means "damaged" - and the only authority on
  /// what a provider legitimately sends is the provider. OpenAI's <c>[DONE]</c> sentinel is handled before
  /// the parse; GOOGLE's reader has no such check, so whatever non-JSON event the silent catch has been
  /// absorbing there is unknown. Refuse it blind and every reply from that provider dies. CURRENT's law is
  /// the same point from the other side: <b>test a reader against the writer's REAL output</b> - a fixture
  /// typed from documentation is the test's idea of the writer (#649).</para>
  ///
  /// <para>THE KEY NEVER LEAVES THE PLUGIN. It rides a request header; this records the RESPONSE body only,
  /// which never contains it. Nothing here reads the credential store (#873's hard line).</para>
  ///
  /// <para>WHAT IT HOLDS: the reply text the model wrote - so it records ONLY INSIDE A CAPTURE WINDOW (see
  /// <see cref="Open"/>). Outside one, nothing is recorded at all. A committed fixture is in git history for
  /// good and client work must never be (ADR-042's exception), so that has to hold by construction rather
  /// than by anyone remembering which document they used earlier in the day.</para>
  ///
  /// <para>DEBUG-only, like <see cref="LlmTrace"/>: <see cref="Begin"/> returns null in Release, so every
  /// provider call site costs a null check and nothing else.</para>
  /// </summary>
  internal static class StreamCapture
  {
    /// <summary>Streams kept per provider. A Scaffold turn is several streams - the steps that CALL a tool
    /// stream <c>tool_use</c>, the last one streams prose - so "the last stream" alone would usually be the
    /// one without the arguments in it.</summary>
    public const int KeepPerProvider = 6;

    /// <summary>One stream's cap. Past it the stream is marked truncated rather than cut silently.</summary>
    public const int MaxCharsPerStream = 256 * 1024;

    /// <summary>How many unparseable payloads are kept as samples, per provider.</summary>
    public const int MaxSamples = 5;

    /// <summary>One reply stream, line by line, exactly as the reader saw it - blank lines included, since
    /// a blank line is where one SSE event ends and the next begins.
    ///
    /// <para>IDisposable so a reader can stack it on its existing <c>using</c>: it is then kept on EVERY exit
    /// path - including a thrown error event or a Stop, which are exactly the streams worth having - with no
    /// try/finally re-indenting a whole reader. <c>using (null)</c> is legal, so Release costs nothing.</para></summary>
    internal sealed class Recording : IDisposable
    {
      public string Provider;
      public DateTime StartedUtc;
      public readonly StringBuilder Text = new StringBuilder();
      public bool Truncated;
      public int DataPayloads;
      public int Unparsed;
      private bool _kept;

      public void Line(string line)
      {
        if (line == null || Truncated) return;
        if (Text.Length + line.Length + 1 > MaxCharsPerStream) { Truncated = true; return; }
        Text.Append(line).Append('\n');
      }

      /// <summary>Called for every non-empty <c>data:</c> payload the reader considers.</summary>
      public void Data() => DataPayloads++;

      /// <summary>Called where the reader's parse FAILED and it skipped the event. This is the measurement:
      /// what the silent catch has been absorbing, with a sample of each.</summary>
      public void FailedToParse(string payload)
      {
        Unparsed++;
        StreamCapture.Sample(Provider, payload);
      }

      /// <summary>Kept once, however many times it is disposed.</summary>
      public void Dispose()
      {
        if (_kept) return;
        _kept = true;
        StreamCapture.Keep(this);
      }
    }

    /// <summary>Per provider: the counter totals across every stream read this session, the samples of what
    /// failed, and the recent streams themselves.</summary>
    private sealed class ProviderLog
    {
      public int Streams;
      public int DataPayloads;
      public int Unparsed;
      public readonly List<string> Samples = new List<string>();
      public readonly Queue<Recording> Recent = new Queue<Recording>();
    }

    private static readonly object _gate = new object();
    private static readonly SortedDictionary<string, ProviderLog> _logs = new SortedDictionary<string, ProviderLog>(StringComparer.Ordinal);

    // ---- THE CAPTURE WINDOW (#873 review) --------------------------------------------------------------
    //
    // The first version kept the last streams from ANY use of Dwight for the whole Rhino session. So a
    // client definition explained in the morning, then a scratch document opened for captures in the
    // afternoon, and the capture item would write the CLIENT-derived replies to disk - and the parse-failure
    // samples came from the same buffer. Those files are read and committed as test fixtures, and client
    // definitions never entering git is the one rule this project never bends. "(SCRATCH DOC)" in a menu
    // label is a request, not a guarantee; this makes it hold BY CONSTRUCTION:
    //
    //   - nothing is recorded AT ALL unless a window is open - not "recorded and not written", NOT RECORDED,
    //     so text from outside the window never sits in memory to be written by anyone;
    //   - opening a window clears everything before it;
    //   - writing refuses, in words, when no window is open - and writing CLOSES it, so a buffer can never
    //     be written twice or picked up later by accident.

    private static bool _open;
    private static string _windowDocument;

    /// <summary>True while a capture window is open.</summary>
    public static bool IsOpen { get { lock (_gate) return _open; } }

    /// <summary>
    /// Open a capture window on <paramref name="documentKey"/>: forget everything recorded before this moment,
    /// and record from now until <see cref="WriteAll"/>. The key is the document the window was opened on, so a
    /// write from a DIFFERENT document can be refused.
    /// </summary>
    public static void Open(string documentKey)
    {
      lock (_gate)
      {
        _logs.Clear();
        _open = true;
        _windowDocument = documentKey ?? "";
      }
    }

    /// <summary>Start recording one reply stream - or return null, which records nothing, when no window is open
    /// or in a Release build. A call site's cost outside a window is a null check.</summary>
    public static Recording Begin(string provider)
    {
#if DEBUG
      lock (_gate) { if (!_open) return null; }
      return new Recording { Provider = provider ?? "unknown", StartedUtc = DateTime.UtcNow };
#else
      return null;
#endif
    }

    private static ProviderLog LogFor(string provider)
    {
      if (!_logs.TryGetValue(provider, out var log)) _logs[provider] = log = new ProviderLog();
      return log;
    }

    private static void Keep(Recording r)
    {
      if (r == null) return;
      lock (_gate)
      {
        // A stream still finishing when its window was written or closed is dropped, not kept for a later
        // window it never belonged to.
        if (!_open) return;
        var log = LogFor(r.Provider);
        log.Streams++;
        log.DataPayloads += r.DataPayloads;
        log.Unparsed += r.Unparsed;
        log.Recent.Enqueue(r);
        while (log.Recent.Count > KeepPerProvider) log.Recent.Dequeue();
      }
    }

    private static void Sample(string provider, string payload)
    {
      lock (_gate)
      {
        if (!_open) return;   // a sample IS reply content, so it obeys the window exactly as a stream does
        var log = LogFor(provider ?? "unknown");
        if (log.Samples.Count >= MaxSamples) return;
        var p = payload ?? "";
        log.Samples.Add(p.Length > 160 ? p.Substring(0, 160) + " ... (" + p.Length + " chars)" : p);
      }
    }

    /// <summary>
    /// THE COUNTER, WITH ITS DENOMINATOR (#212): for each provider, how many data payloads were read and how
    /// many failed to parse, with samples of the failures. "0 of 0" and "0 of 4000" are different readings
    /// and would print identically without the denominator. A non-zero count WITH its samples is the direct
    /// answer to the question the new reader turns on: is this a damaged event, or a legitimate one the
    /// silent catch was hiding?
    /// </summary>
    public static string CounterReport()
    {
      var sb = new StringBuilder();
      lock (_gate)
      {
        if (_logs.Count == 0) return "No reply stream has been read in this capture window.";
        foreach (var kv in _logs)
        {
          var log = kv.Value;
          sb.AppendLine(kv.Key + ": " + log.Streams + " stream(s), " + log.DataPayloads + " data payload(s), " +
                        log.Unparsed + " failed to parse");
          foreach (var s in log.Samples) sb.AppendLine("    unparsed: " + s);
        }
      }
      return sb.ToString();
    }

    /// <summary>
    /// Write the streams recorded in the open window to <paramref name="dir"/>, one file each, EXACTLY as read -
    /// one line per line, blank lines included - then CLOSE the window and forget them. Returns what happened.
    ///
    /// <para>REFUSES, writing nothing, when no window is open - so the capture can never be written by
    /// accident - and when <paramref name="activeDocumentKey"/> is not the document the window was opened on.
    /// That second check catches a capture written from a different document than the one prepared for it.
    /// <b>It does not catch a switch to another document and BACK inside one window:</b> streams are not
    /// stamped with their document, because a reply is read on a worker thread that does not know which
    /// document asked for it. The window being short and deliberate is what covers that case, and the menu
    /// and the report both say so rather than implying more.</para>
    /// </summary>
    public static string WriteAll(string dir, string activeDocumentKey)
    {
      var sb = new StringBuilder();
      List<Recording> all;
      lock (_gate)
      {
        if (!_open)
          return "REFUSED - no capture window is open, so nothing was written. Use \"Start stream capture\" " +
                 "first: it clears everything recorded before it, so only what you run afterwards can be written.";
        if (!string.Equals(_windowDocument, activeDocumentKey ?? "", StringComparison.Ordinal))
          return "REFUSED - the capture window was opened on a different document from the active one, so " +
                 "nothing was written. Switch back to the scratch document, or start a new window on this one.";

        all = new List<Recording>();
        foreach (var kv in _logs) all.AddRange(kv.Value.Recent);
        // One window, one write: closing here means these streams can never be written a second time, and a
        // stream still finishing after this point is dropped rather than kept for a window it never joined.
        _open = false;
        _windowDocument = null;
        _logs.Clear();
      }
      if (all.Count == 0) return "The window was open but no reply was read inside it - nothing to write. It is now closed.";

      Directory.CreateDirectory(dir);
      var n = 0;
      foreach (var r in all)
      {
        n++;
        var name = r.Provider + "-" + r.StartedUtc.ToString("yyyyMMdd'T'HHmmss'Z'") + "-" + n.ToString("00") + ".sse";
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, r.Text.ToString(), new UTF8Encoding(false));
        sb.AppendLine("  " + path + "  (" + r.Text.Length + " chars" + (r.Truncated ? ", TRUNCATED at the cap" : "") +
                      (r.Unparsed > 0 ? ", " + r.Unparsed + " event(s) failed to parse" : "") + ")");
      }
      return sb.ToString();
    }

    /// <summary>For tests: forget everything recorded this session and close any window.</summary>
    internal static void Reset()
    {
      lock (_gate) { _logs.Clear(); _open = false; _windowDocument = null; }
    }
  }
}
