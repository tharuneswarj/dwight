using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Dwight.Agent;
using Dwight.Llm;

namespace Dwight.AgentEval
{
  /// <summary>
  /// WHAT ANSWERS THE TOOLS: THE PRODUCT'S OWN PROJECTIONS OVER A STATED WORLD. A fixture states a canvas (nodes, wires, positions); it holds a real
  /// <see cref="AgentSnapshot"/> with ids from a real <see cref="AgentIdRegistry"/>, and <see cref="AgentToolCore"/> answers the reads over it, exactly as
  /// it answers them over a document. A recorded build adds its components to the snapshot and answers with the real <see cref="BuildVerdict"/>, so the
  /// model reads ids it can wire from and later reads see what it placed. An approved value, script edit or wire is written the same way, so a model that
  /// re-reads sees its own change. A verdict's outcome is still stated: nothing was solved or checked.
  /// </summary>
  internal sealed class EvalWorld
  {
    private readonly EvalFixture _fixture;
    private readonly ComponentList _components;
    private readonly AgentIdRegistry _ids = new AgentIdRegistry();
    private readonly AgentSnapshot _snap = new AgentSnapshot();
    private readonly Dictionary<int, CanvasBox> _boxes = new Dictionary<int, CanvasBox>();
    private readonly List<LayoutGroup> _groups = new List<LayoutGroup>();
    private readonly Dictionary<string, List<int>> _built = new Dictionary<string, List<int>>(StringComparer.Ordinal);
    /// <summary>The panel's mint is one per document, and a row is a document. Never the static one: rows run at once, and a number must not depend on another row.</summary>
    private readonly SpotMint _mint = new SpotMint();
    /// <summary>What an earlier card set. A stated slider has only its constant and no domain, so until a card sets it, what it holds is not known as a state.</summary>
    private readonly Dictionary<int, BuildState> _values = new Dictionary<int, BuildState>();
    /// <summary>A script's whole source. The node holds the bounded text a read shows, and a diff is taken against this.</summary>
    private readonly Dictionary<int, string> _sources = new Dictionary<int, string>();
    /// <summary>An id per named group. The stated world's groups have none, and a card's click finds its group by one, as the panel's does.</summary>
    private readonly Dictionary<string, Guid> _groupIds = new Dictionary<string, Guid>(StringComparer.Ordinal);
    private HashSet<string> _ambiguous;

    private string _storedMap;
    private string _partitionStamp;
    private readonly List<CanvasRegion> _regions = new List<CanvasRegion>();
    private int _revision;
    private int _nextCluster;

    /// <summary>What the user last wrote: the panel's <c>LastUserText</c>. Set by the runner, which holds the conversation.</summary>
    public Func<string> LastUserText { get; set; }

    /// <summary>The scripts this turn's approval granted, as the build's ledger holds them in the product.</summary>
    public IReadOnlyList<ApprovedScript> ApprovedScripts { get; set; }

    /// <summary>What a fixture miss tells the model. It must not say this is a test: the first version did, and the model stopped and told its user to
    /// open a real document, so the row measured the harness. The result counts every miss, which is where the truth about it belongs.</summary>
    public const string MissText = "could not be read just now. Try another read, or carry on from what you know.";

    public EvalWorld(EvalFixture fixture, ComponentList components)
    {
      _fixture = fixture;
      _components = components ?? new ComponentList();
      Load(fixture.Canvas);
    }

    public IReadOnlyList<string> Regions => _regions.Select(r => r.Address).ToList();

    public string Stamp => "eval-revision-" + _revision;

    public CanvasReading Reading() => new CanvasReading { Stamp = Stamp, Regions = Regions };

    public int ObjectCount => _snap.Nodes.Count + _regions.Count * 2;

    /// <summary>What the plugin tells the model the canvas holds right now: the wording of the panel's <c>Situation</c>, which is GH-bound, with nothing selected.</summary>
    public string Situation()
    {
      var objects = ObjectCount;
      if (objects == 0) return "\n\nRight now: the canvas is EMPTY. There is nothing to read and nothing to extend.";
      return "\n\nRight now: the canvas holds " + objects + " object" + (objects == 1 ? "" : "s") +
             ", and the user has NOTHING selected, so the question is about the definition as a whole. Read before you answer.";
    }

    // ---- the stated canvas ------------------------------------------------------------------------------------

    private void Load(JsonObject canvas)
    {
      if (canvas == null) return;

      var byRef = new Dictionary<string, int>(StringComparer.Ordinal);
      var clusters = new Dictionary<string, AgentCluster>(StringComparer.Ordinal);
      var groups = new Dictionary<string, LayoutGroup>(StringComparer.Ordinal);

      foreach (var c in canvas["components"]?.AsArray() ?? new JsonArray())
      {
        var id = _ids.Issue(Guid.NewGuid());
        byRef[Str(c, "ref")] = id;
        _snap.Nodes.Add(new AgentNode
        {
          Id = id,
          Type = Str(c, "type"),
          Nick = Str(c, "nick"),
          Kind = Str(c, "kind") ?? "component",
          Inputs = Strs(c, "inputs"),
          Outputs = Strs(c, "outputs"),
          OutCounts = Solved(Strs(c, "outputs")),
          Constant = Str(c, "constant"),
        });
        var x = c["x"]?.GetValue<double>() ?? 0;
        var y = c["y"]?.GetValue<double>() ?? 0;
        var box = new CanvasBox(x, y, x + (c["w"]?.GetValue<double>() ?? 120), y + (c["h"]?.GetValue<double>() ?? 40));
        _boxes[id] = box;

        var region = Str(c, "region") ?? "Region 0";
        if (!clusters.TryGetValue(region, out var cluster))
          clusters[region] = cluster = new AgentCluster { Id = _nextCluster++, Label = region };
        cluster.Members.Add(id);

        var group = Str(c, "group");
        if (group != null)
        {
          if (!groups.TryGetValue(group, out var g)) groups[group] = g = new LayoutGroup { Key = "group-" + groups.Count, Name = group, Box = box };
          g.Members.Add(id);
          g.Box = new CanvasBox(Math.Min(g.Box.X0, box.X0), Math.Min(g.Box.Y0, box.Y0), Math.Max(g.Box.X1, box.X1), Math.Max(g.Box.Y1, box.Y1));
        }
      }
      _snap.Clusters.AddRange(clusters.Values);
      _groups.AddRange(groups.Values);

      foreach (var w in canvas["wires"]?.AsArray() ?? new JsonArray())
        _snap.Edges.Add(new AgentEdgeDto { FromNode = byRef[Str(w, "from")], FromParam = Str(w, "fromParam"), ToNode = byRef[Str(w, "to")], ToParam = Str(w, "toParam") });

      Reflow();
    }

    private void Reflow()
    {
      var clusterOf = _snap.Clusters.SelectMany(c => c.Members.Select(m => (m, c.Id))).ToDictionary(t => t.m, t => t.Id);
      _snap.Flow = _snap.Edges.Select(e => (From: clusterOf[e.FromNode], To: clusterOf[e.ToNode])).Where(f => f.From != f.To).Distinct().ToList();
    }

    /// <summary>One item on every output. Stated, like the clean solve: with nothing here a read says the output is empty, and a real model then spent
    /// its turn repairing a script that was not broken.</summary>
    private static int[] Solved(string[] outputs) => outputs.Select(_ => 1).ToArray();

    private static string Str(JsonNode n, string key) => n[key]?.GetValue<string>();

    private static string[] Strs(JsonNode n, string key) =>
      (n[key]?.AsArray() ?? new JsonArray()).Select(s => s.GetValue<string>()).ToArray();

    // ---- one tool ---------------------------------------------------------------------------------------------

    /// <summary>One catalogue tool: its envelope, and which of the sources answered it.</summary>
    public string Answer(string tool, string argsJson, out string answeredBy)
    {
      var args = AgentToolCore.ParseArgs(argsJson, out var argError);
      if (argError != null) { answeredBy = "refused"; return argError.ToJson(); }

      if (tool == AgentToolCatalog.ProposeScaffold) return Scaffold(args, argsJson, out answeredBy);
      if (tool == AgentToolCatalog.BuildRegion) return Build(args, out answeredBy);

      var canned = _fixture.CannedFor(tool, argsJson);
      if (canned != null) { answeredBy = "fixture"; return canned; }

      var real = RealRead(tool, args);
      if (real != null)
      {
        answeredBy = real.Ok ? "real-core" : "refused";
        return real.ToJson();
      }

      answeredBy = "fixture-miss";
      return ToolResult.Fail(ToolErrors.PreconditionRefused, "\"" + tool + "\" " + MissText).ToJson();
    }

    private ToolResult RealRead(string tool, JsonObject args)
    {
      switch (tool)
      {
        case AgentToolCatalog.GetOverview:
          _partitionStamp = AgentToolCore.ShapeHash(_snap);
          return AgentToolCore.GetOverview(_snap, _partitionStamp);
        case AgentToolCatalog.GetRegion: return AgentToolCore.GetRegion(_snap, args, _partitionStamp, _ids);
        case AgentToolCatalog.GetComponent: return AgentToolCore.GetComponent(_snap, _ids, args);
        case AgentToolCatalog.GetConnections: return AgentToolCore.GetConnections(_snap, _ids, args);
        case AgentToolCatalog.GetTreeSignature: return AgentToolCore.GetTreeSignature(_snap, _ids, args);
        case AgentToolCatalog.Search: return AgentToolCore.SearchNodes(_snap, args);
        case AgentToolCatalog.Diagnose: return AgentToolCore.Diagnose(new List<DiagnoseFaultDto>(), 0, _snap.Nodes.Count, true);
        case AgentToolCatalog.GetDocumentUnits:
          return DocumentUnitsReport.Build(new DocumentUnitsFact { Units = "Millimeters", AbsoluteTolerance = 0.001, AngleToleranceDegrees = 1.0 });
        case AgentToolCatalog.GetFileState:
          return FileStateReport.Build(new FileStateFact { Saved = _snap.Nodes.Count > 0, ModifiedSinceSave = _revision > 0 });
        case AgentToolCatalog.GetCanvasLayout:
          return CanvasLayoutReport.Build(_boxes.Select(b => new LayoutComponent { Id = b.Key, Box = b.Value }).ToList(), _groups,
                                          _snap.Edges.Select(e => (e.FromNode, e.ToNode)).ToList());
        case AgentToolCatalog.GetScaffoldMap:
          return ToolResult.Success(ScaffoldMapPayload.Build(ScaffoldMapRecord.Read(_storedMap, _regions), 0, 200));
        case AgentToolCatalog.KbLookup:
          if (!_components.Loaded) return null;
          return KbLookupReport.Run(args, _components.Types, _components.Nearest, PortsFromCanvas);
        case AgentToolCatalog.GetInstalledPlugins:
          return _components.Loaded ? PluginInventoryReport.Build(_components.Inventory()) : null;
        default:
          return null;
      }
    }

    /// <summary>The canvas half of the product's rule, through its own <see cref="LivePorts.Pick"/>. The temporary instance needs Grasshopper, so it is not here, and the result says so.</summary>
    private JsonObject PortsFromCanvas(InstalledType info)
    {
      var live = _snap.Nodes.Select(n => new LiveObject { Guid = Guid.Empty, Name = n.Type, IsParam = n.Kind == "param", Inputs = n.Inputs, Outputs = n.Outputs });
      var shared = _components.Types.Any(t => t.Guid != info.Guid && string.Equals((t.Name ?? "").Trim(), (info.Name ?? "").Trim(), StringComparison.OrdinalIgnoreCase));
      return LivePorts.Pick(live, info, shared);
    }

    // ---- the panel's ask, over the stated world --------------------------------------------------------------------

    /// <summary>
    /// <c>GhBuildRegion.LiveResolvers</c>, from what this world knows. The rules are the product's (<see cref="SpotAsk"/>, <see cref="BuildSpecParser"/>); only the reads differ. A port is
    /// known only for a component on the stated canvas or one of its type, because the saved component list holds no ports. Elsewhere it is not known, which the parser treats as no rule,
    /// so a wrong port on such a type passes here where the panel would refuse it.
    /// </summary>
    public SpecResolvers Resolvers() => new SpecResolvers
    {
      TypeExists = TypeExists,
      Nearest = name => _components.Nearest(name),
      IdState = _ids.State,
      PortsOfType = type =>
      {
        var bare = Dwight.Components.ComponentMatchCore.BareName(type);
        return ShapeOf(_snap.Nodes.FirstOrDefault(n => string.Equals(Dwight.Components.ComponentMatchCore.BareName(n.Type), bare, StringComparison.OrdinalIgnoreCase)));
      },
      PortsOfId = id => ShapeOf(_snap.Nodes.FirstOrDefault(n => n.Id == id)),
      AmbiguousForms = type =>
      {
        if (!_components.Loaded) return null;
        var r = QualifiedNames.Resolve(type, _components.Types);
        return r.Outcome == QualifiedNames.Outcome.Ambiguous ? r.Matches.Select(QualifiedNames.Format).ToList() : null;
      },
      CanonicalType = type =>
      {
        if (!_components.Loaded) return type;
        var r = QualifiedNames.Resolve(type, _components.Types);
        if (r.Outcome != QualifiedNames.Outcome.One) return type;
        _ambiguous = _ambiguous ?? QualifiedNames.AmbiguousNames(_components.Types);
        return QualifiedNames.QualifiedIfAmbiguous(r.Matches[0], _ambiguous) ?? r.Matches[0].Name;
      },
      Groups = NamedGroups,
      LastUserText = () => LastUserText?.Invoke(),
    };

    /// <summary>The panel's list of groups a request can name: the stated ones, and the regions a map or a placement made.</summary>
    private IReadOnlyList<NamedGroup> NamedGroups() =>
      _groups.Select(g => g.Name).Concat(_regions.Select(RegionNick))
        .Select(name => NamedGroup.Of(GroupId(name), name)).Where(g => g != null).ToList();

    private static string RegionNick(CanvasRegion r) => r.Address + ScaffoldAddress.Separator + r.Name;

    private Guid GroupId(string name)
    {
      if (!_groupIds.TryGetValue(name, out var id)) _groupIds[name] = id = Guid.NewGuid();
      return id;
    }

    private bool TypeExists(string name) =>
      !_components.Loaded || QualifiedNames.Resolve(name, _components.Types).Outcome != QualifiedNames.Outcome.None;

    /// <summary>What <c>GhBuildRegion.LabelOfId</c> says: the nickname, else the type. Null for an id that is not there.</summary>
    private string LabelOf(int id)
    {
      var node = _snap.Nodes.FirstOrDefault(n => n.Id == id);
      return node == null ? null : string.IsNullOrWhiteSpace(node.Nick) ? node.Type : node.Nick;
    }

    /// <summary>The panel's <c>ShapeOf</c>. A param has EMPTY port lists, one implicit port and no name to give it. A recorded component with no same-type instance has stated no ports at
    /// all, and that is null, not known, so the parser makes no rule of it.</summary>
    private static PortShape ShapeOf(AgentNode node)
    {
      if (node == null) return null;
      if (node.Kind == "param") return new PortShape { Inputs = new string[0], Outputs = new string[0] };
      var inputs = node.Inputs ?? new string[0];
      var outputs = node.Outputs ?? new string[0];
      if (inputs.Length == 0 && outputs.Length == 0) return null;
      return new PortShape
      {
        Inputs = inputs.Select(p => SplitPort(p).Label).ToList(),
        Outputs = outputs.Select(p => SplitPort(p).Label).ToList(),
        InputNames = inputs.Select(p => SplitPort(p).Name).ToList(),
        OutputNames = outputs.Select(p => SplitPort(p).Name).ToList(),
      };
    }

    /// <summary>A stated port reads as kb_lookup shows one, "Radius (R)": the full name, then the label the canvas draws. One with no parentheses is both.</summary>
    private static (string Name, string Label) SplitPort(string port)
    {
      var text = port ?? "";
      var open = text.LastIndexOf(" (", StringComparison.Ordinal);
      return open > 0 && text.EndsWith(")", StringComparison.Ordinal)
        ? (text.Substring(0, open), text.Substring(open + 2, text.Length - open - 3))
        : (text, text);
    }

    /// <summary>The panel's <c>PrepareSpot</c>: validate, mint and write the record onto the seed, through <see cref="SpotAsk.Prepare"/> itself. <paramref name="savedTexts"/> is what the row's
    /// conversation holds, because the panel reserves every spot its saved conversation mentions.</summary>
    public bool PrepareSpot(HandoffSeed seed, IEnumerable<string> savedTexts, out SpotAsk spot, out ToolResult error)
    {
      try
      {
        if (!SpotAsk.Prepare(seed, Resolvers(), LabelOf, _mint, SpotMint.InUse(Regions, savedTexts), out spot, out error)) return false;
      }
      catch (Exception ex)
      {
        spot = null;
        error = ToolResult.Fail(ToolErrors.PreconditionRefused, "Dwight could not check that placement (" + ex.Message + "), so it will not offer it.");
        return false;
      }
      seed.SpotDone = ConsentCard.SpotDone(spot.Placement);
      return true;
    }

    /// <summary>What the panel's <c>GhEditOps.Ask</c> does, through <see cref="EditOpsAsk.Validate(JsonArray, EditCanvas, out EditPlan, out ToolResult)"/> itself: every row against the stated canvas, before a card exists.</summary>
    public bool PrepareEdit(HandoffSeed seed, out EditPlan plan, out ToolResult error) =>
      EditOpsAsk.Validate(seed?.Ops, EditView(seed?.Ops), out plan, out error);

    /// <summary>The panel's ask for a value, through <see cref="SetValueAsk.Validate"/> itself: refused by name before a card exists, and the card records the type the click checks.</summary>
    public bool PrepareValue(HandoffSeed seed, out ToolResult error)
    {
      if (!SetValueAsk.Validate(seed, _ids.State, LiveValue, out var change, out _, out error)) return false;
      seed.TargetType = change.Type;
      return true;
    }

    /// <summary>The panel's ask for a script edit, through <see cref="EditScriptAsk.Validate"/> itself. The trial cannot run here, so its facts are stated and clean, as a build's verdict is, and the
    /// outcome and report are the product's words for a trial that passed. A script that would not compile or run therefore gets a card here. Named in every result.</summary>
    public bool PrepareScript(HandoffSeed seed, out ToolResult error)
    {
      if (!EditScriptAsk.Validate(seed, _ids.State, LiveScriptOf, out var change, out error)) return false;
      var compared = _snap.Nodes.First(n => n.Id == change.TargetId).Outputs?.Length ?? 0;
      var facts = new ScriptTrialFacts { Ran = true, OutputsCompared = compared, OutputsWithData = compared, AppendedInputs = change.AddInputs.Count };
      var outcome = ScriptEditVerdict.Outcome(change.Claim, facts);
      seed.BaseSource = change.BaseSource;
      seed.Outcome = outcome.ToString();
      seed.Report = ScriptEditVerdict.WithAccess(ScriptEditVerdict.ReportLine(outcome, facts, ScriptEditVerdict.Subject(change)), change);
      return true;
    }

    /// <summary><c>GhSetValue.ReadLive</c>, from what this world states.</summary>
    private LiveTarget LiveValue(int id)
    {
      var node = _snap.Nodes.FirstOrDefault(n => n.Id == id);
      return node == null ? null : new LiveTarget { Type = node.Type, Nick = node.Nick, Current = _values.TryGetValue(id, out var state) ? state : null };
    }

    /// <summary><c>GhEditScript.ReadLive</c>, from what this world states. A stated script with no stated source reads as empty, not locked, because here not knowing is not a refusal (#208's "fail open on knowledge").</summary>
    private LiveScript LiveScriptOf(int id)
    {
      var node = _snap.Nodes.FirstOrDefault(n => n.Id == id);
      if (node == null) return null;
      return new LiveScript
      {
        Type = RuntimeTypeName(node.Type),
        Nick = node.Nick,
        Source = _sources.TryGetValue(id, out var source) ? source : "",
        SourceReadable = true,
        Inputs = (node.Inputs ?? new string[0]).Select(p => SplitPort(p).Label).ToList(),
        Outputs = (node.Outputs ?? new string[0]).Select(p => SplitPort(p).Label).ToList(),
        InputAccess = (node.ScriptInputAccess ?? new List<KeyValuePair<string, string>>()).Where(kv => kv.Value != PortAccess.Item)
          .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase),
      };
    }

    /// <summary>The product's script rules read the runtime's type name, and this world states the one a person reads.</summary>
    private static string RuntimeTypeName(string stated)
    {
      switch (BuildScriptTypes.LanguageOf(stated))
      {
        case BuildScriptTypes.Python: return "RhinoCodePluginGH.Components.Python3Component";
        case BuildScriptTypes.CSharp: return "RhinoCodePluginGH.Components.CSharpComponent";
        default: return stated;
      }
    }

    /// <summary>Params a wire cannot land on. The panel reads each one's input grip, and the stated world has only its type.</summary>
    private static readonly HashSet<string> NoInputGrip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
      "Number Slider", "Boolean Toggle", "Button", "Value List", "Colour Swatch", "Digit Scroller",
    };

    /// <summary><c>GhEditOps.ReadCanvas</c>, over the stated world: the same shape, and the stated wires as each input's sources.</summary>
    private EditCanvas EditView(JsonArray ops)
    {
      var canvas = new EditCanvas { State = _ids.State };
      var unstated = new HashSet<int>();
      foreach (var n in _snap.Nodes)
      {
        if (!_ids.TryGetGuid(n.Id, out var guid)) continue;
        var node = new EditNode
        {
          Id = n.Id, Guid = guid, Label = LabelOf(n.Id) ?? "component", IsParam = n.Kind == "param",
          Region = _groups.FirstOrDefault(g => g.Members.Contains(n.Id))?.Name,
        };
        if (node.IsParam)
        {
          var port = new EditPort { Name = n.Type, Nick = n.Nick, Guid = Guid.NewGuid() };
          node.AcceptsInput = !NoInputGrip.Contains(n.Type ?? "");
          if (node.AcceptsInput) node.Inputs.Add(port);
          node.Outputs.Add(port);
        }
        else
        {
          node.Inputs = (n.Inputs ?? new string[0]).Select(StatedPort).ToList();
          node.Outputs = (n.Outputs ?? new string[0]).Select(StatedPort).ToList();
          if (node.Inputs.Count == 0 && node.Outputs.Count == 0) unstated.Add(n.Id);
        }
        canvas.Nodes[n.Id] = node;
      }

      // A recorded component with no ports stated takes the ones the rows name, because here not knowing is not a refusal (#208's "fail open on knowledge").
      foreach (var op in ops ?? new JsonArray())
      {
        NameUnstated(canvas, unstated, op?["from"], output: true);
        NameUnstated(canvas, unstated, op?["to"], output: false);
      }

      foreach (var e in _snap.Edges)
      {
        var from = PortOn(canvas, e.FromNode, e.FromParam, output: true);
        var to = PortOn(canvas, e.ToNode, e.ToParam, output: false);
        if (from != null && to != null) to.Sources.Add(from.Guid);
      }
      return canvas;
    }

    private static EditPort StatedPort(string port)
    {
      var (name, label) = SplitPort(port);
      return new EditPort { Name = name, Nick = label, Guid = Guid.NewGuid() };
    }

    private static void NameUnstated(EditCanvas canvas, HashSet<int> unstated, JsonNode end, bool output)
    {
      int id;
      try { if (end?["id"] == null) return; id = end["id"].GetValue<int>(); } catch { return; }
      if (!unstated.Contains(id)) return;
      string param = null;
      try { param = end["param"]?.GetValue<string>(); } catch { }
      PortOn(canvas, id, string.IsNullOrWhiteSpace(param) ? "port" : param, output);
    }

    /// <summary>The port a stated wire names on one end. A wire may name it in any of a port's spellings, and a wire the canvas states says the port exists, so one not listed is added.</summary>
    private static EditPort PortOn(EditCanvas canvas, int id, string param, bool output)
    {
      if (!canvas.Nodes.TryGetValue(id, out var node)) return null;
      var ports = output ? node.Outputs : node.Inputs;
      if (node.IsParam) return ports.FirstOrDefault();
      var found = ports.FirstOrDefault(p => string.Equals(p.Label, param, StringComparison.OrdinalIgnoreCase) || string.Equals(p.Name, param, StringComparison.OrdinalIgnoreCase) ||
                                            string.Equals(p.Name + " (" + p.Nick + ")", param, StringComparison.OrdinalIgnoreCase));
      if (found != null) return found;
      var added = StatedPort(param);
      ports.Add(added);
      return added;
    }

    // ---- the mutations, recorded --------------------------------------------------------------------------------

    /// <summary>The panel's <c>ApplySpot</c>, recorded: the card's own record is what is placed, at the address the card named, with no spec from the model. A group makes it a region the
    /// canvas carries. The option a row takes is the card's primary one, in a group, so nothing here measures the loose one.</summary>
    public ToolResult PlaceSpot(JsonObject record, string spot, bool grouped, Guid? into = null)
    {
      var args = (JsonObject)record.DeepClone();
      args["region"] = spot;
      var result = BuildResult(args, out _, lands: true, into);
      if (result.Ok && into == null && grouped && !_regions.Any(r => string.Equals(r.Address, spot, StringComparison.Ordinal)))
      {
        var placed = record["components"]?.AsArray() ?? new JsonArray();
        var first = placed.Count > 0 ? (placed[0]?["nick"] ?? placed[0]?["type"])?.GetValue<string>() : null;
        _regions.Add(new CanvasRegion { Address = spot, Name = (first ?? "component") + (placed.Count > 1 ? " and " + (placed.Count - 1) + " more" : ""), ComponentCount = placed.Count });
      }
      return result;
    }

    /// <summary>The panel's <c>ConsentRoute.SetValue</c>, recorded: the card's record is checked against the world as it is now, then written, so later reads see it. The panel's report comes from what this returns.</summary>
    public ToolResult ApplyValue(SeedPlan plan)
    {
      var idState = plan.Target.HasValue ? _ids.State(plan.Target.Value) : IdState.Unknown;
      var liveType = plan.Target.HasValue ? LiveValue(plan.Target.Value)?.Type : null;
      if (!SetValueCore.MayProceed(idState, liveType, plan.TargetType, out var scopeRefusal))
        return ToolResult.Fail(ToolErrors.PreconditionRefused, scopeRefusal);

      var again = new HandoffSeed { Target = plan.Target, State = plan.State };
      if (!SetValueAsk.Validate(again, _ids.State, LiveValue, out var change, out var declared, out var error)) return error;

      var node = _snap.Nodes.First(n => n.Id == change.Id);
      if (!_values.ContainsKey(node.Id) && node.Constant != null) change.Before = node.Constant;   // all a stated slider states of what it holds
      Hold(node, declared);
      _revision++;
      return SetValueCore.Applied(change);
    }

    /// <summary>What <c>GhAgent</c> reads off a slider or a panel, in its shapes. A constant in a param has no such shape, only the constant.</summary>
    private void Hold(AgentNode node, BuildState state)
    {
      _values[node.Id] = state;
      node.Constant = BuildStateText.Value(state);
      if (state.Kind == BuildStateKind.Slider)
        node.Value = new ValueDto
        {
          Kind = "slider", Value = state.Value, Min = state.Min, Max = state.Max,
          Rounding = state.Rounding.ToString().ToLowerInvariant(), Decimals = state.DecimalPlaces,
        };
      else if (state.Kind == BuildStateKind.PanelText)
      {
        var text = state.Text ?? "";
        var cut = text.Length > DwightConfig.AgentPanelTextMaxChars;
        node.Value = new ValueDto { Kind = "panel", Text = cut ? text.Substring(0, DwightConfig.AgentPanelTextMaxChars) : text, TextTruncated = cut, TextTotalChars = text.Length };
      }
    }

    /// <summary>The panel's <c>ConsentRoute.EditScript</c>, recorded: re-validated, the compare-and-set against the base the card diffed from, then written. <paramref name="text"/> is the report the card was drawn from, or why nothing changed.</summary>
    public bool ApplyScript(SeedPlan plan, out string text)
    {
      var again = new HandoffSeed
      {
        Target = plan.Target,
        Claim = plan.Claim == ScriptEditClaim.Preserves ? "preserves" : "changes",
        Source = plan.Source,
        AddInputs = plan.AddInputs.ToList(),
        AddOutputs = plan.AddOutputs.ToList(),
        InputAccess = new Dictionary<string, string>(plan.InputAccess, StringComparer.OrdinalIgnoreCase),
      };
      if (!EditScriptAsk.Validate(again, _ids.State, LiveScriptOf, out var change, out var error))
      {
        text = error.Message;
        return false;
      }
      if (!string.Equals(Dwight.Data.SourceToken.Normalise(plan.BaseSource), change.BaseSource, StringComparison.Ordinal))
      {
        text = "That component's source has changed since Dwight showed you the diff, so the change you " +
               "approved is no longer what it would do. Nothing was changed. Ask again and Dwight will diff " +
               "against what is there now.";
        return false;
      }

      var node = _snap.Nodes.First(n => n.Id == change.TargetId);
      HoldSource(node, change.NewSource);
      node.Inputs = (node.Inputs ?? new string[0]).Concat(change.AddInputs).ToArray();
      node.Outputs = (node.Outputs ?? new string[0]).Concat(change.AddOutputs).ToArray();
      node.OutCounts = Solved(node.Outputs);
      var fed = change.AccessToSet;
      node.ScriptInputAccess = node.Inputs.Select(n => new KeyValuePair<string, string>(n,
        fed.TryGetValue(n, out var word) ? word : (node.ScriptInputAccess?.FirstOrDefault(kv => string.Equals(kv.Key, n, StringComparison.OrdinalIgnoreCase)).Value ?? PortAccess.Item))).ToList();
      _revision++;
      text = plan.Report;
      return true;
    }

    /// <summary>What <c>GhAgent</c> reads off a script: its language, and its source within a read's bound.</summary>
    private void HoldSource(AgentNode node, string source)
    {
      var whole = Dwight.Data.SourceToken.Normalise(source);
      _sources[node.Id] = whole;
      node.ScriptLanguage = SourceBound.LanguageOf(RuntimeTypeName(node.Type));
      node.Source = SourceBound.Trim(whole, node.ScriptLanguage, DwightConfig.AgentSourceMaxLines, DwightConfig.AgentSourceMaxChars);
    }

    /// <summary>The panel's <c>ConsentRoute.Edit</c>, recorded: every row re-validated against the world as it is now, then written. A row takes the whole card, so every row is ticked. <paramref name="text"/> is the product's report, or why nothing changed.</summary>
    public bool ApplyEdit(SeedPlan plan, out string text)
    {
      var canvas = EditView(plan.Ops);
      if (!EditOpsAsk.Validate(plan.Ops, canvas, out var edit, out var error))
      {
        text = error.Message;
        return false;
      }

      var objectsBefore = CountedObjects();
      var groupsBefore = GroupFacts();

      foreach (var step in edit.Steps.Where(s => s.Kind == EditOpKind.Unwire))
        _snap.Edges.RemoveAll(e => PortOn(canvas, e.FromNode, e.FromParam, output: true)?.Guid == step.FromPort.Guid &&
                                   PortOn(canvas, e.ToNode, e.ToParam, output: false)?.Guid == step.ToPort.Guid);
      foreach (var step in edit.Steps.Where(s => s.Kind == EditOpKind.Wire))
        _snap.Edges.Add(new AgentEdgeDto { FromNode = step.From.Id, FromParam = step.FromParam ?? step.FromPort.Label, ToNode = step.To.Id, ToParam = step.ToParam ?? step.ToPort.Label });
      foreach (var step in edit.Steps.Where(s => s.Kind == EditOpKind.Delete))
        Remove(step.Target.Id);

      // Brief CL: the product's own decision, over the stated world.
      var deleted = edit.Steps.Where(s => s.Kind == EditOpKind.Delete).Select(s => IdGuid(s.Target.Id)).ToList();
      var groups = EditEmptyGroups.Decide(groupsBefore, deleted, g => !deleted.Contains(g));
      foreach (var name in groups.Removed)
      {
        _groups.RemoveAll(g => g.Name == name);
        foreach (var region in _regions.Where(r => RegionGroupName(r) == name).ToList())
        {
          _regions.Remove(region);
          _built.Remove(region.Address);
        }
      }

      Reflow();
      _revision++;
      text = EditOpsAsk.AppliedReport(edit, edit.Steps.Count, objectsBefore, CountedObjects(), groups);
      return true;
    }

    private static Guid IdGuid(int id) => new Guid(id, 0, 0, new byte[8]);

    private static string RegionGroupName(CanvasRegion r) => r.Address + ScaffoldAddress.Separator + r.Name;

    /// <summary>What this world counts: its components, its stated groups, and the regions it has built. It lists no scribbles, so the number is its own, not a real canvas's.</summary>
    private int CountedObjects() => _snap.Nodes.Count + _groups.Count + _regions.Count;

    /// <summary>Who holds whom, in the product's shape. A map's region holds a header scribble this world does not list, and no edit can delete one, so it is given one here.</summary>
    private List<EditGroupFacts> GroupFacts()
    {
      var header = Guid.NewGuid();
      var facts = _groups.Select(g => new EditGroupFacts { Guid = Guid.NewGuid(), Name = g.Name, Members = g.Members.Select(IdGuid).ToList() }).ToList();
      foreach (var r in _regions)
      {
        var members = (_built.TryGetValue(r.Address, out var placed) ? placed : new List<int>()).Select(IdGuid).ToList();
        if (ScaffoldAddress.IsAddress(r.Address)) members.Add(header);
        facts.Add(new EditGroupFacts { Guid = Guid.NewGuid(), Name = RegionGroupName(r), Members = members });
      }
      return facts;
    }

    /// <summary>A deleted component leaves every read, its id says retired, and a region it was built into counts one fewer. Whether its group goes with it is <see cref="ApplyEdit"/>'s, as it is the product's.</summary>
    private void Remove(int id)
    {
      _snap.Nodes.RemoveAll(n => n.Id == id);
      _snap.Edges.RemoveAll(e => e.FromNode == id || e.ToNode == id);
      foreach (var cluster in _snap.Clusters) cluster.Members.Remove(id);
      _snap.Clusters.RemoveAll(c => c.Members.Count == 0);
      foreach (var group in _groups) group.Members.Remove(id);
      foreach (var built in _built)
      {
        if (!built.Value.Remove(id)) continue;
        var region = _regions.FirstOrDefault(r => string.Equals(r.Address, built.Key, StringComparison.Ordinal));
        if (region != null) region.ComponentCount = built.Value.Count;
      }
      _values.Remove(id);
      _sources.Remove(id);
      _boxes.Remove(id);
      _ids.Retire(id);
    }

    private string Scaffold(JsonObject args, string argsJson, out string answeredBy)
    {
      if (!ScaffoldPlanParser.TryParse(args, _ids.State, Regions, out var plan, out var error))
      {
        answeredBy = "refused";
        return error.ToJson();
      }

      answeredBy = "recorded";
      var verdict = new JsonObject
      {
        ["applied"] = true,
        ["preserved"] = true,
        ["regions"] = ScaffoldMapRecord.AssignedRegions(plan.Regions, plan.Regions),
        ["undo"] = "one Ctrl-Z removes the whole map",
        ["proves"] = "The map was laid down as specified, and the definition computes exactly what it computed before - a scaffold adds only groups and scribbles, which are not part of the computation.",
        ["doesNotProve"] = "That this is a GOOD decomposition, or that the regions are the right ones. Nothing has been built yet; the map is a plan for the user to read and work from.",
      };
      var envelope = ToolResult.Success(verdict).ToJson();

      _storedMap = ScaffoldMapRecord.Build(argsJson, envelope);
      foreach (var region in plan.Regions)
        _regions.Add(new CanvasRegion { Address = region.Key, Name = region.GroupName, ComponentCount = 0 });
      _revision++;
      return envelope;
    }

    private string Build(JsonObject args, out string answeredBy) => BuildResult(args, out answeredBy).ToJson();

    /// <summary>The panel's <c>Land</c>, over the stated boxes, through <see cref="Landing.Find"/> itself. The sizes are this world's stated ones, and it has no view, so a placement with no wires starts past the drawing's edge.</summary>
    private string LandOf(BuildSpec spec, SpaceBox into, out TidyPoint at)
    {
      var n = spec.Components.Count;
      var w = 140 * (Math.Min(n, 6) - 1) + 120;
      var h = 90 * ((n - 1) / 6) + 40;
      var canvas = _boxes.Values.Select(Space).Concat(_groups.Where(g => into == null || !SameBox(g.Box, into)).Select(g => Space(g.Box))).ToList();

      SpaceBox BoxOfId(int? id) => id.HasValue && _boxes.TryGetValue(id.Value, out var b) ? Space(b) : null;
      var fedBy = spec.Wires.Select(x => BoxOfId(x.FromId)).Where(b => b != null).ToList();
      var feeding = spec.Wires.Select(x => BoxOfId(x.ToId)).Where(b => b != null).ToList();

      at = Landing.Find(new LandingAsk { W = w, H = h, Canvas = canvas, Into = into, FedBy = fedBy, Feeding = feeding });
      var landed = new SpaceBox("", at.X, at.Y, w, h, SpaceRole.Fixed);
      if (into != null) return Landing.InGroup(landed, into);

      var firstWired = spec.Wires.Select(x => x.FromId).Concat(spec.Wires.Select(x => x.ToId)).FirstOrDefault(id => BoxOfId(id) != null);
      return Landing.Where(landed, canvas, null, firstWired.HasValue ? LabelOf(firstWired.Value) : null, Landing.Extent(fedBy.Count > 0 ? fedBy : feeding));
    }

    private static SpaceBox Space(CanvasBox b) => new SpaceBox("", b.X0, b.Y0, b.X1 - b.X0, b.Y1 - b.Y0, SpaceRole.Fixed);

    private static bool SameBox(CanvasBox b, SpaceBox s) => b.X0 == s.X && b.Y0 == s.Y && b.X1 == s.Right && b.Y1 == s.Bottom;

    /// <summary>A named group's box, or null for a region this world states no box for. It lands by its other anchors then, and the region still grows.</summary>
    private SpaceBox IntoBox(Guid? into)
    {
      if (!into.HasValue) return null;
      var group = _groups.FirstOrDefault(g => _groupIds.TryGetValue(g.Name, out var id) && id == into.Value);
      return group == null ? null : Space(group.Box);
    }

    /// <summary>What the panel's <c>target.AddObject</c> does to a group: the new components are its members, and its box or its count grows.</summary>
    private void Grow(Guid into, IReadOnlyList<int> placed)
    {
      var group = _groups.FirstOrDefault(g => _groupIds.TryGetValue(g.Name, out var id) && id == into);
      if (group != null)
      {
        foreach (var id in placed)
        {
          group.Members.Add(id);
          var b = _boxes[id];
          group.Box = new CanvasBox(Math.Min(group.Box.X0, b.X0), Math.Min(group.Box.Y0, b.Y0), Math.Max(group.Box.X1, b.X1), Math.Max(group.Box.Y1, b.Y1));
        }
        return;
      }

      var region = _regions.FirstOrDefault(r => _groupIds.TryGetValue(RegionNick(r), out var id) && id == into);
      if (region == null) return;
      region.ComponentCount += placed.Count;
      if (_built.TryGetValue(region.Address, out var held)) held.AddRange(placed);
      else _built[region.Address] = placed.ToList();
    }

    private bool GroupStands(Guid into) =>
      _groups.Any(g => _groupIds.TryGetValue(g.Name, out var id) && id == into) ||
      _regions.Any(r => _groupIds.TryGetValue(RegionNick(r), out var id) && id == into);

    private ToolResult BuildResult(JsonObject args, out string answeredBy, bool lands = false, Guid? into = null)
    {
      BuildScriptGate.FillFromCard(args, ApprovedScripts);   // the product's step before its parse
      if (!BuildSpecParser.TryParse(args, TypeExists, name => _components.Nearest(name), _ids.State, out var spec, out var error))
      {
        answeredBy = "refused";
        return error;
      }

      if (into.HasValue && !GroupStands(into.Value))
      {
        answeredBy = "refused";
        return ToolResult.Fail(ToolErrors.UnknownRegion,
          "The group this card named is no longer on the canvas, so nothing was built. Ask again and Dwight will look at what is there now.");
      }

      var address = args?["region"]?.GetValue<string>();
      var replaced = RemoveBuilt(address);

      // A placement lands where the product's own search says, over the stated boxes, with its wires and nothing else as its anchors.
      TidyPoint landedAt = default;
      var landedWhere = lands ? LandOf(spec, IntoBox(into), out landedAt) : null;

      // Each component gets a real id, so the verdict names what the next call can wire from.
      var placed = new List<int>();
      var byRef = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
      foreach (var c in spec.Components)
      {
        var id = _ids.Issue(Guid.NewGuid());
        placed.Add(id);
        if (!string.IsNullOrEmpty(c.Ref)) byRef[c.Ref] = id;
        var like = _snap.Nodes.FirstOrDefault(n => string.Equals(n.Type, c.Type, StringComparison.OrdinalIgnoreCase));
        var cell = placed.Count - 1;
        var node = new AgentNode
        {
          Id = id,
          Type = c.Type,
          Nick = c.Nick,
          Kind = like?.Kind ?? "component",
          Inputs = c.CarriesSource ? c.Inputs.ToArray() : like?.Inputs ?? Array.Empty<string>(),
          Outputs = c.CarriesSource ? c.Outputs.ToArray() : like?.Outputs ?? Array.Empty<string>(),
          OutCounts = Solved(c.CarriesSource ? c.Outputs.ToArray() : like?.Outputs ?? Array.Empty<string>()),
        };
        if (c.CarriesSource)
        {
          HoldSource(node, c.Source);   // a read shows it, and an edit diffs against it
          node.ScriptInputAccess = BuildScriptTypes.InputsOf(c, null).Select(n => new KeyValuePair<string, string>(n, PortAccess.Of(c.InputAccess, n))).ToList();
        }
        _snap.Nodes.Add(node);
        _boxes[id] = lands
          ? new CanvasBox(landedAt.X + 140 * (cell % 6), landedAt.Y + 90 * (cell / 6), landedAt.X + 140 * (cell % 6) + 120, landedAt.Y + 90 * (cell / 6) + 40)
          : new CanvasBox(4000 + placed.Count * 160, 4000 + _built.Count * 200, 4120 + placed.Count * 160, 4040 + _built.Count * 200);
      }

      var wired = 0;
      foreach (var w in spec.Wires)
      {
        var from = w.FromId ?? (w.FromRef != null && byRef.TryGetValue(w.FromRef, out var f) ? f : (int?)null);
        var to = w.ToId ?? (w.ToRef != null && byRef.TryGetValue(w.ToRef, out var t) ? t : (int?)null);
        if (from == null || to == null) continue;
        _snap.Edges.Add(new AgentEdgeDto { FromNode = from.Value, FromParam = w.FromParam, ToNode = to.Value, ToParam = w.ToParam });
        wired++;
      }

      if (placed.Count > 0)
        _snap.Clusters.Add(new AgentCluster { Id = _nextCluster++, Label = address ?? "Placed", Members = placed.ToList() });

      var region = _regions.FirstOrDefault(r => string.Equals(r.Address, address, StringComparison.Ordinal));
      if (region != null) region.ComponentCount = placed.Count;
      if (into.HasValue) Grow(into.Value, placed);
      else if (address != null) _built[address] = placed;
      _revision++;

      answeredBy = "recorded";
      var verdict = BuildVerdict.Build(true, placed, wired, spec.Wires.Count, new BuildPreservation.Result(), spec.Components.Count, 0, null,
                                       region: address, replaced: replaced);
      return ToolResult.Success(Landing.Stamped(verdict, landedWhere));
    }

    private int RemoveBuilt(string address)
    {
      if (address == null || !_built.TryGetValue(address, out var old)) return 0;
      var gone = new HashSet<int>(old);
      _snap.Nodes.RemoveAll(n => gone.Contains(n.Id));
      _snap.Edges.RemoveAll(e => gone.Contains(e.FromNode) || gone.Contains(e.ToNode));
      _snap.Clusters.RemoveAll(c => c.Members.Any(gone.Contains));
      foreach (var id in old) { _ids.Retire(id); _boxes.Remove(id); }
      _built.Remove(address);
      return old.Count;
    }

    /// <summary>A catalogue tool that mutates, on a turn that holds it: recorded, answered from the fixture or a plain stand-in. It changes nothing a read shows.</summary>
    public string RecordMutation(string tool, string argsJson)
    {
      _revision++;
      return _fixture.CannedFor(tool, argsJson) ?? ToolResult.Success(new JsonObject { ["status"] = "applied", ["errors"] = 0, ["warnings"] = 0 }).ToJson();
    }
  }
}
