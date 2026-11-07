using System;
using System.Linq;
using System.Text.Json.Nodes;
using Dwight.Agent;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// Ruling #195 - the partition stamp. Region ids are an index into a segmentation recomputed from the
  /// live topology, so after a rewiring "region 2" can name a DIFFERENT region: the #191 hazard's shape
  /// one layer up. The plan shipped a sentence in the tool description; the ruling replaced it with a
  /// computed guard, because a deterministic failure class the plugin can compute is the plugin's job,
  /// never a prompt's (#141/#146).
  ///
  /// The stamp's validity scope has to match the partition's exactly - which is why it is topology-only:
  /// segmentation ignores position (ADR-087, scattered==tidy), so a PURE MOVE must not cost the agent
  /// its region ids, while any rewiring must.
  /// </summary>
  public class PartitionStampTests
  {
    private static Guid G(int n) { var b = new byte[16]; b[0] = (byte)n; b[1] = (byte)(n >> 8); return new Guid(b); }

    private static (AgentSnapshot snap, AgentIdRegistry ids) Graph(bool rewired = false, string retypedNode = null)
    {
      var ids = new AgentIdRegistry();
      ids.Sync(new[] { G(1), G(2), G(3) });
      int a = ids.Issue(G(1)), b = ids.Issue(G(2)), c = ids.Issue(G(3));

      var snap = new AgentSnapshot
      {
        Nodes =
        {
          new AgentNode { Id = a, Type = "Number Slider", Kind = "param" },
          new AgentNode { Id = b, Type = retypedNode ?? "Divide Curve", Kind = "component" },
          new AgentNode { Id = c, Type = "Loft", Kind = "component" },
        },
        Clusters =
        {
          new AgentCluster { Id = 0, Label = "Divide", Members = { a, b } },
          new AgentCluster { Id = 1, Label = "Loft", Members = { c } },
        },
      };

      snap.Edges.Add(new AgentEdgeDto { FromNode = a, FromParam = "N", ToNode = b, ToParam = "Count" });
      // The rewire: the second wire lands on a DIFFERENT input port.
      snap.Edges.Add(new AgentEdgeDto
      {
        FromNode = b, FromParam = "Points", ToNode = c, ToParam = rewired ? "Options" : "Curves",
      });
      return (snap, ids);
    }

    private static JsonObject Args(string json) => (JsonObject)JsonNode.Parse(json);

    // ---- what the stamp is sensitive to ---------------------------------------------------------

    [Fact]
    public void The_stamp_is_stable_for_an_unchanged_graph()
    {
      var (a, _) = Graph();
      var (b, _) = Graph();
      Assert.Equal(AgentToolCore.ShapeHash(a), AgentToolCore.ShapeHash(b));
    }

    [Fact]
    public void A_pure_move_does_not_change_the_stamp()
    {
      // The snapshot DTOs carry no position at all, and ids are guid-pinned - so a move is INVISIBLE
      // here by construction. That is the property that makes tidying safe for the agent's region ids.
      var (snap, _) = Graph();
      var before = AgentToolCore.ShapeHash(snap);

      // Re-project the same graph in a different enumeration order: nodes and edges shuffled, nothing
      // rewired. This is what a moved canvas looks like to a topology-only stamp.
      var moved = new AgentSnapshot();
      moved.Nodes.AddRange(snap.Nodes.AsEnumerable().Reverse());
      moved.Edges.AddRange(snap.Edges.AsEnumerable().Reverse());

      Assert.Equal(before, AgentToolCore.ShapeHash(moved));
    }

    [Fact]
    public void A_rewiring_changes_the_stamp()
    {
      var (before, _) = Graph();
      var (after, _) = Graph(rewired: true);
      Assert.NotEqual(AgentToolCore.ShapeHash(before), AgentToolCore.ShapeHash(after));
    }

    [Fact]
    public void Replacing_a_component_with_a_different_type_changes_the_stamp()
    {
      var (before, _) = Graph();
      var (after, _) = Graph(retypedNode: "Divide Length");
      Assert.NotEqual(AgentToolCore.ShapeHash(before), AgentToolCore.ShapeHash(after));
    }

    [Fact]
    public void Adding_a_component_changes_the_stamp()
    {
      var (snap, ids) = Graph();
      var before = AgentToolCore.ShapeHash(snap);

      ids.Sync(new[] { G(1), G(2), G(3), G(4) });
      snap.Nodes.Add(new AgentNode { Id = ids.Issue(G(4)), Type = "Panel", Kind = "param" });

      Assert.NotEqual(before, AgentToolCore.ShapeHash(snap));
    }

    // ---- the guard --------------------------------------------------------------------------------

    [Fact]
    public void Overview_carries_the_partition_stamp()
    {
      var (snap, _) = Graph();
      var stamp = AgentToolCore.ShapeHash(snap);
      var res = AgentToolCore.GetOverview(snap, stamp);

      Assert.True(res.Ok);
      Assert.Equal(stamp, res.Result["partition"].GetValue<string>());
    }

    [Fact]
    public void A_region_read_against_a_matching_stamp_succeeds()
    {
      var (snap, _) = Graph();
      var res = AgentToolCore.GetRegion(snap, Args("{\"id\":0}"), AgentToolCore.ShapeHash(snap));
      Assert.True(res.Ok, res.Code);
    }

    [Fact]
    public void A_topology_change_between_overview_and_get_region_REFUSES()
    {
      // THE RULED CASE. The stamp was taken on the old wiring; the canvas has since been rewired, so
      // region 0 may no longer be the region the agent read about. Refuse - never silently different.
      var (before, _) = Graph();
      var staleStamp = AgentToolCore.ShapeHash(before);

      var (after, _) = Graph(rewired: true);
      var res = AgentToolCore.GetRegion(after, Args("{\"id\":0}"), staleStamp);

      Assert.False(res.Ok);
      Assert.Equal(ToolErrors.StalePartition, res.Code);
      Assert.Contains("get_overview again", res.Message);   // the refusal says how to recover
    }

    [Fact]
    public void A_pure_move_between_overview_and_get_region_does_NOT_refuse()
    {
      // The other half of the ruling: tidying must not invalidate region ids, or the agent would be
      // forced to re-segment after every cosmetic change.
      var (snap, _) = Graph();
      var stamp = AgentToolCore.ShapeHash(snap);

      var moved = new AgentSnapshot();
      moved.Nodes.AddRange(snap.Nodes.AsEnumerable().Reverse());
      moved.Edges.AddRange(snap.Edges.AsEnumerable().Reverse());
      moved.Clusters.AddRange(snap.Clusters);

      var res = AgentToolCore.GetRegion(moved, Args("{\"id\":0}"), stamp);
      Assert.True(res.Ok, res.Code);
    }

    [Fact]
    public void A_region_read_with_no_partition_computed_yet_refuses_and_says_so()
    {
      var (snap, _) = Graph();
      var res = AgentToolCore.GetRegion(snap, Args("{\"id\":0}"), expectedStamp: null);

      Assert.False(res.Ok);
      Assert.Equal(ToolErrors.StalePartition, res.Code);
      Assert.Contains("get_overview first", res.Message);
    }

    [Fact]
    public void The_stale_check_runs_before_the_id_is_interpreted()
    {
      // Order matters: against a stale partition, even a "valid-looking" id is meaningless, so the
      // partition refusal must win rather than the tool answering about the wrong region.
      var (before, _) = Graph();
      var staleStamp = AgentToolCore.ShapeHash(before);
      var (after, _) = Graph(rewired: true);

      var res = AgentToolCore.GetRegion(after, Args("{\"id\":1}"), staleStamp);
      Assert.Equal(ToolErrors.StalePartition, res.Code);
    }
  }
}
