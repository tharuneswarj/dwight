using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Dwight.Agent;
using Dwight.Data;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// U34 (#925): A verification_failed NAMES ITS LEG AND ITS SUBJECT.
  ///
  /// Two false "tool limitation" claims in one session, both at the moment a build reverted: the model could not see
  /// into the failure, so it invented a cause - a wire resolver that "can't disambiguate", a tool that "cannot replace
  /// a wire". The data was in the verdict. These tests hold the sentence to the structure: per leg, per breach kind,
  /// the component and input named through the shim's label, and a positive control that a clean build yields no
  /// failure at all (a composer that could describe a failure of a passing build would be the instrument inventing
  /// the defect it exists to stop).
  /// </summary>
  public class BuildFailureTests
  {
    private static Guid G(int n) { var b = new byte[16]; b[0] = (byte)n; b[1] = (byte)(n >> 8); return new Guid(b); }

    private static FpObject Obj(int id, string type = "Loft", string value = null, params (int input, int[] sources)[] inputs)
    {
      var o = new FpObject { Guid = G(id), TypeName = type, ValueToken = value };
      foreach (var (input, sources) in inputs)
        o.Inputs.Add(new FpInput { Guid = G(input), MappingToken = "none", Sources = sources.Select(G).ToList() });
      return o;
    }

    private static FpDoc Doc(params FpObject[] objects) { var d = new FpDoc(); d.Objects.AddRange(objects); return d; }

    /// <summary>Two existing components: 1 (a slider) feeding 2 (Line SDL) on input 20.</summary>
    private static FpDoc Before() => Doc(Obj(1, "Number Slider", "0.5"), Obj(2, "Line SDL", inputs: (20, new[] { 1 })));

    private static BuildPreservation.Result Check(FpDoc before, FpDoc after, params int[] createdSources) =>
      BuildPreservation.Check(before, after, new BuildIdentity
      {
        CreatedObjects = new HashSet<Guid>(createdSources.Select(G)),
        CreatedSources = new HashSet<Guid>(createdSources.Select(G)),
      });

    /// <summary>The shim's label, faked: object 2 is "Line SDL (23)", input 20 is "L".</summary>
    private static string Label(Guid obj, Guid? input)
    {
      var name = obj == G(2) ? "Line SDL (23)" : obj == G(1) ? "Number Slider \"Count\" (7)" : null;
      if (name == null) return null;
      return input == G(20) ? name + ", input L" : name;
    }

    private static BuildFailure Diagnose(BuildPreservation.Result p, int placed = 1, int asked = 1, int wired = 1, int wiresAsked = 1,
      int reconnectDone = 0, int reconnectPlanned = 0, string mismatch = null) =>
      BuildFailureDiagnosis.Diagnose(p, Label, placed, asked, wired, wiresAsked, reconnectDone, reconnectPlanned, mismatch);

    // ---- the positive control --------------------------------------------------------------------------------

    [Fact]
    public void A_clean_complete_build_has_NO_failure_to_describe()
    {
      var clean = Check(Before(), Before());
      Assert.True(clean.Preserved);
      Assert.Empty(clean.Breaches);
      Assert.Null(Diagnose(clean));
    }

    // ---- the preservation leg, per breach kind -----------------------------------------------------------------

    /// <summary>#925's own case: a new wire into Line SDL's L from something that was already on the canvas.</summary>
    [Fact]
    public void A_REWIRE_FROM_AN_EXISTING_COMPONENT_names_the_component_the_input_and_the_cause()
    {
      var after = Doc(Obj(1, "Number Slider", "0.5"), Obj(2, "Line SDL", inputs: (20, new[] { 1, 1 })));   // slider 1 wired again - not a created source

      var f = Diagnose(Check(Before(), after, createdSources: 50));

      Assert.Equal(BuildFailureDiagnosis.Preservation, f.Leg);
      Assert.Equal("Line SDL (23), input L: a new source came from an EXISTING component, not from anything this build placed", f.What);
      Assert.Null(f.Also);
    }

    [Fact]
    public void A_LOST_WIRE_says_so_with_the_counts()
    {
      var after = Doc(Obj(1, "Number Slider", "0.5"), Obj(2, "Line SDL", inputs: (20, new int[0])));

      var f = Diagnose(Check(Before(), after));

      Assert.Equal(BuildFailureDiagnosis.Preservation, f.Leg);
      Assert.Equal("Line SDL (23), input L: a wire into it was lost (1 source before, 0 after)", f.What);
    }

    [Fact]
    public void A_REORDER_OR_INSERT_BEFORE_is_named_as_such()
    {
      var after = Doc(Obj(1, "Number Slider", "0.5"), Obj(2, "Line SDL", inputs: (20, new[] { 50, 1 })));   // new source inserted FIRST

      var f = Diagnose(Check(Before(), after, createdSources: 50));

      Assert.Equal("Line SDL (23), input L: its existing sources were reordered, or a new one was inserted before them instead of after", f.What);
    }

    [Fact]
    public void A_DELETED_component_is_named()
    {
      var after = Doc(Obj(2, "Line SDL", inputs: (20, new[] { 1 })));   // the slider is gone

      var f = Diagnose(Check(Before(), after));

      Assert.Equal(BuildFailureDiagnosis.Preservation, f.Leg);
      Assert.Equal("Number Slider \"Count\" (7): it was removed", f.What);
      Assert.Equal(PreservationBreach.KindDeleted, Assert.Single(Check(Before(), after).Breaches).Kind);
    }

    [Fact]
    public void AN_ALTERED_component_names_which_of_its_properties_moved()
    {
      var after = Doc(Obj(1, "Number Slider", "0.7") , Obj(2, "Line SDL", inputs: (20, new[] { 1 })));
      after.Objects[0].Locked = true;

      var f = Diagnose(Check(Before(), after));

      Assert.Equal("Number Slider \"Count\" (7): its value, locked state changed", f.What);
    }

    [Fact]
    public void A_DECLARED_RECONNECTION_THAT_DID_NOT_FIT_is_named_on_its_input()
    {
      var after = Doc(Obj(1, "Number Slider", "0.5"), Obj(2, "Line SDL", inputs: (20, new[] { 50 })));
      var p = BuildPreservation.Check(Before(), after, new BuildIdentity
      {
        CreatedSources = new HashSet<Guid> { G(50) },
        Reconnections = new[] { new PredictedReconnection { InputGuid = G(20), Position = 0, OldSource = G(99), NewSource = G(50) } },   // 99 was never there
      });

      var f = BuildFailureDiagnosis.Diagnose(p, Label, 1, 1, 0, 0, 1, 1, null);

      Assert.Equal(BuildFailureDiagnosis.Preservation, f.Leg);
      Assert.Equal("Line SDL (23), input L: the declared reconnection did not fit - the cut source was not at the position the plugin recorded", f.What);
    }

    [Fact]
    public void Many_breaches_are_named_to_a_bound_and_the_rest_counted()
    {
      var before = Doc(Enumerable.Range(1, 6).Select(i => Obj(i, "Panel")).ToArray());
      var after = Doc();   // every one deleted

      var f = Diagnose(Check(before, after));

      Assert.Equal(BuildFailureDiagnosis.MaxNamed, f.What.Split(';').Length - 1);
      Assert.EndsWith("; and 3 more", f.What);
    }

    [Fact]
    public void An_unlabelled_guid_is_called_an_existing_component_never_printed()
    {
      var before = Doc(Obj(9, "Loft"));
      var f = BuildFailureDiagnosis.Diagnose(Check(before, Doc()), (g, i) => null, 1, 1, 0, 0, 0, 0, null);

      Assert.Equal("an existing component: it was removed", f.What);
      Assert.DoesNotContain(G(9).ToString(), f.What);
    }

    // ---- the completeness leg -------------------------------------------------------------------------------

    [Fact]
    public void A_SHORT_PLACEMENT_counts_what_landed_against_what_was_declared()
    {
      var f = Diagnose(Check(Before(), Before()), placed: 3, asked: 4);
      Assert.Equal(BuildFailureDiagnosis.Completeness, f.Leg);
      Assert.Equal("3 of 4 declared components were placed", f.What);
    }

    [Fact]
    public void MISSING_WIRES_AND_RECONNECTIONS_are_each_counted()
    {
      var f = Diagnose(Check(Before(), Before()), wired: 2, wiresAsked: 3, reconnectDone: 0, reconnectPlanned: 1);
      Assert.Equal("2 of 3 declared wires landed; 0 of 1 declared reconnections were carried out", f.What);
    }

    [Fact]
    public void A_STATE_MISMATCH_names_the_component_and_the_two_values()
    {
      var f = Diagnose(Check(Before(), Before()), mismatch: "\"s2\": the slider holds 1, not the declared 40");
      Assert.Equal(BuildFailureDiagnosis.Completeness, f.Leg);
      Assert.Equal("component \"s2\": the slider holds 1, not the declared 40", f.What);
    }

    [Fact]
    public void When_both_legs_fail_preservation_leads_and_completeness_is_still_said()
    {
      var after = Doc(Obj(2, "Line SDL", inputs: (20, new[] { 1 })));
      var f = Diagnose(Check(Before(), after), placed: 0, asked: 2);

      Assert.Equal(BuildFailureDiagnosis.Preservation, f.Leg);
      Assert.Equal("Completeness failed too: 0 of 2 declared components were placed.", f.Also);
      Assert.Contains("Completeness failed too", BuildFailureDiagnosis.Note(f));
    }

    // ---- the sentences the model and the user read ----------------------------------------------------------

    [Fact]
    public void The_message_names_the_leg_in_capitals_and_denies_the_invented_limit()
    {
      var after = Doc(Obj(1, "Number Slider", "0.5"), Obj(2, "Line SDL", inputs: (20, new[] { 1, 1 })));
      var f = Diagnose(Check(Before(), after, createdSources: 50));

      var message = BuildFailureDiagnosis.Message(f);

      Assert.StartsWith("The build was applied, failed its PRESERVATION check, and was reverted - the canvas is as it was. " +
                        "Preservation failed on Line SDL (23), input L: a new source came from an EXISTING component", message);
      Assert.EndsWith("this is not a limit of the tool.", message);
    }

    [Fact]
    public void The_pre_check_and_timeout_messages_name_their_leg_too()
    {
      Assert.Contains("COMPLETENESS could not be met: a wire's \"to\" port could not be resolved (\"L\")",
        BuildFailureDiagnosis.StoppedBefore("a wire's \"to\" port could not be resolved (\"L\")"));
      Assert.StartsWith("SOLVE-HEALTH could not be judged: the definition did not finish solving within 120 s",
        BuildFailureDiagnosis.SolveTimedOut(120_000));
      Assert.Contains("before any leg could be judged", BuildFailureDiagnosis.CheckThrew("boom"));
    }

    /// <summary>THE WIRE: through a real reverted verdict and a real envelope (#325, #649) - the verdict carries
    /// failedLeg, the note is the leg sentence, and the panel's line and the turn-start fact both carry it.</summary>
    [Fact]
    public void A_reverted_verdict_carries_the_leg_and_the_subject_to_every_reader()
    {
      var after = Doc(Obj(1, "Number Slider", "0.5"), Obj(2, "Line SDL", inputs: (20, new[] { 1, 1 })));
      var p = Check(Before(), after, createdSources: 50);
      var f = Diagnose(p);

      var verdict = BuildVerdict.Build(false, new List<int> { 0 }, 1, 1, p, 1, 0, new JsonArray(),
        note: BuildFailureDiagnosis.Note(f), region: "B2", attemptsLeft: 2, failedLeg: f.Leg);
      Assert.Equal("preservation", verdict["failedLeg"].GetValue<string>());

      var envelope = ToolResult.Fail(ToolErrors.VerificationFailed, BuildFailureDiagnosis.Message(f), verdict).ToJson();
      var env = BuildEnvelope.Classify(envelope, "{\"region\":\"B2\"}");

      Assert.Equal(RegionOutcome.Reverted, env.Outcome);
      Assert.Equal("Preservation failed on Line SDL (23), input L: a new source came from an EXISTING component, not from anything this build placed.", env.Note);

      var record = new BuildTurnRecord(new[] { "B2" });
      record.Record(env.Address, env.Outcome, env.Placed, env.Note);
      Assert.Contains("B2 REVERTED - Preservation failed on Line SDL (23), input L", BuildOutcomeFact.For(record, 1));
    }

    [Fact]
    public void An_applied_verdict_never_carries_a_failed_leg()
    {
      var v = BuildVerdict.Build(true, new List<int> { 7 }, 0, 0, Check(Before(), Before()), 1, 0, new JsonArray(), failedLeg: "preservation");
      Assert.Null(v["failedLeg"]);
    }
  }
}
