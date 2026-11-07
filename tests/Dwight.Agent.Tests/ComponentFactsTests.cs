using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Dwight.Agent;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// Phase-9 U1 (reads-unification, #382(9)): what a component HOLDS, on the agent's reads.
  ///
  /// THE GAP. The study's section 2 found that the agent could not read a single value - not a slider's
  /// setting, not a panel's text, not a script's source - while the verbs have read all of them since
  /// Phase 7 (ADR-093, RR17 #36). In the WP-4 gate that blindness broke the agent's own build (#377).
  ///
  /// These tests are about the PROJECTION (GH-free, ADR-091): a fact that is present is emitted in a
  /// bounded, typed shape; a fact that is absent is OMITTED, never emitted as an empty or a guess; and
  /// the region brief carries only the compact hint, never the payload.
  /// </summary>
  public class ComponentFactsTests
  {
    private static Guid G(int n) { var b = new byte[16]; b[0] = (byte)n; b[1] = (byte)(n >> 8); return new Guid(b); }

    private static (AgentSnapshot snap, AgentIdRegistry ids, int slider, int panel, int script, int curve, int loft, int plug) Fixture()
    {
      var ids = new AgentIdRegistry();
      ids.Sync(new[] { G(1), G(2), G(3), G(4), G(5), G(6) });
      var slider = ids.Issue(G(1)); var panel = ids.Issue(G(2)); var script = ids.Issue(G(3));
      var curve = ids.Issue(G(4)); var loft = ids.Issue(G(5)); var plug = ids.Issue(G(6));

      var snap = new AgentSnapshot
      {
        Nodes =
        {
          new AgentNode
          {
            Id = slider, Type = "Number Slider", Kind = "param", Outputs = new[] { "N" }, OutCounts = new[] { 1 },
            Constant = "40",
            Value = new ValueDto { Kind = "slider", Value = 40m, Min = 0m, Max = 100m, Rounding = "integer", Decimals = 0 },
          },
          new AgentNode
          {
            Id = panel, Type = "Panel", Kind = "param", Nick = "notes",
            Constant = "\"hello\"",
            Value = new ValueDto { Kind = "panel", Text = "hello", TextTotalChars = 5 },
          },
          new AgentNode
          {
            Id = script, Type = "Python 3 Script", Kind = "component", Inputs = new[] { "x" }, Outputs = new[] { "a" },
            ScriptLanguage = "python",
            Source = new SourceDto { Language = "python", Text = "a = x * 2", TotalLines = 1, TotalChars = 9 },
          },
          new AgentNode
          {
            Id = curve, Type = "Curve", Kind = "param", Outputs = new[] { "Crv" }, OutCounts = new[] { 3 },
            Persistent = { new PersistentDto { Param = "Crv", Count = 3, Paths = 1, Item = "PolylineCurve (4 segments), closed, planar" } },
          },
          new AgentNode { Id = loft, Type = "Loft", Kind = "component", Inputs = new[] { "Curves", "Options" }, Outputs = new[] { "Loft" } },
          new AgentNode
          {
            Id = plug, Type = "Solver", Kind = "component", Inputs = new[] { "Goals" }, Outputs = new[] { "O" },
            Plugin = new PluginDto { Library = "Kangaroo2", Does = "Physics solver." },
          },
        },
        Clusters = { new AgentCluster { Id = 0, Label = "Cluster 0", Members = { slider, panel, script, curve, loft, plug } } },
      };
      return (snap, ids, slider, panel, script, curve, loft, plug);
    }

    private static JsonObject Args(string json) => (JsonObject)JsonNode.Parse(json);
    private static JsonObject Component(AgentSnapshot snap, AgentIdRegistry ids, int id)
    {
      var r = AgentToolCore.GetComponent(snap, ids, Args("{\"id\":" + id + "}"));
      Assert.True(r.Ok, r.Code + ": " + r.Message);
      return (JsonObject)r.Result;
    }

    [Fact]
    public void A_SLIDER_REPORTS_ITS_VALUE_DOMAIN_ROUNDING_AND_DECIMALS()
    {
      var (snap, ids, slider, _, _, _, _, _) = Fixture();
      var v = (JsonObject)Component(snap, ids, slider)["value"];

      Assert.Equal("slider", v["kind"].GetValue<string>());
      Assert.Equal(40m, v["value"].GetValue<decimal>());
      Assert.Equal(0m, v["min"].GetValue<decimal>());
      Assert.Equal(100m, v["max"].GetValue<decimal>());
      Assert.Equal("integer", v["rounding"].GetValue<string>());
      Assert.Equal(0, v["decimals"].GetValue<int>());
    }

    [Fact]
    public void A_PANEL_REPORTS_ITS_TEXT_AND_A_TRUNCATED_PANEL_SAYS_SO()
    {
      var (snap, ids, _, panel, _, _, _, _) = Fixture();
      var v = (JsonObject)Component(snap, ids, panel)["value"];
      Assert.Equal("panel", v["kind"].GetValue<string>());
      Assert.Equal("hello", v["text"].GetValue<string>());
      Assert.Null(v["truncated"]);

      snap.NodeById(panel).Value = new ValueDto { Kind = "panel", Text = "hel", TextTruncated = true, TextTotalChars = 5000 };
      v = (JsonObject)Component(snap, ids, panel)["value"];
      Assert.True(v["truncated"].GetValue<bool>());
      Assert.Equal(5000, v["chars"].GetValue<int>());
    }

    [Fact]
    public void A_FED_PANEL_REPORTS_THE_VALUES_IT_DISPLAYS_AND_SAYS_SO_NEVER_ITS_PLACEHOLDER_TEXT()
    {
      // U31 (#926): the GFA and height panels the model had built read back "Double click to edit panel
      // content..." - the UserText - while their volatile data held 8672.21 and 61.2. The unfed case above is
      // unchanged; the fed case returns the displayed values and says that is what they are.
      var (snap, ids, _, panel, _, _, _, _) = Fixture();
      snap.NodeById(panel).Value = PanelDisplay.Fed(new[] { "8672.21" }, totalCount: 1, paths: 1, maxItems: 25, maxChars: 400);
      snap.NodeById(panel).Constant = null;

      var c = Component(snap, ids, panel);
      var v = (JsonObject)c["value"];
      Assert.Equal("panel", v["kind"].GetValue<string>());
      Assert.Equal("8672.21", v["displayed"].AsArray()[0].GetValue<string>());
      Assert.Equal(1, v["displayedCount"].GetValue<int>());
      Assert.Equal(1, v["displayedPaths"].GetValue<int>());
      Assert.False(v.ContainsKey("text"));                 // the placeholder is not a fact about the definition
      Assert.False(v.ContainsKey("displayedTruncated"));
      Assert.Equal(PanelDisplay.Means, c["means"]["displayed"].GetValue<string>());
      Assert.Contains("not text the user typed", c["means"]["displayed"].GetValue<string>());
    }

    [Fact]
    public void A_FED_PANEL_CUT_BY_THE_BOUND_SAYS_HOW_MANY_IT_SHOWS_AND_HOW_MANY_THERE_ARE()
    {
      var (snap, ids, _, panel, _, _, _, _) = Fixture();
      var first = Enumerable.Range(0, 25).Select(i => (i * 0.5).ToString(System.Globalization.CultureInfo.InvariantCulture)).ToList();
      snap.NodeById(panel).Value = PanelDisplay.Fed(first, totalCount: 486, paths: 5, maxItems: 25, maxChars: 400);

      var v = (JsonObject)Component(snap, ids, panel)["value"];
      Assert.True(v["displayedTruncated"].GetValue<bool>());
      Assert.Equal(486, v["displayedCount"].GetValue<int>());
      Assert.Equal(v["displayed"].AsArray().Count, v["displayedShown"].GetValue<int>());
      Assert.Equal(5, v["displayedPaths"].GetValue<int>());
    }

    [Fact]
    public void A_READABLE_SCRIPT_REPORTS_ITS_LANGUAGE_AND_SOURCE()
    {
      var (snap, ids, _, _, script, _, _, _) = Fixture();
      var s = (JsonObject)Component(snap, ids, script)["script"];

      Assert.Equal("python", s["language"].GetValue<string>());
      Assert.Equal("a = x * 2", s["source"].GetValue<string>());
      Assert.Equal(1, s["lines"].GetValue<int>());
      Assert.Null(s["unreadable"]);
      Assert.Null(s["omitted"]);
    }

    [Fact]
    public void A_TRUNCATED_SCRIPT_STATES_WHAT_IT_OMITTED()
    {
      var (snap, ids, _, _, script, _, _, _) = Fixture();
      snap.NodeById(script).Source = new SourceDto
      {
        Language = "python", Text = "a = 1\n# [Dwight cut this here: 300 more lines not shown; 9000 chars in total]",
        TotalLines = 301, TotalChars = 9000, Truncated = true, Omitted = "300 more lines not shown; 9000 chars in total",
      };
      var s = (JsonObject)Component(snap, ids, script)["script"];

      Assert.True(s["truncated"].GetValue<bool>());
      Assert.Equal("300 more lines not shown; 9000 chars in total", s["omitted"].GetValue<string>());
      Assert.Equal(301, s["lines"].GetValue<int>());
    }

    [Fact]
    public void AN_UNREADABLE_SCRIPT_IS_SAID_TO_BE_UNREADABLE_NEVER_SHOWN_AS_EMPTY()
    {
      // Null source is a first-class answer (RR17 #36): a locked or encrypted script is a black box,
      // and "no code" would be a lie the model would reason from.
      var (snap, ids, _, _, script, _, _, _) = Fixture();
      snap.NodeById(script).Source = null;
      var s = (JsonObject)Component(snap, ids, script)["script"];

      Assert.Equal("python", s["language"].GetValue<string>());
      Assert.True(s["unreadable"].GetValue<bool>());
      Assert.Null(s["source"]);
    }

    [Fact]
    public void PERSISTENT_DATA_IS_REPORTED_AS_A_TYPE_A_COUNT_AND_A_PATH_COUNT()
    {
      // ADR-093's shape exactly: a value, a type, a count - never the geometry.
      var (snap, ids, _, _, _, curve, _, _) = Fixture();
      var p = (JsonArray)Component(snap, ids, curve)["persistent"];

      Assert.Single(p);
      Assert.Equal("Crv", p[0]["param"].GetValue<string>());
      Assert.Equal(3, p[0]["count"].GetValue<int>());
      Assert.Equal(1, p[0]["paths"].GetValue<int>());
      Assert.Equal("PolylineCurve (4 segments), closed, planar", p[0]["item"].GetValue<string>());
    }

    [Fact]
    public void A_PLUGIN_COMPONENT_NAMES_ITS_LIBRARY_AND_WHAT_IT_DOES()
    {
      var (snap, ids, _, _, _, _, _, plug) = Fixture();
      var p = (JsonObject)Component(snap, ids, plug)["plugin"];

      Assert.Equal("Kangaroo2", p["library"].GetValue<string>());
      Assert.Equal("Physics solver.", p["does"].GetValue<string>());
    }

    [Fact]
    public void A_COMPONENT_WITH_NONE_OF_THESE_FACTS_CARRIES_NONE_OF_THESE_FIELDS()
    {
      // Absent means absent. An empty "value" or a null "script" would invite the model to reason about
      // a fact that was never read.
      var (snap, ids, _, _, _, _, loft, _) = Fixture();
      var c = Component(snap, ids, loft);

      Assert.False(c.ContainsKey("value"));
      Assert.False(c.ContainsKey("script"));
      Assert.False(c.ContainsKey("persistent"));
      Assert.False(c.ContainsKey("plugin"));
    }

    [Fact]
    public void THE_REGION_BRIEF_CARRIES_THE_HINT_AND_NEVER_THE_PAYLOAD()
    {
      // get_region lists many nodes; a source dump per node would swamp it. The brief says "this is a
      // python script" and "this slider reads 40" - enough to decide what to drill into.
      var (snap, ids, slider, _, script, _, loft, _) = Fixture();
      var region = AgentToolCore.GetRegion(snap, Args("{\"id\":0}"), AgentToolCore.ShapeHash(snap));
      Assert.True(region.Ok, region.Code + ": " + region.Message);
      var nodes = ((JsonArray)region.Result["nodes"]).Cast<JsonObject>().ToDictionary(n => n["id"].GetValue<int>());

      Assert.Equal("40", nodes[slider]["value"].GetValue<string>());
      Assert.Equal("python", nodes[script]["script"].GetValue<string>());
      Assert.False(nodes[script].ContainsKey("source"));
      Assert.False(nodes[loft].ContainsKey("value"));
      Assert.False(nodes[loft].ContainsKey("script"));
    }
  }
}
