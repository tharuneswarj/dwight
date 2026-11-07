using System;
using System.Collections.Generic;
using System.Linq;

namespace Dwight.Models
{
  /// <summary>
  /// The pure model SELECTION (ADR-082, ADR-091): given a candidate list and a task's band, which model
  /// wins. Extracted from <see cref="ModelResolver"/> at Phase-8 WP-2 Stage C so the choice can be
  /// asserted headlessly - it reads no catalogue and touches no disk.
  ///
  /// WHY THIS EXISTS AS ITS OWN UNIT (#199). The order was tier-distance, then price - and catalogue v2
  /// put two models in the Strong band at an identical price sum (Sonnet 5 and Sonnet 4.6, both
  /// $3/$15). A tie there fell through to whatever order the entries happened to sit in the JSON file,
  /// so Sonnet 5 won only by being on the earlier line. Reordering two lines in a data file - an edit
  /// nobody would think twice about, and one a USER may make in their own copy - would silently move
  /// every Strong-band task to a different model. Nothing would error; the bill and the behaviour would
  /// just change.
  ///
  /// So the comparison is now a TOTAL ORDER with no appeal to file position: tier distance, then price,
  /// then the catalogue's explicit preference, then the id ordinally. The last key guarantees the
  /// outcome is a function of the DATA rather than its layout.
  /// </summary>
  internal static class ModelPick
  {
    private static readonly string[] TierOrder = { "Fast", "Mid", "Strong", "Top" };

    /// <summary>U52: does a pinned model belong to <paramref name="provider"/>? A pin is one id for every provider in
    /// settings.json, so switching the provider left a model the new provider cannot serve (OpenAI's GPT-5.6 Terra
    /// sent to Google: a 404). Null pin (an id the catalogue does not know) counts as not belonging. Both the
    /// provider switch and the resolver read this one rule.</summary>
    public static bool PinBelongsTo(ModelInfo pin, string provider) =>
      pin != null && string.Equals(pin.Provider, provider, StringComparison.OrdinalIgnoreCase);

    /// <summary>The panel's sentence when a provider switch clears a pin. Names the model and the provider it belongs to.</summary>
    public static string PinClearedMessage(ModelInfo pin) =>
      "Pin cleared: " + (string.IsNullOrWhiteSpace(pin.DisplayName) ? pin.Id : pin.DisplayName) + " is " +
      ("AEIOUaeiou".IndexOf(pin.Provider?[0] ?? 'x') >= 0 ? "an " : "a ") + pin.Provider + " model";

    public static int Idx(string tier)
    {
      var i = Array.IndexOf(TierOrder, tier);
      return i < 0 ? 1 : i;   // unknown tier -> Mid
    }

    /// <summary>
    /// U72: THE CHAT PICKS A MODEL THAT CAN RUN IT. The pool the Scaffold chat is resolved from: the provider's models the
    /// catalogue marks <c>agentic</c>. A band-and-price pick landed on GPT-6.1 Sol, which the chat then refused, for every
    /// OpenAI user without a pin. On Economy the floor drops to the lowest agentic tier the provider has, so the dial still
    /// means something (a lower band, which on OpenAI is not the lower price: GPT-5.6 Terra costs more than GPT-6.1 Sol); the other profiles keep the task's band over the agentic rows. A provider
    /// with NO agentic row gets its whole list back, so <c>AgentEligibility</c>'s refusal is what the user reads, unchanged.
    /// A pin never comes through here (the settings return it first), so a pin is never substituted.
    /// </summary>
    public static IReadOnlyList<ModelInfo> AgentPool(IReadOnlyList<ModelInfo> models, bool economy, ref string floor)
    {
      var agentic = (models ?? new ModelInfo[0]).Where(m => m != null && m.Agentic).ToList();
      if (agentic.Count == 0) return models;
      if (economy)
      {
        var lowest = agentic.Min(m => Idx(m.Tier));
        if (lowest < Idx(floor)) floor = TierOrder[lowest];
      }
      return agentic;
    }

    /// <summary>
    /// The cheapest model nearest <paramref name="target"/> within [floor, ceiling], optionally
    /// structured-only; falls back to the nearest band if nothing sits inside it.
    /// </summary>
    public static ModelInfo Choose(IReadOnlyList<ModelInfo> models, string target, string floor, string ceiling,
      bool structuredOnly)
    {
      if (models == null) return null;
      // Brief BZ: a pin-only row is not a candidate, at any band and any price. A pin reaches it by another door, so this stands for every path that chooses.
      var pool = models.Where(m => !m.PinOnly && (!structuredOnly || m.StructuredOutput)).ToList();
      if (pool.Count == 0) return null;

      int lo = Idx(floor), hi = Idx(ceiling), want = Idx(target);
      var inBand = pool.Where(m => { var t = Idx(m.Tier); return t >= lo && t <= hi; }).ToList();
      var search = inBand.Count > 0 ? inBand : pool;

      return search
        .OrderBy(m => Math.Abs(Idx(m.Tier) - want))                      // 1. nearest the target band
        .ThenBy(m => m.InputPerMTok + m.OutputPerMTok)                   // 2. cheapest (ADR-082's dial)
        .ThenBy(m => m.Preference)                                       // 3. the catalogue's explicit say
        .ThenBy(m => m.Id, StringComparer.Ordinal)                       // 4. total order - never file order
        .FirstOrDefault();
    }
  }
}
