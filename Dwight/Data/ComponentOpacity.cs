namespace Dwight.Data
{
  /// <summary>
  /// RR17 #36 (GH-free, ADR-091): can Dwight SHOW the model what this component does? The live reads (is it a
  /// cluster, is it a script, did its source read, is it a core library) are the GH shim's job; the RULE is here,
  /// so it is headless-testable.
  ///
  /// The rule exists because "the model can see it" and "the model can name it" are different things. A readable
  /// script is transparent - its source goes in the payload. An unreadable script, a cluster, or a third-party
  /// plugin component is OPAQUE: its behaviour lives somewhere Dwight cannot read, so any claim about what it does
  /// is invention. Better-logic can still work around an opaque node (a SEALED UNIT - keep it, rewire around it);
  /// to-script cannot, because it must REPRODUCE the behaviour, and so it refuses.
  /// </summary>
  internal static class ComponentOpacity
  {
    public static bool IsOpaque(bool isCluster, bool isScript, bool sourceReadable, bool isCoreLibrary)
    {
      if (isCluster) return true;                    // a hidden subgraph
      if (isScript) return !sourceReadable;          // a script we cannot read (locked, encrypted, oversized)
      return !isCoreLibrary;                         // a third-party plugin component: logic only in its assembly
    }
  }
}
