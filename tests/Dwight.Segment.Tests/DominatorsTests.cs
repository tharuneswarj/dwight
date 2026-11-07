using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Dwight.Segment.Tests
{
  /// <summary>
  /// Tests the dominator / post-dominator foundation the RPST is built on. If dominance is wrong, every SESE
  /// boundary above it is wrong, so these pin the well-defined cases (a diamond, a sequence) against
  /// hand-computed answers.
  /// </summary>
  public class DominatorsTests
  {
    private static Guid G(int i) => new Guid(i, 0, 0, new byte[8]);

    private static DataflowGraph Graph(int[] ids, params (int a, int b)[] edges)
    {
      var nodes = ids.Select(i => new SegNode(i, G(i), false)).ToList();
      var es = edges.Select(e => new SegEdge(e.a, e.b)).ToList();
      return DataflowGraph.Build(new SegInput(nodes, es), new HashSet<int>());
    }

    [Fact]
    public void Diamond_dominators_and_post_dominators_are_correct()
    {
      // 1 -> 2, 1 -> 3, 2 -> 4, 3 -> 4  (Source -> 1, 4 -> Sink added)
      var g = Graph(new[] { 1, 2, 3, 4 }, (1, 2), (1, 3), (2, 4), (3, 4));
      var idom = Dominators.Compute(g, reverse: false);
      var ipdom = Dominators.Compute(g, reverse: true);

      Assert.Equal(DataflowGraph.Source, idom[1]);
      Assert.Equal(1, idom[2]);
      Assert.Equal(1, idom[3]);
      Assert.Equal(1, idom[4]);          // both branches reconverge, so 4's immediate dominator is 1

      Assert.Equal(DataflowGraph.Sink, ipdom[4]);
      Assert.Equal(4, ipdom[1]);          // every path from 1 reconverges at 4
      Assert.Equal(4, ipdom[2]);
      Assert.Equal(4, ipdom[3]);
    }

    [Fact]
    public void Sequence_dominators_chain()
    {
      var g = Graph(new[] { 1, 2, 3 }, (1, 2), (2, 3));
      var idom = Dominators.Compute(g, reverse: false);
      var ipdom = Dominators.Compute(g, reverse: true);

      Assert.Equal(DataflowGraph.Source, idom[1]);
      Assert.Equal(1, idom[2]);
      Assert.Equal(2, idom[3]);

      Assert.Equal(2, ipdom[1]);
      Assert.Equal(3, ipdom[2]);
      Assert.Equal(DataflowGraph.Sink, ipdom[3]);
    }

    [Fact]
    public void Dominators_are_deterministic()
    {
      var g = Graph(new[] { 1, 2, 3, 4, 5 }, (1, 2), (1, 3), (2, 4), (3, 4), (4, 5));
      var a = Dominators.Compute(g, reverse: false);
      var b = Dominators.Compute(g, reverse: false);
      Assert.Equal(a.Count, b.Count);
      foreach (var kv in a) Assert.Equal(kv.Value, b[kv.Key]);
    }
  }
}
