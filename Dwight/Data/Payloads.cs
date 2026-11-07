using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Grasshopper.Kernel;
using Dwight.Diagnostics;

namespace Dwight.Data
{
  /// <summary>
  /// Projects the <see cref="DwightGraph"/> snapshot into the per-action LLM payloads (Spec B1/B2),
  /// obeying the payload rules without exception (ADR-058): short-int ids (never guids), an explicit
  /// edge list, tree signatures (never tree contents), deterministic node ordering (by id, which is
  /// pivot order), and flat schemas. <c>guid</c> and <c>pivot</c> never appear here. Built on the UI
  /// thread (tree signatures read live volatile data), then handed as an immutable string to the
  /// network task.
  /// </summary>
  internal static class Payloads
  {
    /// <summary>explain.short: the focus plus its immediate (1-hop) neighbours for context, with tree
    /// signatures on the wires that touch the focus.</summary>
    public static string ExplainShort(DwightGraph graph) => Explain(graph, "explain.short", hops: 1);

    /// <summary>explain.detailed: the focus plus its 1-2 hop neighbourhood, with tree signatures.</summary>
    public static string ExplainDetailed(DwightGraph graph) => Explain(graph, "explain.detailed", hops: 2);

    /// <summary>explain.oncanvas: the selection subgraph plus immediate neighbours, so the model can
    /// emit an ordered step list with annotation marks referencing the focus ids. (Region-level
    /// callouts are gated on the segmenter, Phase 6; Phase 4 on-canvas works on the selection.)</summary>
    public static string ExplainOnCanvas(DwightGraph graph) => Explain(graph, "explain.oncanvas", hops: 1);

    private static string Explain(DwightGraph graph, string action, int hops)
    {
      var focus = graph.Selection.Select(n => n.Id).ToList();
      var neighbours = graph.Neighbourhood(focus, hops);
      var include = new HashSet<int>(focus);
      include.UnionWith(neighbours);

      var root = new JsonObject { ["action"] = action, ["focus"] = ToIdArray(focus) };
      Emit(graph, include, root);
      return root.ToJsonString();
    }

    /// <summary>diagnose: only the deterministically-found ROOT-CAUSE faults + their upstream trace +
    /// the runtime messages and input tree signatures (built only AFTER the scan, ADR-059). Downstream
    /// symptoms are NOT sent - the plugin renders them deterministically as "caused by &lt;root&gt;", so
    /// the model only spends tokens on the roots (the cost + latency win). Each root carries its class
    /// (error|warning|silent) so the model treats silent failures as suspicions, not certainties.</summary>
    public static string Diagnose(DwightGraph graph, FaultScan.Report report) =>
      DiagnoseFaults(graph, report.Intrinsic, report.WholeCanvas);

    /// <summary>Build a diagnose payload for a specific set of INTRINSIC faults to explain (ADR-078) -
    /// the whole report's intrinsic set, or one chain's, for the lazy chain-picker. Pure-propagation
    /// symptoms are never sent (the plugin renders them deterministically).</summary>
    public static string DiagnoseFaults(DwightGraph graph, IEnumerable<FaultScan.Fault> intrinsic, bool wholeCanvas)
    {
      var root = new JsonObject
      {
        ["action"] = "diagnose",
        ["scope"] = wholeCanvas ? "whole-canvas" : "selection",
      };

      var faultsArr = new JsonArray();
      var faultIds = new HashSet<int>();
      foreach (var f in intrinsic)   // intrinsic faults only - propagation symptoms are deterministic
      {
        faultIds.Add(f.Node.Id);
        var msgs = new JsonArray();
        foreach (var m in f.Node.Msgs.Where(m => m.Level == "error" || m.Level == "warning"))
          msgs.Add(new JsonObject { ["level"] = m.Level, ["text"] = m.Text });

        var fault = new JsonObject
        {
          ["id"] = f.Node.Id,
          ["type"] = f.Node.Type,
          ["nick"] = f.Node.Nick,
          ["class"] = f.Kind == FaultScan.Kind.Error ? "error" : f.Kind == FaultScan.Kind.Warning ? "warning" : "silent",
          ["role"] = f.IsRoot ? "root" : "symptom",
          ["msgs"] = msgs,
          ["outCounts"] = new JsonArray((f.Node.OutCounts ?? new int[0]).Select(c => (JsonNode)c).ToArray()),
        };
        if (f.Unwired.Count > 0) fault["unwired"] = new JsonArray(f.Unwired.Select(n => (JsonNode)n).ToArray());   // Brief EX: a fact the graph states, so the model has nothing to guess from
        faultsArr.Add(fault);
      }
      root["faults"] = faultsArr;

      // Upstream context: the 1-2 hop upstream of every fault, so the model can reason about causes.
      var upstream = UpstreamContext(graph, faultIds, hops: 2);
      var upArr = new JsonArray();
      foreach (var id in upstream.OrderBy(i => i))
      {
        var n = graph.NodeById(id);
        if (n != null) upArr.Add(Node(n));
      }
      root["upstream"] = upArr;

      // Edges + tree signatures over the fault set and its upstream context.
      var scope = new HashSet<int>(faultIds);
      scope.UnionWith(upstream);
      EmitEdgesAndTrees(graph, scope, root);
      return root.ToJsonString();
    }

    /// <summary>Brief CS: how many components a diagnose payload names, the faults and their upstream context, as
    /// <see cref="DiagnoseFaults"/> scopes them.</summary>
    public static int DiagnoseScopeCount(DwightGraph graph, IEnumerable<FaultScan.Fault> intrinsic)
    {
      var faultIds = new HashSet<int>(intrinsic.Select(f => f.Node.Id));
      var scope = new HashSet<int>(faultIds);
      scope.UnionWith(UpstreamContext(graph, faultIds, hops: 2));
      return scope.Count;
    }

    /// <summary>simplify.betterlogic (Phase 7 WP-2, minimal-diff RR3): the selection subgraph (nodes with an
    /// ""internal"" hint on components holding a hardcoded value, so the model can spot externalize candidates)
    /// + internal edges + tree signatures + the boundary that crosses the selection edge (informational, with
    /// tree sigs). <paramref name="outputProducers"/> is the plugin-computed set of selection ids that produce an
    /// output (boundary or terminal) - the validator's output-surface guard uses it to kill an output collapse.</summary>
    public static string Simplify(DwightGraph graph, GH_Document doc, out HashSet<int> outputProducers)
    {
      var selection = new HashSet<int>(graph.Selection.Select(n => n.Id));
      var root = new JsonObject
      {
        ["action"] = "simplify.betterlogic",
        ["focus"] = ToIdArray(selection.OrderBy(i => i)),
      };

      // Stage 7: the measured HOTSPOT signal - only the top few components with MEANINGFUL solve time, coarsely
      // bucketed. Advisory INPUT ONLY: ProcessorTime varies run to run, so `ms` must NEVER enter a cache key or the
      // graph-shape hash (the Simplify payload is not cached, and ReorganiseShapeHash hashes guids/types/edges, not
      // this payload - so it is isolated by construction).
      // Which components are hotspots, and the coarse 5ms bucket each is sent with, is SolveTimeBucket's decision -
      // ONE rounding, shared with the U23 timing read, and a guard on the value SENT, so a component whose time
      // rounds to zero is never named as slow (#875). This loop only gathers the readings.
      var readings = new List<KeyValuePair<int, int>>();
      try
      {
        foreach (var n in graph.Nodes.Where(n => selection.Contains(n.Id)))
          readings.Add(new KeyValuePair<int, int>(n.Id, ProcessorMs(doc, n.Guid)));
      }
      catch { }
      var hot = SolveTimeBucket.Hotspots(readings);

      var nodesArr = new JsonArray();
      foreach (var n in graph.Nodes.Where(n => selection.Contains(n.Id)).OrderBy(n => n.Id))
      {
        var node = Node(n);
        if (HasInternalValue(doc, n.Guid)) node["internal"] = true;   // an externalize candidate (a hardcoded lever)
        if (hot.TryGetValue(n.Id, out var ms)) node["ms"] = ms;
        EmitScriptSource(doc, n, node);   // RR17 #36: a script node is not a black box - the model sees its code
        nodesArr.Add(node);
      }
      root["nodes"] = nodesArr;

      EmitEdgesAndTrees(graph, selection, root);

      // boundary (informational): the distinct selection ports that cross the edge + tree sigs. The crossing
      // ORDER (the ext index the i<n>/o<n> refs point back at) comes from SimplifyBoundary - the SAME helper the
      // resolve builder resolves against, so ref resolution can never drift from what the model was shown.
      var edges = graph.Edges.Select(e => new SimplifyBoundary.Edge(e.FromNode, e.FromParam, e.ToNode, e.ToParam)).ToList();
      var inCross = SimplifyBoundary.Inputs(edges, selection);
      var outCross = SimplifyBoundary.Outputs(edges, selection);

      // the sig per crossing (first edge wins) + the output-producer guard set, in one edge pass.
      var hasOut = new HashSet<int>();
      var boundaryOut = new HashSet<int>();
      var inSig = new Dictionary<(int, string), TreeSig>();
      var outSig = new Dictionary<(int, string), TreeSig>();
      foreach (var e in graph.Edges)
      {
        var fromIn = selection.Contains(e.FromNode);
        var toIn = selection.Contains(e.ToNode);
        if (fromIn) { hasOut.Add(e.FromNode); if (!toIn) { boundaryOut.Add(e.FromNode); var k = (e.FromNode, e.FromParam); if (!outSig.ContainsKey(k)) outSig[k] = e.Tree; } }
        if (toIn && !fromIn) { var k = (e.ToNode, e.ToParam); if (!inSig.ContainsKey(k)) inSig[k] = e.Tree; }
      }

      var insArr = new JsonArray();
      for (var i = 0; i < inCross.Count; i++)
      {
        var c = inCross[i];
        var o = new JsonObject { ["ext"] = i, ["to"] = new JsonObject { ["node"] = c.Node, ["param"] = c.Param } };
        if (inSig.TryGetValue((c.Node, c.Param), out var s) && s != null) o["sig"] = SigJson(s);
        insArr.Add(o);
      }
      var outsArr = new JsonArray();
      for (var j = 0; j < outCross.Count; j++)
      {
        var c = outCross[j];
        var o = new JsonObject { ["ext"] = j, ["from"] = new JsonObject { ["node"] = c.Node, ["param"] = c.Param } };
        if (outSig.TryGetValue((c.Node, c.Param), out var s) && s != null) o["sig"] = SigJson(s);
        outsArr.Add(o);
      }
      root["boundary"] = new JsonObject { ["inputs"] = insArr, ["outputs"] = outsArr };

      outputProducers = new HashSet<int>(selection.Where(id => boundaryOut.Contains(id) || !hasOut.Contains(id)));
      return root.ToJsonString();
    }

    /// <summary>
    /// RR17 #36: emit a script component's SOURCE onto its payload node. Without this a script node arrives as
    /// <c>{"type":"GhPython Script"}</c> - an unreadable black box the model was nonetheless asked to reason about
    /// (and, for to-script, to REPRODUCE). It is the user's own code and the model needs it.
    ///
    /// <c>opaque:true</c> is the honest alternative when the code cannot be read (a locked or oversized script, a
    /// third-party plugin component, a cluster): the model is told plainly that it cannot see inside, rather than
    /// being left to assume the node does nothing. Better-logic can still work around an opaque node (it is a
    /// SEALED UNIT - keep it, rewire around it); to-script refuses outright, because it cannot reproduce what it
    /// cannot read.
    /// </summary>
    internal static void EmitScriptSource(GH_Document doc, GraphNode n, JsonObject node)
    {
      try
      {
        var obj = doc?.FindObject(n.Guid, false);
        if (obj == null) return;
        var src = ScriptSourceReader.IsScriptComponent(obj) ? ScriptSourceReader.TryRead(obj) : null;
        if (src != null) { node["source"] = src; return; }
        if (ScriptSourceReader.IsOpaque(obj)) node["opaque"] = true;
      }
      catch { /* a payload hint is never worth failing the action over */ }
    }

    /// <summary>Best-effort: does this component hold a hardcoded / internalized value (a param with persistent
    /// data, or a component with such an input)? Used only as an externalize HINT to the model, so a false miss
    /// is harmless. Reflection-guarded like the fingerprint reader, on the UI thread.</summary>
    private static bool HasInternalValue(GH_Document doc, System.Guid guid)
    {
      try
      {
        var obj = doc?.FindObject(guid, false);
        if (obj is IGH_Param p) return HasPersistent(p);
        if (obj is IGH_Component c) { foreach (var ip in c.Params.Input) if (HasPersistent(ip)) return true; }
      }
      catch { }
      return false;
    }

    // Phase-9 U1: the ONE persistent-data reader, shared with the agent's get_component so the verb payload
    // and the agent read can never disagree about whether a param holds data.
    private static bool HasPersistent(IGH_Param p) => PersistentValues.Has(p);

    /// <summary>Stage 7: the time a component took THE LAST TIME IT COMPUTED, in rounded ms
    /// (<c>IGH_ActiveObject.ProcessorTime</c>), structure-only - the HOTSPOT signal that lets performance advice target
    /// components that are MEASURABLY slow. Reflection-guarded, UI thread.
    ///
    /// 0 MEANS THREE DIFFERENT THINGS - not an active object, not yet solved, or under a millisecond - and this
    /// function cannot tell them apart. That is fine for Simplify, which only lists what is slow. It is NOT fine for
    /// a reader that must say whether a zero means "fast" or "never ran" (#843, distrust the zero), so the U23 timing
    /// read pairs this value with the object's solution phase rather than reading the zero on its own. Internal
    /// rather than private for that reason: one read of the value, shared, not a second one beside it.</summary>
    internal static int ProcessorMs(GH_Document doc, System.Guid guid)
    {
      try
      {
        if (doc?.FindObject(guid, false) is IGH_ActiveObject a)
          return (int)System.Math.Round(a.ProcessorTime.TotalMilliseconds);
      }
      catch { }
      return 0;
    }

    private static JsonObject SigJson(TreeSig sig) => new JsonObject
    {
      ["paths"] = sig.Paths,
      ["depth"] = sig.Depth,
      ["items"] = new JsonArray(sig.Items.Select(i => (JsonNode)i).ToArray()),
    };

    // ---- shared emit helpers ------------------------------------------------------------------------

    /// <summary>Emit nodes (ordered by id), the explicit edge list, and tree signatures for the wires
    /// inside <paramref name="include"/>.</summary>
    private static void Emit(DwightGraph graph, HashSet<int> include, JsonObject root)
    {
      var nodesArr = new JsonArray();
      foreach (var n in graph.Nodes.Where(n => include.Contains(n.Id)).OrderBy(n => n.Id))
        nodesArr.Add(Node(n));
      root["nodes"] = nodesArr;
      EmitEdgesAndTrees(graph, include, root);
    }

    internal static void EmitEdgesAndTrees(DwightGraph graph, HashSet<int> include, JsonObject root)
    {
      var edgesArr = new JsonArray();
      var treesArr = new JsonArray();
      var emittedIndex = 0;
      foreach (var e in graph.Edges
                 .Where(e => include.Contains(e.FromNode) && include.Contains(e.ToNode))
                 .OrderBy(e => e.FromNode).ThenBy(e => e.ToNode))
      {
        edgesArr.Add(new JsonObject
        {
          ["from"] = new JsonObject { ["node"] = e.FromNode, ["param"] = e.FromParam },
          ["to"] = new JsonObject { ["node"] = e.ToNode, ["param"] = e.ToParam },
        });

        var sig = e.Tree; // computed on demand from live volatile data (UI thread)
        if (sig != null)
        {
          treesArr.Add(new JsonObject
          {
            ["edge"] = emittedIndex,
            ["sig"] = new JsonObject
            {
              ["paths"] = sig.Paths,
              ["depth"] = sig.Depth,
              ["items"] = new JsonArray(sig.Items.Select(i => (JsonNode)i).ToArray()),
            },
          });
        }
        emittedIndex++;
      }
      root["edges"] = edgesArr;
      if (treesArr.Count > 0) root["trees"] = treesArr;
    }

    internal static JsonObject Node(GraphNode n)
    {
      var o = new JsonObject
      {
        ["id"] = n.Id,
        ["type"] = n.Type,
        ["nick"] = n.Nick,   // null when the user did not set one
      };
      Dwight.Session.DwightNotes.AddTo(o, n.Notes);
      return o;
    }

    internal static JsonArray ToIdArray(IEnumerable<int> ids) =>
      new JsonArray(ids.Select(i => (JsonNode)i).ToArray());

    private static HashSet<int> UpstreamContext(DwightGraph graph, HashSet<int> faultIds, int hops)
    {
      var ctx = new HashSet<int>();
      var frontier = new HashSet<int>(faultIds);
      for (var h = 0; h < hops; h++)
      {
        var next = new HashSet<int>();
        foreach (var id in frontier)
          foreach (var e in graph.IncomingTo(id))
            if (!faultIds.Contains(e.FromNode) && ctx.Add(e.FromNode)) next.Add(e.FromNode);
        frontier = next;
        if (frontier.Count == 0) break;
      }
      return ctx;
    }
  }
}
