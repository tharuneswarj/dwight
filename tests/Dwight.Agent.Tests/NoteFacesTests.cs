using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Dwight.Session;
using Dwight.UI;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// Brief DO, PR 1b: a note's card draws in the panel's own faces, from the bytes the panel ships. The decoder is held on the six real files, each table checked as a font's own reader would check it, and its outlines
  /// against the font's own stated cap height, which a mis-decoded coordinate could not keep.
  /// </summary>
  public class NoteFacesTests
  {
    private static string Repo(params string[] parts)
    {
      var dir = new DirectoryInfo(AppContext.BaseDirectory);
      while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Dwight.sln"))) dir = dir.Parent;
      Assert.NotNull(dir);
      return File.ReadAllText(Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray())).Replace("\r\n", "\n");
    }

    private static readonly string Css = Repo("Dwight", "Resources", "dwight-fonts.css");
    private static readonly List<EmbeddedFace> Faces = NoteFaces.Read(Css);

    // One for the whole class, as the product has one for the session: GDI's memory fonts are not made to be loaded and dropped over and over.
    private static readonly NoteFaces Loaded = NoteFaces.FromCss(Css);

    private static uint U32(byte[] d, int at) => (uint)((d[at] << 24) | (d[at + 1] << 16) | (d[at + 2] << 8) | d[at + 3]);
    private static int U16(byte[] d, int at) => (d[at] << 8) | d[at + 1];

    private static Dictionary<string, (int Offset, int Length, uint Sum)> Tables(byte[] font)
    {
      var tables = new Dictionary<string, (int, int, uint)>();
      for (var i = 0; i < U16(font, 4); i++)
      {
        var at = 12 + 16 * i;
        tables[Encoding.ASCII.GetString(font, at, 4)] = ((int)U32(font, at + 8), (int)U32(font, at + 12), U32(font, at + 4));
      }
      return tables;
    }

    private static uint Sum(byte[] d, int at, int length)
    {
      uint sum = 0;
      for (var i = 0; i < length; i += 4)
      {
        uint word = 0;
        for (var k = 0; k < 4; k++) word = (word << 8) | (i + k < length ? d[at + i + k] : (byte)0);
        sum = unchecked(sum + word);
      }
      return sum;
    }

    // ---- reading the CSS ------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_six_faces_the_panel_ships_are_read_from_its_css()
    {
      Assert.Equal(new[] { "Inter", "Space Mono", "Space Mono", "Space Grotesk", "Space Grotesk", "Space Grotesk" }, Faces.Select(f => f.Family));
      Assert.Equal(new[] { "400 600", "400", "700", "500", "600", "700" }, Faces.Select(f => f.Weight));
      Assert.All(Faces, f => Assert.Equal(0x774F4632u, U32(f.Woff2, 0)));
    }

    [Fact]
    public void No_css_is_no_faces() => Assert.Empty(NoteFaces.Read(null));

    [Fact]
    public void A_rule_with_no_data_is_left_out_and_the_rest_are_read()
    {
      var faces = NoteFaces.Read("@font-face{font-family:'Gone';font-weight:400;}@font-face{font-family:'Here';font-weight:400;src:url(data:font/woff2;base64,AAAA) format('woff2')}");
      Assert.Equal("Here", Assert.Single(faces).Family);
    }

    /// <summary>Space Grotesk's three rules are one variable file three times over, which is one font to GDI.</summary>
    [Fact]
    public void Each_familys_distinct_files_go_in_once()
    {
      int Files(string family) => NoteFaces.Distinct(Faces.Where(f => f.Family == family)).Count;
      Assert.Equal(1, Files("Inter"));
      Assert.Equal(2, Files("Space Mono"));
      Assert.Equal(1, Files("Space Grotesk"));
    }

    // ---- the decoder ----------------------------------------------------------------------------------------------------------------------------

    public static IEnumerable<object[]> EachFace() => Enumerable.Range(0, 6).Select(i => new object[] { i });

    [Theory]
    [MemberData(nameof(EachFace))]
    public void A_decoded_face_is_a_well_formed_font(int index)
    {
      var font = Woff2.ToSfnt(Faces[index].Woff2);
      var tables = Tables(font);

      var tags = tables.Keys.ToList();
      Assert.Equal(tags.OrderBy(t => t, StringComparer.Ordinal), tags);
      foreach (var required in new[] { "cmap", "glyf", "head", "hhea", "hmtx", "loca", "maxp", "name", "post" }) Assert.Contains(required, tags);

      var head = tables["head"];
      Assert.Equal(0x5F0F3CF5u, U32(font, head.Offset + 12));
      Assert.Equal(0xB1B0AFBAu, Sum(font, 0, font.Length));   // what a font's own reader checks, with checkSumAdjustment in place
      foreach (var t in tables.Where(t => t.Key != "head")) Assert.Equal(t.Value.Sum, Sum(font, t.Value.Offset, t.Value.Length));

      var glyphs = U16(font, tables["maxp"].Offset + 4);
      var longForm = U16(font, head.Offset + 50) != 0;
      var loca = tables["loca"];
      Assert.Equal((glyphs + 1) * (longForm ? 4 : 2), loca.Length);
      long Offset(int i) => longForm ? U32(font, loca.Offset + 4 * i) : 2L * U16(font, loca.Offset + 2 * i);
      for (var i = 0; i < glyphs; i++) Assert.True(Offset(i) <= Offset(i + 1), "glyph " + i);
      Assert.Equal(tables["glyf"].Length, Offset(glyphs));
    }

    /// <summary>A simple glyph's points, walked as TrueType's own reader walks them. It fails unless the walk ends at the glyph's end, give or take its padding.</summary>
    private static List<(int Glyph, int[] Xs, int[] Ys, bool[] On)> SimpleGlyphs(byte[] font)
    {
      var tables = Tables(font);
      var glyphs = U16(font, tables["maxp"].Offset + 4);
      var longForm = U16(font, tables["head"].Offset + 50) != 0;
      var loca = tables["loca"].Offset;
      var glyf = tables["glyf"].Offset;
      long Offset(int i) => longForm ? U32(font, loca + 4 * i) : 2L * U16(font, loca + 2 * i);
      short S16(int at) => (short)U16(font, at);

      var found = new List<(int, int[], int[], bool[])>();
      for (var g = 0; g < glyphs; g++)
      {
        var start = glyf + (int)Offset(g);
        var end = glyf + (int)Offset(g + 1);
        if (start == end) continue;
        int contours = S16(start);
        if (contours < 0) continue;
        var at = start + 10;
        var last = -1;
        for (var c = 0; c < contours; c++) { var e = U16(font, at); Assert.True(e > last, "glyph " + g); last = e; at += 2; }
        var points = last + 1;
        at += 2 + U16(font, at);
        var flags = new byte[points];
        for (var p = 0; p < points;)
        {
          var f = font[at++];
          flags[p++] = f;
          if ((f & 8) == 0) continue;
          for (int repeat = font[at++]; repeat > 0; repeat--) flags[p++] = f;
        }
        int[] Walk(int shortBit, int sameBit)
        {
          var values = new int[points];
          var v = 0;
          for (var p = 0; p < points; p++)
          {
            var f = flags[p];
            if ((f & shortBit) != 0) { var d = font[at++]; v += (f & sameBit) != 0 ? d : -d; }
            else if ((f & sameBit) == 0) { v += S16(at); at += 2; }
            values[p] = v;
          }
          return values;
        }
        var xs = Walk(2, 16);
        var ys = Walk(4, 32);
        Assert.InRange(end - at, 0, 3);
        found.Add((g, xs, ys, flags.Select(f => (f & 1) != 0).ToArray()));
      }
      return found;
    }

    /// <summary>
    /// THE ORACLES A DECODER CANNOT WRITE. A bounding box the file left out is computed from the very points being checked, so a box proves nothing: this test once passed with every delta's sign reversed. But hmtx and head
    /// reach the decoder as stored bytes, so a glyph's left side bearing, written by the font's maker, must be the leftmost of its decoded points, and every point must lie inside the font's own extents.
    /// </summary>
    [Theory]
    [MemberData(nameof(EachFace))]
    public void Every_glyphs_points_agree_with_what_the_font_says_of_itself(int index)
    {
      var font = Woff2.ToSfnt(Faces[index].Woff2);
      var tables = Tables(font);
      var head = tables["head"].Offset;
      short S16(int at) => (short)U16(font, at);
      int xMin = S16(head + 36), yMin = S16(head + 38), xMax = S16(head + 40), yMax = S16(head + 42);

      var longMetrics = U16(font, tables["hhea"].Offset + 34);
      var hmtx = tables["hmtx"].Offset;
      int Bearing(int g) => g < longMetrics ? S16(hmtx + 4 * g + 2) : S16(hmtx + 4 * longMetrics + 2 * (g - longMetrics));

      var glyphs = SimpleGlyphs(font);
      Assert.True(glyphs.Count > 100, glyphs.Count.ToString());
      var agree = 0;
      foreach (var (g, xs, ys, _) in glyphs)
      {
        if (xs.Length == 0) continue;
        Assert.InRange(xs.Min(), xMin, xMax);
        Assert.InRange(xs.Max(), xMin, xMax);
        Assert.InRange(ys.Min(), yMin, yMax);
        Assert.InRange(ys.Max(), yMin, yMax);
        if (xs.Min() == Bearing(g)) agree++;
      }
      Assert.True(agree >= glyphs.Count * 98 / 100, agree + " of " + glyphs.Count);
    }

    [Theory]
    [MemberData(nameof(EachFace))]
    public void A_font_of_curves_has_points_off_the_curve(int index) =>
      Assert.Contains(SimpleGlyphs(Woff2.ToSfnt(Faces[index].Woff2)), g => g.On.Contains(false) && g.On.Contains(true));

    /// <summary>The whole of what the decoder writes, for each file the product ships, as it stood when it was read against those oracles and drawn beside the installed faces. Nothing but a change to the decoder or the files moves it.</summary>
    [Theory]
    [InlineData(0, "1137a7b69d802c1003f0609d0d7872349aa03928f6f2adb2dc78c9d121b32cd1")]
    [InlineData(1, "de1ce778cff0ea90470aaef6513ac33c2de4b210af2fde08b5e81607da86ee4a")]
    [InlineData(2, "3e83c24a43a6d70202c44bae19e389a0742f4c318787bfd0c8dd34cf661bd424")]
    [InlineData(3, "bde5a54b7c49ce02db19c1e6b46f700ea79f9246ca98cc85758ccdde43b247d8")]
    public void The_decoded_files_are_the_ones_that_were_checked(int index, string expected)
    {
      using (var sha = System.Security.Cryptography.SHA256.Create())
        Assert.Equal(expected, Convert.ToHexString(sha.ComputeHash(Woff2.ToSfnt(Faces[index].Woff2))).ToLowerInvariant());
    }

    [Fact]
    public void The_variable_faces_keep_their_axes_because_that_is_where_their_weights_come_from()
    {
      var inter = Tables(Woff2.ToSfnt(Faces[0].Woff2));
      Assert.Contains("fvar", inter.Keys);
      Assert.Contains("gvar", inter.Keys);
      Assert.DoesNotContain("bsln", inter.Keys);   // what the fvar tag's index meant in a table one step wrong
      Assert.DoesNotContain("just", inter.Keys);
    }

    [Fact]
    public void What_is_not_woff2_is_refused()
    {
      Assert.Throws<InvalidDataException>(() => Woff2.ToSfnt(new byte[60]));
      Assert.ThrowsAny<Exception>(() => Woff2.ToSfnt(Faces[1].Woff2.Take(200).ToArray()));
    }

    // ---- the faces ------------------------------------------------------------------------------------------------------------------------------

    /// <summary>The ruling: the collection is built from the embedded bytes, and it names the three faces. This machine may have them installed; these are not those, because the family's own default instance is what names the display face.</summary>
    [Fact]
    public void The_three_faces_come_from_the_embedded_bytes()
    {
      Assert.True(Loaded.Embedded, Loaded.Body.FontFamily.Name + " | " + Loaded.Bold.FontFamily.Name + " | " + Loaded.Tag.FontFamily.Name + " | " + Loaded.Code.FontFamily.Name);
      Assert.Equal(NoteFaces.BodyFamily, Loaded.Body.FontFamily.Name);
      Assert.Equal(NoteFaces.DisplayFamily, Loaded.Tag.FontFamily.Name);
      Assert.Equal(NoteFaces.MonoFamily, Loaded.Code.FontFamily.Name);
      Assert.Equal(NoteFaces.MonoFamily, Loaded.Count.FontFamily.Name);
      Assert.StartsWith("Inter", Loaded.Bold.FontFamily.Name);
      Assert.True(Loaded.Tag.Bold);
    }

    /// <summary>GDI now and then lists another family for a new collection. Each is checked against what it was made for and tried again, and what it was given stays alive for the session.</summary>
    [Fact]
    public void A_collection_is_checked_against_its_family_and_tried_again()
    {
      Assert.True(NoteFaces.Holds(new[] { "Inter", "Inter SemiBold" }, "Inter"));
      Assert.True(NoteFaces.Holds(new[] { "Space Grotesk Light", "Space Grotesk Light Medium" }, "Space Grotesk"));
      Assert.False(NoteFaces.Holds(new[] { "Space Mono" }, "Space Grotesk"));
      Assert.False(NoteFaces.Holds(new string[0], "Inter"));
      Assert.False(NoteFaces.Holds(null, "Inter"));

      var load = Repo("Dwight", "UI", "NoteFaces.cs");
      Assert.Contains("if (!Holds(names, family.Key)) continue;", load);
      Assert.Contains("for (var attempt = 0; attempt < MaxLoads; attempt++)", load);
      Assert.Contains("private const int MaxLoads = 3;", load);
      Assert.InRange(load.IndexOf("_held.Add(collection);", StringComparison.Ordinal), 0, load.IndexOf("collection.AddMemoryFont(", StringComparison.Ordinal));
      Assert.DoesNotContain("FreeCoTaskMem", load);
      Assert.Contains("private static readonly Lazy<NoteFaces> Brand", Repo("Dwight", "UI", "NoteCard.cs"));   // once per session
    }

    [Fact]
    public void A_bold_run_is_wider_than_its_regular_so_it_is_a_weight_and_not_the_same_face()
    {
      using (var bmp = new Bitmap(4, 4))
      using (var g = Graphics.FromImage(bmp))
        Assert.True(g.MeasureString("Remap Numbers squeezes", Loaded.Bold).Width > g.MeasureString("Remap Numbers squeezes", Loaded.Body).Width);
    }

    /// <summary>Glyphs drawn from the decoded bytes stand as tall as the font says its capitals are. A coordinate decoded wrongly would not.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Drawn_outlines_match_the_fonts_stated_cap_height(int index)
    {
      var font = Woff2.ToSfnt(Faces[index].Woff2);
      var tables = Tables(font);
      var perEm = (double)U16(font, tables["head"].Offset + 18);
      var capHeight = (short)U16(font, tables["OS/2"].Offset + 88);
      Assert.InRange(capHeight, 1, 2000);

      var family = index == 0 ? Loaded.Body.FontFamily : Loaded.Code.FontFamily;
      using (var path = new GraphicsPath())
      {
        path.AddString("H", family, (int)FontStyle.Regular, 1000f, PointF.Empty, StringFormat.GenericTypographic);
        Assert.Equal(capHeight / perEm, path.GetBounds().Height / 1000.0, 2);
      }
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("not css at all")]
    [InlineData("@font-face{font-family:'Inter';font-weight:400;src:url(data:font/woff2;base64,AAAAAAAA) format('woff2')}")]
    public void What_cannot_be_read_is_the_system_faces_and_never_a_throw(string css)
    {
      var faces = NoteFaces.FromCss(css);
      Assert.False(faces.Embedded);
      Assert.NotNull(faces.Body);
      Assert.NotNull(faces.Bold);
      Assert.NotNull(faces.Code);
      Assert.NotNull(faces.Tag);
      Assert.NotNull(faces.Count);
    }

    [Fact]
    public void The_systems_faces_are_the_named_fallback()
    {
      var faces = NoteFaces.System();
      Assert.False(faces.Embedded);
      Assert.Equal("Consolas", faces.Code.OriginalFontName);
      Assert.Equal("Segoe UI", faces.Body.OriginalFontName);
    }

    // ---- what the faces do not hold -------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Loft and `96` panels", false)]
    [InlineData("café ü ř", false)]
    [InlineData("→ × —", false)]
    [InlineData("", false)]
    [InlineData("கூடு", true)]
    [InlineData("客厅", true)]
    [InlineData("a 😀 b", true)]
    public void Words_the_embedded_faces_lack_take_the_systems(string text, bool system) =>
      Assert.Equal(system, NoteLayout.NeedsSystemFace(text));

    private sealed class Fixed : INoteMetrics
    {
      public double Width(string text, NoteStyle style) => (text ?? "").Length * 6;
    }

    [Fact]
    public void A_line_is_not_merged_across_two_faces_because_each_was_measured_in_its_own()
    {
      var r = NoteLayout.Build(NoteMarkdown.Parse("the கூடு here"), new Fixed());
      Assert.Equal(new[] { "the ", "கூடு", " here" }, r.Text.Select(t => t.Text));
    }

    // ---- the card uses them -----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_card_draws_from_the_embedded_faces_and_a_runs_own_text_chooses()
    {
      var card = Repo("Dwight", "UI", "NoteCard.cs");
      Assert.Contains("GetManifestResourceStream(\"Dwight.Fonts.css\")", card);
      Assert.Contains("NoteFaces.FromCss(EmbeddedCss())", card);
      Assert.Contains("NoteLayout.NeedsSystemFace(text) ? Fallback.Value : Brand.Value", card);
      Assert.Contains("var font = FontOf(t.Style, t.Text);", card);
      Assert.Contains("Brand.Value.Tag", card);
      Assert.Contains("Brand.Value.Count", card);
      Assert.DoesNotContain("InstalledFontCollection", card);
      Assert.Contains("LogicalName=\"Dwight.Fonts.css\"", Repo("Dwight", "Dwight.csproj"));
    }
  }
}
