using System;
using System.Collections.Generic;
using Dwight.Agent;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// U28 at the click, the panel half (wave-2 wiring). The panel hands Reorganise/Simplify the count the card NAMED and a
  /// closure that appends the verb's scope refusal through the REPORT door with ScriptEditReport's renderers. These hold
  /// the two facts that wiring rests on, GH-free:
  ///   - the sentence that reaches the thread on both channels is the verb's own refusal, under a lead that is true for a
  ///     reorganise or a simplify (not set_value's, which names the wrong kind);
  ///   - the opening notice is suppressed exactly when the verb will refuse - a seeded route with no surviving ids.
  /// What only a sitting can witness: that an approved card whose components were deleted before the click puts this
  /// line into the thread under "Yes, go ahead." as well as the toast, and that the next turn's model reads it.
  /// </summary>
  public class ScopeRefusedReportTests
  {
    [Theory]
    [InlineData("reorganise", 0)]
    [InlineData("reorganise", 3)]
    [InlineData("simplify", 1)]
    [InlineData("simplify", -1)]
    public void The_refusal_reaches_both_channels_verbatim_under_a_true_lead(string verb, int named)
    {
      var message = VerbScope.Resolve(seeded: true, new Guid[0], new[] { Guid.NewGuid() }, confirmedWhole: false,
                                      verb, named).Message;

      var html = ScriptEditReport.NotAppliedHtml(message);
      var text = ScriptEditReport.NotAppliedText(message);

      Assert.Contains("nothing was changed", message);
      Assert.Contains(System.Net.WebUtility.HtmlEncode(message.Trim()), html);
      Assert.StartsWith("[Not applied - the canvas is unchanged] ", text);
      Assert.EndsWith(message.Trim(), text);
      // The lead set_value's renderer would have put here is a claim about the wrong kind.
      Assert.DoesNotContain("set_value", text);
      Assert.DoesNotContain("script", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_seeded_route_refuses_exactly_when_nothing_survives_so_the_opening_notice_can_key_off_the_count()
    {
      // The panel posts "Opening Reorganise ..." only when onto.Count > 0, relying on this: seeded + empty = Refuse,
      // seeded + any = Seeded. If that ever changed, the notice would announce a run that does not happen (or hide one).
      Assert.Equal(ScopeOutcome.Refuse,
        VerbScope.Resolve(true, new List<Guid>(), new[] { Guid.NewGuid() }, true, "reorganise", 2).Outcome);
      Assert.Equal(ScopeOutcome.Seeded,
        VerbScope.Resolve(true, new List<Guid> { Guid.NewGuid() }, new Guid[0], false, "simplify", 1).Outcome);
    }
  }
}
