using System;
using System.Linq;
using System.Text.Json.Nodes;

namespace Dwight.Agent
{
  /// <summary>The one target shape every geometry tool takes: which component, which output, which page.</summary>
  internal sealed class GeometryTarget
  {
    public int Id { get; set; }
    public string Param { get; set; }
    public int Skip { get; set; }
    public int Limit { get; set; } = GeometryArgs.MaxObjects;
  }

  /// <summary>
  /// Phase-9 U2: how every geometry tool ADDRESSES geometry (#381 R3, study §6). GH-free (ADR-091).
  ///
  /// ONE target shape for all seven tools - {id, param?, skip?, limit?} - and ONE port-resolution rule,
  /// which is #208's arity rule exactly as build_region keeps it: the plugin answers wherever the answer
  /// is forced (a param has one port; a single-output component has one), a name is required only where
  /// it is a genuine CHOICE, and wherever a name is given it must MATCH or is refused with the real list.
  /// Never a silent fallback to output[0] beneath a report - a report about the wrong port is a wrong
  /// report with a verdict's confidence.
  ///
  /// The cap is REFUSED, never clamped (#134): a clamp would let the model believe it asked for 200 and
  /// got 200. The refusal names the cap and the page mechanism, so it repairs rather than blocks.
  /// </summary>
  internal static class GeometryArgs
  {
    /// <summary>Geometry objects any one call returns, at most. ~50-150 tokens each (study §6), so a
    /// full page is a few thousand tokens - affordable once, ruinous unbounded. Page with "skip".</summary>
    public const int MaxObjects = 25;

    public static bool TryParseTarget(JsonObject args, out GeometryTarget target, out ToolResult error)
    {
      target = null;
      error = null;

      var idNode = args?["id"];
      if (idNode == null)
      {
        error = ToolResult.Fail(ToolErrors.InvalidArgs, "Missing required argument \"id\" - the component whose output to read.");
        return false;
      }
      int id;
      try { id = idNode.GetValue<int>(); }
      catch
      {
        error = ToolResult.Fail(ToolErrors.InvalidArgs, "Argument \"id\" must be an integer.");
        return false;
      }

      var t = new GeometryTarget { Id = id };

      var paramNode = args["param"];
      if (paramNode != null)
      {
        try { var p = paramNode.GetValue<string>(); t.Param = string.IsNullOrWhiteSpace(p) ? null : p.Trim(); }
        catch
        {
          error = ToolResult.Fail(ToolErrors.InvalidArgs, "Argument \"param\" must be a string - an output port name.");
          return false;
        }
      }

      if (!OptionalInt(args, "skip", 0, int.MaxValue, "\"skip\" must be zero or more.", ref t, (x, v) => x.Skip = v, out error))
        return false;
      if (!OptionalInt(args, "limit", 1, MaxObjects,
            "\"limit\" must be between 1 and " + MaxObjects + " - a report is capped at " + MaxObjects +
            " objects per call; page through the rest with \"skip\".",
            ref t, (x, v) => x.Limit = v, out error))
        return false;

      target = t;
      return true;
    }

    private static bool OptionalInt(JsonObject args, string field, int min, int max, string rule,
      ref GeometryTarget t, Action<GeometryTarget, int> set, out ToolResult error)
    {
      error = null;
      var node = args?[field];
      if (node == null) return true;
      int v;
      try { v = node.GetValue<int>(); }
      catch
      {
        error = ToolResult.Fail(ToolErrors.InvalidArgs, "Argument \"" + field + "\" must be an integer.");
        return false;
      }
      if (v < min || v > max)
      {
        error = ToolResult.Fail(ToolErrors.InvalidArgs, rule, new JsonObject { ["asked"] = v });
        return false;
      }
      set(t, v);
      return true;
    }

    /// <summary>
    /// Resolve the OUTPUT port to read, by arity (#208). Returns the canvas's own spelling of the port,
    /// never the model's.
    /// </summary>
    public static bool TryResolveOutput(AgentNode node, string param, out string port, out ToolResult error)
    {
      port = null;
      error = null;
      var outputs = node?.Outputs ?? Array.Empty<string>();
      var where = "component " + node?.Id + " (" + node?.Type + ")";

      if (outputs.Length == 0)
      {
        error = ToolResult.Fail(ToolErrors.InvalidArgs, "There is no output port on " + where + " to read geometry from.");
        return false;
      }

      if (!string.IsNullOrWhiteSpace(param))
      {
        // Given, so it must MATCH - even where it was not needed. A wrong name on a one-port node would
        // otherwise resolve to a port the model did not name.
        var match = outputs.FirstOrDefault(o => string.Equals(o, param.Trim(), StringComparison.OrdinalIgnoreCase));
        if (match == null)
        {
          error = ToolResult.Fail(ToolErrors.InvalidArgs,
            "No output named \"" + param + "\" on " + where + ".",
            new JsonObject { ["asked"] = param, ["outputs"] = Names(outputs) });
          return false;
        }
        port = match;
        return true;
      }

      if (outputs.Length == 1)
      {
        port = outputs[0];   // forced: the plugin answers
        return true;
      }

      error = ToolResult.Fail(ToolErrors.InvalidArgs,
        where + " has " + outputs.Length + " outputs - name the one to read with \"param\".",
        new JsonObject { ["outputs"] = Names(outputs) });
      return false;
    }

    private static JsonArray Names(string[] outputs) => new JsonArray(outputs.Select(o => (JsonNode)o).ToArray());

    // ---- spatial_query --------------------------------------------------------------------------------

    /// <summary>
    /// spatial_query's arguments: the target (what to search) and EXACTLY ONE query shape. "One of" cannot
    /// be said at the top level of the schema (Anthropic rejects anyOf there, #247 F1), so the rule lives
    /// here and the descriptor tells the model. Fixed-size inputs only - a point, a box, an object address -
    /// so the query itself can never be a coordinate dump either.
    /// </summary>
    public static bool TryParseSpatial(JsonObject args, out SpatialQuery query, out ToolResult error)
    {
      query = null;
      if (!TryParseTarget(args, out var target, out error)) return false;

      var near = args["near"];
      var within = args["within"];
      var overlapping = args["overlapping"];
      var given = (near != null ? 1 : 0) + (within != null ? 1 : 0) + (overlapping != null ? 1 : 0);
      if (given != 1)
      {
        error = ToolResult.Fail(ToolErrors.InvalidArgs,
          "spatial_query takes exactly one of \"near\" (a point [x,y,z]), \"within\" (a box {min,max}) or " +
          "\"overlapping\" (another object {id, param?, index?}) - " + (given == 0 ? "none was given." : given + " were given."));
        return false;
      }

      var q = new SpatialQuery { Target = target };

      if (near != null)
      {
        if (!TryVec3(near, out var p))
        {
          error = ToolResult.Fail(ToolErrors.InvalidArgs, "\"near\" must be a point - exactly three numbers [x, y, z].");
          return false;
        }
        q.Kind = "near";
        q.Point = p;
      }
      else if (within != null)
      {
        var box = within as JsonObject;
        if (box == null || !TryVec3(box["min"], out var min) || !TryVec3(box["max"], out var max))
        {
          error = ToolResult.Fail(ToolErrors.InvalidArgs,
            "\"within\" must be a box - {\"min\":[x,y,z], \"max\":[x,y,z]}, both corners as exactly three numbers.");
          return false;
        }
        q.Kind = "within";
        q.BoxMin = min;
        q.BoxMax = max;
      }
      else
      {
        var other = overlapping as JsonObject;
        var idNode = other?["id"];
        int otherId;
        try { otherId = idNode == null ? throw new FormatException() : idNode.GetValue<int>(); }
        catch
        {
          error = ToolResult.Fail(ToolErrors.InvalidArgs,
            "\"overlapping\" must name another object - {\"id\": <component id>, \"param\"?: <output>, \"index\"?: <object index, default 0>}.");
          return false;
        }
        q.Kind = "overlapping";
        q.OtherId = otherId;
        try { var p = other["param"]?.GetValue<string>(); q.OtherParam = string.IsNullOrWhiteSpace(p) ? null : p.Trim(); } catch { }
        try { q.OtherIndex = other["index"]?.GetValue<int>() ?? 0; } catch { q.OtherIndex = 0; }
        if (q.OtherIndex < 0)
        {
          error = ToolResult.Fail(ToolErrors.InvalidArgs, "\"overlapping.index\" must be zero or more.");
          return false;
        }
      }

      query = q;
      return true;
    }

    private static bool TryVec3(JsonNode node, out double[] v)
    {
      v = null;
      if (!(node is JsonArray a) || a.Count != 3) return false;
      var d = new double[3];
      for (var k = 0; k < 3; k++)
      {
        try { d[k] = a[k].GetValue<double>(); }
        catch { return false; }
      }
      v = d;
      return true;
    }
  }

  /// <summary>One spatial_query: a target to search and exactly one query shape.</summary>
  internal sealed class SpatialQuery
  {
    public GeometryTarget Target { get; set; }
    /// <summary>near | within | overlapping</summary>
    public string Kind { get; set; }
    public double[] Point { get; set; }
    public double[] BoxMin { get; set; }
    public double[] BoxMax { get; set; }
    public int OtherId { get; set; }
    public string OtherParam { get; set; }
    public int OtherIndex { get; set; }
  }
}
