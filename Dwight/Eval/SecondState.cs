using System;
using System.Collections.Generic;
using System.Linq;

namespace Dwight.Eval
{
  /// <summary>
  /// Phase-8 WP-0 Stage C (#93c/#99/#101): a deterministic, bounded sequence of in-range values to perturb a numeric
  /// value source TO. HARNESS-ONLY (the product holds at single-input verify, #93a). Deterministic because the eval
  /// forbids RNG (a reproducible measurement), bounded because the perturbation-validity gate (#101c) may reject a
  /// candidate and we try the next one; if the whole sequence is invalid the row is scored no-valid-perturbation.
  /// </summary>
  internal static class PerturbationSequence
  {
    // Midpoint first, then quarters, then the near-extremes: a fixed order so a run is reproducible and a hardcode
    // that overfits state 1 is exposed by a value meaningfully different from its current one.
    private static readonly decimal[] Fracs = { 0.5m, 0.25m, 0.75m, 0.1m, 0.9m };

    public static List<decimal> Candidates(decimal min, decimal max, decimal current, bool integer)
    {
      var result = new List<decimal>();
      if (max <= min) return result;   // degenerate: there is nowhere to perturb to
      var seen = new HashSet<decimal>();
      foreach (var f in Fracs)
      {
        var v = min + (max - min) * f;
        if (integer) v = Math.Round(v, MidpointRounding.AwayFromZero);
        if (v < min || v > max) continue;   // rounding can push a near-extreme out of range - guard
        if (v == current) continue;         // never a no-op perturbation
        if (seen.Add(v)) result.Add(v);
      }
      return result;
    }

    // #121(a): fixed MULTIPLES of the current value, for a RANGE-LESS source (a numeric panel has no min/max). The
    // corpus house style is panel -> named relay -> consumer, so a slider-only scan starves and fires #101's
    // no-perturbable tripwire spuriously. The #101(c) validity gate is the backstop that rejects a candidate the
    // definition can't take - same law, different generator.
    private static readonly decimal[] Multiples = { 0.5m, 0.25m, 0.75m, 1.5m, 2m };

    public static List<decimal> CandidatesFromCurrent(decimal current)
    {
      var result = new List<decimal>();
      var seen = new HashSet<decimal>();
      foreach (var m in Multiples)
      {
        var v = current * m;
        if (v == current) continue;   // no-op (also catches current == 0, where every multiple is 0)
        if (seen.Add(v)) result.Add(v);
      }
      return result;
    }
  }

  /// <summary>Which second-state bucket a to-script row landed in (#101). Reported beside the rate so no row's
  /// meaning is hidden - never a silent pass, never a fail-for-input-type.</summary>
  internal enum SecondStateBucket
  {
    FailedState1,         // not built, or state-1 mismatch - never reached the second state
    /// <summary>A1 (#133): ZERO external input ports - a single-point input state space, so state-1 verification is
    /// COMPLETE BY CONSTRUCTION (over an empty input domain a computed and a "hardcoded" script are the same object).
    /// Verified on state 1; NOT a tripwire member (it is not a measurement gap - there is nothing to perturb).</summary>
    SelfContained,
    NoPerturbable,        // HAS external inputs but NONE trace to a perturbable value source - the genuine gap, the tripwire member (#101a/#133)
    NoValidPerturbation,  // no candidate both solved the ORIGINALS clean AND changed an output - state 1 alone (#101c/F7)
    SecondStateVerified,  // a valid perturbation, and the script still matched - overfit-clean
    Overfit,              // a valid perturbation, and the script now diverges - a hardcode single-input verify missed
    /// <summary>F8 (#129): an ANOMALY, never a Decide outcome. A row that verified in the gate trial but does NOT
    /// reproduce on re-instantiation (the script errors, or its state-1 re-check mismatches) contradicts same-inputs
    /// re-solve determinism (the RR6 foundation) - a harness bug or nondeterminism that must be SEEN, not laundered
    /// into the benign NoValidPerturbation bucket. Not in the overfit denominator; any count &gt; 0 is a finding.</summary>
    State1NotReproduced,
  }

  internal readonly struct SecondStateVerdict
  {
    /// <summary>The HEADLINE verify bit (#101a): built AND state-1 match AND (state-2 match if a valid perturbation
    /// exists). The >=60% label bar (#99) is computed over this.</summary>
    public readonly bool Verified;
    public readonly SecondStateBucket Bucket;
    /// <summary>True iff this row is a PERTURBABLE state-1 matcher with a valid perturbation - the denominator of the
    /// overfit rate (#101b). The numerator is those of these whose <see cref="Bucket"/> is <c>Overfit</c>.</summary>
    public readonly bool CountsForOverfit;
    public SecondStateVerdict(bool verified, SecondStateBucket bucket, bool countsForOverfit)
    { Verified = verified; Bucket = bucket; CountsForOverfit = countsForOverfit; }
  }

  /// <summary>
  /// The #101 second-state scoring decision, pure and headless-tested because the de-experimentalization call (#99)
  /// rests on the number it produces. VERIFIED requires the perturbed re-solve to still match WHEN a valid
  /// perturbation exists; a row with no perturbable source (or no valid perturbation) is honestly scored on state 1
  /// alone and reported in its own bucket, never silently passed.
  /// </summary>
  internal static class SecondStateScore
  {
    public static SecondStateVerdict Decide(bool built, bool state1Match, bool hasExternalInputs, bool hasPerturbable, bool perturbationValid, bool state2Match)
    {
      if (!built || !state1Match)
        return new SecondStateVerdict(false, SecondStateBucket.FailedState1, false);
      // A1 (#133): no external inputs -> the input space is a single point -> state-1 verification is complete.
      // Verified, but NOT a tripwire member (nothing to perturb is not a measurement GAP).
      if (!hasExternalInputs)
        return new SecondStateVerdict(true, SecondStateBucket.SelfContained, false);
      // HAS external inputs but none reach a perturbable value source: the genuine gap, the tripwire's member.
      if (!hasPerturbable)
        return new SecondStateVerdict(true, SecondStateBucket.NoPerturbable, false);
      if (!perturbationValid)
        return new SecondStateVerdict(true, SecondStateBucket.NoValidPerturbation, false);
      // a valid perturbation exists: the second state is the arbiter, and this row is in the overfit denominator.
      return state2Match
        ? new SecondStateVerdict(true, SecondStateBucket.SecondStateVerified, true)
        : new SecondStateVerdict(false, SecondStateBucket.Overfit, true);
    }
  }

  /// <summary>Whether a node in the upstream cone is a perturbable value source to COLLECT, or a pass-through to
  /// RECURSE through. A persistent-data param or any dead-end is a Passthrough with no upstream, so it stops
  /// naturally and is never collected (non-perturbable in v1, #134).</summary>
  internal enum NodeKind { ValueSource, Passthrough }

  /// <summary>
  /// Phase-8 WP-0 Stage C (#134): the BROADENED root walk - the perturbation targets are every value source in the
  /// UPSTREAM CONE of the boundary inputs, not just the first pass-through relay. At a computed component we recurse
  /// into ALL its input sources; a slider / single-line-numeric-panel is collected and not recursed into. Deduped by
  /// id, bounded by depth + a cycle guard + a root cap (if the cap truncates, the caller LOGS it - no silent caps).
  ///
  /// GH-free and generic so the WALK DECISION is headless-tested (ADR-091): the caller supplies kindOf / upstreamOf /
  /// idOf over its own node type; the tests supply them over ints, the GH shim over live document objects.
  /// </summary>
  internal static class RootConeWalk
  {
    public static (List<T> Roots, bool Truncated) Collect<T>(
      IEnumerable<T> seeds, Func<T, NodeKind> kindOf, Func<T, IEnumerable<T>> upstreamOf,
      Func<T, object> idOf, int depthBound, int rootCap)
    {
      var roots = new List<T>();
      var rootIds = new HashSet<object>();
      var visited = new HashSet<object>();
      var truncated = false;
      var q = new Queue<(T Node, int Depth)>();
      foreach (var s in seeds ?? Enumerable.Empty<T>()) q.Enqueue((s, 0));

      while (q.Count > 0)
      {
        var (node, depth) = q.Dequeue();
        if (depth > depthBound) continue;
        var id = idOf(node);
        if (!visited.Add(id)) continue;   // cycle guard
        if (kindOf(node) == NodeKind.ValueSource)
        {
          if (roots.Count >= rootCap) { truncated = true; continue; }   // no silent caps
          if (rootIds.Add(id)) roots.Add(node);
          continue;   // a value source has no upstream to walk
        }
        foreach (var up in upstreamOf(node) ?? Enumerable.Empty<T>()) q.Enqueue((up, depth + 1));
      }
      return (roots, truncated);
    }
  }
}
