using System;
using System.Collections.Generic;
using Grasshopper.Kernel;
using Dwight.Data;

namespace Dwight.Agent
{
  /// <summary>
  /// U23's session reads - the Rhino and Grasshopper half. It gathers FACTS and decides nothing: what each one
  /// means, what is listed and how it is said are <see cref="DocumentUnitsReport"/>'s and
  /// <see cref="PluginInventoryReport"/>'s, which are GH-free and tested.
  ///
  /// Both are reads in the strict sense (#190): zero-token, solve-free, mutation-free.
  /// </summary>
  internal static class GhSessionFacts
  {
    /// <summary>
    /// The active Rhino document's units and tolerances - the document Grasshopper's own components read theirs
    /// from. No active document is the shared refusal, never a default.
    /// </summary>
    public static ToolResult DocumentUnits()
    {
      Rhino.RhinoDoc rhino;
      try { rhino = Rhino.RhinoDoc.ActiveDoc; }
      catch { rhino = null; }
      if (rhino == null) return DocumentUnitsReport.NoActiveDocument();

      try
      {
        return DocumentUnitsReport.Build(new DocumentUnitsFact
        {
          Units = rhino.ModelUnitSystem.ToString(),
          AbsoluteTolerance = rhino.ModelAbsoluteTolerance,
          AngleToleranceDegrees = rhino.ModelAngleToleranceDegrees,
        });
      }
      catch (Exception ex)
      {
        return ToolResult.Fail(ToolErrors.PreconditionRefused,
          "Dwight could not read the active Rhino document's units (" + ex.Message + ").");
      }
    }

    /// <summary>
    /// The active document's absolute tolerance, for the geometry reads - or the shared refusal. The decision is
    /// <see cref="DocumentUnitsReport.TryTolerance"/>'s; this only supplies the reading.
    /// </summary>
    public static bool TryTolerance(out double tolerance, out ToolResult error) =>
      DocumentUnitsReport.TryTolerance(() => Rhino.RhinoDoc.ActiveDoc?.ModelAbsoluteTolerance, out tolerance, out error);

    /// <summary>
    /// Every library Grasshopper loaded and every user object it found, with how many components each plugin provides
    /// and how many objects on this canvas come from it. The join is <see cref="PluginInventoryFacts"/>'s. Core-versus-plugin
    /// is <c>GH_AssemblyInfo.IsCoreLibrary</c> - the definition <c>ScriptSourceReader</c> already uses - and the name is
    /// <see cref="PluginName.Of"/>, the one get_component sends. Location, author contact, id and description are never
    /// read into the facts at all.
    /// </summary>
    public static ToolResult InstalledPlugins(GH_Document doc)
    {
      var server = Grasshopper.Instances.ComponentServer;
      if (server == null)
        return ToolResult.Fail(ToolErrors.PreconditionRefused, "Grasshopper's component server is not available.");

      var proxies = new List<ServerProxy>();
      foreach (var proxy in server.ObjectProxies)
      {
        try
        {
          proxies.Add(new ServerProxy
          {
            Guid = proxy.Guid,
            Library = proxy.LibraryGuid,
            Obsolete = proxy.Obsolete,
            UserObject = proxy.Kind == GH_ObjectType.UserObject,
            Category = proxy.Desc?.Category,
          });
        }
        catch { }
      }

      var canvas = new List<CanvasObjectFact>();
      if (doc != null)
        foreach (var obj in doc.Objects)
        {
          try { canvas.Add(new CanvasObjectFact { ComponentGuid = obj.ComponentGuid, Category = obj.Category }); }
          catch { }
        }

      var libraries = new List<LoadedLibrary>();
      foreach (var lib in server.Libraries)
      {
        try { libraries.Add(new LoadedLibrary { Id = lib.Id, Name = lib.Name, Version = lib.Version, Core = lib.IsCoreLibrary }); }
        catch { }
      }

      return PluginInventoryReport.Build(PluginInventoryFacts.Join(libraries, proxies, canvas));
    }
  }
}
