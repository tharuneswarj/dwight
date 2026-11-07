using System.Collections.Generic;

namespace Dwight.Simplify
{
  /// <summary>RR14 #27 (GH-free): the completeness backstop's DECISION, separated from the live-doc OBSERVATION so
  /// it is headless-testable. Completeness is the universal guard for the inert-no-op class - equivalence proves
  /// "nothing unintended changed", completeness proves "every ACCEPTED change actually took effect". An accepted
  /// change that no-ops (an externalize slider left unwired, a removal that did not happen) is a FAILED apply that
  /// must revert, NOT a false "verified". It also backstops WP-3's Merge insertion and WP-4's to-script.</summary>
  internal static class Completeness
  {
    /// <summary>Given each accepted change's observed effect, return the reason of the FIRST that did NOT take
    /// effect (-> revert + surface), or null when every accepted change landed.</summary>
    public static string FirstNoOp(IReadOnlyList<(bool TookEffect, string Reason)> effects)
    {
      if (effects == null) return null;
      foreach (var e in effects) if (!e.TookEffect) return e.Reason;
      return null;
    }
  }
}
