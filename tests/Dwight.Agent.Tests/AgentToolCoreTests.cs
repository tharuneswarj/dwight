using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Dwight.Agent;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// Stage A: the read-tool projections, groundedness resolution, and refusal shapes. Every assertion
  /// here is about a property the agent's honesty rests on - ids that resolve, refusals that are coded
  /// and actionable, caps that announce themselves, and reads that never claim more than they read.
  /// </summary>
  public class AgentToolCoreTests
  {
    private static Guid G(int n) { var b = new byte[16]; b[0] = (byte)n; b[1] = (byte)(n >> 8); return new Guid(b); }

    /// <summary>A small definition: slider(1) -> divide(2) -> loft(3), plus a stray panel(4).
    /// Clusters: {1,2} "Divide the curves", {3} "Loft", {4} its own.</summary>
    private static (AgentSnapshot snap, AgentIdRegistry ids) Fixture()
    {
      var ids = new AgentIdRegistry();
      ids.Sync(new[] { G(1), G(2), G(3), G(4) });
      var i1 = ids.Issue(G(1)); var i2 = ids.Issue(G(2));
      var i3 = ids.Issue(G(3)); var i4 = ids.Issue(G(4));

      var snap = new AgentSnapshot
      {
        Nodes =
        {
          new AgentNode { Id = i1, Type = "Number Slider", Kind = "param", Outputs = new[] { "N" }, OutCounts = new[] { 1 } },
          new AgentNode { Id = i2, Type = "Divide Curve", Kind = "component", Nick = "divider",
                          Inputs = new[] { "Curve", "Count" }, Outputs = new[] { "Points", "Tangents" }, OutCounts = new[] { 7, 7 } },
          new AgentNode { Id = i3, Type = "Loft", Kind = "component",
                          Inputs = new[] { "Curves", "Options" }, Outputs = new[] { "Loft" }, OutCounts = new[] { 1 },
                          Msgs = { new MsgDto { Level = "warning", Text = "Insufficient curves for a loft." } } },
          new AgentNode { Id = i4, Type = "Panel", Kind = "param" },
        },
        Edges =
        {
          new AgentEdgeDto { FromNode = i1, FromParam = "N", ToNode = i2, ToParam = "Count",
                             Tree = new TreeSigDto { Paths = 1, Depth = 1, Items = new[] { 1 } } },
          new AgentEdgeDto { FromNode = i2, FromParam = "Points", ToNode = i3, ToParam = "Curves",
                             Tree = new TreeSigDto { Paths = 7, Depth = 2, Items = new[] { 3, 3, 3, 3, 3, 3, 3 } } },
        },
        Clusters =
        {
          new AgentCluster { Id = 0, Label = "Divide the curves", Members = { i1, i2 } },
          new AgentCluster { Id = 1, Label = "Region 1", Members = { i3 } },
          new AgentCluster { Id = 2, Label = "Region 2", Members = { i4 } },
        },
        Flow = { (0, 1) },
      };
      return (snap, ids);
    }

    private static JsonObject Args(string json) => (JsonObject)JsonNode.Parse(json);
    private static JsonNode Ok(ToolResult r) { Assert.True(r.Ok, r.Code + ": " + r.Message); return r.Result; }

    /// <summary>Region reads require a partition stamp from a preceding get_overview (#195). These
    /// tests are about the PROJECTION, so they stand in for that call with the current stamp; the
    /// guard itself is covered in <see cref="PartitionStampTests"/>.</summary>
    private static ToolResult Region(AgentSnapshot snap, string argsJson) =>
      AgentToolCore.GetRegion(snap, Args(argsJson), AgentToolCore.ShapeHash(snap));

    // ---- get_overview ---------------------------------------------------------------------------

    [Fact]
    public void Overview_reports_counts_clusters_and_flow()
    {
      var (snap, _) = Fixture();
      var res = Ok(AgentToolCore.GetOverview(snap));

      Assert.Equal(2, res["counts"]["components"].GetValue<int>());
      Assert.Equal(2, res["counts"]["params"].GetValue<int>());
      Assert.Equal(2, res["counts"]["edges"].GetValue<int>());
      Assert.Equal(3, res["counts"]["regions"].GetValue<int>());
      Assert.Equal(3, res["regions"].AsArray().Count);
      Assert.Equal(1, res["flow"].AsArray().Count);
    }

    [Fact]
    public void Overview_carries_region_labels_including_the_generic_fallback()
    {
      // The zero-token law (#190): an unnamed region comes back generic rather than triggering a
      // paid labelling call. Both forms must survive the projection.
      var (snap, _) = Fixture();
      var regions = Ok(AgentToolCore.GetOverview(snap))["regions"].AsArray();
      var labels = regions.Select(c => c["label"].GetValue<string>()).ToList();

      Assert.Contains("Divide the curves", labels);
      Assert.Contains("Region 1", labels);
    }

    [Fact]
    public void The_overview_never_calls_a_region_a_cluster()
    {
      // U29 (#918): "Cluster" is a Grasshopper component type with its own tool; the overview presented segmenter
      // regions under that word, labelled "Cluster N", and the model told the user to explode a cluster that did
      // not exist. The only key allowed to say cluster is the one that IS about Grasshopper clusters.
      var (snap, _) = Fixture();
      var res = Ok(AgentToolCore.GetOverview(snap)).AsObject();
      Assert.False(res.ContainsKey("clusters"));
      Assert.False(res["counts"].AsObject().ContainsKey("clusters"));
      Assert.True(res["counts"].AsObject().ContainsKey("insideClusters"));   // Grasshopper clusters, counted honestly
      Assert.Contains("NOT a Grasshopper Cluster component", res["means"]["regions"].GetValue<string>());
      Assert.Contains("get_region", res["means"]["regions"].GetValue<string>());
    }

    [Fact]
    public void Overview_type_histogram_is_per_region()
    {
      var (snap, _) = Fixture();
      var first = Ok(AgentToolCore.GetOverview(snap))["regions"].AsArray()[0];
      Assert.Equal(1, first["types"]["Number Slider"].GetValue<int>());
      Assert.Equal(1, first["types"]["Divide Curve"].GetValue<int>());
    }

    // ---- U27 (#917): every number a read states says what it counts, IN the payload ------------------

    [Fact]
    public void Every_number_the_overview_states_carries_its_meaning()
    {
      var (snap, _) = Fixture();
      var res = Ok(AgentToolCore.GetOverview(snap));
      var means = (JsonObject)res["means"];
      var counts = means["counts"].GetValue<string>();
      foreach (var key in res["counts"].AsObject().Select(kv => kv.Key))
        Assert.Contains(key, counts);                                   // each total is named in its sentence
      Assert.Contains("Grasshopper Cluster components", counts);        // insideClusters is about real clusters
      Assert.True(means.ContainsKey("size"));
      Assert.True(means.ContainsKey("types"));
    }

    [Fact]
    public void Get_component_explains_outCounts_as_flat_per_output_not_a_total()
    {
      var (snap, ids) = Fixture();
      var divide = snap.Nodes.First(n => n.Type == "Divide Curve").Id;
      var res = Ok(AgentToolCore.GetComponent(snap, ids, Args("{\"id\":" + divide + "}")));
      var means = res["means"]["outCounts"].GetValue<string>();
      Assert.Contains("on each output port", means);
      Assert.Contains("neither a branch structure nor a total across ports", means);
      Assert.Equal(SolveCheckReport.OutCountsMeans, means);             // one definition with solve_and_check
      // A plain component carries no script, no internalised data, no cluster - so no sentence about them.
      Assert.False(res["means"].AsObject().ContainsKey("script"));
      Assert.False(res["means"].AsObject().ContainsKey("persistent"));
      Assert.False(res["means"].AsObject().ContainsKey("cluster"));
    }

    [Fact]
    public void Search_count_says_it_is_the_total_listed_or_not()
    {
      var ids = new AgentIdRegistry();
      var snap = new AgentSnapshot();
      var guids = Enumerable.Range(1, AgentToolCore.SearchCap + 5).Select(G).ToList();
      ids.Sync(guids);
      foreach (var g in guids) snap.Nodes.Add(new AgentNode { Id = ids.Issue(g), Type = "Panel", Kind = "param" });

      var res = Ok(AgentToolCore.SearchNodes(snap, Args("{\"type\":\"Panel\"}")));
      var means = res["means"]["count"].GetValue<string>();
      Assert.Contains("listed or not", means);
      Assert.Contains(AgentToolCore.SearchCap.ToString(), means);
      Assert.Contains("shown", means);
    }

    [Fact]
    public void Tree_signature_and_the_wire_lists_say_what_paths_depth_and_items_count()
    {
      var (snap, ids) = Fixture();
      var d = snap.Nodes.First(n => n.Type == "Divide Curve").Id;
      var l = snap.Nodes.First(n => n.Type == "Loft").Id;

      var sig = Ok(AgentToolCore.GetTreeSignature(snap, ids, Args(
        "{\"from\":{\"node\":" + d + ",\"param\":\"Points\"},\"to\":{\"node\":" + l + ",\"param\":\"Curves\"}}")));
      var means = sig["means"].GetValue<string>();
      Assert.Contains("paths is the branch count", means);
      Assert.Contains("items the item count per branch", means);
      Assert.Contains("never values", means);

      // The same sentence travels with every list of wires, so a sig in get_connections or get_region is never bare.
      Assert.Equal(means, Ok(AgentToolCore.GetConnections(snap, ids, Args("{\"id\":" + d + "}")))["means"].GetValue<string>());
      Assert.Equal(means, Ok(Region(snap, "{\"id\":0}"))["means"]["sig"].GetValue<string>());
    }

    [Fact]
    public void Diagnose_says_what_scanned_faultCount_and_strays_count()
    {
      var res = Ok(AgentToolCore.Diagnose(new List<DiagnoseFaultDto>(), strayCount: 2, scanned: 10, wholeCanvas: true, cap: 7));
      var means = res["means"].GetValue<string>();
      Assert.Contains("scanned is how many components", means);
      Assert.Contains("faultCount is every fault", means);
      Assert.Contains("at most 7", means);
      Assert.Contains("strays are isolated components", means);
      Assert.Contains("not faults", means);
    }

    // ---- groundedness ---------------------------------------------------------------------------

    [Fact]
    public void An_invented_id_is_refused_with_unknown_id()
    {
      var (snap, ids) = Fixture();
      var res = AgentToolCore.GetComponent(snap, ids, Args("{\"id\":9999}"));

      Assert.False(res.Ok);
      Assert.Equal(ToolErrors.UnknownId, res.Code);
      Assert.Contains("do not invent", res.Message);   // the correction is IN the refusal
    }

    [Fact]
    public void A_deleted_ids_refusal_says_deleted_not_invented()
    {
      var (snap, ids) = Fixture();
      var gone = ids.Issue(G(4));
      ids.Sync(new[] { G(1), G(2), G(3) });            // node 4 removed from the canvas
      snap.Nodes.RemoveAll(n => n.Id == gone);

      var res = AgentToolCore.GetComponent(snap, ids, Args("{\"id\":" + gone + "}"));
      Assert.False(res.Ok);
      Assert.Equal(ToolErrors.RetiredId, res.Code);    // NOT unknown_id - a different correction
    }

    [Fact]
    public void Every_id_a_read_returns_resolves_to_a_live_node()
    {
      // The anti-hallucination anchor, checked at the boundary: nothing escapes that the registry
      // cannot resolve back to something on the canvas.
      var (snap, ids) = Fixture();
      var overview = Ok(AgentToolCore.GetOverview(snap));

      foreach (var c in overview["regions"].AsArray())
      {
        var region = Ok(Region(snap, "{\"id\":" + c["id"].GetValue<int>() + "}"));
        foreach (var n in region["nodes"].AsArray())
        {
          var id = n["id"].GetValue<int>();
          Assert.Equal(IdState.Alive, ids.State(id));
          Assert.NotNull(snap.NodeById(id));
        }
      }
    }

    // ---- get_component / get_connections --------------------------------------------------------

    [Fact]
    public void Component_carries_ports_messages_and_out_counts()
    {
      var (snap, ids) = Fixture();
      var loft = snap.Nodes.First(n => n.Type == "Loft").Id;
      var res = Ok(AgentToolCore.GetComponent(snap, ids, Args("{\"id\":" + loft + "}")));

      Assert.Equal("Loft", res["type"].GetValue<string>());
      Assert.Equal(2, res["inputs"].AsArray().Count);
      Assert.Equal("warning", res["msgs"].AsArray()[0]["level"].GetValue<string>());
      Assert.Equal(1, res["outCounts"].AsArray()[0].GetValue<int>());
    }

    [Fact]
    public void Connections_split_upstream_from_downstream()
    {
      var (snap, ids) = Fixture();
      var divide = snap.Nodes.First(n => n.Type == "Divide Curve").Id;
      var res = Ok(AgentToolCore.GetConnections(snap, ids, Args("{\"id\":" + divide + "}")));

      Assert.Equal(1, res["upstream"].AsArray().Count);
      Assert.Equal(1, res["downstream"].AsArray().Count);
      Assert.Equal("Count", res["upstream"].AsArray()[0]["to"]["param"].GetValue<string>());
    }

    // ---- get_tree_signature ---------------------------------------------------------------------

    [Fact]
    public void Tree_signature_is_addressed_by_the_from_to_quad()
    {
      var (snap, ids) = Fixture();
      var d = snap.Nodes.First(n => n.Type == "Divide Curve").Id;
      var l = snap.Nodes.First(n => n.Type == "Loft").Id;

      var res = Ok(AgentToolCore.GetTreeSignature(snap, ids, Args(
        "{\"from\":{\"node\":" + d + ",\"param\":\"Points\"},\"to\":{\"node\":" + l + ",\"param\":\"Curves\"}}")));

      Assert.Equal(7, res["paths"].GetValue<int>());
      Assert.Equal(2, res["depth"].GetValue<int>());
      Assert.Equal(7, res["items"].AsArray().Count);
    }

    [Fact]
    public void A_wire_that_does_not_exist_is_refused_with_the_wires_that_do()
    {
      var (snap, ids) = Fixture();
      var d = snap.Nodes.First(n => n.Type == "Divide Curve").Id;
      var l = snap.Nodes.First(n => n.Type == "Loft").Id;

      var res = AgentToolCore.GetTreeSignature(snap, ids, Args(
        "{\"from\":{\"node\":" + d + ",\"param\":\"Tangents\"},\"to\":{\"node\":" + l + ",\"param\":\"Curves\"}}"));

      Assert.False(res.Ok);
      // The refusal hands back the real wires between the two nodes - a repair instruction, not a wall.
      Assert.Single(res.Detail["wiresBetweenTheseNodes"].AsArray());
    }

    [Fact]
    public void Tree_signature_requires_both_ends()
    {
      var (snap, ids) = Fixture();
      var res = AgentToolCore.GetTreeSignature(snap, ids, Args("{\"from\":{\"node\":1,\"param\":\"N\"}}"));
      Assert.False(res.Ok);
      Assert.Equal(ToolErrors.InvalidArgs, res.Code);
    }

    // ---- search ----------------------------------------------------------------------------------

    [Fact]
    public void Search_matches_by_exact_type_and_by_substring_over_type_or_nickname()
    {
      var (snap, _) = Fixture();

      // exact type
      Assert.Equal(1, Ok(AgentToolCore.SearchNodes(snap, Args("{\"type\":\"Loft\"}")))["count"].GetValue<int>());

      // substring over the NICKNAME ("divider" appears in no type name)
      var byNick = Ok(AgentToolCore.SearchNodes(snap, Args("{\"query\":\"divider\"}")));
      Assert.Equal(1, byNick["count"].GetValue<int>());
      Assert.Equal("Divide Curve", byNick["matches"].AsArray()[0]["type"].GetValue<string>());

      // substring over the TYPE, case-insensitively
      Assert.Equal(1, Ok(AgentToolCore.SearchNodes(snap, Args("{\"query\":\"CURVE\"}")))["count"].GetValue<int>());

      // a query matching neither returns an honest empty set, not an error
      Assert.Equal(0, Ok(AgentToolCore.SearchNodes(snap, Args("{\"query\":\"brep\"}")))["count"].GetValue<int>());
    }

    [Fact]
    public void Search_presents_the_qualified_form_where_a_bare_name_is_ambiguous_and_says_so()
    {
      // U25 (#915): two Rectangles on the canvas - a param and a component - share a bare type name. Each match
      // carries the form that names it, the payload says what the form is, and an unambiguous match carries nothing.
      var ids = new AgentIdRegistry();
      ids.Sync(new[] { G(1), G(2), G(3) });
      var snap = new AgentSnapshot
      {
        Nodes =
        {
          new AgentNode { Id = ids.Issue(G(1)), Type = "Rectangle", Kind = "param", Qualified = "Params > Geometry > Rectangle" },
          new AgentNode { Id = ids.Issue(G(2)), Type = "Rectangle", Kind = "component", Qualified = "Curve > Primitive > Rectangle" },
          new AgentNode { Id = ids.Issue(G(3)), Type = "Loft", Kind = "component" },
        },
      };

      var res = Ok(AgentToolCore.SearchNodes(snap, Args("{\"type\":\"Rectangle\"}")));
      Assert.Equal(2, res["count"].GetValue<int>());
      var forms = res["matches"].AsArray().Select(m => m["qualified"].GetValue<string>()).OrderBy(s => s).ToArray();
      Assert.Equal(new[] { "Curve > Primitive > Rectangle", "Params > Geometry > Rectangle" }, forms);
      Assert.Contains("Category > SubCategory > Name", res["means"]["qualified"].GetValue<string>());
      Assert.Contains("build_region", res["means"]["qualified"].GetValue<string>());

      var loft = Ok(AgentToolCore.SearchNodes(snap, Args("{\"type\":\"Loft\"}")));
      Assert.False(loft["matches"].AsArray()[0].AsObject().ContainsKey("qualified"));
      Assert.False(loft["means"].AsObject().ContainsKey("qualified"));

      // get_component carries the same form and the same sentence.
      var comp = Ok(AgentToolCore.GetComponent(snap, ids, Args("{\"id\":" + snap.Nodes[1].Id + "}")));
      Assert.Equal("Curve > Primitive > Rectangle", comp["qualified"].GetValue<string>());
      Assert.Equal(AgentToolCore.QualifiedMeans, comp["means"]["qualified"].GetValue<string>());
    }

    [Fact]
    public void Search_accepts_the_qualified_form_as_type_and_matches_only_that_one()
    {
      var ids = new AgentIdRegistry();
      ids.Sync(new[] { G(1), G(2) });
      var snap = new AgentSnapshot
      {
        Nodes =
        {
          new AgentNode { Id = ids.Issue(G(1)), Type = "Rectangle", Kind = "param", Qualified = "Params > Geometry > Rectangle" },
          new AgentNode { Id = ids.Issue(G(2)), Type = "Rectangle", Kind = "component", Qualified = "Curve > Primitive > Rectangle" },
        },
      };
      var res = Ok(AgentToolCore.SearchNodes(snap, Args("{\"type\":\"Curve>Primitive>Rectangle\"}")));   // any spacing
      Assert.Equal(1, res["count"].GetValue<int>());
      Assert.Equal("component", res["matches"].AsArray()[0]["kind"]?.GetValue<string>() ?? "component");
      Assert.Equal(snap.Nodes[1].Id, res["matches"].AsArray()[0]["id"].GetValue<int>());

      // A qualified form nothing on the canvas carries is an honest empty set, not a fall-back to the bare name.
      Assert.Equal(0, Ok(AgentToolCore.SearchNodes(snap, Args("{\"type\":\"Surface > Freeform > Rectangle\"}")))["count"].GetValue<int>());
    }

    [Fact]
    public void Search_requires_a_criterion()
    {
      var (snap, _) = Fixture();
      var res = AgentToolCore.SearchNodes(snap, Args("{}"));
      Assert.False(res.Ok);
      Assert.Equal(ToolErrors.InvalidArgs, res.Code);
    }

    [Fact]
    public void Search_announces_truncation_rather_than_capping_silently()
    {
      // #134: a silent cap reads as "that is all there is". It must never.
      var ids = new AgentIdRegistry();
      var snap = new AgentSnapshot();
      var guids = Enumerable.Range(1, AgentToolCore.SearchCap + 25).Select(G).ToList();
      ids.Sync(guids);
      foreach (var g in guids)
        snap.Nodes.Add(new AgentNode { Id = ids.Issue(g), Type = "Panel", Kind = "param" });

      var res = Ok(AgentToolCore.SearchNodes(snap, Args("{\"type\":\"Panel\"}")));
      Assert.Equal(AgentToolCore.SearchCap + 25, res["count"].GetValue<int>());   // the TRUE total
      Assert.True(res["truncated"].GetValue<bool>());
      Assert.Equal(AgentToolCore.SearchCap, res["shown"].GetValue<int>());
    }

    // ---- argument handling + the envelope --------------------------------------------------------

    [Fact]
    public void Malformed_json_is_a_coded_refusal_not_a_throw()
    {
      var args = AgentToolCore.ParseArgs("{not json", out var err);
      Assert.Null(args);
      Assert.Equal(ToolErrors.InvalidArgs, err.Code);
    }

    [Fact]
    public void A_non_object_argument_payload_is_refused()
    {
      AgentToolCore.ParseArgs("[1,2,3]", out var err);
      Assert.Equal(ToolErrors.InvalidArgs, err.Code);
    }

    [Fact]
    public void Empty_arguments_parse_to_an_empty_object()
    {
      Assert.NotNull(AgentToolCore.ParseArgs("", out var err));
      Assert.Null(err);
    }

    [Fact]
    public void A_wrongly_typed_id_is_refused_with_invalid_args()
    {
      var (snap, ids) = Fixture();
      var res = AgentToolCore.GetComponent(snap, ids, Args("{\"id\":\"the loft one\"}"));
      Assert.False(res.Ok);
      Assert.Equal(ToolErrors.InvalidArgs, res.Code);
    }

    [Fact]
    public void A_missing_required_argument_names_the_field()
    {
      var (snap, ids) = Fixture();
      var res = AgentToolCore.GetComponent(snap, ids, Args("{}"));
      Assert.Equal(ToolErrors.InvalidArgs, res.Code);
      Assert.Contains("id", res.Message);
    }

    [Fact]
    public void The_envelope_shape_is_uniform_for_success_and_refusal()
    {
      var (snap, ids) = Fixture();

      var ok = JsonNode.Parse(AgentToolCore.GetOverview(snap).ToJson());
      Assert.True(ok["ok"].GetValue<bool>());
      Assert.NotNull(ok["result"]);

      var bad = JsonNode.Parse(AgentToolCore.GetComponent(snap, ids, Args("{\"id\":9999}")).ToJson());
      Assert.False(bad["ok"].GetValue<bool>());
      Assert.Equal(ToolErrors.UnknownId, bad["error"]["code"].GetValue<string>());
      Assert.NotNull(bad["error"]["message"]);
    }

    [Fact]
    public void An_unknown_region_id_lists_the_regions_that_exist()
    {
      var (snap, _) = Fixture();
      var res = Region(snap, "{\"id\":77}");
      Assert.False(res.Ok);
      Assert.Equal(3, res.Detail["known"].AsArray().Count);
    }

    [Fact]
    public void Region_separates_interior_edges_from_the_boundary()
    {
      var (snap, _) = Fixture();
      var res = Ok(Region(snap, "{\"id\":0}"));

      Assert.Equal(2, res["nodes"].AsArray().Count);
      Assert.Equal(1, res["edges"].AsArray().Count);                        // slider -> divide
      Assert.Equal(1, res["boundary"]["outputs"].AsArray().Count);          // divide -> loft leaves
      Assert.Equal(0, res["boundary"]["inputs"].AsArray().Count);
    }
  }
}
