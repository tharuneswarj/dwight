using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Dwight.Branding;
using Xunit;

namespace Dwight.Session.Tests
{
  /// <summary>
  /// #899 follow-up. <see cref="UiTokens"/> is the C# mirror of dwight-tokens.css - the native surfaces (menus,
  /// glow, toast, the Eto fallback) paint from it - and the two were held in sync by a comment. A comment is a
  /// hand-kept second list, the same kind of thing that let dark mode's aliases drift. Each mirror constant is
  /// now named against the TOKEN it mirrors, and the value it must equal is read from the CSS file, never
  /// restated here.
  ///
  /// What this asserts is the value the token file DECLARES for that token in that theme: the body.dark block
  /// over the base block, var() followed to its end. tools/check-token-aliases.js is the other half - it proves
  /// in a real browser that the file computes what it declares.
  /// </summary>
  public class TokenMirrorTests
  {
    // Which token each mirror constant stands for, and in which theme. Names only - every hex comes from the CSS.
    private static readonly (string Constant, string Token, bool Dark)[] Mirror =
    {
      ("InsetHex",            "--inset",         false),
      ("InsetDarkHex",        "--inset",         true),
      ("ShadowInkDarkHex",    "--shadow-ink",    true),
      ("BodyHex",             "--s11",           false),
      ("BodyDarkHex",         "--s11",           true),
      ("BrandHex",            "--amber-7",         false),
      ("BrandHoverHex",       "--amber-8",       false),
      ("BrandInkHex",         "--accent-ink",    false),
      ("BrandSubHex",         "--amber-3",     false),
      ("BrandTintHex",        "--accent-tint",    false),
      ("BrandDarkHex",        "--amber-7",         true),
      ("BrandSubDarkHex",     "--amber-3",     true),
      ("TealHex",             "--link",          false),
      ("TealInkHex",          "--teal-8", false),
      ("TealDarkHex",         "--link",          true),
      ("TealWireHex",         "--teal-wire",     false),
      ("LavenderHex",         "--thinking",           false),
      ("LavenderDarkHex",     "--thinking",           true),
      ("ErrorHex",            "--err",         false),
      ("WarningHex",          "--warn",       false),
      ("SuccessHex",          "--ok",       false),
      ("SurfaceHex",          "--surface",       false),
      ("SurfaceRaisedHex",    "--raised",     false),
      ("BarHex",              "--bar",           false),
      ("BorderHex",           "--border",        false),
      ("BorderStrongHex",     "--border-strong", false),
      ("TextHex",             "--text",          false),
      ("SubtleHex",           "--subtle",        false),
      ("DisabledHex",         "--faint",         false),
      ("SurfaceDarkHex",      "--surface",       true),
      ("SurfaceRaisedDarkHex","--raised",     true),
      ("BarDarkHex",          "--bar",           true),
      ("BorderDarkHex",       "--border",        true),
      ("BorderStrongDarkHex", "--border-strong", true),
      ("TextDarkHex",         "--text",          true),
      ("SubtleDarkHex",       "--subtle",        true),
      ("DisabledDarkHex",     "--faint",         true),
    };

    [Fact]
    public void Every_mirror_constant_equals_the_token_it_mirrors()
    {
      var css = TokenFile.Load();
      var wrong = Mirror
        .Select(m => (m, mirror: HexConstants()[m.Constant], file: css.Resolve(m.Token, m.Dark)))
        .Where(x => !string.Equals(x.mirror, x.file, StringComparison.OrdinalIgnoreCase))
        .Select(x => $"UiTokens.{x.m.Constant} = {x.mirror}, but dwight-tokens.css gives {x.m.Token} = {x.file} in {(x.m.Dark ? "dark" : "light")}")
        .ToList();

      Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }

    // The mapping cannot go stale in silence: a hex constant added to the mirror without naming its token fails
    // here, and so does a mapping row whose constant has gone.
    [Fact]
    public void Every_hex_constant_in_the_mirror_names_its_token()
    {
      var constants = HexConstants().Keys.OrderBy(k => k).ToList();
      var mapped = Mirror.Select(m => m.Constant).OrderBy(k => k).ToList();

      Assert.True(constants.Count > 0, "no hex constants were found on UiTokens - the reflection read nothing");
      Assert.Equal(mapped.Distinct().Count(), mapped.Count);
      Assert.Equal(constants, mapped);
    }

    // Positive control for the reader: the dark block was found and read, so a theme difference is visible.
    // Without it, a reader that silently ignored body.dark would pass every light constant and every dark
    // constant that happens to equal its light twin.
    [Fact]
    public void The_reader_sees_the_dark_block()
    {
      var css = TokenFile.Load();

      Assert.NotEqual(css.Resolve("--s1", dark: false), css.Resolve("--s1", dark: true));
      Assert.NotEqual(css.Resolve("--amber-7", dark: false), css.Resolve("--amber-7", dark: true));
    }

    // #208: a token the file does not declare is a refusal, never an empty string that two sides could share.
    [Fact]
    public void A_token_the_file_does_not_declare_is_refused()
    {
      var css = TokenFile.Load();

      var e = Assert.Throws<InvalidOperationException>(() => css.Resolve("--no-such-token", dark: true));
      Assert.Contains("--no-such-token", e.Message);
    }

    private static Dictionary<string, string> HexConstants() =>
      typeof(UiTokens).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.IsLiteral && f.FieldType == typeof(string) && f.Name.EndsWith("Hex", StringComparison.Ordinal))
        .ToDictionary(f => f.Name, f => (string)f.GetRawConstantValue());

    /// <summary>The declarations of dwight-tokens.css: the base block (the rule whose selectors include :root)
    /// and the body.dark block.</summary>
    private sealed class TokenFile
    {
      private readonly Dictionary<string, string> _base, _dark;

      private TokenFile(Dictionary<string, string> @base, Dictionary<string, string> dark) { _base = @base; _dark = dark; }

      public static TokenFile Load()
      {
        var css = Regex.Replace(System.IO.File.ReadAllText(PathToTokens()), @"/\*[\s\S]*?\*/", "");
        var @base = Block(css, ":root");
        var dark = Block(css, "body.dark");
        if (@base.Count == 0 || dark.Count == 0)
          throw new InvalidOperationException($"dwight-tokens.css: read {@base.Count} base and {dark.Count} body.dark declarations - the reader no longer matches the file");
        return new TokenFile(@base, dark);
      }

      /// <summary>The value the file declares for <paramref name="token"/> in that theme, var() followed to its end.</summary>
      public string Resolve(string token, bool dark)
      {
        var seen = new HashSet<string>();
        for (var name = token; ;)
        {
          if (!seen.Add(name)) throw new InvalidOperationException($"{token}: var() cycle at {name}");
          if (!((dark && _dark.TryGetValue(name, out var value)) || _base.TryGetValue(name, out value)))
            throw new InvalidOperationException($"{name} is not declared in dwight-tokens.css" + (name == token ? "" : $" (reached from {token})"));
          var next = Regex.Match(value, @"^var\(\s*(--[\w-]+)\s*\)$");
          if (!next.Success) return value;
          name = next.Groups[1].Value;
        }
      }

      private static Dictionary<string, string> Block(string css, string selector)
      {
        var d = new Dictionary<string, string>();
        foreach (Match rule in Regex.Matches(css, @"([^{}]+)\{([^{}]*)\}"))
        {
          if (!rule.Groups[1].Value.Split(',').Any(s => s.Trim() == selector)) continue;
          foreach (Match decl in Regex.Matches(rule.Groups[2].Value, @"(--[\w-]+)\s*:\s*([^;]+);"))
            d[decl.Groups[1].Value] = decl.Groups[2].Value.Trim();
        }
        return d;
      }

      private static string PathToTokens()
      {
        var dir = AppContext.BaseDirectory;
        while (dir != null)
        {
          var candidate = System.IO.Path.Combine(dir, "Dwight", "Resources", "dwight-tokens.css");
          if (System.IO.File.Exists(candidate)) return candidate;
          dir = System.IO.Directory.GetParent(dir)?.FullName;
        }
        throw new System.IO.FileNotFoundException("dwight-tokens.css not found from " + AppContext.BaseDirectory);
      }
    }

    // R11: the words the Eto fallback draws clear the floor on the surfaces it draws them on, in both themes. Worked out from the tokens, because it runs only with no WebView2.
    [Theory]
    [InlineData(UiTokens.BodyHex, UiTokens.SurfaceRaisedHex)]
    [InlineData(UiTokens.TextHex, UiTokens.SurfaceRaisedHex)]
    [InlineData(UiTokens.TextHex, UiTokens.BarHex)]
    [InlineData(UiTokens.BrandInkHex, UiTokens.SurfaceRaisedHex)]
    [InlineData(UiTokens.BodyDarkHex, UiTokens.SurfaceDarkHex)]
    [InlineData(UiTokens.TextDarkHex, UiTokens.SurfaceDarkHex)]
    [InlineData(UiTokens.TextDarkHex, UiTokens.BarDarkHex)]
    [InlineData(UiTokens.BrandDarkHex, UiTokens.SurfaceDarkHex)]
    public void The_fallbacks_words_clear_the_floor(string ink, string ground)
    {
      static double Lum(string h)
      {
        double G(int i) { var v = Convert.ToInt32(h.Substring(i, 2), 16) / 255.0; return v <= .03928 ? v / 12.92 : Math.Pow((v + .055) / 1.055, 2.4); }
        return .2126 * G(1) + .7152 * G(3) + .0722 * G(5);
      }
      double a = Lum(ink), b = Lum(ground);
      var ratio = (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
      Assert.True(ratio >= 4.5, ink + " on " + ground + " is " + ratio.ToString("0.00") + ":1");
    }
  }
}
