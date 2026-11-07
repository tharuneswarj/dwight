using System;

namespace Dwight.UI
{
  /// <summary>
  /// Chooses the result surface (ADR-048): the WebView panel (the designed HTML in WebView2) when it
  /// can be created, else the Eto custom-draw popup (ADR-045) as the automatic fallback. The actions
  /// only ever see <see cref="IResultPanel"/>, so neither knows which surface rendered it.
  /// </summary>
  internal static class ResultSurface
  {
    public static IResultPanel Show(string title)
    {
      var action = ActionFrom(title);
      try { return WebPanel.Show(action); }
      catch (Exception ex)
      {
        // WebView2 unavailable -> Eto fallback. The reason USED to be swallowed silently, which hid the cause
        // of a missing/degraded panel; log it so a "why did the panel fall back" question is answerable off the
        // command line without the bridge. A WebView2-runtime InvalidOperationException here means H3; anything
        // else (a NullRef, a TypeInitializationException) means a real bug upstream of the WebView.
        Rhino.RhinoApp.WriteLine($"Dwight: result panel fell back to Eto - {ex.GetType().Name}: {ex.Message}");
        return ResultPopup.ShowThinking(title);
      }
    }

    private static string ActionFrom(string title)
    {
      if (string.IsNullOrEmpty(title)) return "Dwight";
      var i = title.IndexOf(" - ", StringComparison.Ordinal);
      return i >= 0 ? title.Substring(i + 3) : title;
    }
  }
}
