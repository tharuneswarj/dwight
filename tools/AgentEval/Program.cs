using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dwight;
using Dwight.Agent;
using Dwight.Credentials;
using Dwight.Llm;
using Dwight.Models;

namespace Dwight.AgentEval
{
  /// <summary>
  /// Lane A's console (Brief BY): the real agent loop on real providers, with no Rhino. See <c>tools/AgentEval/README.md</c>.
  /// Keys are read from the OS credential store by this process, are held in memory, and pass through no argument, file or line of output.
  /// </summary>
  internal static class Program
  {
    private static int Main(string[] args)
    {
      var keys = new KeyRule();
      try { return Run(Options.Parse(args), keys); }
      catch (Exception ex)
      {
        Console.Error.WriteLine(keys.Scrub(ex.Message));
        return 2;
      }
    }

    private sealed class Options
    {
      public string Rows, Out, Components, Fixtures;
      public double CapCents = 100;
      public int Concurrency = 4;
      public bool SkipPreflight;
      /// <summary>Brief BZ: eligibility is by measurement, and this is how a row the catalogue does not yet mark gets measured. In memory, for this run: the catalogue changes only when the gate passes.</summary>
      public bool PinForGate;
      /// <summary>A chat turn's steps, as Settings' Steps per turn would set them. Null is the product's default.</summary>
      public int? StepsPerTurn;

      /// <summary>The list has no default: it is a file on the machine that made it, never in the repository.</summary>
      public const string MissingComponents =
        "--components is required: the path to gh_component_list.json, a JSON list of every component Grasshopper's ComponentServer offers " +
        "(name, category, subcategory, description and guid), dumped once from a Grasshopper session on your own machine. It is not in the " +
        "repository, because it names that machine's user objects and plugins. A path to a file that does not exist runs with kb_lookup as a fixture miss, and the result says the list was not loaded.";

      public static Options Parse(string[] a)
      {
        var o = new Options { Fixtures = Path.Combine(AppContext.BaseDirectory, "fixtures") };
        for (var i = 0; i < a.Length; i++)
        {
          string Next() => i + 1 < a.Length ? a[++i] : throw new ArgumentException("Nothing follows " + a[i] + ".");
          switch (a[i])
          {
            case "--rows": o.Rows = Next(); break;
            case "--out": o.Out = Next(); break;
            case "--components": o.Components = Next(); break;
            case "--fixtures": o.Fixtures = Next(); break;
            case "--cap-cents": o.CapCents = double.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture); break;
            case "--concurrency": o.Concurrency = Math.Max(1, int.Parse(Next())); break;
            case "--skip-preflight": o.SkipPreflight = true; break;
            case "--pin-for-gate": o.PinForGate = true; break;
            case "--steps-per-turn": o.StepsPerTurn = EvalLimits.ParseSteps(Next()); break;
            case "--strict-thinking-check": AnthropicWire.StrictPrefixCheckForGate = true; break;
            case "--thinking-check-control": AnthropicWire.StrictPrefixCheckForGate = true; AnthropicWire.ElideAnywayForGate = true; break;
            default: throw new ArgumentException("Unknown argument: " + a[i] + ". Usage: AgentEval --rows rows.json --out dir --components file [--cap-cents 100] [--concurrency 4] [--fixtures dir] [--skip-preflight]");
          }
        }
        if (o.Components == null) throw new ArgumentException(MissingComponents);
        if (o.Rows == null || o.Out == null) throw new ArgumentException("--rows and --out are required.");
        return o;
      }
    }

    private static int Run(Options o, KeyRule keys)
    {
      ModelCatalogue.BundledOnly = true;   // a test run never seeds or refreshes the user's copy
      Directory.CreateDirectory(o.Out);

      var rows = EvalRow.Parse(File.ReadAllText(o.Rows));
      var components = File.Exists(o.Components) ? ComponentList.Parse(File.ReadAllText(o.Components)) : new ComponentList();
      Console.WriteLine(rows.Count + " rows, cap " + o.CapCents + " cents, concurrency " + o.Concurrency + ", component list " +
                        (components.Loaded ? "loaded (" + components.Types.Count + " types)" : "NOT LOADED, kb_lookup rows will say so"));

      foreach (var provider in rows.Select(r => r.Provider).Distinct())
      {
        var target = LlmProviders.TargetFor(provider);
        if (target != null) keys.Hold(CredentialStore.Load(target));
      }

      var guard = new RunGuard(o.CapCents);
      var eligible = new List<EvalRow>();
      var skipped = new List<(EvalRow Row, string Why)>();
      foreach (var row in rows)
      {
        var model = DwightConfig.FindModel(row.Model);
        if (o.PinForGate) model?.PinAgenticForGate();
        var why = model == null ? "The catalogue has no model \"" + row.Model + "\"."
          : !LlmProviders.HasKey(row.Provider) ? "No key is stored for " + row.Provider + "."
          : LlmProviders.AgentRefusal(model, row.Provider, true);
        if (why != null) skipped.Add((row, why)); else eligible.Add(row);
      }

      if (!o.SkipPreflight)
        foreach (var pair in eligible.Select(r => (r.Provider, r.Model)).Distinct().ToList())
        {
          var failed = Preflight(pair.Provider, pair.Model, guard);
          if (failed == null) { Console.WriteLine("pre-flight ok: " + pair.Provider + " " + pair.Model); continue; }
          Console.WriteLine("pre-flight FAILED: " + pair.Provider + " " + pair.Model + ": " + keys.Scrub(failed));
          foreach (var row in eligible.Where(r => r.Provider == pair.Provider && r.Model == pair.Model).ToList())
          {
            eligible.Remove(row);
            skipped.Add((row, "Pre-flight failed: " + failed));
          }
        }

      var summary = new List<RowSummary>();
      var results = new List<RowResult>();
      var gate = new object();
      foreach (var (row, why) in skipped)
        summary.Add(new RowSummary { Row = row.Id, Ended = "not run: " + why });

      Parallel.ForEach(eligible, new ParallelOptions { MaxDegreeOfParallelism = o.Concurrency }, row =>
      {
        RowSummary line;
        RowResult done = null;
        if (guard.OverCap) line = new RowSummary { Row = row.Id, Ended = "not run: " + GuardedProvider.CapMessage };
        else if (guard.BreakerOpen(row.Provider)) line = new RowSummary { Row = row.Id, Ended = "not run: " + GuardedProvider.BreakerMessage };
        else
        {
          var result = done = RunRow(row, o, components, guard);
          ResultWriter.Write(result, o.Out, keys);
          line = new RowSummary { Row = row.Id, Ended = result.EndedBecause, Cents = result.TotalCents, Seconds = result.TotalSeconds, Turns = result.Turns.Count };
          Console.WriteLine(keys.Scrub(row.Id + ": " + result.EndedBecause + ", " + result.Turns.Count + " turns, " + result.TotalCents.ToString("0.00") + " cents"));
        }
        lock (gate)
        {
          summary.Add(line);
          if (done != null) results.Add(done);
        }
      });

      keys.WriteFile(Path.Combine(o.Out, "_run.json"), JsonSerializer.Serialize(new
      {
        didNotRunInGrasshopper = EvalNotReal.Statement,
        capCents = guard.CapCents,
        spentCents = guard.SpentCents,
        announcing = AnnouncingCounts.ByProvider(results),
        rows = summary,
      }, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
      Console.WriteLine("spent " + guard.SpentCents.ToString("0.00") + " of " + guard.CapCents + " cents. Results in " + o.Out);
      return 0;
    }

    private sealed class RowSummary
    {
      public string Row;
      public string Ended;
      public double Cents;
      public double Seconds;
      public int Turns;
    }

    /// <summary>#144: one small real call per provider and model, before a row is spent on either. Returns why it failed, or null.</summary>
    private static string Preflight(string provider, string modelId, RunGuard guard)
    {
      try
      {
        var model = DwightConfig.FindModel(modelId);
        var guarded = new GuardedProvider(LlmProviders.Create(provider, modelId), guard, provider, u => Cents(model, u));
        using (var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(90)))
          guarded.Send(new LlmRequest { System = "Reply with one word.", Messages = { LlmMessage.User("Ready?") }, MaxTokens = 16 }, cancel.Token);
        return null;
      }
      catch (Exception ex)
      {
        guard.Open(provider);
        return ex.Message;
      }
    }

    private static RowResult RunRow(EvalRow row, Options o, ComponentList components, RunGuard guard)
    {
      var model = DwightConfig.FindModel(row.Model);
      var posture = DwightConfig.PostureBalanced;
      var thinks = ChatHeader.PinThinks(model, row.Provider, posture);
      var effort = thinks ? ThinkingWire.EffortForProfile(posture) : null;

      var fixturePath = Path.Combine(o.Fixtures, row.Fixture + ".json");
      var fixture = EvalFixture.Parse(row.Fixture, File.ReadAllText(fixturePath));
      var provider = new GuardedProvider(LlmProviders.Create(row.Provider, row.Model), guard, row.Provider, u => Cents(model, u));

      // The session backstop the product gives a turn, once per row's worth of turns, so a wedged row cannot hold a slot.
      using (var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(DwightConfig.AgentBuildSessionTimeoutMs * 2)))
        return new RowRunner(row, fixture, components, provider, c => EvalLimits.For(c, o.StepsPerTurn), u => Cents(model, u), thinks, effort, cancel.Token).Run();
    }

    private static double Cents(ModelInfo model, LlmUsage u) =>
      model.CostCents(u.InputTokens, u.OutputTokens, u.CacheReadInputTokens, u.CacheCreationInputTokens);
  }
}
