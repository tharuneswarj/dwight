using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;
using Dwight.Data;
using Dwight.Simplify;

namespace Dwight.Eval
{
  /// <summary>The outcome of the second-input-state overfit check (#101/#93c), harness-only.</summary>
  internal sealed class SecondStateResult
  {
    public bool HasPerturbable;
    public bool PerturbationValid;
    public bool State2Match;
    public bool Verified;
    public bool CountsForOverfit;
    public SecondStateBucket Bucket;
    public int Roots;
    /// <summary>#118(4): a restore / read-back / document-proof failure ABORTS THE RUN, loudly - continuing would
    /// measure a drifted state-1 on every later row. Non-null = the caller must stop the whole run and report this.</summary>
    public string AbortReason;

    public static SecondStateResult Score(bool hasExternalInputs, bool hasPerturbable, bool valid, bool state2Match, int roots)
    {
      var v = SecondStateScore.Decide(built: true, state1Match: true, hasExternalInputs: hasExternalInputs,
        hasPerturbable: hasPerturbable, perturbationValid: valid, state2Match: state2Match);
      return new SecondStateResult { HasPerturbable = hasPerturbable, PerturbationValid = valid, State2Match = state2Match,
        Verified = v.Verified, CountsForOverfit = v.CountsForOverfit, Bucket = v.Bucket, Roots = roots };
    }
    public static SecondStateResult Abort(string reason) => new SecondStateResult { AbortReason = reason };

    /// <summary>F8 (#129): a row that verified in the gate trial but does NOT reproduce on re-instantiation - an
    /// anomaly (nondeterminism / harness bug), stamped to its own bucket, never verified, never in the overfit
    /// denominator. Any count of these is a finding to investigate before trusting the run's numbers.</summary>
    public static SecondStateResult NotReproduced(int roots) => new SecondStateResult
    { HasPerturbable = roots > 0, Bucket = SecondStateBucket.State1NotReproduced, Roots = roots };
  }

  /// <summary>
  /// Phase-8 WP-0 Stage C (#101/#118/#126): the SECOND-INPUT-STATE overfit check, HARNESS-ONLY (#93a - the product
  /// holds at single-input). Called only for a row that ALREADY verified on state 1. It perturbs the upstream value
  /// sources feeding the selection's boundary inputs, re-solves, and compares the originals against the beside-script
  /// 1:1 AGAIN - a script that hardcoded state 1's answer now diverges (the overfit ADR-095 named and #99 forbids
  /// graduating on). Everything here is async (`ScheduleSolution` + `SolutionEnd` + a per-solve watchdog, #117c) and
  /// the FILE IS NEVER SAVED.
  ///
  /// The perturbation targets are found by TRACING each boundary input's external source through single-source,
  /// zero-persistent-data pass-through relays to the root slider / single-line-numeric panel (#121b), DEDUPED by guid
  /// (#126a - siteLength feeds two Domain ports but is ONE source), and SNAPSHOTTED before any mutation so a second
  /// visit can never overwrite an original value. The validity gate and the comparison read the SAME solved state
  /// (#118a). The restore discipline is the full #118: try/finally + per-source read-back assert + a document-level
  /// restored proof (re-solve, digest-match the originals against state 1) + ABORT-THE-RUN on any restore failure.
  /// </summary>
  internal static class SecondStatePass
  {
    // the frac / multiple sequence length: at most this many combined states are tried before no-valid-perturbation.
    private const int MaxCombinedStates = 5;

    private sealed class Root
    {
      public IGH_DocumentObject Obj;
      public bool IsSlider;
      public GH_NumberSlider Slider;
      public GH_Panel Panel;
      public decimal OrigDec;      // the snapshot value (slider value / parsed panel number)
      public string OrigText;      // the panel's EXACT original string, for a byte-clean restore + read-back (#118.2)
      public List<decimal> Candidates = new List<decimal>();
    }

    public static void Run(ToScriptResolve rc, Action<SecondStateResult> done)
    {
      var doc = rc?.Doc;
      if (doc == null || SimplifyApply.SolverDisabled(doc))
      { done?.Invoke(SecondStateResult.Score(false, false, false, false, 0)); return; }   // can't measure; scores state 1

      // A1 (#133): ZERO external input ports -> SELF-CONTAINED. A single-point input state space, so state-1
      // verification is complete by construction; NOT a tripwire member (nothing external to perturb).
      if (rc.ExtIn == null || rc.ExtIn.Count == 0)
      { done?.Invoke(SecondStateResult.Score(hasExternalInputs: false, hasPerturbable: false, valid: false, state2Match: false, roots: 0)); return; }

      var (roots, truncated) = CollectRoots(doc, rc);   // #134: the broadened UPSTREAM CONE, deduped, snapshotted
      if (truncated) try { Rhino.RhinoApp.WriteLine($"Dwight eval: second-state ROOT CAP hit ({RootCap}) - some cone value sources were not perturbed (no silent caps)."); } catch { }
      // HAS external inputs but NONE reach a perturbable value source -> the genuine gap, the tripwire's member (#133).
      if (roots.Count == 0)
      { done?.Invoke(SecondStateResult.Score(hasExternalInputs: true, hasPerturbable: false, valid: false, state2Match: false, roots: 0)); return; }

      IGH_Component comp = null;
      List<OutputSample> state1Originals = null;    // the state-1 baseline the doc-proof + compare use
      List<IGH_Param> scriptOuts = null;
      HashSet<Guid> state1ErrorSet = null;          // #118a: the ORIGINALS' error set at state 1
      bool[] state1NonEmpty = null;                 // which boundary outputs were non-empty at state 1

      // one shared finish, so every early-out still runs the restore + proof (the try/finally spirit across async).
      void Fail(string reason) => Finish(doc, rc, comp, roots, state1Originals, false, false, false, done, reason);

      // ---- STEP 1: instantiate the script beside + capture the state-1 baseline (with the script present, which
      // cannot change the originals - forward dataflow, #118a) --------------------------------------------------
      Step(doc,
        mutate: d =>
        {
          comp = ScriptComponentShim.TryCreate(rc.Language, out var createErr);
          if (comp == null) throw new Exception("second-state: could not create the script (" + createErr + ")");
          if (!ScriptComponentShim.Shape(comp, rc.Iface, rc.Language, rc.Source, out var shapeErr))
            throw new Exception("second-state: could not shape the script (" + shapeErr + ")");
          try { comp.CreateAttributes(); } catch { }
          comp.Attributes.Pivot = new System.Drawing.PointF(rc.SelectionBounds.Left, rc.SelectionBounds.Bottom + 5000f);
          d.AddObject(comp, false);
          for (var i = 0; i < rc.Iface.Inputs.Count && i < comp.Params.Input.Count; i++)
            if (rc.ExtIn.TryGetValue(rc.Iface.Inputs[i].Ext, out var src))
            {
              var sp = OutputPortParam(d.FindObject(src.Src, false), src.Port);
              if (sp != null) comp.Params.Input[i].AddSource(sp);
            }
        },
        onSolved: () =>
        {
          try
          {
            // F8 (#129): the re-instantiated script errors here - it verified in the gate trial, so it MUST NOT.
            // That contradicts same-inputs re-solve determinism: an anomaly to SEE (its own bucket), not a benign
            // no-valid-perturbation. Clean up via Finish; the row keeps its trial verdict but is flagged.
            if (ScriptComponentShim.Errors(comp).Count > 0) { Finish(doc, rc, comp, roots, null, false, false, true, done, null); return; }
            state1Originals = SimplifyApply.CaptureOutputs(doc, rc.BoundaryOutParams);
            scriptOuts = ScriptComponentShim.ScriptOutputs(comp);
            var s1Script = SimplifyApply.CaptureOutputs(doc, scriptOuts.Select(p => p.InstanceGuid).ToList());
            state1ErrorSet = OriginalErrorSet(doc, rc);
            state1NonEmpty = rc.BoundaryOutParams.Select(g => ParamNonEmpty(doc, g)).ToArray();
            // F8: state 1 must still match here (deterministic re-solve). If it does not, that is the SAME anomaly -
            // nondeterminism or a harness bug - stamped State1NotReproduced, never laundered into a benign bucket.
            if (OutputEquivalence.Mismatches(state1Originals, s1Script).Count > 0) { Finish(doc, rc, comp, roots, state1Originals, false, false, true, done, null); return; }
            TryCandidate(doc, rc, comp, roots, state1Originals, scriptOuts, state1ErrorSet, state1NonEmpty, 0, done);
          }
          catch (Exception ex) { Fail("second-state baseline capture threw: " + ex.Message); }
        },
        onWatchdog: () => Fail("second-state baseline solve did not finish in time"));
    }

    // ---- STEP 2: try combined state k (all roots at their k-th candidate); validity + compare in ONE solve -----
    private static void TryCandidate(GH_Document doc, ToScriptResolve rc, IGH_Component comp, List<Root> roots,
      List<OutputSample> state1Originals, List<IGH_Param> scriptOuts, HashSet<Guid> state1ErrorSet, bool[] state1NonEmpty,
      int k, Action<SecondStateResult> done)
    {
      // exhausted, or no root can move at this index -> no valid perturbation was found.
      if (k >= MaxCombinedStates || roots.All(r => k >= r.Candidates.Count))
      { Finish(doc, rc, comp, roots, state1Originals, perturbationValid: false, state2Match: false, state1NotReproduced: false, done, null); return; }

      Step(doc,
        mutate: d =>
        {
          // ONE combined state (#101d): every root to its k-th candidate; a root with no candidate at k stays at
          // its SNAPSHOT (never a stale previous-candidate value).
          foreach (var r in roots) SetRoot(r, k < r.Candidates.Count ? r.Candidates[k] : (decimal?)null);
        },
        onSolved: () =>
        {
          try
          {
            // #118a VALIDITY GATE (same solve as the compare): the ORIGINALS must still solve clean, else both sides
            // degrade together and nulls compare "equal".
            var noNewError = OriginalErrorSet(doc, rc).All(g => state1ErrorSet.Contains(g));
            var nonEmptyOk = true;
            for (var i = 0; i < rc.BoundaryOutParams.Count; i++)
              if (state1NonEmpty[i] && !ParamNonEmpty(doc, rc.BoundaryOutParams[i])) { nonEmptyOk = false; break; }

            var originals2 = SimplifyApply.CaptureOutputs(doc, rc.BoundaryOutParams);
            // F7 (#128): the perturbation must have DONE something - the originals' outputs must DIFFER from state 1
            // in >=1 output. Proves it PROPAGATED and that the check has POWER: a no-op perturbation (a candidate
            // snapping onto the current value on an Even/Odd/low-precision slider, or a text change that failed to
            // propagate) cannot expose a hardcode - a hardcoded script also still matches a state that equals state 1.
            var perturbationHadEffect = OutputEquivalence.Mismatches(state1Originals, originals2).Count > 0;

            // any of the three failing -> this candidate is not a valid perturbation; try the next combined state.
            if (!noNewError || !nonEmptyOk || !perturbationHadEffect)
            { TryCandidate(doc, rc, comp, roots, state1Originals, scriptOuts, state1ErrorSet, state1NonEmpty, k + 1, done); return; }

            // VALID perturbation: compare the originals against the script 1:1, in THIS solve.
            var script2 = SimplifyApply.CaptureOutputs(doc, scriptOuts.Select(p => p.InstanceGuid).ToList());
            var match = OutputEquivalence.Mismatches(originals2, script2).Count == 0;
            Finish(doc, rc, comp, roots, state1Originals, perturbationValid: true, state2Match: match, state1NotReproduced: false, done, null);
          }
          catch (Exception ex) { Finish(doc, rc, comp, roots, state1Originals, false, false, false, done, "second-state compare threw: " + ex.Message); }
        },
        onWatchdog: () => Finish(doc, rc, comp, roots, state1Originals, false, false, false, done, "second-state perturbed solve did not finish in time"));
    }

    // ---- STEP 3: the full #118 restore discipline, then remove the script and report ---------------------------
    private static void Finish(GH_Document doc, ToScriptResolve rc, IGH_Component comp, List<Root> roots,
      List<OutputSample> state1Originals, bool perturbationValid, bool state2Match, bool state1NotReproduced,
      Action<SecondStateResult> done, string abortReason)
    {
      Step(doc,
        mutate: d =>
        {
          foreach (var r in roots) RestoreRoot(r);   // (1) try/finally restore - to the SNAPSHOT, always
          try { if (comp != null && d.FindObject(comp.InstanceGuid, false) != null) d.RemoveObject(comp, false); } catch { }
        },
        onSolved: () =>
        {
          try
          {
            // (2) READ-BACK ASSERT per source: the widget is back to its exact snapshot.
            var readBackOk = roots.All(ReadBackOk);
            // (3) THE DOCUMENT-LEVEL RESTORED PROOF: the DOCUMENT computes what it computed at state 1, not merely
            // that a widget looks right. Nearly free - the settle solve already ran.
            var proofOk = state1Originals == null ||
                          OutputEquivalence.Equivalent(state1Originals, SimplifyApply.CaptureOutputs(doc, rc.BoundaryOutParams));

            if (abortReason != null) { done?.Invoke(SecondStateResult.Abort(abortReason)); return; }
            // (4) any restore failure ABORTS THE RUN, loudly - a drifted state 1 would silently corrupt every later row.
            if (!readBackOk) { done?.Invoke(SecondStateResult.Abort("second-state RESTORE FAILED a read-back assert - a source did not return to its snapshot")); return; }
            if (!proofOk) { done?.Invoke(SecondStateResult.Abort("second-state RESTORE FAILED the document-level proof - the originals do not recompute state 1")); return; }

            // F8 (#129): a verified row that did not reproduce on re-instantiation is an ANOMALY, its own bucket.
            if (state1NotReproduced) { done?.Invoke(SecondStateResult.NotReproduced(roots.Count)); return; }
            // the perturbable path always has external inputs (Run bailed to SelfContained otherwise).
            done?.Invoke(SecondStateResult.Score(hasExternalInputs: true, hasPerturbable: true, valid: perturbationValid, state2Match: state2Match, roots: roots.Count));
          }
          catch (Exception ex) { done?.Invoke(SecondStateResult.Abort("second-state restore/proof threw: " + ex.Message)); }
          try { Instances.RedrawCanvas(); } catch { }
        },
        onWatchdog: () => done?.Invoke(SecondStateResult.Abort("second-state restore solve did not finish in time")));
    }

    // ==== roots: trace (#121b) + dedupe (#126a) + perturbable test (#126b) + snapshot =============================

    /// <summary>The most perturbable value sources any ONE selection's boundary cone should ever surface; if the
    /// cone is somehow bigger, <see cref="RootConeWalk"/> truncates and the caller LOGS it (no silent caps, #134).</summary>
    private const int RootCap = 32;

    /// <summary>#134: collect every perturbable value source in the UPSTREAM CONE of the boundary inputs - recursing
    /// through computed components, not stopping at the first relay. The GH-free <see cref="RootConeWalk"/> owns the
    /// walk decision; this shim supplies the live-graph kind / upstream / id, then MakeRoot applies the final
    /// perturbable test (numeric single-line) + snapshot to each collected value source, deduped by guid (#126a).</summary>
    private static (List<Root> Roots, bool Truncated) CollectRoots(GH_Document doc, ToScriptResolve rc)
    {
      var seeds = rc.ExtIn.Values.Select(v => doc.FindObject(v.Src, false)).Where(o => o != null);
      var (valueSources, truncated) = RootConeWalk.Collect(
        seeds,
        kindOf: o => IsValueSourceNode(o) ? NodeKind.ValueSource : NodeKind.Passthrough,
        upstreamOf: UpstreamOwners,
        idOf: o => o.InstanceGuid,
        depthBound: 16, rootCap: RootCap);

      var byGuid = new Dictionary<Guid, Root>();
      foreach (var vs in valueSources)
      {
        if (byGuid.ContainsKey(vs.InstanceGuid)) continue;
        var r = MakeRoot(vs);
        if (r != null) byGuid[vs.InstanceGuid] = r;
      }
      return (byGuid.Values.ToList(), truncated);
    }

    /// <summary>A perturbable value source: a slider, or a panel with NO input (a standalone constant). A panel WITH
    /// a source is displaying an upstream value - it is a pass-through the walk recurses through, not a settable
    /// constant. Everything else (a component, a relay, a persistent-data param) is a pass-through.</summary>
    private static bool IsValueSourceNode(IGH_DocumentObject o)
      => o is GH_NumberSlider || (o is GH_Panel pn && pn.SourceCount == 0);

    /// <summary>The upstream owners feeding a node's inputs - for a component, every input param's sources; for a
    /// param/relay/sourced-panel, its own sources. A persistent-data param has no sources, so the walk stops there.</summary>
    private static IEnumerable<IGH_DocumentObject> UpstreamOwners(IGH_DocumentObject obj)
    {
      var inputs = new List<IGH_Param>();
      if (obj is IGH_Param p) inputs.Add(p);
      else if (obj is IGH_Component c) inputs.AddRange(c.Params.Input);
      foreach (var ip in inputs)
        foreach (var s in (ip.Sources ?? new List<IGH_Param>()))
        {
          var owner = s?.Attributes?.GetTopLevel?.DocObject as IGH_DocumentObject ?? s;
          if (owner != null) yield return owner;
        }
    }

    /// <summary>#126b: a root is perturbable iff it is a slider OR a single-line NUMERIC panel. A degenerate source
    /// (min==max slider, 0-valued panel) is STILL a root - it exists, so it lands in no-valid-perturbation, not
    /// no-perturbable (#123). A multi-line or non-numeric panel, or anything else, is not a root.</summary>
    private static Root MakeRoot(IGH_DocumentObject obj)
    {
      if (obj is GH_NumberSlider sl)
      {
        var isInt = sl.Slider.Type == Grasshopper.GUI.Base.GH_SliderAccuracy.Integer;
        var cur = sl.Slider.Value;
        return new Root { Obj = obj, IsSlider = true, Slider = sl, OrigDec = cur, OrigText = null,
          Candidates = PerturbationSequence.Candidates(sl.Slider.Minimum, sl.Slider.Maximum, cur, isInt) };
      }
      if (obj is GH_Panel pn)
      {
        var text = pn.UserText ?? "";
        if (text.IndexOf('\n') >= 0 || text.IndexOf('\r') >= 0) return null;   // multi-line -> emits a list, not one value
        if (!decimal.TryParse(text.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var cur)) return null;   // non-numeric
        return new Root { Obj = obj, IsSlider = false, Panel = pn, OrigDec = cur, OrigText = text,
          Candidates = PerturbationSequence.CandidatesFromCurrent(cur) };
      }
      return null;
    }

    // ==== perturb / restore / read-back ==========================================================================

    /// <summary>Set a root to a value (null = restore to its snapshot). Expire so the pending solve recomputes it.</summary>
    private static void SetRoot(Root r, decimal? value)
    {
      try
      {
        if (r.IsSlider) r.Slider.SetSliderValue(value ?? r.OrigDec);
        else r.Panel.UserText = value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : r.OrigText;
        r.Obj.ExpireSolution(false);
      }
      catch { }
    }

    private static void RestoreRoot(Root r) => SetRoot(r, null);

    /// <summary>#118.2: the widget is back to its EXACT snapshot - slider value equal (Type/precision round-trip
    /// through SetSliderValue is the same op both times), panel text byte-identical.</summary>
    private static bool ReadBackOk(Root r)
    {
      try { return r.IsSlider ? r.Slider.Slider.Value == r.OrigDec : (r.Panel.UserText ?? "") == (r.OrigText ?? ""); }
      catch { return false; }
    }

    // ==== the originals' health (validity gate baseline, #118a) ==================================================

    private static HashSet<Guid> OriginalErrorSet(GH_Document doc, ToScriptResolve rc)
    {
      var set = new HashSet<Guid>();
      foreach (var g in rc.Originals)
      {
        try
        {
          if (doc.FindObject(g, false) is IGH_ActiveObject a &&
              (a.RuntimeMessages(GH_RuntimeMessageLevel.Error)?.Count ?? 0) > 0) set.Add(g);
        }
        catch { }
      }
      return set;
    }

    private static bool ParamNonEmpty(GH_Document doc, Guid paramGuid)
    {
      try { return (doc.FindObject(paramGuid, false) as IGH_Param)?.VolatileData?.DataCount > 0; }
      catch { return false; }
    }

    // ==== the async solve step (ScheduleSolution + one-shot SolutionEnd + watchdog, #117c) =======================

    private static void Step(GH_Document doc, Action<GH_Document> mutate, Action onSolved, Action onWatchdog)
    {
      doc.ScheduleSolution(5, d =>
      {
        try { mutate(d); }
        catch (Exception) { onWatchdog(); return; }   // a mutation failure is handled like a failed step (safe path)

        GH_Document.SolutionEndEventHandler handler = null;
        Eto.Forms.UITimer timeout = null;
        var fired = false;
        void Unhook() { try { if (handler != null) d.SolutionEnd -= handler; } catch { } try { timeout?.Stop(); } catch { } }
        handler = (s, e) => { if (fired) return; fired = true; Unhook(); onSolved(); };
        try { d.SolutionEnd += handler; } catch { }
        timeout = new Eto.Forms.UITimer { Interval = 20.0 };   // per-solve watchdog: a hung solve fails the ROW, never the bridge
        timeout.Elapsed += (s, e) => { if (fired) return; fired = true; Unhook(); onWatchdog(); };
        timeout.Start();
      });
    }

    private static IGH_Param OutputPortParam(IGH_DocumentObject obj, int port)
    {
      if (obj is IGH_Param p) return p;
      if (obj is IGH_Component c && port >= 0 && port < c.Params.Output.Count) return c.Params.Output[port];
      return null;
    }
  }
}
