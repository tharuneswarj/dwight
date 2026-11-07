using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Dwight.Session
{
  /// <summary>
  /// One change Dwight made to a document, as History lists it (Brief AP): the id of the undo record Grasshopper
  /// returned when Dwight pushed it, the record's name, the verb or card it came from, the card's scope, who
  /// approved it, and when. Kept in the per-document store, newest first. An entry with no id was recorded where
  /// the id could not be kept, and reads as "position unknown".
  /// </summary>
  internal sealed class StoredChange
  {
    /// <summary>The undo record's Guid, as returned by <c>RecordEvent</c>; "" when unknown.</summary>
    [JsonPropertyName("undoId")] public string UndoId { get; set; }
    /// <summary>The record's name on Grasshopper's undo list ("Dwight: build region").</summary>
    [JsonPropertyName("undoName")] public string UndoName { get; set; }
    /// <summary>The verb the change belongs to (Build, Reorganise, Simplify).</summary>
    [JsonPropertyName("kind")] public string Kind { get; set; }
    /// <summary>The card's one line, or for a verb the record's own name.</summary>
    [JsonPropertyName("scope")] public string Scope { get; set; }
    /// <summary>"approved by you", "approved automatically (...)", or "" when the site could not say.</summary>
    [JsonPropertyName("approvedBy")] public string ApprovedBy { get; set; }
    [JsonPropertyName("utc")] public string Utc { get; set; }
    /// <summary>Dwight took this change back itself because its check failed. It is never offered for redo.</summary>
    [JsonPropertyName("reverted")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool Reverted { get; set; }
    /// <summary>A Full control run: <see cref="Delta"/> replaces the scope, with the words of <see cref="HistoryCore.FullControlWords"/>.</summary>
    [JsonPropertyName("fullControl")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool FullControl { get; set; }
    [JsonPropertyName("delta")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string Delta { get; set; }
    /// <summary>Brief BQ: the chat this change came from, kept at the source with the approval that led to it and never inferred from
    /// time. Null on a verb's change and on a change from an earlier build.</summary>
    [JsonPropertyName("threadId")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string ThreadId { get; set; }
    /// <summary>Brief BQ: what ran, in the past tense, from the site that ran it. Null on a verb's change and on a change from an earlier
    /// build, whose line is mapped on reading (<see cref="HistoryCore.LineOf"/>). A stored record is never rewritten.</summary>
    [JsonPropertyName("done")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string Done { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement> Extra { get; set; }

    [JsonIgnore] public Guid? Id => Guid.TryParse(UndoId, out var g) && g != Guid.Empty ? g : (Guid?)null;
  }

  /// <summary>Where a change sits on the live undo stack, read every time History renders.</summary>
  internal enum ChangeState { Standing, Undone, Gone, Unknown }

  /// <summary>What the timeline is built from (Brief BQ): the changes, the live stack, a clock, and what the host knows about each chat.</summary>
  internal sealed class TimelineInput
  {
    public IReadOnlyList<StoredChange> Changes { get; set; } = Array.Empty<StoredChange>();
    public StackSnapshot Stack { get; set; } = new StackSnapshot();
    public DateTime NowUtc { get; set; }
    /// <summary>A chat's title by thread id; null when the thread is gone from the store.</summary>
    public Func<string, string> TitleOf { get; set; }
    /// <summary>Can the dock open this thread by id?</summary>
    public Func<string, bool> CanOpen { get; set; }
  }

  /// <summary>Grasshopper's undo stack at one moment, as the core needs it. Index 0 of each list is the NEWEST record
  /// (read from Grasshopper.dll's IL: <c>PushUndoRecord</c> and <c>PushRedoRecord</c> insert at 0, and
  /// <c>FirstUndoName</c> reads index 0).</summary>
  internal sealed class StackSnapshot
  {
    public IReadOnlyList<Guid> UndoIds { get; set; } = Array.Empty<Guid>();
    public IReadOnlyList<string> UndoNames { get; set; } = Array.Empty<string>();
    public IReadOnlyList<Guid> RedoIds { get; set; } = Array.Empty<Guid>();
    public IReadOnlyList<string> RedoNames { get; set; } = Array.Empty<string>();
  }

  /// <summary>A walk on the stack to one change: how many steps, and the record each step must meet on top.</summary>
  internal sealed class WalkPlan
  {
    public bool Back { get; set; }
    public Guid Target { get; set; }
    public int Steps { get; set; }
    /// <summary>For step i, the id and name that must be on top before it runs.</summary>
    public List<Guid> ExpectedIds { get; } = new List<Guid>();
    public List<string> ExpectedNames { get; } = new List<string>();
    /// <summary>The records in the way that are not Dwight's, by their Grasshopper names, newest first.</summary>
    public List<string> YourNames { get; } = new List<string>();
    public int Dwights { get; set; }
    /// <summary>Set when the walk must not be offered; the sentence says why.</summary>
    public string Refusal { get; set; }
  }

  /// <summary>
  /// History's decisions (Brief AP; ADR-091 GH-free): each change's state from the live lists, the walk to a change
  /// with its step count and the user's own records in the way, the check that the stack has not moved, and every
  /// sentence History says. The GH shims (<c>ChangeLog</c>, <c>HistoryWalk</c>) only read the stack and step it.
  ///
  /// HISTORY IS A WALK ON GRASSHOPPER'S OWN STACK, NOT A SNAPSHOT RESTORE. Going back undoes every record above the
  /// change, the user's own included, so the confirmation names them before anything moves.
  /// </summary>
  internal static class HistoryCore
  {
    public const int MaxChanges = 200;

    // ---------------------------------------------------------------- the words (pinned)

    public const string WordStanding = "standing";
    public const string WordUndone = "undone";
    public const string WordGone = "gone";
    public const string WordUnknown = "position unknown";
    public const string RevertedLine = "Dwight took this back because its check failed.";
    public const string FullControlWords = "what Grasshopper recorded";
    public const string ByYou = "approved by you";
    public const string GoBackButton = "Go back to here";
    public const string RedoButton = "Redo to here";
    public const string EmptyLine = "Dwight has not changed this file yet.";
    public const string ConfirmBackButton = "Go back";
    public const string ConfirmRedoButton = "Redo";
    public const string CancelButton = "Cancel";
    public const string YoursWord = "Yours";
    public const string WordTakenBack = "taken back";
    public const string GoBackBeforeButton = "Go back to before this";
    public const string HereLine = "You are here";
    public const string HeadingTimeline = "Timeline";
    public const string HeadingHistory = "History";
    public const string ExpandAll = "Expand all";
    public const string CollapseAll = "Collapse all";
    public const string BackToHistory = "Back to history";
    public const string OpenChatButton = "Open chat";
    public const string NewChatTitle = "New chat";
    public const string HistoryEmpty = "No history yet on this file.";
    public const string LiveWaitingWords = "Waiting for you";
    public const string LiveRunningWords = "Working";
    /// <summary>A chat's name in the row's data. The row shows one line, so this bounds the bytes, not the view.</summary>
    public const int MaxTitleChars = 120;

    public static string ByAuto(string modeLabel) =>
      "approved automatically (" + (string.IsNullOrWhiteSpace(modeLabel) ? "by the approval mode" : modeLabel.Trim()) + ")";

    public static string StateWord(ChangeState s) =>
      s == ChangeState.Standing ? WordStanding : s == ChangeState.Undone ? WordUndone : s == ChangeState.Gone ? WordGone : WordUnknown;

    /// <summary>The scope for a change that came without a card: the record's own name, without Dwight's prefix.</summary>
    public static string ScopeFromRecordName(string undoName)
    {
      var n = (undoName ?? "").Trim();
      return n.StartsWith("Dwight: ", StringComparison.Ordinal) ? n.Substring(8) : n;
    }

    /// <summary>A chat change's scope: the card's own title, then the regions it named ("Build these regions? A1, B1").</summary>
    public static string CardScope(string cardTitle, IEnumerable<string> regions)
    {
      var named = (regions ?? Enumerable.Empty<string>()).Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r.Trim()).ToList();
      var title = (cardTitle ?? "").Trim();
      return named.Count == 0 ? title : title + " " + string.Join(", ", named);
    }

    /// <summary>The line a row shows: Full control's delta, the sentence its site wrote, or for a stored record the sentence mapped on reading.</summary>
    public static string RowLine(StoredChange c) => LineOf(c);

    /// <summary>
    /// WHAT HAPPENED, NEVER A QUESTION (Brief BQ). A change noted since this build carries the site's own sentence. A verb's change and a
    /// change from an earlier build are read from what they stored: the record's name first, because it says what ran, and the card's title
    /// only for its regions. A stored row that holds a card's question never shows that question.
    /// </summary>
    public static string LineOf(StoredChange c)
    {
      if (c == null) return "";
      if (c.FullControl) return (c.Delta ?? "") + " (" + FullControlWords + ")";
      if (!string.IsNullOrWhiteSpace(c.Done)) return c.Done.Trim();
      var fromRecord = LineFromRecordName(c.UndoName, c.Scope);
      if (fromRecord != null) return fromRecord;
      var fromCard = LineFromCardTitle(c.Scope);
      if (fromCard != null) return fromCard;
      var text = (c.Scope ?? "").Replace("?", "").Trim();
      return text.Length == 0 ? "" : text.TrimEnd('.') + ".";
    }

    // The titles an earlier build stored, frozen here: what is on disk does not change when a card's title does. The Agent tests pin every
    // title the card can still produce against this reading.
    private static readonly (string Title, string Line)[] StoredTitles =
    {
      ("Reorganise this definition?", "Reorganised this definition."),
      ("Simplify this selection?", "Simplified this selection."),
      ("Re-run the definition?", "Re-ran the definition."),
      ("Lay down a scaffold map?", "Laid down a scaffold map."),
      ("Build these regions?", "Built a region."),
      ("Set a value?", "Set one value."),
      ("Change this script?", "Changed a script."),
      ("Make these changes?", "Changed existing components."),
      ("Run this script?", "Ran a Python 3 script."),
    };

    private static string LineFromCardTitle(string scope)
    {
      var s = (scope ?? "").Trim();
      foreach (var (title, line) in StoredTitles)
        if (s.StartsWith(title, StringComparison.Ordinal)) return line;
      return null;
    }

    private static string LineFromRecordName(string undoName, string scope)
    {
      var n = ScopeFromRecordName(undoName);
      if (n.Length == 0 || n == (undoName ?? "").Trim()) return null;   // not one of Dwight's record names
      switch (n)
      {
        case "build region": return BuiltLine(scope);
        case "edit wiring": return "Changed existing components.";
        case "scaffold map": return "Laid down a scaffold map.";
        case "tidy built regions": return "Tidied the built regions.";
        case "Reorganise": return "Reorganised the layout.";
        case "Reorganise (best-practice)": return "Reorganised the layout with labelled inputs and outputs.";
        case "Re-arrange": return "Re-arranged the layout.";
        case "Simplify (better logic)": return "Simplified the logic.";
      }
      if (n.EndsWith("incomplete)", StringComparison.Ordinal))
        return n.StartsWith("Re-arrange", StringComparison.Ordinal) ? "Began a re-arrange that did not finish." : "Began a Reorganise that did not finish.";
      if (n.StartsWith("set ", StringComparison.Ordinal))
      {
        var quoted = Regex.Match(n, "\"([^\"]+)\"");
        return "Set " + (quoted.Success ? quoted.Groups[1].Value : Regex.Replace(n.Substring(4), @"\s*\(\d+\)$", "")) + ".";
      }
      if (n.StartsWith("edit ", StringComparison.Ordinal)) return "Edited " + n.Substring(5) + ".";
      var script = Regex.Match(n, @"^To script \((.+), (alongside|unverified draft|replaced (\d+))\)$");
      if (script.Success)
      {
        var lang = script.Groups[1].Value;
        if (script.Groups[2].Value == "alongside") return "Added a " + lang + " script alongside the selection.";
        if (script.Groups[3].Success) return "Replaced " + Count(int.Parse(script.Groups[3].Value), "component") + " with a " + lang + " script.";
        return "Added an unverified " + lang + " draft script.";
      }
      return null;
    }

    /// <summary>A stored build's line. One record is one region, and a card could name several, so a line that cannot say which never claims all of them.</summary>
    private static string BuiltLine(string scope)
    {
      var s = (scope ?? "").Trim();
      var q = s.IndexOf('?');
      var named = q < 0 ? Array.Empty<string>() : s.Substring(q + 1).Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToArray();
      if (named.Length == 0) return "Built a region.";
      return named.Length == 1 ? "Built " + named[0] + "." : "Built one of " + string.Join(", ", named) + ".";
    }

    /// <summary>
    /// A CHAT'S NAME (Brief BQ): the first thing the user asked in it, whitespace collapsed, computed from the thread so it works for every old
    /// chat. An approval's echo is not something the user asked: it carries a flag since U111, and an earlier build stored the same two
    /// sentences without one. Before the first message, <see cref="NewChatTitle"/>.
    /// </summary>
    public static string ThreadTitle(IEnumerable<StoredMessage> messages)
    {
      foreach (var m in messages ?? Enumerable.Empty<StoredMessage>())
      {
        if (m == null || m.Who != "me" || m.Echo || string.IsNullOrWhiteSpace(m.Src)) continue;
        var text = string.Join(" ", m.Src.Split((char[])null, StringSplitOptions.RemoveEmptyEntries));
        if (text == Dwight.Agent.ApprovalMode.ClickUserText || text == Dwight.Agent.ApprovalMode.AutoUserText) continue;
        return text.Length > MaxTitleChars ? text.Substring(0, MaxTitleChars).TrimEnd() : text;
      }
      return NewChatTitle;
    }

    /// <summary>Brief EC: a run's chat is named for the run ("Explain, 14:02") whatever is asked in it; any other chat by its first question.</summary>
    public static string ThreadName(StoredThread thread) =>
      thread == null ? null : !string.IsNullOrWhiteSpace(thread.RunTitle) ? thread.RunTitle : ThreadTitle(thread.Messages);

    /// <summary>Brief EC: "Explain, 14:02", the verb and the run's local time.</summary>
    public static string RunThreadTitle(string verb, DateTime localTime) =>
      (string.IsNullOrWhiteSpace(verb) ? "Run" : verb.Trim()) + ", " + localTime.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// ONE RUN, ONE CHAT (Brief EC unit 5, Tharun's sitting): the chat already opened from this run, whichever door opened it, or null. A run with no key (a row
    /// from an older build) finds none, so each door opens a chat as it always did.
    /// </summary>
    public static string ThreadOfRun(IEnumerable<StoredThread> threads, string runKey)
    {
      if (string.IsNullOrEmpty(runKey)) return null;
      foreach (var t in threads ?? Enumerable.Empty<StoredThread>())
        if (t != null && !string.IsNullOrEmpty(t.Id) && string.Equals(t.RunKey, runKey, StringComparison.Ordinal)) return t.Id;
      return null;
    }

    /// <summary>A list of conversations is ordered by when they were last alive: the matching item moves to the front, and nothing else moves.
    /// Returns whether it found one.</summary>
    public static bool MoveToFront<T>(IList<T> list, Func<T, bool> isIt)
    {
      if (list == null) return false;
      for (var i = 0; i < list.Count; i++)
      {
        if (!isIt(list[i])) continue;
        var item = list[i];
        list.RemoveAt(i);
        list.Insert(0, item);
        return true;
      }
      return false;
    }

    /// <summary>The words beside a row's live state (Brief BR), so the shell writes no word. Null for a row with no live state.</summary>
    public static string LiveWords(string live) =>
      live == "waiting" ? LiveWaitingWords : live == "running" ? LiveRunningWords : null;

    /// <summary>The short time a timeline row shows: "now", "9m", "3h", "2d".</summary>
    public static string ShortAgo(string utc, DateTime nowUtc)
    {
      if (!DateTime.TryParse(utc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var t)) return "";
      var d = nowUtc - t.ToUniversalTime();
      if (d.TotalSeconds < 60) return "now";
      if (d.TotalMinutes < 60) return (int)d.TotalMinutes + "m";
      if (d.TotalHours < 24) return (int)d.TotalHours + "h";
      return (int)d.TotalDays + "d";
    }

    // ---------------------------------------------------------------- state

    public static ChangeState StateOf(StoredChange c, StackSnapshot s)
    {
      var id = c?.Id;
      if (id == null) return ChangeState.Unknown;
      if (s?.UndoIds != null && s.UndoIds.Contains(id.Value)) return ChangeState.Standing;
      if (s?.RedoIds != null && s.RedoIds.Contains(id.Value)) return ChangeState.Undone;
      return ChangeState.Gone;
    }

    /// <summary>The stack is the same as when the walk was counted: both lists, the same ids in the same order.</summary>
    public static bool SameStack(StackSnapshot a, StackSnapshot b) =>
      a != null && b != null && a.UndoIds.SequenceEqual(b.UndoIds) && a.RedoIds.SequenceEqual(b.RedoIds);

    // ---------------------------------------------------------------- the walks

    /// <summary>Go back to a standing change: undo every record above it, so it is the newest thing that stands. Null when
    /// the change is not standing or nothing is above it (there is nothing to offer).</summary>
    public static WalkPlan PlanBack(Guid target, StackSnapshot s, ISet<Guid> dwightIds)
    {
      var idx = IndexOf(s?.UndoIds, target);
      if (idx <= 0) return null;
      var plan = new WalkPlan { Back = true, Target = target, Steps = idx };
      Fill(plan, s.UndoIds, s.UndoNames, idx, dwightIds);
      return plan;
    }

    /// <summary>Go back to before a standing change (Brief BQ): undo it and every record above it. What someone looking at an event usually
    /// wants, and the only walk that can undo the oldest change. Null when the change is not standing.</summary>
    public static WalkPlan PlanBackBefore(Guid target, StackSnapshot s, ISet<Guid> dwightIds)
    {
      var idx = IndexOf(s?.UndoIds, target);
      if (idx < 0) return null;
      var plan = new WalkPlan { Back = true, Target = target, Steps = idx + 1 };
      Fill(plan, s.UndoIds, s.UndoNames, idx + 1, dwightIds);
      return plan;
    }

    /// <summary>Redo to an undone change: redo every record down to it, itself included. A walk that would redo a change
    /// Dwight took back is refused, because that change failed its check.</summary>
    public static WalkPlan PlanRedo(Guid target, StackSnapshot s, ISet<Guid> dwightIds, ISet<Guid> revertedIds)
    {
      var idx = IndexOf(s?.RedoIds, target);
      if (idx < 0) return null;
      var plan = new WalkPlan { Back = false, Target = target, Steps = idx + 1 };
      Fill(plan, s.RedoIds, s.RedoNames, idx + 1, dwightIds);
      if (revertedIds != null && plan.ExpectedIds.Any(revertedIds.Contains)) plan.Refusal = RedoCrossesRevertLine;
      return plan;
    }

    /// <summary>Before step <paramref name="step"/>: is the record on top the one that was counted? Both the id and the name.</summary>
    public static bool StepMatches(WalkPlan plan, int step, Guid topId, string topName) =>
      plan != null && step >= 0 && step < plan.Steps &&
      plan.ExpectedIds[step] == topId && string.Equals(plan.ExpectedNames[step] ?? "", topName ?? "", StringComparison.Ordinal);

    private static void Fill(WalkPlan plan, IReadOnlyList<Guid> ids, IReadOnlyList<string> names, int count, ISet<Guid> dwightIds)
    {
      for (var i = 0; i < count; i++)
      {
        var id = ids[i];
        var name = names != null && i < names.Count ? names[i] ?? "" : "";
        plan.ExpectedIds.Add(id);
        plan.ExpectedNames.Add(name);
        if (dwightIds != null && dwightIds.Contains(id)) plan.Dwights++;
        else plan.YourNames.Add(string.IsNullOrWhiteSpace(name) ? "unnamed" : name.Trim());
      }
    }

    private static int IndexOf(IReadOnlyList<Guid> list, Guid id)
    {
      if (list == null) return -1;
      for (var i = 0; i < list.Count; i++) if (list[i] == id) return i;
      return -1;
    }

    // ---------------------------------------------------------------- the sentences of a walk (pinned)

    /// <summary>"This undoes 5 changes, 3 of them yours (Move, Move, Delete) and 2 of Dwight's. Go back?"</summary>
    public static string Confirmation(WalkPlan p)
    {
      var verb = p.Back ? "undoes" : "redoes";
      var lead = "This " + verb + " " + Count(p.Steps, "change");
      var yours = p.YourNames.Count;
      var names = "(" + string.Join(", ", p.YourNames) + ")";
      string who;
      if (yours == 0) who = p.Steps == 1 ? ", Dwight's" : ", all of them Dwight's";
      else if (p.Dwights == 0) who = (p.Steps == 1 ? ", yours " : ", all of them yours ") + names;
      else who = ", " + yours + " of them yours " + names + " and " + p.Dwights + " of Dwight's";
      return lead + who + ". " + (p.Back ? "Go back?" : "Redo?");
    }

    /// <summary>"Went back 5 changes. Standing: ..." after a walk that completed.</summary>
    public static string Result(bool back, int steps, IReadOnlyList<string> standing) =>
      (back ? "Went back " : "Redid ") + Count(steps, "change") + ". " + StandingLine(standing);

    /// <summary>What of Dwight's stands now, newest first; at most three named.</summary>
    public static string StandingLine(IReadOnlyList<string> standing)
    {
      // A row's line is a sentence; here they are clauses of one.
      var list = (standing ?? Array.Empty<string>()).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim().TrimEnd('.', '?')).ToList();
      if (list.Count == 0) return "None of Dwight's changes are standing.";
      var shown = string.Join("; ", list.Take(3));
      return "Standing: " + shown + (list.Count > 3 ? "; and " + (list.Count - 3) + " more." : ".");
    }

    /// <summary>The walk refused before its first step because the stack moved after it was counted.</summary>
    public static string MovedLine(bool back) =>
      "The undo history changed after this was counted, so nothing was " + (back ? "undone" : "redone") +
      ". Look at the list again and choose.";

    /// <summary>A step met a record it did not count; the walk stops there.</summary>
    public static string StoppedLine(bool back, int taken, int planned, string expected, string found) =>
      "Stopped after " + taken + " of " + Count(planned, "step") + ", because the next change on the " + (back ? "undo" : "redo") +
      " history was \"" + Name(found) + "\", not the \"" + Name(expected) + "\" that was counted.";

    /// <summary>Grasshopper could not take a step; the walk stops there.</summary>
    public static string StepFailedLine(bool back, int taken, int planned, string name) =>
      "Stopped after " + taken + " of " + Count(planned, "step") + ", because Grasshopper could not " + (back ? "undo" : "redo") +
      " \"" + Name(name) + "\".";

    public const string RedoCrossesRevertLine =
      "This would redo a change Dwight took back because its check failed, so nothing was redone.";

    public const string NoDocumentLine = "Open the definition these changes belong to first.";

    /// <summary>Brief BR: a walk under a running build would change the stack the turn's verdict is measured against. The list stays readable.</summary>
    public const string WalkBusyLine = "Dwight is working. Wait for it to finish, or stop it, then go back.";

    public static string Count(int n, string noun) => n + " " + noun + (n == 1 ? "" : "s");

    private static string Name(string n) => string.IsNullOrWhiteSpace(n) ? "unnamed" : n.Trim();

    // ---------------------------------------------------------------- the timeline (Brief BQ)

    private enum EntryKind { Change, Yours, Here }

    private sealed class Entry
    {
      public EntryKind Kind;
      public StoredChange Change;
      public ChangeState State;
      public Guid Id;
      public string Name;
      public bool YoursUndone;
    }

    private sealed class Run
    {
      public string GroupKey;
      public readonly List<Entry> Entries = new List<Entry>();
      public IEnumerable<StoredChange> Changes => Entries.Where(e => e.Kind == EntryKind.Change).Select(e => e.Change);
    }

    private static bool IsChatChange(StoredChange c) => DwightVerbs.Canonical(c.Kind) == DwightVerbs.Build;

    /// <summary>Consecutive changes with one key are one event. A chat's are keyed by the chat, and an earlier build's chat changes by the kind
    /// alone. A verb's change has no key and is always its own event.</summary>
    private static string GroupKeyOf(StoredChange c) =>
      !IsChatChange(c) ? null : string.IsNullOrEmpty(c.ThreadId) ? "old" : "chat:" + c.ThreadId;

    /// <summary>
    /// EVERYTHING ON ONE TIMELINE, NEWEST FIRST. The stack orders itself: the redo list reversed (its index 0 is the next to come back, so the
    /// oldest of what was undone), then the undo list. The stored list orders what is on neither. The user's records come from the stack, as
    /// the confirmation reads them, and the ones older than Dwight's oldest change are dropped: nothing in History can reach them.
    /// "You are here" is drawn only when something is undone, and a change Dwight took back itself does not count: nobody undid it.
    /// </summary>
    private static List<Entry> Timeline(TimelineInput input)
    {
      var changes = (input.Changes ?? Array.Empty<StoredChange>()).Where(c => c != null).ToList();
      var s = input.Stack ?? new StackSnapshot();
      var ids = new List<Guid>(); var names = new List<string>();
      for (var i = s.RedoIds.Count - 1; i >= 0; i--) { ids.Add(s.RedoIds[i]); names.Add(i < s.RedoNames.Count ? s.RedoNames[i] : ""); }
      var redoCount = ids.Count;
      for (var i = 0; i < s.UndoIds.Count; i++) { ids.Add(s.UndoIds[i]); names.Add(i < s.UndoNames.Count ? s.UndoNames[i] : ""); }

      var dwight = new HashSet<Guid>(changes.Where(c => c.Id != null).Select(c => c.Id.Value));
      var taken = new HashSet<Guid>(changes.Where(c => c.Id != null && c.Reverted).Select(c => c.Id.Value));
      var showHere = s.RedoIds.Any(id => !taken.Contains(id));
      var hereDone = !showHere;
      var at = new Dictionary<Guid, int>();
      for (var i = 0; i < ids.Count; i++) at[ids[i]] = i;

      var outp = new List<Entry>();
      var p = 0;
      void ReachIndex(int end)
      {
        for (; p < end; p++)
        {
          if (dwight.Contains(ids[p])) continue;   // met in the stored order, below
          outp.Add(new Entry { Kind = EntryKind.Yours, Id = ids[p], Name = string.IsNullOrWhiteSpace(names[p]) ? "unnamed" : names[p].Trim(), YoursUndone = p < redoCount });
        }
        DrawHereIfReached();
      }
      void DrawHereIfReached()
      {
        if (!hereDone && p == redoCount) { outp.Add(new Entry { Kind = EntryKind.Here }); hereDone = true; }
      }

      foreach (var c in changes)
      {
        if (c.Id != null && at.TryGetValue(c.Id.Value, out var i) && i >= p)
        {
          ReachIndex(i);
          p = i + 1;
          outp.Add(new Entry { Kind = EntryKind.Change, Change = c, State = StateOf(c, s), Id = c.Id.Value });
          DrawHereIfReached();   // the marker sits straight under the last thing undone, above anything stored but off the stack
          continue;
        }
        outp.Add(new Entry { Kind = EntryKind.Change, Change = c, State = StateOf(c, s), Id = c.Id ?? Guid.Empty });
      }
      // The rest of the redo list is above the marker and still reachable; the rest of the undo list is older than anything of Dwight's.
      if (!hereDone) ReachIndex(redoCount);
      return outp;
    }

    /// <summary>
    /// THE EVENTS (a pure function, ADR-091). Consecutive changes from one chat are one event, and another verb or another chat between them
    /// splits it: that is the sandwich. The user's records never split an event: flanked by one event's changes they are its quiet steps,
    /// otherwise one quiet line between events. "You are here" always splits, so a row has one state.
    /// </summary>
    public static JsonArray TimelineItems(TimelineInput input)
    {
      var items = new JsonArray();
      var dwightIds = new HashSet<Guid>((input.Changes ?? Array.Empty<StoredChange>()).Where(c => c?.Id != null).Select(c => c.Id.Value));
      Run run = null;
      var yours = new List<Entry>();

      void CloseRun()
      {
        if (run != null) items.Add(EventJson(run, input, dwightIds));
        run = null;
      }
      void FlushYours()
      {
        if (yours.Count > 0) items.Add(YoursJson(yours));
        yours = new List<Entry>();
      }

      foreach (var e in Timeline(input))
      {
        if (e.Kind == EntryKind.Yours) { yours.Add(e); continue; }
        if (e.Kind == EntryKind.Here)
        {
          CloseRun();
          FlushYours();
          items.Add(new JsonObject { ["type"] = "here", ["line"] = HereLine });
          continue;
        }
        var key = GroupKeyOf(e.Change);
        if (run != null && key != null && run.GroupKey == key)
        {
          run.Entries.AddRange(yours);
          yours = new List<Entry>();
          run.Entries.Add(e);
          continue;
        }
        CloseRun();
        FlushYours();
        run = new Run { GroupKey = key };
        run.Entries.Add(e);
      }
      CloseRun();
      FlushYours();
      return items;
    }

    /// <summary>The whole of <c>dwightDockHistory</c>'s new fields: the items and every word the view shows. Today's fields sit beside them until the shell moves.</summary>
    public static JsonObject TimelineView(TimelineInput input, string confirm, IEnumerable<Guid> confirmIds, bool confirmBack, string note)
    {
      var tinted = new JsonArray();
      if (confirm != null) foreach (var id in confirmIds ?? Enumerable.Empty<Guid>()) tinted.Add(JsonValue.Create(id.ToString("D")));
      return new JsonObject
      {
        ["items"] = TimelineItems(input),
        ["heading"] = HeadingTimeline,
        ["historyHeading"] = HeadingHistory,
        ["newChat"] = NewChatTitle,
        ["expandAll"] = ExpandAll,
        ["collapseAll"] = CollapseAll,
        ["back"] = BackToHistory,
        ["historyEmpty"] = HistoryEmpty,
        ["empty"] = EmptyLine,
        ["confirm"] = confirm,
        ["confirmIds"] = tinted,
        ["yes"] = confirmBack ? ConfirmBackButton : ConfirmRedoButton,
        ["no"] = CancelButton,
        ["note"] = note,
      };
    }

    private static string StateName(StoredChange c, ChangeState s) =>
      c.Reverted ? "takenback" : s.ToString().ToLowerInvariant();

    private static string StateWordOf(StoredChange c, ChangeState s) => c.Reverted ? WordTakenBack : StateWord(s);

    private static JsonObject ActionJson(string action, Guid id, string button) =>
      new JsonObject { ["action"] = action, ["id"] = id.ToString("D"), ["button"] = button };

    private static JsonObject YoursJson(List<Entry> yours)
    {
      var names = yours.Select(y => y.Name).ToList();
      var line = YoursWord + ": " + string.Join(", ", names.Take(3)) + (names.Count > 3 ? " and " + (names.Count - 3) + " more" : "");
      var ids = new JsonArray();
      foreach (var y in yours) ids.Add(JsonValue.Create(y.Id.ToString("D")));
      return new JsonObject { ["type"] = "yours", ["line"] = line, ["ids"] = ids };
    }

    private static JsonObject EventJson(Run run, TimelineInput input, ISet<Guid> dwightIds)
    {
      var stack = input.Stack ?? new StackSnapshot();
      var mine = run.Entries.Where(e => e.Kind == EntryKind.Change).ToList();
      var live = mine.Where(e => !e.Change.Reverted).ToList();
      var newest = mine[0].Change;
      var oldest = mine[mine.Count - 1].Change;

      // A row keeps its state's plain reading: a step taken back inside a standing event shows only when opened.
      Entry First(ChangeState st) => live.FirstOrDefault(e => e.State == st);
      var lead = First(ChangeState.Standing) ?? First(ChangeState.Undone) ?? First(ChangeState.Gone) ?? First(ChangeState.Unknown);
      var state = lead == null ? "takenback" : lead.State.ToString().ToLowerInvariant();
      var word = lead == null ? WordTakenBack : StateWord(lead.State);

      var actions = new JsonArray();
      var standing = live.Where(e => e.State == ChangeState.Standing && e.Id != Guid.Empty).ToList();
      var undone = live.Where(e => e.State == ChangeState.Undone && e.Id != Guid.Empty).ToList();
      if (standing.Count > 0)
      {
        if (PlanBack(standing[0].Id, stack, dwightIds) != null) actions.Add(ActionJson("back", standing[0].Id, GoBackButton));
        actions.Add(ActionJson("before", standing[standing.Count - 1].Id, GoBackBeforeButton));
      }
      else if (undone.Count > 0) actions.Add(ActionJson("redo", undone[0].Id, RedoButton));

      var steps = new JsonArray();
      foreach (var e in run.Entries) steps.Add(StepJson(e, stack, dwightIds, input.NowUtc));

      var isChat = IsChatChange(newest);
      var thread = isChat && !string.IsNullOrEmpty(newest.ThreadId) ? newest.ThreadId : null;
      return new JsonObject
      {
        ["type"] = "event",
        ["key"] = oldest.Id?.ToString("D") ?? "utc:" + (oldest.Utc ?? ""),   // the oldest step's: stable as the chat grows
        ["state"] = state,
        ["word"] = word,
        ["verb"] = isChat ? DwightVerbs.Build : newest.Kind ?? "",
        // A chat is named by its chat; a verb by what it did; an earlier build's chat, or one gone from the store, by its kind alone.
        ["title"] = isChat ? (thread == null ? "" : input.TitleOf?.Invoke(thread) ?? "") : LineOf(newest),
        ["count"] = mine.Count,
        ["countWord"] = mine.Count > 1 ? Count(mine.Count, "step") : null,   // the words are the plugin's; a single change has no count to show
        ["time"] = ShortAgo(newest.Utc, input.NowUtc),
        ["threadId"] = thread,
        ["openChat"] = thread != null && input.CanOpen != null && input.CanOpen(thread) ? OpenChatButton : null,
        ["by"] = MostCommonBy(mine.Select(e => e.Change.ApprovedBy)),
        ["actions"] = actions,
        ["steps"] = steps,
      };
    }

    /// <summary>
    /// An event's approval (#1153): the most common among its steps, newest first so the newest step's wins a tie. One approval taken from the
    /// newest step alone made a chat the user approved read "approved automatically". Each step carries its own, and the shell shows a step's
    /// only where it differs.
    /// </summary>
    public static string MostCommonBy(IEnumerable<string> newestFirst)
    {
      var named = (newestFirst ?? Enumerable.Empty<string>()).Where(b => !string.IsNullOrWhiteSpace(b)).ToList();
      if (named.Count == 0) return null;
      // GroupBy keeps first-seen order and OrderByDescending is stable, so among equal counts the one the newest step holds stays first.
      return named.GroupBy(b => b, StringComparer.Ordinal).OrderByDescending(g => g.Count()).First().Key;
    }

    private static JsonObject StepJson(Entry e, StackSnapshot stack, ISet<Guid> dwightIds, DateTime nowUtc)
    {
      if (e.Kind == EntryKind.Yours)
      {
        var st = e.YoursUndone ? ChangeState.Undone : ChangeState.Standing;
        return new JsonObject
        {
          ["id"] = e.Id.ToString("D"),
          ["state"] = st.ToString().ToLowerInvariant(),
          ["word"] = StateWord(st),
          ["line"] = YoursWord + ": " + e.Name,
          ["time"] = "",
          ["yours"] = true,
          ["reverted"] = null,
          ["action"] = null,
        };
      }
      var c = e.Change;
      JsonObject action = null;
      if (!c.Reverted && e.Id != Guid.Empty)
      {
        if (e.State == ChangeState.Standing && PlanBack(e.Id, stack, dwightIds) != null) action = ActionJson("back", e.Id, GoBackButton);
        else if (e.State == ChangeState.Undone) action = ActionJson("redo", e.Id, RedoButton);
      }
      return new JsonObject
      {
        ["id"] = c.Id?.ToString("D") ?? "",
        ["state"] = StateName(c, e.State),
        ["word"] = StateWordOf(c, e.State),
        ["line"] = LineOf(c),
        ["time"] = ShortAgo(c.Utc, nowUtc),
        ["by"] = string.IsNullOrWhiteSpace(c.ApprovedBy) ? null : c.ApprovedBy,
        ["yours"] = false,
        ["reverted"] = c.Reverted ? RevertedLine : null,
        ["action"] = action,
      };
    }

    // ---------------------------------------------------------------- the store

    /// <summary>Newest first, no null entries, within <see cref="MaxChanges"/>. Applied on every read and write of the record.</summary>
    public static List<StoredChange> Normalize(List<StoredChange> changes) =>
      (changes ?? new List<StoredChange>()).Where(c => c != null).Take(MaxChanges).ToList();
  }
}
