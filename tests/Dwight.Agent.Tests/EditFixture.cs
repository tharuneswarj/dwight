using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Dwight.Agent;
using Dwight.Data;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// A small canvas for the consented-edit tests (Phase-9 U6 + U21), built as BOTH of the shapes the edit reads: the
  /// <see cref="EditCanvas"/> the validator sees and the <see cref="FpDoc"/> the verdict fingerprints.
  ///
  /// THE TWO ARE ONE SOURCE. <see cref="Doc"/> is derived from the same nodes and the same port sources, so a test can
  /// build an "actual after" by mutating a SECOND fixture by hand - <see cref="Wire"/>, <see cref="Unwire"/>,
  /// <see cref="Delete"/>, written here and independent of <see cref="EditOpsAsk"/>'s own simulation - and the verdict
  /// compares the plugin's prediction against an oracle the plugin did not write.
  ///
  /// <code>
  ///   1 Slider X (param, no input)      2 Slider Y (param, no input)
  ///   3 Addition   in A,B  out Result/R    4 Multiplication in A,B out Result    5 Panel (param, takes a wire)
  ///   6 Negative   in X    out Y           7 Subtraction    in A,B out Result    10 Deconstruct in P out X,Y
  ///   8 alive but NOT a component (a group)     9 retired      99 unknown
  ///   wires: 1->3.A  2->3.B  3.Result->4.A  7.Result->4.A (second source)  3.Result->5  2->7.A  1->7.B
  /// </code>
  /// </summary>
  internal sealed class EditFixture
  {
    public readonly Dictionary<int, EditNode> Nodes = new Dictionary<int, EditNode>();
    private readonly HashSet<int> _deleted = new HashSet<int>();

    public static Guid G(int n, int port = 0) => new Guid(n, (short)port, 0, new byte[8]);

    public EditCanvas Canvas() => new EditCanvas
    {
      State = id => id == 9 ? IdState.Retired
                  : id == 8 || Nodes.ContainsKey(id) ? IdState.Alive
                  : IdState.Unknown,
      Nodes = Nodes.Where(kv => !_deleted.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value),
    };

    public static EditFixture Make()
    {
      var f = new EditFixture();
      f.Param(1, "Slider X", accepts: false);
      f.Param(2, "Slider Y", accepts: false);
      f.Comp(3, "Addition", new[] { "A", "B" }, new[] { ("Result", "R") });
      f.Comp(4, "Multiplication", new[] { "A", "B" }, new[] { ("Result", "Result") });
      f.Param(5, "Panel", accepts: true);
      f.Comp(6, "Negative", new[] { "X" }, new[] { ("Y", "Y") });
      f.Comp(7, "Subtraction", new[] { "A", "B" }, new[] { ("Result", "Result") });
      f.Comp(10, "Deconstruct", new[] { "P" }, new[] { ("X", "X"), ("Y", "Y") });   // two outputs, so an output name is a CHOICE
      f.Nodes[3].Region = "A1"; f.Nodes[4].Region = "A1"; f.Nodes[7].Region = "B2";

      f.Wire(1, 0, 3, 0); f.Wire(2, 0, 3, 1);
      f.Wire(3, 0, 4, 0); f.Wire(7, 0, 4, 0);
      f.Wire(3, 0, 5, 0);
      f.Wire(2, 0, 7, 0); f.Wire(1, 0, 7, 1);
      return f;
    }

    public EditNode Param(int id, string label, bool accepts)
    {
      var port = new EditPort { Name = label, Nick = label, Guid = G(id) };
      var n = new EditNode
      {
        Id = id, Guid = G(id), Label = label, IsParam = true, AcceptsInput = accepts,
        Inputs = accepts ? new List<EditPort> { port } : new List<EditPort>(),
        Outputs = new List<EditPort> { port },
      };
      Nodes[id] = n;
      return n;
    }

    public EditNode Comp(int id, string label, string[] inputs, (string name, string nick)[] outputs)
    {
      var n = new EditNode
      {
        Id = id, Guid = G(id), Label = label, IsParam = false,
        Inputs = inputs.Select((nm, i) => new EditPort { Name = nm, Nick = nm, Guid = G(id, i + 1) }).ToList(),
        Outputs = outputs.Select((o, i) => new EditPort { Name = o.name, Nick = o.nick, Guid = G(id, 100 + i) }).ToList(),
      };
      Nodes[id] = n;
      return n;
    }

    /// <summary>Source = output <paramref name="outIdx"/> of node <paramref name="from"/>; target = input
    /// <paramref name="inIdx"/> of node <paramref name="to"/> (a param's single port is index 0).</summary>
    public void Wire(int from, int outIdx, int to, int inIdx) => InputOf(to, inIdx).Sources.Add(OutputOf(from, outIdx).Guid);

    public void Unwire(int from, int outIdx, int to, int inIdx) => InputOf(to, inIdx).Sources.Remove(OutputOf(from, outIdx).Guid);

    /// <summary>Delete a node the way Grasshopper does: it goes, and every wire from its outputs goes with it.</summary>
    public void Delete(int id)
    {
      var outs = new HashSet<Guid>(Nodes[id].Outputs.Select(o => o.Guid));
      _deleted.Add(id);
      foreach (var n in Nodes.Values)
        foreach (var ip in n.Inputs) ip.Sources.RemoveAll(outs.Contains);
    }

    public EditPort InputOf(int node, int idx) => Nodes[node].Inputs[idx];
    public EditPort OutputOf(int node, int idx) => Nodes[node].Outputs[idx];

    /// <summary>The fingerprint model of the CURRENT state, read from the same nodes.</summary>
    public FpDoc Doc()
    {
      var doc = new FpDoc();
      foreach (var kv in Nodes.OrderBy(kv => kv.Key))
      {
        if (_deleted.Contains(kv.Key)) continue;
        var n = kv.Value;
        var o = new FpObject { Guid = n.Guid, TypeName = n.Label };
        if (n.IsParam && !n.AcceptsInput)
          o.Inputs.Add(new FpInput { Guid = n.Guid, MappingToken = "None/False/False" });   // as BehaviourFingerprint reads a slider
        else
          foreach (var ip in n.Inputs)
            o.Inputs.Add(new FpInput { Guid = ip.Guid, MappingToken = "None/False/False", Sources = new List<Guid>(ip.Sources) });
        doc.Objects.Add(o);
      }
      return doc;
    }

    // ---- request builders (the model's real output shape) -----------------------------------------------------------

    public static JsonObject WireOp(int from, string fromParam, int to, string toParam) => Op("wire", from, fromParam, to, toParam);
    public static JsonObject UnwireOp(int from, string fromParam, int to, string toParam) => Op("unwire", from, fromParam, to, toParam);
    public static JsonObject DeleteOp(int id) => new JsonObject { ["op"] = "delete", ["id"] = id };

    private static JsonObject Op(string op, int from, string fromParam, int to, string toParam)
    {
      JsonObject End(int id, string p)
      {
        var e = new JsonObject { ["id"] = id };
        if (p != null) e["param"] = p;
        return e;
      }
      return new JsonObject { ["op"] = op, ["from"] = End(from, fromParam), ["to"] = End(to, toParam) };
    }

    public static JsonArray Ops(params JsonObject[] ops) => new JsonArray(ops.Select(o => (JsonNode)o.DeepClone()).ToArray());
  }
}
