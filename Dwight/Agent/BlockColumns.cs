using System;
using System.Collections.Generic;
using System.Linq;

namespace Dwight.Agent
{
  /// <summary>Where a component stands in a block Dwight lays down. The two "below kept" roles are a new input or output on a rebuild whose column already stands: it goes under that column and is not packed with the block.</summary>
  internal enum BlockRole { Middle, Input, Output, InputBelowKept, OutputBelowKept }

  /// <summary>What the live wiring and the object's own shape say about one component of a block. Read after the wires are made, by the shim (<c>GhBlockRoles</c>), so a test can state each case.</summary>
  internal sealed class BlockNode
  {
    /// <summary>It is a parameter at the top level of the canvas: a slider, a panel, a toggle, a value list, a floating param.</summary>
    public bool Holder;

    /// <summary>It can take no wire in: a slider, a toggle, a value list.</summary>
    public bool SourceOnly;

    /// <summary>It has nothing to wire out: a Custom Preview.</summary>
    public bool Sink;

    public bool FedInside, FedOutside, FeedsInside, FeedsOutside;
  }

  /// <summary>One wire to another component of the block: its place in the list, and which of its ports the wire reaches.</summary>
  internal readonly struct BlockWire
  {
    public BlockWire(int partner, int port) { Partner = partner; Port = port; }
    public int Partner { get; }
    public int Port { get; }
  }

  /// <summary>Who reads a component and who feeds it, inside the block. Read after the wires are made, by the shim (<c>GhBlockRoles</c>).</summary>
  internal sealed class BlockWires
  {
    public IReadOnlyList<BlockWire> Readers = new BlockWire[0];
    public IReadOnlyList<BlockWire> Feeders = new BlockWire[0];
  }

  /// <summary>
  /// INPUTS STAND IN A COLUMN, OUTPUTS STAND IN A COLUMN, NEVER IN A ROW (Brief DP, Tharun's ruling of 1 November 2026: "All the inputs go in vertically only; all the outputs also come in vertically"). Reorganise's boundary
  /// columns (WP-D, ADR-085) are the model, applied to everything Dwight places. The classes come from the wiring, never from a type name alone: a Panel fed from inside the block and read by nothing is an output.
  ///
  /// THE CASES THE WIRING ALONE CANNOT SETTLE. A component with no wires at all feeds nothing and is fed by nothing. A slider, a toggle or a value list can only ever be a source, so it is an input however early it is placed;
  /// a Custom Preview can only ever be a sink, so it is an output. A Panel or a computing component with no wires is neither yet, so it stays in the middle. A computing component with nothing inside feeding it stays in the
  /// middle too: it is where the flow starts, and it holds no value of its own for a person to set.
  ///
  /// GH-free (ADR-091): the shim reads and moves, this decides.
  /// </summary>
  internal static class BlockColumns
  {
    /// <summary>
    /// How much taller than the middle a column may stand before it wraps. The tidy's constants hold no such bound, and the nearest, <c>BlockBuffer</c> at 120, would wrap the brief's own column of six sliders beside two
    /// consumers. So it comes from the cap: a placement without a map holds twelve, and twelve sliders at 20 high with the row gap stand 438, so no card-sized column ever wraps, even with nothing beside it. Only a map
    /// region with a long list of inputs can reach this.
    /// </summary>
    public const double WrapBound = 480;

    public static BlockRole RoleOf(BlockNode n)
    {
      if (n.Holder && !n.FedInside && (n.FeedsInside || n.SourceOnly)) return BlockRole.Input;
      if (!n.FeedsInside && (n.Sink || n.FedInside || n.FedOutside || n.FeedsOutside)) return BlockRole.Output;
      return BlockRole.Middle;
    }

    /// <summary>A rebuild keeps the columns: a new input or output goes under a column that already stands, and only where there is one.</summary>
    public static List<BlockRole> WithKept(IEnumerable<BlockRole> roles, bool keptInputs, bool keptOutputs) =>
      roles.Select(r => r == BlockRole.Input && keptInputs ? BlockRole.InputBelowKept : r == BlockRole.Output && keptOutputs ? BlockRole.OutputBelowKept : r).ToList();

    /// <summary>Fill columns top to bottom, in the order given, up to <paramref name="limit"/>. The first in a column always goes there, so what is taller than the limit stands alone and is never refused.</summary>
    public static List<List<int>> Stack(IReadOnlyList<int> order, IReadOnlyList<SpaceBox> measured, double limit)
    {
      var columns = new List<List<int>>();
      List<int> column = null;
      double y = 0;
      foreach (var i in order)
      {
        var bottom = column == null ? measured[i].H : y + SpotLayout.RowGap + measured[i].H;
        if (column == null || bottom > limit + 0.001)
        {
          columns.Add(column = new List<int>());
          bottom = measured[i].H;
        }
        column.Add(i);
        y = bottom;
      }
      return columns;
    }

    /// <summary>How wide and how tall a side's columns stand: each as wide as its widest member, the tidy's column gap between them.</summary>
    public static (double W, double H) Size(IReadOnlyList<List<int>> columns, IReadOnlyList<SpaceBox> measured)
    {
      double w = 0, h = 0;
      for (var c = 0; c < columns.Count; c++)
      {
        w += columns[c].Max(i => measured[i].W) + (c > 0 ? SpotLayout.ColumnGap : 0);
        h = Math.Max(h, columns[c].Sum(i => measured[i].H) + SpotLayout.RowGap * (columns[c].Count - 1));
      }
      return (w, h);
    }

    /// <summary>Put a side's columns down from (<paramref name="x"/>, <paramref name="top"/>), each centred on the block's height, as Reorganise's boundary columns are centred on the body.</summary>
    public static void Place(IReadOnlyList<List<int>> columns, IReadOnlyList<SpaceBox> measured, double x, double top, double blockH, SpaceBox[] places)
    {
      foreach (var column in columns)
      {
        var h = column.Sum(i => measured[i].H) + SpotLayout.RowGap * (column.Count - 1);
        var y = top + Math.Floor((blockH - h) / 2);
        foreach (var i in column)
        {
          places[i] = new SpaceBox("", x, y, measured[i].W, measured[i].H, SpaceRole.Fixed);
          y += measured[i].H + SpotLayout.RowGap;
        }
        x += column.Max(i => measured[i].W) + SpotLayout.ColumnGap;
      }
    }

    /// <summary>
    /// A MAP REGION'S BLOCK: its middle keeps the tidy's flow, moved only as a whole, and its inputs and outputs stand either side of it, with the block keeping the top-left it had, because a tidy arranges inside a region and never
    /// relocates it. What a rebuild kept comes first (<paramref name="keptAtY"/> holds where each stood, and null for what is new), so a new one takes the next place below.
    ///
    /// THE COLUMN'S ORDER IS THE FLOW'S, READ FROM ITS WIRES, NEVER FROM WHERE THE ENGINE PUT THE COLUMN'S OWN MEMBERS (the twelfth gate: a column read b, a, c, d). The engine packs what is not wired by guid, and it stacks what feeds one
    /// component with no knowledge of its ports, so a member's own height changes from build to build and says nothing. What the flow does know is where the wires go. So an input with a wire inside stands by the component it feeds,
    /// topmost first, then by that component's port, which keeps the wires from crossing, and an output the same way by what feeds it. What has no wire inside follows, in the order the card placed them, and that order settles any tie.
    /// </summary>
    public static List<SpaceBox> AroundFlow(IReadOnlyList<SpaceBox> flow, IReadOnlyList<BlockRole> roles, IReadOnlyList<double?> keptAtY = null, IReadOnlyList<BlockWires> wires = null)
    {
      var all = Enumerable.Range(0, flow.Count).ToList();
      if (all.Count == 0) return new List<SpaceBox>();
      var origin = Landing.Extent(flow);
      var middle = all.Where(i => roles[i] == BlockRole.Middle).ToList();
      var body = Landing.Extent(middle.Select(i => flow[i]));
      var bodyH = body?.H ?? 0;
      var bodyW = body?.W ?? 0;

      // LINQ's order is stable, so what no key separates stays in the card's order.
      List<int> InOrder(IEnumerable<int> side, Func<BlockWires, IReadOnlyList<BlockWire>> across)
      {
        BlockWire? First(int i)
        {
          var far = wires == null ? null : across(wires[i]);
          if (far == null || far.Count == 0) return null;
          return far.OrderBy(w => flow[w.Partner].Y).ThenBy(w => w.Partner).ThenBy(w => w.Port).First();
        }

        return side
          .Select(i => (I: i, Wire: First(i)))
          .OrderBy(s => keptAtY?[s.I] == null ? 1 : 0)
          .ThenBy(s => keptAtY?[s.I] ?? 0)
          .ThenBy(s => s.Wire == null ? 1 : 0)
          .ThenBy(s => s.Wire == null ? 0 : flow[s.Wire.Value.Partner].Y)
          .ThenBy(s => s.Wire?.Partner ?? 0)
          .ThenBy(s => s.Wire?.Port ?? 0)
          .Select(s => s.I)
          .ToList();
      }

      var limit = bodyH + WrapBound;
      var left = Stack(InOrder(all.Where(i => IsInput(roles[i])), w => w.Readers), flow, limit);
      var right = Stack(InOrder(all.Where(i => IsOutput(roles[i])), w => w.Feeders), flow, limit);
      var (leftW, leftH) = Size(left, flow);
      var (_, rightH) = Size(right, flow);
      var blockH = Math.Max(bodyH, Math.Max(leftH, rightH));

      var places = new SpaceBox[flow.Count];
      Place(left, flow, origin.X, origin.Y, blockH, places);

      var bodyX = origin.X + leftW + (left.Count > 0 && middle.Count > 0 ? SpotLayout.ColumnGap : 0);
      if (body != null)
      {
        var dx = bodyX - body.X;
        var dy = origin.Y + Math.Floor((blockH - bodyH) / 2) - body.Y;
        foreach (var i in middle) places[i] = flow[i].At(dx, dy);
      }

      var rightX = bodyX + bodyW + ((left.Count > 0 || middle.Count > 0) && right.Count > 0 ? SpotLayout.ColumnGap : 0);
      Place(right, flow, rightX, origin.Y, blockH, places);
      return places.ToList();
    }

    public static bool IsInput(BlockRole role) => role == BlockRole.Input || role == BlockRole.InputBelowKept;

    public static bool IsOutput(BlockRole role) => role == BlockRole.Output || role == BlockRole.OutputBelowKept;

    /// <summary>The next free place at or below <paramref name="y"/> in a column: clear of everything by the gaps the law asks for. A column grows downward, so this only ever looks down.</summary>
    public static double NextFreeBelow(double x, double y, double w, double h, IReadOnlyList<SpaceBox> obstacles)
    {
      var all = obstacles ?? new SpaceBox[0];
      for (var tries = 0; tries <= all.Count; tries++)
      {
        var at = y;
        var hit = all.Where(o => o.X < x + w + SpotLayout.ColumnGap && x < o.Right + SpotLayout.ColumnGap && o.Y < at + h + SpotLayout.RowGap && at < o.Bottom + SpotLayout.RowGap)
          .OrderBy(o => o.Bottom).FirstOrDefault();
        if (hit == null) return y;
        y = hit.Bottom + SpotLayout.RowGap;
      }
      return y;
    }
  }
}
