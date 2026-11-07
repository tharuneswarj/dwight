using Dwight.Agent;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// Brief CH: the two announcement catches against lane A's 17 real turns (31 October, read cold by the judges and by Architect Test), and the one rule they now share. The line says "Dwight did not
  /// receive a request", so it must not speak under a sentence that asked the user something and promised what follows the answer. The force runs a card under "Do not ask", so it stays the stricter of
  /// the two. The text is the models' own, verbatim, down to the cut at the start of each turn's end.
  /// </summary>
  [Collection(TurnShapeTests.StaticRecord)]   // touches TurnShape's static counters (Clear, Examined, AnnouncedChangeCount): never in parallel with the classes that read them; main went red on the #222 merge with a count of 3 for 1
  public class AnnouncementCatchTests
  {
    private static TurnResult Turn(string text) => new TurnResult { Outcome = TurnOutcome.Complete, Text = text, LastStepText = text };
    private static bool Line(string text) => AgentLoopCore.AnnouncedNothingAsked(TurnCapability.ReadOnly, Turn(text));
    private static bool Forces(string text) => AgentLoopCore.AnnouncedForForcing(TurnCapability.ReadOnly, Turn(text));

    [Theory]
    // The true announcements. Two of them end by inviting edits, and the invitation clears nothing: they announce first and invite second.
    [InlineData("a01-student-vague T2", ":** the panel Breps, the panel count and the opening-area ratio.  Next I'll build the components into the regions. I'll start with A1 and B1, since the script in C1 needs their outputs. Tell me if you want to change anything in the plan first, for example a different base surface or more parameters.", true, true)]
    [InlineData("a02-never-opened T2", "it's cheaper to change the plan than the built definition.  **Next step:** I'll build A1 first, since everything else depends on it. It's only sliders, so it's the easiest place to see how Grasshopper works. Then I'll build B1 to D1. Tell me if you want to change anything in the plan before I start.", true, true)]
    [InlineData("a18-changes-mind T5", "x Grid 6x6:** a count slider set to 6, a cell-size slider, and the Hexagonal component (Vector > Grid). - **A2, Circle of 12 Points:** a radius slider, a count slider set to 12, a Circle component and Divide Curve.  I haven't added any components. To fill them, I'll raise a build card for A1 and A2.", true, true)]
    // The conditional offers the line used to speak under.
    [InlineData("a02-never-opened T3", "built around an attractor point and a floor grid. For this study, I'd swap that for a single line of fins driven by the sun vector, and drop the floor and bay grid.  Tell me the latitude, facade height and whether the fins should rotate or stay fixed. Then I'll raise a revised scaffold and build it.", false, false)]
    [InlineData("a17-wrong T1", "you want the radius value, one of the four sliders may be the one you mean. I can't tell which, because I haven't read their nicknames. The Panel may also be in a different open document, or it may have been deleted earlier.  Tell me which component you want removed and I'll raise the delete for it.", false, false)]
    [InlineData("a19-long-workflow T6", "ed the canvas since.  If the cleanup is still wanted, the edit would need these rows: - Unwire Series \u2192 Unit X. - Unwire Unit X \u2192 Construct Point's X input. - Wire Series \u2192 Construct Point's X input. - Delete Unit X. - Unwire Height \u2192 Line SDL's Length.  Tell me to go ahead and I'll raise that card.", false, false)]
    [InlineData("c12-undo T1, the brief's a12-undo-style", " steps back through changes in order. If you want to reject one change but keep a later one, you'll have to do that by hand. - I can't snapshot or restore a file. I can check whether the definition is saved and has unsaved changes if you'd like.  Tell me what you want to try and I'll raise the card.", false, false)]
    [InlineData("d-oai-vague T1", "od for: massing, skyline, programmatic variation.  For a first Grasshopper definition, I recommend the **attractor fa\u00e7ade**: it demonstrates genuine parametric logic without becoming too difficult. Reply with `1`, `2`, or `3`, and I\u2019ll map out a buildable definition with the key sliders and outputs.", false, false)]
    [InlineData("d-oll-vague T1", ", a facade fragment, a prototype? - What should it respond to \u2014 sun, a draggable point, a curve, a growth rule?  Say which grabs you (or describe the brief and I'll match it) and I'll lay down a region map for it \u2014 purpose, inputs and outputs per region \u2014 that you approve before anything gets built.", false, false)]
    // The eight in plain words, none of them an announcement.
    [InlineData("a07-compute T1", "component in a new definition. 3. Start Compute and send a test request with BayWidth, BayCount, Height and FinDepth. 4. Build the page that sends the same values.  If you want, I can raise a card to add the Get Number params and the output rename once you tell me which parameter \"radius\" should be.", false, false)]
    [InlineData("a11-kangaroo T2", "- **C1 Result and Flip:** shows the hanging mesh and a mirrored copy, the inverted compression shell.  Nothing is built yet. Once you've looked over the map, ask me to build it and I'll raise a build card for A1 to C1. If you want a plain chain first, I can use a 1D line of points instead of a grid.", false, false)]
    [InlineData("a13-site T2", "ilding, I need three things from you: - Building heights: do you have them as attributes, or should I use a slider? - The location and date range for the sun analysis. - Whether the view corridors are fixed lines or angular fans from a viewpoint.  Once you've answered, I'll request the build for A1.", false, false)]
    [InlineData("a25-opus-revit T1", "all runs in one transaction.  **Before you run it:** do it on a copy of the project, and run it on a handful of walls first. If you install Rhino.Inside.Revit on this machine, I can read the components it provides and build this from its native nodes, with sliders for the offset and the name suffix.", false, false)]
    [InlineData("c01-fabrication T2", "wn on the card before it runs.  To start, I need the answers to the questions from my last message: section and laminations, stock size, flat-bend or machined-curved, and your target controller. If you'd rather start now, say so. I'll assume a 12 m span, a flat-bent lath and generic toolpath output.", false, false)]
    [InlineData("c09-vague T1", "s without it.  **To start, tell me:** 1. What you want to make. A sentence is enough. 2. Any key parameters you want as sliders, such as counts, sizes or angles. 3. Whether you prefer Python or C# for the script parts. I'll use Python 3 by default.  Or open or paste a definition and ask me about it.", false, false)]
    [InlineData("c16-opus-student T1", "Once you choose, I'll lay out a map of the definition: empty labelled sections such as inputs, logic and output, each describing what it will do. Then I'll fill them in, and you approve each step before anything goes on the canvas. If you can't decide, **#1 (attractor fa\u00e7ade)** gives the most result for the least effort and is easy to defend in a review.", false, false)]
    [InlineData("d-goo-vague T1", "rban design iteration.  ---  **What is your studio brief about** (e.g. a pavilion, housing, cultural building, high-rise, or facade detail)?   If one of the above fits, tell me which one you prefer (or name your site/brief constraints), and I will lay down the scaffold on the canvas to get it built.", false, false)]
    public void Lane_As_turns_end_as_they_were_judged(string row, string end, bool line, bool forces)
    {
      Assert.True(Line(end) == line, row + ": the line");
      Assert.True(Forces(end) == forces, row + ": the force");
    }

    [Theory]
    [InlineData("a01-student-vague T2", "Next I'll build the components into the regions", true, true)]
    [InlineData("a02-never-opened T2", "**Next step:** I'll build A1 first, since everything else depends on it", true, true)]
    [InlineData("a18-changes-mind T5", "to fill them, i'll raise a build card for a1 and a2", true, true)]
    // The sentence the brief calls the clearest kind of announcement, and it is one, with nothing before it. Above, the answers it waits on sit straight before it.
    [InlineData("a02-never-opened T3, with nothing before it", "Then I'll raise a revised scaffold and build it", true, true)]
    [InlineData("a17-wrong T1", "Tell me which component you want removed and I'll raise the delete for it", false, false)]
    [InlineData("a19-long-workflow T6", "Tell me to go ahead and I'll raise that card", false, false)]
    [InlineData("c12-undo T1", "Tell me what you want to try and I'll raise the card", false, false)]
    [InlineData("d-oai-vague T1", "Reply with `1`, `2`, or `3`, and I\u2019ll map out a buildable definition with the key sliders and outputs", false, false)]
    [InlineData("d-oll-vague T1", "Say which grabs you (or describe the brief and I'll match it) and I'll lay down a region map for it \u2014 purpose, inputs and outputs per region \u2014 that you approve before anything gets built", false, false)]
    [InlineData("a07-compute T1", "If \"radius\" means something else, such as Fin Depth or Bay Width, tell me and I'll map it", false, false)]
    [InlineData("a11-kangaroo T2", "Once you've looked over the map, ask me to build it and I'll raise a build card for A1 to C1", false, false)]
    [InlineData("a13-site T2", "Once you've answered, I'll request the build for A1", false, false)]
    [InlineData("a25-opus-revit T1", "Tell me and I'll change it", false, false)]
    [InlineData("c01-fabrication T2", "I'll assume a 12 m span, a flat-bent lath and generic toolpath output", false, false)]
    // The promise sits in a list item, after the imperative that opens it, and the list is what clears it.
    [InlineData("c09-vague T1", "- Describe a design, for example \"a parametric facade with a sine-wave panel pattern\" or \"a spiral staircase\". I'll propose a scaffold, which is a map of named empty regions. After you approve it, I'll fill the regions with wired components and scripts.", false, false)]
    [InlineData("c09-vague T1", "3. Whether you prefer Python or C# for the script parts. I'll use Python 3 by default.", false, false)]
    [InlineData("c16-opus-student T1", "Once you choose, I'll lay out a map of the definition: empty labelled sections such as inputs, logic and output, each describing what it will do", false, false)]
    [InlineData("d-goo-vague T1", "If one of the above fits, tell me which one you prefer (or name your site/brief constraints), and I will lay down the scaffold on the canvas to get it built", false, false)]
    public void Lane_As_sentences_read_as_they_were_judged(string row, string sentence, bool line, bool forces)
    {
      Assert.True(Line(sentence) == line, row + ": the line");
      Assert.True(Forces(sentence) == forces, row + ": the force");
    }

    [Fact]
    public void The_sentence_a_forced_turn_quotes_is_the_promise_and_not_the_invitation()
    {
      Assert.Equal("next i'll build the components into the regions",
        TurnShape.ForcingSentence("Next I'll build the components into the regions. I'll start with A1 and B1, since the script in C1 needs their outputs. Tell me if you want to change anything in the plan first, for example a different base surface or more parameters.", 0, 0));
      Assert.Equal("next step: i'll build a1 first, since everything else depends on it",
        TurnShape.ForcingSentence("**Next step:** I'll build A1 first, since everything else depends on it. It's only sliders. Then I'll build B1 to D1. Tell me if you want to change anything in the plan before I start.", 0, 0));
    }

    [Theory]
    // One sentence: the user's move, then the promise.
    [InlineData("Tell me the radius and I'll build A1.")]
    [InlineData("**Tell me** the radius and I'll build A1.")]
    [InlineData("Please confirm the radius and I'll build A1.")]
    [InlineData("Let me know which one, and I'll build it.")]
    [InlineData("Choose one and I'll build it.")]
    [InlineData("Pick a region, then I'll build it.")]
    [InlineData("Say which and I'll build it.")]
    [InlineData("Reply with a number, and I'll build it.")]
    [InlineData("Describe the brief and I'll build it.")]
    [InlineData("Give me the radius and I'll build it.")]
    [InlineData("If you want, I'll build A1.")]
    [InlineData("Once you choose, I'll build A1.")]
    // Two sentences, side by side.
    [InlineData("Tell me the radius. Then I'll build A1.")]
    [InlineData("Choose one. Then I'll build it.")]
    [InlineData("Pick a region. After that, I'll build it.")]
    [InlineData("Once you choose, I'll build a map. Then I'll build the rest.")]
    [InlineData("Tell me:\n- the radius\n- the height\nThen I'll build A1.")]
    public void A_promise_that_waits_on_the_user_fires_no_catch(string text)
    {
      Assert.False(Line(text), "the line: " + text);
      Assert.False(TurnShape.AnnouncedWithoutAsking(text, 0, 0, buildFamily: true), "the build family: " + text);
      Assert.False(TurnShape.AnnouncedIntentWithoutCard(text, true, 0, 0), "the structural catch: " + text);
      Assert.False(Forces(text), "the force: " + text);
    }

    [Theory]
    [InlineData("Tell me the radius and I'll raise the card.")]
    [InlineData("Tell me the radius. Then I'll raise the card.")]
    [InlineData("Tell me:\n- the radius\n- the height\nThen I'll raise the card.")]
    public void A_card_promised_after_the_users_answer_is_not_a_request_announced(string text)
    {
      Assert.False(AgentLoopCore.AnnouncedNothingAsked(TurnCapability.Build, Turn(text)), text);
    }

    /// <summary>Only the request family reads list items, which is why this one needs its own pin: a bullet is not the sentence before.</summary>
    [Fact]
    public void A_bullet_is_not_the_sentence_before()
    {
      Assert.True(AgentLoopCore.AnnouncedNothingAsked(TurnCapability.Build, Turn("- Tell me the radius.\nThen I'll raise the card.")));
    }

    /// <summary>The force's own conditions, which the shared rule does not hold: a promise on something other than the user's move still costs only the line.</summary>
    [Theory]
    [InlineData("I'll build A1 if the radius holds.")]
    [InlineData("I'll build A1 once the map is ready.")]
    [InlineData("I'll build A1 when the map is ready.")]
    [InlineData("I'll build A1 unless it is already there.")]
    [InlineData("I read the map and I'll build A1.")]
    public void The_forces_own_conditions_still_hold_it(string text)
    {
      Assert.False(Forces(text), text);
    }

    [Theory]
    // The pair is two sentences side by side, and only "Then I'll". A bare "I'll" after a question of taste is an announcement.
    [InlineData("Tell me the radius. I read the map. Then I'll build A1.", true, false)]
    [InlineData("Tell me the radius. I read the map and then I'll build it.", true, false)]
    [InlineData("Let me know what you think. I'll build A1 now.", true, false)]
    // A list item says nothing about what the writer's next sentence waits on, and the force drops it.
    [InlineData("- Tell me the radius.\nThen I'll build A1.", true, true)]
    // The first person's "confirm", and "say" as "for example": not the user's move.
    [InlineData("I'll confirm the wiring and I'll build it.", true, false)]
    [InlineData("Please note I'll build A1.", true, false)]
    [InlineData("As I say, I'll build A1.", true, true)]
    [InlineData("With a radius, say 10, I'll build A1.", true, true)]
    [InlineData("I read the map, so I'll build A1.", true, true)]
    public void A_promise_that_does_not_wait_keeps_the_line(string text, bool line, bool forces)
    {
      Assert.True(Line(text) == line, "the line: " + text);
      Assert.True(Forces(text) == forces, "the force: " + text);
    }

    [Theory]
    // The last sentence invites a change, and the promise before it stands.
    [InlineData("I'll build A1. Tell me if you want to change anything first.", true)]
    [InlineData("I'll build A1. Let me know if you'd like a different base surface.", true)]
    [InlineData("I'll build A1. Tell me if you would prefer a mesh.", true)]
    // Anything else that holds the message up keeps the force off, at any distance, because under "Do not ask" a forced card runs. The line still speaks.
    [InlineData("I'll build A1. Tell me whether the fins rotate.", false)]
    [InlineData("I'll build A1. Tell me if the radius is right.", false)]
    [InlineData("I'll build A1. Tell me the radius first.", false)]
    [InlineData("I'll build A1. It has three sliders. Please confirm the radius.", false)]
    [InlineData("I need the diameter. I'll build A1.", false)]
    // An invitation before the promise is not a closing one.
    [InlineData("Tell me if you want changes. I'll build A1.", false)]
    public void Only_a_closing_invitation_leaves_the_force_on(string text, bool forces)
    {
      Assert.True(Line(text), "the line: " + text);
      Assert.True(Forces(text) == forces, "the force: " + text);
    }

    [Theory]
    [InlineData("I'll build A1 next.")]
    [InlineData("I'll raise a revised scaffold.")]
    [InlineData("I'll raise the delete.")]
    [InlineData("Then I'll raise that card.")]
    [InlineData("I'll place a Number Slider.")]
    [InlineData("I'll wire the slider into the script.")]
    [InlineData("I'll delete the Panel.")]
    [InlineData("I'll change the radius.")]
    [InlineData("I'll lay down the regions.")]
    [InlineData("I'll scaffold it now.")]
    [InlineData("I'll map out the regions.")]
    public void The_plain_future_of_a_change_forces(string text)
    {
      Assert.True(Forces(text), text);
    }

    [Theory]
    [InlineData("I'll raise a question about the radius.")]
    [InlineData("I'll raise the concern.")]
    [InlineData("I'll raise that with you.")]
    [InlineData("I'll raise it with the studio.")]
    [InlineData("I'll map out how the data flows.")]
    public void What_a_person_raises_or_maps_in_talk_does_not_force(string text)
    {
      Assert.False(Forces(text), text);
    }
  }
}
