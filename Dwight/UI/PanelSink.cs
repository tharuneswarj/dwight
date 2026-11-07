using System;
using Dwight.Session;

namespace Dwight.UI
{
  /// <summary>
  /// The thin shim between the GH-free <see cref="IPanelSink"/> (which <see cref="PanelScript.Play"/> drives and
  /// the headless tests verify) and the real, Eto-coupled <see cref="IResultPanel"/> (WP-1 step 3, ADR-091).
  ///
  /// It exists because <c>IResultPanel</c> lives in a file that imports Eto, so it cannot be link-compiled into
  /// a headless test project. Everything that DECIDES anything - the op whitelist, arg validation, skip
  /// accounting - stays in the core; this type only forwards, so it is correct by inspection.
  /// </summary>
  internal sealed class PanelSink : IPanelSink
  {
    private readonly IResultPanel _p;

    public PanelSink(IResultPanel panel) { _p = panel; }

    public void SetMarkdown(string markdown) => _p.SetMarkdown(markdown);
    public void SetContent(string text) => _p.SetContent(text);
    public void SetStatus(string text) => _p.SetStatus(text);
    public void SetSteps(string stepsJson) => _p.SetSteps(stepsJson);
    /// <summary>Brief EI: only a WebView page draws a deck; the fallback keeps the steps it was given.</summary>
    public void SetDeck(string deckJson) { if (_p is WebPanel w) w.SetDeck(deckJson); }
    public void SetFaults(string faultsJson) => _p.SetFaults(faultsJson);
    public void SetChat(string messagesJson) => _p.SetChat(messagesJson);
    public void SetError(string message) => _p.SetError(message);

    /// <summary>Replays without the action pair on purpose: a recorded button whose handler died with its
    /// session would be a control that does nothing (see <see cref="IPanelSink.ShowState"/>).</summary>
    public void ShowState(string kind, string title, string message) =>
      _p.ShowState(kind, title, message, null, null);

    /// <summary><c>SetOutcome</c> is WebPanel-only, so a non-WebView panel degrades to plain content - which is
    /// exactly what the shipped Reorganise fallback branch already does, not a new behaviour.</summary>
    public void SetOutcome(string text)
    {
      if (_p is WebPanel w) w.SetOutcome(text);
      else _p.SetContent(text);
    }
  }
}
