using System.Collections.Generic;
using Dwight.Simplify;
using Xunit;

namespace Dwight.Simplify.Tests
{
  /// <summary>
  /// Layer 2 compares only the PRESERVED output surface (RR4 #3). The load-bearing case is the round-4 trap: a
  /// dead terminal producer that the plan removes must be EXCLUDED from the compare, or removing it - a correct,
  /// intended change - would falsely revert. Kept producers' outputs are compared; a boundary consumer re-fed by
  /// a rewire is compared; an output owned solely by a removal is not.
  /// </summary>
  public class Layer2SurfaceTests
  {
    private static OutputPort Terminal(int producer) => new OutputPort(producer, true, -1);
    private static OutputPort Boundary(int producer, int consumer) => new OutputPort(producer, false, consumer);
    private static ISet<int> Set(params int[] xs) => new HashSet<int>(xs);

    [Fact]
    public void A_terminal_output_of_a_removed_producer_is_excluded()
    {
      // "removed dead Sort List (154) as its outputs are unused" - a terminal producer the plan removes. Its
      // output must not be in the compare, or the correct removal falsely reverts.
      var surface = new[] { Terminal(154), Terminal(10) };
      var preserved = Layer2Surface.Preserved(surface, keep: Set(10), rewiredConsumers: Set());
      Assert.DoesNotContain(preserved, p => p.ProducerId == 154);
      Assert.Contains(preserved, p => p.ProducerId == 10);   // the kept terminal output stays in the compare
    }

    [Fact]
    public void A_boundary_output_of_a_kept_producer_is_compared()
    {
      var surface = new[] { Boundary(5, 200) };
      var preserved = Layer2Surface.Preserved(surface, keep: Set(5), rewiredConsumers: Set());
      Assert.Contains(preserved, p => p.ProducerId == 5);
    }

    [Fact]
    public void A_boundary_output_of_a_removed_producer_with_no_rewire_is_excluded()
    {
      var surface = new[] { Boundary(5, 200) };
      var preserved = Layer2Surface.Preserved(surface, keep: Set(), rewiredConsumers: Set());
      Assert.Empty(preserved);
    }

    [Fact]
    public void A_boundary_consumer_re_fed_by_a_rewire_is_compared()
    {
      // the producer (5) is removed, but a rewire re-feeds the external consumer (200) from a new/kept source -
      // so the consumer still receives data and belongs in the compare.
      var surface = new[] { Boundary(5, 200) };
      var preserved = Layer2Surface.Preserved(surface, keep: Set(), rewiredConsumers: Set(200));
      Assert.Contains(preserved, p => p.ConsumerKey == 200);
    }
  }
}
