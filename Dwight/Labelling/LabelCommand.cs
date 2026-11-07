using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;
using Grasshopper.Kernel.Undo;
using Grasshopper.Kernel.Undo.Actions;
using Dwight.Config;
using Dwight.Data;
using Dwight.Llm;
using Dwight.Models;
using Dwight.Segment;
using Rhino;

namespace Dwight.Labelling
{
  /// <summary>
  /// The WP-C label gate (Dwight > Dev > "Label clusters"): segment the selection-or-whole canvas, label the
  /// clusters with the minimal LLM pass, and paint each cluster as a flat coloured group whose NICKNAME is the
  /// label - so the label quality is verified on real files before WP-D wires names into Reorganise. One undo;
  /// the LLM call runs off the UI thread, the paint goes through ScheduleSolution. Not a user-facing v1 surface.
  /// </summary>
  internal static class LabelCommand
  {
    private static readonly Color[] Palette =
    {
      Color.FromArgb(55, 120, 180, 90), Color.FromArgb(55, 95, 150, 210), Color.FromArgb(55, 235, 160, 70),
      Color.FromArgb(55, 175, 130, 210), Color.FromArgb(55, 235, 210, 90), Color.FromArgb(55, 90, 200, 190),
      Color.FromArgb(55, 220, 120, 140), Color.FromArgb(55, 150, 150, 150),
    };

    public static void Run()
    {
      try
      {
        var doc = Instances.ActiveCanvas?.Document;
        if (doc == null) { RhinoApp.WriteLine("Dwight: no Grasshopper document is open."); return; }

        var provider = DwightSettings.Current.EffectiveProvider;
        if (!LlmProviders.HasKey(provider))
        { RhinoApp.WriteLine($"Dwight: add your {DwightConfig.ProviderLabel(provider)} key in Settings to label clusters."); return; }

        var selected = SelectedGuids(doc);
        var whole = selected.Count == 0;
        var graph = DwightGraph.Build(doc, selected);
        var ids = whole
          ? new HashSet<int>(graph.Nodes.Select(n => n.Id))
          : new HashSet<int>(graph.Nodes.Where(n => n.InSelection).Select(n => n.Id));
        if (ids.Count == 0) { RhinoApp.WriteLine("Dwight: nothing to label."); return; }

        var macro = new Segmenter().Segment(GhSegment.BuildInput(graph, doc, ids)).Macro;
        var model = DwightSettings.Current.ResolveModel(DwightTask.Label, TaskScope.Any, structured: true).Model;
        var prov = LlmProviders.Create(model.Provider ?? provider, model.Id);
        var clusterCount = macro.Count == 0 ? 0 : macro.Values.Distinct().Count();
        RhinoApp.WriteLine($"Dwight: labelling {clusterCount} cluster(s) ({(whole ? "whole canvas" : "selection")}) with {model.DisplayName}...");

        Task.Run(() =>
        {
          try
          {
            var res = ClusterLabeller.Label(graph, macro, prov);
            if (!res.Ok) { RhinoApp.WriteLine("Dwight: labelling failed - " + res.Error); return; }
            PaintLabels(doc, graph, macro, res.Labels);
            RhinoApp.WriteLine($"Dwight: labelled {res.Labels.Count}/{res.ClusterCount} cluster(s); billed {res.Billed} (rest cached). " +
                               $"Tokens in {res.Usage.InputTokens}, out {res.Usage.OutputTokens}.");

            // the coarse arrangement hints (relationships only - WP-D realises the geometry). Shown by name so
            // the advice is verifiable: e.g. "row: Site boundary -> Panel grid -> Roof loft".
            if (res.Arrangement.Count == 0) RhinoApp.WriteLine("Dwight: arrangement - none advised.");
            foreach (var h in res.Arrangement)
            {
              var names = h.Clusters.Select(c => res.Labels.TryGetValue(c, out var l) ? l : "cluster " + c);
              RhinoApp.WriteLine($"  arrangement [{h.Kind}]: {string.Join(h.Kind == "row" ? " -> " : ", ", names)}");
            }
          }
          catch (Exception ex) { RhinoApp.WriteLine("Dwight: labelling error - " + ex.Message); }
        });
      }
      catch (Exception ex) { RhinoApp.WriteLine("Dwight: label command failed - " + ex.Message); }
    }

    private static void PaintLabels(GH_Document doc, DwightGraph graph, IReadOnlyDictionary<int, int> macro, Dictionary<int, string> labels)
    {
      doc.ScheduleSolution(10, d =>
      {
        try
        {
          var actions = new List<IGH_UndoAction>();
          var i = 0;
          foreach (var grp in macro.GroupBy(kv => kv.Value).OrderBy(g => g.Key))
          {
            var members = grp.Select(kv => graph.NodeById(kv.Key)?.Guid)
              .Where(g => g.HasValue && d.FindObject(g.Value, true) != null).Select(g => g.Value).ToList();
            if (members.Count == 0) { i++; continue; }

            labels.TryGetValue(grp.Key, out var label);
            var group = new GH_Group
            {
              NickName = string.IsNullOrWhiteSpace(label) ? "cluster " + grp.Key : label,
              Colour = Palette[i % Palette.Length],
            };
            foreach (var g in members) group.AddObject(g);
            d.AddObject(group, false, d.Objects.Count);
            actions.Add(new GH_AddObjectAction(group));
            i++;
          }
          if (actions.Count == 0) { RhinoApp.WriteLine("Dwight: no clusters to label."); return; }
          Dwight.Session.ChangeLog.NoteVerb(d, d.UndoUtil.RecordEvent("Dwight: label clusters", actions), "Dwight: label clusters", "Dev");
          Instances.RedrawCanvas();
          RhinoApp.WriteLine($"Dwight: painted {actions.Count} labelled group(s) (Ctrl-Z to remove).");
        }
        catch (Exception ex) { RhinoApp.WriteLine("Dwight: paint labels failed - " + ex.Message); }
      });
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
