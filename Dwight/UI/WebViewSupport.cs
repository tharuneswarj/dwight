using System;
using System.IO;
using System.Reflection;
using Eto.Forms;

namespace Dwight.UI
{
  /// <summary>Shared WebView wiring (ADR-048): forces Eto's WebView2 (Chromium) handler and loads the
  /// embedded HTML shell. Confirmed available in Rhino 8 (ships the WebView2 SDK; runtime present).</summary>
  internal static class WebViewSupport
  {
    private static bool _forced;

    /// <summary>Register the WebView2 (Chromium) handler so <c>Eto.Forms.WebView</c> uses it instead of
    /// the legacy IE/Trident handler. By reflection, so no compile-time Eto.Wpf dependency; safe if it
    /// fails (we then get the default handler / Eto fallback path).</summary>
    public static void ForceWebView2()
    {
      if (_forced) return;
      _forced = true;
      try
      {
        var t = Type.GetType("Eto.Wpf.Forms.Controls.WebView2Handler, Eto.Wpf");
        if (t != null)
          Eto.Platform.Instance.Add<WebView.IHandler>(() => (WebView.IHandler)Activator.CreateInstance(t));
      }
      catch { /* default handler */ }
    }

    /// <summary>WP-2.2: lock the WebView down to its loaded shell. The shell is loaded via <c>LoadHtml</c>
    /// (an about:/data: origin, allowed); any REAL navigation - a clicked auto-link in model output, say - is
    /// cancelled, and an http/https target is opened in the user's default browser instead of hijacking the
    /// panel. Belt-and-braces with the shells' <c>connect-src 'none'</c> CSP (which already blocks fetch/XHR).</summary>
    public static void HardenNavigation(WebView web)
    {
      if (web == null) return;
      web.DocumentLoading += (s, e) =>
      {
        try
        {
          var scheme = e.Uri?.Scheme?.ToLowerInvariant();
          if (scheme == null || scheme == "about" || scheme == "data") return;   // the shell load itself
          e.Cancel = true;                                                        // block navigating away
          if (scheme == "http" || scheme == "https")
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); } catch { }
        }
        catch { }
      };
    }

    /// <summary>WP-5.6: is the WebView2 runtime actually available? A missing runtime makes WebView2 fail
    /// ASYNC (nothing throws, the shell never loads - a dead blank rectangle), so a panel probes this at
    /// construction and throws when it is false, letting <see cref="ResultSurface"/>'s catch route to the Eto
    /// fallback. Probed by reflection through the Rhino-shipped SDK so there is no compile-time dependency;
    /// cached. Conservative: only a definitive "absent" returns false - an unexpected SDK shape lets WebView try.</summary>
    private static bool? _available;
    public static bool IsWebView2Available()
    {
      if (_available.HasValue) return _available.Value;
      bool ok;
      try
      {
        var t = Type.GetType("Microsoft.Web.WebView2.Core.CoreWebView2Environment, Microsoft.Web.WebView2.Core");
        if (t == null) ok = false;                                   // SDK not loaded -> definitely unavailable
        else
        {
          var m = t.GetMethod("GetAvailableBrowserVersionString");
          if (m == null) ok = true;                                  // unexpected SDK shape - don't block; let it try
          else
          {
            var ver = m.Invoke(null, new object[] { null }) as string;
            ok = !string.IsNullOrEmpty(ver);
          }
        }
      }
      catch (Exception ex)   // GetAvailableBrowserVersionString throws when the runtime is not installed
      {
        Rhino.RhinoApp.WriteLine($"Dwight: WebView2 runtime probe reports unavailable - {ex.GetType().Name}: {ex.Message}");
        ok = false;
      }
      _available = ok;
      if (!ok) Rhino.RhinoApp.WriteLine("Dwight: WebView2 not available -> results use the Eto fallback and the Dwight window will not appear.");
      return ok;
    }

    public static string LoadShell() => ShellAssets.Inline(LoadResource("Dwight.PanelShell.html"));

    public static string LoadResource(string name)
    {
      try
      {
        using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
        {
          if (stream == null) return null;
          using (var reader = new StreamReader(stream)) return reader.ReadToEnd();
        }
      }
      catch { return null; }
    }
  }

  /// <summary>The result-surface contract the actions drive, implemented by both the WebView panel
  /// (ADR-048) and the Eto custom-draw popup (ADR-045 fallback).</summary>
  internal interface IResultPanel
  {
    void SetMarkdown(string markdown);                 // Explain output
    void SetContent(string text);                      // plain summary (Reorganise)
    void SetError(string message);                     // legacy plain error (kept)
    void SetStatus(string text);                       // model/time/cost footer line

    void SetThinking(string label, bool withStages);   // breathing loader (+ Reading/Planning/Applying)
    void SetRunMeta(string text);                      // "Provider · Model" shown under the loader while running
    void SetStage(int activeIndex);                    // 0=Reading, 1=Planning, 2=Applying
    void ShowState(string kind, string title, string message, string actionLabel, string actionCmd,
      string actionLabel2 = null, string actionCmd2 = null); // empty|notice|error; a 2nd action for a two-choice prompt (WP-3 opt-in)
    void AskToContinue(string title, string sentence, Action onContinue, Action onCancel); // Brief CS: Continue or Cancel, then one of the two runs on the UI thread

    void SetSteps(string stepsJson);                   // on-canvas Explain: {"steps":[{text,ids,marks}]}
    void SetFaults(string faultsJson);                 // Diagnose: {"lead":..,"faults":[{title,cls,role,cause,fix,guid}]}
    void SetChat(string messagesJson);                 // Scaffold thread: [{who,html}]
    void AppendChat(string who, string html);          // Scaffold: append one message
  }
}
