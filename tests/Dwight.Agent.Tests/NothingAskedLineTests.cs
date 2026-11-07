using System;
using System.Collections.Generic;
using Dwight.Agent;
using Dwight.Llm;
using Xunit;
using static Dwight.Agent.Tests.AgentTurnDriverTests;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// U32 (#924, #932): A TURN THAT ANNOUNCED A CHANGE AND ASKED FOR NOTHING GETS DWIGHT'S LINE, ON BOTH CHANNELS.
  ///
  /// Thirteen turns in the U20 gate run and eight in the massing session ended on "submitting now" / "raising the card"
  /// with no request_mutation; once the model quoted a refusal that was never issued. The product cannot stop the prose; it
  /// can contradict it in the same thread. These tests witness the DECISION and both lines' words, and they drive the REAL
  /// loop (AgentTurnDriver, a scripted provider) for the field shapes, so the TurnResult the panel reads is the loop's own.
  ///
  /// WHAT THEY CANNOT WITNESS: that the panel renders the line and writes it through the report door. That wiring lives in
  /// DwightScaffold.cs, which this lane does not own (a named deferral in the PR), and only a sitting sees it.
  ///
  /// Serial with TurnShapeTests: every driven turn is observed by the static TurnShape record.
  /// </summary>
  [Collection(TurnShapeTests.StaticRecord)]
  public class NothingAskedLineTests
  {
    private const string ReadOk = "{\"ok\":true,\"result\":{}}";
    private const string BuildOk = "{\"ok\":true,\"result\":{\"verdict\":\"ok\"}}";

    private static string ReadsOnly(LlmBlock call) => ReadOk;

    // ---- the three shapes of #924, and the fabricated result of #932 - each through the real loop --------------------

    /// <summary>#924 shape 1, the bare read turn: the only call is get_scaffold_map, then a promise and the end.</summary>
    [Fact]
    public void A_bare_read_turn_that_promises_the_card_gets_the_line()
    {
      var provider = new ScriptedProvider(
        Calls("t1", AgentToolCatalog.GetScaffoldMap, "{}"),
        Says("B2 confirmed on canvas (23 components), plan intact, ready for a full rebuild. Calling it now."));

      var result = Drive(provider, ReadsOnly);

      Assert.Equal(0, result.RequestCalls);
      Assert.True(AgentLoopCore.AnnouncedNothingAsked(TurnCapability.ReadOnly, result));
      Assert.NotNull(AgentLoopCore.NothingAskedHtml(TurnCapability.ReadOnly, result));
      Assert.Equal(AgentLoopCore.NothingAskedFact, AgentLoopCore.NothingAskedReport(TurnCapability.ReadOnly, result));
    }

    /// <summary>#924 shape 2, the post-build close: an APPROVED build turn ran build_region for A3, then wrote "Now raising B2"
    /// and stopped. Building is not asking - this is the case PR #12's repair step could never see, because a build ran.</summary>
    [Fact]
    public void A_post_build_close_that_announces_the_next_card_gets_the_line()
    {
      var provider = new ScriptedProvider(
        Calls("t1", AgentToolCatalog.BuildRegion, "{\"region\":\"A3\"}"),
        Says("A3 is built. Now raising B2 as requested: this builds the facade grid on the loft."));

      var result = Drive(provider, _ => BuildOk, actingTool: AgentToolCatalog.BuildRegion, approvedRegions: new[] { "A3" });

      Assert.Equal(1, result.ActingCalls);
      Assert.Equal(0, result.RequestCalls);
      Assert.True(AgentLoopCore.AnnouncedNothingAsked(TurnCapability.Build, result));
      Assert.Null(AgentLoopCore.NoBuildBanner(TurnCapability.Build, result, new[] { "A3" }));   // the build ran: no banner
    }

    /// <summary>#924/#932 shape 3, the honest `changes` edit: search and get_component, then "Submitting the requested edit"
    /// with no call. Not the honesty guard - it recurred on an edit the model believed honest.</summary>
    [Theory]
    [InlineData("Submitting the requested edit: the script now writes a comment line above the area.")]
    [InlineData("Submitting now, as instructed.")]
    [InlineData("That cannot be claimed as preserves, so I'll raise it instead as changes.")]
    public void An_honest_edit_announced_and_never_submitted_gets_the_line(string text)
    {
      var provider = new ScriptedProvider(
        Calls("t1", AgentToolCatalog.Search, "{\"query\":\"F3\"}"),
        Calls("t2", AgentToolCatalog.GetComponent, "{\"id\":13}"),
        Says(text));

      var result = Drive(provider, ReadsOnly);

      Assert.Equal(2, result.ToolCalls);
      Assert.True(AgentLoopCore.AnnouncedNothingAsked(TurnCapability.ReadOnly, result));
    }

    /// <summary>#932: THE FABRICATED RESULT. get_component, then completion - and prose quoting a refusal in Dwight's voice
    /// that was never issued. The line contradicts it in the same thread.</summary>
    [Fact]
    public void A_turn_that_quotes_a_result_it_never_requested_gets_the_line()
    {
      var provider = new ScriptedProvider(
        Calls("t1", AgentToolCatalog.GetComponent, "{\"id\":13}"),
        Says("Submitted as instructed, and refused again - correctly this time too. Result: precondition_refused - " +
             "'Dwight could not check it: the component is currently errored, with no working behaviour to preserve. " +
             "The canvas is unchanged.'"));

      var result = Drive(provider, ReadsOnly);

      Assert.Equal(0, result.RequestCalls);
      Assert.True(AgentLoopCore.AnnouncedNothingAsked(TurnCapability.ReadOnly, result));
    }

    // ---- the two controls the brief names --------------------------------------------------------------------------

    /// <summary>A turn that ASKED gets no line - a shown card and a REFUSED request alike, because both asked: the refusal
    /// went to the model and the close says why. Driven, so RequestCalls is the loop's own count.</summary>
    [Fact]
    public void A_turn_that_asked_gets_no_line_whether_the_card_was_shown_or_refused()
    {
      var shown = Drive(new ScriptedProvider(
          Calls("t1", AgentHostTools.RequestMutation, "{\"kind\":\"build\",\"regions\":[\"B2\"]}", prose: "Raising the build card for B2 now.")),
        _ => CardShown);
      Assert.True(shown.AskedForConsent);
      Assert.False(AgentLoopCore.AnnouncedNothingAsked(TurnCapability.ReadOnly, shown));

      var refused = Drive(new ScriptedProvider(
          Calls("t1", AgentHostTools.RequestMutation, "{\"kind\":\"edit_script\"}"),
          Says("Submitted - it was refused, and I have told you why.")),
        call => call.ToolName == AgentHostTools.RequestMutation ? Refused : ReadOk);
      Assert.Equal(1, refused.RequestCalls);
      Assert.Null(AgentLoopCore.NothingAskedHtml(TurnCapability.ReadOnly, refused));
      Assert.Null(AgentLoopCore.NothingAskedReport(TurnCapability.ReadOnly, refused));
    }

    /// <summary>A turn that announced nothing gets no line, whatever it called.</summary>
    [Theory]
    [InlineData("B2 holds 23 components and its loft is 61 m long.")]
    [InlineData("Nothing was submitted - the edit would change the area, so I need you to decide first.")]
    [InlineData("I won't submit a false claim, even as a test.")]
    [InlineData("If you approve the card, I'll raise the next one for B2.")]
    [InlineData("I'd suggest raising the beam height to 4 m.")]
    public void A_turn_that_announced_nothing_gets_no_line(string text)
    {
      var result = Drive(new ScriptedProvider(Calls("t1", AgentToolCatalog.GetOverview, "{}"), Says(text)), ReadsOnly);
      Assert.False(AgentLoopCore.AnnouncedNothingAsked(TurnCapability.ReadOnly, result));
    }

    // ---- the boundaries of the rule --------------------------------------------------------------------------------

    /// <summary>On an approved BUILD turn a build announced and not run is the no-build banner's sentence; a second line
    /// would say the same thing in other words. Off a build turn the same text gets this line (#659's build family).</summary>
    [Fact]
    public void A_build_announced_on_a_build_turn_is_the_banners_not_this_lines()
    {
      var result = new TurnResult { Outcome = TurnOutcome.Complete, Text = "Rebuilding B1 now." };
      Assert.False(AgentLoopCore.AnnouncedNothingAsked(TurnCapability.Build, result));
      Assert.NotNull(AgentLoopCore.NoBuildBanner(TurnCapability.Build, result, new[] { "B1" }));
      Assert.True(AgentLoopCore.AnnouncedNothingAsked(TurnCapability.ReadOnly, result));
    }

    /// <summary>A Stop already says what happened, and the user did it.</summary>
    [Fact]
    public void A_stopped_turn_gets_no_line()
    {
      var result = new TurnResult { Outcome = TurnOutcome.Stopped, Text = "Raising the card now." };
      Assert.False(AgentLoopCore.AnnouncedNothingAsked(TurnCapability.ReadOnly, result));
    }

    /// <summary>A caveat after the announcement, in the same sentence, does not hide it.</summary>
    [Fact]
    public void A_caveat_after_the_announcement_does_not_hide_it()
    {
      Assert.True(TurnShape.AnnouncedRequestWithoutAsking("Raising the build card for A2 now: it will not touch A1.", 0));
      Assert.True(TurnShape.AnnouncedRequestWithoutAsking("I can't build it from here, so I'm raising a card instead.", 0));
    }

    /// <summary>U32 precision (#975). A NEGATED announcement - a decision NOT to ask - is not an announcement. The
    /// first row is the field instance (the refusal code sits in a parenthesis, so only a sentence-level halt reads it);
    /// the rest are the phrasings the ruling listed. Each row's positive control is the same sentence with the negation
    /// removed, which must still fire.</summary>
    [Theory]
    [InlineData("I stop here rather than submit an edit (precondition_refused).", "I submit an edit (precondition_refused).")]
    [InlineData("I stop here rather than submit an edit.", "I'll submit an edit.")]
    [InlineData("I'm stopping here, not submitting it (precondition_refused).", "Submitting it (precondition_refused).")]
    [InlineData("Instead of raising the card for B2 now, I'll explain why.", "Raising the card for B2 now.")]
    [InlineData("I will not raise the card again.", "I will raise the card again.")]
    [InlineData("I won't be raising the card for A1 now.", "I'm raising the card for A1 now.")]
    [InlineData("I decline to submit it, calling it now would repeat the refusal.", "Calling it now, submitting it.")]
    public void A_negated_announcement_is_not_an_announcement(string negated, string control)
    {
      Assert.False(TurnShape.AnnouncedRequestWithoutAsking(negated, 0), negated);
      Assert.True(TurnShape.AnnouncedRequestWithoutAsking(control, 0), control);
    }

    /// <summary>The words that negate must not hide an announcement that merely sits beside them.</summary>
    [Fact]
    public void A_real_announcement_beside_a_caveat_still_fires()
    {
      Assert.True(TurnShape.AnnouncedRequestWithoutAsking("Raising the build card for A2 now, it will not touch A1.", 0));
    }

    /// <summary>The counter reads the SAME rule as the line, so the number and the line cannot disagree - and it is a
    /// superset of #659's rule, so no turn it counted before stops counting.</summary>
    [Fact]
    public void The_counter_reads_the_lines_rule()
    {
      TurnShape.Clear();
      TurnShape.Observe("A3 is built. Now raising B2 as requested.", 1, 1, 0);   // acted, announced a card, asked nothing
      TurnShape.Observe("I'll rebuild A1 now.", 1, 0, 0);                         // #659's original shape
      TurnShape.Observe("Raising the card now.", 1, 0, 1);                        // asked: not counted
      Assert.Equal(2, TurnShape.AnnouncedChangeCount);
      TurnShape.Clear();
    }

    // ---- the words: a claim about our own behaviour, held on the behaviour's side (#315) -----------------------------

    /// <summary>The user's sentence says only what the condition guarantees: no request_mutation call means nothing was
    /// submitted for approval and no card was raised. It claims nothing about the canvas - a post-build close changed it.</summary>
    [Fact]
    public void The_users_line_claims_only_what_the_condition_guarantees()
    {
      Assert.Equal("Dwight did not receive a request this turn - nothing was submitted for your approval and no card was raised.",
                   AgentLoopCore.NothingAskedSentence);
      Assert.DoesNotContain("canvas", AgentLoopCore.NothingAskedSentence);
      var html = AgentLoopCore.NothingAskedHtml(TurnCapability.ReadOnly,
        new TurnResult { Outcome = TurnOutcome.Complete, Text = "Calling it now." });
      Assert.Contains(AgentLoopCore.NothingAskedSentence, html);
    }

    /// <summary>The model's line is the same fact, attributed to the plugin, naming the tool it did not call.</summary>
    [Fact]
    public void The_models_line_is_the_same_fact_attributed_to_the_plugin()
    {
      Assert.StartsWith("PLUGIN FACT (from Dwight, not the user):", AgentLoopCore.NothingAskedFact);
      Assert.Contains("no request_mutation call", AgentLoopCore.NothingAskedFact);
      Assert.Contains("no card was raised", AgentLoopCore.NothingAskedFact);
    }
  }
}
