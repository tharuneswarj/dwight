using System;
using System.Collections.Generic;
using System.Linq;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;
using Dwight.Components;
using Dwight.Data;
using Dwight.Labelling;
using Dwight.Segment;

namespace Dwight.Agent
{
  /// <summary>
  /// The Grasshopper adapter for the agent tool surface: projects the live document into a GH-free
  /// <see cref="AgentSnapshot"/> in REGISTRY-id space (#191). Everything Rhino-shaped lives here; every
  /// decision lives in <see cref="AgentToolCore"/>, which is why the surface is testable outside Rhino
  /// (ADR-091). GH-only, so not in the headless test set.
  ///
  /// THREE DISCIPLINES THIS FILE KEEPS:
  /// - UI THREAD. Reads live volatile data, so it runs on the UI thread and materialises every tree
  ///   signature before anything can go off-thread (the WP-4.4 guardrail).
  /// - REGISTRY IDS ONLY. <see cref="DwightGraph"/>'s pivot-ordered ordinals never leave this file. The
  ///   substitution happens once, here, so no projection downstream can leak an unstable id.
  /// - ZERO-TOKEN, SOLVE-FREE (#190). Cluster labels are read from <see cref="LabelCache"/> or come back
  ///   generic; nothing here calls a model or triggers a solution.
  /// </summary>
  internal static class GhAgent
  {
    /// <summary>
    /// Build one snapshot. <paramref name="includeClusters"/> gates the SEGMENTER, which is the
    /// expensive half (the RPST pass): only <c>get_overview</c> and <c>get_region</c> need the
    /// partition, so a plain <c>get_component</c> never pays for it.
    /// </summary>
    public static AgentSnapshot Build(GH_Document doc, AgentIdRegistry ids, bool includeClusters, bool stamp = false)
    {
      var snap = new AgentSnapshot();
      if (doc == null || ids == null) return snap;

      var graph = DwightGraph.Build(doc, Array.Empty<Guid>());   // whole canvas, nothing selected
      graph.MaterializeTreeSignatures();                          // UI thread, before any off-thread read

      // Phase-9 U1(c): each top-level cluster's internal document, ONE level, through the same graph
      // builder the verbs use. Read BEFORE the registry syncs, because Sync retires every guid it is not
      // handed - an internal object read afterwards would be issued an id and retired in the same breath.
      var internals = new List<(GraphNode owner, GH_Cluster cluster, ClusterDto dto, DwightGraph inner, GH_Document innerDoc)>();
      foreach (var n in graph.Nodes)
        if (FindObject(doc, n.Guid) is GH_Cluster cl)
          internals.Add((n, cl, ReadClusterDocument(cl, out var inner, out var innerDoc), inner, innerDoc));

      // The one substitution: live guids reconcile the registry, and every id below is a registry id.
      ids.Sync(graph.Nodes.Select(n => n.Guid)
        .Concat(internals.Where(c => c.inner != null).SelectMany(c => c.inner.Nodes.Take(DwightConfig.AgentClusterMaxNodes).Select(x => x.Guid))));

      var agentIdByGraphId = new Dictionary<int, int>();
      foreach (var n in graph.Nodes)
      {
        var agentId = ids.Issue(n.Guid);
        agentIdByGraphId[n.Id] = agentId;

        var (inputs, outputs) = PortsOf(doc, n.Guid);
        var node = new AgentNode
        {
          Id = agentId,
          Type = n.Type,
          Nick = n.Nick,
          Kind = n.Kind,
          Inputs = inputs,
          Outputs = outputs,
          OutCounts = n.OutCounts ?? Array.Empty<int>(),
          Notes = n.Notes,
        };
        foreach (var m in n.Msgs) node.Msgs.Add(new MsgDto { Level = m.Level, Text = m.Text });
        var live = FindObject(doc, n.Guid);
        ReadFacts(node, live);   // Phase-9 U1: what it holds
        if (stamp) node.Stamp = StampOf(live);   // Phase-9 U22: what a read saw
        snap.Nodes.Add(node);
      }

      foreach (var c in internals) AddInternals(snap, ids, agentIdByGraphId[c.owner.Id], c.dto, c.inner, c.innerDoc, stamp);
      snap.Nodes.Sort((a, b) => a.Id.CompareTo(b.Id));

      // U22: the revision of the canvas as the reads can see it - one token over every stamped node, so a move (which
      // no stamp carries) cannot raise it and a change to any component's content does.
      if (stamp) snap.ReadRevision = ids.ObserveCanvas(CanvasToken(snap));

      foreach (var e in graph.Edges)
      {
        if (!agentIdByGraphId.TryGetValue(e.FromNode, out var from)) continue;
        if (!agentIdByGraphId.TryGetValue(e.ToNode, out var to)) continue;
        snap.Edges.Add(new AgentEdgeDto
        {
          FromNode = from,
          FromParam = e.FromParam,
          ToNode = to,
          ToParam = e.ToParam,
          Tree = Sig(e.Tree),
        });
      }

      if (includeClusters) AddClusters(snap, graph, doc, agentIdByGraphId);
      return snap;
    }

    /// <summary>The flat cluster partition (ADR-085/087) plus cached labels. The segmenter input is a
    /// cheap UI-thread projection; the RPST pass itself is pure and runs here on the caller's thread -
    /// the same split the Reorganise pipeline uses.</summary>
    private static void AddClusters(AgentSnapshot snap, DwightGraph graph, GH_Document doc,
      Dictionary<int, int> agentIdByGraphId)
    {
      try
      {
        var graphIds = new HashSet<int>(graph.Nodes.Select(n => n.Id));
        if (graphIds.Count == 0) return;

        var macro = new Segmenter().Segment(GhSegment.BuildInput(graph, doc, graphIds)).Macro;
        if (macro == null || macro.Count == 0) return;

        // Cached labels only - naming a cluster is a paid call, and a read tool never spends (#190).
        // The hash is the same guid-keyed one the labeller writes under, so a hit here is a real hit.
        var hashes = GhLabel.ContentHashes(graph, macro);

        foreach (var grp in macro.GroupBy(kv => kv.Value).OrderBy(g => g.Key))
        {
          var cluster = new AgentCluster { Id = grp.Key };
          foreach (var kv in grp)
            if (agentIdByGraphId.TryGetValue(kv.Key, out var agentId)) cluster.Members.Add(agentId);
          cluster.Members.Sort();

          string label = null;
          if (hashes.TryGetValue(grp.Key, out var hash)) LabelCache.TryGet(hash, out label);
          // U29 (#918): the generic label is the labelling pass's own ("Region N"), never "Cluster N" - one definition.
          cluster.Label = string.IsNullOrWhiteSpace(label) ? RegionVocabulary.GenericLabel(grp.Key) : label;

          snap.Clusters.Add(cluster);
        }

        var clusterOf = new Dictionary<int, int>();
        foreach (var c in snap.Clusters) foreach (var m in c.Members) clusterOf[m] = c.Id;

        var flow = new HashSet<(int, int)>();
        foreach (var e in snap.Edges)
          if (clusterOf.TryGetValue(e.FromNode, out var cf) && clusterOf.TryGetValue(e.ToNode, out var ct) && cf != ct)
            flow.Add((cf, ct));
        snap.Flow = flow.OrderBy(f => f.Item1).ThenBy(f => f.Item2).ToList();
      }
      catch
      {
        // A segmentation failure degrades to "no regions", never a broken tool call (ADR-029).
        snap.Clusters.Clear();
        snap.Flow.Clear();
      }
    }

    /// <summary>Port NAMES for a node, read off the live object. The names are what the agent must use
    /// to address a wire, so they come from the same <c>ParamLabel</c> the graph and payload use - one
    /// definition, no drift.</summary>
    private static (string[] inputs, string[] outputs) PortsOf(GH_Document doc, Guid guid)
    {
      try
      {
        var obj = doc?.FindObject(guid, false);
        if (obj is IGH_Component c)
          return (c.Params.Input.Select(DwightGraph.ParamLabel).ToArray(),
                  c.Params.Output.Select(DwightGraph.ParamLabel).ToArray());
        if (obj is IGH_Param p)
        {
          var label = DwightGraph.ParamLabel(p);
          return (new[] { label }, new[] { label });
        }
      }
      catch { /* a port list is never worth failing the call over */ }
      return (Array.Empty<string>(), Array.Empty<string>());
    }

    private static IGH_DocumentObject FindObject(GH_Document doc, Guid guid)
    {
      try { return doc?.FindObject(guid, false); } catch { return null; }
    }

    // ---- Phase-9 U1(c): clusters, one level -----------------------------------------------------------

    /// <summary>
    /// Decide whether a cluster may be read, and read it. THE ORDER IS THE RULING (#392 (i), the
    /// architect): protection is checked FIRST against an ALLOW-LIST - only <c>Unprotected</c> is read; Protected
    /// and any level this build does not recognise are refused BY NAME - and <c>Document</c> is called only after
    /// the level passed. Never discover protection by attempting: a guard that learns by trying has already
    /// done the thing it exists to prevent. Member existence is a compile-time fact against Grasshopper 8.24
    /// (ProtectionLevel, Locked, Document(string)); the runtime behaviour of Document on an unprotected
    /// cluster is a gate row, and an unexpected result is reported with its reason, never guessed at.
    /// </summary>
    private static ClusterDto ReadClusterDocument(GH_Cluster cl, out DwightGraph inner, out GH_Document innerDoc)
    {
      inner = null;
      innerDoc = null;
      var dto = new ClusterDto();

      // Symmetric with the ProtectionLevel read below: a throw is RECORDED, never left as a silent false.
      try { dto.Locked = cl.Locked; }
      catch (Exception ex) { dto.Locked = null; dto.LockedUnknown = "Locked threw " + ex.GetType().Name + ": " + ex.Message; }

      GH_ClusterProtection level;
      try { level = cl.ProtectionLevel; }
      catch (Exception ex) { dto.Protected = true; dto.Level = "unknown (ProtectionLevel threw: " + ex.Message + ")"; return dto; }
      dto.Level = level.ToString();

      // THE ALLOW-LIST. One permissive branch, reachable only after every refusal.
      if (level != GH_ClusterProtection.Unprotected)
      {
        dto.Protected = true;
        return dto;
      }

      try { innerDoc = cl.Document(null); }
      catch (Exception ex) { dto.Unreadable = "Document(null) threw " + ex.GetType().Name + ": " + ex.Message; return dto; }
      if (innerDoc == null) { dto.Unreadable = "Document(null) returned null"; return dto; }

      try
      {
        inner = DwightGraph.Build(innerDoc, Array.Empty<Guid>());
        inner.MaterializeTreeSignatures();
      }
      catch (Exception ex) { inner = null; dto.Unreadable = "the internal document could not be projected: " + ex.Message; return dto; }

      dto.Count = inner.Nodes.Count;
      dto.Omitted = Math.Max(0, inner.Nodes.Count - DwightConfig.AgentClusterMaxNodes);
      return dto;
    }

    /// <summary>Project a readable cluster's internals into the snapshot, tagged with the cluster they sit
    /// inside, capped and counted. A nested cluster is listed, not opened - one level per read.</summary>
    private static void AddInternals(AgentSnapshot snap, AgentIdRegistry ids, int clusterAgentId, ClusterDto dto,
      DwightGraph inner, GH_Document innerDoc, bool stamp)
    {
      var owner = snap.NodeById(clusterAgentId);
      if (owner != null) owner.Cluster = dto;
      if (inner == null) return;

      var innerIdByGraphId = new Dictionary<int, int>();
      foreach (var n in inner.Nodes.Take(DwightConfig.AgentClusterMaxNodes))
      {
        var agentId = ids.Issue(n.Guid);
        innerIdByGraphId[n.Id] = agentId;
        var obj = FindObject(innerDoc, n.Guid);
        var (inputs, outputs) = PortsOf(innerDoc, n.Guid);
        var node = new AgentNode
        {
          Id = agentId, Type = n.Type, Nick = n.Nick, Kind = n.Kind, Inputs = inputs, Outputs = outputs,
          OutCounts = n.OutCounts ?? Array.Empty<int>(),
          Inside = clusterAgentId,
          Hook = obj is GH_ClusterInputHook ? "input" : obj is GH_ClusterOutputHook ? "output" : null,
        };
        foreach (var m in n.Msgs) node.Msgs.Add(new MsgDto { Level = m.Level, Text = m.Text });
        ReadFacts(node, obj);
        if (stamp) node.Stamp = StampOf(obj);
        if (obj is GH_Cluster)
          node.Cluster = new ClusterDto { Unreadable = "a cluster inside a cluster is listed, not opened - one level per read" };
        snap.Nodes.Add(node);
      }

      foreach (var e in inner.Edges)
      {
        if (!innerIdByGraphId.TryGetValue(e.FromNode, out var from)) continue;
        if (!innerIdByGraphId.TryGetValue(e.ToNode, out var to)) continue;
        snap.Edges.Add(new AgentEdgeDto { FromNode = from, FromParam = e.FromParam, ToNode = to, ToParam = e.ToParam, Tree = Sig(e.Tree) });
      }
    }

    /// <summary>
    /// The live object behind an agent node - at the top level, or inside the cluster it sits in. The
    /// cluster path re-applies the allow-list (the level may have changed since the snapshot), so an
    /// internal object is never reached through a cluster that would now refuse.
    /// </summary>
    public static IGH_DocumentObject FindLive(GH_Document doc, AgentIdRegistry ids, AgentNode node)
    {
      if (doc == null || ids == null || node == null) return null;
      if (!ids.TryGetGuid(node.Id, out var guid)) return null;
      if (!node.Inside.HasValue) return FindObject(doc, guid);

      if (!ids.TryGetGuid(node.Inside.Value, out var clusterGuid)) return null;
      if (!(FindObject(doc, clusterGuid) is GH_Cluster cl)) return null;
      try
      {
        if (cl.ProtectionLevel != GH_ClusterProtection.Unprotected) return null;
        var innerDoc = cl.Document(null);
        return innerDoc == null ? null : FindObject(innerDoc, guid);
      }
      catch { return null; }
    }

    // ---- Phase-9 U22: the staleness stamp -------------------------------------------------------------

    /// <summary>The content stamp of a live object, read by the fingerprint's own reader (position-free) plus the nickname
    /// and the whole held state. Null for a group or scribble, or an object that cannot be read - never a guessed stamp.</summary>
    internal static ComponentStamp StampOf(IGH_DocumentObject obj)
    {
      if (obj == null) return null;
      try
      {
        var fo = BehaviourFingerprint.ReadObject(obj);
        if (fo == null) return null;
        string state = null;
        try { state = StateToken.Of(GhBuildState.Read(obj)); } catch { }
        return ComponentStamp.Of(fo, obj.NickName, state, ScriptSourceReader.IsScriptComponent(obj));
      }
      catch { return null; }
    }

    private static string CanvasToken(AgentSnapshot snap) =>
      ComponentStamp.CanvasToken(snap.Nodes.Select(n => new KeyValuePair<int, ComponentStamp>(n.Id, n.Stamp)));

    /// <summary>
    /// The components the model has READ whose live content now differs from the reading. A component that is gone is
    /// "removed"; one inside a cluster that can no longer be opened is SKIPPED, not called removed - a refusal is a
    /// claim and must be true in every state that can reach it, and "the cluster is protected now" is not "the
    /// component is gone". UI thread, like every read in this file.
    /// </summary>
    internal static List<StaleRead> StaleReads(GH_Document doc, AgentIdRegistry ids)
    {
      var stale = new List<StaleRead>();
      if (doc == null || ids == null) return stale;

      foreach (var kv in ids.Reads())
      {
        if (!ids.TryGetGuid(kv.Key, out var guid)) continue;
        var rec = kv.Value;

        IGH_DocumentObject live;
        if (!rec.Inside.HasValue) live = FindObject(doc, guid);
        else
        {
          if (!ids.TryGetGuid(rec.Inside.Value, out var clusterGuid)) continue;
          if (!(FindObject(doc, clusterGuid) is GH_Cluster cl))
          {
            stale.Add(new StaleRead { Id = kv.Key, Removed = true });   // its cluster is gone, and so is it
            continue;
          }
          live = FindLive(doc, ids, new AgentNode { Id = kv.Key, Inside = rec.Inside });
          if (live == null) continue;   // the cluster cannot be opened now: we cannot tell, so we say nothing
        }

        if (live == null) { stale.Add(new StaleRead { Id = kv.Key, Removed = true }); continue; }

        var now = StampOf(live);
        if (now == null) continue;
        var changes = now.ChangesSince(rec.Stamp);
        if (changes.Count > 0) stale.Add(new StaleRead { Id = kv.Key, Changes = changes });
      }
      return stale;
    }

    /// <summary>The turn-start sentence about the components the model has read, or null when none has changed. Never
    /// throws: a fact that cannot be computed is not stated, and the turn proceeds.</summary>
    public static string StaleReadsFact(GH_Document doc, AgentIdRegistry ids)
    {
      try { return Dwight.Agent.StaleReadsFact.For(StaleReads(doc, ids)); }
      catch { return null; }
    }

    /// <summary>The whitelist's proxy as the GH-free type <see cref="QualifiedNames"/> reasons over. Null in, null out.</summary>
    internal static InstalledType Installed(ComponentInfo c) => c == null ? null : new InstalledType
    {
      Name = c.Name, Category = c.Category, SubCategory = c.SubCategory, Description = c.Description, Guid = c.ComponentGuid,
    };

    /// <summary>Every installed type, in the GH-free shape, in the whitelist's own order.</summary>
    internal static List<InstalledType> InstalledTypes() => ComponentWhitelist.All().Select(Installed).ToList();

    private static IReadOnlyList<ComponentInfo> _ambiguousSource;
    private static HashSet<string> _ambiguous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The bare names more than one installed type shares, computed once per whitelist enumeration (the
    /// whitelist re-enumerates on <c>Reset</c>, and the cache is keyed to the list instance so it follows).
    /// UI-thread only, like everything in this file.</summary>
    private static HashSet<string> AmbiguousInstalledNames()
    {
      var all = ComponentWhitelist.All();
      if (!ReferenceEquals(all, _ambiguousSource))
      {
        _ambiguous = QualifiedNames.AmbiguousNames(all.Select(Installed));
        _ambiguousSource = all;
      }
      return _ambiguous;
    }

    /// <summary>
    /// Phase-9 U1 (reads-unification, #382(9)): what the component HOLDS, read by the VERBS' OWN readers -
    /// <see cref="RuntimeType"/> (ADR-093's constant values), <see cref="ScriptSourceReader"/> (RR17 #36),
    /// <see cref="PersistentValues"/> (the internalised-data read Simplify's payload hints from) and the
    /// whitelist kb_lookup answers from. Built ON the verb path, never beside it: if a verb and the agent
    /// ever disagreed about a value, that would be a defect, and sharing the reader makes it unrepresentable.
    ///
    /// Each fact is read under its own guard, so one unreadable fact never costs the others (the startup-
    /// isolation rule, WP-1 #178, at a read). Every bound is applied HERE and reported in the DTO, so the
    /// projection can say what was cut (#134/#209).
    /// </summary>
    private static void ReadFacts(AgentNode node, IGH_DocumentObject obj)
    {
      if (obj == null) return;

      // U25 (#915): when this type's bare name belongs to more than one installed type, carry the form that names
      // THIS one - resolved by the live object's ComponentGuid against the whitelist, never by name.
      try
      {
        var ambiguous = AmbiguousInstalledNames();
        if (ambiguous.Count > 0 && ambiguous.Contains((node.Type ?? "").Trim()))
        {
          var info = ComponentWhitelist.Describe(new[] { obj.ComponentGuid }).FirstOrDefault();
          node.Qualified = QualifiedNames.QualifiedIfAmbiguous(Installed(info), ambiguous);   // null when the guid is not a listed proxy
        }
      }
      catch { node.Qualified = null; }

      try { node.Constant = RuntimeType.ConstantValue(obj); } catch { }

      try
      {
        switch (obj)
        {
          case GH_NumberSlider s:
            node.Value = new ValueDto
            {
              Kind = "slider",
              Value = s.CurrentValue,
              Min = s.Slider.Minimum,
              Max = s.Slider.Maximum,
              Rounding = s.Slider.Type.ToString().ToLowerInvariant(),
              Decimals = s.Slider.DecimalPlaces,
            };
            break;
          case GH_Panel p when p.SourceCount > 0:
          {
            // U31 (#926): a FED panel displays what flows in; its UserText is the placeholder. Read the solved
            // volatile data the way the U2 geometry reads do - never a solve - and bound it in PanelDisplay.
            var first = new List<string>();
            var total = 0;
            var paths = 0;
            Grasshopper.Kernel.Data.IGH_Structure data = null;
            try { data = p.VolatileData; } catch { }
            if (data != null)
            {
              try { total = data.DataCount; paths = data.PathCount; } catch { }
              foreach (var path in data.Paths)
              {
                if (first.Count >= PanelDisplay.MaxItems) break;
                System.Collections.IList branch = null;
                try { branch = data.get_Branch(path); } catch { }
                if (branch == null) continue;
                foreach (var item in branch)
                {
                  if (first.Count >= PanelDisplay.MaxItems) break;
                  string s;
                  try { s = item == null ? "<null>" : item.ToString(); } catch (Exception ex) { s = "<unreadable: " + ex.GetType().Name + ">"; }
                  first.Add(s);
                }
              }
            }
            node.Value = PanelDisplay.Fed(first, total, paths, PanelDisplay.MaxItems, DwightConfig.AgentPanelTextMaxChars);
            node.Constant = null;   // a fed panel is computed upstream, not a constant (RuntimeType would hand back the placeholder)
            break;
          }
          case GH_Panel p:
          {
            var text = (p.UserText ?? "").Replace("\r\n", "\n");
            var cut = text.Length > DwightConfig.AgentPanelTextMaxChars;
            node.Value = new ValueDto
            {
              Kind = "panel",
              Text = cut ? text.Substring(0, DwightConfig.AgentPanelTextMaxChars) : text,
              TextTruncated = cut,
              TextTotalChars = text.Length,
            };
            break;
          }
          case GH_BooleanToggle t:
            node.Value = new ValueDto { Kind = "toggle", On = t.Value };
            break;
          case GH_ValueList v:
            node.Value = new ValueDto
            {
              Kind = "valuelist",
              Selected = v.ListItems.Where(i => i.Selected).Select(i => i.Name ?? "")
                          .Take(DwightConfig.AgentValueListMaxItems).ToArray(),
              Options = v.ListItems.Count,
            };
            break;
          case GH_ColourSwatch c:
            node.Value = new ValueDto { Kind = "swatch", Text = "#" + c.SwatchColour.ToArgb().ToString("X8") };
            break;
        }
      }
      catch { node.Value = null; }

      try
      {
        if (ScriptSourceReader.IsScriptComponent(obj))
        {
          node.ScriptLanguage = SourceBound.LanguageOf(obj.GetType().FullName);
          if (obj is IGH_Component scriptComp)
            node.ScriptInputAccess = scriptComp.Params.Input
              .Select(p => new KeyValuePair<string, string>(DwightGraph.ParamLabel(p), p.Access == GH_ParamAccess.list ? PortAccess.List : p.Access == GH_ParamAccess.tree ? PortAccess.Tree : PortAccess.Item))
              .ToList();
          var raw = ScriptSourceReader.TryReadRaw(obj);
          node.Source = raw == null
            ? null   // unreadable: a first-class answer, reported as such
            : SourceBound.Trim(raw, node.ScriptLanguage, DwightConfig.AgentSourceMaxLines, DwightConfig.AgentSourceMaxChars);
        }
      }
      catch { node.Source = null; }

      // Internalised data on the inputs (or on the param itself). A value-source object reports its
      // editable value above instead - the same fact twice would read as two facts.
      if (node.Value == null)
      {
        try
        {
          var inputs = obj is IGH_Component comp ? comp.Params.Input.AsEnumerable()
                     : obj is IGH_Param prm ? new[] { prm }
                     : Enumerable.Empty<IGH_Param>();
          foreach (var ip in inputs)
            if (PersistentValues.TryDescribe(ip, out var count, out var paths, out var item))
              node.Persistent.Add(new PersistentDto
              {
                Param = DwightGraph.ParamLabel(ip), Count = count, Paths = paths, Item = item,
              });
        }
        catch { node.Persistent.Clear(); }
      }

      try
      {
        var lib = ScriptSourceReader.LibraryOf(obj);
        if (lib != null)
          // By the object's OWN ComponentGuid, never by name: Find(name) is null for a name several installed components
          // share (U25, PR #55 deferral 4), and a lookup by name could only ever describe a namesake.
          node.Plugin = new PluginDto
          {
            Library = lib,
            Does = ComponentWhitelist.Describe(new[] { obj.ComponentGuid }).FirstOrDefault()?.Description,
          };
      }
      catch { node.Plugin = null; }
    }

    private static TreeSigDto Sig(TreeSig s) => s == null ? null : new TreeSigDto
    {
      Paths = s.Paths,
      Depth = s.Depth,
      Items = s.Items ?? Array.Empty<int>(),
    };
  }
}
