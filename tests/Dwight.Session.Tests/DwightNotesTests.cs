using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Xunit;
using Dwight.Session;

namespace Dwight.Session.Tests
{
  /// <summary>
  /// Brief DC, PR 1: Dwight notes as data. The record keeps them and preserves what it cannot read, a note is bounded and refused in the plugin's
  /// own sentence, the plugin states when a note is stale or its component gone, and a component's notes become a bounded list of lines for a
  /// payload. Each law below has a mutation that turns a test red (see the PR).
  /// </summary>
  public class DwightNotesTests
  {
    private static readonly Guid Comp = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly DateTime T0 = new DateTime(2026, 11, 1, 9, 0, 0, DateTimeKind.Utc);

    private static StoredNote Note(string text, string by = "you", int minute = 0, string stamp = "S1", Guid? anchor = null)
    {
      Assert.True(DwightNotes.TryMake(text, by, anchor ?? Comp, 10, -20, null, stamp, T0.AddMinutes(minute), out var n, out var why), why);
      return n;
    }

    // ---- the record ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_record_round_trips_notes_and_keeps_every_field()
    {
      var rec = new SessionRecord { Notes = new List<StoredNote> { Note("keep the offset at 40", "dwight", 1), Note("ask the client", "you", 2) } };
      rec.Notes[0].FromRun = "run-7";
      var back = SessionStoreCore.Read(SessionStoreCore.Write(rec)).Record;

      Assert.Equal(2, back.Notes.Count);
      var n = back.Notes[0];
      Assert.Equal("keep the offset at 40", n.Text);
      Assert.Equal("dwight", n.By);
      Assert.Equal(Comp.ToString(), n.Anchor);
      Assert.Equal(new[] { 10.0, -20.0 }, n.At);
      Assert.Equal("S1", n.Stamp);
      Assert.Equal("run-7", n.FromRun);
      Assert.Equal(rec.Notes[0].Id, n.Id);
      Assert.Equal(DwightNotes.Now(T0.AddMinutes(1)), n.WhenUtc);
    }

    [Fact]
    public void The_last_known_point_survives_a_round_trip_and_a_bad_one_is_dropped()
    {
      var n = Note("x");
      n.Last = new[] { 30.5, 40.25 };
      var back = SessionStoreCore.Read(SessionStoreCore.Write(new SessionRecord { Notes = new List<StoredNote> { n } })).Record.Notes.Single();
      Assert.Equal(new[] { 30.5, 40.25 }, back.Last);

      n.Last = new[] { 1.0 };
      Assert.Null(SessionStoreCore.Read(SessionStoreCore.Write(new SessionRecord { Notes = new List<StoredNote> { n } })).Record.Notes.Single().Last);
    }

    [Fact]
    public void A_field_this_build_does_not_know_survives_a_read_and_write_on_the_record_and_on_a_note()
    {
      var json = "{\"schema\":1,\"minor\":9,\"documentId\":\"d\",\"futureRecordField\":{\"a\":1},\"notes\":[{\"id\":\"n1\",\"text\":\"hello\",\"by\":\"you\","
        + "\"at\":[1,2],\"futureNoteField\":\"kept\"}]}";
      var again = SessionStoreCore.Write(SessionStoreCore.Read(json).Record);
      Assert.Contains("futureRecordField", again);
      Assert.Contains("futureNoteField", again);
      Assert.Equal("hello", SessionStoreCore.Read(again).Record.Notes.Single().Text);
    }

    [Fact]
    public void An_older_record_reads_with_an_empty_list_and_a_write_stamps_the_new_minor()
    {
      var old = "{\"schema\":1,\"minor\":7,\"documentId\":\"d\",\"commands\":[]}";
      var rec = SessionStoreCore.Read(old).Record;
      Assert.NotNull(rec.Notes);
      Assert.Empty(rec.Notes);
      Assert.Equal(11, SessionStoreCore.SchemaMinor);
      Assert.Contains("\"minor\":11", SessionStoreCore.Write(rec));
    }

    [Fact]
    public void A_record_with_only_notes_has_content_and_one_without_notes_does_not()
    {
      Assert.False(SessionStoreCore.HasContent(new SessionRecord()));
      Assert.True(SessionStoreCore.HasContent(new SessionRecord { Notes = new List<StoredNote> { Note("x") } }));
    }

    [Fact]
    public void A_hand_edited_note_is_brought_to_shape_on_read()
    {
      var longText = new string('a', DwightNotes.MaxChars + 50);
      var json = "{\"schema\":1,\"notes\":["
        + "{\"id\":\"long\",\"text\":\"" + longText + "\",\"by\":\"someone else\",\"at\":[1,2]},"
        + "{\"id\":\"noplace\",\"text\":\"no anchor and no point\"},"
        + "{\"id\":\"badanchor\",\"text\":\"anchor is not a guid\",\"anchor\":\"zzz\",\"at\":[3,4]},"
        + "{\"id\":\"\",\"text\":\"no id\",\"at\":[1,1]},"
        + "{\"id\":\"notext\",\"text\":\"  \",\"at\":[1,1]}]}";
      var notes = SessionStoreCore.Read(json).Record.Notes;

      Assert.Equal(new[] { "long", "badanchor" }, notes.Select(n => n.Id).ToArray());
      Assert.Equal(DwightNotes.MaxChars, notes[0].Text.Length);
      Assert.Equal("you", notes[0].By);          // anything but "dwight" is the person's
      Assert.Null(notes[1].Anchor);              // a malformed guid could never resolve, so it is no anchor
    }

    [Fact]
    public void Past_the_cap_the_oldest_notes_go_first()
    {
      var rec = new SessionRecord { Notes = Enumerable.Range(0, DwightNotes.MaxNotes + 3).Select(i => Note("n" + i)).ToList() };
      var notes = SessionStoreCore.Read(SessionStoreCore.Write(rec)).Record.Notes;
      Assert.Equal(DwightNotes.MaxNotes, notes.Count);
      Assert.Equal("n3", notes.First().Text);
      Assert.Equal("n" + (DwightNotes.MaxNotes + 2), notes.Last().Text);
    }

    // ---- the bound -------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_note_of_exactly_the_bound_is_made_and_one_character_more_is_refused_in_the_plugins_sentence()
    {
      Assert.True(DwightNotes.TryMake(new string('a', 4000), "you", Comp, 0, 0, null, "S", T0, out var ok, out _));
      Assert.Equal(4000, ok.Text.Length);

      Assert.False(DwightNotes.TryMake(new string('a', 4001), "you", Comp, 0, 0, null, "S", T0, out var none, out var refusal));
      Assert.Null(none);
      Assert.Equal("A note holds at most 4000 characters, and this one has 4001. Shorten it, or split it into two notes.", refusal);
    }

    /// <summary>Brief DO: what a note holds rose, and what a run sends did not. The two numbers are held together, because the second is what the privacy page speaks for.</summary>
    [Fact]
    public void A_note_holds_4000_and_a_run_sends_600()
    {
      Assert.Equal(4000, DwightNotes.MaxChars);
      Assert.Equal(600, DwightNotes.MaxCharsPerComponent);
      var lines = Lines((Note(new string('y', DwightNotes.MaxChars)), NoteStatus.Fresh));
      Assert.Equal(600, Assert.Single(lines).Length);
    }

    /// <summary>Brief DO, item 4: the marks stay, because the model reads Markdown, and a table is lines.</summary>
    [Fact]
    public void The_model_reads_a_notes_marks_and_a_table_as_lines()
    {
      var lines = Lines((Note("Why **this**:\n| a | b |\n|---|---|\n| 1 | `2` |"), NoteStatus.Fresh));
      Assert.Equal("you: Why **this**:\na | b\n1 | `2`", Assert.Single(lines));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_note_is_no_note(string text)
    {
      Assert.False(DwightNotes.TryMake(text, "you", Comp, 0, 0, null, "S", T0, out _, out var refusal));
      Assert.Equal("A note needs some text.", refusal);
    }

    [Fact]
    public void A_note_about_a_point_carries_no_anchor_and_no_stamp()
    {
      Assert.True(DwightNotes.TryMake("the corner", "you", null, 5, 6, null, "ignored", T0, out var n, out _));
      Assert.Null(n.Anchor);
      Assert.Null(n.Stamp);
      Assert.Equal(new[] { 5.0, 6.0 }, n.At);
    }

    // ---- stale and gone, as facts ----------------------------------------------------------------------------------------------------

    [Fact]
    public void A_note_is_fresh_when_the_stamp_is_the_same_stale_when_it_differs_and_gone_when_the_component_is_not_on_the_canvas()
    {
      var n = Note("x", stamp: "S1");
      Assert.Equal(NoteStatus.Fresh, NoteState.Of(n, true, "S1"));
      Assert.Equal(NoteStatus.Stale, NoteState.Of(n, true, "S2"));
      Assert.Equal(NoteStatus.Gone, NoteState.Of(n, false, null));
      Assert.Equal(NoteStatus.Gone, NoteState.Of(n, false, "S1"));   // not on the canvas wins over any stamp
    }

    [Fact]
    public void A_component_that_is_present_but_cannot_be_stamped_reads_as_fresh_never_as_gone()
    {
      Assert.Equal(NoteStatus.Fresh, NoteState.Of(Note("x", stamp: "S1"), true, null));
      Assert.Equal(NoteStatus.Fresh, NoteState.Of(Note("x", stamp: null), true, "S9"));   // no stamp was taken, so there is nothing to compare
    }

    [Fact]
    public void A_note_about_a_point_is_never_stale_or_gone()
    {
      Assert.True(DwightNotes.TryMake("the corner", "you", null, 5, 6, null, null, T0, out var n, out _));
      Assert.Equal(NoteStatus.Fresh, NoteState.Of(n, false, null));
    }

    [Fact]
    public void The_two_state_sentences_are_the_plugins_words()
    {
      Assert.Equal("written before this component changed", NoteState.Sentence(NoteStatus.Stale));
      Assert.Equal("the component this was about is no longer on the canvas", NoteState.Sentence(NoteStatus.Gone));
      Assert.Null(NoteState.Sentence(NoteStatus.Fresh));
    }

    // ---- the lines a component's notes become -----------------------------------------------------------------------------------------

    private static List<string> Lines(params (StoredNote n, NoteStatus s)[] notes) =>
      DwightNotes.Lines(notes.Select(t => new KeyValuePair<StoredNote, NoteStatus>(t.n, t.s)));

    [Fact]
    public void No_notes_make_no_lines()
    {
      Assert.Empty(Lines());
    }

    [Fact]
    public void One_note_is_one_line_prefixed_with_its_author()
    {
      Assert.Equal(new[] { "you: keep it" }, Lines((Note("keep it"), NoteStatus.Fresh)));
      Assert.Equal(new[] { "dwight: it divides" }, Lines((Note("it divides", "dwight"), NoteStatus.Fresh)));
    }

    [Fact]
    public void A_stale_note_says_so_in_the_plugins_words()
    {
      Assert.Equal(new[] { "you: (before this component changed) keep it" }, Lines((Note("keep it"), NoteStatus.Stale)));
    }

    [Fact]
    public void Three_notes_are_all_listed_newest_first_with_no_more_line()
    {
      var lines = Lines((Note("one", minute: 1), NoteStatus.Fresh), (Note("two", minute: 2), NoteStatus.Fresh), (Note("three", minute: 3), NoteStatus.Fresh));
      Assert.Equal(new[] { "you: three", "you: two", "you: one" }, lines);
    }

    [Fact]
    public void Five_notes_list_the_newest_three_and_say_how_many_were_left_out()
    {
      var notes = Enumerable.Range(1, 5).Select(i => (Note("note" + i, minute: i), NoteStatus.Fresh)).ToArray();
      Assert.Equal(new[] { "you: note5", "you: note4", "you: note3", "and 2 more" }, Lines(notes));
    }

    [Fact]
    public void The_character_bound_cuts_the_list_and_the_cut_is_stated()
    {
      var big = new string('x', 400);
      var lines = Lines((Note(big, minute: 1), NoteStatus.Fresh), (Note(big, minute: 2), NoteStatus.Fresh));
      Assert.Equal(2, lines.Count);
      Assert.Equal("you: " + big, lines[0]);
      Assert.Equal("and 1 more", lines[1]);
      Assert.True(lines.Take(1).Sum(l => l.Length) <= DwightNotes.MaxCharsPerComponent);
    }

    [Fact]
    public void A_first_note_that_alone_is_over_the_bound_is_cut_with_a_visible_mark()
    {
      var lines = Lines((Note(new string('y', 600)), NoteStatus.Fresh));
      Assert.Single(lines);
      Assert.Equal(DwightNotes.MaxCharsPerComponent, lines[0].Length);
      Assert.EndsWith("...", lines[0]);
    }

    [Fact]
    public void A_note_whose_component_is_gone_is_never_a_line()
    {
      Assert.Empty(Lines((Note("lost"), NoteStatus.Gone)));
      Assert.Equal(new[] { "you: here" }, Lines((Note("lost", minute: 1), NoteStatus.Gone), (Note("here", minute: 2), NoteStatus.Fresh)));
    }

    [Fact]
    public void Nothing_but_text_reaches_the_payload_never_an_id_a_guid_a_place_or_a_stamp()
    {
      var node = new JsonObject();
      var n = Note("only words", "dwight");
      DwightNotes.AddTo(node, Lines((n, NoteStatus.Fresh)));
      var json = node.ToJsonString();

      Assert.Equal("{\"notes\":[\"dwight: only words\"]}", json);
      Assert.DoesNotContain(n.Id, json);
      Assert.DoesNotContain(Comp.ToString(), json);
      Assert.DoesNotContain("S1", json);
    }

    [Fact]
    public void A_component_with_no_notes_gets_no_notes_field()
    {
      var node = new JsonObject { ["id"] = 1 };
      DwightNotes.AddTo(node, null);
      DwightNotes.AddTo(node, new List<string>());
      Assert.False(node.ContainsKey("notes"));
    }
  }
}
