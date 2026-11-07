using System.Text.Json.Nodes;
using Dwight.Agent;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// Phase-9 U20 sitting (#907-#909): edit_script as a request_mutation KIND - the args-to-seed wiring that
  /// <c>EditScriptAskTests</c> never exercises, because it constructs <see cref="HandoffSeed"/> by hand and so
  /// never runs <see cref="AgentHostTools.TryParseRequest"/> at all.
  ///
  /// FOUND FIXING #907 (the "target" schema-wording defect): <c>TryParseRequest</c> read kind, why,
  /// components, mode, regions, target and state from args - and NEVER claim, source, add_inputs or
  /// add_outputs, anywhere. So an edit_script <c>request_mutation</c> call was refused at
  /// <see cref="EditScriptAsk"/>'s claim check (<c>seed.Claim</c> permanently null) regardless of what the
  /// model sent, even once target's wording is fixed. <c>SeedPlan.From</c> (HandoffSeed.cs) already reads
  /// <c>seed.Claim</c>/<c>Source</c>/<c>AddInputs</c>/<c>AddOutputs</c> assuming an upstream parse populated
  /// them - none did.
  /// </summary>
  public class EditScriptKindTests
  {
    private static JsonObject Args(string json) => (JsonObject)JsonNode.Parse(json);

    [Fact]
    public void EDIT_SCRIPT_IS_AN_OFFERABLE_KIND_AND_ITS_ASK_CARRIES_TARGET_CLAIM_SOURCE_AND_PORTS_ON_THE_SEED()
    {
      Assert.Contains("edit_script", AgentHostTools.Kinds);

      var ok = AgentHostTools.TryParseRequest(
        Args("{\"kind\":\"edit_script\",\"why\":\"add an offset input\",\"target\":42," +
             "\"claim\":\"changes\",\"source\":\"a = (x + offset) ** 2\\n\"," +
             "\"add_inputs\":[\"offset\"],\"add_outputs\":[\"area\"]}"),
        out var kind, out _, out var seed, out var error);

      Assert.True(ok, error?.Message);
      Assert.Equal("edit_script", kind);
      Assert.Equal(42, seed.Target);
      Assert.Equal("changes", seed.Claim);
      Assert.Equal("a = (x + offset) ** 2\n", seed.Source);
      Assert.Equal(new[] { "offset" }, seed.AddInputs);
      Assert.Equal(new[] { "area" }, seed.AddOutputs);
    }

    /// <summary>The mirror of set_value's malformed-target test: absent or malformed is simply absent, never
    /// guessed, and it is <see cref="EditScriptAsk"/>'s job to refuse it by name - which it can only do once
    /// the field actually reaches the seed.</summary>
    [Fact]
    public void A_REQUEST_WITH_NO_CLAIM_OR_SOURCE_LEAVES_THEM_ABSENT_NOT_GUESSED()
    {
      Assert.True(AgentHostTools.TryParseRequest(
        Args("{\"kind\":\"edit_script\",\"why\":\"x\",\"target\":42}"),
        out _, out _, out var seed, out _));

      Assert.Null(seed.Claim);
      Assert.Null(seed.Source);
      Assert.Empty(seed.AddInputs);
      Assert.Empty(seed.AddOutputs);
    }

    /// <summary>The architect's item 3: the append-only refusal <see cref="EditScriptAskTests"/> already
    /// covers by constructing the seed directly, now driven end to end from the model's own JSON - through
    /// the arrival path this unit fixes, never around it.</summary>
    [Fact]
    public void AN_APPENDED_INPUT_COLLIDING_WITH_AN_EXISTING_PORT_IS_REFUSED_WHEN_DRIVEN_FROM_ARGS()
    {
      Assert.True(AgentHostTools.TryParseRequest(
        Args("{\"kind\":\"edit_script\",\"why\":\"t\",\"target\":1,\"claim\":\"changes\",\"source\":\"a = 2\\n\"," +
             "\"add_inputs\":[\"x\"]}"),
        out _, out _, out var seed, out _));

      Assert.False(EditScriptAsk.Validate(seed, _ => IdState.Alive,
        _ => new LiveScript
        {
          Type = "RhinoCodePluginGH.Components.Python3Component", Source = "a = 1\n", SourceReadable = true,
          Inputs = new[] { "x" }, Outputs = new[] { "a" },
        },
        out _, out var error));
      Assert.Contains("already has an input called", error.ToJson());
    }
  }
}
