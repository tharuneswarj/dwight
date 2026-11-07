using System;
using System.Linq;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;

namespace Dwight.Data
{
  /// <summary>
  /// RR17 #36: read a script component's SOURCE out of the live document, so the model can SEE what a script node
  /// in the selection actually does.
  ///
  /// The bug this closes: a script node reached the model as <c>{"type":"GhPython Script"}</c> and nothing else -
  /// an unreadable black box. Asking it to reproduce the selection's behaviour when part of that behaviour is
  /// invisible does not risk a hallucination, it GUARANTEES one. It is the user's own code, on their canvas, and
  /// the model needs it.
  ///
  /// Every access is reflection (GhPython and RhinoCodePluginGH are Rhino plugins, not referenceable SDKs) and
  /// every failure degrades to "unreadable", which the caller treats as OPAQUE - never as an empty script.
  /// Instrumented live via the bridge, not inferred: Rhino 8 canvases carry BOTH the legacy
  /// <c>GhPython.Component.ZuiPythonComponent</c> (a settable <c>Code</c> property) and the new
  /// <c>RhinoCodePluginGH</c> components (<c>TryGetSource(out string)</c>).
  /// </summary>
  internal static class ScriptSourceReader
  {
    /// <summary>A script longer than this is not sent. It would swamp the payload, and a partially-shown script is
    /// worse than an honestly-unreadable one - the model would reproduce what it could see and silently drop the
    /// rest. Over the cap, the component is treated as OPAQUE.</summary>
    public const int MaxChars = 12000;

    /// <summary>Is this object a script component at all (readable or not)? Keyed on the type name, so a script
    /// whose source we cannot read is still recognised AS a script - and therefore reported as opaque rather than
    /// mistaken for an ordinary component.</summary>
    public static bool IsScriptComponent(IGH_DocumentObject obj) =>
      obj != null && ScriptSourceReflect.IsScriptTypeName(obj.GetType().FullName);

    /// <summary>The component's source, or null when it cannot be read (a locked / encrypted / unknown script)
    /// OR when it is over <see cref="MaxChars"/> - the verbs' bound: to-script must reproduce the whole script
    /// or nothing, so an oversized one is honestly OPAQUE, never half-shown.
    /// Null is a first-class answer: the caller must treat it as opaque, never as "no code".</summary>
    public static string TryRead(IGH_DocumentObject obj)
    {
      var raw = TryReadRaw(obj);
      return raw != null && raw.Length > MaxChars ? null : raw;
    }

    /// <summary>The same read, UNCAPPED (Phase-9 U1): the agent is a reader, not a reproducer, so it takes a
    /// bounded prefix of a long script and states what it left out (<c>SourceBound</c>) rather than treating
    /// the whole thing as unreadable. One reader, three stated policies - never three readers.
    ///
    /// THIS ONE'S POLICY, kept exactly as it was: an EMPTY script reads as null, the same as an unreadable
    /// one. That is right for its callers - to-script has nothing to reproduce from an empty script, and
    /// the payload has nothing to show - but it is WRONG for a guard that must tell "Dwight's empty stub"
    /// from "a script we cannot read", which is why <see cref="TryReadSource"/> exists beneath it.</summary>
    public static string TryReadRaw(IGH_DocumentObject obj)
    {
      var read = TryReadSource(obj, out var src);
      return read && !string.IsNullOrWhiteSpace(src) ? Normalised(src) : null;
    }

    /// <summary>
    /// THE PRIMITIVE READ, and the only one that separates "there is no code here" from "we could not
    /// look" (#779).
    ///
    /// <see cref="TryReadRaw"/> collapses both onto null, which is right for a caller that wants CODE and
    /// wrong for a guard that wants to know whether the component CHANGED: an empty script Dwight placed
    /// and a locked script it cannot read are the same answer there, and treating the first as unreadable
    /// would make Dwight's own empty stub permanently un-rebuildable.
    ///
    /// The decision and the reflection live in the GH-free <see cref="ScriptSourceReflect"/>, where they
    /// can be driven by a test; what is left here is delegation with no decision in it.
    /// </summary>
    public static bool TryReadSource(IGH_DocumentObject obj, out string source) =>
      ScriptSourceReflect.TryRead(obj, out source);

    private static string Normalised(string src)
    {
      if (string.IsNullOrWhiteSpace(src)) return null;
      return src.Replace("\r\n", "\n").Trim();
    }

    /// <summary>The installed PLUGIN a component comes from, or null for a core Grasshopper library (Phase-9
    /// U1). The same proxy-and-assembly lookup <see cref="IsCoreLibrary"/> makes, kept beside it so the two
    /// cannot disagree about what "core" means.</summary>
    public static string LibraryOf(IGH_DocumentObject obj)
    {
      try
      {
        var proxy = Grasshopper.Instances.ComponentServer.EmitObjectProxy(obj.ComponentGuid);
        if (proxy == null) return null;
        var lib = Grasshopper.Instances.ComponentServer.FindAssembly(proxy.LibraryGuid);
        if (lib == null || lib.IsCoreLibrary) return null;
        return PluginName.Of(lib.Name);   // one name rule, shared with the installed-plugins inventory (U23)
      }
      catch { return null; }
    }

    /// <summary>Is this component's behaviour something Dwight can actually SHOW the model? A cluster hides a whole
    /// subgraph; a third-party plugin component's logic exists only in its compiled assembly; an unreadable script
    /// is a black box. The <c>IsCoreLibrary</c> flag is the right test and the assembly NAME is not - Grasshopper's
    /// own components live in CurveComponents / MathComponents / SurfaceComponents / ... (instrumented; an
    /// assembly-name test would have refused a perfectly ordinary definition).</summary>
    public static bool IsOpaque(IGH_DocumentObject obj)
    {
      if (obj == null) return false;
      var isScript = IsScriptComponent(obj);
      return ComponentOpacity.IsOpaque(          // the RULE is GH-free and headless-tested; this is just the read
        isCluster: obj is GH_Cluster,
        isScript: isScript,
        sourceReadable: isScript && TryRead(obj) != null,
        isCoreLibrary: IsCoreLibrary(obj));
    }

    /// <summary>Does this object come from a CORE Grasshopper library (as opposed to an installed plugin)?
    /// Conservative on failure: unknown -> treated as core, so a lookup wobble never refuses a real definition.</summary>
    public static bool IsCoreLibrary(IGH_DocumentObject obj)
    {
      try
      {
        var proxy = Grasshopper.Instances.ComponentServer.EmitObjectProxy(obj.ComponentGuid);
        if (proxy == null) return true;
        var lib = Grasshopper.Instances.ComponentServer.FindAssembly(proxy.LibraryGuid);
        return lib == null || lib.IsCoreLibrary;
      }
      catch { return true; }
    }
  }
}
