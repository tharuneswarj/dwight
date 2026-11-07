using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using Dwight.Credentials;
using Dwight.Models;

namespace Dwight.Llm
{
  /// <summary>
  /// The list-models reconciler (ADR-081): the truth-check between the catalogue and what the user's key
  /// can actually call. For a provider, it calls the list endpoint with the user's key and marks each
  /// catalogue model:
  ///   green - returned by the key (callable);
  ///   amber - in the catalogue but not returned by this key, and flagged preview/paid (e.g. a paid-only
  ///           Gemini preview on a free key);
  ///   red   - in the catalogue but the id is absent (a stale or wrong id - the Gemini-404 case, caught at
  ///           setup instead of mid-task).
  /// On an unreachable endpoint or no key, every model stays "unknown" (we never punish offline). All
  /// network failure is swallowed (ADR-029); the key only ever rides a header, never a log.
  /// </summary>
  internal static class ModelReconciler
  {
    private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };

    /// <summary>
    /// Phase-8 WP-2 Stage C (#200): the RAW id list the provider's list endpoint returned on the most
    /// recent reconcile, per provider, sorted.
    ///
    /// Why it is kept rather than discarded. A red model means "the catalogue's id is not in the live
    /// list" - but the reconciler previously threw the list away, leaving the only actionable question
    /// ("then what IS the id?") answerable solely by guessing at a website. The corrected id must come
    /// from the SAME instrument that produced the red (ADR-083's own rule: the reconciler, not the web,
    /// settles what a key can call). Surfaced copyable in Advanced settings; the ids are public model
    /// names, never a key.
    /// </summary>
    public static IReadOnlyList<string> LastListedIds(string provider)
    {
      lock (_listGate)
        return _lastListed.TryGetValue(provider ?? "", out var ids) ? ids : new List<string>();
    }

    private static readonly object _listGate = new object();
    private static readonly Dictionary<string, List<string>> _lastListed =
      new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Reconcile a provider's catalogue models against its key; mutates each model's
    /// <see cref="ModelInfo.Reconcile"/> and returns id -> status.</summary>
    public static Dictionary<string, string> Reconcile(string provider)
    {
      var models = ModelCatalogue.ForProvider(provider);
      var available = ListModelIds(provider);   // null = could not reach / no key -> leave unknown
      var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

      // Keep the raw list for the reconcile view (#200). An unreachable endpoint records nothing rather
      // than an empty list, so "no key" can never read as "the provider offers no models".
      if (available != null)
      {
        var sorted = available.ToList();
        sorted.Sort(StringComparer.Ordinal);
        lock (_listGate) _lastListed[provider ?? ""] = sorted;
      }

      foreach (var m in models)
      {
        string status;
        if (available == null) status = "unknown";
        else if (Matches(m.Id, available)) status = "green";
        else status = (m.Status == "preview" || m.Status == "paid") ? "amber" : "red";
        m.Reconcile = status;
        result[m.Id] = status;
      }
      return result;
    }

    /// <summary>Match a catalogue id against the live list, alias↔dated-snapshot aware. Exact (normalised)
    /// match wins; failing that, a live id of the form <c>&lt;alias&gt;-&lt;snapshot&gt;</c> counts, where the
    /// snapshot is a date (digits/hyphens) or a channel tag (preview/latest/exp). Anthropic's <c>/v1/models</c>
    /// lists Haiku 4.5 as <c>claude-haiku-4-5-20251001</c> while the catalogue (and the inference endpoint) use
    /// the bare alias <c>claude-haiku-4-5</c> - without this, Haiku reconciled red even though it calls fine,
    /// which then starved the resolver's Fast band and pushed a one-component Explain up to Sonnet. Also covers
    /// dated/version siblings on other providers (e.g. Gemini <c>-002</c>). Never matches a sibling variant
    /// (<c>gpt-5.4</c> does not match <c>gpt-5.4-mini</c>): the suffix must be snapshot-shaped, not a word.</summary>
    private static bool Matches(string catId, HashSet<string> live)
    {
      var c = Normalize(catId);
      if (c.Length == 0) return false;
      if (live.Contains(c)) return true;
      var prefix = c + "-";
      foreach (var l in live)
        if (l.Length > prefix.Length && l.StartsWith(prefix, StringComparison.Ordinal) && IsSnapshotSuffix(l.Substring(prefix.Length)))
          return true;
      return false;
    }

    /// <summary>True if a suffix is a model snapshot/version tag (a date like <c>20251001</c> or <c>2025-10-01</c>,
    /// or a channel word) rather than a distinct sibling model (<c>mini</c>, <c>nano</c>, <c>turbo</c>).</summary>
    private static bool IsSnapshotSuffix(string s)
    {
      if (s.Length == 0) return false;
      if (s == "preview" || s == "latest" || s == "exp") return true;
      foreach (var ch in s) if (!char.IsDigit(ch) && ch != '-') return false;
      return true;
    }

    /// <summary>Normalise an id for cross-side matching: lowercase, and drop the tags that differ between
    /// the catalogue and the live list - <c>:latest</c> (a pulled local model lists as <c>name:latest</c>)
    /// and <c>:cloud</c>/<c>-cloud</c> (the catalogue tags cloud ids, but the live cloud /api/tags returns
    /// the bare id). Applied to BOTH sides so e.g. catalogue <c>phi4-mini</c> matches live
    /// <c>phi4-mini:latest</c>, and catalogue <c>deepseek-v4-pro:cloud</c> matches live <c>deepseek-v4-pro</c>.</summary>
    private static string Normalize(string id)
    {
      var s = (id ?? "").Trim().ToLowerInvariant();
      if (s.EndsWith(":latest", StringComparison.Ordinal)) s = s.Substring(0, s.Length - 7);
      else if (s.EndsWith(":cloud", StringComparison.Ordinal) || s.EndsWith("-cloud", StringComparison.Ordinal)) s = s.Substring(0, s.Length - 6);
      return s;
    }

    /// <summary>The set of model ids the key can list, or null on any failure (offline, no key, error).</summary>
    private static HashSet<string> ListModelIds(string provider)
    {
      try
      {
        switch (provider)
        {
          case DwightConfig.ProviderOpenAI:
            return OpenAiShape(Get("https://api.openai.com/v1/models", h => h.Add("Authorization", "Bearer " + Key(provider))));
          case DwightConfig.ProviderOllamaCloud:
            // Ollama Cloud lists via the NATIVE /api/tags (not /v1/models), and returns ids WITHOUT the
            // :cloud suffix; Normalize strips :cloud from the catalogue side so they match (per the docs).
            return OllamaTags(Get("https://ollama.com/api/tags", h => h.Add("Authorization", "Bearer " + Key(provider))));
          case DwightConfig.ProviderGoogle:
            return GeminiShape(Get("https://generativelanguage.googleapis.com/v1beta/models", h => h.Add("x-goog-api-key", Key(provider))));
          case DwightConfig.ProviderAnthropic:
            return OpenAiShape(Get("https://api.anthropic.com/v1/models", h => { h.Add("x-api-key", Key(provider)); h.Add("anthropic-version", "2023-06-01"); }));
          case DwightConfig.ProviderOllamaLocal:
            return OllamaTags(Get("http://localhost:11434/api/tags", h => { }));
          default:
            return null;
        }
      }
      catch { return null; }
    }

    private static string Key(string provider)
    {
      var k = CredentialStore.Load(LlmProviders.TargetFor(provider));
      if (string.IsNullOrEmpty(k) && DwightConfig.ProviderNeedsKey(provider)) throw new InvalidOperationException("no key");
      return k;
    }

    private static JsonNode Get(string url, Action<System.Net.Http.Headers.HttpRequestHeaders> headers)
    {
      using (var msg = new HttpRequestMessage(HttpMethod.Get, url))
      {
        headers(msg.Headers);
        var resp = Http.SendAsync(msg).GetAwaiter().GetResult();
        if (!resp.IsSuccessStatusCode) return null;
        var body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        try { return JsonNode.Parse(body); } catch { return null; }
      }
    }

    /// <summary>OpenAI / Anthropic / Ollama-cloud shape: <c>{ data: [ { id } ] }</c>.</summary>
    private static HashSet<string> OpenAiShape(JsonNode node)
    {
      var arr = node?["data"]?.AsArray();
      if (arr == null) return null;
      return arr.Select(x => Normalize(x?["id"]?.GetValue<string>())).Where(s => s.Length > 0).ToHashSet();
    }

    /// <summary>Gemini shape: <c>{ models: [ { name: "models/gemini-3-flash" } ] }</c> (strip the prefix).</summary>
    private static HashSet<string> GeminiShape(JsonNode node)
    {
      var arr = node?["models"]?.AsArray();
      if (arr == null) return null;
      var set = new HashSet<string>();
      foreach (var x in arr)
      {
        var name = x?["name"]?.GetValue<string>();
        if (string.IsNullOrEmpty(name)) continue;
        var i = name.LastIndexOf('/');
        set.Add(Normalize(i >= 0 ? name.Substring(i + 1) : name));
      }
      return set;
    }

    /// <summary>Ollama local shape: <c>{ models: [ { name: "gpt-oss:20b" } ] }</c>.</summary>
    private static HashSet<string> OllamaTags(JsonNode node)
    {
      var arr = node?["models"]?.AsArray();
      if (arr == null) return null;
      return arr.Select(x => Normalize(x?["name"]?.GetValue<string>())).Where(s => s.Length > 0).ToHashSet();
    }
  }
}
