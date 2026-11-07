using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Dwight.Agent;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// Stage D: the build spec's validation - everything that must be refused BEFORE a single component
  /// reaches the canvas (validate-before-instantiate, ADR-010). These are the two hallucination doors:
  /// a component type nothing provides, and an id the model invented.
  ///
  /// The refusals are as much the product as the successes. A half-valid spec must never half-build:
  /// the canvas is either untouched or it carries the whole declared change.
  /// </summary>
  public class BuildSpecTests
  {
    private static readonly string[] Installed = { "Loft", "Divide Curve", "Number Slider", "Panel", "Move" };

    private static bool Exists(string t) => Installed.Contains(t, StringComparer.OrdinalIgnoreCase);
    private static IReadOnlyList<string> Nearest(string t) => Dwight.Components.ComponentMatchCore.Nearest(t, Installed);

    /// <summary>ids 1-2 alive, 3 retired, everything else unknown.</summary>
    private static IdState State(int id) => id == 1 || id == 2 ? IdState.Alive : id == 3 ? IdState.Retired : IdState.Unknown;

    private static bool Parse(string json, out BuildSpec spec, out ToolResult error) =>
      BuildSpecParser.TryParse((JsonObject)JsonNode.Parse(json), Exists, Nearest, State, out spec, out error);

    // ---- the happy path ----------------------------------------------------------------------------

    [Fact]
    public void A_valid_spec_parses_components_and_wires()
    {
      var ok = Parse(@"{
        ""components"":[{""ref"":""a"",""type"":""Divide Curve""},{""ref"":""b"",""type"":""Loft"",""nick"":""roof""}],
        ""wires"":[{""from"":{""ref"":""a"",""param"":""Points""},""to"":{""ref"":""b"",""param"":""Curves""}}]
      }", out var spec, out var err);

      Assert.True(ok, err?.Message);
      Assert.Equal(2, spec.Components.Count);
      Assert.Equal("roof", spec.Components[1].Nick);
      Assert.Single(spec.Wires);
    }

    [Fact]
    public void A_wire_may_land_on_an_existing_component_by_id()
    {
      var ok = Parse(@"{
        ""components"":[{""ref"":""a"",""type"":""Loft""}],
        ""wires"":[{""from"":{""ref"":""a"",""param"":""Loft""},""to"":{""id"":1,""param"":""Geometry""}}]
      }", out var spec, out var err);

      Assert.True(ok, err?.Message);
      Assert.Equal(1, spec.Wires[0].ToId);
    }

    [Fact]
    public void Wires_are_optional()
    {
      Assert.True(Parse(@"{""components"":[{""ref"":""a"",""type"":""Panel""}]}", out var spec, out _));
      Assert.Empty(spec.Wires);
      Assert.Empty(spec.Reconnects);
    }

    // ---- #625: declared reconnections ---------------------------------------------------------------

    [Fact]
    public void A_reconnection_parses_its_consumer_input_position_and_new_output()
    {
      var ok = Parse(@"{
        ""components"":[{""ref"":""pt"",""type"":""Loft""}],
        ""reconnect"":[{""to"":{""id"":1,""param"":""Origin"",""at"":2},""from"":{""ref"":""pt"",""param"":""Loft""}}]
      }", out var spec, out var err);

      Assert.True(ok, err?.Message);
      var r = Assert.Single(spec.Reconnects);
      Assert.Equal(1, r.ToId);
      Assert.Equal("Origin", r.ToParam);
      Assert.Equal(2, r.At);
      Assert.Equal("pt", r.FromRef);
      Assert.Equal("Loft", r.FromParam);
    }

    [Fact]
    public void A_reconnections_to_end_must_be_an_existing_id_never_a_ref()
    {
      Assert.False(Parse(@"{""components"":[{""ref"":""pt"",""type"":""Loft""}],
        ""reconnect"":[{""to"":{""ref"":""pt""},""from"":{""ref"":""pt""}}]}", out _, out var err));
      Assert.Equal(ToolErrors.InvalidArgs, err.Code);
      Assert.Contains("give its \"id\"", err.Message);
    }

    [Fact]
    public void A_reconnections_from_end_must_be_a_ref_never_an_existing_id()
    {
      Assert.False(Parse(@"{""components"":[{""ref"":""pt"",""type"":""Loft""}],
        ""reconnect"":[{""to"":{""id"":1},""from"":{""id"":2}}]}", out _, out var err));
      Assert.Contains("must be a component this build places", err.Message);
    }

    [Theory]
    [InlineData(@"[{""to"":{""id"":1}}]", "needs a \"to\" and a \"from\"")]
    [InlineData(@"[{""to"":{""id"":1,""at"":-1},""from"":{""ref"":""pt""}}]", "\"at\" must be a whole number")]
    [InlineData(@"[{""to"":{""id"":1},""from"":{""ref"":""nope""}}]", "not a component in this spec")]
    [InlineData(@"[{""to"":{""id"":99},""from"":{""ref"":""pt""}}]", "No component with id 99")]
    public void A_malformed_reconnection_is_refused_before_anything_is_built(string reconnect, string expected)
    {
      Assert.False(Parse(@"{""components"":[{""ref"":""pt"",""type"":""Loft""}],""reconnect"":" + reconnect + "}",
        out _, out var err));
      Assert.Contains(expected, err.Message);
    }

    /// <summary>The consumer's input resolves by ARITY against its real ports, like any wire end (#208).</summary>
    [Fact]
    public void A_reconnections_input_is_resolved_against_the_consumers_real_ports()
    {
      Func<int, PortShape> ports = id => new PortShape { Inputs = new[] { "Origin", "X", "Y" }, Outputs = new[] { "Plane" } };
      Func<string, PortShape> types = t => new PortShape { Inputs = new[] { "A" }, Outputs = new[] { "Point" } };

      Assert.False(BuildSpecParser.TryParse((System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse(
        @"{""components"":[{""ref"":""pt"",""type"":""Loft""}],""reconnect"":[{""to"":{""id"":1,""param"":""Orign""},""from"":{""ref"":""pt""}}]}"),
        Exists, Nearest, State, types, ports, out _, out var err));
      Assert.Contains("No input named \"Orign\"", err.Message);

      Assert.True(BuildSpecParser.TryParse((System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse(
        @"{""components"":[{""ref"":""pt"",""type"":""Loft""}],""reconnect"":[{""to"":{""id"":1,""param"":""origin""},""from"":{""ref"":""pt""}}]}"),
        Exists, Nearest, State, types, ports, out var spec, out err), err?.Message);
      Assert.Equal("Origin", spec.Reconnects[0].ToParam);   // the canvas's spelling
      Assert.Equal("Point", spec.Reconnects[0].FromParam);  // one output: the plugin fills it in
    }

    // ---- door 1: invented component types ----------------------------------------------------------

    [Fact]
    public void AN_UNINSTALLED_TYPE_IS_REFUSED_WITH_THE_NEAREST_REAL_NAMES()
    {
      // ADR-034 at the boundary, with ADR-092's shape: the refusal carries the ground truth, so the
      // model can correct itself instead of guessing a second time.
      Assert.False(Parse(@"{""components"":[{""ref"":""a"",""type"":""Lofr""}]}", out _, out var err));

      Assert.Equal(ToolErrors.UnknownComponentType, err.Code);
      Assert.Contains("Loft", err.Detail["nearestInstalled"].AsArray().Select(n => n.GetValue<string>()));
    }

    [Fact]
    public void A_wholly_invented_type_is_still_refused_even_with_no_suggestion_to_offer()
    {
      Assert.False(Parse(@"{""components"":[{""ref"":""a"",""type"":""QuantumFluxCapacitor""}]}", out _, out var err));
      Assert.Equal(ToolErrors.UnknownComponentType, err.Code);
    }

    // ---- door 2: invented or dead ids --------------------------------------------------------------

    [Fact]
    public void AN_INVENTED_ID_IS_REFUSED()
    {
      Assert.False(Parse(@"{
        ""components"":[{""ref"":""a"",""type"":""Loft""}],
        ""wires"":[{""from"":{""ref"":""a""},""to"":{""id"":9999,""param"":""X""}}]
      }", out _, out var err));

      Assert.Equal(ToolErrors.UnknownId, err.Code);
      Assert.Contains("do not invent", err.Message);
    }

    [Fact]
    public void A_RETIRED_ID_IS_REFUSED_DISTINCTLY_FROM_AN_INVENTED_ONE()
    {
      // "that was deleted" and "you made that up" call for different corrections (#191).
      Assert.False(Parse(@"{
        ""components"":[{""ref"":""a"",""type"":""Loft""}],
        ""wires"":[{""from"":{""ref"":""a""},""to"":{""id"":3,""param"":""X""}}]
      }", out _, out var err));

      Assert.Equal(ToolErrors.RetiredId, err.Code);
    }

    [Fact]
    public void A_wire_to_a_ref_that_is_not_in_the_spec_is_refused()
    {
      Assert.False(Parse(@"{
        ""components"":[{""ref"":""a"",""type"":""Loft""}],
        ""wires"":[{""from"":{""ref"":""ghost""},""to"":{""ref"":""a"",""param"":""Curves""}}]
      }", out _, out var err));
      Assert.Equal(ToolErrors.InvalidArgs, err.Code);
    }

    // ---- structural validation ---------------------------------------------------------------------

    [Fact]
    public void An_empty_or_missing_components_array_is_refused()
    {
      Assert.False(Parse(@"{""components"":[]}", out _, out var e1));
      Assert.Equal(ToolErrors.InvalidArgs, e1.Code);
      Assert.False(Parse(@"{}", out _, out var e2));
      Assert.Equal(ToolErrors.InvalidArgs, e2.Code);
    }

    [Fact]
    public void Duplicate_refs_are_refused()
    {
      Assert.False(Parse(@"{""components"":[{""ref"":""a"",""type"":""Loft""},{""ref"":""a"",""type"":""Panel""}]}",
        out _, out var err));
      Assert.Equal(ToolErrors.InvalidArgs, err.Code);
      Assert.Contains("Duplicate", err.Message);
    }

    [Fact]
    public void A_component_missing_ref_or_type_is_refused()
    {
      Assert.False(Parse(@"{""components"":[{""type"":""Loft""}]}", out _, out _));
      Assert.False(Parse(@"{""components"":[{""ref"":""a""}]}", out _, out _));
    }

    [Fact]
    public void A_wire_end_with_both_ref_and_id_is_refused_rather_than_one_being_guessed()
    {
      Assert.False(Parse(@"{
        ""components"":[{""ref"":""a"",""type"":""Loft""}],
        ""wires"":[{""from"":{""ref"":""a"",""id"":1},""to"":{""id"":2,""param"":""X""}}]
      }", out _, out var err));
      Assert.Equal(ToolErrors.InvalidArgs, err.Code);
    }

    // ---- door 3: a PORT that does not exist --------------------------------------------------------
    //
    // Found at the Stage-D gate. The target port name used to be demanded unconditionally and then
    // ignored for params, while an unmatched SOURCE name silently fell back to output[0] - so a model
    // could name a port that does not exist, land somewhere else, and read a verdict saying the build
    // "landed as specified". Resolution is now by ARITY: the plugin answers wherever the answer is
    // forced, and a name is required only where it is a genuine choice - and there it must match.

    private static readonly Dictionary<string, PortShape> Ports = new Dictionary<string, PortShape>(StringComparer.OrdinalIgnoreCase)
    {
      ["Loft"] = Shape(new[] { "Curves", "Options" }, new[] { "Loft" }),
      ["Move"] = Shape(new[] { "Geometry", "Motion" }, new[] { "Geometry", "Transform" }),
      ["Divide Curve"] = Shape(new[] { "Curve", "Count", "Kinks" }, new[] { "Points", "Tangents", "Parameters" }),
      ["Panel"] = Shape(new string[0], new string[0]),           // a param: one implicit port, unnamed
      ["Number Slider"] = Shape(new string[0], new string[0]),
    };

    private static PortShape Shape(string[] inputs, string[] outputs) =>
      new PortShape { Inputs = inputs, Outputs = outputs };

    /// <summary>id 1 is a multi-input component; id 2 is a param.</summary>
    private static PortShape PortsOfId(int id) =>
      id == 1 ? Ports["Loft"] : id == 2 ? Ports["Panel"] : null;

    private static bool ParseWithPorts(string json, out BuildSpec spec, out ToolResult error) =>
      BuildSpecParser.TryParse((JsonObject)JsonNode.Parse(json), Exists, Nearest, State,
        t => Ports.TryGetValue(t, out var s) ? s : null, PortsOfId, out spec, out error);

    [Fact]
    public void A_WIRE_TO_A_MULTI_INPUT_COMPONENT_STILL_NEEDS_TO_SAY_WHICH_ONE()
    {
      // The property the old unconditional rule existed to protect, now enforced where it is true:
      // Move has two inputs, so picking one silently would build something the spec never described.
      Assert.False(ParseWithPorts(@"{
        ""components"":[{""ref"":""a"",""type"":""Loft""},{""ref"":""b"",""type"":""Move""}],
        ""wires"":[{""from"":{""ref"":""a""},""to"":{""ref"":""b""}}]
      }", out _, out var err));

      Assert.Equal(ToolErrors.InvalidArgs, err.Code);
      Assert.Contains("Geometry", err.Detail["available"].ToJsonString());   // the refusal repairs
    }

    [Fact]
    public void AN_INVENTED_PORT_NAME_IS_REFUSED_WITH_THE_REAL_ONES()
    {
      // The silent mis-wire this closes: "Crv" is not a port on Loft, and used to resolve to output[0].
      Assert.False(ParseWithPorts(@"{
        ""components"":[{""ref"":""a"",""type"":""Divide Curve""},{""ref"":""b"",""type"":""Loft""}],
        ""wires"":[{""from"":{""ref"":""a"",""param"":""Points""},""to"":{""ref"":""b"",""param"":""Crv""}}]
      }", out _, out var err));

      Assert.Equal(ToolErrors.InvalidArgs, err.Code);
      Assert.Contains("Crv", err.Detail["asked"].ToJsonString());
      Assert.Contains("Curves", err.Detail["available"].ToJsonString());
    }

    [Fact]
    public void AN_INVENTED_OUTPUT_NAME_IS_REFUSED_TOO_RATHER_THAN_FALLING_BACK_TO_THE_FIRST()
    {
      Assert.False(ParseWithPorts(@"{
        ""components"":[{""ref"":""a"",""type"":""Divide Curve""},{""ref"":""b"",""type"":""Loft""}],
        ""wires"":[{""from"":{""ref"":""a"",""param"":""Pts""},""to"":{""ref"":""b"",""param"":""Curves""}}]
      }", out _, out var err));

      Assert.Equal(ToolErrors.InvalidArgs, err.Code);
      Assert.Contains("Tangents", err.Detail["available"].ToJsonString());
    }

    [Fact]
    public void A_PARAM_TARGET_NEEDS_NO_PORT_NAME_BECAUSE_IT_HAS_NONE()
    {
      // The bug the gate hit: a Panel has no named input, so demanding one asked the model to invent a
      // name that was then discarded - teaching it that inventing port names works.
      Assert.True(ParseWithPorts(@"{
        ""components"":[{""ref"":""s"",""type"":""Number Slider""},{""ref"":""p"",""type"":""Panel""}],
        ""wires"":[{""from"":{""ref"":""s""},""to"":{""ref"":""p""}}]
      }", out var spec, out var err), err?.Message);

      Assert.Null(spec.Wires[0].ToParam);
      Assert.Null(spec.Wires[0].FromParam);
    }

    [Fact]
    public void A_SINGLE_PORT_END_IS_FILLED_IN_BY_THE_PLUGIN()
    {
      // ADR-056: the plugin computes structure. Loft has one output, so there is only one answer and
      // the model is not asked for it - but the resolved name is recorded, not left blank.
      Assert.True(ParseWithPorts(@"{
        ""components"":[{""ref"":""a"",""type"":""Loft""},{""ref"":""p"",""type"":""Panel""}],
        ""wires"":[{""from"":{""ref"":""a""},""to"":{""ref"":""p""}}]
      }", out var spec, out var err), err?.Message);

      Assert.Equal("Loft", spec.Wires[0].FromParam);
    }

    [Fact]
    public void A_PORT_NAME_IS_NORMALISED_TO_THE_CANVAS_SPELLING()
    {
      // The model's casing is not carried forward - what is stored is the port's own name.
      Assert.True(ParseWithPorts(@"{
        ""components"":[{""ref"":""a"",""type"":""Divide Curve""},{""ref"":""b"",""type"":""Loft""}],
        ""wires"":[{""from"":{""ref"":""a"",""param"":""points""},""to"":{""ref"":""b"",""param"":""CURVES""}}]
      }", out var spec, out _));

      Assert.Equal("Points", spec.Wires[0].FromParam);
      Assert.Equal("Curves", spec.Wires[0].ToParam);
    }

    [Fact]
    public void The_port_rules_apply_to_an_EXISTING_component_addressed_by_id()
    {
      // id 1 is a multi-input component; naming a port it does not have must refuse the same way.
      Assert.False(ParseWithPorts(@"{
        ""components"":[{""ref"":""a"",""type"":""Divide Curve""}],
        ""wires"":[{""from"":{""ref"":""a"",""param"":""Points""},""to"":{""id"":1,""param"":""Shape""}}]
      }", out _, out var err));
      Assert.Equal(ToolErrors.InvalidArgs, err.Code);

      // id 2 is a param, so it needs no name at all.
      Assert.True(ParseWithPorts(@"{
        ""components"":[{""ref"":""a"",""type"":""Divide Curve""}],
        ""wires"":[{""from"":{""ref"":""a"",""param"":""Points""},""to"":{""id"":2}}]
      }", out _, out var paramTargetError));
      Assert.Null(paramTargetError);
    }

    [Fact]
    public void UNKNOWN_PORTS_ARE_NOT_A_RULE()
    {
      // Fail open on KNOWLEDGE, never on safety. When the shape cannot be determined the parser invents
      // no requirement; an unresolvable port still fails later, loudly, with the canvas untouched.
      Assert.True(ParseWithPorts(@"{
        ""components"":[{""ref"":""a"",""type"":""Number Slider""}],
        ""wires"":[{""from"":{""ref"":""a""},""to"":{""id"":1,""param"":""Curves""}}]
      }", out _, out _));

      // id 99 has no known shape at all - and is also not a live id, so the id guard is what refuses.
      Assert.False(ParseWithPorts(@"{
        ""components"":[{""ref"":""a"",""type"":""Number Slider""}],
        ""wires"":[{""from"":{""ref"":""a""},""to"":{""id"":99}}]
      }", out _, out var err));
      Assert.Equal(ToolErrors.UnknownId, err.Code);
    }

    [Fact]
    public void An_oversized_build_is_refused_with_a_reason_rather_than_truncated()
    {
      // No silent caps (#134). Truncating to the limit would build something the model did not ask for.
      var comps = string.Join(",", Enumerable.Range(0, BuildSpecParser.MaxComponents + 1)
        .Select(i => $@"{{""ref"":""r{i}"",""type"":""Panel""}}"));
      Assert.False(Parse("{\"components\":[" + comps + "]}", out _, out var err));

      Assert.Equal(ToolErrors.PreconditionRefused, err.Code);
      Assert.Contains("smaller pieces", err.Message);
    }

    // ---- the region a build is AIMED at (WP-4 Stage B) --------------------------------------------

    [Fact]
    public void A_build_may_name_the_region_it_targets()
    {
      Assert.True(Parse(@"{""region"":""A1"",""components"":[{""ref"":""a"",""type"":""Panel""}]}",
        out var spec, out _));

      Assert.Equal("A1", spec.Region);
    }

    /// <summary>#354 Q3: the region is OPTIONAL - extend-build does not presuppose a map, and requiring
    /// one would break every build that is a small addition rather than a mapped region.</summary>
    [Fact]
    public void A_build_with_no_region_is_still_valid()
    {
      Assert.True(Parse(@"{""components"":[{""ref"":""a"",""type"":""Panel""}]}", out var spec, out _));

      Assert.Null(spec.Region);
    }

    /// <summary>Shape-checked here, resolved on the canvas later - but a malformed address is refused NOW
    /// rather than carried to a resolution step that would have to refuse it anyway (#208).</summary>
    [Theory]
    [InlineData("Prepare curves")]
    [InlineData("a1")]
    [InlineData("A")]
    [InlineData("1")]
    public void Something_that_is_not_an_address_is_refused_with_what_one_looks_like(string region)
    {
      Assert.False(Parse("{\"region\":\"" + region + "\",\"components\":[{\"ref\":\"a\",\"type\":\"Panel\"}]}",
        out _, out var err));

      Assert.Equal(ToolErrors.InvalidArgs, err.Code);
      Assert.Contains("A1", err.Message);
      Assert.Contains("get_scaffold_map", err.Message);
    }

    [Fact]
    public void A_blank_region_is_treated_as_no_region_rather_than_refused()
    {
      // An empty string is the shape a model produces when it has nothing to say, not an attempt to name
      // a region. Refusing it would be a refusal about punctuation.
      Assert.True(Parse(@"{""region"":""   "",""components"":[{""ref"":""a"",""type"":""Panel""}]}",
        out var spec, out _));

      Assert.Null(spec.Region);
    }

    // ---- U41 (#975): a port is matched by exact NAME or exact NICKNAME ---------------------------------------------

    private static readonly PortShape Circle = new PortShape
    {
      Inputs = new[] { "P", "R" }, InputNames = new[] { "Plane", "Radius" },
      Outputs = new[] { "C" }, OutputNames = new[] { "Circle" },
    };

    private static bool ParseCircleWire(string toParam, PortShape circle, out BuildSpec spec, out ToolResult error) =>
      BuildSpecParser.TryParse((JsonObject)JsonNode.Parse(
        "{\"components\":[{\"ref\":\"s\",\"type\":\"Panel\"},{\"ref\":\"c\",\"type\":\"Loft\"}]," +
        "\"wires\":[{\"from\":{\"ref\":\"s\"},\"to\":{\"ref\":\"c\",\"param\":\"" + toParam + "\"}}]}"),
        Exists, Nearest, State, t => t == "Loft" ? circle : Ports["Panel"], PortsOfId, out spec, out error);

    [Fact]
    public void U41_A_port_resolves_by_its_full_NAME()
    {
      Assert.True(ParseCircleWire("Radius", Circle, out var spec, out var e), e?.Message);
      Assert.Equal("R", spec.Wires[0].ToParam);          // resolved to the canvas's own label
    }

    [Fact]
    public void U41_A_port_still_resolves_by_its_NICKNAME()
    {
      Assert.True(ParseCircleWire("r", Circle, out var spec, out var e), e?.Message);   // positive control, case-insensitive as before
      Assert.Equal("R", spec.Wires[0].ToParam);
    }

    [Fact]
    public void U41_A_name_that_matches_two_ports_is_refused_not_first_wins()
    {
      // port 0 is labelled "X" and named "Y"; port 1 is labelled "Y" - the text "Y" is both, and the choice is not forced
      var ambiguous = new PortShape { Inputs = new[] { "X", "Y" }, InputNames = new[] { "Y", "Other" }, Outputs = new string[0] };
      Assert.False(ParseCircleWire("Y", ambiguous, out _, out var e));
      Assert.Equal(ToolErrors.InvalidArgs, e.Code);
      Assert.Contains("matches 2 inputs", e.Message);
    }

    /// <summary>U62: kb_lookup now shows "Radius (R)" and a refusal lists "R (Radius)"; a model that copies either is not refused for it.</summary>
    [Fact]
    public void U62_A_port_resolves_by_the_combined_spelling_the_lookup_shows()
    {
      Assert.True(ParseCircleWire("Radius (R)", Circle, out var spec, out var e), e?.Message);
      Assert.Equal("R", spec.Wires[0].ToParam);
      Assert.True(ParseCircleWire("R (Radius)", Circle, out spec, out e), e?.Message);
      Assert.Equal("R", spec.Wires[0].ToParam);
      // the control: one port's two names together, never a mix of two ports
      Assert.False(ParseCircleWire("Radius (P)", Circle, out _, out e));
      Assert.Contains("No input named \"Radius (P)\"", e.Message);
    }

    [Fact]
    public void U41_The_refusal_lists_BOTH_forms()
    {
      Assert.False(ParseCircleWire("Diameter", Circle, out _, out var e));
      Assert.Contains("No input named \"Diameter\"", e.Message);
      var available = ((JsonArray)e.Detail["available"]).Select(n => (string)n).ToArray();
      Assert.Equal(new[] { "P (Plane)", "R (Radius)" }, available);
    }

    [Fact]
    public void U41_A_shape_without_full_names_behaves_exactly_as_before()
    {
      var labelsOnly = new PortShape { Inputs = new[] { "P", "R" }, Outputs = new string[0] };
      Assert.False(ParseCircleWire("Radius", labelsOnly, out _, out var e));
      Assert.Equal(new[] { "P", "R" }, ((JsonArray)e.Detail["available"]).Select(n => (string)n).ToArray());
      Assert.True(ParseCircleWire("R", labelsOnly, out _, out _));
    }
  }
}
