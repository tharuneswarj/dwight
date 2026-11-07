using System;
using System.Collections.Generic;
using System.Linq;

namespace Dwight.Session
{
  /// <summary>
  /// What a no-vision refusal names instead of "a model that does" (Brief EE unit 2). Pure: the refusal's sentence is <see cref="ChatImage.VisionAdvice.Sentence"/>'s,
  /// built here from the catalogue's rows, not a template in the UI. Kept apart from <see cref="ChatImage"/> so that file stays free of the model types.
  /// </summary>
  internal static class VisionAdvisor
  {
    /// <summary>
    /// Up to two models of the same provider that read images and that the model menu offers (agentic, in the menu's order), or, when the provider has
    /// none, the best-ranked model elsewhere that does (Anthropic first, then the catalogue's own preference). <paramref name="providerModels"/> is the
    /// list the menu is drawn from, <paramref name="allModels"/> the whole catalogue.
    /// </summary>
    public static ChatImage.VisionAdvice Advise(IReadOnlyList<ModelInfo> providerModels, IReadOnlyList<ModelInfo> allModels)
    {
      var same = (providerModels ?? new ModelInfo[0]).Where(m => m != null && m.Agentic && m.Vision).Take(2).Select(NameOf).ToList();
      if (same.Count > 0) return new ChatImage.VisionAdvice { SameProvider = same };
      var elsewhere = (allModels ?? new ModelInfo[0]).Where(m => m != null && m.Agentic && m.Vision)
        .OrderBy(m => string.Equals(m.Provider, "Anthropic", StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(m => m.Preference).FirstOrDefault();
      return new ChatImage.VisionAdvice { Elsewhere = elsewhere == null ? null : NameOf(elsewhere) + " (" + elsewhere.Provider + ")" };
    }

    private static string NameOf(ModelInfo m) => string.IsNullOrWhiteSpace(m.DisplayName) ? m.Id : m.DisplayName;
  }
}
