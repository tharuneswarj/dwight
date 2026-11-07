using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Dwight.Agent;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// U27 (#917): a number the plugin states without saying what it counts gets explained by the model,
  /// sometimes wrongly, to the user. <c>solve_and_check</c> returned <c>"solved": false</c> beside zero errors
  /// and the model told the user, twice, that the sliders were not wired to anything; it called
  /// <c>outCounts</c> "likely a flattened total". These tests hold the payload to carrying each figure's
  /// meaning IN the payload, keyed by the field it explains.
  /// </summary>
  public class SolveCheckReportTests
  {
    private static List<SolveCheckRow> Rows() => new List<SolveCheckRow>
    {
      new SolveCheckRow { Id = 1, Type = "Number Slider", OutCounts = new[] { 1 } },
      new SolveCheckRow { Id = 2, Type = "Divide Domain2", OutCounts = new[] { 0 },
        Msgs = { new MsgDto { Level = "warning", Text = "Input parameter I failed to collect data" } } },
      new SolveCheckRow { Id = 3, Type = "Isotrim", OutCounts = new[] { 40, 0 },
        Msgs = { new MsgDto { Level = "error", Text = "boom" }, new MsgDto { Level = "warning", Text = "w" } } },
      new SolveCheckRow { Id = 4, Type = "Area", OutCounts = new[] { 40, 40 } },
    };

    [Fact]
    public void Solved_false_says_nothing_needed_solving_and_what_it_does_not_mean()
    {
      var r = SolveCheckReport.Build(Rows(), solved: false);
      Assert.False(r["solved"].GetValue<bool>());
      var means = r["means"]["solved"].GetValue<string>();
      Assert.Contains("NOTHING NEEDED SOLVING", means);
      Assert.Contains("does not mean the definition failed to solve", means);
      Assert.Contains("nothing about what is or is not wired", means);   // the exact wrong inference, contradicted
    }

    [Fact]
    public void Solved_true_says_this_call_ran_a_solve()
    {
      var r = SolveCheckReport.Build(Rows(), solved: true);
      Assert.True(r["solved"].GetValue<bool>());
      Assert.Contains("this call ran a solve", r["means"]["solved"].GetValue<string>());
    }

    [Fact]
    public void Every_number_the_report_states_has_a_sentence_under_means()
    {
      var r = SolveCheckReport.Build(Rows(), solved: true);
      var means = (JsonObject)r["means"];
      foreach (var kv in r.AsObject())
      {
        if (kv.Key == "means") continue;
        Assert.True(means.ContainsKey(kv.Key), "no means for " + kv.Key);
        Assert.False(string.IsNullOrWhiteSpace(means[kv.Key].GetValue<string>()), "empty means for " + kv.Key);
      }
      // outCounts sits inside emptyOutputs[] and is explained once, at the top.
      Assert.Contains("on each output port", means["outCounts"].GetValue<string>());
      Assert.Contains("neither a branch structure nor a total across ports", means["outCounts"].GetValue<string>());
    }

    [Fact]
    public void Counts_are_what_they_say_they_are()
    {
      var r = SolveCheckReport.Build(Rows(), solved: true);
      Assert.Equal(4, r["components"].GetValue<int>());
      Assert.Equal(1, r["errors"].GetValue<int>());
      Assert.Equal(2, r["warnings"].GetValue<int>());
      Assert.Equal(3, r["msgs"].AsArray().Count);

      // Only the components with a zero output are listed - 2 and 3 - and the slider and Area are omitted, as said.
      var empties = r["emptyOutputs"].AsArray().Select(e => e["id"].GetValue<int>()).ToArray();
      Assert.Equal(new[] { 2, 3 }, empties);
      Assert.Contains("Components that produced data are omitted", r["means"]["emptyOutputs"].GetValue<string>());
    }

    [Fact]
    public void An_empty_canvas_reports_zeros_not_a_throw()
    {
      var r = SolveCheckReport.Build(null, solved: false);
      Assert.Equal(0, r["components"].GetValue<int>());
      Assert.Empty(r["msgs"].AsArray());
      Assert.Empty(r["emptyOutputs"].AsArray());
    }

    [Fact]
    public void Get_component_and_solve_and_check_share_one_definition_of_outCounts()
    {
      // The two reads cannot drift apart about what the same figure means: one constant, read by both.
      var r = SolveCheckReport.Build(Rows(), solved: true);
      Assert.Equal(SolveCheckReport.OutCountsMeans, r["means"]["outCounts"].GetValue<string>());
    }
  }
}
