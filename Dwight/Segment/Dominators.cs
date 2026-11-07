using System;
using System.Collections.Generic;
using System.Linq;

namespace Dwight.Segment
{
  /// <summary>
  /// Dominator and post-dominator trees (Phase 6 WP-B Stage 1, ADR-087), via the Cooper-Harvey-Kennedy
  /// "simple, fast dominance" iterative algorithm - simpler than Lengauer-Tarjan and ample for these graph
  /// sizes. <c>idom[n]</c> is n's immediate dominator (the last node every path from the entry to n must pass
  /// through); post-dominators are just dominators on the reversed graph rooted at the sink. On the acyclic
  /// dataflow these give the SESE boundaries cleanly: a region [e,x] is single-entry-single-exit when e
  /// dominates x and x post-dominates e. Deterministic - the DFS and the predecessor scan run in guid order.
  /// </summary>
  internal static class Dominators
  {
    /// <summary>Immediate dominators from <see cref="DataflowGraph.Source"/> (forward) or immediate
    /// post-dominators from <see cref="DataflowGraph.Sink"/> (reverse). Returns node -&gt; idom; the entry maps
    /// to itself; unreachable nodes are absent.</summary>
    public static Dictionary<int, int> Compute(DataflowGraph g, bool reverse)
    {
      var entry = reverse ? DataflowGraph.Sink : DataflowGraph.Source;
      Func<int, IReadOnlyList<int>> succ = reverse ? (Func<int, IReadOnlyList<int>>)g.Predecessors : g.Successors;
      Func<int, IReadOnlyList<int>> pred = reverse ? (Func<int, IReadOnlyList<int>>)g.Successors : g.Predecessors;

      // 1. postorder DFS from the entry, following succ in guid order (the lists are already key-sorted).
      var postNum = new Dictionary<int, int>();
      var order = new List<int>();
      Dfs(entry, succ, postNum, order);

      // 2. reverse postorder (entry last in postorder -> first here), excluding the entry itself.
      var rpo = Enumerable.Reverse(order).Where(n => n != entry).ToList();

      var idom = new Dictionary<int, int> { [entry] = entry };
      var changed = true;
      while (changed)
      {
        changed = false;
        foreach (var b in rpo)
        {
          var newIdom = -1;
          foreach (var p in pred(b))
          {
            if (!postNum.ContainsKey(p)) continue;       // unreachable from entry
            if (!idom.ContainsKey(p)) continue;          // not processed yet this pass
            newIdom = newIdom == -1 ? p : Intersect(p, newIdom, idom, postNum);
          }
          if (newIdom != -1 && (!idom.TryGetValue(b, out var cur) || cur != newIdom))
          { idom[b] = newIdom; changed = true; }
        }
      }
      return idom;
    }

    /// <summary>
    /// Do <paramref name="a"/> and <paramref name="b"/> share a REAL post-dominator - i.e. is there a real
    /// node every path from both must pass through before the virtual sink? (Pass an ipdom map from
    /// <see cref="Compute"/> with reverse=true.) This is the entry-crossing test (Phase 6 WP-B boundary
    /// terms): an edge U-&gt;E whose endpoints share NO real post-dominator (they reconverge only at the global
    /// sink) crosses a region boundary - E's downstream is a different region than U's. Pure topology, so it
    /// survives a scattered canvas.
    /// </summary>
    public static bool SharesRealPostDominator(IReadOnlyDictionary<int, int> ipdom, int a, int b)
    {
      var ancestors = new HashSet<int>();
      Collect(ipdom, a, ancestors);
      var cur = b;
      while (true)
      {
        if (IsReal(cur) && ancestors.Contains(cur)) return true;
        if (!ipdom.TryGetValue(cur, out var p) || p == cur || p == DataflowGraph.Sink) break;
        cur = p;
      }
      return false;
    }

    private static void Collect(IReadOnlyDictionary<int, int> ipdom, int start, HashSet<int> into)
    {
      var cur = start;
      while (true)
      {
        if (IsReal(cur)) into.Add(cur);
        if (!ipdom.TryGetValue(cur, out var p) || p == cur || p == DataflowGraph.Sink) break;
        cur = p;
      }
    }

    private static bool IsReal(int id) => id != DataflowGraph.Source && id != DataflowGraph.Sink;

    /// <summary>Walk both fingers up the partial dominator tree until they meet (CHK), using postorder numbers
    /// so the lower-numbered finger - deeper in the DFS - climbs.</summary>
    private static int Intersect(int a, int b, Dictionary<int, int> idom, Dictionary<int, int> postNum)
    {
      while (a != b)
      {
        while (postNum[a] < postNum[b]) a = idom[a];
        while (postNum[b] < postNum[a]) b = idom[b];
      }
      return a;
    }

    /// <summary>Iterative postorder DFS (guid-ordered successors), so the numbering is deterministic and the
    /// recursion cannot stack-overflow on a deep definition.</summary>
    private static void Dfs(int entry, Func<int, IReadOnlyList<int>> succ, Dictionary<int, int> postNum, List<int> order)
    {
      var visited = new HashSet<int> { entry };
      var stack = new Stack<(int node, int next)>();
      stack.Push((entry, 0));
      while (stack.Count > 0)
      {
        var (node, next) = stack.Pop();
        var children = succ(node);
        if (next < children.Count)
        {
          stack.Push((node, next + 1));
          var c = children[next];
          if (visited.Add(c)) stack.Push((c, 0));
        }
        else
        {
          postNum[node] = order.Count;
          order.Add(node);
        }
      }
    }
  }
}
