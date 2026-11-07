using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Text.Json.Nodes;
using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;
using Grasshopper.Kernel.Undo;
using Grasshopper.Kernel.Undo.Actions;
using Dwight.Components;
using Dwight.Data;
using Dwight.UI;

namespace Dwight.Agent
{
  /// <summary>
  /// <c>build_region</c> - the agent's only way to add to the canvas, and therefore the place the whole
  /// "verified tools, never a god-tool" thesis (#186) either holds or does not.
  ///
  /// THE ORDER, which is the design (read top to bottom in <see cref="Run"/>):
  ///   1. VALIDATE EVERYTHING FIRST - whitelist-only types, grounded ids, before a single AddObject. A
  ///      half-valid spec must never half-build.
  ///   2. CAPTURE THE BASELINE BEFORE MUTATING - the fingerprint AND the pre-existing error set, in this
  ///      same call (#193c). A definition may legitimately already carry warnings; "new errors" is
  ///      meaningless without the set they are new against.
  ///   3. MUTATE UNDO-FIRST - every change from the first AddObject is in the recorded actions, and
  ///      RecordEvent is the LAST statement, so a pre-commit failure rolls back with no event and a
  ///      post-commit failure has exactly one Ctrl-Z to undo.
  ///   4. SOLVE, THEN VERIFY the trio - preservation, completeness, solve-health.
  ///   5. REVERT ON FAILURE, then report. Never leave a build standing that failed its own check.
  ///
  /// The verdict says what it proves AND what it does not (#193a): that the build landed as specified
  /// and broke nothing existing - never that the region computes what the user wanted.
  /// </summary>
  internal static class GhBuildRegion
  {
    private const string UndoName = "Dwight: build region";
    private const int WatchdogMs = 120_000;
    private const int StartWatchdogMs = 30_000;

#if DEBUG
    /// <summary>A dev-menu live-fire drill (the SimplifyApply.Sabotage precedent): corrupt exactly the
    /// NEXT build by locking a pre-existing component, then self-clear. Preservation must catch it and
    /// the build must revert - which is the only way to demonstrate revert-first WORKING rather than
    /// merely being written. DEBUG-gated permanently as a re-runnable drill.</summary>
    internal static bool SabotageNextBuild;

    /// <summary>Set ONLY when a victim was actually corrupted. The drill has a precondition - something
    /// pre-existing to damage - and on a canvas that has none it must say it did not fire rather than
    /// report a guard as exercised when nothing was tested (#209). It read true regardless once, and a
    /// build that passed honestly was printed as a caught drill.</summary>
    internal static bool LastBuildSabotaged;

    /// <summary>What the drill did, or why it could not - written for the gate's reader.</summary>
    internal static string LastSabotageNote;

    private static IGH_ActiveObject _sabotaged;
    private static bool _sabotagedWasLocked;
#endif

    public static void Run(GH_Document doc, AgentIdRegistry ids, JsonObject args, Action<ToolResult> done) =>
      Run(doc, ids, null, args, done);

    /// <summary>
    /// THE LIVE RESOLVERS - the only factory for them, used by the build AND by the ask (U18, #862).
    ///
    /// A wrong wire must refuse at the ASK, before a card exists. The ask can only do that by asking the same
    /// question the build asks, so it asks it through the same bundle rather than a parallel set of delegates
    /// that would agree today and drift silently tomorrow. Two resolvers answering "does this wire resolve?"
    /// is the second-definition class (#248); one factory makes the sameness a fact about the call graph.
    ///
    /// Behaviour-preserving for the build: these are the delegates <see cref="Run"/> has always used, with the
    /// per-type port cache now held per bundle rather than per call - a cache over reads of the installed
    /// components, which cannot change a result.
    /// </summary>
    public static SpecResolvers LiveResolvers(GH_Document doc, AgentIdRegistry ids)
    {
      var typePorts = new Dictionary<string, PortShape>(StringComparer.OrdinalIgnoreCase);
      return new SpecResolvers
      {
        TypeExists = ComponentWhitelist.Exists,
        Nearest = t => ComponentWhitelist.Nearest(t),
        IdState = ids.State,
        PortsOfType = t => PortsOfType(t, typePorts),
        PortsOfId = id => PortsOfId(doc, ids, id),
        // U25 (#915): a bare name shared by several installed components is refused with their qualified forms.
        AmbiguousForms = t => ComponentWhitelist.Resolve(t, out _, out var forms) == Dwight.Components.NameResolution.Ambiguous
          ? forms : null,
        // Used only by a spot's record and card, never by Parse - so how the build resolves is unchanged. U25: the
        // canonical spelling is QUALIFIED where the bare name is shared, so the record the click builds from resolves.
        CanonicalType = t => ComponentWhitelist.Canonical(t) ?? t,
        // Brief CM: the groups a request can name. The ask reads them, and the panel adds what the user last wrote.
        Groups = () => doc.Objects.OfType<GH_Group>().Select(g => NamedGroup.Of(g.InstanceGuid, g.NickName)).Where(g => g != null).ToList(),
      };
    }

    /// <summary>
    /// What an existing component is CALLED, by agent id - for a spot card's "beside the ... it connects to"
    /// (U18). The same <see cref="Describe"/> a group's name uses, so the card and the canvas call a
    /// component by one name. Null for an id that does not resolve: the card then says nothing about where it
    /// sits rather than guessing.
    /// </summary>
    public static Func<int, string> LabelOfId(GH_Document doc, AgentIdRegistry ids) => id =>
    {
      try
      {
        return ids != null && ids.TryGetGuid(id, out var guid) && doc?.FindObject(guid, false) is IGH_DocumentObject o
          ? Describe(o)
          : null;
      }
      catch { return null; }
    };

    /// <summary>U73: say on the verdict which scripts were built from the card's text, and where the model's text differed.</summary>
    private static JsonObject StampCardScripts(JsonObject verdict, List<ScriptSubstitution> subs)
    {
      if (verdict == null || subs == null || subs.Count == 0) return verdict;
      verdict["scriptsFromCard"] = new JsonArray(subs.Select(x => (JsonNode)x.ToJson()).ToArray());
      verdict["scriptNote"] = ScriptSubstitution.NoteFor(subs);   // U89: says first that the sent source was not built, when it was not
      return verdict;
    }

    public static void Run(GH_Document doc, AgentIdRegistry ids, BuildLedger ledger, JsonObject args,
                           Action<ToolResult> done)
    {
      if (doc == null) { done?.Invoke(ToolResult.Fail(ToolErrors.NoDocument, "No Grasshopper document is open.")); return; }
      ledger = ledger ?? new BuildLedger(doc);

      // U47 (second pass): ONE REPORT, AND ALWAYS ONE. The caller hears from the verdict (a SolutionEnd handler) or from the
      // watchdog below (a timer), whichever speaks first; the other is silenced, so a late verdict never reports - or
      // reverts - a build the watchdog already dealt with.
      var latch = new SpotClickGuard.ReportLatch();
      var report = done;
      Eto.Forms.UITimer startWatch = null;
      void StopStartWatch() { try { startWatch?.Stop(); } catch { } }
      done = r => { if (!latch.TryClaim()) return; StopStartWatch(); report?.Invoke(r); };
      var started = false;     // the scheduled callback began
      var recorded = false;    // ... and wrote its undo record
      var verifying = false;   // ... and reached the solve-then-verify wait
      var kit = new RevertKit();   // U77: what the revert checks before it asks Grasshopper to undo

      // ---- 1. validate before anything touches the canvas -------------------------------------------
      // Through LiveResolvers, the ONE factory the ask uses too (U18) - so what the build refuses and what the
      // ask refuses first are decided by the same code. The delegates are exactly the ones this line has
      // always used; only their location moved.
      // Brief CA: a script component with no source, in a region whose card showed one, is that script: the plugin holds the text, so the model need not retype it.
      BuildScriptGate.FillFromCard(args, ledger.ApprovedScripts);
      if (!LiveResolvers(doc, ids).Parse(args, out var spec, out var specError))
      { done?.Invoke(specError); return; }

      // ---- 1a. CODE RUNS ONLY WHERE THE USER SAW IT (U19) -------------------------------------------
      // A script's source is shown, complete, on a PLACEMENT card. A map region's card names regions and not
      // components, so the same source would run unseen - refused here, before anything else, however real the
      // region is. The discriminator is the turn's permission (which the user's click set and the model cannot reach).
      var codeRefusal = BuildScriptGate.Adopt(spec, spec.Region != null && ledger.IsApprovedSpot(spec.Region), ledger.ApprovedScripts, out var cardSubstitutions);
      if (codeRefusal != null) { done?.Invoke(codeRefusal); return; }
      spec.CardScripts = cardSubstitutions;   // U73: the verdict says which scripts were built from the card
      if (spec.Region != null && cardSubstitutions.Any(s => s.SourceDiffered)) ledger.NoteCardSourceBuilt(spec.Region);   // U89

      // ---- 1b. the REGION: resolved exactly, or refused (WP-4 Stage B) ------------------------------
      GH_Group target = null;
      var replacing = new List<Guid>();
      var recognisedBefore = 0;   // the drill's "before": how many of its own components Dwight recognised here
      var freshRegion = false;    // #625's narrow sibling applies to a FRESH build of a region only
      var landsInto = false;      // Brief CM: the card named this existing group as where a placement lands
      IReadOnlyList<Guid> kept = new List<Guid>();   // Brief DK: what a rebuild here leaves standing, which keeps its place and which what lands starts beside
      if (spec.Region != null)
      {
        // THE APPROVED SCOPE IS A CONTRACT (#338, Stage C). Checked BEFORE the region is even looked up:
        // a region the user did not approve is refused however real it is and however convincingly it was
        // asked for. The plugin holds this, not the prompt - a prompt line is not a guard (#195).
        if (!ledger.MayBuild(spec.Region))
        {
          done?.Invoke(ToolResult.Fail(ToolErrors.RegionNotApproved,
            $"The user approved building {Join(ledger.Authorised)} - not {spec.Region}. Nothing was built. " +
            "Ask them for that region separately if it needs building too.",
            new JsonObject
            {
              ["asked"] = spec.Region,
              ["approved"] = new JsonArray(ledger.Authorised.Select(a => (JsonNode)a).ToArray()),
            }));
          return;
        }

        if (!GhScaffoldMap.TryFindGroup(doc, spec.Region, out target, out var available, out var ambiguous))
        {
          // A SPOT BRINGS ITS OWN PLACE, OR NONE AT ALL (U18).
          //
          // Every map region must already be a group on the canvas - that is #221b, and a build aimed at
          // one that is not there is aimed at nothing. A SPOT IS THE OPPOSITE CASE BY DESIGN: the user
          // approved it before it existed, and this build is what creates it. It may end with a group
          // (the user ticked that) or with none at all (they did not), so a missing group here is the
          // expected state rather than a missing region.
          //
          // The allowance is NARROW ON PURPOSE: only this turn's own approved spot, and never an
          // ambiguous one. `IsApprovedSpot` reads the turn's permission - which the user's click set and
          // the model cannot reach - so no address the model supplies can talk its way through here.
          if (ambiguous || !ledger.IsApprovedSpot(spec.Region))
          {
            done?.Invoke(ToolResult.Fail(ToolErrors.UnknownRegion,
              ambiguous
                ? $"More than one group on this canvas is called \"{spec.Region}\", so it does not name one " +
                  "place. Nothing was built."
                : $"No region \"{spec.Region}\" is on this canvas.",
              new JsonObject
              {
                ["asked"] = spec.Region,
                ["available"] = new JsonArray(available.Select(a => (JsonNode)a).ToArray()),
              }));
            return;
          }
        }

        // Brief CM: A PLACE THE CARD NAMED, BY ITS IDENTITY. The spot stays the ledger's key, so everything keyed on an approved spot holds; the group is only where the
        // components land. What it already holds is not this build's, so it is no part of the claim below and nothing in it is replaced. Gone at the click is a refusal, never a
        // new place, because the user approved that group.
        var into = ledger.SpotInto(spec.Region);
        if (into.HasValue)
        {
          target = doc.FindObject(into.Value, false) as GH_Group;
          if (target == null)
          {
            done?.Invoke(ToolResult.Fail(ToolErrors.UnknownRegion,
              "The group this card named is no longer on the canvas, so nothing was built. Ask again and Dwight will look at what is there now.",
              new JsonObject { ["asked"] = spec.Region }));
            return;
          }
          landsInto = true;
        }

        var inRegion = target == null || landsInto ? new List<Guid>() : GhScaffoldMap.ComponentsIn(doc, target);

        // F1(b): the claim is RE-DERIVED against the canvas rather than trusted from storage. The ledger
        // is handed what is in the region NOW, each with its current as-left token, and sorts it into
        // ours-and-untouched, ours-but-adopted, and never-ours.
        //
        // WITH NO GROUP THERE IS NO BOUNDARY, so the extent is the RECORD instead (#770): the ledger walks
        // its own facts and asks only what each one's token is now. `theirs` is then zero by construction -
        // nothing can be "inside" a placement that has no inside - which is the honest meaning of having
        // no box rather than a weakening of the claim.
        var claim = target != null && !landsInto
          ? ledger.Resolve(spec.Region, AsLeftNow(doc, inRegion),
                           DwightConfig.AgentBuildMaxAttemptsPerRegion)
          : ledger.ResolveWithoutBoundary(spec.Region, g => AsLeftOne(doc, g),
                                          DwightConfig.AgentBuildMaxAttemptsPerRegion);
        switch (claim.Decision)
        {
          case RebuildDecision.Exhausted:
            // #352: whatever is standing STAYS standing and is reported. Never a silent revert of work
            // the user watched land - the honest stop keeps what the turn produced (#223).
            done?.Invoke(ToolResult.Fail(ToolErrors.RegionAttemptsExhausted,
              BuildLedger.ExhaustedMessage(spec.Region, DwightConfig.AgentBuildMaxAttemptsPerRegion, ledger.CardSourceBuilt(spec.Region)),
              new JsonObject
              {
                ["region"] = spec.Region,
                ["attempts"] = ledger.Attempts(spec.Region),
                ["componentsStanding"] = inRegion.Count,
              }));
            return;

          case RebuildDecision.Occupied:
            // Constraint 3, and it now has a much better story to tell. Dwight removes only what it
            // placed AND has not been touched since; everything else in the region stays. So the refusal
            // can name WHY there is nothing to remove, which is what a model needs in order to explain it
            // without inventing a cause (the F2 class, at its first site).
            //
            // The old wording said "components that THIS CONVERSATION did not place", which was false in
            // the case a user hits most: the claim was scoped to one MESSAGE, so a user who built A1 and
            // then asked to rebuild it was accused of components Dwight had just placed.
            done?.Invoke(ToolResult.Fail(ToolErrors.RegionOccupied,
              BuildLedger.OccupiedMessage(spec.Region, claim),
              new JsonObject
              {
                ["region"] = spec.Region,
                ["componentsInRegion"] = inRegion.Count,
                ["placedByDwightButChangedSince"] = claim.Adopted.Count,
                ["neverPlacedByDwight"] = claim.Theirs,
                ["placedEarlier"] = claim.PlacedEarlier.Count,
                ["cause"] = BuildLedger.OccupiedCause(claim),
              }));
            return;

          case RebuildDecision.Replace:
            // #631: EXACTLY WHAT THE CARD NAMED - the resolved claim's Remove, still on the canvas. Never
            // everything Dwight recorded here: that list included components the user has edited since,
            // which the card promises to LEAVE, and preservation would have certified their deletion.
            replacing = claim.RemovalPresentIn(g => { try { return doc.FindObject(g, false) != null; } catch { return false; } })
              .ToList();
            recognisedBefore = claim.Remove.Count + claim.Adopted.Count;
            break;
        }
        freshRegion = claim.Decision == RebuildDecision.Fresh;
        kept = claim.Adopted.Concat(claim.PlacedEarlier).ToList();

        // ---- 1b'. A REBUILD NEVER DUPLICATES A KEPT COMPONENT (U33, #922) ------------------------------
        // The as-left rule keeps what the user has edited since Dwight placed it; a spec placing the same nickname
        // and type beside it would orphan the kept one, not replace it. Refused HERE, before anything is removed
        // and before an attempt is counted, naming the kept component by id so the model wires from it instead.
        var duplicate = RebuildDuplicates.Check(spec, KeptOf(doc, ids, claim), spec.Region);
        if (duplicate != null) { done?.Invoke(duplicate); return; }
      }

      // ---- 1c. A REBUILD KEEPS ITS BOUNDARY (#625), decided before anything is removed ------------------
      //
      // Every wire the removal would cut into something it keeps must be matched by exactly one declared
      // reconnection, or the build refuses here - a validation refusal, so no attempt is spent, and it carries
      // the cut list and #647's way out. A declaration that matches no cut refuses too (including on a build
      // that removes nothing).
      var cuts = CutWiresOf(doc, ids, replacing);

      // #625's narrow sibling: on a FRESH build of a region, the inputs that lost only their source from that
      // region's earlier components. Offered, never required: Match refuses no undeclared restore.
      if (freshRegion) cuts.AddRange(RestorableLossesOf(doc, ids, ledger, spec.Region));

      if (!RebuildBoundary.Match(spec.Region, cuts, spec.Reconnects, out var planned, out var boundaryError))
      {
        done?.Invoke(boundaryError);
        return;
      }

      // #625/#648(v): each consumer's as-left token IMMEDIATELY BEFORE this build - the compare half of the
      // compare-and-set that lets Dwight's own reconnection keep a component Dwight's, and never launders a user edit.
      var consumersBefore = AsLeftNow(doc, planned.Where(p => !p.Cut.Restore).Select(p => p.Cut.ConsumerGuid).Distinct());

      // A RESTORE's compare is against the STORED record: the sibling's diff proved the lost source is the only
      // difference, so once it is back the component is exactly as Dwight left it (#648 iv). If the record moved
      // between the diff and the commit, the compare-and-set refuses.
      foreach (var p in planned.Where(p => p.Cut.Restore))
      {
        var stored = ledger.AsLeftOf(p.Cut.ConsumerGuid)?.Token;
        if (!string.IsNullOrEmpty(stored)) consumersBefore[p.Cut.ConsumerGuid] = stored;
      }

      if (!SolverEnabled())
      {
        // Without a solve there is no verdict, and an unverified build must never land (#109).
        done?.Invoke(ToolResult.Fail(ToolErrors.SolverDisabled,
          "The Grasshopper solver is disabled, so a build cannot be verified. Nothing was changed."));
        return;
      }

      // ---- 2. baseline, captured before the mutation, in this same call (#193c) ---------------------
      try { if (doc.Objects.Any(o => o is IGH_ActiveObject a && a.Phase == GH_SolutionPhase.Blank)) doc.NewSolution(false); }
      catch { }

      FpDoc before;
      HashSet<string> baselineErrors;
      try
      {
        before = BehaviourFingerprint.ReadDoc(doc);
        baselineErrors = ErrorKeys(doc);
      }
      catch (Exception ex)
      {
        done?.Invoke(ToolResult.Fail(ToolErrors.PreconditionRefused, "Could not read the current state (" + ex.Message + ")."));
        return;
      }

      // THE START WATCHDOG (U47). The verify wait has its own timer; this one covers the stretch BEFORE it - the scheduled
      // callback that never ran, or ran and never got as far as the wait. The U19 gate left a document inside a solution with
      // the build on the canvas and nobody told. It fires once, only if no verdict has been delivered and the verify wait was
      // never reached; a callback that has not begun is cancelled, so it cannot change the canvas after the user was told
      // nothing was.
      try
      {
        startWatch = new Eto.Forms.UITimer { Interval = StartWatchdogMs / 1000.0 };
        startWatch.Elapsed += (s, e) =>
        {
          StopStartWatch();
          if (latch.Delivered || verifying) return;
          var secs = StartWatchdogMs / 1000;
          if (!started)
          {
            started = true;   // the callback, should it still arrive, finds the gate shut and does nothing
            done(ToolResult.Fail(ToolErrors.PreconditionRefused,
              "Grasshopper did not start the build within " + secs + " seconds, so nothing was changed."));
          }
          else if (recorded)
          {
            UndoOnce(doc, kit);
            var wdMeasured = Measure(doc, kit);
            done(ToolResult.Fail(ToolErrors.VerificationFailed,
              RevertMeasure.Apply(SpotClickGuard.ReportLine("the build did not reach its check within " + secs + " seconds", true), wdMeasured),
              new JsonObject { ["failedLeg"] = BuildFailureDiagnosis.SolveHealth }));
          }
          else
          {
            done(ToolResult.Fail(ToolErrors.PreconditionRefused,
              "The build stopped partway, within " + secs + " seconds, before it could be undone as one step. " +
              "Check the canvas: part of it may be there."));
          }
        };
        startWatch.Start();
      }
      catch { startWatch = null; }

      doc.ScheduleSolution(10, d =>
      {
        if (started) return;   // the start watchdog already told the caller this build did not begin
        started = true;
        kit.Before = NamesOf(d);   // U91: what stood BEFORE the build, so a revert can be MEASURED against it
        var actions = new List<IGH_UndoAction>();
        var created = new Dictionary<string, IGH_DocumentObject>(StringComparer.Ordinal);
        var committed = false;

        // Every input param whose wire state is snapshotted in this undo record, so one input is never
        // snapshotted twice (the removal's consumers and WireAll's targets can overlap).
        var snapshotted = new HashSet<IGH_Param>();
        try
        {
          // ---- 3. mutate, undo-first ---------------------------------------------------------------
          //
          // 3a. A REBUILD IS A DELETE, and it removes exactly the guids the ledger names (#352's
          // constraint 1) - never the group's membership. The removals go in the SAME undo record as the
          // new build, so one Ctrl-Z restores the previous attempt and takes the replacement away
          // together; a half-undone correction would be its own kind of wrong.
          if (replacing.Count > 0)
          {
            // The GROUP's membership is about to change, so snapshot the group itself: without this,
            // undo restores the components while the group's own member list stays as the rebuild left
            // it. Verified at the gate, not assumed here.
            if (target != null) actions.Add(new GH_GenericObjectAction(target));

            // #625: EVERY INPUT OUTSIDE THE REMOVAL THAT A REMOVED OBJECT FEEDS, snapshotted BEFORE the removal
            // cuts it. The removal's own undo action restores the objects; nothing guaranteed it restored the
            // consumers' sources, so a revert could put the components back and leave what they fed disconnected
            // (#623's half-revert, one wire further out). With the snapshot in the same record, the revert is
            // whole by construction, whatever GH_RemoveObjectAction does. Such a cut is not yet declarable, so
            // the verdict reverts it (BuildPreservation keeps the wire in its baseline, #643).
            foreach (var consumer in ConsumersOutside(d, replacing))
              if (snapshotted.Add(consumer)) actions.Add(new GH_WireAction(consumer));

            foreach (var guid in replacing)
            {
              var old = d.FindObject(guid, false);
              if (old == null) continue;
              actions.Add(new GH_RemoveObjectAction(old));
              d.RemoveObject(old, false);
            }
            // #623: the ledger is NOT touched here. This is inside the mutation and before the verdict, and
            // every failure path reverts with an undo that restores these components under their own guids.
            // Only a verified commit writes the ledger (CommitBuild, below), and it forgets nothing.
          }
          else if (target != null && spec.Components.Count > 0)
          {
            // Membership changes on a fresh build into a region too.
            actions.Add(new GH_GenericObjectAction(target));
          }

          var anchor = AnchorFor(d, spec, ids, target);

          // ---- 3a. INSTANTIATE AND CONFIGURE EVERYTHING BEFORE ANYTHING ENTERS THE DOCUMENT (U77) ----------------------
          // A script's source and ports, and then its type hints, are settled while the component is still only in memory.
          // Selecting a hint on a Rhino 8 script input REPLACES the parameter, so doing it after AddObject or after a wire
          // left the recorded undo actions pointing at a parameter that no longer existed, and the revert raised
          // Grasshopper's modal "Undo failed: Parameter could not be found". Nothing below this block touches the canvas
          // until every component exists, so a throw here rolls back with nothing placed.
          var built = new List<KeyValuePair<BuildComponent, IGH_DocumentObject>>();
          var placed = 0;
          foreach (var c in spec.Components)
          {
            var obj = Create(c.Type);
            if (obj == null) throw new InvalidOperationException($"could not instantiate \"{c.Type}\"");
            if (!string.IsNullOrWhiteSpace(c.Nick)) { try { obj.NickName = c.Nick; } catch { } }
            try { obj.CreateAttributes(); } catch { }
            // Only a first draw: the tidy replaces it for a map region (#239), and Land's measured rows for a spot (Brief DK).
            try { obj.Attributes.Pivot = new PointF(anchor.X + 140f * (placed % 6), anchor.Y + 90f * (placed / 6)); } catch { }

            // U19 (#898): a script's source and appended ports, set INSIDE the build, before the verdict. A failure throws
            // into the rollback below with the canvas untouched.
            ApplyScript(obj, c);

            created[c.Ref] = obj;
            built.Add(new KeyValuePair<BuildComponent, IGH_DocumentObject>(c, obj));
            placed++;
          }

          // U74/U77: each script input takes the hint its wired sources imply, chosen now (see above).
          foreach (var kv in built)
            if (kv.Key.CarriesSource && kv.Value is IGH_Component scComp)
            {
              var isCSharp = kv.Key.Language == BuildScriptTypes.CSharp;
              var hints = Dwight.Simplify.ScriptTypeMap.HintsByInput(
                ScriptInputWires(d, spec, created, ids, kv.Key, scComp),
                isCSharp ? Dwight.Simplify.ScriptLanguage.CSharp : Dwight.Simplify.ScriptLanguage.Python3);
              Dictionary<int, string> outputHints = null;
              if (isCSharp)
              {
                // U88: the types the model declared in its own RunScript choose the hints, so the component hands the method
                // what the method declares. The generated signature and any unmapped type are kept for the verdict.
                var inNames = scComp.Params.Input.Select(DwightGraph.ParamLabel).ToList();
                var outNames = Dwight.Simplify.ScriptComponentShim.ScriptOutputs(scComp).Select(DwightGraph.ParamLabel).ToList();
                var accessOf = inNames.Select((n, i) => (n, i)).ToDictionary(p => p.i, p => PortAccess.Of(kv.Key.InputAccess, p.n));
                var plan = Dwight.Simplify.CSharpSignature.Plan(Dwight.Simplify.CSharpSignature.Parse(kv.Key.Source), inNames, outNames, hints, accessOf);
                hints = plan.InputHints;
                outputHints = plan.OutputHints;
                kv.Key.HintNotes = plan.Notes;
                kv.Key.GeneratedSignature = Dwight.Simplify.CSharpSignature.Generated(inNames, plan.InputHints, outNames, plan.OutputHints, accessOf);
              }
              Dwight.Simplify.ScriptComponentShim.SelectInputHints(scComp, hints);
              ApplyDeclaredAccess(scComp, kv.Key);   // Brief CP: selecting a hint can replace the parameter (U77), so the declaration is set and read back again here
              if (outputHints != null) Dwight.Simplify.ScriptComponentShim.SelectOutputHints(scComp, outputHints);
            }

          // Brief CM: what a landing reads, before anything of this build is on the canvas. A map region's build is the tidy's to place, once the run is over.
          kit.FramesItself = target == null || landsInto;
          // Brief DK: a spot has no tidy, so its block is packed here, and that holds for a rebuild into a spot's own group as well as for a first landing.
          var packs = kit.FramesItself || ScaffoldAddress.IsSpot(spec.Region);
          var landingFacts = packs ? ReadLandingFacts(d, spec, ids, target, kept) : null;

          // ---- 3b. NOW THEY ENTER THE DOCUMENT, each with its undo action ---------------------------------------------
          foreach (var kv in built)
          {
            var c = kv.Key;
            var obj = kv.Value;

            d.AddObject(obj, false, d.Objects.Count);
            actions.Add(new GH_AddObjectAction(obj));   // identity exists from here; undo covers it

            // #377: the declared initial state, applied AFTER AddObject so nothing the document does on
            // insertion can quietly undo it. No separate undo action is owed - the object itself is new,
            // so the GH_AddObjectAction above already takes the whole thing away.
            GhBuildState.Apply(obj, c.State);

            // Into the region's group, so the address keeps meaning something and Stage D's re-layout has
            // a membership to lay out. The arrangement above is deliberately a plain grid INSIDE the
            // region: #239 ruled that the re-layout after a build is what makes it read well, and laying
            // out twice would discard the first result.
            if (target != null) { try { target.AddObject(obj.InstanceGuid); } catch { } }
          }

          // A GROUPED SPOT'S BOX IS MADE BY THE BUILD THAT FILLS IT (U18/#770).
          //
          // Unlike a map region, a spot has no group until now - the user approved one on the card and
          // this is where it appears. It is created INSIDE the same undo record as the components, so the
          // card's promise of one Ctrl-Z covers the box as well as its contents.
          //
          // The tick is read from the TURN'S PERMISSION, which the user's click set. The model cannot
          // see it, name it, or bias it - a `grouped` field on the spec would have rebuilt #346 exactly.
          // Unticked, nothing is created and the placement stands loose, which is the whole point of the
          // choice: the record is the enforcement, the group is only a view of it.
          GH_Group spotGroup = null;
          if (target == null && ledger.SpotIsGrouped(spec.Region) && created.Count > 0)
            spotGroup = MakeSpotGroup(d, spec.Region, created.Values, actions);
          kit.BlockGroup = spotGroup ?? target;

          // #625: the declared reconnections, carried out BEFORE the ordinary wires so each cut source goes back
          // at its recorded position and any new appends land after it. Ascending position per input restores
          // every position, because each earlier gap is refilled before a later one is inserted.
          var predicted = Reconnect(d, planned, created, actions, snapshotted);

          var wired = WireAll(d, spec, created, ids, actions, snapshotted);

          // Brief CM: WHERE IT LANDS, with what landed measured. Brief DP: after the wires, because what is an input and what is an output is read from them.
          if (packs) Land(landingFacts, built, kit);

#if DEBUG
          // The drill: touch something pre-existing, which preservation must catch and revert.
          if (SabotageNextBuild) Sabotage(d, created);
#endif

          // RecordEvent is the LAST statement in the try: before it, a throw rolls back manually with no
          // undo event; after it, the whole change is exactly one Ctrl-Z.
          kit.Fill(target, created.Values, snapshotted);
          // U91: EVERYTHING THE BUILD ADDS GOES IN THE KIT. The spot group was made inside the undo record but not named in the kit, so the guarded
          // revert (which removes the kit's objects by hand when Grasshopper's own Undo would raise a breakpoint) left it standing.
          kit.AddSpotGroup(spotGroup);
          // Brief AP: the record's id, kept at the source. Brief BQ: this region's sentence; a spot's is the card's own, which is what ran.
          Dwight.Session.ChangeLog.NoteChat(d, d.UndoUtil.RecordEvent(UndoName, actions), UndoName,
            ledger.IsApprovedSpot(spec.Region) ? null : ConsentCard.BuildDone(spec.Region, replacing.Count > 0));
          committed = true;
          recorded = true;

          // AN ATTEMPT IS A BUILD THAT ACTUALLY RAN (#352 constraint 4). Counted here - after the
          // canvas has been changed and before it is judged - so a spec refused at validation never
          // spends the region's budget, and an attempt that FAILS its verdict still does. A rebuild
          // counts exactly like a first build; otherwise a model that can always replace its own
          // work loops until the turn's step ceiling stops it, paying for every attempt.
          ledger.CountAttempt(spec.Region);

          // ---- 4. solve, then verify ---------------------------------------------------------------
          verifying = true;
          StopStartWatch();   // the verify wait has its own watchdog from here
          Verify(d, ids, ledger, spec, created, replacing, recognisedBefore, planned, predicted, consumersBefore,
                 before, baselineErrors, wired, kit, done);
        }
        catch (Exception ex)
        {
          if (!committed) CanvasWriter.RollbackOrRecord(d, actions, UndoName);
          else UndoOnce(d, kit);
          var stoppedMeasured = Measure(d, kit);
#if DEBUG
          RestoreSabotage();
#endif
          // U34: a throw here is a wire, reconnection or type that did not resolve - completeness, before the check.
          done?.Invoke(ToolResult.Fail(ToolErrors.VerificationFailed, RevertMeasure.Apply(BuildFailureDiagnosis.StoppedBefore(ex.Message), stoppedMeasured),
            new JsonObject { ["failedLeg"] = BuildFailureDiagnosis.Completeness }));
        }
      });
    }

#if DEBUG
    /// <summary>
    /// Corrupt one PRE-EXISTING component so preservation has something real to catch.
    ///
    /// THE DRILL FORCES ITS OWN PRECONDITION (#209). On a canvas with nothing pre-existing there is
    /// nothing to damage, preservation is never challenged, and the build passes honestly - so the drill
    /// must report that it did NOT fire. It previously set its fired flag before looking for a victim,
    /// which printed "the sabotage drill fired" over a build that was never tested: a guard reported as
    /// exercised on an unexercised path, which is the failure mode the drill exists to rule out.
    /// </summary>
    private static void Sabotage(GH_Document d, Dictionary<string, IGH_DocumentObject> created)
    {
      SabotageNextBuild = false;
      _sabotaged = null;
      LastBuildSabotaged = false;

      var victim = d.Objects.FirstOrDefault(o => o is IGH_ActiveObject && !created.Values.Contains(o)) as IGH_ActiveObject;
      if (victim == null)
      {
        LastSabotageNote = "The drill did NOT fire - this document holds no pre-existing component to corrupt, " +
                           "so preservation was never challenged and this build passed on its own merits. " +
                           "Put anything on the canvas (a Panel will do) and arm it again.";
        return;
      }

      try
      {
        _sabotagedWasLocked = victim.Locked;
        victim.Locked = !victim.Locked;
        _sabotaged = victim;
        LastBuildSabotaged = true;
        LastSabotageNote = "The drill toggled Locked on a pre-existing \"" + (victim.Name ?? "component") +
                           "\". Preservation must catch it and REVERT.";
      }
      catch (Exception ex)
      {
        LastSabotageNote = "The drill could not corrupt anything (" + ex.Message + "), so nothing was tested.";
      }
    }

    /// <summary>
    /// #623's witness, run as a REBUILD. When the drill caught a rebuild and reverted it, say how many of its
    /// own components Dwight recognised in the region before the attempt and after the revert - read back
    /// through the same Resolve the next attempt will run, against the canvas the revert restored. Equal
    /// means the record survived the revert; fewer after is #623 returning.
    ///
    /// Written to the command line as well as the harness note, because a chat rebuild never reaches the
    /// harness and that is where #623 was seen.
    /// </summary>
    private static void NoteRecordAcrossRevert(GH_Document doc, BuildLedger ledger, string region,
                                               int replacedCount, int recognisedBefore)
    {
      if (!LastBuildSabotaged || replacedCount == 0 || string.IsNullOrEmpty(region) || ledger == null) return;

      var after = -1;
      try
      {
        if (GhScaffoldMap.TryFindGroup(doc, region, out var group, out _, out _))
        {
          // int.MaxValue: the count is about the RECORD, and this turn's attempt budget must not hide it.
          var claim = ledger.Resolve(region, AsLeftNow(doc, GhScaffoldMap.ComponentsIn(doc, group)), int.MaxValue);
          after = claim.Remove.Count + claim.Adopted.Count;
        }
      }
      catch { }

      var line = "The drill caught a REBUILD of " + region + " and reverted it. Dwight's own components it " +
                 "recognises in " + region + ": " + recognisedBefore + " before this attempt, " +
                 (after < 0 ? "UNREADABLE" : after.ToString()) + " after the revert" +
                 (after == recognisedBefore ? " - the record survived the revert."
                  : " - THE RECORD DID NOT SURVIVE THE REVERT (#623).");

      LastSabotageNote = (LastSabotageNote ?? "") + "\n        " + line;
      try { Rhino.RhinoApp.WriteLine("Dwight drill: " + line); } catch { }
    }

    /// <summary>Put the victim back. The drill's damage sits deliberately OUTSIDE the recorded undo
    /// actions - that is what makes it a real test of preservation rather than of undo - so one Ctrl-Z
    /// does not cover it and the drill has to clean up after itself. A drill that left the canvas
    /// damaged would disprove the very claim it exists to demonstrate.</summary>
    private static void RestoreSabotage()
    {
      try { if (_sabotaged != null) _sabotaged.Locked = _sabotagedWasLocked; } catch { }
      _sabotaged = null;
    }
#endif

    /// <summary>Arm the after-solve read, verify the trio, and revert if any leg fails.</summary>
    private static void Verify(GH_Document doc, AgentIdRegistry ids, BuildLedger ledger, BuildSpec spec,
      Dictionary<string, IGH_DocumentObject> created, IReadOnlyList<Guid> replaced, int recognisedBefore,
      IReadOnlyList<PlannedReconnection> planned, IReadOnlyList<PredictedReconnection> predicted,
      IReadOnlyDictionary<Guid, string> consumersBefore,
      FpDoc before, HashSet<string> baselineErrors, int wired, RevertKit kit, Action<ToolResult> done)
    {
      GH_Document.SolutionEndEventHandler handler = null;
      Eto.Forms.UITimer timeout = null;
      var fired = false;
      var reverted = false;   // this verify has already undone the build (U47)

      void Finish(Action body)
      {
        if (fired) return;
        fired = true;
        try { if (handler != null) doc.SolutionEnd -= handler; } catch { }
        try { timeout?.Stop(); } catch { }
        body();
      }

      handler = (s, e) => Finish(() =>
      {
        try
        {
          var createdGuids = new HashSet<Guid>(created.Values.Select(o => o.InstanceGuid));
          var after = BehaviourFingerprint.ReadDoc(doc);
          // A REBUILD is judged against the definition WITHOUT the objects it removed (#352): the replaced
          // set is the resolved claim's Remove (#631). #643: OBJECTS and wire SOURCES are separate sets - a
          // wire from a new component carries its OUTPUT param's guid, read here off the live params.
          var preservation = BuildPreservation.Check(before, after, new BuildIdentity
          {
            CreatedObjects = createdGuids,
            CreatedSources = new HashSet<Guid>(created.Values.SelectMany(SourceGuidsOf)),
            ReplacedObjects = new HashSet<Guid>(replaced),
            // #625: the declared reconnections as carried out - the predicted delta.
            Reconnections = predicted,
          });

          var newErrors = new JsonArray();
          foreach (var key in ErrorKeys(doc).Except(baselineErrors).OrderBy(k => k, StringComparer.Ordinal))
            newErrors.Add(key);

          // Completeness reads off what was actually instantiated and wired - it needs no registry ids,
          // so the decision can be made BEFORE the registry is touched (#206).
          //
          // #377 JOINS THIS LEG RATHER THAN BECOMING A FOURTH ONE, and that is the honest place for it.
          // The existing legs already say "nothing unintended changed" (preservation) and "the intended
          // change happened" (completeness); a slider that was placed and wired but never took the value
          // it declared is precisely an INCOMPLETE intended change - the same shape as the inert-no-op
          // class the Phase-7 completeness guard was built for. Read back AFTER the solve, so anything
          // AddObject or the solve did to the component is inside the compared window.
          var stateMismatch = FirstStateMismatch(spec, created);
          // #625: every declared reconnection carried out is part of the intended change too.
          var complete = created.Count == spec.Components.Count && wired == spec.Wires.Count
                         && (predicted?.Count ?? 0) == (planned?.Count ?? 0)
                         && stateMismatch == null;
          var ok = preservation.Preserved && complete;

          // U19 (#898): THE SCRIPT LEG - errors first, then the three-state emptiness oracle. Read after the solve, off
          // the components the build placed. A read that throws is the outer catch's: no leg judged, build reverted.
          var scriptFacts = ScriptFactsOf(spec, created);
          var scriptJudgement = ScriptBuildVerdict.Judge(scriptFacts,
            errs => Dwight.Simplify.ToScriptCompileGate.ReflectedApiFor(new List<string>(errs)));

          if (!ok || !scriptJudgement.Stands)
          {
            // ---- 5. revert FIRST, then report. An unverified build never stands. -------------------
            var revertNote = UndoOnce(doc, kit);
            reverted = true;
            // U91: THE "AS IT WAS" SENTENCE IS MEASURED. What stands now against what stood before the build, with Dwight's own additions named.
            var measured = Measure(doc, kit);

            // #206: reconcile the registry against the state the CALLER will see. The revert removed
            // those components, so syncing the built state would knowingly leave the registry a step
            // ahead of the canvas - self-healing on the next read, but never knowingly wrong.
            ids.Sync(BehaviourFingerprint.ReadDoc(doc).Objects.Select(o => o.Guid));
#if DEBUG
            RestoreSabotage();
            NoteRecordAcrossRevert(doc, ledger, spec.Region, replaced.Count, recognisedBefore);
#endif

            // U34 (#925): WHICH LEG, ON WHAT. Named after the revert, against the restored canvas, so every guid the
            // preservation leg recorded is back where it was and can be called by its name and id.
            var failure = BuildFailureDiagnosis.Diagnose(preservation, (g, input) => LabelOf(doc, ids, g, input),
              created.Count, spec.Components.Count, wired, spec.Wires.Count,
              predicted?.Count ?? 0, planned?.Count ?? 0, stateMismatch);

            // U19: the structural legs keep their precedence, and a script that ALSO failed is said beside them - one
            // attempt should not have to find the second defect by spending another. A build that passed every
            // structural leg and failed only the script leg is refused in the script leg's own words.
            var message = failure != null ? BuildFailureDiagnosis.Message(failure) : scriptJudgement.Refusal;
            var note = failure != null ? BuildFailureDiagnosis.Note(failure) : scriptJudgement.Detail;
            if (revertNote != null) note = string.IsNullOrEmpty(note) ? revertNote : note + " " + revertNote;
            // U46: THE USER'S READING of the same refusal - the reflected RhinoCommon signatures are the model's repair
            // material and stay in `message`. Absent when the refusal has nothing reflected to leave out.
            var userMessage = failure != null ? BuildFailureDiagnosis.Message(failure) : scriptJudgement.UserRefusal;
            if (failure != null && !scriptJudgement.Stands)
            {
              message += " " + scriptJudgement.Detail;
              note += " " + scriptJudgement.Detail;
              userMessage += " " + scriptJudgement.UserDetail;
            }
            // U89: a failure on a script built from the card's text is the CARD's script failing, and the model that sent a
            // corrected one must be told so on the failure itself, not only on a success.
            var cardNote = ScriptSubstitution.NoteFor(spec.CardScripts);
            if (cardNote != null)
            {
              message += " " + cardNote;
              note = string.IsNullOrEmpty(note) ? cardNote : note + " " + cardNote;
              if (userMessage != null) userMessage += " " + cardNote;
            }
            // Every sentence written before the measurement is rewritten by it: none may claim what the measurement refused.
            message = RevertMeasure.Apply(message, measured);
            note = RevertMeasure.Apply(note, measured);
            if (userMessage != null) userMessage = RevertMeasure.Apply(userMessage, measured);
            if (string.Equals(userMessage, message, StringComparison.Ordinal)) userMessage = null;   // nothing to say differently

            // The revert already ran: from here a throw must NOT undo again (that would take back the user's own previous
            // step) - the catch below reports instead (U47).
            reverted = true;
            // U91: the verdict carries the measurement, so the running line, the persisted record and the next turn's fact say what was found.
            done?.Invoke(ToolResult.Fail(ToolErrors.VerificationFailed, message,
              WithMeasure(BuildVerdict.Build(false, new List<int>(new int[created.Count]), wired, spec.Wires.Count,
                preservation, spec.Components.Count, newErrors.Count, newErrors,
                note: note,
                region: spec.Region, replaced: replaced.Count,
                attemptsLeft: AttemptsLeft(ledger, spec.Region),
                failedLeg: failure?.Leg ?? ScriptBuildVerdict.Leg,
                userMessage: userMessage), measured)));
            return;
          }

          // Brief DK: the law again, now the solve has run. Here on the path that stands, because a revert has nothing left to arrange.
          SettleBlock(kit);

          // The built state is the state the caller will see - and `after` already holds it, so there
          // is no second read (#206).
          ids.Sync(after.Objects.Select(o => o.Guid));
          var placedIds = created.Values.Select(o => ids.Issue(o.InstanceGuid)).OrderBy(i => i).ToList();

          // THE LEDGER IS WRITTEN ONLY ON A BUILD THAT STOOD (#352 constraint 1). A reverted build left
          // nothing on the canvas, so claiming its guids would give a later rebuild a licence to delete
          // objects that do not exist - and, worse, a claim over ids the registry may later reissue.
          //
          // AND THE AS-LEFT TOKEN IS TAKEN HERE, which is the only honest moment for it. build_region
          // adds components and wires them AFTERWARDS, so a token captured at AddObject would record zero
          // sources - and every component would then read as CHANGED at the next rebuild, on a canvas
          // nobody had touched. An advisory line that is often wrong is not a weak feature; it destroys
          // the credibility of every other line beside it. Here, post-wire and post-solve, is the state
          // Dwight actually left the component in, which is the only thing "unchanged since Dwight placed
          // it" can honestly mean.
          // #623/#632: the ledger's ONLY writer, on the verified path only - and it forgets nothing. What this
          // rebuild replaced stays recorded as dormant facts, so a user's Ctrl-Z of it resolves to the old set.
          ledger.CommitBuild(spec.Region, AsLeftStatesNow(doc, created.Values.Select(o => o.InstanceGuid)));

          // #625/#648(v): Dwight's own reconnection changed each consumer's sources, so its as-left record follows -
          // COMPARE-AND-SET: only where the consumer was still exactly as Dwight left it immediately before this
          // build. One the user had already edited stays theirs; the ledger refuses to launder it.
          foreach (var consumer in consumersBefore ?? new Dictionary<Guid, string>())
          {
            var now = AsLeftStatesNow(doc, new[] { consumer.Key });
            if (now.TryGetValue(consumer.Key, out var state)) ledger.RefreshAsLeft(consumer.Key, consumer.Value, state);
          }
#if DEBUG
          RestoreSabotage();   // a drill that fired and was NOT caught still leaves the canvas clean
#endif

          // U26: what the placed components hold NOW, read after the solve by the geometry reads' own reader.
          // Never a reason to revert - a fact the line carries, in the units the document is in.
          var placedFacts = PlacedFactsOf(ids, spec, created);
          foreach (var pf in placedFacts)
            if (pf.Ref != null && scriptJudgement.Notes.TryGetValue(pf.Ref, out var scriptNote))
            {
              pf.ScriptNote = scriptNote;
              pf.ScriptOutcome = scriptJudgement.Outcomes.TryGetValue(pf.Ref, out var oc) ? oc.ToString().ToLowerInvariant() : null;
            }

          // Brief CM: framed only here, on a build that stands, so a revert never moves the view to what has gone.
          if (kit.FramesItself) CanvasNav.GoTo(created.Values.Select(o => o.InstanceGuid), NavReason.BuildJustLanded);

          done?.Invoke(ToolResult.Success(Landing.Stamped(StampCardScripts(BuildVerdict.Build(true, placedIds, wired, spec.Wires.Count,
            preservation, spec.Components.Count, newErrors.Count, newErrors,
            region: spec.Region, replaced: replaced.Count,
            attemptsLeft: AttemptsLeft(ledger, spec.Region),
            initialState: StateSet(spec, created),
            reconnected: ReconnectedPayload(planned),
            placedFacts: BuildPlacedFacts.ToJson(placedFacts),
            units: UnitAbbreviation()), spec.CardScripts), kit.LandedWhere)));
        }
        catch (Exception ex)
        {
          // U47: THE CHECK THREW (or the report that follows it did). Revert once - never twice, which would undo the
          // user's own previous step - and ALWAYS tell the caller: an exception out of a SolutionEnd handler is
          // swallowed by Grasshopper without a word, which left a build standing and a click silent.
          if (!reverted) UndoOnce(doc, kit);
          reverted = true;
          var threwMeasured = Measure(doc, kit);
          try { done?.Invoke(ToolResult.Fail(ToolErrors.VerificationFailed, RevertMeasure.Apply(BuildFailureDiagnosis.CheckThrew(ex.Message), threwMeasured))); }
          catch (Exception ex2) { try { Rhino.RhinoApp.WriteLine("Dwight: the build's report threw - " + ex2.Message); } catch { } }
        }
      });

      try
      {
        doc.SolutionEnd += handler;
        timeout = new Eto.Forms.UITimer { Interval = WatchdogMs / 1000.0 };
        timeout.Elapsed += (s, e) => Finish(() =>
        {
          UndoOnce(doc, kit);   // unverifiable is treated exactly like failed
          done?.Invoke(ToolResult.Fail(ToolErrors.VerificationFailed, RevertMeasure.Apply(BuildFailureDiagnosis.SolveTimedOut(WatchdogMs), Measure(doc, kit)),
            new JsonObject { ["failedLeg"] = BuildFailureDiagnosis.SolveHealth }));
        });
        timeout.Start();
        doc.ScheduleSolution(10);
      }
      catch (Exception ex)
      {
        Finish(() =>
        {
          UndoOnce(doc, kit);
          done?.Invoke(ToolResult.Fail(ToolErrors.VerificationFailed, RevertMeasure.Apply(BuildFailureDiagnosis.CheckThrew(ex.Message), Measure(doc, kit))));
        });
      }
    }

    /// <summary>
    /// What a guid the preservation leg recorded is CALLED (U34): "Line SDL (23), input L" - the component's nickname
    /// where it has one, its type, its agent id, and the input's label when the breach is on one. Read after the revert,
    /// so the object is back on the canvas. Null when it cannot be read; the diagnosis then says "an existing component"
    /// rather than printing a guid.
    /// </summary>
    private static string LabelOf(GH_Document doc, AgentIdRegistry ids, Guid objectGuid, Guid? inputGuid)
    {
      try
      {
        var obj = doc?.FindObject(objectGuid, true);
        if (obj == null) return null;
        var type = obj.Name ?? "component";
        var nick = obj.NickName;
        var name = string.IsNullOrWhiteSpace(nick) || string.Equals(nick, type, StringComparison.Ordinal)
          ? type
          : type + " \"" + nick + "\"";
        try { name += " (" + ids.Issue(obj.InstanceGuid) + ")"; } catch { }

        if (inputGuid.HasValue && inputGuid.Value != objectGuid && obj is IGH_Component c)
        {
          var input = c.Params.Input.FirstOrDefault(i => i.InstanceGuid == inputGuid.Value);
          if (input != null) name += ", input " + DwightGraph.ParamLabel(input);
        }
        return name;
      }
      catch { return null; }
    }

    /// <summary>U77: for one script, (input index, GH type name of its source) for every spec wire into it, read BEFORE the
    /// script is added to the document. A source whose type cannot be read is "" and leaves that input's hint alone.</summary>
    private static List<KeyValuePair<int, string>> ScriptInputWires(GH_Document d, BuildSpec spec,
      Dictionary<string, IGH_DocumentObject> created, AgentIdRegistry ids, BuildComponent script, IGH_Component comp)
    {
      var list = new List<KeyValuePair<int, string>>();
      try
      {
        foreach (var w in spec.Wires)
        {
          if (!ReferenceEquals(Resolve(d, w.ToRef, w.ToId, created, ids), comp)) continue;
          var target = InputParam(comp, w.ToParam);
          var index = target == null ? -1 : comp.Params.Input.IndexOf(target);
          if (index < 0) continue;
          var source = OutputParam(Resolve(d, w.FromRef, w.FromId, created, ids), w.FromParam);
          string typeName = null;
          try { typeName = source?.TypeName; } catch { }
          list.Add(new KeyValuePair<int, string>(index, typeName ?? ""));
        }
      }
      catch { }
      return list;
    }

    /// <summary>Wire the spec. A <c>GH_WireAction</c> snapshot is taken BEFORE each input is touched, so
    /// one undo restores the original sources of every input this build wrote to.</summary>
    private static int WireAll(GH_Document d, BuildSpec spec, Dictionary<string, IGH_DocumentObject> created,
      AgentIdRegistry ids, List<IGH_UndoAction> actions, HashSet<IGH_Param> snapshotted)
    {
      var wired = 0;

      foreach (var w in spec.Wires)
      {
        var source = OutputParam(Resolve(d, w.FromRef, w.FromId, created, ids), w.FromParam);
        var target = InputParam(Resolve(d, w.ToRef, w.ToId, created, ids), w.ToParam);

        // Never skip silently. A skipped wire used to surface as "the build did not place everything it
        // declared" - safe, because completeness still reverted it, but the wrong DIAGNOSIS for what is
        // actually a port that does not exist. Throwing rolls back the same way and says the true thing.
        if (source == null || target == null)
          throw new InvalidOperationException(
            "a wire's \"" + (source == null ? "from" : "to") + "\" port could not be resolved" +
            (source == null ? Where(w.FromParam) : Where(w.ToParam)));

        if (snapshotted.Add(target)) actions.Add(new GH_WireAction(target));
        target.AddSource(source);
        wired++;
      }
      return wired;
    }

    /// <summary>
    /// EVERY WIRE THIS REMOVAL WOULD CUT (#625), read before anything is removed: each removed object's output,
    /// each recipient whose top-level owner the removal does not include. The consumer is given a registry id so
    /// the model can name it, and its region when a scaffold group holds it.
    ///
    /// A recipient that is not a top-level object of this canvas - inside a cluster, or with no readable owner -
    /// is recorded UNREADABLE and refused by <see cref="RebuildBoundary.Match"/>, never guessed at: cluster
    /// internals are not in the fingerprint, so no verdict could check a reconnection there.
    /// </summary>
    private static List<CutWire> CutWiresOf(GH_Document doc, AgentIdRegistry ids, IReadOnlyCollection<Guid> removing)
    {
      var cuts = new List<CutWire>();
      var removal = new HashSet<Guid>(removing ?? new Guid[0]);
      if (removal.Count == 0 || doc == null) return cuts;

      Dictionary<Guid, string> regionOf = null;
      foreach (var guid in removal)
      {
        IGH_DocumentObject obj;
        try { obj = doc.FindObject(guid, false); } catch { continue; }
        if (obj == null) continue;

        foreach (var output in OutputParamsOf(obj))
        {
          List<IGH_Param> recipients;
          try { recipients = output.Recipients?.ToList() ?? new List<IGH_Param>(); } catch { continue; }

          foreach (var r in recipients)
          {
            if (r == null) continue;
            IGH_DocumentObject owner = null;
            try { owner = r.Attributes?.GetTopLevel?.DocObject; } catch { }
            if (owner != null && removal.Contains(owner.InstanceGuid)) continue;   // inside the removal: no cut

            IGH_DocumentObject topLevel = null;
            try { if (owner != null) topLevel = doc.FindObject(owner.InstanceGuid, true); } catch { }

            var position = -1;
            try { position = r.Sources.IndexOf(output); } catch { }

            var unreadable = topLevel == null || position < 0;
            if (!unreadable && regionOf == null) regionOf = RegionMembership(doc);

            cuts.Add(new CutWire
            {
              ConsumerGuid = topLevel?.InstanceGuid ?? Guid.Empty,
              ConsumerId = unreadable ? 0 : ids.Issue(topLevel.InstanceGuid),
              ConsumerType = topLevel?.Name,
              ConsumerRegion = !unreadable && regionOf.TryGetValue(topLevel.InstanceGuid, out var at) ? at : null,
              InputGuid = r.InstanceGuid,
              InputLabel = topLevel is IGH_Param ? null : DwightGraph.ParamLabel(r),
              Position = Math.Max(0, position),
              SourceGuid = output.InstanceGuid,
              SourceType = obj.Name,
              SourceOutputLabel = obj is IGH_Param ? null : DwightGraph.ParamLabel(output),
              Unreadable = unreadable,
            });
          }
        }
      }
      return cuts;
    }

    /// <summary>Which scaffold region each component on the canvas sits in, by guid.</summary>
    private static Dictionary<Guid, string> RegionMembership(GH_Document doc)
    {
      var map = new Dictionary<Guid, string>();
      try
      {
        foreach (var region in GhScaffoldMap.CanvasRegions(doc))
        {
          if (!GhScaffoldMap.TryFindGroup(doc, region.Address, out var group, out _, out _)) continue;
          foreach (var g in GhScaffoldMap.ComponentsIn(doc, group))
            if (!map.ContainsKey(g)) map[g] = region.Address;
        }
      }
      catch { }
      return map;
    }

    /// <summary>
    /// Carry out the declared reconnections (#625) and return what the verdict must predict. Each cut input was
    /// already snapshotted before the removal (ConsumersOutside), so this is inside the one undo record. A
    /// reconnection that cannot be resolved THROWS, which rolls the whole build back with the true reason - a
    /// silently skipped reconnection would leave exactly the disconnect this unit exists to prevent.
    /// </summary>
    private static List<PredictedReconnection> Reconnect(GH_Document d, IReadOnlyList<PlannedReconnection> planned,
      Dictionary<string, IGH_DocumentObject> created, List<IGH_UndoAction> actions, HashSet<IGH_Param> snapshotted)
    {
      var predicted = new List<PredictedReconnection>();
      foreach (var p in planned ?? new PlannedReconnection[0])
      {
        var input = InputParamByGuid(d, p.Cut.ConsumerGuid, p.Cut.InputGuid);
        var from = created.TryGetValue(p.Declared.FromRef ?? "", out var obj) ? OutputParam(obj, p.Declared.FromParam) : null;
        if (input == null || from == null)
          throw new InvalidOperationException("a reconnection into " + RebuildBoundary.Describe(p.Cut, false) +
                                              " could not be resolved on the canvas");

        if (snapshotted.Add(input)) actions.Add(new GH_WireAction(input));
        try { if (input.Sources.Any(s => s?.InstanceGuid == p.Cut.SourceGuid)) input.RemoveSource(p.Cut.SourceGuid); } catch { }
        input.AddSource(from, Math.Min(p.Cut.Position, input.SourceCount));

        predicted.Add(new PredictedReconnection
        {
          InputGuid = p.Cut.InputGuid, Position = p.Cut.Position,
          // A restore has no old source in the baseline - it was already lost - so the verdict predicts an insert.
          OldSource = p.Cut.Restore ? Guid.Empty : p.Cut.SourceGuid,
          NewSource = from.InstanceGuid,
        });
      }
      return predicted;
    }

    /// <summary>One input param, by its owner and its own guid - the owner itself for a floating param.</summary>
    private static IGH_Param InputParamByGuid(GH_Document d, Guid ownerGuid, Guid inputGuid)
    {
      try
      {
        var owner = d.FindObject(ownerGuid, true);
        if (owner is IGH_Param p) return p.InstanceGuid == inputGuid ? p : null;
        if (owner is IGH_Component c) return c.Params.Input.FirstOrDefault(i => i.InstanceGuid == inputGuid);
      }
      catch { }
      return null;
    }

    /// <summary>What the verdict reports as reconnected - the plugin's own labels and ids.</summary>
    private static JsonArray ReconnectedPayload(IReadOnlyList<PlannedReconnection> planned) =>
      new JsonArray((planned ?? new PlannedReconnection[0]).Select(p => (JsonNode)new JsonObject
      {
        ["consumer"] = p.Cut.ConsumerId,
        ["consumerType"] = p.Cut.ConsumerType ?? "",
        ["consumerRegion"] = p.Cut.ConsumerRegion ?? "",
        ["input"] = p.Cut.InputLabel ?? "",
        ["restored"] = p.Cut.Restore,
      }).ToArray());

    /// <summary>
    /// #625's NARROW SIBLING: every input on a component Dwight placed (any region) whose ONLY change since Dwight
    /// left it is one source lost from <paramref name="region"/>'s earlier components - read before a fresh build of
    /// that region. The diff is <see cref="RestorableLoss.Find"/>'s; this only reads the canvas and the ledger.
    /// </summary>
    private static List<CutWire> RestorableLossesOf(GH_Document doc, AgentIdRegistry ids, BuildLedger ledger, string region)
    {
      var found = new List<CutWire>();
      if (doc == null || ids == null || ledger == null || string.IsNullOrEmpty(region)) return found;

      var dormant = ledger.DormantOutputs(region, g => { try { return doc.FindObject(g, false) != null; } catch { return true; } });
      if (dormant.Count == 0) return found;

      Dictionary<Guid, string> regionOf = null;
      foreach (var candidate in ledger.RestoreCandidates())
      {
        try
        {
          var obj = doc.FindObject(candidate.Guid, true);
          if (obj == null) continue;
          var loss = RestorableLoss.Find(candidate.State, BehaviourFingerprint.ReadObject(obj), obj.NickName,
                                         StateToken.Of(GhBuildState.Read(obj)), dormant);
          if (loss == null) continue;

          var input = obj is IGH_Param own ? own
            : (obj as IGH_Component)?.Params.Input.FirstOrDefault(i => i.InstanceGuid == loss.InputGuid);
          if (input == null) continue;

          regionOf = regionOf ?? RegionMembership(doc);
          found.Add(new CutWire
          {
            ConsumerGuid = candidate.Guid,
            ConsumerId = ids.Issue(candidate.Guid),
            ConsumerType = obj.Name,
            ConsumerRegion = regionOf.TryGetValue(candidate.Guid, out var at) ? at : null,
            InputGuid = loss.InputGuid,
            InputLabel = obj is IGH_Param ? null : DwightGraph.ParamLabel(input),
            Position = loss.Position,
            SourceGuid = loss.LostSource,
            Restore = true,
            FromRegion = region,
          });
        }
        catch { }
      }
      return found;
    }

    /// <summary>
    /// The params a wire FROM this object carries as its source (#643): a component's OUTPUT params, or a floating
    /// param itself. Never the component's own guid - a wire never carries that.
    /// </summary>
    private static IEnumerable<Guid> SourceGuidsOf(IGH_DocumentObject obj)
    {
      try
      {
        if (obj is IGH_Param p) return new[] { p.InstanceGuid };
        if (obj is IGH_Component c) return c.Params.Output.Select(o => o.InstanceGuid).ToList();
      }
      catch { }
      return Enumerable.Empty<Guid>();
    }

    /// <summary>The output params of one object - the params whose Recipients are the wires it feeds.</summary>
    private static IEnumerable<IGH_Param> OutputParamsOf(IGH_DocumentObject obj)
    {
      try
      {
        if (obj is IGH_Param p) return new[] { p };
        if (obj is IGH_Component c) return c.Params.Output.ToList();
      }
      catch { }
      return Enumerable.Empty<IGH_Param>();
    }

    /// <summary>
    /// Every input param OUTSIDE the removal that one of the removed objects feeds (#625) - read before anything
    /// is removed. "Outside" is by the recipient's top-level owner; a recipient whose owner cannot be read is
    /// included, because the safe direction for a snapshot is to cover too much.
    /// </summary>
    private static List<IGH_Param> ConsumersOutside(GH_Document d, IReadOnlyCollection<Guid> removing)
    {
      var removal = new HashSet<Guid>(removing ?? new Guid[0]);
      var consumers = new List<IGH_Param>();
      foreach (var guid in removal)
      {
        IGH_DocumentObject obj;
        try { obj = d.FindObject(guid, false); } catch { continue; }
        if (obj == null) continue;

        foreach (var output in OutputParamsOf(obj))
        {
          List<IGH_Param> recipients;
          try { recipients = output.Recipients?.ToList() ?? new List<IGH_Param>(); } catch { continue; }
          foreach (var r in recipients)
          {
            if (r == null || consumers.Contains(r)) continue;
            Guid owner;
            try { owner = r.Attributes?.GetTopLevel?.DocObject?.InstanceGuid ?? Guid.Empty; } catch { owner = Guid.Empty; }
            if (owner != Guid.Empty && removal.Contains(owner)) continue;
            consumers.Add(r);
          }
        }
      }
      return consumers;
    }

    private static IGH_DocumentObject Resolve(GH_Document d, string byRef, int? byId,
      Dictionary<string, IGH_DocumentObject> created, AgentIdRegistry ids)
    {
      if (!string.IsNullOrEmpty(byRef)) return created.TryGetValue(byRef, out var o) ? o : null;
      if (byId.HasValue && ids.TryGetGuid(byId.Value, out var guid))
      { try { return d.FindObject(guid, true); } catch { return null; } }
      return null;
    }

    /// <summary>A readable list for a refusal that has to name what WAS approved.</summary>
    private static string Join(IReadOnlyList<string> items) =>
      items == null || items.Count == 0 ? "nothing" : string.Join(", ", items);

    private static string Where(string param) =>
      string.IsNullOrWhiteSpace(param) ? "" : " (\"" + param + "\")";

    /// <summary>The named output, and NOTHING ELSE when the name does not match. The old
    /// <c>?? Output[0]</c> fallback meant a model naming a port that does not exist got wired to the
    /// first one instead and read a verdict saying the build landed as specified - a silent mis-wire
    /// dressed as a success. A blank name reaches here only when the port shape was unknown at parse
    /// time, and then only a single-output component has an unambiguous answer.</summary>
    private static IGH_Param OutputParam(IGH_DocumentObject obj, string name)
    {
      if (obj is IGH_Param p) return p;
      if (!(obj is IGH_Component c) || c.Params.Output.Count == 0) return null;
      if (string.IsNullOrWhiteSpace(name)) return c.Params.Output.Count == 1 ? c.Params.Output[0] : null;
      return c.Params.Output.FirstOrDefault(x => Named(x, name));
    }

    private static IGH_Param InputParam(IGH_DocumentObject obj, string name)
    {
      if (obj is IGH_Param p) return p;
      if (!(obj is IGH_Component c) || c.Params.Input.Count == 0) return null;
      if (string.IsNullOrWhiteSpace(name)) return c.Params.Input.Count == 1 ? c.Params.Input[0] : null;
      return c.Params.Input.FirstOrDefault(x => Named(x, name));
    }

    /// <summary>The ports a type WILL have, read off a throwaway instance of it - ground truth from the
    /// SDK rather than a curated map (ADR-092), and the same instantiation path the build itself uses,
    /// so what is validated is what gets created. Cached per call: a spec places at most 40 components
    /// and usually repeats types.</summary>
    private static PortShape PortsOfType(string type, Dictionary<string, PortShape> cache)
    {
      if (cache.TryGetValue(type, out var known)) return known;
      PortShape shape = null;
      try { shape = ShapeOf(Create(type)); } catch { }
      cache[type] = shape;
      return shape;
    }

    /// <summary>The ports an existing component HAS, read live off the canvas.</summary>
    private static PortShape PortsOfId(GH_Document doc, AgentIdRegistry ids, int id)
    {
      try
      {
        if (!ids.TryGetGuid(id, out var guid)) return null;
        return ShapeOf(doc.FindObject(guid, true));
      }
      catch { return null; }
    }

    /// <summary>A param reports EMPTY port lists, not null: it genuinely has no named ports, which is a
    /// fact the parser acts on. Null is reserved for "could not be determined".</summary>
    private static PortShape ShapeOf(IGH_DocumentObject obj)
    {
      if (obj == null) return null;
      if (obj is IGH_Param) return new PortShape { Inputs = new string[0], Outputs = new string[0] };
      if (!(obj is IGH_Component c)) return null;
      return new PortShape
      {
        Inputs = c.Params.Input.Select(DwightGraph.ParamLabel).ToList(),
        Outputs = c.Params.Output.Select(DwightGraph.ParamLabel).ToList(),
        InputNames = c.Params.Input.Select(p => p.Name ?? "").ToList(),
        OutputNames = c.Params.Output.Select(p => p.Name ?? "").ToList(),
      };
    }

    private static bool Named(IGH_Param p, string name) =>
      string.Equals(DwightGraph.ParamLabel(p), name, StringComparison.OrdinalIgnoreCase) ||
      string.Equals(p.Name ?? "", name, StringComparison.OrdinalIgnoreCase);

    private static IGH_DocumentObject Create(string type)
    {
      try
      {
        var guid = ComponentWhitelist.GuidFor(type);
        if (!guid.HasValue || guid.Value == Guid.Empty) return null;
        return Instances.ComponentServer.EmitObjectProxy(guid.Value)?.CreateInstance();
      }
      catch { return null; }
    }

    /// <summary>
    /// Where the new components go.
    ///
    /// INTO THE REGION when one was named (WP-4 Stage B) - beneath its scribbles, so the header and body
    /// stay readable above the work. That is the whole point of aiming a build: a region's address has to
    /// mean a PLACE, not just a label on a record.
    ///
    /// The arrangement itself is a plain grid, deliberately: #239 ruled that the re-layout AFTER a build
    /// is what makes a region read well, because a group's box is derived from its members and the
    /// scaffold's box was sized for TEXT. Laying out here as well would compute an arrangement that the
    /// re-layout immediately discards.
    ///
    /// With no region, this is where the arrangement is first drawn, and <see cref="Land"/> then packs it from measured bounds and moves it to a clear place. It stands alone only if that measure fails.
    /// </summary>
    private static PointF AnchorFor(GH_Document d, BuildSpec spec, AgentIdRegistry ids, GH_Group target)
    {
      if (target != null)
      {
        try
        {
          // The group's own bounds, read live. A scaffold region's box holds two scribbles at this point,
          // so its bottom-left is clear space inside the region.
          var box = target.Attributes.Bounds;
          if (box.Width > 0 || box.Height > 0)
            return new PointF(box.Left + 20f, box.Bottom + 20f);
        }
        catch { }
      }

      try
      {
        var anchors = spec.Wires
          .Select(w => w.ToId ?? w.FromId)
          .Where(id => id.HasValue)
          .Select(id => ids.TryGetGuid(id.Value, out var g) ? d.FindObject(g, true) : null)
          .Where(o => o?.Attributes != null)
          .Select(o => o.Attributes.Pivot)
          .ToList();
        if (anchors.Count > 0) return new PointF(anchors.Max(p => p.X) + 220f, anchors.Min(p => p.Y));

        var all = d.Objects.Where(o => o?.Attributes != null).Select(o => o.Attributes.Pivot).ToList();
        if (all.Count > 0) return new PointF(all.Max(p => p.X) + 220f, all.Min(p => p.Y));
      }
      catch { }
      return new PointF(0, 0);
    }

    /// <summary>What a landing reads off the canvas before anything of this build is on it: what stands, what it wires to, the view, and the box a named group has before it grows.</summary>
    private sealed class LandingFacts
    {
      public List<SpaceBox> Canvas;
      public List<SpaceBox> FedBy;
      public List<SpaceBox> Feeding;
      public SpaceBox View;
      public SpaceBox Into;
      public List<SpaceBox> Kept;
      public List<IGH_DocumentObject> KeptObjects;
      public string AnchorLabel;
    }

    /// <summary>Null if the read fails, which costs the search and never the build: the first arrangement stands, unmeasured.</summary>
    private static LandingFacts ReadLandingFacts(GH_Document d, BuildSpec spec, AgentIdRegistry ids, GH_Group into, IReadOnlyList<Guid> kept)
    {
      try
      {
        SpaceBox BoxOf(Guid g)
        {
          var r = d.FindObject(g, true)?.Attributes?.Bounds;
          return r == null ? null : new SpaceBox("", r.Value.X, r.Value.Y, r.Value.Width, r.Value.Height, SpaceRole.Fixed);
        }
        SpaceBox BoxOfId(int? id) => id.HasValue && ids.TryGetGuid(id.Value, out var g) ? BoxOf(g) : null;

        var canvas = GhRegionTidy.MeasuredCanvas(d, into == null ? null : new HashSet<Guid> { into.InstanceGuid });
        // Brief DP: what stands in the place this lands in is what its columns continue: a group's members, or what a rebuild of a loose spot kept.
        IReadOnlyList<Guid> keptIds = into != null ? GhScaffoldMap.ComponentsIn(d, into) : kept;
        var wires = spec.Wires ?? new List<BuildWire>();
        var firstWired = wires.Select(w => w?.FromId).Concat(wires.Select(w => w?.ToId)).FirstOrDefault(id => BoxOfId(id) != null);
        var b = into?.Attributes.Bounds;
        return new LandingFacts
        {
          Canvas = canvas,
          FedBy = wires.Select(w => BoxOfId(w?.FromId)).Where(x => x != null).ToList(),
          Feeding = wires.Select(w => BoxOfId(w?.ToId)).Where(x => x != null).ToList(),
          View = ViewOf(d),
          Into = b == null ? null : new SpaceBox("", b.Value.X, b.Value.Y, b.Value.Width, b.Value.Height, SpaceRole.Fixed),
          Kept = keptIds.Select(BoxOf).Where(x => x != null).ToList(),
          KeptObjects = keptIds.Select(g => d.FindObject(g, true)).Where(o => o?.Attributes != null).ToList(),
          AnchorLabel = firstWired.HasValue ? LabelOfId(d, ids)(firstWired.Value) : null,
        };
      }
      catch { return null; }
    }

    /// <summary>
    /// BRIEF CM: A PLACEMENT NEVER LANDS ON EXISTING WORK. Measure what has just entered the document, as Simplify's <c>NudgeClear</c> does and with a slider's state applied, ask <see cref="Landing.Find"/>
    /// against what stood before it, and move it all there. These objects are new, so <c>GH_AddObjectAction</c> already takes them away, and a move needs no undo action of its own.
    /// BRIEF DK: AND NOTHING IT PLACES OVERLAPS. <see cref="SpotLayout"/> packs them by what they measure first, so the clear place is found for the block's real size.
    /// A failure costs the layout and never the build: the first arrangement stands, unmeasured.
    /// </summary>
    private static void Land(LandingFacts facts, IReadOnlyList<KeyValuePair<BuildComponent, IGH_DocumentObject>> built, RevertKit kit)
    {
      if (built.Count == 0) return;
      try
      {
        kit.Facts = facts;
        kit.Built = built;
        ReadColumns(kit);
        kit.Block = SpotLayout.Arrange(() => MeasureBlock(built), block => PlaceBlock(kit, block), kit.Roles);
        kit.LandedWhere = SayLanded(kit);
        kit.BlockGroup?.Attributes?.ExpireLayout();   // the group was made before its members found their places
      }
      catch { kit.Block = null; }
    }

    /// <summary>Each component as it is now, laid out first: a fresh component's bounds are not real until it has been (WP-D's <c>ParkParam</c> lesson).</summary>
    private static List<SpaceBox> MeasureBlock(IReadOnlyList<KeyValuePair<BuildComponent, IGH_DocumentObject>> built)
    {
      var boxes = new List<SpaceBox>();
      foreach (var kv in built)
      {
        var attributes = kv.Value.Attributes;
        attributes.ExpireLayout();
        attributes.PerformLayout();
        var b = attributes.Bounds;
        if (b.Width <= 0 || b.Height <= 0) b = new RectangleF(attributes.Pivot.X, attributes.Pivot.Y, 140f, 90f);
        boxes.Add(new SpaceBox("", b.X, b.Y, b.Width, b.Height, SpaceRole.Fixed));
      }
      return boxes;
    }

    /// <summary>Brief DP: what each new component is, read from the live wiring, with what stands in this place counted as inside the block, and the columns that already stand there, so a rebuild keeps them.</summary>
    private static void ReadColumns(RevertKit kit)
    {
      var kept = kit.Facts?.KeptObjects ?? new List<IGH_DocumentObject>();
      var built = kit.Built.Select(kv => kv.Value).ToList();
      var block = new HashSet<Guid>(built.Concat(kept).Select(o => o.InstanceGuid));

      var keptRoles = GhBlockRoles.Read(kept, block);
      kit.KeptInputs = kept.Where((_, i) => keptRoles[i] == BlockRole.Input).Select(BoxNow).ToList();
      kit.KeptOutputs = kept.Where((_, i) => keptRoles[i] == BlockRole.Output).Select(BoxNow).ToList();
      kit.Roles = BlockColumns.WithKept(GhBlockRoles.Read(built, block), kit.KeptInputs.Count > 0, kit.KeptOutputs.Count > 0);
    }

    private static SpaceBox BoxNow(IGH_DocumentObject o)
    {
      var b = o.Attributes.Bounds;
      return new SpaceBox("", b.X, b.Y, b.Width, b.Height, SpaceRole.Fixed);
    }

    /// <summary>
    /// Find the block's place for the size it is, and move each component by how far its measured box is from where the block puts it, so the distance from a pivot to its bounds is kept. A new input or output on a rebuild goes
    /// under its column first, so the block's search sees it there. With no facts there is no canvas to search: the block starts where the first arrangement did.
    /// </summary>
    private static void PlaceBlock(RevertKit kit, SpotBlock block)
    {
      var now = MeasureBlock(kit.Built);
      var facts = kit.Facts;
      void MoveTo(int i, double x, double y)
      {
        var attributes = kit.Built[i].Value.Attributes;
        attributes.Pivot = new PointF(attributes.Pivot.X + (float)(x - now[i].X), attributes.Pivot.Y + (float)(y - now[i].Y));
        attributes.ExpireLayout();
      }

      var canvas = (facts?.Canvas ?? new List<SpaceBox>()).ToList();
      void Below(BlockRole role, IReadOnlyList<SpaceBox> column)
      {
        if (block.Roles == null || column == null || column.Count == 0) return;
        var x = column.Min(b => b.X);
        var y = column.Max(b => b.Bottom) + SpotLayout.RowGap;
        for (var i = 0; i < kit.Built.Count; i++)
        {
          if (block.Roles[i] != role) continue;
          y = BlockColumns.NextFreeBelow(x, y, now[i].W, now[i].H, canvas);
          MoveTo(i, x, y);
          canvas.Add(new SpaceBox("", x, y, now[i].W, now[i].H, SpaceRole.Fixed));
          y += now[i].H + SpotLayout.RowGap;
        }
      }
      Below(BlockRole.InputBelowKept, kit.KeptInputs);
      Below(BlockRole.OutputBelowKept, kit.KeptOutputs);

      if (block.Places.All(p => p == null)) return;
      kit.At = facts == null
        ? new TidyPoint(now.Min(b => b.X), now.Min(b => b.Y))
        : Landing.Find(new LandingAsk
          {
            W = block.W, H = block.H, Canvas = canvas, Into = facts.Into, Kept = facts.Kept, FedBy = facts.FedBy, Feeding = facts.Feeding,
            ViewCentre = facts.View == null ? (TidyPoint?)null : new TidyPoint(facts.View.X + facts.View.W / 2, facts.View.Y + facts.View.H / 2),
          });

      for (var i = 0; i < kit.Built.Count; i++)
        if (block.Places[i] != null) MoveTo(i, kit.At.X + block.Places[i].X, kit.At.Y + block.Places[i].Y);
    }

    /// <summary>Where the block went, from what was placed, and how many rows it took.</summary>
    private static string SayLanded(RevertKit kit)
    {
      var facts = kit.Facts;
      var shape = kit.Block;
      if (facts == null) return SpotLayout.WithColumns(SpotLayout.WithRows(null, shape.Rows), shape.Columns);

      var landed = Landing.Extent(MeasureBlock(kit.Built));
      if (facts.Into != null) return SpotLayout.WithColumns(SpotLayout.WithRows(Landing.InGroup(landed, facts.Into), shape.Rows), shape.Columns);

      var beside = facts.Kept.Count > 0;
      var where = Landing.Where(landed, facts.Canvas, facts.View, beside ? Landing.KeptLabel : facts.AnchorLabel,
        Landing.Extent(beside ? facts.Kept : facts.FedBy.Count > 0 ? facts.FedBy : facts.Feeding));
      return SpotLayout.WithColumns(SpotLayout.WithRows(where, shape.Rows), shape.Columns);
    }

    /// <summary>BRIEF DK: THE LAW AFTER THE SOLVE, when a component may have taken a new size. A failure here costs the rerun and never a build that passed its check.</summary>
    private static void SettleBlock(RevertKit kit)
    {
      if (kit.Block == null) return;
      try
      {
        var settled = SpotLayout.Settle(kit.Block, () => MeasureBlock(kit.Built), block => PlaceBlock(kit, block));
        if (ReferenceEquals(settled, kit.Block)) return;
        kit.Block = settled;
        kit.LandedWhere = SayLanded(kit);
        kit.BlockGroup?.Attributes?.ExpireLayout();
      }
      catch { }
    }

    /// <summary>The part of the canvas the user is looking at, in canvas units, or null when this document is not the one on screen. Read before a landing is framed.</summary>
    internal static SpaceBox ViewOf(GH_Document d)
    {
      try
      {
        var canvas = Instances.ActiveCanvas;
        if (canvas == null || canvas.Document != d || canvas.Width <= 0 || canvas.Height <= 0) return null;
        var vp = canvas.Viewport;
        var w = canvas.Width / vp.Zoom;
        var h = canvas.Height / vp.Zoom;
        return new SpaceBox("", vp.MidPoint.X - w / 2, vp.MidPoint.Y - h / 2, w, h, SpaceRole.Fixed);
      }
      catch { return null; }
    }

    private static HashSet<string> ErrorKeys(GH_Document doc)
    {
      var keys = new HashSet<string>(StringComparer.Ordinal);
      try
      {
        foreach (var obj in doc.Objects)
        {
          if (!(obj is IGH_ActiveObject a)) continue;
          foreach (var t in a.RuntimeMessages(GH_RuntimeMessageLevel.Error) ?? new List<string>())
            keys.Add(obj.InstanceGuid + "|" + t);
        }
      }
      catch { }
      return keys;
    }

    /// <summary>How many attempts this region has left, or null when there is no region to key them to
    /// (#354 Q3) - stated rather than reported as a number that does not exist.</summary>
    /// <summary>
    /// WHAT A REBUILD OF THESE REGIONS WOULD REMOVE, for the consent card - resolved against the canvas
    /// as it stands right now, at the moment the card is drawn.
    ///
    /// It runs the SAME claim the build itself will run (<see cref="BuildLedger.ResolveForNextTurn"/>: Resolve
    /// without this turn's attempt budget, because the approved turn's budget is fresh - #626), so the card
    /// cannot promise a different scope from the one that executes. Computing it a second way would be two
    /// statements of one rule, free to drift - and the place they would drift is between what the user
    /// agreed to and what happened.
    /// </summary>
    internal static List<RegionRebuild> PreviewRebuild(GH_Document doc, BuildLedger ledger, AgentIdRegistry ids,
      IReadOnlyList<string> regions)
    {
      var previews = new List<RegionRebuild>();
      if (doc == null || ledger == null) return previews;

      foreach (var address in regions ?? new string[0])
      {
        try
        {
          if (!TryClaimForNextTurn(doc, ledger, address, out var claim)) continue;
          var present = claim.RemovalPresentIn(g => { try { return doc.FindObject(g, false) != null; } catch { return false; } });

          previews.Add(new RegionRebuild
          {
            Address = address,
            Remove = claim.Remove.Select(g => Describe(doc, g)).Where(c => c != null).ToList(),
            Adopted = claim.Adopted.Count,
            // U33: the kept ones NAMED on the card, so the user reads which components stay - the same ones a spec
            // that duplicates them will be refused over.
            AdoptedNamed = claim.Adopted.Select(g => Describe(doc, g)).Where(c => c != null).ToList(),
            Theirs = claim.Theirs,
            PlacedEarlier = claim.PlacedEarlier.Select(g => Describe(doc, g)).Where(c => c != null).ToList(),
            // #625: what the removal feeds outside it - the same cut read the build runs, so the card cannot name a
            // different boundary from the one the build enforces.
            Reconnect = ids == null ? new RebuildReconnection[0]
              : CutWiresOf(doc, ids, present).Select(ToReconnection).ToList(),
            // #625's narrow sibling, on a fresh build: what it MAY restore.
            Restore = ids == null || claim.Decision != RebuildDecision.Fresh ? new RebuildReconnection[0]
              : RestorableLossesOf(doc, ids, ledger, address).Select(ToReconnection).ToList(),
          });
        }
        catch { }
      }
      return previews;
    }

    /// <summary>
    /// What each approved region's rebuild would CUT, read at turn start for the approved-turn fact (#625, #647) -
    /// through the same claim the card previews (<see cref="BuildLedger.ResolveForNextTurn"/>) and the same cut read
    /// the build runs, so the model is told the boundary the build will enforce. Regions with no ledger record or
    /// nothing to remove are absent.
    /// </summary>
    internal static Dictionary<string, IReadOnlyList<CutWire>> CutsForRebuild(GH_Document doc, BuildLedger ledger,
      AgentIdRegistry ids, IReadOnlyList<string> regions)
    {
      var cuts = new Dictionary<string, IReadOnlyList<CutWire>>(StringComparer.Ordinal);
      if (doc == null || ledger == null || ids == null) return cuts;

      foreach (var address in regions ?? new string[0])
      {
        try
        {
          if (!TryClaimForNextTurn(doc, ledger, address, out var claim)) continue;
          var present = claim.RemovalPresentIn(g => { try { return doc.FindObject(g, false) != null; } catch { return false; } });
          var found = CutWiresOf(doc, ids, present);
          // #625's narrow sibling: a fresh build is told what it may restore, through the same diff the build runs.
          if (claim.Decision == RebuildDecision.Fresh) found.AddRange(RestorableLossesOf(doc, ids, ledger, address));
          if (found.Count > 0) cuts[address] = found;
        }
        catch { }
      }
      return cuts;
    }

    /// <summary>
    /// What each approved region's rebuild KEEPS, read at turn start for the approved-turn fact (U33's panel half, #954) -
    /// the sibling of <see cref="CutsForRebuild"/>, through the same claim (<see cref="TryClaimForNextTurn"/>) and the
    /// same kept read the duplicate check runs (<see cref="KeptOf"/>), so the model is told up front the components
    /// <see cref="RebuildDuplicates.Check"/> would refuse a duplicate of. Regions that keep nothing are absent.
    /// </summary>
    internal static Dictionary<string, IReadOnlyList<KeptComponent>> KeptForRebuild(GH_Document doc, BuildLedger ledger,
      AgentIdRegistry ids, IReadOnlyList<string> regions)
    {
      var kept = new Dictionary<string, IReadOnlyList<KeptComponent>>(StringComparer.Ordinal);
      if (doc == null || ledger == null || ids == null) return kept;

      foreach (var address in regions ?? new string[0])
      {
        try
        {
          if (!TryClaimForNextTurn(doc, ledger, address, out var claim)) continue;
          // Only the decisions that REACH the duplicate check in Run: an Occupied or Exhausted region refuses before it,
          // and telling the model to wire from components a build there could never place beside would be a claim about
          // a path that does not run.
          if (claim.Decision != RebuildDecision.Replace && claim.Decision != RebuildDecision.Fresh) continue;
          var found = KeptOf(doc, ids, claim);
          if (found.Count > 0) kept[address] = found;
        }
        catch { }
      }
      return kept;
    }

    private static RebuildReconnection ToReconnection(CutWire c) => new RebuildReconnection
    {
      ConsumerType = c.ConsumerType, ConsumerId = c.ConsumerId, ConsumerRegion = c.ConsumerRegion, Input = c.InputLabel,
    };

    /// <summary>Does this region hold only what Dwight placed and left as it was? Nothing a rebuild kept (U33), and nothing the user added. No record reads as no, the safe direction:
    /// the re-space leaves such a region where it is.</summary>
    internal static bool HoldsOnlyWhatDwightLeft(GH_Document doc, BuildLedger ledger, string address) =>
      ledger != null && TryClaimForNextTurn(doc, ledger, address, out var claim) && claim.HasRecordForRegion && claim.Adopted.Count == 0 && claim.Theirs == 0 && claim.PlacedEarlier.Count == 0;

    private static bool TryClaimForNextTurn(GH_Document doc, BuildLedger ledger, string address, out RebuildClaim claim)
    {
      claim = null;
      if (!GhScaffoldMap.TryFindGroup(doc, address, out var group, out _, out _)) return false;
      var inRegion = GhScaffoldMap.ComponentsIn(doc, group);
      // #626: the card previews the turn it OPENS, whose attempt budget is fresh - never this turn's, which a card
      // minted after the attempts are spent would otherwise show as "nothing to remove".
      claim = ledger.ResolveForNextTurn(address, AsLeftNow(doc, inRegion));
      return true;
    }

    /// <summary>The components a rebuild KEEPS (U33) - the claim's Adopted set and what was placed earlier, as the plugin knows each: agent id, type and
    /// nickname. One that cannot be read is left out, which only makes the duplicate check miss it; it is never removed.</summary>
    private static List<KeptComponent> KeptOf(GH_Document doc, AgentIdRegistry ids, RebuildClaim claim)
    {
      var kept = new List<KeptComponent>();
      foreach (var (g, earlier) in claim.Adopted.Select(g => (g, false)).Concat(claim.PlacedEarlier.Select(g => (g, true))))
      {
        try
        {
          var obj = doc?.FindObject(g, false);
          if (obj == null) continue;
          kept.Add(new KeptComponent { Id = ids.Issue(g), Type = obj.Name, Nick = obj.NickName, PlacedEarlier = earlier });
        }
        catch { }
      }
      return kept;
    }

    /// <summary>One component as a person would recognise it on the canvas.</summary>
    private static RebuildComponent Describe(GH_Document doc, Guid guid)
    {
      try
      {
        var obj = doc?.FindObject(guid, false);
        return obj == null ? null : new RebuildComponent { Nick = obj.NickName, Type = obj.Name };
      }
      catch { return null; }
    }


    /// <summary>
    /// The as-left token for each of these components, as they stand right now.
    ///
    /// A component that cannot be read yields a NULL token, and a null compares as CHANGED - so an
    /// unreadable component is left alone rather than removed. Failing towards "leave it" is the only
    /// acceptable direction for a check that decides what gets deleted.
    ///
    /// ================================================================================================
    /// THE DIRECTION IS LOAD-BEARING, AND TWO DIFFERENT ROUTES REACH IT (U18/#770). Do not flatten them.
    ///
    /// A null from here means two different things to the two callers, and BOTH arrive at "leave it":
    ///
    ///   - THE GROUPED PATH hands this dictionary to `Resolve`, where a null token COMPARES AS CHANGED.
    ///     The component is therefore ADOPTED - the user's now, not Dwight's - and is never removed.
    ///   - THE LOOSE PATH (`ResolveWithoutBoundary`, for a placement with no box) reads a null as THE
    ///     COMPONENT CANNOT BE ACCOUNTED FOR - gone, or present and unreadable - so it is excluded from
    ///     the claim entirely and, again, is never removed.
    ///
    /// THE TWO READINGS REPORT DIFFERENTLY, AND ONLY THE REPORTING DIFFERS. An unreadable component shows
    /// on the grouped path as ADOPTED (the user's now), and on the loose path it does not appear in the
    /// claim's counts at all. Both leave it standing, so nothing unsafe follows; but a loose rebuild will
    /// not tell the user it left something behind. That is unreachable until a script can be placed at a
    /// spot (U19) - noted here rather than solved speculatively, so whoever gets there finds the question
    /// already asked.
    ///
    /// That is the shape a later tidy-up unifies into one meaning and silently inverts: make the loose
    /// path treat null as "unchanged, therefore ours" and an unreadable component becomes deletable. The
    /// two readings are deliberate, and the property that matters is not which reading is used but that
    /// BOTH FAIL TOWARD LEAVING SOMEBODY'S WORK ALONE.
    ///
    /// The per-guid catch below is therefore NOT noisy ON PURPOSE. A throw while reading one component
    /// must not take the whole claim with it, and must not be recoverable into "assume it is ours" - so
    /// it degrades to the safe reading for whichever caller asked. The token growing richer (a script's
    /// source digest, #777) does not change this: a readable object still yields a non-null record,
    /// because AsLeftRecord.Of returns null only for a null FpObject.
    /// ================================================================================================
    /// </summary>
    /// <summary>
    /// The group a GROUPED spot lands in, created here because a spot has none until its first build.
    ///
    /// Its nickname carries the address in the producer's own format (<c>ScaffoldAddress.Separator</c>),
    /// so the spot survives the loss of its record exactly as a map region does (#221b) - the canvas is
    /// the authoritative half for both kinds. The NAME after the address is plugin-computed from what was
    /// actually placed, never from the model's text.
    ///
    /// Failure here is not failure of the build: the components are placed and recorded either way, and
    /// the record is what the ledger, the attempt cap and the scope check all key on. A box we could not
    /// draw leaves a loose placement, which is a state the product already supports.
    /// </summary>
    private static GH_Group MakeSpotGroup(GH_Document d, string address, IEnumerable<IGH_DocumentObject> objects,
                                          List<IGH_UndoAction> actions)
    {
      try
      {
        var members = objects.Where(o => o != null).ToList();
        if (members.Count == 0) return null;

        var label = members.Count == 1
          ? Describe(members[0])
          : Describe(members[0]) + " and " + (members.Count - 1) + " more";

        var group = new GH_Group { NickName = address + ScaffoldAddress.Separator + label };
        group.CreateAttributes();
        foreach (var o in members) group.AddObject(o.InstanceGuid);

        d.AddObject(group, false);
        actions.Add(new GH_AddObjectAction(group));   // inside the one undo record, with its contents
        return group;
      }
      catch { return null; }
    }

    /// <summary>What to call a component in a group's name - its nickname where the user would recognise
    /// it, else its type.</summary>
    private static string Describe(IGH_DocumentObject o)
    {
      try
      {
        var nick = o.NickName;
        return string.IsNullOrWhiteSpace(nick) ? (o.Name ?? "component") : nick;
      }
      catch { return "component"; }
    }

    /// <summary>
    /// One component's as-left token, or null if it is no longer on the canvas. The single-guid door onto
    /// <see cref="AsLeftNow"/>, for the boundary-less claim a loose spot resolves through (#770) - so both
    /// paths read a token exactly the same way and cannot drift apart.
    ///
    /// COST, STATED SO NOBODY REDISCOVERS IT: this reads one object per RECORDED component, and a spot
    /// holds at most <c>SpotPlacement.MaxComponents</c> of them, so the walk is bounded by the same rule that
    /// bounds the placement. It stays bounded when the token grows (a script's source digest, #777): the
    /// per-component read gets bigger, the number of reads does not.
    /// </summary>
    private static string AsLeftOne(GH_Document doc, Guid guid) => AsLeftNow(doc, new[] { guid })[guid];

    private static Dictionary<Guid, string> AsLeftNow(GH_Document doc, IEnumerable<Guid> guids)
    {
      var now = new Dictionary<Guid, string>();
      foreach (var g in guids ?? Enumerable.Empty<Guid>())
      {
        if (now.ContainsKey(g)) continue;
        try
        {
          var obj = doc?.FindObject(g, false);
          now[g] = obj == null ? null
            : AsLeftRecord.Of(BehaviourFingerprint.ReadObject(obj), obj.NickName, StateToken.Of(GhBuildState.Read(obj)));
        }
        catch { now[g] = null; }
      }
      return now;
    }

    /// <summary>
    /// The as-left STATE of each component as it stands now (#625): the token, plus the ordered sources per input
    /// and the output params the token cannot expose. An unreadable component gets a null token, which compares as
    /// changed - the same safe direction as <see cref="AsLeftNow"/>.
    /// </summary>
    private static Dictionary<Guid, AsLeftState> AsLeftStatesNow(GH_Document doc, IEnumerable<Guid> guids)
    {
      var now = new Dictionary<Guid, AsLeftState>();
      foreach (var g in guids ?? Enumerable.Empty<Guid>())
      {
        if (now.ContainsKey(g)) continue;
        try
        {
          var obj = doc?.FindObject(g, false);
          var fp = obj == null ? null : BehaviourFingerprint.ReadObject(obj);
          now[g] = fp == null
            ? new AsLeftState()
            : new AsLeftState
              {
                Token = AsLeftRecord.Of(fp, obj.NickName, StateToken.Of(GhBuildState.Read(obj))),
                Sources = (fp.Inputs ?? new List<FpInput>())
                  .GroupBy(i => i.Guid)
                  .ToDictionary(grp => grp.Key, grp => (IReadOnlyList<Guid>)(grp.First().Sources ?? new List<Guid>()).ToList()),
                Outputs = SourceGuidsOf(obj).ToList(),
              };
        }
        catch { now[g] = new AsLeftState(); }
      }
      return now;
    }

    /// <summary>
    /// What the built components actually HOLD, in spec order, for the region's success row (#394).
    ///
    /// Read off the objects, never echoed from the spec: by this point equality has been enforced so the
    /// two agree, but a line that restated the ask would be reporting the request rather than the result,
    /// and the whole reason this exists is that #377's wrong value was one nobody could see.
    /// </summary>
    private static JsonArray StateSet(BuildSpec spec, Dictionary<string, IGH_DocumentObject> created)
    {
      var set = new JsonArray();
      foreach (var c in spec.Components)
      {
        if (c.State == null || !created.TryGetValue(c.Ref, out var obj)) continue;
        var described = GhBuildState.Describe(obj, c.State);
        if (!string.IsNullOrWhiteSpace(described)) set.Add(described);
      }
      return set;
    }

    /// <summary>
    /// The first component whose declared initial state is not what it actually holds, described in the
    /// user's terms - or null when every declaration landed (#377).
    ///
    /// It names the component, because a reverted build has to be repairable: "a slider did not take its
    /// value" sends the model looking, while "s2: the slider holds 1, not the declared 40" is a repair
    /// instruction. That is ADR-092's shape at the state boundary.
    /// </summary>
    private static string FirstStateMismatch(BuildSpec spec, Dictionary<string, IGH_DocumentObject> created)
    {
      foreach (var c in spec.Components)
      {
        if (c.State == null) continue;
        if (!created.TryGetValue(c.Ref, out var obj)) continue;   // absent is the count check's business
        if (!GhBuildState.Matches(obj, c.State, out var mismatch))
          return "\"" + c.Ref + "\": " + mismatch;
      }
      return null;
    }

    /// <summary>
    /// Load a script component's source and append its declared ports (U19). Throws, with a reason the model can act on,
    /// when the shim cannot - the caller's catch rolls the build back with nothing on the canvas.
    ///
    /// THE LIVE PORT NAMES ARE CHECKED AGAINST THE DECLARATION, not only the parse's assumption of what a new script
    /// component is born with: a Rhino whose defaults differ would otherwise build two ports of one name, and the code
    /// would bind to whichever the runtime preferred. Refusing is the answer wherever the answer is not forced (#208).
    /// </summary>
    /// <summary>Brief CP: what the script declared on each input. A failure throws into the rollback with the canvas untouched, because "placed and wired as declared" includes it.</summary>
    private static void ApplyDeclaredAccess(IGH_Component comp, BuildComponent c)
    {
      foreach (var kv in c.InputAccess ?? new Dictionary<string, string>())
        if (!Dwight.Simplify.ScriptComponentShim.SetInputAccess(comp, kv.Key, PortAccess.ToScript(kv.Value), out var accessError))
          throw new InvalidOperationException(accessError);
    }

    private static void ApplyScript(IGH_DocumentObject obj, BuildComponent c)
    {
      if (c == null || !c.CarriesSource) return;

      var comp = obj as IGH_Component;
      if (comp == null)
        throw new InvalidOperationException("\"" + c.Type + "\" did not instantiate as a script component, so it cannot take source");

      if (!Dwight.Simplify.ScriptComponentShim.SetSource(comp, c.Source, out var sourceError))
        throw new InvalidOperationException(sourceError);
      // U78: a script that declares its own inputs or outputs has ONLY those - the born-with x, y (inputs) and a (output) go
      // first, so the declared ports are the whole interface and the verdict has no unwired default to report.
      if (!Dwight.Simplify.ScriptComponentShim.RemoveDefaults(comp, BuildScriptTypes.ReplacesDefaultInputs(c),
            BuildScriptTypes.ReplacesDefaultOutputs(c), BuildScriptTypes.DefaultInputs, BuildScriptTypes.DefaultOutputs, out var removeError))
        throw new InvalidOperationException(removeError);
      // Brief EA: what is STILL here is what a declared name can collide with. This stood ahead of the removal for a week, so an input
      // called a was refused for an output a about to be removed, and a build paid for three attempts at it. Side by side, because a name
      // across the sides lands in Python and the parser refuses it in C#, so this answers only a Rhino whose defaults differ.
      var haveInputs = new HashSet<string>(comp.Params.Input.Select(DwightGraph.ParamLabel), StringComparer.OrdinalIgnoreCase);
      var haveOutputs = new HashSet<string>(Dwight.Simplify.ScriptComponentShim.ScriptOutputs(comp).Select(DwightGraph.ParamLabel), StringComparer.OrdinalIgnoreCase);
      foreach (var name in (c.Inputs ?? new List<string>()).Where(haveInputs.Contains).Concat((c.Outputs ?? new List<string>()).Where(haveOutputs.Contains)))
        throw new InvalidOperationException("script \"" + c.Ref + "\" still has a port called \"" + name + "\" once its default ports are gone, so a new " +
          "one of that name would be two ports the code cannot tell apart");
      if (!Dwight.Simplify.ScriptComponentShim.Append(comp, c.Inputs, c.Outputs, out var appendError))
        throw new InvalidOperationException(appendError);
      ApplyDeclaredAccess(comp, c);
      // U84: the declared outputs are configured as to-script configures its outputs (one shared function), here, before AddObject.
      Dwight.Simplify.ScriptComponentShim.ConfigureDeclaredOutputs(comp, c.Outputs,
        c.Language == BuildScriptTypes.CSharp ? Dwight.Simplify.ScriptLanguage.CSharp : Dwight.Simplify.ScriptLanguage.Python3);
      // U85: the base script component's marshal flags, as a hand-made component carries them, so a Python list emits as items.
      Dwight.Simplify.ScriptComponentShim.SetMarshFlags(comp);
    }

    /// <summary>
    /// What the solve showed about each SCRIPT the build placed - facts only (U19). Errors are the component's own, verbatim;
    /// an output's non-null count uses <c>OutputItems</c>' rule (a null item is not data); an input is unwired when nothing
    /// feeds it NOW. Not guarded: a read that throws means no leg could be judged, and the caller reverts.
    /// </summary>
    /// <summary>A script that assigned <c>None</c> leaves an item whose goo unwraps to nothing (U47): in the data, not data.</summary>
    private static bool IsNothingGoo(object item) => item is Grasshopper.Kernel.Types.IGH_Goo goo && goo.ScriptVariable() == null;

    private static List<ScriptBuildFacts> ScriptFactsOf(BuildSpec spec, Dictionary<string, IGH_DocumentObject> created)
    {
      var facts = new List<ScriptBuildFacts>();
      foreach (var c in spec.Components)
      {
        if (!c.CarriesSource || !created.TryGetValue(c.Ref, out var obj)) continue;

        var f = new ScriptBuildFacts
        {
          Ref = c.Ref,
          Language = c.Language,
          Name = string.IsNullOrWhiteSpace(c.Nick) ? ComponentMatchCore.BareName(c.Type ?? "script") : c.Nick.Trim(),
          Errors = Dwight.Simplify.ScriptComponentShim.Errors(obj),
          LineShift = c.LineShift,
          GeneratedSignature = c.GeneratedSignature,
          HintNotes = c.HintNotes ?? new List<string>(),
        };
        if (obj is IGH_Component comp)
        {
          f.Inputs = comp.Params.Input.Select(DwightGraph.ParamLabel).ToList();
          f.UnwiredInputs = comp.Params.Input.Where(p => p.SourceCount == 0).Select(DwightGraph.ParamLabel).ToList();
          // U92: WHAT EACH WIRED INPUT RECEIVED, read off the live input and never inferred from the error text. An input that
          // received no data or only nulls is EMPTY; one given item access whose sources hold more than one item is the access case.
          var emptyInputs = new List<string>();
          var itemMulti = new List<string>();
          foreach (var p in comp.Params.Input.Where(p => p.SourceCount > 0))
          {
            try
            {
              var (items, nonNull) = OutputItems.Count(p.VolatileData.AllData(false), IsNothingGoo);
              if (nonNull == 0) { emptyInputs.Add(DwightGraph.ParamLabel(p)); continue; }
              if (p.Access == GH_ParamAccess.item)
              {
                var upstream = 0;
                foreach (var src in p.Sources) { try { upstream += src.VolatileDataCount; } catch { } }
                if (upstream > 1) itemMulti.Add(DwightGraph.ParamLabel(p));
              }
            }
            catch { }
          }
          f.EmptyInputs = emptyInputs;
          f.ItemAccessMultiItemInputs = itemMulti;
          f.Outputs = Dwight.Simplify.ScriptComponentShim.ScriptOutputs(comp).Select(p =>
          {
            var (items, nonNull) = OutputItems.Count(p.VolatileData.AllData(false), IsNothingGoo);
            return new ScriptOutputRead { Port = DwightGraph.ParamLabel(p), Items = items, NonNull = nonNull };
          }).ToList();
        }
        facts.Add(f);
      }
      return facts;
    }

    /// <summary>
    /// WHAT EACH PLACED COMPONENT HOLDS AFTER THE SOLVE (U26; #916, #923): its own runtime warnings and errors, every
    /// output's item count, and the geometry reads' facts over every output that holds geometry - through
    /// <see cref="GhGeometry.Items"/> and <see cref="GhGeometry.Facts"/>, the one reader, so the buildline cannot
    /// disagree with get_geometry_summary about the same output.
    ///
    /// BOUNDED (ADR-035): at most <see cref="BuildPlacedFacts.MaxItemsMeasured"/> objects measured per output, and
    /// ONE area/volume time budget across the whole build (the summary read's own, <see cref="GhGeometry.MassPropsBudgetMs"/>);
    /// each bound that bites is written into the output's note rather than silently applied. Bounding boxes and
    /// lengths are cheap and always read. Read-only over the last solution's volatile data: no solve, no mutation.
    /// Any read that throws yields a fact-less output rather than a failed build - this is reporting, not a leg.
    /// </summary>
    private static List<PlacedComponentFact> PlacedFactsOf(AgentIdRegistry ids, BuildSpec spec,
      Dictionary<string, IGH_DocumentObject> created)
    {
      var facts = new List<PlacedComponentFact>();
      var budget = Stopwatch.StartNew();
      foreach (var c in spec.Components)
      {
        if (!created.TryGetValue(c.Ref, out var obj) || obj == null) continue;
        var f = new PlacedComponentFact { Ref = c.Ref };
        try { f.Type = obj.Name; f.Nick = obj.NickName; } catch { }
        try { f.Id = ids.Issue(obj.InstanceGuid); } catch { }
        if (obj is IGH_ActiveObject a)
        {
          try { f.Warnings.AddRange(a.RuntimeMessages(GH_RuntimeMessageLevel.Warning) ?? new List<string>()); } catch { }
          try { f.Errors.AddRange(a.RuntimeMessages(GH_RuntimeMessageLevel.Error) ?? new List<string>()); } catch { }
        }
        foreach (var output in OutputParamsOf(obj))
        {
          string port = null;
          try { port = obj is IGH_Param ? null : DwightGraph.ParamLabel(output); } catch { }
          try { f.Outputs.Add(OutputFactOf(output, port, budget)); } catch { }
        }
        facts.Add(f);
      }
      return facts;
    }

    private static PlacedOutputFact OutputFactOf(IGH_Param output, string port, Stopwatch budget)
    {
      var items = 0;
      try { items = output.VolatileDataCount; } catch { }

      List<Rhino.Geometry.GeometryBase> geometry;
      try { geometry = GhGeometry.Items(output, out _); }
      catch { geometry = new List<Rhino.Geometry.GeometryBase>(); }

      var measured = new List<GeoObject>();
      var skipped = 0;
      var cap = Math.Min(geometry.Count, BuildPlacedFacts.MaxItemsMeasured);
      for (var i = 0; i < cap; i++)
      {
        try { measured.Add(GhGeometry.Facts(geometry[i], i, massProps: true, budget, ref skipped)); }
        catch { }
      }

      string note = null;
      if (geometry.Count > cap) note = "measured on the first " + cap + " of " + geometry.Count;
      if (skipped > 0)
        note = (note == null ? "" : note + "; ") + "area not computed for " + skipped + " object" + (skipped == 1 ? "" : "s") +
               " after " + GhGeometry.MassPropsBudgetMs + " ms";
      return BuildPlacedFacts.Aggregate(port, items, geometry.Count, measured, note);
    }

    /// <summary>The active Rhino document's model unit, abbreviated for a line a person reads - or null when there is no
    /// document to read it from, in which case the facts carry no unit rather than a guessed one.</summary>
    private static string UnitAbbreviation()
    {
      try
      {
        var rhino = Rhino.RhinoDoc.ActiveDoc;
        if (rhino == null) return null;
        switch (rhino.ModelUnitSystem)
        {
          case Rhino.UnitSystem.Millimeters: return "mm";
          case Rhino.UnitSystem.Centimeters: return "cm";
          case Rhino.UnitSystem.Meters: return "m";
          case Rhino.UnitSystem.Kilometers: return "km";
          case Rhino.UnitSystem.Inches: return "in";
          case Rhino.UnitSystem.Feet: return "ft";
          case Rhino.UnitSystem.Yards: return "yd";
          case Rhino.UnitSystem.Miles: return "mi";
          default: return rhino.ModelUnitSystem.ToString().ToLowerInvariant();
        }
      }
      catch { return null; }
    }

    private static int? AttemptsLeft(BuildLedger ledger, string region) =>
      string.IsNullOrEmpty(region)
        ? (int?)null
        : Math.Max(0, DwightConfig.AgentBuildMaxAttemptsPerRegion - ledger.Attempts(region));

    /// <summary>What a revert checks before it calls Undo (U77): the guids the build's recorded actions refer to, and the
    /// components the build placed (the ledger of what is Dwight's to remove).</summary>
    private sealed class RevertKit
    {
      public readonly BuildUndoRefs Refs = new BuildUndoRefs();
      public readonly List<IGH_DocumentObject> Placed = new List<IGH_DocumentObject>();

      /// <summary>U91: what the build added to the document OUTSIDE <see cref="Placed"/> (the spot group) - removed with them by the guarded revert.</summary>
      public readonly List<IGH_DocumentObject> AddedBesides = new List<IGH_DocumentObject>();

      /// <summary>Brief CM: the sentence for where this build landed, and whether it frames itself. A map region's does neither here: it says and frames once, after the run's tidy.</summary>
      public string LandedWhere;
      public bool FramesItself;

      /// <summary>Brief DK: what was packed and where, kept for the law after the solve. No block for a map region, which the tidy places, or if the layout failed.</summary>
      public LandingFacts Facts;
      public IReadOnlyList<KeyValuePair<BuildComponent, IGH_DocumentObject>> Built;
      public SpotBlock Block;
      public IReadOnlyList<BlockRole> Roles;
      public List<SpaceBox> KeptInputs, KeptOutputs;
      public TidyPoint At;
      public GH_Group BlockGroup;

      /// <summary>U91: every object that stood BEFORE the build, with the name a user would call it. Taken when the kit is made, before anything changes.</summary>
      public Dictionary<Guid, string> Before = new Dictionary<Guid, string>();

      public void AddSpotGroup(GH_Group group)
      {
        if (group == null) return;
        Refs.Add(BuildUndoRefs.RoleSpotGroup, NameOf(group), group.InstanceGuid);
        AddedBesides.Add(group);
      }

      /// <summary>Everything this build added to the document, by guid: the placed components and the spot group.</summary>
      public HashSet<Guid> AddedGuids() =>
        new HashSet<Guid>(Placed.Concat(AddedBesides).Select(o => { try { return o.InstanceGuid; } catch { return Guid.Empty; } }));

      // U87: the name as the user would call it, read NOW, while the object is still the one the action refers to.
      private static string NameOf(IGH_DocumentObject o) { try { return o?.NickName; } catch { return null; } }

      public void Fill(GH_Group target, IEnumerable<IGH_DocumentObject> created, IEnumerable<IGH_Param> wireTargets)
      {
        if (target != null) Refs.Add(BuildUndoRefs.RoleRegionGroup, NameOf(target), target.InstanceGuid);
        foreach (var o in created) { Refs.Add(BuildUndoRefs.RolePlacedComponent, NameOf(o), o.InstanceGuid); Placed.Add(o); }
        foreach (var p in wireTargets) Refs.Add(BuildUndoRefs.RoleWireTarget, NameOf(p), p.InstanceGuid);
      }
    }

    /// <summary>
    /// Undo the build's record (U77: guarded). If any object or parameter an action refers to is gone, Grasshopper's own
    /// Undo would raise a modal breakpoint that no catch of ours can stop, so Undo is NOT called: the components this build
    /// placed are removed from the record instead, and the returned line says so for the verdict. Null on the ordinary revert.
    /// </summary>
    private static string UndoOnce(GH_Document doc, RevertKit kit)
    {
      string note = null;
      var missing = kit?.Refs.FirstMissingDescribed(g =>
      { try { return doc.FindObject(g, true) != null; } catch { return false; } });
      // U108: BuildRevertPlan.For(missing != null) is the order, on BOTH paths: mark the record as taken back first (Brief AP: History
      // shows it reverted and never offers it for redo), then either undo it through the stack, or - when the stack cannot undo it -
      // remove what was placed by hand AND the record itself, so no dead "Dwight: build region" stands for a build that is not there.
      var marked = Dwight.Session.ChangeLog.Reverting(doc);
      if (missing != null)
      {
        foreach (var o in kit.Placed.Concat(kit.AddedBesides))
          try { doc.RemoveObject(o, false); } catch { }
        if (marked != Guid.Empty) { try { doc.UndoServer?.RemoveRecord(marked); } catch { } }
        note = BuildUndoRefs.RevertNote(missing);   // U87: names the role and the name of what was missing
      }
      else
      {
        try { doc.Undo(); } catch { }
      }
      try { Instances.RedrawCanvas(); } catch { }
      return note;
    }

    /// <summary>U91: every object in the document with the name a user would call it. A dictionary by guid; a name that cannot be read is empty.</summary>
    private static Dictionary<Guid, string> NamesOf(GH_Document d)
    {
      var map = new Dictionary<Guid, string>();
      try
      {
        foreach (var o in d.Objects)
        {
          try
          {
            var nick = o.NickName;
            map[o.InstanceGuid] = string.IsNullOrWhiteSpace(nick) ? (o.Name ?? "") : nick;
          }
          catch { }
        }
      }
      catch { }
      return map;
    }

    /// <summary>U91: MEASURE what the revert left, against what stood before the build. Null (unmeasured) only when no "before" was taken, which
    /// cannot happen after the callback began.</summary>
    private static RevertMeasurement Measure(GH_Document doc, RevertKit kit)
    {
      try
      {
        if (kit == null || kit.Before == null || kit.Before.Count == 0) return null;
        return RevertMeasure.Compare(kit.Before, NamesOf(doc), kit.AddedGuids());
      }
      catch { return null; }
    }

    /// <summary>U91: the measurement on the verdict: the undo field always, and, only when the revert did not leave the canvas as it was, the flag and the sentence.</summary>
    private static JsonObject WithMeasure(JsonObject verdict, RevertMeasurement m)
    {
      verdict["undo"] = RevertMeasure.UndoField(m);
      if (m != null && !m.AsItWas)
      {
        verdict["canvasAsItWas"] = false;
        verdict["residue"] = RevertMeasure.Sentence(m);
      }
      return verdict;
    }

    private static bool SolverEnabled()
    {
      try { return GH_Document.EnableSolutions; } catch { return true; }
    }
  }
}
