using System.Collections.Generic;
using System.Linq;

namespace Dwight.Agent
{
  /// <summary>
  /// Phase-9 U2: the ranking cap behind spatial_query. The RTree finds the hits; this orders them,
  /// keeps the nearest <c>limit</c>, and counts what it dropped so the payload can say so (#134).
  /// Ties break on index, so the order is deterministic across calls. GH-free (ADR-091).
  /// </summary>
  internal static class RankCap
  {
    public static List<(int Index, double Distance)> Nearest(
      IEnumerable<(int Index, double Distance)> hits, int limit, out int omitted)
    {
      var all = (hits ?? Enumerable.Empty<(int, double)>())
        .OrderBy(h => h.Distance).ThenBy(h => h.Index).ToList();
      var kept = all.Take(limit < 1 ? 1 : limit).ToList();
      omitted = all.Count - kept.Count;
      return kept;
    }
  }
}
