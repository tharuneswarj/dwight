using System;
using System.Linq;
using System.Text.Json.Nodes;
using Dwight.Llm;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// Ruling #192 - the three-state thinking wire, and the inertness proof re-scoped.
  ///
  /// WHY THREE STATES AND NOT A BOOLEAN. ADR-082 promises thinking is ON only for agentic Top-tier work
  /// and OFF for all comprehension. The API's default is not uniform: on the Claude 5 family omitting
  /// the field RUNS thinking, so "off" must be said explicitly; on Sonnet 4.6 / Opus 4.8 omitting means
  /// off. A single send-it-or-don't boolean would have honoured ADR-082 on one family and silently
  /// broken it on the other - comprehension thinking, and being billed for it, with nothing on screen
  /// to show for it.
  ///
  /// The state is resolved from CATALOGUE DATA, never from matching model ids - the pattern ADR-092
  /// rejected. These tests drive the decision directly, so they hold whatever the catalogue says.
  /// </summary>
  public class ThinkingWireTests
  {
    // ---- the resolution ---------------------------------------------------------------------------

    [Fact]
    public void Comprehension_on_a_default_OFF_family_sends_nothing()
    {
      // Sonnet 4.6 / Opus 4.8: silence already means off, and adding a field would only churn the cache.
      Assert.Equal(ThinkingWireState.Omit,
        ThinkingWire.Resolve(taskWantsThinking: false, thinkingOnByDefault: false, supportsAdaptive: true));
    }

    [Fact]
    public void Comprehension_on_a_default_ON_family_sends_an_explicit_disable()
    {
      // THE CASE A BOOLEAN WOULD HAVE MISSED. On the Claude 5 family, omitting leaves thinking running.
      Assert.Equal(ThinkingWireState.Disabled,
        ThinkingWire.Resolve(taskWantsThinking: false, thinkingOnByDefault: true, supportsAdaptive: true));
    }

    [Fact]
    public void An_agentic_task_asks_for_adaptive_thinking()
    {
      Assert.Equal(ThinkingWireState.Adaptive,
        ThinkingWire.Resolve(taskWantsThinking: true, thinkingOnByDefault: true, supportsAdaptive: true));
      Assert.Equal(ThinkingWireState.Adaptive,
        ThinkingWire.Resolve(taskWantsThinking: true, thinkingOnByDefault: false, supportsAdaptive: true));
    }

    [Fact]
    public void A_model_with_no_adaptive_mode_is_never_asked_to_think()
    {
      // Asking is a 400. Degrading to "runs without thinking" is the honest alternative.
      Assert.Equal(ThinkingWireState.Omit,
        ThinkingWire.Resolve(taskWantsThinking: true, thinkingOnByDefault: false, supportsAdaptive: false));
    }

    [Fact]
    public void A_model_with_no_thinking_mode_gets_no_field_either_way()
    {
      Assert.Equal(ThinkingWireState.Omit,
        ThinkingWire.Resolve(taskWantsThinking: false, thinkingOnByDefault: false, supportsAdaptive: false));
    }

    // ---- effort ------------------------------------------------------------------------------------

    [Theory]
    [InlineData("balanced", "medium")]
    [InlineData("quality", "high")]
    [InlineData("ultra", "max")]
    public void Profile_maps_to_reasoning_depth(string profile, string expected) =>
      Assert.Equal(expected, ThinkingWire.EffortForProfile(profile));

    [Fact]
    public void An_unknown_profile_takes_the_api_default_rather_than_guessing()
    {
      Assert.Null(ThinkingWire.EffortForProfile("economy"));
      Assert.Null(ThinkingWire.EffortForProfile("nonsense"));
      Assert.Null(ThinkingWire.EffortForProfile(null));
    }

    // ---- what actually goes on the wire ------------------------------------------------------------

    private static LlmRequest Req(bool thinking = false, string effort = null) => new LlmRequest
    {
      System = "You are Dwight.",
      Messages = { LlmMessage.User("Explain this definition.") },
      MaxTokens = 4096,
      CacheSystem = true,
      Thinking = thinking,
      Effort = effort,
    };

    private static JsonObject Body(LlmRequest r, ThinkingWireState state) =>
      (JsonObject)JsonNode.Parse(AnthropicRequestBody.Build(r, "claude-test", state));

    [Fact]
    public void Omit_emits_no_thinking_and_no_output_config()
    {
      var body = Body(Req(), ThinkingWireState.Omit);
      Assert.Null(body["thinking"]);
      Assert.Null(body["output_config"]);
    }

    [Fact]
    public void THE_INERTNESS_PROOF_omit_is_byte_identical_to_the_pre_thinking_body()
    {
      // #192a, re-scoped: byte-identity is claimed ONLY where off == omit. The whole document must be
      // untouched - same keys, same order, same values - or Stage B has changed behaviour for all five
      // shipped verbs while claiming to have changed none.
      var request = Req();

      var withThinkingFieldAbsent = AnthropicRequestBody.Build(request, "claude-test", ThinkingWireState.Omit);
      var expectedKeys = new[] { "model", "max_tokens", "stream", "system", "messages" };

      var body = (JsonObject)JsonNode.Parse(withThinkingFieldAbsent);
      Assert.Equal(expectedKeys, body.Select(kv => kv.Key).ToArray());

      // And it is stable: building twice yields the same bytes, so nothing non-deterministic crept in.
      Assert.Equal(withThinkingFieldAbsent, AnthropicRequestBody.Build(request, "claude-test", ThinkingWireState.Omit));
    }

    [Fact]
    public void Disabled_SENDS_the_disable_rather_than_relying_on_silence()
    {
      // The other half of the re-scope: on a default-on family, inertness is NOT byte-identity - the
      // correct assertion is that the disable is present.
      var body = Body(Req(), ThinkingWireState.Disabled);
      Assert.Equal("disabled", body["thinking"]["type"].GetValue<string>());
      Assert.Null(body["output_config"]);   // no effort while disabled - it is rejected above `high`
    }

    [Fact]
    public void Adaptive_sends_the_adaptive_type_and_puts_effort_beside_it_not_inside_it()
    {
      var body = Body(Req(thinking: true, effort: "high"), ThinkingWireState.Adaptive);

      Assert.Equal("adaptive", body["thinking"]["type"].GetValue<string>());
      Assert.Null(body["thinking"]["effort"]);                                  // NOT nested
      Assert.Equal("high", body["output_config"]["effort"].GetValue<string>()); // a sibling
    }

    [Fact]
    public void Adaptive_without_an_effort_omits_output_config_entirely()
    {
      var body = Body(Req(thinking: true), ThinkingWireState.Adaptive);
      Assert.Equal("adaptive", body["thinking"]["type"].GetValue<string>());
      Assert.Null(body["output_config"]);
    }

    [Fact]
    public void The_retired_fixed_budget_shape_is_never_emitted()
    {
      // budget_tokens is a 400 on every family Dwight now ships, and it is also the shape that rejects
      // forced tool use - which would have broken the structured path outright.
      foreach (var state in new[] { ThinkingWireState.Omit, ThinkingWireState.Adaptive, ThinkingWireState.Disabled })
      {
        var json = AnthropicRequestBody.Build(Req(thinking: true, effort: "max"), "claude-test", state);
        Assert.DoesNotContain("budget_tokens", json);
        Assert.DoesNotContain("\"enabled\"", json);
      }
    }

    // ---- the sampling guard (#192c) -----------------------------------------------------------------

    [Fact]
    public void No_temperature_is_sent_when_the_product_leaves_it_unset()
    {
      // Sampling params are REMOVED on Opus 4.7+ and the 5-family: sending one is a 400. The product
      // never sets Temperature - this makes that a guard rather than an accident.
      foreach (var state in new[] { ThinkingWireState.Omit, ThinkingWireState.Adaptive, ThinkingWireState.Disabled })
      {
        var json = AnthropicRequestBody.Build(Req(thinking: true, effort: "high"), "claude-test", state);
        Assert.DoesNotContain("temperature", json);
      }
    }

    [Fact]
    public void An_explicit_temperature_still_rides_for_the_evals_pinned_model()
    {
      // The eval forces claude-sonnet-4-6, where temp 0 remains legal and every stamped baseline
      // depends on it (#188's eval pin). The guard above must not become a blanket strip.
      var r = Req();
      r.Temperature = 0;
      Assert.Contains("temperature", AnthropicRequestBody.Build(r, "claude-sonnet-4-6", ThinkingWireState.Omit));
    }

    // ---- forced tool choice coexists with adaptive thinking -------------------------------------

    [Fact]
    public void A_structured_request_still_forces_its_tool_with_adaptive_thinking_on()
    {
      // The compatibility that lets ADR-058's structured path and ADR-082's thinking coexist: forced
      // tool choice is rejected by the RETIRED fixed-budget mode, not by adaptive.
      var r = Req(thinking: true, effort: "high");
      r.StructuredName = "emit_plan";
      r.StructuredSchemaJson = "{\"type\":\"object\",\"properties\":{}}";

      var body = Body(r, ThinkingWireState.Adaptive);
      Assert.Equal("tool", body["tool_choice"]["type"].GetValue<string>());
      Assert.Equal("emit_plan", body["tool_choice"]["name"].GetValue<string>());
      Assert.Equal("adaptive", body["thinking"]["type"].GetValue<string>());
    }
  }
}
