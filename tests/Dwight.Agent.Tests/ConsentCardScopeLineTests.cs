using System;
using System.Collections.Generic;
using System.Linq;
using Dwight.Agent;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// U28 at the mint, the panel half (wave-2 wiring): the reorganise/simplify card states the scope resolved AT THE ASK.
  ///
  /// The panel's HostTool does three things in a row - VerbScope.ResolveAsk, write the ids back to the seed,
  /// ConsentCard.Html(..., VerbScope.CardScopeLine(scope)). These tests drive that same sequence GH-free, so what is held
  /// here is the card the user reads for each state the rule can reach. What only a sitting can witness is that the
  /// panel's selection read (on the UI thread, issued through the thread's registry) reaches ResolveAsk at all.
  /// </summary>
  public class ConsentCardScopeLineTests
  {
    private const string Token = "tok";

    private static Func<int, IdState> States(params (int Id, IdState State)[] known) =>
      id => known.Where(k => k.Id == id).Select(k => k.State).DefaultIfEmpty(IdState.Unknown).First();

    /// <summary>The panel's mint sequence, verbatim in shape: resolve, write back, render with the scope line.</summary>
    private static string Mint(string kind, List<int> named, Func<int, IdState> state, IReadOnlyList<int> selected,
                               out ToolResult refusal, out List<int> seedIds)
    {
      seedIds = named;
      refusal = VerbScope.ResolveAsk(kind, named, state, selected, out var scope);
      if (refusal != null) return null;
      seedIds = scope.Ids.ToList();
      return ConsentCard.Html(kind, "why", Token, null, null, null, VerbScope.CardScopeLine(scope));
    }

    [Fact]
    public void A_card_resolved_from_the_selection_states_its_count_before_the_consequence()
    {
      var html = Mint("reorganise", new List<int>(), States(), new[] { 4, 5, 6 }, out var refusal, out var ids);

      Assert.Null(refusal);
      Assert.Equal(new[] { 4, 5, 6 }, ids);   // written back: the click runs on these, not the click-time selection
      var line = "On the 3 components that were selected when Dwight asked.";
      Assert.Contains(line, html);
      // Scope first, then what the act does - the count is read before the consequence it qualifies.
      Assert.True(html.IndexOf(line, StringComparison.Ordinal) <
                  html.IndexOf(ConsentCard.ReorganiseConsequence, StringComparison.Ordinal));
    }

    [Fact]
    public void A_card_over_named_components_counts_only_the_live_ones()
    {
      var html = Mint("simplify", new List<int> { 1, 2, 3 },
        States((1, IdState.Alive), (2, IdState.Retired), (3, IdState.Alive)), new[] { 9 }, out var refusal, out var ids);

      Assert.Null(refusal);
      Assert.Equal(new[] { 1, 3 }, ids);
      Assert.Contains("On the 2 components Dwight named.", html);
      Assert.DoesNotContain("selected", html.Substring(0, html.IndexOf("consent-why", StringComparison.Ordinal)));
    }

    [Fact]
    public void Nothing_named_and_nothing_selected_mints_no_card()
    {
      var html = Mint("reorganise", new List<int>(), States(), new int[0], out var refusal, out _);

      Assert.Null(html);
      Assert.NotNull(refusal);
      Assert.False(refusal.Ok);
    }

    [Fact]
    public void Named_components_all_gone_mint_no_card_even_with_a_selection()
    {
      var html = Mint("simplify", new List<int> { 7 }, States((7, IdState.Retired)), new[] { 1, 2 }, out var refusal, out _);

      Assert.Null(html);
      Assert.NotNull(refusal);
    }

    [Fact]
    public void Without_a_scope_line_every_card_reads_exactly_as_before()
    {
      foreach (var kind in new[] { "reorganise", "simplify", "solve", "scaffold" })
      {
        Assert.Equal(ConsentCard.Html(kind, "why", Token, null, null, null),
                     ConsentCard.Html(kind, "why", Token, null, null, null, null));
        Assert.Equal(ConsentCard.Html(kind, "why", Token, null, null, null),
                     ConsentCard.Html(kind, "why", Token, null, null, null, "   "));
      }
      Assert.Equal("a", ConsentCard.WithScope("a", null));
    }

    [Fact]
    public void The_scope_line_is_escaped_like_every_plugin_line()
    {
      // Plugin-authored, but it passes through the same encoder as the authority line it joins.
      var html = ConsentCard.Html("reorganise", "why", Token, null, null, null, "On <b>3</b>.");
      Assert.Contains("On &lt;b&gt;3&lt;/b&gt;.", html);
    }
  }
}
