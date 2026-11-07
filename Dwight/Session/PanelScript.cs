using System;
using System.Collections.Generic;
using System.Linq;

namespace Dwight.Session
{
  /// <summary>
  /// The panel operations a recorded result may replay. Deliberately Eto-free and GH-free (ADR-091) so
  /// <see cref="PanelScript.Play"/> - which owns the whitelist, the validation and the skip accounting - is
  /// unit-tested headlessly; <c>Dwight.UI.PanelSink</c> is the thin adapter onto the real
  /// <c>IResultPanel</c>.
  ///
  /// This is EXACTLY the whitelist, as an interface: an op that is not here cannot be replayed, and the
  /// compiler enforces that rather than a convention.
  /// </summary>
  internal interface IPanelSink
  {
    void SetMarkdown(string markdown);
    void SetContent(string text);
    void SetStatus(string text);
    void SetSteps(string stepsJson);
    /// <summary>Brief EI: a Learn run's deck, its cards and the answers given, as data. Played after <see cref="SetSteps"/>, so the deck stands over the steps it was made from.</summary>
    void SetDeck(string deckJson);
    void SetFaults(string faultsJson);
    void SetChat(string messagesJson);
    /// <summary>WebPanel-only in the UI layer; the adapter degrades it to <see cref="SetContent"/> exactly as
    /// the shipped non-WebPanel branch already does.</summary>
    void SetOutcome(string text);
    void SetError(string message);
    /// <summary>THREE args only. The live <c>IResultPanel.ShowState</c> also takes actionLabel/actionCmd pairs,
    /// and those are deliberately NOT replayable - a persisted "retry" command string without its handler is a
    /// dead button, which is a panel state the canvas contradicts.</summary>
    void ShowState(string kind, string title, string message);
  }

  /// <summary>What a replay actually managed to do. A partial replay must be VISIBLE, never silent (#161).</summary>
  internal sealed class PlayReport
  {
    public int Played;
    public int Skipped;
    public List<string> SkippedOps = new List<string>();

    /// <summary>An honest, appendable note when something was lost - null when nothing was. Formatted here
    /// rather than at the call site so the wording is tested, not retyped.</summary>
    public string Note => Skipped <= 0
      ? null
      : $"· {Skipped} part{(Skipped == 1 ? "" : "s")} of this result couldn't be restored";
  }

  /// <summary>
  /// A recorded result's content, as replayable DATA rather than a captured closure (WP-1 step 3).
  ///
  /// THE PROBLEM IT SOLVES. Until now a dock entry replayed by invoking a stored
  /// <c>Action&lt;IResultPanel&gt;</c>. That works within a session and cannot possibly work across one: a
  /// closure is not serializable, and several of ours are STATEFUL (they capture a first-render flag) or bound
  /// to live objects (a solved graph, an apply state, a document). So durable history is not "write the history
  /// to JSON" - it is replacing replay-by-delegate with replay-by-data. This is that data.
  ///
  /// THE SAFETY PROPERTY IS STRUCTURAL, NOT PROCEDURAL. A script is a list of op names and strings. There is no
  /// field a delegate could occupy, so a persisted record CANNOT carry a mutation offer or a paid-call offer -
  /// the rule is unrepresentable rather than remembered. Live affordances travel separately, in an in-memory
  /// field that is not part of the wire format, and are therefore absent after a restart BY CONSTRUCTION.
  ///
  /// IT REUSES THE STORE'S OWN WIRE TYPE (<see cref="StoredOp"/>) on purpose: no conversion layer means no
  /// layer in which what is shown and what is replayed could drift - #153's law applied to replay.
  /// </summary>
  internal static class PanelScript
  {
    public const string SetMarkdown = "SetMarkdown";
    public const string SetContent = "SetContent";
    public const string SetStatus = "SetStatus";
    public const string SetSteps = "SetSteps";
    public const string SetDeck = "SetDeck";
    public const string SetFaults = "SetFaults";
    public const string SetChat = "SetChat";
    public const string SetOutcome = "SetOutcome";
    public const string SetError = "SetError";
    public const string ShowState = "ShowState";

    /// <summary>op -> required arg count. THE closed whitelist; everything else is skipped and counted.
    ///
    /// Excluded on purpose, each for its own reason: SetThinking / SetRunMeta / SetStage are transient loader
    /// state (replaying them would show a finished result as still running); AppendChat is incremental and
    /// would duplicate a conversation that SetChat already carries whole; SetSimplifyApplied /
    /// SetSimplifyOutcome ARE the live-affordance surfaces (Undo / Re-apply / Locate) and would render buttons
    /// with no handlers; SetPreview had no call site at all (#169 C1 - a closed set contains only what is
    /// used, and previews are never recorded because nothing reaches the dock until the user applies) and the
    /// panel method itself is now DELETED (#326), so this exclusion is historical - the op could not be
    /// emitted today even by mistake.</summary>
    private static readonly Dictionary<string, int> Whitelist = new Dictionary<string, int>(StringComparer.Ordinal)
    {
      [SetMarkdown] = 1,
      [SetContent] = 1,
      [SetStatus] = 1,
      [SetSteps] = 1,
      [SetDeck] = 1,
      [SetFaults] = 1,
      [SetChat] = 1,
      [SetOutcome] = 1,
      [SetError] = 1,
      [ShowState] = 3,
    };

    public static IReadOnlyCollection<string> KnownOps => Whitelist.Keys;

    public static bool IsKnown(string op) => !string.IsNullOrEmpty(op) && Whitelist.ContainsKey(op);

    public static Builder New() => new Builder();

    /// <summary>
    /// Brief EI: a recorded script with one op's argument replaced, as a deck's answers are written into its run's record. Only an op the script already holds is
    /// changed, the first of its name, and nothing is added: a row that never had a deck is not given one. True when it changed.
    /// </summary>
    public static bool Replace(List<StoredOp> script, string op, string arg)
    {
      var at = script?.FirstOrDefault(o => o != null && o.Op == op && o.Args != null && o.Args.Count >= 1);
      if (at == null || !IsKnown(op)) return false;
      at.Args[0] = arg ?? "";
      return true;
    }

    /// <summary>Fluent construction, so a call site reads as the sequence of panel calls it replaces.</summary>
    internal sealed class Builder
    {
      private readonly List<StoredOp> _ops = new List<StoredOp>();

      public Builder Op(string op, params string[] args)
      {
        _ops.Add(new StoredOp { Op = op, Args = (args ?? new string[0]).ToList() });
        return this;
      }

      /// <summary>Add only when the value is worth adding - keeps a script free of empty status lines.</summary>
      public Builder OpIf(bool when, string op, params string[] args) => when ? Op(op, args) : this;

      public List<StoredOp> Build() => _ops;
    }

    /// <summary>
    /// Replay a script onto a sink. NEVER THROWS, whatever the script or the sink does.
    ///
    /// Three ways an op is skipped, all counted and reported: it is not on the whitelist (a newer Dwight's op
    /// in an older build - which must not blank the panel), it carries too few args to call safely (never
    /// guessed at), or the sink itself threw (a WebView call can fail, and one failure must not abandon the
    /// rest of the panel - the fail-safe guardrail, mother doc 4.6).
    /// </summary>
    public static PlayReport Play(IEnumerable<StoredOp> script, IPanelSink sink)
    {
      var report = new PlayReport();
      if (script == null || sink == null) return report;

      foreach (var op in script)
      {
        if (op == null || string.IsNullOrEmpty(op.Op)) { Skip(report, "(none)"); continue; }
        if (!Whitelist.TryGetValue(op.Op, out var arity)) { Skip(report, op.Op); continue; }

        var args = op.Args ?? new List<string>();
        if (args.Count < arity) { Skip(report, op.Op); continue; }

        try
        {
          switch (op.Op)
          {
            case SetMarkdown: sink.SetMarkdown(args[0]); break;
            case SetContent: sink.SetContent(args[0]); break;
            case SetStatus: sink.SetStatus(args[0]); break;
            case SetSteps: sink.SetSteps(args[0]); break;
            case SetDeck: sink.SetDeck(args[0]); break;
            case SetFaults: sink.SetFaults(args[0]); break;
            case SetChat: sink.SetChat(args[0]); break;
            case SetOutcome: sink.SetOutcome(args[0]); break;
            case SetError: sink.SetError(args[0]); break;
            case ShowState: sink.ShowState(args[0], args[1], args[2]); break;
            default: Skip(report, op.Op); continue;   // unreachable while the switch mirrors the whitelist
          }
          report.Played++;
        }
        catch
        {
          Skip(report, op.Op);
        }
      }
      return report;
    }

    private static void Skip(PlayReport r, string op)
    {
      r.Skipped++;
      if (!r.SkippedOps.Contains(op)) r.SkippedOps.Add(op);
    }
  }
}
