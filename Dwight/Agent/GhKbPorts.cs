using System;
using System.Linq;
using System.Text.Json.Nodes;
using Dwight.Components;
using Grasshopper;
using Grasshopper.Kernel;

namespace Dwight.Agent
{
  /// <summary>
  /// Brief CI: the one part of kb_lookup's temporary instance that touches Grasshopper. <see cref="KbPortsRun"/> decides everything else and is tested.
  ///
  /// THE INSTANCE IS MADE, READ AND DROPPED, IN THIS FILE, ON THE UI THREAD. It is never added to a document and never solved, so nothing here can change the
  /// canvas or its undo stack. A type whose creation shows a dialog or faults is what the clock and the catch are for, and it is logged by name.
  /// </summary>
  internal static class GhKbPorts
  {
    public static void Run(GH_Document doc, JsonObject args, Action<Action> post, Action<ToolResult> finish)
    {
      var installed = GhAgent.InstalledTypes();
      KbPortsRun.Start(args, installed, name => ComponentWhitelist.Nearest(name), info => AgentTools.PortsFromLiveInstance(doc, info, installed),
                       FromTemporaryInstance, post, KbPortsRun.RealClock, finish, Log);
    }

    /// <summary>The live read's own marshalling: the next step comes after whatever the UI thread has waiting.</summary>
    public static void NextOnUi(Action step)
    {
      var app = Eto.Forms.Application.Instance;
      if (app != null) app.AsyncInvoke(step); else step();
    }

    /// <summary>Read as <see cref="AgentTools.PortsFromLiveInstance"/> reads: a component's own params in order, and a param's one label for its one in and out.
    /// An object with neither, such as a scribble, has no ports, and an empty list says so.</summary>
    private static JsonObject FromTemporaryInstance(InstalledType info)
    {
      var obj = Instances.ComponentServer.EmitObjectProxy(info.Guid)?.CreateInstance();
      switch (obj)
      {
        case null:
          return null;
        case IGH_Component c:
          return LivePorts.Instantiated(c.Params.Input.Select(AgentTools.LookupPortLabel).ToList(), c.Params.Output.Select(AgentTools.LookupPortLabel).ToList());
        case IGH_Param p:
          var label = AgentTools.LookupPortLabel(p);
          return LivePorts.Instantiated(new[] { label }, new[] { label });
        default:
          return LivePorts.Instantiated(null, null);
      }
    }

    private static void Log(string line)
    {
      try { Rhino.RhinoApp.WriteLine("Dwight kb_lookup: " + line); } catch { }
    }
  }
}
