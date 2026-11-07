using Dwight.UI;
using Xunit;

namespace Dwight.Session.Tests
{
  /// <summary>
  /// ADR-088 rule 1, asserted rather than trusted: model text becomes HTML in exactly one place, and
  /// markup inside it arrives ESCAPED.
  ///
  /// Why these tests exist now: until WP-3 the only place model text reached the shell was the panel
  /// body. The chat append is the second, and it injects with insertAdjacentHTML - so the escaping claim
  /// is about to carry twice the weight it did, on a surface where the author is an agent whose input
  /// includes canvas text the user did not write.
  /// </summary>
  public class ModelMarkdownTests
  {
    [Theory]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("<img src=x onerror=\"alert(1)\">")]
    [InlineData("<iframe src=\"https://example.com\"></iframe>")]
    [InlineData("<a href=\"javascript:alert(1)\">click</a>")]
    public void Raw_markup_in_model_text_is_escaped_not_executed(string hostile)
    {
      var html = ModelMarkdown.ToHtml("Here is what I found: " + hostile);

      // The dangerous form is the LIVE tag; the escaped form is the safe one, and it must be present -
      // asserting only "no <script>" would also pass if the text had been silently dropped.
      Assert.DoesNotContain(hostile, html);
      Assert.Contains("&lt;", html);
    }

    /// <summary>U82. The reply of a cited answer is built from consecutive text blocks joined with nothing between them, so a
    /// table row a citation split is whole again and the pipeline's pipe-table extension renders a table. The CONTROL is the
    /// same row with a newline put where the join was: that is the old behaviour, and it renders no table.</summary>
    [Fact]
    public void A_table_whose_row_was_split_by_a_citation_renders_as_a_table_when_the_blocks_are_joined_verbatim()
    {
      var joined = "| Part | Length |\n|---|---|\n| Bridge | 12" + " m |\n| Hall | 30" + " m |\n\nFirst paragraph.";
      var html = ModelMarkdown.ToHtml(joined);

      Assert.Contains("<table>", html);
      Assert.Contains("<td>Bridge</td>", html);
      Assert.Contains("<td>12 m</td>", html);
      Assert.Contains("<td>30 m</td>", html);

      var brokenAtTheJoin = "| Part | Length |\n|---|---|\n| Bridge | 12" + "\n m |\n| Hall | 30" + "\n m |\n\nFirst paragraph.";
      Assert.DoesNotContain("<td>12 m</td>", ModelMarkdown.ToHtml(brokenAtTheJoin));
    }

    [Fact]
    public void Ordinary_markdown_still_formats()
    {
      var html = ModelMarkdown.ToHtml("**bold** and `Loft` and\n\n- one\n- two");

      Assert.Contains("<strong>bold</strong>", html);
      Assert.Contains("<code>Loft</code>", html);
      Assert.Contains("<li>one</li>", html);
    }

    [Fact]
    public void A_table_renders_because_the_pipeline_carries_pipe_tables()
    {
      // Guards the extracted pipeline against silently losing an extension the panel already shipped -
      // the extraction's whole claim is that it is byte-identical to what it replaced.
      var html = ModelMarkdown.ToHtml("| a | b |\n|---|---|\n| 1 | 2 |");
      Assert.Contains("<table>", html);
    }

    /// <summary>
    /// #315: the surface renders Markdown and NOT LaTeX, so the model is told so in the system prompt.
    /// This is the other end of that claim - the prompt line is derived from what this renderer actually
    /// does, and if math rendering is ever added the assertion below fails and points at the prompt that
    /// still says it will not. A promise made to a model has to be kept by the surface.
    /// </summary>
    [Fact]
    public void LaTeX_is_not_rendered_it_reaches_the_screen_as_literal_text()
    {
      var dollars = ModelMarkdown.ToHtml("The height is $h = r \\cos(\\theta)$ at the top.");
      Assert.Contains("$h = r", dollars);          // still there, unrendered
      Assert.DoesNotContain("<math", dollars);
      Assert.DoesNotContain("katex", dollars);

      var env = ModelMarkdown.ToHtml("\\begin{equation} E = mc^2 \\end{equation}");
      Assert.Contains("begin{equation}", env);
      Assert.DoesNotContain("<math", env);
    }

    [Fact]
    public void Null_and_empty_are_safe()
    {
      Assert.Equal("", ModelMarkdown.ToHtml(null).Trim());
      Assert.Equal("", ModelMarkdown.ToHtml("").Trim());
    }
  }
}
