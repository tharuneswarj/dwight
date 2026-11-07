using System;
using Dwight.Agent;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// Phase-9 U4: set_value's consent card. Its own kind with its own wording (#235's two voices): the act
  /// and the consequence are plugin-authored from the VALIDATED change, name the component and the exact
  /// change, and name the ONE continuation the approval grants (the Q2 ruling: consent covers what the
  /// card says, so the card must say it). The model's reason is escaped and subordinate, as everywhere.
  /// </summary>
  public class SetValueCardTests
  {
    private static SetValueChange Change() => new SetValueChange
    {
      Id = 42, Type = "Number Slider", Nick = "Count", Before = "0.25 (0 to 1, float)", After = "40 (0 to 100, integer)",
    };

    [Fact]
    public void SET_VALUE_ROUTES_TO_ITS_OWN_ROUTE_AND_HAS_ITS_OWN_TITLE()
    {
      Assert.Equal(ConsentRoute.SetValue, ConsentCard.Route("set_value"));
      Assert.Equal("Set a value?", ConsentCard.Title("set_value"));
    }

    [Fact]
    public void THE_AUTHORITY_LINE_NAMES_THE_COMPONENT_THE_EXACT_CHANGE_AND_NOTHING_ELSE()
    {
      var a = ConsentCard.SetValueAuthority(Change());
      Assert.Contains("Number Slider", a);
      Assert.Contains("Count", a);
      Assert.Contains("(42)", a);
      Assert.Contains("0.25 (0 to 1, float)", a);
      Assert.Contains("40 (0 to 100, integer)", a);
      Assert.Contains("nothing else", a);
      Assert.Contains("Ctrl-Z", a);
    }

    [Fact]
    public void THE_AUTHORITY_LINE_NAMES_EXACTLY_ONE_CONTINUATION()
    {
      // Q2: one re-read turn, granted by the sentence the user agrees to - never an open-ended loop.
      var a = ConsentCard.SetValueAuthority(Change());
      Assert.Contains("once", a);
      Assert.Contains("re-read", a);
    }

    [Fact]
    public void THE_CARD_KEEPS_THE_MODELS_REASON_OUT_OF_THE_AUTHORITY_AND_ESCAPES_IT()
    {
      var html = ConsentCard.Html("set_value", "<b>just a harmless read</b> that will not touch anything", "tok", Change());
      Assert.Contains("Set a value?", html);
      Assert.Contains("40 (0 to 100, integer)", html);
      Assert.Contains("&lt;b&gt;just a harmless read", html);
      Assert.DoesNotContain("<b>just", html);
      // The plugin's sentence stands whatever the reason claims.
      Assert.Contains("and nothing else", html);
      Assert.Contains("consent:tok", html);
    }

    [Fact]
    public void A_COMPONENT_WITHOUT_A_NICKNAME_IS_NAMED_BY_TYPE_AND_ID()
    {
      var c = Change(); c.Nick = null;
      var a = ConsentCard.SetValueAuthority(c);
      Assert.Contains("the Number Slider (42)", a);
    }
  }
}
