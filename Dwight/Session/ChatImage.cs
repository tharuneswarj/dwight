using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;

namespace Dwight.Session
{
  /// <summary>
  /// AN IMAGE THE PERSON ATTACHED TO A TURN OF THE BUILD CHAT (Brief DX): its numbers and its sentences, GH-free (ADR-091).
  ///
  /// <para>An image is a reference the person gave, never a source of ids, coordinates or component names (ADR-056). It leaves only because
  /// the person attached it to a turn they sent. Nothing in the plugin takes one from the canvas, the viewport or the clipboard by itself.</para>
  ///
  /// <para>A NUMBER ONLY WHERE THE RULE IS KNOWN (Brief AK, CY, and the architect's ruling of 2 November: never a number that is wrong). Anthropic's free token count on Claude Sonnet 5.5, with and without an image
  /// through this codec, gave exactly ceil(width / 28) x ceil(height / 28) + 3 at seven sizes, and Anthropic's page says the 28 of Claude 4.7 and later. For the same 1568 x 1176 image, where that says 2,355, the ledger's
  /// difference was 1,064 on Gemini 3.8 Flash and 1,820 on qwen3.5:9b, and no page's rule gives those. Anthropic's older rows are capped at 1,568. So the catalogue marks the rows the rule applies to
  /// (<see cref="ModelInfo.ImagePatchTokens"/>), and every other row says the provider counts it. The ledger carries what each provider billed, on any row.</para>
  /// </summary>
  internal static class ChatImage
  {
    /// <summary>The longest edge an image is sent at. Past it Anthropic would downscale it anyway on the older models, and the count would be a guess.</summary>
    public const int MaxLongEdge = 1568;

    /// <summary>The side of one image patch, one token each.</summary>
    public const int PatchPixels = 28;

    /// <summary>What Anthropic's count adds to an image's patches, at every size measured.</summary>
    public const int BlockTokens = 3;

    /// <summary>The largest file taken in. The shell refuses at this number before it reads a byte, and a test holds the shell to it.</summary>
    public const int MaxFileBytes = 20 * 1024 * 1024;

    /// <summary>The most an image may weigh once encoded. Base64 makes it 9.3 MB, inside the 10 MB Anthropic's page states. Only a downscaled PNG of a noisy photograph gets near it.</summary>
    public const int MaxEncodedBytes = 7_000_000;

    /// <summary>The largest picture opened at all: a small file can decompress to more memory than Rhino has.</summary>
    public const long MaxSourcePixels = 100_000_000;

    /// <summary>What a thread's images may weigh together in one request, in base64 characters, the newest kept first. Google's page limits a request with inline data to 20 MB.</summary>
    public const int MaxReplayChars = 15_000_000;

    public const string Png = "image/png";
    public const string Jpeg = "image/jpeg";

    // ---- the numbers ----------------------------------------------------------------------------------

    /// <summary>The size an image is sent at: its own when it fits, never larger, and with the proportions it had.</summary>
    public static (int Width, int Height) Fit(int width, int height)
    {
      if (width <= 0 || height <= 0) return (0, 0);
      var longest = Math.Max(width, height);
      if (longest <= MaxLongEdge) return (width, height);
      var k = (double)MaxLongEdge / longest;
      return (Math.Max(1, (int)Math.Round(width * k, MidpointRounding.AwayFromZero)),
              Math.Max(1, (int)Math.Round(height * k, MidpointRounding.AwayFromZero)));
    }

    /// <summary>The tokens one image costs by the measured rule. A person sees it, and a stopped step prices from it, only on a row the rule applies to, where it is true; on any other it is only a weight against the transcript's bound.</summary>
    public static int Tokens(int width, int height) =>
      width <= 0 || height <= 0 ? 0 : (width + PatchPixels - 1) / PatchPixels * ((height + PatchPixels - 1) / PatchPixels) + BlockTokens;

    // ---- the words ------------------------------------------------------------------------------------

    /// <summary>"1568 x 1000 px".</summary>
    public static string SizeLine(int width, int height) =>
      width.ToString(CultureInfo.InvariantCulture) + " x " + height.ToString(CultureInfo.InvariantCulture) + " px";

    /// <summary>"+1 image, about 2,000 tokens": to the nearest hundred, which is all it needs to claim. On a row the rule does not apply to, no number at all.</summary>
    public static string EstimateLine(int width, int height, bool ruleKnown)
    {
      if (!ruleKnown) return "+1 image, " + CountedByProvider;
      var shown = Math.Max(100, (long)Math.Round(Tokens(width, height) / 100.0, MidpointRounding.AwayFromZero) * 100);
      return "+1 image, about " + shown.ToString("n0", CultureInfo.InvariantCulture) + " tokens";
    }

    public const string CountedByProvider = "counted by the provider";

    /// <summary>The chip's line, above the text box, before anything is sent.</summary>
    public static string ChipLine(int width, int height, bool ruleKnown) => SizeLine(width, height) + ", " + EstimateLine(width, height, ruleKnown);

    /// <summary>A stopped turn's estimate leaves out an image the plugin has no rule for. The person is told, since the number alone reads as the whole.</summary>
    public static string StoppedLeftOutSentence(int images) =>
      "It leaves out " + (images == 1 ? "the image" : "the " + images.ToString(CultureInfo.InvariantCulture) + " images") + ", which this model's provider counts itself.";

    public const string ReplacedNote = "The new image replaced the last one: one image a turn.";
    public const string NotFoundChip = "image not found";
    public const string TooBigLine = "That image is over 20 MB, so it was not attached.";
    public const string WrongTypeLine = "Dwight reads PNG and JPEG images, and this is neither, so it was not attached.";
    public const string UnreadableLine = "Dwight could not read that image, so it was not attached.";
    public const string NotKeptLine = "Dwight could not keep this image on disk, so this message was not sent. Nothing was spent.";

    public sealed class VisionAdvice
    {
      public IReadOnlyList<string> SameProvider = new string[0];
      public string Elsewhere;

      /// <summary>"A and B do; pick one from the model menu." / "A does; pick it from the model menu." / the none-here sentence / null with no alternative at all.</summary>
      public string Sentence()
      {
        if (SameProvider.Count == 2) return SameProvider[0] + " and " + SameProvider[1] + " do; pick one from the model menu.";
        if (SameProvider.Count == 1) return SameProvider[0] + " does; pick it from the model menu.";
        return Elsewhere == null ? null : "No model on this provider reads images; " + Elsewhere + " does if you have a key.";
      }
    }

    /// <summary>The refusal at the attach, naming the model, before any call. With no alternative it is the old sentence.</summary>
    public static string NoVisionLine(string modelName, VisionAdvice advice = null) =>
      (modelName ?? "This model") + " does not read images, so the image was not attached. " + (advice?.Sentence() ?? "Pick a model that does from the model menu.");

    /// <summary>The same refusal at the send, for a model chosen after the image was attached.</summary>
    public static string NoVisionAtSendLine(string modelName, VisionAdvice advice = null)
    {
      var head = (modelName ?? "This model") + " does not read images, so this message was not sent and nothing was spent. ";
      var s = advice?.Sentence();
      if (s == null) return head + "Remove the image, or pick a model that does from the model menu.";
      return head + s + " Or remove the image.";
    }

    // ---- why an image in the record did not go -----------------------------------------------------------

    /// <summary>The model's half: the person attached an image here and this request does not carry it. In the user turn, attributed, as a plugin report is.</summary>
    public static string OmittedNote(ImageOmission why, string modelName)
    {
      switch (why)
      {
        case ImageOmission.NotFound:
          return "[Dwight: the person attached an image to this message. The file is no longer on this computer, so this request does not carry it.]";
        case ImageOmission.ModelReadsNoImages:
          return "[Dwight: the person attached an image to this message. " + (modelName ?? "This model") + " does not read images, so this request does not carry it.]";
        default:
          return "[Dwight: the person attached an image to this message. It was left out of this request to keep the request inside the provider's size limit.]";
      }
    }

    /// <summary>The person's half: one line, once, in the conversation.</summary>
    public static string OmittedLine(ImageOmission why, string modelName)
    {
      switch (why)
      {
        case ImageOmission.NotFound:
          return "An image earlier in this chat is no longer on this computer, so the model did not get it this turn.";
        case ImageOmission.ModelReadsNoImages:
          return (modelName ?? "This model") + " does not read images, so it did not get the images earlier in this chat. Everything else in the conversation went.";
        default:
          return "This chat's earlier images are too many for one request, so the model got the newest and not the oldest.";
      }
    }

    // ---- the record ---------------------------------------------------------------------------------------

    /// <summary>Lower-case hex, the file's name in the images folder.</summary>
    public static string Sha256Hex(byte[] bytes)
    {
      using (var sha = SHA256.Create())
      {
        var hash = sha.ComputeHash(bytes ?? new byte[0]);
        var chars = new char[hash.Length * 2];
        for (var i = 0; i < hash.Length; i++)
        {
          chars[i * 2] = "0123456789abcdef"[hash[i] >> 4];
          chars[i * 2 + 1] = "0123456789abcdef"[hash[i] & 15];
        }
        return new string(chars);
      }
    }

    /// <summary>The record is a file a person can edit, and this becomes a file name, so nothing but a real hash gets that far.</summary>
    public static bool IsSha256(string s)
    {
      if (s == null || s.Length != 64) return false;
      foreach (var c in s)
        if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
      return true;
    }

    /// <summary>The file's extension for a media type, or null for anything Dwight does not write.</summary>
    public static string Extension(string mediaType) =>
      mediaType == Png ? ".png" : mediaType == Jpeg ? ".jpg" : null;

    public static string DataUrl(string mediaType, string base64) => "data:" + mediaType + ";base64," + base64;
  }

  /// <summary>Why an image in the record is not in this request.</summary>
  internal enum ImageOmission { NotFound, ModelReadsNoImages, TooMuchForOneRequest }

  /// <summary>An image downscaled and encoded: the bytes that are shown, recorded and sent.</summary>
  internal sealed class PreparedImage
  {
    public byte[] Bytes;
    public string MediaType;
    public int Width;
    public int Height;
    public string Sha256;
  }

  /// <summary>How one chat turn replays the thread's images. Null on a caller with none to replay, which then says so for each one it meets.</summary>
  internal sealed class ImageReplay
  {
    /// <summary>The image's bytes as base64, or null when the file is gone or is not the file recorded.</summary>
    public Func<StoredImage, string> LoadBase64;
    public bool ModelReadsImages = true;
    /// <summary>Does the model count an image by the measured rule? Otherwise a stopped step leaves the image out of its estimate and says so.</summary>
    public bool RuleKnown;
    public string ModelName;
    public int MaxChars = ChatImage.MaxReplayChars;

    /// <summary>What this build left out, for the person's line. Filled by the build.</summary>
    public readonly HashSet<ImageOmission> Omitted = new HashSet<ImageOmission>();
  }
}
