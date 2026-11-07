using System.Linq;
using System.Text;
using Dwight.Data;
using Xunit;

namespace Dwight.Data.Tests
{
  /// <summary>
  /// Phase-9 U20 - THE DIFF A USER READS BEFORE APPROVING AN EDIT TO THEIR SCRIPT.
  ///
  /// It is not the consent surface: the card shows the COMPLETE new source, because this is code that will
  /// RUN on the user's machine and Phase 7 settled that question (the Phase 7 to-script ruling, §4.5). The diff sits
  /// above it to ORIENT - which three lines moved, in a file of ninety. So the properties worth pinning are
  /// the ones that decide whether it orients or misleads: that it finds the change where the change IS,
  /// that a line-ending round-trip does not render as a whole-file rewrite, that a gap is VISIBLE rather
  /// than silent, and that the counts describe the whole diff rather than the shown part.
  /// </summary>
  public class SourceDiffTests
  {
    private static string Lines(params string[] lines) => string.Join("\n", lines);

    // ---- it finds the change where the change is -----------------------------------------------------

    [Fact]
    public void An_inserted_line_is_one_addition_not_a_rewrite_of_everything_after_it()
    {
      var before = Lines("a = 1", "b = 2", "c = 3");
      var after = Lines("a = 1", "b = 2", "b2 = 2.5", "c = 3");

      var diff = SourceDiff.Between(before, after);

      Assert.Equal(1, diff.Added);
      Assert.Equal(0, diff.Removed);
      Assert.Single(diff.Lines.Where(l => l.Kind == DiffKind.Added));
      Assert.Equal("b2 = 2.5", diff.Lines.First(l => l.Kind == DiffKind.Added).Text);
    }

    [Fact]
    public void A_changed_line_reads_as_one_removal_and_one_addition()
    {
      var diff = SourceDiff.Between(Lines("a = 1", "b = 2"), Lines("a = 1", "b = 99"));

      Assert.Equal(1, diff.Added);
      Assert.Equal(1, diff.Removed);
      Assert.Equal("b = 2", diff.Lines.First(l => l.Kind == DiffKind.Removed).Text);
      Assert.Equal("b = 99", diff.Lines.First(l => l.Kind == DiffKind.Added).Text);
    }

    /// <summary>THE FIELD CASE, in miniature: an offset feature added to a script that computes an area.
    /// The diff must show the two new lines and leave the rest alone, or the user cannot tell at a glance
    /// what they are approving.</summary>
    [Fact]
    public void Adding_a_feature_shows_only_the_feature()
    {
      var before = Lines("import math", "", "area = math.pi * r * r", "centroid = (0, 0)");
      var after = Lines("import math", "", "r2 = r + offset", "area = math.pi * r2 * r2", "centroid = (0, 0)");

      var diff = SourceDiff.Between(before, after);

      Assert.Equal(2, diff.Added);
      Assert.Equal(1, diff.Removed);
      Assert.Contains("2 lines added, 1 line removed", diff.Summary());
    }

    // ---- an identical pair is identical, and whitespace does not lie --------------------------------

    [Fact]
    public void Identical_sources_produce_an_identical_diff()
    {
      var diff = SourceDiff.Between(Lines("a = 1", "b = 2"), Lines("a = 1", "b = 2"));
      Assert.True(diff.Identical);
      Assert.Equal("no change", diff.Summary());
    }

    /// <summary>
    /// A LINE-ENDING ROUND-TRIP IS NOT A REWRITE. Without this, a user whose editor saves CRLF would be
    /// shown a card claiming every line of their script had been replaced - which would make the card
    /// unreadable at exactly the moment the card is the safety property.
    /// </summary>
    [Fact]
    public void A_line_ending_round_trip_is_not_a_change()
    {
      var lf = Lines("a = 1", "b = 2", "c = 3");
      Assert.True(SourceDiff.Between(lf, lf.Replace("\n", "\r\n")).Identical);
      Assert.True(SourceDiff.Between(lf, lf.Replace("\n", "\r")).Identical);
    }

    /// <summary>But indentation IS the program in Python, so a re-indent is a real change and must show as
    /// one. Over-normalising would hide the single most dangerous silent edit there is.</summary>
    [Fact]
    public void A_re_indent_is_a_real_change()
    {
      var diff = SourceDiff.Between(Lines("if x:", "    a = 1"), Lines("if x:", "\ta = 1"));
      Assert.False(diff.Identical);
    }

    // ---- gaps are visible ---------------------------------------------------------------------------

    /// <summary>
    /// A stretch of untouched code between two hunks is replaced by a GAP LINE rather than by silence,
    /// because simply omitting it makes two distant changes look adjacent - and a user reading a card has
    /// no other way to tell that fifty lines sit between them.
    /// </summary>
    [Fact]
    public void Untouched_code_between_two_changes_is_marked_rather_than_silently_dropped()
    {
      var before = new StringBuilder("first = 1\n");
      for (var i = 0; i < 40; i++) before.Append("filler_").Append(i).Append(" = ").Append(i).Append('\n');
      before.Append("last = 2");

      var after = before.ToString().Replace("first = 1", "first = 11").Replace("last = 2", "last = 22");
      var diff = SourceDiff.Between(before.ToString(), after);

      var gaps = diff.Lines.Where(l => l.Kind == DiffKind.Gap).ToList();
      Assert.Single(gaps);
      Assert.Contains("unchanged line", gaps[0].Text);
      // and the far-apart hunks did NOT collapse into one another
      Assert.True(diff.Lines.Count < 40);
    }

    [Fact]
    public void Context_lines_are_kept_around_each_change_so_a_hunk_reads_in_place()
    {
      var before = Lines("a", "b", "c", "d", "e", "f", "g", "h", "i");
      var diff = SourceDiff.Between(before, before.Replace("e", "E"));

      var context = diff.Lines.Where(l => l.Kind == DiffKind.Context).Select(l => l.Text).ToList();
      Assert.Contains("b", context);
      Assert.Contains("h", context);
      Assert.DoesNotContain("a", context);   // beyond the context window
    }

    // ---- the bound, and the counts it must not distort ------------------------------------------------

    /// <summary>
    /// THE COUNTS DESCRIBE THE WHOLE DIFF, NEVER THE SHOWN PART. A summary that counted only what survived
    /// truncation would understate the change the user is being asked to approve - which is the one
    /// direction a consent surface must never round.
    /// </summary>
    [Fact]
    public void Truncation_cuts_the_rendering_and_never_the_counts()
    {
      var before = new StringBuilder();
      var after = new StringBuilder();
      for (var i = 0; i < 300; i++)
      {
        before.Append("line_").Append(i).Append(" = ").Append(i).Append('\n');
        // +1000 rather than *2: at i=0 the doubled line would be IDENTICAL, so the fixture would change
        // 299 lines while the test claimed 300 - a wrong expectation dressed as a wrong result.
        after.Append("line_").Append(i).Append(" = ").Append(i + 1000).Append('\n');
      }

      var diff = SourceDiff.Between(before.ToString(), after.ToString(), maxLines: 40);

      Assert.True(diff.Truncated);
      Assert.True(diff.Lines.Count <= 41);              // the bound, plus the marker
      Assert.Equal(300, diff.Added);                    // the WHOLE diff
      Assert.Equal(300, diff.Removed);
      Assert.Contains("300 lines added", diff.Summary());
      Assert.NotNull(diff.Omitted);
      Assert.Equal(DiffKind.Gap, diff.Lines.Last().Kind);
      Assert.Contains("full source is below", diff.Lines.Last().Text);
    }

    [Fact]
    public void A_short_diff_is_not_truncated_and_says_nothing_about_omissions()
    {
      var diff = SourceDiff.Between(Lines("a = 1"), Lines("a = 2"));
      Assert.False(diff.Truncated);
      Assert.Null(diff.Omitted);
    }

    // ---- degenerate inputs ---------------------------------------------------------------------------

    [Fact]
    public void An_empty_script_being_written_into_is_all_additions()
    {
      var diff = SourceDiff.Between("", Lines("a = 1", "b = 2"));
      Assert.Equal(2, diff.Added);
      Assert.Equal(0, diff.Removed);
      Assert.False(diff.Identical);
    }

    [Fact]
    public void Null_sources_do_not_throw()
    {
      Assert.True(SourceDiff.Between(null, null).Identical);
      Assert.Equal(1, SourceDiff.Between(null, "a = 1").Added);
      Assert.Equal(1, SourceDiff.Between("a = 1", null).Removed);
    }

    /// <summary>The text rendering is for a prompt or a log, never for HTML - escaping is a property of
    /// the DESTINATION, not of the producer (#335), so this must not do any.</summary>
    [Fact]
    public void The_text_rendering_marks_each_side_and_escapes_nothing()
    {
      var diff = SourceDiff.Between(Lines("a = \"<b>\""), Lines("a = \"<i>\""));
      var text = diff.ToText();
      Assert.Contains("- a = \"<b>\"", text);
      Assert.Contains("+ a = \"<i>\"", text);
      Assert.DoesNotContain("&lt;", text);
    }
  }
}
