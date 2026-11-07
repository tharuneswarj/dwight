using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Dwight.Llm;

namespace Dwight.Agent
{
  /// <summary>
  /// What the driver needs to run one turn - everything except how a tool is DISPATCHED, which is the one
  /// GH-coupled step and arrives as <see cref="Execute"/>.
  /// </summary>
  internal sealed class TurnDrive
  {
    public ILlmProvider Provider;
    public string System;
    public List<LlmMessage> Messages;
    public List<LlmTool> Tools = new List<LlmTool>();
    public TurnLimits Limits;
    public bool Thinking;
    public string Effort;
    /// <summary>Brief BT: the tool every step of this turn must call, or null. Sent only where the tool is offered and the connection
    /// can force it (<see cref="AgentLoopCore.ForcedTool"/>).</summary>
    public string ForceToolName;
    public CancellationToken Cancel;
    /// <summary>The generation guard (ADR-089). False once this turn has been superseded.</summary>
    public Func<bool> IsCurrent;
    /// <summary>Called on each tool dispatch, before it runs. Best-effort.</summary>
    public Action<string> OnToolCall;
    /// <summary>Run ONE tool call and return its wire envelope. In the product this is
    /// <c>AgentLoop.Execute</c>, the grant check and the real dispatch; in a test it is a script.</summary>
    public Func<LlmBlock, string> Execute;
    /// <summary>The tool that carries out the approval this turn holds (build_region for an approved build),
    /// or null for a turn that holds none - so the turn's shape can say whether the approval ever ran (#624).</summary>
    public string ActingTool;
    /// <summary>For an approved build: the regions the user approved - what the #657 repair checks dispatches against and
    /// names in its fact. Null for every other turn.</summary>
    public IReadOnlyList<string> ApprovedRegions;
    /// <summary>Brief V: ask the provider to run its own web search inside each request (Anthropic's server tool). False
    /// unless the user turned web search on and the provider has one; false leaves every request as it was.</summary>
    public bool ServerWebSearch;
  }

  /// <summary>
  /// THE AGENT LOOP'S STEPS, GH-free (#612).
  ///
  /// WHY IT WAS MOVED OUT OF <c>AgentLoop</c>. The loop's decisions were tested as pure functions beside
  /// it - and one of those tests asserted that <c>request_mutation</c> ends the turn BY NAME. It was
  /// green, and it was the defect: a REFUSED request ended the turn exactly as a shown card did, so the
  /// model never read the refusal (#609 link 3). A function beside the loop tests the function, not the
  /// loop. So the loop itself lives here, where a scripted provider can drive it, and
  /// <c>AgentLoop.Run</c> is a wrapper that supplies the one step a test cannot run: dispatching a tool
  /// against the live document.
  /// </summary>
  internal static class AgentTurnDriver
  {
    public static TurnResult Drive(TurnDrive d, Stopwatch clock)
    {
      var result = new TurnResult();
      var messages = d.Messages ?? new List<LlmMessage>();
      // #973: the refusals the model has been SENT and so has read - the bound counts these, not arrivals.
      var readRefusals = new HashSet<string>(StringComparer.Ordinal);
      var readRefusalCount = 0;
      // Brief V: consecutive replies that ended with the provider PAUSING its own search loop. One is continued; a
      // second in a row ends the turn with a sentence.
      var consecutivePauses = 0;

      // Brief BT: what the turn offered and could force, for the turn line, and the one tool it must call if any.
      result.RequestOffered = d.Tools != null && d.Tools.Exists(t => string.Equals(t.Name, AgentHostTools.RequestMutation, StringComparison.Ordinal));
      result.ProviderForces = d.Provider != null && d.Provider.SendsForcedTool;
      var forcedTool = AgentLoopCore.ForcedTool(d.ForceToolName, d.Tools, result.ProviderForces);
      result.Forced = forcedTool != null;

      while (true)
      {
        var stop = AgentLoopCore.PreFlight(Cancelled(d), result.Steps, clock.ElapsedMilliseconds, d.Limits);
        // Brief CN: a bound must not end a turn on a diagnostic nobody has read. A turn out of steps, whose last reply was a call, gets one step to say where it got to. The fact
        // joins the results that have not been sent yet, so the history stays append-only.
        var closing = stop.HasValue && AgentLoopCore.EarnsClosingStep(stop.Value, result.LastStepToolCalls);
        if (stop.HasValue && !closing) return Finish(result, stop.Value, d);
        if (closing)
        {
          result.ClosingStep = true;
          messages[messages.Count - 1].Blocks.Add(LlmBlock.TextBlock(AgentLoopCore.ClosingStepFact));
        }
        var forcing = closing ? null : forcedTool;

        // Hold the payload inside its budget BEFORE spending on it, and carry the count so the meta
        // line can state it. Elision is cumulative across the turn, which is why it accumulates here.
        // Brief BZ: not where the history must stay append-only. The provider's "Preserved thinking" page lists "Clear or shorten an earlier tool_result" as invalid for every later
        // thinking block, a 400 at the next step, and stripping the blocks instead is its last resort. These rows have a 1M window and read the cache at 0.05 to 0.1 of input, so
        // what elision protects against (#209, paying for every row on every step) costs little there, and the step cap still bounds the turn.
        if (!d.Provider.KeepsHistoryAppendOnly)
          result.ElidedToolResults += AgentLoopCore.Elide(messages, d.Limits.MaxTranscriptChars);

        // #297: check our own payload before spending on it. AFTER elision, because elision is the one
        // step in this loop that edits the transcript - a check that ran before it would certify a list
        // that is not the list we send.
        var malformed = AgentLoopCore.CheckTranscript(messages) ?? AgentLoopCore.CheckEnding(messages, consecutivePauses > 0);
        if (malformed != null)
        {
          if (closing) return Finish(result, TurnOutcome.Exhausted, d);
          result.Error = "Dwight could not assemble this turn: " + malformed + ".";
          return Finish(result, TurnOutcome.Failed, d);
        }

        // The same check one field over (LlmSchemaCheck): a tool whose schema cannot be published must
        // not be offered. The provider seam refuses this too, and THAT is the guarantee - it is what
        // covers the five verbs, which never reach this loop. This one exists because a turn that dies
        // here reads as ours and names the tool, rather than arriving as an exception from the transport.
        var badSchema = LlmSchemaCheck.Check(new LlmRequest { Tools = d.Tools });
        if (badSchema != null)
        {
          if (closing) return Finish(result, TurnOutcome.Exhausted, d);
          result.Error = "Dwight could not assemble this turn: " + badSchema + ".";
          return Finish(result, TurnOutcome.Failed, d);
        }

        // The plan note is an Anthropic-only diagnostic held in a static: only an Anthropic body build rewrites it. Cleared
        // before every send, so a step on another provider cannot read the note a previous Anthropic call left behind.
        AnthropicRequestBody.LastPlanNote = null;

        LlmResponse response;
        try
        {
          response = d.Provider.Send(new LlmRequest
          {
            System = d.System,
            Messages = messages,
            Tools = d.Tools,
            MaxTokens = d.Limits.AnswerBudget,   // the ANSWER budget; the reserve rides on top (#216)
            CacheSystem = true,
            // Brief BT: a forced step sends thinking off, so on Anthropic it is the wire the comprehension verbs already ship: forced plus
            // Disabled on an adaptive-on model, forced plus nothing on an adaptive-off one (ADR-082, ThinkingWire). Forced plus adaptive was
            // measured live on Sonnet 5 alone (#215), and a 400 here would lose the card the turn exists to raise.
            // Brief BV: a connection may say otherwise, because on OpenAI this would send a forced script written with no reasoning.
            Thinking = (forcing == null || d.Provider.ThinksWhileForced) && d.Thinking,
            Effort = forcing == null || d.Provider.ThinksWhileForced ? d.Effort : null,
            ForceToolName = forcing,
            ForbidTools = closing,
            ServerWebSearch = forcing == null && d.ServerWebSearch,
          }, d.Cancel);
        }
        catch (OperationCanceledException stopped)
        {
          if (UsageCarrier.TryTake(stopped, out var counted))
          {
            Accumulate(result.Usage, counted);   // the stream had said the provider's count before the cut
            result.InFlightCounted = true;
            return Finish(result, TurnOutcome.Stopped, d);
          }
          result.InFlightChars = RequestChars(d.System, messages);   // Brief DL: the request went out; the Stop cancelled it, and no usage will arrive
          result.InFlightImageTokens = RequestImageTokens(messages);   // Brief DX: and the images in it, by the plugin's own count
          result.InFlightUncountedImages = RequestUncountedImages(messages);   // or the ones it has no count for
          return Finish(result, TurnOutcome.Stopped, d);
        }
        catch (Exception ex)
        {
          // The closing step is a courtesy: the turn ran out of steps, and that stays what happened, with the note alone as it was.
          if (closing) return Finish(result, TurnOutcome.Exhausted, d);
          result.Error = ex.Message;
          return Finish(result, TurnOutcome.Failed, d);
        }

        if (closing) return FinishClosing(result, response, d);

        result.Steps++;
        result.LastStopReason = response.StopReason;

        // Brief BZ: a refusal ends the turn in the plugin's words. Nothing in the refused reply is used, kept or run, and the words from earlier steps stay, with the turn's other work.
        if (LlmRefusal.Is(response))
        {
          if (LlmRefusal.IsBilled(response)) Accumulate(result.Usage, response.Usage);
          var refused = Finish(result, TurnOutcome.Refused, d);
          refused.Note = LlmRefusal.Sentence(response.RefusalCategory);
          return refused;
        }

        result.LastStepText = response.Text ?? "";
        Accumulate(result.Usage, response.Usage);

        // #216c: an effort step-down is a fact the user paid for. Read it immediately - the field
        // describes the last body built and the next step will overwrite it.
        var planNote = AnthropicRequestBody.LastPlanNote;
        if (!string.IsNullOrWhiteSpace(planNote)) result.PlanNote = planNote;

        var text = response.Text;
        if (!string.IsNullOrWhiteSpace(text)) result.Text = text;   // keep the latest prose, whatever ends the turn

        // Brief V: a search the provider could not run is reported in Dwight's voice by the caller. It is a 200 with an
        // error inside, and the model reads it and answers anyway, so it is a note, never a failed run.
        if (response.WebSearchErrors != null) result.WebSearchErrors.AddRange(response.WebSearchErrors);

        // Brief V: the provider paused its own search loop. "Send the paused assistant message back unchanged" - once.
        // A second pause in a row ends the turn, in words, rather than looping on the provider's schedule.
        if (string.Equals(response.StopReason, "pause_turn", StringComparison.Ordinal))
        {
          consecutivePauses++;
          if (consecutivePauses > 1 || response.Blocks.Count == 0)
          {
            var paused = Finish(result, TurnOutcome.Complete, d);
            paused.Note = WebSearch.PausedTwiceNote;
            return paused;
          }
          messages.Add(LlmMessage.Assistant(response.Blocks));
          continue;
        }
        consecutivePauses = 0;

        var toolUses = response.ToolUses;
        result.LastStepToolCalls = toolUses.Count;   // U97: zero when the turn ends on prose
        var verdict = AgentLoopCore.PostResponse(response.StopReason, toolUses.Count > 0);

        // #657/#666: THE BOUNDED REPAIR. An approved build turn that reaches its own end of turn without having
        // dispatched build_region for any approved region gets ONE more step, carrying a plugin fact that names the
        // tool and the region - mechanically actionable, the ADR-094 condition for a closed loop. The prose written
        // before it described a build that had not happened, so it is DISCARDED: it must never render over a real
        // build it did not describe. The fact lives in this turn's message list only - never in the text, the note,
        // or the stored transcript.
        if (verdict == TurnOutcome.Complete && AgentLoopCore.ShouldRepairBuild(d.ActingTool, d.ApprovedRegions,
              result.ApprovedBuildDispatches, result.BuildRepairFired))
        {
          if (response.Blocks.Count > 0) messages.Add(LlmMessage.Assistant(response.Blocks));
          messages.Add(LlmMessage.User(AgentLoopCore.BuildRepairFact(d.ApprovedRegions)));
          result.BuildRepairFired = true;
          result.Text = "";
          continue;
        }

        if (verdict == TurnOutcome.Truncated)
        {
          result.ContextWindowFilled = response.ContextWindowFilled;
          result.ThoughtWithoutAnswer = response.ThoughtWithoutAnswer;
        }
        if (verdict.HasValue) return Finish(result, verdict.Value, d);

        // The assistant turn is echoed back VERBATIM - thinking blocks, signatures and all. Rebuilding
        // it or dropping a block is a 400 on the next step (#192b), and this loop is the first code to
        // exercise that round-trip more than once per call.
        messages.Add(LlmMessage.Assistant(response.Blocks));

        var results = new LlmMessage { Role = LlmRole.User };
        string lastRefusedArgs = null, lastRefusal = null;
        var stepRefusals = new List<string>();
        foreach (var call in toolUses)
        {
          if (Cancelled(d)) return Finish(result, TurnOutcome.Stopped, d);

          try { d.OnToolCall?.Invoke(call.ToolName); } catch { }
          var envelope = d.Execute(call);
          results.Blocks.Add(LlmBlock.ToolResult(call.ToolUseId, envelope));
          result.ToolCalls++;
          if (AgentToolCatalog.Acts(call.ToolName)) result.ActingCalls++;
          if (d.ActingTool != null && string.Equals(call.ToolName, d.ActingTool, StringComparison.OrdinalIgnoreCase))
            result.ApprovedToolCalls++;
          // #657: a build_region call aimed at a region the user APPROVED - the only dispatch that means the approved
          // build ran. An unaddressed call, or one for another region, does not.
          if (AgentLoopCore.IsApprovedBuild(call.ToolName, call.ToolInputJson, d.ApprovedRegions))
            result.ApprovedBuildDispatches++;

          // #611a: what ends the turn is a card the user can SEE, read off the result. A refused request
          // opened no gate, so the model reads the refusal in its next step and acts on it.
          // #659: every request counts, a shown card or a refusal alike - both ASKED.
          result.ToolTrail.Add(TurnReadLine.Of(call.ToolName, call.ToolInputJson, envelope));
          if (AgentHostTools.IsRequest(call.ToolName)) result.RequestCalls++;
          if (AgentLoopCore.WentThroughCardDoor(call.ToolName, envelope)) result.CardDoorCalls++;
          if (AgentHostTools.ShowedCard(call.ToolName, envelope)) result.AskedForConsent = true;
          else if (AgentHostTools.IsRequest(call.ToolName))
          {
            result.RefusedRequests++;
            lastRefusedArgs = call.ToolInputJson;
            lastRefusal = envelope;
            stepRefusals.Add(AgentLoopCore.RefusalIdentity(envelope));
          }
        }
        messages.Add(results);

        // #256: a tool that opened a HUMAN gate ends the turn. The answer cannot arrive in this reply -
        // it arrives as a new message when the user clicks - so another step could only re-ask. Given
        // nothing back and another chance, the model called it four times and then invented a
        // Grasshopper dialog for the user to go and find.
        if (result.AskedForConsent) return Finish(result, TurnOutcome.Complete, d);

        // #611a's bound, as #973 refined it: the bound counts refusals the model has READ. A step's refusals end the
        // turn only if the model already read the same refusal and repeated it, or already read the cap and produced
        // another; otherwise they go back in this step's results and are read at the next. The user is told why in
        // the plugin's own words (#615) - the LAST refusal's class, never the refusal itself, which is addressed
        // to the model.
        if (AgentLoopCore.RefusalEndsTurn(readRefusals, readRefusalCount, stepRefusals) ||
            AgentLoopCore.ForcedRefusalEndsTurn(forcedTool != null, readRefusalCount, stepRefusals.Count))
        {
          var ended = Finish(result, TurnOutcome.Complete, d);
          ended.Note = AgentLoopCore.RefusedRequestNote(lastRefusedArgs, lastRefusal);
          return ended;
        }
        foreach (var id in stepRefusals) readRefusals.Add(id);
        readRefusalCount += stepRefusals.Count;
      }
    }

    private static bool Cancelled(TurnDrive d)
    {
      if (d.Cancel.IsCancellationRequested) return true;
      try { return d.IsCurrent != null && !d.IsCurrent(); } catch { return false; }
    }

    /// <summary>Brief CN: what the closing step said, shown above the note. It counts toward the tokens and not toward the steps, whose number the note states. A call it made anyway is
    /// not run, and a refusal leaves the note alone.</summary>
    private static TurnResult FinishClosing(TurnResult result, LlmResponse response, TurnDrive d)
    {
      result.LastStopReason = response.StopReason;
      if (LlmRefusal.Is(response))
      {
        if (LlmRefusal.IsBilled(response)) Accumulate(result.Usage, response.Usage);
        return Finish(result, TurnOutcome.Exhausted, d);
      }
      Accumulate(result.Usage, response.Usage);
      result.LastStepText = response.Text ?? "";
      result.LastStepToolCalls = response.ToolUses.Count;
      if (!string.IsNullOrWhiteSpace(response.Text)) result.Text = response.Text;
      return Finish(result, TurnOutcome.Exhausted, d);
    }

    private static TurnResult Finish(TurnResult result, TurnOutcome outcome, TurnDrive d)
    {
      result.Outcome = outcome;
      result.Note = AgentLoopCore.NoteFor(outcome, d.Limits, !string.IsNullOrWhiteSpace(result.Text), result.ContextWindowFilled, result.ThoughtWithoutAnswer);

      // #624: an APPROVED BUILD turn that never dispatched build_region - the field case, counted with its
      // denominator. Only build: the ruling asked for it, and it is the kind whose close contradicted its prose.
      if (string.Equals(d.ActingTool, AgentToolCatalog.BuildRegion, StringComparison.OrdinalIgnoreCase))
      {
        try { TurnShape.ObserveApprovedBuild(result.ApprovedToolCalls); } catch { }
      }

      // F5: observe the turn's SHAPE, and change nothing about it. This is the one place every turn
      // ends, so it is the only place the observation is complete. It records whether the turn stopped
      // to ask permission to ask - a prompt line's effect on a model cannot be asserted, so the honest
      // instrument counts recurrence instead (#523).
      // A closing step was asked to say what it was about to do, so its words are not a turn's ending to be counted.
      if (!result.ClosingStep)
        try { TurnShape.Observe(result.Text, result.ToolCalls, result.ActingCalls, result.RequestCalls); } catch { }

      return result;
    }

    private static void Accumulate(LlmUsage total, LlmUsage one)
    {
      if (one == null) return;
      total.InputTokens += one.InputTokens;
      total.OutputTokens += one.OutputTokens;
      total.CacheReadInputTokens += one.CacheReadInputTokens;
      total.CacheCreationInputTokens += one.CacheCreationInputTokens;
      total.ThinkingTokens += one.ThinkingTokens;
      total.WebSearchRequests += one.WebSearchRequests;
    }
      /// <summary>Brief DL: the characters a step's request carries, which is what a stopped step is estimated from: the system text, and every block's text and tool input.</summary>
    internal static long RequestChars(string system, System.Collections.Generic.IEnumerable<LlmMessage> messages)
    {
      long n = system?.Length ?? 0;
      foreach (var m in messages ?? System.Linq.Enumerable.Empty<LlmMessage>())
        foreach (var b in m?.Blocks ?? new System.Collections.Generic.List<LlmBlock>())
          n += (b?.Text?.Length ?? 0) + (b?.ToolInputJson?.Length ?? 0);
      return n;
    }

    /// <summary>Brief DX: the tokens a step's images are estimated to cost, which are not in its characters. Only where the rule holds: anywhere else the count would be wrong.</summary>
    internal static long RequestImageTokens(System.Collections.Generic.IEnumerable<LlmMessage> messages)
    {
      long n = 0;
      foreach (var m in messages ?? System.Linq.Enumerable.Empty<LlmMessage>())
        foreach (var b in m?.Blocks ?? new System.Collections.Generic.List<LlmBlock>())
          if (b?.ImageRuleKnown == true) n += b.ImageTokens;
      return n;
    }

    /// <summary>The images in a step's request that the rule does not hold for, so its estimate leaves them out.</summary>
    internal static int RequestUncountedImages(System.Collections.Generic.IEnumerable<LlmMessage> messages)
    {
      var n = 0;
      foreach (var m in messages ?? System.Linq.Enumerable.Empty<LlmMessage>())
        foreach (var b in m?.Blocks ?? new System.Collections.Generic.List<LlmBlock>())
          if (b?.Kind == LlmBlock.ImageKind && !b.ImageRuleKnown) n++;
      return n;
    }
}
}
