using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Dwight.Layout.Tests
{
  /// <summary>
  /// Phase 7 WP-1: the three cluster-arrangement modes (Compact / HorizontalChain / Grid). The precedence law
  /// holds in every mode - HORIZONTAL is the flow (engine, from the cluster DAG) and hints refine ONLY the
  /// vertical - and the member-block layout INSIDE each cluster is mode-independent. The distinguishing tests
  /// use a DISCONNECTED cluster graph (two independent pipelines): Grid collapses same-depth clusters into one
  /// shared column ACROSS components, and HorizontalChain stacks the components as vertically-disjoint bands -
  /// both properties Compact's 2D forest packing does not have.
  /// </summary>
  public class ArrangementModeTests
  {
    private static Guid G(int i) => new Guid(i, 0, 0, new byte[8]);
    private static LayoutNode N(int id, double w = 60, double h = 30) => new LayoutNode(id, G(id), w, h);
    private static LayoutEdge E(int a, int b) => new LayoutEdge(a, b);
    private static (string, IReadOnlyList<int>) Hint(string kind, params int[] clusters) => (kind, (IReadOnlyList<int>)clusters);

    private static LayoutOptions Opt(ArrangementMode mode) => new LayoutOptions { Arrangement = mode };

    // A deliberately WIDE target aspect forces the Compact forest-packer to place independent components
    // side-by-side (same Y band, staggered X origins). Grid and HorizontalChain ignore the aspect entirely,
    // so this is exactly the fixture where their global-column / banded arrangement diverges from Compact -
    // the distinguishing assertions below would coincide with Compact only on a stacked, symmetric packing.
    private static LayoutOptions WideOpt(ArrangementMode mode) => new LayoutOptions { Arrangement = mode, TargetAspect = 100 };

    // Two INDEPENDENT pipelines (no wire between them):
    //   component A: cluster 0 {1,2} -> cluster 1 {3,4}
    //   component B: cluster 2 {5,6} -> cluster 3 {7,8}
    // Global depth: c0 = c2 = layer 0, c1 = c3 = layer 1.
    private static (List<LayoutNode> nodes, List<LayoutEdge> edges, Dictionary<int, int> macro) TwoComponents()
    {
      var nodes = new[] { 1, 2, 3, 4, 5, 6, 7, 8 }.Select(i => N(i)).ToList();
      var edges = new List<LayoutEdge> { E(1, 2), E(3, 4), E(5, 6), E(7, 8), E(2, 3), E(6, 7) };
      var macro = new Dictionary<int, int> { [1] = 0, [2] = 0, [3] = 1, [4] = 1, [5] = 2, [6] = 2, [7] = 3, [8] = 3 };
      return (nodes, edges, macro);
    }

    // One connected fan: cluster 0 {1,2} feeds cluster 1 {3,4} AND cluster 2 {5,6}. c1,c2 are co-depth peers.
    private static (List<LayoutNode> nodes, List<LayoutEdge> edges, Dictionary<int, int> macro) Fan()
    {
      var nodes = new[] { 1, 2, 3, 4, 5, 6 }.Select(i => N(i)).ToList();
      var edges = new List<LayoutEdge> { E(1, 2), E(3, 4), E(5, 6), E(2, 3), E(2, 5) };
      var macro = new Dictionary<int, int> { [1] = 0, [2] = 0, [3] = 1, [4] = 1, [5] = 2, [6] = 2 };
      return (nodes, edges, macro);
    }

    private static double Cx(ClusterLayout.Result r, int cluster)
    {
      var b = r.Clusters.First(c => c.Id == cluster);
      return (b.MinX + b.MaxX) / 2;
    }

    private static double Cy(ClusterLayout.Result r, int cluster)
    {
      var b = r.Clusters.First(c => c.Id == cluster);
      return (b.MinY + b.MaxY) / 2;
    }

    // c0 feeds c1 AND c2 (co-depth peers), which both feed c3. ONE connected component WITH same-layer peers -
    // the non-degenerate case where grid and chain must diverge (runtime round 2).
    private static (List<LayoutNode> nodes, List<LayoutEdge> edges, Dictionary<int, int> macro) ParallelBranch()
    {
      var nodes = new[] { 1, 2, 3, 4, 5, 6, 7, 8 }.Select(i => N(i)).ToList();
      var edges = new List<LayoutEdge> { E(1, 2), E(3, 4), E(5, 6), E(7, 8), E(2, 3), E(2, 5), E(4, 7), E(6, 7) };
      var macro = new Dictionary<int, int> { [1] = 0, [2] = 0, [3] = 1, [4] = 1, [5] = 2, [6] = 2, [7] = 3, [8] = 3 };
      return (nodes, edges, macro);
    }

    // ---- Grid ---------------------------------------------------------------------------------------

    [Fact]
    public void Grid_puts_same_depth_clusters_in_one_shared_column_across_components()
    {
      var (nodes, edges, macro) = TwoComponents();
      var r = ClusterLayout.Layout(nodes, edges, macro, null, WideOpt(ArrangementMode.Grid));

      // layer-0 clusters c0 and c2 (from different components) share one column X; likewise layer-1 c1 and c3.
      Assert.Equal(Cx(r, 0), Cx(r, 2), 3);
      Assert.Equal(Cx(r, 1), Cx(r, 3), 3);
      // and flow still owns horizontal: layer 0 is left of layer 1.
      Assert.True(Cx(r, 0) < Cx(r, 1), "grid column for depth 0 must sit left of depth 1");
    }

    [Fact]
    public void Grid_hint_refines_vertical_only_never_the_flow()
    {
      var (nodes, edges, macro) = Fan();
      // valid: c1,c2 are same-layer and mutually unreachable. invalid: c0->c1 is a wire, so a stack cannot move it.
      var hints = new List<(string, IReadOnlyList<int>)> { Hint("stack", 1, 2), Hint("stack", 0, 1) };
      var r = ClusterLayout.Layout(nodes, edges, macro, hints, Opt(ArrangementMode.Grid));

      Assert.True(r.PeerPairs >= 1, "the co-depth {1,2} stack should survive");
      Assert.True(r.DroppedHintPairs >= 1, "the {0,1} stack contradicts the flow and must be dropped");
      Assert.True(Cx(r, 0) < Cx(r, 1) && Cx(r, 0) < Cx(r, 2), "flow still owns horizontal in grid mode");
    }

    // ---- HorizontalChain ----------------------------------------------------------------------------

    [Fact]
    public void HorizontalChain_stacks_disconnected_components_as_vertically_disjoint_bands()
    {
      var (nodes, edges, macro) = TwoComponents();
      var r = ClusterLayout.Layout(nodes, edges, macro, null, WideOpt(ArrangementMode.HorizontalChain));

      // component A = {c0,c1}, component B = {c2,c3}. The two bands must not share any Y span.
      var aMin = new[] { 0, 1 }.Min(c => r.Clusters.First(x => x.Id == c).MinY);
      var aMax = new[] { 0, 1 }.Max(c => r.Clusters.First(x => x.Id == c).MaxY);
      var bMin = new[] { 2, 3 }.Min(c => r.Clusters.First(x => x.Id == c).MinY);
      var bMax = new[] { 2, 3 }.Max(c => r.Clusters.First(x => x.Id == c).MaxY);
      var disjoint = aMax <= bMin + 0.5 || bMax <= aMin + 0.5;
      Assert.True(disjoint, "the two independent pipelines must form separate horizontal bands (disjoint Y)");

      // within each band the flow reads left-to-right.
      Assert.True(Cx(r, 0) < Cx(r, 1), "band A flow must read left-to-right");
      Assert.True(Cx(r, 2) < Cx(r, 3), "band B flow must read left-to-right");
    }

    [Fact]
    public void Grid_and_chain_are_distinct_on_a_parallel_branch_graph()
    {
      // Runtime round 2: the invariant is EDGE-MONOTONICITY, not equal-depth-equal-x. Grid keeps co-depth peers
      // in one column (stacked); chain linearizes the DAG into one strict left-to-right row, so the co-depth
      // peers get DISTINCT x - a provable divergence on any parallel-branch definition. (On a pure pipeline there
      // are no same-layer peers, so grid == chain there by construction - that is the degenerate case, not a bug.)
      var (nodes, edges, macro) = ParallelBranch();
      var grid = ClusterLayout.Layout(nodes, edges, macro, null, Opt(ArrangementMode.Grid));
      var chain = ClusterLayout.Layout(nodes, edges, macro, null, Opt(ArrangementMode.HorizontalChain));

      // GRID: the co-depth peers c1 and c2 share a column (same x), stacked in y.
      Assert.Equal(Cx(grid, 1), Cx(grid, 2), 3);
      Assert.NotEqual(Cy(grid, 1), Cy(grid, 2));

      // CHAIN: one strict row - the co-depth peers get DISTINCT x, and every cluster shares one y (the band).
      Assert.NotEqual(Cx(chain, 1), Cx(chain, 2));
      var ys = new[] { 0, 1, 2, 3 }.Select(c => Cy(chain, c)).ToList();
      Assert.All(ys, y => Assert.Equal(ys[0], y, 3));

      // EDGE-MONOTONE in both: c0 reaches c1/c2 reaches c3, so c0 is left of c1 is left of c3.
      Assert.True(Cx(grid, 0) < Cx(grid, 1) && Cx(grid, 1) < Cx(grid, 3));
      Assert.True(Cx(chain, 0) < Cx(chain, 1) && Cx(chain, 1) < Cx(chain, 3));
    }

    // ---- Vertical (chain's Y-twin, WP-1 RR2) --------------------------------------------------------

    [Fact]
    public void Vertical_lays_a_component_down_a_single_column()
    {
      // Vertical is chain rotated onto Y: one connected component becomes one column, topologically linearized
      // down the Y axis (if A reaches B, A is ABOVE B), so co-depth peers get distinct y at a shared x.
      var (nodes, edges, macro) = ParallelBranch();
      var v = ClusterLayout.Layout(nodes, edges, macro, null, Opt(ArrangementMode.Vertical));

      var xs = new[] { 0, 1, 2, 3 }.Select(c => Cx(v, c)).ToList();
      Assert.All(xs, x => Assert.Equal(xs[0], x, 3));       // one column: shared x
      Assert.NotEqual(Cy(v, 1), Cy(v, 2));                  // co-depth peers stacked in y
      Assert.True(Cy(v, 0) < Cy(v, 1) && Cy(v, 1) < Cy(v, 3), "edge-monotone down Y: A above B when A reaches B");
    }

    [Fact]
    public void Vertical_places_disconnected_components_as_side_by_side_columns()
    {
      var (nodes, edges, macro) = TwoComponents();
      var v = ClusterLayout.Layout(nodes, edges, macro, null, Opt(ArrangementMode.Vertical));

      var aMaxX = new[] { 0, 1 }.Max(c => v.Clusters.First(x => x.Id == c).MaxX);
      var aMinX = new[] { 0, 1 }.Min(c => v.Clusters.First(x => x.Id == c).MinX);
      var bMaxX = new[] { 2, 3 }.Max(c => v.Clusters.First(x => x.Id == c).MaxX);
      var bMinX = new[] { 2, 3 }.Min(c => v.Clusters.First(x => x.Id == c).MinX);
      Assert.True(aMaxX <= bMinX + 0.5 || bMaxX <= aMinX + 0.5, "the two pipelines must be side-by-side columns (disjoint X)");
      Assert.True(Cy(v, 0) < Cy(v, 1), "column A flow reads top-to-bottom");
      Assert.True(Cy(v, 2) < Cy(v, 3), "column B flow reads top-to-bottom");
    }

    [Fact] public void Vertical_has_no_cluster_box_overlap() => AssertNoOverlap(ArrangementMode.Vertical);
    [Fact] public void Vertical_is_deterministic() => AssertDeterministic(ArrangementMode.Vertical);

    // ---- cross-cutting invariants (all three modes) -------------------------------------------------

    // internal enum cannot be a public [Theory] parameter (CS0051), so each mode gets a [Fact] over a private
    // helper (private methods may take the internal type).
    [Fact] public void Compact_preserves_flow_order() => AssertFlowOrder(ArrangementMode.Compact);
    [Fact] public void HorizontalChain_preserves_flow_order() => AssertFlowOrder(ArrangementMode.HorizontalChain);
    [Fact] public void Grid_preserves_flow_order() => AssertFlowOrder(ArrangementMode.Grid);

    private void AssertFlowOrder(ArrangementMode mode)
    {
      var (nodes, edges, macro) = TwoComponents();
      var r = ClusterLayout.Layout(nodes, edges, macro, null, Opt(mode));
      Assert.True(Cx(r, 0) < Cx(r, 1), $"{mode}: upstream cluster 0 must sit left of cluster 1");
      Assert.True(Cx(r, 2) < Cx(r, 3), $"{mode}: upstream cluster 2 must sit left of cluster 3");
    }

    [Fact] public void Compact_has_no_cluster_box_overlap() => AssertNoOverlap(ArrangementMode.Compact);
    [Fact] public void HorizontalChain_has_no_cluster_box_overlap() => AssertNoOverlap(ArrangementMode.HorizontalChain);
    [Fact] public void Grid_has_no_cluster_box_overlap() => AssertNoOverlap(ArrangementMode.Grid);

    private void AssertNoOverlap(ArrangementMode mode)
    {
      var (nodes, edges, macro) = TwoComponents();
      var r = ClusterLayout.Layout(nodes, edges, macro, null, Opt(mode));
      for (var i = 0; i < r.Clusters.Count; i++)
        for (var j = i + 1; j < r.Clusters.Count; j++)
        {
          var a = r.Clusters[i]; var b = r.Clusters[j];
          var sepX = a.MaxX <= b.MinX + 0.5 || b.MaxX <= a.MinX + 0.5;
          var sepY = a.MaxY <= b.MinY + 0.5 || b.MaxY <= a.MinY + 0.5;
          Assert.True(sepX || sepY, $"{mode}: cluster boxes {a.Id} and {b.Id} overlap");
        }
    }

    [Fact] public void Compact_is_deterministic() => AssertDeterministic(ArrangementMode.Compact);
    [Fact] public void HorizontalChain_is_deterministic() => AssertDeterministic(ArrangementMode.HorizontalChain);
    [Fact] public void Grid_is_deterministic() => AssertDeterministic(ArrangementMode.Grid);

    private void AssertDeterministic(ArrangementMode mode)
    {
      var (nodes, edges, macro) = TwoComponents();
      var a = ClusterLayout.Layout(nodes, edges, macro, null, Opt(mode));
      var b = ClusterLayout.Layout(nodes, edges, macro, null, Opt(mode));
      foreach (var kv in a.Centers)
      {
        Assert.Equal(kv.Value.X, b.Centers[kv.Key].X);
        Assert.Equal(kv.Value.Y, b.Centers[kv.Key].Y);
      }
    }

    [Fact]
    public void Intra_cluster_member_geometry_is_identical_across_modes()
    {
      // The member-block layout INSIDE a cluster is mode-independent: the relative position of two members in
      // the same cluster is the same vector in every arrangement. Only the cluster BLOCKS move between modes.
      var (nodes, edges, macro) = TwoComponents();
      var modes = new[] { ArrangementMode.Compact, ArrangementMode.HorizontalChain, ArrangementMode.Vertical, ArrangementMode.Grid };
      var results = modes.ToDictionary(m => m, m => ClusterLayout.Layout(nodes, edges, macro, null, Opt(m)));

      // members 1 and 2 both live in cluster 0; their relative offset must not depend on the mode.
      var baseline = results[ArrangementMode.Compact];
      var refDx = baseline.Centers[1].X - baseline.Centers[2].X;
      var refDy = baseline.Centers[1].Y - baseline.Centers[2].Y;
      foreach (var m in modes)
      {
        var r = results[m];
        Assert.Equal(refDx, r.Centers[1].X - r.Centers[2].X, 6);
        Assert.Equal(refDy, r.Centers[1].Y - r.Centers[2].Y, 6);
      }
    }
  }
}
