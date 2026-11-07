using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace Dwight.Agent
{
  /// <summary>
  /// The GH-free heart of the agent tool surface (ADR-091): argument validation, groundedness
  /// resolution, and every read-tool projection, over an <see cref="AgentSnapshot"/> alone. No
  /// Grasshopper, no live document - so the decisions that keep the model honest are unit-tested
  /// outside Rhino, and the shim above stays a thin reader.
  ///
  /// THREE LAWS THIS FILE ENFORCES:
  /// - GROUNDEDNESS (ADR-058): every id in, and every id out, resolves through the registry to an object
  ///   that is actually on the canvas. An id the model invented is refused, not guessed at.
  /// - ZERO-TOKEN, SOLVE-FREE, MUTATION-FREE READS (#190): nothing here calls a model, runs a solution,
  ///   or touches the document. Cluster labels come from cache or come back generic.
  /// - NO SILENT CAPS (#134): a projection that truncates says so in its payload.
  /// </summary>
  internal static class AgentToolCore
  {
    /// <summary>How many ids <c>search</c> will return before it truncates (and says so).</summary>
    public const int SearchCap = 100;

    /// <summary>How many faults <c>diagnose</c> lists before it truncates (and says so). Roots come
    /// first, so a truncated list is still the useful end of it.</summary>
    public const int DiagnoseCap = 60;

    // ---- argument plumbing ---------------------------------------------------------------------

    public static JsonObject ParseArgs(string argsJson, out ToolResult error)
    {
      error = null;
      if (string.IsNullOrWhiteSpace(argsJson)) return new JsonObject();
      try
      {
        if (JsonNode.Parse(argsJson) is JsonObject o) return o;
        error = ToolResult.Fail(ToolErrors.InvalidArgs, "Arguments must be a JSON object.");
        return null;
      }
      catch (Exception ex)
      {
        error = ToolResult.Fail(ToolErrors.InvalidArgs, "Arguments are not valid JSON (" + ex.Message + ").");
        return null;
      }
    }

    private static bool TryInt(JsonObject args, string field, out int value, out ToolResult error)
    {
      value = 0; error = null;
      var node = args?[field];
      if (node == null)
      {
        error = ToolResult.Fail(ToolErrors.InvalidArgs, $"Missing required argument \"{field}\".");
        return false;
      }
      try { value = node.GetValue<int>(); return true; }
      catch
      {
        error = ToolResult.Fail(ToolErrors.InvalidArgs, $"Argument \"{field}\" must be an integer.");
        return false;
      }
    }

    private static string OptString(JsonObject args, string field)
    {
      var node = args?[field];
      if (node == null) return null;
      try { var s = node.GetValue<string>(); return string.IsNullOrWhiteSpace(s) ? null : s.Trim(); }
      catch { return null; }
    }

    /// <summary>
    /// THE DIAGNOSE PROJECTION (#258) - the deterministic fault scan, as a tool.
    ///
    /// WHY IT EXISTS. Given only primitive reads, the agent rebuilt Diagnose by SAMPLING: it walked 25
    /// script components, declared the definition clean, and was wrong by 150-200 failing params. That is
    /// not a prompt failure - it is what an agent must do when the verb it needs is not on the surface.
    /// <c>FaultScan</c> has answered this exhaustively, in ZERO tokens, since Phase 4 (ADR-078), and
    /// sampling is precisely what a deterministic scan replaces.
    ///
    /// It is a pure READ: it reports the runtime messages and output counts Grasshopper already holds. It
    /// does not solve - so a stale solution reports stale facts, which is what the <c>solve</c> consent
    /// card is for (#222).
    ///
    /// Bounded and self-reporting (#209/#134): roots first, capped, and the omission stated.
    /// </summary>
    public static ToolResult Diagnose(IReadOnlyList<DiagnoseFaultDto> faults, int strayCount, int scanned,
      bool wholeCanvas, int cap = DiagnoseCap)
    {
      var all = faults ?? new List<DiagnoseFaultDto>();
      var shown = all.Take(cap).ToList();

      var arr = new JsonArray();
      foreach (var f in shown)
      {
        var o = new JsonObject
        {
          ["id"] = f.Id,
          ["type"] = f.Type ?? "",
          ["kind"] = f.Kind ?? "",
          ["root"] = f.Root,
          ["intrinsic"] = f.Intrinsic,
        };
        if (!string.IsNullOrWhiteSpace(f.Nick)) o["nick"] = f.Nick;
        if (!string.IsNullOrWhiteSpace(f.Message)) o["message"] = f.Message;
        arr.Add(o);
      }

      var result = new JsonObject
      {
        ["scope"] = wholeCanvas ? "the whole definition" : "the selection and everything upstream of it",
        ["scanned"] = scanned,
        ["faultCount"] = all.Count,
        ["clean"] = all.Count == 0,
        ["faults"] = arr,
        ["strays"] = strayCount,
        ["means"] = "This is the COMPLETE list for the scope - it is a deterministic scan of every " +
                    "component's runtime messages and output counts, not a sample. If it says clean, it " +
                    "is clean as of the last solution. Do not go looking component by component. " +
                    "scanned is how many components the scan examined; faultCount is every fault it found " +
                    "(faults lists at most " + cap + " of them, roots first); strays are isolated components " +
                    "with no wires in or out - informational, not faults.",
      };

      if (all.Count > shown.Count)
        result["omitted"] = (all.Count - shown.Count) + " further fault(s) not listed (roots and the most " +
                            "severe come first). Narrow the scope to see the rest.";

      return ToolResult.Success(result);
    }

    // ---- groundedness (#191) -------------------------------------------------------------------

    /// <summary>
    /// Resolve an agent id to a live node: id -> guid -> alive-in-current-graph. The three outcomes are
    /// deliberately distinct, because they call for three different corrections - "you made that up",
    /// "that was deleted", and here it is.
    /// </summary>
    public static bool TryResolve(AgentSnapshot snap, AgentIdRegistry ids, int id, out AgentNode node, out ToolResult error)
    {
      node = null; error = null;
      var state = ids?.State(id) ?? IdState.Unknown;
      if (state == IdState.Unknown)
      {
        error = ToolResult.Fail(ToolErrors.UnknownId,
          $"No component with id {id} exists. Ids come from this session's reads - do not invent them.",
          new JsonObject { ["id"] = id });
        return false;
      }
      if (state == IdState.Retired)
      {
        error = ToolResult.Fail(ToolErrors.RetiredId,
          $"Component {id} is no longer on the canvas (it was deleted, or an undo removed it).",
          new JsonObject { ["id"] = id });
        return false;
      }
      node = snap?.NodeById(id);
      if (node == null)
      {
        // THE FOURTH STATE, AND IT IS NOT A DELETION (#719, #613's law again). The registry says this id is ALIVE, so
        // the RetiredId refusal's cause - "its component has since been deleted from the canvas" - would be false, and
        // it was false twice over in the field: the component was on the canvas, inside a cluster, and the reader's own
        // stale index was what could not see it. A refusal is a claim; alive-but-absent is a different claim from
        // deleted, so it gets its own code and its own words.
        error = ToolResult.Fail(ToolErrors.NotInThisRead,
          $"Component {id} exists in this session but is not in what this read covered. It was not deleted. Read it " +
          "where it lives - if it is inside a cluster, call get_cluster on that cluster - or call get_overview again.",
          new JsonObject { ["id"] = id });
        return false;
      }
      return true;
    }

    // ---- JSON emit helpers ---------------------------------------------------------------------

    /// <summary>The compact form a region or a search lists. Phase-9 U1: it carries the HINT (this is a
    /// python script; this slider reads 40) and never the payload - a source dump per node would swamp
    /// a region listing, and the hint is enough to decide what to drill into with get_component.</summary>
    private static JsonObject NodeBrief(AgentNode n)
    {
      var o = new JsonObject
      {
        ["id"] = n.Id,
        ["type"] = n.Type,
        ["nick"] = n.Nick,          // null when the user set none
      };
      if (n.Qualified != null) o["qualified"] = n.Qualified;   // U25 (#915): only where the bare name is ambiguous
      Dwight.Session.DwightNotes.AddTo(o, n.Notes);            // Brief DC: what the person or an earlier Dwight wrote about it
      if (n.ScriptLanguage != null) o["script"] = n.ScriptLanguage;
      if (n.Constant != null) o["value"] = n.Constant;
      if (n.Inside.HasValue) o["inside"] = n.Inside.Value;
      if (n.Cluster != null) o["cluster"] = ClusterHint(n.Cluster);
      return o;
    }

    /// <summary>What a <c>qualified</c> field on a component means, wherever a read carries one (U25 #915).</summary>
    public const string QualifiedMeans =
      "a component carries \"qualified\" when its bare type name belongs to more than one installed component (a " +
      "Rectangle param and a Rectangle component, say); it is the form Category > SubCategory > Name that names THIS " +
      "one. Use it wherever a type is asked for - kb_lookup, search's type, build_region - so the right one is meant.";

    /// <summary>true when disabled; "unmeasured (reason)" when the read threw; null (omitted) when measured
    /// and not locked. A failed read is never rendered as false.</summary>
    private static JsonNode LockedJson(ClusterDto c) =>
      c.Locked == true ? (JsonNode)true
      : c.Locked == null && c.LockedUnknown != null ? "unmeasured (" + c.LockedUnknown + ")"
      : null;

    /// <summary>The one-word cluster hint a brief carries: the count, or why it was not read.</summary>
    private static JsonNode ClusterHint(ClusterDto c) =>
      c.Protected ? "protected" : c.Unreadable != null ? "unreadable" : c.Count.HasValue ? (JsonNode)c.Count.Value : "unread";

    private static string ClusterWhy(ClusterDto c)
    {
      if (c.Protected)
        return "This cluster is password-protected (" + (c.Level ?? "Protected") + "). Dwight does not read inside a " +
               "cluster its author locked; ask the user.";
      if (c.Unreadable != null) return "The cluster's internal document could not be read: " + c.Unreadable + ".";
      return null;
    }

    /// <summary>
    /// get_cluster: a cluster's internals as a REGION the model can drill into, addressed by the cluster's
    /// component id (its own tool - the Q3 ruling: component ids and region ids have different lifetimes
    /// and must never share a parameter). A protected or unreadable cluster is REPORTED with the reason,
    /// never read (#392 (i)); a non-cluster is refused as a precondition, not as malformed arguments.
    /// </summary>
    public static ToolResult GetCluster(AgentSnapshot snap, AgentIdRegistry ids, JsonObject args)
    {
      if (!TryInt(args, "id", out var id, out var err)) return err;
      if (!TryResolve(snap, ids, id, out var n, out var resolveErr)) return resolveErr;

      if (n.Cluster == null)
        return ToolResult.Fail(ToolErrors.PreconditionRefused,
          "Component " + n.Id + " (" + n.Type + ") is not a cluster, so there is nothing inside it to read. " +
          "get_component tells you what it holds.",
          new JsonObject { ["id"] = n.Id, ["type"] = n.Type });

      var result = new JsonObject { ["id"] = n.Id, ["nick"] = n.Nick };
      var locked = LockedJson(n.Cluster);
      if (locked != null) result["locked"] = locked;

      var why = ClusterWhy(n.Cluster);
      if (why != null)
      {
        if (n.Cluster.Protected) { result["protected"] = true; if (n.Cluster.Level != null) result["level"] = n.Cluster.Level; }
        else result["unreadable"] = true;
        result["why"] = why;
        return ToolResult.Success(result);
      }

      var members = new HashSet<int>(snap.Nodes.Where(x => x.Inside == n.Id).Select(x => x.Id));
      result["count"] = n.Cluster.Count ?? members.Count;
      result["means"] = new JsonObject
      {
        ["count"] = "the objects inside this cluster, one level deep (a nested cluster counts as one); nodes lists " +
                    "them, capped, and omitted says how many were left out.",
      };
      result["inputs"] = new JsonArray((n.Inputs ?? Array.Empty<string>()).Select(s => (JsonNode)s).ToArray());
      result["outputs"] = new JsonArray((n.Outputs ?? Array.Empty<string>()).Select(s => (JsonNode)s).ToArray());

      var nodes = new JsonArray();
      foreach (var x in snap.Nodes.Where(x => members.Contains(x.Id)).OrderBy(x => x.Id)) nodes.Add(NodeBrief(x));
      result["nodes"] = nodes;

      var edges = new JsonArray();
      foreach (var e in snap.Edges.Where(e => members.Contains(e.FromNode) && members.Contains(e.ToNode))
                 .OrderBy(e => e.FromNode).ThenBy(e => e.ToNode))
        edges.Add(EdgeJson(e));
      result["edges"] = edges;

      var hookIn = new JsonArray();
      var hookOut = new JsonArray();
      foreach (var x in snap.Nodes.Where(x => members.Contains(x.Id) && x.Hook != null).OrderBy(x => x.Id))
      {
        var h = new JsonObject { ["id"] = x.Id, ["name"] = x.Nick ?? (x.Outputs?.FirstOrDefault() ?? x.Type) };
        if (x.Hook == "input") hookIn.Add(h); else hookOut.Add(h);
      }
      result["hooks"] = new JsonObject { ["inputs"] = hookIn, ["outputs"] = hookOut };

      if (n.Cluster.Omitted > 0)
        result["omitted"] = n.Cluster.Omitted + " more internal object(s) not projected - the cluster holds " +
                            (n.Cluster.Count ?? members.Count) + "; this read shows the first " + members.Count + ".";
      return ToolResult.Success(result);
    }

    // ---- Phase-9 U1: what a component HOLDS (#382(9)) --------------------------------------------------
    // Present facts are emitted typed and bounded; absent facts are OMITTED. The bounding happened at
    // read time (SourceBound, the panel cap) and is REPORTED here, never silently applied (#134).

    private static void AppendFacts(AgentNode n, JsonObject o)
    {
      if (n.Value != null) o["value"] = ValueJson(n.Value);
      if (n.ScriptLanguage != null) o["script"] = ScriptJson(n);
      if (n.Persistent != null && n.Persistent.Count > 0)
      {
        var arr = new JsonArray();
        foreach (var p in n.Persistent)
          arr.Add(new JsonObject { ["param"] = p.Param, ["count"] = p.Count, ["paths"] = p.Paths, ["item"] = p.Item });
        o["persistent"] = arr;
      }
      if (n.Plugin != null) o["plugin"] = new JsonObject { ["library"] = n.Plugin.Library, ["does"] = n.Plugin.Does };
      if (n.Inside.HasValue) o["inside"] = n.Inside.Value;
      if (n.Hook != null) o["hook"] = n.Hook;
      if (n.Cluster != null)
      {
        var c = new JsonObject();
        if (n.Cluster.Count.HasValue) c["count"] = n.Cluster.Count.Value;
        if (n.Cluster.Omitted > 0) c["omitted"] = n.Cluster.Omitted;
        if (n.Cluster.Protected) { c["protected"] = true; if (n.Cluster.Level != null) c["level"] = n.Cluster.Level; }
        var locked = LockedJson(n.Cluster);
        if (locked != null) c["locked"] = locked;
        if (n.Cluster.Unreadable != null) c["unreadable"] = n.Cluster.Unreadable;
        c["readWith"] = n.Cluster.Protected || n.Cluster.Unreadable != null
          ? "get_cluster reports why it cannot be read"
          : "get_cluster reads inside it";
        o["cluster"] = c;
      }
    }

    private static JsonObject ValueJson(ValueDto v)
    {
      var o = new JsonObject { ["kind"] = v.Kind };
      if (v.Value.HasValue) o["value"] = v.Value.Value;
      if (v.Min.HasValue) o["min"] = v.Min.Value;
      if (v.Max.HasValue) o["max"] = v.Max.Value;
      if (v.Rounding != null) o["rounding"] = v.Rounding;
      if (v.Decimals.HasValue) o["decimals"] = v.Decimals.Value;
      if (v.Text != null)
      {
        o["text"] = v.Text;
        if (v.TextTruncated) { o["truncated"] = true; o["chars"] = v.TextTotalChars; }
      }
      if (v.Displayed != null)
      {
        // U31 (#926): a fed panel's DISPLAYED values, bounded, with the cut stated - and no "text", because the
        // placeholder a fed panel holds is not a fact about the definition.
        o["displayed"] = new JsonArray(v.Displayed.Select(s => (JsonNode)s).ToArray());
        o["displayedCount"] = v.DisplayedCount;
        o["displayedPaths"] = v.DisplayedPaths;
        if (v.DisplayedTruncated) { o["displayedTruncated"] = true; o["displayedShown"] = v.Displayed.Length; }
      }
      if (v.On.HasValue) o["on"] = v.On.Value;
      if (v.Selected != null) o["selected"] = new JsonArray(v.Selected.Select(s => (JsonNode)s).ToArray());
      if (v.Options.HasValue) o["options"] = v.Options.Value;
      return o;
    }

    private static JsonObject ScriptJson(AgentNode n)
    {
      var o = new JsonObject { ["language"] = n.ScriptLanguage };
      if (n.ScriptInputAccess != null && n.ScriptInputAccess.Count > 0)
        o["inputAccess"] = new JsonObject(n.ScriptInputAccess.Select(kv => new KeyValuePair<string, JsonNode>(kv.Key, kv.Value)));
      if (n.Source == null)
      {
        o["unreadable"] = true;    // a black box, said plainly - never "no code"
        return o;
      }
      o["source"] = n.Source.Text;
      o["lines"] = n.Source.TotalLines;
      o["chars"] = n.Source.TotalChars;
      if (n.Source.Truncated)
      {
        o["truncated"] = true;
        o["omitted"] = n.Source.Omitted;
      }
      return o;
    }

    private static JsonObject SigJson(TreeSigDto s) => s == null ? null : new JsonObject
    {
      ["paths"] = s.Paths,
      ["depth"] = s.Depth,
      ["items"] = new JsonArray((s.Items ?? Array.Empty<int>()).Select(i => (JsonNode)i).ToArray()),
    };

    private static JsonObject EdgeJson(AgentEdgeDto e)
    {
      var o = new JsonObject
      {
        ["from"] = new JsonObject { ["node"] = e.FromNode, ["param"] = e.FromParam },
        ["to"] = new JsonObject { ["node"] = e.ToNode, ["param"] = e.ToParam },
      };
      var sig = SigJson(e.Tree);
      if (sig != null) o["sig"] = sig;
      return o;
    }

    private static JsonArray Ids(IEnumerable<int> ids) =>
      new JsonArray(ids.OrderBy(i => i).Select(i => (JsonNode)i).ToArray());

    private static bool IsInside(AgentSnapshot snap, int id) => snap.NodeById(id)?.Inside != null;

    // ---- the read tools ------------------------------------------------------------------------

    // ---- the partition stamp (#195) --------------------------------------------------------------

    /// <summary>
    /// A move-invariant, topology-sensitive stamp over the graph - the same instrument Reorganise uses
    /// to guard a stale apply, computed here over the snapshot DTOs so it is testable.
    ///
    /// WHY IT IS THE RIGHT STAMP FOR REGION IDS. Region ids come from the segmenter, which reads
    /// dataflow TOPOLOGY and never position (ADR-087 - the scattered==tidy invariance). So the
    /// partition's validity scope is exactly this hash's: moving components cannot change either, and
    /// any rewiring changes both. Nodes and edges only - no pivots, no ordinals.
    /// </summary>
    public static string ShapeHash(AgentSnapshot snap)
    {
      var tokens = new List<string>();
      foreach (var n in snap.Nodes) tokens.Add("n:" + n.Id + ":" + n.Type);
      foreach (var e in snap.Edges) tokens.Add("e:" + e.FromNode + ">" + e.ToNode + ":" + e.ToParam);
      tokens.Sort(StringComparer.Ordinal);
      using (var sha = System.Security.Cryptography.SHA256.Create())
        return Convert.ToHexString(sha.ComputeHash(
          System.Text.Encoding.UTF8.GetBytes(string.Join("|", tokens))));
    }

    /// <summary>get_overview: counts + the flat region partition (ADR-085) + labels + inter-region
    /// flow, stamped with the topology it was computed against (#195). Labels are cached-or-generic;
    /// naming a region costs a model call, and a read tool never spends (#190).
    ///
    /// THE WORD IS "REGION" (U29 #918). The segmenter's partitions were presented here as "clusters", labelled
    /// "Cluster N" on a cache miss - and "Cluster" is a Grasshopper component type with its own tool
    /// (get_cluster). The model called get_cluster on a region id on every fresh session, and once told the
    /// user its work had been collapsed into "a single opaque Cluster 0" on a canvas holding no clusters. The
    /// payload now says regions, get_region's own noun; "insideClusters" stays, because it IS about Grasshopper
    /// Cluster components. <c>RegionVocabulary</c> holds the rule that the noun is never an installed type.</summary>
    public static ToolResult GetOverview(AgentSnapshot snap, string partitionStamp = null)
    {
      var regions = new JsonArray();
      foreach (var c in snap.Clusters.OrderBy(c => c.Id))
      {
        var types = new JsonObject();
        foreach (var grp in c.Members
                   .Select(m => snap.NodeById(m)).Where(n => n != null)
                   .GroupBy(n => n.Type).OrderBy(g => g.Key))
          types[grp.Key] = grp.Count();

        regions.Add(new JsonObject
        {
          ["id"] = c.Id,
          ["label"] = c.Label,
          ["size"] = c.Members.Count,
          ["types"] = types,
        });
      }

      var flow = new JsonArray();
      foreach (var (from, to) in snap.Flow.OrderBy(f => f.From).ThenBy(f => f.To))
        flow.Add(new JsonObject { ["from"] = from, ["to"] = to });

      var result = new JsonObject
      {
        // Top-level counts; what sits inside Grasshopper clusters is counted separately (Phase-9 U1(c)), so the
        // overview describes the canvas the user sees and says how much more is folded away.
        ["counts"] = new JsonObject
        {
          ["components"] = snap.Nodes.Count(n => n.Kind == "component" && !n.Inside.HasValue),
          ["params"] = snap.Nodes.Count(n => n.Kind == "param" && !n.Inside.HasValue),
          ["edges"] = snap.Edges.Count(e => !IsInside(snap, e.FromNode) && !IsInside(snap, e.ToNode)),
          ["regions"] = snap.Clusters.Count,
          ["insideClusters"] = snap.Nodes.Count(n => n.Inside.HasValue),
        },
        ["regions"] = regions,
        ["flow"] = flow,
        // U27 (#917): every number says what it counts, in the payload. U29 (#918): and what a region IS.
        ["means"] = new JsonObject
        {
          ["counts"] = "components and params are the objects on the canvas at the top level; edges are the wires " +
                       "between them; regions is how many regions the definition was cut into; insideClusters is " +
                       "how many objects sit inside Grasshopper Cluster components, not counted above.",
          ["regions"] = "a region is a group of components Dwight cut from the dataflow for reading - NOT a " +
                        "Grasshopper Cluster component. Read one with get_region by its id here; get_cluster is " +
                        "for a Cluster component, by that component's id. A label \"Region N\" means nobody has " +
                        "named it yet.",
          ["size"] = "how many components and params a region holds.",
          ["types"] = "a histogram of the region's component types: type name -> how many of that type.",
        },
      };
      if (partitionStamp != null) result["partition"] = partitionStamp;
      return ToolResult.Success(result);
    }

    /// <summary>
    /// get_region: one cluster's members, its internal edges, and the boundary that crosses its edge
    /// (the shape Simplify's payload already uses).
    ///
    /// #195 - THE PARTITION GUARD. A region id is an index into a segmentation, and the segmentation is
    /// recomputed from the live topology. If the canvas was rewired since <c>get_overview</c>, id 2 may
    /// now name a DIFFERENT region - the #191 hazard's shape, at the region layer. Rather than plead
    /// with the model in a description, the surface COMPUTES it: the stamp taken at partition time is
    /// checked against the current graph, and a mismatch REFUSES. A deterministic failure class the
    /// plugin can compute is the plugin's job, never a prompt's (the #141/#146 law).
    /// </summary>
    public static ToolResult GetRegion(AgentSnapshot snap, JsonObject args, string expectedStamp = null, AgentIdRegistry ids = null)
    {
      if (!TryInt(args, "id", out var id, out var err)) return err;

      var stale = CheckPartition(snap, expectedStamp);
      if (stale != null) return stale;

      var cluster = snap.ClusterById(id);
      if (cluster == null)
        return ToolResult.Fail(ToolErrors.UnknownId,
          $"No region with id {id}. Call get_overview for the regions on this definition.",
          new JsonObject { ["id"] = id, ["known"] = Ids(snap.Clusters.Select(c => c.Id)) });

      var members = new HashSet<int>(cluster.Members);
      var nodes = new JsonArray();
      foreach (var n in snap.Nodes.Where(n => members.Contains(n.Id)).OrderBy(n => n.Id))
        nodes.Add(NodeBrief(n));

      var inner = new JsonArray();
      var crossIn = new JsonArray();
      var crossOut = new JsonArray();
      foreach (var e in snap.Edges.OrderBy(e => e.FromNode).ThenBy(e => e.ToNode))
      {
        var f = members.Contains(e.FromNode);
        var t = members.Contains(e.ToNode);
        if (f && t) inner.Add(EdgeJson(e));
        else if (t) crossIn.Add(EdgeJson(e));
        else if (f) crossOut.Add(EdgeJson(e));
      }

      // U22: every component this read returned is now READ - the turn-start fact compares it with the live one.
      RecordReads(snap, ids, members);

      var means = new JsonObject { ["sig"] = SigMeans };
      var result = new JsonObject
      {
        ["id"] = cluster.Id,
        ["label"] = cluster.Label,
        ["nodes"] = nodes,
        ["edges"] = inner,
        ["boundary"] = new JsonObject { ["inputs"] = crossIn, ["outputs"] = crossOut },
        ["means"] = means,
      };
      if (snap.ReadRevision.HasValue)
      {
        result["readRevision"] = snap.ReadRevision.Value;
        means["readRevision"] = ReadRevisionMeans;
      }
      return ToolResult.Success(result);
    }

    /// <summary>What <c>readRevision</c> is, wherever a read carries one (U22) - one definition for get_component and
    /// get_region. It is stated for what it is: a count of the changes this session's reads have SEEN, not a version the
    /// document keeps, so the sentence cannot be read as more than it is.</summary>
    public const string ReadRevisionMeans =
      "readRevision numbers the states of the canvas this session's reads have seen: it rises by one each time a read " +
      "finds a component's type, name, wiring, held value or script source different from the read before, and a move " +
      "never raises it. Two reads with the same readRevision saw the same canvas; the higher one is the later. It is not " +
      "a Grasshopper version number. Which components you hold a stale reading of is stated at the start of each turn.";

    /// <summary>
    /// U22: record, against each id, the content a read just returned. Only a stamped snapshot carries stamps, and only a
    /// caller with a registry records - so a read through a path with neither (the MCP face's tests, a fixture) is
    /// unchanged. A node with no stamp (an object the fingerprint could not read) is skipped rather than recorded as
    /// "unchanged": a reading we cannot compare is not one we can call fresh.
    /// </summary>
    internal static void RecordReads(AgentSnapshot snap, AgentIdRegistry ids, IEnumerable<int> nodeIds)
    {
      if (snap == null || ids == null || nodeIds == null) return;
      var revision = snap.ReadRevision ?? 0;
      foreach (var id in nodeIds)
      {
        var node = snap.NodeById(id);
        if (node?.Stamp != null) ids.RecordRead(id, node.Stamp, node.Inside, revision);
      }
    }

    /// <summary>What a wire's <c>sig</c> is, wherever a read carries one (U27 #917) - one definition for
    /// get_region, get_connections and get_tree_signature.</summary>
    private const string SigMeans =
      "a wire's sig is its data-tree signature: paths is the branch count, depth the path depth, items the item " +
      "count per branch in branch order (the first 24 branches). Structure only, never values; a wire with no " +
      "sig carried no data at the last solution.";

    /// <summary>
    /// Refuse a region id whose partition no longer describes the canvas (#195). Two failure modes,
    /// both refused rather than guessed at: no partition was ever computed in this session, or the
    /// topology has changed since it was. A PURE MOVE changes neither the segmentation nor this stamp,
    /// so tidying the canvas never costs the agent its region ids.
    /// </summary>
    private static ToolResult CheckPartition(AgentSnapshot snap, string expectedStamp)
    {
      if (expectedStamp == null)
        return ToolResult.Fail(ToolErrors.StalePartition,
          "No regions have been computed in this session. Call get_overview first.");

      if (!string.Equals(expectedStamp, ShapeHash(snap), StringComparison.Ordinal))
        return ToolResult.Fail(ToolErrors.StalePartition,
          "The canvas was rewired since these regions were computed, so region ids no longer mean " +
          "what they did. Call get_overview again.");

      return null;
    }

    /// <summary>get_component: the full node - ports, runtime messages, per-output counts. The
    /// messages and counts are the same deterministic signals Diagnose reads (ADR-078).</summary>
    public static ToolResult GetComponent(AgentSnapshot snap, AgentIdRegistry ids, JsonObject args)
    {
      if (!TryInt(args, "id", out var id, out var err)) return err;
      if (!TryResolve(snap, ids, id, out var n, out var resolveErr)) return resolveErr;

      var msgs = new JsonArray();
      foreach (var m in n.Msgs) msgs.Add(new JsonObject { ["level"] = m.Level, ["text"] = m.Text });

      var result = new JsonObject
      {
        ["id"] = n.Id,
        ["type"] = n.Type,
        ["nick"] = n.Nick,
        ["kind"] = n.Kind,
        ["inputs"] = new JsonArray((n.Inputs ?? Array.Empty<string>()).Select(s => (JsonNode)s).ToArray()),
        ["outputs"] = new JsonArray((n.Outputs ?? Array.Empty<string>()).Select(s => (JsonNode)s).ToArray()),
        ["msgs"] = msgs,
        ["outCounts"] = new JsonArray((n.OutCounts ?? Array.Empty<int>()).Select(i => (JsonNode)i).ToArray()),
      };
      Dwight.Session.DwightNotes.AddTo(result, n.Notes);   // Brief DC
      if (n.Qualified != null) result["qualified"] = n.Qualified;   // U25 (#915)
      AppendFacts(n, result);   // Phase-9 U1: what it holds - values, source, internalised data, plugin
      var means = ComponentMeans(n);   // U27 (#917): every number says what it counts
      if (snap.ReadRevision.HasValue)
      {
        result["readRevision"] = snap.ReadRevision.Value;   // U22
        means["readRevision"] = ReadRevisionMeans;
      }
      result["means"] = means;
      RecordReads(snap, ids, new[] { id });   // U22: this component is now READ
      return ToolResult.Success(result);
    }

    /// <summary>The sentence for each counted fact get_component states, keyed by the field it explains -
    /// only for the facts this node actually carries, so a slider's read is not padded with a script's.</summary>
    private static JsonObject ComponentMeans(AgentNode n)
    {
      var m = new JsonObject { ["outCounts"] = SolveCheckReport.OutCountsMeans };
      if (n.Qualified != null) m["qualified"] = QualifiedMeans;
      if (n.Value?.Displayed != null) m["displayed"] = PanelDisplay.Means;
      if (n.Value != null && (n.Value.Text != null || n.Value.Options.HasValue))
        m["value"] = "value.chars is the text's full length in characters when it was cut; value.options is how many " +
                     "items a value list offers, of which value.selected are chosen.";
      if (n.Persistent != null && n.Persistent.Count > 0)
        m["persistent"] = "per input holding internalised data: count is its item count across every branch, paths " +
                          "its branch count, item the first item's type (with its value when primitive).";
      if (n.ScriptLanguage != null && n.Source != null)
        m["script"] = "script.lines and script.chars are the whole source's size; source is cut at the bound stated " +
                      "beside it when truncated is true.";
      if (n.Cluster != null && (n.Cluster.Count.HasValue || n.Cluster.Omitted > 0))
        m["cluster"] = "cluster.count is how many objects sit inside this Cluster component, one level deep; " +
                       "cluster.omitted how many of them get_cluster will not list.";
      return m;
    }

    /// <summary>get_connections: the wires into and out of a node, with their tree signatures.</summary>
    public static ToolResult GetConnections(AgentSnapshot snap, AgentIdRegistry ids, JsonObject args)
    {
      if (!TryInt(args, "id", out var id, out var err)) return err;
      if (!TryResolve(snap, ids, id, out _, out var resolveErr)) return resolveErr;

      var up = new JsonArray();
      var down = new JsonArray();
      foreach (var e in snap.Edges.OrderBy(e => e.FromNode).ThenBy(e => e.ToNode))
      {
        if (e.ToNode == id) up.Add(EdgeJson(e));
        if (e.FromNode == id) down.Add(EdgeJson(e));
      }
      return ToolResult.Success(new JsonObject { ["id"] = id, ["upstream"] = up, ["downstream"] = down, ["means"] = SigMeans });
    }

    /// <summary>
    /// get_tree_signature: the signature on ONE wire, addressed by the (from, to) param quad rather
    /// than a positional edge index - an index is an ordinal into a projection and would drift for
    /// exactly the reason #191 exists; the quad is grounded in ids that cannot.
    /// </summary>
    public static ToolResult GetTreeSignature(AgentSnapshot snap, AgentIdRegistry ids, JsonObject args)
    {
      var from = args?["from"] as JsonObject;
      var to = args?["to"] as JsonObject;
      if (from == null || to == null)
        return ToolResult.Fail(ToolErrors.InvalidArgs,
          "Requires \"from\":{node,param} and \"to\":{node,param} - the wire's two ends.");

      if (!TryInt(from, "node", out var fromNode, out var e1)) return e1;
      if (!TryInt(to, "node", out var toNode, out var e2)) return e2;
      var fromParam = OptString(from, "param");
      var toParam = OptString(to, "param");

      if (!TryResolve(snap, ids, fromNode, out _, out var er1)) return er1;
      if (!TryResolve(snap, ids, toNode, out _, out var er2)) return er2;

      var edge = snap.Edges.FirstOrDefault(x => x.Matches(fromNode, fromParam, toNode, toParam));
      if (edge == null)
      {
        // Honest and actionable: list the wires that DO run between these two nodes.
        var between = new JsonArray();
        foreach (var x in snap.Edges.Where(x => x.FromNode == fromNode && x.ToNode == toNode))
          between.Add(EdgeJson(x));
        return ToolResult.Fail(ToolErrors.UnknownId,
          $"No wire runs from {fromNode}.{fromParam} to {toNode}.{toParam}.",
          new JsonObject { ["wiresBetweenTheseNodes"] = between });
      }

      var sig = SigJson(edge.Tree);
      if (sig == null)
        return ToolResult.Success(new JsonObject
        {
          ["empty"] = true,
          ["means"] = "this wire carried no data at the last solution, so it has no signature.",
        });
      sig["means"] = SigMeans;
      return ToolResult.Success(sig);
    }

    /// <summary>search: ids whose type or nickname matches. Either <c>type</c> (exact type name, or its qualified
    /// form - U25 #915) or <c>query</c> (substring over type + nickname). Truncation is REPORTED, never silent.</summary>
    public static ToolResult SearchNodes(AgentSnapshot snap, JsonObject args)
    {
      var type = OptString(args, "type");
      var query = OptString(args, "query");
      if (type == null && query == null)
        return ToolResult.Fail(ToolErrors.InvalidArgs, "Requires \"query\" or \"type\".");

      // A qualified type names ONE of the installed types sharing a bare name; it matches only the nodes carrying
      // that exact form. Any spacing around '>' is accepted, so the comparison is on the canonical form.
      var qualifiedType = type != null && QualifiedNames.TryParse(type, out var qc, out var qs, out var qn)
        ? QualifiedNames.Format(qc, qs, qn) : null;

      bool Hit(AgentNode n)
      {
        if (qualifiedType != null && !string.Equals(n.Qualified ?? "", qualifiedType, StringComparison.OrdinalIgnoreCase)) return false;
        if (qualifiedType == null && type != null && !string.Equals(n.Type ?? "", type, StringComparison.OrdinalIgnoreCase)) return false;
        if (query == null) return true;
        return (n.Type ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
            || (n.Nick ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
      }

      var matches = snap.Nodes.Where(Hit).OrderBy(n => n.Id).ToList();
      var shown = matches.Take(SearchCap).ToList();

      var arr = new JsonArray();
      foreach (var n in shown) arr.Add(NodeBrief(n));

      var means = new JsonObject
      {
        ["count"] = "every component on the canvas that matched, listed or not; matches is cut at " + SearchCap +
                    " and shown says how many are listed when it was.",
      };
      if (shown.Any(n => n.Qualified != null)) means["qualified"] = QualifiedMeans;   // said where it is shown

      var result = new JsonObject
      {
        ["matches"] = arr,
        ["count"] = matches.Count,
        ["means"] = means,
      };
      if (matches.Count > shown.Count)
      {
        result["truncated"] = true;                       // no silent caps (#134)
        result["shown"] = shown.Count;
      }
      return ToolResult.Success(result);
    }
  }
}
