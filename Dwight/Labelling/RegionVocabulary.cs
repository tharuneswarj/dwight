using System;
using System.Collections.Generic;
using System.Linq;

namespace Dwight.Labelling
{
  /// <summary>
  /// U29 (#918): the word Dwight uses for a segmenter region, and the rule that it is not a Grasshopper word.
  ///
  /// THE HARM. The labelling pass's generic fallback was "Cluster N", and <c>get_overview</c> presented the
  /// segmenter's regions under it. "Cluster" is also a Grasshopper COMPONENT TYPE, and <c>get_cluster</c> the
  /// tool that reads one - so on every fresh session the model called get_cluster on a region id and was
  /// refused, and once, asked whether a reorganise had happened, it read "Cluster 0" and told the user
  /// *"everything I built has been collapsed into a single opaque Cluster 0... I'd need you to explode the
  /// cluster by hand"* - on a canvas holding zero Grasshopper clusters. One word, two meanings, one surface.
  ///
  /// THE RULE. A region is called a REGION - the noun <c>get_region</c> already uses - and the overview's
  /// vocabulary for a region must never BE an installed component type's name. <see cref="Collisions"/> is
  /// that rule as a function over the installed names, so a test can hold it against a fixture of them and
  /// the deploy probe can hold it against <c>ComponentWhitelist.All()</c> on a machine with Rhino.
  ///
  /// The Jev study's type-histogram name (§3.2, "Transform: array") would be a better fallback than any
  /// numbered noun; it needs a taxonomy this unit does not build, so the fallback here is the minimum the
  /// ruling asked for and the richer name is deferred with a home (this class).
  /// </summary>
  internal static class RegionVocabulary
  {
    /// <summary>The noun, singular, as it appears in a generic label and in the overview's keys.</summary>
    public const string Noun = "Region";

    /// <summary>Every word the overview uses to mean a segmenter region: its labels' noun and its payload keys.
    /// Checked against the installed component-type names by <see cref="Collisions"/>.</summary>
    public static readonly IReadOnlyList<string> Words = new[] { "Region", "Regions" };

    /// <summary>The generic label for a region nobody has named yet - the ONE definition, used by the labelling
    /// pass's fallback and by the overview's cache-miss path alike.</summary>
    public static string GenericLabel(int id) => Noun + " " + id;

    /// <summary>Is this label one of ours - a generic fallback rather than a name a model or a user gave?</summary>
    public static bool IsGeneric(string label)
    {
      if (string.IsNullOrWhiteSpace(label)) return true;
      var t = label.Trim();
      if (!t.StartsWith(Noun + " ", StringComparison.Ordinal)) return false;
      return int.TryParse(t.Substring(Noun.Length + 1), out _);
    }

    /// <summary>
    /// The installed component-type names that ARE one of the overview's region words (case-insensitive, whitespace
    /// trimmed). Empty means the vocabulary is safe on this machine. "Cluster" against the old word "Cluster" is the
    /// positive control a test keeps, so the rule is seen to find something (#212).
    /// </summary>
    public static IReadOnlyList<string> Collisions(IEnumerable<string> installedTypeNames, IEnumerable<string> words = null)
    {
      var vocabulary = new HashSet<string>((words ?? Words).Select(w => w.Trim()), StringComparer.OrdinalIgnoreCase);
      return (installedTypeNames ?? Enumerable.Empty<string>())
        .Where(n => !string.IsNullOrWhiteSpace(n))
        .Select(n => n.Trim())
        .Where(vocabulary.Contains)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
        .ToList();
    }
  }
}
