using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace Dwight.Simplify
{
  /// <summary>Parse the LLM's CHANGE-GROUPED <c>simplify_proposal</c> JSON (Stage 6) into a list of
  /// <see cref="ChangeGroup"/>s - each classified change owning its low-level ops. GH-free and defensive (returns
  /// null on a non-object root). The caller flattens the groups into a <see cref="SimplifyProposal"/> via
  /// <see cref="PlanFilter"/> (all groups for the full preview, a ticked subset for selective accept).</summary>
  internal static class SimplifyProposalParser
  {
    public static List<ChangeGroup> Parse(string json)
    {
      try
      {
        if (!(JsonNode.Parse(json) is JsonObject root)) return null;
        var groups = new List<ChangeGroup>();

        if (root["changes"] is JsonArray changes)
          foreach (var c in changes)
          {
            if (!(c is JsonObject o)) continue;
            var g = new ChangeGroup { Class = Str(o["class"]), Text = Str(o["text"]), Confirm = Bool(o["confirm"]) };
            var changeIndex = groups.Count;   // RR8 #21: this group's index in the rendered change list

            foreach (var i in Ints(o["remove"])) g.Remove.Add(i);
            if (o["add"] is JsonArray add)
              foreach (var a in add)
                if (a is JsonObject ao) g.Add.Add(new AddNode { Type = Str(ao["type"]), Nick = Str(ao["nick"]), OriginChange = changeIndex });

            // RR7: the externalize target the model NOMINATES (component + optional input param NAME). Attach it to
            // the change's slider add so it flows through flatten to the resolve, where the plugin computes the rest.
            if (o["target"] is JsonObject t)
              foreach (var node in g.Add)
                if (IsSlider(node.Type)) { node.ExternalizeTargetId = Int(t["id"]); node.ExternalizeTargetParam = Str(t["param"]); break; }
            if (o["rewire"] is JsonArray rewire)
              foreach (var r in rewire)
                if (r is JsonObject ro) g.Rewire.Add(new Rewire { From = Str(ro["from"]), FromPort = Int(ro["fromPort"]), To = Str(ro["to"]), ToPort = Int(ro["toPort"]) });
            if (o["rename"] is JsonArray rename)
              foreach (var r in rename)
                if (r is JsonObject rno) g.Rename.Add(new Rename { Id = Int(rno["id"]), Nick = Str(rno["nick"]) });

            groups.Add(g);
          }

        return groups;
      }
      catch { return null; }
    }

    /// <summary>Stage 7: the read-only ADVISORY insights (separate from the applyable change groups, so the
    /// existing <see cref="Parse"/> contract is untouched). Empty on any parse issue - advice is never load-bearing.</summary>
    public static List<Advice> ParseAdvice(string json)
    {
      var advice = new List<Advice>();
      try
      {
        if (!(JsonNode.Parse(json) is JsonObject root) || !(root["advice"] is JsonArray arr)) return advice;
        foreach (var a in arr)
          if (a is JsonObject o)
          {
            var obs = Str(o["observation"]); var sug = Str(o["suggestion"]);
            if (!string.IsNullOrWhiteSpace(obs) || !string.IsNullOrWhiteSpace(sug))
              advice.Add(new Advice { Observation = obs, Suggestion = sug });
          }
      }
      catch { }
      return advice;
    }

    private static bool IsSlider(string type) => (type ?? "").Trim().Equals("Number Slider", System.StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<int> Ints(JsonNode n)
    {
      if (n is JsonArray a) foreach (var e in a) yield return Int(e);
    }
    private static string Str(JsonNode n) { try { return n?.GetValue<string>(); } catch { return null; } }
    private static bool Bool(JsonNode n) { try { return n != null && n.GetValue<bool>(); } catch { return false; } }
    private static int Int(JsonNode n)
    {
      if (n == null) return 0;
      try { return n.GetValue<int>(); }
      catch { try { return (int)n.GetValue<double>(); } catch { return 0; } }
    }
  }
}
