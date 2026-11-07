using System;
using System.Collections.Generic;
using System.Linq;
using Dwight.Agent;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// #244(c), OWED SINCE WP-3 AND NEVER EXERCISED: whether a RESTORED scaffold map's anchors still
  /// resolve to the right components.
  ///
  /// The obligation's history is the reason it is worth a file of its own. #229 ruled that the id
  /// registry persists; #244 promoted it from "carried" to LOAD-BEARING the moment the map itself was
  /// persisted, because <c>ScaffoldRegion.AnchorIds</c> holds AGENT IDS and WP-4 builds from that map;
  /// #245 built the persistence; and every WP-3 gate checklist since has said, in its own words, that a
  /// pass does not imply this. It could not be tested before `ChatAnchors` existed to resolve with.
  ///
  /// THE PROPERTY UNDER TEST is the one that matters to a build: after a restart, an anchor either names
  /// the SAME component it named before, or says it is gone. There is no third outcome - no drift to
  /// "whatever occupies that place now" - because an anchor is an id and resolution is
  /// id -> registry -> guid -> in-the-document-now (#291).
  /// </summary>
  public class RestoredMapAnchorTests
  {
    private static readonly Guid Divide = new Guid("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Loft = new Guid("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Panel = new Guid("33333333-3333-3333-3333-333333333333");

    /// <summary>A session that read the canvas, then laid down a map anchored to what it read.</summary>
    private static (string Registry, string Map, int DivideId, int LoftId) LayDownAMap()
    {
      var live = new AgentIdRegistry();
      live.Sync(new[] { Divide, Loft, Panel });
      var divideId = live.Issue(Divide);
      var loftId = live.Issue(Loft);

      var args = "{\"regions\":[" +
                 "{\"ref\":\"prep\",\"name\":\"Prepare curves\",\"purpose\":\"Clean the input\"," +
                 "\"anchorIds\":[" + divideId + "]}," +
                 "{\"ref\":\"roof\",\"name\":\"Loft the roof\",\"purpose\":\"Build it\"," +
                 "\"dependsOn\":[\"prep\"],\"anchorIds\":[" + loftId + "]}]}";

      var verdict = "{\"result\":{\"regions\":[" +
                    "{\"ref\":\"prep\",\"key\":\"A1\",\"name\":\"Prepare curves\",\"group\":\"A1 - Prepare curves\",\"dependsOn\":[]}," +
                    "{\"ref\":\"roof\",\"key\":\"B1\",\"name\":\"Loft the roof\",\"group\":\"B1 - Loft the roof\",\"dependsOn\":[\"A1\"]}]}}";

      return (live.ToJson(), ScaffoldMapRecord.Build(args, verdict), divideId, loftId);
    }

    private static List<CanvasRegion> Canvas() => new List<CanvasRegion>
    {
      new CanvasRegion { Address = "A1", Name = "Prepare curves", ComponentCount = 0 },
      new CanvasRegion { Address = "B1", Name = "Loft the roof", ComponentCount = 0 },
    };

    /// <summary>THE HEADLINE: reopen, and A1's anchor still means the component it meant.</summary>
    [Fact]
    public void A_restored_map_anchor_resolves_to_the_SAME_component()
    {
      var (registryJson, mapJson, divideId, _) = LayDownAMap();

      // --- a restart: a brand-new registry, restored from the store, synced against the live canvas ---
      var reopened = new AgentIdRegistry();
      reopened.FromJson(registryJson);
      reopened.Sync(new[] { Divide, Loft, Panel });

      var map = ScaffoldMapRecord.Read(mapJson, Canvas());
      var anchors = map.Regions.Single(r => r.Address == "A1").AnchorIds;

      Assert.Equal(new[] { divideId }, anchors);
      Assert.True(reopened.TryGetGuid(anchors[0], out var guid));
      Assert.Equal(Divide, guid);   // the SAME component, not merely A component

      var resolution = ChatAnchors.Resolve(anchors, id => reopened.State(id));
      Assert.Equal(AnchorOutcome.Landed, resolution.Outcome);
    }

    /// <summary>The component was deleted while the file was closed. The honest answer is "that was
    /// deleted" - never a different guid, and never silence. #245's aliveness-is-never-stored decision is
    /// what makes this work: the restored registry knows only which ids are PINNED to which guids, and
    /// the next Sync decides what is still there.</summary>
    [Fact]
    public void An_anchor_whose_component_was_deleted_between_sessions_says_so()
    {
      var (registryJson, mapJson, divideId, _) = LayDownAMap();

      var reopened = new AgentIdRegistry();
      reopened.FromJson(registryJson);
      reopened.Sync(new[] { Loft, Panel });   // Divide is gone

      var map = ScaffoldMapRecord.Read(mapJson, Canvas());
      var anchors = map.Regions.Single(r => r.Address == "A1").AnchorIds;

      Assert.Equal(new[] { divideId }, anchors);
      Assert.Equal(IdState.Retired, reopened.State(divideId));

      var resolution = ChatAnchors.Resolve(anchors, id => reopened.State(id));
      Assert.Equal(AnchorOutcome.AllRetired, resolution.Outcome);
      Assert.NotEmpty(resolution.Message);
    }

    /// <summary>THE MIS-GROUNDING CASE, and the one this whole apparatus exists to make impossible: the
    /// canvas churned between sessions, so the id a naive scheme would have re-derived now belongs to a
    /// DIFFERENT object. The pinned registry cannot produce that answer - the id is bound to its original
    /// guid for the scope's life, and if that guid is gone the answer is "retired", never a substitute.</summary>
    [Fact]
    public void A_restored_anchor_NEVER_resolves_to_a_component_that_took_its_place()
    {
      var (registryJson, mapJson, divideId, _) = LayDownAMap();
      var newcomer = new Guid("44444444-4444-4444-4444-444444444444");

      var reopened = new AgentIdRegistry();
      reopened.FromJson(registryJson);
      reopened.Sync(new[] { Loft, Panel, newcomer });   // Divide deleted, something else added

      // The newcomer gets its OWN id, and it is not the retired one.
      var newcomerId = reopened.Issue(newcomer);
      Assert.NotEqual(divideId, newcomerId);

      var map = ScaffoldMapRecord.Read(mapJson, Canvas());
      var anchors = map.Regions.Single(r => r.Address == "A1").AnchorIds;

      Assert.False(reopened.TryGetGuid(anchors[0], out var stillThere) &&
                   stillThere == newcomer);
      Assert.Equal(IdState.Retired, reopened.State(anchors[0]));
    }

    /// <summary>A partial loss frames what survives AND states what did not - never quietly fewer
    /// (#291). A build addressed at a region whose anchors are half gone must know which half.</summary>
    [Fact]
    public void A_partly_retired_anchor_set_reports_the_loss()
    {
      var (registryJson, mapJson, divideId, loftId) = LayDownAMap();

      var reopened = new AgentIdRegistry();
      reopened.FromJson(registryJson);
      reopened.Sync(new[] { Loft, Panel });   // Divide gone, Loft still there

      var map = ScaffoldMapRecord.Read(mapJson, Canvas());
      var both = map.Regions.SelectMany(r => r.AnchorIds).ToList();

      var resolution = ChatAnchors.Resolve(both, id => reopened.State(id));

      Assert.Equal(AnchorOutcome.PartlyRetired, resolution.Outcome);
      Assert.Equal(new[] { loftId }, resolution.Alive);
      Assert.Equal(new[] { divideId }, resolution.Retired);
    }

    /// <summary>#245's counter rule, exercised across the restore that makes it matter: a truncated or
    /// hand-edited registry degrades to FEWER IDS REMEMBERED, never to an id meaning two things.</summary>
    [Fact]
    public void A_restored_registry_never_reissues_an_id_it_restored()
    {
      var (registryJson, _, divideId, loftId) = LayDownAMap();

      var reopened = new AgentIdRegistry();
      reopened.FromJson(registryJson);
      reopened.Sync(new[] { Divide, Loft, Panel });

      var fresh = reopened.Issue(Panel);

      Assert.NotEqual(divideId, fresh);
      Assert.NotEqual(loftId, fresh);
      Assert.True(reopened.TryGetGuid(divideId, out var d) && d == Divide);
      Assert.True(reopened.TryGetGuid(loftId, out var l) && l == Loft);
    }
  }
}
