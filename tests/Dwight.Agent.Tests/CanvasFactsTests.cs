using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using Dwight.Agent;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// Phase-9 U23 (lane C), unit 3 - whether the definition is saved, and the canvas layout as fact. The properties
  /// that matter are bounds held by SHAPE: the file fact has exactly two booleans, so a name has nowhere to go; and
  /// no layout field can carry a coordinate, so positions are read and only relations leave (ADR-058).
  /// </summary>
  public class CanvasFactsTests
  {
    private static JsonObject Ok(ToolResult r)
    {
      Assert.True(r.Ok, r.ToJson());
      return (JsonObject)r.Result;
    }

    // ---- file state ----------------------------------------------------------------------------------

    /// <summary>
    /// PRIVACY BY CONSTRUCTION, held by name (the architect's ruling). The file fact is exactly two booleans; a later
    /// "helpful" third field - a name, a path, a date - fails HERE, by name, before it can ship.
    /// </summary>
    [Fact]
    public void The_file_fact_is_exactly_two_booleans()
    {
      var members = typeof(FileStateFact)
        .GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
        .Where(m => m.MemberType == MemberTypes.Property || m.MemberType == MemberTypes.Field)
        .Where(m => !m.Name.Contains("k__BackingField"))
        .ToList();

      Assert.Equal(new[] { "ModifiedSinceSave", "Saved" }, members.Select(m => m.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
      Assert.All(members.OfType<PropertyInfo>(), p => Assert.Equal(typeof(bool), p.PropertyType));
      Assert.Equal(2, typeof(FileStateFact).GetFields(BindingFlags.NonPublic | BindingFlags.Instance).Length);   // the two backing fields, nothing else
    }

    [Fact]
    public void A_saved_definition_says_whether_it_changed_since()
    {
      var clean = Ok(FileStateReport.Build(new FileStateFact { Saved = true, ModifiedSinceSave = false }));
      var dirty = Ok(FileStateReport.Build(new FileStateFact { Saved = true, ModifiedSinceSave = true }));

      Assert.False(clean["modifiedSinceSave"].GetValue<bool>());
      Assert.Equal(FileStateReport.SavedAndClean, clean["state"].GetValue<string>());
      Assert.True(dirty["modifiedSinceSave"].GetValue<bool>());
      Assert.Equal(FileStateReport.SavedAndModified, dirty["state"].GetValue<string>());
    }

    /// <summary>Q1's ruling: "modified since a save" has no referent when there was no save, so it is OMITTED - as
    /// either value it would be a claim about nothing - and the sentence says why.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_never_saved_definition_omits_modified_and_says_why(bool modified)
    {
      var o = Ok(FileStateReport.Build(new FileStateFact { Saved = false, ModifiedSinceSave = modified }));

      Assert.False(o["saved"].GetValue<bool>());
      Assert.Null(o["modifiedSinceSave"]);
      Assert.Equal(FileStateReport.NeverSaved, o["state"].GetValue<string>());
    }

    // ---- layout fixtures -------------------------------------------------------------------------------

    private static LayoutComponent C(int id, double x0, double y0, double x1, double y1) =>
      new LayoutComponent { Id = id, Box = new CanvasBox(x0, y0, x1, y1) };

    private static LayoutGroup G(string key, double x0, double y0, double x1, double y1, string name = null,
      string address = null, params int[] members) =>
      new LayoutGroup { Key = key, Name = name, Address = address, Box = new CanvasBox(x0, y0, x1, y1), Members = members.ToList() };

    private static JsonObject Layout(IEnumerable<LayoutComponent> components = null, IEnumerable<LayoutGroup> groups = null,
      IEnumerable<(int, int)> wires = null) =>
      Ok(CanvasLayoutReport.Build((components ?? new LayoutComponent[0]).ToList(), (groups ?? new LayoutGroup[0]).ToList(),
        (wires ?? new (int, int)[0]).ToList()));

    private static JsonObject Group(JsonObject o, int n) =>
      (JsonObject)((JsonArray)o["groups"]).First(g => g["n"].GetValue<int>() == n);

    // ---- no coordinates ------------------------------------------------------------------------------

    /// <summary>
    /// NO NUMBER A POSITION COULD TRAVEL IN (ADR-058). Every number in the payload is an id, a group number or a count
    /// - so with components placed at large, distinctive coordinates, none of those coordinates appears anywhere.
    /// </summary>
    [Fact]
    public void No_coordinate_appears_anywhere_in_the_payload()
    {
      var o = Layout(
        new[] { C(1, 1234.5, 6789.5, 1334.5, 6889.5), C(2, 1300.5, 6800.5, 1400.5, 6900.5), C(3, 4321.5, 5432.5, 4421.5, 5532.5) },
        new[] { G("g1", 1200.5, 6700.5, 1500.5, 7000.5, "Setup", null, 1, 2) },
        new[] { (3, 1) });

      var text = o.ToJsonString();
      foreach (var coordinate in new[] { "1234", "6789", "1334", "6889", "1300", "6800", "4321", "5432", "1200", "6700", "1500", "7000", ".5" })
        Assert.DoesNotContain(coordinate, text);
    }

    /// <summary>The note says plainly that group numbers are local and not ids, because the risk is a model trying
    /// to pass one (the architect's ruling on Q2).</summary>
    [Fact]
    public void The_payload_says_group_numbers_are_not_ids()
    {
      var note = Layout()["note"].GetValue<string>();

      Assert.Contains("NOT ids", note);
      Assert.Contains("cannot be passed to any tool", note);
      Assert.Contains("this read only", note);
    }

    // ---- groups ---------------------------------------------------------------------------------------

    [Fact]
    public void Groups_are_numbered_left_to_right_and_list_their_members()
    {
      var o = Layout(groups: new[]
      {
        G("b", 500, 0, 600, 100, "Output", null, 9, 7),
        G("a", 0, 0, 100, 100, "Input", null, 3),
      });

      Assert.Equal("Input", Group(o, 1)["name"].GetValue<string>());
      Assert.Equal("Output", Group(o, 2)["name"].GetValue<string>());
      Assert.Equal(new[] { 7, 9 }, ((JsonArray)Group(o, 2)["members"]).Select(m => m.GetValue<int>()).ToArray());
    }

    /// <summary>A position decides the order and the group's key breaks a tie - never a pivot-derived id (WP-A).</summary>
    [Fact]
    public void Two_groups_at_one_position_are_ordered_by_key()
    {
      var o = Layout(groups: new[] { G("zz", 0, 0, 10, 10, "Second"), G("aa", 0, 0, 10, 10, "First") });

      Assert.Equal("First", Group(o, 1)["name"].GetValue<string>());
    }

    [Fact]
    public void A_scaffold_region_carries_its_address()
    {
      var o = Layout(groups: new[] { G("a", 0, 0, 10, 10, "Prepare curves", "A1") });

      Assert.Equal("A1", Group(o, 1)["address"].GetValue<string>());
      Assert.Null(Group(Layout(groups: new[] { G("a", 0, 0, 10, 10, "Mine") }), 1)["address"]);
    }

    [Fact]
    public void Past_the_caps_the_payload_counts_what_it_did_not_list()
    {
      var groups = Enumerable.Range(0, CanvasLayoutReport.GroupCap + 2)
        .Select(i => G("k" + i.ToString("D3"), i * 100, 0, i * 100 + 50, 50)).ToList();
      groups[0].Members = Enumerable.Range(1, CanvasLayoutReport.MemberCap + 5).ToList();

      var o = Layout(groups: groups);

      Assert.Equal(CanvasLayoutReport.GroupCap, ((JsonArray)o["groups"]).Count);
      Assert.Equal(2, o["groupsOmitted"].GetValue<int>());
      Assert.Equal(CanvasLayoutReport.MemberCap, ((JsonArray)Group(o, 1)["members"]).Count);
      Assert.Equal(5, Group(o, 1)["membersOmitted"].GetValue<int>());
    }

    [Fact]
    public void Components_in_no_group_are_counted()
    {
      var o = Layout(new[] { C(1, 0, 0, 1, 1), C(2, 5, 5, 6, 6), C(3, 9, 9, 10, 10) },
                     new[] { G("a", 0, 0, 2, 2, null, null, 1) });

      Assert.Equal(2, o["ungrouped"].GetValue<int>());
    }

    // ---- relative arrangement ---------------------------------------------------------------------------

    /// <summary>
    /// A 2x2 grid of groups, plus one far to the right but OFF the row (diagonal): it is nobody's "right", because
    /// "right of" means across from, sharing some height - not merely further along.
    /// </summary>
    [Fact]
    public void Each_group_knows_its_nearest_neighbour_on_each_side()
    {
      var o = Layout(groups: new[]
      {
        G("tl", 0, 0, 100, 100), G("tr", 200, 0, 300, 100),
        G("bl", 0, 200, 100, 300), G("br", 200, 200, 300, 300),
        G("far", 1000, 500, 1100, 600),
      });
      // reading order: tl(1), bl(2), tr(3), br(4), far(5)
      var tl = Group(o, 1);
      Assert.Equal(3, tl["right"].GetValue<int>());
      Assert.Equal(2, tl["below"].GetValue<int>());
      Assert.Null(tl["left"]);
      Assert.Null(tl["above"]);

      var br = Group(o, 4);
      Assert.Equal(2, br["left"].GetValue<int>());
      Assert.Equal(3, br["above"].GetValue<int>());
      Assert.Null(br["right"]);

      var far = Group(o, 5);
      Assert.Null(far["left"]);
      Assert.Null(far["above"]);
    }

    [Fact]
    public void The_nearest_is_the_one_with_the_smallest_gap()
    {
      var o = Layout(groups: new[] { G("a", 0, 0, 100, 100), G("near", 150, 0, 250, 100), G("farther", 400, 0, 500, 100) });

      Assert.Equal(2, Group(o, 1)["right"].GetValue<int>());
      Assert.Equal(2, Group(o, 3)["left"].GetValue<int>());
    }

    // ---- overlaps ------------------------------------------------------------------------------------

    [Fact]
    public void Components_drawn_over_one_another_are_reported_by_id()
    {
      var o = Layout(new[] { C(4, 0, 0, 100, 50), C(2, 50, 10, 150, 60), C(9, 500, 0, 600, 50) });

      var pairs = (JsonArray)o["overlaps"];
      Assert.Single(pairs);
      Assert.Equal(2, pairs[0]["a"].GetValue<int>());
      Assert.Equal(4, pairs[0]["b"].GetValue<int>());
      Assert.Equal(1, o["overlapsTotal"].GetValue<int>());
    }

    /// <summary>
    /// Boxes that only touch along an edge are neighbours, not a component hidden under another - side by side AND
    /// stacked.
    ///
    /// THE STACKED CASE IS THE ONE THAT TESTS THE RULE. With only the side-by-side pair this test stayed GREEN when
    /// Overlaps was sabotaged to count touching: the sweep drops a box whose right edge meets the next one's left
    /// edge BEFORE Overlaps is ever asked, so the pair never reached the rule this test is named for. Stacked boxes
    /// share their whole width, the sweep keeps them, and Overlaps alone decides.
    /// </summary>
    [Fact]
    public void Touching_is_not_overlapping()
    {
      Assert.Equal(0, Layout(new[] { C(1, 0, 0, 100, 50), C(2, 100, 0, 200, 50) })["overlapsTotal"].GetValue<int>());
      Assert.Equal(0, Layout(new[] { C(1, 0, 0, 100, 50), C(2, 0, 50, 100, 100) })["overlapsTotal"].GetValue<int>());
    }

    /// <summary>The sweep must find exactly the pairs a brute-force check finds, on a scattered canvas.</summary>
    [Fact]
    public void The_sweep_finds_exactly_the_pairs_a_brute_force_check_finds()
    {
      var rng = new Random(7);
      var comps = Enumerable.Range(1, 200).Select(i =>
      {
        var x = rng.Next(0, 3000);
        var y = rng.Next(0, 3000);
        return C(i, x, y, x + rng.Next(20, 160), y + rng.Next(20, 80));
      }).ToList();

      var brute = new List<(int, int)>();
      for (var i = 0; i < comps.Count; i++)
        for (var j = i + 1; j < comps.Count; j++)
          if (comps[i].Box.Overlaps(comps[j].Box))
            brute.Add(comps[i].Id < comps[j].Id ? (comps[i].Id, comps[j].Id) : (comps[j].Id, comps[i].Id));

      Assert.Equal(brute.OrderBy(p => p.Item1).ThenBy(p => p.Item2).ToList(), CanvasLayoutReport.OverlappingPairs(comps));
      Assert.NotEmpty(brute);   // the fixture must actually exercise overlaps, or equality proves nothing
    }

    /// <summary>A group drawn inside another is nesting, not a clash; two whose outlines cross are a clash.</summary>
    [Fact]
    public void Crossing_groups_overlap_and_nested_groups_do_not()
    {
      var o = Layout(groups: new[]
      {
        G("outer", 0, 0, 500, 500), G("inner", 100, 100, 200, 200), G("crossing", 400, 400, 700, 700),
      });

      var pairs = ((JsonArray)o["groupOverlaps"]).Select(p => (p["a"].GetValue<int>(), p["b"].GetValue<int>())).ToList();
      Assert.Equal(new[] { (1, 3) }, pairs);
    }

    // ---- backward wires ------------------------------------------------------------------------------

    [Fact]
    public void A_wire_whose_source_sits_right_of_its_target_is_backward()
    {
      var o = Layout(
        new[] { C(1, 0, 0, 100, 50), C(2, 300, 0, 400, 50), C(3, 50, 100, 150, 150) },
        wires: new[] { (1, 2), (2, 1), (1, 3), (2, 1) });

      var backward = ((JsonArray)o["backwardWires"]).Select(w => (w["from"].GetValue<int>(), w["to"].GetValue<int>())).ToList();
      Assert.Equal(new[] { (2, 1) }, backward);   // once per pair, however many wires join them
      Assert.Equal(1, o["backwardWiresTotal"].GetValue<int>());
      Assert.Contains("right", o["backwardMeans"].GetValue<string>());
    }

    /// <summary>Components that share some width are not backward either way - "backward" is only claimed where the
    /// source sits ENTIRELY to the right.</summary>
    [Fact]
    public void Overlapping_columns_are_never_called_backward()
    {
      var o = Layout(new[] { C(1, 0, 0, 100, 50), C(2, 80, 100, 180, 150) }, wires: new[] { (2, 1), (1, 2) });

      Assert.Equal(0, o["backwardWiresTotal"].GetValue<int>());
    }
  }
}
