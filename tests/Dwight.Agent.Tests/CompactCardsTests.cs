using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dwight.Agent;
using Dwight.Data;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// Brief BG: a card that waits is open and trimmed (the act and scope in one sentence, the reason, the buttons, one plain line, and one
  /// fold "What this does and what it cannot"); a card that was answered is a receipt row; an automatic approval arrives as one; the
  /// thread's result lines are receipts of the same shape; and the complete text stays in the DOM in every state (#338).
  /// </summary>
  public class CompactCardsTests
  {
    private const string Why = "Because the slider feeds the panel you asked about.";
    private static readonly string[] Regions = { "A1", "B2" };

    private static SpotPlacement Spot() => new SpotPlacement
    {
      Address = "Spot 5", Beside = "empty_number", Components = new List<string> { "a Square" },
    };

    private static SetValueChange Value() => new SetValueChange
    {
      Id = 42, Type = "Number Slider", Nick = "Count", Before = "0.25 (0 to 1, float)", After = "40 (0 to 100, integer)",
    };

    private static ScriptEditChange ScriptEdit() => new ScriptEditChange
    {
      Label = "\"F1\"", Claim = ScriptEditClaim.Changes, NewSource = "a = x * 2", Language = BuildScriptTypes.Python,
    };

    /// <summary>Every card a thread can hold, waiting, keyed by its kind.</summary>
    private static IEnumerable<(string kind, string html, string act)> Waiting()
    {
      yield return ("solve", ConsentCard.Html("solve", Why, "tok-solve"), "Re-run the definition.");
      yield return ("scaffold", ConsentCard.Html("scaffold", Why, "tok-scaffold"), "Lay down a scaffold map.");
      yield return ("reorganise", ConsentCard.Html("reorganise", Why, "tok-reorg"), "Reorganise this definition.");
      yield return ("simplify", ConsentCard.Html("simplify", Why, "tok-simp"), "Simplify this selection.");
      yield return ("build", ConsentCard.Html("build", Why, "tok-build", null, Regions), "Build A1 and B2.");
      yield return ("set_value", ConsentCard.Html("set_value", Why, "tok-sv", Value()),
                    "Set " + Value().Label + " from 0.25 (0 to 1, float) to 40 (0 to 100, integer).");
      yield return ("edit_script", ConsentCard.EditScriptHtml(Why, "tok-es", ScriptEdit(), ScriptEditOutcome.RanClean, new ScriptTrialFacts { Ran = true }),
                    "Replace the source of \"F1\".");
      yield return ("edit", ConsentCard.EditHtml(Why, "tok-edit", null), "Make these changes.");
      yield return ("run_script", ConsentCard.RunScriptHtml(Why, "tok-run", "print(1)", expanded: true), "Run a Python 3 script of 1 line.");
      yield return ("spot", ConsentCard.SpotHtml(Why, Spot(), "tok-g", "tok-l"), "Place a Square, Spot 5, near empty_number.");
    }

    private static string Text(string html) =>
      Regex.Replace(WebUtility.HtmlDecode(Regex.Replace(html ?? "", "<[^>]+>", " ")), "\\s+", " ").Trim();

    // ---- waiting: open and trimmed --------------------------------------------------------------------------------------------

    [Fact]
    public void Every_waiting_card_shows_the_act_the_reason_its_buttons_the_one_liner_and_one_fold_in_that_order()
    {
      foreach (var (kind, html, act) in Waiting())
      {
        Assert.False(CardReceipt.IsReceipt(html), kind);
        var a = html.IndexOf("<p class=\"consent-act\"><strong>" + WebUtility.HtmlEncode(act) + "</strong></p>", StringComparison.Ordinal);
        var why = html.IndexOf("<div class=\"consent-why\"><span class=\"consent-why-label\">Dwight's reason for asking</span>" + WebUtility.HtmlEncode(Why), StringComparison.Ordinal);
        var btn = html.IndexOf("<div class=\"consent-btns\">", StringComparison.Ordinal);
        var line = html.IndexOf("<p class=\"consent-line\">", StringComparison.Ordinal);
        var fold = html.IndexOf("<details class=\"consent-more\"><summary>What this does and what it cannot</summary>", StringComparison.Ordinal);
        Assert.True(a >= 0 && a < why && why < btn && btn < line && line < fold, kind + ": " + a + " " + why + " " + btn + " " + line + " " + fold);
        Assert.Equal(1, Regex.Matches(html, "consent-more").Count);
      }
    }

    [Fact]
    public void The_fold_holds_the_question_title_and_the_full_consequence_and_the_face_carries_neither()
    {
      foreach (var (kind, html, _) in Waiting().Where(w => w.kind != "spot" && w.kind != "edit" && w.kind != "set_value" && w.kind != "edit_script" && w.kind != "build"))
      {
        var fold = html.Substring(html.IndexOf("<details class=\"consent-more\">", StringComparison.Ordinal));
        var face = html.Substring(0, html.IndexOf("<details class=\"consent-more\">", StringComparison.Ordinal));
        Assert.Contains("<strong>" + WebUtility.HtmlEncode(ConsentCard.Title(kind)) + "</strong>", fold);
        Assert.Contains(WebUtility.HtmlEncode(ConsentCard.Authority(kind)), fold);
        Assert.DoesNotContain(WebUtility.HtmlEncode(ConsentCard.Authority(kind)), face);
      }
    }

    [Fact]
    public void A_spot_card_has_short_buttons_and_both_full_consequences_in_the_fold()
    {
      var html = ConsentCard.SpotHtml(Why, Spot(), "tok-g", "tok-l");
      Assert.Contains("onclick=\"dwightSend('consent:tok-g')\">In a group</button>", html);
      Assert.Contains("onclick=\"dwightSend('consent:tok-l')\">Loose</button>", html);
      var fold = html.Substring(html.IndexOf("consent-more", StringComparison.Ordinal));
      Assert.Contains(WebUtility.HtmlEncode(ConsentCard.SpotAuthority(Spot(), true)), fold);
      Assert.Contains(WebUtility.HtmlEncode(ConsentCard.SpotAuthority(Spot(), false)), fold);
      Assert.Contains("<strong>Place this component?</strong>", fold);
    }

    [Fact]
    public void A_build_cards_scripts_sit_in_the_fold_and_a_run_script_cards_source_stays_on_its_face()
    {
      var s = new ApprovedScript { Region = "A1", Language = BuildScriptTypes.Python, Source = "a = 1" };
      var build = ConsentCard.Html("build", Why, "tok", null, new[] { "A1" }, null, null, "scripts", new[] { s });
      Assert.True(build.IndexOf("consent-more", StringComparison.Ordinal) < build.IndexOf(ConsentCard.ScriptsWarning, StringComparison.Ordinal));

      // ADR-100: the user reads the source before a run_script click, so it is on the face, above the button.
      var run = ConsentCard.RunScriptHtml(Why, "tok", "print(1)", expanded: true);
      Assert.True(run.IndexOf("<pre class=\"consent-source\">", StringComparison.Ordinal) < run.IndexOf("consent-btns", StringComparison.Ordinal));
      Assert.Contains("<details class=\"consent-script\" open>", run);
      Assert.True(run.IndexOf("consent-more", StringComparison.Ordinal) < run.IndexOf(WebUtility.HtmlEncode(FullControl.PreambleNote), StringComparison.Ordinal));
    }

    [Fact]
    public void The_batch_card_keeps_its_ticks_on_the_face_and_its_button_is_make_these_changes()
    {
      Assert.Equal("Make these changes", ConsentCard.EditButton);
      var html = ConsentCard.EditHtml(Why, "tok", null);
      Assert.True(html.IndexOf("edit-rows", StringComparison.Ordinal) < html.IndexOf("consent-btns", StringComparison.Ordinal));
      Assert.Contains(">Make these changes</button>", html);
    }

    [Fact]
    public void The_one_liners_and_the_fold_label_are_pinned_per_kind()
    {
      Assert.Equal("What this does and what it cannot", ConsentCard.MoreLabel);
      Assert.Equal("Checked after it runs; reverts itself if the check fails; one Ctrl-Z.", ConsentCard.OneLiner("build"));
      Assert.Equal("Checked after it runs; reverts itself if the check fails; one Ctrl-Z.", ConsentCard.OneLiner("set_value"));
      Assert.Equal("Checked after it runs; reverts itself if the check fails; one Ctrl-Z.", ConsentCard.OneLiner("edit"));
      Assert.Equal("Checked after it runs; puts your source back if the check fails; one Ctrl-Z.", ConsentCard.OneLiner("edit_script"));
      Assert.Equal("Not checked; one Ctrl-Z for what Grasshopper recorded.", ConsentCard.OneLiner("run_script"));
      Assert.Equal("Nothing is built; one Ctrl-Z removes the map.", ConsentCard.OneLiner("scaffold"));
      Assert.Equal("Nothing on the canvas changes.", ConsentCard.OneLiner("solve"));
      Assert.Equal("Opens Reorganise; changes nothing the definition computes; one Ctrl-Z.", ConsentCard.OneLiner("reorganise"));
      Assert.Equal("Opens Simplify, which previews before it changes anything.", ConsentCard.OneLiner("simplify"));
      foreach (var (kind, html, _) in Waiting())
        Assert.Contains("<p class=\"consent-line\">" + WebUtility.HtmlEncode(ConsentCard.OneLiner(kind == "spot" ? "build" : kind)) + "</p>", html);
    }

    // ---- answered: a receipt row ----------------------------------------------------------------------------------------------

    [Fact]
    public void An_answered_card_becomes_one_row_with_its_act_its_state_and_its_reason_and_the_whole_card_inside()
    {
      foreach (var (kind, html, act) in Waiting())
      {
        var token = Regex.Match(html, "consent:([a-z\\-]+)").Groups[1].Value;
        var receipt = CardReceipt.Answer(html, token, CardReceipt.ApprovedByYou);
        Assert.NotNull(receipt);
        Assert.True(CardReceipt.IsReceipt(receipt), kind);
        Assert.Contains("data-state=\"approved by you\"", receipt);
        Assert.Contains("<span class=\"rc-text\">" + WebUtility.HtmlEncode(act) + "</span>", receipt);
        Assert.Contains("<div class=\"rc-why\"><span class=\"rc-state ok\">approved by you</span> " + WebUtility.HtmlEncode(Why) + "</div>", receipt);   // two words when read aloud or copied, not one
        Assert.Contains("</span> RCPT-REASON</div>", File.ReadAllText(Path.Combine(RepoRoot(), "tools", "check-shell-render.js")));   // the gate's mirror of this row, so the gate reads what the product writes
        Assert.Contains("<div class=\"rc-full\">" + html + "</div>", receipt);   // the card as it was, byte for byte
        Assert.Contains("<svg class=\"ic\"", receipt);
      }
    }

    [Fact]
    public void The_complete_text_of_the_card_is_in_the_dom_in_every_state()
    {
      foreach (var (kind, html, _) in Waiting())
      {
        var token = Regex.Match(html, "consent:([a-z\\-]+)").Groups[1].Value;
        foreach (var state in new[] { CardReceipt.ApprovedByYou, CardReceipt.Superseded })
          Assert.Contains(Text(html), Text(CardReceipt.Answer(html, token, state)));
      }
    }

    [Fact]
    public void A_card_approved_automatically_arrives_as_a_collapsed_receipt_with_its_state()
    {
      var html = ConsentCard.Html("build", Why, "tok", null, Regions, null, null, null, null, ApprovalMode.Never);
      Assert.True(CardReceipt.IsReceipt(html));
      Assert.Contains("data-state=\"approved automatically (Do not ask)\"", html);
      Assert.Contains("aria-expanded=\"false\"", html);
      Assert.DoesNotContain("class=\"receipt open\"", html);
      Assert.Contains("Approved automatically (Do not ask).", html);   // the card's own state line, inside
      Assert.DoesNotContain("consent-btns", html);

      var spot = ConsentCard.SpotHtml(Why, Spot(), "g", "l", ApprovalMode.ScriptsAndDeletions);
      Assert.Contains("data-state=\"approved automatically (Ask for scripts and deletions only)\"", spot);
    }

    [Fact]
    public void Answer_rewrites_only_the_card_with_that_token_and_only_once()
    {
      var card = ConsentCard.Html("solve", Why, "tok-a");
      Assert.Null(CardReceipt.Answer(card, "tok-b", CardReceipt.ApprovedByYou));
      Assert.Null(CardReceipt.Answer("<p>Placed in Spot 1.</p>", "tok-a", CardReceipt.ApprovedByYou));
      var once = CardReceipt.Answer(card, "tok-a", CardReceipt.ApprovedByYou);
      Assert.Null(CardReceipt.Answer(once, "tok-a", CardReceipt.Superseded));   // already a receipt: left as it is

      var superseded = CardReceipt.Answer(card, "tok-a", CardReceipt.Superseded);
      Assert.Contains("data-state=\"superseded\"", superseded);
      Assert.Contains("<span class=\"rc-state\">superseded</span>", superseded);
    }

    [Fact]
    public void A_model_reason_with_markup_stays_escaped_in_the_receipt()
    {
      var card = ConsentCard.Html("solve", "<img src=x onerror=alert(1)>", "tok");
      var receipt = CardReceipt.Answer(card, "tok", CardReceipt.ApprovedByYou);
      Assert.DoesNotContain("<img", receipt);
      Assert.Contains("&lt;img src=x onerror=alert(1)&gt;", receipt);
    }

    // ---- the thread's result lines --------------------------------------------------------------------------------------------

    [Fact]
    public void A_regions_running_line_is_a_receipt_with_the_outcomes_state_and_a_refusal_stays_prose()
    {
      var built = BuildProgress.LineHtml("A1", RegionOutcome.Built, 4, "Checked: placed and wired as declared.");
      var r = CardReceipt.BuildLine(RegionOutcome.Built, built);
      Assert.Contains("data-state=\"built\"", r);
      Assert.Contains("<span class=\"rc-text\">A1 built - 4 components</span>", r);
      Assert.Contains("<div class=\"rc-full\">" + built + "</div>", r);

      Assert.Contains("data-state=\"reverted\"", CardReceipt.BuildLine(RegionOutcome.Reverted, BuildProgress.LineHtml("A1", RegionOutcome.Reverted, 0, null)));
      Assert.Contains("data-state=\"not clean\"", CardReceipt.BuildLine(RegionOutcome.RevertedWithResidue, BuildProgress.LineHtml("A1", RegionOutcome.RevertedWithResidue, 0, null)));
      Assert.Contains("data-state=\"built, with errors\"", CardReceipt.BuildLine(RegionOutcome.BuiltWithErrors, BuildProgress.LineHtml("A1", RegionOutcome.BuiltWithErrors, 2, null)));

      var refused = BuildProgress.LineHtml("A1", RegionOutcome.Refused, 0, "You have used all 3 attempts.");
      Assert.Equal(refused, CardReceipt.BuildLine(RegionOutcome.Refused, refused));
    }

    [Fact]
    public void The_close_line_the_map_and_the_full_control_report_are_receipts()
    {
      var close = "<p class=\"buildclose\"><strong>Build finished.</strong> A1 built and standing.</p>";
      Assert.Contains("data-state=\"done\"", CardReceipt.CloseLine(close, false));
      Assert.Contains("data-state=\"stopped\"", CardReceipt.CloseLine(close, true));
      Assert.Contains("<span class=\"rc-text\">Build finished</span>", CardReceipt.CloseLine(close, false));
      Assert.Contains("data-state=\"laid down\"", CardReceipt.MapLine("<p>Laid down 3 regions.</p>"));
      var report = CardReceipt.ReportLine("<p>The script ran in 0.03 s. Components 24 to 24.</p>");
      Assert.Contains("data-state=\"not checked\"", report);
      Assert.Contains("<span class=\"rc-state warn\">not checked</span>", report);
      Assert.Contains("The script ran in 0.03 s. Components 24 to 24.", report);
    }

    [Fact]
    public void The_first_sentence_is_plain_text_and_bounded()
    {
      Assert.Equal("A1 built - 4 components", CardReceipt.FirstSentence("<p><strong>A1</strong> built - 4 components. One Ctrl-Z.</p>"));
      var longer = "<p>" + string.Join(" ", Enumerable.Repeat("word", 60)) + "</p>";
      Assert.True(CardReceipt.FirstSentence(longer).Length <= 143);
      Assert.EndsWith("...", CardReceipt.FirstSentence(longer));
    }

    // ---- U110: a spot build's verdict, in the region's shape and state words ---------------------------------------------------

    private const string SpotArgs = "{\"region\":\"Spot 5\"}";

    /// <summary>The real writer's verdict, as the reader meets it (test a reader against the writer's real output).</summary>
    private static JsonObject SpotVerdict(bool applied, int newErrors = 0) =>
      BuildVerdict.Build(applied, new List<int> { 24 }, 1, 1, BuildPreservation.Check(new FpDoc(), new FpDoc(), new BuildIdentity()), 1,
                         newErrors, new JsonArray(), region: "Spot 5");

    private const string Placed = "Placed Panel in Spot 5. Checked: placed and wired as declared, nothing existing changed, no new errors.";

    [Fact]
    public void U110_A_spot_that_landed_is_a_receipt_with_the_regions_state_word_and_its_full_text_in_the_fold()
    {
      var html = "<p>" + WebUtility.HtmlEncode(Placed) + "</p>";
      var r = CardReceipt.SpotLine(ToolResult.Success(SpotVerdict(true)), SpotArgs, html);

      Assert.True(CardReceipt.IsReceipt(r));
      Assert.Contains("data-state=\"built\"", r);
      Assert.Contains("<span class=\"rc-text\">Placed Panel in Spot 5</span>", r);
      Assert.Contains("<div class=\"rc-full\">" + html + "</div>", r);          // the whole verdict, byte for byte, in the fold
      Assert.Contains(Text(html), Text(r));

      // The same words and the same shape as the region's receipt beside it.
      var region = CardReceipt.BuildLine(RegionOutcome.Built, BuildProgress.LineHtml("A1", RegionOutcome.Built, 4, "Checked: placed."));
      Assert.Contains("<span class=\"rc-state ok\">built</span>", region);
      Assert.Contains("<span class=\"rc-state ok\">built</span>", r);
    }

    [Fact]
    public void U110_A_spot_with_new_errors_says_so_in_its_state()
    {
      var r = CardReceipt.SpotLine(ToolResult.Success(SpotVerdict(true, newErrors: 2)), SpotArgs, "<p>" + WebUtility.HtmlEncode(Placed) + "</p>");
      Assert.Contains("data-state=\"built, with errors\"", r);
    }

    [Fact]
    public void U110_A_reverted_spot_is_a_receipt_whose_sentence_says_what_happened_not_the_state_again()
    {
      const string why = "The script did not run because its inputs were empty: n read no data. The build was reverted; the canvas is as it was.";
      var failed = ToolResult.Fail(ToolErrors.VerificationFailed, why, SpotVerdict(false));
      var html = SpotAsk.FailedHtml(failed);
      var r = CardReceipt.SpotLine(failed, SpotArgs, html);

      Assert.Contains("data-state=\"reverted\"", r);
      Assert.Contains("<span class=\"rc-state warn\">reverted</span>", r);
      Assert.Contains("<span class=\"rc-text\">The script did not run because its inputs were empty: n read no data</span>", r);
      Assert.Contains("<div class=\"rc-full\">" + html + "</div>", r);          // "Reverted." and the whole reason, in the fold

      // A revert the measurement found unclean takes the region's word for it.
      var dirty = SpotVerdict(false);
      dirty["canvasAsItWas"] = false;
      dirty["residue"] = "Reverted, but 1 object Dwight added still stands: Spot 5; one Ctrl-Z may remove it, or delete it by hand.";
      Assert.Contains("data-state=\"not clean\"", CardReceipt.SpotLine(ToolResult.Fail(ToolErrors.VerificationFailed, why, dirty), SpotArgs, html));
    }

    [Fact]
    public void U110_A_refused_spot_stays_prose_and_so_does_an_envelope_that_cannot_be_read()
    {
      var refused = ToolResult.Fail(ToolErrors.PreconditionRefused, "That address is taken.");
      var html = SpotAsk.FailedHtml(refused);
      Assert.Equal(html, CardReceipt.SpotLine(refused, SpotArgs, html));                 // the user must read a refusal
      Assert.Equal(html, CardReceipt.SpotLine(null, SpotArgs, html));
    }

    [Fact]
    public void U110_The_spot_number_is_still_found_in_the_saved_receipt_so_it_is_never_minted_twice()
    {
      // #878: the mint reads every saved text for the spot addresses it mentions, and a receipt is what is saved now.
      var r = CardReceipt.SpotLine(ToolResult.Success(SpotVerdict(true)), SpotArgs, "<p>" + WebUtility.HtmlEncode(Placed) + "</p>");
      Assert.Contains("Spot 5", SpotMint.MentionedIn(new[] { r }));
    }

    [Fact]
    public void U110_Both_spot_verdict_appends_go_through_the_receipt_and_the_transcript_lines_are_unchanged()
    {
      var cs = File.ReadAllText(Path.Combine(RepoRoot(), "Dwight", "UI", "DwightScaffold.cs"));
      Assert.Contains("CardReceipt.SpotLine(result, args.ToJsonString(), \"<p>\" + System.Net.WebUtility.HtmlEncode(landed) + \"</p>\"), landed);", cs);
      Assert.Contains("CardReceipt.SpotLine(result, args.ToJsonString(), SpotAsk.FailedHtml(result)), SpotAsk.FailedText(result));", cs);
    }

    // ---- U109: the "continuing" line, once ---------------------------------------------------------------------------------------

    [Fact]
    public void U109_The_announcement_follow_up_appends_its_line_once_through_the_report_door()
    {
      // STATED BOUND: the panel is GH-bound, so this counts the appends in its source; the DOM count is for the gate. The double line was
      // two appends side by side, a plugin line and a report with the same text, and the report door alone draws the line and carries the fact.
      var cs = File.ReadAllText(Path.Combine(RepoRoot(), "Dwight", "UI", "DwightScaffold.cs"));
      Assert.Equal(1, Regex.Matches(cs, Regex.Escape("AutoFollowUp.AnnouncementLine")).Count);
      Assert.Contains("AppendReport(wp, doc, threadId, System.Net.WebUtility.HtmlEncode(AutoFollowUp.AnnouncementLine), AutoFollowUp.AnnouncementFact);", cs);
      Assert.DoesNotContain("AppendPluginHtml(wp, doc, threadId, System.Net.WebUtility.HtmlEncode(AutoFollowUp.AnnouncementLine))", cs);

      // The revert's follow-up line has the same shape of risk: it is said once per path, and never beside a report of itself. This used to forbid the
      // report door for the line altogether, which held the 400 after a revert in place: the build's follow-up needed that door, carrying its own fact.
      Assert.DoesNotContain("AppendReport(wp, doc, threadId, System.Net.WebUtility.HtmlEncode(AutoFollowUp.Line), AutoFollowUp.Line)", cs);
      Assert.Contains("AppendReport(wp, doc, threadId, System.Net.WebUtility.HtmlEncode(AutoFollowUp.Line), AutoFollowUp.BuildEndedFact(builtThisTurn, approvedRegions));", cs);
    }

    // ---- the host and the shell ----------------------------------------------------------------------------------------------

    [Fact]
    public void The_click_turns_its_card_into_a_receipt_before_the_echo_and_a_new_card_retires_the_old_ones()
    {
      var cs = File.ReadAllText(Path.Combine(RepoRoot(), "Dwight", "UI", "DwightScaffold.cs"));
      var answer = cs.IndexOf("if (autoMode == null) AnswerCard(wp, doc, threadId, token, CardReceipt.ApprovedByYou);", StringComparison.Ordinal);
      var echo = cs.IndexOf("AppendApprovalEcho(wp, doc, threadId, autoMode != null ? ApprovalMode.AutoUserText : ApprovalMode.ClickUserText);", StringComparison.Ordinal);
      Assert.True(answer > 0 && answer < echo);
      Assert.Contains("foreach (var t in tokens) AnswerCard(wp, doc, threadId, t, CardReceipt.Superseded);", cs);
      Assert.Contains("if (pendingCards.Count > 0) ApplySuperseded(wp, doc, threadId);", cs);
      Assert.Equal(2, Regex.Matches(cs, "QueueSuperseded\\(threadId\\);").Count);   // both mint doors

      var history = File.ReadAllText(Path.Combine(RepoRoot(), "Dwight", "Session", "CommandHistory.cs"));
      Assert.Contains("if (m == null || !string.IsNullOrEmpty(m.Src) || string.IsNullOrEmpty(m.Html)) continue;", history);   // only Dwight's chrome
    }

    [Fact]
    public void The_shell_opens_a_receipt_on_click_swaps_an_answered_card_and_leaves_the_echo_unrendered_after_a_receipt()
    {
      var html = File.ReadAllText(Path.Combine(RepoRoot(), "Dwight", "Resources", "panel-shell.html"));
      Assert.Contains("function dwightCardAnswered(token, html)", html);
      Assert.Contains("r.classList.toggle('open')", html);
      var css = File.ReadAllText(Path.Combine(RepoRoot(), "Dwight", "Resources", "dwight-components.css"));
      Assert.Contains(".receipt .rc-full{display:none;", css);
      Assert.Contains(".receipt.open .rc-full{display:block}", css);
      // U111: the shell hides an echo by the host's mark and knows no words to match; only the approval's door marks one
      Assert.DoesNotContain("_ECHO", html);
      Assert.DoesNotContain("'" + ApprovalMode.ClickUserText + "'", html);
      Assert.Contains("function _echoAfterReceipt(m, prev){ return !!m.echo &&", html);
      var scaffold = File.ReadAllText(Path.Combine(RepoRoot(), "Dwight", "UI", "DwightScaffold.cs"));
      Assert.Contains("AppendApprovalEcho(wp, doc, threadId, autoMode != null ? ApprovalMode.AutoUserText : ApprovalMode.ClickUserText);", scaffold);
      Assert.DoesNotContain("AppendUserText(wp, doc, threadId, autoMode", scaffold);

      var panel = File.ReadAllText(Path.Combine(RepoRoot(), "Dwight", "UI", "WebPanel.cs"));
      Assert.Contains("Run($\"dwightCardAnswered({Js(token)},{Js(receiptHtml)})\")", panel);
    }

    private static string RepoRoot()
    {
      var dir = new DirectoryInfo(AppContext.BaseDirectory);
      while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Dwight.sln"))) dir = dir.Parent;
      Assert.True(dir != null, "the repository root (Dwight.sln) could not be found from " + AppContext.BaseDirectory);
      return dir.FullName;
    }
  }
}
