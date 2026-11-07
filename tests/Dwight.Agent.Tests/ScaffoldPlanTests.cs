using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Dwight.Agent;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// The scaffold plan's validator and ADDRESSING (Stage C, the #219 substrate).
  ///
  /// The property under test throughout is the ADR-056 split: the model decomposes and labels; the plugin
  /// decides what each region is CALLED and where it sits in the flow. An address the model could choose
  /// would stop meaning position in the flow the first time it chose differently.
  /// </summary>
  public class ScaffoldPlanTests
  {
    private static JsonObject Region(string refName, string name, string purpose,
      string[] dependsOn = null, int[] anchors = null, string[] pseudo = null,
      string[] inputs = null, string[] outputs = null)
    {
      var o = new JsonObject { ["ref"] = refName, ["name"] = name, ["purpose"] = purpose };
      if (dependsOn != null) o["dependsOn"] = new JsonArray(dependsOn.Select(d => (JsonNode)d).ToArray());
      if (anchors != null) o["anchorIds"] = new JsonArray(anchors.Select(a => (JsonNode)a).ToArray());
      if (pseudo != null) o["pseudo"] = new JsonArray(pseudo.Select(p => (JsonNode)p).ToArray());
      if (inputs != null) o["inputs"] = new JsonArray(inputs.Select(p => (JsonNode)p).ToArray());
      if (outputs != null) o["outputs"] = new JsonArray(outputs.Select(p => (JsonNode)p).ToArray());
      return o;
    }

    private static JsonObject Args(params JsonObject[] regions) =>
      new JsonObject { ["regions"] = new JsonArray(regions.Cast<JsonNode>().ToArray()) };

    /// <summary>Every id alive - the ordinary case.</summary>
    private static Func<int, IdState> AllAlive => _ => IdState.Alive;

    /// <summary><paramref name="inUse"/> is the addresses the canvas and the stored map already carry (#720);
    /// omitted means an empty canvas, which is what every test written before #720 was standing on.</summary>
    private static ScaffoldPlan Parse(JsonObject args, Func<int, IdState> ids = null, string[] inUse = null)
    {
      Assert.True(ScaffoldPlanParser.TryParse(args, ids ?? AllAlive, inUse ?? ScaffoldPlanParser.EmptyCanvas.ToArray(),
        out var plan, out var error), error?.ToJson());
      return plan;
    }

    // ---- addressing --------------------------------------------------------------------------------

    [Fact]
    public void The_address_is_the_plugins_and_comes_from_the_dependency_layering()
    {
      // Declared out of flow order on purpose: "roof" is declared first but depends on "profiles", so the
      // ADDRESS must still put profiles in the first layer. If the model's declaration order decided the
      // address, "A1" would mean "whatever it listed first" rather than "the start of the flow".
      var plan = Parse(Args(
        Region("roof", "Loft the roof", "loft the profiles", dependsOn: new[] { "profiles" }),
        Region("profiles", "Build profiles", "divide the curves")));

      Assert.Equal("A1", plan.Regions.Single(r => r.Ref == "profiles").Key);
      Assert.Equal("B1", plan.Regions.Single(r => r.Ref == "roof").Key);
    }

    [Fact]
    public void Regions_in_one_layer_number_in_declaration_order()
    {
      // Within a layer the flow says nothing, so the only ordering the model actually expressed is the one
      // used - rather than an arbitrary one that would move between runs.
      var plan = Parse(Args(
        Region("a", "First", "p"),
        Region("b", "Second", "p"),
        Region("c", "Third", "p")));

      Assert.Equal(new[] { "A1", "A2", "A3" }, plan.Regions.Select(r => r.Key));
    }

    [Fact]
    public void Addressing_is_deterministic_across_repeated_parses()
    {
      // A user says "build A2" out loud. It has to mean the same region every time it is computed.
      JsonObject Build() => Args(
        Region("c", "Third", "p", dependsOn: new[] { "a" }),
        Region("a", "First", "p"),
        Region("b", "Second", "p", dependsOn: new[] { "a" }));

      var first = Parse(Build()).Regions.ToDictionary(r => r.Ref, r => r.Key);
      var second = Parse(Build()).Regions.ToDictionary(r => r.Ref, r => r.Key);

      Assert.Equal(first, second);
    }

    [Fact]
    public void A_long_flow_addresses_past_Z()
    {
      Assert.Equal("A", ScaffoldPlanParser.Letter(0));
      Assert.Equal("Z", ScaffoldPlanParser.Letter(25));
      Assert.Equal("AA", ScaffoldPlanParser.Letter(26));
      Assert.Equal("AB", ScaffoldPlanParser.Letter(27));
    }

    [Fact]
    public void The_group_name_carries_the_address_so_the_map_survives_the_record()
    {
      // #221b: the region key must be recoverable FROM THE CANVAS, so WP-4 addresses a region by an object
      // that exists rather than by a stored map that might not.
      var plan = Parse(Args(Region("prep", "Prepare curves", "clean and divide")));
      var region = plan.Regions.Single();

      Assert.Equal("A1 - Prepare curves", region.GroupName);
      Assert.StartsWith(region.Key, region.GroupName);
    }

    // ---- cycles ------------------------------------------------------------------------------------

    [Fact]
    public void A_cyclic_dependency_set_is_broken_and_reported_rather_than_refused()
    {
      // A model-proposed dependency set can be cyclic, exactly as a real cluster graph can be. The
      // existing law applies (ADR-084/#221): reverse the closing edge, display ordering only, and SAY so -
      // an unexplained reading order is how a map stops being trustworthy.
      var plan = Parse(Args(
        Region("a", "One", "p", dependsOn: new[] { "c" }),
        Region("b", "Two", "p", dependsOn: new[] { "a" }),
        Region("c", "Three", "p", dependsOn: new[] { "b" })));

      Assert.Single(plan.ReversedEdges);
      Assert.All(plan.Regions, r => Assert.False(string.IsNullOrEmpty(r.Key)));

      // Whatever was reversed, the result is layerable: no region depends on one in a later layer.
      var layer = plan.Regions.ToDictionary(r => r.Ref, r => r.Layer, StringComparer.OrdinalIgnoreCase);
      Assert.All(plan.Edges, e => Assert.True(layer[e.From] < layer[e.To]));
    }

    [Fact]
    public void An_acyclic_plan_reverses_nothing()
    {
      var plan = Parse(Args(
        Region("a", "One", "p"),
        Region("b", "Two", "p", dependsOn: new[] { "a" })));

      Assert.Empty(plan.ReversedEdges);
    }

    // ---- grounding ---------------------------------------------------------------------------------

    [Fact]
    public void An_invented_anchor_id_is_refused()
    {
      // Inventing ids is the class the registry exists to catch, so it is refused rather than dropped -
      // #208's line: exactly, or refuse.
      var args = Args(Region("a", "One", "p", anchors: new[] { 999 }));

      Assert.False(ScaffoldPlanParser.TryParse(args, _ => IdState.Unknown, ScaffoldPlanParser.EmptyCanvas, out _, out var error));
      Assert.Contains("unknown_id", error.ToJson());
      Assert.Contains("999", error.ToJson());
    }

    [Fact]
    public void A_retired_anchor_id_is_dropped_and_reported_rather_than_refused()
    {
      // It existed and is gone: the canvas moved under the model, which is not its mistake. The map still
      // lands, minus one adjacency hint, and the loss is stated.
      var plan = Parse(Args(Region("a", "One", "p", anchors: new[] { 7 })), _ => IdState.Retired);

      Assert.Empty(plan.Regions.Single().AnchorIds);
      Assert.Single(plan.DroppedAnchors);
      Assert.Contains("7", plan.DroppedAnchors[0]);
    }

    [Fact]
    public void A_live_anchor_is_kept()
    {
      var plan = Parse(Args(Region("a", "One", "p", anchors: new[] { 7, 8 })));
      Assert.Equal(new[] { 7, 8 }, plan.Regions.Single().AnchorIds);
    }

    [Fact]
    public void A_dependency_on_a_region_that_does_not_exist_is_refused_with_the_real_list()
    {
      var args = Args(
        Region("a", "One", "p"),
        Region("b", "Two", "p", dependsOn: new[] { "ghost" }));

      Assert.False(ScaffoldPlanParser.TryParse(args, AllAlive, ScaffoldPlanParser.EmptyCanvas, out _, out var error));
      var json = error.ToJson();
      Assert.Contains("ghost", json);
      Assert.Contains("\"a\"", json);   // and what it could have depended on instead
    }

    // ---- the refusals that keep the map a map ------------------------------------------------------

    [Fact]
    public void A_region_with_no_purpose_is_refused_because_the_map_is_the_point()
    {
      var args = Args(new JsonObject { ["ref"] = "a", ["name"] = "One" });
      Assert.False(ScaffoldPlanParser.TryParse(args, AllAlive, ScaffoldPlanParser.EmptyCanvas, out _, out var error));
      Assert.Contains("purpose", error.ToJson());
    }

    [Fact]
    public void A_region_with_no_name_is_refused_because_the_name_becomes_the_group_label()
    {
      var args = Args(new JsonObject { ["ref"] = "a", ["purpose"] = "does things" });
      Assert.False(ScaffoldPlanParser.TryParse(args, AllAlive, ScaffoldPlanParser.EmptyCanvas, out _, out var error));
      Assert.Contains("name", error.ToJson());
    }

    [Fact]
    public void Duplicate_refs_are_refused()
    {
      var args = Args(Region("a", "One", "p"), Region("a", "Two", "p"));
      Assert.False(ScaffoldPlanParser.TryParse(args, AllAlive, ScaffoldPlanParser.EmptyCanvas, out _, out var error));
      Assert.Contains("share the ref", error.ToJson());
    }

    [Fact]
    public void An_empty_plan_is_refused()
    {
      Assert.False(ScaffoldPlanParser.TryParse(new JsonObject { ["regions"] = new JsonArray() }, AllAlive, ScaffoldPlanParser.EmptyCanvas, out _, out _));
      Assert.False(ScaffoldPlanParser.TryParse(new JsonObject(), AllAlive, ScaffoldPlanParser.EmptyCanvas, out _, out _));
    }

    [Fact]
    public void Too_many_regions_is_refused_rather_than_truncated()
    {
      // Silently keeping the first N would hand back a map missing work the model planned - the caller
      // would never know what was dropped (#134: no silent caps).
      var many = Enumerable.Range(0, ScaffoldPlanParser.MaxRegions + 1)
        .Select(i => Region("r" + i, "Region " + i, "p")).ToArray();

      Assert.False(ScaffoldPlanParser.TryParse(Args(many), AllAlive, ScaffoldPlanParser.EmptyCanvas, out _, out var error));
      Assert.Contains("at most", error.ToJson());
    }

    // ---- sizing ------------------------------------------------------------------------------------

    [Fact]
    public void A_region_carrying_more_pseudocode_gets_a_bigger_box()
    {
      var plan = Parse(Args(
        Region("small", "Small", "p", pseudo: new[] { "one line" }),
        Region("big", "Big", "p", pseudo: Enumerable.Range(0, 8).Select(i => "line " + i).ToArray())));

      var small = plan.Regions.Single(r => r.Ref == "small");
      var big = plan.Regions.Single(r => r.Ref == "big");

      Assert.True(big.Height > small.Height);
      Assert.True(small.Width > 0 && small.Height > 0);
    }

    [Fact]
    public void Pseudocode_is_bounded_per_region_and_per_line()
    {
      var plan = Parse(Args(Region("a", "One", "p",
        pseudo: Enumerable.Range(0, 200).Select(i => new string('x', 300)).ToArray())));

      var region = plan.Regions.Single();
      Assert.True(region.Pseudo.Count <= ScaffoldPlanParser.MaxPseudoLines);
      Assert.All(region.Pseudo, l => Assert.True(l.Length <= ScaffoldPlanParser.MaxLineChars));
    }

    // ---- the body scribble's bound (#238) ----------------------------------------------------------

    [Fact]
    public void The_body_scribble_is_bounded_and_says_what_it_left_out()
    {
      // #238: the full pseudo-code stays in the stored map; only the DRAWN lines are capped, because a box
      // sized by a thirty-line body dominates the canvas it exists to orient. The elision is stated on the
      // scribble itself - the reader here is a person, not a model, so a payload field would not reach them.
      var lines = Enumerable.Range(1, ScaffoldPlanParser.RenderedBodyLines + 7).Select(i => "step " + i).ToArray();
      var region = Parse(Args(Region("a", "One", "divide the curves", pseudo: lines))).Regions.Single();

      Assert.Equal(lines.Length, region.Pseudo.Count);                  // kept in full for the map and WP-4
      Assert.Contains("7 more lines in the plan", region.BodyText);     // and the loss is stated
      Assert.Contains("divide the curves", region.BodyText);            // the purpose always leads
      Assert.DoesNotContain("step " + lines.Length, region.BodyText);   // the tail genuinely is not drawn
    }

    [Fact]
    public void A_short_body_says_nothing_about_elision()
    {
      var region = Parse(Args(Region("a", "One", "p", pseudo: new[] { "one", "two" }))).Regions.Single();
      Assert.DoesNotContain("more line", region.BodyText);
    }

    [Fact]
    public void The_box_is_sized_from_what_is_drawn_not_from_what_is_stored()
    {
      // Otherwise a region with a long stored plan would claim a field of canvas it does not fill.
      var drawn = Parse(Args(Region("a", "One", "p",
        pseudo: Enumerable.Range(0, ScaffoldPlanParser.RenderedBodyLines).Select(i => "l" + i).ToArray()))).Regions.Single();
      var stored = Parse(Args(Region("a", "One", "p",
        pseudo: Enumerable.Range(0, ScaffoldPlanParser.MaxPseudoLines).Select(i => "l" + i).ToArray()))).Regions.Single();

      // The second holds four times the plan but draws one extra line - the elision notice.
      Assert.True(stored.Pseudo.Count > drawn.Pseudo.Count);
      Assert.Equal(drawn.RenderedLineCount + 1, stored.RenderedLineCount);
    }

    // ---- declared boundaries (#240 / C1) -----------------------------------------------------------

    [Fact]
    public void A_region_declares_what_it_takes_and_gives_in_words()
    {
      // The map is anchored CLEAR of the existing definition, so a region attached to component 42 can sit
      // a long way from it and the relationship is invisible on screen. Words are the right instrument -
      // moving geometry to imply it would cost the purely-additive guarantee for something a sentence
      // says better.
      var region = Parse(Args(Region("a", "Prepare", "clean the curves",
        inputs: new[] { "curves from Divide (42)" },
        outputs: new[] { "an ordered point grid" }))).Regions.Single();

      Assert.Contains("takes: curves from Divide (42)", region.BodyText);
      Assert.Contains("gives: an ordered point grid", region.BodyText);
    }

    [Fact]
    public void The_boundary_lines_read_before_the_steps_and_after_the_purpose()
    {
      var lines = Parse(Args(Region("a", "Prepare", "clean the curves",
        inputs: new[] { "raw curves" }, outputs: new[] { "points" },
        pseudo: new[] { "divide each curve" }))).Regions.Single().BodyLines;

      Assert.Equal("clean the curves", lines[0]);
      Assert.StartsWith("takes:", lines[1]);
      Assert.StartsWith("gives:", lines[2]);
      Assert.Equal("divide each curve", lines[3]);
    }

    [Fact]
    public void A_region_that_declares_no_boundary_says_nothing_about_one()
    {
      // An empty "takes:" line is worse than no line - it reads as "takes nothing", which is a claim.
      var region = Parse(Args(Region("a", "One", "p"))).Regions.Single();
      Assert.DoesNotContain("takes:", region.BodyText);
      Assert.DoesNotContain("gives:", region.BodyText);
    }

    [Fact]
    public void Declared_boundaries_are_bounded_in_count_and_in_width()
    {
      var region = Parse(Args(Region("a", "One", "p",
        inputs: Enumerable.Range(0, 20).Select(i => "input " + i).ToArray()))).Regions.Single();

      Assert.True(region.Inputs.Count <= ScaffoldPlanParser.MaxBoundaryItems);
      Assert.All(region.BodyLines, l => Assert.True(l.Length <= ScaffoldPlanParser.MaxLineChars));
    }

    [Fact]
    public void The_box_grows_for_the_boundary_lines_it_draws()
    {
      // One list feeds both the scribble and the geometry, so a declared boundary cannot be drawn in a box
      // that was sized without it.
      var bare = Parse(Args(Region("a", "One", "p"))).Regions.Single();
      var declared = Parse(Args(Region("a", "One", "p",
        inputs: new[] { "curves" }, outputs: new[] { "points" }))).Regions.Single();

      Assert.Equal(bare.RenderedLineCount + 2, declared.RenderedLineCount);
      Assert.True(declared.Height > bare.Height);
    }

    [Fact]
    public void The_header_carries_the_address_and_is_not_subject_to_the_body_bound()
    {
      // #238: the header is load-bearing whatever else changes - it is the navigation layer and it is how
      // WP-4 finds a region by an object that exists.
      var region = Parse(Args(Region("a", "Prepare curves", "p",
        pseudo: Enumerable.Range(0, 30).Select(i => "l" + i).ToArray()))).Regions.Single();

      Assert.Equal("A1 - Prepare curves", region.HeaderText);
      Assert.DoesNotContain("more line", region.HeaderText);
    }

    // ---- #581: no region ADDRESSES in the plan's own prose ------------------------------------------

    /// <summary>
    /// THE FOUNDING CASE. Dwight's self-audit found B1's and C1's prose referencing A2 through A5 in a plan that
    /// assigned A1, B1, C1, D1, E1 and F1. None of A2-A5 is assigned, so a check against only the addresses the plan
    /// assigns would have MISSED it. Brief CK narrowed the rule to what the plan COULD produce: its layers and the
    /// region counts in them. This test changed from "A2" in a one-region layer A (now prose, see
    /// An_A_number_past_the_regions_in_its_layer_is_prose) to "A2" in a layer A that holds two regions, which is the
    /// same defect: an address the plan could have minted and that may not match the map.
    /// </summary>
    [Fact]
    public void An_address_the_plan_does_not_even_assign_is_still_refused()
    {
      var args = Args(
        Region("start", "Surface", "the base surface"),
        Region("start2", "Curve", "the base curve"),
        Region("grid", "UV Subdivision", "divides the surface", dependsOn: new[] { "start" },
          inputs: new[] { "the surface from A2" }));

      Assert.False(ScaffoldPlanParser.TryParse(args, AllAlive, ScaffoldPlanParser.EmptyCanvas, out _, out var error));
      Assert.Contains("\"A2\"", error.Message);
      Assert.Contains("by its NAME", error.Message);
    }

    /// <summary>
    /// Addresses are assigned AFTER this parse, so "A2" in the model's prose is a guess rather than a ref,
    /// and there is nothing to map it FROM. The refusal says so, which is what makes it repairable.
    /// </summary>
    [Fact]
    public void The_refusal_explains_that_addresses_do_not_exist_until_the_plan_is_accepted()
    {
      // Changed for Brief CK: one region is one layer, so only A1 is a token this plan could produce (B1 is now prose).
      var args = Args(Region("a", "Panels", "makes panels", pseudo: new[] { "take the grid from A1" }));

      Assert.False(ScaffoldPlanParser.TryParse(args, AllAlive, ScaffoldPlanParser.EmptyCanvas, out _, out var error));
      Assert.Contains("assigned by Dwight only after the plan is accepted", error.Message);
      Assert.Equal("pseudo", error.Detail["field"].GetValue<string>());
      Assert.Equal("A1", error.Detail["token"].GetValue<string>());
    }

    /// <summary>Every free-text field the model writes is stored as written, so every one is checked - not
    /// only the pseudo-code, inputs and outputs the self-audit happened to catch.</summary>
    [Theory]
    [InlineData("name")]
    [InlineData("purpose")]
    [InlineData("pseudo")]
    [InlineData("inputs")]
    [InlineData("outputs")]
    public void Every_prose_field_is_checked(string field)
    {
      // Changed for Brief CK: C3 is prose in a one-region plan, so the token is A1, the one address this plan produces.
      var name = field == "name" ? "Panels for A1" : "Panels";
      var purpose = field == "purpose" ? "feeds A1" : "makes panels";
      var region = Region("a", name, purpose,
        pseudo: field == "pseudo" ? new[] { "from A1" } : null,
        inputs: field == "inputs" ? new[] { "from A1" } : null,
        outputs: field == "outputs" ? new[] { "to A1" } : null);

      Assert.False(ScaffoldPlanParser.TryParse(Args(region), AllAlive, ScaffoldPlanParser.EmptyCanvas, out _, out var error));
      Assert.Equal(field, error.Detail["field"].GetValue<string>());
    }

    /// <summary>
    /// The prompt rule is "refer to regions by NAME", and a plan that does that passes untouched. This is
    /// the half that stops the check becoming a wall.
    /// </summary>
    [Fact]
    public void A_plan_that_refers_to_regions_by_name_passes()
    {
      var plan = Parse(Args(
        Region("start", "Surface", "the base surface"),
        Region("grid", "UV Subdivision", "divides the surface", dependsOn: new[] { "start" },
          inputs: new[] { "the surface from Surface" },
          pseudo: new[] { "divide Surface into a 3D grid of cells" })));

      Assert.Equal(2, plan.Regions.Count);
    }

    /// <summary>
    /// THE PATTERN, pinned to <see cref="ScaffoldAddress.IsAddress"/> rather than a second regex: one or
    /// more UPPERCASE letters then one or more digits, on whole tokens. The named false positive - "an A4
    /// sheet" - is refused too, and that is accepted: it costs one retry and the refusal names the token.
    /// </summary>
    // ---- Brief CK: the guard fires only on an address this plan, on this canvas, could have ----------------------------------

    private static string[] Pseudo(string line) => new[] { line };

    private static bool Refused(JsonObject args, string[] inUse, out ToolResult error) =>
      !ScaffoldPlanParser.TryParse(args, AllAlive, inUse ?? ScaffoldPlanParser.EmptyCanvas, out _, out error);

    /// <summary>Two regions in layer A, then one in B, then one in C, then one in D: A1, A2, B1, C1, D1.</summary>
    private static JsonObject FourLayers(string pseudoOnLast)
    {
      return Args(
        Region("p", "Points", "the points"),
        Region("q", "Curves", "the curves"),
        Region("m", "Mesh", "meshes them", dependsOn: new[] { "p", "q" }),
        Region("n", "Toolpath", "walks the mesh", dependsOn: new[] { "m" }),
        Region("g", "Machine", "drives the machine", dependsOn: new[] { "n" }, pseudo: Pseudo(pseudoOnLast)));
    }

    [Theory]
    [InlineData("G21")]
    [InlineData("G1 X10 Y10")]
    [InlineData("M104 S200")]
    [InlineData("G28 homing")]
    public void G_and_M_code_words_are_prose_on_a_plan_of_several_layers(string line)
    {
      // G is layer index 6 and the plan has four layers, so the LETTER bound alone makes these prose.
      Assert.False(Refused(FourLayers(line), null, out var error), error?.ToJson());
    }

    [Fact]
    public void An_address_the_canvas_already_holds_is_refused()
    {
      // A map already on the canvas holds A1; "A1" in this plan's text would point at a region of THAT map.
      var args = Args(Region("only", "Panels", "makes panels", pseudo: Pseudo("take it from A1")));
      Assert.True(Refused(args, new[] { "A1" }, out var error));
      Assert.Equal("A1", error.Detail["token"].GetValue<string>());
    }

    [Fact]
    public void An_address_in_use_is_refused_even_when_the_plan_could_not_produce_it()
    {
      // Z9 is past this plan's layers and regions, so only the in-use set can make it suspect, and it does.
      var args = Args(Region("only", "Panels", "makes panels", pseudo: Pseudo("see Z9")));
      Assert.False(Refused(args, null, out _));
      Assert.True(Refused(args, new[] { "Z9" }, out _));
    }

    private static JsonObject TwoLayersTwoInB(string line) => Args(
      Region("a", "Start", "starts"),
      Region("b1", "Left", "left", dependsOn: new[] { "a" }),
      Region("b2", "Right", "right", dependsOn: new[] { "a" }, pseudo: Pseudo(line)));

    [Fact]
    public void B2_in_a_plan_with_two_layers_and_two_regions_in_B_is_refused_and_B9_and_C1_are_not()
    {
      Assert.True(Refused(TwoLayersTwoInB("joins B2"), null, out var error));
      Assert.Equal("B2", error.Detail["token"].GetValue<string>());
      Assert.False(Refused(TwoLayersTwoInB("joins B9"), null, out _));   // the NUMBER bound: B has two regions, 9 is past them
      Assert.False(Refused(TwoLayersTwoInB("joins C1"), null, out _));   // the LETTER bound: the plan has two layers, no C
    }

    [Fact]
    public void An_A_number_past_the_regions_in_its_layer_is_prose()
    {
      Assert.False(Refused(LayerAWith(3, "an A4 sheet"), null, out _));          // three regions in A: A4 cannot be minted
      Assert.True(Refused(LayerAWith(4, "an A4 sheet"), null, out var error));   // four regions in A: A4 is region four
      Assert.Equal("A4", error.Detail["token"].GetValue<string>());
    }

    private static JsonObject LayerAWith(int regions, string line)
    {
      var list = new List<JsonObject>();
      for (var i = 0; i < regions; i++)
        list.Add(Region("r" + i, "Region " + i, "does " + i, pseudo: i == 0 ? Pseudo(line) : null));
      return Args(list.ToArray());
    }

    [Fact]
    public void The_refusal_when_it_still_fires_keeps_its_wording_and_names_the_token()
    {
      var args = Args(Region("only", "Panels", "makes panels", pseudo: Pseudo("from A1")));
      Assert.True(Refused(args, null, out var error));
      Assert.Equal("Region \"only\" mentions \"A1\" in its pseudo. That looks like a region ADDRESS, and addresses are assigned by Dwight "
        + "only after the plan is accepted - so an address written into the plan is a guess that will not match the map. Refer to the other "
        + "region by its NAME instead.", error.Message);
    }

    [Theory]
    [InlineData("from A2", "A2")]
    [InlineData("(B12)", "B12")]
    [InlineData("AA3's output", "AA3")]
    [InlineData("an A4 sheet", "A4")]
    [InlineData("A1-A5", "A1")]
    public void Address_shaped_whole_tokens_are_found(string text, string expected)
    {
      Assert.Equal(expected, ScaffoldPlanParser.FirstAddressToken(text));
    }

    [Theory]
    [InlineData("step2 of the loop")]    // lowercase is not an address
    [InlineData("a 3D grid")]            // digits first is not an address
    [InlineData("UV Subdivision")]       // letters with no digits
    [InlineData("A2B")]                  // trailing junk: not a whole address
    [InlineData("")]
    [InlineData(null)]
    public void Tokens_that_are_not_addresses_are_left_alone(string text)
    {
      Assert.Null(ScaffoldPlanParser.FirstAddressToken(text));
    }
  }
}
