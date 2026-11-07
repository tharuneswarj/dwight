using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Dwight.Agent;
using Dwight.Data;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// Phase-9 U4: set_value's GH-free core (#377, #381 R4; the Q1/Q2/Q4 rulings). A consented edit to an
  /// EXISTING slider or panel - the first write to something the user made rather than something Dwight
  /// placed, which is a different consent class (#338) with its own card and its own test.
  ///
  /// WHAT THE USER APPROVED IS WHAT EXECUTES: nothing may sit between an approval and its execution that
  /// could differ from it (Q1). The verdict is the predicted-delta fingerprint in Simplify's G.1 pattern
  /// - the before-document with ONLY the target's object replaced by its harvested post-apply tokens -
  /// plus the read-back equality of the declared state. The fingerprint's slider token carries the value
  /// only, so a domain-only change elsewhere is invisible to that leg; the verdict SAYS so (Q4), and the
  /// applier's one-guid-no-loop shape is what makes the gap acceptable.
  /// </summary>
  public class SetValueCoreTests
  {
    private static Guid G(int n) { var b = new byte[16]; b[0] = (byte)n; return new Guid(b); }

    private static FpObject Slider(Guid g, string value) => new FpObject
    {
      Guid = g, TypeName = "Grasshopper.Kernel.Special.GH_NumberSlider", ValueToken = "slider:" + value,
      Inputs = { new FpInput { Guid = g, MappingToken = "None/False/False" } },
    };
    private static FpObject Panel(Guid g, string text) => new FpObject
    {
      Guid = g, TypeName = "Grasshopper.Kernel.Special.GH_Panel", ValueToken = "panel:" + text,
      Inputs = { new FpInput { Guid = g, MappingToken = "None/False/False" } },
    };

    private static FpDoc Before() => new FpDoc { Objects = { Slider(G(1), "0.25"), Panel(G(2), "hello"), Slider(G(3), "7") } };

    [Fact]
    public void THE_PREDICTION_REPLACES_ONLY_THE_TARGET_AND_EQUALS_THE_ACTUAL_WHEN_NOTHING_ELSE_MOVED()
    {
      var before = Before();
      var harvested = Slider(G(1), "40");   // what the live object reads after the apply
      var predicted = SetValueCore.Predict(before, G(1), harvested);

      var actual = new FpDoc { Objects = { Slider(G(1), "40"), Panel(G(2), "hello"), Slider(G(3), "7") } };
      Assert.Equal(BehaviourFingerprintCore.Compute(actual), predicted);
      Assert.NotEqual(BehaviourFingerprintCore.Compute(before), predicted);
    }

    [Fact]
    public void A_SECOND_CHANGE_ANYWHERE_ELSE_BREAKS_THE_PREDICTION()
    {
      // The whole point of the leg: an unintended change on ANY other object is caught.
      var predicted = SetValueCore.Predict(Before(), G(1), Slider(G(1), "40"));
      var strayValue = new FpDoc { Objects = { Slider(G(1), "40"), Panel(G(2), "hello"), Slider(G(3), "8") } };
      var strayWire = new FpDoc { Objects = { Slider(G(1), "40"), Panel(G(2), "hello"), Slider(G(3), "7") } };
      strayWire.Objects[1].Inputs[0].Sources.Add(G(1));

      Assert.NotEqual(predicted, BehaviourFingerprintCore.Compute(strayValue));
      Assert.NotEqual(predicted, BehaviourFingerprintCore.Compute(strayWire));
    }

    /// <summary>
    /// #642's SOURCE IDENTITY DOES NOT APPLY HERE, BY CONSTRUCTION - and this says so with the one shape #642 was about.
    /// Preservation's bug compared wire sources (PARAM guids) against sets of COMPONENT guids; a component's output param
    /// has its own guid. set_value's prediction holds no guid sets at all: it swaps the target's WHOLE object by guid and
    /// compares whole fingerprints, and its target is a Number Slider or a Panel - floating params whose own guid IS their
    /// source guid. So a slider feeding a component, and a Panel fed BY a component's output param (guid O, not the
    /// component's), predict exactly the actual document after a value change, sources included.
    /// </summary>
    [Fact]
    public void A_COMPONENT_OUTPUT_ON_EITHER_SIDE_OF_THE_TARGET_PREDICTS_EXACTLY_SOURCES_INCLUDED()
    {
      Guid slider = G(10), component = G(11), componentInput = G(12), componentOutput = G(13), panel = G(14), consumerInput = G(15);

      FpObject Component() => new FpObject
      {
        Guid = component, TypeName = "Construct Point",
        Inputs = { new FpInput { Guid = componentInput, MappingToken = "None/False/False", Sources = { slider } } },
      };
      FpObject Consumer() => new FpObject
      {
        Guid = G(16), TypeName = "XY Plane",
        Inputs = { new FpInput { Guid = consumerInput, MappingToken = "None/False/False", Sources = { componentOutput } } },
      };
      FpObject FedPanel(string text)
      {
        var p = Panel(panel, text);
        p.Inputs[0].Sources.Add(componentOutput);   // a Panel wired FROM the component's output param
        return p;
      }

      var before = new FpDoc { Objects = { Slider(slider, "4"), Component(), Consumer(), FedPanel("old") } };

      // The slider (upstream of the component) changes value.
      var sliderAfter = new FpDoc { Objects = { Slider(slider, "8"), Component(), Consumer(), FedPanel("old") } };
      Assert.Equal(BehaviourFingerprintCore.Compute(sliderAfter), SetValueCore.Predict(before, slider, Slider(slider, "8")));

      // The panel (downstream of the component's OUTPUT param) changes text; its incoming wire is part of what is harvested.
      var panelAfter = new FpDoc { Objects = { Slider(slider, "4"), Component(), Consumer(), FedPanel("new") } };
      Assert.Equal(BehaviourFingerprintCore.Compute(panelAfter), SetValueCore.Predict(before, panel, FedPanel("new")));
    }

    [Fact]
    public void A_TARGET_ABSENT_FROM_THE_BEFORE_DOCUMENT_IS_NOT_INVENTED_INTO_THE_PREDICTION()
    {
      // Predicting for a target that was not there would certify a placement as an edit.
      var before = Before();
      var predicted = SetValueCore.Predict(before, G(9), Slider(G(9), "1"));
      Assert.Equal(BehaviourFingerprintCore.Compute(before), predicted);
    }

    [Fact]
    public void THE_SCOPE_RULE_REFUSES_A_TARGET_THAT_IS_GONE_OR_CHANGED_TYPE_AND_NEVER_FALLS_BACK()
    {
      // #338: an approved card whose target no longer exists REFUSES rather than falling back.
      Assert.False(SetValueCore.MayProceed(IdState.Retired, "Number Slider", "Number Slider", out var gone));
      Assert.Contains("no longer on the canvas", gone);
      Assert.False(SetValueCore.MayProceed(IdState.Unknown, "Number Slider", "Number Slider", out _));
      Assert.False(SetValueCore.MayProceed(IdState.Alive, "Panel", "Number Slider", out var swapped));
      Assert.Contains("Panel", swapped);
      Assert.False(SetValueCore.MayProceed(IdState.Alive, null, "Number Slider", out _));
      Assert.True(SetValueCore.MayProceed(IdState.Alive, "number slider", "Number Slider", out var ok));
      Assert.Null(ok);
    }

    [Fact]
    public void THE_VERDICT_NAMES_THE_CHANGE_STATES_THE_DOMAIN_GAP_AND_THE_SCOPE_THAT_MAKES_IT_ACCEPTABLE()
    {
      var change = new SetValueChange { Id = 42, Type = "Number Slider", Nick = "Count", Before = "0.25 (0 to 1, float)", After = "40 (0 to 100, integer)" };
      var v = SetValueCore.Verdict(applied: true, change, preserved: true, complete: true, mismatch: null);

      Assert.Equal("applied", v["status"].GetValue<string>());
      Assert.Equal(42, v["target"]["id"].GetValue<int>());
      Assert.Equal("Count", v["target"]["nick"].GetValue<string>());
      Assert.Equal("0.25 (0 to 1, float)", v["before"].GetValue<string>());
      Assert.Equal("40 (0 to 100, integer)", v["after"].GetValue<string>());
      Assert.True(v["preserved"].GetValue<bool>());
      Assert.True(v["complete"].GetValue<bool>());
      Assert.Contains("one Ctrl-Z", v["undo"].GetValue<string>());
      // Q4, stated in the verdict: the fingerprint's slider token carries the value only.
      var gap = v["doesNotProve"].GetValue<string>();
      Assert.Contains("domain", gap);
      Assert.Contains("one component", gap);
      Assert.False(v.ContainsKey("mismatch"));
    }

    [Fact]
    public void A_REVERTED_VERDICT_SAYS_WHICH_LEG_FAILED_AND_THAT_THE_CANVAS_IS_AS_IT_WAS()
    {
      var change = new SetValueChange { Id = 42, Type = "Panel", Nick = null, Before = "\"a\"", After = "\"b\"" };
      var v = SetValueCore.Verdict(applied: false, change, preserved: false, complete: true, mismatch: null);
      Assert.Equal("reverted", v["status"].GetValue<string>());
      Assert.False(v["preserved"].GetValue<bool>());
      Assert.Contains("as it was", v["note"].GetValue<string>());

      var v2 = SetValueCore.Verdict(applied: false, change, preserved: true, complete: false, mismatch: "the slider holds 39, not the declared 40");
      Assert.Contains("39", v2["mismatch"].GetValue<string>());
    }

    [Fact]
    public void THE_CHANGE_DESCRIPTION_IS_PLUGIN_FORMATTED_FROM_THE_STATE_NEVER_THE_MODELS_TEXT()
    {
      var slider = new BuildState { Kind = BuildStateKind.Slider, Min = 0m, Max = 100m, Value = 40m, Rounding = SliderRounding.Integer, DecimalPlaces = 0 };
      Assert.Equal("40 (0 to 100, integer)", SetValueCore.Describe(slider));
      var f = new BuildState { Kind = BuildStateKind.Slider, Min = 0m, Max = 1m, Value = 0.25m, Rounding = SliderRounding.Float, DecimalPlaces = 2 };
      Assert.Equal("0.25 (0 to 1, float)", SetValueCore.Describe(f));
      var panel = new BuildState { Kind = BuildStateKind.PanelText, Text = "a long\nline of text that goes on and on and on and on past the cut" };
      var d = SetValueCore.Describe(panel);
      Assert.StartsWith("\"a long line", d);
      Assert.True(d.Length <= 70, d);
    }
  }
}
