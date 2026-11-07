using System;
using System.Collections.Generic;
using System.Linq;

namespace Dwight.Agent
{
  /// <summary>What a build turn was actually authorised to do.</summary>
  internal enum BuildScopeOutcome
  {
    /// <summary>Build exactly these regions, in this order.</summary>
    Approved = 0,
    /// <summary>Some of what was approved is still there and some is gone. Build the survivors, and SAY
    /// which are missing - narrowing is safe, going quiet about it is not.</summary>
    PartlyMissing = 1,
    /// <summary>Nothing the card named is on the canvas any more. Do nothing and say so.</summary>
    Refuse = 2,
  }

  internal sealed class BuildScopeResolution
  {
    public BuildScopeOutcome Outcome { get; set; }
    /// <summary>The addresses to build, in address order. Never wider than what was approved.</summary>
    public IReadOnlyList<string> Regions { get; set; } = new string[0];
    /// <summary>Approved addresses that no longer name a region on the canvas.</summary>
    public IReadOnlyList<string> Missing { get; set; } = new string[0];
    public string Message { get; set; } = "";
  }

  /// <summary>
  /// WHICH REGIONS A BUILD TURN MAY WRITE TO - <see cref="VerbScope"/>'s sibling in ADDRESS space
  /// (Phase-8 WP-4 Stage C; #338/#355).
  ///
  /// #338's law is that a consent card is a contract about SCOPE: the verb runs on the scope the card
  /// stated, or not at all. For the five verbs that scope is a set of guids and <see cref="VerbScope"/>
  /// holds it. A build's scope is a set of ADDRESSES, so it needs the same law expressed in the namespace
  /// a build is actually aimed by - and it needs it for the same reason: correct execution of the wrong
  /// scope is still the wrong scope, and a card that named "A1 through A3" must never produce a build of
  /// the whole map.
  ///
  /// THE THREE RULES, in the same shape as VerbScope's:
  ///
  ///   1. THERE IS NO "EVERYTHING" OUTCOME, by construction. This resolves to a LIST, and the list can
  ///      only ever be a subset of what was approved. A build turn cannot reach every region even if
  ///      every other piece of state is lost, because there is no value here that means "all of them".
  ///   2. AN APPROVED SCOPE THAT RESOLVES TO NOTHING REFUSES. It never falls back to the map, and never
  ///      to "whatever regions exist". Scope is identity, and #208's rule for identity is resolve exactly
  ///      or refuse.
  ///   3. A PARTIAL LOSS BUILDS THE SURVIVORS AND NAMES THE LOSS. Narrowing is safe - it is strictly less
  ///      than the user approved - but going quiet about it is not: that is #327's rule for anchors ("frame
  ///      the survivors and SAY which are gone, never silently frame fewer") arriving at a mutation, where
  ///      it matters more.
  ///
  /// GH-free (ADR-091). The caller supplies what the canvas currently carries.
  /// </summary>
  internal static class BuildScope
  {
    public static BuildScopeResolution Resolve(IReadOnlyList<string> approved, IReadOnlyList<string> onCanvas,
                                               string mintedSpot = null)
    {
      var wanted = Clean(approved);
      var live = new HashSet<string>(Clean(onCanvas), StringComparer.Ordinal);

      // ---- THE SPOT CASE (U18) ------------------------------------------------------------------
      //
      // A spot is the one region approved BEFORE its group exists: the build creates it. Every other
      // region must be live on the canvas at click time, and that rule is #338's machinery - so rather
      // than relax it, a spot arrives through ITS OWN PARAMETER, minted by the plugin and carried in its
      // own field on the seed. "Approve an address that is not on the canvas" is therefore not
      // expressible for a plan region at all: the shape of the call refuses it, not a check inside it.
      //
      // Three conditions, all of them refusals rather than filters, because this is the only door in the
      // scope gate that admits something the canvas cannot confirm.
      if (!string.IsNullOrEmpty(mintedSpot))
      {
        if (!ScaffoldAddress.IsSpot(mintedSpot))
          return Refuse("\"" + mintedSpot + "\" is not a spot address, so there is nothing to build. " +
                        "Spots are minted by Dwight and look like \"Spot 1\".");

        // A COLLISION, NAMED. The mint continues past every spot in use, so a spot that already exists
        // means the canvas changed under the card - and building into a group somebody else's work is
        // already in is the one thing the whole scope gate exists to prevent.
        if (live.Contains(mintedSpot))
          return Refuse(mintedSpot + " is already on the canvas, so Dwight will not create it again. " +
                        "Ask again and it will offer a fresh one.");

        // ONE CARD, ONE KIND OF SCOPE. A card that approved regions AND a new spot would be a contract
        // about two different things, and #338 is that the sentence the user read IS the scope.
        if (wanted.Count > 0)
          return Refuse("That approval names both scaffold regions and a new spot, which is two different " +
                        "kinds of scope on one card. Nothing was built. Ask for them separately.");

        return new BuildScopeResolution
        {
          Outcome = BuildScopeOutcome.Approved,
          Regions = new List<string> { mintedSpot },
        };
      }

      if (wanted.Count == 0)
        return new BuildScopeResolution
        {
          Outcome = BuildScopeOutcome.Refuse,
          Message = "No region was approved for this build, so there is nothing to build. Ask again and " +
                    "name the regions.",
        };

      var found = wanted.Where(live.Contains).ToList();
      var missing = wanted.Where(a => !live.Contains(a)).ToList();

      if (found.Count == 0)
        return new BuildScopeResolution
        {
          Outcome = BuildScopeOutcome.Refuse,
          Missing = missing,
          // The refusal's own copy, never a neighbouring route's (#347): a build scope that resolves to
          // nothing cannot be repaired by selecting something or by widening - only by a fresh map.
          Message = "The region" + (missing.Count == 1 ? " " : "s ") + string.Join(", ", missing) +
                    (missing.Count == 1 ? " is" : " are") + " no longer on the canvas, so there is nothing " +
                    "to build. Ask me to look at the map again.",
        };

      if (missing.Count > 0)
        return new BuildScopeResolution
        {
          Outcome = BuildScopeOutcome.PartlyMissing,
          Regions = found,
          Missing = missing,
          Message = string.Join(", ", missing) + (missing.Count == 1 ? " is" : " are") +
                    " no longer on the canvas and will be skipped; building " + string.Join(", ", found) + ".",
        };

      return new BuildScopeResolution { Outcome = BuildScopeOutcome.Approved, Regions = found };
    }

    private static BuildScopeResolution Refuse(string message) =>
      new BuildScopeResolution { Outcome = BuildScopeOutcome.Refuse, Message = message };

    /// <summary>Addresses only, de-duplicated, in ADDRESS order (#286 - never text order, or A10 builds
    /// before A2 and the order the addresses exist to express is lost at the moment it matters most).
    /// BOTH KINDS (U18): a spot is a place this turn may build, and the live set must carry spots or the
    /// collision check above could not see one.</summary>
    private static List<string> Clean(IReadOnlyList<string> addresses) =>
      (addresses ?? new string[0])
        .Where(a => ScaffoldAddress.IsRegionAddress(a))
        .Distinct(StringComparer.Ordinal)
        .OrderBy(a => ScaffoldAddress.SortKey(a).Letters, StringComparer.Ordinal)
        .ThenBy(a => ScaffoldAddress.SortKey(a).Number)
        .ToList();
  }
}
