using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Dwight.Agent;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// THE PUBLISHED DESCRIPTOR IS THE CONTRACT, SO IT IS WHAT THE PARSER GETS TESTED AGAINST.
  ///
  /// Stage D's F1 was a descriptor and its parser disagreeing: build_region's schema advertised
  /// <c>param</c> as optional while the parser demanded it unconditionally, so a model obeying the
  /// published tool list was refused. That is the #200/#202 class - "green means LISTED, not CALLABLE" -
  /// recurring inside our own surface, where it is cheaper to close for good than to find twice.
  ///
  /// These tests close the CLASS rather than the instance. For every tool: what the descriptor calls
  /// required is enforced, what it calls optional is accepted absent, and the schema itself is coherent.
  /// A tool cannot be added to the catalogue without either satisfying this or being explicitly listed
  /// as needing a live document - and that list is asserted too, so it cannot quietly grow.
  /// </summary>
  public class DescriptorConformanceTests
  {
    private static Guid G(int n) { var b = new byte[16]; b[0] = (byte)n; b[1] = (byte)(n >> 8); return new Guid(b); }

    /// <summary>slider(1) -> loft(2), one cluster - enough for every read tool to resolve real ids.</summary>
    private static (AgentSnapshot snap, AgentIdRegistry ids, string stamp) Fixture()
    {
      var ids = new AgentIdRegistry();
      ids.Sync(new[] { G(1), G(2) });
      var a = ids.Issue(G(1));
      var b = ids.Issue(G(2));

      var snap = new AgentSnapshot
      {
        Nodes =
        {
          new AgentNode { Id = a, Type = "Number Slider", Kind = "param", Outputs = new[] { "N" }, OutCounts = new[] { 1 } },
          new AgentNode { Id = b, Type = "Loft", Kind = "component",
                          Inputs = new[] { "Curves", "Options" }, Outputs = new[] { "Loft" }, OutCounts = new[] { 1 } },
        },
        Edges =
        {
          new AgentEdgeDto { FromNode = a, FromParam = "N", ToNode = b, ToParam = "Curves",
                             Tree = new TreeSigDto { Paths = 1, Depth = 1, Items = new[] { 1 } } },
        },
        Clusters = { new AgentCluster { Id = 0, Label = "Cluster 0", Members = { a, b } } },
      };
      return (snap, ids, AgentToolCore.ShapeHash(snap));
    }

    /// <summary>A minimal VALID argument object for a tool - exactly its required fields, nothing more.
    /// This is the payload a model following the published schema to the letter would send.</summary>
    private static string MinimalArgs(string tool) =>
      tool == AgentToolCatalog.GetOverview ? "{}"
      : tool == AgentToolCatalog.GetRegion ? "{\"id\":0}"
      : tool == AgentToolCatalog.GetComponent ? "{\"id\":1}"
      : tool == AgentToolCatalog.GetConnections ? "{\"id\":1}"
      : tool == AgentToolCatalog.GetTreeSignature ? "{\"from\":{\"node\":1,\"param\":\"N\"},\"to\":{\"node\":2,\"param\":\"Curves\"}}"
      : tool == AgentToolCatalog.Search ? "{\"type\":\"Loft\"}"
      : tool == AgentToolCatalog.BuildRegion ? "{\"components\":[{\"ref\":\"a\",\"type\":\"Loft\"}]}"
      : tool == AgentToolCatalog.ProposeScaffold
        ? "{\"regions\":[{\"ref\":\"a\",\"name\":\"Prepare\",\"purpose\":\"divide the curves\"}]}"
      : tool == AgentToolCatalog.Diagnose ? "{}"
      : tool == AgentToolCatalog.GetScaffoldMap ? "{}"
      // Phase-9 U2: every geometry tool takes the one target shape; spatial_query also needs one query.
      : tool == AgentToolCatalog.GetGeometrySummary ? "{\"id\":1}"
      : tool == AgentToolCatalog.CheckMesh ? "{\"id\":1}"
      : tool == AgentToolCatalog.CheckBrep ? "{\"id\":1}"
      : tool == AgentToolCatalog.GetNakedEdges ? "{\"id\":1}"
      : tool == AgentToolCatalog.FindIntersecting ? "{\"id\":1}"
      : tool == AgentToolCatalog.GetMassProperties ? "{\"id\":1}"
      : tool == AgentToolCatalog.SpatialQuery ? "{\"id\":1,\"near\":[0,0,0]}"
      : tool == AgentToolCatalog.GetCluster ? "{\"id\":1}"
      : tool == AgentToolCatalog.SetValue ? "{\"id\":1,\"state\":{\"min\":0,\"max\":10,\"value\":5}}"
      // Phase-9 U23 (lane C): get_solve_timing takes nothing required, so a compliant model sends {}.
      : tool == AgentToolCatalog.GetSolveTiming ? "{}"
      // Phase-9 U23 (lane C) unit 2: both session reads take no arguments at all.
      : tool == AgentToolCatalog.GetDocumentUnits ? "{}"
      : tool == AgentToolCatalog.GetInstalledPlugins ? "{}"
      // Phase-9 U23 (lane C) unit 3: neither canvas read takes an argument.
      : tool == AgentToolCatalog.GetFileState ? "{}"
      : tool == AgentToolCatalog.GetCanvasLayout ? "{}"
      // Brief EE unit 4: the one required field, a list of ids.
      : tool == AgentToolCatalog.ShowOnCanvas ? "{\"ids\":[1]}"
      : null;

    private static readonly string[] GeometryTools =
    {
      AgentToolCatalog.GetGeometrySummary, AgentToolCatalog.CheckMesh, AgentToolCatalog.CheckBrep,
      AgentToolCatalog.GetNakedEdges, AgentToolCatalog.FindIntersecting, AgentToolCatalog.GetMassProperties,
    };

    /// <summary>Drive one tool GH-free. Null for the two that genuinely need a live document.</summary>
    private static ToolResult Invoke(string tool, string argsJson)
    {
      var (snap, ids, stamp) = Fixture();
      var args = AgentToolCore.ParseArgs(argsJson, out var parseError);
      if (parseError != null) return parseError;

      if (tool == AgentToolCatalog.GetOverview) return AgentToolCore.GetOverview(snap, stamp);
      if (tool == AgentToolCatalog.GetRegion) return AgentToolCore.GetRegion(snap, args, stamp);
      if (tool == AgentToolCatalog.GetComponent) return AgentToolCore.GetComponent(snap, ids, args);
      if (tool == AgentToolCatalog.GetConnections) return AgentToolCore.GetConnections(snap, ids, args);
      if (tool == AgentToolCatalog.GetTreeSignature) return AgentToolCore.GetTreeSignature(snap, ids, args);
      if (tool == AgentToolCatalog.Search) return AgentToolCore.SearchNodes(snap, args);
      if (tool == AgentToolCatalog.BuildRegion)
      {
        BuildSpecParser.TryParse(args, _ => true, _ => new string[0], _ => IdState.Alive, out _, out var e);
        return e ?? ToolResult.Success(new JsonObject());
      }
      if (tool == AgentToolCatalog.Diagnose)
      {
        // Its PROJECTION is GH-free and is what the descriptor contract is about - the tool advertises no
        // required field, so the payload a compliant model sends is {}. Only the scan itself needs a live
        // document, which is not what this file tests. Exempting it instead would have grown a list whose
        // whole purpose is not to grow.
        return AgentToolCore.Diagnose(new List<DiagnoseFaultDto>(), 0, 0, wholeCanvas: true);
      }
      if (tool == AgentToolCatalog.ProposeScaffold)
      {
        // Driveable headlessly: the validator and the addressing are GH-free, and they are the half the
        // descriptor contract is about. Only the geometry and the apply need a live document.
        ScaffoldPlanParser.TryParse(args, _ => IdState.Alive, ScaffoldPlanParser.EmptyCanvas, out _, out var e);
        return e ?? ToolResult.Success(new JsonObject());
      }
      if (tool == AgentToolCatalog.GetScaffoldMap)
      {
        // Its JOIN is GH-free and is the half the descriptor contract is about; only reading the groups
        // needs a live document. The tool advertises no field at all, so the payload a compliant model
        // sends is {} - which is exactly what is driven here.
        //
        // Driven rather than exempted, on #262's precedent in this same file: adding it to
        // NeedsLiveDocument would grow a list whose whole purpose is not to grow, and the guard refusing
        // its own author is the reason that list still means something.
        ScaffoldMapRecord.Read(null, new List<CanvasRegion>());
        return ToolResult.Success(new JsonObject());
      }
      if (GeometryTools.Contains(tool))
      {
        // Phase-9 U2: the target parse and the arity port resolution are the GH-free half the descriptor
        // contract is about; only the RhinoCommon read needs a live document. The fixture's slider (1)
        // has one output, so a compliant {"id":1} resolves without a name.
        if (!GeometryArgs.TryParseTarget(args, out var t, out var e)) return e;
        var node = snap.NodeById(t.Id);
        if (node == null) return ToolResult.Fail(ToolErrors.UnknownId, "fixture has no node " + t.Id);
        return GeometryArgs.TryResolveOutput(node, t.Param, out _, out var e2) ? ToolResult.Success(new JsonObject()) : e2;
      }
      if (tool == AgentToolCatalog.SpatialQuery)
      {
        return GeometryArgs.TryParseSpatial(args, out _, out var e) ? ToolResult.Success(new JsonObject()) : e;
      }
      // The fixture's node 1 is a slider, so a compliant {"id":1} is refused as a PRECONDITION (not a
      // cluster), never as malformed arguments - which is exactly the distinction the contract test draws.
      if (tool == AgentToolCatalog.GetCluster) return AgentToolCore.GetCluster(snap, ids, args);
      // Brief EE unit 4: the resolution is GH-free; only the glow needs a canvas, and the null pointer is the headless one.
      if (tool == AgentToolCatalog.ShowOnCanvas) return ShowOnCanvasCore.Run(args, ids, _ => true, null);
      if (tool == AgentToolCatalog.SetValue)
      {
        // The argument parse and the ask validation are GH-free and are the half the contract is about;
        // the fixture's slider (1) is the live target. Only the apply needs a document.
        if (!SetValueCore.TryParseArgs(args, out var seed, out var e)) return e;
        var ok = SetValueAsk.Validate(seed, id => ids.State(id),
          id => new LiveTarget { Type = "Number Slider", Current = new BuildState { Kind = BuildStateKind.Slider, Min = 0, Max = 1, Value = 0.25m } },
          out _, out _, out var e2);
        return ok ? ToolResult.Success(new JsonObject()) : e2;
      }
      if (tool == AgentToolCatalog.GetSolveTiming)
      {
        // Phase-9 U23 (lane C). DRIVEN, NOT EXEMPTED - and it was nearly the other way round: the parse first
        // lived in the Grasshopper read, this harness could not reach it, and the tempting fix was a third entry
        // in NeedsLiveDocument. The parse moved into the GH-free core instead, because it is exactly the half the
        // descriptor is a contract about. Only reading processor times off live objects needs a document; the
        // report is driven here over the fixture's own nodes and regions.
        if (!SolveTimingReport.TryParseArgs(args, out var limit, out var e)) return e;
        var facts = snap.Nodes.Select(n => new SolveTimeFact
        {
          Id = n.Id, Type = n.Type, Nick = n.Nick, Recorded = true, Ms = 10 * n.Id,
          Region = snap.Clusters.FirstOrDefault(c => c.Members.Contains(n.Id))?.Id,
        }).ToList();
        return SolveTimingReport.Build(facts, snap.Clusters, stamp, limit);
      }
      // Phase-9 U23 (lane C) unit 2. Driven, not exempted, for the reason above: the reports are the GH-free half the
      // descriptors are contracts about, and neither tool takes an argument to parse. Only the Rhino and
      // component-server reads need a live session.
      if (tool == AgentToolCatalog.GetDocumentUnits)
        return DocumentUnitsReport.Build(new DocumentUnitsFact { Units = "Millimeters", AbsoluteTolerance = 0.001, AngleToleranceDegrees = 1 });
      if (tool == AgentToolCatalog.GetInstalledPlugins)
        return PluginInventoryReport.Build(new List<PluginFact> { new PluginFact { Name = "Plugin", Version = "1.0", Components = 2, UsedHere = 1 } });
      // Phase-9 U23 (lane C) unit 3: the same shape - the reports are GH-free, only the reads need a document.
      if (tool == AgentToolCatalog.GetFileState)
        return FileStateReport.Build(new FileStateFact { Saved = true, ModifiedSinceSave = false });
      if (tool == AgentToolCatalog.GetCanvasLayout)
        return CanvasLayoutReport.Build(
          snap.Nodes.Select(n => new LayoutComponent { Id = n.Id, Box = new CanvasBox(n.Id * 200, 0, n.Id * 200 + 100, 50) }).ToList(),
          new List<LayoutGroup>(), snap.Edges.Select(e => (e.FromNode, e.ToNode)).ToList());
      return null;
    }

    /// <summary>The two tools that cannot be driven without a live Grasshopper document - kb_lookup reads
    /// the installed set, solve_and_check runs the solution. Named explicitly so the exemption is a
    /// stated list of two rather than a silent gap that any new tool could fall into.</summary>
    private static readonly string[] NeedsLiveDocument = { AgentToolCatalog.KbLookup, AgentToolCatalog.SolveAndCheck };

    private static (JsonObject props, HashSet<string> required) Schema(ToolDescriptor t)
    {
      var root = (JsonObject)JsonNode.Parse(t.InputSchemaJson);
      var required = new HashSet<string>(
        (root["required"] as JsonArray ?? new JsonArray()).Select(n => n.GetValue<string>()), StringComparer.Ordinal);
      return (root["properties"] as JsonObject ?? new JsonObject(), required);
    }

    /// <summary>
    /// Sufficiency rules the SCHEMA CANNOT EXPRESS, declared explicitly so they are still tested (#247 F1).
    ///
    /// <c>search</c> requires one of <c>query</c> / <c>type</c>, which JSON Schema says with a top-level
    /// <c>anyOf</c> - and the transport rejects that outright. So the rule moved to the parser, and this
    /// list is what keeps it under test: without it the old <c>anyOf</c> test would have found no
    /// alternatives to check and passed over nothing, which is the silently-vacuous-test class.
    ///
    /// A stated list of one, on the same principle as <see cref="NeedsLiveDocument"/>: an exemption that
    /// cannot quietly grow.
    /// </summary>
    private static readonly (string Tool, (string Field, string SampleJson)[] Fields)[] AtLeastOneOf =
    {
      (AgentToolCatalog.Search, new[] { ("query", "\"Loft\""), ("type", "\"Loft\"") }),
      // Phase-9 U2: spatial_query takes exactly one of three query shapes; each alone must be enough.
      (AgentToolCatalog.SpatialQuery, new[]
      {
        ("near", "[0,0,0]"),
        ("within", "{\"min\":[0,0,0],\"max\":[1,1,1]}"),
        ("overlapping", "{\"id\":1}"),
      }),
    };

    // ---- the schema is coherent on its own terms ---------------------------------------------------

    [Fact]
    public void EVERY_DESCRIPTOR_SCHEMA_PARSES_AND_DECLARES_AN_OBJECT()
    {
      foreach (var t in AgentToolCatalog.All())
      {
        var root = JsonNode.Parse(t.InputSchemaJson) as JsonObject;
        Assert.True(root != null, t.Name + ": the schema is not a JSON object");
        Assert.Equal("object", root["type"]?.GetValue<string>());
      }
    }

    [Fact]
    public void EVERY_REQUIRED_FIELD_IS_ONE_THE_SCHEMA_ACTUALLY_DECLARES()
    {
      // A required name with no matching property is a typo that would refuse every call forever.
      foreach (var t in AgentToolCatalog.All())
      {
        var (props, required) = Schema(t);
        foreach (var name in required)
          Assert.True(props.ContainsKey(name), t.Name + ": requires \"" + name + "\", which it never declares");
      }
    }

    // ---- the parser and the descriptor agree -------------------------------------------------------

    [Fact]
    public void WHAT_THE_DESCRIPTOR_CALLS_OPTIONAL_IS_ACCEPTED_ABSENT()
    {
      // F1 exactly: build_region advertised "param" optional and refused without it. A model that
      // follows the published schema to the letter must never be refused for following it.
      foreach (var t in AgentToolCatalog.All())
      {
        if (NeedsLiveDocument.Contains(t.Name)) continue;

        var minimal = MinimalArgs(t.Name);
        Assert.True(minimal != null, t.Name + ": no minimal payload - add one, or list it as needing a live document");

        var result = Invoke(t.Name, minimal);
        Assert.True(result.Ok || result.Code != ToolErrors.InvalidArgs,
          t.Name + ": refused a payload carrying every required field - " + result.Message);
      }
    }

    [Fact]
    public void WHAT_THE_DESCRIPTOR_CALLS_REQUIRED_IS_ENFORCED()
    {
      // The other direction, and the reason the first is safe: optional-means-optional must not have
      // been bought by letting everything through.
      foreach (var t in AgentToolCatalog.All())
      {
        if (NeedsLiveDocument.Contains(t.Name)) continue;
        var (_, required) = Schema(t);

        foreach (var field in required)
        {
          var args = (JsonObject)JsonNode.Parse(MinimalArgs(t.Name));
          args.Remove(field);

          var result = Invoke(t.Name, args.ToJsonString());
          Assert.False(result.Ok, t.Name + ": accepted a payload with required \"" + field + "\" missing");
          Assert.False(string.IsNullOrEmpty(result.Code), t.Name + ": refused \"" + field + "\" without a code");
        }
      }
    }

    [Fact]
    public void EVERY_DESCRIPTOR_IS_ACCEPTABLE_TO_THE_TRANSPORT_THAT_PUBLISHES_IT()
    {
      // THE CLASS FIX (#248). #208's law extends: the published descriptor is a contract with the PARSER
      // and with the TRANSPORT, so it must be valid at both. This file proved descriptor-vs-parser
      // agreement and never asked whether a provider would accept the descriptor AT ALL - which is how a
      // schema nobody could call passed a fully green suite and halted the runtime gate.
      //
      // Anthropic: "input_schema does not support oneOf, allOf, or anyOf at the top level." Rejecting one
      // tool rejects the whole request, so a single bad descriptor means NO agent turn can run.
      //
      // The reach is wider than the bug: these descriptors ARE the v1.1 MCP list_tools response, so an
      // invalid one is a defect on both faces at once - which is the leverage that made one surface worth
      // building, and exactly why validity is machine-checked rather than eyeballed.
      var unsupported = new[] { "oneOf", "allOf", "anyOf", "not", "$ref" };

      foreach (var t in AgentToolCatalog.All().Concat(AgentHostTools.All()))
      {
        var root = (JsonObject)JsonNode.Parse(t.InputSchemaJson);
        foreach (var keyword in unsupported)
          Assert.False(root.ContainsKey(keyword),
            t.Name + ": the top-level schema carries \"" + keyword + "\", which the provider rejects - " +
            "the whole tool list 400s and no turn can run. Express the rule in the parser instead.");
      }
    }

    [Fact]
    public void A_SUFFICIENCY_RULE_THE_SCHEMA_CANNOT_EXPRESS_IS_STILL_ENFORCED_BY_THE_PARSER()
    {
      // The protection the anyOf test used to give, kept after the anyOf had to go (#247 F1). "One of
      // these" is a real constraint: a payload satisfying none must be refused, and satisfying any one
      // must be enough. It simply lives in the parser now, because the transport will not carry it.
      foreach (var (tool, fields) in AtLeastOneOf)
      {
        var stripped = (JsonObject)JsonNode.Parse(MinimalArgs(tool));
        foreach (var (field, _) in fields) stripped.Remove(field);

        var none = Invoke(tool, stripped.ToJsonString());
        Assert.False(none.Ok, tool + ": accepted a payload satisfying none of {" + string.Join(",", fields.Select(f => f.Field)) + "}");
        Assert.Equal(ToolErrors.InvalidArgs, none.Code);

        foreach (var (field, sample) in fields)
        {
          var args = (JsonObject)stripped.DeepClone();
          args[field] = JsonNode.Parse(sample);   // a value of the field's OWN type, so a refusal is about sufficiency

          var one = Invoke(tool, args.ToJsonString());
          Assert.True(one.Ok || one.Code != ToolErrors.InvalidArgs,
            tool + ": refused a payload satisfying \"" + field + "\" alone - " + one.Message);
        }

        // And the rule is TOLD to the model, since the schema can no longer say it.
        var descriptor = AgentToolCatalog.All().First(d => d.Name == tool);
        Assert.All(fields, f => Assert.Contains(f.Field, descriptor.InputSchemaJson));
        Assert.Contains("at least one", descriptor.Description, StringComparison.OrdinalIgnoreCase);
      }
    }

    [Fact]
    public void A_TOOL_IS_EITHER_DRIVABLE_HEADLESS_OR_EXPLICITLY_EXEMPT()
    {
      // The exemption cannot quietly grow. A new tool joins the catalogue and is either covered by the
      // conformance run above or named here on purpose - never neither by omission.
      foreach (var t in AgentToolCatalog.All())
        Assert.True(NeedsLiveDocument.Contains(t.Name) || MinimalArgs(t.Name) != null,
          t.Name + ": neither conformance-tested nor listed as needing a live document");

      Assert.Equal(2, NeedsLiveDocument.Length);   // the list is two, and growing it is a decision
    }

    [Fact]
    public void EVERY_ADVERTISED_TOOL_IS_ONE_THE_SURFACE_ROUTES()
    {
      // The descriptor list IS the MCP list_tools response, so an entry the dispatcher does not know
      // would be an advertised tool that 404s - the #202 hole in its purest form.
      foreach (var t in AgentToolCatalog.All())
        Assert.True(AgentToolCatalog.Knows(t.Name), t.Name + ": advertised but not routed");
    }
  }
}
