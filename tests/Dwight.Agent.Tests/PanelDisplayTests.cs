using System.Linq;
using Dwight.Agent;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// U31 (#926): a fed Panel reports what it SHOWS. The bound is the decision (ADR-035): a cut list must say it
  /// was cut and how much there is, and the total can never read as less than what is listed.
  /// </summary>
  public class PanelDisplayTests
  {
    [Fact]
    public void Under_the_cap_every_item_is_listed_and_nothing_says_cut()
    {
      var v = PanelDisplay.Fed(new[] { "8672.21", "61.2" }, totalCount: 2, paths: 1, maxItems: 25, maxChars: 400);
      Assert.Equal("panel", v.Kind);
      Assert.Equal(new[] { "8672.21", "61.2" }, v.Displayed);
      Assert.Equal(2, v.DisplayedCount);
      Assert.Equal(1, v.DisplayedPaths);
      Assert.False(v.DisplayedTruncated);
      Assert.Null(v.Text);   // the placeholder is not reported
    }

    [Fact]
    public void Cut_by_item_count_lists_the_head_and_states_the_total()
    {
      var first = Enumerable.Range(0, 25).Select(i => i.ToString()).ToList();   // the shim hands in at most MaxItems
      var v = PanelDisplay.Fed(first, totalCount: 486, paths: 5, maxItems: 25, maxChars: 4000);
      Assert.Equal(25, v.Displayed.Length);
      Assert.Equal(486, v.DisplayedCount);
      Assert.True(v.DisplayedTruncated);
    }

    [Fact]
    public void Cut_by_characters_stops_before_the_item_that_would_overflow()
    {
      var first = new[] { new string('a', 150), new string('b', 150), new string('c', 150) };
      var v = PanelDisplay.Fed(first, totalCount: 3, paths: 1, maxItems: 25, maxChars: 400);
      Assert.Equal(2, v.Displayed.Length);
      Assert.Equal(3, v.DisplayedCount);
      Assert.True(v.DisplayedTruncated);
    }

    [Fact]
    public void One_enormous_item_still_shows_its_head()
    {
      var v = PanelDisplay.Fed(new[] { new string('x', 10_000) }, totalCount: 1, paths: 1, maxItems: 25, maxChars: 400);
      Assert.Single(v.Displayed);
      Assert.Equal(400, v.Displayed[0].Length);
      Assert.Equal(1, v.DisplayedCount);
    }

    [Fact]
    public void A_fed_panel_showing_nothing_is_an_empty_list_not_null()
    {
      // Wired, but the upstream produced nothing: the panel is still FED, and that is a different fact from
      // "holds this text" - so Displayed is present and empty, never null (null would mean unfed).
      var v = PanelDisplay.Fed(new string[0], totalCount: 0, paths: 0, maxItems: 25, maxChars: 400);
      Assert.NotNull(v.Displayed);
      Assert.Empty(v.Displayed);
      Assert.Equal(0, v.DisplayedCount);
      Assert.False(v.DisplayedTruncated);
    }

    [Fact]
    public void The_total_never_reads_as_less_than_what_is_listed()
    {
      var v = PanelDisplay.Fed(new[] { "1", "2", "3" }, totalCount: 0, paths: 1, maxItems: 25, maxChars: 400);
      Assert.Equal(3, v.DisplayedCount);
    }

    [Fact]
    public void A_null_item_is_said_not_dropped()
    {
      var v = PanelDisplay.Fed(new string[] { null, "2" }, totalCount: 2, paths: 1, maxItems: 25, maxChars: 400);
      Assert.Equal(new[] { "<null>", "2" }, v.Displayed);
    }

    [Fact]
    public void The_sentence_says_these_are_displayed_values_not_typed_text()
    {
      Assert.Contains("SHOWS", PanelDisplay.Means);
      Assert.Contains("not text the user typed", PanelDisplay.Means);
      Assert.Contains("placeholder", PanelDisplay.Means);
    }
  }
}
