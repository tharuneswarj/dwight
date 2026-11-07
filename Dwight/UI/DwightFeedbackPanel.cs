using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using Eto.Drawing;
using Eto.Forms;
using Dwight.Branding;
using Dwight.Config;
using Dwight.Feedback;

namespace Dwight.UI
{
  /// <summary>"Report a problem": a small panel on the shared WebView shell, built the way the Settings panel is.
  /// It shows the diagnostic block the plugin computes (read-only, selectable), takes a few words from the user,
  /// and offers a prefilled GitHub issue and, when an address is configured, a prefilled mail. Dwight sends
  /// nothing itself: the browser or mail app opens with the text and the user decides what to send.</summary>
  internal sealed class DwightFeedbackPanel : Form
  {
    private static DwightFeedbackPanel _current;
    private readonly WebView _web;
    private readonly List<string> _pending = new List<string>();
    private bool _loaded;

    public static void Open()
    {
      try { _current?.Close(); } catch { }
      try
      {
        var p = new DwightFeedbackPanel();
        _current = p;
        p.Closed += (s, e) => { if (_current == p) _current = null; };
        WindowPlacer.PlaceCentred(p, "feedback, before show");
        p.Show();
        NativeWindowEffects.ApplyFloating(p);
        NativeWindowEffects.SetGhOwner(p);
        WindowPlacer.PlaceCentred(p, "feedback, after show");
      }
      catch
      {
        // No WebView2: the same words, in the user's browser, with the block but no free text.
        _current = null;
        try
        {
          var block = ProblemReport.Compose(Facts(), "");
          if (CanIssue) Launch(ProblemReport.IssueUrl(DwightConfig.FeedbackRepo, Title, block));
          else { var mail = ProblemReport.MailUrl(DwightConfig.FeedbackEmail, Title, block); if (mail != null) Launch(mail); }
        }
        catch { }
      }
    }

    private const string Title = "Problem report";

    private DwightFeedbackPanel()
    {
      if (!WebViewSupport.IsWebView2Available())
        throw new InvalidOperationException("WebView2 runtime is not available");

      WindowStyle = WindowStyle.None; ShowInTaskbar = false; Topmost = false; Resizable = false;
      ClientSize = new Size(460, 580);   // Brief FN: as it opens, with nothing to scroll at 100, 125 and 150 percent (tools/check-window-fit.js); at 540 its last sentence sat under the footer
      BackgroundColor = DwightTheme.Current() == "dark" ? Brand.Dark : Brand.White;

      WebViewSupport.ForceWebView2();
      _web = new WebView { BrowserContextMenuEnabled = false };
      WebViewSupport.HardenNavigation(_web);
      _web.DocumentLoaded += (s, e) =>
      {
        _loaded = true;
        WindowPlacer.PlaceCentred(this, "feedback, page loaded");
        foreach (var js in _pending) Exec(js);
        _pending.Clear();
        PushInit();
      };
      _web.MessageReceived += (s, e) => Handle(e.Message);
      _web.DocumentTitleChanged += (s, e) => { var t = e.Title; if (!string.IsNullOrEmpty(t) && t.StartsWith("dwight:", StringComparison.Ordinal)) Handle(t.Substring(7)); };
      Content = _web;

      var html = ShellAssets.Inline(WebViewSupport.LoadResource("Dwight.Feedback.html"));
      if (html != null) _web.LoadHtml(html, null);
      KeyDown += (s, e) => { if (e.Key == Keys.Escape) Close(); };

      Action<string> onTheme = t => Run($"dwightSetTheme({Js(t)})");
      DwightTheme.Changed += onTheme;
      Closed += (s, e) => DwightTheme.Changed -= onTheme;
    }

    /// <summary>
    /// Brief ES unit 3: this build in one line, the version and the Release, which About shows and the report sends: one source, so the two agree. Read from
    /// the assembly and its file at the first ask and kept, since the file Rhino loaded does not change under it. A .gha loaded from bytes has no location,
    /// so its file is the one Grasshopper loaded it from.
    /// </summary>
    internal static string ThisBuild => _thisBuild ?? (_thisBuild = BuildLine.Of(typeof(DwightFeedbackPanel).Assembly, GhaFile));
    private static string _thisBuild;

    private static string GhaFile()
    {
      try { foreach (var lib in Grasshopper.Instances.ComponentServer.Libraries) if (lib is DwightInfo) return lib.Location; } catch { }
      return null;
    }

    /// <summary>The facts, read at the moment the panel opens. Names and versions only.</summary>
    internal static ProblemFacts Facts()
    {
      var f = new ProblemFacts { LastErrors = LastErrors.Snapshot(), LastAction = DwightSession.LastAction };
      try { f.DwightVersion = ThisBuild; } catch { }   // Brief ES unit 3: the line About shows
      try { f.RhinoVersion = Rhino.RhinoApp.Version.ToString(); } catch { }
      try { f.GrasshopperVersion = typeof(Grasshopper.Instances).Assembly.GetName().Version?.ToString(); } catch { }
      try { f.OsVersion = Environment.OSVersion.VersionString; } catch { }
      try { f.Provider = DwightConfig.ProviderLabel(DwightSettings.Current.EffectiveProvider); } catch { }
      return f;
    }

    private static bool CanIssue { get { ProblemReport.Doors(DwightConfig.RepoPublic, DwightConfig.FeedbackEmail, out var i, out _); return i; } }
    private static bool CanMail { get { ProblemReport.Doors(DwightConfig.RepoPublic, DwightConfig.FeedbackEmail, out _, out var m); return m; } }

    private void PushInit()
    {
      var init = new JsonObject
      {
        ["block"] = ProblemReport.DiagnosticBlock(Facts()),
        ["canIssue"] = CanIssue,
        ["canMail"] = CanMail,
        ["applied"] = DwightTheme.Current(),
      };
      Run($"dwightFeedbackInit({Js(init.ToJsonString())})");
    }

    private void Handle(string cmd)
    {
      var app = Application.Instance;
      Action act = () =>
      {
        try
        {
          if (cmd == "close") { Close(); return; }
          if (cmd.StartsWith("issue:", StringComparison.Ordinal) && CanIssue)
          {
            Launch(ProblemReport.IssueUrl(DwightConfig.FeedbackRepo, Title, ProblemReport.Compose(Facts(), cmd.Substring(6))));
            return;
          }
          if (cmd.StartsWith("mail:", StringComparison.Ordinal))
          {
            var url = ProblemReport.MailUrl(DwightConfig.FeedbackEmail, Title, ProblemReport.Compose(Facts(), cmd.Substring(5)));
            if (url != null) Launch(url);
            return;
          }
        }
        catch { }
      };
      if (app != null) app.AsyncInvoke(act); else act();
    }

    private static void Launch(string url)
    {
      try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
      catch (Exception ex) { LastErrors.Record("Report a problem could not open the link: " + ex.Message); }
    }

    private void Run(string js) { var app = Application.Instance; Action a = () => { if (_loaded) Exec(js); else _pending.Add(js); }; if (app != null) app.AsyncInvoke(a); else a(); }
    private void Exec(string js) { try { _web.ExecuteScript(js); } catch { } }
    private static string Js(string s) => JsonSerializer.Serialize(s ?? "");
  }
}
