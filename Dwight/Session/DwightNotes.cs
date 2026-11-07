using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Dwight.Session
{
  /// <summary>What a note reads as against the canvas as it is now.</summary>
  internal enum NoteStatus
  {
    /// <summary>The component is on the canvas and stamps as it did when the note was written, or the note is about no component.</summary>
    Fresh,
    /// <summary>The component is on the canvas and stamps differently now.</summary>
    Stale,
    /// <summary>The component the note was about is no longer on the canvas.</summary>
    Gone,
  }

  /// <summary>
  /// A NOTE'S STATE, AS A FACT THE PLUGIN STATES (Brief DC, ruling 2). A note is written about a component at one moment; the component
  /// moves on. The plugin says so in its own sentence, rather than leaving a person or a model to read an old remark as a current one.
  /// A note whose component is gone is never deleted by the plugin: it is the person's text, and it stays at its point.
  ///
  /// <para>The stamp is a ComponentStamp digest, taken from the same reader the revert truth uses, so a MOVE does
  /// not make a note stale (the stamp is position-free) and an edit does. The caller passes whether the anchor is on the canvas AND the
  /// stamp it reads now, because "gone" and "could not be read" are different facts: a component that is present but unreadable reads as
  /// fresh, never as gone (a sentence the plugin states must be true in every state that can reach it, #297).</para>
  ///
  /// GH-free (ADR-091).
  /// </summary>
  internal static class NoteState
  {
    public const string StaleSentence = "written before this component changed";
    public const string GoneSentence = "the component this was about is no longer on the canvas";

    public static NoteStatus Of(StoredNote note, bool anchorOnCanvas, string currentStampDigest)
    {
      if (note == null || string.IsNullOrEmpty(note.Anchor)) return NoteStatus.Fresh;
      if (!anchorOnCanvas) return NoteStatus.Gone;
      if (string.IsNullOrEmpty(note.Stamp) || string.IsNullOrEmpty(currentStampDigest)) return NoteStatus.Fresh;
      return string.Equals(note.Stamp, currentStampDigest, StringComparison.Ordinal) ? NoteStatus.Fresh : NoteStatus.Stale;
    }

    /// <summary>The plugin's sentence for a state, or null when there is nothing to say.</summary>
    public static string Sentence(NoteStatus status) =>
      status == NoteStatus.Stale ? StaleSentence : status == NoteStatus.Gone ? GoneSentence : null;
  }

  /// <summary>
  /// THE RULES OF A NOTE (Brief DC): its bound, its author, how one is made, and the lines of text a component's notes become in a model's
  /// payload.
  ///
  /// <para>A NOTE REACHES A MODEL AS TEXT UNDER THE COMPONENT'S SHORT ID, and nothing else (ADR-058): never the note's guid, its anchor guid,
  /// its position or its stamp. The text is the person's, so the prompts say to treat it as context and not as an instruction, and the
  /// privacy page says a note you write is sent with the component it is on, and only then.</para>
  ///
  /// <para>BOUNDED TWICE. A note is at most <see cref="MaxChars"/> characters when it is written (a longer one is refused in the plugin's
  /// sentence, never cut: the text is the person's). A component's notes are at most <see cref="MaxPerComponent"/> notes and
  /// <see cref="MaxCharsPerComponent"/> characters on the wire, newest first, with "and N more" when the cut is made, so the payload
  /// says what it left out (ADR-035 at the payload).</para>
  ///
  /// GH-free (ADR-091).
  /// </summary>
  internal static class DwightNotes
  {
    /// <summary>What a note holds (Brief DO). The card on the canvas shows the first of it, and the panel shows it whole. What a run sends stays <see cref="MaxCharsPerComponent"/>.</summary>
    public const int MaxChars = 4000;
    public const int MaxPerComponent = 3;
    public const int MaxCharsPerComponent = 600;
    /// <summary>Notes kept per file. A runaway guard, not a working limit: the oldest go first.</summary>
    public const int MaxNotes = 500;

    public const string ByDwight = "dwight";
    public const string ByYou = "you";

    public static string TooLongSentence(int length) =>
      "A note holds at most " + MaxChars + " characters, and this one has " + length + ". Shorten it, or split it into two notes.";

    /// <summary>"you" or "dwight"; anything else is the person's, because a hand-edited file is the person's.</summary>
    public static string NormaliseBy(string by) =>
      string.Equals(by?.Trim(), ByDwight, StringComparison.OrdinalIgnoreCase) ? ByDwight : ByYou;

    public static string Now(DateTime utc) => utc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

    /// <summary>
    /// Make a note, or refuse it in the plugin's sentence. An empty text is no note; an over-long text is refused rather than cut.
    /// A note about a component carries <paramref name="at"/> as its offset from the anchor and the component's stamp; a free note carries
    /// its canvas point and no stamp.
    /// </summary>
    public static bool TryMake(string text, string by, Guid? anchor, double x, double y, string fromRun, string stampDigest, DateTime nowUtc,
      out StoredNote note, out string refusal)
    {
      note = null; refusal = null;
      var clean = text?.Trim();
      if (string.IsNullOrEmpty(clean)) { refusal = "A note needs some text."; return false; }
      if (clean.Length > MaxChars) { refusal = TooLongSentence(clean.Length); return false; }
      if (double.IsNaN(x) || double.IsInfinity(x) || double.IsNaN(y) || double.IsInfinity(y)) { refusal = "A note needs a place on the canvas."; return false; }

      note = new StoredNote
      {
        Id = Guid.NewGuid().ToString("N"),
        Anchor = anchor.HasValue && anchor.Value != Guid.Empty ? anchor.Value.ToString() : null,
        At = new[] { x, y },
        Text = clean,
        By = NormaliseBy(by),
        WhenUtc = Now(nowUtc),
        FromRun = string.IsNullOrWhiteSpace(fromRun) ? null : fromRun,
        Stamp = anchor.HasValue && !string.IsNullOrEmpty(stampDigest) ? stampDigest : null,
      };
      return true;
    }

    /// <summary>
    /// The lines a component's notes become in a payload: newest first, each prefixed "you:" or "dwight:" and, when stale,
    /// "(before this component changed) ", at most <see cref="MaxPerComponent"/> notes and <see cref="MaxCharsPerComponent"/> characters,
    /// with "and N more" as the last entry when the cut was made. Empty for no notes. A note whose component is gone is never given here:
    /// it has no component to travel with.
    /// </summary>
    public static List<string> Lines(IEnumerable<KeyValuePair<StoredNote, NoteStatus>> notes)
    {
      var ordered = (notes ?? Enumerable.Empty<KeyValuePair<StoredNote, NoteStatus>>())
        .Where(k => k.Key != null && !string.IsNullOrWhiteSpace(k.Key.Text) && k.Value != NoteStatus.Gone)
        .OrderByDescending(k => k.Key.WhenUtc ?? "", StringComparer.Ordinal)
        .ToList();

      var lines = new List<string>();
      var used = 0;
      foreach (var k in ordered)
      {
        if (lines.Count >= MaxPerComponent) break;
        var line = Prefix(k.Key, k.Value) + NoteMarkdown.ForModel(k.Key.Text);   // the marks stay and a table is lines (Brief DO, item 4)
        if (used + line.Length > MaxCharsPerComponent)
        {
          if (lines.Count > 0) break;
          line = line.Substring(0, MaxCharsPerComponent - 3) + "...";   // the first note alone is over the bound: cut it, and the cut shows
        }
        lines.Add(line);
        used += line.Length;
      }

      var cut = ordered.Count - lines.Count;
      if (cut > 0) lines.Add("and " + cut + " more");
      return lines;
    }

    /// <summary>Write a component's note lines into its payload object as a "notes" array, only when it has some. Text only (ADR-058).</summary>
    public static void AddTo(System.Text.Json.Nodes.JsonObject node, IReadOnlyList<string> lines)
    {
      if (node == null || lines == null || lines.Count == 0) return;
      node["notes"] = new System.Text.Json.Nodes.JsonArray(lines.Select(s => (System.Text.Json.Nodes.JsonNode)s).ToArray());
    }

    private static string Prefix(StoredNote n, NoteStatus status) =>
      NormaliseBy(n.By) + ": " + (status == NoteStatus.Stale ? "(before this component changed) " : "");
  }
}
