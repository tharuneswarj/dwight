using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// Brief CV: A PLACEMENT WITHOUT A MAP HOLDS TWELVE, AND THE CARD STAYS READABLE BY FOLDING. Tharun ruled that the old limit of five should not
  /// be a bottleneck. Twelve is the bound of one block on the canvas, not of the card: past five the card's sentences name five and "and N more", its
  /// authority line states the total, and its body lists every one. Up to five a card reads as it always did. Every statement of the number to the model is held to the constant here (#315).
  /// </summary>
  public class SpotCapTests
  {
    private static SpotPlacement Placement(int n, string into = null, string beside = null) => new SpotPlacement
    {
      Address = "Spot 1",
      Into = into,
      Beside = beside,
      Components = Enumerable.Range(1, n).Select(i => "a Part " + i).ToList(),
    };

    private static List<string> Names(int n) => Enumerable.Range(1, n).Select(i => "a Part " + i).ToList();

    private const string FiveNames = "a Part 1, a Part 2, a Part 3, a Part 4, a Part 5";

    // ---- the cap ----------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Twelve_are_described_and_thirteen_are_refused_with_the_keys_the_stop_line_reads()
    {
      Assert.True(SpotPlacement.TryDescribe(Names(12), "Spot 1", null, out var placement, out var none));
      Assert.Null(none);
      Assert.Equal(12, placement.Components.Count);

      Assert.False(SpotPlacement.TryDescribe(Names(13), "Spot 1", null, out var refused, out var error));
      Assert.Null(refused);
      Assert.Equal(ToolErrors.PreconditionRefused, error.Code);
      Assert.Equal(13, error.Detail["asked"].GetValue<int>());
      Assert.Equal(12, error.Detail["mostThatCanBeNamed"].GetValue<int>());
    }

    /// <summary>The refusal is a claim to the model about our own behaviour, so it gives the reason the ruling gave: the block, not the card, which now folds.</summary>
    [Fact]
    public void The_refusal_gives_the_blocks_reason_and_names_the_door()
    {
      SpotPlacement.TryDescribe(Names(13), "Spot 1", null, out _, out var error);

      Assert.Contains("That is 13 components to place at once.", error.Message);
      Assert.Contains("they land as one block", error.Message);
      Assert.Contains("past 12 a block stops being", error.Message);
      Assert.Contains("propose_scaffold", error.Message);
      Assert.DoesNotContain("the card stops being", error.Message);
    }

    /// <summary>The stop line after two such requests is Brief CU's, and this move must not disturb it.</summary>
    [Fact]
    public void The_stop_line_still_reads_the_keys()
    {
      SpotPlacement.TryDescribe(Names(13), "Spot 1", null, out _, out var error);

      var note = AgentLoopCore.RefusedRequestNote("{\"kind\":\"build\"}", error.ToJson());

      Assert.Equal("Dwight stopped: it asked twice to approve a build, and the last request placed more components than one card can name.", note);
    }

    /// <summary>The rebuild card's sentence and a placement's bound were one constant, and they are two rules: a rebuild still names five.</summary>
    [Fact]
    public void The_rebuild_sentence_and_the_fold_keep_their_own_number()
    {
      Assert.Equal(5, RebuildPreview.MaxNamed);
      Assert.NotEqual(RebuildPreview.MaxNamed, SpotPlacement.MaxComponents);
    }

    // ---- the fold ---------------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Six_names_fold_to_five_and_one_more_in_both_options(bool grouped)
    {
      var sentence = ConsentCard.SpotAuthority(Placement(6), grouped);

      Assert.Contains(FiveNames + " and 1 more: 6 components", sentence);
      Assert.DoesNotContain("a Part 6", sentence);
    }

    [Fact]
    public void Six_names_fold_in_the_act_and_the_done_line()
    {
      Assert.Equal("Place " + FiveNames + " and 1 more, Spot 1, in clear space on the canvas.", ConsentCard.SpotAct(Placement(6)));
      Assert.Equal("Placed " + FiveNames + " and 1 more, Spot 1, in clear space on the canvas.", ConsentCard.SpotDone(Placement(6)));
    }

    [Fact]
    public void A_placement_into_a_group_folds_the_same_way()
    {
      var into = Placement(9, into: "Answer Key");

      Assert.Equal("Place " + FiveNames + " and 4 more in Answer Key.", ConsentCard.SpotAct(into));
      Assert.Equal("Placed " + FiveNames + " and 4 more in Answer Key.", ConsentCard.SpotDone(into));
      Assert.Contains(FiveNames + " and 4 more: 9 components inside Answer Key", ConsentCard.SpotAuthority(into, true));
    }

    [Fact]
    public void Five_names_do_not_fold()
    {
      const string five = "a Part 1, a Part 2, a Part 3, a Part 4 and a Part 5";

      Assert.Equal("Place " + five + ", Spot 1, in clear space on the canvas.", ConsentCard.SpotAct(Placement(5)));
      Assert.Equal("Placed " + five + ", Spot 1, in clear space on the canvas.", ConsentCard.SpotDone(Placement(5)));
      foreach (var grouped in new[] { true, false })
      {
        Assert.Contains(five, ConsentCard.SpotAuthority(Placement(5), grouped));
        Assert.DoesNotContain(" more", ConsentCard.SpotAuthority(Placement(5), grouped));
      }
    }

    /// <summary>Only a spot's sentences fold. The regions a build card names are the scope it approves, every one, however many (#338).</summary>
    [Fact]
    public void A_region_builds_act_still_names_every_region()
    {
      var regions = Enumerable.Range(1, 7).Select(i => "A" + i).ToList();

      Assert.Equal("Build A1, A2, A3, A4, A5, A6 and A7.", ConsentCard.BuildAct(regions));
    }

    /// <summary>The title names no component at any size, so there is nothing in it to fold, and it stays short.</summary>
    [Theory]
    [InlineData(6)]
    [InlineData(12)]
    public void The_title_names_no_component(int n)
    {
      Assert.Equal("Place these components?", ConsentCard.SpotTitle(Placement(n)));
    }

    // ---- the total: said by naming them all, or as a number once the sentence has folded -----------------------------------------------------

    public static IEnumerable<object[]> FoldedSizes() => Enumerable.Range(6, 7).Select(n => new object[] { n });

    public static IEnumerable<object[]> SmallSizes() => Enumerable.Range(1, 5).Select(n => new object[] { n });

    private static IEnumerable<string> EveryAuthority(int n)
    {
      foreach (var placement in new[] { Placement(n), Placement(n, beside: "Curve"), Placement(n, into: "Answer Key") })
        foreach (var grouped in new[] { true, false })
          yield return ConsentCard.SpotAuthority(placement, grouped);
    }

    [Theory]
    [MemberData(nameof(FoldedSizes))]
    public void A_folded_authority_line_states_the_total_as_the_count(int n)
    {
      foreach (var sentence in EveryAuthority(n))
      {
        var match = Regex.Match(sentence, @" and (\d+) more: (\d+) components ");
        Assert.True(match.Success, sentence);
        Assert.Equal(n - 5, int.Parse(match.Groups[1].Value));
        Assert.Equal(n, int.Parse(match.Groups[2].Value));
      }
    }

    /// <summary>A sentence that names every component has said the total, so up to five there is no digit and every name is there.</summary>
    [Theory]
    [MemberData(nameof(SmallSizes))]
    public void A_small_authority_line_names_every_component_and_states_no_number(int n)
    {
      foreach (var sentence in EveryAuthority(n))
      {
        foreach (var name in Names(n)) Assert.Contains(name, sentence);
        Assert.DoesNotContain(" component", sentence.Replace("no group", ""));
        Assert.DoesNotContain(" more", sentence);
      }
    }

    /// <summary>The small card's words, whole, because they moved once and must not again: "a Panel" stays "a Panel".</summary>
    [Fact]
    public void The_small_cards_words_are_what_they_always_were()
    {
      Assert.Equal(
        "In a group: Dwight will place a Panel in a new group called Spot 1, in clear space on the canvas - and nothing else. " +
        "The build is checked and reverts itself if it cannot be verified, and one Ctrl-Z removes all of it.",
        ConsentCard.SpotAuthority(new SpotPlacement { Address = "Spot 1", Components = new List<string> { "a Panel" } }, true));
      Assert.Equal(
        "In a group: Dwight will place a Part 1, a Part 2, a Part 3, a Part 4 and a Part 5 in a new group called Spot 1, in clear space on the canvas - and nothing else. " +
        "The build is checked and reverts itself if it cannot be verified, and one Ctrl-Z removes all of it.",
        ConsentCard.SpotAuthority(Placement(5), true));
    }

    /// <summary>And the folded one, whole, as the person reads it.</summary>
    [Fact]
    public void The_folded_cards_words()
    {
      Assert.Equal(
        "In a group: Dwight will place " + FiveNames + " and 4 more: 9 components in a new group called Spot 1, in clear space on the canvas - and nothing else. " +
        "The build is checked and reverts itself if it cannot be verified, and one Ctrl-Z removes all of it.",
        ConsentCard.SpotAuthority(Placement(9), true));
    }

    // ---- how many scripts: the region card's own number, in its own words --------------------------------------------------------------------

    /// <summary>The refusal is one function, so a spot and a region card cannot say different things.</summary>
    [Fact]
    public void A_cards_script_limit_is_refused_with_its_number_and_the_stop_line_reads_it()
    {
      var error = ScriptsAsk.TooMany(11);

      Assert.Equal(ToolErrors.InvalidArgs, error.Code);
      Assert.Contains("at most " + ScriptsAsk.MaxScripts + " scripts", error.Message);
      Assert.Contains("this asks for 11", error.Message);
      Assert.Equal(11, error.Detail["asked"].GetValue<int>());
      Assert.Equal(ScriptsAsk.MaxScripts, error.Detail["mostScripts"].GetValue<int>());
      Assert.Equal("Dwight stopped: it asked twice to approve a build, and the last request held more scripts than one card can show.",
        AgentLoopCore.RefusedRequestNote("{\"kind\":\"build\"}", error.ToJson()));
    }

    [Fact]
    public void A_region_card_refuses_in_those_words()
    {
      var many = new JsonArray(Enumerable.Range(0, ScriptsAsk.MaxScripts + 1)
        .Select(i => (JsonNode)new JsonObject { ["region"] = "A1", ["type"] = "Python 3 Script", ["source"] = "a = 1\n" }).ToArray());

      Assert.False(ScriptsAsk.Resolve(many, new[] { "A1" }, out _, out var error));
      Assert.Equal(ScriptsAsk.TooMany(ScriptsAsk.MaxScripts + 1).Message, error.Message);
    }

    // ---- the body: every component, its own row, only the sentences fold --------------------------------------------------------------------

    private static List<string> Rows(string html) =>
      Regex.Matches(html, "<li>(.*?)</li>").Cast<Match>().Select(m => m.Groups[1].Value).ToList();

    [Theory]
    [InlineData(6)]
    [InlineData(9)]
    [InlineData(12)]
    public void Once_the_sentences_fold_the_body_lists_every_component_on_its_own_row(int n)
    {
      var html = ConsentCard.SpotHtml("why", Placement(n), "g", "l");

      Assert.Equal(Names(n), Rows(html));
      Assert.Contains("All " + n + " components:", html);

      // Inside the card's one fold: the rows sit with the sentences, and the fold's label says where to read them.
      var fold = html.IndexOf("<details class=\"consent-more\">", StringComparison.Ordinal);
      Assert.True(fold >= 0);
      Assert.True(html.IndexOf("consent-names", StringComparison.Ordinal) > fold);
    }

    /// <summary>While the sentences name every one the same list twice would be noise, so such a card has no rows.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public void While_the_sentences_name_every_one_there_are_no_rows(int n)
    {
      Assert.Equal("", ConsentCard.SpotComponentRows(Placement(n)));
      Assert.DoesNotContain("consent-names", ConsentCard.SpotHtml("why", Placement(n), "g", "l"));
    }

    [Fact]
    public void A_placement_with_nothing_has_no_rows()
    {
      Assert.Equal("", ConsentCard.SpotComponentRows(null));
      Assert.Equal("", ConsentCard.SpotComponentRows(new SpotPlacement { Address = "Spot 1" }));
    }

    /// <summary>A nickname is text the model or the user wrote, so a row escapes it (#335).</summary>
    [Fact]
    public void A_row_escapes_what_it_carries()
    {
      var placement = Placement(6);
      placement.Components[5] = "a <img src=x onerror=y> Part";

      var rows = ConsentCard.SpotComponentRows(placement);

      Assert.DoesNotContain("<img", rows);
      Assert.Contains("a &lt;img src=x onerror=y&gt; Part", rows);
    }

    /// <summary>The consent surface shows a script whole (Phase 7 WP-4): one named past the fold still has its row and its complete source.</summary>
    [Fact]
    public void A_scripts_source_is_never_folded()
    {
      var source = string.Join("\n", Enumerable.Range(1, 60).Select(i => "a = " + i));
      var placement = Placement(7);
      placement.Components[6] = "a Python 3 Script";
      placement.Scripts = new List<SpotScript>
      {
        new SpotScript { Name = "Python 3 Script", LanguageLabel = "Python 3", Source = source, Outputs = new List<string> { "a" } },
      };

      Assert.DoesNotContain("a Python 3 Script", ConsentCard.SpotAct(placement));
      var html = ConsentCard.SpotHtml("why", placement, "g", "l");

      Assert.Contains("a Python 3 Script", Rows(html));
      Assert.Contains(source, html);
    }

    // ---- what the model is told: the number, from the constant (#315) -----------------------------------------------------------------------

    private static string Most => SpotPlacement.MaxComponents.ToString();

    [Fact]
    public void The_build_capability_states_the_number()
    {
      var said = AgentHostTools.ScopeOf("build");

      Assert.Contains("up to " + Most + " components WITHOUT a map", said);
      Assert.Contains("More than " + Most + " needs a map first", said);
    }

    [Fact]
    public void The_request_descriptor_states_the_number_on_both_fields()
    {
      var schema = JsonNode.Parse(AgentHostTools.SchemaOf(AgentHostTools.RequestMutation));
      var properties = schema["properties"];
      var regions = properties["regions"]["description"].GetValue<string>();
      var place = properties["place"]["description"].GetValue<string>();

      Assert.Contains("To place up to " + Most + " components with no map at all", regions);
      Assert.Contains("The components to put down, at most " + Most + " and no more than " + ScriptsAsk.MaxScripts + " of them scripts with source, and how they wire.", place);
      Assert.Contains("More than " + Most + " is work that wants a map", place);
    }

    [Fact]
    public void The_routing_refusal_states_the_number()
    {
      var routing = SpotAsk.RouteFor(new HandoffSeed
      {
        Regions = new List<string> { "A1" },
        Place = new JsonObject { ["components"] = new JsonArray() },
      });

      Assert.Contains("To put up to " + Most + " components down without a map", routing.Refusal.Message);
    }

    /// <summary>"A few" told the model nothing it could count, and a model that reads "few" takes the map at six. Nothing it is told about placing says that now.</summary>
    [Fact]
    public void Nothing_the_model_is_told_about_placing_says_few()
    {
      foreach (var text in new[] { AgentHostTools.ScopeOf("build"), AgentHostTools.SchemaOf(AgentHostTools.RequestMutation) })
      {
        Assert.DoesNotContain("few components", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("few enough", text, StringComparison.OrdinalIgnoreCase);
      }
    }
  }
}
