using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace Dwight.Agent
{
  /// <summary>What <c>show_on_canvas</c> does to the screen, behind a seam so the resolution is tested without Grasshopper. The host's is glow-only.</summary>
  internal interface ICanvasPointer
  {
    /// <summary>Glow these components; wash the rest when <paramref name="dim"/>. Never a selection change.</summary>
    void Light(IReadOnlyList<Guid> guids, bool dim);

    /// <summary>Bring the set into view.</summary>
    void Pan(IReadOnlyList<Guid> guids);
  }

  /// <summary>
  /// <c>show_on_canvas</c> (Brief EE unit 4): the model points at components while it explains. A read: it changes nothing in the definition, costs no
  /// tokens from the canvas and runs no solve. THE PLUGIN RESOLVES THE IDS (ADR-056): each id goes through the thread's registry and must be alive on the
  /// canvas now, and an id it cannot place refuses the whole call with the real list (#208), so a glow never lands on a neighbour or on fewer components
  /// than the model named. The person's selection is never touched (#322).
  /// </summary>
  internal static class ShowOnCanvasCore
  {
    /// <summary>The most components one call may name. A glow on hundreds is not pointing at anything.</summary>
    public const int MaxIds = 60;

    public static ToolResult Run(JsonObject args, AgentIdRegistry ids, Func<Guid, bool> onCanvas, ICanvasPointer pointer)
    {
      if (!(args?["ids"] is JsonArray array) || array.Count == 0)
        return ToolResult.Fail(ToolErrors.InvalidArgs, "Requires \"ids\": the component ids to light, from this session's reads.");
      if (array.Count > MaxIds)
        return ToolResult.Fail(ToolErrors.InvalidArgs, "At most " + MaxIds + " ids in one call; name the components you are talking about, not the whole canvas.");

      var wanted = new List<int>();
      foreach (var node in array)
      {
        try { wanted.Add(node.GetValue<int>()); }
        catch { return ToolResult.Fail(ToolErrors.InvalidArgs, "\"ids\" must be integers."); }
      }

      var dim = false;
      if (args["dim"] != null)
      {
        try { dim = args["dim"].GetValue<bool>(); }
        catch { return ToolResult.Fail(ToolErrors.InvalidArgs, "\"dim\" must be true or false."); }
      }

      var shown = new List<int>();
      var guids = new List<Guid>();
      var refused = new List<int>();
      foreach (var id in wanted.Distinct())
      {
        var state = ids?.State(id) ?? IdState.Unknown;
        if (state == IdState.Alive && ids.TryGetGuid(id, out var g) && onCanvas != null && onCanvas(g)) { shown.Add(id); guids.Add(g); }
        else refused.Add(id);
      }

      if (refused.Count > 0)
        return ToolResult.Fail(ToolErrors.UnknownId,
          "Nothing was lit. No component with " + (refused.Count == 1 ? "id " : "ids ") + string.Join(", ", refused) +
          " is on the canvas now. Ids come from this session's reads; do not invent them, and re-read if the canvas has changed.",
          new JsonObject
          {
            ["refused"] = new JsonArray(refused.Select(i => (JsonNode)i).ToArray()),
            ["alive"] = new JsonArray((ids?.AliveIds() ?? new List<int>()).Take(200).Select(i => (JsonNode)i).ToArray()),
          });

      pointer?.Light(guids, dim);
      pointer?.Pan(guids);
      return ToolResult.Success(new JsonObject
      {
        ["shown"] = new JsonArray(shown.Select(i => (JsonNode)i).ToArray()),
        ["dimmed"] = dim,
        ["panned"] = pointer != null,
      });
    }
  }
}
