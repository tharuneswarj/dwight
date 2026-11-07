using System;
using System.Text.Json.Nodes;

namespace Dwight.Llm
{
  /// <summary>
  /// A DEBUG-only record of the last request Dwight actually put on the wire, and what came back that
  /// matters for the ADR-082 thinking promise (Phase-8 WP-2 Stage C, #199).
  ///
  /// WHY A CAPTURE AND NOT JUST TESTS. The headless tests prove the body Dwight CONSTRUCTS is correct.
  /// They cannot prove what leaves the machine, nor that the model did what was asked. The promise being
  /// verified - thinking on only for agentic work, off for all comprehension - fails SILENTLY and
  /// EXPENSIVELY if it regresses: thinking is billed as output tokens, so a comprehension call that
  /// quietly thinks costs the user real money with nothing on screen to show for it. That is a claim
  /// worth reading off the wire rather than inferring.
  ///
  /// SAFETY. DEBUG-only - the whole capture compiles out of a Release build, so there is no shipping
  /// surface. The API key never appears: it rides an HTTP header, never the body, and the body is all
  /// that is kept. What IS kept is prompt content, which is the same data already being sent to the
  /// provider - so this reveals nothing to the machine's owner that their own request did not.
  ///
  /// One request deep, in memory, never written to disk (the store holds the user's work; a debug trace
  /// is not that).
  /// </summary>
  internal static class LlmTrace
  {
#if DEBUG
    private static readonly object _gate = new object();
    private static string _lastBody;
    private static string _lastModel;
    private static LlmUsage _lastUsage;
    private static string _lastRawUsage;
    private static int _lastThinkingBlocks;
    private static string _lastStopReason;

    public static void RecordRequest(string modelId, string body)
    {
      lock (_gate) { _lastModel = modelId; _lastBody = body; _lastUsage = null; _lastRawUsage = null; _lastThinkingBlocks = 0; _lastStopReason = null; }
    }

    /// <summary>The usage object EXACTLY as the API sent it, before Dwight reads anything out of it.
    /// A parsed zero and an unreported field are indistinguishable downstream, and every thinking
    /// measurement so far has been a zero - so the parse cannot be the only witness to its own
    /// correctness (#137: read the authoritative structure, never a derived echo of it).</summary>
    public static void RecordRawUsage(string usageJson)
    {
      lock (_gate) { if (!string.IsNullOrEmpty(usageJson)) _lastRawUsage = usageJson; }
    }

    /// <summary>How many thinking blocks came back in the content. A SECOND, independent witness: it
    /// comes from the content stream rather than the usage object, so the two cannot fail the same way.</summary>
    /// <summary>
    /// D2: the first non-ASCII text the provider DECODED this session, with its code points.
    ///
    /// Every other stage is now eliminated - the panel renders a known string correctly (the probe), the
    /// JS marshalling escapes non-ASCII and so cannot mangle, the store round-trips, and all three
    /// adapters read their stream as explicit UTF-8. So the one thing nobody has actually LOOKED at is
    /// what the decoder produced. U+00D7 here means the provider is clean and the corruption is
    /// downstream; U+00C3 U+2014 means the decoded text is ALREADY mojibake, and the bytes that produced
    /// it were double-encoded rather than mis-decoded - two different fixes, and no way to tell them
    /// apart by reasoning.
    /// </summary>
    public static string LastNonAscii;

    public static void RecordDecodedText(string text)
    {
      if (LastNonAscii != null || string.IsNullOrEmpty(text)) return;
      var hasNonAscii = false;
      foreach (var c in text) if (c > 127) { hasNonAscii = true; break; }
      if (!hasNonAscii) return;

      var sample = text.Length > 160 ? text.Substring(0, 160) : text;
      var points = new System.Text.StringBuilder();
      foreach (var c in sample)
        if (c > 127) points.Append("U+").Append(((int)c).ToString("X4")).Append(' ');
      LastNonAscii = sample + "\n     code points: " + points.ToString().Trim();
    }

    public static void RecordResponse(LlmUsage usage, int thinkingBlocks = 0, string stopReason = null)
    {
      lock (_gate) { _lastUsage = usage; _lastThinkingBlocks = thinkingBlocks; _lastStopReason = stopReason; }
    }
#else
    public static void RecordRequest(string modelId, string body) { }
    public static void RecordRawUsage(string usageJson) { }
    public static void RecordResponse(LlmUsage usage, int thinkingBlocks = 0, string stopReason = null) { }
    public static void RecordDecodedText(string text) { }
    public static string LastNonAscii => null;
#endif

#if DEBUG
    /// <summary>Brief EX: the Walk's reply as the model wrote it, before the plugin marks its neighbours, so a Debug gate can read the boundary words in the model's own form. In memory, one reply deep.</summary>
    public static string LastWalkUnmarked;
#endif

    /// <summary>Brief EX: keep the Walk's reply before it is marked. The call itself is compiled out of a Release build.</summary>
    [System.Diagnostics.Conditional("DEBUG")]
    public static void RecordWalkUnmarked(string rawJson)
    {
#if DEBUG
      LastWalkUnmarked = rawJson;
#endif
    }

    /// <summary>One exchange's thinking readings, for a caller that needs to COMPARE two calls rather
    /// than read one. Null in Release, and null before any request.</summary>
    internal sealed class ThinkingReading
    {
      public string Sent;        // what the request's thinking field said
      public string Effort;
      public int Tokens;         // witness 1: the usage object
      public int Blocks;         // witness 2: the content stream
      public string RawUsage;    // ground truth, unparsed
      public string StopReason;  // "end_turn" finished; "max_tokens" was CUT OFF - not the same result
      public bool Thought => Tokens > 0 || Blocks > 0;

      /// <summary>Did the budget run out? Thinking is billed inside <c>max_tokens</c>, so a thinking
      /// pass can consume the whole allowance and leave nothing for the answer. "It thought" and "it
      /// thought and then had nothing left to say" are different outcomes and must not read alike.</summary>
      public bool Truncated => string.Equals(StopReason, "max_tokens", StringComparison.Ordinal);

      /// <summary>Did the API AFFIRMATIVELY report a thinking count, whatever its value? This is the
      /// question that separates "the model did not think" from "we never read the number", and it can
      /// only be answered from the raw payload - a parsed zero looks identical either way. A witness
      /// that is captured and then not consulted is not a witness.</summary>
      public bool ReportedThinkingField =>
        RawUsage != null && (RawUsage.IndexOf("thinking_tokens", StringComparison.Ordinal) >= 0 ||
                             RawUsage.IndexOf("reasoning_tokens", StringComparison.Ordinal) >= 0);   // OpenAI's Responses endpoint
    }

    /// <summary>
    /// Snapshot the current readings. A probe that makes two calls must capture the first before the
    /// second overwrites it - the trace is one exchange deep by design, and comparing A with B is the
    /// only way a null gets a positive control (#212).
    /// </summary>
    public static ThinkingReading LastThinking()
    {
#if DEBUG
      string body, raw, stop; LlmUsage usage; int blocks;
      lock (_gate) { body = _lastBody; usage = _lastUsage; raw = _lastRawUsage; blocks = _lastThinkingBlocks; stop = _lastStopReason; }
      if (string.IsNullOrEmpty(body)) return null;

      var reading = new ThinkingReading { Sent = "omitted", Tokens = usage?.ThinkingTokens ?? 0, Blocks = blocks, RawUsage = raw, StopReason = stop };
      try
      {
        var root = JsonNode.Parse(body);
        reading.Sent = root?["thinking"]?["type"]?.GetValue<string>() ?? (root?["reasoning"] != null ? "reasoning" : "omitted");
        reading.Effort = root?["output_config"]?["effort"]?.GetValue<string>() ?? root?["reasoning"]?["effort"]?.GetValue<string>();
      }
      catch { }
      return reading;
#else
      return null;
#endif
    }

    /// <summary>
    /// The gate's read (#193e / #199): what was asked for, and what the model actually did. Both
    /// directions in one place, because either alone proves the wrong thing - the request shows only
    /// what Dwight asked, the token count shows only that something thought.
    /// </summary>
    public static string LastExchangeReport()
    {
#if DEBUG
      string body, model, rawUsage, stop; LlmUsage usage; int blocks;
      lock (_gate) { body = _lastBody; model = _lastModel; usage = _lastUsage; rawUsage = _lastRawUsage; blocks = _lastThinkingBlocks; stop = _lastStopReason; }

      if (string.IsNullOrEmpty(body)) return "No LLM request has been made in this session.";

      var thinkingSent = "OMITTED (no thinking field)";
      try
      {
        var sent = JsonNode.Parse(body);
        var t = sent?["thinking"]?["type"]?.GetValue<string>() ?? (sent?["reasoning"] != null ? "reasoning" : null);
        if (!string.IsNullOrEmpty(t)) thinkingSent = t;
      }
      catch { thinkingSent = "(could not parse the body)"; }

      string effort = null;
      try { var sent = JsonNode.Parse(body); effort = sent?["output_config"]?["effort"]?.GetValue<string>() ?? sent?["reasoning"]?["effort"]?.GetValue<string>(); } catch { }

      var lines = new System.Text.StringBuilder();
      lines.AppendLine("model:            " + model);
      lines.AppendLine("thinking SENT:    " + thinkingSent + (effort != null ? "  (effort " + effort + ")" : ""));
      lines.AppendLine("thinking TOKENS:  " + (usage == null ? "(no response yet)" : usage.ThinkingTokens.ToString()) + "   [read from usage]");
      lines.AppendLine("thinking BLOCKS:  " + blocks + "   [read from the content stream - an INDEPENDENT witness]");
      if (usage != null)
        lines.AppendLine("output tokens:    " + usage.OutputTokens + "  (thinking is part of this, not extra)");
      lines.AppendLine("stop reason:      " + (stop ?? "(none)") +
        (string.Equals(stop, "max_tokens", StringComparison.Ordinal)
          ? "   <- CUT OFF: the budget ran out, so this answer is incomplete" : ""));
      lines.AppendLine("raw usage:        " + (rawUsage ?? "(none captured)"));
      lines.AppendLine();
      // The two witnesses exist because a parsed zero and an unreported field look identical, and every
      // thinking measurement so far has been a zero. Blocks come from the content stream, tokens from
      // the usage object: if both read zero AND the raw usage carries no thinking field, the model
      // genuinely did not think. If they disagree, the reader is the defect, not the model.
      lines.AppendLine("If TOKENS and BLOCKS are both 0 and the raw usage shows no thinking field, the model");
      lines.AppendLine("chose not to think. If they disagree, the READER is wrong - not the model.");
      // The two lines do NOT carry equal weight, and saying they did was an overclaim.
      //
      // The REQUEST is what Dwight controls and is the thing under test: 'adaptive' + an effort on
      // agentic work, 'disabled' or omitted on comprehension. That is a hard pass/fail.
      //
      // The TOKEN COUNT is the model's decision. "Adaptive" means exactly that the model chooses whether
      // to think, so zero on an easy prompt is adaptive working, not the wire failing - it is evidence
      // the wire is live end-to-end when it is positive, and not a verdict when it is zero. The
      // comprehension direction is the strict one: 'disabled' with anything above zero is a real failure,
      // because disabled admits no discretion.
      lines.AppendLine("REQUEST (the wire, pass/fail): 'adaptive' + effort on agentic work;");
      lines.AppendLine("                               'disabled' or omitted on comprehension.");
      lines.AppendLine("TOKENS  (the model's choice):  > 0 is end-to-end evidence the wire is live.");
      lines.AppendLine("                               0 under 'adaptive' means it saw no need to think;");
      lines.AppendLine("                               anything above 0 under 'disabled' IS a failure.");
      lines.AppendLine();
      lines.AppendLine("--- exact request body ---");
      lines.AppendLine(body);
      return lines.ToString();
#else
      return "The request trace is a DEBUG-only diagnostic and is not compiled into this build.";
#endif
    }
  }
}
