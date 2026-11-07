using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using Dwight.Components;

namespace Dwight.Agent
{
  /// <summary>One installed component type, GH-free: what <c>ComponentWhitelist</c>'s proxy distils to.</summary>
  internal sealed class InstalledType
  {
    public string Name { get; set; }
    public string Category { get; set; }
    public string SubCategory { get; set; }
    public string Description { get; set; }
    public Guid Guid { get; set; }
  }

  /// <summary>
  /// U25 (#915), the reads' half: a bare component name can belong to MORE THAN ONE installed type. "Rectangle" is a
  /// param (Params &gt; Geometry) and a component (Curve &gt; Primitive); so are Line and Circle. The whitelist's
  /// name index keeps whichever proxy Grasshopper enumerated first, so a build of "Rectangle" placed the param, and
  /// the model - with no spelling that reached the component - told the user *"there is no plain Plane+X+Y rectangle
  /// type installed under that name"*: a false statement about the user's machine, manufactured by resolution.
  ///
  /// The qualified form is <c>Category &gt; SubCategory &gt; Name</c>. Lane A's build side REFUSES an ambiguous bare
  /// name and accepts the qualified one; this half makes the reads PRESENT it - <c>search</c> and <c>get_component</c>
  /// carry it on every component whose bare name is ambiguous, and <c>kb_lookup</c> takes it, returns it, and on an
  /// ambiguous bare name returns every candidate with its qualified form and says so. The separator is the one thing
  /// the two halves agreed on: <c>&gt;</c>; parsing tolerates any spacing around it.
  /// </summary>
  internal static class QualifiedNames
  {
    public const string Separator = " > ";

    public static string Format(string category, string subCategory, string name) =>
      (category ?? "").Trim() + Separator + (subCategory ?? "").Trim() + Separator + (name ?? "").Trim();

    public static string Format(InstalledType t) => t == null ? null : Format(t.Category, t.SubCategory, t.Name);

    /// <summary>Three non-empty parts split on <c>&gt;</c>, in any spacing. Anything else is a bare name.</summary>
    public static bool TryParse(string text, out string category, out string subCategory, out string name)
    {
      category = subCategory = name = null;
      if (string.IsNullOrWhiteSpace(text) || text.IndexOf('>') < 0) return false;
      var parts = text.Split('>').Select(p => p.Trim()).ToArray();
      if (parts.Length != 3 || parts.Any(string.IsNullOrEmpty)) return false;
      category = parts[0]; subCategory = parts[1]; name = parts[2];
      return true;
    }

    /// <summary>The bare names (case-insensitive) that more than one DISTINCT qualified form shares. Two proxies
    /// with the same name in the same category and sub-category are not ambiguous - the qualified form could not
    /// tell them apart either, and the whitelist's first-wins rule is the only one there is.</summary>
    public static HashSet<string> AmbiguousNames(IEnumerable<InstalledType> installed)
    {
      var forms = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
      foreach (var t in installed ?? Enumerable.Empty<InstalledType>())
      {
        if (t == null || string.IsNullOrWhiteSpace(t.Name)) continue;
        var key = t.Name.Trim();
        if (!forms.TryGetValue(key, out var set)) forms[key] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        set.Add(Format(t));
      }
      return new HashSet<string>(forms.Where(kv => kv.Value.Count > 1).Select(kv => kv.Key), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The qualified form for a type whose bare name is ambiguous; null otherwise - an unambiguous name needs
    /// no qualification, and a field that is always present is a field nobody reads.</summary>
    public static string QualifiedIfAmbiguous(InstalledType t, HashSet<string> ambiguous) =>
      t != null && ambiguous != null && !string.IsNullOrWhiteSpace(t.Name) && ambiguous.Contains(t.Name.Trim()) ? Format(t) : null;

    public enum Outcome { None, One, Ambiguous }

    public sealed class Resolution
    {
      public Outcome Outcome { get; set; }
      /// <summary>The argument was in qualified form.</summary>
      public bool WasQualified { get; set; }
      /// <summary>One for <see cref="Outcome.One"/>; every candidate, ordered by qualified form, for Ambiguous.</summary>
      public List<InstalledType> Matches { get; set; } = new List<InstalledType>();
    }

    /// <summary>
    /// Resolve a <c>type</c> argument against the installed set. Qualified: all three parts must match (the name by the
    /// whitelist's own normalisation, the other two case-insensitively). Bare: exact name first, then the normalised
    /// name - the same two steps <c>ComponentWhitelist.Lookup</c> takes - and the DISTINCT qualified forms among the
    /// hits decide: one is One; more is Ambiguous; none is None.
    /// </summary>
    public static Resolution Resolve(string typeArg, IReadOnlyList<InstalledType> installed)
    {
      var r = new Resolution();
      var all = installed ?? new List<InstalledType>();
      if (string.IsNullOrWhiteSpace(typeArg)) return r;

      if (TryParse(typeArg, out var cat, out var sub, out var name))
      {
        r.WasQualified = true;
        var norm = ComponentMatchCore.Normalize(name);
        var hits = all.Where(t => t != null
                                  && string.Equals((t.Category ?? "").Trim(), cat, StringComparison.OrdinalIgnoreCase)
                                  && string.Equals((t.SubCategory ?? "").Trim(), sub, StringComparison.OrdinalIgnoreCase)
                                  && (string.Equals((t.Name ?? "").Trim(), name, StringComparison.OrdinalIgnoreCase)
                                      || ComponentMatchCore.Normalize(t.Name ?? "") == norm)).ToList();
        if (hits.Count == 0) return r;
        r.Outcome = Outcome.One;          // the qualified form IS the disambiguation; a duplicate of it is first-wins
        r.Matches.Add(hits[0]);
        return r;
      }

      var bare = typeArg.Trim();
      var exact = all.Where(t => t != null && string.Equals((t.Name ?? "").Trim(), bare, StringComparison.OrdinalIgnoreCase)).ToList();
      if (exact.Count == 0)
      {
        var n = ComponentMatchCore.Normalize(bare);
        if (n.Length > 0) exact = all.Where(t => t != null && ComponentMatchCore.Normalize(t.Name ?? "") == n).ToList();
      }
      if (exact.Count == 0) return r;

      var byForm = exact.GroupBy(Format, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
                        .OrderBy(Format, StringComparer.OrdinalIgnoreCase).ToList();
      r.Outcome = byForm.Count == 1 ? Outcome.One : Outcome.Ambiguous;
      r.Matches.AddRange(byForm);
      return r;
    }
  }

  /// <summary>One object sitting on the canvas, GH-free: what <c>kb_lookup</c> may read ports from.</summary>
  internal sealed class LiveObject
  {
    public Guid Guid { get; set; }
    public string Name { get; set; }
    /// <summary>A stand-alone param (Rectangle the PARAM): one input, one output, no component ports.</summary>
    public bool IsParam { get; set; }
    public IReadOnlyList<string> Inputs { get; set; }
    public IReadOnlyList<string> Outputs { get; set; }
  }

  /// <summary>
  /// U39 (#975): WHICH LIVE INSTANCE ANSWERS FOR WHICH CANDIDATE. On the ambiguous "Rectangle" both candidates came
  /// back with the component's ports (inputs P, X, Y, R) and "readFrom an instance on this canvas" - the param's
  /// candidate was handed the component's ports because the only live instance was found by NAME. A name is shared
  /// precisely when this question is hard, so the name is not an identity: a candidate reads ports from an instance of
  /// ITS OWN ComponentGuid, a param instance answers with its own single in and out, and a candidate with no instance
  /// of its guid is "unavailable" - stated, never borrowed (#208).
  /// </summary>
  internal static class LivePorts
  {
    public const string ReadFrom = "an instance on this canvas";

    /// <summary>Brief CI: where an answer's ports came from, as the record and the model both read it.</summary>
    public const string FromCanvas = "canvas";
    public const string FromInstantiated = "instantiated";
    public const string FromUnavailable = "unavailable";

    /// <summary>Brief CI: <see cref="ReadFrom"/>'s sentence for a type with no instance on the canvas.</summary>
    public const string ReadFromTemporary = "a temporary instance of the installed type, never added to this definition";

    /// <summary>Brief CI: the same shape as an instance on the canvas gives, with its own <c>readFrom</c>.</summary>
    public static JsonObject Instantiated(IEnumerable<string> inputs, IEnumerable<string> outputs) => new JsonObject
    {
      ["inputs"] = new JsonArray((inputs ?? new string[0]).Select(n => (JsonNode)n).ToArray()),
      ["outputs"] = new JsonArray((outputs ?? new string[0]).Select(n => (JsonNode)n).ToArray()),
      ["readFrom"] = ReadFromTemporary,
    };

    /// <param name="nameSharedByInstalledTypes">Another installed type carries this candidate's bare name, so a
    /// name match could be the other one.</param>
    public static JsonObject Pick(IEnumerable<LiveObject> live, InstalledType info, bool nameSharedByInstalledTypes)
    {
      if (info == null || live == null) return null;
      LiveObject byName = null;
      var nameShared = nameSharedByInstalledTypes;
      foreach (var o in live)
      {
        if (o == null) continue;
        if (o.Guid == info.Guid) return Of(o);       // its own instance: the only match that tells param from component
        if (o.IsParam) continue;                      // a param is never matched by name: it has no ports of a component's
        if (string.Equals(o.Name ?? "", info.Name ?? "", StringComparison.OrdinalIgnoreCase))
        {
          if (byName != null && byName.Guid != o.Guid) nameShared = true;
          byName = byName ?? o;
        }
      }
      return byName != null && !nameShared ? Of(byName) : null;
    }

    /// <summary>
    /// U62: A PORT AS THE LOOKUP SPEAKS IT - "Radius (R)" where the nickname differs from the name, the bare name where
    /// they agree. The canvas labels a port by nickname and the model used to be handed only that, so it read "R" and "D3"
    /// and guessed at the rest (the tower's five refusals were all port names the model guessed). A build accepts either form
    /// (U41) and the combined spelling itself (<c>BuildSpecParser.Port</c>), so what is shown here is always sayable.
    /// A blank on one side leaves the other; both blank is ".", the label the graph uses for a port with neither.
    /// </summary>
    public static string PortLabel(string name, string nick)
    {
      var n = (name ?? "").Trim();
      var k = (nick ?? "").Trim();
      if (n.Length == 0) return k.Length == 0 ? "." : k;
      if (k.Length == 0 || string.Equals(n, k, StringComparison.OrdinalIgnoreCase)) return n;
      return n + " (" + k + ")";
    }

    private static JsonObject Of(LiveObject o) => new JsonObject
    {
      ["inputs"] = new JsonArray((o.Inputs ?? new string[0]).Select(n => (JsonNode)n).ToArray()),
      ["outputs"] = new JsonArray((o.Outputs ?? new string[0]).Select(n => (JsonNode)n).ToArray()),
      ["readFrom"] = ReadFrom,
    };
  }

  /// <summary>
  /// The <c>kb_lookup</c> payload, GH-free so its shape is asserted (ADR-091) and its row in the privacy contract is
  /// held to the writer's real output rather than read off the shim. Ports are handed in as a function, because
  /// reading them needs a live instance, on the canvas or made for the purpose (<see cref="KbPortsRun"/>; the ADR-068
  /// honesty: never a fabricated schema).
  /// </summary>
  internal static class KbLookupReport
  {
    public const string AmbiguousMeans =
      "the name you asked for belongs to more than one installed component, so a bare \"{0}\" is ambiguous: each " +
      "candidate is listed with its qualified form Category > SubCategory > Name. Use the qualified form wherever a " +
      "type is asked for - in kb_lookup, in search's type, and in build_region - so the right one is placed.";

    /// <summary>U58: the most names one kb_lookup call may carry.</summary>
    public const int MaxTypes = 25;

    /// <summary>
    /// U58: the whole kb_lookup call, GH-free so the published descriptor can be tested against it (#217).
    /// Exactly one of <c>type</c> (the single lookup, unchanged) or <c>types</c> (up to 25 names, one row
    /// each in the order given, each row what a single lookup returns) is required.
    /// </summary>
    public static ToolResult Run(JsonObject args, IReadOnlyList<InstalledType> installed,
      Func<string, IReadOnlyList<string>> nearestOf, Func<InstalledType, JsonObject> portsOf)
    {
      var type = args?["type"]?.GetValue<string>()?.Trim();
      var names = new List<string>();
      var typesNode = args?["types"];
      if (typesNode != null)
      {
        if (!(typesNode is JsonArray arr))
          return ToolResult.Fail(ToolErrors.InvalidArgs, "\"types\" must be an array of component type names.");
        foreach (var n in arr)
        {
          string name;
          try { name = n?.GetValue<string>()?.Trim(); }
          catch (InvalidOperationException) { name = null; }
          if (string.IsNullOrWhiteSpace(name))
            return ToolResult.Fail(ToolErrors.InvalidArgs, "Every entry in \"types\" must be a component type name, bare or qualified as Category > SubCategory > Name.");
          names.Add(name);
        }
      }
      var hasType = !string.IsNullOrWhiteSpace(type);
      var hasTypes = names.Count > 0;

      if (hasType == hasTypes)
        return ToolResult.Fail(ToolErrors.InvalidArgs, hasType
          ? "Send either \"type\" (one name) or \"types\" (a list of names), not both."
          : "Requires \"type\" (one component type name) or \"types\" (a list of up to " + MaxTypes + " names), bare or qualified as Category > SubCategory > Name.");
      if (names.Count > MaxTypes)
        return ToolResult.Fail(ToolErrors.InvalidArgs, "\"types\" holds at most " + MaxTypes + " names; " + names.Count + " were sent. Split them across calls.");

      ToolResult One(string asked)
      {
        var resolution = QualifiedNames.Resolve(asked, installed);
        var nearest = resolution.Outcome == QualifiedNames.Outcome.None ? nearestOf?.Invoke(asked) : null;
        return Build(asked, resolution, nearest, portsOf);
      }

      if (hasType) return One(type);

      var rows = new JsonArray();
      foreach (var asked in names)
      {
        var r = One(asked);
        if (r.Ok)
        {
          rows.Add(JsonNode.Parse(r.Result.ToJsonString()));
          continue;
        }
        var row = new JsonObject { ["asked"] = asked, ["found"] = false, ["code"] = r.Code, ["message"] = r.Message };
        if (r.Detail != null)
          foreach (var kv in r.Detail)
            if (row[kv.Key] == null) row[kv.Key] = kv.Value == null ? null : JsonNode.Parse(kv.Value.ToJsonString());
        rows.Add(row);
      }
      return ToolResult.Success(new JsonObject { ["results"] = rows });
    }

    public static ToolResult Build(string asked, QualifiedNames.Resolution r, IReadOnlyList<string> nearest,
      Func<InstalledType, JsonObject> portsOf)
    {
      if (r == null || r.Outcome == QualifiedNames.Outcome.None)
      {
        var message = r != null && r.WasQualified
          ? $"No installed component has the qualified name \"{asked}\" - check the category and sub-category against kb_lookup's answer for the bare name."
          : $"No installed component is named \"{asked}\".";
        return ToolResult.Fail(ToolErrors.UnknownComponentType, message, new JsonObject
        {
          ["asked"] = asked,
          ["nearestInstalled"] = new JsonArray((nearest ?? new List<string>()).Select(n => (JsonNode)n).ToArray()),
        });
      }

      if (r.Outcome == QualifiedNames.Outcome.One)
      {
        var info = r.Matches[0];
        var result = new JsonObject
        {
          ["name"] = info.Name,
          ["qualified"] = QualifiedNames.Format(info),
          ["category"] = info.Category,
          ["subCategory"] = info.SubCategory,
          ["does"] = info.Description,
          ["installed"] = true,
        };
        PutPorts(result, portsOf?.Invoke(info));
        return ToolResult.Success(result);
      }

      var candidates = new JsonArray();
      foreach (var info in r.Matches)
      {
        var c = new JsonObject
        {
          ["qualified"] = QualifiedNames.Format(info),
          ["name"] = info.Name,
          ["category"] = info.Category,
          ["subCategory"] = info.SubCategory,
          ["does"] = info.Description,
          ["installed"] = true,
        };
        PutPorts(c, portsOf?.Invoke(info));
        candidates.Add(c);
      }
      return ToolResult.Success(new JsonObject
      {
        ["asked"] = asked,
        ["ambiguous"] = true,
        ["candidates"] = candidates,
        ["means"] = string.Format(AmbiguousMeans, asked),
      });
    }

    /// <summary>Brief CI: one place says what a row's ports are and where they came from. No port list is the old "unavailable",
    /// stated and never guessed, and a lookup that tried and could not carries its sentence (<see cref="KbPortsRun.CouldNot"/>).</summary>
    private static void PutPorts(JsonObject row, JsonObject ports)
    {
      if (ports?["inputs"] == null)
      {
        row["ports"] = "unavailable";
        row["portsFrom"] = LivePorts.FromUnavailable;
        var note = ports?[KbPortsRun.NoteKey]?.GetValue<string>();
        if (note != null) row["portsNote"] = note;
        return;
      }
      var temporary = string.Equals(ports["readFrom"]?.GetValue<string>(), LivePorts.ReadFromTemporary, StringComparison.Ordinal);
      row["ports"] = ports;
      row["portsFrom"] = temporary ? LivePorts.FromInstantiated : LivePorts.FromCanvas;
    }
  }

  /// <summary>
  /// Brief CI: ONE kb_lookup CALL THAT MAY HAVE TO INSTANTIATE. ADR-068 left a type with no instance on the canvas as "unavailable", because reading every
  /// proxy is slow and can fault. On Honeybee, Ladybug and Dragonfly that hole cost turns: the model stated port names from memory and met the arity
  /// resolver's refusals. So the one proxy asked about is instantiated on demand, read, and dropped. Nothing is invented: the installed type describes itself.
  ///
  /// THE CLOCK CANNOT INTERRUPT (U48; <c>AgentLoop.ExecuteRunScript</c>'s law). An instance is made on the UI thread, where a read tool already runs, and nothing can
  /// stop that call, or a dialog it shows. So the clock is a timer on another thread: when it runs out it answers the caller, and a late return is thrown away.
  /// One step runs per posted callback, so the UI thread breathes between them.
  ///
  /// GH-free so all of this is tested: <c>GhKbPorts</c> hands in the one call that touches Grasshopper. Holds nothing past the call (the brief: a stale cache is a
  /// new class of defect). The first pass asks every name once, so a refusal or a canvas answer never makes an instance.
  /// </summary>
  internal sealed class KbPortsRun
  {
    /// <summary>The one sentence an answer carries when ports were asked for and could not be read, whichever way it failed.</summary>
    public const string CouldNotInstantiate =
      "Dwight could not instantiate this component here to read its ports, so do not state them from memory: a build that names a wrong port is refused with the real list.";

    public const string NoteKey = "note";

    public const int ClockBaseMs = 5000;
    public const int ClockStepMs = 250;
    public const int ClockCeilingMs = 15000;

    /// <summary>A few seconds for one instance, a little more for each further one in a <c>types</c> list, never past the ceiling.</summary>
    public static int ClockMs(int instances) => Math.Min(ClockCeilingMs, ClockBaseMs + ClockStepMs * Math.Max(0, instances - 1));

    /// <summary>What <see cref="KbLookupReport"/> reads as "tried, and could not".</summary>
    public static JsonObject CouldNot() => new JsonObject { [NoteKey] = CouldNotInstantiate };

    /// <summary>A timer on its own thread, which can fire while the UI thread is stuck in the instance.</summary>
    public static IDisposable RealClock(Action fire, int ms) =>
      new Timer(_ => { try { fire(); } catch { } }, null, ms, Timeout.Infinite);

    private readonly JsonObject _args;
    private readonly IReadOnlyList<InstalledType> _installed;
    private readonly Func<string, IReadOnlyList<string>> _nearestOf;
    private readonly Func<InstalledType, JsonObject> _canvasPortsOf;
    private readonly Func<InstalledType, JsonObject> _instantiate;
    private readonly Action<Action> _post;
    private readonly Action<ToolResult> _finish;
    private readonly Action<string> _finding;

    private readonly object _gate = new object();
    // As text, so each row of a list gets its own node: a JsonNode has one parent.
    private readonly Dictionary<Guid, string> _answers = new Dictionary<Guid, string>();
    private readonly Dictionary<string, IReadOnlyList<string>> _nearest = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
    private readonly HashSet<Guid> _seen = new HashSet<Guid>();
    private readonly Queue<InstalledType> _queue = new Queue<InstalledType>();
    private InstalledType _inFlight;
    private int _clockMs;
    private IDisposable _clock;
    private bool _finished;

    private KbPortsRun(JsonObject args, IReadOnlyList<InstalledType> installed, Func<string, IReadOnlyList<string>> nearestOf,
      Func<InstalledType, JsonObject> canvasPortsOf, Func<InstalledType, JsonObject> instantiate, Action<Action> post,
      Action<ToolResult> finish, Action<string> finding)
    {
      _args = args;
      _installed = installed;
      _nearestOf = nearestOf;
      _canvasPortsOf = canvasPortsOf;
      _instantiate = instantiate;
      _post = post;
      _finish = finish;
      _finding = finding;
    }

    /// <param name="canvasPortsOf">Ports from an instance already on the canvas, or null.</param>
    /// <param name="instantiate">Ports from a temporary instance. Null or a throw is a failure, stated and never guessed.</param>
    /// <param name="post">Runs a step later on the thread that may touch Grasshopper.</param>
    /// <param name="startClock">Starts the clock, with what to call when it runs out.</param>
    /// <param name="finish">Called exactly once, on every path.</param>
    /// <param name="finding">Records by name what could not be instantiated. It never reaches the model, so an exception's own words stay here.</param>
    public static void Start(JsonObject args, IReadOnlyList<InstalledType> installed, Func<string, IReadOnlyList<string>> nearestOf,
      Func<InstalledType, JsonObject> canvasPortsOf, Func<InstalledType, JsonObject> instantiate, Action<Action> post,
      Func<Action, int, IDisposable> startClock, Action<ToolResult> finish, Action<string> finding = null)
    {
      new KbPortsRun(args, installed, nearestOf, canvasPortsOf, instantiate, post, finish, finding).Begin(startClock);
    }

    private void Begin(Func<Action, int, IDisposable> startClock)
    {
      try
      {
        var first = KbLookupReport.Run(_args, _installed, FirstNearest, FirstPass);
        if (!first.Ok || _queue.Count == 0) { Complete(); return; }

        lock (_gate)
        {
          _clockMs = ClockMs(_queue.Count);
          _clock = startClock(OnClock, _clockMs);
        }
        Post();
      }
      catch { Complete(); }
    }

    private IReadOnlyList<string> FirstNearest(string asked)
    {
      var found = _nearestOf?.Invoke(asked);
      _nearest[asked] = found;
      return found;
    }

    private JsonObject FirstPass(InstalledType info)
    {
      if (info == null || !_seen.Add(info.Guid)) return null;
      JsonObject live = null;
      try { live = _canvasPortsOf?.Invoke(info); } catch { }
      if (live != null) _answers[info.Guid] = live.ToJsonString();
      else _queue.Enqueue(info);
      return null;
    }

    private void Post()
    {
      try { _post(Step); }
      catch { Complete(); }
    }

    private void Step()
    {
      InstalledType info;
      lock (_gate)
      {
        if (_finished || _queue.Count == 0) return;
        info = _queue.Dequeue();
        _inFlight = info;
      }

      JsonObject read = null;
      string failure = null;
      try
      {
        read = _instantiate(info);
        if (read == null) failure = "the installed type gave no instance";
      }
      catch (Exception ex) { failure = ex.GetType().Name + ": " + ex.Message; }

      bool late, more = false;
      lock (_gate)
      {
        late = _finished;
        if (!late)
        {
          _answers[info.Guid] = (read ?? CouldNot()).ToJsonString();
          _inFlight = null;
          more = _queue.Count > 0;
        }
      }

      if (failure != null) Find(info.Name + ": " + failure);
      if (late) { Find(info.Name + " came back after the clock had run out, and that instance was dropped."); return; }
      if (more) Post();
      else Complete();
    }

    private void OnClock()
    {
      var names = new List<string>();
      lock (_gate)
      {
        if (_finished) return;
        if (_inFlight != null) names.Add(_inFlight.Name);
        names.AddRange(_queue.Select(i => i.Name));
      }
      Find("no answer within " + _clockMs + " ms: " + string.Join(", ", names));
      Complete();
    }

    private void Complete()
    {
      lock (_gate)
      {
        if (_finished) return;
        _finished = true;
        try { _clock?.Dispose(); } catch { }

        ToolResult result;
        try { result = KbLookupReport.Run(_args, _installed, Nearest, Answer); }
        catch (Exception ex) { result = ToolResult.Fail(ToolErrors.PreconditionRefused, "The tool could not complete (" + ex.Message + ")."); }
        try { _finish?.Invoke(result); } catch { }
      }
    }

    private IReadOnlyList<string> Nearest(string asked) => _nearest.TryGetValue(asked, out var found) ? found : null;

    // No entry means the clock ran out before this one was reached: the same sentence, because the same is true of it.
    private JsonObject Answer(InstalledType info) =>
      info != null && _answers.TryGetValue(info.Guid, out var json) ? (JsonObject)JsonNode.Parse(json) : CouldNot();

    private void Find(string line)
    {
      try { _finding?.Invoke(line); } catch { }
    }
  }
}
