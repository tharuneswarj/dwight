using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Grasshopper;
using Grasshopper.Kernel;
using Dwight.Data;

namespace Dwight.Simplify
{
  /// <summary>
  /// RR20 #51: COMPILE BEFORE PREVIEW. The user must never again see a preview of code that does not build.
  ///
  /// We already DETECT this and merely reported it: beside-verify solves the script, and an errored script emits a
  /// null on every output (which is why errors gate the compare). So the failure was known - it was just shown too
  /// late, after the user had read a script that was never going to run.
  ///
  /// This gate runs the script BEFORE the preview: instantiate it off-canvas, wire it from the same sources, solve,
  /// read the compiler diagnostics, and remove it again - no undo record, nothing committed, the canvas untouched.
  /// On an error the diagnostic goes back to the model WITH REFLECTED SIGNATURES for the types it got wrong (the
  /// compiler names them: "LoftOptions could not be found", "Brep does not contain a definition for X"), so the
  /// answer to a hallucinated API is ground truth from the loaded DLL, not a scolding. Bounded retries; if it still
  /// fails, we say so honestly with the error and a Retry.
  ///
  /// This is the refine loop's cheapest, highest-value slice, and it is the PLUGIN closing the loop against a
  /// MACHINE ORACLE (the compiler) - never touching intent (#52). It lands in Phase 7; the general loop is Phase 8.
  ///
  /// WHY NOTHING ELSE CAN SEE THE OFF-CANVAS SCRIPT WHILE IT EXISTS - written at U20, when a SECOND trial
  /// site made the same assumption and the reason turned out to live in another file entirely.
  ///
  /// The candidate IS on the live canvas for a while, and this gate spans FOUR scheduled steps - add and
  /// wire, the solve, the SolutionEnd read, then a fresh callback to remove - so it is NOT protected by
  /// sitting inside a single callback. A callback landing between the steps could take a behaviour
  /// fingerprint that INCLUDED the candidate and later compare it against one taken after it was gone;
  /// preservation would then see an object removed that it never placed and revert a good apply.
  ///
  /// What stops that is <c>DwightSession.Start</c>'s busy gate - <c>if (_running) return null</c>, ONE
  /// OPERATION AT A TIME, process-wide. This gate runs inside a verb, and the verb holds that claim, so no
  /// other operation can start: it shows the busy notice and bails.
  ///
  /// WHAT WOULD BREAK IT, named so a later change cannot remove it quietly: any path that schedules a
  /// solution WITHOUT holding the session, or a session that ever permits two operations at once.
  /// <c>ScriptEditTrial</c> ASSERTS this precondition rather than documenting it, because it can be reached
  /// from the chat; this gate is only ever entered from inside a verb, and the day that stops being true it
  /// needs the same assertion.
  /// </summary>
  /// <summary>The outcome of running a candidate script off-canvas: did it BUILD, and did it REPRODUCE. Two
  /// separate facts, and the eval harness needs both (compile-rate and verify-rate are different numbers, and
  /// improving one can leave the other flat - which is exactly what we cannot see without measuring).</summary>
  internal sealed class ScriptTrial
  {
    public List<string> Errors = new List<string>();          // empty = it built and ran
    public List<string> MismatchedOutputs = new List<string>(); // empty (and Errors empty) = it reproduced the originals
    /// <summary>RR20 #57: WHY each output mismatched - "name:kind". The kinds are not decoration, they decide
    /// whether the number is real (see <see cref="Dwight.Data.MismatchKind"/>).</summary>
    public List<string> MismatchKinds = new List<string>();
    /// <summary>RR20 #77: the SAME mismatches classified by the OLD bbox loose-comparison, so the very same scripts
    /// can be scored both ways in one pass - the strong proof of the wrapper question (does a bbox "wrapper" survive
    /// under shape-sampling, or was the bbox coincidental?).</summary>
    public List<string> MismatchKindsBbox = new List<string>();
    /// <summary>Phase-8 WP-0 (#88): the CLASSIFIED per-output diffs (name + class + value-free counts) - the raw
    /// material the output oracle renders into class-only feedback. Strict-digest classification.</summary>
    public List<Dwight.Data.OutputDiff> Diffs = new List<Dwight.Data.OutputDiff>();
    public int OutputCount;                                    // RR20 #58: for the single- vs multi-output split
    /// <summary>Phase-8 WP-0 (#109): did the off-canvas solve actually RUN and get read? False for the
    /// solver-disabled path (which returns an empty trial), so a vacuous trial can NEVER read as Built or Verified.
    /// The moment the preview gates on the trial (#91), a vacuous "verified" would be the exact false claim this
    /// project never ships - so a verdict is earned only by a real check.</summary>
    public bool Checked;
    public bool Built => Checked && Errors.Count == 0;
    public bool Verified => Built && MismatchedOutputs.Count == 0;
  }

  // RR20 #57: a bit-exact comparator is RIGHT FOR THE PRODUCT and TOO BLUNT FOR THE MEASUREMENT. The mismatch
  // taxonomy (STRUCTURE / VALUE / ORDER / WRAPPER) and its decision now live in the GH-free Dwight.Data.
  // MismatchClassifier (Phase-8 WP-0 #95), graduated so the production output oracle and the eval share ONE
  // bucketing rule; this gate is the thin shim that reads the live goo into the digest lists the core compares.

  internal static class ToScriptCompileGate
  {
    /// <summary>
    /// The full off-canvas trial: build it, wire it from the same sources, solve, read the diagnostics AND compare
    /// its outputs 1:1 against the originals' - then remove it. This is the beside-verify without the accept, so it
    /// costs one solve and risks nothing. The product path gates the preview on <see cref="ScriptTrial.Built"/>
    /// (#51); the eval harness records both numbers.
    /// </summary>
    public static void Trial(ToScriptResolve rc, string source, Action<ScriptTrial> done)
    {
      var doc = rc?.Doc;
      if (doc == null) { done?.Invoke(Failed("the document is no longer available")); return; }
      if (SimplifyApply.SolverDisabled(doc)) { done?.Invoke(new ScriptTrial()); return; }   // can't check; let the accept path catch it

      doc.ScheduleSolution(5, d =>
      {
        IGH_Component comp = null;
        try
        {
          comp = ScriptComponentShim.TryCreate(rc.Language, out var createError);
          if (comp == null) { done?.Invoke(Failed(createError)); return; }
          if (!ScriptComponentShim.Shape(comp, rc.Iface, rc.Language, source, out var shapeError))
          { done?.Invoke(Failed(shapeError)); return; }

          try { comp.CreateAttributes(); } catch { }
          comp.Attributes.Pivot = new PointF(rc.SelectionBounds.Left, rc.SelectionBounds.Bottom + 4000f);   // far clear of the definition
          d.AddObject(comp, false);

          // wire from the SAME sources, so the check sees the real data - a script can compile and still fail on
          // the actual types flowing through it, and that is exactly what we want to catch before the preview.
          for (var i = 0; i < rc.Iface.Inputs.Count && i < comp.Params.Input.Count; i++)
          {
            if (!rc.ExtIn.TryGetValue(rc.Iface.Inputs[i].Ext, out var src)) continue;
            var sourceParam = OutputPortParam(d.FindObject(src.Src, false), src.Port);
            if (sourceParam != null) comp.Params.Input[i].AddSource(sourceParam);
          }

          Arm(d, rc, comp, done);
        }
        catch (Exception ex)
        {
          TryRemove(d, comp);
          done?.Invoke(Failed(ex.Message));
        }
      });
    }

    private static ScriptTrial Failed(string error)
    {
      var t = new ScriptTrial { Checked = true };   // a real "couldn't build/run" verdict, not a skipped check (#109)
      t.Errors.Add(error ?? "unknown error");
      return t;
    }

    private static void Arm(GH_Document doc, ToScriptResolve rc, IGH_Component comp, Action<ScriptTrial> done)
    {
      GH_Document.SolutionEndEventHandler handler = null;
      Eto.Forms.UITimer timeout = null;
      var fired = false;

      void Finish(ScriptTrial trial)
      {
        try { if (handler != null) doc.SolutionEnd -= handler; } catch { }
        try { timeout?.Stop(); } catch { }
        // remove in a FRESH callback - never mutate the document inside SolutionEnd.
        doc.ScheduleSolution(1, d2 => { TryRemove(d2, comp); done?.Invoke(trial); });
      }

      handler = (s, e) =>
      {
        if (fired) return;
        fired = true;
        try
        {
          // Checked=true: the off-canvas solve RAN and we are reading its verdict now (#109). Only the
          // solver-disabled early-out returns an unchecked (vacuous) trial.
          var trial = new ScriptTrial { Checked = true, Errors = ScriptComponentShim.Errors(comp), OutputCount = rc.Iface.Outputs.Count };

          // An errored script still emits a null on every output, so the compare is only meaningful once it BUILDS.
          if (trial.Built)
          {
            var scriptParams = ScriptComponentShim.ScriptOutputs(comp);
            var originals = SimplifyApply.CaptureOutputs(doc, rc.BoundaryOutParams);
            var scriptOuts = SimplifyApply.CaptureOutputs(doc, scriptParams.Select(p => p.InstanceGuid).ToList());

            foreach (var i in OutputEquivalence.Mismatches(originals, scriptOuts))
            {
              var name = i >= 0 && i < rc.Iface.Outputs.Count ? (rc.Iface.Outputs[i].Name ?? $"output{i}") : $"output{i}";
              trial.MismatchedOutputs.Add(name);

              // RR20 #57 / WP-0 #95: classify it while we still have the live goos - after this callback they are
              // gone. The DECISION is the GH-free MismatchClassifier; this shim only reads the goo into digest lists.
              var before = i < rc.BoundaryOutParams.Count ? doc.FindObject(rc.BoundaryOutParams[i], false) as IGH_Param : null;
              var after = i < scriptParams.Count ? scriptParams[i] : null;
              var facts = ClassifyFacts(before, after, bbox: false);
              trial.MismatchKinds.Add(name + ":" + facts.Kind.ToString().ToLowerInvariant());
              trial.MismatchKindsBbox.Add(name + ":" + Classify(before, after, bbox: true).ToString().ToLowerInvariant());   // #77
              // WP-0 #88: the classified diff (name + class + value-free counts) the output oracle feeds back.
              trial.Diffs.Add(new Dwight.Data.OutputDiff(name, facts.Kind, facts.ItemsBefore, facts.ItemsAfter, facts.PathsBefore, facts.PathsAfter));
            }
          }
          Finish(trial);
        }
        catch (Exception ex) { Finish(Failed(ex.Message)); }
      };

      try { doc.SolutionEnd += handler; } catch { }
      timeout = new Eto.Forms.UITimer { Interval = 20.0 };
      timeout.Elapsed += (s, e) =>
      {
        if (fired) return;
        fired = true;
        Finish(Failed("the script did not finish solving in time - it is too slow or does not terminate"));
      };
      timeout.Start();
    }

    /// <summary>RR20 #57 / WP-0 #95: WHY did this output differ? The DECISION is the GH-free MismatchClassifier;
    /// this shim reads the two params into strict + loose digest lists and hands them over. Structure and Value are
    /// real failures; Order is the tractable class the oracle feeds back; Wrapper would mean the RR18 digest bug is
    /// back and is a BUG, not a score.</summary>
    private static Dwight.Data.MismatchFacts ClassifyFacts(IGH_Param before, IGH_Param after, bool bbox)
    {
      try
      {
        var b = Flatten(before, bbox);
        var a = Flatten(after, bbox);
        return Dwight.Data.MismatchClassifier.Classify(b.Paths, b.Items, b.Loose, a.Paths, a.Items, a.Loose);
      }
      catch { return new Dwight.Data.MismatchFacts(Dwight.Data.MismatchKind.Value, 0, 0, 0, 0); }
    }

    private static Dwight.Data.MismatchKind Classify(IGH_Param before, IGH_Param after, bool bbox)
      => ClassifyFacts(before, after, bbox).Kind;

    private sealed class Flat
    {
      public int Paths;
      public List<string> Items = new List<string>();   // strict per-item digests (what the product compares)
      public List<string> Loose = new List<string>();   // relaxed: shape-sampled (or bbox, for the #77 comparison)
    }

    private static Flat Flatten(IGH_Param p, bool bbox)
    {
      var f = new Flat();
      var data = p?.VolatileData;
      if (data == null) return f;
      f.Paths = data.PathCount;
      foreach (var path in data.Paths)
      {
        var branch = data.get_Branch(path) as System.Collections.IList;
        if (branch == null) continue;
        for (var k = 0; k < branch.Count; k++)
        {
          object goo = null;
          try { goo = branch[k]; } catch { }
          f.Items.Add(GooDigest.Item(goo));
          f.Loose.Add(bbox ? LooseItemBbox(goo) : LooseItem(goo));
        }
      }
      return f;
    }

    /// <summary>RR20 #77: the OLD bbox loose-comparison, kept ONLY so the same scripts can be scored both ways in
    /// one reclassify pass. Geometry by bounding box - which cannot tell an identical curve carried by a different
    /// class from a genuinely different curve that shares a bbox. That blindness is the whole point of comparing it
    /// against the shape-sampling version. NEVER used by the product; the pass/fail gate is strict, always.</summary>
    private static string LooseItemBbox(object item)
    {
      try
      {
        object v = item;
        if (item is Grasshopper.Kernel.Types.IGH_Goo g) { try { v = g.ScriptVariable() ?? item; } catch { } }
        if (v is double d) return "n:" + R(d);
        if (v is int i) return "n:" + i;
        if (v is Rhino.Geometry.GeometryBase geo)
        {
          var bb = geo.GetBoundingBox(true);
          return "g:" + string.Join(",", new[] { bb.Min.X, bb.Min.Y, bb.Min.Z, bb.Max.X, bb.Max.Y, bb.Max.Z }.Select(R));
        }
        return GooDigest.Item(item);
      }
      catch { return GooDigest.Item(item); }
    }

    /// <summary>
    /// A deliberately RELAXED digest, used ONLY to tell "our comparator was too strict" from "the script is wrong".
    /// If the strict digest differs and this AGREES, the script computed the same SHAPE and the difference is in how
    /// it was represented - which is a finding about US, not about the model.
    ///
    /// The first version of this compared geometry by BOUNDING BOX, which was useless: it could not distinguish
    /// "the same curve as a LineCurve instead of a PolylineCurve" (identical geometry, different class - our
    /// strict digest serializes the class, so it rejects it) from "a completely different curve that happens to
    /// share a bbox". It therefore could neither confirm nor clear the wrapper bucket.
    ///
    /// So geometry is compared by SAMPLED SHAPE - points along the curve, in order - which is representation-
    /// independent but genuinely shape-sensitive. That is the only way this bucket can be trusted.
    /// </summary>
    private static string LooseItem(object item)
    {
      try
      {
        object v = item;
        if (item is Grasshopper.Kernel.Types.IGH_Goo g) { try { v = g.ScriptVariable() ?? item; } catch { } }

        if (v is double d) return "n:" + Math.Round(d, 6).ToString("R", CI);
        if (v is int i) return "n:" + i;
        if (v is Rhino.Geometry.Point3d p) return "p:" + R(p.X) + "," + R(p.Y) + "," + R(p.Z);
        if (v is Rhino.Geometry.Vector3d vec) return "v:" + R(vec.X) + "," + R(vec.Y) + "," + R(vec.Z);

        if (v is Rhino.Geometry.Curve c)
        {
          // 32 points along the curve, in order: the same shape sampled the same way, whatever class carries it.
          var sb = new StringBuilder("c:");
          for (var k = 0; k <= 32; k++)
          {
            var t = c.Domain.ParameterAt(k / 32.0);
            var pt = c.PointAt(t);
            sb.Append(R(pt.X)).Append('_').Append(R(pt.Y)).Append('_').Append(R(pt.Z)).Append(';');
          }
          sb.Append(c.IsClosed ? "closed" : "open");
          return sb.ToString();
        }
        if (v is Rhino.Geometry.Brep b)
        {
          var bb = b.GetBoundingBox(true);
          var area = Rhino.Geometry.AreaMassProperties.Compute(b);
          return $"b:{b.Faces.Count}:{R(bb.Min.X)},{R(bb.Min.Y)},{R(bb.Min.Z)},{R(bb.Max.X)},{R(bb.Max.Y)},{R(bb.Max.Z)}:{(area == null ? "?" : R(area.Area))}";
        }
        if (v is Rhino.Geometry.Mesh m)
        {
          var bb = m.GetBoundingBox(true);
          return $"m:{m.Faces.Count}:{m.Vertices.Count}:{R(bb.Min.X)},{R(bb.Min.Y)},{R(bb.Min.Z)},{R(bb.Max.X)},{R(bb.Max.Y)},{R(bb.Max.Z)}";
        }
        return GooDigest.Item(item);
      }
      catch { return GooDigest.Item(item); }
    }

    private static readonly System.Globalization.CultureInfo CI = System.Globalization.CultureInfo.InvariantCulture;
    private static string R(double d) => Math.Round(d, 6).ToString("R", CI);

    private static void TryRemove(GH_Document d, IGH_Component comp)
    {
      try { if (comp != null && d.FindObject(comp.InstanceGuid, false) != null) d.RemoveObject(comp, false); } catch { }
      try { Instances.RedrawCanvas(); } catch { }
    }

    private static IGH_Param OutputPortParam(IGH_DocumentObject obj, int port)
    {
      if (obj is IGH_Param p) return p;
      if (obj is IGH_Component c && port >= 0 && port < c.Params.Output.Count) return c.Params.Output[port];
      return null;
    }

    // ==== the repair message: the compiler's own words + GROUND TRUTH for what it got wrong ================

    // "The type or namespace name 'LoftOptions' could not be found", "'Brep' does not contain a definition for 'X'",
    // "cannot convert from 'Brep[]' to 'Brep'" - the compiler NAMES the type it choked on, every time.
    private static readonly Regex TypeMention = new Regex(@"'([A-Za-z_][A-Za-z0-9_]*)(?:\[\])?'", RegexOptions.Compiled);

    /// <summary>
    /// Turn a compiler diagnostic into a repair the model can actually act on: its own error, plus the REFLECTED
    /// signatures of every type the error named. A hallucinated type ("LoftOptions") reflects to nothing, and being
    /// told "LoftOptions does not exist in RhinoCommon" is precisely the correction it needs; a real type reflects
    /// to its true members, so the fix is grounded in the DLL the script compiles against - not in a memory of it.
    ///
    /// The COMPOSITION is <see cref="CompileRepair.Text"/>, GH-free, so the header choice is testable; this keeps the
    /// reflection, which needs the loaded RhinoCommon. Same text as before, byte for byte.
    /// </summary>
    public static string RepairPrompt(List<string> errors, ScriptLanguage lang) =>
      CompileRepair.Text(errors, lang == ScriptLanguage.CSharp, ReflectedApiFor(errors));

    /// <summary>The reflected signatures for every type the compiler complained about - including the verdict that a
    /// type does NOT exist, which is the most useful answer of all when the model invented one. Internal so U20's
    /// refusal can hand it to <see cref="CompileRepair.Text"/> without a second copy of the reflection.</summary>
    internal static string ReflectedApiFor(List<string> errors)
    {
      var names = new List<string>();
      foreach (var e in errors.Take(5))
        foreach (Match m in TypeMention.Matches(e ?? ""))
        {
          var n = m.Groups[1].Value;
          if (n.Length > 2 && char.IsUpper(n[0]) && !names.Contains(n)) names.Add(n);
        }
      if (names.Count == 0) return null;

      var sb = new StringBuilder();
      foreach (var n in names.Take(4))
      {
        var sig = RhinoApiReflector.Signatures(n);
        sb.AppendLine(sig ?? $"{n}: NO SUCH TYPE in RhinoCommon. You invented it - use a type that exists.");
        sb.AppendLine();
      }
      return sb.ToString();
    }
  }
}
