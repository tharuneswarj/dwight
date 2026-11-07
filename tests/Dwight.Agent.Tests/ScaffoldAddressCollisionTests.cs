using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Dwight.Agent;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// #720: TWO MAPS, ONE ADDRESS.
  ///
  /// THE DEFECT. Addresses were minted from the plan's own layering and nothing else, so a second
  /// `propose_scaffold` on a canvas already carrying A1..A3 minted A1..A3 again. Two groups then answered to one
  /// name, and everything downstream is aimed BY name.
  ///
  /// IT DID NOT CORRUPT A BUILD - IT BRICKED ONE, which is why it is worth fixing rather than living with.
  /// `GhScaffoldMap.TryFindGroup` refuses a duplicated address as AMBIGUOUS rather than picking the first (#208 held,
  /// exactly where it was supposed to). So the user's second map was un-buildable at every colliding address, and the
  /// only way out was to delete a map by hand.
  ///
  /// THE FIX IS CONTINUE-OVER-REFUSE. A fresh number always exists, so a refusal here would be a refusal with an
  /// obvious answer standing behind it (#613/#626 - a refusal is only honest when there was nothing else to do). The
  /// number continues from the highest in use for that letter; the LETTER still comes from the flow, because position
  /// in the flow is the entire meaning of an address.
  ///
  /// And the in-use set is an ARGUMENT. The assignment stays GH-free (ADR-091), and the model still cannot reach an
  /// address: the caller computes the set from the canvas groups plus the stored map, and the plugin mints every key.
  /// </summary>
  public class ScaffoldAddressCollisionTests
  {
    private static JsonObject Region(string reference, params string[] dependsOn)
    {
      var o = new JsonObject { ["ref"] = reference, ["name"] = reference, ["purpose"] = "does " + reference };
      if (dependsOn.Length > 0) o["dependsOn"] = new JsonArray(dependsOn.Select(d => (JsonNode)d).ToArray());
      return o;
    }

    private static ScaffoldPlan Parse(IReadOnlyCollection<string> inUse, params JsonObject[] regions)
    {
      var args = new JsonObject { ["regions"] = new JsonArray(regions.Cast<JsonNode>().ToArray()) };
      Assert.True(ScaffoldPlanParser.TryParse(args, _ => IdState.Alive, inUse, out var plan, out var error),
        error?.ToJson());
      return plan;
    }

    private static string[] Keys(ScaffoldPlan plan) => plan.Regions.Select(r => r.Key).ToArray();

    // ---- the defect, and the shape of the fix --------------------------------------------------------------

    /// <summary>THE FIELD CASE: a canvas already carrying A1, A2 and A3 gets A4 next, not a second A1.</summary>
    [Fact]
    public void A_second_map_continues_the_numbering_instead_of_reusing_it()
    {
      var plan = Parse(new[] { "A1", "A2", "A3" }, Region("first"), Region("second"));

      Assert.Equal(new[] { "A4", "A5" }, Keys(plan));
    }

    /// <summary>Per LETTER, not globally - the letter is the flow layer and must not drift because some other
    /// layer is crowded. A1..A3 and B1 in use: the new flow's first layer starts at A4 and its second at B2.</summary>
    [Fact]
    public void The_continuation_is_per_letter_so_the_flow_still_sets_the_letter()
    {
      var plan = Parse(new[] { "A1", "A2", "A3", "B1" }, Region("source"), Region("sink", "source"));

      Assert.Equal(new[] { "A4", "B2" }, Keys(plan));
    }

    /// <summary>The empty canvas is unchanged - #720 must not cost the ordinary first map its A1.</summary>
    [Fact]
    public void An_empty_canvas_still_starts_at_A1()
    {
      Assert.Equal(new[] { "A1", "B1" },
        Keys(Parse(ScaffoldPlanParser.EmptyCanvas, Region("source"), Region("sink", "source"))));
    }

    /// <summary>Gaps are not filled. A2 deleted while A1 and A3 stand means the next is A4: the conversation and the
    /// stored plan both still say "A2", and re-minting it would put new work under a name already in use upstream.</summary>
    [Fact]
    public void A_deleted_middle_address_is_not_recycled()
    {
      Assert.Equal(new[] { "A4" }, Keys(Parse(new[] { "A1", "A3" }, Region("only"))));
    }

    /// <summary>The set is joined from group nicknames and a stored record, so a stray string in it must not be able
    /// to stop a map being laid down (#375 - an instrument's own mess is not the subject's).</summary>
    [Fact]
    public void Unparseable_entries_in_the_in_use_set_are_skipped_not_thrown_on()
    {
      var plan = Parse(new[] { "A1", "not an address", "", "a2" }, Region("only"));

      // "a2" is lower case, and the plugin assigns upper case - so it is somebody else's string, not an address.
      Assert.Equal(new[] { "A2" }, Keys(plan));
    }

    // ---- the address is never the model's -------------------------------------------------------------------

    /// <summary>#720 hands the plugin a NEW input, so the standing law gets re-checked against it: a plan that names
    /// its own addresses still cannot make one stick. The keys come from the layering and the in-use set, never from
    /// anything in the model's JSON.</summary>
    [Fact]
    public void An_address_the_model_asked_for_changes_nothing()
    {
      var asking = Region("first");
      asking["key"] = "Z9";
      asking["address"] = "Z9";

      var plan = Parse(new[] { "A1" }, asking);

      Assert.Equal(new[] { "A2" }, Keys(plan));
      Assert.DoesNotContain("Z9", Keys(plan));
    }

    // ---- past Z ----------------------------------------------------------------------------------------------

    /// <summary>
    /// PAST Z REFUSES, AND NAMES WHAT IS IN USE.
    ///
    /// BOUND (#297): this cannot be reached through <c>TryParse</c> today, because <c>MaxRegions</c> caps a plan's
    /// depth below Z - so the guard is driven at the unit below the parser. That bound is ASSERTED, not claimed, by
    /// <see cref="The_parsers_region_cap_keeps_every_plan_inside_the_letter_space"/>.
    /// </summary>
    [Fact]
    public void A_flow_deeper_than_Z_is_refused_and_says_what_is_using_the_space()
    {
      var plan = new ScaffoldPlan();
      for (var i = 0; i < 27; i++)
        plan.Regions.Add(new ScaffoldRegion
        {
          Ref = "r" + i,
          Name = "r" + i,
          DependsOn = i == 0 ? new List<string>() : new List<string> { "r" + (i - 1) },
        });

      Assert.False(ScaffoldPlanParser.AssignAddresses(plan, new[] { "A1", "B1" }, out var error));

      Assert.Equal(ToolErrors.PreconditionRefused, error.Code);
      Assert.Contains("A to Z", error.Message);
      Assert.Equal("Z", error.Detail["lastAddressable"].GetValue<string>());
      Assert.Equal(27, error.Detail["layers"].GetValue<int>());
      var named = error.Detail["inUse"].AsArray().Select(n => n.GetValue<string>()).ToArray();
      Assert.Equal(new[] { "A1", "B1" }, named);
    }

    /// <summary>
    /// THE TWO CONSTANTS ARE HELD TOGETHER BY THIS TEST, not by the paragraph that used to say so (#732).
    ///
    /// <c>MaxRegions</c> and the A..Z address space are set in different places for different reasons, and nothing
    /// made them talk to each other. The claim "past Z is unreachable" was true, and it was true in PROSE - so the day
    /// someone raises MaxRegions it would go on reading as true while <c>Letter()</c> started emitting "AA1", or the
    /// past-Z guard started refusing plans the parser had just accepted. A BOUND STATED IN PROSE ROTS; A BOUND
    /// ASSERTED FAILS.
    ///
    /// So: the deepest plan the parser will ACCEPT is built - a chain of MaxRegions regions, each depending on the
    /// last, which is the only shape that puts every region in its own layer - and driven through the real
    /// <c>TryParse</c>. If the two constants ever disagree, this goes red in one of two directions, and both are the
    /// warning: the parse is refused (MaxRegions outgrew the letters), or the last address wears two letters.
    /// </summary>
    [Fact]
    public void The_parsers_region_cap_keeps_every_plan_inside_the_letter_space()
    {
      var chain = new JsonObject[ScaffoldPlanParser.MaxRegions];
      for (var i = 0; i < chain.Length; i++)
        chain[i] = i == 0 ? Region("r0") : Region("r" + i, "r" + (i - 1));

      var plan = Parse(ScaffoldPlanParser.EmptyCanvas, chain);   // asserts the parse succeeded

      var deepest = plan.Regions.Single(r => r.Layer == ScaffoldPlanParser.MaxRegions - 1).Key;
      var letters = new string(deepest.TakeWhile(char.IsLetter).ToArray());

      Assert.Equal(1, letters.Length);                    // never "AA" - the single-letter space IS the space
      Assert.True(letters[0] <= 'Z', "the deepest layer a plan can reach addressed past Z as \"" + deepest + "\"");
    }

    /// <summary>Z itself is still addressable - an off-by-one here would cost a legitimate layer.</summary>
    [Fact]
    public void A_flow_exactly_as_deep_as_Z_still_addresses()
    {
      var plan = new ScaffoldPlan();
      for (var i = 0; i < 26; i++)
        plan.Regions.Add(new ScaffoldRegion
        {
          Ref = "r" + i,
          Name = "r" + i,
          DependsOn = i == 0 ? new List<string>() : new List<string> { "r" + (i - 1) },
        });

      Assert.True(ScaffoldPlanParser.AssignAddresses(plan, ScaffoldPlanParser.EmptyCanvas, out _));
      Assert.Equal("Z1", plan.Regions.Last().Key);
    }
  }
}
