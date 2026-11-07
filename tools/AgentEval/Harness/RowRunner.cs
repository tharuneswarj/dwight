using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Dwight.Agent;
using Dwight.Llm;
using Dwight.Session;

namespace Dwight.AgentEval
{
  /// <summary>
  /// ONE ROW, THE REAL LOOP. <see cref="AgentTurnDriver.Drive"/> runs every turn, with the product's prompt (<see cref="TurnSystemText"/>), its
  /// tool list (<see cref="AgentLoopCore.ToolsFor(TurnCapability, bool)"/>), its transcript (<see cref="ChatToTranscript"/>), and its request
  /// parser (<see cref="AgentHostTools.TryParseRequest(System.Text.Json.Nodes.JsonObject, MutationRequestLog, string, string, IReadOnlyList{string}, Func{string, bool}, out string, out string, out HandoffSeed, out ToolResult)"/>).
  ///
  /// WHAT IS A COPY, AND IT IS SMALL: the order of events after a turn. That order lives in the GH-bound panel (<c>DwightScaffold.RunTurn</c>), so
  /// it is re-sequenced here, calling the product's own decisions (<see cref="AgentLoopCore.AnnouncedForForcing"/>,
  /// <see cref="AutoFollowUp.ShouldRunForAnnouncement"/>, <see cref="ForcedTryBound"/>, <see cref="ApprovalMode.Decide"/>, <see cref="ConsentCard.Route"/>).
  /// The result says so. If the panel's order changes, this does not notice.
  /// </summary>
  internal sealed class RowRunner
  {
    /// <summary>Plain first-person intent, the upper bound in <see cref="TurnRecord.SaidWillNoCard"/>. Decides nothing.</summary>
    private static readonly Regex PlainIntent = new Regex(
      @"[^.!?\n]*\b(?:i['\u2019]ll|i will|i['\u2019]m going to|i am going to|let me)\b[^.!?\n]*",
      RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex Lacked = new Regex(
      @"[^.!?\n]*\b(?:i don't have|i do not have|no tool|don't have a tool|isn't a tool|not able to|there is no way|i wish i|i'd need a tool|no capability)\b[^.!?\n]*",
      RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private sealed class Pending
    {
      public TurnCapability Capability;
      public IReadOnlyList<string> Regions;
      public IReadOnlyList<ApprovedScript> Scripts;
      public bool FollowUp;
      public bool Continuation;
      public bool AutoApproved;
    }

    private sealed class RaisedCard
    {
      public CardRecord Record;
      public string Kind;
      public HandoffSeed Seed;
      public bool Auto;
    }

    private readonly EvalRow _row;
    private readonly ILlmProvider _provider;
    private readonly EvalWorld _world;
    private readonly Func<TurnCapability, TurnLimits> _limits;
    private readonly Func<LlmUsage, double> _centsOf;
    private readonly CancellationToken _cancel;
    private readonly bool _thinking;
    private readonly string _effort;

    private readonly List<StoredMessage> _stored = new List<StoredMessage>();
    private readonly ForcedTryBound _forcedTries = new ForcedTryBound();
    private readonly ScriptsOnFile _scriptsOnFile = new ScriptsOnFile();
    private readonly ExhaustedReads _exhaustedReads = new ExhaustedReads();
    private HandoffSeed _waitingScript;
    private readonly MutationRequestLog _log = new MutationRequestLog();
    private readonly RowResult _result;
    private CanvasReading _lastCanvas;

    public RowRunner(EvalRow row, EvalFixture fixture, ComponentList components, ILlmProvider provider,
                     Func<TurnCapability, TurnLimits> limits, Func<LlmUsage, double> centsOf, bool thinking, string effort, CancellationToken cancel)
    {
      _row = row; _provider = provider; _limits = limits; _centsOf = centsOf; _thinking = thinking; _effort = effort; _cancel = cancel;
      _world = new EvalWorld(fixture, components);
      _result = new RowResult
      {
        RowId = row.Id, Judge = row.Judge, Persona = row.Persona, Provider = row.Provider, Model = row.Model, ApprovalMode = row.ApprovalMode,
        Fixture = row.Fixture, FixtureOrigin = fixture.Origin, FullControl = row.FullControl, ComponentListLoaded = components?.Loaded == true, UserTurns = row.Turns.ToList(),
        StepsPerTurn = limits(TurnCapability.ReadOnly).MaxSteps,
      };
    }

    public RowResult Run()
    {
      var clock = Stopwatch.StartNew();
      _result.EndedBecause = "every user turn ran";
      foreach (var text in _row.Turns)
      {
        _forcedTries.Reset(_row.Id);   // a message of the user's own buys one forced try
        _stored.Add(new StoredMessage { Who = "me", Src = text, Html = text });
        if (ScreenAsk.Asks(text, false)) Report(ScreenAsk.Fact);   // Brief DX unit 8, as the panel's door does. A row attaches no image.
        if (!Chain(new Pending { Capability = TurnCapability.ReadOnly })) break;
      }

      _result.TotalSeconds = clock.Elapsed.TotalSeconds;
      _result.TotalCents = _result.Turns.Sum(t => t.Cents);
      _result.TotalInputTokens = _result.Turns.Sum(t => t.InputTokens + t.CacheReadTokens + t.CacheWriteTokens);
      _result.TotalOutputTokens = _result.Turns.Sum(t => t.OutputTokens);
      _result.FixtureMisses = _result.Turns.SelectMany(t => t.Tools).Where(t => t.AnsweredBy == "fixture-miss").Select(t => t.Name).Distinct().ToList();
      foreach (var t in _result.Turns)
        foreach (var text in new[] { t.Text, t.WithheldText })
          if (!string.IsNullOrEmpty(text))
            foreach (Match m in Lacked.Matches(text)) _result.SaidItLacked.Add(m.Value.Trim());
      return _result;
    }

    /// <summary>Run a turn and whatever it leads to. False ends the row.</summary>
    private bool Chain(Pending first)
    {
      var queue = new Queue<Pending>();
      queue.Enqueue(first);
      while (queue.Count > 0)
      {
        if (_result.Turns.Count >= _row.MaxTurns)
        {
          _result.EndedBecause = "the row's limit of " + _row.MaxTurns + " model turns";
          return false;
        }
        var turn = Turn(queue.Dequeue(), queue);
        if (turn.Outcome == TurnOutcome.Failed.ToString() || turn.Outcome == TurnOutcome.Stopped.ToString())
        {
          _result.EndedBecause = "a turn ended " + turn.Outcome + (string.IsNullOrEmpty(turn.Error) ? "" : ": " + turn.Error);
          return false;
        }
      }
      return true;
    }

    private TurnRecord Turn(Pending p, Queue<Pending> next)
    {
      var record = new TurnRecord { Number = _result.Turns.Count + 1, Capability = p.Capability.ToString(), FollowUp = p.FollowUp };
      _result.Turns.Add(record);
      var cards = new List<RaisedCard>();

      _world.LastUserText = () => _stored.LastOrDefault(m => m?.Who == "me" && m.Src != ApprovalMode.ClickUserText && m.Src != ApprovalMode.AutoUserText)?.Src;

      var history = ChatToTranscript.Build(_stored);
      var situation = _world.Situation();
      var fullFact = _row.FullControl ? FullControl.ThreadFact(_row.ApprovalMode) : null;
      if (fullFact != null) situation += "\n\n" + fullFact;
      var waitingFact = _waitingScript != null ? FullControl.WaitingFact(_waitingScript.Why, _waitingScript.Source) : null;
      if (waitingFact != null) situation += "\n\n" + waitingFact;
      var lastReads = _exhaustedReads.FactFor(_row.Id);
      if (lastReads != null) situation += "\n\n" + lastReads;
      var onFile = p.Capability == TurnCapability.Build ? null : ApprovedTurnFact.ScriptsOnFileFact(_scriptsOnFile.For(_row.Id));
      if (onFile != null) situation += "\n\n" + onFile;
      _world.ApprovedScripts = p.Scripts;
      var canvasFact = CanvasChangeFact.For(_lastCanvas, _world.Reading(), history.Any(m => m.Role == LlmRole.Assistant));
      var styleFact = BuildStyle.Fact(_row.BuildStyle);
      var system = TurnSystemText.Compose(situation, canvasFact, null, p.Capability, p.Regions, null, null, null,
                                          styleFact, p.Scripts, null);
      record.SystemChars = system.Length;
      record.TurnStartFacts = TurnStartFact.Of(system, ("the canvas", _world.Situation()), ("the canvas since last turn", canvasFact), ("full control", fullFact), ("a card waiting", waitingFact),
                                               ("what the last turn read", lastReads), ("scripts on file", onFile), ("build style", styleFact));
      var tools = AgentLoopCore.ToolsFor(p.Capability, _row.FullControl)
        .Select(t => new LlmTool { Name = t.Name, Description = t.Description, InputSchemaJson = t.InputSchemaJson }).ToList();
      var approvedKind = AgentHostTools.KindApprovedBy(p.Capability);

      var clock = Stopwatch.StartNew();
      var result = AgentTurnDriver.Drive(new TurnDrive
      {
        Provider = _provider,
        System = system,
        Messages = history,
        Tools = tools,
        Limits = _limits(p.Capability),
        Thinking = _thinking,
        Effort = _effort,
        ForceToolName = p.FollowUp ? AgentHostTools.RequestMutation : null,
        Cancel = _cancel,
        IsCurrent = () => true,
        OnToolCall = tool => _log.ToolDispatched(approvedKind, tool),
        Execute = call => Execute(p, call, record, cards, approvedKind),
        ActingTool = AgentHostTools.ActingToolFor(approvedKind),
        ApprovedRegions = p.Capability == TurnCapability.Build ? p.Regions : null,
      }, clock);
      clock.Stop();

      _exhaustedReads.Note(_row.Id, result);
      record.ClosingStep = result.ClosingStep;
      record.Outcome = result.Outcome.ToString();
      record.StopReason = result.LastStopReason;
      record.Note = result.Note;
      record.Error = result.Error;
      record.Forced = result.Forced;
      record.RequestOffered = result.RequestOffered;
      record.Steps = result.Steps;
      record.InputTokens = result.Usage?.InputTokens ?? 0;
      record.CacheReadTokens = result.Usage?.CacheReadInputTokens ?? 0;
      record.CacheWriteTokens = result.Usage?.CacheCreationInputTokens ?? 0;
      record.OutputTokens = result.Usage?.OutputTokens ?? 0;
      record.Cents = result.Usage != null ? _centsOf(result.Usage) : 0;
      record.Seconds = clock.Elapsed.TotalSeconds;
      _lastCanvas = _world.Reading();

      // The measure the ruling on the catch will read. Only a turn that could have asked and did not.
      if (p.Capability != TurnCapability.Build && result.Outcome == TurnOutcome.Complete && result.RequestCalls == 0 && result.LastStepToolCalls == 0)
      {
        record.AnnouncedLineCatch = AgentLoopCore.AnnouncedNothingAsked(p.Capability, result);
        record.AnnouncedSentence = TurnShape.ForcingSentence(result.LastStepText, 0, 0);
        record.AnnouncedForcingCatch = record.AnnouncedSentence != null;
        var plain = PlainIntent.Match(result.LastStepText ?? "");
        record.SaidWillNoCard = plain.Success;
        if (record.AnnouncedSentence == null && plain.Success) record.AnnouncedSentence = plain.Value.Trim();
      }

      // What this build reached, and each landed region's script comes off the file. A recorded build always lands.
      var outcomes = new Dictionary<string, RegionOutcome>(StringComparer.Ordinal);
      foreach (var t in record.Tools.Where(t => t.Name == AgentToolCatalog.BuildRegion))
      {
        string region = null;
        try { region = System.Text.Json.Nodes.JsonNode.Parse(t.Arguments)?["region"]?.GetValue<string>(); } catch { }
        if (region != null) outcomes[region] = t.AnsweredBy == "recorded" ? RegionOutcome.Built : RegionOutcome.Refused;
      }
      foreach (var landed in outcomes.Where(o => o.Value == RegionOutcome.Built)) _scriptsOnFile.Landed(_row.Id, landed.Key);

      // ---- from here, the panel's order (DwightScaffold.RunTurn's completion) ----
      var banner = AgentLoopCore.NoBuildBanner(p.Capability, result, p.Regions);
      if (banner != null) record.PluginLines.Add(banner);

      var followUpNoCard = AgentLoopCore.FollowUpRaisedNoCard(p.FollowUp, result);
      if (!string.IsNullOrWhiteSpace(result.Text))
      {
        if (followUpNoCard) record.WithheldText = result.Text;
        else
        {
          record.Text = result.Text;
          _stored.Add(new StoredMessage { Who = "dw", Src = result.Text, Html = result.Text });
        }
      }

      var complete = result.Outcome == TurnOutcome.Complete;
      var autos = complete ? cards.Where(c => c.Auto).ToList() : new List<RaisedCard>();

      var followUpDue = autos.Count == 0 &&
        AutoFollowUp.ShouldRunForAnnouncement(!string.Equals(_row.ApprovalMode, ApprovalMode.Ask, StringComparison.Ordinal), result.ProviderForces,
                                              AgentLoopCore.AnnouncedForForcing(p.Capability, result), p.FollowUp) &&
        _forcedTries.TryTake(_row.Id);

      var followsApproval = ChatToTranscript.FollowsApproval(_stored);
      var nothingAsked = followUpDue || followUpNoCard ? null : AgentLoopCore.NothingAskedReport(p.Capability, result, followsApproval);
      if (nothingAsked != null)
      {
        record.PluginLines.Add(AgentLoopCore.NothingAskedSentenceFor(p.Capability, followsApproval));
        Report(nothingAsked);
      }

      if (followUpNoCard && complete)
      {
        var noCard = AutoFollowUp.NoCardSentence(result.Note, result.RefusedRequests);
        record.PluginLines.Add(noCard);
        Report(noCard);
      }
      else if (result.Note != null) record.PluginLines.Add(result.Note);

      // The product's two follow-ups to an automatically approved build. A recorded build always lands, so only a refusal leads to the first.
      if (p.Capability == TurnCapability.Build && p.AutoApproved && complete)
      {
        var end = outcomes.Values.Any(o => o == RegionOutcome.Refused) ? AutoFollowUp.FlowEnd.Refused : AutoFollowUp.FlowEnd.Succeeded;
        if (AutoFollowUp.ShouldRun(true, end))
        {
          record.PluginLines.Add(AutoFollowUp.Line);
          Report(AutoFollowUp.BuildEndedFact(outcomes, p.Regions));
          next.Enqueue(new Pending { Capability = TurnCapability.ReadOnly });
        }

        var left = AutoFollowUp.NotAttempted(p.Regions, outcomes);
        if (AutoFollowUp.ShouldContinueBuild(true, false, end, left.Count, outcomes.Count, p.Continuation))
        {
          record.PluginLines.Add(AutoFollowUp.ContinueLine(left));
          Report(AutoFollowUp.ContinueFact(left));
          next.Enqueue(new Pending
          {
            Capability = TurnCapability.Build, Regions = left, AutoApproved = true, Continuation = true,
            Scripts = (p.Scripts ?? new ApprovedScript[0]).Where(s => left.Contains(s.Region)).ToList(),
          });
        }
      }

      if (followUpDue)
      {
        record.PluginLines.Add(AutoFollowUp.AnnouncementLine);
        Report(AutoFollowUp.AnnouncementFact);
        next.Enqueue(new Pending { Capability = TurnCapability.ReadOnly, FollowUp = true });
      }

      foreach (var card in cards)
      {
        if (card.Auto && complete)
        {
          card.Record.Answer = "approved-automatically";
          Approve(card, ApprovalMode.AutoUserText, record, next);
        }
        else if (!card.Auto && complete && _row.OnCard == "approve")
        {
          card.Record.Answer = "approved-by-row";
          Approve(card, ApprovalMode.ClickUserText, record, next);
        }
        else if (!card.Auto && complete && _row.OnCard == "decline") card.Record.Answer = "declined-by-row";
        else if (!card.Auto && complete && _row.OnCard == "ignore") card.Record.Answer = "ignored-by-row";
      }
      return record;
    }

    /// <summary>The route is the kind's alone, as the panel's is (#235). What the plugin would apply from the card is recorded, and the turn after
    /// it is told so in the report the panel would write.</summary>
    private void Approve(RaisedCard card, string userText, TurnRecord record, Queue<Pending> next)
    {
      _log.RecordApproved(card.Kind, _world.Stamp);
      _stored.Add(new StoredMessage { Who = "me", Src = userText, Html = userText, Echo = true });

      // U100: the plugin runs the grant with no model call, then one read-only turn reads the report. Nothing runs here, and the report's counts are stated.
      if (card.Kind == FullControl.Kind)
      {
        _waitingScript = null;
        var ran = new RunOutcome { Before = new DocSnapshot(), After = new DocSnapshot() };
        record.PluginLines.Add("[not performed] the approved script would run here. " + RunScriptApproval.PanelLine(card.Record.Why, ran));
        Report(RunScriptApproval.TranscriptLine(ran, RunScriptApproval.PanelLine(card.Record.Why, ran), card.Auto));
        next.Enqueue(new Pending { Capability = TurnCapability.ReadOnly });
        return;
      }

      var route = ConsentCard.Route(card.Kind);
      var plan = SeedPlan.From(card.Kind, card.Seed, id => IdState.Unknown);
      switch (route)
      {
        case ConsentRoute.Solve:
          next.Enqueue(new Pending { Capability = TurnCapability.ReadAndSolve });
          return;
        case ConsentRoute.Scaffold:
          next.Enqueue(new Pending { Capability = TurnCapability.Scaffold });
          return;
        case ConsentRoute.Build when string.IsNullOrEmpty(plan.PreFillSpot):
        {
          var resolution = BuildScope.Resolve(plan.Regions, _world.Regions);
          if (!string.IsNullOrEmpty(resolution.Message)) record.PluginLines.Add(resolution.Message);
          if (resolution.Outcome == BuildScopeOutcome.Refuse) return;
          var scripts = plan.Scripts.Where(s => resolution.Regions.Contains(s.Region)).ToList();
          next.Enqueue(new Pending { Capability = TurnCapability.Build, Regions = resolution.Regions, Scripts = scripts, AutoApproved = userText == ApprovalMode.AutoUserText });
          return;
        }
        case ConsentRoute.Build:
          ApplySpot(plan, record, next, userText == ApprovalMode.AutoUserText);
          return;
        case ConsentRoute.Reorganise:
        case ConsentRoute.Simplify:
          // These open a verb's own flow, which has no place here.
          record.PluginLines.Add("[not performed] the approved " + card.Kind + " would open its verb here.");
          return;
        case ConsentRoute.SetValue:
          ApplyValue(plan, record, next, userText == ApprovalMode.AutoUserText);
          return;
        case ConsentRoute.EditScript:
          ApplyScript(plan, record, next, userText == ApprovalMode.AutoUserText);
          return;
        case ConsentRoute.Edit:
          ApplyEdit(plan, record, next, userText == ApprovalMode.AutoUserText);
          return;
        default:
          // The parser refuses a kind with no route before a card exists.
          record.PluginLines.Add("[not performed] the approved " + card.Kind + " has no route here.");
          return;
      }
    }

    /// <summary>The panel's <c>ConsentRoute.SetValue</c>: the report from the plugin's own renderer, then the one re-read the card named, only if it landed.</summary>
    private void ApplyValue(SeedPlan plan, TurnRecord record, Queue<Pending> next, bool autoApproved)
    {
      var result = _world.ApplyValue(plan);
      var report = SetValueReport.Text(result);
      record.PluginLines.Add(result.Ok ? "[not performed] the approved value was recorded, not set. " + report : report);
      Report(report);
      if (result.Ok) next.Enqueue(new Pending { Capability = SetValueCore.Continuation });
      else FollowUpIfAuto(record, next, autoApproved, landed: false);
    }

    /// <summary>The panel's <c>ConsentRoute.EditScript</c>. A script edit that lands grants no read, in the panel either: the report ends it.</summary>
    private void ApplyScript(SeedPlan plan, TurnRecord record, Queue<Pending> next, bool autoApproved)
    {
      var ok = _world.ApplyScript(plan, out var text);
      var report = ScriptEditReport.Text(ok, text);
      record.PluginLines.Add(ok ? "[not performed] the approved script edit was recorded, not run. " + report : report);
      Report(report);
      FollowUpIfAuto(record, next, autoApproved, ok);
    }

    /// <summary>The panel's <c>ConsentRoute.Edit</c>: the report, then the one re-read the card named, only if it landed.</summary>
    private void ApplyEdit(SeedPlan plan, TurnRecord record, Queue<Pending> next, bool autoApproved)
    {
      var ok = _world.ApplyEdit(plan, out var text);
      var report = ScriptEditReport.Text(ok, text);
      record.PluginLines.Add(ok ? "[not performed] the approved edit was recorded, not made. " + report : report);
      Report(report);
      if (ok) next.Enqueue(new Pending { Capability = SetValueCore.Continuation });
      else FollowUpIfAuto(record, next, autoApproved, landed: false);
    }

    /// <summary>The panel's <c>FollowUpIfAuto</c>: an automatic approval that did not land gets one read, because nobody clicked and nobody would otherwise answer it.</summary>
    private static void FollowUpIfAuto(TurnRecord record, Queue<Pending> next, bool autoApproved, bool landed)
    {
      if (!AutoFollowUp.AfterApplied(autoApproved, landed)) return;
      record.PluginLines.Add(AutoFollowUp.Line);
      next.Enqueue(new Pending { Capability = TurnCapability.ReadOnly });
    }

    /// <summary>The panel's <c>ApplySpot</c>: the plugin applies a placement from the card's own record, with the spot through its own door in the scope gate, then grants one read. Recorded, so
    /// it always lands unless the record does not parse, and the failure path is the panel's too.</summary>
    private void ApplySpot(SeedPlan plan, TurnRecord record, Queue<Pending> next, bool autoApproved)
    {
      var resolution = BuildScope.Resolve(plan.Regions, _world.Regions, plan.PreFillSpot);
      if (!string.IsNullOrEmpty(resolution.Message)) record.PluginLines.Add(resolution.Message);
      if (resolution.Outcome == BuildScopeOutcome.Refuse) return;

      if (plan.PreFillPlace == null)
      {
        const string lost = "The placement Dwight offered is no longer on this card, so nothing was built.";
        record.PluginLines.Add(lost);
        Report(SpotAsk.NotAppliedText(lost));
        return;
      }

      var result = _world.PlaceSpot(plan.PreFillPlace, plan.PreFillSpot, plan.PreFillSpotGrouped, plan.PreFillIntoGroup);
      if (result.Ok)
      {
        var landed = SpotAsk.PlacedReport(plan.PreFillPlace, plan.PreFillIntoName ?? plan.PreFillSpot, result.Result, plan.PreFillSpotGrouped);
        record.PluginLines.Add("[not performed] the approved placement was recorded at " + (plan.PreFillIntoName ?? plan.PreFillSpot) + ", not built. " + landed);
        Report(landed);
        next.Enqueue(new Pending { Capability = TurnCapability.ReadOnly });
        return;
      }

      var failed = SpotAsk.FailedText(result);
      record.PluginLines.Add(failed);
      Report(failed);
      if (AutoFollowUp.AfterApplied(autoApproved, false))
      {
        record.PluginLines.Add(AutoFollowUp.Line);
        next.Enqueue(new Pending { Capability = TurnCapability.ReadOnly });
      }
    }

    private void Report(string src) => _stored.Add(new StoredMessage { Who = ChatToTranscript.Report, Src = src, Html = src });

    /// <summary>What the panel's <c>SavedTexts</c> reads for the mint: both what was shown and the source beside it. A row is one conversation on one document.</summary>
    private IEnumerable<string> SavedTexts() => _stored.SelectMany(m => new[] { m.Html, m.Src });

    /// <summary>One tool call, with the gates of <c>AgentLoop.Execute</c> in their order.</summary>
    private string Execute(Pending p, LlmBlock call, TurnRecord record, List<RaisedCard> cards, string approvedKind)
    {
      var tool = new ToolRecord { Name = call.ToolName, Arguments = call.ToolInputJson };
      record.Tools.Add(tool);

      var known = AgentHostTools.Knows(call.ToolName) || AgentToolCatalog.All().Any(t => t.Name == call.ToolName) ||
                  string.Equals(call.ToolName, FullControl.ToolName, StringComparison.OrdinalIgnoreCase);
      if (!known && !_result.UnknownToolsTried.Contains(call.ToolName)) _result.UnknownToolsTried.Add(call.ToolName);

      if (!AgentLoopCore.IsGranted(p.Capability, call.ToolName))
      {
        tool.AnsweredBy = "refused";
        return tool.Answer = AgentLoopCore.GrantRefusal(call.ToolName);
      }

      if (AgentHostTools.Knows(call.ToolName))
        return tool.Answer = Request(p, call, tool, cards, approvedKind);

      var descriptor = AgentToolCatalog.All().FirstOrDefault(t => t.Name == call.ToolName);
      var mutates = descriptor != null && (descriptor.Mutates || descriptor.Solves);
      if (mutates && call.ToolName != AgentToolCatalog.ProposeScaffold && call.ToolName != AgentToolCatalog.BuildRegion)
      {
        tool.AnsweredBy = "recorded";
        return tool.Answer = _world.RecordMutation(call.ToolName, call.ToolInputJson);
      }

      var answer = _world.Answer(call.ToolName, call.ToolInputJson, out var by);
      tool.AnsweredBy = by;
      return tool.Answer = answer;
    }

    /// <summary>The door, in the order of the panel's <c>HostTool</c>. The product's parser runs and its refusals stand. An edit's rows and a placement are checked by the product's own code over
    /// the stated world; the checks that need a live canvas do not exist here, and every result says which. The card is recorded, and <see cref="ApprovalMode.Decide"/> says what happens to it.</summary>
    private string Request(Pending p, LlmBlock call, ToolRecord tool, List<RaisedCard> cards, string approvedKind)
    {
      tool.AnsweredBy = "refused";
      var args = AgentToolCore.ParseArgs(call.ToolInputJson, out var argError);
      if (argError != null) return argError.ToJson();

      // The panel's run_script door, ahead of the parse, only where Full control is on.
      if (_row.FullControl && args["kind"]?.GetValue<string>() == FullControl.Kind)
      {
        var runWhy = args["why"]?.GetValue<string>();
        if (!FullControl.TryParseAsk(args, runWhy, out var grant, out var runError)) return runError.ToJson();
        tool.AnsweredBy = "recorded";
        if (FullControl.SameScript(_waitingScript?.Source, grant.Source)) return FullControl.StillWaitingResult();

        var runDecision = ApprovalMode.Decide(_row.ApprovalMode, FullControl.Kind, ApprovalMode.FactsFor(FullControl.Kind, 1, 0, _row.FullControl));
        var runCard = new CardRecord { Kind = FullControl.Kind, Why = runWhy, Seed = call.ToolInputJson, Decision = runDecision.ToString() };
        _result.Turns[_result.Turns.Count - 1].Cards.Add(runCard);
        var runSeed = new HandoffSeed { Source = grant.Source, Why = grant.Purpose };
        _waitingScript = runDecision == ApprovalDecision.Auto ? null : runSeed;
        cards.Add(new RaisedCard { Record = runCard, Kind = FullControl.Kind, Seed = runSeed, Auto = runDecision == ApprovalDecision.Auto });
        return AgentHostTools.CardShownResult(FullControl.Kind);
      }

      if (!AgentHostTools.TryParseRequest(args, _log, _world.Stamp, approvedKind, p.Regions, null,
                                          out var kind, out var why, out var seed, out var error))
        return error.ToJson();

      // The panel's asks for a value and a script edit: refused by name before a card exists.
      if (ConsentCard.Route(kind) == ConsentRoute.SetValue && !_world.PrepareValue(seed, out var valueError)) return valueError.ToJson();
      if (ConsentCard.Route(kind) == ConsentRoute.EditScript && !_world.PrepareScript(seed, out var scriptError)) return scriptError.ToJson();

      // The panel's PrepareEdit: every row resolves against the canvas or the ask is refused by name, the resolved record overwrites the nomination, and the
      // deletes in it are what the mode decides on.
      EditPlan editPlan = null;
      if (ConsentCard.Route(kind) == ConsentRoute.Edit)
      {
        if (!_world.PrepareEdit(seed, out editPlan, out var editError)) return editError.ToJson();
        seed.Ops = editPlan.Record;
      }

      var scriptCount = 0;
      SpotAsk spot = null;
      if (ConsentCard.Route(kind) == ConsentRoute.Build)
      {
        var routing = SpotAsk.RouteFor(seed);
        if (routing.Refusal != null) return routing.Refusal.ToJson();
        if (routing.Route == BuildAskRoute.Spot)
        {
          // The panel's PrepareSpot. Without it the card has no spot, the click reads no placement, and the scope gate refuses a card the row shows as approved.
          if (seed?.Scripts != null && seed.Scripts.Count > 0)
            return ToolResult.Fail(ToolErrors.InvalidArgs,
              "\"scripts\" belongs to a build into a map region. A placement without a map carries a script's source on the " +
              "component in \"place\" itself, and its card shows it there.").ToJson();
          if (!_world.PrepareSpot(seed, SavedTexts(), out spot, out var spotError)) return spotError.ToJson();
          scriptCount = spot.Placement?.Scripts?.Count ?? 0;
        }
        else
        {
          var live = (seed?.Regions ?? new List<string>()).Where(r => _world.Regions.Contains(r)).ToList();
          if (live.Count == 0)
            return ToolResult.Fail(ToolErrors.UnknownRegion,
              "None of those regions are on the canvas, so there is nothing to ask the user to approve. Call get_scaffold_map and name regions that exist.").ToJson();
          seed.Regions = live;
          if (!ScriptsAsk.Resolve(seed.Scripts, live, out var resolved, out var scriptsError)) return scriptsError.ToJson();
          if (resolved.Count == 0) resolved = _scriptsOnFile.For(_row.Id, live);
          _scriptsOnFile.Remember(_row.Id, resolved);
          scriptCount = resolved.Count;
          seed.Scripts = resolved.Count == 0 ? null : ScriptsAsk.Record(resolved);
        }
      }

      var decision = ApprovalMode.Decide(_row.ApprovalMode, kind, ApprovalMode.FactsFor(kind, scriptCount, editPlan?.Deletes ?? 0));
      var card = new CardRecord { Kind = kind, Why = why, Seed = call.ToolInputJson, Decision = decision.ToString(), Spot = spot?.Placement?.Into ?? spot?.Placement?.Address };
      _result.Turns[_result.Turns.Count - 1].Cards.Add(card);
      // A spot card has two buttons. A row, like an automatic approval, takes the primary one: in a group.
      cards.Add(new RaisedCard { Record = card, Kind = kind, Seed = spot != null ? seed.ForOption(grouped: true) : seed, Auto = decision == ApprovalDecision.Auto });

      tool.AnsweredBy = "recorded";
      return AgentHostTools.CardShownResult(kind);
    }
  }
}
