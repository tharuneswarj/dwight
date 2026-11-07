using System;
using Eto.Drawing;
using Eto.Forms;

namespace Dwight.Branding
{
  /// <summary>
  /// The Dwight brand, applied to the Eto (fallback) UI. Phase-10 reskin: the ecosystem AMBER
  /// drawing-set system replaces the v0 lime identity - hexes mirror <see cref="UiTokens"/> (the
  /// C# twin of the CSS token layer) so the WebView shells, the native menus, and this fallback
  /// read as one system. Type roles: Space Grotesk for titles/headers, Inter for body/UI, Space
  /// Mono for code - resolved by family name with graceful fallback to a system font when the
  /// brand font is not installed.
  /// </summary>
  internal static class Brand
  {
    // --- accent (ecosystem amber) ---
    public static readonly Color AccentBright = Color.Parse(UiTokens.BrandDarkHex); // reads well on dark
    public static readonly Color AccentDeep   = Color.Parse(UiTokens.BrandHex);     // for light backgrounds

    // --- semantic / build-status (ecosystem) ---
    public static readonly Color Success = Color.Parse(UiTokens.SuccessHex);
    public static readonly Color Error   = Color.Parse(UiTokens.ErrorHex);
    public static readonly Color Warning = Color.Parse(UiTokens.WarningHex);
    public static readonly Color Info    = Color.Parse(UiTokens.TealHex);

    // --- neutrals (sand) ---
    public static readonly Color Dark     = Color.Parse(UiTokens.SurfaceDarkHex);
    public static readonly Color Black     = Color.Parse(UiTokens.SurfaceRaisedDarkHex);
    public static readonly Color Charcoal = Color.Parse("#2A2A28");
    public static readonly Color White     = Color.Parse("#FFFFFF");
    public static readonly Color Sand      = Color.Parse(UiTokens.BarHex);

    private static bool? _dark;

    /// <summary>True when Windows is in dark mode (default true - dark matches the canvas). What "System" resolves to, and nothing else should read it.</summary>
    public static bool WindowsDark => _dark ?? (_dark = DetectDark()).Value;

    /// <summary>The theme the user chose in Settings, as the shells have it (R11). The fallback was reading Windows alone, so a user who chose Light or Dark was ignored.</summary>
    public static bool IsDark => Dwight.UI.DwightTheme.Current() == "dark";

    // --- resolved tokens (per light/dark) ---
    public static Color Surface     => IsDark ? Dark : White;
    public static Color SurfaceDeep => IsDark ? Black : Sand;
    public static Color OnSurface   => IsDark ? Color.Parse(UiTokens.TextDarkHex) : Color.Parse(UiTokens.TextHex);
    public static Color Subtle      => IsDark ? Color.Parse(UiTokens.SubtleDarkHex) : Color.Parse(UiTokens.SubtleHex);
    public static Color Divider     => IsDark ? Color.Parse(UiTokens.BorderDarkHex) : Color.Parse(UiTokens.BorderHex);
    public static Color Accent      => IsDark ? AccentBright : AccentDeep;
    public static Color CodeBg      => IsDark ? Charcoal : Color.Parse(UiTokens.BarHex);
    public static Color Bar         => IsDark ? Color.Parse(UiTokens.BarDarkHex) : Color.Parse(UiTokens.BarHex);
    public static Color BodyText    => IsDark ? Color.Parse(UiTokens.BodyDarkHex) : Color.Parse(UiTokens.BodyHex);
    /// <summary>Amber for words. Accent is amber for marks, and in light it is too pale to read.</summary>
    public static Color AccentInk   => IsDark ? AccentBright : Color.Parse(UiTokens.BrandInkHex);

    // --- fonts ---
    // Baseline is a clean system font (Segoe UI / Segoe UI Variable - Windows 11's own), which is
    // guaranteed present and reads close to the brand. Bundling the brand fonts (Inter / Space
    // Grotesk / Space Mono) into the .gha and binding them in Eto-on-WPF is a *flagged stretch* (ADR-045),
    // so it is NOT relied on here: BrandFonts stays false until that path is proven. Each role tries an
    // ordered family list and falls back to the system default, so a missing family never throws.
    public static bool BrandFonts = false;

    public static Font Body(float size = 13f, FontStyle style = FontStyle.None) =>
      Make(size, style, BrandFonts ? new[] { "Inter", "Segoe UI Variable Text", "Segoe UI" } : new[] { "Segoe UI Variable Text", "Segoe UI" });

    public static Font Title(float size = 15f, FontStyle style = FontStyle.Bold) =>
      Make(size, style, BrandFonts ? new[] { "Space Grotesk", "Segoe UI Variable Display", "Segoe UI" } : new[] { "Segoe UI Variable Display", "Segoe UI" });

    public static Font Mono(float size = 12f, FontStyle style = FontStyle.None) =>
      Make(size, style, BrandFonts ? new[] { "Space Mono", "Cascadia Mono", "Consolas" } : new[] { "Cascadia Mono", "Consolas" });

    private static Font Make(float size, FontStyle style, string[] families)
    {
      foreach (var f in families)
      {
        try { return new Font(f, size, style); }
        catch { /* try the next family */ }
      }
      try { return SystemFonts.Default(size); }
      catch { return SystemFonts.Default(); }
    }

    /// <summary>Paint a window in the brand surface colour (dark-first). Safe to call on any Eto
    /// <see cref="Panel"/> or <see cref="Window"/>; never throws.</summary>
    public static void ApplySurface(Control control)
    {
      try { if (control != null) control.BackgroundColor = Surface; } catch { }
    }

    private static bool DetectDark()
    {
      try
      {
        using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
          @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
        {
          if (key?.GetValue("AppsUseLightTheme") is int i) return i == 0;
        }
      }
      catch { /* fall through */ }
      return true; // default to dark - matches the Grasshopper canvas
    }
  }
}
