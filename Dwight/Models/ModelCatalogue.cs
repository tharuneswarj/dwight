using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace Dwight.Models
{
  /// <summary>
  /// The model catalogue (ADR-081): the single source of truth for model ids, prices, tiers, and
  /// capabilities, as a JSON manifest in three layers resolved in order:
  ///   1. bundled default - embedded in the .gha (works offline / first run);
  ///   2. user-editable copy - <c>%APPDATA%\Dwight\models.json</c> (seeded from the bundled default on first
  ///      run; the user can add/drop/reprice a model or point at a custom endpoint with no recompile);
  ///   3. optional hosted manifest - a static file refreshed on a schedule, ETag-cached, behind a flag
  ///      until its URL is set (the schema is identical, so it never breaks an older plugin).
  /// Unknown JSON fields are ignored (forward-compatible). All access is fail-safe - any read/parse error
  /// falls back to the previous layer, never throws (ADR-029).
  /// </summary>
  internal static class ModelCatalogue
  {
    private static readonly object _gate = new object();
    private static List<ModelInfo> _models;

    /// <summary>Optional hosted manifest. Empty until Tharun sets the URL (Phase 5.5 §6); fetch is wired
    /// behind this flag so the schema/plumbing exist without a live endpoint.</summary>
    public static string HostedUrl = "";

    /// <summary>Brief BY: read the bundled catalogue and never touch the user's copy. The console harness sets it before any read, so a test run
    /// cannot seed, refresh or move a file in %APPDATA%. False in the plugin, which nothing changes.</summary>
    public static bool BundledOnly;

    public static IReadOnlyList<ModelInfo> All
    {
      get { lock (_gate) { return _models ?? (_models = Load()); } }
    }

    public static IReadOnlyList<ModelInfo> ForProvider(string provider) =>
      All.Where(m => string.Equals(m.Provider, provider, StringComparison.OrdinalIgnoreCase)).ToList();

    public static ModelInfo Find(string id) =>
      string.IsNullOrEmpty(id) ? null : All.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Re-read the catalogue from disk (after the user edits <c>models.json</c>, ADR-081).</summary>
    public static void Reload() { lock (_gate) { _models = Load(); } }

    /// <summary>The user-editable copy's path (shown / opened from Advanced settings).</summary>
    public static string UserPath =>
      Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Dwight", "models.json");

    // ---- loading -------------------------------------------------------------------------------------

    private static int _loadedVersion;

    /// <summary>
    /// The <c>catalogueVersion</c> of the catalogue ACTUALLY IN EFFECT - the user copy's when it wins,
    /// the bundled one's otherwise.
    ///
    /// This exists to be PROBED before a runtime gate (#196). The #131 loaded-assembly probe stops a
    /// stale binary from being measured; this is its extension to DATA, promoted after a deploy where
    /// the binary was fresh and the catalogue was months old. A gate that verifies the right code
    /// against the wrong models measures the wrong thing and reports a pass.
    /// </summary>
    public static int LoadedCatalogueVersion { get { var _ = All; lock (_gate) return _loadedVersion; } }

    private static List<ModelInfo> Load()
    {
      var bundled = ReadBundled();
      if (BundledOnly)
      {
        _loadedVersion = CatalogueSeed.Version(bundled);
        return Parse(bundled) ?? new List<ModelInfo>();
      }
      EnsureUserCopy(bundled);                       // seed, or refresh a superseded seed (see below)

      var user = ReadFile(UserPath);
      var fromUser = Parse(user);                    // user copy wins; bundled is the fallback
      if (fromUser != null && fromUser.Count > 0)
      {
        _loadedVersion = CatalogueSeed.Version(user);
        return fromUser;
      }
      _loadedVersion = CatalogueSeed.Version(bundled);
      return Parse(bundled) ?? new List<ModelInfo>();
    }

    /// <summary>
    /// Seed <c>%APPDATA%\Dwight\models.json</c> on first run - and REFRESH it when the build ships a
    /// newer catalogue.
    ///
    /// WHY THE REFRESH EXISTS (found at the Phase-8 WP-2 Stage-B deploy, Tharun's report). The user copy
    /// WINS over the bundled one (that is ADR-081's whole point - edit a price, add a model the day it
    /// ships, no recompile). But the seed was written once and never revisited, so once the file existed
    /// it shadowed the bundled catalogue FOREVER: shipping a new lineup updated the <c>.gha</c> and
    /// changed nothing a user could see. Every install seeded before the refresh would have kept
    /// resolving the old models silently - including the machine the Stage-B thinking capture runs on,
    /// which would then have measured the wrong model family.
    ///
    /// THE POLICY, and why it is not a clobber. <c>catalogueVersion</c> is the bundled content revision.
    /// When the build's is newer than the user copy's, the user copy is PRESERVED ASIDE as a
    /// <c>.bak</c> and the new catalogue is written - never deleted, never silently overwritten. That is
    /// the WP-1 store law (#162/#167: cannot-read is never a licence to destroy; a superseded file is
    /// moved aside, not clobbered) applied to the catalogue. A copy with no <c>catalogueVersion</c> - every
    /// install seeded before this shipped - reads as 0 and is refreshed exactly once.
    ///
    /// The user keeps the last word: edit the refreshed file and it wins again until the next revision.
    /// </summary>
    private static void EnsureUserCopy(string bundled)
    {
      try
      {
        if (bundled == null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(UserPath));

        if (!File.Exists(UserPath)) { File.WriteAllText(UserPath, bundled); return; }   // first run

        var userJson = ReadFile(UserPath);
        if (!CatalogueSeed.ShouldRefresh(bundled, userJson)) return;                    // user copy is current

        PreserveAside(CatalogueSeed.Version(userJson));   // throws rather than overwrite unpreserved
        File.WriteAllText(UserPath, bundled);
      }
      catch { /* a read-only profile just runs off the bundled copy */ }
    }

    /// <summary>Move the superseded user copy aside before it is replaced, so an edited catalogue is
    /// always recoverable. If it cannot be preserved this THROWS, so the caller's catch abandons the
    /// refresh: keeping a stale catalogue is recoverable, destroying the user's edits is not.</summary>
    private static void PreserveAside(int supersededVersion)
    {
      var dir = Path.GetDirectoryName(UserPath);
      var name = CatalogueSeed.BakFileName(supersededVersion, n => File.Exists(Path.Combine(dir, n)));
      File.Copy(UserPath, Path.Combine(dir, name), overwrite: false);
    }

    private static string ReadBundled()
    {
      try
      {
        using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("Dwight.Models.json"))
        {
          if (s == null) return null;
          using (var r = new StreamReader(s)) return r.ReadToEnd();
        }
      }
      catch { return null; }
    }

    private static string ReadFile(string path)
    {
      try { return File.Exists(path) ? File.ReadAllText(path) : null; } catch { return null; }
    }

    private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
    {
      PropertyNameCaseInsensitive = true,
      ReadCommentHandling = JsonCommentHandling.Skip,
      AllowTrailingCommas = true,
    };

    internal static List<ModelInfo> Parse(string json)
    {
      if (string.IsNullOrWhiteSpace(json)) return null;
      try
      {
        var manifest = JsonSerializer.Deserialize<Manifest>(json, JsonOpts);
        if (manifest?.models == null) return null;
        var list = new List<ModelInfo>();
        foreach (var e in manifest.models)
        {
          if (e == null || string.IsNullOrWhiteSpace(e.id) || string.IsNullOrWhiteSpace(e.provider)) continue;
          var c = e.capabilities ?? new Caps();
          list.Add(new ModelInfo(
            e.id, e.displayName ?? e.id, e.provider, string.IsNullOrWhiteSpace(e.tier) ? "Mid" : e.tier,
            e.priceIn, e.priceOut, e.contextWindow,
            structured: c.structuredOutput, reasoning: c.reasoning, reasoningKnob: c.reasoningKnob, caching: c.caching,
            status: string.IsNullOrWhiteSpace(e.status) ? "stable" : e.status,
            pricingModel: string.IsNullOrWhiteSpace(e.pricingModel) ? "perToken" : e.pricingModel,
            minVramGb: e.minVramGb, speed: e.speedNote,
            // #188's two new capability facts. Absent thinkingDefault => "none" (never send a thinking
            // field); absent agentic => false (never silently hand a model the agent loop).
            thinkingDefault: c.thinkingDefault, agentic: c.agentic, preference: e.preference,
            cacheReadPerMTok: e.priceCachedIn, cacheWritePerMTok: e.priceCacheWrite,
            forcedToolChoice: c.forcedToolChoice ?? true, structuredFormat: c.structuredFormat, thinkingBound: c.thinkingBound,
            pinOnly: c.pinOnly, thinkingOffEffort: c.thinkingOffEffort, retentionDays: c.retentionDays, vision: c.vision, imagePatchTokens: c.imagePatchTokens));
        }
        return list;
      }
      catch { return null; }
    }

    // ---- JSON DTOs (flat, forward-compatible: unknown fields are ignored) ----------------------------

    /// <summary><c>catalogueVersion</c> is the bundled CONTENT revision - bump it whenever the shipped
    /// lineup changes, or the update will not reach an existing install (see <see cref="EnsureUserCopy"/>).
    /// Distinct from <c>schemaVersion</c>, which is the FORMAT and moves only on a breaking shape change.</summary>
    private sealed class Manifest { public List<Entry> models { get; set; } }

    private sealed class Entry
    {
      public string id { get; set; }
      public string displayName { get; set; }
      public string provider { get; set; }
      public string tier { get; set; }
      public double priceIn { get; set; }
      public double priceOut { get; set; }
      /// <summary>The vendor's own cached-input and cache-write prices. Absent means the row has none, and the cost falls to the ratio on <see cref="ModelInfo"/>.</summary>
      public double? priceCachedIn { get; set; }
      public double? priceCacheWrite { get; set; }
      public int contextWindow { get; set; }
      public int minVramGb { get; set; }
      /// <summary>Ruling #199: the explicit tie-break when band AND price tie. Lower wins; absent reads
      /// as 100 (neutral), so no entry silently jumps the queue by sitting on an earlier line.</summary>
      public int? preference { get; set; }
      public string status { get; set; }
      public string pricingModel { get; set; }
      public string speedNote { get; set; }
      public Caps capabilities { get; set; }
    }

    private sealed class Caps
    {
      public bool structuredOutput { get; set; }
      public bool reasoning { get; set; }
      public bool reasoningKnob { get; set; }
      public bool caching { get; set; }
      /// <summary>#188/#192: "adaptive-on" | "adaptive-off" | "none". Absent reads as "none", so a
      /// catalogue entry that says nothing never gets a thinking field.</summary>
      public string thinkingDefault { get; set; }
      /// <summary>#188/#189: may this model be handed the agent loop? Absent reads as false.</summary>
      public bool agentic { get; set; }
      /// <summary>Brief BZ: the per-model wire facts, read from the vendor's pages (see ModelInfo). Absent keeps the shape every earlier row was measured on.</summary>
      public bool? forcedToolChoice { get; set; }
      public bool structuredFormat { get; set; }
      public bool thinkingBound { get; set; }
      public bool pinOnly { get; set; }
      public string thinkingOffEffort { get; set; }
      public int retentionDays { get; set; }
      /// <summary>Brief DX: does the vendor say this model takes image input? Absent reads as false.</summary>
      public bool vision { get; set; }
      /// <summary>Brief DX's follow-up: does this row count an image by the measured rule? Absent reads as false.</summary>
      public bool imagePatchTokens { get; set; }
    }
  }
}
