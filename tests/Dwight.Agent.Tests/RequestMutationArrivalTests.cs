using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Dwight.Agent;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// #907-#909's law, generalised (the architect's ruling): TEST A READER AGAINST THE WRITER'S REAL OUTPUT.
  /// The writer here is the model, and its real output is JSON args - not a hand-built <see cref="HandoffSeed"/>.
  /// Every existing edit_script test (<c>EditScriptAskTests</c>) constructs the seed directly and hands it to
  /// <see cref="EditScriptAsk"/>, so all of them tested the SEED path and none tested the ARRIVAL path -
  /// <see cref="AgentHostTools.TryParseRequest"/>, the one place a model's JSON becomes a seed. That is
  /// exactly how <c>claim</c>, <c>source</c>, <c>add_inputs</c> and <c>add_outputs</c> could be published in
  /// the schema, read by nothing, and never fail a single test: nothing tested the reading.
  ///
  /// SO THIS TEST WALKS THE PUBLISHED SCHEMA rather than listing fields by hand - a hand list would have
  /// agreed with the bug it exists to catch, on the day it shipped. Every property <c>request_mutation</c>
  /// advertises must have a registered landing spot on the seed (or on <c>kind</c>/<c>why</c>, the two
  /// out-parameters that are not seed fields); a property with none is refused coverage rather than silently
  /// skipped, so a FUTURE field nobody wires fails this test by construction, not by someone remembering to
  /// extend a list.
  ///
  /// SEEN TO FAIL: with today's fix reverted (<c>TryParseRequest</c> not reading claim/source/add_inputs/
  /// add_outputs), the arrival assertion is red on all four.
  /// </summary>
  public class RequestMutationArrivalTests
  {
    /// <summary>One property: the distinctive JSON it sends, and where that value must land. The value
    /// returned is compared by its canonical JSON form, so arrays/objects compare structurally rather than
    /// by reference.</summary>
    private static readonly Dictionary<string, (string ArgsJson, System.Func<string, string, HandoffSeed, JsonNode> Landed)> Map =
      new Dictionary<string, (string, System.Func<string, string, HandoffSeed, JsonNode>)>
      {
        // kind and why are TryParseRequest's own out-parameters, not seed fields - but why also lands on
        // the seed (SeedPlan reads it from there), so both are worth arriving-testing from the one call.
        ["kind"] = ("\"edit_script\"", (k, w, s) => JsonValue.Create(k)),
        ["why"] = ("\"a distinctive reason so a swap is obvious\"", (k, w, s) => JsonValue.Create(s.Why)),
        ["components"] = ("[7,9]", (k, w, s) => new JsonArray(s.ComponentIds.Select(i => (JsonNode)JsonValue.Create(i)).ToArray())),
        ["mode"] = ("\"grid\"", (k, w, s) => s.Mode == null ? null : JsonValue.Create(s.Mode)),
        ["regions"] = ("[\"A7\"]", (k, w, s) => new JsonArray(s.Regions.Select(r => (JsonNode)JsonValue.Create(r)).ToArray())),
        ["target"] = ("99", (k, w, s) => s.Target.HasValue ? JsonValue.Create(s.Target.Value) : null),
        ["claim"] = ("\"preserves\"", (k, w, s) => s.Claim == null ? null : JsonValue.Create(s.Claim)),
        ["source"] = ("\"a = distinctive_source\\n\"", (k, w, s) => s.Source == null ? null : JsonValue.Create(s.Source)),
        ["add_inputs"] = ("[\"offset\"]", (k, w, s) => new JsonArray(s.AddInputs.Select(i => (JsonNode)JsonValue.Create(i)).ToArray())),
        ["add_outputs"] = ("[\"area\"]", (k, w, s) => new JsonArray(s.AddOutputs.Select(i => (JsonNode)JsonValue.Create(i)).ToArray())),
        ["set_access"] = ("[{\"name\":\"gcode\",\"access\":\"list\"}]", (k, w, s) => new JsonArray(s.InputAccess.Select(kv => (JsonNode)new JsonObject { ["name"] = kv.Key, ["access"] = kv.Value }).ToArray())),
        ["state"] = ("{\"text\":\"a distinctive panel text\"}", (k, w, s) => s.State),
        // Phase-9 U6 + U21: edit's changes, carried as a raw array until EditOpsAsk resolves every row against the canvas.
        ["ops"] = ("[{\"op\":\"delete\",\"id\":4},{\"op\":\"wire\",\"from\":{\"id\":3,\"param\":\"Result\"},\"to\":{\"id\":6}}]",
                   (k, w, s) => s.Ops),
        // U18: the spot placement, carried as a raw JsonObject nomination until SpotAsk resolves it (#862).
        // U30: a constant rides inside `place` as a component's state ({"value": 12} on a Number param), so the fixture
        // carries one - the landing spot is the whole object, and this is what proves the field survives arrival.
        ["place"] = ("{\"components\":[{\"ref\":\"c1\",\"name\":\"a distinctive component\"}," +
                     "{\"ref\":\"h\",\"type\":\"Number\",\"nick\":\"Height\",\"state\":{\"value\":12}}],\"wires\":[]}", (k, w, s) => s.Place),
        // U65: the scripts a region build card shows whole, carried raw until ScriptsAsk resolves them at the ask.
        ["scripts"] = ("[{\"region\":\"A1\",\"type\":\"Python 3 Script\",\"source\":\"a = distinctive_script\\n\"}]", (k, w, s) => s.Scripts),
      };

    private static IEnumerable<string> PublishedProperties() =>
      ((JsonObject)AgentHostTools.All()[0].InputSchema["properties"]).Select(kv => kv.Key);

    [Fact]
    public void EVERY_PUBLISHED_PROPERTY_HAS_A_REGISTERED_LANDING_SPOT()
    {
      // The coverage guarantee: a property in the schema with nothing in this map is #907's class waiting
      // to happen again - a field the model is told to send that nothing here has been taught to check.
      foreach (var p in PublishedProperties())
        Assert.True(Map.ContainsKey(p),
          p + ": request_mutation publishes it, but this test has no landing spot registered for it - " +
          "add one (and confirm TryParseRequest actually reads it) before shipping the field");

      // And the reverse: no entry surviving here for a field the schema no longer has.
      foreach (var p in Map.Keys)
        Assert.Contains(p, PublishedProperties());
    }

    [Fact]
    public void EVERY_PUBLISHED_PROPERTY_ACTUALLY_ARRIVES_ON_THE_SEED()
    {
      var args = new JsonObject();
      foreach (var (prop, (json, _)) in Map)
        args[prop] = JsonNode.Parse(json);

      Assert.True(AgentHostTools.TryParseRequest(args, out var kind, out var why, out var seed, out var error),
        "the combined request was refused before arrival could even be checked: " + error?.Message);

      foreach (var (prop, (json, landed)) in Map)
      {
        var expected = JsonNode.Parse(json).ToJsonString();
        var actual = landed(kind, why, seed)?.ToJsonString();
        Assert.True(expected == actual,
          prop + ": sent " + json + " but the seed holds " + (actual ?? "<nothing>"));
      }
    }
  }
}
