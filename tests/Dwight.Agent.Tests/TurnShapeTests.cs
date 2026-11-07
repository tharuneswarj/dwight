using Dwight.Agent;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// F5, from the WP-4 gate: the model proposed a pipeline in prose, asked "want me to go ahead and
  /// scaffold it?", waited for "cool, go ahead", and only then raised the consent card - a billed
  /// round-trip and a second user interaction to reach a card whose entire job is to BE the ask.
  ///
  /// IT WAS NOT A REGRESSION. The instruction existed the whole time, in <c>request_mutation</c>'s TOOL
  /// DESCRIPTOR - which governs how you use a tool once you have decided to call it, while this is about
  /// HOW A TURN ENDS, decided before any tool is chosen. The line was never in a position to work.
  ///
  /// AND THIS IS A DETECTOR, NOT A GUARD. A prompt line's effect on a model cannot be unit-tested, and
  /// claiming a test proves compliance would be its own false claim. So what is tested here is the
  /// DETECTOR's own accuracy - whether it counts the right things - and the model's behaviour is
  /// measured by recurrence in the field. Measurement before mechanism.
  /// </summary>
  /// <summary>The collection had a name and no definition, so it ran beside every other collection; any class that drives a real turn observes into TurnShape's statics
  /// whether or not it names them, and main went red on the #222 merge with a count of 3 for 1. Defined with parallelization off, it runs alone, after the parallel ones (as PlanNoteTests does).</summary>
  [CollectionDefinition(TurnShapeTests.StaticRecord, DisableParallelization = true)]
  public class TurnShapeStaticRecordCollection { }

  [Collection(StaticRecord)]
  public class TurnShapeTests
  {
    /// <summary>The collection every test that feeds the static TurnShape record belongs to, so none of them
    /// runs while another is counting. The agent loop observes every turn it ends (#612).</summary>
    public const string StaticRecord = "TurnShape static record";

    // ---- what it must catch --------------------------------------------------------------------------

    [Theory]
    [InlineData("I'd lay this out in four regions. Want me to go ahead and scaffold it?")]
    [InlineData("That would need a rebuild of A1. Shall I request that?")]
    [InlineData("This is straightforward to fix. Should I put that to you as a change?")]
    [InlineData("I can reorganise this. Let me know if you want me to?")]
    public void A_turn_that_proposes_and_then_asks_whether_to_propose_is_caught(string text)
    {
      Assert.True(TurnShape.AskedPermissionToAsk(text, toolCallsThisTurn: 0));
    }

    // ---- and what it must NOT, which is the half that decides whether the number is worth reading ----

    /// <summary>
    /// A turn that CALLED something did something. The shape being counted is specifically "stopped and
    /// asked instead of acting", so any tool call disqualifies it - including the request_mutation call
    /// that raises the card, which is the correct behaviour and would otherwise be counted as the defect.
    /// </summary>
    [Fact]
    public void A_turn_that_actually_called_a_tool_is_never_counted()
    {
      Assert.False(TurnShape.AskedPermissionToAsk(
        "I'd lay this out in four regions. Shall I?", toolCallsThisTurn: 1));
    }

    /// <summary>A genuine question is not this. Asking which of two things the user meant is exactly what
    /// a good turn does when it cannot tell, and counting it would make the number meaningless.</summary>
    [Theory]
    [InlineData("There are two Loft components here - did you mean the one feeding the Brep Join?")]
    [InlineData("Which surface should the panels follow?")]
    public void A_genuine_clarifying_question_is_not_counted(string text)
    {
      Assert.False(TurnShape.AskedPermissionToAsk(text, 0));
    }

    /// <summary>An explanation that merely mentions what it could do, without asking, is not the shape -
    /// the defect is the WAIT, and without a question there is nothing to wait for.</summary>
    [Fact]
    public void A_statement_with_no_question_is_not_counted()
    {
      Assert.False(TurnShape.AskedPermissionToAsk(
        "I could reorganise this, and I will put that to you as a card.", 0));
    }

    [Fact]
    public void Empty_or_missing_text_is_not_counted()
    {
      Assert.False(TurnShape.AskedPermissionToAsk(null, 0));
      Assert.False(TurnShape.AskedPermissionToAsk("   ", 0));
    }

    // ---- the record reports its own denominator ------------------------------------------------------

    /// <summary>
    /// #212 at a counter: a zero that cannot be told from an absence is not a measurement. "0 of 0
    /// examined" and "0 of 40 examined" are completely different readings and would print identically
    /// without the denominator.
    /// </summary>
    [Fact]
    public void The_record_counts_what_it_EXAMINED_not_only_what_it_found()
    {
      TurnShape.Clear();

      TurnShape.Observe("Here is what I found.", 2, 0, 0);
      TurnShape.Observe("I could scaffold this. Shall I?", 0, 0, 0);
      TurnShape.Observe("Done - three regions built.", 4, 3, 0);

      Assert.Equal(3, TurnShape.Examined);
      Assert.Equal(1, TurnShape.Count);

      TurnShape.Clear();
    }

    [Fact]
    public void An_untouched_session_reports_nothing_examined_rather_than_a_clean_zero()
    {
      TurnShape.Clear();

      Assert.Equal(0, TurnShape.Examined);
      Assert.Equal(0, TurnShape.Count);
      Assert.Empty(TurnShape.Examples);
    }

    /// <summary>#624: the approved-build count and its denominator are cleared with the rest, so a drill that
    /// asks for a clean denominator gets one for this number too.</summary>
    [Fact]
    public void Clearing_resets_the_approved_build_count_and_its_denominator()
    {
      TurnShape.Clear();
      TurnShape.ObserveApprovedBuild(0);
      TurnShape.ObserveApprovedBuild(2);
      Assert.Equal(2, TurnShape.ApprovedBuildTurns);
      Assert.Equal(1, TurnShape.ApprovedBuildTurnsWithoutBuild);

      TurnShape.Clear();

      Assert.Equal(0, TurnShape.ApprovedBuildTurns);
      Assert.Equal(0, TurnShape.ApprovedBuildTurnsWithoutBuild);
    }

    /// <summary>The example keeps the END of the text, because that is where the ask lives - a head-first
    /// truncation would show the explanation and cut off the thing being counted.</summary>
    [Fact]
    public void An_example_keeps_the_tail_where_the_ask_is()
    {
      TurnShape.Clear();
      TurnShape.Observe(new string('x', 400) + " Shall I go ahead?", 0, 0, 0);

      Assert.Single(TurnShape.Examples);
      Assert.EndsWith("Shall I go ahead?", TurnShape.Examples[0]);

      TurnShape.Clear();
    }

    // ---- #582: CLAIMED TO HAVE TRIED IT, while calling nothing that acts ----------------------------

    /// <summary>
    /// THE FIELD CASE, from Dwight's own self-audit: asked to try things on the canvas, the agent read once
    /// and reported "tested/confirmed by trying just now" having called no mutation.
    /// </summary>
    [Theory]
    [InlineData("The slider range is fine - tested and confirmed by trying just now.")]
    [InlineData("I tried it on the canvas and the loft closes cleanly.")]
    [InlineData("I've tested this and the grid is 40 cells.")]
    [InlineData("I ran it and the output looks right.")]
    public void A_turn_that_claims_to_have_tried_something_while_acting_on_nothing_is_caught(string text)
    {
      Assert.True(TurnShape.ClaimedToHaveTried(text, actingCallsThisTurn: 0));
    }

    /// <summary>A turn that DID mutate or solve tried something, whatever it then says about it.</summary>
    [Fact]
    public void A_turn_that_actually_mutated_or_solved_is_never_counted()
    {
      Assert.False(TurnShape.ClaimedToHaveTried("I tried it and it works.", actingCallsThisTurn: 1));
    }

    /// <summary>
    /// THE BOUNDARY THAT KEEPS THE NUMBER WORTH READING. Checking something by READING it is legitimate
    /// verification, and a detector that counted it would punish the model for doing its job.
    /// </summary>
    [Theory]
    [InlineData("I verified the wiring with get_connections - the loft is fed by both curves.")]
    [InlineData("I checked the component and it has two inputs.")]
    [InlineData("Confirmed by reading the map: A1 to A6 are on the canvas.")]
    public void Verifying_by_reading_is_not_a_claim_to_have_tried(string text)
    {
      Assert.False(TurnShape.ClaimedToHaveTried(text, 0));
    }

    /// <summary>"I haven't tested it" is an honest statement, not a claim. Counting it would punish the
    /// model for telling the truth - the exact opposite of what this detector is for.</summary>
    [Theory]
    [InlineData("I haven't tested it, so I can't promise the output.")]
    [InlineData("I have not tried this on the canvas.")]
    [InlineData("I cannot run it from here.")]
    public void An_honest_statement_that_it_was_NOT_tried_is_not_counted(string text)
    {
      Assert.False(TurnShape.ClaimedToHaveTried(text, 0));
    }

    /// <summary>
    /// Negation is judged per SENTENCE, so an honest caveat in one sentence does not hide a claim in
    /// another. (The known cost runs the other way and is accepted: "I tried it and it didn't break" holds
    /// a negation and is missed. For a recurrence counter a miss costs a data point, while a false positive
    /// costs trust in the number.)
    /// </summary>
    [Fact]
    public void A_caveat_in_one_sentence_does_not_hide_a_claim_in_another()
    {
      Assert.True(TurnShape.ClaimedToHaveTried(
        "I tested it on the canvas. I can't say how it behaves on larger surfaces.", 0));
    }

    /// <summary>Both detectors share one denominator, because they examine the same turns.</summary>
    [Fact]
    public void Both_detectors_count_against_the_same_turns()
    {
      TurnShape.Clear();

      TurnShape.Observe("Here is the map.", 1, 0, 0);
      TurnShape.Observe("I tried it just now and it works.", 1, 0, 0);   // read once, claimed to try
      TurnShape.Observe("Built A1.", 2, 1, 0);

      Assert.Equal(3, TurnShape.Examined);
      Assert.Equal(1, TurnShape.ClaimedTriedCount);
      Assert.Single(TurnShape.ClaimedTriedExamples);

      TurnShape.Clear();
      Assert.Equal(0, TurnShape.ClaimedTriedCount);
    }

    // ---- #659/#669: ANNOUNCED A CHANGE while requesting and acting on nothing -------------------------------

    /// <summary>
    /// The third number (#659), MEASURE ONLY (#669). A turn whose prose announces a change - "I'll rebuild A1", "raising
    /// the build card" - while it made no request_mutation call and no call that mutates or solves. The announcement
    /// promised a card or a build the turn never produced.
    /// </summary>
    [Theory]
    [InlineData("I'll rebuild A1 with the radius at 8.")]
    [InlineData("Rebuilding B1 now.")]
    [InlineData("Let me build the grid region.")]
    [InlineData("I will build A2 next.")]
    [InlineData("Raising the build card for A2 now.")]
    [InlineData("I'll reorganise the loft region so it reads left to right.")]
    [InlineData("I'll lay down A1 and A2.")]
    public void A_turn_that_announces_a_change_while_requesting_and_acting_on_nothing_is_caught(string text)
    {
      Assert.True(TurnShape.AnnouncedChangeWithoutRequest(text, requestCallsThisTurn: 0, actingCallsThisTurn: 0));
    }

    /// <summary>A turn that ASKED (request_mutation) did what it announced: the card is the ask.</summary>
    [Fact]
    public void A_turn_that_made_a_request_is_never_counted()
    {
      Assert.False(TurnShape.AnnouncedChangeWithoutRequest("Raising a card to rebuild A1.", requestCallsThisTurn: 1, actingCallsThisTurn: 0));
    }

    /// <summary>A turn that built or solved did something, whatever it then said it would do.</summary>
    [Fact]
    public void A_turn_that_acted_is_never_counted()
    {
      Assert.False(TurnShape.AnnouncedChangeWithoutRequest("Rebuilding B1 now.", requestCallsThisTurn: 0, actingCallsThisTurn: 1));
    }

    /// <summary>A negated announcement is the opposite of one - the same per-sentence negation filter as #582. Every row
    /// holds a lexicon phrase AND a negation, so each is a witness of the filter, not of the lexicon.</summary>
    [Theory]
    [InlineData("Rebuilding A1 is not possible from this turn.")]
    [InlineData("I'll build nothing without an approved card.")]
    [InlineData("I cannot promise I'll reorganise it today.")]
    public void A_negated_announcement_is_not_counted(string text)
    {
      Assert.False(TurnShape.AnnouncedChangeWithoutRequest(text, 0, 0));
    }

    /// <summary>#669: a CONDITIONAL OFFER is not an announcement. "If you approve, I'll rebuild A1" describes what happens
    /// after the user's move - it promised nothing for this turn.</summary>
    [Theory]
    [InlineData("If you approve the card, I'll rebuild A1.")]
    [InlineData("Once you approve it, I will build B1.")]
    [InlineData("When you're ready, let me build the grid.")]
    public void A_conditional_offer_is_not_counted(string text)
    {
      Assert.False(TurnShape.AnnouncedChangeWithoutRequest(text, 0, 0));
    }

    [Theory]
    [InlineData("A1 was built earlier and holds 8 components.")]
    [InlineData("build_region places components inside one region.")]
    public void A_description_that_announces_nothing_is_not_counted(string text)
    {
      Assert.False(TurnShape.AnnouncedChangeWithoutRequest(text, 0, 0));
    }

    /// <summary>Judged per sentence: a caveat in one sentence does not hide an announcement in another.</summary>
    [Fact]
    public void A_caveat_in_one_sentence_does_not_hide_an_announcement_in_another()
    {
      Assert.True(TurnShape.AnnouncedChangeWithoutRequest("I'll rebuild A1. I can't promise the radius holds.", 0, 0));
    }

    /// <summary>Model prose often carries a typographic apostrophe (U+2019); "I'll rebuild" written with it is the same
    /// announcement. Built from the code point so the test says exactly what it feeds in.</summary>
    [Fact]
    public void A_typographic_apostrophe_is_the_same_announcement()
    {
      Assert.True(TurnShape.AnnouncedChangeWithoutRequest("I" + (char)0x2019 + "ll rebuild A1 now.", 0, 0));
    }

    /// <summary>U54, the field instance: GLM's first reply to "Hi" was a bulleted OFFER. "When you're ready, I can help you:" sat on
    /// one line and "- I'll lay down a scaffold" on the next, and the newline split made the bullet a sentence with no conditional
    /// in it. A list item is an offer, not an announcement.</summary>
    [Fact]
    public void A_bulleted_offer_in_a_greeting_is_not_an_announcement()
    {
      var greeting =
        "Hi! When you're ready, I can help you:\n" +
        "\n" +
        "- Start a new definition - I'll lay down a scaffold (a map of labelled regions), then place and wire real components into it with your approval\n" +
        "- Place a few components directly on the canvas\n" +
        "- Read what is already there and explain it\n" +
        "\n" +
        "What are you working on?";

      Assert.False(TurnShape.AnnouncedChangeWithoutRequest(greeting, 0, 0));
      Assert.False(TurnShape.AnnouncedChangeWithoutRequest("1. I'll lay down a scaffold first\n2) I'll build it after", 0, 0));
      Assert.False(TurnShape.AnnouncedChangeWithoutRequest("* I'll rebuild A1", 0, 0));
      Assert.False(TurnShape.AnnouncedChangeWithoutRequest("• I'll rebuild A1", 0, 0));
    }

    /// <summary>The same words as a plain sentence, with no request call, still count; so does a non-list line after a list; so
    /// does the request family with a bulleted "raising the card" (U54 touches the build family only).</summary>
    [Fact]
    public void A_plain_announcement_still_counts_and_the_request_family_still_reads_bullets()
    {
      Assert.True(TurnShape.AnnouncedChangeWithoutRequest("I'll lay down the scaffold now.", 0, 0));
      Assert.True(TurnShape.AnnouncedChangeWithoutRequest("- one thing\nI'll rebuild A1 now.", 0, 0));
      Assert.True(TurnShape.AnnouncedRequestWithoutAsking("Plan:\n- Raising the card for B1 now", 0));
      Assert.True(TurnShape.AnnouncedWithoutAsking("Plan:\n- Raising the card for B1 now", 0, 0));
    }

    [Fact]
    public void I_can_help_you_and_I_can_are_offers()
    {
      Assert.False(TurnShape.AnnouncedChangeWithoutRequest("I can help you: I'll rebuild A1 when asked", 0, 0));
      Assert.False(TurnShape.AnnouncedChangeWithoutRequest("I can: build it, I'll build it with your approval", 0, 0));
    }

    /// <summary>U60: the offer phrases AsksToAsk already names are conditional offers too. The verbatim sentence is the field
    /// instance (the tower session's lookup turn): it offered a card, announced none, and was counted as an announcement.</summary>
    [Fact]
    public void An_offer_that_waits_on_the_user_is_not_an_announcement()
    {
      const string Lookup = "Nothing placed - this was a lookup only. Say the word and I'll raise a build card for any of them.";
      Assert.False(TurnShape.AnnouncedRequestWithoutAsking(Lookup, 0));
      Assert.False(TurnShape.AnnouncedWithoutAsking(Lookup, 0, 0));
      Assert.False(TurnShape.AnnouncedChangeWithoutRequest("Just say so and I'll rebuild A1.", 0, 0));
      Assert.False(TurnShape.AnnouncedChangeWithoutRequest("Let me know if you like it and I'll build it.", 0, 0));
      Assert.False(TurnShape.AnnouncedChangeWithoutRequest("I'll rebuild A1, want me to?", 0, 0));
      Assert.False(TurnShape.AnnouncedChangeWithoutRequest("Tell me and I'll build it.", 0, 0));
    }

    [Fact]
    public void The_offer_phrases_do_not_hide_a_real_announcement()
    {
      Assert.True(TurnShape.AnnouncedRequestWithoutAsking("Nothing placed. Raising the build card for A1 now.", 0));
      Assert.True(TurnShape.AnnouncedChangeWithoutRequest("I'll rebuild A1 now.", 0, 0));
    }

    [Fact]
    public void Empty_or_missing_text_announces_nothing()
    {
      Assert.False(TurnShape.AnnouncedChangeWithoutRequest(null, 0, 0));
      Assert.False(TurnShape.AnnouncedChangeWithoutRequest("  ", 0, 0));
    }

    /// <summary>The third number shares the one denominator, keeps its examples, and is cleared with the rest.</summary>
    [Fact]
    public void The_announced_change_count_shares_the_denominator_and_is_cleared()
    {
      TurnShape.Clear();

      TurnShape.Observe("Here is the map.", 1, 0, 0);
      TurnShape.Observe("I'll rebuild A1 now.", 1, 0, 0);          // read once, announced, requested nothing
      TurnShape.Observe("Raising a card to rebuild A1.", 1, 0, 1);  // requested: not counted

      Assert.Equal(3, TurnShape.Examined);
      Assert.Equal(1, TurnShape.AnnouncedChangeCount);
      Assert.Single(TurnShape.AnnouncedChangeExamples);
      Assert.EndsWith("I'll rebuild A1 now.", TurnShape.AnnouncedChangeExamples[0]);

      TurnShape.Clear();
      Assert.Equal(0, TurnShape.AnnouncedChangeCount);
      Assert.Empty(TurnShape.AnnouncedChangeExamples);
    }
  }
}
