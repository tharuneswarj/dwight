using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Parameters;
using Grasshopper.Kernel.Special;
using Grasshopper.GUI.Base;
using Dwight.Components;
using Dwight.Data;

namespace Dwight.Simplify
{
  /// <summary>
  /// Builds the <see cref="SimplifyResolve"/> for a better-logic APPLY (WP-2 Stage 5, 3/3) - the GH shim that
  /// turns the validated proposal + the fresh graph + the live document into the guid-space context
  /// <see cref="SimplifyApply.Run"/> consumes. Built at PREVIEW time on the fresh graph, so the accept works in
  /// stable guid space (proposal ids are pivot-derived and shift between runs). Runs on the UI thread (it reads
  /// live params, bounds, and persistent values); every read is guarded and a failure degrades gracefully.
  ///
  /// The correctness hinge (progress doc): <see cref="SimplifyResolve.ExtIn"/>/<see cref="SimplifyResolve.ExtOut"/>
  /// are recomputed in <c>Payloads.Simplify</c>'s EXACT crossing order - via the SAME <see cref="SimplifyBoundary"/>
  /// helper the payload emits from - so the proposal's <c>i&lt;n&gt;</c>/<c>o&lt;n&gt;</c> refs resolve to the very
  /// crossings the model saw. Guard #6: an externalize slider's value is read from the REPLACED source here
  /// (plugin-derived), never emitted by the model.
  /// </summary>
  internal static class SimplifyResolveBuilder
  {
    /// <summary>The validator's removal-coverage input (progress doc item 2): for each selection component, its
    /// KEPT/selection-internal consumer ids - so removing a component whose output feeds a still-present consumer
    /// without a rewire is rejected at preview, not silently starved. Selection-internal edges only.</summary>
    public static Dictionary<int, HashSet<int>> BuildConsumers(DwightGraph graph, ISet<int> selection)
    {
      var map = new Dictionary<int, HashSet<int>>();
      if (graph == null || selection == null) return map;
      foreach (var e in graph.Edges)
      {
        if (!selection.Contains(e.FromNode) || !selection.Contains(e.ToNode)) continue;
        if (!map.TryGetValue(e.FromNode, out var set)) { set = new HashSet<int>(); map[e.FromNode] = set; }
        set.Add(e.ToNode);
      }
      return map;
    }

    /// <summary>Build the resolve context on the fresh graph + live document (UI thread). Null on a bad input.</summary>
    public static SimplifyResolve Build(DwightGraph graph, GH_Document doc, SimplifyProposal proposal, HashSet<int> outputProducers)
    {
      if (graph == null || doc == null || proposal == null) return null;
      var selection = new HashSet<int>(graph.Selection.Select(n => n.Id));

      var rc = new SimplifyResolve
      {
        Doc = doc,
        Proposal = proposal,
        SelectionBounds = SelectionBounds(graph, doc, selection),
      };

      // proposal id -> live guid (selection ids only; k<id> refs point into the selection).
      foreach (var n in graph.Nodes) if (selection.Contains(n.Id)) rc.IdToGuid[n.Id] = n.Guid;

      // AddType: a type already in the SELECTION is placeable by its own ComponentGuid (RR3 #3); otherwise the
      // normalised whitelist. Same order the validator's TypeAllowed accepted, so a validated add always resolves.
      var selectionTypes = SelectionComponentGuids(graph, doc, selection);
      foreach (var a in proposal.Add ?? new List<AddNode>())
      {
        var t = (a?.Type ?? "").Trim();
        if (t.Length == 0 || rc.AddType.ContainsKey(t)) continue;
        var g = selectionTypes.TryGetValue(t, out var sg) ? sg : ComponentWhitelist.GuidFor(t);
        if (g.HasValue && g.Value != Guid.Empty) rc.AddType[t] = g.Value;
      }

      // ExtIn / ExtOut in Payloads.Simplify's EXACT ordering (the shared helper is the identity guarantee).
      var edges = graph.Edges.Select(e => new SimplifyBoundary.Edge(e.FromNode, e.FromParam, e.ToNode, e.ToParam)).ToList();
      var inCross = SimplifyBoundary.Inputs(edges, selection);
      var outCross = SimplifyBoundary.Outputs(edges, selection);

      for (var i = 0; i < inCross.Count; i++)
      {
        var first = inCross[i].Edges[0];   // the external source feeding this crossing (first-wins, as the sig)
        var g = graph.NodeById(first.FromNode)?.Guid ?? Guid.Empty;
        rc.ExtIn[i] = (g, OutPortIndex(doc, g, first.FromParam));
      }
      for (var j = 0; j < outCross.Count; j++)
      {
        var consumers = new List<(Guid, int)>();
        foreach (var e in outCross[j].Edges)
        {
          var g = graph.NodeById(e.ToNode)?.Guid ?? Guid.Empty;
          if (g != Guid.Empty) consumers.Add((g, InPortIndex(doc, g, e.ToParam)));
        }
        rc.ExtOut[j] = consumers;
      }

      // Externalize per slider add (guard #6): read the replaced source's live value + int/float kind.
      DeriveExternalize(graph, doc, proposal, rc, selection);

      // Stage 6: the behaviour-neutral renames, resolved to guids (the validator already sealed named components).
      foreach (var rn in proposal.Renames ?? new List<Rename>())
        if (rn != null && !string.IsNullOrWhiteSpace(rn.Nick) && rc.IdToGuid.TryGetValue(rn.Id, out var rg))
          rc.Renames.Add((rg, rn.Nick));

      // Layer2Params: the PRESERVED output surface -> the node guids layer 2 samples before/after.
      rc.Layer2Params = BuildLayer2Params(graph, proposal, outCross, outputProducers, selection);

      return rc;
    }

    // ==== the added components' anchor + type resolution =================================================

    private static RectangleF SelectionBounds(DwightGraph graph, GH_Document doc, ISet<int> selection)
    {
      var acc = RectangleF.Empty; var any = false;
      foreach (var n in graph.Nodes)
      {
        if (!selection.Contains(n.Id)) continue;
        var b = doc.FindObject(n.Guid, false)?.Attributes?.Bounds;
        if (!b.HasValue) continue;
        acc = any ? RectangleF.Union(acc, b.Value) : b.Value; any = true;
      }
      return acc;
    }

    /// <summary>Type name -> ComponentGuid, read from a live selected instance of that type - so a proposed add of
    /// a type ALREADY present (a sealed plugin/old component the whitelist might not name) is placeable by its own
    /// registered guid.</summary>
    private static Dictionary<string, Guid> SelectionComponentGuids(DwightGraph graph, GH_Document doc, ISet<int> selection)
    {
      var map = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
      foreach (var n in graph.Nodes)
      {
        if (!selection.Contains(n.Id)) continue;
        var obj = doc.FindObject(n.Guid, false);
        var name = (obj?.Name ?? "").Trim();
        if (obj == null || name.Length == 0 || map.ContainsKey(name)) continue;
        try { var cg = obj.ComponentGuid; if (cg != Guid.Empty) map[name] = cg; } catch { }
      }
      return map;
    }

    // ==== port-index resolution (reuses DwightGraph.ParamLabel - one label rule, no drift) ================

    private static int OutPortIndex(GH_Document doc, Guid guid, string label) => PortIndex(doc.FindObject(guid, false), label, input: false);
    private static int InPortIndex(GH_Document doc, Guid guid, string label) => PortIndex(doc.FindObject(guid, false), label, input: true);

    private static int PortIndex(IGH_DocumentObject obj, string label, bool input)
    {
      if (obj is IGH_Component c)
      {
        var ports = input ? c.Params.Input : c.Params.Output;
        for (var i = 0; i < ports.Count; i++)
          if (DwightGraph.ParamLabel(ports[i]) == label) return i;
      }
      return 0;   // a standalone param (InputPort/OutputPortParam ignore the port for a param), or an unmatched label
    }

    // ==== externalize: the replaced source's value is plugin-read (guard #6) =============================

    /// <summary>RR7: externalize is a fully PLUGIN-COMPUTED, value-preserving substitution. The model only NOMINATES
    /// the target (<c>node.ExternalizeTargetId</c> + optional param NAME); the plugin reads the exact value, the
    /// TYPE (a gate/index/selector input -> an INTEGER slider), and the exact consumer input port(s) from the LIVE
    /// graph, and records the wiring so the apply connects the slider to PRECISELY those (never the model's rewire).
    /// Two cases: a REMOVED value-source (the slider takes over every input it fed), or an INTERNALIZED value on a
    /// kept input (the slider replaces it in place and the internalized copy is cleared - the value lives in one
    /// place). Nothing here trusts the model beyond the nominated target id/name.</summary>
    private static void DeriveExternalize(DwightGraph graph, GH_Document doc, SimplifyProposal proposal, SimplifyResolve rc, ISet<int> selection)
    {
      var removed = new HashSet<int>(proposal.Remove ?? new List<int>());
      var adds = proposal.Add ?? new List<AddNode>();
      for (var i = 0; i < adds.Count; i++)
      {
        var node = adds[i];
        if (node == null || !IsSliderType(node.Type) || node.ExternalizeTargetId == null) continue;
        var targetId = node.ExternalizeTargetId.Value;

        // RR8 #15: one shared, all-or-nothing resolution primitive (value + type + >=1 port). On failure the add
        // is left UNRESOLVED here - it should already have been dropped as an un-externalizable group by
        // ApplySelected (fix B); the completeness guard (fix C) is the backstop so an orphan can never ship.
        if (!TryResolveExternalize(graph, doc, targetId, node.ExternalizeTargetParam, removed.Contains(targetId),
              out var value, out var isInt, out var wiring, out var kind, out _))
          continue;

        node.Externalize = new ExternalizeSpec { Value = value, Integer = isInt };
        rc.ExternalizeWiring[i] = wiring;
        // RR8 #20: CASE 2 (a removed source) inherits the source pivot; CASE 1 (a NEW slider for an internalized
        // value) has no source, so it lands a margin to the LEFT of the input it feeds, at that input's height -
        // GH reads left->right, so an input's source belongs to its left. Overlap is nudged in LayoutAdds.
        rc.AddAnchor[i] = kind == "internalized" && wiring.Count > 0
          ? LeftOfInput(doc, wiring[0].Item1, wiring[0].Item2)
          : graph.NodeById(targetId)?.Pivot ?? default;
#if DEBUG
        try { Rhino.RhinoApp.WriteLine($"Dwight externalize (plugin-computed): add[{i}] target={targetId} value={value} int={isInt} -> {wiring.Count} port(s) [{kind}]"); } catch { }
#endif
      }
    }

    /// <summary>RR8 #15 (fix A): the ONE externalize resolution primitive, shared by <see cref="DeriveExternalize"/>
    /// and the ApplySelected pre-drop (fix B) so the two views can never diverge. Reads the LIVE graph and succeeds
    /// only when value + type + at least one target port ALL resolve; otherwise returns false with a human label
    /// for the "couldn't externalize X - skipped" note. Component-driven: the model's param NAME is a preference,
    /// never a requirement (a mismatched/absent name falls back to the sole internalized-numeric lever).</summary>
    internal static bool TryResolveExternalize(DwightGraph graph, GH_Document doc, int targetId, string paramName,
      bool targetRemoved, out double value, out bool isInt, out List<(Guid Owner, int Port)> wiring, out string kind, out string label)
    {
      value = 0; isInt = false; wiring = new List<(Guid, int)>(); kind = null;
      var targetGuid = graph.NodeById(targetId)?.Guid ?? Guid.Empty;
      var targetObj = targetGuid == Guid.Empty ? null : doc.FindObject(targetGuid, false);
      label = targetObj == null ? $"component {targetId}"
            : (string.IsNullOrWhiteSpace(targetObj.NickName) ? targetObj.Name : targetObj.NickName);
      if (targetObj == null) return false;

      if (targetRemoved)
      {
        // CASE 2: a REMOVED value-source. Read its value+type; the slider takes over EVERY input it fed.
        if (!TryReadValue(targetObj, out value, out isInt)) return false;
        foreach (var outp in OutputParamsOf(targetObj))
          foreach (var consumer in RecipientsOf(outp))
          { var p = PortOf(consumer); if (p.HasValue) wiring.Add(p.Value); }
        kind = "removed-source";
      }
      else
      {
        // CASE 1: an INTERNALIZED value on the kept target's input (the model's named lever, or the sole one).
        var input = ResolveExternalizeInput(targetObj, paramName);
        if (input == null) return false;
        if (!(TryReadPersistentNumber(input, out value, out isInt) || TryReadVolatileNumber(input, out value, out isInt))) return false;
        if (input is Param_Integer) isInt = true;   // a gate / index / selector input yields an INTEGER slider
        var pt = PortOf(input); if (!pt.HasValue) return false;
        wiring.Add(pt.Value);   // the slider feeds this input; its value overrides the internalized copy (value-preserving)
        kind = "internalized";
      }
      return wiring.Count > 0;   // all-or-nothing: no ports resolved -> not externalizable
    }

    /// <summary>RR8 #15 (fix B): externalize is all-or-nothing. Given the ticked change-group indices, return the
    /// SURVIVORS - dropping any group whose externalize cannot fully resolve (value + type + >=1 port) against the
    /// live graph - and collect a human label per dropped group for the honest "couldn't externalize X - skipped"
    /// note. A group with no externalize add always survives. Runs on the UI thread (it reads live params).</summary>
    internal static List<int> ResolvableTickedGroups(List<ChangeGroup> groups, IEnumerable<int> ticked, DwightGraph graph, GH_Document doc, out List<string> skipped)
    {
      skipped = new List<string>();
      var survivors = new List<int>();
      if (groups == null) return survivors;
      var tickedList = (ticked ?? Enumerable.Empty<int>()).Where(gi => gi >= 0 && gi < groups.Count).Distinct().ToList();
      var removedUnion = new HashSet<int>();
      foreach (var gi in tickedList) foreach (var r in groups[gi].Remove ?? new List<int>()) removedUnion.Add(r);

      foreach (var gi in tickedList)
      {
        var ok = true;
        foreach (var a in groups[gi].Add ?? new List<AddNode>())
        {
          if (a == null || !IsSliderType(a.Type) || a.ExternalizeTargetId == null) continue;
          if (!TryResolveExternalize(graph, doc, a.ExternalizeTargetId.Value, a.ExternalizeTargetParam,
                removedUnion.Contains(a.ExternalizeTargetId.Value), out _, out _, out _, out _, out var label))
          { ok = false; skipped.Add(label); break; }
        }
        if (ok) survivors.Add(gi);
      }
      return survivors;
    }

    /// <summary>RR8 #20: the pivot for a CASE-1 externalize slider - a margin to the LEFT of the input it feeds,
    /// at that input's height (its source belongs to its left). Reads the live input param's bounds; degrades to
    /// the owner's bounds, then origin.</summary>
    private static PointF LeftOfInput(GH_Document doc, Guid owner, int port)
    {
      try
      {
        var o = doc.FindObject(owner, false);
        IGH_Param ip = (o is IGH_Component c && port >= 0 && port < c.Params.Input.Count) ? c.Params.Input[port] : o as IGH_Param;
        var b = ip?.Attributes?.Bounds ?? o?.Attributes?.Bounds ?? default;
        return new PointF(b.Left - 210f, b.Top);   // ~a slider width + a gap to the left; aligned to the input's row
      }
      catch { return default; }
    }

    private static bool IsSliderType(string type) => (type ?? "").Trim().Equals("Number Slider", StringComparison.OrdinalIgnoreCase);

    /// <summary>RR8 #15 (fix A): the input to externalize on a kept component, resolved COMPONENT-FIRST. The
    /// candidate levers are the component's UNWIRED inputs carrying a readable internalized numeric value (a wired
    /// input's value comes from upstream, not a constant, so it is excluded; a boolean like Wrap='True' fails the
    /// numeric read). The model's param NAME only PICKS among candidates (Name / NickName / label); when it matches
    /// none - or is absent - the sole candidate is used. Null only when there is no lever, or several with no name
    /// match (ambiguous) - which the caller turns into an honest skip, never an orphan.</summary>
    private static IGH_Param ResolveExternalizeInput(IGH_DocumentObject obj, string paramName)
    {
      if (!(obj is IGH_Component comp)) return obj as IGH_Param;   // a standalone value-param is its own input

      var candidates = new List<IGH_Param>();
      var identity = new List<(string, string, string)>();
      foreach (var ip in comp.Params.Input)
        if (ip != null && ip.SourceCount == 0 && (TryReadPersistentNumber(ip, out _, out _) || TryReadVolatileNumber(ip, out _, out _)))
        { candidates.Add(ip); identity.Add((DwightGraph.ParamLabel(ip), ip.Name, ip.NickName)); }

      var pick = ExternalizeInputPicker.Pick(identity, paramName);   // GH-free decision (ADR-091)
      return pick >= 0 ? candidates[pick] : null;                    // -1 -> no lever / ambiguous -> caller skips (fix B)
    }

    /// <summary>The owner component guid + input port index of a live input param, so it re-resolves after the apply
    /// (a component's input param is not a top-level FindObject target; a standalone param is its own owner).</summary>
    private static (Guid Owner, int Port)? PortOf(IGH_Param input)
    {
      try
      {
        var owner = input?.Attributes?.GetTopLevel?.DocObject;
        if (owner is IGH_Component c) { var idx = c.Params.Input.IndexOf(input); if (idx >= 0) return (owner.InstanceGuid, idx); return null; }
        if (owner != null) return (owner.InstanceGuid, 0);
        if (input != null) return (input.InstanceGuid, 0);
      }
      catch { }
      return null;
    }

    private static IEnumerable<IGH_Param> RecipientsOf(IGH_Param source)
    {
      var list = new List<IGH_Param>();
      try { if (source?.Recipients != null) foreach (var r in source.Recipients) if (r != null) list.Add(r); } catch { }
      return list;
    }

    private static IEnumerable<IGH_Param> OutputParamsOf(IGH_DocumentObject obj)
    {
      if (obj is IGH_Component c) return c.Params.Output;
      if (obj is IGH_Param p) return new[] { p };
      return System.Array.Empty<IGH_Param>();
    }

    private static bool TryReadValue(IGH_DocumentObject obj, out double value, out bool isInt)
    {
      value = 0; isInt = false;
      try
      {
        switch (obj)
        {
          case GH_NumberSlider s:
            value = (double)s.CurrentValue;
            isInt = s.Slider != null && s.Slider.Type == GH_SliderAccuracy.Integer;
            return true;
          case GH_Panel p:
            return TryParseNumber(p.UserText, out value, out isInt);
          case IGH_Param param:
            if (TryReadPersistentNumber(param, out value, out isInt)) { if (param is Param_Integer) isInt = true; return true; }
            return false;
        }
      }
      catch { }
      return false;
    }

    /// <summary>The input's live EFFECTIVE numeric value - its solved VolatileData first item. For an unwired input
    /// this is the value it is actually computing with (a hardcoded index surfaced by the solve), which is exactly
    /// what an externalized slider must reproduce.</summary>
    private static bool TryReadVolatileNumber(IGH_Param p, out double value, out bool isInt)
    {
      value = 0; isInt = false;
      try
      {
        var data = p?.VolatileData;
        if (data == null || data.DataCount == 0) return false;
        object first = null;
        foreach (var path in data.Paths)
        {
          IList branch = null; try { branch = data.get_Branch(path); } catch { }
          if (branch != null && branch.Count > 0) { first = branch[0]; break; }
        }
        if (first == null || !TryParseNumber(first.ToString(), out value, out isInt)) return false;
        if (p is Param_Integer) isInt = true;
        return true;
      }
      catch { return false; }
    }

    private static bool TryReadPersistentNumber(IGH_Param p, out double value, out bool isInt)
    {
      value = 0; isInt = false;
      try
      {
        if (!(p?.GetType().GetProperty("PersistentData")?.GetValue(p) is IGH_Structure data) || data.DataCount == 0) return false;
        object first = null;
        foreach (var path in data.Paths)
        {
          IList branch = null; try { branch = data.get_Branch(path); } catch { }
          if (branch != null && branch.Count > 0) { first = branch[0]; break; }
        }
        if (first == null || !TryParseNumber(first.ToString(), out value, out isInt)) return false;
        if (p is Param_Integer) isInt = true;
        return true;
      }
      catch { return false; }
    }

    private static bool TryParseNumber(string text, out double value, out bool isInt)
    {
      value = 0; isInt = false;
      var t = (text ?? "").Trim();
      if (!double.TryParse(t, NumberStyles.Any, CultureInfo.InvariantCulture, out value)) return false;
      isInt = value == Math.Truncate(value) && t.IndexOf('.') < 0 && t.IndexOf('e') < 0 && t.IndexOf('E') < 0;
      return true;
    }

    // ==== layer-2 preserved surface -> sample node guids ================================================

    /// <summary>The output surface as <see cref="OutputPort"/>s, filtered by <see cref="Layer2Surface.Preserved"/>
    /// to what must survive the change, mapped to the node guids <c>CaptureOutputs</c> samples: a terminal port at
    /// its producer, a boundary port at its EXTERNAL consumer (where the data leaves - and which survives even
    /// when the producer is removed but re-fed). The surface producer set is <paramref name="outputProducers"/> -
    /// the SAME set the validator's output-surface guard used - so the two views cannot diverge.</summary>
    private static List<Guid> BuildLayer2Params(DwightGraph graph, SimplifyProposal proposal, List<SimplifyBoundary.Crossing> outCross, HashSet<int> outputProducers, ISet<int> selection)
    {
      var ports = new List<OutputPort>();
      foreach (var n in graph.Nodes)
      {
        if (outputProducers == null || !outputProducers.Contains(n.Id)) continue;   // only surface producers
        var external = graph.OutgoingFrom(n.Id).Where(e => !selection.Contains(e.ToNode)).ToList();
        if (external.Count > 0)
          foreach (var e in external) ports.Add(new OutputPort(n.Id, false, e.ToNode));   // boundary: ConsumerKey = external consumer id
        else
          ports.Add(new OutputPort(n.Id, true, -1));                                        // terminal: an output producer with no external edge feeds nothing
      }

      var keep = new HashSet<int>(proposal.Keep ?? new List<int>());
      // a boundary consumer re-fed by an o<ext> rewire is still fed -> keep it in the compare (RR4 #3).
      var rewired = new HashSet<int>();
      foreach (var w in proposal.Rewires ?? new List<Rewire>())
        if (TryRefId(w?.To, 'o', out var ext) && ext >= 0 && ext < outCross.Count)
          foreach (var e in outCross[ext].Edges) rewired.Add(e.ToNode);

      var guids = new List<Guid>();
      var seen = new HashSet<Guid>();
      foreach (var p in Layer2Surface.Preserved(ports, keep, rewired))
      {
        var g = p.Terminal ? graph.NodeById(p.ProducerId)?.Guid : graph.NodeById(p.ConsumerKey)?.Guid;
        if (g.HasValue && g.Value != Guid.Empty && seen.Add(g.Value)) guids.Add(g.Value);
      }
      return guids;
    }

    // ==== ref helpers ===================================================================================

    private static bool TryRefId(string reff, char kind, out int n)
    {
      n = 0;
      if (string.IsNullOrEmpty(reff) || reff.Length < 2 || reff[0] != kind) return false;
      return int.TryParse(reff.Substring(1), out n);
    }
  }
}
