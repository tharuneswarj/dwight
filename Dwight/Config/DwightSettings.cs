using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dwight.Models;

namespace Dwight.Config
{
  /// <summary>
  /// User-chosen, non-secret settings, persisted as JSON in <c>%APPDATA%\Dwight\settings.json</c>
  /// (mother doc 4.2: profiles live on disk; the API key stays in the credential store, never here).
  /// Provider/model selection lives here (ADR-041). The invocation trigger is no longer a setting -
  /// it is Ctrl+Shift+Right-click only (ADR-047). Loaded once, cached in memory; <see cref="Save"/>
  /// persists changes. All access is fail-safe - a missing or malformed file yields sensible
  /// defaults, never an exception.
  /// </summary>
  internal sealed class DwightSettings
  {
    /// <summary>The chosen provider (ADR-066). OllamaLocal is the fully-local privacy wedge.</summary>
    public string Provider { get; set; } = DwightConfig.ProviderAnthropic;

    /// <summary>Cost-posture - the everyday lever (ADR-057): economy / balanced / quality.</summary>
    public string Posture { get; set; } = DwightConfig.PostureBalanced;

    /// <summary>Advanced escape hatch (off by default): pin one specific model id for every action,
    /// bypassing the tier table. Empty = use provider + posture. (Per-action override is a later refinement.)</summary>
    public string OverrideModelId { get; set; } = "";

    // Vestigial 3b per-action picker ids - retired from the UI (ADR-077); kept so the Eto fallback
    // settings dialog still compiles. The actions no longer read these.
    public string ExplainModelId { get; set; } = DwightConfig.Haiku45.Id;
    public string ReorganiseModelId { get; set; } = DwightConfig.Sonnet46.Id;

    /// <summary>UI theme: "light" (default), "dark", or "system" (follow the OS). Brief Part A revises
    /// ADR-050 - Dwight is a reading tool on the light canvas, so light is the default; dark persists.</summary>
    public string Theme { get; set; } = "light";

    /// <summary>The user's name, for the Scaffold chat (the avatar shows its initial). Optional profile.</summary>
    public string UserName { get; set; } = "";

    /// <summary>True once the first-run tour has been SHOWN (not necessarily read to the end - see
    /// <see cref="Dwight.UI.FirstRunCore.ShouldShowTour"/>). A plain bool, never a version: re-showing a
    /// tour somebody has already dismissed is not a feature. An absent field reads as false, so a user
    /// upgrading from a build that had no tour sees it once, which is the intended reading of "once".
    /// "Show the tour again" in Settings opens it WITHOUT touching this - an explicit request is not a
    /// state change.</summary>
    public bool TourSeen { get; set; }

    /// <summary>How Reorganise displays the long wires that cross between groups: "faint" (default), "hidden",
    /// or "solid" (leave them). Display-only - it never changes what the definition computes.</summary>
    public string ReorganiseWireDisplay { get; set; } = "faint";

    /// <summary>The default Reorganise arrangement (Phase 7 WP-1): "compact" (default, dense forest packing),
    /// "hchain" (one flow band per component), or "grid" (columns by flow depth). The result panel also offers
    /// an in-place switcher; this is only the starting choice for a fresh Reorganise. Layout-only - never
    /// changes what the definition computes.</summary>
    public string ReorganiseArrangement { get; set; } = "compact";

    /// <summary>How the Scaffold chat builds (U63): "components" (default, every step a component), "scripts" (one script per
    /// region where the logic is algorithmic) or "auto" (Dwight chooses). Stated to the model as a plugin fact at turn start and
    /// named on the build card; read through <see cref="Dwight.Agent.BuildStyle.Normalise"/>, so a hand-edited value is the default.</summary>
    public string BuildStyle { get; set; } = Dwight.Agent.BuildStyle.Components;

    /// <summary>The approval mode a NEW Build conversation is opened with (Brief AL): "ask" (default), "scripts_and_deletions" or
    /// "never". A conversation copies it when it is opened and keeps its own from then on, so changing this never changes one that
    /// is already open. Read through <see cref="Dwight.Agent.ApprovalMode.Normalise"/>, so a hand-edited value is the default.</summary>
    public string ApprovalMode { get; set; } = Dwight.Agent.ApprovalMode.Ask;

    /// <summary>Full control (U90): whether a NEW Build conversation is opened with the run_script kind on offer. Off by default. A
    /// conversation copies it when it is opened and keeps its own from then on, so turning it on never changes one that is already open.</summary>
    public bool FullControl { get; set; }

    /// <summary>Whether Dwight's notes are drawn on the canvas (Brief DI). Per machine, on by default; an absent field reads as on. It is about the canvas only: hidden notes still ride every read.</summary>
    public bool ShowNotes { get; set; } = true;

    /// <summary>Brief CN: the steps a chat turn may take. Read through <see cref="Dwight.Agent.ChatSteps.Clamp"/>, so a hand-edited value stays in bounds.</summary>
    public int StepsPerTurn { get; set; } = DwightConfig.AgentMaxStepsPerTurn;

    /// <summary>Brief CS: "Ask above", in cents. A verb whose read would cost more than this asks first; zero never asks.</summary>
    public int AskBeforeRunOverCents { get; set; } = Dwight.Session.LargeRun.DefaultThresholdCents;

    /// <summary>Brief BO: the Settings rail keeps its names open. The user's choice, remembered between sessions.</summary>
    public bool SettingsRailPinned { get; set; }

    /// <summary>Let the model search the web through ITS PROVIDER's own search (U64, read by the web-search work). Off by
    /// default. Dwight itself never fetches a page; with this on, the search query leaves the machine exactly as the prompts do.</summary>
    public bool WebSearchEnabled { get; set; }

    /// <summary>Brief DH: a Walk's step washes the rest of the canvas. On by default; the footer's switch and Settings under Explain both set this, and the next step reads it.</summary>
    public bool WalkDim { get; set; } = true;

    /// <summary>True when running fully local (the privacy badge shows).</summary>
    [JsonIgnore] public bool IsLocal => Provider == DwightConfig.ProviderOllamaLocal;

    /// <summary>The effective provider, validated (unknown values fall back to Anthropic).</summary>
    [JsonIgnore] public string EffectiveProvider =>
      System.Array.IndexOf(DwightConfig.Providers, Provider) >= 0 ? Provider : DwightConfig.ProviderAnthropic;

    /// <summary>Resolve the concrete model + thinking for an action (ADR-082): an advanced model override
    /// wins if set (still structured-gated by the caller), else the smart resolver picks from the task's
    /// tier band sliding by the profile. The single resolution point the actions call.</summary>
    public ModelResolver.Resolved ResolveModel(DwightTask task, TaskScope scope, bool structured)
    {
      if (!string.IsNullOrEmpty(OverrideModelId))
      {
        var o = DwightConfig.FindModel(OverrideModelId);
        // U52: a pin made under another provider is ignored here (a hand-edited or older settings.json can still hold
        // one) - the posture resolver picks for the provider in use. Sending it would be a 404 from the new provider.
        if (ModelPick.PinBelongsTo(o, EffectiveProvider)) return new ModelResolver.Resolved { Model = o, Thinking = false };
      }
      var profile = System.Array.IndexOf(DwightConfig.Postures, Posture) >= 0 ? Posture : DwightConfig.PostureBalanced;
      return ModelResolver.Resolve(task, scope, structured, profile, EffectiveProvider);
    }

    /// <summary>
    /// Brief AO: the chat's model, resolved THREAD FIRST. The thread's pin, then the Settings pin, then the posture - the thread's own posture
    /// when it has one, else the Settings one. <paramref name="threadPinIgnored"/> is true when the thread has a pin that does not belong to
    /// the provider in use; <paramref name="effectivePinId"/> is the pin that applies (thread or Settings), for the front door's "pinned" wording.
    /// </summary>
    public ModelResolver.Resolved ResolveChatModel(string threadModelId, string threadPosture, out bool threadPinIgnored, out string effectivePinId)
    {
      var threadPin = string.IsNullOrEmpty(threadModelId) ? null : DwightConfig.FindModel(threadModelId);
      var settingsPin = string.IsNullOrEmpty(OverrideModelId) ? null : DwightConfig.FindModel(OverrideModelId);
      var pin = Dwight.Agent.ChatHeader.PickPin(threadModelId, threadPin, settingsPin, EffectiveProvider, out threadPinIgnored);
      effectivePinId = pin?.Id ?? "";

      var posture = !string.IsNullOrEmpty(threadPosture) && System.Array.IndexOf(DwightConfig.Postures, threadPosture) >= 0 ? threadPosture : Posture;
      var profile = System.Array.IndexOf(DwightConfig.Postures, posture) >= 0 ? posture : DwightConfig.PostureBalanced;
      if (pin != null)
      {
        var thinks = Dwight.Agent.ChatHeader.PinThinks(pin, EffectiveProvider, profile);
        return new ModelResolver.Resolved { Model = pin, Thinking = thinks, Effort = thinks ? Dwight.Llm.ThinkingWire.EffortForProfile(profile) : null };
      }
      return ModelResolver.Resolve(DwightTask.Scaffold, TaskScope.Any, false, profile, EffectiveProvider);
    }

    /// <summary>Brief CW: what Balanced resolves to for the chat on the provider in force, whatever this chat or Settings has chosen. Every price a person reads is said against it.</summary>
    public ModelInfo BalancedChatModel() =>
      ModelResolver.Resolve(DwightTask.Scaffold, TaskScope.Any, false, DwightConfig.PostureBalanced, EffectiveProvider).Model;

    // ---- persistence ----------------------------------------------------------------

    private static DwightSettings _current;
    private static readonly object _gate = new object();

    private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
    {
      WriteIndented = true,
      DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The live settings, loaded from disk on first use.</summary>
    public static DwightSettings Current
    {
      get
      {
        lock (_gate)
        {
          if (_current == null) _current = Load();
          return _current;
        }
      }
    }

    private static string FilePath =>
      Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Dwight", "settings.json");

    private static DwightSettings Load()
    {
      try
      {
        var path = FilePath;
        if (!File.Exists(path)) return new DwightSettings();
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<DwightSettings>(json, JsonOpts) ?? new DwightSettings();
      }
      catch
      {
        return new DwightSettings(); // fail-safe: defaults on any read/parse error
      }
    }

    /// <summary>Persist the current settings. Best-effort - never throws.</summary>
    public void Save() { TrySave(); }

    /// <summary>Persist, and SAY whether it worked. Identical to <see cref="Save"/> except that the
    /// caller can tell - which matters for the one setting whose whole job is not to happen twice
    /// (<see cref="TourSeen"/>): a silent-failing write there means the tour returns next launch while
    /// the surface behaves as though it never will. A write that can fail must say so (#299).</summary>
    public bool TrySave()
    {
      try
      {
        var path = FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOpts));
        lock (_gate) { _current = this; }
        return true;
      }
      catch
      {
        // Settings are a convenience; failing to persist must never break an action.
        return false;
      }
    }
  }
}
