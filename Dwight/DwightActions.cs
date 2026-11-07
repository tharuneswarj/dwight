using System;
using System.Collections.Generic;
using System.IO;
using GhJSON.Core;
using GhJSON.Grasshopper;
using Grasshopper;
using Grasshopper.Kernel;
using Dwight.Data;
using Rhino;

namespace Dwight
{
  /// <summary>
  /// The surface-agnostic Dwight actions. Both invocation surfaces (the top-bar menu
  /// and the cursor pop-up) call these, keeping the engine decoupled from the trigger
  /// (mother doc 4.7). Phase 1 actions involve no AI; Phase 2 adds the LLM actions in
  /// <see cref="LlmActions"/>, which reuse <see cref="SerializeSelection"/> here.
  /// </summary>
  internal static class DwightActions
  {
    /// <summary>Task 1.2 - list the current selection's NickName / Name / InstanceGuid.</summary>
    public static void ReadSelection()
    {
      var doc = Instances.ActiveCanvas?.Document;
      if (doc == null)
      {
        RhinoApp.WriteLine("Dwight: no active Grasshopper document.");
        return;
      }

      var selected = doc.SelectedObjects();
      if (selected == null || selected.Count == 0)
      {
        RhinoApp.WriteLine("Dwight: nothing selected. Select components on the canvas, then try again.");
        return;
      }

      RhinoApp.WriteLine($"Dwight: {selected.Count} object(s) selected:");
      foreach (var obj in selected)
      {
        RhinoApp.WriteLine(
          $"  - nick='{obj.NickName}'  name='{obj.Name}'  id={obj.InstanceGuid}  compGuid={obj.ComponentGuid}  cat={obj.Category}");
      }
    }

    /// <summary>
    /// Task 1.3 - serialise the selection to GhJSON, validate it, save + print it, and
    /// round-trip a working copy back onto the canvas (offset, one undo record,
    /// UI-thread marshaled, internalized-data stripped) - proving the whole data layer
    /// with no AI (ADR-010, ADR-019, ADR-029).
    /// </summary>
    public static void DumpSelectionAsGhJson()
    {
      var doc = Instances.ActiveCanvas?.Document;
      if (doc == null)
      {
        RhinoApp.WriteLine("Dwight: no active Grasshopper document.");
        return;
      }

      var json = SerializeSelection(out var count, out var error);
      if (json == null)
      {
        RhinoApp.WriteLine($"Dwight: {error}");
        return;
      }

      // Validate before doing anything with it (ADR-010).
      var validation = GhJson.Validate(json);
      if (!validation.IsValid)
      {
        RhinoApp.WriteLine("Dwight: the serialised GhJSON did not validate - not placing anything.");
        foreach (var err in validation.Errors)
          RhinoApp.WriteLine($"    - {err.Message}");
        return;
      }

      var savedPath = TrySaveJson(json);
      RhinoApp.WriteLine($"Dwight: serialised {count} object(s) to valid GhJSON ({json.Length} chars).");
      if (savedPath != null) RhinoApp.WriteLine($"    saved: {savedPath}");

      var note = GhJsonGuards.Inspect(json);
      if (note.Any)
        RhinoApp.WriteLine($"Dwight: note - {note.ParameterCount} parameter(s) carry internalized data; " +
                           "these will not survive placement in GhJSON v1.0 and are stripped from the copy.");

      RhinoApp.WriteLine(json);

      CanvasWriter.PlaceCopyOffset(doc, json, RhinoApp.WriteLine);
    }

    /// <summary>
    /// Serialise the current selection to GhJSON. Crash-safe (ADR-029): on any failure it
    /// returns null with a friendly message rather than throwing. Returns the JSON and the
    /// number of selected objects on success.
    /// </summary>
    public static string SerializeSelection(out int count, out string error)
    {
      count = 0;
      var doc = Instances.ActiveCanvas?.Document;
      if (doc == null)
      {
        error = "no active Grasshopper document.";
        return null;
      }

      var selected = doc.SelectedObjects();
      if (selected == null || selected.Count == 0)
      {
        error = "nothing selected. Select components on the canvas, then try again.";
        return null;
      }
      count = selected.Count;

      try
      {
        var ghDoc = GhJsonGrasshopper.GetSelected();
        error = null;
        return GhJson.ToJson(ghDoc);
      }
      catch (Exception ex)
      {
        error = $"failed to serialise the selection to GhJSON - {ex.Message}";
        return null;
      }
    }

    /// <summary>The currently selected document objects, or an empty list.</summary>
    public static IReadOnlyList<IGH_DocumentObject> CurrentSelection()
    {
      var doc = Instances.ActiveCanvas?.Document;
      var sel = doc?.SelectedObjects();
      return sel == null ? new List<IGH_DocumentObject>() : new List<IGH_DocumentObject>(sel);
    }

    private static string TrySaveJson(string json)
    {
      try
      {
        var dir = Path.Combine(
          Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Dwight", "dumps");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"selection-{DateTime.Now:yyyyMMdd-HHmmss}.ghjson");
        File.WriteAllText(path, json);
        return path;
      }
      catch (Exception ex)
      {
        RhinoApp.WriteLine($"Dwight: could not save the GhJSON file - {ex.Message}");
        return null;
      }
    }
  }
}
