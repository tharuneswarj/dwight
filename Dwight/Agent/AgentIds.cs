using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dwight.Agent
{
  /// <summary>One pinned id, as it survives a restart.</summary>
  internal sealed class AgentIdEntry
  {
    [JsonPropertyName("i")] public int Id { get; set; }
    [JsonPropertyName("g")] public string Guid { get; set; }
  }

  /// <summary>
  /// The registry, serialisable (#229). ALIVENESS IS DELIBERATELY NOT PERSISTED: whether an object is on
  /// the canvas is a fact about the canvas, and the canvas is what answers it. A restored registry knows
  /// only which ids are PINNED to which guids, and <see cref="AgentIdRegistry.Sync"/> against the live
  /// document decides the rest - so a file edited between sessions cannot leave a stale "alive" behind.
  /// </summary>
  internal sealed class AgentIdSnapshot
  {
    [JsonPropertyName("v")] public int Version { get; set; } = 1;
    /// <summary>The next id to issue. Persisted so a new session CONTINUES the sequence rather than
    /// restarting it - reusing a number for a different object is the one thing the registry exists to
    /// prevent, and a reset counter would do exactly that.</summary>
    [JsonPropertyName("next")] public int Next { get; set; }
    [JsonPropertyName("ids")] public List<AgentIdEntry> Ids { get; set; } = new List<AgentIdEntry>();
  }

  /// <summary>Whether an agent id still points at something on the canvas.</summary>
  internal enum IdState
  {
    /// <summary>Never issued in this scope - the model invented it.</summary>
    Unknown = 0,
    /// <summary>Issued, and its object is present in the current graph.</summary>
    Alive = 1,
    /// <summary>Issued, but its object is no longer on the canvas (deleted, or an undo removed it).</summary>
    Retired = 2,
  }

  /// <summary>
  /// The APPEND-ONLY id registry (Phase-8 WP-2, ruling #191) - the anti-hallucination anchor made safe
  /// across a whole agent session instead of one call.
  ///
  /// THE HAZARD IT CLOSES. <see cref="Dwight.Data.DwightGraph"/> ids are pivot-ordered ORDINALS assigned
  /// per snapshot (Spec A2). <c>build_region</c> moves and adds nodes, so the next snapshot's ordinals
  /// RESHUFFLE - and an id the agent read BEFORE a write would silently address a DIFFERENT node AFTER
  /// it. That is the mis-grounding class: not an error the model can see, but a WRONG ACTION taken with
  /// full confidence. It would defeat ADR-058's groundedness from the inside.
  ///
  /// THE RULE. An id, once issued, is pinned to its guid for the scope's lifetime. New objects get NEW
  /// ids; an id is NEVER handed to a different object. This is the WP-A law - determinism keys off
  /// <c>InstanceGuid</c>, never the pivot-derived id - arriving at the tool surface.
  ///
  /// ON DELETION AND UNDO. A vanished object's id is RETIRED, not forgotten: the guid mapping is kept, so
  /// a stale reference resolves to a truthful "that object is gone" rather than an indistinguishable
  /// "unknown id". Because the mapping survives, an object that comes BACK - the ordinary Ctrl-Z / Ctrl-Y
  /// case on a canvas where every write is undoable - reclaims its original id. That is the pin holding,
  /// not a reissue: the id has never addressed anything but that one guid.
  ///
  /// GH-free and headless-tested (ADR-091). The shim feeds it live guids; it knows nothing about Rhino.
  /// </summary>
  internal sealed class AgentIdRegistry
  {
    private readonly Dictionary<Guid, int> _idByGuid = new Dictionary<Guid, int>();
    private readonly Dictionary<int, Guid> _guidById = new Dictionary<int, Guid>();
    private readonly HashSet<int> _alive = new HashSet<int>();
    private int _next = 1;

    /// <summary>How many ids this scope has ever issued (never decreases).</summary>
    public int Issued => _guidById.Count;

    /// <summary>The id for <paramref name="guid"/>, issuing a new one on first sight. Idempotent: the
    /// same guid always returns the same id, and issuing marks it alive.</summary>
    public int Issue(Guid guid)
    {
      if (_idByGuid.TryGetValue(guid, out var existing)) { _alive.Add(existing); return existing; }
      var id = _next++;
      _idByGuid[guid] = id;
      _guidById[id] = guid;
      _alive.Add(id);
      return id;
    }

    /// <summary>The guid an id is pinned to, whether or not it is still alive.</summary>
    public bool TryGetGuid(int id, out Guid guid) => _guidById.TryGetValue(id, out guid);

    /// <summary>The id already issued for a guid, without issuing one.</summary>
    public bool TryGetId(Guid guid, out int id) => _idByGuid.TryGetValue(guid, out id);

    /// <summary>Mark an id's object as no longer on the canvas. The guid mapping is KEPT so the id can
    /// never be handed to a different object, and so a stale reference reports honestly.</summary>
    public void Retire(int id)
    {
      if (_guidById.ContainsKey(id)) _alive.Remove(id);
    }

    public IdState State(int id)
    {
      if (!_guidById.ContainsKey(id)) return IdState.Unknown;
      return _alive.Contains(id) ? IdState.Alive : IdState.Retired;
    }

    /// <summary>
    /// Reconcile the registry against the objects currently on the canvas: issue ids for guids seen for
    /// the first time, retire ids whose guid has vanished, and revive ids whose guid has returned.
    /// Called once per snapshot, so every projection in this scope speaks the same stable id space.
    /// </summary>
    public void Sync(IEnumerable<Guid> aliveGuids)
    {
      var present = new HashSet<Guid>();
      if (aliveGuids != null)
        foreach (var g in aliveGuids) { present.Add(g); Issue(g); }   // issues or revives

      // Retire anything the canvas no longer holds. Snapshot the key list first - Retire mutates _alive.
      var toRetire = new List<int>();
      foreach (var id in _alive)
        if (!_guidById.TryGetValue(id, out var g) || !present.Contains(g)) toRetire.Add(id);
      foreach (var id in toRetire) _alive.Remove(id);
    }

    // ---- U22: what the model has READ, and the canvas revision it read it at ----------------------------
    //
    // NOT PERSISTED, deliberately: a reading is a fact about this session's conversation. After a restart the model
    // holds no reading from this registry, and the canvas fact already says Dwight cannot tell.

    private readonly Dictionary<int, ReadRecord> _reads = new Dictionary<int, ReadRecord>();
    private string _canvasToken;
    private int _revision;

    /// <summary>The revision of the canvas as the reads can see it: 1 at the first observation, and one higher each time
    /// the token handed in differs from the last. A move leaves the token alone, so it leaves the revision alone.</summary>
    public int ObserveCanvas(string token)
    {
      if (_revision == 0 || !string.Equals(token, _canvasToken, StringComparison.Ordinal))
      {
        _canvasToken = token;
        _revision++;
      }
      return _revision;
    }

    /// <summary>Remember what a read returned for this id. A newer read of the same id replaces the older: the stamp is
    /// the model's LATEST reading of it.</summary>
    public void RecordRead(int id, ComponentStamp stamp, int? inside, int revision)
    {
      if (stamp == null || !_guidById.ContainsKey(id)) return;
      _reads[id] = new ReadRecord { Stamp = stamp, Inside = inside, Revision = revision };
    }

    /// <summary>Every reading held, ascending by id.</summary>
    public List<KeyValuePair<int, ReadRecord>> Reads() =>
      _reads.OrderBy(kv => kv.Key).ToList();

    /// <summary>Every id currently alive, ascending - a deterministic enumeration order for projections.</summary>
    public List<int> AliveIds()
    {
      var ids = new List<int>(_alive);
      ids.Sort();
      return ids;
    }

    // ---- persistence (#229) --------------------------------------------------------------------------
    //
    // WHY THE REGISTRY OUTLIVES THE SESSION. A scaffold map stores AGENT IDS in its anchors, and WP-4
    // builds from that map. Without persistence a reopened map's anchors would resolve against a fresh id
    // space - the same mis-grounding this class exists to prevent, arriving through the one door it left
    // open. ADR-067 already mandates persisting the int-to-guid map per file; this is that slot used for
    // its stated purpose.

    /// <summary>Ids kept across a restart. Generous against a large definition, and only ever exceeded by
    /// LIVE ids, which are never dropped.</summary>
    public const int MaxPersistedIds = 4000;

    /// <summary>
    /// A snapshot to store. Over the cap the OLDEST RETIRED entries go and a live one never does: dropping
    /// a retired id degrades its answer from "that was deleted" to "you invented that" - less informative,
    /// and never wrong - whereas dropping a live one would unbind an id the map still points at.
    /// </summary>
    public AgentIdSnapshot Export(int cap = MaxPersistedIds)
    {
      var alive = new List<int>(_alive);
      var retired = _guidById.Keys.Where(id => !_alive.Contains(id)).ToList();

      var room = Math.Max(0, cap - alive.Count);
      retired.Sort();                                    // ascending = oldest first
      var keptRetired = retired.Skip(Math.Max(0, retired.Count - room));

      var keep = alive.Concat(keptRetired).OrderBy(id => id);

      return new AgentIdSnapshot
      {
        Next = _next,
        Ids = keep.Select(id => new AgentIdEntry { Id = id, Guid = _guidById[id].ToString() }).ToList(),
      };
    }

    /// <summary>
    /// Restore pinned ids into a FRESH registry. Nothing is marked alive - the caller's next
    /// <see cref="Sync"/> against the live document decides that, which is the only honest source for it.
    ///
    /// The counter continues past the highest id restored even if the stored one lags, so a truncated or
    /// hand-edited file can never cause a number to be reused for a different object.
    /// </summary>
    public void Import(AgentIdSnapshot snapshot)
    {
      if (snapshot?.Ids == null) return;

      foreach (var e in snapshot.Ids)
      {
        if (e == null || e.Id <= 0) continue;
        if (!Guid.TryParse(e.Guid, out var guid)) continue;
        if (_guidById.ContainsKey(e.Id) || _idByGuid.ContainsKey(guid)) continue;   // never re-bind
        _guidById[e.Id] = guid;
        _idByGuid[guid] = e.Id;
      }

      var highest = _guidById.Count == 0 ? 0 : _guidById.Keys.Max();
      _next = Math.Max(Math.Max(_next, snapshot.Next), highest + 1);
    }

    private static readonly JsonSerializerOptions Opts = new JsonSerializerOptions
    {
      DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToJson(int cap = MaxPersistedIds)
    {
      try { return JsonSerializer.Serialize(Export(cap), Opts); } catch { return null; }
    }

    /// <summary>Restore from stored JSON. Unreadable input restores NOTHING and is not an error: a lost id
    /// space costs a re-read, while a wrongly-restored one costs a wrong action.</summary>
    public void FromJson(string json)
    {
      if (string.IsNullOrWhiteSpace(json)) return;
      try { Import(JsonSerializer.Deserialize<AgentIdSnapshot>(json, Opts)); } catch { }
    }
  }
}
