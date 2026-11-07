using System;
using System.Collections.Generic;
using System.Linq;

namespace Dwight.Segment
{
  /// <summary>
  /// The Refined Process Structure Tree (Phase 6 WP-B Stage 1, ADR-087). A "converge -> process -> diverge"
  /// unit is a single-entry-single-exit (SESE) region; the definition decomposes into a nested tree of them,
  /// read from dominance. A canonical SESE region is a matched pair (e, x) where x is e's immediate
  /// post-dominator AND e is x's immediate dominator - so every path into the region passes through e and
  /// every path out passes through x. The regions are laminar (nested or disjoint), so they form a tree; the
  /// granularity descent walks it and locks fragments in a definition-relative size band, never a fixed
  /// count. Deterministic - every traversal is in guid order. Headless.
  /// </summary>
  internal sealed class RpstRegion
  {
    public int Entry, Exit;                         // boundary nodes (may be Source / Sink)
    public HashSet<int> Interior = new HashSet<int>(); // real nodes strictly inside (excludes the boundaries)
    public int Rep = -1;                            // a deterministic representative interior node (min guid) - the
                                                    // O(1) subset probe for the containment tree (WP-4.1)
    public readonly List<RpstRegion> Children = new List<RpstRegion>();
  }

  internal static class Rpst
  {
    /// <summary>Build the region tree from the dominator + post-dominator trees. The root is the whole graph
    /// (Source, Sink) so coverage is total; the matched SESE regions nest beneath it by containment.</summary>
    public static RpstRegion Build(DataflowGraph g, Dictionary<int, int> idom, Dictionary<int, int> ipdom)
    {
      var realNodes = g.Nodes.Where(g.IsReal).OrderBy(g.KeyOf).ToList();

      var root = new RpstRegion { Entry = DataflowGraph.Source, Exit = DataflowGraph.Sink, Interior = new HashSet<int>(realNodes) };
      root.Rep = RepOf(root.Interior, g);

      // WP-4.1: Euler-tour interval numbering on the dominator + post-dominator trees, so "a dominates n" /
      // "a post-dominates n" are O(1) interval-containment checks instead of O(depth) walks up the tree. This
      // turns Interior from O(V*depth) per region into O(V), and BuildContainmentTree's subset test into O(1).
      var (tinD, toutD) = EulerTour(idom, g);
      var (tinP, toutP) = EulerTour(ipdom, g);
      bool DomAnc(int a, int n) => tinD.TryGetValue(a, out var ta) && tinD.TryGetValue(n, out var tn) && ta <= tn && toutD[n] <= toutD[a];
      bool PdomAnc(int a, int n) => tinP.TryGetValue(a, out var ta) && tinP.TryGetValue(n, out var tn) && ta <= tn && toutP[n] <= toutP[a];

      // matched regions (e, x): x = ipdom(e) and e = idom(x), with a non-empty interior (trivial edge regions
      // are skipped - they add no structure). De-duplicated by interior content.
      var regions = new List<RpstRegion>();
      var seen = new HashSet<string>();
      foreach (var e in g.Nodes.OrderBy(g.KeyOf))
      {
        if (!ipdom.TryGetValue(e, out var x) || e == x) continue;
        if (!idom.TryGetValue(x, out var dx) || dx != e) continue;
        var interior = Interior(e, x, realNodes, DomAnc, PdomAnc);
        if (interior.Count == 0) continue;
        var sig = Signature(interior);
        if (!seen.Add(sig)) continue;
        regions.Add(new RpstRegion { Entry = e, Exit = x, Interior = interior, Rep = RepOf(interior, g) });
      }

      BuildContainmentTree(root, regions, g);
      return root;
    }

    /// <summary>A deterministic representative of a region: its minimum-guid interior node. Used as an O(1)
    /// membership probe against a candidate parent's interior in the (laminar) containment test (WP-4.1).</summary>
    private static int RepOf(HashSet<int> interior, DataflowGraph g)
    {
      var rep = -1;
      foreach (var n in interior) if (rep < 0 || g.KeyOf(n).CompareTo(g.KeyOf(rep)) < 0) rep = n;
      return rep;
    }

    /// <summary>Euler-tour enter/exit times for a dominance tree given as a child -> immediate-parent map (a
    /// self-parent is a root). Iterative DFS (deep dominance chains would overflow a recursive one), children
    /// visited in guid order for determinism. Node <c>a</c> is an ancestor of <c>n</c> iff
    /// <c>tin[a] &lt;= tin[n] &amp;&amp; tout[n] &lt;= tout[a]</c>.</summary>
    private static (Dictionary<int, int> tin, Dictionary<int, int> tout) EulerTour(Dictionary<int, int> tree, DataflowGraph g)
    {
      var children = new Dictionary<int, List<int>>();
      var roots = new List<int>();
      foreach (var kv in tree)
      {
        if (kv.Value == kv.Key) roots.Add(kv.Key);
        else { if (!children.TryGetValue(kv.Value, out var l)) children[kv.Value] = l = new List<int>(); l.Add(kv.Key); }
      }
      foreach (var l in children.Values) l.Sort((a, b) => g.KeyOf(a).CompareTo(g.KeyOf(b)));
      roots.Sort((a, b) => g.KeyOf(a).CompareTo(g.KeyOf(b)));

      var tin = new Dictionary<int, int>();
      var tout = new Dictionary<int, int>();
      var time = 0;
      foreach (var root in roots)
      {
        var stack = new Stack<(int node, int childIdx)>();
        tin[root] = time++;
        stack.Push((root, 0));
        while (stack.Count > 0)
        {
          var (node, ci) = stack.Pop();
          var kids = children.TryGetValue(node, out var k) ? k : null;
          if (kids != null && ci < kids.Count)
          {
            stack.Push((node, ci + 1));       // resume this node at its next child
            var child = kids[ci];
            tin[child] = time++;
            stack.Push((child, 0));           // descend
          }
          else tout[node] = time++;           // all children done
        }
      }
      return (tin, tout);
    }

    /// <summary>Flatten the nested region tree to FLAT groups (ADR-085: GH groups cannot nest), emitting each
    /// component in EXACTLY ONE group. The rule is LARGEST band region first, not deepest: a coherent unit at
    /// or below the size max is emitted WHOLE as one group - a real Grasshopper group is many-in-many-out
    /// (e.g. 3 input params + 3 output params, one logical unit), and the dominator/post-dominator matcher
    /// finds legitimate SESE sub-regions inside it (its back half is single-entry-single-exit), so descending
    /// to the deepest region would shred the unit. A region is descended ONLY when it is bigger than the max.
    /// A global assigned-set guarantees no component is ever in two groups (the parent/child overlap guard).
    /// The synthetic root and every leftover are split by WEAK CONNECTIVITY, so independent sub-logics never
    /// merge; a matched SESE region is emitted whole (its parallel branches are one unit).</summary>
    public static List<HashSet<int>> Fragments(RpstRegion root, DataflowGraph g, SegOptions options)
    {
      var groups = new List<HashSet<int>>();
      var assigned = new HashSet<int>();
      Descend(root, g, options, groups, assigned, split: true);
      return groups;
    }

    private static void Descend(RpstRegion region, DataflowGraph g, SegOptions o, List<HashSet<int>> groups, HashSet<int> assigned, bool split)
    {
      var mine = new HashSet<int>(region.Interior.Where(n => !assigned.Contains(n)));   // only what is still unclaimed
      if (mine.Count == 0) return;

      if (mine.Count <= o.GranularityMax)
      {
        Emit(mine, g, groups, assigned, split);   // a coherent unit at/below the max is ONE group, whole
        return;
      }

      // too big for one group: descend children (each claims its sub-region), then the unclaimed remainder is
      // split by weak connectivity (so independent pieces of a big rigid never merge).
      foreach (var child in region.Children) Descend(child, g, o, groups, assigned, split: false);
      var leftover = new HashSet<int>(region.Interior.Where(n => !assigned.Contains(n)));
      if (leftover.Count > 0) Emit(leftover, g, groups, assigned, split: true);
    }

    /// <summary>Emit a node set as one group (a matched region, kept whole), or - when <paramref name="split"/>
    /// (the root / a leftover) - one group per weakly-connected component. Marks every emitted node assigned.</summary>
    private static void Emit(HashSet<int> nodes, DataflowGraph g, List<HashSet<int>> groups, HashSet<int> assigned, bool split)
    {
      if (!split || nodes.Count <= 1)
      {
        foreach (var n in nodes) assigned.Add(n);
        groups.Add(new HashSet<int>(nodes));
        return;
      }
      foreach (var comp in WeakComponents(nodes, g))
      {
        foreach (var n in comp) assigned.Add(n);
        groups.Add(comp);
      }
    }

    /// <summary>Weakly-connected components within <paramref name="nodes"/>, using the spine graph's edges
    /// (treated undirected). Deterministic - components are seeded in guid order.</summary>
    private static List<HashSet<int>> WeakComponents(HashSet<int> nodes, DataflowGraph g)
    {
      var comps = new List<HashSet<int>>();
      var seen = new HashSet<int>();
      foreach (var start in nodes.OrderBy(g.KeyOf))
      {
        if (!seen.Add(start)) continue;
        var comp = new HashSet<int> { start };
        var queue = new Queue<int>();
        queue.Enqueue(start);
        while (queue.Count > 0)
        {
          var u = queue.Dequeue();
          foreach (var v in g.Successors(u).Concat(g.Predecessors(u)))
            if (nodes.Contains(v) && seen.Add(v)) { comp.Add(v); queue.Enqueue(v); }
        }
        comps.Add(comp);
      }
      return comps;
    }

    /// <summary>Diagnostics: how many matched SESE regions the tree holds beneath the root (0 = only the
    /// whole-graph region matched, the "one group" failure), and the tree's nesting depth.</summary>
    public static int CountRegions(RpstRegion root)
    {
      var n = 0;
      void Walk(RpstRegion r) { foreach (var c in r.Children) { n++; Walk(c); } }
      Walk(root);
      return n;
    }

    public static int MaxDepth(RpstRegion root)
    {
      var best = 0;
      void Walk(RpstRegion r, int d) { if (d > best) best = d; foreach (var c in r.Children) Walk(c, d + 1); }
      Walk(root, 0);
      return best;
    }

    // ---- internals ----------------------------------------------------------------------------------

    /// <summary>The real nodes strictly between e and x: dominated by e and post-dominated by x. O(V) with the
    /// O(1) interval ancestor tests (WP-4.1).</summary>
    private static HashSet<int> Interior(int e, int x, List<int> realNodes, Func<int, int, bool> domAnc, Func<int, int, bool> pdomAnc)
    {
      var interior = new HashSet<int>();
      foreach (var n in realNodes)
      {
        if (n == e || n == x) continue;
        if (domAnc(e, n) && pdomAnc(x, n)) interior.Add(n);
      }
      return interior;
    }

    private static void BuildContainmentTree(RpstRegion root, List<RpstRegion> regions, DataflowGraph g)
    {
      // smallest-first, so the first strict superset found is the immediate parent.
      var ordered = regions.OrderBy(r => r.Interior.Count).ThenBy(r => g.KeyOf(r.Entry)).ToList();
      var all = new List<RpstRegion>(ordered) { root };

      foreach (var r in ordered)
      {
        RpstRegion parent = null;
        foreach (var s in all)
        {
          if (ReferenceEquals(s, r)) continue;
          if (s.Interior.Count <= r.Interior.Count) continue;
          // WP-4.1: the RPST regions are laminar (nested or disjoint), so for a strictly-larger s, "r subset of s"
          // is decided by a SINGLE membership test - if s's interior holds r's representative node, it holds all of
          // r. O(1) instead of the O(V) IsSubsetOf, and identical for laminar sets.
          if (r.Rep < 0 || !s.Interior.Contains(r.Rep)) continue;
          if (parent == null || s.Interior.Count < parent.Interior.Count ||
              (s.Interior.Count == parent.Interior.Count && g.KeyOf(s.Entry).CompareTo(g.KeyOf(parent.Entry)) < 0))
            parent = s;
        }
        (parent ?? root).Children.Add(r);
      }

      // order every region's children by entry guid, so the granularity descent is deterministic.
      foreach (var region in all) region.Children.Sort((a, b) => g.KeyOf(a.Entry).CompareTo(g.KeyOf(b.Entry)));
    }

    private static string Signature(HashSet<int> set) => string.Join(",", set.OrderBy(i => i));
  }
}
