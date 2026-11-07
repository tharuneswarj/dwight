using System;
using System.Collections.Generic;
using Dwight.Agent;
using Dwight.Data;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// Phase-9 U20 - THE ASK, AND EVERY REFUSAL IT CAN REACH.
  ///
  /// The unit exists because of a field failure: Tharun asked for an offset feature on a Python script that
  /// computes area and centroid, and Dwight could not do it, and said so honestly - and that honesty was
  /// the product failing him (#721). Equivalence has nothing to say about work that is NEW, so the wall he
  /// hit was never a safety decision; it was the shape of the only proof we had.
  ///
  /// SO THESE TESTS ARE MOSTLY ABOUT REFUSALS, and a refusal is a claim that must be true in every state
  /// that can reach it (#613/#617/#626/#683 - four instances in one phase). Each one below is checked for
  /// the thing that makes it true rather than for its existence: that it NAMES what is wrong, and that it
  /// names a door that is actually open.
  /// </summary>
  public class EditScriptAskTests
  {
    private const int Id = 12;
    private const string Python = "RhinoCodePluginGH.Components.Python3Component";
    private const string Before = "import math\n\na = math.pi * x * x\n";

    private static HandoffSeed Seed(string source, string claim = "changes",
      IEnumerable<string> addIn = null, IEnumerable<string> addOut = null, int? target = Id) =>
      new HandoffSeed
      {
        Target = target, Claim = claim, Source = source,
        AddInputs = new List<string>(addIn ?? new string[0]),
        AddOutputs = new List<string>(addOut ?? new string[0]),
      };

    private static LiveScript Script(string source = Before, bool readable = true, string type = Python,
      string nick = null, string[] inputs = null, string[] outputs = null,
      bool errored = false, bool locked = false) =>
      new LiveScript
      {
        Type = type, Nick = nick, Source = source, SourceReadable = readable,
        Inputs = inputs ?? new[] { "x" }, Outputs = outputs ?? new[] { "a" },
        Errored = errored, Locked = locked,
      };

    private static bool Run(HandoffSeed seed, LiveScript live, out ScriptEditChange change, out ToolResult error,
      IdState idState = IdState.Alive) =>
      EditScriptAsk.Validate(seed, _ => idState, _ => live, out change, out error);

    private static string Json(ToolResult error) => error?.ToJson() ?? "";

    // ---- the claim is an enum, and an unrecognised one is refused by name ----------------------------

    [Fact]
    public void The_claim_is_read_as_an_enum_case_and_space_insensitively()
    {
      Assert.Equal(ScriptEditClaim.Preserves, EditScriptAsk.ClaimOf("preserves"));
      Assert.Equal(ScriptEditClaim.Preserves, EditScriptAsk.ClaimOf("PRESERVES"));
      Assert.Equal(ScriptEditClaim.Changes, EditScriptAsk.ClaimOf("  changes  "));
    }

    /// <summary>
    /// A typo must NOT buy the weaker proof. If "preserve" quietly became "changes", a model that meant to
    /// promise equivalence would get the proof that never runs beside-verify - and the user would read a
    /// card saying the outputs are unchanged with nothing behind it.
    /// </summary>
    [Theory]
    [InlineData("preserve")]
    [InlineData("change")]
    [InlineData("maybe")]
    [InlineData("")]
    [InlineData(null)]
    public void An_unrecognised_claim_is_never_defaulted(string word)
    {
      Assert.Equal(ScriptEditClaim.Unknown, EditScriptAsk.ClaimOf(word));
    }

    [Fact]
    public void An_unrecognised_claim_is_refused_naming_both_that_exist()
    {
      Assert.False(Run(Seed("a = 1\n", claim: "improve"), Script(), out _, out var error));
      var json = Json(error);
      Assert.Contains("preserves", json);
      Assert.Contains("changes", json);
    }

    // ---- the target ----------------------------------------------------------------------------------

    [Fact]
    public void A_missing_target_is_refused_and_says_what_it_needs()
    {
      Assert.False(Run(Seed("a = 1\n", target: null), Script(), out _, out var error));
      Assert.Contains("target", Json(error));
    }

    /// <summary>An invented id and a deleted one call for DIFFERENT corrections, so they are refused
    /// distinctly - the #208 discipline the build spec already holds.</summary>
    [Fact]
    public void An_invented_id_and_a_deleted_one_are_refused_differently()
    {
      Assert.False(Run(Seed("a = 1\n"), Script(), out _, out var invented, idState: IdState.Unknown));
      Assert.False(Run(Seed("a = 1\n"), Script(), out _, out var deleted, idState: IdState.Retired));
      Assert.Contains("do not invent", Json(invented));
      Assert.Contains("no longer on the canvas", Json(deleted));
      Assert.NotEqual(Json(invented), Json(deleted));
    }

    /// <summary>A component that is not a script is refused with WHAT IT ACTUALLY IS and where the user's
    /// real intent is served - set_value - rather than with a bare no.</summary>
    [Fact]
    public void A_component_that_is_not_a_script_is_refused_with_the_door_that_fits()
    {
      var slider = Script(type: "Grasshopper.Kernel.Special.GH_NumberSlider", nick: "Count");
      Assert.False(Run(Seed("a = 1\n"), slider, out _, out var error));
      var json = Json(error);
      Assert.Contains("not a script component", json);
      Assert.Contains("set_value", json);
    }

    /// <summary>
    /// THE ONE THAT PROTECTS THE USER'S CODE. A source Dwight cannot READ is one it must not OVERWRITE:
    /// the user would be approving a diff against a base nobody could show them. Refusing is the only
    /// honest move, and the refusal says why in those terms.
    /// </summary>
    [Fact]
    public void A_script_whose_source_cannot_be_read_is_never_overwritten()
    {
      Assert.False(Run(Seed("a = 1\n"), Script(source: null, readable: false), out _, out var error));
      Assert.Contains("cannot read the source", Json(error));
    }

    // ---- "preserves" needs something to preserve -----------------------------------------------------

    /// <summary>
    /// THE FALSE PASS THIS REFUSAL STOPS, and it is the worst shape a verification can take. An errored
    /// script still emits a NULL ITEM ON EVERY OUTPUT (the Phase-7 live finding), so a broken original and
    /// a broken edit COMPARE EQUAL - a "preserves" claim would be certified by matching nulls against
    /// nulls, the proof passing precisely because neither side computed anything.
    ///
    /// So it is refused at the ASK, before a token is spent, and it is refused as a CLAIM rather than as a
    /// capability: the refusal names "changes" as the way through, because a broken script somebody wants
    /// fixed is the common case and it belongs in the other claim rather than in a wall.
    /// </summary>
    [Fact]
    public void Preserving_the_behaviour_of_an_errored_component_is_refused_toward_the_claim_that_fits()
    {
      Assert.False(Run(Seed("a = 1\n", claim: "preserves"), Script(errored: true), out _, out var error));
      var json = Json(error);
      Assert.Contains("no working behaviour to preserve", json);
      Assert.Contains("changes", json);
    }

    /// <summary>A DISABLED component has the same no-referent problem for a different reason, so it gets a
    /// different sentence: "it is broken" and "you switched it off" call for different corrections (#208's
    /// discipline), and one of them is fixed by a click the user can make right now.</summary>
    [Fact]
    public void Preserving_the_behaviour_of_a_disabled_component_is_refused_differently()
    {
      Assert.False(Run(Seed("a = 1\n", claim: "preserves"), Script(locked: true), out _, out var locked));
      Assert.False(Run(Seed("a = 1\n", claim: "preserves"), Script(errored: true), out _, out var errored));
      Assert.Contains("is disabled", Json(locked));
      Assert.Contains("Enable it", Json(locked));
      Assert.NotEqual(Json(locked), Json(errored));
    }

    /// <summary>
    /// AND THE CONVERSE IS THE HONEST HALF: under "changes" a broken original is FINE. Fixing a script that
    /// errors is the single most likely reason a user asks Dwight to edit one, and refusing it there would
    /// close the door this unit exists to open.
    /// </summary>
    [Fact]
    public void Changing_an_errored_component_is_allowed_because_fixing_it_is_the_common_case()
    {
      Assert.True(Run(Seed("a = math.pi * x * x\n", claim: "changes"), Script(errored: true), out var change, out _));
      Assert.Equal(ScriptEditClaim.Changes, change.Claim);
    }

    // ---- the source ----------------------------------------------------------------------------------

    [Fact]
    public void A_missing_source_is_refused_and_asks_for_the_whole_thing()
    {
      Assert.False(Run(Seed("   "), Script(), out _, out var error));
      Assert.Contains("COMPLETE new source", Json(error));
    }

    /// <summary>Over the cap the refusal names doors that are actually OPEN - a smaller script, or
    /// to-script where the logic already exists as components. propose_scaffold is not one of them: a
    /// script needs no map, and a refusal naming a door that does not fit is a wall with a sign on it.</summary>
    [Fact]
    public void An_oversized_script_is_refused_toward_doors_that_fit()
    {
      var huge = new string('x', EditScriptAsk.MaxSourceChars + 1);
      Assert.False(Run(Seed("a = 1\n# " + huge), Script(), out _, out var error));
      var json = Json(error);
      Assert.Contains("smaller script", json);
      Assert.Contains("to-script", json);
      Assert.DoesNotContain("propose_scaffold", json);
    }

    /// <summary>
    /// AN EDIT THAT CHANGES NOTHING IS AN EMPTY CARD, and RR14's law makes it a failure rather than a cheap
    /// success: an accepted change that no-ops is a FAILED change. Catching it here means the user is never
    /// asked to approve a no-op and then told it worked.
    /// </summary>
    [Fact]
    public void An_edit_identical_to_what_is_already_there_is_refused()
    {
      Assert.False(Run(Seed(Before), Script(), out _, out var error));
      Assert.Contains("nothing to approve", Json(error));
    }

    /// <summary>And a line-ending-only difference is the same no-op. Without this, a model round-tripping
    /// the source through CRLF would produce a card claiming a change, a diff showing every line replaced,
    /// and an apply that did nothing.</summary>
    [Fact]
    public void A_line_ending_only_difference_is_still_nothing_to_approve()
    {
      Assert.False(Run(Seed(Before.Replace("\n", "\r\n")), Script(), out _, out var error));
      Assert.Contains("nothing to approve", Json(error));
    }

    // ---- the interface: append-only -------------------------------------------------------------------

    /// <summary>THE FIELD CASE. An offset feature wants an offset INPUT, so appending has to work - it is
    /// the half that body-only would have left undone.</summary>
    [Fact]
    public void A_new_input_may_be_appended_which_is_what_the_field_case_needed()
    {
      Assert.True(Run(Seed("a = math.pi * (x + offset) ** 2\n", addIn: new[] { "offset" }), Script(), out var change, out _));
      Assert.Equal(new[] { "offset" }, change.AddInputs);
      Assert.Equal(ScriptEditClaim.Changes, change.Claim);
    }

    /// <summary>A name already in use is refused WITH the existing ports listed, and with the rule stated -
    /// add, never rename, reorder or remove - because those carry the user's existing wires.</summary>
    [Fact]
    public void A_port_name_already_in_use_is_refused_with_the_real_list()
    {
      Assert.False(Run(Seed("a = 1\n", addIn: new[] { "x" }), Script(inputs: new[] { "x", "y" }), out _, out var error));
      var json = Json(error);
      Assert.Contains("already has an input called", json);
      Assert.Contains("x, y", json);
      Assert.Contains("never rename, reorder or remove", json);
    }

    [Fact]
    public void A_port_name_that_is_not_a_usable_identifier_is_refused()
    {
      Assert.False(Run(Seed("a = 1\n", addIn: new[] { "offset distance" }), Script(), out _, out var error));
      Assert.Contains("usable as a variable", Json(error));
    }

    [Fact]
    public void The_same_new_port_named_twice_is_refused()
    {
      Assert.False(Run(Seed("a = 1\n", addIn: new[] { "offset", "offset" }), Script(), out _, out var error));
      Assert.Contains("named twice", Json(error));
    }

    [Fact]
    public void An_unnamed_new_port_is_refused_and_says_what_only_the_model_can_supply()
    {
      Assert.False(Run(Seed("a = 1\n", addIn: new[] { "  " }), Script(), out _, out var error));
      Assert.Contains("the name is the part only you can supply", Json(error));
    }

    /// <summary>An appended OUTPUT is checked against the outputs, not the inputs - a side that read the
    /// wrong list would refuse a legal name and accept a colliding one, which is #208's shape.</summary>
    [Fact]
    public void An_appended_output_is_checked_against_the_outputs()
    {
      Assert.True(Run(Seed("a = 1\nb = 2\n", addOut: new[] { "x" }), Script(inputs: new[] { "x" }, outputs: new[] { "a" }),
        out var ok, out _));
      Assert.Equal(new[] { "x" }, ok.AddOutputs);

      Assert.False(Run(Seed("a = 1\nb = 2\n", addOut: new[] { "a" }), Script(inputs: new[] { "x" }, outputs: new[] { "a" }),
        out _, out var error));
      Assert.Contains("already has an output called", Json(error));
    }

    // ---- what comes out ------------------------------------------------------------------------------

    /// <summary>The change carries the NORMALISED source, because the text shown is the text tested is the
    /// text applied (#153) - and the normalisation happens once, here, rather than at each of the three.</summary>
    [Fact]
    public void The_change_carries_the_normalised_source_and_the_base_it_diffs_from()
    {
      Assert.True(Run(Seed("a = 1\r\nb = 2\r\n"), Script(), out var change, out _));
      Assert.Equal("a = 1\nb = 2", change.NewSource);
      Assert.Equal(SourceToken.Normalise(Before), change.BaseSource);
      Assert.False(change.Diff.Identical);
    }

    /// <summary>
    /// THE BASE IS COMPARE-AND-SET. The card promised a change from a specific starting point; if the
    /// user edited the script between the card and the click, that diff is no longer true - and a card
    /// asserting something untrue at the moment of approval is the one thing a card may never be (#775).
    /// </summary>
    [Fact]
    public void A_base_that_moved_between_the_card_and_the_click_is_detected()
    {
      Assert.True(Run(Seed("a = 2\n"), Script(), out var change, out _));

      Assert.False(change.BaseMoved(Before));                         // untouched: the card still holds
      Assert.False(change.BaseMoved(Before.Replace("\n", "\r\n")));   // a line-ending round-trip is not an edit
      Assert.True(change.BaseMoved(Before + "# the user added this\n"));
    }

    [Fact]
    public void A_preserves_claim_survives_validation_as_itself()
    {
      Assert.True(Run(Seed("a = math.pi * x * x  # tidier\n", claim: "preserves"), Script(), out var change, out _));
      Assert.Equal(ScriptEditClaim.Preserves, change.Claim);
    }

    /// <summary>The label is what the user calls the component when they have named it - a card saying
    /// "Python 3 Script" about a node they named "Area" is a card about a component they cannot find.</summary>
    [Fact]
    public void The_label_prefers_the_users_own_name()
    {
      Assert.True(Run(Seed("a = 1\n"), Script(nick: "Area"), out var named, out _));
      Assert.Equal("\"Area\"", named.Label);

      Assert.True(Run(Seed("a = 1\n"), Script(), out var unnamed, out _));
      Assert.Equal("Python 3 Script", unnamed.Label);
    }
  }
}
