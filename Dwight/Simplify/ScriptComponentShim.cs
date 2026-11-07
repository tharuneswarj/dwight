using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Grasshopper;
using Grasshopper.Kernel;
using Dwight.Components;

namespace Dwight.Simplify
{
  /// <summary>
  /// WP-4: the thin GH shim over Rhino 8's script components (<c>C# Script</c> / <c>Python 3 Script</c>, both
  /// <c>RhinoCodePluginGH</c>). That assembly is a Rhino plugin, not a referenceable SDK, so the language-specific
  /// members - <c>SetSource</c>, <c>VariableName</c>, <c>TypeHints.Select</c>, <c>AllowTreeAccess</c> - are reached
  /// by REFLECTION. Everything else (variable params, access, wiring) is plain Grasshopper SDK.
  ///
  /// The discipline that makes reflection safe here: <see cref="TryCreate"/> PROBES every member it will later
  /// need and fails BEFORE the caller has mutated anything. A Rhino build that renamed an API therefore produces
  /// an honest "Dwight can't drive the script component on this Rhino" notice - never a half-built component on
  /// the user's canvas. The whole API was instrumented live through the bridge, not inferred (the RR8/13/14
  /// lesson); the one finding that would have burned us: the type-hint names are LANGUAGE-SPECIFIC, and
  /// <c>Select</c> THROWS on an unknown name.
  /// </summary>
  internal static class ScriptComponentShim
  {
    private const string ScriptVariableParamType = "ScriptVariableParam";

    /// <summary>Create the script component for a language, whitelist-gated (ADR-034) and fully probed. Returns
    /// null with an honest reason if the component is absent or its API does not answer.</summary>
    public static IGH_Component TryCreate(ScriptLanguage lang, out string error)
    {
      error = null;
      var name = ScriptTypeMap.ProxyName(lang);
      var proxyGuid = ComponentWhitelist.GuidFor(name);   // guardrail 3: validate before instantiate
      if (proxyGuid == null || proxyGuid == Guid.Empty)
      {
        error = $"the \"{name}\" component isn't installed on this Rhino, so Dwight can't build the script.";
        return null;
      }

      IGH_Component comp = null;
      try { comp = Instances.ComponentServer.EmitObjectProxy(proxyGuid.Value)?.CreateInstance() as IGH_Component; } catch { }
      if (comp == null) { error = $"Dwight could not create the \"{name}\" component."; return null; }
      if (!(comp is IGH_VariableParameterComponent))
      { error = $"the \"{name}\" component on this Rhino does not support variable parameters."; return null; }

      // PROBE the reflected members now, while nothing has been touched.
      if (comp.GetType().GetMethod("SetSource", new[] { typeof(string) }) == null)
      { error = $"Dwight can't set the source on this Rhino's \"{name}\" component (its API has changed)."; return null; }
      var probe = comp.Params.Input.FirstOrDefault(p => IsScriptVariable(p));
      if (probe == null) { error = $"the \"{name}\" component exposed no script parameters."; return null; }
      if (probe.GetType().GetProperty("VariableName")?.CanWrite != true)
      { error = $"Dwight can't name the script parameters on this Rhino (the \"{name}\" API has changed)."; return null; }

      return comp;
    }

    public static bool IsScriptVariable(IGH_Param p) => p != null && p.GetType().Name == ScriptVariableParamType;

    /// <summary>The script component's OWN outputs, in order - excluding the standard <c>out</c> print stream
    /// (a Param_String the component always carries). Index j here is boundary output j.</summary>
    public static List<IGH_Param> ScriptOutputs(IGH_Component comp) =>
      comp?.Params.Output.Where(IsScriptVariable).ToList() ?? new List<IGH_Param>();

    /// <summary>
    /// Shape the component to the plugin-computed interface and load the source: grow/shrink the variable params
    /// to the exact arity, then name, type-hint and set the access of each. Returns false with a reason, having
    /// mutated only the (not-yet-added) component itself.
    /// </summary>
    public static bool Shape(IGH_Component comp, ScriptInterface iface, ScriptLanguage lang, string source, out string error)
    {
      error = null;
      try
      {
        var vp = (IGH_VariableParameterComponent)comp;

        // ---- inputs: exact arity, then per-param structure ------------------------------------------------
        while (comp.Params.Input.Count > iface.Inputs.Count)
          comp.Params.UnregisterInputParameter(comp.Params.Input[comp.Params.Input.Count - 1]);
        while (comp.Params.Input.Count < iface.Inputs.Count)
        {
          var np = vp.CreateParameter(GH_ParameterSide.Input, comp.Params.Input.Count);
          if (np == null) { error = "Dwight could not add an input to the script component."; return false; }
          comp.Params.RegisterInputParam(np, comp.Params.Input.Count);
        }
        for (var i = 0; i < iface.Inputs.Count; i++)
          Configure(comp.Params.Input[i], iface.Inputs[i], lang);

        // ---- outputs: the script's own, beside the standard "out" stream ----------------------------------
        while (ScriptOutputs(comp).Count > iface.Outputs.Count)
        {
          var last = ScriptOutputs(comp).Last();
          comp.Params.UnregisterOutputParameter(last);
        }
        while (ScriptOutputs(comp).Count < iface.Outputs.Count)
        {
          var np = vp.CreateParameter(GH_ParameterSide.Output, comp.Params.Output.Count);
          if (np == null) { error = "Dwight could not add an output to the script component."; return false; }
          comp.Params.RegisterOutputParam(np, comp.Params.Output.Count);
        }
        var outs = ScriptOutputs(comp);
        for (var j = 0; j < iface.Outputs.Count && j < outs.Count; j++)
          ConfigureOutput(outs[j], iface.Outputs[j], lang);

        // ---- the source ----------------------------------------------------------------------------------
        var setSource = comp.GetType().GetMethod("SetSource", new[] { typeof(string) });
        setSource.Invoke(comp, new object[] { source ?? "" });
        try { vp.VariableParameterMaintenance(); } catch { }
        SetMarshFlags(comp);   // U85: born as the editor's own component is, before AddObject
        return true;
      }
      catch (Exception ex)
      {
        error = "Dwight could not build the script component: " + ex.Message;
        return false;
      }
    }

    /// <summary>U85: set MarshInputs, MarshOutputs and MarshGuids true on a script component (guarded reflection, see
    /// <see cref="ScriptMarshFlags"/>). Both languages; for C# these are already set, so it changes nothing there. Call BEFORE
    /// AddObject, after the source is set. Never throws.</summary>
    public static IReadOnlyList<string> SetMarshFlags(IGH_Component comp) => ScriptMarshFlags.Set(comp);

    /// <summary>Name + type-hint + access one script param. The hint is best-effort BY DESIGN: an unmapped GH type
    /// falls back to no hint (generic goo), which the model's code may still handle - and if it cannot, the
    /// compile error is surfaced before anything is spliced. <c>Select</c> THROWS on an unknown hint name (the
    /// live finding), so it is guarded rather than trusted.</summary>
    private static void Configure(IGH_Param p, ScriptParam spec, ScriptLanguage lang)
    {
      var t = p.GetType();
      try { p.Name = spec.Name; p.NickName = spec.Variable; } catch { }
      try { t.GetProperty("VariableName")?.SetValue(p, spec.Variable); } catch { }
      SetAccess(p, spec.Access);

      var hint = ScriptTypeMap.HintFor(spec.GhType, lang);
      if (hint == null) return;   // no hint: generic goo, which is a legitimate contract
      TrySelectHint(p, hint);
    }

    /// <summary>The one way a script param's access is set, for to-script and for a declared input (Brief CP). Tree needs the component's own flag as well. Never throws: the caller reads the result back.</summary>
    private static void SetAccess(IGH_Param p, ScriptAccess access)
    {
      try { p.Access = ParamAccessOf(access); } catch { }
      if (access == ScriptAccess.Tree)
        try { p.GetType().GetProperty("AllowTreeAccess")?.SetValue(p, true); } catch { }
    }

    private static GH_ParamAccess ParamAccessOf(ScriptAccess access) =>
      access == ScriptAccess.Item ? GH_ParamAccess.item : access == ScriptAccess.List ? GH_ParamAccess.list : GH_ParamAccess.tree;

    /// <summary>
    /// Brief CP: the access a script declares on one input, set and READ BACK. "Placed and wired as declared" now includes it, so an input that did not take it is a failed build and never a
    /// quiet item. Matched by the declared name. It may be called again after a type hint is selected, because selecting one can replace the parameter (U77).
    /// </summary>
    public static bool SetInputAccess(IGH_Component comp, string name, ScriptAccess access, out string error)
    {
      error = null;
      var port = comp?.Params.Input.FirstOrDefault(q => string.Equals(q.NickName, name, StringComparison.OrdinalIgnoreCase));
      if (port == null) { error = "the script has no input called \"" + name + "\" to set its access on"; return false; }
      SetAccess(port, access);
      if (port.Access == ParamAccessOf(access)) return true;
      error = "the input \"" + name + "\" did not take " + access.ToString().ToLowerInvariant() + " access";
      return false;
    }

    /// <summary>
    /// U84: THE ONE FUNCTION that configures a script component's OUTPUT param: name, access and type hint. to-script's
    /// <see cref="Shape"/> calls it for every output it builds (those pass a per-output verify), and a scaffold-built script's
    /// declared outputs go through <see cref="ConfigureDeclaredOutputs"/>, which calls it too, so the two paths cannot drift.
    /// to-script's answer is the ACCESS (and, where the type is known, the hint) set on the output param: a list-access Python
    /// output emits its list as items, where an item-access one wraps the whole list as a single item.
    /// </summary>
    internal static void ConfigureOutput(IGH_Param p, ScriptParam spec, ScriptLanguage lang) => Configure(p, spec, lang);

    /// <summary>
    /// U84: configure the DECLARED outputs of a scaffold-built script exactly as to-script configures its outputs, through
    /// <see cref="ConfigureOutput"/>. Matches by the declared name, runs on the not-yet-added component (selecting a hint can
    /// replace the parameter, so it must precede AddObject and any recorded undo action), and never throws: an output it cannot
    /// configure keeps the access it was born with. GH-coupled; the live emission is checked through the bridge after deploy.
    /// </summary>
    public static void ConfigureDeclaredOutputs(IGH_Component comp, IReadOnlyList<string> outputs, ScriptLanguage lang)
    {
      try
      {
        if (comp == null || outputs == null) return;
        foreach (var name in outputs)
        {
          var access = ScriptTypeMap.DeclaredOutputAccess(lang);
          if (access == null || string.IsNullOrEmpty(name)) continue;
          var spec = new ScriptParam { Name = name, Variable = name, GhType = null, Access = access.Value };
          var port = ScriptOutputs(comp).FirstOrDefault(q => string.Equals(q.NickName, name, StringComparison.Ordinal));
          if (port != null) ConfigureOutput(port, spec, lang);
        }
      }
      catch { }
    }

    private static void TrySelectHint(IGH_Param p, string hint)
    {
      try
      {
        var hints = p.GetType().GetProperty("TypeHints")?.GetValue(p);
        hints?.GetType().GetMethod("Select", new[] { typeof(string) })?.Invoke(hints, new object[] { hint });
      }
      catch { /* an unknown hint name throws; no hint is a safe, honest fallback */ }
    }

    /// <summary>
    /// U74/U77: give the wired INPUTS of a scaffold-built script the type hints their sources imply (computed by
    /// <see cref="ScriptTypeMap.HintsByInput"/>), so a Curve parameter's data arrives as a Curve rather than as a Guid.
    /// SELECTING A HINT REPLACES THE PARAMETER on a Rhino 8 script input, so this runs BEFORE the component is added to the
    /// document and before anything is recorded for undo: an undo action recorded earlier would refer to a parameter that
    /// no longer exists. Best-effort and never throws: a hint that cannot be set leaves the input as it was.
    /// GH-coupled; not exercised by a test.
    /// </summary>
    public static void SelectInputHints(IGH_Component comp, IReadOnlyDictionary<int, string> hintsByInput)
    {
      try
      {
        if (comp == null || hintsByInput == null) return;
        foreach (var kv in hintsByInput.OrderBy(k => k.Key))
        {
          if (kv.Key < 0 || kv.Key >= comp.Params.Input.Count) continue;
          var input = comp.Params.Input[kv.Key];
          if (IsScriptVariable(input)) TrySelectHint(input, kv.Value);
        }
      }
      catch { }
    }

    /// <summary>
    /// U88: the hint the model's <c>ref</c> parameter types choose for a C# script's OUTPUTS (index into <see cref="ScriptOutputs"/>).
    /// Same discipline as <see cref="SelectInputHints"/>: before the component enters the document, best-effort, never throws,
    /// and only hint names <see cref="ScriptTypeMap.IsKnownHint"/> approves reach it. GH-coupled; not exercised by a test.
    /// </summary>
    public static void SelectOutputHints(IGH_Component comp, IReadOnlyDictionary<int, string> hintsByOutput)
    {
      try
      {
        if (comp == null || hintsByOutput == null) return;
        var outs = ScriptOutputs(comp);
        foreach (var kv in hintsByOutput.OrderBy(k => k.Key))
          if (kv.Key >= 0 && kv.Key < outs.Count && ScriptTypeMap.IsKnownHint(kv.Value)) TrySelectHint(outs[kv.Key], kv.Value);
      }
      catch { }
    }

    /// <summary>
    /// PHASE-9 U20: A DUPLICATE OF AN EXISTING SCRIPT COMPONENT, for the beside-verify.
    ///
    /// IT COPIES RATHER THAN RE-DERIVES, and that is the design. to-script computes a new script's whole
    /// interface from a boundary contract because no component exists yet; here one does, so its ports,
    /// type hints, access and tree settings travel through GRASSHOPPER'S OWN SERIALISATION - the same
    /// mechanism its copy-and-paste uses. Every recomputation is a chance to differ from the thing being
    /// compared against, and a copy that differs in a way nobody declared measures the wrong question.
    ///
    /// THE PROBE IS THE POINT, and it is <see cref="TryCreate"/>'s discipline: the copy's port counts are
    /// checked against the original's BEFORE the caller has added anything to the document. A serialisation
    /// that silently carried no ports would otherwise produce a copy with a different interface, wire it
    /// from the original's sources, and compare two things that were never comparable - a false verdict
    /// rather than an honest failure.
    /// </summary>
    public static bool TryDuplicate(IGH_Component source, out IGH_Component copy, out string error)
    {
      copy = null;
      error = null;
      if (source == null) { error = "Dwight could not read the component to copy it."; return false; }

      try
      {
        var proxy = Instances.ComponentServer.EmitObjectProxy(source.ComponentGuid);   // guardrail 3
        copy = proxy?.CreateInstance() as IGH_Component;
      }
      catch { }
      if (copy == null)
      { error = "Dwight could not create a copy of that script component to test the change against."; return false; }

      try
      {
        var chunk = new GH_IO.Serialization.GH_LooseChunk("dwight-script-edit");
        if (!source.Write(chunk)) { error = "Dwight could not read that script component's settings."; copy = null; return false; }
        if (!copy.Read(chunk)) { error = "Dwight could not copy that script component's settings."; copy = null; return false; }
      }
      catch (Exception ex)
      { error = "Dwight could not copy that script component (" + ex.Message + ")."; copy = null; return false; }

      // The serialisation carries the instance guid too, so without this the copy collides with its original.
      try { copy.NewInstanceGuid(); } catch { }

      // AND IT CARRIES EVERY PARAMETER'S GUID (U35, #928). Until this was measured, only the component's guid
      // was renewed: the copy's output "a" WAS the original's output "a" by identity, the trial captured both
      // sides by guid through the document, and compared the original with itself - "they are identical" was
      // true by construction for every edit ever shown. Every parameter gets its own identity here, in the shim,
      // so EVERY caller gets it; the probe below then refuses a copy that still shares one.
      try
      {
        foreach (var p in copy.Params.Input) p.NewInstanceGuid();
        foreach (var p in copy.Params.Output) p.NewInstanceGuid();
      }
      catch { }

      // THE PROBE: the copy must have the SAME interface, or there is nothing meaningful to compare - and its
      // OWN identity down to the last parameter, or there are not two things to compare at all.
      // Checked now, while nothing has been added to the document.
      try
      {
        if (copy.Params.Input.Count != source.Params.Input.Count ||
            ScriptOutputs(copy).Count != ScriptOutputs(source).Count)
        {
          error = "Dwight's copy of that script component did not come out with the same inputs and outputs, " +
                  "so it cannot check the change against it on this Rhino.";
          copy = null;
          return false;
        }
        if (copy.InstanceGuid == source.InstanceGuid)
        {
          error = "Dwight could not give the test copy its own identity on this Rhino.";
          copy = null;
          return false;
        }
        var shared = ScriptTrialCompare.SharedIdentities(ParamGuids(source), ParamGuids(copy));
        if (shared.Count > 0)
        {
          error = "Dwight could not give the test copy's parameters their own identity on this Rhino (" +
                  shared.Count + " of them still share the original's), so a comparison would read the " +
                  "original twice; Dwight did not run the check.";
          copy = null;
          return false;
        }
      }
      catch (Exception ex)
      { error = "Dwight could not verify the copy of that script component (" + ex.Message + ")."; copy = null; return false; }

      return true;
    }

    /// <summary>Every parameter's identity on a component, inputs then outputs - the set the copy's must be
    /// disjoint from (U35).</summary>
    public static List<Guid> ParamGuids(IGH_Component comp)
    {
      var guids = new List<Guid>();
      try
      {
        foreach (var p in comp.Params.Input) guids.Add(p.InstanceGuid);
        foreach (var p in comp.Params.Output) guids.Add(p.InstanceGuid);
      }
      catch { }
      return guids;
    }

    /// <summary>
    /// U36: REMOVE the LAST <paramref name="inputs"/> inputs and the LAST <paramref name="outputs"/> script
    /// outputs - the undo of <see cref="Append"/>, which only ever adds at the end. Counts, not names: the
    /// restore data holds how many Dwight appended, and never removes more than that or below the count the
    /// component had before (see <c>ScriptEditRestore</c>, where that arithmetic is decided and tested).
    /// </summary>
    public static bool RemoveTrailingPorts(IGH_Component comp, int inputs, int outputs, out string error)
    {
      error = null;
      try
      {
        for (var i = 0; i < inputs && comp.Params.Input.Count > 0; i++)
          comp.Params.UnregisterInputParameter(comp.Params.Input[comp.Params.Input.Count - 1]);
        for (var j = 0; j < outputs; j++)
        {
          var last = ScriptOutputs(comp).LastOrDefault();
          if (last == null) break;
          comp.Params.UnregisterOutputParameter(last);
        }
        try { (comp as IGH_VariableParameterComponent)?.VariableParameterMaintenance(); } catch { }
        return true;
      }
      catch (Exception ex)
      { error = "Dwight could not remove the ports it had added to the script component: " + ex.Message; return false; }
    }

    /// <summary>Load new source into a script component, through the same reflected member
    /// <see cref="Shape"/> uses. Probed rather than trusted: a Rhino that renamed the API fails here with
    /// nothing mutated.</summary>
    public static bool SetSource(IGH_Component comp, string source, out string error)
    {
      error = null;
      try
      {
        var setSource = comp?.GetType().GetMethod("SetSource", new[] { typeof(string) });
        if (setSource == null)
        { error = "Dwight can't set the source on this Rhino's script component (its API has changed)."; return false; }
        setSource.Invoke(comp, new object[] { source ?? "" });
        try { (comp as IGH_VariableParameterComponent)?.VariableParameterMaintenance(); } catch { }
        return true;
      }
      catch (Exception ex)
      { error = "Dwight could not load the new source into the script component: " + ex.Message; return false; }
    }

    /// <summary>
    /// U78: REMOVE the ports a NEW script component is born with, on the sides the caller says the build declares its own.
    /// A scaffold-built script that declares inputs has those inputs and not also the default <c>x</c> and <c>y</c>; one that
    /// declares outputs has those and not also <c>a</c>. The standard <c>out</c> message output is not a script variable and is
    /// never touched. Only a script-variable port whose name is exactly a default is removed, so a Rhino whose defaults
    /// differ loses nothing it should keep. Runs on the not-yet-added component, as <see cref="Append"/> does.
    /// </summary>
    public static bool RemoveDefaults(IGH_Component comp, bool inputs, bool outputs,
      IReadOnlyList<string> defaultInputs, IReadOnlyList<string> defaultOutputs, out string error)
    {
      error = null;
      if (!inputs && !outputs) return true;
      try
      {
        if (inputs)
          foreach (var p in comp.Params.Input.Where(q => IsScriptVariable(q) && IsDefault(q, defaultInputs)).ToList())
            comp.Params.UnregisterInputParameter(p);
        if (outputs)
          foreach (var p in comp.Params.Output.Where(q => IsScriptVariable(q) && IsDefault(q, defaultOutputs)).ToList())
            comp.Params.UnregisterOutputParameter(p);
        return true;
      }
      catch (Exception ex)
      { error = "Dwight could not remove the default ports from the script component: " + ex.Message; return false; }
    }

    private static bool IsDefault(IGH_Param p, IReadOnlyList<string> defaults) =>
      defaults.Any(d => string.Equals(d, p.NickName, StringComparison.OrdinalIgnoreCase)
                     || string.Equals(d, p.Name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// PHASE-9 U20: APPEND ports to a script component - never remove, reorder or rename one.
    ///
    /// Appending cuts nothing: existing ports keep their index and every existing wire keeps its param
    /// object, so the predicted delta stays "this component and nothing else". Removal and reordering
    /// sever or re-bind the user's wires - the boundary-cut class #625 governs elsewhere - and the ask
    /// refuses them long before this is reached.
    ///
    /// The NAME is the model's, because it is the identifier its code uses. The TYPE HINT is deliberately
    /// left unset: an appended port has nothing wired to it yet and therefore no live type to derive one
    /// from, and no hint is a legitimate contract (generic goo), as the type map already documents.
    /// </summary>
    public static bool Append(IGH_Component comp, IReadOnlyList<string> inputs, IReadOnlyList<string> outputs, out string error)
    {
      error = null;
      if ((inputs == null || inputs.Count == 0) && (outputs == null || outputs.Count == 0)) return true;

      try
      {
        if (!(comp is IGH_VariableParameterComponent vp))
        { error = "That script component does not support adding parameters on this Rhino."; return false; }

        foreach (var name in inputs ?? new string[0])
        {
          var np = vp.CreateParameter(GH_ParameterSide.Input, comp.Params.Input.Count);
          if (np == null) { error = "Dwight could not add the input \"" + name + "\" to the script."; return false; }
          comp.Params.RegisterInputParam(np, comp.Params.Input.Count);
          NamePort(np, name);
        }
        foreach (var name in outputs ?? new string[0])
        {
          var np = vp.CreateParameter(GH_ParameterSide.Output, comp.Params.Output.Count);
          if (np == null) { error = "Dwight could not add the output \"" + name + "\" to the script."; return false; }
          comp.Params.RegisterOutputParam(np, comp.Params.Output.Count);
          NamePort(np, name);
        }
        try { vp.VariableParameterMaintenance(); } catch { }
        return true;
      }
      catch (Exception ex)
      { error = "Dwight could not add the new ports to the script component: " + ex.Message; return false; }
    }

    private static void NamePort(IGH_Param p, string name)
    {
      try { p.Name = name; p.NickName = name; } catch { }
      try { p.GetType().GetProperty("VariableName")?.SetValue(p, name); } catch { }
    }

    /// <summary>The component's compile/runtime errors, verbatim (they carry a readable message and a [line:col]).
    /// A script that ERRORS still emits a null item on every output - the live finding - so a caller must check
    /// this BEFORE comparing outputs, never rely on the comparison to notice.</summary>
    public static List<string> Errors(IGH_DocumentObject obj)
    {
      var msgs = new List<string>();
      try
      {
        if (obj is IGH_ActiveObject a)
          foreach (var m in a.RuntimeMessages(GH_RuntimeMessageLevel.Error)) msgs.Add(m);
      }
      catch { }
      return msgs;
    }
  }
}
