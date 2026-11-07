using System.Collections.Generic;
using System.Linq;
using Grasshopper.Kernel;
using Dwight.Data;

namespace Dwight.Diagnostics
{
  /// <summary>
  /// The deterministic fault scan + fault-chain trace (ADR-059, Spec B2): <b>finding is deterministic,
  /// explaining is the LLM.</b> The plugin scans runtime-message levels and per-output volatile counts
  /// with ZERO tokens, separates root causes from downstream symptoms by walking the wire graph, and
  /// only then is the model asked to explain. A clean scan never calls the model at all.
  ///
  /// Two fault classes, deliberately distinct (architect note): <b>hard faults</b> (error/warning from
  /// the live runtime messages) are certain; <b>silent failures</b> are <i>suspicions</i> - a green
  /// component that received data and is used downstream but produced nothing, which can be legitimate
  /// (a Cull that culls all, a filter that filters everything). So roots prefer hard faults, and a
  /// silent suspicion is never chosen as a root when anything faulty sits upstream - Diagnose must not
  /// cry wolf.
  /// </summary>
  internal static class FaultScan
  {
    public enum Kind { Error, Warning, Silent }

    internal sealed class Fault
    {
      public GraphNode Node;
      public Kind Kind;
      public bool IsRoot;                 // upstream-most (drives the root-cause lead + ordering)

      /// <summary>The explain/one-liner line (ADR-078): a fault is INTRINSIC if it has its own runtime
      /// error/warning message, OR it is a silent failure with no faulty upstream (a by-design empty).
      /// Intrinsic faults get a full LLM explanation, even when they sit below a root. A NON-intrinsic
      /// fault is pure propagation - a silent component empty solely because a broken upstream starves it -
      /// and gets a deterministic one-liner, no tokens. The line is own-error-message, not has-faulty-upstream.</summary>
      public bool Intrinsic;
      public List<int> UpstreamFaultIds = new List<int>();  // faults that feed this one (the chain)

      /// <summary>Brief EX: a root's inputs with nothing wired into them, when Grasshopper's "failed to collect data" for those inputs is all it says. The plugin writes this fault's cause; empty for every other.</summary>
      public List<string> Unwired = new List<string>();
    }

    /// <summary>One root cause and every fault that traces back to it - the unit the whole-canvas
    /// chain-picker lists (ADR-059).</summary>
    internal sealed class Chain
    {
      public Fault Root;
      public List<Fault> Members = new List<Fault>();
    }

    internal sealed class Report
    {
      public bool WholeCanvas;
      public List<Fault> Faults = new List<Fault>();

      /// <summary>Isolated components (no wires either side) that are NOT faults - an informational third
      /// class (ADR-078): usually intentional work-in-progress, never a fault or a root, never sent to the LLM.</summary>
      public List<GraphNode> Stray = new List<GraphNode>();

      public bool AnyFaults => Faults.Count > 0;
      public bool HasStray => Stray.Count > 0;

      /// <summary>Roots first (hard errors, then warnings, then silent suspicions); the presentation
      /// leads with these (ADR-059 root-cause-first).</summary>
      public IEnumerable<Fault> Roots => Faults.Where(f => f.IsRoot);

      /// <summary>The faults that earn a full explanation (ADR-078): intrinsic only - the rest are
      /// rendered deterministically as "caused by &lt;root&gt;".</summary>
      public IEnumerable<Fault> Intrinsic => Faults.Where(f => f.Intrinsic);

      /// <summary>Group the faults into chains, one per root cause (for the whole-canvas chain-picker).</summary>
      public List<Chain> Chains()
      {
        var chains = new List<Chain>();
        foreach (var root in Faults.Where(f => f.IsRoot))
          chains.Add(new Chain
          {
            Root = root,
            Members = Faults.Where(f => f == root || f.UpstreamFaultIds.Contains(root.Node.Id)).ToList(),
          });
        return chains;
      }
    }

    /// <summary>Scan for faults. Selection scope diagnoses the selection plus its upstream chain;
    /// whole-canvas scope (empty selection) scans everything. Runs on the UI thread (reads live data).</summary>
    public static Report Run(DwightGraph graph, GH_Document doc, bool wholeCanvas)
    {
      var report = new Report { WholeCanvas = wholeCanvas };
      if (graph == null) return report;

      // The scan set: whole canvas, or the selection plus everything upstream of it (the suspect chain).
      var scan = wholeCanvas
        ? graph.Nodes.Select(n => n.Id).ToHashSet()
        : UpstreamClosure(graph, graph.Selection.Select(n => n.Id));

      // 1. Find faults deterministically, and 2. trace each one's place in the chain (FaultChainCore, GH-free, Brief EX).
      //    IsRoot (ordering/lead): the upstream-most fault - nothing faulty feeds it.
      //    Intrinsic (explain vs one-liner, ADR-078 refined): a fault earns a full explanation only if it
      //    has a REAL own error - an error/warning that is NOT a data-starvation message. Grasshopper's
      //    "failed to collect data" / "failed to connect" are how it reports being starved by an upstream
      //    fault, i.e. downstream propagation, NOT an intrinsic fault. So a starvation-only fault (or a
      //    silent one) is intrinsic only when nothing faulty is upstream (an unwired input = a root); when
      //    starved by an upstream fault it is pure propagation and gets the deterministic one-liner.
      //    Unwired (Brief EX): a root starved only because nothing is wired into the inputs it names - the plugin states that cause.
      var nodes = scan.Select(graph.NodeById).Where(n => n != null).Select(n => new FaultChainCore.Node
      {
        Id = n.Id,
        Msgs = n.Msgs.Select(m => (m.Level, m.Text)).ToList(),
        HasUpstream = n.HasUpstream,
        HasDownstream = n.HasDownstream,
        OutCounts = n.OutCounts,
        InputDataCount = () => DwightGraph.InputDataCount(n, doc),
      }).ToList();
      var wires = graph.Edges.Select(e => new FaultChainCore.Wire { From = e.FromNode, To = e.ToNode, ToParam = e.ToParam }).ToList();
      var found = FaultChainCore.Scan(nodes, wires);
      var faultIds = found.Select(f => f.Id).ToHashSet();

      // Stray (informational, ADR-078): isolated components that are not themselves faults. Computed
      // whether or not anything is failing, so "nothing is failing" can still note unwired components.
      report.Stray = scan.Select(graph.NodeById)
        .Where(n => n != null && n.Kind == "component" && !n.HasUpstream && !n.HasDownstream && !faultIds.Contains(n.Id))
        .OrderBy(n => n.Id)
        .ToList();

      if (found.Count == 0) return report;   // clean of faults (may still carry stray)

      // Order (the core's): roots first, hard before silent, then by id (stable).
      report.Faults = found.Select(f => new Fault
      {
        Node = graph.NodeById(f.Id),
        Kind = (Kind)f.Kind,
        IsRoot = f.IsRoot,
        Intrinsic = f.Intrinsic,
        UpstreamFaultIds = f.Upstream,
        Unwired = f.Unwired,
      }).ToList();
      return report;
    }

    /// <summary>The selection plus the transitive closure of everything upstream of it.</summary>
    private static HashSet<int> UpstreamClosure(DwightGraph graph, IEnumerable<int> seedIds)
    {
      var closure = new HashSet<int>(seedIds);
      var stack = new Stack<int>(closure);
      while (stack.Count > 0)
      {
        var id = stack.Pop();
        foreach (var e in graph.IncomingTo(id))
          if (closure.Add(e.FromNode)) stack.Push(e.FromNode);
      }
      return closure;
    }
  }
}
