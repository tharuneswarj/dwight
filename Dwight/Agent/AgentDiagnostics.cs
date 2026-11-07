using System;
using System.Linq;
using System.Reflection;
using System.Text;
using Dwight.Llm;
using Dwight.Models;

namespace Dwight.Agent
{
  /// <summary>
  /// DEBUG-only read-only diagnostics for the Phase-8 WP-2 runtime gate. Nothing here mutates anything;
  /// each entry answers one question the gate has to answer with evidence rather than assertion.
  /// </summary>
  internal static class AgentDiagnostics
  {
    /// <summary>
    /// #199 - the ADR-082 thinking promise, in BOTH directions: what Dwight asked for (the exact
    /// outbound body) and what the model actually did (billed reasoning tokens). Either alone proves
    /// the wrong thing.
    /// </summary>
    public static void ShowLastExchange() => Show("Last LLM request", LlmTrace.LastExchangeReport());

    /// <summary>
    /// Lane B (#873) - the REPLY STREAM CAPTURE, as a WINDOW. Appended to this shared file; nothing above it
    /// changes.
    ///
    /// <para>The new stream reader will REFUSE an event that does not parse, where today every reader skips it
    /// in silence. That is only safe once we know what each provider legitimately sends - Google's reader has
    /// no check for a non-JSON sentinel. The counter names what failed to parse, with samples; the files are
    /// the real streams the reader's tests are written against (CURRENT: test a reader against the writer's
    /// REAL output). The key never leaves the plugin: a stream is a RESPONSE body; the key rides a header.</para>
    ///
    /// <para>WHY A WINDOW (the #873 review): the files hold reply TEXT and become committed fixtures. A buffer
    /// kept for the whole session would write a client definition's replies from earlier in the day. So
    /// nothing is recorded until "Start" - which clears everything before it - and "Write" refuses without a
    /// window, or from a different document than the one the window was opened on.</para>
    /// </summary>
    public static void StartStreamCapture()
    {
      var key = ActiveDocumentKey();
      if (key == null)
      {
        Show("Start stream capture", "REFUSED - no Grasshopper document is open. Open a new, blank definition " +
                                     "first: the capture is only for a scratch document.");
        return;
      }

      StreamCapture.Open(key);

      var sb = new StringBuilder();
      sb.AppendLine("CAPTURE WINDOW OPEN on this document. Everything recorded before now has been cleared.");
      sb.AppendLine();
      sb.AppendLine("THIS MUST BE A BLANK SCRATCH DEFINITION WITH A MADE-UP PROMPT: the replies you now run are");
      sb.AppendLine("written to files that become committed test fixtures. Do not open a client file until you have");
      sb.AppendLine("clicked \"Write stream capture\" - replies are not tagged with their document, so a switch to");
      sb.AppendLine("another file and back inside one window would not be caught.");
      sb.AppendLine();
      sb.AppendLine("Now: on each provider you have a key for, run Explain > Short on something trivial; on each provider that");
      sb.AppendLine("runs the chat, also one Build turn that reads the canvas (\"What is on the canvas?\") so a stream with a tool");
      sb.AppendLine("call in it is captured. Then Dwight > Dev > \"Write stream capture\".");

      Show("Start stream capture", sb.ToString());
    }

    /// <summary>Write the open window's streams to files, report the counter, and close the window.</summary>
    public static void WriteStreamCapture()
    {
      var dir = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Dwight", "stream-captures");

      // The counter is read BEFORE the write, which closes the window and forgets it - and it is only SHOWN if
      // the write went ahead, because its samples are reply content too, and a refused write must not print
      // what it refused to keep.
      var counter = StreamCapture.CounterReport();
      string written;
      try { written = StreamCapture.WriteAll(dir, ActiveDocumentKey()); }
      catch (Exception ex) { written = "COULD NOT WRITE to " + dir + " - " + ex.GetType().Name + ": " + ex.Message; }

      if (written.StartsWith("REFUSED", StringComparison.Ordinal)) { Show("Write stream capture", written); return; }

      var sb = new StringBuilder();
      sb.AppendLine("THE COUNTER - data payloads read in the window, and how many failed to parse. The new reader");
      sb.AppendLine("will REFUSE those, so a non-zero here whose sample is NOT damage must be named before it ships:");
      sb.AppendLine(counter);
      sb.AppendLine("FILES WRITTEN (the window is now closed):");
      sb.AppendLine(written);
      Show("Write stream capture", sb.ToString());
    }

    /// <summary>The active document's identity - the file-borne DocumentID (ADR-067), which survives Save-As
    /// and reopen - or null when no document is open.</summary>
    private static string ActiveDocumentKey()
    {
      try
      {
        var doc = Grasshopper.Instances.ActiveCanvas?.Document;
        return doc == null ? null : doc.DocumentID.ToString("N");
      }
      catch { return null; }
    }

    /// <summary>
    /// #200 - the raw ids each provider's list endpoint returned on the last "Check models". A red
    /// catalogue entry means its id is not in this list; the CORRECTED id has to come from here, from
    /// the same instrument that produced the red, rather than from a website or a transcription
    /// (ADR-083's own rule). Public model names only - a key never appears.
    /// </summary>
    public static void ShowLiveModelIds()
    {
      var sb = new StringBuilder();
      sb.AppendLine("Ids returned by each provider's list endpoint on the last \"Check models\".");
      sb.AppendLine("A catalogue entry marked red is not in its provider's list below.");
      sb.AppendLine();

      var any = false;
      foreach (var provider in DwightConfig.Providers)
      {
        var ids = ModelReconciler.LastListedIds(provider);
        if (ids == null || ids.Count == 0) continue;
        any = true;
        sb.AppendLine("== " + DwightConfig.ProviderLabel(provider) + " (" + ids.Count + ") ==");
        foreach (var id in ids) sb.AppendLine("  " + id);
        sb.AppendLine();
      }

      if (!any)
        sb.AppendLine("(Nothing recorded yet - run Settings > Connection > \"Check models\" first. " +
                      "A provider with no key stored records nothing rather than an empty list, so " +
                      "\"not checked\" can never read as \"offers no models\".)");

      Show("Live model ids", sb.ToString());
    }

    /// <summary>#196 - the #131 probe extended to DATA: which catalogue is actually in effect. A gate
    /// that verifies fresh code against a months-old catalogue measures the wrong thing.</summary>
    public static void ShowCatalogueVersion()
    {
      var sb = new StringBuilder();
      sb.AppendLine("Loaded catalogueVersion: " + ModelCatalogue.LoadedCatalogueVersion);
      sb.AppendLine("User copy:               " + ModelCatalogue.UserPath);
      sb.AppendLine();
      sb.AppendLine("Models in effect (" + ModelCatalogue.All.Count + "):");
      foreach (var m in ModelCatalogue.All.OrderBy(m => m.Provider).ThenBy(m => m.Tier).ThenBy(m => m.Id))
        sb.AppendLine("  [" + m.Provider + "/" + m.Tier + "] " + m.Id +
                      "  thinking=" + m.ThinkingDefault +
                      (m.Agentic ? " agentic" : "") +
                      "  pref=" + m.Preference);
      Show("Loaded catalogue", sb.ToString());
    }

    /// <summary>
    /// THE #131 LOADED-ASSEMBLY PROBE, as a thing you can CLICK (#247 probe 1).
    ///
    /// #131 made this standing practice - before any runtime gate, probe the loaded assembly for a symbol
    /// NEW TO THAT BUILD, so a stale .gha stops the run before a single solve or token. It was written as
    /// an instruction and never given a surface, so a checklist could name a menu entry that did not
    /// exist, and freshness ended up established by accident instead.
    ///
    /// It NAMES the symbol it checked, because "probe passed" is not a reading - a probe that does not say
    /// what it looked for cannot be told from one that looked for nothing.
    ///
    /// The catalogue version is deliberately NOT freshness evidence here: it changes only when the
    /// catalogue data does, so an old reading is perfectly consistent with a stale build carrying a
    /// current catalogue. Two different questions, reported separately.
    /// </summary>
    public static void ShowLoadedBuild()
    {
      // The newest symbols, in build order. Each was added by a specific work-package, so the deepest one
      // present dates the loaded binary.
      //
      // THE MAINTENANCE IS THE INSTRUMENT (#319), and it lapsed once already: this ladder sat at WP-3
      // Stage D through the whole of WP-4's build, so a current binary reported itself as a WP-3 one.
      // That failure is SAFE (it under-claims) but it is not LOUD - "reaches WP-3 Stage D" reads as a
      // coherent answer rather than a stale one, which is the shape a lapsed instrument always takes.
      // APPEND A ROW WHENEVER A STAGE SHIPS, in the same commit.
      //
      // THAT USED TO SAY "WHENEVER A STAGE ADDS A TYPE", AND THAT WAS THE BUG BEHIND THE LAPSE. WP-4
      // Stage E and the #365 tail added no type at all - they shipped behaviour on types that already
      // existed - so the rule silently excused them, and the ladder was not neglected so much as unable
      // to say what it needed to say. An instrument that cannot express its check does not fail loudly;
      // it goes quiet, which is the Family-A shape (#212) wearing a maintenance rule. So a row may now
      // name a MEMBER as well as a type.
      //
      // The member is given through `nameof`, never as a string literal: the compiler then checks it, so
      // renaming the thing this row exists to witness breaks the BUILD rather than emptying the probe.
      //
      // Its bound, stated because a reader deciding whether to trust a deploy needs it: this probe is
      // COMPILED INTO the assembly it reports on, so a stale .gha runs the stale probe and honestly
      // reports its own older depth. It cannot detect a staleness NEWER than itself, and it is not
      // supposed to - tools/probe-deploy.py is the EXTERNAL instrument for that, and the gate runs it
      // first.
      var probes = new (string Stage, Type Symbol, string Member)[]
      {
        ("WP-2 (agent tool surface)", typeof(AgentTools), null),
        ("WP-3 Stage A (the agent loop)", typeof(AgentLoop), null),
        ("WP-3 Stage B (the host tool set)", typeof(AgentHostTools), null),
        ("WP-3 Stage C (propose_scaffold)", typeof(ScaffoldPlanParser), null),
        ("WP-3 Stage D (the stored map)", typeof(ScaffoldMapRecord), null),
        ("WP-4 Stage 0 (the full-path drill)", typeof(FullPathDrill), null),
        ("WP-4 Stage A (the map's join key)", typeof(ScaffoldAddress), null),
        ("WP-4 Stage B (the rebuild ledger)", typeof(BuildLedger), null),
        ("WP-4 Stage C (the build scope contract)", typeof(BuildScope), null),
        ("WP-4 Stage D (the moved-set guard)", typeof(RegionTidy), null),
        // The two rows the type-only ladder could not carry - both stages shipped on existing types.
        ("WP-4 Stage E (the build recovery record)", typeof(AgentDiagnostics), nameof(ShowBuildRecoveries)),
        ("WP-4 tail (panel graduation)", typeof(Dwight.UI.DwightScaffold), nameof(Dwight.UI.DwightScaffold.GraduateFrom)),
        ("WP-4 tail (the placed component's initial state)", typeof(BuildState), null),
        // The gate's own findings, each a row because each was a defect a user hit.
        ("WP-4 gate F1 (the rebuild claim, re-derived)", typeof(AsLeftRecord), null),
        ("WP-4 gate F1 (the card names the removal)", typeof(RebuildPreview), null),
        ("WP-4 gate F4 (the map partitions present from absent)", typeof(ScaffoldMapPayload), null),
        ("WP-4 gate F5 (the permission-to-ask detector)", typeof(TurnShape), null),
        ("WP-4 gate F2/F3 (refusal causes, generated capabilities)", typeof(AgentHostTools),
         nameof(AgentHostTools.CapabilitySentence)),
        // The final fix cycle. Two of these shipped a METHOD on an existing type, which is exactly the case
        // the member form of a row exists for (the ladder's own lapse, #373).
        ("WP-4 final fix #573 (the canvas-change fact)", typeof(CanvasChangeFact), null),
        ("WP-4 final fix #574/#577 (capability alignment)", typeof(KindCapability), null),
        ("WP-4 final fix #581 (no addresses in plan prose)", typeof(ScaffoldPlanParser),
         nameof(ScaffoldPlanParser.FirstAddressToken)),
        ("WP-4 final fix #582 (the tried-it detector)", typeof(TurnShape), nameof(TurnShape.ClaimedToHaveTried)),
        // The approved-turn unit (#611). Three of its rows are members of existing types, and (e) changed a
        // method body with no symbol at all.
        ("WP-4 approved turn #612 (the loop a script can drive)", typeof(AgentTurnDriver), null),
        ("WP-4 approved turn #611a (a turn ends on a shown card)", typeof(AgentHostTools),
         nameof(AgentHostTools.ShowedCard)),
        ("WP-4 approved turn #611b (only solve arms the breaker)", typeof(MutationRequestLog),
         nameof(MutationRequestLog.ToolDispatched)),
        ("WP-4 approved turn #611c (the approved-turn fact)", typeof(ApprovedTurnFact), null),
        ("WP-4 approved turn #611d (the same-kind refusal)", typeof(ToolErrors), nameof(ToolErrors.AlreadyApproved)),
        // #611f changed ClosingHtml's PARAMETER type, which a member row cannot see; the external probe checks it.
        ("WP-4 approved turn #611g (a region outside the approval)", typeof(AgentHostTools),
         nameof(AgentHostTools.OutsideApproval)),
        // The rebuild fix unit (#623-#628, ruled at #631-#637). Four of its rows are members of existing types;
        // #627 changed a method body and #628 changed text, with no symbol at all - the external probe checks
        // those by CONTENT.
        ("WP-4 rebuild #631 (a rebuild removes only what its card named)", typeof(RebuildClaim),
         nameof(RebuildClaim.RemovalPresentIn)),
        ("WP-4 rebuild #623/#632 (facts are never destroyed)", typeof(BuildLedger), nameof(BuildLedger.CommitBuild)),
        ("WP-4 rebuild #624/#634 (the last build's outcome as a fact)", typeof(BuildOutcomeFact), null),
        ("WP-4 rebuild #624 (approved builds that never built)", typeof(TurnShape),
         nameof(TurnShape.ApprovedBuildTurnsWithoutBuild)),
        ("WP-4 rebuild #626 (the card previews the turn it opens)", typeof(BuildLedger),
         nameof(BuildLedger.ResolveForNextTurn)),
        // Unit 2 (#625, ruled at #642-#650): a rebuild keeps its boundary. Commit 7 changed text only, with no symbol -
        // the external probe checks it by content.
        ("WP-4 rebuild unit 2 #643 (wire sources judged by output param)", typeof(BuildIdentity), null),
        ("WP-4 rebuild unit 2 #625 (a rebuild keeps its boundary)", typeof(RebuildBoundary), null),
        ("WP-4 rebuild unit 2 #625 (the boundary refusal code)", typeof(ToolErrors), nameof(ToolErrors.BoundaryCut)),
        ("WP-4 rebuild unit 2 #648v (compare-and-set as-left refresh)", typeof(BuildLedger),
         nameof(BuildLedger.RefreshAsLeft)),
        ("WP-4 rebuild unit 2 #625/#647 (the approved turn's cut list)", typeof(RebuildBoundary),
         nameof(RebuildBoundary.TurnFact)),
        ("WP-4 rebuild unit 2 #648iv (the narrow sibling)", typeof(RestorableLoss), null),
        ("WP-4 rebuild unit 2 #649 (a revert is classified as a revert)", typeof(BuildEnvelope), null),
        ("WP-4 rebuild unit 2 #647 (refused: would cut)", typeof(BuildOutcomeFact), nameof(BuildOutcomeFact.BoundaryNote)),
        // The final WP-4 unit (#657-#662, ruled at #665-#670). Every row is a member of an existing type; #662a changed a
        // private join with no symbol - the external probe checks it by content.
        ("WP-4 final unit #657/#666 (the bounded build repair)", typeof(AgentLoopCore),
         nameof(AgentLoopCore.ShouldRepairBuild)),
        ("WP-4 final unit #657/#667 (the no-build banner)", typeof(AgentLoopCore), nameof(AgentLoopCore.NoBuildBanner)),
        ("WP-4 final unit #658(1)/#668 (an off-parity bound refused)", typeof(BuildStateParser),
         nameof(BuildStateParser.BoundOnParity)),
        ("WP-4 final unit #660 (per-region component counts)", typeof(CanvasReading), nameof(CanvasReading.CountsByAddress)),
        ("WP-4 final unit #659/#669 (announced a change, asked for nothing)", typeof(TurnShape),
         nameof(TurnShape.AnnouncedChangeWithoutRequest)),
        // The capability slice (Phase-9 U1, U2, U4 and the payload contract), merged with main at #677. The slice's own
        // units never had ladder rows - its deploy was dated by the external probe alone - so they enter here, in build
        // order, followed by the integration's checklist items (#678-#685). #679 changed a switch case with no symbol -
        // the external probe checks it by content.
        ("Phase-9 U1 (get_component reads what a component holds)", typeof(SourceBound), null),
        ("Phase-9 U1 (one shared persistent-values reader)", typeof(Dwight.Data.PersistentValues), null),
        ("Phase-9 U2 (the geometry read set)", typeof(GhGeometry), null),
        ("Phase-9 U2 (geometry facts, never geometry)", typeof(GeometryReport), null),
        ("Phase-9 payload contract (what Dwight sends, as data)", typeof(Dwight.Data.PayloadContract), null),
        ("Phase-9 U4 (set_value: the validated ask)", typeof(SetValueAsk), null),
        ("Phase-9 U4 (set_value: the one applier)", typeof(GhSetValue), null),
        ("capability slice #678 (the continuation holds no approval)", typeof(SetValueCore), nameof(SetValueCore.Continuation)),
        ("capability slice #680 (held state read whole)", typeof(StateToken), null),
        ("capability slice #680 (the canvas stamp, composed GH-free)", typeof(CanvasStampCore), null),
        ("capability slice #680 (the one held-state reader)", typeof(GhBuildState), nameof(GhBuildState.Read)),
        ("capability slice #682 (a click that changes nothing is a report)", typeof(SetValueReport),
         nameof(SetValueReport.NotAppliedText)),
        ("capability slice #683 (a way through that passes)", typeof(SetValueCore), nameof(SetValueCore.WayThrough)),
        ("capability slice #683 6b (the card's type reaches the click)", typeof(HandoffSeed), nameof(HandoffSeed.TargetType)),
        ("capability slice #683 6c (the applier's outcomes, one set)", typeof(SetValueCore), nameof(SetValueCore.GoneAtApply)),
        // The two slice defects and the bound test that followed them (#719/#720, merged as PRs #15-#17).
        //
        // THE MISS THESE ROWS EXIST FOR (#738-#740). All three merged with no ladder row and no probe stage, so the
        // deploy that carried them read back as "capability slice #683 6c" - TRUE AND STALE IN ONE SENTENCE, which is
        // this instrument's characteristic failure (see the lapse at the top of this comment: it under-claims, and an
        // under-claim reads as a coherent answer rather than a wrong one).
        //
        // THE RULE IT EARNED: A UNIT IS NOT FINISHED WHEN ITS TESTS PASS; IT IS FINISHED WHEN THE INSTRUMENT THAT
        // DATES A BUILD CAN SEE IT. A green suite says the code is right; only this says the binary in Rhino is the
        // one that holds it.
        //
        // #733 is TEST-ONLY and names no symbol - nothing shipped into this assembly for it, so there is deliberately
        // no row here rather than a row pointing at something else. The probe's stage comment says the same.
        ("slice defect #719 (the collection that invalidates the index)", typeof(AgentNodeList), null),
        ("slice defect #719 (alive-but-absent is not a deletion)", typeof(ToolErrors), nameof(ToolErrors.NotInThisRead)),
        ("slice defect #720 (addresses continue past what is in use)", typeof(ScaffoldPlanParser),
         nameof(ScaffoldPlanParser.AssignAddresses)),

        // #779 - THE FINGERPRINT COULD NOT SEE A SCRIPT'S SOURCE, and that was live on main rather than waiting on a
        // future unit. build_region already places script components; it just cannot give them source. So Dwight could
        // place an empty Python component, the user could write two hundred lines into it, and a rebuild would read it
        // as ours-and-untouched and put it in the removal set - #631 at source level. The card named the component, so
        // it was a CONSENT defect rather than a silent deletion: true, and unhelpful at the one moment it mattered.
        //
        // The member rows are the ones worth having here. A type row is compile-time by nature; ForScript is the
        // DECISION the Grasshopper reader calls, and it exists as a named function precisely because the first version
        // of this change's tests asserted a function BESIDE the reader and stayed green when the reader was reverted.
        ("Phase-9 #779 (a script's source digest)", typeof(Dwight.Data.SourceToken), nameof(Dwight.Data.SourceToken.ForScript)),
        ("Phase-9 #779 (the reader's decisions, GH-free)", typeof(Dwight.Data.ScriptSourceReflect),
         nameof(Dwight.Data.ScriptSourceReflect.TryRead)),

        // Phase-9 U20 - Dwight edits an existing script. The MEMBER rows are the ones worth having: each names a
        // decision that was got wrong once and now carries a test, so a build holding the type but not the member
        // is dated honestly rather than generously.
        //
        // ClaimClause earns its row because it is the one string SHARED by the card and the report, and the defect
        // it closed was a shared sentence pointing at its own surroundings ("the diff above" - true on a card,
        // false in a report). Outcome earns its row because the vacuous case falls through into Preserved under any
        // other ordering. MayOfferCard, because an outcome that cannot be offered must have NO card sentence
        // rather than a plausible one.
        ("Phase-9 U20 (the claim, shared and deixis-free)", typeof(ScriptEditVerdict), nameof(ScriptEditVerdict.ClaimClause)),
        ("Phase-9 U20 (a vacuous check is its own outcome)", typeof(ScriptEditVerdict), nameof(ScriptEditVerdict.Outcome)),
        ("Phase-9 U20 (only a checked edit may be offered)", typeof(ScriptEditVerdict), nameof(ScriptEditVerdict.MayOfferCard)),
        ("Phase-9 U20 (the edit ask and its refusals)", typeof(EditScriptAsk), nameof(EditScriptAsk.Validate)),
        ("Phase-9 U20 (the diff a user reads)", typeof(Dwight.Data.SourceDiff), nameof(Dwight.Data.SourceDiff.Between)),
        ("Phase-9 U20 (the card's authority line)", typeof(ConsentCard), nameof(ConsentCard.EditScriptAuthority)),
        ("Phase-9 U20 (the trial, run beside the original)", typeof(Dwight.Simplify.ScriptEditTrial), null),
        ("Phase-9 U20 (the applier, and the checked revert)", typeof(GhEditScript), nameof(GhEditScript.Apply)),

        // Phase-9 U6 + U21 (#760) - consented edits, batched: wire, unwire, delete existing components. One row per decision
        // a build could hold the type without: every row resolves to exactly one existing target or the card refuses
        // naming the rows; the total stands in the authority line whatever is collapsed; the ticks ride the click; the
        // verdict is the predicted delta over the fingerprint, on the ticked set alone; and the applier is one undo record.
        ("Phase-9 U6/U21 (the edit ask: every row resolves or the card refuses)", typeof(EditOpsAsk), nameof(EditOpsAsk.Validate)),
        ("Phase-9 U6/U21 (the batch card states the total)", typeof(ConsentCard), nameof(ConsentCard.EditAuthority)),
        ("Phase-9 U6/U21 (the ticks that ride the click)", typeof(ConsentTokens), nameof(ConsentTokens.Split)),
        ("Phase-9 U6/U21 (the verdict: predicted delta on the applied set)", typeof(EditPrediction), nameof(EditPrediction.Matches)),
        ("Phase-9 U6/U21 (the applier, one undo record)", typeof(GhEditOps), nameof(GhEditOps.Apply)),

        // Phase-9 U23, lane C - get_solve_timing. Three rows, one per decision a build could hold the type without:
        // the ONE rounding Simplify and this read now share (two roundings of one number would show a user two
        // answers), the report that never reads a zero on its own (#843), and the read the dispatcher routes to.
        ("Phase-9 U23 lane C (one rounding for a solve time)", typeof(Dwight.Data.SolveTimeBucket),
         nameof(Dwight.Data.SolveTimeBucket.Of)),
        ("Phase-9 U23 lane C (the timing report, zero read with its phase)", typeof(SolveTimingReport),
         nameof(SolveTimingReport.Build)),
        ("Phase-9 U23 lane C (the timing read, never a solve)", typeof(GhSolveTiming), nameof(GhSolveTiming.Run)),
        // #875: Simplify's hotspot guard tested the RAW reading and sent the bucket, so a 2 ms component could be named
        // as slow with "ms": 0. The selection moved beside the rounding, and the guard now tests the value sent.
        ("Phase-9 #875 (a hotspot is never sent with a zero bucket)", typeof(Dwight.Data.SolveTimeBucket),
         nameof(Dwight.Data.SolveTimeBucket.Hotspots)),

        // Phase-9 U23, lane C unit 2 - get_document_units and get_installed_plugins. The member rows name the decisions:
        // a tolerance read or refused (never defaulted), the inventory that withholds by construction, the ONE name a
        // plugin is called by, and the reads the dispatcher routes to.
        ("Phase-9 U23 lane C (a tolerance read or refused, never defaulted)", typeof(DocumentUnitsReport),
         nameof(DocumentUnitsReport.TryTolerance)),
        ("Phase-9 U23 lane C (the plugin inventory)", typeof(PluginInventoryReport), nameof(PluginInventoryReport.Build)),
        ("Phase-9 U23 lane C (one name per plugin)", typeof(Dwight.Data.PluginName), nameof(Dwight.Data.PluginName.Of)),
        ("Phase-9 U23 lane C (the session reads)", typeof(GhSessionFacts), nameof(GhSessionFacts.InstalledPlugins)),
        // #208, found beside the units read: the geometry reads measured against 0.001 when Rhino had no document.
        ("Phase-9 #208 lane C (geometry reads refuse without a document)", typeof(GhSessionFacts),
         nameof(GhSessionFacts.TryTolerance)),

        // Phase-9 U23, lane C unit 3 - get_file_state and get_canvas_layout. The file fact's two booleans ARE the
        // privacy bound; the layout report is where positions become relations and stop.
        ("Phase-9 U23 lane C (saved state, two booleans and no name)", typeof(FileStateReport), nameof(FileStateReport.Build)),
        ("Phase-9 U23 lane C (the layout as relations, never coordinates)", typeof(CanvasLayoutReport),
         nameof(CanvasLayoutReport.Build)),
        ("Phase-9 U23 lane C (the canvas reads)", typeof(GhCanvasFacts), nameof(GhCanvasFacts.Layout)),

        // U20's sitting fixes, the unit author's half (3h, 3c). Each row is a member, because each is a decision that
        // was got wrong or never wired: the language the edit carries (read from the TYPE, never the label), the
        // refusal composed where a test can hold its header, and the sentence a user reads when a turn ends with an
        // edit refused. The panel's USE of the last two is lane A's, and dates itself with lane A's own row.
        ("Phase-9 U20 sitting 3h (the edit carries its language)", typeof(ScriptEditChange), nameof(ScriptEditChange.Language)),
        ("Phase-9 U20 sitting 3h (the refusal, composed where it is tested)", typeof(ScriptEditVerdict),
         nameof(ScriptEditVerdict.RefusalBody)),
        ("Phase-9 U20 sitting 3c (the line a refused edit ends its turn with)", typeof(ScriptEditVerdict),
         nameof(ScriptEditVerdict.RefusedLine)),
        // Lane C's finding while building U19: U20's trial counted ITEMS, so an output of nulls "carried data" and a
        // preserves check of nothing against nothing read as verified (#109). "Has data" is a non-null item now.
        ("Phase-9 U20 (has data = a non-null item)", typeof(Dwight.Data.OutputItems), nameof(Dwight.Data.OutputItems.HasNonNull)),

        // Lane A's row for the PANEL'S USE of the two above - the wiring, which lands in one commit with both. The
        // member is the turn's per-target record the panel now keeps: it exists only in a build that also calls
        // RefusalBody from the refusal and RefusedLine at turn end, so its presence dates the wiring, not just the
        // functions it calls.
        ("Phase-9 U20 sitting 3c+3h (the panel's wiring: each target's last outcome, read at turn end)",
         typeof(EditAttempts), nameof(EditAttempts.RefusedLines)),

        // Phase-9 WAVE 2 - the agentic-run units, in the order they reached main (entries #938-#956), then the panel
        // wiring that made them reachable. No lane added a row here: the table is the one every lane collides on (#934),
        // so each left its symbols named in its PR for the wiring pass. MEMBER rows wherever the lane's decision is a
        // method, because a build can hold a type without the decision the unit shipped in it (#373).
        //
        // Lane B voice (PR #53, #938-#940).
        ("Phase-9 wave 2 U32 (announced a change, asked for nothing)", typeof(TurnShape),
         nameof(TurnShape.AnnouncedWithoutAsking)),
        ("Phase-9 wave 2 U28 (no card over nothing, decided at the ask)", typeof(VerbScope), nameof(VerbScope.ResolveAsk)),
        // Lane C scripts (PR #52, #942-#945).
        ("Phase-9 wave 2 U35 (the compare refuses two sides that share an identity)", typeof(Dwight.Simplify.ScriptTrialCompare),
         nameof(Dwight.Simplify.ScriptTrialCompare.Compare)),
        ("Phase-9 wave 2 U36 (the undo restores source and ports)", typeof(ScriptEditRestore), null),
        ("Phase-9 wave 2 U37 (the edit report names its subject)", typeof(ScriptEditVerdict), nameof(ScriptEditVerdict.Subject)),
        // Lane C reads (PR #54, #946-#950).
        ("Phase-9 wave 2 U27 (every stated number says what it counts)", typeof(SolveCheckReport), nameof(SolveCheckReport.Build)),
        ("Phase-9 wave 2 U31 (a fed panel reports what it shows)", typeof(PanelDisplay), nameof(PanelDisplay.Fed)),
        ("Phase-9 wave 2 U29 (a region is called a region)", typeof(Dwight.Labelling.RegionVocabulary),
         nameof(Dwight.Labelling.RegionVocabulary.GenericLabel)),
        ("Phase-9 wave 2 U25 reads (the qualified form where a name is ambiguous)", typeof(QualifiedNames),
         nameof(QualifiedNames.Resolve)),
        ("Phase-9 wave 2 U25 reads (kb_lookup lists every candidate)", typeof(KbLookupReport), nameof(KbLookupReport.Build)),
        // Lane A verdicts (PR #55, #951-#956).
        ("Phase-9 wave 2 U26 (the buildline says what the build placed)", typeof(BuildPlacedFacts), nameof(BuildPlacedFacts.Summary)),
        ("Phase-9 wave 2 U34 (a reverted build names its leg and subject)", typeof(BuildFailureDiagnosis),
         nameof(BuildFailureDiagnosis.Diagnose)),
        ("Phase-9 wave 2 U25 build (an ambiguous name refuses with the forms)", typeof(Dwight.Components.ComponentMatchCore),
         nameof(Dwight.Components.ComponentMatchCore.Resolve)),
        ("Phase-9 wave 2 U33 (a rebuild never duplicates a kept component)", typeof(RebuildDuplicates),
         nameof(RebuildDuplicates.Check)),
        // The wiring pass (this round): the members only the panel's half brought. The panel's CALLS into the lanes'
        // functions (HostTool -> VerbScope.ResolveAsk, the click -> the named-count overloads) have no symbol of their own;
        // tools/probe-deploy.py reads them by IL.
        ("Phase-9 wave 2 wiring U33 (a rebuild's kept components, read at turn start)", typeof(GhBuildRegion),
         nameof(GhBuildRegion.KeptForRebuild)),
        ("Phase-9 wave 2 wiring U33 (the kept-components fact)", typeof(ApprovedTurnFact), nameof(ApprovedTurnFact.KeptFact)),
        ("Phase-9 wave 2 wiring U28 (the card states the scope resolved at the ask)", typeof(ConsentCard),
         nameof(ConsentCard.WithScope)),
      };

      var sb = new StringBuilder();
      var assembly = typeof(AgentDiagnostics).Assembly;
      sb.AppendLine("Assembly: " + assembly.Location);
      try { sb.AppendLine("Built:    " + System.IO.File.GetLastWriteTime(assembly.Location).ToString("yyyy-MM-dd HH:mm:ss")); }
      catch { }
      sb.AppendLine();
      sb.AppendLine("SYMBOLS CHECKED (each names the build that introduced it):");

      string newest = null;
      foreach (var p in probes)
      {
        // A member row asks the LOADED assembly whether that member is really there, which is the only
        // half of this ladder that is a runtime question at all. The type rows are compile-time by
        // nature - the file would not build without them - and their value is the stage name they date.
        var present = p.Symbol != null &&
          (p.Member == null || p.Symbol.GetMember(p.Member,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance).Length > 0);

        var what = p.Symbol?.FullName + (p.Member == null ? "" : "." + p.Member);
        sb.AppendLine("  " + (present ? "PRESENT" : "MISSING") + "  " + what + "   <- " + p.Stage);
        if (present) newest = p.Stage;
      }

      sb.AppendLine();
      sb.AppendLine(newest == null
        ? "VERDICT: no probed symbol is present - this .gha predates the agent surface entirely. STOP."
        : "VERDICT: the loaded build reaches " + newest + ".");
      sb.AppendLine("If that is older than the work you are about to gate, the deploy is STALE: close Rhino,");
      sb.AppendLine("rebuild, reopen, and probe again before spending a solve or a token (#131).");
      sb.AppendLine();
      sb.AppendLine("Catalogue version is a SEPARATE question (#196) - see \"Loaded catalogue\". It tracks the");
      sb.AppendLine("data, not the code, so a current catalogue is no evidence of a fresh build.");

      Show("Loaded build probe", sb.ToString());
    }

    /// <summary>
    /// D2: WHERE does the model's text get mangled? Four stages are already proven clean headlessly -
    /// markdown rendering, the JS marshalling (which escapes non-ASCII and is therefore
    /// encoding-independent), the store round-trip, and the shell's charset declaration. This is the one
    /// stage no headless test can reach: the WebView hand-off itself.
    ///
    /// It renders a KNOWN string through the ordinary panel path and prints what C# actually sent, so
    /// the comparison is decisive rather than interpretive:
    ///   - log escaped + screen mojibake -> the WEBVIEW hand-off is the mangler;
    ///   - log already mojibake          -> the corruption is upstream of the panel;
    ///   - both correct                  -> the panel path is clean and the mangling arrives with the
    ///                                      model's own bytes.
    ///
    /// A check only a human at a screen can discharge, so it is handed to one with the reading spelled
    /// out (#278).
    /// </summary>
    public static void ShowTextEncodingProbe()
    {
      const string sample = "Encoding probe: 3 × 4 curves — arrow → degree ° half ½";

      var panel = Dwight.UI.ResultSurface.Show("Dwight - Encoding probe");
      try { panel.SetMarkdown("**" + sample + "**\n\nIf that line shows mojibake, the WebView hand-off is the mangler."); }
      catch { }

      var sb = new StringBuilder();
      sb.AppendLine("WHAT C# HOLDS (should read cleanly here):");
      sb.AppendLine("  " + sample);
      sb.AppendLine();
      sb.AppendLine("WHAT C# SENDS TO THE PAGE (the same call WebPanel.Js makes):");
      sb.AppendLine("  " + System.Text.Json.JsonSerializer.Serialize(sample));
      sb.AppendLine();
      sb.AppendLine("CODE POINTS: " + string.Join(" ", sample.Where(c => c > 127).Select(c => "U+" + ((int)c).ToString("X4"))));
      sb.AppendLine();
      sb.AppendLine("WHICH PATH THIS WALKS - and it is NOT the chat's (#293b):");
      sb.AppendLine("  This renders through SetMarkdown -> dwightSetBody. The CHAT renders through");
      sb.AppendLine("  AppendChat -> dwightChatAppend, which this probe does not touch. So a clean panel");
      sb.AppendLine("  here clears SetMarkdown ALONE, never \"the panel path\" - claiming otherwise is how");
      sb.AppendLine("  an instrument measures something adjacent to the thing it is asked about.");
      sb.AppendLine();
      sb.AppendLine("NOW COMPARE with the panel that just opened:");
      sb.AppendLine("  panel correct + this correct -> SetMarkdown is clean. Nothing is proven about the");
      sb.AppendLine("                                  chat's render path or about the provider.");
      sb.AppendLine("  panel MANGLED + this correct -> the WEBVIEW hand-off is the mangler.");
      sb.AppendLine("  both mangled                 -> the corruption is upstream of both.");
      sb.AppendLine();
      sb.AppendLine("WHAT THE PROVIDER LAST DECODED (run a chat turn containing x, a dash or an arrow first):");
      var decoded = LlmTrace.LastNonAscii;
      sb.AppendLine(decoded == null
        ? "  (nothing captured yet - no non-ASCII has come back from a model this session)"
        : "  " + decoded);
      sb.AppendLine();
      sb.AppendLine("  U+00D7 for a multiplication sign -> the provider is CLEAN; look downstream.");
      sb.AppendLine("  U+00C3 U+2014 and friends        -> the DECODED text is already mojibake, so the");
      sb.AppendLine("                                      bytes were double-encoded, not mis-decoded.");

      Show("Text encoding probe", sb.ToString());
    }

    private static void Show(string title, string text)
    {
      try { Dwight.UI.DwightToast.Show(title + " - see the Rhino command line"); } catch { }
      try { Rhino.RhinoApp.WriteLine("=== Dwight: " + title + " ===\n" + text); } catch { }
    }
    /// <summary>
    /// THE RECOVERY RATE (#143/#212) - does the build's self-correction loop actually recover anything?
    ///
    /// WP-0 measured its repair loop at 0 recoveries in 56 firings and only knew because the firings were
    /// COUNTED. WP-4's loop has a different oracle - a runtime message rather than a semantic class-label,
    /// which #94's refined law says should be actionable - but "should" is a hypothesis, and #143 says
    /// never spend on a measured zero. This is what makes it measurable rather than assumed.
    ///
    /// It reads Dwight's own record, costs nothing, and reports the DENOMINATOR beside the rate: a
    /// recovery count with no retries behind it is not a zero, it is an absence of data (#212 - a
    /// measurement that has only ever produced one value has not been shown to measure).
    /// </summary>
    /// <summary>
    /// F5's detector, read back. It reports the DENOMINATOR beside the count, because a zero that cannot
    /// be told from an absence is not a measurement (#212) - "0 of 0 turns examined" and "0 of 40" are
    /// completely different readings and they would print identically without it.
    /// </summary>
    public static void ShowTurnShape()
    {
      var sb = new StringBuilder();
      sb.AppendLine("TURN SHAPE - did a turn ask PERMISSION TO ASK instead of raising the card?");
      sb.AppendLine();
      sb.AppendLine("The card IS the ask. A turn that proposes an action, stops, and waits for \"go ahead\"");
      sb.AppendLine("costs a billed round-trip and a second user interaction to reach a card whose whole job");
      sb.AppendLine("is to be the question. The instruction now lives in the master prompt (it used to sit in");
      sb.AppendLine("request_mutation's descriptor, which governs tool USE and never turn SHAPE).");
      sb.AppendLine();
      sb.AppendLine("THIS DETECTOR MEASURES, IT DOES NOT ENFORCE. A prompt line's effect on a model cannot be");
      sb.AppendLine("asserted, so recurrence is counted and a rate earns a structural fix.");
      sb.AppendLine();
      sb.AppendLine("  turns examined:        " + TurnShape.Examined);
      sb.AppendLine("  asked permission:      " + TurnShape.Count);

      sb.AppendLine("  claimed to have TRIED:  " + TurnShape.ClaimedTriedCount +
                    "   (said it tried or tested something while calling nothing that mutates or solves)");

      // #659/#669: the third number, measure only.
      sb.AppendLine("  announced a CHANGE:     " + TurnShape.AnnouncedChangeCount +
                    "   (said it would build, rebuild, reorganise or raise a card while requesting and acting on nothing)");

      // #624: the approved build that never ran, with its own denominator.
      sb.AppendLine("  approved BUILD turns:  " + TurnShape.ApprovedBuildTurns);
      sb.AppendLine("    ...that dispatched NO build_region: " + TurnShape.ApprovedBuildTurnsWithoutBuild +
                    "   (the approval never ran, whatever the turn said)");

      if (TurnShape.Examined == 0)
        sb.AppendLine("  NOT MEASURED - no turn has finished in this session yet. This is an absence, not a zero.");
      else if (TurnShape.ApprovedBuildTurns == 0)
        sb.AppendLine("  (no approved build turn has finished yet - the build count is an absence, not a zero)");

      if (TurnShape.ClaimedTriedExamples.Count > 0)
      {
        sb.AppendLine();
        sb.AppendLine("Claimed to have tried it:");
        foreach (var e in TurnShape.ClaimedTriedExamples) sb.AppendLine("  ... " + e);
      }

      if (TurnShape.AnnouncedChangeExamples.Count > 0)
      {
        sb.AppendLine();
        sb.AppendLine("Announced a change and asked for nothing:");
        foreach (var e in TurnShape.AnnouncedChangeExamples) sb.AppendLine("  ... " + e);
      }

      if (TurnShape.Examples.Count > 0)
      {
        sb.AppendLine();
        sb.AppendLine("The tail of each one (the ask lives at the end):");
        foreach (var e in TurnShape.Examples) sb.AppendLine("  ... " + e);
      }

      Show("Turn shape", sb.ToString());
    }

    /// <summary>
    /// WHAT DWIGHT IS TOLD IT CAN AND CANNOT DO - the generated capability statement, exactly as it goes into
    /// the master prompt (capability alignment, #574/#577).
    ///
    /// It exists so the gate row that compares what Dwight SAYS it can do against what it is TOLD can
    /// actually be run. The first draft of that row sent the reader to "Last LLM request", which shows the
    /// thinking settings and token counts and not the system prompt at all - the row would have been
    /// unrunnable, and a hand-written copy of the statement in the gate document would go stale the way
    /// the probe's row list did. This reads the one generated source.
    /// </summary>
    public static void ShowCapabilityStatement()
    {
      var sb = new StringBuilder();
      sb.AppendLine("WHAT DWIGHT IS TOLD IT CAN AND CANNOT OFFER");
      sb.AppendLine("Generated from the plugin's own list of kinds - this is the text the model receives.");
      sb.AppendLine("Compare it against what Dwight SAYS it can do, having written that down first.");
      sb.AppendLine();
      sb.AppendLine(AgentHostTools.CapabilitySentence());
      Show("What Dwight is told it can do", sb.ToString());
    }

    public static void ShowBuildRecoveries()
    {
      var doc = Grasshopper.Instances.ActiveCanvas?.Document;
      if (doc == null) { Show("Build recoveries", "No document is open."); return; }

      var sb = new System.Text.StringBuilder();
      var retried = 0;
      var recovered = 0;
      var regions = 0;

      foreach (var threadId in Dwight.Session.CommandHistory.ThreadIds(doc))
      {
        var rows = Dwight.Agent.BuildProgress.Rows(Dwight.Session.CommandHistory.ThreadBuildProgress(doc, threadId));
        foreach (var r in rows)
        {
          regions++;
          if (r.Attempts > 1) retried++;
          if (r.Recovered) recovered++;
          sb.AppendLine("  " + r.Address.PadRight(6) + r.Outcome.ToString().PadRight(18) +
                        "attempts " + r.Attempts + "  first " + r.FirstOutcome +
                        (r.Recovered ? "   <- RECOVERED" : ""));
        }
      }

      var head = regions == 0
        ? "No build has been recorded on this document yet - so this is an ABSENCE OF DATA, not a zero."
        : regions + " region(s) recorded; " + retried + " needed more than one attempt; " +
          recovered + " of those " + retried + " recovered." + Environment.NewLine +
          (retried == 0
            ? "NO RETRY HAS HAPPENED YET, so the recovery rate is unmeasured rather than zero (#212)."
            : "Recovery rate: " + recovered + "/" + retried + ".");

      Show("Build recoveries",
        head + Environment.NewLine + Environment.NewLine +
        (sb.Length == 0 ? "  (no rows)" : sb.ToString()));
    }

  }
}
