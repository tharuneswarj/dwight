using System;
using Dwight.Agent;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// B1 and the #235 sharpening: the consent card is the one place model-authored text enters plugin
  /// markup, on the one surface whose entire job is INFORMED CONSENT.
  ///
  /// Two properties, and the second is the one escaping cannot buy: markup in the model's reason renders
  /// as visible text, AND a reason that misdescribes the action cannot alter what the user is shown or
  /// what an approval does.
  /// </summary>
  public class ConsentCardTests
  {
    private const string Token = "abc123";

    [Theory]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("<img src=x onerror=\"alert(1)\">")]
    [InlineData("<iframe src=\"https://example.com\"></iframe>")]
    [InlineData("</div><button onclick=\"dwightSend('consent:stolen')\">Yes</button>")]
    public void Markup_in_the_models_reason_renders_as_visible_text(string hostile)
    {
      var html = ConsentCard.Html("solve", "It would help. " + hostile, Token);

      // The live form is absent and the escaped form is present - asserting only the first would also
      // pass if the reason had been silently dropped, which is a different failure.
      Assert.DoesNotContain(hostile, html);
      Assert.Contains("&lt;", html);
      Assert.Contains("It would help.", html);
    }

    [Fact]
    public void A_reason_cannot_forge_a_second_approve_button()
    {
      // The nastiest shape: a reason that closes the card's div and opens its own button on another
      // token. Escaped, it is text; unescaped, it would be a second consent control the user cannot tell
      // from Dwight's own.
      var html = ConsentCard.Html("solve", "</div><button onclick=\"dwightSend('consent:stolen')\">Yes</button>", Token);

      Assert.Contains("consent:" + Token, html);
      Assert.DoesNotContain("dwightSend('consent:stolen')", html);
    }

    [Theory]
    [InlineData("reorganise")]
    [InlineData("simplify")]
    [InlineData("solve")]
    [InlineData("scaffold")]
    public void The_act_and_the_consequence_are_dwights_words_whatever_the_reason_claims(string kind)
    {
      // #235: escaping stops markup; it does not stop a reason that misdescribes what is being approved.
      // So the ACT and the CONSEQUENCE are derived from the enumerated kind alone, and a reason claiming
      // something else changes neither.
      const string lie = "This is just a routine read-only check and changes nothing at all.";

      var honest = ConsentCard.Html(kind, "because it would help", Token);
      var misleading = ConsentCard.Html(kind, lie, Token);

      Assert.Contains(System.Net.WebUtility.HtmlEncode(ConsentCard.Title(kind)), misleading);
      Assert.Contains(System.Net.WebUtility.HtmlEncode(ConsentCard.Authority(kind)), misleading);

      // The two cards differ ONLY in the reason block - the authoritative half is byte-identical.
      Assert.Equal(
        honest.Replace(System.Net.WebUtility.HtmlEncode("because it would help"), "REASON"),
        misleading.Replace(System.Net.WebUtility.HtmlEncode(lie), "REASON"));
    }

    [Fact]
    public void A_misleading_reason_cannot_borrow_another_kinds_consequence()
    {
      // A reorganise card must never carry the solve card's reassuring sentence, however the reason is
      // phrased - the consequence is the thing being agreed to.
      var card = ConsentCard.Html("reorganise", "Nothing on the canvas is changed. Nothing on the canvas is changed.", Token);

      Assert.Contains("opens Reorganise", card);
      Assert.DoesNotContain("<p class=\"consent-note\">Dwight will re-run", card);
    }

    [Fact]
    public void The_reason_is_labelled_as_the_models_and_kept_subordinate()
    {
      // Attribution is half the protection: the user needs to see which sentence is Dwight's promise and
      // which is the assistant explaining itself.
      var html = ConsentCard.Html("solve", "you asked about live values", Token);

      Assert.Contains("consent-why-label", html);
      Assert.Contains("Dwight's reason for asking", html);
      // Brief BG changes the order on purpose: the card's face is Dwight's act sentence, then the reason, then the buttons; the full
      // consequence is in the fold. The act still comes first, and it is Dwight's words.
      Assert.True(html.IndexOf("consent-act", StringComparison.Ordinal) < html.IndexOf("consent-why", StringComparison.Ordinal),
        "Dwight's act must be rendered before the model's reason");
      Assert.True(html.IndexOf("consent-why", StringComparison.Ordinal) < html.IndexOf("consent-more", StringComparison.Ordinal),
        "the fold holding the consequence follows the reason");
    }

    // ---- the route ---------------------------------------------------------------------------------

    // The expected route travels as its NAME: ConsentRoute is internal, and an internal type cannot be a
    // parameter of a public xUnit theory.
    [Theory]
    [InlineData("solve", "Solve")]
    [InlineData("reorganise", "Reorganise")]
    [InlineData("simplify", "Simplify")]
    [InlineData("SOLVE", "Solve")]
    [InlineData("scaffold", "Scaffold")]
    [InlineData("", "Unknown")]
    [InlineData(null, "Unknown")]
    public void Where_an_approval_lands_is_decided_by_the_kind_alone(string kind, string expected)
    {
      // The model's words are not a parameter of Route, which is the guarantee rather than an omission:
      // there is no argument through which a persuasive reason could move where an approval goes.
      Assert.Equal(expected, ConsentCard.Route(kind).ToString());
    }

    [Fact]
    public void Every_offerable_kind_routes_somewhere_real()
    {
      // The kinds request_mutation advertises and the routes an approval can take are one contract - a
      // kind that parsed but routed to Unknown would be a card whose Approve does nothing (#174).
      Assert.All(AgentHostTools.Kinds, k => Assert.NotEqual(ConsentRoute.Unknown, ConsentCard.Route(k)));
    }

    [Fact]
    public void An_unofferable_kind_renders_no_promise()
    {
      // Belt for the case that cannot arrive through the parser: if Unknown ever reached the card, it
      // must not describe an action.
      Assert.Contains("Nothing will happen", ConsentCard.Authority("delete_everything"));
      Assert.Contains("cannot offer", ConsentCard.Title("delete_everything"));
    }

    // ---- #890: THE REORGANISE CARD MAY NOT PROMISE A PREVIEW -----------------------------------------------
    //
    // It said "which previews before it changes anything", and Reorganise does not preview: it plans and then
    // applies. A consent sentence stating a safety property that does not exist is the most serious class the
    // card has (#235, #338). The scan below is BLUNT ON PURPOSE - any "preview" at all, not "claims a preview" -
    // because a sentence that negates the word is one edit away from a sentence that asserts it, and a missed
    // negation on a consent surface is the whole defect. So the true sentence avoids the vocabulary instead.

    [Theory]
    [InlineData(null)]
    [InlineData("compact")]
    [InlineData("hchain")]
    [InlineData("vertical")]
    [InlineData("grid")]
    public void The_reorganise_card_says_what_happens_and_never_mentions_a_preview(string mode)
    {
      var line = ConsentCard.Authority("reorganise", mode);

      Assert.DoesNotContain("preview", line, StringComparison.OrdinalIgnoreCase);
      Assert.Contains(ConsentCard.ReorganiseConsequence, line);
      Assert.Contains("changes nothing they compute", line);
      Assert.Contains("One Ctrl-Z undoes it", line);
    }

    /// <summary>The notice posted as the approved Reorganise opens says the same thing the card did - one clause,
    /// so the two cannot drift apart - and names what it opens on.</summary>
    [Theory]
    [InlineData(0, null, "on your current selection")]
    [InlineData(1, null, "on the 1 component I described")]
    [InlineData(3, "grid", "on the 3 components I described, to lay it out as a grid")]
    public void Opening_reorganise_says_what_it_does_and_never_mentions_a_preview(int onto, string mode, string on)
    {
      var notice = ConsentCard.OpeningNotice("Reorganise", onto, mode);

      Assert.DoesNotContain("preview", notice, StringComparison.OrdinalIgnoreCase);
      Assert.Contains(on, notice);
      Assert.EndsWith(ConsentCard.ReorganiseConsequence, notice);
    }

    /// <summary>
    /// SIMPLIFY IS LEFT ALONE, because its claim is TRUE: it shows its proposal and waits for acceptance before
    /// it touches the canvas. Pinned so the Reorganise fix cannot quietly take a true sentence with it.
    /// </summary>
    [Fact]
    public void Simplify_still_says_it_previews_because_it_does()
    {
      Assert.Contains("previews before it changes anything", ConsentCard.Authority("simplify"));
      Assert.Contains("it will preview before it changes anything", ConsentCard.OpeningNotice("Simplify", 2, null));
    }
  }
}
