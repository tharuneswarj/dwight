using System.Text.Json.Nodes;

namespace Dwight.Llm
{
  /// <summary>
  /// THE SCHEMA PRE-FLIGHT: a tool whose schema cannot be published is not published, and the turn
  /// refuses in our own words naming the tool - before the request is sent, and before anything is spent.
  ///
  /// <para>WHY THIS EXISTS. The transport used to parse each schema with a catch that returned an empty
  /// object, so a malformed schema reached the provider as <c>input_schema: {}</c>. The tool was then
  /// advertised as taking NO PARAMETERS: the model can see it, calls it, and our own parser refuses the
  /// call for missing fields - with nothing anywhere saying why. The model loops against unexplained
  /// refusals with no route to the cause, and the user sees a capability that exists and never works.</para>
  ///
  /// <para><b>An empty object is the most dangerous possible fallback, because it is SHAPED LIKE A VALID
  /// ANSWER.</b> A throw would have been loud and a null would have been caught; a well-formed empty
  /// schema passes every downstream check and lies only about what it permits. So the empty fallback is
  /// gone from the schema path entirely rather than left sitting behind this check - "defence in depth"
  /// there would leave the defect intact for the next caller and call it safety.</para>
  ///
  /// <para>Shaped deliberately like <c>AgentLoopCore.CheckTranscript</c> (#297), which does this job one
  /// field over: null means sendable, anything else is the problem in one sentence. Same reason, too -
  /// the defect is OURS, so the diagnosis must be ours, not a provider error that sends the reader to the
  /// adapter, the key and the model id, everywhere except the place that built the schema.</para>
  ///
  /// <para>It is deliberately NOT a repair. A schema we cannot parse is one we must not guess at and
  /// spend on (#297: a guard that repairs is a guard that hides).</para>
  /// </summary>
  internal static class LlmSchemaCheck
  {
    /// <summary>Returns null when every schema in <paramref name="request"/> can be published, otherwise
    /// one sentence naming what is wrong and which tool it belongs to.</summary>
    public static string Check(LlmRequest request)
    {
      if (request == null) return null;

      if (request.Tools != null)
        foreach (var t in request.Tools)
        {
          if (t == null) return "a tool in this request was missing";
          var name = string.IsNullOrWhiteSpace(t.Name) ? "an unnamed tool" : "the tool \"" + t.Name + "\"";
          var problem = SchemaProblem(t.InputSchemaJson);
          if (problem != null) return name + " could not be published: its input schema " + problem;
        }

      // The structured-output path (ADR-058): the same failure, on the route all five verbs take. A
      // malformed schema here would ask the model to fill a shape we never described.
      if (!string.IsNullOrEmpty(request.StructuredName))
      {
        var problem = SchemaProblem(request.StructuredSchemaJson);
        if (problem != null)
          return "the structured reply \"" + request.StructuredName + "\" could not be requested: its schema " + problem;
      }

      return null;
    }

    /// <summary>What is wrong with one schema, or null if nothing is. <b>Parsing is not the bar</b> - an
    /// array and a bare string both parse cleanly and neither can be an <c>input_schema</c>, so the type
    /// is checked as well as the syntax.</summary>
    private static string SchemaProblem(string json)
    {
      if (string.IsNullOrWhiteSpace(json)) return "was empty";

      JsonNode parsed;
      try { parsed = JsonNode.Parse(json); }
      catch (System.Text.Json.JsonException ex) { return "is not valid JSON (" + ex.Message + ")"; }

      if (parsed == null) return "was empty";
      if (!(parsed is JsonObject)) return "is valid JSON but not an object, so it cannot describe parameters";
      return null;
    }
  }
}
