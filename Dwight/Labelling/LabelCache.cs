using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Dwight.Labelling
{
  /// <summary>
  /// The content-hash label cache (Phase 6 WP-C): a cluster's label is keyed by a SHA-256 of its STABLE
  /// content - sorted member guids + types + internal edges - so on a re-run after an edit, only the changed
  /// cluster misses the cache and is re-billed; every unchanged cluster is free. This drives repeat cost to
  /// near zero and keeps the token budget flat as the graph grows. The hashing is headless and
  /// order-independent, so it is unit-tested.
  ///
  /// WP-5: the cache now SURVIVES A RESTART, persisted to <c>%APPDATA%\Dwight\labelcache.json</c> - without it
  /// every Rhino session re-billed the same labels for the same unchanged definition. Persistence is OPT-IN by
  /// the host (<see cref="EnablePersistence"/>, called once from the startup hook): a headless test never names
  /// a path, so it never reads or writes the user's real cache. Entries carry a monotonic sequence stamp so the
  /// file can be bounded to the most-recent <see cref="LabelCacheStore.MaxEntries"/>; the (de)serialization is
  /// the GH-free <see cref="LabelCacheStore"/>. This is NOT the ADR-067 durable store (Phase 8) - it is a cache,
  /// and a lost or corrupt file costs exactly one re-bill.
  /// </summary>
  internal static class LabelCache
  {
    private static readonly Dictionary<string, (string Label, long Seq)> _cache = new Dictionary<string, (string, long)>();
    // arrangement is cached by the WHOLE cluster-graph shape (the clusters' content hashes + the flow), not
    // per-cluster, because the relationships depend on the whole picture. Cleared with the label cache.
    private static readonly Dictionary<string, (List<ArrangementHint> Hints, long Seq)> _arrange = new Dictionary<string, (List<ArrangementHint>, long Seq)>();
    private static readonly object _gate = new object();

    private static string _path;          // null => in-memory only (the headless default)
    private static bool _loaded;
    private static bool _dirty;
    private static long _seq;             // monotonic; stamps recency without a clock

    /// <summary>The host turns persistence on once at startup. Until then the cache is in-memory only, which is
    /// what keeps the unit tests off the user's real file.</summary>
    public static void EnablePersistence(string path)
    {
      lock (_gate) { _path = path; _loaded = false; }
    }

    public static string DefaultPath =>
      Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Dwight", "labelcache.json");

    /// <summary>A stable hash of a cluster's content tokens (member guids, types, internal edges). The tokens
    /// are sorted, so the hash is independent of enumeration order - the same cluster always hashes the same.</summary>
    public static string Hash(IEnumerable<string> contentTokens)
    {
      var canonical = string.Join("|", contentTokens.OrderBy(t => t, StringComparer.Ordinal));
      using (var sha = SHA256.Create())
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(canonical)));
    }

    public static bool TryGet(string hash, out string label)
    {
      lock (_gate)
      {
        EnsureLoaded();
        if (!_cache.TryGetValue(hash, out var e)) { label = null; return false; }
        _cache[hash] = (e.Label, ++_seq);   // touch: an actively-used label is not the one evicted at the cap
        label = e.Label;
        return true;
      }
    }

    public static void Set(string hash, string label)
    {
      lock (_gate) { EnsureLoaded(); _cache[hash] = (label, ++_seq); _dirty = true; }
    }

    // WP-3.4(iii): store and return the arrangement list by COPY, so a caller mutating what it got back (or
    // the list it passed in) can never corrupt the cached entry that a later run reuses.
    public static bool TryGetArrangement(string hash, out List<ArrangementHint> hints)
    {
      lock (_gate)
      {
        EnsureLoaded();
        if (!_arrange.TryGetValue(hash, out var stored)) { hints = null; return false; }
        _arrange[hash] = (stored.Hints, ++_seq);
        hints = new List<ArrangementHint>(stored.Hints);
        return true;
      }
    }

    public static void SetArrangement(string hash, List<ArrangementHint> hints)
    {
      lock (_gate)
      {
        EnsureLoaded();
        _arrange[hash] = (new List<ArrangementHint>(hints ?? (IEnumerable<ArrangementHint>)Array.Empty<ArrangementHint>()), ++_seq);
        _dirty = true;
      }
    }

    /// <summary>Persist, if anything changed and the host enabled persistence. Called once at the end of a
    /// labelling pass rather than per entry, so a 12-cluster run writes the file once. Best-effort: a failed
    /// write leaves the in-memory cache intact and costs, at worst, a re-bill next session.</summary>
    public static void Flush()
    {
      lock (_gate)
      {
        if (_path == null || !_dirty) return;
        try
        {
          var labels = _cache.Select(kv => new LabelCacheStore.LabelDto { Hash = kv.Key, Label = kv.Value.Label, Seq = kv.Value.Seq });
          var arrangements = _arrange.Select(kv => new LabelCacheStore.ArrangeDto
          {
            Hash = kv.Key,
            Seq = kv.Value.Seq,
            Hints = kv.Value.Hints.Select(h => new LabelCacheStore.HintDto { Kind = h.Kind, Clusters = new List<int>(h.Clusters) }).ToList(),
          });
          var json = LabelCacheStore.Write(_seq, labels, arrangements);
          Directory.CreateDirectory(Path.GetDirectoryName(_path));
          File.WriteAllText(_path, json);
          _dirty = false;
        }
        catch { /* a cache write is never worth failing the user's Reorganise over */ }
      }
    }

    /// <summary>Load once, on the first use after the host enables persistence. A missing or corrupt file reads
    /// as empty (<see cref="LabelCacheStore.Read"/>), so a bad file degrades to "cold cache", never to an error.</summary>
    private static void EnsureLoaded()
    {
      if (_loaded || _path == null) return;
      _loaded = true;   // set FIRST: a failed load must not retry on every lookup
      try
      {
        if (!File.Exists(_path)) return;
        var file = LabelCacheStore.Read(File.ReadAllText(_path));
        foreach (var e in file.Labels) _cache[e.Hash] = (e.Label, e.Seq);
        foreach (var a in file.Arrangements)
        {
          var hints = new List<ArrangementHint>();
          foreach (var h in a.Hints)
          {
            var hint = new ArrangementHint { Kind = h.Kind };
            foreach (var c in h.Clusters ?? new List<int>()) hint.Clusters.Add(c);
            hints.Add(hint);
          }
          _arrange[a.Hash] = (hints, a.Seq);
        }
        _seq = Math.Max(_seq, file.Seq);
      }
      catch { /* cold cache */ }
    }

    public static void Clear()
    {
      lock (_gate) { _cache.Clear(); _arrange.Clear(); _seq = 0; _dirty = true; }
    }
  }
}
