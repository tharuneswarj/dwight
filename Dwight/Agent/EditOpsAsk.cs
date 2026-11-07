using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Dwight.Data;
using Dwight.Simplify;

namespace Dwight.Agent
{
  /// <summary>The three things a consented edit can do to what is already on the canvas (Phase-9 U6 + U21, ruled at #760).</summary>
  internal enum EditOpKind
  {
    /// <summary>Add one wire from an output to an input. APPENDS a source (an input that already has one keeps it).</summary>
    Wire,

    /// <summary>Remove one existing wire.</summary>
    Unwire,

    /// <summary>Delete one existing component or parameter - and, with it, every wire on it.</summary>
    Delete,
  }

  /// <summary>
  /// One port as the live canvas reports it - GH-free, so every rule below is asserted by a test rather than reviewed
  /// (ADR-091). An INPUT port carries the guids of the output ports wired into it, in order (order is behaviour, WP-1.8).
  /// </summary>
  internal sealed class EditPort
  {
    public string Name { get; set; }
    public string Nick { get; set; }
    public Guid Guid { get; set; }
    public List<Guid> Sources { get; set; } = new List<Guid>();

    /// <summary>What the canvas calls it: the nickname where there is one, else the name (DwightGraph.ParamLabel's rule).</summary>
    public string Label => !string.IsNullOrWhiteSpace(Nick) ? Nick : (Name ?? ".");
  }

  /// <summary>
  /// One component or parameter, as the edit validator sees it. A standalone PARAMETER (a slider, a panel, a
  /// relay) has one implicit port that is both its output and - when the canvas lets a wire in - its input, and no
  /// name to give it: <see cref="Inputs"/> and <see cref="Outputs"/> then hold that one port (the same object), and
  /// <see cref="AcceptsInput"/> says whether a wire may land on it (a slider has no input grip).
  /// </summary>
  internal sealed class EditNode
  {
    public int Id { get; set; }
    public Guid Guid { get; set; }
    public string Label { get; set; }
    public bool IsParam { get; set; }
    public bool AcceptsInput { get; set; } = true;

    /// <summary>The group the object sits in, by the group's own name, or null. It exists so a card longer than the
    /// bound can be collapsed BY REGION from the canvas's own structure rather than from anything the model wrote.</summary>
    public string Region { get; set; }

    public List<EditPort> Inputs { get; set; } = new List<EditPort>();
    public List<EditPort> Outputs { get; set; } = new List<EditPort>();
  }

  /// <summary>The slice of the live canvas the edit validator reads: which ids are alive, and the nodes behind them.</summary>
  internal sealed class EditCanvas
  {
    public Func<int, IdState> State { get; set; }
    public Dictionary<int, EditNode> Nodes { get; set; } = new Dictionary<int, EditNode>();
  }

  /// <summary>One validated row of the card - the change, its plugin-written sentence, and what it leaves behind.</summary>
  internal sealed class EditStep
  {
    /// <summary>The row's number ON THE CARD, 1-based. A re-validated subset keeps the numbers the user saw, so a
    /// refusal at the click names the row they were looking at.</summary>
    public int Row { get; set; }

    public EditOpKind Kind { get; set; }
    public EditNode From { get; set; }
    public EditNode To { get; set; }
    public EditNode Target { get; set; }
    public EditPort FromPort { get; set; }
    public EditPort ToPort { get; set; }

    /// <summary>The ports by the canvas's own spelling, or null for a parameter (it has none to name).</summary>
    public string FromParam { get; set; }
    public string ToParam { get; set; }

    /// <summary>The row, as a sentence. Plugin-authored: every word is a canvas label, an id or this file's own text.</summary>
    public string Text { get; set; }

    /// <summary>What a DELETE leaves orphaned, named - "Deleting Addition (14) leaves Multiplication (18) input A with
    /// no source" (#625/#647: a cut that orphans a downstream input is named, never discovered afterwards).</summary>
    public List<string> Orphans { get; set; } = new List<string>();

    public string Region { get; set; }
  }

  /// <summary>
  /// A validated edit: the rows, the record the click re-validates, and the DECLARED DELTA the verdict is computed from.
  /// </summary>
  internal sealed class EditPlan
  {
    public List<EditStep> Steps { get; set; } = new List<EditStep>();

    /// <summary>The ops in the NORMAL form the plugin resolved (canvas spellings), in card order. THE RECORD IS THE
    /// SPEC, not a copy of the request (#862's shape): this is what rides the consent token and what the click
    /// re-validates.</summary>
    public JsonArray Record { get; set; } = new JsonArray();

    /// <summary>
    /// What the apply DECLARES it will do, in the fingerprint's own terms - the objects deleted, and for every input
    /// the edit touches (a wire, an unwire, or a source lost to a delete) the COMPLETE ordered source list it ends
    /// with. <see cref="DeltaPredictor"/> turns it into the predicted after-document (Simplify's layer 1, ADR-090).
    /// </summary>
    public SimplifyPlan Declared { get; set; } = new SimplifyPlan();

    /// <summary>Every object guid the edit names, with how the card calls it - so a verdict can name what differed.</summary>
    public Dictionary<Guid, string> Labels { get; set; } = new Dictionary<Guid, string>();

    /// <summary>The inputs whose sources change and whose OWNER survives - snapshotted for undo before anything moves.</summary>
    public List<Guid> TouchedInputs { get; set; } = new List<Guid>();

    public int Wires => Steps.Count(s => s.Kind == EditOpKind.Wire);
    public int Unwires => Steps.Count(s => s.Kind == EditOpKind.Unwire);
    public int Deletes => Steps.Count(s => s.Kind == EditOpKind.Delete);
  }

  /// <summary>
  /// THE ASK, VALIDATED (Phase-9 U6 + U21, ruled at #760): wire, unwire and delete EXISTING components, batched on one card.
  ///
  /// THE RULE THE RULING MAKES, AND THIS FILE IS ITS ENFORCER: every op resolves to exactly one existing target at
  /// approval time, or the card REFUSES with the failing rows NAMED (#338). The bound on what is SHOWN (twelve rows,
  /// then "and N more") never loosens what is CHECKED - all rows are validated here, every time, shown or not.
  ///
  /// It runs twice: at the ask, over every row the model nominated, before a card exists; and at the click, over the
  /// rows the user left ticked, against the canvas as it is THEN (a card can sit while the canvas moves, #775).
  ///
  /// WIRE RESOLUTION IS THE BUILD'S OWN. A port is matched by an exact NAME or an exact NICKNAME through
  /// <see cref="BuildSpecParser.Port"/> - the same code <c>build_region</c> and the spot ask use - so what an edit
  /// accepts is what a build accepts, and a model that learned a port's spelling from one learned it for both (U41,
  /// #975). Where a port is forced (a parameter has one implicit port; a side with one port has one answer) the plugin
  /// answers; where it is a choice it must MATCH or the refusal carries the real list.
  ///
  /// GH-free (ADR-091): the canvas arrives as an <see cref="EditCanvas"/>.
  /// </summary>
  internal static class EditOpsAsk
  {
    /// <summary>The most rows one card may carry. This bounds what a MODEL may nominate, which is not the same number as
    /// what the card shows at once (<see cref="ConsentCard.MaxEditRowsShown"/>): rows past that collapse, they are not
    /// refused. Past this, the refusal names the door - ask for the first part, then the rest (#613/#626).</summary>
    public const int MaxOps = 40;

    public static bool Validate(JsonArray ops, EditCanvas canvas, out EditPlan plan, out ToolResult error) =>
      Validate(ops, canvas, null, out plan, out error);

    /// <param name="cardRows">For a re-validated subset: the number each op carried on the card, so a refusal names the
    /// row the user was looking at. Null means rows are numbered 1..n as they arrive.</param>
    public static bool Validate(JsonArray ops, EditCanvas canvas, IReadOnlyList<int> cardRows,
      out EditPlan plan, out ToolResult error)
    {
      plan = null;
      error = null;

      if (ops == null || ops.Count == 0)
      {
        error = ToolResult.Fail(ToolErrors.InvalidArgs,
          "edit needs \"ops\" - the list of changes to put on one card, each {\"op\": \"wire\" | \"unwire\" | \"delete\", ...}. " +
          "A wire or unwire gives \"from\" (an output: {\"id\", \"param\"?}) and \"to\" (an input: {\"id\", \"param\"?}); " +
          "a delete gives \"id\".");
        return false;
      }

      if (ops.Count > MaxOps)
      {
        error = ToolResult.Fail(ToolErrors.PreconditionRefused,
          "That is " + ops.Count + " changes on one card. Dwight puts at most " + MaxOps + " on a card - past that the " +
          "user is approving more than anyone reads. Ask for the first " + MaxOps + ", and ask again for the rest once " +
          "those are done.",
          new JsonObject { ["asked"] = ops.Count, ["most"] = MaxOps });
        return false;
      }

      canvas = canvas ?? new EditCanvas();
      var problems = new List<RowProblem>();

      // ---- 1. shape, ids, ports - every row, so the refusal can name them ALL at once -------------------------
      var rows = new List<Row>();
      for (var i = 0; i < ops.Count; i++)
      {
        var number = cardRows != null && i < cardRows.Count ? cardRows[i] : i + 1;
        var row = ReadRow(ops[i] as JsonObject, number, canvas, problems);
        rows.Add(row);   // a bad row still occupies its slot, so the numbers the card shows never shift
      }

      // ---- 2. the card as a whole: what is deleted may not also be touched --------------------------------------
      var deletedIds = new Dictionary<int, int>();   // node id -> the row that deletes it
      foreach (var r in rows)
      {
        if (r.Kind != EditOpKind.Delete || r.Target == null) continue;
        if (deletedIds.TryGetValue(r.Target.Id, out var firstRow))
        {
          problems.Add(new RowProblem(r.Number, Ref(r.Target) + " is deleted twice (rows " + firstRow + " and " + r.Number + "). Delete it once."));
          r.Bad = true;
          continue;
        }
        deletedIds[r.Target.Id] = r.Number;
      }
      foreach (var r in rows)
      {
        if (r.Kind == EditOpKind.Delete) continue;
        foreach (var n in new[] { r.From, r.To })
          if (n != null && deletedIds.TryGetValue(n.Id, out var delRow))
          {
            problems.Add(new RowProblem(r.Number, Ref(n) + " is deleted by row " + delRow + ", so this row cannot " +
              (r.Kind == EditOpKind.Wire ? "wire it" : "unwire it") + ". Drop one of the two."));
            r.Bad = true;
          }
      }

      // ---- 3. the wiring, simulated in card order ----------------------------------------------------------------
      // The simulation IS the declared delta: the sources every input ends with if exactly these rows are applied. A
      // duplicate wire, a wire that is not there to remove and a loop are all decided against it, in order, so an
      // unwire after a wire of the same pair is valid and the other order is not.
      var original = new Dictionary<Guid, List<Guid>>();
      var sim = new Dictionary<Guid, List<Guid>>();
      var outputOwner = new Dictionary<Guid, EditNode>();
      var inputOwner = new Dictionary<Guid, EditNode>();
      foreach (var n in canvas.Nodes.Values)
      {
        foreach (var o in n.Outputs) outputOwner[o.Guid] = n;
        foreach (var ip in n.Inputs)
        {
          inputOwner[ip.Guid] = n;
          original[ip.Guid] = new List<Guid>(ip.Sources ?? new List<Guid>());
          sim[ip.Guid] = new List<Guid>(ip.Sources ?? new List<Guid>());
        }
      }

      var touched = new List<Guid>();
      foreach (var r in rows)
      {
        if (r.Bad || r.Kind == EditOpKind.Delete) continue;
        var input = r.ToPort.Guid;
        var source = r.FromPort.Guid;

        if (r.Kind == EditOpKind.Wire)
        {
          if (ReferenceEquals(r.From, r.To))
          {
            problems.Add(new RowProblem(r.Number, Ref(r.From) + " cannot feed its own input - that would be a loop."));
            r.Bad = true;
            continue;
          }
          if (sim[input].Contains(source))
          {
            problems.Add(new RowProblem(r.Number, Describe(r) + " already exists, so wiring it again would do nothing."));
            r.Bad = true;
            continue;
          }
          if (Reaches(r.To, r.From, sim, canvas, outputOwner))
          {
            problems.Add(new RowProblem(r.Number, Describe(r) + " would make " + Ref(r.To) + " feed itself through " +
              Ref(r.From) + " - a loop, which Grasshopper cannot solve."));
            r.Bad = true;
            continue;
          }
          r.HadSources = sim[input].Count;
          sim[input].Add(source);
        }
        else   // Unwire
        {
          if (!sim[input].Contains(source))
          {
            problems.Add(new RowProblem(r.Number, "There is no wire from " + EndText(r.From, r.FromParam) + " to " +
              EndText(r.To, r.ToParam) + " to remove" + (original[input].Contains(source) ? " (an earlier row already removes it)." : ".")));
            r.Bad = true;
            continue;
          }
          sim[input].Remove(source);
        }
        if (!touched.Contains(input)) touched.Add(input);
      }

      if (problems.Count > 0)
      {
        error = Refusal(problems, shown: cardRows != null);
        return false;
      }

      // ---- 4. deletes: what each one cuts, and what that leaves ---------------------------------------------------
      var deletedGuids = new HashSet<Guid>(rows.Where(r => r.Kind == EditOpKind.Delete).Select(r => r.Target.Guid));
      var deletedOutputs = new Dictionary<Guid, EditNode>();   // an output port guid -> the deleted node that owns it
      foreach (var r in rows.Where(r => r.Kind == EditOpKind.Delete))
        foreach (var o in r.Target.Outputs) deletedOutputs[o.Guid] = r.Target;

      // The consumers a delete cuts, found against the sources each input has AFTER the wire rows - a row that wires
      // something new into an input the delete also starves is part of the answer.
      var lostBy = new Dictionary<Guid, List<EditNode>>();   // input guid -> the deleted nodes feeding it
      foreach (var n in canvas.Nodes.Values)
      {
        if (deletedGuids.Contains(n.Guid)) continue;
        foreach (var ip in n.Inputs)
        {
          foreach (var s in sim[ip.Guid])
            if (deletedOutputs.TryGetValue(s, out var dead))
            {
              if (!lostBy.TryGetValue(ip.Guid, out var list)) lostBy[ip.Guid] = list = new List<EditNode>();
              if (!list.Contains(dead)) list.Add(dead);
            }
        }
      }
      var finalSources = new Dictionary<Guid, List<Guid>>();
      foreach (var kv in sim) finalSources[kv.Key] = kv.Value.Where(s => !deletedOutputs.ContainsKey(s)).ToList();
      foreach (var g in lostBy.Keys) if (!touched.Contains(g)) touched.Add(g);

      // ---- 5. the steps, the record, the declared delta ----------------------------------------------------------
      plan = new EditPlan();
      foreach (var r in rows)
      {
        var step = new EditStep
        {
          Row = r.Number, Kind = r.Kind, From = r.From, To = r.To, Target = r.Target,
          FromPort = r.FromPort, ToPort = r.ToPort, FromParam = r.FromParam, ToParam = r.ToParam,
          Region = (r.Kind == EditOpKind.Delete ? r.Target : r.To)?.Region,
        };
        step.Text = StepText(r);
        plan.Steps.Add(step);
        plan.Record.Add(RecordOf(r));
        foreach (var n in new[] { r.From, r.To, r.Target })
          if (n != null) plan.Labels[n.Guid] = Ref(n);
      }

      AttachOrphans(plan, rows, lostBy, canvas, inputOwner, original, finalSources);

      foreach (var g in deletedGuids) plan.Declared.Removed.Add(g);
      foreach (var input in touched)
      {
        if (inputOwner.TryGetValue(input, out var owner) && deletedGuids.Contains(owner.Guid)) continue;   // goes with its owner
        plan.TouchedInputs.Add(input);
        plan.Declared.Wires.Add(new PlanWire(input, finalSources[input].ToArray()));
      }
      foreach (var kv in lostBy)
        foreach (var d in kv.Value) plan.Labels[d.Guid] = Ref(d);
      foreach (var g in plan.TouchedInputs)
        if (inputOwner.TryGetValue(g, out var o)) plan.Labels[o.Guid] = Ref(o);

      return true;
    }

    // ==== rows =========================================================================================================

    private sealed class Row
    {
      public int Number;
      public EditOpKind Kind;
      public EditNode From, To, Target;
      public EditPort FromPort, ToPort;
      public string FromParam, ToParam;
      public bool Bad;
      public int HadSources;
    }

    private sealed class RowProblem
    {
      public readonly int Row;
      public readonly string Text;
      public RowProblem(int row, string text) { Row = row; Text = text; }
    }

    private static Row ReadRow(JsonObject op, int number, EditCanvas canvas, List<RowProblem> problems)
    {
      var row = new Row { Number = number, Bad = true };
      void Fail(string text) => problems.Add(new RowProblem(number, text));

      if (op == null) { Fail("This row is not an object. Each change is {\"op\": \"wire\" | \"unwire\" | \"delete\", ...}."); return row; }

      var word = (Str(op["op"]) ?? "").Trim().ToLowerInvariant();
      if (word == "wire") row.Kind = EditOpKind.Wire;
      else if (word == "unwire") row.Kind = EditOpKind.Unwire;
      else if (word == "delete") row.Kind = EditOpKind.Delete;
      else
      {
        Fail(string.IsNullOrEmpty(word)
          ? "This row has no \"op\". It must be exactly one of: wire, unwire, delete."
          : "\"" + Clip(word) + "\" is not something an edit can do. It must be exactly one of: wire, unwire, delete.");
        return row;
      }

      if (row.Kind == EditOpKind.Delete)
      {
        if (!ReadNode(op["id"], "this row's \"id\"", canvas, out var node, Fail)) return row;
        row.Target = node;
        row.Bad = false;
        return row;
      }

      var from = op["from"] as JsonObject;
      var to = op["to"] as JsonObject;
      if (from == null || to == null)
      {
        Fail("A " + word + " needs both a \"from\" (the output end) and a \"to\" (the input end), each {\"id\", \"param\"?}.");
        return row;
      }

      var ok = ReadNode(from["id"], "\"from\" id", canvas, out var fromNode, Fail);
      ok &= ReadNode(to["id"], "\"to\" id", canvas, out var toNode, Fail);
      if (!ok) return row;

      // the output end
      var fromParam = Str(from["param"]);
      if (!ResolvePort(fromNode, output: true, fromParam, number, problems, out var fromPort, out var fromLabel)) return row;

      // the input end
      var toParam = Str(to["param"]);
      if (!ResolvePort(toNode, output: false, toParam, number, problems, out var toPort, out var toLabel)) return row;

      row.From = fromNode; row.To = toNode; row.FromPort = fromPort; row.ToPort = toPort;
      row.FromParam = fromLabel; row.ToParam = toLabel;
      row.Bad = false;
      return row;
    }

    private static bool ReadNode(JsonNode idNode, string what, EditCanvas canvas, out EditNode node, Action<string> fail)
    {
      node = null;
      int id;
      try { id = idNode.GetValue<int>(); }
      catch
      {
        fail("Needs " + what + " - a whole number, an id from a previous read.");
        return false;
      }

      var state = canvas.State == null ? IdState.Unknown : canvas.State(id);
      if (state == IdState.Unknown)
      {
        fail("No component with id " + id + " exists. Ids come from this session's reads - do not invent them.");
        return false;
      }
      if (state == IdState.Retired)
      {
        fail("Component " + id + " is no longer on the canvas (it was deleted, or an undo removed it).");
        return false;
      }
      if (!canvas.Nodes.TryGetValue(id, out node) || node == null)
      {
        fail("Id " + id + " is on the canvas but is not a component or a parameter (a group or a note, perhaps), so it has no wires to edit.");
        return false;
      }
      return true;
    }

    /// <summary>
    /// One end of a wire, resolved to exactly one port - or a problem naming why not. THE SAME RULE THE BUILD USES
    /// (<see cref="BuildSpecParser.Port"/>): forced where there is one answer, an exact name or nickname where there is a
    /// choice, refused with the real list where it does not match. On top of it, the two errors an EDIT has that a build
    /// does not: a wire between two OUTPUTS (the model named an output where an input belongs, or the reverse) and a
    /// port that does not exist on this side at all.
    /// </summary>
    private static bool ResolvePort(EditNode node, bool output, string given, int row, List<RowProblem> problems,
      out EditPort port, out string label)
    {
      port = null;
      label = null;
      var side = output ? "from" : "to";
      var kind = output ? "output" : "input";
      var ports = output ? node.Outputs : node.Inputs;

      if (!output && node.IsParam && !node.AcceptsInput)
      {
        problems.Add(new RowProblem(row, Ref(node) + " has no input to wire into (a " + node.Label + " is a source, not a destination)."));
        return false;
      }
      if (ports == null || ports.Count == 0)
      {
        problems.Add(new RowProblem(row, Ref(node) + " has no " + kind + "s" + (output ? " to wire from." : " to wire into.")));
        return false;
      }

      // A parameter has one implicit port and no name to give it (ADR-056: the plugin answers wherever it is forced).
      if (node.IsParam)
      {
        port = ports[0];
        return true;
      }

      var labels = ports.Select(p => p.Label).ToList();
      var names = ports.Select(p => p.Name ?? "").ToList();

      // A WIRE BETWEEN TWO OUTPUTS (or two inputs) is the specific mistake worth a specific sentence, and it is decided
      // BEFORE the build's port rule runs: that rule answers a one-port side without reading the name, which is right
      // for a model that left it out and wrong for one that NAMED THE OTHER SIDE. A name that matches a port on the wrong
      // side of this component and none on the right one says the model has the direction backwards - and silently wiring
      // the only port that was left would carry out a different edit from the one it described (#208).
      if (!string.IsNullOrWhiteSpace(given))
      {
        bool Is(EditPort p) => string.Equals(p.Label, given, StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(p.Name, given, StringComparison.OrdinalIgnoreCase);
        var other = (output ? node.Inputs : node.Outputs) ?? new List<EditPort>();
        if (!ports.Any(Is) && other.Any(Is))
        {
          problems.Add(new RowProblem(row, output
            ? "\"" + Clip(given) + "\" on " + Ref(node) + " is an INPUT, and a wire starts at an OUTPUT. A wire between two inputs cannot be made."
            : "\"" + Clip(given) + "\" on " + Ref(node) + " is an OUTPUT, and a wire ends at an INPUT. A wire between two outputs cannot be made."));
          return false;
        }
      }

      if (!BuildSpecParser.Port(labels, names, given, side, kind, out var resolved, out var portError))
      {
        problems.Add(new RowProblem(row, "On " + Ref(node) + ": " + portError.Message + AvailableText(portError)));
        return false;
      }

      // The resolved label must point at exactly ONE port: two ports sharing a label would make "first wins" the
      // answer, which is a fallback beneath a verdict (#208).
      var at = labels.IndexOf(resolved);
      if (at < 0 || labels.Count(l => string.Equals(l, resolved, StringComparison.Ordinal)) != 1)
      {
        problems.Add(new RowProblem(row, Ref(node) + " has more than one " + kind + " called \"" + Clip(resolved ?? given) +
                                         "\", so the name does not say which one is meant."));
        return false;
      }
      port = ports[at];
      label = resolved;
      return true;
    }

    private static string AvailableText(ToolResult e)
    {
      try
      {
        var arr = e.Detail?["available"] as JsonArray;
        if (arr == null || arr.Count == 0) return "";
        return " Its ports on that side: " + string.Join(", ", arr.Select(a => Str(a) ?? "")) + ".";
      }
      catch { return ""; }
    }

    /// <summary>Does <paramref name="goal"/> sit downstream of <paramref name="start"/> in the simulated wiring? A wire
    /// from <c>goal</c> into <c>start</c> would then close a loop.</summary>
    private static bool Reaches(EditNode start, EditNode goal, Dictionary<Guid, List<Guid>> sim, EditCanvas canvas,
      Dictionary<Guid, EditNode> outputOwner)
    {
      var consumers = new Dictionary<EditNode, List<EditNode>>();
      foreach (var n in canvas.Nodes.Values)
        foreach (var ip in n.Inputs)
          if (sim.TryGetValue(ip.Guid, out var sources))
            foreach (var s in sources)
              if (outputOwner.TryGetValue(s, out var owner) && !ReferenceEquals(owner, n))
              {
                if (!consumers.TryGetValue(owner, out var list)) consumers[owner] = list = new List<EditNode>();
                list.Add(n);
              }

      var seen = new HashSet<EditNode>();
      var stack = new Stack<EditNode>();
      stack.Push(start);
      while (stack.Count > 0)
      {
        var cur = stack.Pop();
        if (ReferenceEquals(cur, goal)) return true;
        if (!seen.Add(cur)) continue;
        if (consumers.TryGetValue(cur, out var next)) foreach (var c in next) stack.Push(c);
      }
      return false;
    }

    // ==== orphans ======================================================================================================

    /// <summary>
    /// NAME WHAT EACH DELETE LEAVES BEHIND (#625/#647). For every input a delete starves, one sentence: with NO source
    /// left it is "leaves Multiplication (18) input A with no source"; with others remaining it says it keeps them. Where
    /// several deleted components fed the same input they are named together, on the LAST of those delete rows - the one
    /// that completes the cut - so the sentence is true of the row it sits on and of the rows before it.
    /// </summary>
    private static void AttachOrphans(EditPlan plan, List<Row> rows, Dictionary<Guid, List<EditNode>> lostBy,
      EditCanvas canvas, Dictionary<Guid, EditNode> inputOwner, Dictionary<Guid, List<Guid>> original,
      Dictionary<Guid, List<Guid>> finalSources)
    {
      var stepOf = new Dictionary<Guid, EditStep>();
      foreach (var s in plan.Steps.Where(s => s.Kind == EditOpKind.Delete)) stepOf[s.Target.Guid] = s;

      // deterministic: by the consumer's id, then the input's position
      var inputs = lostBy.Keys
        .Select(g => new { Input = g, Owner = inputOwner[g] })
        .OrderBy(x => x.Owner.Id)
        .ThenBy(x => x.Owner.Inputs.FindIndex(p => p.Guid == x.Input))
        .ToList();

      foreach (var x in inputs)
      {
        var killers = lostBy[x.Input].OrderBy(d => stepOf[d.Guid].Row).ToList();
        var carrier = stepOf[killers.Last().Guid];
        var port = x.Owner.Inputs.First(p => p.Guid == x.Input);
        var where = x.Owner.IsParam ? Ref(x.Owner) : Ref(x.Owner) + " input " + port.Label;
        var who = string.Join(" and ", killers.Select(Ref));
        var left = finalSources[x.Input].Count;

        carrier.Orphans.Add(left == 0
          ? "Deleting " + who + (killers.Count == 1 ? " leaves " : " leave ") + where + " with no source"
          : "Deleting " + who + " cuts a wire into " + where + ", which keeps its other " + left + (left == 1 ? " source" : " sources"));
      }
    }

    // ==== sentences ====================================================================================================

    /// <summary>"Addition (14)" - how the card, the report and the model all name a component. The id is the agent id
    /// the model reads everywhere else, so a sentence here and a read there name the same thing.</summary>
    public static string Ref(EditNode n) => n == null ? "a component" : n.Label + " (" + n.Id + ")";

    private static string EndText(EditNode n, string param) =>
      Ref(n) + (string.IsNullOrWhiteSpace(param) ? "" : " " + param);

    private static string Describe(Row r) =>
      "The wire from " + EndText(r.From, r.FromParam) + " to " + EndText(r.To, r.ToParam);

    private static string StepText(Row r)
    {
      switch (r.Kind)
      {
        case EditOpKind.Wire:
          return "Wire " + EndText(r.From, r.FromParam) + " to " + InputText(r) +
                 (r.HadSources > 0
                   ? " - it already has " + (r.HadSources == 1 ? "a source" : r.HadSources + " sources") + "; this one is added alongside"
                   : "");
        case EditOpKind.Unwire:
          return "Remove the wire from " + EndText(r.From, r.FromParam) + " to " + InputText(r);
        default:
          return "Delete " + Ref(r.Target) + " and every wire on it";
      }
    }

    private static string InputText(Row r) =>
      Ref(r.To) + (r.To.IsParam || string.IsNullOrWhiteSpace(r.ToParam) ? "" : " input " + r.ToParam);

    private static JsonObject RecordOf(Row r)
    {
      if (r.Kind == EditOpKind.Delete) return new JsonObject { ["op"] = "delete", ["id"] = r.Target.Id };

      JsonObject End(EditNode n, string param)
      {
        var o = new JsonObject { ["id"] = n.Id };
        if (!string.IsNullOrWhiteSpace(param)) o["param"] = param;
        return o;
      }

      return new JsonObject
      {
        ["op"] = r.Kind == EditOpKind.Wire ? "wire" : "unwire",
        ["from"] = End(r.From, r.FromParam),
        ["to"] = End(r.To, r.ToParam),
      };
    }

    /// <summary>
    /// The refusal: every failing row, by number, in one message - so the model fixes the card in one pass instead of
    /// being walked through it a row at a time (and a model that is refused five times in a row has been told to stop,
    /// #615). The detail carries the rows as data too.
    ///
    /// TWO STATES REACH THIS SENTENCE, AND IT MUST BE TRUE IN BOTH (#1009, U49): at mint time no card was shown, and the
    /// sentence says so; at click time the card WAS shown and pressed, and a row stopped resolving in between (a component
    /// deleted by hand), so "No card was shown" would be a false claim. <paramref name="shown"/> picks the preamble; the
    /// row lines are the same, numbered as the card numbered them.
    /// </summary>
    private static ToolResult Refusal(List<RowProblem> problems, bool shown)
    {
      var ordered = problems.OrderBy(p => p.Row).ToList();
      var lines = ordered.Select(p => "Row " + p.Row + ": " + p.Text).ToList();
      var rows = new JsonArray(ordered.Select(p => (JsonNode)new JsonObject { ["row"] = p.Row, ["problem"] = p.Text }).ToArray());
      var one = ordered.Count == 1;
      var preamble = shown
        ? (one ? "One row of this card no longer resolves" : ordered.Count + " rows of this card no longer resolve") +
          " to exactly one existing target, so none of the ticked changes was made. "
        : "No card was shown, because " + (one ? "one row does" : ordered.Count + " rows do") + " not resolve to " +
          "exactly one existing target. Nothing is applied unless every row is right. ";
      return ToolResult.Fail(ToolErrors.InvalidArgs, preamble + string.Join(" ", lines), new JsonObject { ["rows"] = rows });
    }

    private static string Str(JsonNode n)
    {
      if (n == null) return null;
      try { return n.GetValue<string>(); } catch { return null; }
    }

    /// <summary>Model text echoed into a sentence is cut short and stripped of line breaks (it is data, never prose).</summary>
    private static string Clip(string s)
    {
      s = (s ?? "").Replace("\r", " ").Replace("\n", " ");
      return s.Length <= 40 ? s : s.Substring(0, 40) + "...";
    }

    // ==== the card's rows, and the subset the click applies ==============================================================

    /// <summary>
    /// The ops the user LEFT TICKED, from the record the card carried, with the card-row number each one had. An index
    /// the card never had refuses (a hostile or stale click cannot reach a row that was not shown), and the same row
    /// twice counts once.
    /// </summary>
    public static bool Subset(JsonArray record, IReadOnlyList<int> ticked, out JsonArray ops, out List<int> cardRows,
      out string refusal)
    {
      ops = new JsonArray();
      cardRows = new List<int>();
      refusal = null;

      if (record == null || record.Count == 0)
      {
        refusal = "This card no longer holds the changes it was drawn from, so nothing was applied. Ask again and Dwight will offer them afresh.";
        return false;
      }

      foreach (var i in (ticked ?? new List<int>()).Distinct().OrderBy(i => i))
      {
        if (i < 0 || i >= record.Count)
        {
          refusal = "That click named a row this card does not have, so nothing was applied.";
          return false;
        }
        ops.Add(record[i].DeepClone());
        cardRows.Add(i + 1);
      }
      return true;
    }

    // ==== the sentences the click reports ==================================================================================

    /// <summary>
    /// WHAT THE USER READS AFTER AN APPLIED EDIT: what was done, by row, what it left unsourced, and what was not applied.
    /// The same sentence goes to the screen and to the transcript the model reads (#624), so they cannot disagree.
    /// Plugin-authored throughout.
    /// </summary>
    /// <param name="objectsBefore">The objects on the canvas before the first change, counted, not assumed.</param>
    /// <param name="objectsAfter">And after the last. Brief CL: "nothing else differs" came from the behaviour fingerprint, which sees neither a group nor a scribble, so it read
    /// true over an empty group left behind (1,290 objects against 1,289). What was checked is now said as what it is, with the count.</param>
    public static string AppliedReport(EditPlan plan, int cardTotal, int objectsBefore, int objectsAfter, EditGroupFate groups = null)
    {
      var n = plan.Steps.Count;
      var parts = plan.Steps.Take(ConsentCard.MaxEditRowsShown).Select(s => s.Text).ToList();
      var more = n - parts.Count;
      var text = "Applied " + Plural(n, "change") + ": " + string.Join("; ", parts) +
                 (more > 0 ? "; and " + more + " more." : ".") +
                 " Dwight then checked the result: wires and values differ only as those changes should, and the canvas " +
                 (objectsBefore == objectsAfter
                   ? "still holds " + Count(objectsAfter) + " objects."
                   : "went from " + Count(objectsBefore) + " objects to " + Count(objectsAfter) + ".");
      var fate = EditEmptyGroups.Sentence(groups);
      if (fate != null) text += " " + fate;

      var orphans = plan.Steps.SelectMany(s => s.Orphans).ToList();
      if (orphans.Count > 0)
        text += " " + string.Join(". ", orphans.Take(ConsentCard.MaxOrphansShown)) +
                (orphans.Count > ConsentCard.MaxOrphansShown ? ". And " + (orphans.Count - ConsentCard.MaxOrphansShown) + " more like it" : "") + ".";

      var skipped = cardTotal - n;
      if (skipped > 0)
        text += " " + Plural(skipped, "unticked change") + (skipped == 1 ? " was" : " were") + " not applied.";
      return text + " One Ctrl-Z undoes all of it.";
    }

    public static string Plural(int n, string noun) => n + " " + noun + (n == 1 ? "" : "s");

    private static string Count(int n) => n.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
  }

  /// <summary>One group on the canvas as it stood before a delete, GH-free. The members are a copy, taken first: what a removal does to the live list is Grasshopper's.</summary>
  internal sealed class EditGroupFacts
  {
    public Guid Guid { get; set; }
    public string Name { get; set; }
    public IReadOnlyList<Guid> Members { get; set; }
  }

  /// <summary>What becomes of the groups a delete emptied.</summary>
  internal sealed class EditGroupFate
  {
    public List<Guid> Remove { get; } = new List<Guid>();
    public List<string> Removed { get; } = new List<string>();
    public List<string> LeftEmpty { get; } = new List<string>();
  }

  /// <summary>
  /// Brief CL: A DELETE THAT EMPTIES A GROUP. "Remove the panel you just added" took the panel out of the spot group Dwight had made for it and left the group, an empty box
  /// nobody asked for, while the report said nothing else differed. A group Dwight made (its name is a region's address, the one way the plugin knows its own) goes with the last thing
  /// in it, in the same Ctrl-Z. A group of the person's own is theirs: it stays, and the report names it. A map's region keeps its header scribble, so a delete never empties one.
  /// </summary>
  internal static class EditEmptyGroups
  {
    /// <param name="deleted">What this edit deleted. A group it did not touch is not its to judge, even an empty one.</param>
    /// <param name="stillThere">Is this member on the canvas now?</param>
    public static EditGroupFate Decide(IEnumerable<EditGroupFacts> groups, ICollection<Guid> deleted, Func<Guid, bool> stillThere)
    {
      var fate = new EditGroupFate();
      foreach (var g in groups ?? Enumerable.Empty<EditGroupFacts>())
      {
        var members = g?.Members ?? new List<Guid>();
        if (!members.Any(deleted.Contains) || members.Any(stillThere)) continue;

        if (ScaffoldAddress.FromGroupName(g.Name, out _, out _))
        {
          fate.Remove.Add(g.Guid);
          fate.Removed.Add(g.Name);
        }
        else fate.LeftEmpty.Add(string.IsNullOrWhiteSpace(g.Name) ? "with no name" : g.Name);
      }
      return fate;
    }

    /// <summary>The report's words for it, or null. Both channels read them (#624), so the model is not left to say the group is still there.</summary>
    public static string Sentence(EditGroupFate fate)
    {
      var said = new List<string>();
      if (fate?.Removed.Count > 0)
        said.Add(fate.Removed.Count == 1
          ? "The group \"" + fate.Removed[0] + "\" held nothing else, so it went too."
          : "The groups " + Quoted(fate.Removed) + " held nothing else, so they went too.");
      if (fate?.LeftEmpty.Count > 0)
        said.Add(fate.LeftEmpty.Count == 1
          ? "The group \"" + fate.LeftEmpty[0] + "\" is now empty; it is not one Dwight made, so it was left."
          : "The groups " + Quoted(fate.LeftEmpty) + " are now empty; they are not ones Dwight made, so they were left.");
      return said.Count == 0 ? null : string.Join(" ", said);
    }

    private static string Quoted(IEnumerable<string> names) => string.Join(", ", names.Select(n => "\"" + n + "\""));
  }

  /// <summary>
  /// THE VERDICT FOR AN EDIT: PREDICTED DELTA over the behaviour fingerprint, exactly Simplify's layer 1 (ADR-090).
  /// Predict the after-document from the before-document plus ONLY the declared change; compute the actual; they are
  /// equal or the edit is taken back. It catches everything the plan did not declare - a wire that landed on the wrong
  /// port, a component that vanished with the one that was deleted, a value that moved.
  ///
  /// COMPUTED ON THE APPLIED SET ONLY (#760): the plan it is handed is the one built from the ticked rows, so an
  /// unticked row is not in the prediction and, had it been applied, would show as a difference.
  /// </summary>
  internal static class EditPrediction
  {
    /// <summary>The predicted fingerprint after the edit.</summary>
    public static string Predict(FpDoc before, EditPlan plan) =>
      DeltaPredictor.Predict(before, plan?.Declared);

    /// <summary>
    /// Does the actual after-document equal what the declared change predicts? When it does not, the objects that differ
    /// are returned so the report can NAME them - an auto-revert that says only "something differed" teaches the model
    /// to invent a cause.
    /// </summary>
    public static bool Matches(FpDoc before, EditPlan plan, FpDoc actual, out List<Guid> differing)
    {
      var predictedDoc = DeltaPredictor.PredictDoc(before, plan?.Declared);
      differing = new List<Guid>();
      if (string.Equals(BehaviourFingerprintCore.Compute(predictedDoc), BehaviourFingerprintCore.Compute(actual), StringComparison.Ordinal))
        return true;
      differing = Differences(predictedDoc, actual);
      return false;
    }

    /// <summary>The objects whose state differs between the predicted and the actual document: present in only one,
    /// or differing in type, value, lock, an input's sources (order included) or its data mapping.</summary>
    public static List<Guid> Differences(FpDoc predicted, FpDoc actual)
    {
      var result = new List<Guid>();
      var p = (predicted?.Objects ?? new List<FpObject>()).Where(o => o != null).GroupBy(o => o.Guid).ToDictionary(g => g.Key, g => g.First());
      var a = (actual?.Objects ?? new List<FpObject>()).Where(o => o != null).GroupBy(o => o.Guid).ToDictionary(g => g.Key, g => g.First());

      foreach (var kv in p)
      {
        if (!a.TryGetValue(kv.Key, out var other) || !SameObject(kv.Value, other)) result.Add(kv.Key);
      }
      foreach (var g in a.Keys) if (!p.ContainsKey(g)) result.Add(g);
      return result;
    }

    private static bool SameObject(FpObject x, FpObject y)
    {
      if (x.TypeName != y.TypeName || x.ValueToken != y.ValueToken || x.Locked != y.Locked) return false;
      var xi = x.Inputs ?? new List<FpInput>();
      var yi = y.Inputs ?? new List<FpInput>();
      if (xi.Count != yi.Count) return false;
      for (var i = 0; i < xi.Count; i++)
      {
        if (xi[i].Guid != yi[i].Guid || xi[i].MappingToken != yi[i].MappingToken || xi[i].PersistentToken != yi[i].PersistentToken) return false;
        if (!(xi[i].Sources ?? new List<Guid>()).SequenceEqual(yi[i].Sources ?? new List<Guid>())) return false;
      }
      return true;
    }

    /// <summary>Brief CL: the objects an edit should leave. Only deletes and the groups they emptied change the count, so anything else is something this edit did not declare.</summary>
    public static int ExpectedObjects(int before, int deleted, int groupsRemoved) => before - deleted - groupsRemoved;

    /// <summary>The revert when the count is wrong. The fingerprint sees no group or scribble, so this is the only witness of one.</summary>
    public static string CountRevertText(int expected, int actual) =>
      "Dwight made the changes, checked the result, and put everything back because the canvas held " + actual +
      " objects where those changes should have left " + expected + ". Nothing on the canvas is changed.";

    /// <summary>The revert, in words: which objects differed, by the card's own names, capped with a count.</summary>
    public static string RevertText(IReadOnlyList<Guid> differing, IReadOnlyDictionary<Guid, string> labels, Func<Guid, string> fallback)
    {
      var names = (differing ?? new List<Guid>())
        .Select(g => labels != null && labels.TryGetValue(g, out var l) ? l : (fallback?.Invoke(g) ?? "a component"))
        .Distinct().ToList();
      var told = names.Count == 0
        ? "the wiring did not come out as those changes should have left it"
        : "these did not come out as those changes should have left them: " + string.Join(", ", names.Take(5)) +
          (names.Count > 5 ? " and " + (names.Count - 5) + " more" : "");
      return "Dwight made the changes, checked the result, and put everything back because " + told +
             ". Nothing on the canvas is changed.";
    }
  }
}
