using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dwight.Session;
using Dwight.UI;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>Brief DO, PR 1: every verb offers its note, in one click, a note remembers where it came from, the card is the panel's look, and the editor's tools write the subset. The words are pure and held on fixtures; the shim is held by its text and read at the gate.</summary>
  public class NoteOffersTests
  {
    private static string Repo(params string[] parts)
    {
      var dir = new DirectoryInfo(AppContext.BaseDirectory);
      while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Dwight.sln"))) dir = dir.Parent;
      Assert.NotNull(dir);
      return File.ReadAllText(Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray())).Replace("\r\n", "\n");
    }

    private static string Between(string text, string from, string to)
    {
      var a = text.IndexOf(from, StringComparison.Ordinal);
      Assert.True(a >= 0, from);
      var b = text.IndexOf(to, a + from.Length, StringComparison.Ordinal);
      Assert.True(b >= 0, to);
      return text.Substring(a, b - a);
    }

    // ---- an Explain step ------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_step_keeps_its_marks() =>
      Assert.Equal("**Remap** squeezes it, then `Loft`.", NoteOfferText.Step("  **Remap** squeezes it, then `Loft`.  ", "<strong>Remap</strong> squeezes it"));

    [Fact]
    public void A_row_from_an_older_build_has_only_its_sentence_as_plain_text() =>
      Assert.Equal("Remap squeezes it", NoteOfferText.Step(null, "<strong>Remap</strong> squeezes it"));

    [Fact]
    public void A_step_is_never_more_than_a_note_holds()
    {
      var text = NoteOfferText.Step(string.Join(" ", Enumerable.Repeat("word", 1000)), null);
      Assert.True(text.Length <= DwightNotes.MaxChars);
      Assert.EndsWith("...", text);
    }

    [Fact]
    public void The_steps_view_gives_each_steps_markdown_and_the_summary()
    {
      var view = new JsonObject
      {
        ["summary"] = "A loft.",
        ["steps"] = new JsonArray(
          new JsonObject { ["text"] = "<code>a</code>", ["md"] = "`a`" },
          new JsonObject { ["text"] = "<b>old</b> row" }),
      }.ToJsonString();
      var steps = NoteOfferText.Steps(view, out var summary);
      Assert.Equal("A loft.", summary);
      Assert.Equal(new[] { "`a`", "old row" }, steps);
    }

    [Fact]
    public void A_broken_view_offers_nothing_and_does_not_throw()
    {
      Assert.Empty(NoteOfferText.Steps("{not json", out var summary));
      Assert.Null(summary);
    }

    [Fact]
    public void The_run_keeps_a_steps_markdown_for_the_pin()
    {
      Assert.Contains("[\"md\"] = text,", Between(Repo("Dwight", "LlmActions.cs"), "private static string TranslateSteps(", "private static JsonArray TranslateMarks("));
      Assert.Contains("NoteOfferText.Steps(stepsJson, out summary)", Repo("Dwight", "UI", "WebPanel.cs"));
    }

    // ---- Reorganise -----------------------------------------------------------------------------------------------------------------------------

    /// <summary>The undo clause is true when it is said and stale in a note, and the verified clause is the point.</summary>
    [Fact]
    public void Reorganise_says_its_own_facts_without_the_undo_clause()
    {
      var note = NoteOfferText.Reorganise("Organised 20 components into 4 labelled groups (compact). Wiring and values are unchanged (verified); one Ctrl-Z reverts the arrangement.");
      Assert.Equal("Organised 20 components into 4 labelled groups (compact). Wiring and values are unchanged (verified).", note);
    }

    [Fact]
    public void A_summary_with_no_undo_clause_stands_and_none_offers_nothing()
    {
      Assert.Equal("Tidied.", NoteOfferText.Reorganise("Tidied."));
      Assert.Null(NoteOfferText.Reorganise("  "));
      Assert.Null(NoteOfferText.Reorganise(null));
    }

    [Fact]
    public void The_real_summaries_carry_the_clause_this_cuts_at()
    {
      var writer = Repo("Dwight", "Data", "CanvasWriter.cs");
      Assert.Contains("Wiring and values are unchanged (verified); one Ctrl-Z reverts the arrangement.", writer);
      Assert.Contains("Wiring and values are unchanged (verified); one Ctrl-Z reverts the layout and the wire display.", writer);
    }

    // ---- Simplify -------------------------------------------------------------------------------------------------------------------------------

    private static string Applied(params string[] names) => new JsonObject
    {
      ["summary"] = "2 removed, 1 added.",
      ["changes"] = new JsonArray(names.Select(n => (JsonNode)new JsonObject { ["text"] = n }).ToArray()),
    }.ToJsonString();

    [Fact]
    public void Simplify_names_what_it_applied() =>
      Assert.Equal("2 removed, 1 added.\n\n- Merge the two domains\n- Name the slider", NoteOfferText.Simplify(Applied("Merge the two domains", "Name the slider")));

    [Fact]
    public void A_name_is_one_bullet_however_it_was_written() =>
      Assert.EndsWith("\n- one two", NoteOfferText.Simplify(Applied("one\ntwo")));

    [Fact]
    public void Nothing_applied_offers_nothing()
    {
      Assert.Null(NoteOfferText.Simplify(Applied()));
      Assert.Null(NoteOfferText.Simplify(Applied("  ")));
      Assert.Null(NoteOfferText.Simplify("{not json"));
    }

    // ---- Diagnose -------------------------------------------------------------------------------------------------------------------------------

    private static readonly Guid Root = new Guid("11111111-2222-3333-4444-555555555555");

    private static string Faults(string message, string reading, string fix) => new JsonObject
    {
      ["chains"] = new JsonArray(new JsonObject
      {
        ["root"] = new JsonObject { ["title"] = "Loft (12)", ["guid"] = Root.ToString(), ["ghMessage"] = message, ["causeMd"] = reading, ["fixMd"] = fix },
      }),
    }.ToJsonString();

    /// <summary>A Dwight note says who wrote what: what the person read, with each line's author. The plugin's fact comes first, as the scan found it, and each of the model's lines is labelled as the model's.</summary>
    [Fact]
    public void Diagnose_holds_what_was_read_with_who_wrote_each_line()
    {
      var note = NoteOfferText.Diagnose(Faults("Input parameter S failed to collect data", "The `Curves` input has no source.", "Wire it from `Divide`."), out var anchor);
      Assert.Equal("**Root cause (the scan):** Loft (12): Input parameter S failed to collect data\n\n**The model's reading:** The `Curves` input has no source.\n\n**Suggested fix (the model's):** Wire it from `Divide`.", note);
      Assert.Equal(Root, anchor);
    }

    [Fact]
    public void A_silent_failure_has_no_message_and_an_empty_line_has_no_label()
    {
      Assert.Equal("**Root cause (the scan):** Loft (12)\n\n**The model's reading:** r\n\n**Suggested fix (the model's):** f", NoteOfferText.Diagnose(Faults(null, "r", "f"), out _));
      Assert.Equal("**Root cause (the scan):** Loft (12): m\n\n**Suggested fix (the model's):** f", NoteOfferText.Diagnose(Faults("m", "", "f"), out _));
      Assert.Equal("**Root cause (the scan):** Loft (12): m\n\n**The model's reading:** r", NoteOfferText.Diagnose(Faults("m", "r", ""), out _));
      Assert.Equal("**Root cause (the scan):** Loft (12): m", NoteOfferText.Diagnose(Faults("m", "", ""), out _));
    }

    [Fact]
    public void A_clean_run_offers_nothing()
    {
      Assert.Null(NoteOfferText.Diagnose(new JsonObject { ["chains"] = new JsonArray() }.ToJsonString(), out var anchor));
      Assert.Null(anchor);
      Assert.Null(NoteOfferText.Diagnose("{not json", out _));
    }

    [Fact]
    public void A_root_with_no_name_offers_nothing()
    {
      var nameless = new JsonObject { ["chains"] = new JsonArray(new JsonObject { ["root"] = new JsonObject { ["title"] = "  ", ["guid"] = Root.ToString() } }) }.ToJsonString();
      Assert.Null(NoteOfferText.Diagnose(nameless, out _));
    }

    [Fact]
    public void The_faults_view_keeps_grasshoppers_message_and_the_fixs_markdown()
    {
      var actions = Repo("Dwight", "LlmActions.cs");
      Assert.Contains("[\"ghMessage\"] = (f.Node.Msgs.FirstOrDefault(m => m.Level == \"error\") ?? f.Node.Msgs.FirstOrDefault())?.Text,", actions);
      Assert.Contains("[\"causeMd\"] = cause,", actions);
      Assert.Contains("[\"fixMd\"] = fix,", actions);
    }

    // ---- each verb makes its offer --------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Every_verb_offers_on_its_result_and_each_offer_is_only_a_button()
    {
      var actions = Repo("Dwight", "LlmActions.cs");
      Assert.Contains("(popup as WebPanel)?.OfferNote(cause, causeAnchor, NoteOfferText.PinTheCause);", actions);
      Assert.Contains("w.OfferNote(NoteOfferText.Reorganise(summary), FirstStanding(arrange.Doc, arrange.GuidToCluster?.Keys), NoteOfferText.PinWhatChanged);", actions);
      Assert.Contains("wp.OfferNote(NoteOfferText.Reorganise(outcome.Summary), FirstStanding(st.Doc, st.GuidToCluster?.Keys), NoteOfferText.PinWhatChanged);", actions);
      Assert.Contains("if (inPlace) w.OfferNote(NoteOfferText.Simplify(applied), FirstStanding(st?.Doc, st?.Rc?.IdToGuid?.Values), NoteOfferText.PinWhatChanged);", actions);

      var offer = Between(Repo("Dwight", "UI", "WebPanel.cs"), "internal void OfferNote(string text, Guid? anchor, string title)", "\"Pin as a note\" on a step");
      Assert.Contains("if (string.IsNullOrWhiteSpace(text)) return;", offer);
      Assert.Contains("NoteOffers.Register(text, anchor, Dwight.Session.DwightNotes.ByDwight, RunId)", offer);
      Assert.Contains("dwightPinOffer(", offer);
      Assert.DoesNotContain("AddNote", offer);
    }

    [Fact]
    public void A_note_about_a_change_anchors_to_something_still_there()
    {
      var first = Between(Repo("Dwight", "LlmActions.cs"), "private static Guid? FirstStanding(", "private static string BuildSimplifyAppliedJson(");
      Assert.Contains("if (doc?.FindObject(g, false) != null) return g;", first);
    }

    [Fact]
    public void The_glyph_on_a_result_stays_in_view_and_keeps_its_click_off_the_card()
    {
      var shell = Repo("Dwight", "Resources", "panel-shell.html");
      Assert.Contains("event.stopPropagation();dwightSend(\\'pin:'+esc(arg)+'\\')", shell);
      Assert.Contains("_pinGlyph(token,title,' shown')", shell);
      Assert.Contains("<use href=\"#ic-notepin\"/>", shell);   // the name "pin" in the table is the Settings rail's sidebar
      Assert.Contains("notepin: 'pinned'", Repo("tools", "build-icon-sprite.js"));
      Assert.Contains("id=\"ic-notepin\"", Repo("Dwight", "Resources", "dwight-icons.svg"));
      Assert.Contains(".pincorner.shown{opacity:1}", Repo("Dwight", "Resources", "dwight-components.css"));
    }

    // ---- a note remembers where it came from ----------------------------------------------------------------------------------------------------

    [Fact]
    public void An_offer_carries_its_run_to_the_note()
    {
      var token = NoteOffers.Register("words", null, "dwight", "run-7");
      Assert.True(NoteOffers.TryGet(token, out _, out _, out _, out var fromRun));
      Assert.Equal("run-7", fromRun);
    }

    [Fact]
    public void Every_dwight_note_is_made_with_its_run_and_a_persons_with_none()
    {
      var panel = Repo("Dwight", "UI", "WebPanel.cs");
      Assert.Contains("PinNote(doc, anchor, text, fromRun);", panel);
      Assert.Contains("PinNote(doc, anchor, text, RunId);", panel);
      Assert.Contains("text, Dwight.Session.DwightNotes.ByDwight, fromRun, out _);", panel);
      Assert.Contains("MakeNew(_doc, _anchor, _point, text, DwightNotes.ByYou, null, out var why)", Repo("Dwight", "UI", "DwightNotePanel.cs"));
      Assert.Contains("NoteOffers.Register(sentence, first, Dwight.Session.DwightNotes.ByDwight, threadId);", Repo("Dwight", "UI", "DwightScaffold.cs"));
    }

    [Fact]
    public void A_run_reaches_its_panel_and_its_row_and_a_reopened_row_keeps_it()
    {
      var session = Repo("Dwight", "DwightSession.cs");
      Assert.Equal(2, Regex.Matches(session, @"var row = new DwightCommand\n\s*\{\n\s*RunId = ctx\.RunId,").Count);
      Assert.Contains("if (ctx.Panel is WebPanel running) running.RunId = ctx.RunId;", session);
      Assert.Contains("wp.RunId = cmd.RunId;", session);

      var history = Repo("Dwight", "Session", "CommandHistory.cs");
      Assert.Contains("RunId = c.RunId,", history);
      Assert.Contains("RunId = s.RunId,", history);
      Assert.Contains("[JsonPropertyName(\"runId\")] public string RunId { get; set; }", Repo("Dwight", "Session", "SessionStoreCore.cs"));
    }

    // ---- the card --------------------------------------------------------------------------------------------------------------------------------

    /// <summary>The panel's card, from the same tokens, and never a scribble's look. A literal colour here would drift from the panel in silence.</summary>
    [Fact]
    public void The_card_is_the_panels_look_from_the_tokens()
    {
      var card = Repo("Dwight", "UI", "NoteCard.cs");
      Assert.Contains("UiTokens.Surface(dark)", card);
      Assert.Contains("UiTokens.BorderStrong(dark)", card);
      Assert.Contains("UiTokens.ShadowInk(dark)", card);
      Assert.Contains("UiTokens.Inset(dark)", card);
      Assert.Contains("byDwight ? accent : UiTokens.Text(dark)", card);
      Assert.Contains("byDwight ? \"Dwight\" : \"You\"", card);
      Assert.DoesNotContain("FromArgb", card);
      Assert.DoesNotContain("Rounded", card);
    }

    [Fact]
    public void The_card_is_laid_out_once_for_its_words_and_drawn_from_that()
    {
      var notes = Repo("Dwight", "UI", "CanvasNotes.cs");
      Assert.Contains("if (_layouts.TryGetValue(note.Id, out var kept) && kept.Text == note.Text) return kept.Layout;", notes);
      Assert.Contains("NoteLayout.Build(NoteMarkdown.Parse(note.Text), new GdiNoteMetrics(g))", notes);
      Assert.Contains("_layouts.Clear();", notes);
      Assert.Contains("NoteCard.Draw(g, origin, layout, note.By == DwightNotes.ByDwight, DateLabel(note.WhenUtc), stale, dark);", notes);
      Assert.Contains("NoteLayout.CardWidth(layout), NoteLayout.CardHeight(layout, st != NoteStatus.Fresh)", notes);
    }

    /// <summary>The click that opens a note is the card's whole rectangle, so the "and more" line opens it too, and the panel shows the whole text through the one renderer.</summary>
    [Fact]
    public void A_note_opens_rendered_whole_through_the_one_renderer()
    {
      Assert.Contains("[\"html\"] = note != null ? ModelMarkdown.ToHtml(note.Text) : null,", Repo("Dwight", "UI", "DwightNotePanel.cs"));
      Assert.Contains("DwightNotePanel.Open(doc, note);", Repo("Dwight", "UI", "CanvasNotes.cs"));
    }

    // ---- the editor -----------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_editor_has_its_tools_and_its_bound()
    {
      var note = Repo("Dwight", "Resources", "note.html");
      Assert.Contains("onclick=\"noteWrap('**')\"", note);
      Assert.Contains("onclick=\"noteWrap('`')\"", note);
      Assert.Contains("onclick=\"noteBullets()\"", note);
      Assert.Contains("{value:'4',name:'4 columns'}", note);
      Assert.DoesNotContain("{value:'5'", note);
      Assert.Contains("maxlength=\"4000\"", note);
      Assert.Contains("' of 4000'", note);
      Assert.Equal(4000, DwightNotes.MaxChars);
      Assert.Equal(NoteMarkdown.MaxColumns, 4);
    }
  }
}
