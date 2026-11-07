using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;

namespace Dwight.Agent
{
  /// <summary>One output of a component the build placed, as it stood after the solve.</summary>
  internal sealed class PlacedOutputFact
  {
    /// <summary>The output's label, or null for a floating param (it has one implicit output and no name for it).</summary>
    public string Port { get; set; }
    /// <summary>Items on the output in total, geometry or not.</summary>
    public int Items { get; set; }
    /// <summary>Geometry objects among them.</summary>
    public int Geometry { get; set; }
    /// <summary>Geometry objects RhinoCommon reports invalid (a zero-length line, a singular surface).</summary>
    public int Invalid { get; set; }
    /// <summary>The plain word for what the geometry is - "line", "brep", "point" - or "object" when mixed.</summary>
    public string Kind { get; set; }
    public double? TotalLength { get; set; }
    public double? TotalArea { get; set; }
    /// <summary>The size of the union bounding box, x y z, when any object had one.</summary>
    public double[] Size { get; set; }
    /// <summary>A bound the reader applied (a time budget spent, items past a cap not measured) - stated, never silent.</summary>
    public string Note { get; set; }
  }

  /// <summary>One component the build placed, with what Grasshopper says about it and what it holds.</summary>
  internal sealed class PlacedComponentFact
  {
    public string Ref { get; set; }
    public int Id { get; set; }
    public string Type { get; set; }
    public string Nick { get; set; }
    public List<string> Warnings { get; set; } = new List<string>();
    public List<string> Errors { get; set; } = new List<string>();
    public List<PlacedOutputFact> Outputs { get; set; } = new List<PlacedOutputFact>();

    /// <summary>U19: for a SCRIPT the build placed, the emptiness oracle's outcome ("produced" / "unexercised") and the
    /// sentence that states it (<see cref="ScriptBuildVerdict.Note"/>); null for every other component. The outputs'
    /// items above count NULL items too (an errored or None-assigning script emits one), so this - which counts only
    /// non-null items - is what the line says about a script's data.</summary>
    public string ScriptOutcome { get; set; }
    public string ScriptNote { get; set; }
  }

  /// <summary>
  /// WHAT THE BUILD'S OWN COMPONENTS HOLD AFTER THE SOLVE, said on the line that used to say "verified" (U26; #916, #923).
  ///
  /// THE DEFECT, FOUR TIMES. The verdict trio - preservation, completeness, solve-health - is right as a GATE and wrong
  /// as a SENTENCE. It passed, truthfully, over a region that computed nothing (every component warning "failed to
  /// collect data"), over an invalid subsurface (the component's own warning sat under the word "verified"), over
  /// forty cells of 0.025 m2 on a 240 m2 facade, and over 486 lines of length zero. Each error was visible through
  /// the geometry reads the moment the model was asked to look; the buildline never carried them, so nobody looked.
  ///
  /// SO THE LINE STATES WHAT WAS CHECKED, NEVER THE WORD "VERIFIED", and carries one plugin-read fact per output the
  /// build placed that holds geometry - count, and the bounding size, total length or total area the type has - plus
  /// every new warning and every empty output on a component the build PLACED. A component the build created has no
  /// pre-existing state, so a warning on it is the build's own.
  ///
  /// NOT A REVERT. A region may legitimately wait on a dependency (the errors wording already says so), and a fact is
  /// not a judgement: "486 lines, total length 0 m" is for the model and the user to read, and both will.
  ///
  /// THE FACTS ARE READ BY THE GEOMETRY READS' OWN READER (<c>GhGeometry.Items</c> / <c>GhGeometry.Facts</c>), zero-token
  /// and solve-free - reused, never duplicated. GH-free here (ADR-091): the shim hands in <see cref="GeoObject"/>s and
  /// this composes. Bounded (ADR-035): one line per component, a cap on lines, and every cut said.
  /// </summary>
  internal static class BuildPlacedFacts
  {
    /// <summary>Components named on one line before the rest are counted - the same bound the running summary keeps.</summary>
    public const int MaxLines = 12;

    /// <summary>Chars of the facts sentence before it is cut and marked - it rides a verdict note and a thread line.</summary>
    public const int MaxChars = 640;

    /// <summary>Geometry objects measured per output before the reader stops and says so. A fact does not scale with the
    /// data (the Q6 ruling), and neither may the time it takes to state one.</summary>
    public const int MaxItemsMeasured = 5000;

    // ---- aggregation: from the geometry reads' per-object facts to one output's fact ------------------------

    /// <summary>Fold the per-object facts of one output into its fact. <paramref name="items"/> is the output's total
    /// item count; <paramref name="measured"/> the objects the reader produced facts for (at most <see cref="MaxItemsMeasured"/>);
    /// <paramref name="geometry"/> how many geometry objects the output holds in total.</summary>
    public static PlacedOutputFact Aggregate(string port, int items, int geometry, IReadOnlyList<GeoObject> measured, string note)
    {
      var f = new PlacedOutputFact { Port = port, Items = items, Geometry = geometry, Note = note };
      measured = measured ?? new GeoObject[0];
      if (measured.Count == 0) return f;

      var kinds = new HashSet<string>(StringComparer.Ordinal);
      double length = 0, area = 0;
      bool anyLength = false, anyArea = false;
      double[] min = null, max = null;
      foreach (var o in measured)
      {
        if (o == null) continue;
        kinds.Add(KindOf(o.Type));
        if (o.Valid == false) f.Invalid++;
        if (o.Length.HasValue) { length += o.Length.Value; anyLength = true; }
        if (o.Area.HasValue) { area += o.Area.Value; anyArea = true; }
        if (o.BboxMin != null && o.BboxMax != null && o.BboxMin.Length == 3 && o.BboxMax.Length == 3)
        {
          if (min == null) { min = (double[])o.BboxMin.Clone(); max = (double[])o.BboxMax.Clone(); }
          else
            for (var i = 0; i < 3; i++)
            {
              min[i] = Math.Min(min[i], o.BboxMin[i]);
              max[i] = Math.Max(max[i], o.BboxMax[i]);
            }
        }
      }
      f.Kind = kinds.Count == 1 ? kinds.First() : "object";
      if (anyLength) f.TotalLength = length;
      if (anyArea) f.TotalArea = area;
      if (min != null) f.Size = new[] { max[0] - min[0], max[1] - min[1], max[2] - min[2] };
      return f;
    }

    /// <summary>The plain word for a RhinoCommon type name: a person reads "486 lines", not "486 LineCurves".</summary>
    public static string KindOf(string rhinoType)
    {
      var t = rhinoType ?? "";
      if (t == "LineCurve") return "line";
      if (t == "ArcCurve") return "arc";
      if (t == "PolylineCurve") return "polyline";
      if (t.EndsWith("Curve", StringComparison.Ordinal)) return "curve";
      if (t == "Point") return "point";
      if (t == "PointCloud") return "point cloud";
      if (t == "Brep") return "brep";
      if (t == "Mesh") return "mesh";
      if (t == "Extrusion") return "extrusion";
      if (t == "SubD") return "SubD";
      if (t.EndsWith("Surface", StringComparison.Ordinal)) return "surface";
      return t.Length == 0 ? "object" : t;
    }

    // ---- the wire: facts to JSON on the verdict, and back off the envelope -------------------------------------

    public static JsonArray ToJson(IReadOnlyList<PlacedComponentFact> facts)
    {
      var arr = new JsonArray();
      foreach (var c in facts ?? new PlacedComponentFact[0])
      {
        if (c == null) continue;
        var o = new JsonObject
        {
          ["ref"] = c.Ref ?? "",
          ["id"] = c.Id,
          ["type"] = c.Type ?? "",
        };
        if (!string.IsNullOrEmpty(c.Nick)) o["nick"] = c.Nick;
        if (c.Warnings.Count > 0) o["warnings"] = new JsonArray(c.Warnings.Select(w => (JsonNode)w).ToArray());
        if (c.Errors.Count > 0) o["errors"] = new JsonArray(c.Errors.Select(e => (JsonNode)e).ToArray());
        if (!string.IsNullOrEmpty(c.ScriptNote))
          o["script"] = new JsonObject { ["outcome"] = c.ScriptOutcome ?? "", ["note"] = c.ScriptNote };
        var outputs = new JsonArray();
        foreach (var p in c.Outputs ?? new List<PlacedOutputFact>())
        {
          var po = new JsonObject { ["items"] = p.Items };
          if (p.Port != null) po["port"] = p.Port;
          if (p.Items == 0) po["empty"] = true;
          if (p.Geometry > 0)
          {
            po["geometry"] = p.Geometry;
            po["kind"] = p.Kind ?? "object";
            if (p.Invalid > 0) po["invalid"] = p.Invalid;
            if (p.TotalLength.HasValue) po["totalLength"] = p.TotalLength.Value;
            if (p.TotalArea.HasValue) po["totalArea"] = p.TotalArea.Value;
            if (p.Size != null) po["size"] = new JsonArray(p.Size.Take(3).Select(d => (JsonNode)d).ToArray());
          }
          if (!string.IsNullOrEmpty(p.Note)) po["note"] = p.Note;
          outputs.Add(po);
        }
        o["outputs"] = outputs;
        arr.Add(o);
      }
      return arr;
    }

    /// <summary>The verdict's facts read back the way the panel receives them - tested against <see cref="ToJson"/>'s own output.</summary>
    public static List<PlacedComponentFact> FromJson(JsonNode node)
    {
      var facts = new List<PlacedComponentFact>();
      if (!(node is JsonArray arr)) return facts;
      foreach (var n in arr)
      {
        var o = n as JsonObject;
        if (o == null) continue;
        var c = new PlacedComponentFact { Ref = Str(o["ref"]), Type = Str(o["type"]), Nick = Str(o["nick"]) };
        try { c.Id = o["id"]?.GetValue<int>() ?? 0; } catch { }
        c.Warnings = Strings(o["warnings"]);
        c.Errors = Strings(o["errors"]);
        if (o["script"] is JsonObject script) { c.ScriptOutcome = Str(script["outcome"]); c.ScriptNote = Str(script["note"]); }
        foreach (var pn in (o["outputs"] as JsonArray) ?? new JsonArray())
        {
          var po = pn as JsonObject;
          if (po == null) continue;
          var p = new PlacedOutputFact { Port = Str(po["port"]), Kind = Str(po["kind"]), Note = Str(po["note"]) };
          try { p.Items = po["items"]?.GetValue<int>() ?? 0; } catch { }
          try { p.Geometry = po["geometry"]?.GetValue<int>() ?? 0; } catch { }
          try { p.Invalid = po["invalid"]?.GetValue<int>() ?? 0; } catch { }
          try { p.TotalLength = po["totalLength"]?.GetValue<double>(); } catch { }
          try { p.TotalArea = po["totalArea"]?.GetValue<double>(); } catch { }
          try
          {
            if (po["size"] is JsonArray size && size.Count == 3)
              p.Size = size.Select(v => v.GetValue<double>()).ToArray();
          }
          catch { p.Size = null; }
          c.Outputs.Add(p);
        }
        facts.Add(c);
      }
      return facts;
    }

    // ---- the sentences ----------------------------------------------------------------------------------------

    /// <summary>
    /// WHAT THE TRIO CHECKED, said instead of "verified" - true in every state that reaches an applied verdict:
    /// completeness holds (placed and wired as declared), preservation holds (nothing existing changed), and
    /// solve-health is reported by count rather than as a word.
    ///
    /// Brief CO: AND WHAT IT DID NOT. A Bambu A1 script whose input had item access ran once per G-code line, 1,070,700 output items from 42,828 lines, and this line passed it, truly: all three legs held.
    /// The person read "checked" as "right". So the sentence names its three checks and says what they leave open.
    /// </summary>
    public static string Checked(int newErrors) => Checked(newErrors, 1);

    /// <summary>
    /// Ninth gate, row 7 (1 November 2026): a lone Panel placed with no wires declared read "placed and wired as declared", a claim
    /// about a check that had nothing to check (#297: a sentence is true in every state that reaches it). With no wires declared the
    /// sentence says "placed as declared"; the leg still ran, on an empty set, and the words say what it covered.
    /// </summary>
    public static string Checked(int newErrors, int wiresDeclared) =>
      "Checked: every declared component is placed" + (wiresDeclared > 0 ? " and wired" : "") +
      " as declared; nothing that existed before has changed; the solution has " +
      (newErrors == 0 ? "no new errors" : newErrors + " new error" + (newErrors == 1 ? "" : "s")) +
      ". Not checked: whether the result is what you meant, or what the outputs hold.";

    /// <summary>One component's line: its name and id, then its warnings and errors, its empty outputs, and one fact per
    /// output that holds geometry. Null for a component with nothing to say - no warning, no empty output, no geometry.</summary>
    public static string Line(PlacedComponentFact c, string units)
    {
      if (c == null) return null;
      var parts = new List<string>();
      foreach (var e in c.Errors ?? new List<string>()) parts.Add("ERROR " + One(e));
      foreach (var w in c.Warnings ?? new List<string>()) parts.Add("WARNING " + One(w));

      // U19: a script's data is stated by the script note (non-null items, the oracle's rule), so the generic "empty output"
      // wording - which counts items, nulls included - is not also said about it.
      var empty = string.IsNullOrEmpty(c.ScriptNote)
        ? (c.Outputs ?? new List<PlacedOutputFact>()).Where(p => p.Items == 0).Select(p => p.Port).ToList()
        : new List<string>();
      if (empty.Count > 0)
        parts.Add(empty.Count == 1 && empty[0] == null
          ? "empty - it holds nothing"
          : "empty output" + (empty.Count == 1 ? "" : "s") + " " + string.Join(", ", empty.Select(p => p ?? "(unnamed)")));

      if (!string.IsNullOrEmpty(c.ScriptNote)) parts.Add(One(c.ScriptNote));

      foreach (var p in (c.Outputs ?? new List<PlacedOutputFact>()).Where(p => p.Geometry > 0))
        parts.Add(Geometry(p, units));

      if (parts.Count == 0) return null;
      return Name(c) + ": " + string.Join("; ", parts);
    }

    /// <summary>"486 lines, total length 0 m" / "1 brep, 61.2 x 24.1 x 18.0 m" / "40 surfaces, total area 1.00 m2".</summary>
    public static string Geometry(PlacedOutputFact p, string units)
    {
      var sb = new System.Text.StringBuilder();
      if (p.Port != null) sb.Append(p.Port).Append(": ");
      sb.Append(p.Geometry).Append(' ').Append(Plural(p.Kind ?? "object", p.Geometry));
      if (p.Invalid > 0) sb.Append(" (").Append(p.Invalid).Append(" invalid)");
      var u = string.IsNullOrEmpty(units) ? "" : " " + units;
      if (p.TotalLength.HasValue) sb.Append(", total length ").Append(Fmt(p.TotalLength.Value)).Append(u);
      if (p.TotalArea.HasValue) sb.Append(", total area ").Append(Fmt(p.TotalArea.Value)).Append(u.Length == 0 ? "" : u + "2");
      if (!p.TotalLength.HasValue && !p.TotalArea.HasValue && p.Size != null && p.Size.Length == 3)
        sb.Append(", ").Append(Fmt(p.Size[0])).Append(" x ").Append(Fmt(p.Size[1])).Append(" x ").Append(Fmt(p.Size[2])).Append(u);
      else if (p.Size != null && p.Size.Length == 3 && (p.TotalArea.HasValue || p.Geometry == 1))
        sb.Append(", bounds ").Append(Fmt(p.Size[0])).Append(" x ").Append(Fmt(p.Size[1])).Append(" x ").Append(Fmt(p.Size[2])).Append(u);
      if (!string.IsNullOrEmpty(p.Note)) sb.Append(" (").Append(p.Note).Append(')');
      return sb.ToString();
    }

    /// <summary>Every component's line, bounded: at most <see cref="MaxLines"/>, and the count of the rest said.</summary>
    public static List<string> Lines(IReadOnlyList<PlacedComponentFact> facts, string units)
    {
      var lines = new List<string>();
      var all = (facts ?? new PlacedComponentFact[0]).Select(c => Line(c, units)).Where(l => l != null).ToList();
      lines.AddRange(all.Take(MaxLines));
      if (all.Count > MaxLines)
        lines.Add((all.Count - MaxLines) + " more component" + (all.Count - MaxLines == 1 ? "" : "s") +
                  " measured - read them with get_geometry_summary.");
      return lines;
    }

    /// <summary>
    /// THE NOTE AN APPLIED VERDICT CARRIES TO THE BUILDLINE AND THE MODEL: what was checked, then the facts, in one
    /// bounded sentence. Read off the verdict's own payload (<c>placedFacts</c>, <c>newErrors</c>, <c>units</c>), so the
    /// panel and the turn-start fact say what the verdict says.
    /// </summary>
    public static string Summary(JsonNode verdict)
    {
      if (verdict == null) return null;
      var newErrors = 0;
      try { newErrors = verdict["newErrors"]?.GetValue<int>() ?? 0; } catch { }
      var units = Str(verdict["units"]);
      var facts = FromJson(verdict["placedFacts"]);
      // Tenth gate, row 1 (1 November 2026): this path recomputed the sentence through the one-argument form, so a lone
      // Panel's receipt still read "wired" after PR #191 fixed the verdict's own field. The verdict carries the sentence it
      // was checked with ("checked", written with its declared wire count); read that, and fall back to the count it carries.
      var text = Str(verdict["checked"]);
      if (string.IsNullOrEmpty(text))
      {
        var wires = 1;
        try { wires = verdict["asked"]?["wires"]?.GetValue<int>() ?? 1; } catch { }
        text = Checked(newErrors, wires);
      }
      var lines = Lines(facts, units);
      if (lines.Count > 0) text += " " + string.Join(" ", lines.Select(l => l.EndsWith(".", StringComparison.Ordinal) ? l : l + "."));
      // U73: a script built from the card's text rather than the model's says so on the panel's line.
      if (verdict["scriptsFromCard"] is JsonArray fromCard && fromCard.Count > 0) text += " Built from the source on the card.";
      return Bounded(text);
    }

    /// <summary>True when a sentence still says the bare word the line is no longer allowed to say.</summary>
    public static bool SaysVerified(string text) =>
      !string.IsNullOrEmpty(text) &&
      System.Text.RegularExpressions.Regex.IsMatch(text, @"\bverified\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    // ---- helpers ----------------------------------------------------------------------------------------------

    private static string Name(PlacedComponentFact c)
    {
      var name = string.IsNullOrWhiteSpace(c.Nick) || string.Equals(c.Nick, c.Type, StringComparison.Ordinal)
        ? (c.Type ?? "component")
        : c.Nick + " (" + c.Type + ")";
      return name + (c.Id > 0 ? " (" + c.Id + ")" : "");
    }

    private static string Plural(string kind, int n)
    {
      if (n == 1) return kind;
      if (kind == "SubD") return "SubDs";
      if (kind.EndsWith("s", StringComparison.Ordinal) || kind.EndsWith("x", StringComparison.Ordinal)) return kind + "es";
      return kind + "s";
    }

    /// <summary>A length or area for a person: whole numbers past a hundred, one decimal past ten, two past one, three
    /// significant figures below - so 0.025 m2 reads as 0.025 and 61.2 as 61.2.</summary>
    public static string Fmt(double v)
    {
      if (double.IsNaN(v) || double.IsInfinity(v)) return "?";
      var a = Math.Abs(v);
      if (a == 0) return "0";
      if (a >= 100) return v.ToString("F0", CultureInfo.InvariantCulture);
      if (a >= 10) return v.ToString("F1", CultureInfo.InvariantCulture);
      if (a >= 1) return v.ToString("F2", CultureInfo.InvariantCulture);
      return v.ToString("G3", CultureInfo.InvariantCulture);
    }

    private static string One(string text) =>
      string.Join(" ", (text ?? "").Split(new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()));

    private static string Bounded(string text) =>
      text == null || text.Length <= MaxChars ? text : text.Substring(0, MaxChars - 3) + "...";

    private static string Str(JsonNode n)
    {
      if (n == null) return null;
      try { return n.GetValue<string>(); } catch { return null; }
    }

    private static List<string> Strings(JsonNode n)
    {
      var list = new List<string>();
      if (!(n is JsonArray arr)) return list;
      foreach (var x in arr) { var s = Str(x); if (s != null) list.Add(s); }
      return list;
    }
  }
}
