using System.Text.Json.Nodes;

namespace Dwight.Agent
{
  /// <summary>
  /// The published tool schemas, built as OBJECTS rather than assembled as strings (#823 Part 2).
  ///
  /// <para>WHY. A schema written as text has to be quoted by hand, and the first computed value carrying
  /// a quotation mark broke one (#820: <c>edit_script</c>'s description names its claims as "preserves"
  /// and "changes", which closed the description string and made the whole descriptor unparseable). The
  /// string form bought nothing at the only place a schema is consumed - the transport parses it straight
  /// back - so the repair is not a better escape, it is to <b>stop the malformed state being
  /// representable</b>. A <see cref="JsonObject"/> cannot be mis-quoted; serialisation does the escaping,
  /// at the destination, which is where escaping belongs (#335).</para>
  ///
  /// <para>Deliberately small and boring: these are the five shapes the 22 published schemas actually
  /// use, not a JSON-Schema library. Key insertion order is preserved by <c>JsonObject</c>, so the model
  /// still reads a tool's properties in the order they are written here - which is why every helper takes
  /// its properties in order and none of them sorts anything.</para>
  /// </summary>
  internal static class Sch
  {
    /// <summary>A published input schema: an object with these properties, these required names, and
    /// <c>additionalProperties:false</c>. Every one of the 22 is closed, so this helper closes them -
    /// an open one would have to say so, rather than being reachable by forgetting.</summary>
    public static JsonObject Object(JsonObject properties, params string[] required)
    {
      var o = new JsonObject { ["type"] = "object", ["properties"] = properties ?? new JsonObject() };
      if (required != null && required.Length > 0) o["required"] = Names(required);
      o["additionalProperties"] = false;
      return o;
    }

    /// <summary>A nested object PROPERTY. Open (no <c>additionalProperties</c>), which is what the
    /// published schemas do today - the closed/open distinction is the caller's to state.</summary>
    public static JsonObject Sub(string description, JsonObject properties, params string[] required)
    {
      var o = new JsonObject { ["type"] = "object" };
      if (description != null) o["description"] = description;
      o["properties"] = properties ?? new JsonObject();
      if (required != null && required.Length > 0) o["required"] = Names(required);
      return o;
    }

    /// <summary>The properties map, in the order given. A plain <c>JsonObject</c> under the hood; this
    /// exists so a call site reads as a list of named fields rather than as nested braces.</summary>
    public static JsonObject Props(params (string name, JsonObject schema)[] props)
    {
      var o = new JsonObject();
      if (props != null)
        foreach (var (name, schema) in props) o[name] = schema;
      return o;
    }

    public static JsonObject Str(string description = null) => Leaf("string", description);
    public static JsonObject Int(string description = null) => Leaf("integer", description);
    public static JsonObject Num(string description = null) => Leaf("number", description);
    public static JsonObject Bool(string description = null) => Leaf("boolean", description);

    /// <summary>A string constrained to a fixed set - the values the model must send verbatim.</summary>
    public static JsonObject Enum(string description, params string[] values)
    {
      var o = Leaf("string", description);
      o["enum"] = Names(values);
      return o;
    }

    /// <summary>An array of <paramref name="items"/>. The description rides on the ARRAY, which is where
    /// the published schemas put it.</summary>
    public static JsonObject Arr(JsonObject items, string description = null)
    {
      var o = new JsonObject { ["type"] = "array", ["items"] = items };
      if (description != null) o["description"] = description;
      return o;
    }

    private static JsonObject Leaf(string type, string description)
    {
      var o = new JsonObject { ["type"] = type };
      if (description != null) o["description"] = description;
      return o;
    }

    private static JsonArray Names(string[] values)
    {
      var a = new JsonArray();
      foreach (var v in values) a.Add(v);
      return a;
    }
  }
}
