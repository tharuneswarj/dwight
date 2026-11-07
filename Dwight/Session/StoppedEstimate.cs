using System;

namespace Dwight.Session
{
  /// <summary>
  /// A RUN STOPPED AFTER ITS REQUEST WENT OUT STILL COST ITS INPUT (Brief DL). A provider bills the input tokens of a request it accepted, and the output it generated up to the
  /// cancellation, whether or not the client read the reply. Dwight cancels through a real token (ADR-089), so the provider's usage never arrives, and Brief AK's law ("every paid call
  /// leaves one line") had no number to write. This is the number: an ESTIMATE, marked as one everywhere it appears.
  ///
  /// <para>The input is the request's characters at the most characters per token this model has measured on this machine (Brief EG), so the fewest tokens it has counted for text
  /// of that size. The output is 0, because what was generated before the cut is unknowable. The cents are that input's alone, at the cache's read rate. So the estimate sits at or
  /// below what every comparable run measured here would bill for the request, and THAT IS ALL IT CAN CLAIM: a request the model counts more tightly than any it has measured can
  /// still bill under it, and a chat turn, whose lines carry no characters, is converted at its model's verb runs and is measured by none.</para>
  ///
  /// GH-free (ADR-091).
  /// </summary>
  internal static class StoppedEstimate
  {
    /// <summary>The mark on a line, a spend and a row.</summary>
    public const string Mark = "stopped";

    /// <summary>The estimated spend of a request that went out and was stopped, or null when nothing went out (no characters, no model): a Stop before the send writes nothing.</summary>
    /// <param name="imageTokens">Brief DX: what the request's images are estimated to cost, by the plugin's own formula. They are priced as tokens, never through the characters.</param>
    /// <remarks>
    /// THE INPUT IS PRICED AT THE CACHE'S RATE, for a verb as for a chat turn, because that is the least rate it can have cost. A chat step's input is mostly its transcript, served from the
    /// cache. A verb's is cached too: <c>StructuredOutput</c> sends with <c>CacheSystem</c>, which puts a breakpoint on the system block and on the transcript's tail, so a verb run again inside
    /// the cache's life reads the whole request at a tenth of the price. The plugin cannot tell from a stopped request which read it was. Priced cold, as a verb's was until #254's known item
    /// was closed, a stopped re-read stood above its bill. The cost of the low rate is that a cold first read, which writes the cache, reads far under its bill, and it is marked as an
    /// estimate wherever it shows.
    ///
    /// <para>THE TOKENS ARE THE FLOOR'S, NEVER THE MEDIAN'S (Brief EG): the eighteenth gate's row 22 converted 4,143 characters at a median of 1.72 and stood 27 percent above a bill of 1,717
    /// tokens. The question before a read keeps the median, because it estimates the likely bill.</para>
    /// </remarks>
    public static StoredSpend Of(ModelInfo model, long chars, TimeSpan elapsed, LedgerSample sample, long imageTokens = 0)
    {
      if (model == null || chars <= 0) return null;
      var tokens = LargeRun.FloorInputTokens(model, (int)Math.Min(chars, int.MaxValue), sample) + Math.Max(0, imageTokens);
      if (tokens <= 0) return null;
      var spend = new StoredSpend
      {
        Model = model.Id,
        Provider = model.Provider,
        ElapsedMs = (long)Math.Max(0, elapsed.TotalMilliseconds),
        InputTokens = (int)Math.Min(tokens, int.MaxValue),
        OutputTokens = 0,
        Chars = chars,
        Estimated = Mark,
      };
      if (model.IsLocal) spend.CostText = "local · free";
      else if (model.IsSubscription) spend.CostText = SpendMath.OllamaCloudCostText;
      else
      {
        spend.Cents = model.CostCents(0, 0, tokens, 0);   // the least rate the input can cost: see the remarks
        spend.CostText = SpendMath.CentsText(spend.Cents.Value);
      }
      return spend;
    }
  }
}
