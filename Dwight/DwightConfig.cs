using System;
using System.Collections.Generic;
using System.Linq;
using Dwight.Models;

namespace Dwight
{

  /// <summary>
  /// Central knobs for the LLM path. Phase 5 (ADR-057/066) turns the Anthropic-only table into a
  /// per-provider tier table: each action is pinned to a <see cref="ModelTier"/>, and
  /// <see cref="Resolve"/> maps (provider, cost-posture, tier) -> a concrete model. Model ids/prices are
  /// sane current defaults and live ONLY here - everything resolves through this table (no scattered ids).
  /// </summary>
  internal static class DwightConfig
  {
    // --- providers (ADR-066). OllamaLocal is the fully-local privacy wedge (no key, no cloud). ----------
    public const string ProviderAnthropic = "Anthropic";
    public const string ProviderOpenAI = "OpenAI";
    public const string ProviderGoogle = "Google";
    public const string ProviderOllamaLocal = "OllamaLocal";
    public const string ProviderOllamaCloud = "OllamaCloud";

    /// <summary>Where "Report a problem" opens a prefilled issue.</summary>
    public const string FeedbackRepo = "tharuneswarj/dwight";

    /// <summary>Whether <see cref="FeedbackRepo"/> exists in public. While false, Report a problem hides its GitHub button, because the page it
    /// opens is a 404. Flipped to true in the 1.0.0 build, the one that comes with the public repository (Brief EK).</summary>
    public const bool RepoPublic = true;

    /// <summary>The address behind the "Write an email" button in Report a problem. While it is empty the button does not appear
    /// (ProblemReport.MailUrl returns null). The public support address, receiving as of 3 November 2026; it replaces Tharun's own, which the soft launch began with.</summary>
    public const string FeedbackEmail = "hello@tha.run";

    public static readonly string[] Providers =
      { ProviderAnthropic, ProviderOpenAI, ProviderGoogle, ProviderOllamaLocal, ProviderOllamaCloud };

    public static bool IsLocalProvider(string provider) => provider == ProviderOllamaLocal;
    public static bool ProviderNeedsKey(string provider) =>
      provider == ProviderAnthropic || provider == ProviderOpenAI || provider == ProviderGoogle || provider == ProviderOllamaCloud;

    public static string ProviderLabel(string provider)
    {
      switch (provider)
      {
        case ProviderAnthropic: return "Anthropic";
        case ProviderOpenAI: return "OpenAI";
        case ProviderGoogle: return "Google";
        case ProviderOllamaLocal: return "Ollama (local)";
        case ProviderOllamaCloud: return "Ollama Cloud";
        default: return provider;
      }
    }

    // --- profiles (the everyday dial, ADR-082): Economy / Balanced / Quality / Ultra. Stored as the
    // "posture" value for back-compat; the smart resolver (ModelResolver) slides the model within the
    // task's tier band by profile. ---------------------------------------------------------------------
    public const string PostureEconomy = "economy";
    public const string PostureBalanced = "balanced";
    public const string PostureQuality = "quality";
    public const string PostureUltra = "ultra";
    public static readonly string[] Postures = { PostureEconomy, PostureBalanced, PostureQuality, PostureUltra };

    // ---- the model table is now the JSON catalogue (ADR-081, ModelCatalogue) - the single source of
    // truth, user-editable without a recompile. These three Anthropic statics remain as compat shims for
    // the legacy Eto SettingsDialog fallback and the DwightSettings defaults; everything else resolves
    // through the catalogue. ---------------------------------------------------------------------------

    // WP-3.5: prices corrected to match the catalogue (models.json is the RUNTIME source of truth, ADR-081;
    // these statics are only a compat shim for the legacy Eto SettingsDialog fallback + the DwightSettings
    // defaults). Haiku 4.5 is $1 / $5 per Mtok.
    public static readonly ModelInfo Haiku45 = new ModelInfo("claude-haiku-4-5", "Claude Haiku 4.5", ProviderAnthropic, "Fast", 1.0, 5.0, 200_000, structured: true, caching: true, vision: true);
    public static readonly ModelInfo Sonnet46 = new ModelInfo("claude-sonnet-4-6", "Claude Sonnet 4.6", ProviderAnthropic, "Strong", 3.0, 15.0, 1_000_000, structured: true, caching: true, vision: true);
    public static readonly ModelInfo Opus48 = new ModelInfo("claude-opus-4-8", "Claude Opus 4.8", ProviderAnthropic, "Top", 5.0, 25.0, 1_000_000, structured: true, reasoning: true, reasoningKnob: true, caching: true, vision: true, imagePatchTokens: true);

    public static readonly IReadOnlyList<ModelInfo> AnthropicModels = new[] { Haiku45, Sonnet46, Opus48 };
    public static readonly ModelInfo DefaultExplainModel = Haiku45;
    public static readonly ModelInfo DefaultReorganiseModel = Sonnet46;

    /// <summary>The models a provider offers (for the advanced-override picker).</summary>
    public static IReadOnlyList<ModelInfo> ModelsForProvider(string provider) => ModelCatalogue.ForProvider(provider);

    /// <summary>Find a model by id across the catalogue (advanced override lookup).</summary>
    public static ModelInfo FindModel(string id) => ModelCatalogue.Find(id);

    // The Phase-5 (provider, posture, tier) table is retired (ADR-082): model choice now flows through the
    // smart resolver (Dwight.Models.ModelResolver) keyed by task + scope + profile, with floors/ceilings.

    /// <summary>Per-task auto-timeout (the kill-switch backstop, so a wedged call never freezes Dwight).
    /// Cloud calls time out at 2.5 min; local has no auto-timeout (a 16 GB box can take minutes to load a
    /// model) and relies on the manual Stop button. Both are always cancellable via Stop / Restart.</summary>
    public const int CloudTimeoutMs = 150_000;
    public const int LocalTimeoutMs = 0;

    /// <summary>The ANSWER budget (ADR-030). The compact plan (ADR-032) and a concise Explain stay well
    /// under this; streaming keeps a big output from tripping the idle timeout. When thinking is on, the
    /// provider seam adds a reserve ON TOP of this rather than carving it out (#216).
    ///
    /// Defined once, in <see cref="Dwight.Llm.ThinkingBudget"/>, because <c>LlmRequest.MaxTokens</c>
    /// defaults to the same number and must not drift from it - which it had, silently, at 4096.</summary>
    public const int MaxTokens = Dwight.Llm.ThinkingBudget.DefaultAnswerBudget;

    /// <summary>Bounded cap on search_components iterations within one Reorganise request (ADR-007/033).</summary>
    public const int MaxToolIterations = 4;

    /// <summary>Fraction of a model's context window one request's input may fill before the graceful
    /// ceiling (ADR-035) - leaves room for the output.</summary>
    public const double InputBudgetFraction = 0.70;

    /// <summary>A generous node ceiling for Reorganise (WP-1.6): the deterministic segment + layout run
    /// off-thread and the LLM only ever sees the bounded cluster summaries, so the real cost is the local
    /// compute. Beyond this Dwight reports "this definition is large" rather than working the canvas for a long
    /// time. Set well above a normal working selection; tune with the eval harness (ADR-066).</summary>
    public const int ReorganiseMaxNodes = 2000;

    /// <summary>WP-2 RR3: the Simplify (better-logic) selection ceiling. Simplify rewrites LOGIC, so it is a far
    /// smaller job than Reorganise - a huge selection both blows the token budget and invites a nonsense whole-
    /// definition rewrite (the 317-component "replace it all with one Panel" the gate caught). Beyond this Dwight
    /// asks for a region rather than proposing. Tune with the eval harness (ADR-066).</summary>
    public const int SimplifyMaxNodes = 60;

    /// <summary>Phase-8 WP-0 (#90/#100): the to-script refine loop's TOTAL generation budget - one initial
    /// generation plus up to two repairs, so 3 in all. It is ONE shared budget across BOTH oracles (the compiler
    /// and, from Stage B, the output-diff): whichever oracle fails feeds its diagnostic back and the model retries,
    /// drawing from this single ceiling - never two nested loops. Pinned at 3 (not #90's loose "initial + 3
    /// repair") because the recorded Phase-7 baseline exists only at 3 total, so a bigger budget would make the
    /// re-measure's baseline-match untestable (#100). Tunable: if the eval shows the compile repairs starving the
    /// output oracle before it can fire, a budget-4 arm is the NAMED escalation (its own experiment), never bundled.
    /// Shared by the product loop (<c>LlmActions.ToScriptAttempt</c>) and the eval (<c>ToScriptEval.Attempt</c>).</summary>
    public const int ToScriptMaxAttempts = 3;

    // ---- Phase-8 WP-3: the agent turn's ceilings (#223). Per TURN, never per session: a conversation
    // may run all afternoon; one answer may not run away. Every ceiling is an honest STOP that keeps
    // what the turn produced, never a discarded turn. Stop is live throughout regardless. --------------

    /// <summary>Steps (model calls) one turn may take. BASIS: a comprehension answer typically needs an
    /// overview plus one to three targeted reads, so four to six steps is the working case; twelve
    /// leaves room for a genuinely multi-region question while bounding what a single message can spend
    /// to twelve billed calls. Tunable - the eval fixes it empirically like every other band (ADR-066).</summary>
    public const int AgentMaxStepsPerTurn = 12;

    /// <summary>Wall clock for one turn. BASIS: the per-CALL cloud timeout is 150s, which is the wrong
    /// instrument for a loop - a legitimate multi-step turn exceeds it while every individual call is
    /// healthy. Five minutes bounds the turn instead, and the per-call timeout still guards each step
    /// inside it.</summary>
    public const int AgentTurnWallClockMs = 300_000;

    /// <summary>The session-level backstop for one turn, deliberately ABOVE
    /// <see cref="AgentTurnWallClockMs"/>. The ordering is the point: the LOOP notices its own ceiling
    /// first and stops honestly, keeping what the turn produced; the session timer only fires if the loop
    /// itself is wedged. Equal values would race, and the race would be won by the cruder reporter.</summary>
    public const int AgentTurnSessionTimeoutMs = AgentTurnWallClockMs + 30_000;

    /// <summary>The transcript one turn may resend. BASIS: the loop resends the whole turn on every
    /// step, so an unbounded transcript grows quadratically and the user pays for every row on every
    /// call (#209). ~120k characters is roughly 30k tokens - comfortably inside the smallest Strong-band
    /// context with room for the system prompt and the tool schemas. Over it, the OLDEST tool results
    /// are replaced by a stated placeholder and the count is reported (#134 - no silent caps).</summary>
    public const int AgentMaxTranscriptChars = 120_000;

    // ---- Phase-9 U1 (reads-unification, #382(9)): the bounds on what get_component reports a component
    // HOLDS. Every bound is REPORTED in the payload where it bites (ADR-035 at the tool surface, #209),
    // never silently applied. Sized against AgentMaxTranscriptChars: one full script read is a fifteenth
    // of a turn's transcript, which is affordable once and ruinous unbounded. ----------------------------

    /// <summary>Lines of a script component's source get_component returns before it cuts and says so.</summary>
    public const int AgentSourceMaxLines = 200;
    /// <summary>Chars of a script component's source get_component returns before it cuts and says so.</summary>
    public const int AgentSourceMaxChars = 8_000;
    /// <summary>Chars of a Panel's text get_component returns before it cuts and says so.</summary>
    public const int AgentPanelTextMaxChars = 400;
    /// <summary>Selected items of a Value List get_component names before it stops at the option count.</summary>
    public const int AgentValueListMaxItems = 10;

    /// <summary>Phase-9 U1(c): internal objects of one cluster the snapshot projects, at most. Beyond it the
    /// cluster reports how many it omitted. A cluster holding thousands is a document of its own.</summary>
    public const int AgentClusterMaxNodes = 200;

    /// <summary>Phase-9 U2: geometry objects any one geometry read returns, at most (the page size). The
    /// value lives beside its parser (<see cref="Dwight.Agent.GeometryArgs.MaxObjects"/>); this alias is
    /// the one the deploy probe reads by content.</summary>
    public const int AgentGeometryMaxObjects = Dwight.Agent.GeometryArgs.MaxObjects;

    /// <summary>
    /// How many times one REGION may be built in a single turn before the plugin refuses (#97/#352).
    ///
    /// BASIS: it is #97's cap, arrived at for the same reason - ADR-094's bound, and the Phase-7 eval
    /// measured real recoveries on attempt 3, so a cap of 2 leaves those on the table. A constant, so the
    /// gate or field data retunes it without an ADR change.
    ///
    /// ENFORCED BY THE PLUGIN, not asked of the model. "You have had three attempts at A1" is a
    /// deterministic fact the plugin can compute, and #141/#146/#195 say a deterministic fact is the
    /// plugin's job rather than a prompt's - a prompt plea is not a guard.
    ///
    /// An ATTEMPT is a build that actually ran. A spec refused before the canvas was touched never
    /// tested anything, so it does not spend the budget; the turn's step ceiling bounds that case.
    /// </summary>
    public const int AgentBuildMaxAttemptsPerRegion = 3;

    // ---- A BUILD TURN'S OWN CEILINGS (WP-4 Stage C, pinned item 5). ---------------------------------
    //
    // Named separately rather than inherited, and #216's finding is exactly why: an inherited budget is
    // one nobody chose. A comprehension turn answers a question; a build turn reads a map, places a
    // region, solves, reads the result, and does it again for every region the user named - a different
    // shape of work, so it gets numbers chosen for that shape.

    /// <summary>Steps (model calls) one BUILD turn may take. BASIS: a single region costs read-the-map +
    /// build + check ≈ 3 steps, and a self-correction adds 2 more; three regions with one correction each
    /// is ~15. Thirty leaves room for a three-region build that needs help without letting one message
    /// spend unboundedly. The per-REGION attempt cap
    /// (<see cref="AgentBuildMaxAttemptsPerRegion"/>) is the tighter, semantically meaningful bound and
    /// fires first wherever a region is named; this is the outer backstop.</summary>
    public const int AgentBuildMaxStepsPerTurn = 30;

    /// <summary>Wall clock for one BUILD turn. BASIS: <c>build_region</c> arms a 120s solve watchdog per
    /// call, so three regions with corrections can legitimately spend several minutes SOLVING alone -
    /// the comprehension turn's five minutes would time out a healthy build. Fifteen minutes bounds it
    /// while leaving every individual solve its own guard. Stop is live throughout regardless.</summary>
    public const int AgentBuildWallClockMs = 900_000;

    /// <summary>The session-level backstop for a BUILD turn, above
    /// <see cref="AgentBuildWallClockMs"/> for the same reason as the comprehension pair: the LOOP must
    /// notice its own ceiling first and stop honestly, keeping what it produced. Equal values would race,
    /// and the race would be won by the cruder reporter.</summary>
    public const int AgentBuildSessionTimeoutMs = AgentBuildWallClockMs + 30_000;

    /// <summary>The ANSWER budget for one step of a BUILD turn. BASIS: a build step's reply is a tool call
    /// plus a sentence, but that call carries an approved script again, and the build runs it only if it
    /// equals the card's text byte for byte (U65). So it needs what the chat turn that raised the card
    /// needed: the same computed number (#1171), or a script could fit its card and not its build.</summary>
    public static int AgentBuildAnswerBudget => Dwight.Agent.ChatAnswerBudget.Turn;

    /// <summary>The ANSWER budget for one step of a chat turn (#216 - the thinking reserve is added on
    /// top, at the provider seam, never carved out of this). BASIS: computed from the script limit, never
    /// typed (#1171): a reply must be able to carry the longest script the parser accepts.</summary>
    public static int AgentTurnAnswerBudget => Dwight.Agent.ChatAnswerBudget.Turn;

    /// <summary>Phase-8 WP-0 (#143): is the PRODUCT's output-diff oracle live for C#? MEASURED OFF. The oracle-on
    /// arm fired 22 times on a 48-row corpus and recovered ZERO (0 built, 0 verified), while costing two extra
    /// billed generations per firing - and F1 already delivers the same best-built draft without it, cheaper and
    /// faster. Never spend a user's money on a measured zero. It goes live again only when the re-measure on a
    /// format-stable script (the #141 using-normalizer + minimal-change retry pin) shows actual recovery.
    /// The EVAL is unaffected - it drives the oracle through its own flag, which is how the re-measure happens.</summary>
    public const bool ToScriptOutputOracleCSharp = false;

    /// <summary>Phase-8 WP-0 (#151/#153 C1): Python's oracle is MEASURED OFF too. Python was the clean discriminator -
    /// no using mechanism, so its retries never broke the build (8 of 8 still compiled, against C#'s 0 of 22) and 7 of
    /// the 8 firings used BOTH retries, i.e. the full budget on a format-stable language. It still recovered ZERO, and
    /// verify came out flat (35.4% on vs 37.5% off). Aggregated with C#, that is 0 recoveries in 30 firings: the
    /// class-only feedback is not actionable AS DELIVERED (hypothesis H2), so the oracle is dormant in the product for
    /// BOTH languages. The EVAL still drives it through its own flag - that is how it earns its way back.</summary>
    public const bool ToScriptOutputOraclePython = false;
  }
}
