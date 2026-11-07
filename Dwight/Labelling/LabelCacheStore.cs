using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dwight.Labelling
{
  /// <summary>
  /// The GH-free serialization core behind <see cref="LabelCache"/>'s on-disk persistence (WP-5): pure string
  /// in, entries out. It exists so the cheap re-bill fix - Reorganise labels surviving a Rhino restart - is
  /// unit-tested headlessly (ADR-091), while <see cref="LabelCache"/> keeps the file I/O.
  ///
  /// This is NOT the ADR-067 durable store (Phase 8): no document binding, no history, no schema evolution
  /// beyond a version stamp. It is a bounded, content-hash-keyed cache of a pure function's results, so the
  /// worst a bad file can do is cost one re-bill - which is exactly how <see cref="Read"/> treats every error:
  /// a corrupt, truncated, or future-versioned file reads as EMPTY, never as an exception.
  /// </summary>
  internal static class LabelCacheStore
  {
    public const int Version = 1;
    /// <summary>Most-recent entries kept per map. A label is a few dozen bytes, so 500 is generous for a
    /// working set of definitions while keeping the file small enough to read on first use without a stutter.</summary>
    public const int MaxEntries = 500;

    // ---- the wire DTOs. Deliberately NOT the domain types: ArrangementHint's Clusters is get-only, which
    // System.Text.Json cannot populate, and a wire format that drifts with a domain type is a bug waiting to
    // happen. Short names because the file is written on every labelling pass.
    internal sealed class HintDto
    {
      [JsonPropertyName("k")] public string Kind { get; set; }
      [JsonPropertyName("c")] public List<int> Clusters { get; set; }
    }
    internal sealed class LabelDto
    {
      [JsonPropertyName("h")] public string Hash { get; set; }
      [JsonPropertyName("v")] public string Label { get; set; }
      [JsonPropertyName("s")] public long Seq { get; set; }
    }
    internal sealed class ArrangeDto
    {
      [JsonPropertyName("h")] public string Hash { get; set; }
      [JsonPropertyName("s")] public long Seq { get; set; }
      [JsonPropertyName("hints")] public List<HintDto> Hints { get; set; }
    }
    internal sealed class FileDto
    {
      [JsonPropertyName("version")] public int Version { get; set; }
      [JsonPropertyName("seq")] public long Seq { get; set; }
      [JsonPropertyName("labels")] public List<LabelDto> Labels { get; set; }
      [JsonPropertyName("arrangements")] public List<ArrangeDto> Arrangements { get; set; }
    }

    private static readonly JsonSerializerOptions Opts = new JsonSerializerOptions { WriteIndented = false };

    /// <summary>Parse a cache file. ANY problem - missing, malformed, wrong version, null entries - yields an
    /// empty cache rather than an error: a lost cache costs one re-bill, a thrown exception would cost the
    /// Reorganise.</summary>
    public static FileDto Read(string json)
    {
      var empty = new FileDto { Version = Version, Seq = 0, Labels = new List<LabelDto>(), Arrangements = new List<ArrangeDto>() };
      if (string.IsNullOrWhiteSpace(json)) return empty;
      try
      {
        var file = JsonSerializer.Deserialize<FileDto>(json, Opts);
        if (file == null || file.Version != Version) return empty;   // a future version is not guessed at
        file.Labels = (file.Labels ?? new List<LabelDto>())
          .Where(e => e != null && !string.IsNullOrEmpty(e.Hash) && !string.IsNullOrEmpty(e.Label)).ToList();
        file.Arrangements = (file.Arrangements ?? new List<ArrangeDto>())
          .Where(e => e != null && !string.IsNullOrEmpty(e.Hash)).ToList();
        foreach (var a in file.Arrangements)
          a.Hints = (a.Hints ?? new List<HintDto>()).Where(h => h != null).ToList();
        // a seq below the entries' own stamps would let a later write collide with an existing one, so heal it.
        var highest = Math.Max(
          file.Labels.Count == 0 ? 0 : file.Labels.Max(e => e.Seq),
          file.Arrangements.Count == 0 ? 0 : file.Arrangements.Max(e => e.Seq));
        if (file.Seq < highest) file.Seq = highest;
        return file;
      }
      catch { return empty; }
    }

    public static string Write(long seq, IEnumerable<LabelDto> labels, IEnumerable<ArrangeDto> arrangements)
    {
      var file = new FileDto
      {
        Version = Version,
        Seq = seq,
        Labels = Trim(labels, e => e.Seq),
        Arrangements = Trim(arrangements, e => e.Seq),
      };
      return JsonSerializer.Serialize(file, Opts);
    }

    /// <summary>Bound a map to the <see cref="MaxEntries"/> MOST-RECENT entries (highest sequence stamp), so a
    /// long-lived cache evicts the definitions the user stopped touching, not the one they are working on. The
    /// returned list is in ascending stamp order, so a re-read preserves the recency ranking.</summary>
    public static List<T> Trim<T>(IEnumerable<T> entries, Func<T, long> seq) =>
      (entries ?? Enumerable.Empty<T>())
        .Where(e => e != null)
        .OrderByDescending(seq)
        .Take(MaxEntries)
        .OrderBy(seq)
        .ToList();
  }
}
