using System;
using System.Collections.Generic;
using System.Linq;
using Dwight.Session;
using Dwight.UI;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>Brief DO, PR 1: the subset a note's text is written in, and the layout the canvas draws it from. Each construct on its own, the bound, and the text the model reads.</summary>
  public class NoteMarkdownTests
  {
    /// <summary>Every character 6 wide, so a line of the plain width holds 36.</summary>
    private sealed class Fixed : INoteMetrics
    {
      public double Width(string text, NoteStyle style) => (text ?? "").Length * 6;
    }

    private static NoteLayoutResult Lay(string text) => NoteLayout.Build(NoteMarkdown.Parse(text), new Fixed());

    private static string Plain(IEnumerable<NoteRun> runs) => string.Concat(runs.Select(r => r.Text));

    private static string Words(NoteLayoutResult r) => string.Join(" ", r.Text.Select(t => t.Text));

    // ---- the inline marks ----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Bold_and_code_are_runs_and_lose_their_marks()
    {
      var runs = NoteMarkdown.Inline("A **bold** word and `Loft` here");
      Assert.Equal(new[] { "A ", "bold", " word and ", "Loft", " here" }, runs.Select(r => r.Text));
      Assert.Equal(new[] { NoteStyle.Body, NoteStyle.Bold, NoteStyle.Body, NoteStyle.Code, NoteStyle.Body }, runs.Select(r => r.Style));
    }

    [Fact]
    public void Code_inside_bold_stays_code()
    {
      var runs = NoteMarkdown.Inline("**the `Loft` step**");
      Assert.Equal(new[] { NoteStyle.Bold, NoteStyle.Code, NoteStyle.Bold }, runs.Select(r => r.Style));
      Assert.Equal("the Loft step", Plain(runs));
    }

    [Fact]
    public void Marks_inside_code_are_its_own_characters()
    {
      var runs = NoteMarkdown.Inline("`a**b*c`");
      Assert.Equal(NoteStyle.Code, Assert.Single(runs).Style);
      Assert.Equal("a**b*c", runs[0].Text);
    }

    [Theory]
    [InlineData("two ** stars", "two ** stars")]
    [InlineData("one ` tick", "one ` tick")]
    [InlineData("a * b * c", "a * b * c")]
    [InlineData("an [open bracket", "an [open bracket")]
    public void An_unmatched_mark_is_its_own_character_so_nothing_typed_disappears(string text, string expected)
    {
      var run = Assert.Single(NoteMarkdown.Inline(text));
      Assert.Equal(NoteStyle.Body, run.Style);
      Assert.Equal(expected, run.Text);
    }

    [Theory]
    [InlineData("an *emphasis* word", "an emphasis word")]
    [InlineData("an _emphasis_ word", "an emphasis word")]
    [InlineData("see [the docs](https://example.com) here", "see the docs here")]
    public void What_is_not_in_the_subset_reads_as_its_words(string text, string expected) =>
      Assert.Equal(expected, Plain(NoteMarkdown.Inline(text)));

    [Theory]
    [InlineData("set_value and my_other_name")]
    [InlineData("set_value_ here")]
    public void A_name_with_underscores_is_not_emphasis(string text) =>
      Assert.Equal(text, Plain(NoteMarkdown.Inline(text)));

    [Fact]
    public void A_backslash_keeps_a_mark_literal() =>
      Assert.Equal("2 * 3 ` and **", Plain(NoteMarkdown.Inline("2 \\* 3 \\` and \\*\\*")));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void No_text_is_no_runs_and_no_blocks(string text)
    {
      Assert.Empty(NoteMarkdown.Inline(text));
      Assert.Empty(NoteMarkdown.Parse(text));
    }

    // ---- the blocks ----------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Lines_join_into_a_paragraph_and_a_blank_line_ends_it()
    {
      var blocks = NoteMarkdown.Parse("one\ntwo\n\nthree");
      Assert.Equal(2, blocks.Count);
      Assert.Equal("one two", Plain(blocks[0].Runs));
      Assert.Equal("three", Plain(blocks[1].Runs));
    }

    [Theory]
    [InlineData("- a\n- b")]
    [InlineData("* a\n* b")]
    [InlineData("+ a\n+ b")]
    [InlineData("  - a\r\n  - b")]
    public void Each_bullet_is_a_block(string text)
    {
      var blocks = NoteMarkdown.Parse(text);
      Assert.Equal(new[] { NoteBlockKind.Bullet, NoteBlockKind.Bullet }, blocks.Select(b => b.Kind));
      Assert.Equal("a", Plain(blocks[0].Runs));
      Assert.Equal("b", Plain(blocks[1].Runs));
    }

    [Fact]
    public void A_star_with_no_space_is_not_a_bullet() =>
      Assert.Equal(NoteBlockKind.Paragraph, Assert.Single(NoteMarkdown.Parse("*word* first")).Kind);

    [Fact]
    public void A_heading_is_a_bold_paragraph()
    {
      var block = Assert.Single(NoteMarkdown.Parse("## The `Loft` step ##"));
      Assert.Equal(NoteBlockKind.Paragraph, block.Kind);
      Assert.Equal(new[] { NoteStyle.Bold, NoteStyle.Code, NoteStyle.Bold }, block.Runs.Select(r => r.Style));
      Assert.Equal("The Loft step", Plain(block.Runs));
    }

    /// <summary>Brief ED: a numbered line was a paragraph keeping its own number; it is now a numbered item, its number the card's to draw (NotesSittingTests holds the run).</summary>
    [Fact]
    public void A_numbered_line_is_its_own_item_with_its_number_apart()
    {
      var blocks = NoteMarkdown.Parse("1. first\n2. second");
      Assert.Equal(new[] { NoteBlockKind.Numbered, NoteBlockKind.Numbered }, blocks.Select(b => b.Kind));
      Assert.Equal(new[] { "first", "second" }, blocks.Select(b => Plain(b.Runs)));
      Assert.Equal(new[] { 1, 2 }, blocks.Select(b => b.Number));
    }

    [Fact]
    public void A_table_has_a_header_and_rows_of_cells()
    {
      var block = Assert.Single(NoteMarkdown.Parse("| Port | Count |\n|---|:--:|\n| **A** | `96` |\n| B | 12 |"));
      Assert.Equal(NoteBlockKind.Table, block.Kind);
      Assert.Equal(3, block.Rows.Count);
      Assert.Equal(new[] { "Port", "Count" }, block.Rows[0].Select(Plain));
      Assert.Equal(NoteStyle.Bold, block.Rows[1][0][0].Style);
      Assert.Equal(NoteStyle.Code, block.Rows[1][1][0].Style);
    }

    [Fact]
    public void A_table_without_its_outer_pipes_is_still_a_table() =>
      Assert.Equal(NoteBlockKind.Table, Assert.Single(NoteMarkdown.Parse("a | b\n---|---\n1 | 2")).Kind);

    [Fact]
    public void A_short_row_is_padded_and_a_long_one_is_cut_to_the_header()
    {
      var block = Assert.Single(NoteMarkdown.Parse("| a | b |\n|---|---|\n| 1 |\n| 1 | 2 | 3 |"));
      Assert.All(block.Rows, r => Assert.Equal(2, r.Count));
      Assert.Empty(block.Rows[1][1]);
      Assert.Equal("2", Plain(block.Rows[2][1]));
    }

    [Fact]
    public void A_pipe_in_code_or_after_a_backslash_is_the_cells_own()
    {
      var block = Assert.Single(NoteMarkdown.Parse("| a | b |\n|---|---|\n| `x|y` | c\\|d |"));
      Assert.Equal("x|y", Plain(block.Rows[1][0]));
      Assert.Equal("c|d", Plain(block.Rows[1][1]));
    }

    [Fact]
    public void A_pipe_with_no_delimiter_under_it_is_a_paragraph() =>
      Assert.Equal(NoteBlockKind.Paragraph, Assert.Single(NoteMarkdown.Parse("a | b\nc | d")).Kind);

    [Fact]
    public void A_table_wider_than_the_bound_reads_as_lines()
    {
      var blocks = NoteMarkdown.Parse("| a | b | c | d | e |\n|---|---|---|---|---|\n| 1 | 2 | 3 | 4 | 5 |");
      Assert.Equal(new[] { NoteBlockKind.Paragraph, NoteBlockKind.Paragraph }, blocks.Select(b => b.Kind));
      Assert.Equal("1 | 2 | 3 | 4 | 5", Plain(blocks[1].Runs));
    }

    [Fact]
    public void Four_columns_is_a_table()
    {
      Assert.Equal(4, NoteMarkdown.MaxColumns);
      Assert.Equal(NoteBlockKind.Table, Assert.Single(NoteMarkdown.Parse("| a | b | c | d |\n|---|---|---|---|\n| 1 | 2 | 3 | 4 |")).Kind);
    }

    [Fact]
    public void Text_after_a_table_starts_a_new_block()
    {
      var blocks = NoteMarkdown.Parse("| a | b |\n|---|---|\n| 1 | 2 |\n\nAfter it.");
      Assert.Equal(new[] { NoteBlockKind.Table, NoteBlockKind.Paragraph }, blocks.Select(b => b.Kind));
    }

    // ---- what the model reads ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_model_keeps_the_marks()
    {
      const string text = "A **bold** word and `Loft`.\n\n- first\n- second";
      Assert.Equal(text, NoteMarkdown.ForModel(text));
    }

    [Fact]
    public void A_table_reaches_the_model_as_lines()
    {
      Assert.Equal("Before\na | b\n1 | **2**\nAfter", NoteMarkdown.ForModel("Before\n| a | b |\n|---|---|\n| 1 | **2** |\n\nAfter").Replace("\n\n", "\n"));
    }

    [Fact]
    public void No_note_is_an_empty_string_for_the_model() => Assert.Equal("", NoteMarkdown.ForModel(null));

    // ---- the layout ----------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_short_note_is_one_line_at_the_plain_width()
    {
      var r = Lay("A short note.");
      Assert.Equal(NoteLayout.PlainWidth, r.Width);
      Assert.Equal(1, r.Lines);
      Assert.Equal(NoteLayout.LineHeight, r.Height);
      Assert.False(r.Cut);
    }

    [Fact]
    public void The_card_grows_to_its_content()
    {
      var one = Lay("word");
      var three = Lay(string.Join(" ", Enumerable.Repeat("word", 20)));
      Assert.Equal(3, three.Lines);
      Assert.Equal(3 * one.Height, three.Height);
    }

    [Fact]
    public void Nothing_is_drawn_past_the_card()
    {
      foreach (var text in new[] { new string('x', 200), string.Join(" ", Enumerable.Repeat("word", 60)), "**" + new string('y', 100) + "** and `" + new string('z', 100) + "`" })
      {
        var r = Lay(text);
        var metrics = new Fixed();
        Assert.All(r.Text, t => Assert.True(t.X + metrics.Width(t.Text, t.Style) <= r.Width + 0.001, t.Text));
      }
    }

    [Fact]
    public void A_long_word_breaks_and_no_character_is_lost()
    {
      var r = Lay(new string('x', 100));
      Assert.Equal(3, r.Lines);
      Assert.Equal(100, r.Text.Sum(t => t.Text.Length));
    }

    [Fact]
    public void Runs_share_a_line_and_are_placed_after_one_another()
    {
      var r = Lay("A **bold** `code`");
      Assert.Equal(1, r.Lines);
      var words = r.Text.Where(t => t.Text != " ").ToList();
      Assert.Equal(new[] { 0.0, 12.0, 42.0 }, words.Select(t => t.X));
      Assert.Equal(new[] { NoteStyle.Body, NoteStyle.Bold, NoteStyle.Code }, words.Select(t => t.Style));
    }

    [Fact]
    public void White_space_folds_and_a_line_never_ends_in_a_space()
    {
      var r = Lay("a    b");
      Assert.Equal("a b", Words(r));
      var wrapped = Lay(new string('x', 36) + " y");
      Assert.DoesNotContain(wrapped.Text, t => t.Text == " ");
      Assert.Equal(2, wrapped.Lines);
      var roomForTheSpace = Lay(new string('x', 30) + " " + new string('y', 10));
      Assert.Equal(new[] { new string('x', 30), new string('y', 10) }, roomForTheSpace.Text.Select(t => t.Text));
    }

    [Fact]
    public void A_bullet_has_its_mark_and_its_wrapped_lines_hang()
    {
      var r = Lay("- " + string.Join(" ", Enumerable.Repeat("word", 12)));
      Assert.Equal(2, r.Lines);
      Assert.Single(r.Bullets);
      Assert.All(r.Text, t => Assert.True(t.X >= NoteLayout.BulletIndent));
      Assert.Equal(NoteLayout.LineHeight / 2, r.Bullets[0].Y);
    }

    [Fact]
    public void Blocks_have_a_gap_between_them_and_none_before_the_first()
    {
      var r = Lay("one\n\ntwo");
      Assert.Equal(new[] { 0.0, NoteLayout.LineHeight + NoteLayout.BlockGap }, r.Text.Select(t => t.Y));
      Assert.Equal(2 * NoteLayout.LineHeight + NoteLayout.BlockGap, r.Height);
    }

    [Fact]
    public void A_table_takes_the_wider_card_and_equal_columns_and_its_header_is_bold()
    {
      var r = Lay("| a | b |\n|---|---|\n| 1 | 2 |");
      Assert.Equal(NoteLayout.TableWidth, r.Width);
      Assert.Equal(new[] { NoteLayout.CellPad, NoteLayout.TableWidth / 2 + NoteLayout.CellPad, NoteLayout.CellPad, NoteLayout.TableWidth / 2 + NoteLayout.CellPad }, r.Text.Select(t => t.X));
      Assert.Equal(new[] { NoteStyle.Bold, NoteStyle.Bold, NoteStyle.Body, NoteStyle.Body }, r.Text.Select(t => t.Style));
      Assert.Equal(2 * NoteLayout.LineHeight, r.Height);
    }

    [Fact]
    public void A_table_has_its_rules_on_every_row_and_column()
    {
      var r = Lay("| a | b |\n|---|---|\n| 1 | 2 |");
      Assert.Equal(3, r.Rules.Count(l => l.Y1 == l.Y2));   // the top and under each of two rows
      Assert.Equal(3, r.Rules.Count(l => l.X1 == l.X2));   // the left, the middle and the right
      Assert.All(r.Rules.Where(l => l.X1 == l.X2), l => { Assert.Equal(0, l.Y1); Assert.Equal(r.Height, l.Y2); });
    }

    [Fact]
    public void A_row_is_as_tall_as_its_tallest_cell_and_a_cell_wraps_inside_its_column()
    {
      var r = Lay("| a | b |\n|---|---|\n| " + string.Join(" ", Enumerable.Repeat("word", 8)) + " | x |");
      var metrics = new Fixed();
      Assert.All(r.Text.Where(t => t.X < NoteLayout.TableWidth / 2), t => Assert.True(t.X + metrics.Width(t.Text, t.Style) <= NoteLayout.TableWidth / 2 - NoteLayout.CellPad + 0.001));
      Assert.True(r.Height > 2 * NoteLayout.LineHeight);
      Assert.Equal(r.Lines * NoteLayout.LineHeight, r.Height);
    }

    // ---- the bound -----------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_bound_is_the_briefs()
    {
      Assert.Equal(24, NoteLayout.MaxLines);
      Assert.Equal(1200, NoteLayout.MaxChars);
    }

    [Fact]
    public void A_note_at_the_line_bound_is_whole()
    {
      var r = Lay(string.Join("\n", Enumerable.Range(0, 24).Select(i => "- line " + i)));
      Assert.Equal(24, r.Lines);
      Assert.False(r.Cut);
      Assert.DoesNotContain(NoteLayout.AndMore, Words(r));
    }

    [Fact]
    public void One_line_past_the_bound_says_and_more()
    {
      var r = Lay(string.Join("\n", Enumerable.Range(0, 25).Select(i => "- line " + i)));
      Assert.Equal(24, r.Lines);
      Assert.True(r.Cut);
      Assert.Equal(NoteLayout.AndMore, r.Text[r.Text.Count - 1].Text);
      Assert.Equal(NoteStyle.Bold, r.Text[r.Text.Count - 1].Style);
      Assert.DoesNotContain("24", Words(r));
    }

    /// <summary>Half the width per character: a line holds 73, so 24 lines would hold far past the bound, and the characters are what stop it.</summary>
    private sealed class Narrow : INoteMetrics
    {
      public double Width(string text, NoteStyle style) => (text ?? "").Length * 3;
    }

    [Fact]
    public void The_character_bound_cuts_before_the_line_bound_where_the_lines_are_long()
    {
      var r = NoteLayout.Build(NoteMarkdown.Parse(string.Join(" ", Enumerable.Repeat(new string('x', 72), 40))), new Narrow());
      Assert.True(r.Cut);
      Assert.True(r.Chars <= NoteLayout.MaxChars, r.Chars.ToString());
      Assert.True(r.Lines < NoteLayout.MaxLines, r.Lines.ToString());
    }

    [Fact]
    public void A_row_of_two_lines_that_would_pass_the_bound_is_not_begun()
    {
      var tall = new string('x', 40);   // two lines in a column of 152
      var rows = string.Join("\n", Enumerable.Range(0, 30).Select(i => "| " + tall + " | v" + i + " |"));
      var r = Lay("| a | b |\n|---|---|\n" + rows);
      Assert.True(r.Cut);
      Assert.Equal(23, r.Lines);   // the header and eleven rows of two: a twelfth would make 25
      Assert.DoesNotContain("v11", r.Text.Select(t => t.Text));
    }

    [Fact]
    public void A_word_breaks_at_the_cells_padding_not_its_edge()
    {
      var r = Lay("| a | b |\n|---|---|\n| " + new string('x', 26) + " | y |");
      var metrics = new Fixed();
      Assert.All(r.Text.Where(t => t.Text.StartsWith("x", StringComparison.Ordinal)), t => Assert.True(t.X + metrics.Width(t.Text, t.Style) <= NoteLayout.TableWidth / 2 - NoteLayout.CellPad + 0.001, t.Text));
    }

    [Fact]
    public void A_long_note_stops_at_the_character_bound_in_short_lines_too()
    {
      var r = Lay(new string('x', 2000));
      Assert.True(r.Cut);
      Assert.True(r.Chars <= NoteLayout.MaxChars);
    }

    [Fact]
    public void The_height_counts_the_and_more_line()
    {
      var r = Lay(string.Join("\n", Enumerable.Range(0, 25).Select(i => "- line " + i)));
      var last = r.Text[r.Text.Count - 1];
      Assert.Equal(last.Y + NoteLayout.LineHeight, r.Height, 6);
    }

    [Fact]
    public void A_table_never_loses_part_of_a_row()
    {
      var rows = string.Join("\n", Enumerable.Range(0, 30).Select(i => "| r" + i + " | v" + i + " |"));
      var r = Lay("| a | b |\n|---|---|\n" + rows);
      Assert.True(r.Cut);
      Assert.Equal(24, r.Lines);
      var words = r.Text.Select(t => t.Text).ToList();
      Assert.Contains("r22", words);
      Assert.Contains("v22", words);
      Assert.DoesNotContain("r23", words);
      Assert.DoesNotContain("v23", words);
    }

    [Fact]
    public void A_cut_table_still_closes_with_its_rules()
    {
      var rows = string.Join("\n", Enumerable.Range(0, 30).Select(i => "| r" + i + " | v" + i + " |"));
      var r = Lay("| a | b |\n|---|---|\n" + rows);
      var bottom = 24 * NoteLayout.LineHeight;
      Assert.Contains(r.Rules, l => l.Y1 == l.Y2 && Math.Abs(l.Y1 - bottom) < 0.001);
      Assert.All(r.Rules.Where(l => l.X1 == l.X2), l => Assert.Equal(bottom, l.Y2, 6));
    }

    [Fact]
    public void A_first_row_too_big_for_the_bound_draws_no_table_and_still_says_and_more()
    {
      var r = Lay("| " + string.Join(" ", Enumerable.Repeat("word", 300)) + " | b |\n|---|---|\n| 1 | 2 |");
      Assert.True(r.Cut);
      Assert.Empty(r.Rules);
      Assert.Equal(NoteLayout.AndMore, Assert.Single(r.Text).Text);
    }

    [Fact]
    public void A_lines_neighbours_of_one_style_are_one_stretch_so_inline_code_is_one_chip()
    {
      var r = Lay("the `Loft Surface node` here");
      Assert.Equal(new[] { "the ", "Loft Surface node", " here" }, r.Text.Select(t => t.Text));
    }

    [Fact]
    public void The_card_is_the_body_with_its_padding_tag_and_stale_line()
    {
      var r = Lay("word");
      Assert.Equal(NoteLayout.PlainWidth + 2 * NoteLayout.Pad, NoteLayout.CardWidth(r));
      Assert.Equal(NoteLayout.Pad + NoteLayout.TagRow + NoteLayout.TagGap + NoteLayout.LineHeight + NoteLayout.Pad, NoteLayout.CardHeight(r, false), 6);
      Assert.Equal(NoteLayout.CardHeight(r, false) + NoteLayout.BlockGap + NoteLayout.LineHeight, NoteLayout.CardHeight(r, true), 6);
    }

    [Fact]
    public void No_blocks_is_an_empty_card_body()
    {
      var r = NoteLayout.Build(new List<NoteBlock>(), new Fixed());
      Assert.Equal(0, r.Height);
      Assert.Empty(r.Text);
      Assert.False(r.Cut);
    }
  }
}
