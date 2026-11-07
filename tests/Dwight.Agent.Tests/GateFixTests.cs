using System;
using System.Collections.Generic;
using System.Linq;
using Dwight.Agent;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// The two behaviours the runtime gate bought (#256, #258) - both of them things a green suite could
  /// not have told us, and both now held by one.
  /// </summary>
  [Collection(TurnShapeTests.StaticRecord)]   // drives turns, which Observe into TurnShape's static counters: never in parallel with the classes that read them
  public class GateFixTests
  {
    // ---- #256: a tool that opens a human gate ends the turn -----------------------------------------

    // #611a: these two used to assert EndsTurn(name). They were green while a REFUSED request ended the
    // turn unread (#609 link 3), so what ends a turn is now read off the RESULT - and the loop itself is
    // driven in AgentTurnDriverTests, which is where the behaviour lives.

    [Fact]
    public void A_shown_card_is_what_ends_the_turn()
    {
      // Given nothing back and another step, the model called request_mutation FOUR times and then sent
      // the user hunting a Grasshopper dialog that has never existed. The answer cannot arrive in this
      // reply, so another step could only ask the same question again.
      Assert.True(AgentHostTools.ShowedCard(AgentHostTools.RequestMutation, AgentTurnDriverTests.CardShown));
      Assert.False(AgentHostTools.ShowedCard(AgentHostTools.RequestMutation, AgentTurnDriverTests.Refused));
      Assert.False(AgentHostTools.ShowedCard(AgentHostTools.RequestMutation, "not json"));
      Assert.False(AgentHostTools.ShowedCard(AgentHostTools.RequestMutation, "{\"ok\":true,\"result\":{}}"));
    }

    [Fact]
    public void No_catalogue_tool_ends_the_turn()
    {
      // Turn-ending is a property of opening a HUMAN gate, not of doing work. A canvas tool returns its
      // result and the loop carries on with it - even one whose envelope happens to say cardShown.
      Assert.All(AgentToolCatalog.All(),
        t => Assert.False(AgentHostTools.ShowedCard(t.Name, AgentTurnDriverTests.CardShown)));
    }

    [Fact]
    public void The_descriptor_tells_the_model_the_turn_ends_and_not_to_re_ask()
    {
      // The behaviour is enforced by the loop; saying it in the contract is what stops the model spending
      // a step discovering it. Both halves, or the model learns the rule by paying for it.
      var d = AgentHostTools.All().First(t => t.Name == AgentHostTools.RequestMutation).Description;

      Assert.Contains("ENDS YOUR TURN", d);
      Assert.Contains("cannot produce an answer", d);
      Assert.Contains("new message", d, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>#611a, the contract half: a refusal does not end the turn, and the descriptor says so - the
    /// loop behaviour is held in AgentTurnDriverTests, and this holds the sentence the model reads about it.</summary>
    [Fact]
    public void The_descriptor_tells_the_model_a_refusal_is_read_inside_the_turn()
    {
      var d = AgentHostTools.All().First(t => t.Name == AgentHostTools.RequestMutation).Description;

      Assert.Contains("IF THE REQUEST IS REFUSED", d);
      Assert.Contains("does NOT end", d);
      Assert.Contains("second refused request ends the turn", d, StringComparison.OrdinalIgnoreCase);
    }

    // ---- #258: diagnose, so the agent stops rebuilding a verb by sampling ---------------------------

    private static DiagnoseFaultDto Fault(int id, string kind = "error", bool root = false) =>
      new DiagnoseFaultDto { Id = id, Type = "Loft", Kind = kind, Root = root, Intrinsic = true, Message = "boom" };

    [Fact]
    public void Diagnose_says_its_list_is_complete_rather_than_a_sample()
    {
      // The whole reason the tool exists: given only primitive reads the agent sampled 25 components,
      // declared the definition clean, and was wrong by 150-200 failing params. The payload has to say
      // plainly that this is not a sample, or an agent will reach for the sampling habit anyway.
      var json = AgentToolCore.Diagnose(new[] { Fault(1) }, 0, 573, wholeCanvas: true).ToJson();

      Assert.Contains("COMPLETE list", json);
      Assert.Contains("not a sample", json);
      Assert.Contains("component by component", json);
    }

    [Fact]
    public void A_clean_scan_says_clean_and_carries_no_faults()
    {
      var json = AgentToolCore.Diagnose(new List<DiagnoseFaultDto>(), 3, 573, wholeCanvas: true).ToJson();

      Assert.Contains("\"clean\":true", json);
      Assert.Contains("\"faultCount\":0", json);
      Assert.Contains("\"strays\":3", json);
    }

    [Fact]
    public void A_long_fault_list_is_bounded_and_says_what_it_left_out()
    {
      // #209/#134 at the tool surface: the WP-3 loop pays for every row on every call.
      var many = Enumerable.Range(1, AgentToolCore.DiagnoseCap + 25).Select(i => Fault(i)).ToList();
      var json = AgentToolCore.Diagnose(many, 0, 900, wholeCanvas: true).ToJson();

      Assert.Contains("\"faultCount\":" + many.Count, json);   // the true total is never hidden
      Assert.Contains("omitted", json);
      Assert.Contains("25 further fault", json);
    }

    [Fact]
    public void A_short_fault_list_claims_no_omission()
    {
      Assert.DoesNotContain("omitted", AgentToolCore.Diagnose(new[] { Fault(1) }, 0, 10, wholeCanvas: true).ToJson());
    }

    [Fact]
    public void Diagnose_is_a_pure_read_and_every_turn_may_call_it()
    {
      // It reports the runtime messages Grasshopper already holds - no model call, no solve, no mutation
      // (#190). So it needs no consent, and a comprehension turn has it.
      var d = AgentToolCatalog.All().First(t => t.Name == AgentToolCatalog.Diagnose);
      Assert.False(d.Mutates);
      Assert.False(d.Solves);

      foreach (var capability in (TurnCapability[])Enum.GetValues(typeof(TurnCapability)))
        Assert.True(AgentLoopCore.IsGranted(capability, AgentToolCatalog.Diagnose));
    }

    [Fact]
    public void Diagnose_states_that_it_does_not_re_solve()
    {
      // It reads the LAST solution's state. Saying so is what sends the model to the solve consent card
      // (#222) rather than letting it treat stale facts as current ones.
      var d = AgentToolCatalog.All().First(t => t.Name == AgentToolCatalog.Diagnose).Description;
      Assert.Contains("does not re-run", d, StringComparison.OrdinalIgnoreCase);
    }
  }
}
