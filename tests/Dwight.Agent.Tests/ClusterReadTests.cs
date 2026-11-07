using System;
using System.Linq;
using System.Text.Json.Nodes;
using Dwight.Agent;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// Phase-9 U1(c): a cluster's internals as a nested region the model can drill into (#382(9); the Q3
  /// ruling: its OWN tool, because component ids and region ids have different lifetimes and must never
  /// share a parameter).
  ///
  /// The GH reader walks each top-level cluster's internal document ONE level and issues registry ids for
  /// the internals, tagged with the cluster they sit inside; this file tests the GH-free projection over
  /// that snapshot. #392 (i): a protected or locked cluster is REPORTED, never read - reading a cluster
  /// its author locked is the failure no message can undo - and the report says why.
  /// </summary>
  public class ClusterReadTests
  {
    private static Guid G(int n) { var b = new byte[16]; b[0] = (byte)n; b[1] = (byte)(n >> 8); return new Guid(b); }

    /// <summary>slider(1) -> cluster(2) -> panel(3); inside the cluster: in-hook(4) -> multiply(5) -> out-hook(6).
    /// Plus a protected cluster(7) and a plain component(8).</summary>
    private static (AgentSnapshot snap, AgentIdRegistry ids, int[] id) Fixture()
    {
      var ids = new AgentIdRegistry();
      var guids = Enumerable.Range(1, 8).Select(G).ToArray();
      ids.Sync(guids);
      var id = guids.Select(g => ids.Issue(g)).ToArray();

      var snap = new AgentSnapshot
      {
        Nodes =
        {
          new AgentNode { Id = id[0], Type = "Number Slider", Kind = "param", Outputs = new[] { "N" }, Constant = "4" },
          new AgentNode { Id = id[1], Type = "Cluster", Kind = "component", Nick = "Doubler", Inputs = new[] { "x" }, Outputs = new[] { "y" },
            Cluster = new ClusterDto { Count = 3 } },
          new AgentNode { Id = id[2], Type = "Panel", Kind = "param", Inputs = new[] { "P" }, Outputs = new[] { "P" } },
          new AgentNode { Id = id[3], Type = "Cluster Input", Kind = "param", Nick = "x", Outputs = new[] { "x" }, Inside = id[1], Hook = "input" },
          new AgentNode { Id = id[4], Type = "Multiplication", Kind = "component", Inputs = new[] { "A", "B" }, Outputs = new[] { "Result" }, Inside = id[1] },
          new AgentNode { Id = id[5], Type = "Cluster Output", Kind = "param", Nick = "y", Inputs = new[] { "y" }, Outputs = new[] { "y" }, Inside = id[1], Hook = "output" },
          new AgentNode { Id = id[6], Type = "Cluster", Kind = "component", Nick = "Secret", Inputs = new[] { "a" }, Outputs = new[] { "b" },
            Cluster = new ClusterDto { Protected = true } },
          new AgentNode { Id = id[7], Type = "Loft", Kind = "component", Inputs = new[] { "Curves" }, Outputs = new[] { "Loft" } },
        },
        Edges =
        {
          new AgentEdgeDto { FromNode = id[0], FromParam = "N", ToNode = id[1], ToParam = "x" },
          new AgentEdgeDto { FromNode = id[1], FromParam = "y", ToNode = id[2], ToParam = "P" },
          new AgentEdgeDto { FromNode = id[3], FromParam = "x", ToNode = id[4], ToParam = "A", Tree = new TreeSigDto { Paths = 1, Depth = 1, Items = new[] { 1 } } },
          new AgentEdgeDto { FromNode = id[4], FromParam = "Result", ToNode = id[5], ToParam = "y" },
        },
        Clusters = { new AgentCluster { Id = 0, Label = "Cluster 0", Members = { id[0], id[1], id[2], id[6], id[7] } } },
      };
      return (snap, ids, id);
    }

    private static JsonObject Args(string json) => (JsonObject)JsonNode.Parse(json);

    [Fact]
    public void GET_CLUSTER_RETURNS_THE_INTERNALS_AS_A_REGION_WITH_ITS_HOOKS()
    {
      var (snap, ids, id) = Fixture();
      var r = AgentToolCore.GetCluster(snap, ids, Args("{\"id\":" + id[1] + "}"));
      Assert.True(r.Ok, r.Code + ": " + r.Message);

      Assert.Equal(id[1], r.Result["id"].GetValue<int>());
      Assert.Equal("Doubler", r.Result["nick"].GetValue<string>());
      Assert.Equal(3, r.Result["count"].GetValue<int>());
      var nodes = ((JsonArray)r.Result["nodes"]).Select(n => n["id"].GetValue<int>()).ToArray();
      Assert.Equal(new[] { id[3], id[4], id[5] }.OrderBy(i => i), nodes.OrderBy(i => i));
      Assert.Equal(2, ((JsonArray)r.Result["edges"]).Count);
      Assert.NotNull(((JsonArray)r.Result["edges"])[0]["sig"]);

      var hooks = r.Result["hooks"];
      Assert.Equal(id[3], hooks["inputs"][0]["id"].GetValue<int>());
      Assert.Equal("x", hooks["inputs"][0]["name"].GetValue<string>());
      Assert.Equal(id[5], hooks["outputs"][0]["id"].GetValue<int>());
      Assert.Null(r.Result["omitted"]);
    }

    [Fact]
    public void A_PROTECTED_CLUSTER_IS_REPORTED_NOT_READ()
    {
      var (snap, ids, id) = Fixture();
      var r = AgentToolCore.GetCluster(snap, ids, Args("{\"id\":" + id[6] + "}"));
      Assert.True(r.Ok);
      Assert.True(r.Result["protected"].GetValue<bool>());
      Assert.False(r.Result.AsObject().ContainsKey("nodes"));
      Assert.Contains("password", r.Result["why"].GetValue<string>(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_CLUSTER_WHOSE_DOCUMENT_COULD_NOT_BE_READ_SAYS_WHY()
    {
      var (snap, ids, id) = Fixture();
      snap.NodeById(id[1]).Cluster = new ClusterDto { Unreadable = "Document(null) returned null" };
      var r = AgentToolCore.GetCluster(snap, ids, Args("{\"id\":" + id[1] + "}"));
      Assert.True(r.Ok);
      Assert.Contains("Document(null) returned null", r.Result["why"].GetValue<string>());
      Assert.False(r.Result.AsObject().ContainsKey("nodes"));
    }

    [Fact]
    public void A_LOCKED_STATE_THAT_COULD_NOT_BE_READ_IS_REPORTED_AS_UNMEASURED_NEVER_AS_FALSE()
    {
      // The architect's finding on the review: a read that threw left Locked false and nothing recorded
      // that we failed - a field stating something we did not measure. Now the DTO says "unknown" and why.
      var (snap, ids, id) = Fixture();
      snap.NodeById(id[1]).Cluster = new ClusterDto { Count = 3, LockedUnknown = "Locked threw InvalidOperationException: boom" };

      var r = AgentToolCore.GetCluster(snap, ids, Args("{\"id\":" + id[1] + "}"));
      Assert.Contains("unmeasured", r.Result["locked"].GetValue<string>());
      Assert.Contains("boom", r.Result["locked"].GetValue<string>());

      var c = AgentToolCore.GetComponent(snap, ids, Args("{\"id\":" + id[1] + "}")).Result;
      Assert.Contains("unmeasured", c["cluster"]["locked"].GetValue<string>());

      snap.NodeById(id[1]).Cluster = new ClusterDto { Count = 3 };   // measured and false: nothing to say
      Assert.False(AgentToolCore.GetCluster(snap, ids, Args("{\"id\":" + id[1] + "}")).Result.AsObject().ContainsKey("locked"));
      snap.NodeById(id[1]).Cluster = new ClusterDto { Count = 3, Locked = true };
      Assert.True(AgentToolCore.GetCluster(snap, ids, Args("{\"id\":" + id[1] + "}")).Result["locked"].GetValue<bool>());
    }

    [Fact]
    public void A_NON_CLUSTER_IS_REFUSED_AS_A_PRECONDITION_NOT_AS_MALFORMED_ARGS()
    {
      var (snap, ids, id) = Fixture();
      var r = AgentToolCore.GetCluster(snap, ids, Args("{\"id\":" + id[7] + "}"));
      Assert.False(r.Ok);
      Assert.Equal(ToolErrors.PreconditionRefused, r.Code);
      Assert.Contains("Loft", r.Message);
    }

    [Fact]
    public void A_CAPPED_CLUSTER_STATES_WHAT_IT_OMITTED()
    {
      var (snap, ids, id) = Fixture();
      snap.NodeById(id[1]).Cluster = new ClusterDto { Count = 500, Omitted = 497 };
      var r = AgentToolCore.GetCluster(snap, ids, Args("{\"id\":" + id[1] + "}"));
      Assert.Contains("497", r.Result["omitted"].GetValue<string>());
    }

    [Fact]
    public void GET_COMPONENT_ON_A_CLUSTER_CARRIES_THE_CLUSTER_BLOCK_AND_ON_AN_INTERNAL_SAYS_WHERE_IT_IS()
    {
      var (snap, ids, id) = Fixture();
      var c = AgentToolCore.GetComponent(snap, ids, Args("{\"id\":" + id[1] + "}")).Result;
      Assert.Equal(3, c["cluster"]["count"].GetValue<int>());
      Assert.Contains("get_cluster", c["cluster"]["readWith"].GetValue<string>());

      var inner = AgentToolCore.GetComponent(snap, ids, Args("{\"id\":" + id[4] + "}")).Result;
      Assert.Equal(id[1], inner["inside"].GetValue<int>());
      Assert.False(inner.AsObject().ContainsKey("cluster"));

      var hook = AgentToolCore.GetComponent(snap, ids, Args("{\"id\":" + id[3] + "}")).Result;
      Assert.Equal("input", hook["hook"].GetValue<string>());
    }

    [Fact]
    public void REGION_BRIEFS_HINT_THE_CLUSTER_AND_THE_OVERVIEW_COUNTS_INTERNALS_SEPARATELY()
    {
      var (snap, ids, id) = Fixture();
      var stamp = AgentToolCore.ShapeHash(snap);
      var region = AgentToolCore.GetRegion(snap, Args("{\"id\":0}"), stamp);
      var brief = ((JsonArray)region.Result["nodes"]).Cast<JsonObject>().First(n => n["id"].GetValue<int>() == id[1]);
      Assert.Equal(3, brief["cluster"].GetValue<int>());
      var secret = ((JsonArray)region.Result["nodes"]).Cast<JsonObject>().First(n => n["id"].GetValue<int>() == id[6]);
      Assert.Equal("protected", secret["cluster"].GetValue<string>());

      var overview = AgentToolCore.GetOverview(snap, stamp);
      Assert.Equal(3, overview.Result["counts"]["components"].GetValue<int>());   // top level only: cluster, cluster, loft
      Assert.Equal(2, overview.Result["counts"]["params"].GetValue<int>());
      Assert.Equal(3, overview.Result["counts"]["insideClusters"].GetValue<int>());
      Assert.Equal(2, overview.Result["counts"]["edges"].GetValue<int>());        // internal edges are not canvas edges
    }

    [Fact]
    public void SEARCH_FINDS_INTERNALS_AND_SAYS_WHERE_THEY_ARE()
    {
      var (snap, ids, id) = Fixture();
      var r = AgentToolCore.SearchNodes(snap, Args("{\"type\":\"Multiplication\"}"));
      var m = ((JsonArray)r.Result["matches"])[0];
      Assert.Equal(id[4], m["id"].GetValue<int>());
      Assert.Equal(id[1], m["inside"].GetValue<int>());
    }
  }
}
