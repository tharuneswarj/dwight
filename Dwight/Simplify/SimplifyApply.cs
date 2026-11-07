using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Parameters;
using Grasshopper.Kernel.Special;
using Grasshopper.Kernel.Undo;
using Grasshopper.Kernel.Undo.Actions;
using Dwight.Data;

namespace Dwight.Simplify
{
  /// <summary>The retained resolve context for a better-logic APPLY, built at preview time on a FRESH graph so
  /// the accept works in guid space (proposal ids are pivot-derived and unstable). Everything the apply needs to
  /// realise the minimal diff and verify it.</summary>
  internal sealed class SimplifyResolve
  {
    public GH_Document Doc;
    public SimplifyProposal Proposal;
    public Dictionary<int, Guid> IdToGuid = new Dictionary<int, Guid>();     // proposal id -> live component guid
    public Dictionary<string, Guid> AddType = new Dictionary<string, Guid>(); // resolved COMPONENT (proxy) guid per add type
    public Dictionary<int, (Guid Src, int Port)> ExtIn = new Dictionary<int, (Guid, int)>();     // i<ext> -> external source output
    public Dictionary<int, List<(Guid Obj, int Port)>> ExtOut = new Dictionary<int, List<(Guid, int)>>(); // o<ext> -> external consumer inputs
    public List<Guid> Layer2Params = new List<Guid>();   // the PRESERVED output surface, as sample param guids (RR4 #3)
    public RectangleF SelectionBounds;                   // anchors the added components' layout
    public Dictionary<int, PointF> AddAnchor = new Dictionary<int, PointF>();   // RR5 #1: add index -> the pivot to be born at (its replaced source); the generic stack is only for adds replacing nothing specific
    public List<(Guid Guid, string Nick)> Renames = new List<(Guid, string)>();   // Stage 6: behaviour-neutral nickname edits on kept components
    public List<Guid> AppliedAddGuids;                   // Stage 6 item 10: written back by Run on commit, so a dock reopen can tell the change is still in place
    public Dictionary<int, List<Guid>> AppliedAddByChange = new Dictionary<int, List<Guid>>();   // RR8 #21: change index -> the guids it created (for the applied list's click-to-locate)
    // RR7: an externalize is PLUGIN-COMPUTED. Per externalize slider add index, the exact consumer input port(s)
    // the plugin wires it to (owner component guid + input port index) - read from the live graph, never the model.
    public Dictionary<int, List<(Guid Owner, int Port)>> ExternalizeWiring = new Dictionary<int, List<(Guid, int)>>();
  }

  /// <summary>
  /// The better-logic APPLY (WP-2 Stage 5) - the first canvas-mutating path, guarded by BOTH verification layers
  /// (ADR-090). Read <see cref="Run"/> top-to-bottom: it is the locked architecture in order, and the six round-4
  /// corrections are the NAMED guard points -
  ///   #1 <c>RevertAndDefuseRedo</c> (a reverted mutation must not be one Ctrl-Y away),
  ///   #2 the freshness refusal + <c>EnsureSolved</c> (never verify against a stale baseline),
  ///   #3 <c>Layer2Surface.Preserved</c> (declared-dead removals excluded from the compare - upstream, in the surface),
  ///   #4 <c>ArmLayer2</c> (SolutionEnd first-fire, unhook-before-undo, timeout) + busy held to the verdict,
  ///   #5 the undo-first invariant (every mutation from the first AddObject is in the recorded actions),
  ///   #6 <c>BuildSlider</c> (externalize values are plugin-read, never model-emitted).
  /// Keeps are never instantiated; only removes / adds / rewires touch the canvas. The wiring is ONE computed
  /// list (<see cref="ComputeFinalWiring"/>) that both the prediction and the apply derive from, so the source
  /// ORDER the fingerprint hashes matches by construction (MUST-FIX 2). Capture + mutate happen in the SAME
  /// scheduled callback (MUST-FIX 1), and the commit splits the scopes: a pre-commit failure reverses the actions
  /// manually, a post-commit failure reverts via undo (MUST-FIX 3).
  /// </summary>
  internal static class SimplifyApply
  {
#if DEBUG
    /// <summary>A dev-menu live-fire sabotage that corrupts exactly one apply, then self-clears (the gate's
    /// trigger; DEBUG-gated permanently as a re-runnable drill). The value sabotage NO-OPS unless the proposal
    /// contains an externalize add (a slider).</summary>
    internal enum Sabotage { None, StrayRewire, ValueChange }
    internal static Sabotage PendingSabotage = Sabotage.None;
    /// <summary>Set true when a sabotage actually fired this run, so the resulting STOP/revert can label itself a
    /// caught drill (RR5 #2) - the demo reads as success, not a real failure. Reset at the start of each run.</summary>
    internal static bool LastRunSabotaged;
#endif

    public static void Run(SimplifyResolve rc, Action<string> log, Action<ApplyOutcome> done)
    {
#if DEBUG
      LastRunSabotaged = false;   // RR5 #2: a fresh run is not a drill until a sabotage actually fires
#endif
      var doc = rc?.Doc;
      if (doc == null) { done?.Invoke(ApplyOutcome.Fail("Simplify hit a problem", "The document is no longer available.")); return; }

      // ---- GUARD #2: freshness (OUTSIDE the callback - you cannot NewSolution mid-cycle) -----------------
      if (SolverDisabled(doc)) { done?.Invoke(ApplyOutcome.Stop("Enable the solver first", "Dwight verifies a Simplify by re-solving and comparing the outputs, which needs the Grasshopper solver enabled. Enable it, then try again.")); return; }
      EnsureSolved(doc);   // one solve if anything sits expired, so the before-capture reads fresh data

      doc.ScheduleSolution(10, d =>
      {
        var actions = new List<IGH_UndoAction>();   // GUARD #5: every mutation from the first AddObject onward
        List<OutputSample> beforeOutputs;
        try
        {
          // ---- capture BEFORE - the callback's FIRST act (MUST-FIX 1: capture + mutate in ONE callback) ----
          var beforeDoc = BehaviourFingerprint.ReadDoc(d);
          beforeOutputs = CaptureOutputs(d, rc.Layer2Params);

          // ---- instantiate the adds (pre-stamped guids), then HARVEST them BEFORE wiring (the G.1 order) ----
          var addGuids = InstantiateAdds(d, rc, actions, out var harvested);

          // ---- ONE computed wiring list drives BOTH the prediction and the apply (MUST-FIX 2) --------------
          var wiring = ComputeFinalWiring(d, rc, addGuids);

          // ---- build the plan + PREDICT the after-fingerprint, purely from before + the declared delta ------
          var plan = BuildPlan(rc, harvested, wiring);
          var predicted = DeltaPredictor.Predict(beforeDoc, plan);

          // ---- complete the apply: rebuild the touched inputs, delete the removes, lay out the adds ----------
          WireRewires(d, rc, wiring, actions);
          DeleteRemoves(d, rc, actions);
          LayoutAdds(d, rc, addGuids, actions);
          ApplyRenames(d, rc, actions);   // Stage 6: behaviour-neutral - invisible to the fingerprint, so it rides layer 1 unchanged

          // ---- LAYER 1 (predicted-delta) - needs no re-solve; a mismatch reverts everything (adds too) -------
          var actual = BehaviourFingerprint.Of(d);
          if (actual != predicted)
          {
            Rollback(d, actions);   // pre-commit: reverse every action (GUARD #5), no undo event recorded
            done?.Invoke(ApplyOutcome.Stop("Simplify stopped", "The applied change did not match what the preview declared - a stray edit slipped in - so Dwight reverted it. Nothing on the canvas changed. Please report this.")
              .With(RefineFeedback.Structural("the apply touched something the plan did not declare")));   // #104
            return;
          }

          if (actions.Count == 0) { done?.Invoke(ApplyOutcome.Ok("Nothing to change - the selection is already best-practice.")); return; }

          // ---- COMPLETENESS (RR8 #15 fix C) - every ACCEPTED change must have TAKEN EFFECT ------------------
          // Equivalence proves "nothing unintended changed"; this proves "the intended change happened". The
          // inert-no-op class (an externalize slider left unwired - identical outputs, matching fingerprint - that
          // BOTH layers pass) lives exactly in that gap. Pre-commit, so a miss reverts like a layer-1 mismatch.
          if (!CompletenessOk(d, rc, addGuids, out var incomplete))
          {
            Rollback(d, actions);
            done?.Invoke(ApplyOutcome.Stop("Simplify reverted", $"An accepted change did not take effect ({incomplete}), so Dwight reverted it - nothing on the canvas changed. Please report this.")
              .With(RefineFeedback.Incomplete(incomplete)));   // #104
            return;
          }

          // ---- COMMIT one undo event. Everything past here is POST-COMMIT and reverts via undo (MUST-FIX 3) --
          Dwight.Session.ChangeLog.NoteVerb(d, d.UndoUtil.RecordEvent("Dwight: Simplify (better logic)", actions), "Dwight: Simplify (better logic)", DwightVerbs.Simplify);   // Brief AP: the record's id
          rc.AppliedAddGuids = new List<Guid>(addGuids.Values);   // item 10: a dock reopen tells the change is in place by these
          MapAddsToChanges(rc, addGuids);   // RR8 #21: index each created guid by its origin change for click-to-locate
          Instances.RedrawCanvas();
        }
        catch (Exception ex)
        {
          // PRE-COMMIT failure only (RecordEvent is the LAST statement in the try): manual rollback, no event.
          try { Rollback(d, actions); } catch { }
          done?.Invoke(ApplyOutcome.Fail("Simplify hit a problem", "Dwight hit an error applying the change: " + ex.Message + " Any partial change was reverted."));
          return;
        }

        // ---- POST-COMMIT: LAYER 2 across the re-solve. Owns its own errors and reverts via undo (MUST-FIX 3) -
        try { ArmLayer2(d, rc, beforeOutputs, done); }
        catch (Exception ex)
        {
          var defused = RevertAndDefuseRedo(d);
          done?.Invoke(ApplyOutcome.Fail("Could not verify - reverted", "Dwight could not start the verification (" + ex.Message + "), so it reverted the change." + RedoWarn(defused)));
        }
      });
    }

    // ==== LAYER 2: re-solve, capture, compare, revert-on-diff (GUARD #4 discipline) =======================

    private static void ArmLayer2(GH_Document doc, SimplifyResolve rc, List<OutputSample> beforeOutputs, Action<ApplyOutcome> done)
    {
      GH_Document.SolutionEndEventHandler handler = null;
      Eto.Forms.UITimer timeout = null;
      var fired = false;   // GUARD #4: first fire only

      void Finish(bool verifiedOk, bool couldNotVerify, string title, string message, RefineFeedback refine = null)
      {
        // GUARD #4: unhook BEFORE any undo (undo schedules its own solution; a live handler would re-enter here).
        try { if (handler != null) doc.SolutionEnd -= handler; } catch { }
        try { timeout?.Stop(); } catch { }
        if (verifiedOk) { done?.Invoke(ApplyOutcome.Ok(SuccessSummary(rc))); return; }
        var defused = RevertAndDefuseRedo(doc);   // one Ctrl-Z of work reversed; GUARD #1 clears the redo record
        var msg = message + RedoWarn(defused);
        // #92/#104: a layer-2 revert carries its structured, VALUE-FREE feedback (differing output names + class),
        // so the panel can OFFER a corrected proposal - never auto-apply one.
        done?.Invoke((couldNotVerify ? ApplyOutcome.Fail(title, msg) : ApplyOutcome.Stop(title, msg)).With(refine));
      }

      handler = (sender, e) =>
      {
        if (fired) return;   // GUARD #4
        fired = true;
        try
        {
          var afterOutputs = CaptureOutputs(doc, rc.Layer2Params);
          if (OutputEquivalence.Equivalent(beforeOutputs, afterOutputs))
            Finish(true, false, null, null);
          else
          {
#if DEBUG
            LogLayer2Diff(beforeOutputs, afterOutputs);   // structural (tree changed) vs value-only (likely a shape change or non-determinism)
#endif
            Finish(false, false, "Simplify reverted", "The simplified version computed different results for your current inputs, so Dwight reverted it - the canvas is back as it was. " + DiffReport(beforeOutputs, afterOutputs),
              RefineFeedback.MismatchClassified(Layer2Diffs(beforeOutputs, afterOutputs)));   // #104: names + structure/value class
          }
        }
        catch (Exception ex) { Finish(false, true, "Could not verify - reverted", "Dwight could not read the outputs after re-solving (" + ex.Message + "), so it reverted the change to be safe."); }
      };

      try { doc.SolutionEnd += handler; } catch { }
      // GUARD #4: if the re-solve wedges and SolutionEnd never fires, revert + report rather than hang forever.
      timeout = new Eto.Forms.UITimer { Interval = 12.0 };
      timeout.Elapsed += (s, e) => { if (!fired) { fired = true; Finish(false, true, "Could not verify - reverted", "The re-solve did not finish in time, so Dwight reverted the change to be safe."); } };
      timeout.Start();
    }

    /// <summary>GUARD #1: undo the committed event, then CLEAR the redo record so Ctrl-Y cannot re-apply the exact
    /// mutation verification just rejected. GH 8.24 (ADR-027 min) exposes <c>ClearRedo</c> directly - if it ever
    /// throws we degrade to a WARNING in the report, never to a full-history wipe. Returns whether redo was
    /// defused.</summary>
    internal static bool RevertAndDefuseRedo(GH_Document doc)
    {
      Dwight.Session.ChangeLog.Reverting(doc);   // Brief AP: never offered for redo (redo is cleared below as well)
      try { doc.Undo(); } catch { }   // reverts the recorded "Dwight: Simplify" event (one Ctrl-Z)
      var defused = false;
      try { doc.UndoServer?.ClearRedo(); defused = true; } catch { }
      try { Instances.RedrawCanvas(); } catch { }
      return defused;
    }

    internal static string RedoWarn(bool defused) => defused ? "" : " Do not press Ctrl-Y - it would re-apply the reverted change.";

    // ==== COMPLETENESS (RR8 #15 fix C): the intended change actually happened =============================

    /// <summary>Assert every ACCEPTED change took effect, so an inert no-op (the RR8 #15 orphan: an externalize
    /// slider left unwired, which both verification layers pass because an unconnected node computes nothing) is a
    /// FAILED apply, not a false "verified". Keyed off INTENT, not the recorded wiring: every add nominated as an
    /// externalize (<see cref="AddNode.ExternalizeTargetId"/>) must feed at least one consumer; every removal must
    /// be gone. Returns false with a human reason on the first violation.</summary>
    private static bool CompletenessOk(GH_Document d, SimplifyResolve rc, Dictionary<int, Guid> addGuids, out string failure)
    {
      failure = null;
      try
      {
        var effects = new List<(bool, string)>();
        // RR14 #27: an added Number Slider exists ONLY to externalize a value, so it MUST feed at least one
        // consumer - an unwired slider is inert. Keyed off the SLIDER TYPE, not ExternalizeTargetId, so an
        // externalize whose target metadata never resolved (the RR14 gap) is still caught, not silently skipped.
        var addList = rc.Proposal.Add ?? new List<AddNode>();
        for (var i = 0; i < addList.Count; i++)
        {
          if (!IsSliderType(addList[i]?.Type)) continue;
          var wired = addGuids.TryGetValue(i, out var g) && d.FindObject(g, false) is IGH_Param slider
                      && slider.Recipients != null && slider.Recipients.Count > 0;
          effects.Add((wired, "an externalized slider was left unconnected"));
        }
        foreach (var id in rc.Proposal.Remove ?? new List<int>())
          if (rc.IdToGuid.TryGetValue(id, out var rg))
            effects.Add((d.FindObject(rg, false) == null, "a component that should have been removed is still present"));

        failure = Completeness.FirstNoOp(effects);   // GH-free decision (headless-tested)
        return failure == null;
      }
      catch (Exception ex) { failure = ex.Message; return false; }
    }

    private static bool IsSliderType(string type) => (type ?? "").Trim().Equals("Number Slider", StringComparison.OrdinalIgnoreCase);

    // ==== freshness (GUARD #2) ============================================================================

    internal static bool SolverDisabled(GH_Document doc)
    { try { return !GH_Document.EnableSolutions; } catch { return false; } }

    internal static void EnsureSolved(GH_Document doc)
    {
      try { if (doc.Objects.Any(o => o is IGH_ActiveObject a && a.Phase == GH_SolutionPhase.Blank)) doc.NewSolution(false); }
      catch { }
    }

    // ==== instantiate + harvest (the G.1 sequence) =======================================================

    /// <summary>Instantiate each add with a PRE-STAMPED instance guid (so the predictor stays pure), place it,
    /// and record the AddObject action FIRST (GUARD #5). Then harvest each add's intrinsic FpObject BEFORE any
    /// wiring, so its type + persistent defaults are captured while the plan owns the wiring (the G.1 order).</summary>
    private static Dictionary<int, Guid> InstantiateAdds(GH_Document d, SimplifyResolve rc, List<IGH_UndoAction> actions, out Dictionary<int, FpObject> harvested)
    {
      var addGuids = new Dictionary<int, Guid>();
      harvested = new Dictionary<int, FpObject>();
      var addList = rc.Proposal.Add ?? new List<AddNode>();
      var created = new Dictionary<int, IGH_DocumentObject>();

      for (var i = 0; i < addList.Count; i++)   // pass 1: create + place + record (identity exists from here)
      {
        var node = addList[i];
        var obj = CreateComponent(node.Type, rc);
        if (obj == null) throw new InvalidOperationException($"could not instantiate \"{node?.Type}\"");
        var guid = Guid.NewGuid();
        try { obj.NewInstanceGuid(guid); } catch { guid = obj.InstanceGuid; }   // pre-stamp; else its own
        if (!string.IsNullOrWhiteSpace(node.Nick)) { try { obj.NickName = node.Nick; } catch { } }
        BuildSlider(obj, node, rc);   // GUARD #6: an externalize slider is born holding the source's live value
        try { obj.CreateAttributes(); } catch { }
        d.AddObject(obj, false, d.Objects.Count);
        actions.Add(new GH_AddObjectAction(obj));
        addGuids[i] = guid;
        created[i] = obj;
      }

      foreach (var kv in created)   // pass 2: harvest each add's intrinsic tokens BEFORE wiring (empty sources)
      {
        var fo = BehaviourFingerprint.ReadObject(kv.Value);
        if (fo != null) harvested[kv.Key] = fo;
      }
      return addGuids;
    }

    private static IGH_DocumentObject CreateComponent(string type, SimplifyResolve rc)
    {
      try
      {
        if (rc.AddType.TryGetValue((type ?? "").Trim(), out var proxyGuid) && proxyGuid != Guid.Empty)
          return Instances.ComponentServer.EmitObjectProxy(proxyGuid)?.CreateInstance();
      }
      catch { }
      return null;
    }

    /// <summary>GUARD #6: when an add is a Number Slider standing in for a hardcoded source (externalize), the
    /// plugin reads the REPLACED source's current value (at preview time, into <see cref="ExternalizeSpec"/>) and
    /// constructs the slider to hold it - value, integer-vs-float type, a sensible range. The model named the
    /// lever; it never supplied the number.</summary>
    private static void BuildSlider(IGH_DocumentObject obj, AddNode node, SimplifyResolve rc)
    {
      if (!(obj is GH_NumberSlider slider)) return;
      if (node.Externalize != null)
      {
        try
        {
          var value = node.Externalize.Value;
          var isInt = node.Externalize.Integer;
          var lo = (decimal)Math.Min(0, Math.Floor(value));
          var hi = (decimal)Math.Max(1, Math.Ceiling(value == 0 ? 10 : value * 2));
          slider.Slider.Type = isInt ? Grasshopper.GUI.Base.GH_SliderAccuracy.Integer : Grasshopper.GUI.Base.GH_SliderAccuracy.Float;
          slider.Slider.Minimum = lo; slider.Slider.Maximum = hi;
          slider.SetSliderValue((decimal)value);
        }
        catch { }
      }
#if DEBUG
      // LAYER-2 sabotage: corrupt the value AFTER the externalize build, so the harvest captures the wrong value
      // (layer 1 passes: predicted == actual) but the re-solved output differs from the original (layer 2 catches).
      if (PendingSabotage == Sabotage.ValueChange) { PendingSabotage = Sabotage.None; LastRunSabotaged = true; try { slider.SetSliderValue(slider.CurrentValue + 1); } catch { } }
#endif
    }

    // ==== the ONE computed wiring (MUST-FIX 2) ===========================================================

    /// <summary>Per TOUCHED input param, its COMPLETE final ordered source list, computed by IN-PLACE substitution
    /// from the input's LIVE current sources (RR4 review completion): a surviving source keeps its position, a
    /// removed source is substituted in place by the next declared rewire source, extras append. Both
    /// <see cref="BuildPlan"/> and <see cref="WireRewires"/> derive from THIS one list, so they cannot diverge -
    /// the source ORDER the fingerprint hashes matches by construction - and a proposal that does not restate an
    /// untouched kept source cannot destroy it. Keyed by the live param (reference identity), so a param nested in
    /// a component needs no by-guid re-find.</summary>
    private static Dictionary<IGH_Param, List<IGH_Param>> ComputeFinalWiring(GH_Document d, SimplifyResolve rc, Dictionary<int, Guid> addGuids)
    {
      var removedGuids = new HashSet<Guid>();
      foreach (var id in rc.Proposal.Remove ?? new List<int>())
        if (rc.IdToGuid.TryGetValue(id, out var g)) removedGuids.Add(g);

      // group the DECLARED rewire sources by their target input param (live reference), in listed order.
      var externalizeAdds = new HashSet<int>(rc.ExternalizeWiring?.Keys ?? Enumerable.Empty<int>());
      var declaredByInput = new Dictionary<IGH_Param, List<IGH_Param>>();
      foreach (var w in rc.Proposal.Rewires ?? new List<Rewire>())
      {
        // RR7: the plugin OWNS externalize wiring - ignore any model rewire whose source is an externalize slider,
        // so a stray/wrong model rewire can never place the slider on the wrong port.
        if (w != null && IsAddRef(w.From, out var ai) && externalizeAdds.Contains(ai)) continue;
        var target = ResolveInputParam(d, rc, addGuids, w.To, w.ToPort);
        var source = ResolveOutputParam(d, rc, addGuids, w.From, w.FromPort);
        if (target == null || source == null) continue;
        if (!declaredByInput.TryGetValue(target, out var list)) { list = new List<IGH_Param>(); declaredByInput[target] = list; }
        list.Add(source);
      }

      // merge each touched input's LIVE current sources with the declared ones, in place.
      var wiring = new Dictionary<IGH_Param, List<IGH_Param>>();
      foreach (var kv in declaredByInput)
        wiring[kv.Key] = WiringMerge.InPlace(CurrentSources(kv.Key), s => removedGuids.Contains(OwnerGuid(s)), kv.Value);

      // RE-FEED EVERY input that still references a removed source. Two reasons: (1) a removed source may feed
      // several inputs while the model declares a rewire for only one (externalizing a shared width); (2) GH
      // auto-removes a deleted source from ITS consumers, so an undeclared consumer left OUT of the plan diverges
      // from the prediction - a false "stray edit" layer-1 stop. Learn removed-source -> replacement keyed by the
      // SOURCE PARAM guid, so a MULTI-OUTPUT component (GhPython's area vs centroid) maps each output independently
      // and never wires one output's consumer to another output (a type mismatch GH would reject, causing exactly
      // that divergence). A removed source with a declared replacement is substituted in place; one without is
      // dropped, mirroring GH - a genuinely-starved kept consumer is then a real change layer 2 reverts, not a
      // false layer-1 stop.
      var replacement = new Dictionary<Guid, IGH_Param>();
      foreach (var kv in declaredByInput)
      {
        var declared = kv.Value; var di = 0;
        foreach (var s in CurrentSources(kv.Key))
        {
          if (!removedGuids.Contains(OwnerGuid(s))) continue;   // key by the SOURCE PARAM guid so multi-output maps independently
          if (di < declared.Count)
          {
            if (s != null && !replacement.ContainsKey(s.InstanceGuid)) replacement[s.InstanceGuid] = declared[di];
            di++;
          }
        }
      }
      foreach (var id in rc.Proposal.Remove ?? new List<int>())
        {
          if (!rc.IdToGuid.TryGetValue(id, out var rg)) continue;
          foreach (var outp in OutputParamsOf(d.FindObject(rg, false)))
            foreach (var consumer in Recipients(outp))
              if (consumer != null && !wiring.ContainsKey(consumer))
              {
                wiring[consumer] = SubstituteRemoved(CurrentSources(consumer), removedGuids, replacement);
#if DEBUG
                try { Rhino.RhinoApp.WriteLine($"Dwight rewire: re-fed an undeclared consumer of a removed source (\"{consumer.Name}\") - {wiring[consumer].Count} source(s) after substitution."); } catch { }
#endif
              }
        }

      // RR7: wire each externalize slider to its PLUGIN-COMPUTED target port(s) - never the model's rewire. The
      // ports were read from the live graph (a removed source's consumers, or the internalized input), so the
      // slider lands on PRECISELY the input the value belonged to, with the value+type the plugin read.
      foreach (var kv in rc.ExternalizeWiring ?? new Dictionary<int, List<(Guid, int)>>())
      {
        if (!addGuids.TryGetValue(kv.Key, out var sliderGuid) || !(d.FindObject(sliderGuid, false) is IGH_Param slider)) continue;
        foreach (var (owner, port) in kv.Value)
        {
          var input = InputPort(d.FindObject(owner, false), port);
          if (input != null) wiring[input] = WiringMerge.InPlace(CurrentSources(input), s => removedGuids.Contains(OwnerGuid(s)), new List<IGH_Param> { slider });
        }
      }
      return wiring;
    }

    /// <summary>Rebuild an input's source list, replacing each removed source with its learned replacement in place
    /// (a removed source with no replacement is dropped - the removal-coverage validator guards that case).</summary>
    private static List<IGH_Param> SubstituteRemoved(List<IGH_Param> current, HashSet<Guid> removed, Dictionary<Guid, IGH_Param> replacement)
    {
      var result = new List<IGH_Param>();
      foreach (var s in current)
      {
        if (!removed.Contains(OwnerGuid(s))) { result.Add(s); continue; }
        if (s != null && replacement.TryGetValue(s.InstanceGuid, out var repl)) result.Add(repl);
        // else: a removed source with no declared replacement -> dropped, exactly as GH drops a deleted source from its consumer
      }
      return result;
    }

    private static IEnumerable<IGH_Param> OutputParamsOf(IGH_DocumentObject obj)
    {
      if (obj is IGH_Param p) return new[] { p };
      if (obj is IGH_Component c) return c.Params.Output;
      return System.Array.Empty<IGH_Param>();
    }

    private static IEnumerable<IGH_Param> Recipients(IGH_Param source)
    {
      var list = new List<IGH_Param>();
      try { if (source?.Recipients != null) foreach (var r in source.Recipients) if (r != null) list.Add(r); } catch { }
      return list;
    }

    private static List<IGH_Param> CurrentSources(IGH_Param input)
    {
      var list = new List<IGH_Param>();
      try { if (input?.Sources != null) foreach (var s in input.Sources) if (s != null) list.Add(s); } catch { }
      return list;
    }

    private static Guid OwnerGuid(IGH_Param p)
    {
      try { var owner = p?.Attributes?.GetTopLevel?.DocObject as IGH_DocumentObject ?? p; return owner?.InstanceGuid ?? Guid.Empty; } catch { return Guid.Empty; }
    }

    private static SimplifyPlan BuildPlan(SimplifyResolve rc, Dictionary<int, FpObject> harvested, Dictionary<IGH_Param, List<IGH_Param>> wiring)
    {
      var plan = new SimplifyPlan();
      foreach (var id in rc.Proposal.Remove ?? new List<int>())
        if (rc.IdToGuid.TryGetValue(id, out var g)) plan.Removed.Add(g);
      foreach (var kv in harvested) plan.Added.Add(kv.Value);
      foreach (var kv in wiring)
        plan.Wires.Add(new PlanWire(kv.Key.InstanceGuid, kv.Value.Select(p => p.InstanceGuid).ToArray()));
      return plan;
    }

    private static void WireRewires(GH_Document d, SimplifyResolve rc, Dictionary<IGH_Param, List<IGH_Param>> wiring, List<IGH_UndoAction> actions)
    {
      foreach (var kv in wiring)
      {
        var target = kv.Key;
        try
        {
          actions.Add(new GH_WireAction(target));   // one snapshot per touched input covers its undo
          target.RemoveAllSources();                // REBUILD to the complete declared list, in order (MUST-FIX 2)
          foreach (var source in kv.Value) target.AddSource(source);
        }
        catch { }
      }
#if DEBUG
      // LAYER-1 sabotage: an undeclared wire the plan did not predict - actual fingerprint != predicted.
      if (PendingSabotage == Sabotage.StrayRewire) { PendingSabotage = Sabotage.None; LastRunSabotaged = true; InjectStrayRewire(d, rc, actions); }
#endif
    }

    private static void DeleteRemoves(GH_Document d, SimplifyResolve rc, List<IGH_UndoAction> actions)
    {
      foreach (var id in rc.Proposal.Remove ?? new List<int>())
      {
        if (!rc.IdToGuid.TryGetValue(id, out var g)) continue;
        var obj = d.FindObject(g, false);
        if (obj == null) continue;
        try { actions.Add(new GH_RemoveObjectAction(obj)); d.RemoveObject(obj, false); } catch { }
      }
    }

    /// <summary>Place each add. RR5 #1: an add that REPLACES a known source is born at that source's pivot (the
    /// slider lands where the Panel it replaces sat - the placement is deterministic, so the model emits no
    /// coordinate, ADR-056). Only adds that replace nothing specific fall to the generic stack just BELOW the
    /// selection's bounds (never to the left, where they collide). A minimal diff adds 0-3 components; a full
    /// engine layout of the new sub-graph is later polish.</summary>
    /// <summary>RR8 #21: build change-index -> created guids from each add's <see cref="AddNode.OriginChange"/>, so
    /// the applied "done" list can locate exactly the components a change created (its slider). Add index i in the
    /// flattened proposal matches <paramref name="addGuids"/>[i].</summary>
    private static void MapAddsToChanges(SimplifyResolve rc, Dictionary<int, Guid> addGuids)
    {
      rc.AppliedAddByChange = new Dictionary<int, List<Guid>>();
      var adds = rc.Proposal.Add ?? new List<AddNode>();
      for (var i = 0; i < adds.Count; i++)
      {
        var ci = adds[i]?.OriginChange ?? -1;
        if (ci < 0 || !addGuids.TryGetValue(i, out var g)) continue;
        if (!rc.AppliedAddByChange.TryGetValue(ci, out var list)) { list = new List<Guid>(); rc.AppliedAddByChange[ci] = list; }
        list.Add(g);
      }
    }

    private static void LayoutAdds(GH_Document d, SimplifyResolve rc, Dictionary<int, Guid> addGuids, List<IGH_UndoAction> actions)
    {
      var x = float.IsNaN(rc.SelectionBounds.Left) ? 0f : rc.SelectionBounds.Left;
      var y = (float.IsNaN(rc.SelectionBounds.Bottom) ? 0f : rc.SelectionBounds.Bottom) + 30f;
      var i = 0;
      foreach (var kv in addGuids)
      {
        var obj = d.FindObject(kv.Value, false);
        if (obj?.Attributes == null) continue;
        PointF anchor = default;
        var anchored = rc.AddAnchor != null && rc.AddAnchor.TryGetValue(kv.Key, out anchor);
        var pivot = anchored ? NudgeClear(d, obj, anchor, kv.Value) : new PointF(x, y + i * 70f);   // RR8 #20: keep an anchored add off existing components
        try { actions.Add(new GH_PivotAction(obj)); obj.Attributes.Pivot = pivot; obj.Attributes.ExpireLayout(); } catch { }
        if (!anchored) i++;   // only the generic stack advances; an anchored add sits at its replaced source
      }
    }

    /// <summary>RR8 #20: step an anchored add DOWN until its bounds clear every existing component, so a CASE-1
    /// externalize slider placed left of its input does not land over a neighbour. Deterministic (top-to-bottom,
    /// fixed step, bounded attempts); the removed-source CASE-2 spot is already empty so this no-ops there.</summary>
    private static PointF NudgeClear(GH_Document d, IGH_DocumentObject obj, PointF anchor, Guid selfGuid)
    {
      var pivot = anchor;
      try
      {
        for (var attempt = 0; attempt < 12; attempt++)
        {
          obj.Attributes.Pivot = pivot;
          obj.Attributes.ExpireLayout();
          obj.Attributes.PerformLayout();
          var b = obj.Attributes.Bounds;
          var clash = d.Objects.Any(o => o != null && o.InstanceGuid != selfGuid && o.Attributes != null
                        && RectangleF.Inflate(o.Attributes.Bounds, 6f, 6f).IntersectsWith(b));
          if (!clash) break;
          pivot = new PointF(pivot.X, pivot.Y + b.Height + 14f);   // one row down and retry
        }
      }
      catch { }
      return pivot;
    }

    /// <summary>Stage 6: apply the behaviour-NEUTRAL nickname edits, each as a GH_NickNameAction in the SAME undo
    /// record (one Ctrl-Z reverts everything). Nicknames are NOT in the behaviour fingerprint, so this runs before
    /// the layer-1 check without disturbing predicted == actual, and needs no layer-2 verification. Only kept
    /// components are renamed (the validator sealed any that already carry a user nickname).</summary>
    private static void ApplyRenames(GH_Document d, SimplifyResolve rc, List<IGH_UndoAction> actions)
    {
      foreach (var (guid, nick) in rc.Renames ?? new List<(Guid, string)>())
      {
        if (string.IsNullOrWhiteSpace(nick)) continue;
        var obj = d.FindObject(guid, false);
        if (obj == null) continue;
        try { actions.Add(new GH_NickNameAction(obj)); obj.NickName = nick; obj.Attributes?.ExpireLayout(); } catch { }
      }
    }

    // ==== ref resolution ================================================================================
    // A ref token: "k<id>" kept original, "a<index>" an add, "i<ext>" external input, "o<ext>" external output.

    private static IGH_Param ResolveInputParam(GH_Document d, SimplifyResolve rc, Dictionary<int, Guid> addGuids, string reff, int port)
    {
      var (kind, n) = Parse(reff); if (kind == '?') return null;
      switch (kind)
      {
        case 'k': return rc.IdToGuid.TryGetValue(n, out var g) ? InputPort(d.FindObject(g, false), port) : null;
        case 'a': return addGuids.TryGetValue(n, out var ag) ? InputPort(d.FindObject(ag, false), port) : null;
        case 'o': return rc.ExtOut.TryGetValue(n, out var cs) && cs.Count > 0 ? InputPort(d.FindObject(cs[0].Obj, false), cs[0].Port) : null;
        default: return null;
      }
    }

    private static IGH_Param ResolveOutputParam(GH_Document d, SimplifyResolve rc, Dictionary<int, Guid> addGuids, string reff, int port)
    {
      var (kind, n) = Parse(reff); if (kind == '?') return null;
      switch (kind)
      {
        case 'k': return rc.IdToGuid.TryGetValue(n, out var g) ? OutputPortParam(d.FindObject(g, false), port) : null;
        case 'a': return addGuids.TryGetValue(n, out var ag) ? OutputPortParam(d.FindObject(ag, false), port) : null;
        case 'i': return rc.ExtIn.TryGetValue(n, out var src) ? OutputPortParam(d.FindObject(src.Src, false), src.Port) : null;
        default: return null;
      }
    }

    private static (char kind, int n) Parse(string reff)
    {
      if (string.IsNullOrEmpty(reff) || reff.Length < 2 || !int.TryParse(reff.Substring(1), out var n)) return ('?', 0);
      return (reff[0], n);
    }

    /// <summary>True when a ref token names an ADD ("a&lt;index&gt;"); the index is returned - RR7 uses it to skip a
    /// model rewire that targets an externalize slider (the plugin owns that wiring).</summary>
    private static bool IsAddRef(string reff, out int n) { var (k, m) = Parse(reff); n = m; return k == 'a'; }

    private static IGH_Param InputPort(IGH_DocumentObject obj, int port)
    {
      if (obj is IGH_Param p) return p;
      if (obj is IGH_Component c && port >= 0 && port < c.Params.Input.Count) return c.Params.Input[port];
      return null;
    }
    private static IGH_Param OutputPortParam(IGH_DocumentObject obj, int port)
    {
      if (obj is IGH_Param p) return p;
      if (obj is IGH_Component c && port >= 0 && port < c.Params.Output.Count) return c.Params.Output[port];
      return null;
    }

    // ==== layer-2 output sampling (the preserved surface, by param guid) =================================

    internal static List<OutputSample> CaptureOutputs(GH_Document doc, List<Guid> paramGuids)
    {
      var samples = new List<OutputSample>();
      foreach (var g in paramGuids ?? new List<Guid>())
      {
        var p = doc.FindObject(g, false) as IGH_Param ?? (doc.FindObject(g, false) as IGH_Component)?.Params.Output.FirstOrDefault();
        samples.Add(SampleParam(p));
      }
      return samples;
    }

    /// <summary>
    /// A (tree-sig + value-digest) sample of a param's live VolatileData, plus a human LABEL (the owner's
    /// nickname/type) so the diff report can NAME the mismatches (G.2). The label is not part of the token.
    ///
    /// RR18 (second finding): this used to SAMPLE - the first 24 branches, the first 8 items each - which is a
    /// false-positive hole and the worst one in the system: an output differing at item 20 matched on its first 8
    /// and its count, digested EQUAL, and the user's components were deleted for a silently wrong replacement.
    /// It is now UNCAPPED - every branch, every item - streamed into a rolling hash, so coverage is complete while
    /// memory stays O(1). Cheap by construction: this surface is only the boundary outputs, and the solve that
    /// produced them already paid O(n). The equivalence check is the last thing standing between a wrong result
    /// and a deleted original, so it does not get to look at a sample.
    /// </summary>
    private static OutputSample SampleParam(IGH_Param p)
    {
      var label = OwnerLabel(p);
      try
      {
        var data = p?.VolatileData;
        if (data == null || data.PathCount == 0) return new OutputSample("0", "", label);
        var depth = 0; foreach (var path in data.Paths) if (path.Length > depth) depth = path.Length;
        var tree = data.PathCount + ":" + depth;

        return new OutputSample(tree, ValueDigest.Compose(data.PathCount, AllBranches(data)), label);
      }
      catch { return new OutputSample("?", "", label); }
    }

    /// <summary>Stream EVERY branch and EVERY item through the geometry-aware per-item digest - lazily, so a huge
    /// output is never materialised.</summary>
    private static IEnumerable<(int Count, IEnumerable<string> Items)> AllBranches(IGH_Structure data)
    {
      foreach (var path in data.Paths)
      {
        IList branch = null;
        try { branch = data.get_Branch(path); } catch { }
        yield return (branch?.Count ?? 0, Items(branch));
      }
    }

    private static IEnumerable<string> Items(IList branch)
    {
      if (branch == null) yield break;
      for (var k = 0; k < branch.Count; k++)
      {
        object goo = null;
        try { goo = branch[k]; } catch { }
        yield return GooDigest.Item(goo);
      }
    }

    private static string OwnerLabel(IGH_Param p)
    {
      try
      {
        var owner = p?.Attributes?.GetTopLevel?.DocObject as IGH_DocumentObject ?? p;
        if (owner == null) return "an output";
        return !string.IsNullOrWhiteSpace(owner.NickName) ? owner.NickName : owner.GetType().Name;
      }
      catch { return "an output"; }
    }

    // ==== rollback + reporting ===========================================================================

    internal static void Rollback(GH_Document d, List<IGH_UndoAction> actions)
    {
      for (var k = actions.Count - 1; k >= 0; k--) { try { actions[k].Undo(d); } catch { } }
      try { Instances.RedrawCanvas(); } catch { }
    }

    private static string SuccessSummary(SimplifyResolve rc) =>
      $"Simplified - kept {rc.Proposal.Keep.Count}, removed {rc.Proposal.Remove.Count}, added {rc.Proposal.Add.Count} component(s). " +
      "The outputs are unchanged for your current inputs (verified by re-solve); one Ctrl-Z reverts everything.";

    /// <summary>G.2: NAME the differing outputs, not just a count - the samples that were in the before-bag and
    /// are gone from the after-bag, by their owner label.</summary>
    private static string DiffReport(List<OutputSample> before, List<OutputSample> after)
    {
      try
      {
        var afterTokens = new HashSet<string>(after.Select(Token));
        var changed = before.Where(b => !afterTokens.Contains(Token(b))).Select(b => b.Label ?? "an output").Distinct().Take(6).ToList();
        return changed.Count == 0 ? "" : "The results changed at: " + string.Join(", ", changed) + ".";
      }
      catch { return ""; }
    }

    /// <summary>#104: the layer-2 multiset mismatch as CLASSIFIED per-output diffs for the refine feedback - the
    /// differing outputs BY NAME (DiffReport's law: a before-sample gone from the after-bag) plus a coarse class:
    /// STRUCTURE when the sample's tree signature vanished entirely from the after-bag, else VALUE (the same shape
    /// carrying different data). No ORDER class - the multiset compare is order-independent by design. All
    /// value-free (#89): a name, a class word, and the tree's path count - never a value. Item counts are unknown
    /// for a multiset sample (the digest is composed, not enumerated), so they stay 0 and the feedback is class-only.</summary>
    private static List<OutputDiff> Layer2Diffs(List<OutputSample> before, List<OutputSample> after)
    {
      var diffs = new List<OutputDiff>();
      try
      {
        var aList = after ?? new List<OutputSample>();
        var afterTokens = new HashSet<string>(aList.Select(Token));
        var afterTrees = new HashSet<string>(aList.Select(s => s.Tree ?? ""));
        foreach (var b in before ?? new List<OutputSample>())
        {
          if (afterTokens.Contains(Token(b))) continue;   // this sample is preserved
          var name = string.IsNullOrWhiteSpace(b.Label) ? "an output" : b.Label;
          var kind = afterTrees.Contains(b.Tree ?? "") ? MismatchKind.Value : MismatchKind.Structure;
          var paths = ParsePaths(b.Tree);   // the sample tree is "pathCount:depth"
          diffs.Add(new OutputDiff(name, kind, 0, 0, paths, paths));
          if (diffs.Count >= 6) break;
        }
      }
      catch { }
      return diffs;
    }

    private static int ParsePaths(string tree)
    {
      try { var c = (tree ?? "").Split(':'); return c.Length > 0 && int.TryParse(c[0], out var p) ? p : 0; }
      catch { return 0; }
    }

    private static string Token(OutputSample s) => (s.Tree ?? "") + "\u001F" + (s.Value ?? "");

#if DEBUG
    /// <summary>Diagnostic (RR): on a layer-2 revert, say per differing output whether its TREE changed (a real
    /// structural change) or only its VALUE (a shape change - or the definition computing a bit-different result
    /// each solve, i.e. non-determinism). Samples share Layer2Params order, so index i is the same param.</summary>
    private static void LogLayer2Diff(List<OutputSample> before, List<OutputSample> after)
    {
      try
      {
        var n = Math.Min(before.Count, after.Count);
        for (var i = 0; i < n; i++)
        {
          if (Token(before[i]) == Token(after[i])) continue;
          var treeChanged = before[i].Tree != after[i].Tree;
          Rhino.RhinoApp.WriteLine($"Dwight layer2 diff: \"{before[i].Label}\" tree before={before[i].Tree} after={after[i].Tree} -> {(treeChanged ? "STRUCTURAL (a real change)" : "value-only (a shape change, or non-determinism if the logic is unchanged)")}");
        }
        if (before.Count != after.Count) Rhino.RhinoApp.WriteLine($"Dwight layer2 diff: output COUNT before={before.Count} after={after.Count}");
      }
      catch { }
    }

    private static void InjectStrayRewire(GH_Document d, SimplifyResolve rc, List<IGH_UndoAction> actions)
    {
      // an undeclared rewire: point some kept input at a different kept output - layer 1 must catch it.
      try
      {
        var kept = rc.Proposal.Keep.Select(id => rc.IdToGuid.TryGetValue(id, out var g) ? d.FindObject(g, false) : null).Where(o => o != null).ToList();
        var target = kept.Select(o => InputPort(o, 0)).FirstOrDefault(p => p != null);
        var source = kept.Select(o => OutputPortParam(o, 0)).FirstOrDefault(p => p != null && p != target);
        if (target != null && source != null) { actions.Add(new GH_WireAction(target)); target.AddSource(source); }
      }
      catch { }
    }
#endif
  }
}
