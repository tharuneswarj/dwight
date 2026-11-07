using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Dwight.Session;
using Dwight.UI;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// Brief ED, from Tharun's sitting on the seventeenth build: a note too small to read is a dot, the dock has a Notes pane, a note moves by its header and is pinned or
  /// free by choice, and lists behave. The rules are pure and held here on fixtures; the paint, the mouse and the two windows are a shim, held by their text and read at the
  /// eighteenth gate.
  /// </summary>
  public class NotesSittingTests
  {
    /// <summary>Every character 6 wide, so "00." is 18 and a plain line holds 36.</summary>
    private sealed class Fixed : INoteMetrics
    {
      public double Width(string text, NoteStyle style) => (text ?? "").Length * 6;
    }

    private static NoteLayoutResult Lay(string text) => NoteLayout.Build(NoteMarkdown.Parse(text), new Fixed());

    private static string Plain(IEnumerable<NoteRun> runs) => string.Concat(runs.Select(r => r.Text));

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

    private static NoteRect R(string id, double x, double y, double w = 240, double h = 60) => new NoteRect(id, x, y, w, h, false);

    private static DotSource S(string id, double sx, double sy, NoteStatus status = NoteStatus.Fresh) => new DotSource(R(id, sx * 10, sy * 10), sx, sy, status);

    private static IReadOnlyList<NoteDot> Merge(params DotSource[] s) => NoteDots.Merge(s, 1000, 800, t => t.Length * 6.3);

    // ---- unit 1: below a size, a note is a dot -------------------------------------------------------------------------------------------------

    [Fact]
    public void A_plain_card_is_a_dot_at_zoom_0_39_and_a_card_at_0_4()
    {
      var w = NoteLayout.CardWidth(Lay("A short note."));
      Assert.Equal(240, w);
      Assert.False(CanvasNotesCore.CardsAtZoom(0.39, w));
      Assert.True(CanvasNotesCore.CardsAtZoom(0.4, w));
      Assert.True(CanvasNotesCore.CardsAtZoom(0.4f, w));   // the canvas's own zoom is a float
      Assert.Equal(96, CanvasNotesCore.CardMinScreenWidth);
    }

    [Fact]
    public void A_table_cards_threshold_follows_from_its_width()
    {
      var w = NoteLayout.CardWidth(Lay("| a | b |\n|---|---|\n| 1 | 2 |"));
      Assert.Equal(340, w);
      var threshold = CanvasNotesCore.CardMinScreenWidth / w;   // about 0.28
      Assert.True(CanvasNotesCore.CardsAtZoom(threshold + 0.001, w));
      Assert.False(CanvasNotesCore.CardsAtZoom(threshold - 0.001, w));
      Assert.True(CanvasNotesCore.CardsAtZoom(0.3, w));          // still a card where a plain one is a dot
      Assert.False(CanvasNotesCore.CardsAtZoom(0.3, 240));
    }

    [Fact]
    public void Dots_within_fourteen_pixels_merge_and_carry_their_count()
    {
      Assert.Equal(14, NoteDots.MergeDistance);
      var merged = Assert.Single(Merge(S("a", 100, 100), S("b", 114, 100)));
      Assert.True(merged.Merged);
      Assert.Equal(2, merged.Count);
      Assert.Equal(107, merged.X);
      Assert.Equal(new[] { "a", "b" }, merged.Ids);

      var apart = Merge(S("a", 100, 100), S("b", 114.1, 100));
      Assert.Equal(2, apart.Count);
      Assert.All(apart, d => Assert.False(d.Merged));
      Assert.All(apart, d => Assert.Equal(NoteDots.Diameter, d.Diameter));
    }

    [Fact]
    public void A_chain_of_near_dots_is_one_dot_in_whatever_order_they_come()
    {
      var a = S("a", 100, 100); var b = S("b", 112, 100); var c = S("c", 124, 100);   // a and c are 24 apart, each 12 from b
      foreach (var order in new[] { new[] { a, b, c }, new[] { c, a, b }, new[] { a, c, b } })
        Assert.Equal(3, Assert.Single(Merge(order)).Count);

      // Four in a line, each 12 from the next, given ends first: the joins leave the third pointing at the second, which points at the first.
      Assert.Equal(4, Assert.Single(Merge(S("w", 136, 100), S("x", 100, 100), S("y", 112, 100), S("z", 124, 100))).Count);
    }

    [Fact]
    public void Only_dots_on_the_screen_count_so_the_count_says_how_many_are_here()
    {
      var dots = Merge(S("on", 10, 10), S("off", -50, 10), S("below", 10, 900));
      Assert.Equal("on", Assert.Single(dots).Ids.Single());
    }

    [Fact]
    public void A_merged_dot_is_wide_enough_for_its_count()
    {
      Assert.Equal(NoteDots.MergedMinDiameter, NoteDots.MergedDiameter(6.3));
      Assert.Equal(13.6 + 2 * NoteDots.CountPad, NoteDots.MergedDiameter(13.6), 6);
      Assert.Equal(16, NoteDots.MergedMinDiameter);
      Assert.Equal(10, NoteDots.Diameter);
    }

    [Fact]
    public void A_click_lands_on_a_dot_only_inside_its_own_circle_and_the_top_one_takes_it()
    {
      var dots = Merge(S("a", 100, 100), S("b", 300, 100));
      Assert.Equal("a", NoteDots.At(dots, 100, 104.9).Ids.Single());
      Assert.Null(NoteDots.At(dots, 100, 105.1));        // a dot of 10 has a radius of 5: no halo for a finger
      Assert.Null(NoteDots.At(dots, 200, 100));
      Assert.Null(NoteDots.At(null, 0, 0));

      var stack = new[] { new NoteDot(new[] { S("under", 0, 0) }, 50, 50, 10), new NoteDot(new[] { S("over", 0, 0) }, 52, 50, 10) };
      Assert.Equal("over", NoteDots.At(stack, 51, 50).Ids.Single());   // drawn last, so on top
    }

    [Fact]
    public void A_dot_uses_the_cards_colours_for_its_state()
    {
      Assert.Equal(NoteDotLook.Filled, NoteDots.LookOf(NoteStatus.Fresh));
      Assert.Equal(NoteDotLook.Hollow, NoteDots.LookOf(NoteStatus.Stale));
      Assert.Equal(NoteDotLook.HollowDashed, NoteDots.LookOf(NoteStatus.Gone));
      Assert.Equal(NoteDotLook.HollowDashed, Assert.Single(Merge(S("g", 10, 10, NoteStatus.Gone))).Look);
      Assert.Equal(NoteDotLook.Filled, Assert.Single(Merge(S("g", 10, 10, NoteStatus.Gone), S("s", 12, 10, NoteStatus.Stale))).Look);   // a count is read, so it is filled
    }

    [Fact]
    public void A_merged_dots_click_zooms_until_the_nearest_two_are_a_card_apart_and_centres_on_them()
    {
      var (zoom, x, y) = NoteDots.Separate(new[] { R("a", 0, 0), R("b", 100, 0) }, 0.1);
      Assert.Equal(0.96, zoom, 6);                                     // 100 units apart at 96 px: side by side
      Assert.Equal((0 + 340) / 2.0, x, 6);                              // the middle of both cards, not of their corners
      Assert.Equal(30, y, 6);
    }

    [Fact]
    public void The_separating_zoom_is_never_less_than_the_cards_own_nor_more_than_one_nor_less_than_now()
    {
      Assert.Equal(0.4, NoteDots.Separate(new[] { R("a", 0, 0), R("b", 1000, 0) }, 0.1).Zoom, 6);   // far apart: the cards' zoom
      Assert.Equal(1, NoteDots.Separate(new[] { R("a", 0, 0), R("b", 0, 0) }, 0.1).Zoom, 6);        // one corner: capped at the default zoom
      Assert.Equal(1, NoteDots.Separate(new[] { R("a", 0, 0), R("b", 50, 0) }, 0.1).Zoom, 6);
      Assert.Equal(0.5, NoteDots.Separate(new[] { R("a", 0, 0), R("b", 1000, 0, 340) }, 0.5).Zoom, 6);   // never further out than now
      Assert.Equal(1, NoteDots.MaxSeparateZoom);
    }

    [Fact]
    public void Every_note_is_a_dot_up_to_four_hundred_and_the_chip_says_the_rest()
    {
      Assert.Equal(400, NoteDots.MaxDots);
      var all = Enumerable.Range(0, 450).Select(i => R("n" + i.ToString("D3"), i * 5, 0)).ToList();
      var dots = CanvasNotesCore.Choose(all, 0, 0, 10000, 10000, out var past, NoteDots.MaxDots);
      Assert.Equal(400, dots.Count);
      Assert.Equal(50, past);
      Assert.Equal("and 50 more notes", NotesVisibility.Chip(true, 450, past));
    }

    [Fact]
    public void A_press_moves_a_card_only_on_its_own_header()
    {
      Assert.Equal(22, CanvasNotesCore.HeaderHeight);
      var drawn = new[] { R("top", 0, 0, 240, 100), R("under", 0, 90, 240, 100) };   // top-first
      Assert.Equal("top", CanvasNotesCore.HeaderAt(drawn, 10, 22));
      Assert.Null(CanvasNotesCore.HeaderAt(drawn, 10, 22.1));
      Assert.Null(CanvasNotesCore.HeaderAt(drawn, 10, 95));       // the top card's body, over the lower card's header
      Assert.Equal("under", CanvasNotesCore.HeaderAt(drawn, 10, 105));
      Assert.Null(CanvasNotesCore.HeaderAt(drawn, 300, 10));
      Assert.Null(CanvasNotesCore.HeaderAt(null, 0, 0));
    }

    // ---- unit 2: the Notes pane ----------------------------------------------------------------------------------------------------------------

    private static readonly DateTime Now = new DateTime(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc);

    private static StoredNote Note(string id, string text, string by = "you", double minutesAgo = 5, string anchor = null) => new StoredNote
    {
      Id = id, Text = text, By = by, Anchor = anchor, At = new[] { 0.0, 0.0 },
      WhenUtc = DwightNotes.Now(Now.AddMinutes(-minutesAgo)),
    };

    [Fact]
    public void The_pane_lists_the_files_notes_newest_first_in_the_plugins_words()
    {
      var notes = new[] { Note("old", "Old one", minutesAgo: 600), Note("new", "**New** one with `Loft`", by: "dwight", minutesAgo: 3), Note("mid", "Middle", minutesAgo: 90) };
      var view = NotesPane.View(notes, n => n.Id == "new" ? ("Slider", NoteStatus.Stale) : n.Id == "old" ? (null, NoteStatus.Gone) : (null, NoteStatus.Fresh), Now);
      var rows = view["rows"]!.AsArray().Select(r => r!.AsObject()).ToList();
      Assert.Equal(new[] { "new", "mid", "old" }, rows.Select(r => r["id"]!.GetValue<string>()));
      Assert.Equal("New one with Loft", rows[0]["words"]!.GetValue<string>());
      Assert.Equal("Dwight", rows[0]["who"]!.GetValue<string>());
      Assert.Equal("you", rows[1]["who"]!.GetValue<string>());
      Assert.Equal("3m", rows[0]["when"]!.GetValue<string>());
      Assert.Equal("1h", rows[1]["when"]!.GetValue<string>());
      Assert.Equal("Slider", rows[0]["about"]!.GetValue<string>());
      Assert.Null(rows[1]["about"]);
      Assert.Equal(new[] { "changed since", "fresh", "gone" }, rows.Select(r => r["stateWord"]!.GetValue<string>()));
      Assert.Equal(new[] { "stale", "fresh", "gone" }, rows.Select(r => r["state"]!.GetValue<string>()));
      Assert.Equal(3, view["count"]!.GetValue<int>());
      Assert.Equal("Notes", view["heading"]!.GetValue<string>());
      Assert.Equal("New note", view["newNote"]!.GetValue<string>());
      Assert.Equal("Show notes", view["show"]!.GetValue<string>());
    }

    [Fact]
    public void An_empty_file_says_so_in_dwights_voice_and_counts_nothing()
    {
      var view = NotesPane.View(new StoredNote[0], null, Now);
      Assert.Equal("No notes in this file yet. Select a component and choose Note here from the chord, or press New note.", view["empty"]!.GetValue<string>());
      Assert.Equal(0, view["count"]!.GetValue<int>());
      Assert.Empty(view["rows"]!.AsArray());
      Assert.Equal(0, NotesPane.View(null, null, Now)["count"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("First line\nSecond line", "First line")]
    [InlineData("\n\n  Leading blank lines  \nthen", "Leading blank lines")]
    [InlineData("| Port | Count |\n|---|---|\n| A | 96 |", "Port | Count")]
    [InlineData("- a bullet keeps its mark", "- a bullet keeps its mark")]
    [InlineData("1. a number keeps its mark", "1. a number keeps its mark")]
    public void A_rows_words_are_the_first_line_with_its_marks_read(string text, string expected) =>
      Assert.Equal(expected, NotesPane.Words(text));

    [Fact]
    public void A_rows_words_are_cut_at_eighty_with_an_ellipsis()
    {
      Assert.Equal(80, NotesPane.WordsMax);
      var exact = new string('x', 80);
      Assert.Equal(exact, NotesPane.Words(exact));
      var long1 = string.Join(" ", Enumerable.Repeat("word", 40));
      var cut = NotesPane.Words(long1);
      Assert.True(cut.Length <= 80, cut.Length.ToString());
      Assert.EndsWith("...", cut);
      Assert.EndsWith("word...", cut);        // at a word, never inside one
      Assert.Equal(80, NotesPane.Words(new string('y', 200)).Length);
    }

    [Fact]
    public void A_rows_click_goes_to_the_cards_middle_or_to_its_dot()
    {
      Assert.Equal((220.0, 130.0), NotesPane.GoTarget(100, 100, 240, 60, 1));   // a card: its middle
      Assert.Equal((100.0, 100.0), NotesPane.GoTarget(100, 100, 240, 60, 0.2)); // a dot: where the dot is
    }

    [Fact]
    public void Go_there_finds_an_anchored_a_free_and_a_gone_note()
    {
      var anchored = new StoredNote { Anchor = Guid.NewGuid().ToString(), At = new[] { 0.0, 64.0 }, Last = new[] { 500.0, 564.0 } };
      Assert.Equal((300.0, 264.0), NotePlace.Origin(anchored, (300, 200)));   // the component's corner and its offset
      Assert.Equal((500.0, 564.0), NotePlace.Origin(anchored, null));         // its component gone: where it was last
      var free = new StoredNote { At = new[] { 40.0, 50.0 }, Last = new[] { 40.0, 50.0 } };
      Assert.Equal((40.0, 50.0), NotePlace.Origin(free, (300, 200)));
      Assert.Null(NotePlace.Origin(new StoredNote(), null));
      Assert.Null(NotePlace.Origin(null, null));
    }

    // ---- unit 3: a note moves, and is pinned or free by choice ---------------------------------------------------------------------------------

    private static readonly Guid A = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid B = Guid.Parse("99999999-8888-7777-6666-555555555555");

    private static StoredNote Pinned() => new StoredNote
    {
      Id = "n1", Text = "keep it under 120", By = "you", WhenUtc = "2026-11-02T06:00:00.0000000Z",
      Anchor = A.ToString(), At = new[] { 0.0, 64.0 }, Last = new[] { 300.0, 264.0 }, Stamp = "stamp-a",
    };

    [Fact]
    public void Unpinning_keeps_the_card_where_it_is()
    {
      var note = Pinned();
      var before = NotePlace.Origin(note, (300, 200)).Value;
      NotePlace.Apply(note, NotePlace.Unpinned(before.X, before.Y));
      Assert.Null(note.Anchor);
      Assert.Null(note.Stamp);
      Assert.Equal(before, NotePlace.Origin(note, null).Value);
      Assert.Equal(new[] { 300.0, 264.0 }, note.At);
      Assert.Equal(new[] { 300.0, 264.0 }, note.Last);
    }

    [Fact]
    public void Pinning_recomputes_the_offset_so_the_card_does_not_jump_and_takes_the_new_components_stamp()
    {
      var note = Pinned();
      NotePlace.Apply(note, NotePlace.Unpinned(300, 264));
      NotePlace.Apply(note, NotePlace.PinnedTo(B, (250, 100), 300, 264, "stamp-b"));
      Assert.Equal(B.ToString(), note.Anchor);
      Assert.Equal(new[] { 50.0, 164.0 }, note.At);
      Assert.Equal((300.0, 264.0), NotePlace.Origin(note, (250, 100)).Value);
      Assert.Equal("stamp-b", note.Stamp);   // not stamp-a: the note would say it was written before a component it was never on changed
      Assert.Equal(NoteStatus.Fresh, NoteState.Of(note, true, "stamp-b"));
    }

    [Fact]
    public void A_moved_or_repinned_note_is_the_same_note()
    {
      var note = Pinned();
      NotePlace.Apply(note, NotePlace.Moved(note, 10, 20, (300, 200)));
      NotePlace.Apply(note, NotePlace.Unpinned(10, 20));
      NotePlace.Apply(note, NotePlace.PinnedTo(B, (0, 0), 10, 20, null));
      Assert.Equal("n1", note.Id);
      Assert.Equal("keep it under 120", note.Text);
      Assert.Equal("you", note.By);
      Assert.Equal("2026-11-02T06:00:00.0000000Z", note.WhenUtc);
      Assert.Null(note.Stamp);   // a component that could not be read gives no stamp, and a note with none reads as fresh
    }

    [Fact]
    public void A_drag_writes_the_offset_for_a_pinned_note_the_point_for_a_free_one_and_only_the_place_for_a_gone_one()
    {
      var pinned = NotePlace.Moved(Pinned(), 400, 300, (300, 200));
      Assert.Equal(new[] { 100.0, 100.0 }, pinned.At);
      Assert.Equal(new[] { 400.0, 300.0 }, pinned.Last);
      Assert.Equal(A.ToString(), pinned.Anchor);
      Assert.Equal("stamp-a", pinned.Stamp);

      var free = NotePlace.Moved(new StoredNote { At = new[] { 1.0, 2.0 } }, 400, 300, null);
      Assert.Equal(new[] { 400.0, 300.0 }, free.At);
      Assert.Equal(new[] { 400.0, 300.0 }, free.Last);
      Assert.Null(free.Anchor);

      var gone = NotePlace.Moved(Pinned(), 400, 300, null);
      Assert.Equal(new[] { 0.0, 64.0 }, gone.At);       // the offset it goes back to if Ctrl-Z brings its component back
      Assert.Equal(new[] { 400.0, 300.0 }, gone.Last);  // and where it is drawn meanwhile
      Assert.Equal(A.ToString(), gone.Anchor);
    }

    [Fact]
    public void Pinning_with_nothing_selected_is_refused_in_the_panels_words_and_the_switch_says_where_it_is()
    {
      Assert.Equal("Select the component to pin to.", NotePlace.PinRefusal);
      Assert.Equal("Pinned to Slider", NotePlace.PinLabel(true, " Slider "));
      Assert.Equal("Free", NotePlace.PinLabel(false, "Slider"));
      Assert.Equal("Pinned to a component no longer on the canvas", NotePlace.PinLabel(true, null));
      Assert.True(NotePlace.Pinned(Pinned()));
      Assert.False(NotePlace.Pinned(new StoredNote()));
    }

    [Fact]
    public void The_record_round_trips_with_and_without_an_anchor()
    {
      var pinned = Pinned();
      var free = Pinned(); free.Id = "n2";
      NotePlace.Apply(free, NotePlace.Unpinned(10, 20));
      var back = SessionStoreCore.Read(SessionStoreCore.Write(new SessionRecord { Notes = new List<StoredNote> { pinned, free } })).Record.Notes;
      Assert.Equal(2, back.Count);
      Assert.Equal(A.ToString(), back[0].Anchor);
      Assert.Equal(new[] { 0.0, 64.0 }, back[0].At);
      Assert.Equal("stamp-a", back[0].Stamp);
      Assert.Null(back[1].Anchor);
      Assert.Null(back[1].Stamp);
      Assert.Equal(new[] { 10.0, 20.0 }, back[1].At);
      Assert.Equal(new[] { 10.0, 20.0 }, back[1].Last);
    }

    [Fact]
    public void A_drag_writes_once_when_it_is_let_go_and_never_for_a_click_or_an_abandoned_drag()
    {
      var writes = new List<(string, double, double)>();
      void Write(string id, double x, double y) => writes.Add((id, x, y));

      var drag = new NoteDrag("n1", 100, 200, 110, 205, Write);
      drag.Move(130, 215, 3, 3);                     // inside a click's slop: the card stays
      Assert.False(drag.Moved);
      Assert.Equal((100.0, 200.0), (drag.X, drag.Y));
      drag.Move(160, 245, 40, 30);
      drag.Move(170, 255, 50, 40);
      Assert.Equal((160.0, 250.0), (drag.X, drag.Y)); // the card keeps the offset it was picked up at
      Assert.Empty(writes);                          // nothing per move
      Assert.True(drag.Drop());
      Assert.False(drag.Drop());
      Assert.Equal(new[] { ("n1", 160.0, 250.0) }, writes);

      var click = new NoteDrag("n2", 0, 0, 0, 0, Write);
      click.Move(2, 2, 2, 2);
      Assert.False(click.Drop());
      var lost = new NoteDrag("n3", 0, 0, 0, 0, Write);
      lost.Move(50, 50, 50, 50);
      lost.Abandon();
      Assert.False(lost.Drop());
      lost.Move(80, 80, 80, 80);
      Assert.Single(writes);
    }

    [Fact]
    public void A_drag_and_a_click_share_one_slop()
    {
      Assert.Equal(3, NoteDrag.Slop);
      Assert.False(NoteDrag.IsDrag(3, -3));
      Assert.True(NoteDrag.IsDrag(4, 0));
      Assert.True(NoteDrag.IsDrag(0, -4));
      Assert.Contains("if (NoteDrag.IsDrag(e.X - _down.X, e.Y - _down.Y)) return;", Repo("Dwight", "UI", "CanvasNotes.cs"));
    }

    // ---- unit 4: lists ---------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("1. first\n2. second")]
    [InlineData("1) first\n2) second")]
    [InlineData("  3. first\n9. second")]
    public void Numbered_lines_are_numbered_items(string text)
    {
      var blocks = NoteMarkdown.Parse(text);
      Assert.Equal(new[] { NoteBlockKind.Numbered, NoteBlockKind.Numbered }, blocks.Select(b => b.Kind));
      Assert.Equal(new[] { "first", "second" }, blocks.Select(b => Plain(b.Runs)));
      Assert.Equal(new[] { 1, 2 }, blocks.Select(b => b.Number));
    }

    [Fact]
    public void The_number_drawn_is_the_place_in_the_run_not_the_number_typed()
    {
      Assert.Equal(new[] { 1, 2, 3 }, NoteMarkdown.Parse("1. a\n1. b\n1. c").Select(b => b.Number));
      Assert.Equal(new[] { 1, 2 }, NoteMarkdown.Parse("7. a\n\n3. b").Select(b => b.Number));   // a blank line does not end a run
      var broken = NoteMarkdown.Parse("1. a\nA paragraph.\n5. b\n- bullet\n4. c");
      Assert.Equal(new[] { NoteBlockKind.Numbered, NoteBlockKind.Paragraph, NoteBlockKind.Numbered, NoteBlockKind.Bullet, NoteBlockKind.Numbered }, broken.Select(b => b.Kind));
      Assert.Equal(new[] { 1, 1, 1 }, broken.Where(b => b.Kind == NoteBlockKind.Numbered).Select(b => b.Number));
    }

    [Theory]
    [InlineData("2.5 mm is the gap")]
    [InlineData("1.first with no space")]
    public void A_number_without_its_mark_and_space_is_a_paragraph(string text) =>
      Assert.Equal(NoteBlockKind.Paragraph, Assert.Single(NoteMarkdown.Parse(text)).Kind);

    [Fact]
    public void The_model_and_a_rows_words_keep_the_markers()
    {
      const string text = "1. first\n2) second\n- third";
      Assert.Equal(text, NoteMarkdown.ForModel(text));
      Assert.Equal("1. first", NoteMarkdown.Words("1. first"));
      Assert.Equal("- x", NoteMarkdown.Words("- **x**"));
    }

    [Fact]
    public void A_bullet_is_a_round_mark_of_radius_three_inside_its_indent()
    {
      Assert.Equal(3, NoteLayout.BulletRadius);
      var r = Lay("- one");
      var (x, y) = Assert.Single(r.Bullets);
      Assert.True(x - NoteLayout.BulletRadius >= 0);
      Assert.True(x + NoteLayout.BulletRadius < NoteLayout.BulletIndent);
      Assert.Equal(NoteLayout.LineHeight / 2, y);
      var card = Repo("Dwight", "UI", "NoteCard.cs");
      Assert.Contains("g.FillEllipse(b, x + (float)bx - radius, y + (float)by - radius, 2f * radius, 2f * radius);", card);
      Assert.DoesNotContain("g.FillRectangle(b, x + (float)bx - 2f", card);
    }

    [Fact]
    public void A_numbered_item_draws_its_numeral_right_aligned_in_an_indent_for_two_digits()
    {
      var r = Lay(string.Join("\n", Enumerable.Range(1, 10).Select(i => i + ". item")));
      var indent = NoteLayout.NumberIndent(new Fixed());
      Assert.Equal(18 + NoteLayout.NumberGap, indent);   // "00." in the fixed metrics, then the gap
      var numerals = r.Text.Where(t => t.Text.EndsWith(".", StringComparison.Ordinal)).ToList();
      Assert.Equal(Enumerable.Range(1, 10).Select(i => i + "."), numerals.Select(t => t.Text));
      Assert.All(numerals, t => Assert.Equal(indent - NoteLayout.NumberGap, t.X + t.Text.Length * 6, 6));   // "1." and "10." end in one place
      Assert.All(numerals, t => Assert.Equal(NoteStyle.Body, t.Style));
      Assert.All(r.Text.Where(t => t.Text == "item"), t => Assert.Equal(indent, t.X, 6));
      Assert.Empty(r.Bullets);
      Assert.Equal(10, r.Lines);
      Assert.Equal(40, r.Chars);   // the words, not the numerals: the bound is the note's text
    }

    [Fact]
    public void A_numbered_items_wrapped_lines_hang_under_its_words()
    {
      var r = Lay("1. " + string.Join(" ", Enumerable.Repeat("word", 12)));
      Assert.Equal(2, r.Lines);
      var indent = NoteLayout.NumberIndent(new Fixed());
      Assert.All(r.Text.Where(t => t.Text != "1."), t => Assert.True(t.X >= indent, t.Text));
      Assert.Single(r.Text, t => t.Text == "1.");
    }

    // ---- the shim, held by its text --------------------------------------------------------------------------------------------------------

    /// <summary>What the IL read decided (Brief ED's discriminator): Grasshopper's own handler has chosen before ours runs, the setter takes ours, and only a window select or a drag is taken over.</summary>
    [Fact]
    public void A_press_on_a_header_takes_over_only_from_grasshoppers_window_select_or_drag()
    {
      var notes = Repo("Dwight", "UI", "CanvasNotes.cs");
      var down = Between(notes, "private static void OnDown(object sender, MouseEventArgs e)", "internal static void DragMove(");
      Assert.Contains("if (e.Button != MouseButtons.Left || Control.ModifierKeys != Keys.None || _drawn.Count == 0) return;", down);
      Assert.Contains("if (doc == null || canvas.IsActiveObject || canvas.IsActiveWidget) return;", down);
      Assert.Contains("if (active != null && !(active is GH_WindowSelectInteraction) && !(active is GH_DragInteraction)) return;", down);
      Assert.Contains("var id = CanvasNotesCore.HeaderAt(_drawn, p.X, p.Y);", down);
      Assert.InRange(down.IndexOf("HeaderAt", StringComparison.Ordinal), 0, down.IndexOf("canvas.ActiveInteraction = new NoteDragInteraction(", StringComparison.Ordinal));

      var interaction = notes.Substring(notes.IndexOf("internal sealed class NoteDragInteraction : GH_AbstractInteraction", StringComparison.Ordinal));
      Assert.Contains(": base(canvas, e, true) { }", interaction);
      Assert.Contains("return GH_ObjectResponse.Handled;", interaction);
      Assert.Contains("CanvasNotes.DragEnd(true);\n      return GH_ObjectResponse.Release;", interaction);
      Assert.Contains("CanvasNotes.DragEnd(false);\n      base.Destroy();", interaction);
    }

    [Fact]
    public void A_drop_is_one_write_through_the_per_file_store_and_its_release_is_not_a_click()
    {
      var notes = Repo("Dwight", "UI", "CanvasNotes.cs");
      Assert.Contains("if (note != null) CommandHistory.PlaceNote(doc, noteId, NotePlace.Moved(note, x, y, Corner(doc, note)));", notes);
      Assert.Contains("try { if (released) _dropped = drag.Drop(); else drag.Abandon(); } catch { }", notes);
      var up = Between(notes, "private static void OnUp(object sender, MouseEventArgs e)", "private static void ClickDot(");
      Assert.InRange(up.IndexOf("if (_dropped) { _dropped = false; return; }", StringComparison.Ordinal), 0, up.IndexOf("NoteDrag.IsDrag", StringComparison.Ordinal));
      Assert.InRange(up.IndexOf("NoteDots.At(_dots, e.X, e.Y)", StringComparison.Ordinal), 0, up.IndexOf("CanvasNotesCore.HitTest(_drawn", StringComparison.Ordinal));

      var history = Repo("Dwight", "Session", "CommandHistory.cs");
      var place = Between(history, "public static bool PlaceNote(GH_Document doc, string noteId, Dwight.UI.NotePlacement place)", "Remove a note and persist it.");
      Assert.Contains("Dwight.UI.NotePlace.Apply(note, place);", place);
      Assert.Contains("Persist(doc, key, list);", place);
      Assert.Contains("NotesChanged?.Invoke();", place);
    }

    [Fact]
    public void The_dots_are_drawn_on_the_overlay_from_the_tokens_and_the_cards_threshold_decides()
    {
      var notes = Repo("Dwight", "UI", "CanvasNotes.cs");
      var paint = Between(notes, "private static void OnPaint(GH_Canvas sender)", "private static NoteRect DrawCard(");
      Assert.Contains("var chosen = CanvasNotesCore.Choose(visible.Where(r => CanvasNotesCore.CardsAtZoom(zoom, r.W)), view.X, view.Y, view.Width, view.Height, out _hidden);", paint);
      Assert.Contains("var small = CanvasNotesCore.Choose(visible.Where(r => !CanvasNotesCore.CardsAtZoom(zoom, r.W)), view.X, view.Y, view.Width, view.Height, out var past, NoteDots.MaxDots);", paint);
      Assert.Contains("out var past, NoteDots.MaxDots);", paint);
      Assert.Contains("_hidden += past;", paint);
      Assert.Contains("sender.Viewport.ProjectPoint(", paint);
      Assert.DoesNotContain("MinZoom", notes);

      var overlay = Between(notes, "private static void OnOverlay(GH_Canvas sender)", "private static void OnDown(");
      Assert.InRange(overlay.IndexOf("DrawDots(sender);", StringComparison.Ordinal), 0, overlay.IndexOf("NotesVisibility.Chip(", StringComparison.Ordinal));
      Assert.Contains("_dots = NoteDots.Merge(_dotSources, sender.Width, sender.Height,", overlay);
      Assert.DoesNotContain("FromArgb", notes);

      // Brief EH: the dots are drawn by the painter, whose fills are held by their pixels (NoteDotPainterTests) and whose dash is held here.
      var painter = Repo("Dwight", "UI", "NoteDotPainter.cs");
      Assert.Contains("UiTokens.BrandAccent(dark) : UiTokens.Surface(dark)", painter);
      Assert.Contains("if (d.Look == NoteDotLook.HollowDashed) ring.DashStyle = DashStyle.Dash;", painter);
      Assert.DoesNotContain("FromArgb", painter);
    }

    [Fact]
    public void The_dock_hears_the_panes_commands_on_their_own_door_and_follows_every_note_change()
    {
      var dock = Repo("Dwight", "UI", "DwightDock.cs");
      Assert.InRange(dock.IndexOf("if (cmd.StartsWith(\"note:\", StringComparison.Ordinal)) { HandleNote(cmd.Substring(5)); return; }", StringComparison.Ordinal), 0,
        dock.IndexOf("if (cmd.StartsWith(\"notes:\", StringComparison.Ordinal))", StringComparison.Ordinal));
      Assert.Contains("CommandHistory.NotesChanged += () => { try { _instance?.PushNoteList(); } catch { } };", dock);
      Assert.Contains("CanvasNotes.StatesChanged += () => { try { _instance?.OnNoteStates(); } catch { } };", dock);
      Assert.Contains("PushNoteList();", Between(dock, "public void Refresh()", "_view = CommandHistory.ForActiveFileView();"));
      Assert.Contains("DwightCanvasPopup.NoteHere(canvas.Viewport.MidPoint)", dock);
      Assert.Contains("if (verb == \"go\") CanvasNotes.GoTo(Instances.ActiveCanvas?.Document, arg);", dock);
      Assert.Contains("internal static void NoteHere(System.Drawing.PointF point)", Repo("Dwight", "DwightCanvasPopup.cs"));
    }

    [Fact]
    public void The_panels_switch_pins_to_the_first_selected_component_or_refuses_and_is_the_hosts_to_move()
    {
      var panel = Repo("Dwight", "UI", "DwightNotePanel.cs");
      var pin = Between(panel, "private void Pin(bool on)", "private void Handle(string cmd)");
      Assert.Contains("CanvasNotesCore.AnchorFor(DwightActions.CurrentSelection().Select(o => o.InstanceGuid).ToList())", pin);
      Assert.Contains("if (b == null) refusal = NotePlace.PinRefusal;", pin);
      Assert.Contains("NotePlace.PinnedTo(anchor.Value, (b.Value.X, b.Value.Y), origin.Value.X, origin.Value.Y, stamp)", pin);
      Assert.Contains("NotePlace.Unpinned(origin.Value.X, origin.Value.Y)", pin);
      Assert.Contains("Run($\"dwightNotePin(", pin);
      Assert.Contains("init[\"pin\"] = PinState(note);", panel);
      Assert.Contains("if (cmd.StartsWith(\"pin:\", StringComparison.Ordinal)) { Pin(cmd.StartsWith(\"pin:on\", StringComparison.Ordinal)); return; }", panel);
    }

    [Fact]
    public void The_shells_carry_the_tools_the_tab_and_the_switch()
    {
      var note = Repo("Dwight", "Resources", "note.html");
      Assert.Contains("onclick=\"noteNumbers()\"", note);
      Assert.Contains("<use href=\"#ic-numbers\"/>", note);
      Assert.Contains("id=\"notepin\" role=\"switch\"", note);
      Assert.Contains("<symbol id=\"ic-numbers\" viewBox=\"0 0 24 24\" data-tabler=\"list-numbers\">", Repo("Dwight", "Resources", "dwight-icons.svg"));
      Assert.Contains("numbers: 'list-numbers'", Repo("tools", "build-icon-sprite.js"));

      var dock = Repo("Dwight", "Resources", "dock-shell.html");
      Assert.Contains("<button class=\"iconbtn nbtn\" id=\"nbtn\" title=\"Notes\" aria-label=\"Notes\"><svg class=\"ic\" aria-hidden=\"true\"><use href=\"#ic-note\"/></svg>", dock);
      Assert.InRange(dock.IndexOf("id=\"tlbtn\"", StringComparison.Ordinal), 0, dock.IndexOf("id=\"nbtn\"", StringComparison.Ordinal));
      var pane = Between(dock, "<div id=\"notes\" class=\"dpane dnotelist\">", "<div id=\"noterows\"></div>");
      Assert.Contains("id=\"dnotes\" role=\"switch\"", pane);   // the switch moved into the pane's head (Brief ED)
      Assert.Single(System.Text.RegularExpressions.Regex.Matches(dock, "id=\"dnotes\""));
    }

    /// <summary>The dock is measured on tools/ui-notes-pane.json, and this holds it to NotesPane's own output, so a word changed here fails until the fixture, and the fit it measures, follow.</summary>
    [Fact]
    public void The_docks_fixture_is_the_panes_own_output()
    {
      var file = JsonNode.Parse(Repo("tools", "ui-notes-pane.json"))!;
      var now = new DateTime(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc);
      StoredNote At(string id, string text, string by, double minutesAgo) => new StoredNote { Id = id, Text = text, By = by, At = new[] { 0.0, 0.0 }, WhenUtc = DwightNotes.Now(now.AddMinutes(-minutesAgo)) };
      var notes = new[]
      {
        At("a1", "Keep the **span** under 120, or the solve slows past a second.", "you", 4),
        At("b2", "**Root cause (the scan):** Loft (12): Input parameter S failed to collect data\n\n**Suggested fix (the model's):** wire the curves from `Divide`.", "dwight", 130),
        At("c3", "Dwight built B1: 4 components.", "dwight", 1500),
        At("d4", "A first line long enough to run past the width of the dock, so that its row has to cut it short somewhere.\n\n1. then\n2. more", "you", 4400),
      };
      var states = new Dictionary<string, (string, NoteStatus)> { ["a1"] = ("Span", NoteStatus.Fresh), ["b2"] = ("Loft", NoteStatus.Stale), ["c3"] = (null, NoteStatus.Gone), ["d4"] = (null, NoteStatus.Fresh) };
      var pane = NotesPane.View(notes, n => states[n.Id], now);
      var empty = NotesPane.View(new StoredNote[0], null, now);
      Assert.True(JsonNode.DeepEquals(file["pane"], pane), "tools/ui-notes-pane.json's pane is not the product's: " + pane.ToJsonString());
      Assert.True(JsonNode.DeepEquals(file["empty"], empty), "tools/ui-notes-pane.json's empty is not the product's: " + empty.ToJsonString());
    }
  }
}
