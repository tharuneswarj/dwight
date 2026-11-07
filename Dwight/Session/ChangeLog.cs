using System;
using System.Collections.Generic;
using System.Linq;
using Grasshopper.Kernel;

namespace Dwight.Session
{
  /// <summary>
  /// The shim History stands on (Brief AP): every Dwight site that pushes an undo record hands the id Grasshopper
  /// returned to <see cref="Note"/> on the same call, so the change's place on the stack is known from the source and
  /// never inferred. The probe of Grasshopper.dll (8.36) found every <c>RecordEvent</c>, <c>RecordAddObjectEvent</c>
  /// and <c>RecordUndoEvent(string)</c> overload returns the record's <c>Guid</c>.
  ///
  /// A site that undoes its own record because its check failed calls <see cref="Reverting"/> first, so History never
  /// offers to redo a change that failed its check (the Phase-7 rule: an auto-revert defuses redo).
  ///
  /// The approval a change ran under is set by the chat when a card is redeemed (<see cref="Approve"/>) and read by the
  /// next site of the same family: chat tools take the card's words; a verb opened from a card takes them once, within
  /// a few minutes. Anything else is the user's own click. UI thread only, like every site that calls it. Never throws.
  /// </summary>
  internal static class ChangeLog
  {
    private sealed class Approval
    {
      public string Scope;
      public string By;
      public bool ForVerb;
      public string ThreadId;
      public string Done;
      public DateTime At;
    }

    private static readonly Dictionary<GH_Document, Approval> _approval = new Dictionary<GH_Document, Approval>();
    private static readonly TimeSpan VerbApprovalLife = TimeSpan.FromMinutes(5);

    // Every id noted this session with a number that only goes up, so a mark taken before a change can find the ids
    // noted after it even when the stored list is at its cap. Bounded; a mark older than the bound finds nothing.
    private static readonly List<(int Seq, GH_Document Doc, Guid Id)> _noted = new List<(int, GH_Document, Guid)>();
    private static int _seq;

    /// <summary>A card was redeemed: the changes it leads to carry its words, who approved it and the chat it was redeemed in (Brief BQ: the
    /// chat id is kept here, at the approval, and never inferred from time). <paramref name="done"/> is the sentence when the card's record IS what
    /// runs (a spot); a site that knows better passes its own to <see cref="NoteChat"/>.</summary>
    public static void Approve(GH_Document doc, string scope, string approvedBy, bool forVerb, string threadId, string done = null)
    {
      if (doc == null) return;
      try { _approval[doc] = new Approval { Scope = scope, By = approvedBy, ForVerb = forVerb, ThreadId = threadId, Done = done, At = DateTime.UtcNow }; } catch { }
    }

    /// <summary>A chat tool's change (build, map, edit, set value, script edit, tidy). <paramref name="done"/> says what ran, from the site
    /// that ran it; null only where the approval's own sentence is the exact one.</summary>
    public static void NoteChat(GH_Document doc, Guid undoId, string undoName, string done) => Note(doc, undoId, undoName, DwightVerbs.Build, false, done);

    /// <summary>The record a failed rollback leaves (CanvasWriter.RollbackOrRecord): the part of a revert still to undo. It is
    /// already marked as taken back, so it is never offered for redo.</summary>
    public static void NoteRevertRemainder(GH_Document doc, Guid undoId, string undoName)
    {
      var name = undoName ?? "";
      var verb = name.Contains("Reorganise") || name.Contains("Re-arrange");
      Note(doc, undoId, name, verb ? DwightVerbs.Reorganise : DwightVerbs.Build, verb);
      try
      {
        var c = CommandHistory.Changes(doc).FirstOrDefault(x => x.Id == undoId);
        if (c != null && undoId != Guid.Empty) { c.Reverted = true; CommandHistory.SaveChanges(doc); }
      }
      catch { }
    }

    /// <summary>A Full control run's record: History shows its delta line and "what Grasshopper recorded" in place of a scope,
    /// because one Ctrl-Z covers only what the script added through Grasshopper's own undo.</summary>
    public static void NoteFullControl(GH_Document doc, Guid undoId, string undoName, string delta)
    {
      Note(doc, undoId, undoName, DwightVerbs.Build, false);
      try
      {
        var c = CommandHistory.Changes(doc).FirstOrDefault(x => x.Id == undoId);
        if (c != null && undoId != Guid.Empty) { c.FullControl = true; c.Delta = delta ?? ""; CommandHistory.SaveChanges(doc); }
      }
      catch { }
    }

    /// <summary>A verb's change (Reorganise, Simplify, to-script) or a developer command's.</summary>
    public static void NoteVerb(GH_Document doc, Guid undoId, string undoName, string verb) => Note(doc, undoId, undoName, verb, true);

    private static void Note(GH_Document doc, Guid undoId, string undoName, string kind, bool verbSite, string done = null)
    {
      if (doc == null) return;
      try
      {
        var c = new StoredChange
        {
          UndoId = undoId == Guid.Empty ? "" : undoId.ToString("D"),
          UndoName = undoName ?? "",
          Kind = kind ?? "",
          Utc = DateTime.UtcNow.ToString("o"),
        };
        if (_approval.TryGetValue(doc, out var a) && a.ForVerb == verbSite && (!a.ForVerb || DateTime.UtcNow - a.At < VerbApprovalLife))
        {
          c.Scope = a.Scope;
          c.ApprovedBy = a.By;
          if (a.ForVerb) _approval.Remove(doc);   // a verb opened from a card applies once
          else
          {
            c.ThreadId = string.IsNullOrEmpty(a.ThreadId) ? null : a.ThreadId;   // a verb's change is its own event, whoever opened it
            done = done ?? a.Done;
            a.Done = null;   // the card's sentence is for the one change its record ran as
          }
        }
        c.Done = string.IsNullOrWhiteSpace(done) ? null : done;
        if (string.IsNullOrWhiteSpace(c.Scope)) c.Scope = HistoryCore.ScopeFromRecordName(undoName);
        if (verbSite && string.IsNullOrEmpty(c.ApprovedBy)) c.ApprovedBy = HistoryCore.ByYou;   // the verb ran from the user's click
        _seq++;
        if (undoId != Guid.Empty) { _noted.Add((_seq, doc, undoId)); if (_noted.Count > 500) _noted.RemoveAt(0); }
        CommandHistory.AppendChange(doc, c);
      }
      catch { }
    }

    /// <summary>Call just before a site undoes its own record because its check failed. Marks the change on top of the
    /// stack, if it is one of Dwight's, as taken back; a record that is not Dwight's is left alone. Returns the id of the record it
    /// marked (Guid.Empty when the top is not one of Dwight's), so a revert that cannot undo through the stack can remove that record
    /// instead (U108).</summary>
    public static Guid Reverting(GH_Document doc)
    {
      try
      {
        var top = doc?.UndoServer?.UndoGuids;
        if (top == null || top.Count == 0) return Guid.Empty;
        var change = CommandHistory.Changes(doc).FirstOrDefault(c => c.Id == top[0]);
        if (change == null) return Guid.Empty;
        if (!change.Reverted) { change.Reverted = true; CommandHistory.SaveChanges(doc); }
        return top[0];
      }
      catch { return Guid.Empty; }
    }

    /// <summary>The live stack, read on the UI thread. Empty lists when there is no document or no undo server.</summary>
    public static StackSnapshot Snapshot(GH_Document doc)
    {
      var s = new StackSnapshot();
      try
      {
        var u = doc?.UndoServer;
        if (u == null) return s;
        s.UndoIds = u.UndoGuids ?? new List<Guid>();
        s.UndoNames = u.UndoNames ?? new List<string>();
        s.RedoIds = u.RedoGuids ?? new List<Guid>();
        s.RedoNames = u.RedoNames ?? new List<string>();
      }
      catch { }
      return s;
    }

    /// <summary>How many changes the document has so far; with <see cref="TopIsNotedSince"/>, a record-id check that replaces
    /// comparing undo counts.</summary>
    public static int Mark(GH_Document doc) => _seq;

    /// <summary>Is the record on top of the stack a change noted after <paramref name="mark"/>? True only while that change
    /// still stands and nothing has been pushed above it, so undoing the top undoes exactly that change.</summary>
    public static bool TopIsNotedSince(GH_Document doc, int mark)
    {
      try
      {
        var top = doc?.UndoServer?.UndoGuids;
        if (top == null || top.Count == 0) return false;
        return _noted.Any(n => n.Seq > mark && ReferenceEquals(n.Doc, doc) && n.Id == top[0]);
      }
      catch { return false; }
    }
  }
}
