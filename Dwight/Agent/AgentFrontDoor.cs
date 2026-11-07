using System;

namespace Dwight.Agent
{
  /// <summary>Which front-door refusal a Scaffold turn got, if any.</summary>
  internal enum FrontDoorRefusal
  {
    /// <summary>The turn may start.</summary>
    None = 0,

    /// <summary>The model, or the connection, cannot drive the agent. <see cref="FrontDoorResult.Text"/> says why.</summary>
    NotEligible,

    /// <summary>No key is stored for the provider. The panel has its own notice for this.</summary>
    NoKey,
  }

  internal sealed class FrontDoorResult
  {
    public FrontDoorRefusal Refusal { get; set; }

    /// <summary>Plain text for <see cref="FrontDoorRefusal.NotEligible"/> - escaped at the destination (#335).</summary>
    public string Text { get; set; }
  }

  /// <summary>
  /// THE SCAFFOLD TURN'S FRONT DOOR: whether a turn may start, decided BEFORE anything is claimed or spent -
  /// and in a fixed ORDER, which is the thing this type exists to hold.
  ///
  /// THE ORDER IS ELIGIBILITY, THEN THE KEY (#885). On a provider that cannot carry tools and has no key, the user
  /// must hear "the agent cannot run here" - not "add a key", followed by a refusal once they have added one. The
  /// model is resolved without a key, so checking eligibility first costs nothing. The reshuffle that put a
  /// resolve above the key check (#37) is exactly the kind of edit that loses this silently, so it lives here,
  /// GH-free, where a test fails if the order flips (#892: a property true only by construction is the one a
  /// later helpful change breaks).
  ///
  /// THE DECISION ITSELF IS LANE B'S (<c>LlmProviders.AgentRefusal</c>), passed in rather than called, so this
  /// holds the SEQUENCE without a second copy of the rule - and so the test can drive both halves.
  ///
  /// Used by the panel's RunTurn and by the DEBUG full-path drill, so the drill's preconditions are the panel's
  /// by construction rather than by a list somebody keeps in step.
  /// </summary>
  internal static class AgentFrontDoor
  {
    /// <summary>
    /// Was the resolved model the user's PIN? Only when an override is set AND the resolved model IS that override:
    /// the resolver falls back when a pinned id is not in the catalogue, and a refusal blaming a pin that was not
    /// in effect would send the user to fix the wrong thing.
    /// </summary>
    public static bool IsPinned(string overrideModelId, string resolvedModelId) =>
      !string.IsNullOrEmpty(overrideModelId) &&
      string.Equals(resolvedModelId, overrideModelId, StringComparison.OrdinalIgnoreCase);

    /// <param name="agentRefusal">Lane B's decision: why the agent may not run on (model, provider, pinned), or
    /// null when it may.</param>
    /// <param name="hasKey">Is a key stored for this provider?</param>
    public static FrontDoorResult Check(string overrideModelId, ModelInfo resolved, string provider,
                                        Func<ModelInfo, string, bool, string> agentRefusal,
                                        Func<string, bool> hasKey)
    {
      var pinned = IsPinned(overrideModelId, resolved?.Id);

      // No checker is not a pass: both halves fail CLOSED, so a caller that forgets one cannot start a turn.
      var notEligible = agentRefusal == null
        ? "Dwight could not check whether this model can run the Build chat, so it has not started it."
        : agentRefusal(resolved, provider, pinned);
      if (notEligible != null)
        return new FrontDoorResult { Refusal = FrontDoorRefusal.NotEligible, Text = notEligible };

      if (hasKey == null || !hasKey(provider))
        return new FrontDoorResult { Refusal = FrontDoorRefusal.NoKey };

      return new FrontDoorResult { Refusal = FrontDoorRefusal.None };
    }
  }
}
