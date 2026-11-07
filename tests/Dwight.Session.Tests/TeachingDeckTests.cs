using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using Dwight.UI;

namespace Dwight.Session.Tests
{
  /// <summary>
  /// Brief EI, Learn as a deck of quiz cards, and Brief EO, the deck teaching the why; the GH-free half: which cards the plugin deals and where each truth comes from (the
  /// plugin's, except a why-card's, which says so), the grader on every kind, the judge's verdict on an answer in the person's own words, Try again, the views the shells
  /// draw (rendered into the harness's fixture file from the product's own writer), the result's reading, the names on a card as anchors, the record a reopened run reads
  /// back, and the checks on the model's reply. Each law has a mutation that turns a test red (see the PR).
  /// </summary>
  public class TeachingDeckTests
  {
    private static Guid G(int n) => new Guid(n, 0, 0, new byte[8]);

    private static DeckNode N(int n, string type, string nick = null) => new DeckNode { Guid = G(n), Id = 10 + n, Type = type, Nick = nick };

    private static DeckWire W(int from, string fromPort, int to, string toPort, int items = 1) => new DeckWire { From = G(from), FromPort = fromPort, To = G(to), ToPort = toPort, Items = items };

    private static PortReading R(int n, string type, string port, string value, ReadingKind kind = ReadingKind.Number) =>
      new PortReading { Component = G(n), Type = type, Port = port, Kind = kind, Value = value };

    private static IReadOnlyList<IReadOnlyList<Guid>> Steps(params int[][] steps) => steps.Select(s => (IReadOnlyList<Guid>)s.Select(G).ToList()).ToList();

    /// <summary>
    /// Six components in four steps: a slider (12), a Divide Curve that sends 12 points to a List Length and a Move, an Area whose centroid feeds the Move and the Loft,
    /// a Move whose 12 geometries go to the Loft. The List Length reads 3.1416 somewhere.
    /// </summary>
    private static DeckInput Input() => new DeckInput
    {
      Nodes = new[] { N(1, "Number Slider", "U count"), N(2, "Divide Curve"), N(3, "List Length"), N(4, "Area"), N(5, "Move"), N(6, "Loft") },
      Wires = new[] { W(1, "N", 2, "Count"), W(2, "Points", 3, "List", 12), W(2, "Points", 5, "Geometry", 12), W(4, "Centroid", 5, "Motion"), W(5, "Geometry", 6, "Curves", 12), W(4, "Centroid", 6, "Options") },
      Readings = new Dictionary<Guid, IReadOnlyList<PortReading>>
      {
        [G(1)] = new[] { R(1, "Number Slider", "N", "12") },
        [G(3)] = new[] { R(3, "List Length", "L", "3.1416") },
      },
    };

    private static IReadOnlyList<IReadOnlyList<Guid>> TheSteps() => Steps(new[] { 1 }, new[] { 2 }, new[] { 3, 4 }, new[] { 5, 6 });

    // ---- which cards the plugin deals (Brief EO unit 1) ---------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("12", "Number", "Enter")]
    [InlineData("0.25", "Number", "Enter")]
    [InlineData("1200", "Number", "Enter")]
    [InlineData("-3.5", "Number", "Enter")]
    [InlineData("3.1416", "Number", "ChooseOne")]
    [InlineData("123456", "Number", "ChooseOne")]
    [InlineData("96", "ListCount", "Enter")]
    public void A_reading_is_typed_when_it_is_a_count_or_four_digits_at_most_and_chosen_from_options_otherwise(string value, string kind, string form)
    {
      Assert.Equal(Enum.Parse<DeckForm>(form), DeckPlanner.FormFor(new PortReading { Value = value, Kind = Enum.Parse<ReadingKind>(kind) }));
    }

    /// <summary>Tharun's ruling from his tour: the person touches the canvas once. One card read from the live solve, the first value in the Walk's order, and one card about
    /// the wiring, whatever else the selection offers.</summary>
    [Fact]
    public void The_plugin_deals_one_card_read_from_the_live_solve_and_one_about_the_wiring()
    {
      var facts = DeckPlanner.Plan(Input(), TheSteps());
      Assert.Equal(2, facts.Count);
      var read = facts.Single(f => f.Kind == DeckKind.Value || f.Kind == DeckKind.Count);
      Assert.Equal(DeckKind.Value, read.Kind);
      Assert.Equal("12", read.Truth);
      Assert.Equal(DeckForm.Enter, read.Form);
      Assert.Equal(G(1), read.Cover);                                      // the value's component is hatched while the card is open
      var wiring = facts.Single(f => f.Kind == DeckKind.Which || f.Kind == DeckKind.Feeds);
      Assert.Equal(DeckKind.Which, wiring.Kind);                           // the slider's N goes to the Divide Curve, the first wire the Walk's order offers
      Assert.Equal("Divide Curve (12)", wiring.Options[wiring.TrueSet.Single()]);
      Assert.Equal(new[] { 0, 0 }, facts.Select(f => f.Step).ToArray());
      Assert.Equal(new[] { DeckKind.Value, DeckKind.Which }, facts.Select(f => f.Kind).ToArray());   // within a step, what a port holds before where its wires go
    }

    [Fact]
    public void With_no_value_read_the_one_card_from_the_live_solve_is_a_count_on_a_wire()
    {
      var input = Input();
      input.Readings = new Dictionary<Guid, IReadOnlyList<PortReading>>();
      var read = DeckPlanner.Plan(input, TheSteps()).Single(f => f.Kind == DeckKind.Value || f.Kind == DeckKind.Count);
      Assert.Equal(DeckKind.Count, read.Kind);
      Assert.Equal("12", read.Truth);
      Assert.Equal("Points", read.Port);
      Assert.Equal(1, read.Step);
    }

    [Fact]
    public void A_deck_never_deals_two_cards_from_the_live_solve_or_two_about_the_wiring()
    {
      // Twelve steps of two wired sliders each: far more to ask than one of each.
      var nodes = Enumerable.Range(1, 24).Select(i => N(i, "Number Slider")).ToList();
      var wires = Enumerable.Range(0, 12).Select(k => W(2 * k + 1, "N", 2 * k + 2, "X", 5)).ToList();
      var readings = nodes.ToDictionary(n => n.Guid, n => (IReadOnlyList<PortReading>)new[] { R(n.Id - 10, "Number Slider", "N", "7") });
      var steps = Steps(Enumerable.Range(0, 12).Select(k => new[] { 2 * k + 1, 2 * k + 2 }).ToArray());
      var facts = DeckPlanner.Plan(new DeckInput { Nodes = nodes, Wires = wires, Readings = readings }, steps);
      Assert.Single(facts, f => f.Kind == DeckKind.Value || f.Kind == DeckKind.Count);
      Assert.Single(facts, f => f.Kind == DeckKind.Which || f.Kind == DeckKind.Feeds);
      Assert.DoesNotContain(facts, f => f.Kind == DeckKind.Concept);       // the concept card is retired: the why-cards are the model's reading
      Assert.Equal(DeckPlanner.MaxWhy, DeckPlanner.WhyWanted(facts.Count));
      Assert.Equal(DeckPlanner.MaxCards, facts.Count + DeckPlanner.WhyWanted(facts.Count));   // seven cards at most
    }

    [Fact]
    public void The_why_questions_asked_for_fill_the_seven_and_are_five_at_most()
    {
      Assert.Equal(5, DeckPlanner.WhyWanted(0));
      Assert.Equal(5, DeckPlanner.WhyWanted(2));
      Assert.Equal(4, DeckPlanner.WhyWanted(3));
      Assert.Equal(0, DeckPlanner.WhyWanted(9));
    }

    /// <summary>A feeds card is the wiring card when it is the first the Walk's order offers: two wires into one port, so no which question has a single answer there.</summary>
    [Fact]
    public void A_feeds_card_offers_its_sources_exactly_and_a_component_that_is_not_one()
    {
      var input = new DeckInput
      {
        Nodes = new[] { N(1, "Number Slider", "a"), N(2, "Number Slider", "b"), N(3, "Mass Addition", "sum"), N(4, "Panel", "note") },
        Wires = new[] { W(1, "N", 3, "Input"), W(2, "N", 3, "Input") },
      };
      var facts = DeckPlanner.Plan(input, Steps(new[] { 3 }, new[] { 1 }, new[] { 2 }, new[] { 4 }));
      var feeds = facts.Single();
      Assert.Equal(DeckKind.Feeds, feeds.Kind);
      Assert.Equal(DeckForm.ChooseAll, feeds.Form);
      var labels = new HashSet<string>(input.Nodes.Select(n => n.Label));
      Assert.All(feeds.Options, o => Assert.Contains(o, labels));
      Assert.DoesNotContain("sum (13)", feeds.Options);                     // never the component asked about
      Assert.Equal(new[] { "a (11)", "b (12)" }, feeds.TrueSet.Select(t => feeds.Options[t]).OrderBy(x => x).ToArray());
      Assert.True(feeds.Options.Count > feeds.TrueSet.Count);
    }

    [Fact]
    public void One_component_or_none_plans_no_deck()
    {
      Assert.Empty(DeckPlanner.Plan(new DeckInput { Nodes = new[] { N(1, "x") } }, Steps(new[] { 1 })));
      Assert.Empty(DeckPlanner.Plan(null, TheSteps()));
    }

    // ---- the grader ------------------------------------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("12", "12", true)]
    [InlineData("12.0", "12", true)]
    [InlineData("12.4", "12", false)]      // an integer exactly
    [InlineData("3.50", "3.5", true)]      // a decimal with trailing zeros is the same number
    [InlineData("3.54", "3.5", true)]      // and rounds alike at the reading's one place
    [InlineData("3.56", "3.5", false)]
    [InlineData("3.14159", "3.1416", true)]
    [InlineData("3.14", "3.1416", false)]
    [InlineData(" 96 ", "96", true)]
    [InlineData("-3", "-3", true)]
    public void A_typed_answer_is_a_number_compared_at_the_readings_own_precision(string entered, string truth, bool right)
    {
      Assert.Equal(right, DeckGrader.Enter(entered, truth));
    }

    [Theory]
    [InlineData("twelve")]
    [InlineData("")]
    [InlineData("12 points")]
    [InlineData("1,200")]
    public void Something_that_is_not_a_number_is_not_an_answer(string entered)
    {
      Assert.Null(DeckGrader.Enter(entered, "12"));
    }

    [Fact]
    public void Choose_one_is_the_index_and_choose_all_is_the_exact_set()
    {
      Assert.True(DeckGrader.ChooseOne(2, new[] { 2 }));
      Assert.False(DeckGrader.ChooseOne(1, new[] { 2 }));
      Assert.True(DeckGrader.ChooseAll(new[] { 3, 0 }, new[] { 0, 3 }));
      Assert.False(DeckGrader.ChooseAll(new[] { 0, 1, 3 }, new[] { 0, 3 }));   // a superset is wrong
      Assert.False(DeckGrader.ChooseAll(new[] { 0 }, new[] { 0, 3 }));         // and so is a subset
    }

    /// <summary>Brief EO unit 2: the judge's word maps to exactly three verdicts, and anything else is no verdict, which the card reads as could not judge.</summary>
    [Theory]
    [InlineData("right", "Right")]
    [InlineData(" Partly ", "Partly")]
    [InlineData("WRONG", "Wrong")]
    [InlineData("correct", "None")]
    [InlineData("", "None")]
    [InlineData(null, "None")]
    public void The_judges_word_is_right_partly_or_wrong_and_anything_else_is_no_verdict(string word, string verdict)
    {
      Assert.Equal(Enum.Parse<JudgeVerdict>(verdict), DeckGrader.Verdict(word));
    }

    // ---- the deck --------------------------------------------------------------------------------------------------------------------------------------------------

    private static DeckNode[] Nodes() => new[] { N(1, "Number Slider", "U count"), N(2, "Divide Curve"), N(3, "List Length"), N(4, "Area"), N(5, "Move"), N(6, "Loft") };

    private static DeckCard Typed(string value = "12") => new DeckCard
    {
      Kind = DeckKind.Value, Form = DeckForm.Enter, Step = 0, Question = "What does U count hold?", Hint = "Read the slider.", Value = value, Lights = new List<Guid> { G(1) }, Cover = G(1),
    };

    private static DeckCard One() => new DeckCard
    {
      Kind = DeckKind.Which, Form = DeckForm.ChooseOne, Step = 1, Question = "Which component takes the points?", Hint = "Follow the Points wire.",
      Options = new List<string> { "List Length (13)", "Area (14)", "Move (15)" }, TrueSet = new List<int> { 2 }, Lights = new List<Guid> { G(2), G(3), G(4), G(5) },
    };

    private static DeckCard All() => new DeckCard
    {
      Kind = DeckKind.Feeds, Form = DeckForm.ChooseAll, Step = 3, Question = "Which of these feed the Loft?", Hint = "Look at its inputs.",
      Options = new List<string> { "Area (14)", "Move (15)", "List Length (13)" }, TrueSet = new List<int> { 0, 1 }, Lights = new List<Guid> { G(6), G(4), G(5), G(3) },
    };

    private static DeckCard Why(string topic = "tree") => new DeckCard
    {
      Kind = DeckKind.Why, Form = DeckForm.ChooseOne, Step = 3, Question = "Why is Move (15) grafted before Loft (16)?", Hint = "Think about what each curve needs.",
      Explain = "Loft (16) lofts each branch on its own. Grafting puts every moved curve on its own branch, so each is lofted on its own.", Topic = topic,
      Options = new List<string> { "So each curve is lofted on its own", "To make it faster", "To bake it", "To colour it" }, TrueSet = new List<int> { 0 }, Dwight = true,
      Lights = new List<Guid> { G(5), G(6) },
    };

    private static DeckState Deck(string id = "deck1") => new DeckState(new[] { Typed(), One(), All(), Why() }, id, Nodes());

    [Fact]
    public void A_card_takes_one_answer_and_turns()
    {
      var d = Deck();
      Assert.Equal(DeckOutcome.Right, d.Answer(0, "v:12"));
      Assert.Equal(DeckOutcome.None, d.Answer(0, "v:13"));   // answered once
      Assert.Equal(DeckOutcome.Wrong, d.Answer(1, "o:0"));
      Assert.Equal(DeckOutcome.None, d.Answer(1, "o:2"));
      Assert.Equal(DeckOutcome.Wrong, d.Answer(2, "s:0,1,2"));   // a superset
      Assert.Equal(1, d.Right);
      Assert.False(d.AllAnswered);
    }

    [Fact]
    public void A_typed_word_turns_nothing_and_the_card_says_to_type_a_number()
    {
      var d = Deck();
      Assert.Equal(DeckOutcome.NotANumber, d.Answer(0, "v:twelve"));
      Assert.False(d.IsAnswered(0));
      Assert.Equal(DeckText.NotANumber, d.CardView(0)["line"]!.GetValue<string>());
      Assert.Equal(DeckOutcome.Right, d.Answer(0, "v:12"));
      Assert.False(d.CardView(0).ContainsKey("line"));
    }

    [Theory]
    [InlineData(0, "o:0")]     // a choose-one answer to a typed card
    [InlineData(1, "s:2")]     // a set to a choose-one card
    [InlineData(2, "o:0")]
    [InlineData(1, "o:9")]     // past the options
    [InlineData(2, "s:")]      // no tick
    [InlineData(1, "x")]
    [InlineData(9, "o:0")]
    [InlineData(1, "t:Because the points go to the Move")]   // own words answer a why-card only
    [InlineData(3, "t:")]                                    // and never nothing
    [InlineData(3, "t:   ")]
    public void An_answer_in_the_wrong_shape_is_no_answer(int card, string answer)
    {
      Assert.Equal(DeckOutcome.None, Deck().Answer(card, answer));
    }

    // ---- own words and the judge (Brief EO unit 2) -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Own_words_on_a_why_card_wait_for_the_judge_and_the_card_takes_no_other_answer_meanwhile()
    {
      var d = Deck();
      Assert.Equal(DeckOutcome.Judging, d.Answer(3, "t:  So every curve is lofted alone  "));
      Assert.True(d.IsJudging(3));
      Assert.False(d.IsAnswered(3));
      Assert.Equal("So every curve is lofted alone", d.TypedAt(3));
      Assert.Equal(DeckOutcome.None, d.Answer(3, "o:0"));                       // one answer at a time
      Assert.Equal(DeckOutcome.None, d.Answer(3, "t:again"));
      var v = d.CardView(3);
      Assert.True(v["busy"]!.GetValue<bool>());
      Assert.Equal(DeckText.Judging, v["judging"]!["line"]!.GetValue<string>());
      Assert.Equal(DeckText.Stop, v["judging"]!["stop"]!.GetValue<string>());
      Assert.False(v.ContainsKey("own"));
      Assert.False(d.TryAgain());                                               // nothing is reset under a judge at work
      Assert.Equal(DeckOutcome.None, d.Answer(3, "t:" + new string('x', DeckState.MaxTypedChars + 1)));
    }

    [Theory]
    [InlineData("Right", "Right", true, "right")]
    [InlineData("Partly", "Partly", false, "partly")]
    [InlineData("Wrong", "Wrong", false, "wrong")]
    public void The_judges_verdict_turns_the_card_and_only_right_counts(string said, string turned, bool counts, string shown)
    {
      var verdict = Enum.Parse<JudgeVerdict>(said);
      var outcome = Enum.Parse<DeckOutcome>(turned);
      var d = Deck();
      d.Answer(3, "t:So every curve is lofted alone");
      Assert.Equal(outcome, d.Judged(3, verdict, "You named the branches, not why.", "About 0.1 cents, on Claude Haiku 5."));
      Assert.True(d.IsAnswered(3));
      Assert.False(d.IsJudging(3));
      Assert.Equal(counts ? 1 : 0, d.Right);
      var turn = d.CardView(3)["turnedAs"]!.AsObject();
      Assert.Equal(shown, turn["verdict"]!.GetValue<string>());
      Assert.Equal("You named the branches, not why.", turn["judged"]!.GetValue<string>());
      Assert.Equal(DeckText.JudgedBy, turn["judgedBy"]!.GetValue<string>());
      Assert.Equal("About 0.1 cents, on Claude Haiku 5.", turn["cost"]!.GetValue<string>());
      Assert.Equal(DeckText.DwightsReading, turn["mark"]!.GetValue<string>());
      Assert.Equal(verdict == JudgeVerdict.Right ? DeckText.RightStamp : verdict == JudgeVerdict.Partly ? DeckText.PartlyStamp : null, turn["stamp"]?.GetValue<string>());
      Assert.Equal(!counts, turn.ContainsKey("given"));
      Assert.Equal(counts ? "right" : verdict == JudgeVerdict.Partly ? "partly" : "wrong", d.CardView(3)["ticks"]!.AsArray()[3]!.GetValue<string>());
      Assert.Equal(counts ? 0 : 1, d.MissedCards.Count(i => i == 3));            // partly is missed, as wrong is
    }

    [Fact]
    public void A_judge_that_fails_or_is_stopped_leaves_the_card_open_with_its_options_and_no_own_words()
    {
      var d = Deck();
      d.Answer(3, "t:So every curve is lofted alone");
      Assert.Equal(DeckOutcome.None, d.Judged(3, JudgeVerdict.None, null, null));   // no verdict is a judge that failed
      Assert.False(d.IsJudging(3));
      Assert.False(d.IsAnswered(3));
      var v = d.CardView(3);
      Assert.Equal(DeckText.CouldNotJudge, v["line"]!.GetValue<string>());
      Assert.False(v.ContainsKey("own"));
      Assert.False(v.ContainsKey("busy"));
      Assert.Equal(DeckOutcome.None, d.Answer(3, "t:try once more"));          // the options are the way to answer it now
      Assert.Equal(DeckOutcome.Right, d.Answer(3, "o:0"));
      Assert.False(Deck().JudgeFailed(3));                                        // nothing to stop on a card that is not judging
    }

    [Fact]
    public void With_no_fast_model_the_field_says_why_and_own_words_are_no_answer()
    {
      var d = Deck();
      d.JudgeOff = DeckText.NoJudge;
      var v = d.CardView(3);
      Assert.Equal(DeckText.NoJudge, v["ownOff"]!.GetValue<string>());
      Assert.False(v.ContainsKey("own"));
      Assert.Equal(DeckOutcome.None, d.Answer(3, "t:So every curve is lofted alone"));
      var open = Deck().CardView(3)["own"]!.AsObject();
      Assert.Equal(DeckText.OwnWords, open["label"]!.GetValue<string>());
      Assert.Equal(DeckText.OwnPlaceholder, open["placeholder"]!.GetValue<string>());
      Assert.False(Deck().CardView(1).ContainsKey("own"));                       // a card the plugin dealt is answered by its own controls only
    }

    [Fact]
    public void A_card_opens_once_those_before_it_are_answered()
    {
      var d = Deck();
      Assert.True(d.MayOpen(0));
      Assert.False(d.MayOpen(1));
      d.Answer(0, "v:12");
      Assert.True(d.MayOpen(1));
      Assert.True(d.MayOpen(0));      // and back to a turned card
      Assert.False(d.MayOpen(2));
      Assert.Equal(1, d.FirstOpen);
    }

    [Fact]
    public void The_cover_is_over_a_values_component_until_its_card_is_answered()
    {
      var d = Deck();
      Assert.Equal(G(1), d.CoverOf(0));
      Assert.Null(d.CoverOf(1));
      d.Answer(0, "v:11");
      Assert.Null(d.CoverOf(0));
    }

    [Fact]
    public void Try_again_keeps_the_cards_puts_the_options_in_a_new_order_and_follows_the_truth()
    {
      var d = Deck();
      d.Answer(0, "v:12"); d.Answer(1, "o:2"); d.Answer(2, "s:0,1"); d.Answer(3, "o:0");
      var before = d.Cards.Select(c => c.Options.ToList()).ToList();
      var truths = d.Cards.Select(c => c.TrueText).ToList();
      Assert.True(d.TryAgain());
      Assert.Equal(1, d.Attempt);
      Assert.Equal(0, d.Answered);
      for (var i = 0; i < d.Count; i++)
      {
        if (before[i].Count < 2) continue;
        Assert.NotEqual(before[i], d.Cards[i].Options);                                   // a new order
        Assert.Equal(before[i].OrderBy(x => x), d.Cards[i].Options.OrderBy(x => x));      // the same options
        Assert.Equal(truths[i].Split(new[] { ", " }, StringSplitOptions.None).OrderBy(x => x), d.Cards[i].TrueText.Split(new[] { ", " }, StringSplitOptions.None).OrderBy(x => x));   // and the truth followed
      }
      Assert.Equal(DeckOutcome.Right, d.Answer(1, "o:" + d.Cards[1].TrueSet.Single()));
    }

    [Fact]
    public void The_streak_is_the_longest_run_of_right_answers()
    {
      var d = new DeckState(new[] { Typed(), Typed(), Typed(), Typed(), Typed() });
      d.Answer(0, "v:1"); d.Answer(1, "v:12"); d.Answer(2, "v:12"); d.Answer(3, "v:12"); d.Answer(4, "v:1");
      Assert.Equal(3, d.Streak);
      Assert.Equal("3 in a row", d.ResultView()["streak"]!.GetValue<string>());
      var two = new DeckState(new[] { Typed(), Typed(), Typed() });
      two.Answer(0, "v:12"); two.Answer(1, "v:12"); two.Answer(2, "v:1");
      Assert.False(two.ResultView().ContainsKey("streak"));   // two in a row is not said
    }

    // ---- the views -------------------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Card_0_says_the_idea_the_count_and_the_cost_and_names_Dwights_reading()
    {
      var v = Deck().StartView("Divides a curve and lofts the moved points.", 6, 4, "Questions cost one more call, 2.1 cents.");
      Assert.Equal("start", v["view"]!.GetValue<string>());
      Assert.Equal("Divides a curve and lofts the moved points.", v["title"]!.GetValue<string>());
      Assert.Equal("4 questions", v["count"]!.GetValue<string>());
      Assert.Equal(DeckText.Gist(4, 4, true, true), v["gist"]!.GetValue<string>());
      Assert.Contains("The why questions are Dwight's reading; the rest are read from your definition.", v["gist"]!.GetValue<string>());
      Assert.Contains("Every answer is Dwight's reading.", DeckText.Gist(5, 3, true, false));
      Assert.Contains("Every answer is read from your definition.", DeckText.Gist(2, 3, false, true));
      Assert.Equal("A quiz on these 6 components", Deck().StartView("  ", 6, 4, null)["title"]!.GetValue<string>());
      Assert.False(Deck().StartView("t", 6, 4, null).ContainsKey("cost"));
    }

    [Fact]
    public void An_open_card_shows_its_question_and_its_controls_and_no_answer()
    {
      var d = Deck();
      d.Answer(0, "v:12");
      var v = d.CardView(2);
      Assert.Equal("3 of 4", v["of"]!.GetValue<string>());
      Assert.Equal(new[] { "right", "open", "on", "open" }, v["ticks"]!.AsArray().Select(t => t!.GetValue<string>()).ToArray());
      Assert.Equal("all", v["form"]!.GetValue<string>());
      Assert.Equal(DeckText.ChooseAll, v["prompt"]!.GetValue<string>());
      Assert.Equal(DeckText.Check, v["check"]!.GetValue<string>());
      Assert.All(v["options"]!.AsArray(), o => Assert.Equal("open", o!["state"]!.GetValue<string>()));
      Assert.False(v.ContainsKey("turnedAs"));
      Assert.False(v.ContainsKey("next"));
      Assert.False(d.CardView(0).ContainsKey("peek"));   // a turned card has no peek
      Assert.Equal(DeckText.Peek, new DeckState(new[] { Typed() }).CardView(0)["peek"]!.GetValue<string>());
      Assert.False(Deck().CardView(1).ContainsKey("check"));   // choose one answers on the click
    }

    /// <summary>Brief EO unit 3: right is green and wrong red, which the shell draws from the verdict; a wrong turn shows the truth, where it came from and the hint, and on
    /// a why-card the explanation under "Why" at once.</summary>
    [Fact]
    public void A_right_answer_earns_the_stamp_and_a_wrong_one_shows_the_truth_where_it_came_from_the_hint_and_why()
    {
      var d = Deck();
      d.Answer(0, "v:12"); d.Answer(1, "o:0"); d.Answer(2, "s:0,1"); d.Answer(3, "o:2");
      var right = d.CardView(0)["turnedAs"]!.AsObject();
      Assert.Equal("right", right["verdict"]!.GetValue<string>());
      Assert.Equal(DeckText.RightStamp, right["stamp"]!.GetValue<string>());
      Assert.Equal("The answer is 12.", right["answer"]!.GetValue<string>());
      Assert.Equal(DeckText.Read, right["mark"]!.GetValue<string>());
      Assert.False(right.ContainsKey("hint"));
      var wrong = d.CardView(1)["turnedAs"]!.AsObject();
      Assert.Equal("wrong", wrong["verdict"]!.GetValue<string>());
      Assert.False(wrong.ContainsKey("stamp"));
      Assert.Equal("You answered List Length (13).", wrong["given"]!.GetValue<string>());
      Assert.Equal("The answer is Move (15).", wrong["answer"]!.GetValue<string>());
      Assert.Equal("Follow the Points wire.", wrong["hint"]!.GetValue<string>());
      var opts = d.CardView(1)["options"]!.AsArray();
      Assert.Equal("false", opts[0]!["state"]!.GetValue<string>());
      Assert.Equal("true", opts[2]!["state"]!.GetValue<string>());
      Assert.Equal(DeckText.Next, d.CardView(1)["next"]!.GetValue<string>());
      var why = d.CardView(3)["turnedAs"]!["why"]!.AsObject();
      Assert.Equal(DeckText.Why, why["head"]!.GetValue<string>());
      Assert.Equal(Why().Explain, why["text"]!.GetValue<string>());
    }

    [Fact]
    public void A_why_card_is_marked_as_Dwights_reading_and_never_as_read_from_the_definition()
    {
      var d = Deck();
      d.Answer(3, "o:1");
      var turn = d.CardView(3)["turnedAs"]!.AsObject();
      Assert.Equal(DeckText.DwightsReading, turn["mark"]!.GetValue<string>());
      Assert.True(turn["dwight"]!.GetValue<bool>());
      Assert.Equal(DeckText.Score, d.CardView(3)["next"]!.GetValue<string>());   // the last card's arrow opens the score
    }

    /// <summary>Brief EO unit 4, as the architect ruled it: three doors, Understand, Play again and Ask the chat, and Try again and Walk this as two quiet links under them.
    /// A deck read back from history offers neither Play again nor Try again.</summary>
    [Fact]
    public void The_result_has_the_score_a_reading_three_doors_and_two_quiet_links_and_a_reopened_one_cannot_play_again()
    {
      var d = Deck();
      d.Answer(0, "v:12"); d.Answer(1, "o:0"); d.Answer(2, "s:0,1"); d.Answer(3, "o:0");
      d.AgainEstimate = "about 2 cents";
      var r = d.ResultView();
      Assert.Equal("3 of 4", r["stamp"]!.GetValue<string>());
      Assert.Equal(DeckReading.Of(d.Cards, new[] { true, false, true, true }), r["reading"]!.GetValue<string>());
      var missed = r["missed"]!.AsArray();
      Assert.Single(missed);
      Assert.Equal(1, missed[0]!["k"]!.GetValue<int>());
      Assert.Equal("Question 2: the answer is Move (15).", missed[0]!["text"]!.GetValue<string>());
      Assert.Equal(new[] { "understand", "playagain", "ask" }, r["doors"]!.AsArray().Select(o => o!["cmd"]!.GetValue<string>()).ToArray());
      Assert.Equal(new[] { DeckText.Understand, "Play again · about 2 cents", DeckText.AskTheChat }, r["doors"]!.AsArray().Select(o => o!["label"]!.GetValue<string>()).ToArray());
      Assert.Equal(new[] { "again", "walk" }, r["links"]!.AsArray().Select(o => o!["cmd"]!.GetValue<string>()).ToArray());
      Assert.Equal(new[] { DeckText.TryAgain, DeckText.WalkThis }, r["links"]!.AsArray().Select(o => o!["label"]!.GetValue<string>()).ToArray());

      var back = DeckState.FromJson(d.ToJson());
      Assert.True(back.Reopened);
      Assert.Equal(DeckOutcome.None, back.Answer(0, "v:1"));
      Assert.False(back.TryAgain());
      var rr = back.ResultView();
      Assert.Equal(new[] { "understand", "ask" }, rr["doors"]!.AsArray().Select(o => o!["cmd"]!.GetValue<string>()).ToArray());
      Assert.Equal(new[] { "walk" }, rr["links"]!.AsArray().Select(o => o!["cmd"]!.GetValue<string>()).ToArray());
      Assert.Equal(DeckText.FromHistory, rr["note"]!.GetValue<string>());
    }

    [Fact]
    public void Every_card_right_says_so_with_no_understand_door_and_an_unanswered_card_is_named_on_a_reopened_result()
    {
      var d = Deck();
      d.Answer(0, "v:12"); d.Answer(1, "o:2"); d.Answer(2, "s:1,0"); d.Answer(3, "o:0");
      Assert.StartsWith(DeckText.AllRight, d.ResultView()["reading"]!.GetValue<string>());
      Assert.False(d.ResultView().ContainsKey("missedHead"));
      Assert.DoesNotContain(d.ResultView()["doors"]!.AsArray(), o => o!["cmd"]!.GetValue<string>() == "understand");
      var part = Deck(); part.Answer(0, "v:12");
      var r = DeckState.FromJson(part.ToJson()).ResultView();
      Assert.Equal("1 of 4", r["stamp"]!.GetValue<string>());
      Assert.Equal("Question 2: not answered.", r["missed"]!.AsArray()[0]!["text"]!.GetValue<string>());
    }

    // ---- the result's reading (Brief EO unit 4: a pure function of the outcomes) --------------------------------------------------------------------------------------

    private static DeckCard Card(DeckKind kind, int step, string topic = null) => new DeckCard { Kind = kind, Step = step, Topic = topic };

    /// <summary>A card as "Kind:step:topic", so the table reads as the outcomes it is about.</summary>
    private static DeckCard[] Cards(string spec) => spec.Split(',').Select(c => c.Split(':')).Select(p => Card(Enum.Parse<DeckKind>(p[0]), int.Parse(p[1]), p.Length > 2 ? p[2] : null)).ToArray();

    [Theory]
    [InlineData("Why:1:tree,Why:3:tree,Why:0:component", "0,0,1", "Missed: why the data tree is grafted, flattened or merged, on steps 2 and 4. Right: why these components.")]
    [InlineData("Value:0,Which:2,Why:1:approach", "0,1,1", "Missed: the reading from the live solve, on step 1. Right: why the definition takes this approach and the question about the wiring.")]
    [InlineData("Why:0:list,Feeds:1,Count:2", "0,0,0", "Missed: why the data is a list or a single item, on step 1; the reading from the live solve, on step 3; the question about the wiring, on step 2.")]
    [InlineData("Why:0:tree,Why:1:list", "1,1", "Every one right. You have the why of this selection.")]
    [InlineData("Why:-1:nonsense,Why:2:component,Why:2:component", "0,0,1", "Missed: why these components, on step 3; why the definition takes this approach.")]
    public void The_reading_names_the_missed_kinds_with_their_steps_and_the_kinds_had_right(string cards, string right, string reading)
    {
      Assert.Equal(reading, DeckReading.Of(Cards(cards), right.Split(',').Select(r => r == "1").ToList()));
    }

    [Fact]
    public void An_empty_deck_has_no_reading()
    {
      Assert.Equal("", DeckReading.Of(Array.Empty<DeckCard>(), Array.Empty<bool>()));
      Assert.Equal("", DeckReading.Steps(new[] { -1 }));
    }

    // ---- Understand (Brief EO unit 4: no new call) -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Understand_walks_the_missed_cards_one_at_a_time_with_why_and_back_to_the_score()
    {
      var d = Deck();
      d.Answer(0, "v:11"); d.Answer(1, "o:2"); d.Answer(2, "s:0,1"); d.Answer(3, "o:3");
      Assert.Equal(new[] { 0, 3 }, d.MissedCards.ToArray());
      Assert.Null(d.UnderstandView());                                   // not started
      Assert.Equal(0, d.UnderstandGo(0));
      var first = d.UnderstandView();
      Assert.Equal("understand", first["view"]!.GetValue<string>());
      Assert.Equal("1 of 2", first["of"]!.GetValue<string>());
      Assert.Equal(0, first["card"]!.GetValue<int>());
      Assert.Equal("Read the slider.", first["why"]!["text"]!.GetValue<string>());   // a card with no explanation shows its hint
      Assert.Equal(DeckText.Next, first["next"]!.GetValue<string>());
      Assert.False(first["back"]!.GetValue<bool>());
      Assert.Equal(1, d.UnderstandGo(1));
      var last = d.UnderstandView();
      Assert.Equal(Why().Explain, last["why"]!["text"]!.GetValue<string>());
      Assert.Equal(DeckText.BackToScore, last["next"]!.GetValue<string>());
      Assert.Equal("You answered To colour it.", last["given"]!.GetValue<string>());
      Assert.Equal(-1, d.UnderstandGo(2));                              // past the last is the score
      Assert.Null(d.UnderstandView());
    }

    // ---- names on a card are doors (Brief EO unit 5) ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_name_the_deck_holds_becomes_an_anchor_and_nothing_else_does()
    {
      var labels = new Dictionary<int, string> { [15] = "Move (15)", [16] = "Loft (16)" };
      var seg = DeckAnchors.Segments("Why is Move (15) grafted before (16) and not after Panel (99) or 4 (15 items)?", labels);
      Assert.Equal("[\"Why is \",{\"t\":\"Move (15)\",\"id\":15},\" grafted before \",{\"t\":\"(16)\",\"id\":16},\" and not after Panel (99) or 4 (15 items)?\"]", seg.ToJsonString());
      Assert.Equal("[\"plain\"]", DeckAnchors.Segments("plain", labels).ToJsonString());
      Assert.Equal("[]", DeckAnchors.Segments("", labels).ToJsonString());
      Assert.Equal("[\"Move (15)\"]", DeckAnchors.Segments("Move (15)", new Dictionary<int, string>()).ToJsonString());
    }

    [Fact]
    public void Every_text_on_a_card_carries_its_names_and_the_deck_knows_each_ones_component()
    {
      var d = Deck();
      d.Answer(3, "o:1");
      var v = d.CardView(3);
      Assert.Contains("\"id\":15", v["q"]!.ToJsonString());
      Assert.Contains("\"id\":16", v["turnedAs"]!["why"]!["seg"]!.ToJsonString());
      Assert.True(d.TryGuid(15, out var g) && g == G(5));
      Assert.False(d.TryGuid(99, out _));
      Assert.Contains("\"id\":13", d.CardView(1)["options"]!.AsArray()[0]!["seg"]!.ToJsonString());
    }

    // ---- too few cards (Brief EO unit 1) ---------------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0, false, true)]
    [InlineData(2, false, true)]
    [InlineData(2, true, true)]
    [InlineData(0, true, false)]
    [InlineData(3, false, false)]
    public void Under_three_cards_no_deck_is_dealt_and_a_call_that_failed_with_none_is_no_deck_at_all(int kept, bool failed, bool few)
    {
      var cards = Enumerable.Range(0, kept).Select(_ => Why()).ToList();
      var deck = TeachingCall.Deal(cards, failed, Nodes());
      if (kept == 0 && failed) { Assert.Null(deck); return; }
      Assert.Equal(!few, deck.Dealt);
      if (!few) return;
      var start = deck.StartView("t", 6, 4, "cost");
      Assert.Equal("result", start["view"]!.GetValue<string>());   // the card says so instead of dealing
      Assert.Equal(DeckText.TooFew(kept), start["line"]!.GetValue<string>());
      Assert.Equal(new[] { "playagain" }, start["doors"]!.AsArray().Select(o => o!["cmd"]!.GetValue<string>()).ToArray());
      Assert.Equal(new[] { "walk" }, start["links"]!.AsArray().Select(o => o!["cmd"]!.GetValue<string>()).ToArray());
      var back = DeckState.FromJson(deck.ToJson());
      Assert.False(back.Dealt);
      Assert.Equal(kept, back.Few);
    }

    // ---- the record ------------------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_deck_is_strings_in_the_record_and_reads_back_to_the_same_result_card()
    {
      var d = Deck();
      d.Answer(0, "v:12.0"); d.Answer(1, "o:0"); d.Answer(2, "s:0,1");
      d.TryAgain(); d.Answer(0, "v:12"); d.Answer(1, "o:1"); d.Answer(2, "s:0,2"); d.Answer(3, "t:So each is lofted alone"); d.Judged(3, JudgeVerdict.Partly, "Half of it.", "cost");
      var json = d.ToJson();
      var live = DeckState.FromJson(json, live: true);
      Assert.Equal(d.ResultView().ToJsonString(), live.ResultView().ToJsonString());
      Assert.Equal(d.Id, live.Id);
      Assert.Equal(1, live.Attempt);
      for (var i = 0; i < d.Count - 1; i++) Assert.Equal(d.CardView(i).ToJsonString(), live.CardView(i).ToJsonString());
      Assert.Equal("partly", live.CardView(3)["turnedAs"]!["verdict"]!.GetValue<string>());
      Assert.Equal("Half of it.", live.CardView(3)["turnedAs"]!["judged"]!.GetValue<string>());
      Assert.Equal(Why().Explain, live.Cards[3].Explain);
      Assert.Equal("tree", live.Cards[3].Topic);
      Assert.True(live.TryGuid(15, out var g) && g == G(5));
      Assert.Equal("Move (15)", live.Labels[15]);
      Assert.True(JsonNode.Parse(json)!["cards"]!.AsArray().All(c => c!["lights"]!.AsArray().All(x => x!.GetValueKind() == JsonValueKind.String)));   // strings, never an object
    }

    /// <summary>A record written before Brief EO, with its concept card and no names, reads back as it was written.</summary>
    [Fact]
    public void A_record_from_before_the_why_cards_reads_back()
    {
      const string v1 = "{\"v\":1,\"id\":\"old\",\"attempt\":0,\"cards\":[{\"kind\":\"value\",\"form\":\"enter\",\"step\":0,\"question\":\"Q\",\"hint\":\"h\",\"dwight\":false,\"lights\":[],\"value\":\"12\"}," +
        "{\"kind\":\"concept\",\"form\":\"chooseone\",\"step\":-1,\"question\":\"Why?\",\"hint\":\"h\",\"dwight\":true,\"lights\":[],\"options\":[\"a\",\"b\",\"c\",\"d\"],\"true\":[2]}]," +
        "\"answers\":[{\"card\":0,\"right\":true,\"given\":\"12\",\"picked\":[]},{\"card\":1,\"right\":false,\"given\":\"a\",\"picked\":[0]}]}";
      var d = DeckState.FromJson(v1);
      Assert.NotNull(d);
      Assert.True(d.Dealt);
      Assert.Equal(DeckKind.Concept, d.Cards[1].Kind);
      Assert.Equal("1 of 2", d.ResultView()["stamp"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"cards\":[]}")]
    [InlineData("{\"cards\":[{\"form\":\"chooseone\",\"options\":[\"a\"],\"true\":[0]}]}")]
    [InlineData("{\"cards\":[{\"form\":\"chooseone\",\"options\":[\"a\",\"b\"],\"true\":[5]}]}")]
    [InlineData("{\"cards\":[{\"form\":\"enter\"}]}")]
    public void A_record_that_is_not_a_deck_reads_as_none(string json)
    {
      Assert.Null(DeckState.FromJson(json));
    }

    [Fact]
    public void A_record_op_is_rewritten_never_added()
    {
      var script = PanelScript.New().Op(PanelScript.SetSteps, "{}").Op(PanelScript.SetDeck, "old").Build();
      Assert.True(PanelScript.Replace(script, PanelScript.SetDeck, "new"));
      Assert.Equal("new", script[1].Args[0]);
      var none = PanelScript.New().Op(PanelScript.SetSteps, "{}").Build();
      Assert.False(PanelScript.Replace(none, PanelScript.SetDeck, "new"));
      Assert.Single(none);
      Assert.False(PanelScript.Replace(script, "Unknown", "x"));
    }

    // ---- the chat's lines --------------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_chat_carries_an_answer_only_once_its_card_is_turned_and_marks_Dwights_reading()
    {
      var d = Deck();
      d.Answer(0, "v:12"); d.Answer(3, "o:1");
      var lines = d.Lines();
      Assert.Equal("12", lines[0].Answer);
      Assert.Equal("12", lines[0].TrueValue);
      Assert.Null(lines[1].Answer);
      Assert.Null(lines[1].TrueValue);
      Assert.True(lines[3].DwightsReading);
      Assert.False(lines[0].DwightsReading);
      var text = Dwight.Agent.RunHandOff.Lesson(lines, d.Count, id => id);
      Assert.Contains("(Dwight's own reading, not read from the definition)", text);
      Assert.Contains("Question 2 of 4 (step 2): Which component takes the points? Not answered yet.", text);
    }

    [Fact]
    public void Asking_from_a_card_carries_its_question_and_once_turned_the_answers()
    {
      var d = Deck();
      Assert.Equal("About question 2 of this quiz: Which component takes the points? ", d.AskDraft(1));
      d.Answer(1, "o:0");
      Assert.Equal("About question 2 of this quiz: Which component takes the points? I answered List Length (13); the answer is Move (15). ", d.AskDraft(1));
    }

    // ---- the call's pure half: the payload and the checks ------------------------------------------------------------------------------------------------------------

    private static DeckAsk Ask(bool withFacts = true) => new DeckAsk
    {
      Facts = withFacts ? DeckPlanner.Plan(Input(), TheSteps()) : Array.Empty<DeckFact>(),
      Nodes = Input().Nodes, Steps = TheSteps(),
      Sentences = new[] { "Sets the count.", "Divides the curve (12).", "Measures it.", "Lofts the moved points." }, Summary = "Lofts a divided curve (12).",
      Graph = "{\"nodes\":[{\"id\":11,\"type\":\"Number Slider\"}]}",
    };

    private static JsonObject WhyReply(string question = "Why is Move (15) fed before Loft (16)?", string topic = "approach") => new JsonObject
    {
      ["question"] = question, ["options"] = new JsonArray("So the loft gets moved curves", "To be faster", "To bake", "To colour"), ["answer"] = 0,
      ["hint"] = "What does Loft (16) need?", ["explain"] = "Move (15) shifts every curve first. Loft (16) then lofts the moved curves.", ["topic"] = topic,
    };

    private static string GoodReply(DeckAsk ask)
    {
      var cards = new JsonArray();
      var k = 0;
      foreach (var f in ask.Facts)
      {
        k++;
        var c = new JsonObject { ["card"] = k, ["question"] = "What does it hold?", ["hint"] = "Follow the wire.", ["explain"] = "The slider sets it." };
        if (f.Kind == DeckKind.Value && f.Form == DeckForm.ChooseOne) c["options"] = new JsonArray("3.14", f.Truth, "6.2832");
        if (f.Options != null) c["options"] = new JsonArray("made up", "by the model");   // ignored: the plugin's options stand
        cards.Add(c);
      }
      var why = new JsonArray();
      for (var i = 0; i < ask.Why; i++) why.Add(WhyReply("Why is Move (15) step " + i + " of Loft (16)?", DeckReading.Topics[i % 4]));
      return new JsonObject { ["cards"] = cards, ["why"] = why }.ToJsonString();
    }

    [Fact]
    public void A_good_reply_keeps_the_plugins_cards_with_their_truth_and_the_why_cards_asked_for_marked_as_Dwights_reading()
    {
      var ask = Ask();
      var (kept, error) = TeachingCall.Parse(GoodReply(ask), ask);
      Assert.Null(error);
      Assert.Equal(DeckPlanner.MaxCards, kept.Count);
      var plugin = kept.Where(c => c.Kind != DeckKind.Why).ToList();
      Assert.Equal(ask.Facts.Count, plugin.Count);
      for (var i = 0; i < plugin.Count; i++)
      {
        var f = ask.Facts[i];
        var c = plugin[i];
        Assert.Equal(f.Kind, c.Kind);
        Assert.False(c.Dwight);
        if (f.Form == DeckForm.Enter) Assert.Equal(f.Truth, c.Value);
        else if (f.Options != null) { Assert.Equal(f.Options, c.Options); Assert.Equal(f.TrueSet, c.TrueSet); }
      }
      var whys = kept.Where(c => c.Kind == DeckKind.Why).ToList();
      Assert.Equal(DeckPlanner.MaxWhy, whys.Count);
      Assert.All(whys, c =>
      {
        Assert.True(c.Dwight);
        Assert.Equal(4, c.Options.Count);
        Assert.Equal(new[] { 0 }, c.TrueSet);
        Assert.Equal(3, c.Step);                                       // the step of the first component its question names (Move, step 4)
        Assert.Equal(new[] { G(5), G(6) }, c.Lights);                  // it lights every component its words name
        Assert.NotEmpty(c.Explain);
      });
      Assert.True(kept.Select(c => c.Step).SequenceEqual(kept.Select(c => c.Step).OrderBy(s => s)));   // the deck goes in the Walk's order
    }

    [Theory]
    [InlineData("[\"3.14\",\"6.28\",\"1.57\"]")]               // the truth is not among them
    [InlineData("[\"3.1416\",\"6.28\"]")]                       // two
    [InlineData("[\"3.1416\",\"3.1416\",\"1.57\"]")]           // not different
    [InlineData("[\"3.1416 \",\"6.28\",\"1.57\"]")]            // trimmed, so this one is kept
    public void A_value_card_chosen_from_options_must_hold_the_truth_copied_exactly(string options)
    {
      var input = Input();
      input.Readings = new Dictionary<Guid, IReadOnlyList<PortReading>> { [G(3)] = new[] { R(3, "List Length", "L", "3.1416") } };
      var ask = Ask();
      ask.Facts = DeckPlanner.Plan(input, TheSteps());
      var k = ask.Facts.ToList().FindIndex(f => f.Kind == DeckKind.Value && f.Form == DeckForm.ChooseOne) + 1;
      Assert.True(k > 0);
      var reply = JsonNode.Parse(GoodReply(ask))!;
      reply["cards"]!.AsArray().First(c => c!["card"]!.GetValue<int>() == k)!["options"] = JsonNode.Parse(options);
      var (kept, error) = TeachingCall.Parse(reply.ToJsonString(), ask);
      var keptIt = options.Contains("3.1416 ");
      Assert.Equal(keptIt ? 7 : 6, kept.Count);
      if (!keptIt) Assert.StartsWith("card " + k + ":", error);
    }

    /// <summary>ADR-058 and the prompt's own claim (#315): a card naming any component outside the SELECTION is thrown away, whether the payload's graph holds it or not.</summary>
    [Fact]
    public void A_card_naming_a_component_outside_the_selection_is_dropped_and_never_shown()
    {
      var ask = Ask();
      var reply = JsonNode.Parse(GoodReply(ask))!;
      reply["cards"]![0]!["question"] = "What does the Loft (99) hold?";
      reply["why"]![1]!["explain"] = "Because Panel (77) reads it.";
      var (kept, error) = TeachingCall.Parse(reply.ToJsonString(), ask);
      Assert.Equal(DeckPlanner.MaxCards - 2, kept.Count);
      Assert.Contains("names component (99), which is not in the selection", error);
      Assert.DoesNotContain(kept, c => (c.Question + c.Explain).Contains("(77)"));

      reply["cards"]![0]!["question"] = "What does the Divide Curve (12) send on?";   // a component of the selection is fine
      Assert.Equal(DeckPlanner.MaxCards - 1, TeachingCall.Parse(reply.ToJsonString(), ask).Kept.Count);
    }

    [Theory]
    [InlineData("options", "[\"a\",\"b\",\"c\"]")]
    [InlineData("options", "[\"a\",\"a\",\"c\",\"d\"]")]
    [InlineData("answer", "4")]
    [InlineData("question", "\"\"")]
    [InlineData("question", "\"Why is the curve divided?\"")]     // names no component
    [InlineData("explain", "\"\"")]
    [InlineData("hint", "\"\"")]
    [InlineData("topic", "\"geometry\"")]
    public void A_why_card_needs_a_named_question_four_different_options_its_answer_a_hint_an_explanation_and_a_topic(string field, string value)
    {
      var ask = Ask();
      var reply = JsonNode.Parse(GoodReply(ask))!;
      reply["why"]![0]![field] = JsonNode.Parse(value);
      var (kept, error) = TeachingCall.Parse(reply.ToJsonString(), ask);
      Assert.Equal(DeckPlanner.MaxWhy - 1, kept.Count(c => c.Kind == DeckKind.Why));
      Assert.StartsWith("why question 1:", error);
    }

    [Fact]
    public void A_missing_card_is_an_error_too_few_why_cards_ask_again_and_a_reply_that_is_not_json_keeps_nothing()
    {
      var ask = Ask();
      var reply = JsonNode.Parse(GoodReply(ask))!;
      reply["cards"]!.AsArray().RemoveAt(0);
      var (kept, error) = TeachingCall.Parse(reply.ToJsonString(), ask);
      Assert.Equal(DeckPlanner.MaxCards - 1, kept.Count);
      Assert.Equal("card 1 has no question", error);
      var short1 = JsonNode.Parse(GoodReply(ask))!;
      short1["why"]!.AsArray().RemoveAt(0);
      Assert.Equal("there must be 5 why questions", TeachingCall.Parse(short1.ToJsonString(), ask).Error);
      Assert.Empty(TeachingCall.Parse("not json", ask).Kept);
      Assert.Equal("the reply has no why questions", TeachingCall.Parse("{\"cards\":[]}", Ask(withFacts: false)).Error);
      var many = JsonNode.Parse(GoodReply(ask))!;
      many["why"]!.AsArray().Add(WhyReply());
      Assert.Equal(DeckPlanner.MaxWhy, TeachingCall.Parse(many.ToJsonString(), ask).Kept.Count(c => c.Kind == DeckKind.Why));   // never more than asked for
    }

    [Fact]
    public void The_payload_holds_run_ids_and_words_the_values_the_plugin_read_the_options_it_built_and_the_walks_own_request_and_never_a_guid()
    {
      var ask = Ask();
      var json = TeachingCall.Payload(ask);
      var p = JsonNode.Parse(json)!;
      var cards = p["cards"]!.AsArray();
      Assert.Equal(ask.Facts.Count, cards.Count);
      Assert.Equal("12", cards[0]!["true"]!.GetValue<string>());
      Assert.Equal(11, cards[0]!["id"]!.GetValue<int>());
      foreach (var c in cards)
      {
        var kind = c!["kind"]!.GetValue<string>();
        Assert.Equal(kind == "value" || kind == "count", c.AsObject().ContainsKey("true"));   // a which or feeds card's truth never travels
        Assert.Equal(kind == "which" || kind == "feeds", c.AsObject().ContainsKey("options"));
      }
      Assert.Equal(DeckPlanner.MaxWhy, p["why"]!.GetValue<int>());
      Assert.Equal(ask.Graph, p["graph"]!.GetValue<string>());                             // the Walk's own request, sent again as it is
      Assert.Equal(new[] { "U count (11)", "Divide Curve (12)", "List Length (13)", "Area (14)", "Move (15)", "Loft (16)" }, p["selection"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray());
      Assert.Equal("Lofts a divided curve (12).", p["summary"]!.GetValue<string>());
      for (var i = 1; i <= 6; i++) Assert.DoesNotContain(G(i).ToString(), json);
    }

    // ---- the judge's pure half (Brief EO unit 2) -------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_judge_is_sent_the_question_the_true_answer_why_and_what_was_typed_and_nothing_else()
    {
      var p = JsonNode.Parse(JudgeCall.Payload(Why(), "So each is lofted alone"))!.AsObject();
      Assert.Equal(new[] { "answer", "explain", "question", "true" }, p.Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal).ToArray());
      Assert.Equal("So each curve is lofted on its own", p["true"]!.GetValue<string>());
      Assert.Equal("So each is lofted alone", p["answer"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("{\"verdict\":\"right\",\"sentence\":\"Yes: each curve is its own branch.\"}", "Right", null)]
    [InlineData("{\"verdict\":\"partly\",\"sentence\":\"Branches, yes; why, no.\"}", "Partly", null)]
    [InlineData("{\"verdict\":\"maybe\",\"sentence\":\"x\"}", "None", "the verdict must be right, partly or wrong")]
    [InlineData("{\"verdict\":\"wrong\",\"sentence\":\"\"}", "None", "the sentence is empty or too long")]
    [InlineData("{\"verdict\":\"wrong\",\"sentence\":\"Panel (77) is not it.\"}", "None", "it names component (77), which is not in the deck")]
    [InlineData("{\"verdict\":\"wrong\",\"sentence\":\"Move (15) moves before the loft.\"}", "Wrong", null)]
    [InlineData("not json", "None", "the reply was not valid JSON")]
    public void The_judges_reply_is_a_verdict_and_one_sentence_naming_only_the_decks_components(string json, string verdict, string error)
    {
      var (v, sentence, why) = JudgeCall.Parse(json, new[] { 15, 16 });
      Assert.Equal(Enum.Parse<JudgeVerdict>(verdict), v);
      if (error == null) { Assert.Null(why); Assert.False(string.IsNullOrEmpty(sentence)); }
      else Assert.StartsWith(error, why);
    }

    // ---- the shells' fixtures, from the product's own writer ---------------------------------------------------------------------------------------------------------

    private static JsonObject Fixture()
    {
      var open = Deck();
      var oneOpen = Deck(); oneOpen.Answer(0, "v:12");
      var allOpen = Deck(); allOpen.Answer(0, "v:12"); allOpen.Answer(1, "o:2");
      var nan = Deck(); nan.Answer(0, "v:twelve");
      var right = Deck(); right.Answer(0, "v:12");
      var wrong = Deck(); wrong.Answer(0, "v:12"); wrong.Answer(1, "o:0");
      var allTurned = Deck(); allTurned.Answer(0, "v:12"); allTurned.Answer(1, "o:2"); allTurned.Answer(2, "s:0,2");
      var whyOpen = Deck(); whyOpen.Answer(0, "v:12"); whyOpen.Answer(1, "o:2"); whyOpen.Answer(2, "s:0,1");
      var judging = Deck(); judging.Answer(0, "v:12"); judging.Answer(1, "o:2"); judging.Answer(2, "s:0,1"); judging.Answer(3, "t:So each curve is lofted alone");
      var notJudged = Deck(); notJudged.Answer(0, "v:12"); notJudged.Answer(1, "o:2"); notJudged.Answer(2, "s:0,1"); notJudged.Answer(3, "t:So each curve is lofted alone"); notJudged.JudgeFailed(3);
      var noJudge = Deck(); noJudge.JudgeOff = DeckText.NoJudge; noJudge.Answer(0, "v:12"); noJudge.Answer(1, "o:2"); noJudge.Answer(2, "s:0,1");
      var partly = Deck(); partly.Answer(0, "v:12"); partly.Answer(1, "o:2"); partly.Answer(2, "s:0,1"); partly.Answer(3, "t:So each is a branch"); partly.Judged(3, JudgeVerdict.Partly, "Each is a branch, yes; the loft is why.", "About 0.1 cents, on Claude Haiku 5.");
      var whyWrong = Deck(); whyWrong.Answer(0, "v:12"); whyWrong.Answer(1, "o:2"); whyWrong.Answer(2, "s:0,1"); whyWrong.Answer(3, "o:1");
      var done = Deck(); done.Answer(0, "v:12"); done.Answer(1, "o:0"); done.Answer(2, "s:0,1"); done.Answer(3, "o:1"); done.AgainEstimate = "about 2 cents";
      var understand = Deck(); understand.Answer(0, "v:12"); understand.Answer(1, "o:0"); understand.Answer(2, "s:0,1"); understand.Answer(3, "o:1"); understand.UnderstandGo(1);
      var perfect = new DeckState(new[] { Typed(), Typed(), Typed() }); perfect.Answer(0, "v:12"); perfect.Answer(1, "v:12"); perfect.Answer(2, "v:12");
      return new JsonObject
      {
        ["note"] = "Rendered by TeachingDeckTests from DeckState's views and WalkText.View; it fails if this file differs. The shells' fixtures read it, so they hold only what the host can send. Regenerate with DWIGHT_WRITE_FIXTURES=1.",
        ["start"] = open.StartView("Divides a curve and lofts the moved points.", 6, 4, TeachingText.Cost(2.1)).ToJsonString(),
        ["enter"] = open.CardView(0).ToJsonString(),
        ["notANumber"] = nan.CardView(0).ToJsonString(),
        ["one"] = oneOpen.CardView(1).ToJsonString(),
        ["all"] = allOpen.CardView(2).ToJsonString(),
        ["right"] = right.CardView(0).ToJsonString(),
        ["wrong"] = wrong.CardView(1).ToJsonString(),
        ["allTurned"] = allTurned.CardView(2).ToJsonString(),
        ["whyOpen"] = whyOpen.CardView(3).ToJsonString(),
        ["judging"] = judging.CardView(3).ToJsonString(),
        ["notJudged"] = notJudged.CardView(3).ToJsonString(),
        ["noJudge"] = noJudge.CardView(3).ToJsonString(),
        ["partly"] = partly.CardView(3).ToJsonString(),
        ["whyWrong"] = whyWrong.CardView(3).ToJsonString(),
        ["result"] = done.ResultView().ToJsonString(),
        ["understand"] = understand.UnderstandView().ToJsonString(),
        ["perfect"] = perfect.ResultView().ToJsonString(),
        ["reopened"] = DeckState.FromJson(done.ToJson()).ResultView().ToJsonString(),
        ["few"] = TeachingCall.Deal(new[] { Why(), Why() }, false, Nodes()).StartView("t", 6, 4, null).ToJsonString(),
        ["confirm"] = DeckViews.Confirm("This will read 6 components, about 9,000 characters, about 3 cents on Claude Sonnet 5.5 for the read and the answer. Continue?").ToJsonString(),
        ["writing"] = DeckViews.Writing().ToJsonString(),
        ["bar"] = WalkText.View(1, 4, "", null, canPlay: false, everyWire: false, deck: oneOpen.CardView(1)),
        ["barWrong"] = WalkText.View(1, 4, "", null, canPlay: false, deck: wrong.CardView(1)),
      };
    }

    [Fact]
    public void A_walk_with_no_deck_sends_none_of_it_and_the_bar_draws_the_panels_card()
    {
      var plain = JsonNode.Parse(WalkText.View(1, 3, "<p>x</p>", null))!.AsObject();
      Assert.False(plain.ContainsKey("deck"));
      Assert.Equal("Step 2 of 3", plain["label"]!.GetValue<string>());
      var d = Deck(); d.Answer(0, "v:12");
      var bar = JsonNode.Parse(WalkText.View(1, 4, "", null, deck: d.CardView(1)))!.AsObject();
      Assert.Equal(d.CardView(1).ToJsonString(), bar["deck"]!.ToJsonString());   // the panel and the bar draw one card from one state
      Assert.Equal(DeckText.Kicker, bar["label"]!.GetValue<string>());
    }

    [Fact]
    public void The_shells_fixture_file_is_the_real_output_of_the_writer()
    {
      var text = Fixture().ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }).Replace("\r\n", "\n") + "\n";
      var dir = new DirectoryInfo(AppContext.BaseDirectory);
      while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Dwight.sln"))) dir = dir.Parent;
      var path = Path.Combine(dir!.FullName, "tools", "ui-teaching-views.json");
      if (Environment.GetEnvironmentVariable("DWIGHT_WRITE_FIXTURES") == "1") File.WriteAllText(path, text, new System.Text.UTF8Encoding(false));
      Assert.True(File.Exists(path), "tools/ui-teaching-views.json is missing; run this test with DWIGHT_WRITE_FIXTURES=1");
      Assert.True(File.ReadAllText(path).Replace("\r\n", "\n") == text, "tools/ui-teaching-views.json is not the writer's real output; run this test with DWIGHT_WRITE_FIXTURES=1 and read the diff");
    }
  }
}
