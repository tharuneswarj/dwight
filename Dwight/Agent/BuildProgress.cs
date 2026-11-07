using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Dwight.Agent
{
  /// <summary>How one region's build ended - the honest set, with no outcome that means "probably".</summary>
  internal enum RegionOutcome
  {
    /// <summary>Built, checked (placed as declared, nothing existing changed, no new errors), and standing.</summary>
    Built = 0,
    /// <summary>Built and standing, but Grasshopper reports new errors in it. NOT a failure - a region can
    /// legitimately error until its neighbours exist - and NOT a success either.</summary>
    BuiltWithErrors = 1,
    /// <summary>Applied, failed its own check, and reverted itself. The canvas is as it was.</summary>
    Reverted = 2,
    /// <summary>Refused before anything was touched.</summary>
    Refused = 3,
    /// <summary>Applied, failed its own check and was reverted - but the revert's measurement found objects Dwight added still standing (U91).
    /// Never described as "the canvas is as it was".</summary>
    RevertedWithResidue = 4,
  }

  /// <summary>
  /// WHAT THIS BUILD HAS DONE SO FAR (Phase-8 WP-4 Stage C).
  ///
  /// #96 ruled build-then-report with a RUNNING SUMMARY and no per-region pause: the user consented once
  /// by naming the regions, and control comes from watching it happen plus Stop plus one Ctrl-Z per step -
  /// not from being asked five times. So each region reports as it lands, and this is the record behind
  /// that.
  ///
  /// IT IS PERSISTED, and that is the second reason it exists. <c>StoredThread.BuildProgress</c> has been
  /// a schema slot with no writer since WP-1; a thread reopened after a restart could show a conversation
  /// about a build with no record of what the build actually did. The capability to build does not survive
  /// the turn (#220) and must not - but the FACT of what was built is history, and history is exactly what
  /// the store is for.
  ///
  /// GH-free (ADR-091). The merge is a decision - what a second attempt on one region does to the record -
  /// so it is tested rather than reasoned about in a shim.
  /// </summary>
  internal static class BuildProgress
  {
    public const int CurrentVersion = 1;

    /// <summary>Regions kept in the record. A scaffold holds at most 24 (<c>ScaffoldPlanParser</c>), so
    /// this cannot bite on a real map; it bounds a pathological thread rather than a real one, and when it
    /// bites it drops the OLDEST - the tail is what a reader is asking about.</summary>
    public const int MaxRegions = 48;

    /// <summary>
    /// Fold one region's outcome into the stored record, replacing any earlier entry for that region.
    ///
    /// REPLACING, not appending: a region has ONE current state, and a rebuild means the earlier attempt
    /// no longer describes the canvas. A record that listed A1 as both built and reverted would be a
    /// history of attempts pretending to be a description of the definition - and the reader who needs
    /// this most is a reopened thread asking "what is actually there?".
    /// </summary>
    public static string Merge(string existingJson, string address, RegionOutcome outcome,
                               int components, string note, string utc)
    {
      var root = Parse(existingJson) ?? new JsonObject();
      var regions = root["regions"] as JsonArray ?? new JsonArray();

      // THE RECOVERY INSTRUMENT (#143/#212, and it is built BEFORE the question is asked).
      //
      // WP-0's whole lesson was that a repair loop can fire 56 times and recover 0, and that we only knew
      // because the firings were counted. WP-4 has its own repair loop, and its oracle is a runtime
      // message rather than a semantic class-label - which #94's refined law says SHOULD be actionable.
      // "Should" is a hypothesis. So the loop is instrumented from its first run: how many attempts a
      // region took, and what its FIRST attempt produced, which is what makes "did a retry ever turn a
      // broken region into a clean one" answerable instead of assumed.
      //
      // #212 in advance: the counter must be able to register BOTH outcomes. It reads a recovery whenever
      // a first attempt was not clean and the final one is - a condition reachable in ordinary use, not a
      // number that could only ever be zero.
      var attempts = 1;
      var first = outcome;
      foreach (var node in regions)
      {
        var prior = node as JsonObject;
        if (!string.Equals(prior?["address"]?.GetValue<string>(), address, StringComparison.Ordinal)) continue;
        try { attempts = (prior["attempts"]?.GetValue<int>() ?? 1) + 1; } catch { attempts = 2; }
        try { Enum.TryParse(prior["firstOutcome"]?.GetValue<string>() ?? "", out first); } catch { }
        break;
      }

      var kept = new JsonArray();
      foreach (var node in regions)
      {
        var o = node as JsonObject;
        var at = o?["address"]?.GetValue<string>();
        if (o == null || string.Equals(at, address, StringComparison.Ordinal)) continue;   // superseded
        kept.Add(o.DeepClone());
      }

      kept.Add(new JsonObject
      {
        ["address"] = address ?? "",
        ["outcome"] = outcome.ToString(),
        ["components"] = components,
        ["note"] = note ?? "",
        ["utc"] = utc ?? "",
        ["attempts"] = attempts,
        ["firstOutcome"] = first.ToString(),
      });

      while (kept.Count > MaxRegions) kept.RemoveAt(0);

      return new JsonObject
      {
        ["version"] = CurrentVersion,
        ["regions"] = kept,
      }.ToJsonString();
    }

    /// <summary>
    /// U76: WHAT STANDS AFTER AN ATTEMPT, not what the attempt was. A refused attempt (a precondition, an occupied region, an
    /// exhausted budget) changed nothing, so it cannot take away a build that already stands this turn: B1 built and
    /// checked, then refused twice, is still built and checked. An attempt that applied or reverted IS the region's new
    /// state (a reverted rebuild is the ledger's own record of it), and a region with no standing build keeps the refusal.
    /// </summary>
    public static RegionOutcome Standing(RegionOutcome? prior, RegionOutcome latest) =>
      latest == RegionOutcome.Refused && (prior == RegionOutcome.Built || prior == RegionOutcome.BuiltWithErrors)
        ? prior.Value
        : latest;

    /// <summary>Fold one attempt into the turn's per-region standing state (<see cref="Standing"/>).</summary>
    public static void FoldTurn(IDictionary<string, RegionOutcome> thisTurn, string address, RegionOutcome latest)
    {
      if (thisTurn == null || string.IsNullOrEmpty(address)) return;
      thisTurn[address] = Standing(thisTurn.TryGetValue(address, out var prior) ? prior : (RegionOutcome?)null, latest);
    }

    /// <summary>One region as the record holds it.</summary>
    public sealed class Row
    {
      public string Address;
      public RegionOutcome Outcome;
      public int Components;
      public string Note = "";
      /// <summary>How many builds this region took in this thread.</summary>
      public int Attempts = 1;
      /// <summary>What the FIRST attempt produced - the other half of the recovery question.</summary>
      public RegionOutcome FirstOutcome;

      /// <summary>Did a retry turn a region that was not clean into one that is? THE measurable form of
      /// #143's question: never spend on a measured zero, and you cannot measure it without this.</summary>
      public bool Recovered => Attempts > 1 && FirstOutcome != RegionOutcome.Built && Outcome == RegionOutcome.Built;
    }

    /// <summary>What the record says, in address order - the shape a reopened thread reads back.</summary>
    public static IReadOnlyList<Row> Rows(string json)
    {
      var rows = new List<Row>();
      var regions = (Parse(json)?["regions"] as JsonArray) ?? new JsonArray();

      foreach (var node in regions)
      {
        var o = node as JsonObject;
        var address = o?["address"]?.GetValue<string>();
        if (string.IsNullOrEmpty(address)) continue;

        var row = new Row { Address = address, Note = o["note"]?.GetValue<string>() ?? "" };

        var outcome = RegionOutcome.Refused;
        try { Enum.TryParse(o["outcome"]?.GetValue<string>() ?? "", out outcome); } catch { }
        row.Outcome = outcome;

        var first = outcome;
        try { Enum.TryParse(o["firstOutcome"]?.GetValue<string>() ?? "", out first); } catch { }
        row.FirstOutcome = first;

        try { row.Components = o["components"]?.GetValue<int>() ?? 0; } catch { }
        try { row.Attempts = Math.Max(1, o["attempts"]?.GetValue<int>() ?? 1); } catch { }

        rows.Add(row);
      }

      return rows
        .OrderBy(r => ScaffoldAddress.SortKey(r.Address).Letters, StringComparer.Ordinal)
        .ThenBy(r => ScaffoldAddress.SortKey(r.Address).Number)
        .ToList();
    }

    /// <summary>The older tuple shape, kept for callers that only need the outcome.</summary>
    public static IReadOnlyList<(string Address, RegionOutcome Outcome, int Components, string Note)> Read(string json)
    {
      var found = new List<(string, RegionOutcome, int, string)>();
      var regions = (Parse(json)?["regions"] as JsonArray) ?? new JsonArray();

      foreach (var node in regions)
      {
        var o = node as JsonObject;
        var address = o?["address"]?.GetValue<string>();
        if (string.IsNullOrEmpty(address)) continue;

        var outcome = RegionOutcome.Refused;
        try { Enum.TryParse(o["outcome"]?.GetValue<string>() ?? "", out outcome); } catch { }

        var components = 0;
        try { components = o["components"]?.GetValue<int>() ?? 0; } catch { }

        found.Add((address, outcome, components, o["note"]?.GetValue<string>() ?? ""));
      }

      return found
        .OrderBy(r => ScaffoldAddress.SortKey(r.Item1).Letters, StringComparer.Ordinal)
        .ThenBy(r => ScaffoldAddress.SortKey(r.Item1).Number)
        .ToList();
    }

    /// <summary>
    /// One region's line in the running summary - plugin-authored, with the region's own address in it.
    ///
    /// It states the outcome in the user's language and never softens the two that are not successes:
    /// a reverted region says the canvas is unchanged, and a built-but-erroring one says so plainly rather
    /// than being reported as done. A surface never states what the canvas contradicts.
    /// </summary>
    /// <summary>
    /// The initial state a built region actually set, bounded for a chat line (#394, ADR-035 at a UI row).
    ///
    /// The bound is the point, not an afterthought: a forty-component region would otherwise push the
    /// whole conversation off screen with a list nobody reads, which is how a witness stops being one.
    /// Four, then a count - the same shape every other payload in this system uses when it has more to
    /// say than room to say it, and it SAYS how many it left out rather than trailing off.
    /// </summary>
    public static string StateLine(IReadOnlyList<string> parts, int max = 4)
    {
      if (parts == null) return null;
      var shown = parts.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
      if (shown.Count == 0) return null;

      if (shown.Count <= max) return string.Join(", ", shown) + ".";
      var extra = shown.Count - max;
      return string.Join(", ", shown.Take(max)) + ", and " + extra + " more.";
    }

    public static string LineHtml(string address, RegionOutcome outcome, int components, string note) =>
      LineHtml(address, outcome, components, note, null);

    public static string LineHtml(string address, RegionOutcome outcome, int components, string note,
      IReadOnlyList<string> state) =>
      LineHtml(address, outcome, components, note, state, null);

    /// <param name="reconnected">#625: the wires this build put back - named on the running line in the plugin's own
    /// labels, so the user sees B1's XY Plane was kept connected, not only that A1 was rebuilt.</param>
    public static string LineHtml(string address, RegionOutcome outcome, int components, string note,
      IReadOnlyList<string> state, IReadOnlyList<ReconnectionNote> reconnected)
    {
      var sb = new StringBuilder();
      sb.Append("<p class=\"buildline\"><strong>").Append(WebUtility.HtmlEncode(address ?? "")).Append("</strong> ");

      switch (outcome)
      {
        // U26 (#916, #923): never the bare word "verified" - four builds carried it over an empty region, an invalid
        // subsurface, a 240x scale error and 486 zero-length lines. The NOTE beside this (the verdict's own account:
        // what was checked, and one post-solve fact per placed component) is where the line says what it knows.
        case RegionOutcome.Built:
          sb.Append("built - ").Append(components)
            .Append(components == 1 ? " component" : " components").Append(". One Ctrl-Z removes it.");
          break;
        case RegionOutcome.BuiltWithErrors:
          sb.Append("built, but Grasshopper reports errors in it. It is left standing so you can see " +
                    "them - a region can legitimately error until the ones it depends on exist.");
          break;
        case RegionOutcome.Reverted:
          sb.Append("could not be verified and was REVERTED - the canvas is as it was.");
          break;
        case RegionOutcome.RevertedWithResidue:
          sb.Append("could not be verified and was REVERTED, but not cleanly: the revert did not remove everything Dwight added.");
          break;
        default:
          sb.Append("was not built.");
          break;
      }

      // #394: on a SUCCESS, name the values that were set. Only on a success - a reverted build set
      // nothing, and printing what it would have set would be describing a canvas that does not exist.
      if (outcome == RegionOutcome.Built || outcome == RegionOutcome.BuiltWithErrors)
      {
        var values = StateLine(state);
        if (values != null)
          sb.Append(" <span class=\"buildstate\">").Append(WebUtility.HtmlEncode(values)).Append("</span>");

        // #625: what it kept connected, and what it restored.
        var all = reconnected ?? new ReconnectionNote[0];
        var kept = all.Where(r => !r.Restored).Select(r => r.Label).ToList();
        var restored = all.Where(r => r.Restored).Select(r => r.Label).ToList();
        if (kept.Count > 0)
          sb.Append(" <span class=\"buildreconnect\">").Append(WebUtility.HtmlEncode("Reconnected: " + string.Join("; ", kept) + "."))
            .Append("</span>");
        if (restored.Count > 0)
          sb.Append(" <span class=\"buildreconnect\">").Append(WebUtility.HtmlEncode("Restored: " + string.Join("; ", restored) + "."))
            .Append("</span>");
      }

      if (!string.IsNullOrWhiteSpace(note))
        sb.Append(" <span class=\"buildnote\">").Append(WebUtility.HtmlEncode(note)).Append("</span>");

      // SHOW-AND-EXPLAIN (ADR-054's clause), offered only where there is something to explain.
      //
      // It asks the AGENT to explain the region IN THE CONVERSATION rather than opening the Explain panel.
      // ADR-054 asked for "Explain on a region from inside the chat", and the agent already holds every
      // read that answers it - so this needs no new capability, no panel replacement, and no second
      // consent model. It is also #340's one-way flow in its easiest case: a READ has no spend-versus-
      // apply question to settle, so the direction can be honoured now rather than waiting for the study.
      //
      // The button posts a message the user can see, saying exactly what the label said it would - which
      // is the same per-message consent typing is (#182), made visible rather than implied.
      if (outcome == RegionOutcome.Built || outcome == RegionOutcome.BuiltWithErrors)
      {
        var safe = WebUtility.HtmlEncode(address ?? "");
        sb.Append(" <a class=\"anch\" href=\"#\" onclick=\"dwightSend('explainregion:")
          .Append(safe).Append("');return false;\">Explain ").Append(safe).Append("</a>");
      }

      sb.Append("</p>");
      return sb.ToString();
    }

    /// <summary>
    /// THE CLOSING REPORT: what was approved, against what actually landed (Stage E).
    ///
    /// #223 says an exhausted turn keeps what it produced and says what happened - and the running summary
    /// alone does not do that, because a region that was approved and NEVER ATTEMPTED produces no line at
    /// all. Silence is exactly how a half-finished build reads as a finished one, so the reconciliation is
    /// explicit: every approved address is accounted for, including the ones nothing happened to.
    ///
    /// <paramref name="stoppedEarly"/> is true when the turn ended on a budget, a timeout or a Stop rather
    /// than because the model finished - the difference between "this is all of it" and "this is as far as
    /// it got", which is the whole point of an honest stop.
    ///
    /// IT IS ABOUT THIS TURN, AND ITS SIGNATURE SAYS SO (#616). It used to be fed the THREAD's build record,
    /// which accumulates across every build turn in the conversation. An approved turn that attempted
    /// nothing therefore read an earlier turn's row and could close "Build finished. built and verified: A1"
    /// over a turn that built nothing - #253's lie, on the very gate step that rebuilds A1. So the close
    /// takes THIS turn's outcomes and nothing else: a thread record is not a parameter, and passing one does
    /// not compile. The persisted record stays thread-wide, because a reopened conversation reads it.
    /// </summary>
    public static string ClosingHtml(IReadOnlyList<string> approved,
                                     IReadOnlyDictionary<string, RegionOutcome> attemptedThisTurn, bool stoppedEarly,
                                     string autoClause = null)
    {
      if (approved == null || approved.Count == 0) return null;
      // Brief AL: when changes in this run were approved automatically the close line says so, in the plugin's own count
      // (ApprovalMode.CloseClause). Null leaves the line exactly as it was.
      var tail = string.IsNullOrEmpty(autoClause) ? "" : " " + WebUtility.HtmlEncode(autoClause);

      var thisTurn = attemptedThisTurn ?? new Dictionary<string, RegionOutcome>();
      var built = new List<string>();
      var withErrors = new List<string>();
      var failed = new List<string>();
      var untouched = new List<string>();

      foreach (var address in approved)
      {
        if (!thisTurn.TryGetValue(address, out var outcome)) { untouched.Add(address); continue; }
        switch (outcome)
        {
          case RegionOutcome.Built: built.Add(address); break;
          case RegionOutcome.BuiltWithErrors: withErrors.Add(address); break;
          default: failed.Add(address); break;
        }
      }

      var sb = new StringBuilder("<p class=\"buildclose\">");

      // #611e/#614: THE HEADLINE MAY NOT CLAIM A STATE THE BUILD DID NOT REACH (#253). The gate's build ran
      // nothing and closed "Build finished. not attempted: A1" - a headline asserting completion over a body
      // that contradicts it. "Finished" is said only when every approved region was attempted.
      //
      // #627: AND ONLY WHEN SOMETHING STANDS. The reverted-rebuild turn closed "Build finished. could not be
      // built: A1" - every region attempted, nothing built. The headline keys on what is on the canvas now.
      if (built.Count + withErrors.Count == 0)
      {
        if (stoppedEarly) sb.Append("<strong>Stopped before finishing.</strong> ");

        var clauses = new List<string>();
        if (failed.Count > 0) clauses.Add(Join(failed) + " could not be built");
        if (untouched.Count > 0) clauses.Add(Join(untouched) + (untouched.Count == 1 ? " was not attempted" : " were not attempted"));

        sb.Append("<strong>Nothing was built:</strong> ")
          .Append(WebUtility.HtmlEncode(string.Join("; ", clauses)))
          .Append(".");

        // #636: the trailer describes attempts that reverted or were refused, so it stays when one did - and
        // goes when nothing was attempted, because there is no attempt for it to describe.
        if (failed.Count > 0)
          sb.Append(" Nothing was reverted behind your back - what is on the canvas is what this says.");

        sb.Append(tail).Append("</p>");
        return sb.ToString();
      }

      if (stoppedEarly) sb.Append("<strong>Stopped before finishing.</strong> ");
      else if (untouched.Count > 0) sb.Append("<strong>Build ended before every approved region was attempted.</strong> ");
      else sb.Append("<strong>Build finished.</strong> ");

      var parts = new List<string>();
      // U26: what the check WAS, not the word. True of every region in `built` by construction - that outcome requires
      // completeness and preservation to hold and the new-error count to be zero.
      if (built.Count > 0)
        parts.Add("built and checked (placed as declared, nothing existing changed, no new errors): " + Join(built));
      if (withErrors.Count > 0) parts.Add("built but still reporting errors: " + Join(withErrors));
      if (failed.Count > 0) parts.Add("could not be built: " + Join(failed));
      // THE ONE THAT MUST NEVER BE SILENT: approved, and nothing happened to it.
      if (untouched.Count > 0) parts.Add("not attempted: " + Join(untouched));

      sb.Append(WebUtility.HtmlEncode(string.Join("; ", parts))).Append(".");

      if (withErrors.Count > 0 || failed.Count > 0 || untouched.Count > 0)
        sb.Append(" Nothing was reverted behind your back - what is on the canvas is what this says.");

      sb.Append(tail).Append("</p>");
      return sb.ToString();
    }

    private static string Join(IReadOnlyList<string> items) => string.Join(", ", items);

    private static JsonObject Parse(string json)
    {
      if (string.IsNullOrWhiteSpace(json)) return null;
      try { return JsonNode.Parse(json) as JsonObject; } catch { return null; }
    }
  }
}
