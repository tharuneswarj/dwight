using System;
using System.Collections.Generic;
using System.Linq;
using Grasshopper;
using Grasshopper.Kernel;
using Dwight.UI;

namespace Dwight.Session
{
  /// <summary>One recorded Dwight action against a file: enough to re-open its panel without re-running
  /// (the <see cref="Script"/> replay) and to re-highlight its source selection (ADR-049; spec §6).</summary>
  internal sealed class DwightCommand
  {
    public string Action;                 // "Explain", "Reorganise", ...
    public string Subtitle = "";          // e.g. "15 components"
    public List<Guid> SourceGuids = new List<Guid>();
    public List<Guid> SecondaryGuids;     // the lighter tier on reopen (Diagnose chain behind the target) - D4
    public Dictionary<Guid, int> Labels;  // guid -> short-int id, to restore the canvas number badges (ADR-079)
    public DateTime Time = DateTime.Now;
    public string CostLine = "";
    /// <summary>Brief DE: the lesson's score, right then asked, or null.</summary>
    public int[] Score;
    /// <summary>The run's cost as numbers (tokens, cents), persisted. Null for a run with no model call and
    /// for a record from a build that did not keep it.</summary>
    public StoredSpend Spend;
    /// <summary>Brief CT: this run's ledger line was written when its paid calls ended, so
    /// <see cref="CommandHistory.Record"/> must not write it again. The row still holds <see cref="Spend"/>.
    /// In memory only: a row read back from the store never passes through Record.</summary>
    public bool SpendLedgered;
    /// <summary>WP-5: "ok" | "failed" | "stopped". A run that did not land is still recorded ("nothing is lost"
    /// includes the failures) but must never read in the dock like one that did.</summary>
    public string State = "ok";

    /// <summary>#179: the ORIGIN file path this command ran against. One DocumentID record can hold a file and
    /// its Save-As copies (they share the id); the dock view filters by this so a clean file and its messy copy
    /// do not interleave. "" until the doc is first saved, then re-stamped by FilePathChanged.</summary>
    public string Origin = "";

    /// <summary>#182 step 5: a Scaffold row points at its persisted thread by id; reopening it restores the whole
    /// conversation (via <see cref="CommandHistory.ThreadMessages"/>), not a static snapshot. Null otherwise.</summary>
    public string ThreadId;
    public string RunId;                  // Brief DO: what a note offered from this row remembers

    /// <summary>
    /// The result's CONTENT, as replayable data (WP-1 step 3). This is the only part that can reach disk, and it
    /// is strings all the way down - so a persisted record cannot carry a mutation or a paid-call offer.
    /// </summary>
    public List<StoredOp> Script;

    /// <summary>
    /// The result's LIVE AFFORDANCES - the switcher, Undo/Re-apply, click-to-locate, per-chain re-diagnose,
    /// Retry - as the original closure over live state (a solved graph, an apply state, a document).
    ///
    /// IN-MEMORY ONLY, AND THAT IS THE SAFETY DESIGN: it is not part of the wire format, so after a restart it
    /// is null BY CONSTRUCTION and a reopened record renders <see cref="Script"/> alone. A control bound to a
    /// canvas that no longer exists in that form is exactly the panel-contradicts-the-canvas failure this
    /// project does not ship (7.0 WP-1.3).
    ///
    /// When present it renders INSTEAD OF the script, not after it (#169 §7a): step 3's bar is behaviour-
    /// identity, so the live path stays byte-for-byte what it was.
    /// </summary>
    public Action<IResultPanel> Live;
  }

  /// <summary>
  /// Per-file command history (spec §6, ADR-049), made DURABLE in WP-1 step 4 (ADR-067).
  ///
  /// Keyed by the file-borne <see cref="DocumentKey"/> (the spike proved <c>DocumentID</c> is stamped in the
  /// .gh, exists at birth, and is re-keyed by nothing outside cluster internals), so history survives a save, a
  /// reopen, a restart, and a move/rename - and a Save-As shares its lineage's record, exactly the one-record-
  /// per-DocumentID design #166 accepted.
  ///
  /// The store persists <see cref="DwightCommand.Script"/> (the replayable content), never <see cref="DwightCommand.Live"/>
  /// (the in-memory affordances), so a reopened-after-restart record renders its content and its honest markers
  /// but offers no stale mutation. The dock (Plan 3) subscribes to <see cref="Changed"/>.
  ///
  /// TWO OPEN DOCUMENTS SHARING ONE ID (a file and its Save-As, both open): a single process, a single shared
  /// in-memory list, and an ATOMIC write mean their histories merge into the one record with no race and no
  /// corruption - which is #166's accepted shared-lineage design, reached without the "second doc memory-only"
  /// mechanism whose only job was to prevent a race that a shared list does not have. (A true second PROCESS on
  /// the same file is undetectable via <c>DocumentServer</c>; the atomic write bounds that to last-write-wins,
  /// never a torn file - the honest v1 limit, matching ADR-067's cross-machine-is-v2 clause.)
  /// </summary>
  internal static class CommandHistory
  {
    /// <summary>
    /// The record key meaning THERE IS NO DOCUMENT - not "a document we could not identify" (#310).
    ///
    /// The distinction is provable, not stylistic. <see cref="DocumentFingerprint.Compute"/> hashes even an
    /// empty token list, so it never returns blank; <see cref="DocumentKey.For"/> returns null only ON a
    /// blank fingerprint. Every real document is therefore identifiable - untitled, empty, or a foreign .gh
    /// with no anchor - and this key is reachable only from a null <c>GH_Document</c>.
    ///
    /// IT IS NAMED BECAUSE THE BARE LITERAL COST US A ROUND. Two conditions could produce it in principle,
    /// so each consumer read it as whichever one it had in mind: four sites treated it as "unidentifiable,
    /// so refuse", a case that has never once occurred, and in doing so refused the case that does. A
    /// sentinel that two conditions can produce will be read as whichever the reader was thinking of - so it
    /// carries its proof here, and every site that tests it inherits the proof instead of an assumption.
    ///
    /// What it gates: DISK, always - <see cref="ListFor"/> skips the load and <see cref="Persist"/> skips
    /// the save. What it must never gate: MEMORY. See <see cref="KeyFor"/>.
    /// </summary>
    private const string NoDocumentKey = "(none)";

    private static readonly int Cap = SessionStoreCore.MaxCommands;   // durable now, so the dock keeps more than the old 10

    // in-memory command lists, keyed by DocumentKey; and the on-disk record per key (kept so a command write
    // preserves the record's OTHER sections - threads (step 5), the graph slot, and forward-compat extension
    // data - instead of overwriting the whole file with commands alone).
    private static readonly Dictionary<string, List<DwightCommand>> _byFile = new Dictionary<string, List<DwightCommand>>();
    private static readonly Dictionary<string, SessionRecord> _record = new Dictionary<string, SessionRecord>();
    private static readonly HashSet<string> _loaded = new HashSet<string>();
    private static bool _hooked;

    /// <summary>Raised when the active file's history changes (the dock subscribes in Plan 3).</summary>
    public static event Action Changed;

    public static void Record(GH_Document doc, DwightCommand cmd)
    {
      if (cmd == null) return;
      HookOnce();
      var key = KeyFor(doc);
      cmd.Origin = doc?.FilePath ?? "";             // #179: stamp which file this command ran against
      var list = ListFor(doc, key);
      list.Insert(0, cmd);                          // newest first
      while (list.Count > Cap) list.RemoveAt(list.Count - 1);
      Persist(doc, key, list);
      // Brief AK: the central ledger takes its line here, from the same spend the session row just persisted.
      // Brief CT: unless it was already written, when the run's paid calls ended.
      try { if (SpendLedgerCore.RecordWritesLine(cmd.Spend, cmd.SpendLedgered)) SpendLedger.RecordVerb(doc, cmd.Action, cmd.Time, cmd.Spend); } catch { }
      try { Changed?.Invoke(); } catch { }
    }

    public static IReadOnlyList<DwightCommand> ForActiveFile()
    {
      var doc = Instances.ActiveCanvas?.Document;
      if (doc == null) return Array.Empty<DwightCommand>();
      return ListFor(doc, KeyFor(doc));
    }

    /// <summary>One row of the provenance-aware dock view (#179).</summary>
    internal readonly struct CommandView
    {
      public readonly DwightCommand Cmd;
      /// <summary>null for the active document's OWN rows; otherwise the display name to render as
      /// "from &lt;name&gt;" - a row inherited from a Save-As sibling of this file.</summary>
      public readonly string From;
      public CommandView(DwightCommand cmd, string from) { Cmd = cmd; From = from; }
    }

    /// <summary>The active file's history AS THE DOCK SHOULD SHOW IT (#179 Option C): while 2+ documents share
    /// this DocumentID and are open, only the active doc's OWN rows; with one open, the full lineage with
    /// foreign rows labelled. The disk record is unchanged - this is reversible display logic over it.</summary>
    public static IReadOnlyList<CommandView> ForActiveFileView()
    {
      var doc = Instances.ActiveCanvas?.Document;
      if (doc == null) return Array.Empty<CommandView>();
      var list = ListFor(doc, KeyFor(doc));
      var myOrigin = doc.FilePath ?? "";
      var multiLive = CountLiveWithSameId(doc) >= 2;

      var views = new List<CommandView>();
      foreach (var c in list)
      {
        var (show, foreign) = ProvenanceView.Decide(c.Origin, myOrigin, multiLive);
        if (!show) continue;
        views.Add(new CommandView(c, foreign ? OriginDisplayName(c.Origin) : null));
      }
      return views;
    }

    /// <summary>How many OPEN documents carry this document's DocumentID (itself included). &gt;=2 means a
    /// Save-As copy is open beside it. An anchorless doc (no id) is keyed by content and has no sibling.</summary>
    private static int CountLiveWithSameId(GH_Document doc)
    {
      if (doc == null) return 0;
      var id = doc.DocumentID;
      if (id == Guid.Empty) return 1;
      var n = 0;
      try { foreach (GH_Document d in Instances.DocumentServer) if (d != null && d.DocumentID == id) n++; }
      catch { return 1; }
      return n < 1 ? 1 : n;
    }

    private static string OriginDisplayName(string origin)
    {
      if (string.IsNullOrWhiteSpace(origin)) return "an unsaved file";
      try { return System.IO.Path.GetFileName(origin); } catch { return origin; }
    }

    /// <summary>The dock's per-file token; also the store key. The path is deliberately NOT part of it (a move
    /// or rename must not re-key). <see cref="NoDocumentKey"/> when there is no document, so the dock has a
    /// stable empty state.</summary>
    /// <summary>Brief DX: where this document's attached images are kept, or null when there is nowhere: the no-document bucket gates disk (#303), so it has no folder either.</summary>
    internal static string ImagesDirFor(GH_Document doc)
    {
      var key = KeyFor(doc);
      return key == NoDocumentKey ? null : SessionStore.ImagesDir(key);
    }

    internal static string KeyFor(GH_Document doc)
    {
      if (doc == null) return NoDocumentKey;
      var key = DocumentKey.For(doc.DocumentID, doc.DocumentID == Guid.Empty ? Fingerprint(doc) : null);
      return key ?? NoDocumentKey;   // unreachable for a real document - see NoDocumentKey

      // THE RULE THIS KEY OBEYS (#303): it is a real in-memory bucket, not a refusal sentinel. It gates
      // DISK - and only disk. Nothing that touches MEMORY may gate on it. #163/#167 chose memory-only as a
      // DEGRADATION - losing a session is recoverable, attaching the wrong file's history is not - and a
      // degradation that discards the conversation is not the thing that was chosen; it is a failure
      // wearing the name of a fallback.
      //
      // What the key provably MEANS, and why it is named rather than spelled: see NoDocumentKey.
    }

    // ---------------------------------------------------------------- in-memory + disk

    /// <summary>The in-memory list for a key, loading the durable record from disk on first touch.</summary>
    private static List<DwightCommand> ListFor(GH_Document doc, string key)
    {
      if (_byFile.TryGetValue(key, out var existing) && _loaded.Contains(key)) return existing;

      var list = existing ?? new List<DwightCommand>();
      if (!_byFile.ContainsKey(key)) _byFile[key] = list;

      if (!_loaded.Contains(key))
      {
        _loaded.Add(key);   // set FIRST: a failed load must not retry on every access
        var rec = key == NoDocumentKey ? null : SessionStore.Load(key);   // DISK gate
        if (rec != null)
        {
          _record[key] = rec;
          foreach (var s in rec.Commands ?? new List<StoredCommand>()) list.Add(FromStored(s));   // newest-first on disk
          while (list.Count > Cap) list.RemoveAt(list.Count - 1);
          MigrateBlankOrigins(doc, key, list);
        }
      }
      return list;
    }

    /// <summary>
    /// #180: rows recorded BEFORE provenance existed carry no origin. Left blank, they read as FOREIGN to a
    /// saved document (origin "" != its path), so the moment a same-id Save-As copy is opened beside it the
    /// active doc's OWN history VANISHES from its dock - the exact bug Tharun hit. On load, a saved document
    /// claims its record's blank-origin rows as its own by stamping them with its path.
    ///
    /// For genuinely un-attributed ancestor rows (no file ever recorded), the FIRST saved document to load the
    /// record claims them; that is arbitrary only for rows that never had an origin, is stable once persisted,
    /// and never steals a row already attributed to a sibling (only blanks are touched). An unsaved document
    /// has no path to claim with - and no same-id sibling can exist - so its blanks are left alone.
    /// </summary>
    private static void MigrateBlankOrigins(GH_Document doc, string key, List<DwightCommand> list)
    {
      var path = doc?.FilePath;
      if (string.IsNullOrEmpty(path)) return;
      var changed = false;
      foreach (var c in list) if (string.IsNullOrEmpty(c.Origin)) { c.Origin = path; changed = true; }
      if (changed) Persist(doc, key, list);
    }

    private static SessionRecord GetOrCreateRecord(string key)
    {
      if (!_record.TryGetValue(key, out var rec) || rec == null) { rec = new SessionRecord(); _record[key] = rec; }
      return rec;
    }

    /// <summary>Write the current list back, preserving the on-disk record's other sections.</summary>
    private static void Persist(GH_Document doc, string key, List<DwightCommand> list)
    {
      if (!SessionStore.Enabled || key == NoDocumentKey) return;   // DISK gate: the ONLY caller of Save

      var rec = GetOrCreateRecord(key);
      rec.DocumentId = doc?.DocumentID.ToString();
      rec.Fingerprint = doc != null && doc.DocumentID == Guid.Empty ? DocumentFingerprint.Compute(Tokens(doc)) : rec.Fingerprint;
      rec.Labels = MergeLabel(rec.Labels, doc?.FilePath);
      rec.UpdatedUtc = DateTime.UtcNow.ToString("o");
      rec.Commands = list.Select(ToStored).ToList();   // threads / graph / extension-data on `rec` are untouched
      SessionStore.Save(key, rec);
    }

    // ---------------------------------------------------------------- Scaffold threads (WP-1 step 5, #182)
    //
    // Threads ride the SAME per-file record as commands. Persist rebuilds only Commands, so mutating a thread
    // and calling Persist saves it while leaving commands intact (the step-1 preservation test); the message cap
    // + head-trim (keep the live tail, never the scaffoldMap/buildProgress, which sit OUTSIDE the message list)
    // are enforced once, in SessionStoreCore.Normalize, on every write - so appends need no separate trim.

    /// <summary>Start a thread and seed its first (greeting) message. Idempotent by id. The greeting is
    /// plugin chrome, so it carries no source and is not part of the model's conversation.</summary>
    public static void StartThread(GH_Document doc, string threadId, string who, string html, string approvalMode = null, bool? fullControl = null)
    {
      if (string.IsNullOrEmpty(threadId)) return;
      HookOnce();
      var t = EnsureThread(doc, threadId);
      if (t == null) return;
      // Brief AL: the mode is copied from the Settings default ONCE, when the thread is opened, and persisted with it. An existing
      // thread keeps the one it has - this never overwrites.
      if (t.ApprovalMode == null && approvalMode != null) t.ApprovalMode = Dwight.Agent.ApprovalMode.Normalise(approvalMode);
      // Full control (U90): copied from the Settings default once, here, and kept with the thread.
      if (t.FullControl == null && fullControl != null) t.FullControl = fullControl;
      if (!string.IsNullOrEmpty(html)) t.Messages.Add(Msg(who, html, null));
      SaveThreads(doc);
    }

    /// <summary>Brief BQ: the user spoke in this chat. Its row moves to the top with its time updated, and its thread moves to the front of the
    /// threads, so the cap that trims the oldest tail trims what has been quiet longest. Verb rows do not move; opening a chat does not call this.</summary>
    public static void ThreadSpoke(GH_Document doc, string threadId)
    {
      if (string.IsNullOrEmpty(threadId)) return;
      var key = KeyFor(doc);
      var list = ListFor(doc, key);
      var moved = HistoryCore.MoveToFront(list, c => c.ThreadId == threadId);
      if (moved) list[0].Time = DateTime.Now;
      if (_record.TryGetValue(key, out var rec)) HistoryCore.MoveToFront(rec?.Threads, t => t != null && t.Id == threadId);
      if (!moved) return;
      Persist(doc, key, list);
      try { Changed?.Invoke(); } catch { }
    }

    /// <summary>Brief BQ: a chat's name, computed from the thread; null when the thread is gone from the store.</summary>
    public static string ThreadTitle(GH_Document doc, string threadId) => HistoryCore.ThreadName(FindThread(doc, threadId));

    /// <summary>Brief EC: the chat this run already has, or null.</summary>
    public static string ThreadForRun(GH_Document doc, string runKey)
    {
      if (string.IsNullOrEmpty(runKey)) return null;
      var key = KeyFor(doc);
      ListFor(doc, key);   // as FindThread: the record is read before it is asked
      return _record.TryGetValue(key, out var rec) ? HistoryCore.ThreadOfRun(rec?.Threads, runKey) : null;
    }

    /// <summary>Brief EC: this chat was opened from that run, and is named for it. Set once, when the chat is made.</summary>
    public static void SetThreadRun(GH_Document doc, string threadId, string runKey, string title)
    {
      var t = FindThread(doc, threadId);
      if (t == null || string.IsNullOrEmpty(runKey)) return;
      t.RunKey = runKey;
      t.RunTitle = title;
      SaveThreads(doc);
    }

    /// <summary>Append one message to a thread and persist. The single record-side of the AppendBoth primitive
    /// (#182 R1): the on-screen append and this append are one call, so the two can never drift.
    /// <paramref name="src"/> is the author's original text (WP-3), null for plugin-authored chrome.</summary>
    public static bool AppendThreadMessage(GH_Document doc, string threadId, string who, string html,
                                           string src = null, IReadOnlyList<int> anchors = null, bool echo = false, StoredImage image = null)
    {
      if (string.IsNullOrEmpty(threadId)) return false;
      HookOnce();
      var t = EnsureThread(doc, threadId);
      // #296: RETURNS whether it recorded. It used to return void, so a document with no per-file record
      // discarded the message and the caller could not tell - the screen kept it, the record did not, and
      // the next transcript was empty. A write that can fail must say so.
      if (t == null) return false;
      var m = Msg(who, html, src);
      // #291: ids only, and only when there are some - an empty list would be noise in every stored message.
      if (anchors != null && anchors.Count > 0) m.Anchors = anchors.ToList();
      m.Echo = echo;
      m.Image = image;   // Brief DX: the hash and the size. The file is already in the images folder.
      t.Messages.Add(m);
      SaveThreads(doc);
      return true;
    }

    /// <summary>
    /// Brief BG: rewrite the plugin messages of a thread in place - an answered card becomes its receipt. <paramref name="rewrite"/> returns
    /// the new HTML, or null to leave a message as it is; only Dwight's own chrome (no source) is offered, so neither the user's text nor
    /// the model's can be rewritten. Persists when anything changed and returns the new HTML of each changed message.
    /// </summary>
    public static IReadOnlyList<string> RewriteThreadPluginHtml(GH_Document doc, string threadId, Func<string, string> rewrite)
    {
      var changed = new List<string>();
      var t = FindThread(doc, threadId);
      if (t?.Messages == null || rewrite == null) return changed;
      foreach (var m in t.Messages)
      {
        if (m == null || !string.IsNullOrEmpty(m.Src) || string.IsNullOrEmpty(m.Html)) continue;
        string next;
        try { next = rewrite(m.Html); } catch { next = null; }
        if (next == null || next == m.Html) continue;
        m.Html = next;
        changed.Add(next);
      }
      if (changed.Count > 0) SaveThreads(doc);
      return changed;
    }

    // ---- Brief AO: the chat header writes these, and nothing else does ----------------------------------------------------------
    /// <summary>This conversation's pinned model id, or empty when the posture chooses. Reads the thread record and nothing else.</summary>
    public static string ThreadModelId(GH_Document doc, string threadId) => FindThread(doc, threadId)?.ModelId ?? "";

    /// <summary>This conversation's own posture, or null to use the Settings one.</summary>
    public static string ThreadPosture(GH_Document doc, string threadId) => FindThread(doc, threadId)?.Posture;

    /// <summary>Set the pin and the posture together (a pin clears the posture override; choosing a posture clears the pin) and persist.</summary>
    public static void SetThreadModel(GH_Document doc, string threadId, string modelId, string posture)
    {
      if (string.IsNullOrEmpty(threadId)) return;
      var t = EnsureThread(doc, threadId);
      if (t == null) return;
      t.ModelId = string.IsNullOrWhiteSpace(modelId) ? null : modelId.Trim();
      t.Posture = string.IsNullOrWhiteSpace(posture) ? null : posture.Trim().ToLowerInvariant();
      SaveThreads(doc);
    }

    public static void SetThreadApprovalMode(GH_Document doc, string threadId, string mode)
    {
      if (string.IsNullOrEmpty(threadId)) return;
      var t = EnsureThread(doc, threadId);
      if (t == null) return;
      t.ApprovalMode = Dwight.Agent.ApprovalMode.Normalise(mode);
      SaveThreads(doc);
    }

    public static void SetThreadFullControl(GH_Document doc, string threadId, bool on)
    {
      if (string.IsNullOrEmpty(threadId)) return;
      var t = EnsureThread(doc, threadId);
      if (t == null) return;
      t.FullControl = on;
      SaveThreads(doc);
    }

    /// <summary>Whether this conversation was opened with Full control on (U90). Reads the thread record and nothing else; none stored is off.</summary>
    public static bool ThreadFullControl(GH_Document doc, string threadId) =>
      FindThread(doc, threadId)?.FullControl == true;

    /// <summary>Has this conversation already shown a run_script card? Decides whether the next one opens expanded.</summary>
    public static bool ThreadRunScriptCardShown(GH_Document doc, string threadId) =>
      FindThread(doc, threadId)?.RunScriptCardShown == true;

    /// <summary>Remember that a run_script card has been shown in this conversation.</summary>
    public static void MarkRunScriptCardShown(GH_Document doc, string threadId)
    {
      if (string.IsNullOrEmpty(threadId)) return;
      var t = EnsureThread(doc, threadId);
      if (t == null || t.RunScriptCardShown == true) return;
      t.RunScriptCardShown = true;
      SaveThreads(doc);
    }

    /// <summary>The approval mode this conversation was opened with (Brief AL). Reads the thread record and NOTHING ELSE - not the Settings
    /// default - so changing the default cannot change an open conversation. A thread with none stored is "ask".</summary>
    public static string ThreadApprovalMode(GH_Document doc, string threadId) =>
      Dwight.Agent.ApprovalMode.ForThread(FindThread(doc, threadId)?.ApprovalMode);

    /// <summary>
    /// Record which stage this conversation reached (WP-3 Stage B). NARRATIVE ONLY (#220): it is for
    /// rendering and for reading the thread back, and it confers NOTHING. The capability to act travels
    /// on the call stack and dies with the turn, so a reopened thread in any phase must ask again before
    /// it can do anything - persisting a phase must never become persisting a privilege.
    /// </summary>
    public static void SetThreadPhase(GH_Document doc, string threadId, string phase)
    {
      if (string.IsNullOrEmpty(threadId)) return;
      HookOnce();
      var t = EnsureThread(doc, threadId);
      if (t == null) return;
      t.Phase = phase ?? "plan";
      SaveThreads(doc);
    }

    /// <summary>
    /// Store the thread's scaffold map (WP-3 Stage D) - the plan WP-4 builds from.
    ///
    /// It lives OUTSIDE the message list on purpose (#167): the message caps trim a long conversation's
    /// head, and the plan is the one thing in a thread that must survive that. It is data for a machine,
    /// not a render source - what the user reads is the summary message the conversation already carries.
    /// </summary>
    public static void SetThreadScaffoldMap(GH_Document doc, string threadId, string mapJson)
    {
      if (string.IsNullOrEmpty(threadId)) return;
      HookOnce();
      var t = EnsureThread(doc, threadId);
      if (t == null) return;
      t.ScaffoldMap = mapJson;
      SaveThreads(doc);
    }

    /// <summary>The thread's scaffold map, or null. WP-4's entry point into what was planned.</summary>
    public static string ThreadScaffoldMap(GH_Document doc, string threadId) =>
      FindThread(doc, threadId)?.ScaffoldMap;

    /// <summary>What this thread's builds have done, per region (WP-4 Stage C). The slot has existed
    /// since WP-1 with no writer; this is the writer. The CAPABILITY to build never survives the turn
    /// (#220) - but the FACT of what was built is history, which is what the store is for.</summary>
    public static void SetThreadBuildProgress(GH_Document doc, string threadId, string progressJson)
    {
      if (string.IsNullOrEmpty(threadId)) return;
      HookOnce();
      var t = EnsureThread(doc, threadId);
      if (t == null) return;
      t.BuildProgress = progressJson;
      SaveThreads(doc);
    }

    public static string ThreadBuildProgress(GH_Document doc, string threadId) =>
      FindThread(doc, threadId)?.BuildProgress;

    /// <summary>Every thread id recorded for this document. For a diagnostic that reports across a
    /// document's whole history rather than one conversation - it reads Dwight's own bookkeeping and
    /// touches neither the canvas nor a model.</summary>
    public static IReadOnlyList<string> ThreadIds(GH_Document doc)
    {
      try
      {
        var key = KeyFor(doc);
        ListFor(doc, key);   // load the record first, exactly as FindThread does
        return _record.TryGetValue(key, out var rec)
          ? (rec?.Threads ?? new List<StoredThread>())
              .Where(t => !string.IsNullOrEmpty(t?.Id)).Select(t => t.Id).ToList()
          : new List<string>();
      }
      catch { return new List<string>(); }
    }

    // ---------------------------------------------------------------- the agent id space (#229)
    //
    // ADR-067's per-file int-to-guid map, in the `graph` slot WP-1 deliberately left empty for it (#162
    // Q4: "populate in WP-3"). It is per DOCUMENT rather than per thread, because an id means a guid in
    // THIS file - two threads on one document share one id space, correctly.
    //
    // It is due now rather than later because a stored scaffold map holds AGENT IDS in its anchors, and
    // WP-4 builds from that map: a restored map resolving against a fresh id space would point at
    // different components, which is the mis-grounding the registry exists to prevent.

    private const string IdSpaceField = "ids";

    /// <summary>The stored agent id space for this document, or null.</summary>
    public static string IdSpace(GH_Document doc)
    {
      var key = KeyFor(doc);
      // #303 sweep: same class as the thread doors. #229 made the id space LOAD-BEARING - a restored map
      // resolving against a fresh id space points at different components - so an unpersistable document
      // needs it in memory all the more, not less.
      ListFor(doc, key);
      if (!_record.TryGetValue(key, out var rec) || rec?.Graph == null) return null;
      try
      {
        var graph = rec.Graph.Value;
        if (graph.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
        return graph.TryGetProperty(IdSpaceField, out var ids) ? ids.GetRawText() : null;
      }
      catch { return null; }
    }

    /// <summary>Store the agent id space, preserving anything else the graph slot carries.</summary>
    public static void SetIdSpace(GH_Document doc, string json)
    {
      if (string.IsNullOrWhiteSpace(json)) return;
      HookOnce();
      var key = KeyFor(doc);
      // #303 sweep: see IdSpace.
      ListFor(doc, key);
      var rec = GetOrCreateRecord(key);
      try
      {
        // Rebuilt rather than mutated in place: JsonElement is immutable, and re-serialising the whole
        // slot keeps any sibling a newer build put there.
        var slot = new System.Text.Json.Nodes.JsonObject();
        if (rec.Graph != null && rec.Graph.Value.ValueKind == System.Text.Json.JsonValueKind.Object)
          foreach (var p in rec.Graph.Value.EnumerateObject())
            if (p.Name != IdSpaceField)
              slot[p.Name] = System.Text.Json.Nodes.JsonNode.Parse(p.Value.GetRawText());

        slot[IdSpaceField] = System.Text.Json.Nodes.JsonNode.Parse(json);
        rec.Graph = System.Text.Json.JsonDocument.Parse(slot.ToJsonString()).RootElement.Clone();
        Persist(doc, key, ListFor(doc, key));
      }
      catch { /* the id space is a cache of identity, not the user's work: never fail a turn for it */ }
    }

    /// <summary>A thread's messages for a reopen render, or empty if the thread is gone (cap-evicted -> the
    /// caller falls back to the greeting, honest degradation).</summary>
    public static IReadOnlyList<StoredMessage> ThreadMessages(GH_Document doc, string threadId)
    {
      var t = FindThread(doc, threadId);
      return (IReadOnlyList<StoredMessage>)t?.Messages ?? Array.Empty<StoredMessage>();
    }

    private static StoredThread EnsureThread(GH_Document doc, string threadId)
    {
      var key = KeyFor(doc);
      // #303: NO GUARD HERE. NoDocumentKey gates PERSISTENCE, never MEMORY - see KeyFor. A thread must exist in
      // memory whether or not it can ever reach disk; refusing to create one turned "we cannot save this
      // conversation" into "we cannot have this conversation", which is the failure #296 shipped.
      ListFor(doc, key);                       // load the record first, so we never clobber existing history
      var rec = GetOrCreateRecord(key);
      rec.Threads = rec.Threads ?? new List<StoredThread>();
      var t = rec.Threads.FirstOrDefault(x => x != null && x.Id == threadId);
      if (t == null)
      {
        t = new StoredThread { Id = threadId, Phase = "plan", Messages = new List<StoredMessage>() };
        rec.Threads.Insert(0, t);              // newest-first; the cap (Normalize) trims the oldest tail
      }
      t.Messages = t.Messages ?? new List<StoredMessage>();
      return t;
    }

    private static StoredThread FindThread(GH_Document doc, string threadId)
    {
      var key = KeyFor(doc);
      // #303: no guard - the memory bucket is readable for the same reason it is writable.
      ListFor(doc, key);
      return _record.TryGetValue(key, out var rec)
        ? rec?.Threads?.FirstOrDefault(x => x != null && x.Id == threadId)
        : null;
    }

    private static void SaveThreads(GH_Document doc)
    {
      var key = KeyFor(doc);
      Persist(doc, key, ListFor(doc, key));    // rebuilds Commands, preserves + persists Threads, Normalize trims
    }

    /// <summary>Record what one chat turn cost, on its thread, and persist it.</summary>
    public static void AppendThreadSpend(GH_Document doc, string threadId, StoredSpend spend)
    {
      if (string.IsNullOrEmpty(threadId) || spend == null) return;
      HookOnce();
      var t = EnsureThread(doc, threadId);
      if (t == null) return;
      (t.Turns = t.Turns ?? new List<StoredSpend>()).Add(spend);
      SaveThreads(doc);
      // Brief AK: the same spend, with its ordinal on the thread, into the central ledger.
      try { SpendLedger.RecordTurn(doc, threadId, t.Turns.Count, spend); } catch { }
      try { Changed?.Invoke(); } catch { }   // the dock row's totals move
    }

    /// <summary>The chat's last turn, or null before the first.</summary>
    public static StoredSpend ThreadLastTurn(GH_Document doc, string threadId)
    {
      try { var turns = FindThread(doc, threadId)?.Turns; return turns == null || turns.Count == 0 ? null : turns[turns.Count - 1]; }
      catch { return null; }
    }

    /// <summary>The chat's total so far, computed from its turns. Zero turns for a thread from an older build.</summary>
    public static SpendTotal ThreadSpend(GH_Document doc, string threadId)
    {
      try { return SpendMath.Sum(FindThread(doc, threadId)?.Turns); }
      catch { return SpendMath.Sum(null); }
    }

    private static StoredMessage Msg(string who, string html, string src) =>
      new StoredMessage { Who = who ?? "dw", Html = html ?? "", Src = src, Utc = DateTime.UtcNow.ToString("o") };

    private static void HookOnce()
    {
      if (_hooked) return;
      _hooked = true;
      try
      {
        var server = Instances.DocumentServer;
        foreach (GH_Document d in server) SubscribeFilePath(d);   // #179: catch a save on an already-open doc
        server.DocumentAdded += (s, d) => SubscribeFilePath(d);
        server.DocumentRemoved += (s, d) =>
        {
          // Drop the in-memory copy only; the disk record is DURABLE and must survive the close (that is the
          // whole point of WP-1). A reopen reloads it from disk.
          try
          {
            var key = KeyFor(d);
            _byFile.Remove(key); _record.Remove(key); _loaded.Remove(key);
            _subscribed.Remove(d);
            Changed?.Invoke();
          }
          catch { }
        };
      }
      catch { }
    }

    private static readonly HashSet<GH_Document> _subscribed = new HashSet<GH_Document>();

    private static void SubscribeFilePath(GH_Document d)
    {
      if (d == null || !_subscribed.Add(d)) return;
      try { d.FilePathChanged += (s, e) => OnFilePathChanged(e.Document, e.OldFilePath, e.NewFilePath); } catch { }
    }

    /// <summary>#179: a document was saved / saved-as. Two jobs. (1) The name fix: refresh so the dock label
    /// stops reading "UNSAVED FILE" (a save leaves the DocumentID stable, so the key-gated refresh never fired -
    /// this is the trigger it was missing). (2) Re-stamp this doc's own rows from the old path (including "")
    /// to the new one, so the lineage stays correctly attributed as the file gains or changes its name.</summary>
    private static void OnFilePathChanged(GH_Document doc, string oldPath, string newPath)
    {
      try
      {
        var key = KeyFor(doc);
        if (key != NoDocumentKey && _byFile.TryGetValue(key, out var list))
        {
          var o = oldPath ?? "";
          var np = newPath ?? "";
          foreach (var c in list) if (ProvenanceView.SameOrigin(c.Origin, o)) c.Origin = np;
          Persist(doc, key, list);
        }
        Changed?.Invoke();   // the dock refreshes -> the label and the provenance view update immediately
      }
      catch { }
    }

    // ---------------------------------------------------------------- Brief AP: the changes Dwight made (History)
    //
    // They ride the same per-file record as commands and threads; Persist rebuilds only Commands, so these survive
    // every other write. The decisions about them live in HistoryCore; ChangeLog is the only caller.

    /// <summary>Record one change Dwight made to the document, newest first, and persist it.</summary>
    public static void AppendChange(GH_Document doc, StoredChange change)
    {
      if (change == null) return;
      HookOnce();
      var key = KeyFor(doc);
      var list = ListFor(doc, key);            // load the record first, so a change never clobbers the history
      var rec = GetOrCreateRecord(key);
      rec.Changes = rec.Changes ?? new List<StoredChange>();
      rec.Changes.Insert(0, change);
      if (rec.Changes.Count > HistoryCore.MaxChanges) rec.Changes.RemoveRange(HistoryCore.MaxChanges, rec.Changes.Count - HistoryCore.MaxChanges);
      Persist(doc, key, list);
      try { Changed?.Invoke(); } catch { }
    }

    /// <summary>Persist the changes after one of them was marked (Dwight took it back).</summary>
    public static void SaveChanges(GH_Document doc)
    {
      var key = KeyFor(doc);
      Persist(doc, key, ListFor(doc, key));
      try { Changed?.Invoke(); } catch { }
    }

    /// <summary>Note where the plugin has just left these objects, and persist it. See <see cref="RegionPlacesCore"/>.</summary>
    public static void NoteRegionPlaces(GH_Document doc, IEnumerable<(Guid Id, double X, double Y)> places)
    {
      var key = KeyFor(doc);
      var list = ListFor(doc, key);            // load the record first, so a note never clobbers the history
      var rec = GetOrCreateRecord(key);
      rec.RegionPlaces = rec.RegionPlaces ?? new List<StoredRegionPlace>();
      foreach (var p in places) RegionPlacesCore.Note(rec.RegionPlaces, p.Id, p.X, p.Y);
      Persist(doc, key, list);
    }

    /// <summary>Where the plugin has left things on this document. Empty for no document or a record from an older build.</summary>
    public static IReadOnlyList<StoredRegionPlace> RegionPlaces(GH_Document doc)
    {
      if (doc == null) return Array.Empty<StoredRegionPlace>();
      var key = KeyFor(doc);
      ListFor(doc, key);
      return _record.TryGetValue(key, out var rec) && rec?.RegionPlaces != null
        ? rec.RegionPlaces
        : (IReadOnlyList<StoredRegionPlace>)Array.Empty<StoredRegionPlace>();
    }

    /// <summary>Brief DE: put a lesson's score on the Explain's row and persist it. A row that has gone from the list is left alone.</summary>
    public static void SetTeachScore(GH_Document doc, DwightCommand row, int right, int asked)
    {
      if (doc == null || row == null) return;
      var key = KeyFor(doc);
      var list = ListFor(doc, key);
      var pair = Dwight.UI.TeachingText.Pair(right, asked);   // a pair the store would drop on read is not written
      if (pair == null || !list.Contains(row)) return;
      row.Score = pair;
      Persist(doc, key, list);
      try { Changed?.Invoke(); } catch { }
    }

    /// <summary>
    /// Brief EI: a deck's answers into its run's record, so the run reopened from History shows its result card. The row's own <c>SetDeck</c> op is rewritten, never
    /// added, and the score goes with it when every card is answered. A row that has gone from the list is left alone.
    /// </summary>
    public static void SetDeck(GH_Document doc, DwightCommand row, string deckJson, int right, int asked, bool finished)
    {
      if (doc == null || row == null) return;
      var key = KeyFor(doc);
      var list = ListFor(doc, key);
      if (!list.Contains(row)) return;
      var changed = PanelScript.Replace(row.Script, PanelScript.SetDeck, deckJson);
      var pair = finished ? Dwight.UI.TeachingText.Pair(right, asked) : null;
      if (pair != null) { row.Score = pair; changed = true; }
      if (!changed) return;
      Persist(doc, key, list);
      try { Changed?.Invoke(); } catch { }
    }

    /// <summary>
    /// Brief DG: put a run's spend on the row it already wrote. A Stop writes the run's row at once, before the provider's reply (and so its bill) is known; when the
    /// reply lands the run's line is written, and this puts the same spend on the row, so the dock reads the cost beside "stopped". The row is found by reference in
    /// this file's list; a row that has gone (the list is capped) is left alone.
    /// </summary>
    public static void SetSpend(GH_Document doc, DwightCommand row, StoredSpend spend)
    {
      if (doc == null || row == null || spend == null) return;
      var key = KeyFor(doc);
      var list = ListFor(doc, key);
      if (!list.Contains(row)) return;
      row.Spend = spend;
      row.SpendLedgered = true;
      Persist(doc, key, list);
      try { Changed?.Invoke(); } catch { }
    }

    // ---------------------------------------------------------------- Brief DC: Dwight notes
    //
    // They ride the same per-file record as commands, threads and changes; Persist rebuilds only Commands, so they survive every other write. The
    // rules about a note (its bound, its state, its lines in a payload) live in DwightNotes and NoteState; this is only where they are kept.

    /// <summary>Raised when a note is added, changed or removed, so the canvas can redraw them.</summary>
    public static event Action NotesChanged;

    /// <summary>The document's notes, oldest first. Empty for no document or a record from an older build.</summary>
    public static IReadOnlyList<StoredNote> Notes(GH_Document doc)
    {
      if (doc == null) return Array.Empty<StoredNote>();
      var key = KeyFor(doc);
      ListFor(doc, key);
      return _record.TryGetValue(key, out var rec) && rec?.Notes != null
        ? rec.Notes
        : (IReadOnlyList<StoredNote>)Array.Empty<StoredNote>();
    }

    /// <summary>Keep one note and persist it. The note was made by <see cref="DwightNotes.TryMake"/>, which has already held its bound.</summary>
    public static void AddNote(GH_Document doc, StoredNote note)
    {
      if (doc == null || note == null) return;
      HookOnce();
      var key = KeyFor(doc);
      var list = ListFor(doc, key);            // load the record first, so a note never clobbers the history
      var rec = GetOrCreateRecord(key);
      rec.Notes = rec.Notes ?? new List<StoredNote>();
      rec.Notes.Add(note);
      if (rec.Notes.Count > DwightNotes.MaxNotes) rec.Notes.RemoveRange(0, rec.Notes.Count - DwightNotes.MaxNotes);
      Persist(doc, key, list);
      try { NotesChanged?.Invoke(); } catch { }
    }

    /// <summary>Change a note's text and persist it. False when there is no such note or the text is refused (<paramref name="refusal"/> says why).</summary>
    public static bool EditNote(GH_Document doc, string noteId, string text, out string refusal)
    {
      refusal = null;
      var clean = text?.Trim();
      if (string.IsNullOrEmpty(clean)) { refusal = "A note needs some text."; return false; }
      if (clean.Length > DwightNotes.MaxChars) { refusal = DwightNotes.TooLongSentence(clean.Length); return false; }
      if (doc == null) return false;
      var key = KeyFor(doc);
      var list = ListFor(doc, key);
      var note = GetOrCreateRecord(key).Notes?.FirstOrDefault(n => n.Id == noteId);
      if (note == null) { refusal = "That note is no longer there."; return false; }
      note.Text = clean;
      Persist(doc, key, list);
      try { NotesChanged?.Invoke(); } catch { }
      return true;
    }

    /// <summary>Put a note somewhere (Brief ED): a drag, a pin or an unpin, worked out by <c>NotePlace</c>. One write, persisted; the note keeps its id, its text, its author
    /// and its time. False when there is no such note.</summary>
    public static bool PlaceNote(GH_Document doc, string noteId, Dwight.UI.NotePlacement place)
    {
      if (doc == null || place == null) return false;
      var key = KeyFor(doc);
      var list = ListFor(doc, key);
      var note = GetOrCreateRecord(key).Notes?.FirstOrDefault(n => n.Id == noteId);
      if (note == null) return false;
      Dwight.UI.NotePlace.Apply(note, place);
      Persist(doc, key, list);
      try { NotesChanged?.Invoke(); } catch { }
      return true;
    }

    /// <summary>Remove a note and persist it. A deleted note is gone; there is no undo for it.</summary>
    public static bool DeleteNote(GH_Document doc, string noteId)
    {
      if (doc == null) return false;
      var key = KeyFor(doc);
      var list = ListFor(doc, key);
      var rec = GetOrCreateRecord(key);
      if (rec.Notes == null || rec.Notes.RemoveAll(n => n.Id == noteId) == 0) return false;
      Persist(doc, key, list);
      try { NotesChanged?.Invoke(); } catch { }
      return true;
    }

    /// <summary>The document's changes, newest first. Empty for no document or a record from an older build.</summary>
    public static IReadOnlyList<StoredChange> Changes(GH_Document doc)
    {
      if (doc == null) return Array.Empty<StoredChange>();
      var key = KeyFor(doc);
      ListFor(doc, key);
      return _record.TryGetValue(key, out var rec) && rec?.Changes != null
        ? rec.Changes
        : (IReadOnlyList<StoredChange>)Array.Empty<StoredChange>();
    }

    // ---------------------------------------------------------------- mapping (shim; the pure round-trip is tested in the core)

    private static StoredCommand ToStored(DwightCommand c) => new StoredCommand
    {
      Action = DwightVerbs.Canonical(c.Action),
      Subtitle = c.Subtitle ?? "",
      State = string.IsNullOrEmpty(c.State) ? "ok" : c.State,
      SourceGuids = c.SourceGuids?.Select(g => g.ToString()).ToList() ?? new List<string>(),
      SecondaryGuids = c.SecondaryGuids?.Select(g => g.ToString()).ToList(),
      Labels = c.Labels?.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
      TimeUtc = c.Time.ToUniversalTime().ToString("o"),
      CostLine = c.CostLine ?? "",
      Spend = c.Spend,
      Origin = c.Origin ?? "",
      ThreadId = c.ThreadId,
      RunId = c.RunId,
      Score = c.Score,
      // a failed/stopped run's live offer is its in-session Retry; cross-session it is content-only, honestly.
      LiveClass = (c.State == "failed" || c.State == "stopped") ? "retry" : "content",
      Script = c.Script,
    };

    private static DwightCommand FromStored(StoredCommand s) => new DwightCommand
    {
      Action = DwightVerbs.Canonical(s.Action),   // a record from an earlier build says Scaffold; it is Build now (#1083)
      Subtitle = s.Subtitle ?? "",
      State = string.IsNullOrEmpty(s.State) ? "ok" : s.State,
      SourceGuids = ParseGuids(s.SourceGuids),
      SecondaryGuids = s.SecondaryGuids != null && s.SecondaryGuids.Count > 0 ? ParseGuids(s.SecondaryGuids) : null,
      Labels = ParseLabels(s.Labels),
      Time = ParseTime(s.TimeUtc),
      CostLine = s.CostLine ?? "",
      Spend = s.Spend,
      Origin = s.Origin ?? "",
      ThreadId = s.ThreadId,
      RunId = s.RunId,
      Score = s.Score != null && s.Score.Length == 2 ? Dwight.UI.TeachingText.Pair(s.Score[0], s.Score[1]) : null,
      Script = s.Script,
      Live = null,   // cross-session: no live affordance exists, by construction
    };

    private static List<Guid> ParseGuids(List<string> raw)
    {
      var outp = new List<Guid>();
      if (raw == null) return outp;
      foreach (var g in raw) if (Guid.TryParse(g, out var id)) outp.Add(id);
      return outp;
    }

    private static Dictionary<Guid, int> ParseLabels(Dictionary<string, int> raw)
    {
      if (raw == null) return null;
      var outp = new Dictionary<Guid, int>();
      foreach (var kv in raw) if (Guid.TryParse(kv.Key, out var id)) outp[id] = kv.Value;
      return outp.Count > 0 ? outp : null;
    }

    private static DateTime ParseTime(string iso) =>
      DateTime.TryParse(iso, null, System.Globalization.DateTimeStyles.RoundtripKind, out var t) ? t.ToLocalTime() : DateTime.Now;

    /// <summary>Keep the path list most-recent-first, deduped, bounded - a label only, never a key.</summary>
    private static List<string> MergeLabel(List<string> existing, string path)
    {
      var list = existing ?? new List<string>();
      if (string.IsNullOrWhiteSpace(path)) return list;
      list.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
      list.Insert(0, path);
      while (list.Count > 5) list.RemoveAt(list.Count - 1);
      return list;
    }

    // ---------------------------------------------------------------- the fingerprint (only when there is no DocumentID anchor)

    /// <summary>Content tokens for <see cref="DocumentFingerprint"/> - used ONLY to key an anchorless document
    /// (a .gh with no DocumentID header, the untested defensive edge). Component types + wires, order-
    /// independent by the hash. Instance guids are stable within a file, which is all an ephemeral unsaved doc
    /// needs.</summary>
    private static IEnumerable<string> Tokens(GH_Document doc)
    {
      if (doc == null) yield break;
      foreach (var obj in doc.Objects)
      {
        if (obj == null) continue;
        yield return "c:" + obj.ComponentGuid;
        if (obj is IGH_Component comp)
        {
          foreach (var p in comp.Params.Input)
            foreach (var src in p.Sources)
              yield return "e:" + src.InstanceGuid + ">" + p.InstanceGuid;
        }
        else if (obj is IGH_Param param)
        {
          foreach (var src in param.Sources)
            yield return "e:" + src.InstanceGuid + ">" + param.InstanceGuid;
        }
      }
    }

    private static string Fingerprint(GH_Document doc) => DocumentFingerprint.Compute(Tokens(doc));
  }
}
