using System;

namespace Dwight.Llm
{
  /// <summary>
  /// THE ANSWER BUDGET AND THE THINKING RESERVE (ruling #216, from the WP-2 Stage-D measurement).
  ///
  /// WHAT WENT WRONG. Thinking is billed INSIDE <c>max_tokens</c>, and nothing coupled the two: effort
  /// comes from the user's profile, <c>max_tokens</c> from each call site, set independently and with no
  /// awareness of each other. Measured on the wire: at effort <c>max</c> with a 4096 budget, the model
  /// spent thinking_tokens 4096 = output_tokens 4096 and returned ZERO characters of answer. A call that
  /// thought until there was nothing left to say, and was paid for in full.
  ///
  /// THE RULE. <c>max_tokens</c> is the ANSWER budget. When the wire state is Adaptive the provider seam
  /// adds a reserve ON TOP - they never share. If the sum exceeds what the model will accept, the EFFORT
  /// steps down, never the answer: the answer is the deliverable, and quietly shrinking it to make room
  /// for reasoning inverts what the caller asked for. Every step-down is recorded, because a silent
  /// degrade is how "we asked for max" becomes folklore.
  ///
  /// WHERE. One place - the provider seam - rather than every call site (#216). A call site that forgets
  /// is the whole failure mode; there is nothing to forget if it is not their job.
  ///
  /// THE NUMBERS are config constants, tunable, with their basis stated rather than guessed silently.
  /// They are deliberately generous: an unused reserve costs NOTHING (only tokens actually produced are
  /// billed), while too small a reserve costs a truncated answer that was paid for. The asymmetry is the
  /// whole argument for erring high.
  ///
  /// GH-free and headless-tested (ADR-091) - this decides what every thinking-enabled call may spend.
  /// </summary>
  internal static class ThinkingBudget
  {
    /// <summary>
    /// The default ANSWER budget - ADR-030's pinned output ceiling, defined HERE and referenced by
    /// <c>DwightConfig.MaxTokens</c> and <c>LlmRequest.MaxTokens</c> alike.
    ///
    /// It lives in this file rather than in DwightConfig because <c>LlmRequest</c> is link-compiled into
    /// the headless tests (ADR-091) and cannot reach the config. Two definitions was exactly the drift
    /// this ruling caught: the request default sat at 4096 while the config said 16000, so any call site
    /// that forgot to pass one silently took the smaller - a defect with no symptom until an answer came
    /// back short.
    /// </summary>
    public const int DefaultAnswerBudget = 16_000;

    // Reserves per effort level. The measured anchor is the only hard datum: effort "max" consumed
    // 4096 thinking tokens and was still cut off mid-thought, so max's true appetite is ABOVE 4096 and
    // unmeasured. These scale from that floor rather than from a guess about the ceiling.
    public const int ReserveLow = 4_000;
    public const int ReserveMedium = 8_000;
    public const int ReserveHigh = 16_000;
    public const int ReserveMax = 32_000;

    /// <summary>
    /// The largest <c>max_tokens</c> a request may carry. A CONSERVATIVE FLOOR across the shipped
    /// catalogue, not a per-model fact - the catalogue records contextWindow (input + output together)
    /// and has no per-model output limit to read. Stated as the approximation it is; the per-model
    /// number belongs in the catalogue if a model ever needs a different one, and that is a recorded
    /// deferred unit rather than a silent assumption.
    /// </summary>
    public const int Ceiling = 48_000;

    /// <summary>Effort levels, strongest first - the ladder a step-down walks down.</summary>
    private static readonly string[] Ladder = { "max", "high", "medium", "low" };

    internal sealed class Plan
    {
      /// <summary>What goes on the wire: the answer budget PLUS the reserve.</summary>
      public int MaxTokens;
      /// <summary>The effort actually sent - stepped down from the request's if it would not fit.</summary>
      public string Effort;
      /// <summary>The caller's untouched answer budget, so the reserve is auditable.</summary>
      public int AnswerBudget;
      public int Reserve;
      public bool SteppedDown;
      /// <summary>Non-null whenever something was changed from what was asked for. Never silent.</summary>
      public string Note;
    }

    public static int ReserveFor(string effort)
    {
      switch ((effort ?? "").Trim().ToLowerInvariant())
      {
        case "max": return ReserveMax;
        case "high": return ReserveHigh;
        case "medium": return ReserveMedium;
        case "low": return ReserveLow;
        default: return ReserveMedium;   // adaptive with no effort set takes the API default: reserve for it
      }
    }

    /// <summary>
    /// Plan a thinking-enabled request. <paramref name="answerBudget"/> is the caller's max_tokens and is
    /// never reduced.
    /// </summary>
    public static Plan Resolve(int answerBudget, string effort, int ceiling = Ceiling)
    {
      var plan = new Plan { AnswerBudget = answerBudget, Effort = effort };

      var start = Array.FindIndex(Ladder, e => string.Equals(e, effort, StringComparison.OrdinalIgnoreCase));
      if (start < 0)
      {
        // No effort named: adaptive takes the API default, so reserve for it but never "step down" a
        // level that was never requested.
        plan.Reserve = ReserveFor(null);
        plan.MaxTokens = answerBudget + plan.Reserve;
        if (plan.MaxTokens > ceiling)
        {
          plan.MaxTokens = ceiling;
          plan.Reserve = Math.Max(0, ceiling - answerBudget);
          plan.Note = "The answer budget leaves under " + ReserveFor(null) + " tokens of headroom below the " +
                      ceiling + "-token ceiling, so thinking may be cut short. Lower the answer budget or the effort.";
        }
        return plan;
      }

      for (var i = start; i < Ladder.Length; i++)
      {
        var reserve = ReserveFor(Ladder[i]);
        if (answerBudget + reserve > ceiling) continue;

        plan.Effort = Ladder[i];
        plan.Reserve = reserve;
        plan.MaxTokens = answerBudget + reserve;
        plan.SteppedDown = i > start;
        if (plan.SteppedDown)
          plan.Note = "Effort stepped down from \"" + Ladder[start] + "\" to \"" + Ladder[i] + "\": the " +
                      answerBudget + "-token answer budget plus a " + Ladder[start] + " reserve exceeds the " +
                      ceiling + "-token ceiling. The answer budget was kept whole.";
        return plan;
      }

      // Not even the smallest reserve fits, which means the ANSWER budget alone is at the ceiling. The
      // answer is never shrunk to make room, so the reserve is what gives - said out loud, because this
      // is the one case where the guarantee cannot be kept.
      plan.Effort = Ladder[Ladder.Length - 1];
      plan.Reserve = Math.Max(0, ceiling - answerBudget);
      plan.MaxTokens = Math.Max(answerBudget, ceiling);
      plan.SteppedDown = true;
      plan.Note = "The answer budget of " + answerBudget + " tokens leaves no room for a thinking reserve " +
                  "below the " + ceiling + "-token ceiling. Effort was dropped to \"" + plan.Effort +
                  "\" and the answer budget kept whole; thinking may still be cut short.";
      return plan;
    }
  }
}
