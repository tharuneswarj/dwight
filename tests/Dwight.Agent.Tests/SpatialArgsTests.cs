using System.Text.Json.Nodes;
using Dwight.Agent;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// Phase-9 U2: spatial_query's own arguments. One of three query shapes - near a point, within a box,
  /// overlapping another object - is required, and the schema cannot say "one of" at the top level
  /// (Anthropic rejects anyOf there, #247 F1), so the rule lives in the parser and is tested here.
  /// </summary>
  public class SpatialArgsTests
  {
    private static JsonObject Args(string json) => (JsonObject)JsonNode.Parse(json);

    [Fact]
    public void NEAR_TAKES_A_POINT_OF_EXACTLY_THREE_NUMBERS()
    {
      Assert.True(GeometryArgs.TryParseSpatial(Args("{\"id\":1,\"near\":[1,2,3]}"), out var q, out var err), err?.Message);
      Assert.Equal("near", q.Kind);
      Assert.Equal(3.0, q.Point[2]);

      Assert.False(GeometryArgs.TryParseSpatial(Args("{\"id\":1,\"near\":[1,2]}"), out _, out var bad));
      Assert.Equal(ToolErrors.InvalidArgs, bad.Code);
      Assert.Contains("three numbers", bad.Message);
    }

    [Fact]
    public void WITHIN_TAKES_A_BOX_AS_MIN_AND_MAX_CORNERS()
    {
      Assert.True(GeometryArgs.TryParseSpatial(Args("{\"id\":1,\"within\":{\"min\":[0,0,0],\"max\":[1,1,1]}}"), out var q, out _));
      Assert.Equal("within", q.Kind);
      Assert.Equal(1.0, q.BoxMax[0]);

      Assert.False(GeometryArgs.TryParseSpatial(Args("{\"id\":1,\"within\":{\"min\":[0,0,0]}}"), out _, out var bad));
      Assert.Equal(ToolErrors.InvalidArgs, bad.Code);
    }

    [Fact]
    public void OVERLAPPING_NAMES_ANOTHER_OBJECT_BY_ID_PORT_AND_INDEX()
    {
      Assert.True(GeometryArgs.TryParseSpatial(Args("{\"id\":1,\"overlapping\":{\"id\":9,\"param\":\"Brep\",\"index\":2}}"), out var q, out _));
      Assert.Equal("overlapping", q.Kind);
      Assert.Equal(9, q.OtherId);
      Assert.Equal("Brep", q.OtherParam);
      Assert.Equal(2, q.OtherIndex);

      // index defaults to 0 - the first object on that output - and is said to in the descriptor.
      Assert.True(GeometryArgs.TryParseSpatial(Args("{\"id\":1,\"overlapping\":{\"id\":9}}"), out var q0, out _));
      Assert.Equal(0, q0.OtherIndex);
    }

    [Fact]
    public void EXACTLY_ONE_QUERY_SHAPE_IS_REQUIRED()
    {
      Assert.False(GeometryArgs.TryParseSpatial(Args("{\"id\":1}"), out _, out var none));
      Assert.Equal(ToolErrors.InvalidArgs, none.Code);
      Assert.Contains("near", none.Message);
      Assert.Contains("within", none.Message);
      Assert.Contains("overlapping", none.Message);

      Assert.False(GeometryArgs.TryParseSpatial(Args("{\"id\":1,\"near\":[0,0,0],\"within\":{\"min\":[0,0,0],\"max\":[1,1,1]}}"), out _, out var two));
      Assert.Equal(ToolErrors.InvalidArgs, two.Code);
    }

    [Fact]
    public void THE_TARGET_RULES_STILL_APPLY_ID_REQUIRED_LIMIT_CAPPED()
    {
      Assert.False(GeometryArgs.TryParseSpatial(Args("{\"near\":[0,0,0]}"), out _, out var noId));
      Assert.Equal(ToolErrors.InvalidArgs, noId.Code);

      Assert.False(GeometryArgs.TryParseSpatial(Args("{\"id\":1,\"near\":[0,0,0],\"limit\":999}"), out _, out var big));
      Assert.Contains(GeometryArgs.MaxObjects.ToString(), big.Message);

      Assert.True(GeometryArgs.TryParseSpatial(Args("{\"id\":1,\"near\":[0,0,0],\"limit\":3}"), out var q, out _));
      Assert.Equal(3, q.Target.Limit);
    }
  }
}
