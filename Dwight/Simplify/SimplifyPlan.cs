using System;
using System.Collections.Generic;
using Dwight.Data;

namespace Dwight.Simplify
{
  /// <summary>
  /// The declared delta of a Simplify apply - what the accepted change SAYS it will do, expressed in the
  /// fingerprint's own terms so <see cref="DeltaPredictor"/> can compute the predicted after-fingerprint from
  /// it (ADR-090's expected-delta contract, layer 1). Every deterministic refactor (WP-3) and better-logic
  /// (WP-2) produces one of these; the plugin owns it, so it is the ground truth the apply is checked against.
  ///
  /// The G.1 discipline: the <see cref="Added"/> objects carry INTRINSIC state HARVESTED from the freshly
  /// instantiated live objects BEFORE they are wired (so a default value the predictor cannot know statically -
  /// a Division's D=10 - is captured verbatim), while the WIRING is declared separately in <see cref="Wires"/>.
  /// So a stray rewire the apply makes but the plan did not declare shows up as actual != predicted, rather than
  /// being harvested into the prediction and laundered away.
  /// </summary>
  internal sealed class SimplifyPlan
  {
    /// <summary>Objects the apply deletes (the old sub-graph).</summary>
    public List<Guid> Removed { get; } = new List<Guid>();

    /// <summary>Objects the apply inserts, as INTRINSIC <see cref="FpObject"/>s harvested pre-wiring (their
    /// <see cref="FpInput.Sources"/> are empty here; the wiring is declared in <see cref="Wires"/>).</summary>
    public List<FpObject> Added { get; } = new List<FpObject>();

    /// <summary>The declared wiring: set each named input param's ordered sources. Applies to an added node's
    /// input AND to an existing consumer's input that the apply retargets (a boundary rewire).</summary>
    public List<PlanWire> Wires { get; } = new List<PlanWire>();
  }

  /// <summary>Set one input param's ordered wire sources (source order is behaviour, so it is exact).</summary>
  internal sealed class PlanWire
  {
    public Guid InputGuid;
    public List<Guid> Sources { get; } = new List<Guid>();

    public PlanWire() { }
    public PlanWire(Guid input, params Guid[] sources) { InputGuid = input; if (sources != null) Sources.AddRange(sources); }
  }
}
