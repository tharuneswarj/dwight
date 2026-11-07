using System.Collections.Generic;
using System.Linq;
using Dwight.Agent;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// U33 (#922): A REBUILD NEVER DUPLICATES A KEPT COMPONENT.
  ///
  /// The as-left rule kept two params the user had bound (Site, Terrain); the rebuild's spec placed two more with the
  /// same nicknames and types and wired the new logic to the empty pair. The user was told "your bindings were lost"
  /// over two orphaned, intact params. These tests hold the refusal to the fixture: same nickname AND type refuses,
  /// naming the kept one by id and the way through (wire from it); a different nickname, a default nickname, or no
  /// kept components at all pass - and the first test is the positive control that the checker can refuse at all.
  /// </summary>
  public class RebuildDuplicatesTests
  {
    private static readonly IReadOnlyList<KeptComponent> Kept = new[]
    {
      new KeptComponent { Id = 41, Type = "Curve", Nick = "Site" },
      new KeptComponent { Id = 42, Type = "Surface", Nick = "Terrain" },
      new KeptComponent { Id = 43, Type = "Panel", Nick = "Panel" },   // a default nickname: identifies nothing
    };

    private static BuildSpec Spec(params (string Ref, string Type, string Nick)[] components)
    {
      var spec = new BuildSpec();
      foreach (var (r, t, n) in components) spec.Components.Add(new BuildComponent { Ref = r, Type = t, Nick = n });
      return spec;
    }

    // ---- the defect, refused ------------------------------------------------------------------------------------

    [Fact]
    public void THE_A2_CASE_a_spec_placing_the_kept_Site_and_Terrain_again_is_refused_naming_them_by_id()
    {
      var refusal = RebuildDuplicates.Check(
        Spec(("site", "Curve", "Site"), ("terrain", "Surface", "Terrain"), ("offset", "Offset Curve", null)), Kept, "A2");

      Assert.NotNull(refusal);
      Assert.Equal(ToolErrors.PreconditionRefused, refusal.Code);
      Assert.Equal("This rebuild of A2 keeps Curve \"Site\" (41), Surface \"Terrain\" (42) - components Dwight placed that have " +
                   "been edited since, so they stay exactly as they are. The spec would place another Curve \"Site\" (ref \"site\"), " +
                   "another Surface \"Terrain\" (ref \"terrain\") beside them, leaving the kept ones orphaned and the user with two of " +
                   "each. Nothing was built. Drop those from the spec and wire from id 41, id 42 instead.", refusal.Message);

      Assert.Equal(RebuildDuplicates.Reason, refusal.Detail["reason"].GetValue<string>());
      var dups = refusal.Detail["duplicates"].AsArray();
      Assert.Equal(2, dups.Count);
      Assert.Equal(41, dups[0]["keptId"].GetValue<int>());
      Assert.Equal("site", dups[0]["ref"].GetValue<string>());
    }

    [Fact]
    public void One_duplicate_reads_in_the_singular()
    {
      var refusal = RebuildDuplicates.Check(Spec(("s", "Curve", "Site")), Kept, "A2");
      Assert.StartsWith("This rebuild of A2 keeps Curve \"Site\" (41) - a component Dwight placed that has been edited since, so it stays exactly as it is.", refusal.Message);
      Assert.EndsWith("wire from id 41 instead.", refusal.Message);
    }

    [Fact]
    public void The_nickname_match_ignores_case_and_surrounding_space()
    {
      Assert.NotNull(RebuildDuplicates.Check(Spec(("s", "curve", " site ")), Kept, "A2"));
    }

    [Fact]
    public void A_QUALIFIED_TYPE_matches_by_its_bare_name()
    {
      // U25's qualified form is accepted in `type`; the duplicate check must see through it.
      Assert.NotNull(RebuildDuplicates.Check(Spec(("s", "Params > Geometry > Curve", "Site")), Kept, "A2"));
    }

    // ---- what is NOT a duplicate --------------------------------------------------------------------------------

    [Fact]
    public void A_different_nickname_of_the_same_type_is_not_a_duplicate()
    {
      Assert.Null(RebuildDuplicates.Check(Spec(("b", "Curve", "Boundary")), Kept, "A2"));
    }

    [Fact]
    public void The_same_nickname_on_a_different_type_is_not_a_duplicate()
    {
      Assert.Null(RebuildDuplicates.Check(Spec(("s", "Surface", "Site")), Kept, "A2"));
    }

    [Fact]
    public void A_KEPT_COMPONENT_WITH_A_DEFAULT_NICKNAME_blocks_nothing()
    {
      // Every Panel is called "Panel" until someone names it: a kept one does not stop a spec adding another, with or
      // without a nick of its own.
      Assert.Null(RebuildDuplicates.Check(Spec(("p", "Panel", null)), Kept, "A2"));
      Assert.Null(RebuildDuplicates.Check(Spec(("p", "Panel", "Panel")), Kept, "A2"));
    }

    [Fact]
    public void A_spec_component_with_no_nick_matches_a_kept_one_only_if_its_type_IS_the_kept_nickname()
    {
      // No nick means the placed component will be called by its type - "Curve" - which is not "Site".
      Assert.Null(RebuildDuplicates.Check(Spec(("c", "Curve", null)), Kept, "A2"));
    }

    [Fact]
    public void With_nothing_kept_there_is_nothing_to_duplicate()
    {
      Assert.Null(RebuildDuplicates.Check(Spec(("s", "Curve", "Site")), new KeptComponent[0], "A2"));
      Assert.Null(RebuildDuplicates.Check(Spec(("s", "Curve", "Site")), null, "A2"));
    }

    [Fact]
    public void Many_duplicates_are_named_to_a_bound_and_counted()
    {
      var kept = Enumerable.Range(1, 7).Select(i => new KeptComponent { Id = i, Type = "Curve", Nick = "Edge" + i }).ToList();
      var spec = Spec(kept.Select(k => ("e" + k.Id, "Curve", k.Nick)).ToArray());

      var refusal = RebuildDuplicates.Check(spec, kept, "B1");

      Assert.Contains(", and 2 more", refusal.Message);
      Assert.Equal(7, refusal.Detail["duplicates"].AsArray().Count);
    }

    // ---- the card names the kept ones, so the refusal is no surprise (U33's half of the consent surface) ----------

    [Fact]
    public void The_card_names_the_kept_components_when_it_is_given_them()
    {
      var line = RebuildPreview.Sentence(new[]
      {
        new RegionRebuild
        {
          Address = "A2",
          Remove = new[] { new RebuildComponent { Nick = "Offset", Type = "Offset Curve" } },
          Adopted = 2,
          AdoptedNamed = new[]
          {
            new RebuildComponent { Nick = "Site", Type = "Curve" },
            new RebuildComponent { Nick = "Terrain", Type = "Surface" },
          },
        },
      });

      Assert.Contains("LEAVE the 2 you have edited since (Site (Curve), Terrain (Surface))", line);
    }

    [Fact]
    public void Without_names_the_card_clause_is_exactly_what_it_was()
    {
      var line = RebuildPreview.Sentence(new[]
      {
        new RegionRebuild { Address = "A2", Remove = new[] { new RebuildComponent { Nick = "Offset", Type = "Offset Curve" } }, Adopted = 2 },
      });
      Assert.Contains("and build it again and LEAVE the 2 you have edited since.", line);
    }
  }
}
