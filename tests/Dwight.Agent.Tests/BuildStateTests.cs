using System.Linq;
using System.Text.Json.Nodes;
using Dwight.Agent;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// #377 - THE INITIAL STATE OF A PLACED COMPONENT, and the refusals that let its verdict be an equality.
  ///
  /// THE DEFECT, from the WP-4 gate's first real build: every region built, every guard passed, and the
  /// definition solved to nothing - because a placed Number Slider is born at Grasshopper's factory
  /// setting (domain 0 to 1, value 0.250, three decimals). The build was verified and useless. "Placed and
  /// wired" was never the same claim as "ready to compute", and only the second one is what a user means.
  ///
  /// WHY THESE PARTICULAR REFUSALS EXIST. Each was earned by instrumenting the live slider API rather than
  /// reading it, and each one is a case where GRASSHOPPER'S OWN ANSWER IS TO ACCEPT IT IN SILENCE:
  ///   - min above max            -> accepted; the slider is dead and cannot be moved
  ///   - value outside the domain -> clamped without a word (250 into 0..10 lands on 10)
  ///   - value off the grid       -> snapped without a word (7.6 under Integer becomes 8)
  ///
  /// The third is the one that decides the verdict's shape (#383). Refusing off-grid declarations here
  /// means Grasshopper has nothing left to snap by the time the value is applied - so the read-back can be
  /// required to match EXACTLY, and any difference at all is a real defect rather than an expected
  /// rounding. Integrality and parity are properties of the number the model wrote down; checking them
  /// borrows nothing from Grasshopper's rounding rules, so #137 is not in play.
  /// </summary>
  public class BuildStateTests
  {
    /// <summary>
    /// Built by PARSING JSON TEXT, never by assembling JsonValue objects by hand - because parsed JSON is
    /// what actually arrives from a provider, and a hand-built node is backed differently. The first run of
    /// this file failed 19 of 19 on exactly that: the instrument was testing a shape its subject never
    /// sees. Numbers are passed as literal text for the same reason - so a decimal declaration is the
    /// exact one written down, with no float noise between the test and the claim.
    /// </summary>
    private static JsonNode Slider(string min, string max, string value, string rounding = null)
    {
      var json = "{\"min\":" + min + ",\"max\":" + max + ",\"value\":" + value +
                 (rounding == null ? "" : ",\"rounding\":\"" + rounding + "\"") + "}";
      return JsonNode.Parse(json);
    }

    private static bool Parse(JsonNode state, string type, out BuildState parsed, out ToolResult error) =>
      BuildStateParser.TryParse(state, type, "s1", out parsed, out error);

    // ---- the ordinary case ---------------------------------------------------------------------------

    [Fact]
    public void A_component_with_no_state_is_not_an_error()
    {
      Assert.True(Parse(null, "Number Slider", out var parsed, out var error));
      Assert.Null(parsed);
      Assert.Null(error);
    }

    [Fact]
    public void A_whole_slider_declaration_is_carried_through()
    {
      Assert.True(Parse(Slider("0", "100", "40", "integer"), "Number Slider", out var s, out _));
      Assert.Equal(BuildStateKind.Slider, s.Kind);
      Assert.Equal(0m, s.Min);
      Assert.Equal(100m, s.Max);
      Assert.Equal(40m, s.Value);
      Assert.Equal(SliderRounding.Integer, s.Rounding);
    }

    [Fact]
    public void Rounding_defaults_to_float_when_it_is_not_declared()
    {
      Assert.True(Parse(Slider("0", "10", "2.5"), "Number Slider", out var s, out _));
      Assert.Equal(SliderRounding.Float, s.Rounding);
    }

    // ---- the plugin computes the presentation (ADR-056 / #146) ---------------------------------------

    /// <summary>
    /// The model never sends a decimal-place count: it is derivable from the numbers it already sent, and
    /// a deterministic plugin-computable quantity is never a billed round-trip.
    ///
    /// This is ALSO what makes a float slider on-grid by construction. Grasshopper snaps a float slider to
    /// its decimal places, so a precision chosen independently of the value would re-introduce the exact
    /// silent snap these refusals exist to prevent - 2.567 at two places becomes 2.57. Derived from the
    /// declaration, the declaration is always representable.
    /// </summary>
    [Theory]
    [InlineData("2.567", 3)]
    [InlineData("0.25", 2)]
    [InlineData("40", 0)]
    public void Decimal_places_are_derived_from_the_declaration_never_sent(string value, int expected)
    {
      Assert.True(Parse(Slider("0", "1000", value), "Number Slider", out var s, out _));
      Assert.Equal(expected, s.DecimalPlaces);
    }

    [Fact]
    public void A_whole_number_accuracy_forces_zero_decimal_places()
    {
      Assert.True(Parse(Slider("0", "100", "40", "integer"), "Number Slider", out var s, out _));
      Assert.Equal(0, s.DecimalPlaces);
    }

    // ---- the three silent-acceptance refusals --------------------------------------------------------

    [Fact]
    public void An_inverted_domain_is_refused_because_Grasshopper_would_accept_it_and_be_dead()
    {
      Assert.False(Parse(Slider("100", "0", "50"), "Number Slider", out _, out var error));
      Assert.Equal(ToolErrors.InvalidArgs, error.Code);
      Assert.Contains("above its maximum", error.Message);
    }

    [Fact]
    public void A_value_outside_the_domain_is_refused_because_Grasshopper_would_clamp_it_silently()
    {
      Assert.False(Parse(Slider("0", "10", "250"), "Number Slider", out _, out var error));
      Assert.Contains("outside its domain", error.Message);
    }

    /// <summary>#383's ruling, and the reason the verdict can demand equality. The refusal NAMES the two
    /// neighbours, so it repairs rather than blocks (ADR-092).</summary>
    [Fact]
    public void An_off_grid_integer_is_refused_and_the_refusal_says_what_to_declare_instead()
    {
      Assert.False(Parse(Slider("0", "100", "7.6", "integer"), "Number Slider", out _, out var error));
      Assert.Contains("7.6 is not an integer", error.Message);
      Assert.Contains("declare 7 or 8", error.Message);
    }

    [Fact]
    public void An_odd_value_on_an_even_slider_is_refused_with_both_neighbours()
    {
      Assert.False(Parse(Slider("0", "100", "7", "even"), "Number Slider", out _, out var error));
      Assert.Contains("7 is not an even number", error.Message);
      Assert.Contains("declare 6 or 8", error.Message);
    }

    [Fact]
    public void An_even_value_on_an_odd_slider_is_refused_with_both_neighbours()
    {
      // #668: an odd slider's bounds must be odd too, so this fixture's domain is 1..99 (it was 0..100).
      Assert.False(Parse(Slider("1", "99", "8", "odd"), "Number Slider", out _, out var error));
      Assert.Contains("8 is not an odd number", error.Message);
      Assert.Contains("declare 7 or 9", error.Message);
    }

    [Theory]
    [InlineData("0", "100", "40", "integer")]
    [InlineData("0", "100", "6", "even")]
    [InlineData("1", "99", "9", "odd")]   // #668: odd bounds for an odd slider (was 0..100)
    [InlineData("0", "100", "7.625", null)]
    public void An_on_grid_value_passes(string min, string max, string value, string rounding)
    {
      Assert.True(Parse(Slider(min, max, value, rounding), "Number Slider", out _, out var error));
      Assert.Null(error);
    }

    // ---- #658(1)/#668: an Even or Odd slider's BOUNDS must be on parity too ---------------------------

    /// <summary>
    /// THE FINDING (#658). An even slider declared 1 to 99 landed as 0 to 98 on the authoritative path, and the equality
    /// check reverted it. So an off-parity bound is a PARSE-TIME refusal naming both legal neighbours - a deterministic
    /// failure class the plugin can compute is never a billed revert (#146). #668: the refusal claims NO mapping; it names
    /// the neighbours, which are true whatever Grasshopper does.
    /// </summary>
    [Theory]
    [InlineData("1", "98", "even", "minimum 1 is not an even number", "declare 0 or 2")]
    [InlineData("0", "99", "even", "maximum 99 is not an even number", "declare 98 or 100")]
    [InlineData("0", "99", "odd", "minimum 0 is not an odd number", "declare -1 or 1")]
    [InlineData("1", "100", "odd", "maximum 100 is not an odd number", "declare 99 or 101")]
    public void An_off_parity_bound_is_refused_with_both_neighbours(string min, string max, string rounding,
                                                                    string says, string repair)
    {
      var value = rounding == "even" ? "50" : "51";

      Assert.False(Parse(Slider(min, max, value, rounding), "Number Slider", out _, out var error));

      Assert.Equal(ToolErrors.InvalidArgs, error.Code);
      Assert.Contains("The slider's " + says, error.Message);
      Assert.Contains(repair, error.Message);
      Assert.DoesNotContain("lands", error.Message);   // no mapping claimed (#668)
    }

    /// <summary>The field's exact declaration: both bounds are off, and the MINIMUM is named first.</summary>
    [Fact]
    public void The_field_declaration_even_1_to_99_is_refused_before_anything_is_built()
    {
      Assert.False(Parse(Slider("1", "99", "50", "even"), "Number Slider", out _, out var error));
      Assert.Contains("minimum 1 is not an even number", error.Message);
      Assert.Contains("declare 0 or 2", error.Message);
    }

    [Fact]
    public void A_negative_off_parity_bound_names_its_neighbours_on_both_sides()
    {
      Assert.False(Parse(Slider("-1", "10", "4", "even"), "Number Slider", out _, out var error));
      Assert.Contains("minimum -1 is not an even number", error.Message);
      Assert.Contains("declare -2 or 0", error.Message);
    }

    [Fact]
    public void A_fractional_bound_on_an_even_slider_is_refused_too()
    {
      Assert.False(Parse(Slider("0.5", "10", "4", "even"), "Number Slider", out _, out var error));
      Assert.Contains("minimum 0.5 is not an even number", error.Message);
    }

    /// <summary>Float and Integer sliders are untouched by this rule. The Integer row carries a FRACTIONAL bound so it
    /// witnesses the scope (a rule that reached Integer would refuse 0.5); whether Integer should refuse it is ledgered,
    /// not in this unit (#668). The Float row is a pin only: every Float bound is on grid by construction.</summary>
    [Theory]
    [InlineData("1", "99", "50", null)]
    [InlineData("0.5", "99", "50", "integer")]
    public void Parity_bounds_do_not_apply_to_float_or_integer_sliders(string min, string max, string value, string rounding)
    {
      Assert.True(Parse(Slider(min, max, value, rounding), "Number Slider", out _, out var error));
      Assert.Null(error);
    }

    // ---- the vocabulary boundary (#208) -------------------------------------------------------------

    /// <summary>
    /// A state the plugin would IGNORE must never be silently accepted. That is the #208 class arriving in
    /// a new field: the model sets something, nothing happens, and the verdict still says the build landed
    /// as specified. The refusal carries the list of types that do take one.
    /// </summary>
    [Fact]
    public void A_type_that_takes_no_state_refuses_one_rather_than_ignoring_it()
    {
      Assert.False(Parse(Slider("0", "10", "5"), "Circle", out _, out var error));
      Assert.Contains("does not take a \"state\"", error.Message);
      Assert.Contains("Number Slider", error.Detail["typesThatAcceptState"].ToJsonString());
    }

    [Fact]
    public void Slider_fields_on_a_Panel_are_refused()
    {
      Assert.False(Parse(Slider("0", "10", "5"), "Panel", out _, out var error));
      Assert.Contains("takes only \"text\"", error.Message);
    }

    [Fact]
    public void Panel_text_on_a_slider_is_refused()
    {
      Assert.False(Parse(new JsonObject { ["text"] = "hello" }, "Number Slider", out _, out var error));
      Assert.Contains("not \"text\"", error.Message);
    }

    [Fact]
    public void A_panel_carries_its_text()
    {
      Assert.True(Parse(new JsonObject { ["text"] = "notes" }, "Panel", out var s, out _));
      Assert.Equal(BuildStateKind.PanelText, s.Kind);
      Assert.Equal("notes", s.Text);
    }

    // ---- a domain is not a thing you can declare half of ---------------------------------------------

    /// <summary>Half a domain would leave the other half at the factory 0-to-1 - which is the very defect
    /// this unit closes, arriving through a partially-filled field instead of an empty one.</summary>
    [Theory]
    [InlineData("min")]
    [InlineData("max")]
    [InlineData("value")]
    public void A_partial_slider_declaration_is_refused(string missing)
    {
      var state = (JsonObject)Slider("0", "100", "40");
      state.Remove(missing);

      Assert.False(Parse(state, "Number Slider", out _, out var error));
      Assert.Contains(missing, error.Message);
    }

    [Fact]
    public void An_unknown_rounding_is_refused_with_the_real_list()
    {
      Assert.False(Parse(Slider("0", "10", "5", "logarithmic"), "Number Slider", out _, out var error));
      Assert.Contains("not a slider rounding", error.Message);
      Assert.Contains("integer", error.Detail["available"].ToJsonString());
    }

    /// <summary>A precision finer than a slider can hold would be silently rounded, which is the class
    /// every refusal above exists to prevent - so it is refused rather than accepted and lost.</summary>
    [Fact]
    public void A_value_finer_than_a_slider_can_hold_is_refused()
    {
      Assert.False(Parse(Slider("0", "1", "0.12345678"), "Number Slider", out _, out var error));
      Assert.Contains("decimal places", error.Message);
    }

    // ---- the boundary itself (#377: own-ledger only) -------------------------------------------------

    /// <summary>
    /// The capability boundary, asserted as a property of the SHAPE rather than as a promise: state rides
    /// on a component the spec is CREATING, so there is no field through which an existing component could
    /// be named. Editing what the user already has is a different capability (set_value), homed to #340.
    /// </summary>
    [Fact]
    public void State_can_only_be_declared_on_a_component_being_created()
    {
      var fields = typeof(BuildComponent).GetProperties().Select(p => p.Name).ToList();
      Assert.Contains("State", fields);
      Assert.DoesNotContain("Id", fields);        // nothing here can name an existing component
      Assert.DoesNotContain("Guid", fields);
    }
  }
}
