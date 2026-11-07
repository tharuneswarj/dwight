using System;
using System.Collections.Generic;
using Dwight.Data;
using Dwight.Simplify;
using Xunit;

namespace Dwight.Simplify.Tests
{
  /// <summary>
  /// Layer 1 of the Simplify verification stack (ADR-090): the delta predictor. Predict the after-fingerprint
  /// from the declared plan, then the apply compares it against the ACTUAL after-fingerprint. These tests pin
  /// the two properties that make the guard meaningful: a FAITHFUL apply matches its prediction, and any change
  /// the plan did NOT declare (the sabotage cases) makes actual != predicted so the apply auto-reverts.
  /// </summary>
  public class DeltaPredictorTests
  {
    // ---- builders (mirroring FingerprintCoreTests) ----

    private static readonly Guid A = new Guid("11111111-0000-0000-0000-000000000001");
    private static readonly Guid Aout = new Guid("11111111-0000-0000-0000-0000000000a1");
    private static readonly Guid B = new Guid("22222222-0000-0000-0000-000000000002");
    private static readonly Guid Bin = new Guid("22222222-0000-0000-0000-0000000000b1");
    private static readonly Guid C = new Guid("33333333-0000-0000-0000-000000000003");
    private static readonly Guid Cout = new Guid("33333333-0000-0000-0000-0000000000c1");
    private static readonly Guid D = new Guid("44444444-0000-0000-0000-000000000004");
    private static readonly Guid Din = new Guid("44444444-0000-0000-0000-0000000000d1");
    private static readonly Guid Relay = new Guid("55555555-0000-0000-0000-000000000005");

    private static FpObject Comp(Guid id, string type, params FpInput[] inputs)
      => new FpObject { Guid = id, TypeName = type, Inputs = new List<FpInput>(inputs) };
    private static FpInput In(Guid id, params Guid[] sources)
      => new FpInput { Guid = id, MappingToken = "None/False/False", Sources = new List<Guid>(sources) };
    private static FpObject Relayed(Guid id, params Guid[] sources)   // an unwired generic param harvest (Sources set later)
      => new FpObject { Guid = id, TypeName = "Param_GenericObject", IsGenericParam = true, Inputs = new List<FpInput> { In(id, sources) } };
    private static string H(FpDoc d) => BehaviourFingerprintCore.Compute(d);

    // ---- faithful applies match the prediction ----

    [Fact]
    public void An_empty_plan_predicts_the_before_state_unchanged()
    {
      var before = new FpDoc { Objects = { Comp(A, "Number"), Comp(B, "Curve", In(Bin, Aout)) } };
      Assert.Equal(H(before), DeltaPredictor.Predict(before, new SimplifyPlan()));
    }

    [Fact]
    public void Removing_a_relay_and_retargeting_the_consumer_predicts_the_direct_wire()
    {
      // before: A.out -> relay -> B.in.  plan: delete the relay, rewire B directly to A.out.
      var before = new FpDoc { Objects = { Comp(A, "Number"), Relayed(Relay, Aout), Comp(B, "Curve", In(Bin, Relay)) } };
      var plan = new SimplifyPlan();
      plan.Removed.Add(Relay);
      plan.Wires.Add(new PlanWire(Bin, Aout));

      var directAfter = new FpDoc { Objects = { Comp(A, "Number"), Comp(B, "Curve", In(Bin, Aout)) } };
      Assert.Equal(H(directAfter), DeltaPredictor.Predict(before, plan));
    }

    [Fact]
    public void Inserting_a_pass_through_relay_is_predicted_behaviour_neutral()
    {
      // before: A.out -> B.in (direct). plan: insert a generic relay between them (harvested unwired, then wired).
      // The added relay must splice out in the prediction, so the predicted hash equals the before hash - which
      // is exactly the tidy-I/O invariant (WP-3): a boundary pass-through param changes nothing.
      var before = new FpDoc { Objects = { Comp(A, "Number"), Comp(B, "Curve", In(Bin, Aout)) } };
      var plan = new SimplifyPlan();
      plan.Added.Add(Relayed(Relay));                 // harvested: generic, Sources still empty
      plan.Wires.Add(new PlanWire(Relay, Aout));      // relay.in <- A.out
      plan.Wires.Add(new PlanWire(Bin, Relay));       // B.in    <- relay

      Assert.Equal(H(before), DeltaPredictor.Predict(before, plan));
    }

    [Fact]
    public void An_added_node_carries_its_harvested_default_value()
    {
      // The G.1 hole: a freshly instantiated component ships a persistent input DEFAULT (Division's D=10) that a
      // static predictor cannot know. The harvest reads that digest from the live object BEFORE wiring, so the
      // prediction includes it and matches an apply that instantiated the same default.
      var before = new FpDoc { Objects = { Comp(A, "Number") } };
      FpObject Division(string digest) => new FpObject
      {
        Guid = D, TypeName = "Division",
        Inputs = new List<FpInput> { new FpInput { Guid = Din, MappingToken = "None/False/False", PersistentToken = digest } },
      };
      var plan = new SimplifyPlan();
      plan.Added.Add(Division("1:10"));   // harvested default (the D=10), unwired

      var after = new FpDoc { Objects = { Comp(A, "Number"), Division("1:10") } };
      Assert.Equal(H(after), DeltaPredictor.Predict(before, plan));
    }

    // ---- SABOTAGE: layer 1 catches an undeclared change ----

    [Fact]
    public void A_rewire_declaring_the_complete_ordered_sources_including_a_kept_one_predicts_that_order()
    {
      // MUST-FIX 2: a PlanWire declares the touched input's COMPLETE final ordered source list (not just the
      // change), and the apply rebuilds the input to exactly it - so an input that RETAINS a kept source plus
      // gains a new one predicts the right order, and layer 1 does not false-STOP a correct multi-source apply.
      var before = new FpDoc { Objects = { Comp(A, "Number"), Comp(C, "Number"), Comp(B, "Merge", In(Bin, Aout)) } };
      var plan = new SimplifyPlan();
      plan.Wires.Add(new PlanWire(Bin, Aout, Cout));   // complete: the kept A.out retained, C.out added, in order

      var after = new FpDoc { Objects = { Comp(A, "Number"), Comp(C, "Number"), Comp(B, "Merge", In(Bin, Aout, Cout)) } };
      Assert.Equal(H(after), DeltaPredictor.Predict(before, plan));

      // and the ORDER is load-bearing (the fingerprint hashes source index): the reversed list predicts differently.
      var reversed = new SimplifyPlan();
      reversed.Wires.Add(new PlanWire(Bin, Cout, Aout));
      Assert.NotEqual(DeltaPredictor.Predict(before, plan), DeltaPredictor.Predict(before, reversed));
    }

    [Fact]
    public void A_stray_rewire_the_plan_did_not_declare_is_caught()
    {
      // before: two independent wires A.out->B.in and C.out->D.in. The plan declares NOTHING (empty). A buggy
      // apply strays and rewires D.in to A.out. The prediction (before, unchanged) must NOT equal the actual
      // after (with the stray wire) - so the guard reverts. This is the demonstration that "verified" is earned.
      var before = new FpDoc { Objects = { Comp(A, "Number"), Comp(B, "Curve", In(Bin, Aout)), Comp(C, "Number"), Comp(D, "Curve", In(Din, Cout)) } };
      var predicted = DeltaPredictor.Predict(before, new SimplifyPlan());

      var strayActual = new FpDoc { Objects = { Comp(A, "Number"), Comp(B, "Curve", In(Bin, Aout)), Comp(C, "Number"), Comp(D, "Curve", In(Din, Aout)) } };
      Assert.NotEqual(BehaviourFingerprintCore.Compute(strayActual), predicted);
    }

    [Fact]
    public void A_touched_value_outside_the_plan_is_caught()
    {
      // The plan does not touch slider A's value; a buggy apply nudges it. Predicted (value unchanged) must not
      // equal actual (value changed) - the off-target side-effect catch.
      FpObject Slider(string v) => new FpObject { Guid = A, TypeName = "Number Slider", ValueToken = v };
      var before = new FpDoc { Objects = { Slider("10"), Comp(B, "Curve", In(Bin, Aout)) } };
      var predicted = DeltaPredictor.Predict(before, new SimplifyPlan());

      var touchedActual = new FpDoc { Objects = { Slider("20"), Comp(B, "Curve", In(Bin, Aout)) } };
      Assert.NotEqual(BehaviourFingerprintCore.Compute(touchedActual), predicted);
    }

    [Fact]
    public void A_reordered_multi_source_wire_the_plan_did_not_declare_is_caught()
    {
      // The plan declares a merge fed [A, C] in that order; a buggy apply wires it [C, A]. Source order is
      // behaviour, so the prediction (declared order) must not equal the actual (swapped order).
      var before = new FpDoc { Objects = { Comp(A, "Number"), Comp(C, "Number") } };
      var plan = new SimplifyPlan();
      plan.Added.Add(Comp(B, "Merge", In(Bin)));      // harvested merge, unwired
      plan.Wires.Add(new PlanWire(Bin, Aout, Cout));  // declared: [A, C]
      var predicted = DeltaPredictor.Predict(before, plan);

      var swappedActual = new FpDoc { Objects = { Comp(A, "Number"), Comp(C, "Number"), Comp(B, "Merge", In(Bin, Cout, Aout)) } };
      Assert.NotEqual(BehaviourFingerprintCore.Compute(swappedActual), predicted);
    }
  }
}
