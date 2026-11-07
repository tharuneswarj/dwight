using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Dwight.Layout;

namespace Dwight.Agent
{
  /// <summary>A block as packed, with no place on the canvas yet.</summary>
  internal sealed class SpotBlock
  {
    /// <summary>Each component's box from the block's top-left, in the order they were asked for. Null for a new input or output a rebuild puts under its kept column (Brief DP), which is not part of this block.</summary>
    public IReadOnlyList<SpaceBox> Places = new SpaceBox[0];

    /// <summary>The block's footprint, which is what the search for a clear place reads.</summary>
    public double W, H;

    public int Rows;

    /// <summary>Brief DP: how many columns the longer side took. One unless a column wrapped, and none with no input or output.</summary>
    public int Columns;

    /// <summary>Brief DP: what each component is, read from the wiring once, which a rerun keeps. Null is all middle.</summary>
    public IReadOnlyList<BlockRole> Roles;

    /// <summary>The width its rows were filled to, which is what a rerun widens.</summary>
    public double Width;
  }

  /// <summary>
  /// A SPOT BLOCK IS LAID OUT FROM MEASURED BOUNDS, SO NOTHING IT PLACES OVERLAPS (Brief DK; ADR-056).
  ///
  /// The tenth gate measured the old grid: nine components landed six and three, the 150-wide sliders 140 apart, five pairs overlapping by 10 units and the eighth on the tall
  /// Panel. It was computed from a count before anything existed. A spot has no tidy after it, so this is the only layout it ever gets.
  ///
  /// THE WIDTH. The tidy's constants hold no row width, so a block's is six components' worth: the six widest in this block, side by side with the gap between. Any six then fit on a
  /// row, so a block never takes more than one row per six, however long its nicknames run, and one wide Panel always fits on some row without stretching the others apart.
  ///
  /// THE COLUMNS (Brief DP). Inputs stand in a column at the block's left and outputs in one at its right (<see cref="BlockColumns"/>). The rows are the middle's, and the width is the middle's.
  ///
  /// THE LAW. Every pair of placed bounds is apart by at least the gap (<see cref="Disjoint"/>). It cannot fail straight after a measured pack, and it is checked regardless, off what
  /// actually stands, because a component can change size afterwards: a Panel takes its size from its text on one later layout. A failure reruns once, from the sizes as they now are,
  /// with a wider block. That is once for each time the law is read, here and after the solve, so a component that keeps changing can never loop.
  ///
  /// GH-free (ADR-091): the shim measures and moves, this decides.
  /// </summary>
  internal static class SpotLayout
  {
    /// <summary>How many components' worth a block is wide. It was the old grid's row, and it stays what a row can always hold.</summary>
    public const int RowOf = 6;

    /// <summary>The tidy's own gap between neighbours along the way a definition reads.</summary>
    public static double ColumnGap => Tidy.LayerSeparation;

    /// <summary>The tidy's own gap between what is stacked.</summary>
    public static double RowGap => Tidy.NodeSeparation;

    /// <summary>What the law allows a pair for Grasshopper's rounding. A component's layout rounds its pivot to a whole unit (<c>GH_ComponentAttributes.Layout</c>), half a unit each way on each of the two.</summary>
    public const double Rounding = 1;

    private static readonly LayoutOptions Tidy = LayoutOptions.Default;

    private static readonly string[] Counted = { "", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "eleven", "twelve" };

    public static double WidthFor(IReadOnlyList<SpaceBox> measured)
    {
      var widest = (measured ?? new SpaceBox[0]).OrderByDescending(m => m.W).Take(RowOf).ToList();
      return widest.Sum(m => m.W) + ColumnGap * Math.Max(0, widest.Count - 1);
    }

    public static SpotBlock Pack(IReadOnlyList<SpaceBox> measured) => Pack(measured, WidthFor(measured));

    /// <summary>Rows filled left to right up to <paramref name="width"/>, each as tall as the tallest in it. The first in a row always goes there, so what is wider than the block has a row of its own and is never refused.</summary>
    public static SpotBlock Pack(IReadOnlyList<SpaceBox> measured, double width)
    {
      var places = new List<SpaceBox>();
      double x = 0, y = 0, rowHeight = 0, right = 0;
      var rows = 0;
      var inRow = 0;
      foreach (var m in measured ?? new SpaceBox[0])
      {
        if (inRow > 0 && x + m.W > width + 0.001)
        {
          y += rowHeight + RowGap;
          x = 0;
          rowHeight = 0;
          inRow = 0;
        }
        if (inRow == 0) rows++;

        places.Add(new SpaceBox("", x, y, m.W, m.H, SpaceRole.Fixed));
        right = Math.Max(right, x + m.W);
        rowHeight = Math.Max(rowHeight, m.H);
        x += m.W + ColumnGap;
        inRow++;
      }
      return new SpotBlock { Places = places, W = right, H = rows == 0 ? 0 : y + rowHeight, Rows = rows, Width = width };
    }

    /// <summary>Brief DP: the inputs in a column at the left, the outputs in a column at the right, and the rest in the rows above, between them, with the middle's width six of its own.</summary>
    public static SpotBlock Pack(IReadOnlyList<SpaceBox> measured, IReadOnlyList<BlockRole> roles) =>
      Pack(measured, WidthFor(measured.Where((_, i) => roles[i] == BlockRole.Middle).ToList()), roles);

    /// <summary>The same, with the middle's rows filled to <paramref name="width"/>. Each part is centred on the block's height, as Reorganise's boundary columns are centred on the body.</summary>
    public static SpotBlock Pack(IReadOnlyList<SpaceBox> measured, double width, IReadOnlyList<BlockRole> roles)
    {
      if (roles == null) return Pack(measured, width);

      var all = Enumerable.Range(0, measured.Count).ToList();
      var middle = all.Where(i => roles[i] == BlockRole.Middle).ToList();
      var rows = Pack(middle.Select(i => measured[i]).ToList(), width);

      var limit = rows.H + BlockColumns.WrapBound;
      var left = BlockColumns.Stack(all.Where(i => roles[i] == BlockRole.Input).ToList(), measured, limit);
      var right = BlockColumns.Stack(all.Where(i => roles[i] == BlockRole.Output).ToList(), measured, limit);
      var (leftW, leftH) = BlockColumns.Size(left, measured);
      var (rightW, rightH) = BlockColumns.Size(right, measured);
      var blockH = Math.Max(rows.H, Math.Max(leftH, rightH));

      var places = new SpaceBox[measured.Count];
      BlockColumns.Place(left, measured, 0, 0, blockH, places);

      var bodyX = leftW + (left.Count > 0 && middle.Count > 0 ? ColumnGap : 0);
      var bodyY = Math.Floor((blockH - rows.H) / 2);
      for (var k = 0; k < middle.Count; k++) places[middle[k]] = rows.Places[k].At(bodyX, bodyY);

      var rightX = bodyX + rows.W + ((left.Count > 0 || middle.Count > 0) && right.Count > 0 ? ColumnGap : 0);
      BlockColumns.Place(right, measured, rightX, 0, blockH, places);

      return new SpotBlock { Places = places, W = rightX + rightW, H = blockH, Rows = rows.Rows, Columns = Math.Max(left.Count, right.Count), Width = width, Roles = roles };
    }

    /// <summary>THE LAW: every pair is apart by at least the gap of the way they are apart, across or down.</summary>
    public static bool Disjoint(IReadOnlyList<SpaceBox> placed)
    {
      var all = (placed ?? new SpaceBox[0]).Where(b => b != null).ToList();
      for (var i = 0; i < all.Count; i++)
        for (var j = i + 1; j < all.Count; j++)
          if (!Apart(all[i], all[j])) return false;
      return true;
    }

    private static bool Apart(SpaceBox a, SpaceBox b) =>
      b.X - a.Right >= ColumnGap - Rounding || a.X - b.Right >= ColumnGap - Rounding ||
      b.Y - a.Bottom >= RowGap - Rounding || a.Y - b.Bottom >= RowGap - Rounding;

    /// <summary>Measure, then place. The two passes of the ruling, then the law over what stands.</summary>
    public static SpotBlock Arrange(Func<IReadOnlyList<SpaceBox>> measure, Action<SpotBlock> place, IReadOnlyList<BlockRole> roles = null)
    {
      var block = roles == null ? Pack(measure()) : Pack(measure(), roles);
      place(block);
      return Settle(block, measure, place);
    }

    /// <summary>The law over what stands now. Holding, it is the same block and nothing moves. Failing, it is packed once more from the sizes as they now are, with a wider block.</summary>
    public static SpotBlock Settle(SpotBlock block, Func<IReadOnlyList<SpaceBox>> measure, Action<SpotBlock> place)
    {
      var now = measure();
      if (Disjoint(now)) return block;

      var wider = Pack(now, block.Width + now.Max(m => m.W) + ColumnGap, block.Roles);
      place(wider);
      return wider;
    }

    /// <summary>"in two rows", or null for a block that stayed one row.</summary>
    public static string RowWords(int rows)
    {
      if (rows <= 1) return null;
      return "in " + Count(rows) + " rows";
    }

    /// <summary>"in two columns", or null where no column wrapped (Brief DP): the sentence says nothing new unless there is something to look for.</summary>
    public static string ColumnWords(int columns)
    {
      if (columns <= 1) return null;
      return "in " + Count(columns) + " columns";
    }

    private static string Count(int n) => n < Counted.Length ? Counted[n] : n.ToString(CultureInfo.InvariantCulture);

    /// <summary>The verdict's sentence for where it landed, with the rows on the end, so a person knows to look down: "inside the view, in two rows".</summary>
    public static string WithRows(string where, int rows)
    {
      var words = RowWords(rows);
      if (words == null) return where;
      return string.IsNullOrWhiteSpace(where) ? words : where + ", " + words;
    }

    /// <summary>The same for a column that wrapped: "inside the view, in two columns".</summary>
    public static string WithColumns(string where, int columns)
    {
      var words = ColumnWords(columns);
      if (words == null) return where;
      return string.IsNullOrWhiteSpace(where) ? words : where + ", " + words;
    }
  }
}
