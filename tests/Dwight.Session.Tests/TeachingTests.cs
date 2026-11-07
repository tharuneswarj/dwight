using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using Dwight.Session;
using Dwight.UI;

namespace Dwight.Session.Tests
{
  /// <summary>
  /// Brief DE, Learn, the GH-free half that outlives the deck: which steps have a value the plugin read (it reads every true value), the words that are not the deck's,
  /// the gate on all of it, the mode, and the stored score. The deck itself (Brief EI) is <c>TeachingDeckTests</c>; the one structured call is <c>TeachingCallTests</c>.
  /// Each law has a mutation that turns a test red (see the PR).
  /// </summary>
  public class TeachingTests
  {
    private static Guid G(int n) => new Guid(n, 0, 0, new byte[8]);

    private static PortReading Reading(int n, string type, string nick, string port, string value, ReadingKind kind = ReadingKind.Number) =>
      new PortReading { Component = G(n), Type = type, Nick = nick, Port = port, Kind = kind, Value = value };

    // ---- which steps have a question --------------------------------------------------------------------------------------------------

    private static IReadOnlyDictionary<Guid, IReadOnlyList<PortReading>> Graph() => new Dictionary<Guid, IReadOnlyList<PortReading>>
    {
      [G(1)] = new[] { Reading(1, "Number Slider", "U count", "U count", "12") },
      [G(2)] = new[] { Reading(2, "List Length", null, "L", "96") },
      [G(3)] = new PortReading[0],   // a component the plugin read nothing from
      [G(5)] = new[] { Reading(5, "Divide Domain", null, "S", "8", ReadingKind.ListCount), Reading(5, "Divide Domain", null, "T", "9") },
    };

    private static IReadOnlyList<IReadOnlyList<Guid>> Steps(params Guid[][] steps) => steps.Select(s => (IReadOnlyList<Guid>)s).ToList();

    [Fact]
    public void A_step_with_a_readable_port_has_a_question_and_the_plugin_names_the_reading()
    {
      var facts = TeachingChooser.Choose(Steps(new[] { G(1) }, new[] { G(2) }), Graph());
      Assert.Equal(2, facts.Count);
      Assert.Equal(new[] { 0, 1 }, facts.Select(f => f.Step).ToArray());
      Assert.Equal("12", facts[0].Reading.Value);
      Assert.Equal("96", facts[1].Reading.Value);
    }

    [Fact]
    public void A_step_with_nothing_the_plugin_can_read_has_no_question_and_the_step_after_it_keeps_its_number()
    {
      var facts = TeachingChooser.Choose(Steps(new[] { G(3) }, new[] { G(4) }, new[] { G(2) }), Graph());
      Assert.Single(facts);
      Assert.Equal(2, facts[0].Step);   // step 3 of the Walk, by its own index, so the bar finds it
    }

    [Fact]
    public void Within_a_step_the_first_component_with_a_reading_and_its_first_reading_are_the_question()
    {
      var facts = TeachingChooser.Choose(Steps(new[] { G(3), G(5), G(1) }), Graph());
      Assert.Single(facts);
      Assert.Equal(G(5), facts[0].Reading.Component);
      Assert.Equal("S", facts[0].Reading.Port);
      Assert.Equal(ReadingKind.ListCount, facts[0].Reading.Kind);
    }

    [Fact]
    public void A_reading_with_no_value_is_never_asked_about()
    {
      var readings = new Dictionary<Guid, IReadOnlyList<PortReading>> { [G(1)] = new[] { Reading(1, "x", null, "p", "  ") } };
      Assert.Empty(TeachingChooser.Choose(Steps(new[] { G(1) }), readings));
      Assert.Empty(TeachingChooser.Choose(null, readings));
      Assert.Empty(TeachingChooser.Choose(Steps(new[] { G(1) }), null));
    }

    [Theory]
    [InlineData(12, "12")]
    [InlineData(0.25, "0.25")]
    [InlineData(1.23456789, "1.2346")]
    [InlineData(-3.0, "-3")]
    public void A_number_reads_as_a_person_writes_it(double d, string text)
    {
      Assert.True(TeachingNumbers.TryText(d, out var t));
      Assert.Equal(text, t);
    }

    [Fact]
    public void A_bool_a_nan_a_string_and_a_null_are_not_numbers()
    {
      Assert.False(TeachingNumbers.TryText(true, out _));
      Assert.False(TeachingNumbers.TryText(double.NaN, out _));
      Assert.False(TeachingNumbers.TryText(double.PositiveInfinity, out _));
      Assert.False(TeachingNumbers.TryText("12", out _));
      Assert.False(TeachingNumbers.TryText(null, out _));
      Assert.True(TeachingNumbers.TryText(7, out var i)); Assert.Equal("7", i);
    }

    // ---- the plugin's words ------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_cost_line_states_the_figure_after_the_call_and_the_note_says_there_are_no_questions()
    {
      Assert.Equal("Questions cost one more call, 2.1 cents.", TeachingText.Cost(2.14));
      Assert.Equal("Questions cost one more call, less than 0.1 cents.", TeachingText.Cost(0.04));
      Assert.Equal("Questions cost one more call.", TeachingText.Cost(null));
      Assert.Equal("Dwight could not write questions this time, so Step by step runs without them.", TeachingText.Unavailable);
    }

    // ---- the toggle gates everything ----------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(false, false, false)]
    [InlineData(true, true, true)]
    public void Nothing_runs_unless_Learn_was_chosen_and_the_run_is_a_Walk(bool learn, bool walk, bool active)
    {
      Assert.Equal(active, TeachingGate.Active(learn, walk));
    }

    private static string Source(params string[] parts)
    {
      var dir = new DirectoryInfo(AppContext.BaseDirectory);
      while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Dwight.sln"))) dir = dir.Parent;
      Assert.True(dir != null);
      return File.ReadAllText(Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray())).Replace("\r\n", "\n");
    }

    [Fact]
    public void Unless_Learn_was_chosen_the_run_reads_nothing_asks_nothing_and_hands_the_panel_no_deck()
    {
      var actions = Source("Dwight", "LlmActions.cs");
      Assert.Contains("var teachInput = TeachingGate.Active(learn, true) ? GhTeaching.Deck(graph, GhTeaching.Read(", actions);
      Assert.Contains("if (teachInput != null)", actions);
      Assert.Equal(1, actions.Split(new[] { "TeachingCall.Ask(" }, StringSplitOptions.None).Length - 1);
      var ask = actions.IndexOf("TeachingCall.Ask(", StringComparison.Ordinal);
      Assert.True(actions.LastIndexOf("if (teachInput != null)", ask, StringComparison.Ordinal) > 0);   // the one call is inside the gate
      Assert.Contains(".OpIf(teach != null, PanelScript.SetDeck, teach?.ToJson())", actions);              // a run with no deck records none

      var session = Source("Dwight", "UI", "WalkSession.cs");
      Assert.Contains("private static bool IsDeck => _panel?.DeckShown == true;", session);   // a Walk with no deck draws none of it
      Assert.Contains("CanvasGlow.SetHatch(cover.HasValue ? new[] { cover.Value } : null);", session);
    }

    // ---- Learn: a mode of Explain, chosen per run, with no global state (Tharun, 1 November 2026) ----------------------------------------------------------

    [Fact]
    public void Learn_is_a_mode_of_Explain_with_its_own_word_on_the_row()
    {
      Assert.Equal("learn", ExplainModes.Learn);
      Assert.Equal("learn", ExplainModes.Label(ExplainModes.Learn));
      Assert.Equal("step by step", ExplainModes.Label(ExplainModes.Walk));
      Assert.Equal("walk", ExplainModes.Walk);   // the id and its persisted rows are unchanged (Brief EO)
      Assert.Equal(ExplainModes.Walk, ExplainModes.ForCount(5));   // choosing nothing is still a Walk, never a lesson
    }

    [Fact]
    public void Learn_is_the_fourth_entry_after_Walk_Detailed_and_Short_in_both_menus_and_group_only()
    {
      // Brief EU: the chord's shape is ChordMenu's, held built in Dwight.Menu.Tests; the chord gives each row its mode.
      var chord = Source("Dwight", "UI", "ChordMenu.cs");
      var walk = chord.IndexOf("\"Step by step\"", StringComparison.Ordinal);
      var detailed = chord.IndexOf("\"Detailed\"", StringComparison.Ordinal);
      var shortAt = chord.IndexOf("\"Short\"", StringComparison.Ordinal);
      var learn = chord.IndexOf("\"Learn\"", StringComparison.Ordinal);
      Assert.True(walk > 0 && walk < detailed && detailed < shortAt && shortAt < learn);
      Assert.Contains("if (selCount >= 2) Sub(explain, \"Learn\", doors.ExplainLearn);", chord);
      Assert.Contains("ExplainLearn = () => LlmActions.Explain(Dwight.UI.ExplainModes.Learn),", Source("Dwight", "DwightCanvasPopup.cs"));

      var bar = Source("Dwight", "DwightTopBarMenu.cs");
      Assert.Contains("new ToolStripMenuItem(\"Learn\")", bar);
      Assert.Contains("walk.Visible = learn.Visible = DwightActions.CurrentSelection().Count >= 2;", bar);
    }

    [Fact]
    public void A_Learn_run_is_a_Walk_that_remembers_it_was_chosen_and_a_Retry_of_it_is_a_Learn_run()
    {
      var actions = Source("Dwight", "LlmActions.cs");
      Assert.Contains("var learn = mode == ExplainModes.Learn;", actions);
      Assert.Contains("if (learn) mode = ExplainModes.Walk;", actions);
      var retry = actions.IndexOf("ctx.Rerun = () => ExplainOn(guids, mode, ctx.CostAnswered);", StringComparison.Ordinal);
      var convert = actions.IndexOf("if (learn) mode = ExplainModes.Walk;", StringComparison.Ordinal);
      Assert.True(retry > 0 && retry < convert);   // the Retry is bound to the mode as it was chosen, before Learn becomes a Walk
      Assert.Contains("(mode == ExplainModes.Walk || mode == ExplainModes.Learn) && guids.Count < 2", actions);   // one component: Detailed, and no lesson
    }

    [Fact]
    public void An_answer_redraws_both_surfaces_lifts_the_cover_and_writes_the_record_and_the_score()
    {
      var session = Source("Dwight", "UI", "WalkSession.cs");
      var deck = session.Substring(session.IndexOf("public static void DeckAnswer(", StringComparison.Ordinal));
      deck = deck.Substring(0, deck.IndexOf("public static void DeckNext(", StringComparison.Ordinal));
      Assert.Contains("Redraw();", deck);   // the bar in Focus
      Assert.Contains("PushDeck();", deck);   // and the panel's card, from the same state
      Assert.Contains("if (_focus.Active && !IsDeck) return WalkBar.Get()?.Present(ViewNow()) ?? WalkSurface.Panel;", session);
      Assert.Contains("UpdateCover();", deck);
      Assert.Contains("CommandHistory.SetDeck(_doc, _panel.RunRow, deck.ToJson(), deck.Right, deck.Count, deck.AllAnswered);", deck);
      Assert.True(deck.IndexOf("if (outcome != DeckOutcome.NotANumber)", StringComparison.Ordinal) < deck.IndexOf("CommandHistory.SetDeck(", StringComparison.Ordinal));   // a typed word that is no number writes nothing
      Assert.DoesNotContain("FrameStep", deck);   // an answer never moves the person's canvas
    }

    [Fact]
    public void A_card_opens_in_order_the_arrow_waits_for_an_answer_and_after_the_last_comes_the_result()
    {
      var session = Source("Dwight", "UI", "WalkSession.cs");
      Assert.Contains("if (panel?.DeckShown == true && !panel.Deck.MayOpen(i)) return;", session);   // a dot or a card cannot skip ahead
      var next = session.Substring(session.IndexOf("public static void Next()", StringComparison.Ordinal));
      next = next.Substring(0, next.IndexOf("public static void Back()", StringComparison.Ordinal));
      Assert.Contains("if (!_panel.Deck.IsAnswered(_state.Index)) return;", next);
      Assert.Contains("if (_state.Index >= _state.Count - 1) { Leave(false); return; }", next);
      Assert.True(next.IndexOf("IsAnswered(", StringComparison.Ordinal) < next.IndexOf("_state.Next()", StringComparison.Ordinal));
      Assert.Contains("if (deck && panel != null && !panel.IsClosed) panel.ShowDeckAfterWalk();", session);   // a Walk that ends never leaves a card with no Walk behind it
      Assert.Contains("if (panel.DeckShown && (panel.Deck.Reopened || panel.Deck.AllAnswered || !panel.Deck.Dealt)) { panel.ShowDeckResult(); return; }", session);   // a reopened deck opens no card, nor one that was not dealt
    }

    [Fact]
    public void A_page_keeps_its_own_live_deck_and_reads_any_other_back_as_reopened()
    {
      var panel = Source("Dwight", "UI", "WebPanel.cs");
      Assert.Contains("var read = DeckState.FromJson(deckJson);", panel);
      Assert.Contains("if (Deck == null || Deck.Id != read.Id) Deck = read;", panel);
      var live = new DeckState(new[] { new DeckCard { Form = DeckForm.Enter, Value = "12", Question = "q" } });
      Assert.True(DeckState.FromJson(live.ToJson()).Reopened);                 // what the record plays is read back reopened
      Assert.False(DeckState.FromJson(live.ToJson(), live: true).Reopened);
      Assert.Contains("case \"deck\": WalkSession.LivePanel?.DeckCommand(message.Substring(5)); break;", Source("Dwight", "UI", "WalkBar.cs"));   // the bar's card is the page's card
    }

    [Fact]
    public void There_is_no_Teaching_setting_no_switch_and_no_handler_left()
    {
      Assert.DoesNotContain("Teaching", Source("Dwight", "Config", "DwightSettings.cs"));
      Assert.DoesNotContain("teaching", Source("Dwight", "UI", "DwightSettingsPanel.cs"));
      Assert.DoesNotContain("eaching", Source("Dwight", "Resources", "settings.html"));
    }

    // ---- the stored score ----------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_score_is_a_pair_of_numbers_on_the_row_and_survives_the_store()
    {
      var rec = new SessionRecord { Commands = new List<StoredCommand> { new StoredCommand { Action = "Explain", Subtitle = "walk", Score = new[] { 4, 6 } } } };
      var back = SessionStoreCore.Read(SessionStoreCore.Write(rec)).Record.Commands.Single();
      Assert.Equal(new[] { 4, 6 }, back.Score);
      Assert.Contains("\"score\":[4,6]", SessionStoreCore.Write(rec));
    }

    [Fact]
    public void A_row_with_no_lesson_stores_no_score()
    {
      var rec = new SessionRecord { Commands = new List<StoredCommand> { new StoredCommand { Action = "Explain", Subtitle = "walk" } } };
      Assert.DoesNotContain("score", SessionStoreCore.Write(rec));
    }

    [Fact]
    public void A_row_that_is_read_back_keeps_only_a_score_that_could_be_true()
    {
      var history = Source("Dwight", "Session", "CommandHistory.cs");
      // The rule is TeachingText.Pair now, asked by the writer, the read and the reopened line alike (the same question, so the three cannot drift).
      Assert.Contains("Score = s.Score != null && s.Score.Length == 2 ? Dwight.UI.TeachingText.Pair(s.Score[0], s.Score[1]) : null,", history);
      Assert.Contains("var pair = Dwight.UI.TeachingText.Pair(right, asked);", history);
      Assert.Contains("row.Score = pair;", history);
    }

    [Fact]
    public void A_reopened_explain_says_how_the_lesson_went_beside_its_cost()
    {
      var s = Source("Dwight", "DwightSession.cs");
      var reopen = s.Substring(s.IndexOf("public static void Reopen(", StringComparison.Ordinal));
      // The line is composed by DockRows.ReopenedStatus (the score and the stopped word no longer replace each other); the reopen hands it the row's own fields.
      Assert.Contains("DockRows.ReopenedStatus(cmd.State, cmd.Spend, cmd.CostLine, cmd.Score)", reopen);
      Assert.Contains("Dwight.UI.TeachingText.Score(score[0], score[1])", Source("Dwight", "Session", "DockRows.cs"));
    }

    [Theory]
    [InlineData("ok", null, "about 4 c", 3, 3, "about 4 c · 3 of 3 right")]
    [InlineData("ok", null, "", 3, 3, "3 of 3 right")]
    [InlineData("stopped", 10.7, "", 2, 3, "stopped · ~10.7¢ · 2 of 3 right")]
    public void A_reopened_learn_reads_its_score_beside_its_cost_or_its_state(string state, double? cents, string costLine, int right, int asked, string line)
    {
      var spend = cents.HasValue ? new StoredSpend { Model = "m", Cents = cents.Value, InputTokens = 10, OutputTokens = 10 } : null;
      Assert.Equal(line, DockRows.ReopenedStatus(state, spend, costLine, new[] { right, asked }));
    }

    [Fact]
    public void A_reopened_row_with_no_lesson_reads_as_it_did_and_a_score_that_could_not_be_true_is_not_said()
    {
      Assert.Null(DockRows.ReopenedStatus("ok", null, "about 4 c", null));
      Assert.Null(DockRows.ReopenedStatus("ok", null, "about 4 c", new[] { 4, 3 }));
      Assert.Null(DockRows.ReopenedStatus("ok", null, "about 4 c", new[] { 0, 0 }));
    }

    [Theory]
    [InlineData(3, 3, true)]
    [InlineData(0, 3, true)]
    [InlineData(4, 3, false)]
    [InlineData(-1, 3, false)]
    [InlineData(0, 0, false)]
    public void The_pair_a_row_may_hold_is_one_that_could_be_true(int right, int asked, bool kept)
    {
      var pair = TeachingText.Pair(right, asked);
      if (kept) Assert.Equal(new[] { right, asked }, pair); else Assert.Null(pair);
    }

    [Fact]
    public void The_writer_the_read_and_the_reopened_line_ask_the_pair_rule_and_the_row_must_still_be_in_the_list()
    {
      var history = Source("Dwight", "Session", "CommandHistory.cs");
      var writer = history.Substring(history.IndexOf("public static void SetTeachScore(", StringComparison.Ordinal));
      writer = writer.Substring(0, writer.IndexOf("Brief DG: put a run's spend", StringComparison.Ordinal));
      Assert.Contains("if (pair == null || !list.Contains(row)) return;", writer);   // a row that has gone from the list is left alone
      Assert.Contains("Persist(doc, key, list);", writer);
      Assert.True(writer.IndexOf("row.Score = pair;", StringComparison.Ordinal) < writer.IndexOf("Persist(doc, key, list);", StringComparison.Ordinal));   // set, then written
    }

    [Fact]
    public void The_score_the_writer_keeps_is_the_score_the_reopened_line_says()
    {
      var pair = TeachingText.Pair(3, 3);
      Assert.Equal("about 4 c · 3 of 3 right", DockRows.ReopenedStatus("ok", null, "about 4 c", pair));
    }
  }
}
