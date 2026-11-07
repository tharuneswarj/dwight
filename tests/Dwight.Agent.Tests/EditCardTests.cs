using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Dwight.Agent;
using Xunit;
using static Dwight.Agent.Tests.EditFixture;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// Phase-9 U6 + U21 (#760): the BATCH CARD - tickable rows, the collapse past about twelve, the TOTAL in the authority
  /// line, the orphan sentence on its own delete row.
  ///
  /// WHAT THESE CAN WITNESS: the markup the plugin writes. WHAT THEY CANNOT: that a real WebView2 renders it, that the
  /// checkboxes are clickable, that the inline handler gathers the ticks and posts them - the render harness
  /// (<c>tools/check-shell-render.js</c>) holds the stylesheet, and the click itself is checked by hand in Rhino before a release.
  /// </summary>
  public class EditCardTests
  {
    private static EditPlan Plan(EditFixture f, params System.Text.Json.Nodes.JsonObject[] ops)
    {
      Assert.True(EditOpsAsk.Validate(Ops(ops), f.Canvas(), out var plan, out var error), error?.Message);
      return plan;
    }

    /// <summary>A card of <paramref name="n"/> distinct valid rows, built over a canvas big enough to hold them: one
    /// delete per spare component, so every row is a different object.</summary>
    private static EditPlan Rows(int n, Action<EditFixture, int> region = null)
    {
      var f = new EditFixture();
      f.Param(1, "Source", accepts: false);
      var ops = new List<System.Text.Json.Nodes.JsonObject>();
      for (var i = 0; i < n; i++)
      {
        var id = 100 + i;
        f.Comp(id, "Part " + i, new[] { "In" }, new[] { ("Out", "Out") });
        region?.Invoke(f, id);
        ops.Add(i % 2 == 0 ? WireOp(1, null, id, null) : DeleteOp(id));
      }
      Assert.True(EditOpsAsk.Validate(Ops(ops.ToArray()), f.Canvas(), out var plan, out var error), error?.Message);
      return plan;
    }

    private static int Count(string html, string needle) => Regex.Matches(html, Regex.Escape(needle)).Count;

    // ---- the rows ------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_small_card_shows_every_row_ticked_and_collapses_nothing()
    {
      var html = ConsentCard.EditHtml("because", "tok", Rows(5));
      Assert.Equal(5, Count(html, "class=\"edit-tick\""));
      Assert.Equal(5, Count(html, "checked=\"checked\""));
      Assert.DoesNotContain("<details class=\"edit-more\"", html);   // Brief BG: the card's one fold is "What this does", not the rows
      Assert.DoesNotContain("more, grouped by region", html);
    }

    [Fact]
    public void A_card_past_twelve_shows_twelve_and_collapses_the_rest_with_the_count()
    {
      var plan = Rows(15);
      var html = ConsentCard.EditHtml("because", "tok", plan);

      var cut = html.IndexOf("<details", StringComparison.Ordinal);
      Assert.True(cut > 0);
      Assert.Equal(ConsentCard.MaxEditRowsShown, Count(html.Substring(0, cut), "class=\"edit-tick\""));
      Assert.Equal(3, Count(html.Substring(cut), "class=\"edit-tick\""));
      Assert.Contains("and 3 more, grouped by region", html);
      // every row is still a checkbox, ticked - the rest are hidden, not dropped, and not unticked
      Assert.Equal(15, Count(html, "checked=\"checked\""));
    }

    [Fact]
    public void The_authority_line_states_the_TOTAL_whether_or_not_every_row_is_on_screen()
    {
      // #760: "the total is the contract; the rows are the evidence".
      foreach (var n in new[] { 1, 3, 12, 13, 30 })
      {
        var html = ConsentCard.EditHtml("because", "tok", Rows(n));
        var total = n == 1 ? "1 change in all" : n + " changes in all";
        Assert.Contains("This card holds " + total, html);
      }
    }

    [Fact]
    public void The_authority_line_says_an_unticked_row_is_not_applied_and_what_a_delete_takes_with_it()
    {
      var line = ConsentCard.EditAuthority(Rows(4));
      Assert.Contains("an unticked row is left exactly as it is", line);
      Assert.Contains("changes nothing else on your canvas", line);
      Assert.Contains("A deleted component takes every wire on it with it", line);
      Assert.Contains("one Ctrl-Z undoes them", line);
      Assert.Contains("2 wires added, 2 components deleted", line);

      // a card with no delete does not say what a delete does
      var wiresOnly = Plan(Make(), WireOp(3, "Result", 6, null));
      Assert.DoesNotContain("A deleted component", ConsentCard.EditAuthority(wiresOnly));
    }

    [Fact]
    public void The_remainder_is_grouped_by_the_canvas_own_regions_with_a_count_each()
    {
      var plan = Rows(16, (f, id) =>
      {
        f.Nodes[id].Region = id < 110 ? "A1" : id < 114 ? "B2" : null;
      });
      var html = ConsentCard.EditHtml("because", "tok", plan);
      var rest = html.Substring(html.IndexOf("<details", StringComparison.Ordinal));

      // rows 13..16 are ids 112..115: two in B2, two in no group
      Assert.Contains("Region B2 (2)", rest);
      Assert.Contains("Not in a group (2)", rest);
      Assert.DoesNotContain("Region A1", rest);   // A1's rows are all on screen
    }

    // ---- the orphan sentence travels with its row --------------------------------------------------------------------------

    [Fact]
    public void The_orphan_sentence_is_inside_the_delete_row_it_belongs_to_the_briefs_exact_wording()
    {
      var html = ConsentCard.EditHtml("because", "tok", Plan(Make(), DeleteOp(3), DeleteOp(7)));
      Assert.Contains("Deleting Addition (3) and Subtraction (7) leave Multiplication (4) input A with no source", html);

      // inside the SECOND row's label, not the first
      var rows = Regex.Matches(html, "<label class=\"edit-row\">.*?</label>", RegexOptions.Singleline).Cast<Match>().ToList();
      Assert.Equal(2, rows.Count);
      Assert.DoesNotContain("Multiplication (4) input A", rows[0].Value);
      Assert.Contains("Multiplication (4) input A with no source", rows[1].Value);
    }

    [Fact]
    public void A_delete_that_feeds_many_inputs_names_eight_and_counts_the_rest()
    {
      var f = new EditFixture();
      f.Comp(1, "Hub", new[] { "In" }, new[] { ("Out", "Out") });
      for (var i = 0; i < 11; i++)
      {
        f.Comp(10 + i, "Sink " + i, new[] { "In" }, new[] { ("Out", "Out") });
        f.Wire(1, 0, 10 + i, 0);
      }
      var plan = Plan(f, DeleteOp(1));
      Assert.Equal(11, plan.Steps[0].Orphans.Count);   // all eleven are computed and reported...
      var html = ConsentCard.EditHtml("because", "tok", plan);
      Assert.Equal(ConsentCard.MaxOrphansShown + 1, Count(html, "class=\"edit-orphan\""));   // ...eight shown, one line for the rest
      Assert.Contains("and 3 more inputs lose a source", html);
    }

    // ---- the click, the escaping, the voices --------------------------------------------------------------------------------

    [Fact]
    public void The_button_posts_the_token_and_the_ticked_row_indices_and_refuses_to_post_none()
    {
      var html = ConsentCard.EditHtml("because", "abc123", Rows(3));
      Assert.Contains("dwightSend('consent:abc123:'+x.join(','))", html);
      Assert.Contains("querySelectorAll('.edit-tick')", html);
      Assert.Contains("if(!x.length){this.textContent='" + ConsentCard.NothingTickedLabel + "';return;}", html);
      Assert.Contains(ConsentCard.EditButton, html);
      // the row value IS the index ConsentTokens.Split hands back
      Assert.Contains("value=\"0\"", html);
      Assert.Contains("value=\"2\"", html);
    }

    [Fact]
    public void The_reason_is_escaped_and_subordinate_and_the_authority_line_contains_none_of_it()
    {
      var html = ConsentCard.EditHtml("<script>alert(1)</script> a distinctive reason", "tok", Rows(2));
      Assert.DoesNotContain("<script>alert(1)", html);
      Assert.Contains("&lt;script&gt;", html);

      var authority = Regex.Match(html, "<p class=\"consent-note\">(.*?)</p>", RegexOptions.Singleline).Groups[1].Value;
      Assert.DoesNotContain("distinctive reason", authority);
      Assert.Contains("Dwight&#39;s reason for asking", html.Replace("'", "&#39;"));
    }

    [Fact]
    public void A_label_that_is_markup_is_encoded_in_its_row()
    {
      var f = Make();
      f.Nodes[6].Label = "<img src=x onerror=alert(1)>";
      var html = ConsentCard.EditHtml("because", "tok", Plan(f, DeleteOp(6)));
      Assert.DoesNotContain("<img", html);
      Assert.Contains("&lt;img", html);
    }

    [Fact]
    public void The_route_title_and_default_authority_exist_for_the_kind()
    {
      Assert.Equal(ConsentRoute.Edit, ConsentCard.Route("edit"));
      Assert.Equal("Make these changes?", ConsentCard.Title("edit"));
      Assert.Contains("only the changes you tick", ConsentCard.Authority("edit"));
      Assert.Contains("only the changes you tick", ConsentCard.EditAuthority(null));
    }

    [Fact]
    public void The_applied_report_names_what_was_done_what_was_left_unsourced_and_what_was_not_applied()
    {
      var plan = Plan(Make(), DeleteOp(3), WireOp(1, null, 6, null));
      var report = EditOpsAsk.AppliedReport(plan, 5, 20, 19);

      Assert.Contains("Applied 2 changes:", report);
      Assert.Contains("Delete Addition (3) and every wire on it", report);
      Assert.Contains("Deleting Addition (3) leaves Panel (5) with no source", report);
      Assert.Contains("3 unticked changes were not applied", report);
      Assert.Contains("One Ctrl-Z undoes all of it", report);
      Assert.DoesNotContain("verified", report);   // it says what was CHECKED, not a bare word that outruns the check
    }
  }
}
