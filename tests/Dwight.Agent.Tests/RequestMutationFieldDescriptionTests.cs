using System.Collections.Generic;
using System.Text.Json.Nodes;
using Dwight.Agent;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// #907's CLASS, closed by a test over the DESCRIPTIONS rather than the schema's <c>required</c> array -
  /// which cannot express a per-kind requirement at all (<c>target</c> is required for set_value AND
  /// edit_script, optional for everything else), so no structural check
  /// (<c>DescriptorConformanceTests</c>, <c>PublishedSchemaTests</c>) could ever have seen #907's bug: a
  /// field the parser demands for one kind, described as belonging to a different one.
  ///
  /// THE (kind, field) PAIRS ARE NOT HAND-LISTED - a hand list would have agreed with #907's bug, which is
  /// exactly the failure mode this closes (the architect's condition on the fix). Each pair is discovered by
  /// actually RUNNING the real ask pipeline - <see cref="AgentHostTools.TryParseRequest"/>, then the kind's
  /// own <c>Validate</c>, exactly as <c>DwightScaffold.cs</c> calls them - with one field missing from an
  /// otherwise-valid request. If the ask refuses, the parser DEMANDS that field for that kind, and the
  /// published description of that field must say so BY NAME - the same "kind=X" convention every other
  /// per-kind field already uses.
  ///
  /// SEEN TO FAIL: reverting <c>target</c>'s description to "For kind=set_value ONLY" turns the
  /// (edit_script, target) row red.
  /// </summary>
  public class RequestMutationFieldDescriptionTests
  {
    private static JsonObject Args(string json) => (JsonObject)JsonNode.Parse(json);

    private static JsonObject RequestMutationProperties() =>
      (JsonObject)AgentHostTools.All()[0].InputSchema["properties"];

    private static string DescriptionOf(string field) =>
      RequestMutationProperties()[field]?["description"]?.GetValue<string>() ?? "";

    private static readonly LiveTarget Slider = new LiveTarget
    {
      Type = "Number Slider",
      Current = new BuildState { Kind = BuildStateKind.Slider, Min = 0, Max = 1, Value = 0.25m },
    };

    private static readonly LiveScript Script = new LiveScript
    {
      Type = "RhinoCodePluginGH.Components.Python3Component",
      Source = "a = x\n", SourceReadable = true,
      Inputs = new[] { "x" }, Outputs = new[] { "a" },
    };

    /// <summary>Runs the SAME two-stage pipeline the panel runs (TryParseRequest, then the kind's own ask) and
    /// answers whether the ASK refused. Never inspects WHY - only whether removing one field, alone, turns a
    /// valid request into a refused one.</summary>
    private static bool Refused(string kind, JsonObject args)
    {
      Assert.True(AgentHostTools.TryParseRequest(args, out var parsedKind, out _, out var seed, out var parseError),
        "the request was refused at the parse stage, before the ask under test ever ran: " + parseError?.Message);
      Assert.Equal(kind, parsedKind);

      switch (kind)
      {
        case "set_value":
          return !SetValueAsk.Validate(seed, _ => IdState.Alive, _ => Slider, out _, out _, out _);
        case "edit_script":
          return !EditScriptAsk.Validate(seed, _ => IdState.Alive, _ => Script, out _, out _);
        default:
          throw new System.ArgumentException("no ask wired up for kind " + kind);
      }
    }

    /// <summary>One (kind, field) candidate: a MINIMAL VALID baseline for the kind (mechanical wiring, not a
    /// requiredness claim - the baseline's own validity is asserted before anything else runs) and the field
    /// to try removing. Whether that field turns out to be required is what <see cref="Refused"/> measures,
    /// never this table.</summary>
    public static IEnumerable<object[]> Candidates()
    {
      const string setValueBaseline = "{\"kind\":\"set_value\",\"why\":\"t\",\"target\":1,\"state\":{\"min\":0,\"max\":10,\"value\":5}}";
      const string editScriptBaseline = "{\"kind\":\"edit_script\",\"why\":\"t\",\"target\":1,\"claim\":\"changes\",\"source\":\"a = 2\\n\"}";

      yield return new object[] { "set_value", setValueBaseline, "target" };
      yield return new object[] { "set_value", setValueBaseline, "state" };
      yield return new object[] { "edit_script", editScriptBaseline, "target" };
      yield return new object[] { "edit_script", editScriptBaseline, "claim" };
      yield return new object[] { "edit_script", editScriptBaseline, "source" };
    }

    [Theory]
    [MemberData(nameof(Candidates))]
    public void A_FIELD_THE_PARSER_DEMANDS_FOR_A_KIND_NAMES_THAT_KIND_IN_ITS_OWN_DESCRIPTION(
      string kind, string baselineJson, string field)
    {
      Assert.False(Refused(kind, Args(baselineJson)),
        kind + "'s own baseline must be VALID on its own terms, or this row proves nothing about \"" + field + "\"");

      var stripped = Args(baselineJson);
      stripped.Remove(field);
      if (!Refused(kind, stripped))
        return;   // not actually required for this kind at this layer - nothing to assert

      Assert.Contains("kind=" + kind, DescriptionOf(field));
    }
  }
}
