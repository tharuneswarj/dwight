using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Dwight.Agent;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// Brief CM: where a placement lands, and whether the person can find it. Tharun's metric, measured on the night's real files: a slider 153 units past the drawing's edge with the view
  /// unmoved, three components on top of existing ones, a request to add to a named region built as a new spot. The search, the anchor order, the margin, the named region and the sentence
  /// are GH-free and held here; the view's frame and the group's growth are the architect's Rhino row.
  /// </summary>
  public class LandingTests
  {
    private static SpaceBox Box(double x, double y, double w = 100, double h = 40) => new SpaceBox("", x, y, w, h, SpaceRole.Fixed);

    private static void AssertClear(TidyPoint at, double w, double h, IEnumerable<SpaceBox> canvas)
    {
      var landed = Box(at.X, at.Y, w, h);
      foreach (var b in canvas)
      {
        var within = landed.X < b.Right + RegionSpacing.Gap - 0.01 && b.X < landed.Right + RegionSpacing.Gap - 0.01 &&
                     landed.Y < b.Bottom + RegionSpacing.Gap - 0.01 && b.Y < landed.Bottom + RegionSpacing.Gap - 0.01;
        Assert.False(within, "landed at " + at.X + "," + at.Y + " within the gap of a box at " + b.X + "," + b.Y);
      }
    }

    // ---- the search ---------------------------------------------------------------------------------------------

    [Fact]
    public void What_feeds_it_puts_it_to_the_right_with_the_gap()
    {
      var source = Box(0, 0);

      var at = Landing.Find(new LandingAsk { W = 120, H = 40, Canvas = new[] { source }, FedBy = new[] { source } });

      Assert.Equal(source.Right + RegionSpacing.Gap, at.X);
      Assert.Equal(0, at.Y);
    }

    [Fact]
    public void What_it_only_feeds_puts_it_to_the_left_with_the_gap()
    {
      var consumer = Box(1000, 500);

      var at = Landing.Find(new LandingAsk { W = 120, H = 40, Canvas = new[] { consumer }, Feeding = new[] { consumer } });

      Assert.Equal(consumer.X - RegionSpacing.Gap - 120, at.X);
      Assert.Equal(500, at.Y);
    }

    /// <summary>The night's "3 of 3 overlapping": something already stands where the old rule would have put it.</summary>
    [Fact]
    public void What_stands_in_the_way_is_gone_round_and_it_stays_near()
    {
      var source = Box(0, 0);
      var inTheWay = Box(source.Right + RegionSpacing.Gap, 0, 300, 200);
      var canvas = new[] { source, inTheWay };

      var at = Landing.Find(new LandingAsk { W = 120, H = 40, Canvas = canvas, FedBy = new[] { source } });

      AssertClear(at, 120, 40, canvas);
      Assert.True(Math.Abs(at.X - source.Right) + Math.Abs(at.Y - source.Y) < 800, "it went far: " + at.X + "," + at.Y);
    }

    [Fact]
    public void The_anchor_order_is_the_named_group_then_the_wires_then_the_view_then_the_drawing()
    {
      var source = Box(0, 0);
      var consumer = Box(3000, 3000);
      var group = Box(6000, 6000, 800, 600);
      var view = new TidyPoint(9000, 9000);

      // The shim leaves the named group's own box out of the canvas, and its members in.
      var all = new LandingAsk { W = 100, H = 40, Canvas = new[] { source, consumer }, Into = group, FedBy = new[] { source }, Feeding = new[] { consumer }, ViewCentre = view };
      Assert.Equal(new TidyPoint(group.X + 20, group.Y + 20), Landing.Find(all));

      all.Canvas = new[] { source, consumer, group };
      all.Into = null;
      Assert.Equal(new TidyPoint(source.Right + RegionSpacing.Gap, 0), Landing.Find(all));

      all.FedBy = new SpaceBox[0];
      Assert.Equal(new TidyPoint(consumer.X - RegionSpacing.Gap - 100, 3000), Landing.Find(all));

      all.Feeding = new SpaceBox[0];
      Assert.Equal(new TidyPoint(view.X - 50, view.Y - 20), Landing.Find(all));

      all.ViewCentre = null;
      Assert.Equal(new TidyPoint(group.Right + RegionSpacing.Gap, 0), Landing.Find(all));
    }

    [Fact]
    public void An_empty_canvas_with_no_view_lands_at_the_origin()
    {
      var at = Landing.Find(new LandingAsk { W = 100, H = 40 });

      Assert.Equal(0, at.X);
      Assert.Equal(0, at.Y);
    }

    /// <summary>A grown region treats a box round its corner as a frame the user drew. Something arriving has no such claim: "never lands on existing work" includes a group the view is inside.</summary>
    [Fact]
    public void A_box_round_the_view_is_work_and_is_not_landed_in()
    {
      var usersGroup = Box(-500, -500, 1000, 1000);

      var at = Landing.Find(new LandingAsk { W = 100, H = 40, Canvas = new[] { usersGroup }, ViewCentre = new TidyPoint(0, 0) });

      AssertClear(at, 100, 40, new[] { usersGroup });
    }

    [Theory]
    [InlineData(60)]        // the gap exactly: it stays
    [InlineData(59)]        // one short: it moves
    public void The_margin_is_the_region_spacings_gap_where_it_starts_too(double gap)
    {
      var neighbour = Box(0, 0);
      var start = new TidyPoint(neighbour.Right + gap + 50, neighbour.Y + 20);

      var at = Landing.Find(new LandingAsk { W = 100, H = 40, Canvas = new[] { neighbour }, ViewCentre = start });

      AssertClear(at, 100, 40, new[] { neighbour });
      Assert.Equal(gap >= RegionSpacing.Gap, at.X == start.X - 50 && at.Y == start.Y - 20);
    }

    /// <summary>When nothing within the search's reach is clear, past the drawing's edge is clear by construction. It is far, and the sentence says how far.</summary>
    [Fact]
    public void With_nothing_clear_in_reach_it_lands_past_the_drawing()
    {
      var enormous = Box(-30000, -30000, 60000, 60000);

      var at = Landing.Find(new LandingAsk { W = 100, H = 40, Canvas = new[] { enormous }, ViewCentre = new TidyPoint(0, 0) });

      Assert.Equal(enormous.Right + RegionSpacing.Gap, at.X);
      AssertClear(at, 100, 40, new[] { enormous });
    }

    /// <summary>The law itself, over a canvas the size of the night's file: 1,300 boxes, many anchors, wired and not.</summary>
    [Fact]
    public void Over_a_crowded_canvas_it_never_lands_within_the_gap_of_any_box()
    {
      var random = new Random(1289);
      var canvas = Enumerable.Range(0, 1300)
        .Select(_ => Box(random.Next(-6000, 6000), random.Next(-4000, 4000), random.Next(40, 400), random.Next(20, 200))).ToList();

      for (var i = 0; i < 300; i++)
      {
        var w = random.Next(100, 900);
        var h = random.Next(30, 400);
        var ask = new LandingAsk { W = w, H = h, Canvas = canvas };
        switch (i % 4)
        {
          case 0: ask.FedBy = new[] { canvas[random.Next(canvas.Count)] }; break;
          case 1: ask.Feeding = new[] { canvas[random.Next(canvas.Count)] }; break;
          case 2: ask.ViewCentre = new TidyPoint(random.Next(-6000, 6000), random.Next(-4000, 4000)); break;
        }

        AssertClear(Landing.Find(ask), w, h, canvas);
      }
    }

    [Fact]
    public void Named_into_a_group_with_room_it_lands_inside_its_box()
    {
      var group = Box(0, 0, 1000, 800);
      var member = Box(20, 20, 200, 100);

      var at = Landing.Find(new LandingAsk { W = 100, H = 40, Canvas = new[] { member }, Into = group });

      AssertClear(at, 100, 40, new[] { member });
      Assert.Equal("inside its bounds", Landing.InGroup(Box(at.X, at.Y, 100, 40), group));
    }

    /// <summary>The nearest clear place to the group's corner is above it, outside. There is room inside, so that is where it goes, and the group does not grow for nothing.</summary>
    [Fact]
    public void Room_inside_the_group_beats_a_nearer_place_outside_it()
    {
      var group = Box(0, 0, 400, 400);
      var member = Box(20, 20, 300, 40);

      var at = Landing.Find(new LandingAsk { W = 100, H = 40, Canvas = new[] { member }, Into = group });

      Assert.Equal("inside its bounds", Landing.InGroup(Box(at.X, at.Y, 100, 40), group));
    }

    [Fact]
    public void Named_into_a_full_group_it_clears_what_the_group_holds_and_the_group_grows()
    {
      var group = Box(0, 0, 240, 140);
      var member = Box(20, 20, 200, 100);

      var at = Landing.Find(new LandingAsk { W = 100, H = 40, Canvas = new[] { member }, Into = group });

      AssertClear(at, 100, 40, new[] { member });
      Assert.Contains("past its old edge, so the group grew to hold it", Landing.InGroup(Box(at.X, at.Y, 100, 40), group));
    }

    // ---- the sentence ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(2150, 0, "to the right of the drawing, 150 units past its edge")]
    [InlineData(-300, 0, "to the left of the drawing, 200 units past its edge")]
    [InlineData(0, 1500, "below the drawing, 500 units past its edge")]
    [InlineData(0, -140, "above the drawing, 100 units past its edge")]
    [InlineData(3500, 0, "to the right of the drawing, 1,500 units past its edge")]
    public void Past_the_drawing_it_says_which_side_and_how_far(double x, double y, string says)
    {
      var canvas = new[] { Box(0, 0, 2000, 1000) };

      Assert.Equal(says, Landing.Where(Box(x, y), canvas));
    }

    [Fact]
    public void Inside_the_drawing_it_says_the_view_or_how_far_in()
    {
      var canvas = new[] { Box(0, 0, 2000, 1000) };

      Assert.Equal("inside the view", Landing.Where(Box(500, 500), canvas, Box(400, 400, 800, 400)));
      Assert.Equal("inside the drawing, 500 units from its left edge", Landing.Where(Box(500, 500), canvas, Box(1500, 0, 100, 100)));
      Assert.Equal("inside the drawing, 500 units from its left edge", Landing.Where(Box(500, 500), canvas));
    }

    [Fact]
    public void How_far_in_is_measured_from_the_drawings_edge_not_from_zero()
    {
      Assert.Equal("inside the drawing, 500 units from its left edge", Landing.Where(Box(1500, 500), new[] { Box(1000, 0, 2000, 1000) }));
    }

    /// <summary>Half out of the view is not inside it: the person would be looking at half of what was built.</summary>
    [Theory]
    [InlineData(1150, 500)]   // past its right edge
    [InlineData(500, 790)]    // past its bottom edge
    public void Straddling_the_views_edge_is_not_inside_the_view(double x, double y)
    {
      var says = Landing.Where(Box(x, y), new[] { Box(0, 0, 2000, 1000) }, Box(400, 400, 800, 400));

      Assert.StartsWith("inside the drawing,", says);
    }

    /// <summary>Past the drawing is the more useful thing to say, even with the view on it, because it has a number and it stays true after the view moves.</summary>
    [Fact]
    public void Past_the_drawing_is_said_even_when_the_view_is_on_it()
    {
      Assert.Equal("to the right of the drawing, 150 units past its edge", Landing.Where(Box(2150, 0), new[] { Box(0, 0, 2000, 1000) }, Box(0, 0, 4000, 2000)));
    }

    [Fact]
    public void With_nothing_else_on_the_canvas_it_says_so_and_does_not_invent_a_drawing()
    {
      Assert.Equal("inside the view", Landing.Where(Box(0, 0), new SpaceBox[0], Box(-100, -100, 400, 400)));
      Assert.Equal("with nothing else on the canvas", Landing.Where(Box(0, 0), new SpaceBox[0]));
    }

    [Theory]
    [InlineData(160, 0, "60 units to the right of Radius")]
    [InlineData(-160, 0, "60 units to the left of Radius")]
    [InlineData(0, 100, "60 units below Radius")]
    [InlineData(0, -100, "60 units above Radius")]
    public void A_wired_landing_says_how_far_from_what_it_wires_to(double x, double y, string says)
    {
      Assert.Equal(says, Landing.Where(Box(x, y), new[] { Box(0, 0) }, null, "Radius", Box(0, 0)));
    }

    /// <summary>Two wired ends either side of where it landed: "N units right of" would not be true of the pair, so it reads the drawing instead.</summary>
    [Fact]
    public void A_landing_inside_the_box_of_what_it_wires_to_does_not_claim_a_side()
    {
      var ends = Landing.Extent(new[] { Box(0, 0), Box(1000, 0) });

      Assert.Equal("inside the view", Landing.Where(Box(500, 0), new[] { ends }, Box(0, -100, 2000, 400), "Radius", ends));
    }

    [Fact]
    public void A_run_line_says_each_region_and_none_for_nothing()
    {
      Assert.Equal("Built A1 inside the view. Built A2 to the right of the drawing, 150 units past its edge.",
        Landing.RunLine(new[] { ("A1", "inside the view"), ("A2", "to the right of the drawing, 150 units past its edge") }));
      Assert.Null(Landing.RunLine(new (string, string)[0]));
    }

    // ---- the report ------------------------------------------------------------------------------------------------

    private static JsonObject Record() => JsonNode.Parse("{\"components\":[{\"ref\":\"a\",\"type\":\"Number Slider\",\"nick\":\"Radius\"}]}").AsObject();

    private static JsonObject Verdict(string where) => Landing.Stamped(new JsonObject(), where);

    [Fact]
    public void The_report_says_where_from_what_the_build_measured()
    {
      Assert.StartsWith("Placed Radius in Spot 3, to the right of the drawing, 150 units past its edge.",
        SpotAsk.PlacedReport(Record(), "Spot 3", Verdict("to the right of the drawing, 150 units past its edge"), true));
      Assert.StartsWith("Placed Radius loose on the canvas (Spot 3), inside the view.",
        SpotAsk.PlacedReport(Record(), "Spot 3", Verdict("inside the view"), false));
      Assert.StartsWith("Placed Radius in Answer Key, inside its bounds.",
        SpotAsk.PlacedReport(Record(), "Answer Key", Verdict("inside its bounds"), true));
    }

    /// <summary>A build whose measure failed has nothing to say about where, and says nothing false.</summary>
    [Fact]
    public void With_no_measure_the_report_is_the_old_line()
    {
      Assert.StartsWith("Placed Radius in Spot 3.", SpotAsk.PlacedReport(Record(), "Spot 3", Verdict(null), true));
      Assert.Equal("Placed Radius in Spot 3.", SpotAsk.PlacedReport(Record(), "Spot 3", null, true));
    }

    /// <summary>The mint's reader keeps a number from being reissued by finding "Spot N" in the saved text, so the sentence must not cost it that.</summary>
    [Fact]
    public void The_report_still_holds_the_spot_for_the_mints_reader()
    {
      var report = SpotAsk.PlacedReport(Record(), "Spot 7", Verdict("inside the view"), true);

      Assert.Contains("Spot 7", SpotMint.InUse(new string[0], new[] { report }));
    }

    // ---- the names ---------------------------------------------------------------------------------------------------

    private static readonly Guid AnswerKeyId = Guid.NewGuid();
    private static readonly Guid A1Id = Guid.NewGuid();
    private static readonly Guid GridId = Guid.NewGuid();

    private static List<NamedGroup> Groups() => new List<NamedGroup>
    {
      NamedGroup.Of(AnswerKeyId, "Answer Key"),
      NamedGroup.Of(A1Id, "A1 - Inputs"),
      NamedGroup.Of(GridId, "Grid"),
    };

    [Theory]
    [InlineData("Answer Key")]
    [InlineData("answer key")]
    [InlineData("  Answer Key ")]
    public void A_group_is_found_by_its_name(string asked)
    {
      Assert.True(RegionNames.Resolve(asked, Groups(), out var group, out var error), error?.Message);
      Assert.Equal(AnswerKeyId, group.Guid);
      Assert.Equal("Answer Key", group.Display);
    }

    [Theory]
    [InlineData("A1")]
    [InlineData("a1")]
    [InlineData("Inputs")]
    [InlineData("A1 - Inputs")]
    public void A_region_is_found_by_its_address_its_name_or_both_and_shown_by_its_address(string asked)
    {
      Assert.True(RegionNames.Resolve(asked, Groups(), out var group, out var error), error?.Message);
      Assert.Equal(A1Id, group.Guid);
      Assert.Equal("A1", group.Display);
    }

    [Fact]
    public void A_name_that_is_not_there_is_refused_with_the_ones_that_are()
    {
      Assert.False(RegionNames.Resolve("My Answers", Groups(), out var group, out var error));

      Assert.Null(group);
      Assert.Equal(ToolErrors.UnknownRegion, error.Code);
      Assert.Contains("\"My Answers\" is not a region or group on this canvas", error.Message);
      Assert.Contains("A1, Answer Key, Grid", error.Message);
    }

    [Fact]
    public void A_refusals_list_is_bounded_and_says_how_many_more()
    {
      var many = Enumerable.Range(0, 40).Select(i => NamedGroup.Of(Guid.NewGuid(), "Group " + i.ToString("00"))).ToList();

      Assert.False(RegionNames.Resolve("Nope", many, out _, out var error));

      Assert.Contains("and 28 more", error.Message);
      Assert.DoesNotContain("Group 39", error.Message);
    }

    [Fact]
    public void A_canvas_with_no_named_groups_says_so()
    {
      Assert.False(RegionNames.Resolve("Anything", new NamedGroup[0], out _, out var error));

      Assert.Contains("This canvas has no named groups", error.Message);
    }

    [Fact]
    public void Two_groups_with_one_name_name_no_place()
    {
      var two = new List<NamedGroup> { NamedGroup.Of(Guid.NewGuid(), "Notes"), NamedGroup.Of(Guid.NewGuid(), "notes") };

      Assert.False(RegionNames.Resolve("Notes", two, out var group, out var error));

      Assert.Null(group);
      Assert.Contains("does not name one place", error.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("Group")]
    public void A_group_nobody_named_cannot_be_named(string nick)
    {
      Assert.Null(NamedGroup.Of(Guid.NewGuid(), nick));
    }

    [Theory]
    [InlineData("add a check to my answer key region")]
    [InlineData("Put it in Answer Key please")]
    [InlineData("into region A1")]
    [InlineData("the A1 region needs a slider")]
    [InlineData("put it into A1")]
    [InlineData("put it inside the Grid")]
    [InlineData("add it to the Grid group")]
    [InlineData("add a slider to group Grid")]
    public void The_users_words_name_a_destination(string text)
    {
      Assert.NotNull(RegionNames.NamedIn(text, Groups()));
    }

    /// <summary>The architect's negative. A bare address or a common word is not a destination: a refusal here would cost a build the person wanted, and the model can still name the place.</summary>
    [Theory]
    [InlineData("put a1 beside it")]
    [InlineData("a1")]
    [InlineData("add a slider to the grid")]
    [InlineData("connect it to inputs")]
    [InlineData("make the grid denser")]
    [InlineData("add to my answer keys")]       // not the name: a word that merely starts with it
    [InlineData("add to my homework region")]   // a place that is not on the canvas names nothing
    [InlineData("")]
    [InlineData(null)]
    public void The_users_words_do_not_name_a_destination(string text)
    {
      Assert.Null(RegionNames.NamedIn(text, Groups()));
    }

    // ---- the ask ---------------------------------------------------------------------------------------------------

    private static SpecResolvers Resolvers(string userText = null) => new SpecResolvers
    {
      TypeExists = _ => true,
      Nearest = _ => new string[0],
      IdState = _ => IdState.Alive,
      Groups = Groups,
      LastUserText = () => userText,
    };

    private static HandoffSeed Seed(string extra = "") =>
      new HandoffSeed { Place = JsonNode.Parse("{\"components\":[{\"ref\":\"p\",\"type\":\"Panel\",\"nick\":\"Check\"}]" + extra + "}").AsObject() };

    private static bool Prepare(HandoffSeed seed, string userText, out SpotAsk ask, out ToolResult error) =>
      SpotAsk.Prepare(seed, Resolvers(userText), _ => null, new SpotMint(), new string[0], out ask, out error);

    [Fact]
    public void A_placement_naming_a_group_lands_in_it_and_the_card_says_so()
    {
      var seed = Seed(",\"region\":\"Answer Key\"");

      Assert.True(Prepare(seed, null, out var ask, out var error), error?.Message);

      Assert.Equal(AnswerKeyId.ToString(), seed.IntoGroup);
      Assert.Equal("Answer Key", seed.IntoName);
      Assert.Equal("Answer Key", ask.Placement.Into);
      Assert.Null(seed.Place["region"]);   // the record is what the click builds, and its address is the plugin's
      Assert.Equal("Place a Check in Answer Key.", ConsentCard.SpotAct(ask.Placement));
      Assert.Equal("Placed a Check in Answer Key.", ConsentCard.SpotDone(ask.Placement));
      Assert.Contains("inside Answer Key, which grows to hold it, and what it already holds stays", ConsentCard.SpotAuthority(ask.Placement, true));
    }

    /// <summary>One consequence, one button. The loose option has no meaning for a place the request named.</summary>
    [Fact]
    public void A_card_with_a_named_place_has_one_button_and_one_without_has_two()
    {
      var named = Seed(",\"region\":\"Answer Key\"");
      Assert.True(Prepare(named, null, out var into, out _));
      var one = ConsentCard.SpotHtml("why", into.Placement, "tok-g", "tok-l");
      Assert.Contains("Place in Answer Key", one);
      Assert.Contains("tok-g", one);
      Assert.DoesNotContain("tok-l", one);
      Assert.Equal(2, one.Split(new[] { "Dwight will place" }, StringSplitOptions.None).Length);   // one consequence, stated once

      Assert.True(Prepare(Seed(), null, out var own, out _));
      var two = ConsentCard.SpotHtml("why", own.Placement, "tok-g", "tok-l");
      Assert.Contains("tok-g", two);
      Assert.Contains("tok-l", two);
    }

    [Theory]
    [InlineData(",\"region\":\"Spot 9\"", "not a region or group on this canvas")]
    [InlineData(",\"region\":\"\"", "not a region or group on this canvas")]
    [InlineData(",\"region\":7", "not a region or group on this canvas")]
    public void A_place_that_is_not_there_is_refused_and_mints_nothing(string extra, string says)
    {
      var seed = Seed(extra);
      var mint = new SpotMint();

      Assert.False(SpotAsk.Prepare(seed, Resolvers(), _ => null, mint, new string[0], out _, out var error));

      Assert.Contains(says, error.Message);
      Assert.Null(seed.Spot);
      Assert.Null(seed.IntoGroup);
      Assert.Equal("Spot 1", mint.Reserve(new string[0]));   // the number is still free
    }

    /// <summary>The teaching file's failure. The model did not name the place, and the user did.</summary>
    [Fact]
    public void A_place_the_user_named_and_the_model_did_not_is_refused_with_both_doors()
    {
      Assert.False(Prepare(Seed(), "add this to my answer key region", out _, out var error));

      Assert.Contains("names \"Answer Key\"", error.Message);
      Assert.Contains("\"region\": \"Answer Key\"", error.Message);
      Assert.Contains("\"separate\": true", error.Message);
    }

    [Fact]
    public void Separate_makes_the_card_say_it_is_not_in_that_place()
    {
      var seed = Seed(",\"separate\":true");

      Assert.True(Prepare(seed, "add this beside my answer key region", out var ask, out var error), error?.Message);

      Assert.Null(seed.IntoGroup);
      Assert.Equal("Answer Key", ask.Placement.NotIn);
      Assert.Contains("not in Answer Key", ConsentCard.SpotAct(ask.Placement));
      Assert.Contains("not in Answer Key", ConsentCard.SpotDone(ask.Placement));
      Assert.Contains("not in Answer Key", ConsentCard.SpotAuthority(ask.Placement, true));
      Assert.Contains("not in Answer Key", ConsentCard.SpotAuthority(ask.Placement, false));
    }

    [Fact]
    public void Separate_says_nothing_when_nothing_was_named()
    {
      Assert.True(Prepare(Seed(",\"separate\":true"), "put a note down", out var ask, out var error), error?.Message);

      Assert.Null(ask.Placement.NotIn);
      Assert.DoesNotContain("not in", ConsentCard.SpotAct(ask.Placement));
    }

    [Fact]
    public void The_model_naming_the_place_answers_the_user_naming_it()
    {
      var seed = Seed(",\"region\":\"Answer Key\"");

      Assert.True(Prepare(seed, "add this to my answer key region", out var ask, out var error), error?.Message);

      Assert.Equal(AnswerKeyId.ToString(), seed.IntoGroup);
      Assert.Null(ask.Placement.NotIn);
    }

    [Fact]
    public void A_bare_address_in_the_users_words_does_not_stop_a_placement()
    {
      Assert.True(Prepare(Seed(), "put a1 beside it", out var ask, out var error), error?.Message);

      Assert.Null(ask.Into);
      Assert.Null(ask.Placement.NotIn);
    }

    /// <summary>The placement itself is unchanged by where it lands: "region" and "separate" are the ask's, and never reach the spec as a region.</summary>
    [Fact]
    public void Naming_a_place_does_not_make_the_spec_a_region_build()
    {
      Assert.True(Prepare(Seed(",\"region\":\"Answer Key\",\"separate\":true"), null, out var ask, out var error), error?.Message);

      Assert.Null(ask.Spec.Region);
      Assert.Null(ask.Record["region"]);
      Assert.Null(ask.Record["separate"]);
    }

    // ---- the carriers ---------------------------------------------------------------------------------------------

    [Fact]
    public void The_group_travels_as_an_identity_on_both_a_seed_and_its_option()
    {
      var seed = Seed(",\"region\":\"Answer Key\"");
      Assert.True(Prepare(seed, null, out _, out _));

      var plan = SeedPlan.From("build", seed.ForOption(grouped: true), id => IdState.Unknown);

      Assert.Equal(AnswerKeyId, plan.PreFillIntoGroup);
      Assert.Equal("Answer Key", plan.PreFillIntoName);
    }

    [Fact]
    public void A_spot_with_no_named_place_carries_none()
    {
      var seed = Seed();
      Assert.True(Prepare(seed, null, out _, out _));

      var plan = SeedPlan.From("build", seed, id => IdState.Unknown);

      Assert.Null(plan.PreFillIntoGroup);
      Assert.Null(plan.PreFillIntoName);
    }

    /// <summary>The model cannot point a card at a group by a field of its own: the parser reads none of these, and only the ask writes them (#346's shape).</summary>
    [Fact]
    public void A_model_cannot_write_the_group_a_card_lands_in()
    {
      var args = JsonNode.Parse("{\"kind\":\"build\",\"why\":\"x\",\"intoGroup\":\"" + AnswerKeyId + "\",\"IntoGroup\":\"" + AnswerKeyId + "\",\"IntoName\":\"x\"," +
                                "\"place\":{\"components\":[{\"ref\":\"p\",\"type\":\"Panel\"}]}}").AsObject();

      Assert.True(AgentHostTools.TryParseRequest(args, new MutationRequestLog(), "stamp", null, null, null, out _, out _, out var seed, out var error), error?.Message);

      Assert.Null(seed.IntoGroup);
      Assert.Null(seed.IntoName);
    }

    [Fact]
    public void The_permission_holds_the_group_for_that_spot_only()
    {
      var ledger = new BuildLedger((object)null);
      ledger.AuthoriseSpot("Spot 3", true, AnswerKeyId);

      Assert.Equal(AnswerKeyId, ledger.SpotInto("Spot 3"));
      Assert.Null(ledger.SpotInto("Spot 4"));
      Assert.Null(ledger.SpotInto("A1"));
      Assert.Null(ledger.SpotInto(null));
    }

    // ---- what the model is told ------------------------------------------------------------------------------------

    /// <summary>A prompt claim about our own behaviour is a contract, and carries a test on the behaviour's side (#315): the fields it publishes are the fields the ask reads.</summary>
    [Fact]
    public void The_published_place_names_the_fields_the_ask_reads()
    {
      var schema = AgentHostTools.All().Single(t => t.Name == AgentHostTools.RequestMutation).InputSchemaJson;
      var place = JsonNode.Parse(schema)["properties"]["place"]["properties"];

      Assert.Equal("string", place["region"]["type"].GetValue<string>());
      Assert.Equal("boolean", place["separate"]["type"].GetValue<string>());
      Assert.Contains("ALREADY on the canvas", place["region"]["description"].GetValue<string>());
    }
  }
}
