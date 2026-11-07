using System;
using System.IO;
using System.Text.Json.Nodes;
using Dwight.UI;
using Xunit;

namespace Dwight.Session.Tests
{
  /// <summary>
  /// The thirteenth gate's Learn capture read "Two number sliders (`width` and `height`)", backticks and all, where its Walk capture read plain words. The two share one path, and the Walk's reply
  /// simply held no code span, so the line is held here, at the door both go through, for any summary that holds one.
  /// </summary>
  public class WalkSummaryTests
  {
    private const string FromTheGate = "Two number sliders (`width` and `height`) feed a Multiplication (`area`) whose result is shown in a Panel (`shown`).";

    private static string View(string summary) => new JsonObject
    {
      ["summary"] = summary,
      ["steps"] = new JsonArray(new JsonObject { ["text"] = "The <code>Loft</code> (1)", ["md"] = "The `Loft` (1)", ["ids"] = new JsonArray("a") }),
    }.ToJsonString();

    private static string Summary(string view) => JsonNode.Parse(view)["summary"].GetValue<string>();

    [Fact]
    public void The_gates_summary_draws_as_plain_words_with_no_backtick()
    {
      var drawn = Summary(WalkOrder.ForPanel(View(FromTheGate)));
      Assert.Equal("Two number sliders (width and height) feed a Multiplication (area) whose result is shown in a Panel (shown).", drawn);
      Assert.DoesNotContain("`", drawn);
    }

    [Theory]
    [InlineData("A **loft** through `Curve`.", "A loft through Curve.")]
    [InlineData("A *loft* through the curves.", "A loft through the curves.")]
    [InlineData("`Number Slider` feeds `A`.", "Number Slider feeds A.")]
    public void Every_mark_the_prompt_teaches_reads_as_its_words(string summary, string words) =>
      Assert.Equal(words, Summary(WalkOrder.ForPanel(View(summary))));

    [Theory]
    [InlineData("A lone ` mark stays, because nothing a model wrote disappears.")]
    [InlineData("Multiplies width * height.")]
    [InlineData("A snake_case_name stays whole.")]
    [InlineData("<b>Not a tag</b>: the shell escapes this.")]
    public void What_is_not_a_mark_stays_as_written(string summary) => Assert.Equal(summary, Summary(WalkOrder.ForPanel(View(summary))));

    [Fact]
    public void A_summary_with_no_marks_leaves_the_view_byte_for_byte()
    {
      var view = View("A loft through the curves.");
      Assert.Same(view, WalkOrder.ForPanel(view));
    }

    [Fact]
    public void Only_the_summary_changes_so_the_steps_rendered_text_and_its_marks_are_what_they_were()
    {
      var drawn = JsonNode.Parse(WalkOrder.ForPanel(View(FromTheGate)));
      var step = drawn["steps"][0];
      Assert.Equal("The <code>Loft</code> (1)", step["text"].GetValue<string>());
      Assert.Equal("The `Loft` (1)", step["md"].GetValue<string>());
      Assert.Equal("a", step["ids"][0].GetValue<string>());
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("{\"steps\":[]}")]
    [InlineData("{\"summary\":\"\",\"steps\":[]}")]
    [InlineData("{\"summary\":7}")]
    public void A_view_it_cannot_read_or_with_no_summary_is_returned_as_it_came(string view) => Assert.Equal(view, WalkOrder.ForPanel(view));

    /// <summary>The door is the panel's, which needs Eto, so it is read from the source (ADR-091). Every view reaches the shell here, live and replayed; what a pin keeps is read from the view as recorded.</summary>
    [Fact]
    public void The_panel_draws_through_it_and_the_pin_keeps_the_marks()
    {
      var dir = new DirectoryInfo(AppContext.BaseDirectory);
      while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Dwight.sln"))) dir = dir.Parent;
      var panel = File.ReadAllText(Path.Combine(dir.FullName, "Dwight", "UI", "WebPanel.cs"));
      Assert.Contains("_stepTexts = ParseStepTexts(stepsJson, out _stepSummary);", panel);
      Assert.Contains("Run($\"dwightSteps({Js(WalkOrder.ForPanel(stepsJson))})\");", panel);
      Assert.Equal(panel.IndexOf("dwightSteps(", StringComparison.Ordinal), panel.LastIndexOf("dwightSteps(", StringComparison.Ordinal));   // one door
    }

    /// <summary>The to-script preview's hint is the same class: model text the shell shows as escaped text. LlmActions needs Rhino, so its one line is read from the source and the reader is run here.</summary>
    [Fact]
    public void A_to_script_previews_hint_reads_as_plain_words_too()
    {
      Assert.Equal("Sums the Curve lengths.", Dwight.Session.NoteMarkdown.Words("Sums the `Curve` lengths."));

      var dir = new DirectoryInfo(AppContext.BaseDirectory);
      while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Dwight.sln"))) dir = dir.Parent;
      var actions = File.ReadAllText(Path.Combine(dir.FullName, "Dwight", "LlmActions.cs"));
      Assert.Contains("[\"summary\"] = NoteMarkdown.Words(p.Summary ?? \"\"),", actions);
      Assert.DoesNotContain("[\"summary\"] = p.Summary", actions);
    }
  }
}
