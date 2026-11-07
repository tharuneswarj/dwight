using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dwight.Agent;
using Dwight.Llm;
using Dwight.Models;

namespace Dwight.AgentEval
{
  /// <summary>One row: who is asking, what, on which provider and model, under which approval mode, on which canned world.</summary>
  internal sealed class EvalRow
  {
    public string Id;
    public string Persona = "";
    /// <summary>"scope" asks <see cref="ScopeJudge"/> to mark each turn; anything else marks nothing.</summary>
    public string Judge;
    public string Provider;
    public string Model;
    /// <summary><see cref="ApprovalMode.Ask"/>, <see cref="ApprovalMode.ScriptsAndDeletions"/> or <see cref="ApprovalMode.Never"/>.</summary>
    public string ApprovalMode = Dwight.Agent.ApprovalMode.Ask;
    public string Fixture = "empty";
    public bool FullControl;
    public string BuildStyle = Dwight.Agent.BuildStyle.Auto;
    /// <summary>What the row's user does with a card left waiting: "approve", "decline", or "ignore".</summary>
    public string OnCard = "approve";
    public List<string> Turns = new List<string>();
    /// <summary>The most model turns this row may run, follow-ups and approved turns included.</summary>
    public int MaxTurns = 12;

    public static List<EvalRow> Parse(string json)
    {
      var rows = new List<EvalRow>();
      foreach (var n in JsonNode.Parse(json).AsArray())
      {
        var r = new EvalRow
        {
          Id = Str(n, "id"),
          Persona = Str(n, "persona") ?? "",
          Judge = Str(n, "judge"),
          Provider = Str(n, "provider"),
          Model = Str(n, "model"),
          ApprovalMode = Dwight.Agent.ApprovalMode.Normalise(Str(n, "approvalMode")),
          Fixture = Str(n, "fixture") ?? "empty",
          FullControl = n["fullControl"]?.GetValue<bool>() ?? false,
          BuildStyle = Str(n, "buildStyle") ?? Dwight.Agent.BuildStyle.Auto,
          OnCard = Str(n, "onCard") ?? "approve",
          MaxTurns = n["maxTurns"]?.GetValue<int>() ?? 12,
        };
        var single = Str(n, "request");
        if (single != null) r.Turns.Add(single);
        if (n["turns"] is JsonArray turns) r.Turns.AddRange(turns.Select(t => t.GetValue<string>()));
        if (string.IsNullOrWhiteSpace(r.Id) || string.IsNullOrWhiteSpace(r.Provider) || string.IsNullOrWhiteSpace(r.Model) || r.Turns.Count == 0)
          throw new InvalidDataException("A row needs id, provider, model, and request or turns: " + n.ToJsonString());
        if (r.Id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
          throw new InvalidDataException("A row id becomes a file name, and this one cannot: " + r.Id);
        rows.Add(r);
      }
      return rows;
    }

    private static string Str(JsonNode n, string key) => n[key]?.GetValue<string>();
  }

  /// <summary>
  /// A stated world, written by hand and never a client's: a canvas of nodes, wires and positions that the product's own projections answer over, and
  /// <c>canned</c> for anything else, a tool name mapped to its envelope or to a list of <c>{argsContain, envelope}</c> tried in order. A read nothing can
  /// answer is a fixture miss: the result counts it, and a judge reads it as the instrument's gap and never the product's.
  /// </summary>
  internal sealed class EvalFixture
  {
    public string Name = "empty";
    /// <summary>What the fixture says about where it came from. In every result.</summary>
    public string Origin = "An empty canvas, stated by hand.";
    public JsonObject Canvas;
    public JsonObject Canned = new JsonObject();

    public static EvalFixture Parse(string name, string json)
    {
      var n = JsonNode.Parse(json).AsObject();
      return new EvalFixture
      {
        Name = name,
        Origin = n["origin"]?.GetValue<string>() ?? "Stated by hand.",
        Canvas = n["canvas"]?.AsObject(),
        Canned = n["canned"]?.AsObject() ?? new JsonObject(),
      };
    }

    public string CannedFor(string tool, string argsJson)
    {
      var node = Canned[tool];
      if (node is JsonArray list)
      {
        foreach (var option in list)
        {
          var needle = option["argsContain"]?.GetValue<string>();
          if (needle == null || (argsJson ?? "").IndexOf(needle, StringComparison.Ordinal) >= 0)
            return option["envelope"]?.ToJsonString();
        }
        return null;
      }
      return node?.ToJsonString();
    }
  }

  /// <summary>Grasshopper's real component list, dumped once from Rhino. Absent, and <see cref="Loaded"/> says so: no row pretends to know it.</summary>
  internal sealed class ComponentList
  {
    public bool Loaded;
    public List<InstalledType> Types = new List<InstalledType>();
    private readonly List<LoadedLibrary> _libraries = new List<LoadedLibrary>();
    private readonly List<ServerProxy> _proxies = new List<ServerProxy>();

    // What the dump's machine holds for building Dwight, which a user's does not: a row must not teach a model it could call a bridge.
    private static readonly HashSet<string> HiddenComponents = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "LAMCP Bridge", "Grasshopper MCP" };
    private static readonly HashSet<string> HiddenLibraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "GH_MCP", "Dwight" };

    public static ComponentList Parse(string json)
    {
      var n = JsonNode.Parse(json);
      var list = new ComponentList { Loaded = true };
      foreach (var c in n["components"].AsArray())
      {
        if (HiddenComponents.Contains(c["name"]?.GetValue<string>())) continue;
        var guid = Guid.TryParse(c["guid"]?.GetValue<string>(), out var g) ? g : Guid.Empty;
        list.Types.Add(new InstalledType
        {
          Name = c["name"]?.GetValue<string>(),
          Category = c["category"]?.GetValue<string>(),
          SubCategory = c["subCategory"]?.GetValue<string>(),
          Description = c["description"]?.GetValue<string>(),
          Guid = guid,
        });
        list._proxies.Add(new ServerProxy
        {
          Guid = guid,
          Library = Guid.TryParse(c["library"]?.GetValue<string>(), out var l) ? l : Guid.Empty,
          UserObject = c["userObject"]?.GetValue<bool>() ?? false,
          Category = c["category"]?.GetValue<string>(),
        });
      }
      foreach (var l in n["libraries"]?.AsArray() ?? new JsonArray())
      {
        var name = l["name"]?.GetValue<string>();
        if (HiddenLibraries.Contains(name)) continue;
        var version = l["version"]?.GetValue<string>();
        list._libraries.Add(new LoadedLibrary
        {
          Id = Guid.TryParse(l["id"]?.GetValue<string>(), out var id) ? id : Guid.Empty,
          Name = name,
          Version = version,
          Core = LooksCore(name, version),
        });
      }
      return list;
    }

    /// <summary>The plugin inventory, through the product's own join. The canvas is not passed, so <c>usedHere</c> is 0 in every row: lane A does not measure it.</summary>
    public List<PluginFact> Inventory() => PluginInventoryFacts.Join(_libraries, _proxies, null);

    /// <summary>The dump did not read <c>IsCoreLibrary</c>, so this is a stand-in from what it did read: Rhino's own libraries carry Rhino's
    /// version number. Only the split between "core" and "plugins" in the answer depends on it.</summary>
    private static bool LooksCore(string name, string version) =>
      string.Equals(name, "Grasshopper", StringComparison.OrdinalIgnoreCase) || (version ?? "").StartsWith("8.", StringComparison.Ordinal) && (name ?? "").EndsWith(" Components", StringComparison.Ordinal);

    public IReadOnlyList<string> Nearest(string name) =>
      Types.Select(t => t.Name).Where(n => n != null).Distinct(StringComparer.OrdinalIgnoreCase)
        .Where(n => n.IndexOf(name ?? "", StringComparison.OrdinalIgnoreCase) >= 0 || (name ?? "").IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0)
        .Take(5).ToList();
  }

  // ---- what a row records --------------------------------------------------------------------------------------

  internal sealed class ToolRecord
  {
    public string Name;
    public string Arguments;
    /// <summary>real-core (the product's own code over the fixture), fixture, recorded (a mutation, not performed), refused, or fixture-miss.</summary>
    public string AnsweredBy;
    public string Answer;
  }

  internal sealed class CardRecord
  {
    public string Kind;
    public string Why;
    public string Seed;
    /// <summary>What <see cref="ApprovalMode.Decide"/> said: auto or ask.</summary>
    public string Decision;
    /// <summary>approved-automatically, approved-by-row, declined-by-row, ignored-by-row, or left-waiting when the row ran out.</summary>
    public string Answer = "left-waiting";
    /// <summary>The address the plugin minted, on a placement with no map. Null on every other card.</summary>
    public string Spot;
  }

  /// <summary>
  /// One fact the plugin stated at a turn's start, as a judge reads it. The system text was never saved, so a fact that had gone out and one that had not looked the same in a result:
  /// lane A's first read of PR #179 could not tell whether the turn had been told. <see cref="Sent"/> is read from the system text that went out, not assumed.
  /// </summary>
  internal sealed class TurnStartFact
  {
    public string Name;
    public string Text;
    public bool Sent;

    /// <param name="stated">Name and text. A null text is a fact this turn did not have, and is left out.</param>
    public static List<TurnStartFact> Of(string system, params (string Name, string Text)[] stated) =>
      stated.Where(f => !string.IsNullOrWhiteSpace(f.Text))
            .Select(f => new TurnStartFact { Name = f.Name, Text = f.Text.Trim(), Sent = (system ?? "").Contains(f.Text.Trim()) })
            .ToList();
  }

  /// <summary>The panel's two sets of limits, from the same constants. A chat turn's steps are its Steps per turn; an approved build keeps its own, as in the product.</summary>
  internal static class EvalLimits
  {
    public static TurnLimits For(TurnCapability capability, int? stepsPerTurn = null) =>
      capability == TurnCapability.Build
        ? new TurnLimits { MaxSteps = DwightConfig.AgentBuildMaxStepsPerTurn, WallClockMs = DwightConfig.AgentBuildWallClockMs, MaxTranscriptChars = DwightConfig.AgentMaxTranscriptChars, AnswerBudget = DwightConfig.AgentBuildAnswerBudget }
        : new TurnLimits { MaxSteps = stepsPerTurn ?? DwightConfig.AgentMaxStepsPerTurn, WallClockMs = DwightConfig.AgentTurnWallClockMs, MaxTranscriptChars = DwightConfig.AgentMaxTranscriptChars, AnswerBudget = DwightConfig.AgentTurnAnswerBudget };

    /// <summary><c>--steps-per-turn</c>. Refused rather than clamped: Settings cannot offer a number outside the bounds, and a run that quietly measured another number would say nothing.</summary>
    public static int ParseSteps(string text)
    {
      if (!int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var steps) || steps != ChatSteps.Clamp(steps))
        throw new ArgumentException("--steps-per-turn takes a whole number from " + ChatSteps.Min + " to " + ChatSteps.Max + ", as Settings does: \"" + text + "\".");
      return steps;
    }
  }

  internal sealed class TurnRecord
  {
    public int Number;
    public bool ClosingStep;
    /// <summary>What the plugin told this turn at its start, each with whether it went out.</summary>
    public List<TurnStartFact> TurnStartFacts = new List<TurnStartFact>();
    public int SystemChars;
    public string Capability;
    public bool FollowUp;
    public bool Forced;
    public bool RequestOffered;
    public string Outcome;
    public string StopReason;
    public string Note;
    public string Error;
    public string Text;
    /// <summary>The model's prose on a follow-up with no card: withheld from the transcript as the product withholds it, kept here for the judge.</summary>
    public string WithheldText;
    public int Steps;
    public int InputTokens;
    public int CacheReadTokens;
    public int CacheWriteTokens;
    public int OutputTokens;
    public double Cents;
    public double Seconds;
    public List<ToolRecord> Tools = new List<ToolRecord>();
    public List<CardRecord> Cards = new List<CardRecord>();
    /// <summary>What the plugin would have said on screen this turn, in order.</summary>
    public List<string> PluginLines = new List<string>();
    /// <summary>The turn's last word announced a change and no card was asked for: what draws the product's turn-end line (generous).</summary>
    public bool AnnouncedLineCatch;
    /// <summary>The same, by the catch that may force a card (strict). What forces, and what a widening would change.</summary>
    public bool AnnouncedForcingCatch;
    /// <summary>The same, by plain words alone: "I'll", "I will", "let me", "going to" anywhere in a last word with no request. Over-counts on purpose,
    /// because neither catch saw "I'll structure this as...". It decides nothing; it is the upper bound for the ruling.</summary>
    public bool SaidWillNoCard;
    /// <summary>The sentence the strict catch fired on, or the plain-word sentence, for a judge to read.</summary>
    public string AnnouncedSentence;
  }

  internal sealed class RowResult
  {
    /// <summary>In every result, first, by construction: see <see cref="EvalNotReal"/>.</summary>
    public string DidNotRunInGrasshopper = EvalNotReal.Statement;
    public List<string> NotReal = EvalNotReal.Items.ToList();
    public string RowId;
    public string Judge;
    /// <summary>What <see cref="ScopeJudge"/> marked, per user turn, with its reasons. Empty unless the row asks.</summary>
    public List<ScopeJudge.Verdict> ScopeVerdicts = new List<ScopeJudge.Verdict>();
    public string Persona;
    public string Provider;
    public string Model;
    public string ApprovalMode;
    public string Fixture;
    public bool FullControl;
    public string FixtureOrigin;
    public bool ComponentListLoaded;
    /// <summary>The steps a chat turn of this row could take.</summary>
    public int StepsPerTurn;
    public string EndedBecause;
    public List<string> UserTurns = new List<string>();
    public List<TurnRecord> Turns = new List<TurnRecord>();
    public List<string> UnknownToolsTried = new List<string>();
    /// <summary>Sentences where the model said it lacked a tool. Found by plain words, so a judge reads them; it counts nothing.</summary>
    public List<string> SaidItLacked = new List<string>();
    public List<string> FixtureMisses = new List<string>();
    public int TotalInputTokens;
    public int TotalOutputTokens;
    public double TotalCents;
    public double TotalSeconds;
  }

  /// <summary>What every result says about itself. A row can prove what the agent would do and say, never that a build works.</summary>
  internal static class EvalNotReal
  {
    public const string Statement =
      "This row did not run in Grasshopper. Nothing was placed, solved or changed. It shows what the agent would do and say, not that a build works.";

    public static readonly IReadOnlyList<string> Items = new[]
    {
      "Every mutation was recorded, not performed. A build's verdict has the product's shape and real ids, but its outcome is stated: it always lands, clean. So a landed build here proves nothing about the build, and a repair loop after a real failure is not exercised. An approved value, script edit or wire is written into the stated world with no solve behind it, so later reads see it and a landed one proves nothing.",
      "A recorded component has only the ports a script declares or a same-type component on the canvas shows. Every output on the canvas is stated to hold one item, with no values, and diagnose always answers clean.",
      "Card refusals that need the live canvas did not run: named components, a script's trial and its attempts per target. A script edit's trial is stated clean, so a script that would not compile or run gets a card and its report says it was checked. A placement's check, an edit's rows, a value's target and state, and a script edit's target, claim, source and ports ran, through the product's own code over the stated world: a port is known only for a component on the stated canvas or one of its type, so a wrong port elsewhere passes. A stated slider's domain and a stated script's source are not known until a card or a build writes them, so the restore rule and the diff have nothing to read before that. A 'too strict' finding on a card needs lane R to confirm it.",
      "A Full control script did not run: its report is stated, with no change and no output.",
      "The script-against-card comparison at the build (U65), the build's region scope and its attempt cap did not run.",
      "The order of follow-ups and approvals is this harness's copy of the panel's sequence, calling the product's own decisions.",
      "The canvas is a stated world, answered by the product's own projections. Its regions are stated, not cut by the segmenter, and the geometry, cluster-internal and solve-timing reads are fixture misses.",
      "A placement lands where the product's own search says, over the stated boxes, so its sentence is the product's. The sizes are stated, and there is no view, so one with no wires starts past the drawing's edge where the panel starts at the user's view. A map region's place, its sentence and every framing of the view are not here.",
      "kb_lookup answers from a saved component list, with ports only for a type on the stated canvas. The product also makes a temporary instance to read a type's ports, which needs Grasshopper, so here a type not on the canvas reads unavailable where the product would answer.",
      "No turn was told what the last build did (the build-outcome fact), because no build ran.",
      "Whether new work overlaps old was not measured, because the sentence reads real bounds after the layout, so the product's overlap line never appears here.",
    };
  }

  /// <summary>
  /// THE KEY RULE. Keys are read by this process from the credential store and live only in memory. Everything written or printed passes
  /// through here, and a written file is read back and checked, so a key that reaches a result stops the run instead of being published.
  /// </summary>
  internal sealed class KeyRule
  {
    public const string Redacted = "[key removed]";
    private readonly List<string> _keys = new List<string>();
    private readonly object _gate = new object();

    public void Hold(string key)
    {
      if (string.IsNullOrEmpty(key)) return;
      lock (_gate) { if (!_keys.Contains(key)) _keys.Add(key); }
    }

    public string Scrub(string text)
    {
      if (string.IsNullOrEmpty(text)) return text;
      lock (_gate)
      {
        foreach (var k in _keys) text = text.Replace(k, Redacted);
      }
      return text;
    }

    public bool Holds(string text)
    {
      if (string.IsNullOrEmpty(text)) return false;
      lock (_gate) return _keys.Any(k => text.IndexOf(k, StringComparison.Ordinal) >= 0);
    }

    /// <summary>Write through the rule, then read back: the file on disk is what the rule is about.</summary>
    public void WriteFile(string path, string text)
    {
      File.WriteAllText(path, Scrub(text), new UTF8Encoding(false));
      if (Holds(File.ReadAllText(path)))
      {
        File.Delete(path);
        throw new InvalidOperationException("A key reached an output file, so the file was removed and the run stopped.");
      }
    }
  }
}
