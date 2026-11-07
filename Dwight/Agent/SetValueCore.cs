using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using Dwight.Data;

namespace Dwight.Agent
{
  /// <summary>The exact change a set_value card promises, plugin-formatted from the validated state and the
  /// live read - never from the model's text. This is what the user approves and what the verdict names.</summary>
  internal sealed class SetValueChange
  {
    public int Id { get; set; }
    public string Type { get; set; }
    public string Nick { get; set; }
    /// <summary>What the component holds now, as <see cref="SetValueCore.Describe"/> renders it.</summary>
    public string Before { get; set; }
    /// <summary>What it will hold, the same way.</summary>
    public string After { get; set; }

    /// <summary>"Number Slider \"Count\" (42)", or "the Number Slider (42)" when it has no nickname.</summary>
    public string Label =>
      string.IsNullOrWhiteSpace(Nick) ? "the " + Type + " (" + Id + ")" : Type + " \"" + Nick + "\" (" + Id + ")";
  }

  /// <summary>
  /// The plugin's REPORT of a set_value outcome, in the two forms one record needs: HTML for the screen (a
  /// Dwight card) and plain text for the transcript, attributed, so the model reads what was done on the
  /// user's approval without any model call having sat between the approval and the execution (Q1).
  ///
  /// PUT THE PROVENANCE IN THE TEXT, NOT ONLY IN THE METADATA (the architect's naming of the property).
  /// The record's author field ("report") is what routes this into the user turn; the leading bracket in the
  /// text is what tells anyone - a person, a model, or an older build that lost the field - who did it and on
  /// whose approval. A consumer that loses the metadata then still gets the truth, which is why the one
  /// residual (an older build rendering a report as a user bubble) is cosmetic rather than misleading. The
  /// same instinct as a verdict row naming the values it set rather than relying on a field.
  ///
  /// WHY A REPORT, AND NOT #624's TURN-START FACT - two mechanisms, one reason: WHO ACTED, AND WHEN (#682).
  /// A BUILD runs INSIDE a model turn: the model has its own narrative of what it did, so the plugin's correction is
  /// re-asserted at the start of the NEXT turn, above whatever the model wrote (BuildOutcomeFact), and a user line
  /// cannot sit mid-turn. A SET_VALUE runs BETWEEN turns, from a click, with no model call (Q1): the transcript has a
  /// gap that only a transcript line can fill, and it must persist with the thread - an in-memory, last-only fact would
  /// be gone after a restart while the report was still on screen.
  ///
  /// A CLICK THAT CHANGES NOTHING IS REPORTED THE SAME WAY (#682). The model's last act was raising the card; a refusal
  /// rendered as chrome is invisible to it (#624's lesson), so it could believe the change ran.
  /// </summary>
  internal static class SetValueReport
  {
    /// <summary>The report's transcript line when the approved change could not be carried out and nothing was changed.</summary>
    public static string NotAppliedText(string reason) =>
      "[Not applied: the user approved this set_value, and Dwight changed nothing] " + (reason ?? "").Trim();

    /// <summary>The same, for the screen.</summary>
    public static string NotAppliedHtml(string reason) =>
      "<p><strong>Not applied.</strong> " + System.Net.WebUtility.HtmlEncode((reason ?? "").Trim()) + "</p>";

    public static string Text(ToolResult result)
    {
      var v = result?.Ok == true ? result.Result as JsonObject : result?.Detail;
      var change = Describe(v);
      if (result?.Ok == true)
        return "[Applied by Dwight on the user's approval] " + change + ": preserved (nothing else on the canvas changed) " +
               "and complete (it reads back exactly the declared state); one Ctrl-Z undoes it. Re-read the result and continue.";
      // #683 (6c): only a VerificationFailed result took an undo record and undid it. Every other failure the applier
      // returns changed nothing, and saying "REVERTED" about it would be a false claim about the canvas.
      if (!IsRevert(result)) return NotAppliedText(result?.Message);
      var why = v?["mismatch"]?.GetValue<string>();
      return "[Attempted by Dwight on the user's approval, and REVERTED] " + change + " could not be verified" +
             (string.IsNullOrEmpty(why) ? "" : " - " + why) + "; the canvas is as it was. " +
             (result?.Message ?? "");
    }

    public static string Html(ToolResult result)
    {
      var esc = new Func<string, string>(s => System.Net.WebUtility.HtmlEncode(s ?? ""));
      var v = result?.Ok == true ? result.Result as JsonObject : result?.Detail;
      var change = Describe(v);
      if (result?.Ok == true)
        return "<p><strong>Applied.</strong> " + esc(change) + " - nothing else changed, and it reads back exactly as " +
               "declared. One Ctrl-Z undoes it.</p>";
      if (!IsRevert(result)) return NotAppliedHtml(result?.Message);
      var why = v?["mismatch"]?.GetValue<string>();
      return "<p><strong>Reverted.</strong> " + esc(change) + " could not be verified" +
             (string.IsNullOrEmpty(why) ? "" : " - " + esc(why)) + ". The canvas is as it was.</p>";
    }

    private static bool IsRevert(ToolResult result) =>
      result != null && string.Equals(result.Code, ToolErrors.VerificationFailed, StringComparison.Ordinal);

    private static string Describe(JsonObject v)
    {
      // A detail that names no change (an empty detail, or one without before/after) is described as "the change" -
      // never "the component from ? to ?" (6c).
      if (v == null || v["before"] == null || v["after"] == null) return "the change";
      string S(string k) { try { return v[k]?.GetValue<string>(); } catch { return null; } }
      var t = v["target"] as JsonObject;
      string T(string k) { try { return t?[k]?.GetValue<string>(); } catch { return null; } }
      int? id = null; try { id = t?["id"]?.GetValue<int>(); } catch { }
      var label = string.IsNullOrWhiteSpace(T("nick"))
        ? "the " + (T("type") ?? "component") + (id.HasValue ? " (" + id + ")" : "")
        : (T("type") ?? "component") + " \"" + T("nick") + "\"" + (id.HasValue ? " (" + id + ")" : "");
      return label + " from " + (S("before") ?? "?") + " to " + (S("after") ?? "?");
    }
  }

  /// <summary>
  /// set_value's GH-free core (Phase-9 U4; #377, #381 R4; the Q1/Q2/Q4 rulings). A consented edit to an
  /// EXISTING slider or panel - the first write to something the user made, a different consent class
  /// from adding what Dwight placed (#338), with its own card and its own scope test.
  ///
  /// WHAT THE USER APPROVED IS WHAT EXECUTES - NOTHING MAY SIT BETWEEN AN APPROVAL AND ITS EXECUTION THAT
  /// COULD DIFFER FROM IT (the Q1 ruling). The card carries the validated (target, state); the click
  /// applies exactly that; no model call is placed between the two, because a contract executed by
  /// re-derivation is not the contract. The catalogue tool routes to the same applier, for the MCP face
  /// that will call it; in the panel loop it is granted on no turn.
  ///
  /// THE VERDICT IS SIMPLIFY's G.1 PATTERN: the predicted after-fingerprint is the before-document with
  /// ONLY the target's object replaced by its harvested post-apply tokens; actual == predicted proves that
  /// nothing ELSE changed, and the read-back equality of the declared state (stream A's GhBuildState.Matches)
  /// proves the intended change HAPPENED. Two legs, kept separate, so a reader sees which guard did the work.
  ///
  /// THE GAP, STATED TWICE (the Q4 ruling - here and in the verdict): the fingerprint's slider token carries
  /// the VALUE only, not the domain or the rounding, so a domain-only change on a component OTHER than the
  /// target is invisible to the fingerprint leg; only the read-back covers the target itself. What makes that
  /// acceptable is the applier's shape - it takes ONE guid and has no loop, so there is no path by which it
  /// could touch another component's domain. Extending the token is homed as its own unit: a slider's domain
  /// and rounding are behaviour, not decoration - they clamp and snap what the definition computes - so
  /// ADR-090's own principle argues for including them; the reason it is not done here is blast radius (the
  /// fingerprint is shared machinery under three verbs), not correctness.
  /// </summary>
  internal static class SetValueCore
  {
    /// <summary>
    /// THE ONE CONTINUATION an applied set_value grants (Q2): a RE-READ turn, and nothing more (#678).
    ///
    /// It is not ReadAndSolve. On main, ReadAndSolve IS the approved-SOLVE capability
    /// (<see cref="AgentHostTools.KindApprovedBy"/>): a continuation run with it would be told "the user has approved a
    /// solve" (<see cref="ApprovedTurnFact"/>), would refuse its own solve request as already approved, and would feed
    /// the solve breaker's dispatch arming - three claims about an approval that was never given. ReadOnly holds no
    /// approval at all. The apply's own ScheduleSolution recomputes downstream, so the reads return solved state without
    /// the model holding solve.
    /// </summary>
    public const TurnCapability Continuation = TurnCapability.ReadOnly;

    /// <summary>The Q4 gap, in the verdict's own words.</summary>
    public const string DomainGap =
      "The fingerprint leg compares slider VALUES, not domains or rounding, so a domain-only change on a " +
      "component other than the target would not trip it. The applier reaches exactly one component (this " +
      "one) and has no path to another, which is why the gap is acceptable; the target's own domain and " +
      "rounding are proven by the read-back.";

    /// <summary>
    /// The predicted after-fingerprint: <paramref name="before"/> with only the target's object replaced by
    /// <paramref name="harvested"/> - the live object as read AFTER the apply, the way Simplify harvests an
    /// added node's intrinsic tokens (G.1). A target absent from the before-document is not invented into
    /// the prediction: that would certify a placement as an edit.
    /// </summary>
    public static string Predict(FpDoc before, Guid target, FpObject harvested)
    {
      var objects = new List<FpObject>();
      foreach (var o in before?.Objects ?? new List<FpObject>())
      {
        if (o == null) continue;
        objects.Add(o.Guid == target && harvested != null ? harvested : o);
      }
      return BehaviourFingerprintCore.Compute(new FpDoc { Objects = objects });
    }

    /// <summary>
    /// The click-time scope rule (#338): the target must still be on the canvas AND still be the type the
    /// card named. Anything else REFUSES with the reason; nothing falls back to another component or to
    /// "whatever is there now" - scope is identity, and #208's rule for identity is resolve exactly or refuse.
    /// </summary>
    public static bool MayProceed(IdState state, string liveType, string cardType, out string refusal)
    {
      refusal = null;
      if (state != IdState.Alive)
      {
        refusal = "The component I named is no longer on the canvas (it was deleted, or an undo removed it), " +
                  "so nothing was changed. Ask me again and I will look at what is there now.";
        return false;
      }
      if (string.IsNullOrWhiteSpace(liveType) ||
          !string.Equals(liveType.Trim(), (cardType ?? "").Trim(), StringComparison.OrdinalIgnoreCase))
      {
        refusal = "The component I named is now a " + (string.IsNullOrWhiteSpace(liveType) ? "different object" : liveType) +
                  ", not the " + cardType + " the card described, so nothing was changed.";
        return false;
      }
      return true;
    }

    /// <summary>
    /// THE RESTORE RULE, measured over the bridge (Rhino 8.34, seven variants on in-memory documents) and
    /// ruled at option (1): one undo record does NOT restore a slider whose rounding changed to a grid its
    /// OLD value is not on - Grasshopper's restore replays the old value while the new accuracy is still in
    /// force, snaps it, then restores the accuracy (Float 0..1 @0.25 -> Integer 0..100 @40, undo -> value
    /// 0). Every other change measured restores exactly. So that one request is refused BEFORE it is
    /// asked, which makes the un-restorable case unrepresentable rather than repaired in the undo path -
    /// the single worst place in this product to add machinery. THE REFUSAL NAMES THE WAY THROUGH: the
    /// operation is achievable in two steps the measurements prove (value-only restores; an on-grid
    /// before-value restores), so it says which.
    /// </summary>
    public static bool MayChangeRounding(BuildState declared, BuildState current, out string refusal)
    {
      refusal = null;
      if (Allows(declared, current)) return true;

      var grid = declared.Rounding == SliderRounding.Integer ? "a whole number"
               : declared.Rounding == SliderRounding.Even ? "an even number" : "an odd number";
      var first = WayThrough(declared, current);
      refusal = "This slider holds " + Plain(current.Value) + " and " + declared.Rounding.ToString().ToLowerInvariant() +
                " rounding cannot represent it (it is not " + grid + "), so undo could not restore it. Do it in two " +
                "steps: first set it to " + (first == null ? "a value its current rounding and the new one both hold" : Describe(first)) +
                ", then make the change you asked for (" + Describe(declared) + ").";
      return false;
    }

    /// <summary>The measured rule itself, with no words attached.</summary>
    private static bool Allows(BuildState declared, BuildState current)
    {
      if (declared == null || declared.Kind != BuildStateKind.Slider) return true;
      if (current == null || current.Kind != BuildStateKind.Slider) return true;
      if (declared.Rounding == SliderRounding.Float) return true;                 // float's grid holds everything (variant G)
      if (declared.Rounding == current.Rounding) return true;                     // no accuracy change: the restore replays cleanly (B, C)
      return BuildStateParser.IsOnGrid(current.Value, declared.Rounding, out _, out _);   // variant F
    }

    /// <summary>
    /// THE FIRST STEP OF A WAY THROUGH THAT PASSES (#683) - or null when the declared change needs none.
    ///
    /// #626's law: a refusal that names a way forward must name one the plugin would ACCEPT. The branch's refusal named
    /// the NEW grid's neighbours of the current value ("set the value to an odd number first (3 or 5)"), and two states
    /// reached a step that would itself be refused: an Even-to-Odd flip, where 3 is not on the slider's CURRENT even grid,
    /// and a domain holding neither neighbour (0.1 to 0.9 has no whole number). So the step is COMPUTED and then CHECKED:
    /// each candidate must parse for a Number Slider and pass the restore rule both ways - step 1 from the current state,
    /// the declared change from step 1 - through the same functions the ask runs. Candidates, least change first:
    ///   1. the current domain and rounding, value only (value-only restores);
    ///   2. the current domain at INTEGER rounding (holds any even or odd value, so it bridges a parity flip);
    ///   3. the current domain at FLOAT rounding;
    ///   4-6. the same three on the DECLARED domain.
    /// The last always passes for a declared state that parsed: the declared domain and value at float rounding.
    /// </summary>
    public static BuildState WayThrough(BuildState declared, BuildState current)
    {
      if (Allows(declared, current)) return null;

      var domains = new[] { (current.Min, current.Max), (declared.Min, declared.Max) };
      var roundings = new[] { current.Rounding, SliderRounding.Integer, SliderRounding.Float };
      foreach (var (lo, hi) in domains)
        foreach (var r1 in roundings.Distinct())
        {
          var grid = Both(declared.Rounding, r1);
          if (grid == null) continue;
          var w = Nearest(Math.Min(hi, Math.Max(lo, current.Value)), lo, hi, grid.Value);
          if (w == null) continue;

          var json = new JsonObject
          {
            ["min"] = lo, ["max"] = hi, ["value"] = w.Value, ["rounding"] = r1.ToString().ToLowerInvariant(),
          };
          if (!BuildStateParser.TryParse(json, "Number Slider", "the first step", out var step, out _) || step == null) continue;
          if (Allows(step, current) && Allows(declared, step)) return step;
        }
      return null;
    }

    /// <summary>The grid both roundings hold, or null when they share none (even and odd).</summary>
    private static SliderRounding? Both(SliderRounding a, SliderRounding b)
    {
      if (a == SliderRounding.Float) return b;
      if (b == SliderRounding.Float || b == SliderRounding.Integer || a == b) return a;
      if (a == SliderRounding.Integer) return b;
      return null;
    }

    /// <summary>The value on <paramref name="grid"/> within [lo, hi] nearest to <paramref name="x"/> (the lower on a tie),
    /// or null when the domain holds none. <paramref name="grid"/> is never Float here: a Float target is never refused.</summary>
    private static decimal? Nearest(decimal x, decimal lo, decimal hi, SliderRounding grid)
    {
      var candidates = new List<decimal>();
      for (var c = decimal.Floor(x) - 2; c <= decimal.Ceiling(x) + 2; c++) candidates.Add(c);
      foreach (var edge in new[] { decimal.Ceiling(lo), decimal.Ceiling(lo) + 1, decimal.Floor(hi), decimal.Floor(hi) - 1 })
        candidates.Add(edge);

      return candidates
        .Where(c => c >= lo && c <= hi && BuildStateParser.IsOnGrid(c, grid, out _, out _))
        .OrderBy(c => Math.Abs(c - x)).ThenBy(c => c)
        .Select(c => (decimal?)c)
        .FirstOrDefault();
    }

    /// <summary>The catalogue tool's arguments - {id, state} - onto the same seed the ask uses, so the MCP
    /// face and the panel card validate through one path (<see cref="SetValueAsk.Validate"/>).</summary>
    public static bool TryParseArgs(JsonObject args, out HandoffSeed seed, out ToolResult error)
    {
      seed = null;
      error = null;
      var idNode = args?["id"];
      int id;
      try { id = idNode == null ? throw new FormatException() : idNode.GetValue<int>(); }
      catch
      {
        error = ToolResult.Fail(ToolErrors.InvalidArgs, "Requires \"id\" - the EXISTING component to change, as an integer from a previous read.");
        return false;
      }
      if (!(args["state"] is JsonObject state))
      {
        error = ToolResult.Fail(ToolErrors.InvalidArgs,
          "Requires \"state\" - an object: {min, max, value, rounding?} for a Number Slider, {text} for a Panel.");
        return false;
      }
      seed = new HandoffSeed { Target = id, State = state };
      return true;
    }

    /// <summary>A state in the user's words: "40 (0 to 100, integer)" or "\"the text\"" (trimmed).</summary>
    public static string Describe(BuildState state)
    {
      if (state == null) return "";
      if (state.Kind == BuildStateKind.Slider)
        return Plain(state.Value) + " (" + Plain(state.Min) + " to " + Plain(state.Max) + ", " +
               state.Rounding.ToString().ToLowerInvariant() + ")";
      var text = (state.Text ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
      if (text.Length > 60) text = text.Substring(0, 59) + "…";
      return "\"" + text + "\"";
    }

    private static string Plain(decimal d) => d.ToString(CultureInfo.InvariantCulture);

    // ---- THE APPLIER'S OUTCOMES, as one set of constructors (#683, 6c) ----------------------------------------------
    //
    // GhSetValue builds every result it returns HERE, so the report reads exactly the shapes the writer produces -
    // #649's lesson (a reader and a writer that never meet in a test can disagree about a key forever), applied before
    // it can happen. Two families, and the difference is a claim about the canvas:
    //   - REVERTED: an undo record was taken and the change was undone - VerificationFailed, and only that code;
    //   - NOT APPLIED: nothing was changed at all - no document, could not schedule, the target gone at apply, or an
    //     error before the undo record existed.

    public static ToolResult Applied(SetValueChange change) =>
      ToolResult.Success(Verdict(true, change, preserved: true, complete: true, mismatch: null));

    public static ToolResult Reverted(SetValueChange change, bool preserved, bool complete, string mismatch) =>
      ToolResult.Fail(ToolErrors.VerificationFailed,
        "The value was applied, failed its own check, and was reverted - the canvas is as it was.",
        Verdict(false, change, preserved, complete, mismatch));

    /// <summary>An error AFTER the undo record was taken: the change was undone.</summary>
    public static ToolResult RevertedAfterError(string error) =>
      ToolResult.Fail(ToolErrors.VerificationFailed, "The change could not be verified and was reverted (" + error + ").");

    /// <summary>An error BEFORE the undo record existed: nothing was changed, and nothing may be undone.</summary>
    public static ToolResult NotAppliedAfterError(string error) =>
      ToolResult.Fail(ToolErrors.PreconditionRefused, "The change could not be started, so nothing was changed (" + error + ").");

    public static ToolResult GoneAtApply(SetValueChange change) =>
      ToolResult.Fail(ToolErrors.RetiredId, "The component is no longer on the canvas, so nothing was changed.",
        new JsonObject
        {
          ["status"] = "not_applied",
          ["target"] = new JsonObject { ["id"] = change?.Id, ["type"] = change?.Type, ["nick"] = change?.Nick },
          ["note"] = "Nothing was changed.",
        });

    public static ToolResult NoDocument() =>
      ToolResult.Fail(ToolErrors.NoDocument, "No Grasshopper document is open, so nothing was changed.");

    public static ToolResult CouldNotSchedule(string error) =>
      ToolResult.Fail(ToolErrors.PreconditionRefused, "The change could not be scheduled, so nothing was changed (" + error + ").");

    /// <summary>
    /// The verdict, in the same two-leg shape as build_region's (preservation / completeness), plus what it
    /// does NOT prove (Q4). A reverted verdict says the canvas is as it was.
    /// </summary>
    public static JsonObject Verdict(bool applied, SetValueChange change, bool preserved, bool complete, string mismatch)
    {
      var v = new JsonObject
      {
        ["status"] = applied ? "applied" : "reverted",
        ["target"] = new JsonObject { ["id"] = change.Id, ["type"] = change.Type, ["nick"] = change.Nick },
        ["before"] = change.Before,
        ["after"] = change.After,
        ["preserved"] = preserved,
        ["complete"] = complete,
        ["proves"] = "preserved: the behaviour fingerprint after the change equals the one predicted from the " +
                     "declared change alone, so nothing else changed. complete: the component reads back exactly " +
                     "the declared state.",
        ["doesNotProve"] = DomainGap + " It does not say the new value is what the user wanted.",
        ["undo"] = applied ? "one Ctrl-Z restores the previous value" : "nothing to undo - the change was reverted",
      };
      if (!string.IsNullOrEmpty(mismatch)) v["mismatch"] = mismatch;
      if (!applied)
        v["note"] = "The change was applied, failed its own check, and was reverted - the canvas is as it was.";
      return v;
    }
  }
}
