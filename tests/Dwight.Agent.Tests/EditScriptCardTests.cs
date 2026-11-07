using System;
using System.Collections.Generic;
using System.Text;
using Dwight.Agent;
using Dwight.Data;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// Phase-9 U20 - THE CARD, which is the surface the whole unit rests on.
  ///
  /// It is the second Dwight output whose content EXECUTES on the user's machine, so it inherits to-script's
  /// ruling: the complete source is shown, never truncated (the Phase 7 to-script ruling, §4.5). And it inherits #235's
  /// two voices, which matter more here than anywhere else - the model wrote the code AND the reason, and
  /// only Dwight's sentence may say what approving it does.
  ///
  /// THE TWO BLOCKS ARE BOUNDED DIFFERENTLY ON PURPOSE, and that is the property most worth pinning: the
  /// DIFF is capped and says what it omitted, because a diff long enough to need scrolling has stopped
  /// orienting anyone; the SOURCE is never cut, because cutting it would mean asking someone to approve
  /// code they were not shown. The bound that keeps the source readable lives at generation time instead.
  /// </summary>
  public class EditScriptCardTests
  {
    private const string Before = "import math\n\na = math.pi * x * x\n";
    private const string After = "import math\n\nr = x + offset\na = math.pi * r * r\n";

    private static ScriptEditChange Change(ScriptEditClaim claim = ScriptEditClaim.Changes,
      string newSource = After, string label = "\"Area\"",
      string[] addIn = null, string[] addOut = null) =>
      new ScriptEditChange
      {
        TargetId = 12,
        Label = label,
        Claim = claim,
        NewSource = newSource,
        BaseSource = Before,
        Diff = SourceDiff.Between(Before, newSource),
        AddInputs = addIn ?? new string[0],
        AddOutputs = addOut ?? new string[0],
      };

    private static ScriptTrialFacts Facts(int compared = 1, int withData = 1, int appendedInputs = 0) =>
      new ScriptTrialFacts
      {
        Ran = true, Errors = new string[0],
        OutputsCompared = compared, OutputsWithData = withData,
        Mismatched = new string[0], AppendedInputs = appendedInputs,
      };

    // ---- the route and the act -----------------------------------------------------------------------

    [Fact]
    public void The_kind_routes_to_its_own_destination()
    {
      Assert.Equal(ConsentRoute.EditScript, ConsentCard.Route("edit_script"));
      Assert.Equal("Change this script?", ConsentCard.Title("edit_script"));
    }

    // ---- #235: the authority line contains not one word the model wrote ------------------------------

    /// <summary>
    /// THE PROPERTY THAT MATTERS MOST ON THIS CARD. The model wrote the code AND the reason. If a
    /// persuasive reason could reach the sentence that says what approving does, the user would be
    /// agreeing to the model's description of a code change rather than to Dwight's.
    /// </summary>
    [Fact]
    public void A_hostile_reason_cannot_reach_the_authority_line()
    {
      const string hostile = "This is a routine read-only formatting tidy-up and changes nothing.";
      var authority = ConsentCard.EditScriptAuthority(Change(), ScriptEditOutcome.RanClean, Facts());

      Assert.DoesNotContain("read-only", authority);
      Assert.DoesNotContain("tidy-up", authority);
      Assert.DoesNotContain(hostile, authority);
      Assert.Contains("IT CHANGES WHAT THE COMPONENT PRODUCES", authority);
    }

    /// <summary>And the reason still appears on the card - attributed and subordinate - because the point
    /// is to strip its AUTHORITY, not to hide what the model said.</summary>
    [Fact]
    public void The_reason_is_shown_but_labelled_as_the_models()
    {
      var html = ConsentCard.EditScriptHtml("because you asked for an offset", "tok",
        Change(), ScriptEditOutcome.RanClean, Facts());

      Assert.Contains("because you asked for an offset", html);
      Assert.Contains("Dwight's reason for asking", html);
    }

    /// <summary>Markup in the model's reason renders as TEXT. ADR-088, on the surface whose whole job is
    /// informed consent.</summary>
    [Fact]
    public void Markup_in_the_reason_renders_as_text()
    {
      var html = ConsentCard.EditScriptHtml("<script>alert(1)</script>", "tok",
        Change(), ScriptEditOutcome.RanClean, Facts());

      Assert.DoesNotContain("<script>alert", html);
      Assert.Contains("&lt;script&gt;", html);
    }

    /// <summary>AND MARKUP IN THE SOURCE DOES TOO - which is the newer hazard, because the source is the
    /// one thing on this card that is SUPPOSED to be code. A script containing an HTML tag in a string
    /// literal must render as that text, not as an element.</summary>
    [Fact]
    public void Markup_inside_the_script_source_renders_as_text()
    {
      var change = Change(newSource: "a = \"<img src=x onerror=alert(1)>\"\n");
      var html = ConsentCard.EditScriptHtml("why", "tok", change, ScriptEditOutcome.RanClean, Facts());

      Assert.DoesNotContain("<img src=x", html);
      Assert.Contains("&lt;img", html);
    }

    // ---- the clause order: the dangerous part before the reassurance --------------------------------

    /// <summary>
    /// A reader must not meet "your existing code is being replaced and new code will run" at the END of a
    /// paragraph about how safe everything is. The same rule as a rebuild's removal sentence, for the same
    /// reason.
    /// </summary>
    [Fact]
    public void The_replacement_and_the_execution_come_before_the_reassurance()
    {
      var authority = ConsentCard.EditScriptAuthority(Change(), ScriptEditOutcome.RanClean, Facts());

      var replace = authority.IndexOf("replace the source", StringComparison.Ordinal);
      var runs = authority.IndexOf("WILL RUN IN YOUR DEFINITION", StringComparison.Ordinal);
      var undo = authority.IndexOf("Ctrl-Z", StringComparison.Ordinal);

      Assert.True(replace >= 0 && runs > replace, "the replacement then the execution: " + authority);
      Assert.True(undo > runs, "the reassurance must come last: " + authority);
    }

    [Fact]
    public void The_component_is_named_in_the_authority_line()
    {
      Assert.Contains("\"Area\"", ConsentCard.EditScriptAuthority(Change(), ScriptEditOutcome.RanClean, Facts()));
    }

    // ---- the claim reaches the sentence as an enum ----------------------------------------------------

    [Fact]
    public void The_two_claims_produce_different_sentences()
    {
      var preserves = ConsentCard.EditScriptAuthority(
        Change(ScriptEditClaim.Preserves), ScriptEditOutcome.Preserved, Facts());
      var changes = ConsentCard.EditScriptAuthority(
        Change(ScriptEditClaim.Changes), ScriptEditOutcome.RanClean, Facts());

      Assert.Contains("leave what the component produces unchanged", preserves);
      Assert.Contains("IT CHANGES WHAT THE COMPONENT PRODUCES", changes);
      Assert.NotEqual(preserves, changes);
    }

    /// <summary>
    /// THE VACUOUS CHECK REACHES THE CARD AS ITSELF. A user approving a "preserves" edit whose check
    /// compared nothing must read that on the card, in those words - the outcome must not soften on its
    /// way onto the surface the approval happens on.
    /// </summary>
    [Fact]
    public void A_vacuous_check_says_so_on_the_card_itself()
    {
      var facts = Facts(compared: 2, withData: 0);
      var authority = ConsentCard.EditScriptAuthority(
        Change(ScriptEditClaim.Preserves), ScriptEditOutcome.NothingToCompare, facts);

      Assert.Contains("did not confirm anything", authority);
      Assert.DoesNotContain("identical", authority);
      Assert.DoesNotContain("verified", authority);
    }

    // ---- appended ports are NAMED, never counted ------------------------------------------------------

    /// <summary>A user who cannot see WHICH port is appearing has been given a number rather than a scope.
    /// And they are told nothing is wired to it, because they will have to do that themselves.</summary>
    [Fact]
    public void An_appended_port_is_named_and_its_unwired_state_stated()
    {
      var authority = ConsentCard.EditScriptAuthority(
        Change(addIn: new[] { "offset" }), ScriptEditOutcome.RanClean, Facts(appendedInputs: 1));

      Assert.Contains("an input called \"offset\"", authority);
      Assert.Contains("nothing is wired to it yet", authority);
      Assert.Contains("connect it yourself", authority);
    }

    [Fact]
    public void An_edit_that_adds_no_ports_says_nothing_about_ports()
    {
      var authority = ConsentCard.EditScriptAuthority(Change(), ScriptEditOutcome.RanClean, Facts());
      Assert.DoesNotContain("It also adds", authority);
    }

    // ---- THE TWO BOUNDS, WHICH ARE DIFFERENT ON PURPOSE ----------------------------------------------

    /// <summary>
    /// THE SOURCE IS NEVER TRUNCATED. This is the ruling to-script earned: the card is a consent surface
    /// for code that will run, so it shows all of it. Cutting it here would mean asking someone to approve
    /// code they were not shown - which is why the readability bound lives at GENERATION time instead,
    /// where an over-long source is refused before a card exists.
    /// </summary>
    [Fact]
    public void The_complete_source_is_on_the_card_however_long_it_is()
    {
      var sb = new StringBuilder();
      for (var i = 0; i < 400; i++) sb.Append("line_").Append(i).Append(" = ").Append(i).Append('\n');
      var source = sb.ToString();

      var html = ConsentCard.EditScriptHtml("why", "tok",
        Change(newSource: source), ScriptEditOutcome.RanClean, Facts());

      Assert.Contains("line_0 = 0", html);
      Assert.Contains("line_399 = 399", html);     // the LAST line is there, so nothing was cut
      Assert.DoesNotContain("not shown; ", html);  // and no truncation marker was rendered over it
    }

    /// <summary>The DIFF, by contrast, IS bounded - and says what it left out, with a pointer to the
    /// source below it that is not bounded (ADR-035: every omission stated).</summary>
    [Fact]
    public void The_diff_is_bounded_and_says_what_it_omitted()
    {
      var before = new StringBuilder();
      var after = new StringBuilder();
      for (var i = 0; i < 300; i++)
      {
        before.Append("v").Append(i).Append(" = ").Append(i).Append('\n');
        after.Append("v").Append(i).Append(" = ").Append(i + 1000).Append('\n');
      }

      var diff = SourceDiff.Between(before.ToString(), after.ToString());
      Assert.True(diff.Truncated);
      Assert.Contains("full source is below", diff.Lines[diff.Lines.Count - 1].Text);

      var change = new ScriptEditChange
      {
        Label = "\"Area\"", Claim = ScriptEditClaim.Changes,
        NewSource = after.ToString(), BaseSource = before.ToString(), Diff = diff,
      };
      var html = ConsentCard.EditScriptHtml("why", "tok", change, ScriptEditOutcome.RanClean, Facts());

      // the diff's own summary counts the WHOLE change, not the shown part
      Assert.Contains("300 lines added", html);
      // and the full source is still complete beneath it
      Assert.Contains("v299 = 1299", html);
    }

    /// <summary>An identical pair renders no diff block at all rather than an empty one - though the ask
    /// refuses a no-op long before a card is drawn, so this is the belt to that braces.</summary>
    [Fact]
    public void An_identical_pair_renders_no_diff_block()
    {
      var change = Change(newSource: Before);
      var html = ConsentCard.EditScriptHtml("why", "tok", change, ScriptEditOutcome.RanClean, Facts());
      Assert.DoesNotContain("What changes", html);
    }

    // ---- the card has ONE action -----------------------------------------------------------------------

    /// <summary>
    /// ONE PRIMARY ACTION, NO ROUTE BUTTON, NO TICK (§9.5's SMALL band, and the architect's ruling). An
    /// edit to one existing component is unambiguously small; the route alternative would be "plan it
    /// first", which is meaningless when the component already exists and the work is one edit to it. A
    /// card gains an alternative only where a real second path exists - manufacturing one to match another
    /// card's shape would be #774's mis-framing in reverse.
    /// </summary>
    [Fact]
    public void The_card_offers_exactly_one_action()
    {
      var html = ConsentCard.EditScriptHtml("why", "tok", Change(), ScriptEditOutcome.RanClean, Facts());

      var buttons = html.Split(new[] { "<button" }, StringSplitOptions.None).Length - 1;
      Assert.Equal(1, buttons);
      Assert.Contains("Yes, go ahead", html);
      Assert.DoesNotContain("type=\"checkbox\"", html);
    }

    /// <summary>
    /// A null change falls back to the kind's generic consequence rather than composing a sentence about a
    /// component it cannot name.
    ///
    /// #851: THIS TEST USED TO BE UNABLE TO FAIL ON ITS OWN DOC COMMENT. It asserted only that the phrase
    /// "replace the source of" was ABSENT, and the fallback it describes did not exist - Authority had no
    /// EditScript case - so the call returned "Nothing will happen." and the test passed on it. A doc
    /// comment on a test is a claim the test must be able to fail; this one could not, and the suite had
    /// certified a consequence that was never written.
    ///
    /// The old assertion was also the wrong proxy: the generic consequence legitimately says "replace the
    /// source of ONE EXISTING script component", because that is what the kind does. What the comment
    /// actually forbids is naming a SPECIFIC component, which a null change cannot supply. So it now asserts
    /// the fallback EXISTS, IS the kind's generic consequence, and keeps the family's danger-first order.
    /// </summary>
    [Fact]
    public void A_missing_change_does_not_compose_a_sentence_about_nothing()
    {
      var authority = ConsentCard.EditScriptAuthority(null, ScriptEditOutcome.RanClean, Facts());

      // The fallback it describes must EXIST - the failure this test could not see.
      Assert.NotEqual("Nothing will happen.", authority);
      Assert.Equal(ConsentCard.Authority("edit_script", null), authority);

      // And it is the generic consequence, not a sentence about a particular component: it says code will
      // run - the part a reader must meet first - without naming anything it was never told about.
      Assert.Contains("THE NEW CODE WILL RUN", authority);
      Assert.Contains("one existing script component", authority);
    }
  }
}
