using System;
using System.Text.Json.Nodes;
using Dwight.Agent;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// Phase-9 U2 (the geometry read set, #381 R3, study §6): how every geometry tool ADDRESSES geometry.
  ///
  /// One target shape for all seven tools - {id, param?, skip?, limit?} - and one resolution rule for the
  /// output port, which is #208's arity rule as build_region already keeps it: the plugin answers wherever
  /// the answer is forced (a param has one port; a single-output component has one), a name is required
  /// only where it is a genuine CHOICE, and there it must MATCH or is refused with the real list. Never a
  /// silent fallback to output[0] beneath a report.
  /// </summary>
  public class GeometryArgsTests
  {
    private static JsonObject Args(string json) => (JsonObject)JsonNode.Parse(json);

    private static AgentNode Param() =>
      new AgentNode { Id = 1, Type = "Mesh", Kind = "param", Outputs = new[] { "M" } };
    private static AgentNode OneOutput() =>
      new AgentNode { Id = 2, Type = "Mesh Box", Kind = "component", Outputs = new[] { "Mesh" } };
    private static AgentNode TwoOutputs() =>
      new AgentNode { Id = 3, Type = "Divide Curve", Kind = "component", Outputs = new[] { "Points", "Tangents" } };

    // ---- the target ---------------------------------------------------------------------------------

    [Fact]
    public void ID_IS_REQUIRED_AND_EVERYTHING_ELSE_DEFAULTS()
    {
      Assert.True(GeometryArgs.TryParseTarget(Args("{\"id\":4}"), out var t, out var err), err?.Message);
      Assert.Equal(4, t.Id);
      Assert.Null(t.Param);
      Assert.Equal(0, t.Skip);
      Assert.Equal(GeometryArgs.MaxObjects, t.Limit);

      Assert.False(GeometryArgs.TryParseTarget(Args("{}"), out _, out var missing));
      Assert.Equal(ToolErrors.InvalidArgs, missing.Code);
    }

    [Fact]
    public void A_LIMIT_OVER_THE_CAP_IS_REFUSED_NAMING_THE_CAP_NEVER_SILENTLY_CLAMPED()
    {
      // A clamp would let the model believe it asked for 200 and got 200 (#134).
      Assert.False(GeometryArgs.TryParseTarget(Args("{\"id\":4,\"limit\":200}"), out _, out var err));
      Assert.Equal(ToolErrors.InvalidArgs, err.Code);
      Assert.Contains(GeometryArgs.MaxObjects.ToString(), err.Message);

      Assert.False(GeometryArgs.TryParseTarget(Args("{\"id\":4,\"limit\":0}"), out _, out _));
      Assert.False(GeometryArgs.TryParseTarget(Args("{\"id\":4,\"skip\":-1}"), out _, out _));
      Assert.True(GeometryArgs.TryParseTarget(Args("{\"id\":4,\"skip\":25,\"limit\":5}"), out var t, out _));
      Assert.Equal(25, t.Skip);
      Assert.Equal(5, t.Limit);
    }

    // ---- the output port, by arity (#208) ---------------------------------------------------------------

    [Fact]
    public void A_PARAM_NEEDS_NO_NAME_AND_RESOLVES_TO_ITS_ONE_PORT()
    {
      Assert.True(GeometryArgs.TryResolveOutput(Param(), null, out var port, out _));
      Assert.Equal("M", port);
    }

    [Fact]
    public void A_SINGLE_OUTPUT_COMPONENT_NEEDS_NO_NAME()
    {
      Assert.True(GeometryArgs.TryResolveOutput(OneOutput(), null, out var port, out _));
      Assert.Equal("Mesh", port);
    }

    [Fact]
    public void A_MULTI_OUTPUT_COMPONENT_REQUIRES_A_NAME_AND_THE_REFUSAL_LISTS_THE_REAL_PORTS()
    {
      Assert.False(GeometryArgs.TryResolveOutput(TwoOutputs(), null, out _, out var err));
      Assert.Equal(ToolErrors.InvalidArgs, err.Code);
      Assert.Contains("Points", err.Detail["outputs"].ToJsonString());
      Assert.Contains("Tangents", err.Detail["outputs"].ToJsonString());
    }

    [Fact]
    public void A_GIVEN_NAME_MUST_MATCH_A_REAL_PORT_CASE_INSENSITIVELY_OR_IS_REFUSED()
    {
      Assert.True(GeometryArgs.TryResolveOutput(TwoOutputs(), "tangents", out var port, out _));
      Assert.Equal("Tangents", port);   // the canvas's own spelling, never the model's

      Assert.False(GeometryArgs.TryResolveOutput(TwoOutputs(), "Normals", out _, out var err));
      Assert.Equal(ToolErrors.InvalidArgs, err.Code);
      Assert.Contains("Normals", err.Message);
    }

    [Fact]
    public void A_NAME_GIVEN_WHERE_NONE_WAS_NEEDED_STILL_HAS_TO_BE_RIGHT()
    {
      // Fail-open on knowledge is allowed only where a later hard failure is guaranteed (#208); a wrong
      // name on a one-port node would otherwise resolve to a port the model did not name.
      Assert.True(GeometryArgs.TryResolveOutput(OneOutput(), "mesh", out var port, out _));
      Assert.Equal("Mesh", port);
      Assert.False(GeometryArgs.TryResolveOutput(OneOutput(), "Brep", out _, out var err));
      Assert.Equal(ToolErrors.InvalidArgs, err.Code);
    }

    [Fact]
    public void A_NODE_WITH_NO_OUTPUTS_AT_ALL_IS_REFUSED_HONESTLY()
    {
      var none = new AgentNode { Id = 9, Type = "Group", Kind = "component", Outputs = new string[0] };
      Assert.False(GeometryArgs.TryResolveOutput(none, null, out _, out var err));
      Assert.Equal(ToolErrors.InvalidArgs, err.Code);
      Assert.Contains("no output", err.Message, StringComparison.OrdinalIgnoreCase);
    }
  }
}
