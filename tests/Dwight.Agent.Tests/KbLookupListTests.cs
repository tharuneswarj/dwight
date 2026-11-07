using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Dwight.Agent;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// U58: kb_lookup takes a list. The call is driven through the GH-free <see cref="KbLookupReport.Run"/>, and the
  /// arguments are the ones the PUBLISHED descriptor advertises (#217: the descriptor is what the parser is tested against).
  /// </summary>
  public class KbLookupListTests
  {
    private static Guid G(int n) { var b = new byte[16]; b[0] = (byte)n; return new Guid(b); }

    private static List<InstalledType> Installed() => new List<InstalledType>
    {
      new InstalledType { Name = "Rectangle", Category = "Params", SubCategory = "Geometry", Description = "Contains rectangles.", Guid = G(1) },
      new InstalledType { Name = "Rectangle", Category = "Curve", SubCategory = "Primitive", Description = "Create a rectangle.", Guid = G(2) },
      new InstalledType { Name = "Loft", Category = "Surface", SubCategory = "Freeform", Description = "Create a lofted surface.", Guid = G(5) },
      new InstalledType { Name = "Line", Category = "Curve", SubCategory = "Primitive", Description = "Create a line.", Guid = G(3) },
    };

    private static ToolResult Run(string json) =>
      KbLookupReport.Run((JsonObject)JsonNode.Parse(json), Installed(), _ => new[] { "Loft", "Line" }, _ => null);

    private static string Names(int n) => "[" + string.Join(",", Enumerable.Range(0, n).Select(i => "\"Loft\"")) + "]";

    [Fact]
    public void The_published_descriptor_advertises_types_as_an_array_and_requires_neither_field_alone()
    {
      var d = AgentToolCatalog.All().Single(t => t.Name == AgentToolCatalog.KbLookup);
      var schema = (JsonObject)JsonNode.Parse(d.InputSchemaJson);
      Assert.Equal("array", schema["properties"]["types"]["type"].GetValue<string>());
      Assert.NotNull(schema["properties"]["type"]);
      Assert.True(schema["required"] == null || schema["required"].AsArray().Count == 0);
      Assert.Contains("send them all in `types` in ONE call rather than one call each", d.Description);

      var build = AgentToolCatalog.All().Single(t => t.Name == AgentToolCatalog.BuildRegion);
      Assert.Contains("(check them all with one kb_lookup call)", build.Description);
    }

    [Fact]
    public void Three_names_in_types_return_three_rows_in_the_order_given()
    {
      var r = Run("{\"types\":[\"Loft\",\"Lofr\",\"Rectangle\"]}");
      Assert.True(r.Ok, r.Message);
      var rows = r.Result["results"].AsArray();
      Assert.Equal(3, rows.Count);

      // found: exactly what a single lookup returns
      Assert.Equal("Surface > Freeform > Loft", rows[0]["qualified"].GetValue<string>());
      Assert.Equal(Run("{\"type\":\"Loft\"}").Result.ToJsonString(), rows[0].ToJsonString());

      // unknown: names the asked type and the nearest installed ones
      Assert.Equal("Lofr", rows[1]["asked"].GetValue<string>());
      Assert.False(rows[1]["found"].GetValue<bool>());
      Assert.Equal(new[] { "Loft", "Line" }, rows[1]["nearestInstalled"].AsArray().Select(n => n.GetValue<string>()).ToArray());

      // ambiguous: every match
      Assert.True(rows[2]["ambiguous"].GetValue<bool>());
      Assert.Equal(2, rows[2]["candidates"].AsArray().Count);
    }

    [Fact]
    public void The_single_type_call_is_unchanged()
    {
      var r = Run("{\"type\":\"Loft\"}");
      Assert.True(r.Ok);
      Assert.Equal("Loft", r.Result["name"].GetValue<string>());
      Assert.Null(r.Result["results"]);
    }

    [Fact]
    public void Neither_field_is_refused_with_a_message_naming_both()
    {
      var r = Run("{}");
      Assert.False(r.Ok);
      Assert.Equal(ToolErrors.InvalidArgs, r.Code);
      Assert.Contains("\"type\"", r.Message);
      Assert.Contains("\"types\"", r.Message);
      Assert.False(Run("{\"types\":[]}").Ok);
    }

    [Fact]
    public void Both_fields_more_than_25_names_and_a_blank_entry_are_refused()
    {
      Assert.False(Run("{\"type\":\"Loft\",\"types\":[\"Line\"]}").Ok);
      var r = Run("{\"types\":" + Names(26) + "}");
      Assert.False(r.Ok);
      Assert.Contains("25", r.Message);
      Assert.True(Run("{\"types\":" + Names(25) + "}").Ok);
      Assert.False(Run("{\"types\":[\"Loft\",\"  \"]}").Ok);
      Assert.False(Run("{\"types\":[\"Loft\",3]}").Ok);
    }
  }
}
