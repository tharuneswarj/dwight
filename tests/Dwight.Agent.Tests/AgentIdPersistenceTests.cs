using System;
using System.Linq;
using Dwight.Agent;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// The id registry across a restart (#229).
  ///
  /// It matters because a stored scaffold map holds AGENT IDS in its anchors and WP-4 builds from that
  /// map: a restored map resolving against a fresh id space would point at different components - the
  /// mis-grounding the registry exists to prevent, arriving through the one door it left open.
  /// </summary>
  public class AgentIdPersistenceTests
  {
    private static Guid G(int n) { var b = new byte[16]; b[0] = (byte)n; b[1] = (byte)(n >> 8); return new Guid(b); }

    [Fact]
    public void An_id_still_means_the_same_component_after_a_restart()
    {
      var first = new AgentIdRegistry();
      first.Sync(new[] { G(1), G(2), G(3) });
      var idOfTwo = first.Issue(G(2));

      var reopened = new AgentIdRegistry();
      reopened.FromJson(first.ToJson());
      reopened.Sync(new[] { G(1), G(2), G(3) });   // the same canvas, reopened

      Assert.True(reopened.TryGetGuid(idOfTwo, out var guid));
      Assert.Equal(G(2), guid);
      Assert.Equal(IdState.Alive, reopened.State(idOfTwo));
    }

    [Fact]
    public void A_component_deleted_between_sessions_reports_as_retired_not_unknown()
    {
      // The honest answer the registry already gives, now surviving a restart: "that was deleted" is a
      // different correction from "you made that up", and a stored map's anchor deserves the true one.
      var first = new AgentIdRegistry();
      first.Sync(new[] { G(1), G(2) });
      var gone = first.Issue(G(2));

      var reopened = new AgentIdRegistry();
      reopened.FromJson(first.ToJson());
      reopened.Sync(new[] { G(1) });               // G(2) was deleted while the file was closed

      Assert.Equal(IdState.Retired, reopened.State(gone));
      Assert.True(reopened.TryGetGuid(gone, out var guid));
      Assert.Equal(G(2), guid);
    }

    [Fact]
    public void Aliveness_is_never_restored_from_the_file()
    {
      // Whether an object is on the canvas is a fact about the CANVAS. A file edited between sessions
      // must not be able to leave a stale "alive" behind - so a restored registry knows nothing until it
      // has Synced.
      var first = new AgentIdRegistry();
      first.Sync(new[] { G(1) });
      var id = first.Issue(G(1));

      var reopened = new AgentIdRegistry();
      reopened.FromJson(first.ToJson());

      Assert.Equal(IdState.Retired, reopened.State(id));   // pinned, but not yet seen on any canvas
      reopened.Sync(new[] { G(1) });
      Assert.Equal(IdState.Alive, reopened.State(id));
    }

    [Fact]
    public void New_components_continue_the_sequence_rather_than_reusing_a_number()
    {
      // A reset counter would hand an old number to a new object, which is precisely the mis-grounding
      // the whole registry exists to prevent.
      var first = new AgentIdRegistry();
      first.Sync(new[] { G(1), G(2), G(3) });

      var reopened = new AgentIdRegistry();
      reopened.FromJson(first.ToJson());
      var fresh = reopened.Issue(G(99));

      Assert.True(fresh > 3);
      Assert.True(reopened.TryGetGuid(1, out var one));
      Assert.Equal(G(1), one);
    }

    [Fact]
    public void A_truncated_file_can_never_cause_a_number_to_be_reused()
    {
      // The counter continues past the highest id RESTORED even when the stored counter lags - a
      // hand-edited or clipped file must degrade to "fewer ids remembered", never to "an id means two
      // different things".
      var reopened = new AgentIdRegistry();
      reopened.FromJson("{\"v\":1,\"next\":2,\"ids\":[{\"i\":7,\"g\":\"" + G(7) + "\"}]}");

      var fresh = reopened.Issue(G(50));
      Assert.True(fresh > 7);
    }

    [Fact]
    public void Over_the_cap_the_oldest_retired_go_and_a_live_one_never_does()
    {
      // Dropping a retired id degrades its answer from "that was deleted" to "you invented that" - less
      // informative, never wrong. Dropping a LIVE one would unbind an id a stored map still points at.
      var registry = new AgentIdRegistry();
      registry.Sync(Enumerable.Range(1, 10).Select(G));           // ids 1..10
      registry.Sync(new[] { G(9), G(10) });                       // 1..8 retired, 9 and 10 alive

      var snapshot = registry.Export(cap: 4);
      var kept = snapshot.Ids.Select(e => e.Id).ToList();

      Assert.Equal(4, kept.Count);
      Assert.Contains(registry.Issue(G(9)), kept);                 // both live ids survive
      Assert.Contains(registry.Issue(G(10)), kept);
      Assert.DoesNotContain(1, kept);                              // and the oldest retired went first
      Assert.DoesNotContain(2, kept);
    }

    [Fact]
    public void A_live_id_survives_even_when_live_ids_alone_exceed_the_cap()
    {
      var registry = new AgentIdRegistry();
      registry.Sync(Enumerable.Range(1, 10).Select(G));

      var snapshot = registry.Export(cap: 3);

      Assert.Equal(10, snapshot.Ids.Count);   // the cap yields rather than unbinding something live
    }

    [Fact]
    public void Unreadable_stored_state_restores_nothing_and_does_not_throw()
    {
      // A lost id space costs a re-read; a wrongly-restored one costs a wrong action. So doubt reads as
      // "remember nothing".
      var registry = new AgentIdRegistry();
      registry.FromJson("not json at all");
      registry.FromJson("");
      registry.FromJson(null);

      Assert.Equal(0, registry.Issued);
      Assert.Equal(1, registry.Issue(G(1)));   // and it still works
    }

    [Fact]
    public void A_stored_entry_never_re_binds_an_id_already_in_use()
    {
      // Import is additive and defensive: a file claiming id 1 for a different guid than the registry has
      // must not be allowed to move it.
      var registry = new AgentIdRegistry();
      var one = registry.Issue(G(1));

      registry.FromJson("{\"v\":1,\"next\":9,\"ids\":[{\"i\":" + one + ",\"g\":\"" + G(42) + "\"}]}");

      Assert.True(registry.TryGetGuid(one, out var guid));
      Assert.Equal(G(1), guid);
    }
  }
}
