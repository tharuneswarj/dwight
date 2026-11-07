using System;
using System.Collections.Generic;
using System.Linq;
using Dwight.Models;

namespace Dwight.Llm
{
  /// <summary>
  /// May the Scaffold agent run on THIS model, through THIS provider's connection? Two facts, both required,
  /// and they live in different places on purpose (lane B, section 3.1 step 2):
  ///
  /// <list type="number">
  /// <item><b>The MODEL may drive the tool loop</b> - the catalogue's <c>agentic</c> fact (#189). Not mere
  /// tool-call support: a Fast model can emit a tool call and still be the wrong model to drive a multi-step
  /// loop, which is exactly why #189 marked the Fast tiers false. A capability is a catalogue fact, never
  /// inferred from an id (ADR-082).</item>
  /// <item><b>Dwight's connection to the provider carries tools at all</b> - a fact about Dwight's CODE, not
  /// about any model, so it lives on the adapters beside the code that makes it true, and a test holds each
  /// declaration to what that adapter's request body really contains.</item>
  /// </list>
  ///
  /// <para><b>WHY IT EXISTS: A FACT WITH NO ENFORCER IS A COMMENT.</b> <c>agentic</c> was on all 21 models and
  /// nothing read it to decide anything - #189's "a 4-8B model can never be silently handed Scaffold" was kept by
  /// coincidence. And the coincidence had a live hole on the one provider that ran the agent: pin Claude Haiku
  /// 4.5 under Advanced and the Scaffold loop ran on a model the catalogue marks agentic=false. The old check
  /// could not see it, because it ran before the model was resolved - a check that runs before the thing it
  /// checks exists can only check something coarser.</para>
  ///
  /// <para>Every input is a parameter, so the property that matters is testable directly: flip the adapter fact
  /// and a capable model goes through while a small one is still refused. The plugin-side wrapper that reads the
  /// live catalogue and adapters is <c>LlmProviders.AgentRefusal</c>. The refusal is PLAIN TEXT - escaping is a
  /// property of the destination (#335), so the chat encodes it where it renders it.</para>
  /// </summary>
  internal static class AgentEligibility
  {
    /// <summary>
    /// Null when the Scaffold agent may run; otherwise the refusal, in our own words (#297's shape), saying what
    /// to do. <paramref name="agenticAlternatives"/> and <paramref name="providersThatCan"/> are read from the
    /// catalogue and the adapters by the caller - never typed into a sentence here (#146).
    /// </summary>
    public static string Check(ModelInfo model, bool adapterPassesTools, string providerLabel,
                               IReadOnlyList<ModelInfo> agenticAlternatives, IReadOnlyList<string> providersThatCan,
                               bool modelIsPinned)
    {
      var provider = string.IsNullOrWhiteSpace(providerLabel) ? "this provider" : providerLabel;

      // THE PROVIDER FIRST. If Dwight's connection cannot carry tools, no choice of model helps - naming models
      // to switch to would send the user somewhere that cannot work.
      if (!adapterPassesTools)
      {
        var can = (providersThatCan ?? Array.Empty<string>()).Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        // PRESENT TENSE, never "yet" (#883): no user-visible string promises a future of the product. It stays
        // true on its own - it can only appear while the adapter's PassesTools says so, so the day that flips,
        // the sentence stops appearing rather than going false.
        return "The Build chat does not run on " + provider + ": Dwight's connection to " + provider +
               " does not carry the tools the agent works through, so it could not even read your canvas. " +
               (can.Count > 0
                 ? "It runs on " + Join(can) + " - switch the provider in Dwight > Settings. "
                 : "Switch the provider in Dwight > Settings. ") +
               "Every other verb works on " + provider + " as usual.";
      }

      // FAIL CLOSED on a model Dwight cannot look up - the catalogue loader's own rule (absent agentic => false).
      if (model == null)
        return "Dwight cannot tell whether the model it would use can run the Build chat - it is not in the " +
               "model catalogue - so it will not guess. Pin a known model in Dwight > Settings > Model.";

      if (model.Agentic) return null;

      var name = string.IsNullOrWhiteSpace(model.DisplayName) ? model.Id : model.DisplayName;
      var others = (agenticAlternatives ?? Array.Empty<ModelInfo>())
        .Where(m => m != null && m.Agentic)
        .Select(m => string.IsNullOrWhiteSpace(m.DisplayName) ? m.Id : m.DisplayName)
        .ToList();

      // Plain statement of what works, never of what Dwight "trusts" (Tharun, 25 October). The pin is named as
      // the CAUSE when it is one: silently resolving a different model for Scaffold would override an explicit
      // choice without saying so - a silent substitution under a verdict (#208).
      var opening = modelIsPinned
        ? "You have pinned " + name + " for every action. The Build chat does not run on it. "
        : "The Build chat does not run on " + name + ". ";

      if (others.Count == 0)
        return opening + "No model on " + provider + " can run it - switch the provider in Dwight > Settings.";

      return opening + "On " + provider + (modelIsPinned ? " it runs on " : " the chat runs on ") + Join(others) +
             (modelIsPinned
               ? " - unpin it, or pin one of those, under Dwight > Settings > Model."
               : " - pick one under Dwight > Settings > Model.");
    }

    private static string Join(IReadOnlyList<string> items)
    {
      if (items.Count == 1) return items[0];
      return string.Join(", ", items.Take(items.Count - 1)) + " or " + items[items.Count - 1];
    }
  }
}
