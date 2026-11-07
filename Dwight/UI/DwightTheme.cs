using System;
using Dwight.Branding;

namespace Dwight.UI
{
  /// <summary>One global theme for every Dwight surface (panel, dock, settings, future tabs). Toggling
  /// in any surface saves the preference and broadcasts it so all open surfaces update live (spec §6).</summary>
  internal static class DwightTheme
  {
    /// <summary>Fired with the concrete theme ("light"/"dark") whenever it changes; surfaces subscribe.</summary>
    public static event Action<string> Changed;

    public static void Set(string theme)
    {
      var t = (theme == "light" || theme == "dark") ? theme : "system";
      try { var s = Config.DwightSettings.Current; s.Theme = t; s.Save(); } catch { }
      try { Changed?.Invoke(Resolve(t)); } catch { }
    }

    /// <summary>The concrete theme to apply now: the saved choice, else the OS (ADR-050 / brief light default).</summary>
    public static string Current() => Resolve(Safe());

    private static string Safe() { try { return Config.DwightSettings.Current.Theme; } catch { return "system"; } }
    private static string Resolve(string t) => (t == "light" || t == "dark") ? t : (Brand.WindowsDark ? "dark" : "light");
  }
}
