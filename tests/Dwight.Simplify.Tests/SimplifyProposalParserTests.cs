using System.Collections.Generic;
using System.Linq;
using Dwight.Simplify;
using Xunit;

namespace Dwight.Simplify.Tests
{
  /// <summary>Parsing the LLM's Stage-6 change-grouped simplify_proposal JSON (defensive: malformed -> null). Each
  /// change owns its ops; the groups flatten (via PlanFilter) into a proposal the validator accepts.</summary>
  public class SimplifyProposalParserTests
  {
    private const string Valid = @"{
      ""changes"":[
        {""class"":""remove-dead"",""text"":""The relay does nothing."",""confirm"":true,
         ""remove"":[2],""rewire"":[{""from"":""i0"",""fromPort"":0,""to"":""k3"",""toPort"":0}]},
        {""class"":""externalize"",""text"":""Hardcoded radius is now a slider."",""confirm"":true,
         ""add"":[{""type"":""Number Slider"",""nick"":""radius""}],""rewire"":[{""from"":""a0"",""fromPort"":0,""to"":""k3"",""toPort"":1}]},
        {""class"":""rename"",""text"":""Named the panel."",""confirm"":false,""rename"":[{""id"":1,""nick"":""iso directions""}]}
      ]}";

    [Fact]
    public void Parses_change_groups_with_their_ops()
    {
      var groups = SimplifyProposalParser.Parse(Valid);
      Assert.NotNull(groups);
      Assert.Equal(3, groups.Count);

      Assert.Equal("remove-dead", groups[0].Class);
      Assert.True(groups[0].Confirm);
      Assert.Equal(new[] { 2 }, groups[0].Remove.ToArray());
      Assert.Equal("i0", groups[0].Rewire[0].From);

      Assert.Equal("externalize", groups[1].Class);
      Assert.Single(groups[1].Add);
      Assert.Equal("Number Slider", groups[1].Add[0].Type);
      Assert.Equal("a0", groups[1].Rewire[0].From);   // change-LOCAL ref

      Assert.Equal("rename", groups[2].Class);
      Assert.Single(groups[2].Rename);
      Assert.Equal(1, groups[2].Rename[0].Id);
      Assert.Equal("iso directions", groups[2].Rename[0].Nick);
    }

    [Fact]
    public void The_flattened_proposal_passes_the_validator()
    {
      var groups = SimplifyProposalParser.Parse(Valid);
      var p = PlanFilter.Flatten(groups, ticked: null, selectionIds: new HashSet<int> { 1, 2, 3 });
      Assert.Equal(new[] { 1, 3 }, p.Keep.OrderBy(x => x).ToArray());   // derived: selection minus removed 2
      var ctx = new SimplifyContext
      {
        SelectionIds = new HashSet<int> { 1, 2, 3 },
        OutputProducers = new HashSet<int> { 3 },
        TypeAllowed = _ => true,
      };
      var (ok, error) = ProposalValidator.Validate(p, ctx);
      Assert.True(ok, error);
    }

    [Fact]
    public void An_externalize_change_carries_its_nominated_target_onto_the_slider_add()
    {
      // RR7: the model NOMINATES the target (component id + optional input name); the plugin computes value+type+
      // wiring. The parser attaches the target to the change's Number Slider add.
      var groups = SimplifyProposalParser.Parse(@"{""changes"":[
        {""class"":""externalize"",""text"":""gate to a switch"",""confirm"":true,
         ""add"":[{""type"":""Number Slider"",""nick"":""boundaryIndex""}],
         ""target"":{""id"":11,""param"":""Gate""}}]}");
      Assert.Single(groups);
      var add = groups[0].Add[0];
      Assert.Equal(11, add.ExternalizeTargetId);
      Assert.Equal("Gate", add.ExternalizeTargetParam);
    }

    [Fact]
    public void Malformed_json_parses_to_null()
    {
      Assert.Null(SimplifyProposalParser.Parse("not json at all"));
      Assert.Null(SimplifyProposalParser.Parse("[1,2,3]"));   // a non-object root
    }

    [Fact]
    public void Advice_parses_separately_and_never_breaks_the_change_parse()
    {
      const string withAdvice = @"{
        ""changes"":[{""class"":""reduce"",""text"":""Clean."",""confirm"":false}],
        ""advice"":[{""observation"":""The Loft rebuilds every solve."",""suggestion"":""Cache it downstream.""},
                    {""observation"":""Index is hardcoded."",""suggestion"":""Expose it.""}]}";
      Assert.Single(SimplifyProposalParser.Parse(withAdvice));           // changes still parse
      var advice = SimplifyProposalParser.ParseAdvice(withAdvice);
      Assert.Equal(2, advice.Count);
      Assert.Equal("The Loft rebuilds every solve.", advice[0].Observation);
      Assert.Equal("Cache it downstream.", advice[0].Suggestion);

      Assert.Empty(SimplifyProposalParser.ParseAdvice(@"{""changes"":[]}"));   // no advice key -> empty, not null
      Assert.Empty(SimplifyProposalParser.ParseAdvice("not json"));            // malformed -> empty, never throws
    }

    [Fact]
    public void An_already_clean_proposal_is_a_single_op_less_change()
    {
      var groups = SimplifyProposalParser.Parse(@"{""changes"":[{""class"":""reduce"",""text"":""Already clean."",""confirm"":false}]}");
      Assert.Single(groups);
      Assert.Empty(groups[0].Remove);
      Assert.Empty(groups[0].Add);
      var p = PlanFilter.Flatten(groups, null, new HashSet<int> { 1, 2 });
      Assert.Equal(new[] { 1, 2 }, p.Keep.OrderBy(x => x).ToArray());   // everything kept
    }
  }
}
