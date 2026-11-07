using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace Dwight.Agent
{
  /// <summary>
  /// ONE WIRE A REBUILD WOULD CUT (#625): a removed object's output feeding an input the rebuild does NOT remove.
  /// Read by the shim off the live params BEFORE anything is removed, so every field is the plugin's own - the
  /// model never names a guid, a position or a source.
  /// </summary>
  internal sealed class CutWire
  {
    /// <summary>The consumer's top-level object. <see cref="Guid.Empty"/> when it could not be read.</summary>
    public Guid ConsumerGuid { get; set; }

    /// <summary>The consumer's registry id - what the model names it by.</summary>
    public int ConsumerId { get; set; }

    /// <summary>The consumer's type name, as the canvas reports it.</summary>
    public string ConsumerType { get; set; }

    /// <summary>The scaffold region holding the consumer, or null.</summary>
    public string ConsumerRegion { get; set; }

    /// <summary>The input param the wire lands on - the object itself for a floating param.</summary>
    public Guid InputGuid { get; set; }

    /// <summary>The input's label, or null for a floating param (it has one implicit input and no name).</summary>
    public string InputLabel { get; set; }

    /// <summary>Where the cut source sits in that input's ORDERED source list. Grasshopper merges multi-source
    /// data in source order, so a reconnection goes back exactly here.</summary>
    public int Position { get; set; }

    /// <summary>The removed output param the wire comes from.</summary>
    public Guid SourceGuid { get; set; }

    /// <summary>The removed object's type name.</summary>
    public string SourceType { get; set; }

    /// <summary>The removed output's label, or null when the source is a floating param.</summary>
    public string SourceOutputLabel { get; set; }

    /// <summary>
    /// True when the consumer is not a top-level object of this canvas (inside a cluster, or unowned). Such a wire
    /// cannot be checked - cluster internals are not in the fingerprint - so it is refused, never guessed at.
    /// </summary>
    public bool Unreadable { get; set; }

    /// <summary>
    /// True when this is not a cut but a RESTORABLE LOSS (#625's narrow sibling): the consumer already lost this
    /// source when <see cref="FromRegion"/>'s earlier components were removed, and a fresh build of that region may
    /// put it back at <see cref="Position"/>. Declaring it is optional; leaving it undeclared is never refused.
    /// </summary>
    public bool Restore { get; set; }

    /// <summary>For a restorable loss: the region whose earlier build fed it.</summary>
    public string FromRegion { get; set; }
  }

  /// <summary>A reconnection as the plugin will carry it out: which cut, fed by which declared new output.</summary>
  internal sealed class PlannedReconnection
  {
    public CutWire Cut { get; set; }
    public BuildReconnect Declared { get; set; }
  }

  /// <summary>
  /// A REBUILD KEEPS ITS BOUNDARY (#625), decided BEFORE anything is removed.
  ///
  /// Every wire a rebuild would cut must be matched by exactly one declared reconnection naming which NEW output
  /// feeds it. The plugin supplies the input and the position; the model supplies only the semantic judgement of
  /// which new output (ADR-056). Anything else refuses here - a validation refusal, so it spends no attempt - and
  /// it carries the ground truth (the cut list), so it is the repair loop rather than an error path.
  ///
  /// #647, THE CUT THE USER WANTS. If nothing in the new build should feed a cut input, a model told only
  /// "reconnect" loops or invents a mapping. So the refusal also says the way out: a build cannot remove that
  /// wire; the user deletes it by hand first, and then the rebuild cuts nothing.
  ///
  /// GH-free (ADR-091): the shim reads the cuts, this decides.
  /// </summary>
  internal static class RebuildBoundary
  {
    /// <summary>The sentence #647 requires wherever a model is told to reconnect.</summary>
    public const string WayOut =
      "If nothing in the new build should feed one of these, a build cannot remove that wire: tell the user to " +
      "delete the wire themselves first, and then the rebuild cuts nothing.";

    public static bool Match(string region, IReadOnlyList<CutWire> cuts, IReadOnlyList<BuildReconnect> declared,
                             out List<PlannedReconnection> planned, out ToolResult error)
    {
      planned = new List<PlannedReconnection>();
      var chosen = new List<PlannedReconnection>();
      error = null;
      cuts = cuts ?? new CutWire[0];
      declared = declared ?? new BuildReconnect[0];
      var where = string.IsNullOrEmpty(region) ? "this build" : "rebuilding " + region;

      var unreadable = cuts.Where(c => c.Unreadable && !c.Restore).ToList();
      if (unreadable.Count > 0)
      {
        error = ToolResult.Fail(ToolErrors.BoundaryCut,
          Capitalise(where) + " would cut " + Plural(unreadable.Count, "wire") + " into something Dwight cannot read " +
          "(inside a cluster, or not on this canvas's top level), so it cannot check a reconnection. Nothing was " +
          "changed. Tell the user: that wire has to be removed or rerouted by hand before this region can be rebuilt.",
          Detail(region, cuts, ReasonUnreadable));
        return false;
      }

      foreach (var d in declared)
      {
        var onInput = cuts.Where(c => c.ConsumerId == d.ToId && SameLabel(c.InputLabel, d.ToParam)).ToList();
        if (onInput.Count == 0)
        {
          error = ToolResult.Fail(ToolErrors.BoundaryCut,
            cuts.Count == 0
              ? Capitalise(where) + " cuts no wire and has no lost input to restore, so there is nothing to " +
                "reconnect. Remove \"reconnect\"."
              : "Nothing " + where + " removes feeds " + Target(d) + ", so there is nothing there to reconnect. " +
                "The wires it cuts are: " + List(cuts) + ".",
            Detail(region, cuts, "unknown_target"));
          return false;
        }

        CutWire cut;
        if (d.At.HasValue)
        {
          cut = onInput.FirstOrDefault(c => c.Position == d.At.Value);
          if (cut == null)
          {
            error = ToolResult.Fail(ToolErrors.BoundaryCut,
              Target(d) + " has no cut wire at " + d.At.Value + ". The cut wires on it are: " + List(onInput) + ".",
              Detail(region, cuts, "no_cut_at"));
            return false;
          }
        }
        else if (onInput.Count > 1)
        {
          error = ToolResult.Fail(ToolErrors.BoundaryCut,
            Target(d) + " is fed by " + onInput.Count + " wires " + where + " cuts, so each reconnection needs " +
            "\"at\" saying which: " + List(onInput) + ".",
            Detail(region, cuts, "needs_at"));
          return false;
        }
        else cut = onInput[0];

        if (chosen.Any(p => ReferenceEquals(p.Cut, cut)))
        {
          error = ToolResult.Fail(ToolErrors.BoundaryCut,
            "More than one reconnection names " + Describe(cut, true) + ". Each cut wire takes exactly one.",
            Detail(region, cuts, "duplicate"));
          return false;
        }

        chosen.Add(new PlannedReconnection { Cut = cut, Declared = d });
      }

      // A restorable loss left undeclared is a permission not taken, never a refusal (#648 iv).
      var missing = cuts.Where(c => !c.Restore && chosen.All(p => !ReferenceEquals(p.Cut, c))).ToList();
      if (missing.Count > 0)
      {
        error = ToolResult.Fail(ToolErrors.BoundaryCut,
          Capitalise(where) + " removes components that feed " + Plural(missing.Count, "wire") + " outside it, and " +
          "this build does not reconnect " + (missing.Count == 1 ? "it" : "them") + ": " + List(missing) + ". " +
          "Nothing was changed. Reconnect each in \"reconnect\" - {\"to\":{\"id\":<consumer>,\"param\":<input>}," +
          "\"from\":{\"ref\":<a component in this spec>,\"param\":<its output>}} - and the plugin puts it back in " +
          "the same place. " + WayOut,
          Detail(region, cuts, ReasonUnreconnected, missing));
        return false;
      }

      planned = chosen.OrderBy(p => p.Cut.InputGuid).ThenBy(p => p.Cut.Position).ToList();
      return true;
    }

    public const string TurnHeading = "REBUILD BOUNDARY:";

    /// <summary>
    /// WHAT AN APPROVED BUILD TURN'S REBUILDS WILL CUT, told as a fact at turn start (#625, #647) - so the model plans
    /// the reconnections when it writes the spec, instead of learning them from a refusal. Read from the canvas at
    /// turn start through the same cut read the build runs. Ends on the way out, so a wire nothing new should feed is
    /// relayed to the user rather than looped on or invented around. Null when no approved region cuts anything.
    /// </summary>
    public static string TurnFact(IReadOnlyDictionary<string, IReadOnlyList<CutWire>> cutsByRegion)
    {
      var regions = (cutsByRegion ?? new Dictionary<string, IReadOnlyList<CutWire>>())
        .Where(kv => kv.Value != null && kv.Value.Count > 0)
        .OrderBy(kv => ScaffoldAddress.SortKey(kv.Key).Letters, StringComparer.Ordinal)
        .ThenBy(kv => ScaffoldAddress.SortKey(kv.Key).Number)
        .ToList();
      if (regions.Count == 0) return null;

      var lines = new List<string>();
      foreach (var kv in regions)
      {
        var cuts = kv.Value.Where(c => !c.Restore).ToList();
        var restores = kv.Value.Where(c => c.Restore).ToList();

        if (cuts.Any(c => c.Unreadable))
          lines.Add(kv.Key + " cannot be rebuilt until the user removes or reroutes, by hand, a wire from it into " +
                    "something Dwight cannot read (inside a cluster, or not on this canvas's top level).");
        else if (cuts.Count > 0)
          lines.Add("rebuilding " + kv.Key + " cuts " + Plural(cuts.Count, "wire") + " you must reconnect in " +
                    "\"reconnect\": " + List(cuts) + ".");

        // #625's narrow sibling: an OPTION, said as one - never the obligation a cut is.
        if (restores.Count > 0)
          lines.Add("building " + kv.Key + " may restore " + Plural(restores.Count, "input") + " that lost " +
                    (restores.Count == 1 ? "its source" : "their sources") + " when " + kv.Key + "'s earlier " +
                    "components were removed - declare " + (restores.Count == 1 ? "it" : "each") + " in \"reconnect\" " +
                    "only if the new build should feed it again: " + List(restores) + ".");
      }

      // #647's way out is about a CUT; a region with only restorable losses has nothing to be let out of.
      var anyCut = regions.Any(kv => kv.Value.Any(c => !c.Restore));
      return TurnHeading + " " + string.Join(" ", lines) + (anyCut ? " " + WayOut : "");
    }

    /// <summary>One cut, in words - the plugin's own labels only.</summary>
    public static string Describe(CutWire c, bool withPosition)
    {
      if (c == null) return "";
      var s = (c.ConsumerType ?? "component") + " (" + c.ConsumerId + ")" +
              (string.IsNullOrEmpty(c.ConsumerRegion) ? "" : " in " + c.ConsumerRegion) +
              (string.IsNullOrEmpty(c.InputLabel) ? "" : " input " + c.InputLabel) +
              (c.Restore
                ? ", which lost its source when " + (c.FromRegion ?? "that region") + "'s earlier components were removed"
                : ", fed by " + (c.SourceType ?? "component") +
                  (string.IsNullOrEmpty(c.SourceOutputLabel) ? "" : "'s " + c.SourceOutputLabel));
      return withPosition ? s + " at " + c.Position : s;
    }

    /// <summary>The cuts as a list, naming a position only where one input carries more than one cut.</summary>
    public static string List(IReadOnlyList<CutWire> cuts)
    {
      var all = cuts ?? new CutWire[0];
      return string.Join("; ", all.Select(c =>
        Describe(c, all.Count(o => o.ConsumerId == c.ConsumerId && SameLabel(o.InputLabel, c.InputLabel)) > 1)));
    }

    /// <summary>The refusal's reason codes that mean "this rebuild would cut a wire it did not reconnect" - the #624
    /// fact's "refused: would cut" row class reads these, never the model-addressed message.</summary>
    public const string ReasonUnreconnected = "unreconnected";
    public const string ReasonUnreadable = "unreadable";

    /// <summary>A consumer as the plugin names it: type (id), region, input - no source.</summary>
    public static string ConsumerLabel(CutWire c) =>
      (c.ConsumerType ?? "component") + " (" + c.ConsumerId + ")" +
      (string.IsNullOrEmpty(c.ConsumerRegion) ? "" : " in " + c.ConsumerRegion) +
      (string.IsNullOrEmpty(c.InputLabel) ? "" : " input " + c.InputLabel);

    private static JsonObject Detail(string region, IReadOnlyList<CutWire> cuts, string reason,
                                     IReadOnlyList<CutWire> unreconnected = null) => new JsonObject
    {
      ["region"] = region ?? "",
      ["reason"] = reason ?? "",
      ["unreconnected"] = new JsonArray((unreconnected ?? new CutWire[0]).Select(c => (JsonNode)ConsumerLabel(c)).ToArray()),
      ["cuts"] = new JsonArray(cuts.Select(c => (JsonNode)new JsonObject
      {
        ["consumer"] = c.ConsumerId,
        ["consumerType"] = c.ConsumerType ?? "",
        ["consumerRegion"] = c.ConsumerRegion ?? "",
        ["input"] = c.InputLabel ?? "",
        ["at"] = c.Position,
        ["fedBy"] = c.SourceType ?? "",
        ["fedByOutput"] = c.SourceOutputLabel ?? "",
        ["readable"] = !c.Unreadable,
        ["restorable"] = c.Restore,
      }).ToArray()),
    };

    private static string Target(BuildReconnect d) =>
      "component " + d.ToId + (string.IsNullOrEmpty(d.ToParam) ? "" : " input " + d.ToParam);

    private static bool SameLabel(string a, string b) =>
      string.Equals(a ?? "", b ?? "", StringComparison.OrdinalIgnoreCase);

    private static string Plural(int n, string noun) => n + " " + noun + (n == 1 ? "" : "s");

    private static string Capitalise(string s) =>
      string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);
  }
}
