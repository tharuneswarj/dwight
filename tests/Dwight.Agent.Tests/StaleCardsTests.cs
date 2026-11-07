using System;
using System.IO;
using System.Linq;
using Dwight.Agent;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// Brief AV. U96: minting a card retires the earlier pending ones in that chat. U97: an announcement with no card is caught from
  /// the turn's structure, and followed up once under an automatic mode.
  /// </summary>
  [Collection(TurnShapeTests.StaticRecord)]   // drives turns, which Observe into TurnShape's static counters: never in parallel with the classes that read them
  public class StaleCardsTests
  {
    private const string Thread1 = "thread-one-aaaaaaaa";
    private const string Thread2 = "thread-two-bbbbbbbb";

    // ---- U96 -------------------------------------------------------------------------------------------

    [Fact]
    public void A_later_card_in_the_same_chat_retires_the_earlier_one()
    {
      var t = new ConsentTokens();
      var a = t.Mint(Thread1, "build", null);
      var b = t.Mint(Thread1, "edit", null);

      Assert.False(t.Take(a, Thread1, out _, out _, out var reason));
      Assert.Equal(ConsentTokens.Superseded, reason);
      Assert.Equal(b, t.SupersededBy(a));

      Assert.True(t.Take(b, Thread1, out var kind, out _, out _));   // positive control: the newer card is accepted
      Assert.Equal("edit", kind);
    }

    [Fact]
    public void Every_earlier_card_is_retired_not_only_the_last()
    {
      var t = new ConsentTokens();
      var a = t.Mint(Thread1, "build", null);
      var b = t.Mint(Thread1, "edit", null);
      var c = t.Mint(Thread1, "set_value", null);

      Assert.False(t.Take(a, Thread1, out _, out _, out var ra));
      Assert.False(t.Take(b, Thread1, out _, out _, out var rb));
      Assert.Equal(ConsentTokens.Superseded, ra);
      Assert.Equal(ConsentTokens.Superseded, rb);
      Assert.Equal(c, t.SupersededBy(b));
      Assert.Equal(2, t.RetiredCount);
      Assert.True(t.Take(c, Thread1, out _, out _, out _));
    }

    [Fact]
    public void A_spot_cards_pair_retires_together()
    {
      var t = new ConsentTokens();
      t.MintSiblings(Thread1, "build", null, null, out var grouped, out var loose);
      var next = t.Mint(Thread1, "edit", null);

      Assert.False(t.Take(grouped, Thread1, out _, out _, out var r1));
      Assert.False(t.Take(loose, Thread1, out _, out _, out var r2));
      Assert.Equal(ConsentTokens.Superseded, r1);
      Assert.Equal(ConsentTokens.Superseded, r2);
      Assert.Equal(next, t.SupersededBy(grouped));
      Assert.Equal(next, t.SupersededBy(loose));
    }

    [Fact]
    public void A_new_spot_pair_retires_an_earlier_card_and_still_voids_its_own_sibling()
    {
      var t = new ConsentTokens();
      var earlier = t.Mint(Thread1, "build", null);
      t.MintSiblings(Thread1, "build", null, null, out var grouped, out var loose);

      Assert.False(t.Take(earlier, Thread1, out _, out _, out var reason));
      Assert.Equal(ConsentTokens.Superseded, reason);

      Assert.True(t.Take(grouped, Thread1, out _, out _, out _));
      Assert.False(t.Take(loose, Thread1, out _, out _, out var other));
      Assert.Equal(ConsentTokens.OtherOptionUsed, other);   // Brief U18 is unchanged
    }

    [Fact]
    public void A_card_in_another_chat_is_untouched()
    {
      var t = new ConsentTokens();
      var other = t.Mint(Thread2, "build", null);
      t.Mint(Thread1, "edit", null);

      Assert.True(t.Take(other, Thread2, out _, out _, out _));
      Assert.Equal(0, t.RetiredCount);
    }

    [Fact]
    public void A_card_that_waits_across_turns_with_no_newer_card_still_works()
    {
      var t = new ConsentTokens();
      var a = t.Mint(Thread1, "build", null);
      // Turns happen; nothing is minted. Retirement is at MINT, never at turn end.
      Assert.True(t.Take(a, Thread1, out var kind, out _, out var reason), reason);
      Assert.Equal("build", kind);
    }

    [Fact]
    public void A_used_card_stays_used_and_is_not_relabelled_superseded()
    {
      var t = new ConsentTokens();
      var a = t.Mint(Thread1, "build", null);
      Assert.True(t.Take(a, Thread1, out _, out _, out _));
      t.Mint(Thread1, "edit", null);

      Assert.False(t.Take(a, Thread1, out _, out _, out var reason));
      Assert.Equal(ConsentTokens.AlreadyUsed, reason);
    }

    [Fact]
    public void The_superseded_sentence_is_pinned_and_the_log_reads_the_retired_count()
    {
      Assert.Equal("That card was superseded by a later one in this chat; the later card is the one to answer.",
                   ConsentTokens.Say(ConsentTokens.Superseded));

      var t = new ConsentTokens();
      t.Mint(Thread1, "build", null);
      t.Mint(Thread1, "edit", null);
      Assert.EndsWith("(retired: 1)", t.Summary());
      Assert.Contains("edit@thread-o", t.Summary());
    }

    [Fact]
    public void The_click_path_reports_the_reason_through_Say_and_every_mint_goes_through_the_one_store()
    {
      var source = File.ReadAllText(Path.Combine(RepoRoot(), "Dwight", "UI", "DwightScaffold.cs"));
      Assert.Contains("ConsentTokens.Say(reason)", source);
      Assert.Contains("_consents.Mint(", source);
      Assert.Contains("_consents.MintSiblings(", source);
      // Only these two mint calls exist, so no card can be minted without retiring the earlier ones.
      Assert.Equal(1, Count(source, "_consents.Mint("));
      Assert.Equal(1, Count(source, "_consents.MintSiblings("));
    }

    // ---- U97 -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("I'll lay out and group the whole canvas now.")]
    [InlineData("This is a component delete, which falls under the edit capability.")]
    [InlineData("Let me rename those sliders now.")]
    [InlineData("I'm going to delete the unused panels.")]
    [InlineData("Next I will rewire the Series into the Panel.")]
    [InlineData("Now I'll tidy the regions.")]
    [InlineData("I can now apply the change to the slider.")]
    [InlineData("I'll go ahead and move the group to the left.")]
    [InlineData("I’ll rebuild the roof region.")]
    public void An_announced_change_with_no_card_is_caught_structurally(string message)
    {
      Assert.True(TurnShape.AnnouncedIntentWithoutCard(message, mutationAvailable: true, lastStepToolCalls: 0, requestCalls: 0));
    }

    [Theory]
    [InlineData("I'll lay out the canvas. Shall I go ahead?")]                 // a question
    [InlineData("Which slider did you mean? I'll rename it then.")]            // a question
    [InlineData("If you approve, I'll rebuild A1.")]                           // a conditional offer
    [InlineData("I won't delete anything without a card.")]                    // a negation
    [InlineData("Let me explain how the series feeds the panel.")]             // not a changing verb
    [InlineData("I'll check the wiring and read the regions first.")]          // reads, not changes
    [InlineData("The canvas has 12 components and 14 wires.")]                 // no intent at all
    [InlineData("")]
    public void A_message_that_asks_offers_conditionally_negates_or_only_reads_is_not_caught(string message)
    {
      Assert.False(TurnShape.AnnouncedIntentWithoutCard(message, mutationAvailable: true, lastStepToolCalls: 0, requestCalls: 0));
    }

    [Fact]
    public void A_turn_whose_last_step_called_a_tool_a_turn_that_asked_or_one_with_no_mutation_route_is_not_caught()
    {
      const string announced = "I'll rename those sliders now.";
      Assert.True(TurnShape.AnnouncedIntentWithoutCard(announced, true, 0, 0));    // control: the same text, caught
      Assert.False(TurnShape.AnnouncedIntentWithoutCard(announced, true, 1, 0));   // a tool call in the last step
      Assert.False(TurnShape.AnnouncedIntentWithoutCard(announced, true, 0, 1));   // it made a request
      Assert.False(TurnShape.AnnouncedIntentWithoutCard(announced, false, 0, 0));  // no mutation capability or route
    }

    [Fact]
    public void The_existing_turn_end_line_is_shown_for_the_new_catch_in_its_existing_words()
    {
      var caught = new TurnResult { Outcome = TurnOutcome.Complete, Text = "I'll lay out and group the whole canvas now.", LastStepToolCalls = 0 };
      Assert.True(AgentLoopCore.AnnouncedNothingAsked(TurnCapability.ReadOnly, caught));
      Assert.Contains(AgentLoopCore.NothingAskedSentence, AgentLoopCore.NothingAskedHtml(TurnCapability.ReadOnly, caught));
      Assert.Equal(AgentLoopCore.NothingAskedFact, AgentLoopCore.NothingAskedReport(TurnCapability.ReadOnly, caught));

      // The same prose after a tool call in the last step, a refused request, a card or a stop is not caught.
      Assert.False(AgentLoopCore.AnnouncedNothingAsked(TurnCapability.ReadOnly,
        new TurnResult { Outcome = TurnOutcome.Complete, Text = caught.Text, LastStepToolCalls = 2 }));
      Assert.False(AgentLoopCore.AnnouncedNothingAsked(TurnCapability.ReadOnly,
        new TurnResult { Outcome = TurnOutcome.Complete, Text = caught.Text, RequestCalls = 1 }));
      Assert.False(AgentLoopCore.AnnouncedNothingAsked(TurnCapability.ReadOnly,
        new TurnResult { Outcome = TurnOutcome.Complete, Text = caught.Text, AskedForConsent = true }));
      Assert.False(AgentLoopCore.AnnouncedNothingAsked(TurnCapability.ReadOnly,
        new TurnResult { Outcome = TurnOutcome.Stopped, Text = caught.Text }));
      // An approved build turn is left to its own banner, as the build family always was.
      Assert.False(AgentLoopCore.AnnouncedNothingAsked(TurnCapability.Build, caught));
    }

    [Fact]
    public void The_earlier_catches_still_catch_what_they_caught()
    {
      var r = new TurnResult { Outcome = TurnOutcome.Complete, Text = "Raising the build card for B1 now.", LastStepToolCalls = 1, ActingCalls = 0 };
      Assert.True(AgentLoopCore.AnnouncedNothingAsked(TurnCapability.ReadOnly, r));
    }

    [Theory]
    [InlineData(true, true, true, false, true)]      // automatic mode, forcible, caught, first in the chain: follow up once
    [InlineData(false, true, true, false, true)]     // Brief BT: "ask" too where the card can be forced; it waits for the click
    [InlineData(true, false, true, false, true)]     // Ollama under an automatic mode: unforced, nobody is there to type
    [InlineData(false, false, true, false, false)]   // Ollama under "ask": the user is there and the turn-end line told them
    [InlineData(true, true, true, true, false)]      // caught twice in a chain: once only
    [InlineData(true, true, false, false, false)]    // nothing caught
    public void The_follow_up_decision_is_bounded_to_one_per_chain(bool automatic, bool canForce, bool caught, bool isFollowUp, bool expected)
    {
      Assert.Equal(expected, AutoFollowUp.ShouldRunForAnnouncement(automatic, canForce, caught, isFollowUp));
    }

    [Fact]
    public void The_follow_up_fact_and_line_are_pinned_and_the_panel_wires_them_once()
    {
      Assert.Equal("PLUGIN FACT (from Dwight, not the user): Your last message announced a change and raised no card. " +
                   "Raise the card now.", AutoFollowUp.AnnouncementFact);
      Assert.Equal("Dwight is continuing: the last message announced a change and raised no card.", AutoFollowUp.AnnouncementLine);

      var source = File.ReadAllText(Path.Combine(RepoRoot(), "Dwight", "UI", "DwightScaffold.cs"));
      Assert.Contains("AutoFollowUp.ShouldRunForAnnouncement(", source);
      Assert.Contains("AutoFollowUp.AnnouncementFact", source);
      Assert.Contains("isAnnouncementFollowUp: true", source);
      Assert.Contains("bool isAnnouncementFollowUp = false", source);

      var driver = File.ReadAllText(Path.Combine(RepoRoot(), "Dwight", "Agent", "AgentTurnDriver.cs"));
      Assert.Contains("result.LastStepToolCalls = toolUses.Count;", driver);
    }

    private static int Count(string text, string needle)
    {
      var n = 0;
      for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal)) n++;
      return n;
    }

    private static string RepoRoot()
    {
      var dir = new DirectoryInfo(AppContext.BaseDirectory);
      while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Dwight.sln"))) dir = dir.Parent;
      Assert.True(dir != null, "the repository root (Dwight.sln) could not be found from " + AppContext.BaseDirectory);
      return dir.FullName;
    }
  }
}
