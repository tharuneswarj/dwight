using System.Linq;
using System.Text.Json.Nodes;
using Dwight.Agent;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// Phase-9 U6 + U21 (#760): the kind `edit` on the request_mutation surface - the kind table, the published
  /// descriptor, the parse, the seed, the breaker.
  ///
  /// THE KIND TABLE'S SENTENCE IS A CONTRACT ABOUT OUR OWN BEHAVIOUR (#315), so each claim in it is paired below with a
  /// test on the behaviour's side: the cap of forty, the refusal of a loop and of a group, "added alongside" an input's
  /// existing sources, the failing rows named. (The ones in <c>EditOpsAskTests</c> are cross-referenced here by name.)
  /// </summary>
  public class EditKindTests
  {
    private static JsonObject Properties() => (JsonObject)AgentHostTools.All()[0].InputSchema["properties"];

    [Fact]
    public void Edit_is_an_offered_kind_with_both_halves_and_a_route()
    {
      Assert.Contains("edit", AgentHostTools.Kinds);
      var c = AgentHostTools.KindScope["edit"];
      Assert.False(string.IsNullOrWhiteSpace(c.Can));
      Assert.False(string.IsNullOrWhiteSpace(c.Cannot));
      Assert.Equal(ConsentRoute.Edit, ConsentCard.Route("edit"));
      Assert.Contains("\"edit\"", AgentHostTools.SchemaOf(AgentHostTools.RequestMutation));
    }

    [Fact]
    public void The_kind_sentence_states_what_edit_can_and_cannot_do_and_every_claim_is_a_real_behaviour()
    {
      var can = AgentHostTools.KindScope["edit"].Can;
      var cannot = AgentHostTools.KindScope["edit"].Cannot;

      // CAN: the three ops, the one card, the ticks, the named consequence, the check, one Ctrl-Z, one re-read
      foreach (var phrase in new[] { "add a wire", "remove an existing wire", "delete an existing component", "ONE card",
                                     "ticks the ones they accept", "names the inputs it leaves without a source",
                                     "only the ticked rows", "puts everything back if it did", "one Ctrl-Z", "one turn to re-read" })
        Assert.Contains(phrase, can);

      // CANNOT: each limit is enforced - see the tests named here
      Assert.Contains("At most 40", cannot);                 // EditOpsAskTests.Nothing_or_not_an_array_or_too_many_rows...
      Assert.Equal(40, EditOpsAsk.MaxOps);
      Assert.Contains("make a loop", cannot);                // EditOpsAskTests.A_component_cannot_feed_itself...
      Assert.Contains("touch a group or a note", cannot);    // EditOpsAskTests.An_id_that_is_alive_but_not_a_component...
      Assert.Contains("ADDED alongside what an input already has", cannot);   // ...Wiring_into_an_input_that_has_a_source_APPENDS
      Assert.Contains("the whole card is refused with the failing rows named", cannot);   // ...Every_failing_row_is_named
      // and it points at the kinds that DO the things edit does not, rather than leaving the model to invent them
      Assert.Contains("set_value", cannot);
      Assert.Contains("edit_script", cannot);
    }

    [Fact]
    public void The_generated_capability_sentence_contains_delete_if_and_only_if_a_kind_backs_it()
    {
      // The gate caught the model promising a "delete" card that no kind backed (#926), and a test held that the
      // generated sentence never contained the word. That guard is the SAME guard, now two-sided: the word is allowed
      // exactly when `edit` is offered, and a withdrawn `edit` would make this fail at the prompt still promising it.
      var sentence = AgentHostTools.CapabilitySentence().ToLowerInvariant();
      Assert.Equal(AgentHostTools.Kinds.Contains("edit"), sentence.Contains("delete"));
    }

    // ---- the published descriptor IS the contract (#316) -------------------------------------------------------------------

    [Fact]
    public void The_schema_publishes_ops_as_an_array_of_changes_with_the_three_ops_as_an_enum()
    {
      var ops = (JsonObject)Properties()["ops"];
      Assert.Equal("array", (string)ops["type"]);
      var item = (JsonObject)ops["items"];
      var itemProps = (JsonObject)item["properties"];

      Assert.Equal(new[] { "wire", "unwire", "delete" }, ((JsonArray)itemProps["op"]["enum"]).Select(v => (string)v).ToArray());
      foreach (var end in new[] { "from", "to" })
      {
        var p = (JsonObject)itemProps[end]["properties"];
        Assert.NotNull(p["id"]);
        Assert.NotNull(p["param"]);
        Assert.Equal("id", (string)((JsonArray)itemProps[end]["required"])[0]);   // an end without an id is not a row
      }
      Assert.NotNull(itemProps["id"]);                                            // delete's target
      Assert.Equal("op", (string)((JsonArray)item["required"])[0]);
      Assert.Contains("kind=edit", (string)ops["description"]);                   // said to belong to this kind, not another (#907)
    }

    [Fact]
    public void A_model_shaped_request_ARRIVES_on_the_seed_and_is_not_validated_at_the_parse()
    {
      var args = new JsonObject
      {
        ["kind"] = "edit",
        ["why"] = "tidy the wiring",
        ["ops"] = new JsonArray(
          EditFixture.DeleteOp(4),
          EditFixture.WireOp(3, "Result", 6, null)),
      };
      Assert.True(AgentHostTools.TryParseRequest(args, out var kind, out _, out var seed, out var error), error?.Message);
      Assert.Equal("edit", kind);
      Assert.Equal(2, seed.Ops.Count);
      Assert.Equal(args["ops"].ToJsonString(), seed.Ops.ToJsonString());
    }

    [Fact]
    public void Ops_that_are_not_an_array_arrive_as_nothing_never_coerced_and_the_ask_refuses_them()
    {
      var args = new JsonObject { ["kind"] = "edit", ["why"] = "because", ["ops"] = "delete everything" };
      Assert.True(AgentHostTools.TryParseRequest(args, out _, out _, out var seed, out _));
      Assert.Null(seed.Ops);
      Assert.False(EditOpsAsk.Validate(seed.Ops, new EditFixture().Canvas(), out _, out var error));
      Assert.Contains("needs \"ops\"", error.Message);
    }

    // ---- the seed and the breaker ---------------------------------------------------------------------------------------------

    [Fact]
    public void The_seed_plan_for_edit_offers_one_action_whatever_the_seed_says_and_carries_the_ops()
    {
      var honest = new HandoffSeed { Ops = EditFixture.Ops(EditFixture.DeleteOp(4)), Mode = "vertical", ComponentIds = { 4 } };
      var hostile = new HandoffSeed { Ops = EditFixture.Ops(EditFixture.DeleteOp(4)), Mode = "delete-everything", ComponentIds = { 1, 2, 3, 4, 5 } };

      var a = SeedPlan.From("edit", honest, _ => IdState.Alive);
      var b = SeedPlan.From("edit", hostile, _ => IdState.Alive);

      Assert.Equal(new[] { "edit" }, a.Actions);
      Assert.Equal(a.Actions, b.Actions);   // #287(d): the authority boundary is the shape of the function
      Assert.Equal(1, a.Ops.Count);
      Assert.Null(a.PreFillMode);           // edit has no modes; a suggestion is ignored, never borrowed from another kind
    }

    [Fact]
    public void Edit_never_arms_the_loop_breaker()
    {
      // A reverted edit leaves the canvas stamp unchanged; an armed breaker would then tell the next ask "nothing changed
      // ... you cannot make this change", which is false (#613). The sentence must be true in every state that arms it.
      Assert.Equal(BreakerArming.Never, MutationRequestLog.ArmingOf("edit"));
      var log = new MutationRequestLog();
      log.RecordApproved("edit", "stamp");
      Assert.False(log.RepeatWithNothingChanged("edit", "stamp"));
    }

    [Fact]
    public void An_edit_ask_is_a_request_like_any_other_it_needs_a_reason()
    {
      var args = new JsonObject { ["kind"] = "edit", ["ops"] = new JsonArray(EditFixture.DeleteOp(4)) };
      Assert.False(AgentHostTools.TryParseRequest(args, out _, out _, out _, out var error));
      Assert.Contains("why", error.Message);
    }
  }
}
