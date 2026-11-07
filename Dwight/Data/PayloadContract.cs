using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Dwight.Data
{
  /// <summary>What a field carries, which is the whole substance of a privacy statement.</summary>
  internal enum Carries
  {
    /// <summary>Component types, port names, wires, ids, counts, Dwight's own words. Not the user's data.</summary>
    Structure,
    /// <summary>A FACT ABOUT the user's data that does not scale with it - a bounding box, a count, a flag,
    /// a validity verdict, at most ten sample points. Contents scale with the data; a fact does not.</summary>
    Fact,
    /// <summary>The user's data itself, bounded - a slider's number, a constant on a wire, an internalised
    /// item described with its value.</summary>
    Value,
    /// <summary>The user's own text, verbatim and bounded - a nickname, a panel, a script's source, and
    /// Grasshopper's own runtime message, which components sometimes fill with a path or a value.</summary>
    Text,
  }

  /// <summary>How a row's field set is held to the code. The unmeasured state REPORTS ITSELF (#374).</summary>
  internal enum AssertedBy
  {
    /// <summary>A headless test builds the payload from a fixture and asserts the emitted paths equal the row.
    /// GUARANTEES: checked on every CI run, unattended, on every commit - a change trips it before it merges.</summary>
    UnitTest,
    /// <summary>The runtime suite builds the payload over a live document and asserts the same equality.
    /// GUARANTEES: checked whenever someone runs the suite on a machine with Rhino open, and on the freeze
    /// commit - NOT continuously, and NEVER in CI (windows-latest has no Rhino, so no runtime-suite check can
    /// run there; zero tokens is not zero preconditions). A different CONTINUITY of assertion from
    /// <see cref="UnitTest"/>, kept visibly distinct so a reader is never told a row is checked more often than
    /// it is (stream A's ruling).</summary>
    RuntimeSuite,
    /// <summary>Needs a live Grasshopper document to build and the runtime suite's check does not exist yet.
    /// GUARANTEES: nothing beyond the hand-read that wrote the row; the row's WaitingFor names the check.</summary>
    RuntimeSuitePending,
  }

  /// <summary>WHEN a row's fields leave. A verb's payload is fixed: run Explain and those fields leave every
  /// time. The Build chat's reads are conditional: fields leave only for the components the model asks
  /// about and the tools it calls, which may be none. Flattening the two would over-state the chat in exactly
  /// the direction that makes a user refuse a feature over something it does not do.</summary>
  internal enum Scope
  {
    /// <summary>Every run of this surface sends these fields.</summary>
    Always,
    /// <summary>Sent only when the model calls this tool, and only for the components it names.</summary>
    OnRequest,
    /// <summary>Brief DX: sent only because the person attached it to a turn they sent, and then with that thread's later turns. Neither the model nor the plugin can make this one leave.</summary>
    WhenAttached,
  }

  internal sealed class PayloadField
  {
    public string Path { get; set; }
    public Carries Carries { get; set; }
    public string Meaning { get; set; }
    /// <summary>The bound where one applies - a Fact without its bound is indistinguishable from a Value that
    /// happens to be small today.</summary>
    public string Bound { get; set; }
  }

  internal sealed class PayloadRow
  {
    /// <summary>The payload's own action name (explain.short, simplify.toscript) or "tool:" + a catalogue tool.</summary>
    public string Surface { get; set; }
    public string Title { get; set; }
    /// <summary>One line in the user's language: what leaves the machine when this runs.</summary>
    public string Sends { get; set; }
    public AssertedBy AssertedBy { get; set; }
    public Scope Scope { get; set; }
    /// <summary>For a pending row: WHAT it is waiting for, not just that it waits.</summary>
    public string WaitingFor { get; set; }
    public IReadOnlyList<PayloadField> Fields { get; set; } = new List<PayloadField>();
  }

  /// <summary>
  /// THE PRIVACY CONTRACT, AS DATA (the ruling on stream D's two-copies finding, Phase 9).
  ///
  /// The statement of what Dwight sends to a model used to live twice - the in-product Settings block and
  /// the public privacy page - with no test on either. Two independently-maintained statements of one
  /// contract drift, and two texts AGREEING proves nothing, because both can be wrong together. So the
  /// contract is this table, keyed per verb and per MODE (Simplify's logic mode and its script modes send
  /// different things, which is exactly how the shorthand "Simplify sends values" went wrong), and the
  /// test is on the BEHAVIOUR's side (#316): <c>PayloadContractTests</c> builds each agent payload from a
  /// fixture and asserts the emitted paths EQUAL the row. Both surfaces render from here.
  ///
  /// An OVER-stated privacy claim is not harmless: it makes a user refuse a feature that does not do what
  /// they fear, and it devalues every claim we state exactly. Precision in both directions.
  ///
  /// The five verbs' rows are AssertedBy.RuntimeSuitePending: their builders read a live document
  /// (DwightGraph's edge holds the live source param so signatures compute lazily), and the split that
  /// would make them headless is a change to shared machinery no single unit may make. Those rows name
  /// what they wait for. GH-free (ADR-091).
  /// </summary>
  internal static class PayloadContract
  {
    // ---- field builders -------------------------------------------------------------------------------

    private static PayloadField F(string path, Carries c, string meaning, string bound = null) =>
      new PayloadField { Path = path, Carries = c, Meaning = meaning, Bound = bound };

    private static IEnumerable<PayloadField> Sig(string prefix)
    {
      yield return F(prefix, Carries.Structure, "The data-tree signature on this wire - structure, never contents.");
      yield return F(prefix + ".paths", Carries.Structure, "Branch count.");
      yield return F(prefix + ".depth", Carries.Structure, "Path depth.");
      yield return F(prefix + ".items", Carries.Structure, "Items per branch.", "first 24 branches");
      yield return F(prefix + ".items[]", Carries.Structure, "One branch's item count.");
    }

    private static IEnumerable<PayloadField> EdgeEnds(string prefix)
    {
      yield return F(prefix + ".from", Carries.Structure, "The wire's source end.");
      yield return F(prefix + ".from.node", Carries.Structure, "Source component id.");
      yield return F(prefix + ".from.param", Carries.Structure, "Source port name.");
      yield return F(prefix + ".to", Carries.Structure, "The wire's target end.");
      yield return F(prefix + ".to.node", Carries.Structure, "Target component id.");
      yield return F(prefix + ".to.param", Carries.Structure, "Target port name.");
    }

    private static IEnumerable<PayloadField> EdgeList(string name, string what, bool withSig)
    {
      yield return F(name, Carries.Structure, what);
      yield return F(name + "[]", Carries.Structure, "One wire.");
      foreach (var f in EdgeEnds(name + "[]")) yield return f;
      if (withSig) foreach (var f in Sig(name + "[].sig")) yield return f;
    }

    private static IEnumerable<PayloadField> Trees()
    {
      yield return F("trees", Carries.Structure, "Tree signatures for the wires listed, by edge index.");
      yield return F("trees[]", Carries.Structure, "One wire's signature.");
      yield return F("trees[].edge", Carries.Structure, "Index into edges.");
      foreach (var f in Sig("trees[].sig")) yield return f;
    }

    /// <param name="inside">Whether this listing can contain a cluster's INTERNALS. A region never can -
    /// the segmenter partitions the top level only - so its row must not declare the field; search and
    /// get_cluster can.</param>
    private static IEnumerable<PayloadField> NodeBrief(string prefix, bool hints, bool inside = false)
    {
      yield return F(prefix + ".id", Carries.Structure, "Component id (session-local integer, never a guid).");
      yield return F(prefix + ".type", Carries.Structure, "Component type name.");
      yield return F(prefix + ".nick", Carries.Text, "The user's nickname for it, when set.");
      foreach (var f in Notes(prefix)) yield return f;
      if (hints)
      {
        yield return F(prefix + ".qualified", Carries.Structure, "Category > SubCategory > Name, only when the bare type name belongs to more than one installed component.");
        yield return F(prefix + ".script", Carries.Fact, "The script's language, when the component is a script.");
        yield return F(prefix + ".value", Carries.Value, "The constant it holds, when it is a slider, a panel or an internalised param.", "one line; text trimmed to 60 chars");
        if (inside) yield return F(prefix + ".inside", Carries.Structure, "The cluster this component sits inside, when it is an internal.");
        yield return F(prefix + ".cluster", Carries.Fact, "For a cluster: how many objects it holds, or protected / unreadable.");
      }
    }

    /// <summary>Brief DC: the lines of text a component's Dwight notes become. The user's own words, so Text, and bounded; absent when it has none.</summary>
    private static IEnumerable<PayloadField> Notes(string prefix)
    {
      var path = prefix.Length == 0 ? "notes" : prefix + ".notes";
      yield return F(path, Carries.Text, "What you or an earlier Dwight wrote about this component, when it has a note: text only, newest first, each prefixed you: or dwight:, with a stale note marked.", "3 notes and 600 chars a component; the cut is stated as 'and N more'");
      yield return F(path + "[]", Carries.Text, "One note's text.");
    }

    private static IEnumerable<PayloadField> ClusterBlock()
    {
      yield return F("inside", Carries.Structure, "The cluster this component sits inside, when it is an internal.");
      yield return F("hook", Carries.Structure, "input | output, for a cluster hook inside a cluster.");
      yield return F("cluster", Carries.Structure, "For a cluster: what the reader found inside, or why it did not look.");
      yield return F("cluster.count", Carries.Fact, "Internal objects.");
      yield return F("cluster.omitted", Carries.Fact, "Internals beyond the cap, not projected.", "200 internals a cluster");
      yield return F("cluster.protected", Carries.Fact, "Password-protected: not read.");
      yield return F("cluster.level", Carries.Fact, "Grasshopper's protection level, by name.");
      yield return F("cluster.locked", Carries.Fact, "The cluster is disabled on the canvas - or \"unmeasured (reason)\" when the read failed; never false for a failed read.");
      yield return F("cluster.unreadable", Carries.Structure, "Why the internal document could not be read, verbatim.");
      yield return F("cluster.readWith", Carries.Structure, "Which tool reads inside it.");
    }

    private static IEnumerable<PayloadField> Msgs(string prefix)
    {
      yield return F(prefix, Carries.Structure, "Runtime messages Grasshopper holds for the component.");
      yield return F(prefix + "[]", Carries.Structure, "One message.");
      yield return F(prefix + "[].level", Carries.Structure, "error | warning | remark.");
      yield return F(prefix + "[].text", Carries.Text, "Grasshopper's own message, verbatim - components sometimes fill it with a path or a value.");
    }

    private static IEnumerable<PayloadField> Envelope()
    {
      yield return F("id", Carries.Structure, "The component read.");
      yield return F("port", Carries.Structure, "The output port read.");
      yield return F("total", Carries.Fact, "Geometry objects on the output.");
      yield return F("nonGeometry", Carries.Fact, "Items that were not geometry.");
      yield return F("shown", Carries.Structure, "Objects in this page.");
      yield return F("skip", Carries.Structure, "Objects skipped for paging.");
      yield return F("empty", Carries.Fact, "The output holds no geometry.");
      yield return F("note", Carries.Structure, "A bound the reader applied across the page.");
      yield return F("omitted", Carries.Structure, "How many more objects there are and how to get them.");
      yield return F("objects", Carries.Structure, "The page.", "25 objects a call");
      yield return F("objects[]", Carries.Structure, "One object.");
      yield return F("objects[].i", Carries.Structure, "Object index on the output.");
      yield return F("objects[].type", Carries.Fact, "RhinoCommon type name.");
      yield return F("objects[].readFailed", Carries.Structure, "Why this object could not be read, verbatim from the exception.");
    }

    private static IEnumerable<PayloadField> Bbox()
    {
      yield return F("objects[].bbox", Carries.Fact, "Axis-aligned bounding box.");
      yield return F("objects[].bbox.min", Carries.Fact, "Minimum corner.", "3 numbers");
      yield return F("objects[].bbox.min[]", Carries.Fact, "One coordinate.");
      yield return F("objects[].bbox.max", Carries.Fact, "Maximum corner.", "3 numbers");
      yield return F("objects[].bbox.max[]", Carries.Fact, "One coordinate.");
    }

    private static IEnumerable<PayloadField> ValidLog()
    {
      yield return F("objects[].valid", Carries.Fact, "RhinoCommon's validity verdict.");
      yield return F("objects[].log", Carries.Text, "RhinoCommon's validity log, verbatim.", "300 chars, cut stated");
    }

    // ---- the table ------------------------------------------------------------------------------------

    /// <summary>A "tool:" surface is sent ON REQUEST (the model called it, for the components it named); a
    /// verb's payload is sent ALWAYS - decided from the surface name so the two can never disagree.</summary>
    private static PayloadRow Row(string surface, string title, string sends, AssertedBy by, IEnumerable<PayloadField> fields, string waitingFor = null) =>
      new PayloadRow
      {
        Surface = surface, Title = title, Sends = sends, AssertedBy = by, WaitingFor = waitingFor, Fields = fields.ToList(),
        Scope = surface.StartsWith("tool:", StringComparison.Ordinal) ? Scope.OnRequest
          : surface.StartsWith(ChatSurface, StringComparison.Ordinal) ? Scope.WhenAttached
          : Scope.Always,
      };

    /// <summary>Brief DX: a surface of the Build chat that is no tool of the model's. Shown with the chat's rows, and sent only when the person attaches it.</summary>
    private const string ChatSurface = "chat:";

    private const string VerbWait =
      "stream F's runtime-suite check over a scratch document: build the payload live and assert its emitted " +
      "paths equal this row (the builders read a live GH_Document, so no headless fixture can drive them).";

    private static readonly Lazy<IReadOnlyList<PayloadRow>> _all = new Lazy<IReadOnlyList<PayloadRow>>(Build);

    public static IReadOnlyList<PayloadRow> All() => _all.Value;

    public static PayloadRow For(string surface) =>
      All().FirstOrDefault(r => string.Equals(r.Surface, surface, StringComparison.Ordinal));

    private static IReadOnlyList<PayloadRow> Build()
    {
      var rows = new List<PayloadRow>();

      // ---- the verbs (pending: live-document builders) ---------------------------------------------------

      IEnumerable<PayloadField> Explain()
      {
        yield return F("action", Carries.Structure, "Which Explain mode.");
        yield return F("focus", Carries.Structure, "The selected component ids.");
        yield return F("focus[]", Carries.Structure, "One id.");
        yield return F("nodes", Carries.Structure, "The selection plus its neighbours.");
        yield return F("nodes[]", Carries.Structure, "One component.");
        foreach (var f in NodeBrief("nodes[]", hints: false)) yield return f;
        foreach (var f in EdgeList("edges", "The wires among them.", withSig: false)) yield return f;
        foreach (var f in Trees()) yield return f;
      }
      foreach (var (surface, title) in new[] { ("explain.short", "Explain (short)"), ("explain.detailed", "Explain (detailed)"), ("explain.oncanvas", "Explain (walk)") })
        rows.Add(Row(surface, title,
          "Structure only: component types, your nicknames, wires and tree signatures for the selection and its neighbours. No values, no geometry.",
          AssertedBy.RuntimeSuitePending, Explain(), VerbWait));

      // Brief DE, as Brief EI's deck and Brief EO's why-questions: Learn's one extra request. It is the one Explain payload that carries a value, so it has its own row and the privacy page says so.
      IEnumerable<PayloadField> Learn()
      {
        yield return F("selection", Carries.Structure, "The components a question may name.");
        yield return F("selection[]", Carries.Text, "One component: your nickname for it, or its type when it has none, and its number in this run.");
        yield return F("summary", Carries.Text, "The Explain's summary, as Step by step's own reply wrote it.");
        yield return F("steps", Carries.Structure, "Step by step's step sentences.");
        yield return F("steps[]", Carries.Text, "One step's sentence, as Step by step's own reply wrote it.");
        yield return F("graph", Carries.Text, "Step by step's own request about your selection, sent again as it is: what Explain (step by step) sends, structure and your nicknames, no values.");
        yield return F("cards", Carries.Structure, "One entry for each card the plugin dealt: at most one read from the live solve and one about the wiring.");
        yield return F("cards[]", Carries.Structure, "One card.");
        yield return F("cards[].card", Carries.Structure, "The card's number among the plugin's cards.");
        yield return F("cards[].kind", Carries.Structure, "value | count | which | feeds.");
        yield return F("cards[].form", Carries.Structure, "enter | choose | choose-all.");
        yield return F("cards[].step", Carries.Structure, "The Step by step's step the card belongs to.");
        yield return F("cards[].component", Carries.Structure, "The type name of the component the card asks about.");
        yield return F("cards[].id", Carries.Structure, "That component's number in this run.");
        yield return F("cards[].nick", Carries.Text, "Your nickname for it, when set.");
        yield return F("cards[].port", Carries.Text, "The port's name: your own nickname for a floating parameter.");
        yield return F("cards[].relation", Carries.Structure, "For a card asking where a wire goes: consumer | source.");
        yield return F("cards[].true", Carries.Value, "For a value or count card: the value that port holds in the solved definition, a number or the count of a list, or how many items its wire carries.");
        yield return F("cards[].options", Carries.Structure, "For a which or feeds card: the components offered as choices, which the plugin chose from your selection.");
        yield return F("cards[].options[]", Carries.Text, "One choice: a component's nickname, or its type when it has none, and its number in this run.");
        yield return F("why", Carries.Structure, "How many why-questions to write.");
      }
      rows.Add(Row("explain.learn", "Explain (Learn)",
        "Step by step's request, and then one more: Step by step's own request again, its summary and step sentences, the components a question may name, and for each card the plugin dealt, the type, nickname and number of the component it asks about, a port's name, and either the value that port holds or how many items its wire carries, or the names and numbers of the components offered as choices. The one Explain that sends a value.",
        AssertedBy.UnitTest, Learn()));

      // Brief EO unit 2: a why-answer typed in your own words, judged by one small call. It is sent only when you type one and press Check.
      IEnumerable<PayloadField> LearnJudge()
      {
        yield return F("question", Carries.Text, "The card's question, as the quiz call wrote it about your selection.");
        yield return F("true", Carries.Text, "The card's right answer, as the quiz call wrote it.");
        yield return F("explain", Carries.Text, "Why that answer is right, as the quiz call wrote it.");
        yield return F("answer", Carries.Text, "What you typed, as you typed it, up to 300 characters.");
      }
      rows.Add(Row("explain.learn.judge", "Explain (Learn, an answer in your own words)",
        "When you answer a Learn card in your own words: the card's question, its right answer and why, and what you typed, to your provider's fast model. Nothing else.",
        AssertedBy.UnitTest, LearnJudge()));

      IEnumerable<PayloadField> Diagnose()
      {
        yield return F("action", Carries.Structure, "diagnose.");
        yield return F("scope", Carries.Structure, "selection | whole-canvas.");
        yield return F("faults", Carries.Structure, "The root-cause faults found deterministically.");
        yield return F("faults[]", Carries.Structure, "One fault.");
        yield return F("faults[].id", Carries.Structure, "Component id.");
        yield return F("faults[].type", Carries.Structure, "Component type.");
        yield return F("faults[].nick", Carries.Text, "Your nickname for it.");
        yield return F("faults[].class", Carries.Structure, "error | warning | silent.");
        yield return F("faults[].role", Carries.Structure, "root | symptom.");
        foreach (var f in Msgs("faults[].msgs")) yield return f;
        yield return F("faults[].outCounts", Carries.Fact, "Items per output.");
        yield return F("faults[].outCounts[]", Carries.Fact, "One output's item count.");
        yield return F("upstream", Carries.Structure, "The components feeding the faults, two hops up.");
        yield return F("upstream[]", Carries.Structure, "One component.");
        foreach (var f in NodeBrief("upstream[]", hints: false)) yield return f;
        foreach (var f in EdgeList("edges", "The wires among them.", withSig: false)) yield return f;
        foreach (var f in Trees()) yield return f;
      }
      rows.Add(Row("diagnose", "Diagnose",
        "Structure plus Grasshopper's own runtime messages, verbatim - the one place a value or a file path can leave without a verb intending it, because a component wrote it into its error text.",
        AssertedBy.RuntimeSuitePending, Diagnose(), VerbWait));

      IEnumerable<PayloadField> Label()
      {
        yield return F("action", Carries.Structure, "label.clusters.");
        yield return F("clusters", Carries.Structure, "The regions the segmenter found.");
        yield return F("clusters[]", Carries.Structure, "One region.");
        yield return F("clusters[].id", Carries.Structure, "Region id.");
        yield return F("clusters[].types", Carries.Structure, "Component types in the region, with counts.");
        yield return F("clusters[].types.*", Carries.Structure, "One type's count.");
        yield return F("clusters[].in", Carries.Structure, "Types feeding the region.");
        yield return F("clusters[].in[]", Carries.Structure, "One type.");
        yield return F("clusters[].out", Carries.Structure, "Types the region feeds.");
        yield return F("clusters[].out[]", Carries.Structure, "One type.");
        yield return F("clusters[].central", Carries.Structure, "The region's most connected types.");
        yield return F("clusters[].central[]", Carries.Structure, "One type.");
        yield return F("clusters[].label", Carries.Text, "A label already cached for this region.");
        yield return F("flow", Carries.Structure, "Which regions feed which.");
        yield return F("flow[]", Carries.Structure, "One flow edge.");
        yield return F("flow[].from", Carries.Structure, "Region id.");
        yield return F("flow[].to", Carries.Structure, "Region id.");
      }
      rows.Add(Row("label.clusters", "Reorganise (region labelling)",
        "Structure only: per region, its component types with counts and the flow between regions. No nicknames, no values, no geometry.",
        AssertedBy.RuntimeSuitePending, Label(), VerbWait));

      IEnumerable<PayloadField> Simplify()
      {
        yield return F("action", Carries.Structure, "simplify.betterlogic.");
        yield return F("focus", Carries.Structure, "The selected component ids.");
        yield return F("focus[]", Carries.Structure, "One id.");
        yield return F("nodes", Carries.Structure, "The selection.");
        yield return F("nodes[]", Carries.Structure, "One component.");
        foreach (var f in NodeBrief("nodes[]", hints: false)) yield return f;
        yield return F("nodes[].internal", Carries.Fact, "A FLAG that the component holds internalised data. The data itself is not sent.");
        yield return F("nodes[].ms", Carries.Fact, "The time the component took the last time it computed, in coarse 5 ms buckets.");
        yield return F("nodes[].source", Carries.Text, "A script component's source, verbatim.", "12,000 chars; over it the script is sent as opaque");
        yield return F("nodes[].opaque", Carries.Fact, "The component's logic cannot be read (a cluster, a plugin, an unreadable script).");
        foreach (var f in EdgeList("edges", "The wires inside the selection.", withSig: false)) yield return f;
        foreach (var f in Trees()) yield return f;
        yield return F("boundary", Carries.Structure, "The wires crossing the selection's edge.");
        yield return F("boundary.inputs", Carries.Structure, "Crossings in.");
        yield return F("boundary.inputs[]", Carries.Structure, "One crossing.");
        yield return F("boundary.inputs[].ext", Carries.Structure, "Crossing index.");
        yield return F("boundary.inputs[].to", Carries.Structure, "Where it lands.");
        yield return F("boundary.inputs[].to.node", Carries.Structure, "Component id.");
        yield return F("boundary.inputs[].to.param", Carries.Structure, "Port name.");
        foreach (var f in Sig("boundary.inputs[].sig")) yield return f;
        yield return F("boundary.outputs", Carries.Structure, "Crossings out.");
        yield return F("boundary.outputs[]", Carries.Structure, "One crossing.");
        yield return F("boundary.outputs[].ext", Carries.Structure, "Crossing index.");
        yield return F("boundary.outputs[].from", Carries.Structure, "Where it leaves.");
        yield return F("boundary.outputs[].from.node", Carries.Structure, "Component id.");
        yield return F("boundary.outputs[].from.param", Carries.Structure, "Port name.");
        foreach (var f in Sig("boundary.outputs[].sig")) yield return f;
      }
      rows.Add(Row("simplify.betterlogic", "Simplify (logic)",
        "Structure, the source of any script component in the selection (your code), a flag saying a component holds internalised data, and coarse solve-time buckets. NOT the values: the flag is not the number.",
        AssertedBy.RuntimeSuitePending, Simplify(), VerbWait));

      IEnumerable<PayloadField> ToScript()
      {
        yield return F("action", Carries.Structure, "simplify.toscript.");
        yield return F("language", Carries.Structure, "csharp | python3.");
        yield return F("focus", Carries.Structure, "The selected component ids.");
        yield return F("focus[]", Carries.Structure, "One id.");
        yield return F("nodes", Carries.Structure, "The selection.");
        yield return F("nodes[]", Carries.Structure, "One component.");
        foreach (var f in NodeBrief("nodes[]", hints: false)) yield return f;
        yield return F("nodes[].source", Carries.Text, "A script component's source, verbatim.", "12,000 chars; over it the collapse is refused");
        yield return F("nodes[].opaque", Carries.Fact, "The component's logic cannot be read.");
        foreach (var f in EdgeList("edges", "The wires inside the selection.", withSig: false)) yield return f;
        foreach (var f in Trees()) yield return f;
        yield return F("constants", Carries.Structure, "The constants inside the selection (a slider, a panel, an internalised param).");
        yield return F("constants[]", Carries.Structure, "One constant.");
        yield return F("constants[].node", Carries.Structure, "Component id.");
        yield return F("constants[].of", Carries.Text, "Which component and port, by name.");
        yield return F("constants[].value", Carries.Value, "THE VALUE, as a literal - a slider's 0.75, a panel's text, an internalised point.", "text trimmed to 60 chars");
        yield return F("constants[].feeds", Carries.Structure, "Which port the constant feeds, by name.");
        yield return F("interface", Carries.Structure, "The script's computed interface.");
        foreach (var side in new[] { "inputs", "outputs" })
        {
          yield return F("interface." + side, Carries.Structure, "Ports on this side.");
          yield return F("interface." + side + "[]", Carries.Structure, "One port.");
          yield return F("interface." + side + "[].index", Carries.Structure, "Position.");
          yield return F("interface." + side + "[].ghType", Carries.Structure, "Grasshopper type.");
          yield return F("interface." + side + "[].scriptType", Carries.Structure, "Declared type in the script language.");
          yield return F("interface." + side + "[].access", Carries.Structure, "item | list | tree.");
          yield return F("interface." + side + "[].connects", Carries.Structure, "What it connects to, by component and port name.");
          yield return F("interface." + side + "[].runtime", Carries.Fact, "The concrete type on the wire, described (closed planar polyline, 4 segments).");
          yield return F("interface." + side + "[].constant", Carries.Value, "The constant on this port, when its source is a constant.", "one line");
        }
      }
      rows.Add(Row("simplify.toscript", "Simplify to-script (C# / Python)",
        "Structure, script sources (your code), and the CONSTANT VALUES on the selection's boundary and inside it - a slider's number, a panel's text - because a script has no components to hold them. This is the one verb that sends values.",
        AssertedBy.RuntimeSuitePending, ToScript(), VerbWait));

      // ---- the agent tools (unit-tested) --------------------------------------------------------------------

      // Brief DX. The field names are Anthropic's, the wire the test compares; the other three families carry the same two facts in their own shapes, each pinned by a body-shape test.
      rows.Add(Row("chat:image", "Build chat: an image you attach",
        "That image, downscaled to 1568 px on its longest edge, with the turn you attach it to and with every later turn of that thread, because the conversation is sent whole each time. Never a capture Dwight took itself.",
        AssertedBy.UnitTest, new[]
        {
          F("source", Carries.Structure, "How the image travels."),
          F("source.type", Carries.Structure, "base64."),
          F("source.media_type", Carries.Fact, "PNG or JPEG."),
          F("source.data", Carries.Value, "The image you attached, drawn again by Dwight, so what the file carried beside its pixels stays behind.", "1568 px on the longest edge; one image a turn"),
        }));

      rows.Add(Row("tool:envelope", "Build chat: every tool result's envelope",
        "Whether the call succeeded, the result, or a coded refusal with its message and detail (port names, real lists).",
        AssertedBy.UnitTest, new[]
        {
          F("ok", Carries.Structure, "Success or refusal."),
          F("result", Carries.Structure, "The tool's result (its own row)."),
          F("error", Carries.Structure, "The refusal."),
          F("error.code", Carries.Structure, "A closed-set refusal code."),
          F("error.message", Carries.Structure, "Dwight's words on what to do differently."),
          F("error.detail", Carries.Structure, "The real list the model may choose from (ports, types, ids)."),
        }));

      IEnumerable<PayloadField> Overview()
      {
        yield return F("counts", Carries.Structure, "Totals.");
        yield return F("counts.components", Carries.Structure, "Component count.");
        yield return F("counts.params", Carries.Structure, "Param count.");
        yield return F("counts.edges", Carries.Structure, "Wire count.");
        yield return F("counts.regions", Carries.Structure, "Region count.");
        yield return F("counts.insideClusters", Carries.Structure, "Objects folded away inside Grasshopper Cluster components, not counted above.");
        yield return F("regions", Carries.Structure, "The regions - Dwight's reading groups, cut from the dataflow; never Grasshopper Cluster components.");
        yield return F("regions[]", Carries.Structure, "One region.");
        yield return F("regions[].id", Carries.Structure, "Region id.");
        yield return F("regions[].label", Carries.Text, "A cached label, or the generic \"Region N\".");
        yield return F("regions[].size", Carries.Structure, "Members.");
        yield return F("regions[].types", Carries.Structure, "Component types in the region, with counts.");
        yield return F("regions[].types.*", Carries.Structure, "One type's count.");
        yield return F("flow", Carries.Structure, "Which regions feed which.");
        yield return F("flow[]", Carries.Structure, "One flow edge.");
        yield return F("flow[].from", Carries.Structure, "Region id.");
        yield return F("flow[].to", Carries.Structure, "Region id.");
        yield return F("partition", Carries.Structure, "A hash of the topology the regions were computed against.");
        yield return F("means", Carries.Structure, "Dwight's words on what each figure counts.");
        yield return F("means.counts", Carries.Structure, "What the totals count.");
        yield return F("means.regions", Carries.Structure, "What a region is - and that it is not a Grasshopper Cluster.");
        yield return F("means.size", Carries.Structure, "What a region's size counts.");
        yield return F("means.types", Carries.Structure, "What a region's type histogram counts.");
      }
      rows.Add(Row("tool:get_overview", "Build chat: get_overview", "Structure: counts, the regions with their types and labels, the flow between them.", AssertedBy.UnitTest, Overview()));

      IEnumerable<PayloadField> Region()
      {
        yield return F("id", Carries.Structure, "Region id.");
        yield return F("label", Carries.Text, "A cached label, or a generic one.");
        yield return F("nodes", Carries.Structure, "The region's components.");
        yield return F("nodes[]", Carries.Structure, "One component.");
        foreach (var f in NodeBrief("nodes[]", hints: true)) yield return f;
        foreach (var f in EdgeList("edges", "Wires inside the region.", withSig: true)) yield return f;
        yield return F("boundary", Carries.Structure, "Wires crossing the region's edge.");
        foreach (var f in EdgeList("boundary.inputs", "Crossings in.", withSig: true)) yield return f;
        foreach (var f in EdgeList("boundary.outputs", "Crossings out.", withSig: true)) yield return f;
        yield return F("readRevision", Carries.Structure, "Which state of the canvas this read saw - a count of the changes the session's reads have seen, never a Grasshopper version.");
        yield return F("means", Carries.Structure, "Dwight's words on what a wire's signature and the readRevision count.");
        yield return F("means.sig", Carries.Structure, "What a wire's signature counts.");
        yield return F("means.readRevision", Carries.Structure, "What readRevision is: the canvas states this session's reads have seen; a move never raises it.");
      }
      rows.Add(Row("tool:get_region", "Build chat: get_region",
        "Structure plus a hint per component: a script's language, and the constant a slider or panel holds (one line).", AssertedBy.UnitTest, Region()));

      IEnumerable<PayloadField> Component()
      {
        yield return F("id", Carries.Structure, "Component id.");
        yield return F("type", Carries.Structure, "Type name.");
        yield return F("qualified", Carries.Structure, "Category > SubCategory > Name, only when the bare type name belongs to more than one installed component.");
        yield return F("nick", Carries.Text, "Your nickname for it, when set.");
        foreach (var f in Notes("")) yield return f;
        yield return F("kind", Carries.Structure, "component | param.");
        yield return F("inputs", Carries.Structure, "Input port names.");
        yield return F("inputs[]", Carries.Structure, "One port name.");
        yield return F("outputs", Carries.Structure, "Output port names.");
        yield return F("outputs[]", Carries.Structure, "One port name.");
        foreach (var f in Msgs("msgs")) yield return f;
        yield return F("outCounts", Carries.Fact, "Items per output.");
        yield return F("outCounts[]", Carries.Fact, "One output's item count.");
        yield return F("value", Carries.Value, "What a value-source component holds.");
        yield return F("value.kind", Carries.Structure, "slider | panel | toggle | valuelist | swatch.");
        yield return F("value.value", Carries.Value, "A slider's number.");
        yield return F("value.min", Carries.Value, "A slider's minimum.");
        yield return F("value.max", Carries.Value, "A slider's maximum.");
        yield return F("value.rounding", Carries.Fact, "float | integer | even | odd.");
        yield return F("value.decimals", Carries.Fact, "Decimal places.");
        yield return F("value.text", Carries.Text, "A panel's text, or a swatch's colour.", "400 chars, cut stated");
        yield return F("value.truncated", Carries.Structure, "The text was cut.");
        yield return F("value.chars", Carries.Fact, "The text's full length.");
        yield return F("value.displayed", Carries.Value, "A FED panel's displayed values, as text - what the wire into it carries, never its placeholder text.", "25 items / 400 chars, cut stated");
        yield return F("value.displayed[]", Carries.Value, "One displayed item, as text.");
        yield return F("value.displayedCount", Carries.Fact, "How many items the fed panel displays in total.");
        yield return F("value.displayedPaths", Carries.Fact, "How many branches they sit in.");
        yield return F("value.displayedTruncated", Carries.Structure, "The displayed list was cut.");
        yield return F("value.displayedShown", Carries.Structure, "How many displayed items are listed when cut.");
        yield return F("value.on", Carries.Value, "A toggle's state.");
        yield return F("value.selected", Carries.Text, "A value list's selected item names.", "10 items");
        yield return F("value.selected[]", Carries.Text, "One selected item.");
        yield return F("value.options", Carries.Fact, "How many items the list offers.");
        yield return F("script", Carries.Structure, "What a script component holds.");
        yield return F("script.language", Carries.Fact, "csharp | python | ironpython | vb | script.");
        yield return F("script.source", Carries.Text, "The script's source, verbatim.", "200 lines / 8,000 chars, cut stated in the text and beside it");
        yield return F("script.lines", Carries.Fact, "Total lines.");
        yield return F("script.chars", Carries.Fact, "Total chars.");
        yield return F("script.truncated", Carries.Structure, "The source was cut.");
        yield return F("script.omitted", Carries.Structure, "What the cut left out, in words.");
        yield return F("script.unreadable", Carries.Fact, "The source could not be read (locked, encrypted, unknown).");
        yield return F("persistent", Carries.Structure, "Internalised data on the inputs.");
        yield return F("persistent[]", Carries.Structure, "One input's internalised data.");
        yield return F("persistent[].param", Carries.Structure, "Port name.");
        yield return F("persistent[].count", Carries.Fact, "Item count.");
        yield return F("persistent[].paths", Carries.Fact, "Branch count.");
        yield return F("persistent[].item", Carries.Value, "The first item's concrete type with its value where it is a primitive (double (0.75), Point3d (0,0,5)). Geometry is described by class, never serialised.", "one line");
        yield return F("plugin", Carries.Structure, "For a plugin component: where it comes from.");
        yield return F("plugin.library", Carries.Structure, "The installed plugin's name.");
        yield return F("plugin.does", Carries.Structure, "The component's own SDK description.");
        foreach (var f in ClusterBlock()) yield return f;
        yield return F("means", Carries.Structure, "Dwight's words on what each figure counts, for the facts this component carries.");
        yield return F("means.outCounts", Carries.Structure, "What outCounts counts: flat items per output, across branches.");
        yield return F("means.qualified", Carries.Structure, "What the qualified form is and where to use it.");
        yield return F("means.displayed", Carries.Structure, "That a fed panel's displayed values are what it shows, not text the user typed.");
        yield return F("means.value", Carries.Structure, "What a value's chars and options count.");
        yield return F("means.persistent", Carries.Structure, "What internalised data's count and paths count.");
        yield return F("means.script", Carries.Structure, "What a script's lines and chars count.");
        yield return F("means.cluster", Carries.Structure, "What a cluster's count and omitted count.");
        yield return F("readRevision", Carries.Structure, "Which state of the canvas this read saw - a count of the changes the session's reads have seen, never a Grasshopper version.");
        yield return F("means.readRevision", Carries.Structure, "What readRevision is: the canvas states this session's reads have seen; a move never raises it.");
      }
      rows.Add(Row("tool:get_component", "Build chat: get_component",
        "Structure, your nickname, Grasshopper's runtime messages, and what the component HOLDS: a slider's value and domain, a panel's text (or, for a panel fed by a wire, the values it displays, as text), a toggle's state, a value list's selection, internalised data as a type with its value and a count, a script's source, a plugin's name.",
        AssertedBy.UnitTest, Component()));

      IEnumerable<PayloadField> Connections()
      {
        yield return F("id", Carries.Structure, "Component id.");
        foreach (var f in EdgeList("upstream", "Wires into it.", withSig: true)) yield return f;
        foreach (var f in EdgeList("downstream", "Wires out of it.", withSig: true)) yield return f;
        yield return F("means", Carries.Structure, "Dwight's words on what a wire's signature counts.");
      }
      rows.Add(Row("tool:get_connections", "Build chat: get_connections", "Structure: the wires in and out, with tree signatures.", AssertedBy.UnitTest, Connections()));

      IEnumerable<PayloadField> Cluster()
      {
        yield return F("id", Carries.Structure, "The cluster's component id.");
        yield return F("nick", Carries.Text, "Your nickname for the cluster.");
        yield return F("locked", Carries.Fact, "The cluster is disabled on the canvas - or \"unmeasured (reason)\" when the read failed; never false for a failed read.");
        yield return F("protected", Carries.Fact, "Password-protected: not read.");
        yield return F("level", Carries.Fact, "Grasshopper's protection level, by name.");
        yield return F("unreadable", Carries.Fact, "The internal document could not be read.");
        yield return F("why", Carries.Structure, "Dwight's words on why it was not read.");
        yield return F("count", Carries.Fact, "Internal objects.");
        yield return F("means", Carries.Structure, "Dwight's words on what each figure counts.");
        yield return F("means.count", Carries.Structure, "What count counts: objects one level inside.");
        yield return F("inputs", Carries.Structure, "The cluster's input port names.");
        yield return F("inputs[]", Carries.Structure, "One name.");
        yield return F("outputs", Carries.Structure, "The cluster's output port names.");
        yield return F("outputs[]", Carries.Structure, "One name.");
        yield return F("nodes", Carries.Structure, "The internals.", "200 a cluster");
        yield return F("nodes[]", Carries.Structure, "One internal component.");
        foreach (var f in NodeBrief("nodes[]", hints: true, inside: true)) yield return f;
        foreach (var f in EdgeList("edges", "Wires inside the cluster.", withSig: true)) yield return f;
        yield return F("hooks", Carries.Structure, "The internal hook params, by side.");
        foreach (var side in new[] { "inputs", "outputs" })
        {
          yield return F("hooks." + side, Carries.Structure, "Hooks on this side.");
          yield return F("hooks." + side + "[]", Carries.Structure, "One hook.");
          yield return F("hooks." + side + "[].id", Carries.Structure, "The hook's component id.");
          yield return F("hooks." + side + "[].name", Carries.Structure, "The hook's name, matching the cluster's port.");
        }
        yield return F("omitted", Carries.Structure, "How many internals were not projected.");
      }
      rows.Add(Row("tool:get_cluster", "Build chat: get_cluster",
        "Structure plus the same per-component hints get_region carries, for what is inside one cluster; a password-protected cluster is reported, never read.", AssertedBy.UnitTest, Cluster()));

      IEnumerable<PayloadField> TreeSignature()
      {
        yield return F("empty", Carries.Fact, "The wire carries no data.");
        yield return F("paths", Carries.Structure, "Branch count.");
        yield return F("depth", Carries.Structure, "Path depth.");
        yield return F("items", Carries.Structure, "Items per branch.", "first 24 branches");
        yield return F("items[]", Carries.Structure, "One branch's item count.");
        yield return F("means", Carries.Structure, "Dwight's words on what paths, depth and items count - or that the wire carried nothing.");
      }
      rows.Add(Row("tool:get_tree_signature", "Build chat: get_tree_signature", "Structure only: the signature on one wire, never its contents.", AssertedBy.UnitTest, TreeSignature()));

      IEnumerable<PayloadField> Search()
      {
        yield return F("matches", Carries.Structure, "Matching components.", "100, truncation stated");
        yield return F("matches[]", Carries.Structure, "One match.");
        foreach (var f in NodeBrief("matches[]", hints: true, inside: true)) yield return f;
        yield return F("count", Carries.Structure, "Total matches.");
        yield return F("truncated", Carries.Structure, "The list was cut at the cap.");
        yield return F("shown", Carries.Structure, "Matches listed.");
        yield return F("means", Carries.Structure, "Dwight's words on what each figure counts.");
        yield return F("means.count", Carries.Structure, "What count counts: every match, listed or not.");
        yield return F("means.qualified", Carries.Structure, "What a match's qualified form is and where to use it - present when a match carries one.");
      }
      rows.Add(Row("tool:search", "Build chat: search", "Structure plus the per-component hints get_region carries.", AssertedBy.UnitTest, Search()));

      IEnumerable<PayloadField> DiagnoseTool()
      {
        yield return F("scope", Carries.Structure, "What was scanned.");
        yield return F("scanned", Carries.Structure, "Components scanned.");
        yield return F("faultCount", Carries.Fact, "Faults found.");
        yield return F("clean", Carries.Fact, "No faults.");
        yield return F("faults", Carries.Structure, "The faults, roots first.", "60, omission stated");
        yield return F("faults[]", Carries.Structure, "One fault.");
        yield return F("faults[].id", Carries.Structure, "Component id.");
        yield return F("faults[].type", Carries.Structure, "Type name.");
        yield return F("faults[].kind", Carries.Structure, "error | warning | silent.");
        yield return F("faults[].root", Carries.Fact, "Upstream-most in its chain.");
        yield return F("faults[].intrinsic", Carries.Fact, "Its own failure, not starvation.");
        yield return F("faults[].nick", Carries.Text, "Your nickname for it.");
        yield return F("faults[].message", Carries.Text, "Grasshopper's first runtime message for it, verbatim.");
        yield return F("strays", Carries.Fact, "Components outside the scope with faults.");
        yield return F("means", Carries.Structure, "Dwight's words on how to read the list.");
        yield return F("omitted", Carries.Structure, "How many faults were not listed.");
      }
      rows.Add(Row("tool:diagnose", "Build chat: diagnose", "Structure plus Grasshopper's own runtime message per fault, verbatim.", AssertedBy.UnitTest, DiagnoseTool()));

      IEnumerable<PayloadField> Summary()
      {
        foreach (var f in Envelope()) yield return f;
        foreach (var f in Bbox()) yield return f;
        yield return F("objects[].valid", Carries.Fact, "RhinoCommon's validity verdict.");
        yield return F("objects[].closed", Carries.Fact, "Closed (mesh, curve, surface) or solid (brep).");
        yield return F("objects[].solid", Carries.Fact, "A brep is solid.");
        yield return F("objects[].planar", Carries.Fact, "A curve is planar.");
        yield return F("objects[].faces", Carries.Fact, "Face count.");
        yield return F("objects[].vertices", Carries.Fact, "Vertex count.");
        yield return F("objects[].edges", Carries.Fact, "Edge count.");
        yield return F("objects[].segments", Carries.Fact, "Polyline or polycurve segments.");
        yield return F("objects[].degree", Carries.Fact, "Curve degree.");
        yield return F("objects[].length", Carries.Fact, "Curve length.");
        yield return F("objects[].area", Carries.Fact, "Surface area.");
        yield return F("objects[].volume", Carries.Fact, "Volume, when closed.");
      }
      rows.Add(Row("tool:get_geometry_summary", "Build chat: get_geometry_summary",
        "Geometry FACTS per object: type, bounding box, validity, closed/solid, counts, length/area/volume. Never the geometry.", AssertedBy.UnitTest, Summary()));

      IEnumerable<PayloadField> CheckMeshRow()
      {
        foreach (var f in Envelope()) yield return f;
        foreach (var f in ValidLog()) yield return f;
        yield return F("objects[].closed", Carries.Fact, "The mesh is closed.");
        yield return F("objects[].faces", Carries.Fact, "Face count.");
        yield return F("objects[].vertices", Carries.Fact, "Vertex count.");
        yield return F("objects[].checkFailed", Carries.Structure, "Why Mesh.Check could not run, verbatim from the exception.");
        yield return F("objects[].counts", Carries.Fact, "Mesh.Check's count per defect class, named as this Rhino names them.");
        yield return F("objects[].counts.*", Carries.Fact, "One defect class's count.");
        yield return F("objects[].checked", Carries.Structure, "The checks that were switched on.");
        yield return F("objects[].checked[]", Carries.Structure, "One check's name.");
        yield return F("caveat", Carries.Structure, "Rhino's own limit on self-intersection detection.");
      }
      rows.Add(Row("tool:check_mesh", "Build chat: check_mesh", "Geometry facts: validity with Rhino's log, and a count per defect class. Never the geometry.", AssertedBy.UnitTest, CheckMeshRow()));

      IEnumerable<PayloadField> CheckBrepRow()
      {
        foreach (var f in Envelope()) yield return f;
        foreach (var f in ValidLog()) yield return f;
        yield return F("objects[].manifold", Carries.Fact, "Manifold.");
        yield return F("objects[].solid", Carries.Fact, "Solid.");
        yield return F("objects[].faces", Carries.Fact, "Face count.");
        yield return F("objects[].edges", Carries.Fact, "Edge count.");
        yield return F("objects[].nakedEdges", Carries.Fact, "Naked (open) edge count.");
      }
      rows.Add(Row("tool:check_brep", "Build chat: check_brep", "Geometry facts: validity with Rhino's log, manifold, solid, counts. Never the geometry.", AssertedBy.UnitTest, CheckBrepRow()));

      IEnumerable<PayloadField> NakedRow()
      {
        foreach (var f in Envelope()) yield return f;
        yield return F("sampleCap", Carries.Structure, "The sample cap, when more was asked.");
        yield return F("objects[].nakedEdges", Carries.Fact, "Naked edge count.");
        yield return F("objects[].sample", Carries.Fact, "Representative locations (edge midpoints).", "10 points an object");
        yield return F("objects[].sample[]", Carries.Fact, "One location.", "3 numbers");
        yield return F("objects[].sample[][]", Carries.Fact, "One coordinate.");
        yield return F("objects[].sampleOmitted", Carries.Structure, "How many locations were not shown.");
      }
      rows.Add(Row("tool:get_naked_edges", "Build chat: get_naked_edges", "Geometry facts: a naked-edge count per object and at most ten sample locations. Never the edge curves.", AssertedBy.UnitTest, NakedRow()));

      IEnumerable<PayloadField> MassRow()
      {
        foreach (var f in Envelope()) yield return f;
        yield return F("objects[].length", Carries.Fact, "Curve length.");
        yield return F("objects[].area", Carries.Fact, "Area.");
        yield return F("objects[].volume", Carries.Fact, "Volume.");
        yield return F("objects[].centroid", Carries.Fact, "Centroid.", "3 numbers");
        yield return F("objects[].centroid[]", Carries.Fact, "One coordinate.");
      }
      rows.Add(Row("tool:get_mass_properties", "Build chat: get_mass_properties", "Geometry facts: length, area, volume and one centroid per object.", AssertedBy.UnitTest, MassRow()));

      rows.Add(Row("tool:find_intersecting", "Build chat: find_intersecting",
        "Geometry facts: which pairs of objects intersect, as indices and a yes/no per tested pair, with the funnel of how many were tested.", AssertedBy.UnitTest, new[]
        {
          F("candidatePairs", Carries.Fact, "Pairs considered."),
          F("afterBoundingBoxFilter", Carries.Fact, "Pairs whose boxes overlap."),
          F("tested", Carries.Fact, "Pairs the true test answered."),
          F("intersecting", Carries.Fact, "Pairs that intersect."),
          F("pairs", Carries.Structure, "The survivors.", "200 a call"),
          F("pairs[]", Carries.Structure, "One pair."),
          F("pairs[].a", Carries.Structure, "Object index."),
          F("pairs[].b", Carries.Structure, "Object index."),
          F("pairs[].kind", Carries.Fact, "Which test applied (mesh-mesh, brep-brep...)."),
          F("pairs[].intersects", Carries.Fact, "The verdict, for a tested pair."),
          F("pairs[].testFailed", Carries.Structure, "Why an untested pair has no verdict."),
          F("capped", Carries.Structure, "The pair list stopped at the cap."),
          F("omitted", Carries.Structure, "What to do about the cap."),
        }));

      rows.Add(Row("tool:spatial_query", "Build chat: spatial_query",
        "Geometry facts: which objects are near a point, inside a box or overlapping another object, as indices and distances.", AssertedBy.UnitTest, new[]
        {
          F("query", Carries.Structure, "near | within | overlapping."),
          F("searched", Carries.Fact, "Objects searched."),
          F("shown", Carries.Structure, "Hits listed."),
          F("hits", Carries.Structure, "The hits, nearest first.", "limit, at most 25"),
          F("hits[]", Carries.Structure, "One hit."),
          F("hits[].i", Carries.Structure, "Object index."),
          F("hits[].distance", Carries.Fact, "Distance from the query."),
          F("omitted", Carries.Structure, "How many hits were not listed."),
        }));

      // Phase-9 U23 (lane C). The only VALUE-like numbers are solve times, and they leave only as 5 ms buckets
      // (SolveTimeBucket - the same rounding Simplify's nodes[].ms uses, so the two surfaces cannot disagree) and
      // as whole-percent shares. A nickname and a region label are the user's own text, declared as such.
      rows.Add(Row("tool:get_solve_timing", "Build chat: get_solve_timing",
        "Where the solve time goes: the slowest components by type, id and your nickname, each with the time it took the last time it computed (in 5 ms buckets), and each region's share of the recorded time. Never your geometry or values.",
        AssertedBy.UnitTest, new[]
        {
          F("measures", Carries.Structure, "What the figures mean and what they do not measure, in Dwight's own words."),
          F("recorded", Carries.Structure, "How many components have recorded a solve time."),
          F("negligible", Carries.Structure, "How many of those were too quick to register in 5 ms buckets."),
          F("notRecorded", Carries.Structure, "How many components have not computed."),
          F("partition", Carries.Structure, "The region partition the region ids belong to."),
          F("nothingRecorded", Carries.Structure, "Dwight's note, when nothing has computed yet."),
          F("allNegligible", Carries.Structure, "Dwight's note, when nothing that computed registers."),
          F("components", Carries.Structure, "The slowest components, slowest first.", "limit, at most 25"),
          F("components[]", Carries.Structure, "One component."),
          F("components[].id", Carries.Structure, "Component id."),
          F("components[].type", Carries.Structure, "Component type name."),
          F("components[].nick", Carries.Text, "Your nickname for it, when set."),
          F("components[].ms", Carries.Fact, "The time it took the last time it computed, in coarse 5 ms buckets."),
          F("omitted", Carries.Structure, "How many measurable components were not listed."),
          F("regions", Carries.Structure, "The regions holding the most recorded time.", "8"),
          F("regions[]", Carries.Structure, "One region."),
          F("regions[].id", Carries.Structure, "Region id."),
          F("regions[].label", Carries.Text, "A cached label, or a generic one."),
          F("regions[].ms", Carries.Fact, "The region's recorded time, in coarse 5 ms buckets."),
          F("regions[].share", Carries.Fact, "The region's share of all recorded time, as a whole percentage."),
          F("regionsOmitted", Carries.Structure, "How many regions with recorded time were not listed."),
        }));

      // Phase-9 U23 (lane C). Document settings, not the user's data: they are Facts about the model, and the
      // document is described in words - its name and path are never read into the payload.
      rows.Add(Row("tool:get_document_units", "Build chat: get_document_units",
        "The units and tolerances of Rhino's active document. Never the document's name or path.",
        AssertedBy.UnitTest, new[]
        {
          F("document", Carries.Structure, "Which document was read, in Dwight's own words."),
          F("units", Carries.Fact, "The model's unit system (for example Millimeters). A custom unit's name is not sent."),
          F("absoluteTolerance", Carries.Fact, "The model's absolute tolerance, in those units."),
          F("angleToleranceDegrees", Carries.Fact, "The model's angle tolerance, in degrees."),
          F("measures", Carries.Structure, "What the numbers apply to, in Dwight's own words."),
        }));

      // Phase-9 U23 (lane C). WHAT IS WITHHELD IS PART OF THE ROW: a library's location (a path carrying your Windows
      // user name), its author contact (an email), its id (a GUID) and its description are never read into the
      // payload. Name and version are Structure, like component types (the architect's ruling).
      rows.Add(Row("tool:get_installed_plugins", "Build chat: get_installed_plugins",
        "The Grasshopper plugins you have loaded: each one's name, version, how many components it provides and how many objects on this canvas come from it. A plugin installed as user objects is named by its ribbon category, which is whatever that category is called on your machine. Never where it is installed, its author's contact or its id.",
        AssertedBy.UnitTest, new[]
        {
          F("bound", Carries.Structure, "What the list cannot contain, in Dwight's own words."),
          F("installed", Carries.Structure, "How many plugins are loaded."),
          F("core", Carries.Structure, "Grasshopper's own libraries, as one entry."),
          F("core.libraries", Carries.Structure, "How many core libraries."),
          F("core.components", Carries.Structure, "How many components they provide."),
          F("core.usedHere", Carries.Structure, "How many objects on this canvas come from them."),
          F("plugins", Carries.Structure, "The plugins, the ones used here first.", "80"),
          F("plugins[]", Carries.Structure, "One plugin."),
          F("plugins[].name", Carries.Structure, "The plugin's name, as it gives it, or the ribbon category of its user objects."),
          F("plugins[].userObjects", Carries.Structure, "How many of its components are user objects, when any are."),
          F("plugins[].version", Carries.Structure, "Its version, when it gives one."),
          F("plugins[].components", Carries.Structure, "How many components it provides."),
          F("plugins[].usedHere", Carries.Structure, "How many objects on this canvas come from it."),
          F("omitted", Carries.Structure, "How many plugins were not listed."),
        }));

      // Phase-9 U23 (lane C). Two booleans and a sentence. The file's name and path are never READ, not merely not
      // sent - the architect's ruling for file metadata, and a line Tharun may loosen.
      rows.Add(Row("tool:get_file_state", "Build chat: get_file_state",
        "Whether your definition is saved, and whether it has changes since it was last saved. Never its file name or path.",
        AssertedBy.UnitTest, new[]
        {
          F("saved", Carries.Structure, "Whether the definition has a file on disk."),
          F("modifiedSinceSave", Carries.Structure, "Whether it has changes since the last save - only when it has been saved."),
          F("state", Carries.Structure, "The same, in Dwight's own words."),
        }));

      // Brief EE unit 4. What comes back is the component ids it lit; the glow is drawn on your screen and nothing about it leaves.
      rows.Add(Row("tool:show_on_canvas", "Build chat: show_on_canvas",
        "Which components the model lit on your canvas to show you what it is explaining. It points at them on your screen and changes nothing in your definition.",
        AssertedBy.UnitTest, new[]
        {
          F("shown", Carries.Structure, "The component ids that were lit.", "60"),
          F("shown[]", Carries.Structure, "One component id."),
          F("dimmed", Carries.Structure, "Whether the rest of the canvas was washed."),
          F("panned", Carries.Structure, "Whether the view moved to show them."),
        }));

      // Phase-9 U23 (lane C). Relations only - no coordinate, position or size leaves (ADR-058); positions are read
      // to decide them. Group names are the user's own text, like nicknames: the words INSIDE the definition leave,
      // what identifies WHOSE definition it is does not (the architect's line).
      rows.Add(Row("tool:get_canvas_layout", "Build chat: get_canvas_layout",
        "How your canvas is laid out, as facts rather than positions: your group names and which components each holds, how groups sit relative to one another, which components or groups overlap, how many components are in no group, and which wires run backwards. Never a coordinate.",
        AssertedBy.UnitTest, new[]
        {
          F("note", Carries.Structure, "What the group numbers are and are not, in Dwight's own words."),
          F("groups", Carries.Structure, "The groups, left to right.", "30"),
          F("groups[]", Carries.Structure, "One group."),
          F("groups[].n", Carries.Structure, "Its position in this list - not an id."),
          F("groups[].name", Carries.Text, "Your name for the group."),
          F("groups[].address", Carries.Structure, "The region address, for one of Dwight's scaffold regions."),
          F("groups[].members", Carries.Structure, "The component ids it holds.", "50"),
          F("groups[].members[]", Carries.Structure, "One component id."),
          F("groups[].membersOmitted", Carries.Structure, "How many more components it holds."),
          F("groups[].otherObjects", Carries.Structure, "How many other things it holds (notes, nested groups)."),
          F("groups[].left", Carries.Structure, "The nearest group to its left, by number."),
          F("groups[].right", Carries.Structure, "The nearest group to its right, by number."),
          F("groups[].above", Carries.Structure, "The nearest group above it, by number."),
          F("groups[].below", Carries.Structure, "The nearest group below it, by number."),
          F("groupsOmitted", Carries.Structure, "How many groups were not listed."),
          F("ungrouped", Carries.Structure, "How many components are in no group."),
          F("overlaps", Carries.Structure, "Pairs of components drawn over one another.", "25"),
          F("overlaps[]", Carries.Structure, "One pair."),
          F("overlaps[].a", Carries.Structure, "Component id."),
          F("overlaps[].b", Carries.Structure, "Component id."),
          F("overlapsTotal", Carries.Structure, "How many such pairs there are."),
          F("groupOverlaps", Carries.Structure, "Pairs of groups whose outlines cross.", "25"),
          F("groupOverlaps[]", Carries.Structure, "One pair."),
          F("groupOverlaps[].a", Carries.Structure, "Group number."),
          F("groupOverlaps[].b", Carries.Structure, "Group number."),
          F("groupOverlapsTotal", Carries.Structure, "How many such pairs there are."),
          F("backwardWires", Carries.Structure, "Wires running right to left, as component id pairs.", "25"),
          F("backwardWires[]", Carries.Structure, "One wire's two ends."),
          F("backwardWires[].from", Carries.Structure, "Source component id."),
          F("backwardWires[].to", Carries.Structure, "Target component id."),
          F("backwardWiresTotal", Carries.Structure, "How many such component pairs there are."),
          F("backwardMeans", Carries.Structure, "What a backward wire is, in Dwight's own words."),
        }));

      rows.Add(Row("tool:set_value", "Build chat: set_value (verdict)",
        "Structure plus the change itself: which component, what it held and what it holds now (a slider's value and domain, or a panel's text), and the two-leg verdict.",
        AssertedBy.UnitTest, new[]
        {
          F("status", Carries.Structure, "applied | reverted."),
          F("target", Carries.Structure, "The component changed."),
          F("target.id", Carries.Structure, "Component id."),
          F("target.type", Carries.Structure, "Type name."),
          F("target.nick", Carries.Text, "Your nickname for it."),
          F("before", Carries.Value, "What it held, as one line.", "text trimmed to 60 chars"),
          F("after", Carries.Value, "What it holds now, as one line.", "text trimmed to 60 chars"),
          F("preserved", Carries.Fact, "Nothing else on the canvas changed."),
          F("complete", Carries.Fact, "The component reads back exactly the declared state."),
          F("proves", Carries.Structure, "Dwight's words on what the two legs prove."),
          F("doesNotProve", Carries.Structure, "Dwight's words on what they do not - the domain gap and the one-component scope."),
          F("undo", Carries.Structure, "How to undo."),
          F("mismatch", Carries.Value, "On a revert: the first difference, in the user's terms (may quote the value read back)."),
          F("note", Carries.Structure, "On a revert: the canvas is as it was."),
        }));

      // ---- the GH-side tools (pending: live-document readers) ---------------------------------------------

      const string ToolWait = "stream F's runtime-suite check over a scratch document: call the tool live and assert its emitted paths equal this row.";

      // U25 (#915): the projection moved GH-free (KbLookupReport), so this row is asserted against the writer's real
      // output in PayloadContractTests; the ports function is the one GH-coupled part and is handed in.
      rows.Add(Row("tool:kb_lookup", "Build chat: kb_lookup", "Structure: an installed component's name, category and its own SDK description; its ports, read from an instance on the canvas or from a temporary instance that never joins the definition. A name shared by more than one installed component lists each with its qualified form.",
        AssertedBy.UnitTest, new[]
        {
          F("name", Carries.Structure, "Type name."), F("qualified", Carries.Structure, "Category > SubCategory > Name."),
          F("category", Carries.Structure, "Category."), F("subCategory", Carries.Structure, "Sub-category."),
          F("does", Carries.Structure, "The SDK description."), F("installed", Carries.Structure, "true."),
          F("ports", Carries.Structure, "Port names read off an instance, or \"unavailable\"."),
          F("portsFrom", Carries.Structure, "Where the ports came from: canvas, instantiated or unavailable."),
          F("portsNote", Carries.Structure, "Dwight's one sentence when an instance could not be made here."),
          F("ports.inputs", Carries.Structure, "Input names."), F("ports.inputs[]", Carries.Structure, "One name."),
          F("ports.outputs", Carries.Structure, "Output names."), F("ports.outputs[]", Carries.Structure, "One name."),
          F("ports.readFrom", Carries.Structure, "Where the ports were read."),
          F("asked", Carries.Structure, "The name asked for, when it was ambiguous."), F("ambiguous", Carries.Structure, "The bare name belongs to more than one installed component."),
          F("candidates", Carries.Structure, "Each installed component of that name."), F("candidates[]", Carries.Structure, "One candidate."),
          F("candidates[].qualified", Carries.Structure, "Category > SubCategory > Name."), F("candidates[].name", Carries.Structure, "Type name."),
          F("candidates[].category", Carries.Structure, "Category."), F("candidates[].subCategory", Carries.Structure, "Sub-category."),
          F("candidates[].does", Carries.Structure, "The SDK description."), F("candidates[].installed", Carries.Structure, "true."),
          F("candidates[].ports", Carries.Structure, "Port names read off an instance of THAT candidate, or \"unavailable\"."),
          F("candidates[].portsFrom", Carries.Structure, "Where the ports came from: canvas, instantiated or unavailable."),
          F("candidates[].portsNote", Carries.Structure, "Dwight's one sentence when an instance could not be made here."),
          F("candidates[].ports.inputs", Carries.Structure, "Input names."), F("candidates[].ports.inputs[]", Carries.Structure, "One name."),
          F("candidates[].ports.outputs", Carries.Structure, "Output names."), F("candidates[].ports.outputs[]", Carries.Structure, "One name."),
          F("candidates[].ports.readFrom", Carries.Structure, "Where the ports were read."),
          F("means", Carries.Structure, "Dwight's words on why the name is ambiguous and which form to use."),
        }));

      // U27 (#917): the projection moved GH-free (SolveCheckReport), so this row is now asserted against the
      // writer's real output in PayloadContractTests - and the row it replaces had guessed the nested shape wrong
      // (a "components[]" list that the payload never had; the lists are msgs[] and emptyOutputs[]).
      rows.Add(Row("tool:solve_and_check", "Build chat: solve_and_check", "Structure plus Grasshopper's runtime messages after a re-solve, verbatim, and per-output item counts.",
        AssertedBy.UnitTest, new[]
        {
          F("solved", Carries.Fact, "Whether this call ran a solve; false means nothing needed solving."),
          F("components", Carries.Structure, "Top-level objects on the canvas."),
          F("errors", Carries.Fact, "Error count."), F("warnings", Carries.Fact, "Warning count."),
          F("msgs", Carries.Structure, "Every runtime message, by component."), F("msgs[]", Carries.Structure, "One message."),
          F("msgs[].id", Carries.Structure, "Component id."), F("msgs[].type", Carries.Structure, "Type name."),
          F("msgs[].level", Carries.Structure, "Level."), F("msgs[].text", Carries.Text, "Grasshopper's own message, verbatim."),
          F("emptyOutputs", Carries.Fact, "Only the components with an output that produced nothing."), F("emptyOutputs[]", Carries.Structure, "One component."),
          F("emptyOutputs[].id", Carries.Structure, "Component id."), F("emptyOutputs[].type", Carries.Structure, "Type name."),
          F("emptyOutputs[].outCounts", Carries.Fact, "Items per output, flat across branches."), F("emptyOutputs[].outCounts[]", Carries.Fact, "One output's item count."),
          F("means", Carries.Structure, "Dwight's words on what each figure means."),
          F("means.solved", Carries.Structure, "What solved means - above all what false does NOT mean."),
          F("means.components", Carries.Structure, "What components counts."), F("means.errors", Carries.Structure, "What errors counts."),
          F("means.warnings", Carries.Structure, "What warnings counts."), F("means.msgs", Carries.Structure, "What msgs lists."),
          F("means.emptyOutputs", Carries.Structure, "What emptyOutputs lists and what it omits."), F("means.outCounts", Carries.Structure, "What outCounts counts."),
        }));

      rows.Add(Row("tool:build_region", "Build chat: build_region (verdict)", "Structure: the verdict on a build - what was placed and wired, what was preserved, what the verdict proves and does not, how to undo.",
        AssertedBy.RuntimeSuitePending, new[]
        {
          "status", "region", "placed", "wired", "wires", "components", "preserved", "complete", "preservation", "errors", "newErrors",
          "judgedAgainst", "proves", "doesNotProve", "note", "undo", "replaced", "replacedNote", "revertedComponents",
          "attemptsLeftForRegion", "initialState", "asked", "scriptsFromCard", "scriptNote",
        }.Select(k => F(k, k == "initialState" ? Carries.Value : Carries.Structure,
          k == "initialState" ? "The slider/panel state the build set, read back from the placed component." : "Verdict field (see BuildVerdict.cs).")),
        ToolWait + " Top-level keys read from BuildVerdict.cs; the nested shape is to be confirmed by that check."));

      rows.Add(Row("tool:propose_scaffold", "Build chat: propose_scaffold (verdict)", "Structure: the addresses assigned, what could not be placed, and the fingerprint verdict.",
        AssertedBy.RuntimeSuitePending,
        new[] { "applied", "regions", "droppedAnchors", "unplaceable", "reversedForReadingOrder", "preserved", "proves", "doesNotProve", "undo" }
          .Select(k => F(k, Carries.Structure, "Verdict field (see GhProposeScaffold.cs).")),
        ToolWait + " Top-level keys read from GhProposeScaffold.cs; the nested shape is to be confirmed by that check."));

      rows.Add(Row("tool:get_scaffold_map", "Build chat: get_scaffold_map", "Structure plus the plan's own text: each region's address, name, purpose, pseudo-code and declared inputs and outputs, as written on the canvas and in Dwight's record.",
        AssertedBy.RuntimeSuitePending, new[]
        {
          F("planAvailable", Carries.Structure, "A stored plan exists."), F("planKnown", Carries.Structure, "The plan was reconciled."),
          F("empty", Carries.Fact, "No map on the canvas."), F("regions", Carries.Structure, "The regions."), F("regions[]", Carries.Structure, "One region."),
          F("regions[].address", Carries.Structure, "A1, A2, B1 ..."), F("regions[].name", Carries.Text, "The region's label, as written."),
          F("regions[].purpose", Carries.Text, "Its purpose, as written."), F("regions[].pseudo", Carries.Text, "Its pseudo-code, as written."),
          F("regions[].takes", Carries.Text, "Declared inputs."), F("regions[].gives", Carries.Text, "Declared outputs."),
          F("regions[].dependsOn", Carries.Structure, "Addresses it depends on."), F("regions[].anchorIds", Carries.Structure, "Anchor component ids."),
          F("regions[].componentsInside", Carries.Fact, "Components in the group now."), F("regions[].onCanvas", Carries.Fact, "The group exists."),
          F("regions[].notes", Carries.Structure, "What could not be reconciled."),
        }, ToolWait + " Names read from GhScaffoldMap.cs; the nested shape is to be confirmed by that check."));

      // Full control (U90): present on a conversation whose switch is on. The script's source is the model's own text and the user read it on the
      // card; what goes back is a report of counts and what the script printed, bounded.
      rows.Add(Row("tool:run_script", "Build chat: run_script (Full control only)",
        "Counts of what changed (components, wires, groups, errors, and the Rhino document's objects and layers), whether the behaviour fingerprint changed, and what the script printed to its output, cut at 2,000 characters. Never the geometry.",
        AssertedBy.RuntimeSuitePending, new[]
        {
          F("ran", Carries.Fact, "The script ran."), F("raised", Carries.Fact, "The script raised an exception."),
          F("report", Carries.Value, "The change counts, the fingerprint line, and the script's printed output or its exception, verbatim."),
          F("seconds", Carries.Fact, "How long the script took."), F("note", Carries.Text, "Dwight's reminder that this is a report and not a check."),
          F("stillRunning", Carries.Fact, "The clock ran out and the script was still running."),
        }, ToolWait + " Fields read from FullControl.cs; the live shape is to be confirmed by that check."));

      return rows;
    }

    // ---- the renders ----------------------------------------------------------------------------------

    private static string Esc(string s) => WebUtility.HtmlEncode(s ?? "");

    /// <summary>The in-product "What Dwight sends" block. Plugin-authored HTML from the table alone - no
    /// user or model text enters it - and the rows not yet held to the code say so.</summary>
    public static string SettingsHtml()
    {
      var sb = new StringBuilder();
      var verbs = All().Where(r => r.Scope == Scope.Always).ToList();
      var tools = All().Where(r => r.Scope != Scope.Always).ToList();

      // Brief BJ: a table, not prose. WHAT GOES and WHEN are computed from each row's own fields and scope, so the page cannot say more or less than the
      // contract; a row's full sentence stays on it as its tooltip. There is no "what never goes" column: only what every field proves is stated, below.
      sb.Append("<p class=\"sendsnote\">Nothing is sent unless you run an action.</p>");
      sb.Append("<table class=\"sendstable\"><thead><tr><th>Action</th><th>What goes</th><th>When</th></tr></thead><tbody>");
      foreach (var r in verbs) SendsRow(sb, r, r.Title);
      sb.Append("<tr class=\"group\"><td colspan=\"3\">The Build chat</td></tr>");
      foreach (var r in tools) SendsRow(sb, r, r.Title.StartsWith(ChatPrefix, StringComparison.Ordinal) ? r.Title.Substring(ChatPrefix.Length) : r.Title);
      sb.Append("</tbody></table><p class=\"sendsnote\">");

      var chatValues = tools.Any(t => t.Fields.Any(f => f.Carries == Carries.Value));
      var chatText = tools.Any(t => t.Fields.Any(f => f.Carries == Carries.Text));
      sb.Append("<b>The Build chat</b> - sends what its reads return, <i>only for the components the model asks about</i>: structure");
      if (chatValues) sb.Append(", the values a slider, panel or internalised param holds");
      if (chatText) sb.Append(", a script's source and Grasshopper's runtime messages");
      sb.Append(", and geometry <i>facts</i> (bounding boxes, validity, counts, areas, at most ten sample points) - never the geometry itself. " +
                "A component the model never reads sends nothing. An image goes only if you attach it.</p><p class=\"sendsnote\">");

      // WHAT IS SENT, and nothing about how we test it (the architect's ruling on this block): AssertedBy
      // answers "will we notice if this changes", not "is this true today" - every row was read from the
      // code before it was written down - and a user cannot act on our regression coverage. The state
      // stays in the table and on the developer-facing renders.
      sb.Append(NeverLine).Append(" <b>With a local (Ollama) model, none of this leaves your machine at all.</b></p>");
      return sb.ToString();
    }

    private const string ChatPrefix = "Build chat: ";

    /// <summary>Stated because a test reads every field of every row and finds neither. File paths are not on this line: Diagnose forwards Grasshopper's own
    /// error text verbatim, and a component can put a path in it, so the claim would not be true.</summary>
    public const string NeverLine = "No row sends a component's guid or its position on the canvas.";

    /// <summary>"Structure, Facts": the kinds of thing this row's fields carry, in a fixed order, from the row itself.</summary>
    public static string WhatGoes(PayloadRow r)
    {
      var kinds = new List<string>();
      if (r.Fields.Any(f => f.Carries == Carries.Structure)) kinds.Add("Structure");
      if (r.Fields.Any(f => f.Carries == Carries.Fact)) kinds.Add("Facts");
      if (r.Fields.Any(f => f.Carries == Carries.Value)) kinds.Add("Values");
      if (r.Fields.Any(f => f.Carries == Carries.Text)) kinds.Add("Your text");
      return string.Join(", ", kinds);
    }

    public static string WhenGoes(PayloadRow r) => r.Scope == Scope.Always ? "Every run" : r.Scope == Scope.WhenAttached ? "If you attach one" : "If asked";

    private static void SendsRow(StringBuilder sb, PayloadRow r, string name) =>
      sb.Append("<tr title=\"").Append(Esc(r.Sends)).Append("\"><td>").Append(Esc(name)).Append("</td><td>").Append(Esc(WhatGoes(r))).Append("</td><td>").Append(Esc(WhenGoes(r))).Append("</td></tr>");

    /// <summary>
    /// The public page's source: every row and what each field carries. EMBEDDABLE and DETERMINISTIC by
    /// contract with stream D, whose page keeps its own H1 and delimits a generated section that a drift
    /// test compares byte-for-byte against this: no H1, sections at <paramref name="headingLevel"/>, no
    /// timestamps, list order only (never a dictionary's), invariant formatting. The assertion state is
    /// rendered only when <paramref name="includeAssertion"/> asks for it - a developer-facing detail, not
    /// a user-facing one (the same ruling as the Settings block).
    /// </summary>
    public static string Markdown(int headingLevel = 3, bool includeAssertion = false)
    {
      var h = new string('#', Math.Max(2, Math.Min(6, headingLevel)));
      var sb = new StringBuilder();
      sb.AppendLine("Each row is one payload; each field says what it CARRIES: " +
                    "**structure** (types, wires, ids, Dwight's own words), a **fact** about your data that does not scale with it, " +
                    "a **value** (your data, bounded), or **text** (your own words or code, verbatim and bounded).");
      sb.AppendLine();
      foreach (var r in All())
      {
        sb.Append(h).Append(' ').Append(r.Title).Append(" (`").Append(r.Surface).Append("`)");
        if (includeAssertion)
          sb.Append(" - asserted by: ").Append(r.AssertedBy == AssertedBy.UnitTest ? "unit test" : "runtime suite (pending)");
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine(r.Scope == Scope.Always
          ? "Sent every time this runs."
          : r.Scope == Scope.WhenAttached
          ? "Sent only when you attach it to a turn, and then with that thread's later turns."
          : "Sent only when the model calls this tool, and only for the components it names - a component it never reads sends nothing.");
        sb.AppendLine();
        sb.AppendLine(r.Sends);
        if (includeAssertion && !string.IsNullOrEmpty(r.WaitingFor)) { sb.AppendLine(); sb.Append("Waiting for: ").AppendLine(r.WaitingFor); }
        sb.AppendLine();
        sb.AppendLine("| field | carries | meaning | bound |");
        sb.AppendLine("|---|---|---|---|");
        foreach (var f in r.Fields)
          sb.Append("| `").Append(f.Path).Append("` | ").Append(f.Carries.ToString().ToLowerInvariant()).Append(" | ")
            .Append(f.Meaning.Replace("|", "\\|")).Append(" | ").Append(f.Bound ?? "").AppendLine(" |");
        sb.AppendLine();
      }
      return sb.ToString();
    }
  }

  /// <summary>
  /// The JSON PATHS a payload emits - the thing the contract is compared against. Objects contribute their
  /// keys (a null value still counts: the key left the machine); arrays contribute <c>name[]</c> when they
  /// hold anything, and their elements' paths beneath it. <see cref="Fold"/> collapses a map keyed by DATA
  /// (component type names as keys) into the wildcard the table declares for it.
  /// </summary>
  internal static class PayloadPaths
  {
    public static HashSet<string> Collect(JsonNode node)
    {
      var paths = new HashSet<string>(StringComparer.Ordinal);
      Walk(node, "", paths);
      return paths;
    }

    private static void Walk(JsonNode node, string prefix, HashSet<string> paths)
    {
      switch (node)
      {
        case JsonObject o:
          foreach (var kv in o)
          {
            var p = prefix.Length == 0 ? kv.Key : prefix + "." + kv.Key;
            paths.Add(p);
            Walk(kv.Value, p, paths);
          }
          break;
        case JsonArray a:
          if (a.Count == 0) break;
          var item = prefix + "[]";
          paths.Add(item);
          foreach (var x in a) Walk(x, item, paths);
          break;
      }
    }

    /// <summary>Replace every emitted path under a declared <c>prefix.*</c> by the wildcard itself.</summary>
    public static HashSet<string> Fold(IEnumerable<string> emitted, IEnumerable<string> declared)
    {
      var wildcards = (declared ?? Enumerable.Empty<string>())
        .Where(d => d.EndsWith(".*", StringComparison.Ordinal))
        .Select(d => d.Substring(0, d.Length - 1))   // keep the trailing dot
        .ToList();
      var result = new HashSet<string>(StringComparer.Ordinal);
      foreach (var e in emitted ?? Enumerable.Empty<string>())
      {
        var w = wildcards.FirstOrDefault(x => e.StartsWith(x, StringComparison.Ordinal) && e.Length > x.Length
                                              && e.IndexOf('[', x.Length) < 0);
        result.Add(w == null ? e : w + "*");
      }
      return result;
    }
  }
}
