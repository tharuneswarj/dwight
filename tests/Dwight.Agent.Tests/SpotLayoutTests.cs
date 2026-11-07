using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Dwight.Layout;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// Brief DK: A SPOT BLOCK IS LAID OUT FROM MEASURED BOUNDS, SO NOTHING IT PLACES OVERLAPS. The tenth gate measured the old grid: nine components landed six and three, the 150-wide
  /// sliders 140 apart, five pairs overlapping by 10 units and the eighth on the tall Panel. The packer, its law and its sentence are GH-free; the seams that feed them are pinned by text.
  /// </summary>
  public class SpotLayoutTests
  {
    private static SpaceBox Box(double x, double y, double w, double h) => new SpaceBox("", x, y, w, h, SpaceRole.Fixed);

    private static List<SpaceBox> Sized(params (double W, double H)[] sizes) => sizes.Select(s => Box(0, 0, s.W, s.H)).ToList();

    private static List<SpaceBox> Sliders(int n, double w = 150, double h = 20) => Enumerable.Range(0, n).Select(_ => Box(0, 0, w, h)).ToList();

    /// <summary>Six sliders, a ten-line Panel and two more: the gate's nine.</summary>
    private static List<SpaceBox> GateNine() =>
      Sized((150, 20), (150, 20), (150, 20), (150, 20), (150, 20), (150, 20), (200, 100), (150, 20), (150, 20));

    private static List<double> Xs(SpotBlock block) => block.Places.Select(p => p.X).ToList();

    private static List<double> Ys(SpotBlock block) => block.Places.Select(p => p.Y).ToList();

    // ---- the gaps are the tidy's ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_gaps_are_the_tidys_own()
    {
      Assert.Equal(LayoutOptions.Default.LayerSeparation, SpotLayout.ColumnGap);
      Assert.Equal(LayoutOptions.Default.NodeSeparation, SpotLayout.RowGap);
    }

    // ---- the packer -------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Mixed_widths_sit_by_what_they_measure()
    {
      var block = SpotLayout.Pack(Sized((150, 30), (300, 30), (90, 30), (220, 30)));

      Assert.Equal(new double[] { 0, 198, 546, 684 }, Xs(block));
      Assert.Equal(1, block.Rows);
      Assert.Equal(904, block.W);
      Assert.Equal(30, block.H);
    }

    [Fact]
    public void A_row_is_as_tall_as_its_tallest_and_the_next_starts_past_it()
    {
      var block = SpotLayout.Pack(Sized((150, 30), (150, 30), (150, 30), (150, 30), (150, 30), (200, 400), (150, 30)));

      Assert.Equal(2, block.Rows);
      Assert.Equal(new double[] { 0, 0, 0, 0, 0, 0, 400 + SpotLayout.RowGap }, Ys(block));
      Assert.Equal(400 + SpotLayout.RowGap + 30, block.H);
    }

    [Fact]
    public void A_single_wide_Panel_stands_whole()
    {
      var alone = SpotLayout.Pack(Sized((900, 100)));
      Assert.Equal(1, alone.Rows);
      Assert.Equal(900, alone.W);
      Assert.Equal(100, alone.H);

      // Wider than the block it is packed to: a row of its own, and what follows starts below it. Never refused, and never cut.
      var over = SpotLayout.Pack(Sized((900, 100), (150, 20)), 400);
      Assert.Equal(2, over.Rows);
      Assert.Equal(900, over.Places[0].W);
      Assert.Equal(100 + SpotLayout.RowGap, over.Places[1].Y);

      // Among others, the six-widest width makes room for it on a row.
      var among = SpotLayout.Pack(Sized((150, 20), (150, 20), (150, 20), (150, 20), (150, 20), (900, 100), (150, 20), (150, 20)));
      Assert.Equal(2, among.Rows);
      Assert.Equal(198 * 5, among.Places[5].X);
      Assert.Equal(0, among.Places[5].Y);
    }

    [Fact]
    public void Twelve_equal_sliders_land_in_two_rows_of_six()
    {
      var block = SpotLayout.Pack(Sliders(12));

      Assert.Equal(2, block.Rows);
      Assert.Equal(Enumerable.Range(0, 12).Select(i => 198d * (i % 6)), Xs(block));
      Assert.Equal(Enumerable.Range(0, 12).Select(i => i < 6 ? 0d : 20 + SpotLayout.RowGap), Ys(block));
    }

    [Fact]
    public void Twelve_of_different_lengths_still_land_in_two_rows()
    {
      var sizes = new[] { 150d, 150, 190, 150, 210, 150, 150, 150, 170, 150, 150, 240 }.Select(w => Box(0, 0, w, 20)).ToList();

      Assert.Equal(2, SpotLayout.Pack(sizes).Rows);
    }

    [Fact]
    public void A_block_of_six_or_fewer_is_one_row_however_uneven()
    {
      Assert.Equal(1, SpotLayout.Pack(Sized((40, 20), (800, 20), (40, 20), (300, 20), (40, 20), (40, 20))).Rows);
      Assert.Equal(1, SpotLayout.Pack(Sliders(3)).Rows);
    }

    [Fact]
    public void Nothing_to_pack_is_an_empty_block()
    {
      var block = SpotLayout.Pack(new List<SpaceBox>());

      Assert.Equal(0, block.Rows);
      Assert.Empty(block.Places);
      Assert.Equal(0, block.W);
      Assert.Equal(0, block.H);
    }

    [Fact]
    public void The_order_asked_for_is_the_reading_order()
    {
      var sizes = Sized((150, 20), (90, 40), (300, 60), (120, 80), (200, 20), (60, 30), (500, 90), (70, 20));
      var block = SpotLayout.Pack(sizes);

      for (var i = 0; i < sizes.Count; i++)
      {
        Assert.Equal(sizes[i].W, block.Places[i].W);
        Assert.Equal(sizes[i].H, block.Places[i].H);
      }
      for (var i = 1; i < block.Places.Count; i++)
        Assert.True(block.Places[i].Y > block.Places[i - 1].Y || block.Places[i].X > block.Places[i - 1].X);
    }

    // ---- the gate's nine --------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_gates_nine_land_in_two_rows_with_nothing_overlapping()
    {
      var block = SpotLayout.Pack(GateNine());

      Assert.Equal(2, block.Rows);
      Assert.Equal(new double[] { 0, 198, 396, 594, 792, 990, 0, 248, 446 }, Xs(block));
      Assert.True(SpotLayout.Disjoint(block.Places));

      // The footprint is what it took, narrower than the width it was allowed, and the clear place is found for this.
      Assert.Equal(1140, block.W);
      Assert.Equal(1190, block.Width);
      Assert.Equal(20 + SpotLayout.RowGap + 100, block.H);
    }

    /// <summary>The positive control: the law reads the gate's own measurement as the failure it was. A law that has only ever said yes has not been shown to measure.</summary>
    [Fact]
    public void The_old_grid_breaks_the_law_on_the_same_nine()
    {
      var nine = GateNine();
      var oldGrid = nine.Select((m, i) => Box(140 * (i % 6), 90 * (i / 6), m.W, m.H)).ToList();

      Assert.False(SpotLayout.Disjoint(oldGrid));
    }

    // ---- the law ----------------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0, true)]        // exactly the gap
    [InlineData(-1, true)]       // Grasshopper rounds each pivot to a whole unit, so a pair can read a unit short
    [InlineData(-1.01, false)]   // past what rounding can do
    [InlineData(-48, false)]     // touching
    [InlineData(-58, false)]     // the gate's 10-unit overlap
    public void Side_by_side_they_are_apart_by_the_column_gap(double short_, bool holds)
    {
      var gap = SpotLayout.ColumnGap + short_;
      Assert.Equal(holds, SpotLayout.Disjoint(new[] { Box(0, 0, 150, 20), Box(150 + gap, 0, 150, 20) }));
      Assert.Equal(holds, SpotLayout.Disjoint(new[] { Box(150 + gap, 0, 150, 20), Box(0, 0, 150, 20) }));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(-1, true)]
    [InlineData(-1.01, false)]
    [InlineData(-18, false)]
    public void Stacked_they_are_apart_by_the_row_gap(double short_, bool holds)
    {
      var gap = SpotLayout.RowGap + short_;
      Assert.Equal(holds, SpotLayout.Disjoint(new[] { Box(0, 0, 150, 20), Box(0, 20 + gap, 150, 20) }));
      Assert.Equal(holds, SpotLayout.Disjoint(new[] { Box(0, 20 + gap, 150, 20), Box(0, 0, 150, 20) }));
    }

    [Fact]
    public void Every_pair_is_read_not_only_neighbours()
    {
      // The first and third meet; the middle one is clear of both.
      Assert.False(SpotLayout.Disjoint(new[] { Box(0, 0, 150, 20), Box(0, 500, 150, 20), Box(100, 0, 150, 20) }));
    }

    [Fact]
    public void One_or_none_holds_the_law()
    {
      Assert.True(SpotLayout.Disjoint(new SpaceBox[0]));
      Assert.True(SpotLayout.Disjoint(null));
      Assert.True(SpotLayout.Disjoint(new[] { Box(0, 0, 150, 20) }));
    }

    [Fact]
    public void Every_pack_holds_the_law_and_takes_no_more_than_a_row_per_six()
    {
      for (var seed = 0; seed < 300; seed++)
      {
        var random = new Random(seed);
        var n = random.Next(1, 41);
        var sizes = Enumerable.Range(0, n).Select(_ => Box(0, 0, random.Next(24, 600), random.Next(20, 400))).ToList();
        var block = SpotLayout.Pack(sizes);

        Assert.True(SpotLayout.Disjoint(block.Places), "seed " + seed);
        Assert.InRange(block.Rows, 1, (n + SpotLayout.RowOf - 1) / SpotLayout.RowOf);
        Assert.True(block.Places.All(p => p.Right <= block.W && p.Bottom <= block.H), "seed " + seed);
        Assert.True(block.W <= block.Width, "seed " + seed);
      }
    }

    // ---- the two passes, and the rerun ------------------------------------------------------------------------------------------------------

    /// <summary>What the shim stands for: components with sizes of their own, which may change once they are placed.</summary>
    private sealed class World
    {
      public List<SpaceBox> Boxes;
      public int Placed;
      public Action<World> AfterPlace;
      public readonly List<double> Widths = new List<double>();

      public IReadOnlyList<SpaceBox> Measure() => Boxes.ToList();

      public void Place(SpotBlock block)
      {
        Placed++;
        Widths.Add(block.Width);
        Boxes = block.Places.Select(p => Box(1000 + p.X, 2000 + p.Y, p.W, p.H)).ToList();
        AfterPlace?.Invoke(this);
      }

      public void Grow(int i, double w, double h)
      {
        var b = Boxes[i];
        Boxes[i] = Box(b.X, b.Y, b.W + w, b.H + h);
      }
    }

    [Fact]
    public void What_does_not_change_is_placed_once()
    {
      var world = new World { Boxes = GateNine() };
      var block = SpotLayout.Arrange(world.Measure, world.Place);

      Assert.Equal(1, world.Placed);
      Assert.Equal(2, block.Rows);
      Assert.True(SpotLayout.Disjoint(world.Measure()));
    }

    [Fact]
    public void Placing_reads_real_sizes_not_what_was_asked_for_first()
    {
      // A fresh component measures nothing useful until it has been laid out. The block is packed from what measure returns, whatever came before it.
      var world = new World { Boxes = Sized((300, 20), (300, 20), (300, 20)) };
      var block = SpotLayout.Arrange(world.Measure, world.Place);

      Assert.Equal(new double[] { 0, 348, 696 }, Xs(block));
    }

    [Fact]
    public void A_component_that_changes_size_when_placed_reruns_once_with_a_wider_block()
    {
      var world = new World { Boxes = GateNine() };
      world.AfterPlace = w => { if (w.Placed == 1) w.Grow(1, 0, 300); };   // a Panel taking its size from its text, over the row beneath it
      var block = SpotLayout.Arrange(world.Measure, world.Place);

      Assert.Equal(2, world.Placed);
      Assert.True(world.Widths[1] > world.Widths[0]);
      Assert.Equal(world.Widths[1], block.Width);
      Assert.True(SpotLayout.Disjoint(world.Measure()));
    }

    [Fact]
    public void A_component_that_keeps_changing_never_loops()
    {
      var world = new World { Boxes = GateNine() };
      world.AfterPlace = w => w.Grow(1, 0, 300);

      SpotLayout.Arrange(world.Measure, world.Place);

      Assert.Equal(2, world.Placed);
    }

    [Fact]
    public void After_the_solve_a_law_that_holds_moves_nothing()
    {
      var world = new World { Boxes = GateNine() };
      var block = SpotLayout.Arrange(world.Measure, world.Place);
      var before = world.Measure();

      var settled = SpotLayout.Settle(block, world.Measure, world.Place);

      Assert.Same(block, settled);
      Assert.Equal(1, world.Placed);
      Assert.Equal(before.Select(b => (b.X, b.Y)), world.Measure().Select(b => (b.X, b.Y)));
    }

    [Fact]
    public void After_the_solve_a_Panel_that_grew_is_made_room_for()
    {
      var world = new World { Boxes = GateNine() };
      var block = SpotLayout.Arrange(world.Measure, world.Place);
      world.Grow(1, 0, 300);
      Assert.False(SpotLayout.Disjoint(world.Measure()));

      var settled = SpotLayout.Settle(block, world.Measure, world.Place);

      Assert.NotSame(block, settled);
      Assert.True(settled.Width > block.Width);
      Assert.Equal(2, world.Placed);
      Assert.True(SpotLayout.Disjoint(world.Measure()));
      Assert.Equal(320, world.Measure()[1].H);   // the size it grew to is the size it was packed at
    }

    // ---- the block's place is found for its real size ---------------------------------------------------------------------------------------

    [Fact]
    public void A_wide_block_does_not_land_on_work_a_narrow_one_would_miss()
    {
      var canvas = new[] { Box(500, 0, 100, 100) };

      // The old footprint's width, centred on the view: clear where it starts.
      var narrow = Landing.Find(new LandingAsk { W = 140, H = 90, Canvas = canvas, ViewCentre = new TidyPoint(0, 0) });
      Assert.Equal(-70, narrow.X);

      var wide = Landing.Find(new LandingAsk { W = 1200, H = 90, Canvas = canvas, ViewCentre = new TidyPoint(0, 0) });
      Assert.True(wide.X + 1200 + RegionSpacing.Gap <= 500 + 0.01 || wide.X + 0.01 >= 600 + RegionSpacing.Gap ||
                  wide.Y + 90 + RegionSpacing.Gap <= 0 + 0.01 || wide.Y + 0.01 >= 100 + RegionSpacing.Gap);
    }

    // ---- a rebuild keeps what it kept -------------------------------------------------------------------------------------------------------

    private static readonly SpaceBox[] KeptFour = { Box(0, 0, 150, 20), Box(198, 0, 150, 20), Box(0, 38, 150, 20), Box(198, 38, 150, 20) };

    [Fact]
    public void A_rebuild_starts_beside_what_it_kept()
    {
      var at = Landing.Find(new LandingAsk { W = 300, H = 60, Canvas = KeptFour, Kept = KeptFour });

      Assert.Equal(348 + RegionSpacing.Gap, at.X);
      Assert.Equal(0, at.Y);
    }

    [Fact]
    public void What_it_kept_comes_before_the_view_and_the_wires()
    {
      var far = new[] { Box(9000, 9000, 150, 20) };
      var canvas = KeptFour.Concat(far).ToList();

      var at = Landing.Find(new LandingAsk { W = 300, H = 60, Canvas = canvas, Kept = KeptFour, FedBy = far, Feeding = far, ViewCentre = new TidyPoint(5000, 5000) });

      Assert.Equal(348 + RegionSpacing.Gap, at.X);
    }

    [Fact]
    public void A_group_the_card_named_comes_before_what_it_kept()
    {
      var at = Landing.Find(new LandingAsk { W = 100, H = 40, Canvas = KeptFour, Kept = KeptFour, Into = Box(-1000, -1000, 3000, 3000) });

      Assert.Equal(-980, at.X);
      Assert.Equal(-980, at.Y);
    }

    [Fact]
    public void What_it_landed_beside_is_said()
    {
      var landed = Box(348 + RegionSpacing.Gap, 0, 300, 60);

      Assert.Equal("60 units to the right of what this spot kept", Landing.Where(landed, KeptFour, null, Landing.KeptLabel, Landing.Extent(KeptFour)));
    }

    // ---- the sentence -----------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0, "inside the view")]
    [InlineData(1, "inside the view")]
    [InlineData(2, "inside the view, in two rows")]
    [InlineData(3, "inside the view, in three rows")]
    [InlineData(12, "inside the view, in twelve rows")]
    [InlineData(13, "inside the view, in 13 rows")]
    public void The_sentence_says_how_many_rows(int rows, string expected)
    {
      Assert.Equal(expected, SpotLayout.WithRows("inside the view", rows));
    }

    [Theory]
    [InlineData(2, "in two rows")]
    [InlineData(3, "in three rows")]
    public void With_nowhere_measured_the_rows_still_stand(int rows, string expected)
    {
      Assert.Equal(expected, SpotLayout.WithRows(null, rows));
      Assert.Equal(expected, SpotLayout.WithRows(" ", rows));
    }

    [Fact]
    public void One_row_and_nowhere_measured_says_nothing()
    {
      Assert.Null(SpotLayout.WithRows(null, 1));
      Assert.Null(SpotLayout.RowWords(1));
    }

    private static JsonObject Record() => new JsonObject
    {
      ["components"] = new JsonArray(new JsonObject { ["nick"] = "s1" }, new JsonObject { ["nick"] = "s2" }),
    };

    private static JsonObject Verdict(string where) => Landing.Stamped(new JsonObject(), where);

    [Fact]
    public void The_verdict_says_it_and_the_persons_line_reads_it()
    {
      var where = SpotLayout.WithRows("inside the view", 2);

      Assert.Equal("inside the view, in two rows", Landing.Read(Verdict(where)));
      Assert.StartsWith("Placed s1, s2 in Spot 3, inside the view, in two rows.", SpotAsk.PlacedReport(Record(), "Spot 3", Verdict(where), true));
      Assert.StartsWith("Placed s1, s2 loose on the canvas (Spot 3), inside the view, in three rows.",
        SpotAsk.PlacedReport(Record(), "Spot 3", Verdict(SpotLayout.WithRows("inside the view", 3)), false));
    }

    [Fact]
    public void A_group_it_landed_in_says_its_rows_too()
    {
      var inGroup = Landing.InGroup(Box(0, 0, 100, 100), Box(-10, -10, 500, 500));

      Assert.Equal("inside its bounds, in two rows", SpotLayout.WithRows(inGroup, 2));
    }

    [Fact]
    public void One_row_says_what_it_always_said()
    {
      Assert.StartsWith("Placed s1, s2 in Spot 3, inside the view.", SpotAsk.PlacedReport(Record(), "Spot 3", Verdict(SpotLayout.WithRows("inside the view", 1)), true));
    }

    /// <summary>The card's words are written before anything exists, so they cannot know the rows, and they stay.</summary>
    [Fact]
    public void The_cards_sentence_stays()
    {
      var placement = new SpotPlacement { Address = "Spot 1", Components = Enumerable.Range(1, 12).Select(i => "a Part " + i).ToList() };

      Assert.EndsWith(", Spot 1, in clear space on the canvas.", ConsentCard.SpotAct(placement));
      Assert.EndsWith(", Spot 1, in clear space on the canvas.", ConsentCard.SpotDone(placement));
    }

    // ---- the seams, by text (ADR-091) -------------------------------------------------------------------------------------------------------

    private static string Repo(params string[] parts)
    {
      var dir = AppContext.BaseDirectory;
      while (dir != null && !File.Exists(Path.Combine(dir, "Dwight.sln"))) dir = Path.GetDirectoryName(dir);
      Assert.NotNull(dir);
      return Path.Combine(new[] { dir }.Concat(parts).ToArray());
    }

    private static string Source(string rel) => File.ReadAllText(Repo("Dwight", rel));

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

    private static string Body(string source, string from, string to)
    {
      var start = source.IndexOf(from, StringComparison.Ordinal);
      Assert.True(start >= 0, from);
      var end = source.IndexOf(to, start + from.Length, StringComparison.Ordinal);
      Assert.True(end >= 0, to);
      return source.Substring(start, end - start);
    }

    [Fact]
    public void A_spot_is_measured_then_placed_and_a_map_region_is_left_to_the_tidy()
    {
      var src = Source("Agent/GhBuildRegion.cs");

      InOrder(src, "var packs = kit.FramesItself || ScaffoldAddress.IsSpot(spec.Region);",
                   "var landingFacts = packs ? ReadLandingFacts(d, spec, ids, target, kept) : null;",
                   "d.AddObject(obj, false, d.Objects.Count);",
                   "if (packs) Land(landingFacts, built, kit);",
                   "d.UndoUtil.RecordEvent(UndoName, actions)");
      InOrder(Body(src, "private static void Land(", "private static List<SpaceBox> MeasureBlock("),
        "SpotLayout.Arrange(() => MeasureBlock(built), block => PlaceBlock(kit, block), kit.Roles)", "kit.LandedWhere = SayLanded(kit);");
    }

    [Fact]
    public void A_measure_lays_each_component_out_first()
    {
      var body = Body(Source("Agent/GhBuildRegion.cs"), "private static List<SpaceBox> MeasureBlock(", "return boxes;");

      InOrder(body, "attributes.ExpireLayout();", "attributes.PerformLayout();", "var b = attributes.Bounds;");
    }

    [Fact]
    public void The_clear_place_is_found_for_the_blocks_real_size_and_moves_only_what_was_built()
    {
      var body = Body(Source("Agent/GhBuildRegion.cs"), "private static void PlaceBlock(", "private static string SayLanded(");

      Assert.Contains("W = block.W, H = block.H, Canvas = canvas, Into = facts.Into, Kept = facts.Kept, FedBy = facts.FedBy, Feeding = facts.Feeding", body);
      Assert.Contains("kit.Built[i].Value.Attributes", body);
      Assert.Contains("MoveTo(i, kit.At.X + block.Places[i].X, kit.At.Y + block.Places[i].Y);", body);
      Assert.Contains("(float)(x - now[i].X)", body);
      Assert.DoesNotContain("140", body);
    }

    [Fact]
    public void The_law_is_read_again_after_the_solve_on_the_path_that_stands()
    {
      var src = Source("Agent/GhBuildRegion.cs");

      InOrder(src, "if (!ok || !scriptJudgement.Stands)", "SettleBlock(kit);", "ledger.CommitBuild(spec.Region", "kit.LandedWhere)));");

      var body = Body(src, "private static void SettleBlock(", "/// <summary>The part of the canvas the user is looking at");
      InOrder(body, "SpotLayout.Settle(kit.Block,", "kit.Block = settled;", "kit.LandedWhere = SayLanded(kit);", "kit.BlockGroup?.Attributes?.ExpireLayout();");
      // A rerun must never cost a build that passed its check, and the outer catch reverts.
      Assert.Contains("catch { }", body);
    }

    [Fact]
    public void A_rebuild_reads_what_it_kept_and_the_tidy_leaves_a_spot_where_it_is()
    {
      var src = Source("Agent/GhBuildRegion.cs");
      InOrder(src, "freshRegion = claim.Decision == RebuildDecision.Fresh;", "kept = claim.Adopted.Concat(claim.PlacedEarlier).ToList();");
      InOrder(src, "private static LandingFacts ReadLandingFacts(", "IReadOnlyList<Guid> keptIds = into != null ? GhScaffoldMap.ComponentsIn(d, into) : kept;", "Kept = keptIds.Select(BoxOf)");
      InOrder(src, "spotGroup = MakeSpotGroup(d, spec.Region, created.Values, actions);", "kit.BlockGroup = spotGroup ?? target;");

      var tidy = Source("Agent/GhRegionTidy.cs");
      InOrder(tidy, "foreach (var address in outcomes.Keys", "if (ScaffoldAddress.IsSpot(address)) continue;", "foreach (var kv in LayoutFor(doc, members, ledger?.LastBuilt(address)))");
    }

    [Fact]
    public void Every_sentence_for_where_it_landed_carries_the_rows()
    {
      var body = Body(Source("Agent/GhBuildRegion.cs"), "private static string SayLanded(", "private static void SettleBlock(");

      Assert.Contains("SpotLayout.WithColumns(SpotLayout.WithRows(null, shape.Rows), shape.Columns)", body);
      Assert.Contains("SpotLayout.WithColumns(SpotLayout.WithRows(Landing.InGroup(landed, facts.Into), shape.Rows), shape.Columns)", body);
      Assert.Contains("SpotLayout.WithColumns(SpotLayout.WithRows(where, shape.Rows), shape.Columns)", body);
      Assert.Contains("var landed = Landing.Extent(MeasureBlock(kit.Built));", body);
      Assert.Contains("beside ? Landing.KeptLabel : facts.AnchorLabel", body);
    }

    [Fact]
    public void Nothing_tells_a_reader_it_is_still_a_fixed_grid()
    {
      Assert.DoesNotContain("six to a row", Source("Agent/ConsentCard.cs"));
    }
  }
}
