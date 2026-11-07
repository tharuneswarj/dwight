using System;
using System.Collections.Generic;
using Dwight.Data;
using Dwight.Simplify;
using Xunit;

namespace Dwight.Simplify.Tests
{
  /// <summary>
  /// RR7 (canon item 14, corrected): externalize is a fully PLUGIN-COMPUTED, value-preserving substitution -
  /// the model only NOMINATES the target; the plugin reads the exact value, the int/float type, and the exact
  /// consumer port(s) from the LIVE graph. An externalize legitimately changes topology (an internalized value
  /// becomes a wired slider), so the guarantee is NOT "fingerprint identical to the original" - it is
  /// <c>predicted == actual</c> (the apply realised exactly the plugin-computed plan) plus layer-2 output
  /// equivalence. These headless tests pin the layer-1 half: a faithful externalize matches its prediction, and
  /// both of RR7's real failure modes - the slider wired to the WRONG port, and the slider MISTYPED (a float
  /// where the selector is an integer) - make actual != predicted, so the apply would auto-revert.
  ///
  /// The other half of item 14 - that <c>DeriveExternalize</c> reads the RIGHT port and the RIGHT type from the
  /// live target in the first place (so a faithful apply is the ONLY apply) - is GH-coupled (it reads live
  /// <c>IGH_Param</c>s) and is verified at Tharun's runtime re-gate, with layer-2 equivalence as the backstop.
  /// </summary>
  public class ExternalizeRoundTripTests
  {
    // A Gate/Stream-Filter (the RR7 component) with two inputs: a selector index (internalized) and a curve.
    private static readonly Guid Gate = new Guid("aaaaaaaa-0000-0000-0000-00000000000a");
    private static readonly Guid GateSel = new Guid("aaaaaaaa-0000-0000-0000-0000000000a1");   // the selector input
    private static readonly Guid GateCurve = new Guid("aaaaaaaa-0000-0000-0000-0000000000a2"); // a boundary-curve input
    private static readonly Guid Crv = new Guid("bbbbbbbb-0000-0000-0000-00000000000b");        // upstream curve source
    private static readonly Guid Cout = new Guid("bbbbbbbb-0000-0000-0000-0000000000b1");
    private static readonly Guid Slider = new Guid("cccccccc-0000-0000-0000-00000000000c");     // the externalize add
    private static readonly Guid Sout = new Guid("cccccccc-0000-0000-0000-0000000000c1");        // the slider's output

    private const string SelDigest = "int:0";   // the internalized selector value (an INTEGER index)

    private static FpObject Comp(Guid id, string type, params FpInput[] inputs)
      => new FpObject { Guid = id, TypeName = type, Inputs = new List<FpInput>(inputs) };
    private static FpInput In(Guid id, params Guid[] sources)
      => new FpInput { Guid = id, MappingToken = "None/False/False", Sources = new List<Guid>(sources) };
    private static FpInput Internalized(Guid id, string digest, params Guid[] sources)
      => new FpInput { Guid = id, MappingToken = "None/False/False", PersistentToken = digest, Sources = new List<Guid>(sources) };
    private static FpObject SliderObj(string valueToken)
      => new FpObject { Guid = Slider, TypeName = "Number Slider", ValueToken = valueToken };
    private static string H(FpDoc d) => BehaviourFingerprintCore.Compute(d);

    // The BEFORE state, shared: the Gate's selector carries an internalized integer 0, and a curve is wired in.
    private static FpDoc Before() => new FpDoc
    {
      Objects =
      {
        Comp(Crv, "Curve"),
        Comp(Gate, "Stream Filter", Internalized(GateSel, SelDigest), In(GateCurve, Cout)),
      }
    };

    // The externalize PLAN: add an INTEGER slider carrying the read-back value, wire it to EXACTLY the selector
    // input (the plugin-computed consumer port). No value/type/wiring came from the model.
    private static SimplifyPlan Plan()
    {
      var plan = new SimplifyPlan();
      plan.Added.Add(SliderObj(SelDigest));          // harvested: the plugin's INTEGER slider at value 0
      plan.Wires.Add(new PlanWire(GateSel, Sout));   // the exact consumer port the value fed
      return plan;
    }

    [Fact]
    public void A_faithful_externalize_to_the_correct_port_matches_its_prediction()
    {
      // The selector keeps its (now shadowed) internalized digest and gains the slider as its source - the exact
      // realisation the plugin-computed plan declares. predicted == actual, so layer 1 does not false-STOP it.
      var faithful = new FpDoc
      {
        Objects =
        {
          Comp(Crv, "Curve"),
          SliderObj(SelDigest),
          Comp(Gate, "Stream Filter", Internalized(GateSel, SelDigest, Sout), In(GateCurve, Cout)),
        }
      };
      Assert.Equal(H(faithful), DeltaPredictor.Predict(Before(), Plan()));
    }

    [Fact]
    public void The_RR7_wrong_port_apply_is_caught_by_layer_1()
    {
      // RR7's first failure: the apply wired the new slider to the boundary-CURVE input instead of the selector.
      // The plan declared selector <- slider; the stray apply leaves the selector unwired and feeds the curve
      // input a second source. predicted != actual -> auto-revert.
      var wrongPort = new FpDoc
      {
        Objects =
        {
          Comp(Crv, "Curve"),
          SliderObj(SelDigest),
          Comp(Gate, "Stream Filter", Internalized(GateSel, SelDigest), In(GateCurve, Cout, Sout)),
        }
      };
      Assert.NotEqual(H(wrongPort), DeltaPredictor.Predict(Before(), Plan()));
    }

    [Fact]
    public void The_RR7_mistyped_slider_apply_is_caught_by_layer_1()
    {
      // RR7's second failure: a FLOAT 0.250 where the selector is an INTEGER. The slider's value token differs
      // from the plugin-computed integer the plan declared, so even wired to the correct port the fingerprints
      // diverge - the coincidental "0.250 floors to 0" that fooled layer-2's current-inputs check cannot fool
      // the structural digest.
      var mistyped = new FpDoc
      {
        Objects =
        {
          Comp(Crv, "Curve"),
          SliderObj("float:0.25"),   // wrong type/value vs the plan's integer 0
          Comp(Gate, "Stream Filter", Internalized(GateSel, SelDigest, Sout), In(GateCurve, Cout)),
        }
      };
      Assert.NotEqual(H(mistyped), DeltaPredictor.Predict(Before(), Plan()));
    }
  }
}
