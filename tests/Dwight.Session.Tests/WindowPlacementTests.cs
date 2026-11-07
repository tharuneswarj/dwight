using System.Collections.Generic;
using Dwight.UI;
using Xunit;
using Rect = Dwight.UI.WindowPlacement.Rect;

namespace Dwight.Session.Tests
{
  /// <summary>
  /// Where a floating Dwight window goes (Tharun's first tour finding, 2026-09-19).
  ///
  /// <para>WHAT THESE CAN AND CANNOT PROVE, stated first. The tour opened at the bottom left at startup and
  /// centred correctly when re-opened from Settings - SAME code, so the defect he saw was TIMING, and no
  /// headless test reproduces timing. Its witness is the Debug placement log at the re-sit. A test "failing on
  /// today's code with his working area" would have had to be contrived: today's line places a 520x580 window
  /// at (380, 56) on his 1280x693 logical area, which is centred and inside - one row below says so outright.
  /// These hold the arithmetic's own two real faults instead: nothing clamped, and constant half-sizes.</para>
  /// </summary>
  public class WindowPlacementTests
  {
    /// <summary>Tharun's screen: 1920x1080 at 150% is 1280x720 logical, less the taskbar.</summary>
    private static readonly Rect His = new Rect(0, 0, 1280, 693);

    private static void AssertInside(Rect area, (double X, double Y) at, double w, double h)
    {
      Assert.True(at.X >= area.X && at.X + w <= area.X + area.Width, "off the side: x=" + at.X);
      Assert.True(at.Y >= area.Y && at.Y + h <= area.Y + area.Height, "off the top or bottom: y=" + at.Y);
    }

    // ---- centred, and inside ----------------------------------------------------------------------

    /// <summary>The tour's size on his screen: centred and inside. It passes on the old arithmetic too - which
    /// is the point. His defect was not here; this row is the proof, not a regression guard.</summary>
    [Fact]
    public void The_tour_on_his_screen_is_centred_and_inside()
    {
      var at = WindowPlacement.Centre(His, 520, 580);

      Assert.Equal((380.0, 56.5), at);
      AssertInside(His, at, 520, 580);
    }

    /// <summary>THE CONSTANT-HALF-SIZE FAULT. The old line centred with 260 and 290, which is right for exactly
    /// one window. The tour is being redesigned at a new size; this is that window, centred by its OWN size.</summary>
    [Theory]
    [InlineData(700, 400)]
    [InlineData(360, 640)]
    [InlineData(460, 540)]   // Settings' size, which also carried hard-coded halves
    public void A_window_of_any_size_is_centred_by_its_own_size(double w, double h)
    {
      var at = WindowPlacement.Centre(His, w, h);

      Assert.Equal(((1280 - w) / 2, (693 - h) / 2), at);
    }

    // ---- clamped when it cannot be centred ---------------------------------------------------------

    /// <summary>THE NO-CLAMP FAULT. A window taller than the area went off its TOP - title bar and close button
    /// gone. Now it is pinned to the top edge so it can always be dragged or closed.</summary>
    [Fact]
    public void A_window_taller_than_the_area_is_pinned_to_its_top_not_pushed_above_it()
    {
      var small = new Rect(0, 0, 1280, 500);

      var at = WindowPlacement.Centre(small, 520, 580);

      Assert.Equal(0, at.Y);
      Assert.Equal(380, at.X);   // still centred on the axis where it fits
    }

    [Fact]
    public void A_window_wider_than_the_area_is_pinned_to_its_left()
    {
      var narrow = new Rect(0, 0, 400, 693);

      Assert.Equal(0, WindowPlacement.Centre(narrow, 520, 580).X);
    }

    // ---- the screen it is on -----------------------------------------------------------------------

    /// <summary>A second monitor to the LEFT of the primary has negative coordinates. The area's own origin is
    /// honoured, so the window lands on that monitor rather than being dragged back to the primary.</summary>
    [Fact]
    public void A_monitor_to_the_left_of_the_primary_keeps_its_negative_origin()
    {
      var left = new Rect(-1280, 0, 1280, 693);

      var at = WindowPlacement.Centre(left, 520, 580);

      Assert.Equal(-900, at.X);
      AssertInside(left, at, 520, 580);
    }

    private static readonly IReadOnlyList<Rect> TwoScreens = new[]
    {
      new Rect(0, 0, 1280, 720),        // 0: primary
      new Rect(1280, 0, 1920, 1080),    // 1: to its right
    };

    /// <summary>"Primary" is not necessarily where the user is looking - the screen Grasshopper is on wins.</summary>
    [Fact]
    public void The_screen_Grasshopper_is_on_wins_over_the_primary()
    {
      Assert.Equal(1, WindowPlacement.PickScreen(TwoScreens, (2000, 500), (100, 100), primaryIndex: 0));
    }

    /// <summary>At startup the editor may not be showing yet; then the screen under the mouse.</summary>
    [Fact]
    public void With_Grasshopper_not_yet_showing_the_mouse_decides()
    {
      Assert.Equal(1, WindowPlacement.PickScreen(TwoScreens, null, (2000, 500), primaryIndex: 0));
    }

    [Fact]
    public void With_neither_known_the_primary_is_the_fallback()
    {
      Assert.Equal(0, WindowPlacement.PickScreen(TwoScreens, null, null, primaryIndex: 0));
      Assert.Equal(0, WindowPlacement.PickScreen(TwoScreens, (99999, 99999), null, primaryIndex: 0));
    }

    [Fact]
    public void No_screens_at_all_is_answered_honestly_not_with_an_invented_index()
    {
      Assert.Equal(-1, WindowPlacement.PickScreen(new Rect[0], (0, 0), (0, 0), primaryIndex: 0));
    }
  }
}
