using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace Dwight.Agent
{
  /// <summary>
  /// What the agent nominates when it offers to hand a job to a verb (D1) - the components it was talking
  /// about, the mode it thinks fits, and why.
  ///
  /// It exists because the verbs read the LIVE SELECTION. Before this, an agent could analyse a region in
  /// detail, raise a consent card, and the user's approval would open Simplify on whatever happened to be
  /// selected at that moment - which is usually nothing. The analysis was thrown away at the door.
  ///
  /// MOST OF THIS IS MODEL-AUTHORED AND THEREFORE UNTRUSTED. See <see cref="SeedPlan"/> for the boundary.
  ///
  /// BUT NOT ALL OF IT, AND THE DISTINCTION IS THE WHOLE REASON TO READ THIS CLASS. Until U20 this summary
  /// said "EVERYTHING here is model-authored" - which had already stopped being true at U4
  /// (<see cref="TargetType"/>) and at Stage C (<see cref="Regions"/>, overwritten with the VALIDATED
  /// set), and which U20 then made false in four more places. A claim about our own behaviour that has gone
  /// stale is bad anywhere (#315); on the documentation of a TRUST BOUNDARY it is worse, because this
  /// comment is exactly what a reader consults to decide whether a field needs validating before use - and
  /// the two mistakes it invites are opposite and both live: validating a plugin-computed value as though a
  /// model wrote it, or trusting a model-written one by analogy with the neighbour above it.
  ///
  /// AND THE SHARPER VERSION OF WHY (mantis-bd, reaching it independently on the same line): THE FIELDS THE
  /// OLD SENTENCE WAS WRONG ABOUT ARE EXACTLY THE ONES WHOSE PLUGIN AUTHORSHIP *IS* THE GUARANTEE. A reader
  /// who believes everything here is model-authored concludes that guarantee is absent - and the repair
  /// that follows is to route a plugin-written field through model-validation, which quietly discards the
  /// reason the field exists. The damage is not untidiness; it is a correct guarantee being validated away
  /// by someone trying to help.
  ///
  /// SO BOTH HALVES ARE STATED, AND SO IS WHAT EACH DOES *NOT* MEAN. Model-authored is not "unvalidated and
  /// dangerous" - <see cref="SeedPlan"/> is where it is checked, and that is the whole point of the split.
  /// Plugin-written is not "skip the check" - there is no adversary for that field, so there is nothing for
  /// a check to defend against. Each field's own comment says which it is:
  ///
  ///   MODEL-AUTHORED, untrusted, re-grounded or shape-checked before it is used: ComponentIds, Mode, Why,
  ///   Target, State, Claim, Source, AddInputs, AddOutputs, and - AS THEY ARRIVE - Regions, Place and Ops.
  ///
  ///   PLUGIN-WRITTEN AT THE ASK, from the live canvas or from a check the plugin ran, and so as
  ///   trustworthy as anything else the plugin computed: TargetType, Regions ONCE VALIDATED, Place ONCE
  ///   VALIDATED, Ops ONCE VALIDATED, Spot, BaseSource, Outcome, Report, TrialNote.
  ///
  ///   PLACE LIVES IN BOTH LISTS, AS REGIONS DOES, AND FOR THE SAME REASON (U18, #862). It arrives as the
  ///   model's NOMINATION and the ask OVERWRITES it with the plugin's RESOLVED spec - canonical type names,
  ///   real port spellings - which is what the card renders and what the click builds. THE RECORD IS THE
  ///   SPEC, not a copy of the request. Filed under model-authored alone, it would invite exactly the two
  ///   mistakes above: re-validating a spec the plugin already resolved, or trusting the raw nomination by
  ///   analogy with the field beside it.
  ///
  ///   PLUGIN-WRITTEN AT THE CLICK, from which button the user pressed: SpotGrouped. It is the one
  ///   field written after the card was drawn, and it is listed apart so that the "at the ask" claim
  ///   above stays true of every field it names (#315 - a summary of a trust boundary that is right
  ///   about most fields is the kind that gets believed about the rest).
  ///
  /// The plugin-written fields exist because THE CARD'S RECORD MUST OUTLIVE THE ASK: the click happens in a
  /// later turn, and what the user approved has to reach it without being re-derived from a canvas that may
  /// have moved. That is a different job from carrying a nomination, and one class doing both is recorded
  /// as a design pressure rather than defended - see the plan document.
  /// </summary>
  internal sealed class HandoffSeed
  {
    /// <summary>Agent ids (#191), never guids and never positions - see <c>ChatAnchors</c> for why that
    /// choice is what makes a stale nomination refusable rather than misleading.</summary>
    public List<int> ComponentIds { get; set; } = new List<int>();

    /// <summary>A mode the model suggests. Validated against the kind's own vocabulary, or ignored.</summary>
    public string Mode { get; set; }

    /// <summary>The model's reason, in its own words. Rendered escaped, attributed, and subordinate to the
    /// card's own authority line - #235's two voices. (Not "to the verb's preview": Reorganise has none, #890.)</summary>
    public string Why { get; set; }

    /// <summary>Scaffold region ADDRESSES a build asks to be approved for (Stage C). Nominated by the
    /// model, VALIDATED by the plugin against the canvas, and then binding: the approved set is what the
    /// turn may write to, and nothing else (#338 in address space).</summary>
    public List<string> Regions { get; set; } = new List<string>();

    /// <summary>Phase-9 U4, kind=set_value ONLY: the EXISTING component to change, by agent id. Parsed
    /// leniently here (a malformed value is null, never another integer), validated by
    /// <see cref="SetValueAsk"/> at the ask and again at the click.</summary>
    public int? Target { get; set; }

    /// <summary>Phase-9 U4, kind=set_value ONLY: the declared state, raw. Parsed by stream A's
    /// <see cref="BuildStateParser"/> against the target's LIVE type at the ask - never here.</summary>
    public System.Text.Json.Nodes.JsonObject State { get; set; }

    /// <summary>Phase-9 U4, kind=set_value ONLY: the type the CARD named, written by the PLUGIN at the ask from the live
    /// read (never by the model), so the click can refuse a target that is now something else (#683, 6b).</summary>
    public string TargetType { get; set; }

    /// <summary>Phase-9 U20, kind=edit_script ONLY: WHAT THE EDIT CLAIMS TO DO - "preserves" or "changes".
    /// Read here as the model's raw word and turned into an ENUM by <see cref="EditScriptAsk.ClaimOf"/>,
    /// because the claim decides WHICH PROOF RUNS: a decision the model could phrase its way into is not a
    /// decision the plugin controls (#235). An unrecognised word is refused by name, never defaulted to the
    /// weaker claim - a typo must not buy a model the easier proof.</summary>
    public string Claim { get; set; }

    /// <summary>Phase-9 U20, kind=edit_script ONLY: the COMPLETE new source, never a fragment or a patch.
    /// Normalised at the ask, so the text shown is the text tested is the text applied (#153).</summary>
    public string Source { get; set; }

    /// <summary>Full control (U90), kind=run_script ONLY: the clock the approval carries, in seconds. Validated at the ask (1 to 600).</summary>
    public int? RunTimeoutSeconds { get; set; }

    /// <summary>Phase-9 U20, kind=edit_script ONLY: ports to APPEND. Never a removal, a reorder or a rename -
    /// appending cuts nothing, while the other three sever or re-bind the user's existing wires, which is the
    /// boundary-cut class #625 already governs elsewhere.</summary>
    public List<string> AddInputs { get; set; } = new List<string>();
    public List<string> AddOutputs { get; set; } = new List<string>();

    /// <summary>Brief CP, kind=edit_script ONLY: the access declared on an input, an existing one or one being added, by name. As the model sent it: <see cref="EditScriptAsk"/> reads the words.</summary>
    public Dictionary<string, string> InputAccess { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Phase-9 U20: the source the CARD DIFFED FROM, written by the PLUGIN at the ask from the live read -
    /// never by the model (<see cref="TargetType"/>'s precedent, #683 6b).
    ///
    /// It is what makes the click's compare-and-set possible: the click has to know the source as it was
    /// WHEN THE CARD WAS DRAWN, because re-reading it live would compare the live source against itself
    /// and always agree - defeating the one check whose purpose is to notice that the user typed into the
    /// component while the card was up. A diff whose base has moved is a card asserting something untrue
    /// at the moment of approval (#775).
    /// </summary>
    public string BaseSource { get; set; }

    /// <summary>Phase-9 U20: the TRIAL OUTCOME the card was drawn from, written by the PLUGIN at the ask.
    /// It travels so the report the user reads after applying is the one they approved - recomputing it at
    /// the click would cost a second trial and could disagree with what the card said. Its own field rather
    /// than Mode, which is validated against the kind action vocabulary and would discard it (#680).</summary>
    public string Outcome { get; set; }

    /// <summary>
    /// Phase-9 U20: the two sentences the TRIAL produced, rendered by the PLUGIN at the ask and carried
    /// on the card record - <see cref="Report"/> for the user, <see cref="TrialNote"/> for the model.
    ///
    /// They travel as STRINGS because the trial FACTS do not survive the click: the record carries text,
    /// not live observations, so re-rendering at the click would have to invent the counts it no longer
    /// has. Carried, "the report says what the card said" is structural rather than a coincidence.
    /// </summary>
    public string Report { get; set; }
    public string TrialNote { get; set; }
    /// <summary>
    /// U18: the SPOT this card reserved, written by the PLUGIN at the ask (never by the model) from
    /// <see cref="SpotMint"/>, so the address the card NAMES is the address the build uses.
    ///
    /// Its own field, beside <see cref="Regions"/> rather than inside it, because a spot is the one
    /// address that is legitimately not on the canvas yet - and the list is re-resolved against the live
    /// canvas at click time, where a missing region refuses. Kept apart, that rule never has to be
    /// relaxed for anything.
    /// </summary>
    public string Spot { get; set; }

    /// <summary>U18: did the user take the GROUPED option? Written by the PLUGIN, never by the model - see the
    /// #346 note on <c>TurnPermission.SpotGrouped</c>. Fixed at the MINT, on one copy of the seed per button
    /// (<see cref="ForOption"/>), so the button pressed selects a copy and the click reads no option at all.</summary>
    public bool SpotGrouped { get; set; }

    /// <summary>Brief CM: the existing group this placement lands in, by its guid. Written by the PLUGIN at the ask from the one group the name resolved to, never by the model.</summary>
    public string IntoGroup { get; set; }

    /// <summary>Brief CM: what the card called that group, written with it.</summary>
    public string IntoName { get; set; }

    /// <summary>Brief BQ: History's sentence for this spot, PLUGIN-WRITTEN at the ask from the validated placement. It rides the record because a
    /// spot's record is what runs, and nothing at the build knows what the card named or sat beside.</summary>
    public string SpotDone { get; set; }

    /// <summary>
    /// U18, kind=build WITHOUT a map: the components to put down. MODEL-AUTHORED AS IT ARRIVES - a nomination,
    /// validated by <see cref="SpotAsk"/> at the ask - and then PLUGIN-WRITTEN: the ask overwrites it with the
    /// resolved spec, so the record holds what will be built rather than what was asked for. The same
    /// declaration shape <c>build_region</c> takes, minus the region, because the plugin mints the address.
    ///
    /// IT RIDES THE ASK BECAUSE THE CARD MUST NAME WHAT IT APPROVES. A spot has no region name standing
    /// in for its contents, so the components are the scope - and a card cannot name what the plugin does
    /// not yet know. The VALIDATED result is then authoritative for the build itself, not a description
    /// of it: see <see cref="SpotAsk"/> for why that is unrepresentability rather than a check.
    /// </summary>
    public JsonObject Place { get; set; }

    /// <summary>
    /// U65, kind=build INTO A MAP ONLY: the scripts the card must show whole - region, type, source, appended ports. MODEL-AUTHORED
    /// AS IT ARRIVES (a nomination, carried leniently: anything that is not an array is null) and then PLUGIN-WRITTEN:
    /// <see cref="ScriptsAsk"/> parses every entry with the build's own parser at the ask and OVERWRITES this with the resolved,
    /// normalised record, so what the card shows and what the click grants is what the build will compare against.
    /// </summary>
    public JsonArray Scripts { get; set; }

    /// <summary>
    /// Phase-9 U6 + U21, kind=edit ONLY: the changes to put on ONE card - wire, unwire, delete - raw. MODEL-AUTHORED AS
    /// IT ARRIVES (a nomination, carried leniently: anything that is not an array is null, never coerced) and then
    /// PLUGIN-WRITTEN: <see cref="EditOpsAsk"/> validates every row against the live canvas at the ask and OVERWRITES this
    /// with the plugin's resolved form (canvas port spellings), so the record the click re-validates is what the card
    /// showed (#862's shape, as <see cref="Place"/>). The user's ticks never live here - they arrive with the click.
    /// </summary>
    public JsonArray Ops { get; set; }

    /// <summary>
    /// U18: this seed as ONE OPTION of a two-button spot card - a copy differing only in
    /// <see cref="SpotGrouped"/>, so each button's token carries its own answer.
    ///
    /// A COPY OF EVERY FIELD, BY CONSTRUCTION (MemberwiseClone), not a list of fields somebody must keep in
    /// step with this class - a field added later is carried without anyone remembering to. It is shallow,
    /// which is safe because nothing mutates a seed's lists or record after the card is minted: the click
    /// deep-clones the record before it builds from it.
    /// </summary>
    public HandoffSeed ForOption(bool grouped)
    {
      var copy = (HandoffSeed)MemberwiseClone();
      copy.SpotGrouped = grouped;
      return copy;
    }
  }

  /// <summary>
  /// What a seeded hand-off actually offers - and the one place #287's authority boundary is enforced.
  ///
  /// THE STRUCTURAL GUARANTEE, which is stronger than a rule anyone has to remember:
  /// <see cref="Actions"/> IS A PURE FUNCTION OF <c>kind</c>. It is built before the seed is examined, and
  /// there is no code path by which a seed value reaches it. So #287(d) - *the same verb invocation with an
  /// honest payload and a hostile one must offer the identical set of actions, only the pre-fill differing*
  /// - is true by the shape of the function, and its tests are a regression guard rather than the proof.
  ///
  /// The rest follows from that split:
  ///   (a) THE SEED BIASES THE PROPOSAL, NEVER THE VERDICT. It picks the verb's input set and pre-selects a
  ///       control. The verification stack - fingerprint, predicted-delta, equivalence, completeness - runs
  ///       on what the verb ACTUALLY did and is structurally independent of anything suggested here. A
  ///       hostile seed can waste a run; it cannot certify one.
  ///   (b) EVERY ID IS RE-GROUNDED, NEVER TRUSTED - through the registry to alive-in-the-current-graph, and
  ///       an id that fails is DROPPED AND NAMED, never silently absent (#208: a verdict is only as honest
  ///       as its weakest resolution step).
  ///   (c) A SUGGESTED MODE PRE-SELECTS A CONTROL THE USER CAN CHANGE. An unrecognised one is ignored and
  ///       the control stays at its default; it can never auto-execute.
  ///
  /// And the clause that keeps it from needing new guards at all: THE SEED REACHES THE VERB AS INPUT, AT
  /// THE SAME DOOR A HAND-MADE SELECTION ARRIVES THROUGH - so the whole-canvas confirm, the stale-document
  /// guard and every other check on that door still apply to it, unchanged.
  /// </summary>
  internal sealed class SeedPlan
  {
    /// <summary>What the surface will offer. A pure function of the kind.</summary>
    public IReadOnlyList<string> Actions { get; private set; } = new string[0];

    /// <summary>The nominated components that survived re-grounding, in the order given.</summary>
    public IReadOnlyList<int> PreFillIds { get; private set; } = new int[0];

    /// <summary>The suggested mode if the kind actually has it, otherwise null.</summary>
    public string PreFillMode { get; private set; }

    /// <summary>The model's reason, HTML-escaped and ready to render as its own voice.</summary>
    public string Note { get; private set; } = "";

    /// <summary>
    /// The same reason UNESCAPED, for the one place that needs the text rather than the markup: the verb's
    /// prompt, where it rides as the intent the user approved (#332).
    ///
    /// Two forms of one string, deliberately, because escaping is a property of the DESTINATION - HTML
    /// entities in a prompt are noise the model has to see through, and raw text in a panel is an injection.
    /// Keeping both here means neither call site has to remember which it needs.
    /// </summary>
    public string Intent { get; private set; } = "";

    /// <summary>What was refused and why, in the user's language. Never silent.</summary>
    public IReadOnlyList<string> Dropped { get; private set; } = new string[0];

    /// <summary>
    /// For a BUILD: the region addresses the card approved (Stage C).
    ///
    /// They were already validated against the canvas before the card was drawn, and they are re-resolved
    /// AGAIN at click time - a card can sit while the canvas moves, and the turn must be scoped to what
    /// exists when it runs rather than to what existed when it was offered.
    /// </summary>
    public IReadOnlyList<string> Regions { get; private set; } = new string[0];

    /// <summary>
    /// The action vocabulary per kind - the SINGLE definition, used both to build <see cref="Actions"/> and
    /// to validate a suggested mode, so the two can never disagree about what exists.
    /// </summary>
    private static readonly Dictionary<string, string[]> ActionsByKind =
      new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
      {
        ["simplify"] = new[] { "logic", "csharp", "python" },
        ["reorganise"] = new[] { "compact", "hchain", "vertical", "grid" },
        ["solve"] = new[] { "solve" },
        ["scaffold"] = new[] { "scaffold" },
        ["build"] = new[] { "build" },
        ["set_value"] = new[] { "set_value" },
        ["edit_script"] = new[] { "edit_script" },
        ["edit"] = new[] { "edit" },
      };

    /// <summary>Phase-9 U4: the set_value target, carried through shape-checked only; the ask and the click
    /// validate it against the live canvas (<see cref="SetValueAsk"/>). A carrier, never a grant.</summary>
    public int? Target { get; private set; }

    /// <summary>Phase-9 U4: the declared state, raw, for the same validation.</summary>
    public System.Text.Json.Nodes.JsonObject State { get; private set; }

    /// <summary>Phase-9 U4: the type the card named, for the click's scope rule (<see cref="SetValueCore.MayProceed"/>).</summary>
    public string TargetType { get; private set; }

    /// <summary>Phase-9 U20: the edit, as it reached the click. The claim is an ENUM by the time it is
    /// here, so an unrecognised word never travels as a default; BaseSource is the plugin-written base the
    /// card diffed from, which the click compares against the live source.</summary>
    public ScriptEditClaim Claim { get; private set; }
    public string Source { get; private set; }
    /// <summary>Phase-9 U6 + U21: the edit's ops as the card carried them - the plugin's resolved record, shape-checked
    /// only here. The click re-validates the ticked subset against the canvas as it is then (<see cref="EditOpsAsk"/>).</summary>
    public JsonArray Ops { get; private set; }

    public IReadOnlyList<string> AddInputs { get; private set; } = new string[0];
    public IReadOnlyList<string> AddOutputs { get; private set; } = new string[0];

    /// <summary>Brief CP: the access the edit declares, as it reached the click, where the ask reads it again.</summary>
    public IReadOnlyDictionary<string, string> InputAccess { get; private set; } = new Dictionary<string, string>();
    public string BaseSource { get; private set; }
    public ScriptEditOutcome Outcome { get; private set; }
    public string Report { get; private set; }
    public string TrialNote { get; private set; }
    /// <summary>U18: the reserved spot this card names, or null. Shape-checked here; that it is the one
    /// the mint promised is settled at the click, and whether it is free is settled by
    /// <see cref="BuildScope.Resolve"/>, which REFUSES a collision rather than renumbering - an address on
    /// a card is a promise, and one that moved between the card and the click would have been approved as
    /// something else.</summary>
    public string PreFillSpot { get; private set; }

    /// <summary>U18: the grouping option the user took. Set from the button that was pressed.</summary>
    public bool PreFillSpotGrouped { get; private set; }

    /// <summary>Brief CM: the existing group the card said this lands in, or null. An identity, so the click finds that group or refuses, never one with the same name.</summary>
    public Guid? PreFillIntoGroup { get; private set; }

    /// <summary>Brief CM: what the card called that group.</summary>
    public string PreFillIntoName { get; private set; }

    /// <summary>
    /// U18: the nomination the card was drawn from, carried to the click so the placement can be
    /// RE-VALIDATED against the canvas as it stands then - the compare-and-set shape set_value already
    /// uses, because a card can sit while the canvas moves.
    ///
    /// Re-validating cannot produce a DIFFERENT placement, only a refused one: the components come from
    /// this same nomination either way, so the only thing that can change is whether they still resolve.
    /// A type uninstalled since, or a wire target deleted since, refuses at the click rather than
    /// building something the card did not name.
    /// </summary>
    public JsonObject PreFillPlace { get; private set; }

    /// <summary>U65: the scripts the region build card showed, shape-checked. The click grants them to the build turn; the build
    /// holds every script in its spec to them (<see cref="BuildScriptGate"/>).</summary>
    public IReadOnlyList<ApprovedScript> Scripts { get; private set; } = new ApprovedScript[0];

    /// <summary>
    /// The HUMAN NAME of a mode - plugin-authored, from the enumerated kind and the validated mode id.
    ///
    /// It sits beside <see cref="ActionsByKind"/> deliberately: the list of what exists and the words for
    /// what exists are one definition, so a mode can never be offerable but unnameable. The consent card's
    /// authority line is built from this (#332) and therefore still contains not one word the model wrote.
    /// Null for an unknown kind or mode - the caller then says nothing rather than guessing.
    /// </summary>
    /// <summary>
    /// The suggested mode if the kind really has it, otherwise null - the SAME validation
    /// <see cref="From"/> applies, exposed so the consent card can name the mode without needing a
    /// document. It is a pure function of two strings, which is why it is safe to call from the tool
    /// thread where no GH state may be touched.
    /// </summary>
    public static string ValidMode(string kind, string mode)
    {
      if (kind == null || string.IsNullOrWhiteSpace(mode)) return null;
      if (!ActionsByKind.TryGetValue(kind, out var actions)) return null;
      return actions.FirstOrDefault(a => string.Equals(a, mode.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    public static string ModeLabel(string kind, string mode)
    {
      if (kind == null || mode == null) return null;
      var k = kind.Trim().ToLowerInvariant();
      var m = mode.Trim().ToLowerInvariant();

      if (k == "simplify")
        return m == "logic" ? "rewrite the logic to be cleaner and more robust"
             : m == "csharp" ? "collapse it into a single C# script component"
             : m == "python" ? "collapse it into a single Python script component"
             : null;

      if (k == "reorganise")
        return m == "compact" ? "lay it out compactly"
             : m == "hchain" ? "lay it out as one left-to-right chain"
             : m == "vertical" ? "lay it out as one top-to-bottom chain"
             : m == "grid" ? "lay it out as a grid"
             : null;

      return null;
    }

    public static SeedPlan From(string kind, HandoffSeed seed, Func<int, IdState> state)
    {
      var plan = new SeedPlan();

      // ---- THE AUTHORITY BOUNDARY. Actions come from the kind, and the seed is not in scope yet. ----
      if (kind == null || !ActionsByKind.TryGetValue(kind, out var actions)) return plan;
      plan.Actions = actions;

      // ---- Below this line the seed is untrusted input, and can only reach the pre-fill. ----
      if (seed == null) return plan;

      var kept = new List<int>();
      var dropped = new List<string>();
      var seen = new HashSet<int>();

      foreach (var id in seed.ComponentIds ?? new List<int>())
      {
        if (!seen.Add(id)) continue;   // a nomination repeated is still one component
        var s = state == null ? IdState.Unknown : state(id);
        if (s == IdState.Alive) kept.Add(id);
        else if (s == IdState.Retired)
          dropped.Add("component " + id + " is no longer on the canvas");
        else
          dropped.Add("component " + id + " is not one Dwight has seen");
      }

      plan.PreFillIds = kept;
      plan.Dropped = dropped;

      // #287(c): recognised or ignored. Never a third outcome, and never a mode borrowed from another kind.
      plan.PreFillMode = ValidMode(kind, seed.Mode);

      // Stage C: the approved region addresses travel through, SHAPE-checked only. Whether each still
      // names a group is re-resolved at click time against the live canvas - this is a carrier, not a
      // grant, and it is deliberately not the place that decides what may be built.
      //
      // IsAddress HERE MEANS PLAN ADDRESS, AND MUST KEEP MEANING THAT (U18). A spot travels in its own
      // field below, never in this list - see the note on PreFillSpot for why the separation is the
      // guarantee rather than a tidiness.
      plan.Regions = (seed.Regions ?? new List<string>())
        .Where(r => ScaffoldAddress.IsAddress(r))
        .Distinct(StringComparer.Ordinal)
        .ToList();

      // U18: the minted spot, in a FIELD OF ITS OWN.
      //
      // WHY NOT JUST PUT IT IN Regions? Because every address in that list is re-resolved at click time
      // against the live canvas, and a region that is not there REFUSES (BuildScope's rule 2 - scope is
      // identity, so resolve exactly or refuse). A spot is the one address that is legitimately NOT on
      // the canvas yet: the card approved it and the build creates it. Carrying it in the same list would
      // mean relaxing that rule for everything in the list, and the rule is #338's machinery.
      //
      // Held apart, "approve an address that is not on the canvas" IS NOT EXPRESSIBLE FOR A PLAN REGION
      // AT ALL - not checked for and rejected, but unrepresentable, because there is no field a plan
      // region could travel in that reaches the spot door. That is ADR-056's shape applied to a carrier.
      //
      // Shape-checked only, like everything else here: that it is a SPOT is asserted, that it is the one
      // the card reserved is settled at the click.
      plan.PreFillSpot = ScaffoldAddress.IsSpot(seed.Spot) ? seed.Spot : null;
      plan.PreFillSpotGrouped = seed.SpotGrouped;
      plan.PreFillIntoGroup = Guid.TryParse(seed.IntoGroup, out var intoGroup) ? intoGroup : (Guid?)null;
      plan.PreFillIntoName = seed.IntoName;
      plan.PreFillPlace = seed.Place;
      plan.Scripts = ScriptsAsk.FromRecord(seed.Scripts);
      plan.Ops = seed.Ops;

      // Phase-9 U4: set_value's target and state travel through, validated at the ask and the click.
      plan.Target = seed.Target;
      plan.State = seed.State;
      plan.TargetType = seed.TargetType;

      // Phase-9 U20: the edit travels through, SHAPE-checked only - the claim must be one of the two that
      // exist or it does not travel, so an unrecognised word cannot reach the click as a default.
      plan.Claim = EditScriptAsk.ClaimOf(seed.Claim);
      plan.Source = seed.Source;
      plan.AddInputs = (seed.AddInputs ?? new List<string>()).ToList();
      plan.AddOutputs = (seed.AddOutputs ?? new List<string>()).ToList();
      plan.InputAccess = new Dictionary<string, string>(seed.InputAccess ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);

      // AND THE BASE THE CARD DIFFED FROM - the one field here the PLUGIN writes rather than the model
      // (TargetType's precedent, #683 6b). It is what makes the click's compare-and-set possible at all:
      // the click must know the source as it was WHEN THE CARD WAS DRAWN, because re-reading it live would
      // compare the live source against itself and always agree, defeating the check whose entire purpose
      // is to notice that the user typed into the component while the card was up (#775).
      plan.BaseSource = seed.BaseSource;
      plan.Outcome = Enum.TryParse<ScriptEditOutcome>(seed.Outcome, out var o) ? o : ScriptEditOutcome.CouldNotCheck;
      plan.Report = seed.Report;
      plan.TrialNote = seed.TrialNote;

      plan.Intent = (seed.Why ?? "").Trim();
      plan.Note = System.Net.WebUtility.HtmlEncode(plan.Intent);
      return plan;
    }
  }
}
