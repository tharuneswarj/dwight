using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace Dwight.Agent
{
  /// <summary>One component the spec asks for. <see cref="Ref"/> is a spec-local label, not an id -
  /// the plugin assigns real identity, so the model can never name a guid or an id it invented.</summary>
  internal sealed class BuildComponent
  {
    public string Ref { get; set; }
    public string Type { get; set; }
    public string Nick { get; set; }

    /// <summary>The initial state this component is born with (#377), or null for the types that have
    /// none. Own-ledger only by construction: a <see cref="BuildComponent"/> is a NEW object, so there is
    /// no representable way to reach the user's existing components from this field.</summary>
    public BuildState State { get; set; }

    /// <summary>
    /// U19 (#898): the COMPLETE source of a script component this build places, or null for every other component
    /// (and for a script left empty). Normalised once at the parse, so the text on the card, in the record and set
    /// on the component are the same bytes (#153). Own-ledger only, like <see cref="State"/>: a
    /// <see cref="BuildComponent"/> is a NEW object, so there is no representable way to write into a script the
    /// user already owns from here - that is <c>edit_script</c>'s consent class, not a field on a build.
    /// </summary>
    public string Source { get; set; }

    /// <summary>Input names APPENDED after the script component's defaults, in order (as edit_script appends).</summary>
    public List<string> Inputs { get; set; } = new List<string>();

    /// <summary>Output names APPENDED after the script component's defaults, in order.</summary>
    public List<string> Outputs { get; set; } = new List<string>();

    /// <summary>Brief CP: each declared input whose access is not item, by name (<see cref="PortAccess"/>).</summary>
    public Dictionary<string, string> InputAccess { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The script's language ("python" / "csharp"), DERIVED from <see cref="Type"/> and never stored beside it,
    /// or null when this is not a script type. Derived per call so canonicalising <see cref="Type"/> (the spot's record)
    /// cannot leave it describing the model's spelling.</summary>
    public string Language => BuildScriptTypes.LanguageOf(Type);

    /// <summary>U88: for a C# script, the lines the normaliser put before the model's first line (compiled line - shift = the
    /// model's line). Zero for Python and for a class that passed through untouched.</summary>
    public int LineShift { get; set; }

    /// <summary>U88: the RunScript signature the component really generated for this C# script (what its input hints made each
    /// parameter), set when the hints are chosen; shown on a compile failure. Null for Python and before the hints are chosen.</summary>
    public string GeneratedSignature { get; set; }

    /// <summary>U88: the sentences for parameter types of the model's signature that map to no hint.</summary>
    public List<string> HintNotes { get; set; } = new List<string>();

    /// <summary>Does this component carry source the user must see before it runs?</summary>
    public bool CarriesSource => !string.IsNullOrEmpty(Source);
  }

  /// <summary>One wire. Each end is either a spec-local <c>ref</c> (a component being built) or an
  /// existing component's registry <c>id</c> - never both, never neither.</summary>
  internal sealed class BuildWire
  {
    public string FromRef { get; set; }
    public int? FromId { get; set; }
    public string FromParam { get; set; }
    public string ToRef { get; set; }
    public int? ToId { get; set; }
    public string ToParam { get; set; }
  }

  /// <summary>
  /// One declared RECONNECTION (#625): which NEW output feeds a wire a rebuild cuts. The model names the consumer
  /// by id and the new output by ref; the plugin supplies the input param and the position from its own cut record
  /// (<see cref="CutWire"/>). A separate declaration from <see cref="BuildWire"/> because it is a separate
  /// operation: a wire APPENDS a source, a reconnection REPLACES a cut one IN PLACE.
  /// </summary>
  internal sealed class BuildReconnect
  {
    public int ToId { get; set; }
    public string ToParam { get; set; }

    /// <summary>Which cut wire on that input, by its position - required only when the input carries more than one.</summary>
    public int? At { get; set; }

    public string FromRef { get; set; }
    public string FromParam { get; set; }
  }

  internal sealed class BuildSpec
  {
    public List<BuildComponent> Components { get; } = new List<BuildComponent>();
    public List<BuildWire> Wires { get; } = new List<BuildWire>();

    /// <summary>U73: the scripts the gate rewrote to the approved card's text or port order, with where the model's text
    /// differed. Set by the gate, read by the verdict - never part of what the model sends.</summary>
    public List<ScriptSubstitution> CardScripts { get; set; } = new List<ScriptSubstitution>();

    /// <summary>Declared reconnections for the wires a rebuild cuts (#625). Empty for an ordinary build.</summary>
    public List<BuildReconnect> Reconnects { get; } = new List<BuildReconnect>();

    /// <summary>
    /// The scaffold region ADDRESS this build targets, or null (WP-4 Stage B).
    ///
    /// Optional by ruling (#354 Q3): extend-build does not presuppose a map, so a build with no region
    /// stays legal - and it has no per-region attempt cap, because there is no region to key one to. The
    /// turn's step ceiling is what bounds that case, and the report says so rather than implying a cap
    /// that is not there.
    /// </summary>
    public string Region { get; set; }
  }

  /// <summary>The port names on one end of a wire, as the live graph reports them. Empty lists mean a
  /// PARAM (Panel, Relay, Number, a floating Curve): it has one implicit port and no name to give it.
  /// A null <see cref="PortShape"/> means "not known", which is never treated as a rule.</summary>
  internal sealed class PortShape
  {
    public IReadOnlyList<string> Inputs { get; set; }
    public IReadOnlyList<string> Outputs { get; set; }

    /// <summary>
    /// U41 (#975): the FULL port names, parallel to <see cref="Inputs"/>/<see cref="Outputs"/> (which hold the
    /// canvas LABEL - the nickname, else the name). A Circle's input is labelled "R" and named "Radius", and
    /// <c>get_component</c> presents the name; a wire that asked for "Radius" was refused with ["P","R"]. Null =
    /// the labels are all there is (a test fixture, an unknown shape).
    /// </summary>
    public IReadOnlyList<string> InputNames { get; set; }
    public IReadOnlyList<string> OutputNames { get; set; }
  }

  /// <summary>
  /// Parse and VALIDATE a build spec before anything touches the canvas (the validate-before-instantiate
  /// guardrail, ADR-010). GH-free, so every refusal is unit-tested.
  ///
  /// The two hallucination doors this closes (ADR-034/058):
  /// - a component TYPE no installed plugin provides - refused with the nearest installed names, so the
  ///   refusal is a repair instruction rather than a wall;
  /// - an ID the model invented, or one whose object is gone - refused distinctly, because "you made that
  ///   up" and "that was deleted" call for different corrections.
  ///
  /// Everything is checked BEFORE the first AddObject. A spec that is half-valid must not half-build:
  /// the canvas is either untouched or it carries the whole declared change.
  ///
  /// THE THIRD DOOR, found at the Stage-D gate: a PORT that does not exist. The port name was previously
  /// demanded unconditionally on a wire's target and then IGNORED for param targets, while an unmatched
  /// name on a source silently fell back to output[0] - so a model could name a port that does not
  /// exist, be wired somewhere else entirely, and read a verdict saying the build "landed as specified".
  /// Ports are now resolved by ARITY (see <see cref="Port"/>): the plugin computes the answer wherever
  /// it is forced, and a name is required only where it is a genuine choice - where it must MATCH.
  /// </summary>
  internal static class BuildSpecParser
  {
    public const int MaxComponents = 40;

    /// <summary>Brief CU: the detail marker on a wire's end naming a port the component does not have, so the user's note can say so without reading a sentence written for the model.</summary>
    public const string NoSuchPortReason = "no_such_port";

    /// <summary>Brief CU: the same for a bare name more than one installed component carries.</summary>
    public const string AmbiguousTypeReason = "ambiguous_component_type";

    public static bool TryParse(JsonObject args, Func<string, bool> typeExists, Func<string, IReadOnlyList<string>> nearest,
      Func<int, IdState> idState, out BuildSpec spec, out ToolResult error)
      => TryParse(args, typeExists, nearest, idState, null, null, out spec, out error);

    public static bool TryParse(JsonObject args, Func<string, bool> typeExists, Func<string, IReadOnlyList<string>> nearest,
      Func<int, IdState> idState, Func<string, PortShape> portsOfType, Func<int, PortShape> portsOfId,
      out BuildSpec spec, out ToolResult error)
      => TryParse(args, typeExists, nearest, idState, portsOfType, portsOfId, null, out spec, out error);

    /// <param name="ambiguousForms">U25 (#915): the qualified forms a BARE type name could mean when more than one
    /// installed component shares it - refused with those forms, before <paramref name="typeExists"/> is asked. Null,
    /// or a null/empty result, is "not ambiguous" and the parse is unchanged.</param>
    public static bool TryParse(JsonObject args, Func<string, bool> typeExists, Func<string, IReadOnlyList<string>> nearest,
      Func<int, IdState> idState, Func<string, PortShape> portsOfType, Func<int, PortShape> portsOfId,
      Func<string, IReadOnlyList<string>> ambiguousForms,
      out BuildSpec spec, out ToolResult error)
    {
      spec = new BuildSpec();
      error = null;

      // The address is only SHAPE-checked here; whether it names a real group is the shim's question,
      // because only the canvas can answer it. A malformed address is refused now rather than carried
      // to a resolution step that would have to refuse it anyway (#208: exactly, or refuse).
      var region = args?["region"]?.GetValue<string>()?.Trim();
      if (!string.IsNullOrEmpty(region))
      {
        // BOTH KINDS (U18). A spot is echoed back exactly as a map region is: the plugin mints it, the
        // approved-turn fact names it, and the model repeats it - which is the same path "A1" already
        // takes. The model can no more invent a spot than a map address, because an address it was not
        // given is refused by the ledger before the canvas is touched.
        if (!ScaffoldAddress.IsRegionAddress(region))
        {
          error = ToolResult.Fail(ToolErrors.InvalidArgs,
            $"\"{region}\" is not a region address. Map regions look like A1, A2, B1 - get them from " +
            "get_scaffold_map. A spot Dwight has just been approved to make looks like \"Spot 1\", and " +
            "the approval names it.");
          return false;
        }
        spec.Region = region;
      }

      var comps = args?["components"] as JsonArray;
      if (comps == null || comps.Count == 0)
      {
        error = ToolResult.Fail(ToolErrors.InvalidArgs, "Requires a non-empty \"components\" array.");
        return false;
      }
      if (comps.Count > MaxComponents)
      {
        error = ToolResult.Fail(ToolErrors.PreconditionRefused,
          $"A single build_region places at most {MaxComponents} components; this asks for {comps.Count}. " +
          "Build the region in smaller pieces so each one can be verified.");
        return false;
      }

      var refs = new HashSet<string>(StringComparer.Ordinal);
      foreach (var c in comps)
      {
        var r = (c?["ref"]?.GetValue<string>() ?? "").Trim();
        var t = (c?["type"]?.GetValue<string>() ?? "").Trim();
        if (r.Length == 0 || t.Length == 0)
        {
          error = ToolResult.Fail(ToolErrors.InvalidArgs, "Every component needs a \"ref\" and a \"type\".");
          return false;
        }
        if (!refs.Add(r))
        {
          error = ToolResult.Fail(ToolErrors.InvalidArgs, $"Duplicate component ref \"{r}\".");
          return false;
        }
        // U25 (#915): A BARE NAME SHARED BY MORE THAN ONE INSTALLED COMPONENT IS REFUSED WITH THE QUALIFIED FORMS,
        // never resolved to whichever proxy Grasshopper enumerated first. "Rectangle" placed a Rectangle PARAM wired
        // from a plane and two numbers; no spelling of the bare name could reach the component, and the model told
        // the user it was not installed. #208 at the build surface: identity is resolved exactly, or refused.
        IReadOnlyList<string> forms = null;
        try { forms = ambiguousForms?.Invoke(t); } catch { forms = null; }
        if (forms != null && forms.Count > 1)
        {
          error = ToolResult.Fail(ToolErrors.PreconditionRefused,
            $"\"{t}\" names {forms.Count} installed components, so it does not say which one to place. Give the " +
            "qualified form in \"type\" instead: " + string.Join(" or ", forms.Select(f => "\"" + f + "\"")) + ".",
            new JsonObject
            {
              ["asked"] = t,
              ["reason"] = AmbiguousTypeReason,
              ["qualifiedForms"] = new JsonArray(forms.Select(f => (JsonNode)f).ToArray()),
              ["cause"] = "more than one installed component carries that bare name, and placing whichever was " +
                          "enumerated first would be identity resolved by accident",
            });
          return false;
        }
        if (!typeExists(t))
        {
          // ADR-034 at the boundary, with ADR-092's shape: hand back the ground truth.
          var near = nearest?.Invoke(t) ?? new List<string>();
          error = ToolResult.Fail(ToolErrors.UnknownComponentType,
            $"No installed component is named \"{t}\".",
            new JsonObject
            {
              ["asked"] = t,
              ["nearestInstalled"] = new JsonArray(near.Select(n => (JsonNode)n).ToArray()),
            });
          return false;
        }
        // #377: the initial state, validated here with the type already known - so an off-grid value or
        // an inverted domain is refused BEFORE the first AddObject, exactly like an unknown type. Every
        // one of those refusals exists because Grasshopper's own response is to accept it and be wrong.
        // U30: in the BUILD scope, so a Number, Integer or Text param may be born holding a constant.
        if (!BuildStateParser.TryParse(c["state"], t, r, StateScope.Build, out var state, out error)) return false;

        // U19 (#898): a script's source and appended ports. Decided on the CANONICAL type (U40), after the type is
        // known to exist - so a `source` on a Slider is refused BY NAME with the two types that take one, and the
        // language is the type's, never a field the model could set against it.
        if (!BuildScriptParser.TryParse(c, t, r, out var source, out var scriptInputs, out var scriptOutputs, out var scriptShift, out var scriptAccess, out error)) return false;

        spec.Components.Add(new BuildComponent
        {
          Ref = r, Type = t, Nick = c["nick"]?.GetValue<string>(), State = state,
          Source = source, Inputs = scriptInputs, Outputs = scriptOutputs, InputAccess = scriptAccess, LineShift = scriptShift,
        });
      }

      var wires = args?["wires"] as JsonArray ?? new JsonArray();
      foreach (var w in wires)
      {
        var wire = new BuildWire
        {
          FromParam = w?["from"]?["param"]?.GetValue<string>(),
          ToParam = w?["to"]?["param"]?.GetValue<string>(),
        };
        if (!ReadEnd(w?["from"] as JsonObject, refs, idState, "from", out var fRef, out var fId, out error)) return false;
        if (!ReadEnd(w?["to"] as JsonObject, refs, idState, "to", out var tRef, out var tId, out error)) return false;
        wire.FromRef = fRef; wire.FromId = fId; wire.ToRef = tRef; wire.ToId = tId;

        var fromShape = Shape(fRef, fId, spec, portsOfType, portsOfId);
        var toShape = Shape(tRef, tId, spec, portsOfType, portsOfId);
        if (!Port(fromShape?.Outputs, fromShape?.OutputNames, wire.FromParam, "from", "output", out var fromPort, out error)) return false;
        if (!Port(toShape?.Inputs, toShape?.InputNames, wire.ToParam, "to", "input", out var toPort, out error)) return false;
        wire.FromParam = fromPort;
        wire.ToParam = toPort;

        spec.Wires.Add(wire);
      }

      // #625: declared reconnections. SHAPE and PORTS only - whether each names a wire the rebuild really cuts is
      // RebuildBoundary's question, because only the canvas knows what the removal cuts.
      var reconnects = args?["reconnect"] as JsonArray ?? new JsonArray();
      foreach (var r in reconnects)
      {
        var to = r?["to"] as JsonObject;
        var from = r?["from"] as JsonObject;
        if (to == null || from == null)
        {
          error = ToolResult.Fail(ToolErrors.InvalidArgs, "Every reconnection needs a \"to\" and a \"from\".");
          return false;
        }
        if (to["ref"] != null)
        {
          error = ToolResult.Fail(ToolErrors.InvalidArgs,
            "A reconnection's \"to\" is the EXISTING component the rebuild cuts a wire into - give its \"id\", not a \"ref\".");
          return false;
        }
        if (from["id"] != null)
        {
          error = ToolResult.Fail(ToolErrors.InvalidArgs,
            "A reconnection's \"from\" must be a component this build places - give its \"ref\". Two existing " +
            "components are never wired to each other.");
          return false;
        }
        if (!ReadEnd(to, refs, idState, "to", out _, out var toId, out error)) return false;
        if (!ReadEnd(from, refs, idState, "from", out var fromRef, out _, out error)) return false;

        int? at = null;
        if (to["at"] != null)
        {
          try { at = to["at"].GetValue<int>(); } catch { at = -1; }
          if (at < 0)
          {
            error = ToolResult.Fail(ToolErrors.InvalidArgs, "A reconnection's \"at\" must be a whole number, 0 or more.");
            return false;
          }
        }

        var reconnect = new BuildReconnect
        {
          ToId = toId.Value, At = at, FromRef = fromRef,
          ToParam = to["param"]?.GetValue<string>(), FromParam = from["param"]?.GetValue<string>(),
        };
        if (!Port(Shape(null, toId, spec, portsOfType, portsOfId)?.Inputs, Shape(null, toId, spec, portsOfType, portsOfId)?.InputNames, reconnect.ToParam, "to", "input",
              out var toPort, out error)) return false;
        if (!Port(Shape(fromRef, null, spec, portsOfType, portsOfId)?.Outputs, Shape(fromRef, null, spec, portsOfType, portsOfId)?.OutputNames, reconnect.FromParam, "from", "output",
              out var fromPort, out error)) return false;
        reconnect.ToParam = toPort;
        reconnect.FromParam = fromPort;

        spec.Reconnects.Add(reconnect);
      }
      return true;
    }

    /// <summary>
    /// Which port one end of a wire lands on. THE PLUGIN DECIDES WHEREVER THE ANSWER IS FORCED
    /// (ADR-056): a param has one implicit port and no name to give it, and a component with exactly one
    /// port on that side has only one possible answer - asking the model to supply either is asking it
    /// for structure the plugin already knows, and inviting it to invent a name that would then be
    /// silently ignored.
    ///
    /// A name is required only where it is a GENUINE CHOICE, and there it must MATCH: the refusal
    /// carries the real port list, so it repairs rather than blocks (ADR-092's shape).
    ///
    /// Unknown ports (a null list) are not a rule. Fail open on KNOWLEDGE - never on safety: an
    /// unresolvable port still fails later, loudly, with the canvas untouched.
    /// </summary>
    internal static bool Port(IReadOnlyList<string> names, IReadOnlyList<string> fullNames, string given, string side, string kind,
      out string resolved, out ToolResult error)
    {
      resolved = given;
      error = null;

      if (names == null) return true;                          // not known - do not invent a rule
      if (names.Count == 0) { resolved = null; return true; }  // a param: one implicit port, unnamed
      if (names.Count == 1) { resolved = names[0]; return true; }

      if (string.IsNullOrWhiteSpace(given))
      {
        error = ToolResult.Fail(ToolErrors.InvalidArgs,
          $"This wire's \"{side}\" end has {names.Count} {kind}s, so it needs a \"param\" saying which one.",
          new JsonObject { ["available"] = Available(names, fullNames) });
        return false;
      }

      // U41 (#975): an exact NAME or an exact NICKNAME, nothing looser. The model reads names from get_component
      // ("Radius") and the canvas labels ports by nickname ("R"); refusing the one the plugin itself presented
      // was a refusal that was not true. If the text matches more than one port - by either form - the choice is
      // not forced and is refused (#208): no first-wins, no fallback.
      var hits = new List<int>();
      for (var i = 0; i < names.Count; i++)
      {
        var full = fullNames != null && i < fullNames.Count ? fullNames[i] : null;
        if (string.Equals(names[i], given, StringComparison.OrdinalIgnoreCase) ||
            (full != null && string.Equals(full, given, StringComparison.OrdinalIgnoreCase)) ||
            // U62: the spelling kb_lookup shows, "Radius (R)", and the one a refusal lists, "R (Radius)" - one port's own two
            // names together, so a model that copies what it was shown is not refused for obeying it.
            (full != null && (string.Equals(full + " (" + names[i] + ")", given, StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(names[i] + " (" + full + ")", given, StringComparison.OrdinalIgnoreCase))))
          hits.Add(i);
      }
      if (hits.Count > 1)
      {
        error = ToolResult.Fail(ToolErrors.InvalidArgs,
          $"\"{given}\" matches {hits.Count} {kind}s on this wire's \"{side}\" end, so it does not say which one.",
          new JsonObject { ["asked"] = given, ["available"] = Available(names, fullNames) });
        return false;
      }
      var match = hits.Count == 1 ? names[hits[0]] : null;
      if (match == null)
      {
        error = ToolResult.Fail(ToolErrors.InvalidArgs,
          $"No {kind} named \"{given}\" on this wire's \"{side}\" end.",
          new JsonObject { ["asked"] = given, ["reason"] = NoSuchPortReason, ["available"] = Available(names, fullNames) });
        return false;
      }

      resolved = match;   // the canvas's own spelling, not the model's
      return true;
    }

    private static JsonArray Names(IReadOnlyList<string> names) =>
      new JsonArray(names.Select(n => (JsonNode)n).ToArray());

    /// <summary>BOTH forms where they differ - "R (Radius)" - so the repair names what the model can say.</summary>
    private static JsonArray Available(IReadOnlyList<string> names, IReadOnlyList<string> fullNames) =>
      new JsonArray(names.Select((n, i) =>
      {
        var full = fullNames != null && i < fullNames.Count ? fullNames[i] : null;
        return (JsonNode)(string.IsNullOrWhiteSpace(full) || string.Equals(full, n, StringComparison.Ordinal) ? n : n + " (" + full + ")");
      }).ToArray());

    /// <summary>The ports on one wire end - looked up by TYPE for a component this spec is building, or
    /// by registry ID for one already on the canvas.</summary>
    private static PortShape Shape(string byRef, int? byId, BuildSpec spec,
      Func<string, PortShape> portsOfType, Func<int, PortShape> portsOfId)
    {
      try
      {
        if (!string.IsNullOrEmpty(byRef))
        {
          var comp = spec.Components.FirstOrDefault(c => string.Equals(c.Ref, byRef, StringComparison.Ordinal));
          if (comp?.Type == null) return null;
          return WithAppended(portsOfType?.Invoke(comp.Type), comp);
        }
        if (byId.HasValue) return portsOfId?.Invoke(byId.Value);
      }
      catch { }
      return null;
    }

    /// <summary>
    /// U19: A SCRIPT'S SHAPE IS ITS TYPE'S PORTS PLUS THE ONES THIS SPEC APPENDS. Without this a wire into (or out of) an
    /// input the spec itself declared was refused "no input named offset" - the type has x and y, and the appended port
    /// exists only in the declaration. Null stays null (not known is never a rule); a component that appends nothing is
    /// returned as it was, so every other type's port resolution is byte-for-byte what it was.
    /// </summary>
    private static PortShape WithAppended(PortShape shape, BuildComponent comp)
    {
      if (shape == null || comp == null) return shape;
      var inputs = comp.Inputs ?? new List<string>();
      var outputs = comp.Outputs ?? new List<string>();
      if (inputs.Count == 0 && outputs.Count == 0) return shape;

      // U78: a side the script declares REPLACES the born-with ports on that side (x, y / a); the build removes them. The
      // standard "out" message output is not a default and stays.
      var keepIn = Keep(shape.Inputs, shape.InputNames, inputs.Count > 0 ? BuildScriptTypes.DefaultInputs : null);
      var keepOut = Keep(shape.Outputs, shape.OutputNames, outputs.Count > 0 ? BuildScriptTypes.DefaultOutputs : null);
      return new PortShape
      {
        Inputs = keepIn.Item1.Concat(inputs).ToList(),
        Outputs = keepOut.Item1.Concat(outputs).ToList(),
        InputNames = keepIn.Item2?.Concat(inputs).ToList(),
        OutputNames = keepOut.Item2?.Concat(outputs).ToList(),
      };
    }

    /// <summary>The labels (and the parallel full names) that remain once <paramref name="drop"/> is removed; nothing is
    /// removed when it is null.</summary>
    private static Tuple<List<string>, List<string>> Keep(IReadOnlyList<string> labels, IReadOnlyList<string> names, IReadOnlyList<string> drop)
    {
      var l = (labels ?? new string[0]).ToList();
      if (drop == null) return Tuple.Create(l, names?.ToList());
      var keepIdx = Enumerable.Range(0, l.Count)
        .Where(i => !drop.Any(d => string.Equals(d, l[i], StringComparison.OrdinalIgnoreCase)
                                || (names != null && i < names.Count && string.Equals(d, names[i], StringComparison.OrdinalIgnoreCase))))
        .ToList();
      return Tuple.Create(keepIdx.Select(i => l[i]).ToList(),
        names == null ? null : keepIdx.Where(i => i < names.Count).Select(i => names[i]).ToList());
    }

    private static bool ReadEnd(JsonObject end, HashSet<string> refs, Func<int, IdState> idState, string side,
      out string endRef, out int? endId, out ToolResult error)
    {
      endRef = null; endId = null; error = null;
      if (end == null)
      {
        error = ToolResult.Fail(ToolErrors.InvalidArgs, $"Every wire needs a \"{side}\" end.");
        return false;
      }

      var r = end["ref"]?.GetValue<string>();
      var hasId = end["id"] != null;

      if (!string.IsNullOrWhiteSpace(r) && hasId)
      {
        error = ToolResult.Fail(ToolErrors.InvalidArgs,
          $"A wire's \"{side}\" end takes either \"ref\" (a component being built) or \"id\" (an existing one), not both.");
        return false;
      }

      if (!string.IsNullOrWhiteSpace(r))
      {
        if (!refs.Contains(r))
        {
          error = ToolResult.Fail(ToolErrors.InvalidArgs,
            $"Wire {side} references \"{r}\", which is not a component in this spec.");
          return false;
        }
        endRef = r;
        return true;
      }

      if (!hasId)
      {
        error = ToolResult.Fail(ToolErrors.InvalidArgs, $"A wire's \"{side}\" end needs a \"ref\" or an \"id\".");
        return false;
      }

      int id;
      try { id = end["id"].GetValue<int>(); }
      catch
      {
        error = ToolResult.Fail(ToolErrors.InvalidArgs, $"Wire {side} \"id\" must be an integer.");
        return false;
      }

      // Groundedness at the write boundary (#191): the two failures are distinguished, because the
      // corrections differ.
      var state = idState(id);
      if (state == IdState.Unknown)
      {
        error = ToolResult.Fail(ToolErrors.UnknownId,
          $"No component with id {id} exists. Ids come from this session's reads - do not invent them.",
          new JsonObject { ["id"] = id });
        return false;
      }
      if (state == IdState.Retired)
      {
        error = ToolResult.Fail(ToolErrors.RetiredId,
          $"Component {id} is no longer on the canvas, so nothing can be wired to it.",
          new JsonObject { ["id"] = id });
        return false;
      }

      endId = id;
      return true;
    }
  }

  /// <summary>One component a rebuild KEEPS - Dwight placed it and the user has edited it since (#631's as-left rule),
  /// so the card promised to LEAVE it, or it was placed here earlier by name. As the plugin knows it: its agent id, type and nickname.</summary>
  internal sealed class KeptComponent
  {
    public int Id { get; set; }
    public string Type { get; set; }
    public string Nick { get; set; }

    /// <summary>Kept because it was placed earlier, not because anyone edited it, so nothing may say it was edited.</summary>
    public bool PlacedEarlier { get; set; }
  }

  /// <summary>
  /// A REBUILD NEVER DUPLICATES A KEPT COMPONENT (U33, #922).
  ///
  /// THE DEFECT. A2 had to be rebuilt; the card read "remove the 2 components it placed there... and LEAVE the 2 you have
  /// edited since" - the user had bound two params (Site, Terrain) to Rhino geometry, so the as-left rule kept them.
  /// Then the spec placed two MORE Site/Terrain params and wired the new offset to the empty pair. Two Sites, two
  /// Terrains, the bound pair orphaned, the footprint empty - and the model told the user "your two manual bindings
  /// were lost". False: they were duplicated, intact, beside the new ones.
  ///
  /// SO A SPEC THAT PLACES A COMPONENT OF THE SAME NICKNAME AND TYPE AS A KEPT ONE IS REFUSED, naming the kept one by
  /// id, before anything is removed - a validation refusal, so no attempt is spent. REFUSAL, NOT BINDING: binding the
  /// spec's ref to the kept component would make the spec's state and its wires INTO that ref writes to a component
  /// the user has adopted - a different consent class from placing (#338), and one the card did not name. The model
  /// wires FROM the kept id instead, which the refusal says in so many words.
  ///
  /// THE MATCH IS NICKNAME AND TYPE, BOTH. A nickname that is just the type's own name identifies nothing (every Panel
  /// is "Panel"), so such a kept component is not a duplicate of a spec Panel - that is the stated bound, chosen so a
  /// user's adopted default-named Panel does not block adding a second. A qualified type ("Params > Geometry > Curve")
  /// matches by its bare name. GH-free (ADR-091): the shim hands in what the canvas holds.
  /// </summary>
  internal static class RebuildDuplicates
  {
    public const string Reason = "duplicates_kept";

    /// <summary>Kept components named on the refusal before the rest are counted (ADR-035).</summary>
    public const int MaxNamed = 5;

    /// <summary>The refusal, or null when the spec duplicates nothing kept.</summary>
    public static ToolResult Check(BuildSpec spec, IReadOnlyList<KeptComponent> kept, string region)
    {
      if (spec == null || kept == null || kept.Count == 0) return null;

      var duplicates = new List<(BuildComponent Spec, KeptComponent Kept)>();
      foreach (var c in spec.Components)
      {
        if (c == null || string.IsNullOrWhiteSpace(c.Type)) continue;
        var type = Dwight.Components.ComponentMatchCore.Normalize(BareName(c.Type));
        var nick = (string.IsNullOrWhiteSpace(c.Nick) ? BareName(c.Type) : c.Nick).Trim();

        foreach (var k in kept)
        {
          if (k == null || string.IsNullOrWhiteSpace(k.Type) || string.IsNullOrWhiteSpace(k.Nick)) continue;
          var keptType = Dwight.Components.ComponentMatchCore.Normalize(k.Type);
          // A default nickname is not a nickname: it identifies nothing.
          if (Dwight.Components.ComponentMatchCore.Normalize(k.Nick) == keptType) continue;
          if (type != keptType) continue;
          if (!string.Equals(nick, k.Nick.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
          duplicates.Add((c, k));
          break;
        }
      }
      if (duplicates.Count == 0) return null;

      var keptLabels = duplicates.Select(d => Label(d.Kept)).ToList();
      var placing = duplicates.Select(d => "another " + BareName(d.Spec.Type) + " \"" + (d.Spec.Nick ?? BareName(d.Spec.Type)).Trim() +
                                           "\" (ref \"" + d.Spec.Ref + "\")").ToList();
      var ids = duplicates.Select(d => "id " + d.Kept.Id).ToList();
      var where = string.IsNullOrEmpty(region) ? "This rebuild" : "This rebuild of " + region;
      var plural = duplicates.Count > 1;
      var earlier = duplicates.Count(d => d.Kept.PlacedEarlier);
      var whatKept = earlier == 0 ? (plural ? "components" : "a component") + " Dwight placed that " + (plural ? "have" : "has") + " been edited since"
        : earlier == duplicates.Count ? (plural ? "components" : "a component") + " Dwight placed earlier"
        : "components Dwight placed earlier, some of them edited since";

      var message = where + " keeps " + Bounded(keptLabels) + " - " + whatKept + ", so " + (plural ? "they stay" : "it stays") +
                    " exactly as " + (plural ? "they are" : "it is") + ". The spec would place " + Bounded(placing) + " beside " +
                    (plural ? "them" : "it") + ", leaving the kept " + (plural ? "ones" : "one") + " orphaned and the user with two of each. " +
                    "Nothing was built. Drop " + (plural ? "those" : "that") + " from the spec and wire from " + Bounded(ids) + " instead.";

      return ToolResult.Fail(ToolErrors.PreconditionRefused, message, new JsonObject
      {
        ["region"] = region ?? "",
        ["reason"] = Reason,
        ["duplicates"] = new JsonArray(duplicates.Select(d => (JsonNode)new JsonObject
        {
          ["ref"] = d.Spec.Ref ?? "",
          ["type"] = BareName(d.Spec.Type),
          ["nick"] = (d.Spec.Nick ?? BareName(d.Spec.Type)).Trim(),
          ["keptId"] = d.Kept.Id,
        }).ToArray()),
        ["cause"] = "the as-left rule keeps components the user has edited since Dwight placed them, and a second copy " +
                    "of one would orphan it rather than replace it",
      });
    }

    /// <summary>"Curve \"Site\" (41)".</summary>
    private static string Label(KeptComponent k) => k.Type + " \"" + k.Nick.Trim() + "\" (" + k.Id + ")";

    /// <summary>The bare name of a possibly qualified type ("Params > Geometry > Curve" is a Curve).</summary>
    private static string BareName(string type)
    {
      var t = (type ?? "").Trim();
      var at = t.LastIndexOf(Dwight.Components.ComponentMatchCore.QualifiedSeparator.Trim(), StringComparison.Ordinal);
      return at < 0 ? t : t.Substring(at + 1).Trim();
    }

    private static string Bounded(IReadOnlyList<string> items) =>
      items.Count <= MaxNamed
        ? string.Join(", ", items)
        : string.Join(", ", items.Take(MaxNamed)) + ", and " + (items.Count - MaxNamed) + " more";
  }
}
