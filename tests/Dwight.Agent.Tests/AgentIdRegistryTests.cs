using System;
using System.Collections.Generic;
using System.Linq;
using Dwight.Agent;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// Ruling #191 - the append-only id registry, encoded. These tests exist because the failure they
  /// prevent is INVISIBLE at runtime: a reshuffled ordinal makes the agent take a confident, wrong
  /// action on the wrong component. There is no exception to catch and no error to report, so the
  /// property has to be proven here.
  ///
  /// The simulation mirrors what build_region actually does to a canvas: nodes are ADDED (new guids
  /// appear), nodes MOVE (pivot order changes - which is precisely what reshuffles DwightGraph's
  /// ordinals), and nodes are DELETED (guids vanish, possibly returning on an undo).
  /// </summary>
  public class AgentIdRegistryTests
  {
    private static Guid G(int n)
    {
      var b = new byte[16];
      b[0] = (byte)n; b[1] = (byte)(n >> 8);
      return new Guid(b);
    }

    [Fact]
    public void Issue_is_idempotent_per_guid()
    {
      var r = new AgentIdRegistry();
      var first = r.Issue(G(1));
      Assert.Equal(first, r.Issue(G(1)));
      Assert.Equal(first, r.Issue(G(1)));
      Assert.Equal(1, r.Issued);
    }

    [Fact]
    public void Distinct_guids_get_distinct_ids()
    {
      var r = new AgentIdRegistry();
      var a = r.Issue(G(1));
      var b = r.Issue(G(2));
      Assert.NotEqual(a, b);
    }

    // ---- THE CORE PROPERTY: stability across a mutation ------------------------------------------

    [Fact]
    public void Ids_survive_a_mutation_that_reshuffles_snapshot_ordinals()
    {
      // Before: three nodes. DwightGraph would number these 1,2,3 by pivot order.
      var r = new AgentIdRegistry();
      r.Sync(new[] { G(10), G(20), G(30) });
      var idA = r.Issue(G(10));
      var idB = r.Issue(G(20));
      var idC = r.Issue(G(30));

      // build_region runs: a new node is added AND the existing nodes move, so the next snapshot's
      // pivot ordering is completely different. This is the exact scenario #191 names.
      r.Sync(new[] { G(30), G(99), G(10), G(20) });

      // Every pre-existing id STILL points at the same object.
      Assert.Equal(idA, r.Issue(G(10)));
      Assert.Equal(idB, r.Issue(G(20)));
      Assert.Equal(idC, r.Issue(G(30)));

      // ...and the new node got a genuinely NEW id, not one of theirs.
      var idNew = r.Issue(G(99));
      Assert.DoesNotContain(idNew, new[] { idA, idB, idC });
    }

    [Fact]
    public void A_new_node_never_receives_a_previously_issued_id()
    {
      var r = new AgentIdRegistry();
      r.Sync(new[] { G(1), G(2), G(3) });
      var issued = new[] { r.Issue(G(1)), r.Issue(G(2)), r.Issue(G(3)) };

      // Delete everything, then add three brand-new nodes.
      r.Sync(Array.Empty<Guid>());
      r.Sync(new[] { G(7), G(8), G(9) });

      var fresh = new[] { r.Issue(G(7)), r.Issue(G(8)), r.Issue(G(9)) };
      Assert.Empty(fresh.Intersect(issued));      // no reissue, ever
    }

    // ---- deletion: retired, not forgotten --------------------------------------------------------

    [Fact]
    public void A_deleted_nodes_id_is_retired_and_reports_as_such()
    {
      var r = new AgentIdRegistry();
      r.Sync(new[] { G(1), G(2) });
      var id = r.Issue(G(1));
      Assert.Equal(IdState.Alive, r.State(id));

      r.Sync(new[] { G(2) });                     // node 1 deleted
      Assert.Equal(IdState.Retired, r.State(id));
    }

    [Fact]
    public void Retired_and_unknown_are_distinguishable()
    {
      // They call for different corrections: "that was deleted" vs "you made that up".
      var r = new AgentIdRegistry();
      r.Sync(new[] { G(1) });
      var id = r.Issue(G(1));
      r.Sync(Array.Empty<Guid>());

      Assert.Equal(IdState.Retired, r.State(id));
      Assert.Equal(IdState.Unknown, r.State(9999));
    }

    [Fact]
    public void A_retired_id_still_resolves_to_its_original_guid()
    {
      var r = new AgentIdRegistry();
      r.Sync(new[] { G(42) });
      var id = r.Issue(G(42));
      r.Sync(Array.Empty<Guid>());

      Assert.True(r.TryGetGuid(id, out var guid));
      Assert.Equal(G(42), guid);                  // the pin outlives the object
    }

    [Fact]
    public void An_undone_then_redone_node_reclaims_its_original_id()
    {
      // Every Dwight write is one Ctrl-Z, so vanish-and-return is the ORDINARY case, not an edge.
      // Returning the same id is the pin holding - the id has never addressed another object.
      var r = new AgentIdRegistry();
      r.Sync(new[] { G(5) });
      var before = r.Issue(G(5));

      r.Sync(Array.Empty<Guid>());                // Ctrl-Z
      Assert.Equal(IdState.Retired, r.State(before));

      r.Sync(new[] { G(5) });                     // Ctrl-Y
      Assert.Equal(before, r.Issue(G(5)));
      Assert.Equal(IdState.Alive, r.State(before));
    }

    [Fact]
    public void Retiring_an_unknown_id_is_a_no_op_rather_than_a_throw()
    {
      var r = new AgentIdRegistry();
      r.Retire(4242);                             // fail safe (ADR-029)
      Assert.Equal(IdState.Unknown, r.State(4242));
    }

    // ---- enumeration ------------------------------------------------------------------------------

    [Fact]
    public void AliveIds_is_ascending_and_excludes_retired()
    {
      var r = new AgentIdRegistry();
      r.Sync(new[] { G(1), G(2), G(3) });
      var dead = r.Issue(G(2));
      r.Sync(new[] { G(1), G(3) });

      var alive = r.AliveIds();
      Assert.DoesNotContain(dead, alive);
      Assert.Equal(alive.OrderBy(i => i), alive);
    }

    [Fact]
    public void Sync_tolerates_a_null_guid_list()
    {
      var r = new AgentIdRegistry();
      r.Sync(new[] { G(1) });
      r.Sync(null);                               // fail safe: everything retires, nothing throws
      Assert.Empty(r.AliveIds());
    }

    [Fact]
    public void Issued_count_never_decreases()
    {
      var r = new AgentIdRegistry();
      r.Sync(new[] { G(1), G(2) });
      var after = r.Issued;
      r.Sync(Array.Empty<Guid>());
      Assert.Equal(after, r.Issued);              // append-only
    }
  }
}
