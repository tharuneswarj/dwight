using System;

namespace Dwight.Llm
{
  /// <summary>What, if anything, goes on the wire in the request's <c>thinking</c> field.</summary>
  internal enum ThinkingWireState
  {
    /// <summary>Send no thinking field at all.</summary>
    Omit = 0,
    /// <summary>Send <c>{"type":"adaptive"}</c> (plus an effort, when one is set).</summary>
    Adaptive = 1,
    /// <summary>Send <c>{"type":"disabled"}</c> - required on families where OMITTING would leave
    /// thinking running.</summary>
    Disabled = 2,
    /// <summary>Brief BZ: thinking must be off and this model cannot turn it off: send no field and the lowest sensible effort, with room for the thinking
    /// (Opus 5.5 and Fable 5.1; <c>disabled</c> is a 400 on both at every effort).</summary>
    AlwaysOn = 3,
    /// <summary>Brief BZ: thinking must be off, and the model's way to say so is <c>{"type":"between_tools"}</c> with no other field, at effort high or below
    /// (Sonnet 5.5; <c>disabled</c> is a 400 there).</summary>
    BetweenTools = 4,
  }

  /// <summary>
  /// THE THREE-STATE THINKING WIRE (Phase-8 WP-2, ruling #192). ADR-082 promises thinking is ON only
  /// for agentic Top-tier work and OFF for all comprehension. Keeping that promise is NOT one flag,
  /// because the API's default is not uniform across model families:
  ///
  ///   - on the Claude 5 family, OMITTING the field runs thinking, so "off" must be said EXPLICITLY;
  ///   - on Sonnet 4.6 / Opus 4.8, omitting means off, so "off" is silence;
  ///   - on everything else Dwight ships, there is no adaptive mode to ask for at all.
  ///
  /// A single "send it or don't" boolean would therefore have honoured ADR-082 on one family and
  /// silently broken it on another - comprehension quietly thinking, and being billed for it.
  ///
  /// The state is resolved from CATALOGUE DATA (the <c>thinkingDefault</c> fact, #188), never from
  /// matching model ids: ids churn, and a hardcoded id map is exactly the pattern ADR-092 rejected for
  /// API grounding - stale the day a model ships, and duplicating what the catalogue already states.
  ///
  /// GH-free and headless-tested (ADR-091), because this decides what every shipped verb puts on the
  /// wire.
  /// </summary>
  internal static class ThinkingWire
  {
    /// <summary>
    /// Resolve the wire state. <paramref name="taskWantsThinking"/> is ADR-082's per-task decision;
    /// the other two are catalogue facts about the model.
    /// </summary>
    public static ThinkingWireState Resolve(bool taskWantsThinking, bool thinkingOnByDefault, bool supportsAdaptive)
    {
      if (taskWantsThinking)
      {
        // Only ask for thinking where the model actually offers the mode we speak. Asking a model that
        // does not is a 400, and silently degrading to "no thinking" is the honest alternative - the
        // task still runs, it simply does not think.
        return supportsAdaptive ? ThinkingWireState.Adaptive : ThinkingWireState.Omit;
      }

      // Thinking must be OFF. On a default-on family that requires saying so; anywhere else, silence
      // already means off and adding a field would only churn the prompt cache.
      return thinkingOnByDefault ? ThinkingWireState.Disabled : ThinkingWireState.Omit;
    }

    /// <summary>
    /// Brief BZ: the wire state from the row's own <c>thinkingDefault</c>. The three-fact overload above answers for the families it was written for, and "off" is not one
    /// shape any more: <c>adaptive-always</c> cannot be turned off at all, and <c>adaptive-between-tools</c> says it differently. Never id matching (ADR-082).
    /// </summary>
    public static ThinkingWireState Resolve(bool taskWantsThinking, string thinkingDefault)
    {
      if (!taskWantsThinking)
      {
        if (string.Equals(thinkingDefault, AdaptiveAlways, StringComparison.OrdinalIgnoreCase)) return ThinkingWireState.AlwaysOn;
        if (string.Equals(thinkingDefault, AdaptiveBetweenTools, StringComparison.OrdinalIgnoreCase)) return ThinkingWireState.BetweenTools;
      }
      return Resolve(taskWantsThinking, IsOnByDefault(thinkingDefault), SupportsAdaptive(thinkingDefault));
    }

    /// <summary>Omitting runs thinking and nothing turns it off (Opus 5.5, Fable 5.1).</summary>
    public const string AdaptiveAlways = "adaptive-always";
    /// <summary>Omitting runs thinking, and off is <c>between_tools</c> (Sonnet 5.5).</summary>
    public const string AdaptiveBetweenTools = "adaptive-between-tools";

    /// <summary>Does this row reject <c>{"type":"disabled"}</c>? Such a row has no thinking-free chat to pin, so a pinned chat on it thinks at the profile's effort
    /// rather than sitting at the lowest setting a verb would use (<see cref="Dwight.Agent.ChatHeader.PinThinks"/>).</summary>
    public static bool CannotDisable(string thinkingDefault) =>
      string.Equals(thinkingDefault, AdaptiveAlways, StringComparison.OrdinalIgnoreCase) ||
      string.Equals(thinkingDefault, AdaptiveBetweenTools, StringComparison.OrdinalIgnoreCase);

    /// <summary>Does OMITTING the thinking field leave thinking running on this model? The one case
    /// that needs an explicit disable to honour ADR-082. Unrecognised values read as false - silence
    /// must mean the SAFE state, never a guess.</summary>
    public static bool IsOnByDefault(string thinkingDefault) =>
      string.Equals(thinkingDefault, "adaptive-on", StringComparison.OrdinalIgnoreCase) || CannotDisable(thinkingDefault);

    /// <summary>Can Dwight turn adaptive thinking ON for this model? Unrecognised values read as false,
    /// so an unknown catalogue entry is never sent a thinking field it might reject.</summary>
    public static bool SupportsAdaptive(string thinkingDefault) =>
      IsOnByDefault(thinkingDefault) ||
      string.Equals(thinkingDefault, "adaptive-off", StringComparison.OrdinalIgnoreCase);

    /// <summary>The effort level for a profile (ADR-082: "Ultra = thinking maxed where the task
    /// allows"). Only meaningful alongside <see cref="ThinkingWireState.Adaptive"/>; Economy never
    /// reaches here because the resolver turns thinking off for it entirely.</summary>
    public static string EffortForProfile(string profile)
    {
      switch ((profile ?? "").Trim().ToLowerInvariant())
      {
        case "ultra": return "max";
        case "quality": return "high";
        case "balanced": return "medium";
        default: return null;   // unknown or Economy: send no effort, take the API default
      }
    }
  }
}
