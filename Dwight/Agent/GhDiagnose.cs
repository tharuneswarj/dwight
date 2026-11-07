using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Grasshopper.Kernel;
using Dwight.Data;
using Dwight.Diagnostics;

namespace Dwight.Agent
{
  /// <summary>
  /// <c>diagnose</c> - the deterministic fault scan, on the agent surface (#258).
  ///
  /// THE EMPIRICAL CASE FOR VERBS-AS-TOOLS, made in the field. Given only primitive reads, the agent
  /// reconstructed Diagnose by sampling 25 script components, declared the definition clean, and was
  /// wrong by 150-200 failing params. An agent handed only primitives WILL rebuild a verb badly - not
  /// because it is careless, but because sampling is the only move available to it. <c>FaultScan</c>
  /// answers the same question exhaustively, in zero tokens, and has since Phase 4 (ADR-078).
  ///
  /// It is a PURE READ (#190): no model call, no solve, no mutation. It reports the runtime messages and
  /// output counts Grasshopper is already holding, so a stale solution yields stale facts - which is
  /// exactly what the <c>solve</c> consent card exists to refresh (#222).
  ///
  /// The FINDING stays deterministic and the EXPLAINING stays the model's (ADR-078's split), which is why
  /// this returns facts rather than prose.
  /// </summary>
  internal static class GhDiagnose
  {
    public static ToolResult Run(GH_Document doc, AgentIdRegistry ids, JsonObject args)
    {
      if (doc == null) return ToolResult.Fail(ToolErrors.NoDocument, "No Grasshopper document is open.");

      // Scope: the ids given (and everything upstream of them), or the whole definition.
      var raw = args?["ids"] as JsonArray;
      var guids = new List<Guid>();
      if (raw != null)
        foreach (var node in raw)
        {
          int id;
          try { id = node.GetValue<int>(); } catch { continue; }

          var state = ids.State(id);
          if (state == IdState.Unknown)
            return ToolResult.Fail(ToolErrors.UnknownId,
              "Id " + id + " is not a component this session has seen. Read the canvas first.");
          if (state == IdState.Retired)
            return ToolResult.Fail(ToolErrors.RetiredId,
              "Id " + id + " was on the canvas but is not any more, so there is nothing to diagnose there.");

          if (ids.TryGetGuid(id, out var guid)) guids.Add(guid);
        }

      var wholeCanvas = guids.Count == 0;

      DwightGraph graph;
      FaultScan.Report report;
      try
      {
        graph = DwightGraph.Build(doc, guids);
        report = FaultScan.Run(graph, doc, wholeCanvas);
      }
      catch (Exception ex)
      {
        return ToolResult.Fail(ToolErrors.PreconditionRefused, "The scan could not run (" + ex.Message + ").");
      }

      // Registry ids, so what comes back is addressable with every other tool (#191).
      ids.Sync(graph.Nodes.Select(n => n.Guid));

      // Roots first, then hard errors before warnings before silent suspicions - so a capped list is
      // still the useful end of it (the presentation order ADR-078 already fixed).
      var faults = report.Faults
        .OrderByDescending(f => f.IsRoot)
        .ThenBy(f => f.Kind)
        .ThenBy(f => f.Node.Id)
        .Select(f => new DiagnoseFaultDto
        {
          Id = ids.Issue(f.Node.Guid),
          Type = f.Node.Type,
          Nick = f.Node.Nick,
          Kind = f.Kind == FaultScan.Kind.Error ? "error" : f.Kind == FaultScan.Kind.Warning ? "warning" : "silent",
          Root = f.IsRoot,
          Intrinsic = f.Intrinsic,
          Message = FirstMessage(f.Node),
        })
        .ToList();

      return AgentToolCore.Diagnose(faults, report.Stray.Count, graph.Nodes.Count, wholeCanvas);
    }

    /// <summary>The component's own first runtime message - Grasshopper's words, not ours (ADR-078: the
    /// explanation is grounded in the real message, never guessed).</summary>
    private static string FirstMessage(GraphNode node)
    {
      try
      {
        var m = node?.Msgs?.FirstOrDefault();
        return m?.Text;
      }
      catch { return null; }
    }
  }
}
