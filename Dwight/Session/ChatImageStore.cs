using System;
using System.IO;

namespace Dwight.Session
{
  /// <summary>
  /// Brief DX: the images of a document's threads, as files beside its record: <c>%APPDATA%\Dwight\sessions\&lt;key&gt;\images\&lt;sha256&gt;.png|jpg</c>. The message JSON holds the hash and the
  /// pixel size, never the bytes, so the record stays small and the folder stays copy-portable. Named by content, so one image attached twice is one file. The policy is the store's: this
  /// holds the user's work, so nothing here deletes, and a read that cannot be trusted says "not found" rather than throwing. GH-free; the host passes the folder.
  /// </summary>
  internal static class ChatImageStore
  {
    /// <summary>Where this image's file is, or null when the record does not hold a real hash and a type Dwight writes. A record can be edited by hand, and this becomes a path.</summary>
    public static string PathOf(string imagesDir, StoredImage image)
    {
      if (string.IsNullOrEmpty(imagesDir) || image == null || !ChatImage.IsSha256(image.Sha256)) return null;
      var ext = ChatImage.Extension(image.MediaType);
      return ext == null ? null : Path.Combine(imagesDir, image.Sha256 + ext);
    }

    /// <summary>Keep the image and return what the record holds of it, or null when it could not be written. Atomic, as the record is. A file already there under this hash is this image.</summary>
    public static StoredImage Save(string imagesDir, PreparedImage image)
    {
      if (image?.Bytes == null) return null;
      var stored = new StoredImage { Sha256 = image.Sha256, MediaType = image.MediaType, Width = image.Width, Height = image.Height };
      var path = PathOf(imagesDir, stored);
      if (path == null) return null;
      try
      {
        Directory.CreateDirectory(imagesDir);
        if (File.Exists(path)) return stored;
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, image.Bytes);
        File.Move(tmp, path, overwrite: true);
        return stored;
      }
      catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return null; }
    }

    /// <summary>The image's bytes, or null when the file is gone, unreadable, or is not the file the record names. The thumbnail and the provider read through here, so they read the same bytes.</summary>
    public static byte[] Load(string imagesDir, StoredImage image)
    {
      var path = PathOf(imagesDir, image);
      if (path == null) return null;
      try
      {
        if (!File.Exists(path)) return null;
        var bytes = File.ReadAllBytes(path);
        return ChatImage.Sha256Hex(bytes) == image.Sha256 ? bytes : null;
      }
      catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return null; }
    }
  }
}
