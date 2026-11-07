using System;
using System.Collections.Generic;

namespace Dwight.Layout
{
  /// <summary>
  /// Phase 7 WP-1: remap a cached guid-&gt;cluster segmentation onto a freshly-built graph's id space, so an
  /// arrangement flip re-runs the nested layout on the SAME clustering without re-segmenting or re-calling the
  /// LLM. Keyed on the stable InstanceGuid, NEVER the pivot-derived id: the first apply moves pivots, so a fresh
  /// DwightGraph assigns the same components different ids (the WP-A cycling trap). A component whose guid is no
  /// longer present is dropped; the caller's graph-shape-hash guard already rejects a flip when wires changed,
  /// so this only ever runs on a topologically-identical graph.
  /// </summary>
  internal static class MacroRemap
  {
    public static Dictionary<int, int> Remap(
      IReadOnlyDictionary<Guid, int> guidToCluster,
      IReadOnlyDictionary<int, Guid> idToGuid)
    {
      var macro = new Dictionary<int, int>();
      foreach (var kv in idToGuid)
        if (guidToCluster.TryGetValue(kv.Value, out var cluster)) macro[kv.Key] = cluster;
      return macro;
    }
  }
}
