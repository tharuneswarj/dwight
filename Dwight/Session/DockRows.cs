using System.Text.Json.Nodes;

namespace Dwight.Session
{
  /// <summary>
  /// THE DOCK'S LIST ROW, WRITTEN ONCE (Brief DG). The host sends the shell one object per recorded run; this is the only place that object is made, so the
  /// shell's fixtures can be rendered from it and never written by hand, and a field added here is a field the fixture file holds.
  ///
  /// THE LIST SHOWS NO COST ON ANY ROW (Brief BQ section 9, Tharun's ruling, which Brief DG's first reading got wrong): not a finished run's, and not a
  /// stopped or failed run's either. The row takes no spend, so it cannot carry one. What a run that billed and then did not land cost lives on its record
  /// (<see cref="DwightCommand.Spend"/>, from the same spend the ledger line was written from, Brief AK) and shows where a finished row's cost shows today:
  /// the run's status line when the row is reopened, with "stopped" or "failed" beside it (<see cref="StatusWord"/>).
  ///
  /// GH-free (ADR-091).
  /// </summary>
  internal static class DockRows
  {
    /// <summary>The words for a failed or stopped run's cost, or null: the cents when the run had a price, the run's own words when it had none (a local run is
    /// "local · free"), and nothing when no call was billed or the run landed.</summary>
    public static string CostWord(string state, StoredSpend spend)
    {
      if (state != "failed" && state != "stopped") return null;
      if (!SpendLedgerCore.HasUsage(spend)) return null;
      // Brief DL: an estimate says so, in the words the Usage section uses for one: "about 4 c (estimated)".
      if (spend.Estimated != null)
        return spend.Cents.HasValue ? SpendLedgerCore.EstimateWord(spend.Cents.Value) + " (estimated)"
             : string.IsNullOrWhiteSpace(spend.CostText) ? null : spend.CostText + " (estimated)";
      if (spend.Cents.HasValue) return SpendMath.CentsText(spend.Cents.Value);
      return string.IsNullOrWhiteSpace(spend.CostText) ? null : spend.CostText;
    }

    /// <summary>The status line of a reopened stopped or failed run: its state, then its cost ("stopped · ~10.7¢"). Null when it billed nothing, so a run that
    /// never did shows no cost and no zero, and null for a run that landed (its own status line is its cost line).</summary>
    public static string StatusWord(string state, StoredSpend spend)
    {
      var cost = CostWord(state, spend);
      return cost == null ? null : state + " · " + cost;
    }

    /// <summary>The whole status line of a reopened row (Brief DG's cost beside the state, and a lesson's score beside the cost). A stopped or failed run that billed
    /// reads "stopped · ~10.7¢"; a finished Explain that was a Learn reads "&lt;its cost&gt; · 3 of 3 right"; a stopped Learn reads both, so neither word
    /// takes the other's place. Null when there is nothing to say.</summary>
    public static string ReopenedStatus(string state, StoredSpend spend, string costLine, int[] score)
    {
      var word = StatusWord(state, spend);
      if (score == null || score.Length != 2 || Dwight.UI.TeachingText.Pair(score[0], score[1]) == null) return word;
      var kept = Dwight.UI.TeachingText.Score(score[0], score[1]);
      var lead = word ?? (string.IsNullOrEmpty(costLine) ? null : costLine);
      return lead == null ? kept : lead + " · " + kept;
    }

    public static JsonObject Row(string action, string subtitle, bool chat, string live, string state, string meta, string from)
    {
      return new JsonObject
      {
        ["icon"] = string.IsNullOrEmpty(action) ? "·" : action.Substring(0, 1),
        ["action"] = action ?? "",
        ["subtitle"] = subtitle ?? "",
        ["chat"] = chat,
        ["live"] = live,
        ["liveWord"] = HistoryCore.LiveWords(live),
        ["state"] = string.IsNullOrEmpty(state) ? "ok" : state,   // WP-5: failed / stopped runs are kept, and read as such
        ["meta"] = meta,
        ["from"] = from,
      };
    }
  }
}
