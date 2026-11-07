using System;
using System.IO;

namespace Dwight.Session
{
  /// <summary>
  /// The file-I/O shim for the durable per-file store (WP-1 step 4; ADR-067, ADR-091). All the DECISIONS -
  /// schema, caps, corruption handling, forward compatibility - live in the GH-free, unit-tested
  /// <see cref="SessionStoreCore"/>; this type only moves bytes to and from
  /// <c>%APPDATA%\Dwight\sessions\&lt;key&gt;.json</c>, so it is correct by inspection and is runtime-verified
  /// by Tharun (the survival gate), exactly as <see cref="Dwight.Labelling.LabelCache"/>'s file I/O is.
  ///
  /// OPT-IN BY THE HOST (<see cref="Enable"/>), so nothing touches the user's real store until the plugin turns
  /// it on at startup - a headless context never writes a byte.
  /// </summary>
  internal static class SessionStore
  {
    private static string _baseDir;   // null => persistence off (the default; headless-safe)

    /// <summary>Turn persistence on, pointed at a real directory. Called once from the startup hook.</summary>
    public static void Enable(string baseDir) { _baseDir = baseDir; }

    public static bool Enabled => _baseDir != null;

    public static string DefaultDir =>
      Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Dwight", "sessions");

    /// <summary>
    /// Read a document's record, or null when there is nothing usable to read. A corrupt, truncated, or empty
    /// file reads as null (the caller starts cold) rather than throwing - losing history is recoverable, a
    /// crash is not. A file written by a NEWER Dwight is PRESERVED ASIDE and read as null: an older build must
    /// never clobber a newer one's threads.
    /// </summary>
    public static SessionRecord Load(string key)
    {
      if (_baseDir == null || string.IsNullOrEmpty(key)) return null;
      try
      {
        var path = PathFor(key);
        if (!File.Exists(path)) return null;

        var result = SessionStoreCore.Read(File.ReadAllText(path));
        if (result.Outcome == StoreReadOutcome.FutureVersion) { PreserveAside(path); return null; }
        return result.Outcome == StoreReadOutcome.Ok ? result.Record : null;
      }
      catch { return null; }
    }

    /// <summary>
    /// Write a document's record. No-ops unless there is something worth a file (#165), so an untitled scratch
    /// document the user abandons never litters the folder. The write is ATOMIC - a full temp file is moved
    /// over the target - so a crash mid-write can never leave a half-written store that reads as corrupt.
    /// Best-effort: a failed write costs a re-record next session, never the user's work.
    /// </summary>
    public static void Save(string key, SessionRecord rec)
    {
      if (_baseDir == null || string.IsNullOrEmpty(key)) return;
      // #165: no empty store files. A file that already exists is still written, so deleting the last note (or the last of anything) reaches the disk.
      if (!SessionStoreCore.HasContent(rec) && !File.Exists(PathFor(key))) return;
      try
      {
        Directory.CreateDirectory(_baseDir);
        var path = PathFor(key);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, SessionStoreCore.Write(rec));
        // MoveFileEx(REPLACE_EXISTING) is atomic on one volume, so a reader never sees a torn file.
        File.Move(tmp, path, overwrite: true);
      }
      catch { }
    }

    private static string PathFor(string key) => Path.Combine(_baseDir, key + ".json");

    /// <summary>Brief DX: where this document's attached images live, or null when there is nowhere to keep one (persistence off, or a document with no key).</summary>
    public static string ImagesDir(string key) =>
      _baseDir == null || string.IsNullOrEmpty(key) ? null : SessionStoreCore.ImagesDir(_baseDir, key);

    /// <summary>Rename a future-versioned file out of the way so it is never overwritten. Stamped so two such
    /// files in one session do not collide (the plugin may use the clock; only the GH-free core may not).</summary>
    private static void PreserveAside(string path)
    {
      try
      {
        var aside = path + "." + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + ".future.bak";
        if (!File.Exists(aside)) File.Move(path, aside);
      }
      catch { }
    }
  }
}
