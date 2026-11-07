using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dwight.Agent;
using Dwight.Llm;
using Dwight.Session;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// Brief EC units 5 and 5b, from Rhino 2's measurement of the seventeenth: a run's chat is seeded through the MODEL door with the verb, its time, every
  /// source in the chat's numbering and the result in the plugin's words. Each verb's seed goes through the real <see cref="ChatToTranscript"/>, the one
  /// place a thread becomes what the model is sent, and the test reads what the model reads. The run numbers add1 395 and add2 409; the chat numbers them
  /// 5096 and 5097, as Tharun's sitting found them.
  /// </summary>
  public class RunHandOffTests
  {
    private static readonly Guid Add1 = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Add2 = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Gone = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly DateTime At = new DateTime(2026, 11, 2, 14, 2, 0);

    private static readonly Dictionary<Guid, int> RunLabels = new Dictionary<Guid, int> { [Add1] = 395, [Add2] = 409, [Gone] = 425 };
    private static int? ChatId(Guid g) => g == Add1 ? 5096 : g == Add2 ? 5097 : (int?)null;
    private static string NameOf(Guid g) => g == Add1 ? "add1" : g == Add2 ? "add2" : null;

    private static RunResult Run(string verb) => new RunResult { Verb = verb, At = At, Sources = new[] { Add1, Add2 }, Labels = RunLabels };

    /// <summary>What the model is sent on the person's first message after the seed: the seed as Dwight's prior words, then the question.</summary>
    private static string ModelReads(RunResult run)
    {
      var seed = RunHandOff.Seed(run, NameOf, ChatId);
      var stored = new List<StoredMessage>
      {
        new StoredMessage { Who = "dw", Html = "Tell me what you'd like to build." },   // the greeting is chrome, and never sent
        new StoredMessage { Who = "dw", Src = seed, Html = seed },
        new StoredMessage { Who = "me", Src = "Which action produced the result above?", Html = "Which action produced the result above?" },
      };
      var messages = ChatToTranscript.Build(stored);
      Assert.Null(AgentLoopCore.CheckTranscript(messages));
      Assert.Equal(LlmRole.Assistant, messages[0].Role);
      return string.Join("\n", messages[0].Blocks.Select(b => b.Text));
    }

    private static void CarriesTheRun(string sent, string verb)
    {
      Assert.Contains("This conversation carries your " + verb + " of 14:02, which ran on 2 components: add1 (5096), add2 (5097). Its result follows.", sent);
      Assert.DoesNotContain("(395)", sent);
      Assert.DoesNotContain("(409)", sent);
    }

    [Fact]
    public void Explain_prose_reaches_the_model_with_the_verb_the_ids_and_its_words_in_the_chats_numbering()
    {
      var run = Run("Explain");
      run.Prose = "The **add1** component (395) sums `s1` and `s2`, and add2 (409) sums the other two; Stream Filter (425) picks one.";
      var sent = ModelReads(run);
      CarriesTheRun(sent, "Explain");
      Assert.Contains("The **add1** component (5096) sums `s1` and `s2`, and add2 (5097) sums the other two; Stream Filter picks one.", sent);
    }

    [Fact]
    public void A_walk_reaches_the_model_as_its_summary_and_its_steps_in_order()
    {
      var run = Run("Explain");
      run.Prose = "";
      run.StepsSummary = "Two sums multiplied.";
      run.Steps = new[] { "add1 (395) sums s1 and s2.", "add2 (409) sums s3 and s4." };
      var sent = ModelReads(run);
      CarriesTheRun(sent, "Explain");
      Assert.Contains("Two sums multiplied.\n\n1. add1 (5096) sums s1 and s2.\n2. add2 (5097) sums s3 and s4.", sent);
    }

    [Fact]
    public void A_lesson_reaches_the_model_with_its_questions_the_answers_given_and_the_score()
    {
      var run = Run("Explain");
      run.Steps = new[] { "add1 (395) sums s1 and s2.", "add2 (409) sums s3 and s4." };
      run.LessonQuestions = 2;
      run.Lesson = new[]
      {
        new LessonLine { Step = 0, Question = "What does add1 output?", Answer = "7", TrueValue = "7", Right = true },
        new LessonLine { Step = 1, Question = "What does add2 output?" },
      };
      var sent = ModelReads(run);
      CarriesTheRun(sent, "Explain");
      Assert.Contains("- Question 1 of 2 (step 1): What does add1 output? They answered 7; the answer is 7 (read from the definition).", sent);
      Assert.Contains("- Question 2 of 2 (step 2): What does add2 output? Not answered yet.", sent);
      Assert.Contains("1 of 1 answered right so far.", sent);
    }

    [Fact]
    public void A_question_not_yet_answered_carries_no_true_value()
    {
      var text = RunHandOff.Lesson(new[] { new LessonLine { Step = 0, Question = "Q?", TrueValue = "42" } }, 1, Ids);
      Assert.DoesNotContain("42", text);
    }

    [Fact]
    public void A_finished_lesson_carries_its_score()
    {
      var text = RunHandOff.Lesson(new[]
      {
        new LessonLine { Step = 0, Question = "A?", Answer = "1", TrueValue = "1", Right = true },
        new LessonLine { Step = 2, Question = "B?", Answer = "3", TrueValue = "2", Right = false },
      }, 2, Ids);
      Assert.EndsWith("They got 1 of 2.", text);
    }

    [Fact]
    public void Reorganise_reaches_the_model_with_its_outcome_and_its_regions_in_words()
    {
      var run = Run("Reorganise");
      run.Prose = "Reorganised - moved 2 component(s), 1 labelled group(s), faded 0 wire(s). Wiring and values are unchanged (verified); one Ctrl-Z reverts the layout and the wire display.";
      run.Regions = new[] { new RunRegion { Label = "Sums", Members = new[] { Add1, Add2 } } };
      var sent = ModelReads(run);
      CarriesTheRun(sent, "Reorganise");
      Assert.Contains("Reorganised - moved 2 component(s)", sent);
      Assert.Contains("Its regions:\n- **Sums**: add1 (5096), add2 (5097)", sent);
    }

    [Fact]
    public void Simplify_reaches_the_model_with_its_changes_and_advice_from_the_data_the_panel_rendered()
    {
      var run = Run("Simplify");
      run.SimplifyPreviewJson = "{\"title\":\"Better logic\",\"canApply\":true,\"changes\":[{\"i\":0,\"cls\":\"\",\"text\":\"Merge add1 (395) and add2 (409).\",\"confirm\":false,"
        + "\"removes\":[{\"type\":\"Addition\",\"id\":409}],\"adds\":[{\"type\":\"Mass Addition\",\"nick\":\"sum\"}],\"renames\":[]}],"
        + "\"advice\":[{\"observation\":\"add1 (395) has no output wire.\",\"suggestion\":\"Wire it or remove it.\"}]}";
      var sent = ModelReads(run);
      CarriesTheRun(sent, "Simplify");
      Assert.Contains("**Simplify, better logic.** 1 change proposed, none applied yet:\n- Merge add1 (5096) and add2 (5097). (removes Addition (5097); adds Mass Addition \"sum\")", sent);
      Assert.Contains("Dwight also noticed:\n- add1 (5096) has no output wire. Wire it or remove it.", sent);
    }

    [Fact]
    public void A_clean_simplify_says_there_was_nothing_to_change()
    {
      var run = Run("Simplify");
      run.SimplifyPreviewJson = "{\"title\":\"Better logic\",\"canApply\":false,\"changes\":[],\"advice\":[]}";
      var sent = ModelReads(run);
      CarriesTheRun(sent, "Simplify");
      Assert.Contains("**Simplify, better logic.** The selection is already clean: nothing to change.", sent);
    }

    [Fact]
    public void An_applied_simplify_reaches_the_model_with_what_it_applied()
    {
      var run = Run("Simplify");
      run.SimplifyAppliedJson = "{\"summary\":\"Removed 1 component.\",\"changes\":[{\"i\":0,\"text\":\"Removed the spare add2 (409).\",\"ops\":[]}],\"skipped\":[],\"advice\":[]}";
      var sent = ModelReads(run);
      Assert.Contains("**Simplify, applied.** Removed 1 component.\n- Removed the spare add2 (5097).", sent);
    }

    [Fact]
    public void Diagnose_reaches_the_model_with_the_erroring_components_and_grasshoppers_messages()
    {
      var run = Run("Diagnose");
      run.FaultsJson = "{\"lead\":\"Found 1 issue.\",\"chains\":[{\"root\":{\"title\":\"Negative (409)\",\"role\":\"root\",\"ghMessage\":\"Input parameter X failed to collect data\","
        + "\"causeMd\":\"Nothing reaches its X from add1 (395).\",\"fixMd\":\"Wire add1 (395) into X.\",\"guid\":\"" + Add2 + "\"},\"members\":[]}],"
        + "\"stray\":[{\"title\":\"Panel\",\"guid\":\"" + Add1 + "\"}]}";
      var sent = ModelReads(run);
      CarriesTheRun(sent, "Diagnose");
      Assert.Contains("**Diagnose.** Found 1 issue.", sent);
      Assert.Contains("- **Root cause:** Negative (5097). Grasshopper says: \"Input parameter X failed to collect data\". Nothing reaches its X from add1 (5096). Fix: Wire add1 (5096) into X.", sent);
      Assert.Contains("Not wired in: Panel (5096).", sent);
    }

    [Fact]
    public void A_run_that_rendered_nothing_says_so_rather_than_seeming_to_carry_something()
    {
      var sent = ModelReads(Run("Diagnose"));
      Assert.Contains(RunHandOff.NoResult, sent);
    }

    // ---- the context line ---------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_source_that_has_gone_is_counted_and_not_numbered()
    {
      var line = RunHandOff.ContextLine("Explain", At, new[] { Add1, Gone }, NameOf, ChatId);
      Assert.Equal("This conversation carries your Explain of 14:02, which ran on 2 components: add1 (5096); 1 is no longer on the canvas. Its result follows.", line);
    }

    [Fact]
    public void One_component_reads_as_one_and_an_unnamed_one_keeps_its_id()
    {
      Assert.Equal("This conversation carries your Explain of 14:02, which ran on 1 component: (5097). Its result follows.",
        RunHandOff.ContextLine("Explain", At, new[] { Add2 }, _ => null, ChatId));
    }

    [Fact]
    public void A_whole_canvas_run_names_its_first_components_lists_the_next_and_counts_the_rest()
    {
      var many = Enumerable.Range(0, RunHandOff.MaxListed + 10).Select(i => new Guid(i + 1, 0, 0, new byte[8])).ToList();
      var ids = many.Select((g, i) => (g, i + 1)).ToDictionary(x => x.g, x => x.Item2);
      var line = RunHandOff.ContextLine("Reorganise", At, many, g => "c" + ids[g], g => ids[g]);
      Assert.Contains("c1 (1), ", line);
      Assert.Contains(", (" + (RunHandOff.MaxNamed + 1) + "), ", line);
      Assert.DoesNotContain("c" + (RunHandOff.MaxNamed + 1) + " (", line);
      Assert.Contains(", and 10 more. Its result follows.", line);
    }

    // ---- the translation ----------------------------------------------------------------------------------------------------------------------------------------

    private static readonly Func<int, int?> Ids = RunIds.Through(RunLabels, ChatId);

    /// <summary>
    /// Brief EI's addendum, the twentieth's gate: a chat seeded from a deck carried the prose at add1 (5), add2 (6), mul (7) and the quiz lines at add1 (7), add2 (8),
    /// mul (9), so "mul (7)" and "add1 (7)" sat in one message. The deck's lines go through the prose's own translation now, and the whole seed has one number per component.
    /// </summary>
    [Fact]
    public void A_deck_seeds_its_chat_with_one_number_per_component()
    {
      var add1 = Guid.Parse("a0000000-0000-0000-0000-000000000001");
      var add2 = Guid.Parse("a0000000-0000-0000-0000-000000000002");
      var mul = Guid.Parse("a0000000-0000-0000-0000-000000000003");
      var labels = new Dictionary<Guid, int> { [add1] = 7, [add2] = 8, [mul] = 9 };                       // the run's numbers, as the canvas badges and the deck's options show them
      int? chat(Guid g) => g == add1 ? 5 : g == add2 ? 6 : g == mul ? 7 : (int?)null;                  // the chat's, where 7 is mul's
      string name(Guid g) => g == add1 ? "add1" : g == add2 ? "add2" : g == mul ? "mul" : null;
      var deck = new Dwight.UI.DeckState(new[]
      {
        new Dwight.UI.DeckCard { Kind = Dwight.UI.DeckKind.Which, Form = Dwight.UI.DeckForm.ChooseOne, Step = 0, Question = "Which component takes the sum from add1 (7)?", Hint = "h",
          Options = new List<string> { "add2 (8)", "mul (9)" }, TrueSet = new List<int> { 1 } },
        new Dwight.UI.DeckCard { Kind = Dwight.UI.DeckKind.Value, Form = Dwight.UI.DeckForm.Enter, Step = 1, Question = "What does mul (9) output?", Hint = "h", Value = "21" },
        new Dwight.UI.DeckCard { Kind = Dwight.UI.DeckKind.Concept, Form = Dwight.UI.DeckForm.ChooseOne, Step = -1, Question = "Why does mul (9) come last?", Hint = "h",
          Options = new List<string> { "It needs add1 (7) and add2 (8) first", "b", "c", "d" }, TrueSet = new List<int> { 0 }, Dwight = true },
      });
      deck.Answer(0, "o:0"); deck.Answer(1, "v:21"); deck.Answer(2, "o:0");
      var run = new RunResult
      {
        Verb = "Explain", At = At, Sources = new[] { add1, add2, mul }, Labels = labels,
        StepsSummary = "add1 (7) and add2 (8) feed mul (9).", Steps = new[] { "add1 (7) sums a and b.", "mul (9) multiplies the sums." },
        Lesson = deck.Lines(), LessonQuestions = deck.Count,
      };
      var seed = RunHandOff.Seed(run, name, chat);

      var named = System.Text.RegularExpressions.Regex.Matches(seed, @"\b(add1|add2|mul) \((\d+)\)").Cast<System.Text.RegularExpressions.Match>()
        .GroupBy(m => m.Groups[1].Value).ToDictionary(g => g.Key, g => g.Select(m => int.Parse(m.Groups[2].Value)).Distinct().ToList());
      Assert.Equal(new[] { "add1", "add2", "mul" }, named.Keys.OrderBy(k => k).ToArray());
      Assert.Equal(new[] { 5 }, named["add1"]);   // one number each, the chat's, across the context line, the prose and the quiz
      Assert.Equal(new[] { 6 }, named["add2"]);
      Assert.Equal(new[] { 7 }, named["mul"]);
      Assert.Contains("They answered add2 (6); the answer is mul (7) (read from the definition).", seed);
      Assert.Contains("Why does mul (7) come last?", seed);
      Assert.Contains("the answer is It needs add1 (5) and add2 (6) first (Dwight's own reading, not read from the definition).", seed);
    }

    [Fact]
    public void An_id_the_run_issued_becomes_the_chats_id_for_the_same_guid()
    {
      Assert.Equal("The accessDirection text (5096) feeds the GhPython Script (5097).", RunIds.RewriteMarkdown("The accessDirection text (395) feeds the GhPython Script (409).", Ids));
    }

    [Fact]
    public void A_component_the_chat_cannot_place_keeps_its_name_and_loses_its_id()
    {
      Assert.Equal("Stream Filter picks one.", RunIds.RewriteMarkdown("Stream Filter (425) picks one.", Ids));
      Assert.Equal("Stream Filter.", RunIds.RewriteMarkdown("Stream Filter (425).", Ids));
    }

    [Fact]
    public void A_bracketed_number_the_run_never_issued_is_not_left_to_read_as_the_chats_id()
    {
      Assert.Equal("the slider is set to 12 here.", RunIds.RewriteMarkdown("the slider is set to 12 (7) here.", Ids));
    }

    [Fact]
    public void A_list_of_ids_in_one_bracket_is_translated_whole()
    {
      Assert.Equal("the sums (5096, 5097) and nothing else", RunIds.RewriteMarkdown("the sums (395, 409) and nothing else", Ids));
      Assert.Equal("the sums (5096) and", RunIds.RewriteMarkdown("the sums (395, 425) and", Ids));
    }

    [Fact]
    public void Code_and_a_call_are_left_as_written()
    {
      Assert.Equal("`x (395)` and f(395) and ```\n(409)\n``` then (5097)", RunIds.RewriteMarkdown("`x (395)` and f(395) and ```\n(409)\n``` then (409)", Ids));
      Assert.Equal("<code>(395)</code> then <strong>add1</strong> (5096)", RunIds.RewriteHtml("<code>(395)</code> then <strong>add1</strong> (395)", Ids));
    }

    [Fact]
    public void A_tag_is_copied_as_it_is()
    {
      Assert.Equal("<a href=\"x(395)\">add1</a> (5096)", RunIds.RewriteHtml("<a href=\"x(395)\">add1</a> (395)", Ids));
    }

    [Fact]
    public void With_no_translation_every_bracketed_id_goes()
    {
      Assert.Equal("add1 feeds add2.", RunIds.RewriteHtml("add1 (395) feeds add2 (409).", null));
    }

    // ---- the doors, by text (ADR-091): the GH-bound half is the eighteenth's gate ---------------------------------------------------------------------------------

    [Fact]
    public void The_seed_goes_through_the_model_door_and_a_later_door_finds_the_runs_chat_first()
    {
      var door = Between(Repo("Dwight", "UI", "DwightScaffold.cs"), "internal static WebPanel GraduateFrom(", "private static WebPanel ReopenRunThread(");
      InOrder(door, "var threadId = CommandHistory.ThreadForRun(doc, run.RunKey);", "if (threadId != null)", "wp = ReopenRunThread(doc, threadId);",
        "var opened = OpenThread(run.RunKey, HistoryCore.RunThreadTitle(verb, run.At));", "var seed = RunHandOff.Seed(run, g => NameOf(doc, g), g => ChatId(doc, scope, g));",
        "AppendModelMarkdown(wp, doc, threadId, seed);", "var text = draft(RunIds.Through(run.Labels, g => ChatId(doc, scope, g)));", "wp.SetComposerDraft(text);");
      Assert.DoesNotContain("ContextLine", door);
    }

    [Fact]
    public void Both_of_a_runs_doors_hand_off_the_same_run()
    {
      Assert.Contains("private void GraduateToChat() => DwightScaffold.GraduateFrom(HandOff());", Repo("Dwight", "UI", "WebPanel.cs"));
      Assert.Contains("DwightScaffold.GraduateFrom(panel.HandOff(), ids => deck != null ? Dwight.Agent.RunIds.RewriteMarkdown(deck.AskDraft(index), ids) : WalkText.AskLine(index, panel.WalkHtml(index), ids))", Repo("Dwight", "UI", "WalkSession.cs"));
    }

    private static string Repo(params string[] parts)
    {
      var dir = AppContext.BaseDirectory;
      while (dir != null && !File.Exists(Path.Combine(dir, "Dwight.sln"))) dir = Path.GetDirectoryName(dir);
      Assert.NotNull(dir);
      return File.ReadAllText(Path.Combine(new[] { dir }.Concat(parts).ToArray())).Replace("\r\n", "\n");
    }

    private static void InOrder(string source, params string[] parts)
    {
      var at = 0;
      foreach (var part in parts)
      {
        var found = source.IndexOf(part, at, StringComparison.Ordinal);
        Assert.True(found >= 0, "not found in order: " + part);
        at = found + part.Length;
      }
    }

    private static string Between(string source, string from, string to)
    {
      var a = source.IndexOf(from, StringComparison.Ordinal);
      Assert.True(a >= 0, from);
      var b = source.IndexOf(to, a + from.Length, StringComparison.Ordinal);
      Assert.True(b >= 0, to);
      return source.Substring(a, b - a);
    }
  }
}
