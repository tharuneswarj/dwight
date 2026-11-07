using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;

namespace Dwight.Session
{
  internal enum ImageRefusal { None, TooBig, WrongType, Unreadable }

  /// <summary>What came of reading an image: the image, or why not.</summary>
  internal sealed class ImageIntake
  {
    public PreparedImage Image;
    public ImageRefusal Refusal;

    public string RefusalLine =>
      Refusal == ImageRefusal.TooBig ? ChatImage.TooBigLine
      : Refusal == ImageRefusal.WrongType ? ChatImage.WrongTypeLine
      : Refusal == ImageRefusal.Unreadable ? ChatImage.UnreadableLine
      : null;

    public static ImageIntake No(ImageRefusal why) => new ImageIntake { Refusal = why };
  }

  /// <summary>
  /// Brief DX: an image as it comes (a paste's data URL, a file's bytes) becomes the one set of bytes that is shown, recorded and sent. The type is read from the bytes, never from a name.
  /// Every image is drawn again on a fresh bitmap, so what a file carried beside its pixels (a photo's location, a PNG's text) goes no further, and a phone's sideways photo stands the way
  /// the person saw it. GH-free; System.Drawing only.
  /// </summary>
  internal static class ChatImageCodec
  {
    private const int ExifOrientation = 0x0112;
    private const long JpegQuality = 85L;

    /// <summary>The media type the bytes say they are, or null.</summary>
    public static string Sniff(byte[] b)
    {
      if (b == null) return null;
      if (b.Length >= 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47 && b[4] == 0x0D && b[5] == 0x0A && b[6] == 0x1A && b[7] == 0x0A) return ChatImage.Png;
      if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return ChatImage.Jpeg;
      return null;
    }

    /// <summary>A paste or a drop, as the shell's FileReader writes it: <c>data:image/png;base64,....</c></summary>
    public static ImageIntake FromDataUrl(string dataUrl)
    {
      const string marker = ";base64,";
      var at = dataUrl?.IndexOf(marker, StringComparison.Ordinal) ?? -1;
      if (at < 0) return ImageIntake.No(ImageRefusal.Unreadable);
      var start = at + marker.Length;
      if ((dataUrl.Length - start) / 4L * 3L > ChatImage.MaxFileBytes + 2L) return ImageIntake.No(ImageRefusal.TooBig);
      byte[] raw;
      try { raw = Convert.FromBase64String(dataUrl.Substring(start)); }
      catch (FormatException) { return ImageIntake.No(ImageRefusal.Unreadable); }
      return Prepare(raw);
    }

    /// <summary>Check it, stand it up, fit it, encode it. Never throws: a picture Dwight cannot read is a refusal with its reason.</summary>
    public static ImageIntake Prepare(byte[] raw)
    {
      if (raw == null || raw.Length == 0) return ImageIntake.No(ImageRefusal.Unreadable);
      if (raw.Length > ChatImage.MaxFileBytes) return ImageIntake.No(ImageRefusal.TooBig);
      var mediaType = Sniff(raw);
      if (mediaType == null) return ImageIntake.No(ImageRefusal.WrongType);
      try
      {
        using (var ms = new MemoryStream(raw))
        using (var source = Image.FromStream(ms, useEmbeddedColorManagement: false, validateImageData: false))
        {
          if (source.Width * (long)source.Height > ChatImage.MaxSourcePixels) return ImageIntake.No(ImageRefusal.Unreadable);
          StandUp(source);
          var (width, height) = ChatImage.Fit(source.Width, source.Height);
          var isPng = mediaType == ChatImage.Png;
          var bytes = Encode(source, width, height, keepAlpha: isPng, png: isPng);
          // A noisy photograph pasted as a PNG can pass what a provider takes after base64. The plugin can compute that, so it does not become a 400.
          if (isPng && bytes.Length > ChatImage.MaxEncodedBytes)
          {
            bytes = Encode(source, width, height, keepAlpha: false, png: false);
            mediaType = ChatImage.Jpeg;
          }
          return new ImageIntake
          {
            Image = new PreparedImage { Bytes = bytes, MediaType = mediaType, Width = width, Height = height, Sha256 = ChatImage.Sha256Hex(bytes) },
          };
        }
      }
      catch (Exception ex) when (ex is ArgumentException || ex is OutOfMemoryException || ex is System.Runtime.InteropServices.ExternalException || ex is InvalidOperationException || ex is IOException)
      {
        return ImageIntake.No(ImageRefusal.Unreadable);
      }
    }

    /// <summary>The EXIF orientation, applied, because the redraw below loses the tag that told a viewer to turn the picture.</summary>
    private static void StandUp(Image image)
    {
      if (!image.PropertyIdList.Contains(ExifOrientation)) return;
      var value = image.GetPropertyItem(ExifOrientation)?.Value;
      if (value == null || value.Length < 1) return;
      switch (value[0])
      {
        case 2: image.RotateFlip(RotateFlipType.RotateNoneFlipX); break;
        case 3: image.RotateFlip(RotateFlipType.Rotate180FlipNone); break;
        case 4: image.RotateFlip(RotateFlipType.Rotate180FlipX); break;
        case 5: image.RotateFlip(RotateFlipType.Rotate90FlipX); break;
        case 6: image.RotateFlip(RotateFlipType.Rotate90FlipNone); break;
        case 7: image.RotateFlip(RotateFlipType.Rotate270FlipX); break;
        case 8: image.RotateFlip(RotateFlipType.Rotate270FlipNone); break;
      }
    }

    private static byte[] Encode(Image source, int width, int height, bool keepAlpha, bool png)
    {
      using (var canvas = new Bitmap(width, height, keepAlpha ? PixelFormat.Format32bppArgb : PixelFormat.Format24bppRgb))
      using (var g = Graphics.FromImage(canvas))
      using (var edges = new ImageAttributes())
      using (var output = new MemoryStream())
      {
        if (!keepAlpha) g.Clear(Color.White);   // a transparent PNG going to JPEG has no black to fall back on
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.CompositingQuality = CompositingQuality.HighQuality;
        g.SmoothingMode = SmoothingMode.HighQuality;
        edges.SetWrapMode(WrapMode.TileFlipXY);   // without it a downscale draws a pale line along the edge
        g.DrawImage(source, new Rectangle(0, 0, width, height), 0, 0, source.Width, source.Height, GraphicsUnit.Pixel, edges);

        if (png) canvas.Save(output, ImageFormat.Png);
        else
        {
          var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
          using (var settings = new EncoderParameters(1))
          {
            settings.Param[0] = new EncoderParameter(Encoder.Quality, JpegQuality);
            canvas.Save(output, codec, settings);
          }
        }
        return output.ToArray();
      }
    }
  }
}
