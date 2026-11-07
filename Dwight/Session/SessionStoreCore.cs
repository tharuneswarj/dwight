using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dwight.Session
{
  /// <summary>How a store file read turned out. The distinction is the whole safety design.</summary>
  internal enum StoreReadOutcome
  {
    /// <summary>Nothing usable was found (missing, empty, corrupt, truncated, wrong shape). The caller may
    /// safely write over this file - there is nothing in it to lose.</summary>
    Empty,
    /// <summary>A record this build understands.</summary>
    Ok,
    /// <summary>Written by a NEWER Dwight. Reads as empty (we do not guess at a format we do not know) but the
    /// file MUST BE PRESERVED, not overwritten - see the class remarks.</summary>
    FutureVersion,
  }

  internal sealed class StoreReadResult
  {
    public StoreReadOutcome Outcome;
    /// <summary>Never null, and its collections are never null - callers do not null-check.</summary>
    public SessionRecord Record;
  }

  // ---- the wire DTOs. Deliberately NOT the domain types (the DwightCommand carries a live Action<IResultPanel>
  // that cannot be serialized at all), and short-ish names because this file is rewritten on every action.

  internal sealed class StoredOp
  {
    [JsonPropertyName("op")] public string Op { get; set; }
    [JsonPropertyName("args")] public List<string> Args { get; set; }
  }

  internal sealed class StoredMessage
  {
    [JsonPropertyName("who")] public string Who { get; set; }
    [JsonPropertyName("html")] public string Html { get; set; }
    [JsonPropertyName("utc")] public string Utc { get; set; }

    /// <summary>
    /// The author's ORIGINAL text - the user's typed string, or the model's Markdown (WP-3 Stage A).
    /// <see cref="Html"/> is DERIVED from it at append time, by the door that knows the author, so the
    /// two cannot drift: the text shown is the render of the text recorded (#153/#182, sharpened).
    ///
    /// It exists because a chat agent needs the conversation back. <see cref="Html"/> is what was SHOWN,
    /// and the model's half of it is a lossy render - re-feeding rendered HTML would be wasteful and
    /// would slowly teach the model to answer in HTML. So the source is kept beside it.
    ///
    /// NULL means "not part of the conversation": a greeting, a notice, a consent card. Those are UI,
    /// not turns, and the model transcript is built from messages that HAVE a source - so plugin-authored
    /// chrome is excluded by construction rather than by a filter somebody has to remember.
    /// </summary>
    [JsonPropertyName("src")] public string Src { get; set; }

    /// <summary>
    /// The AGENT IDS this message is about (#291) - what a click on it returns you to on the canvas.
    ///
    /// Ids, and only ids. Never a guid, never a coordinate: an anchor resolves through the registry to
    /// alive-in-the-document-right-now, so a message from an hour ago either finds its components or says
    /// they are gone. Storing anything positional would let a stale anchor pan to whatever now occupies
    /// the space, which is the mis-grounding this closes.
    ///
    /// Ints keep WP-1's law intact: a persisted message still cannot carry a mutation or a paid-call offer.
    /// Aliveness is deliberately NOT stored (#245) - the live document is the only honest source for it.
    /// </summary>
    [JsonPropertyName("anchors")] public List<int> Anchors { get; set; }

    /// <summary>
    /// U111: this user line is the plugin's echo of an approval ("Yes, go ahead."), set only by the consent click and the automatic approval. The screen
    /// leaves it out after a receipt that already states the answer, and the model still reads it. A line the user TYPED is never marked, whatever its words, so
    /// typing those exact words is always drawn. Absent from the file unless true; a thread saved before this has no marks, and draws its echoes.
    /// </summary>
    [JsonPropertyName("echo")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool Echo { get; set; }

    /// <summary>Brief DX: the image the person attached to this message, by hash and pixel size, never its bytes. Only a "me" message carries one. Absent on every other message.</summary>
    [JsonPropertyName("img")] public StoredImage Image { get; set; }

    /// <summary>Same preservation rule as its siblings (#167): a newer minor's fields survive an older
    /// build's read-write cycle instead of being silently dropped. Added with the first field this type
    /// ever gained, which is precisely when the absence would have started costing something.</summary>
    [JsonExtensionData] public Dictionary<string, JsonElement> Extra { get; set; }
  }

  /// <summary>Brief DX: what the record holds of an attached image. The file is <c>sessions\&lt;key&gt;\images\&lt;sha256&gt;.png|jpg</c> (<see cref="ChatImageStore"/>).</summary>
  internal sealed class StoredImage
  {
    [JsonPropertyName("sha256")] public string Sha256 { get; set; }
    [JsonPropertyName("type")] public string MediaType { get; set; }
    [JsonPropertyName("w")] public int Width { get; set; }
    [JsonPropertyName("h")] public int Height { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement> Extra { get; set; }
  }

  internal sealed class StoredThread
  {
    [JsonPropertyName("id")] public string Id { get; set; }
    [JsonPropertyName("phase")] public string Phase { get; set; }
    [JsonPropertyName("messages")] public List<StoredMessage> Messages { get; set; }
    [JsonPropertyName("scaffoldMap")] public string ScaffoldMap { get; set; }
    [JsonPropertyName("buildProgress")] public string BuildProgress { get; set; }
    /// <summary>The approval mode this conversation was opened with (Brief AL): "ask", "scripts_and_deletions" or "never". Null in a file
    /// from a build that had no modes, which reads as "ask" - the behaviour that conversation was opened under.</summary>
    [JsonPropertyName("approvalMode")] public string ApprovalMode { get; set; }
    /// <summary>Full control (U90): whether this conversation was opened with it on. Null in a file from a build that had no such switch,
    /// which reads as OFF - the behaviour that conversation was opened under.</summary>
    [JsonPropertyName("fullControl")] public bool? FullControl { get; set; }
    /// <summary>Full control (U90): whether this conversation has already shown a run_script card, so the second one opens folded.</summary>
    [JsonPropertyName("runScriptCardShown")] public bool? RunScriptCardShown { get; set; }
    /// <summary>Brief AO (the chat header): this conversation's own model pin and posture. Empty/null mean "use the Settings default".</summary>
    [JsonPropertyName("modelId")] public string ModelId { get; set; }
    [JsonPropertyName("posture")] public string Posture { get; set; }
    /// <summary>What each turn cost (minor 4). The chat's total is computed from these when read, never stored.
    /// Absent in a file from an older build, which reads as no turns and costs nothing to read.</summary>
    [JsonPropertyName("turns")] public List<StoredSpend> Turns { get; set; }
    /// <summary>Brief EC: the run this chat was opened from (its <see cref="StoredCommand.RunId"/>), so every door from that run finds this one thread. Null for a chat opened on its own, and in a file from an older build.</summary>
    [JsonPropertyName("runKey")] public string RunKey { get; set; }
    /// <summary>Brief EC: the name the chat list shows for a run's chat, the verb and the run's time ("Explain, 14:02"). Null for a chat opened on its own, which is named by what the person asked.</summary>
    [JsonPropertyName("runTitle")] public string RunTitle { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement> Extra { get; set; }
  }

  internal sealed class StoredCommand
  {
    [JsonPropertyName("action")] public string Action { get; set; }
    [JsonPropertyName("subtitle")] public string Subtitle { get; set; }
    /// <summary>"ok" | "failed" | "stopped" - WP-5's failed-run retention, now durable.</summary>
    [JsonPropertyName("state")] public string State { get; set; }
    [JsonPropertyName("sourceGuids")] public List<string> SourceGuids { get; set; }
    [JsonPropertyName("secondaryGuids")] public List<string> SecondaryGuids { get; set; }
    /// <summary>guid -> short-int id, to restore the canvas number badges (ADR-079).</summary>
    [JsonPropertyName("labels")] public Dictionary<string, int> Labels { get; set; }
    [JsonPropertyName("timeUtc")] public string TimeUtc { get; set; }
    [JsonPropertyName("costLine")] public string CostLine { get; set; }
    /// <summary>The run's cost as numbers (minor 4). Null in a file from an older build, and for a run with
    /// no model call.</summary>
    [JsonPropertyName("spend")] public StoredSpend Spend { get; set; }
    /// <summary>#179: the ORIGIN file path this command ran against - the provenance stamp that lets one
    /// DocumentID record hold a file AND its Save-As copies, filtered per active document at view time.</summary>
    [JsonPropertyName("origin")] public string Origin { get; set; }
    /// <summary>"content" | "retry" | "stale" - what a CROSS-SESSION reopen may offer. A persisted record never
    /// carries a mutation offer or a paid-call offer; this records which class the row belongs to.</summary>
    [JsonPropertyName("liveClass")] public string LiveClass { get; set; }
    /// <summary>#182 step 5: a Scaffold row points at its THREAD (in <see cref="SessionRecord.Threads"/>) by id,
    /// so reopening it restores the whole conversation, not a static snapshot. Null for every other command.</summary>
    [JsonPropertyName("threadId")] public string ThreadId { get; set; }
    /// <summary>Brief DO, item 8: the run this row records, which a note offered from it remembers. Null for a chat, whose row is its thread, and in a file from an older build.</summary>
    [JsonPropertyName("runId")] public string RunId { get; set; }
    /// <summary>The replayable panel content. WP-1 defines and round-trips the wire format; the interpreter and
    /// the render-path refactor are step 3, held for review.</summary>
    [JsonPropertyName("script")] public List<StoredOp> Script { get; set; }
    /// <summary>Brief DE: a Teaching lesson's score, right then asked, as numbers. Null when no lesson was asked. Never in a model's payload; no streaks, no totals across files.</summary>
    [JsonPropertyName("score")] public int[] Score { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement> Extra { get; set; }
  }

  internal sealed class SessionRecord
  {
    [JsonPropertyName("schema")] public int Schema { get; set; }
    [JsonPropertyName("minor")] public int Minor { get; set; }
    [JsonPropertyName("documentId")] public string DocumentId { get; set; }
    /// <summary>Human-readable file paths, most recent first. A LABEL ONLY - the path never keys anything.</summary>
    [JsonPropertyName("labels")] public List<string> Labels { get; set; }
    [JsonPropertyName("fingerprint")] public string Fingerprint { get; set; }
    [JsonPropertyName("updatedUtc")] public string UpdatedUtc { get; set; }
    [JsonPropertyName("commands")] public List<StoredCommand> Commands { get; set; }
    [JsonPropertyName("threads")] public List<StoredThread> Threads { get; set; }
    /// <summary>Brief AP: the changes Dwight made to this document, newest first, each with its undo record's id.</summary>
    [JsonPropertyName("changes")] public List<StoredChange> Changes { get; set; }
    /// <summary>Where the plugin itself laid the scribbles of each scaffold region, so the re-space can tell a region the user moved from one they did not.</summary>
    [JsonPropertyName("regionPlaces")] public List<StoredRegionPlace> RegionPlaces { get; set; }
    /// <summary>Brief DC: Dwight notes, oldest first. Plain text a person or Dwight wrote about a component or a point on the canvas.</summary>
    [JsonPropertyName("notes")] public List<StoredNote> Notes { get; set; }
    /// <summary>The large-graph cache (region map, summaries, int&lt;-&gt;guid map). A SLOT ONLY in WP-1: it is
    /// carried opaquely so WP-3 can populate it without a schema migration. Invalidation with no consumer is
    /// invalidation untested, so nothing here writes it.</summary>
    [JsonPropertyName("graph")] public JsonElement? Graph { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement> Extra { get; set; }
  }

  /// <summary>
  /// A DWIGHT NOTE (Brief DC): text a person or Dwight wrote about a component, or about a point on the canvas. It lives in the per-file
  /// record, never in the definition (a Dwight object in the .gh would raise Grasshopper's "Unrecognized Objects" dialog for anyone opening
  /// the file without Dwight, and would change the person's file). Ids, guids and numbers are the plugin's and never enter a model's
  /// payload; a note reaches a model as text under the component's short id (ADR-058).
  /// </summary>
  internal sealed class StoredNote
  {
    [JsonPropertyName("id")] public string Id { get; set; }
    /// <summary>The anchored component's instance guid as a string, or null for a note about a point.</summary>
    [JsonPropertyName("anchor")] public string Anchor { get; set; }
    /// <summary>A canvas point, x then y: the note's place when it has no anchor, and its offset from the anchor when it has one.</summary>
    [JsonPropertyName("at")] public double[] At { get; set; }
    /// <summary>Where the card was first put, as an absolute canvas point: where it is still drawn if its component goes. Null on a note about a point.</summary>
    [JsonPropertyName("last")] public double[] Last { get; set; }
    [JsonPropertyName("text")] public string Text { get; set; }
    /// <summary>"dwight" or "you".</summary>
    [JsonPropertyName("by")] public string By { get; set; }
    [JsonPropertyName("whenUtc")] public string WhenUtc { get; set; }
    /// <summary>The history row's id when Dwight wrote the note, else null.</summary>
    [JsonPropertyName("fromRun")] public string FromRun { get; set; }
    /// <summary>The anchored component's stamp digest when the note was written, else null. What lets a note say it is stale.</summary>
    [JsonPropertyName("stamp")] public string Stamp { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement> Extra { get; set; }
  }

  /// <summary>One object the plugin placed, and every place it has left it. Numbers only, and never in a model's payload (ADR-058).</summary>
  internal sealed class StoredRegionPlace
  {
    [JsonPropertyName("id")] public string Id { get; set; }
    [JsonPropertyName("at")] public List<double[]> At { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement> Extra { get; set; }
  }

  /// <summary>
  /// WHERE THE PLUGIN LEFT A REGION, as the only witness of whether the user has moved it since. Grasshopper keeps no such thing, and a drag leaves nothing
  /// on the object. It holds EVERY place the plugin left the object rather than the latest, because Ctrl-Z of a re-space puts it back on an earlier one, and
  /// that is not the user moving it. An object with no entry, or anywhere else, reads as moved: the safe direction is to leave it alone.
  /// </summary>
  internal static class RegionPlacesCore
  {
    public const int MaxPlaces = 8;
    public const int MaxObjects = 400;
    /// <summary>Pivots round-trip through a float, and a drag is whole units. This is between the two.</summary>
    public const double Tolerance = 0.5;

    /// <summary>Note one place, newest last. Past the bounds the oldest goes, which only makes that object read as moved.</summary>
    public static void Note(List<StoredRegionPlace> places, Guid id, double x, double y)
    {
      if (places == null || IsAt(places, id, x, y)) return;
      var key = id.ToString("N");
      var row = places.FirstOrDefault(p => p?.Id == key);
      if (row == null)
      {
        row = new StoredRegionPlace { Id = key, At = new List<double[]>() };
        places.Add(row);
        if (places.Count > MaxObjects) places.RemoveRange(0, places.Count - MaxObjects);
      }
      row.At = row.At ?? new List<double[]>();
      row.At.Add(new[] { x, y });
      if (row.At.Count > MaxPlaces) row.At.RemoveRange(0, row.At.Count - MaxPlaces);
    }

    public static bool IsAt(IReadOnlyList<StoredRegionPlace> places, Guid id, double x, double y)
    {
      if (places == null) return false;
      var key = id.ToString("N");
      var row = places.FirstOrDefault(p => p?.Id == key);
      return row?.At != null && row.At.Any(a => a != null && a.Length == 2 && Math.Abs(a[0] - x) <= Tolerance && Math.Abs(a[1] - y) <= Tolerance);
    }
  }

  /// <summary>
  /// The GH-free serialization core of the durable per-file store (WP-1; ADR-067, ADR-091).
  /// <c>%APPDATA%\Dwight\sessions\&lt;DocumentID&gt;.json</c>.
  ///
  /// THE ASYMMETRY THAT SHAPES EVERY RULE HERE. <see cref="Dwight.Labelling.LabelCacheStore"/> is a CACHE of a
  /// pure function's results, so it is correctly LOSSY: any doubt at all reads as empty, because the worst case
  /// is one re-bill. This store is not that. It holds the user's own work - their command history and their
  /// Scaffold threads - so the failure costs are reversed, and two rules follow:
  ///
  ///   - "CANNOT READ" STILL MEANS EMPTY, NEVER AN EXCEPTION. A corrupt store must not cost the user their
  ///     Rhino session. Degrading to a cold start is the right trade; throwing is not.
  ///   - "CANNOT READ" DOES NOT LICENCE OVERWRITING. A file from a NEWER Dwight is perfectly good data this
  ///     build simply cannot parse. It reads as <see cref="StoreReadOutcome.FutureVersion"/> so the caller
  ///     preserves it (renamed aside) rather than clobbering it - otherwise running an older Dwight once would
  ///     silently destroy a newer one's threads.
  ///
  /// FORWARD COMPATIBILITY IS PRESERVATION, NOT JUST TOLERANCE. Unknown fields within the same major are not
  /// merely ignored on read - they are captured and RE-EMITTED on write. Tolerating-then-dropping would delete
  /// a newer minor's data on the first save by an older build, which is the same silent loss in slower motion.
  /// ONE INHERENT CAVEAT, on the record: preserved extension data can be STALE relative to the siblings an
  /// older build legitimately mutated - a preserved scaffoldMap may reference a message that older build's cap
  /// trimmed. That is intrinsic to round-tripping fields you cannot interpret, and strictly better than
  /// dropping them; a NEWER reader must therefore treat re-read extension fields as POSSIBLY STALE and
  /// reconcile them against the data it does understand.
  ///
  /// THE SESSIONS FOLDER IS COPY-PORTABLE BY CONSTRUCTION, AND HOLDS NO SECRETS. Records reconnect by the
  /// file-borne DocumentID and never by a path, so copying the folder to another machine reattaches history to
  /// the same definitions wherever they sit; and nothing here is confidential - API keys live in the OS
  /// credential store (ADR-011), never in this file. That is a property, NOT a feature: naive folder-syncing
  /// can never be the team-sync mechanism (concurrent writers, no merge), which stays v2+ per ADR-067's
  /// export/sidecar clause.
  ///
  /// The GH read (DocumentID, the live document, the file I/O) is the thin shim above this; everything that
  /// decides anything lives here and is unit-tested headlessly.
  /// </summary>
  internal static class SessionStoreCore
  {
    /// <summary>Breaking format changes. A HIGHER major is preserved, never parsed and never overwritten.</summary>
    public const int SchemaMajor = 1;
    /// <summary>Additive changes within a major. A higher minor is read normally; its unknown fields survive.
    /// 1: <see cref="StoredMessage.Src"/> (WP-3 Stage A) - the author's original text beside the rendered
    /// HTML, so a reopened conversation can be handed back to the model.</summary>
    /// 2: <see cref="StoredMessage.Anchors"/> (#291).
    /// 3: a new VALUE in an existing field - <see cref="StoredMessage.Who"/> may be "report" (Phase-9 U4): the
    /// plugin's own account of a change it applied on the user's approval. Rendered as Dwight, mapped into the
    /// USER turn of the model transcript.
    ///
    /// THE BUMP IS A DECLARATION, NOT A GATE. The read path gates on the MAJOR only (FutureVersion), so this
    /// changes no behaviour; it is how a reader learns the file may hold a value an older build renders
    /// differently. A version number that gates nothing is still worth incrementing - do not remove it as dead.
    ///
    /// THE ACCEPTED RESIDUAL (the architect's ruling, not engineered around): an OLDER build reading a newer
    /// file renders a report as a user bubble and maps it as an assistant turn. It is a downgrade scenario,
    /// needs a rollback, and touches only threads that used set_value; guarding the build everyone runs to
    /// protect the one almost nobody will would cost more than it buys. It is cosmetic rather than misleading
    /// because the PROVENANCE IS IN THE TEXT ("[Applied by Dwight on the user's approval] ..."), not only in
    /// this field - a consumer that loses the metadata still gets the truth.
    /// 4: <see cref="StoredCommand.Spend"/> and <see cref="StoredThread.Turns"/> - what a run and a chat turn
    /// cost, as numbers (tokens, cents). Additive; an older file reads with none.
    /// 5: <see cref="SessionRecord.Changes"/> - the changes Dwight made to the document, each with the id of its undo
    /// record, for History (Brief AP). Additive; an older file reads with none, and a change without an id reads as
    /// "position unknown".
    /// 6: <see cref="StoredChange.ThreadId"/> and <see cref="StoredChange.Done"/> - the chat a change came from, and the sentence that says
    /// what ran (Brief BQ). Additive; an older change reads with neither, groups under its kind alone, and has its sentence mapped on reading.
    /// 7: <see cref="SessionRecord.RegionPlaces"/> - where the plugin laid each scaffold region's scribbles. Additive; an older file reads with none, and a region
    /// with no entry reads as moved, so it is left where it is.
    /// 8: <see cref="SessionRecord.Notes"/> - Dwight notes (Brief DC), each anchored to a component or a point. Additive; an older file reads with none.
    /// 9: <see cref="StoredCommand.RunId"/> - the run a row records, the same id its ledger line carries, so a note can remember where it came from (Brief DO). Additive; an older row reads with none.
    /// 10: <see cref="StoredMessage.Image"/> - an image the person attached, by hash and size, with its file in the images folder (Brief DX). Additive; an older file reads with none.
    /// 11: <see cref="StoredThread.RunKey"/> and <see cref="StoredThread.RunTitle"/> - the run a chat was opened from and the name it shows (Brief EC). Additive; an older thread reads with neither and is a chat of its own.
    public const int SchemaMinor = 11;

    /// <summary>Brief DX: the images of a document's threads sit in this folder under a folder named for its key, beside its record. Files, so the record stays small and the sessions folder stays copy-portable.</summary>
    public const string ImagesFolder = "images";

    public static string ImagesDir(string baseDir, string key) => System.IO.Path.Combine(baseDir, key, ImagesFolder);

    /// <summary>Commands kept per file. The in-memory dock showed 10; durable history is worth more than that
    /// to scroll back through, while staying small enough to read on document open without a stutter.</summary>
    public const int MaxCommands = 50;
    public const int MaxThreads = 20;
    /// <summary>Messages kept per thread. Generous - a Scaffold conversation is the artifact WP-1 exists to
    /// protect, so this bound is a runaway guard, not a working limit.</summary>
    public const int MaxMessagesPerThread = 500;
    /// <summary>Turn-cost entries kept per thread; a runaway guard, not a working limit.</summary>
    public const int MaxTurnsPerThread = 1000;

    /// <summary>
    /// The SIZE bound per message (WP-3 Stage 0; the thrice-deferred #170/#182 item). The count cap above
    /// guards a runaway number of messages; this guards a runaway SIZE, which is the axis the agent
    /// actually moves.
    ///
    /// BASIS: a realistic agent reply is 2-4k characters and a rendered plan card with a dozen regions is
    /// under 10k, so 32k is roughly an order of magnitude above the working case - a message that hits it
    /// is an anomaly, not a long answer. Over it, the message is TRUNCATED with a visible marker
    /// (<see cref="ChatBudget"/>), never dropped.
    /// </summary>
    public const int MaxMessageChars = 32_000;

    /// <summary>
    /// The bound on one message's anchors (#291, ADR-035 at the store). A message naming sixty-four
    /// components is already past the point where "click to go back to it" means anything, and an unbounded
    /// list is a file-size and parse cost the user never asked for. Trimmed from the TAIL: the ids a message
    /// leads with are the ones it is actually about.
    /// </summary>
    public const int MaxAnchorsPerMessage = 64;

    /// <summary>
    /// The SIZE bound per thread, head-trimmed so the live tail survives.
    ///
    /// BASIS: ~128k characters is a few hundred typical messages - a long working conversation - while
    /// bounding what a document open must parse. THE RECORD-LEVEL WORST CASE, stated rather than left to
    /// be discovered: <see cref="MaxThreads"/> x this = ~2.5M characters of message text per file, plus
    /// the command scripts under <see cref="MaxCommands"/>.
    ///
    /// ONE DELIBERATE EXEMPTION: <c>scaffoldMap</c> and <c>buildProgress</c> live OUTSIDE the message list
    /// (#167) and are NOT trimmed - losing the plan to a size cap would defeat the store's purpose. Their
    /// real size is the open unknown in that worst case, and it is measured at WP-3's runtime gate; these
    /// constants are re-cut once, on that measurement, if it contradicts them.
    /// </summary>
    public const int MaxThreadChars = 128_000;

    private static readonly JsonSerializerOptions Opts = new JsonSerializerOptions
    {
      WriteIndented = false,
      DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Parse a store file. Never throws, whatever the input. See the class remarks for why the outcome matters
    /// as much as the record.
    /// </summary>
    public static StoreReadResult Read(string json)
    {
      if (string.IsNullOrWhiteSpace(json)) return EmptyResult(StoreReadOutcome.Empty);

      SessionRecord rec;
      try
      {
        rec = JsonSerializer.Deserialize<SessionRecord>(json, Opts);
      }
      catch
      {
        return EmptyResult(StoreReadOutcome.Empty);      // corrupt or truncated: cold start, never a crash
      }

      if (rec == null) return EmptyResult(StoreReadOutcome.Empty);

      // A newer Dwight wrote this. Report it as empty so nothing is guessed at, and flag it so the caller
      // preserves the file instead of replacing it.
      if (rec.Schema > SchemaMajor) return EmptyResult(StoreReadOutcome.FutureVersion);

      Normalize(rec);
      return new StoreReadResult { Outcome = StoreReadOutcome.Ok, Record = rec };
    }

    /// <summary>Serialize a record, stamping the current schema and applying the caps. A null record writes a
    /// valid empty record rather than failing - a caller mid-teardown must not be able to produce broken JSON.</summary>
    public static string Write(SessionRecord rec)
    {
      rec = rec ?? new SessionRecord();
      rec.Schema = SchemaMajor;
      rec.Minor = SchemaMinor;
      Normalize(rec);
      return JsonSerializer.Serialize(rec, Opts);
    }

    /// <summary>
    /// #165: is there anything here worth a file? A store file is written only once it has content, so an
    /// untitled scratch document the user opens and abandons does not litter the sessions folder.
    /// </summary>
    public static bool HasContent(SessionRecord rec) =>
      rec != null &&
      ((rec.Commands != null && rec.Commands.Count > 0) ||
       (rec.Threads != null && rec.Threads.Count > 0) ||
       (rec.Changes != null && rec.Changes.Count > 0) ||
       (rec.Notes != null && rec.Notes.Count > 0));

    // ---------------------------------------------------------------- internals

    private static StoreReadResult EmptyResult(StoreReadOutcome outcome)
    {
      var rec = new SessionRecord { Schema = SchemaMajor, Minor = SchemaMinor };
      Normalize(rec);
      return new StoreReadResult { Outcome = outcome, Record = rec };
    }

    /// <summary>
    /// Bring a record to the shape callers may rely on: no null collections, no unusable entries, and within
    /// the caps. Applied on BOTH read and write, so a hand-edited file and a freshly-built record are held to
    /// exactly one standard.
    /// </summary>
    private static void Normalize(SessionRecord rec)
    {
      rec.Labels = (rec.Labels ?? new List<string>())
        .Where(p => !string.IsNullOrWhiteSpace(p)).ToList();

      // Commands are newest-first, so the cap trims the TAIL - the user keeps what they just did.
      rec.Commands = (rec.Commands ?? new List<StoredCommand>())
        .Where(c => c != null && !string.IsNullOrWhiteSpace(c.Action))   // an action-less row can never render
        .Take(MaxCommands)
        .ToList();

      foreach (var c in rec.Commands)
      {
        c.Action = DwightVerbs.Canonical(c.Action);   // read AND write: an earlier build's "Scaffold" is Build, and is never written back as Scaffold (#1083)
        c.Subtitle = DwightVerbs.CanonicalSubtitle(c.Action, c.Subtitle);   // and an earlier build's "on canvas" row is a Walk (Brief DA)
        c.SourceGuids = CleanGuids(c.SourceGuids);
        c.SecondaryGuids = CleanGuids(c.SecondaryGuids);
        c.Labels = CleanLabels(c.Labels);
        c.Script = (c.Script ?? new List<StoredOp>())
          .Where(o => o != null && !string.IsNullOrWhiteSpace(o.Op))
          .Select(o => { o.Args = (o.Args ?? new List<string>()).Where(a => a != null).ToList(); return o; })
          .ToList();
      }

      rec.Changes = HistoryCore.Normalize(rec.Changes);   // Brief AP: newest first, within the cap

      // Brief DC: notes, oldest first. A note with no id or no text can never be shown or deleted; one with neither an anchor nor a point can
      // never be drawn; an over-long text is a hand edit (the writer refuses it), cut here rather than trusted. The oldest go past the cap.
      rec.Notes = (rec.Notes ?? new List<StoredNote>())
        .Where(n => n != null && !string.IsNullOrWhiteSpace(n.Id) && !string.IsNullOrWhiteSpace(n.Text))
        .Select(n =>
        {
          if (!string.IsNullOrEmpty(n.Anchor) && !Guid.TryParse(n.Anchor, out _)) n.Anchor = null;
          if (n.At != null && (n.At.Length != 2 || n.At.Any(v => double.IsNaN(v) || double.IsInfinity(v)))) n.At = null;
          if (n.Last != null && (n.Last.Length != 2 || n.Last.Any(v => double.IsNaN(v) || double.IsInfinity(v)))) n.Last = null;
          if (n.Text.Length > DwightNotes.MaxChars) n.Text = n.Text.Substring(0, DwightNotes.MaxChars);
          n.By = DwightNotes.NormaliseBy(n.By);
          return n;
        })
        .Where(n => n.Anchor != null || n.At != null)
        .ToList();
      if (rec.Notes.Count > DwightNotes.MaxNotes) rec.Notes = rec.Notes.Skip(rec.Notes.Count - DwightNotes.MaxNotes).ToList();

      rec.Threads = (rec.Threads ?? new List<StoredThread>())
        .Where(t => t != null && !string.IsNullOrWhiteSpace(t.Id))
        .Take(MaxThreads)
        .ToList();

      foreach (var t in rec.Threads)
      {
        // Brief AL: a stored mode is read through the one normaliser, so a hand-edited value is the default; an ABSENT one stays
        // absent (it is a thread from before modes existed), and the reader treats it as "ask".
        if (t.ApprovalMode != null) t.ApprovalMode = Dwight.Agent.ApprovalMode.Normalise(t.ApprovalMode);
        t.ModelId = string.IsNullOrWhiteSpace(t.ModelId) ? null : t.ModelId.Trim();   // Brief AO: a blank pin is no pin
        t.Posture = string.IsNullOrWhiteSpace(t.Posture) ? null : t.Posture.Trim().ToLowerInvariant();
        var msgs = (t.Messages ?? new List<StoredMessage>()).Where(m => m != null).ToList();

        // Per-message SIZE first, so the thread bound below measures what is actually kept. An over-long
        // message is truncated with a marker, never dropped - a silently missing turn would make the
        // conversation discontinuous, which is the one thing this store exists to prevent.
        foreach (var m in msgs)
        {
          if (m.Html != null) m.Html = ChatBudget.TrimHtml(m.Html, MaxMessageChars);
          // The source is plain text, so it needs no tag balancing - but it is real bytes in the file
          // and real tokens on the next turn's wire, so it is bounded on the same budget.
          if (m.Src != null) m.Src = ChatBudget.TrimText(m.Src, MaxMessageChars);
          if (m.Anchors != null && m.Anchors.Count > MaxAnchorsPerMessage)
            m.Anchors = m.Anchors.Take(MaxAnchorsPerMessage).ToList();
        }

        // Messages are chronological, so the cap trims the HEAD: a long conversation keeps its live tail
        // rather than its opening.
        if (msgs.Count > MaxMessagesPerThread) msgs = msgs.Skip(msgs.Count - MaxMessagesPerThread).ToList();

        // Then the thread's own SIZE bound, head-trimmed the same way and for the same reason.
        msgs = ChatBudget.TrimThread(msgs, MaxThreadChars);

        t.Messages = msgs;

        // Turn spend: a runaway guard only (a chat is not 1000 turns), keeping the tail like the messages do.
        if (t.Turns != null)
        {
          t.Turns = t.Turns.Where(x => x != null).ToList();
          if (t.Turns.Count > MaxTurnsPerThread) t.Turns = t.Turns.Skip(t.Turns.Count - MaxTurnsPerThread).ToList();
        }
      }
    }

    /// <summary>Drop anything that is not a real guid. A malformed id could never resolve to a component, and
    /// carrying it forward would only produce a confusing "not found" later (ADR-058 groundedness).</summary>
    private static List<string> CleanGuids(List<string> raw) =>
      (raw ?? new List<string>())
        .Where(g => !string.IsNullOrWhiteSpace(g) && Guid.TryParse(g, out _))
        .ToList();

    private static Dictionary<string, int> CleanLabels(Dictionary<string, int> raw)
    {
      var clean = new Dictionary<string, int>();
      if (raw == null) return clean;
      foreach (var kv in raw)
        if (!string.IsNullOrWhiteSpace(kv.Key) && Guid.TryParse(kv.Key, out _)) clean[kv.Key] = kv.Value;
      return clean;
    }
  }
}
