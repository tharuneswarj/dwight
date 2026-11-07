using System;
using System.Collections.Generic;
using System.Linq;

namespace Dwight.Agent
{
  /// <summary>
  /// WHAT AN ADDRESS IS - in one place, because three things depend on agreeing about it (WP-4 Stage A).
  ///
  /// An address is what the user says out loud ("build A1 through A3"), what the plugin assigns from the
  /// dependency layering, what the canvas carries in a group's name (#221b), and what the stored map is
  /// joined on. Four consumers of one convention: the addressing pass that mints it, the re-stack that
  /// orders by it, the reader that parses it back off the canvas, and WP-4's build that is aimed by it.
  ///
  /// A SECOND DEFINITION OF IT WOULD BE THE #248 CLASS - two halves of one contract disagreeing, with
  /// nothing to notice. The numeric-parse in particular has already been got wrong once at authorship
  /// (#286: sorting addresses as TEXT puts A10 before A2, which breaks the one property the address
  /// exists to carry - "read this first"), so it lives here and every caller uses it.
  ///
  /// PARSING IS EXACT OR NOTHING (#208). A group the user made, a group called "A1" with no name, a
  /// lower-case imitation - none of them are scaffold regions, and each is refused rather than coerced
  /// into one. A build aimed at a mis-parsed address would be a mutation in the wrong place.
  ///
  /// GH-free (ADR-091).
  /// </summary>
  /// <summary>What KIND of region an address names. Two shapes, deliberately disjoint - see
  /// <see cref="ScaffoldAddress"/>.</summary>
  internal enum AddressKind
  {
    /// <summary>Not an address of either kind.</summary>
    None = 0,
    /// <summary>A1, B2 - a region of a scaffold MAP, whose letter is its layer in the plan's flow.</summary>
    Plan = 1,
    /// <summary>Spot 1 - one small placement the user approved on its own card, belonging to no map (U18).</summary>
    Spot = 2,
  }

  internal static class ScaffoldAddress
  {
    /// <summary>What separates an address from its name in a group's nickname. It is the producer's own
    /// format (<c>ScaffoldRegion.GroupName</c>); parsing and building agree because both are here.</summary>
    public const string Separator = " - ";

    // ================================================================================================
    // THE SECOND SHAPE (U18), AND WHY IT IS A SHAPE RATHER THAN MORE LETTERS
    //
    // U18 lets Dwight place a couple of components without a map first - the friction Tharun measured was
    // that ONE Panel cost a scaffold card, a build card and two approvals. That placement still needs an
    // ADDRESS, and not for tidiness: FOUR things key on the address string and every one of them fails
    // SILENTLY without it - BuildLedger.CommitBuild records nothing (so it is never rebuildable),
    // CountAttempt no-ops (so the three-attempt cap does not apply), Resolve returns no claim, and
    // GhRegionTidy is keyed by address so the layout pass is skipped.
    //
    // AND THE ADDRESS IS THE ENFORCEMENT, not bookkeeping. BuildLedger.MayBuild refuses any address
    // outside the turn's approved set; an un-addressed build had no such check at all. Giving the
    // placement a real address closes that by construction, with machinery that already exists.
    //
    // SO WHY NOT JUST MINT "A4"? Because an address is a CLAIM. #732 settled that the letter MEANS the
    // dependency layer, and every tool and card reads it that way. A placement that belongs to no map has
    // no layer, so "A4" asserts a position in a flow that does not exist - and a model reading
    // get_scaffold_map would see A4 after A3 and reason about it as a downstream stage. A FABRICATED
    // DEPENDENCY is the class ADR-058 exists to stop, and it would propagate into the stored map, the card
    // sentence and the conversation before anyone noticed.
    //
    // THE TWO SHAPES ARE DISJOINT, AND THAT IS ASSERTED BOTH WAYS BY TEST, never by inspection: a spot
    // address must fail the plan parser and a plan address must fail the spot parser. That pair is what
    // fails if anyone later "simplifies" the two together.
    //
    // ONE DEFINITION, HERE (#248). The previous second definition of an address cost #286.
    // ================================================================================================

    /// <summary>The word a spot address begins with. A whole word, with a space: it can never be the start
    /// of a plan address, which is letters immediately followed by digits.</summary>
    public const string SpotPrefix = "Spot ";

    /// <summary>
    /// A SPOT address - <c>Spot 1</c>, <c>Spot 2</c> - or false.
    ///
    /// Structurally disjoint from <see cref="TryParse"/> by construction rather than by agreement: a plan
    /// address is <c>[A-Z]+[0-9]+</c> with nothing between the halves, and the space after "Spot" cannot
    /// appear there. Neither parser can accept the other's input, and a test holds both directions.
    /// </summary>
    public static bool TrySpot(string address, out int number)
    {
      number = 0;
      if (string.IsNullOrEmpty(address)) return false;
      if (!address.StartsWith(SpotPrefix, StringComparison.Ordinal)) return false;

      var digits = address.Substring(SpotPrefix.Length);
      if (digits.Length == 0 || !digits.All(char.IsDigit)) return false;
      if (!int.TryParse(digits, out number)) return false;
      return number >= 1;                                    // Spot 0 is nobody's address
    }

    public static bool IsSpot(string address) => TrySpot(address, out _);

    /// <summary>Which kind, or <see cref="AddressKind.None"/>. The one place that decides.</summary>
    public static AddressKind Kind(string address) =>
      IsAddress(address) ? AddressKind.Plan : IsSpot(address) ? AddressKind.Spot : AddressKind.None;

    /// <summary>
    /// ANY region Dwight addresses - a plan region or a spot.
    ///
    /// Most callers mean THIS rather than <see cref="IsAddress"/>: "is this a place Dwight can build,
    /// rebuild, tidy, refuse or report on". <see cref="IsAddress"/> keeps its narrower meaning - "is this a
    /// region of a MAP" - and the callers that genuinely need that keep using it. Every call site was
    /// decided one at a time when this was introduced; none was converted by search-and-replace, because
    /// the two questions look identical and are not.
    /// </summary>
    public static bool IsRegionAddress(string address) => Kind(address) != AddressKind.None;

    /// <summary>
    /// The next free spot number, continuing past every spot already spoken for (#720's law, which is the
    /// same law: a second placement must not mint a name something else already answers to). Unparseable
    /// entries are skipped rather than thrown on - the set is joined from group nicknames.
    /// </summary>
    public static string NextSpot(IEnumerable<string> inUse)
    {
      var highest = 0;
      foreach (var address in inUse ?? Enumerable.Empty<string>())
        if (TrySpot(address, out var n) && n > highest) highest = n;
      return SpotPrefix + (highest + 1);
    }

    /// <summary>
    /// Split an address into its LAYER letters and its NUMBER. Upper case only: the plugin assigns
    /// addresses and assigns them upper case, so a lower-case token is somebody else's string.
    /// </summary>
    public static bool TryParse(string address, out string letters, out int number)
    {
      letters = null;
      number = 0;
      if (string.IsNullOrEmpty(address)) return false;

      var split = 0;
      while (split < address.Length && address[split] >= 'A' && address[split] <= 'Z') split++;
      if (split == 0 || split == address.Length) return false;   // needs both halves

      var digits = address.Substring(split);
      if (!digits.All(char.IsDigit)) return false;               // no trailing junk
      if (!int.TryParse(digits, out number)) return false;

      letters = address.Substring(0, split);
      return true;
    }

    /// <summary>
    /// A PLAN address - a region of a scaffold map - and deliberately NOT "any address Dwight uses".
    ///
    /// Callers asking "is this a place Dwight can build or report on" want <see cref="IsRegionAddress"/>;
    /// this one answers the narrower question "does this name a region of a MAP", which is what the
    /// addressing pass mints, what dependsOn orders, and what #720's in-use set continues past.
    /// </summary>
    public static bool IsAddress(string address) => TryParse(address, out _, out _);

    /// <summary>
    /// The order a map READS in: layer first, then the number NUMERICALLY (#286). Anything unparseable
    /// sorts last rather than throwing - a reader shows what it has, and the caller reports the oddity.
    /// </summary>
    public static (string Letters, int Number) SortKey(string address)
    {
      if (TryParse(address, out var letters, out var number)) return (letters, number);

      // SPOTS SORT AFTER EVERY PLAN LETTER, AND NUMERICALLY AMONG THEMSELVES. Left to the unparseable
      // fallback they would all tie at int.MaxValue and Spot 10 could read before Spot 2 - the one
      // property an address exists to carry, lost for the new shape exactly as #286 lost it for the old.
      // U+FFFE sorts after every letter and before the U+FFFF the truly-unparseable get, so the three
      // classes read in a fixed order: the map, then the spots, then whatever nobody can parse.
      if (TrySpot(address, out var spot)) return ("￾", spot);

      return ("￿", int.MaxValue);
    }

    /// <summary>
    /// The address and name carried by a region's GROUP NICKNAME - the canvas-side half of #221b, which is
    /// what lets a map survive the loss of its stored record.
    ///
    /// IT READS BOTH KINDS, and that is a decision rather than a convenience: its three callers all ask
    /// "is this group one of Dwight's regions" - the tidy pass, the address resolver a build is aimed by,
    /// and the canvas reader the map is built from - and a spot is one of Dwight's regions in every one of
    /// those senses. A caller that needs to know WHICH kind asks <see cref="Kind"/> on what comes back.
    /// </summary>
    public static bool FromGroupName(string nickname, out string address, out string name)
    {
      address = null;
      name = null;
      if (string.IsNullOrEmpty(nickname)) return false;

      var at = nickname.IndexOf(Separator, StringComparison.Ordinal);
      if (at <= 0) return false;

      var head = nickname.Substring(0, at);
      if (!IsRegionAddress(head)) return false;

      address = head;
      name = nickname.Substring(at + Separator.Length).Trim();
      return true;
    }
  }
}
