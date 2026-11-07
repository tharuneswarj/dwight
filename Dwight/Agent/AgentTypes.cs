using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace Dwight.Agent
{
  /// <summary>
  /// The CLOSED refusal-code set (Phase-8 WP-2). A refusal is a structured RESULT the model can act on,
  /// never an exception - so every failure arrives in the same shape as a success and the agent loop
  /// cannot be derailed by a throw. The set is closed on purpose: an open-ended error string is
  /// unactionable, and a code the model has never seen is a code it cannot route on.
  /// </summary>
  internal static class ToolErrors
  {
    /// <summary>An id that was never issued in this scope - the model invented it.</summary>
    /// <summary>
    /// WHY a refusal happened, in one sentence a model can relay to a user without inventing anything.
    ///
    /// These are deliberately CAUSES and not restatements. The message already says what was refused; a
    /// cause that repeated it would leave exactly the gap the model filled with fiction. Null means the
    /// code needs no cause beyond its message.
    /// </summary>
    public static string CauseOf(string code)
    {
      switch (code)
      {
        case NoDocument:
          return "every one of Dwight's tools reads the canvas, and there is no open document to read";
        case StaleDocument:
          return "this conversation is bound to a document that is no longer the active one, and Dwight " +
                 "will not write to a canvas the user is not looking at";
        case Busy:
          return "another Dwight request is still running, and two changes in flight at once could not " +
                 "be undone as separate steps";
        case SolverDisabled:
          return "Grasshopper's solver is off, so nothing Dwight built could be verified - and an " +
                 "unverified build must never stand";
        case UnknownComponentType:
          return "Dwight only places components that are actually installed, so it never invents one";
        case UnknownId:
          return "ids come from this session's own reads; this one was never issued";
        case RetiredId:
          return "that id was real and its component has since been deleted from the canvas";
        case NotInThisRead:
          return "that id is alive in this session, but the read that answered did not cover where it lives - " +
                 "which is a different fact from having been deleted (#719)";
        case UnknownRegion:
          return "regions are read from the groups on the canvas, and no group carries that address";
        case RegionOccupied:
          return "Dwight removes only its own work that the user has left untouched";
        case RegionAttemptsExhausted:
          return "the per-region attempt budget for this turn is spent, and retrying past it would " +
                 "spend the user's money on the same failure";
        case RegionNotApproved:
          return "the user approved specific regions and this is not one of them";
        case VerificationFailed:
          return "the change was applied, failed its own check, and was reverted - so the canvas is " +
                 "exactly as it was";
        case StalePartition:
          return "the canvas changed after these region ids were issued, so they no longer point at " +
                 "what they did";
        case AlreadyApproved:
          return "the user has already approved this, and this turn is the approval being carried out";
        case BoundaryCut:
          return "a rebuild must keep every wire its removed components fed outside the region, reconnected to " +
                 "the new build, and Dwight cannot delete such a wire - only the user can";
        default:
          return null;   // invalid_args, precondition_refused and the like: the message IS the cause
      }
    }

    public const string UnknownId = "unknown_id";
    /// <summary>An id that WAS issued but whose object is no longer on the canvas (#191). Distinct from
    /// <see cref="UnknownId"/> on purpose: "you are addressing something that was deleted" is a
    /// different correction from "you made that up".</summary>
    public const string RetiredId = "retired_id";
    /// <summary>An id that is ALIVE in this session but absent from the projection this tool read - a component inside
    /// a cluster the read did not open, say. Distinct from <see cref="RetiredId"/> on purpose (#719): "deleted" and
    /// "not in this read" call for different corrections, and one of them was a false claim about the user's canvas.</summary>
    public const string NotInThisRead = "not_in_this_read";
    /// <summary>A component type no installed plugin provides. Carries the nearest installed names.</summary>
    public const string UnknownComponentType = "unknown_component_type";
    /// <summary>Structurally malformed arguments (missing field, wrong JSON type).</summary>
    public const string InvalidArgs = "invalid_args";
    /// <summary>No such tool on the surface.</summary>
    public const string UnknownTool = "unknown_tool";
    /// <summary>The document changed identity under the call (ADR-049 stale-doc guard).</summary>
    public const string StaleDocument = "stale_document";
    /// <summary>Another Dwight operation holds the session (ADR-072's one-operation rule).</summary>
    public const string Busy = "busy";
    /// <summary>The Grasshopper solver is disabled, so nothing can be verified (#109 - never claim a
    /// vacuous pass).</summary>
    public const string SolverDisabled = "solver_disabled";
    /// <summary>A deterministic pre-condition refused the request before any model or canvas work.</summary>
    public const string PreconditionRefused = "precondition_refused";
    /// <summary>A mutation was applied, failed verification, and was REVERTED. Carries the verdict.</summary>
    public const string VerificationFailed = "verification_failed";
    /// <summary>The region partition this id came from no longer describes the canvas (#195). Region ids
    /// are derived from a topology-dependent segmentation, so a wiring change invalidates them - the
    /// tool refuses rather than silently addressing a DIFFERENT region.</summary>
    public const string StalePartition = "stale_partition";
    /// <summary>The canvas is unavailable (no active document).</summary>
    public const string NoDocument = "no_document";

    /// <summary>A build named a region address that no group on the canvas carries (WP-4 Stage B).
    /// Carries the addresses that DO exist - resolve exactly or refuse (#208), because a build aimed at
    /// a mis-read address is a mutation in the wrong place.</summary>
    public const string UnknownRegion = "unknown_region";

    /// <summary>A build named a region that holds components THIS TURN did not place (#352). We cannot
    /// name what would be deleted, and by now it may be the user's own work - so the rebuild refuses
    /// rather than falling back to "whatever is in the group".</summary>
    public const string RegionOccupied = "region_occupied";

    /// <summary>The per-region attempt budget is spent (#97/#352). Whatever is standing STAYS standing
    /// and is reported - never a silent revert of work the user watched land.</summary>
    public const string RegionAttemptsExhausted = "region_attempts_exhausted";

    /// <summary>A build named a region the USER DID NOT APPROVE (#338 in address space, WP-4 Stage C).
    /// A consent card is a contract about scope, so a card naming A1-A3 cannot produce a build of B1 -
    /// enforced by the plugin, because a prompt line is not a guard.</summary>
    public const string RegionNotApproved = "region_not_approved";

    /// <summary>A consent request for the kind this very turn was approved to carry out (#611d). The build
    /// that never ran asked for its own approval again; the answer is to use the tool, not a second card.</summary>
    public const string AlreadyApproved = "already_approved";

    /// <summary>A rebuild would cut a wire into something it does not remove, and the spec does not declare how to
    /// reconnect it - or declares a reconnection that matches no cut (#625). Refused before anything is changed,
    /// with the cut list and #647's way out.</summary>
    public const string BoundaryCut = "boundary_cut";
  }

  /// <summary>One tool on the surface, as the registry advertises it. Name + JSON Schema + description is
  /// exactly what an MCP <c>list_tools</c> returns (ADR-069/098), so the v1.1 face is a transport
  /// adapter over this and not a second definition.</summary>
  internal sealed class ToolDescriptor
  {
    public string Name { get; set; }
    public string Description { get; set; }

    /// <summary>The published input schema, AS AN OBJECT (#823 Part 2). It is built rather than written
    /// (see <see cref="Sch"/>), so a malformed schema is not a state this type can hold. The failure that
    /// bought this change was a hand-quoted string broken by the first computed quotation mark, and the
    /// repair is to remove the representation that could be wrong rather than to escape it better.</summary>
    public System.Text.Json.Nodes.JsonObject InputSchema { get; set; }

    /// <summary>The same schema as text, for the consumers that need one: the transport, the MCP
    /// <c>list_tools</c> face, and the external deploy probe, which cannot read a property off an
    /// internal type (#374). DERIVED, never stored - there is one schema and it is the object.</summary>
    public string InputSchemaJson => InputSchema?.ToJsonString();

    /// <summary>True when the tool can change the document. Mutating tools return a VERIFICATION
    /// VERDICT (ADR-098) and ride the full 7.0 safety stack; reads are zero-token, solve-free and
    /// mutation-free (#190).</summary>
    public bool Mutates { get; set; }

    /// <summary>True when the tool RUNS THE SOLUTION. Distinct from <see cref="Mutates"/>: solving
    /// changes nothing about the definition, so it carries no verdict - but it is observable work
    /// (seconds, recomputed geometry) and must be dispatched asynchronously, so it cannot hide inside
    /// the read surface's zero-cost promise.</summary>
    public bool Solves { get; set; }
  }

  /// <summary>The uniform result envelope. Success carries <c>result</c>; refusal carries a coded
  /// <c>error</c>. Never a bare string, never a throw across the boundary.</summary>
  internal sealed class ToolResult
  {
    public bool Ok { get; private set; }
    public JsonNode Result { get; private set; }
    public string Code { get; private set; }
    public string Message { get; private set; }
    public JsonObject Detail { get; private set; }

    public static ToolResult Success(JsonNode result) =>
      new ToolResult { Ok = true, Result = result ?? new JsonObject() };

    /// <summary>
    /// A refusal - and every refusal carries a CAUSE (F2, from the WP-4 gate).
    ///
    /// WHY. Twice at the gate the model narrated Dwight's internals from guesswork. Explaining a refusal
    /// it invented "a fresh session boundary or the 'map was gone' episode intervened, the system no
    /// longer recognizes it as mine to remove" - none of which had happened. Asked to delete, it said
    /// deletion "has to be routed through an approval card" and produced none, because there is no delete
    /// kind.
    ///
    /// THE SHARED CAUSE: our refusals stated WHAT was refused and not WHY, so a model that must account
    /// for our behaviour to a user filled the gap with a plausible story. That is not a prompt-discipline
    /// failure - THE MODEL WAS DOING ITS JOB WITH THE INFORMATION IT HAD. The fix is to give it a true
    /// cause to relay.
    ///
    /// THE CAUSE IS DERIVED FROM THE CODE rather than written out at each of the ninety-odd refusal
    /// sites, for the same reason F3's capability sentence is generated: a default that comes from the
    /// thing it describes cannot be forgotten at a new site, and a new refusal is the one most likely to
    /// be explained badly. A site with something more specific to say overrides it by putting its own
    /// "cause" in the detail, and several do.
    /// </summary>
    public static ToolResult Fail(string code, string message, JsonObject detail = null)
    {
      var why = ToolErrors.CauseOf(code);
      if (why != null)
      {
        detail = detail ?? new JsonObject();
        // Never overwrite a site's own cause: the specific one is always better than the generic one.
        if (detail["cause"] == null) detail["cause"] = why;
      }

      return new ToolResult { Ok = false, Code = code, Message = message ?? "", Detail = detail };
    }

    /// <summary>The wire form both the chat agent (WP-3) and the MCP face (v1.1) read.</summary>
    public string ToJson()
    {
      // Copied for the same reason as the detail below: a JsonNode has one parent, and this method is
      // called once for the wire and again for a log or a test. The success path had the identical
      // defect and was found by the test written for the failure path.
      if (Ok)
        return new JsonObject
        {
          ["ok"] = true,
          ["result"] = Result == null ? new JsonObject() : JsonNode.Parse(Result.ToJsonString()),
        }.ToJsonString();
      var err = new JsonObject { ["code"] = Code, ["message"] = Message };

      // A COPY, because a JsonNode may have only one parent and this method must be callable twice.
      //
      // Found when F2 gave every refusal a detail: ToJson() had never been idempotent, and nothing
      // noticed because most refusals carried no detail at all, so the second call had nothing to
      // re-parent. A tool result is exactly the kind of thing that gets serialised once for the wire and
      // again for a log or a test, and the second call threw "the node already has a parent".
      if (Detail != null) err["detail"] = JsonNode.Parse(Detail.ToJsonString());
      return new JsonObject { ["ok"] = false, ["error"] = err }.ToJsonString();
    }
  }

  // ---- the GH-free snapshot DTOs ------------------------------------------------------------------
  // The shim projects DwightGraph + the live document into these; every core decision reads only these,
  // so the whole surface is testable outside Rhino (ADR-091). Ids here are REGISTRY ids (#191), never
  // DwightGraph's pivot-ordered ordinals - that substitution is the entire point.

  /// <summary>A data-tree signature: structure only, never contents (ADR-058 rule 3).</summary>
  internal sealed class TreeSigDto
  {
    public int Paths { get; set; }
    public int Depth { get; set; }
    public int[] Items { get; set; } = Array.Empty<int>();
  }

  internal sealed class MsgDto
  {
    public string Level { get; set; }
    public string Text { get; set; }
  }

  internal sealed class AgentNode
  {
    public int Id { get; set; }                 // registry id, stable for the scope's lifetime
    public string Type { get; set; }
    public string Nick { get; set; }
    public string Kind { get; set; }            // component | param
    public string[] Inputs { get; set; } = Array.Empty<string>();
    public string[] Outputs { get; set; } = Array.Empty<string>();
    public List<MsgDto> Msgs { get; set; } = new List<MsgDto>();
    public int[] OutCounts { get; set; } = Array.Empty<int>();

    /// <summary>U25 (#915): the qualified form "Category &gt; SubCategory &gt; Name", set ONLY when the bare type name
    /// belongs to more than one installed type (Rectangle, Line, Circle on a stock machine) - so the model can name
    /// THIS one unambiguously. Null for the common case; an always-present field is a field nobody reads.</summary>
    public string Qualified { get; set; }
    /// <summary>Brief DC: the lines of text this component's Dwight notes become (<see cref="Dwight.Session.DwightNotes.Lines"/>), or null. Text only.</summary>
    public List<string> Notes { get; set; }

    // ---- Phase-9 U1 (reads-unification, #382(9)): what the component HOLDS, read by the verbs' own
    // readers and projected here. Null or empty means ABSENT, and an absent fact is omitted from the
    // payload - never emitted as an empty the model would reason from. ----------------------------------

    /// <summary>The ADR-093 constant string ("40", "\"hello\""), for the compact region brief.</summary>
    public string Constant { get; set; }
    /// <summary>The typed editable value of a value-source object (slider, panel, toggle, value list, swatch).</summary>
    public ValueDto Value { get; set; }
    /// <summary>Non-null for ANY script component, readable or not - so an unreadable one is still
    /// recognised as a script and reported as unreadable rather than mistaken for an ordinary node.</summary>
    public string ScriptLanguage { get; set; }
    /// <summary>The bounded source, or null on a script component = UNREADABLE (a first-class answer,
    /// RR17 #36: a locked or encrypted script is a black box, never "no code").</summary>
    public SourceDto Source { get; set; }
    /// <summary>Brief CP: a script's inputs with their access, in order, so a model that reads one knows how the code will be fed. Null for anything else.</summary>
    public List<KeyValuePair<string, string>> ScriptInputAccess { get; set; }
    /// <summary>Per input param that carries internalised data: a type, a count, a path count.</summary>
    public List<PersistentDto> Persistent { get; set; } = new List<PersistentDto>();
    /// <summary>Set when the component comes from an installed plugin rather than a core library.</summary>
    public PluginDto Plugin { get; set; }

    // ---- Phase-9 U1(c): clusters, one level (#382(9); the Q3 ruling). ----------------------------------

    /// <summary>For a node INSIDE a cluster's internal document: the cluster's own id. Null at the top level.</summary>
    public int? Inside { get; set; }
    /// <summary>U22: this node's content as a read saw it. Never serialised into a result - it feeds the registry's
    /// readings and the canvas revision. Null when the snapshot was not stamped or the object could not be read.</summary>
    public ComponentStamp Stamp { get; set; }
    /// <summary>"input" | "output" for a cluster hook param inside the internal document; null otherwise.</summary>
    public string Hook { get; set; }
    /// <summary>For a GH_Cluster: what the reader found inside, or why it did not look.</summary>
    public ClusterDto Cluster { get; set; }
  }

  /// <summary>
  /// A cluster's internals, or the reason they were not read. #392 (i), as ruled: protection is checked
  /// FIRST against an ALLOW-LIST of levels safe to read, and only then is the internal document opened -
  /// never the other way round. A level this build does not recognise is refused by name, so a future
  /// Grasshopper's new protection level can never become silently readable.
  /// </summary>
  internal sealed class ClusterDto
  {
    /// <summary>Internal objects found; null when the cluster was not read.</summary>
    public int? Count { get; set; }
    /// <summary>Internals beyond the cap, not projected.</summary>
    public int Omitted { get; set; }
    /// <summary>The protection level refused reading (Protected, or a level this build does not know).</summary>
    public bool Protected { get; set; }
    /// <summary>The level's own name, for the report.</summary>
    public string Level { get; set; }
    /// <summary>The cluster is disabled on the canvas (Grasshopper's Locked). Reported, not a reason to refuse.
    /// NULL means NOT MEASURED - the read threw, and <see cref="LockedUnknown"/> says why - never "false":
    /// a field stating something we did not measure is the class this project has a name for.</summary>
    public bool? Locked { get; set; }
    /// <summary>Why the locked state could not be read, verbatim, when <see cref="Locked"/> is null.</summary>
    public string LockedUnknown { get; set; }
    /// <summary>The internal document could not be read, with the reason verbatim.</summary>
    public string Unreadable { get; set; }
  }

  /// <summary>A script component's SOURCE, bounded by <see cref="SourceBound"/>.</summary>
  internal sealed class SourceDto
  {
    public string Language { get; set; }
    public string Text { get; set; }
    public int TotalLines { get; set; }
    public int TotalChars { get; set; }
    public bool Truncated { get; set; }
    /// <summary>What the bound left out, in words ("300 more lines not shown; 9000 chars in total").</summary>
    public string Omitted { get; set; }
  }

  /// <summary>The editable value of a value-source object. <see cref="Kind"/> says which fields apply:
  /// slider (value, min, max, rounding, decimals) | panel (text) | toggle (on) | valuelist (selected,
  /// options) | swatch (text).</summary>
  internal sealed class ValueDto
  {
    public string Kind { get; set; }
    public decimal? Value { get; set; }
    public decimal? Min { get; set; }
    public decimal? Max { get; set; }
    public string Rounding { get; set; }
    public int? Decimals { get; set; }
    public string Text { get; set; }
    public bool TextTruncated { get; set; }
    public int TextTotalChars { get; set; }
    public bool? On { get; set; }
    public string[] Selected { get; set; }
    public int? Options { get; set; }

    // ---- U31 (#926): a FED panel. A Panel with a wire into it DISPLAYS what flows in and its UserText is the
    // placeholder ("Double click to edit panel content..."); the model, reading Text, could not read the GFA and
    // tower height it had just built. Non-null Displayed means "this panel is fed, and these are the values it
    // shows, as text"; Text is then null, because the placeholder is not a fact about the definition. ----------

    /// <summary>The displayed items, as text, bounded (<see cref="PanelDisplay"/>); null for an unfed panel.</summary>
    public string[] Displayed { get; set; }
    /// <summary>How many items the panel displays in total, shown or not.</summary>
    public int DisplayedCount { get; set; }
    /// <summary>How many branches the displayed data has.</summary>
    public int DisplayedPaths { get; set; }
    /// <summary>The list was cut (by item count or by characters); <see cref="Displayed"/> is the first part.</summary>
    public bool DisplayedTruncated { get; set; }
  }

  /// <summary>A param's internalised data, in ADR-093's shape: a type (with the value where the item is a
  /// primitive), a count, a path count. Never the geometry.</summary>
  internal sealed class PersistentDto
  {
    public string Param { get; set; }
    public int Count { get; set; }
    public int Paths { get; set; }
    public string Item { get; set; }
  }

  internal sealed class PluginDto
  {
    public string Library { get; set; }
    public string Does { get; set; }
  }

  internal sealed class AgentEdgeDto
  {
    public int FromNode { get; set; }
    public string FromParam { get; set; }
    public int ToNode { get; set; }
    public string ToParam { get; set; }
    public TreeSigDto Tree { get; set; }

    public bool Matches(int fromNode, string fromParam, int toNode, string toParam) =>
      FromNode == fromNode && ToNode == toNode &&
      string.Equals(FromParam ?? "", fromParam ?? "", StringComparison.OrdinalIgnoreCase) &&
      string.Equals(ToParam ?? "", toParam ?? "", StringComparison.OrdinalIgnoreCase);
  }

  /// <summary>One fault as the <c>diagnose</c> tool reports it (#258). Deterministically FOUND by
  /// <c>FaultScan</c> - the model is told what is wrong, never asked to look for it.</summary>
  internal sealed class DiagnoseFaultDto
  {
    public int Id { get; set; }
    public string Type { get; set; }
    public string Nick { get; set; }
    /// <summary>error | warning | silent (green but empty).</summary>
    public string Kind { get; set; }
    /// <summary>Upstream-most in its chain - fix these first.</summary>
    public bool Root { get; set; }
    /// <summary>It has its own failure, rather than being starved by a broken upstream (ADR-078).</summary>
    public bool Intrinsic { get; set; }
    public string Message { get; set; }
  }

  internal sealed class AgentCluster
  {
    public int Id { get; set; }
    /// <summary>The cached label, or the generic "Region N" (RegionVocabulary, U29 #918 - never "Cluster N",
    /// a Grasshopper type's name). A read tool NEVER pays to name a region (#190): no LLM call, no billing, ever.</summary>
    public string Label { get; set; }
    public List<int> Members { get; set; } = new List<int>();
  }

  /// <summary>
  /// The snapshot's nodes, and THE REASON THIS IS NOT A PLAIN LIST (#719).
  ///
  /// <see cref="AgentSnapshot.NodeById"/> memoises a lookup. <c>GhAgent.AddInternals</c> called it to hang a cluster's
  /// DTO on its owner and THEN appended the cluster's internals, so the index froze with the top-level nodes and every
  /// internal node was invisible to it afterwards: <c>get_component</c> refused a live cluster-internal id as deleted,
  /// and <c>IsInside</c> answered false for it - two readers disagreeing about one object, in one instant.
  ///
  /// A count check alone would heal an append and miss a replacement of equal size, so THE MUTATION PATH OWNS THE
  /// INVALIDATION: every way to change this collection tells the snapshot to drop its index, and there is no way to
  /// append behind the index's back. That is #631/#616's shape - make the wrong thing hard to WRITE rather than merely
  /// detected afterwards.
  /// </summary>
  internal sealed class AgentNodeList : IEnumerable<AgentNode>
  {
    private readonly List<AgentNode> _nodes = new List<AgentNode>();
    private readonly Action _changed;

    public AgentNodeList(Action changed) { _changed = changed; }

    public int Count => _nodes.Count;
    public AgentNode this[int index] => _nodes[index];

    public void Add(AgentNode node) { _nodes.Add(node); _changed(); }
    public void AddRange(IEnumerable<AgentNode> nodes) { _nodes.AddRange(nodes); _changed(); }
    public void Sort(Comparison<AgentNode> comparison) { _nodes.Sort(comparison); _changed(); }
    public int RemoveAll(Predicate<AgentNode> match) { var n = _nodes.RemoveAll(match); _changed(); return n; }

    public IEnumerator<AgentNode> GetEnumerator() => _nodes.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
  }

  /// <summary>One immutable projection of the canvas, in registry-id space.</summary>
  internal sealed class AgentSnapshot
  {
    public AgentNodeList Nodes { get; }
    public List<AgentEdgeDto> Edges { get; set; } = new List<AgentEdgeDto>();
    public List<AgentCluster> Clusters { get; set; } = new List<AgentCluster>();
    /// <summary>Inter-cluster flow, deduplicated and ordered.</summary>
    public List<(int From, int To)> Flow { get; set; } = new List<(int, int)>();

    /// <summary>U22: the revision of the canvas this snapshot was read at (see <see cref="AgentIdRegistry.ObserveCanvas"/>),
    /// or null when the snapshot was not stamped - then no read states one.</summary>
    public int? ReadRevision { get; set; }

    public AgentSnapshot() { Nodes = new AgentNodeList(() => _byId = null); }

    private Dictionary<int, AgentNode> _byId;

    /// <summary>
    /// The node with this id, or null. The index is rebuilt whenever the collection has changed (it drops the memo) -
    /// and, as braces, whenever the two disagree about how many nodes there are.
    ///
    /// Built defensively rather than with ToDictionary: two nodes sharing an id is a defect elsewhere, and this lookup
    /// answering it with a throw would turn that defect into an unreadable tool call (#375).
    /// </summary>
    public AgentNode NodeById(int id)
    {
      if (_byId == null || _byId.Count != Nodes.Count)
      {
        _byId = new Dictionary<int, AgentNode>();
        foreach (var n in Nodes)
          if (n != null) _byId[n.Id] = n;
      }
      return _byId.TryGetValue(id, out var node) ? node : null;
    }

    public AgentCluster ClusterById(int id) => Clusters.FirstOrDefault(c => c.Id == id);
  }
}
