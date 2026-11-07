using Dwight.Data;
using Xunit;

namespace Dwight.Data.Tests
{
  /// <summary>
  /// Phase-8 WP-0 (#88/#89/#93): the output-diff oracle's feedback is CLASS-ONLY. It names the output, its failure
  /// CLASS (order / value / structure), and value-free structural facts (item + branch counts) - and NEVER the
  /// expected values, a permutation, or a position. The type makes the leak impossible by construction: it only
  /// ever RECEIVES counts, so it cannot render a value.
  ///
  /// #105 pins the #89 LITMUS as a TEST, not a review habit: given sample data, assert no digit, position, or
  /// permutation FROM THE SAMPLE appears in the rendered order/value feedback.
  /// </summary>
  public class RefineFeedbackClassTests
  {
    [Fact]
    public void An_order_mismatch_says_wrong_order_and_forbids_reorder_to_fit()
    {
      var fb = RefineFeedback.MismatchClassified(new[]
      {
        new OutputDiff("segments", MismatchKind.Order, itemsBefore: 5, itemsAfter: 5, pathsBefore: 1, pathsAfter: 1),
      });
      var prompt = fb.ToPrompt().ToLowerInvariant();

      Assert.Contains("segments", prompt);        // the named output (the per-output correspondence to-script has)
      Assert.Contains("order", prompt);           // the CLASS
      // #93(b): forbid the post-hoc reorder-to-fit - re-derive the enumeration, do not bolt on a sort/reverse.
      Assert.Contains("enumerat", prompt);
      Assert.True(prompt.Contains("do not") && (prompt.Contains("sort") || prompt.Contains("reverse")),
        "the order feedback must forbid appending a sort/reverse to force agreement (#93b)");
    }

    [Fact]
    public void A_value_mismatch_names_the_class_but_never_the_values_the_litmus()
    {
      // the facts carry COUNTS only; the classifier never hands the feedback a value. So even though the real
      // outputs held 47.3 and 12.0, those strings CANNOT appear - the type has no field to carry them (#89).
      var fb = RefineFeedback.MismatchClassified(new[]
      {
        new OutputDiff("area", MismatchKind.Value, itemsBefore: 3, itemsAfter: 3, pathsBefore: 1, pathsAfter: 1),
      });
      var prompt = fb.ToPrompt();

      Assert.Contains("area", prompt);
      Assert.DoesNotContain("47.3", prompt);   // no expected value leaks (ADR-095 / #71)
      Assert.DoesNotContain("12.0", prompt);
      Assert.Contains("hardcode", prompt.ToLowerInvariant());   // and it still forbids hardcoding to pass
    }

    [Fact]
    public void A_structure_mismatch_reports_the_counts_which_are_value_free_facts()
    {
      var fb = RefineFeedback.MismatchClassified(new[]
      {
        new OutputDiff("pts", MismatchKind.Structure, itemsBefore: 7, itemsAfter: 4, pathsBefore: 1, pathsAfter: 1),
      });
      var prompt = fb.ToPrompt();

      Assert.Contains("pts", prompt);
      // counts ARE ADR-095-legal facts (#88): "4 items, expected 7" is diagnostic, not the values themselves.
      Assert.Contains("7", prompt);
      Assert.Contains("4", prompt);
    }

    [Fact]
    public void The_interface_is_never_up_for_renegotiation()
    {
      var fb = RefineFeedback.MismatchClassified(new[]
      {
        new OutputDiff("x", MismatchKind.Order, itemsBefore: 2, itemsAfter: 2, pathsBefore: 1, pathsAfter: 1),
      });
      Assert.Contains("interface", fb.ToPrompt().ToLowerInvariant());
    }

    [Fact]
    public void Multiple_outputs_each_get_their_own_class_line()
    {
      var fb = RefineFeedback.MismatchClassified(new[]
      {
        new OutputDiff("a", MismatchKind.Order, 5, 5, 1, 1),
        new OutputDiff("b", MismatchKind.Value, 3, 3, 1, 1),
      });
      var prompt = fb.ToPrompt().ToLowerInvariant();
      Assert.Contains("a", prompt);
      Assert.Contains("b", prompt);
      Assert.Contains("order", prompt);
      Assert.Contains("value", prompt);
    }

    [Fact]
    public void The_class_aware_mismatch_is_still_an_output_mismatch_kind()
    {
      var fb = RefineFeedback.MismatchClassified(new[] { new OutputDiff("a", MismatchKind.Order, 1, 1, 1, 1) });
      Assert.Equal(RefineKind.OutputMismatch, fb.Kind);
    }
  }
}
