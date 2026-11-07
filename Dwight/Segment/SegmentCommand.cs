using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;
using Grasshopper.Kernel.Undo;
using Grasshopper.Kernel.Undo.Actions;
using Dwight.Data;
using Rhino;

namespace Dwight.Segment
{
  /// <summary>
  /// The WP-B benchmark dev commands (Dwight > Dev). Two halves of the gate:
  ///   - "Segment (export clusters)" runs the segmenter on the selection (or whole canvas) and paints the
  ///     macro clusters as FLAT coloured groups over the existing layout, plus dumps the partition to JSON.
  ///     One undo. This is the visual aid AND the machine-readable segmenter partition.
  ///   - "Score against my chunks" reads YOUR hand-chunking (the GH groups you made, excluding Dwight's own)
  ///     as the human partition, runs the segmenter, and prints ARI + boundary F1 to the Rhino command line.
  ///     Record each file's number with a clean/messy tag - the messy-file number is the real gate.
  /// Not user-facing v1; gated off once WP-B is accepted. No LLM, no key.
  /// </summary>
  internal static class SegmentCommand
  {
    // low-alpha cluster fills (cycled); alpha kept low so blocks read when zoomed out.
    private static readonly Color[] Palette =
    {
      Color.FromArgb(55, 120, 180, 90), Color.FromArgb(55, 95, 150, 210), Color.FromArgb(55, 235, 160, 70),
      Color.FromArgb(55, 175, 130, 210), Color.FromArgb(55, 235, 210, 90), Color.FromArgb(55, 90, 200, 190),
      Color.FromArgb(55, 220, 120, 140), Color.FromArgb(55, 150, 150, 150),
    };
    private const string DwightGroupPrefix = "Dwight: cluster ";

    public static void ExportClusters()
    {
      try
      {
        var doc = Instances.ActiveCanvas?.Document;
        if (doc == null) { RhinoApp.WriteLine("Dwight: no Grasshopper document is open."); return; }

        var (graph, ids, whole) = Snapshot(doc);
        if (ids.Count == 0) { RhinoApp.WriteLine("Dwight: nothing to segment."); return; }

        var result = new Segmenter().Segment(GhSegment.BuildInput(graph, doc, ids));
        var macro = result.Regions.Where(r => r.Level == 0).OrderBy(r => r.Id).ToList();
        RhinoApp.WriteLine($"Dwight: segmented {ids.Count} component(s) ({(whole ? "whole canvas" : "selection")}) into {macro.Count} macro cluster(s).");

        // raw RPST diagnostics (Tharun's ask): shows WHICH structure the dataflow yielded - real nested SESE
        // regions vs. weakly-connected pieces vs. one giant tangle.
        var d = result.Diagnostics;
        RhinoApp.WriteLine($"  RPST: spine {d.RootInteriorSize}, {d.HubCount} hub(s), {d.WeakComponents} weak component(s), " +
                           $"{d.MatchedRegions} matched SESE region(s) (depth {d.RpstDepth}).");
        RhinoApp.WriteLine($"  Stage 2: {d.RigidSubdivided}/{d.RigidRegions} rigid region(s) subdivided -> {d.GroupCount} group(s) total.");

        PaintClusters(doc, graph, macro);
        var path = DumpJson(doc, graph, macro);
        if (path != null) RhinoApp.WriteLine("Dwight: cluster membership written to " + path);
      }
      catch (Exception ex) { RhinoApp.WriteLine("Dwight: segment export failed - " + ex.Message); }
    }

    public static void ScoreAgainstMyChunks()
    {
      try
      {
        var doc = Instances.ActiveCanvas?.Document;
        if (doc == null) { RhinoApp.WriteLine("Dwight: no Grasshopper document is open."); return; }

        var graph = DwightGraph.Build(doc, Array.Empty<Guid>());
        var allIds = new HashSet<int>(graph.Nodes.Select(n => n.Id));
        var segInput = GhSegment.BuildInput(graph, doc, allIds);
        var human = ReadHumanChunks(doc, graph);
        if (human.Count == 0)
        {
          RhinoApp.WriteLine("Dwight: no hand-chunk groups found. Group your components into your logical chunks " +
                             "(plain GH groups, not Dwight's), then run this again.");
          return;
        }

        var wireEdges = segInput.Edges.Select(e => (e.FromId, e.ToId)).ToList();
        IReadOnlyDictionary<int, int> Run(SegOptions o) => new Segmenter(o).Segment(segInput).Macro;

        // full (both boundary terms on), each term off, and Stage 1 only - so each term's contribution is
        // measured separately. The acceptance number is PRECISION (the terms fix wrong-place cuts): precision
        // must go UP with recall holding. ARI up + precision flat/down = the entry-crossing is over-firing.
        var full = Run(new SegOptions());
        var noParam = Run(new SegOptions { ParamBoundaryEnabled = false });
        var noEntry = Run(new SegOptions { EntryCrossingEnabled = false });
        var noWire = Run(new SegOptions { WireLengthEnabled = false });
        var stage1 = Run(new SegOptions { Stage2Enabled = false });

        double Ari(IReadOnlyDictionary<int, int> p) => SegmentScorer.AdjustedRandIndex(p, human);
        double Prec(IReadOnlyDictionary<int, int> p) => SegmentScorer.BoundaryAgreement(p, human, wireEdges).precision;

        var (precision, recall, f1) = SegmentScorer.BoundaryAgreement(full, human, wireEdges);
        var commonNodes = human.Keys.Count(full.ContainsKey);

        RhinoApp.WriteLine($"Dwight benchmark - {commonNodes} chunked component(s), " +
                           $"{human.Values.Distinct().Count()} human chunk(s) vs {full.Values.Distinct().Count()} segmenter cluster(s):");
        RhinoApp.WriteLine($"  ARI {Ari(full):0.000}   precision {precision:0.000}   recall {recall:0.000}   (F1 {f1:0.000})");
        RhinoApp.WriteLine($"  param-boundary delta:  ARI {Ari(full) - Ari(noParam):+0.000;-0.000}   precision {precision - Prec(noParam):+0.000;-0.000}");
        RhinoApp.WriteLine($"  entry-crossing delta:  ARI {Ari(full) - Ari(noEntry):+0.000;-0.000}   precision {precision - Prec(noEntry):+0.000;-0.000}   <- must be precision UP, recall holding");
        RhinoApp.WriteLine($"  wire-length delta:     ARI {Ari(full) - Ari(noWire):+0.000;-0.000}   precision {precision - Prec(noWire):+0.000;-0.000}   (real-layout bonus; OFF in the scatter cross-check)");
        RhinoApp.WriteLine($"  Stage 1 only: ARI {Ari(stage1):0.000}");
        RhinoApp.WriteLine("  Gate: precision up, recall holding. ARI up with precision flat/down = entry-crossing over-firing (relax EntryMaxInDegree).");
        RhinoApp.WriteLine("  Cross-check: scatter the components and re-run - the score should be the SAME (topology, not layout).");
      }
      catch (Exception ex) { RhinoApp.WriteLine("Dwight: benchmark scoring failed - " + ex.Message); }
    }

    // ---- helpers ------------------------------------------------------------------------------------

    private static (DwightGraph graph, HashSet<int> ids, bool whole) Snapshot(GH_Document doc)
    {
      var selected = new List<Guid>();
      try { foreach (var o in doc.SelectedObjects()) selected.Add(o.InstanceGuid); } catch { }
      var whole = selected.Count == 0;
      var graph = DwightGraph.Build(doc, selected);
      var ids = whole
        ? new HashSet<int>(graph.Nodes.Select(n => n.Id))
        : new HashSet<int>(graph.Nodes.Where(n => n.InSelection).Select(n => n.Id));
      return (graph, ids, whole);
    }

    /// <summary>Create one flat coloured group per macro cluster, nicknamed so the scorer can tell Dwight's
    /// groups from the human's. One undo record.</summary>
    private static void PaintClusters(GH_Document doc, DwightGraph graph, List<SegRegion> macro)
    {
      doc.ScheduleSolution(10, d =>
      {
        try
        {
          var actions = new List<IGH_UndoAction>();
          var painted = 0;
          for (var i = 0; i < macro.Count; i++)
          {
            var members = macro[i].Members
              .Select(id => graph.NodeById(id)?.Guid)
              .Where(g => g.HasValue && d.FindObject(g.Value, true) != null)
              .Select(g => g.Value).ToList();
            if (members.Count == 0) continue;

            var group = new GH_Group { NickName = DwightGroupPrefix + (i + 1), Colour = Palette[i % Palette.Length] };
            foreach (var g in members) group.AddObject(g);
            d.AddObject(group, false, d.Objects.Count);
            actions.Add(new GH_AddObjectAction(group));
            painted++;
          }
          if (actions.Count == 0) { RhinoApp.WriteLine("Dwight: no clusters to paint."); return; }
          Dwight.Session.ChangeLog.NoteVerb(d, d.UndoUtil.RecordEvent("Dwight: segment export", actions), "Dwight: segment export", "Dev");
          Instances.RedrawCanvas();
          RhinoApp.WriteLine($"Dwight: painted {painted} cluster group(s) (Ctrl-Z to remove).");
        }
        catch (Exception ex) { RhinoApp.WriteLine("Dwight: paint failed - " + ex.Message); }
      });
    }

    /// <summary>Read the user's hand-chunking: every GH group that is NOT one of Dwight's export groups
    /// becomes a chunk; map its member guids to graph ids. Nodes the user did not group are left out of the
    /// comparison (scored over the chunked set).</summary>
    private static Dictionary<int, int> ReadHumanChunks(GH_Document doc, DwightGraph graph)
    {
      var human = new Dictionary<int, int>();
      var chunk = 0;
      foreach (var group in doc.Objects.OfType<GH_Group>())
      {
        var nick = group.NickName ?? "";
        if (nick.StartsWith(DwightGroupPrefix, StringComparison.Ordinal)) continue;   // skip Dwight's own
        var any = false;
        foreach (var g in MemberIds(group))
        {
          var node = graph.NodeByGuid(g);
          if (node != null) { human[node.Id] = chunk; any = true; }
        }
        if (any) chunk++;
      }
      return human;
    }

    private static IEnumerable<Guid> MemberIds(GH_Group group)
    {
      try { return group.ObjectIDs ?? Enumerable.Empty<Guid>(); } catch { return Enumerable.Empty<Guid>(); }
    }

    private static string DumpJson(GH_Document doc, DwightGraph graph, List<SegRegion> macro)
    {
      try
      {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Dwight", "benchmark");
        Directory.CreateDirectory(dir);
        var name = SafeName(doc) + "-segment.json";
        var path = Path.Combine(dir, name);

        var clusters = new JsonArray();
        foreach (var r in macro)
        {
          var members = new JsonArray();
          foreach (var id in r.Members)
          {
            var node = graph.NodeById(id);
            if (node != null) members.Add(node.Guid.ToString());
          }
          clusters.Add(new JsonObject { ["id"] = r.Id, ["members"] = members });
        }
        var root = new JsonObject { ["macroCount"] = macro.Count, ["clusters"] = clusters };
        File.WriteAllText(path, root.ToJsonString());
        return path;
      }
      catch { return null; }
    }

    private static string SafeName(GH_Document doc)
    {
      var name = "untitled";
      try { if (!string.IsNullOrWhiteSpace(doc.DisplayName)) name = doc.DisplayName; } catch { }
      foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
      return name;
    }
  }
}
