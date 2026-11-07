using System;
using System.Collections.Generic;
using System.Linq;

namespace Dwight.Data
{
  /// <summary>Phase-8 WP-0 (#95): the class of an output mismatch (the RR20 #57 taxonomy). Used ONLY to render
  /// diagnostic feedback (#88/#89) and to bucket the eval taxonomy - NEVER to decide pass/fail (that stays the
  /// bit-exact <see cref="OutputEquivalence"/> gate, ADR-095). The classifier buckets an already-failed compare.</summary>
  internal enum MismatchKind { Structure, Value, Order, Wrapper }

  /// <summary>The classification plus the VALUE-FREE facts the feedback may carry (#89): item and branch COUNTS,
  /// never the items, never a value, never a position. Litmus (#89): none of these fields, alone, could let a model
  /// with no comprehension of the selection satisfy the check for this one input.</summary>
  internal readonly struct MismatchFacts
  {
    public readonly MismatchKind Kind;
    public readonly int ItemsBefore, ItemsAfter, PathsBefore, PathsAfter;
    public MismatchFacts(MismatchKind kind, int ib, int ia, int pb, int pa)
    { Kind = kind; ItemsBefore = ib; ItemsAfter = ia; PathsBefore = pb; PathsAfter = pa; }
  }

  /// <summary>
  /// The GH-free classification DECISION, lifted verbatim from the eval's proven <c>ToScriptCompileGate.Classify</c>
  /// so the production feedback and the eval taxonomy share ONE bucketing rule. Its inputs are the per-item digests
  /// the GH shim already computes: the STRICT digests (what the product's pass/fail gate compares) and the relaxed
  /// LOOSE digests (shape-sampled - representation-independent but shape-sensitive). The shim (`Flatten`) reads the
  /// live goo; this decision is pure, so it is headless-tested against the four buckets directly.
  ///
  /// The order is deliberate and each step is the more-specific class winning:
  ///   1. different counts (items or branches)                 -> STRUCTURE
  ///   2. same strict multiset, different sequence             -> ORDER  (checked before wrapper - more actionable)
  ///   3. strict differs, but the loose shapes agree in place  -> WRAPPER (our over-strictness, RR18, not the model)
  ///   4. otherwise                                            -> VALUE  (genuinely different numbers, the #50 leak)
  /// </summary>
  internal static class MismatchClassifier
  {
    public static MismatchFacts Classify(
      int pathsBefore, IReadOnlyList<string> itemsBefore, IReadOnlyList<string> looseBefore,
      int pathsAfter, IReadOnlyList<string> itemsAfter, IReadOnlyList<string> looseAfter)
    {
      itemsBefore = itemsBefore ?? Array.Empty<string>();
      itemsAfter = itemsAfter ?? Array.Empty<string>();
      looseBefore = looseBefore ?? Array.Empty<string>();
      looseAfter = looseAfter ?? Array.Empty<string>();

      var kind = Decide(pathsBefore, itemsBefore, looseBefore, pathsAfter, itemsAfter, looseAfter);
      return new MismatchFacts(kind, itemsBefore.Count, itemsAfter.Count, pathsBefore, pathsAfter);
    }

    private static MismatchKind Decide(
      int pathsBefore, IReadOnlyList<string> itemsBefore, IReadOnlyList<string> looseBefore,
      int pathsAfter, IReadOnlyList<string> itemsAfter, IReadOnlyList<string> looseAfter)
    {
      // different shape (item count, branch count) -> a structurally different result.
      if (pathsBefore != pathsAfter || itemsBefore.Count != itemsAfter.Count) return MismatchKind.Structure;

      // the same values in a different sequence: order IS behaviour in Grasshopper, so this is still a failure -
      // but it is a re-ordering, not a wrong computation, and it is the class the output oracle can most cleanly
      // feed back (#88). Decided BEFORE wrapper because it is the more specific and more actionable diagnosis.
      if (Sorted(itemsBefore).SequenceEqual(Sorted(itemsAfter), StringComparer.Ordinal))
        return MismatchKind.Order;

      // identical once strictness is relaxed -> the STRICT digest was the discriminator, not the geometry. Under
      // shape-sampling this means the shapes are genuinely identical (RR18's over-strict class), a finding about US.
      if (looseBefore.SequenceEqual(looseAfter, StringComparer.Ordinal)) return MismatchKind.Wrapper;

      return MismatchKind.Value;   // same shape, genuinely different numbers - the #50 leak
    }

    private static IEnumerable<string> Sorted(IReadOnlyList<string> xs) => xs.OrderBy(x => x, StringComparer.Ordinal);
  }
}
