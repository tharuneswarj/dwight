using System.Linq;
using Dwight.Credentials;
using Dwight.Models;

namespace Dwight.Llm
{
  /// <summary>
  /// The single place that constructs a provider (ADR-066). It is the chokepoint that enforces the
  /// fully-local privacy wedge: <b>OllamaLocal can only ever produce a localhost adapter with no key</b>,
  /// so when the user is in fully-local mode there is no code path that contacts a cloud endpoint. Keys are
  /// loaded from the OS credential store here and handed only to the adapter (which puts them on a header,
  /// never in a payload or log). Ollama Cloud reuses the OpenAI wire shape at its hosted base URL.
  /// </summary>
  internal static class LlmProviders
  {
    private const string OpenAIBase = "https://api.openai.com/v1";
    private const string OllamaLocalBase = "http://localhost:11434/v1";
    private const string OllamaCloudBase = "https://ollama.com/v1";   // Tharun: confirm/replace the Cloud base

    /// <summary>The credential-store target for a provider, or null for the keyless local endpoint.</summary>
    public static string TargetFor(string provider)
    {
      switch (provider)
      {
        case DwightConfig.ProviderAnthropic: return CredentialStore.AnthropicTarget;
        case DwightConfig.ProviderOpenAI: return CredentialStore.OpenAITarget;
        case DwightConfig.ProviderGoogle: return CredentialStore.GoogleTarget;
        case DwightConfig.ProviderOllamaCloud: return CredentialStore.OllamaCloudTarget;
        default: return null; // OllamaLocal
      }
    }

    /// <summary>True when the provider has its key (or needs none, for local).</summary>
    public static bool HasKey(string provider)
    {
      if (!DwightConfig.ProviderNeedsKey(provider)) return true;
      try { return CredentialStore.Exists(TargetFor(provider)); } catch { return false; }
    }

    /// <summary>Build the adapter for a provider + model. Throws <see cref="LlmException"/> if a required
    /// key is missing (the actions pre-check, so this is the defensive backstop).</summary>
    public static ILlmProvider Create(string provider, string modelId)
    {
      switch (provider)
      {
        case DwightConfig.ProviderOpenAI:
          return new OpenAIProvider(OpenAIBase, RequireKey(provider), modelId, ProviderCapability.StrictSchema, useCompletionTokens: true, "OpenAI");

        case DwightConfig.ProviderGoogle:
          return new GoogleProvider(RequireKey(provider), modelId);

        case DwightConfig.ProviderOllamaLocal:
          // The privacy wedge: localhost only, no key, no cloud. Never reachable for any cloud provider.
          return new OpenAIProvider(OllamaLocalBase, null, modelId, ProviderCapability.Format, useCompletionTokens: false, "Ollama (local)",
            reasoningModel: DwightConfig.FindModel(modelId)?.Reasoning == true);

        case DwightConfig.ProviderOllamaCloud:
          return new OpenAIProvider(OllamaCloudBase, RequireKey(provider), modelId, ProviderCapability.PromptOnly, useCompletionTokens: false, "Ollama Cloud",
            reasoningModel: DwightConfig.FindModel(modelId)?.Reasoning == true);

        case DwightConfig.ProviderAnthropic:
        default:
          return new AnthropicProvider(RequireKey(DwightConfig.ProviderAnthropic), modelId);
      }
    }

    /// <summary>
    /// Does Dwight's connection to <paramref name="provider"/> carry tools to the model? Read from the adapter that
    /// <see cref="Create"/> builds for it - each adapter declares its own fact beside the code that makes it true,
    /// and a test holds every declaration to the body that adapter really builds. One case per provider, mirroring
    /// Create, and the default is FALSE: a provider no case names is not handed the agent (fail closed).
    /// </summary>
    public static bool PassesTools(string provider)
    {
      switch (provider)
      {
        case DwightConfig.ProviderAnthropic: return AnthropicRequestBody.PassesTools;
        case DwightConfig.ProviderOpenAI:
        case DwightConfig.ProviderOllamaLocal:
        case DwightConfig.ProviderOllamaCloud: return OpenAIProvider.PassesTools;   // one adapter serves all three
        case DwightConfig.ProviderGoogle: return GoogleProvider.PassesTools;
        default: return false;
      }
    }

    /// <summary>
    /// Why the Scaffold agent may NOT run on <paramref name="model"/> through <paramref name="provider"/>, or null
    /// when it may. The live half of <see cref="AgentEligibility.Check"/>: it reads the catalogue's agentic models
    /// and the adapters' tool facts, so nothing in the sentence is typed by hand (#146).
    /// <paramref name="modelIsPinned"/> is true when the model came from the Advanced pin, so the refusal can name
    /// the pin as the cause.
    /// </summary>
    public static string AgentRefusal(ModelInfo model, string provider, bool modelIsPinned)
    {
      var alternatives = DwightConfig.ModelsForProvider(provider).Where(m => m != null && m.Agentic).ToList();
      var providersThatCan = DwightConfig.Providers.Where(PassesTools).Select(DwightConfig.ProviderLabel).ToList();
      return AgentEligibility.Check(model, PassesTools(provider), DwightConfig.ProviderLabel(provider),
                                    alternatives, providersThatCan, modelIsPinned);
    }

    private static string RequireKey(string provider)
    {
      var key = CredentialStore.Load(TargetFor(provider));
      if (string.IsNullOrEmpty(key))
        throw new LlmException($"No API key stored for {DwightConfig.ProviderLabel(provider)}. Add it in Dwight > Settings.");
      return key;
    }
  }
}
