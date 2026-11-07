using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dwight.UI;
using Markdig;
using Markdig.Syntax;
using Xunit;

namespace Dwight.Session.Tests
{
  /// <summary>
  /// ADR-088's claim gains the test it lacked. "Markup in model text arrives escaped" held for raw HTML, and <c>DisableHtml</c> does not touch a Markdown link, so <c>[x](javascript:alert(1))</c> came out as a
  /// live href on both pipelines, under a CSP that allows inline script. A model reply is untrusted (a panel string in someone's definition flows into a prompt), and script in the panel can reach
  /// <c>dwightSend</c>. Every case runs on both pipelines, because the defect was on both and the rule is one.
  /// </summary>
  public class ModelMarkdownLinkTests
  {
    public static IEnumerable<object[]> Both(params string[] cases) =>
      cases.SelectMany(c => new[] { new object[] { "model", c }, new object[] { "snippet", c } });

    private static string Render(string pipeline, string markdown) =>
      pipeline == "model" ? ModelMarkdown.ToHtml(markdown) : ModelMarkdown.Snippet(markdown);

    public static IEnumerable<object[]> Hostile => Both(
      "[click](javascript:alert(1))",
      "[click](JAVASCRIPT:alert(1))",
      "[click](JaVaScRiPt:alert(1))",
      "[click](data:text/html,<script>alert(1)</script>)",
      "[click](data:text/html;base64,PHNjcmlwdD4=)",
      "[click](vbscript:msgbox(1))",
      "[click](file:///C:/Windows/System32/calc.exe)",
      "[click](mailto:a@example.com)",
      "[click](ftp://example.com/x)",
      "[click](< javascript:alert(1)>)",        // leading whitespace
      "[click](<\u0001javascript:alert(1)>)",    // a leading control character
      "[click](<\u001fjavascript:alert(1)>)",
      "[click](<java\tscript:alert(1)>)",        // a tab a browser takes out of the scheme
      "[click](&#106;avascript:alert(1))",       // the scheme in entities
      "[click](javascript&#58;alert(1))",
      "[click](javascript&colon;alert(1))",
      "[click][r]\n\n[r]: javascript:alert(1)",  // by reference
      "[click](foo/bar)",                        // no scheme at all
      "[click](#section)",
      "[click](//example.com/x)",
      "[click]()");

    [Theory]
    [MemberData(nameof(Hostile))]
    public void A_link_with_any_other_target_renders_as_its_words_with_no_anchor(string pipeline, string markdown)
    {
      var html = Render(pipeline, markdown);
      Assert.Contains("click", html);          // the words are kept: a control on a rule that could pass by dropping everything
      Assert.DoesNotContain("<a", html);
      Assert.DoesNotContain("href", html);
    }

    public static IEnumerable<object[]> HostileAutolinks => Both("<javascript:alert(1)>", "<JAVASCRIPT:alert(1)>", "<data:text/html,x>", "<vbscript:x>", "<file:///C:/x>", "<a@example.com>");

    [Theory]
    [MemberData(nameof(HostileAutolinks))]
    public void An_autolink_with_any_other_target_is_its_own_text(string pipeline, string markdown)
    {
      var html = Render(pipeline, markdown);
      Assert.DoesNotContain("<a", html);
      Assert.DoesNotContain("href", html);
      Assert.Contains(markdown.Trim('<', '>'), html);
    }

    public static IEnumerable<object[]> HostileImages => Both(
      "![click](javascript:alert(1))", "![click](JAVASCRIPT:alert(1))", "![click](data:image/svg+xml,<svg/>)", "![click](data:image/png;base64,iVBORw0KGgo=)",
      "![click](vbscript:x)", "![click](file:///C:/x.png)", "![click](x.png)", "![click](< javascript:alert(1)>)", "![click][r]\n\n[r]: javascript:alert(1)");

    /// <summary><c>DisableHtml</c> does not cover a Markdown image either, and the shells' CSP lets a <c>data:</c> image load, so an image is held to the rule, and its alt text stands.</summary>
    [Theory]
    [MemberData(nameof(HostileImages))]
    public void An_image_with_any_other_source_renders_as_its_alt_text(string pipeline, string markdown)
    {
      var html = Render(pipeline, markdown);
      Assert.Contains("click", html);
      Assert.DoesNotContain("<img", html);
      Assert.DoesNotContain("src", html);
    }

    public static IEnumerable<object[]> Web => Both(
      "[click](https://example.com/a?b=1)", "[click](http://example.com)", "[click](HTTPS://EXAMPLE.COM)", "[click](HtTp://example.com)", "<https://example.com>", "[click][r]\n\n[r]: https://example.com");

    [Theory]
    [MemberData(nameof(Web))]
    public void An_http_or_https_link_keeps_its_target(string pipeline, string markdown)
    {
      var html = Render(pipeline, markdown);
      Assert.Contains("<a href=\"", html);
      Assert.Contains("example.com", html, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("model")]
    [InlineData("snippet")]
    public void An_http_image_keeps_its_source_because_the_rule_is_one_and_the_csp_holds_its_load(string pipeline) =>
      Assert.Contains("<img src=\"https://example.com/a.png\"", Render(pipeline, "![click](https://example.com/a.png)"));

    [Fact]
    public void A_bare_address_still_autolinks_in_the_full_pipeline()
    {
      Assert.Contains("<a href=\"https://example.com\"", ModelMarkdown.ToHtml("See https://example.com for it."));
      Assert.Contains("<a href=\"http://www.example.com\"", ModelMarkdown.ToHtml("See www.example.com for it."));
    }

    [Theory]
    [InlineData("mailto:a@example.com", "a@example.com")]     // the autolinker shows these two without their scheme
    [InlineData("tel:+15551234", "+15551234")]
    [InlineData("ftp://example.com/x", "ftp://example.com/x")]
    public void A_bare_address_of_another_kind_stays_as_words(string address, string words)
    {
      var html = ModelMarkdown.ToHtml("Write to " + address + " today.");
      Assert.DoesNotContain("<a", html);
      Assert.Contains("Write to " + words + " today.", html);
    }

    [Theory]
    [InlineData("model")]
    [InlineData("snippet")]
    public void A_target_with_a_space_or_a_tab_in_front_is_drawn_as_the_target_that_was_judged(string pipeline)
    {
      Assert.Contains("<a href=\"https://example.com\"", Render(pipeline, "[click](< https://example.com>)"));
      Assert.Contains("<a href=\"https://example.com\"", Render(pipeline, "[click](<ht\ttps://example.com>)"));
    }

    [Theory]
    [InlineData("model")]
    [InlineData("snippet")]
    public void What_a_held_link_says_stays_marked_and_an_image_inside_it_is_judged_on_its_own(string pipeline)
    {
      var marked = Render(pipeline, "[**bold** and `code`](javascript:alert(1))");
      Assert.Contains("<strong>bold</strong>", marked);
      Assert.Contains("<code>code</code>", marked);
      Assert.DoesNotContain("<a", marked);

      var nested = Render(pipeline, "[![pic](data:image/png;base64,AA==)](https://example.com)");
      Assert.Contains("<a href=\"https://example.com\"", nested);
      Assert.Contains("pic", nested);
      Assert.DoesNotContain("<img", nested);
    }

    [Theory]
    [InlineData("model")]
    [InlineData("snippet")]
    public void Text_about_a_scheme_is_words_and_a_link_inside_a_longer_sentence_leaves_the_sentence(string pipeline)
    {
      var html = Render(pipeline, "Before [click](javascript:alert(1)) after, and the word javascript: in a sentence.");
      Assert.Contains("Before click after, and the word javascript: in a sentence.", html);
      Assert.DoesNotContain("<a", html);
    }

    [Fact]
    public void A_link_in_a_table_cell_is_held_like_any_other()
    {
      var html = ModelMarkdown.ToHtml("| a |\n|---|\n| [click](javascript:alert(1)) |");
      Assert.Contains("<table>", html);
      Assert.Contains("click", html);
      Assert.DoesNotContain("<a", html);
    }

    /// <summary>
    /// The gate's probe, held to being a real link. My first string had a space in its target, and CommonMark does not take that as a link at all, so it made no anchor and a null read beside it would have
    /// been a false "does not run". The control is a pipeline with no rule: this string is a link there, with the target percent-encoded as Rhino 2 measured it, and the space form is not.
    /// </summary>
    [Fact]
    public void The_gates_probe_string_is_a_real_link_and_the_rule_holds_it()
    {
      const string probe = "[probe](javascript:void(document.documentElement.setAttribute('data-dwight-probe','ran')))";
      var noRule = new Markdig.MarkdownPipelineBuilder().DisableHtml().Build();

      Assert.Contains("<a href=\"javascript:void(document.documentElement.setAttribute(%27data-dwight-probe%27,%27ran%27))\">probe</a>", Markdig.Markdown.ToHtml(probe, noRule));
      Assert.DoesNotContain("<a", Markdig.Markdown.ToHtml("[probe](javascript:a b)", noRule));

      foreach (var html in new[] { ModelMarkdown.ToHtml(probe), ModelMarkdown.Snippet(probe) })
      {
        Assert.Contains("probe", html);
        Assert.DoesNotContain("<a", html);
        Assert.DoesNotContain("javascript", html);
      }
    }

    private const string BS = "\\\\";
    private const string Q = BS + "\"";

    /// <summary>The second wall is one script, in the three shells that draw model text, and each is held to it here, because a shell that loses its placeholder loses the wall in silence.</summary>
    [Theory]
    [InlineData("panel-shell.html")]
    [InlineData("note.html")]
    [InlineData("walk-bar.html")]
    public void Every_shell_that_draws_model_text_carries_the_second_wall(string shell) =>
      Assert.Contains("/*__DWIGHT_LINKS__*/", Repo("Dwight", "Resources", shell));

    [Fact]
    public void The_second_wall_is_loaded_and_stands_ahead_of_everything_and_lets_the_plugins_own_anchors_through()
    {
      Assert.Contains(".Replace(\"/*__DWIGHT_LINKS__*/\", Links)", Repo("Dwight", "UI", "ShellAssets.cs"));
      Assert.Contains("LogicalName=\"Dwight.Links.js\"", Repo("Dwight", "Dwight.csproj"));

      var wall = Repo("Dwight", "Resources", "dwight-links.js");
      Assert.Contains("document.addEventListener('click',guard,true);", wall);
      Assert.Contains("e.preventDefault(); e.stopPropagation();", wall);
      Assert.Contains("if(href==='#'&&a.classList.contains('anch')) return;", wall);

      // The exception is exactly what the plugin writes. Every writer in the plugin is read, because one that changed its shape would have its clicks go dead here, and this says why.
      var dir = new DirectoryInfo(AppContext.BaseDirectory);
      while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Dwight.sln"))) dir = dir.Parent;
      var anchors = Directory.GetFiles(Path.Combine(dir.FullName, "Dwight"), "*.cs", SearchOption.AllDirectories)
        .SelectMany(f => System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(f), "<a class=" + Q + "anch[^>]*?href=" + Q + "([^" + BS + "]*)" + Q).Cast<System.Text.RegularExpressions.Match>())
        .ToList();
      // Three writers since Brief EC unit 5b: the graduation line's own anchor went to the model door, where Linkify writes it like any of Dwight's words.
      Assert.True(anchors.Count >= 3, "read only " + anchors.Count + " anchor writers; the reader no longer matches the writers");
      Assert.All(anchors, m => Assert.Equal("#", m.Groups[1].Value));
    }

    /// <summary>The renderer draws a dynamic URL ahead of <c>Url</c>. Nothing in these pipelines sets one today, so this sets it by hand: the rule must judge what would be drawn, and draw what it judged.</summary>
    [Fact]
    public void What_the_renderer_would_draw_is_what_is_judged_even_from_a_dynamic_url()
    {
      static string Held(Func<string> dynamicUrl)
      {
        var doc = Markdig.Markdown.Parse("[click](https://safe.example)");
        doc.Descendants<Markdig.Syntax.Inlines.LinkInline>().Single().GetDynamicUrl = () => dynamicUrl();
        ModelMarkdown.HoldLinks(doc);
        using (var w = new StringWriter())
        {
          var renderer = new Markdig.Renderers.HtmlRenderer(w);
          renderer.Render(doc);
          w.Flush();
          return w.ToString();
        }
      }

      Assert.DoesNotContain("<a", Held(() => "javascript:alert(1)"));
      Assert.Contains("<a href=\"https://other.example\"", Held(() => " https://other.example"));
    }

    // ---- the snippet moved here, and is what it was ------------------------------------------------------------

    [Fact]
    public void A_snippet_renders_as_it_did_with_its_paragraph_stripped()
    {
      Assert.Equal("<strong>Lofts</strong> a surface through the <code>Curve</code> (8)", ModelMarkdown.Snippet("**Lofts** a surface through the `Curve` (8)"));
      Assert.Equal("", ModelMarkdown.Snippet(null));
      Assert.Equal("", ModelMarkdown.Snippet("   "));
      Assert.Contains("&lt;script&gt;", ModelMarkdown.Snippet("<script>alert(1)</script>"));
    }

    [Theory]
    [InlineData("", "")]
    [InlineData(null, "")]
    [InlineData("  https://a.example  ", "https://a.example")]
    [InlineData("\u0001\u001f https://a.example", "https://a.example")]
    [InlineData("ht\ttp\ns\r://a.example", "https://a.example")]
    public void A_url_is_judged_as_a_browser_reads_it(string url, string reads) => Assert.Equal(reads, ModelMarkdown.WhatABrowserReads(url));

    // ---- the one place, and what stays out of it, read from the source (ADR-091) ---------------------------------

    private static string Repo(params string[] parts)
    {
      var dir = new DirectoryInfo(AppContext.BaseDirectory);
      while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Dwight.sln"))) dir = dir.Parent;
      return File.ReadAllText(Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray()));
    }

    [Fact]
    public void Every_render_to_html_goes_through_the_one_place()
    {
      var dir = new DirectoryInfo(AppContext.BaseDirectory);
      while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Dwight.sln"))) dir = dir.Parent;
      var sources = Directory.GetFiles(Path.Combine(dir.FullName, "Dwight"), "*.cs", SearchOption.AllDirectories)
        .Select(f => (Path: f.Replace('\\', '/'), Text: File.ReadAllText(f))).ToList();

      Assert.EndsWith("UI/ModelMarkdown.cs", Assert.Single(sources, s => s.Text.Contains("Markdown.ToHtml(") && !s.Text.Contains("ModelMarkdown.ToHtml(") || s.Path.EndsWith("UI/ModelMarkdown.cs")).Path);
      Assert.Equal(2, sources.Single(s => s.Path.EndsWith("UI/ModelMarkdown.cs")).Text.Split("Holding(new MarkdownPipelineBuilder()").Length - 1);
      Assert.Contains("private static string Md(string text) => ModelMarkdown.Snippet(text);", sources.Single(s => s.Path.EndsWith("/LlmActions.cs")).Text);

      // The Eto fallback has a pipeline of its own and writes no HTML: it draws a link's words and holds no target, so it has nothing for a click to follow. Should it ever read one, this stops it.
      var builders = sources.Where(s => s.Text.Contains("new MarkdownPipelineBuilder()")).Select(s => s.Path).ToList();
      Assert.Equal(2, builders.Count);
      Assert.Contains(builders, p => p.EndsWith("UI/Drawing/DocumentView.cs"));
      var fallback = sources.Single(s => s.Path.EndsWith("UI/Drawing/DocumentView.cs")).Text;
      Assert.DoesNotContain("Markdown.ToHtml(", fallback);
      Assert.DoesNotContain(".Url", fallback);
      Assert.DoesNotContain("Process.Start", fallback);
    }

    /// <summary>The anchors are not Markdown links. They are written into the rendered HTML afterwards, by the plugin, so the rule cannot reach them and they keep their own test.</summary>
    [Fact]
    public void The_chats_id_anchors_are_written_after_the_render_so_the_rule_does_not_touch_them()
    {
      Assert.Contains("ChatAnchors.Linkify(ModelMarkdown.ToHtml(markdown), state)", Repo("Dwight", "UI", "DwightScaffold.cs"));
      Assert.Contains("(42)", ModelMarkdown.ToHtml("The Loft (42) feeds it."));
    }
  }
}
