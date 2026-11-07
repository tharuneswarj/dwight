using System;
using System.Linq;
using System.Text.Json.Nodes;
using Dwight.Agent;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// The advertised surface is a CONTRACT, not documentation: it is what the model reads to decide what
  /// it may call and with what arguments. A malformed schema or a duplicated name is a defect the model
  /// discovers at runtime, mid-task, with no way to recover - so the shape is asserted here.
  ///
  /// This list is also the MCP <c>list_tools</c> response (ADR-069/098), so these assertions are the
  /// v1.1 face's contract test arriving a version early.
  /// </summary>
  public class AgentToolCatalogTests
  {
    [Fact]
    public void Every_tool_has_a_name_a_description_and_a_schema()
    {
      foreach (var t in AgentToolCatalog.All())
      {
        Assert.False(string.IsNullOrWhiteSpace(t.Name));
        Assert.False(string.IsNullOrWhiteSpace(t.Description));
        Assert.False(string.IsNullOrWhiteSpace(t.InputSchemaJson));
      }
    }

    [Fact]
    public void Tool_names_are_unique()
    {
      var names = AgentToolCatalog.All().Select(t => t.Name).ToList();
      Assert.Equal(names.Distinct(StringComparer.OrdinalIgnoreCase).Count(), names.Count);
    }

    [Fact]
    public void Tool_names_are_lowercase_snake_case()
    {
      // The MCP convention, and what keeps dispatch case-folding predictable.
      foreach (var t in AgentToolCatalog.All())
        Assert.Matches("^[a-z][a-z0-9_]*$", t.Name);
    }

    [Fact]
    public void Every_input_schema_is_a_valid_json_schema_object()
    {
      foreach (var t in AgentToolCatalog.All())
      {
        JsonNode parsed = null;
        var ex = Record.Exception(() => parsed = JsonNode.Parse(t.InputSchemaJson));
        Assert.Null(ex);                                                   // it parses
        Assert.Equal("object", parsed["type"].GetValue<string>());         // it describes an object
        Assert.NotNull(parsed["properties"]);                              // it declares its arguments
      }
    }

    [Fact]
    public void Schemas_are_flat_enough_for_the_weakest_provider_path()
    {
      // ADR-058 rule 5: flat schemas survive every provider. Arguments nest at most one level
      // (get_tree_signature's from/to pair), never deeper.
      foreach (var t in AgentToolCatalog.All())
      {
        var props = JsonNode.Parse(t.InputSchemaJson)["properties"].AsObject();
        foreach (var p in props)
        {
          var inner = p.Value?["properties"] as JsonObject;
          if (inner == null) continue;
          foreach (var q in inner)
            Assert.Null(q.Value?["properties"]);                           // no third level
        }
      }
    }

    [Fact]
    public void The_read_surface_is_zero_cost_and_only_named_tools_do_work()
    {
      // #190 as a standing property rather than a Stage-A snapshot: everything except the two tools
      // that are DECLARED to do work must be a pure read - zero-token, solve-free, mutation-free. A
      // tool that quietly gained the ability to solve or mutate without saying so here would escape
      // both the async dispatch and the verdict law (ADR-098).
      var doesWork = new[]
      {
        AgentToolCatalog.SolveAndCheck, AgentToolCatalog.BuildRegion, AgentToolCatalog.ProposeScaffold,
        AgentToolCatalog.SetValue,   // Phase-9 U4: the consented edit to an existing component
      };

      foreach (var t in AgentToolCatalog.All().Where(t => !doesWork.Contains(t.Name)))
      {
        Assert.False(t.Mutates, t.Name + " must not mutate");
        Assert.False(t.Solves, t.Name + " must not solve");
      }
    }

    [Fact]
    public void Every_mutating_tool_declares_its_costs_honestly()
    {
      // REWRITTEN at WP-3 Stage C (#219), to the DURABLE property rather than to a count. `Assert.Single`
      // was true while build_region was the only write; propose_scaffold makes it two, and nudging the
      // number to 2 would have asserted nothing (#194). What must hold is that each write declares
      // exactly the costs it incurs, because the dispatcher routes on those declarations - a tool that
      // solves and does not say so would be answered synchronously and deadlock against its own solve.
      var mutating = AgentToolCatalog.All().Where(t => t.Mutates).ToList();
      Assert.Equal(3, mutating.Count);

      var build = mutating.Single(t => t.Name == AgentToolCatalog.BuildRegion);
      var scaffold = mutating.Single(t => t.Name == AgentToolCatalog.ProposeScaffold);
      var setValue = mutating.Single(t => t.Name == AgentToolCatalog.SetValue);

      Assert.True(build.Solves, "build_region places components and must solve to verify them");
      Assert.False(scaffold.Solves, "a scaffold lays down groups and scribbles - there is nothing to solve");
      // Phase-9 U4: a value change expires the component and the definition recomputes downstream, so it
      // is observable work and must be dispatched asynchronously - it cannot hide inside the read surface.
      Assert.True(setValue.Solves, "set_value changes what the definition computes and must be dispatched as work");
    }

    [Fact]
    public void The_two_mutating_tools_carry_DIFFERENT_verdict_instruments()
    {
      // #190 Q1's ruling, asserted rather than remembered: build_region proves the TRIO (preservation,
      // completeness, solve-health) because it adds computation; propose_scaffold proves the stronger
      // BYTE-IDENTICAL claim, because groups and scribbles are not part of the computation at all. The
      // difference IS the ruling, so it is what the test holds.
      var build = AgentToolCatalog.All().First(t => t.Name == AgentToolCatalog.BuildRegion);
      var scaffold = AgentToolCatalog.All().First(t => t.Name == AgentToolCatalog.ProposeScaffold);

      Assert.Contains("VERDICT", build.Description);
      Assert.Contains("reverted", build.Description);
      Assert.Contains("does NOT say the result is what you wanted", build.Description);

      // The scaffold's claim is that it changed NOTHING - which is exactly what its instrument proves.
      Assert.Contains("Nothing is built", scaffold.Description);
      Assert.Contains("changes nothing about what the definition computes", scaffold.Description);
    }

    /// <summary>
    /// #633: build_region's descriptor is what the model reads INSIDE the build turn, and it had the same wiring
    /// gap as the CANNOT half ("wire ends are either a ref ... or the id of an existing component", with nothing
    /// about which pairs preservation rejects). It states the rule now, and never the over-strong sentence.
    /// </summary>
    [Fact]
    public void The_build_descriptor_states_the_wiring_rule()
    {
      var build = AgentToolCatalog.DescriptionOf(AgentToolCatalog.BuildRegion);

      Assert.Contains("never two existing components to each other", build);
      Assert.Contains("no wire that already exists is removed or reordered", build);
      Assert.DoesNotContain("into a component that already exists", build);
    }

    /// <summary>#643/#644: the descriptor names a component's output as a legal source into an existing component,
    /// and says a rebuild that would cut a wire into what it keeps is reverted.</summary>
    [Fact]
    public void The_build_descriptor_says_component_outputs_may_feed_existing_ones_and_a_rebuild_cannot_cut()
    {
      var build = AgentToolCatalog.DescriptionOf(AgentToolCatalog.BuildRegion);

      Assert.Contains("anything this build places - a component's output included - into an existing one", build);
      Assert.Contains("if a component it would remove feeds anything outside what it removes", build);
    }

    /// <summary>
    /// #625/#647: the descriptor tells the build turn how a rebuild keeps its boundary - declare "reconnect", the
    /// plugin restores the position, an undeclared cut is refused before anything changes - and the way out for a
    /// wire nothing new should feed. And the published schema advertises the field the parser accepts, with its
    /// two ends required (#219: the descriptor is the contract).
    /// </summary>
    [Fact]
    public void The_build_descriptor_and_schema_publish_reconnect_and_its_way_out()
    {
      var build = AgentToolCatalog.DescriptionOf(AgentToolCatalog.BuildRegion);
      Assert.Contains("declare in \"reconnect\" which new output feeds each of those wires", build);
      Assert.Contains("puts it back in the same place", build);
      Assert.Contains("refused before anything is changed", build);
      Assert.Contains("tell the user to delete that wire first", build);

      var schema = (System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse(
        AgentToolCatalog.SchemaOf(AgentToolCatalog.BuildRegion));
      var reconnect = schema["properties"]["reconnect"];
      Assert.NotNull(reconnect);
      var item = reconnect["items"];
      Assert.Equal(new[] { "to", "from" }, item["required"].AsArray().Select(n => n.GetValue<string>()));
      Assert.NotNull(item["properties"]["to"]["properties"]["at"]);
      Assert.DoesNotContain("reconnect", schema["required"].AsArray().Select(n => n.GetValue<string>()));
    }

    [Fact]
    public void The_build_descriptor_says_a_fresh_build_may_restore_a_lost_input_optionally()
    {
      var build = AgentToolCatalog.DescriptionOf(AgentToolCatalog.BuildRegion);

      Assert.Contains("A FRESH build of a region may also restore, the same way, an input that lost only its source", build);
      Assert.Contains("declaring it is optional", build);
    }

    /// <summary>
    /// #633: and it makes no TURN-scoped ownership claim. "If that region already holds a build of YOURS from this
    /// turn" went stale at F1(b), when what Dwight placed became a document-scoped fact re-derived at the moment of
    /// use - a model told the replacement only covers this turn's work would refuse a rebuild the plugin allows.
    /// </summary>
    [Fact]
    public void The_build_descriptor_makes_no_turn_scoped_ownership_claim()
    {
      var build = AgentToolCatalog.DescriptionOf(AgentToolCatalog.BuildRegion);

      Assert.DoesNotContain("from this turn", build);
      Assert.DoesNotContain("YOURS", build);
      Assert.Contains("Dwight placed there and the user has not edited since", build);
    }

    [Fact]
    public void The_scaffold_tool_says_the_addresses_are_the_plugins()
    {
      // ADR-056 stated in the contract the model reads: it decomposes, the plugin names and places. A
      // model that believed it chose the addresses could make "A1" mean something different next message.
      var scaffold = AgentToolCatalog.All().First(t => t.Name == AgentToolCatalog.ProposeScaffold);
      Assert.Contains("the plugin decides where they sit and what they are CALLED", scaffold.Description);
    }

    [Fact]
    public void The_documented_read_tools_are_all_present()
    {
      var names = AgentToolCatalog.All().Select(t => t.Name).ToList();
      Assert.Contains(AgentToolCatalog.GetOverview, names);
      Assert.Contains(AgentToolCatalog.GetRegion, names);
      Assert.Contains(AgentToolCatalog.GetComponent, names);
      Assert.Contains(AgentToolCatalog.GetConnections, names);
      Assert.Contains(AgentToolCatalog.GetTreeSignature, names);
      Assert.Contains(AgentToolCatalog.Search, names);
      Assert.Contains(AgentToolCatalog.KbLookup, names);
    }

    [Fact]
    public void Region_id_volatility_is_stated_in_the_overview_description()
    {
      // Component ids are pinned for the session (#191); REGION ids are derived from a partition that
      // legitimately changes when the canvas does. The asymmetry is surfaced to the model rather than
      // left to be discovered.
      var overview = AgentToolCatalog.All().First(t => t.Name == AgentToolCatalog.GetOverview);
      Assert.Contains("REGION ids", overview.Description);
    }

    [Fact]
    public void The_overview_and_region_descriptors_say_a_region_is_not_a_grasshopper_cluster()
    {
      // U29 (#918): the collision cost a refused get_cluster on both sessions' first turn. The contract says the
      // noun's meaning before the model meets a region id.
      var overview = AgentToolCatalog.DescriptionOf(AgentToolCatalog.GetOverview);
      Assert.Contains("NOT a Grasshopper Cluster component", overview);
      Assert.Contains("\"Region N\"", overview);
      Assert.Contains("not a Grasshopper Cluster component", AgentToolCatalog.DescriptionOf(AgentToolCatalog.GetRegion));
    }

    [Fact]
    public void The_descriptors_say_what_solved_and_outCounts_mean_because_they_are_the_contract()
    {
      // U27 (#917, #316): the descriptor is the MCP contract, so the two figures the model misread in the
      // field are explained where it reads before it calls - and the payload says the same (SolveCheckReportTests).
      var solve = AgentToolCatalog.DescriptionOf(AgentToolCatalog.SolveAndCheck);
      Assert.Contains("false means nothing needed solving", solve);
      Assert.Contains("never that solving failed", solve);
      Assert.Contains("flat item counts per output", solve);
      Assert.Contains("\"means\"", solve);

      var component = AgentToolCatalog.DescriptionOf(AgentToolCatalog.GetComponent);
      Assert.Contains("flat, summed across branches", component);
      Assert.Contains("\"means\"", component);
    }

    [Fact]
    public void Search_and_kb_lookup_say_the_qualified_form_exists_and_where_to_use_it()
    {
      // U25 (#915): the model told the user "there is no plain Plane+X+Y rectangle type installed" because no
      // spelling of a bare name reached the component. The contract names the form before it is needed.
      var search = AgentToolCatalog.DescriptionOf(AgentToolCatalog.Search);
      Assert.Contains("Category > SubCategory > Name", search);
      Assert.Contains("\"qualified\"", search);
      // The schema JSON escapes '>' as > on the wire, so the field's text is read back parsed, as the model reads it.
      string TypeFieldDescription(string tool) =>
        JsonNode.Parse(AgentToolCatalog.SchemaOf(tool))["properties"]["type"]["description"].GetValue<string>();
      Assert.Contains("Category > SubCategory > Name", TypeFieldDescription(AgentToolCatalog.Search));

      var kb = AgentToolCatalog.DescriptionOf(AgentToolCatalog.KbLookup);
      Assert.Contains("Category > SubCategory > Name", kb);
      Assert.Contains("returns EACH of them", kb);
      Assert.Contains("build with the qualified form", kb);
      Assert.Contains("Category > SubCategory > Name", TypeFieldDescription(AgentToolCatalog.KbLookup));
    }

    [Fact]
    public void Get_component_says_a_fed_panel_returns_the_values_it_displays()
    {
      // U31 (#926): the model has to be TOLD the fact exists (the #377 lesson) or it reports the placeholder.
      var component = AgentToolCatalog.DescriptionOf(AgentToolCatalog.GetComponent);
      Assert.Contains("FED by a wire", component);
      Assert.Contains("the values it displays", component);
      Assert.Contains("said to be displayed values", component);
    }

    [Fact]
    public void Knows_is_case_insensitive_and_rejects_the_unknown()
    {
      Assert.True(AgentToolCatalog.Knows("get_overview"));
      Assert.True(AgentToolCatalog.Knows("GET_OVERVIEW"));
      Assert.False(AgentToolCatalog.Knows("execute_anything"));   // no god-tool (#186)
      Assert.False(AgentToolCatalog.Knows(""));
      Assert.False(AgentToolCatalog.Knows(null));
    }

    /// <summary>
    /// #582's one extra input: which calls TRY something. Read off the descriptors' own Mutates and Solves
    /// flags, so there is no second list to drift - and a host tool that raises a card is not acting.
    /// </summary>
    [Theory]
    [InlineData("build_region", true)]
    [InlineData("propose_scaffold", true)]
    [InlineData("solve_and_check", true)]
    [InlineData("get_overview", false)]
    [InlineData("get_scaffold_map", false)]
    [InlineData("request_mutation", false)]
    [InlineData("no_such_tool", false)]
    public void Acts_is_true_exactly_for_the_tools_that_mutate_or_solve(string name, bool expected)
    {
      Assert.Equal(expected, AgentToolCatalog.Acts(name));
    }
  }
}
