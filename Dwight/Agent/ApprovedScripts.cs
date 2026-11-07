using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace Dwight.Agent
{
  /// <summary>
  /// ONE SCRIPT THE USER WAS SHOWN, WHOLE, ON A REGION BUILD CARD, AND APPROVED (U65). The plugin-resolved record: the language
  /// is the type's, the source is the normalised text the card displayed, and the ports are the appended names the card listed.
  ///
  /// IT IS THE CONTRACT A LATER build_region IS HELD TO. A region build's spec is written by the model after the click, so the
  /// card cannot be the spec the way a placement's is. What makes the card honest instead is a comparison at the build: a
  /// script in the spec runs only if it equals an approved one for that region, byte for byte after the one normalisation both
  /// sides go through (<see cref="BuildScriptParser"/>). A changed word is a different script and needs its own card.
  /// </summary>
  internal sealed class ApprovedScript
  {
    public string Region { get; set; }

    /// <summary>"python" or "csharp" (<see cref="BuildScriptTypes"/>). Derived from the type, never stored beside it.</summary>
    public string Language { get; set; }

    /// <summary>The complete script, normalised exactly as the build's parse normalises it.</summary>
    public string Source { get; set; }

    public List<string> Inputs { get; set; } = new List<string>();
    public List<string> Outputs { get; set; } = new List<string>();

    /// <summary>Brief CP: each input whose declared access is not item. The card shows them, and the build takes them from the card.</summary>
    public Dictionary<string, string> InputAccess { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>U88: for a C# script, the lines the normaliser put before the model's first line, so a compiler position is given
    /// in the model's numbering. Zero when there is none to give.</summary>
    public int LineShift { get; set; }

    /// <summary>"Python 3 Script" or "C# Script" - the component type this script lives in.</summary>
    public string TypeName => Language == BuildScriptTypes.CSharp ? BuildScriptTypes.CSharpTypeName : BuildScriptTypes.PythonTypeName;

    /// <summary>The language in the user's words ("C#", "Python 3").</summary>
    public string LanguageLabel => BuildScriptTypes.Label(Language);

    /// <summary>Is this the script the spec's component declares, for this region? Source and appended ports must all agree.</summary>
    public bool Matches(string region, BuildComponent c) =>
      c != null && c.CarriesSource
      && string.Equals(Region, region, StringComparison.Ordinal)
      && string.Equals(Language, c.Language, StringComparison.Ordinal)
      && string.Equals(Source, c.Source, StringComparison.Ordinal)
      && (Inputs ?? new List<string>()).SequenceEqual(c.Inputs ?? new List<string>(), StringComparer.Ordinal)
      && (Outputs ?? new List<string>()).SequenceEqual(c.Outputs ?? new List<string>(), StringComparer.Ordinal);

    public JsonObject ToRecord()
    {
      var o = new JsonObject
      {
        ["region"] = Region,
        ["type"] = TypeName,
        ["source"] = Source,
        ["inputs"] = PortAccess.Record(Inputs, InputAccess),
        ["outputs"] = new JsonArray((Outputs ?? new List<string>()).Select(n => (JsonNode)n).ToArray()),
      };
      if (LineShift != 0) o["lineShift"] = LineShift;
      return o;
    }
  }

  /// <summary>
  /// THE ASK'S HALF OF U65: the scripts a region build card carries, validated at the ask, resolved into the record the card
  /// shows and the click grants. GH-free (ADR-091).
  ///
  /// Every script is parsed by the SAME <see cref="BuildScriptParser"/> a build parses with, so what the card shows is the text
  /// the build will compare against (#153: the text shown is the text tested is the text applied) and every refusal is the one
  /// the build would give. A script for a region the card does not approve is refused by name: the card names its scope.
  /// </summary>
  /// <summary>
  /// Brief CA: THE SCRIPTS A CARD SHOWED AND NO BUILD HAS LANDED, per chat and region. An approval's grant dies with its turn, so a later turn had nothing: the model
  /// raised a second card with no script and could not build, or wrote different code. They are held from the card until a build of that region stands, in memory: a
  /// restart loses them, and the model then sends the source again as it always has. Holding one grants nothing. A card has to show it again before anything runs.
  /// </summary>
  internal sealed class ScriptsOnFile
  {
    private readonly Dictionary<string, Dictionary<string, ApprovedScript>> _byThread = new Dictionary<string, Dictionary<string, ApprovedScript>>(StringComparer.Ordinal);
    private readonly object _gate = new object();

    /// <summary>A card showed these. The latest card's script for a region replaces the last.</summary>
    public void Remember(string threadId, IEnumerable<ApprovedScript> scripts)
    {
      lock (_gate)
      {
        if (!_byThread.TryGetValue(threadId ?? "", out var regions)) _byThread[threadId ?? ""] = regions = new Dictionary<string, ApprovedScript>(StringComparer.Ordinal);
        foreach (var s in (scripts ?? new ApprovedScript[0]).Where(s => s != null && !string.IsNullOrEmpty(s.Region))) regions[s.Region] = s;
      }
    }

    /// <summary>A build of this region stands, so the record is the canvas now, and get_component reads it.</summary>
    public void Landed(string threadId, string region)
    {
      lock (_gate)
      {
        if (_byThread.TryGetValue(threadId ?? "", out var regions) && region != null) regions.Remove(region);
      }
    }

    /// <summary>What this chat holds, for these regions or for all, in address order.</summary>
    public List<ApprovedScript> For(string threadId, IEnumerable<string> regions = null)
    {
      lock (_gate)
      {
        if (!_byThread.TryGetValue(threadId ?? "", out var held)) return new List<ApprovedScript>();
        var wanted = regions == null ? null : new HashSet<string>(regions, StringComparer.Ordinal);
        return held.Where(kv => wanted == null || wanted.Contains(kv.Key)).OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Value).ToList();
      }
    }
  }

  internal static class ScriptsAsk
  {
    /// <summary>Scripts one card may carry. Bounded so a card can show every one whole (ADR-035); more is work for a second card.</summary>
    public const int MaxScripts = 10;

    /// <summary>The one refusal for a card with too many scripts, a region card's or a spot's (Brief CV), so the two surfaces cannot disagree.
    /// The key is what the stop line reads.</summary>
    public static ToolResult TooMany(int asked) =>
      ToolResult.Fail(ToolErrors.InvalidArgs,
        "A build card carries at most " + MaxScripts + " scripts, because the user is shown every one whole; this asks for " +
        asked + ". Put the rest on a second card.",
        new JsonObject { ["asked"] = asked, ["mostScripts"] = MaxScripts });

    public static bool Resolve(JsonArray raw, IReadOnlyList<string> approvedRegions,
      out List<ApprovedScript> scripts, out ToolResult error)
    {
      scripts = new List<ApprovedScript>(); error = null;
      if (raw == null || raw.Count == 0) return true;

      if (raw.Count > MaxScripts)
      {
        error = TooMany(raw.Count);
        return false;
      }

      var regions = new HashSet<string>(approvedRegions ?? new string[0], StringComparer.Ordinal);
      for (var i = 0; i < raw.Count; i++)
      {
        var n = i + 1;
        var item = raw[i] as JsonObject;
        if (item == null)
        {
          error = ToolResult.Fail(ToolErrors.InvalidArgs, "Script " + n + " in \"scripts\" must be an object with region, type and source.");
          return false;
        }

        string region, type;
        try { region = (item["region"]?.GetValue<string>() ?? "").Trim(); } catch { region = ""; }
        try { type = (item["type"]?.GetValue<string>() ?? "").Trim(); } catch { type = ""; }

        if (!regions.Contains(region))
        {
          error = ToolResult.Fail(ToolErrors.InvalidArgs,
            "Script " + n + " names region \"" + region + "\", which this card does not approve. A script belongs to one of the " +
            "regions in \"regions\": " + string.Join(", ", regions.OrderBy(r => r, StringComparer.Ordinal)) + ".",
            new JsonObject { ["asked"] = region });
          return false;
        }
        if (BuildScriptTypes.LanguageOf(type) == null)
        {
          error = ToolResult.Fail(ToolErrors.InvalidArgs,
            "Script " + n + " has type \"" + type + "\", which is not a script component. Use " + BuildScriptTypes.Named + ".");
          return false;
        }
        if (item["source"] == null)
        {
          error = ToolResult.Fail(ToolErrors.InvalidArgs,
            "Script " + n + " has no \"source\". Send the COMPLETE script: the user is shown all of it before it runs.");
          return false;
        }

        if (!BuildScriptParser.TryParse(item, type, "script " + n + " in " + region,
              out var source, out var inputs, out var outputs, out var shift, out var access, out error))
          return false;

        scripts.Add(new ApprovedScript
        {
          Region = region, Language = BuildScriptTypes.LanguageOf(type), Source = source, Inputs = inputs, Outputs = outputs, InputAccess = access,
          LineShift = shift,
        });
      }
      return true;
    }

    /// <summary>The resolved record the seed carries to the card and the click.</summary>
    public static JsonArray Record(IEnumerable<ApprovedScript> scripts) =>
      new JsonArray((scripts ?? new ApprovedScript[0]).Select(s => (JsonNode)s.ToRecord()).ToArray());

    /// <summary>The scripts a record holds, SHAPE-checked only: what is not an object with a known type and a source is dropped.
    /// The click and the build compare against these, so a malformed entry can only ever approve nothing.</summary>
    public static List<ApprovedScript> FromRecord(JsonArray record)
    {
      var list = new List<ApprovedScript>();
      if (record == null) return list;
      foreach (var n in record)
      {
        var o = n as JsonObject;
        if (o == null) continue;
        try
        {
          var lang = BuildScriptTypes.LanguageOf(o["type"]?.GetValue<string>());
          var region = o["region"]?.GetValue<string>();
          var source = o["source"]?.GetValue<string>();
          if (lang == null || string.IsNullOrEmpty(region) || string.IsNullOrEmpty(source)) continue;
          PortAccess.ReadRecord(o["inputs"] as JsonArray, out var inputNames, out var inputAccess);
          list.Add(new ApprovedScript
          {
            Region = region, Language = lang, Source = source,
            Inputs = inputNames, InputAccess = inputAccess,
            Outputs = (o["outputs"] as JsonArray)?.Select(x => x.GetValue<string>()).ToList() ?? new List<string>(),
            LineShift = o["lineShift"] is JsonValue ls && ls.TryGetValue<int>(out var shift) ? shift : 0,
          });
        }
        catch { }
      }
      return list;
    }

    /// <summary>
    /// How a card's script covers a spec's component (U73): the card is the contract, so region and language must agree and
    /// the input and output names must agree as SETS; the source and the port ORDER are then the card's, not the model's.
    /// </summary>
    public static bool Covers(ApprovedScript a, string region, BuildComponent c) =>
      a != null && c != null && c.CarriesSource
      && string.Equals(a.Region, region, StringComparison.Ordinal)
      && string.Equals(a.Language, c.Language, StringComparison.Ordinal)
      && SameSet(a.Inputs, c.Inputs) && SameSet(a.Outputs, c.Outputs);

    /// <summary>
    /// U83: a card's script and a build's script that are the same script with RENAMED ports - same region, same language, the
    /// same NUMBER of inputs and of outputs, and the names do not agree as sets (the sets-agree case is <see cref="Covers"/>).
    /// The i-th declared input is then the i-th card input, and likewise for outputs. Counts that differ are not a rename.
    /// </summary>
    public static bool CoversByPosition(ApprovedScript a, string region, BuildComponent c) =>
      a != null && c != null && c.CarriesSource
      && string.Equals(a.Region, region, StringComparison.Ordinal)
      && string.Equals(a.Language, c.Language, StringComparison.Ordinal)
      && (a.Inputs ?? new List<string>()).Count == (c.Inputs ?? new List<string>()).Count
      && (a.Outputs ?? new List<string>()).Count == (c.Outputs ?? new List<string>()).Count
      && !Covers(a, region, c)
      && !HasCaseDuplicates(a.Inputs) && !HasCaseDuplicates(a.Outputs) && !HasCaseDuplicates(c.Inputs) && !HasCaseDuplicates(c.Outputs);

    /// <summary>
    /// U86: a card whose script entry declared NO ports and a build that declares some. The card is still the contract, so the
    /// build's ports are accepted only if the card's SOURCE names every one of them (<see cref="MissingFromSource"/> is empty):
    /// the user read a script that reads and writes exactly those names. Region and language must agree as everywhere else.
    /// </summary>
    public static bool CoversByBuildPorts(ApprovedScript a, string region, BuildComponent c) =>
      a != null && c != null && c.CarriesSource
      && string.Equals(a.Region, region, StringComparison.Ordinal)
      && string.Equals(a.Language, c.Language, StringComparison.Ordinal)
      && (a.Inputs ?? new List<string>()).Count == 0 && (a.Outputs ?? new List<string>()).Count == 0
      && ((c.Inputs ?? new List<string>()).Count + (c.Outputs ?? new List<string>()).Count) > 0
      && MissingFromSource(a, c).Count == 0;

    /// <summary>The build's declared port names that do not occur as an identifier (word boundary, case-sensitive) in the
    /// card's source, in declaration order.</summary>
    public static List<string> MissingFromSource(ApprovedScript a, BuildComponent c) =>
      (c?.Inputs ?? new List<string>()).Concat(c?.Outputs ?? new List<string>())
        .Where(n => !OccursAsIdentifier(a?.Source, n)).Distinct(StringComparer.Ordinal).ToList();

    private static bool OccursAsIdentifier(string source, string name) =>
      !string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(source)
      && System.Text.RegularExpressions.Regex.IsMatch(source, @"(?<![A-Za-z0-9_])" + System.Text.RegularExpressions.Regex.Escape(name) + @"(?![A-Za-z0-9_])");

    private static bool HasCaseDuplicates(IEnumerable<string> names)
    {
      var l = (names ?? new string[0]).ToList();
      return new HashSet<string>(l, StringComparer.OrdinalIgnoreCase).Count != l.Count;
    }

    // U73b: names compare without case (a port called "Count" and one called "count" are the same port to the reader of the
    // card), but a list that holds two names differing only by case is a genuine difference and never equals anything.
    private static bool SameSet(IEnumerable<string> a, IEnumerable<string> b)
    {
      var x = (a ?? new string[0]).ToList();
      var y = (b ?? new string[0]).ToList();
      var xs = new HashSet<string>(x, StringComparer.OrdinalIgnoreCase);
      var ys = new HashSet<string>(y, StringComparer.OrdinalIgnoreCase);
      return xs.Count == x.Count && ys.Count == y.Count && xs.SetEquals(ys);
    }

    /// <summary>
    /// The script components of <paramref name="spec"/> for <paramref name="region"/> that no approved script covers, each with
    /// WHY (the field that differs). Each approved script covers one component at most, so two identical scripts need two
    /// approvals. An exact match is claimed before a relaxed one, so a card of two scripts pairs them the way the model meant.
    /// </summary>
    public static List<UncoveredScript> Uncovered(BuildSpec spec, string region, IReadOnlyList<ApprovedScript> approved) =>
      Pair(spec, region, approved, out _);

    /// <summary>The same walk, also returning which approved script covers which component ref.</summary>
    public static List<UncoveredScript> Pair(BuildSpec spec, string region, IReadOnlyList<ApprovedScript> approved,
      out Dictionary<string, ApprovedScript> covering)
    {
      covering = new Dictionary<string, ApprovedScript>(StringComparer.Ordinal);
      var all = (approved ?? new ApprovedScript[0]).ToList();
      var pool = all.ToList();
      var comps = (spec?.Components ?? new List<BuildComponent>()).Where(c => c != null && c.CarriesSource).ToList();
      var open = new List<BuildComponent>();

      foreach (var c in comps)   // pass 1: byte-for-byte, as before
      {
        var hit = pool.FirstOrDefault(a => a.Matches(region, c));
        if (hit == null) { open.Add(c); continue; }
        covering[c.Ref] = hit; pool.Remove(hit);
      }

      var missing = new List<UncoveredScript>();
      var stillOpen = new List<BuildComponent>();
      foreach (var c in open)    // pass 2: the card's ports as sets; the card's text wins
      {
        var hit = pool.FirstOrDefault(a => Covers(a, region, c));
        if (hit != null) { covering[c.Ref] = hit; pool.Remove(hit); continue; }
        stillOpen.Add(c);
      }
      var afterTaking = new List<BuildComponent>();
      foreach (var c in stillOpen)   // pass 2b (U86): the card listed no ports, the build declares some its source names; forced pairing only
      {
        var taking = pool.Where(a => CoversByBuildPorts(a, region, c)).ToList();
        if (taking.Count == 1 && stillOpen.Count(o => CoversByBuildPorts(taking[0], region, o)) == 1)
        { covering[c.Ref] = taking[0]; pool.Remove(taking[0]); continue; }
        afterTaking.Add(c);
      }
      stillOpen = afterTaking;
      foreach (var c in stillOpen)   // pass 3 (U83): the same script with renamed ports, only where the pairing is forced
      {
        var cands = pool.Where(a => CoversByPosition(a, region, c)).ToList();
        if (cands.Count == 1 && stillOpen.Count(o => CoversByPosition(cands[0], region, o)) == 1)
        { covering[c.Ref] = cands[0]; pool.Remove(cands[0]); continue; }
        missing.Add(Why(c, region, all, pool));
      }
      return missing;
    }

    private static UncoveredScript Why(BuildComponent c, string region, List<ApprovedScript> all, List<ApprovedScript> pool)
    {
      var u = new UncoveredScript { Ref = c.Ref };
      var inRegion = all.Where(a => string.Equals(a.Region, region, StringComparison.Ordinal)).ToList();
      if (inRegion.Count == 0)
      {
        u.Field = "region";
        u.Why = "no script was shown for region " + region + " on the card";
        return u;
      }
      var sameLang = inRegion.Where(a => string.Equals(a.Language, c.Language, StringComparison.Ordinal)).ToList();
      if (sameLang.Count == 0)
      {
        u.Field = "language";
        u.Why = "the card's script for " + region + " is " + string.Join(" / ", inRegion.Select(a => a.LanguageLabel).Distinct()) +
                ", this build's script \"" + c.Ref + "\" is " + BuildScriptTypes.Label(c.Language);
        u.Approved = inRegion[0];
        return u;
      }
      // prefer one not already spent on another component, so the sentence names the script this one was meant to be
      var near = sameLang.FirstOrDefault(a => pool.Contains(a)) ?? sameLang[0];
      u.Approved = near;
      if ((near.Inputs ?? new List<string>()).Count == 0 && (near.Outputs ?? new List<string>()).Count == 0
          && ((c.Inputs ?? new List<string>()).Count + (c.Outputs ?? new List<string>()).Count) > 0
          && MissingFromSource(near, c).Count > 0)
      {
        // U86: the card listed no ports, so the build's would be taken from its source - and these are not in it.
        u.Field = "ports";
        u.Why = "the card's script for " + region + " declares no ports, and a build's ports are accepted only when the card's source names " +
                "them; the source does not name " + string.Join(", ", MissingFromSource(near, c)) + " (this build declares inputs " + Names(c.Inputs) +
                " and outputs " + Names(c.Outputs) + ")";
      }
      else if (!SameSet(near.Inputs, c.Inputs))
      {
        u.Field = "inputs";
        u.Why = "the card's script for " + region + " declares inputs " + Names(near.Inputs) + "; this build declares " + Names(c.Inputs);
      }
      else if (!SameSet(near.Outputs, c.Outputs))
      {
        u.Field = "outputs";
        u.Why = "the card's script for " + region + " declares outputs " + Names(near.Outputs) + "; this build declares " + Names(c.Outputs);
      }
      else
      {
        u.Field = "count";
        u.Why = "the card showed " + sameLang.Count + " " + near.LanguageLabel + " script" + (sameLang.Count == 1 ? "" : "s") +
                " for " + region + " and each runs once; this build sends more than that";
      }
      return u;
    }

    private static string Names(IEnumerable<string> names)
    {
      var l = (names ?? new string[0]).ToList();
      return l.Count == 0 ? "(none)" : string.Join(", ", l);
    }

    /// <summary>The first line at which two sources differ: its 1-based number and both lines, each cut to 120 characters.
    /// Line endings are ignored (the build's normalisation already settled them). False when the texts are the same.</summary>
    public static bool FirstDifference(string sent, string card, out int line, out string sentLine, out string cardLine)
    {
      var a = (sent ?? "").Replace("\r\n", "\n").Split('\n');
      var b = (card ?? "").Replace("\r\n", "\n").Split('\n');
      line = 0; sentLine = cardLine = "";
      for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
      {
        if (i < a.Length && i < b.Length && string.Equals(a[i], b[i], StringComparison.Ordinal)) continue;
        line = i + 1;
        sentLine = Cut(i < a.Length ? a[i] : "");
        cardLine = Cut(i < b.Length ? b[i] : "");
        return true;
      }
      return false;
    }

    private static string Cut(string s) => s.Length <= 120 ? s : s.Substring(0, 120);
  }

  /// <summary>A script component in a region build that no approved script covers, and the field that says why (U73).</summary>
  internal sealed class UncoveredScript
  {
    public string Ref { get; set; }

    /// <summary>"region", "language", "inputs", "outputs", "ports" (U86) or "count".</summary>
    public string Field { get; set; }
    public string Why { get; set; }

    /// <summary>The card's nearest script for this region, or null when the card showed none for it.</summary>
    public ApprovedScript Approved { get; set; }
  }

  /// <summary>A script the build took from the card rather than from the model's spec (U73).</summary>
  internal sealed class ScriptSubstitution
  {
    public string Ref { get; set; }
    public bool SourceDiffered { get; set; }
    public bool PortOrderDiffered { get; set; }

    /// <summary>U83: the build named its ports differently from the card (same counts), so they were taken from the card and the
    /// build's wires moved onto them by position. Each pair is (the build's name, the card's name).</summary>
    public List<KeyValuePair<string, string>> PortsMappedByPosition { get; set; } = new List<KeyValuePair<string, string>>();

    /// <summary>U86: the card listed no ports, so the build's declared ports were built (its source names every one).</summary>
    public bool PortsTakenFromBuild { get; set; }

    /// <summary>U89: said before anything else about a substituted source, because a model that sent a corrected script and reads
    /// an error from the old one concludes the tool is broken (Serpentine, 27 October).</summary>
    public const string NotBuiltNotice =
      "Your sent source was NOT built. The source on the card the user approved was built instead. To run changed source, raise a new card with it.";

    /// <summary>U86's sentence, exactly as the verdict carries it.</summary>
    public const string PortsTakenSentence =
      "Ports taken from the build's declaration; the card listed none and its source names them.";

    /// <summary>The whole note for a set of substitutions: the U89 notice once, ahead of the per-script sentences, when any source
    /// differed. Null when there is nothing to say.</summary>
    public static string NoteFor(IEnumerable<ScriptSubstitution> subs)
    {
      var list = (subs ?? new ScriptSubstitution[0]).Where(s => s != null).ToList();
      if (list.Count == 0) return null;
      var sentences = string.Join(" ", list.Select(x => x.Sentence()));
      return list.Any(s => s.SourceDiffered) ? NotBuiltNotice + " " + sentences : sentences;
    }
    public int FirstDifferenceLine { get; set; }
    public string SentLine { get; set; }
    public string CardLine { get; set; }

    public JsonObject ToJson()
    {
      var o = new JsonObject { ["ref"] = Ref };
      if (SourceDiffered)
      {
        o["sourceTakenFromCard"] = true;
        o["firstDifferenceLine"] = FirstDifferenceLine;
        o["sentLine"] = SentLine;
        o["cardLine"] = CardLine;
      }
      if (PortOrderDiffered) o["portOrderTakenFromCard"] = true;
      if (PortsTakenFromBuild) o["portsTakenFromBuild"] = true;
      if (PortsMappedByPosition.Count > 0)
      {
        var m = new JsonObject();
        foreach (var kv in PortsMappedByPosition) m[kv.Key] = kv.Value;
        o["portsMappedByPosition"] = m;
      }
      return o;
    }

    /// <summary>The sentence the model reads: what was built, and where its text differed.</summary>
    public string Sentence() =>
      PortsTakenFromBuild
        ? (SourceDiffered
            ? "Script \"" + Ref + "\" was built from the source on the card, which is the text the user approved. Your text first differed " +
              "at line " + FirstDifferenceLine + ": you sent \"" + SentLine + "\", the card has \"" + CardLine + "\". "
            : "Script \"" + Ref + "\": ") + PortsTakenSentence
      : PortsMappedByPosition.Count > 0
        ? "Script \"" + Ref + "\": ports taken from the card; your names were mapped by position (" +
          string.Join(", ", PortsMappedByPosition.Select(kv => kv.Key + " as " + kv.Value)) + "), and the source is the card's, which uses the card's names."
      : SourceDiffered
        ? "Script \"" + Ref + "\" was built from the source on the card, which is the text the user approved. Your text first differed " +
          "at line " + FirstDifferenceLine + ": you sent \"" + SentLine + "\", the card has \"" + CardLine + "\"."
        : "Script \"" + Ref + "\" was built with the port names, order and access on the card.";
  }
}
