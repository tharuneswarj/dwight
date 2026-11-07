using System.Collections.Generic;

namespace Dwight.Simplify
{
  /// <summary>One output the selection produces, for layer-2's before/after multiset compare: which selection
  /// component PRODUCES it, whether it is terminal (feeds nothing) or a boundary output (feeds an external
  /// consumer), and a stable key for that external consumer input (boundary only).</summary>
  internal readonly struct OutputPort
  {
    public readonly int ProducerId;
    public readonly bool Terminal;
    public readonly int ConsumerKey;   // boundary only; -1 for terminal
    public OutputPort(int producerId, bool terminal, int consumerKey)
    { ProducerId = producerId; Terminal = terminal; ConsumerKey = consumerKey; }
  }

  /// <summary>
  /// Layer 2's comparison surface (RR4 correction #3, ADR-091). Layer 2 compares the PRESERVED output surface
  /// before vs after, NEVER the raw output set: an output owned SOLELY by a plan-declared removal is excluded
  /// from BOTH captures, because its disappearance IS the accepted change - including it would falsely revert a
  /// correct dead-output removal (the round-4 trap: "removed dead Sort List (154) as its outputs are unused" is a
  /// terminal producer, so its outputs would be in the before-bag and gone from the after-bag).
  ///
  /// The preserved surface is: the terminal outputs of KEPT components, plus the boundary consumers still fed -
  /// their producer kept, or a rewire re-feeds them from a kept/added source. Pure and headless; the GH shim
  /// builds the <see cref="OutputPort"/> list from the live graph and samples exactly this subset.
  /// </summary>
  internal static class Layer2Surface
  {
    public static List<OutputPort> Preserved(IEnumerable<OutputPort> surface, ISet<int> keep, ISet<int> rewiredConsumers)
    {
      var result = new List<OutputPort>();
      if (surface == null) return result;
      foreach (var p in surface)
      {
        if (p.Terminal)
        {
          if (keep != null && keep.Contains(p.ProducerId)) result.Add(p);   // a kept component's terminal output
        }
        else
        {
          // a boundary consumer is compared if its selection producer is kept, or a rewire re-feeds it.
          if ((keep != null && keep.Contains(p.ProducerId)) || (rewiredConsumers != null && rewiredConsumers.Contains(p.ConsumerKey)))
            result.Add(p);
        }
        // an output owned solely by a plan-declared removal falls through - excluded from the compare.
      }
      return result;
    }
  }
}
