using System;
using System.Globalization;
using Dwight.Data;
using Dwight.Models;

namespace Dwight.Session
{
  /// <summary>
  /// Brief CS: what a verb says before a read that will cost real money. BESS: 4,972 components selected with one
  /// click, Explain, 102.51 cents, with no word before or during. The ceiling asks whether a read FITS the model and a
  /// million-token model fits, so this is a second question after it: does the person want to pay for this one.
  ///
  /// The estimate is the plugin's, from the characters that will go out and the catalogue's price, through the same
  /// <see cref="ModelInfo.CostCents"/> the ledger prices a run with. Brief CY: IT COUNTS WHAT THE BILL COUNTS, a cold read and the answer.
  /// The ninth gate's row 10 said "about 5 cents" and was billed 10.74, because four characters to a token is not what a JSON payload
  /// tokenizes to, and the answer was left out. Both come from the ledger's recent lines (<see cref="LedgerSample"/>) and from a stated
  /// default until it has enough. A second read of the same selection inside the cache's life costs far less than this says; the plugin
  /// cannot know that it will, so the sentence only says "if".
  /// </summary>
  internal static class LargeRun
  {
    /// <summary>The Setting's default: "Ask above", 25 cents. Zero never asks.</summary>
    public const int DefaultThresholdCents = 25;

    /// <summary>What a cent buys on the catalogue's Claude Sonnet 5 at its write price, $2.50 a million. An unpriced model has
    /// no cents to compare, so it asks at the size that would cost the threshold there. The Setting's hint says so.</summary>
    public const int UnpricedTokensPerCent = 4000;

    public const string ContinueLabel = "Continue";
    public const string CancelLabel = "Cancel";
    public const string CancelledTitle = "Nothing was sent";
    public const string CancelledSentence = "You cancelled before the read went out, so nothing was spent.";

    /// <summary>What a to-script run is called here. A ledger line calls it Simplify, so no line carries this name and its allowance is the stated one.</summary>
    public const string ToScript = "To script";

    /// <summary>Brief EO: an Explain chosen as Learn: the Walk's read and answer and the quiz's, which sends the Walk's request again with its own. No line carries this name, so its allowance is the stated one.</summary>
    public const string Learn = "Learn";
    /// <summary>Brief EO: the quiz's call alone, as Play again sends it.</summary>
    public const string Quiz = "Quiz";
    /// <summary>Brief EO: a typed Learn answer's judge, a verdict and one sentence.</summary>
    public const string Judge = "Judge";

    public const string ForReadAndAnswer = " for the read and the answer";
    public const string CachedClause = ", less if the last read is still cached";

    /// <summary>The answer's length before the ledger has <see cref="LedgerSample.AtLeast"/> lines of its own, in tokens, by what the run is.
    /// A chat turn is not here because it does not ask. Reorganise's is its labelling call, its one paid call.</summary>
    public static long DefaultOutputTokens(string verb)
    {
      switch (verb)
      {
        case DwightVerbs.Explain: return 1500;
        case DwightVerbs.Diagnose: return 900;
        case DwightVerbs.Reorganise: return 700;
        case DwightVerbs.Simplify: return 2500;
        case ToScript: return 3000;
        case Learn: return 3000;
        case Quiz: return 1500;
        case Judge: return 120;
        default: return 1500;
      }
    }

    /// <summary>True when the catalogue gives this model a price per token. Ollama Cloud's subscription and a local model do not.</summary>
    public static bool IsPriced(ModelInfo model) => !model.IsLocal && !model.IsSubscription && model.InputPerMTok > 0;

    /// <summary>What these input tokens cost on a first read, in cents. Anthropic bills a verb's whole read as cache writes, since
    /// <c>CacheSystem</c> marks the system block and the transcript's tail; the other providers bill it as input.</summary>
    public static double EstimateCents(ModelInfo model, long inputTokens) =>
      model.Provider == DwightConfig.ProviderAnthropic
        ? model.CostCents(0, 0, 0, inputTokens)
        : model.CostCents(inputTokens, 0, 0, 0);

    /// <summary>The input tokens in this read: at the median ratio this model's recent lines measured, else its provider's, else the stated default.</summary>
    public static long InputTokens(ModelInfo model, int chars, LedgerSample sample) =>
      SelectionSerializer.EstimateTokens(chars, sample?.CharsPerToken(model.Provider, model.Id) ?? SelectionSerializer.DefaultCharsPerToken(model.Provider));

    /// <summary>Brief EG: the FEWEST input tokens this read can be, as far as this machine has measured: at the most characters per token this model's recent lines
    /// counted, else its provider's, else the stated default. A stopped request's estimate (<see cref="StoppedEstimate"/>); the question before a read keeps the median.</summary>
    public static long FloorInputTokens(ModelInfo model, int chars, LedgerSample sample) =>
      SelectionSerializer.EstimateTokens(chars, sample?.FloorCharsPerToken(model.Provider, model.Id) ?? SelectionSerializer.DefaultCharsPerToken(model.Provider));

    /// <summary>The answer's length: what this verb gave on this model lately, or its stated default.</summary>
    public static long OutputTokens(ModelInfo model, string verb, LedgerSample sample) =>
      sample?.OutputTokens(verb, model.Id) ?? DefaultOutputTokens(verb);

    /// <summary>Brief CY: a cold read and its answer, in cents. Pure over what the caller read once.</summary>
    public static double Estimate(ModelInfo model, int chars, string verb, LedgerSample sample) =>
      EstimateCents(model, InputTokens(model, chars, sample)) + model.CostCents(0, OutputTokens(model, verb, sample), 0, 0);

    /// <summary>
    /// Brief CY: AT THE THRESHOLD ASKS. The ninth gate's row 10 set the menu's own 5 cents, the estimate came to 5, and nothing asked, so the
    /// gate had to set a value the menu does not offer. A setting the menu's own values cannot reach is not a setting. Rounded to a
    /// millionth of a cent first, so a sum that lands a hair under the setting in floating point is at it.
    /// </summary>
    public static bool AtOrOver(double cents, int thresholdCents) => Math.Round(cents, 6) >= thresholdCents;

    /// <summary>Whether this read asks. Zero or less never does, and a read that sends nothing (a labelling pass whose names are all cached) has no answer to price either.</summary>
    public static bool IsOver(ModelInfo model, int chars, int thresholdCents, string verb, LedgerSample sample)
    {
      if (thresholdCents <= 0 || chars <= 0) return false;
      return IsPriced(model)
        ? AtOrOver(Estimate(model, chars, verb, sample), thresholdCents)
        : InputTokens(model, chars, sample) >= (long)thresholdCents * UnpricedTokensPerCent;
    }

    /// <summary>The sentence to ask with, or null when this read does not ask.</summary>
    public static string Ask(int components, int chars, ModelInfo model, int thresholdCents, string verb, LedgerSample sample = null,
      string documentId = null, DateTime nowUtc = default) =>
      IsOver(model, chars, thresholdCents, verb, sample) ? Sentence(components, chars, model, verb, sample, documentId, nowUtc) : null;

    /// <summary>The sentence for this model: cents for the read and the answer where the catalogue prices it, the read's token count
    /// where it does not, whose count the answer is not part of.</summary>
    public static string Sentence(int components, int chars, ModelInfo model, string verb, LedgerSample sample = null,
      string documentId = null, DateTime nowUtc = default)
    {
      if (!IsPriced(model)) return Sentence(components, chars, About(InputTokens(model, chars, sample)) + " tokens", model.DisplayName);
      var cached = model.HasCachedInPrice && sample != null && sample.ReadWithinCacheLife(documentId, verb, model.Id, chars, nowUtc);
      return Sentence(components, chars, CostText(Estimate(model, chars, verb, sample)), model.DisplayName, ForReadAndAnswer + (cached ? CachedClause : ""));
    }

    /// <summary>The words, in one place. Invariant digits, so the sentence reads the same on every machine.</summary>
    public static string Sentence(int components, int chars, string estimate, string modelName, string counted = "") =>
      "This will read " + components.ToString("n0", CultureInfo.InvariantCulture) + (components == 1 ? " component" : " components") +
      ", about " + About(chars) + " characters, about " + estimate + " on " + modelName + counted + ". Continue?";

    /// <summary>"$1.02" from a dollar up, "26 cents" under it, rounded before it is sorted so 99.6 reads as a dollar.</summary>
    public static string CostText(double cents)
    {
      var whole = Math.Round(cents, MidpointRounding.AwayFromZero);
      if (whole >= 100) return "$" + (cents / 100).ToString("0.00", CultureInfo.InvariantCulture);
      if (whole < 1) return "less than 1 cent";
      return whole.ToString("0", CultureInfo.InvariantCulture) + (whole == 1 ? " cent" : " cents");
    }

    /// <summary>To the nearest thousand, which is all the estimate can claim. Under a thousand it stays as it is.</summary>
    private static string About(long n)
    {
      var shown = n < 1000 ? n : (long)Math.Round(n / 1000.0, MidpointRounding.AwayFromZero) * 1000;
      return shown.ToString("n0", CultureInfo.InvariantCulture);
    }
  }
}
