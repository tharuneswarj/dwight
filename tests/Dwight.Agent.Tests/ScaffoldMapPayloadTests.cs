using System.Collections.Generic;
using System.Linq;
using Dwight.Agent;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// F4, from the WP-4 gate: THE TOOL TOLD THE TRUTH AND THE MODEL DROPPED IT, TWICE.
  ///
  /// The user undid the scaffold map and asked what was on it. Every region came back with
  /// <c>onCanvas:false</c> - the tool was honest, exactly as its descriptor promises - and the model
  /// described all nine as though they were there. Asked again, same answer. Only when the user said
  /// "there are no maps in canvas?" did it report the truth it had been holding all along.
  ///
  /// THE FIX IS THE PAYLOAD'S SHAPE, NOT A PROMPT LINE. A boolean beside a name is a thing a reader can
  /// skim; two separate lists are not. Regions that are not on the canvas are no longer enumerable in the
  /// same breath as ones that are, so "list the map" CANNOT produce a list that mixes them - ADR-056
  /// applied to a read: make the wrong answer unrepresentable rather than discouraged.
  /// </summary>
  public class ScaffoldMapPayloadTests
  {
    private static MapRegion Region(string address, bool onCanvas) =>
      new MapRegion
      {
        Address = address,
        Name = address + " - something",
        OnCanvas = onCanvas,
        ComponentCount = 0,
        PlanKnown = true,
        Purpose = "a purpose",
      };

    private static MapReadResult Map(params MapRegion[] regions) =>
      new MapReadResult { Regions = regions.ToList(), PlanAvailable = true };

    private static List<string> Addresses(System.Text.Json.Nodes.JsonNode list) =>
      (list as System.Text.Json.Nodes.JsonArray ?? new System.Text.Json.Nodes.JsonArray())
        .Select(n => n["address"].GetValue<string>()).ToList();

    // ---- the partition ------------------------------------------------------------------------------

    [Fact]
    public void Regions_on_the_canvas_and_regions_that_are_gone_are_two_separate_lists()
    {
      var payload = ScaffoldMapPayload.Build(
        Map(Region("A1", true), Region("A2", false), Region("B1", true)), 0, 48);

      Assert.Equal(new[] { "A1", "B1" }, Addresses(payload["regions"]));
      Assert.Equal(new[] { "A2" }, Addresses(payload["absentFromCanvas"]));
    }

    /// <summary>
    /// THE EXACT GATE SCENARIO. Nine regions, all undone. "regions" must be EMPTY - a model asked to list
    /// the map cannot produce nine present ones from a list that holds none.
    /// </summary>
    [Fact]
    public void With_every_region_undone_the_present_list_is_empty()
    {
      var nine = Enumerable.Range(1, 9).Select(i => Region("A" + i, false)).ToArray();

      var payload = ScaffoldMapPayload.Build(Map(nine), 0, 48);

      Assert.Empty(Addresses(payload["regions"]));
      Assert.Equal(9, Addresses(payload["absentFromCanvas"]).Count);
    }

    /// <summary>
    /// The condition that used to be wrong. "No regions at all" and "every region was undone" are the same
    /// fact to a reader and were two different code paths: with nine undone regions the old check saw a
    /// non-empty <c>map.Regions</c> and stayed silent, handing back an empty list with nothing saying why.
    /// That silence is what the model filled in.
    /// </summary>
    [Fact]
    public void An_all_undone_map_SAYS_the_canvas_is_bare_rather_than_going_quiet()
    {
      var payload = ScaffoldMapPayload.Build(Map(Region("A1", false), Region("A2", false)), 0, 48);

      var empty = payload["empty"].GetValue<string>();
      Assert.Contains("NOTHING is on this canvas", empty);
      Assert.Contains("Do not describe them as present", empty);
    }

    [Fact]
    public void A_map_with_no_regions_at_all_still_says_so_in_its_own_words()
    {
      var payload = ScaffoldMapPayload.Build(Map(), 0, 48);

      Assert.Contains("No scaffold regions are on this canvas", payload["empty"].GetValue<string>());
    }

    // ---- the absent list explains itself, and only when it exists ------------------------------------

    /// <summary>A count alone would be another thing to skim, so the absence carries a sentence saying
    /// what it means and what may not be done with it.</summary>
    [Fact]
    public void The_absent_list_says_what_absence_means()
    {
      var payload = ScaffoldMapPayload.Build(Map(Region("A1", true), Region("A2", false)), 0, 48);

      var note = payload["absentNote"].GetValue<string>();
      Assert.Contains("NOT on the canvas", note);
      Assert.Contains("must not be described to the user as though they are there", note);
      Assert.Contains("The canvas is the authority", note);
    }

    /// <summary>A map with nothing missing should not carry a paragraph about absence - an unconditional
    /// note is one more thing to read past, which is the habit this whole fix exists to break.</summary>
    [Fact]
    public void A_map_with_nothing_missing_carries_no_absence_note_at_all()
    {
      var payload = ScaffoldMapPayload.Build(Map(Region("A1", true), Region("B1", true)), 0, 48);

      Assert.Null(payload["absentFromCanvas"]);
      Assert.Null(payload["absentNote"]);
      Assert.Null(payload["empty"]);
    }

    // ---- what the partition must NOT have broken ----------------------------------------------------

    /// <summary>
    /// `onCanvas` stays on every entry. Removing it would break the published contract for a caller
    /// reading one region, and the partition is the load-bearing half rather than a replacement.
    /// </summary>
    [Fact]
    public void Each_entry_still_carries_its_own_onCanvas_flag()
    {
      var payload = ScaffoldMapPayload.Build(Map(Region("A1", true), Region("A2", false)), 0, 48);

      Assert.True(payload["regions"][0]["onCanvas"].GetValue<bool>());
      Assert.False(payload["absentFromCanvas"][0]["onCanvas"].GetValue<bool>());
    }

    [Fact]
    public void The_truncation_note_still_names_the_cap_it_hit()
    {
      var payload = ScaffoldMapPayload.Build(Map(Region("A1", true)), truncated: 3, maxRegions: 48);

      var notes = (payload["notes"] as System.Text.Json.Nodes.JsonArray)
        .Select(n => n.GetValue<string>()).ToList();

      Assert.Contains(notes, n => n.Contains("3 further scaffold group(s)") && n.Contains("48"));
    }
  }
}
