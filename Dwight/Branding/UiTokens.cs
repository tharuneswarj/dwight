using System.Drawing;

namespace Dwight.Branding
{
  /// <summary>
  /// The C# mirror of the WebView token layer (<c>Resources/dwight-tokens.css</c>) - one constants
  /// file both rendering families read (Phase-10 reskin), so the native surfaces (menus, glow,
  /// toast, Eto fallback) and the HTML shells can never drift apart. Hexes are duplicated here as
  /// strings (for Eto's <c>Color.Parse</c>) and as <see cref="System.Drawing.Color"/> (for
  /// WinForms/GDI+). Keep in sync with dwight-tokens.css when either changes.
  /// </summary>
  internal static class UiTokens
  {
    // --- brand: ecosystem amber (chrome everywhere) ---
    public const string BrandHex        = "#D4864A";  // amber-7 (light)
    public const string BrandHoverHex   = "#BE6F30";  // amber-8
    public const string BrandInkHex     = "#A0581F";  // amber-9 - accent text on light
    public const string BrandSubHex     = "#FBE3C8";  // amber-3 - hover tint / soft fill
    public const string BrandTintHex    = "#FDF0E2";  // amber-2 - faint tint
    public const string BrandDarkHex    = "#E09458";  // amber on dark surfaces
    public const string BrandSubDarkHex = "#3A2E26";  // hover tint on dark

    // --- accent: teal ---
    public const string TealHex     = "#3B9E8F";
    public const string TealInkHex  = "#2B8578";
    public const string TealDarkHex = "#4FB3A3";
    public const string TealWireHex = "#0A8374";  // Brief DJ: the one teal that clears 3:1 on Grasshopper's light canvas and its dark one

    // --- thinking: lavender (ONLY the working state) ---
    public const string LavenderHex     = "#AE82D9";
    public const string LavenderDarkHex = "#C9A6EC";

    // --- alerts ---
    public const string ErrorHex   = "#D4584A";
    public const string WarningHex = "#E8A23D";
    public const string SuccessHex = "#6B9E4A";

    // --- sand neutrals (the roles native surfaces actually use) ---
    public const string SurfaceHex       = "#F9F9F8";  // s2 - panel body (light)
    public const string SurfaceRaisedHex = "#FDFDFC";  // s1 - menu/button face (light)
    public const string BarHex           = "#F1F0EF";  // s3 - titlebar/inset (light)
    public const string BorderHex        = "#E2E1DE";  // s5
    public const string InsetHex         = "#F1F0EF";  // s3: inline code, on a note as in the panel
    public const string BorderStrongHex  = "#CFCECA";  // s7 - outer borders
    public const string TextHex          = "#21201C";  // s12
    public const string BodyHex          = "#63635E";  // s11
    public const string SubtleHex        = "#6E6E68";  // s10h
    public const string DisabledHex      = "#8D8D86";  // s9

    public const string SurfaceDarkHex       = "#191918";
    public const string SurfaceRaisedDarkHex = "#111110";
    public const string BarDarkHex           = "#222221";
    public const string BorderDarkHex        = "#31312E";
    public const string InsetDarkHex         = "#222221";
    public const string ShadowInkDarkHex     = "#0c0c0b";
    public const string BorderStrongDarkHex  = "#494844";
    public const string TextDarkHex          = "#EEEEEC";
    public const string BodyDarkHex          = "#B5B3AD";
    public const string SubtleDarkHex        = "#8F8D86";
    public const string DisabledDarkHex      = "#6F6D66";

    // --- System.Drawing views (WinForms/GDI+: menus, glow, toast) ---
    public static readonly Color Brand        = H(BrandHex);
    public static readonly Color BrandHover   = H(BrandHoverHex);
    public static readonly Color BrandInk     = H(BrandInkHex);
    public static readonly Color BrandSub     = H(BrandSubHex);
    public static readonly Color BrandTint    = H(BrandTintHex);
    public static readonly Color BrandDark    = H(BrandDarkHex);
    public static readonly Color BrandSubDark = H(BrandSubDarkHex);
    public static readonly Color Teal         = H(TealHex);
    public static readonly Color TealWire     = H(TealWireHex);
    public static readonly Color Lavender     = H(LavenderHex);
    public static readonly Color LavenderDark = H(LavenderDarkHex);
    public static readonly Color Error        = H(ErrorHex);
    public static readonly Color Warning      = H(WarningHex);
    public static readonly Color Success      = H(SuccessHex);

    /// <summary>Theme-resolved roles for the native (WinForms) surfaces.</summary>
    public static Color Surface(bool dark)       => dark ? H(SurfaceDarkHex) : H(SurfaceRaisedHex);
    public static Color Bar(bool dark)           => dark ? H(BarDarkHex) : H(BarHex);
    public static Color Border(bool dark)        => dark ? H(BorderDarkHex) : H(BorderHex);
    public static Color Inset(bool dark)         => dark ? H(InsetDarkHex) : H(InsetHex);
    public static Color ShadowInk(bool dark)     => dark ? H(ShadowInkDarkHex) : H(BorderHex);
    public static Color BorderStrong(bool dark)  => dark ? H(BorderStrongDarkHex) : H(BorderStrongHex);
    public static Color Text(bool dark)          => dark ? H(TextDarkHex) : H(TextHex);
    public static Color Body(bool dark)          => dark ? H(BodyDarkHex) : H(BodyHex);
    public static Color Subtle(bool dark)        => dark ? H(SubtleDarkHex) : H(SubtleHex);
    public static Color Disabled(bool dark)      => dark ? H(DisabledDarkHex) : H(DisabledHex);
    public static Color BrandAccent(bool dark)   => dark ? H(BrandDarkHex) : H(BrandHex);
    public static Color BrandInkOn(bool dark)    => dark ? H(BrandDarkHex) : H(BrandInkHex);
    public static Color HoverTint(bool dark)     => dark ? H(BrandSubDarkHex) : H(BrandSubHex);
    public static Color Thinking(bool dark)      => dark ? H(LavenderDarkHex) : H(LavenderHex);

    private static Color H(string hex) => ColorTranslator.FromHtml(hex);
  }
}
