using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using Dwight.Session;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>Brief DX: the plugin's half of an image, on real bytes: what is taken in, what it becomes, and what the record and the folder hold.</summary>
  public class ChatImageCodecTests
  {
    private static byte[] Encode(int width, int height, ImageFormat format, Color? fill = null, Action<Bitmap> touch = null)
    {
      using (var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb))
      using (var g = Graphics.FromImage(bmp))
      using (var ms = new MemoryStream())
      {
        g.Clear(fill ?? Color.CornflowerBlue);
        touch?.Invoke(bmp);
        bmp.Save(ms, format);
        return ms.ToArray();
      }
    }

    private static PreparedImage Prepared(byte[] raw)
    {
      var intake = ChatImageCodec.Prepare(raw);
      Assert.Equal(ImageRefusal.None, intake.Refusal);
      return intake.Image;
    }

    private static (int W, int H) Decoded(byte[] bytes)
    {
      using (var ms = new MemoryStream(bytes))
      using (var image = Image.FromStream(ms)) return (image.Width, image.Height);
    }

    // ---- the numbers -----------------------------------------------------------------------------------

    [Theory]
    [InlineData(3136, 2000, 1568, 1000)]
    [InlineData(2000, 3136, 1000, 1568)]
    [InlineData(1568, 1000, 1568, 1000)]
    [InlineData(1569, 1569, 1568, 1568)]
    [InlineData(800, 600, 800, 600)]      // never made larger
    [InlineData(10000, 1, 1568, 1)]       // a sliver keeps at least a pixel
    public void An_image_fits_inside_the_long_edge_with_the_proportions_it_had(int w, int h, int wantW, int wantH) =>
      Assert.Equal((wantW, wantH), ChatImage.Fit(w, h));

    [Fact]
    public void The_token_count_is_the_formula_rounded_up_and_nothing_for_no_picture()
    {
      Assert.Equal(2019, ChatImage.Tokens(1568, 1000));
      Assert.Equal(4, ChatImage.Tokens(1, 1));
      Assert.Equal(0, ChatImage.Tokens(0, 100));
    }

    [Fact]
    public void The_chip_says_the_size_and_the_cost()
    {
      Assert.Equal("1568 x 1000 px, +1 image, about 2,000 tokens", ChatImage.ChipLine(1568, 1000, ruleKnown: true));
      Assert.Equal("1568 x 1000 px, +1 image, counted by the provider", ChatImage.ChipLine(1568, 1000, ruleKnown: false));
      Assert.Equal("+1 image, about 100 tokens", ChatImage.EstimateLine(10, 10, ruleKnown: true));   // a tiny image still says a number it can stand behind
    }

    // ---- what comes in --------------------------------------------------------------------------------

    [Fact]
    public void A_large_png_comes_out_a_png_at_the_long_edge_and_is_what_the_record_hashes()
    {
      var image = Prepared(Encode(3136, 2000, ImageFormat.Png));
      Assert.Equal(ChatImage.Png, image.MediaType);
      Assert.Equal((1568, 1000), (image.Width, image.Height));
      Assert.Equal((1568, 1000), Decoded(image.Bytes));
      Assert.Equal(ChatImage.Png, ChatImageCodec.Sniff(image.Bytes));
      Assert.Equal(ChatImage.Sha256Hex(image.Bytes), image.Sha256);
      Assert.True(ChatImage.IsSha256(image.Sha256));
    }

    [Fact]
    public void A_jpeg_comes_out_a_jpeg_and_a_small_image_keeps_its_size()
    {
      var image = Prepared(Encode(400, 300, ImageFormat.Jpeg));
      Assert.Equal(ChatImage.Jpeg, image.MediaType);
      Assert.Equal((400, 300), (image.Width, image.Height));
      Assert.Equal(ChatImage.Jpeg, ChatImageCodec.Sniff(image.Bytes));
    }

    [Fact]
    public void A_png_keeps_its_transparency()
    {
      var image = Prepared(Encode(20, 20, ImageFormat.Png, Color.FromArgb(0, 0, 0, 0)));
      using (var ms = new MemoryStream(image.Bytes))
      using (var bmp = new Bitmap(ms))
        Assert.Equal(0, bmp.GetPixel(10, 10).A);
    }

    [Theory]
    [InlineData(6, 100, 200)]   // a phone held upright: the file is on its side and says so
    [InlineData(8, 100, 200)]
    [InlineData(3, 200, 100)]
    [InlineData(1, 200, 100)]
    public void A_photo_stands_the_way_the_tag_says_and_the_tag_stays_behind(int orientation, int wantW, int wantH)
    {
      var raw = Encode(200, 100, ImageFormat.Jpeg);
      using (var ms = new MemoryStream(raw))
      using (var tagged = (Bitmap)Image.FromStream(ms))
      using (var saved = new MemoryStream())
      {
        var prop = (PropertyItem)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PropertyItem));
        prop.Id = 0x0112; prop.Type = 3; prop.Len = 2; prop.Value = new[] { (byte)orientation, (byte)0 };
        tagged.SetPropertyItem(prop);
        tagged.Save(saved, ImageFormat.Jpeg);

        var image = Prepared(saved.ToArray());
        Assert.Equal((wantW, wantH), (image.Width, image.Height));
        using (var back = new MemoryStream(image.Bytes))
        using (var read = Image.FromStream(back))
          Assert.DoesNotContain(0x0112, read.PropertyIdList);
      }
    }

    [Fact]
    public void A_file_is_judged_by_its_bytes_and_never_by_what_it_is_called()
    {
      Assert.Equal(ImageRefusal.WrongType, ChatImageCodec.Prepare(Encode(10, 10, ImageFormat.Gif)).Refusal);
      Assert.Equal(ImageRefusal.WrongType, ChatImageCodec.Prepare(Encode(10, 10, ImageFormat.Bmp)).Refusal);
      Assert.Equal(ImageRefusal.WrongType, ChatImageCodec.Prepare(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }).Refusal);
      Assert.Equal(ChatImage.WrongTypeLine, ChatImageCodec.Prepare(new byte[] { 1, 2, 3 }).RefusalLine);
    }

    [Fact]
    public void Over_the_cap_is_refused_before_it_is_opened()
    {
      var big = new byte[ChatImage.MaxFileBytes + 1];
      big[0] = 0x89;   // not even a real PNG: the size is what says no
      var intake = ChatImageCodec.Prepare(big);
      Assert.Equal(ImageRefusal.TooBig, intake.Refusal);
      Assert.Equal(ChatImage.TooBigLine, intake.RefusalLine);
    }

    [Fact]
    public void A_damaged_picture_is_a_refusal_and_never_a_throw()
    {
      var png = Encode(10, 10, ImageFormat.Png);
      var cut = png.Take(12).ToArray();   // the right magic, nothing behind it
      Assert.Equal(ImageRefusal.Unreadable, ChatImageCodec.Prepare(cut).Refusal);
      Assert.Equal(ImageRefusal.Unreadable, ChatImageCodec.Prepare(new byte[0]).Refusal);
      Assert.Equal(ImageRefusal.Unreadable, ChatImageCodec.Prepare(null).Refusal);
    }

    [Fact]
    public void A_paste_is_read_from_its_data_url()
    {
      var png = Encode(30, 20, ImageFormat.Png);
      var image = ChatImageCodec.FromDataUrl("data:image/png;base64," + Convert.ToBase64String(png)).Image;
      Assert.Equal((30, 20), (image.Width, image.Height));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unreadable")]
    [InlineData("data:image/png;base64,not base64 at all!")]
    public void A_paste_that_cannot_be_read_says_so(string dataUrl) =>
      Assert.Equal(ImageRefusal.Unreadable, ChatImageCodec.FromDataUrl(dataUrl).Refusal);

    [Fact]
    public void A_paste_over_the_cap_is_refused_before_it_is_decoded()
    {
      // Not base64 at all: only a refusal that came before the decode can say too big rather than unreadable.
      var url = "data:image/png;base64," + new string('!', (ChatImage.MaxFileBytes / 3 + 2) * 4);
      Assert.Equal(ImageRefusal.TooBig, ChatImageCodec.FromDataUrl(url).Refusal);
    }

    // ---- the folder -----------------------------------------------------------------------------------

    private static string Scratch()
    {
      var dir = Path.Combine(Path.GetTempPath(), "dwight-images-" + Guid.NewGuid().ToString("N"), "images");
      return dir;
    }

    [Fact]
    public void An_image_is_kept_by_its_hash_and_read_back_byte_for_byte()
    {
      var dir = Scratch();
      try
      {
        var image = Prepared(Encode(40, 30, ImageFormat.Png));
        var stored = ChatImageStore.Save(dir, image);
        Assert.Equal(image.Sha256, stored.Sha256);
        Assert.Equal((40, 30), (stored.Width, stored.Height));
        Assert.True(File.Exists(Path.Combine(dir, image.Sha256 + ".png")));
        Assert.Equal(image.Bytes, ChatImageStore.Load(dir, stored));
        Assert.DoesNotContain(Directory.GetFiles(dir), f => f.EndsWith(".tmp", StringComparison.Ordinal));

        // The same picture attached twice is one file.
        ChatImageStore.Save(dir, image);
        Assert.Single(Directory.GetFiles(dir));
      }
      finally { Directory.Delete(Path.GetDirectoryName(dir), true); }
    }

    [Fact]
    public void A_file_that_is_gone_or_changed_reads_as_not_found_and_never_as_something_else()
    {
      var dir = Scratch();
      try
      {
        var stored = ChatImageStore.Save(dir, Prepared(Encode(40, 30, ImageFormat.Jpeg)));
        var path = Path.Combine(dir, stored.Sha256 + ".jpg");
        File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
        Assert.Null(ChatImageStore.Load(dir, stored));
        File.Delete(path);
        Assert.Null(ChatImageStore.Load(dir, stored));
      }
      finally { Directory.Delete(Path.GetDirectoryName(dir), true); }
    }

    [Theory]
    [InlineData("..\\..\\..\\secret")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcde")]    // one short
    [InlineData("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF")]   // not what Dwight writes
    [InlineData("")]
    [InlineData(null)]
    public void A_hand_edited_hash_never_becomes_a_path(string sha)
    {
      var stored = new StoredImage { Sha256 = sha, MediaType = ChatImage.Png, Width = 1, Height = 1 };
      Assert.Null(ChatImageStore.PathOf("C:\\images", stored));
      Assert.Null(ChatImageStore.Load("C:\\images", stored));
    }

    [Fact]
    public void A_hand_edited_type_never_becomes_a_path_and_no_folder_means_no_file()
    {
      var sha = new string('a', 64);
      Assert.Null(ChatImageStore.PathOf("C:\\images", new StoredImage { Sha256 = sha, MediaType = "image/svg+xml" }));
      Assert.Null(ChatImageStore.PathOf(null, new StoredImage { Sha256 = sha, MediaType = ChatImage.Png }));
      Assert.Null(ChatImageStore.Save(null, Prepared(Encode(4, 4, ImageFormat.Png))));
    }
  }
}
