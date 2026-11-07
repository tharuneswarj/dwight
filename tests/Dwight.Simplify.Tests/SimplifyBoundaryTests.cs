using System.Collections.Generic;
using System.Linq;
using Dwight.Data;
using Xunit;

namespace Dwight.Simplify.Tests
{
  /// <summary>
  /// The Simplify boundary-crossing ordering (Stage 5 3/3). This is the CORRECTNESS HINGE of ref resolution: the
  /// proposal's <c>i&lt;n&gt;</c>/<c>o&lt;n&gt;</c> tokens index the boundary crossings in exactly the order the
  /// payload (<c>Payloads.Simplify</c>) emitted them, so the ext index the model saw resolves to the same crossing
  /// the resolve builder wires. Both the payload and the resolve builder derive their ordering from this one
  /// helper, so identity is by construction - these tests pin the contract it must hold ("assert it, don't
  /// assume it"): OrderBy(node).ThenBy(param), first-edge grouping, internal edges excluded.
  /// </summary>
  public class SimplifyBoundaryTests
  {
    private static SimplifyBoundary.Edge E(int fromNode, string fromParam, int toNode, string toParam)
      => new SimplifyBoundary.Edge(fromNode, fromParam, toNode, toParam);
    private static ISet<int> Sel(params int[] xs) => new HashSet<int>(xs);

    // ---- inputs: crossings INTO the selection, ordered by (ToNode, ToParam) ----

    [Fact]
    public void Input_crossings_are_ordered_by_target_node_then_param()
    {
      // selection = {10, 11}. External sources 1, 2, 3 feed selection inputs at different (node, param) ports.
      // Ordering must be OrderBy(ToNode).ThenBy(ToParam): (10,"N"), (10,"R"), (11,"A").
      var edges = new[]
      {
        E(3, "out", 11, "A"),   // -> (11, A)
        E(1, "out", 10, "R"),   // -> (10, R)
        E(2, "out", 10, "N"),   // -> (10, N)
      };
      var ins = SimplifyBoundary.Inputs(edges, Sel(10, 11));
      Assert.Equal(new[] { (10, "N"), (10, "R"), (11, "A") },
        ins.Select(c => (c.Node, c.Param)).ToArray());
      // ext index = list position, and the source (Edges[0].FromNode) is preserved per crossing.
      Assert.Equal(2, ins[0].Edges[0].FromNode);   // i0 -> external source 2
      Assert.Equal(1, ins[1].Edges[0].FromNode);   // i1 -> external source 1
      Assert.Equal(3, ins[2].Edges[0].FromNode);   // i2 -> external source 3
    }

    [Fact]
    public void Two_external_sources_into_one_port_are_one_crossing_first_source_wins()
    {
      // A selection input fed by TWO external sources (5 then 6) is ONE boundary crossing; Edges keeps encounter
      // order, so Edges[0] is the first-encountered source - matching the payload's first-edge-wins sig.
      var edges = new[]
      {
        E(5, "out", 10, "X"),
        E(6, "out", 10, "X"),
      };
      var ins = SimplifyBoundary.Inputs(edges, Sel(10));
      Assert.Single(ins);
      Assert.Equal(2, ins[0].Edges.Count);
      Assert.Equal(5, ins[0].Edges[0].FromNode);   // first-encountered source is the resolved i<ext> source
    }

    // ---- outputs: crossings OUT of the selection, ordered by (FromNode, FromParam) ----

    [Fact]
    public void Output_crossings_are_ordered_by_source_node_then_param()
    {
      var edges = new[]
      {
        E(11, "out", 90, "in"),   // (11, out)
        E(10, "B",   91, "in"),   // (10, B)
        E(10, "A",   92, "in"),   // (10, A)
      };
      var outs = SimplifyBoundary.Outputs(edges, Sel(10, 11));
      Assert.Equal(new[] { (10, "A"), (10, "B"), (11, "out") },
        outs.Select(c => (c.Node, c.Param)).ToArray());
    }

    [Fact]
    public void One_output_feeding_many_external_consumers_is_one_crossing_with_all_consumers()
    {
      // o<ext> must resolve to EVERY external consumer of that producer port (ExtOut is a list).
      var edges = new[]
      {
        E(10, "out", 90, "P"),
        E(10, "out", 91, "Q"),
      };
      var outs = SimplifyBoundary.Outputs(edges, Sel(10));
      Assert.Single(outs);
      Assert.Equal(new[] { (90, "P"), (91, "Q") },
        outs[0].Edges.Select(e => (e.ToNode, e.ToParam)).OrderBy(t => t.Item1).ToArray());
    }

    // ---- internal + irrelevant edges are excluded from both ----

    [Fact]
    public void Internal_edges_are_neither_input_nor_output_crossings()
    {
      // 10 -> 11 is wholly inside the selection: not a boundary crossing on either side.
      var edges = new[] { E(10, "out", 11, "in") };
      Assert.Empty(SimplifyBoundary.Inputs(edges, Sel(10, 11)));
      Assert.Empty(SimplifyBoundary.Outputs(edges, Sel(10, 11)));
    }

    [Fact]
    public void A_fully_external_edge_is_ignored()
    {
      var edges = new[] { E(1, "out", 2, "in") };   // neither endpoint in the selection
      Assert.Empty(SimplifyBoundary.Inputs(edges, Sel(10)));
      Assert.Empty(SimplifyBoundary.Outputs(edges, Sel(10)));
    }
  }
}
