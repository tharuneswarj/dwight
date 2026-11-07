using System;
using System.Linq;
using System.Text.Json.Nodes;
using Dwight.Agent;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// #719: A MEMOISED INDEX, BUILT HALFWAY THROUGH THE THING IT INDEXES.
  ///
  /// THE FIELD CASE. Tharun's canvas held a cluster with a GhPython component inside it. `get_cluster` listed that
  /// component; `get_component` on its id refused it as RETIRED - "has since been deleted from the canvas" - in the
  /// same instant, every time. Two readers disagreeing about one object, which is the one-read-path rule failing in the
  /// only way that matters.
  ///
  /// THE MECHANISM, measured on that canvas before it was written down: the registry held the id ALIVE, the snapshot's
  /// node list CONTAINED it, and `snap.NodeById(id)` returned null for it. `AgentSnapshot.NodeById` memoised its
  /// dictionary, and `GhAgent.AddInternals` called it - to hang the cluster's DTO on its owner - BEFORE appending the
  /// cluster's internals. The index froze with the top-level nodes, and every internal node appended afterwards was
  /// invisible to it. Not a stale snapshot, and not a stale registry: a stale INDEX inside a fresh snapshot.
  ///
  /// The fix is structural rather than a check: the collection owns the invalidation, so an append cannot happen behind
  /// the index's back. These tests hold both the mechanism and the three readers it broke.
  /// </summary>
  public class SnapshotIndexTests
  {
    private static Guid G(int n) { var b = new byte[16]; b[0] = (byte)n; b[1] = (byte)(n >> 8); return new Guid(b); }

    private static AgentNode Node(int id, string type = "Panel", int? inside = null) =>
      new AgentNode { Id = id, Type = type, Kind = "component", Inside = inside };

    // ---- the mechanism -------------------------------------------------------------------------------------

    /// <summary>THE EXACT TRAP: look something up, THEN append, and the appended node must still be findable.</summary>
    [Fact]
    public void A_node_appended_after_a_lookup_is_found_by_the_next_lookup()
    {
      var snap = new AgentSnapshot();
      snap.Nodes.Add(Node(1, "Cluster"));

      Assert.NotNull(snap.NodeById(1));      // the lookup that used to freeze the index
      snap.Nodes.Add(Node(16, "GhPython Script", inside: 1));

      Assert.NotNull(snap.NodeById(16));
      Assert.Equal(1, snap.NodeById(16).Inside);
    }

    /// <summary>Every mutation path, not only Add - a replacement that keeps the COUNT equal defeats a count check,
    /// which is why the collection tells the snapshot rather than the snapshot guessing.</summary>
    [Fact]
    public void Sorting_and_removing_refresh_the_index_too()
    {
      var snap = new AgentSnapshot();
      snap.Nodes.AddRange(new[] { Node(3), Node(1), Node(2) });
      Assert.NotNull(snap.NodeById(3));

      snap.Nodes.Sort((a, b) => a.Id.CompareTo(b.Id));
      Assert.Equal(1, snap.Nodes.First().Id);
      Assert.NotNull(snap.NodeById(3));

      snap.Nodes.RemoveAll(n => n.Id == 3);
      Assert.Null(snap.NodeById(3));
      Assert.NotNull(snap.NodeById(2));
    }

    /// <summary>A duplicate id is a defect somewhere else; this lookup must not turn it into an unreadable tool call
    /// (#375 - the instrument's own failure is never the subject's).</summary>
    [Fact]
    public void Two_nodes_sharing_an_id_do_not_throw()
    {
      var snap = new AgentSnapshot();
      snap.Nodes.Add(Node(7, "Panel"));
      snap.Nodes.Add(Node(7, "Slider"));

      Assert.NotNull(snap.NodeById(7));
    }

    // ---- the three readers the stale index broke -----------------------------------------------------------

    /// <summary>Built the way GhAgent builds it: the owner lookup happens BEFORE the internals are appended.</summary>
    private static (AgentSnapshot snap, AgentIdRegistry ids, int cluster, int inner) WithCluster()
    {
      var ids = new AgentIdRegistry();
      ids.Sync(new[] { G(1), G(2) });
      var cluster = ids.Issue(G(1));
      var inner = ids.Issue(G(2));

      var snap = new AgentSnapshot();
      snap.Nodes.Add(Node(cluster, "Cluster"));
      var owner = snap.NodeById(cluster);                       // AddInternals' own call, the one that froze the index
      owner.Cluster = new ClusterDto { Count = 1 };
      snap.Nodes.Add(Node(inner, "GhPython Script", inside: cluster));
      snap.Clusters.Add(new AgentCluster { Id = 0, Label = "Cluster 0", Members = { cluster, inner } });
      return (snap, ids, cluster, inner);
    }

    /// <summary>READER 1, the field symptom: get_component on a cluster-internal id answers, and never refuses.</summary>
    [Fact]
    public void Get_component_answers_for_a_cluster_internal_id()
    {
      var (snap, ids, cluster, inner) = WithCluster();

      var res = AgentToolCore.GetComponent(snap, ids, (JsonObject)JsonNode.Parse("{\"id\":" + inner + "}"));

      Assert.True(res.Ok, res.Code + ": " + res.Message);
      Assert.Equal(cluster, res.Result["inside"].GetValue<int>());
    }

    /// <summary>READER 2 (AgentToolCore's IsInside): a cluster-internal node reads as inside, which is what keeps the
    /// internals out of the top-level projections.</summary>
    [Fact]
    public void An_internal_node_reads_as_inside_its_cluster()
    {
      var (snap, _, cluster, inner) = WithCluster();

      Assert.Equal(cluster, snap.NodeById(inner).Inside);
      Assert.Null(snap.NodeById(cluster).Inside);
    }

    /// <summary>READER 3: the overview maps a cluster's members through NodeById, so a stale index silently DROPPED
    /// every internal member from the counts a user reads.</summary>
    [Fact]
    public void The_cluster_member_mapping_keeps_the_internals()
    {
      var (snap, _, _, _) = WithCluster();

      var overview = AgentToolCore.GetOverview(snap);

      Assert.True(overview.Ok, overview.Message);
      var types = overview.Result["regions"].AsArray()[0]["types"].AsObject();
      Assert.Contains("GhPython Script", types.Select(kv => kv.Key));
    }

    // ---- the refusal is a claim, and the two states are different claims (#613's law) ----------------------

    [Fact]
    public void A_deleted_component_is_refused_as_retired_and_says_deleted()
    {
      var ids = new AgentIdRegistry();
      ids.Sync(new[] { G(1) });
      var id = ids.Issue(G(1));
      ids.Sync(new Guid[0]);                                    // the user deleted it

      var res = AgentToolCore.GetComponent(new AgentSnapshot(), ids, (JsonObject)JsonNode.Parse("{\"id\":" + id + "}"));

      Assert.False(res.Ok);
      Assert.Equal(ToolErrors.RetiredId, res.Code);
      Assert.Contains("no longer on the canvas", res.Message);
    }

    /// <summary>
    /// THE STATE THAT LIED. Alive in the registry, absent from this read: the old code refused it as RetiredId, whose
    /// cause says the component "has since been deleted from the canvas" - false twice over, because it was on the
    /// canvas and the reader's own index was what could not see it.
    /// </summary>
    [Fact]
    public void An_id_alive_but_outside_this_read_is_not_reported_as_deleted()
    {
      var ids = new AgentIdRegistry();
      ids.Sync(new[] { G(1) });
      var id = ids.Issue(G(1));                                 // alive...

      var res = AgentToolCore.GetComponent(new AgentSnapshot(), ids, (JsonObject)JsonNode.Parse("{\"id\":" + id + "}"));

      Assert.False(res.Ok);
      Assert.Equal(ToolErrors.NotInThisRead, res.Code);
      Assert.Contains("It was not deleted", res.Message);
      Assert.Contains("get_cluster", res.Message);               // the way forward, not a bare no (ADR-092)
      Assert.DoesNotContain("deleted from the canvas", res.Message);
      Assert.NotEmpty(ToolErrors.CauseOf(ToolErrors.NotInThisRead));
    }
  }
}
