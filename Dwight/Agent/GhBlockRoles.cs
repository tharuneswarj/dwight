using System;
using System.Collections.Generic;
using System.Linq;
using Grasshopper.Kernel;

namespace Dwight.Agent
{
  /// <summary>Brief DP: what the live wiring says about each component of a block, read after the wires are made. <see cref="BlockColumns"/> decides what that means.</summary>
  internal static class GhBlockRoles
  {
    /// <summary>The role of each of <paramref name="objects"/>, in the same order, where <paramref name="block"/> is everything inside the block.</summary>
    public static List<BlockRole> Read(IEnumerable<IGH_DocumentObject> objects, ISet<Guid> block) =>
      objects.Select(o => BlockColumns.RoleOf(NodeOf(o, block))).ToList();

    /// <summary>Who each of <paramref name="objects"/> is wired to among themselves, by place in the list, and at which port at the far end: what a region's column is ordered by (<see cref="BlockColumns.AroundFlow"/>).</summary>
    public static List<BlockWires> Wires(IReadOnlyList<IGH_DocumentObject> objects)
    {
      var place = new Dictionary<Guid, int>();
      for (var i = 0; i < objects.Count; i++) place[objects[i].InstanceGuid] = i;

      List<BlockWire> Far(IGH_DocumentObject o, IEnumerable<IGH_Param> near, Func<IGH_Param, IEnumerable<IGH_Param>> across, bool farIsInput)
      {
        var wires = new List<BlockWire>();
        foreach (var p in near)
          foreach (var far in (p == null ? null : across(p)) ?? new List<IGH_Param>())
          {
            var owner = OwnerOf(far);
            if (owner.HasValue && owner != o.InstanceGuid && place.TryGetValue(owner.Value, out var partner))
              wires.Add(new BlockWire(partner, PortOf(far, farIsInput)));
          }
        return wires;
      }

      return objects.Select(o => new BlockWires
      {
        Readers = Far(o, OutsOf(o), p => p.Recipients, true),
        Feeders = Far(o, InsOf(o), p => p.Sources, false),
      }).ToList();
    }

    private static int PortOf(IGH_Param p, bool input)
    {
      var component = p?.Attributes?.GetTopLevel?.DocObject as IGH_Component;
      if (component == null) return 0;
      return Math.Max(0, input ? component.Params.Input.IndexOf(p) : component.Params.Output.IndexOf(p));
    }

    private static IEnumerable<IGH_Param> InsOf(IGH_DocumentObject o) =>
      o is IGH_Component c ? c.Params.Input : o is IGH_Param p ? new[] { p } : Array.Empty<IGH_Param>();

    private static IEnumerable<IGH_Param> OutsOf(IGH_DocumentObject o) =>
      o is IGH_Component c ? c.Params.Output : o is IGH_Param p ? new[] { p } : Array.Empty<IGH_Param>();

    private static BlockNode NodeOf(IGH_DocumentObject o, ISet<Guid> block)
    {
      var param = o as IGH_Param;
      var component = o as IGH_Component;
      var node = new BlockNode
      {
        Holder = param != null,
        SourceOnly = param != null && !(param.Attributes?.HasInputGrip ?? true),
        Sink = component != null && component.Params.Output.Count == 0,
      };

      foreach (var input in InsOf(o))
        foreach (var source in input?.Sources ?? new List<IGH_Param>())
        {
          var owner = OwnerOf(source);
          if (owner == o.InstanceGuid) continue;
          if (owner.HasValue && block.Contains(owner.Value)) node.FedInside = true;
          else node.FedOutside = true;
        }

      foreach (var output in OutsOf(o))
        foreach (var recipient in output?.Recipients ?? new List<IGH_Param>())
        {
          var owner = OwnerOf(recipient);
          if (owner == o.InstanceGuid) continue;
          if (owner.HasValue && block.Contains(owner.Value)) node.FeedsInside = true;
          else node.FeedsOutside = true;
        }
      return node;
    }

    private static Guid? OwnerOf(IGH_Param p) => p?.Attributes?.GetTopLevel?.DocObject?.InstanceGuid;
  }
}
