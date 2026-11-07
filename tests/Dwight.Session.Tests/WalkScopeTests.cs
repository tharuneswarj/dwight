using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dwight.UI;
using Xunit;

namespace Dwight.Session.Tests
{
  /// <summary>
  /// Brief EB, from the showcase run's stop: six of a lamp's seventeen components selected, and the Walk lit Height per step, Layer Height and Vase G-code, and its summary read "Slowest: Vase G-code (15), 1,644 ms".
  /// It never showed on a gate, because the gates selected everything. A step is made of the selection's components only, and a neighbour is words in a sentence, in the boundary form.
  /// </summary>
  public class WalkScopeTests
  {
    private static Guid G(int n) => new Guid(n, 0, 0, new byte[8]);

    // The six selected, as ids 1 to 6, and three outside, as 12, 13 and 15.
    private static readonly List<Guid> Six = Enumerable.Range(1, 6).Select(G).ToList();
    private static readonly HashSet<int> Outside = new HashSet<int> { 12, 13, 15 };

    private static JsonArray Ids(params int[] ids) => new JsonArray(ids.Select(i => (JsonNode)G(i).ToString()).ToArray());

    private static JsonObject Step(string text, int[] ids, JsonArray marks = null)
    {
      var o = new JsonObject { ["text"] = text, ["md"] = text, ["ids"] = Ids(ids) };
      if (marks != null) o["marks"] = marks;
      return o;
    }

    private static string View(params JsonObject[] steps) =>
      new JsonObject { ["summary"] = "A lamp's profile.", ["steps"] = new JsonArray(steps.Cast<JsonNode>().ToArray()) }.ToJsonString();

    /// <summary>The showcase's shape: a step with one outside, one with two, one only outside, and one whole.</summary>
    private static string TheStop() => View(
      Step("`Height` (1) feeds `Division` (12).", new[] { 1, 12 }),
      Step("`Radius` (2) and `Waves` (3) meet.", new[] { 2, 3 }),
      Step("`Wave Depth` (4) with `Layer Height` (13) and `Vase G-code` (15).", new[] { 4, 13, 15 }),
      Step("`Vase G-code` (15) writes the file.", new[] { 15 }),
      Step("`Wavy Shade` (5) reaches `Vase Toolpath` (6).", new[] { 5, 6 }));

    private static List<IReadOnlyList<Guid>> StepsOf(string view) =>
      JsonNode.Parse(view)["steps"].AsArray().Select(s => (IReadOnlyList<Guid>)WalkOrder.IdsOf(s)).ToList();

    // ---- a step is the selection's ---------------------------------------------------------------------------------------

    [Fact]
    public void Every_steps_members_are_within_the_six()
    {
      var steps = StepsOf(WalkScope.Within(TheStop(), Six));
      Assert.Equal(4, steps.Count);
      Assert.All(steps, s => Assert.All(s, id => Assert.Contains(id, Six)));
      Assert.Equal(new[] { G(1) }, steps[0]);
      Assert.Equal(new[] { G(4) }, steps[2]);
    }

    [Fact]
    public void What_a_walk_lights_step_by_step_is_within_the_six()
    {
      var state = new WalkState(StepsOf(WalkScope.Within(TheStop(), Six)), Six);
      var lit = new List<Guid>();
      while (state.Next()) lit.AddRange(state.Current);
      Assert.Equal(Six, lit.Distinct().OrderBy(g => g));
      Assert.DoesNotContain(G(15), lit);
      Assert.DoesNotContain(G(12), lit);
    }

    [Fact]
    public void A_step_only_about_what_lies_outside_is_not_a_step()
    {
      var view = JsonNode.Parse(WalkScope.Within(TheStop(), Six));
      Assert.DoesNotContain(view["steps"].AsArray(), s => s["text"].GetValue<string>().Contains("writes the file"));
    }

    [Fact]
    public void A_step_with_no_components_stays_where_the_model_put_it()
    {
      var view = View(Step("Together these make the profile.", new int[0]), Step("`Height` (1) starts it.", new[] { 1 }), Step("Only outside.", new[] { 12 }));
      var steps = JsonNode.Parse(WalkScope.Within(view, Six))["steps"].AsArray();
      Assert.Equal(2, steps.Count);
      Assert.Equal("Together these make the profile.", steps[0]["text"].GetValue<string>());
    }

    [Fact]
    public void If_every_step_lay_outside_the_sentences_stay_with_nothing_lit()
    {
      var view = View(Step("One.", new[] { 12 }), Step("Two.", new[] { 15 }));
      var steps = JsonNode.Parse(WalkScope.Within(view, Six))["steps"].AsArray();
      Assert.Equal(2, steps.Count);
      Assert.All(steps, s => Assert.Empty(s["ids"].AsArray()));
    }

    [Fact]
    public void If_every_step_lay_outside_the_summary_says_why_nothing_lights_before_the_models_words()
    {
      var held = WalkScope.Within(View(Step("One.", new[] { 12 }), Step("Two.", new[] { 15 })), Six);
      Assert.Equal("No step of this run lies within the selection; the sentences name its neighbours. A lamp's profile.", JsonNode.Parse(held)["summary"].GetValue<string>());
      var state = new WalkState(StepsOf(held), Six);
      Assert.True(state.Next());
      Assert.Empty(state.Current);   // the step is there to read, and nothing of it lights
    }

    [Fact]
    public void A_walk_with_no_summary_of_its_own_still_says_why_nothing_lights()
    {
      var view = new JsonObject { ["steps"] = new JsonArray(Step("One.", new[] { 12 })) }.ToJsonString();
      Assert.Equal(WalkScope.NothingWithin, JsonNode.Parse(WalkScope.Within(view, Six))["summary"].GetValue<string>());
    }

    /// <summary>A new run is held before it is recorded and again at the panel's door, so it must say this once.</summary>
    [Fact]
    public void Held_twice_it_says_so_once()
    {
      var once = WalkScope.Within(View(Step("One.", new[] { 12 })), Six);
      Assert.Same(once, WalkScope.Within(once, Six));
    }

    [Fact]
    public void A_walk_with_a_step_inside_or_with_no_steps_does_not_say_it()
    {
      Assert.DoesNotContain(WalkScope.NothingWithin, WalkScope.Within(TheStop(), Six));
      var noSteps = View();
      Assert.Same(noSteps, WalkScope.Within(noSteps, Six));
      Assert.DoesNotContain(WalkScope.NothingWithin, WalkScope.Within(View(Step("Words only.", new int[0])), Six));
    }

    [Fact]
    public void A_walk_of_a_whole_selection_comes_back_byte_for_byte()
    {
      var view = View(Step("`Height` (1).", new[] { 1 }, new JsonArray(new JsonObject { ["t"] = "highlight", ["ids"] = Ids(1) })), Step("`Radius` (2).", new[] { 2, 3 }));
      Assert.Same(view, WalkScope.Within(view, Six));
    }

    [Fact]
    public void A_row_with_no_selection_recorded_comes_back_as_it_came()
    {
      var view = TheStop();
      Assert.Same(view, WalkScope.Within(view, null));
      Assert.Same(view, WalkScope.Within(view, new Guid[0]));
      Assert.Equal("not json", WalkScope.Within("not json", Six));
    }

    // ---- marks -----------------------------------------------------------------------------------------------------------

    [Fact]
    public void An_arrow_needs_both_ends_inside_and_a_highlight_keeps_what_is_inside()
    {
      var marks = new JsonArray(
        new JsonObject { ["t"] = "arrow", ["from"] = G(1).ToString(), ["to"] = G(2).ToString(), ["label"] = "1 item" },
        new JsonObject { ["t"] = "arrow", ["from"] = G(1).ToString(), ["to"] = G(15).ToString() },
        new JsonObject { ["t"] = "arrow", ["from"] = G(12).ToString(), ["to"] = G(1).ToString() },
        new JsonObject { ["t"] = "highlight", ["ids"] = Ids(1, 12) },
        new JsonObject { ["t"] = "highlight", ["ids"] = Ids(15) });
      var left = JsonNode.Parse(WalkScope.Within(View(Step("x", new[] { 1 }, marks)), Six))["steps"][0]["marks"].AsArray();

      Assert.Equal(2, left.Count);
      Assert.Equal(G(2).ToString(), left[0]["to"].GetValue<string>());
      Assert.Equal(new[] { G(1) }, WalkOrder.IdsOf(left[1]));
    }

    [Fact]
    public void A_step_left_with_no_marks_has_no_marks_field()
    {
      var marks = new JsonArray(new JsonObject { ["t"] = "arrow", ["from"] = G(1).ToString(), ["to"] = G(15).ToString() });
      var step = JsonNode.Parse(WalkScope.Within(View(Step("x", new[] { 1 }, marks)), Six))["steps"][0].AsObject();
      Assert.False(step.ContainsKey("marks"));
    }

    // ---- the sentence ----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("feeds `Vase G-code` (15).", "feeds `Vase G-code` (15), outside this selection.")]
    [InlineData("`Division` (12) takes it.", "`Division` (12), outside this selection, takes it.")]
    [InlineData("feeds `Division` (12), then ends.", "feeds `Division` (12), outside this selection, then ends.")]
    [InlineData("feeds `Vase G-code` (15)", "feeds `Vase G-code` (15), outside this selection")]
    [InlineData("`Height` (1) feeds `Division` (12).", "`Height` (1) feeds `Division` (12), outside this selection.")]
    public void A_neighbours_id_gets_the_boundary_form(string text, string expected) =>
      Assert.Equal(expected, WalkScope.MarkOutside(text, Outside));

    [Theory]
    [InlineData("`Height` (1) and `Radius` (2).")]             // the selection's own
    [InlineData("A tree of (99) lists.")]                       // a number that is no neighbour's
    [InlineData("feeds `Vase G-code` (15), outside this selection.")]   // already in the form, so it can be read twice
    [InlineData("feeds `Vase G-code` (15), outside this selection and ends.")]   // left word for word, however it goes on
    [InlineData("")]
    public void Anything_else_is_left_alone(string text) => Assert.Equal(text, WalkScope.MarkOutside(text, Outside));

    [Fact]
    public void A_sentence_may_hold_a_neighbours_id_only_in_the_boundary_form()
    {
      var marked = new[] { "`Height` (1) feeds `Division` (12) and `Layer Height` (13) then `Vase G-code` (15)." }.Select(t => WalkScope.MarkOutside(t, Outside)).Single();
      foreach (var id in Outside)
        Assert.Matches(new Regex(@"\(" + id + @"\), outside this selection"), marked);
      Assert.Contains("(1) feeds", marked);
      Assert.Equal(marked, WalkScope.MarkOutside(marked, Outside));
    }

    // ---- the nickname, from the sixteenth gate: "(9), outside this selection mul" ----------------------------------------------------

    private static readonly Dictionary<int, string> Named = new Dictionary<int, string> { [9] = "mul", [12] = "Vase G-code", [15] = null };

    [Theory]
    [InlineData("goes to the A input of the Multiplication (9) `mul`.", "goes to the A input of the Multiplication `mul` (9), outside this selection.")]    // the gate's own sentence
    [InlineData("goes to (9) `mul`", "goes to `mul` (9), outside this selection")]                                                                             // at the end
    [InlineData("goes to (9) mul.", "goes to mul (9), outside this selection.")]                                                                               // no code marks
    [InlineData("goes to (9) **mul**, which adds.", "goes to **mul** (9), outside this selection, which adds.")]                                               // bold
    [InlineData("the (9) `mul` then sums it.", "the `mul` (9), outside this selection, then sums it.")]                                                        // the sentence goes on
    [InlineData("to (12) `Vase G-code`.", "to `Vase G-code` (12), outside this selection.")]                                                                    // a name of two words
    [InlineData("to (9) MUL.", "to MUL (9), outside this selection.")]                                                                                          // as it was written
    public void A_nickname_after_the_id_moves_before_it(string text, string expected) =>
      Assert.Equal(expected, WalkScope.MarkOutside(text, Named));

    [Theory]
    [InlineData("goes to `mul` (9).", "goes to `mul` (9), outside this selection.")]
    [InlineData("the `mul` (9) sums it.", "the `mul` (9), outside this selection, sums it.")]
    [InlineData("goes to mul (9).", "goes to mul (9), outside this selection.")]
    public void A_nickname_before_the_id_stays_there(string text, string expected) =>
      Assert.Equal(expected, WalkScope.MarkOutside(text, Named));

    [Fact]
    public void A_nickname_on_both_sides_of_the_id_is_said_once() =>
      Assert.Equal("to `mul` (9), outside this selection.", WalkScope.MarkOutside("to `mul` (9) `mul`.", Named));

    [Fact]
    public void A_sentence_already_marked_with_the_nickname_after_is_put_right() =>
      Assert.Equal("to `mul` (9), outside this selection.", WalkScope.MarkOutside("to (9), outside this selection `mul`.", Named));

    [Theory]
    [InlineData("goes to (9) multiplied.", "goes to (9), outside this selection, multiplied.")]    // "mul" is not the start of another word
    [InlineData("goes to (9) `sum`.", "goes to (9), outside this selection `sum`.")]               // another name is not this one's
    [InlineData("goes to (15) `mul`.", "goes to (15), outside this selection `mul`.")]             // a neighbour with no nickname
    [InlineData("the `mul` (1) `mul`.", "the `mul` (1) `mul`.")]                                   // the selection's own id is left alone
    public void Only_this_neighbours_own_nickname_moves(string text, string expected) =>
      Assert.Equal(expected, WalkScope.MarkOutside(text, Named));

    [Fact]
    public void With_a_nickname_it_can_still_be_read_twice()
    {
      var once = WalkScope.MarkOutside("the Multiplication (9) `mul` and the `Vase G-code` (12).", Named);
      Assert.Equal("the Multiplication `mul` (9), outside this selection, and the `Vase G-code` (12), outside this selection.", once);
      Assert.Equal(once, WalkScope.MarkOutside(once, Named));
    }

    // ---- Brief EX: "outside this selection, outside this selection", the sweep's Walk summary and the twentieth's observation 2 ----------------
    // Rhino 2's capture (its measurement of 4 November) did not double in two runs, but it shows the model writing the
    // words itself with no comma, after a name: "then passes the result on to a Division outside this selection." Written so after an id,
    // the mark added its own phrase in front of the model's.

    [Theory]
    [InlineData("then passes the result on to a Division (12) outside this selection.", "then passes the result on to a Division (12), outside this selection.")]   // (a) the captured words, after an id
    [InlineData("feeds `Vase G-code` (15) outside this selection, then ends.", "feeds `Vase G-code` (15), outside this selection, then ends.")]                   // (a) and the sentence goes on
    [InlineData("feeds `Vase G-code` (15) outside this selection", "feeds `Vase G-code` (15), outside this selection")]                                           // (a) at the end
    [InlineData("feeds (15), Outside this selection.", "feeds (15), outside this selection.")]                                                                  // a capital
    [InlineData("feeds (15), outside the selection.", "feeds (15), outside this selection.")]                                                                   // "the selection"
    [InlineData("feeds (15),\u00a0outside this selection.", "feeds (15), outside this selection.")]                                                              // a no-break space
    [InlineData("feeds (15)  outside this selection.", "feeds (15), outside this selection.")]                                                                  // two spaces, no comma
    public void The_models_own_boundary_words_after_an_id_are_said_once(string text, string expected)
    {
      Assert.Equal(expected, WalkScope.Marked(text, OutsideNamed));   // the mark alone, with no backstop under it
      Assert.Equal(expected, WalkScope.MarkOutside(text, Outside));
      Assert.Equal(expected, WalkScope.MarkOutside(expected, Outside));
    }

    private static readonly IReadOnlyDictionary<int, string> OutsideNamed = Outside.ToDictionary(id => id, id => (string)null);

    [Theory]
    [InlineData("goes to (9) `mul`, outside this selection.", "goes to `mul` (9), outside this selection.")]                       // (b) the nickname between the id and the phrase
    [InlineData("goes to (9) `mul` outside this selection, then sums.", "goes to `mul` (9), outside this selection, then sums.")]   // (b) and no comma
    [InlineData("goes to (12) `Vase G-code`, outside this selection.", "goes to `Vase G-code` (12), outside this selection.")]     // (b) a name of two words
    public void A_nickname_between_the_id_and_the_models_phrase_moves_and_the_phrase_is_said_once(string text, string expected)
    {
      Assert.Equal(expected, WalkScope.Marked(text, Named));   // the mark alone, with no backstop under it
      Assert.Equal(expected, WalkScope.MarkOutside(text, Named));
      Assert.Equal(expected, WalkScope.MarkOutside(expected, Named));
    }

    /// <summary>The backstop: whatever the model writes, the plugin never shows the phrase twice running (the WP-0 law: a deterministic failure class the plugin can compute, it computes).</summary>
    [Theory]
    [InlineData("feeds `Vase G-code` (15), outside this selection, outside this selection, then ends.", "feeds `Vase G-code` (15), outside this selection, then ends.")]   // the sweep's observation, already doubled
    [InlineData("feeds (15), outside this selection outside this selection.", "feeds (15), outside this selection.")]
    [InlineData("a Division outside this selection, outside this selection.", "a Division outside this selection.")]                                                  // no id at all
    [InlineData("feeds (15), outside this selection, Outside the selection, outside this selection.", "feeds (15), outside this selection.")]
    public void The_phrase_twice_running_is_read_once(string text, string expected)
    {
      Assert.Equal(expected, WalkScope.MarkOutside(text, Outside));
      Assert.Equal(expected, WalkScope.MarkOutside(text, Named));
    }

    [Theory]
    [InlineData("feeds (15), outside this selection, and (12), outside this selection.")]   // two neighbours, each said once
    [InlineData("outside this selection lies the rest.")]                                     // the words once, with no id
    [InlineData("the selections outside this selection.")]                                    // "selections" is another word
    public void The_phrase_said_once_per_neighbour_is_left_alone(string text) => Assert.Equal(text, WalkScope.MarkOutside(text, Outside));

    // ---- Brief EX addendum, the twenty-fifth's gate row 1: the mark at the id and the model's phrase later in the same sentence ------------
    // Rhino 2's capture (the twenty-fifth build's gate, row 1, the fourth Step by step run) rendered step 5 as "... feeds the Division (11),
    // outside this selection, on its A input, outside this selection." Read back through the mark, the model wrote the first row below.

    private static readonly Dictionary<int, string> Gate25 = new Dictionary<int, string> { [11] = "div", [14] = null };

    [Theory]
    [InlineData("The final value from Subtraction (10) feeds the Division (11) on its A input, outside this selection. The whole chain stays a single branch with a single item throughout, so no tree restructuring occurs.",
                "The final value from Subtraction (10) feeds the Division (11), outside this selection, on its A input. The whole chain stays a single branch with a single item throughout, so no tree restructuring occurs.")]   // the captured step
    [InlineData("feeds (11) on its A input outside this selection; then ends.", "feeds (11), outside this selection, on its A input; then ends.")]                 // no comma, and a semicolon ends the sentence
    [InlineData("feeds (11) `div` on its A input, outside this selection.", "feeds `div` (11), outside this selection, on its A input.")]                         // the nickname after the id
    [InlineData("feeds (11) at 0.5 on A, outside this selection.", "feeds (11), outside this selection, at 0.5 on A.")]                                         // a decimal point does not end it
    [InlineData("feeds (11), outside this selection, on its A input, outside this selection.", "feeds (11), outside this selection, on its A input.")]           // the id already in the form
    public void The_models_phrase_later_in_a_marked_sentence_is_dropped(string text, string expected)
    {
      Assert.Equal(expected, WalkScope.Marked(text, Gate25));   // the mark alone: the backstop reads only the words twice running
      Assert.Equal(expected, WalkScope.MarkOutside(text, Gate25));
      Assert.Equal(expected, WalkScope.MarkOutside(expected, Gate25));
    }

    /// <summary>A phrase after another id is that id's, and is taken there.</summary>
    [Fact]
    public void The_drop_stops_at_the_next_id()
    {
      const string text = "(11) on its A input, outside this selection, and the Panel (14) outside this selection";
      const string expected = "(11), outside this selection, on its A input, and the Panel (14), outside this selection";
      Assert.Equal(expected, WalkScope.Marked(text, Gate25));
      Assert.Equal(expected, WalkScope.MarkOutside(text, Gate25));
      Assert.Equal(expected, WalkScope.MarkOutside(expected, Gate25));
    }

    [Theory]
    [InlineData("Computes (s1 + s2) \u00d7 (s3 + s4) \u2212 s5 from five sliders, then passes the result on to a Division outside this selection.",
                "Computes (s1 + s2) \u00d7 (s3 + s4) \u2212 s5 from five sliders, then passes the result on to a Division outside this selection.")]   // the capture's summary: no id, no mark
    [InlineData("feeds the Division (11) on its A input. It then passes on to a Panel outside this selection.",
                "feeds the Division (11), outside this selection, on its A input. It then passes on to a Panel outside this selection.")]            // the next sentence is not the marked one
    public void A_sentence_with_no_mark_written_in_it_keeps_the_models_words(string text, string expected)
    {
      Assert.Equal(expected, WalkScope.Marked(text, Gate25));
      Assert.Equal(expected, WalkScope.MarkOutside(text, Gate25));
    }

    /// <summary>An id inside a nickname the mark has already moved is past where it writes, so it is passed over: before, the copy ran backwards and threw, and the catch around the Walk lost every step.</summary>
    [Fact]
    public void An_id_inside_a_moved_nickname_is_passed_over() =>
      Assert.Equal("goes to `f (12)` (9), outside this selection.", WalkScope.MarkOutside("goes to (9) `f (12)`.", new Dictionary<int, string> { [9] = "f (12)", [12] = null }));

    /// <summary>A Release build keeps no raw reply (LlmTrace is DEBUG-only, and the mark rewrites in place), so a Debug gate can read the text the model wrote before the plugin marked it.</summary>
    [Fact]
    public void A_Debug_build_keeps_the_walks_text_before_it_is_marked()
    {
      var actions = Repo("Dwight", "LlmActions.cs");
      var record = actions.IndexOf("LlmTrace.RecordWalkUnmarked(rawJson);", StringComparison.Ordinal);
      Assert.True(record > 0);
      Assert.True(record < actions.IndexOf("summary = WalkScope.MarkOutside(rootNode?[\"summary\"]", StringComparison.Ordinal));
      var trace = Repo("Dwight", "Llm", "LlmTrace.cs");
      Assert.Contains("[System.Diagnostics.Conditional(\"DEBUG\")]\n    public static void RecordWalkUnmarked(string rawJson)", trace.Replace("\r\n", "\n"));
    }

    [Fact]
    public void With_nothing_to_mark_the_text_comes_back_as_it_came()
    {
      const string text = "`Height` (1) and `Radius` (2).";
      Assert.Same(text, WalkScope.MarkOutside(text, Named));
      Assert.Same(text, WalkScope.MarkOutside(text, (IReadOnlyDictionary<int, string>)null));
    }

    // ---- the summary and Replay count the selection --------------------------------------------------------------------------

    private static List<TimeFact> FactsFor(IEnumerable<Guid> ids) => ids.Select(id => new TimeFact
    {
      Guid = id,
      Label = id == G(15) ? "Vase G-code (15)" : "Component " + id.ToString().Substring(7, 1),
      Recorded = true,
      Ms = id == G(15) ? 1644 : id == G(2) ? 200 : 100,
    }).ToList();

    [Fact]
    public void The_summarys_total_is_the_sixs_and_its_slowest_is_one_of_the_six()
    {
      var times = new WalkTimes(FactsFor(WalkScope.Timed(Six, StepsOf(TheStop()))));   // steps as the old row held them, outside ids and all
      Assert.Equal(700, times.TotalMs);
      Assert.Equal(G(2), times.Slowest.Guid);
      Assert.DoesNotContain("Vase G-code", times.SummaryLine());
      Assert.Contains("This selection's components last took", times.SummaryLine());
    }

    [Fact]
    public void A_row_with_no_selection_recorded_times_what_its_steps_hold()
    {
      var ids = WalkScope.Timed(null, new[] { (IReadOnlyList<Guid>)new[] { G(1), G(2) }, new[] { G(2), G(3) } }).ToList();
      Assert.Equal(new[] { G(1), G(2), G(3) }, ids);
      Assert.Empty(WalkScope.Timed(null, null));
    }

    // ---- Learn ---------------------------------------------------------------------------------------------------------------

    /// <summary>The readings hold an outside component on purpose, so this proves the steps keep a question off it, whatever else is read.</summary>
    [Fact]
    public void A_learn_question_never_asks_about_a_component_outside_the_selection()
    {
      var readings = new Dictionary<Guid, IReadOnlyList<PortReading>>
      {
        [G(12)] = new[] { new PortReading { Component = G(12), Nick = "Division", Port = "R", Value = "4" } },
        [G(15)] = new[] { new PortReading { Component = G(15), Nick = "Vase G-code", Port = "G", Value = "7" } },
        [G(4)] = new[] { new PortReading { Component = G(4), Nick = "Wave Depth", Port = "N", Value = "2" } },
      };
      var (steps, sentences) = TeachingSteps.Of(WalkScope.Within(TheStop(), Six));
      var facts = TeachingChooser.Choose(steps, readings);

      var fact = Assert.Single(facts);
      Assert.Equal(G(4), fact.Reading.Component);
      Assert.Equal(steps.Count, sentences.Count);

      // the control: the same readings on the steps as they stood do reach an outside component
      Assert.Contains(TeachingChooser.Choose(StepsOf(TheStop()), readings), f => f.Reading.Component == G(12));
    }

    // ---- the order still reads the wires -----------------------------------------------------------------------------------

    [Fact]
    public void The_order_is_the_wires_on_the_held_steps()
    {
      var view = WalkScope.Within(View(Step("second", new[] { 2, 12 }), Step("first", new[] { 1 })), Six);
      var sorted = JsonNode.Parse(WalkOrder.SortView(view, new[] { (G(1), G(12)), (G(12), G(2)) }))["steps"].AsArray();
      Assert.Equal("first", sorted[0]["text"].GetValue<string>());   // through a neighbour is still the flow, and nothing of the neighbour is lit
      Assert.Equal(new[] { G(2) }, WalkOrder.IdsOf(sorted[1]));
    }

    // ---- the doors, read from their source, because they are GH-bound -----------------------------------------------------------

    private static string Repo(params string[] parts)
    {
      var dir = AppContext.BaseDirectory;
      while (dir != null && !File.Exists(Path.Combine(dir, "Dwight.sln"))) dir = Path.GetDirectoryName(dir);
      Assert.NotNull(dir);
      return File.ReadAllText(Path.Combine(new[] { dir }.Concat(parts).ToArray()));
    }

    [Fact]
    public void A_new_run_is_held_before_it_is_recorded_and_a_replayed_row_at_the_panels_door()
    {
      var actions = Repo("Dwight", "LlmActions.cs");
      Assert.Contains("return WalkScope.Within(view.ToJsonString(), graph.Selection.Select(n => n.Guid).ToList());", actions);
      Assert.True(actions.IndexOf("private static string TranslateSteps(", StringComparison.Ordinal) < actions.IndexOf("return WalkScope.Within(view.ToJsonString()", StringComparison.Ordinal));
      Assert.Contains("WalkOrder.SortView(TranslateSteps(graph, res.Json), WireGuids(graph))", actions);
      Assert.Contains("public void SetSteps(string stepsJson) { stepsJson = WalkScope.Within(stepsJson, _sourceGuids); _stepsJson = stepsJson; _stepGuids = ParseSteps(stepsJson);", Repo("Dwight", "UI", "WebPanel.cs"));
    }

    [Fact]
    public void The_sentences_are_marked_with_every_node_outside_the_selection()
    {
      var s = Repo("Dwight", "LlmActions.cs");
      Assert.Contains("graph.Nodes.Where(n => !n.InSelection).ToDictionary(n => n.Id, n => n.Nick)", s);
      Assert.Contains("WalkScope.MarkOutside(o[\"text\"]?.GetValue<string>() ?? \"\", outside)", s);
      Assert.Contains("WalkScope.MarkOutside(rootNode?[\"summary\"]?.GetValue<string>(), outside)", s);
    }

    [Fact]
    public void The_times_are_read_from_the_selection()
    {
      var s = Repo("Dwight", "UI", "WalkSession.cs");
      Assert.Contains("var ids = WalkScope.Timed(_state.Source, _panel?.WalkSteps);", s);
      Assert.DoesNotContain("ids.UnionWith(step)", s);
    }

    /// <summary>What the model is told, held by the tests above on the plugin's side (#315).</summary>
    [Fact]
    public void The_prompt_says_what_the_plugin_holds()
    {
      var s = Repo("Dwight", "Prompts", "Prompts.cs");
      Assert.Contains("the node ids from the payload's \"\"focus\"\" that step covers, and no others", s);
      Assert.Contains("referencing node ids from \"\"focus\"\" only", s);
      Assert.Contains("(15), outside this selection\"\"; it is never given a step, an id or a mark.", s);
      Assert.Contains(WalkScope.OutsidePhrase.TrimStart(',', ' '), s);
    }
  }
}
