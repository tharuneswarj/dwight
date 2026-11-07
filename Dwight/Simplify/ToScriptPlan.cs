using System;
using System.Collections.Generic;
using System.Linq;

namespace Dwight.Simplify
{
  /// <summary>ONE script param, fully plugin-computed except its NAME. <see cref="Ext"/> is the boundary-crossing
  /// index from <see cref="Dwight.Data.SimplifyBoundary"/> - the SAME canonical ordering the payload showed the
  /// model and the resolve builder resolves against - so the 1:1 correspondence used to compare the script's
  /// outputs against the originals' is structural, never a re-derived guess.</summary>
  internal sealed class ScriptParam
  {
    public int Ext;                  // boundary crossing index (canonical order)
    public string GhType;            // the live IGH_Param.TypeName at that crossing
    public ScriptAccess Access;      // derived from the crossing's tree signature
    public string Name;              // the model's chosen name (semantics)
    public string Variable;          // the sanitised identifier the code actually uses (structure)

    // RR19/RR20 #40 + #50 - the port's MEANING, all read from the live SOLVED graph (so it cannot be wrong).
    // Without these the model is told the interface's SHAPE and never its MEANING, so it GUESSES: told only
    // "input0: double", it decided a double next to a Linear Array must be the count, and wrote (int)0.75 = 0.
    /// <summary>What this port connects to, named: "Unit Z (35).Factor", "Linear Array (48).Geometry".</summary>
    public string Connects;
    /// <summary>The CONCRETE runtime type on the wire: not "Curve" but "closed planar PolylineCurve, 4 segments".</summary>
    public string Runtime;
    /// <summary>The VALUE, when this port carries a constant (a slider 0.75, a panel, an internalized point).
    /// Null when it is genuinely computed upstream. This single field is what the Linear-Array case needed.</summary>
    public string Constant;
  }

  /// <summary>The script component's whole interface: arity, order, types and access, all derived from the live
  /// boundary contract. The model is TOLD this and writes code against it; it cannot change it.</summary>
  internal sealed class ScriptInterface
  {
    public List<ScriptParam> Inputs = new List<ScriptParam>();
    public List<ScriptParam> Outputs = new List<ScriptParam>();
  }

  /// <summary>What the model returns: the language, the source, and a NAME per param. Nothing structural.</summary>
  internal sealed class ToScriptProposal
  {
    public ScriptLanguage Language;
    public string Source;
    public string Summary;
    public List<string> InputNames = new List<string>();
    public List<string> OutputNames = new List<string>();
  }

  /// <summary>GH-free parse of the model's to_script reply. Anything missing or malformed yields null, which the
  /// validator turns into an honest failure - never a half-built interface.</summary>
  internal static class ToScriptParser
  {
    public static ToScriptProposal Parse(string json, ScriptLanguage lang)
    {
      try
      {
        var root = System.Text.Json.Nodes.JsonNode.Parse(json) as System.Text.Json.Nodes.JsonObject;
        if (root == null) return null;
        // #141/#146: NORMALIZE THE USINGS AT BIRTH. The script component supplies none (instrumented), and the model
        // declares them unreliably - so the plugin unions its baseline set in here, at the ONE point every path
        // shares (product loop, eval arm, attempt 1 and every retry). Normalising here rather than at the compile
        // gate keeps a single source of truth: the CONSENT SURFACE shows exactly the text that will be compiled,
        // accepted, and re-verified - a gate that tested different text than the accept applies would be a divergence.
        var p = new ToScriptProposal
        {
          Language = lang,
          Source = UsingNormalizer.Normalize(root["source"]?.GetValue<string>(), lang),
        };
        foreach (var n in root["inputs"] as System.Text.Json.Nodes.JsonArray ?? new System.Text.Json.Nodes.JsonArray())
          p.InputNames.Add(n?.ToString() ?? "");
        foreach (var n in root["outputs"] as System.Text.Json.Nodes.JsonArray ?? new System.Text.Json.Nodes.JsonArray())
          p.OutputNames.Add(n?.ToString() ?? "");
        p.Summary = root["summary"]?.GetValue<string>();
        return p;
      }
      catch { return null; }
    }
  }

  /// <summary>
  /// WP-4's GH-free validator (ADR-091) + the naming pass. The contract it enforces is the phase's hardest-won
  /// lesson (RR7 through RR14): STRUCTURE IS THE PLUGIN'S. A proposal whose param counts disagree with the
  /// boundary contract is REJECTED - it does not get "repaired" into whatever the model meant - because a
  /// silently-reshaped interface is exactly how a script ends up wired to the wrong port. The one repair
  /// round-trip gets the honest reason; a second failure is a clean, canvas-untouched refusal.
  /// </summary>
  internal static class ToScriptValidator
  {
    public static string Validate(ToScriptProposal p, ScriptInterface iface)
    {
      if (p == null) return "the response was not a usable proposal";
      if (string.IsNullOrWhiteSpace(p.Source)) return "the proposal contained no source code";
      if (iface == null) return "the selection has no boundary contract to script against";

      // Arity is structure. It comes from the boundary crossings, so a mismatch means the model invented a port.
      if (p.InputNames.Count != iface.Inputs.Count)
        return $"the script must take exactly {iface.Inputs.Count} input(s) - one per value entering the selection - but {p.InputNames.Count} were named";
      if (p.OutputNames.Count != iface.Outputs.Count)
        return $"the script must produce exactly {iface.Outputs.Count} output(s) - one per value leaving the selection - but {p.OutputNames.Count} were named";
      return null;
    }

    /// <summary>Stamp the model's names onto the plugin's interface, sanitised and de-duplicated. Inputs and
    /// outputs share ONE identifier namespace, because they share one scope in the generated script.</summary>
    public static void ApplyNames(ToScriptProposal p, ScriptInterface iface)
    {
      var taken = new HashSet<string>(StringComparer.Ordinal);
      for (var i = 0; i < iface.Inputs.Count; i++)
      {
        var proposed = i < p.InputNames.Count ? p.InputNames[i] : null;
        iface.Inputs[i].Name = Pretty(proposed, "input", i);
        iface.Inputs[i].Variable = ScriptTypeMap.VariableName(proposed, i, taken);
      }
      for (var j = 0; j < iface.Outputs.Count; j++)
      {
        var proposed = j < p.OutputNames.Count ? p.OutputNames[j] : null;
        iface.Outputs[j].Name = Pretty(proposed, "output", j);
        iface.Outputs[j].Variable = ScriptTypeMap.VariableName(proposed, j, taken);
      }
    }

    private static string Pretty(string proposed, string kind, int i) =>
      string.IsNullOrWhiteSpace(proposed) ? $"{kind}{i + 1}" : proposed.Trim();

    /// <summary>The interface, rendered for the model's prompt: every param with the exact declared type and
    /// access it will receive, so the source is written against the real contract rather than a guess at it.</summary>
    public static string Describe(ScriptInterface iface, ScriptLanguage lang)
    {
      if (iface == null) return "";
      var sb = new System.Text.StringBuilder();
      sb.AppendLine("inputs (in order):");
      for (var i = 0; i < iface.Inputs.Count; i++) sb.Append(Line(i, iface.Inputs[i], lang));
      sb.AppendLine("outputs (in order):");
      for (var j = 0; j < iface.Outputs.Count; j++) sb.Append(Line(j, iface.Outputs[j], lang));
      return sb.ToString();
    }

    /// <summary>RR20 #40/#50: one port, with its MEANING - where it lands, what is concretely on the wire, and its
    /// value if it is a constant. A type alone is what let the model read a 0.75 Factor as a count.</summary>
    private static string Line(int i, ScriptParam p, ScriptLanguage lang)
    {
      var sb = new System.Text.StringBuilder();
      sb.Append($"  [{i}] {ScriptTypeMap.DeclaredType(p.GhType, lang, p.Access)}  (Grasshopper type: {p.GhType}, access: {p.Access.ToString().ToLowerInvariant()})");
      if (Dwight.Eval.EvalFlags.PortSemantics)   // RR20 #56: the before-run turns exactly this off, nothing else
      {
        if (!string.IsNullOrWhiteSpace(p.Connects)) sb.Append($"\n        -> {p.Connects}");
        if (!string.IsNullOrWhiteSpace(p.Runtime)) sb.Append($"\n        on the wire: {p.Runtime}");
        if (!string.IsNullOrWhiteSpace(p.Constant)) sb.Append($"\n        CONSTANT VALUE: {p.Constant}   (use exactly this)");
      }
      sb.AppendLine();
      return sb.ToString();
    }
  }
}
