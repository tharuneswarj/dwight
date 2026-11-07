using System;
using System.Linq;
using System.Text.Json.Nodes;
using Dwight.Agent;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// U18: ONE PUBLISHED DEFINITION OF A COMPONENT DECLARATION, held by a test rather than by a promise.
  ///
  /// build_region publishes its components and wires inline, in a file another lane owns; request_mutation's
  /// `place` publishes the same shape through <see cref="BuildSpecSchema"/>. Both are parsed by one
  /// definition already, so if the two PUBLISHED schemas drift, the model is told two different things about
  /// fields that are handled identically. This test is what makes that drift go red, by name, without this
  /// lane ever editing the catalogue.
  ///
  /// THE COMPARISON IS SEMANTIC: PARSED, AND ORDER-INSENSITIVE FOR OBJECT KEYS. #866 found three published
  /// schemas identical in meaning and different in key order. A byte comparison would go red on key order
  /// alone, and a test that fails on spelling rather than meaning teaches whoever meets it to ignore it.
  /// Arrays stay order-SENSITIVE - treating them as sets would hide real differences behind a comparison
  /// that is too forgiving, and too forgiving is the worse failure for a drift test.
  ///
  /// A KNOWN FALSE RED, STATED SO IT CANNOT BE MISREAD (#843: say where a hazard bites). In JSON Schema some
  /// arrays are SETS - `required` and `enum` mean the same thing in any order - while others are not
  /// (`prefixItems`). So if someone REORDERS a `required` or `enum` list on one side only, this test goes red
  /// although the two schemas still agree. IF A RED HERE NAMES A DIFFERENCE ONLY IN THE ORDER OF A `required`
  /// OR `enum` ARRAY, THE SCHEMAS HAVE NOT DIVERGED: put the two lists back in the same order. Any other red
  /// is real drift. The failure is acceptable; a reader unable to tell it from drift would not be.
  ///
  /// AND THE COMPARER HAS CONTROLS IN BOTH DIRECTIONS (#212), because a semantic comparer is exactly the kind
  /// of instrument that can be wrong in the flattering direction: one that always answers "equal" would pass
  /// every drift test while measuring nothing.
  /// </summary>
  public class BuildSpecSchemaTests
  {
    // ---- the comparer, and proof that it measures ---------------------------------------------------

    /// <summary>Same meaning: every object has the same key SET with semantically equal values; arrays match
    /// position by position; leaves match by kind and value.</summary>
    internal static bool SameMeaning(JsonNode a, JsonNode b)
    {
      if (a == null || b == null) return a == null && b == null;

      if (a is JsonObject oa && b is JsonObject ob)
      {
        if (oa.Count != ob.Count) return false;
        foreach (var kv in oa)
        {
          if (!ob.TryGetPropertyValue(kv.Key, out var other)) return false;
          if (!SameMeaning(kv.Value, other)) return false;
        }
        return true;
      }

      if (a is JsonArray aa && b is JsonArray ab)
      {
        if (aa.Count != ab.Count) return false;
        for (var i = 0; i < aa.Count; i++)
          if (!SameMeaning(aa[i], ab[i])) return false;
        return true;
      }

      if (a is JsonValue va && b is JsonValue vb)
        return va.ToJsonString() == vb.ToJsonString();

      return false;   // an object against an array, and so on
    }

    /// <summary>THE NEGATIVE-DIRECTION CONTROL: a comparer that always answered "equal" would pass every drift
    /// test. So a planted extra key, a changed value, and a reordered array must all read as DIFFERENT.</summary>
    [Fact]
    public void The_comparer_can_see_a_difference()
    {
      var baseline = BuildSpecSchema.Components();

      var planted = BuildSpecSchema.Components();
      planted["items"]["properties"]["planted"] = Sch.Str("a key that is not in the other");
      Assert.False(SameMeaning(baseline, planted));

      var changed = BuildSpecSchema.Components();
      changed["items"]["properties"]["type"]["description"] = "a different description";
      Assert.False(SameMeaning(baseline, changed));

      var reordered = BuildSpecSchema.Components();
      reordered["items"]["required"] = new JsonArray("type", "ref");
      Assert.False(SameMeaning(baseline, reordered));
    }

    /// <summary>THE POSITIVE-DIRECTION CONTROL: the whole reason for a semantic comparer is that key ORDER
    /// does not change meaning, so the same keys in a different order must read as EQUAL.</summary>
    [Fact]
    public void The_comparer_ignores_object_key_order()
    {
      var forwards = new JsonObject { ["type"] = "string", ["description"] = "x" };
      var backwards = new JsonObject { ["description"] = "x", ["type"] = "string" };

      Assert.NotEqual(forwards.ToJsonString(), backwards.ToJsonString());   // different spelling...
      Assert.True(SameMeaning(forwards, backwards));                        // ...same meaning
    }

    // ---- the property it exists for -----------------------------------------------------------------

    private static JsonObject BuildRegionProperty(string name)
    {
      var schema = (JsonObject)JsonNode.Parse(AgentToolCatalog.SchemaOf(AgentToolCatalog.BuildRegion));
      return (JsonObject)schema["properties"][name];
    }

    /// <summary>
    /// build_region's published components and the shared helper's mean the same thing. If this goes red,
    /// the model is being told two different things about fields BuildSpecParser handles identically - fix
    /// whichever side drifted, and consider switching build_region to call the helper so there is only one.
    /// </summary>
    [Fact]
    public void Build_regions_components_mean_what_the_shared_declaration_means()
    {
      Assert.True(SameMeaning(BuildRegionProperty("components"), BuildSpecSchema.Components()),
        "build_region's published `components` has drifted from BuildSpecSchema.Components(). " +
        "Both are parsed by BuildSpecParser, so the model is now told two different things about one shape.");
    }

    [Fact]
    public void Build_regions_wires_mean_what_the_shared_declaration_means()
    {
      Assert.True(SameMeaning(BuildRegionProperty("wires"), BuildSpecSchema.Wires()),
        "build_region's published `wires` has drifted from BuildSpecSchema.Wires().");
    }

    // ---- `place`, the carrier, published from the same declaration ---------------------------------

    private static JsonObject PlaceProperty()
    {
      var schema = (JsonObject)JsonNode.Parse(
        AgentHostTools.All().First(t => t.Name == AgentHostTools.RequestMutation).InputSchemaJson);
      return (JsonObject)schema["properties"]["place"];
    }

    /// <summary>
    /// THE CARRIER PUBLISHES THE SAME DECLARATION build_region DOES. Both parse through BuildSpecParser, so
    /// the model must be told one thing about the shape - and it is, because `place` is BUILT from the shared
    /// helper rather than written a second time.
    /// </summary>
    [Fact]
    public void Place_publishes_the_shared_declaration()
    {
      var place = PlaceProperty();

      Assert.True(SameMeaning(place["properties"]["components"], BuildSpecSchema.Components()));
      Assert.True(SameMeaning(place["properties"]["wires"], BuildSpecSchema.Wires()));
      Assert.Equal(new[] { "components" }, place["required"].AsArray().Select(n => n.GetValue<string>()).ToArray());
    }

    /// <summary>The model may not name its own address, and a fresh placement has nothing to cut - so the
    /// published carrier offers neither field. A schema that advertised them would invite exactly the
    /// arguments the ask refuses.</summary>
    [Fact]
    public void A_declared_input_is_a_name_with_its_access_beside_it_and_the_schema_gives_the_rule()
    {
      var item = PlaceProperty()["properties"]["components"]["items"]["properties"]["inputs"]["items"];

      Assert.Equal("string", item["properties"]["name"]["type"].GetValue<string>());
      Assert.Equal(new[] { "item", "list", "tree" }, item["properties"]["access"]["enum"].AsArray().Select(w => w.GetValue<string>()));
      Assert.Contains("a whole list of G-code lines would run once per line", item["properties"]["access"]["description"].GetValue<string>());
      Assert.Equal(new[] { "name" }, item["required"].AsArray().Select(w => w.GetValue<string>()));
    }

    [Fact]
    public void Place_offers_a_region_that_exists_and_no_reconnect()
    {
      var properties = PlaceProperty()["properties"].AsObject();

      Assert.Contains("ALREADY on the canvas", properties["region"]["description"].GetValue<string>());   // Brief CM: never its own address, which the plugin mints
      Assert.False(properties.ContainsKey("reconnect"));
    }

    /// <summary>
    /// #200/#202: THE PUBLISHED DESCRIPTOR IS THE CONTRACT, SO IT IS WHAT THE PARSER GETS TESTED AGAINST. An
    /// example written to the published shape must parse through the build's own parser - if the schema
    /// advertised a field the parser rejected (or demanded one the schema called optional), this goes red.
    /// </summary>
    [Fact]
    public void An_example_written_to_the_published_shape_parses()
    {
      var example = (JsonObject)JsonNode.Parse(
        @"{""components"":[{""ref"":""s"",""type"":""Number Slider"",""nick"":""Count"",
            ""state"":{""min"":0,""max"":10,""value"":4,""rounding"":""integer""}}],
           ""wires"":[]}");

      Assert.True(BuildSpecParser.TryParse(example, t => true, t => new string[0], id => IdState.Alive,
        out var spec, out var error), error?.ToJson());
      Assert.Single(spec.Components);
    }

    /// <summary>Each call must build FRESH nodes - a JsonNode has one parent, so a shared instance would be
    /// one node with two owners, and editing one tool's schema would silently edit the other's.</summary>
    [Fact]
    public void Each_call_builds_its_own_nodes()
    {
      var first = BuildSpecSchema.Components();
      var second = BuildSpecSchema.Components();

      Assert.NotSame(first, second);
      first["items"]["properties"]["type"]["description"] = "edited";
      Assert.NotEqual("edited", second["items"]["properties"]["type"]["description"].GetValue<string>());
    }
  }
}
