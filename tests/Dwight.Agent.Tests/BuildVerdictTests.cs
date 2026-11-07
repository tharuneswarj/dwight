using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Dwight.Agent;
using Dwight.Data;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// The verdict's HONESTY (#193a, #206). The verdict is read by a model that cannot check the canvas
  /// itself, so every field has to mean exactly what it says - an overstatement here is worse than a
  /// missing field, because it is trusted.
  /// </summary>
  public class BuildVerdictTests
  {
    private static BuildPreservation.Result Clean() =>
      BuildPreservation.Check(new FpDoc(), new FpDoc(), new BuildIdentity());

    private static JsonObject Applied() =>
      BuildVerdict.Build(true, new List<int> { 7, 8 }, 1, 1, Clean(), 2, 0, new JsonArray());

    private static JsonObject Reverted() =>
      BuildVerdict.Build(false, new List<int> { 7, 8 }, 1, 1, Clean(), 2, 0, new JsonArray(),
        note: "It changed part of the existing definition, which a build must never do.");

    [Fact]
    public void An_applied_build_reports_its_placed_ids_and_one_undo()
    {
      var v = Applied();
      Assert.Equal("applied", v["status"].GetValue<string>());
      Assert.Equal(2, v["placed"].AsArray().Count);
      Assert.Contains("Ctrl-Z", v["undo"].GetValue<string>());
      Assert.True(v["complete"].GetValue<bool>());
    }

    [Fact]
    public void A_REVERTED_build_never_presents_ids_as_addressable()
    {
      // #206b: those components no longer exist. A model reading "placed":[7,8] would try to wire to
      // them on the next call - the structured field has to say what the prose says.
      var v = Reverted();
      Assert.Equal("reverted", v["status"].GetValue<string>());
      Assert.Empty(v["placed"].AsArray());
      Assert.Equal(2, v["revertedComponents"].GetValue<int>());
      Assert.Equal(0, v["wired"].GetValue<int>());
      Assert.Contains("nothing to undo", v["undo"].GetValue<string>());
    }

    [Fact]
    public void THE_VERDICT_STATES_WHAT_IT_CANNOT_PROVE()
    {
      // The trio proves the build landed and broke nothing. It cannot prove the region is RIGHT - no
      // machine oracle can - and "applied" must never be read as "correct".
      var v = Applied();
      Assert.Contains("computes what you actually want", v["doesNotProve"].GetValue<string>());
    }

    [Fact]
    public void The_verdict_admits_that_a_fed_component_legitimately_CHANGES()
    {
      // #206a. "preserved" is the strongest-sounding field in the payload and would otherwise overstate
      // itself: where the build wired into an existing component, that component's own output changes
      // by design. Preserved means its wiring and settings were not tampered with - not that its data
      // is what it was.
      var doesNotProve = Applied()["doesNotProve"].GetValue<string>();
      Assert.Contains("CHANGES", doesNotProve);
      Assert.Contains("not that its data is the same", doesNotProve);
    }

    [Fact]
    public void The_proves_field_is_specific_rather_than_a_blanket_claim()
    {
      var proves = Applied()["proves"].GetValue<string>();
      Assert.Contains("landed as specified", proves);
      Assert.Contains("apart from the new wires and the reconnections this build declared", proves);
    }

    [Fact]
    public void An_incomplete_build_is_not_complete_even_when_nothing_was_broken()
    {
      // The completeness leg: equivalence proves nothing unintended changed; completeness proves the
      // intended change HAPPENED. A silent no-op must not read as success.
      var v = BuildVerdict.Build(false, new List<int>(), 0, 3, Clean(), 5, 0, new JsonArray());
      Assert.False(v["complete"].GetValue<bool>());
      Assert.Equal(5, v["asked"]["components"].GetValue<int>());
      Assert.Equal(3, v["asked"]["wires"].GetValue<int>());
    }

    [Fact]
    public void New_errors_are_reported_beside_the_status_rather_than_folded_into_it()
    {
      // Solve-health is its own leg: a build can land correctly and still light up errors, and the
      // caller needs to see both facts rather than one collapsed judgement.
      var errors = new JsonArray { "guid|Insufficient curves for a loft." };
      var v = BuildVerdict.Build(true, new List<int> { 1 }, 0, 0, Clean(), 1, 1, errors);

      Assert.Equal("applied", v["status"].GetValue<string>());
      Assert.Equal(1, v["newErrors"].GetValue<int>());
      Assert.Single(v["errors"].AsArray());
    }
    // ---- where the build landed (WP-4 Stage B) ----------------------------------------------------

    [Fact]
    public void A_regioned_build_says_which_region_it_landed_in()
    {
      var v = BuildVerdict.Build(true, new List<int> { 7 }, 0, 0, Clean(), 1, 0, new JsonArray(),
        region: "A1", attemptsLeft: 2);

      Assert.Equal("A1", v["region"].GetValue<string>());
      Assert.Equal(2, v["attemptsLeftForRegion"].GetValue<int>());
    }

    /// <summary>A build with no region reports no region and NO attempt count - #354 Q3's cap does not
    /// exist there, and a number that is not real is worse than a missing one.</summary>
    [Fact]
    public void An_un_addressed_build_reports_neither_a_region_nor_an_attempt_count()
    {
      var v = BuildVerdict.Build(true, new List<int> { 7 }, 0, 0, Clean(), 1, 0, new JsonArray());

      Assert.Null(v["region"]);
      Assert.Null(v["attemptsLeftForRegion"]);
    }

    /// <summary>A DELETE is the one thing a reader must never be surprised by, so a rebuild says it
    /// happened, how many, and - the part that makes it safe - whose they were.</summary>
    [Fact]
    public void A_rebuild_states_plainly_that_it_deleted_something_and_bounds_whose()
    {
      var v = BuildVerdict.Build(true, new List<int> { 9 }, 0, 0, Clean(), 1, 0, new JsonArray(),
        region: "A1", replaced: 3, attemptsLeft: 1);

      Assert.Equal(3, v["replaced"].GetValue<int>());

      // #363: the payload names the FRAME the claim is made in. `preserved` on a rebuild is measured
      // against the definition without this region's previous attempt, and a reader who does not know
      // that would read a true claim as a wider one than it is.
      var frame = v["judgedAgainst"].GetValue<string>();
      Assert.Contains("WITHOUT this region's previous attempt", frame);
      Assert.Contains("not counted as a deletion", frame);

      var note = v["replacedNote"].GetValue<string>();
      Assert.Contains("Dwight placed there and the user had not edited since", note);   // whose components were removed
      Assert.Contains("Nothing else", note);    // the bound that makes it safe
      Assert.Contains("Ctrl-Z", note);          // and that it is recoverable
      // #625 commit 7: "THIS conversation placed" went stale at F1(b) - what Dwight placed is document-scoped.
      Assert.DoesNotContain("conversation", note);
      Assert.DoesNotContain("reconnect", note);   // nothing was reconnected, so nothing is claimed
    }

    /// <summary>
    /// #625 commit 7: a rebuild that reconnected what it cut must not claim "nothing else was touched" or "nothing outside
    /// that set was allowed to change" - its consumers' sources DID change, exactly as declared and checked in place.
    /// The verdict states both bounds with the exception named.
    /// </summary>
    [Fact]
    public void A_rebuild_that_reconnected_names_the_reconnections_as_the_only_other_change()
    {
      var v = BuildVerdict.Build(true, new List<int> { 9 }, 0, 0, Clean(), 1, 0, new JsonArray(),
        region: "A1", replaced: 3, attemptsLeft: 1,
        reconnected: new JsonArray(new JsonObject { ["consumer"] = 19, ["input"] = "Origin" }));

      Assert.Contains("Nothing else was touched apart from the 1 wire it reconnected, each back in its own place",
                      v["replacedNote"].GetValue<string>());
      Assert.Contains("except the reconnections this build declared, each checked in place",
                      v["judgedAgainst"].GetValue<string>());
    }

    [Fact]
    public void A_build_that_replaced_nothing_says_nothing_about_replacing()
    {
      var v = BuildVerdict.Build(true, new List<int> { 7 }, 0, 0, Clean(), 1, 0, new JsonArray(),
        region: "A1", replaced: 0, attemptsLeft: 3);

      Assert.Null(v["replaced"]);
      Assert.Null(v["replacedNote"]);
      // An ordinary build is judged against the canvas as it stood, so there is no other frame to name -
      // and naming one would imply a rebaseline that did not happen.
      Assert.Null(v["judgedAgainst"]);
    }

  }
}
