using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Dwight.Agent;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// U25 (#915), the reads' half. On a stock Rhino 8 the bare names Rectangle, Line and Circle each belong to two
  /// non-obsolete proxies; the whitelist's name index keeps whichever came first, and a build of "Rectangle" placed
  /// the PARAM. These tests hold the GH-free decision: which names are ambiguous, how the qualified form reads and
  /// parses, how a type argument resolves, and what kb_lookup says in each case.
  /// </summary>
  public class QualifiedNamesTests
  {
    private static Guid G(int n) { var b = new byte[16]; b[0] = (byte)n; return new Guid(b); }

    private static List<InstalledType> Installed() => new List<InstalledType>
    {
      new InstalledType { Name = "Rectangle", Category = "Params", SubCategory = "Geometry", Description = "Contains a collection of rectangles.", Guid = G(1) },
      new InstalledType { Name = "Rectangle", Category = "Curve", SubCategory = "Primitive", Description = "Create a rectangle on a plane.", Guid = G(2) },
      new InstalledType { Name = "Line", Category = "Curve", SubCategory = "Primitive", Description = "Create a line between two points.", Guid = G(3) },
      new InstalledType { Name = "Line", Category = "Params", SubCategory = "Geometry", Description = "Contains a collection of lines.", Guid = G(4) },
      new InstalledType { Name = "Loft", Category = "Surface", SubCategory = "Freeform", Description = "Create a lofted surface.", Guid = G(5) },
      // Two user objects with the same name in the same drawer: the qualified form could not tell them apart either,
      // so the name is NOT ambiguous in the sense this unit means - the whitelist's first-wins is all there is.
      new InstalledType { Name = "Twin", Category = "User", SubCategory = "Mine", Description = "a", Guid = G(6) },
      new InstalledType { Name = "Twin", Category = "User", SubCategory = "Mine", Description = "b", Guid = G(7) },
    };

    [Fact]
    public void Ambiguous_names_are_those_with_more_than_one_distinct_qualified_form()
    {
      var a = QualifiedNames.AmbiguousNames(Installed());
      Assert.Contains("Rectangle", a);
      Assert.Contains("Line", a);
      Assert.Contains("rectangle", a);      // case-insensitive, as the whitelist's name index is
      Assert.DoesNotContain("Loft", a);
      Assert.DoesNotContain("Twin", a);     // same drawer twice: not disambiguable, so not flagged
    }

    [Fact]
    public void The_qualified_form_is_category_subcategory_name_and_parses_back_in_any_spacing()
    {
      Assert.Equal("Curve > Primitive > Rectangle", QualifiedNames.Format("Curve", "Primitive", "Rectangle"));
      Assert.True(QualifiedNames.TryParse("Curve > Primitive > Rectangle", out var c, out var s, out var n));
      Assert.Equal(("Curve", "Primitive", "Rectangle"), (c, s, n));
      Assert.True(QualifiedNames.TryParse("Curve>Primitive>Rectangle", out c, out s, out n));
      Assert.Equal(("Curve", "Primitive", "Rectangle"), (c, s, n));
      Assert.False(QualifiedNames.TryParse("Rectangle", out _, out _, out _));
      Assert.False(QualifiedNames.TryParse("Curve > Rectangle", out _, out _, out _));         // two parts is not the form
      Assert.False(QualifiedNames.TryParse("Curve > > Rectangle", out _, out _, out _));       // an empty part is not either
      Assert.False(QualifiedNames.TryParse("", out _, out _, out _));
    }

    [Fact]
    public void QualifiedIfAmbiguous_qualifies_only_the_ambiguous()
    {
      var all = Installed();
      var a = QualifiedNames.AmbiguousNames(all);
      Assert.Equal("Params > Geometry > Rectangle", QualifiedNames.QualifiedIfAmbiguous(all[0], a));
      Assert.Null(QualifiedNames.QualifiedIfAmbiguous(all[4], a));    // Loft
      Assert.Null(QualifiedNames.QualifiedIfAmbiguous(null, a));
    }

    [Fact]
    public void A_unique_bare_name_resolves_to_one_exactly_as_before()
    {
      var r = QualifiedNames.Resolve("Loft", Installed());
      Assert.Equal(QualifiedNames.Outcome.One, r.Outcome);
      Assert.False(r.WasQualified);
      Assert.Equal(G(5), r.Matches.Single().Guid);
      // The whitelist's own normalisation still applies to a bare name (ADR-083's lesson).
      Assert.Equal(QualifiedNames.Outcome.One, QualifiedNames.Resolve("  loft ", Installed()).Outcome);
    }

    [Fact]
    public void An_ambiguous_bare_name_resolves_to_every_candidate_ordered_by_qualified_form()
    {
      var r = QualifiedNames.Resolve("Rectangle", Installed());
      Assert.Equal(QualifiedNames.Outcome.Ambiguous, r.Outcome);
      Assert.Equal(new[] { "Curve > Primitive > Rectangle", "Params > Geometry > Rectangle" }, r.Matches.Select(QualifiedNames.Format).ToArray());
    }

    [Fact]
    public void The_qualified_form_resolves_to_the_one_it_names()
    {
      var r = QualifiedNames.Resolve("Curve > Primitive > Rectangle", Installed());
      Assert.Equal(QualifiedNames.Outcome.One, r.Outcome);
      Assert.True(r.WasQualified);
      Assert.Equal(G(2), r.Matches.Single().Guid);

      Assert.Equal(G(1), QualifiedNames.Resolve("params>geometry>rectangle", Installed()).Matches.Single().Guid);   // case, spacing
    }

    [Fact]
    public void A_qualified_form_nothing_matches_is_none_and_remembers_it_was_qualified()
    {
      var r = QualifiedNames.Resolve("Surface > Freeform > Rectangle", Installed());
      Assert.Equal(QualifiedNames.Outcome.None, r.Outcome);
      Assert.True(r.WasQualified);
      Assert.Equal(QualifiedNames.Outcome.None, QualifiedNames.Resolve("Lofr", Installed()).Outcome);
      Assert.Equal(QualifiedNames.Outcome.None, QualifiedNames.Resolve("", Installed()).Outcome);
      Assert.Equal(QualifiedNames.Outcome.None, QualifiedNames.Resolve("Loft", null).Outcome);
    }

    [Fact]
    public void A_same_drawer_duplicate_resolves_to_one_first_wins()
    {
      var r = QualifiedNames.Resolve("Twin", Installed());
      Assert.Equal(QualifiedNames.Outcome.One, r.Outcome);
      Assert.Equal(G(6), r.Matches.Single().Guid);
    }

    // ---- kb_lookup's payload ----------------------------------------------------------------------------

    // ---- U62: the lookup rows carry port names AND nicknames ----------------------------------------------

    [Fact]
    public void U62_A_port_whose_nickname_differs_renders_Name_and_Nick()
    {
      Assert.Equal("Radius (R)", LivePorts.PortLabel("Radius", "R"));
      Assert.Equal("Plane (P)", LivePorts.PortLabel(" Plane ", " P "));
    }

    [Fact]
    public void U62_A_port_whose_nickname_matches_renders_the_bare_name()
    {
      Assert.Equal("Curve", LivePorts.PortLabel("Curve", "Curve"));
      Assert.Equal("Curve", LivePorts.PortLabel("Curve", "curve"));
      Assert.Equal("Radius", LivePorts.PortLabel("Radius", ""));
      Assert.Equal("R", LivePorts.PortLabel("", "R"));
      Assert.Equal(".", LivePorts.PortLabel(null, null));
    }

    [Fact]
    public void U62_The_published_inputs_and_outputs_carry_the_labels_it_was_given()
    {
      var live = new[]
      {
        new LiveObject
        {
          Guid = G(5), Name = "Loft",
          Inputs = new[] { LivePorts.PortLabel("Curves", "C"), LivePorts.PortLabel("Options", "Options") },
          Outputs = new[] { LivePorts.PortLabel("Loft", "L") },
        },
      };
      var info = Installed().First(t => t.Guid == G(5));
      var ports = LivePorts.Pick(live, info, false);
      Assert.NotNull(ports);
      Assert.Equal(new[] { "Curves (C)", "Options" }, ports["inputs"].AsArray().Select(n => (string)n).ToArray());
      Assert.Equal(new[] { "Loft (L)" }, ports["outputs"].AsArray().Select(n => (string)n).ToArray());
    }

    [Fact]
    public void Kb_lookup_on_a_unique_name_keeps_its_shape_and_adds_the_qualified_form()
    {
      var r = KbLookupReport.Build("Loft", QualifiedNames.Resolve("Loft", Installed()), null, _ => null);
      Assert.True(r.Ok);
      Assert.Equal("Loft", r.Result["name"].GetValue<string>());
      Assert.Equal("Surface > Freeform > Loft", r.Result["qualified"].GetValue<string>());
      Assert.Equal("Surface", r.Result["category"].GetValue<string>());
      Assert.True(r.Result["installed"].GetValue<bool>());
      Assert.Equal("unavailable", r.Result["ports"].GetValue<string>());   // stated, never guessed
      Assert.False(r.Result.AsObject().ContainsKey("ambiguous"));
    }

    [Fact]
    public void Kb_lookup_on_an_ambiguous_name_lists_each_candidate_with_its_qualified_form_and_says_so()
    {
      var ports = new Func<InstalledType, JsonObject>(t => t.Guid == G(2)
        ? new JsonObject { ["inputs"] = new JsonArray("P", "X", "Y", "R"), ["outputs"] = new JsonArray("R", "L"), ["readFrom"] = "an instance on this canvas" }
        : null);
      var r = KbLookupReport.Build("Rectangle", QualifiedNames.Resolve("Rectangle", Installed()), null, ports);
      Assert.True(r.Ok);
      Assert.True(r.Result["ambiguous"].GetValue<bool>());
      Assert.Equal("Rectangle", r.Result["asked"].GetValue<string>());
      var cands = r.Result["candidates"].AsArray();
      Assert.Equal(2, cands.Count);
      Assert.Equal("Curve > Primitive > Rectangle", cands[0]["qualified"].GetValue<string>());
      Assert.Equal(4, cands[0]["ports"]["inputs"].AsArray().Count);          // ports matched to THAT candidate
      Assert.Equal("Params > Geometry > Rectangle", cands[1]["qualified"].GetValue<string>());
      Assert.Equal("unavailable", cands[1]["ports"].GetValue<string>());
      var means = r.Result["means"].GetValue<string>();
      Assert.Contains("\"Rectangle\" is ambiguous", means);
      Assert.Contains("Category > SubCategory > Name", means);
      Assert.Contains("build_region", means);
    }

    [Fact]
    public void Kb_lookup_on_the_qualified_form_answers_for_that_one()
    {
      var r = KbLookupReport.Build("Params > Geometry > Rectangle", QualifiedNames.Resolve("Params > Geometry > Rectangle", Installed()), null, _ => null);
      Assert.True(r.Ok);
      Assert.Equal("Params", r.Result["category"].GetValue<string>());
      Assert.Equal("Params > Geometry > Rectangle", r.Result["qualified"].GetValue<string>());
    }

    [Fact]
    public void Kb_lookup_on_an_unknown_name_refuses_with_the_nearest_installed_and_a_qualified_miss_says_which_part_to_check()
    {
      var bare = KbLookupReport.Build("Lofr", QualifiedNames.Resolve("Lofr", Installed()), new[] { "Loft", "Line" }, _ => null);
      Assert.False(bare.Ok);
      Assert.Equal(ToolErrors.UnknownComponentType, bare.Code);
      Assert.Contains("No installed component is named \"Lofr\"", bare.Message);
      Assert.Equal(2, bare.Detail["nearestInstalled"].AsArray().Count);

      var qualified = KbLookupReport.Build("Surface > Freeform > Rectangle", QualifiedNames.Resolve("Surface > Freeform > Rectangle", Installed()), new string[0], _ => null);
      Assert.False(qualified.Ok);
      Assert.Equal(ToolErrors.UnknownComponentType, qualified.Code);
      Assert.Contains("qualified name", qualified.Message);
      Assert.Contains("category and sub-category", qualified.Message);
    }

    // ---- U39 (#975): a candidate reads its ports from ITS OWN instance ---------------------------------------------

    private static LiveObject RectangleComponent() => new LiveObject
    {
      Guid = G(2), Name = "Rectangle",
      Inputs = new[] { "P", "X", "Y", "R" }, Outputs = new[] { "R", "L" },
    };

    private static JsonObject Candidate(ToolResult r, string qualified) =>
      ((JsonArray)r.Result["candidates"]).Select(n => (JsonObject)n).Single(c => (string)c["qualified"] == qualified);

    [Fact]
    public void U39_The_param_candidate_is_not_handed_the_components_ports_when_only_the_component_is_on_the_canvas()
    {
      var live = new[] { RectangleComponent() };
      var r = KbLookupReport.Build("Rectangle", QualifiedNames.Resolve("Rectangle", Installed()), null,
        info => LivePorts.Pick(live, info, nameSharedByInstalledTypes: true));

      Assert.True(r.Ok);
      Assert.Equal("unavailable", (string)Candidate(r, "Params > Geometry > Rectangle")["ports"]);   // was the component's ports
      var comp = (JsonObject)Candidate(r, "Curve > Primitive > Rectangle")["ports"];                    // positive control
      Assert.Equal(new[] { "P", "X", "Y", "R" }, ((JsonArray)comp["inputs"]).Select(n => (string)n).ToArray());
      Assert.Equal("an instance on this canvas", (string)comp["readFrom"]);
    }

    [Fact]
    public void U39_A_param_instance_gives_its_own_single_input_and_output()
    {
      var live = new[]
      {
        RectangleComponent(),
        new LiveObject { Guid = G(1), Name = "Rectangle", IsParam = true, Inputs = new[] { "Rectangle" }, Outputs = new[] { "Rectangle" } },
      };
      var r = KbLookupReport.Build("Rectangle", QualifiedNames.Resolve("Rectangle", Installed()), null,
        info => LivePorts.Pick(live, info, true));

      var param = (JsonObject)Candidate(r, "Params > Geometry > Rectangle")["ports"];
      Assert.Equal(new[] { "Rectangle" }, ((JsonArray)param["inputs"]).Select(n => (string)n).ToArray());
      Assert.Equal(new[] { "Rectangle" }, ((JsonArray)param["outputs"]).Select(n => (string)n).ToArray());
      var comp = (JsonObject)Candidate(r, "Curve > Primitive > Rectangle")["ports"];
      Assert.Equal(4, ((JsonArray)comp["inputs"]).Count);
    }

    [Fact]
    public void U39_A_shared_name_is_never_a_match_but_an_unshared_one_still_is()
    {
      var loft = new LiveObject { Guid = G(99), Name = "Loft", Inputs = new[] { "C", "O" }, Outputs = new[] { "L" } };
      var info = new InstalledType { Name = "Loft", Category = "Surface", SubCategory = "Freeform", Guid = G(5) };

      Assert.Null(LivePorts.Pick(new[] { loft }, info, nameSharedByInstalledTypes: true));
      var ok = LivePorts.Pick(new[] { loft }, info, nameSharedByInstalledTypes: false);   // the by-name fallback, as before
      Assert.NotNull(ok);
      Assert.Equal(new[] { "C", "O" }, ((JsonArray)ok["inputs"]).Select(n => (string)n).ToArray());
    }

    [Fact]
    public void U39_Two_instances_with_the_same_name_and_different_guids_are_not_a_match_either()
    {
      var a = new LiveObject { Guid = G(10), Name = "Loft", Inputs = new[] { "C" }, Outputs = new[] { "L" } };
      var b = new LiveObject { Guid = G(11), Name = "Loft", Inputs = new[] { "X" }, Outputs = new[] { "Y" } };
      var info = new InstalledType { Name = "Loft", Guid = G(5) };
      Assert.Null(LivePorts.Pick(new[] { a, b }, info, false));
    }

    [Fact]
    public void U39_A_param_on_the_canvas_never_answers_for_a_component_by_name()
    {
      var param = new LiveObject { Guid = G(1), Name = "Rectangle", IsParam = true, Inputs = new[] { "Rectangle" }, Outputs = new[] { "Rectangle" } };
      var componentInfo = Installed()[1];
      Assert.Null(LivePorts.Pick(new[] { param }, componentInfo, nameSharedByInstalledTypes: false));
    }
  }
}
