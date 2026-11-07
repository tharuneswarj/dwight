using System.Collections.Generic;
using Dwight.Simplify;
using Dwight.UI;
using Xunit;

namespace Dwight.Simplify.Tests
{
  /// <summary>RR8 headless cores. #15 fix A: the GH-free externalize-input DECISION (which internalized lever a
  /// nominated component externalizes) - the live reader filters candidates, this picks among them. #16: the
  /// title-transport nonce strip that stops a ticked set arriving empty. The GH-coupled halves - reading the live
  /// value/type/port, the all-or-nothing group drop, and the completeness guard - are runtime-verified at re-gate,
  /// the codebase pattern.</summary>
  public class Rr8Tests
  {
    // ---- #15 fix A: ExternalizeInputPicker (the sole-lever / ambiguity / name-preference decision) ----

    private static (string, string, string) Cand(string name) => (name, name, name);

    [Fact]
    public void The_sole_internalized_lever_is_picked_even_when_the_model_names_nothing()
    {
      // The real RR8 case: a Stream Filter has exactly one unwired numeric input (Gate). No name needed.
      var pick = ExternalizeInputPicker.Pick(new List<(string, string, string)> { Cand("Gate") }, null);
      Assert.Equal(0, pick);
    }

    [Fact]
    public void The_sole_lever_wins_even_when_the_model_names_a_non_candidate()
    {
      // Robustness over the model's string: it named "value" (matches nothing), but there is one lever -> use it,
      // never orphan. This is what turns a fragile name-match into a reliable resolution.
      var pick = ExternalizeInputPicker.Pick(new List<(string, string, string)> { Cand("Index") }, "value");
      Assert.Equal(0, pick);
    }

    [Fact]
    public void A_matching_name_picks_the_right_lever_among_several()
    {
      // Two numeric levers (e.g. Index + Count): the model's name disambiguates.
      var cands = new List<(string, string, string)> { Cand("Index"), Cand("Count") };
      Assert.Equal(1, ExternalizeInputPicker.Pick(cands, "Count"));
      Assert.Equal(0, ExternalizeInputPicker.Pick(cands, "index"));   // case-insensitive
    }

    [Fact]
    public void A_nickname_or_label_also_matches()
    {
      // label / name / nick are all accepted (the payload may surface any of them).
      var cands = new List<(string, string, string)> { ("i", "Index", "i"), ("W", "Wrap", "W") };
      Assert.Equal(0, ExternalizeInputPicker.Pick(cands, "i"));
    }

    [Fact]
    public void Ambiguous_with_no_name_match_drops_rather_than_guess()
    {
      // Two levers, no usable name -> -1 so the caller skips the whole change-group honestly (fix B), never
      // externalizing the wrong input.
      var cands = new List<(string, string, string)> { Cand("Index"), Cand("Count") };
      Assert.Equal(-1, ExternalizeInputPicker.Pick(cands, null));
      Assert.Equal(-1, ExternalizeInputPicker.Pick(cands, "nonesuch"));
    }

    [Fact]
    public void No_lever_drops()
    {
      Assert.Equal(-1, ExternalizeInputPicker.Pick(new List<(string, string, string)>(), "Gate"));
      Assert.Equal(-1, ExternalizeInputPicker.Pick(null, "Gate"));
    }

    // ---- #16: StripTitleNonce (the intermittent "Nothing selected") ----

    [Fact]
    public void A_payload_command_survives_the_title_nonce_strip()
    {
      // The bug: "simplifyapply:1,2,3" + ":<Date.now()>" was truncated by Split(':')[0] to "simplifyapply" - the
      // ticked set lost. Strip ONLY the trailing digit nonce; the inner colons and the payload survive.
      Assert.Equal("simplifyapply:1,2,3", PanelTransport.StripTitleNonce("simplifyapply:1,2,3:1720000000000"));
      Assert.Equal("moveto:100:200", PanelTransport.StripTitleNonce("moveto:100:200:1720000000000"));
      Assert.Equal("arrange:grid", PanelTransport.StripTitleNonce("arrange:grid:42"));
    }

    [Fact]
    public void A_bare_command_still_loses_only_its_nonce()
    {
      Assert.Equal("close", PanelTransport.StripTitleNonce("close:1720000000000"));
    }

    [Fact]
    public void A_command_with_no_nonce_is_untouched()
    {
      // Defensive: a non-digit tail is real payload, not a nonce - never strip it.
      Assert.Equal("simplify:better", PanelTransport.StripTitleNonce("simplify:better"));
      Assert.Equal("arrange:grid", PanelTransport.StripTitleNonce("arrange:grid"));
      Assert.Equal("close", PanelTransport.StripTitleNonce("close"));
    }
  }
}
