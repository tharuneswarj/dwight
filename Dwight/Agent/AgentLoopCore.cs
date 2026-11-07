using System;
using System.Collections.Generic;
using System.Linq;
using Dwight.Llm;

namespace Dwight.Agent
{
  /// <summary>
  /// WHAT ONE TURN IS ALLOWED TO DO (Phase-8 WP-3, ruled at #220).
  ///
  /// ADR-075's gate is CAPABILITY BY CONSTRUCTION, not intent classification: the tools advertised to
  /// the model on a turn are a pure function of the capability the USER granted, and a model holding
  /// only read tools has nothing to write with, whatever it intends. An intent classifier is a
  /// knowledge judgement, and #208 forbids a fallible knowledge step sitting beneath an identity-class
  /// question - and "may this turn write" is identity-class.
  ///
  /// THIS VALUE IS NEVER PERSISTED (#220). <c>StoredThread.Phase</c> records which stage a conversation
  /// REACHED, for rendering; it does not confer anything. The grant is passed per turn, in memory, and
  /// is absent by construction after a reopen - so a scaffold-phase thread that comes back after a
  /// restart renders its history and must ask again before it can write. That is WP-1's own instrument
  /// (#161's non-serialisable <c>Live</c> field) applied to the surface it was invented for.
  /// </summary>
  internal enum TurnCapability
  {
    /// <summary>Comprehension and planning: the seven reads. Zero-token, solve-free, mutation-free
    /// (#190) - so this turn cannot spend, cannot change the canvas, and cannot even make Grasshopper
    /// recompute. <c>solve_and_check</c> is deliberately NOT here (#222 Q6): a solve is an observable
    /// side effect on the user's document, and a read must never do work they did not ask for.</summary>
    ReadOnly = 0,

    /// <summary>Building: the reads plus the two tools that do declared work. WP-4 grants it from the
    /// build consent card; in WP-3 it exists so the loop's asynchronous dispatch can be exercised
    /// against a MUTATING tool (#223), which is the longest path and the one that hangs rather than
    /// errors if the threading is wrong.</summary>
    Build = 1,

    /// <summary>
    /// Reads plus <c>solve_and_check</c>, and nothing that mutates - granted by an approved
    /// <c>solve</c> consent card.
    ///
    /// It exists because of #222's generalisation: the card is consent-to-WORK, not only
    /// consent-to-mutate. A solve is an observable side effect on the user's document, so a turn that
    /// needs fresh state asks for it through the same click as any other change - one mechanism, two
    /// uses, and no second convention.
    /// </summary>
    ReadAndSolve = 2,

    /// <summary>Reads plus <c>propose_scaffold</c> - granted by an approved <c>scaffold</c> consent card.
    /// It lays down a MAP and builds nothing, so it grants no way to place a component: the map is the
    /// point, and building it is WP-4's separate consent.</summary>
    Scaffold = 3,

    /// <summary>Reads plus <c>run_script</c> - granted by an approved <c>run_script</c> consent card, on a conversation whose Full
    /// control is on. The tool runs ONE script, the exact source the card showed, once (U90).</summary>
    RunScript = 4,
  }

  /// <summary>How a turn ended. Every outcome other than <see cref="Complete"/> keeps whatever the turn
  /// produced and says what happened - an exhausted turn is an honest stop with partial value (#223,
  /// WP-0's failure-becomes-advice), never a discarded turn or a silent truncation.</summary>
  internal enum TurnOutcome
  {
    Complete,
    /// <summary>The step budget ran out with the model still calling tools.</summary>
    Exhausted,
    /// <summary>The turn's wall clock ran out.</summary>
    TimedOut,
    /// <summary>A reply was cut off at the token limit (#216d) - a first-class outcome, never rendered
    /// as a finished answer.</summary>
    Truncated,
    /// <summary>Stop, Restart, or the session timeout (ADR-089).</summary>
    Stopped,
    /// <summary>The provider or the loop failed. Carries its reason.</summary>
    Failed,
    /// <summary>Brief BZ: the model's safety checks declined this step. A first-class outcome: never an empty reply, and what it had begun is discarded.</summary>
    Refused,
  }

  internal sealed class TurnLimits
  {
    public int MaxSteps;
    public int WallClockMs;
    public int MaxTranscriptChars;
    /// <summary>The ANSWER budget for each step (#216). The thinking reserve is added at the provider
    /// seam, never carved out of this.</summary>
    public int AnswerBudget;
  }

  internal sealed class TurnResult
  {
    public TurnOutcome Outcome;
    /// <summary>Whatever prose the turn produced - present even when the outcome is not Complete.</summary>
    public string Text = "";
    public int Steps;
    public int ToolCalls;
    /// <summary>
    /// Brief DL: the characters of the request that was in flight when a Stop cancelled the turn (the system text and every message, as the step sent them), or 0. The provider's usage for that
    /// step never arrives, so the turn's line is an estimate of it. Set only on a Stopped turn whose request had gone out.
    /// </summary>
    public long InFlightChars;
    /// <summary>Brief DX: the tokens the images in that request are estimated to cost. Apart from the characters, because an image is not priced by its base64.</summary>
    public long InFlightImageTokens;
    /// <summary>How many images in that request the plugin has no count for, because its rule does not hold for this model. They are left out of the estimate, and the person is told.</summary>
    public int InFlightUncountedImages;
    /// <summary>The stopped request's stream had said the provider's count before the cut, so it is in <see cref="Usage"/> and the fields above stay empty. What the model wrote before the cut is not in it.</summary>
    public bool InFlightCounted;
    /// <summary>The Truncated outcome came from a prompt that filled a local model's context window.</summary>
    public bool ContextWindowFilled;
    /// <summary>The Truncated outcome came from a model that spent its whole reply thinking and wrote no answer.</summary>
    public bool ThoughtWithoutAnswer;

    /// <summary>
    /// How many of <see cref="ToolCalls"/> MUTATED or SOLVED - the calls that try something on the canvas
    /// rather than read it. A host tool that raises a consent card is neither: asking is not trying.
    /// </summary>
    public int ActingCalls;
    /// <summary>How many calls went to the tool that carries out this turn's approval (#624) - zero on an
    /// approved build turn means the approval never ran, whatever the turn's prose says.</summary>
    public int ApprovedToolCalls;
    /// <summary>build_region calls aimed at a region the user approved (#657) - an unaddressed or other-region call is
    /// not the approved build running.</summary>
    public int ApprovedBuildDispatches;
    /// <summary>request_mutation calls this turn, a shown card and a refusal alike - both ASKED (#659).</summary>
    public int RequestCalls;
    /// <summary>Brief CL: calls that got through one of <see cref="AgentLoopCore.CardDoors"/> this turn.</summary>
    public int CardDoorCalls;
    /// <summary>U97: how many tool calls the turn's LAST step made. Zero is a turn that ended on prose.</summary>
    public int LastStepToolCalls;
    /// <summary>Brief BT: the prose of the LAST reply alone. <see cref="Text"/> keeps the latest prose of any step, so a final empty reply
    /// would be judged on what an earlier step wrote beside a call it did make.</summary>
    public string LastStepText = "";
    /// <summary>Brief BT: the last reply's stop reason, in the adapters' translated words (tool_use, max_tokens, or the wire's own).</summary>
    public string LastStopReason;
    /// <summary>Brief BT: request_mutation was among this turn's tools.</summary>
    public bool RequestOffered;
    /// <summary>Brief BT: this connection can put a forced tool on the wire.</summary>
    public bool ProviderForces;
    /// <summary>Brief BT: a forced tool did go on the wire this turn.</summary>
    public bool Forced;
    /// <summary>Brief CN: this turn ran out of steps and was given its one tool-less step to say where it got to.</summary>
    public bool ClosingStep;
    /// <summary>Brief CN: every tool call this turn, one line each from <see cref="TurnReadLine"/>. Computed from the calls and their results, never from the model's words.</summary>
    public List<string> ToolTrail = new List<string>();
    /// <summary>The #657 repair step fired in this turn (at most once).</summary>
    public bool BuildRepairFired;
    /// <summary>How many earlier tool results were replaced by a placeholder to stay inside the
    /// transcript budget. Reported, never silent (#209/#134).</summary>
    public int ElidedToolResults;
    /// <summary>The honest user-facing sentence for a non-Complete outcome; null when Complete.</summary>
    public string Note;
    /// <summary>An effort step-down the provider seam made to keep the answer budget whole (#216c).
    /// Recorded here because a note written and never read is not a record (the WP-2 lesson).</summary>
    public string PlanNote;
    /// <summary>Brief V: the <c>error_code</c> of each web search the provider could not run this turn (rate limit, the
    /// search cap, unavailable). Said to the user in Dwight's own words; the turn itself is not failed by them.</summary>
    public List<string> WebSearchErrors = new List<string>();
    public string Error;
    /// <summary>#256: this turn opened a HUMAN gate (a consent card) and ended there. The card is the
    /// turn's visible output, so a caller must not fill an empty reply with "nothing to say" - something
    /// very definite happened and it is on screen.</summary>
    public bool AskedForConsent;

    /// <summary>How many consent requests this turn made that showed NO card - refused by the plugin, and
    /// read by the model inside the turn (#611a). The second one ends the turn.</summary>
    public int RefusedRequests;
    public LlmUsage Usage = new LlmUsage();
  }

  /// <summary>
  /// THE MULTI-TOOL LOOP'S DECISIONS, GH-free (ADR-091) and provider-free.
  ///
  /// <c>StructuredOutput</c> forces exactly one tool and is untouched - it stays the one-shot,
  /// schema-constrained path all five verbs use. This is the seam BESIDE it: many tools advertised,
  /// the model choosing among them, the loop executing and feeding results back until it answers.
  ///
  /// Everything here is a pure function over data, so the rules that bound a turn - what it may call,
  /// when it must stop, and what it may resend - are unit-tested rather than reasoned about in a shim
  /// that needs Rhino to run.
  /// </summary>
  internal static class AgentLoopCore
  {
    /// <summary>What an elided tool result is replaced by. The model is TOLD the payload was trimmed
    /// rather than left to infer it from a gap - a bounded payload reports what it omitted.</summary>
    public const string ElidedMarker =
      "[an earlier result from this turn was dropped to stay inside the payload budget - " +
      "call the tool again if you still need it]";

    /// <summary>
    /// Catalogue tools that NO capability grants - a DECLARATION rather than a leftover: the conformance
    /// test asserts every catalogue tool is either granted somewhere or named here, so a tool added to the
    /// surface is granted NOWHERE until somebody decides which turns may call it - and the test says so
    /// rather than the tool quietly appearing on a turn.
    ///
    /// set_value (Phase-9 U4) is here BY DESIGN, not by omission: in the panel the user approves a card
    /// naming the exact change and the PLUGIN applies it from the card - a model call between the approval
    /// and the execution would let the two differ (Q1). The tool exists on the catalogue for the MCP face
    /// and the DEBUG harness, which reach the same applier.
    /// </summary>
    public static readonly IReadOnlyList<string> Ungranted = new[] { AgentToolCatalog.SetValue };

    /// <summary>
    /// The tools a turn may see, from BOTH declared sources (#230 Fork 1): the catalogue (what you can do
    /// to the canvas, and the MCP surface) and the host set (what you can do in this surface).
    ///
    /// Two sources, both declared and both grant-checked - never a single unbounded list, and never a
    /// host tool smuggled into the canvas contract.
    /// </summary>
    public static IReadOnlyList<ToolDescriptor> ToolsFor(TurnCapability capability) =>
      AgentToolCatalog.All().Concat(AgentHostTools.All())
        .Where(t => IsGranted(capability, t.Name))
        .ToList();

    /// <summary>
    /// The same list for a conversation whose Full control may be on (U90). WITH IT OFF this IS the list above, the same objects in
    /// the same order, so a thread that has not turned the switch on has a tool list byte-identical to the build before it existed.
    /// With it on, request_mutation is published with the run_script kind added, and an approved run_script turn also holds the
    /// run_script tool itself - and no turn holds it otherwise.
    /// </summary>
    public static IReadOnlyList<ToolDescriptor> ToolsFor(TurnCapability capability, bool fullControl)
    {
      var baseList = ToolsFor(capability);
      if (!fullControl) return baseList;

      var list = baseList.Select(t => string.Equals(t.Name, AgentHostTools.RequestMutation, StringComparison.OrdinalIgnoreCase)
        ? new ToolDescriptor { Name = t.Name, Description = t.Description, Mutates = t.Mutates, Solves = t.Solves,
                               InputSchema = FullControl.WithRunScriptKind(t.InputSchema) }
        : t).ToList();
      if (capability == TurnCapability.RunScript) list.Add(FullControl.Descriptor());
      return list;
    }

    /// <summary>
    /// May this turn call this tool? The loop asks BEFORE dispatching, so a model naming a tool it was
    /// never offered is refused rather than executed.
    ///
    /// Belt AND braces on purpose: the ungranted tool is not advertised, so this should be unreachable -
    /// but "should be unreachable" is not a safety property, and the one thing that must never happen
    /// is a write dispatched on a read-only turn.
    ///
    /// EVERY CAPABILITY ENUMERATES WHAT IT GRANTS (A1). Build used to answer <c>true</c> to everything,
    /// which meant a tool added to the catalogue later was auto-granted the moment it appeared - an
    /// identity-class decision ("may this turn write") taken by default rather than by anyone. That is
    /// #208's line applied to capability itself: granting is exact, or it does not happen.
    ///
    /// The READ predicate stays derived from the catalogue's own <c>Mutates</c>/<c>Solves</c> facts
    /// rather than a hand-kept list, because a read list COULD drift from the surface it guards. The
    /// working tools are named, because there is nothing to derive them from - which side of the line a
    /// write belongs on is a judgement, and judgements are written down.
    /// </summary>
    public static bool IsGranted(TurnCapability capability, string toolName)
    {
      if (string.IsNullOrWhiteSpace(toolName)) return false;

      // HOST tools (#230): granted at every capability, because asking the user for permission is the one
      // thing a turn must always be able to do - a read-only turn that cannot ask has no way out of
      // read-only. Enumerated here rather than waved through: the host set is grant-checked with the same
      // rigour as the catalogue, and it can never be a write path because it is UI-only by construction.
      if (AgentHostTools.Knows(toolName)) return Is(toolName, AgentHostTools.RequestMutation);

      // U90: run_script is in no catalogue list and is granted to exactly one capability. Whether the CONVERSATION has Full control on is
      // the loop's own check (it holds the request), so this answers only what the capability allows.
      if (Is(toolName, FullControl.ToolName)) return capability == TurnCapability.RunScript;

      if (!AgentToolCatalog.Knows(toolName)) return false;

      var descriptor = AgentToolCatalog.All()
        .First(t => string.Equals(t.Name, toolName, StringComparison.OrdinalIgnoreCase));

      switch (capability)
      {
        case TurnCapability.Build:
          return IsRead(descriptor)
              || Is(toolName, AgentToolCatalog.SolveAndCheck)
              || Is(toolName, AgentToolCatalog.BuildRegion);
        case TurnCapability.ReadAndSolve:
          return IsRead(descriptor) || Is(toolName, AgentToolCatalog.SolveAndCheck);
        case TurnCapability.Scaffold:
          return IsRead(descriptor) || Is(toolName, AgentToolCatalog.ProposeScaffold);
        default:
          return IsRead(descriptor);   // ReadOnly: pure reads only
      }
    }

    /// <summary>A pure read: it cannot spend, cannot solve, and cannot mutate (#190).</summary>
    private static bool IsRead(ToolDescriptor d) => !d.Mutates && !d.Solves;

    private static bool Is(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Hold the transcript inside its budget by replacing the OLDEST tool results with a stated
    /// placeholder, oldest first, until it fits. Returns how many were elided.
    ///
    /// WHY ELIDE CONTENT RATHER THAN DROP MESSAGES: the loop resends the whole turn every step, so an
    /// unbounded transcript grows quadratically and the WP-3 loop pays for every row on every call
    /// (#209). But dropping a MESSAGE would break the tool_use/tool_result pairing the API requires,
    /// and would drop thinking blocks whose verbatim round-trip is what keeps a continuation valid
    /// (#192b). Replacing a result's TEXT keeps every block, every id, and every signature in place.
    /// </summary>
    public static int Elide(List<LlmMessage> messages, int maxChars)
    {
      if (messages == null || maxChars <= 0) return 0;

      var elided = 0;
      while (Size(messages) > maxChars)
      {
        var victim = messages
          .SelectMany(m => m.Blocks ?? new List<LlmBlock>())
          .FirstOrDefault(b => b.Kind == "tool_result" && b.Text != ElidedMarker);
        if (victim == null) break;   // nothing left to give: the caller reports it rather than looping

        victim.Text = ElidedMarker;
        elided++;
      }
      return elided;
    }

    /// <summary>What one transcript costs, counted over everything that actually goes on the wire.</summary>
    public static int Size(IEnumerable<LlmMessage> messages)
    {
      var total = 0;
      foreach (var m in messages ?? Enumerable.Empty<LlmMessage>())
        foreach (var b in m.Blocks ?? new List<LlmBlock>())
          total += (b.Text?.Length ?? 0) + (b.ToolInputJson?.Length ?? 0) + (b.Data?.Length ?? 0) +
                   (b.Kind == LlmBlock.ResponseItemKind ? b.RawJson?.Length ?? 0 : 0) +   // Brief BV: encrypted reasoning is real bytes on every step
                   b.ImageTokens * ImageCharsPerToken;
      return total;
    }

    /// <summary>Brief DX: what an image weighs against the transcript's bound, which is a stand-in for tokens: its token estimate at the ratio the ledger measured (Brief CY, about 2.0 characters a token). Its base64
    /// would read as millions of characters and clear every tool result out of the turn for a picture that costs two thousand tokens.</summary>
    private const int ImageCharsPerToken = 2;

    /// <summary>The gate BEFORE a step is sent. Null means carry on.</summary>
    public static TurnOutcome? PreFlight(bool cancelled, int stepsTaken, long elapsedMs, TurnLimits limits)
    {
      if (cancelled) return TurnOutcome.Stopped;
      if (limits != null && stepsTaken >= limits.MaxSteps) return TurnOutcome.Exhausted;
      if (limits != null && limits.WallClockMs > 0 && elapsedMs >= limits.WallClockMs) return TurnOutcome.TimedOut;
      return null;
    }

    /// <summary>The gate AFTER a reply arrives. Null means execute the tool calls and continue.</summary>
    /// <summary>
    /// THE TRANSCRIPT PRE-FLIGHT (#297): the loop checks its OWN payload before every send, and names what
    /// is wrong with it in our words.
    ///
    /// WHY THIS EXISTS. A malformed transcript comes back as a PROVIDER error - "messages: at least one
    /// message is required" - and that error is a lie about ownership. It reads as the provider rejecting
    /// us when in fact we assembled something that could not be sent, and it sends the reader to the
    /// adapter, the key, the model id: everywhere except the place that built the list. The defect is
    /// ours, so the diagnosis must be ours.
    ///
    /// WHAT IT CHECKS is exactly the set of structural facts we have already paid to learn:
    ///   - non-empty, and no message with no blocks (#296 - the greeting is chrome, so a fresh thread's
    ///     ONLY conversational message is the user's own; lose it and the list is empty);
    ///   - a text block that is blank is not sendable, so an empty one is a defect here, not there;
    ///   - tool_use / tool_result pair up both ways - an unanswered call, or an answer to a call that was
    ///     never made, is a 400 (and the loop's own elision runs over these, so this is a live risk);
    ///   - a thinking block carries its signature (#192b) - a rebuilt or stripped assistant turn 400s on
    ///     the first repair round-trip, which is the failure that made that field exist.
    ///
    /// Returns null when the transcript is sendable, otherwise the problem in one line.
    ///
    /// It is deliberately NOT a repair. A transcript we cannot explain is one we must not silently
    /// rewrite and spend on - the honest stop is worth more than a guess that bills.
    /// </summary>
    public static string CheckTranscript(IReadOnlyList<LlmMessage> messages)
    {
      // (The transcript's ending has its own rule, CheckEnding, because the one legal exception is a fact about the step and not about the list.)
      if (messages == null || messages.Count == 0)
        return "the conversation was empty - no message was assembled to send";

      var awaitingResults = new HashSet<string>();

      for (var i = 0; i < messages.Count; i++)
      {
        var m = messages[i];
        var where = "message " + (i + 1) + " of " + messages.Count;

        if (m == null) return where + " was missing";
        if (m.Blocks == null || m.Blocks.Count == 0) return where + " had no content";

        foreach (var b in m.Blocks)
        {
          if (b == null) return where + " held a missing content block";
          switch (b.Kind)
          {
            case "text":
              if (string.IsNullOrWhiteSpace(b.Text)) return where + " held an empty text block";
              break;

            case LlmBlock.ImageKind:
              if (m.Role != LlmRole.User) return where + " held an image outside a user turn";
              if (string.IsNullOrEmpty(b.Base64)) return where + " held an image with nothing in it";
              if (string.IsNullOrEmpty(b.MediaType)) return where + " held an image with no media type";
              break;

            case "thinking":
              if (string.IsNullOrEmpty(b.Signature))
                return where + " held a thinking block with no signature";
              break;

            case LlmBlock.ResponseItemKind:
              if (string.IsNullOrWhiteSpace(b.RawJson))
                return where + " held a provider output item with nothing in it";
              break;

            case "tool_use":
              if (string.IsNullOrEmpty(b.ToolUseId)) return where + " held a tool call with no id";
              awaitingResults.Add(b.ToolUseId);
              break;

            case "tool_result":
              if (string.IsNullOrEmpty(b.ToolUseId)) return where + " held a tool result with no id";
              if (!awaitingResults.Remove(b.ToolUseId))
                return where + " answered a tool call that was never made (" + b.ToolUseId + ")";
              break;
          }
        }
      }

      // An unanswered call is only a defect at the END of the payload: mid-transcript it is answered by a
      // later message, and the loop appends the results itself before the next send.
      if (awaitingResults.Count > 0)
        return awaitingResults.Count + " tool call(s) were left unanswered at the end of the conversation";

      return null;
    }

    /// <summary>
    /// A REQUEST MUST END ON SOMETHING FOR THE MODEL TO ANSWER. A transcript ending on the model's own message is a prefill: Sonnet 5 refuses it with a
    /// 400, and OpenAI and Google quietly answer it, so the defect showed on one provider only. It is always ours: some door into a turn added nothing
    /// the model can read. So it is refused here on every provider, before a cent is spent, in our words (#297), and it is not repaired: appending a
    /// message would make the door look right and tell the model nothing true. The door is what gets fixed.
    ///
    /// The one legal case is <paramref name="continuingPausedReply"/>: Anthropic's <c>pause_turn</c>, whose documented answer is to send the paused
    /// assistant message back unchanged.
    /// </summary>
    public static string CheckEnding(IReadOnlyList<LlmMessage> messages, bool continuingPausedReply)
    {
      if (continuingPausedReply || messages == null || messages.Count == 0) return null;
      return messages[messages.Count - 1]?.Role == LlmRole.Assistant
        ? "the conversation ended on the model's own message, with nothing new for it to answer"
        : null;
    }

    /// <summary>
    /// Should an approved build turn that just ended get the #657 repair step? Only an approved BUILD (its acting tool is
    /// build_region, with approved regions to name), only when no build_region reached an approved region, and only once.
    /// </summary>
    public static bool ShouldRepairBuild(string actingTool, IReadOnlyList<string> approvedRegions,
                                         int approvedBuildDispatches, bool alreadyFired) =>
      string.Equals(actingTool, AgentToolCatalog.BuildRegion, StringComparison.OrdinalIgnoreCase)
      && Addresses(approvedRegions).Count > 0
      && approvedBuildDispatches == 0
      && !alreadyFired;

    /// <summary>Is this call build_region aimed at an approved region? Read from the call's own arguments.</summary>
    public static bool IsApprovedBuild(string toolName, string argsJson, IReadOnlyList<string> approvedRegions)
    {
      if (!string.Equals(toolName, AgentToolCatalog.BuildRegion, StringComparison.OrdinalIgnoreCase)) return false;
      try
      {
        var region = (System.Text.Json.Nodes.JsonNode.Parse(argsJson ?? "") as System.Text.Json.Nodes.JsonObject)?["region"]
          ?.GetValue<string>();
        return !string.IsNullOrEmpty(region) && Addresses(approvedRegions).Contains(region, StringComparer.Ordinal);
      }
      catch { return false; }
    }

    /// <summary>
    /// THE #657 REPAIR FACT (wording ruled at #666). A plugin fact in a user-role message, labelled as the plugin's, naming
    /// only the approved ADDRESSES - never a raw string that is not one.
    /// </summary>
    public static string BuildRepairFact(IReadOnlyList<string> approvedRegions) =>
      "PLUGIN FACT (from Dwight, not the user): nothing has been built - build_region was not called for " +
      string.Join(", ", Addresses(approvedRegions)) + ", which the user approved. Call it now, or say plainly why you cannot.";

    /// <summary>
    /// THE #657 BACKSTOP BANNER, or null. When an approved build turn still ends with no build_region dispatched at all,
    /// the panel renders this plugin line ABOVE the model's prose; the prose itself is never edited (#235).
    ///
    /// The sentence is #667's neutral form, verbatim: a plugin line above prose must be true WHATEVER the prose says - a
    /// fabricated "B1 rebuilt" and an honest "I cannot build B1" alike. And "nothing on the canvas changed" must be true
    /// whatever the TOOLS did, which is why the condition is NO build_region call at all (ApprovedToolCalls), not merely
    /// none for an approved region: an unaddressed build lands components. In that case the close reports what happened.
    /// Null with no prose - there is nothing to sit above, and the close already speaks. Addresses only, encoded.
    /// </summary>
    public static string NoBuildBanner(TurnCapability capability, TurnResult result, IReadOnlyList<string> approvedRegions)
    {
      if (capability != TurnCapability.Build || result == null) return null;
      if (result.ApprovedToolCalls > 0 || string.IsNullOrWhiteSpace(result.Text)) return null;
      var regions = Addresses(approvedRegions);
      if (regions.Count == 0) return null;

      return "<strong>No build ran in this turn:</strong> build_region was never called for " +
             System.Net.WebUtility.HtmlEncode(string.Join(", ", regions)) +
             ", so nothing on the canvas changed. Read the message below with that in mind.";
    }

    // ---- U32 (#924, #932): ANNOUNCED A CHANGE, ASKED FOR NOTHING - the line on both channels ----------------------

    /// <summary>
    /// The user's line, verbatim. TRUE IN EVERY STATE THAT CAN REACH IT, whatever the prose above it says - a fabricated
    /// "Submitted, and refused again" and an honest "raising the card now" alike: it is shown only on a turn that went through
    /// no <see cref="CardDoors"/>, with no approval just above it. Where there is one, <see cref="NothingFurtherAskedSentence"/> says it.
    /// </summary>
    public const string NothingAskedSentence =
      "Dwight did not receive a request this turn - nothing was submitted for your approval and no card was raised.";

    /// <summary>
    /// The model's line, the same fact in the transcript's words (#624: one line, both channels - a correction that never
    /// enters the transcript does not reach the model). It enters the next turn in the USER turn as Dwight's report, so the
    /// model reads it before it reads the user's next words, and is told what to do with it.
    /// </summary>
    public const string NothingAskedFact =
      "PLUGIN FACT (from Dwight, not the user): you made no request_mutation call in your last turn, so nothing was " +
      "submitted for the user's approval and no card was raised, whatever your last message said. If you still mean to " +
      "ask, call request_mutation; if your message described a request, a card or a result, tell the user plainly that " +
      "it did not happen.";

    /// <summary>
    /// Brief CL: the same fact on a turn with an approval just above it. "This turn" is not how a person reads it: the first real file drew "no card was raised"
    /// directly beneath the card that granted the turn. The fact is that nothing NEW was asked.
    /// </summary>
    public const string NothingFurtherAskedSentence =
      "Dwight received no further request after the approval above - nothing new was submitted for your approval and no new card was raised.";

    /// <summary>Brief CL: the model's line for the same state. The approval above did happen, so it is not told to deny a result.</summary>
    public const string NothingFurtherAskedFact =
      "PLUGIN FACT (from Dwight, not the user): you made no request_mutation call in your last turn, so nothing new was " +
      "submitted for the user's approval and no new card was raised, whatever your last message said. If you still mean to " +
      "ask, call request_mutation; if your message promised a further request or card, tell the user plainly that it has not been raised.";

    /// <summary>Brief CN: the fact the closing step reads, beside the last results, and the one thing it is asked.</summary>
    public const string ClosingStepFact =
      "PLUGIN FACT (from Dwight, not the user): you have used this turn's steps. Say, in a few sentences for the person: what you have read so far, what you were about to do, " +
      "and what you need from them, if anything. Do not call a tool.";

    /// <summary>Brief CN: only a turn out of steps, and only if its last reply was a call. One that ended on words already said them.</summary>
    public static bool EarnsClosingStep(TurnOutcome stop, int lastStepToolCalls) =>
      stop == TurnOutcome.Exhausted && lastStepToolCalls > 0;

    /// <summary>
    /// Brief CL: EVERY DOOR THAT MEANS A CARD IS BEHIND THIS TURN, as one set, so a new door cannot be left out of the decision. request_mutation raises a card;
    /// propose_scaffold and run_script each exist only on a turn an approved card granted. A test classifies every tool only an approval can reach, so a new one
    /// must be placed here or named as kept out: build_region stays out, because a build that ran and then promises another card has that line to read, in the
    /// words of <see cref="NothingFurtherAskedSentence"/>.
    /// </summary>
    public static readonly IReadOnlyList<string> CardDoors = new[] { AgentHostTools.RequestMutation, AgentToolCatalog.ProposeScaffold, FullControl.ToolName };

    public static bool IsCardDoor(string toolName) => CardDoors.Any(d => Is(d, toolName));

    /// <summary>Brief CL: a call that got through its door. A refusal did not: a refused map is not a map, and the model's words about it still need the line.</summary>
    public static bool WentThroughCardDoor(string toolName, string envelope)
    {
      if (!IsCardDoor(toolName) || string.IsNullOrWhiteSpace(envelope)) return false;
      try { return (System.Text.Json.Nodes.JsonNode.Parse(envelope) as System.Text.Json.Nodes.JsonObject)?["ok"]?.GetValue<bool>() == true; }
      catch { return false; }
    }

    /// <summary>
    /// Does this finished turn get the U32 line? When its text ANNOUNCED a card, a build or a submission - or quoted the
    /// result of one - and it made NO request_mutation call (<see cref="TurnShape.AnnouncedWithoutAsking"/>, the rule the
    /// counter reads too). A refused request ASKED, so it never gets the line: the refusal went to the model, and the
    /// close says why.
    ///
    /// Not on a Stop: the user stopped it, and the stop line already says what happened. On an approved BUILD turn only the
    /// request family counts - a build announced and not run there is the no-build banner's sentence, not this one.
    /// </summary>
    public static bool AnnouncedNothingAsked(TurnCapability capability, TurnResult result)
    {
      if (result == null || result.Outcome == TurnOutcome.Stopped) return false;
      if (result.ClosingStep) return false;   // Brief CN: it was asked what it was about to do, so saying so announces nothing, and the note says what happened
      if (result.RequestCalls > 0 || result.AskedForConsent || result.CardDoorCalls > 0) return false;
      if (TurnShape.AnnouncedWithoutAsking(result.Text, result.RequestCalls, result.ActingCalls,
                                           buildFamily: capability != TurnCapability.Build)) return true;

      // U97: the structural catch. Every chat turn can reach a card (a read-only turn holds request_mutation, the others hold more),
      // so mutation is available on all of them; an approved BUILD turn is left to its own banner, as the family above is.
      return capability != TurnCapability.Build && result.Outcome == TurnOutcome.Complete &&
             TurnShape.AnnouncedIntentWithoutCard(result.Text, mutationAvailable: true, result.LastStepToolCalls, result.RequestCalls);
    }

    /// <summary>A refusal's wire envelope, as a tool result. Here rather than in <c>AgentLoop</c> so a dispatcher that is not GH-bound speaks the same bytes (Brief BY).</summary>
    public static string Refusal(string code, string message) =>
      new System.Text.Json.Nodes.JsonObject
      {
        ["ok"] = false,
        ["error"] = new System.Text.Json.Nodes.JsonObject { ["code"] = code, ["message"] = message },
      }.ToJsonString();

    /// <summary>#220: the answer to a tool this turn does not hold, whether it exists or the model made it up.</summary>
    public static string GrantRefusal(string toolName) =>
      Refusal(ToolErrors.PreconditionRefused,
        "\"" + toolName + "\" is not available on this turn. Reads are always available; " +
        "changing the canvas needs the user to approve it first - ask with " +
        AgentHostTools.RequestMutation + ".");

    /// <summary>Brief BT: did this finished turn announce a change precisely enough to force the card? Not on an approved BUILD turn,
    /// which is left to its own banner and repair as the line's catch is, and only on a turn that completed.</summary>
    public static bool AnnouncedForForcing(TurnCapability capability, TurnResult result) =>
      result != null && capability != TurnCapability.Build && result.Outcome == TurnOutcome.Complete && !result.AskedForConsent &&
      TurnShape.AnnouncedForForcing(result.LastStepText, result.LastStepToolCalls, result.RequestCalls);

    /// <summary>Brief BT: a follow-up that ended with no card, for any reason but the user's Stop, which says what happened on its own.</summary>
    public static bool FollowUpRaisedNoCard(bool isAnnouncementFollowUp, TurnResult result) =>
      isAnnouncementFollowUp && result != null && !result.AskedForConsent && result.Outcome != TurnOutcome.Stopped;

    /// <summary>
    /// Brief BT: a forced request refused and then refused again ends the turn: the model has had the real refusal once. The existing
    /// bound (#973) would let a third call through when the two refusals differ, and a forced turn has no way to go and read what the
    /// refusal asked for, so another try could only guess.
    /// </summary>
    public static bool ForcedRefusalEndsTurn(bool forced, int readRefusals, int stepRefusals) =>
      forced && stepRefusals > 0 && readRefusals >= 1;

    /// <summary>Brief BT: the tool to force on this step, or null. Only one the turn offers, and only where the connection can send it.</summary>
    public static string ForcedTool(string wanted, IEnumerable<LlmTool> tools, bool providerForces) =>
      !string.IsNullOrEmpty(wanted) && providerForces && tools != null && tools.Any(t => string.Equals(t.Name, wanted, StringComparison.Ordinal))
        ? wanted : null;

    /// <summary>
    /// AN ORPHAN DOES NOT SPEAK FOR A NEWER TURN (the known item recorded with #254). A stopped turn's completion arrives when its reader next hears from the provider, which can be after the
    /// person has sent again on the same page. Its status line and its composer are then the newer turn's, busy, and freeing them would let a second message be typed into a running turn. So
    /// the orphan frees them only when no run is live on its page: none at all, or one on another page, which leaves this one idle. The Stop itself already freed them, in the chat's report.
    /// </summary>
    public static bool OrphanFreesComposer(object page, object runningPage) => page == null || !ReferenceEquals(page, runningPage);

    /// <summary>Brief BT: the "Dwight turn:" line, with what a turn's record lacked at the lamp's gate. No payload in it.</summary>
    public static string TurnEndLine(string threadId, TurnResult result) =>
      "completion on the LIVE generation (outcome " + result.Outcome + ") thread=" + (threadId ?? "") +
      " stop=" + (string.IsNullOrEmpty(result.LastStopReason) ? "none" : result.LastStopReason) +
      " request_mutation offered=" + (result.RequestOffered ? "yes" : "no") + " forced=" + (result.Forced ? "yes" : "no") +
      (result.ClosingStep ? " closing=yes" : "");

    /// <summary>The line for the screen, or null. Plugin-authored HTML, no model text in it (#235).</summary>
    public static string NothingAskedHtml(TurnCapability capability, TurnResult result, bool followsApproval = false)
    {
      if (!AnnouncedNothingAsked(capability, result)) return null;
      return AfterApproval(capability, followsApproval)
        ? "<p><strong>No further request reached Dwight.</strong> " + System.Net.WebUtility.HtmlEncode(NothingFurtherAskedSentence) + "</p>"
        : "<p><strong>No request reached Dwight.</strong> " + System.Net.WebUtility.HtmlEncode(NothingAskedSentence) + "</p>";
    }

    /// <summary>The model's half of the same decision and the same wording. Written through the report door, beside
    /// <see cref="NothingAskedHtml"/>, so the two channels cannot disagree on whether it was shown.</summary>
    public static string NothingAskedReport(TurnCapability capability, TurnResult result, bool followsApproval = false) =>
      !AnnouncedNothingAsked(capability, result) ? null
      : AfterApproval(capability, followsApproval) ? NothingFurtherAskedFact : NothingAskedFact;

    /// <summary>The user's sentence for this turn, for a caller that records it (Brief BY's harness).</summary>
    public static string NothingAskedSentenceFor(TurnCapability capability, bool followsApproval) =>
      AfterApproval(capability, followsApproval) ? NothingFurtherAskedSentence : NothingAskedSentence;

    /// <summary>Brief CL: every capability but ReadOnly exists only on an approved card, and a read-only turn may be the follow-up to one (<see cref="ChatToTranscript.FollowsApproval"/>).</summary>
    private static bool AfterApproval(TurnCapability capability, bool followsApproval) =>
      capability != TurnCapability.ReadOnly || followsApproval;

    /// <summary>
    /// Does the panel show "Dwight had nothing to say to that." for a turn with no note? (#256, #672.)
    ///
    /// Only when the turn wrote no text AND nothing else on screen speaks for it. A consent card speaks (#256). An approved
    /// build that DISPATCHED speaks too: its running lines and the close say what happened. #666 clears the pre-repair prose,
    /// so a repaired turn that builds and then writes nothing is the expected successful repair, and the filler above a
    /// verified build would be false (#672). A build turn that dispatched nothing still gets it, because there it is true.
    /// </summary>
    public static bool ShowEmptyReplyFiller(TurnCapability capability, TurnResult result)
    {
      if (result == null || !string.IsNullOrWhiteSpace(result.Text)) return false;
      if (result.AskedForConsent) return false;
      if (capability == TurnCapability.Build && result.ApprovedBuildDispatches > 0) return false;
      return true;
    }

    private static List<string> Addresses(IReadOnlyList<string> regions) =>
      (regions ?? new string[0]).Where(ScaffoldAddress.IsRegionAddress).Distinct(StringComparer.Ordinal).ToList();

    public static TurnOutcome? PostResponse(string stopReason, bool hasToolUse)
    {
      // Truncation is checked FIRST and is its own outcome: a reply cut off mid-answer must never be
      // presented as a finished one, and no token count reveals which happened (#216d).
      if (string.Equals(stopReason, "max_tokens", StringComparison.Ordinal)) return TurnOutcome.Truncated;
      // Brief BZ: before any tool: a call in a refused reply is part of what is discarded, and must not run.
      if (string.Equals(stopReason, LlmRefusal.StopReason, StringComparison.Ordinal)) return TurnOutcome.Refused;
      if (!hasToolUse) return TurnOutcome.Complete;
      return null;
    }

    /// <summary>The real cause and the real fix when Ollama's default window is filled by the prompt alone.</summary>
    public const string OllamaContextNote =
      "Ollama's context window is too small for the chat: the prompt alone filled it. " +
      "Start Ollama with a larger window (set OLLAMA_CONTEXT_LENGTH=32768 before starting it) and try again.";

    /// <summary>A thinking model that used the whole reply thinking: the shorter-question advice would be false.</summary>
    public const string ThoughtWithoutAnswerNote =
      "The model spent its whole reply thinking and wrote no answer. Try the question again; if it repeats, " +
      "pick a model without thinking under Dwight > Settings > Model.";

    /// <summary>The honest sentence for an outcome, in the user's language. Null when Complete: nothing
    /// happened that the user needs told.</summary>
    public static string NoteFor(TurnOutcome outcome, TurnLimits limits, bool hasText, bool contextWindowFilled = false,
                                   bool thoughtWithoutAnswer = false)
    {
      var kept = hasText ? " Here is what it had so far." : "";
      switch (outcome)
      {
        case TurnOutcome.Exhausted:
          return "Dwight used this turn's budget of " + (limits?.MaxSteps ?? 0) + " steps" + (hasText
            ? ". Above is where it got to; ask again to carry on from there."
            : " without finishing. Ask again to carry on.");
        case TurnOutcome.TimedOut:
          return "This turn ran past its time limit and Dwight stopped it." + kept + " Ask again to carry on.";
        case TurnOutcome.Truncated:
          if (contextWindowFilled) return OllamaContextNote;
          if (thoughtWithoutAnswer && !hasText) return ThoughtWithoutAnswerNote;
          return "The reply was cut off at the token limit before it finished." + kept +
                 " A shorter question, or a smaller part of the definition, will fit.";
        case TurnOutcome.Stopped:
          return "You stopped this." + kept;
        case TurnOutcome.Failed:
          return "Dwight could not finish this turn." + kept;
        case TurnOutcome.Refused:
          return LlmRefusal.Sentence(null);
        default:
          return null;
      }
    }

    /// <summary>
    /// How many refused consent requests the model may READ in a turn before a further one ends it (#611a, refined
    /// by #973: READ, not arrived - see <see cref="RefusalEndsTurn"/>). The first refusal is READ - it carries the way
    /// forward, and a turn that ended on it would never act on it. A repeat of one already read means the way forward
    /// did not take, and another ask would only spend the user's money on a loop.
    /// </summary>
    public const int MaxRefusedRequests = 2;

    /// <summary>
    /// THE BOUND COUNTS REFUSALS THE MODEL HAS READ (#973, seen three times in one sitting). The bound used to end the
    /// turn the moment the second refusal ARRIVED - in the same step, before the model had a step in which to read it.
    /// Three times the second refusal carried the diagnostic that would have fixed the call (the compiler's words; the
    /// state-acceptance sentence; the port list), and nobody saw it: the model got no next turn and the user got a
    /// generic line. A refusal whose text the model has not yet received must not be the one that ends the turn.
    ///
    /// So a step's refusals end the turn only when the model has ALREADY READ the same refusal (it repeated a call the
    /// diagnostic did not change - the loop the bound exists to stop), or has already read
    /// <see cref="MaxRefusedRequests"/> refusals and produced yet another. Otherwise they are sent back, and become
    /// read. Identity is the refusal's code and message: a different message is a different diagnostic and earns its
    /// own reading; the cap keeps the asking finite.
    /// </summary>
    public static bool RefusalEndsTurn(ICollection<string> readIdentities, int readCount, IEnumerable<string> thisStepIdentities)
    {
      var step = (thisStepIdentities ?? Enumerable.Empty<string>()).ToList();
      if (step.Count == 0) return false;
      if (readCount >= MaxRefusedRequests) return true;
      return readIdentities != null && step.Any(readIdentities.Contains);
    }

    /// <summary>What makes one refusal the same as another: its code and its message, as the envelope carries them.</summary>
    public static string RefusalIdentity(string refusalEnvelope)
    {
      try
      {
        var error = System.Text.Json.Nodes.JsonNode.Parse(refusalEnvelope ?? "")?["error"];
        return (error?["code"]?.GetValue<string>() ?? "") + "|" + (error?["message"]?.GetValue<string>() ?? "");
      }
      catch { return "|" + (refusalEnvelope ?? ""); }
    }

    /// <summary>
    /// What the USER is told when a turn ends on its second refused request (#615).
    ///
    /// TWO VOICES (#235). The refusal itself is written TO THE MODEL - "say plainly...", "call
    /// build_region now" - and rendering it would hand the user instructions addressed to someone else. So
    /// this sentence is the plugin's own, chosen from the refusal's CLASS: its code, and the keys the plugin
    /// attaches. Nothing is copied out of the refusal message, and the kind is named only when it is one of the
    /// plugin's own kinds. The one word of the model's that may reach it is a component's name, which the plugin
    /// attached as data (<see cref="AskedName"/>).
    ///
    /// BRIEF CU: EVERY CODE A REQUEST CAN COME BACK WITH HAS ITS OWN SENTENCE. Haiku 4.5 stopped on two real files on
    /// the generic line at the end of this switch, and neither the person nor the transcript could say why, though the
    /// plugin knew. That line is now for a code the list does not know, and a test walks the closed set and the
    /// request path's own source, so a code added later without a sentence turns it red.
    /// </summary>
    public static string RefusedRequestNote(string requestArgsJson, string refusalEnvelope)
    {
      var kind = KnownKind(requestArgsJson);
      string code = null, reason = null;
      System.Text.Json.Nodes.JsonNode detail = null;
      try
      {
        var error = System.Text.Json.Nodes.JsonNode.Parse(refusalEnvelope ?? "")?["error"];
        code = error?["code"]?.GetValue<string>();
        detail = error?["detail"];
        reason = detail?["reason"]?.GetValue<string>();
      }
      catch { }

      var approving = "Dwight stopped: it asked twice to approve " + Noun(kind, "a change");
      switch (code)
      {
        case ToolErrors.RegionNotApproved:
          return "Dwight stopped: it asked twice to approve regions outside the build you approved.";

        case ToolErrors.AlreadyApproved:
          return approving + " that this turn was already carrying out.";

        case ToolErrors.UnknownRegion:
          return "Dwight stopped: it asked twice to approve a build of regions that are not on the canvas.";

        case ToolErrors.PreconditionRefused:
          return reason == AgentHostTools.RepeatUnchangedReason
            ? approving + " that had already run without changing anything."
            : approving + ", and the last request " + (PreconditionReason(detail, reason) ?? "did not pass the checks Dwight runs before it shows you a card") + ".";

        // #907/#908 (Tharun's U20 sitting): a request refused as MALFORMED - most often a field the schema
        // demands for its kind that never arrived - used to fall all the way to the fully generic sentence
        // below, which told the user nothing beyond "it stopped". Same gap as U20's own row 3c, one layer up:
        // the refusal that names what was missing goes to the MODEL (it can correct the next call), and the
        // turn can still end with no card and no report of that to the user. This names the CLASS of what
        // happened, never the refusal's own text - nothing written for the model reaches here (#235).
        case ToolErrors.InvalidArgs:
          // #973: NAME THE LAST REFUSAL'S REASON where the plugin can say it in its own words. The class is read off the
          // refusal's STRUCTURE (the keys the plugin itself attaches), never its message, so nothing written for the
          // model reaches the user (#235); an unclassified one keeps the generic sentence.
          var because = InvalidArgsReason(detail, reason);
          return approving + (because != null
            ? ", and the last request " + because + "."
            : ", and each time left out something Dwight needed before it could put the question to you.");

        case ToolErrors.UnknownComponentType:
          return approving + ", and the last request named a component that is not installed in this Grasshopper" + AskedName(detail) + ".";

        case ToolErrors.UnknownId:
          return approving + ", and the last request named a component that is not on the canvas.";

        case ToolErrors.RetiredId:
          return approving + ", and the last request named a component that is no longer on the canvas.";

        case ToolErrors.RegionOccupied:
          return "Dwight stopped: it asked twice to build into a region that already holds work it would have had to remove.";

        case ToolErrors.RegionAttemptsExhausted:
          return "Dwight stopped: it asked twice to build a region that had already used its attempts this turn.";

        // One sentence for both. It names no remedy: reading again is the model's move, not the person's.
        case ToolErrors.StaleDocument:
        case ToolErrors.StalePartition:
          return approving + " while the canvas had changed under it.";

        case ToolErrors.Busy:
          return approving + " while another Dwight operation held the session.";

        case ToolErrors.NoDocument:
          return approving + " with no document open.";

        case ToolErrors.SolverDisabled:
          return approving + " with the solver off.";

        default:
          return "Dwight stopped: it asked twice for your approval of " + Noun(kind, "a change") +
                 ", and neither request could be put to you.";
      }
    }

    /// <summary>Brief CU: "available" alone says only that a refusal has a list to offer. It is also the kinds Dwight has, a slider's roundings and the ports of a wire that named none, and calling
    /// those a connection point was false. So each sentence reads the marker its own refusal carries.</summary>
    private static string InvalidArgsReason(System.Text.Json.Nodes.JsonNode detail, string reason)
    {
      try
      {
        if (detail?["typesThatAcceptState"] != null) return "asked to give a component a value it cannot hold";
        if (detail?["mostScripts"] != null) return "held more scripts than one card can show";
        if (reason == BuildSpecParser.NoSuchPortReason) return "named a connection point that is not on that component";
        if (reason == AgentHostTools.UnknownKindReason) return "asked for something Dwight cannot offer";
      }
      catch { }
      return null;
    }

    /// <summary>Brief CU: the refusals of this class the person could do something about, named from the keys the plugin attaches. Everything else in the class is true of the sentence the caller falls back to.</summary>
    private static string PreconditionReason(System.Text.Json.Nodes.JsonNode detail, string reason)
    {
      try
      {
        if (reason == BuildSpecParser.AmbiguousTypeReason) return "used a name that more than one installed component shares" + AskedName(detail);
        if (detail?["mostThatCanBeNamed"] != null) return "placed more components than one card can name";
        if (detail?["most"] != null) return "held more changes than one card can carry";
      }
      catch { }
      return null;
    }

    /// <summary>The longest name <see cref="AskedName"/> carries. A type name is short; a model's argument need not be.</summary>
    private const int MaxAskedName = 80;

    /// <summary>
    /// Brief CU: THE NAME THE REQUEST ASKED FOR, as ": "Foo"", or nothing. The plugin wrote the key from the model's argument, and it is the one
    /// fact that lets the person judge: a plug-in they do not have, or a name the model invented. <c>nearestInstalled</c> stays out, because that
    /// list is for the model. On one line, since a line break in a note starts its quieter part (<see cref="Dwight.Llm.ProviderLimit.NoteHtml"/>),
    /// and short. It is escaped where it is rendered, because escaping is a property of the destination (#335).
    /// </summary>
    private static string AskedName(System.Text.Json.Nodes.JsonNode detail)
    {
      string asked = null;
      try { asked = detail?["asked"]?.GetValue<string>(); }
      catch { }
      var name = BuildOutcomeFact.Bounded(asked, MaxAskedName);
      return name.Length == 0 ? "" : ": \"" + name + "\"";
    }

    private static string KnownKind(string argsJson)
    {
      try
      {
        var asked = System.Text.Json.Nodes.JsonNode.Parse(argsJson ?? "")?["kind"]?.GetValue<string>();
        return AgentHostTools.Kinds.FirstOrDefault(k => string.Equals(k, asked, StringComparison.OrdinalIgnoreCase));
      }
      catch { return null; }
    }

    private static string Noun(string kind, string fallback)
    {
      switch (kind)
      {
        case "solve": return "a re-run";
        case "build": return "a build";
        case "scaffold": return "a scaffold";
        case "reorganise": return "a Reorganise";
        case "simplify": return "a Simplify";
        default: return fallback;
      }
    }

    /// <summary>
    /// One command-line line per tool dispatch, with what came back (#610's discriminator). "Did the build
    /// turn call request_mutation or no tool at all, and was it refused?" was unanswerable after the gate
    /// because nothing recorded it. Names and flags only - never an argument or a message.
    /// </summary>
    public static string DispatchLogLine(string toolName, string envelope)
    {
      bool? ok = null, card = null;
      string code = null;
      try
      {
        var root = System.Text.Json.Nodes.JsonNode.Parse(envelope ?? "");
        ok = root?["ok"]?.GetValue<bool>();
        code = root?["error"]?["code"]?.GetValue<string>();
        card = root?["result"]?["cardShown"]?.GetValue<bool>();
      }
      catch { }

      var line = "tool " + (toolName ?? "(none)") + " ok=" + (ok.HasValue ? (ok.Value ? "true" : "false") : "unreadable");
      if (code != null) line += " code=" + code;
      if (AgentHostTools.IsRequest(toolName)) line += " cardShown=" + (card == true ? "true" : "false");
      return line;
    }

    /// <summary>U71: the line a provider-side search leaves in Rhino's command history, or null when it ran none. The client
    /// search tool leaves the same <see cref="DispatchLogLine"/> the host tools do; the provider's own search is not a tool call
    /// Dwight dispatches, so this is the only trace it has. Counts only - never a query or a result.</summary>
    public static string SearchLogLine(int providerSideSearches) =>
      providerSideSearches > 0 ? "search (provider-side) n=" + providerSideSearches : null;

    /// <summary>The sentence the panel shows for a turn that ended with no text and nothing else on screen (U70). A turn that
    /// read a tool result and then said nothing is a different fact from one that was never answered, so it says which.</summary>
    public static string EmptyReplyLine(TurnResult result) =>
      result != null && result.ToolCalls > 0
        ? "The model returned no text after reading the tool result."
        : "Dwight had nothing to say to that.";

    /// <summary>
    /// The turn's meta line - what it cost and anything that was quietly adjusted. The elision count and
    /// the effort step-down both ride here: each is a fact the user paid for and would otherwise never
    /// see. A witness captured and not consulted is not a witness (the WP-2 lesson, one layer up).
    /// </summary>
    public static string Meta(TurnResult result, string costLine)
    {
      if (result == null) return costLine ?? "";
      var parts = new List<string>();
      if (!string.IsNullOrWhiteSpace(costLine)) parts.Add(costLine);
      parts.Add(result.Steps + (result.Steps == 1 ? " step" : " steps"));
      if (result.ToolCalls > 0) parts.Add(result.ToolCalls + (result.ToolCalls == 1 ? " tool call" : " tool calls"));
      if (result.ElidedToolResults > 0)
        parts.Add(result.ElidedToolResults + " earlier result(s) trimmed from the payload");
      if (!string.IsNullOrWhiteSpace(result.PlanNote)) parts.Add(result.PlanNote);
      return string.Join(" · ", parts);
    }
  }
  /// <summary>
  /// Brief CN: ONE TOOL CALL AS A LINE: its name, its arguments and the shape of its result. Counts, ids and names, never a payload, read from the call and its envelope and never from
  /// the model's words. There was no summariser to reuse: the transcript's elision replaces a result with a fixed marker.
  /// </summary>
  internal static class TurnReadLine
  {
    public const int MaxArgsChars = 80;
    public const int MaxFields = 6;

    private static readonly HashSet<string> NameKeys = new HashSet<string>(StringComparer.Ordinal) { "name", "type", "nick", "qualified", "address", "label", "region" };
    private static readonly HashSet<string> NotCounts = new HashSet<string>(StringComparer.Ordinal) { "value", "min", "max", "decimals", "rounding", "x", "y", "w", "h" };
    private static readonly HashSet<string> Words = new HashSet<string>(StringComparer.Ordinal) { "found", "ambiguous", "cardShown" };

    public static string Of(string tool, string argsJson, string envelope)
    {
      var args = Args(argsJson);
      return (tool ?? "(none)") + (args == null ? "" : " " + args) + " -> " + Shape(envelope);
    }

    private static string Args(string json)
    {
      if (string.IsNullOrWhiteSpace(json)) return null;
      string compact;
      try { compact = System.Text.Json.Nodes.JsonNode.Parse(json)?.ToJsonString(); }
      catch { compact = json.Trim(); }
      if (string.IsNullOrEmpty(compact) || compact == "{}") return null;
      return compact.Length <= MaxArgsChars ? compact : compact.Substring(0, MaxArgsChars) + "...";
    }

    public static string Shape(string envelope)
    {
      System.Text.Json.Nodes.JsonObject root;
      try { root = System.Text.Json.Nodes.JsonNode.Parse(envelope ?? "") as System.Text.Json.Nodes.JsonObject; }
      catch { return "unreadable"; }
      if (root == null) return "unreadable";

      bool ok;
      try { ok = root["ok"]?.GetValue<bool>() == true; } catch { ok = false; }
      if (!ok)
      {
        string code = null;
        try { code = root["error"]?["code"]?.GetValue<string>(); } catch { }
        return "refused" + (string.IsNullOrEmpty(code) ? "" : ": " + code);
      }

      var result = root["result"] as System.Text.Json.Nodes.JsonObject;
      if (result == null) return "done";

      var fields = new List<string>();
      var seen = new HashSet<string>(StringComparer.Ordinal);
      void Add(string key, string value) { if (seen.Add(key)) fields.Add(key + " " + value); }

      foreach (var kv in result)
      {
        switch (kv.Value)
        {
          case System.Text.Json.Nodes.JsonArray list:
            Add(kv.Key, list.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            break;
          case System.Text.Json.Nodes.JsonObject inner when kv.Key == "counts":
            foreach (var c in inner)
              if (c.Value is System.Text.Json.Nodes.JsonValue cv && cv.TryGetValue<int>(out var count))
                Add(c.Key, count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            break;
          case System.Text.Json.Nodes.JsonValue value:
            if (value.TryGetValue<bool>(out var flag)) { if (Words.Contains(kv.Key)) Add(kv.Key, flag ? "yes" : "no"); }
            else if (value.TryGetValue<int>(out var n)) { if (!NotCounts.Contains(kv.Key)) Add(kv.Key, n.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
            else if (NameKeys.Contains(kv.Key) && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) && text.Length <= 60) Add(kv.Key, text.Trim());
            break;
        }
      }
      if (fields.Count == 0) return "done";
      var more = fields.Count - MaxFields;
      return string.Join(", ", fields.Take(MaxFields)) + (more > 0 ? " and " + more + " more" : "");
    }
  }

  /// <summary>
  /// Brief CN: THE NEXT TURN STARTS WHERE THIS ONE STOPPED. A turn's tool results do not cross into the next, so "carry on" read the same things three times over and could not progress.
  /// So the plugin states what the turns that ran out of steps read, until a turn completes. Consecutive ones add up: a third turn told only of the second would read the first's again.
  /// Bounded, and it says what it left out (ADR-035). In memory: a restart loses it, and the turn reads again, which is only what it did before this.
  /// </summary>
  internal sealed class ExhaustedReads
  {
    public const int MaxLines = 60;
    public const int MaxChars = 3000;

    /// <summary>How much of <see cref="MaxChars"/> may go on results' shapes. What is left names the older calls.</summary>
    public const int ShapedChars = 2000;

    private readonly Dictionary<string, List<string>> _byThread = new Dictionary<string, List<string>>(StringComparer.Ordinal);
    private readonly object _gate = new object();

    /// <summary>A turn has ended. Out of steps, its calls are kept; complete, the record is done with. Any other ending says nothing about what was read, so it changes nothing.</summary>
    public void Note(string threadId, TurnResult result)
    {
      if (result == null) return;
      lock (_gate)
      {
        var key = threadId ?? "";
        if (result.Outcome == TurnOutcome.Complete) { _byThread.Remove(key); return; }
        if (result.Outcome != TurnOutcome.Exhausted) return;
        if (!_byThread.TryGetValue(key, out var lines)) _byThread[key] = lines = new List<string>();
        lines.AddRange(result.ToolTrail ?? new List<string>());
      }
    }

    /// <summary>The fact for the next turn's start, or null.</summary>
    public string FactFor(string threadId)
    {
      List<string> lines;
      lock (_gate)
      {
        if (!_byThread.TryGetValue(threadId ?? "", out var held) || held.Count == 0) return null;
        lines = held.ToList();
      }

      // The newest carry their shape, being nearest to where it stopped, and older ones are named without it. Keeping only the newest was wrong: on a live row of 43 calls it cut the 14
      // oldest, the overview among them, and what a fact does not list is what gets read again. That a call was made is the thing it must not lose.
      var kept = new List<string>();
      var chars = 0;
      for (var i = lines.Count - 1; i >= 0 && kept.Count < MaxLines; i--)
      {
        var line = lines[i];
        if (chars + line.Length > ShapedChars)
        {
          var shape = line.IndexOf(" -> ", StringComparison.Ordinal);
          if (shape > 0) line = line.Substring(0, shape);
        }
        if (chars + line.Length > MaxChars) break;
        kept.Insert(0, line);
        chars += line.Length;
      }
      var omitted = lines.Count - kept.Count;

      return "PLUGIN FACT (from Dwight, not the user): your last turn ran out of its steps, and its tool results are not carried into this one. You read: " +
             string.Join("; ", kept) + "." +
             (omitted > 0 ? " " + omitted + " earlier call(s) are not listed." : "") +
             " Those results are not repeated here. Call a tool again only for what you did not read or need the detail of; do not start again from the top.";
    }
  }
}
