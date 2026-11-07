using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using Grasshopper;
using Grasshopper.Kernel;
using Dwight.Llm;
using Dwight.Models;

namespace Dwight.Agent
{
  /// <summary>
  /// The DEBUG-only gate harness (#193e). WP-3 builds the chat that drives this surface for real; until
  /// then this is what exercises it, so the tools are verified against a live Grasshopper document
  /// rather than only against headless fixtures.
  ///
  /// SAFETY: every mutating run is intended for a SCRATCH document (the #160 law - never the corpus,
  /// never a client file). The harness says so before it writes, and every write it makes is the
  /// ordinary one-Ctrl-Z build_region with its full verdict - it has no privileged path to the canvas.
  /// </summary>
  internal static class AgentHarness
  {
    private static AgentScope _scope;

    /// <summary>The ANSWER budget for a Scaffold-band call - what the plan itself may be worth, with no
    /// allowance for thinking folded in. The thinking reserve is added at the provider seam (#216), so
    /// this number means only what it says. WP-3's real call sites set their own; the probe states one
    /// explicitly rather than inheriting a default, because the whole point of the measurement was that
    /// an inherited budget is one nobody chose.</summary>
    private const int ScaffoldAnswerBudget = 4096;

    /// <summary>
    /// The harness's session. It RE-BINDS when the active document changes, because a dev driver has to
    /// be able to follow the user to a scratch file - the write half of the gate is only ever run on one
    /// (the #160 law), which is necessarily not the document that happened to be open first.
    ///
    /// This does not soften the product guard. <c>AgentScope.Document</c> still refuses a document its
    /// scope was not bound to, and that refusal is correct: ids from one document mean nothing in
    /// another. What changes here is only that a NEW session starts, with a new id space, exactly as it
    /// would for a WP-3 chat opened against a different file.
    /// </summary>
    private static AgentScope Scope()
    {
      var doc = Instances.ActiveCanvas?.Document;
      if (_scope == null || !_scope.IsBoundTo(doc)) _scope = new AgentScope(doc);
      return _scope;
    }

    private static void Line(StringBuilder sb, string label, string json)
    {
      sb.AppendLine("--- " + label + " ---");
      sb.AppendLine(json.Length > 1400 ? json.Substring(0, 1400) + " ...(truncated for display)" : json);
      sb.AppendLine();
    }

    /// <summary>Read half of the gate: every read tool on the live definition, plus the refusals. No
    /// mutation, no solve, no tokens - so it is safe on any document.</summary>
    public static void RunReads()
    {
      var scope = Scope();
      var sb = new StringBuilder();
      sb.AppendLine("READ SURFACE - grounded data, and refusals that are coded and actionable.");
      sb.AppendLine();

      Line(sb, "list_tools", string.Join(", ", AgentTools.List().Select(t =>
        t.Name + (t.Mutates ? " [mutates]" : t.Solves ? " [solves]" : ""))));

      var overview = AgentTools.Invoke(AgentTools.GetOverview, "{}", scope);
      Line(sb, "get_overview", overview);

      // Take a real id out of the overview and walk the rest of the surface with it.
      int? firstRegion = null;
      try { firstRegion = JsonNode.Parse(overview)?["result"]?["regions"]?.AsArray()?.FirstOrDefault()?["id"]?.GetValue<int>(); }
      catch { }

      if (firstRegion.HasValue)
      {
        var region = AgentTools.Invoke(AgentTools.GetRegion, "{\"id\":" + firstRegion + "}", scope);
        Line(sb, "get_region(" + firstRegion + ")", region);

        int? node = null;
        try { node = JsonNode.Parse(region)?["result"]?["nodes"]?.AsArray()?.FirstOrDefault()?["id"]?.GetValue<int>(); }
        catch { }
        if (node.HasValue)
        {
          Line(sb, "get_component(" + node + ")", AgentTools.Invoke(AgentTools.GetComponent, "{\"id\":" + node + "}", scope));
          Line(sb, "get_connections(" + node + ")", AgentTools.Invoke(AgentTools.GetConnections, "{\"id\":" + node + "}", scope));
        }
      }

      Line(sb, "search(type=Panel)", AgentTools.Invoke(AgentTools.Search, "{\"type\":\"Panel\"}", scope));
      Line(sb, "kb_lookup(Loft)", AgentTools.Invoke(AgentTools.KbLookup, "{\"type\":\"Loft\"}", scope));

      sb.AppendLine("REFUSALS - each must be coded, and say something the model can act on:");
      Line(sb, "get_component(999999) - invented id", AgentTools.Invoke(AgentTools.GetComponent, "{\"id\":999999}", scope));
      Line(sb, "kb_lookup(Lofr) - near-miss type", AgentTools.Invoke(AgentTools.KbLookup, "{\"type\":\"Lofr\"}", scope));
      Line(sb, "no_such_tool", AgentTools.Invoke("no_such_tool", "{}", scope));
      Line(sb, "solve_and_check via the SYNC path - must refuse, not deadlock",
        AgentTools.Invoke(AgentTools.SolveAndCheck, "{}", scope));

      Report("Agent read surface", sb.ToString());
    }

    /// <summary>solve_and_check on the live definition. Solves, so it is observable work - but changes
    /// nothing.</summary>
    public static void RunSolveCheck()
    {
      AgentTools.InvokeAsync(AgentTools.SolveAndCheck, "{}", Scope(),
        json => Report("solve_and_check", json));
    }

    /// <summary>
    /// The write half of the gate. SCRATCH DOCUMENTS ONLY. Places two components and wires them, as one
    /// undoable change, and prints the verdict. Run it twice - once normally, once after arming the
    /// sabotage - to see both outcomes: applied-and-verified, and caught-and-reverted.
    /// </summary>
    public static void RunBuild()
    {
      // Three components and two wires, chosen so the PORT rules are all exercised in one build:
      //   slider -> add."A"   the slider is a param (no output name to give); Addition has two inputs,
      //                       so "A" is a genuine choice and must match a real port;
      //   add    -> panel     Addition has one output, so the plugin fills it in; the Panel is a param,
      //                       so there is no input name either.
      var spec = @"{
        ""components"":[
          {""ref"":""slider"",""type"":""Number Slider"",""nick"":""dwightTest""},
          {""ref"":""add"",""type"":""Addition""},
          {""ref"":""panel"",""type"":""Panel""}
        ],
        ""wires"":[
          {""from"":{""ref"":""slider""},""to"":{""ref"":""add"",""param"":""A""}},
          {""from"":{""ref"":""add""},""to"":{""ref"":""panel""}}
        ]
      }";

      // A port that does not exist, refused FIRST - before any build, so the refusal is visible beside
      // the success it guards. It must name the real ports rather than merely say no.
      var badPort = spec.Replace(@"""param"":""A""", @"""param"":""Number""");

      AgentTools.InvokeAsync(AgentTools.BuildRegion, badPort, Scope(), refusal =>
      {
        AgentTools.InvokeAsync(AgentTools.BuildRegion, spec, Scope(), json =>
        {
          var note = "";
#if DEBUG
          // Whatever the drill did - including NOT firing - is stated. A drill that quietly no-ops and
          // is reported as having fired turns a green into a claim about a path nobody exercised.
          if (GhBuildRegion.LastSabotageNote != null)
          {
            note = "\n\n[drill] " + GhBuildRegion.LastSabotageNote +
                   (GhBuildRegion.LastBuildSabotaged ? "\n        A REVERT above is the correct outcome." : "");
            GhBuildRegion.LastSabotageNote = null;
            GhBuildRegion.LastBuildSabotaged = false;
          }
#endif
          Report("build_region",
            "--- an invented port name, refused before anything was built ---\n" + refusal + "\n\n" +
            "--- the real build ---\n" + json + note +
            "\n\nCheck on the canvas: the components appear (or do not, on a revert), and ONE Ctrl-Z undoes the whole thing.");
        });
      });
    }

    /// <summary>
    /// THE #223 DEADLOCK DRILL - the loop's asynchronous dispatch exercised against a MUTATING tool,
    /// not only against reads. SCRATCH DOCUMENTS ONLY (#160).
    ///
    /// Why a mutating tool specifically: a read answers synchronously inside the UI-thread invoke, so a
    /// read-only loop would pass even if the threading were wrong. <c>build_region</c> and
    /// <c>solve_and_check</c> answer from <c>SolutionEnd</c> - the worker blocks on a completion that
    /// only arrives once the UI thread runs a solution. If the loop ever ran ON the UI thread it would
    /// wait for a solve that cannot start until it yields, and the symptom would be a HANG, not an
    /// error. A hang is exactly what a test suite cannot tell you about.
    ///
    /// It spends no tokens: there is no model in it. The loop's tool-dispatch half is driven directly
    /// with a scripted tool call, which is the half under test - the provider half is exercised by the
    /// chat itself.
    /// </summary>
    /// <summary>
    /// Front-door refusals - the codes a tool returns BEFORE it reaches the UI-thread marshalling path
    /// where the deadlock lives. A result carrying one of these is not a measurement of anything (#249).
    /// </summary>
    private static readonly string[] NeverReachedTheSolve =
    {
      ToolErrors.NoDocument, ToolErrors.StaleDocument, ToolErrors.UnknownTool,
      ToolErrors.SolverDisabled, ToolErrors.PreconditionRefused,
    };

    private static bool Exercised(string envelope, out string why)
    {
      why = null;
      try
      {
        var root = System.Text.Json.Nodes.JsonNode.Parse(envelope);
        if (root?["ok"]?.GetValue<bool>() == true) return true;   // it ran and answered
        var code = root?["error"]?["code"]?.GetValue<string>();
        if (code != null && NeverReachedTheSolve.Contains(code))
        {
          why = "refused at the front door with \"" + code + "\" - it never reached the solve path";
          return false;
        }
        return true;   // a real verdict (e.g. verification_failed) means the path WAS walked
      }
      catch { why = "the result could not be read"; return false; }
    }

    /// <summary>
    /// What the TOOL-DISPATCH drill covers (#280, built here at WP-4 Stage 0 - the wording was approved
    /// as posted and never landed).
    ///
    /// It enters at <c>AgentLoop.Execute</c> via <c>CallToolLikeTheLoop</c>, so its claim is about the
    /// tool-dispatch leg and nothing above it. That distinction is not pedantry: this drill PASSED while
    /// the product hung (#267), because the hang was in a leg it does not walk. The name now says what it
    /// measures, and the full-path drill covers the rest.
    /// </summary>
    private const string ToolDispatchCoverage =
      "MEASURED: Execute -> the UI-thread marshal -> SolutionEnd -> the worker resuming.\n" +
      "NOT COVERED: the chat's turn entry, the provider call, and the step loop - this drill starts\n" +
      "  INSIDE the loop's dispatch and never traverses AgentLoop.Run. A hang in those legs is not\n" +
      "  excluded by anything here; \"Agent: full-path turn drill\" is the instrument for them.";

    /// <summary>
    /// The tool-dispatch drill (#223) - a MUTATING tool called the way the loop's dispatch calls it, from
    /// a worker thread. Renamed from "loop dispatch drill" at #280: the menu label is what a human reads
    /// first and remembers longest, and a verdict that corrects an over-claiming name is a footnote
    /// arguing with a headline.
    /// </summary>
    public static void RunToolDispatchDrill()
    {
      // THE DRILL FORCES ITS OWN PRECONDITIONS (#249, and #209's rule for the third time this phase).
      // With no document open, both tools short-circuit at the front and never reach the UI-thread
      // marshalling path where the deadlock lives - so the drill would print a PASS beside a result
      // satisfying none of its criteria. Refuse, and say what is missing.
      var doc = Instances.ActiveCanvas?.Document;
      if (doc == null)
      {
        Report("Tool dispatch drill NOT RUN",
          "No Grasshopper document is open, so both tools would refuse at the front door in a few\n" +
          "milliseconds without ever reaching the path this drill exists to exercise - a PASS printed\n" +
          "there would measure nothing.\n\n" +
          "Open a SCRATCH document (never the corpus, never a client file) and run it again.");
        return;
      }

      var scope = Scope();
      var sb = new StringBuilder();
      sb.AppendLine("TOOL DISPATCH DRILL - a MUTATING tool called the way the agent loop calls it (#223).");
      sb.AppendLine("Scratch documents only. No model, no tokens.");
      sb.AppendLine("Document: " + (string.IsNullOrEmpty(doc.FilePath) ? "(unsaved)" : doc.FilePath) +
                    "  ·  " + doc.Objects.Count + " object(s)");
      sb.AppendLine();

      var spec = "{\"components\":[{\"ref\":\"p\",\"type\":\"Panel\",\"nick\":\"dwightLoopDrill\"}]}";

      // The worker thread is the point: this mirrors exactly what AgentLoop.Run does from Task.Run.
      System.Threading.Tasks.Task.Run(() =>
      {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var solve = AgentLoop.CallToolLikeTheLoop(AgentTools.SolveAndCheck, "{}", scope);
        var solveMs = clock.ElapsedMilliseconds;

        clock.Restart();
        var build = AgentLoop.CallToolLikeTheLoop(AgentTools.BuildRegion, spec, scope);
        var buildMs = clock.ElapsedMilliseconds;

        var refused = !AgentLoopCore.IsGranted(TurnCapability.ReadOnly, AgentTools.BuildRegion);

        Line(sb, "solve_and_check from a worker (" + solveMs + " ms)", solve);
        Line(sb, "build_region from a worker (" + buildMs + " ms)", build);
        sb.AppendLine("CAPABILITY GATE: build_region granted on a ReadOnly turn? " + (refused ? "NO (correct)" : "YES - THIS IS A DEFECT"));
        sb.AppendLine();

        // THE VERDICT ASSERTS WHAT WAS OBSERVED, not what would have counted (#249). A front-door refusal
        // returns in milliseconds without touching the marshalling path, so "it returned" is only evidence
        // when the call actually went somewhere.
        var solveOk = Exercised(solve, out var solveWhy);
        var buildOk = Exercised(build, out var buildWhy);

        if (solveOk && buildOk)
        {
          sb.AppendLine("MEASURED: both calls reached the solve path from a worker thread and RETURNED");
          sb.AppendLine("(" + solveMs + " ms / " + buildMs + " ms). No deadlock on THIS leg - which is the");
          sb.AppendLine("one failure this drill exists to catch and the test suite cannot.");
          sb.AppendLine("Now check the canvas: the Panel landed, and ONE Ctrl-Z removes it.");
        }
        else
        {
          sb.AppendLine("NOT MEASURED - the deadlock path was never exercised, so this is NOT a pass:");
          if (!solveOk) sb.AppendLine("  solve_and_check: " + solveWhy);
          if (!buildOk) sb.AppendLine("  build_region:    " + buildWhy);
          sb.AppendLine();
          sb.AppendLine("Fix the precondition it names and run it again. A drill that reports ok on a path");
          sb.AppendLine("it did not walk is worse than one that does not run.");
        }

        // #280: the bound rides EVERY verdict, pass or fail. A limitation that only prints on a failure
        // is one nobody reads on the run they are about to trust.
        sb.AppendLine();
        sb.AppendLine(ToolDispatchCoverage);

        Report("Tool dispatch drill", sb.ToString());
      });
    }

#if DEBUG
    /// <summary>Arm the revert-first drill: the next build corrupts something pre-existing, so
    /// preservation must catch it and revert. Demonstrates the guard WORKING, not merely written.</summary>
    public static void ArmBuildSabotage()
    {
      // The drill's precondition, forced at ARM time rather than discovered as a silent no-op later
      // (#209): with nothing pre-existing to corrupt, preservation is never challenged and the build
      // passes honestly - proving nothing about the guard. Refuse to arm and say what is missing.
      var doc = Instances.ActiveCanvas?.Document;
      var candidates = 0;
      try { candidates = doc?.Objects.Count(o => o is IGH_ActiveObject) ?? 0; } catch { }

      if (candidates == 0)
      {
        Report("build_region drill NOT armed",
          "This document holds no component for the drill to corrupt, so preservation would not be\n" +
          "challenged and the build would pass on its own merits - which proves nothing about the guard.\n\n" +
          "Put anything on the scratch canvas (a Panel will do), then arm it again.");
        return;
      }

      GhBuildRegion.SabotageNextBuild = true;
      Report("build_region drill armed",
        "The NEXT build_region will also corrupt one of the " + candidates + " component(s) already here.\n" +
        "Expected: preservation fails, the build is REVERTED, and the canvas is left as it was.");
    }
#endif

    /// <summary>
    /// The Scaffold direction of the thinking capture (#193e), run as an A/B WITH A POSITIVE CONTROL
    /// (#212/#213).
    ///
    /// Two measurements of zero thinking tokens do not establish that the model did not think - they
    /// establish that this instrument has only ever produced one value, which is not the same claim. A
    /// null needs a positive control before it means anything, and the control here is a SECOND CALL
    /// rather than a harder prompt:
    ///
    ///   A - forced structured output, which is the condition every shipped path uses today
    ///       (StructuredOutput always sets tool_choice:{type:"tool"});
    ///   B - the same model, same thinking wire, same effort, same question, as PROSE with no tool
    ///       forced - controlling for tool forcing;
    ///   C - the same prose call at MAX effort - controlling for the one remaining knob DWIGHT owns,
    ///       since effort comes from the user's profile and Balanced sends "medium".
    ///
    /// Each answer is a property of the condition that produced it and generalises to nothing else
    /// (#214) - WP-3's multi-tool loop is a different wire condition and gets its own probe.
    ///
    /// The RAW usage decides the reader's standing, and must be consulted rather than merely captured:
    /// a parsed zero and an unsent field are the same number, but "output_tokens_details.thinking_tokens
    /// present and zero" is the API affirmatively reporting no thinking, which vindicates the parse.
    ///
    /// This is the ONLY harness entry that spends money: three small calls, said plainly.
    /// </summary>
    public static void RunThinkingProbe()
    {
      try
      {
        var settings = Config.DwightSettings.Current;
        var resolved = settings.ResolveModel(DwightTask.Scaffold, TaskScope.Any, structured: true);

        // #109 AT THE PROBE ITSELF. The ADR-082 wire being measured - adaptive + effort - is Anthropic's
        // dialect, and the request trace instruments only that provider. On any other provider this
        // would spend a real call, report "ok", and then read back "no request was made": a vacuous pass
        // dressed as a green. Refuse instead, and say what to change.
        if (!string.Equals(settings.EffectiveProvider, DwightConfig.ProviderAnthropic, StringComparison.OrdinalIgnoreCase))
        {
          Report("Build thinking probe",
            "NOT RUN - no call was made and nothing was spent.\n\n" +
            "provider:  " + settings.EffectiveProvider + "  (would resolve to " + resolved.Model.Id + ")\n\n" +
            "The thinking wire this probe measures is Anthropic's (adaptive + effort), and the request\n" +
            "trace instruments only that provider - so a call here would prove nothing about it.\n" +
            "Switch the provider to Anthropic in Settings and run this again.");
          return;
        }

        // What will actually be SENT, not what the task asked for. Resolved.Thinking is the task's WANT;
        // the catalogue decides the wire, and on a model with no adaptive mode a want of "true" still
        // sends nothing. Printing the want as though it were the wire is the overstatement #206 closed
        // in the build verdict, and it has no more business in a diagnostic.
        var wire = Dwight.Llm.ThinkingWire.Resolve(resolved.Thinking, resolved.Model.ThinkingDefault);

        var provider = LlmProviders.Create(settings.EffectiveProvider, resolved.Model.Id);

        // The SAME question for both arms. The prompt is held constant precisely because it is not the
        // variable under test - the tool condition is.
        const string Question =
          "Plan the minimal component chain that places a staggered grid of circles on a sloped roof " +
          "surface, where alternate rows are offset by half the spacing. Give the components in build " +
          "order, and say what each one contributes.";
        const string System = "You are Dwight, planning a Grasshopper definition.";

        // ---- A: forced structured output - the condition every shipped path uses today --------------
        var a = StructuredOutput.Get(provider,
          system: System, userPrompt: Question,
          toolName: "plan", toolDescription: "The component chain, in build order.",
          schemaJson: "{\"type\":\"object\",\"properties\":{\"steps\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}}},\"required\":[\"steps\"]}",
          validate: json => (json.Contains("steps"), "missing 'steps'"),
          maxTokens: ScaffoldAnswerBudget,   // the ANSWER budget; the thinking reserve is added at the seam (#216)
          thinking: resolved.Thinking,
          effort: resolved.Effort);
        var readA = Dwight.Llm.LlmTrace.LastThinking();   // captured BEFORE B overwrites the trace

        // ---- B: the POSITIVE CONTROL - same everything, no tool forced ------------------------------
        var b = Prose(provider, System, Question, resolved.Thinking, resolved.Effort, out var readB);

        // ---- C: the last knob DWIGHT controls - the same prose call at MAX effort -------------------
        //
        // Effort is not the model's choice, it is ours: it comes from the user's profile, and Balanced
        // sends "medium". If adaptive only thinks at higher effort then ADR-082's "thinking on for
        // Scaffold" is decorative on the default profile - sent, billed as ordinary output tokens, and
        // never benefited from. That is a product consequence, not a curiosity, which is what earns a
        // third small call rather than another guess.
        var c = Prose(provider, System, Question, resolved.Thinking, "max", out var readC);

        Report("Build thinking probe (A/B/C)",
          "provider:    " + settings.EffectiveProvider + "\n" +
          "model:       " + resolved.Model.Id + "  (thinkingDefault " + resolved.Model.ThinkingDefault + ")\n" +
          "task wants:  " + resolved.Thinking + (resolved.Effort != null ? "  effort " + resolved.Effort : "") + "\n" +
          "WIRE STATE:  " + wire + "   <- what is actually sent, every arm\n" +
          "Same model, same question, same thinking state in all three. Only the marked variable moves.\n\n" +
          Arm("A  forced structured (tool_choice: tool) - the shipped condition", readA,
              "ok " + a.Ok + (a.Ok ? "" : "  (" + a.Error + ")")) +
          Arm("B  prose, NO tool forced  [control: tool forcing]", readB, b) +
          Arm("C  prose, no tool, effort MAX  [control: effort]", readC, c) +
          "\nVERDICT: " + Verdict(readA, readB, readC) +
          "\n\nDev > \"Last LLM request\" shows arm C; A and B are above.");
      }
      catch (Exception ex)
      {
        Report("Build thinking probe", "Could not run (" + ex.Message + ").");
      }
    }

    /// <summary>One free-form call: no tool defined, so nothing is forced and the model answers as it
    /// likes. Returns a short outcome line and hands back the trace reading, which must be snapshotted
    /// per arm because the trace is one exchange deep.</summary>
    private static string Prose(ILlmProvider provider, string system, string question, bool thinking, string effort,
      out Dwight.Llm.LlmTrace.ThinkingReading reading)
    {
      reading = null;
      try
      {
        var response = provider.Send(new LlmRequest
        {
          System = system,
          Messages = { LlmMessage.User(question) },
          MaxTokens = ScaffoldAnswerBudget,
          Thinking = thinking,
          Effort = effort,
        });
        reading = Dwight.Llm.LlmTrace.LastThinking();
        return string.Join("", response.Blocks.Where(x => x.Kind == "text").Select(x => x.Text)).Length + " chars of prose";
      }
      catch (Exception ex) { return "FAILED (" + ex.Message + ")"; }
    }

    private static string Arm(string label, Dwight.Llm.LlmTrace.ThinkingReading r, string outcome)
    {
      if (r == null) return "--- " + label + " ---\n    (no reading - Release build, or the call never left)\n\n";
      return "--- " + label + " ---\n" +
             "    sent:    " + r.Sent + (r.Effort != null ? "  effort " + r.Effort : "") + "\n" +
             "    TOKENS:  " + r.Tokens + "   [witness 1: the usage object]\n" +
             "    BLOCKS:  " + r.Blocks + "   [witness 2: the content stream]\n" +
             "    stop:    " + (r.StopReason ?? "(none)") + (r.Truncated ? "   <- CUT OFF, the answer is incomplete" : "") + "\n" +
             "    raw:     " + (r.RawUsage ?? "(none captured)") + "\n" +
             "    result:  " + outcome + "\n\n";
    }

    /// <summary>
    /// The #213 decision table, applied in code rather than left to be read off two number pairs. A
    /// result that has to be interpreted on the way down a paste-bus is a result that can be
    /// misinterpreted, and this one decides whether an earlier proof still stands.
    /// </summary>
    private static string Verdict(Dwight.Llm.LlmTrace.ThinkingReading a, Dwight.Llm.LlmTrace.ThinkingReading b,
      Dwight.Llm.LlmTrace.ThinkingReading c)
    {
      if (a == null || b == null || c == null)
        return "INCONCLUSIVE - an arm produced no reading, so nothing is settled.";

      // Whichever arm thinks first tells us which variable was holding it back - and each answer is a
      // property of THAT condition only. Nothing here generalises to a condition nobody ran (#214).
      if (c.Thought && !b.Thought && c.Truncated)
        return "ONLY C THOUGHT - AND IT SPENT THE WHOLE BUDGET DOING IT.\n" +
               "  Effort is the knob: adaptive thinks at max and not at the profile's medium. That also\n" +
               "  discharges the standing residual - a NON-zero has now been transported, by both\n" +
               "  witnesses at once - so the reader is proven in the positive direction and Stage C's\n" +
               "  comprehension zero stands on a fully witnessed instrument.\n" +
               "  But C stopped at max_tokens with the entire allowance spent on thinking and NOTHING\n" +
               "  left for the answer. Thinking is billed INSIDE max_tokens, and nothing couples the two:\n" +
               "  a caller that raises effort without raising its budget gets a truncated call it still\n" +
               "  pays for. On Balanced, ADR-082's thinking-on is merely decorative; on Ultra it is\n" +
               "  actively destructive at a modest budget. THIS is the finding, not the knob.";

      if (c.Thought && !b.Thought)
        return "ONLY C THOUGHT - EFFORT IS THE KNOB.\n" +
               "  Adaptive thinks at max effort and not at the profile's medium, and a non-zero has now\n" +
               "  been transported by both witnesses - so the reader is proven in the positive direction\n" +
               "  and Stage C's comprehension zero stands on a fully witnessed instrument.\n" +
               "  On the DEFAULT profile, ADR-082's \"thinking on for Build\" is decorative: sent,\n" +
               "  billed as ordinary output, never used. A product decision to take, not a bug to fix.";

      if (b.Thought && !a.Thought)
        return "B THOUGHT, A DID NOT - TOOL FORCING IS THE SUPPRESSOR.\n" +
               "  The reader WORKS - so Stage C's comprehension zero was a real observation and stands.\n" +
               "  Forced single-tool use suppresses adaptive thinking on this model. A FINDING about\n" +
               "  this condition, not a bug, and it does not generalise past it: WP-3's multi-tool loop\n" +
               "  is a different wire condition and must be re-probed under its own.";

      if (a.Thought && b.Thought && c.Thought)
        return "EVERY ARM THOUGHT.\n" +
               "  The instrument is fine and the earlier zeroes were the stimulus, not the wire.";

      if (a.Thought)
        return "A THOUGHT AND THE FREE ARMS DID NOT - the opposite of the hypothesis.\n" +
               "  Record it exactly as measured and generalise from nothing.";

      // Nobody thought - and the RAW payload decides which of the two very different reasons applies.
      // The earlier version of this table asked only the two parsed witnesses and jumped to "the reader
      // is broken", which is the one conclusion the third witness was captured to rule in or out.
      if (a.ReportedThinkingField || b.ReportedThinkingField || c.ReportedThinkingField)
        return "NO ARM THOUGHT - and the API SAID SO.\n" +
               "  The raw usage carries output_tokens_details.thinking_tokens explicitly, and the parsed\n" +
               "  value matches it: the READER IS VINDICATED, so Stage C's comprehension zero was a real\n" +
               "  observation and STANDS.\n" +
               "  Residual, stated rather than glossed: a NON-zero has still never been transported, so\n" +
               "  \"reads a non-zero correctly\" is inferred from the parse reading the exact field the API\n" +
               "  sends, not observed. No mechanism is left by which a non-zero would read as zero.\n" +
               "  The open question is now about the MODEL, not the instrument: this wire has not been\n" +
               "  seen to think under ANY condition Dwight controls - not forced-tool, not free prose,\n" +
               "  not max effort. ADR-082's thinking-on for Build buys nothing measurable here, and\n" +
               "  that is a finding to record rather than a knob to keep turning.";

      return "NO ARM THOUGHT, AND THE API NEVER REPORTED THE FIELD.\n" +
             "  The READER is the prime suspect, not the model. Stage C's comprehension zero is\n" +
             "  UNWITNESSED until this is settled: find where the count actually arrives, then\n" +
             "  re-verify both directions before either zero is relied on.";
    }

#if DEBUG
    /// <summary>How long the drill waits for the whole turn. BASIS: <c>build_region</c> arms a 120s
    /// watchdog and the loop's own per-tool wait is 150s, so anything under those would time out on a
    /// healthy slow solve and report a defect that is not there. 180s is past both.</summary>
    private const int FullPathTimeoutMs = 180_000;

    private const int FullPathPollMs = 500;

    /// <summary>How long to wait for the provider to be reached at all before concluding it never was.
    /// BASIS: everything between the chat entry and the first Send is local - session, guards, a worker
    /// start - and takes milliseconds. 15s is generous enough that a slow first read cannot be mistaken
    /// for a refusal above the provider.</summary>
    private const int FullPathEntryGraceMs = 15_000;

    /// <summary>The beat allowed between the last scripted reply and the answer appearing in the thread.
    /// BASIS: the loop returns on the worker and the render marshals back to the UI thread, so this is one
    /// scheduling hop plus the append. 5s is orders of magnitude more than that, and it means a genuinely
    /// missing render is reported in seconds rather than after the full timeout.</summary>
    private const int FullPathRenderGraceMs = 5_000;

    /// <summary>The scripted final answer, used as the completion witness: the drill looks for exactly
    /// this text in the thread record, so "the answer arrived" is a match on content rather than a count
    /// that could be satisfied by a notice.</summary>
    private const string FullPathAnswer = "Drill complete: the region was placed and verified.";

    /// <summary>
    /// THE FULL-PATH TURN DRILL (#268/#280, WP-4 Stage 0).
    ///
    /// The tool-dispatch drill starts mid-path and PASSED while the product hung (#267). This one starts
    /// where a typed message starts and runs the whole way to a rendered completion, with a MUTATING tool
    /// inside it - so the legs that drill cannot see are covered before WP-4's build loop is trusted to
    /// run on them.
    ///
    /// TOKEN-FREE: the model is a scripted stub (<see cref="ScriptedProvider"/>). Everything else - the
    /// session, the guards, the loop, the dispatcher, the tools, the marshalling, the render - is real,
    /// because a harness testing a mock measures the mock (#97).
    ///
    /// IT FORCES ITS OWN PRECONDITIONS AND REFUSES OUTSIDE THEM (#249). It needs a document, a model and
    /// provider RunTurn's front door accepts (<see cref="AgentFrontDoor"/> - asked, not restated) and a present
    /// key - not because it calls anything, but because <c>RunTurn</c> refuses before the provider is
    /// constructed and the drill walks those guards FOR REAL rather than around them. Anything else and it says
    /// what is missing instead of printing a pass beside a short-circuit.
    /// </summary>
    public static void RunFullPathTurnDrill()
    {
      var doc = Instances.ActiveCanvas?.Document;
      var settings = Config.DwightSettings.Current;

      var missing = new List<string>();
      if (doc == null)
        missing.Add("no Grasshopper document is open - RunTurn refuses before it starts, so nothing below " +
                    "the front door would be exercised. Open a SCRATCH document.");
      // THE PANEL'S OWN FRONT DOOR, asked rather than mirrored (lane B 3.1). The list here once restated #250b's
      // provider rule by hand, and when RunTurn's check grew a MODEL half (a pinned non-agentic model), the list
      // did not: RunTurn refused before the stub and the drill could not say why. Asking the same function makes
      // the two agree by construction. The key is still checked on its own line below, so the list names
      // everything missing at once rather than only the first thing the door would stop on.
      var resolvedModel = settings.ResolveModel(DwightTask.Scaffold, TaskScope.Any, structured: false)?.Model;
      var door = AgentFrontDoor.Check(settings.OverrideModelId, resolvedModel, settings.EffectiveProvider,
                                      LlmProviders.AgentRefusal, LlmProviders.HasKey);
      if (door.Refusal == FrontDoorRefusal.NotEligible)
        missing.Add("RunTurn's front door refuses this model or provider BEFORE the provider is built, so the " +
                    "stub would never be reached. It says: \"" + door.Text + "\" Nothing is spent either way.");
      if (!LlmProviders.HasKey(settings.EffectiveProvider))
        missing.Add("no API key is stored for " + settings.EffectiveProvider + ". RunTurn refuses without " +
                    "one, before the provider is built. The drill spends nothing, but the guard is real " +
                    "and it is walked, so a key must be present.");
      if (Dwight.UI.DwightScaffold.DebugProviderOverride != null)
        missing.Add("a previous run left the stub armed, which means its turn never finished. Refusing " +
                    "rather than re-arming, so two drills cannot race. RESTART RHINO to clear it - " +
                    "\"Restart Dwight\" resets the session but not this field, which lives for the life " +
                    "of the loaded assembly.");

      if (missing.Count > 0)
      {
        var why = new StringBuilder();
        why.AppendLine("NOT RUN - nothing was exercised and nothing was spent:");
        foreach (var m in missing) why.AppendLine("  - " + m);
        why.AppendLine();
        why.AppendLine(FullPathDrill.Coverage);
        Report("Full-path turn drill NOT RUN", why.ToString());
        return;
      }

      // A MUTATING tool inside the script (#354): build_region is the longest path - it marshals to the
      // UI thread, solves, and answers from SolutionEnd - and it is the one that hangs rather than errors.
      var script = new List<ScriptedStep>
      {
        new ScriptedStep
        {
          Text = "Reading the definition before I answer.",
          ToolName = AgentTools.GetOverview, ToolArgsJson = "{}",
        },
        new ScriptedStep
        {
          ToolName = AgentTools.BuildRegion,
          ToolArgsJson = "{\"components\":[{\"ref\":\"p\",\"type\":\"Panel\",\"nick\":\"dwightFullPathDrill\"}]}",
        },
        new ScriptedStep { Text = FullPathAnswer },
      };

      var stub = new ScriptedProvider(script);
      var observations = new FullPathObservations { ScriptSteps = script.Count };

      Report("Full-path turn drill STARTED",
        "SCRATCH DOCUMENTS ONLY (#160). No model, no tokens - the provider is a scripted stub.\n" +
        "Document: " + (string.IsNullOrEmpty(doc.FilePath) ? "(unsaved)" : doc.FilePath) +
        "  ·  " + doc.Objects.Count + " object(s)\n" +
        "Posting a message through the chat's own door and waiting for a rendered completion.\n" +
        "The verdict follows on this command line within " + (FullPathTimeoutMs / 1000) + "s.");

      Dwight.UI.DwightScaffold.DebugProviderOverride = stub;

      GH_Document turnDoc;
      string threadId;
      string why2;
      try
      {
        // THE CHAT'S OWN DOOR. The one difference from a typed message is the capability constant, and
        // the report says so rather than leaving it to be discovered.
        observations.Entered =
          Dwight.UI.DwightScaffold.DebugOpenAndSend(
            "Place a panel in a new region and tell me when it is verified.",
            TurnCapability.Build, out turnDoc, out threadId, out why2);
        observations.Detail = why2;
      }
      catch (Exception ex)
      {
        Dwight.UI.DwightScaffold.DebugProviderOverride = null;
        observations.Entered = false;
        observations.Detail = "the drill threw on entry: " + ex.Message;
        Finish(observations, stub);
        return;
      }

      if (!observations.Entered)
      {
        Dwight.UI.DwightScaffold.DebugProviderOverride = null;
        Finish(observations, stub);
        return;
      }

      // Poll on the UI thread. The completion marshals back to the UI thread, so BLOCKING here would
      // deadlock against the very thing being measured - the drill would manufacture the hang it exists
      // to detect, which is the most embarrassing possible Family-D failure.
      var waited = 0;
      var settledAt = -1;
      Eto.Forms.UITimer timer = null;
      timer = new Eto.Forms.UITimer { Interval = FullPathPollMs / 1000.0 };
      timer.Elapsed += (s, e) =>
      {
        waited += FullPathPollMs;
        var done = Recorded(turnDoc, threadId, FullPathAnswer);

        // Conclude as soon as the answer is known, rather than always burning the full timeout. Each
        // early exit below is a DEFINITE state, never an impatient guess - a drill that concluded early
        // on a maybe would be inventing the result it was built to observe.
        if (!done)
        {
          // (a) the provider was never reached: the turn refused above it, and no amount of further
          //     waiting changes that. The thread tail says which guard spoke.
          var neverReached = stub.Calls == 0 && waited >= FullPathEntryGraceMs;

          // (b) the script is spent, so the loop has stopped asking. Allow a beat for the completion:
          //     the loop returns on the WORKER and the render marshals back, so "the stub is finished"
          //     genuinely precedes "the thread has the answer" by a scheduling hop.
          if (stub.Calls >= script.Count && settledAt < 0) settledAt = waited;
          var rendered = settledAt >= 0 && waited - settledAt >= FullPathRenderGraceMs;

          if (!neverReached && !rendered && waited < FullPathTimeoutMs) return;
        }

        try { timer.Stop(); } catch { }
        Dwight.UI.DwightScaffold.DebugProviderOverride = null;

        observations.AnswerRecorded = done;
        observations.TimedOut = !done && waited >= FullPathTimeoutMs;
        observations.Outcome = done ? TurnOutcome.Complete.ToString() : OutcomeFromThread(turnDoc, threadId);
        if (!done) observations.Detail = ThreadTail(turnDoc, threadId);
        Finish(observations, stub);
      };
      timer.Start();
    }

    /// <summary>Score the run and report it. Kept separate so every exit path - refused, threw, timed
    /// out, completed - lands in exactly one place with exactly one verdict.</summary>
    private static void Finish(FullPathObservations o, ScriptedProvider stub)
    {
      o.ProviderCalls = stub.Calls;
      o.ToolResults = stub.ToolResults.ToList();

      var verdict = FullPathDrill.Decide(o, out var report);

      var sb = new StringBuilder();
      sb.AppendLine("FULL-PATH TURN DRILL - the whole road from the chat's entry to a rendered completion.");
      sb.AppendLine("Entered at DwightScaffold.RunTurn, the function OnUserMessage calls. The ONLY");
      sb.AppendLine("difference from a typed message is the capability constant: the drill passes Build so");
      sb.AppendLine("its script can include a MUTATING tool. Provider: a scripted stub - no tokens.");
      sb.AppendLine();
      sb.Append(report);

      if (stub.Calls > 0)
      {
        sb.AppendLine();
        sb.AppendLine("--- what the stub was asked, per call ---");
        var i = 1;
        foreach (var call in stub.Observed)
        {
          sb.AppendLine("  call " + i + ": " + call.MessagesReceived + " message(s), " +
                        call.ToolsAdvertised.Count + " tool(s) advertised, answer budget " + call.AnswerBudget);
          i++;
        }
        sb.AppendLine("  (the tool list is the capability's grant set - a Build turn, so it includes " +
                      "build_region.)");
      }

      Report(verdict == DrillVerdict.Measured ? "Full-path turn drill: MEASURED"
                                              : "Full-path turn drill: NOT MEASURED", sb.ToString());
    }

    /// <summary>Did the scripted answer reach the thread RECORD? Content, not a count - a message count
    /// would be satisfied by a refusal notice, which is the opposite of what this witnesses.</summary>
    private static bool Recorded(GH_Document doc, string threadId, string answer)
    {
      try
      {
        foreach (var m in Dwight.UI.DwightScaffold.DebugThreadMessages(doc, threadId))
          if (m?.Src != null && m.Src.Contains(answer)) return true;
      }
      catch { }
      return false;
    }

    /// <summary>The thread's tail, so a failure shows what actually happened instead of only that
    /// something did not.</summary>
    private static string ThreadTail(GH_Document doc, string threadId)
    {
      try
      {
        var msgs = Dwight.UI.DwightScaffold.DebugThreadMessages(doc, threadId) ?? new List<Dwight.Session.StoredMessage>();
        var sb = new StringBuilder();
        foreach (var m in msgs.Skip(Math.Max(0, msgs.Count - 4)))
          sb.AppendLine("  [" + (m.Who ?? "?") + (m.Src == null ? "/chrome" : "/said") + "] " +
                        Squash(m.Src ?? m.Html));
        return sb.Length == 0 ? "  (the thread is empty)" : sb.ToString();
      }
      catch (Exception ex) { return "  (the thread could not be read: " + ex.Message + ")"; }
    }

    /// <summary>A best-effort read of how the turn ended, for the case where no answer arrived. The loop's
    /// own outcome is not reachable from here, so this is explicitly a GUESS FROM THE SURFACE and is
    /// labelled as such rather than presented as the loop's verdict.</summary>
    private static string OutcomeFromThread(GH_Document doc, string threadId)
    {
      try
      {
        var msgs = Dwight.UI.DwightScaffold.DebugThreadMessages(doc, threadId);
        if (msgs == null || msgs.Count == 0) return "(no thread)";
        return "(not Complete - inferred from the thread, which holds " + msgs.Count + " message(s))";
      }
      catch { return "(never returned)"; }
    }

    private static string Squash(string s)
    {
      if (string.IsNullOrEmpty(s)) return "";
      var one = s.Replace("\r", " ").Replace("\n", " ");
      return one.Length <= 160 ? one : one.Substring(0, 159) + "…";
    }
#endif

    /// <summary>#255: the live consent state, so a field report can name the cause. Zero cost, no
    /// document needed - it reads Dwight's own bookkeeping, not the canvas.</summary>
    public static void ShowConsents() =>
      Report("Pending consents", Dwight.UI.DwightScaffold.ConsentDiagnostics());

    private static void Report(string title, string text)
    {
      try { Dwight.UI.DwightToast.Show(title + " - see the Rhino command line"); } catch { }
      try { Rhino.RhinoApp.WriteLine("=== Dwight harness: " + title + " ===\n" + text); } catch { }
    }
  }
}
