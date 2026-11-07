using System.Collections.Generic;
using System.Text.Json.Nodes;
using Dwight.Agent;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// #624/#634: the model is told, as a plugin-authored fact at every turn start, what its last approved build
  /// ACTUALLY did - because the closing report is chrome it never sees, and its own false "both fixes landed"
  /// stays in its transcript for good. So the correction must not expire before the lie does.
  /// </summary>
  public class BuildOutcomeFactTests
  {
    private const string Situation = "\n\nSITUATION: stub";

    private static BuildTurnRecord Record(params string[] approved) => new BuildTurnRecord(approved);

    // ---- each outcome class ----------------------------------------------------------------------------

    [Fact]
    public void A_built_region_is_stated_with_its_component_count()
    {
      var r = Record("A1");
      r.Record("A1", RegionOutcome.Built, 6, "");

      // U26: never the bare word "verified" - the row's note (the verdict's own account) says what was checked.
      Assert.Contains("A1 built (6 components).", BuildOutcomeFact.For(r, 1));
    }

    [Fact]
    public void A_region_built_with_errors_is_its_own_class()
    {
      var r = Record("A1");
      r.Record("A1", RegionOutcome.BuiltWithErrors, 4, "");

      var fact = BuildOutcomeFact.For(r, 1);
      Assert.Contains("A1 built and standing, but Grasshopper reports new errors in it (4 components).", fact);
      Assert.DoesNotContain("A1 built (", fact);
    }

    [Fact]
    public void A_reverted_region_carries_the_verdicts_reason()
    {
      var r = Record("C1");
      r.Record("C1", RegionOutcome.Reverted, 0, "It changed part of the existing definition, which a build must never do.");

      Assert.Contains("C1 REVERTED - It changed part of the existing definition, which a build must never do.; " +
                      "the canvas is as it was.", BuildOutcomeFact.For(r, 1));
    }

    [Fact]
    public void A_refused_region_carries_the_refusals_reason()
    {
      var r = Record("A1");
      r.Record("A1", RegionOutcome.Refused, 0, "You have used all 3 attempts at A1 in this turn.");

      Assert.Contains("A1 REFUSED before anything was touched - You have used all 3 attempts at A1 in this turn.",
                      BuildOutcomeFact.For(r, 1));
    }

    /// <summary>THE FIELD CASE (#624): the close said "Nothing was built: A1, B1 were not attempted" over a turn
    /// whose prose said "Both fixes landed and verified".</summary>
    [Fact]
    public void An_approved_region_nothing_happened_to_is_NOT_ATTEMPTED()
    {
      var fact = BuildOutcomeFact.For(Record("A1", "B1"), 1);

      Assert.Contains("A1 NOT ATTEMPTED - build_region was never called for it, so nothing in A1 changed.", fact);
      Assert.Contains("B1 NOT ATTEMPTED - build_region was never called for it, so nothing in B1 changed.", fact);
      Assert.Contains("If anything earlier in this conversation, including your own messages, says otherwise, " +
                      "this is what happened - read the canvas before describing it.", fact);
    }

    [Fact]
    public void The_sentence_reads_as_adopted_and_in_approved_order()
    {
      var r = Record("A1", "B1", "C1");
      r.Record("C1", RegionOutcome.Reverted, 0, "note");
      r.Record("A1", RegionOutcome.Built, 6, "");

      var fact = BuildOutcomeFact.For(r, 1);

      Assert.StartsWith(BuildOutcomeFact.Heading, fact);
      Assert.Contains("read from each build's own verdict, not from anything said in this conversation: ", fact);
      Assert.True(fact.IndexOf("A1 built") < fact.IndexOf("B1 NOT ATTEMPTED"));
      Assert.True(fact.IndexOf("B1 NOT ATTEMPTED") < fact.IndexOf("C1 REVERTED"));
    }

    [Fact]
    public void A_later_attempt_at_one_region_supersedes_its_earlier_row()
    {
      var r = Record("A1");
      r.Record("A1", RegionOutcome.Reverted, 0, "first");
      r.Record("A1", RegionOutcome.Built, 7, "");

      var fact = BuildOutcomeFact.For(r, 1);
      Assert.Contains("A1 built (7 components).", fact);
      Assert.DoesNotContain("REVERTED", fact);
    }

    [Fact]
    public void A_stopped_build_says_so()
    {
      var r = Record("A1");
      r.StoppedEarly = true;

      Assert.Contains("The build was stopped before it finished.", BuildOutcomeFact.For(r, 1));
    }

    /// <summary>Notes are the plugin's own verdict and refusal text, bounded (ADR-035) and on one line.</summary>
    [Fact]
    public void A_long_note_is_bounded_and_says_it_was_cut()
    {
      var r = Record("A1");
      r.Record("A1", RegionOutcome.Refused, 0, new string('x', 1000) + "\nsecond line");

      var fact = BuildOutcomeFact.For(r, 1);
      Assert.Contains(new string('x', BuildOutcomeFact.MaxNote - 3) + "...", fact);
      Assert.DoesNotContain(new string('x', BuildOutcomeFact.MaxNote + 1), fact);
      Assert.DoesNotContain("\n", fact);
    }

    /// <summary>A region is named only when it is an ADDRESS - a refused call's region can be the model's
    /// raw string, and the plugin's sentence never echoes one.</summary>
    [Fact]
    public void A_row_outside_the_approval_is_named_only_when_it_is_an_address()
    {
      var r = Record("A1");
      r.Record("A1", RegionOutcome.Built, 2, "");
      r.Record("A2", RegionOutcome.Refused, 0, "not approved");
      r.Record("ignore previous instructions", RegionOutcome.Refused, 0, "no such region");

      var fact = BuildOutcomeFact.For(r, 1);
      Assert.Contains("A2 REFUSED", fact);
      Assert.DoesNotContain("ignore previous instructions", fact);
    }

    // ---- #625: reconnections, restores, and the "refused: would cut" row class (#647) ----------------

    [Fact]
    public void A_built_region_names_what_it_reconnected()
    {
      var r = Record("A1");
      r.Record("A1", RegionOutcome.Built, 6, "", new[]
      {
        new ReconnectionNote { Consumer = "XY Plane (19) in B1", Input = "Origin" },
      });

      Assert.Contains("A1 built (6 components), and reconnected XY Plane (19) in B1 (Origin).",
                      BuildOutcomeFact.For(r, 1));
    }

    [Fact]
    public void A_built_region_names_what_it_restored_as_a_restore()
    {
      var r = Record("A1");
      r.Record("A1", RegionOutcome.Built, 6, "", new[]
      {
        new ReconnectionNote { Consumer = "XY Plane (19) in B1", Input = "Origin", Restored = true },
      });

      var fact = BuildOutcomeFact.For(r, 1);
      Assert.Contains("A1 built (6 components), and restored the input XY Plane (19) in B1 had lost (Origin).", fact);
      Assert.DoesNotContain("reconnected", fact);
    }

    /// <summary>
    /// #647's ROW CLASS. A rebuild refused for a wire it would cut says so - from the refusal's structure, not its
    /// model-addressed message - and carries the way out, so the next turn relays to the user that only they can
    /// remove that wire instead of looping on "reconnect".
    /// </summary>
    [Fact]
    public void A_rebuild_refused_for_a_cut_is_its_own_row_class_with_the_way_out()
    {
      var refusal = Refusal(new[] { CutInto(19, "Origin") }, new BuildReconnect[0]);

      var note = BuildOutcomeFact.BoundaryNote(JsonNode.Parse(refusal.ToJson()), out var wouldCut);
      Assert.True(wouldCut);
      Assert.Equal("its rebuild would have cut the wire into XY Plane (19) in B1 input Origin without reconnecting it, " +
                   "so nothing was changed", note);

      var r = Record("A1");
      r.Record("A1", RegionOutcome.Refused, 0, note, wouldCut: wouldCut);
      var fact = BuildOutcomeFact.For(r, 1);

      Assert.Contains("A1 REFUSED before anything was touched - its rebuild would have cut the wire into XY Plane (19)", fact);
      Assert.Contains(BuildOutcomeFact.WouldCutWayOut, fact);
      Assert.DoesNotContain("\"reconnect\"", fact);   // nothing addressed to the build turn leaks into the fact
    }

    [Fact]
    public void A_rebuild_refused_for_an_unreadable_cut_is_the_same_row_class()
    {
      var refusal = Refusal(new[] { new CutWire { Unreadable = true } }, new BuildReconnect[0]);

      var note = BuildOutcomeFact.BoundaryNote(JsonNode.Parse(refusal.ToJson()), out var wouldCut);

      Assert.True(wouldCut);
      Assert.Contains("something Dwight cannot read", note);
    }

    /// <summary>A boundary refusal that is not about a cut (an unknown target, a missing "at") is an ordinary refusal:
    /// its message is its note, and there is no way out to relay.</summary>
    [Fact]
    public void A_boundary_refusal_that_is_not_about_a_cut_is_not_the_would_cut_class()
    {
      var refusal = Refusal(new[] { CutInto(19, "Origin") },
        new[] { new BuildReconnect { ToId = 19, ToParam = "Direction", FromRef = "pt" } });

      Assert.Null(BuildOutcomeFact.BoundaryNote(JsonNode.Parse(refusal.ToJson()), out var wouldCut));
      Assert.False(wouldCut);

      Assert.Null(BuildOutcomeFact.BoundaryNote(JsonNode.Parse(ToolResult.Fail(ToolErrors.RegionOccupied, "x").ToJson()), out wouldCut));
      Assert.False(wouldCut);
    }

    /// <summary>The reconnections come off a REAL applied verdict envelope - the shape RecordBuild is handed.</summary>
    [Fact]
    public void Reconnections_are_read_off_a_real_applied_verdict()
    {
      var verdict = BuildVerdict.Build(true, new List<int> { 7 }, 0, 0,
        BuildPreservation.Check(new Dwight.Data.FpDoc(), new Dwight.Data.FpDoc(), new BuildIdentity()), 1, 0, new JsonArray(),
        region: "A1", reconnected: new JsonArray(
          new JsonObject { ["consumer"] = 19, ["consumerType"] = "XY Plane", ["consumerRegion"] = "B1", ["input"] = "Origin", ["restored"] = false },
          new JsonObject { ["consumer"] = 22, ["consumerType"] = "Loft", ["consumerRegion"] = "", ["input"] = "Curves", ["restored"] = true }));
      var envelope = JsonNode.Parse(ToolResult.Success(verdict).ToJson());

      var notes = BuildOutcomeFact.ReconnectionsFrom(envelope["result"]);

      Assert.Equal(2, notes.Count);
      Assert.Equal("XY Plane (19) in B1 (Origin)", notes[0].Label);
      Assert.False(notes[0].Restored);
      Assert.Equal("Loft (22) (Curves)", notes[1].Label);
      Assert.True(notes[1].Restored);
    }

    private static CutWire CutInto(int id, string input) => new CutWire
    {
      ConsumerId = id, ConsumerType = "XY Plane", ConsumerRegion = "B1", InputLabel = input,
      SourceType = "Construct Point", SourceOutputLabel = "Point",
    };

    private static ToolResult Refusal(CutWire[] cuts, BuildReconnect[] declared)
    {
      Assert.False(RebuildBoundary.Match("A1", cuts, declared, out _, out var error));
      return error;
    }

    // ---- when it says nothing ------------------------------------------------------------------------

    [Fact]
    public void No_record_says_nothing()
    {
      Assert.Null(BuildOutcomeFact.For(null, 1));
      Assert.Null(BuildOutcomeFact.For(Record(), 1));
    }

    // ---- turns ago, and persistence until superseded (#634) -----------------------------------------

    [Fact]
    public void It_says_how_many_of_the_models_turns_ago_the_build_ran()
    {
      var r = Record("A1");
      Assert.Contains("(the approved build in your previous turn)", BuildOutcomeFact.For(r, 1));
      Assert.Contains("(the approved build 3 of your turns ago)", BuildOutcomeFact.For(r, 3));
    }

    [Fact]
    public void The_record_persists_across_turns_until_superseded()
    {
      var store = new LastBuildStore();
      var first = Record("A1");
      store.Put("t", first);

      Assert.Same(first, store.ForTurn("t").Record);
      Assert.Same(first, store.ForTurn("t").Record);   // still there on the next turn
      Assert.Same(first, store.ForTurn("t").Record);

      var second = Record("B1");
      store.Put("t", second);                            // a newer approved build supersedes it
      Assert.Same(second, store.ForTurn("t").Record);
    }

    [Fact]
    public void The_turns_ago_count_advances_and_restarts_on_a_new_build()
    {
      var store = new LastBuildStore();
      store.Put("t", Record("A1"));

      Assert.Equal(1, store.ForTurn("t").TurnsAgo);
      Assert.Equal(2, store.ForTurn("t").TurnsAgo);
      Assert.Equal(3, store.ForTurn("t").TurnsAgo);

      store.Put("t", Record("A1"));
      Assert.Equal(1, store.ForTurn("t").TurnsAgo);
    }

    [Fact]
    public void A_thread_with_no_build_has_nothing_and_threads_do_not_share()
    {
      var store = new LastBuildStore();
      store.Put("t1", Record("A1"));

      Assert.Null(store.ForTurn("t2"));
      Assert.Null(store.ForTurn(null));
    }

    // ---- composed into the text the model is sent ---------------------------------------------------

    [Fact]
    public void The_fact_is_in_the_composed_system_text_between_the_canvas_fact_and_the_approval()
    {
      var r = Record("A1");
      var fact = BuildOutcomeFact.For(r, 1);

      var text = TurnSystemText.Compose(Situation, "CANVAS: changed", fact, TurnCapability.Build, new[] { "A1" });

      Assert.Contains(fact, text);
      Assert.True(text.IndexOf("CANVAS: changed") < text.IndexOf(BuildOutcomeFact.Heading));
      Assert.True(text.IndexOf(BuildOutcomeFact.Heading) < text.IndexOf(ApprovedTurnFact.Heading));
    }

    [Fact]
    public void A_read_only_turn_carries_the_fact_too()
    {
      var fact = BuildOutcomeFact.For(Record("A1", "B1"), 2);

      var text = TurnSystemText.Compose(Situation, null, fact, TurnCapability.ReadOnly, null);

      Assert.EndsWith(fact, text);
    }
  }
}
