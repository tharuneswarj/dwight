using System;

namespace Dwight.Data
{
  /// <summary>
  /// The token estimate for the graceful ceiling check (ADR-035). The old action-tailored GhJSON
  /// serialiser + context envelope (the `Build` / `SerializationProfile` / overview path) was retired with
  /// the GhJSON + `Converse` Reorganise path (ADR-077 convergence, Phase 6 WP-D): Explain, Diagnose, and
  /// Reorganise all run on `DwightGraph` + `StructuredOutput` now. Only the rough estimate survives.
  /// </summary>
  internal static class SelectionSerializer
  {
    /// <summary>Rough token estimate (~4 chars/token) for the ceiling check (ADR-035), and only for that. It says whether a read
    /// fits, where a stricter ratio would refuse a read that ran (BESS: 404,165 tokens from 1,897,000 characters). A price is
    /// <see cref="DefaultCharsPerToken"/> or a measurement (Brief CY).</summary>
    public static int EstimateTokens(int chars) => chars / 4;

    /// <summary>
    /// Brief CY: CHARACTERS PER TOKEN BEFORE THE LEDGER HAS MEASURED ANY, per provider, in one table. The ledger's own median takes over at
    /// <see cref="Dwight.Session.LedgerSample.AtLeast"/> lines. Anthropic's is measured on the ninth gate's row 10: an Explain of 75,000
    /// characters billed 39,693 input tokens, tool schema included, which is 1.89, rounded up. A JSON payload of ids, braces and numbers
    /// tokenizes far worse than prose. The other four are the architect's reading of the catalogue's notes, not yet measured.
    /// </summary>
    public static double DefaultCharsPerToken(string provider)
    {
      switch (provider)
      {
        case DwightConfig.ProviderAnthropic: return 2.0;
        case DwightConfig.ProviderOpenAI: return 3.5;
        case DwightConfig.ProviderGoogle: return 3.5;
        case DwightConfig.ProviderOllamaLocal: return 3.5;
        case DwightConfig.ProviderOllamaCloud: return 3.5;
        default: return 3.5;
      }
    }

    /// <summary>The tokens in this many characters at this ratio, rounded up.</summary>
    public static long EstimateTokens(int chars, double charsPerToken) => (long)Math.Ceiling(chars / charsPerToken);
  }
}
