using System;
using System.Collections.Generic;
using System.Linq;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;

namespace Dwight.Agent
{
  /// <summary>
  /// U23's canvas reads - the Grasshopper half. Facts only: what they mean and what is sent are
  /// <see cref="FileStateReport"/>'s and <see cref="CanvasLayoutReport"/>'s, which are GH-free and tested.
  /// Both are reads in the strict sense (#190): zero-token, solve-free, mutation-free.
  /// </summary>
  internal static class GhCanvasFacts
  {
    /// <summary>
    /// Saved, and modified since the save. It reads EXACTLY two properties - IsFilePathDefined and IsModified - and
    /// never FilePath, DisplayName, Author or Properties: the file-metadata ruling is no path and no name, and a value
    /// that is never read cannot be sent by a later mistake.
    /// </summary>
    public static ToolResult FileState(GH_Document doc)
    {
      if (doc == null) return FileStateReport.Build(null);
      try
      {
        return FileStateReport.Build(new FileStateFact { Saved = doc.IsFilePathDefined, ModifiedSinceSave = doc.IsModified });
      }
      catch (Exception ex)
      {
        return ToolResult.Fail(ToolErrors.PreconditionRefused, "Dwight could not read whether the definition is saved (" + ex.Message + ").");
      }
    }

    /// <summary>
    /// The layout: every top-level component's box and id, every group's box, name and members, and the wires between
    /// top-level components. Ids come from the snapshot every read uses, so a member id here is one get_component
    /// accepts. Boxes are read and never leave this method except as relations.
    /// </summary>
    public static ToolResult Layout(GH_Document doc, AgentScope scope)
    {
      if (doc == null || scope == null)
        return ToolResult.Fail(ToolErrors.NoDocument, "No Grasshopper document is open.");

      var snap = GhAgent.Build(doc, scope.Ids, false);
      var topLevel = snap.Nodes.Where(n => n.Inside == null).ToList();

      var components = new List<LayoutComponent>();
      var idOf = new Dictionary<Guid, int>();
      foreach (var n in topLevel)
      {
        if (!scope.Ids.TryGetGuid(n.Id, out var guid)) continue;
        var obj = doc.FindObject(guid, true);
        if (obj?.Attributes == null) continue;
        idOf[guid] = n.Id;
        components.Add(new LayoutComponent { Id = n.Id, Box = BoxOf(obj.Attributes.Bounds) });
      }

      var groups = new List<LayoutGroup>();
      foreach (var g in doc.Objects.OfType<GH_Group>())
      {
        try
        {
          var group = new LayoutGroup { Key = g.InstanceGuid.ToString("N"), Box = BoxOf(g.Attributes.Bounds) };

          // ONE test for "is this a Dwight region" - the one get_scaffold_map and build_region use.
          if (ScaffoldAddress.FromGroupName(g.NickName, out var address, out var name))
          {
            group.Address = address;
            group.Name = name;
          }
          else
            group.Name = g.NickName;

          foreach (var member in g.ObjectIDs ?? new List<Guid>())
          {
            if (idOf.TryGetValue(member, out var id)) group.Members.Add(id);
            else group.OtherObjects++;
          }
          groups.Add(group);
        }
        catch { }
      }

      var top = new HashSet<int>(topLevel.Select(n => n.Id));
      var wires = snap.Edges
        .Where(e => top.Contains(e.FromNode) && top.Contains(e.ToNode))
        .Select(e => (e.FromNode, e.ToNode))
        .ToList();

      return CanvasLayoutReport.Build(components, groups, wires);
    }

    private static CanvasBox BoxOf(System.Drawing.RectangleF r) => new CanvasBox(r.Left, r.Top, r.Right, r.Bottom);
  }
}
