using Dwight.Agent;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>U61: the live line during a long step. GH-free and clock-injected, so the text and the moment are tested, not watched.</summary>
  public class StepProgressTests
  {
    [Fact]
    public void The_line_reads_exactly_as_the_brief_gives_it()
    {
      Assert.Equal("Still working - step 7, 48 s", StepProgress.Format(7, 48));
      Assert.Equal("Still working - step 1, 5 s", StepProgress.Format(1, 5));
    }

    [Fact]
    public void Past_a_minute_the_time_reads_in_minutes_and_seconds()
    {
      Assert.Equal("Still working - step 31, 2 min 5 s", StepProgress.Format(31, 125));
      Assert.Equal("Still working - step 2, 1 min 0 s", StepProgress.Format(2, 60));
      Assert.Equal("Still working - step 2, 59 s", StepProgress.Format(2, 59));
    }

    [Fact]
    public void The_house_dash_is_used_and_no_em_dash_appears()
    {
      var line = StepProgress.Format(3, 10);
      Assert.Contains(" - ", line);
      Assert.DoesNotContain(((char)0x2014).ToString(), line);
    }

    [Fact]
    public void A_nonsense_step_or_time_is_clamped_not_printed()
    {
      Assert.Equal("Still working - step 1, 0 s", StepProgress.Format(0, -4));
    }

    [Fact]
    public void A_young_step_shows_nothing_and_an_old_one_shows_its_age()
    {
      long now = 0;
      var p = new StepProgress(() => now);
      now = 4_000; Assert.Null(p.Line());
      now = 5_000; Assert.Equal("Still working - step 1, 5 s", p.Line());
      now = 48_900; Assert.Equal("Still working - step 1, 48 s", p.Line());
    }

    [Fact]
    public void A_tool_call_ends_the_step_and_the_next_starts_young()
    {
      long now = 0;
      var p = new StepProgress(() => now);
      now = 30_000; Assert.Equal("Still working - step 1, 30 s", p.Line());

      p.ToolCalled();                      // the next tool chip: its own line takes the footer, ours is silent
      Assert.Null(p.Line());
      now = 33_000; Assert.Null(p.Line());
      now = 35_000; Assert.Equal("Still working - step 2, 5 s", p.Line());

      p.ToolCalled(); p.ToolCalled();
      now = 42_000; Assert.Equal("Still working - step 4, 7 s", p.Line());
    }

    [Fact]
    public void A_finished_turn_never_paints_again()
    {
      long now = 0;
      var p = new StepProgress(() => now);
      now = 90_000; Assert.NotNull(p.Line());
      p.Finish();
      Assert.Null(p.Line());
      now = 200_000; Assert.Null(p.Line());
    }
  }
}
