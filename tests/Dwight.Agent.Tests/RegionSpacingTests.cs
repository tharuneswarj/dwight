using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dwight.Agent;
using Dwight.Session;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// The amendment to #369: built regions that grew into each other are moved apart, and nothing else is. These hold the geometry, the table of what may
  /// move, and the sentence. Whether a real group's box lands where this says is the gate's to read in Rhino.
  /// </summary>
  public class RegionSpacingTests
  {
    private static SpaceBox Grown(string a, double x, double y, double w, double h) => new SpaceBox(a, x, y, w, h, SpaceRole.Grown);
    private static SpaceBox Empty(string a, double x, double y, double w, double h) => new SpaceBox(a, x, y, w, h, SpaceRole.Empty);
    private static SpaceBox Fixed(string a, double x, double y, double w, double h) => new SpaceBox(a, x, y, w, h, SpaceRole.Fixed);
    private static SpaceBox Other(double x, double y, double w, double h) => new SpaceBox("", x, y, w, h, SpaceRole.Fixed);

    private static List<SpaceBox> After(IEnumerable<SpaceBox> boxes, SpacePlan plan) =>
      boxes.Select(b => b.IsRegion && plan.Moves.TryGetValue(b.Address, out var m) ? b.At(m.X, m.Y) : b).ToList();

    private static bool Overlap(SpaceBox a, SpaceBox b) => a.X < b.Right && b.X < a.Right && a.Y < b.Bottom && b.Y < a.Bottom;

    private static List<string> Overlapping(IReadOnlyList<SpaceBox> boxes)
    {
      var found = new List<string>();
      for (var i = 0; i < boxes.Count; i++)
        for (var j = i + 1; j < boxes.Count; j++)
          if (Overlap(boxes[i], boxes[j])) found.Add(boxes[i].Address + "/" + boxes[j].Address);
      return found;
    }

    private static SpaceBox Of(IEnumerable<SpaceBox> boxes, string address) => boxes.Single(b => b.Address == address);

    /// <summary>The gate's lamp: every overlapping region on one card. The first stays, the rest give way in reading order, and the order holds.</summary>
    [Fact]
    public void Regions_on_one_card_stand_clear_and_keep_their_order()
    {
      var boxes = new[]
      {
        Grown("A1", 0, 0, 500, 400), Grown("A2", 0, 300, 500, 400),
        Grown("B1", 400, 0, 500, 400), Grown("B2", 400, 300, 500, 400),
        Grown("C1", 800, 0, 300, 300),
      };

      var plan = RegionSpacing.Plan(boxes);
      var after = After(boxes, plan);

      Assert.Empty(Overlapping(after));
      Assert.Empty(plan.Unplaced);
      Assert.False(plan.Moves.ContainsKey("A1"));   // the first reads first: nothing makes it move
      Assert.True(Of(after, "A1").X < Of(after, "B1").X && Of(after, "B1").X < Of(after, "C1").X);
      Assert.True(Of(after, "A1").Y < Of(after, "A2").Y);
      Assert.True(Of(after, "B1").Y < Of(after, "B2").Y);
    }

    [Fact]
    public void A_later_region_on_the_card_gives_way_forward_and_leaves_the_maps_gap()
    {
      var boxes = new[] { Grown("A1", 0, 0, 500, 400), Grown("B1", 400, 0, 300, 200) };

      var plan = RegionSpacing.Plan(boxes);

      Assert.Single(plan.Moves);
      Assert.Equal(500 + RegionSpacing.Gap - 400, plan.Moves["B1"].X);
      Assert.Equal(0, plan.Moves["B1"].Y);
    }

    [Fact]
    public void Within_a_letter_a_region_gives_way_downward_rather_than_leaving_its_column()
    {
      var boxes = new[] { Grown("A1", 0, 0, 300, 900), Grown("A2", 0, 800, 300, 200) };

      var plan = RegionSpacing.Plan(boxes);

      Assert.Equal(0, plan.Moves["A2"].X);
      Assert.Equal(900 + RegionSpacing.Gap - 800, plan.Moves["A2"].Y);
    }

    /// <summary>A short way out sideways does not take a region out of its column: the map's order is read down a letter, even past a tall neighbour.</summary>
    [Fact]
    public void A_tall_narrow_neighbour_does_not_send_a_region_out_of_its_column()
    {
      var boxes = new[] { Grown("A1", 0, 0, 100, 900), Grown("A2", 0, 100, 100, 200) };

      var plan = RegionSpacing.Plan(boxes);

      Assert.Equal(0, plan.Moves["A2"].X);
      Assert.Equal(900 + RegionSpacing.Gap - 100, plan.Moves["A2"].Y);
    }

    [Fact]
    public void Across_letters_a_region_goes_along_the_row_rather_than_down_out_of_it()
    {
      var boxes = new[] { Grown("A1", 0, 0, 900, 100), Grown("B1", 100, 50, 100, 100) };

      var plan = RegionSpacing.Plan(boxes);

      Assert.Equal(900 + RegionSpacing.Gap - 100, plan.Moves["B1"].X);
      Assert.Equal(0, plan.Moves["B1"].Y);
    }

    /// <summary>Where a region lands it leaves the map's gap, even from something it was never overlapping. Where it already stands, only a true overlap moves it.</summary>
    [Fact]
    public void A_moved_region_lands_with_the_gap_around_it()
    {
      var nearby = Other(880, 0, 100, 200);
      var boxes = new[] { Grown("A1", 0, 0, 500, 400), Grown("B1", 400, 0, 300, 200), nearby };

      var b1 = Of(After(boxes, RegionSpacing.Plan(boxes)), "B1");

      var clearAcross = b1.Right + RegionSpacing.Gap <= nearby.X || nearby.Right + RegionSpacing.Gap <= b1.X;
      var clearDown = b1.Bottom + RegionSpacing.Gap <= nearby.Y || nearby.Bottom + RegionSpacing.Gap <= b1.Y;
      Assert.True(clearAcross || clearDown, b1.X + "," + b1.Y);
    }

    [Fact]
    public void Nothing_moves_when_nothing_overlaps_and_touching_is_not_overlapping()
    {
      var boxes = new[]
      {
        Grown("A1", 0, 0, 200, 100), Grown("B1", 200, 0, 200, 100), Empty("C1", 500, 0, 100, 100), Fixed("D1", 700, 0, 100, 100), Other(900, 0, 50, 50),
      };

      var plan = RegionSpacing.Plan(boxes);

      Assert.Empty(plan.Moves);
      Assert.Empty(plan.Unplaced);
    }

    /// <summary>#369's reason, held: a region off the card that holds work is never moved. What grew moves itself away from it, and the two stand clear.</summary>
    [Fact]
    public void A_neighbour_it_may_not_move_stands_and_what_grew_moves_away_from_it()
    {
      var boxes = new[] { Grown("A1", 0, 0, 500, 400), Fixed("B1", 400, 0, 300, 400) };

      var plan = RegionSpacing.Plan(boxes);
      var after = After(boxes, plan);

      Assert.False(plan.Moves.ContainsKey("B1"));
      Assert.True(plan.Moves["A1"].X < 0);   // away, and back along the map
      Assert.Equal(400 - RegionSpacing.Gap - 500, plan.Moves["A1"].X);
      Assert.Empty(Overlapping(after));
      Assert.True(Of(after, "A1").X < Of(after, "B1").X);
    }

    /// <summary>The decision for a user's move: it is in the way, so it stays, and what grew goes around it. Moved past it would lose the map's order, so that is refused.</summary>
    [Fact]
    public void What_the_user_moved_into_the_way_stands_and_what_grew_never_jumps_past_it()
    {
      var boxes = new[]
      {
        Fixed("B1", 100, 0, 200, 200),   // the user dragged it onto A1
        Grown("A1", 0, 0, 500, 400),
      };

      var plan = RegionSpacing.Plan(boxes);
      var after = After(boxes, plan);

      Assert.False(plan.Moves.ContainsKey("B1"));
      Assert.Empty(Overlapping(after));
      Assert.True(Of(after, "A1").X < Of(after, "B1").X);
    }

    [Fact]
    public void Something_on_the_canvas_that_is_not_a_region_stands_and_is_cleared()
    {
      var boxes = new[] { Grown("A1", 0, 0, 300, 300), Other(250, 50, 100, 60) };

      var after = After(boxes, RegionSpacing.Plan(boxes));

      Assert.Empty(Overlapping(after));
      Assert.Equal(250, after[1].X);
    }

    [Fact]
    public void A_frame_the_user_drew_around_a_region_is_not_something_it_hit()
    {
      var boxes = new[] { Grown("A1", 100, 100, 300, 300), Other(0, 0, 1000, 1000) };

      var plan = RegionSpacing.Plan(boxes);

      Assert.Empty(plan.Moves);
      Assert.Empty(plan.Unplaced);
    }

    /// <summary>The preference: what grew moves, and an empty region it missed holds still.</summary>
    [Fact]
    public void What_grew_moves_before_an_empty_region_does()
    {
      var boxes = new[] { Grown("A1", 0, 0, 500, 200), Empty("B1", 400, 0, 200, 100), Empty("B2", 400, 300, 200, 100) };

      var plan = RegionSpacing.Plan(boxes);

      Assert.True(plan.Moves.ContainsKey("A1"));
      Assert.False(plan.Moves.ContainsKey("B1"));
      Assert.False(plan.Moves.ContainsKey("B2"));
      Assert.Empty(Overlapping(After(boxes, plan)));
    }

    /// <summary>Walls left, above and below: what grew has nowhere of its own to go, so the plan box it landed on makes way forward.</summary>
    [Fact]
    public void An_empty_region_makes_way_when_what_grew_has_no_other_clear_place()
    {
      var walls = new[] { Other(-20000, -20000, 19900, 40000), Other(-100, -20000, 800, 19900), Other(-100, 500, 800, 20000) };
      var boxes = new[] { Grown("A1", 0, 0, 500, 400), Empty("B1", 400, 0, 200, 300) }.Concat(walls).ToArray();

      var plan = RegionSpacing.Plan(boxes);
      var after = After(boxes, plan);

      Assert.False(plan.Moves.ContainsKey("A1"));
      Assert.True(plan.Moves["B1"].X > 0);
      Assert.Empty(plan.Unplaced);
      Assert.Empty(Overlapping(after));
    }

    /// <summary>A canvas with no room: it stays, nothing else moves on its account, and the plan says so for the sentence.</summary>
    [Fact]
    public void With_no_room_a_region_stays_where_it_is_and_is_named()
    {
      var boxes = new[]
      {
        Grown("A1", 0, 0, 500, 400), Fixed("B1", 400, 0, 300, 400),
        Other(-20000, -20000, 19900, 40000), Other(-100, -20000, 20000, 19900), Other(-100, 500, 20000, 20000),
      };

      var plan = RegionSpacing.Plan(boxes);

      Assert.Empty(plan.Moves);
      Assert.Equal(new[] { "A1" }, plan.Unplaced);
    }

    /// <summary>A wall it could only clear by going past the bound is the same as no room: the region is not sent off the map.</summary>
    [Fact]
    public void A_region_travels_no_further_than_the_bound()
    {
      var far = RegionSpacing.MaxShift + 1000;
      var boxes = new[]
      {
        Grown("A1", 0, 0, 100, 100),
        Other(50, -far, far, far * 2),      // over its right half, and past the bound up and down
        Other(-far, -far, far - 10, far * 2),   // and a wall to its left, the same depth
      };

      var plan = RegionSpacing.Plan(boxes);

      Assert.Empty(plan.Moves);
      Assert.Equal(new[] { "A1" }, plan.Unplaced);
    }

    /// <summary>#369's reason again, at the other edge: two plan boxes already on top of each other, or one on a neighbour, are not this build's to move.</summary>
    [Fact]
    public void An_overlap_this_build_had_no_part_in_is_left_alone()
    {
      var boxes = new[]
      {
        Grown("A1", 0, 0, 100, 100),
        Empty("B1", 500, 0, 200, 100), Empty("B2", 600, 50, 200, 100),   // the user's map, already crowded
        Empty("C1", 1500, 0, 200, 100), Fixed("C2", 1600, 50, 200, 100),
      };

      var plan = RegionSpacing.Plan(boxes);

      Assert.Empty(plan.Moves);
      Assert.Empty(plan.Unplaced);
    }

    /// <summary>Over many shapes of map: what may not move never does, no one the map ordered loses its order, what the plan moved stands clear, and the answer does not depend on the order the boxes were read in.</summary>
    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)]
    public void The_laws_hold_over_many_maps(int seed)
    {
      var random = new Random(seed);
      var boxes = new List<SpaceBox>();
      var letters = new[] { "A", "B", "C", "D" };
      for (var l = 0; l < letters.Length; l++)
        for (var n = 1; n <= 3; n++)
        {
          var role = random.Next(4) == 0 ? SpaceRole.Fixed : random.Next(3) == 0 ? SpaceRole.Empty : SpaceRole.Grown;
          boxes.Add(new SpaceBox(letters[l] + n, l * 420, (n - 1) * 260, 200 + random.Next(500), 120 + random.Next(400), role));
        }
      for (var i = 0; i < 3; i++) boxes.Add(Other(-600 + random.Next(2400), -300 + random.Next(1200), 100, 80));

      var plan = RegionSpacing.Plan(boxes);
      var after = After(boxes, plan);

      Assert.DoesNotContain(boxes, b => b.Role == SpaceRole.Fixed && b.IsRegion && plan.Moves.ContainsKey(b.Address));
      Assert.True(plan.Moves.Values.All(m => Math.Abs(m.X) <= RegionSpacing.MaxShift && Math.Abs(m.Y) <= RegionSpacing.MaxShift));

      var regions = boxes.Where(b => b.IsRegion).ToList();
      foreach (var a in regions)
        foreach (var b in regions)
        {
          if (string.CompareOrdinal(a.Address, b.Address) >= 0) continue;
          var sameLetter = a.Address[0] == b.Address[0];
          var was = sameLetter ? a.Y - b.Y : a.X - b.X;
          var will = sameLetter ? Of(after, a.Address).Y - Of(after, b.Address).Y : Of(after, a.Address).X - Of(after, b.Address).X;
          if (was != 0) Assert.Equal(Math.Sign(was), Math.Sign(will));
        }

      // Anything still overlapping involves something the plan could not move, or named, or is two plan boxes this build never touched (#369).
      bool Touched(SpaceBox b) => b.Role == SpaceRole.Grown || (b.IsRegion && plan.Moves.ContainsKey(b.Address));
      for (var i = 0; i < after.Count; i++)
        for (var j = i + 1; j < after.Count; j++)
          if (Overlap(after[i], after[j]) && after[i].Role != SpaceRole.Fixed && after[j].Role != SpaceRole.Fixed && (Touched(after[i]) || Touched(after[j])))
            Assert.True(plan.Unplaced.Contains(after[i].Address) || plan.Unplaced.Contains(after[j].Address), after[i].Address + "/" + after[j].Address);

      var shuffled = boxes.OrderBy(_ => random.Next()).ToList();
      var again = RegionSpacing.Plan(shuffled);
      Assert.Equal(plan.Moves.OrderBy(k => k.Key).Select(k => (k.Key, k.Value.X, k.Value.Y)), again.Moves.OrderBy(k => k.Key).Select(k => (k.Key, k.Value.X, k.Value.Y)));
    }

    // ---------------------------------------------------------------- what may move

    [Theory]
    //          onCard holds  onlyOurs atPlace mapReg dup    role
    [InlineData(true,  true,  true,  true,  true,  false, "Grown")]
    [InlineData(true,  true,  false, true,  true,  false, "Fixed")]   // a rebuild's kept component, or one the user added
    [InlineData(true,  true,  true,  false, true,  false, "Fixed")]   // on the card, and the user moved it
    [InlineData(true,  true,  true,  true,  false, false, "Grown")]   // a spot: it has no scribbles, and all of it is this build's
    [InlineData(true,  true,  true,  true,  true,  true,  "Fixed")]   // two groups answer to one address
    [InlineData(false, true,  false, true,  true,  false, "Fixed")]   // #369's reason: off the card and holding work
    [InlineData(false, true,  true,  true,  true,  false, "Fixed")]
    [InlineData(false, false, false, true,  true,  false, "Empty")]   // a plugin-laid map's plan box
    [InlineData(false, false, false, false, true,  false, "Fixed")]   // empty, and the user moved it
    [InlineData(false, false, false, true,  false, false, "Fixed")]   // not a map's region
    [InlineData(false, false, false, true,  true,  true,  "Fixed")]
    public void What_may_move_is_this_table(bool onCard, bool holds, bool onlyOurs, bool atPlace, bool mapRegion, bool duplicated, string role) =>
      Assert.Equal(role, RegionSpacing.RoleOf(onCard, holds, onlyOurs, atPlace, mapRegion, duplicated).ToString());

    // ---------------------------------------------------------------- the moves, and the guard they go under

    [Fact]
    public void A_moved_region_is_one_translation_on_top_of_the_tidy_and_is_declared_before_it_moves()
    {
      Guid a = Guid.NewGuid(), b = Guid.NewGuid(), bystander = Guid.NewGuid();
      var current = new Dictionary<Guid, TidyPoint> { [a] = new TidyPoint(10, 10), [b] = new TidyPoint(20, 20), [bystander] = new TidyPoint(5, 5) };
      var targets = new Dictionary<Guid, TidyPoint> { [a] = new TidyPoint(100, 100) };

      RegionSpacing.Shift(targets, current, new[] { a, b, Guid.NewGuid() }, new TidyPoint(50, -10));

      Assert.Equal(150, targets[a].X);   // the tidy's place, shifted
      Assert.Equal(90, targets[a].Y);
      Assert.Equal(70, targets[b].X);    // no tidy target: where it stands, shifted
      Assert.Equal(10, targets[b].Y);
      Assert.Equal(2, targets.Count);    // a member the canvas does not hold is left out
      var declared = RegionTidy.Declare(current, targets);
      Assert.Equal(new HashSet<Guid> { a, b }, new HashSet<Guid>(declared));
      Assert.DoesNotContain(bystander, declared);
    }

    [Fact]
    public void A_groups_box_is_predicted_with_the_margin_measured_off_it()
    {
      var group = new RegionTidy.TidyBox("A1", 90, 80, 220, 140);
      var members = new RegionTidy.TidyBox("A1", 100, 100, 200, 100);
      var after = new RegionTidy.TidyBox("A1", 100, 100, 600, 300);

      var box = RegionSpacing.PredictBox(group, members, after);

      Assert.Equal(90, box.X);
      Assert.Equal(80, box.Y);
      Assert.Equal(620, box.W);
      Assert.Equal(340, box.H);
    }

    // ---------------------------------------------------------------- the sentence

    [Fact]
    public void Not_re_spaced_is_said_only_where_nothing_moved()
    {
      var pairs = new List<(string, string)> { ("A1", "B1") };
      Assert.Equal("A1 now overlaps B1. The canvas was not re-spaced - Reorganise will tidy it.", RegionTidy.CrowdingText(pairs));
      Assert.Equal("A1 now overlaps B1. Dwight moved regions apart where it could without moving your work, and these still overlap - Reorganise will tidy it.",
        RegionTidy.CrowdingText(pairs, true));
      Assert.Null(RegionTidy.CrowdingText(new List<(string, string)>(), true));
    }

    [Fact]
    public void Work_that_is_not_a_region_is_named_as_that_once()
    {
      var boxes = new[]
      {
        new RegionTidy.TidyBox("A1", 100, 100, 300, 300),
        new RegionTidy.TidyBox("", 350, 120, 100, 50), new RegionTidy.TidyBox("", 350, 220, 100, 50),
        new RegionTidy.TidyBox("", 3000, 0, 100, 100),
      };

      var pairs = RegionTidy.Crowding(boxes, new HashSet<string> { "A1" });

      Assert.Equal(new[] { ("A1", "other work on the canvas") }, pairs.Select(p => (p.Grown, p.Neighbour)));
    }

    [Theory]
    [InlineData(3, 0, 12, "Laid out 3 regions (12 component(s) moved).")]
    [InlineData(1, 0, 2, "Laid out 1 region (2 component(s) moved).")]
    [InlineData(3, 2, 12, "Laid out 3 regions and moved 2 regions apart (12 component(s) moved).")]
    [InlineData(3, 1, 12, "Laid out 3 regions and moved 1 region apart (12 component(s) moved).")]
    [InlineData(0, 2, 5, "Moved 2 regions apart (5 component(s) moved).")]
    [InlineData(0, 1, 0, "Moved 1 region apart.")]
    public void The_thread_says_what_moved(int tidied, int moved, int components, string said) =>
      Assert.Equal(said + " That is a separate Ctrl-Z from the build, so you can keep the components and undo just the arrangement.", RegionTidy.TidyLine(tidied, moved, components));

    // ---------------------------------------------------------------- the record, and the wiring the gate cannot see fail

    [Fact]
    public void A_user_move_reads_as_one_and_Ctrl_Z_of_a_re_space_does_not()
    {
      var id = Guid.NewGuid();
      var places = new List<StoredRegionPlace>();
      Assert.False(RegionPlacesCore.IsAt(places, id, 10, 10));   // no record: read as moved, and left alone

      RegionPlacesCore.Note(places, id, 10, 10);    // the map landed
      RegionPlacesCore.Note(places, id, 300, 10);   // the re-space moved it

      Assert.True(RegionPlacesCore.IsAt(places, id, 300, 10));
      Assert.True(RegionPlacesCore.IsAt(places, id, 10.2, 9.9));   // Ctrl-Z put it back, through a float
      Assert.False(RegionPlacesCore.IsAt(places, id, 40, 10));     // the user dragged it
    }

    [Fact]
    public void The_record_is_bounded_and_past_the_bound_reads_as_moved()
    {
      var id = Guid.NewGuid();
      var places = new List<StoredRegionPlace>();
      for (var i = 0; i < RegionPlacesCore.MaxPlaces + 2; i++) RegionPlacesCore.Note(places, id, i * 100, 0);
      Assert.Equal(RegionPlacesCore.MaxPlaces, places.Single().At.Count);
      Assert.False(RegionPlacesCore.IsAt(places, id, 0, 0));
      Assert.True(RegionPlacesCore.IsAt(places, id, (RegionPlacesCore.MaxPlaces + 1) * 100, 0));

      var first = Guid.NewGuid();
      places = new List<StoredRegionPlace>();
      RegionPlacesCore.Note(places, first, 1, 1);
      for (var i = 0; i < RegionPlacesCore.MaxObjects; i++) RegionPlacesCore.Note(places, Guid.NewGuid(), 1, 1);
      Assert.Equal(RegionPlacesCore.MaxObjects, places.Count);
      Assert.False(RegionPlacesCore.IsAt(places, first, 1, 1));
    }

    private static string Repo(params string[] parts)
    {
      var dir = new DirectoryInfo(AppContext.BaseDirectory);
      while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Dwight.sln"))) dir = dir.Parent;
      Assert.NotNull(dir);
      return File.ReadAllText(Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray()));
    }

    [Fact]
    public void The_shim_is_wired_as_the_ruling_says()
    {
      var tidy = Repo("Dwight", "Agent", "GhRegionTidy.cs");
      // #369's sentence stays, with the amendment beside it.
      Assert.Contains("#338 is about, in geometry.", tidy);
      Assert.Contains("AMENDED BESIDE THAT SENTENCE", tidy);
      // The moves go under the one declaration, in the one record, computed before anything is written (#353).
      Assert.True(tidy.IndexOf("var regionsMoved = Respace(", StringComparison.Ordinal) < tidy.IndexOf("var declared = RegionTidy.Declare(before, targets);", StringComparison.Ordinal));
      Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(tidy, "RecordEvent\\(UndoName, actions\\), UndoName, TidyDone\\);   // LAST").Count);
      Assert.DoesNotContain("AddSource", tidy);   // moves only: no wire is touched
      // What the plugin notes as its own place is only what it moved: noting every region would launder a move the user made.
      Assert.Contains("NoteScribbles(doc, declared);", tidy);
      Assert.DoesNotContain("NotePlaces(doc, doc.Objects", tidy);
      Assert.DoesNotContain("RemoveSource", tidy);

      var scaffold = Repo("Dwight", "UI", "DwightScaffold.cs");
      Assert.Contains("GhRegionTidy.Run(d, built, attempts, AgentScope.LedgerForDocument(d))", scaffold);
      // "Already laid out" must not swallow the sentence: there is no early return before it.
      var said = scaffold.IndexOf("\"Laying the built regions out: \"", StringComparison.Ordinal);
      var crowding = scaffold.IndexOf("HtmlEncode(outcome.Crowding)", StringComparison.Ordinal);
      Assert.True(said > 0 && crowding > said);
      Assert.DoesNotContain("return;", scaffold.Substring(said, crowding - said));

      Assert.Contains("GhRegionTidy.NotePlaces(d, placedGroups);", Repo("Dwight", "Agent", "GhProposeScaffold.cs"));
    }

    [Fact]
    public void Where_a_region_was_laid_never_reaches_a_models_payload()
    {
      var payloadSide = new[] { Repo("Dwight", "Agent", "GhScaffoldMap.cs"), Repo("Dwight", "Agent", "ScaffoldMapRecord.cs"), Repo("Dwight", "Agent", "AgentTools.cs") };
      Assert.All(payloadSide, src => Assert.DoesNotContain("RegionPlaces", src));
    }
  }
}
