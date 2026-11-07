using System;
using System.Collections.Generic;

namespace Dwight.Session
{
  /// <summary>One recorded turn. Before/after GhJSON snapshots are kept for the later
  /// history / time-travel feature (ADR-013) - recording is cheap now, retrofitting is not.</summary>
  internal sealed class TranscriptEntry
  {
    public DateTime TimeUtc { get; set; }
    public string Action { get; set; }       // "Explain" | "Reorganise"
    public string Response { get; set; }      // the model's answer (text), if any
    public string BeforeGhJson { get; set; }  // selection at the time of the request
    public string AfterGhJson { get; set; }   // model's edited GhJSON, for writes
  }

  /// <summary>In-memory session transcript (mother doc 4.2: only the session lives in memory).</summary>
  internal static class SessionTranscript
  {
    // WP-4.7: Record is called from background LLM tasks, so the list is lock-guarded; and it is capped so a
    // long working session cannot grow it without bound (only the most recent turns are kept).
    private const int Cap = 500;
    private static readonly object _gate = new object();
    private static readonly List<TranscriptEntry> _entries = new List<TranscriptEntry>();

    public static IReadOnlyList<TranscriptEntry> Entries { get { lock (_gate) return _entries.ToArray(); } }

    public static void Record(string action, string response, string beforeGhJson, string afterGhJson = null)
    {
      lock (_gate)
      {
        _entries.Add(new TranscriptEntry
        {
          TimeUtc = DateTime.UtcNow,
          Action = action,
          Response = response,
          BeforeGhJson = beforeGhJson,
          AfterGhJson = afterGhJson,
        });
        if (_entries.Count > Cap) _entries.RemoveRange(0, _entries.Count - Cap);
      }
    }
  }
}
