using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Grasshopper.Kernel;

namespace Dwight.UI
{
  /// <summary>
  /// The Grasshopper shim for Teaching mode (Brief DE): reads the TRUE value of a port from the live solve, as the geometry reads do, facts only. The plugin reads it; the
  /// model never asserts one. What may be asked about is a scalar number (a slider's value, a List Length's count, a Divide's count) or the count of a list of strings. A
  /// geometry output, a tree, a null and a list of anything else have no question. Fail-safe (ADR-029): an object that cannot be read has no reading, never an error.
  ///
  /// UI thread only: it reads live volatile data.
  /// </summary>
  internal static class GhTeaching
  {
    private const int MaxListCount = 50;

    public static Dictionary<Guid, IReadOnlyList<PortReading>> Read(GH_Document doc, IEnumerable<Guid> components)
    {
      var result = new Dictionary<Guid, IReadOnlyList<PortReading>>();
      if (doc == null || components == null) return result;
      foreach (var id in components)
      {
        try
        {
          var obj = doc.FindObject(id, true);
          var list = new List<PortReading>();
          if (obj is IGH_Component c)
            foreach (var p in c.Params.Output) Add(list, id, c.Name, c.NickName, p);
          else if (obj is IGH_Param p)
            Add(list, id, p.Name, p.NickName, p);
          if (list.Count > 0) result[id] = list;
        }
        catch { /* an object that cannot be read has no question */ }
      }
      return result;
    }

    /// <summary>
    /// Brief EI: what a deck is planned from, read from the snapshot the Explain was built on, on the UI thread before the call: the selection's components with their run
    /// ids, the wires among them with the item count each source port holds, and the values <see cref="Read"/> found. Facts only; never geometry.
    /// </summary>
    public static DeckInput Deck(Dwight.Data.DwightGraph graph, IReadOnlyDictionary<Guid, IReadOnlyList<PortReading>> readings)
    {
      var input = new DeckInput { Readings = readings ?? new Dictionary<Guid, IReadOnlyList<PortReading>>() };
      if (graph == null) return input;
      try
      {
        var nodes = graph.Selection.OrderBy(n => n.Id).Select(n => new DeckNode { Guid = n.Guid, Id = n.Id, Type = n.Type, Nick = n.Nick }).ToList();
        var inSel = new HashSet<int>(graph.Selection.Select(n => n.Id));
        var wires = new List<DeckWire>();
        foreach (var e in graph.Edges)
        {
          if (!inSel.Contains(e.FromNode) || !inSel.Contains(e.ToNode)) continue;
          var items = -1;
          try { if (e.SourceParam?.VolatileData != null) items = e.SourceParam.VolatileData.DataCount; } catch { }
          wires.Add(new DeckWire { From = graph.NodeById(e.FromNode).Guid, FromPort = e.FromParam, To = graph.NodeById(e.ToNode).Guid, ToPort = e.ToParam, Items = items });
        }
        input.Nodes = nodes;
        input.Wires = wires;
      }
      catch { input.Nodes = Array.Empty<DeckNode>(); input.Wires = Array.Empty<DeckWire>(); }   // a graph that cannot be read plans no deck, and the Walk runs without questions
      return input;
    }

    private static void Add(List<PortReading> into, Guid id, string type, string nick, IGH_Param param)
    {
      var values = new List<object>();
      foreach (var goo in param.VolatileData.AllData(true))
      {
        if (goo == null) return;   // a null in the data: nothing is asked
        values.Add(goo.ScriptVariable());
        if (values.Count > MaxListCount) return;
      }
      if (values.Count == 0) return;

      var port = string.IsNullOrWhiteSpace(param.NickName) ? param.Name : param.NickName;
      if (values.Count == 1 && TeachingNumbers.TryText(values[0], out var text))
        into.Add(new PortReading { Component = id, Type = type, Nick = nick, Port = port, Kind = ReadingKind.Number, Value = text });
      else if (values.Count >= 2 && values.All(v => v is string))
        into.Add(new PortReading { Component = id, Type = type, Nick = nick, Port = port, Kind = ReadingKind.ListCount, Value = values.Count.ToString(CultureInfo.InvariantCulture) });
    }
  }
}
