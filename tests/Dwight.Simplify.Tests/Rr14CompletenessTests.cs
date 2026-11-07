using System.Collections.Generic;
using Dwight.Simplify;
using Xunit;

namespace Dwight.Simplify.Tests
{
  /// <summary>RR14 #27: the completeness backstop's decision. Completeness proves every ACCEPTED change actually
  /// took effect; an accepted change that no-ops (an externalize slider left unwired, a removal that did not
  /// happen) is a FAILED apply that must revert - the airtight guard for the inert-no-op class that also backstops
  /// WP-3 Merge insertion and WP-4 to-script. The GH observation (reading a slider's live recipients) is
  /// runtime-verified; this pins the decision.</summary>
  public class Rr14CompletenessTests
  {
    [Fact]
    public void An_accepted_change_with_no_effect_triggers_a_revert()
    {
      // The RR14 failure: an accepted externalize produced an UNWIRED slider (no effect), yet the run committed.
      // Completeness must report that no-op so the apply reverts instead of falsely "verifying".
      var effects = new List<(bool, string)>
      {
        (true, "a removed component is gone"),
        (false, "an externalized slider was left unconnected"),   // the no-op
        (true, "another removal is gone"),
      };
      Assert.Equal("an externalized slider was left unconnected", Completeness.FirstNoOp(effects));
    }

    [Fact]
    public void Every_accepted_change_taking_effect_passes()
    {
      var effects = new List<(bool, string)> { (true, "slider wired"), (true, "removed gone") };
      Assert.Null(Completeness.FirstNoOp(effects));
    }

    [Fact]
    public void A_removal_that_did_not_happen_is_caught()
    {
      var effects = new List<(bool, string)> { (false, "a component that should have been removed is still present") };
      Assert.Equal("a component that should have been removed is still present", Completeness.FirstNoOp(effects));
    }

    [Fact]
    public void No_accepted_effects_is_vacuously_complete()
    {
      Assert.Null(Completeness.FirstNoOp(new List<(bool, string)>()));
      Assert.Null(Completeness.FirstNoOp(null));
    }
  }
}
