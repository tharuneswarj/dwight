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
  /// Phase-9 U4: the ask-time and click-time validation of a set_value request, GH-free - the plugin
  /// supplies the live facts (id state, the target's type and current state) as functions, so the
  /// decisions are tested here and the shim is a thin reader (ADR-091).
  ///
  /// The measured rule (the architect's ruling on #392 (j), option 1): one undo record does NOT restore a
  /// slider whose rounding changed to a grid its old value is not on - Grasshopper replays the old value
  /// under the new accuracy, snaps it, then restores the accuracy. So that request is REFUSED at parse
  /// time, and the refusal names the way through: set the value on the new grid first, then change the
  /// rounding. Every other change measured restores exactly.
  /// </summary>
  public class SetValueAskTests
  {
    private static BuildState Slider(decimal min, decimal max, decimal value, SliderRounding r, int dp) =>
      new BuildState { Kind = BuildStateKind.Slider, Min = min, Max = max, Value = value, Rounding = r, DecimalPlaces = dp };

    // ---- the rounding-restore rule ----------------------------------------------------------------------

    [Fact]
    public void A_ROUNDING_CHANGE_TO_A_GRID_THE_CURRENT_VALUE_IS_NOT_ON_IS_REFUSED_WITH_THE_WAY_THROUGH()
    {
      var declared = Slider(0, 100, 40, SliderRounding.Integer, 0);
      var current = Slider(0, 1, 0.25m, SliderRounding.Float, 3);

      Assert.False(SetValueCore.MayChangeRounding(declared, current, out var refusal));
      Assert.Contains("0.25", refusal);
      Assert.Contains("integer", refusal, StringComparison.OrdinalIgnoreCase);
      Assert.Contains("undo", refusal, StringComparison.OrdinalIgnoreCase);
      Assert.Contains("whole number", refusal);
      Assert.Contains("first", refusal);
    }

    [Fact]
    public void THE_MEASURED_SAFE_CASES_PASS()
    {
      var current = Slider(0, 1, 0.25m, SliderRounding.Float, 3);
      // value only, domain and value staying float, rounding unchanged
      Assert.True(SetValueCore.MayChangeRounding(Slider(0, 1, 0.75m, SliderRounding.Float, 2), current, out _));
      Assert.True(SetValueCore.MayChangeRounding(Slider(0, 100, 40, SliderRounding.Float, 0), current, out _));
      // the same flip from an on-grid current value (variant F)
      Assert.True(SetValueCore.MayChangeRounding(Slider(0, 100, 40, SliderRounding.Integer, 0), Slider(0, 1, 1, SliderRounding.Float, 3), out _));
      // integer to float (variant G): float's grid holds everything
      Assert.True(SetValueCore.MayChangeRounding(Slider(0, 1, 0.25m, SliderRounding.Float, 2), Slider(0, 100, 40, SliderRounding.Integer, 0), out _));
      // even/odd: parity is part of the grid
      Assert.False(SetValueCore.MayChangeRounding(Slider(0, 10, 4, SliderRounding.Even, 0), Slider(0, 10, 3, SliderRounding.Integer, 0), out var odd));
      Assert.Contains("even number", odd);
      Assert.True(SetValueCore.MayChangeRounding(Slider(0, 10, 4, SliderRounding.Even, 0), Slider(0, 10, 2, SliderRounding.Integer, 0), out _));
      // a panel has no rounding
      Assert.True(SetValueCore.MayChangeRounding(new BuildState { Kind = BuildStateKind.PanelText, Text = "x" }, null, out _));
    }

    // ---- #683: the way through is one the plugin ACCEPTS -------------------------------------------------------

    private static SliderRounding R(string r) => (SliderRounding)Enum.Parse(typeof(SliderRounding), r, true);

    private static JsonObject Json(BuildState s) => new JsonObject
    {
      ["min"] = s.Min, ["max"] = s.Max, ["value"] = s.Value, ["rounding"] = s.Rounding.ToString().ToLowerInvariant(),
    };

    /// <summary>
    /// THE LAW AS A THEORY (#626 applied to set_value, ruled at #683): every refused rounding change names a first step,
    /// and that step - fed back through SetValueAsk.Validate against the slider as it is - is ACCEPTED; and the change the
    /// user asked for, from the state step 1 leaves, is accepted too. The branch named the new grid's neighbours of the
    /// current value, which the rows marked (was refused) sent into a second refusal: an Even-to-Odd flip (the neighbour is
    /// not on the CURRENT grid) and a domain holding no neighbour at all.
    /// </summary>
    [Theory]
    [InlineData("0", "1", "0.25", "float", "0", "100", "40", "integer")]      // the measured flip
    [InlineData("0.1", "0.9", "0.25", "float", "0", "100", "40", "integer")]  // (was refused) no whole number in 0.1..0.9
    [InlineData("0", "10", "4", "even", "1", "9", "3", "odd")]                 // (was refused) 3 is not even
    [InlineData("1", "9", "3", "odd", "0", "10", "4", "even")]                 // (was refused) 4 is not odd
    [InlineData("1", "9", "4", "even", "1", "9", "3", "odd")]                  // (was refused) a user-made even slider, odd bounds
    [InlineData("0", "10", "3", "integer", "0", "10", "4", "even")]
    [InlineData("1", "1", "1", "integer", "0", "2", "2", "even")]              // (was refused) the domain holds no even number
    [InlineData("0", "1", "0.5", "float", "1", "9", "3", "odd")]               // (was refused) the lower odd neighbour, -1, is outside 0..1
    public void EVERY_REFUSED_ROUNDING_CHANGE_NAMES_A_FIRST_STEP_THE_PLUGIN_ACCEPTS(
      string cMin, string cMax, string cValue, string cRounding, string dMin, string dMax, string dValue, string dRounding)
    {
      var current = Slider(decimal.Parse(cMin), decimal.Parse(cMax), decimal.Parse(cValue), R(cRounding), 0);
      Assert.True(SetValueAsk.Validate(new HandoffSeed { Target = 7, State = Json(Slider(decimal.Parse(dMin), decimal.Parse(dMax),
                                         decimal.Parse(dValue), R(dRounding), 0)) },
                                       Alive(7), Live("Number Slider", current), out _, out _, out var refused) == false);
      Assert.Equal(ToolErrors.PreconditionRefused, refused.Code);

      var declared = Slider(decimal.Parse(dMin), decimal.Parse(dMax), decimal.Parse(dValue), R(dRounding), 0);
      var first = SetValueCore.WayThrough(declared, current);
      Assert.NotNull(first);
      Assert.Contains("first set it to " + SetValueCore.Describe(first), refused.Message);

      // Step 1, against the slider as it is now.
      Assert.True(SetValueAsk.Validate(new HandoffSeed { Target = 7, State = Json(first) }, Alive(7), Live("Number Slider", current),
                                       out _, out var landed, out var step1Error), "step 1 refused: " + step1Error?.Message);

      // The change the user asked for, against the state step 1 left.
      Assert.True(SetValueAsk.Validate(new HandoffSeed { Target = 7, State = Json(declared) }, Alive(7), Live("Number Slider", landed),
                                       out _, out _, out var step2Error), "step 2 refused: " + step2Error?.Message);
    }

    [Fact]
    public void A_CHANGE_THE_RULE_ALLOWS_NEEDS_NO_WAY_THROUGH()
    {
      Assert.Null(SetValueCore.WayThrough(Slider(0, 100, 40, SliderRounding.Integer, 0), Slider(0, 1, 1, SliderRounding.Float, 3)));
    }

    // ---- #683 (6b): the type the card named reaches the click's scope rule ---------------------------------------

    [Fact]
    public void THE_CARD_TYPE_TRAVELS_ON_THE_SEED_TO_THE_CLICK()
    {
      var seed = new HandoffSeed { Target = 42, State = State("{\"text\":\"hi\"}"), TargetType = "Panel", Why = "because" };
      var plan = SeedPlan.From("set_value", seed, _ => IdState.Alive);

      Assert.Equal("Panel", plan.TargetType);
      Assert.False(SetValueCore.MayProceed(IdState.Alive, "Number Slider", plan.TargetType, out var refusal));
      Assert.Contains("now a Number Slider, not the Panel the card described", refusal);
    }

    // ---- the whole ask, from the seed to a validated change ----------------------------------------------

    private static JsonObject State(string json) => (JsonObject)JsonNode.Parse(json);

    private static Func<int, IdState> Alive(params int[] ids) => id => ids.Contains(id) ? IdState.Alive : IdState.Unknown;

    private static Func<int, LiveTarget> Live(string type, BuildState current, string nick = null) =>
      id => new LiveTarget { Type = type, Nick = nick, Current = current };

    [Fact]
    public void A_VALID_ASK_YIELDS_THE_EXACT_CHANGE_THE_CARD_WILL_NAME()
    {
      var seed = new HandoffSeed { Target = 42, State = State("{\"min\":0,\"max\":100,\"value\":40,\"rounding\":\"float\"}") };
      var ok = SetValueAsk.Validate(seed, Alive(42), Live("Number Slider", Slider(0, 1, 0.25m, SliderRounding.Float, 3), "Count"),
                                    out var change, out var declared, out var error);
      Assert.True(ok, error?.Message);
      Assert.Equal(42, change.Id);
      Assert.Equal("Number Slider", change.Type);
      Assert.Equal("Count", change.Nick);
      Assert.Equal("0.25 (0 to 1, float)", change.Before);
      Assert.Equal("40 (0 to 100, float)", change.After);
      Assert.Equal(40m, declared.Value);
    }

    [Fact]
    public void A_MISSING_TARGET_OR_STATE_IS_REFUSED_BY_NAME()
    {
      Assert.False(SetValueAsk.Validate(new HandoffSeed { State = State("{\"text\":\"x\"}") }, Alive(1), Live("Panel", null), out _, out _, out var noTarget));
      Assert.Equal(ToolErrors.InvalidArgs, noTarget.Code);
      Assert.Contains("target", noTarget.Message);

      Assert.False(SetValueAsk.Validate(new HandoffSeed { Target = 1 }, Alive(1), Live("Panel", null), out _, out _, out var noState));
      Assert.Equal(ToolErrors.InvalidArgs, noState.Code);
      Assert.Contains("state", noState.Message);
    }

    [Fact]
    public void AN_UNKNOWN_OR_RETIRED_TARGET_IS_REFUSED_AND_NEVER_RESOLVED_TO_SOMETHING_ELSE()
    {
      var seed = new HandoffSeed { Target = 99, State = State("{\"text\":\"x\"}") };
      Assert.False(SetValueAsk.Validate(seed, Alive(1), Live("Panel", null), out _, out _, out var e));
      Assert.Equal(ToolErrors.UnknownId, e.Code);

      Assert.False(SetValueAsk.Validate(seed, id => IdState.Retired, Live("Panel", null), out _, out _, out var r));
      Assert.Equal(ToolErrors.RetiredId, r.Code);
    }

    [Fact]
    public void A_TYPE_THAT_TAKES_NO_STATE_IS_REFUSED_WITH_THE_LIST_BY_THE_SHARED_PARSER()
    {
      // Stream A's parser, unforked: the refusal is its refusal, with typesThatAcceptState in the detail.
      var seed = new HandoffSeed { Target = 7, State = State("{\"min\":0,\"max\":1,\"value\":0.5}") };
      Assert.False(SetValueAsk.Validate(seed, Alive(7), Live("Loft", null), out _, out _, out var e));
      Assert.Equal(ToolErrors.InvalidArgs, e.Code);
      Assert.Contains("Loft", e.Message);
      Assert.NotNull(e.Detail["typesThatAcceptState"]);
    }

    [Fact]
    public void THE_PARSERS_OWN_REFUSALS_ARRIVE_UNCHANGED()
    {
      var seed = new HandoffSeed { Target = 7, State = State("{\"min\":10,\"max\":0,\"value\":5}") };
      Assert.False(SetValueAsk.Validate(seed, Alive(7), Live("Number Slider", Slider(0, 1, 0.5m, SliderRounding.Float, 1)), out _, out _, out var e));
      Assert.Contains("minimum", e.Message);
    }

    [Fact]
    public void THE_ROUNDING_RESTORE_RULE_IS_APPLIED_AT_THE_ASK()
    {
      var seed = new HandoffSeed { Target = 7, State = State("{\"min\":0,\"max\":100,\"value\":40,\"rounding\":\"integer\"}") };
      Assert.False(SetValueAsk.Validate(seed, Alive(7), Live("Number Slider", Slider(0, 1, 0.25m, SliderRounding.Float, 3)), out _, out _, out var e));
      Assert.Equal(ToolErrors.PreconditionRefused, e.Code);
      Assert.Contains("whole number", e.Message);
    }

    [Fact]
    public void A_LIVE_TARGET_THAT_COULD_NOT_BE_READ_IS_REFUSED_NOT_GUESSED()
    {
      var seed = new HandoffSeed { Target = 7, State = State("{\"text\":\"x\"}") };
      Assert.False(SetValueAsk.Validate(seed, Alive(7), id => null, out _, out _, out var e));
      Assert.Equal(ToolErrors.RetiredId, e.Code);
    }

    [Fact]
    public void A_PANEL_ASK_DESCRIBES_BEFORE_AND_AFTER_AS_TEXT()
    {
      var seed = new HandoffSeed { Target = 3, State = State("{\"text\":\"new text\"}") };
      var ok = SetValueAsk.Validate(seed, Alive(3), Live("Panel", new BuildState { Kind = BuildStateKind.PanelText, Text = "old" }),
                                    out var change, out _, out var error);
      Assert.True(ok, error?.Message);
      Assert.Equal("\"old\"", change.Before);
      Assert.Equal("\"new text\"", change.After);
    }

    // ---- the catalogue tool's arguments (the MCP face) ------------------------------------------------------

    [Fact]
    public void THE_CATALOGUE_TOOL_PARSES_ID_AND_STATE_THROUGH_THE_SAME_PATH()
    {
      var args = (JsonObject)JsonNode.Parse("{\"id\":5,\"state\":{\"min\":0,\"max\":10,\"value\":5}}");
      Assert.True(SetValueCore.TryParseArgs(args, out var seed, out var error), error?.Message);
      Assert.Equal(5, seed.Target);
      Assert.Equal(5m, seed.State["value"].GetValue<decimal>());

      Assert.False(SetValueCore.TryParseArgs((JsonObject)JsonNode.Parse("{\"state\":{}}"), out _, out var noId));
      Assert.Equal(ToolErrors.InvalidArgs, noId.Code);
      Assert.False(SetValueCore.TryParseArgs((JsonObject)JsonNode.Parse("{\"id\":5}"), out _, out var noState));
      Assert.Equal(ToolErrors.InvalidArgs, noState.Code);
    }
  }
}
