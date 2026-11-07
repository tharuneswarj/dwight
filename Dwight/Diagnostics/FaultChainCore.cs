using System;
using System.Collections.Generic;
using System.Linq;

namespace Dwight.Diagnostics
{
  /// <summary>
  /// The fault-chain trace with no Grasshopper in it (ADR-091, Brief EX): which fault is a root, which earns a full explanation, which faults feed which, the order the report lists them in, and the chains the
  /// result is drawn as. <see cref="FaultScan"/> finds the faults on the live document and hands them here by id, so the rules that decide a ROOT CAUSE are tested headless on a graph of three nodes.
  /// </summary>
  internal static class FaultChainCore
  {
    /// <summary>One component in the scan set, as the facts the scan reads. <see cref="InputDataCount"/> is a live read of what reached its inputs, asked only when the silent rule needs it.</summary>
    internal sealed class Node
    {
      public int Id;
      public List<(string Level, string Text)> Msgs = new List<(string Level, string Text)>();
      public bool HasUpstream;
      public bool HasDownstream;
      public int[] OutCounts;
      public Func<int> InputDataCount;
    }

    /// <summary>One wire: the node it leaves, the node it enters, and the input it enters by (its label as the graph names it, the nickname first).</summary>
    internal sealed class Wire
    {
      public int From;
      public int To;
      public string ToParam;
    }

    /// <summary>One fault as the scan found it. Kind: 0 error, 1 warning, 2 silent (FaultScan.Kind's order).</summary>
    internal sealed class Found
    {
      public int Id;
      public int Kind;
      public bool IsRoot;
      public bool Intrinsic;
      public List<int> Upstream = new List<int>();

      /// <summary>Brief EX: the inputs with nothing wired into them, for a root whose only messages are Grasshopper's "failed to collect data" for those inputs. Empty for every other fault.</summary>
      public List<string> Unwired = new List<string>();
    }

    /// <summary>What the trace says of one fault.</summary>
    internal sealed class Traced
    {
      public bool IsRoot;
      public bool Intrinsic;
      public List<int> Upstream = new List<int>();
    }

    /// <summary>
    /// The scan, given the scan set and every wire: find the faults (an error, a warning, or a silent failure), trace each one's place in the chain, and list them in the report's order.
    /// </summary>
    public static List<Found> Scan(IEnumerable<Node> scan, IReadOnlyList<Wire> wires)
    {
      var nodes = (scan ?? Enumerable.Empty<Node>()).Where(n => n != null).ToList();
      var found = new Dictionary<int, Found>();
      foreach (var n in nodes)
      {
        var kind = KindOf(n);
        if (kind.HasValue) found[n.Id] = new Found { Id = n.Id, Kind = kind.Value };
      }
      if (found.Count == 0) return new List<Found>();

      var faultIds = found.Keys.ToHashSet();
      var msgsOf = nodes.GroupBy(n => n.Id).ToDictionary(g => g.Key, g => g.First().Msgs);
      IEnumerable<int> Feeding(int id) => wires.Where(w => w.To == id).Select(w => w.From);
      foreach (var f in found.Values)
      {
        var traced = Trace(f.Id, faultIds, Feeding, msgsOf[f.Id]);
        f.Upstream = traced.Upstream;
        f.IsRoot = traced.IsRoot;
        f.Intrinsic = traced.Intrinsic;
        if (f.IsRoot) f.Unwired = UnwiredInputs(msgsOf[f.Id], wires.Where(w => w.To == f.Id).Select(w => w.ToParam));
      }
      return Order(found.Values, f => f.IsRoot, f => f.Kind, f => f.Id);
    }

    /// <summary>A node's fault kind (0 error, 1 warning, 2 silent), or null when it is no fault.</summary>
    public static int? KindOf(Node n)
    {
      if (n.Msgs.Any(m => m.Level == "error")) return 0;
      if (n.Msgs.Any(m => m.Level == "warning")) return 1;
      if (IsSilentFailure(n)) return 2;
      return null;
    }

    /// <summary>A green component that received data and feeds something downstream, yet produced no output at all. Conservative on purpose (architect note): ALL outputs empty, never just one, so a component
    /// with one legitimately-empty output is not flagged. Presented as a suspicion, not a certainty.</summary>
    public static bool IsSilentFailure(Node n)
    {
      if (n.Msgs.Any(m => m.Level == "error" || m.Level == "warning")) return false;
      if (!n.HasDownstream) return false;                 // nothing relies on its output
      if (n.OutCounts == null || n.OutCounts.Length == 0) return false;
      if (!n.OutCounts.All(c => c == 0)) return false;    // some output carried data -> not silent
      if (n.HasUpstream && (n.InputDataCount?.Invoke() ?? 0) == 0) return false; // it simply got nothing
      // A pure source (no upstream) that outputs nothing is usually unset, not a silent failure; skip it.
      return n.HasUpstream;
    }

    /// <summary>Grasshopper's own words for an input that received nothing (its string table: "Input parameter {0} failed to collect data", the parameter's nickname in the braces).</summary>
    private static readonly System.Text.RegularExpressions.Regex Starved =
      new System.Text.RegularExpressions.Regex(@"^Input parameter (.+) failed to collect data\.?$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// Brief EX (the clip's Negative, unwired, badged ROOT CAUSE while the model's words said "a knock-on effect of the failed Division"): when every message a fault carries is Grasshopper's "failed to collect data"
    /// for an input, and none of those inputs has a wire into it, the graph states the cause, so the plugin says it and no model judgement is asked. Any other message, or a named input that IS wired (fed by
    /// something that gave nothing), and this is empty, so the model explains it as before. An optional input with no wire carries no message, so it never fires this.
    /// </summary>
    public static List<string> UnwiredInputs(IEnumerable<(string Level, string Text)> msgs, IEnumerable<string> wiredInputs)
    {
      var named = new List<string>();
      foreach (var m in (msgs ?? Enumerable.Empty<(string Level, string Text)>()).Where(m => m.Level == "error" || m.Level == "warning"))
      {
        var starved = Starved.Match((m.Text ?? "").Trim());
        if (!starved.Success) return new List<string>();
        var name = starved.Groups[1].Value.Trim();
        if (name.Length == 0) return new List<string>();
        if (!named.Contains(name)) named.Add(name);
      }
      var wired = new HashSet<string>(wiredInputs ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
      return named.Count == 0 || named.Any(wired.Contains) ? new List<string>() : named;
    }

    /// <summary>The plugin's cause for an unwired root, in the card's Markdown: "Nothing is wired into `x`."</summary>
    public static string UnwiredCause(IReadOnlyList<string> inputs) => "Nothing is wired into " + Names(inputs, "or") + ".";

    /// <summary>The plugin's fix for an unwired root: "Connect a source to `x`."</summary>
    public static string UnwiredFix(IReadOnlyList<string> inputs) => "Connect a source to " + Names(inputs, "and") + ".";

    private static string Names(IReadOnlyList<string> inputs, string joiner)
    {
      var code = (inputs ?? Array.Empty<string>()).Select(n => "`" + n.Replace("`", "'") + "`").ToList();
      return code.Count <= 1 ? code.FirstOrDefault() ?? "an input" : string.Join(", ", code.Take(code.Count - 1)) + " " + joiner + " " + code[code.Count - 1];
    }

    /// <summary>
    /// One fault's place in the chain. IsRoot: nothing faulty feeds it. Intrinsic: it has a REAL own error, an error or warning that is not a data-starvation message, or nothing faulty feeds it (ADR-078 refined).
    /// <paramref name="feeding"/> gives the ids wired into a node, <paramref name="faultIds"/> every fault the scan found.
    /// </summary>
    public static Traced Trace(int id, HashSet<int> faultIds, Func<int, IEnumerable<int>> feeding, IEnumerable<(string Level, string Text)> msgs)
    {
      var upstream = AncestorsAmong(id, faultIds, feeding);
      var hasAnyAncestor = upstream.Count > 0;
      var hasRealOwnError = (msgs ?? Enumerable.Empty<(string Level, string Text)>()).Any(m =>
        (m.Level == "error" || m.Level == "warning") && !IsStarvationMessage(m.Text));
      return new Traced { IsRoot = !hasAnyAncestor, Intrinsic = hasRealOwnError || !hasAnyAncestor, Upstream = upstream.ToList() };
    }

    /// <summary>Grasshopper's data-starvation messages: how a component reports it was starved by an upstream fault (downstream propagation), as opposed to a genuine own error (a conversion / type / compute
    /// failure). Kept narrow on purpose, so a real error is never misread as starvation.</summary>
    public static bool IsStarvationMessage(string text)
    {
      if (string.IsNullOrEmpty(text)) return false;
      var t = text.ToLowerInvariant();
      return t.Contains("collect data") || t.Contains("failed to connect");
    }

    /// <summary>All ids upstream of <paramref name="start"/> that are in <paramref name="among"/> (transitive), excluding the start itself.</summary>
    public static HashSet<int> AncestorsAmong(int start, HashSet<int> among, Func<int, IEnumerable<int>> feeding)
    {
      var hits = new HashSet<int>();
      var seen = new HashSet<int> { start };
      var stack = new Stack<int>();
      stack.Push(start);
      while (stack.Count > 0)
      {
        var id = stack.Pop();
        foreach (var from in feeding(id))
        {
          if (!seen.Add(from)) continue;
          if (among.Contains(from)) hits.Add(from);
          stack.Push(from);
        }
      }
      return hits;
    }

    /// <summary>The report's order: roots first, hard before silent (<paramref name="kindRank"/>: error, warning, silent), then by id (stable).</summary>
    public static List<T> Order<T>(IEnumerable<T> faults, Func<T, bool> isRoot, Func<T, int> kindRank, Func<T, int> id) =>
      faults.OrderByDescending(isRoot).ThenBy(kindRank).ThenBy(id).ToList();

    /// <summary>
    /// The chains the Diagnose result is drawn as: one per root, with the faults that trace to it (a fault on two roots under the first), and any fault no root reaches in a chain of its own, at the end.
    /// </summary>
    public static List<(T Root, List<T> Members)> Group<T>(IReadOnlyList<T> faults, Func<T, int> id, Func<T, bool> isRoot, Func<T, IReadOnlyCollection<int>> upstream)
    {
      var chains = new List<(T Root, List<T> Members)>();
      var assigned = new HashSet<int>();
      foreach (var root in faults.Where(isRoot))
      {
        var members = new List<T>();
        foreach (var f in faults.Where(f => !isRoot(f) && upstream(f).Contains(id(root))))
          if (assigned.Add(id(f)))           // first chain wins for a fault on multiple roots
            members.Add(f);
        chains.Add((root, members));
      }
      foreach (var f in faults.Where(f => !isRoot(f) && !assigned.Contains(id(f))))
        chains.Add((f, new List<T>()));
      return chains;
    }
  }
}
