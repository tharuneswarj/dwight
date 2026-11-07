using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Eto.Drawing;
using Eto.Forms;
using Dwight.Branding;
using Dwight.Config;
using Dwight.Credentials;
using Dwight.Llm;
using Dwight.Models;

namespace Dwight.UI
{
  /// <summary>The Settings surface on the shared WebView shell (ADR-041; spec §8): provider + per-action
  /// model picker with an honest cost/speed estimate, the global theme toggle, and API-key management
  /// (the key goes to the OS credential store via <see cref="ApiKeyDialog"/>, never the settings file).
  /// Replaces the Eto SettingsDialog (kept as a fallback).</summary>
  internal sealed class DwightSettingsPanel : Form
  {
    private static DwightSettingsPanel _current;
    private readonly WebView _web;
    private readonly List<string> _pending = new List<string>();
    private bool _loaded;
    private bool _usageOnLoad;
    private bool _keyOnLoad;

    /// <summary>Brief BI: Settings, opened at Usage with its totals read. What a click on the chat's cost does.</summary>
    public static void OpenAtUsage()
    {
      Open();
      try
      {
        // A window just opened asks when it has loaded; the shell's own Show opens the section, which asks the plugin for the totals.
        if (_current != null) { if (_current._loaded) _current.Run("dwightOpenUsage()"); else _current._usageOnLoad = true; }
      }
      catch { }
    }

    /// <summary>Brief BO: Settings, opened on Connection with the key field showing. What the tour's "Add your API key" does: it is what that user came for.</summary>
    public static void OpenAtKey()
    {
      Open();
      try
      {
        if (_current != null) { if (_current._loaded) _current.Run("dwightOpenKey()"); else _current._keyOnLoad = true; }
      }
      catch { }
    }

    public static void Open()
    {
      try { _current?.Close(); } catch { }
      try
      {
        var p = new DwightSettingsPanel();
        _current = p;
        p.Closed += (s, e) => { if (_current == p) _current = null; };
        // The shared placement (WindowPlacer), called exactly as the tour calls it: the two surfaces had the
        // identical centre-on-PrimaryScreen line with constant half-sizes, so the fix is to the class (#877).
        WindowPlacer.PlaceCentred(p, "settings, before show");
        p.Show();
        NativeWindowEffects.ApplyFloating(p);
        NativeWindowEffects.SetGhOwner(p);
        WindowPlacer.PlaceCentred(p, "settings, after show");
      }
      catch { _current = null; try { SettingsDialog.Run(); } catch { } }   // WP-5.6: WebView2 unavailable -> Eto settings dialog
    }

    private DwightSettingsPanel()
    {
      // WP-5.6: probe WebView2 first so a missing runtime throws here (-> Open's catch -> Eto SettingsDialog),
      // rather than an async failure leaving a dead blank panel.
      if (!WebViewSupport.IsWebView2Available())
        throw new InvalidOperationException("WebView2 runtime is not available");

      WindowStyle = WindowStyle.None; ShowInTaskbar = false; Topmost = false; Resizable = false;
      ClientSize = new Size(460, 540);
      BackgroundColor = DwightTheme.Current() == "dark" ? Brand.Dark : Brand.White;

      WebViewSupport.ForceWebView2();
      _web = new WebView { BrowserContextMenuEnabled = false };
      WebViewSupport.HardenNavigation(_web);   // WP-2.2: block external navigation
      _web.DocumentLoaded += (s, e) =>
      {
        _loaded = true;
        WindowPlacer.PlaceCentred(this, "settings, page loaded");   // the settled moment - see WindowPlacer
        foreach (var js in _pending) Exec(js);
        _pending.Clear();
        PushInit();
      };
      _web.MessageReceived += (s, e) => Handle(e.Message);
      _web.DocumentTitleChanged += (s, e) => { var t = e.Title; if (!string.IsNullOrEmpty(t) && t.StartsWith("dwight:", StringComparison.Ordinal)) Handle(t.Substring(7)); };
      Content = _web;

      var html = ShellAssets.Inline(WebViewSupport.LoadResource("Dwight.Settings.html"));
      if (html != null) _web.LoadHtml(html, null);
      KeyDown += (s, e) => { if (e.Key == Keys.Escape) Close(); };

      Action<string> onTheme = t => Run($"dwightSetTheme({Js(t)})");
      DwightTheme.Changed += onTheme;
      Closed += (s, e) => DwightTheme.Changed -= onTheme;
      Action onNotes = () => Run($"dwightShowNotes({(CanvasNotes.Shown ? "true" : "false")})");   // the chord or the dock moved it while this was open
      CanvasNotes.ShownChanged += onNotes;
      Closed += (s, e) => CanvasNotes.ShownChanged -= onNotes;
    }

    private void PushInit()
    {
      var s = DwightSettings.Current;

      var providers = new JsonArray();
      foreach (var p in DwightConfig.Providers)
        providers.Add(new JsonObject { ["id"] = p, ["name"] = DwightConfig.ProviderLabel(p), ["local"] = DwightConfig.IsLocalProvider(p), ["needsKey"] = DwightConfig.ProviderNeedsKey(p) });

      var postures = new JsonArray
      {
        new JsonObject { ["id"] = DwightConfig.PostureEconomy, ["name"] = "Economy" },
        new JsonObject { ["id"] = DwightConfig.PostureBalanced, ["name"] = "Balanced" },
        new JsonObject { ["id"] = DwightConfig.PostureQuality, ["name"] = "Quality" },
        new JsonObject { ["id"] = DwightConfig.PostureUltra, ["name"] = "Ultra" },
      };

      // Advanced override = a model pinned for every action (off = use posture).
      var overrides = new JsonArray { new JsonObject { ["id"] = "", ["name"] = "Off (use the profile)" } };
      var balanced = s.BalancedChatModel();
      foreach (var m in DwightConfig.ModelsForProvider(s.EffectiveProvider))
        overrides.Add(new JsonObject { ["id"] = m.Id, ["name"] = m.DisplayName + (m.IsLocal ? " · free" : m.IsSubscription ? " · " + Dwight.Session.SpendMath.OllamaCloudCostPick : $" · ~{Cents(m):0.0}¢"), ["sentence"] = Dwight.Agent.ChatHeader.RowTitle(m, balanced) });

      var init = new JsonObject
      {
        ["providers"] = providers,
        ["provider"] = s.EffectiveProvider,
        ["postures"] = postures,
        ["posture"] = s.Posture,
        ["postureLine"] = PostureLine(s),
        ["overrides"] = overrides,
        ["override"] = s.OverrideModelId ?? "",
        ["models"] = ModelsView(s.EffectiveProvider),
        ["theme"] = s.Theme,
        ["applied"] = DwightTheme.Current(),
        ["isLocal"] = s.IsLocal,
        ["needsKey"] = DwightConfig.ProviderNeedsKey(s.EffectiveProvider),
        ["hasKey"] = HasKey(s.EffectiveProvider),
        ["name"] = s.UserName,
        ["wires"] = s.ReorganiseWireDisplay,
        ["arrangement"] = s.ReorganiseArrangement,
        ["buildStyle"] = Dwight.Agent.BuildStyle.Normalise(s.BuildStyle),
        ["approvalMode"] = Dwight.Agent.ApprovalMode.Normalise(s.ApprovalMode),
        ["fullControl"] = s.FullControl,
        ["showNotes"] = s.ShowNotes,
        ["stepsPerTurn"] = Dwight.Agent.ChatSteps.Clamp(s.StepsPerTurn),
        ["askOverCents"] = Math.Max(0, s.AskBeforeRunOverCents),
        ["railPinned"] = s.SettingsRailPinned,
        ["webSearch"] = s.WebSearchEnabled,
        ["walkDim"] = s.WalkDim,
        ["localBadge"] = Dwight.Agent.LocalBadge.Text(s.IsLocal, s.WebSearchEnabled),
        ["modelsHeading"] = Dwight.Agent.LocalBadge.ModelsHeading(s.IsLocal),   // Brief FN: follows the provider, sent again with every init a change of provider pushes
        // Empty until the documentation page exists, and the Help button stays hidden until it does.
        ["docs"] = FirstRunCore.DocsUrl,
        // Brief EN: the hint under the key input shows only when the keys page is offered; the click sends a bare "keys".
        ["keys"] = FirstRunCore.KeysUrl,
        // Brief ES unit 3: About's build line, the one Report a problem sends.
        ["build"] = DwightFeedbackPanel.ThisBuild,
      };
      Run($"dwightSettingsInit({Js(init.ToJsonString())})");
      // Phase 9: the "What Dwight sends" block renders from the payload contract, the one source both the
      // in-product statement and the public page draw on - held to the code by PayloadContractTests.
      Run($"dwightSetSends({Js(Dwight.Data.PayloadContract.SettingsHtml())})");
      if (_usageOnLoad) { _usageOnLoad = false; Run("dwightOpenUsage()"); }
      if (_keyOnLoad) { _keyOnLoad = false; Run("dwightOpenKey()"); }
      // Brief BC: Usage is closed by default and reads the ledger when it is opened (usage:open), not when Settings opens.
    }

    /// <summary>Brief AK: the Usage section, read from the central ledger off the UI thread (the reader streams,
    /// but a long ledger is still a file read the panel must not wait on).</summary>
    private void PushUsage()
    {
      Task.Run(() =>
      {
        try
        {
          var path = Dwight.Session.SpendLedger.LedgerPath;
          long? bytes = null;
          try { var fi = new System.IO.FileInfo(path); if (fi.Exists) bytes = fi.Length; } catch { }
          var sum = Dwight.Session.SpendLedgerCore.Summarise(Dwight.Session.SpendLedgerCore.ReadLines(path), DateTime.Now);
          // Brief FN: a model's row is named as the catalogue names it; an id it no longer holds is named by its id.
          Run($"dwightUsage({Js(Dwight.Session.SpendLedgerCore.UsageJson(sum, bytes, id => DwightConfig.FindModel(id)?.DisplayName))})");
        }
        catch { }
      });
    }

    private void ExportUsage()
    {
      using (var dlg = new SaveFileDialog { Title = "Export your spend", FileName = "dwight-spend.csv" })
      {
        dlg.Filters.Add(new FileFilter("CSV", ".csv"));
        if (dlg.ShowDialog(this) != DialogResult.Ok || string.IsNullOrEmpty(dlg.FileName)) return;
        try
        {
          var rows = Dwight.Session.SpendLedgerCore.ExportCsv(Dwight.Session.SpendLedger.LedgerPath, dlg.FileName);
          Run($"dwightSetStatus({Js(Dwight.Session.SpendLedgerCore.ExportedLine(rows))})");
        }
        catch { Run($"dwightSetStatus({Js("Could not write the export.")})"); }
      }
    }

    /// <summary>Open the folder that holds spend.jsonl (created if it is missing, so the click always lands).</summary>
    internal static void OpenLedgerFolder()
    {
      try
      {
        var dir = System.IO.Path.GetDirectoryName(Dwight.Session.SpendLedger.LedgerPath);
        System.IO.Directory.CreateDirectory(dir);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dir) { UseShellExecute = true });
      }
      catch { }
    }

    private static bool HasKey(string provider) { try { return LlmProviders.HasKey(provider); } catch { return false; } }
    private static double Cents(ModelInfo m) => (4000 / 1_000_000.0 * m.InputPerMTok + 500 / 1_000_000.0 * m.OutputPerMTok) * 100.0;

    /// <summary>Brief CW: the line under the Profile control, from the function the chat's picker uses, for the provider in force.</summary>
    private static string PostureLine(DwightSettings s)
    {
      var resolved = s.ResolveChatModel("", s.Posture, out _, out var pinId);
      return Dwight.Agent.ChatHeader.SettingsLine(s.Posture, resolved?.Model, s.BalancedChatModel(), !string.IsNullOrEmpty(pinId));
    }

    /// <summary>The provider's catalogue models for the Advanced reconcile view: name, tier, and the
    /// current green/amber/red/unknown status (ADR-081).</summary>
    private static JsonArray ModelsView(string provider)
    {
      var arr = new JsonArray();
      foreach (var m in DwightConfig.ModelsForProvider(provider))
        arr.Add(new JsonObject { ["name"] = m.DisplayName, ["tier"] = m.Tier, ["status"] = m.Reconcile });
      return arr;
    }

    private void Handle(string cmd)
    {
      var app = Application.Instance;
      Action act = () =>
      {
        try
        {
          if (cmd == "close") { Close(); return; }
          // Help. The tour is centred like this panel, so this window steps aside rather than sitting
          // underneath it. Reopening the tour deliberately does NOT reset TourSeen - asking to see it
          // again is a request, not a change of state.
          if (cmd == "tour") { Close(); FirstRunTour.Open(); return; }
          if (cmd == "docs" || cmd == "keys") { FirstRunTour.OpenLink(cmd); return; }   // Documentation; Brief EN's hint under the key input
          if (cmd == "usage:open") { PushUsage(); return; }   // Brief BC: the totals load when the section is opened
          if (cmd == "usage:export") { ExportUsage(); return; }
          if (cmd == "usage:folder") { OpenLedgerFolder(); return; }
          if (cmd.StartsWith("theme:", StringComparison.Ordinal)) { DwightTheme.Set(cmd.Substring(6)); return; }
          if (cmd.StartsWith("name:", StringComparison.Ordinal)) { var st = DwightSettings.Current; st.UserName = cmd.Substring(5); st.Save(); return; }
          if (cmd.StartsWith("wires:", StringComparison.Ordinal))
          {
            var v = cmd.Substring(6);
            if (v == "faint" || v == "hidden" || v == "solid") { var st = DwightSettings.Current; st.ReorganiseWireDisplay = v; st.Save(); }
            return;
          }
          if (cmd.StartsWith("arrangement:", StringComparison.Ordinal))
          {
            var v = cmd.Substring(12);
            if (v == "compact" || v == "hchain" || v == "vertical" || v == "grid") { var st = DwightSettings.Current; st.ReorganiseArrangement = v; st.Save(); }
            return;
          }

          if (cmd.StartsWith("buildstyle:", StringComparison.Ordinal))
          {
            var v = cmd.Substring(11);
            if (Dwight.Agent.BuildStyle.IsValid(v)) { var st = DwightSettings.Current; st.BuildStyle = v; st.Save(); }
            return;
          }
          if (cmd.StartsWith("railpin:", StringComparison.Ordinal))
          {
            var v = cmd.Substring(8);
            if (v == "on" || v == "off") { var st = DwightSettings.Current; st.SettingsRailPinned = v == "on"; st.Save(); }
            return;
          }
          if (cmd.StartsWith("shownotes:", StringComparison.Ordinal))
          {
            var v = cmd.Substring(10);
            if (v == "on" || v == "off") CanvasNotes.SetShown(v == "on");
            return;
          }
          if (cmd.StartsWith("fullcontrol:", StringComparison.Ordinal))
          {
            var v = cmd.Substring(12);
            if (v == "on" || v == "off") { var st = DwightSettings.Current; st.FullControl = v == "on"; st.Save(); }
            return;
          }
          if (cmd.StartsWith("approval:", StringComparison.Ordinal))
          {
            var v = cmd.Substring(9);
            if (Dwight.Agent.ApprovalMode.IsValid(v)) { var st = DwightSettings.Current; st.ApprovalMode = v; st.Save(); }
            return;
          }
          if (cmd.StartsWith("askover:", StringComparison.Ordinal))
          {
            if (int.TryParse(cmd.Substring(8), out var askOver)) { var st = DwightSettings.Current; st.AskBeforeRunOverCents = Math.Max(0, askOver); st.Save(); }
            return;
          }
          if (cmd.StartsWith("steps:", StringComparison.Ordinal))
          {
            if (int.TryParse(cmd.Substring(6), out var steps)) { var st = DwightSettings.Current; st.StepsPerTurn = Dwight.Agent.ChatSteps.Clamp(steps); st.Save(); }
            return;
          }
          if (cmd.StartsWith("walkdim:", StringComparison.Ordinal))
          {
            var v = cmd.Substring(8);
            if (v == "on" || v == "off") WalkSession.SetDim(v == "on");   // Brief EC unit 2: saved, and a Walk on a step takes it at once, as the panel's and the bar's switches do
            return;
          }
          if (cmd.StartsWith("websearch:", StringComparison.Ordinal))
          {
            var v = cmd.Substring(10);
            if (v == "on" || v == "off") { var st = DwightSettings.Current; st.WebSearchEnabled = v == "on"; st.Save(); PushInit(); }   // the privacy badge reads this setting
            return;
          }

          if (cmd.StartsWith("provider:", StringComparison.Ordinal))
          {
            var p = cmd.Substring(9);
            string cleared = null;
            if (Array.IndexOf(DwightConfig.Providers, p) >= 0)
            {
              var st = DwightSettings.Current;
              st.Provider = p;
              // U52: the pin is one id for every provider. One that belongs to the old provider would 404 on the new
              // one, so it is cleared here and the panel says so.
              var pin = string.IsNullOrEmpty(st.OverrideModelId) ? null : DwightConfig.FindModel(st.OverrideModelId);
              if (pin != null && !ModelPick.PinBelongsTo(pin, p))
              {
                st.OverrideModelId = "";
                cleared = ModelPick.PinClearedMessage(pin);
              }
              st.Save();
            }
            PushInit();   // refresh override list + key status for the new provider
            if (cleared != null) Run($"dwightSetStatus({Js(cleared)})");
            return;
          }
          if (cmd.StartsWith("posture:", StringComparison.Ordinal))
          {
            var p = cmd.Substring(8);
            if (Array.IndexOf(DwightConfig.Postures, p) >= 0) { var st = DwightSettings.Current; st.Posture = p; st.Save(); Run($"dwightSetStatus({Js("Saved")})"); Run($"dwightPostureLine({Js(PostureLine(st))})"); }
            return;
          }
          if (cmd.StartsWith("override:", StringComparison.Ordinal))
          {
            var id = cmd.Substring(9);
            var st = DwightSettings.Current; st.OverrideModelId = (id == "" || DwightConfig.FindModel(id) != null) ? id : st.OverrideModelId; st.Save();
            Run($"dwightSetStatus({Js("Saved")})");
            Run($"dwightPostureLine({Js(PostureLine(st))})");
            return;
          }
          if (cmd.StartsWith("setkey:", StringComparison.Ordinal))
          {
            // Inline key entry (ADR-048/011): the key arrived over the in-process WebView bridge; write it
            // straight to the OS credential store and never hold, log, or echo it.
            var val = cmd.Substring(7).Trim();
            var target = LlmProviders.TargetFor(DwightSettings.Current.EffectiveProvider);
            if (target != null && val.Length > 0) { try { CredentialStore.Save(target, val); } catch { } }
            RefreshKey();
            Run($"dwightSetStatus({Js("Key saved")})");
            return;
          }
          if (cmd == "removekey")
          {
            var target = LlmProviders.TargetFor(DwightSettings.Current.EffectiveProvider);
            if (target != null) { try { CredentialStore.Delete(target); } catch { } }
            RefreshKey();
            return;
          }
          if (cmd == "reconcile")
          {
            // list-models truth-check against the user's key (ADR-081), off the UI thread.
            var prov = DwightSettings.Current.EffectiveProvider;
            Run($"dwightSetStatus({Js("Checking models…")})");
            Task.Run(() =>
            {
              try { ModelReconciler.Reconcile(prov); } catch { }
              Run($"dwightReconcile({Js(ModelsView(prov).ToJsonString())})");
              Run($"dwightSetStatus({Js("Checked")})");
            });
            return;
          }
          if (cmd == "openmodels")
          {
            try { var _ = ModelCatalogue.All; } catch { }   // ensure the user copy is seeded
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ModelCatalogue.UserPath) { UseShellExecute = true }); } catch { }
            return;
          }
        }
        catch { }
      };
      if (app != null) app.AsyncInvoke(act); else act();
    }

    private void RefreshKey()
    {
      var prov = DwightSettings.Current.EffectiveProvider;
      Run($"dwightKey({(HasKey(prov) ? "true" : "false")}, {(DwightConfig.ProviderNeedsKey(prov) ? "true" : "false")})");
    }
    private void Run(string js) { var app = Application.Instance; Action a = () => { if (_loaded) Exec(js); else _pending.Add(js); }; if (app != null) app.AsyncInvoke(a); else a(); }
    private void Exec(string js) { try { _web.ExecuteScript(js); } catch { } }
    private static string Js(string s) => JsonSerializer.Serialize(s ?? "");
  }
}
