using System;
using System.Collections.Generic;
using System.Linq;
using Dwight.Data;

namespace Dwight.Simplify
{
  /// <summary>
  /// Layer 1 of the Simplify verification stack (ADR-090): predict the after-fingerprint by applying the plan's
  /// declared delta to the BEFORE tokens, purely (no live document). The apply then computes the ACTUAL
  /// after-fingerprint and compares: actual == predicted, or auto-revert. This catches everything the plan did
  /// not declare - a stray rewire, a touched value, a side effect on a component outside the changed sub-graph.
  ///
  /// It stays a pure function because the plugin controls the inserted guids (pre-stamped) and HARVESTS each
  /// added node's intrinsic tokens from the live object before wiring (ruling G.1) - so the added nodes'
  /// own defaults are known, while the WIRING is taken from the plan, never from the (possibly buggy) live
  /// document. Strategy A: mutate a clone of the before-doc per the plan and re-<see cref="BehaviourFingerprintCore.Compute"/>,
  /// inheriting the relay-splice and token logic for free.
  /// </summary>
  internal static class DeltaPredictor
  {
    public static string Predict(FpDoc before, SimplifyPlan plan) =>
      BehaviourFingerprintCore.Compute(PredictDoc(before, plan));

    /// <summary>
    /// The predicted after-DOCUMENT, before it is hashed - the same computation <see cref="Predict"/> hashes. It is
    /// exposed so a caller whose apply came out different can say WHICH objects differ, by name, instead of only that
    /// two hashes disagree (the consented-edits verdict names what it reverted). <see cref="Predict"/> is
    /// byte-for-byte what it was: it is now this, hashed.
    /// </summary>
    public static FpDoc PredictDoc(FpDoc before, SimplifyPlan plan)
    {
      var removed = new HashSet<Guid>(plan?.Removed ?? Enumerable.Empty<Guid>());
      var objs = new List<FpObject>();
      if (before?.Objects != null)
        foreach (var o in before.Objects) if (o != null && !removed.Contains(o.Guid)) objs.Add(Copy(o));
      if (plan?.Added != null)
        foreach (var a in plan.Added) if (a != null) objs.Add(Copy(a));   // harvested intrinsic state (unwired)

      // apply the DECLARED wiring - to an added node's input AND to any retargeted existing consumer's input.
      // Taking the wiring from the plan (not the live document) is what makes a stray rewire show up as
      // actual != predicted instead of being harvested into the prediction (ruling G.1).
      var inputByGuid = new Dictionary<Guid, FpInput>();
      foreach (var o in objs)
        foreach (var ip in o.Inputs ?? new List<FpInput>())
          if (ip != null) inputByGuid[ip.Guid] = ip;   // later wins on a dup, deterministic per input guid
      if (plan?.Wires != null)
        foreach (var w in plan.Wires)
          if (w != null && inputByGuid.TryGetValue(w.InputGuid, out var ip))
            ip.Sources = new List<Guid>(w.Sources ?? new List<Guid>());

      // a generic param's pass-through detection keys off SourceCount, so recompute it from the wired sources:
      // a harvested relay was unwired at harvest time; the plan's wire is what makes it a 1-source pass-through.
      foreach (var o in objs)
        if (o.IsGenericParam) o.SourceCount = (o.Inputs ?? new List<FpInput>()).Sum(ip => ip?.Sources?.Count ?? 0);

      return new FpDoc { Objects = objs };
    }

    private static FpObject Copy(FpObject o) => new FpObject
    {
      Guid = o.Guid, TypeName = o.TypeName, ValueToken = o.ValueToken, IsGenericParam = o.IsGenericParam,
      SourceCount = o.SourceCount, DataMappingNone = o.DataMappingNone, Simplify = o.Simplify, Reverse = o.Reverse,
      Locked = o.Locked, HasPersistentData = o.HasPersistentData,
      Inputs = (o.Inputs ?? new List<FpInput>()).Select(ip => ip == null ? null : new FpInput
      {
        Guid = ip.Guid, MappingToken = ip.MappingToken, PersistentToken = ip.PersistentToken,
        Sources = new List<Guid>(ip.Sources ?? new List<Guid>()),
      }).ToList(),
    };
  }
}
