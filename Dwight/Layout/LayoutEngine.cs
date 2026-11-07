using System;
using System.Collections.Generic;
using System.Linq;

namespace Dwight.Layout
{
  /// <summary>
  /// The deterministic layout engine (Phase 6 WP-A, ADR-063). It owns everything around the swappable
  /// layered core (<see cref="ILayoutAlgorithm"/>):
  ///   1. split the graph into weakly-connected components (a 6,000-component definition is a forest, not
  ///      one DAG);
  ///   2. lay each component out left-to-right via the core (MSAGL), fed real measured sizes;
  ///   3. pack the component bounding boxes in 2D toward a screen-friendly aspect, with the ~200px buffer,
  ///      so a tall forest becomes a grid of blocks, never one impossibly-wide strip;
  ///   4. compute wire-fade recommendations from the final geometry (the 500/1000px norm).
  /// Everything is deterministic - components ordered by min id, blocks packed in a fixed order, the core
  /// proven byte-identical - so an identical graph re-lays identically and the user's spatial memory holds.
  ///
  /// Headless by design (no Grasshopper, no Rhino): the GH adapter feeds it plain ids + sizes, so the whole
  /// engine is unit-tested outside the host.
  /// </summary>
  internal sealed class LayoutEngine
  {
    private readonly ILayoutAlgorithm _algorithm;

    public LayoutEngine(ILayoutAlgorithm algorithm)
    {
      _algorithm = algorithm ?? throw new ArgumentNullException(nameof(algorithm));
    }

    /// <summary>The committed engine: MSAGL behind the seam (WP-0).</summary>
    public static LayoutEngine Msagl() => new LayoutEngine(new MsaglLayout());

    public LayoutResult Layout(IReadOnlyList<LayoutNode> nodes, IReadOnlyList<LayoutEdge> edges, LayoutOptions options = null)
    {
      options = options ?? LayoutOptions.Default;
      edges = edges ?? Array.Empty<LayoutEdge>();
      var result = new LayoutResult();
      if (nodes == null || nodes.Count == 0) return result;

      var byId = new Dictionary<int, LayoutNode>();
      var keyById = new Dictionary<int, Guid>();
      foreach (var n in nodes) { byId[n.Id] = n; keyById[n.Id] = n.Key; }   // last-wins on a duplicate id

      // 1. weakly-connected components (edges treated undirected), ordered by stable key (guid).
      var components = ConnectedComponents(byId.Keys, edges, keyById);

      // 2. lay each component out locally and box it.
      var blocks = new List<Block>(components.Count);
      foreach (var comp in components)
      {
        var compSet = new HashSet<int>(comp);
        var compNodes = comp.Select(id => byId[id]).ToList();
        var compEdges = edges.Where(e => compSet.Contains(e.FromId) && compSet.Contains(e.ToId)).ToList();
        var local = _algorithm.LayoutBlock(compNodes, compEdges, options);
        blocks.Add(Block.From(comp, compNodes, local, keyById));
      }
      result.BlockCount = blocks.Count;

      // 3. pack the blocks in 2D (deterministic, tie-broken by stable key).
      PackBlocks(blocks, options);

      // 4. emit final canvas centres (block offset + local, y-flipped within the block).
      foreach (var b in blocks)
        foreach (var id in b.Ids)
          result.Centers[id] = b.ToCanvas(b.LocalCenters[id], options);

      // 5. wire-fade recommendations from the final geometry.
      foreach (var f in ComputeFades(edges, result.Centers, byId, keyById, options))
        result.Fades.Add(f);

      return result;
    }

    // ---- 1. connected components ---------------------------------------------------------------------

    /// <summary>Union-find over the node set with edges treated as undirected. Deterministic and stable
    /// across re-layouts: each member list is sorted by stable key (guid), and components are returned
    /// ordered by their minimum key - NOT by the pivot-derived id, which churns when the layout moves
    /// things. The union-find root is an internal representative only; it does not affect the output order.</summary>
    private static List<List<int>> ConnectedComponents(IEnumerable<int> nodeIds, IReadOnlyList<LayoutEdge> edges, Dictionary<int, Guid> keyById)
    {
      var parent = new Dictionary<int, int>();
      foreach (var id in nodeIds) parent[id] = id;

      int Find(int x)
      {
        while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
        return x;
      }
      void Union(int a, int b)
      {
        var ra = Find(a); var rb = Find(b);
        if (ra == rb) return;
        if (ra < rb) parent[rb] = ra; else parent[ra] = rb;   // union to the smaller id (deterministic)
      }

      foreach (var e in edges)
        if (parent.ContainsKey(e.FromId) && parent.ContainsKey(e.ToId)) Union(e.FromId, e.ToId);

      var groups = new Dictionary<int, List<int>>();
      foreach (var id in parent.Keys.OrderBy(i => keyById[i]))   // stable iteration (guid order)
      {
        var r = Find(id);
        if (!groups.TryGetValue(r, out var list)) groups[r] = list = new List<int>();
        list.Add(id);
      }
      return groups.Values
        .Select(g => g.OrderBy(i => keyById[i]).ToList())       // members sorted by stable key
        .OrderBy(g => keyById[g[0]])                            // components ordered by their minimum key
        .ToList();
    }

    // ---- 3. 2D forest packing ------------------------------------------------------------------------

    /// <summary>Shelf-pack the block boxes toward the target aspect ratio: rows of blocks filled left to
    /// right until the row would exceed a target width derived from total area and aspect, then a new row
    /// below. Deterministic order (tallest first, ties by min id) so the packing is reproducible. A single
    /// block (the common case for one wired definition) just sits at the origin.</summary>
    private static void PackBlocks(List<Block> blocks, LayoutOptions o)
    {
      if (blocks.Count == 0) return;
      if (blocks.Count == 1) { blocks[0].OffsetX = 0; blocks[0].OffsetY = 0; return; }

      var totalArea = blocks.Sum(b => (b.Width + o.BlockBuffer) * (b.Height + o.BlockBuffer));
      var targetRowWidth = Math.Sqrt(Math.Max(1.0, totalArea) * o.TargetAspect);

      var ordered = blocks.OrderByDescending(b => b.Height).ThenBy(b => b.MinKey).ToList();   // stable tie-break

      double cursorX = 0, cursorY = 0, shelfHeight = 0;
      foreach (var b in ordered)
      {
        if (cursorX > 0 && cursorX + b.Width > targetRowWidth)   // wrap to the next shelf
        {
          cursorX = 0;
          cursorY += shelfHeight + o.BlockBuffer;
          shelfHeight = 0;
        }
        b.OffsetX = cursorX;
        b.OffsetY = cursorY;
        cursorX += b.Width + o.BlockBuffer;
        shelfHeight = Math.Max(shelfHeight, b.Height);
      }
    }

    // ---- 4. wire-fade --------------------------------------------------------------------------------

    /// <summary>The 500/1000px norm (research/Bedrock): a wire's length is the gap from the source's output
    /// grip (~right edge) to the target's input grip (~left edge). Only non-default recommendations are
    /// emitted, in deterministic (from,to) order.</summary>
    private static IEnumerable<WireFade> ComputeFades(
      IReadOnlyList<LayoutEdge> edges, Dictionary<int, PointD> centers, Dictionary<int, LayoutNode> byId,
      Dictionary<int, Guid> keyById, LayoutOptions o)
    {
      var seen = new HashSet<(int, int)>();   // WP-4.5: one fade per node pair, and never a self-loop
      foreach (var e in edges
                 .Where(e => keyById.ContainsKey(e.FromId) && keyById.ContainsKey(e.ToId))
                 .OrderBy(e => keyById[e.FromId]).ThenBy(e => keyById[e.ToId]))   // stable (guid) order
      {
        if (e.FromId == e.ToId || !seen.Add((e.FromId, e.ToId))) continue;
        if (!centers.TryGetValue(e.FromId, out var cf) || !centers.TryGetValue(e.ToId, out var ct)) continue;
        if (!byId.TryGetValue(e.FromId, out var nf) || !byId.TryGetValue(e.ToId, out var nt)) continue;

        var sx = cf.X + nf.Width / 2.0;   // source output grip ~ right edge
        var tx = ct.X - nt.Width / 2.0;   // target input grip ~ left edge
        var dx = tx - sx;
        var dy = ct.Y - cf.Y;
        var len = Math.Sqrt(dx * dx + dy * dy);

        var display = FadeFor(len, o);
        if (display != WireDisplay.Default)
          yield return new WireFade(e.FromId, e.ToId, display, len);
      }
    }

    /// <summary>The threshold mapping (exposed for unit testing): &gt;= hidden -&gt; Hidden, &gt;= faint -&gt; Faint,
    /// else Default.</summary>
    internal static WireDisplay FadeFor(double length, LayoutOptions o)
    {
      if (length >= o.HiddenThreshold) return WireDisplay.Hidden;
      if (length >= o.FaintThreshold) return WireDisplay.Faint;
      return WireDisplay.Default;
    }

    // ---- a packed block ------------------------------------------------------------------------------

    /// <summary>One weakly-connected component after local layout: its members, their local centres, the
    /// local bounding box (inflated by node sizes), and the offset assigned by packing.</summary>
    private sealed class Block
    {
      public List<int> Ids;
      public Dictionary<int, PointD> LocalCenters;
      public double MinX, MinY, MaxX, MaxY;   // local bounds, including node half-sizes
      public Guid MinKey;                      // the block's minimum stable key (guid) - the packing tie-break
      public double OffsetX, OffsetY;          // canvas position of the block's top-left, set by packing

      public double Width => MaxX - MinX;
      public double Height => MaxY - MinY;

      public static Block From(List<int> ids, List<LayoutNode> nodes, IReadOnlyDictionary<int, PointD> local, Dictionary<int, Guid> keyById)
      {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var n in nodes)
        {
          var c = local[n.Id];
          minX = Math.Min(minX, c.X - n.Width / 2.0);
          maxX = Math.Max(maxX, c.X + n.Width / 2.0);
          minY = Math.Min(minY, c.Y - n.Height / 2.0);
          maxY = Math.Max(maxY, c.Y + n.Height / 2.0);
        }
        return new Block
        {
          Ids = ids,
          LocalCenters = local.ToDictionary(kv => kv.Key, kv => kv.Value),
          MinX = minX, MinY = minY, MaxX = maxX, MaxY = maxY,
          MinKey = ids.Count > 0 ? ids.Select(id => keyById[id]).Min() : Guid.Empty,
        };
      }

      /// <summary>Map a local centre to canvas space: translate to block-relative, flip y (MSAGL is y-up,
      /// GH is y-down), then add the packing offset.</summary>
      public PointD ToCanvas(PointD localCenter, LayoutOptions o)
      {
        var relX = localCenter.X - MinX;
        var relY = localCenter.Y - MinY;
        if (o.FlipY) relY = Height - relY;
        return new PointD(OffsetX + relX, OffsetY + relY);
      }
    }
  }
}
