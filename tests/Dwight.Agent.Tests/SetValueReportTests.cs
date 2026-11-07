using System.Collections.Generic;
using System.Linq;
using Dwight.Agent;
using Dwight.Llm;
using Dwight.Session;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// Phase-9 U4: how a set_value outcome reaches the model. The plugin applied the change on the user's
  /// approval, so no tool result exists to carry the verdict; a REPORT record renders as Dwight on screen
  /// and enters the transcript in the USER turn, attributed - a trailing assistant turn would be a prefill.
  /// </summary>
  public class SetValueReportTests
  {
    private static SetValueChange Change() => new SetValueChange
    {
      Id = 42, Type = "Number Slider", Nick = "Count", Before = "0.25 (0 to 1, float)", After = "40 (0 to 100, integer)",
    };

    [Fact]
    public void AN_APPLIED_REPORT_NAMES_THE_CHANGE_ITS_TWO_LEGS_AND_THE_UNDO_AND_IS_ATTRIBUTED()
    {
      var r = ToolResult.Success(SetValueCore.Verdict(true, Change(), true, true, null));
      var text = SetValueReport.Text(r);
      Assert.StartsWith("[Applied by Dwight on the user's approval]", text);
      Assert.Contains("Number Slider \"Count\" (42) from 0.25 (0 to 1, float) to 40 (0 to 100, integer)", text);
      Assert.Contains("preserved", text);
      Assert.Contains("complete", text);
      Assert.Contains("Ctrl-Z", text);

      var html = SetValueReport.Html(r);
      Assert.Contains("<strong>Applied.</strong>", html);
      Assert.Contains("(42)", html);
    }

    [Fact]
    public void A_REVERTED_REPORT_SAYS_SO_WITH_THE_MISMATCH_AND_ESCAPES_IT()
    {
      var change = Change(); change.Nick = "<b>x</b>";
      var r = ToolResult.Fail(ToolErrors.VerificationFailed, "reverted",
        SetValueCore.Verdict(false, change, true, false, "the slider holds 39, not the declared 40"));
      Assert.Contains("REVERTED", SetValueReport.Text(r));
      Assert.Contains("39", SetValueReport.Text(r));
      Assert.Contains("&lt;b&gt;x&lt;/b&gt;", SetValueReport.Html(r));
      Assert.DoesNotContain("<b>x</b>", SetValueReport.Html(r));
    }

    [Fact]
    public void A_REPORT_RECORD_ENTERS_THE_TRANSCRIPT_AS_A_USER_TURN_SO_THE_NEXT_TURN_CAN_START()
    {
      var stored = new List<StoredMessage>
      {
        new StoredMessage { Who = "me", Html = "Yes, go ahead.", Src = "Yes, go ahead." },
        new StoredMessage { Who = ChatToTranscript.Report, Html = "<p>Applied.</p>", Src = "[Applied by Dwight on the user's approval] x" },
        new StoredMessage { Who = "dw", Html = "chrome", Src = null },
      };
      var msgs = ChatToTranscript.Build(stored);
      Assert.Equal(2, msgs.Count);
      Assert.All(msgs, m => Assert.Equal(LlmRole.User, m.Role));
      Assert.Contains("[Applied by Dwight", msgs.Last().Blocks.First().Text);
    }

    // ---- #683 (6c): every result the APPLIER returns, read by the report exactly as the applier builds it --------

    /// <summary>The applier's REVERTED shapes: an undo record was taken and undone. Never "from ? to ?".</summary>
    [Fact]
    public void A_REVERT_AFTER_AN_ERROR_SAYS_IT_COULD_NOT_BE_VERIFIED_AND_THE_CANVAS_IS_AS_IT_WAS()
    {
      var r = SetValueCore.RevertedAfterError("boom");

      Assert.StartsWith("[Attempted by Dwight on the user's approval, and REVERTED]", SetValueReport.Text(r));
      Assert.Contains("could not be verified", SetValueReport.Text(r));
      Assert.Contains("the canvas is as it was", SetValueReport.Text(r));
      Assert.DoesNotContain("?", SetValueReport.Text(r));
      Assert.Contains("<strong>Reverted.</strong>", SetValueReport.Html(r));
    }

    [Fact]
    public void A_VERIFICATION_REVERT_NAMES_THE_CHANGE_AND_ITS_MISMATCH()
    {
      var r = SetValueCore.Reverted(Change(), preserved: true, complete: false, mismatch: "the slider holds 39, not the declared 40");

      Assert.Contains("REVERTED", SetValueReport.Text(r));
      Assert.Contains("from 0.25 (0 to 1, float) to 40 (0 to 100, integer)", SetValueReport.Text(r));
      Assert.Contains("the slider holds 39", SetValueReport.Text(r));
    }

    /// <summary>
    /// THE FINDING (6c): the applier's NOT-APPLIED shapes - no document, could not schedule, the target gone at apply, an
    /// error before the undo record existed - changed nothing. The report read every non-success as "Attempted... and
    /// REVERTED", a claim about the canvas that is false for each of them.
    /// </summary>
    [Fact]
    public void EVERY_NOT_APPLIED_SHAPE_IS_REPORTED_AS_NOT_APPLIED_NEVER_REVERTED()
    {
      var shapes = new[]
      {
        SetValueCore.NoDocument(),
        SetValueCore.CouldNotSchedule("the solver is busy"),
        SetValueCore.GoneAtApply(Change()),
        SetValueCore.NotAppliedAfterError("boom"),
      };

      foreach (var r in shapes)
      {
        var text = SetValueReport.Text(r);
        Assert.StartsWith("[Not applied: the user approved this set_value, and Dwight changed nothing]", text);
        Assert.DoesNotContain("REVERTED", text);
        Assert.Contains("<strong>Not applied.</strong>", SetValueReport.Html(r));
      }
    }

    /// <summary>The envelope an MCP client reads for the target-gone shape says "not_applied", never "reverted".</summary>
    [Fact]
    public void THE_GONE_AT_APPLY_ENVELOPE_SAYS_NOT_APPLIED()
    {
      var env = System.Text.Json.Nodes.JsonNode.Parse(SetValueCore.GoneAtApply(Change()).ToJson());

      Assert.Equal("not_applied", env["error"]["detail"]["status"].GetValue<string>());
      Assert.DoesNotContain("reverted", env.ToJsonString());
    }

    [Fact]
    public void AN_APPLIED_RESULT_FROM_THE_APPLIERS_CONSTRUCTOR_IS_REPORTED_AS_APPLIED()
    {
      Assert.StartsWith("[Applied by Dwight on the user's approval]", SetValueReport.Text(SetValueCore.Applied(Change())));
    }

    // ---- #682: a click that changes nothing is a report the model reads, never chrome ------------------------

    /// <summary>
    /// #624's lesson inside set_value. The click re-validates against the canvas as it is now, and can refuse: the target
    /// gone, its type changed, the restore rule now failing. Rendered as chrome, that refusal is invisible to the model,
    /// whose last act was raising the card - so it could believe the change ran. The refusal is a REPORT: attributed in
    /// its text, and in the user turn.
    /// </summary>
    [Fact]
    public void A_CLICK_REFUSAL_IS_AN_ATTRIBUTED_REPORT_THE_MODEL_READS()
    {
      const string reason = "Component 42 is no longer on the canvas (it was deleted, or an undo removed it).";

      var text = SetValueReport.NotAppliedText(reason);
      Assert.StartsWith("[Not applied: the user approved this set_value, and Dwight changed nothing] ", text);
      Assert.EndsWith(reason, text);

      var stored = new List<StoredMessage>
      {
        new StoredMessage { Who = "me", Html = "Yes, go ahead.", Src = "Yes, go ahead." },
        new StoredMessage { Who = ChatToTranscript.Report, Html = SetValueReport.NotAppliedHtml(reason), Src = text },
      };
      var last = ChatToTranscript.Build(stored).Last();
      Assert.Equal(LlmRole.User, last.Role);
      Assert.Equal(text, last.Blocks.First().Text);
    }

    [Fact]
    public void A_CLICK_REFUSAL_ON_SCREEN_IS_ESCAPED()
    {
      var html = SetValueReport.NotAppliedHtml("the <b>Count</b> slider is now a Panel");

      Assert.StartsWith("<p><strong>Not applied.</strong> ", html);
      Assert.Contains("&lt;b&gt;Count&lt;/b&gt;", html);
      Assert.DoesNotContain("<b>Count</b>", html);
    }
  }
}
