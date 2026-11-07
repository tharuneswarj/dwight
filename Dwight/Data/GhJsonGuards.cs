using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace Dwight.Data
{
  /// <summary>
  /// What internalized/persistent data a GhJSON document carries. Used both to NOTE it
  /// on the serialise side and to REPORT what was stripped on the placement side.
  /// </summary>
  internal sealed class InternalizedDataReport
  {
    /// <summary>Number of parameters that carried internalized data.</summary>
    public int ParameterCount { get; set; }

    /// <summary>Human-readable notes, e.g. "Division.B (integer:2)".</summary>
    public List<string> Notes { get; } = new List<string>();

    public bool Any => ParameterCount > 0;
  }

  /// <summary>
  /// Task 2.1 - the data-layer crash guard (ADR-029: Dwight never crashes Rhino).
  ///
  /// Diagnosis (Phase 2, from saved Phase-1 dumps): serialise is SAFE - it writes valid
  /// GhJSON that captures internalized data (e.g. <c>"internalizedData": {"{0}":{"{0}(0)":"integer:2"}}</c>).
  /// The crash is in PLACEMENT (<c>GhJsonGrasshopper.Put</c>) when it tries to reconstruct
  /// that internalized data - and because it can surface as a native fault, it cannot be
  /// caught with a try/catch. So the robust fix is to DETECT and STRIP the unsafe data
  /// BEFORE handing the JSON to the library, place the rest, and report what was dropped.
  ///
  /// All methods are fail-safe: on malformed input they return the original string and an
  /// empty report rather than throwing.
  /// </summary>
  internal static class GhJsonGuards
  {
    private const string InternalizedKey = "internalizedData";

    /// <summary>Inspect (no mutation) - for the serialise-side note.</summary>
    public static InternalizedDataReport Inspect(string json)
    {
      var report = new InternalizedDataReport();
      try
      {
        var root = JsonNode.Parse(json) as JsonObject;
        Walk(root, report, strip: false);
      }
      catch
      {
        // Fail-safe: never throw from a guard.
      }
      return report;
    }

    /// <summary>
    /// Strip every <c>internalizedData</c> block from the GhJSON so placement cannot crash.
    /// Returns the cleaned JSON (or the original, unchanged, if nothing was stripped or the
    /// input could not be parsed).
    /// </summary>
    public static string StripInternalizedData(string json, out InternalizedDataReport report)
    {
      report = new InternalizedDataReport();
      try
      {
        var root = JsonNode.Parse(json) as JsonObject;
        if (root == null) return json;
        Walk(root, report, strip: true);
        return report.Any ? root.ToJsonString() : json;
      }
      catch
      {
        // Fail-safe: if we cannot parse it, hand back the original untouched. Validation
        // (ADR-010) runs before placement and will reject anything genuinely broken.
        report = new InternalizedDataReport();
        return json;
      }
    }

    private static void Walk(JsonObject root, InternalizedDataReport report, bool strip)
    {
      if (root == null) return;
      if (!(root["components"] is JsonArray components)) return;

      foreach (var compNode in components)
      {
        if (!(compNode is JsonObject comp)) continue;
        var compName = comp["name"]?.GetValue<string>() ?? "(unnamed)";

        foreach (var settingsKey in new[] { "inputSettings", "outputSettings" })
        {
          if (!(comp[settingsKey] is JsonArray settings)) continue;

          foreach (var settingNode in settings)
          {
            if (!(settingNode is JsonObject setting)) continue;
            if (setting[InternalizedKey] == null) continue;

            var paramName = setting["parameterName"]?.GetValue<string>() ?? "(param)";
            var hint = FirstLeafString(setting[InternalizedKey]);
            report.ParameterCount++;
            report.Notes.Add(hint == null ? $"{compName}.{paramName}" : $"{compName}.{paramName} ({hint})");

            if (strip) setting.Remove(InternalizedKey);
          }
        }
      }
    }

    /// <summary>Pull the first scalar string out of a nested internalizedData node, for a readable hint.</summary>
    private static string FirstLeafString(JsonNode node)
    {
      switch (node)
      {
        case JsonValue v:
          try { return v.GetValue<string>(); } catch { return v.ToJsonString(); }
        case JsonObject o:
          foreach (var kv in o)
          {
            var s = FirstLeafString(kv.Value);
            if (s != null) return s;
          }
          return null;
        case JsonArray a:
          foreach (var item in a)
          {
            var s = FirstLeafString(item);
            if (s != null) return s;
          }
          return null;
        default:
          return null;
      }
    }
  }
}
