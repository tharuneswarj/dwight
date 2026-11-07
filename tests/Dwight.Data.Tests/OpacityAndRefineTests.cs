using Dwight.Data;
using Xunit;

namespace Dwight.Data.Tests
{
  /// <summary>
  /// RR17 #36: the rule behind "can Dwight SHOW the model what this component does?". The bug it closes was not
  /// subtle in hindsight - a script node reached the model as {"type":"GhPython Script"} and nothing else, and it
  /// was then asked to reproduce that node's behaviour. The fix has two halves, and BOTH are tested here as one
  /// rule: a readable script is transparent (its source is sent), and everything Dwight cannot read is opaque.
  /// </summary>
  public class ComponentOpacityTests
  {
    [Fact]
    public void A_readable_script_is_transparent_because_its_source_can_be_sent()
    {
      Assert.False(ComponentOpacity.IsOpaque(isCluster: false, isScript: true, sourceReadable: true, isCoreLibrary: false));
    }

    [Fact]
    public void An_unreadable_script_is_opaque_rather_than_treated_as_an_empty_one()
    {
      // the dangerous failure: reading "no code" as "does nothing". A locked / encrypted / oversized script must
      // be reported as a black box, not silently assumed to be a pass-through.
      Assert.True(ComponentOpacity.IsOpaque(isCluster: false, isScript: true, sourceReadable: false, isCoreLibrary: true));
    }

    [Fact]
    public void A_cluster_is_opaque_because_its_subgraph_is_never_sent()
    {
      Assert.True(ComponentOpacity.IsOpaque(isCluster: true, isScript: false, sourceReadable: false, isCoreLibrary: true));
    }

    [Fact]
    public void A_third_party_plugin_component_is_opaque_its_logic_lives_only_in_its_assembly()
    {
      Assert.True(ComponentOpacity.IsOpaque(isCluster: false, isScript: false, sourceReadable: false, isCoreLibrary: false));
    }

    [Fact]
    public void An_ordinary_core_component_is_transparent()
    {
      // the over-refusal guard: core Grasshopper components live in MANY assemblies (CurveComponents,
      // MathComponents, ...), which is why the rule keys on the CORE-LIBRARY flag and not an assembly name.
      // Getting this wrong would refuse every real definition.
      Assert.False(ComponentOpacity.IsOpaque(isCluster: false, isScript: false, sourceReadable: false, isCoreLibrary: true));
    }
  }

  /// <summary>
  /// RR17 #37: the failure, structured for a RETRY rather than for a label. The Phase-8 refine loop hands the model
  /// its own compiler error / per-output diff; these assert the feedback is specific enough to act on and always
  /// carries the invariant that the interface is not up for renegotiation.
  /// </summary>
  public class RefineFeedbackTests
  {
    [Fact]
    public void A_compile_error_hands_the_model_back_its_own_message()
    {
      var prompt = RefineFeedback.Compile("; expected [1:12]").ToPrompt();

      Assert.Contains("did not compile", prompt);
      Assert.Contains("; expected [1:12]", prompt);       // the specifics, not a paraphrase
      Assert.Contains("interface", prompt);               // and the interface is NOT up for renegotiation
    }

    [Fact]
    public void An_output_mismatch_names_the_outputs_that_differed()
    {
      var prompt = RefineFeedback.Mismatch(new[] { "panelAreas", "siteCurves" }).ToPrompt();

      Assert.Contains("panelAreas", prompt);
      Assert.Contains("siteCurves", prompt);
      Assert.Contains("structure", prompt);   // the tree shape counts too, not only the values
    }

    [Fact]
    public void A_runtime_error_is_distinguished_from_a_compile_error()
    {
      Assert.Equal(RefineKind.RuntimeError, RefineFeedback.Runtime("name 'nope' is not defined").Kind);
      Assert.Equal(RefineKind.CompileError, RefineFeedback.Compile("; expected").Kind);
    }

    [Fact]
    public void An_incomplete_apply_reports_the_reason_it_did_not_take_effect()
    {
      var fb = RefineFeedback.Incomplete("an input of the script was left unconnected");

      Assert.Equal(RefineKind.Incomplete, fb.Kind);
      Assert.Contains("left unconnected", fb.ToPrompt());
    }

    [Fact]
    public void Feedback_never_throws_on_missing_detail()
    {
      // a failure path with nothing to say must still produce usable text, never a null-reference on the retry.
      Assert.False(string.IsNullOrWhiteSpace(RefineFeedback.Compile(null).ToPrompt()));
      Assert.False(string.IsNullOrWhiteSpace(RefineFeedback.Mismatch(null).ToPrompt()));
    }
  }
}
