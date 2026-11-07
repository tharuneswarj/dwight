using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using Grasshopper;
using Grasshopper.Kernel;
using Dwight.Config;
using Dwight.Data;
using Dwight.Llm;
using Dwight.Models;
using Dwight.Simplify;

namespace Dwight.Eval
{
  /// <summary>
  /// RR20 #46(e): THE EVAL HARNESS for to-script (ADR-066, pulled forward from Phase 10).
  ///
  /// "It sometimes fails" is not a fact anyone can act on. This turns it into "compile-rate 40% -> injected the
  /// reflected API + port semantics -> 78%". WITHOUT A NUMBER WE ARE GUESSING, and every fix we ship is a hunch.
  ///
  /// It measures TWO different rates, because they fail for different reasons and a fix can move one without
  /// touching the other:
  ///   COMPILE-RATE - did the script BUILD? (hallucinated API, wrong types)
  ///   VERIFY-RATE  - did it REPRODUCE the originals, output for output? (wrong port semantics, dropped logic)
  ///
  /// It is DRIVEN THROUGH THE REAL PIPELINE - the same payload, the same prompt, the same reflected API seed, the
  /// same compile gate, the same bit-exact output comparison the product uses. A harness that tested a mock would
  /// measure the mock. Every trial runs OFF-CANVAS and is removed: the corpus definition is never mutated, nothing
  /// is committed, and no undo record is written.
  ///
  /// Driven from the LAMCP bridge (public entry points, so Rhino's Python can call in), because the corpus lives in
  /// real definitions and only Rhino can solve them.
  /// </summary>
  public static class ToScriptEval
  {
    /// <summary>One trial's record - written as it completes, so a crashed run still leaves its data.</summary>
    private sealed class Row
    {
      public int Index;
      public int Size;
      public string Language;
      public string Refusal;      // a deterministic pre-LLM refusal (not a failure - a correct "no")
      /// <summary>RR20 #59: a model/schema failure is its OWN bucket, NEVER conflated with a build failure. A bad
      /// denominator can flatter OR damn the result, and we want neither.</summary>
      public string ModelFailure;
      public bool Built;
      public bool Verified;
      /// <summary>#140: did ANY attempt in this row build? The eval scores the FINAL attempt (keeping Source&lt;-&gt;Built
      /// self-consistent for Reclassify), but the PRODUCT delivers the best-built draft (F1) - so on a row that built
      /// then regressed after an oracle retry, `Built` is false while the user still receives runnable code. Reported
      /// beside the compile rate so the eval metric is never misread as the user-facing outcome.</summary>
      public bool Delivered;
      public string Error;        // the first compiler/runtime diagnostic
      public string Mismatch;     // the outputs that differed
      public string Kinds;        // RR20 #57: WHY they differed - structure / value / order / wrapper
      public string KindsBbox;    // RR20 #77: the same, by the old bbox rule - to expose a coincidental wrapper
      public int Outputs;         // RR20 #58: single- vs multi-output stratification
      public int Attempts;        // how many model calls in all (compile + output repairs, one shared budget)
      /// <summary>Phase-8 WP-0 (#100): how many of those attempts were OUTPUT-oracle repairs (vs compile repairs).
      /// The starvation read - if the compile repairs eat the budget, this is 0 and the oracle never got to fire.</summary>
      public int VerifyRepairs;
      public long Ms;
      public string Source;       // the generated script, so a failure is inspectable without re-billing the model
      /// <summary>RR20: the model's chosen param names, in order. Without these the saved source CANNOT be
      /// recompiled (its variables would not bind), so Reclassify is impossible - the names ARE the binding.</summary>
      public string InNames;
      public string OutNames;
      /// <summary>RR20 #63: stamp the model and the corpus into every row. An UNSTAMPED rate becomes folklore -
      /// six weeks from now nobody can say which model or which selections produced "78%".</summary>
      public string Model;
      public string CorpusHash;
      /// <summary>F2 (#111/#113): the FULL EvalFlags snapshot on every row, so a row's meaning never depends on the
      /// filename tag or on which phase's before/after vocabulary the reader has in mind. The oracle bit lives HERE.</summary>
      public string Flags;
      // ---- Stage C (#101): the second-input-state overfit scoring. `Verified` above stays the STATE-1 verify (the
      // oracle's like-for-like lift vs 41.7); these carry the stricter #99/#101 label number and the overfit rate. --
      public string SecondStateBucket;   // failedstate1 | noperturbable | novalidperturbation | secondstateverified | overfit | secondstateoff
      public bool SecondStateVerified;   // built AND state-1 AND (state-2 if a valid perturbation) - the #99 bar's number
      public bool CountsForOverfit;      // a perturbable state-1 matcher with a valid perturbation (the overfit denominator)
      public bool Overfit;               // ...that FAILED state 2 (the overfit numerator)
      public bool HasPerturbable;
      public int Roots;                  // how many perturbable value sources were traced + deduped
      /// <summary>#118(4): a restore/read-back/document-proof failure. Non-null -> the run is ABORTED at this row.</summary>
      public string AbortReason;
      /// <summary>#144/#153: why the ARM stopped short, stamped into the RUN RECORD (not just the console) so a
      /// truncated arm can never be mistaken for a complete one when the JSONL is read back later.</summary>
      public string StopReason;
    }

    private static readonly List<Row> _rows = new List<Row>();
    private static string _path;
    private static bool _running;

    /// <summary>
    /// CORPUS: every connected sub-selection of the active definition that to-script could legitimately be asked to
    /// collapse - a contiguous chunk of <paramref name="minSize"/>..<paramref name="maxSize"/> components that
    /// FEEDS SOMETHING outside itself (or there is nothing to verify against). Grown from each component outward
    /// along the wires, deduplicated by member set.
    ///
    /// This is deliberately a real definition's real sub-graphs, not synthetic toys: the failure taxonomy we are
    /// chasing (hallucinated API, wrong port guess, wrapper mismatch) only shows up on real logic.
    /// </summary>
    public static string BuildCorpus(int minSize = 3, int maxSize = 10, int maxSelections = 100)
    {
      var doc = Instances.ActiveCanvas?.Document;
      if (doc == null) return "no active document";

      var graph = DwightGraph.Build(doc, doc.Objects.Select(o => o.InstanceGuid).ToList());
      var byId = graph.Selection.ToDictionary(n => n.Id, n => n.Guid);
      var adj = new Dictionary<int, HashSet<int>>();
      foreach (var n in graph.Selection) adj[n.Id] = new HashSet<int>();
      foreach (var e in graph.Edges)
      {
        if (!adj.ContainsKey(e.FromNode) || !adj.ContainsKey(e.ToNode)) continue;
        adj[e.FromNode].Add(e.ToNode);
        adj[e.ToNode].Add(e.FromNode);
      }

      var seen = new HashSet<string>();
      var corpus = new List<List<Guid>>();
      foreach (var seed in graph.Selection.OrderBy(n => n.Id))
      {
        // grow a connected blob from this seed, breadth-first, snapshotting at every legal size
        var blob = new List<int> { seed.Id };
        var frontier = new List<int>(adj[seed.Id]);
        while (blob.Count < maxSize && frontier.Count > 0)
        {
          var next = frontier.OrderBy(x => x).First();
          frontier.Remove(next);
          if (blob.Contains(next)) continue;
          blob.Add(next);
          foreach (var nb in adj[next]) if (!blob.Contains(nb) && !frontier.Contains(nb)) frontier.Add(nb);

          if (blob.Count < minSize) continue;
          var key = string.Join(",", blob.OrderBy(x => x));
          if (!seen.Add(key)) continue;
          corpus.Add(blob.Select(id => byId[id]).ToList());
          if (corpus.Count >= maxSelections * 3) break;   // over-generate; the refusals thin it out below
        }
        if (corpus.Count >= maxSelections * 3) break;
      }

      // SPREAD (found live): blobs are grown seed-by-seed, so the raw order is a run of NESTED SUPERSETS of the
      // same region - the first 50 covered only 8 of 59 regions, and a number from 8 regions is not a number.
      // Rotate each seed's blobs by its own index, then round-robin: consecutive trials now come from DIFFERENT
      // regions AND different sizes, so Take(limit) is a real sample rather than one corner of the definition.
      var bySeed = corpus.GroupBy(b => b[0]).Select(g => g.ToList()).ToList();
      for (var k = 0; k < bySeed.Count; k++)
      {
        var off = k % bySeed[k].Count;
        bySeed[k] = bySeed[k].Skip(off).Concat(bySeed[k].Take(off)).ToList();
      }
      var spread = new List<List<Guid>>();
      for (var r = 0; spread.Count < corpus.Count; r++)
      {
        var any = false;
        foreach (var blobs in bySeed)
          if (r < blobs.Count) { spread.Add(blobs[r]); any = true; }
        if (!any) break;
      }

      _corpus = spread.Take(maxSelections * 3).ToList();
      var regions = bySeed.Count;
      return $"corpus: {_corpus.Count} candidates across {regions} distinct regions (sizes {minSize}-{maxSize}) from {graph.Selection.Count} components";
    }

    private static List<List<Guid>> _corpus = new List<List<Guid>>();

    /// <summary>Run the whole corpus. Async by necessity (every trial needs a solve), so it chains: trial i's
    /// completion starts trial i+1. Progress goes to the Rhino command line; the rows stream to a JSONL.</summary>
    /// <summary>RR20 #56: <paramref name="arm"/> is the ONE variable. "after" = every RR20 input on (the product);
    /// "before" = port semantics, the reflected API seed, and the compile repair all OFF - and NOTHING else changed.
    /// Same binary, same comparator, same digest, same corpus, same model. That is a delta; a checkout is not.</summary>
    /// <summary>RR20 #65: <paramref name="modelId"/> overrides the model for THIS arm only. The three RR20 arms all
    /// run on the SHIPPING DEFAULT - switching models between them would measure the swap, not RR20. The fourth arm
    /// changes ONLY the model, which is the payload-ceiling vs model-ceiling test: if both models land at the same
    /// verify-rate, the leak is OUR PAYLOAD (#50 has further to go); if the other is markedly higher, the payload is
    /// fine and the MODEL TABLE is wrong - a config fix, not an architecture problem. (ADR-066's original purpose.)</summary>
    public static string Run(string language = "csharp", int limit = 50, string arm = "after",
      string modelId = null, string provider = null, bool oracle = false)
    {
      if (_running) return "already running";
      if (_corpus.Count == 0) return "no corpus - call BuildCorpus first";

      // The RR20 arm (before = RR20 off; after = RR20 on) sets the payload/compile inputs. The OUTPUT ORACLE is the
      // INDEPENDENT WP-0 variable (#94), set explicitly so the arm's RR20 state and the oracle never entangle. WP-0
      // holds RR20 ON in both arms (arm="after") and toggles ONLY the oracle:
      //   WP-0-before = Run(arm:"after", oracle:false)  -> the shipped ~80/~38 row (the #108 baseline)
      //   WP-0-after  = Run(arm:"after", oracle:true)   -> the treatment; the ONE variable is the oracle
      if (arm.Equals("before", StringComparison.OrdinalIgnoreCase)) EvalFlags.AllOff();
      else EvalFlags.AllOn();
      EvalFlags.OutputOracle = oracle;

      var lang = language.Equals("python", StringComparison.OrdinalIgnoreCase) ? ScriptLanguage.Python3 : ScriptLanguage.CSharp;
      _rows.Clear();
      _running = true;
      _arm = arm;
      _modelOverride = string.IsNullOrWhiteSpace(modelId) ? null : modelId;
      _providerOverride = string.IsNullOrWhiteSpace(provider) ? null : provider;

      _consecutiveModelFailures = 0; _lastModelFailure = null;   // #144: a fresh arm starts with a clear streak

      var work = _corpus.Take(limit).ToList();
      _corpusHash = HashCorpus(work);

      var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Dwight", "eval");
      Directory.CreateDirectory(dir);
      var oracleTag = EvalFlags.OutputOracle ? "-oracle" : "";   // so WP-0-before and WP-0-after never collide on disk
      var tag = (_modelOverride == null ? arm : arm + "-" + _modelOverride.Replace("/", "_").Replace(":", "_")) + oracleTag;
      _path = Path.Combine(dir, $"toscript-{lang}-{tag}-{DateTime.Now:yyyyMMdd-HHmmss}.jsonl");

      Rhino.RhinoApp.WriteLine($"Dwight eval [{arm}]: {work.Count} selections, {lang}, corpus {_corpusHash}. {EvalFlags.Describe()}. Writing {_path}");
      Next(work, 0, lang);
      return $"started [{arm}]: n={work.Count}, {lang}, model={_modelOverride ?? "(shipping default)"}, corpus={_corpusHash}, {EvalFlags.Describe()} -> {_path}";
    }

    private static string _arm = "after";
    private static string _modelOverride, _providerOverride, _corpusHash;

    /// <summary>#144: the CONSECUTIVE-PROVIDER-FAILURE CIRCUIT BREAKER. Two ARM-3 attempts were lost attempting every
    /// remaining row against a dead account (48 identical "credit balance is too low" 400s). A run that sees the same
    /// provider error this many times in a row stops and reports, instead of grinding out a full arm of nothing - it
    /// catches credit exhaustion, an invalid key, and rate-limit storms alike. The rows already written stay valid;
    /// the arm is simply reported SHORT, never silently.</summary>
    private const int ProviderFailureBreaker = 5;
    private static int _consecutiveModelFailures;
    private static string _lastModelFailure;

    /// <summary>RR20 #61: the corpus identity. The refusal set is deterministic and pre-LLM, so it MUST be identical
    /// across arms - if it is not, the arms are not comparable and something non-isolated moved. The hash is what
    /// lets that be asserted rather than assumed.</summary>
    private static string HashCorpus(List<List<Guid>> work)
    {
      var canonical = string.Join(";", work.Select(b => string.Join(",", b.Select(g => g.ToString()).OrderBy(x => x, StringComparer.Ordinal))));
      using (var sha = System.Security.Cryptography.SHA256.Create())
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(canonical))).Substring(0, 12);
    }

    /// <summary>Where the run stands right now (poll this from the bridge - the run is asynchronous).</summary>
    public static string Status()
    {
      var done = _rows.Count;
      var refused = _rows.Count(r => r.Refusal != null);
      var modelFailed = _rows.Count(r => r.Refusal == null && r.ModelFailure != null);
      // the DENOMINATOR is trials where a script was actually produced: a refusal is a correct "no", and a
      // model/schema failure is a different failure - neither is a compile failure (#59).
      var tried = done - refused - modelFailed;
      var built = _rows.Count(r => r.Built);
      var verified = _rows.Count(r => r.Verified);
      // Stage C (#101): the second-state numbers. verified = STATE-1 (the oracle's lift); ssVerified = the stricter
      // #99/#101 label number; overfit rate = overfit numerator / the perturbable-state-1-matcher denominator. The
      // no-perturbable / no-valid buckets are reported so the #101a >=25% tripwire can be checked.
      var ssVerified = _rows.Count(r => r.SecondStateVerified);
      var overfitDenom = _rows.Count(r => r.CountsForOverfit);
      var overfitNum = _rows.Count(r => r.Overfit);
      var selfContained = _rows.Count(r => r.SecondStateBucket == "selfcontained");   // A1 (#133): NOT a tripwire member
      var noPerturbable = _rows.Count(r => r.SecondStateBucket == "noperturbable");
      var noValid = _rows.Count(r => r.SecondStateBucket == "novalidperturbation");
      var notReproduced = _rows.Count(r => r.SecondStateBucket == "state1notreproduced");   // F8: any > 0 is a finding
      // A1 (#133): the tripwire denominator is the INPUT-BEARING verified rows (SelfContained excluded - it is not a
      // measurement gap). The tripwire fires when NoPerturbable exceeds ~25% of them.
      var inputBearing = noPerturbable + noValid + overfitDenom;
      var noPerturbablePct = inputBearing == 0 ? 0 : Math.Round(100.0 * noPerturbable / inputBearing, 1);
      var aborted = _rows.FirstOrDefault(r => r.AbortReason != null)?.AbortReason;
      return new JsonObject
      {
        ["running"] = _running,
        ["arm"] = _arm,
        ["flags"] = EvalFlags.Describe(),
        ["completed"] = done,
        ["refused"] = refused,
        ["modelFailed"] = modelFailed,
        ["attempted"] = tried,
        ["built"] = built,
        ["verified"] = verified,
        ["compileRate"] = tried == 0 ? 0 : Math.Round(100.0 * built / tried, 1),
        // #140: the PRODUCT-delivered rate (F1 best-built) beside the eval's final-attempt compile rate, so the
        // oracle's build regression is never misread as the user losing runnable code.
        ["deliveredRate"] = tried == 0 ? 0 : Math.Round(100.0 * _rows.Count(r => r.Delivered) / tried, 1),
        ["verifyRate"] = tried == 0 ? 0 : Math.Round(100.0 * verified / tried, 1),
        ["verifyRateSecondState"] = tried == 0 ? 0 : Math.Round(100.0 * ssVerified / tried, 1),
        ["overfitRate"] = overfitDenom == 0 ? 0 : Math.Round(100.0 * overfitNum / overfitDenom, 1),
        ["overfitDenom"] = overfitDenom,
        ["selfContained"] = selfContained,
        ["noPerturbable"] = noPerturbable,
        ["noValidPerturbation"] = noValid,
        ["inputBearing"] = inputBearing,
        ["noPerturbablePct"] = noPerturbablePct,   // #101a tripwire: > 25% = the bar cannot be called cleanly
        ["state1NotReproduced"] = notReproduced,
        ["aborted"] = aborted,
        ["path"] = _path ?? "",
      }.ToJsonString();
    }

    private static void Next(List<List<Guid>> work, int i, ScriptLanguage lang)
    {
      if (i >= work.Count)
      {
        _running = false;
        Rhino.RhinoApp.WriteLine("Dwight eval: DONE. " + Status());
        // F3 (#113): restore the PRODUCT flag state. The arm set static flags that the PRODUCT payload builders read
        // (ToScriptPayload/ToScriptPlan gate on EvalFlags.PortSemantics), so an arm="before" eval would otherwise
        // silently degrade a same-session product to-script. Restore to the product defaults.
        EvalFlags.AllOn(); EvalFlags.OutputOracle = true;
        return;
      }

      var guids = work[i];
      var doc = Instances.ActiveCanvas?.Document;
      var row = new Row { Index = i, Size = guids.Count, Language = lang.ToString(), Flags = EvalFlags.Describe() };   // F2
      var sw = Stopwatch.StartNew();

      void Finish()
      {
        sw.Stop();
        row.Ms = sw.ElapsedMilliseconds;
        // #144/#153: decide the breaker BEFORE the row is written, so its stop reason is stamped INTO the run record.
        // It trips only on IDENTICAL consecutive provider errors (a dead account / invalid key / rate-limit storm
        // repeats verbatim); genuine per-row model failures vary and reset the streak.
        var breakerTripped = _consecutiveModelFailures >= ProviderFailureBreaker;
        if (breakerTripped)
          row.StopReason = $"circuit breaker: {_consecutiveModelFailures} consecutive identical provider errors - {_lastModelFailure}";
        _rows.Add(row);
        Append(row);
        // #118(4): a second-state RESTORE failure means state 1 has drifted - every later row would measure a
        // corrupted baseline. STOP THE RUN, loudly; the file is never saved, so a reopen recovers the definition.
        if (row.AbortReason != null)
        {
          _running = false;
          Rhino.RhinoApp.WriteLine($"Dwight eval: ABORTED at row {i} - {row.AbortReason}. The run is STOPPED; results past this row would measure a drifted state 1. Reopen the definition WITHOUT saving.");
          EvalFlags.AllOn(); EvalFlags.OutputOracle = true;   // restore the product flags (F3)
          return;
        }
        // #144: the provider is dead (same error N times running) - STOP rather than attempt every remaining row
        // against it. The rows already written stay valid; the arm is reported SHORT, loudly, never silently.
        if (breakerTripped)
        {
          _running = false;
          Rhino.RhinoApp.WriteLine($"Dwight eval: CIRCUIT BREAKER at row {i} - {row.StopReason}. The arm is STOPPED and INCOMPLETE; fix the provider and re-run.");
          EvalFlags.AllOn(); EvalFlags.OutputOracle = true;   // restore the product flags (F3)
          return;
        }
        if (i % 5 == 0 || row.Refusal == null) Rhino.RhinoApp.WriteLine($"Dwight eval [{i + 1}/{work.Count}] size={row.Size} " +
          (row.Refusal != null ? "refused" : row.Verified ? "VERIFIED" : row.Built ? "built, MISMATCH" : "BUILD FAILED"));
        // hop to the next trial through the UI queue so the solver is idle before we start again
        Eto.Forms.Application.Instance.AsyncInvoke(() => Next(work, i + 1, lang));
      }

      try
      {
        var graph = DwightGraph.Build(doc, guids);
        var tsc = ToScriptResolveBuilder.Build(graph, doc, lang);
        if (tsc.Refusal != null) { row.Refusal = tsc.Refusal; Finish(); return; }

        // the SAME prompt the product builds - reflected API seed, port semantics, constants and all.
        // RR20 #70: the eval NEVER resolves the model from SETTINGS. It did once, and silently ran a whole arm on
        // whatever provider the user last clicked (deepseek-v4-pro:cloud, not the shipping default) - the model
        // STAMP is the only reason we caught it. An eval that reads user config is not a controlled experiment, it
        // is a survey. So: the baseline is PINNED, and an arm may only move it by naming a model explicitly (#65).
        var model = _modelOverride != null
          ? (ModelCatalogue.Find(_modelOverride) ?? DwightConfig.Sonnet46)
          : DwightConfig.Sonnet46;
        row.Model = model.Id;
        row.CorpusHash = _corpusHash;

        var apiSeed = EvalFlags.ApiSeed ? RhinoApiReflector.SeedFor(graph.Selection.Select(n => n.Type)) : null;   // #56
        var system = Prompts.Prompts.ToScriptMaster
                   + (lang == ScriptLanguage.CSharp ? Prompts.Prompts.ToScriptCSharpHint : Prompts.Prompts.ToScriptPythonHint)
                   + "\n" + Prompts.Prompts.ToScriptInstruction
                   + "\nThe interface you must write against:\n" + ToScriptValidator.Describe(tsc.Iface, lang)
                   + (apiSeed == null ? "" : "\nREAL RhinoCommon signatures, reflected from this Rhino:\n" + apiSeed);
        var user = "Payload:\n" + ToScriptPayload.Build(graph, tsc, lang);

        Attempt(tsc, graph, lang, model, system, user, 1, null, row, Finish);
      }
      catch (Exception ex) { row.Error = ex.Message; Finish(); }
    }

    /// <summary>One model call + one off-canvas trial, with the SAME bounded compile-repair the product runs - so
    /// the number we report is the number the user would actually get.</summary>
    private static void Attempt(ToScriptContext tsc, DwightGraph graph, ScriptLanguage lang, ModelInfo model,
      string system, string user, int attempt, string feedback, Row row, Action finish)
    {
      row.Attempts = attempt;
      ToScriptProposal proposal = null;
      var validate = new Func<string, (bool, string)>(json =>
      {
        var p = ToScriptParser.Parse(json, lang);
        var err = ToScriptValidator.Validate(p, tsc.Iface);
        if (err == null) proposal = p;
        return (err == null, err);
      });

      var message = feedback == null ? user : user + "\n\n---\n" + feedback;

      System.Threading.Tasks.Task.Run(() =>
      {
        StructuredOutput.Result res = null;
        try
        {
          var provider = LlmProviders.Create(model.Provider ?? _providerOverride ?? DwightSettings.Current.EffectiveProvider, model.Id);
          // RR20 #62: temperature 0 where the provider allows. At n~50 a few points of sampler drift would swamp
          // the effect we are trying to measure - an A/B must measure the CHANGE, not the dice.
          res = StructuredOutput.Get(provider, system, message,
            Prompts.Prompts.ToScriptToolName, Prompts.Prompts.ToScriptToolDescription,
            Prompts.Prompts.ToScriptSchema, validate, DwightConfig.MaxTokens,
            System.Threading.CancellationToken.None, temperature: 0);
        }
        catch (Exception ex) { row.Error = "model: " + ex.Message; }

        Eto.Forms.Application.Instance.AsyncInvoke(() =>
        {
          // RR20 #59: the model never produced a usable proposal - that is NOT a build failure and must not be
          // counted as one. Its own bucket, out of the compile/verify denominators entirely.
          if (res == null || !res.Ok || proposal == null)
          {
            row.ModelFailure = res?.Error ?? row.Error ?? "no usable proposal";
            row.Error = null;
            // #144: count CONSECUTIVE IDENTICAL provider failures - a dead account / invalid key / rate-limit storm
            // repeats verbatim, where genuine per-row model failures vary.
            if (row.ModelFailure == _lastModelFailure) _consecutiveModelFailures++;
            else { _lastModelFailure = row.ModelFailure; _consecutiveModelFailures = 1; }
            finish();
            return;
          }
          _consecutiveModelFailures = 0; _lastModelFailure = null;   // a live call clears the streak

          ToScriptValidator.ApplyNames(proposal, tsc.Iface);
          tsc.Resolve.Source = proposal.Source;
          tsc.Resolve.Iface = tsc.Iface;
          row.Source = proposal.Source;
          // the model's names, AS APPLIED (sanitised) - the exact identifiers the source binds to, so Reclassify
          // can recompile the saved script without re-billing the model.
          row.InNames = string.Join("|", tsc.Iface.Inputs.Select(p => p.Variable));
          row.OutNames = string.Join("|", tsc.Iface.Outputs.Select(p => p.Variable));

          ToScriptCompileGate.Trial(tsc.Resolve, proposal.Source, trial =>
          {
            // #109: an UNCHECKED trial (the solver was disabled) is not a build/verify fact - its own excluded
            // bucket, never counted as a verify. Unreachable in a normal eval (the corpus doc solves), but honest.
            if (!trial.Checked) { row.ModelFailure = "solver disabled - the trial could not be checked"; finish(); return; }
            if (trial.Built) row.Delivered = true;   // #140: sticky across the recursion - F1's best-built candidate
            row.Built = trial.Built;
            row.Verified = trial.Verified;
            row.Error = trial.Errors.FirstOrDefault();
            row.Outputs = trial.OutputCount;
            row.Mismatch = trial.MismatchedOutputs.Count == 0 ? null : string.Join(", ", trial.MismatchedOutputs);
            row.Kinds = trial.MismatchKinds.Count == 0 ? null : string.Join(", ", trial.MismatchKinds);
            row.KindsBbox = trial.MismatchKindsBbox.Count == 0 ? null : string.Join(", ", trial.MismatchKindsBbox);

            // ONE budget, TWO oracles (#90/#100). The compile-repair is an RR20 input (the before-run turns it off,
            // #56); the OUTPUT oracle is the WP-0 variable. Whichever fires draws from the SAME shared budget - never
            // two nested loops - so a run that spends its budget on compile repairs has none left for the oracle
            // (the starvation the per-row VerifyRepairs count measures, #100).
            if (attempt < DwightConfig.ToScriptMaxAttempts)
            {
              // ORACLE 1 - the compiler.
              if (!trial.Built && EvalFlags.CompileRepair)
              {
                var repair = ToScriptCompileGate.RepairPrompt(trial.Errors, lang);
                Attempt(tsc, graph, lang, model, system, user, attempt + 1, repair, row, finish);
                return;
              }
              // ORACLE 2 - the output diff. Built but did not verify -> feed the CLASS-ONLY diff back (#88/#89: the
              // values never travel; RefineFeedback has no field for them).
              if (trial.Built && !trial.Verified && EvalFlags.OutputOracle)
              {
                row.VerifyRepairs++;   // #100: how many times the output oracle ACTUALLY fired (the starvation read)
                var refine = Dwight.Data.RefineFeedback.MismatchClassified(trial.Diffs);
                Attempt(tsc, graph, lang, model, system, user, attempt + 1, refine.ToPrompt(), row, finish);
                return;
              }
            }
            // The RETRY structure above is branch-identical to the product loop (LlmActions.ToScriptAttempt); the
            // eval only adds the flag gates so the arms can toggle each oracle. F1's best-built DRAFT RETENTION is a
            // product DISPLAY concern with no eval analogue - the eval records rates, and the delivered draft never
            // verifies, so recording the FINAL attempt keeps Source<->Built self-consistent for Reclassify while
            // leaving VERIFY-rate (the oracle's headline) identical either way.

            // Stage C (#101): the SECOND-INPUT-STATE overfit check, only on a state-1-verified row. Harness-only
            // (#93a); it perturbs the upstream value sources, re-solves, compares 1:1 again, and restores under the
            // full #118 discipline. A restore failure ABORTS the whole run (#118.4). `row.Verified` stays the state-1
            // number (the oracle's lift); SecondStateVerified is the stricter #99/#101 label number.
            if (trial.Verified && EvalFlags.SecondState)
            {
              SecondStatePass.Run(tsc.Resolve, ss =>
              {
                if (ss.AbortReason != null) { row.AbortReason = ss.AbortReason; finish(); return; }
                row.SecondStateBucket = ss.Bucket.ToString().ToLowerInvariant();
                row.SecondStateVerified = ss.Verified;
                row.CountsForOverfit = ss.CountsForOverfit;
                row.Overfit = ss.CountsForOverfit && !ss.Verified;
                row.HasPerturbable = ss.HasPerturbable;
                row.Roots = ss.Roots;
                finish();
              });
              return;
            }
            row.SecondStateBucket = trial.Verified ? "secondstateoff" : "failedstate1";
            row.SecondStateVerified = trial.Verified;
            finish();
          });
        });
      });
    }

    /// <summary>
    /// RR20: RE-CLASSIFY a completed arm from its SAVED SOURCES - no model calls, so it costs nothing.
    ///
    /// This exists because the first mismatch classifier compared geometry by BOUNDING BOX, which could not tell
    /// "the same curve carried by a different concrete class" (our digest's fault) from "a different curve with the
    /// same bbox" (the model's fault). The taxonomy is the number that decides what we fix next, so it has to be
    /// trustworthy - and re-running the stored scripts through the fixed classifier is free.
    ///
    /// It re-solves each script against the SAME corpus (asserted by hash) and rewrites the kinds.
    /// </summary>
    public static string Reclassify(string path, string language = "csharp")
    {
      if (_running) return "a run is in progress";
      if (!File.Exists(path)) return "no such file: " + path;
      if (_corpus.Count == 0) return "no corpus - call BuildCorpus first";

      var lang = language.Equals("python", StringComparison.OrdinalIgnoreCase) ? ScriptLanguage.Python3 : ScriptLanguage.CSharp;
      var items = new List<(int Index, string Source, string[] InNames, string[] OutNames)>();
      var missingNames = 0;
      foreach (var line in File.ReadAllLines(path))
      {
        if (string.IsNullOrWhiteSpace(line)) continue;
        var o = JsonNode.Parse(line.TrimStart('﻿')) as JsonObject;
        var src = o?["source"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(src)) continue;
        var inN = o["inNames"]?.GetValue<string>();
        var outN = o["outNames"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(inN) && string.IsNullOrWhiteSpace(outN)) missingNames++;
        items.Add((o["i"].GetValue<int>(), src,
          (inN ?? "").Split('|'), (outN ?? "").Split('|')));
      }
      if (items.Count == 0) return "no rows with a saved source";
      // RR20: without the model's names the saved source cannot be recompiled (its variables do not bind), so a
      // reclassify would report a spurious 0% built. Refuse honestly rather than produce a meaningless taxonomy.
      if (missingNames > items.Count / 2)
        return $"REFUSED: {missingNames}/{items.Count} rows have no saved param names - this arm predates name-saving and cannot be reclassified for free. Re-run it (temp 0, so near-identical) and reclassify that.";

      _running = true;
      _reclassPath = path + ".reclass.jsonl";
      try { File.Delete(_reclassPath); } catch { }
      Rhino.RhinoApp.WriteLine($"Dwight eval: re-classifying {items.Count} saved scripts (no model calls) -> {_reclassPath}");
      Reclass(items, 0, lang);
      return $"re-classifying {items.Count} saved scripts (FREE - no model calls) -> {_reclassPath}";
    }

    private static string _reclassPath;

    private static void Reclass(List<(int Index, string Source, string[] InNames, string[] OutNames)> items, int k, ScriptLanguage lang)
    {
      if (k >= items.Count)
      {
        _running = false;
        Rhino.RhinoApp.WriteLine("Dwight eval: re-classify DONE -> " + _reclassPath);
        return;
      }

      var it = items[k];
      void Step() => Eto.Forms.Application.Instance.AsyncInvoke(() => Reclass(items, k + 1, lang));

      try
      {
        var doc = Instances.ActiveCanvas?.Document;
        if (it.Index < 0 || it.Index >= _corpus.Count) { Step(); return; }
        var graph = DwightGraph.Build(doc, _corpus[it.Index]);
        var tsc = ToScriptResolveBuilder.Build(graph, doc, lang);
        if (tsc.Refusal != null) { Step(); return; }

        // the INTERFACE is plugin-computed and identical (the whole point of the law); apply the SAVED names so the
        // script's variables bind exactly as they did at generation time, then re-solve. Directly, not via
        // ApplyNames (which re-sanitises), because these are already the applied identifiers.
        for (var i = 0; i < tsc.Iface.Inputs.Count && i < it.InNames.Length; i++) tsc.Iface.Inputs[i].Variable = it.InNames[i];
        for (var j = 0; j < tsc.Iface.Outputs.Count && j < it.OutNames.Length; j++) tsc.Iface.Outputs[j].Variable = it.OutNames[j];
        tsc.Resolve.Iface = tsc.Iface;
        tsc.Resolve.Source = it.Source;

        ToScriptCompileGate.Trial(tsc.Resolve, it.Source, trial =>
        {
          // #127: the FREE shakedown + the ORACLE-OFF OVERFIT BASE RATE. On a verified saved script, run the full
          // second-state pass (perturb, validity gate, compare, restore) - real data, zero tokens, so any restore
          // bug surfaces here, not on a paid run, and the base overfit rate is measured, not assumed.
          void Write(SecondStateResult ss)
          {
            try
            {
              var o = new JsonObject
              {
                ["i"] = it.Index,
                ["built"] = trial.Built,
                ["verified"] = trial.Verified,
                ["secondStateVerified"] = ss?.Verified ?? trial.Verified,
                ["secondStateBucket"] = ss != null ? ss.Bucket.ToString().ToLowerInvariant() : (trial.Verified ? "secondstateoff" : "failedstate1"),
                ["overfit"] = ss != null && ss.CountsForOverfit && !ss.Verified,
                ["countsForOverfit"] = ss?.CountsForOverfit ?? false,
                ["hasPerturbable"] = ss?.HasPerturbable ?? false,
                ["roots"] = ss?.Roots ?? 0,
                ["abort"] = ss?.AbortReason,
                ["kinds"] = trial.MismatchKinds.Count == 0 ? null : string.Join(", ", trial.MismatchKinds),
                ["kindsBbox"] = trial.MismatchKindsBbox.Count == 0 ? null : string.Join(", ", trial.MismatchKindsBbox),
                ["mismatch"] = trial.MismatchedOutputs.Count == 0 ? null : string.Join(", ", trial.MismatchedOutputs),
                ["error"] = trial.Errors.FirstOrDefault(),
              };
              File.AppendAllText(_reclassPath, o.ToJsonString() + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
          }
          if (trial.Verified && EvalFlags.SecondState)
          {
            SecondStatePass.Run(tsc.Resolve, ss =>
            {
              Write(ss);
              if (ss.AbortReason != null) { _running = false; Rhino.RhinoApp.WriteLine("Dwight eval: RECLASSIFY ABORTED - " + ss.AbortReason + ". Reopen the definition WITHOUT saving."); return; }
              Step();
            });
            return;
          }
          Write(null);
          Step();
        });
      }
      catch { Step(); }
    }

    private static void Append(Row r)
    {
      try
      {
        var o = new JsonObject
        {
          ["i"] = r.Index, ["size"] = r.Size, ["lang"] = r.Language, ["arm"] = _arm,
          ["refusal"] = r.Refusal, ["modelFailure"] = r.ModelFailure,
          ["built"] = r.Built, ["verified"] = r.Verified, ["delivered"] = r.Delivered,   // #140
          // Stage C (#101): verified = state 1; secondStateVerified = the #99/#101 label number; the bucket +
          // overfit flag drive the overfit rate; abort = a restore failure that stopped the run (#118.4).
          ["secondStateVerified"] = r.SecondStateVerified, ["secondStateBucket"] = r.SecondStateBucket,
          ["overfit"] = r.Overfit, ["countsForOverfit"] = r.CountsForOverfit,
          ["hasPerturbable"] = r.HasPerturbable, ["roots"] = r.Roots, ["abort"] = r.AbortReason,
          ["stopReason"] = r.StopReason,   // #144/#153: a truncated arm says so IN the record
          ["outputs"] = r.Outputs, ["attempts"] = r.Attempts, ["verifyRepairs"] = r.VerifyRepairs, ["ms"] = r.Ms,
          ["error"] = r.Error, ["mismatch"] = r.Mismatch, ["kinds"] = r.Kinds, ["kindsBbox"] = r.KindsBbox,
          ["model"] = r.Model, ["corpus"] = r.CorpusHash, ["flags"] = r.Flags,   // #63/#111/F2: no unstamped rows
          ["inNames"] = r.InNames, ["outNames"] = r.OutNames,   // so Reclassify can recompile the saved source
          ["source"] = r.Source,
        };
        File.AppendAllText(_path, o.ToJsonString() + Environment.NewLine, Encoding.UTF8);
      }
      catch { }
    }
  }
}
