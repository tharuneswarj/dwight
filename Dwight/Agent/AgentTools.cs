using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Grasshopper;
using Grasshopper.Kernel;
using Dwight.Components;

namespace Dwight.Agent
{
  /// <summary>
  /// One agent session's view of one document: the append-only id registry (#191) plus the document it
  /// is bound to. Ids are meaningful only inside a scope, which is exactly why the scope owns the
  /// registry - a new scope starts a new id space rather than inheriting a stale one.
  /// </summary>
  internal sealed class AgentScope
  {
    public AgentIdRegistry Ids { get; } = new AgentIdRegistry();

    /// <summary>The topology stamp of the last partition <c>get_overview</c> computed (#195). The
    /// SURFACE holds it, not the model: the agent never has to carry it back, and cannot forge it.
    /// Null until a partition exists, which is itself a refusable state.</summary>
    public string PartitionStamp { get; set; }

    /// <summary>
    /// The conversation this scope belongs to, or null.
    ///
    /// It exists for <c>get_scaffold_map</c>, whose PLAN half lives in the thread's own record. Null is a
    /// first-class state rather than an oversight: the MCP face and the DEBUG harness have no thread, and
    /// there the map still answers from the CANVAS - which is the authoritative half anyway (#221b). A
    /// scope with no thread loses the plan detail and says so; it never guesses at another thread's map.
    /// </summary>
    public string ThreadId { get; }

    /// <summary>
    /// THE LEDGERS, BY DOCUMENT (F1(b)).
    ///
    /// It is static and keyed by the document rather than held per-scope, because AgentScope is cached
    /// PER THREAD (DwightScaffold._scopes) and "Dwight placed this" is a fact about the DOCUMENT. Held
    /// per scope it would create a cliff the user cannot see: "rebuild A1" works in the chat that built
    /// it and silently refuses in a new one, for a reason that exists nowhere in their model of the
    /// product.
    ///
    /// IN MEMORY ONLY, and the consequence is stated rather than hidden: a Rhino restart forgets what
    /// Dwight placed, so a rebuild after a restart refuses. That is the same cliff, made much rarer, and
    /// making it disappear entirely would mean persisting a delete-claim to disk - a bigger step than
    /// this ruling covers.
    /// </summary>
    private static readonly Dictionary<Guid, BuildLedger> _ledgers = new Dictionary<Guid, BuildLedger>();

    /// <summary>
    /// The ledger of what THIS TURN built, per region (#352's constraint 2) - the list a rebuild is
    /// allowed to delete from.
    ///
    /// TURN-SCOPING IS HELD BY IDENTITY, NOT BY A CLEAR() SOMEBODY MUST REMEMBER. A caller passes the
    /// object identity of its own unit of work; a different one is handed a FRESH, empty ledger and the
    /// old one is dropped. An empty ledger refuses a rebuild, so the failure direction of "the turn
    /// changed" is REFUSE - never "delete something we no longer have a claim to".
    ///
    /// A caller with no turns at all (the DEBUG harness, and the v1.1 MCP face, which has no turn
    /// concept) passes a stable owner and gets one ledger for the scope's life. That is its unit of
    /// work, and the same rule still holds inside it: only guids this surface itself placed.
    /// </summary>
    /// <summary>
    /// The document's ledger, for a caller that only wants to READ it - the consent card, which must
    /// know what a rebuild would remove before the user approves anything.
    ///
    /// It deliberately does NOT call BeginTurn. Drawing a card is not starting a turn, and resetting the
    /// turn's permission as a side effect of rendering would be a surprising and dangerous thing for a
    /// read to do.
    /// </summary>
    public static BuildLedger LedgerForDocument(GH_Document doc)
    {
      var key = Guid.Empty;
      try { key = doc?.DocumentID ?? Guid.Empty; } catch { }
      return _ledgers.TryGetValue(key, out var ledger) ? ledger : null;
    }

    public BuildLedger LedgerFor(object turn)
    {
      // The document's own ledger - shared by every thread on it, created once.
      var key = Guid.Empty;
      try { key = _boundDoc?.DocumentID ?? Guid.Empty; } catch { }

      if (!_ledgers.TryGetValue(key, out var ledger))
        _ledgers[key] = ledger = new BuildLedger(key);

      // The FACTS carry over; the PERMISSION does not. BeginTurn replaces the turn's authorisation and
      // attempt counts wholesale, so an approval can never outlive the turn it was given in.
      ledger.BeginTurn(turn);
      return ledger;
    }

    private readonly GH_Document _boundDoc;

    public AgentScope(GH_Document doc, string threadId = null)
    {
      _boundDoc = doc;
      ThreadId = threadId;
      ComponentWhitelist.Reset();   // re-enumerate: a plugin loaded since startup must be visible
    }

    /// <summary>The bound document, or a coded refusal if it is gone or no longer active. The
    /// stale-document guard every shipped apply path already runs (ADR-049), at the tool boundary.</summary>
    public GH_Document Document(out ToolResult error)
    {
      error = null;
      if (_boundDoc == null)
      {
        error = ToolResult.Fail(ToolErrors.NoDocument, "No Grasshopper document is open.");
        return null;
      }
      GH_Document active = null;
      try { active = Instances.ActiveCanvas?.Document; } catch { }
      if (!ReferenceEquals(active, _boundDoc))
      {
        error = ToolResult.Fail(ToolErrors.StaleDocument,
          "The active document changed since this session started; ids from it no longer apply.");
        return null;
      }
      return _boundDoc;
    }

    /// <summary>Is this scope bound to <paramref name="doc"/>? For a CALLER that legitimately starts a
    /// fresh session when the user moves to another document - a new session, a new id space. It is not
    /// an escape from the guard above: an existing scope still refuses a document it was not bound to.</summary>
    public bool IsBoundTo(GH_Document doc) => ReferenceEquals(_boundDoc, doc);
  }

  /// <summary>
  /// THE AGENT TOOL SURFACE (Phase-8 WP-2; ADR-069/098). A registry of named tools with JSON Schemas and
  /// a single <see cref="Invoke"/> entry point - which is precisely the MCP shape (<c>list_tools</c> /
  /// <c>call_tool</c>), so the v1.1 face is a transport adapter over this and never a second definition
  /// of the same surface.
  ///
  /// THE LAW THIS SURFACE EXISTS TO KEEP (#186): capability grows by adding VERIFIED tools, never by
  /// adding a god-tool. Reads may be broad - they cost tokens, never correctness. A write enters only
  /// wrapped in a stated proof, and every mutating tool returns its verification VERDICT (ADR-098).
  ///
  /// Read tools are ZERO-TOKEN, SOLVE-FREE and MUTATION-FREE (#190): none calls a model, runs a
  /// solution, or touches the document.
  ///
  /// THREADING: <see cref="Invoke"/> reads the live document, so callers marshal it to the UI thread.
  /// WP-2's caller is one operation per call; WP-3's loop wraps many calls in one session envelope.
  /// </summary>
  internal static class AgentTools
  {
    public const string GetOverview = AgentToolCatalog.GetOverview;
    public const string GetRegion = AgentToolCatalog.GetRegion;
    public const string GetComponent = AgentToolCatalog.GetComponent;
    public const string GetConnections = AgentToolCatalog.GetConnections;
    public const string GetTreeSignature = AgentToolCatalog.GetTreeSignature;
    public const string Search = AgentToolCatalog.Search;
    public const string KbLookup = AgentToolCatalog.KbLookup;
    public const string Diagnose = AgentToolCatalog.Diagnose;
    public const string GetScaffoldMap = AgentToolCatalog.GetScaffoldMap;

    /// <summary>The advertised surface - the MCP <c>list_tools</c> response. The descriptors live GH-free
    /// in <see cref="AgentToolCatalog"/> so their shape is tested; this is the shim's view of them.</summary>
    public static IReadOnlyList<ToolDescriptor> List() => AgentToolCatalog.All();

    public const string SolveAndCheck = AgentToolCatalog.SolveAndCheck;
    public const string BuildRegion = AgentToolCatalog.BuildRegion;
    public const string ProposeScaffold = AgentToolCatalog.ProposeScaffold;

    /// <summary>
    /// Call one tool. Always returns the wire envelope - success or a coded refusal - and never throws
    /// across the boundary: an agent loop derailed by an exception cannot recover, but one handed a
    /// coded refusal can.
    ///
    /// SYNCHRONOUS tools only. A tool that solves or mutates cannot answer on the calling thread -
    /// waiting for <c>SolutionEnd</c> from the UI thread would deadlock against the very solve it is
    /// waiting for - so those go through <see cref="InvokeAsync"/> and this refuses them rather than
    /// pretending.
    /// </summary>
    public static string Invoke(string name, string argsJson, AgentScope scope)
    {
      try
      {
        var descriptor = AgentToolCatalog.All().FirstOrDefault(
          t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
        if (descriptor != null && (descriptor.Solves || descriptor.Mutates))
          return ToolResult.Fail(ToolErrors.PreconditionRefused,
            $"\"{name}\" runs the solution or changes the document and must be called asynchronously.").ToJson();

        return Dispatch(name, argsJson, scope).ToJson();
      }
      catch (Exception ex)
      {
        // Fail safe (ADR-029): an unexpected fault is reported as a refusal, not thrown at the caller.
        return ToolResult.Fail(ToolErrors.PreconditionRefused,
          "The tool could not complete (" + ex.Message + ").").ToJson();
      }
    }

    /// <summary>
    /// Call any tool, including the ones that solve or mutate. Reads complete immediately; solving and
    /// mutating tools report from their scheduled callback. WP-3's agent loop drives this form.
    ///
    /// <paramref name="done"/> is invoked exactly once, with the wire envelope, on every path -
    /// including refusals and faults. A tool that can silently never answer would hang the agent.
    /// </summary>
    public static void InvokeAsync(string name, string argsJson, AgentScope scope, Action<string> done,
                                   object turn = null)
    {
      Action<ToolResult> finish = r => { try { done?.Invoke(r.ToJson()); } catch { } };
      try
      {
        if (scope == null) { finish(ToolResult.Fail(ToolErrors.NoDocument, "No agent session is open.")); return; }
        if (!AgentToolCatalog.Knows(name))
        {
          finish(ToolResult.Fail(ToolErrors.UnknownTool, $"No tool named \"{name}\".",
            new JsonObject { ["available"] = new JsonArray(List().Select(t => (JsonNode)t.Name).ToArray()) }));
          return;
        }

        var doc = scope.Document(out var docError);
        if (docError != null) { finish(docError); return; }

        if (string.Equals(name, SolveAndCheck, StringComparison.OrdinalIgnoreCase))
        {
          GhSolveCheck.Run(doc, scope.Ids, finish);
          return;
        }

        // Brief CI: kb_lookup may instantiate a type, under a clock that answers from its own thread, so it reports from a callback like the tools here.
        if (string.Equals(name, KbLookup, StringComparison.OrdinalIgnoreCase))
        {
          var args = AgentToolCore.ParseArgs(argsJson, out var kbArgError);
          if (kbArgError != null) { finish(kbArgError); return; }
          GhKbPorts.Run(doc, args, GhKbPorts.NextOnUi, finish);
          return;
        }

        if (string.Equals(name, BuildRegion, StringComparison.OrdinalIgnoreCase))
        {
          var args = AgentToolCore.ParseArgs(argsJson, out var buildArgError);
          if (buildArgError != null) { finish(buildArgError); return; }
          // The ledger is fetched for the CALLER'S TURN (#352's constraint 2). The loop passes its own
          // TurnRequest, whose identity is fresh per turn and which it cannot run without holding - so
          // turn-scoping is structural rather than a clear() that could be forgotten. A caller with no
          // turn (the harness; the v1.1 MCP face) passes nothing and gets one ledger per scope, which is
          // its unit of work.
          GhBuildRegion.Run(doc, scope.Ids, scope.LedgerFor(turn ?? scope), args, finish);
          return;
        }

        if (string.Equals(name, ProposeScaffold, StringComparison.OrdinalIgnoreCase))
        {
          var args = AgentToolCore.ParseArgs(argsJson, out var scaffoldArgError);
          if (scaffoldArgError != null) { finish(scaffoldArgError); return; }
          // The stored map travels in for the same reason get_scaffold_map reads it: a new map must not
          // mint an address this conversation's plan already uses, even where the group has been undone (#720).
          GhProposeScaffold.Run(doc, scope.Ids, args, StoredMapFor(doc, scope), finish);
          return;
        }

        // Phase-9 U4: the write path the MCP face calls; the panel reaches the same applier from its card.
        if (string.Equals(name, AgentToolCatalog.SetValue, StringComparison.OrdinalIgnoreCase))
        {
          var args = AgentToolCore.ParseArgs(argsJson, out var setValueArgError);
          if (setValueArgError != null) { finish(setValueArgError); return; }
          GhSetValue.Run(doc, scope.Ids, args, finish);
          return;
        }

        finish(Dispatch(name, argsJson, scope));
      }
      catch (Exception ex)
      {
        finish(ToolResult.Fail(ToolErrors.PreconditionRefused, "The tool could not complete (" + ex.Message + ")."));
      }
    }

    private static ToolResult Dispatch(string name, string argsJson, AgentScope scope)
    {
      if (scope == null) return ToolResult.Fail(ToolErrors.NoDocument, "No agent session is open.");

      var args = AgentToolCore.ParseArgs(argsJson, out var argError);
      if (argError != null) return argError;

      if (!AgentToolCatalog.Knows(name))
        return ToolResult.Fail(ToolErrors.UnknownTool, $"No tool named \"{name}\".",
          new JsonObject { ["available"] = new JsonArray(List().Select(t => (JsonNode)t.Name).ToArray()) });

      var doc = scope.Document(out var docError);
      if (docError != null) return docError;

      // Brief EE: show_on_canvas resolves its ids through the registry and points; it needs no snapshot and changes nothing.
      if (string.Equals(name, AgentToolCatalog.ShowOnCanvas, StringComparison.OrdinalIgnoreCase))
        return GhShowOnCanvas.Run(doc, scope, args);

      // kb_lookup asks about the INSTALLED set, not this canvas, so it needs no snapshot.
      if (string.Equals(name, KbLookup, StringComparison.OrdinalIgnoreCase)) return KnowledgeLookup(doc, args);

      // diagnose runs the deterministic fault scan over its own DwightGraph, so it needs no agent
      // snapshot either - and it stays a pure read (#190/#258).
      if (string.Equals(name, Diagnose, StringComparison.OrdinalIgnoreCase))
        return GhDiagnose.Run(doc, scope.Ids, args);

      // get_scaffold_map reads GROUPS and Dwight's own per-file record - no snapshot, no segmenter, and
      // no ids to ground beyond the anchors the stored plan already holds.
      if (string.Equals(name, GetScaffoldMap, StringComparison.OrdinalIgnoreCase))
        return GhScaffoldMap.Run(doc, StoredMapFor(doc, scope));

      // Phase-9 U23 (lane C): get_solve_timing builds its own snapshot WITH regions and stamps the partition, so
      // its region ids are the ones get_region accepts. Routed as its own block rather than by editing the
      // needsClusters line below: this file is append-only for every lane (#867).
      if (string.Equals(name, AgentToolCatalog.GetSolveTiming, StringComparison.OrdinalIgnoreCase))
        return GhSolveTiming.Run(doc, scope, args);

      // Phase-9 U23 (lane C): the session facts. Neither needs a snapshot - units are the Rhino document's, and the
      // plugin inventory is the component server's plus one pass over this canvas - so both return before it.
      if (string.Equals(name, AgentToolCatalog.GetDocumentUnits, StringComparison.OrdinalIgnoreCase))
        return GhSessionFacts.DocumentUnits();
      if (string.Equals(name, AgentToolCatalog.GetInstalledPlugins, StringComparison.OrdinalIgnoreCase))
        return GhSessionFacts.InstalledPlugins(doc);

      // Phase-9 U23 (lane C): the file's saved state (two booleans, never a name), and the canvas layout as fact. The
      // layout builds its own snapshot for the ids, so both return before the shared one.
      if (string.Equals(name, AgentToolCatalog.GetFileState, StringComparison.OrdinalIgnoreCase))
        return GhCanvasFacts.FileState(doc);
      if (string.Equals(name, AgentToolCatalog.GetCanvasLayout, StringComparison.OrdinalIgnoreCase))
        return GhCanvasFacts.Layout(doc, scope);

      var needsClusters = string.Equals(name, GetOverview, StringComparison.OrdinalIgnoreCase)
                       || string.Equals(name, GetRegion, StringComparison.OrdinalIgnoreCase);
      // U22: the two reads that carry readRevision and record what they returned are the two that pay for stamps.
      var stamped = string.Equals(name, GetComponent, StringComparison.OrdinalIgnoreCase)
                 || string.Equals(name, GetRegion, StringComparison.OrdinalIgnoreCase);
      var snap = GhAgent.Build(doc, scope.Ids, needsClusters, stamped);

      switch (name.ToLowerInvariant())
      {
        case GetOverview:
          // Stamp the partition with the topology it was computed against, and remember it on the
          // scope so region-consuming tools can refuse a stale id without the model carrying it (#195).
          scope.PartitionStamp = AgentToolCore.ShapeHash(snap);
          return AgentToolCore.GetOverview(snap, scope.PartitionStamp);
        case GetRegion: return AgentToolCore.GetRegion(snap, args, scope.PartitionStamp, scope.Ids);
        case AgentToolCatalog.GetCluster: return AgentToolCore.GetCluster(snap, scope.Ids, args);

        // Phase-9 U2: the geometry read set - pure reads over the last solution's data (#190).
        case AgentToolCatalog.GetGeometrySummary: return GhGeometry.Summary(doc, snap, scope.Ids, args);
        case AgentToolCatalog.CheckMesh: return GhGeometry.CheckMesh(doc, snap, scope.Ids, args);
        case AgentToolCatalog.CheckBrep: return GhGeometry.CheckBrep(doc, snap, scope.Ids, args);
        case AgentToolCatalog.GetNakedEdges: return GhGeometry.NakedEdges(doc, snap, scope.Ids, args);
        case AgentToolCatalog.FindIntersecting: return GhGeometry.Intersections(doc, snap, scope.Ids, args);
        case AgentToolCatalog.GetMassProperties: return GhGeometry.MassProperties(doc, snap, scope.Ids, args);
        case AgentToolCatalog.SpatialQuery: return GhGeometry.Spatial(doc, snap, scope.Ids, args);
        case GetComponent: return AgentToolCore.GetComponent(snap, scope.Ids, args);
        case GetConnections: return AgentToolCore.GetConnections(snap, scope.Ids, args);
        case GetTreeSignature: return AgentToolCore.GetTreeSignature(snap, scope.Ids, args);
        case Search: return AgentToolCore.SearchNodes(snap, args);
        default: return ToolResult.Fail(ToolErrors.UnknownTool, $"No tool named \"{name}\".");
      }
    }

    /// <summary>The stored plan for this scope's conversation, or null when there is no thread to read one
    /// from. Never another thread's: a scope with no thread gets the canvas half only, honestly.</summary>
    private static string StoredMapFor(GH_Document doc, AgentScope scope)
    {
      if (string.IsNullOrEmpty(scope?.ThreadId)) return null;
      try { return Dwight.Session.CommandHistory.ThreadScaffoldMap(doc, scope.ThreadId); }
      catch { return null; }
    }

    /// <summary>
    /// kb_lookup - ADR-068's v1 lookup, kept HONEST. What it can answer truthfully it answers from the
    /// component's own SDK self-description, read live off <c>ComponentServer</c>: always correct,
    /// version-exact to this machine, zero maintenance (the ADR-092 argument - reflection over a
    /// curated map that is stale the day Rhino ships a point release).
    ///
    /// What it CANNOT answer truthfully it declines to invent. Ports are read from an instance on the
    /// canvas, or, since Brief CI, from a temporary instance of the one type asked about (<see cref="GhKbPorts"/>);
    /// where that faults or the clock runs out they are reported as "unavailable". An honest hole beats a
    /// fabricated schema: the demote-to-advice precedent (ADR-090) applied to knowledge.
    ///
    /// The curated community directory (ADR-068's enriched layer) gates nothing and is not in v1.
    ///
    /// This is the synchronous door (<see cref="Invoke"/>), so each step runs on the caller's thread; the agent loop comes through <see cref="InvokeAsync"/>.
    /// </summary>
    private static ToolResult KnowledgeLookup(GH_Document doc, JsonObject args)
    {
      // U25 (#915): the resolution is the GH-free core's, over EVERY installed proxy - not the whitelist's
      // first-wins name index, which is how "Rectangle" answered for the param and hid the component. A bare name
      // shared by more than one installed type returns each with its qualified form and says so; the qualified
      // form is accepted and resolves to the one it names. The single-match answer keeps its shape, plus
      // "qualified" so the model learns the form before it ever needs it.
      ToolResult answer = null;
      GhKbPorts.Run(doc, args, step => step(), r => answer = r);
      return answer ?? ToolResult.Fail(ToolErrors.PreconditionRefused, "The tool returned nothing.");
    }

    /// <summary>U62: a port as kb_lookup speaks it - "Radius (R)" - not the bare nickname the canvas labels it by.</summary>
    internal static string LookupPortLabel(IGH_Param p)
    {
      try { return LivePorts.PortLabel(p?.Name, p?.NickName); }
      catch { return "."; }
    }

    /// <summary>Real port names, but only when an instance of the type already sits on the canvas -
    /// then reading them costs nothing and cannot be wrong. U39 (#975): matched by the candidate's own
    /// ComponentGuid (the only match that tells a Rectangle param from a Rectangle component); a param instance
    /// answers with its own single in and out; by name only when no other installed type shares it. The decision is
    /// <see cref="LivePorts.Pick"/>'s, GH-free and tested.</summary>
    internal static JsonObject PortsFromLiveInstance(GH_Document doc, InstalledType info, IReadOnlyList<InstalledType> installed)
    {
      try
      {
        var live = new List<LiveObject>();
        foreach (var obj in doc.Objects)
        {
          if (obj is IGH_Component c)
            live.Add(new LiveObject
            {
              Guid = c.ComponentGuid, Name = c.Name,
              Inputs = c.Params.Input.Select(LookupPortLabel).ToList(),
              Outputs = c.Params.Output.Select(LookupPortLabel).ToList(),
            });
          else if (obj is IGH_Param p)
          {
            var label = LookupPortLabel(p);
            live.Add(new LiveObject
            {
              Guid = p.ComponentGuid, Name = p.Name, IsParam = true,
              Inputs = new[] { label }, Outputs = new[] { label },
            });
          }
        }
        var shared = (installed ?? new List<InstalledType>()).Any(t => t != null && t.Guid != info.Guid &&
          string.Equals((t.Name ?? "").Trim(), (info.Name ?? "").Trim(), StringComparison.OrdinalIgnoreCase));
        return LivePorts.Pick(live, info, shared);
      }
      catch { return null; }
    }
  }
}
