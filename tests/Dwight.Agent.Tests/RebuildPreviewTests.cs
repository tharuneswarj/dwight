using System.Collections.Generic;
using System.Linq;
using Dwight.Agent;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// THE SENTENCE THE USER CONSENTS TO, and it is the safety property rather than a courtesy.
  ///
  /// #352 scoped the delete-claim to a single turn because a surviving list of things-we-may-delete is
  /// privilege-shaped. That was correct about its own purpose and forbade the commonest request there is
  /// - "rebuild A1" in a new message - so the claim now lives as long as the document. What replaces the
  /// scope is this: A STORED PRIVILEGE EXERCISED LATER IS WHAT #352 FEARED; A CONSENTED ACT WITH ITS
  /// SCOPE IN FRONT OF THE USER AT THE MOMENT OF CONSENT IS NOT.
  ///
  /// So the card must NAME what goes. At document scope a rebuild may be removing components placed
  /// weeks ago in a conversation the user does not remember: "9 components" is a figure, "Width, Height
  /// and Count" is a scope they can act on.
  /// </summary>
  public class RebuildPreviewTests
  {
    private static RebuildComponent C(string nick, string type) =>
      new RebuildComponent { Nick = nick, Type = type };

    private static RegionRebuild Region(string address, IEnumerable<RebuildComponent> remove = null,
      int adopted = 0, int theirs = 0) =>
      new RegionRebuild
      {
        Address = address,
        Remove = (remove ?? Enumerable.Empty<RebuildComponent>()).ToList(),
        Adopted = adopted,
        Theirs = theirs,
      };

    private static string Sentence(params RegionRebuild[] regions) => RebuildPreview.Sentence(regions);

    // ---- the three fates, which together ARE the scope ----------------------------------------------

    [Fact]
    public void The_sentence_names_what_goes_what_you_changed_and_what_was_never_Dwights()
    {
      var line = Sentence(Region("A1",
        new[] { C("Width", "Number Slider"), C("Height", "Number Slider") },
        adopted: 3, theirs: 2));

      Assert.Contains("remove the 2 components it placed there", line);
      Assert.Contains("Width (Number Slider), Height (Number Slider)", line);
      Assert.Contains("LEAVE the 3 you have edited since", line);
      Assert.Contains("leave the 2 it did not place", line);
    }

    /// <summary>
    /// The whole point: components are NAMED, not counted. A user who cannot recognise what is about to
    /// be removed has been given a figure, not a scope - and at document scope the components may have
    /// been placed in a conversation they no longer remember.
    /// </summary>
    [Fact]
    public void Components_are_named_rather_than_counted()
    {
      var line = Sentence(Region("A1", new[] { C("Count", "Number Slider") }));

      Assert.Contains("Count (Number Slider)", line);
    }

    /// <summary>A component whose nickname is just its type name is named once, not twice - "Number
    /// Slider (Number Slider)" reads like a bug and spends the reader's attention for nothing.</summary>
    [Fact]
    public void An_unrenamed_component_is_named_once()
    {
      var line = Sentence(Region("A1", new[] { C("Number Slider", "Number Slider") }));

      Assert.Contains("Number Slider", line);
      Assert.DoesNotContain("Number Slider (Number Slider)", line);
    }

    // ---- bounded, and it says what it left out ------------------------------------------------------

    /// <summary>ADR-035 at a consent card, where it matters more than anywhere else: a card nobody
    /// finishes reading is a card nobody has consented to.</summary>
    [Fact]
    public void A_long_removal_is_bounded_and_says_how_many_it_did_not_name()
    {
      var many = Enumerable.Range(1, 9).Select(i => C("S" + i, "Number Slider")).ToList();

      var line = Sentence(Region("A1", many));

      Assert.Contains("remove the 9 components", line);
      Assert.Contains("S1 (Number Slider)", line);
      Assert.Contains("and 4 more", line);          // 9 named minus the cap of 5
      Assert.DoesNotContain("S9 (Number Slider)", line);
    }

    // ---- silence when there is nothing to say ------------------------------------------------------

    /// <summary>
    /// A first build removes nothing, so the card says only what it always said. A sentence about
    /// removing nothing is noise, and noise on a consent card trains a reader to skip the paragraph that
    /// will one day matter.
    /// </summary>
    [Fact]
    public void A_build_that_removes_nothing_adds_no_sentence_at_all()
    {
      Assert.Null(Sentence(Region("A1")));
      Assert.Null(RebuildPreview.Sentence(null));
      Assert.Null(RebuildPreview.Sentence(new RegionRebuild[0]));
    }

    /// <summary>Only the regions that would actually be touched appear - a card listing regions where
    /// nothing happens is padding the reader has to filter.</summary>
    [Fact]
    public void Regions_where_nothing_would_happen_are_left_out()
    {
      var line = Sentence(Region("A1", new[] { C("Width", "Number Slider") }), Region("A2"));

      Assert.Contains("In A1", line);
      Assert.DoesNotContain("A2", line);
    }

    [Fact]
    public void Several_touched_regions_each_get_their_own_clause()
    {
      var line = Sentence(
        Region("A1", new[] { C("Width", "Number Slider") }),
        Region("B1", theirs: 4));

      Assert.Contains("In A1, Dwight will remove", line);
      Assert.Contains("In B1, Dwight will leave the 4 it did not place", line);
    }

    // ---- the adopted set is never offered for removal -----------------------------------------------

    /// <summary>
    /// There is deliberately no wording offering to remove what the user has edited. Leaving them is the
    /// safe default, the user can delete by hand, and a capability to remove work Dwight KNOWS they
    /// touched would need justifying by a request nobody has made.
    /// </summary>
    [Fact]
    public void Nothing_in_the_sentence_offers_to_remove_what_the_user_edited()
    {
      var line = Sentence(Region("A1", new[] { C("Width", "Number Slider") }, adopted: 3));

      Assert.Contains("LEAVE the 3 you have edited since", line);
      Assert.DoesNotContain("remove the 3", line);
    }

    // ---- #625: what the removal fed outside the region, named on the card ----------------------------

    private static RebuildReconnection R(string type, int id, string region, string input) =>
      new RebuildReconnection { ConsumerType = type, ConsumerId = id, ConsumerRegion = region, Input = input };

    /// <summary>
    /// THE CARD NAMES THE RECONNECTIONS. The card is drawn before the spec exists, so it names WHAT will be
    /// reconnected - the consumer, its region and input - never which new output (#648: a correction to the ruled
    /// shape, accepted). And it says what happens if the build does not reconnect it: nothing changes.
    /// </summary>
    [Fact]
    public void A_rebuild_that_cuts_wires_names_what_it_will_reconnect()
    {
      var region = Region("A1", new[] { C("Grid", "Construct Point") });
      region.Reconnect = new[] { R("XY Plane", 19, "B1", "Origin") };

      var line = Sentence(region);

      Assert.Contains("remove the 1 component it placed there - Grid (Construct Point) - and build it again", line);
      Assert.Contains("and reconnect what it fed outside A1: XY Plane (19) in B1, input Origin", line);
      Assert.Contains("If the new build does not reconnect it, nothing is changed.", line);
    }

    /// <summary>#647 on the card: the user is the one who can drop a wire, so the card tells them how.</summary>
    [Fact]
    public void The_card_tells_the_user_how_to_drop_a_wire_they_do_not_want_kept()
    {
      var region = Region("A1", new[] { C("Grid", "Construct Point") });
      region.Reconnect = new[] { R("XY Plane", 19, "B1", "Origin"), R("Loft", 22, null, "Curves") };

      var line = Sentence(region);

      Assert.Contains("XY Plane (19) in B1, input Origin; Loft (22), input Curves", line);
      Assert.Contains("If the new build does not reconnect them, nothing is changed.", line);
      Assert.Contains("If you would rather lose one of those wires, delete it yourself before approving.", line);
    }

    [Fact]
    public void A_long_reconnection_list_is_bounded_and_says_how_many_it_did_not_name()
    {
      var region = Region("A1", new[] { C("Grid", "Construct Point") });
      region.Reconnect = Enumerable.Range(1, 8).Select(i => R("Loft", 30 + i, "B1", "Curves")).ToList();

      var line = Sentence(region);

      Assert.Contains("Loft (31) in B1, input Curves", line);
      Assert.Contains("and 3 more", line);
      Assert.DoesNotContain("Loft (38)", line);
    }

    /// <summary>#625's narrow sibling on the card: a fresh build that may restore a lost input says so, so the restore
    /// is never a surprise - and a region with nothing else to say still gets that one sentence.</summary>
    [Fact]
    public void A_fresh_build_that_may_restore_a_lost_input_names_it()
    {
      var region = Region("A1");
      region.Restore = new[] { R("XY Plane", 19, "B1", "Origin") };

      var line = Sentence(region);

      Assert.Equal("Building A1 may also restore an input that lost its source when A1's earlier components were " +
                   "removed: XY Plane (19) in B1, input Origin.", line);
    }

    [Fact]
    public void A_rebuild_that_cuts_nothing_says_nothing_about_reconnecting()
    {
      var line = Sentence(Region("A1", new[] { C("Grid", "Construct Point") }));

      Assert.DoesNotContain("reconnect", line);
    }

    // ---- #662a: where the reconnection list ends --------------------------------------------------------

    /// <summary>
    /// THE FIELD TEXT (#662a): "input P and LEAVE the 1 you have edited since" - the clauses ran together. A reconnection
    /// list carries commas inside each entry ("XY Plane (19) in B1, input Origin"), so after one, ", " and " and " mark
    /// nothing: the next clause reads as part of the list. When a removal carries a reconnection list, the clauses are joined
    /// by "; " with "; and " before the last.
    /// </summary>
    [Fact]
    public void With_a_reconnection_list_the_clauses_are_separated_by_semicolons()
    {
      var region = Region("A1", new[] { C("Grid", "Construct Point") }, adopted: 3, theirs: 2);
      region.Reconnect = new[] { R("XY Plane", 19, "B1", "Origin"), R("Loft", 22, null, "Curves") };

      var line = Sentence(region);

      Assert.StartsWith("In A1, Dwight will remove the 1 component it placed there - Grid (Construct Point) - and build it " +
                        "again, and reconnect what it fed outside A1: XY Plane (19) in B1, input Origin; Loft (22), input " +
                        "Curves; LEAVE the 3 you have edited since; and leave the 2 it did not place. ", line);
    }

    /// <summary>The field shape exactly: one reconnection, then the adopted clause.</summary>
    [Fact]
    public void The_field_case_input_P_then_LEAVE_is_separated()
    {
      var region = Region("B1", new[] { C("Radius", "Number Slider") }, adopted: 1);
      region.Reconnect = new[] { R("Circle", 7, "C1", "P") };

      var line = Sentence(region);

      Assert.Contains("input P; and LEAVE the 1 you have edited since. ", line);
      Assert.DoesNotContain("input P and LEAVE", line);
    }

    [Fact]
    public void With_a_reconnection_list_two_clauses_join_with_semicolon_and()
    {
      var region = Region("A1", new[] { C("Grid", "Construct Point") }, theirs: 2);
      region.Reconnect = new[] { R("XY Plane", 19, "B1", "Origin") };

      Assert.Contains("input Origin; and leave the 2 it did not place. ", Sentence(region));
    }

    /// <summary>Without a reconnection list there is nothing to misread, so the sentence is exactly what it was.</summary>
    [Fact]
    public void Without_a_reconnection_list_the_clauses_keep_commas_and_and()
    {
      var line = Sentence(Region("A1", new[] { C("Width", "Number Slider"), C("Height", "Number Slider") },
                                 adopted: 3, theirs: 2));

      Assert.Equal("In A1, Dwight will remove the 2 components it placed there - Width (Number Slider), Height (Number " +
                   "Slider) - and build it again, LEAVE the 3 you have edited since and leave the 2 it did not place.", line);
    }

    /// <summary>The separator is chosen PER REGION: one region's reconnection list does not change another region's
    /// sentence on the same card.</summary>
    [Fact]
    public void The_separator_is_chosen_per_region()
    {
      var a1 = Region("A1", new[] { C("Grid", "Construct Point") }, theirs: 1);
      a1.Reconnect = new[] { R("XY Plane", 19, "B1", "Origin") };
      var b1 = Region("B1", new[] { C("Width", "Number Slider") }, adopted: 2, theirs: 4);

      var line = Sentence(a1, b1);

      Assert.Contains("input Origin; and leave the 1 it did not place.", line);
      Assert.Contains("In B1, Dwight will remove the 1 component it placed there - Width (Number Slider) - and build it " +
                      "again, LEAVE the 2 you have edited since and leave the 4 it did not place.", line);
    }

    [Fact]
    public void One_component_reads_as_one_rather_than_as_1_components()
    {
      var line = Sentence(Region("A1", new[] { C("Width", "Number Slider") }));

      Assert.Contains("the 1 component it placed", line);
      Assert.DoesNotContain("1 components", line);
    }
  }
}
