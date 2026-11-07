using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Dwight.Components
{
  /// <summary>How a type name resolved against the installed components (U25). Three answers, never two: an
  /// ambiguous name is not "found" and not "unknown", and collapsing it into either is #915's defect.</summary>
  internal enum NameResolution { Unknown, Found, Ambiguous }

  /// <summary>
  /// The GH-free half of the component whitelist (ADR-034, ADR-091): name NORMALISATION and
  /// NEAREST-MATCH ranking, over plain strings. No <c>ComponentServer</c>, no proxies - so the two
  /// decisions that actually matter can be unit-tested outside Rhino.
  ///
  /// Why nearest-match is core logic and not a nicety (WP-2 #190): when a write tool refuses an
  /// invented component type, the refusal carries the nearest INSTALLED names. That turns a dead end
  /// into a repair instruction the model can act on - the ADR-092 move (the plugin computes the ground
  /// truth; the model supplies the judgement) applied at the tool boundary. A refusal that only says
  /// "no such component" spends a round-trip teaching the model nothing.
  ///
  /// Ranking is TOTALLY ORDERED and deterministic (band, then distance, then length, then ordinal
  /// name), so the same query returns the same suggestions every run - the same discipline the layout
  /// engine keys off <c>InstanceGuid</c> for.
  /// </summary>
  internal static class ComponentMatchCore
  {
    /// <summary>
    /// THE ONE SEPARATOR of a qualified component name - "Category > SubCategory > Name" (U25, #915). Agreed between the
    /// build side (which refuses an ambiguous bare name with these forms and accepts one in <c>type</c>) and the reads
    /// (which present them): the character and nothing else, so the two lanes cannot drift on a spelling.
    /// </summary>
    public const string QualifiedSeparator = " > ";

    /// <summary>"Curve > Primitive > Rectangle".</summary>
    public static string Qualified(string category, string subCategory, string name) =>
      (category ?? "").Trim() + QualifiedSeparator + (subCategory ?? "").Trim() + QualifiedSeparator + (name ?? "").Trim();

    /// <summary>Is this a qualified form - three parts around the separator? The bare separator character decides, so
    /// "Curve>Primitive>Rectangle" qualifies too.</summary>
    public static bool IsQualified(string typeName) => Split(typeName) != null;

    /// <summary>The three trimmed parts of a qualified name, or null when it is not one.</summary>
    public static string[] Split(string typeName)
    {
      if (string.IsNullOrWhiteSpace(typeName)) return null;
      var parts = typeName.Split(new[] { QualifiedSeparator.Trim() }, StringSplitOptions.None);
      if (parts.Length != 3) return null;
      for (var i = 0; i < 3; i++) { parts[i] = parts[i].Trim(); if (parts[i].Length == 0) return null; }
      return parts;
    }

    /// <summary>The bare name of a possibly qualified type.</summary>
    public static string BareName(string typeName) => Split(typeName)?[2] ?? (typeName ?? "").Trim();

    /// <summary>
    /// RESOLVE A TYPE NAME EXACTLY, OR SAY WHY NOT (U25, #915). <c>ComponentWhitelist.All()</c> used to keep the first
    /// proxy per name, so "Rectangle" resolved to the PARAM and "Line" to the component - by enumeration order, never
    /// by anything the model said or could say. The model saw the error, rebuilt, got the same param, and told the user
    /// there was "no plain rectangle type installed" - a false statement about their machine, manufactured by our
    /// resolution. #208 at the build surface: identity resolved by accident, never refused.
    ///
    /// So a bare name that matches more than one candidate is AMBIGUOUS, with the qualified forms handed back to choose
    /// from; a qualified form resolves to exactly the one it names; a unique bare name resolves exactly as before -
    /// exact, then normalised (ADR-083). Generic over the candidate type so the whitelist's live infos and a test's
    /// tuples go through one definition. The candidates handed in may be the whole catalogue or the bucket sharing
    /// one name: the answer is the same, because resolution never looks past the name.
    /// </summary>
    public static NameResolution Resolve<T>(string query, IEnumerable<T> candidates,
      Func<T, string> name, Func<T, string> category, Func<T, string> subCategory,
      out T match, out IReadOnlyList<string> qualifiedForms)
    {
      match = default(T);
      qualifiedForms = new string[0];
      if (string.IsNullOrWhiteSpace(query) || candidates == null) return NameResolution.Unknown;

      var all = candidates.Where(c => c != null).ToList();
      var parts = Split(query);
      if (parts != null)
      {
        var byName = SameName(all, name, parts[2]);
        var exact = byName.Where(c => Normalize(category(c)) == Normalize(parts[0]) &&
                                      Normalize(subCategory(c)) == Normalize(parts[1])).ToList();
        if (exact.Count == 0) return NameResolution.Unknown;
        // Two proxies identical in category, sub-category AND name cannot be told apart by any form a model could give,
        // so the first stands - the one bound that is stated rather than hidden.
        match = exact[0];
        return NameResolution.Found;
      }

      var same = SameName(all, name, query.Trim());
      if (same.Count == 0) return NameResolution.Unknown;
      if (same.Count == 1) { match = same[0]; return NameResolution.Found; }

      qualifiedForms = same.Select(c => Qualified(category(c), subCategory(c), name(c)))
        .Distinct(StringComparer.Ordinal).OrderBy(f => f, StringComparer.Ordinal).ToList();
      if (qualifiedForms.Count == 1) { match = same[0]; return NameResolution.Found; }   // duplicates of one form
      return NameResolution.Ambiguous;
    }

    /// <summary>
    /// THE REPAIR PAYLOAD IN CANONICAL NAMES (U25): rank by BARE name exactly as <see cref="Nearest"/> does - the fuzzy
    /// band must see "Rectangle" when the model wrote "Rectangel", which it cannot through "Curve > Primitive >
    /// Rectangle" - then present each hit as the form that will RESOLVE: bare where one component owns the name, every
    /// qualified form where several do. A suggestion the model takes next is then never refused as ambiguous (#626).
    /// </summary>
    public static List<string> NearestCanonical<T>(string query, IEnumerable<T> candidates,
      Func<T, string> name, Func<T, string> category, Func<T, string> subCategory, int max = 5)
    {
      var result = new List<string>();
      if (candidates == null) return result;
      var groups = candidates.Where(c => c != null && !string.IsNullOrWhiteSpace(name(c)))
        .GroupBy(c => name(c).Trim(), StringComparer.OrdinalIgnoreCase)
        .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

      foreach (var bare in Nearest(query, groups.Keys, max))
      {
        var group = groups[bare];
        if (group.Count == 1) result.Add(name(group[0]).Trim());
        else
          result.AddRange(group.Select(c => Qualified(category(c), subCategory(c), name(c)))
            .Distinct(StringComparer.Ordinal).OrderBy(f => f, StringComparer.Ordinal));
        if (result.Count >= max) break;
      }
      return result.Take(max).ToList();
    }

    /// <summary>Candidates with this bare name: exact (case-insensitive) matches if any, else normalised matches.</summary>
    private static List<T> SameName<T>(List<T> all, Func<T, string> name, string bare)
    {
      var exact = all.Where(c => string.Equals((name(c) ?? "").Trim(), bare, StringComparison.OrdinalIgnoreCase)).ToList();
      if (exact.Count > 0) return exact;
      var norm = Normalize(bare);
      if (norm.Length == 0) return exact;
      return all.Where(c => Normalize(name(c)) == norm).ToList();
    }

    /// <summary>Normalise a component name for tolerant matching: lower-case, single-spaced,
    /// punctuation dropped (the ADR-083 lesson - a trivially different spelling of an installed
    /// component must not read as a false absence).</summary>
    public static string Normalize(string name)
    {
      if (string.IsNullOrWhiteSpace(name)) return "";
      var sb = new StringBuilder(name.Length);
      var lastSpace = false;
      foreach (var ch in name.Trim().ToLowerInvariant())
      {
        if (char.IsWhiteSpace(ch)) { if (!lastSpace && sb.Length > 0) { sb.Append(' '); lastSpace = true; } }
        else if (char.IsLetterOrDigit(ch)) { sb.Append(ch); lastSpace = false; }
        // every other character is dropped ("Series." == "Series")
      }
      return sb.ToString().TrimEnd();
    }

    /// <summary>How close a candidate is to the query. Lower is better; the band dominates so an exact
    /// match can never be outranked by a short unrelated name.</summary>
    private enum Band { Exact = 0, Normalised = 1, Prefix = 2, Contains = 3, Fuzzy = 4, None = 5 }

    private static Band Rank(string queryNorm, string candidateNorm, out int distance)
    {
      distance = 0;
      if (candidateNorm.Length == 0) return Band.None;
      if (string.Equals(queryNorm, candidateNorm, StringComparison.Ordinal)) return Band.Exact;
      if (queryNorm.Replace(" ", "") == candidateNorm.Replace(" ", "")) return Band.Normalised;
      if (candidateNorm.StartsWith(queryNorm, StringComparison.Ordinal)) return Band.Prefix;
      if (candidateNorm.IndexOf(queryNorm, StringComparison.Ordinal) >= 0) return Band.Contains;

      // Bounded edit distance: only worth reporting when the names are genuinely close. The cap keeps a
      // long unrelated name from ranking above a short near-miss.
      var max = Math.Max(2, Math.Min(queryNorm.Length, candidateNorm.Length) / 2);
      distance = Levenshtein(queryNorm, candidateNorm, max);
      return distance <= max ? Band.Fuzzy : Band.None;
    }

    /// <summary>The nearest installed names to <paramref name="query"/>, best first, capped.
    /// <paramref name="candidates"/> is the installed name list; duplicates are collapsed.</summary>
    public static List<string> Nearest(string query, IEnumerable<string> candidates, int max = 5)
    {
      var result = new List<string>();
      if (candidates == null) return result;

      // A QUALIFIED query ranks on its NAME part: "Params > Input > Number" normalised whole is "params input number",
      // which is Contained by nothing and a prefix of nothing, so the fuzzy tail offered "Partition Number" and
      // "Mirror Cut Numbers" and never "Number" itself (#975). The category words are routing, not the name asked for.
      var q = Normalize(BareName(query));
      var scored = new List<(Band band, int distance, int length, string name)>();
      var seen = new HashSet<string>(StringComparer.Ordinal);

      foreach (var name in candidates)
      {
        if (string.IsNullOrWhiteSpace(name) || !seen.Add(name)) continue;
        if (q.Length == 0) continue;                       // an empty query suggests nothing
        var band = Rank(q, Normalize(name), out var distance);
        if (band == Band.None) continue;
        scored.Add((band, distance, name.Length, name));
      }

      // Total order, so suggestions are byte-stable across runs.
      scored.Sort((a, b) =>
      {
        var c = a.band.CompareTo(b.band); if (c != 0) return c;
        c = a.distance.CompareTo(b.distance); if (c != 0) return c;
        c = a.length.CompareTo(b.length); if (c != 0) return c;
        return string.CompareOrdinal(a.name, b.name);
      });

      for (var i = 0; i < scored.Count && result.Count < max; i++) result.Add(scored[i].name);
      return result;
    }

    /// <summary>Levenshtein distance, abandoned once it exceeds <paramref name="cap"/> (returns
    /// <c>cap + 1</c>). Two rolling rows - no allocation per cell.</summary>
    internal static int Levenshtein(string a, string b, int cap)
    {
      if (a == b) return 0;
      if (a.Length == 0) return b.Length;
      if (b.Length == 0) return a.Length;
      if (Math.Abs(a.Length - b.Length) > cap) return cap + 1;

      var prev = new int[b.Length + 1];
      var cur = new int[b.Length + 1];
      for (var j = 0; j <= b.Length; j++) prev[j] = j;

      for (var i = 1; i <= a.Length; i++)
      {
        cur[0] = i;
        var rowMin = cur[0];
        for (var j = 1; j <= b.Length; j++)
        {
          var cost = a[i - 1] == b[j - 1] ? 0 : 1;
          cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
          if (cur[j] < rowMin) rowMin = cur[j];
        }
        if (rowMin > cap) return cap + 1;                  // whole row past the cap: cannot recover
        var swap = prev; prev = cur; cur = swap;
      }
      return prev[b.Length];
    }
  }
}
