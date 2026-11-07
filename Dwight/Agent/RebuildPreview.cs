using System;
using System.Collections.Generic;
using System.Linq;

namespace Dwight.Agent
{
  /// <summary>One component a rebuild would touch, as a person would recognise it.</summary>
  internal sealed class RebuildComponent
  {
    public string Nick { get; set; }
    public string Type { get; set; }

    /// <summary>What a user sees on the canvas: the nickname if it has one, and the type when the
    /// nickname would not identify it on its own.</summary>
    public string Label =>
      string.IsNullOrWhiteSpace(Nick) || string.Equals(Nick, Type, StringComparison.Ordinal)
        ? (Type ?? "component")
        : Nick + " (" + Type + ")";
  }

  /// <summary>One wire a rebuild will reconnect, as the card names it (#625) - the consumer, never the new output,
  /// because the card is drawn before the spec exists.</summary>
  internal sealed class RebuildReconnection
  {
    public string ConsumerType { get; set; }
    public int ConsumerId { get; set; }
    public string ConsumerRegion { get; set; }
    public string Input { get; set; }

    public string Label =>
      (ConsumerType ?? "component") + " (" + ConsumerId + ")" +
      (string.IsNullOrEmpty(ConsumerRegion) ? "" : " in " + ConsumerRegion) +
      (string.IsNullOrEmpty(Input) ? "" : ", input " + Input);
  }

  /// <summary>What one region's rebuild would do, for the consent card.</summary>
  internal sealed class RegionRebuild
  {
    public string Address { get; set; }
    public IReadOnlyList<RebuildComponent> Remove { get; set; } = new RebuildComponent[0];
    public int Adopted { get; set; }

    /// <summary>The adopted components by name (U33, #922), so the card says WHICH ones stay and the user can see that
    /// a build must wire from them rather than place them again. Empty leaves the clause as the count alone.</summary>
    public IReadOnlyList<RebuildComponent> AdoptedNamed { get; set; } = new RebuildComponent[0];

    public int Theirs { get; set; }

    /// <summary>What Dwight placed in this region earlier, by name. Nothing of the region's own, so a rebuild keeps them, and the card says which.</summary>
    public IReadOnlyList<RebuildComponent> PlacedEarlier { get; set; } = new RebuildComponent[0];

    /// <summary>What the removed components feed outside the removal, which the rebuild must reconnect (#625).</summary>
    public IReadOnlyList<RebuildReconnection> Reconnect { get; set; } = new RebuildReconnection[0];

    /// <summary>On a FRESH build: inputs that lost only their source from this region's earlier components, which the
    /// build MAY restore (#625's narrow sibling).</summary>
    public IReadOnlyList<RebuildReconnection> Restore { get; set; } = new RebuildReconnection[0];
  }

  /// <summary>
  /// THE REMOVAL, NAMED - the sentence a user reads before agreeing to it.
  ///
  /// WHY IT EXISTS AT ALL. #352 scoped the delete-claim to a single turn because a surviving list of
  /// things-we-may-delete is privilege-shaped. That constraint was correct about its own purpose and
  /// forbade the commonest request there is, so the claim now lives as long as the document - and the
  /// protection that replaces the scope is THIS: a stored privilege exercised later is what #352 feared;
  /// a consented act with its scope in front of the user AT THE MOMENT OF CONSENT is not, because they
  /// read it and decide then.
  ///
  /// SO A COUNT IS NOT ENOUGH. At document scope a rebuild may be removing components placed weeks ago,
  /// in a conversation the user does not remember. "9 components" is a number; "Width, Height and Count"
  /// is a scope they can act on. A USER WHO CANNOT RECOGNISE WHAT IS ABOUT TO BE REMOVED HAS NOT BEEN
  /// GIVEN THE SCOPE, ONLY A FIGURE.
  ///
  /// AND WHAT IS LEFT IS PART OF THE SCOPE. The sentence names three fates, not one: what goes, what
  /// Dwight placed but the user has since edited (left alone, always), and what was never Dwight's.
  /// There is deliberately no option to remove the adopted ones - leaving them is the safe default, the
  /// user can delete by hand, and a capability to remove work Dwight KNOWS they touched would need
  /// justifying by a request nobody has made.
  ///
  /// GH-free, so the sentence a user consents to is testable (ADR-091).
  /// </summary>
  internal static class RebuildPreview
  {
    /// <summary>How many components to name before falling back to a count (ADR-035 at a consent card).
    /// A card nobody finishes reading is a card nobody has consented to. A spot card's sentences fold at this
    /// number too (Brief CV); how many a placement may hold is another rule, <see cref="SpotPlacement.MaxComponents"/>.</summary>
    public const int MaxNamed = 5;

    /// <summary>
    /// The removal sentence for one or more regions, or null when nothing would be removed - in which
    /// case the card says only what it always said, because a sentence about removing nothing is noise
    /// that trains a reader to skip the paragraph that will one day matter.
    /// </summary>
    public static string Sentence(IReadOnlyList<RegionRebuild> regions)
    {
      var touched = (regions ?? new RegionRebuild[0])
        .Where(r => r != null && (r.Remove.Count > 0 || r.Adopted > 0 || r.Theirs > 0 || (r.PlacedEarlier?.Count ?? 0) > 0 || (r.Restore?.Count ?? 0) > 0))
        .ToList();
      if (touched.Count == 0) return null;

      var parts = new List<string>();
      foreach (var r in touched)
      {
        var clauses = new List<string>();

        var reconnect = (r.Remove.Count > 0 ? r.Reconnect : null) ?? new RebuildReconnection[0];

        if (r.Remove.Count > 0)
          clauses.Add("remove the " + r.Remove.Count + " component" + S(r.Remove.Count) +
                      " it placed there - " + Named(r.Remove) + " - and build it again" +
                      // #625: what those components fed outside the region, named - the consumer, never the new
                      // output, which no spec has chosen yet.
                      (reconnect.Count == 0 ? ""
                        : ", and reconnect what " + (r.Remove.Count == 1 ? "it" : "they") + " fed outside " +
                          r.Address + ": " + Bounded(reconnect.Select(x => x.Label).ToList(), "; ")));

        if (r.Adopted > 0)
        {
          var named = r.AdoptedNamed ?? new RebuildComponent[0];
          clauses.Add("LEAVE the " + r.Adopted + " you have edited since" +
                      (named.Count == 0 ? "" : " (" + Named(named) + ")"));
        }

        if (r.Theirs > 0)
          clauses.Add("leave the " + r.Theirs + " it did not place");

        if (clauses.Count > 0)
        {
          // #662a: a reconnection list carries commas inside its entries, so after one the clauses are joined by
          // semicolons - otherwise nothing marks where the list ends. Chosen per region.
          var sentence = "In " + r.Address + ", Dwight will " +
                         (reconnect.Count > 0 ? Join(clauses, "; ", "; and ") : Join(clauses, ", ", " and ")) + ".";
          if (reconnect.Count > 0)
            sentence += " If the new build does not reconnect " + (reconnect.Count == 1 ? "it" : "them") +
                        ", nothing is changed." +
                        // #647, for the one reader who can act on it: a build cannot drop a wire; the user can.
                        (reconnect.Count == 1
                          ? " If you would rather lose that wire, delete it yourself before approving."
                          : " If you would rather lose one of those wires, delete it yourself before approving.");
          parts.Add(sentence);
        }

        var earlier = r.PlacedEarlier ?? new RebuildComponent[0];
        if (earlier.Count > 0)
          parts.Add(r.Address + " holds " + earlier.Count + " component" + S(earlier.Count) + " placed earlier; " +
                    (earlier.Count == 1 ? "it stays" : "they stay") + ": " + Named(earlier) + ".");

        // #625's narrow sibling, on a fresh build: an OPTION the card names, so a restore is never a surprise.
        var restore = r.Restore ?? new RebuildReconnection[0];
        if (restore.Count > 0)
          parts.Add("Building " + r.Address + " may also restore " +
                    (restore.Count == 1 ? "an input" : restore.Count + " inputs") + " that lost " +
                    (restore.Count == 1 ? "its source" : "their sources") + " when " + r.Address +
                    "'s earlier components were removed: " + Bounded(restore.Select(x => x.Label).ToList(), "; ") + ".");
      }

      return parts.Count == 0 ? null : string.Join(" ", parts);
    }

    /// <summary>Names, bounded, and it SAYS how many it did not name rather than trailing off.</summary>
    private static string Named(IReadOnlyList<RebuildComponent> components) =>
      Bounded(components.Select(c => c.Label).ToList(), ", ");

    private static string Bounded(IReadOnlyList<string> labels, string separator)
    {
      if (labels.Count <= MaxNamed) return string.Join(separator, labels);
      return string.Join(separator, labels.Take(MaxNamed)) + ", and " + (labels.Count - MaxNamed) + " more";
    }

    private static string Join(IReadOnlyList<string> clauses, string separator, string last) =>
      clauses.Count == 1 ? clauses[0]
      : string.Join(separator, clauses.Take(clauses.Count - 1)) + last + clauses[clauses.Count - 1];

    private static string S(int n) => n == 1 ? "" : "s";
  }
}
