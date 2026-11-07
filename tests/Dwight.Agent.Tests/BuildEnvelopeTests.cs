using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dwight.Agent;
using Dwight.Data;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// #649 (Finding C): ONE build_region RESULT READ THE WAY IT ACTUALLY ARRIVES. The panel read a reverted verdict from
  /// error.data; ToolResult writes error.detail. Every revert classified as REFUSED. So these tests are built ONLY from
  /// envelopes ToolResult itself produces - never a hand-written JSON shape the subject never sees (#325).
  /// </summary>
  public class BuildEnvelopeTests
  {
    private const string A1Args = "{\"region\":\"A1\",\"components\":[{\"ref\":\"a\",\"type\":\"Panel\"}]}";

    private static BuildPreservation.Result Preserved() =>
      BuildPreservation.Check(new FpDoc(), new FpDoc(), new BuildIdentity());

    private static JsonObject Verdict(bool applied, int newErrors = 0, string note = null, int? attemptsLeft = null,
                                      JsonArray state = null) =>
      BuildVerdict.Build(applied, new List<int> { 7, 8 }, 1, 1, Preserved(), 2, newErrors, new JsonArray(),
        note: note, region: "A1", attemptsLeft: attemptsLeft, initialState: state);

    [Fact]
    public void An_applied_verdict_is_Built_with_its_placed_count_state_and_attempts_left()
    {
      var envelope = ToolResult.Success(Verdict(true, attemptsLeft: 2, state: new JsonArray("Rows = 40"))).ToJson();

      var env = BuildEnvelope.Classify(envelope, A1Args);

      Assert.Equal("A1", env.Address);
      Assert.Equal(RegionOutcome.Built, env.Outcome);
      Assert.Equal(2, env.Placed);
      Assert.Equal(2, env.AttemptsLeft);
      Assert.Equal(new[] { "Rows = 40" }, env.State);
    }

    [Fact]
    public void An_applied_verdict_with_new_errors_is_BuiltWithErrors()
    {
      var env = BuildEnvelope.Classify(ToolResult.Success(Verdict(true, newErrors: 3)).ToJson(), A1Args);
      Assert.Equal(RegionOutcome.BuiltWithErrors, env.Outcome);
    }

    /// <summary>
    /// THE FINDING. A verdict that failed and reverted travels as ToolResult.Fail(verification_failed, message, verdict),
    /// whose verdict is error.detail. It is REVERTED, with the verdict's own note - never "refused before anything was
    /// touched" with the message.
    /// </summary>
    [Fact]
    public void A_reverted_verdict_is_Reverted_with_the_verdicts_own_note()
    {
      var envelope = ToolResult.Fail(ToolErrors.VerificationFailed,
        "The build was applied, failed its own check, and was reverted - the canvas is as it was.",
        Verdict(false, note: "It changed part of the existing definition, which a build must never do.")).ToJson();

      var env = BuildEnvelope.Classify(envelope, A1Args);

      Assert.Equal(RegionOutcome.Reverted, env.Outcome);
      Assert.Equal("It changed part of the existing definition, which a build must never do.", env.Note);
    }

    /// <summary>
    /// #649 addition (1): THE TIDY'S ATTEMPTS-LEFT READING. Before, a revert had no verdict, so attempts-left read 0 and
    /// the tidy was told the reverted region had spent its whole budget. After, it is the verdict's own value.
    /// </summary>
    [Fact]
    public void A_reverted_verdict_carries_its_own_attempts_left()
    {
      var envelope = ToolResult.Fail(ToolErrors.VerificationFailed, "reverted", Verdict(false, attemptsLeft: 1)).ToJson();

      Assert.Equal(1, BuildEnvelope.Classify(envelope, A1Args).AttemptsLeft);
    }

    /// <summary>The exception and timeout reverts carry no verdict; their code still says the change was applied and
    /// reverted, so they are REVERTED too.</summary>
    [Fact]
    public void A_verification_failure_with_no_verdict_is_still_Reverted()
    {
      var envelope = ToolResult.Fail(ToolErrors.VerificationFailed,
        "The definition did not finish solving, so the build could not be verified. It was reverted.").ToJson();

      var env = BuildEnvelope.Classify(envelope, A1Args);

      Assert.Equal(RegionOutcome.Reverted, env.Outcome);
      Assert.Null(env.AttemptsLeft);
    }

    /// <summary>A refusal's own detail (a cause, a count) is not a verdict: it is REFUSED, its message is the note, and
    /// its region comes from the call's arguments.</summary>
    [Fact]
    public void A_refusal_with_a_non_verdict_detail_is_Refused()
    {
      var envelope = ToolResult.Fail(ToolErrors.RegionOccupied, "A1 holds 3 component(s) Dwight did not place.",
        new JsonObject { ["region"] = "A1", ["componentsInRegion"] = 3 }).ToJson();

      var env = BuildEnvelope.Classify(envelope, A1Args);

      Assert.Equal(RegionOutcome.Refused, env.Outcome);
      Assert.Equal("A1 holds 3 component(s) Dwight did not place.", env.Note);
      Assert.Null(env.AttemptsLeft);
      Assert.Equal(0, env.Placed);
    }

    // ---- #625/#647 through the envelope: reconnections, and the "refused: would cut" class ----------------

    [Fact]
    public void An_applied_verdict_carries_what_it_reconnected_and_restored()
    {
      var verdict = BuildVerdict.Build(true, new List<int> { 7 }, 0, 0, Preserved(), 1, 0, new JsonArray(), region: "A1",
        reconnected: new JsonArray(
          new JsonObject { ["consumer"] = 19, ["consumerType"] = "XY Plane", ["consumerRegion"] = "B1", ["input"] = "Origin", ["restored"] = false }));

      var env = BuildEnvelope.Classify(ToolResult.Success(verdict).ToJson(), A1Args);

      var r = Assert.Single(env.Reconnected);
      Assert.Equal("XY Plane (19) in B1 (Origin)", r.Label);
      Assert.False(env.WouldCut);
    }

    /// <summary>
    /// A boundary refusal, as build_region actually returns it: REFUSED, flagged would-cut, and its note built from the
    /// refusal's structure - never the model-addressed message, which says "tell the user..." and would otherwise land
    /// on the user's running line (#615).
    /// </summary>
    [Fact]
    public void A_boundary_refusal_is_Refused_would_cut_with_a_note_from_its_structure()
    {
      var cut = new CutWire { ConsumerId = 19, ConsumerType = "XY Plane", ConsumerRegion = "B1", InputLabel = "Origin",
                              SourceType = "Construct Point", SourceOutputLabel = "Point" };
      Assert.False(RebuildBoundary.Match("A1", new[] { cut }, new BuildReconnect[0], out _, out var refusal));

      var env = BuildEnvelope.Classify(refusal.ToJson(), A1Args);

      Assert.Equal(RegionOutcome.Refused, env.Outcome);
      Assert.True(env.WouldCut);
      Assert.Equal("its rebuild would have cut the wire into XY Plane (19) in B1 input Origin without reconnecting it, " +
                   "so nothing was changed", env.Note);
      Assert.DoesNotContain("tell the user", env.Note);
      Assert.Empty(env.Reconnected);
    }

    [Fact]
    public void An_unaddressed_or_unreadable_result_has_no_row()
    {
      Assert.Null(BuildEnvelope.Classify(ToolResult.Success(new JsonObject { ["status"] = "applied" }).ToJson(), "{}"));
      Assert.Null(BuildEnvelope.Classify("not json", A1Args));
      Assert.Null(BuildEnvelope.Classify(null, A1Args));
    }

    // ---- #649 addition (2): every verdict build_region emits goes through ToolResult --------------------

    /// <summary>
    /// A TEXTUAL CONFORMANCE CHECK, and its bound is stated: GhBuildRegion is GH-coupled and not linked into these
    /// tests, so this reads its SOURCE. Every BuildVerdict.Build(...) must be an argument of a ToolResult.Success or
    /// ToolResult.Fail in the same statement - so every verdict reaches the panel in the envelope Classify is tested
    /// against - and no build-result reader may look for "data" again. It proves the source's shape, not its behaviour.
    /// </summary>
    [Fact]
    public void Every_verdict_build_region_emits_is_wrapped_in_a_ToolResult()
    {
      var source = File.ReadAllText(Path.Combine(RepoRoot(), "Dwight", "Agent", "GhBuildRegion.cs"));
      var builds = Regex.Matches(source, @"BuildVerdict\.Build\(").Cast<Match>().ToList();

      Assert.NotEmpty(builds);   // the check has something to check
      foreach (var m in builds)
      {
        var statementStart = source.LastIndexOf(';', m.Index) + 1;
        var statement = source.Substring(statementStart, m.Index - statementStart);
        Assert.True(statement.Contains("ToolResult.Success(") || statement.Contains("ToolResult.Fail("),
          "a BuildVerdict.Build at offset " + m.Index + " is not inside a ToolResult - its envelope is unclassified");
      }

      var panel = File.ReadAllText(Path.Combine(RepoRoot(), "Dwight", "UI", "DwightScaffold.cs"));
      Assert.DoesNotContain("[\"data\"]", panel);
      Assert.Contains("BuildEnvelope.Classify(", panel);
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
