using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;

namespace Dwight.Agent
{
  /// <summary>What the plugin read off Rhino's active document for the units read. Facts only.</summary>
  internal sealed class DocumentUnitsFact
  {
    /// <summary>The model unit system's name, as Rhino gives it ("Millimeters", "Meters", "CustomUnits", ...).</summary>
    public string Units { get; set; }
    public double AbsoluteTolerance { get; set; }
    public double AngleToleranceDegrees { get; set; }
  }

  /// <summary>
  /// U23: THE DOCUMENT'S UNITS AND TOLERANCE - the numbers every other geometry fact is measured in.
  ///
  /// WHY IT IS A READ AT ALL. The geometry reads send lengths, areas and volumes, and until this read the model
  /// was never told what unit they are in: a bounding box 1200 wide is a table in millimetres and a building in
  /// metres. And two of those reads - intersections and nearness - measure against the absolute tolerance, which
  /// the model could not see either.
  ///
  /// WHICH DOCUMENT, stated in the payload (the architect's ruling): Rhino's ACTIVE document, which is the one
  /// Grasshopper's own components take their tolerance from (the SDK documents GH_Component.DocumentTolerance as
  /// reading "the currently active Rhino document"). It never sends the document's name or path - a file name
  /// carries a client's name as easily as a path does.
  ///
  /// GH-free (ADR-091): a fact in, a tool result out.
  /// </summary>
  internal static class DocumentUnitsReport
  {
    /// <summary>
    /// THE ONE SENTENCE FOR "THERE IS NO RHINO DOCUMENT", shared by every read that needs one. The units read says
    /// it, and so do the geometry reads that measure against the tolerance - which, before this, silently used
    /// 0.001 instead: geometry facts reported against a tolerance nobody set, with nothing saying so (#208).
    /// Two reads refusing for one reason in two wordings would be two answers to one question.
    /// </summary>
    public const string NoActiveDocumentText =
      "Rhino has no active document, so there is no model unit system or tolerance to read.";

    public static ToolResult NoActiveDocument() => ToolResult.Fail(ToolErrors.NoDocument, NoActiveDocumentText);

    /// <summary>The document this read describes, in words - never its name or path.</summary>
    public const string DocumentText =
      "Rhino's active document - the one Grasshopper's components take their tolerance from.";

    /// <summary>
    /// THE TOLERANCE, OR A REFUSAL - never a default. The decision the geometry reads delegate to, GH-free so a
    /// test holds it: a reading that is missing or throws is the no-document refusal, whatever number would have
    /// been convenient. A read that cannot say what tolerance it used cannot say what it found.
    /// </summary>
    public static bool TryTolerance(Func<double?> read, out double tolerance, out ToolResult error)
    {
      tolerance = 0;
      error = null;
      double? value;
      try { value = read?.Invoke(); }
      catch { value = null; }

      if (value == null || double.IsNaN(value.Value) || value.Value <= 0)
      {
        error = NoActiveDocument();
        return false;
      }
      tolerance = value.Value;
      return true;
    }

    public static ToolResult Build(DocumentUnitsFact fact)
    {
      if (fact == null) return NoActiveDocument();

      var custom = string.Equals(fact.Units, "CustomUnits", StringComparison.Ordinal);
      var root = new JsonObject
      {
        ["document"] = DocumentText,
        ["units"] = fact.Units ?? "",
        ["absoluteTolerance"] = fact.AbsoluteTolerance,
        ["angleToleranceDegrees"] = fact.AngleToleranceDegrees,
        ["measures"] =
          "Every length the geometry reads report is in these units - areas in these units squared, volumes cubed. " +
          "The absolute tolerance is in the same units; intersection and nearness checks measure against it." +
          (custom ? " This document uses a custom unit: Dwight does not read its name or its size." : ""),
      };
      return ToolResult.Success(root);
    }
  }

  /// <summary>What the plugin read off ONE loaded Grasshopper library. Facts only.</summary>
  internal sealed class PluginFact
  {
    /// <summary>The name, through <see cref="Dwight.Data.PluginName.Of"/> - the same name get_component's
    /// plugin.library sends, so one plugin is never named two ways.</summary>
    public string Name { get; set; }
    public string Version { get; set; }

    /// <summary>A core Grasshopper library (by <c>GH_AssemblyInfo.IsCoreLibrary</c>, the codebase's one definition
    /// of "core"), rather than an installed plugin.</summary>
    public bool Core { get; set; }

    /// <summary>How many components it provides.</summary>
    public int Components { get; set; }

    /// <summary>How many objects on THIS canvas come from it, top level.</summary>
    public int UsedHere { get; set; }

    /// <summary>How many of <see cref="Components"/> are user objects rather than compiled components. Zero, and then not sent, for most.</summary>
    public int UserObjects { get; set; }
  }

  /// <summary>One library as <c>ComponentServer.Libraries</c> states it.</summary>
  internal sealed class LoadedLibrary
  {
    public Guid Id { get; set; }
    public string Name { get; set; }
    public string Version { get; set; }
    public bool Core { get; set; }
  }

  /// <summary>One entry of <c>ComponentServer.ObjectProxies</c>.</summary>
  internal sealed class ServerProxy
  {
    public Guid Guid { get; set; }
    public Guid Library { get; set; }
    public bool Obsolete { get; set; }
    public bool UserObject { get; set; }
    public string Category { get; set; }
  }

  /// <summary>One object on the canvas: which type it is, and the ribbon category it carries.</summary>
  internal sealed class CanvasObjectFact
  {
    public Guid ComponentGuid { get; set; }
    public string Category { get; set; }
  }

  /// <summary>
  /// WHAT THE COMPONENT SERVER KNOWS, AS PLUGINS. A plugin can be installed as user objects alone: Ladybug Tools' Honeybee and Dragonfly are
  /// hundreds of them, in folders Grasshopper reads, and belong to no library (each carries a library id of its own that matches none). The
  /// list used to walk the libraries only, so a model told a student that Honeybee was not installed when it was: a false statement at the
  /// first question, and an environmental toolset is the common case. So user objects are joined in here, under the ribbon category the
  /// user sees them in, and a category that shares its name with a plugin library is that plugin.
  ///
  /// GH-free (ADR-091), because the join is the part that can be wrong.
  /// </summary>
  internal static class PluginInventoryFacts
  {
    public static List<PluginFact> Join(IEnumerable<LoadedLibrary> libraries, IEnumerable<ServerProxy> proxies, IEnumerable<CanvasObjectFact> canvas)
    {
      var all = (proxies ?? Enumerable.Empty<ServerProxy>()).Where(p => p != null).ToList();
      var byGuid = new Dictionary<Guid, ServerProxy>();
      foreach (var p in all) byGuid[p.Guid] = p;

      // The user objects, by category. Named as first seen, compared without case.
      var groups = new Dictionary<string, PluginFact>(StringComparer.OrdinalIgnoreCase);
      foreach (var p in all.Where(x => x.UserObject && !x.Obsolete))
      {
        var name = Dwight.Data.PluginName.Of(p.Category?.Trim());
        if (!groups.TryGetValue(name, out var group)) groups[name] = group = new PluginFact { Name = name };
        group.Components++;
        group.UserObjects++;
      }

      var provided = new Dictionary<Guid, int>();
      foreach (var p in all.Where(x => !x.Obsolete && !x.UserObject))
        provided[p.Library] = (provided.TryGetValue(p.Library, out var n) ? n : 0) + 1;

      var used = new Dictionary<Guid, int>();
      foreach (var obj in (canvas ?? Enumerable.Empty<CanvasObjectFact>()).Where(o => o != null))
      {
        if (!byGuid.TryGetValue(obj.ComponentGuid, out var proxy)) continue;

        // A user object placed on a canvas is usually an instance of a script or a cluster, whose own type belongs to another library. What still
        // says where it came from is the category it was saved with, when that differs from its type's own.
        var category = obj.Category?.Trim();
        var saidOtherwise = !string.IsNullOrEmpty(category) &&
                            !string.Equals(category, proxy.Category?.Trim(), StringComparison.OrdinalIgnoreCase) &&
                            groups.ContainsKey(category);
        if (saidOtherwise) groups[category].UsedHere++;
        else if (proxy.UserObject && groups.TryGetValue(Dwight.Data.PluginName.Of(proxy.Category?.Trim()), out var own)) own.UsedHere++;
        else used[proxy.Library] = (used.TryGetValue(proxy.Library, out var n) ? n : 0) + 1;
      }

      var facts = new List<PluginFact>();
      foreach (var lib in (libraries ?? Enumerable.Empty<LoadedLibrary>()).Where(l => l != null))
      {
        var fact = new PluginFact
        {
          Name = Dwight.Data.PluginName.Of(lib.Name),
          Version = lib.Version,
          Core = lib.Core,
          Components = provided.TryGetValue(lib.Id, out var p) ? p : 0,
          UsedHere = used.TryGetValue(lib.Id, out var u) ? u : 0,
        };
        // Pufferfish is a library and some user objects under one name: one plugin, never two rows. A core library is left alone, so a user's
        // own object filed under a core tab stays their own row.
        if (!lib.Core && groups.TryGetValue(fact.Name, out var same))
        {
          fact.Components += same.Components;
          fact.UserObjects = same.UserObjects;
          fact.UsedHere += same.UsedHere;
          groups.Remove(fact.Name);
        }
        facts.Add(fact);
      }
      facts.AddRange(groups.Values);
      return facts;
    }
  }

  /// <summary>
  /// U23: THE INSTALLED PLUGINS AS AN INVENTORY - not one kb_lookup at a time.
  ///
  /// WHAT IT NEVER SENDS, and naming it is the point of a privacy row: a library's LOCATION (a path, which carries
  /// the Windows user name), its AUTHOR CONTACT (an email address), its ID (a GUID, which ADR-058 keeps out of every
  /// payload) and its DESCRIPTION. Only the name, the version and two counts leave.
  ///
  /// Grasshopper's own ~15 core libraries are ONE entry: they are the same for every user of this Rhino, and
  /// listing them would spend the bound on what the model already knows.
  ///
  /// GH-free (ADR-091).
  /// </summary>
  internal static class PluginInventoryReport
  {
    /// <summary>At most this many plugins are listed; the ones used in this definition come first.</summary>
    public const int PluginCap = 80;

    /// <summary>What the list cannot contain, stated in it - so an absence is not read as "not installed".</summary>
    public const string BoundText =
      "Only Grasshopper plugins that Grasshopper loaded are listed. Rhino plug-ins (.rhp) are not, and neither is a " +
      "plugin that failed to load. A plugin installed as user objects is listed by its ribbon category, with userObjects saying how many.";

    public static ToolResult Build(IReadOnlyList<PluginFact> libraries)
    {
      var all = libraries ?? Array.Empty<PluginFact>();
      var core = all.Where(l => l.Core).ToList();
      var plugins = all.Where(l => !l.Core)
        .OrderByDescending(l => l.UsedHere)
        .ThenBy(l => l.Name ?? "", StringComparer.OrdinalIgnoreCase)
        .ThenBy(l => l.Version ?? "", StringComparer.Ordinal)
        .ToList();

      var root = new JsonObject
      {
        ["bound"] = BoundText,
        ["installed"] = plugins.Count,
        ["core"] = new JsonObject
        {
          ["libraries"] = core.Count,
          ["components"] = core.Sum(l => l.Components),
          ["usedHere"] = core.Sum(l => l.UsedHere),
        },
      };

      var listed = new JsonArray();
      foreach (var p in plugins.Take(PluginCap))
      {
        var o = new JsonObject { ["name"] = p.Name ?? Dwight.Data.PluginName.Of(null) };
        if (!string.IsNullOrWhiteSpace(p.Version)) o["version"] = p.Version.Trim();
        o["components"] = p.Components;
        o["usedHere"] = p.UsedHere;
        if (p.UserObjects > 0) o["userObjects"] = p.UserObjects;
        listed.Add(o);
      }
      root["plugins"] = listed;

      var hidden = plugins.Skip(PluginCap).ToList();
      if (hidden.Count > 0)
      {
        var usedHidden = hidden.Count(p => p.UsedHere > 0);
        root["omitted"] =
          hidden.Count + " more plugin" + (hidden.Count == 1 ? " is" : "s are") + " installed and not listed" +
          (usedHidden > 0
            ? " (" + usedHidden.ToString(CultureInfo.InvariantCulture) + " of them used in this definition)"
            : ", none of them used in this definition") +
          ". kb_lookup answers for a plugin component by name.";
      }

      return ToolResult.Success(root);
    }
  }
}
