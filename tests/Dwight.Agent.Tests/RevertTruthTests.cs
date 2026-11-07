using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Dwight.Agent;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// Brief AR. U91: "the canvas is as it was" is a MEASUREMENT. U92: a script that failed on empty inputs, or on an access it was given, is
  /// told so. U94: an automatic approval that ends in a revert or a refusal gets one follow-up turn.
  /// </summary>
  public class RevertTruthTests
  {
    private static Dictionary<Guid, string> Doc(params (Guid id, string name)[] objects) =>
      objects.ToDictionary(o => o.id, o => o.name);

    private static readonly Guid A = Guid.NewGuid(), B = Guid.NewGuid(), Script = Guid.NewGuid(), Panel = Guid.NewGuid(),
      Group = Guid.NewGuid(), Bifocals = Guid.NewGuid();

    // ---- U91 -------------------------------------------------------------------------------------------

    [Fact]
    public void A_document_that_matches_what_stood_before_is_as_it_was()
    {
      var before = Doc((A, "Slider"), (B, "Panel"));
      var m = RevertMeasure.Compare(before, Doc((A, "Slider"), (B, "Panel")), new HashSet<Guid> { Script });

      Assert.True(m.AsItWas);
      Assert.Equal("the canvas is as it was", RevertMeasure.Sentence(m));
      Assert.Equal("nothing to undo - the canvas is as it was", RevertMeasure.UndoField(m));
    }

    [Fact]
    public void A_spot_group_left_standing_is_named_with_the_pinned_sentence()
    {
      var before = Doc((A, "Slider"));
      var after = Doc((A, "Slider"), (Group, "Spot group"));
      var m = RevertMeasure.Compare(before, after, new HashSet<Guid> { Script, Panel, Group });

      Assert.False(m.AsItWas);
      Assert.Equal("Reverted, but 1 object Dwight added still stands: Spot group; one Ctrl-Z may remove it, or delete it by hand.",
                   RevertMeasure.Sentence(m));
    }

    [Fact]
    public void Several_objects_Dwight_added_are_counted_and_named()
    {
      var m = RevertMeasure.Compare(Doc((A, "Slider")), Doc((A, "Slider"), (Group, "Spot group"), (Script, "Script")),
                                    new HashSet<Guid> { Group, Script });

      Assert.Equal("Reverted, but 2 objects Dwight added still stand: Spot group, Script; one Ctrl-Z may remove them, or delete them by hand.",
                   RevertMeasure.Sentence(m));
    }

    [Fact]
    public void An_object_that_appeared_and_is_not_Dwights_is_named_as_another_plugins()
    {
      var m = RevertMeasure.Compare(Doc((A, "Slider")), Doc((A, "Slider"), (Bifocals, "Bifocals label")), new HashSet<Guid> { Script });

      Assert.Equal("1 object appeared that Dwight did not make: Bifocals label; another plugin made it.", RevertMeasure.Sentence(m));
      Assert.Empty(m.StandingDwight);   // not Dwight's, so not something Dwight removes or claims
    }

    [Fact]
    public void An_object_that_stood_before_and_is_gone_is_named_as_missing()
    {
      var m = RevertMeasure.Compare(Doc((A, "Slider"), (B, "Panel")), Doc((A, "Slider")), new HashSet<Guid>());

      Assert.Equal("1 object that stood before is now missing: Panel.", RevertMeasure.Sentence(m));
    }

    [Fact]
    public void Every_finding_is_said_when_several_are_found()
    {
      var m = RevertMeasure.Compare(Doc((A, "Slider"), (B, "Panel")), Doc((A, "Slider"), (Group, "Spot group"), (Bifocals, "Bifocals label")),
                                    new HashSet<Guid> { Group });
      var s = RevertMeasure.Sentence(m);

      Assert.Contains("Reverted, but 1 object Dwight added still stands: Spot group;", s);
      Assert.Contains("1 object appeared that Dwight did not make: Bifocals label; another plugin made it.", s);
      Assert.Contains("1 object that stood before is now missing: Panel.", s);
    }

    [Fact]
    public void A_long_list_of_names_is_bounded_and_says_how_many_more()
    {
      var added = Enumerable.Range(0, 8).Select(i => (Guid.NewGuid(), "Obj" + i)).ToArray();
      var m = RevertMeasure.Compare(Doc(), Doc(added), new HashSet<Guid>(added.Select(o => o.Item1)));

      Assert.Contains("Obj0, Obj1, Obj2, Obj3, Obj4 and 3 more", RevertMeasure.Sentence(m));
      Assert.DoesNotContain("Obj5", RevertMeasure.Sentence(m));
    }

    [Fact]
    public void An_unnamed_object_is_named_as_such_and_an_unmeasured_revert_keeps_its_text()
    {
      var m = RevertMeasure.Compare(Doc(), Doc((Group, " ")), new HashSet<Guid> { Group });
      Assert.Contains("(unnamed)", RevertMeasure.Sentence(m));

      const string text = "It could not be verified. The canvas is as it was.";
      Assert.Equal(text, RevertMeasure.Apply(text, null));
      Assert.Equal(text, RevertMeasure.Apply(text, RevertMeasure.Compare(Doc((A, "x")), Doc((A, "x")), new HashSet<Guid>())));
    }

    [Fact]
    public void A_measured_dirty_revert_rewrites_every_as_it_was_claim_in_the_text()
    {
      var m = RevertMeasure.Compare(Doc(), Doc((Group, "Spot group")), new HashSet<Guid> { Group });

      var rewritten = RevertMeasure.Apply("The script did not run. The build was reverted; the canvas is as it was.", m);
      Assert.DoesNotContain("as it was", rewritten);
      Assert.Contains("Reverted, but 1 object Dwight added still stands: Spot group;", rewritten);

      var sentenceCase = RevertMeasure.Apply("The canvas is as it was.", m);
      Assert.DoesNotContain("as it was", sentenceCase);
    }

    [Fact]
    public void The_spot_group_has_a_kit_role_and_the_guarded_revert_removes_everything_the_kit_holds()
    {
      Assert.Equal("spot group", BuildUndoRefs.RoleSpotGroup);

      var source = File.ReadAllText(Path.Combine(RepoRoot(), "Dwight", "Agent", "GhBuildRegion.cs"));
      Assert.Contains("kit.AddSpotGroup(spotGroup);", source);
      Assert.Contains("Refs.Add(BuildUndoRefs.RoleSpotGroup", source);
      Assert.Contains("foreach (var o in kit.Placed.Concat(kit.AddedBesides))", source);
      // Every AddObject the build makes: the placed components and the spot group (a member added to an existing region group is membership,
      // not an object added to the document).
      Assert.Equal(2, CountOccurrences(source, "d.AddObject(") + CountOccurrences(source, "doc.AddObject("));
    }

    [Fact]
    public void Every_revert_site_measures_before_it_speaks_and_the_snapshot_is_taken_before_anything_changes()
    {
      var source = File.ReadAllText(Path.Combine(RepoRoot(), "Dwight", "Agent", "GhBuildRegion.cs"));

      var snapshot = source.IndexOf("kit.Before = NamesOf(d);", StringComparison.Ordinal);
      var firstMutation = source.IndexOf("d.AddObject(obj, false", StringComparison.Ordinal);
      Assert.True(snapshot > 0 && firstMutation > snapshot, "the document is read before the first object is added");

      Assert.True(CountOccurrences(source, "RevertMeasure.Apply(") >= 6, "every revert's sentence is passed through the measurement");
      Assert.Contains("verdict[\"canvasAsItWas\"] = false;", source);
      Assert.Contains("verdict[\"undo\"] = RevertMeasure.UndoField(m);", source);
      Assert.Contains("WithMeasure(BuildVerdict.Build(false,", source);
    }

    [Fact]
    public void A_revert_with_residue_is_its_own_outcome_and_is_never_read_as_as_it_was()
    {
      var verdict = new JsonObject
      {
        ["status"] = "reverted", ["region"] = "A1", ["canvasAsItWas"] = false,
        ["residue"] = "Reverted, but 1 object Dwight added still stands: Spot group; one Ctrl-Z may remove it, or delete it by hand.",
      };
      var envelope = new JsonObject
      {
        ["ok"] = false,
        ["error"] = new JsonObject { ["code"] = "verification_failed", ["message"] = "reverted", ["detail"] = verdict },
      }.ToJsonString();

      var env = BuildEnvelope.Classify(envelope, "{\"region\":\"A1\"}");
      Assert.Equal(RegionOutcome.RevertedWithResidue, env.Outcome);
      Assert.Contains("Spot group", env.Residue);

      var r = new BuildTurnRecord(new[] { "A1" });
      r.Record("A1", env.Outcome, 0, "", null, false, env.Residue);
      var fact = BuildOutcomeFact.For(r, 1);
      Assert.Contains("NOT CLEANLY: Reverted, but 1 object Dwight added still stands: Spot group", fact);
      Assert.DoesNotContain("the canvas is as it was", fact);
    }

    [Fact]
    public void A_clean_revert_still_says_the_canvas_is_as_it_was()
    {
      var verdict = new JsonObject { ["status"] = "reverted", ["region"] = "A1" };
      var envelope = new JsonObject
      {
        ["ok"] = false,
        ["error"] = new JsonObject { ["code"] = "verification_failed", ["message"] = "reverted", ["detail"] = verdict },
      }.ToJsonString();

      var env = BuildEnvelope.Classify(envelope, "{\"region\":\"A1\"}");
      Assert.Equal(RegionOutcome.Reverted, env.Outcome);
    }

    // ---- U92 -------------------------------------------------------------------------------------------

    private static ScriptBuildFacts Failed(IReadOnlyList<string> empty = null, IReadOnlyList<string> access = null, string said = "'NoneType' object is not iterable") =>
      new ScriptBuildFacts
      {
        Ref = "S1", Name = "Script", Language = "python",
        Errors = new[] { said },
        Inputs = new[] { "x", "y" }, UnwiredInputs = new string[0],
        EmptyInputs = empty ?? new string[0],
        ItemAccessMultiItemInputs = access ?? new string[0],
      };

    [Fact]
    public void One_empty_input_is_named_and_the_interpreters_line_is_quoted_after_as_a_fact()
    {
      var j = ScriptBuildVerdict.Judge(new[] { Failed(new[] { "x" }) }, null);

      Assert.False(j.Stands);
      Assert.Equal("The script did not run because its inputs were empty: x read no data. The sources upstream hold nothing to compute with; " +
                   "Diagnose can say why. The interpreter said: 'NoneType' object is not iterable The build was reverted; the canvas is as it was.",
                   j.UserRefusal.Replace(" The interpreter said: 'NoneType' object is not iterable", " The interpreter said: 'NoneType' object is not iterable"));
      Assert.DoesNotContain("Fix the script", j.UserRefusal);
      Assert.DoesNotContain("Fix the script", j.Refusal);
    }

    [Fact]
    public void Two_empty_inputs_are_both_named()
    {
      var j = ScriptBuildVerdict.Judge(new[] { Failed(new[] { "x", "y" }) }, null);

      Assert.Contains("its inputs were empty: x, y read no data.", j.UserRefusal);
    }

    [Fact]
    public void A_script_whose_inputs_held_data_keeps_the_old_sentence()
    {
      var j = ScriptBuildVerdict.Judge(new[] { Failed() }, null);

      Assert.False(j.Stands);
      Assert.DoesNotContain("inputs were empty", j.UserRefusal);
      Assert.DoesNotContain("inputs were empty", j.Refusal);
    }

    [Fact]
    public void The_empty_input_sentence_is_ended_by_the_measurement_not_asserted_when_the_revert_left_something()
    {
      var j = ScriptBuildVerdict.Judge(new[] { Failed(new[] { "x" }) }, null);
      var m = RevertMeasure.Compare(Doc(), Doc((Group, "Spot group")), new HashSet<Guid> { Group });

      var said = RevertMeasure.Apply(j.UserRefusal, m);
      Assert.DoesNotContain("as it was", said);
      Assert.Contains("Reverted, but 1 object Dwight added still stands: Spot group;", said);
      Assert.Contains("inputs were empty: x", said);
    }

    [Fact]
    public void An_input_given_item_access_with_many_items_upstream_is_the_access_case()
    {
      var j = ScriptBuildVerdict.Judge(new[] { Failed(access: new[] { "x" }, said: "'float' object is not iterable") }, null);

      Assert.False(j.Stands);
      Assert.Contains("The input x was given one item at a time, and the script treats it as a list. " +
                      "It has item access because none other was declared; build again declaring list access on x, or change the script to take one item.",
                      j.UserRefusal);
      Assert.Contains("The interpreter said: 'float' object is not iterable", j.UserRefusal);
      Assert.DoesNotContain("inputs were empty", j.UserRefusal);
      Assert.Contains("the canvas is as it was", j.UserRefusal);
    }

    [Fact]
    public void The_empty_case_wins_over_the_access_case_for_the_same_script()
    {
      var j = ScriptBuildVerdict.Judge(new[] { Failed(new[] { "x" }, new[] { "y" }) }, null);

      Assert.Contains("inputs were empty: x", j.UserRefusal);
      Assert.DoesNotContain("one item at a time", j.UserRefusal);
    }

    [Fact]
    public void The_plugin_reads_the_facts_off_the_live_inputs_and_never_from_the_error_text()
    {
      var source = File.ReadAllText(Path.Combine(RepoRoot(), "Dwight", "Agent", "GhBuildRegion.cs"));

      Assert.Contains("f.EmptyInputs = emptyInputs;", source);
      Assert.Contains("f.ItemAccessMultiItemInputs = itemMulti;", source);
      Assert.Contains("p.Access == GH_ParamAccess.item", source);
      Assert.Contains("p.VolatileData.AllData(false)", source);
    }

    // ---- U94 -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(true, 1, true)]    // reverted
    [InlineData(true, 2, true)]    // refused
    [InlineData(false, 1, false)]
    [InlineData(false, 2, false)]
    [InlineData(true, 0, false)]   // succeeded
    [InlineData(false, 0, false)]
    public void An_automatic_approval_that_reverted_or_was_refused_follows_up_and_nothing_else_does(
      bool automatic, int endIndex, bool expected)
    {
      var end = endIndex == 0 ? AutoFollowUp.FlowEnd.Succeeded : endIndex == 1 ? AutoFollowUp.FlowEnd.Reverted : AutoFollowUp.FlowEnd.Refused;
      Assert.Equal(expected, AutoFollowUp.ShouldRun(automatic, end));
    }

    [Fact]
    public void The_follow_up_is_announced_in_the_pinned_words_before_it_runs_and_runs_read_only()
    {
      Assert.Equal("Dwight is continuing after the revert.", AutoFollowUp.Line);

      var source = File.ReadAllText(Path.Combine(RepoRoot(), "Dwight", "UI", "DwightScaffold.cs"));
      var announce = source.IndexOf("HtmlEncode(AutoFollowUp.Line)", StringComparison.Ordinal);
      Assert.True(announce > 0);
      var run = source.IndexOf("RunTurn(wp, doc, threadId, TurnCapability.ReadOnly);", announce, StringComparison.Ordinal);
      Assert.True(run > announce && run - announce < 200, "the line is said immediately before the read-only turn");
      Assert.Contains("autoApproved: autoMode != null", source);
      Assert.Contains("AutoFollowUp.ShouldRun(", source);
    }

    private static int CountOccurrences(string text, string needle)
    {
      var n = 0;
      for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal)) n++;
      return n;
    }

    private static string RepoRoot()
    {
      var dir = new DirectoryInfo(AppContext.BaseDirectory);
      while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Dwight.sln"))) dir = dir.Parent;
      Assert.True(dir != null, "the repository root (Dwight.sln) could not be found from " + AppContext.BaseDirectory);
      return dir.FullName;
    }
  }
}
