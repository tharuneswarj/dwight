using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace Dwight.Agent
{
  /// <summary>What one build_region result says, read off its wire envelope - see <see cref="BuildEnvelope.Classify"/>.</summary>
  internal sealed class BuildEnvelope
  {
    public string Address { get; set; }
    public RegionOutcome Outcome { get; set; }
    public int Placed { get; set; }
    public int NewErrors { get; set; }
    public string Note { get; set; }

    /// <summary>U91: the measured sentence when the revert left something behind; null when the canvas is as it was.</summary>
    public string Residue { get; set; }

    /// <summary>The verdict's own attemptsLeftForRegion, or null when the result carries no verdict.</summary>
    public int? AttemptsLeft { get; set; }

    public List<string> State { get; } = new List<string>();

    /// <summary>What an applied build reconnected or restored (#625), from the verdict's own payload.</summary>
    public List<ReconnectionNote> Reconnected { get; set; } = new List<ReconnectionNote>();

    /// <summary>Refused because it would cut a wire it did not reconnect, or one it cannot read (#647's row class).</summary>
    public bool WouldCut { get; set; }

    /// <summary>
    /// READ ONE build_region RESULT THE WAY IT ACTUALLY ARRIVES (#649, Finding C).
    ///
    /// THE DEFECT. The panel read a reverted build's verdict from <c>error.data</c>, while <c>ToolResult.ToJson</c>
    /// writes a refusal's detail to <c>error.detail</c> and nothing emits "data". So every revert had no verdict,
    /// classified as REFUSED, lost its verdict note and its attempts-left, and the #624 fact said "REFUSED before
    /// anything was touched - The build was applied ... and was reverted". Every test of those surfaces built the
    /// outcome by hand, so none saw the envelope the panel is handed - #325's shape, an instrument testing a shape
    /// its subject never sees.
    ///
    /// So the classification is here, GH-free, and tested ONLY against envelopes made by <c>ToolResult</c> itself:
    ///   - the verdict is <c>result</c> on success, or <c>error.detail</c> when that detail IS a verdict (it carries
    ///     "status"); a refusal's own detail (a cause, a cut list) is not a verdict;
    ///   - a verdict that did not apply, or a <c>verification_failed</c> refusal with no verdict (the exception and
    ///     timeout reverts), is REVERTED; any other failure is REFUSED;
    ///   - the note is the verdict's own note, else the refusal message.
    /// Null when the envelope cannot be read or names no region.
    /// </summary>
    public static BuildEnvelope Classify(string envelope, string args)
    {
      JsonNode root;
      try { root = JsonNode.Parse(envelope ?? ""); } catch { return null; }
      if (root == null) return null;

      var ok = false;
      try { ok = root["ok"]?.GetValue<bool>() ?? false; } catch { }

      var detail = root["error"]?["detail"] as JsonObject;
      var verdict = (root["result"] as JsonObject) ?? (detail?["status"] != null ? detail : null);

      string argRegion = null;
      try { argRegion = (JsonNode.Parse(args ?? "") as JsonObject)?["region"]?.GetValue<string>(); } catch { }
      var address = Str(verdict?["region"]) ?? argRegion;
      if (string.IsNullOrEmpty(address)) return null;   // an un-addressed build has no region row to write

      var applied = string.Equals(Str(verdict?["status"]), "applied", StringComparison.Ordinal);
      var code = Str(root["error"]?["code"]);

      var result = new BuildEnvelope { Address = address };
      try { result.NewErrors = verdict?["newErrors"]?.GetValue<int>() ?? 0; } catch { }
      try { result.Placed = (verdict?["placed"] as JsonArray)?.Count ?? 0; } catch { }
      try { result.AttemptsLeft = verdict?["attemptsLeftForRegion"]?.GetValue<int>(); } catch { }

      // U91: a revert whose measurement found residue says so on the verdict (canvasAsItWas: false), and is never read as "as it was".
      var dirty = false;
      try { dirty = verdict?["canvasAsItWas"]?.GetValue<bool>() == false; } catch { }
      result.Residue = dirty ? Str(verdict?["residue"]) : null;

      result.Outcome = !ok && !applied
        ? (verdict != null || code == ToolErrors.VerificationFailed ? (dirty ? RegionOutcome.RevertedWithResidue : RegionOutcome.Reverted) : RegionOutcome.Refused)
        : result.NewErrors > 0 ? RegionOutcome.BuiltWithErrors
        : RegionOutcome.Built;

      result.Note = Str(verdict?["note"]) ?? Str(root["error"]?["message"]);

      // U26 (#916, #923): an APPLIED verdict's note is what was checked and what the placed components hold - the
      // verdict's own `checked` sentence and `placedFacts`, composed here so the buildline, the persisted record and the
      // turn-start fact all carry the same facts the model read in its tool result. An applied verdict carries no other
      // note, so nothing is displaced.
      if (ok)
      {
        var summary = BuildPlacedFacts.Summary(verdict);
        if (!string.IsNullOrEmpty(summary)) result.Note = summary;
      }

      // #625/#647: a boundary refusal speaks from its STRUCTURE (its reason and the plugin's consumer labels), never
      // its message - that is addressed to the model, and this note reaches the user's running line (#615).
      var boundaryNote = BuildOutcomeFact.BoundaryNote(root, out var wouldCut);
      if (boundaryNote != null) result.Note = boundaryNote;
      result.WouldCut = wouldCut;

      // #625: what an applied build reconnected or restored.
      if (ok) result.Reconnected = BuildOutcomeFact.ReconnectionsFrom(verdict);

      try
      {
        foreach (var n in (verdict?["initialState"] as JsonArray) ?? new JsonArray())
        {
          var text = Str(n);
          if (!string.IsNullOrWhiteSpace(text)) result.State.Add(text);
        }
      }
      catch { }

      return result;
    }

    private static string Str(JsonNode n)
    {
      if (n == null) return null;
      try { return n.GetValue<string>(); } catch { return null; }
    }
  }

  /// <summary>One wire a build put back (#625): the consumer as the plugin names it, its input, and whether it was a
  /// restore of a lost source rather than a rebuild's reconnection.</summary>
  internal sealed class ReconnectionNote
  {
    public string Consumer { get; set; }
    public string Input { get; set; }
    public bool Restored { get; set; }

    public string Label => (Consumer ?? "component") + (string.IsNullOrEmpty(Input) ? "" : " (" + Input + ")");
  }

  /// <summary>One region's latest outcome in one approved build turn.</summary>
  internal sealed class BuildTurnRow
  {
    public RegionOutcome Outcome { get; set; }
    public int Components { get; set; }
    public string Note { get; set; } = "";

    /// <summary>U91: what a not-clean revert left behind, in the plugin's measured words.</summary>
    public string Residue { get; set; } = "";

    /// <summary>What this build reconnected or restored (#625) - empty unless it stood.</summary>
    public IReadOnlyList<ReconnectionNote> Reconnected { get; set; } = new ReconnectionNote[0];

    /// <summary>
    /// True when it was REFUSED because it would cut a wire it did not reconnect, or one into something Dwight cannot
    /// read (#625, #647's row class). The fact then carries the way out, so the model relays to the user that only
    /// they can remove such a wire.
    /// </summary>
    public bool WouldCut { get; set; }
  }

  /// <summary>
  /// WHAT ONE APPROVED BUILD TURN ACTUALLY DID, per region - the subject of <see cref="BuildOutcomeFact"/>.
  /// Written as each build_region result lands (the same values the running summary shows), with a later
  /// attempt at a region superseding its earlier row, exactly as the closing report and the persisted record do.
  /// </summary>
  internal sealed class BuildTurnRecord
  {
    private readonly Dictionary<string, BuildTurnRow> _rows = new Dictionary<string, BuildTurnRow>(StringComparer.Ordinal);
    private readonly List<string> _order = new List<string>();

    public BuildTurnRecord(IEnumerable<string> approved)
    {
      Approved = (approved ?? Enumerable.Empty<string>()).Where(a => !string.IsNullOrWhiteSpace(a))
        .Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>The regions the user approved for this turn, in the order they were approved.</summary>
    public IReadOnlyList<string> Approved { get; }

    /// <summary>True when the turn ended on a Stop, a budget or a timeout rather than because it finished.</summary>
    public bool StoppedEarly { get; set; }

    public void Record(string address, RegionOutcome outcome, int components, string note,
                       IReadOnlyList<ReconnectionNote> reconnected = null, bool wouldCut = false, string residue = null)
    {
      if (string.IsNullOrEmpty(address)) return;
      if (!_rows.ContainsKey(address)) _order.Add(address);
      _rows[address] = new BuildTurnRow
      {
        Outcome = outcome, Components = components, Note = note ?? "",
        Reconnected = (reconnected ?? new ReconnectionNote[0]).ToList(), WouldCut = wouldCut, Residue = residue ?? "",
      };
    }

    public bool TryGet(string address, out BuildTurnRow row) => _rows.TryGetValue(address ?? "", out row);

    /// <summary>Regions with a row, in the order they first landed.</summary>
    public IReadOnlyList<string> Attempted => _order;
  }

  /// <summary>
  /// THE LAST APPROVED BUILD PER CONVERSATION, and how many of the model's turns ago it ran (#634).
  ///
  /// NOT ONE-SHOT. The false sentence this corrects ("Both fixes landed and verified") stays in the model's
  /// transcript for the life of the conversation, so a correction that expired after one turn would leave the
  /// lie standing from the second turn on. The record is stated at EVERY turn start until a newer approved build
  /// supersedes it.
  ///
  /// IN MEMORY ONLY, and the bound is stated: after a restart there is no record and nothing is said - the
  /// canvas-change fact's "cannot tell" already tells the model the conversation's claims may be stale.
  /// Locked, because the build turn completes on the UI thread and a turn may compose elsewhere.
  /// </summary>
  internal sealed class LastBuildStore
  {
    public sealed class Reading
    {
      public BuildTurnRecord Record { get; set; }
      /// <summary>1 on the first turn after the build turn - "your previous turn".</summary>
      public int TurnsAgo { get; set; }
    }

    private readonly object _lock = new object();
    private readonly Dictionary<string, Reading> _byThread = new Dictionary<string, Reading>(StringComparer.Ordinal);

    /// <summary>A build turn finished: its record supersedes any earlier one, and the count restarts.</summary>
    public void Put(string threadId, BuildTurnRecord record)
    {
      if (string.IsNullOrEmpty(threadId) || record == null) return;
      lock (_lock) _byThread[threadId] = new Reading { Record = record, TurnsAgo = 0 };
    }

    /// <summary>
    /// A model turn is starting in this conversation: the last build, one turn further back than it was. Null
    /// when the conversation has had no approved build since Dwight started.
    /// </summary>
    public Reading ForTurn(string threadId)
    {
      if (string.IsNullOrEmpty(threadId)) return null;
      lock (_lock)
      {
        if (!_byThread.TryGetValue(threadId, out var r)) return null;
        r.TurnsAgo++;
        return new Reading { Record = r.Record, TurnsAgo = r.TurnsAgo };
      }
    }
  }

  /// <summary>
  /// WHAT THE LAST APPROVED BUILD ACTUALLY DID, STATED TO THE MODEL AS A FACT (#624).
  ///
  /// THE FIELD FAILURE. Asked to connect A1 to B1, the model raised a build card; after approval it wrote "Both
  /// fixes landed and verified... Construct Point (31) output is wired into XY Plane (19)", and the close said
  /// nothing was attempted. The next turn it "confirmed" the wiring from its own words. The closing report is
  /// chrome - no Src, excluded from the model transcript by construction - so the model's false sentence
  /// persisted in its history and the correction did not; and #573's "unchanged since your last turn" read as
  /// CONFIRMATION to a model that believed its last turn had changed the canvas. A true fact combined with a
  /// false belief about what the model left produced a confident wrong answer.
  ///
  /// So the plugin states, per region, what happened: built (N), built with errors (N), reverted (the verdict's
  /// reason), refused (the reason), or not attempted. GH-free (ADR-091), same seam and shape as
  /// <see cref="CanvasChangeFact"/>, composed through <see cref="TurnSystemText.Compose"/>.
  ///
  /// Every region named is an ADDRESS by the plugin's grammar: an approved one was validated against the canvas,
  /// and a refused row's region - which can be the model's raw string - is named only if it parses as one. The
  /// notes are the plugin's own verdict and refusal text, one line, bounded (ADR-035).
  /// </summary>
  internal static class BuildOutcomeFact
  {
    public const string Heading = "LAST BUILD";

    /// <summary>The longest note carried per region, including the "..." that says it was cut.</summary>
    public const int MaxNote = 240;

    public static string For(BuildTurnRecord record, int turnsAgo)
    {
      if (record == null) return null;

      var extra = record.Attempted
        .Where(a => !record.Approved.Contains(a, StringComparer.Ordinal) && ScaffoldAddress.IsRegionAddress(a))
        .OrderBy(a => ScaffoldAddress.SortKey(a).Letters, StringComparer.Ordinal)
        .ThenBy(a => ScaffoldAddress.SortKey(a).Number);
      var regions = record.Approved.Where(ScaffoldAddress.IsRegionAddress).Concat(extra).ToList();
      if (regions.Count == 0) return null;

      var when = turnsAgo <= 1 ? "the approved build in your previous turn"
                               : "the approved build " + turnsAgo + " of your turns ago";

      var lines = regions.Select(a => record.TryGet(a, out var row) ? Line(a, row)
        : a + " NOT ATTEMPTED - build_region was never called for it, so nothing in " + a + " changed.");

      return Heading + " (" + when + ") - WHAT ACTUALLY HAPPENED, read from each build's own verdict, not from " +
             "anything said in this conversation: " + string.Join(" ", lines) +
             (record.StoppedEarly ? " The build was stopped before it finished." : "") +
             " If anything earlier in this conversation, including your own messages, says otherwise, this is " +
             "what happened - read the canvas before describing it.";
    }

    /// <summary>What #647 asks the model to relay when a rebuild was refused for a wire it would cut.</summary>
    public const string WouldCutWayOut =
      "Only the user can remove or reroute such a wire: if nothing in the new build should feed it, they delete it " +
      "first, and then the rebuild cuts nothing.";

    private static string Line(string address, BuildTurnRow row)
    {
      var note = Bounded(row.Note);
      switch (row.Outcome)
      {
        // U26: never the bare word "verified". The row's note on a build that stood is the verdict's own account of what
        // was checked and what the placed components hold (BuildEnvelope.Classify), bounded by its composer - so the
        // model is told "486 lines, total length 0 m" at the next turn start, not "verified".
        case RegionOutcome.Built:
          return address + " built (" + Count(row.Components) + ")" + Reconnections(row) + "." + Facts(row);
        case RegionOutcome.BuiltWithErrors:
          return address + " built and standing, but Grasshopper reports new errors in it (" + Count(row.Components) + ")" +
                 Reconnections(row) + "." + Facts(row);
        case RegionOutcome.Reverted:
          return address + " REVERTED" + (note.Length > 0 ? " - " + note : "") + "; the canvas is as it was.";
        case RegionOutcome.RevertedWithResidue:
          return address + " REVERTED" + (note.Length > 0 ? " - " + note : "") + (note.EndsWith(".", StringComparison.Ordinal) ? " " : "; ") + "NOT CLEANLY: " + Bounded(row.Residue) +
                 " Read the canvas before saying what is there.";
        default:
          var refused = address + " REFUSED before anything was touched" + (note.Length > 0 ? " - " + note : "") +
                        (note.EndsWith(".", StringComparison.Ordinal) ? "" : ".");
          // #647's row class: the refusal was for a cut, so the fact carries the way out for the model to relay.
          return row.WouldCut ? refused + " " + WouldCutWayOut : refused;
      }
    }

    /// <summary>", and reconnected XY Plane (19) in B1 (Origin), and restored the input Loft (22) had lost (Curves)".</summary>
    private static string Reconnections(BuildTurnRow row)
    {
      var all = row.Reconnected ?? new ReconnectionNote[0];
      var reconnected = all.Where(r => !r.Restored).Select(r => r.Label).ToList();
      var restored = all.Where(r => r.Restored).Select(r => r.Consumer + " had lost" +
                                                          (string.IsNullOrEmpty(r.Input) ? "" : " (" + r.Input + ")")).ToList();
      var parts = new List<string>();
      if (reconnected.Count > 0) parts.Add("reconnected " + string.Join(", ", reconnected));
      if (restored.Count > 0) parts.Add("restored the input " + string.Join(", ", restored));
      return parts.Count == 0 ? "" : ", and " + string.Join(", and ", parts);
    }

    /// <summary>
    /// The reconnections an APPLIED verdict reports (its "reconnected" array) - plugin-authored labels only (#625). Empty
    /// for anything that is not an applied verdict carrying them.
    /// </summary>
    public static List<ReconnectionNote> ReconnectionsFrom(JsonNode verdict)
    {
      var found = new List<ReconnectionNote>();
      try
      {
        foreach (var n in (verdict?["reconnected"] as JsonArray) ?? new JsonArray())
        {
          var type = n?["consumerType"]?.GetValue<string>();
          var id = n?["consumer"]?.GetValue<int>() ?? 0;
          var region = n?["consumerRegion"]?.GetValue<string>();
          found.Add(new ReconnectionNote
          {
            Consumer = (string.IsNullOrEmpty(type) ? "component" : type) + " (" + id + ")" +
                       (string.IsNullOrEmpty(region) ? "" : " in " + region),
            Input = n?["input"]?.GetValue<string>(),
            Restored = n?["restored"]?.GetValue<bool>() ?? false,
          });
        }
      }
      catch { }
      return found;
    }

    /// <summary>
    /// For a boundary_cut refusal, a sentence about WHAT it would have cut, built from the refusal's structure (its
    /// reason and the plugin's own consumer labels) rather than copied from its message - that message is addressed to
    /// the model, and this sentence reaches the user too (#615). Null for any other refusal, or a boundary refusal that
    /// was not about a cut (an unknown target, a missing "at").
    /// </summary>
    public static string BoundaryNote(JsonNode envelope, out bool wouldCut)
    {
      wouldCut = false;
      try
      {
        var error = envelope?["error"];
        if (error?["code"]?.GetValue<string>() != ToolErrors.BoundaryCut) return null;
        var reason = error["detail"]?["reason"]?.GetValue<string>();

        if (reason == RebuildBoundary.ReasonUnreadable)
        {
          wouldCut = true;
          return "its rebuild would have cut a wire into something Dwight cannot read (inside a cluster), so nothing " +
                 "was changed";
        }

        if (reason == RebuildBoundary.ReasonUnreconnected)
        {
          var labels = ((error["detail"]?["unreconnected"] as JsonArray) ?? new JsonArray())
            .Select(x => x?.GetValue<string>()).Where(x => !string.IsNullOrEmpty(x)).ToList();
          wouldCut = true;
          return "its rebuild would have cut the wire" + (labels.Count == 1 ? "" : "s") + " into " +
                 (labels.Count == 0 ? "a component outside it" : string.Join("; ", labels)) +
                 " without reconnecting " + (labels.Count == 1 ? "it" : "them") + ", so nothing was changed";
        }
      }
      catch { }
      return null;
    }

    private static string Count(int n) => n + (n == 1 ? " component" : " components");

    /// <summary>A standing build's facts (U26), to the bound its composer already applied - wider than a refusal's note
    /// because it carries one fact per placed component, and every cut is said by the composer.</summary>
    private static string Facts(BuildTurnRow row)
    {
      var text = Bounded(row.Note, BuildPlacedFacts.MaxChars);
      return text.Length == 0 ? "" : " " + text;
    }

    private static string Bounded(string note) => Bounded(note, MaxNote);

    internal static string Bounded(string note, int max)
    {
      var one = string.Join(" ", (note ?? "").Split(new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries)
        .Select(s => s.Trim()).Where(s => s.Length > 0));
      return one.Length <= max ? one : one.Substring(0, max - 3) + "...";
    }
  }
}
