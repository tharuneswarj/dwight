using System;
using System.Collections.Generic;

namespace Dwight
{
  /// <summary>
  /// One selectable model: id, friendly name, owning provider, pricing (USD per 1M tokens), context
  /// window, and a plain speed note. Pricing/context drive the honest cost/speed estimate (ADR-041) and
  /// the graceful input ceiling (ADR-035). Local models are free (<see cref="IsLocal"/>). Concrete ids and
  /// prices are the single source of truth here and are Tharun's to keep current (ADR-057, Phase 5).
  /// </summary>
  internal sealed class ModelInfo
  {
    public string Id { get; }
    public string DisplayName { get; }
    public string Provider { get; }
    public string Tier { get; }            // Fast | Mid | Strong | Top (the catalogue's capability band)
    public double InputPerMTok { get; }
    public double OutputPerMTok { get; }
    public int ContextWindow { get; }
    public int MinVramGb { get; }          // local only - the hardware hint shown in Advanced settings
    public string Status { get; }          // stable | preview | confirm | ... (catalogue metadata)
    public string PricingModel { get; }    // perToken | subscription | free
    public bool StructuredOutput { get; }  // reliable structured/tool output (the structured-output gate)
    public bool Reasoning { get; }         // emits a thinking/reasoning pass
    public bool ReasoningKnob { get; }     // the reasoning effort is adjustable
    public bool Caching { get; }
    public string SpeedNote { get; }

    /// <summary>
    /// Phase-8 WP-2 (#188/#192): what OMITTING the thinking field does on this model - the fact the
    /// three-state wire is resolved from. Never inferred from the model id (ids churn; a hardcoded
    /// match is the map ADR-092 rejected):
    ///   <c>adaptive-on</c>  - omitting runs thinking (the Claude 5 family), so keeping it OFF for
    ///                         comprehension requires an EXPLICIT <c>{"type":"disabled"}</c>;
    ///   <c>adaptive-off</c> - omitting means no thinking (Sonnet 4.6 / Opus 4.8), so OFF is omission
    ///                         and ON is <c>{"type":"adaptive"}</c>;
    ///   <c>none</c>         - no adaptive thinking mode Dwight will use, so the field is never sent.
    /// Models whose only thinking mode is the retired fixed <c>budget_tokens</c> budget are "none":
    /// Dwight does not carry two thinking dialects, and those models never reach a thinking-on task
    /// anyway (Scaffold floors at Strong; they are Fast-band).
    /// </summary>
    public string ThinkingDefault { get; }

    /// <summary>Brief BZ: true when this row has no way to turn thinking off that Dwight may call off (see <see cref="Dwight.Llm.ThinkingWire.CannotDisable"/>).</summary>
    public bool CannotDisableThinking => Dwight.Llm.ThinkingWire.CannotDisable(ThinkingDefault);

    /// <summary>
    /// Phase-8 WP-2 Stage C (#199): the catalogue's explicit say when two models tie on band AND price.
    /// LOWER WINS. It exists because catalogue v2 created exactly that tie in the Strong band (Sonnet 5
    /// and Sonnet 4.6, both $3/$15), where the winner would otherwise fall out of JSON line order - so
    /// reordering two lines in a data file would silently move every Strong task to another model.
    /// Absent reads as 100, a neutral middle: an entry that expresses no preference neither jumps the
    /// queue nor is pushed behind one that also expresses none.
    /// </summary>
    public int Preference { get; }

    /// <summary>Phase-8 WP-2 (#188/#189): may this model be handed the AGENT LOOP? False for the small
    /// local tiers - a 4-8B model must never be silently handed Scaffold. With ADR-082's Strong floor
    /// this is belt AND braces: the local catalogue carries no Strong/Top entry either.</summary>
    public bool Agentic { get; private set; }

    /// <summary>Brief BV: eligibility is by measurement, so a model the catalogue does not yet mark may be tried by the gate that measures it. In memory
    /// only, for this session: nothing reads it back from disk, and the catalogue changes only when that gate passes.</summary>
    internal void PinAgenticForGate() => Agentic = true;

    /// <summary>True when omitting the thinking field would leave thinking RUNNING - the only case that
    /// needs an explicit disable to honour ADR-082's "thinking stays off for comprehension". The
    /// interpretation lives with the wire that consumes it, so there is one definition.</summary>
    public bool ThinkingOnByDefault => Dwight.Llm.ThinkingWire.IsOnByDefault(ThinkingDefault);

    /// <summary>True when Dwight can turn adaptive thinking ON for this model.</summary>
    public bool SupportsAdaptiveThinking => Dwight.Llm.ThinkingWire.SupportsAdaptive(ThinkingDefault);

    /// <summary>Brief BZ: does this row accept a forced <c>tool_choice</c>? False is a 400 on Sonnet 5.5, Opus 5.5 and Fable 5.1, so nothing is ever forced at them.
    /// Absent in the catalogue reads as true, which is what every earlier row has been measured doing.</summary>
    public bool ForcedToolChoice { get; }

    /// <summary>Brief BZ: does a structured verb on this row go through <c>output_config.format</c> rather than a forced tool?</summary>
    public bool StructuredFormat { get; }

    /// <summary>Brief BZ: does this row run the prefix check on replayed thinking (preserved thinking: "Claude 5.1 and later")? Then the transcript must stay append-only:
    /// clearing an earlier tool result is a 400 at the next step on an account created on or after 31 August 2026.</summary>
    public bool ThinkingBound { get; }

    /// <summary>Brief BZ: true when nothing resolves to this row by itself, and only a person's pin reaches it. A price is not a reason to choose a model nobody has measured,
    /// and at 2.5 times Opus 5.5, Fable 5.1 is always a person's choice.</summary>
    public bool PinOnly { get; }

    /// <summary>Brief BZ: the effort a comprehension task sends where thinking cannot be turned off, or null. Effort defaults differ by model, so it is said rather than left.</summary>
    public string ThinkingOffEffort { get; }

    /// <summary>Brief BZ: the data retention in days this row needs, or 0. An organisation on less gets a 400 that has to say why (Fable 5.1, 30 days).</summary>
    public int RetentionDays { get; }

    /// <summary>Brief BZ, the follow-up: what a person should read before choosing such a row, where they choose it. Null on every other row. A test holds the public privacy page to the same words.</summary>
    public string RetentionSentence => RetentionDays <= 0 ? null :
      DisplayName + " requires Anthropic to keep requests for " + RetentionDays + " days, so it is not available under zero data retention.";

    /// <summary>Brief DX: does this row take image input, by its vendor's page? The fact lived only in <see cref="SpeedNote"/> prose, which no code can read. False refuses an attach by the model's name,
    /// before any call. Absent reads as false, so a row added by hand refuses until it says so.</summary>
    public bool Vision { get; }

    /// <summary>Does this row count an image by the measured rule (<see cref="Dwight.Session.ChatImage.Tokens"/>)? Only such a row shows a number on the image's chip. Absent reads as false, so a row says the provider counts it unless it says otherwise.</summary>
    public bool ImagePatchTokens { get; }

    /// <summary>list-models reconcile status, set at runtime by <see cref="Dwight.Llm.ModelReconciler"/>:
    /// green (callable) | amber (in catalogue, not on this key) | red (id 404s) | unknown (not yet checked).</summary>
    public string Reconcile { get; set; } = "unknown";

    public bool IsLocal => PricingModel == "free";
    public bool IsSubscription => PricingModel == "subscription";

    /// <summary>What a cache hit costs: the vendor's own price where the catalogue states it, otherwise Anthropic's ratio as an estimate. Most of each chat step's input
    /// is cached, so this decides whether the number a person sees is right. A ratio holds for most rows and not all: GPT-6.1 Sol is x0.05 and Gemini 4 Argon x0.05.</summary>
    public double CacheReadPerMTok => _cacheReadPerMTok ?? InputPerMTok * 0.10;

    /// <summary>Brief CY: does the catalogue state a cache price for this row? Only then may a sentence mention the cache.</summary>
    public bool HasCachedInPrice => _cacheReadPerMTok.HasValue;

    /// <summary>What writing to the cache costs: the vendor's own price where the catalogue states it, otherwise Anthropic's ratio. A vendor that bills no write reports
    /// no write tokens, so its row needs no price here.</summary>
    public double CacheWritePerMTok => _cacheWritePerMTok ?? InputPerMTok * 1.25;

    private readonly double? _cacheReadPerMTok, _cacheWritePerMTok;

    /// <summary>The cost of one exchange in cents, at this row's prices. Written once into the record, where it stays: nothing reads a past line back through this.</summary>
    public double CostCents(long inputTokens, long outputTokens, long cacheReadTokens, long cacheWriteTokens) =>
      (inputTokens / 1_000_000.0 * InputPerMTok +
       outputTokens / 1_000_000.0 * OutputPerMTok +
       cacheReadTokens / 1_000_000.0 * CacheReadPerMTok +
       cacheWriteTokens / 1_000_000.0 * CacheWritePerMTok) * 100.0;

    public ModelInfo(string id, string displayName, string provider, string tier, double inPer, double outPer, int ctx,
      bool structured = false, bool reasoning = false, bool reasoningKnob = false, bool caching = false,
      string status = "stable", string pricingModel = "perToken", int minVramGb = 0, string speed = null,
      string thinkingDefault = null, bool agentic = false, int? preference = null, double? cacheReadPerMTok = null, double? cacheWritePerMTok = null,
      bool forcedToolChoice = true, bool structuredFormat = false, bool thinkingBound = false, bool pinOnly = false, string thinkingOffEffort = null, int retentionDays = 0, bool vision = false, bool imagePatchTokens = false)
    {
      Vision = vision;
      ImagePatchTokens = imagePatchTokens;
      ForcedToolChoice = forcedToolChoice;
      StructuredFormat = structuredFormat;
      ThinkingBound = thinkingBound;
      PinOnly = pinOnly;
      ThinkingOffEffort = thinkingOffEffort;
      RetentionDays = retentionDays;
      _cacheReadPerMTok = cacheReadPerMTok;
      _cacheWritePerMTok = cacheWritePerMTok;
      Preference = preference ?? 100;   // neutral middle when the catalogue expresses none
      Id = id; DisplayName = displayName; Provider = provider; Tier = tier ?? "Mid";
      InputPerMTok = inPer; OutputPerMTok = outPer; ContextWindow = ctx; MinVramGb = minVramGb;
      Status = status ?? "stable"; PricingModel = pricingModel ?? "perToken";
      StructuredOutput = structured; Reasoning = reasoning; ReasoningKnob = reasoningKnob; Caching = caching;
      SpeedNote = speed;
      // Absent => "none": a catalogue entry that does not state its thinking default never gets a
      // thinking field. Silence must mean the SAFE state, not a guess.
      ThinkingDefault = string.IsNullOrWhiteSpace(thinkingDefault) ? "none" : thinkingDefault.Trim();
      Agentic = agentic;
    }
  }
}
