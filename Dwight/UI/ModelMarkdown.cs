using System;
using System.Linq;
using System.Text;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Dwight.UI
{
  /// <summary>
  /// THE ONE PLACE MODEL TEXT BECOMES HTML (ADR-088 rule 1).
  ///
  /// Model output is rendered through a Markdig pipeline with <c>DisableHtml()</c>, so markup inside a
  /// model reply - <c>&lt;script&gt;</c>, <c>&lt;img onerror&gt;</c>, an <c>&lt;iframe&gt;</c> - arrives
  /// in the WebView as ESCAPED TEXT and never as live HTML. The canvas is an untrusted input surface
  /// (a nickname, a panel string, a runtime message all flow into a prompt), so a model reply is
  /// untrusted text by construction, not by assumption.
  ///
  /// WHY IT IS ITS OWN FILE, extracted at Phase-8 WP-3 Stage 0. The pipeline lived privately inside
  /// <see cref="WebPanel"/>, which was correct while the panel body was the only place model text
  /// reached the shell. WP-3's chat agent adds a SECOND such place - <c>dwightChatAppend</c> injects
  /// with <c>insertAdjacentHTML</c> - and a second private copy of a security rule is how one copy
  /// later drifts from the other. One definition, both callers.
  ///
  /// DISABLEHTML ONLY COVERS RAW HTML. A Markdown link is not HTML to it, so <c>[x](javascript:...)</c> came out as a live href, and the shells' CSP allows inline script, which
  /// is what lets a <c>javascript:</c> URL run. So every pipeline here passes through <see cref="HoldLinks"/>, and the snippet pipeline moved in from LlmActions to be one of them.
  ///
  /// GH-free and Eto-free, so it is unit-tested outside Rhino (ADR-091) - the escaping claim is
  /// asserted rather than trusted.
  /// </summary>
  internal static class ModelMarkdown
  {
    private static readonly MarkdownPipeline Pipeline =
      Holding(new MarkdownPipelineBuilder().DisableHtml().UsePipeTables().UseEmphasisExtras().UseAutoLinks());

    private static readonly MarkdownPipeline SnippetPipeline =
      Holding(new MarkdownPipelineBuilder().DisableHtml().UseEmphasisExtras());

    private static MarkdownPipeline Holding(MarkdownPipelineBuilder builder)
    {
      builder.DocumentProcessed += HoldLinks;
      return builder.Build();
    }

    /// <summary>Render model-authored Markdown to HTML safe to inject. Never throws: a pipeline fault
    /// degrades to the fully-escaped text, which is the safe direction (ADR-029).</summary>
    public static string ToHtml(string markdown)
    {
      var raw = markdown ?? "";
      try { return Markdown.ToHtml(raw, Pipeline); }
      catch { return "<p>" + System.Net.WebUtility.HtmlEncode(raw) + "</p>"; }
    }

    /// <summary>Model-authored Markdown as inline HTML, for a short snippet: a step's sentence, a fault's cause and fix. The outer paragraph wrapper is stripped. Never throws, as <see cref="ToHtml"/> does not.</summary>
    public static string Snippet(string text)
    {
      if (string.IsNullOrWhiteSpace(text)) return "";
      string html;
      try { html = Markdown.ToHtml(text, SnippetPipeline).Trim(); }
      catch { return System.Net.WebUtility.HtmlEncode(text); }
      if (html.StartsWith("<p>", StringComparison.Ordinal) && html.EndsWith("</p>", StringComparison.Ordinal))
        html = html.Substring(3, html.Length - 7);
      return html;
    }

    /// <summary>
    /// A LINK IN MODEL TEXT KEEPS ITS TARGET ONLY IF THAT IS HTTP OR HTTPS. Anything else renders as its words with no anchor: <c>javascript:</c>, <c>data:</c>, <c>file:</c>, a mailto, and a relative or
    /// fragment href, which nothing here ever writes on purpose. An image is held to the same rule, and renders as its alt text. Read off the parsed document, after every parser has run, so it does not matter
    /// how the link was spelt: inline, by reference, as an autolink, or with its scheme in entities.
    /// </summary>
    internal static void HoldLinks(MarkdownDocument document)
    {
      foreach (var link in document.Descendants<LinkInline>().ToList())
      {
        var url = link.GetDynamicUrl != null ? link.GetDynamicUrl() ?? link.Url : link.Url;   // what the renderer draws, so what is judged. No parser here sets it today; an extension can.
        var cleaned = WhatABrowserReads(url);
        if (!IsWeb(cleaned)) { Unwrap(link); continue; }
        if (cleaned == url) continue;
        link.GetDynamicUrl = null;   // draw the target that was judged, or a stripped space would leave a dead relative href behind it
        link.Url = cleaned;
      }
      foreach (var auto in document.Descendants<AutolinkInline>().ToList())
        if (auto.IsEmail || !IsWeb(WhatABrowserReads(auto.Url)))
          auto.ReplaceBy(new LiteralInline(auto.Url ?? ""));
    }

    /// <summary>The URL with what a browser ignores before it reads a scheme taken out: control characters and spaces at the ends, and a tab or a newline anywhere. So <c>java&#9;script:</c> is judged as <c>javascript:</c>.</summary>
    internal static string WhatABrowserReads(string url)
    {
      if (string.IsNullOrEmpty(url)) return "";
      var kept = new StringBuilder(url.Length);
      foreach (var c in url)
        if (c != '\t' && c != '\n' && c != '\r') kept.Append(c);
      var s = kept.ToString();
      var from = 0;
      var to = s.Length;
      while (from < to && (char.IsControl(s[from]) || char.IsWhiteSpace(s[from]))) from++;
      while (to > from && (char.IsControl(s[to - 1]) || char.IsWhiteSpace(s[to - 1]))) to--;
      return s.Substring(from, to - from);
    }

    private static bool IsWeb(string cleaned) =>
      cleaned.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || cleaned.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// <summary>The link's own words take its place. An image's are its alt text.</summary>
    private static void Unwrap(LinkInline link)
    {
      var child = link.FirstChild;
      while (child != null)
      {
        var next = child.NextSibling;
        child.Remove();
        link.InsertBefore(child);
        child = next;
      }
      link.Remove();
    }
  }
}
