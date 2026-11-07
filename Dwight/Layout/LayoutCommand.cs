using System;
using System.Collections.Generic;
using System.Linq;
using Grasshopper;
using Grasshopper.Kernel;
using Dwight.Data;
using Rhino;

namespace Dwight.Layout
{
  /// <summary>
  /// The "Lay out (engine only)" dev command (Phase 6 WP-A gate). Runs the deterministic layout engine on
  /// the current selection - or the whole canvas when nothing is selected - and applies it as MOVES ONLY,
  /// one undo, with NO LLM and NO API key. This is how the layout engine is runtime-verified on layout
  /// alone before any segmenter or labelling exists (the phase doc's bar: the engine must be correct before
  /// anything semantic is layered on). Not a user-facing v1 action - it lives under Dwight > Dev and is
  /// gated off once WP-A is accepted. It writes a one-line report to the Rhino command line and touches
  /// nothing but pivots.
  /// </summary>
  internal static class LayoutCommand
  {
    public static void Run()
    {
      try
      {
        var doc = Instances.ActiveCanvas?.Document;
        if (doc == null) { RhinoApp.WriteLine("Dwight: no Grasshopper document is open."); return; }

        var selected = SelectedGuids(doc);
        var whole = selected.Count == 0;

        // Build the snapshot on the UI thread (it reads the live document); the engine + apply run from here.
        var graph = DwightGraph.Build(doc, selected);
        if (graph.Nodes.Count == 0) { RhinoApp.WriteLine("Dwight: nothing to lay out."); return; }

        // Selection scope lays out the selected nodes; an empty selection lays out every logic node.
        var ids = whole
          ? new HashSet<int>(graph.Nodes.Select(n => n.Id))
          : new HashSet<int>(graph.Nodes.Where(n => n.InSelection).Select(n => n.Id));
        if (ids.Count == 0) { RhinoApp.WriteLine("Dwight: nothing to lay out."); return; }

        var (nodes, edges) = GhLayout.BuildInput(graph, doc, ids);
        var result = LayoutEngine.Msagl().Layout(nodes, edges);
        RhinoApp.WriteLine($"Dwight: laying out {ids.Count} component(s) ({(whole ? "whole canvas" : "selection")}) into {result.BlockCount} block(s)...");
        CanvasWriter.ApplyLayout(doc, graph, result, RhinoApp.WriteLine);
      }
      catch (Exception ex)
      {
        RhinoApp.WriteLine("Dwight: layout command failed - " + ex.Message);
      }
    }

    private static List<Guid> SelectedGuids(GH_Document doc)
    {
      var list = new List<Guid>();
      try { foreach (var o in doc.SelectedObjects()) list.Add(o.InstanceGuid); }
      catch { }
      return list;
    }
  }
}
