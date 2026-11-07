using System;
using System.Collections.Generic;
using System.Linq;
using Dwight.Agent;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// U33's panel half (#954, wave-2 wiring): an approved build turn is told, at turn start, what each region's rebuild
  /// KEEPS - beside what it cuts - so the model wires from a kept component instead of placing a second copy.
  ///
  /// The fact's shape is held here GH-free, through the same Compose the panel calls. What only a sitting can witness:
  /// that GhBuildRegion.KeptForRebuild reads a real adopted pair off a real region (the claim's Adopted set, read as
  /// KeptComponents by the one KeptOf the duplicate check uses), and that a model told it stops placing the duplicate.
  /// </summary>
  public class KeptTurnFactTests
  {
    private const string Situation = "\n\nRight now: the canvas holds 12 objects.";

    private static Dictionary<string, IReadOnlyList<KeptComponent>> Kept(string region, params KeptComponent[] kept) =>
      new Dictionary<string, IReadOnlyList<KeptComponent>>(StringComparer.Ordinal) { [region] = kept };

    private static readonly KeptComponent Site = new KeptComponent { Id = 41, Type = "Curve", Nick = "Site" };
    private static readonly KeptComponent Terrain = new KeptComponent { Id = 42, Type = "Surface", Nick = "Terrain" };

    [Fact]
    public void The_922_case_names_each_kept_component_by_type_nickname_and_id()
    {
      var fact = ApprovedTurnFact.KeptFact(Kept("A2", Site, Terrain));

      Assert.Equal(
        "KEPT ON REBUILD: rebuilding A2 keeps Curve \"Site\" (41), Surface \"Terrain\" (42). These are components the " +
        "user has edited since Dwight placed them, so the rebuild leaves them exactly as they are: wire from them by id, " +
        "and do not place them again.", fact);
    }

    [Fact]
    public void One_kept_component_is_said_in_the_singular()
    {
      var fact = ApprovedTurnFact.KeptFact(Kept("B1", Site));
      Assert.Contains("rebuilding B1 keeps Curve \"Site\" (41).", fact);
      Assert.Contains("This is a component the user has edited since Dwight placed it", fact);
      Assert.EndsWith("wire from it by id, and do not place it again.", fact);
    }

    [Fact]
    public void A_default_nickname_is_not_quoted_as_if_it_identified_anything()
    {
      var fact = ApprovedTurnFact.KeptFact(Kept("A1",
        new KeptComponent { Id = 7, Type = "Panel", Nick = "Panel" }, new KeptComponent { Id = 8, Type = "Number", Nick = "" }));
      Assert.Contains("keeps Panel (7), Number (8).", fact);
      Assert.DoesNotContain("\"Panel\"", fact);
    }

    [Fact]
    public void Regions_are_ordered_by_address_and_each_is_bounded()
    {
      var many = Enumerable.Range(1, 8).Select(i => new KeptComponent { Id = 100 + i, Type = "Point", Nick = "P" + i }).ToArray();
      var fact = ApprovedTurnFact.KeptFact(new Dictionary<string, IReadOnlyList<KeptComponent>>
      {
        ["B2"] = new[] { Site }, ["A10"] = many, ["A2"] = new[] { Terrain },
      });

      Assert.True(fact.IndexOf("rebuilding A2", StringComparison.Ordinal) < fact.IndexOf("rebuilding A10", StringComparison.Ordinal));
      Assert.True(fact.IndexOf("rebuilding A10", StringComparison.Ordinal) < fact.IndexOf("rebuilding B2", StringComparison.Ordinal));
      Assert.Contains("Point \"P5\" (105), and 3 more.", fact);
      Assert.DoesNotContain("(106)", fact);
    }

    [Fact]
    public void Nothing_kept_states_nothing()
    {
      Assert.Null(ApprovedTurnFact.KeptFact(null));
      Assert.Null(ApprovedTurnFact.KeptFact(new Dictionary<string, IReadOnlyList<KeptComponent>>()));
      Assert.Null(ApprovedTurnFact.KeptFact(Kept("A1")));
    }

    [Fact]
    public void The_fact_rides_inside_an_approved_build_after_the_cut_list()
    {
      var cuts = new Dictionary<string, IReadOnlyList<CutWire>>
      {
        ["A2"] = new[] { new CutWire { ConsumerId = 19, ConsumerType = "XY Plane", ConsumerRegion = "B1", InputLabel = "Origin" } },
      };
      var text = TurnSystemText.Compose(Situation, null, null, TurnCapability.Build, new[] { "A2" }, cuts, Kept("A2", Site));

      var approved = text.IndexOf(ApprovedTurnFact.Heading, StringComparison.Ordinal);
      var boundary = text.IndexOf(RebuildBoundary.TurnHeading, StringComparison.Ordinal);
      var kept = text.IndexOf(ApprovedTurnFact.KeptHeading, StringComparison.Ordinal);
      Assert.True(approved >= 0 && boundary > approved && kept > boundary);
    }

    [Fact]
    public void Only_an_approved_build_is_told_and_no_kept_list_leaves_the_text_unchanged()
    {
      Assert.DoesNotContain(ApprovedTurnFact.KeptHeading,
        TurnSystemText.Compose(Situation, null, null, TurnCapability.ReadOnly, null, null, Kept("A2", Site)));
      Assert.Equal(TurnSystemText.Compose(Situation, null, null, TurnCapability.Build, new[] { "A2" }, null),
                   TurnSystemText.Compose(Situation, null, null, TurnCapability.Build, new[] { "A2" }, null, null));
    }

    [Fact]
    public void Every_component_the_fact_names_is_one_the_duplicate_check_would_refuse_a_copy_of()
    {
      // The fact and the refusal speak about the same set: a spec placing the named Site is refused, naming id 41.
      var spec = new BuildSpec { Region = "A2" };
      spec.Components.Add(new BuildComponent { Ref = "site", Type = "Curve", Nick = "Site" });
      var refusal = RebuildDuplicates.Check(spec, new[] { Site }, "A2");

      Assert.NotNull(refusal);
      Assert.Contains("id 41", refusal.Message);
      Assert.Contains("(41)", ApprovedTurnFact.KeptFact(Kept("A2", Site)));
    }
  }
}
