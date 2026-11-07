using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dwight.UI;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// Brief DI: notes can be hidden. The rules are pure and held here on fixtures; the paint, the click and the three doors are a shim, held by their text and read at the gate.
  /// Hiding is about the canvas and never about what Dwight knows, so the read a verb sends is held as not asking.
  /// </summary>
  public class NotesVisibilityTests
  {
    private static NoteRect R(string id, double x = 0, double y = 0) => new NoteRect(id, x, y, 100, 50, false);

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

    private static string Notes => Repo("Dwight", "UI", "CanvasNotes.cs");

    // ---- hidden draws nothing and hits nothing -------------------------------------------------------------------------------------------------

    [Fact]
    public void Shown_every_note_is_there_to_draw()
    {
      var all = new[] { R("a"), R("b"), R("c") };
      Assert.Equal(new[] { "a", "b", "c" }, NotesVisibility.Visible(all, true, null).Select(r => r.Id));
    }

    [Fact]
    public void Hidden_nothing_is_drawn_so_a_click_lands_on_nothing()
    {
      var all = new[] { R("a", 0, 0), R("b", 200, 0) };
      var drawn = CanvasNotesCore.Choose(NotesVisibility.Visible(all, false, null), 0, 0, 1000, 1000, out var capped);
      Assert.Empty(drawn);
      Assert.Equal(0, capped);
      Assert.Null(CanvasNotesCore.HitTest(drawn, 10, 10));
    }

    [Fact]
    public void Hidden_the_one_new_note_is_drawn_and_can_be_hit_and_the_rest_stay_away()
    {
      var all = new[] { R("a", 0, 0), R("new", 200, 0), R("c", 400, 0) };
      var drawn = CanvasNotesCore.Choose(NotesVisibility.Visible(all, false, "new"), 0, 0, 1000, 1000, out var capped);
      Assert.Equal(new[] { "new" }, drawn.Select(r => r.Id));
      Assert.Equal(0, capped);
      Assert.Equal("new", CanvasNotesCore.HitTest(drawn, 210, 10));
      Assert.Null(CanvasNotesCore.HitTest(drawn, 10, 10));
    }

    [Fact]
    public void A_revealed_note_that_is_not_there_draws_nothing() =>
      Assert.Empty(NotesVisibility.Visible(new[] { R("a") }, false, "gone"));

    // ---- the chip ------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Hidden_the_chip_counts_the_documents_notes()
    {
      Assert.Equal("3 notes", NotesVisibility.Chip(false, 3, 0));
      Assert.Equal("1 note", NotesVisibility.Chip(false, 1, 0));
      Assert.Null(NotesVisibility.Chip(false, 0, 0));
    }

    [Fact]
    public void Shown_the_chip_is_only_what_the_cap_left_out()
    {
      Assert.Equal("and 5 more notes", NotesVisibility.Chip(true, 45, 5));
      Assert.Equal("and 1 more note", NotesVisibility.Chip(true, 41, 1));
      Assert.Null(NotesVisibility.Chip(true, 3, 0));
    }

    [Fact]
    public void A_click_on_the_chip_shows_them_only_when_it_is_the_count_of_hidden_ones()
    {
      Assert.True(NotesVisibility.ChipClickShows(false, 3));
      Assert.False(NotesVisibility.ChipClickShows(false, 0));
      Assert.False(NotesVisibility.ChipClickShows(true, 45));
    }

    // ---- the new note --------------------------------------------------------------------------------------------------------------------------

    private static readonly DateTime T0 = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void With_nothing_added_nothing_is_revealed() => Assert.Null(new NoteReveal().At(T0));

    /// <summary>The redraw that closing the note's window causes must not take it away before anyone saw it.</summary>
    [Fact]
    public void The_new_note_survives_the_redraws_right_after_it_lands()
    {
      var reveal = new NoteReveal();
      reveal.Set("n");
      Assert.Equal("n", reveal.At(T0));
      Assert.Equal("n", reveal.At(T0.AddSeconds(0.1)));
      Assert.Equal("n", reveal.At(T0.AddSeconds(NoteReveal.MinSeconds - 0.1)));
    }

    [Fact]
    public void The_next_redraw_past_the_floor_ends_it_for_good()
    {
      var reveal = new NoteReveal();
      reveal.Set("n");
      reveal.At(T0);
      Assert.Null(reveal.At(T0.AddSeconds(NoteReveal.MinSeconds)));
      Assert.Null(reveal.At(T0.AddSeconds(0.1)));
    }

    /// <summary>The clock starts at its first paint, however long the person took to get there: no redraw, and it stays.</summary>
    [Fact]
    public void The_clock_starts_at_the_first_paint_not_when_it_was_set()
    {
      var reveal = new NoteReveal();
      reveal.Set("n");
      Assert.Equal("n", reveal.At(T0.AddHours(1)));
      Assert.Equal("n", reveal.At(T0.AddHours(1).AddSeconds(1)));
    }

    [Fact]
    public void Clearing_or_a_second_note_replaces_it()
    {
      var reveal = new NoteReveal();
      reveal.Set("n");
      reveal.At(T0);
      reveal.Set("m");
      Assert.Equal("m", reveal.At(T0.AddSeconds(10)));
      reveal.Clear();
      Assert.Null(reveal.At(T0.AddSeconds(11)));
    }

    // ---- one setting, three doors ---------------------------------------------------------------------------------------------------------------

    /// <summary>Brief EU: on the chord the switch is a ticked item under Notes, never a second wording, and it is called what the other doors call it. The menu is
    /// built in Dwight.Menu.Tests, which cannot compile this core, so the two names are held equal here.</summary>
    [Fact]
    public void The_chords_ticked_item_is_called_what_the_switch_is_called() =>
      Assert.Contains("ShowNotes = \"" + NotesVisibility.Label + "\";", Repo("Dwight", "UI", "ChordMenu.cs"));

    [Fact]
    public void It_is_on_by_default_and_an_absent_field_reads_as_on() =>
      Assert.Contains("public bool ShowNotes { get; set; } = true;", Repo("Dwight", "Config", "DwightSettings.cs"));

    /// <summary>Every door goes through one method, so no door can change the setting without the canvas and the other doors hearing.</summary>
    [Fact]
    public void The_one_setting_has_one_way_to_change()
    {
      var set = Between(Notes, "public static void SetShown(bool on)", "A note was just added");
      Assert.Contains("if (st.ShowNotes == on) return;", set);
      Assert.Contains("st.ShowNotes = on;", set);
      Assert.Contains("st.Save();", set);
      Assert.Contains("_reveal.Clear();", set);
      Assert.Contains("Invalidate();", set);
      Assert.Contains("ShownChanged?.Invoke();", set);
      Assert.Contains("public static bool Shown => DwightSettings.Current.ShowNotes;", Notes);

      // Brief EU: the chord reads the setting as it builds and its ticked Show notes asks the same door. The menu's shape, the tick and the click are held
      // on the built menu in Dwight.Menu.Tests; here, that the chord hands it the live state and the one door.
      var chord = Repo("Dwight", "DwightCanvasPopup.cs");
      Assert.Contains("Dwight.UI.ChordMenu.Build(DwightActions.CurrentSelection().Count, Dwight.UI.CanvasNotes.Shown, ", chord);
      Assert.Contains("SetNotesShown = Dwight.UI.CanvasNotes.SetShown,", chord);
      Assert.Contains("Sub(notes, ShowNotes, () => doors.SetNotesShown(!notesShown)).Checked = notesShown;", Repo("Dwight", "UI", "ChordMenu.cs"));

      var dock = Repo("Dwight", "UI", "DwightDock.cs");
      Assert.Contains("if (cmd.StartsWith(\"notes:\", StringComparison.Ordinal)) { CanvasNotes.SetShown(cmd.StartsWith(\"notes:on\", StringComparison.Ordinal)); return; }", dock);
      Assert.Contains("CanvasNotes.ShownChanged += () => { try { _instance?.PushNotes(); } catch { } };", dock);
      Assert.Contains("PushNotes();", Between(dock, "public void Refresh()", "_view = CommandHistory.ForActiveFileView();"));

      var settings = Repo("Dwight", "UI", "DwightSettingsPanel.cs");
      Assert.Contains("if (v == \"on\" || v == \"off\") CanvasNotes.SetShown(v == \"on\");", settings);
      Assert.Contains("[\"showNotes\"] = s.ShowNotes,", settings);
      Assert.Contains("CanvasNotes.ShownChanged += onNotes;", settings);
      Assert.Contains("CanvasNotes.ShownChanged -= onNotes;", settings);
    }

    [Fact]
    public void The_dock_row_and_settings_use_the_librarys_switch_and_settings_says_what_hiding_means()
    {
      var dock = Repo("Dwight", "Resources", "dock-shell.html");
      Assert.Contains("<div class=\"dset\"><div class=\"swrow\"><label>Show notes</label><button class=\"sw\" id=\"dnotes\" role=\"switch\" aria-label=\"Show notes\" aria-checked=\"true\">", dock);
      Assert.InRange(dock.IndexOf("id=\"timeline\"", StringComparison.Ordinal), 0, dock.IndexOf("class=\"dset\"", StringComparison.Ordinal));
      Assert.InRange(dock.IndexOf("<div id=\"notes\" class=\"dpane dnotelist\">", StringComparison.Ordinal), 0, dock.IndexOf("class=\"dset\"", StringComparison.Ordinal));   // Brief ED: at the Notes pane's head

      var html = Repo("Dwight", "Resources", "settings.html");
      var pane = Between(html, "data-pane=\"Notes\"", "data-pane=\"You\"");
      Assert.Contains("<h1 class=\"navtitle\">Notes</h1>", pane);
      Assert.Contains("<button class=\"sw\" id=\"shownotes\" role=\"switch\"", pane);
      Assert.Contains("Hiding notes only takes them off the canvas. Dwight still reads them, and a note still goes to your provider with the component it is on.", pane);
      Assert.Contains("dwightShowNotes(d.showNotes!==false);", html);
    }

    // ---- the paint and the click ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_paint_counts_first_and_hidden_returns_with_nothing_to_hit()
    {
      var paint = Between(Notes, "private static void OnPaint(GH_Canvas sender)", "private static NoteRect DrawCard(");
      Assert.Contains("_total = notes.Count;", paint);
      Assert.InRange(paint.IndexOf("_total = notes.Count;", StringComparison.Ordinal), 0, paint.IndexOf("CardsAtZoom", StringComparison.Ordinal));   // Brief ED: the zoom gate is now the card-or-dot choice
      Assert.Contains("var revealed = show ? null : _reveal.At(DateTime.UtcNow);", paint);
      Assert.Contains("if (!show && revealed == null) return;", paint);
      Assert.Contains("NotesVisibility.Visible(rough, show, revealed)", paint);
    }

    [Fact]
    public void The_chip_says_the_rules_words_and_a_click_on_it_goes_through_the_one_door()
    {
      var overlay = Between(Notes, "private static void OnOverlay(GH_Canvas sender)", "private static void OnDown(");
      Assert.Contains("var text = NotesVisibility.Chip(Shown, _total, _hidden);", overlay);
      Assert.Contains("_chip = RectangleF.Empty;", overlay);
      Assert.Contains("_chip = NoteDotPainter.DrawChip(sender.Graphics, text, sender.Height, Dark);", overlay);

      var up = Between(Notes, "private static void OnUp(object sender, MouseEventArgs e)", "A click on a note selects");
      Assert.Contains("if (NotesVisibility.ChipClickShows(Shown, _total) && _chip.Contains(e.Location)) { SetShown(true); return; }", up);
      Assert.InRange(up.IndexOf("ChipClickShows", StringComparison.Ordinal), 0, up.IndexOf("_drawn.Count == 0", StringComparison.Ordinal));
    }

    [Fact]
    public void A_new_note_shows_itself_only_while_hidden_and_whichever_way_it_was_added()
    {
      Assert.Contains("public static void Reveal(string noteId) { if (!Shown) _reveal.Set(noteId); }", Notes);
      Assert.Contains("_reveal.Clear();", Between(Notes, "private static void HookSolution(GH_Document doc)", "private static NoteStatus StatusOf("));

      var panel = Repo("Dwight", "UI", "DwightNotePanel.cs");
      Assert.InRange(panel.IndexOf("CanvasNotes.Reveal(made.Id);", StringComparison.Ordinal), 0, panel.IndexOf("CommandHistory.AddNote(_doc, made);", StringComparison.Ordinal));
      var pin = Repo("Dwight", "UI", "WebPanel.cs");
      Assert.InRange(pin.IndexOf("CanvasNotes.Reveal(note.Id);", StringComparison.Ordinal), 0, pin.IndexOf("Dwight.Session.CommandHistory.AddNote(doc, note);", StringComparison.Ordinal));
    }

    // ---- what Dwight knows does not change ------------------------------------------------------------------------------------------------------

    /// <summary>Hiding is about the canvas. The read that puts notes in a payload, and the store it reads, never ask the setting, so a hidden note still rides every read and the privacy page's sentence stands.</summary>
    [Fact]
    public void The_read_a_verb_sends_never_asks_whether_notes_are_shown()
    {
      foreach (var file in new[] { Repo("Dwight", "Agent", "GhNotes.cs"), Repo("Dwight", "Session", "CommandHistory.cs"), Repo("Dwight", "Data", "PayloadContract.cs") })
      {
        Assert.DoesNotContain("ShowNotes", file);
        Assert.DoesNotContain("CanvasNotes", file);
        Assert.DoesNotContain("NotesVisibility", file);
      }
      Assert.Contains("var notes = CommandHistory.Notes(doc);", Repo("Dwight", "Agent", "GhNotes.cs"));
    }
  }
}
