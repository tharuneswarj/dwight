using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Dwight.Agent;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// WP-4 Stage A: reading a scaffold map back, and the join that makes it possible.
  ///
  /// The defect this closes (found in the WP-4 plan review, ruled at #355): the stored map's two halves
  /// had NO JOIN KEY. The plan half is keyed by the model's <c>ref</c>; the assigned half carried
  /// <c>key</c> (an address) and no ref at all - and its <c>dependsOn</c> held REFS while its <c>key</c>
  /// held ADDRESSES, so the assigned half expressed its own dependency graph in a namespace it could not
  /// resolve. Getting from "A1" to A1's pseudo-code had no exact route, and WP-4 builds from exactly that.
  /// </summary>
  public class ScaffoldMapReadTests
  {
    // ---- the address: ONE definition of what it means -------------------------------------------

    [Theory]
    [InlineData("A1", "A", 1)]
    [InlineData("B12", "B", 12)]
    [InlineData("AA3", "AA", 3)]
    public void An_address_parses_into_its_letters_and_its_number(string address, string letters, int number)
    {
      Assert.True(ScaffoldAddress.TryParse(address, out var gotLetters, out var gotNumber));
      Assert.Equal(letters, gotLetters);
      Assert.Equal(number, gotNumber);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("A")]           // no number
    [InlineData("1")]           // no letters
    [InlineData("a1")]          // addresses are upper case; the plugin assigns them
    [InlineData("A1b")]         // trailing junk
    [InlineData("Prepare curves")]
    public void Anything_that_is_not_an_address_is_refused_rather_than_coerced(string text)
    {
      Assert.False(ScaffoldAddress.TryParse(text, out _, out _));
    }

    /// <summary>#286's lesson, one level down: an address exists to say "read this first", and sorting it
    /// as TEXT puts A10 before A2 - breaking the one property the address was created to carry.</summary>
    [Fact]
    public void Addresses_order_by_layer_then_NUMERICALLY()
    {
      var shuffled = new[] { "B2", "A10", "A2", "B1", "A1" };

      var sorted = shuffled.OrderBy(ScaffoldAddress.SortKey).ToArray();

      Assert.Equal(new[] { "A1", "A2", "A10", "B1", "B2" }, sorted);
    }

    /// <summary>#221(b): the address is recoverable FROM THE CANVAS, so the map survives even if the
    /// stored record is lost - and WP-4 addresses a region by an object that exists.</summary>
    [Fact]
    public void A_group_name_yields_its_address_and_its_name()
    {
      Assert.True(ScaffoldAddress.FromGroupName("A1 - Prepare curves", out var address, out var name));
      Assert.Equal("A1", address);
      Assert.Equal("Prepare curves", name);
    }

    [Theory]
    [InlineData("Prepare curves")]              // a user's own group
    [InlineData("A1")]                          // no separator, so no name
    [InlineData("Notes - scratch")]             // separator, but the head is not an address
    [InlineData(null)]
    public void A_group_that_is_not_a_scaffold_region_is_not_read_as_one(string nickname)
    {
      Assert.False(ScaffoldAddress.FromGroupName(nickname, out _, out _));
    }

    // ---- the stored map ---------------------------------------------------------------------------

    private const string PlanArgs = @"{""regions"":[
      {""ref"":""prep"",""name"":""Prepare curves"",""purpose"":""Clean the input"",
       ""pseudo"":[""explode"",""join""],""inputs"":[""curves from Divide (42)""],
       ""outputs"":[""clean curves""],""anchorIds"":[42]},
      {""ref"":""loft"",""name"":""Loft the surface"",""purpose"":""Build the roof"",
       ""dependsOn"":[""prep""],""anchorIds"":[]}]}";

    /// <summary>The v2 verdict: every region carries its REF (the join key) and expresses dependsOn in
    /// ADDRESSES - the plugin's namespace, which is what Build builds by.</summary>
    private const string VerdictV2 = @"{""result"":{""regions"":[
      {""ref"":""prep"",""key"":""A1"",""name"":""Prepare curves"",""group"":""A1 - Prepare curves"",""dependsOn"":[]},
      {""ref"":""loft"",""key"":""B1"",""name"":""Loft the surface"",""group"":""B1 - Loft the surface"",""dependsOn"":[""A1""]}]}}";

    /// <summary>
    /// A v1 map AS AN OLDER BUILD ACTUALLY WROTE ONE - constructed literally, because the current
    /// producer cannot make one: <c>Build</c> stamps <c>CurrentMapVersion</c>, so passing it a v1-shaped
    /// verdict would produce a v2 record and the test would be asserting on the wrong artifact.
    ///
    /// (The first draft of this test did exactly that and FAILED, which is the reason to write the
    /// subject out by hand: a test that builds its own subject through the code under test can only ever
    /// see what that code can currently produce, and the whole point here is reading what it USED to.)
    ///
    /// Note the two defects it carries, which are what made it unjoinable: no <c>ref</c> on the assigned
    /// regions, and a <c>dependsOn</c> holding the model's REF beside a <c>key</c> holding an ADDRESS.
    /// </summary>
    private const string StoredV1 = @"{""mapVersion"":1,
      ""plan"":{""regions"":[
        {""ref"":""prep"",""name"":""Prepare curves"",""purpose"":""Clean the input"",
         ""pseudo"":[""explode"",""join""],""anchorIds"":[42]},
        {""ref"":""loft"",""name"":""Loft the surface"",""purpose"":""Build the roof"",""dependsOn"":[""prep""]}]},
      ""assigned"":[
        {""key"":""A1"",""name"":""Prepare curves"",""group"":""A1 - Prepare curves"",""dependsOn"":[]},
        {""key"":""B1"",""name"":""Loft the surface"",""group"":""B1 - Loft the surface"",""dependsOn"":[""prep""]}]}";

    private static List<CanvasRegion> Canvas(params string[] addresses) =>
      addresses.Select(a => new CanvasRegion { Address = a, Name = "on canvas", ComponentCount = 0 }).ToList();

    [Fact]
    public void A_v2_map_joins_its_plan_to_its_addresses()
    {
      var stored = ScaffoldMapRecord.Build(PlanArgs, VerdictV2);

      var read = ScaffoldMapRecord.Read(stored, Canvas("A1", "B1"));

      Assert.True(read.PlanAvailable);
      Assert.Equal(2, read.Regions.Count);

      var a1 = read.Regions.Single(r => r.Address == "A1");
      Assert.Equal("Prepare curves", a1.Name);
      Assert.Equal("Clean the input", a1.Purpose);
      Assert.Equal(new[] { "explode", "join" }, a1.Pseudo);
      Assert.Equal(new[] { "curves from Divide (42)" }, a1.Inputs);
      Assert.Equal(new[] { "clean curves" }, a1.Outputs);
      Assert.Equal(new[] { 42 }, a1.AnchorIds);
      Assert.True(a1.OnCanvas);

      // dependsOn is in ADDRESSES, so it resolves against the same namespace the user says out loud.
      var b1 = read.Regions.Single(r => r.Address == "B1");
      Assert.Equal(new[] { "A1" }, b1.DependsOn);
    }

    [Fact]
    public void Regions_come_back_in_address_order_not_declaration_order()
    {
      var stored = ScaffoldMapRecord.Build(PlanArgs, VerdictV2);

      var read = ScaffoldMapRecord.Read(stored, Canvas("B1", "A1"));

      Assert.Equal(new[] { "A1", "B1" }, read.Regions.Select(r => r.Address).ToArray());
    }

    /// <summary>#355: a v1 map cannot be joined without GUESSING, so the plan half is refused - by name,
    /// with what to do about it - and the CANVAS half still answers, because the canvas is authoritative
    /// and what it says is true regardless of the record's age (#221b).</summary>
    [Fact]
    public void A_v1_map_refuses_its_plan_half_honestly_and_keeps_what_the_canvas_says()
    {
      var read = ScaffoldMapRecord.Read(StoredV1, Canvas("A1", "B1"));

      Assert.False(read.PlanAvailable);
      Assert.Contains(read.Notes, n => n.Contains("earlier build") && n.Contains("propose_scaffold"));

      // The canvas half is intact and honest...
      Assert.Equal(new[] { "A1", "B1" }, read.Regions.Select(r => r.Address).ToArray());
      Assert.All(read.Regions, r => Assert.True(r.OnCanvas));
      // ...and NOTHING from the unjoinable plan leaked into it.
      Assert.All(read.Regions, r => Assert.False(r.PlanKnown));
      Assert.All(read.Regions, r => Assert.Empty(r.Pseudo));
      Assert.All(read.Regions, r => Assert.Empty(r.AnchorIds));
    }

    [Fact]
    public void With_no_stored_record_the_canvas_still_answers_and_says_the_plan_is_missing()
    {
      var read = ScaffoldMapRecord.Read(null, Canvas("A1"));

      Assert.False(read.PlanAvailable);
      Assert.Single(read.Regions);
      Assert.Equal("A1", read.Regions[0].Address);
      Assert.Contains(read.Notes, n => n.Contains("no stored plan"));
    }

    /// <summary>A region in the plan whose group is gone: reported as not on the canvas AND named, never
    /// silently listed as though it were there. An affordance is an assertion that its target exists
    /// (#321) - and so is a map entry Build might aim at.</summary>
    [Fact]
    public void A_planned_region_with_no_group_on_the_canvas_is_named_not_hidden()
    {
      var stored = ScaffoldMapRecord.Build(PlanArgs, VerdictV2);

      var read = ScaffoldMapRecord.Read(stored, Canvas("A1"));   // B1's group was undone

      var b1 = read.Regions.Single(r => r.Address == "B1");
      Assert.False(b1.OnCanvas);
      Assert.True(b1.PlanKnown);   // the plan is still known; only the group is gone
      Assert.Contains(read.Notes, n => n.Contains("B1") && n.Contains("no group"));
    }

    /// <summary>The other direction: a scaffold group on the canvas that the record does not know about -
    /// a second map, or a record that was trimmed. Reported rather than dropped (#134).</summary>
    [Fact]
    public void A_canvas_region_the_plan_does_not_know_is_reported_not_dropped()
    {
      var stored = ScaffoldMapRecord.Build(PlanArgs, VerdictV2);

      var read = ScaffoldMapRecord.Read(stored, Canvas("A1", "B1", "C1"));

      var c1 = read.Regions.Single(r => r.Address == "C1");
      Assert.True(c1.OnCanvas);
      Assert.False(c1.PlanKnown);
      Assert.Contains(read.Notes, n => n.Contains("C1") && n.Contains("not in the stored plan"));
    }

    [Fact]
    public void The_component_count_comes_from_the_canvas_because_only_the_canvas_knows_it()
    {
      var stored = ScaffoldMapRecord.Build(PlanArgs, VerdictV2);
      var canvas = new List<CanvasRegion>
      {
        new CanvasRegion { Address = "A1", Name = "Prepare curves", ComponentCount = 6 },
        new CanvasRegion { Address = "B1", Name = "Loft the surface", ComponentCount = 0 },
      };

      var read = ScaffoldMapRecord.Read(stored, canvas);

      Assert.Equal(6, read.Regions.Single(r => r.Address == "A1").ComponentCount);
      Assert.Equal(0, read.Regions.Single(r => r.Address == "B1").ComponentCount);
    }

    [Fact]
    public void A_corrupt_record_degrades_to_the_canvas_rather_than_throwing()
    {
      var read = ScaffoldMapRecord.Read("{ this is not json", Canvas("A1"));

      Assert.False(read.PlanAvailable);
      Assert.Single(read.Regions);
      Assert.NotEmpty(read.Notes);
    }

    [Fact]
    public void An_empty_canvas_and_an_empty_record_is_an_empty_map_not_a_fault()
    {
      var read = ScaffoldMapRecord.Read(null, new List<CanvasRegion>());

      Assert.Empty(read.Regions);
      Assert.False(read.PlanAvailable);
    }

    /// <summary>The producer's own contract: what Build stores must be what Read can join. Asserted
    /// together so the two halves cannot drift apart (#248's class - the published shape and the parser
    /// held to one contract).</summary>
    [Fact]
    public void What_Build_writes_is_what_Read_joins()
    {
      var stored = ScaffoldMapRecord.Build(PlanArgs, VerdictV2);

      Assert.Contains("\"mapVersion\":2", stored.Replace(" ", ""));
      Assert.True(ScaffoldMapRecord.Read(stored, Canvas("A1", "B1")).PlanAvailable);
    }

    // ---- the PRODUCER side of the join ------------------------------------------------------------
    //
    // These matter because the verdict is emitted from a GH-coupled shim that no test can run. Until the
    // assigned half was extracted here, the only thing standing behind "the producer writes a ref" was a
    // hand-written fixture in a sibling file - a fixture that can stop resembling its producer with
    // nothing to notice. The shim now calls this function, so the tested path is the product path.

    private static ScaffoldRegion Region(string reference, string key, string name, params string[] dependsOn) =>
      new ScaffoldRegion { Ref = reference, Key = key, Name = name, DependsOn = dependsOn.ToList() };

    [Fact]
    public void Every_assigned_region_carries_the_ref_that_joins_it_to_its_plan()
    {
      var all = new[] { Region("prep", "A1", "Prepare"), Region("loft", "B1", "Loft", "prep") };

      var assigned = ScaffoldMapRecord.AssignedRegions(all, all);

      Assert.Equal(2, assigned.Count);
      Assert.Equal("prep", assigned[0]["ref"].GetValue<string>());
      Assert.Equal("A1", assigned[0]["key"].GetValue<string>());
      Assert.Equal("A1 - Prepare", assigned[0]["group"].GetValue<string>());
    }

    /// <summary>The sharper half of #355: the assigned region's dependsOn must be in the SAME namespace as
    /// its key. It used to hold the model's refs beside an address, so the half describing the dependency
    /// graph could not resolve its own references.</summary>
    [Fact]
    public void Assigned_dependencies_are_translated_into_ADDRESSES()
    {
      var all = new[] { Region("prep", "A1", "Prepare"), Region("loft", "B1", "Loft", "prep") };

      var assigned = ScaffoldMapRecord.AssignedRegions(all, all);

      var b1 = assigned.Single(r => r["key"].GetValue<string>() == "B1");
      Assert.Equal(new[] { "A1" }, b1["dependsOn"].AsArray().Select(d => d.GetValue<string>()).ToArray());
    }

    /// <summary>A dependency on a region that could not be PLACED still names a real address: addressing
    /// runs over every region, so the reference is truthful and `unplaceable` says which have no group.
    /// Dropping it would hide a real dependency; passing the raw ref through would put the wrong
    /// namespace in the payload.</summary>
    [Fact]
    public void A_dependency_on_an_unplaceable_region_still_names_its_address()
    {
      var all = new[] { Region("prep", "A1", "Prepare"), Region("loft", "B1", "Loft", "prep") };
      var placed = new[] { all[1] };   // A1 could not be placed

      var assigned = ScaffoldMapRecord.AssignedRegions(placed, all);

      Assert.Single(assigned);
      Assert.Equal(new[] { "A1" }, assigned[0]["dependsOn"].AsArray().Select(d => d.GetValue<string>()).ToArray());
    }

    /// <summary>A ref with no address cannot come from a validated plan - the parser already refuses a
    /// dependsOn naming a region outside it - but if one ever did, it is DROPPED rather than emitted in
    /// the model's namespace, where a reader would take it for an address.</summary>
    [Fact]
    public void A_dependency_that_names_nothing_is_dropped_not_passed_through_in_the_wrong_namespace()
    {
      var all = new[] { Region("loft", "B1", "Loft", "a-ref-that-is-not-a-region") };

      var assigned = ScaffoldMapRecord.AssignedRegions(all, all);

      Assert.Empty(assigned[0]["dependsOn"].AsArray());
    }

    /// <summary>The producer and the reader, end to end on ONE artifact: what AssignedRegions emits is
    /// what Read joins. This is the assertion the hand-written fixture could only imitate.</summary>
    [Fact]
    public void The_producers_output_is_joinable_by_the_reader()
    {
      var all = new[] { Region("prep", "A1", "Prepare"), Region("loft", "B1", "Loft", "prep") };
      var args = "{\"regions\":[{\"ref\":\"prep\",\"name\":\"Prepare\",\"purpose\":\"clean\",\"anchorIds\":[7]}," +
                 "{\"ref\":\"loft\",\"name\":\"Loft\",\"purpose\":\"build\"}]}";
      var verdict = new JsonObject { ["result"] = new JsonObject { ["regions"] = ScaffoldMapRecord.AssignedRegions(all, all) } };

      var read = ScaffoldMapRecord.Read(ScaffoldMapRecord.Build(args, verdict.ToJsonString()), Canvas("A1", "B1"));

      Assert.True(read.PlanAvailable);
      Assert.All(read.Regions, r => Assert.True(r.PlanKnown));
      Assert.Equal(new[] { 7 }, read.Regions.Single(r => r.Address == "A1").AnchorIds);
      Assert.Equal(new[] { "A1" }, read.Regions.Single(r => r.Address == "B1").DependsOn);
    }
  }
}
