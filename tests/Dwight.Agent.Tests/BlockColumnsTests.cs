using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// Brief DP (Tharun's ruling of 1 November 2026: "All the inputs go in vertically only; all the outputs also come in vertically"): INPUTS STAND IN A COLUMN AT THE BLOCK'S LEFT, OUTPUTS IN A COLUMN AT ITS RIGHT, NEVER IN A ROW.
  /// The classes come from the live wiring, never from a type name alone. Everything but the shim that reads the wiring and moves the components is GH-free and held here.
  /// </summary>
  public class BlockColumnsTests
  {
    private static SpaceBox Box(double x, double y, double w, double h) => new SpaceBox("", x, y, w, h, SpaceRole.Fixed);

    private static List<SpaceBox> Sized(params (double W, double H)[] sizes) => sizes.Select(s => Box(0, 0, s.W, s.H)).ToList();

    private static List<SpaceBox> Sliders(int n) => Enumerable.Range(0, n).Select(_ => Box(0, 0, 150, 20)).ToList();

    private static List<BlockRole> Roles(string s) => s.Select(c => c == 'i' ? BlockRole.Input : c == 'o' ? BlockRole.Output : c == 'I' ? BlockRole.InputBelowKept : c == 'O' ? BlockRole.OutputBelowKept : BlockRole.Middle).ToList();

    // ---- the classes, read from the wiring ------------------------------------------------------------------------------------------------------

    private static BlockNode Slider(bool feedsInside = true, bool feedsOutside = false) => new BlockNode { Holder = true, SourceOnly = true, FeedsInside = feedsInside, FeedsOutside = feedsOutside };

    [Fact]
    public void A_slider_a_toggle_a_value_list_feeding_the_block_is_an_input()
    {
      Assert.Equal(BlockRole.Input, BlockColumns.RoleOf(Slider()));
    }

    [Fact]
    public void One_with_no_wire_yet_is_still_an_input_because_it_can_only_be_a_source()
    {
      Assert.Equal(BlockRole.Input, BlockColumns.RoleOf(Slider(feedsInside: false)));
      Assert.Equal(BlockRole.Input, BlockColumns.RoleOf(Slider(feedsInside: false, feedsOutside: true)));
    }

    [Fact]
    public void A_panel_feeding_the_block_is_an_input()
    {
      Assert.Equal(BlockRole.Input, BlockColumns.RoleOf(new BlockNode { Holder = true, FeedsInside = true }));
    }

    [Fact]
    public void A_param_with_internalised_data_feeding_the_block_is_an_input()
    {
      Assert.Equal(BlockRole.Input, BlockColumns.RoleOf(new BlockNode { Holder = true, FeedsInside = true }));
    }

    [Fact]
    public void A_param_whose_sources_are_all_outside_is_an_input()
    {
      Assert.Equal(BlockRole.Input, BlockColumns.RoleOf(new BlockNode { Holder = true, FedOutside = true, FeedsInside = true }));
    }

    [Fact]
    public void A_panel_wired_as_a_result_is_an_output_so_a_type_name_alone_decides_nothing()
    {
      Assert.Equal(BlockRole.Output, BlockColumns.RoleOf(new BlockNode { Holder = true, FedInside = true }));
    }

    [Fact]
    public void A_param_placed_as_the_blocks_result_is_an_output()
    {
      Assert.Equal(BlockRole.Output, BlockColumns.RoleOf(new BlockNode { Holder = true, FedInside = true, FeedsOutside = true }));
    }

    [Fact]
    public void A_custom_preview_is_an_output_wired_or_not()
    {
      Assert.Equal(BlockRole.Output, BlockColumns.RoleOf(new BlockNode { Sink = true, FedInside = true }));
      Assert.Equal(BlockRole.Output, BlockColumns.RoleOf(new BlockNode { Sink = true }));
    }

    [Fact]
    public void A_component_whose_recipients_are_all_outside_is_an_output()
    {
      Assert.Equal(BlockRole.Output, BlockColumns.RoleOf(new BlockNode { FedInside = true, FeedsOutside = true }));
    }

    [Fact]
    public void A_component_feeding_only_outside_with_nothing_feeding_it_is_an_output()
    {
      Assert.Equal(BlockRole.Output, BlockColumns.RoleOf(new BlockNode { FeedsOutside = true }));
    }

    [Fact]
    public void The_last_component_of_a_chain_is_an_output()
    {
      Assert.Equal(BlockRole.Output, BlockColumns.RoleOf(new BlockNode { FedInside = true }));
    }

    [Fact]
    public void A_component_with_something_inside_on_both_sides_is_the_middle()
    {
      Assert.Equal(BlockRole.Middle, BlockColumns.RoleOf(new BlockNode { FedInside = true, FeedsInside = true }));
      Assert.Equal(BlockRole.Middle, BlockColumns.RoleOf(new BlockNode { Holder = true, FedInside = true, FeedsInside = true }));
    }

    [Fact]
    public void A_computing_component_where_the_flow_starts_is_the_middle()
    {
      Assert.Equal(BlockRole.Middle, BlockColumns.RoleOf(new BlockNode { FeedsInside = true }));
      Assert.Equal(BlockRole.Middle, BlockColumns.RoleOf(new BlockNode { FedOutside = true, FeedsInside = true }));
    }

    [Fact]
    public void A_panel_or_a_component_with_no_wires_yet_is_the_middle()
    {
      Assert.Equal(BlockRole.Middle, BlockColumns.RoleOf(new BlockNode { Holder = true }));
      Assert.Equal(BlockRole.Middle, BlockColumns.RoleOf(new BlockNode()));
    }

    // ---- a spot's block ---------------------------------------------------------------------------------------------------------------------------

    private static List<SpaceBox> SixAndTwo() => Sliders(6).Concat(Sized((120, 60), (120, 60))).ToList();

    [Fact]
    public void Six_sliders_and_two_consumers_land_as_a_column_of_six_with_the_two_beside_it()
    {
      var block = SpotLayout.Pack(SixAndTwo(), Roles("iiiiiimm"));

      Assert.All(block.Places.Take(6), p => Assert.Equal(0, p.X));
      Assert.Equal(new double[] { 0, 38, 76, 114, 152, 190 }, block.Places.Take(6).Select(p => p.Y));
      Assert.Equal(150 + SpotLayout.ColumnGap, block.Places[6].X);
      Assert.True(block.Places[7].X > block.Places[6].X);
      Assert.Equal(block.Places[6].Y, block.Places[7].Y);   // the middle keeps its row
      Assert.Equal(1, block.Columns);
      Assert.Equal(1, block.Rows);
      Assert.True(SpotLayout.Disjoint(block.Places));
    }

    [Fact]
    public void A_column_never_runs_along_a_row()
    {
      var block = SpotLayout.Pack(Sliders(4), Roles("iiii"));

      Assert.Single(block.Places.Select(p => p.X).Distinct());
      Assert.Equal(4, block.Places.Select(p => p.Y).Distinct().Count());
    }

    [Fact]
    public void Inputs_at_the_left_outputs_at_the_right_and_the_middle_between()
    {
      var block = SpotLayout.Pack(Sized((150, 20), (120, 60), (100, 40), (150, 20)), Roles("imoi"));

      Assert.Equal(0, block.Places[0].X);
      Assert.Equal(0, block.Places[3].X);
      Assert.True(block.Places[1].X >= 150 + SpotLayout.ColumnGap);
      Assert.Equal(block.Places[1].Right + SpotLayout.ColumnGap, block.Places[2].X);
      Assert.Equal(block.Places[2].Right, block.W);
    }

    [Fact]
    public void A_column_is_in_the_order_asked_for_top_to_bottom()
    {
      var block = SpotLayout.Pack(Sliders(3), Roles("iii"));

      Assert.True(block.Places[0].Y < block.Places[1].Y && block.Places[1].Y < block.Places[2].Y);
    }

    [Fact]
    public void A_slider_keeps_its_own_width_and_the_column_is_as_wide_as_its_widest()
    {
      var block = SpotLayout.Pack(Sized((150, 20), (300, 20), (120, 60)), Roles("iim"));

      Assert.Equal(150, block.Places[0].W);
      Assert.Equal(300, block.Places[1].W);
      Assert.Equal(300 + SpotLayout.ColumnGap, block.Places[2].X);
    }

    [Fact]
    public void Each_part_is_centred_on_the_blocks_height()
    {
      var block = SpotLayout.Pack(SixAndTwo(), Roles("iiiiiimm"));

      var columnH = 6 * 20 + 5 * SpotLayout.RowGap;
      Assert.Equal(Math.Floor((columnH - 60) / 2), block.Places[6].Y);
      Assert.Equal(columnH, block.H);
    }

    [Fact]
    public void A_column_shorter_than_the_middle_is_centred_on_it()
    {
      var block = SpotLayout.Pack(Sized((150, 20), (150, 20), (120, 400)), Roles("iim"));

      Assert.Equal(Math.Floor((400 - (40 + SpotLayout.RowGap)) / 2), block.Places[0].Y);
      Assert.Equal(0, block.Places[2].Y);
      Assert.Equal(400, block.H);
    }

    [Fact]
    public void With_only_inputs_or_only_outputs_there_is_nothing_to_leave_a_gap_for()
    {
      Assert.Equal(150, SpotLayout.Pack(Sliders(2), Roles("ii")).W);
      Assert.Equal(150, SpotLayout.Pack(Sliders(2), Roles("oo")).W);
      Assert.Equal(0, SpotLayout.Pack(Sliders(2), Roles("oo")).Places[0].X);
      Assert.Equal(150 + SpotLayout.ColumnGap, SpotLayout.Pack(Sliders(2), Roles("io")).Places[1].X);
    }

    [Fact]
    public void With_no_input_or_output_the_block_is_exactly_the_rows_it_was()
    {
      var sizes = Sized((150, 20), (300, 40), (90, 30));
      var plain = SpotLayout.Pack(sizes);
      var classed = SpotLayout.Pack(sizes, Roles("mmm"));

      Assert.Equal(plain.Places.Select(p => (p.X, p.Y)), classed.Places.Select(p => (p.X, p.Y)));
      Assert.Equal(plain.W, classed.W);
      Assert.Equal(plain.H, classed.H);
      Assert.Equal(0, classed.Columns);
    }

    [Fact]
    public void The_middles_rows_are_six_of_its_own_and_not_of_the_whole_block()
    {
      var sizes = Sliders(6).Concat(Sized((100, 30), (100, 30), (100, 30), (100, 30), (100, 30), (100, 30), (100, 30))).ToList();
      var block = SpotLayout.Pack(sizes, Roles("iiiiiimmmmmmm"));

      Assert.Equal(2, block.Rows);
    }

    [Fact]
    public void The_nine_of_the_tenth_gate_with_nothing_wired()
    {
      // Six sliders with a Panel and two more sliders, no wires: every slider is an input, and an unwired Panel is neither.
      var sizes = Sliders(6).Concat(Sized((200, 100), (150, 20), (150, 20))).ToList();
      var block = SpotLayout.Pack(sizes, Roles("iiiiiimii"));

      Assert.Equal(new[] { 0d }, new[] { 0, 1, 2, 3, 4, 5, 7, 8 }.Select(i => block.Places[i].X).Distinct());
      Assert.Equal(150 + SpotLayout.ColumnGap, block.Places[6].X);
      Assert.True(SpotLayout.Disjoint(block.Places));
    }

    // ---- the law covers the columns ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_disjointness_law_holds_across_every_shape_of_block()
    {
      for (var seed = 0; seed < 400; seed++)
      {
        var random = new Random(seed);
        var n = random.Next(1, 41);
        var sizes = Enumerable.Range(0, n).Select(_ => Box(0, 0, random.Next(24, 600), random.Next(20, 400))).ToList();
        var roles = Enumerable.Range(0, n).Select(_ => (BlockRole)random.Next(0, 3)).ToList();
        var block = SpotLayout.Pack(sizes, roles);

        Assert.True(SpotLayout.Disjoint(block.Places), "seed " + seed);
        Assert.True(block.Places.All(p => p.X >= 0 && p.Y >= 0 && p.Right <= block.W + 0.01 && p.Bottom <= block.H + 0.01), "seed " + seed);
      }
    }

    [Fact]
    public void A_rerun_keeps_the_columns_and_makes_the_middle_wider()
    {
      var world = SixAndTwo();
      var block = SpotLayout.Arrange(() => world.ToList(), b => world = b.Places.Select(p => Box(p.X, p.Y, p.W, p.H)).ToList(), Roles("iiiiiimm"));
      world[0] = Box(world[0].X, world[0].Y, 150, 300);   // a slider taking a new size after the solve, over the one beneath it
      Assert.False(SpotLayout.Disjoint(world));

      var settled = SpotLayout.Settle(block, () => world.ToList(), b => world = b.Places.Select(p => Box(p.X, p.Y, p.W, p.H)).ToList());

      Assert.Same(block.Roles, settled.Roles);
      Assert.True(settled.Width > block.Width);
      Assert.Single(world.Take(6).Select(b => b.X).Distinct());
      Assert.True(SpotLayout.Disjoint(world));
    }

    [Fact]
    public void A_rerun_on_a_block_of_rows_stays_rows()
    {
      var world = Sliders(2);
      var block = SpotLayout.Arrange(() => world.ToList(), b => world = b.Places.Select(p => Box(p.X, p.Y, p.W, p.H)).ToList());

      Assert.Null(block.Roles);
      Assert.Equal(1, block.Rows);
    }

    // ---- the wrap ---------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_card_sized_column_never_wraps_even_with_nothing_beside_it()
    {
      var block = SpotLayout.Pack(Sliders(SpotPlacement.MaxComponents), Roles(new string('i', SpotPlacement.MaxComponents)));

      Assert.Equal(1, block.Columns);
      Assert.Single(block.Places.Select(p => p.X).Distinct());
    }

    [Fact]
    public void A_column_taller_than_the_middle_by_more_than_the_bound_wraps()
    {
      var n = (int)Math.Ceiling((BlockColumns.WrapBound + SpotLayout.RowGap) / (20 + SpotLayout.RowGap)) + 1;
      var block = SpotLayout.Pack(Sliders(n), Roles(new string('i', n)));

      Assert.Equal(2, block.Columns);
      Assert.Equal(2, block.Places.Select(p => p.X).Distinct().Count());
      Assert.Equal(150 + SpotLayout.ColumnGap, block.Places.Max(p => p.X));
      Assert.True(SpotLayout.Disjoint(block.Places));
    }

    [Fact]
    public void The_bound_is_measured_from_the_middle()
    {
      var n = (int)Math.Ceiling((BlockColumns.WrapBound + SpotLayout.RowGap) / (20 + SpotLayout.RowGap)) + 1;
      var tallMiddle = Sliders(n).Concat(Sized((100, 400))).ToList();

      Assert.Equal(1, SpotLayout.Pack(tallMiddle, Roles(new string('i', n) + "m")).Columns);
    }

    [Fact]
    public void A_wrapped_column_fills_each_before_the_next()
    {
      var columns = BlockColumns.Stack(new[] { 0, 1, 2, 3, 4 }, Sliders(5), 60);

      Assert.Equal(new[] { 2, 2, 1 }, columns.Select(c => c.Count));
      Assert.Equal(new[] { 0, 1 }, columns[0]);
    }

    [Fact]
    public void One_taller_than_the_limit_stands_alone_and_is_never_refused()
    {
      var columns = BlockColumns.Stack(new[] { 0, 1 }, Sized((100, 900), (100, 20)), 60);

      Assert.Equal(2, columns.Count);
      Assert.Equal(new[] { 0 }, columns[0]);
    }

    [Fact]
    public void A_column_that_fits_exactly_does_not_wrap()
    {
      Assert.Single(BlockColumns.Stack(new[] { 0, 1 }, Sliders(2), 20 + SpotLayout.RowGap + 20));
    }

    [Fact]
    public void Outputs_wrap_to_the_right_of_their_first_column()
    {
      var n = (int)Math.Ceiling((BlockColumns.WrapBound + SpotLayout.RowGap) / (20 + SpotLayout.RowGap)) + 1;
      var block = SpotLayout.Pack(Sliders(n), Roles(new string('o', n)));

      Assert.Equal(2, block.Columns);
      Assert.True(SpotLayout.Disjoint(block.Places));
    }

    // ---- the sentence ------------------------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0, "inside the view")]
    [InlineData(1, "inside the view")]
    [InlineData(2, "inside the view, in two columns")]
    [InlineData(3, "inside the view, in three columns")]
    public void The_sentence_says_nothing_new_unless_a_column_wrapped(int columns, string expected)
    {
      Assert.Equal(expected, SpotLayout.WithColumns("inside the view", columns));
    }

    [Fact]
    public void Rows_and_columns_are_both_said_where_both_are_true()
    {
      Assert.Equal("inside the view, in two rows, in two columns", SpotLayout.WithColumns(SpotLayout.WithRows("inside the view", 2), 2));
      Assert.Equal("in two columns", SpotLayout.WithColumns(null, 2));
      Assert.Null(SpotLayout.WithColumns(null, 1));
    }

    // ---- a map region: the flow keeps the middle ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_regions_panel_as_a_result_lands_in_the_output_column()
    {
      // A slider, a component and a Panel wired as the result, as the flow left them.
      var flow = new[] { Box(100, 300, 150, 20), Box(300, 300, 120, 60), Box(500, 300, 200, 100) };
      var moved = BlockColumns.AroundFlow(flow, Roles("imo"));

      Assert.True(moved[0].Right + SpotLayout.ColumnGap <= moved[1].X + 0.01);
      Assert.True(moved[1].Right + SpotLayout.ColumnGap <= moved[2].X + 0.01);
    }

    [Fact]
    public void A_regions_inputs_stand_in_one_column_at_its_left_and_its_preview_at_its_right()
    {
      var flow = new[] { Box(100, 100, 150, 20), Box(120, 200, 150, 20), Box(90, 300, 150, 20), Box(400, 200, 120, 60), Box(700, 180, 100, 80) };
      var moved = BlockColumns.AroundFlow(flow, Roles("iiimo"));

      Assert.Single(moved.Take(3).Select(b => b.X).Distinct());
      Assert.Equal(3, moved.Take(3).Select(b => b.Y).Distinct().Count());
      Assert.True(moved[0].Right + SpotLayout.ColumnGap <= moved[3].X + 0.01);
      Assert.True(moved[3].Right + SpotLayout.ColumnGap <= moved[4].X + 0.01);
    }

    [Fact]
    public void The_flow_keeps_the_middle_moved_only_as_a_whole()
    {
      var flow = new[] { Box(0, 0, 150, 20), Box(200, 40, 100, 40), Box(360, 10, 80, 90), Box(600, 200, 90, 30), Box(800, 0, 100, 40) };
      var moved = BlockColumns.AroundFlow(flow, Roles("immmo"));

      var dx = moved[1].X - flow[1].X;
      var dy = moved[1].Y - flow[1].Y;
      for (var i = 2; i <= 3; i++)
      {
        Assert.Equal(flow[i].X + dx, moved[i].X);
        Assert.Equal(flow[i].Y + dy, moved[i].Y);
      }
    }

    [Fact]
    public void A_middle_shorter_than_its_column_moves_down_as_a_whole_and_stays_as_the_flow_left_it()
    {
      var flow = new[] { Box(0, 0, 150, 100), Box(0, 118, 150, 100), Box(0, 236, 150, 100), Box(300, 10, 100, 40), Box(450, 30, 100, 40) };
      var moved = BlockColumns.AroundFlow(flow, Roles("iiimm"));

      var dy = moved[3].Y - flow[3].Y;
      Assert.True(dy > 0, "the middle should come down to the column's midpoint, moved by " + dy);
      Assert.Equal(flow[4].Y + dy, moved[4].Y);
    }

    [Fact]
    public void The_block_keeps_the_top_left_it_had_so_a_tidy_never_relocates_a_region()
    {
      var flow = new[] { Box(500, 300, 150, 20), Box(700, 320, 100, 40), Box(900, 300, 100, 40) };
      var moved = BlockColumns.AroundFlow(flow, Roles("imo"));

      Assert.Equal(500, moved.Min(b => b.X));
      Assert.Equal(300, moved.Min(b => b.Y));
    }

    // ---- a region's column reads in a named order (the twelfth gate: it read b, a, c, d) ----------------------------------------------------------------------

    /// <summary>The members named by one letter each, top to bottom, for the members of that column.</summary>
    private static string Reads(IReadOnlyList<SpaceBox> moved, string names, string column) =>
      new string(column.OrderBy(c => moved[names.IndexOf(c)].Y).ToArray());

    private static BlockWires Reader(params (int Partner, int Port)[] wires) => new BlockWires { Readers = wires.Select(w => new BlockWire(w.Partner, w.Port)).ToList() };

    private static BlockWires Feeder(params (int Partner, int Port)[] wires) => new BlockWires { Feeders = wires.Select(w => new BlockWire(w.Partner, w.Port)).ToList() };

    private static readonly BlockWires Loose = new BlockWires();

    [Fact]
    public void Four_sliders_with_no_wires_read_a_b_c_d_wherever_the_engine_packed_them()
    {
      // What the gate saw: the engine packs what is not wired by guid, and here that stood b above a.
      var flow = new[] { Box(0, 100, 150, 20), Box(0, 50, 150, 20), Box(0, 150, 150, 20), Box(0, 200, 150, 20), Box(400, 100, 100, 80) };
      var moved = BlockColumns.AroundFlow(flow, Roles("iiiio"), null, new[] { Loose, Loose, Loose, Loose, Loose });

      Assert.Equal("abcd", Reads(moved, "abcdz", "abcd"));
    }

    [Fact]
    public void Four_sliders_wired_in_port_order_read_a_b_c_d_wherever_the_engine_stacked_them()
    {
      // The engine knows nothing of ports, so it can stack them crossed.
      var flow = new[] { Box(0, 100, 150, 20), Box(0, 50, 150, 20), Box(0, 200, 150, 20), Box(0, 150, 150, 20), Box(400, 100, 100, 80) };
      var moved = BlockColumns.AroundFlow(flow, Roles("iiiim"), null, new[] { Reader((4, 0)), Reader((4, 1)), Reader((4, 2)), Reader((4, 3)), Loose });

      Assert.Equal("abcd", Reads(moved, "abcdm", "abcd"));
    }

    [Fact]
    public void Where_the_wires_disagree_with_the_card_the_flow_wins_and_they_do_not_cross()
    {
      var flow = new[] { Box(0, 50, 150, 20), Box(0, 100, 150, 20), Box(400, 100, 100, 80) };
      var moved = BlockColumns.AroundFlow(flow, Roles("iim"), null, new[] { Reader((2, 1)), Reader((2, 0)), Loose });

      Assert.Equal("ba", Reads(moved, "abm", "ab"));
    }

    [Fact]
    public void An_input_stands_by_the_component_it_feeds_top_to_bottom()
    {
      // a feeds the lower component and b the upper, whatever port each reaches.
      var flow = new[] { Box(0, 50, 150, 20), Box(0, 100, 150, 20), Box(400, 300, 100, 40), Box(400, 100, 100, 40) };
      var moved = BlockColumns.AroundFlow(flow, Roles("iimm"), null, new[] { Reader((2, 0)), Reader((3, 5)), Loose, Loose });

      Assert.Equal("ba", Reads(moved, "abmn", "ab"));
    }

    [Fact]
    public void An_input_read_by_several_stands_by_the_topmost_of_them()
    {
      var flow = new[] { Box(0, 50, 150, 20), Box(0, 100, 150, 20), Box(400, 300, 100, 40), Box(400, 100, 100, 40), Box(400, 200, 100, 40) };
      var moved = BlockColumns.AroundFlow(flow, Roles("iimmm"), null, new[] { Reader((2, 0), (3, 0)), Reader((4, 0)), Loose, Loose, Loose });

      Assert.Equal("ab", Reads(moved, "abmno", "ab"));
    }

    [Fact]
    public void What_is_wired_comes_first_and_what_is_not_follows_in_the_cards_order()
    {
      var flow = new[] { Box(0, 200, 150, 20), Box(0, 150, 150, 20), Box(0, 100, 150, 20), Box(0, 50, 150, 20), Box(400, 100, 100, 80) };
      var moved = BlockColumns.AroundFlow(flow, Roles("iiiim"), null, new[] { Loose, Reader((4, 1)), Loose, Reader((4, 0)), Loose });

      Assert.Equal("dbac", Reads(moved, "abcdm", "abcd"));
    }

    [Fact]
    public void Outputs_read_the_same_way_by_what_feeds_them()
    {
      var flow = new[] { Box(0, 100, 100, 80), Box(400, 50, 150, 20), Box(400, 100, 150, 20), Box(400, 150, 150, 20) };
      var moved = BlockColumns.AroundFlow(flow, Roles("mooo"), null, new[] { Loose, Feeder((0, 2)), Feeder((0, 0)), Loose });

      Assert.Equal("yxz", Reads(moved, "mxyz", "xyz"));
    }

    [Fact]
    public void A_rebuild_keeps_what_stood_and_adds_the_new_ones_in_the_cards_order_below()
    {
      // Kept a and b, stood in that order. New c and d, which the engine put above them and the other way up.
      var flow = new[] { Box(0, 100, 150, 20), Box(0, 150, 150, 20), Box(0, 50, 150, 20), Box(0, 10, 150, 20), Box(400, 100, 100, 80) };
      var moved = BlockColumns.AroundFlow(flow, Roles("iiiio"), new double?[] { 300, 400, null, null, null }, new[] { Loose, Loose, Loose, Loose, Loose });

      Assert.Equal("abcd", Reads(moved, "abcdz", "abcd"));
    }

    [Fact]
    public void What_a_rebuild_kept_comes_first_even_when_a_new_one_is_wired_and_first_in_the_list()
    {
      var flow = new[] { Box(0, 10, 150, 20), Box(0, 100, 150, 20), Box(0, 200, 150, 20), Box(400, 100, 100, 80) };
      var moved = BlockColumns.AroundFlow(flow, Roles("iiim"), new double?[] { null, 100, 200, null }, new[] { Reader((3, 0)), Loose, Loose, Loose });

      Assert.Equal("bca", Reads(moved, "abcm", "abc"));
    }

    [Fact]
    public void What_a_rebuild_kept_stands_in_the_order_it_stood_not_the_order_of_the_list()
    {
      var flow = new[] { Box(0, 10, 150, 20), Box(0, 100, 150, 20), Box(400, 100, 100, 80) };
      var moved = BlockColumns.AroundFlow(flow, Roles("iim"), new double?[] { 90, 10, null }, new[] { Loose, Loose, Loose });

      Assert.Equal("ba", Reads(moved, "abm", "ab"));
    }

    [Fact]
    public void The_order_does_not_change_with_where_the_engine_put_the_column_on_any_build()
    {
      for (var seed = 0; seed < 100; seed++)
      {
        var random = new Random(seed);
        var flow = new List<SpaceBox>();
        for (var i = 0; i < 4; i++) flow.Add(Box(0, random.Next(0, 1000), 150, 20));
        flow.Add(Box(400, 100, 100, 80));

        var loose = BlockColumns.AroundFlow(flow, Roles("iiiio"), null, new[] { Loose, Loose, Loose, Loose, Loose });
        Assert.Equal("abcd", Reads(loose, "abcdz", "abcd"));

        var wired = BlockColumns.AroundFlow(flow, Roles("iiiim"), null, new[] { Reader((4, 0)), Reader((4, 1)), Reader((4, 2)), Reader((4, 3)), Loose });
        Assert.Equal("abcd", Reads(wired, "abcdm", "abcd"));
      }
    }

    [Fact]
    public void A_spots_column_reads_in_the_cards_order()
    {
      var block = SpotLayout.Pack(Sliders(4), Roles("iiii"));

      Assert.Equal("abcd", Reads(block.Places, "abcd", "abcd"));
    }

    [Fact]
    public void The_tidy_gives_the_column_its_wires_and_the_shim_reads_ports_from_the_far_end()
    {
      Assert.Contains("BlockColumns.AroundFlow(flow, roles, keptAtY, GhBlockRoles.Wires(members))", Repo("Dwight", "Agent", "GhRegionTidy.cs"));

      var shim = Repo("Dwight", "Agent", "GhBlockRoles.cs");
      Assert.Contains("Readers = Far(o, OutsOf(o), p => p.Recipients, true)", shim);
      Assert.Contains("Feeders = Far(o, InsOf(o), p => p.Sources, false)", shim);
      Assert.Contains("input ? component.Params.Input.IndexOf(p) : component.Params.Output.IndexOf(p)", shim);
    }

    [Fact]
    public void A_regions_block_holds_the_law_for_any_flow_that_held_it()
    {
      for (var seed = 0; seed < 200; seed++)
      {
        var random = new Random(seed);
        var n = random.Next(2, 30);
        // Stood in a tall column, so the flow itself holds the law, and any overlap is what AroundFlow made.
        var y = 0d;
        var flow = new List<SpaceBox>();
        for (var i = 0; i < n; i++) { var h = random.Next(20, 120); flow.Add(Box(random.Next(0, 300), y, random.Next(40, 400), h)); y += h + SpotLayout.RowGap; }
        var roles = Enumerable.Range(0, n).Select(_ => (BlockRole)random.Next(0, 3)).ToList();

        Assert.True(SpotLayout.Disjoint(BlockColumns.AroundFlow(flow, roles)), "seed " + seed);
      }
    }

    [Fact]
    public void Nothing_to_arrange_is_nothing()
    {
      Assert.Empty(BlockColumns.AroundFlow(new SpaceBox[0], new BlockRole[0]));
    }

    // ---- a rebuild keeps the columns ------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void In_a_regions_rebuild_what_was_kept_comes_first_and_a_new_one_takes_the_next_place_below()
    {
      // The flow would put the new slider (the last) at the top. It goes below the three kept, which keep their order.
      var flow = new[] { Box(0, 100, 150, 20), Box(0, 200, 150, 20), Box(0, 300, 150, 20), Box(0, 50, 150, 20), Box(400, 100, 100, 40) };
      var keptAtY = new double?[] { 100, 200, 300, null, 150 };   // all below the new one's 50, so only "kept first" can put it last
      var moved = BlockColumns.AroundFlow(flow, Roles("iiiim"), keptAtY);

      Assert.Single(moved.Take(4).Select(b => b.X).Distinct());
      Assert.True(moved[0].Y < moved[1].Y && moved[1].Y < moved[2].Y && moved[2].Y < moved[3].Y);
    }

    [Fact]
    public void A_kept_input_keeps_its_own_order_in_the_column_whatever_the_flow_says()
    {
      var flow = new[] { Box(0, 300, 150, 20), Box(0, 100, 150, 20), Box(400, 100, 100, 40) };
      var moved = BlockColumns.AroundFlow(flow, Roles("iim"), new double?[] { 10, 90, 15 });

      Assert.True(moved[0].Y < moved[1].Y);
    }

    [Fact]
    public void With_nothing_kept_the_flows_order_stands()
    {
      var flow = new[] { Box(0, 300, 150, 20), Box(0, 100, 150, 20), Box(400, 100, 100, 40) };
      var moved = BlockColumns.AroundFlow(flow, Roles("iim"), new double?[] { null, null, null }, new[] { Reader((2, 1)), Reader((2, 0)), Loose });

      Assert.True(moved[1].Y < moved[0].Y);
    }

    [Fact]
    public void A_new_input_or_output_joins_a_column_only_where_one_stands()
    {
      Assert.Equal(Roles("Iom"), BlockColumns.WithKept(Roles("iom"), true, false));
      Assert.Equal(Roles("iOm"), BlockColumns.WithKept(Roles("iom"), false, true));
      Assert.Equal(Roles("IOm"), BlockColumns.WithKept(Roles("iom"), true, true));
      Assert.Equal(Roles("iom"), BlockColumns.WithKept(Roles("iom"), false, false));
    }

    [Fact]
    public void What_goes_under_a_kept_column_is_not_packed_with_the_block()
    {
      var block = SpotLayout.Pack(Sized((150, 20), (120, 60), (100, 40)), Roles("Imo"));

      Assert.Null(block.Places[0]);
      Assert.Equal(0, block.Places[1].X);   // nothing to its left now
      Assert.Equal(120 + SpotLayout.ColumnGap, block.Places[2].X);
      Assert.True(SpotLayout.Disjoint(block.Places));
    }

    [Fact]
    public void A_block_that_is_all_under_kept_columns_is_empty()
    {
      var block = SpotLayout.Pack(Sliders(2), Roles("IO"));

      Assert.All(block.Places, Assert.Null);
      Assert.Equal(0, block.W);
      Assert.Equal(0, block.H);
    }

    [Fact]
    public void The_next_free_place_is_right_under_a_column_with_the_row_gap()
    {
      var column = new[] { Box(0, 0, 150, 20), Box(0, 38, 150, 20) };

      Assert.Equal(76, BlockColumns.NextFreeBelow(0, 58 + SpotLayout.RowGap, 150, 20, column));
    }

    [Fact]
    public void A_place_taken_below_the_column_is_stepped_over_and_only_ever_downward()
    {
      var obstacles = new[] { Box(0, 0, 150, 20), Box(0, 40, 150, 20), Box(100, 100, 100, 30) };

      var y = BlockColumns.NextFreeBelow(0, 38, 150, 20, obstacles);

      Assert.Equal(148, y);
    }

    [Fact]
    public void Something_clear_across_the_column_gap_is_not_in_the_way()
    {
      var beside = new[] { Box(150 + SpotLayout.ColumnGap, 0, 100, 400) };

      Assert.Equal(10, BlockColumns.NextFreeBelow(0, 10, 150, 20, beside));
    }

    [Fact]
    public void Three_kept_a_fourth_below_and_the_law_holds()
    {
      var kept = new List<SpaceBox> { Box(0, 0, 150, 20), Box(0, 38, 150, 20), Box(0, 76, 150, 20) };
      var y = BlockColumns.NextFreeBelow(0, kept.Max(b => b.Bottom) + SpotLayout.RowGap, 150, 20, kept);

      Assert.Equal(114, y);
      Assert.True(SpotLayout.Disjoint(kept.Concat(new[] { Box(0, y, 150, 20) }).ToList()));
    }

    [Fact]
    public void The_ledger_knows_what_the_last_build_placed_and_only_that()
    {
      var ledger = new BuildLedger(new object());
      var a = Guid.NewGuid(); var b = Guid.NewGuid();

      ledger.CommitBuild("A1", new Dictionary<Guid, string> { [a] = "t" });
      Assert.Equal(new[] { a }, ledger.LastBuilt("A1"));

      ledger.CommitBuild("A1", new Dictionary<Guid, string> { [b] = "t" });
      Assert.Equal(new[] { b }, ledger.LastBuilt("A1"));
      Assert.Empty(ledger.LastBuilt("B1"));
      Assert.Empty(ledger.LastBuilt(null));
    }

    // ---- the seams, by text (ADR-091): the Rhino rows are the twelfth gate's -----------------------------------------------------------------------------------------

    private static string Repo(params string[] parts)
    {
      var dir = AppContext.BaseDirectory;
      while (dir != null && !File.Exists(Path.Combine(dir, "Dwight.sln"))) dir = Path.GetDirectoryName(dir);
      Assert.NotNull(dir);
      return File.ReadAllText(Path.Combine(new[] { dir }.Concat(parts).ToArray())).Replace("\r\n", "\n");
    }

    private static string Source(string file) => Repo("Dwight", "Agent", file);

    private static void InOrder(string source, params string[] parts)
    {
      var at = 0;
      foreach (var part in parts)
      {
        var found = source.IndexOf(part, at, StringComparison.Ordinal);
        Assert.True(found >= 0, "not found in order: " + part);
        at = found + part.Length;
      }
    }

    private static string Between(string source, string from, string to)
    {
      var a = source.IndexOf(from, StringComparison.Ordinal);
      Assert.True(a >= 0, from);
      var b = source.IndexOf(to, a + from.Length, StringComparison.Ordinal);
      Assert.True(b >= 0, to);
      return source.Substring(a, b - a);
    }

    [Fact]
    public void A_block_is_laid_out_after_its_wires_exist_and_before_the_record()
    {
      InOrder(Source("GhBuildRegion.cs"), "spotGroup = MakeSpotGroup(d, spec.Region, created.Values, actions);", "var wired = WireAll(d, spec, created, ids, actions, snapshotted);", "if (packs) Land(landingFacts, built, kit);", "d.UndoUtil.RecordEvent(UndoName, actions)");
    }

    [Fact]
    public void The_classes_are_read_live_counting_what_stands_there_as_inside()
    {
      var read = Between(Source("GhBuildRegion.cs"), "private static void ReadColumns(", "private static SpaceBox BoxNow(");

      InOrder(read, "kit.Facts?.KeptObjects", "new HashSet<Guid>(built.Concat(kept)", "GhBlockRoles.Read(kept, block)", "BlockColumns.WithKept(GhBlockRoles.Read(built, block), kit.KeptInputs.Count > 0, kit.KeptOutputs.Count > 0)");
      InOrder(Between(Source("GhBuildRegion.cs"), "private static void Land(", "private static List<SpaceBox> MeasureBlock("), "ReadColumns(kit);", "SpotLayout.Arrange(", "kit.Roles)", "kit.BlockGroup?.Attributes?.ExpireLayout();");
    }

    [Fact]
    public void What_a_place_already_holds_is_what_its_columns_continue()
    {
      Assert.Contains("IReadOnlyList<Guid> keptIds = into != null ? GhScaffoldMap.ComponentsIn(d, into) : kept;", Source("GhBuildRegion.cs"));
    }

    [Fact]
    public void A_new_input_or_output_goes_under_its_column_before_the_block_looks_for_its_place()
    {
      InOrder(Between(Source("GhBuildRegion.cs"), "private static void PlaceBlock(", "private static string SayLanded("),
        "var x = column.Min(b => b.X);", "var y = column.Max(b => b.Bottom) + SpotLayout.RowGap;", "y = BlockColumns.NextFreeBelow(x, y, now[i].W, now[i].H, canvas);", "canvas.Add(",
        "Below(BlockRole.InputBelowKept, kit.KeptInputs);", "Below(BlockRole.OutputBelowKept, kit.KeptOutputs);", "if (block.Places.All(p => p == null)) return;", "Canvas = canvas,");
    }

    [Fact]
    public void Nothing_a_rebuild_kept_is_ever_moved()
    {
      var place = Between(Source("GhBuildRegion.cs"), "private static void PlaceBlock(", "private static string SayLanded(");

      Assert.Contains("kit.Built[i].Value.Attributes", place);
      Assert.DoesNotContain("KeptObjects", place);
    }

    [Fact]
    public void The_shim_reads_both_sides_of_each_component_and_never_a_type_name()
    {
      var shim = Source("GhBlockRoles.cs");

      InOrder(shim, "Holder = param != null,", "SourceOnly = param != null && !(param.Attributes?.HasInputGrip ?? true),", "Sink = component != null && component.Params.Output.Count == 0,");
      InOrder(shim, "input?.Sources", "owner == o.InstanceGuid", "node.FedInside = true;", "node.FedOutside = true;", "output?.Recipients", "owner == o.InstanceGuid", "node.FeedsInside = true;", "node.FeedsOutside = true;");
      foreach (var typeName in new[] { "GH_NumberSlider", "GH_Panel", "GH_BooleanToggle", "GH_ValueList", ".Name" })
        Assert.DoesNotContain(typeName, shim);
    }

    [Fact]
    public void A_regions_tidy_arranges_the_columns_around_its_flow_and_can_only_lose_them_not_the_tidy()
    {
      var tidy = Source("GhRegionTidy.cs");

      InOrder(tidy, "foreach (var kv in LayoutFor(doc, members, ledger?.LastBuilt(address))) targets[kv.Key] = kv.Value;");
      InOrder(Between(tidy, "private static Dictionary<Guid, TidyPoint> LayoutFor(", "/// <summary>\n    /// The body scribbles"),
        "RegionTidy.Anchor(byGuid, current)", "IntoColumns(objects, placed, lastBuilt);", "return placed;", "BlockColumns.AroundFlow(flow, roles, keptAtY, GhBlockRoles.Wires(members))", "catch { }");
    }

    [Fact]
    public void A_tidy_works_in_true_bounds_and_moves_the_pivot_by_the_difference()
    {
      var into = Between(Source("GhRegionTidy.cs"), "private static void IntoColumns(", "/// <summary>\n    /// The body scribbles");

      Assert.Contains("b.X + t.X - o.Attributes.Pivot.X", into);
      Assert.Contains("t.X + moved[i].X - flow[i].X", into);
      Assert.Contains("rebuilt && !lastBuilt.Contains(o.InstanceGuid) ? (double?)o.Attributes.Bounds.Y : null", into);
    }

    [Fact]
    public void Reorganise_is_left_as_it_is()
    {
      Assert.Contains("SnapParamsToEdges", Repo("Dwight", "Data", "CanvasWriter.cs"));
      Assert.DoesNotContain("BlockColumns", Repo("Dwight", "Data", "CanvasWriter.cs"));
    }
  }
}
