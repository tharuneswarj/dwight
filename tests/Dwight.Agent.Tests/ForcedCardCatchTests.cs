using Dwight.Agent;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// Brief BT section 4: under "Do not ask" a forced card RUNS, so a false positive changes the canvas. The positives and the real
  /// negatives are from the lamp and wall threads and the gate records; the adversarial negatives are the sentences the line's generous
  /// catch reads as announcements, which is why forcing has a catch of its own.
  /// </summary>
  [Collection(TurnShapeTests.StaticRecord)]   // touches TurnShape's static counters (Clear, Examined, AnnouncedChangeCount): never in parallel with the classes that read them; main went red on the #222 merge with a count of 3 for 1
  public class ForcedCardCatchTests
  {
    [Theory]
    [InlineData("I'll place:\n- a Number Slider\n- a Python script")]
    [InlineData("That map belongs to the wall, so I'll place the lamp generator separately with **10 exposed controls**.")]
    [InlineData("That map is the wall's, so I'll add a separate modular lamp scaffold first:")]
    [InlineData("I'll add a separate lamp scaffold for the corrected form.")]
    [InlineData("I'll create a separate lamp scaffold first; once approved, I can populate it.")]
    [InlineData("The earlier scaffold card was never raised, and the existing **A1-C1 map belongs to a wall definition**, so I'll create a separate modular lamp map before placing the script and controls.")]
    [InlineData("I found the four empty lamp regions-**A3, B2, C2, and D1**-and will build the adjustable inputs first.")]
    [InlineData("I found the four empty lamp regions and can build them now.")]
    [InlineData("This canvas is empty, so I'll first lay out empty regions for:")]
    [InlineData("I'll set the first control now: **Max Relief from 58 mm to 100 mm**")]
    [InlineData("The values are clear, so I'm preparing the approval card now")]
    [InlineData("Raising the scaffold card first for one region named \"Squares\".")]
    [InlineData("I'll lay out and group the whole canvas")]
    [InlineData("I’ll rebuild A1 now.")]
    [InlineData("Next, I'll wire the slider into the script.")]
    [InlineData("Confirmed: all six regions are on canvas. Raising the build for A1: a Surface param and two Number Sliders.")]
    [InlineData("I'll append a new input offset and change the output.")]
    [InlineData("Raising the build card now for A2 - it will place the Curve param.")]
    public void A_real_announcement_may_force_the_card(string message)
    {
      Assert.True(TurnShape.AnnouncedForForcing(message, lastStepToolCalls: 0, requestCalls: 0), message);
    }

    [Theory]
    // Real excuses and prose, from the threads.
    [InlineData("I can't raise a build card yet because no lamp scaffold exists.")]
    [InlineData("I can't raise the build card from this turn because the required mutation request is unavailable.")]
    [InlineData("The direct-build card still was not raised, and I can't submit it safely yet.")]
    [InlineData("I cannot raise the direct-build card in this turn because the regions are not on the canvas.")]
    [InlineData("The curve **is affecting the wall**.")]
    [InlineData("These regions are currently empty; the next step is to build them.")]
    [InlineData("A stronger, cleaner setting would be: 100 mm. Each value change requires its own approval card.")]
    [InlineData("Please confirm:\n- the radius\n- the height")]
    // A question back, with and without its mark.
    [InlineData("Shall I build A1?")]
    [InlineData("Which region should I build first, A1 or B1?")]
    [InlineData("Let me know which region to build.")]
    [InlineData("Tell me the height and I'll build it.")]
    // Waiting on the user.
    [InlineData("Once you confirm I will build A1.")]
    [InlineData("I'll wait for your go-ahead before I build.")]
    [InlineData("I'll build it after you confirm.")]
    [InlineData("I'll build it as long as you keep the radius.")]
    [InlineData("Please confirm the radius and I'll build it.")]
    [InlineData("I'll need the diameter before I can build.")]
    [InlineData("If you approve, I'll rebuild A1.")]
    [InlineData("When you're ready, I'll create the map.")]
    [InlineData("Say the word and I'll raise a build card.")]
    // Real, from the saved threads: the user's act comes first, in words the first draft of this catch read as a promise.
    [InlineData("For (1): please clear A1's contents again as you did before, and I'll rebuild it correctly in one pass.")]
    [InlineData("Say which address(es) to build first (A1 and B1 are the natural starting point), and I'll place and wire real components into exactly those.")]
    [InlineData("None of my build tools let me set a value on a component after the fact except set_value.")]
    [InlineData("Note: I'm not going to just assert \"I'll group the whole canvas\" without actually raising a card.")]
    [InlineData("Let me pin down the exact plan before raising the build card.")]
    [InlineData("This is a component delete, which falls under the edit capability.")]
    // A description of what a card would do.
    [InlineData("I'll describe what the card would do.")]
    [InlineData("The card would say that I'll build A1 and B1.")]
    [InlineData("I'll explain how I would build this.")]
    [InlineData("Let me explain what would change.")]
    [InlineData("Raising the amplitude would change the wave height.")]
    // An answer about the past.
    [InlineData("Earlier I said I'll build A1, but it already exists.")]
    [InlineData("I said I'll add the slider, and it landed last turn.")]
    [InlineData("I built A1 and wired it to B1.")]
    // Reads and talk, whose verbs only look like changes.
    [InlineData("Let me read the scaffold map first.")]
    [InlineData("Let me check how the wires connect.")]
    [InlineData("I'll explain the offset between the two curves.")]
    [InlineData("I'll add up the numbers.")]
    [InlineData("I'll lay out the options.")]
    [InlineData("I'll simplify the explanation.")]
    [InlineData("I'll move on to the next point.")]
    [InlineData("I'll build on that idea in prose.")]
    [InlineData("I'll write a short note on it.")]
    // Sonnet 5's headings on the lamp (#1168).
    [InlineData("Now D1: SilkWorm Output.")]
    // What is not the writer's own promise.
    [InlineData("Here is what I found:\n- I'll build A1\n- I'll add a slider")]
    [InlineData("> I'll build the lamp\n\nThat is what the last message said.")]
    [InlineData("```python\n# I'll add the offset here\n```")]
    public void What_is_not_an_announcement_never_forces(string message)
    {
      Assert.False(TurnShape.AnnouncedForForcing(message, lastStepToolCalls: 0, requestCalls: 0), message);
    }

    [Fact]
    public void A_turn_that_called_a_tool_or_asked_never_forces()
    {
      Assert.False(TurnShape.AnnouncedForForcing("I'll build A1 now.", lastStepToolCalls: 1, requestCalls: 0));
      Assert.False(TurnShape.AnnouncedForForcing("I'll build A1 now.", lastStepToolCalls: 0, requestCalls: 1));
      Assert.False(TurnShape.AnnouncedForForcing("", lastStepToolCalls: 0, requestCalls: 0));
      Assert.False(TurnShape.AnnouncedForForcing(null, lastStepToolCalls: 0, requestCalls: 0));
    }

    // The two catches disagree on purpose, and this pins which way each disagreement falls: the line stays generous, the force stays strict.
    [Theory]
    [InlineData("Let me know which region to build.")]
    [InlineData("I'll explain how I would build this.")]
    [InlineData("Raising the amplitude would change the wave height.")]
    [InlineData("I'll need the diameter before I can build.")]
    [InlineData("Earlier I said I'll build A1, but it already exists.")]
    public void The_line_may_fire_where_the_force_must_not(string message)
    {
      Assert.True(TurnShape.AnnouncedIntentWithoutCard(message, mutationAvailable: true, lastStepToolCalls: 0, requestCalls: 0), message);
      Assert.False(TurnShape.AnnouncedForForcing(message, lastStepToolCalls: 0, requestCalls: 0), message);
    }

    [Theory]
    [InlineData("The earlier scaffold card was never raised, and the existing A1-C1 map belongs to a wall definition, so I'll create a separate modular lamp map.")]
    [InlineData("I found the four empty lamp regions and will build the adjustable inputs first.")]
    public void The_force_reads_what_the_line_missed_in_the_field(string message)
    {
      Assert.False(TurnShape.AnnouncedIntentWithoutCard(message, mutationAvailable: true, lastStepToolCalls: 0, requestCalls: 0), message);
      Assert.True(TurnShape.AnnouncedForForcing(message, lastStepToolCalls: 0, requestCalls: 0), message);
    }
  }
}
