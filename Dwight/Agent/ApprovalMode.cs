using System;

namespace Dwight.Agent
{
  /// <summary>Whether a consent card waits for a click or is approved on the plugin's own authority.</summary>
  internal enum ApprovalDecision { Ask = 0, Auto = 1 }

  /// <summary>
  /// What the plugin knows about a card, computed from the card's OWN record (the resolved scripts, the validated
  /// edit plan) and never from the model's text. <see cref="ApprovalMode.Decide"/> reads these and nothing else about a
  /// request, so a persuasive reason cannot move a card from Ask to Auto (#235).
  /// </summary>
  internal sealed class ApprovalFacts
  {
    /// <summary>A build card with a "Scripts in this build" section, a spot placement that carries a script, or an
    /// edit_script card: code that will run on the user's machine.</summary>
    public bool HasScripts { get; set; }

    /// <summary>An edit card whose plan contains a delete of a component. An unwire alone is not a deletion.</summary>
    public bool HasDeletions { get; set; }

    /// <summary>Reorganise or Simplify: the chat hands off to a verb with its own consent surface and apply button.</summary>
    public bool IsVerbHandOff { get; set; }

    /// <summary>This conversation's Full control is on: the one fact a run_script card turns on. False by default, so a caller that forgets it asks.</summary>
    public bool FullControl { get; set; }
  }

  /// <summary>
  /// APPROVAL MODES (Brief AL, Tharun's rulings of 30 October 2026): "Ask every time" (the default and the behaviour
  /// before this existed), "Ask for scripts and deletions only", "Do not ask".
  ///
  /// THE LAW THAT SURVIVES IS THE CONSENT MODEL'S REAL ONE: the forbidden thing is a change the panel says nothing
  /// about, not a change the user did not click. So an automatic approval still renders its card, still marks it as
  /// approved automatically, still records the approval in the transcript, still takes the one undo record and still
  /// says what stands when it closes. The mode decides only whether the click is needed.
  ///
  /// <see cref="Decide"/> is ONE pure function and it is the whole decision. GH-free (ADR-091), so every cell of its
  /// table is tested. One row is fixed by law and no mode moves it: a verb hand-off always asks. A run_script card asks too, except in one
  /// cell, "Do not ask" with Full control on (Tharun's ruling, #1189, overruling ADR-100's "always asks": someone who has turned on both has
  /// chosen, and asking stalled the chat and taught a model to ask five times).
  /// </summary>
  internal static class ApprovalMode
  {
    public const string Ask = "ask";
    public const string ScriptsAndDeletions = "scripts_and_deletions";
    public const string Never = "never";

    /// <summary>The three values, in the order the control shows them.</summary>
    public static readonly string[] All = { Ask, ScriptsAndDeletions, Never };

    /// <summary>The kind the card for a script run will carry (Full control, a later brief). Reserved here so the
    /// table's fixed row exists before the kind does.</summary>
    public const string KindRunScript = "run_script";

    public static bool IsValid(string value) => Array.IndexOf(All, value) >= 0;

    /// <summary>A stored or typed value read back: an unknown, blank or hand-edited one is "ask", the default - never an
    /// exception, and never a mode nobody chose (the <see cref="BuildStyle.Normalise"/> pattern).</summary>
    public static string Normalise(string value)
    {
      var v = (value ?? "").Trim().ToLowerInvariant();
      return IsValid(v) ? v : Ask;
    }

    /// <summary>
    /// The mode a thread was opened with, read back from the thread record. It takes NO setting as a parameter, which is the
    /// guarantee: changing the Settings default cannot change a thread that is already open, because nothing on this path
    /// reads the Settings default. A thread from a build that had no modes has no stored value and reads as "ask",
    /// the behaviour it was opened under.
    /// </summary>
    public static string ForThread(string storedOnThread) => Normalise(storedOnThread);

    // ---- the decision -------------------------------------------------------------------------------------------

    private static bool IsHandOffKind(string kind) =>
      string.Equals(kind, "reorganise", StringComparison.Ordinal) || string.Equals(kind, "simplify", StringComparison.Ordinal);

    private static bool CanBeAutoKind(string kind)
    {
      switch (kind)
      {
        case "solve": case "scaffold": case "build": case "set_value": case "edit_script": case "edit": return true;
        default: return false;   // reorganise, simplify, run_script, and any kind this table has not been told about: Ask
      }
    }

    /// <summary>
    /// <code>
    /// mode                    solve, scaffold, set_value,     build with scripts,   edit with     reorganise,   run_script
    ///                         build w/o scripts, edit w/o     edit_script           a deletion    simplify
    ///                         deletions
    /// ask                     Ask                             Ask                   Ask            Ask           Ask
    /// scripts_and_deletions   Auto                            Ask                   Ask            Ask           Ask
    /// never                   Auto                            Auto                  Auto           Ask           Auto, with Full control on
    /// </code>
    /// With Full control off a conversation has no run_script door at all, and the cell reads Ask if one is ever asked.
    /// An unknown mode is "ask". An unknown kind is Ask: a kind the table has not been told about is never approved on
    /// the plugin's own authority (fail closed).
    /// </summary>
    public static ApprovalDecision Decide(string mode, string kind, ApprovalFacts facts)
    {
      facts = facts ?? new ApprovalFacts();
      var m = Normalise(mode);
      if (m == Ask) return ApprovalDecision.Ask;

      // The one cell that runs a script without asking.
      if (string.Equals(kind, KindRunScript, StringComparison.Ordinal))
        return m == Never && facts.FullControl ? ApprovalDecision.Auto : ApprovalDecision.Ask;
      // The row no mode moves.
      if (IsHandOffKind(kind) || facts.IsVerbHandOff) return ApprovalDecision.Ask;
      if (!CanBeAutoKind(kind ?? "")) return ApprovalDecision.Ask;

      if (m == Never) return ApprovalDecision.Auto;

      // scripts_and_deletions: an edit_script card is a script by its kind, whatever the caller computed.
      var hasScripts = facts.HasScripts || string.Equals(kind, "edit_script", StringComparison.Ordinal);
      return hasScripts || facts.HasDeletions ? ApprovalDecision.Ask : ApprovalDecision.Auto;
    }

    // ---- the sentences (each one a claim, each one pinned by a test) ---------------------------------------------

    public static string Label(string mode)
    {
      switch (Normalise(mode))
      {
        case ScriptsAndDeletions: return "Ask for scripts and deletions only";
        case Never: return "Do not ask";
        default: return "Ask every time";
      }
    }

    /// <summary>The Settings sentence for a mode, exactly as the ruling wrote it.</summary>
    public static string Description(string mode)
    {
      switch (Normalise(mode))
      {
        case ScriptsAndDeletions:
          return "Ask for scripts and deletions only: Dwight applies sliders, wires, placements and builds of components on its " +
                 "own, shows each card marked as approved automatically, and still asks before a script runs or a component is deleted.";
        case Never:
          return "Do not ask: every change Dwight can check is applied on its own; it still asks before Reorganise and Simplify. With Full control on, " +
                 "a script runs without asking too, and one Ctrl-Z may not undo everything it touches. Every other change is one Ctrl-Z.";
        default:
          return "Ask every time: every change waits for your click on its card.";
      }
    }

    /// <summary>The hint under the control: what a thread does with the mode.</summary>
    public const string ThreadHint = "A conversation keeps the mode it was opened with, so changing this does not change one that is already open.";

    /// <summary>The state line a card approved automatically shows under its title, verbatim. Null for "ask", which
    /// never approves automatically.</summary>
    public static string AutoStateLine(string mode)
    {
      switch (Normalise(mode))
      {
        case ScriptsAndDeletions: return "Approved automatically (Ask for scripts and deletions only).";
        case Never: return "Approved automatically (Do not ask).";
        default: return null;
      }
    }

    /// <summary>The user text an automatic approval appends where a click appends "Yes, go ahead.", so the model's
    /// transcript shows the approval exactly as it shows a click.</summary>
    public const string AutoUserText = "Approved automatically.";

    /// <summary>The user text a click appends. Brief BG: like <see cref="AutoUserText"/>, the shell does not render it when a receipt with a
    /// state immediately precedes it (the state carries the same fact); it stays in the record and in the transcript the model reads.</summary>
    public const string ClickUserText = "Yes, go ahead.";

    /// <summary>The clause a close line gains when cards in the run were approved automatically. The count is the plugin's own. Null when there were none.
    /// Brief CL: IT SAYS CARDS BECAUSE IT COUNTS CARDS, the marked receipts on screen. It read "2 changes; each is one Ctrl-Z" beside one map and four built regions: a card
    /// can build several, each region and the tidy are their own Ctrl-Z and say so on their own lines, and a script may not be one at all, so it claims no Ctrl-Z.</summary>
    public static string CloseClause(int count)
    {
      if (count <= 0) return null;
      return count == 1
        ? "1 card was approved automatically."
        : count + " cards were approved automatically.";
    }

    // ---- facts from a card's own record ---------------------------------------------------------------------------

    /// <summary>The facts for a card, from what the plugin resolved and not from what the model wrote.</summary>
    public static ApprovalFacts FactsFor(string kind, int scriptCount, int deleteCount, bool fullControl = false) => new ApprovalFacts
    {
      FullControl = fullControl,
      HasScripts = scriptCount > 0 || string.Equals(kind, "edit_script", StringComparison.Ordinal),
      HasDeletions = deleteCount > 0,
      IsVerbHandOff = IsHandOffKind(kind),
    };
  }

  /// <summary>
  /// How many changes in this run Dwight approved on its own authority, per thread, in memory. Reset when the user sends a
  /// message of their own, because "a run" is what that message started: a turn can raise a card, approve it, and
  /// raise the next, and the close line says how many of them nobody clicked.
  /// </summary>
  internal sealed class AutoApprovalCount
  {
    private readonly System.Collections.Generic.Dictionary<string, int> _counts =
      new System.Collections.Generic.Dictionary<string, int>(StringComparer.Ordinal);
    private readonly object _gate = new object();

    public int Add(string threadId)
    {
      lock (_gate)
      {
        var k = threadId ?? "";
        _counts.TryGetValue(k, out var n);
        _counts[k] = ++n;
        return n;
      }
    }

    public int Get(string threadId)
    {
      lock (_gate) return _counts.TryGetValue(threadId ?? "", out var n) ? n : 0;
    }

    /// <summary>Brief CL: the count the screen shows. A card this turn marked is drawn before the close line and approved after the turn ends, so it is not in
    /// <see cref="Get"/> yet: the close read "1" beneath two marked receipts. The next turn's Add takes it over, so nothing counts twice.</summary>
    public int Shown(string threadId, int markedThisTurn) => Get(threadId) + Math.Max(0, markedThisTurn);

    public void Reset(string threadId)
    {
      lock (_gate) _counts.Remove(threadId ?? "");
    }
  }
}
