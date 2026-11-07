using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace Dwight.Agent
{
  /// <summary>
  /// WHAT THE USER IS ACTUALLY APPROVING WHEN A SPOT CARD IS SHOWN (U18's carrier).
  ///
  /// ================================================================================================
  /// THE RECORD IS THE SPEC. THE CARD DOES NOT DESCRIBE THE BUILD - IT IS THE BUILD.
  ///
  /// A spot card must NAME EVERY COMPONENT, because a spot has no region name standing in for its
  /// contents: the components ARE the scope (#338). That means the plugin has to know them when the card
  /// is drawn, so the model nominates them at the ASK rather than during the approved turn - the
  /// set_value shape, where the change rides the card and the plugin applies it from the card's own
  /// record.
  ///
  /// AND THE DANGER IT REMOVES IS THE ONE NO GUARD WOULD HAVE CAUGHT. Had the card named one set while
  /// the approved turn built from a spec of the model's own, every guard would still have passed and the
  /// verdict would have been TRUE - preservation holds, completeness holds, the trio reports success -
  /// because the card would be the only thing that was wrong, and nothing in the system reads the card.
  /// So this is not checked for; it is made unrepresentable. The validated spec produced here IS what
  /// gets built, and a spot build takes no component list from the model at all (ADR-075's shape: a
  /// capability by construction, not an intent detected).
  /// ================================================================================================
  ///
  /// THE MODEL NOMINATES; THE PLUGIN NAMES. Every type is resolved against what is installed, every wire
  /// end against real ports, every declared state against the type's own grid - and the card's sentence
  /// is rendered from THAT, never from the strings the model sent. This is Simplify's externalize
  /// precedent: the model once wired a float to the wrong port and verification passed, and the answer
  /// was that the plugin computes what the plugin can compute (ADR-056).
  ///
  /// IT REUSES <see cref="BuildSpecParser"/> RATHER THAN PARSING ITS OWN (#248). What a component declaration
  /// IS has one definition, and it already refuses an uninstalled type WITH the installed near-matches,
  /// resolves ports by arity with the real port list on a mismatch, and refuses an off-grid slider value.
  /// A second parser here would be a second answer to the same question, and the two would drift.
  ///
  /// GH-free (ADR-091): every live read arrives as a delegate.
  /// </summary>
  /// <summary>
  /// THE ONE WAY A BUILD SPEC IS RESOLVED - for the build, and for the ask that must refuse what the build
  /// would refuse.
  ///
  /// #862 rules that a wrong wire refuses AT THE ASK, before a card exists and before the user has spent any
  /// attention. That means the ask has to answer "does this wire resolve?" - and the build already answers
  /// it. Two resolvers answering one question is the second-definition class (#248): they would agree today
  /// and drift the day someone changed one, and the drift would be silent because both would stay green. The
  /// worst version of it is quiet: pass null port resolvers at the ask and every wrong-wire refusal moves to
  /// the CLICK, breaking #862 while the whole suite still passes.
  ///
  /// So there is one bundle and ONE <see cref="Parse"/>. <c>GhBuildRegion.LiveResolvers</c> is the only
  /// factory for the live one, and both <c>GhBuildRegion.Run</c> and <see cref="SpotAsk.Validate"/> parse
  /// through it. "The ask and the build check with the same code" is then a fact about the call graph, not a
  /// promise about two call sites.
  ///
  /// GH-free: the delegates are the live reads, so a test can hand in fakes and exercise both paths.
  /// </summary>
  internal sealed class SpecResolvers
  {
    public Func<string, bool> TypeExists { get; set; }
    public Func<string, IReadOnlyList<string>> Nearest { get; set; }
    public Func<int, IdState> IdState { get; set; }
    public Func<string, PortShape> PortsOfType { get; set; }
    public Func<int, PortShape> PortsOfId { get; set; }

    /// <summary>
    /// The INSTALLED spelling of a type the parser accepted - "number slider" becomes "Number Slider".
    ///
    /// The parser only CHECKS that a type exists, through a normalised lookup; it never rewrites the name, so
    /// a validated spec still carries the model's own casing. That is harmless to the build (the lookup finds
    /// the same component either way) and it is wrong for the RECORD and the CARD, which must speak in the
    /// plugin's words (#862, ADR-056). Deliberately NOT used by <see cref="Parse"/>, so how a build resolves
    /// is unchanged; only what a spot's record and card SAY is canonicalised. Null leaves names as they are.
    /// </summary>
    public Func<string, string> CanonicalType { get; set; }

    /// <summary>
    /// U25 (#915): the QUALIFIED FORMS a bare type name could mean, when it is shared by more than one installed
    /// component - or null/empty when it names exactly one (or none). The parser refuses an ambiguous name WITH these
    /// forms, before <see cref="TypeExists"/> is even asked, so "Rectangle" is never silently the param. Null here
    /// means "not known", which is never a rule: the parse then behaves exactly as it did.
    /// </summary>
    public Func<string, IReadOnlyList<string>> AmbiguousForms { get; set; }

    /// <summary>Brief CM: the groups on the canvas a request can name. Used only by the ask, never by <see cref="Parse"/>. Null reads as none.</summary>
    public Func<IReadOnlyList<NamedGroup>> Groups { get; set; }

    /// <summary>Brief CM: what the user last wrote, so a place they named is not silently answered with a new one. Used only by the ask. Null reads as nothing.</summary>
    public Func<string> LastUserText { get; set; }

    /// <summary>Turn a spec's arguments into a validated spec, or refuse by name - the build's own rules,
    /// with the build's own resolvers.</summary>
    public bool Parse(JsonObject args, out BuildSpec spec, out ToolResult error) =>
      BuildSpecParser.TryParse(args, TypeExists, Nearest, IdState, PortsOfType, PortsOfId, AmbiguousForms, out spec, out error);
  }

  /// <summary>Which path a <c>kind=build</c> ask takes.</summary>
  internal enum BuildAskRoute
  {
    /// <summary>Into scaffold regions the model NAMED. Each must exist, or the ask refuses.</summary>
    Regions,
    /// <summary>A small placement with no map - a spot the plugin mints.</summary>
    Spot,
  }

  /// <summary>The routing decision, or the refusal that stands instead of one.</summary>
  internal sealed class BuildAskRouting
  {
    public BuildAskRoute Route { get; set; }
    public ToolResult Refusal { get; set; }
  }

  internal sealed class SpotAsk
  {
    /// <summary>
    /// WHICH PATH A BUILD ASK TAKES - decided on what the model NAMED, never on what the canvas has (U18).
    ///
    /// THE GUARD THIS EXISTS FOR. A model asking to build A1 when A1 is not on the canvas must be REFUSED,
    /// never quietly given a spot instead. Route on "no regions FOUND" and a typo becomes a placement somewhere
    /// else, with a verdict saying it succeeded - #338 (the approved scope is the scope) and #208 (resolve
    /// exactly or refuse) failing at once. So the route is a function of the NOMINATION: any region named at
    /// all takes the regions path, where a missing one is refused by name; only an ask naming NO region
    /// reaches the spot path, where the plugin mints the address.
    ///
    /// It lives here, GH-free, rather than inline in the ask, so the one guard in this path that most needs a
    /// test has one: the rest of the ask runs against a live canvas no headless test can reach.
    ///
    /// Naming regions AND giving a placement is refused rather than resolved either way: it is two kinds of
    /// scope in one request, and silently dropping one half would discard something the model said it meant.
    /// </summary>
    public static BuildAskRouting RouteFor(HandoffSeed seed)
    {
      var named = (seed?.Regions ?? new List<string>()).Where(r => !string.IsNullOrWhiteSpace(r)).ToList();

      if (named.Count > 0 && seed?.Place != null)
        return new BuildAskRouting
        {
          Route = BuildAskRoute.Regions,
          Refusal = ToolResult.Fail(ToolErrors.InvalidArgs,
            "That request names regions AND gives a placement, which is two different kinds of scope in one " +
            "ask. To build into the map, name the regions and leave \"place\" out. To put up to " + SpotPlacement.MaxComponents + " components " +
            "down without a map, give \"place\" and leave \"regions\" out."),
        };

      return new BuildAskRouting { Route = named.Count > 0 ? BuildAskRoute.Regions : BuildAskRoute.Spot };
    }

    /// <summary>The VALIDATED spec - authoritative for the build, not a description of it.</summary>
    public BuildSpec Spec { get; private set; }

    /// <summary>What the card says, rendered from the validated spec.</summary>
    public SpotPlacement Placement { get; private set; }

    /// <summary>Brief CM: the existing group this lands in, or null for a place of its own.</summary>
    public NamedGroup Into { get; private set; }

    /// <summary>
    /// Validate a nomination into the thing a card can be drawn from, or refuse BY NAME.
    ///
    /// EVERY REFUSAL HERE COSTS THE MODEL ONE OF ITS THREE ASKS, so every refusal must be worth the try:
    /// an uninstalled type names the near-matches, a wire end names the real ports, a malformed
    /// nomination names the field. ADR-094 is the whole justification for letting a model retry at all,
    /// and it holds only while the diagnostic is mechanically actionable - a refusal that said "invalid"
    /// and burned a try would be a bound with no loop to close.
    /// </summary>
    public static bool Validate(HandoffSeed seed,
                                Func<string, bool> typeExists,
                                Func<string, IReadOnlyList<string>> nearest,
                                Func<int, IdState> idState,
                                Func<string, PortShape> portsOfType,
                                Func<int, PortShape> portsOfId,
                                Func<int, string> labelOfId,
                                out SpotAsk ask, out ToolResult error) =>
      Validate(seed, new SpecResolvers
      {
        TypeExists = typeExists, Nearest = nearest, IdState = idState,
        PortsOfType = portsOfType, PortsOfId = portsOfId,
      }, labelOfId, out ask, out error);

    /// <summary>The form the live ask uses: the SAME <see cref="SpecResolvers"/> the build parses with, so a
    /// wire the build would refuse is refused here first, by the same code.</summary>
    public static bool Validate(HandoffSeed seed, SpecResolvers resolvers, Func<int, string> labelOfId,
                                out SpotAsk ask, out ToolResult error)
    {
      ask = null;
      error = null;

      var place = seed?.Place;
      if (place == null || (place["components"] as JsonArray) == null || (place["components"] as JsonArray).Count == 0)
      {
        error = ToolResult.Fail(ToolErrors.InvalidArgs,
          "A placement with no map needs \"place\", naming every component to put down - each with its " +
          "\"type\", and its \"state\" where it takes one. The user approves exactly what it names, so " +
          "Dwight cannot ask on behalf of components it has not been told about.");
        return false;
      }

      // THE MODEL MAY NOT NAME ITS OWN ADDRESS (#581's rule, and #763's reason): the spot is minted and reserved by the plugin. What it may name is a place
      // that EXISTS (Brief CM): that lands there or is refused with the names that do exist, and a new group is never the silent answer to a named destination.
      NamedGroup into = null;
      string notIn = null;
      var groups = resolvers?.Groups?.Invoke() ?? new NamedGroup[0];
      if (place["region"] != null)
      {
        string asked = null;
        try { asked = place["region"].GetValue<string>(); } catch { }
        if (!RegionNames.Resolve(asked, groups, out into, out error)) return false;
      }
      else
      {
        var named = RegionNames.NamedIn(resolvers?.LastUserText?.Invoke(), groups);
        if (named != null)
        {
          var separate = false;
          try { separate = place["separate"]?.GetValue<bool>() ?? false; } catch { }
          if (!separate) { error = RegionNames.UserNamedRefusal(named); return false; }
          notIn = named.Display;
        }
      }

      // ONE DEFINITION OF A COMPONENT DECLARATION (#248): the refusals below are BuildSpec's own, which
      // already carry the near-matches and the real port lists this path's budget obliges it to give.
      if (resolvers == null)
      {
        error = ToolResult.Fail(ToolErrors.PreconditionRefused,
          "Dwight could not check that placement against what is installed, so it will not offer it.");
        return false;
      }

      // Where it lands is the ask's question, and answered above. What the spec reads is what it places.
      var nomination = (JsonObject)place.DeepClone();
      nomination.Remove("region");
      nomination.Remove("separate");
      if (!resolvers.Parse(nomination, out var spec, out var specError))
      {
        error = specError;
        return false;
      }

      // THE PLUGIN'S WORDS, NOT THE MODEL'S. The parser accepted each type through a normalised lookup but
      // kept the model's spelling, so without this a card would read "a number slider" - the nomination
      // echoed back - while claiming to render from the validated read. Canonicalising HERE, on the spec the
      // card and the record are both drawn from, makes that claim true for both at once.
      if (resolvers.CanonicalType != null)
        foreach (var c in spec.Components)
          if (c != null && !string.IsNullOrWhiteSpace(c.Type))
            c.Type = resolvers.CanonicalType(c.Type) ?? c.Type;

      // THE BOUND: a placement without a map lands as one block (SpotPlacement.MaxComponents, Brief CV).
      // It REFUSES rather than truncating, and names the door: work this size wants a map.
      if (!SpotPlacement.TryDescribe(spec.Components.Select(Describe).ToList(), address: null,
                                     beside: Beside(spec, labelOfId), out var placement, out var boundError))
      {
        error = boundError;
        return false;
      }

      // U19: the scripts the card must SHOW IN FULL, from the same validated spec the record and the build come from.
      placement.Scripts = SpotScripts.Of(spec, resolvers.PortsOfType);
      // Brief CV: a card shows every script whole, so it carries as many as a region card may, and is refused in the same words.
      if (placement.Scripts.Count > ScriptsAsk.MaxScripts)
      {
        error = ScriptsAsk.TooMany(placement.Scripts.Count);
        return false;
      }
      placement.Into = into?.Display;
      placement.NotIn = notIn;

      ask = new SpotAsk { Spec = spec, Placement = placement, Into = into, Record = Serialise(spec) };
      return true;
    }

    /// <summary>
    /// THE WHOLE SPOT HALF OF THE ASK, in the order it must happen: validate, then mint, then write the
    /// plugin's fields onto the seed. The live ask calls this, and so do the tests - so what is tested is the
    /// path, not a copy of it.
    ///
    /// THE MINT COMES LAST, after everything that can refuse. A reservation is a promise to a card, and a
    /// refused ask has no card to keep it for.
    ///
    /// THEN THE SEED IS OVERWRITTEN (#862): <c>Spot</c> with the minted address, and <c>Place</c> with the
    /// RECORD - the validated spec in the plugin's own words - so the card renders from, and the click builds
    /// from, what was validated rather than what was asked for.
    ///
    /// THERE IS NO PER-TURN SPOT COUNT HERE, deliberately (#876, reversing #863/#864). The turn loop already
    /// bounds what a count would: it ends a turn at its second refused request
    /// (<c>AgentLoopCore.MaxRefusedRequests</c>), and it ends the turn after any step that shows a card. What
    /// that leaves is ONE STEP's worth of cards - a model that calls this twice in one step (parallel tool
    /// use is not disabled) raises two - and a count would protect nothing there: each card mints its own
    /// address and asks its own consent, so two cards are two approved placements, never one placed twice.
    /// The bound is pinned by a test instead: two refused spot asks end the turn.
    /// </summary>
    public static bool Prepare(HandoffSeed seed, SpecResolvers resolvers, Func<int, string> labelOfId,
                               SpotMint mint, IEnumerable<string> onCanvas, out SpotAsk ask, out ToolResult error)
    {
      if (!Validate(seed, resolvers, labelOfId, out ask, out error)) return false;

      if (mint == null)
      {
        ask = null;
        error = ToolResult.Fail(ToolErrors.PreconditionRefused,
          "Dwight could not reserve a place on the canvas for that, so it will not offer it.");
        return false;
      }

      ask.Placement.Address = mint.Reserve(onCanvas);
      seed.Spot = ask.Placement.Address;
      seed.Place = ask.Record;
      seed.IntoGroup = ask.Into?.Guid.ToString();
      seed.IntoName = ask.Into?.Display;
      return true;
    }

    /// <summary>
    /// THE LEDGER INVARIANT, checked at the CLICK (#877 (c)): a spot is ONE placement, so a spot the ledger
    /// already holds facts for has been placed, and a second build to it is refused.
    ///
    /// WHY BOTH THIS AND THE SIBLING TOKENS. The sibling tokens stop the one path we know of - the card's two
    /// buttons, clicked one after the other. This stops EVERY path to a second placement at one spot,
    /// including ones nobody has thought of yet, because it reads the outcome (facts recorded) rather than
    /// the route taken to get there. Dormant facts count too: a spot placed and then undone has still been
    /// used, and the conversation may still say its name (#720).
    ///
    /// STATED BOUND: the ledger lives in memory, so this holds within a session. Across a restart the mint can
    /// re-issue a loose spot's number - see the note on <see cref="SpotMint"/>.
    ///
    /// Returns the refusal to show the user, or null when the spot is unused.
    /// </summary>
    public static string RefuseIfPlaced(BuildLedger ledger, string spot)
    {
      if (ledger == null || string.IsNullOrEmpty(spot) || ledger.FactCount(spot) == 0) return null;
      return "Dwight has already placed components at " + spot + ", so it will not place them there a second " +
             "time. If you want another copy, ask again and Dwight will offer it somewhere new.";
    }

    /// <summary>
    /// THE REPORT THE CLICK APPENDS WHEN A SPOT LANDS - "Placed a Panel, Count in Spot 1." - named from the
    /// card's own record, every component in it. Moved here unchanged from the click (#878) so the saved
    /// conversation's reader is tested against what this writer really writes, not a hand-typed copy of it.
    /// </summary>
    public static string PlacedReport(JsonObject record, string spot) => PlacedReport(record, spot, null);

    /// <summary>
    /// U44: THE REPORT FOR A SPOT THAT DID NOT LAND, in words true of a BUILD. The click used to borrow set_value's
    /// pair, whose transcript form reads "[Not applied: the user approved this set_value, and Dwight changed nothing]" -
    /// a claim about the wrong kind, in the model's own transcript (#680: a borrowed renderer inherits its claim).
    ///
    /// TWO FACTS, TWO HEADERS. A refusal at the click (the address is taken, the placement is gone) changed nothing:
    /// "Not applied - the canvas is unchanged". A build the verdict REVERTED applied something and took it back, which
    /// is a different fact about the undo stack and must not be called "not applied" (#683 6c).
    /// </summary>
    public static string FailedText(ToolResult result)
    {
      var why = (result?.Message ?? "Dwight could not complete the placement.").Trim();
      return IsRevert(result)
        ? "[Attempted by Dwight on the user's approval, and REVERTED - the canvas is as it was] " + why
        : "[Not applied - the canvas is unchanged] " + why;
    }

    /// <summary>The screen form of <see cref="FailedText"/>. THE USER'S READING (U46): the refusal's
    /// <c>userMessage</c> when the build carried one - the compiler's words and the plugin's sentences, without the
    /// reflected signatures that are the model's repair material - else the message as it is.</summary>
    public static string FailedHtml(ToolResult result)
    {
      var shown = UserMessage(result);
      return (IsRevert(result) ? "<p><strong>Reverted.</strong> " : "<p><strong>Not applied.</strong> ") +
             System.Net.WebUtility.HtmlEncode(shown) + "</p>";
    }

    /// <summary>What the USER reads of a refusal: the build's own user-facing wording when it carries one.</summary>
    public static string UserMessage(ToolResult result)
    {
      string user = null;
      try { user = result?.Detail?["userMessage"]?.GetValue<string>(); } catch { }
      return (!string.IsNullOrWhiteSpace(user) ? user : result?.Message ?? "Dwight could not complete the placement.").Trim();
    }

    /// <summary>The click-time refusals (no model, no build ran): the neutral pair.</summary>
    public static string NotAppliedText(string reason) => "[Not applied - the canvas is unchanged] " + (reason ?? "").Trim();

    public static string NotAppliedHtml(string reason) =>
      "<p><strong>Not applied.</strong> " + System.Net.WebUtility.HtmlEncode((reason ?? "").Trim()) + "</p>";

    private static bool IsRevert(ToolResult result) =>
      result != null && string.Equals(result.Code, ToolErrors.VerificationFailed, StringComparison.Ordinal);

    /// <summary>
    /// U43 (#975): THE SAME REPORT AS THE REGION BUILD'S, through the same composer. The spot's line said "Placed X in
    /// Spot 1." and nothing about what was checked or what the placed components hold, while U26 (#951) gave the
    /// region build its "Checked: ..." sentence plus one fact per geometry component. Given the
    /// build's applied verdict, the line carries <see cref="BuildPlacedFacts.Summary"/> - one sentence in one place,
    /// so the two paths cannot say different things. No verdict (the build said nothing to compose) is the old line.
    /// </summary>
    public static string PlacedReport(JsonObject record, string spot, JsonNode verdict) =>
      PlacedReport(record, spot, verdict, true);

    /// <summary>
    /// THE LOOSE ROUTE NAMES NO GROUP. The gate run (2026-10-25) read "Placed radius in Spot 1." after the LOOSE card was
    /// approved, though no group was made and the canvas held a bare slider. A loose placement says so: "Placed radius loose
    /// on the canvas (Spot 1)." The address stays in the line on purpose - the saved conversation's reader (SpotMint, #878)
    /// finds "Spot N" in the text to keep that number from being minted again, and a loose spot has no group to show it.
    /// Brief CM: and it says where, from what the build measured (<see cref="Landing.Where"/>).
    /// </summary>
    public static string PlacedReport(JsonObject record, string spot, JsonNode verdict, bool grouped)
    {
      var names = record?["components"] is JsonArray comps
        ? comps.Select(c => (c?["nick"] ?? c?["type"])?.GetValue<string>() ?? "a component")
        : new[] { "the components" };
      var where = Landing.Read(verdict);
      var at = string.IsNullOrWhiteSpace(where) ? "" : ", " + where;
      var line = grouped
        ? "Placed " + string.Join(", ", names) + " in " + spot + at + "."
        : "Placed " + string.Join(", ", names) + " loose on the canvas (" + spot + ")" + at + ".";
      var summary = BuildPlacedFacts.Summary(verdict);
      return string.IsNullOrEmpty(summary) ? line : line + " " + summary;
    }

    /// <summary>
    /// THE RECORD: the validated spec, written back in the plugin's own words. This is what overwrites the
    /// nomination on the seed, so it is what the card renders from and what the click builds (#862).
    /// </summary>
    public JsonObject Record { get; private set; }

    /// <summary>
    /// A validated spec as JSON the build's parser will read back to the SAME spec - the round trip is
    /// tested, because a record that parses to something other than what was validated is #153 at the
    /// record level: the text stored must be the text that gets built.
    ///
    /// TWO THINGS IT MUST NOT LOSE, each a silent change if it did:
    ///   - A slider's decimal places are never written - they are DERIVED from the scale of the numbers
    ///     (BuildStateParser) - so a number must keep its scale exactly: writing 4.0 as 4 would change the
    ///     precision the slider is born with. Decimals are therefore written as decimals, never via double.
    ///   - Resolved port spellings are written as resolved, so the build reads "Radius", not "radius".
    ///
    /// A nick is the USER'S text and stays exactly as given - the plugin cannot author one. It is escaped at
    /// every render (#335: escaping is a property of the destination), never rewritten here.
    /// </summary>
    internal static JsonObject Serialise(BuildSpec spec)
    {
      var components = new JsonArray();
      foreach (var c in spec?.Components ?? new List<BuildComponent>())
      {
        if (c == null) continue;
        var o = new JsonObject { ["ref"] = c.Ref, ["type"] = c.Type };
        if (!string.IsNullOrEmpty(c.Nick)) o["nick"] = c.Nick;
        var state = State(c.State);
        if (state != null) o["state"] = state;
        // U19: THE SOURCE TRAVELS IN THE RECORD, so the click builds from the text the card showed (#153) - never from
        // anything the model sends again. Written only when present, so every placement without code serialises as it did.
        if (c.CarriesSource) o["source"] = c.Source;
        if (c.Inputs != null && c.Inputs.Count > 0) o["inputs"] = PortAccess.Record(c.Inputs, c.InputAccess);
        if (c.Outputs != null && c.Outputs.Count > 0) o["outputs"] = new JsonArray(c.Outputs.Select(n => (JsonNode)n).ToArray());
        components.Add(o);
      }

      var wires = new JsonArray();
      foreach (var w in spec?.Wires ?? new List<BuildWire>())
        if (w != null)
          wires.Add(new JsonObject { ["from"] = End(w.FromRef, w.FromId, w.FromParam), ["to"] = End(w.ToRef, w.ToId, w.ToParam) });

      return new JsonObject { ["components"] = components, ["wires"] = wires };
    }

    private static JsonObject End(string reference, int? id, string param)
    {
      var o = new JsonObject();
      if (reference != null) o["ref"] = reference;
      if (id.HasValue) o["id"] = id.Value;
      if (param != null) o["param"] = param;
      return o;
    }

    private static JsonObject State(BuildState s)
    {
      if (s == null) return null;
      switch (s.Kind)
      {
        case BuildStateKind.Slider:
          return new JsonObject
          {
            ["min"] = JsonValue.Create(s.Min),
            ["max"] = JsonValue.Create(s.Max),
            ["value"] = JsonValue.Create(s.Value),
            ["rounding"] = s.Rounding.ToString().ToLowerInvariant(),
          };
        case BuildStateKind.PanelText:
          return new JsonObject { ["text"] = s.Text ?? "" };
        // U30: a constant round-trips as the one field its param takes - value as a DECIMAL (the scale is kept, as a
        // slider's is), text as text.
        case BuildStateKind.Constant:
          return s.ConstantType == ConstantType.Text
            ? new JsonObject { ["text"] = s.Text ?? "" }
            : new JsonObject { ["value"] = JsonValue.Create(s.Value) };
        default:
          return null;
      }
    }

    /// <summary>
    /// What ONE component is called on the card - from the VALIDATED declaration, never the model's raw
    /// string. A nickname is what the user will see on the canvas, so it wins where it was given.
    /// </summary>
    private static string Describe(BuildComponent c)
    {
      // U25: the RECORD keeps a qualified type ("Curve > Primitive > Rectangle") so the click resolves exactly; the
      // CARD shows a person the bare name, which is what they will see on the canvas.
      var name = string.IsNullOrWhiteSpace(c?.Nick)
        ? Dwight.Components.ComponentMatchCore.BareName(c?.Type ?? "component")
        : c.Nick.Trim();
      // U30: THE VALUE SHOWS ON THE CARD BESIDE THE COMPONENT. A slider's value, a panel's text, a constant - from the
      // VALIDATED state, so what the user reads is what the click sets; a value nobody could see was #377's whole defect.
      var value = BuildStateText.NamedValue(c?.State);
      return Article(name) + " " + name + (value == null ? "" : " set to " + value);
    }

    private static string Article(string name) =>
      name.Length > 0 && "AEIOUaeiou".IndexOf(name[0]) >= 0 ? "an" : "a";

    /// <summary>
    /// WHAT IT WILL SIT BESIDE, in words a person can check by looking - the first EXISTING component
    /// this placement wires into or out of. Null when it connects to nothing, which the card renders as
    /// <see cref="ConsentCard.ClearPlace"/>.
    ///
    /// Read through a delegate rather than from the model's text: the model names an id, the plugin says
    /// what that id is CALLED. A card that took the name from the nomination could describe the placement
    /// as sitting beside something it is not.
    /// </summary>
    private static string Beside(BuildSpec spec, Func<int, string> labelOfId)
    {
      if (labelOfId == null) return null;

      foreach (var wire in spec.Wires ?? new List<BuildWire>())
      {
        foreach (var id in new[] { wire?.FromId, wire?.ToId })
        {
          if (id == null) continue;
          var label = labelOfId(id.Value);
          if (!string.IsNullOrWhiteSpace(label)) return label.Trim();
        }
      }
      return null;
    }
  }
}
