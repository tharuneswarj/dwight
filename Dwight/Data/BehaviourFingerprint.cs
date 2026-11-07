using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Parameters;
using Grasshopper.Kernel.Special;

namespace Dwight.Data
{
  /// <summary>
  /// The behaviour-preservation tripwire for Reorganise (Phase 6 WP-D). Reorganise must NEVER change what the
  /// definition computes - the prime guardrail (ADR-086). This is the <b>Grasshopper reader</b>: it walks the
  /// live document into a GH-free <see cref="FpDoc"/> - the wire edge-list, the computational component set,
  /// each input's data-tree toggle (Graft/Flatten/Simplify/Reverse) and locked state, and the editable
  /// persistent values (sliders, panels, toggles, value lists, swatches, plus a bounded per-item digest of a
  /// param's internalized data) - and delegates the token assembly, relay splice, and hash to the unit-tested
  /// <see cref="BehaviourFingerprintCore"/>. An identical fingerprint before and after the apply proves no
  /// behaviour-altering edit slipped in; the apply asserts it and aborts on any diff. (Tharun's bake-compare
  /// is the geometry truth; this is the deterministic in-process guard.)
  ///
  /// It is RELAY-TRANSPARENT (the best-practice pass, ADR-085): a provably pass-through node - an UNLOCKED
  /// generic param with a single source, no tree toggle, and no persistent data - copies its input to its
  /// output unchanged, so the core splices it OUT (every consumer resolved back THROUGH it to the real
  /// upstream output). Inserting correct pass-through boundary params leaves the fingerprint identical; any
  /// element that is NOT pass-through (a graft, a coercion, a value, a locked relay) stays in the edge-list
  /// and trips the guard.
  ///
  /// WP-1.8 hardened three blind spots: the wire token carries the source INDEX (a reordered multi-source
  /// input computes different data), <c>Locked</c> is read (a locked relay blocks all data, never transparent),
  /// and the persistent digest samples actual item values (a bounded per-branch / per-item sample, the ADR-076
  /// 24-branch cap precedent), so an in-place value edit on an internalized param trips the guard. The digest
  /// is compared only against itself within one apply, so it needs no culture- or cross-session stability -
  /// only bounded cost (it runs twice per apply).
  /// </summary>
  internal static class BehaviourFingerprint
  {
    public static string Of(GH_Document doc) => BehaviourFingerprintCore.Compute(ReadDoc(doc));

    /// <summary>Read the live document into the GH-free model the core hashes. Crash-safe (ADR-029): an object
    /// we cannot read contributes nothing rather than throwing. Public for the Simplify verification stack (WP-2
    /// Stage 5): it is the before-FpDoc the delta predictor mutates, and the same read the after-hash comes from.</summary>
    public static FpDoc ReadDoc(GH_Document doc)
    {
      var model = new FpDoc();
      if (doc == null) return model;

      foreach (var obj in doc.Objects)
      {
        try
        {
          var fo = ReadObject(obj);
          if (fo != null) model.Objects.Add(fo);
        }
        catch { /* fail safe - skip an unreadable object */ }
      }
      return model;
    }

    /// <summary>Read ONE object into its <see cref="FpObject"/> (null for a group/scribble). The G.1 harvest
    /// (WP-2 Stage 5) reads a freshly-instantiated add with this BEFORE wiring it, so the add's intrinsic tokens
    /// - its type and its persistent input DEFAULTS (a Division's D=10, which a static predictor cannot know) -
    /// are captured, while the wiring is supplied by the plan. Same reader as <see cref="ReadDoc"/>, so a
    /// harvested object hashes identically whether read here or in the full document.</summary>
    public static FpObject ReadObject(IGH_DocumentObject obj)
    {
      if (obj == null || obj is GH_Group || obj is GH_Scribble) return null;   // non-computational

      var fo = new FpObject
      {
        Guid = obj.InstanceGuid,
        TypeName = obj.GetType().FullName,
        ValueToken = ValueToken(obj),
        Locked = SafeLocked(obj),
      };

      // #779: a script component whose source we could not read. The token above already carries the
      // stable SourceToken.Unreadable for the fingerprint's question; this flag carries the as-left
      // record's opposite one. Recomputed here rather than returned out of ValueToken because the token
      // is a string and "could not read" is not a value - see FpObject.ScriptSourceUnreadable.
      fo.ScriptSourceUnreadable = string.Equals(fo.ValueToken, SourceToken.Unreadable, StringComparison.Ordinal);

      if (obj is Param_GenericObject gp)   // the only pass-through shape (a plain generic relay)
      {
        fo.IsGenericParam = true;
        fo.SourceCount = SafeSourceCount(gp);
        fo.DataMappingNone = SafeDataMappingNone(gp);
        fo.Simplify = SafeBool(() => gp.Simplify);
        fo.Reverse = SafeBool(() => gp.Reverse);
        fo.HasPersistentData = HasPersistentData(gp);
      }

      foreach (var ip in InputParams(obj))
        fo.Inputs.Add(new FpInput
        {
          Guid = ip.InstanceGuid,
          MappingToken = Mapping(ip),
          PersistentToken = PersistentToken(ip),
          Sources = SourceGuids(ip),
        });

      return fo;
    }

    /// <summary>The ordered wire sources of an input param, by their param guid (order preserved - GH merges
    /// a param's sources in wire order, so the order is behaviour, WP-1.8).</summary>
    private static List<Guid> SourceGuids(IGH_Param ip)
    {
      var list = new List<Guid>();
      try { if (ip?.Sources != null) foreach (var s in ip.Sources) if (s != null) list.Add(s.InstanceGuid); }
      catch { }
      return list;
    }

    private static bool SafeLocked(IGH_DocumentObject obj)
    { try { return obj is IGH_ActiveObject a && a.Locked; } catch { return false; } }

    private static int SafeSourceCount(IGH_Param p)
    { try { return p.SourceCount; } catch { return 0; } }

    private static bool SafeDataMappingNone(IGH_Param p)
    { try { return p.DataMapping == GH_DataMapping.None; } catch { return true; } }

    private static bool SafeBool(Func<bool> f)
    { try { return f(); } catch { return false; } }

    private static bool HasPersistentData(IGH_Param p)
    {
      try { return p.GetType().GetProperty("PersistentData")?.GetValue(p) is IGH_Structure s && s.DataCount > 0; }
      catch { return false; }
    }

    private static string Mapping(IGH_Param ip)
    {
      try { return ip.DataMapping + "/" + ip.Simplify + "/" + ip.Reverse; } catch { return "?"; }
    }

    /// <summary>The editable value of the common input objects (the things a user actually sets), captured so a
    /// changed value trips the guard. Best-effort - an unrecognised object contributes nothing here.
    ///
    /// #779 ADDS THE ONE OBJECT WHOSE EDITABLE VALUE IS NOT A VALUE: a script component's SOURCE, digested.
    /// It is the same kind of thing as a panel's text - a thing the user types that decides what the
    /// definition computes - and it was missing, which made a rebuild read a script the user had written
    /// into as untouched. Its branch sits BEFORE the switch and owns its own failure, so an unreadable
    /// script can never fall through to the outer null and read as an ordinary component.</summary>
    private static string ValueToken(IGH_DocumentObject obj)
    {
      if (ScriptSourceReader.IsScriptComponent(obj)) return ScriptToken(obj);

      try
      {
        switch (obj)
        {
          case GH_NumberSlider s: return "slider:" + s.CurrentValue.ToString(CultureInfo.InvariantCulture);
          case GH_Panel p: return "panel:" + p.UserText;
          case GH_BooleanToggle t: return "toggle:" + t.Value;
          case GH_ColourSwatch c: return "swatch:" + c.SwatchColour.ToArgb();
          case GH_ValueList v: return "vlist:" + string.Join(",", v.ListItems.Where(i => i.Selected).Select(i => i.Expression));
          default: return null;
        }
      }
      catch { return null; }
    }

    /// <summary>
    /// One script component's source, digested (#779). EVERY failure lands on
    /// <see cref="SourceToken.Unreadable"/> rather than on null, because null here would be
    /// indistinguishable from an ordinary component that holds no value at all - and that
    /// indistinguishability is the whole defect: it is what let a script the user had written into read
    /// as unchanged.
    ///
    /// AN EMPTY SCRIPT IS A REAL, READABLE STATE and must digest like any other, which is why this reads
    /// through <c>TryReadSource</c> rather than <c>TryReadRaw</c>: the latter collapses "empty" and
    /// "could not read" onto null, correctly for a caller that wants CODE and disastrously for a guard.
    /// Dwight's own empty stub would otherwise read as unreadable for ever, so the as-left record would
    /// treat it as adopted and no rebuild could ever replace it - a live feature broken to fix a blind
    /// spot. The empty script and the two-hundred-line one the user typed into it now carry different
    /// tokens, which is the entire point of this change.
    /// </summary>
    private static string ScriptToken(IGH_DocumentObject obj)
    {
      try
      {
        var read = ScriptSourceReader.TryReadSource(obj, out var src);
        return SourceToken.ForScript(read, src);
      }
      catch { return SourceToken.Unreadable; }
    }

    // RR18: these caps are KEPT, deliberately, and they are a KNOWN LIMIT (recorded in the as-built).
    // This digest is a STRUCTURAL TRIPWIRE, not an equivalence proof: it runs CANVAS-WIDE, twice per apply, over
    // every param's internalized data, and its job is to notice that a moves-only Reorganise touched a value it
    // had no business touching. A sample is sufficient for that and full coverage would be quadratic-feeling on a
    // large definition. Layer 2's OUTPUT-equivalence digest is the opposite case - a small surface, and the last
    // thing standing between a wrong result and a deleted original - so it is UNCAPPED (see ValueDigest).
    private const int BranchCap = 24;   // per-branch enumeration cap (the ADR-076 precedent)
    private const int ItemCap = 8;      // per-branch item sample cap - bounds cost on a huge internalized tree

    /// <summary>A BOUNDED digest of a param's internalized data (WP-1.8): path count, then per-branch item
    /// counts and a capped sample of item values, so an in-place value edit changes the digest while a 100k-item
    /// baked param never blows up the cost (it runs twice per apply). Within-session only, so item hashing needs
    /// no culture- or cross-run stability. Null when the param carries no persistent data.</summary>
    private static string PersistentToken(IGH_Param ip)
    {
      try
      {
        if (!(ip.GetType().GetProperty("PersistentData")?.GetValue(ip) is IGH_Structure data) || data.DataCount == 0)
          return null;

        var sb = new StringBuilder();
        sb.Append(data.PathCount).Append('|');
        var bi = 0;
        foreach (var path in data.Paths)
        {
          if (bi >= BranchCap) { sb.Append('+'); break; }
          IList branch = null;
          try { branch = data.get_Branch(path); } catch { }
          var count = branch?.Count ?? 0;
          sb.Append(count).Append(':');
          var sample = Math.Min(count, ItemCap);
          for (var k = 0; k < sample; k++)
          {
            object goo = null;
            try { goo = branch[k]; } catch { }
            sb.Append(GooDigest.Item(goo).GetHashCode()).Append(',');   // RR6: geometry-aware (inherits the shared digest)
          }
          sb.Append(';');
          bi++;
        }
        return sb.ToString();
      }
      catch { return null; }
    }

    private static IEnumerable<IGH_Param> InputParams(IGH_DocumentObject obj)
    {
      if (obj is IGH_Component c) return c.Params.Input;
      if (obj is IGH_Param p) return new[] { p };
      return Array.Empty<IGH_Param>();
    }
  }
}
