using System;
using System.Collections.Generic;
using System.Linq;
using Grasshopper.Kernel;
using Dwight.Data;
using Dwight.Session;

namespace Dwight.Agent
{
  /// <summary>
  /// The Grasshopper shim for Dwight notes on a snapshot (Brief DC): puts each component's note lines on its <see cref="GraphNode"/>, so every
  /// payload and every chat read built from the graph carries them. Everything that decides anything - the bound, the state, the line's text -
  /// lives in <see cref="DwightNotes"/> and <see cref="NoteState"/>, which are GH-free and tested; this only reads the live canvas.
  ///
  /// Fail-safe (ADR-029): a note that cannot be read costs that note, never the snapshot. A note whose component is gone has nothing to travel
  /// with and is left out. A component the stamp cannot be read for reads as fresh, not as gone.
  /// </summary>
  internal static class GhNotes
  {
    public static void Attach(GH_Document doc, DwightGraph graph)
    {
      if (doc == null || graph == null) return;
      try
      {
        var notes = CommandHistory.Notes(doc);
        if (notes.Count == 0) return;

        var perNode = new Dictionary<GraphNode, List<KeyValuePair<StoredNote, NoteStatus>>>();
        foreach (var note in notes)
        {
          if (!Guid.TryParse(note?.Anchor, out var guid)) continue;
          var node = graph.NodeByGuid(guid);
          if (node == null) continue;   // a group, a scribble, or a component no longer here: not a node of this graph

          string current = null;
          if (!string.IsNullOrEmpty(note.Stamp))
          {
            try { current = GhAgent.StampOf(doc.FindObject(guid, false))?.Digest; } catch { }
          }
          var status = NoteState.Of(note, true, current);
          if (!perNode.TryGetValue(node, out var list)) perNode[node] = list = new List<KeyValuePair<StoredNote, NoteStatus>>();
          list.Add(new KeyValuePair<StoredNote, NoteStatus>(note, status));
        }

        foreach (var kv in perNode)
        {
          var lines = DwightNotes.Lines(kv.Value);
          if (lines.Count > 0) kv.Key.Notes = lines;
        }
      }
      catch { /* notes are context; a failure here must never cost the snapshot */ }
    }
  }
}
