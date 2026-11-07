using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace Dwight.Agent
{
  /// <summary>
  /// What a user-approved <c>run_script</c> turn is allowed to run: exactly the source the card showed, once. The grant is the
  /// U65 pattern - the text shown is the text run - so the tool is handed THIS object by the loop and never reads its source from
  /// the model's call. The model's own copy of the source is only compared against it.
  /// </summary>
  internal sealed class RunScriptGrant
  {
    /// <summary>The complete script as the card showed it, line endings normalised to \n.</summary>
    public string Source { get; set; }

    /// <summary>The model's one sentence, shown on the card as Dwight's reason; names the undo record and heads the report.</summary>
    public string Purpose { get; set; }

    /// <summary>The clock the approval carries, in seconds (default 60, at most 600).</summary>
    public int TimeoutSeconds { get; set; } = FullControl.DefaultTimeoutSeconds;

    private int _used;

    /// <summary>One approval covers one run. True the first time it is asked, false after - thread-safe, because the run is
    /// scheduled on one thread and asked for from another.</summary>
    public bool TryUse() => System.Threading.Interlocked.Exchange(ref _used, 1) == 0;
  }

  /// <summary>What a run of the script came to, as the runtime and the two snapshots report it. Plain data, so the tool result built
  /// from it is tested without Rhino.</summary>
  internal sealed class RunOutcome
  {
    /// <summary>Non-null when Rhino's Python 3 runtime could not be reached or refused to start: nothing ran.</summary>
    public string Unavailable { get; set; }

    /// <summary>The script's own exception, verbatim with its line, or null when it did not raise.</summary>
    public string Exception { get; set; }

    public DocSnapshot Before { get; set; }
    public DocSnapshot After { get; set; }
    public string Stdout { get; set; }
    /// <summary>U102: what the script wrote to its error stream, or null. A Python error usually arrives as <see cref="Exception"/>.</summary>
    public string Stderr { get; set; }
    public double Seconds { get; set; }

    /// <summary>U101: the objects the script added, which the plugin's one undo record covers.</summary>
    public int Added { get; set; }
    /// <summary>U101: the id the plugin's undo record was pushed with; empty when none was pushed.</summary>
    public Guid RecordId { get; set; }
    /// <summary>U101: the record on top of Grasshopper's undo stack when the run was measured; empty when the stack was empty.</summary>
    public Guid TopAfter { get; set; }
  }

  /// <summary>U101: whether the plugin's undo record for a run stands - measured from its id, never assumed.</summary>
  internal enum RunUndoState { Standing, NotPushed, NotFound }

  /// <summary>What the document looked like at one moment: the facts a script's report is computed from. Counts and one hash.
  /// Never geometry, never values.</summary>
  internal sealed class DocSnapshot
  {
    public int Components { get; set; }
    public int Wires { get; set; }
    public int Groups { get; set; }
    public int Errors { get; set; }
    public string Fingerprint { get; set; }
    /// <summary>Brief CA: objects in the Rhino document, or null when it could not be read. A count, never geometry.</summary>
    public int? RhinoObjects { get; set; }
    /// <summary>Brief CA: layers in the Rhino document, or null when it could not be read.</summary>
    public int? RhinoLayers { get; set; }
  }

  /// <summary>
  /// FULL CONTROL (U90, the maintainer's ruling of 30 October 2026): a switch on the Build chat, off by default, that lets
  /// the chat offer ONE more kind of card - run a Python 3 script, with the whole Rhino and Grasshopper API, that the user has read.
  ///
  /// THIS IS THE ONE PLACE THE PRODUCT STEPS OUTSIDE ITS OWN RULE, and it does so on purpose and says so (ADR-100). Everywhere
  /// else the plugin computes structure and the model only judges (ADR-056). Under Full control the model writes code that computes
  /// structure, because the user chose the mode and read the source. So the report is not a verdict: Dwight does not check what the
  /// script does, it measures what changed, and "verified" and "checked" are words reserved for what the plugin proves.
  ///
  /// GH-free (ADR-091): the sentences, the descriptor, the arguments' parse, the grant comparison and the report's formatting are
  /// here and tested; the run itself is <c>GhRunScript</c>.
  /// </summary>
  internal static class FullControl
  {
    public const string ToolName = "run_script";
    public const string Kind = ApprovalMode.KindRunScript;

    public const int DefaultTimeoutSeconds = 60;
    public const int MaxTimeoutSeconds = 600;
    public const int MaxStdoutChars = 2000;

    /// <summary>The line the runtime reads to choose Python 3. Added when the source has none, because the source the user read is
    /// Python 3 and the runtime picks a language from this header; it is NOT part of the approved text.</summary>
    public const string Shebang = "#! python 3";

    // ---- the sentences, verbatim from the ruling -----------------------------------------------------------------------

    public const string SettingLabel = "Full control";

    public const string SettingSentence =
      "Full control: everything Build has, plus Dwight can run a script you have read, with full access to Rhino and this definition. " +
      "Dwight cannot check what such a script does, and one Ctrl-Z may not undo everything it touches. It costs more tokens, and " +
      "nothing it does is proven. For people who know Grasshopper and Rhino and want speed. Turn it on only when you need it. " +
      "With the Do not ask approval mode, a script runs without asking, and you read it afterwards.";

    public const string ToolDescription =
      "Run Python 3 inside Rhino with RhinoCommon and the Grasshopper API, on the active document, once the complete source has been shown and " +
      "approved on a card. Use it when a build card cannot express the change: editing many existing components, data extraction, Rhino " +
      "document work, anything the build tools refuse. Dwight does not check what the script does; it reports what changed. " + EnvironmentSentence + " Prefer the " +
      "build tools when they can do the job.";

    public const string CardTitle = "Run this script?";

    public const string CardAuthority =
      "Dwight will run the code below inside Rhino, with full access to this definition and the Rhino document. This is Full control: " +
      "Dwight cannot check what the script does, and cannot stop it once it has started. One Ctrl-Z undoes what Grasshopper recorded; " +
      "changes to the Rhino document or outside it may not come back.";

    /// <summary>Said to the panel when the clock runs out and the script has not returned. It is never "stopped": the runtime
    /// offers no way to interrupt a script on Rhino's thread, so the sentence says what is true.</summary>
    public const string StillRunning = "The script is still running on Rhino's thread; Dwight cannot stop it.";

    public const string UndoClause = "One Ctrl-Z undoes what Grasshopper recorded.";

    // ---- U101: the report's Ctrl-Z clause, measured from the record's id -------------------------------------------------

    /// <summary>"One Ctrl-Z undoes the 3 objects the script added." - the record stands, with that many objects.</summary>
    public static string UndoStandingLine(int added) =>
      "One Ctrl-Z undoes the " + added + " object" + (added == 1 ? "" : "s") + " the script added.";

    public const string UndoNotPushedLine =
      "Nothing was recorded for Ctrl-Z: the script added no objects to this definition, and what it changed stays as it left it.";

    /// <summary>Brief CA: said beside the Ctrl-Z clause when the Rhino document's counts moved. Rhino's own undo is named and not promised, because nothing has
    /// measured what it does for a script run this way (<c>RunContext.RecordDocumentUndo</c> is never set).</summary>
    public const string RhinoUndoLine =
      "Grasshopper's Ctrl-Z does not reach the Rhino document, so those changes stay there. Rhino's own Undo may take them back; Dwight has not checked that.";

    public const string UndoNotFoundLine =
      "The undo record was not found after the run; what the script changed may stay.";

    /// <summary>The state of the run's undo record. Nothing pushed because nothing was added is NotPushed; objects added but no record
    /// (the push failed), or a record that is not on top after the run (something recorded after it, or the stack dropped it), is
    /// NotFound - never the reassuring sentence.</summary>
    public static RunUndoState UndoStateOf(RunOutcome o)
    {
      if (o == null) return RunUndoState.NotFound;
      if (o.RecordId == Guid.Empty) return o.Added == 0 ? RunUndoState.NotPushed : RunUndoState.NotFound;
      return o.TopAfter == o.RecordId ? RunUndoState.Standing : RunUndoState.NotFound;
    }

    public static string UndoLine(RunOutcome o)
    {
      string line;
      switch (UndoStateOf(o))
      {
        case RunUndoState.Standing: line = UndoStandingLine(o.Added); break;
        case RunUndoState.NotPushed: line = UndoNotPushedLine; break;
        default: line = UndoNotFoundLine; break;
      }
      return RhinoChanged(o?.Before, o?.After) ? line + " " + RhinoUndoLine : line;
    }

    /// <summary>Did the Rhino document's counts move? False when either reading is missing: a count that was not read is not a change.</summary>
    internal static bool RhinoChanged(DocSnapshot before, DocSnapshot after) =>
      before?.RhinoObjects != null && after?.RhinoObjects != null && before.RhinoLayers != null && after.RhinoLayers != null &&
      (before.RhinoObjects != after.RhinoObjects || before.RhinoLayers != after.RhinoLayers);

    /// <summary>" In the Rhino document: +2 objects, +1 layer." Counts and never geometry (ADR-100). Empty when it could not be read, and it says what a
    /// count cannot see, so nobody reads "0" as nothing having happened there.</summary>
    internal static string RhinoClause(DocSnapshot before, DocSnapshot after)
    {
      if (before?.RhinoObjects == null || after?.RhinoObjects == null || before.RhinoLayers == null || after.RhinoLayers == null) return "";
      return " In the Rhino document: " + Delta(before.RhinoObjects.Value, after.RhinoObjects.Value, "object") + ", " +
             Delta(before.RhinoLayers.Value, after.RhinoLayers.Value, "layer") +
             (RhinoChanged(before, after) ? "." : " (a count: an edit that adds or removes nothing does not show here).");
    }

    // ---- the descriptor ----------------------------------------------------------------------------------------------------

    /// <summary>The run_script tool. NOT in <see cref="AgentToolCatalog.All"/>: a thread with Full control off has the tool list it
    /// had before this existed, byte for byte, and this tool joins a turn only through <see cref="AgentLoopCore.ToolsFor(TurnCapability, bool)"/>.</summary>
    public static ToolDescriptor Descriptor() => new ToolDescriptor
    {
      Name = ToolName,
      Description = ToolDescription,
      Mutates = true,
      Solves = false,
      InputSchema = Sch.Object(Sch.Props(
        ("source", Sch.Str("The COMPLETE Python 3 script, identical to the text on the card the user approved. A changed script is refused.")),
        ("purpose", Sch.Str("One sentence on what the script is for. It is the reason the card showed, and it names the undo step.")),
        ("timeoutSeconds", Sch.Int("Optional. How long Dwight waits for the script, in seconds. Default " + DefaultTimeoutSeconds +
                                   ", at most " + MaxTimeoutSeconds + ". Dwight cannot interrupt a script that overruns it."))),
        "source", "purpose"),
    };

    // ---- the kind on request_mutation --------------------------------------------------------------------------------------

    public static string KindCan =>
      "shows ONE Python 3 script whole on a card, which runs on the user's approval, or at once in a chat whose approval mode is Do not ask. It runs inside Rhino with the full RhinoCommon and Grasshopper API on " +
      "the active document - the way out when no other kind can express the change. Full control is on for this conversation. " + EnvironmentSentence;

    public static string KindCannot =>
      "be checked: Dwight does not verify what the script does, only reports what changed, and one Ctrl-Z undoes only what Grasshopper " +
      "recorded. Prefer set_value, edit and build when they can do the job.";

    /// <summary>
    /// The published request_mutation schema with the run_script kind added, for a turn on a thread whose Full control is on. The
    /// base schema is edited, not rebuilt, so every other word of it is the base's own and a thread with Full control off never
    /// passes through here at all.
    /// </summary>
    public static JsonObject WithRunScriptKind(JsonObject baseSchema)
    {
      var schema = (JsonObject)JsonNode.Parse(baseSchema.ToJsonString());
      var props = (JsonObject)schema["properties"];
      var kind = (JsonObject)props["kind"];
      var values = (JsonArray)kind["enum"];
      if (!values.Any(v => v?.GetValue<string>() == Kind)) values.Add(Kind);
      kind["description"] = (kind["description"]?.GetValue<string>() ?? "") + " " + Kind + " = CAN: " + KindCan + " CANNOT: " + KindCannot;

      var source = (JsonObject)props["source"];
      source["description"] = (source["description"]?.GetValue<string>() ?? "") +
        " For kind=" + Kind + ": the COMPLETE Python 3 script, which the user reads whole before it runs. The card's reason is your \"why\".";
      props["timeoutSeconds"] = Sch.Int("For kind=" + Kind + " ONLY. How long Dwight waits for the script, in seconds. Default " +
                                        DefaultTimeoutSeconds + ", at most " + MaxTimeoutSeconds + ".");
      return schema;
    }

    /// <summary>
    /// A paragraph stated to the model at turn start on a thread with Full control on - a fact the plugin states, because an
    /// instruction the model must remember does not bind (#573). Absent when the switch is off, so that prompt is unchanged.
    /// </summary>
    public static string ThreadFact(string approvalMode = null) =>
      "FULL CONTROL IS ON FOR THIS CONVERSATION. Besides the kinds above you may ask for kind=" + Kind + ": one Python 3 script, with the " +
      "complete source in \"source\" and your one-sentence reason in \"why\", " +
      (RunsWithoutAsking(approvalMode)
        ? "which runs without asking when your turn ends, because this chat's approval mode is Do not ask: the card stays in the chat marked as approved automatically, and the plugin's report comes back to you. Write nothing you would not run, "
        : "which the user reads whole on a card before it runs, ") +
      "with full access to Rhino and this definition. Use it only when no other kind can do the job. Dwight does not check what the script does and " +
      "will not call the result checked or verified; it reports what changed. Never describe a run as proven. " + EnvironmentSentence;

    /// <summary>Does a script run without asking in this chat? <see cref="ApprovalMode.Decide"/>'s own answer, so a sentence and the table cannot disagree.</summary>
    public static bool RunsWithoutAsking(string approvalMode, bool fullControl = true) =>
      ApprovalMode.Decide(approvalMode, Kind, new ApprovalFacts { FullControl = fullControl }) == ApprovalDecision.Auto;

    // ---- Brief CA: a card waiting for the person -----------------------------------------------------------------------------

    /// <summary>What a turn is told while a run_script card waits. A card that asks, in a chat where the person typed instead of clicking, was raised
    /// again and again because nothing told the model it was still there, and the source is here because the model has no other way to know what it asked.</summary>
    public static string WaitingFact(string why, string source) =>
      "A CARD IS WAITING FOR THE PERSON: you asked to run a script (\"" + (why ?? "").Trim() + "\"), and they have not answered. A typed message is not an answer to " +
      "it, and only their click runs it. Do not ask again unless the script itself must change: tell them in one sentence that the card " +
      "above is waiting for them. Asking again for the same script raises no new card. The source on that card:\n```python\n" + (source ?? "") + "\n```";

    /// <summary>Is this the script already waiting? The grant's own comparison, so it agrees with what a run would accept.</summary>
    public static bool SameScript(string waiting, string asked) => !string.IsNullOrEmpty(waiting) && FirstDifference(waiting, asked) == null;

    /// <summary>The answer to asking again for the script already waiting: no new card, no new token, and the turn ends, as it does on a shown card.</summary>
    public static string StillWaitingResult() =>
      ToolResult.Success(new JsonObject
      {
        ["cardShown"] = true,
        ["kind"] = Kind,
        ["approved"] = false,
        ["turnEnds"] = true,
        ["alreadyWaiting"] = true,
        ["whereItIs"] = "This same script's card is already in this chat, waiting for the person. No new card was raised.",
        ["howTheAnswerArrives"] = "As a NEW message in this conversation if they approve. Only their click runs it.",
        ["doNotCallAgain"] = "Tell them in one sentence that the card is waiting. Stop here.",
      }).ToJson();

    // ---- the ask (request_mutation, kind=run_script) --------------------------------------------------------------------

    /// <summary>
    /// Read the run_script ask: the complete source, the reason, the clock. Refused by name, never quietly shortened - an empty
    /// source is not a card, and one over the size bound is not offered, because the user must be able to read all of it.
    /// </summary>
    public static bool TryParseAsk(JsonObject args, string why, out RunScriptGrant grant, out ToolResult error)
    {
      grant = null; error = null;

      var source = StringOf(args, "source");
      if (string.IsNullOrWhiteSpace(source))
      {
        error = ToolResult.Fail(ToolErrors.InvalidArgs,
          "kind=" + Kind + " needs \"source\": the COMPLETE Python 3 script the user will read before it runs.");
        return false;
      }
      source = NormaliseSource(source);
      if (source.Length > EditScriptAsk.MaxSourceChars)
      {
        error = ToolResult.Fail(ToolErrors.InvalidArgs,
          "That script is " + source.Length + " characters and Dwight's limit is " + EditScriptAsk.MaxSourceChars +
          ", because the user reads all of it on the card before it runs. Split the work into smaller scripts.");
        return false;
      }
      if (string.IsNullOrWhiteSpace(why))
      {
        error = ToolResult.Fail(ToolErrors.InvalidArgs,
          "kind=" + Kind + " needs \"why\": one sentence on what the script is for. It is the reason the card shows the user.");
        return false;
      }
      if (!TryTimeout(args, out var timeout, out error)) return false;

      grant = new RunScriptGrant { Source = source, Purpose = why.Trim(), TimeoutSeconds = timeout };
      return true;
    }

    /// <summary>The model's call to run_script after the approval: its copy of the source and its purpose.</summary>
    public static bool TryParseCall(JsonObject args, out string source, out string purpose, out int? timeout, out ToolResult error)
    {
      source = null; purpose = null; timeout = null; error = null;
      source = StringOf(args, "source");
      purpose = StringOf(args, "purpose");
      if (string.IsNullOrWhiteSpace(source))
      {
        error = ToolResult.Fail(ToolErrors.InvalidArgs, "run_script needs \"source\": the identical script the user approved.");
        return false;
      }
      if (string.IsNullOrWhiteSpace(purpose))
      {
        error = ToolResult.Fail(ToolErrors.InvalidArgs, "run_script needs \"purpose\": one sentence on what the script is for.");
        return false;
      }
      if (args != null && args["timeoutSeconds"] != null)
      {
        if (!TryTimeout(args, out var t, out error)) return false;
        timeout = t;
      }
      source = NormaliseSource(source);
      purpose = purpose.Trim();
      return true;
    }

    private static bool TryTimeout(JsonObject args, out int timeout, out ToolResult error)
    {
      timeout = DefaultTimeoutSeconds; error = null;
      JsonNode n = args?["timeoutSeconds"];
      if (n == null) return true;
      int t;
      try { t = n.GetValue<int>(); }
      catch
      {
        error = ToolResult.Fail(ToolErrors.InvalidArgs, "\"timeoutSeconds\" must be a whole number of seconds, 1 to " + MaxTimeoutSeconds + ".");
        return false;
      }
      if (t < 1 || t > MaxTimeoutSeconds)
      {
        error = ToolResult.Fail(ToolErrors.InvalidArgs,
          "\"timeoutSeconds\" was " + t + "; it must be between 1 and " + MaxTimeoutSeconds + ". Dwight does not shorten or lengthen it for you.");
        return false;
      }
      timeout = t;
      return true;
    }

    private static string StringOf(JsonObject args, string name)
    {
      try { return args?[name]?.GetValue<string>(); } catch { return null; }
    }

    /// <summary>Line endings as \n, so the card, the grant and the comparison all hold one text.</summary>
    public static string NormaliseSource(string source) =>
      (source ?? "").Replace("\r\n", "\n").Replace('\r', '\n');

    // ---- the grant and the comparison -------------------------------------------------------------------------------------

    /// <summary>
    /// THE TEXT SHOWN IS THE TEXT RUN. Null when the source the model sent is the one the card showed; otherwise a sentence naming
    /// the FIRST differing line, both sides, bounded - so the model can see what it changed and the user is never asked to
    /// trust a script that is not the one they read.
    /// </summary>
    public static string FirstDifference(string approved, string sent)
    {
      var a = NormaliseSource(approved).Split('\n');
      var b = NormaliseSource(sent).Split('\n');
      var n = Math.Max(a.Length, b.Length);
      for (var i = 0; i < n; i++)
      {
        var left = i < a.Length ? a[i] : null;
        var right = i < b.Length ? b[i] : null;
        if (string.Equals(left, right, StringComparison.Ordinal)) continue;
        return "The script is not the one the user approved. It first differs at line " + (i + 1) + ": the card showed " +
               Quote(left) + " and you sent " + Quote(right) + ". Send the identical text, or ask the user again.";
      }
      return null;
    }

    private static string Quote(string line) =>
      line == null ? "nothing (the line is missing)" : "\"" + (line.Length > 120 ? line.Substring(0, 120) + "..." : line) + "\"";

    /// <summary>The source as it is handed to the runtime: the approved text, with the language header added when it has none. The
    /// header is a line the runtime needs, not part of what the user read, and is the only thing added.</summary>
    public static string ExecutableSource(string approved)
    {
      var text = NormaliseSource(approved);
      var lines = text.Split('\n');
      var own = OwnShebangLine(lines);

      // Idempotent: a source that already carries the preamble straight after its header is returned as it is.
      if (own > 0 && HasPreambleAt(lines, own)) return text;

      var preamble = string.Join("\n", PreambleLines);
      if (own == 0) return Shebang + "\n" + preamble + "\n" + text;

      // The model's own header stays the first thing the runtime reads; the preamble goes straight after it.
      var head = string.Join("\n", lines.Take(own));
      var rest = string.Join("\n", lines.Skip(own));
      return head + "\n" + preamble + (lines.Length > own ? "\n" + rest : "");
    }

    // ---- U104: THE PREAMBLE - a fact the plugin states, because an instruction the model must remember does not bind -------------

    /// <summary>
    /// The five lines Dwight binds before the script runs, byte-pinned by a test. The script runs in Rhino's Python 3 runtime
    /// (<c>RhinoCode.RunScript</c>), not inside a script component, so <c>ghenv</c> and <c>scriptcontext</c> do not exist; a model that
    /// reached the definition through <c>ghenv</c> raised "'NoneType' object has no attribute 'Component'" at the second gate (row D(1)).
    /// A deterministic format class the plugin can compute is the plugin's (the UsingNormalizer law): it owns the header, the model
    /// supplies the body. A binding the script makes itself comes later and so wins; no conflict handling is needed.
    /// </summary>
    public static readonly string[] PreambleLines =
    {
      "# --- Dwight's preamble: these names are bound before your script runs ---",
      "import Rhino, Grasshopper, System",
      "rhdoc = Rhino.RhinoDoc.ActiveDoc",
      "ghdoc = Grasshopper.Instances.ActiveCanvas.Document if Grasshopper.Instances.ActiveCanvas else None",
      "# --- your script follows ---",
    };

    public static int PreambleLineCount => PreambleLines.Length;

    /// <summary>The card's summary clause, after the document test: what the plugin adds.</summary>
    public const string PreambleSummaryClause = "Runs with Dwight's preamble: rhdoc and ghdoc bound.";

    /// <summary>The fixed note under the fold. The fold is the approved source byte for byte, as the grant compares it, so the preamble
    /// is stated beside it and not inside it.</summary>
    public const string PreambleNote =
      "Dwight adds five lines before your script that import Rhino, Grasshopper and System and bind rhdoc (the Rhino document) and " +
      "ghdoc (this definition). Nothing else is added.";

    /// <summary>The descriptor's environment sentence (the published descriptor is the contract).</summary>
    public const string EnvironmentSentence =
      "The script runs in Rhino's Python 3 runtime, not inside a script component: there is no ghenv and no scriptcontext; rhdoc is the " +
      "Rhino document and ghdoc is this definition, bound by Dwight before your script; print goes to the report.";

    /// <summary>What a position inside the plugin's own lines reports as.</summary>
    public const string InPreamble = "(in Dwight's preamble)";

    /// <summary>1-based number of the first non-blank line when it is the source's own language header, else 0.</summary>
    private static int OwnShebangLine(string[] lines)
    {
      for (var i = 0; i < lines.Length; i++)
      {
        if (lines[i].Trim().Length == 0) continue;
        return lines[i].Trim().StartsWith("#!", StringComparison.Ordinal) ? i + 1 : 0;
      }
      return 0;
    }

    private static bool HasPreambleAt(string[] lines, int afterLine)
    {
      if (lines.Length < afterLine + PreambleLines.Length) return false;
      for (var i = 0; i < PreambleLines.Length; i++)
        if (!string.Equals(lines[afterLine + i].TrimEnd(), PreambleLines[i], StringComparison.Ordinal)) return false;
      return true;
    }

    /// <summary>
    /// The line in the USER's source for a line the runtime reports in the executable one, or null when the line is one of the
    /// plugin's (the shebang Dwight added, or the preamble). Without the source's own header Dwight adds the shebang and the preamble
    /// on top (so the user's line 1 is the runtime's line 7); with one, the preamble follows it.
    /// </summary>
    public static int? UserLine(string approved, int runtimeLine)
    {
      var own = OwnShebangLine(NormaliseSource(approved).Split('\n'));
      if (own == 0)
      {
        var added = 1 + PreambleLineCount;
        return runtimeLine <= added ? (int?)null : runtimeLine - added;
      }
      if (runtimeLine <= own) return runtimeLine;
      if (runtimeLine <= own + PreambleLineCount) return null;
      return runtimeLine - PreambleLineCount;
    }

    private static readonly System.Text.RegularExpressions.Regex PositionRx =
      new System.Text.RegularExpressions.Regex(@"\(at \[(\d+):(\d+)\]\)", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// The runtime's "(at [L:C])" rewritten to the user's line, so "(at [5:1])" means line 5 of the source on the card; a position inside
    /// the plugin's lines reads "(in Dwight's preamble)". Text without a position is returned unchanged.
    /// </summary>
    public static string UserPositions(string runtimeText, string approved)
    {
      if (string.IsNullOrEmpty(runtimeText)) return runtimeText;
      return PositionRx.Replace(runtimeText, m =>
      {
        var line = UserLine(approved, int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
        return line == null ? InPreamble : "(at [" + line.Value + ":" + m.Groups[2].Value + "])";
      });
    }

    // ---- the report, not a verdict ----------------------------------------------------------------------------------------

    /// <summary>Signed count change: "+3 components", "-1 component", "0 components".</summary>
    internal static string Delta(int before, int after, string noun)
    {
      var d = after - before;
      var abs = Math.Abs(d);
      var word = abs == 1 ? noun : noun + "s";
      return (d > 0 ? "+" : d < 0 ? "-" : "") + abs + " " + word;
    }

    /// <summary>Printed output cut to the bound, saying so. Never silently shortened.</summary>
    public static string BoundStdout(string stdout)
    {
      var text = (stdout ?? "").Trim();
      if (text.Length == 0) return "none";
      if (text.Length <= MaxStdoutChars) return text;
      return text.Substring(0, MaxStdoutChars) + " [cut at " + MaxStdoutChars + " of " + text.Length + " characters]";
    }

    /// <summary>
    /// The panel line and the tool result for a script that RAN: what changed, measured by the plugin from two snapshots of the
    /// document. It is a report. It never says "verified" or "checked", because Dwight proves nothing about what a script did; the
    /// fingerprint says whether the definition's behaviour as Dwight reads it changed, which is a measurement and not a promise.
    /// </summary>
    public static string Report(string purpose, DocSnapshot before, DocSnapshot after, string stdout, string stderr = null, string undoLine = null) =>
      "Ran " + TrimStop(purpose) + ". " + ChangeClause(before, after) + " Printed output: " + BoundStdout(stdout) + "." + ErrorClause(stderr) +
      " " + (undoLine ?? UndoClause);

    /// <summary>The report for a run, from its outcome: the raised form when the script failed, with what it printed, what it wrote to its
    /// error stream, and the Ctrl-Z clause measured from the record (U101, U102). Every report of a finished run goes through here.</summary>
    public static string ReportFor(string purpose, RunOutcome o) =>
      o.Exception != null
        ? RaisedReport(purpose, o.Exception, o.Before, o.After, o.Stdout, o.Stderr, UndoLine(o))
        : Report(purpose, o.Before, o.After, o.Stdout, o.Stderr, UndoLine(o));

    /// <summary>" Error output: ..." when the script wrote to its error stream, bounded as the printed output is; empty otherwise.</summary>
    internal static string ErrorClause(string stderr) =>
      string.IsNullOrWhiteSpace(stderr) ? "" : " Error output: " + BoundStdout(stderr) + ".";

    /// <summary>"Changed: +3 components, -1 group, +4 wires, 0 new errors; the definition's behaviour fingerprint did not change."</summary>
    internal static string ChangeClause(DocSnapshot before, DocSnapshot after)
    {
      before = before ?? new DocSnapshot();
      after = after ?? new DocSnapshot();
      var newErrors = after.Errors - before.Errors;
      var errors = newErrors >= 0 ? newErrors + " new errors" : (-newErrors) + " fewer errors";
      var changed = string.Equals(before.Fingerprint, after.Fingerprint, StringComparison.Ordinal)
        ? "the definition's behaviour fingerprint did not change"
        : "the definition's behaviour fingerprint changed";
      return "Changed: " + Delta(before.Components, after.Components, "component") + ", " +
             Delta(before.Wires, after.Wires, "wire") + ", " + Delta(before.Groups, after.Groups, "group") + ", " + errors + "; " +
             changed + "." + RhinoClause(before, after);
    }

    /// <summary>The report for a script that raised: its own exception, verbatim with its line, then what the document looks like
    /// anyway - a script can change things before it fails, and one Ctrl-Z may not undo them.</summary>
    public static string RaisedReport(string purpose, string exceptionText, DocSnapshot before, DocSnapshot after, string stdout,
                                      string stderr = null, string undoLine = null)
    {
      return "The script for " + TrimStop(purpose) + " raised: " + (string.IsNullOrWhiteSpace(exceptionText) ? "(no message)" : exceptionText.Trim()) +
             " Before it failed, " + char.ToLowerInvariant(ChangeClause(before, after)[0]) + ChangeClause(before, after).Substring(1) +
             " Printed output: " + BoundStdout(stdout) + "." + ErrorClause(stderr) + " " + EnvironmentSentence + " " + (undoLine ?? UndoClause);
    }

    /// <summary>The panel sentence when the clock ran out before the script came back, naming the clock and what is true.</summary>
    public static string OverranReport(string purpose, int timeoutSeconds) =>
      "The script for " + TrimStop(purpose) + " has not finished after " + timeoutSeconds + " seconds. " + StillRunning;

    /// <summary>The line for a script that finished AFTER its clock had already been reported: how long it really took, and what changed.</summary>
    public static string LateReport(string purpose, int timeoutSeconds, double seconds, string report) =>
      "The script for " + TrimStop(purpose) + " has now finished, after " + Math.Round(seconds, 1).ToString(System.Globalization.CultureInfo.InvariantCulture) +
      " seconds against a clock of " + timeoutSeconds + ". " + report;

    /// <summary>
    /// The tool result for a finished run. A script that raised is still a RESULT, not a refusal: it ran, things may have changed
    /// before it stopped, and the model needs the report to tell the user. Nothing here says "verified" or "checked".
    /// </summary>
    public static ToolResult ToResult(string purpose, RunOutcome o)
    {
      if (o == null) return ToolResult.Fail(ToolErrors.PreconditionRefused, "The script did not report.");
      if (o.Unavailable != null)
        return ToolResult.Fail(ToolErrors.PreconditionRefused, o.Unavailable +
          " Nothing was run, and nothing on the canvas changed.");

      var raised = o.Exception != null;
      var report = ReportFor(purpose, o);
      return ToolResult.Success(new JsonObject
      {
        ["ran"] = true,
        ["raised"] = raised,
        ["report"] = report,
        ["seconds"] = Math.Round(o.Seconds, 1),
        ["note"] = "This is a report of what changed, not a check. Dwight does not verify what a script does. Tell the user what changed, in your own words, and do not call it verified or proven.",
      });
    }

    /// <summary>The tool result when the clock ran out and the script had not come back.</summary>
    public static ToolResult StillRunningResult(string purpose, int timeoutSeconds) =>
      ToolResult.Success(new JsonObject
      {
        ["ran"] = false,
        ["stillRunning"] = true,
        ["report"] = OverranReport(purpose, timeoutSeconds),
        ["note"] = "Dwight will tell the user when it finishes and what changed. Do not call run_script again for this work.",
      });

    private static string TrimStop(string s) => (s ?? "").Trim().TrimEnd('.');
  }
}
