using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dwight.Diagnostics;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// Brief EX, defect 2: the clip's Diagnose listed Division (11) and Negative (12), both badged ROOT CAUSE, while the Negative's text, the model's, said "most likely a knock-on effect of the failed Division (11)".
  /// The clip's still and Rhino 2's rebuilt run (its measurement of 4 November) show the Negative was NOT wired: Grasshopper's "Input parameter x failed to collect data", no edge into
  /// node 12. So the scan was right that it is a root; the text guessed a link the graph does not hold. The plugin now writes that fault's cause itself, and the payload carries the fact. Every rule here is
  /// FaultChainCore's, GH-free (ADR-091); FaultScan.Run and the result view delegate to it, pinned below.
  /// </summary>
  public class FaultChainCoreTests
  {
    private const string DivByZero = "Division by zero";   // Grasshopper's error on a Division with B at 0 (both captures)
    private const string XStarved = "Input parameter x failed to collect data";   // its warning on the clip's Negative (the rebuilt run)

    private static FaultChainCore.Node N(int id, bool up, bool down, int[] outs, int inputData, params (string Level, string Text)[] msgs) => new FaultChainCore.Node
    {
      Id = id, HasUpstream = up, HasDownstream = down, OutCounts = outs, InputDataCount = () => inputData, Msgs = msgs.ToList(),
    };

    private static FaultChainCore.Wire W(int from, int to, string toParam) => new FaultChainCore.Wire { From = from, To = to, ToParam = toParam };

    // ---- the three graphs -----------------------------------------------------------------------------------------------------------------------------------------------------

    /// <summary>Rhino 2's first capture: sliders w (1) and dp (2) at 6 and 0, Division (3) on them, a Negative (4) wired to it, a Panel (5). Grasshopper gives the Negative no message.</summary>
    [Fact]
    public void A_Negative_wired_to_a_failed_Division_that_Grasshopper_gives_no_message_is_no_fault()
    {
      var nodes = new[]
      {
        N(1, false, true, new[] { 1 }, 0), N(2, false, true, new[] { 1 }, 0),
        N(3, true, true, new[] { 1 }, 2, ("error", DivByZero)),
        N(4, true, true, new[] { 1 }, 1),
        N(5, true, false, new int[0], 1),
      };
      var found = FaultChainCore.Scan(nodes, new[] { W(1, 3, "A"), W(2, 3, "B"), W(3, 4, "x"), W(4, 5, "in") });

      var only = Assert.Single(found);
      Assert.Equal(3, only.Id);
      Assert.True(only.IsRoot);
      Assert.True(only.Intrinsic);
      Assert.Empty(only.Upstream);
      Assert.Empty(only.Unwired);   // "Division by zero" is its own error, and the model explains it
    }

    /// <summary>The clip's take (ws1, s6 at 0, the Negative unwired): the scan's two roots are both true, and the Negative's cause is the plugin's, from the graph.</summary>
    [Fact]
    public void An_unwired_Negative_is_a_root_whose_cause_the_plugin_writes()
    {
      var nodes = new[]
      {
        N(10, true, true, new[] { 1 }, 2),                               // the Subtraction feeding A
        N(6, false, true, new[] { 1 }, 0),                               // s6 at 0
        N(11, true, false, new[] { 1 }, 2, ("error", DivByZero)),
        N(12, false, false, new[] { 0 }, 0, ("warning", XStarved)),
      };
      var found = FaultChainCore.Scan(nodes, new[] { W(10, 11, "A"), W(6, 11, "B") });

      Assert.Equal(new[] { 11, 12 }, found.Select(f => f.Id));   // roots, the error before the warning
      Assert.All(found, f => Assert.True(f.IsRoot));
      Assert.All(found, f => Assert.True(f.Intrinsic));   // a root, so the payload still carries it, with its facts
      Assert.All(found, f => Assert.Empty(f.Upstream));
      Assert.Empty(found[0].Unwired);
      Assert.Equal(new[] { "x" }, found[1].Unwired);
      Assert.Equal("Nothing is wired into `x`.", FaultChainCore.UnwiredCause(found[1].Unwired));
      Assert.Equal("Connect a source to `x`.", FaultChainCore.UnwiredFix(found[1].Unwired));

      var chains = FaultChainCore.Group(found, f => f.Id, f => f.IsRoot, f => f.Upstream);
      Assert.Equal(new[] { 11, 12 }, chains.Select(c => c.Root.Id));
      Assert.All(chains, c => Assert.Empty(c.Members));
    }

    /// <summary>The brief's graph: a Negative fed by a failed Division, starved by it. A propagation member under the Division, and never a root.</summary>
    [Fact]
    public void A_Negative_fed_by_a_failed_Division_is_a_member_of_its_chain_and_never_a_root()
    {
      var nodes = new[]
      {
        N(6, false, true, new[] { 1 }, 0),
        N(11, true, true, new int[] { 0 }, 1, ("error", DivByZero)),
        N(12, true, false, new[] { 0 }, 0, ("warning", XStarved)),
      };
      var found = FaultChainCore.Scan(nodes, new[] { W(6, 11, "B"), W(11, 12, "x") });

      Assert.Equal(new[] { 11, 12 }, found.Select(f => f.Id));
      var negative = found.Single(f => f.Id == 12);
      Assert.False(negative.IsRoot);
      Assert.False(negative.Intrinsic);       // starvation only, with a fault upstream: the deterministic one-liner
      Assert.Equal(new[] { 11 }, negative.Upstream);
      Assert.Empty(negative.Unwired);         // x IS wired, and a non-root never carries the class

      var chain = Assert.Single(FaultChainCore.Group(found, f => f.Id, f => f.IsRoot, f => f.Upstream));
      Assert.Equal(11, chain.Root.Id);
      Assert.Equal(new[] { 12 }, chain.Members.Select(m => m.Id));
    }

    // ---- the class: only Grasshopper's starvation words, only inputs with no wire ------------------------------------------------------------------------------------------------

    [Fact]
    public void A_starved_input_that_is_wired_is_not_the_class() =>
      Assert.Empty(FaultChainCore.UnwiredInputs(new[] { ("warning", XStarved) }, new[] { "x" }));   // fed by something that gave nothing: the model explains it

    [Fact]
    public void Any_other_message_beside_the_starvation_is_not_the_class() =>
      Assert.Empty(FaultChainCore.UnwiredInputs(new[] { ("warning", XStarved), ("error", "1. Solution exception: Index was outside the bounds of the array.") }, new string[0]));

    [Theory]
    [InlineData("Input parameter x failed to collect data.")]
    [InlineData("  Input parameter x failed to collect data  ")]
    public void Grasshoppers_words_with_a_full_stop_or_spaces_are_the_class(string text) =>
      Assert.Equal(new[] { "x" }, FaultChainCore.UnwiredInputs(new[] { ("warning", text) }, new string[0]));

    [Theory]
    [InlineData("Floating parameter x failed to collect data")]       // a param, not a component's input
    [InlineData("Parameter x of unknown type failed to collect data")]
    [InlineData("Input parameter  failed to collect data")]           // no name to state (a blank nickname)
    [InlineData("Input parameter   failed to collect data")]          // a name of spaces only
    [InlineData("x failed to connect")]
    public void Starvation_words_that_name_no_input_are_not_the_class(string text) =>
      Assert.Empty(FaultChainCore.UnwiredInputs(new[] { ("warning", text) }, new string[0]));

    [Fact]
    public void A_remark_does_not_break_the_class() =>
      Assert.Equal(new[] { "x" }, FaultChainCore.UnwiredInputs(new[] { ("warning", XStarved), ("remark", "anything") }, new string[0]));

    [Fact]
    public void Two_unwired_inputs_are_both_named()
    {
      var unwired = FaultChainCore.UnwiredInputs(new[] { ("warning", "Input parameter A failed to collect data"), ("warning", "Input parameter B failed to collect data") }, new[] { "C" });
      Assert.Equal(new[] { "A", "B" }, unwired);
      Assert.Equal("Nothing is wired into `A` or `B`.", FaultChainCore.UnwiredCause(unwired));
      Assert.Equal("Connect a source to `A` and `B`.", FaultChainCore.UnwiredFix(unwired));
    }

    [Fact]
    public void One_wired_input_among_the_starved_ones_is_not_the_class() =>
      Assert.Empty(FaultChainCore.UnwiredInputs(new[] { ("warning", "Input parameter A failed to collect data"), ("warning", "Input parameter B failed to collect data") }, new[] { "B" }));

    /// <summary>Grasshopper warns only on a required input; an optional one with no wire says nothing, so the component is no fault and nothing is claimed.</summary>
    [Fact]
    public void An_optional_input_with_no_wire_fires_nothing()
    {
      var found = FaultChainCore.Scan(new[] { N(7, true, true, new[] { 1 }, 1) }, new[] { W(6, 7, "A") });
      Assert.Empty(found);
    }

    /// <summary>A fault Grasshopper starved, with a fault upstream reached through a wire that is not into the named input, stays propagation: the class is for roots only.</summary>
    [Fact]
    public void A_starved_fault_below_another_fault_never_carries_the_class()
    {
      var nodes = new[] { N(1, false, true, new[] { 1 }, 0, ("error", DivByZero)), N(2, true, false, new[] { 0 }, 0, ("warning", XStarved)) };
      var below = FaultChainCore.Scan(nodes, new[] { W(1, 2, "y") }).Single(f => f.Id == 2);
      Assert.False(below.IsRoot);
      Assert.Empty(below.Unwired);
    }

    // ---- the rules moved from FaultScan, byte for byte ------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_order_is_roots_first_then_error_warning_silent_then_id()
    {
      var nodes = new[]
      {
        N(2, true, true, new[] { 0 }, 3),                       // silent root
        N(4, false, true, new[] { 1 }, 0, ("warning", "w")),     // warning root
        N(9, true, false, new[] { 1 }, 1, ("error", "e")),      // error, below 4
        N(5, false, true, new[] { 1 }, 0, ("error", "e")),      // error root
        N(3, false, true, new[] { 1 }, 0, ("error", "e")),      // error root, a lower id
      };
      var found = FaultChainCore.Scan(nodes, new[] { W(4, 9, "a") });
      Assert.Equal(new[] { 3, 5, 4, 2, 9 }, found.Select(f => f.Id));   // ids out of kind order, so the kind is what orders them
      Assert.Equal(new[] { 0, 0, 1, 2, 0 }, found.Select(f => f.Kind));
    }

    [Fact]
    public void A_root_is_exactly_a_fault_with_no_fault_upstream()
    {
      var nodes = Enumerable.Range(1, 5).Select(i => N(i, i > 1, i < 5, new[] { 1 }, 1, ("error", "e" + i))).ToArray();
      var found = FaultChainCore.Scan(nodes, new[] { W(1, 2, "a"), W(2, 3, "a"), W(3, 4, "a"), W(4, 5, "a") });
      Assert.All(found, f => Assert.Equal(f.Upstream.Count == 0, f.IsRoot));
      Assert.Equal(new[] { 1, 2, 3, 4 }, found.Single(f => f.Id == 5).Upstream.OrderBy(i => i));   // transitive, through every fault
    }

    [Fact]
    public void An_ancestor_is_reached_through_a_component_that_is_no_fault()
    {
      var nodes = new[] { N(1, false, true, new[] { 1 }, 0, ("error", "e")), N(2, true, true, new[] { 1 }, 1), N(3, true, false, new[] { 0 }, 0, ("warning", XStarved)) };
      var found = FaultChainCore.Scan(nodes, new[] { W(1, 2, "a"), W(2, 3, "x") });
      Assert.Equal(new[] { 1 }, found.Single(f => f.Id == 3).Upstream);
    }

    [Theory]
    [InlineData(true, true, new[] { 0 }, 2, true)]     // got data, fed something, gave nothing: silent
    [InlineData(true, false, new[] { 0 }, 2, false)]   // nothing relies on it
    [InlineData(true, true, new[] { 0, 1 }, 2, false)] // one output carried data
    [InlineData(true, true, new[] { 0 }, 0, false)]    // it simply got nothing
    [InlineData(false, true, new[] { 0 }, 0, false)]   // a pure source that outputs nothing is unset
    [InlineData(true, true, new int[0], 2, false)]     // no outputs to read
    public void The_silent_rule_is_kept(bool up, bool down, int[] outs, int inputData, bool silent) =>
      Assert.Equal(silent, FaultChainCore.IsSilentFailure(N(1, up, down, outs, inputData)));

    [Fact]
    public void A_message_makes_a_component_no_silent_suspect() =>
      Assert.Equal(1, FaultChainCore.KindOf(N(1, true, true, new[] { 0 }, 2, ("warning", "w"))));

    [Fact]
    public void A_fault_on_two_roots_is_drawn_under_the_first()
    {
      var nodes = new[] { N(1, false, true, new[] { 1 }, 0, ("error", "e")), N(2, false, true, new[] { 1 }, 0, ("error", "e")), N(3, true, false, new[] { 0 }, 0, ("warning", XStarved)) };
      var found = FaultChainCore.Scan(nodes, new[] { W(1, 3, "a"), W(2, 3, "b") });
      var chains = FaultChainCore.Group(found, f => f.Id, f => f.IsRoot, f => f.Upstream);
      Assert.Equal(new[] { 1, 2 }, chains.Select(c => c.Root.Id));
      Assert.Equal(new[] { 3 }, chains[0].Members.Select(m => m.Id));
      Assert.Empty(chains[1].Members);
    }

    // ---- the doors, pinned by source (ADR-091): the scan, the card, the payload and the prompt read the core's facts ---------------------------------------------------------------

    private static string Repo(params string[] parts)
    {
      var dir = new DirectoryInfo(AppContext.BaseDirectory);
      while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Dwight.sln"))) dir = dir.Parent;
      Assert.True(dir != null, "the repository root (Dwight.sln) could not be found");
      return File.ReadAllText(Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray())).Replace("\r\n", "\n");
    }

    [Fact]
    public void The_scan_is_the_cores()
    {
      var scan = Repo("Dwight", "Diagnostics", "FaultScan.cs");
      Assert.Contains("var found = FaultChainCore.Scan(nodes, wires);", scan);
      Assert.Contains("ToParam = e.ToParam", scan);
      Assert.Contains("Unwired = f.Unwired,", scan);
      Assert.DoesNotContain("AncestorsAmong", scan);
      Assert.DoesNotContain("IsSilentFailure", scan);
    }

    [Fact]
    public void The_result_view_draws_the_cores_chains_and_the_plugins_cause()
    {
      var actions = Repo("Dwight", "LlmActions.cs");
      Assert.Contains("FaultChainCore.Group(report.Faults, f => f.Node.Id, f => f.IsRoot, f => f.UpstreamFaultIds)", actions);
      var obj = actions.Substring(actions.IndexOf("private static JsonObject FaultObj(", StringComparison.Ordinal));
      var unwired = obj.IndexOf("if (f.Unwired.Count > 0)", StringComparison.Ordinal);
      Assert.True(unwired > 0 && unwired < obj.IndexOf("else if (f.Intrinsic)", StringComparison.Ordinal));   // before the model's words are read
      Assert.Contains("cause = FaultChainCore.UnwiredCause(f.Unwired);", obj);
      Assert.Contains("fix = FaultChainCore.UnwiredFix(f.Unwired);", obj);
    }

    /// <summary>#315: what the prompt tells the model about "role" and "unwired" is held on the behaviour's side, above (a root has no fault upstream; the class is present exactly for its faults), and the payload writes the field only then.</summary>
    [Fact]
    public void The_prompt_says_what_the_payload_holds()
    {
      var payloads = Repo("Dwight", "Data", "Payloads.cs");
      Assert.Contains("if (f.Unwired.Count > 0) fault[\"unwired\"] = new JsonArray(f.Unwired.Select(n => (JsonNode)n).ToArray());", payloads);
      Assert.Contains("[\"role\"] = f.IsRoot ? \"root\" : \"symptom\",", payloads);

      var prompt = Repo("Dwight", "Prompts", "Prompts.cs");
      Assert.Contains("Draw a link between components only from the payload's \"\"edges\"\", never from their ids, names or places on the\ncanvas: a fault with \"\"role\"\":\"\"root\"\" has nothing faulty upstream, so it is a cause of its own and never a\nknock-on of another fault. An input listed in a fault's \"\"unwired\"\" has nothing wired into it.", prompt);
    }
  }
}
