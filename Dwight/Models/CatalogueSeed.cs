using System;
using System.Text.Json.Nodes;

namespace Dwight.Models
{
  /// <summary>
  /// The pure DECISION behind seeding and refreshing the user's model catalogue (ADR-081, ADR-091):
  /// given the bundled JSON and the user copy's JSON, should the user copy be refreshed, and what does
  /// the superseded file get renamed to? No file IO and no Grasshopper, so the rule is unit-tested
  /// without ever touching a real <c>%APPDATA%</c> - the WP-1 host-opt-in lesson (tests never write to
  /// the user's own files).
  ///
  /// THE BUG THIS ENCODES THE FIX FOR (Phase-8 WP-2, found at the Stage-B deploy). The user copy wins
  /// over the bundled catalogue by design, but it was seeded once and never revisited - so any install
  /// that had ever run kept resolving the OLD lineup no matter what the build shipped. A catalogue
  /// refresh that cannot reach a user is not a refresh, and worse, it is invisible: the plugin reports
  /// exactly the models it is using, and they are the stale ones.
  /// </summary>
  internal static class CatalogueSeed
  {
    /// <summary>The catalogue's CONTENT revision. Absent (every copy seeded before this shipped),
    /// unparseable, or malformed all read as 0 - so such a copy is refreshed exactly once.</summary>
    public static int Version(string json)
    {
      if (string.IsNullOrWhiteSpace(json)) return 0;
      try
      {
        var v = JsonNode.Parse(json)?["catalogueVersion"];
        if (v == null) return 0;
        return v.GetValue<int>();
      }
      catch { return 0; }
    }

    /// <summary>
    /// Refresh only when the build genuinely ships a NEWER catalogue. Deliberately conservative:
    /// - equal or older bundled version leaves the user copy alone (their edits keep winning);
    /// - a user copy that is somehow AHEAD is left alone too (never downgrade someone's catalogue);
    /// - no bundled catalogue at all means there is nothing to refresh from.
    /// </summary>
    public static bool ShouldRefresh(string bundledJson, string userJson)
    {
      if (string.IsNullOrWhiteSpace(bundledJson)) return false;
      return Version(bundledJson) > Version(userJson);
    }

    /// <summary>
    /// The name the superseded user copy is preserved under. A superseded catalogue may hold the user's
    /// own edits, so it is moved aside and never destroyed (#162/#167: cannot-read is not a licence to
    /// overwrite). <paramref name="exists"/> lets the caller avoid collisions without this method
    /// touching the filesystem.
    /// </summary>
    public static string BakFileName(int supersededVersion, Func<string, bool> exists)
    {
      var name = $"models.v{supersededVersion}.bak";
      if (exists == null) return name;
      for (var i = 2; exists(name) && i < 100; i++) name = $"models.v{supersededVersion}.{i}.bak";
      return name;
    }
  }
}
