using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Text;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Dwight.UI
{
  /// <summary>One face the panel ships: its family, its CSS weight and its WOFF2 bytes.</summary>
  internal sealed class EmbeddedFace
  {
    public string Family { get; set; }
    public string Weight { get; set; }
    public byte[] Woff2 { get; set; }
  }

  /// <summary>
  /// THE FACES A NOTE IS DRAWN IN (Brief DO, item 6, on the architect's ruling): the panel's own, from the bytes the panel ships, so a note reads the same on every machine with nothing to install. GDI cannot read
  /// WOFF2 (handed one it reports success and lists no family), so <see cref="Woff2"/> unpacks them first. Once per session, and Segoe UI and Consolas only where a face cannot be had.
  ///
  /// <para>WHAT GDI DOES WITH THEM, measured. A variable font lists its named instances as families, so Inter's 600 is a real "Inter SemiBold", the panel's own bold, and not a smear. Space Grotesk's default instance is
  /// Light, so that is its family's name, and asking it for bold gets its real bold. Three faces of one file, and some orders of mixed files in one collection, lose a family without a word, so each family has its own
  /// collection and each distinct file goes in once.</para>
  /// </summary>
  internal sealed class NoteFaces
  {
    public const string BodyFamily = "Inter";
    public const string BoldFamily = "Inter SemiBold";
    public const string DisplayFamily = "Space Grotesk Light";
    public const string MonoFamily = "Space Mono";

    private const float BodyPx = 12.5f;
    private const float TagPx = 11.5f;
    private const float CaptionPx = 10.5f;

    /// <summary>Consolas reads small beside the rest, so where it stands in, it is half a pixel up on the panel's caption.</summary>
    private const float ConsolasPx = 11f;

    // A collection, and the memory a font was added from, must outlive every Font made from it. These are for the session, and this is why FromCss is called once.
    private readonly List<PrivateFontCollection> _held = new List<PrivateFontCollection>();

    public Font Body { get; private set; }
    public Font Bold { get; private set; }
    public Font Code { get; private set; }
    public Font Tag { get; private set; }
    public Font Count { get; private set; }

    /// <summary>Every role was found in the embedded faces.</summary>
    public bool Embedded { get; private set; }

    /// <summary>The faces a CSS file's <c>@font-face</c> rules hold. Rules with no data are left out.</summary>
    public static List<EmbeddedFace> Read(string css)
    {
      var faces = new List<EmbeddedFace>();
      foreach (Match rule in Regex.Matches(css ?? "", @"@font-face\s*\{(?<body>[^}]*)\}"))
      {
        var body = rule.Groups["body"].Value;
        var family = Regex.Match(body, @"font-family:\s*'([^']+)'").Groups[1].Value;
        var weight = Regex.Match(body, @"font-weight:\s*([^;]+);").Groups[1].Value.Trim();
        var data = Regex.Match(body, @"base64,([A-Za-z0-9+/=]+)").Groups[1].Value;
        if (family.Length == 0 || data.Length == 0) continue;
        faces.Add(new EmbeddedFace { Family = family, Weight = weight, Woff2 = Convert.FromBase64String(data) });
      }
      return faces;
    }

    /// <summary>The system's faces, each role by name. Never throws: GDI substitutes for a name it does not have.</summary>
    public static NoteFaces System()
    {
      var f = new NoteFaces();
      f.Fill(null, null, null, null);
      return f;
    }

    /// <summary>The panel's faces from their CSS, with the system's standing in for any role that cannot be had. Never throws, because a font is not worth a note.</summary>
    public static NoteFaces FromCss(string css)
    {
      var f = new NoteFaces();
      List<FontFamily> families;
      try { families = f.Load(Read(css)); }
      catch { families = new List<FontFamily>(); }
      FontFamily Find(string name) => families.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
      f.Fill(Find(BodyFamily), Find(BoldFamily), Find(DisplayFamily), Find(MonoFamily));
      return f;
    }

    /// <summary>A family's distinct files. Space Grotesk's three weights are one variable file three times over, which is one font to GDI.</summary>
    public static List<EmbeddedFace> Distinct(IEnumerable<EmbeddedFace> family) =>
      family.GroupBy(x => Convert.ToBase64String(x.Woff2)).Select(g => g.First()).ToList();

    /// <summary>
    /// Does a new collection list the family it was made for? Now and then GDI lists another's instead: Space Grotesk's collection came back naming only Space Mono, on about one load in ten in a busy process and on none of
    /// sixty in a fresh one. The cause was not found, and loading a second time has answered it, so a collection is checked against what it was made for and tried again.
    /// </summary>
    public static bool Holds(IEnumerable<string> listed, string family) =>
      (listed ?? Enumerable.Empty<string>()).Any(name => name != null && name.StartsWith(family, StringComparison.OrdinalIgnoreCase));

    private const int MaxLoads = 3;

    private List<FontFamily> Load(List<EmbeddedFace> faces)
    {
      var all = new List<FontFamily>();
      foreach (var family in faces.GroupBy(x => x.Family))
      {
        var fonts = Distinct(family).Select(face => Woff2.ToSfnt(face.Woff2)).ToList();
        for (var attempt = 0; attempt < MaxLoads; attempt++)
        {
          var collection = new PrivateFontCollection();
          _held.Add(collection);
          foreach (var font in fonts)
          {
            var memory = Marshal.AllocCoTaskMem(font.Length);
            Marshal.Copy(font, 0, memory, font.Length);
            collection.AddMemoryFont(memory, font.Length);
          }
          var names = collection.Families.Select(x => x.Name).ToList();
          if (!Holds(names, family.Key)) continue;
          all.AddRange(collection.Families);
          break;
        }
      }
      return all;
    }

    private static Font Make(FontFamily family, float px, FontStyle style) =>
      family != null && family.IsStyleAvailable(style) ? new Font(family, px, style, GraphicsUnit.Pixel) : null;

    private void Fill(FontFamily body, FontFamily bold, FontFamily display, FontFamily mono)
    {
      Body = Make(body, BodyPx, FontStyle.Regular);
      Bold = Make(bold, BodyPx, FontStyle.Regular) ?? Make(body, BodyPx, FontStyle.Bold);
      Code = Make(mono, CaptionPx, FontStyle.Regular);
      Tag = Make(display, TagPx, FontStyle.Bold);
      Count = Make(mono, CaptionPx, FontStyle.Regular);
      Embedded = Body != null && Bold != null && Code != null && Tag != null;

      Body = Body ?? new Font("Segoe UI", BodyPx, FontStyle.Regular, GraphicsUnit.Pixel);
      Bold = Bold ?? new Font("Segoe UI", BodyPx, FontStyle.Bold, GraphicsUnit.Pixel);
      Code = Code ?? new Font("Consolas", ConsolasPx, FontStyle.Regular, GraphicsUnit.Pixel);
      Tag = Tag ?? new Font("Segoe UI", TagPx, FontStyle.Bold, GraphicsUnit.Pixel);
      Count = Count ?? new Font("Consolas", CaptionPx, FontStyle.Regular, GraphicsUnit.Pixel);
    }
  }
}
