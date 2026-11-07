using System;
using Dwight.Session;

namespace Dwight.Agent
{
  /// <summary>
  /// WHAT ONE CHAT TURN LEAVES ON THE RECORD (Brief AK: every paid call one ledger line; Brief DL: a stopped one an estimate). A provider bills a request it accepted whether or not the
  /// reply is read. Stop and the session's clock bump the generation before the turn returns, so a stopped turn always ends on the superseded branch of its completion, never on the
  /// normal one, and both branches come through here so the two cannot differ. A turn stopped between steps has only what was billed; one stopped with a request out has that request's
  /// input on top, by the provider's own count where its stream said it, else as a marked estimate. GH-free (ADR-091).
  /// </summary>
  internal static class TurnSpend
  {
    /// <summary>The turn's one line, or null when nothing went out.</summary>
    /// <param name="billed">What the turn's usage comes to, or null when it has none.</param>
    public static StoredSpend Of(StoredSpend billed, ModelInfo model, TurnResult result, TimeSpan elapsed, LedgerSample sample)
    {
      if (result == null || result.Outcome != TurnOutcome.Stopped || result.InFlightChars <= 0) return billed;
      var estimate = StoppedEstimate.Of(model, result.InFlightChars, elapsed, sample, result.InFlightImageTokens);   // at the cache's rate, the least rate it can have cost
      if (estimate == null) return billed;

      // Combine adds two attempts, and these are one turn seen twice: one clock, one count of steps.
      var spent = SpendMath.Combine(billed, estimate);
      spent.ElapsedMs = estimate.ElapsedMs;
      spent.Steps = result.Steps;
      spent.ToolCalls = result.ToolCalls;
      return spent;
    }

    /// <summary>What the conversation says about a stopped turn's number, or null when the number needs no words: a turn that ended by itself, or was stopped between steps and so holds the whole bill.</summary>
    public static string StoppedLine(StoredSpend spent, TurnResult result)
    {
      if (spent == null || result == null || result.Outcome != TurnOutcome.Stopped) return null;
      // Brief EG: no "floor" here. A chat turn's line carries no characters, so the ratio its request is converted at was measured on its model's verb runs, never on a chat's, and a
      // claim the plugin cannot prove in every state is not made (#297). The provider's own count, below, is one.
      if (spent.Estimated != null)
        return "The provider's count for the stopped request never reached Dwight, so this turn is on the ledger as an estimate from the request's size, " + Amount(spent) + "." +
          (result.InFlightUncountedImages > 0 ? " " + ChatImage.StoppedLeftOutSentence(result.InFlightUncountedImages) : "");
      if (result.InFlightCounted)
        return "This stopped turn is on the ledger at what the provider had counted, " + Amount(spent) + ". What the model wrote before the stop is not in that count, so it is a floor.";
      return null;
    }

    private static string Amount(StoredSpend s) =>
      s.Cents.HasValue ? SpendMath.CentsText(s.Cents.Value) : SpendMath.TokensText(s.TotalInputTokens) + " tokens in";
  }
}
