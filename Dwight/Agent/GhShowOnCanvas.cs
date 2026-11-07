using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Grasshopper.Kernel;

namespace Dwight.Agent
{
  /// <summary>The Grasshopper end of <c>show_on_canvas</c>: the live canvas answers whether an id's component is there, and the glow and the pan are the chat's re-light (glow-only, never a selection).</summary>
  internal static class GhShowOnCanvas
  {
    public static ToolResult Run(GH_Document doc, AgentScope scope, JsonObject args)
    {
      scope.Ids.Sync(LiveGuids(doc));   // aliveness is never stored; the live document is the only source
      return ShowOnCanvasCore.Run(args, scope.Ids, g => doc.FindObject(g, false) != null, new Pointer(scope));
    }

    private static IEnumerable<Guid> LiveGuids(GH_Document doc)
    {
      foreach (var o in doc.Objects) yield return o.InstanceGuid;
    }

    private sealed class Pointer : ICanvasPointer
    {
      private readonly AgentScope _owner;
      public Pointer(AgentScope owner) { _owner = owner; }

      public void Light(IReadOnlyList<Guid> guids, bool dim) => Dwight.UI.CanvasGlow.ShowWalkStep(guids, null, _owner, dim);
      public void Pan(IReadOnlyList<Guid> guids) => Dwight.UI.CanvasNav.GoTo(guids, Dwight.UI.NavReason.ChatPointed);
    }
  }
}
