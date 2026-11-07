using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace Dwight.Labelling
{
  /// <summary>
  /// Builds the compact per-cluster summaries (Phase 6 WP-C) from a deterministic projection of the graph -
  /// the cluster partition, the component types, the wires, and the tree signatures - and renders them as the
  /// send payload. Headless and GH-free, so the SEND FORMAT is unit-tested: nothing here is a coordinate, an
  /// id beyond a cluster index, or the raw graph. The model only ever sees this projection (ADR-056/058).
  /// </summary>
  internal static class ClusterSummaries
  {
    /// <summary>Build one summary per cluster + the inter-cluster flow edges. <paramref name="sigByEdge"/> is
    /// the optional tree-signature phrase per directed edge (e.g. "4 branches, 1 each").</summary>
    public static LabelInput Build(
      IReadOnlyDictionary<int, int> macro,
      IReadOnlyDictionary<int, string> typeById,
      IReadOnlyList<(int from, int to)> edges,
      IReadOnlyDictionary<(int from, int to), string> sigByEdge = null,
      int centralCount = 3, int boundaryLimit = 6)
    {
      sigByEdge = sigByEdge ?? new Dictionary<(int, int), string>();
      var membersByCluster = macro.GroupBy(kv => kv.Value)
        .ToDictionary(grp => grp.Key, grp => grp.Select(kv => kv.Key).OrderBy(i => i).ToList());

      // intra-cluster degree (for the "most central" pick) and cross-cluster flow
      var degree = new Dictionary<int, int>();
      var flow = new HashSet<(int, int)>();
      foreach (var (u, v) in edges)
      {
        if (!macro.TryGetValue(u, out var cu) || !macro.TryGetValue(v, out var cv)) continue;
        if (cu == cv) { degree[u] = degree.GetValueOrDefault(u) + 1; degree[v] = degree.GetValueOrDefault(v) + 1; }
        else flow.Add((cu, cv));
      }

      var clusters = new List<ClusterSummary>();
      foreach (var cluster in membersByCluster.Keys.OrderBy(c => c))
      {
        var members = membersByCluster[cluster];
        var memberSet = new HashSet<int>(members);
        var s = new ClusterSummary { Id = cluster };

        foreach (var m in members)
        {
          var t = typeById.TryGetValue(m, out var tt) ? tt : "(component)";
          s.Types[t] = s.Types.GetValueOrDefault(t) + 1;
        }

        // boundary: what types (with tree sig) cross in / out, deterministic + capped
        var inn = new List<string>(); var outt = new List<string>();
        foreach (var (u, v) in edges)
        {
          if (!macro.TryGetValue(u, out var cu) || !macro.TryGetValue(v, out var cv) || cu == cv) continue;
          if (cv == cluster) inn.Add(Phrase(typeById, sigByEdge, u, u, v));
          if (cu == cluster) outt.Add(Phrase(typeById, sigByEdge, v, u, v));
        }
        foreach (var p in inn.Distinct().OrderBy(x => x).Take(boundaryLimit)) s.In.Add(p);
        foreach (var p in outt.Distinct().OrderBy(x => x).Take(boundaryLimit)) s.Out.Add(p);

        // central: the highest intra-cluster-degree members' (distinct) types
        var central = members
          .OrderByDescending(m => degree.GetValueOrDefault(m))
          .ThenBy(m => typeById.TryGetValue(m, out var t) ? t : "")
          .ThenBy(m => m)
          .Select(m => typeById.TryGetValue(m, out var t) ? t : "(component)")
          .Distinct().Take(centralCount);
        foreach (var t in central) s.Central.Add(t);

        clusters.Add(s);
      }

      var flowList = flow.OrderBy(f => f.Item1).ThenBy(f => f.Item2).ToList();
      return new LabelInput(clusters, flowList);
    }

    /// <summary>"&lt;type&gt;" or "&lt;type&gt; (&lt;sig&gt;)" for a boundary endpoint.</summary>
    private static string Phrase(IReadOnlyDictionary<int, string> typeById, IReadOnlyDictionary<(int, int), string> sigByEdge, int node, int from, int to)
    {
      var type = typeById.TryGetValue(node, out var t) ? t : "(component)";
      return sigByEdge.TryGetValue((from, to), out var sig) && !string.IsNullOrWhiteSpace(sig) ? $"{type} ({sig})" : type;
    }

    /// <summary>Render the send payload (deterministic JSON): the clusters + the flow edges, in ONE call so
    /// siblings are named AND arranged coherently. A cluster the model must name (in <paramref name="includeIds"/>,
    /// null = all) carries its full summary; a cluster already named (a cache hit, in <paramref name="knownLabels"/>)
    /// carries just its label - so the model always sees the WHOLE picture (every cluster, named or summarised,
    /// plus the full flow) while only the un-cached summaries cost tokens. Arrangement reads this whole picture.</summary>
    public static string Payload(LabelInput input, ISet<int> includeIds = null, IReadOnlyDictionary<int, string> knownLabels = null)
    {
      var clusters = new JsonArray();
      foreach (var c in input.Clusters)
      {
        if (includeIds == null || includeIds.Contains(c.Id))
        {
          var types = new JsonObject();
          foreach (var kv in c.Types.OrderBy(k => k.Key)) types[kv.Key] = kv.Value;
          clusters.Add(new JsonObject
          {
            ["id"] = c.Id,
            ["types"] = types,
            ["in"] = new JsonArray(c.In.Select(x => (JsonNode)x).ToArray()),
            ["out"] = new JsonArray(c.Out.Select(x => (JsonNode)x).ToArray()),
            ["central"] = new JsonArray(c.Central.Select(x => (JsonNode)x).ToArray()),
          });
        }
        else if (knownLabels != null && knownLabels.TryGetValue(c.Id, out var lbl))
        {
          clusters.Add(new JsonObject { ["id"] = c.Id, ["label"] = lbl });   // already named; here for arrangement context
        }
      }
      var flow = new JsonArray();
      foreach (var (from, to) in input.Flow) flow.Add(new JsonObject { ["from"] = from, ["to"] = to });

      return new JsonObject { ["action"] = "label.clusters", ["clusters"] = clusters, ["flow"] = flow }.ToJsonString();
    }

    /// <summary>The closed arrangement vocabulary (a relationship, never a position). Anything else is dropped.</summary>
    private static readonly HashSet<string> ArrangementKinds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    { "row", "stack", "grid", "adjacent" };

    /// <summary>Parse the model's arrangement hints ({arrangement:[{kind,clusters:[id]}]}) - relationships only,
    /// grounded: an unknown kind or an id not in <paramref name="validIds"/> is dropped (ADR-056/058). No
    /// coordinate, size, or position is ever read - the schema does not carry one.</summary>
    public static List<ArrangementHint> ParseArrangement(string json, ISet<int> validIds)
    {
      var result = new List<ArrangementHint>();
      try
      {
        var arr = JsonNode.Parse(json)?["arrangement"]?.AsArray();
        if (arr == null) return result;
        foreach (var a in arr)
        {
          if (!(a is JsonObject o)) continue;
          var kind = o["kind"]?.GetValue<string>()?.Trim().ToLowerInvariant();
          if (kind == null || !ArrangementKinds.Contains(kind)) continue;
          if (!(o["clusters"] is JsonArray cl)) continue;
          var hint = new ArrangementHint { Kind = kind };
          foreach (var n in cl)
          {
            int id; try { id = n.GetValue<int>(); } catch { continue; }
            if (validIds.Contains(id) && !hint.Clusters.Contains(id)) hint.Clusters.Add(id);
          }
          if (hint.Clusters.Count >= 2) result.Add(hint);   // a relationship needs at least two clusters
        }
      }
      catch { }
      return result;
    }

    /// <summary>Parse the model's reply ({labels:[{id,label}]}) into a cluster-id -&gt; label map, capping each
    /// label to a few words and dropping any id not in the request (groundedness, ADR-058).</summary>
    public static Dictionary<int, string> ParseLabels(string json, ISet<int> validIds, int maxWords = 4)
    {
      var result = new Dictionary<int, string>();
      try
      {
        var labels = JsonNode.Parse(json)?["labels"]?.AsArray();
        if (labels == null) return result;
        foreach (var l in labels)
        {
          if (!(l is JsonObject o) || o["id"] == null || o["label"] == null) continue;
          int id; try { id = o["id"].GetValue<int>(); } catch { continue; }
          if (!validIds.Contains(id)) continue;
          var label = o["label"].GetValue<string>()?.Trim();
          if (string.IsNullOrWhiteSpace(label)) continue;
          var words = label.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
          result[id] = string.Join(" ", words.Take(maxWords));
        }
      }
      catch { }
      return result;
    }

    /// <summary>(bool ok, string error) validator for the StructuredOutput seam: a non-empty labels array of
    /// {id,label} objects.</summary>
    public static (bool ok, string error) Validate(string json)
    {
      try
      {
        var root = JsonNode.Parse(json);
        var labels = root?["labels"]?.AsArray();
        if (labels == null) return (false, "missing a 'labels' array");
        if (labels.Count == 0) return (false, "the 'labels' array is empty");
        foreach (var l in labels)
        {
          if (!(l is JsonObject o)) return (false, "a label is not an object");
          if (o["id"] == null) return (false, "a label is missing 'id'");
          if (o["label"] == null) return (false, "a label is missing 'label'");
        }
        // arrangement is required by the schema (it may be an empty array = no relationship advised); each item
        // is a relationship over cluster ids - never a coordinate.
        var arr = root?["arrangement"];
        if (!(arr is JsonArray items)) return (false, "missing an 'arrangement' array");
        foreach (var a in items)
        {
          if (!(a is JsonObject o)) return (false, "an arrangement item is not an object");
          if (o["kind"] == null) return (false, "an arrangement item is missing 'kind'");
          if (!(o["clusters"] is JsonArray)) return (false, "an arrangement item is missing a 'clusters' array");
        }
        return (true, null);
      }
      catch (Exception ex) { return (false, ex.Message); }
    }

    /// <summary>WP-3.4(ii): validate the ARRANGEMENT only (labels may be absent/empty). Used when every cluster
    /// label is already cached but the arrangement is not - the model is asked only to arrange, so requiring a
    /// non-empty labels array would fail a perfectly good arrangement-only reply.</summary>
    public static (bool ok, string error) ValidateArrangement(string json)
    {
      try
      {
        var arr = JsonNode.Parse(json)?["arrangement"];
        if (!(arr is JsonArray items)) return (false, "missing an 'arrangement' array");
        foreach (var a in items)
        {
          if (!(a is JsonObject o)) return (false, "an arrangement item is not an object");
          if (o["kind"] == null) return (false, "an arrangement item is missing 'kind'");
          if (!(o["clusters"] is JsonArray)) return (false, "an arrangement item is missing a 'clusters' array");
        }
        return (true, null);
      }
      catch (Exception ex) { return (false, ex.Message); }
    }
  }
}
