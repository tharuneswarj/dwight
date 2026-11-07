using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;

namespace Dwight.Agent
{
  /// <summary>What one full-path drill run actually observed. Every field is something that was SEEN,
  /// never something inferred from a later field - the verdict below is a pure function of these, so the
  /// reasoning is testable rather than reviewed.</summary>
  internal sealed class FullPathObservations
  {
    /// <summary>The drill got past its preconditions and posted a message through the chat's own door.
    /// False means nothing was exercised and no verdict is available at all.</summary>
    public bool Entered;

    /// <summary>How many steps the script HELD, and how many the loop actually consumed. Unequal means
    /// the loop stopped early - a step it never asked for is a leg it never walked.</summary>
    public int ScriptSteps;
    public int ProviderCalls;

    /// <summary>The loop's own outcome name (<see cref="TurnOutcome"/>), or null if it never returned.</summary>
    public string Outcome;

    /// <summary>Every tool envelope the loop fed back, in call order, paired with the tool it came from.
    /// The CONTENT is what makes a pass non-vacuous: a refused tool still lets the turn complete, so
    /// "the turn finished" is not evidence that the tool leg ran (#249).</summary>
    public List<(string Tool, string Envelope)> ToolResults = new List<(string, string)>();

    /// <summary>The scripted answer reached the thread RECORD - so the completion handler ran and the
    /// render door was called, rather than the loop merely returning to nobody.</summary>
    public bool AnswerRecorded;

    /// <summary>The drill gave up waiting.</summary>
    public bool TimedOut;

    /// <summary>Anything the drill wants shown beneath the verdict - the thread's tail on a failure,
    /// the precondition that refused, the exception text.</summary>
    public string Detail;
  }

  internal enum DrillVerdict
  {
    /// <summary>The claimed path was walked end to end and the claim is evidence.</summary>
    Measured = 0,
    /// <summary>Something short-circuited. NOT a pass, and the reason names which leg.</summary>
    NotMeasured = 1,
  }

  /// <summary>
  /// THE FULL-PATH TURN DRILL'S VERDICT (#268/#280, Phase-8 WP-4 Stage 0).
  ///
  /// WHY THIS DRILL EXISTS. The tool-dispatch drill starts MID-PATH: it calls
  /// <c>AgentLoop.CallToolLikeTheLoop</c>, which enters <c>Execute</c> directly and never traverses
  /// <c>AgentLoop.Run</c> or the chat entry above it. It passed while the product hung (#267), because
  /// the hang was in a leg it does not walk. **A drill that walks half the path and reports the whole of
  /// it PASSED is worse than no drill, because it spends the gate's credibility to say nothing.**
  ///
  /// WHY THE VERDICT IS A PURE FUNCTION HERE, AND NOT A FEW <c>if</c>s IN THE HARNESS. The wiring is
  /// GH-coupled and cannot be tested; the DECISION can, and the decision is the part that has gone wrong
  /// before - #249's drill printed pass criteria beside a result satisfying none of them. So the rule is
  /// asserted headlessly (including the case this drill was built for: the loop completed and NOTHING
  /// was rendered), and the placement carries a positive control instead of a test (#300's shape).
  ///
  /// IT IS NOT <c>#if DEBUG</c>, deliberately. A test whose subject exists only in a Debug build is a
  /// test a build configuration can silently empty - #252's class wearing a compiler flag.
  ///
  /// WHAT A PASS REQUIRES, and every clause is here because its absence has produced a false green
  /// somewhere in this phase:
  ///   1. it ENTERED (else nothing was exercised - #249);
  ///   2. the loop consumed EVERY scripted step (a loop that stops after one call has not looped);
  ///   3. the outcome is Complete;
  ///   4. every tool envelope says <c>ok</c> - a REFUSED tool lets the turn finish while the leg the
  ///      drill exists to walk never ran, which is the vacuous pass in its purest form;
  ///   5. the scripted answer reached the thread record - "it returned" is not a completion (#354).
  /// </summary>
  internal static class FullPathDrill
  {
    /// <summary>
    /// What this drill covers, and what it does NOT (#280 - a verdict that corrects an over-claiming
    /// name is a footnote arguing with a headline, so the bound is stated where the claim is made).
    /// </summary>
    public const string Coverage =
      "MEASURED, when the verdict below says so:\n" +
      "  the chat's own entry (RunTurn) -> DwightSession.BeginOn -> the provider and key guards ->\n" +
      "  Activate -> the worker -> AgentLoop.Run -> the transcript pre-flight -> tool dispatch ->\n" +
      "  the UI-thread marshal -> SolutionEnd -> the worker resuming -> the completion handler ->\n" +
      "  the thread record.\n" +
      "NOT COVERED:\n" +
      "  whether the WebView actually PAINTED the answer. C# cannot witness that - a missing shell\n" +
      "  function fails inside the WebView with nothing thrown, which is exactly how #276 shipped.\n" +
      "  tools/check-shell-render.js is the instrument for that half, and it runs in CI.\n" +
      "  A real provider: the model is a stub, so nothing here says anything about the wire, the\n" +
      "  prompt, thinking, or what a model would choose to call.";

    public static DrillVerdict Decide(FullPathObservations o, out string report)
    {
      var sb = new StringBuilder();

      if (o == null)
      {
        report = "NOT MEASURED - the drill produced no observations at all.";
        return DrillVerdict.NotMeasured;
      }

      // ---- the facts, always printed, whichever way the verdict lands -----------------------------
      sb.AppendLine("entered:          " + (o.Entered ? "yes" : "NO"));
      sb.AppendLine("provider calls:   " + o.ProviderCalls + " of " + o.ScriptSteps + " scripted");
      sb.AppendLine("outcome:          " + (o.Outcome ?? "(never returned)"));
      sb.AppendLine("answer recorded:  " + (o.AnswerRecorded ? "yes" : "NO"));
      foreach (var (tool, envelope) in o.ToolResults)
        sb.AppendLine("tool " + tool + ": " + (Ok(envelope) ? "ok" : "REFUSED - " + Reason(envelope)));
      sb.AppendLine();

      var failures = new List<string>();

      if (!o.Entered)
        failures.Add("it never entered - nothing below this line was exercised.");
      if (o.TimedOut)
        failures.Add("it timed out waiting for the turn to finish.");
      if (o.Entered && o.ProviderCalls < o.ScriptSteps)
        failures.Add("the loop asked for " + o.ProviderCalls + " of " + o.ScriptSteps +
                     " scripted steps, so it stopped before the script did - the remaining legs were not walked.");
      if (o.Entered && o.ProviderCalls > o.ScriptSteps)
        failures.Add("the loop asked for MORE steps than the script holds (" + o.ProviderCalls + " > " +
                     o.ScriptSteps + ") - the drill ran out of script, so what it measured after that is unknown.");
      if (o.Entered && !string.Equals(o.Outcome, TurnOutcome.Complete.ToString(), StringComparison.Ordinal))
        failures.Add("the turn ended as " + (o.Outcome ?? "(never returned)") + ", not Complete.");

      foreach (var (tool, envelope) in o.ToolResults)
        if (!Ok(envelope))
          failures.Add("\"" + tool + "\" was REFUSED (" + Reason(envelope) + "), so its leg never ran - " +
                       "a turn can finish perfectly well around a tool that did nothing.");

      if (o.Entered && !o.AnswerRecorded)
        failures.Add("THE TURN FINISHED AND ITS ANSWER NEVER REACHED THE THREAD. This is the shape of " +
                     "#273 - the work completes, the record is written, and the surface never shows it. " +
                     "It is the exact failure this drill was built for; do not read the outcome above as a pass.");

      if (failures.Count == 0)
      {
        sb.AppendLine("MEASURED - and this is a PASS.");
        sb.AppendLine("The whole path from the chat's own entry to a rendered completion was walked, with a");
        sb.AppendLine("MUTATING tool inside it. The scripted answer is in the thread, so the completion");
        sb.AppendLine("handler ran; every tool answered ok, so no leg was skipped by a refusal.");
        sb.AppendLine();
        sb.AppendLine(Coverage);
        report = sb.ToString();
        return DrillVerdict.Measured;
      }

      sb.AppendLine("NOT MEASURED - this is NOT a pass:");
      foreach (var f in failures) sb.AppendLine("  - " + f);
      sb.AppendLine();
      sb.AppendLine("Fix what it names and run it again. A drill that reports ok on a path it did not walk");
      sb.AppendLine("is worse than one that does not run.");
      sb.AppendLine();
      sb.AppendLine(Coverage);
      if (!string.IsNullOrWhiteSpace(o.Detail))
      {
        sb.AppendLine();
        sb.AppendLine("--- what the thread holds ---");
        sb.AppendLine(o.Detail);
      }
      report = sb.ToString();
      return DrillVerdict.NotMeasured;
    }

    /// <summary>Did this tool envelope report success? An unreadable envelope is NOT a success - the one
    /// thing a drill must never do is read an absence as a pass.</summary>
    private static bool Ok(string envelope)
    {
      try { return JsonNode.Parse(envelope)?["ok"]?.GetValue<bool>() == true; }
      catch { return false; }
    }

    private static string Reason(string envelope)
    {
      try
      {
        var error = JsonNode.Parse(envelope)?["error"] as JsonObject;
        var code = error?["code"]?.GetValue<string>();
        var message = error?["message"]?.GetValue<string>();
        if (code == null && message == null) return "unreadable envelope";
        return code + (message == null ? "" : ": " + Trim(message));
      }
      catch { return "unreadable envelope"; }
    }

    private static string Trim(string s) =>
      s.Length <= 160 ? s : s.Substring(0, 159) + "…";
  }
}
