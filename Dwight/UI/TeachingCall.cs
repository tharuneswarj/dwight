using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Dwight.Llm;

namespace Dwight.UI
{
  /// <summary>The LLM half of <see cref="TeachingCall"/> (the pure half, the payload and the checks, is in TeachingDeck.cs, which compiles without the provider seam).</summary>
  internal static partial class TeachingCall
  {
    internal sealed class Result
    {
      public IReadOnlyList<DeckCard> Cards = Array.Empty<DeckCard>();
      public LlmUsage Usage = new LlmUsage();
      /// <summary>Set when the call itself failed (a provider error, a refusal): the Walk then runs without questions and says so.</summary>
      public string Error;
      /// <summary>The call itself failed, as against a reply whose cards the checks dropped (<see cref="Deal"/>).</summary>
      public bool Failed;
    }

    /// <summary>
    /// Ask once, repair once (Brief EI: the one call Learn makes; Brief EO: it writes the why-questions too). A reply that fails a check is repaired by the structured
    /// seam's own single round-trip, fed the first reason; whatever the LAST reply held that passed is kept per card, so one bad card does not cost the rest. A failure
    /// of the call itself is a result with an error and the usage the provider billed (carried on the exception), never an exception: the Explain it follows has
    /// succeeded and is not failed by this.
    /// </summary>
    /// <summary>Brief EO: the deck's fields beyond the Walk's own request (the selection, the steps, the plugin's cards), allowed for before they are known.</summary>
    public const int DeckAllowanceChars = 2500;

    /// <summary>The quiz's request in characters before the Walk has answered, for the question before a Learn run (the architect's ruling 3): the prompt, the Walk's request
    /// sent again, and the allowance.</summary>
    public static int RequestChars(int walkRequestChars) => Prompts.Prompts.TeachingSystem.Length + Math.Max(0, walkRequestChars) + DeckAllowanceChars;

    public static Result Ask(ILlmProvider provider, DeckAsk ask, int maxTokens, CancellationToken cancel = default)
    {
      var result = new Result();
      if (ask == null || (ask.Facts.Count == 0 && ask.Why == 0)) return result;

      var payload = Payload(ask);
      IReadOnlyList<DeckCard> best = Array.Empty<DeckCard>();
      Func<string, (bool ok, string error)> validate = json =>
      {
        var (kept, error) = Parse(json, ask);
        best = kept;
        return (error == null, error);
      };

      try
      {
        var res = StructuredOutput.Get(provider, Prompts.Prompts.TeachingSystem, payload,
          Prompts.Prompts.TeachingToolName, Prompts.Prompts.TeachingToolDescription, Prompts.Prompts.TeachingSchema, validate, maxTokens, cancel);
        result.Usage = res.Usage;
        result.Cards = best;
        if (!res.Ok && best.Count == 0) result.Error = res.Error ?? "no usable questions";
      }
      catch (OperationCanceledException) { throw; }
      catch (Exception ex)
      {
        if (UsageCarrier.TryTake(ex, out var paid)) result.Usage = paid;
        result.Cards = best;   // what the last reply read held that passed is kept, whatever stopped the call after it
        result.Error = ex.Message;
        result.Failed = true;
      }
      return result;
    }
  }

  /// <summary>The LLM half of <see cref="JudgeCall"/>.</summary>
  internal static partial class JudgeCall
  {
    internal sealed class Result
    {
      public JudgeVerdict Verdict;
      public string Sentence;
      public LlmUsage Usage = new LlmUsage();
      public string Error;
    }

    /// <summary>The answer budget for a verdict and one sentence: small, because the judge says little.</summary>
    public const int MaxTokens = 400;

    /// <summary>Ask once, repair once. A reply that is no verdict, a refusal or a provider error is a result with no verdict and the usage billed, which the card reads
    /// as could not judge. A Stop is an <see cref="OperationCanceledException"/>, carrying any usage the provider billed.</summary>
    public static Result Ask(ILlmProvider provider, DeckCard card, string typed, ICollection<int> ids, CancellationToken cancel = default)
    {
      var result = new Result();
      var payload = Payload(card, typed);
      (JudgeVerdict v, string s) best = (JudgeVerdict.None, null);
      Func<string, (bool ok, string error)> validate = json =>
      {
        var (verdict, sentence, error) = Parse(json, ids);
        if (error == null) best = (verdict, sentence);
        return (error == null, error);
      };
      try
      {
        var res = StructuredOutput.Get(provider, Prompts.Prompts.JudgeSystem, payload,
          Prompts.Prompts.JudgeToolName, Prompts.Prompts.JudgeToolDescription, Prompts.Prompts.JudgeSchema, validate, MaxTokens, cancel);
        result.Usage = res.Usage;
        result.Verdict = best.v;
        result.Sentence = best.s;
        if (best.v == JudgeVerdict.None) result.Error = res.Error ?? "no verdict";
      }
      catch (OperationCanceledException) { throw; }
      catch (Exception ex)
      {
        if (UsageCarrier.TryTake(ex, out var paid)) result.Usage = paid;
        result.Verdict = JudgeVerdict.None;
        result.Error = ex.Message;
      }
      return result;
    }
  }
}
