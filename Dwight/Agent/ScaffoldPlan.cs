using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;

namespace Dwight.Agent
{
  /// <summary>One region of a scaffold map: the model's judgement, plus everything the plugin computed
  /// about where it goes and what it is called.</summary>
  internal sealed class ScaffoldRegion
  {
    // ---- the MODEL's half: semantic judgement, and nothing else (ADR-056) ----
    /// <summary>The model's own label for this region, used only to wire <see cref="DependsOn"/>. It never
    /// reaches the canvas - the ADDRESS is the plugin's.</summary>
    public string Ref;
    public string Name;
    public string Purpose;
    public List<string> Pseudo = new List<string>();
    public List<string> DependsOn = new List<string>();

    /// <summary>
    /// What this region TAKES and GIVES, declared in words (#240/C1).
    ///
    /// These are DECLARED boundaries, not real params: nothing is wired at scaffold time, and real
    /// boundary params arrive only when a region is built. Their job is legibility - and the map needs it
    /// precisely because the map is anchored CLEAR of the existing definition, so a region attached to
    /// component 42 can sit a long way from component 42 and the relationship it describes is invisible
    /// on screen. Naming it in words is the right instrument; moving geometry to imply it would cost the
    /// purely-additive guarantee (#221) for something a sentence says better.
    /// </summary>
    public List<string> Inputs = new List<string>();
    public List<string> Outputs = new List<string>();
    /// <summary>Existing components this region attaches to. Grounded through the registry before it is
    /// believed; it nudges geometry and nothing else.</summary>
    public List<int> AnchorIds = new List<int>();

    // ---- the PLUGIN's half ----
    /// <summary>The ADDRESS - A1, A2, B1 ... - assigned from the dependency layering, never by the model.
    /// It is what the user says out loud ("build A1 through A3"), so it must mean position in the flow.</summary>
    public string Key;
    public int Layer;
    /// <summary>The box this region needs, computed from how much pseudo-code it carries.</summary>
    public double Width, Height;

    /// <summary>What the canvas group is called, and therefore how a region is found again with F3 - or by
    /// WP-4, which addresses a region by an object that exists rather than by a record that might not
    /// (#221b).</summary>
    public string GroupName => Key + " - " + (Name ?? "");

    /// <summary>The HEADER scribble: the address and the name. Load-bearing and unbounded by the body's
    /// rules (#238) - it is the navigation layer (scribbles do not fade at zoom-out) and it is how WP-4
    /// finds a region by an object that exists.</summary>
    public string HeaderText => GroupName;

    /// <summary>
    /// The BODY scribble: what this region is FOR, and how. BOUNDED, and it says so (#238).
    ///
    /// The full pseudo-code stays in <see cref="Pseudo"/> and therefore in the stored map; only the
    /// RENDERED lines are capped, because a region box sized by a thirty-line body dominates the canvas it
    /// exists to orient. That is the bounded-payload rule (#209/#134) applied to a canvas artifact, where
    /// the reader is a person rather than a model - so the elision is stated on the scribble itself rather
    /// than in a payload field nobody is looking at.
    /// </summary>
    public string BodyText => string.Join("\n", BodyLines);

    /// <summary>The body scribble's lines, in reading order: what it is for, what it takes and gives, then
    /// how. One list, so what is drawn and what the geometry is sized from cannot diverge.</summary>
    public IReadOnlyList<string> BodyLines
    {
      get
      {
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(Purpose)) lines.Add(Purpose);

        var takes = Join(Inputs);
        if (takes != null) lines.Add("takes: " + takes);
        var gives = Join(Outputs);
        if (gives != null) lines.Add("gives: " + gives);

        lines.AddRange(Pseudo.Take(ScaffoldPlanParser.RenderedBodyLines));
        var hidden = Math.Max(0, Pseudo.Count - ScaffoldPlanParser.RenderedBodyLines);
        if (hidden > 0) lines.Add("... " + hidden + " more line" + (hidden == 1 ? "" : "s") + " in the plan");
        return lines;
      }
    }

    /// <summary>How many lines the box actually draws - what the geometry is sized from.</summary>
    public int RenderedLineCount => BodyLines.Count;

    private static string Join(List<string> items)
    {
      if (items == null || items.Count == 0) return null;
      var joined = string.Join(", ", items);
      return joined.Length > ScaffoldPlanParser.MaxLineChars
        ? joined.Substring(0, ScaffoldPlanParser.MaxLineChars - 1) + "…"
        : joined;
    }
  }

  internal sealed class ScaffoldPlan
  {
    public List<ScaffoldRegion> Regions = new List<ScaffoldRegion>();
    /// <summary>The dependency edges by <see cref="ScaffoldRegion.Ref"/>, ACYCLIC - cycles are broken
    /// before layering.</summary>
    public List<(string From, string To)> Edges = new List<(string From, string To)>();
    /// <summary>Edges reversed to break a cycle, reported so the map's ordering is explainable rather than
    /// mysterious. Display ordering only, exactly as the cluster-DAG law says (ADR-084/#221).</summary>
    public List<string> ReversedEdges = new List<string>();
    /// <summary>Anchors that pointed at something no longer on the canvas: dropped and reported, never
    /// silently believed (ADR-058 groundedness).</summary>
    public List<string> DroppedAnchors = new List<string>();
  }

  /// <summary>
  /// THE SCAFFOLD PLAN'S VALIDATOR AND ADDRESSING (Phase-8 WP-3 Stage C - the #219 substrate, GH-free).
  ///
  /// THE SPLIT THIS FILE ENFORCES (ADR-056). The model supplies REGION DECOMPOSITION and LABELS - what the
  /// regions are, what each is for, what depends on what. The plugin computes everything else: the
  /// ADDRESSES (A1..Bn, from the dependency layering), the box each region needs, and - in the shim above -
  /// every coordinate. A model that could name its own addresses could make "A1" mean whatever it liked
  /// between one message and the next; the address means POSITION IN THE FLOW because the plugin derives
  /// it from the flow.
  ///
  /// CYCLES ARE NOT AN ERROR. A model-proposed dependency set can be cyclic, and so can a real definition's
  /// cluster graph - the existing law applies unchanged (ADR-084): reverse the closing edges, deterministic
  /// and display-ordering-only, and REPORT what was reversed so the reading order is explainable.
  /// </summary>
  internal static class ScaffoldPlanParser
  {
    /// <summary>Regions per map. A scaffold is a map a human reads and then works through by address;
    /// past a couple of dozen regions it stops being a map. Beyond this the model is asked to decompose
    /// more coarsely rather than being silently truncated.</summary>
    public const int MaxRegions = 24;

    /// <summary>Pseudo-code lines KEPT per region - a payload bound, generous, because these travel into
    /// the stored map and WP-4 builds from them.</summary>
    public const int MaxPseudoLines = 40;

    /// <summary>Pseudo-code lines DRAWN on the region's body scribble (#238). Smaller than what is kept,
    /// on purpose: a box sized by a thirty-line body dominates the canvas it exists to orient. What is not
    /// drawn is stated on the scribble, never silently dropped.</summary>
    public const int RenderedBodyLines = 10;

    /// <summary>Declared inputs / outputs kept per region (#240/C1). A boundary a reader can hold in their
    /// head; past this the region is really two regions.</summary>
    public const int MaxBoundaryItems = 6;

    /// <summary>The width one line may claim.</summary>
    public const int MaxLineChars = 90;

    // Box geometry, in canvas units. A region's box is sized from what it carries so a dense region gets
    // room and a one-liner does not claim a field.
    private const double MinWidth = 260, MaxWidth = 620, CharWidth = 7.0;
    private const double BaseHeight = 90, LineHeight = 22, MaxHeight = 460;

    /// <summary>No scaffold addresses are spoken for - the empty canvas. Named rather than a bare empty array so a
    /// call site SAYS which case it stands in. There is deliberately NO overload defaulting to this: a caller that
    /// forgets the in-use set would silently be the #720 defect again, and a default is how that forgetting hides.</summary>
    public static readonly IReadOnlyCollection<string> EmptyCanvas = new string[0];

    /// <summary>The last layer the single-letter address space reaches. See <see cref="AssignAddresses"/>.</summary>
    private const int LastLetterLayer = 25;   // 'Z'

    public static bool TryParse(JsonObject args, Func<int, IdState> idState, IReadOnlyCollection<string> inUseAddresses,
      out ScaffoldPlan plan, out ToolResult error)
    {
      plan = null; error = null;

      var rawRegions = args?["regions"] as JsonArray;
      if (rawRegions == null || rawRegions.Count == 0)
      {
        error = ToolResult.Fail(ToolErrors.InvalidArgs, "Requires \"regions\" - at least one region to lay down.");
        return false;
      }
      if (rawRegions.Count > MaxRegions)
      {
        error = ToolResult.Fail(ToolErrors.InvalidArgs,
          "That is " + rawRegions.Count + " regions; a scaffold map holds at most " + MaxRegions +
          ". Group the work more coarsely - a map somebody has to scroll is not a map.");
        return false;
      }

      var built = new ScaffoldPlan();
      var seenRefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

      foreach (var node in rawRegions)
      {
        var o = node as JsonObject;
        if (o == null)
        {
          error = ToolResult.Fail(ToolErrors.InvalidArgs, "Each region must be an object.");
          return false;
        }

        var region = new ScaffoldRegion
        {
          Ref = Str(o, "ref"),
          Name = Str(o, "name"),
          Purpose = Str(o, "purpose"),
        };

        if (string.IsNullOrWhiteSpace(region.Ref))
        { error = ToolResult.Fail(ToolErrors.InvalidArgs, "Each region needs a \"ref\" - your own label for it, so other regions can depend on it."); return false; }
        if (!seenRefs.Add(region.Ref))
        { error = ToolResult.Fail(ToolErrors.InvalidArgs, "Two regions share the ref \"" + region.Ref + "\"; each needs its own."); return false; }
        if (string.IsNullOrWhiteSpace(region.Name))
        { error = ToolResult.Fail(ToolErrors.InvalidArgs, "Region \"" + region.Ref + "\" needs a \"name\" - it becomes the group's label on the canvas."); return false; }
        if (string.IsNullOrWhiteSpace(region.Purpose))
        { error = ToolResult.Fail(ToolErrors.InvalidArgs, "Region \"" + region.Ref + "\" needs a \"purpose\" - one line saying what it should do. The map is the point; a box with no statement of intent is not a map."); return false; }

        region.Pseudo = Lines(o, "pseudo", MaxPseudoLines);
        region.Inputs = Lines(o, "inputs", MaxBoundaryItems);
        region.Outputs = Lines(o, "outputs", MaxBoundaryItems);
        region.DependsOn = (o["dependsOn"] as JsonArray)?.Select(x => SafeString(x)).Where(s => !string.IsNullOrWhiteSpace(s)).ToList()
                           ?? new List<string>();

        built.Regions.Add(region);
      }

      // ---- anchors: grounded before they are believed ------------------------------------------------
      foreach (var region in built.Regions)
      {
        var raw = FindArray(rawRegions, region.Ref, "anchorIds");
        foreach (var idNode in raw)
        {
          int id;
          try { id = idNode.GetValue<int>(); } catch { continue; }

          var state = idState?.Invoke(id) ?? IdState.Unknown;
          if (state == IdState.Unknown)
          {
            // An id that was never issued is INVENTED, and inventing ids is the class the registry exists
            // to catch - refused, never quietly ignored (#208: exactly, or refuse).
            error = ToolResult.Fail(ToolErrors.UnknownId,
              "Region \"" + region.Ref + "\" anchors to id " + id + ", which is not a component this " +
              "session has seen. Read the canvas first and anchor to an id a tool returned.");
            return false;
          }
          if (state == IdState.Retired)
          {
            // It existed and is gone: the canvas moved under the model, which is not the model's mistake.
            // Dropped and reported rather than refused - the map still lands, minus one adjacency hint.
            built.DroppedAnchors.Add(region.Ref + " -> " + id);
            continue;
          }
          region.AnchorIds.Add(id);
        }
      }

      // ---- dependencies: every ref must resolve ------------------------------------------------------
      var byRef = built.Regions.ToDictionary(r => r.Ref, StringComparer.OrdinalIgnoreCase);
      foreach (var region in built.Regions)
        foreach (var dep in region.DependsOn)
        {
          if (byRef.ContainsKey(dep)) continue;
          error = ToolResult.Fail(ToolErrors.InvalidArgs,
            "Region \"" + region.Ref + "\" depends on \"" + dep + "\", which is not one of the regions.",
            new JsonObject
            {
              ["asked"] = dep,
              ["available"] = new JsonArray(built.Regions.Select(r => (JsonNode)r.Ref).ToArray()),
            });
          return false;
        }

      if (!AssignAddresses(built, inUseAddresses, out error)) return false;

      // #581: NO ADDRESSES IN THE PLAN'S OWN PROSE. Refused before anything is laid down, but after the layering, because what
      // counts as an address is what THIS plan could produce, and that is known only once the layers are.
      foreach (var region in built.Regions)
        if (!NoAddressIn(region, built, inUseAddresses, out error)) return false;

      foreach (var r in built.Regions) Size(r);

      plan = built;
      return true;
    }

    /// <summary>
    /// Break cycles, layer, and assign the addresses. Deterministic end to end: the edge order is sorted,
    /// so the same plan produces the same map every time - which is what lets a user say "A2" twice and
    /// mean the same thing.
    ///
    /// AND IT NUMBERS AROUND WHAT IS ALREADY SPOKEN FOR (#720). The layering knows only the plan, so a
    /// second map on a canvas that already carried A1..A3 minted A1..A3 again: two groups answering to one
    /// address. Everything downstream is aimed BY address - <c>GhScaffoldMap.TryFindGroup</c> refuses a
    /// duplicate as AMBIGUOUS (#208) rather than guessing - so the collision did not corrupt a build, it
    /// bricked one, and the user's way out was to delete a map by hand.
    ///
    /// CONTINUE, DO NOT REFUSE. A fresh number is always available, so refusing here would be a refusal
    /// with an obvious answer behind it - and a refusal is only honest when there was nothing else to do
    /// (#613/#626). The number continues from the highest already in use FOR THAT LETTER; the letter still
    /// comes from the flow, because that is the whole meaning of an address.
    ///
    /// <paramref name="inUse"/> is an ARGUMENT, not a canvas read, so this stays GH-free and testable
    /// (ADR-091) - and so the model still cannot reach an address: the caller computes the set from the
    /// groups plus the stored map, and the plugin mints every key.
    /// </summary>
    public static bool AssignAddresses(ScaffoldPlan plan, IReadOnlyCollection<string> inUse, out ToolResult error)
    {
      error = null;
      var order = plan.Regions.Select((r, i) => (r.Ref, i)).ToDictionary(t => t.Ref, t => t.i, StringComparer.OrdinalIgnoreCase);

      var edges = plan.Regions
        .SelectMany(r => r.DependsOn.Select(d => (From: d, To: r.Ref)))   // "r depends on d" => d flows into r
        .Distinct()
        .OrderBy(e => order[e.From]).ThenBy(e => order[e.To])
        .ToList();

      // Cycle-break by reversing the edge that CLOSES a cycle, found by a deterministic depth-first walk.
      // Display ordering only - nothing about the plan's meaning changes, and what was reversed is reported.
      var kept = new List<(string From, string To)>();
      foreach (var e in edges)
      {
        if (Reaches(kept, e.To, e.From))
        {
          plan.ReversedEdges.Add(e.From + " -> " + e.To);
          kept.Add((e.To, e.From));
        }
        else kept.Add(e);
      }
      plan.Edges = kept;

      // Longest-path layering over the acyclic set.
      var layer = plan.Regions.ToDictionary(r => r.Ref, _ => 0, StringComparer.OrdinalIgnoreCase);
      for (var pass = 0; pass < plan.Regions.Count; pass++)
      {
        var moved = false;
        foreach (var e in plan.Edges)
          if (layer[e.To] < layer[e.From] + 1) { layer[e.To] = layer[e.From] + 1; moved = true; }
        if (!moved) break;
      }

      foreach (var r in plan.Regions) r.Layer = layer[r.Ref];

      // The highest number already spoken for, PER LETTER. Anything unparseable is skipped rather than
      // thrown on: the set is joined from group nicknames and a stored record, and a stray string in it
      // must not be able to stop a map being laid down.
      var highest = new Dictionary<string, int>(StringComparer.Ordinal);
      foreach (var address in inUse ?? EmptyCanvas)
      {
        if (!ScaffoldAddress.TryParse(address, out var letters, out var number)) continue;
        if (!highest.TryGetValue(letters, out var top) || number > top) highest[letters] = number;
      }

      // A = the first layer of the flow, then B, C ... and within a layer the model's declaration order,
      // which is the only ordering it actually expressed. The NUMBER starts past whatever that letter
      // already carries, so a second map continues the namespace instead of arguing with it.
      foreach (var group in plan.Regions.GroupBy(r => r.Layer).OrderBy(g => g.Key))
      {
        if (group.Key > LastLetterLayer)
        {
          // PAST Z. Letter() would cheerfully return "AA", and "AA1" is not an address anyone says out
          // loud - so the single-letter space is the space, and running out of it is a refusal that names
          // what is using it rather than inventing a second namespace nobody agreed to.
          error = ToolResult.Fail(ToolErrors.PreconditionRefused,
            "This plan is " + (group.Key + 1) + " layers deep, and scaffold addresses run A to Z. Break the work " +
            "into fewer stages, or lay it down as a second map once the first is built.",
            new JsonObject
            {
              ["layers"] = group.Key + 1,
              ["lastAddressable"] = Letter(LastLetterLayer),
              ["inUse"] = new JsonArray(InUseOrder(inUse).Select(a => (JsonNode)a).ToArray()),
            });
          return false;
        }

        var letter = Letter(group.Key);
        var n = (highest.TryGetValue(letter, out var taken) ? taken : 0) + 1;
        foreach (var r in group.OrderBy(x => order[x.Ref]))
          r.Key = letter + n++;
      }
      return true;
    }

    /// <summary>The in-use addresses as a reader would list them - parseable ones in address order, the rest
    /// after, deduplicated. A refusal that names what is in use has to name it legibly.
    ///
    /// <c>IsAddress</c> HERE MEANS PLAN ADDRESS, AND MUST KEEP MEANING THAT (U18). This orders the set a
    /// #720 refusal names, and the thing being run out of is the MAP's letter space. A spot occupies no
    /// letter and no layer, so it can never be what exhausted A..Z; sorting it first here would put an
    /// irrelevant name at the head of the one list that exists to explain a refusal about layers.</summary>
    private static List<string> InUseOrder(IReadOnlyCollection<string> inUse) =>
      (inUse ?? EmptyCanvas)
        .Where(a => !string.IsNullOrWhiteSpace(a))
        .Distinct(StringComparer.Ordinal)
        .OrderBy(a => ScaffoldAddress.IsAddress(a) ? 0 : 1)
        .ThenBy(a => ScaffoldAddress.SortKey(a).Letters, StringComparer.Ordinal)
        .ThenBy(a => ScaffoldAddress.SortKey(a).Number)
        .ToList();

    private static bool Reaches(List<(string From, string To)> edges, string from, string to)
    {
      var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      var stack = new Stack<string>();
      stack.Push(from);
      while (stack.Count > 0)
      {
        var at = stack.Pop();
        if (string.Equals(at, to, StringComparison.OrdinalIgnoreCase)) return true;
        if (!seen.Add(at)) continue;
        foreach (var e in edges)
          if (string.Equals(e.From, at, StringComparison.OrdinalIgnoreCase)) stack.Push(e.To);
      }
      return false;
    }

    /// <summary>A..Z, then AA, AB ... so a deep flow still addresses cleanly.</summary>
    public static string Letter(int layer)
    {
      if (layer < 0) layer = 0;
      var s = "";
      layer++;
      while (layer > 0)
      {
        var rem = (layer - 1) % 26;
        s = (char)('A' + rem) + s;
        layer = (layer - 1) / 26;
      }
      return s;
    }

    /// <summary>The box is sized from what is DRAWN, not from what is stored - so the geometry matches the
    /// scribble the user actually sees.</summary>
    private static void Size(ScaffoldRegion r)
    {
      // Sized from the lines actually DRAWN - one list feeds both the scribble and the geometry, so the
      // box can never describe a body other than the one on it.
      var drawn = r.BodyLines;
      var longest = drawn.Count == 0 ? 0 : drawn.Max(l => l.Length);
      r.Width = Math.Min(MaxWidth, Math.Max(MinWidth, longest * CharWidth));
      r.Height = Math.Min(MaxHeight, BaseHeight + drawn.Count * LineHeight);
    }

    /// <summary>
    /// Refuse a region whose prose names a region ADDRESS (#581).
    ///
    /// THE DEFECT. Addresses are assigned by the plugin from the dependency layering, AFTER this parse,
    /// but the model's free text is stored exactly as written. So a plan whose B1 and C1 said "from UV
    /// Subdivision (A2)" and "the grid from A3" - in a plan that assigned A1, B1, C1, D1, E1 and F1 - left
    /// prose on the canvas contradicting the real map, and it went on contradicting it for as long as the
    /// map stood.
    ///
    /// WHY A REFUSAL AND NOT A REMAP. When the model writes "A2", no address exists yet. The mapping the
    /// plugin holds is ref-to-address, and "A2" is not a ref - it is a guess, so there is nothing to map it
    /// FROM. A remap would only look right when the guess happened to coincide, and it would rewrite a wrong
    /// guess into a confident falsehood.
    ///
    /// WHY THE PATTERN AND NOT THE ASSIGNED SET. The founding case referenced A2 to A5 in a plan that
    /// assigned none of them, so checking against the addresses this plan assigns would have missed the
    /// exact instance that started this. The rule is "refer to regions by name, never by address", and any
    /// address-shaped token breaks it whether or not it happens to match.
    ///
    /// THE PATTERN IS <see cref="ScaffoldAddress.IsAddress"/>, not a second regex: one or more UPPERCASE
    /// letters A-Z followed by one or more digits and nothing else, checked on whole tokens split at every
    /// character that is not a letter or digit. One definition of what an address is, so the check cannot
    /// drift from the addresses the map actually uses.
    ///
    /// NARROWED (Brief CK): THE PATTERN FINDS CANDIDATES, <see cref="CouldBeThisPlans"/> DECIDES. "G21", "M104" and "an A4 sheet" were
    /// refused too, costing a turn and protecting nothing, so a token now counts only if the canvas already has it or this plan's own
    /// layers and region counts could produce it. The founding case above (A2 to A5 in a plan with one region per layer) is therefore
    /// prose now, a loss accepted on purpose: a guess the plan could not have made is not one that can match the map.
    /// Lowercase tokens ("step2") are not addresses.
    /// </summary>
    private static bool NoAddressIn(ScaffoldRegion region, ScaffoldPlan plan, IReadOnlyCollection<string> inUse, out ToolResult error)
    {
      error = null;

      var fields = new List<KeyValuePair<string, string>>
      {
        new KeyValuePair<string, string>("name", region.Name),
        new KeyValuePair<string, string>("purpose", region.Purpose),
      };
      foreach (var line in region.Pseudo ?? new List<string>()) fields.Add(new KeyValuePair<string, string>("pseudo", line));
      foreach (var line in region.Inputs ?? new List<string>()) fields.Add(new KeyValuePair<string, string>("inputs", line));
      foreach (var line in region.Outputs ?? new List<string>()) fields.Add(new KeyValuePair<string, string>("outputs", line));

      foreach (var field in fields)
      {
        var token = FirstAddressToken(field.Value, t => CouldBeThisPlans(t, plan, inUse));
        if (token == null) continue;

        error = ToolResult.Fail(ToolErrors.InvalidArgs,
          "Region \"" + region.Ref + "\" mentions \"" + token + "\" in its " + field.Key + ". That looks like a " +
          "region ADDRESS, and addresses are assigned by Dwight only after the plan is accepted - so an " +
          "address written into the plan is a guess that will not match the map. Refer to the other region " +
          "by its NAME instead.",
          new JsonObject { ["region"] = region.Ref, ["field"] = field.Key, ["token"] = token });
        return false;
      }
      return true;
    }

    /// <summary>
    /// Whether an address-shaped token is one this plan, on this canvas, could actually produce or already has: it is in the map's
    /// in-use addresses, or its letter is one the plan's layers are assigned and its number is no greater than the regions in that
    /// layer. Everything else - "G21", "M104", "A4 sheet" where layer A holds fewer than four regions - is prose, because a guard
    /// that refuses a G-code word protects nothing and costs the model a turn. Single letters only: "AA3" is not an address the
    /// plan can mint (<see cref="LastLetterLayer"/>), so it is prose unless the canvas already carries it.
    /// </summary>
    internal static bool CouldBeThisPlans(string token, ScaffoldPlan plan, IReadOnlyCollection<string> inUse)
    {
      if (inUse != null && inUse.Contains(token, StringComparer.Ordinal)) return true;
      if (!ScaffoldAddress.TryParse(token, out var letters, out var number) || letters.Length != 1) return false;

      // The letter bound is carried by the count: layering leaves no layer empty, so a letter past the plan's last layer has no regions and
      // no number is <= 0. The letter is still what picks the layer, so "G21" in a plan with no layer G is prose.
      var layer = letters[0] - 'A';
      return number <= plan.Regions.Count(r => r.Layer == layer);
    }

    /// <summary>The first whole token in <paramref name="text"/> that is a region address, or null.</summary>
    internal static string FirstAddressToken(string text) => FirstAddressToken(text, null);

    /// <summary>The first whole address-shaped token that <paramref name="accept"/> also accepts (null accepts all).</summary>
    internal static string FirstAddressToken(string text, Func<string, bool> accept)
    {
      if (string.IsNullOrEmpty(text)) return null;

      var token = new StringBuilder();
      foreach (var ch in text + " ")
      {
        if (char.IsLetterOrDigit(ch)) { token.Append(ch); continue; }
        if (token.Length > 0 && ScaffoldAddress.IsAddress(token.ToString()) && (accept == null || accept(token.ToString()))) return token.ToString();
        token.Clear();
      }
      return null;
    }

    private static List<string> Lines(JsonObject o, string field, int cap)
    {
      var arr = o[field] as JsonArray;
      if (arr == null) return new List<string>();
      return arr.Select(x => SafeString(x))
        .Where(s => !string.IsNullOrWhiteSpace(s))
        .Select(s => s.Length > MaxLineChars ? s.Substring(0, MaxLineChars) : s)
        .Take(cap)
        .ToList();
    }

    private static IEnumerable<JsonNode> FindArray(JsonArray regions, string refName, string field)
    {
      foreach (var n in regions)
      {
        var o = n as JsonObject;
        if (o == null || !string.Equals(Str(o, "ref"), refName, StringComparison.OrdinalIgnoreCase)) continue;
        return (o[field] as JsonArray)?.Where(x => x != null) ?? Enumerable.Empty<JsonNode>();
      }
      return Enumerable.Empty<JsonNode>();
    }

    private static string Str(JsonObject o, string field)
    {
      var node = o?[field];
      if (node == null) return null;
      try { return node.GetValue<string>()?.Trim(); } catch { return null; }
    }

    private static string SafeString(JsonNode n)
    {
      if (n == null) return null;
      try { return n.GetValue<string>()?.Trim(); } catch { return null; }
    }
  }
}
