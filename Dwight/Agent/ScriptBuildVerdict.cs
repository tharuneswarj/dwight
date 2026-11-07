using System;
using System.Collections.Generic;
using System.Linq;
using Dwight.Simplify;

namespace Dwight.Agent
{
  /// <summary>What the emptiness oracle concluded about ONE script a build placed (U19, #898).</summary>
  internal enum ScriptBuildOutcome
  {
    /// <summary>It reported a compile or runtime error. Checked FIRST, before anything about data: an errored script still
    /// emits a null item on every output (Phase-7 WP-4, measured live), so reading the data first would pass a script that
    /// threw - the right outcome for the wrong reason (#887, #902).</summary>
    DidNotRun,

    /// <summary>It ran clean and at least one output holds at least one NON-null item. The build stands and the buildline
    /// states what it produced.</summary>
    Produced,

    /// <summary>It ran clean, nothing it declares holds data, and AT LEAST ONE INPUT WAS FED (or it has none to feed). The
    /// build is refused: the script has what it needs and still returned nothing.</summary>
    Empty,

    /// <summary>It ran clean, nothing it declares holds data, and EVERY input was left unwired by the spec. Nothing was fed,
    /// so emptiness proves nothing either way: the build stands, with a clause saying so. This is the vacuous-check class
    /// (#109) given its own value instead of a footnote on <see cref="Produced"/>.</summary>
    Unexercised,
  }

  /// <summary>One output of a script after the solve: how many items it holds, and how many of them are not null.</summary>
  internal sealed class ScriptOutputRead
  {
    public string Port { get; set; }
    public int Items { get; set; }
    public int NonNull { get; set; }
  }

  /// <summary>
  /// WHAT THE SOLVE SHOWED ABOUT ONE SCRIPT - facts only, no judgement and no sentence (the judgement is
  /// <see cref="ScriptBuildVerdict"/>'s, which is why it is tested without Grasshopper).
  /// </summary>
  internal sealed class ScriptBuildFacts
  {
    /// <summary>The spec's own ref for this component - what the model can name in its next attempt.</summary>
    public string Ref { get; set; }

    /// <summary>The nickname or type word, for sentences.</summary>
    public string Name { get; set; }

    /// <summary>"python" or "csharp"; decides which side of the compiler/interpreter line a failure is on.</summary>
    public string Language { get; set; }

    /// <summary>The component's compile or runtime errors, verbatim. Empty means it built and ran.</summary>
    public IReadOnlyList<string> Errors { get; set; } = new string[0];

    public IReadOnlyList<ScriptOutputRead> Outputs { get; set; } = new ScriptOutputRead[0];

    /// <summary>Every input the component has, in order.</summary>
    public IReadOnlyList<string> Inputs { get; set; } = new string[0];

    /// <summary>The inputs nothing is wired to AFTER the build.</summary>
    public IReadOnlyList<string> UnwiredInputs { get; set; } = new string[0];

    /// <summary>
    /// U92: the WIRED inputs that received no data, or only nulls - read by the plugin off each input's volatile data, never inferred from the
    /// error text. A script that fails on an input with nothing in it did not fail on its own logic, and the sentence says which it was.
    /// </summary>
    public IReadOnlyList<string> EmptyInputs { get; set; } = new string[0];

    /// <summary>
    /// U92 (addendum from the scale run's second file): the inputs the plugin GAVE item access (from the card's choice of access) whose sources
    /// upstream carry MORE THAN ONE item. Read off the live input and its sources, never inferred from the error text. A script that treats
    /// such an input as a list fails on access, not on logic ("'float' object is not iterable").
    /// </summary>
    public IReadOnlyList<string> ItemAccessMultiItemInputs { get; set; } = new string[0];

    /// <summary>U88: lines the normaliser put before the model's first line (compiled line - shift = the model's line). Zero: none.</summary>
    public int LineShift { get; set; }

    /// <summary>U88: the signature the component generated for a C# script, shown on a compile failure. Null: nothing to show.</summary>
    public string GeneratedSignature { get; set; }

    /// <summary>U88: parameter types of the model's signature that map to no hint, in the plugin's words.</summary>
    public IReadOnlyList<string> HintNotes { get; set; } = new string[0];
  }

  /// <summary>The verdict's script leg for a whole build: whether it stands, what the model is told if not, and the
  /// per-script sentences the buildline carries if it does.</summary>
  internal sealed class ScriptJudgement
  {
    public bool Stands { get; set; } = true;

    /// <summary>The refusal for the MODEL, whole: what failed, on which script, in the plugin's words, with the compiler's
    /// or interpreter's own and the way out. Null when the build stands.</summary>
    public string Refusal { get; set; }

    /// <summary>The same finding without the framing, for appending to another leg's failure.</summary>
    public string Detail { get; set; }

    /// <summary>U46: THE USER'S READING of the same finding - the compiler's or interpreter's words and the plugin's
    /// sentences, WITHOUT the reflected RhinoCommon signatures (repair material for the model: 8,825 characters in the
    /// U19 gate run) and without the model-addressed instruction. Null when the build stands.</summary>
    public string UserRefusal { get; set; }

    /// <summary>The user's reading of <see cref="Detail"/>, for appending to another leg's failure.</summary>
    public string UserDetail { get; set; }

    /// <summary>The per-script sentence the buildline carries, keyed by the spec's ref. Present for scripts that stand.</summary>
    public Dictionary<string, string> Notes { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>The outcome per ref, for the payload.</summary>
    public Dictionary<string, ScriptBuildOutcome> Outcomes { get; } = new Dictionary<string, ScriptBuildOutcome>(StringComparer.Ordinal);
  }

  /// <summary>
  /// THE THREE-STATE EMPTINESS ORACLE AND THE WORDS FOR IT (U19, #898, #902). GH-free (ADR-091): the live facts arrive as
  /// a <see cref="ScriptBuildFacts"/>, so every state and every sentence is asserted rather than reviewed.
  ///
  /// "CARRIES DATA" MEANS AT LEAST ONE NON-NULL ITEM, WITH ERRORS CHECKED FIRST. Both halves are the architect's ruling and
  /// both are earned: an errored script emits a null item on every output, and a script that runs clean and assigns None
  /// puts one there too, so a count of items says nothing about whether anything was produced. The rule is
  /// <c>Data.OutputItems.HasNonNull</c>'s, applied by the shim that reads the outputs - one definition (U20 had it wrong
  /// until #902 found it by reading).
  ///
  /// EMPTY AND UNEXERCISED ARE DIFFERENT VALUES. A script whose every input was left unwired by the spec was handed
  /// nothing: an empty result is what an honest script returns for nothing, and refusing it would send the model to
  /// "fix" source that may be right. A script that WAS fed (or needs no feed) and still produced nothing is a defect the
  /// build should not leave on the canvas. And the EMPTY refusal names the unwired inputs as well as the empty outputs
  /// (#898), because an output can be empty for want of an input and a refusal naming only outputs points at the wrong place.
  /// </summary>
  internal static class ScriptBuildVerdict
  {
    /// <summary>The leg's name in <c>failedLeg</c>.</summary>
    public const string Leg = "script";

    /// <summary>Scripts named in a refusal before the rest are counted (ADR-035).</summary>
    public const int MaxNamed = 3;

    public static ScriptBuildOutcome Outcome(ScriptBuildFacts f)
    {
      if (f == null) return ScriptBuildOutcome.DidNotRun;

      // ERRORS FIRST. Before a single item is counted.
      if (f.Errors != null && f.Errors.Any(e => !string.IsNullOrWhiteSpace(e))) return ScriptBuildOutcome.DidNotRun;

      if ((f.Outputs ?? new ScriptOutputRead[0]).Any(o => o != null && o.NonNull > 0)) return ScriptBuildOutcome.Produced;

      var inputs = f.Inputs ?? new string[0];
      var unwired = f.UnwiredInputs ?? new string[0];
      return inputs.Count > 0 && unwired.Count >= inputs.Count ? ScriptBuildOutcome.Unexercised : ScriptBuildOutcome.Empty;
    }

    /// <summary>Judge every script the build placed. No scripts is a build that stands with nothing to say.</summary>
    /// <param name="reflectedApi">The RhinoCommon reflection for a C# compile failure (the refusal carries the NO SUCH TYPE
    /// line as U37's does). Reflection is help, never a gate: a throw is swallowed and the refusal goes without it.</param>
    public static ScriptJudgement Judge(IReadOnlyList<ScriptBuildFacts> scripts, Func<IReadOnlyList<string>, string> reflectedApi)
    {
      var judgement = new ScriptJudgement();
      var all = (scripts ?? new ScriptBuildFacts[0]).Where(s => s != null).ToList();
      if (all.Count == 0) return judgement;

      var outcomes = all.Select(s => (Facts: s, Outcome: Outcome(s))).ToList();
      foreach (var o in outcomes) judgement.Outcomes[o.Facts.Ref ?? ""] = o.Outcome;

      var failed = outcomes.Where(o => o.Outcome == ScriptBuildOutcome.DidNotRun).ToList();
      if (failed.Count > 0)
      {
        judgement.Stands = false;
        judgement.Detail = DidNotRunDetail(failed.Select(f => f.Facts).ToList(), reflectedApi);
        // U46: the same detail with NO reflection - one composer, asked twice, so the two readings cannot tell two stories.
        judgement.UserDetail = Bound(DidNotRunDetail(failed.Select(f => f.Facts).ToList(), null));

        // U92: A SCRIPT WHOSE INPUTS WERE EMPTY IS TOLD SO, IN ITS OWN WORDS. When every script that failed had an input that received
        // no data, the build did not fail on the code: the sources upstream held nothing. The refusal says that, and does NOT tell the
        // model to "fix the script" - the old sentence did, on a script that was right and a file open without its model.
        if (failed.All(f => (f.Facts.EmptyInputs != null && f.Facts.EmptyInputs.Count > 0) ||
                            (f.Facts.ItemAccessMultiItemInputs != null && f.Facts.ItemAccessMultiItemInputs.Count > 0)))
        {
          judgement.UserRefusal = judgement.UserDetail + " The build was reverted; " + RevertMeasure.AsItWasPhrase + ".";
          judgement.Refusal = judgement.Detail + " The build was reverted; " + RevertMeasure.AsItWasPhrase + ".";
          return judgement;
        }
        judgement.UserRefusal =
          "The build placed " + (failed.Count == 1 ? "a script that did not run" : failed.Count + " scripts that did not run") +
          ", and was reverted - the canvas is as it was. " + judgement.UserDetail;
        judgement.Refusal =
          "The build placed " + (failed.Count == 1 ? "a script that did not run" : failed.Count + " scripts that did not run") +
          ", and was reverted - the canvas is as it was. " + judgement.Detail +
          " Fix the script and build again; this is not a limit of the tool.";
        return judgement;
      }

      var empty = outcomes.Where(o => o.Outcome == ScriptBuildOutcome.Empty).ToList();
      if (empty.Count > 0)
      {
        judgement.Stands = false;
        judgement.Detail = EmptyDetail(empty.Select(f => f.Facts).ToList());
        judgement.UserDetail = judgement.Detail;
        // U106: when every script that produced nothing had an input that read no data, the build did not fail on the code. As the
        // raised path does (U92), the refusal says so and does not tell the model to fix the code.
        if (empty.All(f => f.Facts.EmptyInputs != null && f.Facts.EmptyInputs.Count > 0 && (f.Facts.UnwiredInputs == null || f.Facts.UnwiredInputs.Count == 0)))
        {
          judgement.UserRefusal = judgement.UserDetail + " The build was reverted; " + RevertMeasure.AsItWasPhrase + ".";
          judgement.Refusal = judgement.Detail + " The build was reverted; " + RevertMeasure.AsItWasPhrase + ".";
          return judgement;
        }
        judgement.UserRefusal =
          "The build placed " + (empty.Count == 1 ? "a script that ran without an error but produced nothing" :
                                 empty.Count + " scripts that ran without an error but produced nothing") +
          ", and was reverted - the canvas is as it was. " + judgement.Detail;
        judgement.Refusal =
          "The build placed " + (empty.Count == 1 ? "a script that ran without an error but produced nothing" :
                                 empty.Count + " scripts that ran without an error but produced nothing") +
          ", and was reverted - the canvas is as it was. " + judgement.Detail +
          " An output can be empty for want of an input: wire what the code reads, or - if the inputs are right - fix " +
          "the code so it assigns its outputs, and build again. This is not a limit of the tool.";
        return judgement;
      }

      foreach (var o in outcomes) judgement.Notes[o.Facts.Ref ?? ""] = Note(o.Facts, o.Outcome);
      return judgement;
    }

    /// <summary>
    /// THE SENTENCE A STANDING SCRIPT ADDS TO THE BUILDLINE: what it produced, as outputs with items - or, when nothing
    /// was fed, that it was NOT EXERCISED and what its emptiness therefore proves. Never the word "verified".
    /// </summary>
    public static string Note(ScriptBuildFacts f, ScriptBuildOutcome outcome)
    {
      var outputs = f?.Outputs ?? new ScriptOutputRead[0];
      var unwired = f?.UnwiredInputs ?? new string[0];

      if (outcome == ScriptBuildOutcome.Unexercised)
      {
        var empties = outputs.Select(o => o.Port).Where(p => !string.IsNullOrEmpty(p)).ToList();
        return "script NOT EXERCISED - nothing is wired to its input" + (unwired.Count == 1 ? " " : "s ") + string.Join(", ", unwired) +
               ", so it ran with nothing fed to it" +
               (empties.Count > 0
                 ? " and " + (empties.Count == 1 ? "its empty output " + empties[0] + " proves" : "its empty outputs " + string.Join(", ", empties) + " prove") + " nothing"
                 : "");
      }

      var produced = outputs.Where(o => o.NonNull > 0).Select(o => o.Port + ": " + Items(o.NonNull)).ToList();
      var text = "script ran without an error and produced " + string.Join(", ", produced);
      var held = outputs.Where(o => o.NonNull == 0).Select(o => o.Port).Where(p => !string.IsNullOrEmpty(p)).ToList();
      if (held.Count > 0) text += "; no data on " + string.Join(", ", held);
      if (f?.HintNotes != null && f.HintNotes.Count > 0) text += "; " + string.Join("; ", f.HintNotes);
      if (unwired.Count > 0)
        text += "; nothing is wired to its input" + (unwired.Count == 1 ? " " : "s ") + string.Join(", ", unwired);
      return text;
    }

    private static string DidNotRunDetail(IReadOnlyList<ScriptBuildFacts> failed, Func<IReadOnlyList<string>, string> reflectedApi)
    {
      var parts = new List<string>();
      foreach (var f in failed.Take(MaxNamed))
      {
        var errors = f.Errors.Where(e => !string.IsNullOrWhiteSpace(e)).ToList();

        // U92: the inputs were empty - say so, name them, and quote the interpreter AFTER, as a fact. The old "Your script FAILED TO RUN ...
        // fix the script" stays for a script whose inputs held data.
        if ((f.EmptyInputs == null || f.EmptyInputs.Count == 0) && f.ItemAccessMultiItemInputs != null && f.ItemAccessMultiItemInputs.Count > 0)
        {
          var access = AccessText(f);
          parts.Add(failed.Count > 1 ? "Script \"" + f.Ref + "\" (" + f.Name + "): " + access : access);
          continue;
        }

        if (f.EmptyInputs != null && f.EmptyInputs.Count > 0)
        {
          var empty = EmptyInputsText(f);
          parts.Add(failed.Count > 1 ? "Script \"" + f.Ref + "\" (" + f.Name + "): " + empty : empty);
          continue;
        }

        string api = null;
        // THE REFLECTION IS C#-ONLY (U37, #930): it reflects RhinoCommon types out of a .NET compiler's error; a Python
        // NameError names a Python name, and answering it with "NO SUCH TYPE in RhinoCommon" is a category error.
        if (BuildScriptTypes.Compiled(f.Language) == true)
          try { api = reflectedApi?.Invoke(errors); } catch { api = null; }

        // U88: a C# diagnostic's position is given in the model's own numbering as well as the compiled file's.
        var shown = BuildScriptTypes.Compiled(f.Language) == true
          ? errors.Select(e => CSharpSignature.MapDiagnostic(e, f.LineShift)).ToList()
          : errors;
        var text = "Script \"" + f.Ref + "\" (" + f.Name + "):" + Environment.NewLine +
                   CompileRepair.Evidence(shown, BuildScriptTypes.Compiled(f.Language), api).TrimEnd();
        if (BuildScriptTypes.Compiled(f.Language) == true && !string.IsNullOrEmpty(f.GeneratedSignature))
          text += Environment.NewLine + "The component compiled your script against this generated signature, so these are the types " +
                  "your method is really handed: " + f.GeneratedSignature;
        if (f.HintNotes != null && f.HintNotes.Count > 0)
          text += Environment.NewLine + string.Join("; ", f.HintNotes) + ".";
        var unwired = f.UnwiredInputs ?? new string[0];
        if (unwired.Count > 0)
          text += Environment.NewLine + "Nothing is wired to its input" + (unwired.Count == 1 ? " " : "s ") + string.Join(", ", unwired) +
                  ", so an error that reads one of " + (unwired.Count == 1 ? "it" : "them") + " may be the script meeting an input " +
                  "with no data: wire it, or make the script cope with nothing there.";
        parts.Add(text);
      }
      var more = failed.Count - parts.Count;
      return string.Join(Environment.NewLine, parts) + (more > 0 ? Environment.NewLine + "And " + more + " more script" + (more == 1 ? "" : "s") + " failed." : "");
    }

    /// <summary>U106: the empty-input sentence for a script that RAN and returned nothing (it guarded against the empty input), so it does
    /// not say the script did not run: "Its input A read no data: the sources upstream hold nothing to compute with; Diagnose can say why."</summary>
    public static string EmptyInputsWhileRunning(IReadOnlyList<string> emptyInputs) =>
      "Its input" + (emptyInputs.Count == 1 ? " " : "s ") + string.Join(", ", emptyInputs) + " read no data: the sources upstream hold nothing to compute " +
      "with; Diagnose can say why.";

    /// <summary>
    /// U92: "The script did not run because its inputs were empty: A, B read no data. The sources upstream hold nothing to compute with;
    /// Diagnose can say why." then what the interpreter said, as a fact and not as the diagnosis.
    /// </summary>
    public static string EmptyInputsText(ScriptBuildFacts f)
    {
      var names = string.Join(", ", f.EmptyInputs);
      var text = "The script did not run because its inputs were empty: " + names + " read no data. " +
                 "The sources upstream hold nothing to compute with; Diagnose can say why.";
      var said = (f.Errors ?? new string[0]).Where(e => !string.IsNullOrWhiteSpace(e)).Select(e => e.Trim()).FirstOrDefault();
      if (said != null) text += " The interpreter said: " + (said.Length > 300 ? said.Substring(0, 300) + "..." : said);
      return text;
    }

    /// <summary>
    /// U92 addendum: "The input V was given one item at a time, and the script treats it as a list. It has item access because none other was declared; build
    /// again declaring list access on V, or change the script to take one item." Then what the interpreter said, as a fact.
    /// </summary>
    public static string AccessText(ScriptBuildFacts f)
    {
      var names = f.ItemAccessMultiItemInputs;
      var sb = new System.Text.StringBuilder();
      foreach (var name in names)
      {
        if (sb.Length > 0) sb.Append(' ');
        sb.Append("The input ").Append(name).Append(" was given one item at a time, and the script treats it as a list. ")
          .Append("It has item access because none other was declared; build again declaring list access on ").Append(name)
          .Append(", or change the script to take one item.");
      }
      var said = (f.Errors ?? new string[0]).Where(e => !string.IsNullOrWhiteSpace(e)).Select(e => e.Trim()).FirstOrDefault();
      if (said != null) sb.Append(" The interpreter said: ").Append(said.Length > 300 ? said.Substring(0, 300) + "..." : said);
      return sb.ToString();
    }

    private static string EmptyDetail(IReadOnlyList<ScriptBuildFacts> empty)
    {
      var parts = new List<string>();
      foreach (var f in empty.Take(MaxNamed))
      {
        var outs = (f.Outputs ?? new ScriptOutputRead[0]).Select(o => o.Port).Where(p => !string.IsNullOrEmpty(p)).ToList();
        var unwired = f.UnwiredInputs ?? new string[0];
        var emptyIn = f.EmptyInputs ?? new string[0];
        var text = "Script \"" + f.Ref + "\" (" + f.Name + "): " +
                   (outs.Count == 0
                     ? "it declares no outputs, so there is nothing for it to produce."
                     : (outs.Count == 1 ? "its output " + outs[0] + " holds no data." : "every output (" + string.Join(", ", outs) + ") holds no data.")) +
                   " " +
                   (unwired.Count > 0
                     ? "Nothing is wired to its input" + (unwired.Count == 1 ? " " : "s ") + string.Join(", ", unwired) + "."
                     // U106: a wired input that read no data is the reason, not the code - a script that guards against an empty input
                     // returns nothing without raising, and "returned nothing for what it was given" blamed the code for the empty input.
                     : emptyIn.Count > 0 ? EmptyInputsWhileRunning(emptyIn)
                     : "Every input is wired, so the code returned nothing for what it was given.");
        parts.Add(text);
      }
      var more = empty.Count - parts.Count;
      return string.Join(" ", parts) + (more > 0 ? " And " + more + " more script" + (more == 1 ? "" : "s") + " produced nothing." : "");
    }

    /// <summary>The user's line is bounded (ADR-035): five errors per script are already the cap, and this catches one
    /// enormous message. Cut at a line boundary where it can, and said.</summary>
    public const int MaxUserChars = 2500;

    private static string Bound(string text)
    {
      if (text == null || text.Length <= MaxUserChars) return text;
      var cut = text.LastIndexOf((char)10, MaxUserChars);
      if (cut < MaxUserChars / 2) cut = MaxUserChars;
      return text.Substring(0, cut).TrimEnd() + Environment.NewLine + "... (cut for length)";
    }

    private static string Items(int n) => n + " item" + (n == 1 ? "" : "s");
  }
}
