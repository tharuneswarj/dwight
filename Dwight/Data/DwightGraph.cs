using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;

namespace Dwight.Data
{
  /// <summary>
  /// The internal graph model (ADR-058 / Spec A2): the typed in-memory snapshot every Phase-4 payload
  /// projects from, built fresh from <see cref="GH_Document"/> on each invocation. It is the
  /// anti-hallucination anchor (the model can only ever name a <c>short-int id</c> that exists here),
  /// and it carries the plugin-only fields (<c>guid</c>, <c>pivot</c>) that map the model's answer back
  /// onto the real canvas. Built once per request (whole-canvas), with the selection marked, so one
  /// graph serves selection-scope Explain/Diagnose AND whole-canvas Diagnose + the upstream trace.
  ///
  /// The deterministic core (ADR-056): the plugin computes every id, edge, and tree signature here; the
  /// LLM only ever reasons over the projection. <c>guid</c> and <c>pivot</c> never enter an LLM payload.
  /// </summary>
  internal sealed class DwightGraph
  {
    public List<GraphNode> Nodes { get; } = new List<GraphNode>();
    public List<GraphEdge> Edges { get; } = new List<GraphEdge>();

    private readonly Dictionary<Guid, GraphNode> _byGuid = new Dictionary<Guid, GraphNode>();
    private readonly Dictionary<int, GraphNode> _byId = new Dictionary<int, GraphNode>();

    public GraphNode NodeById(int id) => _byId.TryGetValue(id, out var n) ? n : null;
    public GraphNode NodeByGuid(Guid g) => _byGuid.TryGetValue(g, out var n) ? n : null;

    /// <summary>The guid -> short-int id map for the canvas number-badges (ADR-079): the glow layer draws
    /// each component's id next to it, matching the "(42)" the prose uses.</summary>
    public Dictionary<Guid, int> GuidToIdMap()
    {
      var map = new Dictionary<Guid, int>();
      foreach (var n in Nodes) map[n.Guid] = n.Id;
      return map;
    }

    /// <summary>The nodes in the user's current selection (the focus set), in deterministic order.</summary>
    public IReadOnlyList<GraphNode> Selection => Nodes.Where(n => n.InSelection).ToList();

    /// <summary>The number of logic objects on the whole canvas (for the cheap overview, ADR-038).</summary>
    public int CanvasCount => Nodes.Count;

    /// <summary>
    /// Build the whole-canvas snapshot from the live document and mark the selection. Crash-safe
    /// (ADR-029): any per-object failure is skipped, never thrown. Call on the UI thread (it reads the
    /// live document); the network task runs afterwards off-thread on the immutable result.
    /// </summary>
    public static DwightGraph Build(GH_Document doc, IEnumerable<Guid> selection)
    {
      var graph = new DwightGraph();
      if (doc == null) return graph;

      var selected = new HashSet<Guid>(selection ?? Array.Empty<Guid>());

      // Deterministic node ordering: left-to-right by pivot.x, ties by pivot.y (A3 rule 4). Ids are
      // assigned in this order, so they are stable within the snapshot and cache-friendly.
      var logic = new List<IGH_DocumentObject>();
      foreach (var obj in doc.Objects)
      {
        if (obj is IGH_Component || obj is IGH_Param) logic.Add(obj); // groups/scribbles are not logic
      }
      logic.Sort((a, b) =>
      {
        // Total order so ids are byte-stable across runs (A3 rule 4): pivot.x, then pivot.y, then guid
        // as the final tiebreaker (two boxes can share an exact pivot - e.g. overlapping or programmatic).
        var pa = PivotOf(a); var pb = PivotOf(b);
        var cx = pa.X.CompareTo(pb.X);
        if (cx != 0) return cx;
        var cy = pa.Y.CompareTo(pb.Y);
        if (cy != 0) return cy;
        return a.InstanceGuid.CompareTo(b.InstanceGuid);
      });

      var nextId = 1;
      foreach (var obj in logic)
      {
        try
        {
          var node = new GraphNode
          {
            Id = nextId++,
            Guid = obj.InstanceGuid,
            Type = obj.Name ?? "(component)",
            Nick = NickOf(obj),
            Pivot = PivotOf(obj),
            Kind = obj is IGH_Param ? "param" : "component",
            InSelection = selected.Contains(obj.InstanceGuid),
            Msgs = MessagesOf(obj),
            OutCounts = OutputCountsOf(obj),
          };
          graph.Nodes.Add(node);
          graph._byGuid[node.Guid] = node;
          graph._byId[node.Id] = node;
        }
        catch { /* skip an object we cannot read; fail safe */ }
      }

      // Edges: walk every input param's sources. The source is an output param whose owning object is
      // the top-level doc object. Both endpoints are graph nodes (we kept all logic objects). The source
      // param is held (plugin-only) so a tree signature can be computed on demand.
      var nextEdge = 0;
      foreach (var obj in logic)
      {
        try
        {
          var inputs = InputParamsOf(obj);
          var targetNode = graph.NodeByGuid(obj.InstanceGuid);
          if (targetNode == null) continue;
          foreach (var ip in inputs)
          {
            if (ip?.Sources == null) continue;
            foreach (var src in ip.Sources)
            {
              var owner = src?.Attributes?.GetTopLevel?.DocObject;
              if (owner == null) continue;
              var fromNode = graph.NodeByGuid(owner.InstanceGuid);
              if (fromNode == null) continue;
              graph.Edges.Add(new GraphEdge
              {
                Id = nextEdge++,
                FromNode = fromNode.Id,
                FromParam = ParamLabel(src),
                ToNode = targetNode.Id,
                ToParam = ParamLabel(ip),
                SourceParam = src,
              });
              fromNode.HasDownstream = true;
              targetNode.HasUpstream = true;
            }
          }
        }
        catch { /* skip; fail safe */ }
      }

      Dwight.Agent.GhNotes.Attach(doc, graph);   // Brief DC: what the person or an earlier Dwight wrote about a component travels with it
      return graph;
    }

    /// <summary>
    /// WP-4.4: force every edge's tree signature to compute from live <c>VolatileData</c> NOW, on the UI thread
    /// (the snapshot boundary), then drop the live <c>SourceParam</c> reference. After this, background code
    /// (segment / label / layout) reads only the cached, immutable <see cref="TreeSig"/>s and never touches live
    /// Grasshopper data off-thread (the ADR-010 guardrail). Must be called on the UI thread before the snapshot
    /// is handed to a task; a torn off-thread read of volatile data was the latent bug this closes.
    /// </summary>
    public void MaterializeTreeSignatures()
    {
      foreach (var e in Edges) e.Materialize();
    }

    /// <summary>Edges whose target is <paramref name="nodeId"/> (the wires feeding this node).</summary>
    public IEnumerable<GraphEdge> IncomingTo(int nodeId) => Edges.Where(e => e.ToNode == nodeId);

    /// <summary>Edges whose source is <paramref name="nodeId"/> (the wires leaving this node).</summary>
    public IEnumerable<GraphEdge> OutgoingFrom(int nodeId) => Edges.Where(e => e.FromNode == nodeId);

    /// <summary>The k-hop neighbourhood of a focus set (both directions), excluding the focus itself.
    /// Used to give Explain "what surrounds the selection" without sending the whole canvas.</summary>
    public HashSet<int> Neighbourhood(IEnumerable<int> focus, int hops)
    {
      var inFocus = new HashSet<int>(focus);
      var frontier = new HashSet<int>(inFocus);
      var reached = new HashSet<int>(inFocus);
      for (var h = 0; h < hops; h++)
      {
        var next = new HashSet<int>();
        foreach (var id in frontier)
        {
          foreach (var e in IncomingTo(id)) if (reached.Add(e.FromNode)) next.Add(e.FromNode);
          foreach (var e in OutgoingFrom(id)) if (reached.Add(e.ToNode)) next.Add(e.ToNode);
        }
        frontier = next;
        if (frontier.Count == 0) break;
      }
      reached.ExceptWith(inFocus);
      return reached;
    }

    // ---- reading the live document (all guarded; GH-version-tolerant) --------------------------------

    private static PointF PivotOf(IGH_DocumentObject obj)
    {
      try { return obj?.Attributes?.Pivot ?? PointF.Empty; } catch { return PointF.Empty; }
    }

    /// <summary>The nickname, only when the user actually set one (differs from the type name).</summary>
    private static string NickOf(IGH_DocumentObject obj)
    {
      try
      {
        var nick = obj?.NickName;
        return string.IsNullOrWhiteSpace(nick) || nick == obj.Name ? null : nick;
      }
      catch { return null; }
    }

    private static List<GraphMsg> MessagesOf(IGH_DocumentObject obj)
    {
      var msgs = new List<GraphMsg>();
      if (!(obj is IGH_ActiveObject active)) return msgs;
      AddMessages(msgs, active, GH_RuntimeMessageLevel.Error, "error");
      AddMessages(msgs, active, GH_RuntimeMessageLevel.Warning, "warning");
      AddMessages(msgs, active, GH_RuntimeMessageLevel.Remark, "remark");
      return msgs;
    }

    private static void AddMessages(List<GraphMsg> into, IGH_ActiveObject active, GH_RuntimeMessageLevel level, string label)
    {
      try
      {
        var list = active.RuntimeMessages(level);
        if (list == null) return;
        foreach (var text in list)
          if (!string.IsNullOrWhiteSpace(text)) into.Add(new GraphMsg { Level = label, Text = text.Trim() });
      }
      catch { }
    }

    /// <summary>Per-output volatile item counts (the silent-failure signal, ADR-059). For a free param,
    /// its own volatile count as a single entry.</summary>
    private static int[] OutputCountsOf(IGH_DocumentObject obj)
    {
      try
      {
        if (obj is IGH_Component comp)
          return comp.Params.Output.Select(p => SafeCount(p)).ToArray();
        if (obj is IGH_Param param)
          return new[] { SafeCount(param) };
      }
      catch { }
      return Array.Empty<int>();
    }

    /// <summary>Total volatile items arriving on a node's inputs (so a silent failure can be told apart
    /// from a node that simply received nothing).</summary>
    public static int InputDataCount(GraphNode node, GH_Document doc)
    {
      // Recomputed from the live object via guid; only used during the same UI-thread scan.
      try
      {
        var obj = doc?.FindObject(node.Guid, true);
        var total = 0;
        foreach (var ip in InputParamsOf(obj)) total += SafeCount(ip);
        return total;
      }
      catch { return 0; }
    }

    private static int SafeCount(IGH_Param p)
    {
      try { return p?.VolatileData?.DataCount ?? 0; } catch { return 0; }
    }

    private static IEnumerable<IGH_Param> InputParamsOf(IGH_DocumentObject obj)
    {
      if (obj is IGH_Component comp) return comp.Params.Input;
      if (obj is IGH_Param param) return new[] { param };
      return Array.Empty<IGH_Param>();
    }

    /// <summary>The label used to identify a param on an edge (nickname, else name). Internal so the Simplify
    /// resolve builder resolves a boundary crossing's (node, param) back to the same live port index the graph and
    /// payload used - one method, no drift (WP-2 Stage 5).</summary>
    internal static string ParamLabel(IGH_Param p)
    {
      try
      {
        var nick = p?.NickName;
        if (!string.IsNullOrWhiteSpace(nick)) return nick;
        return p?.Name ?? ".";
      }
      catch { return "."; }
    }
  }

  /// <summary>A runtime message on a node: level (error|warning|remark) + the GH text, verbatim.</summary>
  internal sealed class GraphMsg
  {
    public string Level { get; set; }
    public string Text { get; set; }
  }

  /// <summary>A node in the snapshot. <c>Guid</c> and <c>Pivot</c> are PLUGIN-ONLY (never serialised to
  /// the LLM); they are how a returned <c>id</c> maps back to the real canvas (ADR-058).</summary>
  internal sealed class GraphNode
  {
    public int Id { get; set; }
    public Guid Guid { get; set; }          // plugin-only
    public string Type { get; set; }
    public string Nick { get; set; }
    public PointF Pivot { get; set; }        // plugin-only
    public string Kind { get; set; }         // component | param
    public bool InSelection { get; set; }
    public List<GraphMsg> Msgs { get; set; } = new List<GraphMsg>();
    public int[] OutCounts { get; set; } = Array.Empty<int>();
    public bool HasUpstream { get; set; }
    public bool HasDownstream { get; set; }
    /// <summary>Brief DC: the lines of text this component's Dwight notes become in a payload (<see cref="Dwight.Session.DwightNotes.Lines"/>),
    /// or null when it has none. Text only: never the note's id, anchor, place or stamp.</summary>
    public List<string> Notes { get; set; }

    public bool HasError => Msgs.Any(m => m.Level == "error");
    public bool HasWarning => Msgs.Any(m => m.Level == "warning");
  }

  /// <summary>A wire: an explicit edge (A3 rule 2). <c>SourceParam</c> is plugin-only - it lets the
  /// plugin compute the wire's <see cref="TreeSig"/> on demand without ever sending tree contents.
  ///
  /// A CONSTRAINT THAT KEEPS RECURRING, so it explains itself where it is met (Phase 9, the payload-contract
  /// ruling): this live reference is WHY the whole payload layer is Grasshopper-coupled. Holding the source
  /// param here is a deliberate LAZINESS decision - a signature is computed only for the wires a payload
  /// actually emits, never for the whole graph - and it means no payload builder above this type can be
  /// compiled into the GH-free test projects. Whoever next proposes "just make the payload builders
  /// GH-free" meets that decision, not a tidiness problem; the price of undoing it is computing every
  /// signature eagerly at snapshot time. The verbs' payload contracts are therefore asserted by the runtime
  /// suite over a live document (<c>PayloadContract</c>, AssertedBy.RuntimeSuitePending), not headlessly.</summary>
  internal sealed class GraphEdge
  {
    public int Id { get; set; }
    public int FromNode { get; set; }
    public string FromParam { get; set; }
    public int ToNode { get; set; }
    public string ToParam { get; set; }
    public IGH_Param SourceParam { get; set; }   // plugin-only
    private TreeSig _tree;
    private bool _computed;

    /// <summary>The data-tree signature on this wire (A3 rule 3): structure only, never contents.
    /// Computed on demand from the source param's <c>VolatileData</c> and cached.</summary>
    public TreeSig Tree
    {
      get { if (!_computed) { _computed = true; _tree = TreeSig.From(SourceParam); } return _tree; }
    }

    /// <summary>WP-4.4: compute the signature from live volatile data (if not already) and drop the live
    /// <c>SourceParam</c> reference, so no background thread can read Grasshopper data through this edge. Call on
    /// the UI thread at the snapshot boundary. The signature it computed remains available via <see cref="Tree"/>.</summary>
    internal void Materialize()
    {
      if (!_computed) { _computed = true; _tree = TreeSig.From(SourceParam); }
      SourceParam = null;
    }
  }

  /// <summary>A data-tree signature: branch count, path depth, items-per-branch (ADR-058). The single
  /// highest-value comprehension feature - the thing a learner cannot read off the canvas - and it is
  /// structure only: never the values.
  ///
  /// PRIVACY SCOPE (RR20 #50): this claim is about the TREE SIGNATURE, and it holds. It is NOT a claim about the
  /// whole payload any more: Simplify's payloads deliberately carry a script component's SOURCE (RR17 #36) and the
  /// CONSTANT VALUES on the selection's boundary (RR20 #40) - without them the model is guessing at what the
  /// definition does, and it guessed wrong. Geometry is still never uploaded, and a local model sends nothing at
  /// all. The user-facing statement in Settings says exactly this.</summary>
  internal sealed class TreeSig
  {
    public int Paths { get; set; }
    public int Depth { get; set; }
    public int[] Items { get; set; } = Array.Empty<int>();

    /// <summary>How many per-branch counts to enumerate before summarising (keeps the payload flat).</summary>
    private const int ItemsCap = 24;

    /// <summary>Compute from a source param's volatile data, or null if there is no data / cannot read.</summary>
    public static TreeSig From(IGH_Param source)
    {
      try
      {
        var data = source?.VolatileData;
        if (data == null || data.PathCount == 0) return null;

        var paths = data.PathCount;
        var depth = 0;
        var items = new List<int>(Math.Min(paths, ItemsCap));
        var i = 0;
        foreach (var path in data.Paths)
        {
          if (path != null && path.Length > depth) depth = path.Length;
          if (i < ItemsCap)
          {
            try { items.Add(data.get_Branch(path)?.Count ?? 0); } catch { items.Add(0); }
          }
          i++;
        }
        return new TreeSig { Paths = paths, Depth = depth, Items = items.ToArray() };
      }
      catch { return null; }
    }

    /// <summary>A plain-language summary the prompt can lean on ("4 branches, 1 item each").</summary>
    public string Describe()
    {
      if (Paths == 0) return "empty";
      var perBranch = Items.Length > 0 && Items.All(n => n == Items[0])
        ? $"{Items[0]} item{(Items[0] == 1 ? "" : "s")} each"
        : Items.Length > 0 ? $"{Items.Min()}-{Items.Max()} items per branch" : "?";
      return Paths == 1
        ? $"1 branch, {perBranch}"
        : $"{Paths} branches (depth {Depth}), {perBranch}";
    }
  }
}
