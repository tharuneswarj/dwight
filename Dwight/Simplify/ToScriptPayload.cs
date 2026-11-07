using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Dwight.Data;

namespace Dwight.Simplify
{
  /// <summary>
  /// WP-4's payload: the selection's dataflow, the internalised constants the plugin READ from the live document,
  /// and - the load-bearing part - the INTERFACE the plugin computed from the boundary contract. The model is
  /// shown the exact arity, order, type and access its code will receive, so it writes against the real contract
  /// instead of guessing at one. It cannot alter that contract; a proposal that names a different number of params
  /// is rejected by <see cref="ToScriptValidator"/> rather than reshaped into whatever it meant.
  /// </summary>
  internal static class ToScriptPayload
  {
    public static string Build(DwightGraph graph, ToScriptContext ctx, ScriptLanguage lang)
    {
      var selection = new HashSet<int>(graph.Selection.Select(n => n.Id));
      var root = new JsonObject
      {
        ["action"] = "simplify.toscript",
        ["language"] = lang == ScriptLanguage.CSharp ? "csharp" : "python3",
        ["focus"] = Payloads.ToIdArray(selection.OrderBy(i => i)),
      };

      var nodes = new JsonArray();
      var doc = ctx.Resolve?.Doc;
      foreach (var n in graph.Nodes.Where(n => selection.Contains(n.Id)).OrderBy(n => n.Id))
      {
        var node = Payloads.Node(n);
        // RR17 #36: a script node in the selection is part of the behaviour being reproduced, so the model MUST
        // see its code. (An UNREADABLE one never reaches here - the builder refuses the whole collapse first.)
        Payloads.EmitScriptSource(doc, n, node);
        nodes.Add(node);
      }
      root["nodes"] = nodes;

      Payloads.EmitEdgesAndTrees(graph, selection, root);

      // the internalised values - plugin-read, never model-invented (a script has no components to hold them, so
      // every one must reach the source or the result silently differs).
      var constants = new JsonArray();
      foreach (var c in ctx.Constants ?? new List<ScriptConstant>())
      {
        var o = new JsonObject { ["node"] = c.NodeId, ["of"] = c.Label ?? "", ["value"] = c.Literal ?? "" };
        if (Dwight.Eval.EvalFlags.PortSemantics && !string.IsNullOrWhiteSpace(c.Feeds))
          o["feeds"] = c.Feeds;   // RR20 #40: the constant's ROLE, not just its number
        constants.Add(o);
      }
      root["constants"] = constants;

      root["interface"] = new JsonObject
      {
        ["inputs"] = Params(ctx.Iface?.Inputs, lang),
        ["outputs"] = Params(ctx.Iface?.Outputs, lang),
      };
      return root.ToJsonString();
    }

    /// <summary>RR20 #40/#50: a port is not a TYPE, it is a MEANING. Every field here is read from the live solved
    /// graph, so none of it can be wrong - and without them the model guesses (told only "double", it decided the
    /// 0.75 next to a Linear Array must be the count, and wrote (int)0.75 = 0 copies).</summary>
    private static JsonArray Params(List<ScriptParam> ps, ScriptLanguage lang)
    {
      var arr = new JsonArray();
      for (var i = 0; i < (ps?.Count ?? 0); i++)
      {
        var p = ps[i];
        var o = new JsonObject
        {
          ["index"] = i,
          ["ghType"] = p.GhType ?? "Generic Data",
          ["scriptType"] = ScriptTypeMap.DeclaredType(p.GhType, lang, p.Access),
          ["access"] = p.Access.ToString().ToLowerInvariant(),
        };
        if (Dwight.Eval.EvalFlags.PortSemantics)   // RR20 #56: the ONE variable the before-run turns off
        {
          if (!string.IsNullOrWhiteSpace(p.Connects)) o["connects"] = p.Connects;   // "feeds Unit Z (35).Factor"
          if (!string.IsNullOrWhiteSpace(p.Runtime)) o["runtime"] = p.Runtime;      // "closed planar PolylineCurve, 4 segments"
          if (!string.IsNullOrWhiteSpace(p.Constant)) o["constant"] = p.Constant;   // "0.75" - the fact that was missing
        }
        arr.Add(o);
      }
      return arr;
    }
  }
}
