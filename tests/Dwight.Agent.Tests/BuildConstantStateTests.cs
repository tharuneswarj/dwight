using System.Linq;
using System.Text.Json.Nodes;
using Dwight.Agent;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// U30 (#926): A CONSTANT HAS A HOME IN THE BUILD SPEC.
  ///
  /// Seen twice at a sitting: three constant sliders on a staircase (a slider nobody will drag is the wrong component),
  /// and a valueless Number param that cost a 23-component rebuild. A Number, Integer or Text param may now be born
  /// holding one value. The refusals follow the slider's discipline (#383): the declaration is checked on the param's
  /// own grid - a number for a Number, a whole number for an Integer, text for a Text - so the read-back can be an
  /// equality. And the scope split is held by a test: set_value's published contract (a Number Slider or a Panel) is
  /// not widened by the same parser growing a row for builds.
  /// </summary>
  public class BuildConstantStateTests
  {
    private static JsonNode J(string json) => JsonNode.Parse(json);

    private static bool Build(string json, string type, out BuildState state, out ToolResult error) =>
      BuildStateParser.TryParse(J(json), type, "h", StateScope.Build, out state, out error);

    // ---- the home -------------------------------------------------------------------------------------------

    [Fact]
    public void A_Number_param_takes_a_value_and_becomes_a_constant()
    {
      Assert.True(Build("{\"value\":12.5}", "Number", out var s, out var e), e?.Message);
      Assert.Equal(BuildStateKind.Constant, s.Kind);
      Assert.Equal(ConstantType.Number, s.ConstantType);
      Assert.Equal(12.5m, s.Value);
      Assert.Equal("12.5", BuildStateText.Value(s));
    }

    [Fact]
    public void An_Integer_param_takes_a_whole_value()
    {
      Assert.True(Build("{\"value\":7}", "Integer", out var s, out _));
      Assert.Equal(ConstantType.Integer, s.ConstantType);
      Assert.Equal(7m, s.Value);
    }

    [Fact]
    public void A_Text_param_takes_text()
    {
      Assert.True(Build("{\"text\":\"ground\"}", "Text", out var s, out _));
      Assert.Equal(ConstantType.Text, s.ConstantType);
      Assert.Equal("ground", s.Text);
      Assert.Equal("\"ground\"", BuildStateText.Value(s));
    }

    [Fact]
    public void Type_names_match_the_way_the_rest_of_the_spec_does()
    {
      Assert.True(Build("{\"value\":1}", "number", out var s, out _));   // the parser's lookup is case-insensitive, like Accepts
      Assert.Equal(BuildStateKind.Constant, s.Kind);
    }

    // ---- the refusals, each a repair instruction ---------------------------------------------------------------

    [Fact]
    public void A_NON_NUMERIC_VALUE_FOR_A_NUMBER_IS_REFUSED_never_coerced()
    {
      Assert.False(Build("{\"value\":\"twelve\"}", "Number", out _, out var e));
      Assert.Contains("\"value\" must be a number", e.Message);
      Assert.Contains("(component \"h\")", e.Message);

      // "12" in quotes is text too: a string that happens to parse is still the wrong type, and coercing it would be
      // the silent-acceptance class every slider refusal exists to close.
      Assert.False(Build("{\"value\":\"12\"}", "Number", out _, out var quoted));
      Assert.Contains("must be a number", quoted.Message);
    }

    [Fact]
    public void A_FRACTION_FOR_AN_INTEGER_IS_REFUSED_WITH_ITS_NEIGHBOURS()
    {
      Assert.False(Build("{\"value\":7.6}", "Integer", out _, out var e));
      Assert.Contains("7.6 is not a whole number", e.Message);
      Assert.Contains("declare 7 or 8", e.Message);
    }

    [Fact]
    public void A_DOMAIN_ON_A_NUMBER_IS_REFUSED_AS_A_SLIDER_ASKED_FOR_BY_THE_WRONG_TYPE()
    {
      Assert.False(Build("{\"min\":0,\"max\":10,\"value\":4}", "Number", out _, out var e));
      Assert.Contains("takes only \"value\"", e.Message);
      Assert.Contains("CONSTANT, not a slider", e.Message);
      Assert.Contains("Number Slider", e.Message);
    }

    [Fact]
    public void TEXT_ON_A_NUMBER_and_A_VALUE_ON_A_TEXT_are_each_refused_by_name()
    {
      Assert.False(Build("{\"text\":\"x\"}", "Number", out _, out var e1));
      Assert.Contains("holds one number, not text", e1.Message);

      Assert.False(Build("{\"value\":3}", "Text", out _, out var e2));
      Assert.Contains("takes only \"text\"", e2.Message);
    }

    [Fact]
    public void A_missing_value_or_text_is_refused_not_defaulted()
    {
      Assert.False(Build("{}", "Number", out _, out var e1));
      Assert.Contains("needs \"value\"", e1.Message);
      Assert.False(Build("{}", "Text", out _, out var e2));
      Assert.Contains("needs \"text\"", e2.Message);
    }

    [Fact]
    public void A_value_a_double_cannot_hold_exactly_is_refused_rather_than_rounded()
    {
      // U59: the six-place cap is the SLIDER's; a constant in a param is a double and is refused only past that.
      Assert.False(Build("{\"value\":0.1234567890123456789012}", "Number", out _, out var e));
      Assert.Contains("double", e.Message);
      Assert.Contains("15 significant digits", e.Message);
    }

    [Fact]
    public void Tau_into_a_Number_param_is_accepted_and_lands_as_declared()
    {
      Assert.True(Build("{\"value\":6.283185307179586}", "Number", out var s, out var e), e?.Message);
      Assert.Equal(6.283185307179586m, s.Value);
      Assert.Equal(System.Math.PI * 2, ConstantNumber.ToDouble(s.Value));
      // and reads back as the very same declaration (the read-back is an equality, #383)
      Assert.Equal(s.Value, ConstantNumber.FromDouble(ConstantNumber.ToDouble(s.Value)));
    }

    [Fact]
    public void More_than_six_places_in_a_Number_param_is_no_longer_refused()
    {
      Assert.True(Build("{\"value\":0.1234567}", "Number", out var s, out var e), e?.Message);
      Assert.Equal(0.1234567m, s.Value);
    }

    [Fact]
    public void Tau_into_a_slider_is_refused_with_the_sliders_own_sentence()
    {
      Assert.False(BuildStateParser.TryParse(
        J("{\"min\":0,\"max\":10,\"value\":6.283185307179586}"), "Number Slider", "h", StateScope.Build, out _, out var e));
      Assert.Contains("A slider holds at most 6 decimal places", e.Message);
    }

    // ---- the scope split: set_value's contract is not widened ------------------------------------------------------

    [Fact]
    public void SET_VALUE_STILL_REFUSES_A_NUMBER_PARAM_WITH_ITS_OWN_TWO_TYPE_LIST()
    {
      // The one-argument-shorter TryParse is set_value's (SetValueAsk calls it): a Number param is not in its scope, and
      // the refusal lists exactly what set_value's kind table promises - a slider and a panel.
      Assert.False(BuildStateParser.TryParse(J("{\"value\":12}"), "Number", "component 5", out _, out var e));
      Assert.Contains("does not take a \"state\"", e.Message);
      var listed = e.Detail["typesThatAcceptState"].AsArray().Select(n => n.GetValue<string>()).ToArray();
      Assert.Equal(new[] { "Number Slider", "Panel" }, listed);
    }

    [Fact]
    public void THE_BUILD_SCOPE_LISTS_THE_CONSTANTS_IN_ITS_REFUSAL()
    {
      Assert.False(Build("{\"value\":12}", "Loft", out _, out var e));
      var listed = e.Detail["typesThatAcceptState"].AsArray().Select(n => n.GetValue<string>()).ToArray();
      Assert.Equal(new[] { "Integer", "Number", "Number Slider", "Panel", "Text" }, listed);
    }

    [Fact]
    public void A_slider_and_a_panel_parse_exactly_as_before_in_the_build_scope()
    {
      Assert.True(Build("{\"min\":0,\"max\":100,\"value\":40,\"rounding\":\"integer\"}", "Number Slider", out var s, out _));
      Assert.Equal(BuildStateKind.Slider, s.Kind);
      Assert.True(Build("{\"text\":\"hello\"}", "Panel", out var p, out _));
      Assert.Equal(BuildStateKind.PanelText, p.Kind);
    }

    // ---- the token and the value text ---------------------------------------------------------------------------

    [Fact]
    public void The_held_state_token_tells_two_constants_apart_and_from_a_slider()
    {
      Build("{\"value\":12}", "Number", out var a, out _);
      Build("{\"value\":13}", "Number", out var b, out _);
      Build("{\"min\":0,\"max\":100,\"value\":12}", "Number Slider", out var slider, out _);

      Assert.NotEqual(StateToken.Of(a), StateToken.Of(b));
      Assert.NotEqual(StateToken.Of(a), StateToken.Of(slider));
      Assert.Equal("const:Number@12", StateToken.Of(a));
    }

    [Fact]
    public void The_value_text_is_the_value_only_for_every_kind()
    {
      Build("{\"min\":0,\"max\":100,\"value\":40}", "Number Slider", out var slider, out _);
      Build("{\"text\":\"a long panel text that goes on\"}", "Panel", out var panel, out _);
      Assert.Equal("40", BuildStateText.Value(slider));
      Assert.Equal("\"a long panel text that goes on\"", BuildStateText.Value(panel));
      Assert.Null(BuildStateText.Value(null));
    }

    // ---- U40 (#975): a per-type decision runs on the CANONICAL name, never the raw string ------------------------

    [Fact]
    public void U40_A_QUALIFIED_Number_takes_a_value_exactly_as_the_bare_form_does()
    {
      Assert.True(Build("{\"value\":12}", "Params > Primitive > Number", out var q, out var e), e?.Message);
      Assert.True(Build("{\"value\":12}", "Number", out var b, out _));               // positive control: the bare form
      Assert.Equal(BuildStateKind.Constant, q.Kind);
      Assert.Equal(b.Value, q.Value);
      Assert.Equal(ConstantType.Number, q.ConstantType);
    }

    [Fact]
    public void U40_A_QUALIFIED_slider_and_panel_are_judged_as_the_slider_and_the_panel()
    {
      Assert.True(Build("{\"min\":0,\"max\":10,\"value\":4}", "Params > Input > Number Slider", out var s, out var e1), e1?.Message);
      Assert.Equal(BuildStateKind.Slider, s.Kind);
      Assert.True(Build("{\"text\":\"hi\"}", "Params > Input > Panel", out var p, out var e2), e2?.Message);
      Assert.Equal(BuildStateKind.PanelText, p.Kind);
      Assert.True(BuildStateParser.AcceptsState("Params > Input > Panel"));
    }

    [Fact]
    public void U40_The_refusal_for_a_type_that_takes_no_state_names_the_CANONICAL_type()
    {
      Assert.False(Build("{\"value\":1}", "Maths > Operators > Addition", out _, out var e));
      Assert.Contains("\"Addition\" does not take a \"state\"", e.Message);
      Assert.DoesNotContain("Maths >", e.Message);
      // the unqualified refusal is unchanged
      Assert.False(Build("{\"value\":1}", "Addition", out _, out var e2));
      Assert.Contains("\"Addition\" does not take a \"state\"", e2.Message);
    }

    [Fact]
    public void U40_A_qualified_Number_with_a_value_parses_and_cards_through_the_whole_spec()
    {
      var res = Resolvers();
      res.TypeExists = t => Installed.Contains(Dwight.Components.ComponentMatchCore.BareName(t), System.StringComparer.OrdinalIgnoreCase);
      var seed = new HandoffSeed
      {
        Place = (JsonObject)J(@"{""components"":[
          {""ref"":""h"",""type"":""Params > Primitive > Number"",""state"":{""value"":12}}]}"),
      };
      Assert.True(SpotAsk.Validate(seed, res, id => null, out var ask, out var e), e?.Message);
      Assert.Equal(new[] { "a Number set to 12" }, ask.Placement.Components.ToArray());
    }

    /// <summary>Sixth gate: a row read `Placed a Panel set to "", Spot 5`. A component with no text names no value, on the card's act and in History's sentence, which
    /// are built from the same names. A number's zero is a value.</summary>
    [Theory]
    [InlineData("{\"text\":\"\"}", "a Panel")]
    [InlineData("{\"text\":\"   \"}", "a Panel")]
    [InlineData("{\"text\":\"hello\"}", "a Panel set to \"hello\"")]
    public void A_panel_with_no_text_names_no_value(string state, string name)
    {
      var seed = new HandoffSeed
      {
        Place = (JsonObject)J("{\"components\":[{\"ref\":\"p\",\"type\":\"Panel\",\"state\":" + state + "}]}"),
      };
      Assert.True(SpotAsk.Validate(seed, Resolvers(), id => null, out var ask, out var e), e?.Message);
      Assert.Equal(new[] { name }, ask.Placement.Components.ToArray());
      ask.Placement.Address = "Spot 5";
      Assert.Equal("Place " + name + ", Spot 5, in clear space on the canvas.", ConsentCard.SpotAct(ask.Placement));
      Assert.Equal("Placed " + name + ", Spot 5, in clear space on the canvas.", ConsentCard.SpotDone(ask.Placement));
    }

    [Fact]
    public void A_diagnostic_still_says_a_param_holds_empty_text_and_a_zero_is_a_value()
    {
      Assert.Equal("\"\"", BuildStateText.Value(new BuildState { Kind = BuildStateKind.PanelText, Text = "" }));
      Assert.Null(BuildStateText.NamedValue(new BuildState { Kind = BuildStateKind.PanelText, Text = "  " }));
      Assert.Equal("0", BuildStateText.NamedValue(new BuildState { Kind = BuildStateKind.Slider, Value = 0m }));
    }

    // ---- through the spec and onto the card ----------------------------------------------------------------------

    private static readonly string[] Installed = { "Number", "Integer", "Text", "Number Slider", "Panel", "Loft" };

    private static SpecResolvers Resolvers() => new SpecResolvers
    {
      TypeExists = t => Installed.Contains(t, System.StringComparer.OrdinalIgnoreCase),
      Nearest = t => new string[0],
      IdState = id => IdState.Alive,
      PortsOfType = t => new PortShape { Inputs = new string[0], Outputs = new string[0] },
      PortsOfId = id => null,
    };

    [Fact]
    public void A_spec_may_place_a_constant_and_wire_from_it()
    {
      var args = (JsonObject)J(@"{""components"":[
          {""ref"":""h"",""type"":""Number"",""nick"":""Height"",""state"":{""value"":12}},
          {""ref"":""l"",""type"":""Loft""}],
        ""wires"":[{""from"":{""ref"":""h""},""to"":{""ref"":""l""}}]}");

      Assert.True(Resolvers().Parse(args, out var spec, out var e), e?.Message);
      var h = spec.Components.First(c => c.Ref == "h");
      Assert.Equal(BuildStateKind.Constant, h.State.Kind);
      Assert.Equal(12m, h.State.Value);
    }

    [Fact]
    public void THE_CARD_SHOWS_THE_VALUE_BESIDE_THE_COMPONENT()
    {
      var seed = new HandoffSeed
      {
        Place = (JsonObject)J(@"{""components"":[
          {""ref"":""h"",""type"":""Number"",""nick"":""Height"",""state"":{""value"":12}},
          {""ref"":""c"",""type"":""Number Slider"",""nick"":""Count"",""state"":{""min"":0,""max"":100,""value"":40,""rounding"":""integer""}},
          {""ref"":""p"",""type"":""Panel"",""state"":{""text"":""hello""}},
          {""ref"":""g"",""type"":""Text"",""nick"":""Level"",""state"":{""text"":""ground""}},
          {""ref"":""n"",""type"":""Number""}]}"),
      };

      Assert.True(SpotAsk.Validate(seed, Resolvers(), id => null, out var ask, out var e), e?.Message);
      Assert.Equal(new[] { "a Height set to 12", "a Count set to 40", "a Panel set to \"hello\"", "a Level set to \"ground\"", "a Number" },
        ask.Placement.Components.ToArray());
    }

    [Fact]
    public void A_constant_round_trips_through_the_spot_record_to_the_same_state()
    {
      var seed = new HandoffSeed
      {
        Place = (JsonObject)J(@"{""components"":[
          {""ref"":""h"",""type"":""Number"",""state"":{""value"":12.50}},
          {""ref"":""g"",""type"":""Text"",""state"":{""text"":""ground""}}]}"),
      };
      Assert.True(SpotAsk.Validate(seed, Resolvers(), id => null, out var ask, out _));

      Assert.True(Resolvers().Parse(ask.Record, out var back, out var e), e?.Message);
      Assert.Equal(12.50m, back.Components[0].State.Value);
      Assert.Equal(2, (decimal.GetBits(back.Components[0].State.Value)[3] >> 16) & 0xFF);   // the scale survived the text
      Assert.Equal(ConstantType.Text, back.Components[1].State.ConstantType);
      Assert.Equal("ground", back.Components[1].State.Text);
    }
  }
}
