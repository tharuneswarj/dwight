using System;
using System.Collections.Generic;
using Dwight.Agent;
using Dwight.Data;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// #680: TWO INSTRUMENTS BORROWED A TOKEN MADE FOR A DIFFERENT QUESTION.
  ///
  /// The behaviour fingerprint's slider token is the VALUE only, and that is right for its question - a domain or rounding
  /// change that leaves the value where it was changes no output. The as-left record ("has anything about this component
  /// changed since Dwight left it?") and the canvas stamp ("has anything on the canvas changed?") borrowed it, so a
  /// domain-only edit was invisible to both: the rebuild would remove a slider the user had re-ranged, the canvas fact
  /// stayed silent, and the breaker read "nothing changed" after a change that landed. Each now reads the WHOLE held state
  /// itself; the fingerprint is untouched (so no test here touches it).
  /// </summary>
  public class HeldStateInstrumentTests
  {
    private static readonly Guid SliderGuid = new Guid("55555555-0000-0000-0000-000000000001");

    private static BuildState Slider(decimal min, decimal max, decimal value, SliderRounding rounding = SliderRounding.Integer,
                                     int dp = 0) =>
      new BuildState { Kind = BuildStateKind.Slider, Min = min, Max = max, Value = value, Rounding = rounding, DecimalPlaces = dp };

    /// <summary>The fingerprint's view of a slider: its value, and nothing else.</summary>
    private static FpObject Fp(decimal value) =>
      new FpObject { Guid = SliderGuid, TypeName = "Number Slider", ValueToken = "slider:" + value };

    // ---- the token ----------------------------------------------------------------------------------------

    [Fact]
    public void The_token_carries_the_whole_slider_state_and_a_panels_text()
    {
      Assert.Equal("slider:0..10@4/Integer/dp0", StateToken.Of(Slider(0, 10, 4)));
      Assert.Equal("panel:hello", StateToken.Of(new BuildState { Kind = BuildStateKind.PanelText, Text = "hello" }));
      Assert.Null(StateToken.Of(null));
    }

    /// <summary>The same state read twice is the same token, whatever scale the reader returned (0.250 and 0.25).</summary>
    [Fact]
    public void The_token_ignores_trailing_zeros()
    {
      Assert.Equal(StateToken.Of(Slider(0.0m, 1.00m, 0.250m, SliderRounding.Float, 3)),
                   StateToken.Of(Slider(0m, 1m, 0.25m, SliderRounding.Float, 3)));
    }

    // ---- the as-left record ---------------------------------------------------------------------------------

    private static BuildState StateOnlyChange(string what) =>
      what == "domain" ? Slider(0, 20, 4)
      : what == "rounding" ? Slider(0, 10, 4, SliderRounding.Even)
      : Slider(0, 10, 4, SliderRounding.Integer, 2);   // decimals

    /// <summary>THE MISS: the value is unchanged, so the fingerprint's token is identical - and the record still sees it.</summary>
    [Theory]
    [InlineData("domain")]
    [InlineData("rounding")]
    [InlineData("decimals")]
    public void A_state_only_change_reads_as_changed_in_the_as_left_record(string what)
    {
      var asLeft = AsLeftRecord.Of(Fp(4), "Count", StateToken.Of(Slider(0, 10, 4)));
      var now = AsLeftRecord.Of(Fp(4), "Count", StateToken.Of(StateOnlyChange(what)));

      Assert.False(AsLeftRecord.Unchanged(asLeft, now), what + " changed but the record read it as unchanged");
    }

    [Fact]
    public void A_value_only_change_still_reads_as_changed_and_an_untouched_slider_as_unchanged()
    {
      var asLeft = AsLeftRecord.Of(Fp(4), "Count", StateToken.Of(Slider(0, 10, 4)));

      Assert.False(AsLeftRecord.Unchanged(asLeft, AsLeftRecord.Of(Fp(5), "Count", StateToken.Of(Slider(0, 10, 5)))));
      Assert.True(AsLeftRecord.Unchanged(asLeft, AsLeftRecord.Of(Fp(4), "Count", StateToken.Of(Slider(0, 10, 4)))));
    }

    /// <summary>
    /// #681 (6d, option i): an applied set_value of ANY kind adopts a slider Dwight placed, and a rebuild LEAVES it. Before
    /// #680 a value change adopted it and a domain-only change did not - a rebuild would have removed a slider whose domain
    /// the user had approved changing.
    /// </summary>
    [Fact]
    public void A_domain_only_change_adopts_a_dwight_slider_so_a_rebuild_leaves_it()
    {
      var untouched = Guid.NewGuid();
      var ledger = new BuildLedger(new object());
      ledger.CommitBuild("A1", new Dictionary<Guid, string>
      {
        [SliderGuid] = AsLeftRecord.Of(Fp(4), "Count", StateToken.Of(Slider(0, 10, 4))),
        [untouched] = "as-left:untouched",
      });

      var claim = ledger.Resolve("A1", new Dictionary<Guid, string>
      {
        [SliderGuid] = AsLeftRecord.Of(Fp(4), "Count", StateToken.Of(Slider(0, 50, 4))),   // re-ranged, value kept
        [untouched] = "as-left:untouched",
      }, maxAttempts: 3);

      Assert.Contains(SliderGuid, claim.Adopted);
      Assert.DoesNotContain(SliderGuid, claim.Remove);
      Assert.Contains(untouched, claim.Remove);
    }

    // ---- the canvas stamp -----------------------------------------------------------------------------------

    private static CanvasStampPart Part(string state, int x = 10, int y = 20) =>
      new CanvasStampPart { Guid = SliderGuid, X = x, Y = y, State = state };

    [Fact]
    public void A_domain_only_change_moves_the_canvas_stamp_under_an_unchanged_fingerprint()
    {
      var before = CanvasStampCore.Compose("FP", new[] { Part(StateToken.Of(Slider(0, 10, 4))) });
      var after = CanvasStampCore.Compose("FP", new[] { Part(StateToken.Of(Slider(0, 20, 4))) });

      Assert.NotEqual(before, after);
    }

    [Fact]
    public void A_value_only_change_still_moves_the_stamp_and_nothing_changed_keeps_it()
    {
      var before = CanvasStampCore.Compose("FP-4", new[] { Part(StateToken.Of(Slider(0, 10, 4))) });

      Assert.NotEqual(before, CanvasStampCore.Compose("FP-5", new[] { Part(StateToken.Of(Slider(0, 10, 5))) }));
      Assert.Equal(before, CanvasStampCore.Compose("FP-4", new[] { Part(StateToken.Of(Slider(0, 10, 4))) }));
    }

    /// <summary>What the stamp already held is unchanged: a move still moves it, and the order parts arrive in does not.</summary>
    [Fact]
    public void A_move_still_moves_the_stamp_and_part_order_does_not()
    {
      var other = new CanvasStampPart { Guid = Guid.NewGuid(), X = 0, Y = 0 };
      var a = CanvasStampCore.Compose("FP", new[] { Part(null), other });

      Assert.Equal(a, CanvasStampCore.Compose("FP", new[] { other, Part(null) }));
      Assert.NotEqual(a, CanvasStampCore.Compose("FP", new[] { Part(null, x: 11), other }));
    }
  }
}
