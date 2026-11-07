using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Eto.Forms;
using Grasshopper;
using Grasshopper.Kernel;
using Dwight.Session;
using Dwight.UI;

namespace Dwight
{
  /// <summary>The one coordinated Dwight surface (ADR-046; spec §3, §11). Begin opens the right-side
  /// panel immediately (showing the loader) and lights the canvas glow on the selection; the actions
  /// drive the panel loader/stages and call Complete on success to settle the glow and record the
  /// command. (The earlier cursor "thinking chip" was removed - a non-cursor-tracking element was a
  /// bug magnet; the panel-with-loading + the glow are the thinking indicators.)</summary>
  internal static class DwightSession
  {
    internal sealed class Context
    {
      public string Action;
      public IResultPanel Panel;
      public GH_Document Doc;
      public List<Guid> Guids = new List<Guid>();
      public List<Guid> SecondaryGuids;                 // the lighter tier on reopen (Diagnose chain) - D4
      public Dictionary<Guid, int> Labels;              // guid -> id, so reopen restores the number badges
      /// <summary>Phase-8 WP-0 (#92): the better-logic offered-retry generation count. 1 on a fresh run; a revert
      /// that offers a corrected proposal increments it, so the fresh preview's meta line records the spend
      /// (attempt N). Carried on the ctx because the ctx is the one thing that flows generation -> apply -> outcome
      /// -> retry unchanged.</summary>
      public int SimplifyAttempt;
      /// <summary>What this run's model calls have cost so far (Simplify across its attempts, to-script across its
      /// refine loop). Known before the apply closures run, so it rides the ctx - the one object that flows
      /// generation -> apply -> outcome - and is handed to <see cref="Complete"/> where the record is written.
      /// An apply that is reverted leaves it standing: the tokens were spent. Brief CT: it also holds a billed Explain
      /// or Diagnose call that failed, and Reorganise's labelling, for the line <c>LedgerRun</c> writes.</summary>
      public StoredSpend Spend;
      /// <summary>Brief CT: <see cref="Spend"/>'s line is on the ledger, written when the run's paid calls ended, so
      /// <see cref="Complete"/> tells the history row not to write it again. Off on a run whose row writes its own line.</summary>
      public bool SpendLedgered;
      /// <summary>Brief DG: the history row <see cref="Failed"/> wrote for this run, kept so a bill that arrives AFTER it (a reply that lands after Stop) can be put on
      /// the row it belongs to. Null until the run fails or stops. In memory only.</summary>
      public DwightCommand FailedRow;
      /// <summary>Brief DE: the history row <see cref="Complete"/> wrote, so a Walk's lesson can put its score on it. In memory only.</summary>
      public DwightCommand CompletedRow;
      /// <summary>Brief DL: the paid request in flight, set just before it is sent and cleared when it returns, so a Stop can tell a request that went out from one that never did. A Stop before
      /// the send finds none and writes nothing. In memory only.</summary>
      public ModelInfo SentModel;
      public long SentChars;
      public DateTime SentAtUtc;
      /// <summary>Brief DL: this run's key on the ledger, so a bill that arrives after its estimate replaces it rather than doubling it.</summary>
      public string RunId = Guid.NewGuid().ToString("N");
      /// <summary>The estimated line is on the ledger, and the replacement (the bill) is not yet.</summary>
      public bool EstimateWritten, Replaced;
      /// <summary>A person's Stop, the session's clock or a Restart ended this run while it was live (<see cref="ForceEnd"/>, <see cref="Restart"/>), so a chat turn's completion, which finds its generation gone, still writes what it spent: the provider bills a cut-off request whichever of the three cut it (Brief AK).</summary>
      public bool ForceEnded;
      /// <summary>WP-5: how to re-run THIS action on THESE guids - the Retry button, and the reopened dock
      /// entry's Retry. Bound to the guids the run started with, never to the live selection, so a Retry after
      /// the user has clicked elsewhere still retries what they asked for. Null = not retryable.</summary>
      public Action Rerun;
      /// <summary>Brief CS: the person has said yes to this run's size and cost, so neither the run nor its Retry asks again.</summary>
      public bool CostAnswered;

      /// <summary>
      /// D1/#332: what the user APPROVED, in the agent's words, when this run was opened from a consent
      /// card. Null for every hand-invoked run.
      ///
      /// It biases the PROPOSAL and nothing else: it reaches the verb's prompt as the approved intent, and
      /// no verification step reads it. That is #287(a) in its exact form - a hostile note can make Dwight
      /// propose something silly, which the preview shows and the user rejects; it cannot make a wrong
      /// change verify, because the fingerprint, predicted-delta, equivalence and completeness checks all
      /// run against what the verb ACTUALLY did.
      /// </summary>
      public string ApprovedIntent;

      /// <summary>
      /// WP-3: how THIS operation reports a Stop, a timeout, or a failure. Null (every shipped action) keeps
      /// the standard behaviour - the panel's state view plus a dock record.
      ///
      /// The chat sets it, because the standard behaviour is wrong for a conversation: the state view
      /// REPLACES the panel body, so a Stop would erase the conversation it was reporting on - the panel
      /// contradicting itself, which is the one thing this project does not ship (7.0 WP-1.3). A chat reports
      /// inline instead, in the thread, where the user is looking.
      /// </summary>
      public Action<string, string, string> OnFailed;   // (kind, title, message)
    }

    // ---- one active operation at a time (spec §11), with a kill switch + timeout so a wedged call never
    // freezes Dwight. Each run gets a GENERATION; Stop / Restart / timeout bump it, so a late-returning
    // orphan call (whose Done/render carries the old generation) is ignored and cannot clear the new run's
    // busy flag or paint a stale result. WP-3.2: the (_running, _gen) writes are made atomic under _lock so
    // the two-write sequences never tear; the CTS (WP-3.1) is cancelled by Stop/Restart/timeout so the
    // in-flight HTTP call is actually aborted, not just ignored.
    private static readonly object _lock = new object();
    private static bool _running;
    private static int _gen;
    private static IResultPanel _activePanel;
    private static Context _activeCtx;
    private static UITimer _timer;
    private static CancellationTokenSource _cts;

    public static bool IsBusy { get { lock (_lock) return _running; } }

    /// <summary>The panel the current run belongs to; null when nothing runs. The one window reads this to keep a running page alive when it is left.</summary>
    public static IResultPanel RunningPanel { get { lock (_lock) return _running ? _activePanel : null; } }

    /// <summary>The cancellation token of the current run (WP-3.1). The work captures it once and passes it to
    /// the provider, so Stop / Restart / timeout abort the HTTP call. <c>None</c> when no run is active.</summary>
    public static CancellationToken CurrentToken { get { lock (_lock) return _cts?.Token ?? CancellationToken.None; } }

    /// <summary>True only if <paramref name="gen"/> is still the live run - the gate every terminal
    /// render / canvas-write / completion checks, so a stopped or timed-out orphan no-ops.</summary>
    public static bool IsCurrent(int gen) { lock (_lock) return _running && gen == _gen; }

    /// <summary>Begin the active run on the UI thread (just before the background work). Returns the
    /// generation the work threads through its terminal callbacks. <paramref name="timeoutMs"/> 0 = none.</summary>
    public static int Activate(IResultPanel panel, int timeoutMs)
    {
      int gen;
      lock (_lock)
      {
        _running = true;
        gen = ++_gen;
        _activePanel = panel;
        _cts?.Cancel();                        // abort any lingering previous run
        _cts = new CancellationTokenSource();  // (not disposed: a captured token must stay readable)
      }
      ArmTimeout(gen, timeoutMs);
      return gen;
    }

    /// <summary>End a run (UI thread). Ignored if a newer run / stop has superseded this generation.</summary>
    public static void Done(int gen)
    {
      lock (_lock) { if (gen != _gen) return; _running = false; }
      StopTimer();
    }

    /// <summary>User pressed Stop: end the current run now and tell the panel. The orphan call keeps
    /// running in the background but is ignored on return (generation bumped).</summary>
    public static void Stop() => ForceEnd(timedOut: false);

    /// <summary>Hard reset: drop the current run, clear the glow, close the panel. The recovery hatch if
    /// anything ever wedges.</summary>
    public static void Restart()
    {
      lock (_lock)
      {
        // A turn a Restart cuts off is billed as a stopped one is, so it is marked as Stop marks it, and its completion writes its line through the same door; the verbs already write theirs
        // in their cancellation catch. Only a live run: with nothing running, the context is an old run's, finished and written, and marking it would mean nothing at best.
        if (_running && _activeCtx != null) _activeCtx.ForceEnded = true;
        _running = false; _gen++; _cts?.Cancel();
      }
      StopTimer();
      try { WalkSession.Drop(); } catch { }
      try { CanvasGlow.Clear(); } catch { }
      try { WebPanel.CloseCurrent(); } catch { }
      try { DwightToast.Show("Dwight restarted"); } catch { }
    }

    private static void ForceEnd(bool timedOut)
    {
      lock (_lock)
      {
        if (!_running)
        {
          // #290: the hypothesis the caller's own exit logs CANNOT cover - that no exit path ran at all,
          // because Stop arrived when no run was live and this returned before reporting anything. A
          // surface left mid-progress by a Stop that did nothing looks identical to one left by a Stop
          // that did the wrong thing, and only this line tells them apart.
          try { Rhino.RhinoApp.WriteLine("Dwight turn: Stop/timeout arrived with NO live run - nothing was reported or cleared."); }
          catch { }
          return;
        }
        _running = false; _gen++;
        if (_activeCtx != null) _activeCtx.ForceEnded = true;
        _cts?.Cancel();   // WP-3.1: actually abort the in-flight request, not just ignore its result
      }
      StopTimer();
      try { CanvasGlow.SetThinking(false); } catch { }
      var title = timedOut ? "Taking too long" : "Stopped";
      var msg = timedOut
        ? "Dwight stopped this task after the timeout. Try again, a smaller selection, or a faster / local model."
        : "You stopped this task.";
      // WP-5: a stopped / timed-out run is a RESULT, not a nothing - it renders with a Retry and is kept in the
      // dock with its state. "Nothing is lost" has to include the failures, or the dock is quietly lying.
      try { Failed(_activeCtx, "notice", title, msg); } catch { try { _activePanel?.ShowState("notice", title, msg, null, null); } catch { } }
    }

    /// <summary>
    /// WP-5: a run ENDED BADLY (an API error, a stop, a timeout). Render it with a Retry the user can actually
    /// press, and RECORD it in the dock carrying its state, so a failed run is recoverable and visible rather
    /// than evaporating. Retry re-runs the same action on the same BOUND guids (<see cref="Context.Rerun"/>) -
    /// a component deleted since is tolerated by the same stale-doc / grounding checks every action already
    /// runs, so the retry degrades to an honest "nothing to work on", never a crash.
    /// </summary>
    public static void Failed(Context ctx, string kind, string title, string message)
    {
      if (ctx == null) return;
      try { CanvasGlow.SetThinking(false); } catch { }
      try { Dwight.Feedback.LastErrors.Record((title ?? "") + " - " + (message ?? "")); } catch { }   // Report a problem: text Dwight itself produced

      // An operation that owns its own failure surface reports there and returns. Rendering the state view
      // over it would replace the very content the message refers to.
      if (ctx.OnFailed != null)
      {
        try { ctx.OnFailed(kind, title, message); } catch { }
        return;
      }

      var rerun = ctx.Rerun;

      // CLASS R (WP-1 step 3): the CONTENT is the state message - fully replayable, and it records only the
      // three value args, never the "retry" action command (a persisted button string without its handler is a
      // control that does nothing). The Retry itself is a live affordance, re-bindable because it needs only
      // the action and the bound guids, both of which the record already carries.
      var script = PanelScript.New()
        .Op(PanelScript.ShowState, kind ?? "error", title ?? "", message ?? "")
        .Build();

      Action<IResultPanel> live = p =>
      {
        if (p is WebPanel w) w.OnRetry = rerun == null ? null : new Action(() => rerun());
        p.ShowState(kind, title, message, rerun == null ? null : "Retry", rerun == null ? null : "retry");
      };

      Render(ctx.Panel, script, live);
      try
      {
        // Brief DG: the row carries the run's spend, from the same object the ledger line was written from, so a failed or stopped run that billed reads its cost
        // beside its state. SpendLedgered goes with it, so a row recorded after the line was written writes no second one.
        var row = new DwightCommand
        {
          RunId = ctx.RunId,
          Action = ctx.Action,
          Subtitle = title ?? "",
          State = kind == "notice" ? "stopped" : "failed",
          SourceGuids = ctx.Guids,
          Labels = ctx.Labels,
          CostLine = " - ",
          Spend = ctx.Spend,
          SpendLedgered = ctx.SpendLedgered,
          Script = script,
          Live = live,   // in-session, a reopened failure offers the SAME Retry
        };
        ctx.FailedRow = row;
        CommandHistory.Record(ctx.Doc, row);
        if (ctx.Panel is WebPanel failed) failed.SetRun(ctx.Labels, row.Time);
      }
      catch { }
    }

    private static void ArmTimeout(int gen, int ms)
    {
      StopTimer();
      if (ms <= 0) return;   // local: no auto-timeout, manual Stop only
      _timer = new UITimer { Interval = ms / 1000.0 };
      _timer.Elapsed += (s, e) => { StopTimer(); if (IsCurrent(gen)) ForceEnd(timedOut: true); };
      _timer.Start();
    }

    private static void StopTimer() { try { _timer?.Stop(); } catch { } _timer = null; }

    public static Context Begin(string action, IEnumerable<Guid> guids) => Start(action, guids, null);

    /// <summary>
    /// THE SESSION-SCOPED ENVELOPE (#193d, ruled at #223): begin an operation ON AN ALREADY-OPEN PANEL
    /// instead of showing a new one.
    ///
    /// WHY IT CANNOT JUST CALL <see cref="Begin"/>: <c>WebPanel.Show</c> CLOSES the current panel and
    /// creates a fresh one ("one Dwight surface: replace, never stack"). That is exactly right when an
    /// action opens its own result panel, and exactly wrong for a chat - the operation would destroy the
    /// surface it is running in.
    ///
    /// WHAT IT BUYS: WP-2's caller was one <see cref="Begin"/> per tool call, but an agent turn is ONE
    /// operation spanning many tool calls, so Stop (ADR-089) has to cover the whole loop rather than a
    /// single request. One turn, one generation, one cancellation token, one busy claim.
    ///
    /// Everything else is <see cref="Begin"/>'s bookkeeping unchanged: the busy gate, the active context
    /// a Stop renders against, the glow, and the bound guids.
    /// </summary>
    public static Context BeginOn(IResultPanel panel, string action, IEnumerable<Guid> guids) =>
      panel == null ? null : Start(action, guids, panel);

    /// <summary>The verb or chat step last started this session, by name only (Report a problem reads it).</summary>
    public static string LastAction { get; private set; }

    private static Context Start(string action, IEnumerable<Guid> guids, IResultPanel adopt)
    {
      lock (_lock) { if (_running) return null; }   // busy: the action shows a "still working" toast and bails
      LastAction = action;
      // Brief DA: another run ends a Walk, and its glow is about to be replaced. Brief EC unit 5b: a turn in the chat a Walk's step opened does not, and leaves the step lit.
      var keepsWalk = false;
      try { keepsWalk = WalkSession.RunStarting(adopt as WebPanel); } catch { }
      var ctx = new Context
      {
        Action = action,
        Doc = Instances.ActiveCanvas?.Document,
        Guids = guids?.ToList() ?? new List<Guid>(),
      };

      ctx.Panel = adopt ?? ResultSurface.Show("Dwight - " + action);   // opens immediately, shows the loader
      if (ctx.Panel is WebPanel running) running.RunId = ctx.RunId;
      _activePanel = ctx.Panel;
      _activeCtx = ctx;   // WP-5: so a Stop / timeout can render + record the failure against its own context
      if (!keepsWalk) CanvasGlow.Show(ctx.Guids, thinking: true, owner: ctx.Panel);

      if (ctx.Panel is WebPanel wp)
      {
        wp.SetSourceGuids(ctx.Guids);
        SubscribeGlowClear(ctx.Panel, wp);
      }
      return ctx;
    }

    /// <summary>Clear the glow when its panel closes - subscribed ONCE per panel. A freshly-shown panel
    /// subscribes on its first (only) operation, so the shipped paths are unchanged; an ADOPTED panel
    /// runs many operations over its life and must not accumulate a handler per turn.</summary>
    private static readonly HashSet<IResultPanel> _glowSubscribed = new HashSet<IResultPanel>();

    private static void SubscribeGlowClear(IResultPanel panel, WebPanel wp)
    {
      if (!_glowSubscribed.Add(panel)) return;
      wp.Closed += (s, e) => { CanvasGlow.ClearIfOwner(panel); _glowSubscribed.Remove(panel); };
    }

    /// <summary>Success: settle the glow (lavender -> clay) and record the command for the dock.
    /// Call on the UI thread (touches the glow's UITimer + history).
    ///
    /// WP-1 step 3: <paramref name="script"/> is the replayable CONTENT (the only part that can reach disk);
    /// <paramref name="live"/> is the optional in-memory affordance closure for the four Class-L surfaces.
    /// A Class-A result passes live = null and is therefore rendered from the same script in both reopen
    /// modes - divergence is impossible rather than unlikely.</summary>
    public static void Complete(Context ctx, string subtitle, string costLine,
      List<StoredOp> script, Action<IResultPanel> live = null, StoredSpend spend = null)
    {
      if (ctx == null) return;
      CanvasGlow.SetThinking(false);
      try
      {
        var row = new DwightCommand
        {
          RunId = ctx.RunId,
          Action = ctx.Action,
          Subtitle = subtitle ?? "",
          SourceGuids = ctx.Guids,
          SecondaryGuids = ctx.SecondaryGuids,
          Labels = ctx.Labels,
          CostLine = costLine ?? "",
          Spend = spend,
          SpendLedgered = ctx.SpendLedgered,
          Script = script,
          Live = live,
        };
        ctx.CompletedRow = row;
        CommandHistory.Record(ctx.Doc, row);
        if (ctx.Panel is WebPanel done) done.SetRun(ctx.Labels, row.Time);   // Brief EC unit 5: the numbering its words use, and its time, for the chat it hands off to
      }
      catch { }
    }

    /// <summary>
    /// THE ONE RENDER PATH (WP-1 step 3). Every first render and every dock reopen goes through here, so a
    /// reopened panel cannot diverge from the panel that was first shown.
    ///
    /// Live affordances win when they exist (#169 §7a) - the live closure is rendered INSTEAD OF the script, so
    /// the shipped in-session behaviour is untouched to the byte. When they do not exist - a record reopened in
    /// a later session, where <see cref="DwightCommand.Live"/> is null by construction - the script alone
    /// renders, and any op this build could not replay is surfaced honestly rather than silently dropped (#161).
    /// </summary>
    public static void Render(IResultPanel panel, List<StoredOp> script, Action<IResultPanel> live)
    {
      if (panel == null) return;

      if (live != null && !ForceDegradedReplay)
      {
        try { live(panel); } catch { }
        return;
      }

      var report = PanelScript.Play(script, new PanelSink(panel));
      if (report.Note != null)
      {
        // never a silently partial panel: say so on the meta line.
        try { panel.SetStatus(report.Note); } catch { }
      }
    }

    /// <summary>
    /// DEBUG-only (the MsaglSpike precedent, ADR-091 tooling): pretend every record was reopened in a later
    /// session, so the four Class-L degraded views can be checked in ONE session instead of four Rhino
    /// restarts. Zero shipping surface - the field is compiled out of a Release build.
    /// </summary>
    public static bool ForceDegradedReplay
    {
#if DEBUG
      get; set;
#else
      get { return false; }
#endif
    }

    /// <summary>Reopen a recorded command from the dock: a fresh panel, the cached result replayed (no
    /// re-run, no cost), and its source selection re-lit (settled clay). Spec §6 / ADR-049.</summary>
    public static void Reopen(DwightCommand cmd)
    {
      if (cmd == null) return;
      // Brief BR: a conversation left while its turn runs is shown again, live, not replayed. No glow here: a running one keeps its own.
      DwightDock.Host()?.LookedAt(cmd.ThreadId);
      if (!string.IsNullOrEmpty(cmd.ThreadId) && DwightDock.Host()?.ShowLive(cmd.ThreadId) == true) return;
      if (string.IsNullOrEmpty(cmd.ThreadId) && DwightDock.Host()?.ShowKeptWalk(cmd.RunId) == true) return;   // Brief EC unit 5b: its Walk is waiting on its step behind its chat
      var running = IsBusy;   // reading a finished conversation during a run must not take the running one's glow
      var panel = ResultSurface.Show("Dwight - " + cmd.Action);
      if (panel is WebPanel wp)
      {
        wp.SetSourceGuids(cmd.SourceGuids);
        wp.RunId = cmd.RunId;
        wp.SetRun(cmd.Labels, cmd.Time);
        wp.Closed += (s, e) => CanvasGlow.ClearIfOwner(panel);
      }
      // #182 step 5: a Scaffold row restores its whole THREAD (live conversation), not a static snapshot.
      if (!string.IsNullOrEmpty(cmd.ThreadId))
        DwightScaffold.ReopenThread(panel, Instances.ActiveCanvas?.Document, cmd.ThreadId);
      else
      {
        Render(panel, cmd.Script, cmd.Live);   // WP-1 step 3: the same path the first render used
        Dwight.UI.WalkSession.Reopened(panel as Dwight.UI.WebPanel);   // Brief DH: a replay arrives as a finished Explain does, with no wash and no framing until a step is asked for
        // Brief DG: a stopped or failed run that billed shows its cost where a finished run's shows, with its state beside it ("stopped · ~10.7¢").
        // Brief DE: a Learn's score sits beside the cost on the same line; one composer writes the line, so the score is never the word the state's line replaces.
        var status = DockRows.ReopenedStatus(cmd.State, cmd.Spend, cmd.CostLine, cmd.Score);
        if (status != null) { try { panel.SetStatus(status); } catch { } }
      }
      if (!running)
      {
        if (cmd.SecondaryGuids != null && cmd.SecondaryGuids.Count > 0)
          CanvasGlow.ShowDiagnosis(cmd.SourceGuids, cmd.SecondaryGuids, panel);  // root dark red, chain duller (D4) - only Diagnose sets secondary
        else
          CanvasGlow.Show(cmd.SourceGuids, thinking: false, owner: panel);    // re-light, settled
        if (cmd.Labels != null) CanvasGlow.SetLabels(cmd.Labels);             // restore the id badges
      }
      var all = new List<Guid>(cmd.SourceGuids ?? new List<Guid>());
      if (cmd.SecondaryGuids != null) all.AddRange(cmd.SecondaryGuids);
      CanvasNav.GoTo(all, NavReason.UserClicked);                                                  // frame the whole chain (spec ask #5)
    }
  }
}
