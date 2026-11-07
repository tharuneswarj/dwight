using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Special;
using Grasshopper.Kernel.Types;
using Dwight.Data;

namespace Dwight.Simplify
{
  /// <summary>An internalised value living INSIDE the selection (a slider, a hardcoded param). A script has no
  /// components to hold these, so they must be baked into the source as constants - which means the model has to
  /// SEE them. The plugin reads the value; the model only writes it into the code.</summary>
  internal sealed class ScriptConstant
  {
    public int NodeId;
    public string Label;
    public string Literal;
    /// <summary>RR20 #40: what this constant FEEDS ("Unit Z (35).Factor"). A number without a role is exactly what
    /// the model misread as a count; a number WITH its role cannot be misread.</summary>
    public string Feeds;
  }

  /// <summary>Everything the to-script preview + apply need, derived from the live graph.</summary>
  internal sealed class ToScriptContext
  {
    public ToScriptResolve Resolve;
    public ScriptInterface Iface;
    public List<ScriptConstant> Constants = new List<ScriptConstant>();
    public string Refusal;   // non-null => Dwight will not attempt this collapse (and never calls the model)
    /// <summary>Phase-8 WP-0 (#103b): does any boundary INPUT port take MORE THAN ONE external source (Grasshopper
    /// merges them in source-list order)? The beside-copy wires only the first source per input (this builder takes
    /// Edges[0]), so such a selection is only PARTIALLY reproduced - safe to ADD alongside (the user compares), but
    /// NEVER offered as a Replace, which could splice a script that silently ignores a live source. The proper fix
    /// (wire ALL sources in encounter order) is a named post-re-measure item; this guard closes the hole meanwhile.</summary>
    public bool HasMultiSourceBoundaryInput;
  }

  /// <summary>
  /// WP-4's GH shim: turn the live selection into the plugin-computed script INTERFACE (arity, order, GH type,
  /// access) plus the guid-space resolve the apply consumes. The ordering hinge is
  /// <see cref="SimplifyBoundary"/> - the SAME canonical crossing order the payload shows the model and the
  /// Simplify resolve builder uses - so the script's output j IS the selection's boundary output j, and the 1:1
  /// beside-comparison is structural rather than a re-derived coincidence.
  ///
  /// It also owns the two DETERMINISTIC REFUSALS, both decided before a single token is spent:
  ///   - a selection that feeds nothing outside itself has no outputs to reproduce (a script of it would be
  ///     unobservable, and "verified" against an empty surface would be a lie);
  ///   - a selection holding internalised GEOMETRY cannot be reproduced by source code (a scalar can be baked
  ///     into the script; a referenced Brep cannot), so Dwight says so instead of writing a script that fails.
  /// </summary>
  internal static class ToScriptResolveBuilder
  {
    public static ToScriptContext Build(DwightGraph graph, GH_Document doc, ScriptLanguage lang)
    {
      var ctx = new ToScriptContext();
      if (graph == null || doc == null) { ctx.Refusal = "Dwight could not read the selection."; return ctx; }

      var selection = new HashSet<int>(graph.Selection.Select(n => n.Id));
      var edges = graph.Edges.Select(e => new SimplifyBoundary.Edge(e.FromNode, e.FromParam, e.ToNode, e.ToParam)).ToList();
      var inCross = SimplifyBoundary.Inputs(edges, selection);
      var outCross = SimplifyBoundary.Outputs(edges, selection);

      // #103b: flag a boundary input fed by several external sources - the beside-copy wires only Edges[0], so this
      // selection can be ADDED alongside but never REPLACED (see ToScriptContext.HasMultiSourceBoundaryInput).
      ctx.HasMultiSourceBoundaryInput = inCross.Any(c => c.Edges != null && c.Edges.Count > 1);

      if (outCross.Count == 0)
      {
        ctx.Refusal = "This selection doesn't feed anything outside itself, so a script of it would have no outputs to check against. " +
                      "Select a region whose results are used further downstream, and Dwight can collapse that.";
        return ctx;
      }

      // the tree signature per crossing (first edge wins - the same rule the payload's sig uses).
      var inSig = new Dictionary<(int, string), TreeSig>();
      var outSig = new Dictionary<(int, string), TreeSig>();
      foreach (var e in graph.Edges)
      {
        var fromIn = selection.Contains(e.FromNode);
        var toIn = selection.Contains(e.ToNode);
        if (toIn && !fromIn) { var k = (e.ToNode, e.ToParam); if (!inSig.ContainsKey(k)) inSig[k] = e.Tree; }
        if (fromIn && !toIn) { var k = (e.FromNode, e.FromParam); if (!outSig.ContainsKey(k)) outSig[k] = e.Tree; }
      }

      var rc = new ToScriptResolve
      {
        Doc = doc,
        Language = lang,
        SelectionBounds = SelectionBounds(graph, doc, selection),
      };
      var iface = new ScriptInterface();

      // ---- INPUTS: one per crossing INTO the selection --------------------------------------------------
      for (var i = 0; i < inCross.Count; i++)
      {
        var c = inCross[i];
        var first = c.Edges[0];
        var srcGuid = graph.NodeById(first.FromNode)?.Guid ?? Guid.Empty;
        var srcPort = OutPortIndex(doc, srcGuid, first.FromParam);
        rc.ExtIn[i] = (srcGuid, srcPort);

        var targetGuid = graph.NodeById(c.Node)?.Guid ?? Guid.Empty;
        var ghType = TypeNameOfInput(doc, targetGuid, c.Param);
        if (IsGeneric(ghType)) ghType = TypeNameOfOutput(doc, srcGuid, srcPort) ?? ghType;   // a generic target tells us nothing; the source does
        if (IsGeneric(ghType)) ghType = TypeFromData(OutputParamOf(doc, srcGuid, srcPort)) ?? ghType;   // RR18 #3: still generic? ask the DATA

        // RR20 #40/#50: the port's MEANING. Where the value LANDS is what says what it IS - a double landing on
        // "Unit Z.Factor" is a factor, not the count the model guessed. And if it is a constant, SAY the constant.
        var targetParam = InputParamByName(doc, targetGuid, c.Param);
        var sourceParam = OutputParamOf(doc, srcGuid, srcPort);
        iface.Inputs.Add(new ScriptParam
        {
          Ext = i,
          GhType = ghType,
          Access = AccessOf(inSig, (c.Node, c.Param)),
          Connects = $"feeds {NodeLabel(graph, c.Node)}.{c.Param}" +
                     $" (from {NodeLabel(graph, first.FromNode)}.{first.FromParam})",
          Runtime = RuntimeType.Describe(targetParam) ?? RuntimeType.Describe(sourceParam),
          Constant = RuntimeType.ConstantValue(doc.FindObject(srcGuid, false)),
        });
      }

      // ---- OUTPUTS: one per crossing OUT of the selection -----------------------------------------------
      for (var j = 0; j < outCross.Count; j++)
      {
        var c = outCross[j];
        var producerGuid = graph.NodeById(c.Node)?.Guid ?? Guid.Empty;
        var producer = ProducingParam(doc, producerGuid, c.Param);
        if (producer == null) { ctx.Refusal = "Dwight could not read one of the outputs this selection produces, so it won't risk collapsing it."; return ctx; }
        rc.BoundaryOutParams.Add(producer.InstanceGuid);

        var consumers = new List<(Guid, int)>();
        foreach (var e in c.Edges)
        {
          var consumerGuid = graph.NodeById(e.ToNode)?.Guid ?? Guid.Empty;
          if (consumerGuid == Guid.Empty) continue;
          var port = InPortIndex(doc, consumerGuid, e.ToParam);
          consumers.Add((consumerGuid, port));
          var input = InputParam(doc, consumerGuid, port);
          if (input != null && !rc.ConsumerParams.Contains(input.InstanceGuid)) rc.ConsumerParams.Add(input.InstanceGuid);
        }
        rc.ExtOut[j] = consumers;

        // RR18 #3: a GENERIC producer (an Addition's Result is a generic param) told us nothing, so the script's
        // output was typed `object` - which is how it came to emit a differently-wrapped goo than the original in
        // the first place. Ask the DATA what it actually carries, and type the script's output accordingly.
        var outType = producer.TypeName;
        if (IsGeneric(outType)) outType = TypeFromData(producer) ?? outType;

        // RR20 #40/#50: name what the output FEEDS - the downstream port is what the value has to BE.
        var firstConsumer = c.Edges.Count > 0 ? c.Edges[0] : default;
        iface.Outputs.Add(new ScriptParam
        {
          Ext = j,
          GhType = outType ?? "Generic Data",
          Access = AccessOf(outSig, (c.Node, c.Param)),
          Connects = $"produced by {NodeLabel(graph, c.Node)}.{c.Param}" +
                     (c.Edges.Count > 0 ? $", feeds {NodeLabel(graph, firstConsumer.ToNode)}.{firstConsumer.ToParam}" : ""),
          Runtime = RuntimeType.Describe(producer),
        });
      }

      // ---- RR17 #36: refuse an OPAQUE selection, before a single token is spent -------------------------
      // A script whose source cannot be read, a cluster, a third-party plugin component: Dwight cannot show the
      // model what it does, so a script "reproducing" it would be pure invention - and one that could still pass
      // the beside-verify on today's inputs while being wrong on tomorrow's. (A READABLE script is fine: its
      // source goes in the payload.) Saying no is the honest answer, and it costs nothing.
      var opaque = OpaqueComponents(graph, doc);
      if (opaque.Count > 0)
      {
        ctx.Refusal = $"Dwight can't read inside {Join(opaque)}, so it would be guessing at what the script has to reproduce. " +
                      "It won't collapse a selection it can't fully see. Leave that component out of the selection, and Dwight can script the rest.";
        return ctx;
      }

      // ---- the originals + the internalised values the script must carry --------------------------------
      foreach (var n in graph.Selection) rc.Originals.Add(n.Guid);
      if (!CollectConstants(graph, doc, ctx)) return ctx;   // sets Refusal on internalised geometry

      ctx.Resolve = rc;
      ctx.Iface = iface;
      return ctx;
    }

    /// <summary>RR17 #36: the selection's components whose behaviour Dwight cannot SHOW the model - an unreadable
    /// script, a cluster, a third-party plugin component. A readable script is NOT opaque (its source is sent).</summary>
    private static List<string> OpaqueComponents(DwightGraph graph, GH_Document doc)
    {
      var names = new List<string>();
      foreach (var n in graph.Selection)
      {
        try
        {
          var obj = doc.FindObject(n.Guid, false);
          if (obj != null && ScriptSourceReader.IsOpaque(obj)) names.Add($"the {Label(n, obj)} ({n.Id})");
        }
        catch { }
      }
      return names;
    }

    private static string Join(List<string> names)
    {
      if (names.Count == 1) return names[0];
      if (names.Count == 2) return names[0] + " and " + names[1];
      return string.Join(", ", names.Take(3)) + (names.Count > 3 ? $" and {names.Count - 3} more" : "");
    }

    /// <summary>A node as the model must read it: "Unit Z (35)" - the nickname when the user gave one (that IS the
    /// intent), else the component type, always with the payload id so it is groundable.</summary>
    private static string NodeLabel(DwightGraph graph, int id)
    {
      var n = graph.NodeById(id);
      if (n == null) return $"({id})";
      var name = string.IsNullOrWhiteSpace(n.Nick) ? n.Type : n.Nick;
      return $"{name} ({id})";
    }

    private static IGH_Param InputParamByName(GH_Document doc, Guid guid, string paramName)
    {
      var obj = doc.FindObject(guid, false);
      if (obj is IGH_Param p) return p;
      if (obj is IGH_Component c)
        return c.Params.Input.FirstOrDefault(x => NameMatches(x, paramName)) ?? c.Params.Input.FirstOrDefault();
      return null;
    }

    private static ScriptAccess AccessOf(Dictionary<(int, string), TreeSig> sigs, (int, string) key)
    {
      if (!sigs.TryGetValue(key, out var s) || s == null) return ScriptAccess.Item;
      return ScriptTypeMap.AccessFor(s.Paths, s.Items);
    }

    // ==== the internalised values (and the geometry refusal) ==============================================

    /// <summary>Read every internalised SCALAR inside the selection so the model can bake it into the source (the
    /// plugin reads the value; the model never invents one - the RR7 law). Internalised GEOMETRY is a hard refusal:
    /// a script cannot reconstruct a referenced Brep from a literal, so Dwight says so rather than writing a
    /// script that would silently compute something else.</summary>
    private static bool CollectConstants(DwightGraph graph, GH_Document doc, ToScriptContext ctx)
    {
      foreach (var n in graph.Selection)
      {
        var obj = doc.FindObject(n.Guid, false);
        if (obj == null) continue;

        // RR20 #40/#50 (and this is where #39 dissolves): a VALUE SOURCE inside the selection - a slider, a panel -
        // is a CONSTANT the script must embed, and the model must be told BOTH its value AND what it feeds. A bare
        // "0.75" is what the model mistook for a count; "0.75, feeds Unit Z (35).Factor" cannot be misread.
        if (obj is GH_NumberSlider slider)
        {
          ctx.Constants.Add(new ScriptConstant
          {
            NodeId = n.Id,
            Label = Label(n, obj),
            Literal = Literal(slider.CurrentValue),
            Feeds = FeedsOf(graph, slider),
          });
          continue;
        }
        if (obj is GH_Panel panel)
        {
          var text = RuntimeType.ConstantValue(panel);
          if (text != null)
            ctx.Constants.Add(new ScriptConstant { NodeId = n.Id, Label = Label(n, obj), Literal = text, Feeds = FeedsOf(graph, panel) });
          continue;
        }
        if (obj is IGH_Param p)
        {
          if (!ReadPersistent(p, n, obj, ctx, FeedsOf(graph, p))) return false;
          continue;
        }
        if (obj is IGH_Component c)
          foreach (var ip in c.Params.Input)
            if (ip.SourceCount == 0 && !ReadPersistent(ip, n, obj, ctx, $"{Label(n, obj)} ({n.Id}).{ip.Name}")) return false;
      }
      return true;
    }

    /// <summary>What a value source inside the selection FEEDS, named - so a constant carries its ROLE, not just
    /// its number. The whole Linear-Array failure was a number without a role.</summary>
    private static string FeedsOf(DwightGraph graph, IGH_Param source)
    {
      try
      {
        var targets = new List<string>();
        foreach (var r in source.Recipients ?? new List<IGH_Param>())
        {
          if (r == null) continue;
          var owner = r.Attributes?.GetTopLevel?.DocObject as IGH_DocumentObject ?? r;
          var node = graph.Selection.FirstOrDefault(x => x.Guid == owner.InstanceGuid)
                     ?? graph.Nodes.FirstOrDefault(x => x.Guid == owner.InstanceGuid);
          var label = node != null ? NodeLabel(graph, node.Id)
                    : (string.IsNullOrWhiteSpace(owner.NickName) ? owner.GetType().Name : owner.NickName);
          targets.Add($"{label}.{r.Name}");
        }
        return targets.Count == 0 ? null : string.Join(", ", targets.Distinct().Take(4));
      }
      catch { return null; }
    }

    private static bool ReadPersistent(IGH_Param p, GraphNode n, IGH_DocumentObject owner, ToScriptContext ctx, string feeds)
    {
      try
      {
        if (!(p.GetType().GetProperty("PersistentData")?.GetValue(p) is IGH_Structure data) || data.DataCount == 0) return true;
        foreach (var path in data.Paths)
        {
          var branch = data.get_Branch(path) as IList;
          if (branch == null) continue;
          foreach (var item in branch)
          {
            var lit = ScalarLiteral(item);
            if (lit == null)
            {
              ctx.Refusal = $"\"{Label(n, owner)}\" holds internalised geometry, which a script can't rebuild from code. " +
                            "Dwight only collapses selections whose data comes in through wires (or is a plain number, text, or toggle).";
              return false;
            }
            ctx.Constants.Add(new ScriptConstant { NodeId = n.Id, Label = Label(n, owner) + (p.Name != null ? " · " + p.Name : ""), Literal = lit, Feeds = feeds });
          }
        }
      }
      catch { }
      return true;
    }

    /// <summary>A scalar a script can hold as a literal, or null for anything it cannot (geometry, references).</summary>
    private static string ScalarLiteral(object goo)
    {
      switch (goo)
      {
        case GH_Number num: return Literal(num.Value);
        case GH_Integer i: return i.Value.ToString(CultureInfo.InvariantCulture);
        case GH_Boolean b: return b.Value ? "true" : "false";
        case GH_String s: return "\"" + (s.Value ?? "").Replace("\"", "\\\"") + "\"";
        default: return null;
      }
    }

    private static string Literal(decimal d) => d.ToString(CultureInfo.InvariantCulture);
    private static string Literal(double d) => d.ToString("R", CultureInfo.InvariantCulture);

    private static string Label(GraphNode n, IGH_DocumentObject obj)
    {
      if (!string.IsNullOrWhiteSpace(n?.Nick)) return n.Nick;
      if (!string.IsNullOrWhiteSpace(n?.Type)) return n.Type;
      return obj?.GetType().Name ?? "a component";
    }

    // ==== live-graph reads ================================================================================

    private static bool IsGeneric(string typeName) =>
      string.IsNullOrWhiteSpace(typeName) || typeName.Equals("Generic Data", StringComparison.OrdinalIgnoreCase);

    private static string TypeNameOfInput(GH_Document doc, Guid guid, string paramName)
    {
      var obj = doc.FindObject(guid, false);
      if (obj is IGH_Param p) return p.TypeName;
      if (obj is IGH_Component c)
      {
        var ip = c.Params.Input.FirstOrDefault(x => NameMatches(x, paramName)) ?? c.Params.Input.FirstOrDefault();
        return ip?.TypeName;
      }
      return null;
    }

    /// <summary>
    /// RR18 #3: the GH type a param's live DATA actually carries, when the param itself is generic. Measured on the
    /// live canvas: an Addition fed by integer sliders emits GH_Integer, and its output param is generic - so the
    /// declared type says "Generic Data" while the data says "Integer".
    ///
    /// A numeric goo widens to NUMBER, deliberately. Typing the script's output as an integer because today's data
    /// happens to be integral would TRUNCATE a fractional result tomorrow - a behaviour change that passes
    /// verification now and breaks later, which is the exact class RR7 taught us to refuse. The digest treats
    /// GH_Integer and GH_Number as one value anyway, so widening costs nothing and risks nothing.
    /// </summary>
    private static string TypeFromData(IGH_Param p)
    {
      try
      {
        var data = p?.VolatileData;
        if (data == null || data.PathCount == 0) return null;
        foreach (var path in data.Paths)
        {
          var branch = data.get_Branch(path) as IList;
          if (branch == null) continue;
          foreach (var item in branch)
          {
            if (!(item is IGH_Goo goo)) continue;
            if (goo is GH_Integer || goo is GH_Number) return "Number";   // widen: never hint int off today's data
            var tn = goo.TypeName;
            return string.IsNullOrWhiteSpace(tn) ? null : tn;             // Curve, Brep, Point, Text, ...
          }
        }
      }
      catch { }
      return null;
    }

    private static IGH_Param OutputParamOf(GH_Document doc, Guid guid, int port)
    {
      var obj = doc.FindObject(guid, false);
      if (obj is IGH_Param p) return p;
      if (obj is IGH_Component c && port >= 0 && port < c.Params.Output.Count) return c.Params.Output[port];
      return null;
    }

    private static string TypeNameOfOutput(GH_Document doc, Guid guid, int port)
    {
      var obj = doc.FindObject(guid, false);
      if (obj is IGH_Param p) return p.TypeName;
      if (obj is IGH_Component c && port >= 0 && port < c.Params.Output.Count) return c.Params.Output[port].TypeName;
      return null;
    }

    private static IGH_Param ProducingParam(GH_Document doc, Guid guid, string paramName)
    {
      var obj = doc.FindObject(guid, false);
      if (obj is IGH_Param p) return p;
      if (obj is IGH_Component c)
        return c.Params.Output.FirstOrDefault(x => NameMatches(x, paramName)) ?? c.Params.Output.FirstOrDefault();
      return null;
    }

    private static IGH_Param InputParam(GH_Document doc, Guid guid, int port)
    {
      var obj = doc.FindObject(guid, false);
      if (obj is IGH_Param p) return p;
      if (obj is IGH_Component c && port >= 0 && port < c.Params.Input.Count) return c.Params.Input[port];
      return null;
    }

    private static int OutPortIndex(GH_Document doc, Guid guid, string paramName)
    {
      var obj = doc.FindObject(guid, false);
      if (obj is IGH_Component c)
        for (var i = 0; i < c.Params.Output.Count; i++)
          if (NameMatches(c.Params.Output[i], paramName)) return i;
      return 0;
    }

    private static int InPortIndex(GH_Document doc, Guid guid, string paramName)
    {
      var obj = doc.FindObject(guid, false);
      if (obj is IGH_Component c)
        for (var i = 0; i < c.Params.Input.Count; i++)
          if (NameMatches(c.Params.Input[i], paramName)) return i;
      return 0;
    }

    private static bool NameMatches(IGH_Param p, string name) =>
      p != null && (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(p.NickName, name, StringComparison.OrdinalIgnoreCase));

    private static RectangleF SelectionBounds(DwightGraph graph, GH_Document doc, ISet<int> selection)
    {
      var acc = RectangleF.Empty;
      var any = false;
      foreach (var n in graph.Nodes)
      {
        if (!selection.Contains(n.Id)) continue;
        var b = doc.FindObject(n.Guid, false)?.Attributes?.Bounds;
        if (!b.HasValue) continue;
        acc = any ? RectangleF.Union(acc, b.Value) : b.Value;
        any = true;
      }
      return any ? acc : new RectangleF(0, 0, 0, 0);
    }
  }
}
