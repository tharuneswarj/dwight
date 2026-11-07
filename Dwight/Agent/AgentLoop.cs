using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using Dwight.Llm;

namespace Dwight.Agent
{
  /// <summary>
  /// THE MULTI-TOOL PROVIDER LOOP (Phase-8 WP-3 Stage A) - the seam WP-2 named and left for WP-3.
  ///
  /// <c>StructuredOutput</c> forces exactly one tool, which is right for the five one-shot verbs and
  /// useless for an agent: the whole point here is that the model CHOOSES among many tools, sees what
  /// they return, and chooses again. So this sits beside that seam rather than inside it - the shipped
  /// verbs are untouched.
  ///
  /// THE THREADING, which is the part that hangs rather than errors if it is wrong. The loop runs on a
  /// WORKER thread (the caller's job - every action already does this). The LLM call blocks that worker.
  /// Every tool call is marshalled to the UI THREAD, because a tool reads the live document, and the
  /// worker then blocks until the tool's completion callback fires. Reads answer immediately;
  /// <c>solve_and_check</c> and <c>build_region</c> answer from <c>SolutionEnd</c>, which is the longest
  /// path and only reachable because the worker is NOT the UI thread - if this ever ran on the UI thread
  /// it would wait for a solve that cannot start until it yields. That is the deadlock the #223 gate
  /// exercises against a mutating tool rather than only against reads.
  ///
  /// WHAT BOUNDS IT: a per-turn step count and wall clock, a transcript budget with its elision
  /// reported, and the session's cancellation token - checked between steps and while waiting on a
  /// tool, so Stop covers the whole loop rather than one HTTP call (#193d/#223). Exhausting any bound
  /// is an honest STOP that keeps whatever the turn produced.
  ///
  /// WHAT IT MAY CALL is not its own decision: <see cref="AgentLoopCore.IsGranted"/> is consulted before
  /// every dispatch, and the grant comes from the caller, who got it from a user's click (#220).
  /// </summary>
  internal static class AgentLoop
  {
    /// <summary>
    /// How long the worker waits for one tool before giving up on it.
    ///
    /// BASIS: deliberately ABOVE the tools' own watchdogs (<c>build_region</c> arms 120s), so a tool
    /// that is going to refuse gets to refuse in its own words - its message is specific and ours would
    /// be generic. This is the backstop for a tool that never calls back at all, which the surface's
    /// contract says cannot happen; a backstop for a contract is not redundant, it is what makes the
    /// contract's failure survivable.
    /// </summary>
    private const int ToolWaitMs = 150_000;

    internal sealed class TurnRequest
    {
      public ILlmProvider Provider;
      public string System;
      /// <summary>Seeded from the CHAT HISTORY - never from a previous turn's tool traffic (#223). Each
      /// turn re-derives what it needs; carrying stale tool results forward would pay for them again and
      /// describe a canvas that has since moved.</summary>
      public List<LlmMessage> Messages = new List<LlmMessage>();
      public TurnCapability Capability = TurnCapability.ReadOnly;

      /// <summary>The regions a BUILD turn was approved for (WP-4 Stage C). Seeded onto this turn's
      /// ledger before the first step, so build_region can refuse a region outside it - the approved
      /// scope BOUNDS what the turn writes rather than merely being described to the model, because a
      /// prompt line is not a guard (#195/#338). Null for every other capability.</summary>
      public IReadOnlyList<string> ApprovedRegions;
      /// <summary>U65: the scripts the approved card showed whole, granted to the turn's ledger beside the regions.</summary>
      public IReadOnlyList<ApprovedScript> ApprovedScripts;

      /// <summary>U90: whether THIS conversation has Full control on, read from the thread when the turn starts. It decides whether
      /// request_mutation offers the run_script kind and whether an approved run_script turn holds the tool.</summary>
      public bool FullControl;

      /// <summary>U90: the one script a run_script approval granted - exactly the source the card showed, once. Null on every other turn.</summary>
      public RunScriptGrant RunGrant;

      /// <summary>U90: called on the UI thread with a line for the panel when a script finishes AFTER the clock had already reported it
      /// still running - how long it took and what changed. Optional.</summary>
      public Action<string> OnLateRun;
      public AgentScope Scope;
      public TurnLimits Limits;
      public bool Thinking;
      public string Effort;
      /// <summary>Brief BT: the tool this turn must call, or null. Set on an announcement's follow-up.</summary>
      public string ForceToolName;
      public CancellationToken Cancel;
      /// <summary>The generation guard (ADR-089). False once a Stop, Restart, or timeout has superseded
      /// this turn, so a late step never renders into a session that has moved on.</summary>
      public Func<bool> IsCurrent;
      /// <summary>Called on each tool dispatch, for the loader's honest phase label. Best-effort.</summary>
      public Action<string> OnToolCall;

      /// <summary>
      /// The HOST tool handler (#230): given the tool name and its raw arguments, returns the wire
      /// envelope. UI-only work - it renders in this surface and returns immediately, never blocking the
      /// loop on a human.
      ///
      /// Note what it is NOT given: the <see cref="AgentScope"/>. A host tool therefore holds no
      /// reference through which it could reach the document, which is what makes "a host tool may never
      /// be a write path" a property of the wiring rather than a rule someone has to remember.
      /// </summary>
      public Func<string, string, string> OnHostTool;

      /// <summary>
      /// Called after every CATALOGUE tool answers, with its name, the arguments it was given, and its
      /// wire envelope. The host's hook for anything it must record about a canvas change - WP-3 uses it
      /// to persist the scaffold map, which the tool itself has no business knowing about (it serves the
      /// MCP surface too, where there is no thread to write to).
      ///
      /// Best-effort and never load-bearing: a throw here is swallowed, because a bookkeeping failure
      /// must not fail a change that already landed and verified.
      /// </summary>
      public Action<string, string, string> OnToolCompleted;

      /// <summary>U71: a line for Rhino's command history ("tool web_search ok=true"), for a tool that does not reach the host
      /// callbacks above - the web search runs on the worker with no document. Optional; a throw is swallowed.</summary>
      public Action<string> OnLog;

      /// <summary>Brief V: this turn's web search, or null when the setting is off (then nothing about search is advertised,
      /// sent or said). A client mode adds the <c>web_search</c> tool and is dispatched by <see cref="Execute"/> on the
      /// worker thread; the server mode adds Anthropic's own tool to each request and dispatches nothing.</summary>
      public WebSearchPlan WebSearch;
    }

    public static TurnResult Run(TurnRequest req)
    {
      if (req?.Provider == null || req.Limits == null)
      {
        var failed = new TurnResult();
        failed.Outcome = TurnOutcome.Failed;
        failed.Error = "the turn was not configured";
        failed.Note = AgentLoopCore.NoteFor(TurnOutcome.Failed, req?.Limits, false);
        return failed;
      }

      var tools = AgentLoopCore.ToolsFor(req.Capability, req.FullControl)
        .Select(t => new LlmTool { Name = t.Name, Description = t.Description, InputSchemaJson = t.InputSchemaJson })
        .ToList();

      // Brief V: the search tool, only when the user turned it on and the provider has no search of its own in the request.
      if (req.WebSearch != null && req.WebSearch.AddsClientTool)
      {
        var search = Dwight.Agent.WebSearch.Descriptor();
        tools.Add(new LlmTool { Name = search.Name, Description = search.Description, InputSchemaJson = search.InputSchemaJson });
      }

      // The approved scope is granted to THIS turn's ledger, once, before anything runs. The ledger is
      // keyed by `req` identity (#352's constraint 2), so the grant has exactly the turn's lifetime and a
      // later turn is handed a fresh ledger with no authority at all.
      if (req.Capability == TurnCapability.Build && req.Scope != null)
      {
        req.Scope.LedgerFor(req).Authorise(req.ApprovedRegions);
        req.Scope.LedgerFor(req).AuthoriseScripts(req.ApprovedScripts);
      }

      var clock = Stopwatch.StartNew();

      // #612: the steps themselves live in the GH-free driver, so a scripted provider can drive the REAL
      // loop. What stays here is the one step a test cannot run - dispatching a tool against the live
      // document - and it is handed in, not copied: the drill below still calls this same Execute.
      var driven = AgentTurnDriver.Drive(new TurnDrive
      {
        Provider = req.Provider,
        System = req.System,
        Messages = req.Messages,
        Tools = tools,
        Limits = req.Limits,
        Thinking = req.Thinking,
        Effort = req.Effort,
        ForceToolName = req.ForceToolName,
        Cancel = req.Cancel,
        IsCurrent = req.IsCurrent,
        OnToolCall = req.OnToolCall,
        Execute = call => Execute(req, call, clock),
        // #624: the approval's own tool, from the capability the click granted - so the turn's shape can count
        // an approved build that never ran.
        ActingTool = AgentHostTools.ActingToolFor(AgentHostTools.KindApprovedBy(req.Capability)),
        // #657: the approved regions, so an approved build that ends without building gets its one repair step.
        ApprovedRegions = req.Capability == TurnCapability.Build ? req.ApprovedRegions : null,
        ServerWebSearch = req.WebSearch != null && req.WebSearch.UsesServerTool,
      }, clock);

      // What the client search ran (its count, a Google sub-call's tokens) joins the turn's usage, so the cost line is whole.
      req.WebSearch?.FoldInto(driven.Usage);
      return driven;
    }

    /// <summary>
    /// Run one tool call and return its wire envelope. Two gates before anything is dispatched: the
    /// capability grant (#220), and the surface's own validation inside <see cref="AgentTools"/>.
    /// </summary>
    private static string Execute(TurnRequest req, LlmBlock call, Stopwatch clock)
    {
      // Brief V: the search is a NETWORK call with no document in it, so it runs here on the worker - never marshalled to the
      // UI thread, where a slow provider would freeze Rhino - and it is handed no scope. It exists only on a turn whose plan
      // advertised it; a model that names it otherwise is refused like any tool it was never offered.
      if (Dwight.Agent.WebSearch.IsSearchTool(call.ToolName))
      {
        if (req.WebSearch == null || !req.WebSearch.AddsClientTool)
          return Refusal(ToolErrors.PreconditionRefused, "\"" + call.ToolName + "\" is not available on this turn.");
        var searchEnvelope = req.WebSearch.Run(call.ToolInputJson, req.Cancel);
        try { req.OnLog?.Invoke(AgentLoopCore.DispatchLogLine(call.ToolName, searchEnvelope)); } catch { }
        return searchEnvelope;
      }

      // U90: the one tool that runs the user's own Python, on an approved Full-control turn only.
      if (string.Equals(call.ToolName, Dwight.Agent.FullControl.ToolName, StringComparison.OrdinalIgnoreCase))
      {
        var run = ExecuteRunScript(req, call);
        try { req.OnToolCompleted?.Invoke(call.ToolName, call.ToolInputJson, run); } catch { }
        return run;
      }

      if (!AgentLoopCore.IsGranted(req.Capability, call.ToolName))
        return AgentLoopCore.GrantRefusal(call.ToolName);

      // HOST tools take a different road entirely (#230): they never reach AgentTools, and the handler is
      // not given the scope, so a host tool has no path to the canvas even if one were written by
      // mistake. They also return immediately - a host tool that blocked on a human would hold the
      // session busy for as long as the user took to read it.
      if (AgentHostTools.Knows(call.ToolName))
      {
        try
        {
          return req.OnHostTool?.Invoke(call.ToolName, call.ToolInputJson)
                 ?? Refusal(ToolErrors.PreconditionRefused,
                      "\"" + call.ToolName + "\" is not available on this surface.");
        }
        catch (Exception ex)
        {
          return Refusal(ToolErrors.PreconditionRefused, "The request could not be put to the user (" + ex.Message + ").");
        }
      }

      var remaining = req.Limits.WallClockMs > 0
        ? (int)Math.Max(0, req.Limits.WallClockMs - clock.ElapsedMilliseconds)
        : ToolWaitMs;
      var wait = Math.Min(ToolWaitMs, remaining <= 0 ? ToolWaitMs : remaining);

      string answer = null;
      // Deliberately NOT disposed: the surface promises exactly one callback, but a late one arriving
      // after a timeout would set a disposed event and throw on the UI thread. An undisposed, short-
      // lived, per-tool-call event is the cheaper side of that trade.
      var done = new ManualResetEventSlim(false);

      try
      {
        // THE TURN IDENTITY IS THE REQUEST ITSELF (#352's constraint 2). A rebuild may delete only what
        // THIS turn's builds created, and `req` is fresh per turn and is the one thing the loop cannot
        // run without holding - so turn-scoping is a property of the call rather than a clear() that a
        // future edit could forget. A different turn is handed an empty ledger, and an empty ledger
        // REFUSES a rebuild.
        Ui(() => AgentTools.InvokeAsync(call.ToolName, call.ToolInputJson, req.Scope, json =>
        {
          answer = json;
          try { done.Set(); } catch { }
        }, req));

        if (!done.Wait(wait, req.Cancel))
          return Refusal(ToolErrors.PreconditionRefused,
            "\"" + call.ToolName + "\" did not answer in time and was abandoned. The canvas was not left " +
            "in a half-changed state - every tool that writes reverts itself if it cannot verify.");
      }
      catch (OperationCanceledException)
      {
        return Refusal(ToolErrors.PreconditionRefused, "The user stopped this turn.");
      }
      catch (Exception ex)
      {
        return Refusal(ToolErrors.PreconditionRefused, "The tool could not be called (" + ex.Message + ").");
      }

      var envelope = answer ?? Refusal(ToolErrors.PreconditionRefused, "The tool returned nothing.");
      try { req.OnToolCompleted?.Invoke(call.ToolName, call.ToolInputJson, envelope); } catch { }
      return envelope;
    }

    /// <summary>
    /// U90: run the approved script. Four gates before anything runs, each a refusal in the model's reading language - Full control is
    /// on for this conversation, this turn holds the grant, the source the model sent is the source the card showed, and the approval
    /// has not already been spent - and then the run is scheduled on the UI thread (guardrail 1) with a CLOCK.
    ///
    /// THE CLOCK CANNOT INTERRUPT. Rhino offers no way to stop a script on its own thread, so when the clock runs out this returns the
    /// truthful result - still running - and the late finish is reported to the panel when it comes (<see cref="TurnRequest.OnLateRun"/>).
    /// It is never "stopped": a script Dwight could not stop is not one it stopped (U47/U48).
    /// </summary>
    private static string ExecuteRunScript(TurnRequest req, LlmBlock call)
    {
      if (!req.FullControl || req.Capability != TurnCapability.RunScript || req.RunGrant == null)
        return Refusal(ToolErrors.PreconditionRefused,
          "\"" + Dwight.Agent.FullControl.ToolName + "\" is not available on this turn. A script runs only after the user approves it on a " +
          "card, in a conversation where Full control is on - ask with " + AgentHostTools.RequestMutation + " kind=" + Dwight.Agent.FullControl.Kind + ".");

      var args = AgentToolCore.ParseArgs(call.ToolInputJson, out var argError);
      if (argError != null) return argError.ToJson();
      if (!Dwight.Agent.FullControl.TryParseCall(args, out var sent, out var purpose, out var timeoutArg, out var callError))
        return callError.ToJson();

      var differs = Dwight.Agent.FullControl.FirstDifference(req.RunGrant.Source, sent);
      if (differs != null) return Refusal(ToolErrors.PreconditionRefused, differs);

      if (!req.RunGrant.TryUse())
        return Refusal(ToolErrors.PreconditionRefused,
          "That approval covered one run and it has been used. Ask the user again if another script is needed.");

      var doc = req.Scope?.Document(out var docError);
      if (doc == null) return Refusal(ToolErrors.NoDocument, "There is no open definition to run the script on.");

      var timeout = timeoutArg ?? req.RunGrant.TimeoutSeconds;
      // THE TEXT SHOWN IS THE TEXT RUN: the grant's source, never the model's copy of it.
      var source = Dwight.Agent.FullControl.ExecutableSource(req.RunGrant.Source);
      var grantPurpose = string.IsNullOrWhiteSpace(purpose) ? req.RunGrant.Purpose : purpose;

      var done = new ManualResetEventSlim(false);
      var gate = new object();
      var abandoned = false;
      string answer = null;
      var started = Stopwatch.StartNew();

      Ui(() => GhRunScript.Run(doc, source, grantPurpose, outcome =>
      {
        outcome.Seconds = started.Elapsed.TotalSeconds;
        var envelope = Dwight.Agent.FullControl.ToResult(grantPurpose, outcome).ToJson();
        bool late;
        lock (gate) { late = abandoned; if (!late) answer = envelope; }
        if (late)
        {
          try
          {
            var report = outcome.Unavailable != null ? outcome.Unavailable
              : Dwight.Agent.FullControl.ReportFor(grantPurpose, outcome);
            req.OnLateRun?.Invoke(Dwight.Agent.FullControl.LateReport(grantPurpose, timeout, outcome.Seconds, report));
          }
          catch { }
        }
        else { try { done.Set(); } catch { } }
      }));

      try
      {
        if (!done.Wait(timeout * 1000, req.Cancel))
        {
          lock (gate)
          {
            if (answer == null)
            {
              abandoned = true;
              return Dwight.Agent.FullControl.StillRunningResult(grantPurpose, timeout).ToJson();
            }
          }
        }
      }
      catch (OperationCanceledException)
      {
        // The user stopped the TURN. The script, if it is running, is on Rhino's thread and is not stopped by that, and this says so.
        lock (gate) { if (answer == null) { abandoned = true; return Refusal(ToolErrors.PreconditionRefused, "The user stopped this turn. " + Dwight.Agent.FullControl.StillRunning); } }
      }

      lock (gate) return answer ?? Refusal(ToolErrors.PreconditionRefused, "The script returned nothing.");
    }

    /// <summary>
    /// The #223 drill's door onto THIS loop's dispatch - the real <see cref="Execute"/>, called the way a
    /// step calls it, from whatever thread the caller is on. Deliberately not a reimplementation: a drill
    /// that exercises its own copy of the path measures the copy.
    /// </summary>
    internal static string CallToolLikeTheLoop(string name, string argsJson, AgentScope scope,
      TurnCapability capability = TurnCapability.Build)
    {
      var req = new TurnRequest
      {
        Capability = capability,
        Scope = scope,
        Limits = new TurnLimits { WallClockMs = 0, MaxSteps = 1, AnswerBudget = 1, MaxTranscriptChars = 1 },
      };
      return Execute(req, LlmBlock.ToolUse("drill", name, argsJson), Stopwatch.StartNew());
    }

    /// <summary>A refusal in the SAME wire shape a tool would have produced, so the model reads one
    /// envelope whether the refusal came from the tool or from the loop above it.</summary>
    private static string Refusal(string code, string message) => AgentLoopCore.Refusal(code, message);

    /// <summary>Marshal to the Eto UI thread - a tool reads the live document, so it cannot run on the
    /// worker. Mirrors the pattern every shipped action already uses.</summary>
    private static void Ui(Action a)
    {
      var app = Eto.Forms.Application.Instance;
      if (app != null) app.AsyncInvoke(a); else a();
    }
  }
}
