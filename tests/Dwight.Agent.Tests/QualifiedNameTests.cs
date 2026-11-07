using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Dwight.Agent;
using Dwight.Components;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// U25 (#915): AN AMBIGUOUS COMPONENT NAME IS REFUSED WITH ITS QUALIFIED FORMS, AND THE QUALIFIED FORM IS ACCEPTED.
  ///
  /// The whitelist kept the first proxy per name, so "Rectangle" resolved to the PARAM (Params > Geometry) and never
  /// to the component (Curve > Primitive) - and the model, having no spelling that could reach it, told the user it
  /// was not installed. The fixture here is the measured collision set: Rectangle (param first), Line and Circle
  /// (component first, right by luck), beside unique names that must keep resolving bare exactly as before.
  /// </summary>
  public class QualifiedNameTests
  {
    private sealed class Proxy
    {
      public string Name; public string Category; public Sub Sub;
      public Proxy(string name, string category, string sub) { Name = name; Category = category; Sub = new Sub(sub); }
    }
    private sealed class Sub { public string Name; public Sub(string n) { Name = n; } }

    /// <summary>In Grasshopper's own enumeration order on the measured machine: the Rectangle param comes first.</summary>
    private static readonly Proxy[] Installed =
    {
      new Proxy("Rectangle", "Params", "Geometry"),
      new Proxy("Number Slider", "Params", "Input"),
      new Proxy("Line", "Curve", "Primitive"),
      new Proxy("Rectangle", "Curve", "Primitive"),
      new Proxy("Circle", "Curve", "Primitive"),
      new Proxy("Line", "Params", "Geometry"),
      new Proxy("Circle", "Params", "Geometry"),
      new Proxy("Loft", "Surface", "Freeform"),
    };

    private static NameResolution Resolve(string query, out Proxy match, out IReadOnlyList<string> forms) =>
      ComponentMatchCore.Resolve(query, Installed, p => p.Name, p => p.Category, p => p.Sub.Name, out match, out forms);

    // ---- the core decision ----------------------------------------------------------------------------------

    [Fact]
    public void A_unique_bare_name_resolves_exactly_as_before()
    {
      Assert.Equal(NameResolution.Found, Resolve("Loft", out var m, out _));
      Assert.Equal("Surface", m.Category);
      Assert.Equal(NameResolution.Found, Resolve("number slider", out var n, out _));   // normalised, the ADR-083 lesson
      Assert.Equal("Number Slider", n.Name);
    }

    [Fact]
    public void THE_MEASURED_COLLISIONS_are_ambiguous_and_list_their_qualified_forms_sorted()
    {
      Assert.Equal(NameResolution.Ambiguous, Resolve("Rectangle", out _, out var forms));
      Assert.Equal(new[] { "Curve > Primitive > Rectangle", "Params > Geometry > Rectangle" }, forms);

      Assert.Equal(NameResolution.Ambiguous, Resolve("Line", out _, out _));
      Assert.Equal(NameResolution.Ambiguous, Resolve("circle", out _, out _));   // case does not rescue it
    }

    [Fact]
    public void THE_QUALIFIED_FORM_resolves_to_exactly_the_one_it_names()
    {
      Assert.Equal(NameResolution.Found, Resolve("Curve > Primitive > Rectangle", out var component, out _));
      Assert.Equal("Curve", component.Category);

      Assert.Equal(NameResolution.Found, Resolve("Params > Geometry > Rectangle", out var param, out _));
      Assert.Equal("Params", param.Category);
    }

    [Fact]
    public void A_qualified_form_tolerates_spacing_and_case_but_not_a_wrong_category()
    {
      Assert.Equal(NameResolution.Found, Resolve("curve>primitive>rectangle", out var m, out _));
      Assert.Equal("Curve", m.Category);
      Assert.Equal(NameResolution.Unknown, Resolve("Surface > Primitive > Rectangle", out _, out _));
    }

    [Fact]
    public void An_unknown_name_is_unknown_not_ambiguous()
    {
      Assert.Equal(NameResolution.Unknown, Resolve("Rectangel", out _, out var forms));
      Assert.Empty(forms);
    }

    [Fact]
    public void The_separator_is_one_character_both_lanes_agree_on()
    {
      // ComponentWhitelist.QualifiedSeparator is a compile-time alias of this constant (GH-coupled, so not linked here).
      Assert.Equal(" > ", ComponentMatchCore.QualifiedSeparator);
      Assert.Equal("Curve > Primitive > Rectangle", ComponentMatchCore.Qualified("Curve", "Primitive", "Rectangle"));
      Assert.Equal("Rectangle", ComponentMatchCore.BareName("Curve > Primitive > Rectangle"));
      Assert.Equal("Loft", ComponentMatchCore.BareName("Loft"));
      Assert.False(ComponentMatchCore.IsQualified("A > B"));   // two parts is not a qualified name
    }

    // ---- through the build spec's parser --------------------------------------------------------------------

    private static bool Exists(string t) => Resolve(t, out _, out _) != NameResolution.Unknown;
    private static IReadOnlyList<string> Ambiguous(string t) => Resolve(t, out _, out var forms) == NameResolution.Ambiguous ? forms : null;

    private static bool Parse(string json, out BuildSpec spec, out ToolResult error) =>
      BuildSpecParser.TryParse((JsonObject)JsonNode.Parse(json), Exists, t => new string[0], id => IdState.Alive,
        null, null, Ambiguous, out spec, out error);

    [Fact]
    public void THE_PARSER_REFUSES_AN_AMBIGUOUS_BARE_NAME_WITH_THE_QUALIFIED_FORMS()
    {
      var ok = Parse(@"{""components"":[{""ref"":""r"",""type"":""Rectangle""}]}", out _, out var error);

      Assert.False(ok);
      Assert.Equal(ToolErrors.PreconditionRefused, error.Code);
      Assert.Equal("\"Rectangle\" names 2 installed components, so it does not say which one to place. Give the qualified " +
                   "form in \"type\" instead: \"Curve > Primitive > Rectangle\" or \"Params > Geometry > Rectangle\".", error.Message);
      Assert.Equal("ambiguous_component_type", error.Detail["reason"].GetValue<string>());
      Assert.Equal(new[] { "Curve > Primitive > Rectangle", "Params > Geometry > Rectangle" },
        error.Detail["qualifiedForms"].AsArray().Select(n => n.GetValue<string>()).ToArray());
    }

    [Fact]
    public void THE_PARSER_ACCEPTS_THE_QUALIFIED_FORM_IN_TYPE()
    {
      var ok = Parse(@"{""components"":[{""ref"":""r"",""type"":""Curve > Primitive > Rectangle""}]}", out var spec, out var error);
      Assert.True(ok, error?.Message);
      Assert.Equal("Curve > Primitive > Rectangle", spec.Components[0].Type);
    }

    [Fact]
    public void A_UNIQUE_BARE_NAME_STILL_PARSES_BARE()
    {
      Assert.True(Parse(@"{""components"":[{""ref"":""l"",""type"":""Loft""}]}", out var spec, out var error), error?.Message);
      Assert.Equal("Loft", spec.Components[0].Type);
    }

    [Fact]
    public void Without_an_ambiguity_resolver_the_parse_is_exactly_what_it_was()
    {
      // The six-argument overload passes null: not known is not a rule, so an ambiguous name is accepted here as before.
      Assert.True(BuildSpecParser.TryParse((JsonObject)JsonNode.Parse(@"{""components"":[{""ref"":""r"",""type"":""Rectangle""}]}"),
        Exists, t => new string[0], id => IdState.Alive, out _, out _));
    }

    // ---- the spot card and its record ---------------------------------------------------------------------------

    [Fact]
    public void A_spot_record_keeps_the_qualified_type_and_the_card_shows_the_bare_name()
    {
      var resolvers = new SpecResolvers
      {
        TypeExists = Exists, Nearest = t => new string[0], IdState = id => IdState.Alive, AmbiguousForms = Ambiguous,
        PortsOfType = t => new PortShape { Inputs = new string[0], Outputs = new string[0] },
        CanonicalType = t => Resolve(t, out var m, out _) == NameResolution.Found
          ? (Installed.Count(p => p.Name == m.Name) > 1 ? ComponentMatchCore.Qualified(m.Category, m.Sub.Name, m.Name) : m.Name)
          : null,
      };
      var seed = new HandoffSeed { Place = (JsonObject)JsonNode.Parse(@"{""components"":[{""ref"":""r"",""type"":""Curve > Primitive > Rectangle""}]}") };

      Assert.True(SpotAsk.Validate(seed, resolvers, id => null, out var ask, out var error), error?.Message);

      Assert.Equal("Curve > Primitive > Rectangle", ask.Record["components"][0]["type"].GetValue<string>());
      Assert.Equal(new[] { "a Rectangle" }, ask.Placement.Components.ToArray());
      // And the record parses back through the same resolvers - the click will not meet the refusal the ask avoided.
      Assert.True(resolvers.Parse(ask.Record, out _, out var back), back?.Message);
    }

    [Fact]
    public void A_spot_ask_refuses_the_ambiguous_bare_name_before_a_card_exists()
    {
      var resolvers = new SpecResolvers
      {
        TypeExists = Exists, Nearest = t => new string[0], IdState = id => IdState.Alive, AmbiguousForms = Ambiguous,
        PortsOfType = t => new PortShape { Inputs = new string[0], Outputs = new string[0] },
      };
      var seed = new HandoffSeed { Place = (JsonObject)JsonNode.Parse(@"{""components"":[{""ref"":""r"",""type"":""Rectangle""}]}") };

      Assert.False(SpotAsk.Validate(seed, resolvers, id => null, out _, out var error));
      Assert.Contains("Curve > Primitive > Rectangle", error.Message);
    }

    // ---- the repair payload suggests names that will resolve ------------------------------------------------------

    [Fact]
    public void Nearest_ranks_by_bare_name_and_offers_the_qualified_forms_where_the_bare_one_would_be_refused()
    {
      // A typo must still find its target - which ranking over the qualified strings cannot do (the first cut of this
      // test proved it: "Rectangel" is nowhere near "Curve > Primitive > Rectangle" by edit distance).
      var near = ComponentMatchCore.NearestCanonical("Rectangel", Installed, p => p.Name, p => p.Category, p => p.Sub.Name);

      Assert.Contains("Curve > Primitive > Rectangle", near);
      Assert.Contains("Params > Geometry > Rectangle", near);
      Assert.DoesNotContain("Rectangle", near);

      // A unique name is offered bare, exactly as before.
      Assert.Equal("Loft", ComponentMatchCore.NearestCanonical("Lofft", Installed, p => p.Name, p => p.Category, p => p.Sub.Name)[0]);
    }
  }
}
