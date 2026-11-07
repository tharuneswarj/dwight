using System;
using System.Collections.Generic;
using System.Linq;
using Dwight.Agent;
using Dwight.Data;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// The PRESERVATION leg of build_region's verdict (#193a) - the one that decides whether "the agent
  /// may write to the canvas" is a safe sentence.
  ///
  /// It is deliberately NOT byte-identity. Reorganise's guard is byte-identical-or-abort because
  /// Reorganise only moves things; a build ADDS, and a useful build wires INTO what is already there,
  /// which changes those components' input sources. Byte-identity would reject every build that did
  /// anything. So the claim is an expected-delta: unchanged EXCEPT for wires arriving from the
  /// components this build created.
  ///
  /// The distinction with teeth is the last one - "connected into your definition" (asked for) versus
  /// "rewired your definition" (never asked for). That failure would be invisible at the time and
  /// unrecoverable a hundred components later.
  /// </summary>
  public class BuildPreservationTests
  {
    private static Guid G(int n) { var b = new byte[16]; b[0] = (byte)n; b[1] = (byte)(n >> 8); return new Guid(b); }

    private static FpObject Obj(int id, string type = "Loft", string value = null, params (int input, int[] sources)[] inputs)
    {
      var o = new FpObject { Guid = G(id), TypeName = type, ValueToken = value };
      foreach (var (input, sources) in inputs)
        o.Inputs.Add(new FpInput
        {
          Guid = G(input),
          MappingToken = "none",
          Sources = sources.Select(G).ToList(),
        });
      return o;
    }

    private static FpDoc Doc(params FpObject[] objects)
    {
      var d = new FpDoc();
      d.Objects.AddRange(objects);
      return d;
    }

    /// <summary>Two existing components: 1 (a slider) feeding 2 (a Loft) on input 20.</summary>
    private static FpDoc Before() => Doc(
      Obj(1, "Number Slider", "0.5"),
      Obj(2, "Loft", inputs: (20, new[] { 1 })));

    /// <summary>
    /// PARAM-SHAPED: every created object here is a floating param (a slider, a panel), whose own guid IS the
    /// guid a wire from it carries. Only for that shape is "created objects" also "created sources" - which is
    /// exactly the conflation #643 removed from the product, so it lives here, named for the shape it assumes.
    /// </summary>
    private static BuildPreservation.Result CheckParams(FpDoc before, FpDoc after, HashSet<Guid> created) =>
      BuildPreservation.Check(before, after, new BuildIdentity
      {
        CreatedObjects = created ?? new HashSet<Guid>(),
        CreatedSources = created ?? new HashSet<Guid>(),
      });

    // ---- #643 (Finding B): a COMPONENT's wire carries its OUTPUT param's guid ---------------------------

    /// <summary>
    /// THE SHAPE GRASSHOPPER PRODUCES FOR A COMPONENT. The build placed component 50 (a Construct Point) and
    /// wired its output param 51 into the Loft. The Loft's sources read 51, never 50 - so a check that compared
    /// sources with created OBJECT guids reverted this, the most ordinary connection a build makes.
    /// </summary>
    [Fact]
    public void A_new_COMPONENTs_output_wired_into_an_existing_input_is_preserved()
    {
      var after = Doc(
        Obj(1, "Number Slider", "0.5"),
        Obj(2, "Loft", inputs: (20, new[] { 1, 51 })),
        Obj(50, "Construct Point"));

      var r = BuildPreservation.Check(Before(), after, new BuildIdentity
      {
        CreatedObjects = new HashSet<Guid> { G(50) },
        CreatedSources = new HashSet<Guid> { G(51) },
      });

      Assert.True(r.Preserved, r.Describe());
    }

    // ---- #625: a DECLARED reconnection is the predicted delta, in place --------------------------------

    /// <summary>A rebuild removed Construct Point 40 (output 41), which fed the XY Plane's input 20 at position 1,
    /// and placed Construct Point 50 (output 51).</summary>
    private static FpDoc XyBefore() => Doc(Obj(1, "Slider"), Obj(3, "Slider"), Obj(40, "Construct Point"),
                                           Obj(2, "XY Plane", inputs: (20, new[] { 1, 41, 3 })));

    private static BuildIdentity Rebuilt(params PredictedReconnection[] reconnections) => new BuildIdentity
    {
      ReplacedObjects = new HashSet<Guid> { G(40) },
      CreatedObjects = new HashSet<Guid> { G(50) },
      CreatedSources = new HashSet<Guid> { G(51), G(52) },
      Reconnections = reconnections,
    };

    private static PredictedReconnection Recon(int position, int oldSource, int newSource) =>
      new PredictedReconnection { InputGuid = G(20), Position = position, OldSource = G(oldSource), NewSource = G(newSource) };

    [Fact]
    public void A_declared_reconnection_back_in_place_is_preserved()
    {
      var after = Doc(Obj(1, "Slider"), Obj(3, "Slider"), Obj(50, "Construct Point"),
                      Obj(2, "XY Plane", inputs: (20, new[] { 1, 51, 3 })));

      var r = BuildPreservation.Check(XyBefore(), after, Rebuilt(Recon(1, 41, 51)));
      Assert.True(r.Preserved, r.Describe());
    }

    /// <summary>Source order is behaviour: the same wires with the new source appended is a different definition.</summary>
    [Fact]
    public void A_reconnection_appended_instead_of_in_place_is_NOT_preserved()
    {
      var after = Doc(Obj(1, "Slider"), Obj(3, "Slider"), Obj(50, "Construct Point"),
                      Obj(2, "XY Plane", inputs: (20, new[] { 1, 3, 51 })));

      var r = BuildPreservation.Check(XyBefore(), after, Rebuilt(Recon(1, 41, 51)));
      Assert.False(r.Preserved);
      Assert.Contains(G(2), r.Rewired);
    }

    /// <summary>The declaration names WHICH new output; a different one - even a new one - is not what was declared.</summary>
    [Fact]
    public void A_reconnection_from_a_different_new_output_than_declared_is_NOT_preserved()
    {
      var after = Doc(Obj(1, "Slider"), Obj(3, "Slider"), Obj(50, "Construct Point"),
                      Obj(2, "XY Plane", inputs: (20, new[] { 1, 52, 3 })));

      var r = BuildPreservation.Check(XyBefore(), after, Rebuilt(Recon(1, 41, 51)));
      Assert.False(r.Preserved);
      Assert.Contains(G(2), r.Rewired);
    }

    /// <summary>A prediction that does not fit the baseline is not excused - it is a plan about a different canvas.</summary>
    [Fact]
    public void A_reconnection_whose_old_source_is_not_where_it_was_recorded_is_NOT_preserved()
    {
      var after = Doc(Obj(1, "Slider"), Obj(3, "Slider"), Obj(50, "Construct Point"),
                      Obj(2, "XY Plane", inputs: (20, new[] { 51, 41, 3 })));

      var r = BuildPreservation.Check(XyBefore(), after, Rebuilt(Recon(0, 41, 51)));
      Assert.False(r.Preserved);
      Assert.Contains(G(2), r.Rewired);
    }

    [Fact]
    public void Two_reconnections_on_one_input_each_go_back_to_their_own_place()
    {
      var before = Doc(Obj(1, "Slider"), Obj(40, "Construct Point"),
                       Obj(2, "Loft", inputs: (20, new[] { 41, 1, 42 })));
      var after = Doc(Obj(1, "Slider"), Obj(50, "Construct Point"),
                      Obj(2, "Loft", inputs: (20, new[] { 51, 1, 52 })));

      var r = BuildPreservation.Check(before, after, Rebuilt(Recon(0, 41, 51), Recon(2, 42, 52)));
      Assert.True(r.Preserved, r.Describe());
    }

    /// <summary>#625's narrow sibling: a RESTORE inserts the new source where the lost one was - the baseline no longer
    /// holds the lost source, so the prediction is an insert, and anywhere else is still a rewire.</summary>
    [Fact]
    public void A_declared_restore_inserted_where_the_lost_source_was_is_preserved()
    {
      var before = Doc(Obj(1, "Slider"), Obj(3, "Slider"), Obj(2, "XY Plane", inputs: (20, new[] { 1, 3 })));
      var after = Doc(Obj(1, "Slider"), Obj(3, "Slider"), Obj(50, "Construct Point"),
                      Obj(2, "XY Plane", inputs: (20, new[] { 1, 51, 3 })));

      var restore = new PredictedReconnection { InputGuid = G(20), Position = 1, OldSource = Guid.Empty, NewSource = G(51) };
      var r = BuildPreservation.Check(before, after, new BuildIdentity
      {
        CreatedObjects = new HashSet<Guid> { G(50) }, CreatedSources = new HashSet<Guid> { G(51) }, Reconnections = new[] { restore },
      });
      Assert.True(r.Preserved, r.Describe());

      var appended = Doc(Obj(1, "Slider"), Obj(3, "Slider"), Obj(50, "Construct Point"),
                         Obj(2, "XY Plane", inputs: (20, new[] { 1, 3, 51 })));
      var wrong = BuildPreservation.Check(before, appended, new BuildIdentity
      {
        CreatedObjects = new HashSet<Guid> { G(50) }, CreatedSources = new HashSet<Guid> { G(51) }, Reconnections = new[] { restore },
      });
      Assert.False(wrong.Preserved);
      Assert.Contains(G(2), wrong.Rewired);
    }

    /// <summary>Declaring one cut on an input does not excuse another on the same input.</summary>
    [Fact]
    public void An_undeclared_second_cut_on_a_reconnected_input_is_NOT_preserved()
    {
      var before = Doc(Obj(1, "Slider"), Obj(40, "Construct Point"),
                       Obj(2, "Loft", inputs: (20, new[] { 41, 1, 42 })));
      var after = Doc(Obj(1, "Slider"), Obj(50, "Construct Point"),
                      Obj(2, "Loft", inputs: (20, new[] { 51, 1 })));

      var r = BuildPreservation.Check(before, after, Rebuilt(Recon(0, 41, 51)));
      Assert.False(r.Preserved);
      Assert.Contains(G(2), r.Rewired);
    }

    /// <summary>The object guid is not a licence for its outputs, nor the reverse: a source that is not among
    /// the created SOURCES is an existing component's, whatever objects were created.</summary>
    [Fact]
    public void A_created_OBJECT_guid_never_excuses_a_source_that_is_not_a_created_SOURCE()
    {
      var after = Doc(
        Obj(1, "Number Slider", "0.5"),
        Obj(2, "Loft", inputs: (20, new[] { 1, 51 })),
        Obj(50, "Construct Point"));

      var r = BuildPreservation.Check(Before(), after, new BuildIdentity
      {
        CreatedObjects = new HashSet<Guid> { G(50), G(51) },
        CreatedSources = new HashSet<Guid>(),
      });

      Assert.False(r.Preserved);
      Assert.Contains(G(2), r.Rewired);
    }

    // ---- the passing case --------------------------------------------------------------------------

    [Fact]
    public void An_untouched_definition_is_preserved()
    {
      var r = CheckParams(Before(), Before(), new HashSet<Guid>());
      Assert.True(r.Preserved);
    }

    [Fact]
    public void ADDING_components_alongside_is_preserved()
    {
      var after = Doc(Obj(1, "Number Slider", "0.5"), Obj(2, "Loft", inputs: (20, new[] { 1 })), Obj(50, "Panel"));
      var r = CheckParams(Before(), after, new HashSet<Guid> { G(50) });
      Assert.True(r.Preserved);
    }

    [Fact]
    public void WIRING_INTO_an_existing_component_from_a_NEW_one_is_preserved()
    {
      // The case byte-identity would have wrongly rejected: the build connected component 50 into the
      // Loft's input, which is exactly what a build is for.
      var after = Doc(
        Obj(1, "Number Slider", "0.5"),
        Obj(2, "Loft", inputs: (20, new[] { 1, 50 })),
        Obj(50, "Panel"));

      var r = CheckParams(Before(), after, new HashSet<Guid> { G(50) });
      Assert.True(r.Preserved, r.Describe());
    }

    // ---- the failing cases, which are the point ----------------------------------------------------

    [Fact]
    public void DELETING_an_existing_component_is_NOT_preserved()
    {
      var after = Doc(Obj(2, "Loft", inputs: (20, new int[0])));
      var r = CheckParams(Before(), after, new HashSet<Guid>());

      Assert.False(r.Preserved);
      Assert.Contains(G(1), r.Deleted);
    }

    [Fact]
    public void EDITING_an_existing_value_is_NOT_preserved()
    {
      // A build has no business moving a slider the user set.
      var after = Doc(Obj(1, "Number Slider", "0.9"), Obj(2, "Loft", inputs: (20, new[] { 1 })));
      var r = CheckParams(Before(), after, new HashSet<Guid>());

      Assert.False(r.Preserved);
      Assert.Contains(G(1), r.Mutated);
    }

    [Fact]
    public void REWIRING_an_existing_input_from_another_EXISTING_component_is_NOT_preserved()
    {
      // THE ONE THAT MATTERS. The Loft's source was swapped from component 1 to component 3 - both
      // pre-existing, so this is the build quietly re-plumbing the user's definition.
      var before = Doc(Obj(1, "Number Slider", "0.5"), Obj(3, "Panel"), Obj(2, "Loft", inputs: (20, new[] { 1 })));
      var after = Doc(Obj(1, "Number Slider", "0.5"), Obj(3, "Panel"), Obj(2, "Loft", inputs: (20, new[] { 3 })));

      var r = CheckParams(before, after, new HashSet<Guid>());
      Assert.False(r.Preserved);
      Assert.Contains(G(2), r.Rewired);
    }

    [Fact]
    public void DROPPING_an_existing_wire_is_NOT_preserved()
    {
      var after = Doc(Obj(1, "Number Slider", "0.5"), Obj(2, "Loft", inputs: (20, new int[0])));
      var r = CheckParams(Before(), after, new HashSet<Guid>());

      Assert.False(r.Preserved);
      Assert.Contains(G(2), r.Rewired);
    }

    [Fact]
    public void REORDERING_existing_sources_is_NOT_preserved()
    {
      // Grasshopper merges multi-source input data in SOURCE-LIST ORDER, so a reorder changes what the
      // definition computes even though the same wires are present (the ADR-090 lesson).
      var before = Doc(Obj(1, "A"), Obj(3, "B"), Obj(2, "Loft", inputs: (20, new[] { 1, 3 })));
      var after = Doc(Obj(1, "A"), Obj(3, "B"), Obj(2, "Loft", inputs: (20, new[] { 3, 1 })));

      var r = CheckParams(before, after, new HashSet<Guid>());
      Assert.False(r.Preserved);
      Assert.Contains(G(2), r.Rewired);
    }

    // ---- D1: new sources APPEND, they never insert ------------------------------------------------

    [Fact]
    public void APPENDING_a_new_source_after_the_existing_ones_is_preserved()
    {
      var before = Doc(Obj(1, "A"), Obj(3, "B"), Obj(2, "Loft", inputs: (20, new[] { 1, 3 })));
      var after = Doc(Obj(1, "A"), Obj(3, "B"), Obj(2, "Loft", inputs: (20, new[] { 1, 3, 50 })), Obj(50, "Panel"));

      var r = CheckParams(before, after, new HashSet<Guid> { G(50) });
      Assert.True(r.Preserved, r.Describe());
    }

    [Fact]
    public void PREPENDING_a_new_source_is_NOT_preserved_even_though_every_old_wire_survives()
    {
      // D1. A subsequence check passes this: filter out the created guid and [50,1,3] still contains
      // [1,3] in order. But GH merges multi-source input data in SOURCE-LIST ORDER, so the Loft now
      // receives 50 FIRST and every positional index downstream resolves to a different item - the
      // wires are intact and the behaviour is not. Prior sources must be a PREFIX, not a subsequence.
      var before = Doc(Obj(1, "A"), Obj(3, "B"), Obj(2, "Loft", inputs: (20, new[] { 1, 3 })));
      var after = Doc(Obj(1, "A"), Obj(3, "B"), Obj(2, "Loft", inputs: (20, new[] { 50, 1, 3 })), Obj(50, "Panel"));

      var r = CheckParams(before, after, new HashSet<Guid> { G(50) });
      Assert.False(r.Preserved, "a new source inserted BEFORE the existing ones must not pass");
      Assert.Contains(G(2), r.Rewired);
    }

    [Fact]
    public void INSERTING_a_new_source_between_existing_ones_is_NOT_preserved()
    {
      var before = Doc(Obj(1, "A"), Obj(3, "B"), Obj(2, "Loft", inputs: (20, new[] { 1, 3 })));
      var after = Doc(Obj(1, "A"), Obj(3, "B"), Obj(2, "Loft", inputs: (20, new[] { 1, 50, 3 })), Obj(50, "Panel"));

      var r = CheckParams(before, after, new HashSet<Guid> { G(50) });
      Assert.False(r.Preserved);
      Assert.Contains(G(2), r.Rewired);
    }

    [Fact]
    public void An_appended_source_from_an_EXISTING_component_is_NOT_preserved()
    {
      // Appending is only permitted from components this build created. A new wire from something that
      // was already on the canvas is the build re-plumbing the definition, wherever it lands.
      var before = Doc(Obj(1, "A"), Obj(3, "B"), Obj(2, "Loft", inputs: (20, new[] { 1 })));
      var after = Doc(Obj(1, "A"), Obj(3, "B"), Obj(2, "Loft", inputs: (20, new[] { 1, 3 })));

      var r = CheckParams(before, after, new HashSet<Guid>());
      Assert.False(r.Preserved);
      Assert.Contains(G(2), r.Rewired);
    }

    [Fact]
    public void A_new_source_does_not_excuse_losing_an_old_one()
    {
      // Subtle: the build added component 50 AND dropped the original source. The added wire must not
      // mask the removal.
      var after = Doc(Obj(1, "Number Slider", "0.5"), Obj(2, "Loft", inputs: (20, new[] { 50 })), Obj(50, "Panel"));
      var r = CheckParams(Before(), after, new HashSet<Guid> { G(50) });

      Assert.False(r.Preserved);
      Assert.Contains(G(2), r.Rewired);
    }

    [Fact]
    public void LOCKING_an_existing_component_is_NOT_preserved()
    {
      // A locked component blocks all data - disabling one silently changes what the definition computes.
      var before = Before();
      var after = Before();
      after.Objects.First(o => o.Guid == G(2)).Locked = true;

      var r = CheckParams(before, after, new HashSet<Guid>());
      Assert.False(r.Preserved);
      Assert.Contains(G(2), r.Mutated);
    }

    [Fact]
    public void FLIPPING_a_tree_toggle_is_NOT_preserved()
    {
      var before = Before();
      var after = Before();
      after.Objects.First(o => o.Guid == G(2)).Simplify = true;

      var r = CheckParams(before, after, new HashSet<Guid>());
      Assert.False(r.Preserved);
      Assert.Contains(G(2), r.Mutated);
    }

    // ---- reporting ---------------------------------------------------------------------------------

    [Fact]
    public void The_description_names_what_went_wrong_rather_than_just_failing()
    {
      var after = Doc(Obj(2, "Loft", inputs: (20, new int[0])));
      var r = CheckParams(Before(), after, new HashSet<Guid>());
      Assert.Contains("removed", r.Describe());
    }

    [Fact]
    public void A_preserved_result_describes_itself_honestly()
    {
      Assert.Contains("unchanged", CheckParams(Before(), Before(), null).Describe());
    }

    [Fact]
    public void Null_snapshots_do_not_throw()
    {
      Assert.True(CheckParams(null, Before(), null).Preserved);   // fail safe (ADR-029)
    }
  }
}
