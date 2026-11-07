using System;
using System.IO;
using System.Reflection;
using Xunit;

// The product writes statics these tests read (TurnShape, StreamCapture's window, LastPlanNote and whatever comes next),
// so two collections side by side can read each other's writes. Guarding each static by a hand-listed collection went
// wrong three times on 1 November, so the whole project now runs one collection at a time. The collections stay, as the
// map of which statics exist; where their comments say "runs alone, after the parallel ones", there are now no parallel ones.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Dwight.Agent.Tests
{
  public class OneCollectionAtATimeTests
  {
    [Fact]
    public void This_project_runs_one_collection_at_a_time()
    {
      var behaviour = typeof(OneCollectionAtATimeTests).Assembly.GetCustomAttribute<CollectionBehaviorAttribute>();

      Assert.NotNull(behaviour);
      Assert.True(behaviour.DisableTestParallelization);
    }

    /// <summary>A runner config beside the assembly is read after the attribute and overrides it, so one would turn the
    /// parallel runs back on with the attribute still in place.</summary>
    [Theory]
    [InlineData("xunit.runner.json")]
    [InlineData("Dwight.Agent.Tests.xunit.runner.json")]
    public void No_runner_config_can_turn_it_back_on(string config)
    {
      Assert.False(File.Exists(Path.Combine(AppContext.BaseDirectory, config)));
    }
  }
}
