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
  /// THE PRIVACY CONTRACT IS ONE TABLE AS DATA, AND THE TEST IS ON THE BEHAVIOUR'S SIDE (#316; the ruling
  /// on stream D's two-copies finding). Two texts agreeing with each other proves nothing - both can be
  /// wrong together - so this test BUILDS each agent payload from a fixture and asserts the set of JSON
  /// paths it emits EQUALS the table's field set. Add a field to a payload and this fails until the
  /// table says so; both the in-product block and the public page render from the table, so neither can
  /// drift from the other or from the code.
  ///
  /// The five VERBS' payloads need a live Grasshopper document to build (DwightGraph's edge holds the
  /// live source param so signatures are computed lazily; the ruling declined the split), so their rows
  /// carry AssertedBy.RuntimeSuitePending and stream F's runtime suite inherits the same assertion. A
  /// table that implied every row was checked, when half are not, would be a probe reporting OK on a
  /// path it never walked (#374).
  /// </summary>
  public class PayloadContractTests
  {
    private static Guid G(int n) { var b = new byte[16]; b[0] = (byte)n; b[1] = (byte)(n >> 8); return new Guid(b); }

    // ---- the table's own coherence --------------------------------------------------------------------

    [Fact]
    public void EVERY_ROW_HAS_A_SURFACE_A_STATE_AND_AT_LEAST_ONE_FIELD_WITH_A_CARRIES_AND_A_MEANING()
    {
      var rows = PayloadContract.All();
      Assert.NotEmpty(rows);
      foreach (var r in rows)
      {
        Assert.False(string.IsNullOrWhiteSpace(r.Surface), "a row has no surface");
        Assert.False(string.IsNullOrWhiteSpace(r.Title), r.Surface + ": no title");
        Assert.False(string.IsNullOrWhiteSpace(r.Sends), r.Surface + ": no one-line summary");
        Assert.NotEmpty(r.Fields);
        foreach (var f in r.Fields)
        {
          Assert.False(string.IsNullOrWhiteSpace(f.Path), r.Surface + ": a field has no path");
          Assert.False(string.IsNullOrWhiteSpace(f.Meaning), r.Surface + "." + f.Path + ": no meaning");
        }
        Assert.Equal(r.Fields.Count, r.Fields.Select(f => f.Path).Distinct().Count());
        if (r.AssertedBy == AssertedBy.RuntimeSuitePending)
          Assert.False(string.IsNullOrWhiteSpace(r.WaitingFor), r.Surface + ": a pending row must say what it waits for");
      }
      Assert.Equal(rows.Count, rows.Select(r => r.Surface).Distinct(StringComparer.Ordinal).Count());
    }

    // Brief EO: the privacy page prints these words, so they use the name a person reads. The mode's id is unchanged.
    [Fact]
    public void NO_WORD_A_PERSON_READS_IN_THE_CONTRACT_CALLS_THE_MODE_THE_WALK()
    {
      var texts = PayloadContract.All().SelectMany(r => new[] { r.Title, r.Sends, r.WaitingFor }.Concat(r.Fields.Select(f => f.Meaning)));
      foreach (var s in texts.Where(s => s != null))
        Assert.DoesNotMatch("(^|[^A-Za-z])Walk([^A-Za-z]|$)", s);
      Assert.Contains(PayloadContract.All().SelectMany(r => r.Fields), f => f.Meaning.Contains("Step by step"));
    }

    [Fact]
    public void THE_VERB_ROWS_ARE_THE_ONES_MARKED_PENDING_AND_THE_AGENT_ROWS_ARE_UNIT_TESTED()
    {
      var pending = PayloadContract.All().Where(r => r.AssertedBy == AssertedBy.RuntimeSuitePending).Select(r => r.Surface).OrderBy(s => s, StringComparer.Ordinal).ToArray();
      Assert.Equal(new[]
      {
        "diagnose", "explain.detailed", "explain.oncanvas", "explain.short", "label.clusters",
        "simplify.betterlogic", "simplify.toscript",
        "tool:build_region", "tool:get_scaffold_map", "tool:propose_scaffold", "tool:run_script",
      }, pending);

      // Brief DN: explain.learn is the one verb row held by a unit test, because TeachingCall.Payload is GH-free and the test builds the real payload (EXPLAIN_LEARN_EMITS_EXACTLY_ITS_ROW).
      // Brief DX: chat:image is the chat's own surface and no tool of the model's, and CHAT_IMAGE_EMITS_EXACTLY_ITS_ROW holds it the same way.
      // Brief EO: explain.learn.judge, a typed Learn answer's judge, is held the same way by EXPLAIN_LEARN_JUDGE_EMITS_EXACTLY_ITS_ROW.
      foreach (var r in PayloadContract.All().Where(r => r.AssertedBy == AssertedBy.UnitTest && r.Surface != "explain.learn" && r.Surface != "explain.learn.judge" && r.Surface != "chat:image"))
        Assert.StartsWith("tool:", r.Surface);
    }

    [Fact]
    public void A_VERBS_PAYLOAD_IS_SENT_ALWAYS_AND_A_CHAT_TOOLS_ONLY_ON_REQUEST()
    {
      // Stream D's distinction: flattening the two would claim the chat sends every script in the file.
      foreach (var r in PayloadContract.All())
        Assert.Equal(r.Surface.StartsWith("tool:") ? Scope.OnRequest : r.Surface.StartsWith("chat:") ? Scope.WhenAttached : Scope.Always, r.Scope);
      Assert.Contains("only for the components the model asks about", PayloadContract.SettingsHtml());
      Assert.Contains("Sent every time this runs.", PayloadContract.Markdown());
      Assert.Contains("only for the components it names", PayloadContract.Markdown());
    }

    [Fact]
    public void EVERY_CATALOGUE_TOOL_HAS_A_ROW()
    {
      // The catalogue IS the MCP list_tools response; a tool without a contract row is a payload the
      // privacy statement does not know about.
      foreach (var t in AgentToolCatalog.All())
        Assert.NotNull(PayloadContract.For("tool:" + t.Name));
    }

    // ---- the behaviour: every unit-tested row EQUALS what the projection emits over a rich fixture --------

    private static (AgentSnapshot snap, AgentIdRegistry ids) RichFixture()
    {
      var ids = new AgentIdRegistry();
      var guids = Enumerable.Range(1, 19).Select(G).ToArray();
      ids.Sync(guids);
      var id = guids.Select(g => ids.Issue(g)).ToArray();

      var snap = new AgentSnapshot
      {
        Nodes =
        {
          new AgentNode { Id = id[0], Type = "Number Slider", Kind = "param", Outputs = new[] { "N" }, OutCounts = new[] { 1 },
            Constant = "40", Value = new ValueDto { Kind = "slider", Value = 40m, Min = 0m, Max = 100m, Rounding = "integer", Decimals = 0 },
            Notes = new List<string> { "you: keep the offset at 40", "and 1 more" } },
          new AgentNode { Id = id[1], Type = "Panel", Kind = "param", Nick = "notes", Outputs = new[] { "P" }, Constant = "\"hi\"",
            Value = new ValueDto { Kind = "panel", Text = "hi", TextTruncated = true, TextTotalChars = 900 } },
          new AgentNode { Id = id[2], Type = "Python 3 Script", Kind = "component", Inputs = new[] { "x" }, Outputs = new[] { "a" },
            Msgs = { new MsgDto { Level = "warning", Text = "w" } }, OutCounts = new[] { 1 },
            ScriptLanguage = "python", Source = new SourceDto { Language = "python", Text = "a = x", TotalLines = 300, TotalChars = 9000, Truncated = true, Omitted = "299 more lines not shown; 9000 chars in total" } },
          new AgentNode { Id = id[3], Type = "C# Script", Kind = "component", Outputs = new[] { "a" }, ScriptLanguage = "csharp", Source = null },
          new AgentNode { Id = id[4], Type = "Curve", Kind = "param", Outputs = new[] { "Crv" }, OutCounts = new[] { 3 },
            Persistent = { new PersistentDto { Param = "Crv", Count = 3, Paths = 1, Item = "PolylineCurve" } } },
          new AgentNode { Id = id[5], Type = "Solver", Kind = "component", Inputs = new[] { "Goals" }, Outputs = new[] { "O" },
            Plugin = new PluginDto { Library = "Kangaroo2", Does = "Physics solver." } },
          new AgentNode { Id = id[6], Type = "Boolean Toggle", Kind = "param", Outputs = new[] { "B" }, Value = new ValueDto { Kind = "toggle", On = true } },
          new AgentNode { Id = id[7], Type = "Value List", Kind = "param", Outputs = new[] { "V" },
            Value = new ValueDto { Kind = "valuelist", Selected = new[] { "A" }, Options = 3 } },
          // Phase-9 U1(c): a readable cluster with an internal hook, a protected one, and an unreadable one.
          // Appended LAST so the positional indices the tests above use (Nodes[0] is the slider) stay true.
          new AgentNode { Id = id[8], Type = "Cluster", Kind = "component", Nick = "Doubler", Inputs = new[] { "x" }, Outputs = new[] { "y" },
            Cluster = new ClusterDto { Count = 250, Omitted = 50, Locked = true } },
          new AgentNode { Id = id[9], Type = "Cluster Input", Kind = "param", Nick = "x", Outputs = new[] { "x" }, Inside = id[8], Hook = "input" },
          new AgentNode { Id = id[10], Type = "Cluster", Kind = "component", Nick = "Secret", Cluster = new ClusterDto { Protected = true, Level = "Protected" } },
          new AgentNode { Id = id[11], Type = "Cluster", Kind = "component", Nick = "Odd", Cluster = new ClusterDto { Unreadable = "Document(null) returned null" } },
          // ... and inside Doubler: an output hook, a constant, a script, and a nested cluster (listed, not opened).
          new AgentNode { Id = id[12], Type = "Cluster Output", Kind = "param", Nick = "y", Inputs = new[] { "y" }, Outputs = new[] { "y" }, Inside = id[8], Hook = "output" },
          new AgentNode { Id = id[13], Type = "Number Slider", Kind = "param", Outputs = new[] { "N" }, Inside = id[8], Constant = "2", Notes = new List<string> { "you: a note inside a cluster" } },
          new AgentNode { Id = id[14], Type = "Python 3 Script", Kind = "component", Outputs = new[] { "a" }, Inside = id[8], ScriptLanguage = "python", Source = null },
          new AgentNode { Id = id[15], Type = "Cluster", Kind = "component", Nick = "Inner", Inside = id[8],
            Cluster = new ClusterDto { Unreadable = "a cluster inside a cluster is listed, not opened - one level per read" } },
          // U31 (#926): a FED panel, cut by the bound - its displayed values travel, its placeholder text does not.
          new AgentNode { Id = id[16], Type = "Panel", Kind = "param", Nick = "GFA", Inputs = new[] { "P" }, Outputs = new[] { "P" }, OutCounts = new[] { 486 },
            Value = PanelDisplay.Fed(Enumerable.Range(0, 25).Select(i => i.ToString()).ToList(), totalCount: 486, paths: 5, maxItems: 25, maxChars: 400) },
          // U25 (#915): two "Rectangle"s - the param at the top level (its nick matches the search query below) and
          // the component inside Doubler - each carrying the qualified form that names it.
          new AgentNode { Id = id[17], Type = "Rectangle", Kind = "param", Nick = "site", Outputs = new[] { "Rec" }, OutCounts = new[] { 1 },
            Qualified = "Params > Geometry > Rectangle" },
          new AgentNode { Id = id[18], Type = "Rectangle", Kind = "component", Inside = id[8], Inputs = new[] { "P", "X", "Y", "R" }, Outputs = new[] { "R", "L" },
            Qualified = "Curve > Primitive > Rectangle" },
        },
        Edges =
        {
          new AgentEdgeDto { FromNode = id[0], FromParam = "N", ToNode = id[2], ToParam = "x", Tree = new TreeSigDto { Paths = 1, Depth = 1, Items = new[] { 1 } } },
          new AgentEdgeDto { FromNode = id[2], FromParam = "a", ToNode = id[5], ToParam = "Goals", Tree = new TreeSigDto { Paths = 2, Depth = 2, Items = new[] { 1, 1 } } },
          new AgentEdgeDto { FromNode = id[1], FromParam = "P", ToNode = id[5], ToParam = "Goals" },   // no signature: the "empty" case
          new AgentEdgeDto { FromNode = id[9], FromParam = "x", ToNode = id[9], ToParam = "x", Tree = new TreeSigDto { Paths = 1, Depth = 1, Items = new[] { 1 } } },   // an internal wire
        },
        Clusters =
        {
          new AgentCluster { Id = 0, Label = "Inputs", Members = { id[0], id[1], id[2], id[8], id[10], id[11] } },
          new AgentCluster { Id = 1, Label = "Region 1", Members = { id[3], id[4], id[5], id[6], id[7], id[17] } },
        },
        Flow = { (0, 1) },
        ReadRevision = 3,   // U22: a stamped snapshot, so get_component and get_region state readRevision
      };
      return (snap, ids);
    }

    private static JsonObject Args(string json) => (JsonObject)JsonNode.Parse(json);

    private static HashSet<string> Emitted(params ToolResult[] results)
    {
      var paths = new HashSet<string>(StringComparer.Ordinal);
      foreach (var r in results)
      {
        Assert.True(r.Ok, r.Code + ": " + r.Message);
        paths.UnionWith(PayloadPaths.Collect(r.Result));
      }
      return paths;
    }

    private static void AssertRow(string surface, HashSet<string> emitted)
    {
      var row = PayloadContract.For(surface);
      Assert.True(row != null, surface + ": no row in the table");
      Assert.Equal(AssertedBy.UnitTest, row.AssertedBy);
      var declared = new HashSet<string>(row.Fields.Select(f => f.Path), StringComparer.Ordinal);
      var folded = PayloadPaths.Fold(emitted, declared);
      var missingFromTable = folded.Except(declared).OrderBy(s => s, StringComparer.Ordinal).ToArray();
      var missingFromPayload = declared.Except(folded).OrderBy(s => s, StringComparer.Ordinal).ToArray();
      Assert.True(missingFromTable.Length == 0, surface + ": the payload emits fields the table does not declare: " + string.Join(", ", missingFromTable));
      Assert.True(missingFromPayload.Length == 0, surface + ": the table declares fields the fixture never emitted: " + string.Join(", ", missingFromPayload));
    }

    /// <summary>Brief DN: Learn's one extra request is the one Explain payload that carries a value, so its row is held to the payload TeachingCall builds. Brief EI: a deck of every
    /// kind of card, planned by the plugin's own planner, so every field a deck can send is emitted.</summary>
    [Fact]
    public void EXPLAIN_LEARN_EMITS_EXACTLY_ITS_ROW()
    {
      System.Guid G(int n) => new System.Guid(n, 0, 0, new byte[8]);
      Dwight.UI.DeckNode N(int n, string type, string nick = null) => new Dwight.UI.DeckNode { Guid = G(n), Id = 10 + n, Type = type, Nick = nick };
      Dwight.UI.DeckWire W(int a, string ap, int b, string bp, int items = 1) => new Dwight.UI.DeckWire { From = G(a), FromPort = ap, To = G(b), ToPort = bp, Items = items };
      var input = new Dwight.UI.DeckInput
      {
        Nodes = new[] { N(1, "Number Slider", "U count"), N(2, "Divide Curve"), N(3, "List Length"), N(4, "Area"), N(5, "Move"), N(6, "Loft") },
        Wires = new[] { W(1, "N", 2, "Count"), W(2, "Points", 3, "List", 12), W(2, "Points", 5, "Geometry", 12), W(4, "Centroid", 5, "Motion"), W(5, "Geometry", 6, "Curves", 12), W(4, "Centroid", 6, "Options") },
        Readings = new System.Collections.Generic.Dictionary<System.Guid, System.Collections.Generic.IReadOnlyList<Dwight.UI.PortReading>>
        {
          [G(1)] = new[] { new Dwight.UI.PortReading { Component = G(1), Type = "Number Slider", Nick = "U count", Port = "N", Value = "12" } },
        },
      };
      var steps = new[] { new[] { G(1) }, new[] { G(2) }, new[] { G(3), G(4) }, new[] { G(5), G(6) } }.Select(s => (System.Collections.Generic.IReadOnlyList<System.Guid>)s).ToList();
      var facts = Dwight.UI.DeckPlanner.Plan(input, steps);
      Assert.Equal(new[] { "Value", "Which" }, facts.Select(f => f.Kind.ToString()).OrderBy(k => k, StringComparer.Ordinal).ToArray());   // Brief EO: one card from the live solve, one about the wiring, so every card field
      var payload = Dwight.UI.TeachingCall.Payload(new Dwight.UI.DeckAsk
      {
        Facts = facts, Nodes = input.Nodes, Steps = steps, Sentences = new[] { "Sets the U count.", "Divides it.", "Measures.", "Lofts." }, Summary = "Lofts a divided curve.",
        Graph = "{\"nodes\":[]}",
      });
      var emitted = PayloadPaths.Collect(JsonNode.Parse(payload));
      AssertRow("explain.learn", new HashSet<string>(emitted, StringComparer.Ordinal));
    }

    /// <summary>Brief EO unit 2: an answer typed in the person's own words goes to the judge with the card's question, its right answer and why, and nothing else.</summary>
    [Fact]
    public void EXPLAIN_LEARN_JUDGE_EMITS_EXACTLY_ITS_ROW()
    {
      var card = new Dwight.UI.DeckCard
      {
        Kind = Dwight.UI.DeckKind.Why, Form = Dwight.UI.DeckForm.ChooseOne, Question = "Why is Move (15) grafted?", Explain = "So each curve is lofted alone.",
        Options = new System.Collections.Generic.List<string> { "a", "b", "c", "d" }, TrueSet = new System.Collections.Generic.List<int> { 0 },
      };
      var emitted = PayloadPaths.Collect(JsonNode.Parse(Dwight.UI.JudgeCall.Payload(card, "So every curve is its own branch")));
      AssertRow("explain.learn.judge", new HashSet<string>(emitted, StringComparer.Ordinal));
      var row = PayloadContract.For("explain.learn.judge");
      Assert.Equal(Carries.Text, row.Fields.Single(f => f.Path == "answer").Carries);   // what the person typed is their own text, and says so
    }

    [Fact]
    public void GET_COMPONENT_EMITS_EXACTLY_ITS_ROW()
    {
      var (snap, ids) = RichFixture();
      var all = snap.Nodes.Select(n => AgentToolCore.GetComponent(snap, ids, Args("{\"id\":" + n.Id + "}"))).ToArray();
      AssertRow("tool:get_component", Emitted(all));
    }

    [Fact]
    public void A_components_notes_travel_in_get_component_and_get_region_as_text_and_a_component_without_notes_has_no_field()
    {
      var (snap, ids) = RichFixture();
      var slider = snap.Nodes[0];
      var one = AgentToolCore.GetComponent(snap, ids, Args("{\"id\":" + slider.Id + "}"));
      Assert.Equal("[\"you: keep the offset at 40\",\"and 1 more\"]", one.Result["notes"].ToJsonString());

      var panel = AgentToolCore.GetComponent(snap, ids, Args("{\"id\":" + snap.Nodes[1].Id + "}"));
      Assert.False(panel.Result.AsObject().ContainsKey("notes"));

      var region = AgentToolCore.GetRegion(snap, Args("{\"id\":0}"), AgentToolCore.ShapeHash(snap));
      Assert.Contains("\"notes\":[\"you: keep the offset at 40\"", region.Result.ToJsonString());
    }

    [Fact]
    public void GET_REGION_AND_OVERVIEW_EMIT_EXACTLY_THEIR_ROWS()
    {
      var (snap, ids) = RichFixture();
      var stamp = AgentToolCore.ShapeHash(snap);
      AssertRow("tool:get_overview", Emitted(AgentToolCore.GetOverview(snap, stamp)));
      AssertRow("tool:get_region", Emitted(AgentToolCore.GetRegion(snap, Args("{\"id\":0}"), stamp), AgentToolCore.GetRegion(snap, Args("{\"id\":1}"), stamp)));
    }

    [Fact]
    public void CONNECTIONS_SIGNATURE_AND_SEARCH_EMIT_EXACTLY_THEIR_ROWS()
    {
      var (snap, ids) = RichFixture();
      var slider = snap.Nodes[0].Id; var panel = snap.Nodes[1].Id; var script = snap.Nodes[2].Id; var solver = snap.Nodes[5].Id;
      AssertRow("tool:get_connections", Emitted(AgentToolCore.GetConnections(snap, ids, Args("{\"id\":" + script + "}"))));
      var cluster = snap.Nodes.First(n => n.Nick == "Doubler").Id; var secret = snap.Nodes.First(n => n.Nick == "Secret").Id; var odd = snap.Nodes.First(n => n.Nick == "Odd").Id;
      AssertRow("tool:get_cluster", Emitted(
        AgentToolCore.GetCluster(snap, ids, Args("{\"id\":" + cluster + "}")),
        AgentToolCore.GetCluster(snap, ids, Args("{\"id\":" + secret + "}")),
        AgentToolCore.GetCluster(snap, ids, Args("{\"id\":" + odd + "}"))));
      AssertRow("tool:get_tree_signature", Emitted(
        AgentToolCore.GetTreeSignature(snap, ids, Args("{\"from\":{\"node\":" + slider + ",\"param\":\"N\"},\"to\":{\"node\":" + script + ",\"param\":\"x\"}}")),
        AgentToolCore.GetTreeSignature(snap, ids, Args("{\"from\":{\"node\":" + panel + ",\"param\":\"P\"},\"to\":{\"node\":" + solver + ",\"param\":\"Goals\"}}"))));

      // search with a truncation: more matches than the cap, so the truncated fields are exercised too.
      var big = new AgentSnapshot();
      for (var i = 0; i < AgentToolCore.SearchCap + 5; i++) big.Nodes.Add(new AgentNode { Id = i, Type = "Loft", Kind = "component" });
      AssertRow("tool:search", Emitted(AgentToolCore.SearchNodes(snap, Args("{\"query\":\"s\"}")), AgentToolCore.SearchNodes(big, Args("{\"type\":\"Loft\"}"))));
    }

    [Fact]
    public void DIAGNOSE_EMITS_EXACTLY_ITS_ROW()
    {
      var faults = Enumerable.Range(0, 3).Select(i => new DiagnoseFaultDto { Id = i, Type = "Loft", Nick = "n", Kind = "error", Root = true, Intrinsic = true, Message = "m" }).ToList();
      AssertRow("tool:diagnose", Emitted(AgentToolCore.Diagnose(faults, 1, 10, wholeCanvas: false, cap: 2)));
    }

    [Fact]
    public void KB_LOOKUP_EMITS_EXACTLY_ITS_ROW()
    {
      // U25 (#915): one match with ports, one match without, and the ambiguous answer - over the writer's real output.
      var installed = new List<InstalledType>
      {
        new InstalledType { Name = "Loft", Category = "Surface", SubCategory = "Freeform", Description = "Create a lofted surface.", Guid = G(101) },
        new InstalledType { Name = "Rectangle", Category = "Params", SubCategory = "Geometry", Description = "Contains a collection of rectangles.", Guid = G(102) },
        new InstalledType { Name = "Rectangle", Category = "Curve", SubCategory = "Primitive", Description = "Create a rectangle on a plane.", Guid = G(103) },
      };
      JsonObject Ports(InstalledType t) => t.Guid == G(101) || t.Guid == G(103)
        ? new JsonObject { ["inputs"] = new JsonArray("A"), ["outputs"] = new JsonArray("B"), ["readFrom"] = "an instance on this canvas" }
        : null;
      AssertRow("tool:kb_lookup", Emitted(
        KbLookupReport.Build("Loft", QualifiedNames.Resolve("Loft", installed), null, Ports),
        KbLookupReport.Build("Curve > Primitive > Rectangle", QualifiedNames.Resolve("Curve > Primitive > Rectangle", installed), null, _ => null),
        KbLookupReport.Build("Rectangle", QualifiedNames.Resolve("Rectangle", installed), null, Ports),
        // Brief CI: the answer's own words when an instance could not be made, on a single match and on a candidate.
        KbLookupReport.Build("Curve > Primitive > Rectangle", QualifiedNames.Resolve("Curve > Primitive > Rectangle", installed), null, _ => KbPortsRun.CouldNot()),
        KbLookupReport.Build("Rectangle", QualifiedNames.Resolve("Rectangle", installed), null, _ => KbPortsRun.CouldNot())));
    }

    [Fact]
    public void SOLVE_AND_CHECK_EMITS_EXACTLY_ITS_ROW()
    {
      // U27 (#917): the projection is GH-free now, so the row that used to be "pending, names read from
      // GhSolveCheck.cs" - and had the nested shape wrong - is held to the writer's real output.
      var rows = new[]
      {
        new SolveCheckRow { Id = 1, Type = "Number Slider", OutCounts = new[] { 1 } },
        new SolveCheckRow { Id = 2, Type = "Isotrim", OutCounts = new[] { 0 }, Msgs = { new MsgDto { Level = "warning", Text = "w" } } },
      };
      AssertRow("tool:solve_and_check", Emitted(
        ToolResult.Success(SolveCheckReport.Build(rows, solved: true)),
        ToolResult.Success(SolveCheckReport.Build(rows, solved: false))));
    }

    private static GeometrySet RichSet()
    {
      var s = new GeometrySet { NodeId = 1, Port = "M", Total = 3, NonGeometry = 1, Skip = 0, Limit = 2, Note = "budget" };
      s.Objects.Add(new GeoObject
      {
        Index = 0, Type = "Mesh", BboxMin = new[] { 0.0, 0.0, 0.0 }, BboxMax = new[] { 1.0, 1.0, 1.0 }, Valid = false,
        ValidLog = new string('x', 500), Closed = true, Solid = true, Manifold = true, Planar = true, Faces = 6, Vertices = 8, Edges = 12,
        Segments = 4, Degree = 1, Length = 1.0, Area = 6.0, Volume = 1.0, Centroid = new[] { 0.5, 0.5, 0.5 },
        Check = new MeshCheckFacts { Counts = new Dictionary<string, int> { ["NakedEdgeCount"] = 4 }, Enabled = new List<string> { "CheckForNakedEdges" } },
        NakedEdges = 4, NakedSamples = Enumerable.Range(0, 20).Select(k => new[] { (double)k, 0.0, 0.0 }).ToList(),
      });
      s.Objects.Add(new GeoObject { Index = 1, Type = "Brep", Failed = "read threw", Check = new MeshCheckFacts { Failed = "Mesh.Check threw" } });
      return s;
    }

    private static GeometrySet EmptySet() => new GeometrySet { NodeId = 1, Port = "M", Total = 0, Skip = 0, Limit = 25 };

    private static HashSet<string> Both(Func<GeometrySet, ToolResult> tool) => Emitted(tool(RichSet()), tool(EmptySet()));

    [Fact]
    public void THE_GEOMETRY_TOOLS_EMIT_EXACTLY_THEIR_ROWS()
    {
      AssertRow("tool:get_geometry_summary", Both(GeometryReport.Summary));
      AssertRow("tool:check_mesh", Both(GeometryReport.CheckMesh));
      AssertRow("tool:check_brep", Both(GeometryReport.CheckBrep));
      AssertRow("tool:get_naked_edges", Both(s => GeometryReport.NakedEdges(s, 500)));
      AssertRow("tool:get_mass_properties", Both(GeometryReport.MassProperties));
      AssertRow("tool:find_intersecting", Emitted(GeometryReport.Intersections(10, 3, new List<PairFact>
      {
        new PairFact { A = 0, B = 1, Tested = true, Intersects = true, Kind = "mesh-mesh" },
        new PairFact { A = 1, B = 2, Tested = false, Failed = "x" },
      }, capped: true)));
      AssertRow("tool:spatial_query", Emitted(GeometryReport.Spatial("near", Enumerable.Range(0, 30).Select(i => (i, (double)i)).ToList(), 5, 100)));
    }

    /// <summary>
    /// Phase-9 U23 (lane C). THE WRITER'S REAL OUTPUT against its row (#649), across the three states the read can
    /// be in - so a field that only appears when nothing has computed, or only when everything was negligible, is
    /// held to the contract too. The public privacy page is GENERATED from this row, so a field the read sends and
    /// the row does not declare would make a live public page false.
    /// </summary>
    [Fact]
    public void GET_SOLVE_TIMING_EMITS_EXACTLY_ITS_ROW()
    {
      SolveTimeFact Ran(int id, int ms, int region, string nick = null) =>
        new SolveTimeFact { Id = id, Type = "Loft", Nick = nick, Recorded = true, Ms = ms, Region = region };

      // RICH: past the component limit (omitted), past the region cap (regionsOmitted), a nickname, a label.
      var rich = Enumerable.Range(0, SolveTimingReport.RegionCap + 3)
        .Select(i => Ran(i + 1, 20 + 10 * i, region: i, nick: "n" + i)).ToList();
      var regions = Enumerable.Range(0, SolveTimingReport.RegionCap + 3)
        .Select(i => new AgentCluster { Id = i, Label = "Region " + i }).ToList();

      var nothing = new List<SolveTimeFact> { new SolveTimeFact { Id = 1, Type = "Loft", Recorded = false } };
      var negligible = new List<SolveTimeFact> { Ran(1, 0, 0) };

      AssertRow("tool:get_solve_timing", Emitted(
        SolveTimingReport.Build(rich, regions, "stamp", 5),
        SolveTimingReport.Build(nothing, regions, "stamp", 5),
        SolveTimingReport.Build(negligible, regions, "stamp", 5)));
    }

    [Fact]
    public void GET_DOCUMENT_UNITS_EMITS_EXACTLY_ITS_ROW()
    {
      AssertRow("tool:get_document_units", Emitted(
        DocumentUnitsReport.Build(new DocumentUnitsFact { Units = "Millimeters", AbsoluteTolerance = 0.001, AngleToleranceDegrees = 1 }),
        DocumentUnitsReport.Build(new DocumentUnitsFact { Units = "CustomUnits", AbsoluteTolerance = 0.01, AngleToleranceDegrees = 0.5 })));
    }

    [Fact]
    public void GET_INSTALLED_PLUGINS_EMITS_EXACTLY_ITS_ROW()
    {
      // RICH: past the cap (omitted), with and without a version, core libraries, and plugins used here.
      var libs = Enumerable.Range(0, PluginInventoryReport.PluginCap + 3)
        .Select(i => new PluginFact { Name = "Plugin " + i, Version = i % 2 == 0 ? "1." + i : null, Components = 3, UsedHere = i % 5 })
        .Concat(new[] { new PluginFact { Name = "Curve", Core = true, Components = 120, UsedHere = 4 },
                        new PluginFact { Name = "A plugin of user objects", Components = 9, UsedHere = 9, UserObjects = 9 } })
        .ToList();

      AssertRow("tool:get_installed_plugins", Emitted(
        PluginInventoryReport.Build(libs),
        PluginInventoryReport.Build(new List<PluginFact>())));
    }

    [Fact]
    public void SHOW_ON_CANVAS_EMITS_EXACTLY_ITS_ROW()
    {
      var ids = new AgentIdRegistry();
      ids.Sync(new[] { G(1), G(2) });
      var a = ids.Issue(G(1));
      var b = ids.Issue(G(2));
      AssertRow("tool:show_on_canvas", Emitted(
        ShowOnCanvasCore.Run((JsonObject)JsonNode.Parse("{\"ids\":[" + a + "," + b + "],\"dim\":true}"), ids, _ => true, null)));
    }

    [Fact]
    public void GET_FILE_STATE_EMITS_EXACTLY_ITS_ROW()
    {
      AssertRow("tool:get_file_state", Emitted(
        FileStateReport.Build(new FileStateFact { Saved = true, ModifiedSinceSave = true }),
        FileStateReport.Build(new FileStateFact { Saved = false })));
    }

    [Fact]
    public void GET_CANVAS_LAYOUT_EMITS_EXACTLY_ITS_ROW()
    {
      // RICH: past the group and member caps, a named and an addressed group, a 2x2 grid (all four neighbours),
      // crossing groups, overlapping components and a backward wire - so every optional field is emitted.
      LayoutGroup G(string key, double x, double y, string name = null, string address = null) =>
        new LayoutGroup { Key = key, Name = name, Address = address, Box = new CanvasBox(x, y, x + 100, y + 100), OtherObjects = 1 };

      var groups = new List<LayoutGroup>
      {
        G("a", 0, 0, "Input", "A1"), G("b", 200, 0, "Mid"), G("c", 0, 200), G("d", 200, 200), G("e", 250, 250),
      };
      groups[0].Members = Enumerable.Range(1, CanvasLayoutReport.MemberCap + 2).ToList();
      groups.AddRange(Enumerable.Range(0, CanvasLayoutReport.GroupCap).Select(i => G("z" + i.ToString("D2"), 5000 + i * 200, 5000)));

      var components = new List<LayoutComponent>
      {
        new LayoutComponent { Id = 1, Box = new CanvasBox(0, 0, 50, 20) },
        new LayoutComponent { Id = 2, Box = new CanvasBox(40, 10, 90, 30) },
        new LayoutComponent { Id = 3, Box = new CanvasBox(400, 0, 450, 20) },
        new LayoutComponent { Id = 99, Box = new CanvasBox(900, 900, 950, 920) },
      };

      AssertRow("tool:get_canvas_layout", Emitted(
        CanvasLayoutReport.Build(components, groups, new List<(int, int)> { (3, 1) }),
        CanvasLayoutReport.Build(new List<LayoutComponent>(), new List<LayoutGroup>(), new List<(int, int)>())));
    }

    [Fact]
    public void SET_VALUES_VERDICT_EMITS_EXACTLY_ITS_ROW()
    {
      var change = new SetValueChange { Id = 42, Type = "Number Slider", Nick = "Count", Before = "0.25 (0 to 1, float)", After = "40 (0 to 100, float)" };
      AssertRow("tool:set_value", Emitted(
        ToolResult.Success(SetValueCore.Verdict(true, change, true, true, null)),
        ToolResult.Success(SetValueCore.Verdict(false, change, false, false, "the slider holds 39, not the declared 40"))));
    }

    [Fact]
    public void THE_TOOL_ENVELOPE_EMITS_EXACTLY_ITS_ROW()
    {
      var ok = JsonNode.Parse(ToolResult.Success(new JsonObject { ["x"] = 1 }).ToJson());
      var bad = JsonNode.Parse(ToolResult.Fail("code", "msg", new JsonObject { ["y"] = 2 }).ToJson());
      var paths = new HashSet<string>(PayloadPaths.Collect(ok).Concat(PayloadPaths.Collect(bad)).Where(p => p != "result.x" && p != "error.detail.y"), StringComparer.Ordinal);
      var row = PayloadContract.For("tool:envelope");
      Assert.Equal(AssertedBy.UnitTest, row.AssertedBy);
      Assert.Equal(row.Fields.Select(f => f.Path).OrderBy(p => p, StringComparer.Ordinal), paths.OrderBy(p => p, StringComparer.Ordinal));
    }

    // ---- the renders -----------------------------------------------------------------------------------

    [Fact]
    public void THE_SETTINGS_BLOCK_RENDERS_FROM_THE_TABLE_AND_SAYS_WHICH_ROWS_ARE_PENDING()
    {
      var html = PayloadContract.SettingsHtml();
      Assert.Contains("Explain", html);
      Assert.Contains("to-script", html);
      Assert.Contains("Diagnose", html);
      Assert.Contains("local", html, StringComparison.OrdinalIgnoreCase);
      Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
      // The user's surface says WHAT IS SENT and nothing about how we test it (the architect's ruling):
      // AssertedBy answers "will we notice if this changes", which a user cannot act on and would misread.
      Assert.DoesNotContain("pending", html, StringComparison.OrdinalIgnoreCase);
      Assert.DoesNotContain("unit test", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void THE_SETTINGS_TABLE_HAS_A_ROW_FOR_EVERY_VERB_AND_TOOL_AND_EACH_CELL_COMES_FROM_THE_ROW()
    {
      var html = PayloadContract.SettingsHtml();
      Assert.Contains("<table class=\"sendstable\">", html);
      foreach (var r in PayloadContract.All())
      {
        var kinds = new[] { (Carries.Structure, "Structure"), (Carries.Fact, "Facts"), (Carries.Value, "Values"), (Carries.Text, "Your text") };
        foreach (var (c, word) in kinds)
          Assert.Equal(r.Fields.Any(f => f.Carries == c), PayloadContract.WhatGoes(r).Contains(word));
        Assert.Equal(r.Scope == Scope.Always ? "Every run" : r.Scope == Scope.WhenAttached ? "If you attach one" : "If asked", PayloadContract.WhenGoes(r));
        // the row's whole sentence stays on it, as its tooltip
        Assert.Contains("<tr title=\"" + System.Net.WebUtility.HtmlEncode(r.Sends) + "\">", html);
      }
      Assert.Equal(PayloadContract.All().Count + 1, System.Text.RegularExpressions.Regex.Matches(html, "<tr title=").Count + 1);
      Assert.Contains("Nothing is sent unless you run an action.", html);
      Assert.Contains("With a local (Ollama) model, none of this leaves your machine at all.", html);
    }

    [Fact]
    public void THE_NEVER_LINE_IS_TRUE_OF_EVERY_FIELD_OF_EVERY_ROW()
    {
      Assert.Contains(PayloadContract.NeverLine, PayloadContract.SettingsHtml());
      var paths = PayloadContract.All().SelectMany(r => r.Fields).Select(f => f.Path).ToList();
      Assert.True(paths.Count > 100, "read only " + paths.Count + " fields; the reader no longer matches the table");
      foreach (var p in paths)
        foreach (var banned in new[] { "guid", "pivot" })
          Assert.DoesNotContain(banned, p, StringComparison.OrdinalIgnoreCase);
      // A positive control on the rule, and the reason file paths are not on the line: Diagnose carries Grasshopper's own text.
      Assert.Contains(PayloadContract.For("diagnose").Fields, f => f.Carries == Carries.Text);
    }

    [Fact]
    public void THE_MARKDOWN_RENDERS_EVERY_ROW_AND_WHAT_EACH_FIELD_CARRIES_AND_IS_EMBEDDABLE()
    {
      var md = PayloadContract.Markdown();
      foreach (var r in PayloadContract.All()) Assert.Contains(r.Surface, md);
      foreach (var c in new[] { "structure", "fact", "value", "text" }) Assert.Contains(c, md);
      // Embeddable (stream D's page owns its H1): no H1, sections at the requested level, default 3.
      Assert.DoesNotContain("\n# ", "\n" + md);
      Assert.Contains("\n### ", "\n" + md);
      Assert.Contains("\n#### ", "\n" + PayloadContract.Markdown(headingLevel: 4));
      // The public render says what is sent, not how it is tested; the developer render may.
      Assert.DoesNotContain("pending", md);
      Assert.Contains("runtime suite (pending)", PayloadContract.Markdown(includeAssertion: true));
      Assert.Contains("unit test", PayloadContract.Markdown(includeAssertion: true));
    }

    [Fact]
    public void THE_MARKDOWN_IS_DETERMINISTIC()
    {
      // A drift test on stream D's page compares this byte-for-byte; a renderer that flapped would get
      // that gate disabled (#336). No timestamps, list order only, invariant formatting.
      Assert.Equal(PayloadContract.Markdown(), PayloadContract.Markdown());
      Assert.DoesNotContain(DateTime.Now.Year.ToString(), PayloadContract.Markdown());
    }

    [Fact]
    public void THE_PATH_COLLECTOR_WALKS_OBJECTS_AND_ARRAYS_AND_NAMES_ARRAY_ITEMS_WITH_BRACKETS()
    {
      var n = JsonNode.Parse("{\"a\":1,\"b\":{\"c\":[{\"d\":1},{\"e\":2}],\"f\":[1,2]},\"g\":[],\"h\":null}");
      var paths = PayloadPaths.Collect(n);
      Assert.Equal(new[] { "a", "b", "b.c", "b.c[]", "b.c[].d", "b.c[].e", "b.f", "b.f[]", "g", "h" }, paths.OrderBy(p => p, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void A_DECLARED_WILDCARD_FOLDS_A_MAP_KEYED_BY_DATA_INTO_ONE_PATH()
    {
      // A map keyed by component TYPE NAME (get_overview's per-cluster type counts) cannot enumerate its
      // keys in a contract; the table declares "types.*" and the collector folds the data keys into it.
      var folded = PayloadPaths.Fold(new[] { "clusters[].types.Number Slider", "clusters[].types.Loft", "clusters[].id" },
                                     new[] { "clusters[].types.*", "clusters[].id" });
      Assert.Equal(new[] { "clusters[].id", "clusters[].types.*" }, folded.OrderBy(p => p, StringComparer.Ordinal).ToArray());
    }
  }
}
