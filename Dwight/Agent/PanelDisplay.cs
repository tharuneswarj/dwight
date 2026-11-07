using System.Collections.Generic;

namespace Dwight.Agent
{
  /// <summary>
  /// U31 (#926): what a FED Panel reports. A Panel with a wire into it shows the data that flows in, and its
  /// <c>UserText</c> is Grasshopper's placeholder - so <c>get_component</c> on the GFA panel the model had just
  /// built returned "Double click to edit panel content..." and the model told the user it could not read the
  /// value. The read now carries the DISPLAYED values, as text, bounded (ADR-035) with the cut stated, and says
  /// they are displayed values rather than text the user typed.
  ///
  /// GH-free (ADR-091): the shim hands in the first items as strings and the totals; the bound and the DTO
  /// shape are decided here, where a test can reach them. The item strings are what Grasshopper's own goo
  /// prints (<c>IGH_Goo.ToString()</c>), which is what the panel shows up to its number formatting.
  /// </summary>
  internal static class PanelDisplay
  {
    /// <summary>At most this many displayed items are listed. The count is always stated, so a cut list is
    /// never read as the whole.</summary>
    public const int MaxItems = 25;

    /// <summary>The sentence get_component carries beside a fed panel's values.</summary>
    public const string Means =
      "displayed are the values this panel SHOWS from the wire into it, as text, from the last solution - not text " +
      "the user typed (a fed panel's own text is Grasshopper's placeholder and is not reported). displayedCount is " +
      "how many items it shows in total, displayedPaths how many branches they sit in; when the list is cut, " +
      "displayedTruncated is true and displayedShown says how many are listed.";

    /// <summary>
    /// Build the value DTO for a fed panel. <paramref name="firstItems"/> are the leading items as text (the shim
    /// collects no more than <see cref="MaxItems"/> of them); <paramref name="totalCount"/> and
    /// <paramref name="paths"/> are the whole data's. The list is cut at <paramref name="maxItems"/> items or
    /// <paramref name="maxChars"/> characters in total, whichever comes first, and the cut is stated.
    /// </summary>
    public static ValueDto Fed(IReadOnlyList<string> firstItems, int totalCount, int paths, int maxItems, int maxChars)
    {
      var shown = new List<string>();
      var chars = 0;
      var cutByChars = false;
      foreach (var raw in firstItems ?? new List<string>())
      {
        if (shown.Count >= maxItems) break;
        var s = (raw ?? "<null>").Replace("\r\n", "\n");
        if (shown.Count > 0 && chars + s.Length > maxChars) { cutByChars = true; break; }
        if (s.Length > maxChars) s = s.Substring(0, maxChars);   // one enormous item still shows its head
        shown.Add(s);
        chars += s.Length;
      }
      if (totalCount < shown.Count) totalCount = shown.Count;   // the total can never be less than what is listed

      return new ValueDto
      {
        Kind = "panel",
        Displayed = shown.ToArray(),
        DisplayedCount = totalCount,
        DisplayedPaths = paths,
        DisplayedTruncated = cutByChars || shown.Count < totalCount,
      };
    }
  }
}
