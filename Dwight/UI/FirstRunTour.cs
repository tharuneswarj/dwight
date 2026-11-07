using System;
using System.Collections.Generic;
using System.Text.Json;
using Eto.Drawing;
using Eto.Forms;
using Dwight.Agent;
using Dwight.Branding;
using Dwight.Config;
using Dwight.Llm;

namespace Dwight.UI
{
  /// <summary>
  /// The first-run tour: a 560 x 392 card on the shared WebView shell - five drawn steps, a headline and one
  /// line each - shown once the first time Dwight loads, and reopenable for ever after from Settings.
  ///
  /// <para><b>It costs nothing and it touches nothing.</b> No model call, no solve, no canvas write,
  /// zero tokens. The only state it changes anywhere in the product is <see cref="DwightSettings.TourSeen"/>.
  /// That is not incidental - the last step says what the chat will and will not do without asking, and a
  /// first-run surface that quietly did something would be the worst possible place to start. Its final
  /// action never runs a verb either: a tour must not be the thing that spends a new user's first money.
  /// "Open Dwight" closes the tour and makes the dock draw attention to itself, and that is all it does.</para>
  ///
  /// <para>Everything factual on the surface is pushed from the plugin (<see cref="DwightVerbs"/>,
  /// <see cref="DwightChord"/>) rather than typed into the shell, so a renamed verb or a changed chord
  /// cannot leave the tour teaching something untrue.</para>
  /// </summary>
  internal sealed class FirstRunTour : Form
  {
    private static FirstRunTour _current;
    private static bool _shownThisSession;

    private readonly WebView _web;
    private readonly List<string> _pending = new List<string>();
    private bool _loaded;

    /// <summary>The startup path: open the tour only if this profile has never seen it. Called last in
    /// the startup sequence and inside its own isolation, so it can never cost another surface (#176).</summary>
    public static void OpenIfFirstRun()
    {
      if (!FirstRunCore.ShouldShowTour(DwightSettings.Current.TourSeen, _shownThisSession)) return;
      Open();
    }

    /// <summary>Open the tour now - the Settings "Show the tour again" path. An explicit request is not
    /// a state change, so this never resets <see cref="DwightSettings.TourSeen"/>.</summary>
    public static void Open()
    {
      try { _current?.Close(); } catch { }
      try
      {
        var t = new FirstRunTour();
        _current = t;
        t.Closed += (s, e) => { if (_current == t) _current = null; };
        // Placement is the shared helper's (WindowPlacer), and it runs three times - before Show so the window
        // never flashes at a default spot, after Show and the native calls, and again when the page has
        // loaded (in the constructor). At STARTUP the first two land inside the same unsettled moment, which
        // is how the tour opened at the bottom left on Tharun's screen while the Settings re-open centred.
        WindowPlacer.PlaceCentred(t, "tour, before show");
        t.Show();
        NativeWindowEffects.ApplyFloating(t);
        NativeWindowEffects.SetGhOwner(t);
        WindowPlacer.PlaceCentred(t, "tour, after show");
        _shownThisSession = true;
      }
      catch
      {
        _current = null;
        // No WebView2 runtime, and the tour has no Eto twin to fall back to (the Settings panel does).
        // A machine without WebView2 today will not have it next launch either, so silence would be the
        // one outcome that is both useless and repeated for ever: print the tour and count it as shown.
        ShowOnCommandLine();
      }
    }

    private FirstRunTour()
    {
      // Probe first, so a missing runtime throws HERE (-> Open's catch -> the command-line tour) rather
      // than leaving a blank rectangle as somebody's first impression of Dwight.
      if (!WebViewSupport.IsWebView2Available())
        throw new InvalidOperationException("WebView2 runtime is not available");

      WindowStyle = WindowStyle.None; ShowInTaskbar = false; Topmost = false; Resizable = false;
      // A card, landscape: 560 x 392 logical points, against the old panel's 520 x 580. WindowPlacer takes the
      // window's ACTUAL size, so nothing about placement depends on this number (#34 made it an input; before that
      // the half-sizes 260 and 290 were constants inside the arithmetic, and only a 520 x 580 window centred).
      ClientSize = new Size(560, 392);
      BackgroundColor = DwightTheme.Current() == "dark" ? Brand.Dark : Brand.White;

      WebViewSupport.ForceWebView2();
      _web = new WebView { BrowserContextMenuEnabled = false };
      WebViewSupport.HardenNavigation(_web);
      _web.DocumentLoaded += (s, e) =>
      {
        _loaded = true;
        // The third placement: the page has loaded, so the message loop has run and startup has settled.
        // Idempotent - if the earlier placements were right this changes nothing - and its Debug line shows
        // whether anything had moved the window since.
        WindowPlacer.PlaceCentred(this, "tour, page loaded");
        foreach (var js in _pending) Exec(js);
        _pending.Clear();
        PushInit();
        MarkSeen();   // shown, therefore seen - see FirstRunCore.ShouldShowTour for why it is here
        // THE KEYS WORK WITHOUT A CLICK (Tharun's sitting, 2026-10-25): ArrowRight and Escape did nothing until he
        // clicked inside the window, because keyboard focus stayed with Grasshopper. The page's keydown listener
        // needs the browser to HOLD focus, so the host gives it on load - the window first, then the WebView -
        // and the page focuses its body (dwightTourFocus). Best-effort: a focus that fails must not cost the tour.
        try { Focus(); } catch { }
        try { _web.Focus(); } catch { }
        Exec("try{ dwightTourFocus(); }catch(e){}");
      };
      _web.MessageReceived += (s, e) => Handle(e.Message);
      _web.DocumentTitleChanged += (s, e) => { var t = e.Title; if (!string.IsNullOrEmpty(t) && t.StartsWith("dwight:", StringComparison.Ordinal)) Handle(t.Substring(7)); };
      Content = _web;

      var html = ShellAssets.Inline(WebViewSupport.LoadResource("Dwight.Tour.html"));
      if (html != null) _web.LoadHtml(html, null);
      KeyDown += (s, e) => { if (e.Key == Keys.Escape) Close(); };

      Action<string> onTheme = t => Run($"dwightSetTheme({Js(t)})");
      DwightTheme.Changed += onTheme;
      Closed += (s, e) => DwightTheme.Changed -= onTheme;
    }

    private void PushInit() =>
      Run($"dwightTourInit({Js(FirstRunCore.InitJson(DwightVerbs.All, DwightChord.Label, NeedsKey(), FirstRunCore.DocsUrl, FirstRunCore.KeysUrl, ConsentPicture()))})");

    /// <summary>What the last step draws of the build consent card: ConsentCard's OWN title and the first sentence
    /// of its OWN authority line for one region, so the picture is the card and cannot drift from it (#315). The
    /// model's reason is not part of it - the shell draws an example of a reason, labelled as the card labels it.
    /// If the card's words cannot be had, the picture is empty rather than invented.</summary>
    private static FirstRunCore.ConsentPicture ConsentPicture()
    {
      try { return new FirstRunCore.ConsentPicture(ConsentCard.BuildAct(new[] { "A1" }), ConsentCard.OneLiner("build"), ConsentCard.MoreLabel); }
      catch { return null; }
    }

    /// <summary>Does the user still need to supply a key before Dwight can answer anything? A plugin-
    /// computed fact, not a question put to the user - the fully-local provider needs none at all.</summary>
    private static bool NeedsKey()
    {
      try
      {
        var provider = DwightSettings.Current.EffectiveProvider;
        if (!DwightConfig.ProviderNeedsKey(provider)) return false;
        return !LlmProviders.HasKey(provider);
      }
      catch { return false; }   // never let a credential-store hiccup turn the tour into a nag
    }

    /// <summary>Record that the tour has been shown. The write can fail (settings are best-effort), and
    /// a silent failure here means the tour returns next launch while this window behaves as though it
    /// never will - so it is reported by name rather than swallowed (#299).</summary>
    private static void MarkSeen()
    {
      _shownThisSession = true;
      try
      {
        var s = DwightSettings.Current;
        if (s.TourSeen) return;               // already recorded; no reason to touch the disk
        s.TourSeen = true;
        if (!s.TrySave())
          Rhino.RhinoApp.WriteLine("Dwight: could not record that the tour has been shown (settings.json is not writable), " +
                                   "so it will open again next time. Dwight > Settings has it under About either way.");
      }
      catch { }
    }

    /// <summary>The tour for a machine with no WebView2. It still counts as shown.</summary>
    private static void ShowOnCommandLine()
    {
      try { Rhino.RhinoApp.WriteLine(FirstRunCore.CommandLineText(DwightVerbs.All, DwightChord.Label, NeedsKey())); }
      catch { }
      MarkSeen();
    }

    private void Handle(string cmd)
    {
      var app = Application.Instance;
      Action act = () =>
      {
        try
        {
          if (cmd == "close") { Close(); return; }
          // "Open Dwight": the tour's last button when a key is already stored. It closes the tour and points at the
          // dock - nothing else. Deliberately NOT the same message as "close": the X and Escape must not point.
          if (cmd == "finish") { Close(); DwightDock.DrawAttention(); return; }
          if (cmd == "settings") { Close(); DwightCanvasPopup.Safe(() => DwightSettingsPanel.OpenAtKey()); return; }   // "Add your API key": where the key goes, with the field open
          if (cmd == "docs" || cmd == "keys") { OpenLink(cmd); return; }   // Documentation; Brief EN's "How to get a key"
        }
        catch { }
      };
      if (app != null) app.AsyncInvoke(act); else act();
    }

    /// <summary>Open the documentation or the keys page. The URL is the plugin's own constant and never arrives
    /// over the bridge - the shell sends a bare "docs" or "keys" and <see cref="FirstRunCore.LinkFor"/> decides
    /// where that goes, so no string from a page can become something this process launches.</summary>
    internal static void OpenLink(string command)
    {
      var url = FirstRunCore.LinkFor(command);
      if (url == null) return;
      try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    private void Run(string js) { var app = Application.Instance; Action a = () => { if (_loaded) Exec(js); else _pending.Add(js); }; if (app != null) app.AsyncInvoke(a); else a(); }
    private void Exec(string js) { try { _web.ExecuteScript(js); } catch { } }
    private static string Js(string s) => JsonSerializer.Serialize(s ?? "");
  }
}
