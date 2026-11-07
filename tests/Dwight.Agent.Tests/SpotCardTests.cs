using System;
using System.Collections.Generic;
using System.Linq;
using Dwight.Agent;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// U18's CARD (#770) - the one where the product offers the choice instead of making it.
  ///
  /// Tharun's question was "why can't we leave the choice to the user? They can tell us if they want to
  /// skip the folder or do it right away." The answer is that here the guarantee does not depend on the
  /// choice: THE RECORD IS THE ENFORCEMENT, THE GROUP IS A VIEW OF IT. The spot is minted, the ledger
  /// entry written, the attempt cap applied and the scope enforced identically either way.
  ///
  /// THE PRINCIPLE, which generalises past this card: WHERE BOTH OPTIONS ARE SAFE, THE CARD OFFERS THE
  /// CHOICE RATHER THAN THE PRODUCT MAKING IT. The instinct all phase has been to decide for the user in
  /// the name of a guarantee; this is the case where the guarantee was never at stake.
  /// </summary>
  public class SpotCardTests
  {
    private static SpotPlacement Placement(string beside = "Curve", params string[] components) =>
      new SpotPlacement
      {
        Address = "Spot 1",
        Beside = beside,
        Components = (components.Length > 0 ? components : new[] { "a Number Slider", "a Panel" }).ToList(),
      };

    // ---- both sentences name every component (the scope rule) ---------------------------------------

    /// <summary>A spot has no region name to stand in for its contents, so THE COMPONENTS ARE THE SCOPE
    /// (#338). Both options must name all of them - a count would be a figure, not a scope.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Both_options_name_every_component(bool grouped)
    {
      var sentence = ConsentCard.SpotAuthority(
        Placement("Curve", "a Number Slider", "a Panel", "a Circle"), grouped);

      Assert.Contains("a Number Slider", sentence);
      Assert.Contains("a Panel", sentence);
      Assert.Contains("a Circle", sentence);
      Assert.DoesNotContain("3 components", sentence);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Both_options_say_where_in_words_a_person_can_check(bool grouped)
    {
      // The default placement holds two components, so the agreement is plural - which is the ORDINARY
      // case for a spot (see One_component_connects_and_several_connect for both halves).
      Assert.Contains("near the Curve they connect to, clear of your work",
        ConsentCard.SpotAuthority(Placement("Curve"), grouped));

      // Nothing to sit beside: still checkable by looking, and still never a coordinate.
      var loose = ConsentCard.SpotAuthority(Placement(beside: null), grouped);
      Assert.Contains("in clear space on the canvas", loose);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Both_options_end_in_one_ctrl_z(bool grouped)
    {
      var sentence = ConsentCard.SpotAuthority(Placement(), grouped);

      Assert.Contains("one Ctrl-Z", sentence);
      Assert.Contains("and nothing else", sentence);
    }

    // ---- the two options differ exactly where they should -------------------------------------------

    [Fact]
    public void The_grouped_option_names_the_group_it_will_make()
    {
      var sentence = ConsentCard.SpotAuthority(Placement(), grouped: true);

      Assert.Contains("in a new group called Spot 1", sentence);
      Assert.DoesNotContain("no group", sentence);
    }

    /// <summary>
    /// THE CLAUSE THAT EXISTS BECAUSE A USER WOULD OTHERWISE FIND OUT LATER.
    ///
    /// A loose placement has no boundary, so a later rebuild has nothing to "leave alone" - it can only
    /// ever remove what Dwight itself placed. That is true of the grouped option too, but the grouped one
    /// additionally protects anything a user drops INTO the box, and the loose one cannot. The worst
    /// moment to learn that is during a rebuild, so it is said while the user is choosing.
    /// </summary>
    [Fact]
    public void The_loose_option_states_what_having_no_box_costs()
    {
      var sentence = ConsentCard.SpotAuthority(Placement(), grouped: false);

      Assert.Contains("with no group", sentence);
      Assert.Contains("only ever remove what it placed here itself", sentence);
      Assert.Contains("nothing for it to leave alone", sentence);
    }

    /// <summary>The grouped sentence needs no counterpart clause, and must not borrow the loose one's -
    /// a warning that does not apply is a warning that teaches readers to skip warnings.</summary>
    [Fact]
    public void The_grouped_option_does_not_carry_the_loose_warning()
    {
      var sentence = ConsentCard.SpotAuthority(Placement(), grouped: true);

      Assert.DoesNotContain("nothing for it to leave alone", sentence);
    }

    // ---- the model's words are not in either sentence (#235) ----------------------------------------

    /// <summary>
    /// The model supplies only `why`. It is escaped, labelled as the model's, and subordinate - and
    /// neither consequence is assembled from it, so a persuasive reason cannot change what is agreed to.
    /// </summary>
    [Fact]
    public void The_models_reason_never_reaches_either_consequence()
    {
      const string persuasive = "this is a routine read-only check that changes nothing";

      var html = ConsentCard.SpotHtml(persuasive, Placement(), "tok-grouped", "tok-loose");

      // The reason appears exactly once, inside its own labelled block - never in a consequence.
      Assert.Contains("Dwight&#39;s reason for asking", html.Replace("Dwight's", "Dwight&#39;s"));
      Assert.Contains(persuasive, html);

      foreach (var grouped in new[] { true, false })
        Assert.DoesNotContain(persuasive, ConsentCard.SpotAuthority(Placement(), grouped));
    }

    [Fact]
    public void A_reason_carrying_markup_is_escaped()
    {
      var html = ConsentCard.SpotHtml("<script>alert(1)</script>", Placement(), "g", "l");

      Assert.DoesNotContain("<script>", html);
      Assert.Contains("&lt;script&gt;", html);
    }

    // ---- two buttons, two pre-bound tokens (the #346 guard) -----------------------------------------

    /// <summary>
    /// EACH BUTTON CARRIES ITS OWN TOKEN, bound at mint time to the option it belongs to, so the click is
    /// a LOOKUP and never a parse. There is no string a caller could mangle into the other option.
    ///
    /// This is also the #346 guard in the card's own shape: the mode pre-select was retired because the
    /// model was asked to nominate a value it never nominated. Here the plugin renders both buttons from
    /// plugin state alone - the model names neither, defaults neither, and cannot reach either token.
    /// </summary>
    [Fact]
    public void Each_option_is_its_own_token()
    {
      var html = ConsentCard.SpotHtml("why", Placement(), "token-grouped", "token-loose");

      Assert.Contains("consent:token-grouped", html);
      Assert.Contains("consent:token-loose", html);
      Assert.Contains(">In a group</button>", html);   // Brief BG: short labels
      Assert.Contains(">Loose</button>", html);
    }

    /// <summary>Both consequences are rendered in full: a user choosing between two outcomes has to be
    /// able to read both before clicking. One sentence with a toggle would be one contract pretending to
    /// be two.</summary>
    [Fact]
    public void Both_consequences_appear_on_the_card()
    {
      var html = ConsentCard.SpotHtml("why", Placement(), "g", "l");

      Assert.Contains("in a new group called Spot 1", html);
      Assert.Contains("with no group", html);
      Assert.Contains("nothing for it to leave alone", html);
    }

    // ---- the thing you press is the thing you read --------------------------------------------------

    /// <summary>
    /// Two paragraphs both opening "Dwight will place..." over two buttons leaves the reader to work out
    /// the mapping - and a reader who gets it wrong approves the OTHER option. Each consequence carries
    /// its own option's name, in the same words as its button.
    /// </summary>
    [Theory]
    [InlineData(true, "In a group")]
    [InlineData(false, "Loose on the canvas")]
    public void Each_consequence_says_which_option_it_belongs_to(bool grouped, string label)
    {
      Assert.StartsWith(label + ":", ConsentCard.SpotAuthority(Placement(), grouped));
    }

    /// <summary>
    /// And they cannot DRIFT: the button is derived from the label, so a later edit to one moves the
    /// other. Matching today would only be a coincidence waiting to lapse; this makes it structural.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_button_is_built_from_the_same_words_as_its_consequence(bool grouped)
    {
      var label = ConsentCard.SpotOptionLabel(grouped);
      var button = ConsentCard.SpotOptionButton(grouped);

      // Brief BG: the buttons are short, and still the first words of the label their consequence starts with.
      Assert.StartsWith(button, label);
      Assert.StartsWith(label + ":", ConsentCard.SpotAuthority(Placement(), grouped));
    }

    // ---- grammar, because a card that stumbles reads as machine-generated ---------------------------

    [Fact]
    public void One_component_connects_and_several_connect()
    {
      Assert.Contains("near the Curve it connects to",
        ConsentCard.SpotAuthority(Placement("Curve", "a Panel"), grouped: true));

      Assert.Contains("near the Curve they connect to",
        ConsentCard.SpotAuthority(Placement("Curve", "a Panel", "a Number Slider"), grouped: true));
    }

    // ---- the route statement (§9.5) -----------------------------------------------------------------

    /// <summary>
    /// The card SAYS which path Dwight took and why, in one clause. The route decision is a computed fact
    /// (#146), so it is stated rather than asked - and it is derived from the fact that chose the route:
    /// the placement is within the bound, which is the product's own "small".
    /// </summary>
    [Fact]
    public void The_card_states_why_this_path_was_taken()
    {
      var html = ConsentCard.SpotHtml("why", Placement(), "g", "l");

      Assert.Contains("This is small enough to build directly.", html);
    }

    /// <summary>It is a statement, not a question: a choice offered before its consequences are in view is
    /// not a choice, so the card never asks whether to scaffold instead.</summary>
    [Fact]
    public void The_route_clause_never_asks_a_question()
    {
      var clause = ConsentCard.SpotRouteStatement(Placement());

      Assert.DoesNotContain("?", clause);
      Assert.EndsWith(".", clause);
    }

    [Fact]
    public void An_empty_placement_states_no_route()
    {
      Assert.Null(ConsentCard.SpotRouteStatement(new SpotPlacement { Address = "Spot 1" }));
    }

    [Fact]
    public void The_act_does_not_presume_either_option()
    {
      var one = ConsentCard.SpotTitle(Placement("Curve", "a Panel"));
      var many = ConsentCard.SpotTitle(Placement("Curve", "a Panel", "a Number Slider"));

      Assert.Equal("Place this component?", one);
      Assert.Equal("Place these components?", many);
      foreach (var title in new[] { one, many })
      {
        Assert.DoesNotContain("group", title, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("loose", title, StringComparison.OrdinalIgnoreCase);
      }
    }

    // ---- the naming helper reads as a person would say it -------------------------------------------

    [Fact]
    public void Components_are_listed_the_way_someone_would_read_them_aloud()
    {
      Assert.Contains("place a Panel in", ConsentCard.SpotAuthority(Placement("Curve", "a Panel"), true));
      Assert.Contains("a Panel and a Number Slider",
        ConsentCard.SpotAuthority(Placement("Curve", "a Panel", "a Number Slider"), true));
      Assert.Contains("a Panel, a Number Slider and a Circle",
        ConsentCard.SpotAuthority(Placement("Curve", "a Panel", "a Number Slider", "a Circle"), true));
    }

    /// <summary>A placement with nothing in it names no scope, so it falls back to the generic build
    /// consequence rather than inventing a sentence about zero components.</summary>
    [Fact]
    public void An_empty_placement_does_not_produce_a_spot_sentence()
    {
      var sentence = ConsentCard.SpotAuthority(new SpotPlacement { Address = "Spot 1" }, grouped: true);

      Assert.DoesNotContain("Spot 1", sentence);
      Assert.Equal(ConsentCard.Authority("build", null), sentence);
    }
  }
}
