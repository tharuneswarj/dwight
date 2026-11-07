using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using Dwight.Agent;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// The published schemas, ENUMERATED (#823 Part 2).
  ///
  /// <para>THE COUNT IS PART OF THE UNIT. Before this test existed, two people counted these schemas and
  /// got two different wrong answers - one counted identifier occurrences (each catalogue's
  /// <c>SchemaOf</c> accessor read as an extra schema), the other counted a grep pattern that meant
  /// something else again. <b>Neither was counting schemas; both were counting text that mentions
  /// schemas.</b> So the count here is taken by walking <c>All()</c>, and a typed list would have
  /// preserved whichever wrong number we happened to agree on.</para>
  ///
  /// <para>Every assertion below runs over EVERY published descriptor from BOTH catalogues. The one
  /// schema a hand-written list would omit is exactly the one nobody checked.</para>
  /// </summary>
  public class PublishedSchemaTests
  {
    /// <summary>Every tool Dwight publishes, from both registries - the agent catalogue and the host
    /// (UI) tools. Enumerated, never listed.</summary>
    internal static IEnumerable<ToolDescriptor> Published() =>
      AgentToolCatalog.All().Concat(AgentHostTools.All());

    public static IEnumerable<object[]> EachTool() =>
      Published().Select(t => new object[] { t.Name });

    internal static ToolDescriptor ByName(string name) =>
      Published().First(t => t.Name == name);

    // ---- the count, from the code -----------------------------------------------------------------

    /// <summary>
    /// The count, pinned, so adding a tool is a deliberate act rather than a drift. Phase-9 U23 (lane C) added
    /// get_solve_timing, taking the catalogue from 21 to 22 and the total from 22 to 23; its second unit added
    /// get_document_units and get_installed_plugins, taking them to 24 and 25; its third added get_file_state and
    /// get_canvas_layout, taking them to 26 and 27; Brief EE added show_on_canvas, taking them to 27 and 28.
    ///
    /// THE NAME CHANGED WITH THE NUMBERS, and that is not tidiness: a test's name is a claim about our own
    /// behaviour (#837), and "twenty_two" above an assertion of 23 would be a green test whose title is false. A
    /// count in a test name means every added tool renames this test - so two lanes adding tools conflict HERE,
    /// loudly, which is the better way for them to find each other.
    /// </summary>
    [Fact]
    public void The_two_registries_publish_twenty_eight_schemas_between_them()
    {
      Assert.Equal(27, AgentToolCatalog.All().Count);
      Assert.Equal(1, AgentHostTools.All().Count);
      Assert.Equal(28, Published().Count());
    }

    [Fact]
    public void Every_published_tool_is_named_once()
    {
      var names = Published().Select(t => t.Name).ToList();
      Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
      Assert.All(names, n => Assert.False(string.IsNullOrWhiteSpace(n)));
    }

    // ---- what every one of them must be -----------------------------------------------------------

    /// <summary>The property is a <see cref="JsonObject"/> now, so "it parses" is not something that can
    /// fail - which is the point. What is still worth asserting is that each one is a SCHEMA: an object
    /// type, with a properties map, closed to anything it does not name.</summary>
    [Theory]
    [MemberData(nameof(EachTool))]
    public void Every_published_schema_is_a_closed_object_schema(string name)
    {
      var schema = ByName(name).InputSchema;

      Assert.NotNull(schema);
      Assert.Equal("object", schema["type"].GetValue<string>());
      Assert.IsType<JsonObject>(schema["properties"]);
      Assert.False(schema["additionalProperties"].GetValue<bool>());
    }

    /// <summary>Anthropic rejects <c>oneOf</c>/<c>allOf</c>/<c>anyOf</c> at the top level of an
    /// input_schema: one of them there 400s the WHOLE tool list, so no agent turn can run at all (#247
    /// F1 - it happened). A per-tool defect that takes down every tool is worth a standing check.</summary>
    [Theory]
    [MemberData(nameof(EachTool))]
    public void No_published_schema_uses_a_top_level_combinator(string name)
    {
      var schema = ByName(name).InputSchema;

      Assert.Null(schema["oneOf"]);
      Assert.Null(schema["allOf"]);
      Assert.Null(schema["anyOf"]);
    }

    /// <summary>Every name in <c>required</c> must be a property the schema actually declares. A
    /// required name with no property asks the model for a field it is then refused for sending.</summary>
    [Theory]
    [MemberData(nameof(EachTool))]
    public void Every_required_name_is_a_property_that_exists(string name)
    {
      var schema = ByName(name).InputSchema;
      var props = (JsonObject)schema["properties"];
      var required = schema["required"]?.AsArray();
      if (required == null) return;

      foreach (var r in required)
      {
        var field = r.GetValue<string>();
        Assert.True(props.ContainsKey(field), name + " requires \"" + field + "\" but declares no such property");
      }
    }

    /// <summary>The string face is DERIVED, so it must agree with the object it is derived from - and it
    /// is what the transport, the MCP face and the external deploy probe all read.</summary>
    [Theory]
    [MemberData(nameof(EachTool))]
    public void The_json_face_round_trips_to_the_same_schema(string name)
    {
      var t = ByName(name);

      Assert.Equal(t.InputSchema.ToJsonString(), JsonNode.Parse(t.InputSchemaJson).ToJsonString());
    }

    [Fact]
    public void SchemaOf_answers_for_every_tool_in_its_own_registry()
    {
      foreach (var t in AgentToolCatalog.All())
        Assert.False(string.IsNullOrWhiteSpace(AgentToolCatalog.SchemaOf(t.Name)));
      foreach (var t in AgentHostTools.All())
        Assert.False(string.IsNullOrWhiteSpace(AgentHostTools.SchemaOf(t.Name)));
    }

    // ---- the defect this unit removes -------------------------------------------------------------

    /// <summary>
    /// THE ONE THAT MATTERS. <c>request_mutation</c> is the only schema composed at RUNTIME, and it is
    /// the one #820 broke: <c>edit_script</c>'s kind description names its claims as "preserves" and
    /// "changes", and as a hand-quoted string that raw quotation mark closed the description and made the
    /// whole published descriptor unparseable.
    ///
    /// <para>Built as an object there is nothing to escape by hand - serialisation escapes at the
    /// destination (#335). This asserts the quotes SURVIVE, properly escaped, in the published JSON: the
    /// value that broke it is now carried correctly rather than avoided.</para>
    /// </summary>
    [Fact]
    public void The_runtime_composed_schema_carries_quotation_marks_safely()
    {
      var published = ByName(AgentHostTools.RequestMutation).InputSchemaJson;

      // It parses - which is what stopped being true when a computed quote arrived.
      var reparsed = JsonNode.Parse(published);
      var claim = reparsed["properties"]["claim"]["description"].GetValue<string>();

      // ... and the quotation marks are really there, as characters, not stripped to make it parse.
      Assert.Contains("\"preserves\"", claim);
      Assert.Contains("\"changes\"", claim);
    }

    /// <summary>A description built from the kind table reaches the schema whole. The table's text is
    /// also the master prompt's text, where escaping would be wrong - so the schema escapes at its own
    /// edge and nothing upstream has to know (#335).</summary>
    [Fact]
    public void A_kind_description_containing_a_quote_cannot_break_the_descriptor()
    {
      var kind = ByName(AgentHostTools.RequestMutation).InputSchema["properties"]["kind"];

      var description = kind["description"].GetValue<string>();
      Assert.Contains("edit_script", description);
      // The published form must still parse with that text inside it.
      Assert.NotNull(JsonNode.Parse(ByName(AgentHostTools.RequestMutation).InputSchemaJson));
    }

    // ---- the migration instrument, kept ------------------------------------------------------------

    /// <summary>
    /// Dumps every published schema, normalised, when <c>DWIGHT_SCHEMA_DUMP</c> names a file. Inert
    /// otherwise, so it costs nothing in CI.
    ///
    /// <para>It exists because this conversion's whole claim was that it changed HOW schemas are built
    /// and not WHAT they publish - and that is checkable rather than assertable: dump before, dump after,
    /// compare per tool. Kept rather than deleted, because the next person to touch a schema believing
    /// the change is cosmetic will want exactly this.</para>
    /// </summary>
    [Fact]
    public void Dump_every_published_schema_when_asked()
    {
      var path = Environment.GetEnvironmentVariable("DWIGHT_SCHEMA_DUMP");
      if (string.IsNullOrEmpty(path)) return;

      var sb = new StringBuilder();
      var n = 0;
      foreach (var t in Published().OrderBy(x => x.Name, StringComparer.Ordinal))
      {
        n++;
        sb.Append(t.Name).Append('\t').Append(JsonNode.Parse(t.InputSchemaJson).ToJsonString()).Append('\n');
      }
      sb.Append("COUNT\t").Append(n).Append('\n');
      File.WriteAllText(path, sb.ToString());
    }
  }
}
